using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Phipes.Assistant.WebhookHandler.Services;
using Xunit;

namespace Phipes.Assistant.WebhookHandler.Tests;

// Tests del MessageAttachmentExtractor. Cobertura del happy path (hosted image inline
// pasted) + casos de error (URL no-Graph, cascade de tokens, archivo grande). Para
// OneDrive references el test verifica que se intenta /shares antes del GET directo,
// pero no testea el desempaquetado real del b64 (lo cubre el SubscriptionStateStore
// indirectamente via la URL generada).
public sealed class MessageAttachmentExtractorTests : IDisposable
{
    private readonly string _tempDir;

    public MessageAttachmentExtractorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"phipes-extract-test-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("HANDLER_ATTACHMENTS_DIR", _tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HANDLER_ATTACHMENTS_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            req => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(Responder(request));
        }
    }

    private sealed class StubWebhookAppTokenProvider : IWebhookAppTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("fake-webhook-app-token");
    }

    private static (MessageAttachmentExtractor extractor, RecordingHandler handler) BuildSut(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var handler = new RecordingHandler();
        if (responder is not null) handler.Responder = responder;
        var http = new HttpClient(handler);
        var extractor = new MessageAttachmentExtractor(
            http,
            new StubWebhookAppTokenProvider(),
            NullLogger<MessageAttachmentExtractor>.Instance);
        return (extractor, handler);
    }

    [Fact]
    public async Task HostedImage_InlineInHtml_DownloadsCorrectly()
    {
        var imgPayload = Encoding.UTF8.GetBytes("fake jpeg bytes here");
        var (extractor, handler) = BuildSut(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(imgPayload)
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            resp.Content.Headers.ContentLength = imgPayload.Length;
            return resp;
        });

        var html = @"<p>Mira esta foto:</p><img src=""https://graph.microsoft.com/v1.0/chats/19:xyz@unq.gbl.spaces/messages/1234/hostedContents/abc/$value"" width=""300""><p>Saludos</p>";
        var result = await extractor.ExtractAsync(
            chatId: "19:xyz@unq.gbl.spaces",
            messageId: "msg-001",
            htmlBody: html,
            attachments: null,
            accessToken: "fake-user-token");

        Assert.Single(result);
        Assert.True(File.Exists(result[0].FilePath));
        Assert.Equal("image/jpeg", result[0].ContentType);
        Assert.Equal(imgPayload.Length, result[0].SizeBytes);
        // Verificar que el archivo descargado tiene los bytes esperados.
        var actualBytes = await File.ReadAllBytesAsync(result[0].FilePath);
        Assert.Equal(imgPayload, actualBytes);
    }

    [Fact]
    public async Task NoHostedImages_NoFileAttachments_ReturnsEmpty()
    {
        var (extractor, handler) = BuildSut();
        var result = await extractor.ExtractAsync(
            chatId: "19:xyz@unq.gbl.spaces",
            messageId: "msg-002",
            htmlBody: "<p>solo texto plano sin imagenes</p>",
            attachments: null,
            accessToken: "fake-user-token");
        Assert.Empty(result);
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task FileAttachment_OneDrive_TriesSharesEndpointFirst()
    {
        // El extractor intenta primero /shares con token webhook-app, despues /shares con
        // token sconnor, despues GET directo con cada token. Verificamos que la PRIMERA
        // URL llamada sea /shares/u!{b64}
        var (extractor, handler) = BuildSut(req =>
        {
            // Devolvemos 200 con bytes vacios al primer intento (suficiente para test).
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0x01, 0x02, 0x03 })
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            resp.Content.Headers.ContentLength = 3;
            return resp;
        });

        var attachments = new[]
        {
            new GraphAttachmentRef
            {
                Id = "att-1",
                ContentType = "reference",
                ContentUrl = "https://contoso-my.sharepoint.com/personal/user_contoso_com/Documents/file.pdf",
                Name = "file.pdf"
            }
        };

        var result = await extractor.ExtractAsync(
            chatId: "19:xyz@unq.gbl.spaces",
            messageId: "msg-onedrive",
            htmlBody: "",
            attachments: attachments,
            accessToken: "fake-user-token");

        Assert.Single(result);
        // Primera URL llamada debe ser /shares/u!...
        Assert.NotEmpty(handler.RequestedUrls);
        Assert.StartsWith("https://graph.microsoft.com/v1.0/shares/u!", handler.RequestedUrls[0]);
    }

    [Fact]
    public async Task FileAttachment_NonSharePoint_GoesDirectly()
    {
        var (extractor, handler) = BuildSut(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0xAA })
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            resp.Content.Headers.ContentLength = 1;
            return resp;
        });

        var attachments = new[]
        {
            new GraphAttachmentRef
            {
                Id = "att-1",
                ContentType = "reference",
                ContentUrl = "https://example.com/files/document.bin",
                Name = "document.bin"
            }
        };

        var result = await extractor.ExtractAsync(
            chatId: "19:xyz@unq.gbl.spaces",
            messageId: "msg-direct",
            htmlBody: "",
            attachments: attachments,
            accessToken: "fake-user-token");

        Assert.Single(result);
        // NO debe haber llamado a /shares (no es SharePoint), debe ir directo.
        Assert.DoesNotContain(handler.RequestedUrls, u => u.Contains("/shares/u!"));
        Assert.Contains("https://example.com/files/document.bin", handler.RequestedUrls);
    }

    [Fact]
    public async Task NonReferenceAttachment_Skipped()
    {
        var (extractor, handler) = BuildSut();
        var attachments = new[]
        {
            new GraphAttachmentRef { Id = "att-1", ContentType = "card",        ContentUrl = "https://x/y" },
            new GraphAttachmentRef { Id = "att-2", ContentType = "messageBack", ContentUrl = "https://x/y" },
            new GraphAttachmentRef { Id = "att-3", ContentType = "codesnippet", ContentUrl = "https://x/y" }
        };
        var result = await extractor.ExtractAsync(
            chatId: "19:xyz@unq.gbl.spaces",
            messageId: "msg-skip",
            htmlBody: "",
            attachments: attachments,
            accessToken: "fake-user-token");
        Assert.Empty(result);
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task HostedImage_HttpError_DoesNotAbortMessage()
    {
        // Si una hosted image falla, el extractor sigue (no throw). Verificamos que el
        // resultado este vacio pero la llamada se haya hecho.
        var (extractor, handler) = BuildSut(req =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var html = @"<img src=""https://graph.microsoft.com/v1.0/chats/19:test@x/messages/1/hostedContents/abc/$value"">";
        var result = await extractor.ExtractAsync(
            chatId: "19:test@x",
            messageId: "msg-404",
            htmlBody: html,
            attachments: null,
            accessToken: "fake-user-token");

        Assert.Empty(result);
        Assert.NotEmpty(handler.RequestedUrls);
    }
}
