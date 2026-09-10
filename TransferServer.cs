using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CatShareSender;

/// <summary>
/// HTTPS Kestrel server the phone connects to:
///   wss://&lt;pc-ip&gt;:&lt;port&gt;/websocket         envelope handshake (versionNegotiation, sendRequest, status)
///   https://&lt;pc-ip&gt;:&lt;port&gt;/download?taskId=..  ZIP ("folder-stream") download
///   https://&lt;pc-ip&gt;:&lt;port&gt;/thumbnail?taskId=.. 404 (no thumbnails yet)
/// TLS: self-signed; the OShare client uses InsecureTrustManagerFactory and the
/// CatShare app trusts everything, so no cert prep is needed on the phone.
/// </summary>
public sealed class TransferServer : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly SemaphoreSlim _startStop = new(1, 1);

    public int Port { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>True after a stock-alliance credential write; controls the raw "files"
    /// trigger, which the CatShare app's strict parser would reject.</summary>
    public volatile bool PeerLooksStock = true;

    /// <summary>True while a phone WebSocket session is open (used by the OConnect
    /// flow to detect that the phone accepted the ip/port offer).</summary>
    public volatile bool WsConnected;

    /// <summary>The task offered on the active websocket session (null until sending).</summary>
    private volatile TransferTask? _activeTask;
    private CancellationTokenSource _cancelCts = new();
    private string? _successfulTaskId;
    private string? _reportedFailureTaskId;
    private string? _locallyCancelledTaskId;

    // A staged task is NOT remotely readable until the BLE credential flow arms it.
    // Once a WebSocket peer claims the armed task, every /download request must come
    // from the same IP. OConnect can additionally pin the expected LAN peer IP.
    private readonly object _peerGate = new();
    private string? _armedTaskId;
    private IPAddress? _allowedLocalIp;
    private IPAddress? _expectedPeerIp;
    private IPAddress? _authorizedPeerIp;

    /// <summary>Raised when the phone accepts and the ZIP download begins.</summary>
    public event Action<string>? DownloadStarted;
    public event Action<long, long>? DownloadProgress;      // (sent, total)
    public event Action<string>? DownloadFinished;          // taskId; HTTP body finished, not receiver success
    public event Action<string, int, string>? StatusReceived; // (taskId, type, reason)
    public event Action<string, string>? TransferFailed;      // (taskId, reason)

    public void SetTask(TransferTask? task)
    {
        _activeTask = task;
        DisarmTransfer(task is null ? "task cleared" : "new task staged");
    }

    public void ArmTransfer(TransferTask task, string? allowedLocalIp = null, string? expectedPeerIp = null)
    {
        if (_activeTask?.TaskId != task.TaskId)
            throw new InvalidOperationException("Cannot arm a transfer that is not the currently staged task.");

        static IPAddress? Parse(string? value) =>
            IPAddress.TryParse(value, out var ip) ? NormalizeIp(ip) : null;

        lock (_peerGate)
        {
            _armedTaskId = task.TaskId;
            _allowedLocalIp = Parse(allowedLocalIp);
            _expectedPeerIp = Parse(expectedPeerIp);
            _authorizedPeerIp = null;
            _successfulTaskId = null;
            _reportedFailureTaskId = null;
            _locallyCancelledTaskId = null;
        }
        Log.Info($"TransferServer: armed task {task.TaskId}; local={_allowedLocalIp?.ToString() ?? "any"}; expectedPeer={_expectedPeerIp?.ToString() ?? "first-valid-peer"}");
    }

    internal void AuthorizeLoopbackTest(TransferTask task)
    {
        ArmTransfer(task, IPAddress.Loopback.ToString(), IPAddress.Loopback.ToString());
        lock (_peerGate)
            _authorizedPeerIp = IPAddress.Loopback;
        Log.Info($"TransferServer: loopback test authorization enabled for task {task.TaskId}");
    }

    public void DisarmTransfer(string reason = "disarmed")
    {
        lock (_peerGate)
        {
            _armedTaskId = null;
            _allowedLocalIp = null;
            _expectedPeerIp = null;
            _authorizedPeerIp = null;
        }
        WsConnected = false;
        Log.Info($"TransferServer: {reason}");
    }

    public void CancelActiveTransfer()
    {
        lock (_peerGate)
            _locallyCancelledTaskId = _activeTask?.TaskId;
        _cancelCts.Cancel();
        _cancelCts.Dispose();
        _cancelCts = new CancellationTokenSource();
        DisarmTransfer("active transfer cancelled");
        Log.Info("TransferServer: active transfer cancellation requested");
    }

    private static IPAddress? NormalizeIp(IPAddress? ip) =>
        ip?.IsIPv4MappedToIPv6 == true ? ip.MapToIPv4() : ip;

    private bool IsSuccessful(string taskId)
    {
        lock (_peerGate) return _successfulTaskId == taskId;
    }

    private bool IsLocalCancellation(string taskId)
    {
        lock (_peerGate) return _locallyCancelledTaskId == taskId;
    }

    private void MarkSuccessful(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) taskId = _activeTask?.TaskId ?? "";
        if (string.IsNullOrWhiteSpace(taskId)) return;
        lock (_peerGate)
        {
            _successfulTaskId = taskId;
            _reportedFailureTaskId = null;
        }
    }

    private void ReportTransferFailure(string taskId, string reason)
    {
        if (string.IsNullOrWhiteSpace(taskId)) taskId = _activeTask?.TaskId ?? "";
        if (string.IsNullOrWhiteSpace(taskId)) return;
        lock (_peerGate)
        {
            if (_successfulTaskId == taskId || _locallyCancelledTaskId == taskId || _reportedFailureTaskId == taskId)
                return;
            _reportedFailureTaskId = taskId;
        }
        Log.Warn($"TransferServer: remote transfer failed task={taskId}: {reason}");
        try { TransferFailed?.Invoke(taskId, reason); } catch { }
    }

    private bool TryClaimWebSocketPeer(HttpContext ctx, TransferTask task)
    {
        var remote = NormalizeIp(ctx.Connection.RemoteIpAddress);
        var local = NormalizeIp(ctx.Connection.LocalIpAddress);
        if (remote is null || local is null) return false;

        lock (_peerGate)
        {
            if (_armedTaskId != task.TaskId) return false;
            if (_allowedLocalIp is not null && !_allowedLocalIp.Equals(local)) return false;
            if (_expectedPeerIp is not null && !_expectedPeerIp.Equals(remote)) return false;

            if (_authorizedPeerIp is null)
            {
                _authorizedPeerIp = remote;
                Log.Info($"TransferServer: task {task.TaskId} claimed by peer {remote} via local {local}");
                return true;
            }
            return _authorizedPeerIp.Equals(remote);
        }
    }

    private bool IsAuthorizedDownloadPeer(HttpContext ctx, TransferTask task)
    {
        var remote = NormalizeIp(ctx.Connection.RemoteIpAddress);
        var local = NormalizeIp(ctx.Connection.LocalIpAddress);
        if (remote is null || local is null) return false;

        lock (_peerGate)
        {
            return _armedTaskId == task.TaskId &&
                   _authorizedPeerIp is not null && _authorizedPeerIp.Equals(remote) &&
                   (_allowedLocalIp is null || _allowedLocalIp.Equals(local)) &&
                   (_expectedPeerIp is null || _expectedPeerIp.Equals(remote));
        }
    }

    private void ReleaseUncommittedPeer(IPAddress? peer)
    {
        peer = NormalizeIp(peer);
        if (peer is null) return;
        lock (_peerGate)
        {
            if (_authorizedPeerIp is not null && _authorizedPeerIp.Equals(peer))
                _authorizedPeerIp = null;
        }
    }

    public async Task StartAsync(int port, bool configureFirewall = true)
    {
        await _startStop.WaitAsync();
        try
        {
            if (IsRunning) return;
            Port = port;

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new ForwardingLoggerProvider());
            builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
            {
                o.AddServerHeader = false;
                // ZipArchive writes the response body synchronously; async-only mode would
                // abort mid-transfer, so allow sync IO (single transfer at a time anyway).
                o.AllowSynchronousIO = true;
                // explicit IPv4 + IPv6 bindings: ListenAnyIP ended up IPv6-only here.
                // PLAINTEXT on purpose: the pad connects ws:// (no TLS) when the sender's
                // state-1 version >= 10015 (d8/v n(): O=false -> b9.a ssl flag false).
                // TLS here would close the pad's plaintext upgrade mid-handshake (208).
                o.Listen(System.Net.IPAddress.Any, port);
                o.Listen(System.Net.IPAddress.IPv6Any, port);
            });
            // no ASP.NET routing features needed; keep it lean
            builder.Services.AddRouting();

            var app = builder.Build();
            app.UseWebSockets();

            app.Map("/websocket", HandleWebSocket);
            app.MapGet("/download", HandleDownload);
            app.MapGet("/thumbnail", async ctx =>
            {
                Log.Info("HTTP GET /thumbnail (not supported, empty)");
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await ctx.Response.CompleteAsync();
            });

            _app = app;
            await app.StartAsync();
            IsRunning = true;
            if (configureFirewall)
                TryAddFirewallRule(port);
            else
                Log.Info("TransferServer: firewall setup skipped for isolated test server");
            Log.Info($"TransferServer listening on http://0.0.0.0:{port} (ws /websocket, /download)");
        }
        finally
        {
            _startStop.Release();
        }
    }

    public async Task StopAsync()
    {
        await _startStop.WaitAsync();
        try
        {
            if (_app is not null)
            {
                try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
                await _app.DisposeAsync();
                _app = null;
            }
            IsRunning = false;
            Log.Info("TransferServer stopped");
        }
        finally
        {
            _startStop.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _startStop.Dispose();
    }

    // ---------------------------------------------------------------- websocket

    /// <summary>Keep transfer traffic on the local subnet only. Existing broad rules
    /// from older builds are tightened in-place instead of silently re-used.</summary>
    private static void TryAddFirewallRule(int port)
    {
        EnsureFirewallRule(
            "CatShareSender",
            $"advfirewall firewall add rule name=\"CatShareSender\" dir=in action=allow protocol=TCP localport={port} remoteip=localsubnet",
            $"advfirewall firewall set rule name=\"CatShareSender\" new protocol=TCP localport={port} remoteip=localsubnet",
            $"the phone will NOT be able to connect — allow TCP {port} from LocalSubnet in Windows Firewall");

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
            EnsureFirewallRule(
                "CatShareSenderBandEcho",
                $"advfirewall firewall add rule name=\"CatShareSenderBandEcho\" dir=in action=allow program=\"{exe}\" protocol=UDP remoteip=localsubnet",
                $"advfirewall firewall set rule name=\"CatShareSenderBandEcho\" new program=\"{exe}\" protocol=UDP remoteip=localsubnet",
                "the phone's LAN check times out and it falls back to hotspot mode");
    }

    private static void EnsureFirewallRule(string ruleName, string addArgs, string updateArgs, string failureHint)
    {
        var (_, existing) = RunNetsh($"advfirewall firewall show rule name=\"{ruleName}\"", elevate: false);
        var exists = existing.Contains(ruleName, StringComparison.OrdinalIgnoreCase);
        var args = exists ? updateArgs : addArgs;

        var (ok, output) = RunNetsh(args, elevate: false, ruleName);
        if (!ok)
            (ok, output) = RunNetsh(args, elevate: true, ruleName);
        if (ok)
            Log.Info($"firewall rule '{ruleName}': {(exists ? "tightened" : "created")} (LocalSubnet only)");
        else
            Log.Warn($"firewall rule '{ruleName}' could not be secured — {failureHint} ({output.Trim()})");
    }

    private static (bool ok, string output) RunNetsh(string args, bool elevate, string? verifyRuleName = null)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                UseShellExecute = elevate,
                CreateNoWindow = true,
            };
            if (elevate)
                psi.Verb = "runas";
            else
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
            }

            using var p = System.Diagnostics.Process.Start(psi)!;
            string output = "";
            if (!elevate)
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                if (!p.WaitForExit(5000)) return (false, "netsh timed out");
                return (p.ExitCode == 0, output);
            }

            if (!p.WaitForExit(30000)) return (false, "elevated netsh timed out");
            if (p.ExitCode != 0) return (false, $"elevated netsh exited {p.ExitCode}");
            var name = verifyRuleName ?? "CatShareSender";
            var (_, chkOut) = RunNetsh($"advfirewall firewall show rule name=\"{name}\"", elevate: false);
            return (chkOut.Contains(name, StringComparison.OrdinalIgnoreCase), "updated via UAC prompt");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task HandleWebSocket(HttpContext ctx)
    {
        var peerIp = NormalizeIp(ctx.Connection.RemoteIpAddress);
        var peer = peerIp?.ToString() ?? "?";
        Log.Info($"WS: connection attempt from {peer} via local {ctx.Connection.LocalIpAddress}");

        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        var task = _activeTask;
        if (task is null || !TryClaimWebSocketPeer(ctx, task))
        {
            Log.Warn($"WS: rejected unauthorized/unarmed peer {peer}");
            ctx.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            await ctx.Response.WriteAsync("transfer is not armed for this peer");
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var session = new WsSession(this, ws)
        {
            PeerLooksStock = PeerLooksStock
        };
        int seq = 1;
        var committed = false;

        try
        {
            // SENDER initiates versionNegotiation (decompiled j9/l.java:138).
            // CatShare receiver accepts {"version":n} and replies {"version":min(n,1),"threadLimit":5}.
            await session.SendText(Envelope.Build("action", seq++, "versionNegotiation", new { version = 2 }));
            var vnAck = await session.ReceiveUntilAsync(e => e.IsAck && e.Method == "versionNegotiation", TimeSpan.FromSeconds(10));
            if (vnAck is null) { Log.Warn("WS: no versionNegotiation ack within 10s"); return; }
            Log.Info($"WS: version ack: {vnAck}");

            // Use the exact task that was armed when this peer claimed the session.
            if (_activeTask?.TaskId != task.TaskId)
            {
                Log.Warn("WS: staged task changed during handshake; closing");
                return;
            }

            var sendReq = Envelope.Build("action", seq++, "sendRequest", task.BuildSendRequest());
            Log.Info($"WS: -> {sendReq}");
            await session.SendText(sendReq);
            var srAck = await session.ReceiveUntilAsync(e => e.IsAck && e.Method == "sendRequest", TimeSpan.FromSeconds(60));
            if (srAck is null) { Log.Warn("WS: no sendRequest ack within 60s (phone user may not have accepted)"); return; }
            Log.Info($"WS: sendRequest ack: {srAck}");
            committed = true;
            WsConnected = true;

            // The stock receiver starts downloading when it receives the bare
            // string "files" (j9/g.java:898). The CatShare receiver parses every
            // text frame strictly, so we only send it to stock peers.
            if (session.PeerLooksStock)
            {
                await session.SendText("files");
                Log.Info("WS: sent raw trigger 'files'");
            }
            else
            {
                Log.Info("WS: CatShare-style peer, skipping raw 'files' trigger");
            }

            // Wait for the download + final status.
            var done = await session.ReceiveLoopAsync(TimeSpan.FromMinutes(30));
            Log.Info($"WS: session ended ({done})");
            if (committed && done != "transfer completed" && !IsSuccessful(task.TaskId))
                ReportTransferFailure(task.TaskId, "Phone cancelled or disconnected before confirming receipt.");
        }
        catch (Exception ex)
        {
            if (committed && !IsSuccessful(task.TaskId))
            {
                ReportTransferFailure(task.TaskId,
                    ex is System.Net.WebSockets.WebSocketException
                        ? "Phone cancelled the transfer or disconnected."
                        : $"Transfer connection failed: {ex.Message}");
                Log.Warn($"WS: committed session ended before success confirmation: {ex.Message}");
            }
            else
            {
                Log.Error("WS: session error", ex);
            }
        }
        finally
        {
            WsConnected = false;
            if (!committed) ReleaseUncommittedPeer(peerIp);
        }
    }

    private sealed class WsSession
    {
        private readonly System.Net.WebSockets.WebSocket _ws;
        private readonly TransferServer _owner;
        public bool PeerLooksStock = true;   // refine later if needed

        public WsSession(TransferServer owner, System.Net.WebSockets.WebSocket ws)
        {
            _owner = owner;
            _ws = ws;
        }

        public async Task SendText(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _ws.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task<Envelope?> ReceiveUntilAsync(Func<Envelope, bool> predicate, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                var (e, closed) = await ReadFrameAsync(cts.Token);
                if (closed) return null;
                if (e is null) continue;
                if (predicate(e)) return e;
                Log.Info($"WS: (skip) {e}");
            }
        }

        /// <summary>Pumps frames, acking status actions, until the peer closes or the
        /// download completes. Returns a human reason.</summary>
        public async Task<string> ReceiveLoopAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                var (e, closed) = await ReadFrameAsync(cts.Token);
                if (closed) return "peer closed";
                if (e is null) continue;

                if (e.IsAction && (e.Method == "status" || e.Method == "stat"))
                {
                    // OnePlus phone-side Cancel sends action:*:stat and waits for
                    // ack:*:stat before closing with mCanceled=true.
                    var type = e.PayloadInt("type", int.MinValue);
                    if (type == int.MinValue) type = e.PayloadInt("status", int.MinValue);
                    var reason = e.PayloadString("reason");
                    if (string.IsNullOrWhiteSpace(reason)) reason = e.PayloadString("message");
                    var taskId = e.PayloadString("taskId");
                    if (string.IsNullOrWhiteSpace(taskId)) taskId = e.PayloadString("id");
                    Log.Info($"WS: {e.Method} type={(type == int.MinValue ? "?" : type)} reason='{reason}'");
                    await SendText(Envelope.Build("ack", e.Seq, e.Method, new { }));

                    if (e.Method == "stat")
                    {
                        if (type == 1)
                        {
                            _owner.OnStatus(taskId, 1, reason);
                            return "transfer completed";
                        }
                        var why = string.IsNullOrWhiteSpace(reason) ? "Phone cancelled the transfer." : reason;
                        _owner.ReportTransferFailure(taskId, why);
                        return $"phone cancelled: {why}";
                    }

                    _owner.OnStatus(taskId, type, reason);
                    if (type == 1) return "transfer completed";
                    if (type == 2) return string.IsNullOrWhiteSpace(reason) ? "transfer failed on phone" : $"transfer failed: {reason}";
                    if (type == 3) return string.IsNullOrWhiteSpace(reason) ? "phone refused transfer" : $"refused: {reason}";
                }
                else if (e.IsAction)
                {
                    await SendText(Envelope.Build("ack", e.Seq, e.Method, new { }));
                }
                else
                {
                    Log.Info($"WS: raw frame '{e.Method}'");
                }
            }
        }

        private async Task<(Envelope?, bool)> ReadFrameAsync(CancellationToken ct)
        {
            var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (true)
                {
                    var result = await _ws.ReceiveAsync(buf, ct);
                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                        return (null, true);
                    var text = Encoding.UTF8.GetString(buf, 0, result.Count);
                    if (!result.EndOfMessage)
                    {
                        // large frames (unlikely for control messages) — accumulate
                        using var ms = new MemoryStream();
                        ms.Write(buf, 0, result.Count);
                        while (!result.EndOfMessage)
                        {
                            result = await _ws.ReceiveAsync(buf, ct);
                            ms.Write(buf, 0, result.Count);
                        }
                        text = Encoding.UTF8.GetString(ms.ToArray());
                    }
                    var env = Envelope.Parse(text);
                    if (env is { IsRaw: true }) return (env, false);
                    if (env is not null) return (env, false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
    }

    private void OnStatus(string taskId, int type, string reason)
    {
        if (string.IsNullOrWhiteSpace(taskId)) taskId = _activeTask?.TaskId ?? taskId;
        if (type == 1)
            MarkSuccessful(taskId);
        else if (type == 2)
            ReportTransferFailure(taskId, string.IsNullOrWhiteSpace(reason) ? "Phone cancelled or failed the transfer." : reason);
        else if (type == 3)
            ReportTransferFailure(taskId, string.IsNullOrWhiteSpace(reason) ? "Phone refused the transfer." : reason);

        try { StatusReceived?.Invoke(taskId, type, reason); } catch { }
        if (type is 1 or 2 or 3)
            DisarmTransfer($"terminal status {type}: {reason}");
    }

    /// <summary>Routes ASP.NET/Kestrel internal logs (TLS failures etc.) into our log.</summary>
    private sealed class ForwardingLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string category) => new ForwardingLogger(category);
        public void Dispose() { }

        private sealed class ForwardingLogger(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var msg = $"[kestrel:{category.Split('.').LastOrDefault()}] {formatter(state, exception)}";
                if (exception is not null) msg += $" :: {exception.Message}";
                switch (logLevel)
                {
                    case LogLevel.Error or LogLevel.Critical: CatShareSender.Log.Error(msg); break;
                    case LogLevel.Warning: CatShareSender.Log.Warn(msg); break;
                    default: CatShareSender.Log.Info(msg); break;
                }
            }
        }
    }

    // ---------------------------------------------------------------- download

    private async Task HandleDownload(HttpContext ctx)
    {
        var taskId = ctx.Request.Query["taskId"].FirstOrDefault() ?? "";
        var fileId = ctx.Request.Query["fileId"].FirstOrDefault();
        var ndZip = ctx.Request.Headers["nd_zip"].FirstOrDefault();
        Log.Info($"HTTP GET /download taskId='{taskId}' fileId='{fileId}' nd_zip='{ndZip}' from {ctx.Connection.RemoteIpAddress}");

        var task = _activeTask;
        if (task is null || string.IsNullOrEmpty(taskId) || task.TaskId != taskId)
        {
            Log.Warn("HTTP: unknown or stale taskId");
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await ctx.Response.WriteAsync("unknown task");
            return;
        }
        if (!IsAuthorizedDownloadPeer(ctx, task))
        {
            Log.Warn($"HTTP: rejected /download for task {task.TaskId} from unauthorized peer {ctx.Connection.RemoteIpAddress}");
            ctx.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            await ctx.Response.WriteAsync("peer not authorized for this transfer");
            return;
        }

        try { DownloadStarted?.Invoke(task.TaskId); } catch { }

        if (fileId is not null)
        {
            // An explicit fileId must be valid: falling through to the whole-batch
            // zip here would hand a single-file receiver the entire batch.
            if (!int.TryParse(fileId, out var idx) || idx < 0 || idx >= task.Files.Count)
            {
                Log.Warn($"HTTP: fileId '{fileId}' out of range (0..{task.Files.Count - 1})");
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await ctx.Response.WriteAsync("unknown fileId");
                return;
            }
            // per-file mode. The pad's iOS receiver zip-iterates EVERY /download body
            // (iOSReceiveFileManager "create file:" per entry, basename flattened), so
            // raw bytes are fatal: a raw pptx gets exploded into its internal entries.
            // Always answer with a zip whose entries are the transfer files (stock
            // sender does the same for nd_zip != false, aa/b.java -> ga.c).
            var single = new List<string> { task.Files[idx] };
            try
            {
                await WriteDownloadZip(
                    ctx,
                    single,
                    new FileInfo(single[0]).Length,
                    Path.GetFileName(single[0]),
                    _cancelCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (ctx.RequestAborted.IsCancellationRequested && !IsLocalCancellation(task.TaskId))
                    ReportTransferFailure(task.TaskId, "Phone cancelled the download.");
                return;
            }
            catch (IOException ex) when (ctx.RequestAborted.IsCancellationRequested)
            {
                ReportTransferFailure(task.TaskId, $"Phone disconnected during download: {ex.Message}");
                return;
            }
            Log.Info($"HTTP: per-file zip done: {single[0]}");
            try { DownloadFinished?.Invoke(task.TaskId); } catch { }
            return;
        }

        // whole-batch mode — stock OnePlus peers use the official STORED ZIP path;
        // custom/CatShare peers retain the legacy folder-stream compatibility path.
        try
        {
            await WriteDownloadZip(ctx, task.Files, task.TotalSize, task.FirstFileName, _cancelCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (ctx.RequestAborted.IsCancellationRequested && !IsLocalCancellation(task.TaskId))
                ReportTransferFailure(task.TaskId, "Phone cancelled the download.");
            return;
        }
        catch (IOException ex) when (ctx.RequestAborted.IsCancellationRequested)
        {
            ReportTransferFailure(task.TaskId, $"Phone disconnected during download: {ex.Message}");
            return;
        }
        try { DownloadFinished?.Invoke(task.TaskId); } catch { }
    }

    /// <summary>
    /// Stock OnePlus/OShare peers use the APK's iOSFileResponse/FileChunkedInput
    /// format: application/zip, chunked HTTP, direct filenames, STORED entries with
    /// CRC/size known before the local header, and 1 MiB data chunks. The legacy
    /// folder-stream path remains only for the custom CatShare-compatible peer.
    /// </summary>
    private async Task WriteDownloadZip(
        HttpContext ctx,
        IReadOnlyList<string> files,
        long total,
        string legacyDispositionName,
        CancellationToken cancelToken)
    {
        ctx.Response.StatusCode = (int)HttpStatusCode.OK;
        ctx.Response.ContentType = "application/zip";

        if (PeerLooksStock)
        {
            ctx.Response.Headers.Remove("nd_zip");
            ctx.Response.Headers.Remove("Oshare-Transfer-Type");
            ctx.Response.Headers["Content-Disposition"] = "attachment; filename=\"files.zip\"";
            Log.Info($"HTTP: OnePlus-compatible STORED ZIP response, files={files.Count}, payload={total}");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, cancelToken);
            // Commit the HTTP 200 + headers immediately. STORED ZIP needs a CRC
            // before its local header, but the phone must not sit waiting for the
            // first body byte and mistake preparation time for a dead server.
            await ctx.Response.StartAsync(linked.Token);
            Log.Info("HTTP: response headers committed; waiting for prepared CRC if needed");
            await OfficialStoredZipWriter.WriteAsync(
                ctx.Response.Body,
                files,
                total,
                (sent, expected) =>
                {
                    try { DownloadProgress?.Invoke(sent, expected); } catch { }
                },
                linked.Token);
            return;
        }

        ctx.Response.Headers["nd_zip"] = "true";
        ctx.Response.Headers["Oshare-Transfer-Type"] = "folder-stream";
        ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{Uri.EscapeDataString(legacyDispositionName)}\"";
        await WriteFolderStreamZip(ctx, files, total, cancelToken);
    }

    /// <summary>Legacy CatShare compatibility ZIP (entries under 0/). Stock OnePlus
    /// peers do not use this path.</summary>
    private async Task WriteFolderStreamZip(HttpContext ctx, IReadOnlyList<string> files, long total, CancellationToken cancelToken)
    {
        long sent = 0;
        await using (var zip = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entryName = "0/" + SanitizeEntryName(Path.GetFileName(file));
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                // MUST be Deflate, NOT NoCompression/STORED: streaming entries are written
                // with bit-3 data descriptors and a ZERO size in the local header. Java's
                // ZipInputStream (the pad's iOSReceiveFileManager) trusts the local-header
                // size for STORED entries -> reads 0 bytes -> the file lands 0-byte and the
                // rest of the stream (the stored file's own bytes) gets parsed as phantom
                // zip entries. Deflate entries are length-self-terminating via Inflater.
                // ZIP timestamps only support 1980..2107 — files with invalid/older
                // timestamps (placeholders, some system files) threw and aborted the
                // whole transfer, so clamp them.
                var mtime = File.GetLastWriteTime(file);
                if (mtime.Year < 1980 || mtime.Year > 2107) mtime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Local);
                entry.LastWriteTime = mtime;
                try
                {
                    await using var es = entry.Open();
                    await using var fs = File.OpenRead(file);
                    var buf = new byte[256 * 1024];
                    int n;
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, cancelToken);
                    while ((n = await fs.ReadAsync(buf, linked.Token)) > 0)
                    {
                        await es.WriteAsync(buf.AsMemory(0, n), linked.Token);
                        sent += n;
                        try { DownloadProgress?.Invoke(sent, total); } catch { }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // e.g. reading the RUNNING exe can hit a sharing violation — without
                    // this log the transfer just dies silently mid-stream.
                    Log.Error($"HTTP: failed streaming '{file}': {ex.Message}");
                    throw;
                }
            }
        }
        Log.Info($"HTTP: ZIP stream complete, {sent}/{total} bytes, {files.Count} files");
    }

    private static string SanitizeEntryName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }
}

