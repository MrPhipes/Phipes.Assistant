using Phipes.Assistant.WebhookHandler.Models;

namespace Phipes.Assistant.WebhookHandler.Services;

// Descifra el contenido del mensaje real que viene en la notification de Graph.
public interface INotificationDecrypter
{
    // Devuelve el JSON plano (string) del resourceData descifrado.
    string Decrypt(EncryptedContent encryptedContent);
}
