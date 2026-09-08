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
/// HTTP/WebSocket server the phone connects to:
///   ws://&lt;pc-ip&gt;:&lt;port&gt;/websocket              envelope handshake
///   http://&lt;pc-ip&gt;:&lt;port&gt;/download?taskId=..   ZIP ("folder-stream") download
///   http://&lt;pc-ip&gt;:&lt;port&gt;/bigmessage?...        TaskInfo/message payload for URL sharing
/// TLS remains supported by receivers that negotiate the wss/https variant.
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

    /// <summary>Raised when the phone accepts and the ZIP download begins.</summary>
    public event Action<string>? DownloadStarted;
    public event Action<long, long>? DownloadProgress;      // (sent, total)
    public event Action<string>? DownloadFinished;          // taskId
    public event Action<string>? UrlFinished;               // taskId
    public event Action<string, int, string>? StatusReceived; // (taskId, type, reason)

    public void SetTask(TransferTask? task) => _activeTask = task;

    public void CancelActiveTransfer()
    {
        _cancelCts.Cancel();
        _cancelCts.Dispose();
        _cancelCts = new CancellationTokenSource();
        Log.Info("TransferServer: active transfer cancellation requested");
    }

    public async Task StartAsync(int port)
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
                o.AllowSynchronousIO = true;
                o.Listen(System.Net.IPAddress.Any, port);
                o.Listen(System.Net.IPAddress.IPv6Any, port);
            });
            builder.Services.AddRouting();

            var app = builder.Build();
            app.UseWebSockets();

            app.Map("/websocket", HandleWebSocket);
            app.MapGet("/download", HandleDownload);
            app.MapGet("/bigmessage", HandleBigMessage);
            app.MapGet("/thumbnail", async ctx =>
            {
                Log.Info("HTTP GET /thumbnail (not supported, empty)");
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await ctx.Response.CompleteAsync();
            });

            _app = app;
            await app.StartAsync();
            IsRunning = true;
            TryAddFirewallRule(port);
            Log.Info($"TransferServer listening on http://0.0.0.0:{port} (ws /websocket, /download, /bigmessage)");
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

    private static void TryAddFirewallRule(int port)
    {
        AddFirewallRule("CatShareSender",
            $"advfirewall firewall add rule name=\"CatShareSender\" dir=in action=allow protocol=TCP localport={port}",
            $"the phone will NOT be able to connect — allow TCP {port} in Windows Firewall (run once as administrator)");

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
            AddFirewallRule("CatShareSenderBandEcho",
                $"advfirewall firewall add rule name=\"CatShareSenderBandEcho\" dir=in action=allow program=\"{exe}\" protocol=UDP remoteip=localsubnet",
                "the phone's LAN check times out and it falls back to hotspot mode");
    }

    private static void AddFirewallRule(string ruleName, string args, string failureHint)
    {
        var (_, existing) = RunNetsh($"advfirewall firewall show rule name=\"{ruleName}\"", elevate: false);
        if (existing.Contains(ruleName, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info($"firewall rule '{ruleName}': reused existing rule");
            return;
        }

        var (ok, output) = RunNetsh(args, elevate: false, ruleName);
        if (!ok)
            (ok, output) = RunNetsh(args, elevate: true, ruleName);
        if (ok)
            Log.Info($"firewall rule '{ruleName}': created");
        else
            Log.Warn($"firewall rule '{ruleName}' missing — {failureHint} ({output.Trim()})");
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
            if (!elevate)
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
            }
            using var p = System.Diagnostics.Process.Start(psi)!;
            string output = "";
            if (!elevate)
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(5000);
                return (output.Contains("Ok") || output.Contains("确定") || output.Contains("OK"), output);
            }
            p.WaitForExit(30000);
            var name = verifyRuleName ?? "CatShareSender";
            var (chk, chkOut) = RunNetsh($"advfirewall firewall show rule name=\"{name}\"", elevate: false);
            return (chk && chkOut.Contains(name), "added via UAC prompt");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task HandleWebSocket(HttpContext ctx)
    {
        var peer = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        Log.Info($"WS: phone connected from {peer}");

        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        WsConnected = true;
        var session = new WsSession(this, ws)
        {
            PeerLooksStock = PeerLooksStock
        };
        int seq = 1;

        try
        {
            await session.SendText(Envelope.Build("action", seq++, "versionNegotiation", new { version = 2 }));
            var vnAck = await session.ReceiveUntilAsync(e => e.IsAck && e.Method == "versionNegotiation", TimeSpan.FromSeconds(10));
            if (vnAck is null) { Log.Warn("WS: no versionNegotiation ack within 10s"); return; }
            Log.Info($"WS: version ack: {vnAck}");

            var task = _activeTask;
            if (task is null)
            {
                Log.Warn("WS: connected but no task staged; closing");
                return;
            }

            object sendPayload;
            if (task.IsUrl)
            {
                var urlPayload = task.BuildUrlBigMessage();
                urlPayload["isMessageSend"] = "true";
                urlPayload["messageId"] = task.MessageId;
                sendPayload = urlPayload;
            }
            else
            {
                sendPayload = task.BuildSendRequest();
            }

            var sendReq = Envelope.Build("action", seq++, "sendRequest", sendPayload);
            Log.Info($"WS: -> {sendReq}");
            await session.SendText(sendReq);
            var srAck = await session.ReceiveUntilAsync(e => e.IsAck && e.Method == "sendRequest", TimeSpan.FromSeconds(60));
            if (srAck is null) { Log.Warn("WS: no sendRequest ack within 60s (phone user may not have accepted)"); return; }
            Log.Info($"WS: sendRequest ack: {srAck}");

            if (!task.IsUrl && session.PeerLooksStock)
            {
                await session.SendText("files");
                Log.Info("WS: sent raw trigger 'files'");
            }
            else if (task.IsUrl)
            {
                Log.Info("WS: URL metadata sent inline; stock /download compatibility returns an empty message-only ZIP; /bigmessage remains available");
            }
            else
            {
                Log.Info("WS: CatShare-style peer, skipping raw 'files' trigger");
            }

            var done = await session.ReceiveLoopAsync(TimeSpan.FromMinutes(30));
            Log.Info($"WS: session ended ({done})");
            if (task.IsUrl && done == "transfer completed")
            {
                task.Complete = true;
                try { UrlFinished?.Invoke(task.TaskId); } catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Error("WS: session error", ex);
        }
        finally
        {
            WsConnected = false;
        }
    }

    private sealed class WsSession
    {
        private readonly System.Net.WebSockets.WebSocket _ws;
        private readonly TransferServer _owner;
        public bool PeerLooksStock = true;

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

        public async Task<string> ReceiveLoopAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                var (e, closed) = await ReadFrameAsync(cts.Token);
                if (closed) return "peer closed";
                if (e is null) continue;

                if (e.IsAction && e.Method == "status")
                {
                    var type = e.PayloadInt("type");
                    var reason = e.PayloadString("reason");
                    var taskId = e.PayloadString("taskId");
                    Log.Info($"WS: status type={type} reason='{reason}'");
                    _owner.OnStatus(taskId, type, reason);
                    await SendText(Envelope.Build("ack", e.Seq, "status", new { }));
                    if (type == 1 && (string.IsNullOrEmpty(reason) || reason == "ok")) return "transfer completed";
                    if (type == 3) return $"refused: {reason}";
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
        try { StatusReceived?.Invoke(taskId, type, reason); } catch { }
    }

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

    // ---------------------------------------------------------------- HTTP payloads

    private static Dictionary<string, object> BuildUrlHttpPayload(TransferTask task)
    {
        var payload = task.BuildUrlBigMessage();
        payload["isMessageSend"] = "true";
        payload["messageId"] = task.MessageId;
        return payload;
    }

    private async Task WriteUrlMetadataAsync(HttpContext ctx, TransferTask task)
    {
        var json = JsonSerializer.Serialize(BuildUrlHttpPayload(task));
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = (int)HttpStatusCode.OK;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength = bytes.Length;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
        await ctx.Response.CompleteAsync();
        Log.Info($"HTTP: URL big-message metadata served for taskId={task.TaskId}, {bytes.Length} bytes");
    }

    private async Task WriteUrlDownloadZipAsync(HttpContext ctx, TransferTask task)
    {
        // ColorOS' iOS-emulation transport still performs /download for a URL/message
        // task and feeds the body to ZipInputStream. Do not put the URL in a fake
        // *.url entry: doing that makes the OS save a real file and permanently
        // downgrades the receive result to File Manager. The actual URL travels in
        // the http/* TaskInfo/shareText metadata. This zero-entry ZIP only satisfies
        // the transport reader while creating no filesystem object on the phone.
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) { }

        var body = ms.ToArray();
        ctx.Response.StatusCode = (int)HttpStatusCode.OK;
        ctx.Response.ContentType = "application/zip";
        ctx.Response.ContentLength = body.Length;
        ctx.Response.Headers["nd_zip"] = "true";
        ctx.Response.Headers["Oshare-Transfer-Type"] = "folder-stream";
        ctx.Response.Headers["Content-Disposition"] = "attachment; filename=\"message.zip\"";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
        await ctx.Response.CompleteAsync();
        Log.Info($"HTTP: URL message-only empty ZIP served for taskId={task.TaskId}, entries=0, zip={body.Length} bytes");
    }

    private async Task HandleBigMessage(HttpContext ctx)
    {
        var taskId = ctx.Request.Query["taskId"].FirstOrDefault() ?? "";
        var messageId = ctx.Request.Query["messageId"].FirstOrDefault() ?? "";
        var task = _activeTask;
        Log.Info($"HTTP GET /bigmessage taskId='{taskId}' messageId='{messageId}' from {ctx.Connection.RemoteIpAddress}");

        if (task is null || !task.IsUrl || task.TaskId != taskId || task.MessageId != messageId)
        {
            Log.Warn("HTTP: unknown or stale URL big-message request");
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await ctx.Response.WriteAsync("unknown message");
            return;
        }

        await WriteUrlMetadataAsync(ctx, task);
    }

    private async Task HandleDownload(HttpContext ctx)
    {
        var taskId = ctx.Request.Query["taskId"].FirstOrDefault() ?? "";
        var fileId = ctx.Request.Query["fileId"].FirstOrDefault();
        var ndZip = ctx.Request.Headers["nd_zip"].FirstOrDefault();
        Log.Info($"HTTP GET /download taskId='{taskId}' fileId='{fileId}' nd_zip='{ndZip}' from {ctx.Connection.RemoteIpAddress}");

        var task = _activeTask;
        if (task is null)
        {
            Log.Warn("HTTP: no active task");
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await ctx.Response.WriteAsync("unknown task");
            return;
        }

        if (task.IsUrl)
        {
            // New builds should parse the duplicated "id" field and send the real
            // task id. Retain empty-id acceptance for the observed ColorOS parser so
            // older test packages still reach a valid message-only response.
            if (!string.IsNullOrEmpty(taskId) && task.TaskId != taskId)
            {
                Log.Warn($"HTTP: URL /download taskId mismatch ('{taskId}')");
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await ctx.Response.WriteAsync("unknown task");
                return;
            }

            await WriteUrlDownloadZipAsync(ctx, task);
            return;
        }

        if (string.IsNullOrEmpty(taskId) || task.TaskId != taskId)
        {
            Log.Warn("HTTP: unknown or stale file taskId");
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await ctx.Response.WriteAsync("unknown task");
            return;
        }

        try { DownloadStarted?.Invoke(task.TaskId); } catch { }

        if (fileId is not null)
        {
            if (!int.TryParse(fileId, out var idx) || idx < 0 || idx >= task.Files.Count)
            {
                Log.Warn($"HTTP: fileId '{fileId}' out of range (0..{task.Files.Count - 1})");
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await ctx.Response.WriteAsync("unknown fileId");
                return;
            }
            var single = new List<string> { task.Files[idx] };
            ctx.Response.StatusCode = (int)HttpStatusCode.OK;
            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers["nd_zip"] = "true";
            ctx.Response.Headers["Oshare-Transfer-Type"] = "folder-stream";
            ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{Uri.EscapeDataString(Path.GetFileName(single[0]))}\"";
            await WriteFolderStreamZip(ctx, single, new FileInfo(single[0]).Length, _cancelCts.Token);
            Log.Info($"HTTP: per-file zip done: {single[0]}");
            try { DownloadFinished?.Invoke(task.TaskId); } catch { }
            return;
        }

        ctx.Response.StatusCode = (int)HttpStatusCode.OK;
        ctx.Response.ContentType = "application/zip";
        ctx.Response.Headers["nd_zip"] = "true";
        ctx.Response.Headers["Oshare-Transfer-Type"] = "folder-stream";
        ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{Uri.EscapeDataString(task.FirstFileName)}\"";
        await WriteFolderStreamZip(ctx, task.Files, task.TotalSize, _cancelCts.Token);
        try { DownloadFinished?.Invoke(task.TaskId); } catch { }
    }

    private async Task WriteFolderStreamZip(HttpContext ctx, IReadOnlyList<string> files, long total, CancellationToken cancelToken)
    {
        long sent = 0;
        await using (var zip = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entryName = "0/" + SanitizeEntryName(Path.GetFileName(file));
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
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
