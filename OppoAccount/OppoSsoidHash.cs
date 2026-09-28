using System.Security.Cryptography;
using System.Text;

namespace OShareSender.OppoAccount;

/// <summary>
/// com.oplus.oshare.utils.AccountManger#S — the value OPPO/OnePlus Share's own
/// AccountManger.y() returns for the logged-in account, and what the phone compares
/// an incoming BLE "same account" proof against (see GattLink.OConnectLanSendAsync's
/// account-challenge response, and OPPO_ACCOUNT_API_FINDINGS.md section 5f/5g).
/// </summary>
public static class OppoSsoidHash
{
    /// <summary>SHA-256 hex digest of <paramref name="ssoid"/>, or the value itself if
    /// it's already 64+ chars (some ssoid representations are already a full hash and
    /// must not be re-hashed).</summary>
    public static string Hash(string ssoid)
    {
        if (string.IsNullOrEmpty(ssoid)) return "";
        if (ssoid.Length >= 64) return ssoid;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ssoid));
        return Convert.ToHexStringLower(bytes);
    }
}
