using System.Security.Cryptography;
using System.Text;

namespace StormsBackupSystem.Services;

/// <summary>
/// Encrypts secrets (GitHub token, database password, webhook URL) with Windows DPAPI so they are
/// never stored in plain text, and can only be decrypted by the same Windows user account.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Storms Technologies Backup System");

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return string.Empty;
        }

        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return string.Empty;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Core.Log.Warn("A saved secret could not be decrypted (was it saved by another Windows user?). Please re-enter it.");
            return string.Empty;
        }
    }
}
