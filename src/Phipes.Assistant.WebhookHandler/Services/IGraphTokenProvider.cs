namespace Phipes.Assistant.WebhookHandler.Services;

// Provee access tokens validos de Microsoft Graph para llamar como la asistente.
// Maneja el refresh interno (rotación del refresh token incluida).
public interface IGraphTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
