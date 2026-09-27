using System.Security.Cryptography;
using System.Text;

namespace OShareSender.OppoAccount;

/// <summary>
/// Request signing (X-Sign / HMAC1_SK) for OPPO/HeyTap's account-center API.
/// Reverse-engineered from the real iOS app's account-web JS bundle
/// (wrapper-8d4d9d2b.js, function `gE` — the axios "before-sign" interceptor).
///
/// Secrets below are OPPO's own production app-signing constants extracted from
/// their shipped client. Treat as credential material: do not distribute publicly.
/// </summary>
public static class OppoAccountSigning
{
    // "signAppBiz" (g6 in the JS) — appended into the joined string AND sent verbatim
    // as the X-Sign-Key header.
    public const string Secret1 = "4iAEb620alQ8s8c8ss8o0K8sS";

    // "signAppSecret" (_6 in the JS) — the actual HMAC-SHA1 key. Never sent over the wire.
    private const string Secret2 = "80620774fE983aab1E8C564e3f213b62";

    /// <summary>
    /// Builds the X-Sign header value. <paramref name="plaintextJson"/> MUST be the
    /// compact JSON of the plaintext request payload (i.e. JSON.stringify(payload)
    /// BEFORE envelope encryption) — signing and encryption are independent
    /// interceptors in the original JS, and this is the detail that cost the most
    /// reverse-engineering time to discover.
    /// </summary>
    public static string BuildXSign(string plaintextJson, string requestTimeMs)
    {
        var md5Hex = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(plaintextJson)));

        // TreeMap alphabetical order: requestBody, requestTime, signAlgorithm
        var joined = $"requestBody={md5Hex}&requestTime={requestTimeMs}&signAlgorithm=HMAC1_SK" + Secret1;

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(Secret2));
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(joined));
        return Convert.ToBase64String(digest);
    }
}
