using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Phipes.Assistant.TokenBroker.Utilities;

// Lee/escribe XMLs en formato PSCredential (Export-Clixml de PowerShell).
// El password se cifra con DPAPI scope CurrentUser: solo el SID que cifró puede
// descifrar, en la misma máquina. El broker corre como svc-token-broker, por
// eso ningún otro usuario (incluyendo svc-webhook-handler) puede leer el RT.
[SupportedOSPlatform("windows")]
public static class CredXmlIo
{
    public static string ReadRefreshToken(string xmlFilePath)
    {
        var doc = XDocument.Load(xmlFilePath);
        var ssElement = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "SS"
                && e.Attributes().Any(a => a.Name.LocalName == "N" && a.Value == "Password"))
            ?? throw new InvalidOperationException($"No <SS N=\"Password\"> en {xmlFilePath}");

        var encryptedBytes = HexToBytes(ssElement.Value.Trim());
        var decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.Unicode.GetString(decryptedBytes);
    }

    // Lee el UserName del PSCredential (en claro). Se preserva al reescribir el
    // archivo tras una rotación de RT, evitando hardcodear la identidad.
    public static string ReadUserName(string xmlFilePath)
    {
        var doc = XDocument.Load(xmlFilePath);
        var sElement = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "S"
                && e.Attributes().Any(a => a.Name.LocalName == "N" && a.Value == "UserName"))
            ?? throw new InvalidOperationException($"No <S N=\"UserName\"> en {xmlFilePath}");

        return sElement.Value.Trim();
    }

    // Reescribe atómicamente: write a .tmp, fsync, rename. Si crash a mitad,
    // el .tmp queda huerfano pero el original no se corrompe.
    public static void WriteRefreshToken(string xmlFilePath, string userName, string newRefreshToken)
    {
        var encryptedBytes = ProtectedData.Protect(
            Encoding.Unicode.GetBytes(newRefreshToken), null, DataProtectionScope.CurrentUser);
        var hex = BytesToHex(encryptedBytes);

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

        var tmp = xmlFilePath + ".tmp";
        File.WriteAllText(tmp, xml, new UTF8Encoding(false));
        File.Move(tmp, xmlFilePath, overwrite: true);
    }

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static string BytesToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
