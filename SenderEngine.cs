using System.Collections.Concurrent;
using OShareSender.Ui;

namespace OShareSender;

/// <summary>Glues scanner, advertiser, GATT link, transfer server, and receive engine together.</summary>
public sealed class SenderEngine : IDisposable, IAsyncDisposable
{
    public const int DefaultPort = 8959;

    public PhoneScanner Scanner { get; } = new();
    public AllianceAdvertiser Advertiser { get; } = new();
    public TransferServer Server { get; } = new();
    public HotspotManager Hotspot { get; } = new();
    public ReceiveGattServer Receiver { get; } = new();
    public OShareReceiveGattServer OShareReceiveGatt { get; } = new();
    public GattServiceFallbackAdvertiser FallbackAdvertiser { get; } = new();
    public LanDiscovery? LanDisc { get; private set; }

    public LanInfo? Lan { get; private set; }
    public int Port { get; private set; } = DefaultPort;

    /// <summary>4-hex id used in the OShare-style credential payload.</summary>
    public string SenderId { get; private set; } = "";

    private TransferTask? _staged;
    private OShareCrypto? _crypto;
    private bool _rxBeaconUp;      // 8881 connectable advert currently on air
    private bool _catAdvertUp;     // 9955 connectable advert currently on air
    private CancellationTokenSource? _sendCts;
    private CancellationTokenSource? _oShareReceiveCts;
    private CancellationTokenSource? _devicePruneCts;
    private readonly ConcurrentDictionary<string, string> _lanPeerIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _disposed;
    public bool ReceiveEnabled { get; private set; } = true;

    public event Action<string, string>? TransferStateChanged;   // (taskId, state)
    public event Action<PhoneDevice>? DeviceSeen;
    public event Action<long, long>? ReceiveProgress;
    public event Action<ReceiveMetadata>? ReceiveMetadataUpdated;
    public event Action<string, IReadOnlyList<string>>? ReceiveCompleted;
    public event Action<string, string>? ReceiveFailed;

    /// <summary>UI hook for incoming transfers: (senderName, mimeType, fileCount) -> accept/reject.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmIncomingTransfer { get; set; }

    /// <summary>Legacy hook for OShare Wi-Fi Direct incoming offer.</summary>
    public Func<OShareP2pOffer, Task<bool>>? ConfirmIncomingOShare { get; set; }

