using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Phipes.Assistant.WebhookHandler.Configuration;
using Phipes.Assistant.WebhookHandler.Models;
using Phipes.Assistant.WebhookHandler.Services;
using Xunit;

namespace Phipes.Assistant.WebhookHandler.Tests;

// Tests del NotificationDecrypter: genera un cert RSA-2048 self-signed, encripta un
// payload siguiendo el spec de Microsoft Graph (AES-256-CBC + HMAC-SHA256 + RSA-OAEP-SHA1)
// y verifica que el decrypter lo desempaca correctamente.
public sealed class NotificationDecrypterTests : IDisposable
{
    private readonly string _pfxPath;
    private readonly string _pfxPassword = "test-password-123";
    private readonly X509Certificate2 _testCert;

    public NotificationDecrypterTests()
    {
        // Setup: generar cert RSA-2048 self-signed en memoria, exportar a PFX temp.
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=PhipesAssistantTest",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        _testCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));

        _pfxPath = Path.Combine(Path.GetTempPath(), $"phipes-test-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(_pfxPath, _testCert.Export(X509ContentType.Pfx, _pfxPassword));
    }

    public void Dispose()
    {
        _testCert.Dispose();
        try { File.Delete(_pfxPath); } catch { }
    }

    private NotificationDecrypter CreateDecrypter() => new(
        Options.Create(new EncryptionOptions
        {
            PfxPath = _pfxPath,
            PfxPassword = _pfxPassword,
            CertificateId = "test-cert-id"
        }),
        NullLogger<NotificationDecrypter>.Instance);

    // Construye un EncryptedContent simulando lo que Microsoft Graph entrega: una symKey
    // random, encripta payload con AES-CBC, calcula HMAC, y envuelve la symKey con RSA-OAEP-SHA1.
    private EncryptedContent BuildEncryptedContent(string plaintext, X509Certificate2? thumbprintCert = null)
    {
        var symKey = RandomNumberGenerator.GetBytes(32);

        byte[] cipher;
        using (var aes = Aes.Create())
        {
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = symKey;
            var iv = new byte[16];
            Array.Copy(symKey, 0, iv, 0, 16);
            aes.IV = iv;
            using var enc = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(plaintext);
            cipher = enc.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        }

        byte[] mac;
        using (var hmac = new HMACSHA256(symKey))
        {
            mac = hmac.ComputeHash(cipher);
        }

        byte[] wrappedKey;
        using (var rsa = (thumbprintCert ?? _testCert).GetRSAPublicKey()!)
        {
            wrappedKey = rsa.Encrypt(symKey, RSAEncryptionPadding.OaepSHA1);
        }

        return new EncryptedContent
        {
            Data = Convert.ToBase64String(cipher),
            DataKey = Convert.ToBase64String(wrappedKey),
            DataSignature = Convert.ToBase64String(mac),
            EncryptionCertificateId = "test-cert-id",
            EncryptionCertificateThumbprint = (thumbprintCert ?? _testCert).Thumbprint
        };
    }

    [Fact]
    public void Decrypt_RoundtripsPlainText()
    {
        using var decrypter = CreateDecrypter();
        const string original = "Hola Felipe, mensaje desde Hugo. Tildes: áéíóú ñ €";
        var encrypted = BuildEncryptedContent(original);

        var result = decrypter.Decrypt(encrypted);

        Assert.Equal(original, result);
    }

    [Fact]
    public void Decrypt_DetectsHmacTampering()
    {
        using var decrypter = CreateDecrypter();
        var encrypted = BuildEncryptedContent("test");

        // Modificar un byte del ciphertext - HMAC debe fallar antes de intentar descifrar
        var dataBytes = Convert.FromBase64String(encrypted.Data);
        dataBytes[0] ^= 0xFF;
        encrypted = new EncryptedContent
        {
            Data = Convert.ToBase64String(dataBytes),
            DataKey = encrypted.DataKey,
            DataSignature = encrypted.DataSignature,
            EncryptionCertificateId = encrypted.EncryptionCertificateId,
            EncryptionCertificateThumbprint = encrypted.EncryptionCertificateThumbprint
        };

        Assert.Throws<CryptographicException>(() => decrypter.Decrypt(encrypted));
    }

    [Fact]
    public void Decrypt_RejectsWrongThumbprint()
    {
        using var decrypter = CreateDecrypter();
        var encrypted = BuildEncryptedContent("test");
        // Reemplazar thumbprint por uno distinto
        encrypted = new EncryptedContent
        {
            Data = encrypted.Data,
            DataKey = encrypted.DataKey,
            DataSignature = encrypted.DataSignature,
            EncryptionCertificateId = encrypted.EncryptionCertificateId,
            EncryptionCertificateThumbprint = new string('A', 40)
        };

        Assert.Throws<InvalidOperationException>(() => decrypter.Decrypt(encrypted));
    }

    [Fact]
    public void Decrypt_RoundtripsLargePayload()
    {
        using var decrypter = CreateDecrypter();
        // ~10KB de payload realista (un mensaje de Teams puede traer markup HTML grueso)
        var original = string.Concat(Enumerable.Repeat("Mensaje extenso con tildes áéíóú. ", 300));
        var encrypted = BuildEncryptedContent(original);

        var result = decrypter.Decrypt(encrypted);

        Assert.Equal(original, result);
    }

    [Fact]
    public void Constructor_FailsWhenPfxMissing()
    {
        var opts = Options.Create(new EncryptionOptions
        {
            PfxPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.pfx"),
            PfxPassword = "x",
            CertificateId = "x"
        });
        Assert.Throws<FileNotFoundException>(() => new NotificationDecrypter(opts, NullLogger<NotificationDecrypter>.Instance));
    }
}
