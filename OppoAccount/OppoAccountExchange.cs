using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OShareSender.OppoAccount;

/// <summary>
/// Real desktop-app config for the server-side "exchange code for session" step,
/// extracted from OPPO's own official Windows app ("O+Connect", installed at
/// Program Files\Oppo Connect\resources\app.asar, function `login`/`exchangeCode` in
/// background.js). This is the step that turns the OAuth-style `code` the login widget
/// redirects back with (see OppoWebLoginForm.cs) into an actual account session.
/// </summary>
public sealed record OppoExchangeBrandConfig(string Label, string BizAppKey, string AccessSecret, string AccountApiHost, string PathPrefix);

public static class OppoAccountExchange
{
    // Constant client identifier the real app sends — not a secret, just an API-key-ish
    // label; observed value is "pc-assistant-client" verbatim in the real app's code.
    private const string AccessKey = "pc-assistant-client";
    // The real app sends its own build version here; the server doesn't appear to
    // validate this strictly (the earlier "invalid parameter" failures were all on the
    // mobile SDK's endpoints, not this one), so an arbitrary version string is used.
    private const string AppVersion = "1.0.0";

    public static readonly OppoExchangeBrandConfig Oppo = new(
        "Heytap", "8Bn4FenJAasH23ziynsS2u",
        "929er8595lvg3aP8612xqem16yo1npfj71169746xze943u0lkyf6d521b7d7ou6f",
        "pcassistant-account-gl.oppo.com", "/heytap");

    public static readonly OppoExchangeBrandConfig OneplusOversea = new(
        "OnePlus", "PpGBVhay64GC2iBajrsDfU",
        "929er8595lvg3aP8612xqem16yo1npfj71169746xze943u0lkyf6d521b7d7ou6f",
        "pcassistant-opacc-gl.oppo.com", "/oneplus");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>
    /// Exchanges a login widget's one-time `code` for a real account session. Call this
    /// immediately after capturing the code (see OShareBridgeServer.RunOppoWebLoginAsync)
    /// — these codes are short-lived, and (per the real app's own logic) may only
    /// finalize once the redirect that carried them is actually delivered, so speed
    /// matters more here than almost anywhere else in this flow.
    /// </summary>
    public static async Task<JsonDocument> ExchangeCodeAsync(
        OppoExchangeBrandConfig cfg, string code, string? countryCode, CancellationToken ct = default)
    {
        var (host, pathPrefix) = await ResolveRegionAsync(cfg, countryCode, ct);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        // Signing scheme (SHA-256, not the mobile SDK's HMAC-SHA1 — a completely
        // separate scheme for this completely separate, PC-specific API surface):
        // sha256(lowercase("appKey=<key>&code=<code>&get&/v2/account/exchange&<ts>&<secret>"))
        var joined = $"appKey={cfg.BizAppKey}&code={code}&get&/v2/account/exchange&{ts}&{cfg.AccessSecret}";
        var sign = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(joined.ToLowerInvariant())));
        var qs = $"code={Uri.EscapeDataString(code)}&appKey={Uri.EscapeDataString(cfg.BizAppKey)}";
        var url = $"https://{host}{pathPrefix}/api/v2/account/exchange?{qs}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("version", AppVersion);
        req.Headers.TryAddWithoutValidation("accessKey", AccessKey);
        req.Headers.TryAddWithoutValidation("timestamp", ts);
        req.Headers.TryAddWithoutValidation("sign", sign);

        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"exchange HTTP {(int)resp.StatusCode}: {text}");
        return JsonDocument.Parse(text);
    }

    /// <summary>Mirrors the real app's region resolution: three countries map to a fixed
    /// host outright; anything else asks a region-info endpoint; failing that, it falls
    /// back to the brand's default global host with no path prefix (matching what the
    /// real app does when no countryCode was available at all).</summary>
    private static async Task<(string Host, string PathPrefix)> ResolveRegionAsync(
        OppoExchangeBrandConfig cfg, string? countryCode, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(countryCode)) return (cfg.AccountApiHost, "");

        var host = countryCode switch
        {
            "CN" => "pcassistant-account-cn.oppo.com",
            "IN" => "pc-assistant-in.allawnos.com",
            "US" => "pc-assistant-us.allawnos.com",
            _ => null,
        };

        if (host is null)
        {
            try
            {
                var body = JsonSerializer.Serialize(new { accountBrand = cfg.Label, accountRegisterLocation = countryCode });
                using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{cfg.AccountApiHost}/api/v2/common/region-info")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                using var resp = await Http.SendAsync(req, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number &&
                    c.GetInt32() == 200 &&
                    doc.RootElement.TryGetProperty("data", out var d) &&
                    d.TryGetProperty("accountServerRegion", out var region) &&
                    region.ValueKind == JsonValueKind.String)
                {
                    host = region.GetString();
                }
            }
            catch
            {
                // Fall through to the default host below — matches the real app's own
                // behavior when region resolution fails outright.
            }
        }

        return host is null ? (cfg.AccountApiHost, "") : (host, cfg.PathPrefix);
    }
}
