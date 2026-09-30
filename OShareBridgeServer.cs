using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OShareSender.OppoAccount;

namespace OShareSender;

/// <summary>Localhost control plane for the Flutter shell. Manages SenderEngine lifecycle and provides a REST/polling event bridge.</summary>
public sealed class OShareBridgeServer : IAsyncDisposable
{
    public const int Port = 8960;
    public const string TokenHeader = "X-OSharePC-Bridge-Token";

    private readonly SenderEngine _engine = new();
    private readonly ConcurrentQueue<BridgeEvent> _events = new();
    private readonly ConcurrentDictionary<string, PendingTransferInfo> _pendingTransfers = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentlyRejected = new();
    private readonly byte[] _authTokenBytes;
    private long _seq = 0;
    // Regenerated each process start so a client can tell a fresh backend (event
    // sequence restarted at 0) from the one it was already polling, and fast-forward
    // its cursor instead of silently discarding every event as "already seen".
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private WebApplication? _app;
    private string _lastState = "starting";
    private Task? _sendTask;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _shutdownRequested;
    private int _sendQuiet;
    private string? _sendRequestId;
    private OppoAccountLoginSession? _oppoLoginSession;
    private readonly SemaphoreSlim _oppoLoginGate = new(1, 1);
    private volatile bool _oppoWebLoginRunning;
    private OppoAccountBleAdvertiser? _oppoBleAdvertiser;
    private OppoWindowsSenselessAdvertiser? _oppoWindowsAdvertiser;
    private readonly SemaphoreSlim _oppoBleGate = new(1, 1);

    public OShareBridgeServer(string authToken)
    {
        if (string.IsNullOrWhiteSpace(authToken) || authToken.Length < 32)
            throw new ArgumentException("Bridge authentication token is missing or too short.", nameof(authToken));
        _authTokenBytes = Encoding.UTF8.GetBytes(authToken);
    }

