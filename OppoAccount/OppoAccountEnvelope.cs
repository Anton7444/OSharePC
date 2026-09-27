using System.Security.Cryptography;
using System.Text;

namespace OShareSender.OppoAccount;

/// <summary>
/// Envelope encryption for OPPO/HeyTap's account-center API
/// (uc-client-*.heytapmobi{le}.com), reverse-engineered from the real iOS app's
/// "account-web" JS bundle (wrapper-8d4d9d2b.js).
///
/// One instance per HTTP request: the client generates the AES key/IV itself, so
/// the server's response — encrypted with that same key/IV — can be decrypted
/// locally without ever needing a private key.
///
/// Scheme:
///   1. aesKey, aesIv = two random 16-char strings from charset A-Za-z1-9 (no '0')
///   2. RSA-OAEP/SHA-1 encrypt each with OPPO's public key (below)
///   3. AES-128-CTR (NoPadding) encrypt the compact JSON body; the 16-byte aesIv
///      IS the initial big-endian CTR counter value (not a nonce+counter split)
///   4. body = base64(RSA(aesKey)) + "." + base64(RSA(aesIv)) + "." + base64(AES(json))
/// </summary>
public sealed class OppoAccountEnvelope
{
    // Public key embedded in OPPO's own JS bundle — safe to reuse, meant to be public.
    private const string RsaPublicKeyB64 =
        "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDpgSW5VkZ6/xvh+wMXezrOokNdiupuvuMj4RVJ" +
        "y44byWDupl4H37z907A26RVdFzMeyLUQB4rsDIaXdxCODlljWW+/K96uF5MsDtOFUBw7VlOclIjcYTv/" +
        "YDQEul8JoXoOuy1Yf3b5sbTpTuVTcl97tAuLJ8PoGe2K7N3B1eUQqQIDAQAB";

    private const string AesKeyCharset = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz123456789"; // no '0'

    public string AesKey { get; }
    public string AesIv { get; }

    public OppoAccountEnvelope()
    {
        AesKey = RandomKey();
        AesIv = RandomKey();
    }

    private static string RandomKey(int length = 16)
    {
        Span<byte> buf = stackalloc byte[length];
        RandomNumberGenerator.Fill(buf);
        var sb = new StringBuilder(length);
        foreach (var b in buf) sb.Append(AesKeyCharset[b % AesKeyCharset.Length]);
        return sb.ToString();
    }

    /// <summary>Encrypts a compact-JSON plaintext body into the dotted envelope string.</summary>
    public string EncryptBody(string plaintextJson)
    {
        var ciphertext = AesCtrTransform(Encoding.UTF8.GetBytes(plaintextJson), AesKey, AesIv);
        var encKey = RsaEncryptB64(AesKey);
        var encIv = RsaEncryptB64(AesIv);
        return $"{encKey}.{encIv}.{Convert.ToBase64String(ciphertext)}";
    }

    /// <summary>Decrypts a base64 ciphertext (server response body) using this envelope's own key/IV.</summary>
    public string DecryptBody(string base64Ciphertext)
    {
        var raw = Convert.FromBase64String(base64Ciphertext);
        var plain = AesCtrTransform(raw, AesKey, AesIv);
        return Encoding.UTF8.GetString(plain);
    }

    private static string RsaEncryptB64(string plaintext)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(RsaPublicKeyB64), out _);
        var encrypted = rsa.Encrypt(Encoding.UTF8.GetBytes(plaintext), RSAEncryptionPadding.OaepSHA1);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>AES-CTR where the 16-byte IV is used directly as the initial counter block
    /// (matches the captured JS's `initial_value = int.from_bytes(iv, "big")` behavior).</summary>
    private static byte[] AesCtrTransform(byte[] input, string key, string iv)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor();

        var counter = (byte[])Encoding.UTF8.GetBytes(iv).Clone();
        var output = new byte[input.Length];
        var keystream = new byte[16];

        int offset = 0;
        while (offset < input.Length)
        {
            enc.TransformBlock(counter, 0, 16, keystream, 0);
            int n = Math.Min(16, input.Length - offset);
            for (int i = 0; i < n; i++) output[offset + i] = (byte)(input[offset + i] ^ keystream[i]);
            offset += n;
            IncrementCounter(counter);
        }
        return output;
    }

    private static void IncrementCounter(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0) break;
        }
    }
}
