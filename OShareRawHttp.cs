using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace CatShareSender;

/// <summary>
/// Minimal HTTP/1.1 reader for stock OShare's VS-HTTP server. Some ColorOS builds
/// advertise Transfer-Encoding: chunked but do not produce a body that .NET's
/// HttpClient chunk decoder will surface. Reading the wire stream directly lets us
/// accept either a normal chunked body or a raw ZIP body behind that header.
/// </summary>
internal static class OShareRawHttp
{
    public static async Task DownloadZipAsync(
        Uri uri,
        string outputPath,
        long expectedPayloadBytes,
        Action<long, long>? progress,
        Action<string>? state,
        CancellationToken ct)
    {
        var wirePath = outputPath + ".wire";
        try
        {
            using var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            var peer = IPAddress.Parse(uri.Host);
            var local = GetSameSubnetAddress(peer);
            if (local is not null)
            {
                tcp.Client.Bind(new IPEndPoint(local, 0));
                Log.Info($"RX: raw HTTP bound to LAN address {local}");
            }

            await tcp.ConnectAsync(uri.Host, uri.Port, ct);
            Stream stream = tcp.GetStream();
            if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
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

            var request = $"GET {uri.PathAndQuery} HTTP/1.1\r\n" +
                          $"Host: {uri.Host}:{uri.Port}\r\n" +
                          "Accept: application/zip\r\n" +
                          "Connection: close\r\n" +
                          "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
            await stream.FlushAsync(ct);

            var (statusCode, statusLine, headers) = await ReadHeadersAsync(stream, ct);
            var headerSummary = string.Join("; ", headers.Select(kv => $"{kv.Key}={kv.Value}"));
            Log.Info($"RX: raw HTTP response {statusCode} {statusLine}; {headerSummary}");
            if (statusCode < 200 || statusCode >= 300)
                throw new IOException($"phone raw HTTP download returned {statusLine}");

            var receivedWire = await ReadWireBodyAsync(stream, wirePath, expectedPayloadBytes, progress, ct);
            if (receivedWire == 0)
                throw new TimeoutException("phone raw HTTP socket produced no body bytes");

            var advertisedChunked = headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
                                    transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase);

            if (StartsWithZip(wirePath))
            {
                File.Move(wirePath, outputPath, overwrite: true);
                Log.Warn($"RX: VS-HTTP advertised chunked but wire body is a raw ZIP; bypassed chunk decoder ({receivedWire} bytes)");
            }
            else if (advertisedChunked)
            {
                var decoded = DecodeChunkedBody(wirePath, outputPath);
                Log.Info($"RX: raw HTTP chunk decoder produced {decoded} ZIP bytes from {receivedWire} wire bytes");
            }
            else
            {
                File.Move(wirePath, outputPath, overwrite: true);
                Log.Info($"RX: raw HTTP body copied without chunk decoding ({receivedWire} bytes)");
            }

            var outputSize = new FileInfo(outputPath).Length;
            progress?.Invoke(outputSize, expectedPayloadBytes > 0 ? expectedPayloadBytes : outputSize);
            state?.Invoke($"downloaded {FormatSize(outputSize)} from phone");
        }
        finally
        {
            try { if (File.Exists(wirePath)) File.Delete(wirePath); } catch { }
        }
    }

    private static async Task<(int statusCode, string statusLine, Dictionary<string, string> headers)> ReadHeadersAsync(
        Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var one = new byte[1];
        while (ms.Length < 64 * 1024)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) throw new IOException("phone closed raw HTTP socket before response headers completed");
            ms.WriteByte(one[0]);
            if (ms.Length >= 4)
            {
                var b = ms.GetBuffer();
                var len = (int)ms.Length;
                if (b[len - 4] == '\r' && b[len - 3] == '\n' && b[len - 2] == '\r' && b[len - 1] == '\n')
                    break;
            }
        }
        if (ms.Length >= 64 * 1024)
            throw new IOException("phone raw HTTP response headers exceeded 64 KB");

        var text = Encoding.ASCII.GetString(ms.ToArray());
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var statusLine = lines.FirstOrDefault() ?? "";
        var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var statusCode))
            throw new IOException($"invalid raw HTTP status line: {statusLine}");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrEmpty(line)) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (headers.TryGetValue(name, out var existing))
                headers[name] = existing + "," + value;
            else
                headers[name] = value;
        }
        return (statusCode, statusLine, headers);
    }

    private static async Task<long> ReadWireBodyAsync(
        Stream stream, string wirePath, long expectedPayloadBytes,
        Action<long, long>? progress, CancellationToken ct)
    {
        await using var output = new FileStream(wirePath, FileMode.Create, FileAccess.Write, FileShare.None,
            256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[256 * 1024];
        long received = 0;
        while (true)
        {
            int read;
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(received == 0 ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(5));
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(), readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (received == 0)
                    throw new TimeoutException("phone raw HTTP socket produced no body bytes within 8 seconds");
                Log.Warn($"RX: raw HTTP wire body idle for 5 s after {received} bytes; validating what was received");
                break;
            }

            if (read == 0)
            {
                Log.Info($"RX: raw HTTP socket EOF after {received} wire bytes");
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            progress?.Invoke(received, expectedPayloadBytes > 0 ? expectedPayloadBytes : received);
        }
        await output.FlushAsync(ct);
        return received;
    }

    private static bool StartsWithZip(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> sig = stackalloc byte[4];
        if (fs.Read(sig) < 4) return false;
        return sig[0] == (byte)'P' && sig[1] == (byte)'K' &&
               ((sig[2] == 3 && sig[3] == 4) || (sig[2] == 5 && sig[3] == 6) || (sig[2] == 7 && sig[3] == 8));
    }

    private static long DecodeChunkedBody(string wirePath, string outputPath)
    {
        using var input = File.OpenRead(wirePath);
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        long decoded = 0;

        while (input.Position < input.Length)
        {
            var line = ReadAsciiLine(input);
            if (line is null) break;
            if (line.Length == 0) continue;
            var sizeText = line.Split(';', 2)[0].Trim();
            if (!long.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
                throw new InvalidDataException($"invalid OShare HTTP chunk size '{line}'");
            if (size == 0) break;

            CopyExact(input, output, size);
            decoded += size;
            ConsumeChunkTerminator(input);
        }

        output.Flush();
        if (decoded == 0)
            throw new InvalidDataException("OShare chunked response decoded to an empty body");
        if (!StartsWithZip(outputPath))
            throw new InvalidDataException("OShare chunked response did not decode to a ZIP body");
        return decoded;
    }

    private static string? ReadAsciiLine(Stream input)
    {
        using var ms = new MemoryStream();
        while (input.Position < input.Length)
        {
            var b = input.ReadByte();
            if (b < 0) break;
            if (b == '\n') break;
            if (b != '\r') ms.WriteByte((byte)b);
            if (ms.Length > 128) throw new InvalidDataException("OShare HTTP chunk header is too long");
        }
        if (ms.Length == 0 && input.Position >= input.Length) return null;
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static void CopyExact(Stream input, Stream output, long count)
    {
        var buffer = new byte[128 * 1024];
        var remaining = count;
        while (remaining > 0)
        {
            var want = (int)Math.Min(buffer.Length, remaining);
            var n = input.Read(buffer, 0, want);
            if (n <= 0) throw new InvalidDataException("OShare HTTP chunk ended before its declared length");
            output.Write(buffer, 0, n);
            remaining -= n;
        }
    }

    private static void ConsumeChunkTerminator(Stream input)
    {
        if (input.Position >= input.Length) return;
        var first = input.ReadByte();
        if (first == '\r')
        {
            if (input.Position < input.Length && input.ReadByte() != '\n')
                throw new InvalidDataException("invalid OShare HTTP chunk terminator");
        }
        else if (first != '\n')
        {
            throw new InvalidDataException("invalid OShare HTTP chunk terminator");
        }
    }

    private static IPAddress? GetSameSubnetAddress(IPAddress peer)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var description = nic.Description;
            if (description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var a in nic.GetIPProperties().UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork || a.IPv4Mask is null)
                    continue;
                if (IsSameSubnet(a.Address, a.IPv4Mask, peer)) return a.Address;
            }
        }
        return null;
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

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024f:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024f * 1024f):F1} MB",
        _ => $"{bytes / (1024f * 1024f * 1024f):F2} GB",
    };
}
