using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OShareSender.OppoAccount;

public sealed record QrCodeInfo(string Qid, string QrcodeUrl);

public sealed record QrCodeStatus(string Status, string? AccountName, string? AvatarUrl);

public sealed record AuthnCheckResult(string ProcessToken, string? CountryCode);

public sealed record AuthnValidateResult(
    bool Registered,
    string? AccountStatus,
    string? AccountName,
    string? VerificationId,
    string? VerificationUrl);

public sealed record VerificationMethodsResult(
    string ProcessToken,
    string? VerificationId,
    int CurrentRound,
    int TotalRound,
    IReadOnlyList<string> VerMethodList,
    bool TokenExpired);

/// <summary>
/// Client for OPPO/HeyTap's account-center QR-login API, reverse-engineered from the
/// real "OPPO Share" iOS app (com.heytap.oshare). Lets OSharePC drive the same
/// same-account QR login flow the phone app itself uses, independently of any device.
///
/// Confirmed working end-to-end against the live production server as of this
/// writing: GenerateQrCodeAsync, PollQrCodeAsync, AuthnCheckAsync, AuthnValidateAsync,
/// VerificationListAsync. Everything past "list the available 2FA methods" (submitting
/// an OTP code, and the final native-side session/ssoid exchange) is still being
/// reverse-engineered — see docs/oppo-account-login-notes.md.
///
/// This uses OPPO's private, undocumented API and an app-signing secret extracted
/// from their shipped client. Likely against their ToS; could change or break without
/// notice on any app update. Only use with your own account.
/// </summary>
public sealed class OppoAccountClient : IDisposable
{
    private const string QrHost = "uc-client-sg.heytapmobile.com";
    private const string AuthHost = "uc-client-cn.heytapmobi.com";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly Dictionary<string, string> BaseHeaders = new()
    {
        ["x-device-brand"] = "heytap",
        ["x-device-clienttype"] = "IOSSDK",
        ["x-context-timezone"] = "Australia/Melbourne",
        ["x-biz-version"] = "149",
        ["x-sys-talkbackstate"] = "false",
        ["x-context-country"] = "AU",
        ["x-app-deviceid"] = "",
        ["x-device-hardwaretype"] = "Mobile",
        ["x-sdk-type"] = "open",
        ["x-app-overseaclient"] = "true",
        ["x-context-maskregion"] = "AU",
        ["referer"] = "https://muc.heytap.com/",
        ["x-sys-duid"] = "",
        ["x-sdk-version"] = "206",
        ["x-sys-osversioncode"] = "15.7.2",
        ["x-envelope-version"] = "V1",
        ["origin"] = "https://muc.heytap.com",
        ["x-context-locale"] = "en_AU",
        ["accept-language"] = "en-AU",
        ["x-app-hostpackage"] = "com.heytap.oshare",
        ["x-app-hostversion"] = "149",
        ["x-biz-package"] = "com.heytap.oshare",
        ["x-device-model"] = "iPod9,1",
        ["x-app-acpackage"] = "com.oppo.OPLoginRegisterKitOnePlus",
        ["x-app-acversion"] = "206",
        ["x-biz-appkey"] = "5407efeca7f6453c80bea5f961cfb7a3",
        ["x-biz-appid"] = "33561077",
        ["x-sign-key"] = OppoAccountSigning.Secret1,
        ["x-app-acappkey"] = "fa2b41b450744068b7962048802d05f4",
        ["x-sign-algorithm"] = "HMAC1_SK",
    };

