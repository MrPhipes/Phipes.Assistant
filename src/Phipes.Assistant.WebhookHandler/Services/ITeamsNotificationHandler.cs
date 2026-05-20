using Phipes.Assistant.WebhookHandler.Models;

namespace Phipes.Assistant.WebhookHandler.Services;

// Procesa una notification de Microsoft Graph relacionada con Teams chats.
public interface ITeamsNotificationHandler
{
    Task HandleAsync(ChangeNotification notification, CancellationToken cancellationToken = default);
}