    public SenderEngine()
    {
        var saved = SettingsStore.Load();
        if (!string.IsNullOrWhiteSpace(saved.SaveDirectory) && Directory.Exists(saved.SaveDirectory))
            Receiver.SaveDirectory = saved.SaveDirectory;
        if (saved.ReceiveEnabled.HasValue)
            ReceiveEnabled = saved.ReceiveEnabled.Value;
        Log.Info($"Settings loaded: ReceiveEnabled={ReceiveEnabled}");

        var rnd = Random.Shared.Next(0x10000);
        SenderId = $"{rnd:x4}";
        Scanner.DeviceSeen += d => DeviceSeen?.Invoke(d);

        Server.DownloadStarted += taskId => TransferStateChanged?.Invoke(taskId, "phone is downloading");
        Server.DownloadProgress += (sent, total) =>
            TransferStateChanged?.Invoke(_staged?.TaskId ?? "?", $"{sent}/{total}");
        // HTTP body completion only means Windows finished writing bytes.
        // Receiver success is authoritative only after the phone sends status type=1.
        Server.DownloadFinished += taskId =>
            TransferStateChanged?.Invoke(taskId, "upload stream complete — waiting for phone confirmation");
        Server.StatusReceived += (taskId, type, reason) =>
        {
            if (type == 1 && _staged?.TaskId == taskId) _staged.Complete = true;
            TransferStateChanged?.Invoke(taskId, $"status {type}: {reason}");
        };
        Server.TransferFailed += (taskId, reason) =>
            TransferStateChanged?.Invoke(taskId, $"send failed: {reason}");

        // Stock OEM 互传 Receiver wiring
        Receiver.StateChanged += state => TransferStateChanged?.Invoke("", $"receive: {state}");
        Receiver.TransferProgress += (done, total) => ReceiveProgress?.Invoke(done, total);
        Receiver.TransferMetadata += metadata => ReceiveMetadataUpdated?.Invoke(metadata);
        Receiver.TransferCompleted += (pad, files) => ReceiveCompleted?.Invoke(pad, files);
        Receiver.TransferFailed += (pad, err) => ReceiveFailed?.Invoke(pad, err);
        Receiver.ConfirmIncoming = (name, type, count) => ConfirmIncomingTransfer is null
            ? Task.FromResult(true)
            : ConfirmIncomingTransfer(name, type, count);

        // OShare app Wi-Fi Direct GATT receiver wiring
        OShareReceiveGatt.StateChanged += state => TransferStateChanged?.Invoke("", $"oshare-rx: {state}");
        OShareReceiveGatt.ConfirmIncoming = offer => ConfirmIncomingOShare is null
            ? Task.FromResult(false)
            : ConfirmIncomingOShare(offer);
        OShareReceiveGatt.OfferAccepted += offer => _ = PullIncomingOShareAsync(offer);

        // Windows allows only ONE advertising slot. The connectable GATT adverts
        // (8881 beacon / 9955 OShare) are the ones a phone can actually CONNECT
        // to, so they win and retry indefinitely; the non-connectable fallback
        // advert only fills visibility gaps — and only after a 30s grace delay so
        // a retrying GATT advert can claim the slot first.
        void UpdateCoordination()
        {
            // A raw, non-connectable advert can make the phone see the PC but
            // also steals the Windows advertising slot from the real GATT server.
            // Keep it paused while receive mode is active.
            FallbackAdvertiser.Pause();
        }
        Receiver.BeaconStarted += () => { _rxBeaconUp = true; UpdateCoordination(); };
        Receiver.BeaconAborted += () => { _rxBeaconUp = false; UpdateCoordination(); };
        OShareReceiveGatt.AdvertStarted += () => { _catAdvertUp = true; UpdateCoordination(); };
        OShareReceiveGatt.AdvertAborted += () => { _catAdvertUp = false; UpdateCoordination(); };

        // Windows may briefly abort one provider when the other provider claims
        // the single advertising slot. Let both providers retry; the old
        // receive path recovered this exact Started -> Aborted -> Started race.
        Receiver.RetryGate = () => true;
        OShareReceiveGatt.RetryGate = () => true;
    }

    public async Task StartAsync(int port)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Lan = LanInfo.Detect() ?? throw new InvalidOperationException(
            "No LAN adapter with a default gateway found — connect this PC to the same Wi-Fi router as the phone.");
        Port = port;

        // remove leftover hotspot-join profiles (from killed runs) so dead
        // "OShare-*" networks don't linger in the Windows Wi-Fi list
        _ = WifiJoiner.CleanupStaleProfiles();

        await Server.StartAsync(port);
        Advertiser.DeviceName = TruncateName(Advertiser.DeviceName);
        // Phone -> PC discovery uses the stock 0x8881 GATT beacon below.
        // Do not start the unrelated alliance advertisement here: some Windows
        // Bluetooth stacks reject or interfere with concurrent publishers.
        Advertiser.Stop();
        Receiver.DeviceName = Advertiser.DeviceName;
        const string bleState = "0x8881 GATT discovery beacon";
        Log.Info($"DISC: PC discovery state = {bleState}; BT Name='{ReceiveGattServer.BluetoothName(Advertiser.DeviceName)}'");

        // Let the connectable GATT services claim the single Windows advertising slot
        // first. Starting the non-connectable fallback here used to starve 8881 and
        // made the PC visible-but-unconnectable to phones.
        FallbackAdvertiser.StatusChanged += s => TransferStateChanged?.Invoke("", s);

        // Start stock OEM receive services only when the persisted setting says
        // receiving is enabled. The transfer HTTP server remains available for
        // PC -> phone sending in either mode.
        if (ReceiveEnabled)
        {
            try
            {
                Receiver.RestartAdvertiser = () =>
                {
                    try { Advertiser.Stop(); } catch { }
                    Thread.Sleep(200);
                    try { Advertiser.Start(Lan?.MacHex12 ?? ""); } catch { }
                };
                await Receiver.StartAsync();
                Log.Info("RX: receiver started because ReceiveEnabled=true");
            }
            catch (Exception ex)
            {
                Log.Warn($"RX: Stock OEM receive server start failed: {ex.Message}");
            }
        }
        else
        {
            Log.Info("RX: receiver kept stopped because ReceiveEnabled=false");
        }

