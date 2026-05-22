using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Phipes.Assistant.WebhookHandler.Utilities;

namespace Phipes.Assistant.WebhookHandler.Services;

// Extrae adjuntos de un mensaje de Microsoft Teams y los descarga al filesystem local
// para que Claude Code los pueda leer via la tool Read.
//
// Dos tipos de adjuntos cubiertos:
//   1. Hosted images: imagenes pegadas/arrastradas al chat. Aparecen como
//      <img src="https://graph.microsoft.com/v1.0/chats/{chatId}/messages/{msgId}/
//      hostedContents/{id}/$value"> dentro del HTML del body. Se descargan con bearer
//      token sobre el mismo endpoint - el Content-Type del response nos dice la
//      extension correcta.
//   2. File attachments (OneDrive/SharePoint references): aparecen en
//      message.attachments[] con contentType="reference" y un contentUrl que apunta
//      a un share link. Se descarga el binary directo via GET (requiere Files.Read).
//
// Limites defensivos: max 10 adjuntos por mensaje, max 25 MB por archivo. Si un adjunto
// excede, se logguea y se omite sin abortar el mensaje entero.
public interface IMessageAttachmentExtractor
{
    // accessToken: token delegated del usuario (sconnor) - usado para hosted images
    //              (que viven en el endpoint de chats).
    // Internamente para file references (OneDrive/SharePoint) el extractor pide su
    // propio token a IWebhookAppTokenProvider porque ese tiene Chat.ReadWrite.All
    // admin-consented y puede leer attachments donde sconnor delegated no llega.
    Task<IReadOnlyList<ExtractedAttachment>> ExtractAsync(
        string chatId,
        string messageId,
        string htmlBody,
        IReadOnlyList<GraphAttachmentRef>? attachments,
        string accessToken,
        CancellationToken cancellationToken = default);
}

public sealed record ExtractedAttachment(string FilePath, string OriginalName, string ContentType, long SizeBytes);

public sealed class GraphAttachmentRef
{
    [JsonPropertyName("id")]          public string? Id { get; set; }
    [JsonPropertyName("contentType")] public string? ContentType { get; set; }
    [JsonPropertyName("contentUrl")]  public string? ContentUrl { get; set; }
    [JsonPropertyName("name")]        public string? Name { get; set; }
}

public sealed class MessageAttachmentExtractor : IMessageAttachmentExtractor
{
    private readonly HttpClient _http;
    private readonly IWebhookAppTokenProvider _webhookAppTokens;
    private readonly ILogger<MessageAttachmentExtractor> _logger;

