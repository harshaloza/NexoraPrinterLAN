using System.Security.Cryptography;
using System.Text;

namespace Nexora.Shared.Security;

/// <summary>
/// Protects secrets at rest with Windows DPAPI so they are never stored in plain text.
/// CurrentUser scope ties the blob to the Windows account that runs the app.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Nexora Printer LAN secret v1");

    public static string Protect(string plaintext, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, scope));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string? TryUnprotect(string? protectedBase64, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, scope);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