        // Start SSDP LAN discovery
        try
        {
            LanDisc = new LanDiscovery(Lan.MacHex12) { LanIp = Lan.IpString, DeviceName = Advertiser.DeviceName };
            LanDisc.DeviceAnnounced += (ip, _, pdid, _, _) =>
            {
                if (!string.IsNullOrWhiteSpace(pdid))
                    _lanPeerIps[pdid.Replace(":", "").ToUpperInvariant()] = ip;
            };
            LanDisc.Start();
        }
        catch (Exception ex)
        {
            Log.Warn($"RX: LAN discovery start failed: {ex.Message}");
        }

        // Windows normally has one reliable connectable BLE advertising slot.
        // The phone scanner is filtered to the stock 0x8881 service. Starting
        // the optional 0x9955 provider here competes for that same slot and can
        // abort both providers, so keep it out of the automatic receive path.
        Log.Info("RX: 0x9955 provider disabled; keeping stock 0x8881 beacon priority");

        // Only use raw service-data visibility after the connectable providers had
        // time to start/retry. It is a visibility fallback, never the connection
        // endpoint for phone -> PC transfers.
        FallbackAdvertiser.Pause();

        Scanner.Start();
        _devicePruneCts ??= new CancellationTokenSource();
        _ = PruneDevicesLoopAsync(_devicePruneCts.Token);
        TransferStateChanged?.Invoke("", $"ready — {bleState} — LAN {Lan.IpString}:{port} — '{Advertiser.DeviceName}'");
    }

    private async Task PruneDevicesLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                Scanner.PruneStale();
        }
        catch (OperationCanceledException) { }
    }

    public async Task SetReceiveEnabledAsync(bool enabled)
    {
        if (ReceiveEnabled == enabled && enabled) return;
        ReceiveEnabled = enabled;
        SettingsStore.Save(Advertiser.DeviceName, Receiver.SaveDirectory, ReceiveEnabled);

        if (!enabled)
        {
            Advertiser.Stop();
            Receiver.Stop();
            OShareReceiveGatt.Stop();
            FallbackAdvertiser.Stop();
            TransferStateChanged?.Invoke("", "receive paused");
            Log.Info("Receive mode disabled: stopped advertiser and receivers.");
        }
        else
        {
            if (Lan is not null)
            {
                try { await Receiver.StartAsync(); Log.Info("RX: receiver started after enabling"); } catch (Exception ex) { Log.Warn($"RX restart failed: {ex.Message}"); }
                await Task.Delay(1500);
                // A running stock receiver may be briefly Aborted while its
                // 8881 provider retries. Do not start the competing 9955
                // provider during that window or both adverts will fight for
                // Windows' single connectable BLE slot indefinitely.
                if (!Receiver.IsRunning && !_rxBeaconUp)
                {
                    try { await OShareReceiveGatt.StartAsync(Lan.MacColonLower); } catch (Exception ex) { Log.Warn($"RX OShare restart failed: {ex.Message}"); }
                }
                FallbackAdvertiser.Pause();
            }
            TransferStateChanged?.Invoke("", "receive enabled");
            Log.Info("Receive mode enabled: restarted advertiser and receivers.");
        }
    }

    public void CancelTransfer()
    {
        _sendCts?.Cancel();
        _oShareReceiveCts?.Cancel();
        Receiver.CancelTransfer();
        Server.CancelActiveTransfer();
        TransferStateChanged?.Invoke(_staged?.TaskId ?? "", "transfer cancelled");
    }

    public void UpdateSaveDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        Receiver.SaveDirectory = dir;
        SettingsStore.Save(Advertiser.DeviceName, Receiver.SaveDirectory, ReceiveEnabled);
    }

    public async Task StopAsync()
    {
        _devicePruneCts?.Cancel();
        _devicePruneCts?.Dispose();
        _devicePruneCts = null;
        Scanner.Stop();
        Advertiser.Stop();
        Receiver.Stop();
        LanDisc?.Dispose();
        OShareReceiveGatt.Stop();
        FallbackAdvertiser.Stop();
        await Hotspot.StopAsync();
        await Server.StopAsync();
    }

    private static string TruncateName(string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        if (bytes.Length <= 16) return name;
        var take = 15;
        while (take > 0 && (bytes[take] & 0xC0) == 0x80) take--;
        return System.Text.Encoding.UTF8.GetString(bytes, 0, take);
    }

    public TransferTask? StageFiles(IEnumerable<string> files)
    {
        var task = new TransferTask
        {
            Files = files.ToList(),
            SenderName = Advertiser.DeviceName,
            SenderId = SenderId,
        };
        task.ComputeSize();
        _staged = task;
        Server.SetTask(task);
        PreparedCrcCache.Begin(task.Files);
        Log.Info($"staged {task.FileCount} file(s), {task.TotalSize} bytes, taskId={task.TaskId}");
        foreach (var f in task.Files)
            Log.Info($"  {f}");
        return task;
    }

    public void ClearStaged()
    {
        _staged = null;
        Server.SetTask(null);
        Log.Info("staged transfer cleared");
    }

    public async Task SendToAsync(PhoneDevice device, SendFlow flow = SendFlow.Auto)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!await _sendGate.WaitAsync(0))
            throw new InvalidOperationException("A transfer is already running.");
        try
        {
        if (_staged is null)
            throw new InvalidOperationException("No files staged to send.");

        if (Lan is null)
            throw new InvalidOperationException("LAN adapter is not ready.");

        using var transferCts = new CancellationTokenSource();
        _sendCts = transferCts;
        var ct = transferCts.Token;
        // OnePlus Share only promotes a peer after its common BLE parser has a
        // complete ScanRecord. Windows splits ADV + SCAN_RSP, so wait for our
        // reconstructed complete record and never retry a stale BLE address blindly.
        Task? hotspotPrewarmTask = null;
        if (flow == SendFlow.OShareHotspot || device.Kind == PhoneKind.OShare)
        {
            Log.Info("Hotspot: pre-warming mobile hotspot concurrently with BLE connection...");
            hotspotPrewarmTask = Hotspot.EnsureStartedAsync();
        }

        GattLink? connectedLink = null;
        Exception? lastConnectError = null;
        DateTimeOffset? requireAdvertisementNewerThan = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var wait = attempt == 1 ? TimeSpan.FromSeconds(6) : TimeSpan.FromSeconds(8);
            TransferStateChanged?.Invoke(
                _staged.TaskId,
                attempt == 1
                    ? $"waiting for {device.Name} complete BLE advertisement…"
                    : $"waiting for {device.Name} to advertise a fresh ready session…");

            var candidate = await Scanner.WaitForConnectableAsync(
                device,
                wait,
                requireAdvertisementNewerThan,
                ct);
            device = candidate;

            TransferStateChanged?.Invoke(
                _staged.TaskId,
                $"connecting to {candidate.Name} ({candidate.AddressStr}), advertisement generation {attempt}/3…");

            try
            {
                if (flow == SendFlow.Auto)
                {
                    try
                    {
                        // Phones that support the iOS/OConnect path stay on the fast
                        // pure-LAN 9999 flow used by existing working devices.
                        connectedLink = await GattLink.ConnectAsync(
                            candidate.Address,
                            SendFlow.OConnectLan,
                            1,
                            s => TransferStateChanged?.Invoke(_staged.TaskId, s),
                            ct,
                            candidate.AddressType);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception oconnectEx)
                    {
                        // Some OnePlus tablets advertise the normal alliance record and
                        // accept the BLE link, but tear down the iOS/common 9999 probe
                        // before Windows can discover it. The stock Android path is the
                        // separate 9955 service, which the tablet also creates. Reconnect
                        // explicitly through 9955 instead of treating 9999 Unreachable as
                        // proof that the whole GATT peer is unusable.
                        lastConnectError = oconnectEx;
                        Log.Warn($"BLE: OConnect 9999 unavailable on {candidate.AddressStr} ({oconnectEx.Message}); " +
                                 "trying stock alliance 9955 fallback");
                        TransferStateChanged?.Invoke(
                            _staged.TaskId,
                            $"OConnect unavailable on {candidate.Name}; trying stock 互传 BLE service…");

                        // Let Android finish the first GATT disconnect before Windows
                        // creates a fresh client for the stock service.
                        await Task.Delay(250, ct);
                        connectedLink = await GattLink.ConnectAsync(
                            candidate.Address,
                            SendFlow.OShareHotspot,
                            1,
                            s => TransferStateChanged?.Invoke(_staged.TaskId, s),
                            ct,
                            candidate.AddressType);
                        Log.Info($"BLE: stock alliance 9955 fallback connected to {candidate.AddressStr}");
                    }
                }
                else
                {
                    connectedLink = await GattLink.ConnectAsync(
                        candidate.Address,
                        flow,
                        1,
                        s => TransferStateChanged?.Invoke(_staged.TaskId, s),
                        ct,
                        candidate.AddressType);
                }
                break;
            }
            catch (Exception ex) when (attempt < 3)
            {
                lastConnectError = ex;
                requireAdvertisementNewerThan = candidate.LastCompleteAdvertisement;
                Log.Warn($"BLE: GATT for {candidate.AddressStr} was not ready ({ex.Message}); " +
                         "waiting for a fresh complete OnePlus advertisement before retrying");
            }
            catch (Exception ex)
            {
                lastConnectError = ex;
            }
        }

        if (connectedLink is null)
            throw new InvalidOperationException(
                $"BLE: phone never exposed a connectable GATT session after fresh advertisements — {lastConnectError?.Message}");

        using var link = connectedLink;
        _crypto ??= new OShareCrypto();

        var resolvedFlow = flow switch
        {
            SendFlow.OConnectLan => SendFlow.OConnectLan,
            SendFlow.OShareHotspot => SendFlow.OShareHotspot,
            _ => (link.OConnectReadChar != null && link.OConnectWriteChar != null)
                ? SendFlow.OConnectLan
                : SendFlow.OShareHotspot,
        };

        if (resolvedFlow == SendFlow.OConnectLan)
        {
            if (link.OConnectReadChar == null || link.OConnectWriteChar == null)
                throw new InvalidOperationException("Phone does not expose the OConnect (0x9999) service.");

            TransferStateChanged?.Invoke(_staged.TaskId, "OConnect transfer starting…");
            Server.PeerLooksStock = true;   // OConnect peers are stock 互传 receivers
            string? expectedPeerIp = null;
            if (device.DeviceId.Length >= 12)
                _lanPeerIps.TryGetValue(device.DeviceId[..12].ToUpperInvariant(), out expectedPeerIp);

            // Arm only after a real GATT session has been established and the stock
            // OConnect path was selected, but before state1/state3 can make the phone
            // open the WebSocket. This removes the race where the phone could reach
            // /websocket before the old lazy phoneConnected callback armed the task.
            Server.ArmTransfer(_staged, Lan.IpString, expectedPeerIp);

            await link.OConnectLanSendAsync(
                Lan,
                Port,
                _crypto,
                Advertiser.DeviceName,
                _staged.FileCount,
                s => TransferStateChanged?.Invoke(_staged.TaskId, s),
                phoneConnected: () => Server.WsConnected || _staged.Complete,
                waitForPhoneConnectedAsync: (timeout, token) => Server.WaitForPeerConnectedAsync(timeout, token),
                ct: ct);
            TransferStateChanged?.Invoke(_staged.TaskId, $"credentials sent to {device.Name} via LAN — waiting for the phone to connect");
        }

        if (resolvedFlow != SendFlow.OConnectLan)
        {
            var endpoint = link.Endpoints.FirstOrDefault(e => e.IsOShare) ?? link.Endpoints.FirstOrDefault();
            if (endpoint is null)
                throw new InvalidOperationException("Phone exposed no compatible GATT endpoint (neither OShare nor Alliance).");

            TransferStateChanged?.Invoke(_staged.TaskId, "starting Wi-Fi hotspot…");
            // The OShare app parses every WebSocket frame strictly and dies on the raw
            // 'files' trigger — only stock peers may receive it.
            Server.PeerLooksStock = !endpoint.IsOShare;
            if (hotspotPrewarmTask != null)
            {
                await hotspotPrewarmTask;
            }
            else
            {
                await Hotspot.EnsureStartedAsync();
            }
            TransferStateChanged?.Invoke(_staged.TaskId,
                $"hotspot '{Hotspot.Ssid}' up — the phone will switch Wi-Fi to it");

            Server.ArmTransfer(_staged, string.IsNullOrWhiteSpace(Hotspot.GatewayIp) ? null : Hotspot.GatewayIp);
            var credentialMode = endpoint.IsOShare
                ? CredentialMode.OShareLan
                : CredentialMode.StockAlliance;
            Log.Info($"BLE: using {(endpoint.IsOShare ? "OShare" : "stock alliance")} 9955 credentials");
            await link.SendCredentialsAsync(
                endpoint, credentialMode, endpoint.Status, Lan, Port, _crypto, SenderId,
                freq: 0,
                ssidOverride: Hotspot.Ssid,
                pskOverride: Hotspot.Psk,
                macOverride: Hotspot.Bssid,
                ct: ct);
            TransferStateChanged?.Invoke(_staged.TaskId, $"credentials sent to {device.Name} — waiting for the phone to connect");
        }

        // BLE credentials only authorize the phone. Keep the single-flight gate
        // held until the HTTP/WebSocket session reaches a terminal phone status.
        await Server.WaitForTransferCompletionAsync(_staged.TaskId, ct);
        }
        finally
        {
            _sendCts = null;
            _sendGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelTransfer();
        await StopAsync();
        _devicePruneCts?.Cancel();
        _devicePruneCts?.Dispose();
        _devicePruneCts = null;
        Scanner.Dispose();
        Advertiser.Dispose();
        Receiver.Dispose();
        LanDisc?.Dispose();
        OShareReceiveGatt.Dispose();
        FallbackAdvertiser.Dispose();
        await Hotspot.StopAsync();
        await Server.DisposeAsync();
        _crypto?.Dispose();
        _sendGate.Dispose();
    }

    private async Task PullIncomingOShareAsync(OShareP2pOffer offer)
    {
        using var receiveCts = new CancellationTokenSource();
        _oShareReceiveCts = receiveCts;
        try
        {
            TransferStateChanged?.Invoke("", $"joining Wi-Fi Direct group '{offer.Ssid}'…");
            using var connection = await OShareWifiDirectConnection.ConnectAsync(
                offer.Mac,
                state => TransferStateChanged?.Invoke("", $"receive: {state}"), receiveCts.Token);
            var files = await ReceiveSession.PullAsync(
                connection.RemoteHost,
                offer.Port,
                offer.SenderId,
                Receiver.SaveDirectory,
                state => TransferStateChanged?.Invoke("", $"receive: {state}"),
                (done, total) => ReceiveProgress?.Invoke(done, total),
                receiveCts.Token,
                metadata => ReceiveMetadataUpdated?.Invoke(metadata));
            ReceiveCompleted?.Invoke(offer.SenderId, files);
            TransferStateChanged?.Invoke("", $"received {files.Count} file(s)");
        }
        catch (OperationCanceledException)
        {
            TransferStateChanged?.Invoke("", "receive cancelled");
        }
        catch (Exception ex)
        {
            Log.Error("RX: OShare transfer failed", ex);
            ReceiveFailed?.Invoke(offer.SenderId, ex.Message);
            TransferStateChanged?.Invoke("", $"receive failed: {ex.Message}");
        }
        finally { _oShareReceiveCts = null; }
    }

    /// <summary>Blocks until async cleanup (sockets/GATT sessions/hotspot/firewall
    /// processes) actually completes, so a synchronous `using` caller can't proceed
    /// while those resources are still torn down in the background. Prefer
    /// `await using`/`DisposeAsync()` directly where an async context is available.</summary>
    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();
}
