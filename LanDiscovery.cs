using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OShareSender;

/// <summary>
/// The OPlus-Connect LAN discovery (SSDP-style HTTP-over-UDP on port 10150/10151):
///   PC → UDP 239.255.255.250:10150 (+ unicast to known devices) announcing itself.
/// The phone's send-sheet adds the querier to its device list.
/// </summary>
public sealed class LanDiscovery : IDisposable
{
    public string DeviceId { get; }          // 12 hex chars
    public string DeviceName { get; set; } = Environment.MachineName;
    public string AccountDigest { get; set; } = "";
    public string LanIp { get; set; } = "";
    public string? KnownPadIp { get; set; }

    private UdpClient? _udp;
    private TcpListener? _tcp;
    private System.Threading.Timer? _queryTimer;
    private int _dspPort = 10151;
    private CancellationTokenSource? _cts;

    public event Action<string>? StateChanged;
    public event Action<string, int, string, string, string>? DeviceAnnounced;
    public event Action<string, string>? CommandLine;

    public LanDiscovery(string lanMacHex12)
    {
        DeviceId = lanMacHex12.Length == 12 ? lanMacHex12.ToUpperInvariant() : "000000000000";
    }

    private void State(string msg)
    {
        Log.Info($"DISC: {msg}");
        try { StateChanged?.Invoke(msg); } catch { }
    }

    private string QueryText()
    {
        var sb = new StringBuilder();
        sb.Append("QUERY * HTTP/1.1\r\n");
        sb.Append("MAN: \"sdp/1\"\r\n");
        sb.Append("NS: scp.device\r\n");
        sb.Append("DSP: 10151\r\n");
        sb.Append("DMS: 0\r\n");
        sb.Append($"PDID: {DeviceId}\r\n");
        sb.Append($"AD: {AccountDigest}\r\n");
        sb.Append("DT: 6\r\n");
        sb.Append($"DN: {DeviceName}\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    private string AnnounceText(string remoteIp)
    {
        var sb = new StringBuilder();
        sb.Append("ANNOUNCE * HTTP/1.1\r\n");
        sb.Append("MAN: \"sdp/1\"\r\n");
        sb.Append("NS: scp.device\r\n");
        sb.Append("DSP: 10151\r\n");
        sb.Append("CSP: 10152\r\n");
        sb.Append("DMS: 0\r\n");
        sb.Append($"PDID: {DeviceId}\r\n");
        sb.Append($"AD: {AccountDigest}\r\n");
        sb.Append("DT: 6\r\n");
        sb.Append($"DN: {DeviceName}\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    public void Start()
    {
        if (_udp is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        var bindIp = string.IsNullOrEmpty(LanIp) ? IPAddress.Any : IPAddress.Parse(LanIp);
        try
        {
            _udp.Client.Bind(new IPEndPoint(bindIp, 10150));
            State($"UDP listening on {LanIp}:10150");
        }
        catch (Exception ex)
        {
            try
            {
                _udp.Client.Bind(new IPEndPoint(bindIp, 0));
                State($"UDP 10150 busy ({ex.Message.Split('\n')[0]}) - bound ephemeral port");
            }
            catch { }
        }

        try
        {
            _dspPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _udp.MulticastLoopback = false;
            _udp.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"), bindIp);
            _udp.Client.SendTimeout = 1000;
        }
        catch { }

        // receive loop (ANNOUNCEs from devices + QUERYs from other devices)
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_udp == null) break;
                    var res = await _udp.ReceiveAsync(ct);
                    var text = Encoding.UTF8.GetString(res.Buffer);
                    var pdid = ParseHeader(text, "PDID");
                    var dt = ParseHeader(text, "DT");
                    if (text.StartsWith("ANNOUNCE") && pdid is not null)
                        DeviceAnnounced?.Invoke(res.RemoteEndPoint.Address.ToString(), 8959, pdid, dt ?? "", text);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Warn($"DISC: udp recv: {ex.Message}"); }
            }
        }, ct);

        // Periodically query peers and announce this PC. The announcement is the
        // receive-side fallback when the Bluetooth adapter cannot emit the full
        // alliance ADV + scan-response record.
        _queryTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var query = Encoding.UTF8.GetBytes(QueryText());
                var announce = Encoding.UTF8.GetBytes(AnnounceText("239.255.255.250"));
                _udp?.Send(query, query.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 10150));
                _udp?.Send(announce, announce.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 10150));
                if (!string.IsNullOrEmpty(KnownPadIp))
                {
                    _udp?.Send(query, query.Length, new IPEndPoint(IPAddress.Parse(KnownPadIp), 10150));
                    _udp?.Send(announce, announce.Length, new IPEndPoint(IPAddress.Parse(KnownPadIp), 10150));
                }
            }
            catch (Exception ex) { Log.Warn($"DISC: announce/query send: {ex.Message}"); }
        }, null, 0, 3000);

        // TCP command channel on 10150
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _tcp = new TcpListener(IPAddress.Any, 10150);
                    _tcp.Start();
                    State("TCP 10150 listening - receive discovery channel ready");
                    _ = Task.Run(async () =>
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            TcpClient client;
                            try { client = await _tcp!.AcceptTcpClientAsync(ct); }
                            catch { break; }
                            _ = HandleTcpClientAsync(client, ct);
                        }
                    }, ct);
                    return;
                }
                catch (SocketException)
                {
                    try { _tcp?.Stop(); } catch { }
                    await Task.Delay(10000, ct);
                }
                catch { await Task.Delay(10000, ct); }
            }
        }, ct);
    }

    private static string? ParseHeader(string text, string name)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var idx = trimmed.IndexOf(':');
            if (idx > 0 && trimmed[..idx].Trim() == name)
                return trimmed[(idx + 1)..].Trim();
        }
        return null;
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        using var clientRef = client;
        var stream = client.GetStream();
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested && client.Connected)
        {
            int n;
            try { n = await stream.ReadAsync(buffer, ct); }
            catch { break; }
            if (n == 0) break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
            string text;
            while ((text = sb.ToString()).Contains("\r\n\r\n"))
            {
                var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
                var message = text[..headerEnd];
                sb.Remove(0, headerEnd);
                CommandLine?.Invoke(remote, message);

                if (message.StartsWith("QUERY"))
                {
                    var reply = Encoding.UTF8.GetBytes(AnnounceText(remote));
                    await stream.WriteAsync(reply, ct);
                }
                else if (message.StartsWith("SENSELESS"))
                {
                    var reply = Encoding.UTF8.GetBytes(message.Replace("SENSELESS", "SENSELESS-ACK"));
                    await stream.WriteAsync(reply, ct);
                }
            }
        }
    }

    public void Dispose()
    {
        try { _queryTimer?.Dispose(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        try { _tcp?.Stop(); } catch { }
    }
}
