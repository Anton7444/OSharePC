using System.Security.Cryptography;

namespace CatShareSender;

/// <summary>
/// Crypto matching the decompiled lb/a0.java (EncryptOrDecryptUtil):
///  - ECDH P-256, public keys as Base64(X509/SPKI), private as Base64(PKCS#8)
///  - shared secret = raw agreement ("TlsPremasterSecret"), zero-padded to 32 bytes
///  - alliance payloads: AES-256-CTR, fixed IV "0102030405060708", Base64 output
///  - iOS/OPLUS-Connect payloads: AES-128-CBC PKCS7, key = Base64(secret)[..16], same IV
/// </summary>
public sealed class OShareCrypto : IDisposable
{
    public const string FixedIv = "0102030405060708";

    private readonly ECDiffieHellman _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Base64 X509/SPKI public key — sent in the "key" JSON field.</summary>
    public string PublicKeyB64 { get; }

    public OShareCrypto()
    {
        PublicKeyB64 = Convert.ToBase64String(_ecdh.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Raw ECDH shared secret (X coordinate, big-endian, zero-padded to 32 bytes)
    /// against the peer's Base64 X509 public key.</summary>
    public byte[] DeriveSharedSecret(string peerPublicKeyB64)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peerPublicKeyB64), out _);
        var raw = _ecdh.DeriveRawSecretAgreement(peer.PublicKey);
        return PadTo32(raw);
    }

    private static byte[] PadTo32(byte[] x)
    {
        if (x.Length == 32) return x;
        var buf = new byte[32];
        Array.Copy(x, 0, buf, 32 - Math.Min(x.Length, 32), Math.Min(x.Length, 32));
        return buf;
    }

    /// <summary>AES-CTR (Java "AES/CTR/NoPadding") with the fixed IV; plaintext in, Base64 out.</summary>
    public static string CtrEncryptToB64(byte[] key, string plaintext) =>
        Convert.ToBase64String(CtrTransform(key, System.Text.Encoding.UTF8.GetBytes(plaintext)));

    /// <summary>AES-CTR decrypt; Base64 in, UTF-8 out.</summary>
    public static string CtrDecryptFromB64(byte[] key, string b64) =>
        System.Text.Encoding.UTF8.GetString(CtrTransform(key, Convert.FromBase64String(b64)));

    /// <summary>Java-compatible CTR: 16-byte big-endian counter block, keystream XOR.</summary>
    public static byte[] CtrTransform(byte[] key, byte[] input)
    {
        using var aes = Aes.Create();
        aes.Key = NormalizeKey(key);
        aes.Mode = CipherMode.ECB;      // used only to build the keystream
        aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor();

        var counter = (byte[])System.Text.Encoding.ASCII.GetBytes(FixedIv).Clone();
        var outBuf = new byte[input.Length];
        var ks = new byte[16];

        int off = 0;
        while (off < input.Length)
        {
            if (enc.TransformBlock(counter, 0, 16, ks, 0) != 16)
                throw new InvalidOperationException("keystream block failed");
            int n = Math.Min(16, input.Length - off);
            for (int i = 0; i < n; i++) outBuf[off + i] = (byte)(input[off + i] ^ ks[i]);
            off += n;
            IncrementCounter(counter);
        }
        return outBuf;
    }

    private static void IncrementCounter(byte[] ctr)
    {
        for (int i = ctr.Length - 1; i >= 0; i--)
        {
            if (++ctr[i] != 0) break;
        }
    }

    /// <summary>Java SecretKeySpec semantics: truncate/zero-pad the key to 16/24/32 bytes.</summary>
    private static byte[] NormalizeKey(byte[] key) => key.Length switch
    {
        16 or 24 or 32 => key,
        > 32 => key[..32],
        > 24 => Pad(key, 32),
        > 16 => Pad(key, 24),
        _ => Pad(key, 16)
    };

    private static byte[] Pad(byte[] src, int len)
    {
        var b = new byte[len];
        Array.Copy(src, b, src.Length);
        return b;
    }

    /// <summary>iOS/OPLUS-Connect variant: AES-128-CBC PKCS7, key = Base64(secret) first 16 chars.</summary>
    public static (byte[] Key, byte[] Iv) CbcKeyFromSecret(byte[] secret)
    {
        var b64 = Convert.ToBase64String(secret);
        var keyStr = b64.Length < 16 ? b64.PadRight(16, '0') : b64[..16];
        return (System.Text.Encoding.ASCII.GetBytes(keyStr), System.Text.Encoding.ASCII.GetBytes(FixedIv));
    }

    public static string CbcEncryptToB64(byte[] key, byte[] iv, string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor();
        var bytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        return Convert.ToBase64String(enc.TransformFinalBlock(bytes, 0, bytes.Length));
    }

    public static string CbcDecryptFromB64(byte[] key, byte[] iv, string b64)
    {
        using var aes = Aes.Create();
        aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        using var dec = aes.CreateDecryptor();
        var plain = dec.TransformFinalBlock(Convert.FromBase64String(b64), 0, Convert.FromBase64String(b64).Length);
        return System.Text.Encoding.UTF8.GetString(plain);
    }

    public void Dispose() => _ecdh.Dispose();
}
