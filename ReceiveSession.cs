using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;

namespace OShareSender;

public sealed record ReceiveMetadata(
    string TaskId,
    string SenderName,
    string FileName,
    int FileCount,
    long TotalSize,
    string MimeType);

/// <summary>
/// Connects as a WebSocket + HTTP client to the sending phone/pad,
/// performs version negotiation, accepts the sendRequest, and streams the ZIP download.
/// Supports both plain 'ws://' (stock OPlus with version ≥ 10015) and 'wss://' (OShare app).
/// </summary>
public sealed class ReceiveSession : IDisposable
{
    private WebSocket? _ws;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Action<string> _state;
    private readonly Action<long, long>? _progress;
    private readonly Action<ReceiveMetadata>? _metadata;
    private readonly string _saveDir;
    private int _seq;

    public string SenderName { get; private set; } = "";
    public string FileName { get; private set; } = "";
    public long TotalSize { get; private set; }
    public string TaskId { get; private set; } = "";
    public List<string> SavedFiles { get; } = new();

    private ReceiveSession(string saveDir, Action<string> state, Action<long, long>? progress, Action<ReceiveMetadata>? metadata)
    {
        _saveDir = saveDir;
        _state = state;
        _progress = progress;
        _metadata = metadata;
        Directory.CreateDirectory(saveDir);

        _http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        });
    }

    public static async Task<List<string>> PullAsync(
        string phoneIp, int port, string phoneName, string saveDir,
        Action<string> state, Action<long, long>? progress = null,
        CancellationToken ct = default, Action<ReceiveMetadata>? metadata = null)
    {
        using var session = new ReceiveSession(saveDir, state, progress, metadata);
        session.SenderName = phoneName;
        await session.RunAsync(phoneIp, port, ct, preconnected: null);
        return session.SavedFiles;
    }

    public static async Task<List<string>> PullAsync(
        string phoneIp, int port, string phoneName, string saveDir,
        Action<string> state, Action<long, long>? progress,
        CancellationToken ct, ClientWebSocket? preconnected, Action<ReceiveMetadata>? metadata = null)
    {
        using var session = new ReceiveSession(saveDir, state, progress, metadata);
        session.SenderName = phoneName;
        await session.RunAsync(phoneIp, port, ct, preconnected);
        return session.SavedFiles;
    }

    /// <summary>
    /// OnePlus Share 16.10.61 race fix: the phone sends its UDP band-check
    /// control message (action:0:wlanBandwidth) over the WebSocket the moment
    /// it receives wlan_accept — before the PC's WebSocket handshake used to
    /// finish, so the message was lost and the phone hit its 1 s timeout
    /// (check_wlan_band_failed). The receiver therefore PRE-CONNECTS to the
    /// phone's WebSocket before wlan_accept is sent over GATT. Bounded timeout:
    /// on failure the caller falls back to connect-after-accept.
    /// </summary>
    internal static async Task<ClientWebSocket?> PreConnectAsync(
        string host, int port, TimeSpan timeout, Action<string>? state)
    {
        foreach (var scheme in new[] { "ws", "wss" })
        {
            var ws = new ClientWebSocket();
            try
            {
                ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                state?.Invoke($"WLAN WS pre-connect started ({scheme}://{host}:{port})…");
                using var cts = new CancellationTokenSource(timeout);
                await ws.ConnectAsync(new Uri($"{scheme}://{host}:{port}/websocket"), cts.Token);
                state?.Invoke("WLAN WS connected (pre-connected before wlan_accept)");
                return ws;
            }
            catch (Exception ex)
            {
                state?.Invoke($"WLAN WS pre-connect via {scheme} failed: {ex.Message}");
                ws.Dispose();
            }
        }
        return null;
    }

    private async Task RunAsync(string phoneIp, int port, CancellationToken ct, ClientWebSocket? preconnected)
    {
        WebSocket? ws = null;
        string activeScheme = "ws";
        if (preconnected is not null)
        {
            _ws = preconnected;
            _state("using pre-connected WLAN WebSocket");
        }
        else
        {
            // APK b9.c disables TLS for peer protocol versions >= 10015. The
            // observed phone peer is 161061, so plain ws is the primary path.
            var schemes = new[] { "ws", "wss" };
            var connected = false;
            activeScheme = "wss";
            var connectDeadline = DateTime.UtcNow.AddSeconds(60);

            while (!connected && DateTime.UtcNow < connectDeadline)
            {
                foreach (var scheme in schemes)
                {
                    ct.ThrowIfCancellationRequested();
                    var wsUri = new Uri($"{scheme}://{phoneIp}:{port}/websocket");
                    _state($"connecting {wsUri}…");
                    try
                    {
                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                        ws = await ConnectApkWebSocketAsync(wsUri, scheme == "wss", linked.Token);
                        _ws = ws;
                        activeScheme = scheme;
                        connected = true;
                        _state($"connected to sender at {phoneIp}:{port} via {scheme}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _state($"connection attempt via {scheme} failed: {ex.GetType().Name}: {ex.Message}");
                        ws?.Dispose();
                    }
                }

                if (!connected && DateTime.UtcNow < connectDeadline)
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            if (!connected)
                throw new IOException($"Could not establish WebSocket connection to {phoneIp}:{port}");
        }

        // OnePlus Share 16.10.61 (d8/j): the PHONE is the WebSocket server and
        // initiates versionNegotiation itself (action:0:versionNegotiation?{"versions":[1]}),
        // the PC only ACKs {"version":1}. Sending our own first can derail the
        // phone's handshake state machine. But some iPad/LAN clients never send
        // it — so wait ~5 s for the phone's negotiation and only then send ours
        // as a fallback (once).
        var handshaken = false;
        var fallbackSent = false;
        Envelope? sendRequest = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var negotiateBy = DateTime.UtcNow.AddSeconds(5);

        while (sendRequest is null && !ct.IsCancellationRequested)
        {
            // fallback: the phone never negotiated — send our own handshake
            // (the old iPad/LAN client flow) and extend the overall deadline
            if (!handshaken && !fallbackSent && negotiateBy <= DateTime.UtcNow)
            {
                var versionMessage = Envelope.Build("action", 0, "versionNegotiation",
                    new { version = 2, protocolVersion = 4 });
                await SendAsync(versionMessage, ct);
                _state("LAN protocol handshake sent (phone did not negotiate)");
                fallbackSent = true;
                deadline = DateTime.UtcNow.AddSeconds(30);
            }

            // ReceiveAsync blocks until a frame arrives, so the deadline must be
            // enforced on the receive itself — checking it after a frame would
            // leave the session hanging forever on a silent sender.
            var limit = !handshaken && !fallbackSent ? negotiateBy : deadline;
            var remaining = limit - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                if (!handshaken && !fallbackSent) continue;   // loop top sends the fallback
                throw new TimeoutException("timed out waiting for sender's sendRequest");
            }
            Envelope? env;
            try
            {
                env = await ReceiveAsync(ct).WaitAsync(remaining, ct);
            }
            catch (TimeoutException)
            {
                if (!handshaken && !fallbackSent && negotiateBy <= DateTime.UtcNow) continue;
                throw new TimeoutException("timed out waiting for sender's sendRequest");
            }
            if (env is null) throw new IOException("sender closed the websocket before sendRequest");

            if (env.IsRaw)
            {
                _state($"raw frame '{env.Method}' (ignored)");
                continue;
            }

            switch (env.Method)
            {
                case "versionNegotiation":
                    handshaken = true;
                    _state($"← {env}");
                    if (env.IsAction)
                        await SendAckAsync(env, "{\"version\":1}", ct);
                    else if (env.IsAck)
                        _state("LAN protocol handshake acknowledged");
                    break;

                case "sendRequest":
                    _state($"← {env}");
                    FileName = env.PayloadString("fileName");
                    TotalSize = env.PayloadLong("totalSize");
                    TaskId = env.PayloadString("taskId", env.PayloadString("id"));
                    SenderName = env.PayloadString("senderName", SenderName);
                    var fileCount = env.PayloadInt("fileCount");
                    var mimeType = env.PayloadString("mimeType", "file/*");
                    _metadata?.Invoke(new ReceiveMetadata(
                        TaskId, SenderName, FileName, fileCount, TotalSize, mimeType));
                    Log.Info($"RX: receive metadata updated taskId={TaskId} sender='{SenderName}' file='{FileName}' count={fileCount} total={TotalSize} mime='{mimeType}'");
                    await SendAckAsync(env, "{}", ct);
                    sendRequest = env;
                    _state($"receiving {fileCount} file(s) from {SenderName} ({FormatSize(TotalSize)})");
                    break;

                case "status":
                    await HandleStatusAsync(env, ct);
                    break;

                case "wlanBandwidth":
                    // APK l9/n: the phone sends action:0:wlanBandwidth?{"wlan":"start"},
                    // floods ~1 MB of UDP, and waits ≤1 s for ack:0:wlanBandwidth.
                    // Q() parses payload "speed" as float — unparsable/missing is
                    // tolerated (0.0) but the ACK ITSELF must arrive within the window.
                    _state("received WLAN bandwidth command");
                    var speed = ReceiveGattServer.BandwidthSpeedMBps();
                    await SendAckAsync(env, $"{{\"speed\":\"{speed:F2}\"}}", ct);
                    _state($"WLAN bandwidth ACK sent (speed={speed:F2}) — check completed");
                    break;

                default:
                    _state($"← unhandled '{env.Method}'");
                    break;
            }
        }

        // Download the files
        var httpScheme = activeScheme == "wss" ? "https" : "http";
        var downloadUri = new Uri($"{httpScheme}://{phoneIp}:{port}/download?taskId={Uri.EscapeDataString(TaskId)}");
        _state($"downloading from {downloadUri}…");

        using var response = await _http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var networkStream = await response.Content.ReadAsStreamAsync(ct);
        // ZipArchive in Read mode needs a seekable stream (it reads the central
        // directory from the end), and the HTTP response stream is not seekable —
        // spool the download to a temp file first.
        var tempZip = Path.Combine(Path.GetTempPath(), $"oshare-rx-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var spool = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None,
                256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[256 * 1024];
                long received = 0;
                var lastReport = Stopwatch.GetTimestamp();
                var reportInterval = Stopwatch.Frequency / 5;
                int read;
                while ((read = await networkStream.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    await spool.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    var now = Stopwatch.GetTimestamp();
                    if (now - lastReport >= reportInterval)
                    {
                        _progress?.Invoke(received, response.Content.Headers.ContentLength ?? TotalSize);
                        lastReport = now;
                    }
                }
                _progress?.Invoke(received, response.Content.Headers.ContentLength ?? TotalSize);
                await spool.FlushAsync(ct);
            }

            using var zipFile = File.OpenRead(tempZip);
            using var zip = new ZipArchive(zipFile, ZipArchiveMode.Read);
            await ExtractZipAsync(zip, ct);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }

        // Send completion status
        await SendAsync(Envelope.Build("action", ++_seq, "status", new
        {
            taskId = TaskId,
            type = 1,
            reason = "ok",
        }), ct);

        try { await ReceiveAsync(ct).WaitAsync(TimeSpan.FromSeconds(3), ct); } catch { }
        _state($"successfully received {SavedFiles.Count} file(s) to {_saveDir}");
    }

    private static async Task<WebSocket> ConnectApkWebSocketAsync(Uri uri, bool useTls, CancellationToken ct)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        var local = GetPhysicalWifiAddress(IPAddress.Parse(uri.Host));
        if (local is not null)
        {
            tcp.Client.Bind(new IPEndPoint(local, 0));
            Log.Info($"RX: WebSocket bound to Wi-Fi address {local}");
        }

        await tcp.ConnectAsync(uri.Host, uri.Port, ct);
        Stream stream = tcp.GetStream();
        if (useTls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = uri.Host,
                EnabledSslProtocols = SslProtocols.Tls12,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }, ct);
            stream = ssl;
        }

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = $"GET {uri.AbsolutePath} HTTP/1.1\r\n" +
                      $"Host: {uri.Host}:{uri.Port}\r\n" +
                      "Upgrade: websocket\r\n" +
                      "Connection: Upgrade\r\n" +
                      $"Sec-WebSocket-Key: {key}\r\n" +
                      "Sec-WebSocket-Version: 13\r\n" +
                      "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        await stream.FlushAsync(ct);

        using var header = new MemoryStream();
        var one = new byte[1];
        while (header.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(one, ct);
            if (read == 0) throw new IOException("Phone closed the TCP stream before WebSocket handshake");
            header.WriteByte(one[0]);
            if (header.Length >= 4)
            {
                var bytes = header.GetBuffer();
                var n = (int)header.Length;
                if (bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n') break;
            }
        }

        var response = Encoding.ASCII.GetString(header.ToArray());
        if (!response.StartsWith("HTTP/1.1 101", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Phone WebSocket handshake rejected: {response.Split("\r\n")[0]}");
        var accept = response.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(x => x.StartsWith("Sec-WebSocket-Accept:", StringComparison.OrdinalIgnoreCase))?
            .Split(':', 2)[1].Trim();
        var expected = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        if (!string.Equals(accept, expected, StringComparison.Ordinal))
            throw new IOException("Phone WebSocket handshake returned an invalid accept key");

        return WebSocket.CreateClientWebSocket(stream, null, 64 * 1024, 64 * 1024,
            TimeSpan.FromSeconds(20), false, default);
    }

    private static IPAddress? GetPhysicalWifiAddress(IPAddress peer)
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => !n.Name.StartsWith("Local Area Connection*", StringComparison.OrdinalIgnoreCase))
            .Where(n => !n.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .FirstOrDefault(a => IsSameSubnet(a.Address, a.IPv4Mask, peer))?.Address;
    }

    private static bool IsSameSubnet(IPAddress address, IPAddress mask, IPAddress peer)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var p = peer.GetAddressBytes();
        if (a.Length != 4 || m.Length != 4 || p.Length != 4) return false;
        for (var i = 0; i < 4; i++)
            if ((a[i] & m[i]) != (p[i] & m[i])) return false;
        return true;
    }

    private async Task ExtractZipAsync(ZipArchive zip, CancellationToken ct)
    {
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var target = SafePath(_saveDir, entry.FullName);
            var temp = target + ".part-" + Guid.NewGuid().ToString("N");
            Log.Info($"RX: extract: entry '{entry.FullName}' -> temp '{Path.GetFileName(temp)}'");

            try
            {
                // The streams MUST be disposed before File.Move: the writer holds
                // an exclusive (FileShare.None) lock on temp, and `await using var`
                // only releases it at the END OF THE SCOPE — the Move used to run
                // while our own process still held the lock ("being used by another
                // process"). The nested block closes both streams deterministically.
                await using (var input = entry.Open())
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 128 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous))
                {
                    await input.CopyToAsync(output, ct);
                    await output.FlushAsync(ct);
                }
                Log.Info($"RX: extract: temp closed, renaming to '{Path.GetFileName(target)}'");
                File.Move(temp, target, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }

            SavedFiles.Add(target);
            _state($"saved {Path.GetFileName(target)} ({FormatSize(entry.Length)})");
        }
    }

    private async Task HandleStatusAsync(Envelope env, CancellationToken ct)
    {
        // Heartbeat handling: type 6 -> answer type 7
        if (env.PayloadInt("type") == 6)
        {
            await SendAsync(Envelope.Build("action", ++_seq, "status", new
            {
                taskId = TaskId,
                type = 7,
            }), ct);
        }
        await SendAckAsync(env, "{}", ct);
    }

    private async Task<Envelope?> ReceiveAsync(CancellationToken ct)
    {
        var ws = _ws ?? throw new InvalidOperationException("WebSocket is not connected");
        var buffer = new byte[32 * 1024];
        var text = new StringBuilder();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new IOException("sender returned a non-text WebSocket frame");
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (text.Length > 2 * 1024 * 1024) throw new IOException("WebSocket control frame is too large");
            if (result.EndOfMessage)
            {
                var raw = text.ToString();
                Log.Info($"RX: websocket <- {raw}");
                return Envelope.Parse(raw);
            }
        }
    }

    private Task SendAckAsync(Envelope env, string payload, CancellationToken ct) =>
        SendAsync($"ack:{env.Seq}:{env.Method}?{payload}", ct);

    private async Task SendAsync(string text, CancellationToken ct)
    {
        var ws = _ws ?? throw new InvalidOperationException("WebSocket is not connected");
        await _sendGate.WaitAsync(ct);
        try
        {
            Log.Info($"RX: websocket -> {text}");
            await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
        }
        finally { _sendGate.Release(); }
    }

    internal static string SafePath(string root, string entryName)
    {
        var normalized = entryName.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        // Remove batch prefix like "0/" if present
        if (parts.Count > 1 && int.TryParse(parts[0], out _)) parts.RemoveAt(0);
        if (parts.Count == 0) parts.Add("received-file");

        for (var i = 0; i < parts.Count; i++)
        {
            var clean = new string(parts[i].Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            parts[i] = clean is "." or ".." or "" ? "_" : clean;
        }

        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(new[] { rootFull }.Concat(parts).ToArray()));
        if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            candidate = Path.Combine(rootFull, "received-file");

        var dir = Path.GetDirectoryName(candidate)!;
        Directory.CreateDirectory(dir);

        if (!File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(candidate);
        var ext = Path.GetExtension(candidate);
        for (var i = 1; i < 10_000; i++)
        {
            var alternative = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(alternative)) return alternative;
        }
        throw new IOException("Too many files with the same name");
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024f:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024f * 1024f):F1} MB",
        _ => $"{bytes / (1024f * 1024f * 1024f):F2} GB",
    };

    public void Dispose()
    {
        try { _ws?.Dispose(); } catch { }
        _http.Dispose();
        _sendGate.Dispose();
    }
}
