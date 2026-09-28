using System.Security.Cryptography;
using System.Text;

namespace OShareSender.OppoAccount;

/// <summary>
/// Reproduces OPPO/OnePlus Share's "same account" BLE proximity check, reverse
/// engineered from the decompiled Android app (com.oplus.oshare.utils.AccountManger.D
/// and com.oplus.oshare.utils.b0 / EncryptOrDecryptUtil.j/n).
///
/// Each device broadcasts a 3-character "accountId" alongside its own 6-byte deviceId
/// in its BLE advertisement. Any peer that shares the same OPPO account can
/// independently recompute the identical 3 characters — from its own ssoid and the
/// broadcaster's public deviceId — and, on a match, marks that peer as
/// AccountState.SAME_ACCOUNT (which OPPO's own apps use to skip the manual "accept"
/// tap). There is no secret exchanged over the air: the whole scheme is knowing the
/// real ssoid for the logged-in account, which both sides already have.
///
/// Algorithm (see OPPO_ACCOUNT_API_FINDINGS.md section 5f for the derivation):
///   1. ssoidHash = hex(SHA-256(ssoid))                      (or ssoid itself if already >= 64 chars)
///   2. prefix3   = ssoidHash[0..3]
///   3. aesKey    = utf8(deviceId), right-padded with '0' to 16 bytes (or truncated to 16)
///   4. iv        = utf8("0102030405060708")                 (OShareCrypto.FixedIv — same constant used elsewhere in this codebase)
///   5. cipherB64 = base64(AES-128-CTR-NoPadding(prefix3, aesKey, iv))
///   6. accountId = cipherB64[0..3]
/// </summary>
public static class OppoAccountBleHash
{
    /// <summary>SHA-256 hex digest of <paramref name="ssoid"/>, or the value itself if
    /// it's already 64+ chars (matches AccountManger.S — some ssoid representations are
    /// already a full hash and shouldn't be re-hashed).</summary>
    public static string HashSsoid(string ssoid)
    {
        if (string.IsNullOrEmpty(ssoid)) return "";
        if (ssoid.Length >= 64) return ssoid;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ssoid));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>The 3-char "accountId" a device holding <paramref name="ssoid"/> should
    /// broadcast when its own public BLE deviceId is <paramref name="deviceId"/> (an
    /// arbitrary stable string this device also advertises in the clear — its identity
    /// is what a peer uses as the AES key when checking, so it must be the same string
    /// on both ends). Returns "" if ssoid is empty, matching the Android original.</summary>
    public static string ComputeAccountId(string ssoid, string deviceId)
    {
        var ssoidHash = HashSsoid(ssoid);
        if (ssoidHash.Length == 0) return "";
        var prefix3 = ssoidHash[..3];
        var key = DeriveAesKey(deviceId);
        var cipherB64 = OShareCrypto.CtrEncryptToB64(key, prefix3);
        return cipherB64.Length >= 3 ? cipherB64[..3] : cipherB64;
    }

    /// <summary>com.oplus.oshare.utils.b0#n — right-pad with ASCII '0' to 16 bytes, or
    /// truncate to 16 if longer, then take the raw UTF-8 bytes as the AES-128 key.</summary>
    private static byte[] DeriveAesKey(string code)
    {
        if (code.Length < 16) code = code.PadRight(16, '0');
        else if (code.Length > 16) code = code[..16];
        return Encoding.UTF8.GetBytes(code);
    }
}