    private const int MaxAttachmentsPerMessage = 10;
    private const long MaxBytesPerAttachment = 25L * 1024 * 1024;
    private static readonly Regex HostedImageRegex = new(
        // Captura el src de cualquier <img> que apunte a un hostedContent de Graph (con o sin
        // $value URL-encoded). Tolera ' o " como delimitador y espacios alrededor del =.
        @"<img[^>]*?\bsrc\s*=\s*['""]?(?<url>https?://[^'""\s>]*?/hostedContents/[^'""\s>]+?/(?:\$value|%24value))['""]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public MessageAttachmentExtractor(
        HttpClient http,
        IWebhookAppTokenProvider webhookAppTokens,
        ILogger<MessageAttachmentExtractor> logger)
    {
        _http = http;
        _webhookAppTokens = webhookAppTokens;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ExtractedAttachment>> ExtractAsync(
        string chatId,
        string messageId,
        string htmlBody,
        IReadOnlyList<GraphAttachmentRef>? attachments,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ExtractedAttachment>();
        var baseDir = Path.Combine(AttachmentTempRoot(), SanitizeFilename(messageId));
        Directory.CreateDirectory(baseDir);

        // 1. Hosted images del HTML body
        if (!string.IsNullOrWhiteSpace(htmlBody))
        {
            var matches = HostedImageRegex.Matches(htmlBody);
            int idx = 0;
            foreach (Match m in matches)
            {
                if (results.Count >= MaxAttachmentsPerMessage) break;
                var url = System.Net.WebUtility.HtmlDecode(m.Groups["url"].Value);
                try
                {
                    var dl = await DownloadAsync(url, accessToken, baseDir, $"hosted-{idx++}", cancellationToken);
                    if (dl is not null) results.Add(dl);
                }
                catch (Exception ex)
                {
                    FileLog($"hosted image fail (url={Truncate(url, 120)}): {ex.Message}");
                }
            }
        }

        // 2. File attachments (OneDrive/SharePoint references). Usamos el token de la
        // Webhook app (Chat.ReadWrite.All admin-consented) y NO el del usuario delegated
        // - sconnor no tiene Files.Read.All sobre drives de terceros, asi que el endpoint
        // /shares con su token devuelve 403 cuando Teams no genero un sharing link
        // explicito al adjuntar.
        string? webhookAppToken = null;
        if (attachments is not null && attachments.Count > 0)
        {
            try
            {
                webhookAppToken = await _webhookAppTokens.GetAccessTokenAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                FileLog($"webhook app token fail: {ex.Message} - fallback al token de sconnor");
            }
        }

        if (attachments is not null)
        {
            int idx = 0;
            foreach (var att in attachments)
            {
                if (results.Count >= MaxAttachmentsPerMessage) break;
                if (string.IsNullOrEmpty(att.ContentUrl)) continue;
                if (!string.Equals(att.ContentType, "reference", StringComparison.OrdinalIgnoreCase))
                {
                    // Solo descargamos references explicitos. Cards / messageBacks /
                    // codesnippet / etc. quedan fuera por ahora.
                    FileLog($"attachment skip: contentType={att.ContentType} name={att.Name}");
                    continue;
                }
                try
                {
                    var filenameSeed = att.Name ?? $"file-{idx}";
                    var sharesUrl = TryBuildSharesEndpoint(att.ContentUrl);
                    var tokensToTry = new List<(string label, string token)>();
                    if (webhookAppToken is not null) tokensToTry.Add(("webhook-app", webhookAppToken));
                    tokensToTry.Add(("sconnor-delegated", accessToken));

                    ExtractedAttachment? dl = null;
                    foreach (var (label, tok) in tokensToTry)
                    {
                        // Intentar primero /shares/ y luego GET directo, con cada token.
                        var attempts = new List<string>();
                        if (sharesUrl is not null) attempts.Add(sharesUrl);
                        attempts.Add(att.ContentUrl);
                        foreach (var url in attempts)
                        {
                            try
                            {
                                dl = await DownloadAsync(url, tok, baseDir,
                                    $"ref-{idx}-{SanitizeFilename(filenameSeed)}", cancellationToken,
                                    preferredName: att.Name);
                                if (dl is not null)
                                {
                                    FileLog($"download OK with token={label} via {(url == sharesUrl ? "/shares" : "directUrl")}");
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                FileLog($"download fail with token={label}: {ex.Message}");
                            }
                        }
                        if (dl is not null) break;
                    }

                    if (dl is not null) results.Add(dl);
                    idx++;
                }
                catch (Exception ex)
                {
                    FileLog($"reference attachment fail (name={att.Name}): {ex.Message}");
                }
            }
        }

        if (results.Count > 0)
        {
            FileLog($"extracted {results.Count} attachment(s) to {baseDir}: " +
                    string.Join(", ", results.Select(r => $"{r.OriginalName} ({r.SizeBytes} bytes)")));
        }
        else
        {
            // No descargamos nada - borrar el dir vacio
            try { Directory.Delete(baseDir, recursive: false); } catch { }
        }

        return results;
    }

    private async Task<ExtractedAttachment?> DownloadAsync(
        string url,
        string accessToken,
        string baseDir,
        string filenameSeed,
        CancellationToken cancellationToken,
        string? preferredName = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            FileLog($"download fail: HTTP {(int)resp.StatusCode} for {Truncate(url, 100)}");
            return null;
        }

        var contentLength = resp.Content.Headers.ContentLength ?? 0;
        if (contentLength > MaxBytesPerAttachment)
        {
            FileLog($"download skip (too large): {contentLength} bytes > {MaxBytesPerAttachment}");
            return null;
        }

        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var finalName = preferredName ?? $"{filenameSeed}{GuessExtension(contentType)}";
        finalName = SanitizeFilename(finalName);
        var finalPath = Path.Combine(baseDir, finalName);

        await using (var fileStream = File.Create(finalPath))
        await using (var netStream = await resp.Content.ReadAsStreamAsync(cancellationToken))
        {
            // Copia con tope de tamano. Si excede mientras descarga, abortar.
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await netStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                total += read;
                if (total > MaxBytesPerAttachment)
                {
                    FileLog($"download abort: exceeded {MaxBytesPerAttachment} bytes streaming");
                    fileStream.Close();
                    File.Delete(finalPath);
                    return null;
                }
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return new ExtractedAttachment(finalPath, finalName, contentType, total);
        }
    }

    // Convierte una URL de sharing (OneDrive/SharePoint) en el endpoint /shares/u!{b64url}/driveItem/content
    // de Microsoft Graph. Si la URL no es de SharePoint/OneDrive devuelve null para que el caller
    // haga GET directo. Spec: https://learn.microsoft.com/graph/api/shares-get
    private static string? TryBuildSharesEndpoint(string contentUrl)
    {
        if (string.IsNullOrEmpty(contentUrl)) return null;
        if (!Uri.TryCreate(contentUrl, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        // OneDrive personal corp, SharePoint, OneDrive consumer.
        var isOneDriveOrSharePoint =
            host.EndsWith("-my.sharepoint.com") || host.EndsWith(".sharepoint.com") ||
            host.EndsWith("onedrive.live.com") || host.EndsWith("1drv.ms");
        if (!isOneDriveOrSharePoint) return null;

        var bytes = System.Text.Encoding.UTF8.GetBytes(contentUrl);
        var b64 = Convert.ToBase64String(bytes)
                         .TrimEnd('=')
                         .Replace('/', '_')
                         .Replace('+', '-');
        return $"https://graph.microsoft.com/v1.0/shares/u!{b64}/driveItem/content";
    }

    private static string GuessExtension(string contentType) => contentType switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png"                 => ".png",
        "image/gif"                 => ".gif",
        "image/webp"                => ".webp",
        "image/bmp"                 => ".bmp",
        "image/svg+xml"             => ".svg",
        "application/pdf"           => ".pdf",
        "text/plain"                => ".txt",
        "text/csv"                  => ".csv",
        "application/json"          => ".json",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"       => ".xlsx",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
        "application/msword"        => ".doc",
        "application/vnd.ms-excel"  => ".xls",
        "application/zip"           => ".zip",
        _ => ""
    };

    private static string SanitizeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        // Tope de longitud razonable
        return sanitized.Length > 100 ? sanitized.Substring(0, 100) : sanitized;
    }

    private static string AttachmentTempRoot()
    {
        // Por defecto vivimos en C:\inetpub\pacificdev.assistant\attachments\ via env var.
        return Environment.GetEnvironmentVariable("HANDLER_ATTACHMENTS_DIR")
               ?? Path.Combine(Path.GetTempPath(), "webhook-handler", "attachments");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";
    private static void FileLog(string message) => FileLogger.Write("Attach", message);
}
