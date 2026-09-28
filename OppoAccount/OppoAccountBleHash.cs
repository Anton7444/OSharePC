using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>
    /// The newer, actually-verified "same account" primitive — reverse engineered from
    /// three decompiled system components (in order of delegation): the OPPO/OnePlus
    /// Share app's PantaConnect SDK (com.oplus.pantaconnect.account.a#b →
    /// com.oplus.pantaconnect.connection.processor's DeviceConnectionServerImpl#options,
    /// which does a plain Arrays.equals on this value with no per-peer key at all),
    /// the com.heytap.accessory system service it delegates to, and finally
    /// com.oplus.ndsf (e8/d.java#getAccountHash, backed by p8/a.java#t) which actually
    /// computes it from the real logged-in ssoid ("userid").
    ///
    /// Algorithm (p8.a#t, a NIST-SP-800-56A-style hash-based KDF, truncated):
    ///   digest = ""
    ///   for i in 0..ceil(length/32) inclusive:
    ///     digest = SHA256(digest || ascii(f"0x{i:02X}") || ssoid_utf8)
    ///     output += digest
    ///   return output[0:length]
    /// For length &lt;= 32 (the only case that matters here) this reduces to just the
    /// first iteration: SHA256("0x00" + ssoid)[0:length].
    ///
    /// Confirmed byte-for-byte against a real captured accountId ("39154E") broadcast
    /// by a real device on the account this was tested with — see
    /// OPPO_ACCOUNT_API_FINDINGS.md section 5h. This is what the real
    /// {"method":"iBeacon_advertise",...,"account_id":"..."} JSON's account_id field
    /// contains, and (separately) what DeviceConnectionServerImpl.options() compares
    /// via plain byte equality during an actual GATT/RTC connection to decide
    /// AccountState.SAME_ACCOUNT — no peer-specific key, no encryption, unlike the
    /// older o.java scheme above.
    /// </summary>
    public static byte[] ComputeDsfAccountHash(string ssoid, int length = 3)
    {
        if (length <= 0 || length > 32) throw new ArgumentOutOfRangeException(nameof(length));
        var ssoidBytes = Encoding.UTF8.GetBytes(ssoid);
        var digest = Array.Empty<byte>();
        var output = new List<byte>();
        var iterations = (int)Math.Ceiling(length / 32.0);
        for (var i = 0; i <= iterations; i++)
        {
            using var sha = SHA256.Create();
            sha.TransformBlock(digest, 0, digest.Length, null, 0);
            var marker = Encoding.UTF8.GetBytes($"0x{i:X2}");
            sha.TransformBlock(marker, 0, marker.Length, null, 0);
            sha.TransformFinalBlock(ssoidBytes, 0, ssoidBytes.Length);
            digest = sha.Hash!;
            output.AddRange(digest);
        }
        return output.Take(length).ToArray();
    }

    /// <summary>Uppercase hex of <see cref="ComputeDsfAccountHash"/> — the exact string
    /// format used in the real "account_id" JSON field (e.g. "39154E").</summary>
    public static string ComputeDsfAccountIdHex(string ssoid, int length = 3) =>
        Convert.ToHexString(ComputeDsfAccountHash(ssoid, length));
}