    public async Task StartAsync()
    {
        _engine.DeviceSeen += device => Push("deviceSeen", new { name = DisplayName(device), address = device.Address.ToString(), kind = device.KindLabel, rssi = device.Rssi });
        _engine.TransferStateChanged += (taskId, state) =>
        {
            _lastState = state;
            if (state.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                state.Contains("abort", StringComparison.OrdinalIgnoreCase) ||
                state.Contains("reject", StringComparison.OrdinalIgnoreCase) ||
                state.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
                state.Contains("disconnect", StringComparison.OrdinalIgnoreCase))
            {
                ClearPendingTransfers();
            }
            Push("state", new { taskId, state });
        };
        _engine.ReceiveProgress += (done, total) => Push("receiveProgress", new { done, total });
        _engine.ReceiveMetadataUpdated += metadata => Push("receiveMetadata", new
        {
            taskId = metadata.TaskId,
            senderName = metadata.SenderName,
            fileName = metadata.FileName,
            fileCount = metadata.FileCount,
            totalSize = metadata.TotalSize,
            mimeType = metadata.MimeType,
        });
        _engine.ReceiveCompleted += (sender, files) =>
        {
            ClearPendingTransfers();
            Push("receiveCompleted", new { sender, files, count = files.Count });
        };
        _engine.ReceiveFailed += (sender, error) =>
        {
            ClearPendingTransfers();
            Push("receiveFailed", new { sender, error });
        };
        _engine.Server.DownloadProgress += (sent, total) => Push("sendProgress", new { sent, total });
        // Do NOT mark success when the HTTP body merely finished writing. The phone
        // can still cancel/reject before acknowledging receipt.
        _engine.Server.StatusReceived += (taskId, type, reason) =>
        {
            if (type == 1)
            {
                var quiet = Volatile.Read(ref _sendQuiet) != 0;
                var requestId = Volatile.Read(ref _sendRequestId);
                Push("sendCompleted", new { taskId, quiet, requestId });
                Interlocked.CompareExchange(ref _sendRequestId, null, requestId);
                Interlocked.Exchange(ref _sendQuiet, 0);
            }
        };
        _engine.Server.TransferFailed += (taskId, error) =>
        {
            var quiet = Volatile.Read(ref _sendQuiet) != 0;
            var requestId = Volatile.Read(ref _sendRequestId);
            Push("sendFailed", new { taskId, error, quiet, requestId });
            Interlocked.CompareExchange(ref _sendRequestId, null, requestId);
            Interlocked.Exchange(ref _sendQuiet, 0);
        };

        _engine.ConfirmIncomingTransfer = (name, mimeType, count) => ConfirmViaBridge(name, mimeType, count);

        _engine.ConfirmIncomingOShare = offer =>
            ConfirmViaBridge(offer.SenderId, $"Wi-Fi Direct: {offer.Ssid}", "1", offer.SenderId);

        await _engine.StartAsync(SenderEngine.DefaultPort);
        StartOppoBleAdvertiserIfConfigured();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, Port));
        builder.Services.AddRouting();
        var app = builder.Build();

        // The bridge is a native localhost-only control plane, not a browser API.
        // Every /api request must carry the per-GUI-process token. Browser-originated
        // requests are rejected outright; the custom header also forces CORS preflight
        // for ordinary web pages before they can reach any state-changing endpoint.
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/api"))
            {
                await next();
                return;
            }

            if (HttpMethods.IsOptions(ctx.Request.Method) || ctx.Request.Headers.ContainsKey("Origin"))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!ctx.Request.Headers.TryGetValue(TokenHeader, out var suppliedValues))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var supplied = suppliedValues.ToString();
            var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
            var valid = suppliedBytes.Length == _authTokenBytes.Length &&
                        CryptographicOperations.FixedTimeEquals(suppliedBytes, _authTokenBytes);
            CryptographicOperations.ZeroMemory(suppliedBytes);
            if (!valid)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            ctx.Response.Headers.CacheControl = "no-store";
            await next();
        });

        app.MapGet("/api/status", () =>
        {
            var pending = _pendingTransfers.Values.FirstOrDefault();
            return Results.Json(new
            {
                seq = Interlocked.Read(ref _seq),
                instanceId = _instanceId,
                connected = _engine.Lan is not null,
                receiveEnabled = _engine.ReceiveEnabled,
                senderId = _engine.SenderId,
                deviceName = _engine.Advertiser.DeviceName,
                saveDirectory = _engine.Receiver.SaveDirectory,
                lanIp = _engine.Lan?.IpString ?? "",
                mac = _engine.Lan?.MacHex12 ?? "",
                transferPort = _engine.Port,
                state = _lastState,
                themeMode = SettingsStore.Current.ThemeMode,
                quickSaveMode = SettingsStore.Current.QuickSaveMode,
                sendQuiet = Volatile.Read(ref _sendQuiet) != 0,
                minimizeToTray = SettingsStore.Current.MinimizeToTray,
                closeToTray = SettingsStore.Current.CloseToTray,
                oppoAccountName = SettingsStore.Current.OppoAccountName,
                oppoSsoid = SettingsStore.Current.OppoSsoid,
                oppoAvatarUrl = SettingsStore.Current.OppoAvatarUrl,
                pendingTransfer = pending is null ? null : new
                {
                    id = pending.Id,
                    name = pending.Name,
                    mimeType = pending.MimeType,
                    count = pending.Count,
                },
            });
        });

        app.MapGet("/api/devices", () =>
        {
            var devices = _engine.Scanner.Devices;
            return Results.Json(devices.Select(d => new
            {
                address = d.Address.ToString(),
                name = DisplayName(d, devices),
                kind = d.KindLabel,
                rssi = d.Rssi,
                oShare = d.Kind == PhoneKind.OShare,
                addressText = d.AddressStr,
            }));
        });

        app.MapGet("/api/events", (long? since) =>
        {
            var minSeq = since ?? 0;
            var arr = _events.Where(e => e.Seq > minSeq).Take(50).Select(e => new
            {
                seq = e.Seq,
                type = e.Type,
                data = e.Data,
                at = e.At,
            }).ToArray();
            return Results.Json(arr);
        });

        app.MapPost("/api/receive", async (ReceiveRequest body) =>
        {
            await _engine.SetReceiveEnabledAsync(body.Enabled);
            return Results.Ok(new { enabled = _engine.ReceiveEnabled });
        });

        app.MapPost("/api/confirm-receive", (ConfirmReceiveRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Id))
                return Results.BadRequest(new { error = "Expected {id:string, accept:boolean}" });

            if (_pendingTransfers.TryRemove(body.Id, out var pending))
            {
                if (!body.Accept)
                {
                    _recentlyRejected[pending.Identity] = DateTimeOffset.UtcNow;
                }
                pending.Tcs.TrySetResult(body.Accept);
                Push("transferResolved", new { id = body.Id, accepted = body.Accept });
                return Results.Ok(new { success = true, id = body.Id, accepted = body.Accept });
            }
            return Results.Ok(new { success = false, message = "Transfer already completed, cancelled, or expired." });
        });

        app.MapPost("/api/cancel-transfers", () =>
        {
            ClearPendingTransfers();
            return Results.Ok(new { success = true });
        });

        app.MapPost("/api/shutdown", () =>
        {
            Log.Info("Shutdown requested via /api/shutdown");
            if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
                _ = ShutdownAfterResponseAsync();
            return Results.Ok(new { status = "shutting_down" });
        });

        app.MapPost("/api/settings", async (SettingsRequest body) =>
        {
            if (!string.IsNullOrWhiteSpace(body.SaveDirectory)) _engine.UpdateSaveDirectory(body.SaveDirectory);
            SettingsStore.Save(
                themeMode: body.ThemeMode,
                quickSaveMode: body.QuickSaveMode,
                minimizeToTray: body.MinimizeToTray,
                closeToTray: body.CloseToTray,
                oppoSsoid: body.OppoSsoid,
                oppoAccountName: body.OppoAccountName);
            if (body.OppoSsoid is not null)
            {
                _engine.SetOppoAccountIdentity(body.OppoSsoid);
                if (string.IsNullOrWhiteSpace(body.OppoSsoid))
                    await StopOppoBleAdvertiserAsync();
                else
                    await StartOppoBleAdvertiserAsync(body.OppoSsoid);
            }
            return Results.Ok(new
            {
                deviceName = _engine.Advertiser.DeviceName,
                saveDirectory = _engine.Receiver.SaveDirectory,
                themeMode = SettingsStore.Current.ThemeMode,
                quickSaveMode = SettingsStore.Current.QuickSaveMode,
                minimizeToTray = SettingsStore.Current.MinimizeToTray,
                closeToTray = SettingsStore.Current.CloseToTray,
                oppoSsoid = SettingsStore.Current.OppoSsoid,
                oppoAccountName = SettingsStore.Current.OppoAccountName,
            });
        });

        app.MapPost("/api/cancel", () =>
        {
            _engine.CancelTransfer();
            return Results.Ok(new { cancelled = true });
        });

        app.MapPost("/api/stage", (StageRequest body) =>
        {
            if (body.Files is null || body.Files.Length == 0)
            {
                _engine.ClearStaged();
                return Results.Ok(new { taskId = "", fileCount = 0, totalSize = 0L, cleared = true });
            }
            var files = body.Files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0)
            {
                _engine.ClearStaged();
                Log.Warn("staging failed: no existing files were supplied");
                return Results.BadRequest(new { error = "No existing files were supplied." });
            }
            var task = _engine.StageFiles(files)!;
            _engine.PrewarmContactsDevices();
            return Results.Ok(new { taskId = task.TaskId, fileCount = task.FileCount, totalSize = task.TotalSize });
        });

        app.MapPost("/api/send", async (SendRequest body) =>
        {
            var requestId = string.IsNullOrWhiteSpace(body.RequestId)
                ? Guid.NewGuid().ToString("N")
                : body.RequestId!;
            var quiet = body.Quiet;

            IResult Reject(int statusCode, string error)
            {
                if (quiet) Push("sendFailed", new { error, quiet, requestId });
                return statusCode switch
                {
                    StatusCodes.Status400BadRequest => Results.BadRequest(new { error }),
                    StatusCodes.Status404NotFound => Results.NotFound(new { error }),
                    _ => Results.Conflict(new { error }),
                };
            }

            if (string.IsNullOrWhiteSpace(body.Address) || !ulong.TryParse(body.Address, out var address))
                return Reject(StatusCodes.Status400BadRequest, "Expected a BLE address string.");
            var device = _engine.Scanner.Devices.FirstOrDefault(d => d.Address == address);
            if (device is null) return Reject(StatusCodes.Status404NotFound, "Device is no longer visible.");
            // The staging slot is a single global one shared by the main window and the
            // desktop drop-panel process. A caller must name the taskId it staged so a
            // send never goes out against files a different process staged over it.
            if (string.IsNullOrWhiteSpace(body.TaskId))
                return Reject(StatusCodes.Status400BadRequest, "Expected the staged taskId.");
            if (!await _sendGate.WaitAsync(0)) return Reject(StatusCodes.Status409Conflict, "A transfer is already running.");
            try
            {
                if (_sendTask is { IsCompleted: false }) return Reject(StatusCodes.Status409Conflict, "A transfer is already running.");
                if (!string.Equals(_engine.StagedTaskId, body.TaskId, StringComparison.Ordinal))
                    return Reject(StatusCodes.Status409Conflict, "Staged files changed — restage before sending.");
                Interlocked.Exchange(ref _sendRequestId, requestId);
                Interlocked.Exchange(ref _sendQuiet, body.Quiet ? 1 : 0);
                Push("sendStarted", new { device = device.Name, quiet, requestId });
                _sendTask = _engine.SendToAsync(device);
            }
            finally { _sendGate.Release(); }
            _ = _sendTask.ContinueWith(t =>
            {
                if (t.IsFaulted || t.IsCanceled)
                {
                    // A cancelled task (link dropped mid-send) must still tell the UI, or it waits forever.
                    var error = t.IsCanceled ? "The transfer was interrupted." : t.Exception?.GetBaseException().Message ?? "send failed";
                    Log.Warn($"send failed: {error}");
                    Push("sendFailed", new { error, quiet, requestId });
                    Interlocked.CompareExchange(ref _sendRequestId, null, requestId);
                    Interlocked.Exchange(ref _sendQuiet, 0);
                }
            });
            return Results.Accepted(value: new { device = device.Name, address = device.Address, requestId });
        });

        app.MapPost("/api/oppo-account/login/start", async () =>
        {
            await _oppoLoginGate.WaitAsync();
            try
            {
                if (_oppoLoginSession is not null) await _oppoLoginSession.DisposeAsync();
                _oppoLoginSession = new OppoAccountLoginSession();
                _oppoLoginSession.Start((type, data) => Push(type, data));
            }
            finally { _oppoLoginGate.Release(); }
            return Results.Ok(new { started = true });
        });

        app.MapPost("/api/oppo-account/login/cancel", async () =>
        {
            await _oppoLoginGate.WaitAsync();
            try
            {
                if (_oppoLoginSession is not null)
                {
                    await _oppoLoginSession.DisposeAsync();
                    _oppoLoginSession = null;
                }
            }
            finally { _oppoLoginGate.Release(); }
            return Results.Ok(new { cancelled = true });
        });

        app.MapPost("/api/oppo-account/login/verification/gather", async (OppoVerificationRequest body) =>
        {
            await _oppoLoginGate.WaitAsync();
            try
            {
                if (_oppoLoginSession is null)
                    return Results.Conflict(new { error = "There is no active OPPO login." });
                if (string.IsNullOrWhiteSpace(body.VerMethod))
                    return Results.BadRequest(new { error = "verMethod is required" });

                try
                {
                    var result = await _oppoLoginSession.GatherVerificationAsync(body.VerMethod);
                    Push("oppoAccountVerificationChallenge", new
                    {
                        verMethod = result.VerMethod,
                        codeLength = result.CodeLength,
                    });
                    return Results.Ok(new
                    {
                        sent = true,
                        verMethod = result.VerMethod,
                        codeLength = result.CodeLength,
                    });
                }
                catch (Exception ex)
                {
                    Push("oppoAccountVerificationError", new { error = ex.Message });
                    return Results.BadRequest(new { error = ex.Message });
                }
            }
            finally { _oppoLoginGate.Release(); }
        });

        app.MapPost("/api/oppo-account/login/verification/validate", async (OppoVerificationRequest body) =>
        {
            await _oppoLoginGate.WaitAsync();
            try
            {
                if (_oppoLoginSession is null)
                    return Results.Conflict(new { error = "There is no active OPPO login." });
                if (string.IsNullOrWhiteSpace(body.VerMethod) || string.IsNullOrWhiteSpace(body.ValidateData))
                    return Results.BadRequest(new { error = "verMethod and validateData are required" });

                try
                {
                    var result = await _oppoLoginSession.ValidateVerificationAsync(
                        body.VerMethod, body.ValidateData);
                    Push("oppoAccountVerificationValidated", new
                    {
                        verMethod = body.VerMethod,
                        ticket = result.Ticket,
                        needNextRound = result.NeedNextRound,
                        sessionPending = true,
                    });
                    return Results.Ok(new
                    {
                        accepted = false,
                        ticket = result.Ticket,
                        needNextRound = result.NeedNextRound,
                        sessionPending = true,
                    });
                }
                catch (Exception ex)
                {
                    Push("oppoAccountVerificationError", new { error = ex.Message });
                    return Results.BadRequest(new { error = ex.Message });
                }
            }
            finally { _oppoLoginGate.Release(); }
        });

        app.MapPost("/api/oppo-account/weblogin/start", (OppoWebLoginStartRequest? body) =>
        {
            if (_oppoWebLoginRunning) return Results.Conflict(new { error = "A login attempt is already open." });
            var brand = string.Equals(body?.Brand, "oneplus_oversea", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(body?.Brand, "oneplus", StringComparison.OrdinalIgnoreCase)
                ? OppoBrand.OneplusOversea
                : OppoBrand.Oppo;

            _oppoWebLoginRunning = true;
            Push("oppoWebLoginStarted", new { brand = brand.Name });
            _ = RunOppoWebLoginAsync(brand);
            return Results.Ok(new { started = true, brand = brand.Name });
        });

        app.MapPost("/api/oppo-account/logout", async () =>
        {
            SettingsStore.ClearOppoAccount();
            _engine.SetOppoAccountIdentity(null);
            await StopOppoBleAdvertiserAsync();
            Log.Info("OppoWebLogin: logged out — cleared account identity and stopped the same-account BLE beacon.");
            Push("oppoAccountLoggedOut", new { });
            return Results.Ok(new { loggedOut = true });
        });

        app.MapPost("/api/oppo-account/weblogin/clear-data", () =>
        {
            if (_oppoWebLoginRunning)
                return Results.Conflict(new { error = "Close the login window first." });
            try
            {
                OppoWebLogin.ClearBrowserData();
                return Results.Ok(new { cleared = true });
            }
            catch (Exception ex)
            {
                Log.Warn($"OppoWebLogin: ClearBrowserData failed: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        app.MapPost("/api/oppo-account/ble-advertise/start", async (OppoBleAdvertiseStartRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body.Ssoid))
                return Results.BadRequest(new { error = "ssoid is required" });
            SettingsStore.Save(oppoSsoid: body.Ssoid);
            _engine.SetOppoAccountIdentity(body.Ssoid);
            await StartOppoBleAdvertiserAsync(body.Ssoid);
            return Results.Ok(new { started = true });
        });

        app.MapPost("/api/oppo-account/ble-advertise/stop", async () =>
        {
            await StopOppoBleAdvertiserAsync();
            return Results.Ok(new { stopped = true });
        });

        _app = app;
        await app.StartAsync();
        Log.Info($"Authenticated Flutter bridge listening on http://127.0.0.1:{Port}");
    }

    private static string DisplayName(PhoneDevice device, IReadOnlyList<PhoneDevice>? all = null)
    {
        var name = string.IsNullOrWhiteSpace(device.Name) ? "Nearby phone" : device.Name;
        all ??= [device];
        var sameName = all.Count(other => string.Equals(
            string.IsNullOrWhiteSpace(other.Name) ? "Nearby phone" : other.Name,
            name, StringComparison.OrdinalIgnoreCase));
        if (sameName <= 1) return name;

        var identity = PhoneScanner.StableIdentity(device);
        var identityValue = identity[(identity.LastIndexOf(':') + 1)..];
        var suffix = new string(identityValue
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToUpperInvariant();
        if (suffix.Length > 4) suffix = suffix[..4];
        return $"{name} [ID {suffix}]";
    }

    /// <summary>Routes an incoming-transfer confirmation (stock or OShare) through
    /// the bridge's pending-transfer event flow; the Flutter UI resolves it via
    /// POST /api/confirm-receive. Times out to 'reject' after 30 seconds.</summary>
    private Task<bool> ConfirmViaBridge(string name, string mimeType, string count, string? identity = null)
    {
        ClearPendingTransfers();
        identity ??= name;

        if (_recentlyRejected.TryGetValue(identity, out var rejTime) &&
            DateTimeOffset.UtcNow - rejTime < TimeSpan.FromSeconds(5))
        {
            Log.Info($"Auto-rejecting repeated incoming transfer identity='{identity}' name='{name}' within cooldown window.");
            return Task.FromResult(false);
        }

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingTransfers[id] = new PendingTransferInfo(id, name, identity, mimeType, count, tcs);

        Push("incomingTransfer", new { id, name, mimeType, count });

        _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ =>
        {
            if (_pendingTransfers.TryRemove(id, out var pending))
            {
                pending.Tcs.TrySetResult(false);
                Push("transferCancelled", new { id });
            }
        });

        return tcs.Task;
    }

    private void ClearPendingTransfers()
    {
        foreach (var kvp in _pendingTransfers)
        {
            if (_pendingTransfers.TryRemove(kvp.Key, out var pending))
            {
                pending.Tcs.TrySetResult(false);
                Push("transferCancelled", new { id = kvp.Key });
            }
        }
    }

    /// <summary>
    /// Runs the WebView2-hosted OPPO login widget (see OppoAccount/OppoWebLoginForm.cs)
    /// to completion and pushes the result. This replaces the old hand-rolled REST/2FA
    /// flow above, which OPPO's server rejected for reasons documented in
    /// docs/oppo-account-login-notes.md.
    /// </summary>
    private async Task RunOppoWebLoginAsync(OppoBrand brand)
    {
        try
        {
            var result = await OppoWebLogin.ShowAsync(brand);
            if (result is null)
            {
                Log.Info("OppoWebLogin (bridge): login window closed without a result — treating as cancelled.");
                Push("oppoWebLoginCancelled", new { });
                return;
            }

            Log.Info($"OppoWebLogin (bridge): callback received ({RedactUrl(result.Msg)})");

            var (code, callbackCountryCode) = TryParseCallbackCode(result.Msg);
            if (code is not null)
            {
                // This is the normal case: result.Msg is the callback URL carrying a
                // one-time OAuth-style code, which still needs to be exchanged for an
                // actual session — see OppoAccountExchange.cs. Done immediately, since
                // the code appears to be short-lived.
                var cfg = ReferenceEquals(brand, OppoBrand.OneplusOversea)
                    ? OppoAccountExchange.OneplusOversea
                    : OppoAccountExchange.Oppo;
                try
                {
                    using var exchanged = await OppoAccountExchange.ExchangeCodeAsync(cfg, code, callbackCountryCode);
                    var exchangedText = exchanged.RootElement.ToString();
                    var exchangeCode = exchanged.RootElement.TryGetProperty("code", out var exchangeCodeEl)
                        ? exchangeCodeEl.ToString()
                        : "unknown";
                    Log.Info($"OppoWebLogin (bridge): session exchange completed (responseCode={exchangeCode})");

                    // Confirmed real shape (seen live): {"code":200,"data":{"accessToken":
                    // "...","expireIn":..,"accessDomain":"...","userDetail":{"userId":
                    // "<the actual ssoid>","userName":"<display name>",...}}}. The
                    // generic field-name fallback below is kept only for the (unlikely)
                    // case that shape ever changes.
                    string? exAccountName = null, exSsoid = null, exAvatarUrl = null;
                    if (exchanged.RootElement.TryGetProperty("data", out var dataEl) &&
                        dataEl.ValueKind == JsonValueKind.Object &&
                        dataEl.TryGetProperty("userDetail", out var userDetail) &&
                        userDetail.ValueKind == JsonValueKind.Object)
                    {
                        if (userDetail.TryGetProperty("userId", out var uid) && uid.ValueKind == JsonValueKind.String)
                            exSsoid = uid.GetString();
                        if (userDetail.TryGetProperty("userName", out var un) && un.ValueKind == JsonValueKind.String)
                            exAccountName = un.GetString();
                        if (userDetail.TryGetProperty("avatar", out var avatar) && avatar.ValueKind == JsonValueKind.Object)
                        {
                            // "default" is the user's own actual photo (a per-user
                            // filename); "personalizationDefault" is the generic
                            // placeholder silhouette used when there's no photo — prefer
                            // the real one.
                            if (avatar.TryGetProperty("default", out var def) && def.ValueKind == JsonValueKind.String)
                                exAvatarUrl = def.GetString();
                            else if (avatar.TryGetProperty("personalizationDefault", out var pd) && pd.ValueKind == JsonValueKind.String)
                                exAvatarUrl = pd.GetString();
                        }
                    }
                    if (exAccountName is null && exSsoid is null)
                        (exAccountName, exSsoid) = TryParseOppoWebLoginMsg(exchangedText);

                    Log.Info($"OppoWebLogin (bridge): parsed accountName={exAccountName ?? "(none)"}, ssoid={(exSsoid is null ? "(none)" : "(present)")}, avatarUrl={(exAvatarUrl is null ? "(none)" : "(present)")}");
                    if (exAccountName is not null || exSsoid is not null || exAvatarUrl is not null)
                        SettingsStore.Save(oppoSsoid: exSsoid, oppoAccountName: exAccountName, oppoAvatarUrl: exAvatarUrl);
                    if (exSsoid is not null)
                    {
                        _engine.SetOppoAccountIdentity(exSsoid);
                        if (!string.IsNullOrWhiteSpace(exSsoid)) await StartOppoBleAdvertiserAsync(exSsoid);
                        else await StopOppoBleAdvertiserAsync();
                    }

                    Push("oppoWebLoginSuccess", new
                    {
                        accountName = exAccountName,
                        ssoid = exSsoid,
                        avatarUrl = exAvatarUrl,
                        countryCode = callbackCountryCode,
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn($"OppoWebLogin (bridge): code exchange failed: {ex.Message}");
                    Push("oppoWebLoginError", new { error = $"Login succeeded but the session exchange failed: {ex.Message}" });
                }
                return;
            }

            // Fallback for a shape that isn't the callback URL (e.g. if a future change
            // ever gets the widget's onSuccess to fire directly, embedded-style) — treat
            // it as an already-finished result, same as before this exchange step existed.
            var (accountName, ssoid) = TryParseOppoWebLoginMsg(result.Msg);
            Log.Info($"OppoWebLogin (bridge): parsed accountName={accountName ?? "(none)"}, ssoid={(ssoid is null ? "(none)" : "(present)")}");
            if (accountName is not null || ssoid is not null)
                SettingsStore.Save(oppoSsoid: ssoid, oppoAccountName: accountName);
            if (ssoid is not null)
            {
                _engine.SetOppoAccountIdentity(ssoid);
                if (!string.IsNullOrWhiteSpace(ssoid)) await StartOppoBleAdvertiserAsync(ssoid);
                else await StopOppoBleAdvertiserAsync();
            }

            Push("oppoWebLoginSuccess", new
            {
                accountName,
                ssoid,
                countryCode = result.CountryCode,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"OPPO web login failed: {ex.Message}");
            Push("oppoWebLoginError", new { error = ex.Message });
        }
        finally
        {
            _oppoWebLoginRunning = false;
        }
    }

    private static readonly string[] AccountNameFields = { "accountName", "userName", "nickName", "account" };
    // "code" deliberately excluded — that field means the one-time OAuth-style exchange
    // code (handled explicitly by TryParseCallbackCode/OppoAccountExchange), not a
    // finished session id, and would be misleading if surfaced as one.
    private static readonly string[] SsoidFields = { "ssoid", "ssoId", "userId", "token", "authToken", "ticket" };

    /// <summary>Extracts the one-time exchange `code` (and its countryCode) from the
    /// login widget's callback URL, if <paramref name="msg"/> is that URL — this is the
    /// normal case (see OppoWebLoginForm.OnNavigationStarting). Returns (null, null) for
    /// anything else, e.g. a future embedded-widget `onSuccess` payload that's already a
    /// finished result rather than a code needing exchange.</summary>
    private static (string? Code, string? CountryCode) TryParseCallbackCode(string msg)
    {
        if (!msg.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !msg.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        try
        {
            var uri = new Uri(msg);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query.TrimStart('?'));
            var code = query.TryGetValue("code", out var c) ? c.ToString() : null;
            var countryCode = query.TryGetValue("countryCode", out var cc) ? cc.ToString() : null;
            return (string.IsNullOrEmpty(code) ? null : code, string.IsNullOrEmpty(countryCode) ? null : countryCode);
        }
        catch (UriFormatException)
        {
            return (null, null);
        }
    }

    /// <summary>Best-effort extraction of an account name/ssoid from the web login
    /// widget's result. Its exact shape wasn't known ahead of time: it can arrive either
    /// as a JSON object (if a future embedded/iframe use of the widget ever calls
    /// `onSuccess` directly) or — the actually-observed case — as the full URL OPPO
    /// redirected to on completion (`callbackUrl?...`), whose query string or fragment
    /// carries the result instead. Both are tried; failing both, both fields are left
    /// null and the raw value is still pushed to the UI for inspection.</summary>
    private static (string? AccountName, string? Ssoid) TryParseOppoWebLoginMsg(string msg)
    {
        if (msg.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            msg.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var uri = new Uri(msg);
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in new[] { uri.Query.TrimStart('?'), uri.Fragment.TrimStart('#') })
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    foreach (var pair in Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(raw))
                        fields[pair.Key] = pair.Value.ToString();
                }

                string? ReadField(string[] names) =>
                    names.Select(n => fields.TryGetValue(n, out var v) ? v : null).FirstOrDefault(v => v is not null);

                return (ReadField(AccountNameFields), ReadField(SsoidFields));
            }
            catch (UriFormatException)
            {
                return (null, null);
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(msg);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);

            // Also look one level into a "data" envelope — the exchange endpoint's own
            // error responses come back as {"code":..,"message":..,"data":null}, so a
            // real success response is likely {"code":200,"data":{...actual fields...}}.
            var searchRoots = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                ? new[] { data, root }
                : new[] { root };

            string? Read(string[] names)
            {
                foreach (var scope in searchRoots)
                    foreach (var name in names)
                        if (scope.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                            return v.GetString();
                return null;
            }

            return (Read(AccountNameFields), Read(SsoidFields));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private void Push(string type, object data)
    {
        var s = Interlocked.Increment(ref _seq);
        _events.Enqueue(new BridgeEvent(s, type, data, DateTimeOffset.UtcNow));
        while (_events.Count > 100) _events.TryDequeue(out _);
    }

    public async ValueTask DisposeAsync()
    {
        ClearPendingTransfers();
        if (_oppoLoginSession is not null) await _oppoLoginSession.DisposeAsync();
        _oppoLoginGate.Dispose();
        await StopOppoBleAdvertiserAsync();
        _oppoBleGate.Dispose();
        if (_app is not null) await _app.StopAsync();
        await _engine.DisposeAsync();
        _sendGate.Dispose();
        CryptographicOperations.ZeroMemory(_authTokenBytes);
    }

    private async Task ShutdownAfterResponseAsync()
    {
        await Task.Yield();
        try
        {
            _engine.CancelTransfer();
            await _engine.StopAsync();
            if (_app is not null) await _app.StopAsync();
        }
        catch (Exception ex) { Log.Error("Graceful bridge shutdown failed", ex); }
    }

    private sealed record BridgeEvent(long Seq, string Type, object Data, DateTimeOffset At);
    private sealed record PendingTransferInfo(string Id, string Name, string Identity, string MimeType, string Count, TaskCompletionSource<bool> Tcs);
    private sealed record ReceiveRequest(bool Enabled);
    private sealed record ConfirmReceiveRequest(string? Id, bool Accept);
    private sealed record SettingsRequest(
        string? SaveDirectory,
        int? ThemeMode,
        int? QuickSaveMode,
        bool? MinimizeToTray,
        bool? CloseToTray,
        string? OppoSsoid = null,
        string? OppoAccountName = null);
    private sealed record StageRequest(string[]? Files);
    private sealed record SendRequest(string? Address, bool Quiet = false, string? RequestId = null, string? TaskId = null);
    private sealed record OppoWebLoginStartRequest(string? Brand);
    private sealed record OppoBleAdvertiseStartRequest(string? Ssoid);
    private sealed record OppoVerificationRequest(string? VerMethod, string? ValidateData = null);

    /// <summary>A random 6-byte identity for the OPPO "same account" BLE scheme,
    /// generated once and persisted (SettingsStore.OppoBleDeviceId) — see
    /// OppoAccountBleAdvertiser for how it's used.</summary>
    private static string GenerateBleDeviceId()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = RandomNumberGenerator.GetBytes(6);
        var chars = new char[6];
        for (int i = 0; i < 6; i++) chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }

    /// <summary>Starts (or restarts) the same-account BLE beacon for the given ssoid,
    /// generating and persisting a device id on first use. Called both from the manual
    /// start endpoint and automatically whenever a real ssoid is configured, so no
    /// dedicated UI action is required to keep the beacon running.</summary>
    private async Task StartOppoBleAdvertiserAsync(string ssoid)
    {
        await _oppoBleGate.WaitAsync();
        try
        {
            // A startup task may still be queued when logout/account-switch runs.
            // Do not resurrect an identity that is no longer the persisted one.
            if (!string.Equals(SettingsStore.Current.OppoSsoid, ssoid, StringComparison.Ordinal))
                return;
            var deviceId = SettingsStore.Current.OppoBleDeviceId;
            if (string.IsNullOrEmpty(deviceId) || deviceId.Length != 6)
            {
                deviceId = GenerateBleDeviceId();
                SettingsStore.Save(oppoBleDeviceId: deviceId);
            }

            // Windows BLE adapters typically only host one legacy advertising set
            // at a time, and the receive-side connectable GATT advert (0x8881) is
            // deliberately kept as the permanent slot winner (see SenderEngine.cs's
            // UpdateCoordination) since phone->PC quick-save receiving depends on
            // it. This advertiser therefore usually won't get real airtime while
            // receive mode is on -- confirmed via a live nRF Connect scan showing
            // only 0x180A/0x8881 even after isolating it with nothing else running
            // (see OPPO_ACCOUNT_API_FINDINGS.md section 5k) -- but starting it
            // anyway is harmless (non-connectable, doesn't fight 8881 for GATT
            // connections) and lets it pick up the slot on devices/moments where
            // 8881 isn't actively holding it.
            _engine.FallbackAdvertiser.Pause();
            _oppoBleAdvertiser?.Dispose();
            _oppoBleAdvertiser = null;

            _oppoWindowsAdvertiser?.Dispose();
            _oppoWindowsAdvertiser = new OppoWindowsSenselessAdvertiser((byte)deviceId[0]);
            _oppoWindowsAdvertiser.Start(ssoid);

        }
        finally { _oppoBleGate.Release(); }
    }

    private async Task StopOppoBleAdvertiserAsync()
    {
        await _oppoBleGate.WaitAsync();
        try
        {
            _oppoBleAdvertiser?.Dispose();
            _oppoBleAdvertiser = null;
            _oppoWindowsAdvertiser?.Dispose();
            _oppoWindowsAdvertiser = null;
        }
        finally { _oppoBleGate.Release(); }
    }

    private static string RedactUrl(string value)
    {
        try
        {
            var uri = new Uri(value);
            return uri.GetLeftPart(UriPartial.Path);
        }
        catch
        {
            return "non-url login result";
        }
    }

    /// <summary>Called on backend startup and whenever settings are saved — starts the
    /// beacon if a real ssoid is already on file, so it survives process restarts
    /// without the user re-entering anything.</summary>
    private void StartOppoBleAdvertiserIfConfigured()
    {
        var ssoid = SettingsStore.Current.OppoSsoid;
        if (!string.IsNullOrWhiteSpace(ssoid))
            _ = StartOppoBleAdvertiserAsync(ssoid);
    }
}
