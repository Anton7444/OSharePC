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

    private UdpClient? _udp;
    private UdpClient? _udpReply;
    private TcpListener? _tcp;
    private System.Threading.Timer? _queryTimer;
    private CancellationTokenSource? _cts;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _recentPeerIps = new();

    public event Action<string>? StateChanged;
    public event Action<string, int, string, string, string>? DeviceAnnounced;
    public event Action<string, string>? CommandLine;
    // Fires if a real 6-byte Bluetooth address is ever found embedded in a LAN
    // packet's NSData/inner_data TLV or raw body (bt_mac candidate) — args:
    // (deviceId/PDID hint or "?", address as AA:BB:CC:DD:EE:FF).

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

    private string ServicePublishText()
    {
        // The current OPPO/OnePlus firmware's live LAN protocol: legacy ANNOUNCE/QUERY
        // are parsed but rejected as "unsupported lan packet type" by the native
        // DiscoveryResultProcessor. SERVICE-PUBLISH with NS=1 ("Senseless" service id)
        // is the format actually accepted and dispatched, confirmed via live adb logcat
        // against the real device's com.heytap.accessory process.
        var sb = new StringBuilder();
        sb.Append("SERVICE-PUBLISH * HTTP/1.1\r\n");
        sb.Append("MAN: \"sdp/1\"\r\n");
        sb.Append("NS: 1\r\n");
        sb.Append("DSP: 10151\r\n");
        sb.Append("CSP: 10152\r\n");
        sb.Append("DT: 6\r\n");
        sb.Append($"DN: {DeviceName}\r\n");
        sb.Append($"PDID: {DeviceId}\r\n");
        sb.Append($"AD: {AccountDigest}\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    private string SenselessText()
    {
        // The real phone-to-phone exchange we captured is triggered by a SENSELESS
        // broadcast (not ANNOUNCE/QUERY/SERVICE-PUBLISH), answered with a
        // SENSELESS-ACK that carries an NSDATA header — libcpkit.so confirms NSDATA
        // is TLV-encoded and can contain a bt_mac field ("parsed bt_mac from
        // inner_data"). We've only ever replied to SENSELESS before, never sent one
        // ourselves, so we've never seen a real device's SENSELESS-ACK addressed to
        // us specifically (only overheard one meant for another device on the LAN).
        var sb = new StringBuilder();
        sb.Append("SENSELESS * HTTP/1.1\r\n");
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
            _udp.MulticastLoopback = false;
            _udp.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"), bindIp);
            _udp.Client.SendTimeout = 1000;
            State($"joined multicast group 239.255.255.250 on {bindIp}");
        }
        catch (Exception ex) { State($"multicast join FAILED on {bindIp}: {ex.Message}"); }

        // receive loop (ANNOUNCEs from devices + QUERYs from other devices)
        async Task ReceiveLoop(UdpClient sock, string label)
        {
            State($"ReceiveLoop[{label}] entered, bound={sock.Client.LocalEndPoint}");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var res = await sock.ReceiveAsync(ct);
                    var text = Encoding.UTF8.GetString(res.Buffer);
                    var pdid = ParseHeader(text, "PDID");
                    var dt = ParseHeader(text, "DT");
                    var dnDebug = ParseHeader(text, "DN");
                    var peerIp = res.RemoteEndPoint.Address.ToString();
                    _recentPeerIps[peerIp] = DateTimeOffset.UtcNow;
                    foreach (var stale in _recentPeerIps.Where(kv => DateTimeOffset.UtcNow - kv.Value > TimeSpan.FromSeconds(30)).Select(kv => kv.Key).ToArray())
                        _recentPeerIps.TryRemove(stale, out _);
                    Log.Info($"DISC[{label}]: rx from {res.RemoteEndPoint}: {text.Split('\r', '\n')[0]} PDID={pdid} DT={dt} DN={dnDebug}");
                    // Always dump the full raw packet (headers + any TLV body) so a
                    // possible bt_mac field inside SERVICE-PUBLISH/SERVICE-ACK's
                    // NSData/inner_data can be captured and analyzed offline — this
                    // used to skip ANNOUNCE/SERVICE-PUBLISH, which hid exactly the
                    // packet type suspected of carrying the real Bluetooth address.
                    Log.Info($"DISC[{label}]: FULL rx from {res.RemoteEndPoint}: [{text.Replace("\r", "\\r").Replace("\n", "\\n")}]");
                    // libcpkit.so confirms the real header name is "NSDATA" (all
                    // caps); ParseHeader does a case-sensitive match, so try every
                    // spelling variant actually seen ("NSData" mixed-case was a guess
                    // that turned out wrong) rather than silently missing the field.
                    var nsData = ParseHeader(text, "NSDATA") ?? ParseHeader(text, "NSData") ?? ParseHeader(text, "inner_data");
                    if (nsData is not null)
                    {
                        if (nsData.Length == 0)
                            Log.Info($"DISC[{label}]: NSDATA header present but EMPTY (no bt_mac included by peer this time)");
                        else
                        {
                            Log.Info($"DISC[{label}]: NSDATA header present ({nsData.Length} chars): {nsData}");
                            if (text.StartsWith("SERVICE-PUBLISH"))
                                TryDecodeServicePublishNsdata(label, nsData);
                            else
                                TryLogBtMacFromTlv(label, nsData);
                        }
                    }
                    // The header block ends at the first blank line; anything after it
                    // (binary TLV body, not a header) is the most likely place a
                    // nested bt_mac field would live if it isn't in a named header.
                    var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0 && headerEnd + 4 < res.Buffer.Length)
                    {
                        var bodyBytes = res.Buffer[(headerEnd + 4)..];
                        if (bodyBytes.Length > 0)
                        {
                            Log.Info($"DISC[{label}]: raw body after headers ({bodyBytes.Length}B hex): {Convert.ToHexString(bodyBytes)}");
                            TryLogBtMacFromBinary(label, bodyBytes);
                        }
                    }
                    // A real device on this account queries us roughly once a second
                    // (e.g. "QUERY PDID=... DN=..." from a phone actively looking for
                    // paired devices) — that's just as much evidence of presence as an
                    // ANNOUNCE/SERVICE-PUBLISH, and it carries the same PDID/DT/DN fields,
                    // so treat it the same way instead of silently dropping it.
                    if ((text.StartsWith("ANNOUNCE") || text.StartsWith("SERVICE-PUBLISH") || text.StartsWith("QUERY")) && pdid is not null)
                        DeviceAnnounced?.Invoke(res.RemoteEndPoint.Address.ToString(), 8959, pdid, dt ?? "", text);
                }
                catch (OperationCanceledException ex) { State($"ReceiveLoop[{label}] cancelled: {ex.Message}"); break; }
                catch (ObjectDisposedException ex) { State($"ReceiveLoop[{label}] disposed: {ex.Message}"); break; }
                catch (Exception ex) { Log.Warn($"DISC[{label}]: udp recv: {ex.GetType().Name}: {ex.Message}"); }
            }
            State($"ReceiveLoop[{label}] exited");
        }
        _ = ReceiveLoop(_udp, "10150");

        // Real OPPO/OnePlus devices reply to discovery with a UNICAST packet sent to
        // the DSP port we advertise (10151, per ServicePublishText/AnnounceText/QueryText),
        // not back to the port they received our multicast on. Confirmed via tcpdump on a
        // real tablet: it replies from its own :10150 straight to our :10151. Without a
        // listener bound there those replies (which carry the real device name) were
        // silently dropped — this is why LAN devices only ever fell back to
        // "Nearby phone [ID xx]" instead of a real name.
        try
        {
            _udpReply = new UdpClient();
            _udpReply.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpReply.Client.Bind(new IPEndPoint(bindIp, 10151));
            State($"UDP reply listener on {LanIp}:10151");
            _ = ReceiveLoop(_udpReply, "10151");
        }
        catch (Exception ex)
        {
            State($"UDP 10151 reply listener FAILED: {ex.Message}");
        }

        // Periodically query peers and announce this PC. The announcement is the
        // receive-side fallback when the Bluetooth adapter cannot emit the full
        // alliance ADV + scan-response record.
        _queryTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                var query = Encoding.UTF8.GetBytes(QueryText());
                var announce = Encoding.UTF8.GetBytes(AnnounceText("239.255.255.250"));
                var servicePublish = Encoding.UTF8.GetBytes(ServicePublishText());
                var senseless = Encoding.UTF8.GetBytes(SenselessText());
                _udp?.Send(query, query.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 10150));
                _udp?.Send(announce, announce.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 10150));
                _udp?.Send(servicePublish, servicePublish.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 10150));
                _udp?.Send(senseless, senseless.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 10150));
                // Also unicast SENSELESS directly to every peer IP we've actually
                // heard from recently — a real device may only answer a directed
                // SENSELESS with its bt_mac-bearing NSDATA, not a multicast one.
                foreach (var ip in _recentPeerIps.Keys.ToArray())
                {
                    try { _udp?.Send(senseless, senseless.Length, new IPEndPoint(IPAddress.Parse(ip), 10150)); }
                    catch { }
                }
            }
            catch (Exception ex) { Log.Warn($"DISC: announce/query send: {ex.Message}"); }
            finally { ScheduleNextDiscovery(); }
        }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        Nudge();

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

    // Fast start: 0, 250, 500 ms, then 1 s, 2 s, settling at the steady 3 s cadence. One timer, so there is only
    // ever one discovery round in flight. Nudge() restarts the burst (Scan now, interface change).
    private static readonly int[] BurstDelaysMs = [250, 250, 500, 1000, 2000];
    private int _burstStep;

    public void Nudge()
    {
        Interlocked.Exchange(ref _burstStep, 0);
        try { _queryTimer?.Change(0, System.Threading.Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void ScheduleNextDiscovery()
    {
        var step = Interlocked.Increment(ref _burstStep) - 1;
        var delay = step < BurstDelaysMs.Length ? BurstDelaysMs[step] : 3000;
        try { _queryTimer?.Change(delay, System.Threading.Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    // Structured decoder for the NSDATA TLV that real devices attach to NS=4
    // SERVICE-PUBLISH packets (the format the tablet broadcasts when its cpkit
    // responder is publishing its known-peer list). Layout, reverse engineered from
    // live captures on the same account (see the analysis in the session log):
    //   [0]      0x61 marker
    //   [1..7)   subject device id, 6 bytes (its 12-hex PDID prefix as raw bytes)
    //   [7..]    TLV stream; tag 0x0c = ASCII 12-hex PDID; tag 0x02 = list of peers,
    //            each preceded by tag 0x0c + its 12-hex PDID; 0x80 0x10 terminator
    // The peer list is the interesting part: it shows WHICH devices the publishing
    // device currently recognizes (e.g. same-account phones) — our PC appearing in
    // it means the account/contact matching accepted us over LAN.
    private void TryDecodeServicePublishNsdata(string label, string value)
    {
        try
        {
            var pad = value + new string('=', (4 - value.Length % 4) % 4);
            var bytes = Convert.FromBase64String(pad.Replace('-', '+').Replace('_', '/'));
            if (bytes.Length < 8 || bytes[0] != 0x61)
            {
                Log.Info($"DISC[{label}]: NSDATA not the known 0x61 TLV layout ({bytes.Length}B) — hex: {Convert.ToHexString(bytes)}");
                return;
            }
            var subject = Convert.ToHexString(bytes[1..7]);
            var pdids = System.Text.RegularExpressions.Regex.Matches(
                System.Text.Encoding.ASCII.GetString(bytes), @"\b[0-9A-F]{12}\b")
                .Select(m => m.Value).Distinct().ToList();
            var hex = Convert.ToHexString(bytes);
            Log.Info($"DISC[{label}]: NS=4 NSDATA: subject={subject} peers=[{string.Join(", ", pdids.Where(p => !string.Equals(p, subject, StringComparison.OrdinalIgnoreCase)))}] len={bytes.Length}");
            Log.Info($"DISC[{label}]: NS=4 NSDATA hex: {hex}");
        }
        catch (Exception ex)
        {
            Log.Info($"DISC[{label}]: NSDATA structured decode failed: {ex.Message}");
            TryLogBtMacFromTlv(label, value);
        }
    }

    // Best-effort TLV scan for a bt_mac candidate inside a header value that may be
    // base64 or hex encoded. We don't yet know libcpkit.so's exact TLV tag layout,
    // so this is heuristic: decode the value both ways and look for any 6-byte run
    // that looks like a plausible BT address (not all-zero, not all-FF, and not
    // trivially equal to the PDID we already know).
    private void TryLogBtMacFromTlv(string label, string value)
    {
        try
        {
            byte[]? bytes = null;
            try { bytes = Convert.FromBase64String(value); } catch { }
            if (bytes is null)
            {
                try { bytes = Convert.FromHexString(value.Replace(":", "").Replace("-", "")); } catch { }
            }
            if (bytes is not null) TryLogBtMacFromBinary($"{label}/NSData", bytes);
        }
        catch (Exception ex) { Log.Warn($"DISC[{label}]: NSData decode failed: {ex.Message}"); }
    }

    private void TryLogBtMacFromBinary(string label, byte[] bytes)
    {
        for (int i = 0; i + 6 <= bytes.Length; i++)
        {
            var chunk = bytes[i..(i + 6)];
            if (chunk.All(b => b == 0x00) || chunk.All(b => b == 0xFF)) continue;
            // A real BT address has decent byte diversity; skip runs that are
            // clearly ASCII text (all printable) since that's just header noise.
            if (chunk.All(b => b >= 0x20 && b < 0x7F)) continue;
            var mac = string.Join(":", chunk.Select(b => b.ToString("X2")));
            Log.Info($"DISC[{label}]: candidate 6-byte run at offset {i}: {mac} (raw hex context: {Convert.ToHexString(bytes)})");
        }
    }

    public static string? ParseHeader(string text, string name)
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
        try { _udpReply?.Dispose(); } catch { }
        try { _tcp?.Stop(); } catch { }
    }
}
