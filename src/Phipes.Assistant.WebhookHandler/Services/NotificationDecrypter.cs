using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;
using Phipes.Assistant.WebhookHandler.Models;

namespace Phipes.Assistant.WebhookHandler.Services;

// Implementa el descifrado de notifications "encrypted resource data" de Microsoft Graph.
// Algoritmo (ver https://learn.microsoft.com/graph/webhooks-with-resource-data#decrypting-resource-data):
//   1. RSA-OAEP-SHA1 decrypt(dataKey) con private key -> symmetricKey (32 bytes, AES-256).
//   2. Verificar HMAC-SHA256(data, symmetricKey) == dataSignature.
//   3. AES-256-CBC decrypt(data) con symmetricKey como key e IV = primeros 16 bytes de symmetricKey.
[SupportedOSPlatform("windows")]
public sealed class NotificationDecrypter : INotificationDecrypter, IDisposable
{
    private readonly X509Certificate2 _certificate;
    private readonly EncryptionOptions _options;
    private readonly ILogger<NotificationDecrypter> _logger;

    public NotificationDecrypter(IOptions<EncryptionOptions> options, ILogger<NotificationDecrypter> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (!File.Exists(_options.PfxPath))
        {
            throw new FileNotFoundException($"PFX no encontrado en {_options.PfxPath}");
        }

        _certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _options.PfxPath, _options.PfxPassword,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);

        if (!_certificate.HasPrivateKey)
        {
            throw new InvalidOperationException("El PFX no contiene private key");
        }

        _logger.LogInformation("NotificationDecrypter listo. Cert thumbprint={Thumb} certificateId={Id}",
            _certificate.Thumbprint, _options.CertificateId);
    }

    public string Decrypt(EncryptedContent encryptedContent)
    {
        if (!string.IsNullOrEmpty(encryptedContent.EncryptionCertificateThumbprint)
            && !string.Equals(encryptedContent.EncryptionCertificateThumbprint,
                              _certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Thumbprint del cert no coincide. Esperado {_certificate.Thumbprint}, " +
                $"recibido {encryptedContent.EncryptionCertificateThumbprint}");
        }

        var dataKeyBytes      = Convert.FromBase64String(encryptedContent.DataKey);
        var dataBytes         = Convert.FromBase64String(encryptedContent.Data);
        var dataSignatureBytes = Convert.FromBase64String(encryptedContent.DataSignature);

        // 1. RSA-OAEP-SHA1 unwrap de la AES key.
        byte[] symmetricKey;
        using (var rsa = _certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Cert sin RSA private key"))
        {
            symmetricKey = rsa.Decrypt(dataKeyBytes, RSAEncryptionPadding.OaepSHA1);
        }

        try
        {
            // 2. Verificar HMAC-SHA256 sobre el ciphertext.
            using (var hmac = new HMACSHA256(symmetricKey))
            {
                var expected = hmac.ComputeHash(dataBytes);
                if (!CryptographicOperations.FixedTimeEquals(expected, dataSignatureBytes))
                {
                    throw new CryptographicException("HMAC-SHA256 no coincide; payload corrupto o no autenticado");
                }
            }

            // 3. AES-256-CBC decrypt.
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = symmetricKey;
            // El IV son los primeros 16 bytes de la symmetric key (spec Microsoft).
            var iv = new byte[16];
            Array.Copy(symmetricKey, 0, iv, 0, 16);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            var plain = decryptor.TransformFinalBlock(dataBytes, 0, dataBytes.Length);
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(symmetricKey);
        }
    }

    public void Dispose() => _certificate.Dispose();
}
