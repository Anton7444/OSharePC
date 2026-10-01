using System.Security.Cryptography;
using System.Text;

namespace OShareSender;

/// <summary>Encrypts small secrets for storage with Windows DPAPI, bound to the current Windows user: the stored
/// value can only be decrypted by this user on this PC (not from a copied file, another account or a backup).
/// Malware running as this same user can still decrypt it; nothing short of a user-entered password prevents that.</summary>
internal static class SecretProtector
{
    // Ties the ciphertext to this app, so another DPAPI consumer of the same user cannot decrypt it by accident.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OSharePC.settings.v1");

    public static string Protect(string plaintext) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>Null when the value cannot be decrypted (other user/PC, or damaged).</summary>
    public static string? TryUnprotect(string protectedBase64)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Log.Warn($"Settings: a protected value could not be decrypted ({ex.GetType().Name}); it is ignored");
            return null;
        }
    }

    /// <summary>For display: all but the last four characters hidden, e.g. "******1234".</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        return secret.Length <= 4 ? new string('*', secret.Length) : new string('*', 6) + secret[^4..];
    }
}
