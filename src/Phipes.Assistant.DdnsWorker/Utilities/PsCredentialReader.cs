using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Phipes.Assistant.DdnsWorker.Utilities;

/// <summary>
/// Lee archivos PSCredential XML producidos por <c>Export-Clixml</c> en PowerShell.
/// El password queda cifrado DPAPI con scope CurrentUser; solo el mismo usuario Windows
/// que lo guardó puede descifrarlo.
/// </summary>
public static class PsCredentialReader
{
    /// <summary>
    /// Lee el password (string) de un PSCredential XML.
    /// </summary>
    public static string ReadPassword(string xmlFilePath)
    {
        if (!File.Exists(xmlFilePath))
            throw new FileNotFoundException($"PSCredential XML no existe: {xmlFilePath}");

        var doc = XDocument.Load(xmlFilePath);

        // <SS N="Password">{hex}</SS> dentro de <Props>
        var ssElement = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "SS"
                && e.Attributes().Any(a => a.Name.LocalName == "N" && a.Value == "Password"))
            ?? throw new InvalidDataException(
                $"No se encontró <SS N='Password'> en {xmlFilePath}");

        var encryptedHex = ssElement.Value.Trim();
        if (string.IsNullOrEmpty(encryptedHex))
            throw new InvalidDataException("Password vacío en PSCredential XML");

        var encryptedBytes = HexToBytes(encryptedHex);

        // DPAPI Unprotect (CurrentUser scope, igual que Export-Clixml)
        var decryptedBytes = ProtectedData.Unprotect(
            encryptedBytes, null, DataProtectionScope.CurrentUser);

        // El blob descifrado es UTF-16 LE (formato SecureString interno de Windows)
        return Encoding.Unicode.GetString(decryptedBytes);
    }

    /// <summary>
    /// Lee el username del PSCredential XML (no cifrado).
    /// </summary>
    public static string ReadUserName(string xmlFilePath)
    {
        var doc = XDocument.Load(xmlFilePath);
        var sElement = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "S"
                && e.Attributes().Any(a => a.Name.LocalName == "N" && a.Value == "UserName"))
            ?? throw new InvalidDataException(
                $"No se encontró <S N='UserName'> en {xmlFilePath}");
        return sElement.Value;
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex.Length % 2 != 0)
            throw new FormatException("Hex string longitud impar");

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
