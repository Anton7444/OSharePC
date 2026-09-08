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
using System.Text.Json;
using System.Diagnostics;

namespace CatShareSender;

public sealed record ReceiveMetadata(
    string TaskId,
    string SenderName,
    string FileName,
    int FileCount,
    long TotalSize,
    string MimeType);

/// <summary>
/// Connects as a WebSocket + HTTP client to the sending phone/pad, performs
/// version negotiation, accepts sendRequest, and receives either files or the
/// experimental OEM URL big-message form.
/// </summary>
public sealed class ReceiveSession : IDisposable
{
    private WebSocket? _ws;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Action<string> _state;
    private readonly Action<long, long>? _progress;
    private readonly Action<ReceiveMetadata>? _metadata;
    private readonly Action<string>? _urlReceived;
    private readonly string _saveDir;
    private int _seq;

    public string SenderName { get; private set; } = "";
    public string FileName { get; private set; } = "";
    public long TotalSize { get; private set; }
    public string TaskId { get; private set; } = "";
    public string ReceivedUrl { get; private set; } = "";
    public List<string> SavedFiles { get; } = new();

    private ReceiveSession(
        string saveDir, Action<string> state, Action<long, long>? progress,
        Action<ReceiveMetadata>? metadata, Action<string>? urlReceived)
    {
        _saveDir = saveDir;
        _state = state;
        _progress = progress;
        _metadata = metadata;
        _urlReceived = urlReceived;
        Directory.CreateDirectory(saveDir);

        _http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        });
    }

    public static async Task<List<string>> PullAsync(
        string phoneIp, int port, string phoneName, string saveDir,
        Action<string> state, Action<long, long>? progress = null,
        CancellationToken ct = default, Action<ReceiveMetadata>? metadata = null,
        Action<string>? urlReceived = null)
    {
        using var session = new ReceiveSession(saveDir, state, progress, metadata, urlReceived);
        session.SenderName = phoneName;
        await session.RunAsync(phoneIp, port, ct, preconnected: null);
        return session.SavedFiles;
    }

    public static async Task<List<string>> PullAsync(
        string phoneIp, int port, string phoneName, string saveDir,
        Action<string> state, Action<long, long>? progress,
        CancellationToken ct, ClientWebSocket? preconnected,
        Action<ReceiveMetadata>? metadata = null, Action<string>? urlReceived = null)
    {
        using var session = new ReceiveSession(saveDir, state, progress, metadata, urlReceived);
        session.SenderName = phoneName;
        await session.RunAsync(phoneIp, port, ct, preconnected);
        return session.SavedFiles;
    }

    /// <summary>
    /// OnePlus Share 16.10.61 race fix: pre-connect before wlan_accept so the
    /// phone's first wlanBandwidth frame cannot be lost.
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

        var handshaken = false;
        var fallbackSent = false;
        Envelope? sendRequest = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var negotiateBy = DateTime.UtcNow.AddSeconds(5);

        while (sendRequest is null && !ct.IsCancellationRequested)
        {
            if (!handshaken && !fallbackSent && negotiateBy <= DateTime.UtcNow)
            {
                var versionMessage = Envelope.Build("action", 0, "versionNegotiation",
                    new { version = 2, protocolVersion = 4 });
                await SendAsync(versionMessage, ct);
                _state("LAN protocol handshake sent (phone did not negotiate)");
                fallbackSent = true;
                deadline = DateTime.UtcNow.AddSeconds(30);
            }

            var limit = !handshaken && !fallbackSent ? negotiateBy : deadline;
            var remaining = limit - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                if (!handshaken && !fallbackSent) continue;
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
                    TaskId = env.PayloadString("taskId", env.PayloadString("id"));
                    var messageId = env.PayloadString("messageId");
                    var isMessageSend = string.Equals(env.PayloadString("isMessageSend"), "true", StringComparison.OrdinalIgnoreCase);

                    if (isMessageSend && !string.IsNullOrWhiteSpace(messageId))
                    {
                        var bigMessage = await GetBigMessageAsync(phoneIp, port, TaskId, messageId, activeScheme, ct);
                        ApplyTaskInfo(bigMessage);
                    }
                    else
                    {
                        ApplyInlineTaskInfo(env);
                    }

                    await SendAckAsync(env, "{}", ct);
                    sendRequest = env;
                    if (!string.IsNullOrEmpty(ReceivedUrl))
                    {
                        _state($"received URL from {SenderName}: {ReceivedUrl}");
                    }
                    else
                    {
                        _state($"receiving files from {SenderName} ({FormatSize(TotalSize)})");
                    }
                    break;

                case "status":
                    await HandleStatusAsync(env, ct);
                    break;

                case "wlanBandwidth":
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

        if (!string.IsNullOrEmpty(ReceivedUrl))
        {
            await SendCompletionStatusAsync(ct);
            _state($"URL received successfully from {SenderName}");
            return;
        }

        var httpScheme = activeScheme == "wss" ? "https" : "http";
        var downloadUri = new Uri($"{httpScheme}://{phoneIp}:{port}/download?taskId={Uri.EscapeDataString(TaskId)}");
        _state($"downloading from {downloadUri}…");

        // Stock ColorOS can write a complete ZIP but keep the HTTP connection alive
        // without giving .NET a clean EOF. Ask for close explicitly, honor a declared
        // Content-Length when present, and use a bounded no-data timeout as a final
        // compatibility escape hatch. The ZIP central directory is still validated
        // before any extraction, so a genuinely truncated response is rejected.
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.ConnectionClose = true;

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        var headerSummary = string.Join("; ", response.Headers.Concat(response.Content.Headers)
            .Select(h => $"{h.Key}={string.Join(",", h.Value)}"));
        Log.Info($"RX: HTTP download response {(int)response.StatusCode} {response.ReasonPhrase}; contentLength={(contentLength?.ToString() ?? "unknown")}; {headerSummary}");

        await using var networkStream = await response.Content.ReadAsStreamAsync(ct);
        var tempZip = Path.Combine(Path.GetTempPath(), $"catshare-rx-{Guid.NewGuid():N}.zip");
        var bodyEndedByIdleTimeout = false;
        try
        {
            await using (var spool = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None,
                256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[256 * 1024];
                long received = 0;
                var lastReport = Stopwatch.GetTimestamp();
                var reportInterval = Stopwatch.Frequency / 5;

                while (true)
                {
                    int read;
                    using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        // First byte can legitimately take longer while the phone opens
                        // its file. Once data is flowing, five seconds with no next byte
                        // is a protocol/framing stall on a local OShare link.
                        readCts.CancelAfter(received == 0 ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(5));
                        try
                        {
                            read = await networkStream.ReadAsync(buffer.AsMemory(), readCts.Token);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            if (received == 0)
                                throw new TimeoutException("phone HTTP download produced no body data within 12 seconds");

                            bodyEndedByIdleTimeout = true;
                            Log.Warn($"RX: HTTP body produced no more bytes for 5 s after {received} bytes; treating this as a possible stock-OShare missing EOF and validating the ZIP");
                            break;
                        }
                    }

                    if (read == 0)
                    {
                        Log.Info($"RX: HTTP body reached EOF after {received} bytes");
                        break;
                    }

                    await spool.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;

                    if (contentLength is long declared)
                    {
                        if (received > declared)
                            throw new InvalidDataException($"phone sent more HTTP body data than Content-Length ({received}>{declared})");
                        if (received == declared)
                        {
                            Log.Info($"RX: HTTP body complete by Content-Length ({received} bytes); no EOF wait needed");
                            break;
                        }
                    }

                    var now = Stopwatch.GetTimestamp();
                    if (now - lastReport >= reportInterval)
                    {
                        _progress?.Invoke(received, contentLength ?? TotalSize);
                        lastReport = now;
                    }
                }

                _progress?.Invoke(received, contentLength ?? TotalSize);
                await spool.FlushAsync(ct);
                Log.Info($"RX: HTTP body spooled {received} bytes (idle-timeout-termination={bodyEndedByIdleTimeout})");
            }

            try
            {
                using var zipFile = File.OpenRead(tempZip);
                using var zip = new ZipArchive(zipFile, ZipArchiveMode.Read);
                Log.Info($"RX: ZIP central directory valid, entries={zip.Entries.Count}");
                await ExtractZipAsync(zip, ct);
            }
            catch (InvalidDataException ex) when (bodyEndedByIdleTimeout)
            {
                throw new IOException("phone stopped the HTTP body without EOF before a complete ZIP was received", ex);
            }
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }

        await SendCompletionStatusAsync(ct);
        _state($"successfully received {SavedFiles.Count} file(s) to {_saveDir}");
    }

    private async Task<JsonElement> GetBigMessageAsync(
        string host, int port, string taskId, string messageId, string activeWsScheme, CancellationToken ct)
    {
        var preferred = activeWsScheme == "wss" ? new[] { "https", "http" } : new[] { "http", "https" };
        Exception? last = null;
        foreach (var scheme in preferred)
        {
            var uri = new Uri($"{scheme}://{host}:{port}/bigmessage?taskId={Uri.EscapeDataString(taskId)}&messageId={Uri.EscapeDataString(messageId)}");
            try
            {
                _state($"fetching OShare message metadata from {scheme}://{host}:{port}/bigmessage…");
                using var response = await _http.GetAsync(uri, ct);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                Log.Info($"RX: /bigmessage <- {json}");
                return doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                last = ex;
                Log.Warn($"RX: /bigmessage via {scheme} failed: {ex.Message}");
            }
        }
        throw new IOException($"Could not fetch OShare big-message metadata: {last?.Message}");
    }

    private void ApplyInlineTaskInfo(Envelope env)
    {
        FileName = env.PayloadString("fileName");
        TotalSize = env.PayloadLong("totalSize");
        TaskId = env.PayloadString("taskId", env.PayloadString("id"));
        SenderName = env.PayloadString("senderName", SenderName);
        var fileCount = Math.Max(1, env.PayloadInt("fileCount"));
        var mimeType = env.PayloadString("mimeType", "file/*");
        if (mimeType.Equals("http/*", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = FirstNonEmpty(
                env.PayloadString("shareText"), env.PayloadString("url"),
                env.PayloadString("text"), env.PayloadString("content"));
            AcceptUrl(candidate);
        }
        PublishMetadata(fileCount, mimeType);
    }

    private void ApplyTaskInfo(JsonElement root)
    {
        string GetString(string name, string fallback = "") =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;
        long GetLong(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var n) ? n : 0;
        int GetInt(string name, int fallback = 0) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : fallback;

        TaskId = GetString("taskId", TaskId);
        SenderName = GetString("senderName", SenderName);
        FileName = GetString("fileName", FileName);
        TotalSize = GetLong("totalSize");
        var fileCount = Math.Max(1, GetInt("fileCount", 1));
        var mimeType = GetString("mimeType", "file/*");

        if (mimeType.Equals("http/*", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = FirstNonEmpty(
                GetString("shareText"), GetString("url"), GetString("text"),
                GetString("content"), GetString("message"));
            AcceptUrl(candidate);
            if (TotalSize <= 0) TotalSize = Encoding.UTF8.GetByteCount(ReceivedUrl);
            if (string.IsNullOrEmpty(FileName) && Uri.TryCreate(ReceivedUrl, UriKind.Absolute, out var uri))
                FileName = uri.Host;
        }

        PublishMetadata(fileCount, mimeType);
    }

    private void AcceptUrl(string candidate)
    {
        if (!TransferTask.TryNormalizeUrl(candidate, out var normalized, out var error))
            throw new InvalidDataException($"OShare URL payload is invalid: {error}");
        ReceivedUrl = normalized;
        _urlReceived?.Invoke(normalized);
        Log.Info($"RX: URL payload accepted: {normalized}");
    }

    private void PublishMetadata(int fileCount, string mimeType)
    {
        _metadata?.Invoke(new ReceiveMetadata(TaskId, SenderName, FileName, fileCount, TotalSize, mimeType));
        Log.Info($"RX: receive metadata updated taskId={TaskId} sender='{SenderName}' file='{FileName}' count={fileCount} total={TotalSize} mime='{mimeType}'");
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private async Task SendCompletionStatusAsync(CancellationToken ct)
    {
        await SendAsync(Envelope.Build("action", ++_seq, "status", new
        {
            taskId = TaskId,
            type = 1,
            reason = "ok",
        }), ct);
        try { await ReceiveAsync(ct).WaitAsync(TimeSpan.FromSeconds(3), ct); } catch { }
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
