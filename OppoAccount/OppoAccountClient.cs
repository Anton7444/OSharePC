using System.Globalization;
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

public sealed record VerificationMethod(string VerMethod, string? Display, int Order);

public sealed record VerificationMethodsResult(
    string ProcessToken,
    string? VerificationId,
    int CurrentRound,
    int TotalRound,
    IReadOnlyList<VerificationMethod> VerMethodList,
    bool TokenExpired);

public sealed record VerificationGatherResult(string VerMethod, int? CodeLength);

public sealed record VerificationValidationResult(
    string? Ticket,
    bool NeedNextRound,
    string? RuleId);

/// <summary>
/// Client for OPPO/HeyTap's account-center QR-login API, reverse-engineered from the
/// real "OPPO Share" iOS app (com.heytap.oshare). Lets OSharePC drive the same
/// same-account QR login flow the phone app itself uses, independently of any device.
///
/// The captured web flow is: generate/poll the QR, exchange and validate its
/// processToken, open the second verification round, list methods, and gather/validate
/// the selected challenge. The final native-side session/ssoid exchange is still not
/// implemented — see docs/oppo-account-login-notes.md.
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

    // Derived from the machine's own OS settings instead of a hardcoded "AU" so the
    // account-center calls describe whoever is actually running this, not the device
    // the login flow was originally captured from.
    private static readonly string LocalIanaTimeZone = ResolveIanaTimeZone();
    private static readonly string LocalCountryCode = ResolveCountryCode();
    private static readonly string LocalCultureTag = CultureInfo.CurrentCulture.Name; // e.g. "en-AU"
    private static readonly string LocalLocaleUnderscore = LocalCultureTag.Replace('-', '_'); // e.g. "en_AU"

    private static string ResolveIanaTimeZone()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId) return local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var ianaId)
            ? ianaId
            : "Etc/UTC"; // last-resort fallback if the ICU conversion data isn't available
    }

    private static string ResolveCountryCode()
    {
        try
        {
            return RegionInfo.CurrentRegion.TwoLetterISORegionName.ToUpperInvariant();
        }
        catch (ArgumentException)
        {
            // RegionInfo throws if the current culture is a neutral/language-only culture
            // with no associated region (e.g. "en" instead of "en-AU").
            return "US";
        }
    }

    // (device identifier, iOS version it's paired with) -- real devices running a real,
    // plausible iOS version for that hardware, so the model/OS-version combo never looks
    // impossible. Safari's "Mobile/15E148" build tag is left alone: Apple has reused that
    // same token across every iOS release since iOS 11, so it doesn't need to vary with
    // the chosen version.
    private static readonly (string Model, string IosVersion)[] DeviceProfiles =
    [
        ("iPhone9,1", "15.7.9"),    // iPhone 7
        ("iPhone9,3", "15.8"),      // iPhone 7
        ("iPhone10,3", "16.7.2"),   // iPhone X
        ("iPhone10,6", "16.7.8"),   // iPhone X
        ("iPhone11,2", "17.5.1"),   // iPhone XS
        ("iPhone11,6", "17.6"),     // iPhone XS Max
        ("iPhone11,8", "17.6.1"),   // iPhone XR
        ("iPhone12,1", "17.4.1"),   // iPhone 11
        ("iPhone12,3", "17.3"),     // iPhone 11 Pro
        ("iPhone12,5", "17.1.2"),   // iPhone 11 Pro Max
        ("iPhone12,8", "17.5"),     // iPhone SE (2nd gen)
        ("iPhone13,2", "17.6.1"),   // iPhone 12
        ("iPhone13,4", "16.6"),     // iPhone 12 Pro Max
        ("iPhone14,5", "17.2"),     // iPhone 13
        ("iPhone14,7", "18.1"),     // iPhone 14
        ("iPhone14,8", "18.2"),     // iPhone 14 Plus
        ("iPhone15,2", "18.0"),     // iPhone 14 Pro
        ("iPhone15,3", "18.3"),     // iPhone 14 Pro Max
        ("iPhone15,4", "18.2.1"),   // iPhone 15
        ("iPhone15,5", "18.4"),     // iPhone 15 Plus
        ("iPhone16,1", "18.5"),     // iPhone 15 Pro
        ("iPhone16,2", "18.3.2"),   // iPhone 15 Pro Max
        ("iPhone17,3", "18.6"),     // iPhone 16
        ("iPhone17,4", "18.6.1"),   // iPhone 16 Plus
        ("iPhone17,1", "18.5"),     // iPhone 16 Pro
        ("iPhone17,2", "18.6"),     // iPhone 16 Pro Max
        ("iPhone17,5", "18.4.1"),   // iPhone 16e
        ("iPhone18,3", "19.0"),     // iPhone 17
        ("iPhone18,4", "19.0.1"),   // iPhone Air
        ("iPhone18,1", "19.1"),     // iPhone 17 Pro
        ("iPhone18,2", "19.0"),     // iPhone 17 Pro Max
        ("iPod9,1", "15.7.2"),      // iPod touch (7th gen) -- the original captured device
    ];

    private static readonly (string Model, string IosVersion) LocalDeviceProfile =
        DeviceProfiles[Random.Shared.Next(DeviceProfiles.Length)];

    private static string LocalDeviceModel => LocalDeviceProfile.Model;
    private static string LocalIosVersionDotted => LocalDeviceProfile.IosVersion;
    private static string LocalIosVersionUnderscore => LocalIosVersionDotted.Replace('.', '_');

    private static readonly Dictionary<string, string> BaseHeaders = new()
    {
        ["x-device-brand"] = "heytap",
        ["x-device-clienttype"] = "IOSSDK",
        ["x-context-timezone"] = LocalIanaTimeZone,
        ["x-biz-version"] = "149",
        ["x-sys-talkbackstate"] = "false",
        ["x-context-country"] = LocalCountryCode,
        ["x-app-deviceid"] = "",
        ["x-device-hardwaretype"] = "Mobile",
        ["x-sdk-type"] = "open",
        ["x-app-overseaclient"] = "true",
        ["x-context-maskregion"] = LocalCountryCode,
        ["referer"] = "https://muc.heytap.com/",
        ["x-sys-duid"] = "",
        ["x-sdk-version"] = "206",
        ["x-sys-osversioncode"] = LocalIosVersionDotted,
        ["x-envelope-version"] = "V1",
        ["origin"] = "https://muc.heytap.com",
        ["x-context-locale"] = LocalLocaleUnderscore,
        ["accept-language"] = LocalCultureTag,
        ["x-app-hostpackage"] = "com.heytap.oshare",
        ["x-app-hostversion"] = "149",
        ["x-biz-package"] = "com.heytap.oshare",
        ["x-device-model"] = LocalDeviceModel,
        ["x-app-acpackage"] = "com.oppo.OPLoginRegisterKitOnePlus",
        ["x-app-acversion"] = "206",
        ["x-biz-appkey"] = "5407efeca7f6453c80bea5f961cfb7a3",
        ["x-biz-appid"] = "33561077",
        ["x-sign-key"] = OppoAccountSigning.Secret1,
        ["x-app-acappkey"] = "fa2b41b450744068b7962048802d05f4",
        ["x-sign-algorithm"] = "HMAC1_SK",
    };

    private static readonly string UserAgent =
        $"Mozilla/5.0 (iPhone; CPU iPhone OS {LocalIosVersionUnderscore} like Mac OS X) AppleWebKit/605.1.15 " +
        $"(KHTML, like Gecko) Mobile/15E148 regionCode/{LocalCountryCode} isPanel/0 isThird/1 deviceType/IOS " +
        $"Business/account hardwareType/Mobile isMagicWindow/0 DayNight/0 language/{LocalCultureTag} " +
        $"languageTag/{LocalCultureTag} locale/{LocalLocaleUnderscore} timeZone/{LocalIanaTimeZone} model/{LocalDeviceModel} " +
        $"appPackageName/com.heytap.oshare appVersion/1.4.6 AcLegacyLanguageTag/{LocalCultureTag} AcLanguageTag/{LocalCultureTag}";

    /// <summary>Generates a brand-new login QR code. No phone or app needed on this side.</summary>
    public async Task<QrCodeInfo> GenerateQrCodeAsync(CancellationToken ct = default)
    {
        var result = await CallAsync(QrHost, "/identity/v1/authn/scan-code/generate-qrcode",
            new Dictionary<string, object?> { ["appId"] = "usercenter-sdk" }, traceIdPrefix: "LG_", ct: ct);

        EnsureCodeOk(result, "generate-qrcode");

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

        EnsureCodeOk(result, "check-qrcode");

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
        // The QR flow is served by the Singapore client endpoint. The web client
        // also marks this request as a scan validation; the CN endpoint or a
        // missing header is rejected as an incomplete parameter (code 100001).
        // Body is just {"qid": ...} — confirmed against the real captured request
        // (see docs/oppo-account-login-notes.md); a "deviceToken" field here is not
        // part of this call and does not fix a real 100001 (that error also shows up
        // for an unconfirmed qid, which is a different failure).
        var result = await CallAsync(QrHost, "/identity/v1/authn/check",
            new Dictionary<string, object?> { ["qid"] = qid },
            traceIdPrefix: "LG_",
            extraHeaders: new Dictionary<string, string> { ["x-validation-method"] = "scan" },
            ct: ct);

        EnsureCodeOk(result, "authn/check");

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
            traceIdPrefix: "LG_",
            extraHeaders: new Dictionary<string, string> { ["x-validation-method"] = "scan" },
            ct: ct);

        EnsureCodeOk(result, "authn/validate");

        var data = result.RootElement.GetProperty("data");
        return new AuthnValidateResult(
            data.TryGetProperty("registered", out var reg) && reg.ValueKind == JsonValueKind.True,
            data.TryGetProperty("accountStatus", out var st) ? st.GetString() : null,
            data.TryGetProperty("accountName", out var an) ? an.GetString() : null,
            data.TryGetProperty("verificationId", out var vi) ? vi.GetString() : null,
            data.TryGetProperty("verificationUrl", out var vu) ? vu.GetString() : null);
    }

    /// <summary>
    /// Step 3: lists the available 2FA methods (PASSWORD/SMS/EMAIL/INBOUND_SMS) for this
    /// processToken. Confirmed against the real captured request (see
    /// docs/oppo-account-login-notes.md): the environment JSON, captcha and device-token
    /// fields go directly on this call — there is no separate verification/check step
    /// before it.
    /// </summary>
    public async Task<VerificationMethodsResult> VerificationListAsync(
        string processToken,
        string envInfoJson,
        CancellationToken ct = default)
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

        EnsureCodeOk(result, "verification/list");

        var data = result.RootElement.GetProperty("data");
        var methods = new List<VerificationMethod>();
        if (data.TryGetProperty("verMethodList", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var m in list.EnumerateArray())
            {
                if (m.ValueKind == JsonValueKind.String)
                {
                    var method = m.GetString();
                    if (!string.IsNullOrWhiteSpace(method))
                        methods.Add(new VerificationMethod(method, null, methods.Count + 1));
                    continue;
                }

                if (m.ValueKind != JsonValueKind.Object ||
                    !m.TryGetProperty("verMethod", out var methodElement) ||
                    methodElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(methodElement.GetString()))
                    continue;

                var display = ReadVerificationDisplay(m);
                var order = m.TryGetProperty("order", out var orderElement) &&
                            orderElement.ValueKind == JsonValueKind.Number &&
                            orderElement.TryGetInt32(out var parsedOrder)
                    ? parsedOrder
                    : methods.Count + 1;
                methods.Add(new VerificationMethod(methodElement.GetString()!, display, order));
            }

        return new VerificationMethodsResult(
            data.GetProperty("processToken").GetString()!,
            data.TryGetProperty("verificationId", out var vid) ? vid.GetString() : null,
            data.TryGetProperty("currentRound", out var cr) && cr.TryGetInt32(out var currentRound)
                ? currentRound
                : 0,
            data.TryGetProperty("totalRound", out var tr) && tr.TryGetInt32(out var totalRound)
                ? totalRound
                : 0,
            methods,
            data.TryGetProperty("tokenExpired", out var te) && te.ValueKind == JsonValueKind.True);
    }

    /// <summary>Requests that OPPO send the selected email/SMS verification code.</summary>
    public async Task<VerificationGatherResult> GatherUserDataAsync(
        string processToken,
        string verMethod,
        string? captchaType = null,
        string? captchaCode = null,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["verMethod"] = verMethod,
            ["contactId"] = "",
            ["metaInfo"] = "",
            ["processToken"] = processToken,
        };
        if (!string.IsNullOrWhiteSpace(captchaType)) payload["captchaType"] = captchaType;
        if (!string.IsNullOrWhiteSpace(captchaCode)) payload["captchaCode"] = captchaCode;

        var result = await CallAsync(AuthHost, "/api/verification/gather-user-data",
            payload, traceIdPrefix: "WEB_", ct: ct);
        EnsureCodeOk(result, "verification/gather-user-data");

        var data = result.RootElement.GetProperty("data");
        int? codeLength = null;
        if (data.TryGetProperty("gatherData", out var gatherData) &&
            gatherData.ValueKind == JsonValueKind.Object &&
            gatherData.TryGetProperty("length", out var length) &&
            length.ValueKind == JsonValueKind.Number &&
            length.TryGetInt32(out var parsedLength))
        {
            codeLength = parsedLength;
        }

        return new VerificationGatherResult(verMethod, codeLength);
    }

    /// <summary>Submits the password or one-time code for the selected method.</summary>
    public async Task<VerificationValidationResult> ValidateUserDataAsync(
        string processToken,
        string verMethod,
        string validateData,
        CancellationToken ct = default)
    {
        var result = await CallAsync(AuthHost, "/api/verification/validate-data",
            new Dictionary<string, object?>
            {
                ["verMethod"] = verMethod,
                ["validateData"] = validateData,
                ["processToken"] = processToken,
            },
            traceIdPrefix: "WEB_", ct: ct);
        EnsureCodeOk(result, "verification/validate-data");

        var data = result.RootElement.GetProperty("data");
        return new VerificationValidationResult(
            data.TryGetProperty("ticket", out var ticket) ? ticket.GetString() : null,
            data.TryGetProperty("needNextRound", out var next) &&
                next.ValueKind == JsonValueKind.True,
            data.TryGetProperty("ruleId", out var rule) ? rule.GetString() : null);
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
        // The captured UA string isn't a valid RFC 7231 product-token list (it embeds
        // raw "key/value" pairs like "timeZone/Australia/Melbourne"), so the strongly
        // typed UserAgent.ParseAdd rejects it — send it unvalidated like the rest.
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

        foreach (var (k, v) in BaseHeaders) request.Headers.TryAddWithoutValidation(k, v);
        if (extraHeaders != null)
            foreach (var (k, v) in extraHeaders) request.Headers.TryAddWithoutValidation(k, v);

        request.Headers.TryAddWithoutValidation("x-requesttime", requestTime);
        request.Headers.TryAddWithoutValidation("x-app-traceid", traceIdPrefix + Guid.NewGuid().ToString("N")[..24]);
        request.Headers.TryAddWithoutValidation("x-sign", OppoAccountSigning.BuildXSign(plaintextJson, requestTime));

        using var response = await _http.SendAsync(request, ct);
        var bodyText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{path} HTTP {(int)response.StatusCode}: {bodyText}");

        var decrypted = envelope.DecryptBody(bodyText);
        return JsonDocument.Parse(decrypted);
    }

    /// <summary>Every endpoint here replies {"code":200,"data":{...}} on success. Any
    /// other shape (rate limiting, validation errors, etc. — all observed in practice)
    /// has no "data" key, so blindly reading it throws an unhelpful, generic
    /// KeyNotFoundException ("The given key was not present in the dictionary.") with
    /// no indication of what actually went wrong. Call this before reading "data".</summary>
    private static void EnsureCodeOk(JsonDocument result, string endpointLabel)
    {
        var code = result.RootElement.TryGetProperty("code", out var c) &&
                   c.TryGetInt32(out var parsedCode)
            ? parsedCode
            : (int?)null;
        if (code != 200)
            throw new InvalidOperationException($"{endpointLabel} failed: {result.RootElement}");
    }

    private static string? ReadVerificationDisplay(JsonElement method)
    {
        if (!method.TryGetProperty("showInfo", out var showInfo) ||
            showInfo.ValueKind != JsonValueKind.Object)
            return null;

        // Keep the masked contact details useful in the UI, but never surface the
        // inbound SMS random code that is also present in some showInfo objects.
        var values = new List<string>();
        foreach (var key in new[] { "accountName", "maskMobile", "inboundNumber", "countryCallingCode" })
        {
            if (showInfo.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                values.Add(value.GetString()!);
        }
        return values.Count == 0 ? null : string.Join(" · ", values.Distinct());
    }

    public void Dispose() => _http.Dispose();
}
