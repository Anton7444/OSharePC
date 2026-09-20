using System.Text;
using System.Text.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace OShareSender;

/// <summary>
/// PC as the RECEIVER of a phone→PC transfer (the iOS/PC-emulation role in stock ColorOS/OxygenOS):
/// hosts the 00008881 discovery beacon that makes the PC appear in the phone's send-sheet,
/// plus the 00009999 GATT protocol service for handshake and transfer orchestration over the local Wi-Fi network (LAN).
/// </summary>
public sealed class ReceiveGattServer : IDisposable
{
    public const string Version = "161061";   // ≥ 10015 → tells phone to use LAN / WebSocket mode

    // Optional coordination hook for Bluetooth stacks that need the alliance
    // advertisement restarted after the GATT beacon is initialized.
    public Action? RestartAdvertiser { get; set; }

    private GattServiceProvider? _beacon;      // 8881 discovery beacon
    private GattServiceProvider? _service;     // 9999 protocol service
    private GattLocalCharacteristic? _handshake;
    private GattLocalCharacteristic? _cmd;
    private GattLocalCharacteristic? _notify;

    private readonly OShareCrypto _crypto = new();
    private string? _padPubKey;
    private (byte[] Key, byte[] Iv)? _session;
    private string _padName = "";

    public bool IsRunning { get; private set; }
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>Directory where incoming files will be saved.</summary>
    public string SaveDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "OShare");

    /// <summary>
    /// Raised when a remote phone offers files. Return true to accept or false to reject.
    /// Parameters: (senderName, mimeType, fileCount).
    /// </summary>
    public Func<string, string, string, Task<bool>>? ConfirmIncoming { get; set; }

    /// <summary>Human-readable state changes for UI and logs.</summary>
    public event Action<string>? StateChanged;

    /// <summary>Raised during active file download: (bytesReceived, totalBytes).</summary>
    public event Action<long, long>? TransferProgress;
    public event Action<ReceiveMetadata>? TransferMetadata;

    /// <summary>Raised when the 8881 discovery beacon actually starts/stops advertising —
    /// Windows only allows ONE connectable advert, so consumers must yield their own.</summary>
    public event Action? BeaconStarted;
    public event Action? BeaconAborted;
    /// <summary>Beacon gave up after repeated aborts — other advertisers may claim the slot.</summary>
    public event Action? BeaconGaveUp;

    private int _beaconRetrying;

    /// <summary>Set by the engine: returns true when no other advert owns the slot,
    /// so competing GATT adverts don't kick each other off in an endless loop.</summary>
    public Func<bool>? RetryGate { get; set; }

    private async void RetryBeaconAsync(GattServiceProvider beacon)
    {
        if (Interlocked.CompareExchange(ref _beaconRetrying, 1, 0) != 0) return;
        Log.Warn("RX: 8881 retry scheduled");
        try
        {
            // retry indefinitely — the beacon is what makes the PC connectable for
            // stock 互传, and the slot usually frees up within seconds
            while (IsRunning)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (!IsRunning) return;
                // The initial StartAdvertising may report Aborted first and
                // then become Started. Do not restart a healthy advert while
                // the phone is already negotiating over GATT.
                if (beacon.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started)
                    return;
                if (RetryGate?.Invoke() == false) continue;   // 9955 advert owns the slot
                Log.Warn("RX: 8881 retry attempt");
                try { beacon.StopAdvertising(); } catch { }
                try { beacon.StartAdvertising(new GattServiceProviderAdvertisingParameters
                {
                    IsDiscoverable = true,
                    IsConnectable = true,
                }); } catch (Exception ex) { Log.Warn($"RX: 8881 beacon retry failed: {ex.Message}"); }
                await Task.Delay(1500);
                if (beacon.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("RX: 8881 advertisement retry failed", ex); }
        finally { Interlocked.Exchange(ref _beaconRetrying, 0); }
    }

    /// <summary>Raised when a transfer completes: (senderName, savedFilePaths).</summary>
    public event Action<string, IReadOnlyList<string>>? TransferCompleted;

    /// <summary>Raised when a transfer fails: (senderName, errorMessage).</summary>
    public event Action<string, string>? TransferFailed;

    private void State(string msg)
    {
        Log.Info($"RX: {msg}");
        try { StateChanged?.Invoke(msg); } catch { }
    }

    /// <summary>
    /// Checks the Windows Bluetooth local name.
    /// Stock 互传 (d8/o.java) looks for 'dddddd + flag + display' (length ≥ 8).
    /// </summary>
    public static string BluetoothName(string? customName = null)
    {
        var rawName = !string.IsNullOrWhiteSpace(customName) ? customName : Environment.MachineName;
        if (rawName.Length >= 8 && char.IsDigit(rawName[0]) && char.IsDigit(rawName[1]))
            return rawName;
        return "0000001" + rawName;
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;

        Directory.CreateDirectory(SaveDirectory);

        // Build the complete protocol GATT database BEFORE opening the connectable
        // discovery beacon. Android caches GATT aggressively; exposing 8881 while 9999
        // is still being added can leave a peer with a transient/incomplete database.
        // All OShare control characteristics are deliberately Plain: this protocol is
        // pairless and must never require a Bluetooth bond.
        var svc = await GattServiceProvider.CreateAsync(
            new Guid("00009999-0000-1000-8000-00805f9b34fb"));
        if (svc.Error != BluetoothError.Success)
            throw new InvalidOperationException($"RX: 9999 provider failed: {svc.Error}");

        var hs = await svc.ServiceProvider.Service.CreateCharacteristicAsync(
            new Guid("00009898-0000-1000-8000-00805f9b34fb"),
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                ReadProtectionLevel = GattProtectionLevel.Plain,
                WriteProtectionLevel = GattProtectionLevel.Plain,
            });
        if (hs.Error != BluetoothError.Success)
            throw new InvalidOperationException($"RX: 0x9898 create failed: {hs.Error}");
        _handshake = hs.Characteristic;
        _handshake.ReadRequested += OnHandshakeRead;

        var cmd = await svc.ServiceProvider.Service.CreateCharacteristicAsync(
            new Guid("00009896-0000-1000-8000-00805f9b34fb"),
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse,
                ReadProtectionLevel = GattProtectionLevel.Plain,
                WriteProtectionLevel = GattProtectionLevel.Plain,
            });
        if (cmd.Error != BluetoothError.Success)
            throw new InvalidOperationException($"RX: 0x9896 create failed: {cmd.Error}");
        _cmd = cmd.Characteristic;
        _cmd.WriteRequested += OnCommandWrite;

        var notify = await svc.ServiceProvider.Service.CreateCharacteristicAsync(
            new Guid("00009895-0000-1000-8000-00805f9b34fb"),
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Notify,
                ReadProtectionLevel = GattProtectionLevel.Plain,
                WriteProtectionLevel = GattProtectionLevel.Plain,
            });
        if (notify.Error != BluetoothError.Success)
            throw new InvalidOperationException($"RX: 0x9895 create failed: {notify.Error}");
        _notify = notify.Characteristic;
        _notify.SubscribedClientsChanged += (_, _) =>
            State($"0x9895 subscribers = {_notify?.SubscribedClients.Count ?? 0}");

        _service = svc.ServiceProvider;
        _service.AdvertisementStatusChanged += (_, e) => State($"9999 adv -> {e.Status}");
        _service.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsDiscoverable = false,
            IsConnectable = false,
        });

        // Open the discoverable/connectable 8881 beacon only after 9999 is ready.
        var beacon = await GattServiceProvider.CreateAsync(
            new Guid("00008881-0000-1000-8000-00805f9b34fb"));
        if (beacon.Error != BluetoothError.Success)
            throw new InvalidOperationException($"RX: 8881 provider failed: {beacon.Error}");

        var beaconChar = await beacon.ServiceProvider.Service.CreateCharacteristicAsync(
            new Guid("00008882-0000-1000-8000-00805f9b34fb"),
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                ReadProtectionLevel = GattProtectionLevel.Plain,
                WriteProtectionLevel = GattProtectionLevel.Plain,
            });
        if (beaconChar.Error != BluetoothError.Success)
            throw new InvalidOperationException($"RX: 0x8882 create failed: {beaconChar.Error}");

        beacon.ServiceProvider.AdvertisementStatusChanged += (_, e) =>
        {
            State($"8881 beacon -> {e.Status}");
            try
            {
                if (e.Status == GattServiceProviderAdvertisementStatus.Started)
                {
                    BeaconStarted?.Invoke();
                }
                else if (e.Status == GattServiceProviderAdvertisementStatus.Aborted)
                {
                    BeaconAborted?.Invoke();
                    RetryBeaconAsync(beacon.ServiceProvider);
                }
            }
            catch { }
        };

        IsRunning = true;
        _beacon = beacon.ServiceProvider;
        State("Local GATT database ready (8881 + 9999, pairless/plain); starting connectable beacon");
        beacon.ServiceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsDiscoverable = true,
            IsConnectable = true,
        });

        State($"Receive server active (beacon 8881 + service 9999), BT Name='{BluetoothName(DeviceName)}'");
    }

    private async void OnHandshakeRead(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var request = await args.GetRequestAsync();
            if (request is null) return;

        var handshake = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["state"] = 0,
            ["key"] = _crypto.PublicKeyB64,
            ["version"] = Version,
            ["pv"] = 1,
            ["isFast"] = 0,
        });
        State($"Handshake read served ({handshake.Length}B)");
        var handshakeBytes = Encoding.UTF8.GetBytes(handshake);
        var offset = (int)request.Offset;
        var slice = offset >= handshakeBytes.Length ? Array.Empty<byte>() : handshakeBytes[offset..];
            try
            {
                request.RespondWithValue(ToBuffer(slice));
            }
            catch (Exception ex)
            {
                Log.Warn($"RX: handshake response skipped: {ex.Message}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("RX: handshake read failed", ex); }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnCommandWrite(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        GattWriteRequest? request = null;

        try
        {
            request = await args.GetRequestAsync();
            if (request is null) return;
            var reader = DataReader.FromBuffer(request.Value);
            var bytes = new byte[request.Value.Length];
            reader.ReadBytes(bytes);
            var json = Encoding.UTF8.GetString(bytes);
            State($"0x9896 write <- {json}");
            await HandleCommandAsync(json);
            // Responding to a write-without-response request throws an empty
            // WinRT error; only protocol-level (with-response) writes get one.
            if (request.Option == GattWriteOption.WriteWithResponse)
                request.Respond();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"RX: 0x9896 write failed: {ex.Message}");
            try { if (request.Option == GattWriteOption.WriteWithResponse) request.Respond(); } catch { }
        }
    }

    private async Task HandleCommandAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // State 1 from the phone: file offer
        if (root.TryGetProperty("key", out var keyEl))
        {
            _padPubKey = keyEl.GetString();
            if (string.IsNullOrEmpty(_padPubKey))
                throw new InvalidOperationException("State 1 offer has no public key");

            var secret = _crypto.DeriveSharedSecret(_padPubKey);
            _session = OShareCrypto.CbcKeyFromSecret(secret);

            var deviceName = "Phone";
            if (root.TryGetProperty("device_name", out var dnEl) && dnEl.ValueKind == JsonValueKind.String)
            {
                try
                {
                    deviceName = OShareCrypto.CbcDecryptFromB64(_session.Value.Key, _session.Value.Iv, dnEl.GetString() ?? "");
                }
                catch (Exception ex) { Log.Warn($"RX: device_name decrypt failed: {ex.Message}"); }
            }
            else if (root.TryGetProperty("dname", out var dEl) && dEl.ValueKind == JsonValueKind.String)
            {
                deviceName = dEl.GetString() ?? "Phone";
            }
            _padName = deviceName;

            var fileCount = root.TryGetProperty("number", out var numEl) ? numEl.GetString() : "?";
            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "file/*";
            State($"File offer from '{deviceName}': {fileCount} file(s), {type}");

            var accept = false;
            if (ConfirmIncoming != null)
                accept = await ConfirmIncoming(deviceName, type ?? "file/*", fileCount ?? "?");
            else
                accept = true;

            // The phone's iPad/PC BLE client (APK d8/j) treats "6" as
            // USE_LAN_NEED_CHECK.  "1" means USE_DEFAULT_ACCESSORY and lets
            // the phone fall back to creating a SoftAP, which is the path that
            // kept failing here.  This receiver is the iPad role, so request
            // LAN explicitly.  PC -> phone sending uses a different GATT
            // client and is untouched.
            await NotifyAsync(accept ? "6" : "3");
            State(accept ? "Incoming transfer accepted — requesting iPad LAN mode" : "Incoming transfer rejected");
            return;
        }

        // State 3: Phone's WLAN endpoint offer. Two wire formats arrive here:
        //  1. ip/port only — LAN mode; our address is echoed back via wlan_accept.
        //  2. ssid/psk/ip/port — the phone selected its own hotspot path, so
        //     the PC joins that network and continues with the same receiver.
        if (root.TryGetProperty("wlan", out _) ||
            (root.TryGetProperty("ip", out _) && root.TryGetProperty("port", out _)))
        {
            if (_session == null) throw new InvalidOperationException("WLAN offer received without active session");
            var ip = DecryptField(root, "ip");
            var port = DecryptField(root, "port");
            if (string.IsNullOrEmpty(ip) || !int.TryParse(port, out var portNum))
                throw new InvalidOperationException($"WLAN offer decrypt failed (ip='{ip}', port='{port}')");

            var ssid = DecryptField(root, "ssid");
            var psk = DecryptField(root, "psk");
            if (!string.IsNullOrEmpty(ssid) && !string.IsNullOrEmpty(psk))
            {
                await HandleHotspotOfferAsync(ssid, psk, ip, portNum);
                return;
            }

            // The phone's BLE client (APK d8/j, Z==3) blocks in LAN mode until it
            // receives this notify: without "wlan_accept":true it never calls
            // startFileTransfer, so the WebSocket connects but no frames ever flow.
            // The ip must be our own LAN address, encrypted with the same session
            // key/iv the phone used for its state-3 write (softap/a.g() wire format).
            var freq = root.TryGetProperty("freq", out var freqEl) && freqEl.ValueKind == JsonValueKind.String
                ? freqEl.GetString() : null;
            var lan = LanInfo.Detect();
            var lanIp = lan?.IpString;
            if (lan is not null)
                Log.Info($"RX: wlan_accept adapter: '{lan.Name}' ip={lan.IpString} mac={lan.MacColonLower} " +
                         "(must be on the same subnet the phone's Wi-Fi can reach)");
            var acceptPayload = new Dictionary<string, object> { ["wlan"] = "wlan" };
            if (!string.IsNullOrEmpty(lanIp) && _session != null)
            {
                acceptPayload["wlan_accept"] = true;
                acceptPayload["ip"] = OShareCrypto.CbcEncryptToB64(
                    _session.Value.Key, _session.Value.Iv, lanIp);
                if (!string.IsNullOrEmpty(freq)) acceptPayload["freq"] = freq;
            }
            else
            {
                acceptPayload["wlan_accept"] = false;
                Log.Error("RX: no LAN adapter for wlan_accept — phone will abort the transfer");
            }

            // OnePlus Share 16.10.61 race (l9/n): the moment the phone receives
            // wlan_accept it writes action:0:wlanBandwidth to the WebSocket and
            // arms a 1 s timer. If the PC connects afterwards the control frame
            // is lost and the phone aborts with check_wlan_band_failed. So:
            // UDP listener first, WebSocket pre-connect second, wlan_accept last.
            StartBandwidthEcho(portNum);

            ClientWebSocket? preconnected = null;
            if (acceptPayload["wlan_accept"] is true)
            {
                preconnected = await ReceiveSession.PreConnectAsync(ip, portNum, TimeSpan.FromSeconds(2.5), State);
                if (preconnected is null)
                    Log.Warn("RX: WLAN WS pre-connect failed — falling back to connect-after-accept");
            }

            await NotifyAsync(JsonSerializer.Serialize(acceptPayload));
            State($"Accepted LAN transfer at {lanIp ?? "(no adapter)"}");
            State($"Phone LAN endpoint: {ip}:{portNum} — {(preconnected is not null ? "WebSocket pre-connected" : "connecting after accept")}");

            if (acceptPayload["wlan_accept"] is false)
                return;

            _lanPullCts?.Cancel();
            _lanPullCts = new CancellationTokenSource();
            var lanPullCt = _lanPullCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    var files = await ReceiveSession.PullAsync(
                        ip, portNum, _padName, SaveDirectory,
                        State,
                        (done, total) => TransferProgress?.Invoke(done, total),
                        lanPullCt, preconnected,
                        metadata => TransferMetadata?.Invoke(metadata));

                    TransferCompleted?.Invoke(_padName, files);
                }
                catch (OperationCanceledException)
                {
                    State("LAN pull abandoned — switching to the phone's hotspot");
                }
                catch (Exception ex)
                {
                    Log.Error($"RX: Pull transfer failed: {ex.Message}");
                    State($"Transfer failed: {ex.Message}");
                    TransferFailed?.Invoke(_padName, ex.Message);
                }
            });
        }
    }

    private int _hotspotJoining;
    private CancellationTokenSource? _lanPullCts;
    private CancellationTokenSource? _hotspotCts;

    public void CancelTransfer()
    {
        _lanPullCts?.Cancel();
        _hotspotCts?.Cancel();
        Log.Info("RX: transfer cancellation requested");
    }

    /// <summary>
    /// The phone may provide a SoftAP hotspot. Join it on the PC's Wi-Fi
    /// adapter, then run the same WebSocket pull against that endpoint.
    /// </summary>
    private async Task HandleHotspotOfferAsync(string ssid, string psk, string ip, int port)
    {
        if (Interlocked.CompareExchange(ref _hotspotJoining, 1, 0) != 0)
        {
            State($"Hotspot join already in progress — ignoring offer for '{ssid}'");
            return;
        }

        State($"Phone hotspot offer: SSID '{ssid}' at {ip}:{port} — joining…");
        _lanPullCts?.Cancel();
        _hotspotCts?.Cancel();
        _hotspotCts = new CancellationTokenSource();
        var hotspotCt = _hotspotCts.Token;
        try
        {
            await WifiJoiner.ConnectAsync(ssid, psk, ip, port, State, hotspotCt);
            var files = await ReceiveSession.PullAsync(
                ip, port, _padName, SaveDirectory,
                State,
                (done, total) => TransferProgress?.Invoke(done, total), hotspotCt,
                metadata => TransferMetadata?.Invoke(metadata));
            TransferCompleted?.Invoke(_padName, files);
        }
        catch (OperationCanceledException)
        {
            State("Transfer cancelled");
        }
        catch (Exception ex)
        {
            Log.Error($"RX: Hotspot transfer failed: {ex.Message}");
            State($"Transfer failed: {ex.Message}");
            TransferFailed?.Invoke(_padName, ex.Message);
        }
        finally
        {
            WifiJoiner.RestoreAdapter(State);
            _hotspotCts?.Dispose();
            _hotspotCts = null;
            Interlocked.Exchange(ref _hotspotJoining, 0);
        }
    }

    /// <summary>
    /// Echoes the phone's UDP bandwidth-probe flood back to its source
    /// (APK l9/u.g + l9/u.f: the phone sends 256 × 4096 B to our IP on the
    /// port below and needs ≥ 256 KB echoed within its 1 s check window).
    /// The flood targets the phone's own task port + 1 (the value it stores
    /// per task); 8960 (8959 + 1) is the protocol default and bound as well.
    /// </summary>
    private void StartBandwidthEcho(int phonePort)
    {
        _ = Task.Run(async () =>
        {
            Interlocked.Exchange(ref _bandPackets, 0);
            Interlocked.Exchange(ref _bandBytes, 0);
            Interlocked.Exchange(ref _bandStartTicks, DateTime.UtcNow.Ticks);
            var ports = new[] { phonePort + 1, 8960 };
            var clients = new List<UdpClient>();
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                foreach (var port in ports.Distinct())
                {
                    try
                    {
                        clients.Add(new UdpClient(port));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"RX: band echo bind UDP {port} failed: {ex.Message}");
                    }
                }
                if (clients.Count == 0) return;
                State($"Bandwidth probe echo listening on UDP {string.Join("/", clients.Select(c => ((System.Net.IPEndPoint)c.Client.LocalEndPoint!).Port))}");
                // the phone's check window is ~1 s — if nothing arrives by 3 s the
                // flood is not reaching us at all (wrong port / firewall / iface)
                _ = Task.Delay(3000).ContinueWith(_ =>
                {
                    if (Interlocked.Read(ref _bandPackets) == 0)
                        Log.Warn("RX: no band probe packets received within 3 s — the phone's UDP flood is NOT reaching this PC");
                });
                await Task.WhenAll(clients.Select(c => EchoLoopAsync(c, lifetime.Token)));
            }
            catch (Exception ex)
            {
                Log.Warn($"RX: band echo failed: {ex.Message}");
            }
            finally
            {
                foreach (var c in clients) c.Dispose();
            }
        });
    }

    private static long _bandPackets;
    private static long _bandBytes;
    private static long _bandStartTicks;

    /// <summary>Received-bandwidth estimate (MB/s) for the wlanBandwidth ack the
    /// phone parses (APK l9/n.Q, field "speed" — tolerated even when 0.0).</summary>
    internal static double BandwidthSpeedMBps()
    {
        var bytes = Interlocked.Read(ref _bandBytes);
        var seconds = (DateTime.UtcNow.Ticks - _bandStartTicks) / (double)TimeSpan.TicksPerSecond;
        if (seconds <= 0) seconds = 0.001;
        return bytes / (1024.0 * 1024.0) / seconds;
    }

    private static async Task EchoLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var received = await udp.ReceiveAsync(ct);
                var n = Interlocked.Increment(ref _bandPackets);
                if (n == 1)
                    Log.Info($"RX: FIRST band probe packet from {received.RemoteEndPoint} ({received.Buffer.Length}B) — flood is arriving");
                else if (n <= 5 || n % 64 == 0)
                    Log.Info($"RX: band probe #{n} ({received.Buffer.Length}B) from {received.RemoteEndPoint}");
                Interlocked.Add(ref _bandBytes, received.Buffer.Length);
                await udp.SendAsync(received.Buffer, received.RemoteEndPoint, ct);
            }
            catch (OperationCanceledException)
            {
                Log.Info($"RX: band echo window closed — echoed {Interlocked.Read(ref _bandPackets)} packets / {Interlocked.Read(ref _bandBytes)} bytes total");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"RX: band echo send failed: {ex.Message}");
            }
        }
    }

    private string DecryptField(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return "";
        var enc = el.GetString();
        if (string.IsNullOrEmpty(enc) || _session == null) return enc ?? "";
        try
        {
            return OShareCrypto.CbcDecryptFromB64(_session.Value.Key, _session.Value.Iv, enc);
        }
        catch (Exception ex)
        {
            Log.Warn($"RX: decrypt '{name}' failed: {ex.Message}");
            return "";
        }
    }

    private async Task NotifyAsync(string text)
    {
        if (_notify is null) { Log.Warn("RX: 0x9895 notify skipped — characteristic missing"); return; }
        // "log printed" ≠ "phone received": check the per-subscriber results,
        // otherwise a dropped notification (no CCCD subscription, radio busy) looks like success
        var results = await _notify.NotifyValueAsync(ToBuffer(text));
        var failed = results.Where(r => r.Status != GattCommunicationStatus.Success).ToList();
        if (failed.Count == 0)
            State($"0x9895 notify -> '{text}' (Success ×{results.Count})");
        else
            Log.Error($"RX: 0x9895 notify '{text}' FAILED for {failed.Count}/{results.Count} subscribers: " +
                      string.Join(", ", failed.Select(r => r.Status)) +
                      " — the phone never saw it");
    }

    private static IBuffer ToBuffer(string s)
    {
        var w = new DataWriter();
        w.WriteBytes(Encoding.UTF8.GetBytes(s));
        return w.DetachBuffer();
    }

    private static IBuffer ToBuffer(byte[] bytes)
    {
        var w = new DataWriter();
        w.WriteBytes(bytes);
        return w.DetachBuffer();
    }

    public void Stop()
    {
        try { _beacon?.StopAdvertising(); } catch { }
        try { _service?.StopAdvertising(); } catch { }
        if (_handshake is not null) _handshake.ReadRequested -= OnHandshakeRead;
        if (_cmd is not null) _cmd.WriteRequested -= OnCommandWrite;
        _handshake = null;
        _cmd = null;
        _notify = null;
        _beacon = null;
        _service = null;
        IsRunning = false;
        State("Receive server stopped");
    }

    public void Dispose()
    {
        Stop();
        _crypto.Dispose();
    }
}