    private const string UserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 15_7_2 like Mac OS X) AppleWebKit/605.1.15 " +
        "(KHTML, like Gecko) Mobile/15E148 regionCode/AU isPanel/0 isThird/1 deviceType/IOS " +
        "Business/account hardwareType/Mobile isMagicWindow/0 DayNight/0 language/en-AU " +
        "languageTag/en-AU locale/en_AU timeZone/Australia/Melbourne model/iPod9,1 " +
        "appPackageName/com.heytap.oshare appVersion/1.4.6 AcLegacyLanguageTag/en-AU AcLanguageTag/en-AU";

    /// <summary>Generates a brand-new login QR code. No phone or app needed on this side.</summary>
    public async Task<QrCodeInfo> GenerateQrCodeAsync(CancellationToken ct = default)
    {
        var result = await CallAsync(QrHost, "/identity/v1/authn/scan-code/generate-qrcode",
            new Dictionary<string, object?> { ["appId"] = "usercenter-sdk" }, traceIdPrefix: "LG_", ct: ct);

        if (result.RootElement.GetProperty("code").GetInt32() != 200)
            throw new InvalidOperationException($"generate-qrcode failed: {result.RootElement}");

        var data = result.RootElement.GetProperty("data");
        return new QrCodeInfo(data.GetProperty("qid").GetString()!, data.GetProperty("qrcodeUrl").GetString()!);
    }

    /// <summary>Downloads the QR code image bytes for display (e.g. in a WinForms PictureBox).</summary>
    public Task<byte[]> DownloadQrCodeImageAsync(string qrcodeUrl, CancellationToken ct = default) =>
        _http.GetByteArrayAsync(qrcodeUrl, ct);

    /// <summary>One status poll. Caller decides polling cadence/timeout (e.g. every 2-3s).</summary>
    public async Task<QrCodeStatus> CheckQrCodeAsync(string qid, CancellationToken ct = default)
    {
        var result = await CallAsync(QrHost, "/identity/v1/authn/scan-code/check-qrcode",
            new Dictionary<string, object?> { ["qid"] = qid }, traceIdPrefix: "LG_", ct: ct);

        var data = result.RootElement.GetProperty("data");
        return new QrCodeStatus(
            data.GetProperty("qrCodeStatus").GetString() ?? "UNKNOWN",
            data.TryGetProperty("accountName", out var n) ? n.GetString() : null,
            data.TryGetProperty("avatarUrl", out var a) ? a.GetString() : null);
    }

    /// <summary>
    /// Step 1 of the post-CONFIRMED verification chain: exchanges the confirmed qid for a
    /// processToken. NOTE: the returned processToken is single-use — call
    /// AuthnValidateAsync with it immediately, before the phone's own app (if it also
    /// reached CONFIRMED) has a chance to consume it first.
    /// </summary>
    public async Task<AuthnCheckResult> AuthnCheckAsync(string qid, CancellationToken ct = default)
    {
        var result = await CallAsync(AuthHost, "/identity/v1/authn/check",
            new Dictionary<string, object?> { ["qid"] = qid }, traceIdPrefix: "WEB_", ct: ct);

        var data = result.RootElement.GetProperty("data");
        return new AuthnCheckResult(
            data.GetProperty("processToken").GetString()!,
            data.TryGetProperty("countryCode", out var c) ? c.GetString() : null);
    }

    /// <summary>Step 2: validates the (single-use) processToken from AuthnCheckAsync.</summary>
    public async Task<AuthnValidateResult> AuthnValidateAsync(string processToken, CancellationToken ct = default)
    {
        var result = await CallAsync(AuthHost, "/identity/v1/authn/validate",
            new Dictionary<string, object?>
            {
                ["validateParam"] = new Dictionary<string, object?>(),
                ["processToken"] = processToken,
            },
            traceIdPrefix: "WEB_",
            extraHeaders: new Dictionary<string, string> { ["x-validation-method"] = "scan" },
            ct: ct);

        var data = result.RootElement.GetProperty("data");
        return new AuthnValidateResult(
            data.TryGetProperty("registered", out var reg) && reg.GetBoolean(),
            data.TryGetProperty("accountStatus", out var st) ? st.GetString() : null,
            data.TryGetProperty("accountName", out var an) ? an.GetString() : null,
            data.TryGetProperty("verificationId", out var vi) ? vi.GetString() : null,
            data.TryGetProperty("verificationUrl", out var vu) ? vu.GetString() : null);
    }

    /// <summary>Step 3: lists the available 2FA methods (PASSWORD/SMS/EMAIL/INBOUND_SMS) for this processToken.</summary>
    public async Task<VerificationMethodsResult> VerificationListAsync(
        string processToken, string envInfoJson, CancellationToken ct = default)
    {
        var result = await CallAsync(AuthHost, "/api/verification/list",
            new Dictionary<string, object?>
            {
                ["envInfo"] = envInfoJson,
                ["processToken"] = processToken,
                ["captchaCode"] = "",
                ["deviceToken"] = new Dictionary<string, object?>(),
            },
            traceIdPrefix: "WEB_", ct: ct);

        var data = result.RootElement.GetProperty("data");
        var methods = new List<string>();
        if (data.TryGetProperty("verMethodList", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var m in list.EnumerateArray())
                if (m.GetString() is { } s) methods.Add(s);

        return new VerificationMethodsResult(
            data.GetProperty("processToken").GetString()!,
            data.TryGetProperty("verificationId", out var vid) ? vid.GetString() : null,
            data.TryGetProperty("currentRound", out var cr) ? cr.GetInt32() : 0,
            data.TryGetProperty("totalRound", out var tr) ? tr.GetInt32() : 0,
            methods,
            data.TryGetProperty("tokenExpired", out var te) && te.GetBoolean());
    }

    private async Task<JsonDocument> CallAsync(
        string host,
        string path,
        Dictionary<string, object?> payload,
        string traceIdPrefix,
        Dictionary<string, string>? extraHeaders = null,
        CancellationToken ct = default)
    {
        var envelope = new OppoAccountEnvelope();
        var plaintextJson = JsonSerializer.Serialize(payload);
        var encryptedBody = envelope.EncryptBody(plaintextJson);
        var requestTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}{path}")
        {
            Content = new StringContent(encryptedBody, Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/json, text/plain, */*");

        foreach (var (k, v) in BaseHeaders) request.Headers.TryAddWithoutValidation(k, v);
        if (extraHeaders != null)
            foreach (var (k, v) in extraHeaders) request.Headers.TryAddWithoutValidation(k, v);

        request.Headers.TryAddWithoutValidation("x-requesttime", requestTime);
        request.Headers.TryAddWithoutValidation("x-app-traceid", traceIdPrefix + Guid.NewGuid().ToString("N")[..24]);
        request.Headers.TryAddWithoutValidation("x-sign", OppoAccountSigning.BuildXSign(plaintextJson, requestTime));

        using var response = await _http.SendAsync(request, ct);
        var bodyText = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var decrypted = envelope.DecryptBody(bodyText);
        return JsonDocument.Parse(decrypted);
    }

    public void Dispose() => _http.Dispose();
}
