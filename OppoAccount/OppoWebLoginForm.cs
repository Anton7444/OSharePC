using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OShareSender.OppoAccount;

/// <summary>Result of a successful <see cref="OppoWebLoginForm"/> login: whatever the
/// official `account_web_sdk` widget's own `onSuccess(msg, countryCode)` callback handed
/// back. This is the SDK's own finished result — no further token exchange is needed
/// (see docs/oppo-account-login-notes.md for why the old hand-rolled REST client never
/// got this far).</summary>
public sealed record OppoWebLoginResult(string Msg, string? CountryCode);

/// <summary>
/// One named brand entry the official `account_web_sdk` widget supports, along with the
/// real desktop `bizAppKey`/`callbackUrl` pair extracted from OPPO's own official
/// Windows app ("O+Connect", installed at Program Files\Oppo Connect). Using a real,
/// already-registered desktop app key — instead of impersonating the mobile app's own
/// internal client type — is what lets the SDK complete login/2FA without hitting the
/// anti-abuse wall the old hand-rolled REST client ran into.
/// </summary>
public sealed record OppoBrand(string Name, int SdkBrandIndex, string SdkScriptUrl, string BizAppKey, string CallbackUrl)
{
    // supportBrand enum order in the SDK: [oneplus_oversea, oppo, realme, oneplus, heytap].
    // O+Connect's own code only special-cases index 0 (oneplus_oversea) — every other
    // brand shares the same bizAppKey/callbackUrl/SDK script host as "oppo" (index 1),
    // so that one entry covers OPPO, Realme, OnePlus-domestic and HeyTap accounts alike.
    public static readonly OppoBrand OneplusOversea = new(
        "OnePlus (oversea)", 0, "https://accounts.oneplus.com/packages/account_web_sdk/index.umd.js",
        "PpGBVhay64GC2iBajrsDfU", "https://pcassistant-opacc-gl.oppo.com/html/logonBack.html");

    public static readonly OppoBrand Oppo = new(
        "OPPO", 1, "https://id.heytap.com/packages/account_web_sdk/index.umd.js",
        "8Bn4FenJAasH23ziynsS2u", "https://pcassistant-account-gl.oppo.com/html/logonBack.html");
}

/// <summary>
/// Hosts OPPO's own official, publicly embeddable account-login widget
/// (`https://id.heytap.com/packages/account_web_sdk/index.umd.js`) in a WebView2 popup,
/// using the same real desktop `bizAppKey` the official "O+Connect" Windows app uses.
/// The widget itself (real Chromium, real OPPO JS) handles QR/password login, 2FA, and
/// the final session exchange — none of that is reimplemented here.
/// </summary>
public sealed class OppoWebLoginForm : Form
{
    // A pinned, known location (rather than WebView2's own default profile path) so
    // it can be deliberately cleared later — without this, the login widget's cookies
    // persist across attempts and it silently continues the same OPPO account instead
    // of showing a fresh login page, with no way to switch accounts short of finding
    // and deleting WebView2's own default profile folder by hand.
    public static readonly string UserDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OSharePC", "OppoWebView2");

    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly OppoBrand _brand;
    private readonly string _html;

    /// <summary>Set once the widget reports success, cancellation, or an error — read
    /// this only after the form has closed.</summary>
    public OppoWebLoginResult? Result { get; private set; }
    public Exception? Error { get; private set; }

