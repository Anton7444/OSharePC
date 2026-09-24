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
    private WebApplication? _app;
    private string _lastState = "starting";
    private Task? _sendTask;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _shutdownRequested;
    private int _sendQuiet;
    private string? _sendRequestId;

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
                closeToTray: body.CloseToTray);
            return Results.Ok(new
            {
                deviceName = _engine.Advertiser.DeviceName,
                saveDirectory = _engine.Receiver.SaveDirectory,
                themeMode = SettingsStore.Current.ThemeMode,
                quickSaveMode = SettingsStore.Current.QuickSaveMode,
                minimizeToTray = SettingsStore.Current.MinimizeToTray,
                closeToTray = SettingsStore.Current.CloseToTray,
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
            if (!await _sendGate.WaitAsync(0)) return Reject(StatusCodes.Status409Conflict, "A transfer is already running.");
            try
            {
                if (_sendTask is { IsCompleted: false }) return Reject(StatusCodes.Status409Conflict, "A transfer is already running.");
                Interlocked.Exchange(ref _sendRequestId, requestId);
                Interlocked.Exchange(ref _sendQuiet, body.Quiet ? 1 : 0);
                Push("sendStarted", new { device = device.Name, quiet, requestId });
                _sendTask = _engine.SendToAsync(device);
            }
            finally { _sendGate.Release(); }
            _ = _sendTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var error = t.Exception?.GetBaseException().Message ?? "send failed";
                    Log.Warn($"send failed: {error}");
                    Push("sendFailed", new { error, quiet, requestId });
                    Interlocked.CompareExchange(ref _sendRequestId, null, requestId);
                    Interlocked.Exchange(ref _sendQuiet, 0);
                }
            });
            return Results.Accepted(value: new { device = device.Name, address = device.Address, requestId });
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

    private void Push(string type, object data)
    {
        var s = Interlocked.Increment(ref _seq);
        _events.Enqueue(new BridgeEvent(s, type, data, DateTimeOffset.UtcNow));
        while (_events.Count > 100) _events.TryDequeue(out _);
    }

    public async ValueTask DisposeAsync()
    {
        ClearPendingTransfers();
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
        bool? CloseToTray);
    private sealed record StageRequest(string[]? Files);
    private sealed record SendRequest(string? Address, bool Quiet = false, string? RequestId = null);
}
