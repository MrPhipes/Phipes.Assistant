using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Phipes.Assistant.WebhookHandler.Utilities;

// Lee y escribe XMLs en formato PSCredential (Export-Clixml / Import-Clixml de PowerShell).
// El campo Password está cifrado con DPAPI scope CurrentUser: solo el usuario Windows
// que lo cifró puede descifrarlo, en la misma máquina.
[SupportedOSPlatform("windows")]
public static class PsCredentialReader
{
    // Devuelve el password en claro del XML serializado por Export-Clixml.
    public static string ReadPassword(string xmlFilePath)
    {
        var doc = XDocument.Load(xmlFilePath);
        // En el formato CLIXML el password vive en <SS N="Password">HEX_BLOB</SS>
        var ssElement = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "SS"
                && e.Attributes().Any(a => a.Name.LocalName == "N" && a.Value == "Password"))
            ?? throw new InvalidOperationException($"No se encontró el elemento <SS N=\"Password\"> en {xmlFilePath}");

        var encryptedBytes = HexToBytes(ssElement.Value.Trim());
        var decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.Unicode.GetString(decryptedBytes);
    }

    // Reescribe el XML con un nuevo password (cifrado bajo el usuario actual).
    // Útil para rotar el refresh token cuando Microsoft devuelve uno nuevo.
    public static void WritePassword(string xmlFilePath, string userName, string newPasswordPlain)
    {
        var encryptedBytes = ProtectedData.Protect(
            Encoding.Unicode.GetBytes(newPasswordPlain), null, DataProtectionScope.CurrentUser);
        var hex = BytesToHex(encryptedBytes);

        // Generamos el XML con el mismo shape que Export-Clixml produce para PSCredential.
        var xml = $"""
            <Objs Version="1.1.0.1" xmlns="http://schemas.microsoft.com/powershell/2004/04">
              <Obj RefId="0">
                <TN RefId="0">
                  <T>System.Management.Automation.PSCredential</T>
                  <T>System.Object</T>
                </TN>
                <ToString>System.Management.Automation.PSCredential</ToString>
                <Props>
                  <S N="UserName">{System.Security.SecurityElement.Escape(userName)}</S>
                  <SS N="Password">{hex}</SS>
                </Props>
              </Obj>
            </Objs>
            """;
        File.WriteAllText(xmlFilePath, xml, new UTF8Encoding(false));
    }

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    private static string BytesToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