    public OppoWebLoginForm(OppoBrand brand, string language = "en-US")
    {
        _brand = brand;
        Text = "Log in to OPPO account";
        Size = new Size(480, 720);
        MinimumSize = new Size(420, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_webView);
        _html = BuildHtml(brand, language);
        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            Log.Info($"OppoWebLogin: initializing WebView2 (brand={_brand.Name}, sdk={_brand.SdkScriptUrl}, bizAppKey={_brand.BizAppKey}, callbackUrl={_brand.CallbackUrl})");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: UserDataFolder);
            await _webView.EnsureCoreWebView2Async(env);
            Log.Info($"OppoWebLogin: WebView2 runtime version {_webView.CoreWebView2.Environment.BrowserVersionString}");
            _webView.CoreWebView2.WebMessageReceived += OnMessage;
            // The widget's onSuccess callback only fires for an embedded/iframe use.
            // In a plain top-level page like this one, finishing login instead performs
            // a real full-page navigation to `callbackUrl` — a backend-only page that
            // renders blank and is meant to be intercepted (by a native app watching
            // navigation), not actually loaded. Catch it here and pull the result out
            // of its URL instead of letting it load (see
            // docs/oppo-account-login-notes.md).
            _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.NavigateToString(_html);
        }
        catch (Exception ex)
        {
            Log.Warn($"OppoWebLogin: init failed: {ex}");
            Error = ex;
            Close();
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Logged for every navigation, matched or not — this is the actual trail of
        // where the widget takes the page, which is the main thing worth having on
        // record if a login attempt gets stuck somewhere unexpected.
        try
        {
            var uri = new Uri(e.Uri);
            Log.Info($"OppoWebLogin: navigating to {uri.GetLeftPart(UriPartial.Path)}");
        }
        catch
        {
            Log.Info("OppoWebLogin: navigating to an invalid or redacted URL");
        }

        // Match on the actual navigation TARGET's own host+path, not on whether the
        // words "logonback"/"pcassistant" appear anywhere in the URL text — the auth
        // entry-point URL (id.oppo.com/.../auth-and-callback?...&callback=<encoded
        // callback URL>) legitimately carries the callback URL as a query PARAMETER
        // value, which a plain substring check would (and did) false-positive on,
        // cancelling the very first hop before the real login page ever loaded.
        bool isCallback;
        try
        {
            var target = new Uri(e.Uri);
            var expected = new Uri(_brand.CallbackUrl);
            isCallback = string.Equals(target.Host, expected.Host, StringComparison.OrdinalIgnoreCase) &&
                         target.AbsolutePath.StartsWith(expected.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
            isCallback = false;
        }
        if (!isCallback) return;

        try
        {
            e.Cancel = true;
            // Keep the full URL — its query string and/or fragment carry whatever OPPO
            // hands back. The exact shape wasn't known ahead of time (see
            // OShareBridgeServer.TryParseOppoWebLoginMsg for the parsing side), so
            // nothing is dropped here.
            Log.Info("OppoWebLogin: intercepted callback navigation");
            Result = new OppoWebLoginResult(e.Uri, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"OppoWebLogin: error handling callback navigation: {ex}");
            Error = ex;
        }
        finally
        {
            Close();
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
            Log.Warn($"OppoWebLogin: navigation to {_webView.Source} failed: {e.WebErrorStatus}");
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "success":
                    var msg = root.GetProperty("msg").GetString() ?? "";
                    var country = root.TryGetProperty("countryCode", out var c) ? c.GetString() : null;
                    Log.Info($"OppoWebLogin: widget called onSuccess directly (embedded-style) — msg={msg} countryCode={country}");
                    Result = new OppoWebLoginResult(msg, country);
                    Close();
                    break;
                case "cancel":
                    Log.Info("OppoWebLogin: widget called onCancel");
                    Close();
                    break;
                case "error":
                    var errText = root.TryGetProperty("error", out var err) ? err.GetString() : "OPPO login widget reported an error.";
                    Log.Warn($"OppoWebLogin: widget reported an error: {errText}");
                    Error = new InvalidOperationException(errText);
                    Close();
                    break;
                case "create":
                    Log.Info("OppoWebLogin: widget finished loading (onCreate)");
                    break;
                case "console":
                    var level = root.TryGetProperty("level", out var lv) ? lv.GetString() : "log";
                    var text = root.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                    Log.Info($"OppoWebLogin: page console.{level}: {text}");
                    break;
                case "jserror":
                    var jsErr = root.TryGetProperty("text", out var jt) ? jt.GetString() : "";
                    Log.Warn($"OppoWebLogin: uncaught page error: {jsErr}");
                    break;
                default:
                    Log.Info($"OppoWebLogin: unrecognized message type '{type}': {e.WebMessageAsJson}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"OppoWebLogin: failed to handle a widget message ({e.WebMessageAsJson}): {ex.Message}");
            Error = ex;
            Close();
        }
    }

    private static string BuildHtml(OppoBrand brand, string language) => $$"""
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
        <body style="margin:0;font-family:Segoe UI,sans-serif">
        <div id="app"></div>
        <script>
        // Forward the page's own console output and any uncaught exception back to the
        // host so it lands in sender.log — this is the main way to see what the widget
        // itself is doing/complaining about, since it otherwise runs invisibly.
        (function () {
            const post = (payload) => { try { window.chrome.webview.postMessage(payload); } catch (e) {} };
            for (const level of ["log", "info", "warn", "error"]) {
                const orig = console[level];
                console[level] = function (...args) {
                    try { post({ type: "console", level, text: args.map(String).join(" ") }); } catch (e) {}
                    return orig.apply(console, args);
                };
            }
            window.onerror = (message, source, lineno, colno) => {
                post({ type: "jserror", text: `${message} (${source}:${lineno}:${colno})` });
            };
            window.addEventListener("unhandledrejection", (e) => {
                post({ type: "jserror", text: "unhandled rejection: " + String(e.reason) });
            });
        })();
        function loadScript(src) {
            return new Promise((resolve, reject) => {
                const s = document.createElement("script");
                s.src = src;
                s.onload = resolve;
                s.onerror = () => reject(new Error("failed to load " + src));
                document.body.appendChild(s);
            });
        }
        loadScript({{JsonSerializer.Serialize(brand.SdkScriptUrl)}} + "?ts=" + Date.now())
            .then(() => {
                const sdk = window.account_web_sdk;
                const brands = [sdk.supportBrand.oneplus_oversea, sdk.supportBrand.oppo,
                                 sdk.supportBrand.realme, sdk.supportBrand.oneplus, sdk.supportBrand.heytap];
                const widget = new sdk({ brand: brands[{{brand.SdkBrandIndex}}], environment: "prod", enableLog: true, timeout: 20000 });
                widget.loginPopper({
                    bizAppKey: {{JsonSerializer.Serialize(brand.BizAppKey)}},
                    callbackUrl: {{JsonSerializer.Serialize(brand.CallbackUrl)}},
                    language: {{JsonSerializer.Serialize(language)}},
                    onSuccess(msg, countryCode) {
                        window.chrome.webview.postMessage({ type: "success", msg: msg, countryCode: countryCode });
                    },
                    onCancel() {
                        window.chrome.webview.postMessage({ type: "cancel" });
                    },
                    onCreate() {
                        window.chrome.webview.postMessage({ type: "create" });
                    }
                });
            })
            .catch(e => {
                window.chrome.webview.postMessage({ type: "error", error: String(e) });
            });
        </script>
        </body>
        </html>
        """;
}

/// <summary>
/// Runs an <see cref="OppoWebLoginForm"/> to completion on its own dedicated STA thread
/// with its own WinForms message loop, so it can be launched from the bridge server's
/// headless mode (which has no <c>Application.Run</c> of its own — see Program.cs).
/// </summary>
public static class OppoWebLogin
{
    private static readonly object InitLock = new();
    private static bool _appConfigured;

    /// <summary>Deletes the login widget's WebView2 profile (cookies, local storage,
    /// cache) so the next login shows a fresh page instead of silently continuing
    /// whichever OPPO account was last signed into. Must not be called while a login
    /// window is open — the files are locked while WebView2 is using them.</summary>
    public static void ClearBrowserData()
    {
        if (!Directory.Exists(OppoWebLoginForm.UserDataFolder))
        {
            Log.Info("OppoWebLogin: ClearBrowserData — nothing to clear, no profile folder exists yet.");
            return;
        }
        Directory.Delete(OppoWebLoginForm.UserDataFolder, recursive: true);
        Log.Info("OppoWebLogin: cleared the login widget's browser profile.");
    }

    public static Task<OppoWebLoginResult?> ShowAsync(OppoBrand brand, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<OppoWebLoginResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                EnsureAppConfigured();
                using var form = new OppoWebLoginForm(brand);
                Application.Run(form);
                Log.Info(form.Error is not null
                    ? $"OppoWebLogin: finished with an error: {form.Error.Message}"
                    : form.Result is not null
                        ? $"OppoWebLogin: finished with a result (msg length={form.Result.Msg.Length})"
                        : "OppoWebLogin: window closed with no result (likely closed manually)");
                if (form.Error is not null)
                    tcs.TrySetException(form.Error);
                else
                    tcs.TrySetResult(form.Result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (ct.CanBeCanceled)
            ct.Register(() => tcs.TrySetCanceled(ct));

        return tcs.Task;
    }

    private static void EnsureAppConfigured()
    {
        lock (InitLock)
        {
            if (_appConfigured) return;
            _appConfigured = true;
            // Mirrors the auto-generated ApplicationConfiguration.Initialize() call the
            // legacy WinForms UI (--legacy-ui) makes — safe to call here too since the
            // headless bridge path (the one actually used by oshare_gui.exe) never calls
            // it itself. Guarded so a second login attempt in the same process doesn't
            // call it twice.
            ApplicationConfiguration.Initialize();
        }
    }
}
