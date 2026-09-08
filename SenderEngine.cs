using CatShareSender.Ui;

namespace CatShareSender;

/// <summary>Glues scanner, advertiser, GATT link, transfer server, and receive engine together.</summary>
public sealed class SenderEngine : IDisposable
{
    public const int DefaultPort = 8959;

    public PhoneScanner Scanner { get; } = new();
    public AllianceAdvertiser Advertiser { get; } = new();
    public TransferServer Server { get; } = new();
    public HotspotManager Hotspot { get; } = new();
    public ReceiveGattServer Receiver { get; } = new();
    public CatShareReceiveGattServer CatShareReceiveGatt { get; } = new();
    public GattServiceFallbackAdvertiser FallbackAdvertiser { get; } = new();
    public LanDiscovery? LanDisc { get; private set; }

    public LanInfo? Lan { get; private set; }
    public int Port { get; private set; } = DefaultPort;

    /// <summary>4-hex id used in the CatShare-style credential payload.</summary>
    public string SenderId { get; private set; } = "";

    private TransferTask? _staged;
    private OShareCrypto? _crypto;
    private bool _rxBeaconUp;      // 8881 connectable advert currently on air
    private bool _catAdvertUp;     // 9955 connectable advert currently on air
    private CancellationTokenSource? _sendCts;
    private CancellationTokenSource? _catShareReceiveCts;
    private CancellationTokenSource? _devicePruneCts;
    public bool ReceiveEnabled { get; private set; } = true;

    public event Action<string, string>? TransferStateChanged;   // (taskId, state)
    public event Action<PhoneDevice>? DeviceSeen;
    public event Action<long, long>? ReceiveProgress;
    public event Action<ReceiveMetadata>? ReceiveMetadataUpdated;
    public event Action<string, IReadOnlyList<string>>? ReceiveCompleted;
    public event Action<string, string>? ReceiveUrlReceived;     // (sender, url)
    public event Action<string, string>? ReceiveFailed;

    /// <summary>UI hook for incoming transfers: (senderName, mimeType, fileCount) -> accept/reject.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmIncomingTransfer { get; set; }

    /// <summary>Legacy hook for CatShare Wi-Fi Direct incoming offer.</summary>
    public Func<CatShareP2pOffer, Task<bool>>? ConfirmIncomingCatShare { get; set; }

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
        Server.DownloadFinished += taskId =>
        {
            TransferStateChanged?.Invoke(taskId, "download complete");
            if (_staged?.TaskId == taskId) _staged.Complete = true;
        };
        Server.UrlFinished += taskId =>
        {
            TransferStateChanged?.Invoke(taskId, "URL transfer complete");
            if (_staged?.TaskId == taskId) _staged.Complete = true;
        };
        Server.StatusReceived += (taskId, type, reason) =>
            TransferStateChanged?.Invoke(taskId, $"status {type}: {reason}");

        // Stock OEM 互传 Receiver wiring
        Receiver.StateChanged += state => TransferStateChanged?.Invoke("", $"receive: {state}");
        Receiver.TransferProgress += (done, total) => ReceiveProgress?.Invoke(done, total);
        Receiver.TransferMetadata += metadata => ReceiveMetadataUpdated?.Invoke(metadata);
        Receiver.TransferCompleted += (pad, files) => ReceiveCompleted?.Invoke(pad, files);
        Receiver.UrlReceived += (pad, url) => ReceiveUrlReceived?.Invoke(pad, url);
        Receiver.TransferFailed += (pad, err) => ReceiveFailed?.Invoke(pad, err);
        Receiver.ConfirmIncoming = (name, type, count) => ConfirmIncomingTransfer is null
            ? Task.FromResult(true)
            : ConfirmIncomingTransfer(name, type, count);

        // CatShare app Wi-Fi Direct GATT receiver wiring
        CatShareReceiveGatt.StateChanged += state => TransferStateChanged?.Invoke("", $"catshare-rx: {state}");
        CatShareReceiveGatt.ConfirmIncoming = offer => ConfirmIncomingCatShare is null
            ? Task.FromResult(false)
            : ConfirmIncomingCatShare(offer);
        CatShareReceiveGatt.OfferAccepted += offer => _ = PullIncomingCatShareAsync(offer);

        // Windows allows only ONE advertising slot. The connectable GATT adverts
        // (8881 beacon / 9955 CatShare) are the ones a phone can actually CONNECT
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
        CatShareReceiveGatt.AdvertStarted += () => { _catAdvertUp = true; UpdateCoordination(); };
        CatShareReceiveGatt.AdvertAborted += () => { _catAdvertUp = false; UpdateCoordination(); };

        // Windows may briefly abort one provider when the other provider claims
        // the single advertising slot. Let both providers retry; the old
        // receive path recovered this exact Started -> Aborted -> Started race.
        Receiver.RetryGate = () => true;
        CatShareReceiveGatt.RetryGate = () => true;
    }

    public async Task StartAsync(int port)
    {
        Lan = LanInfo.Detect() ?? throw new InvalidOperationException(
            "No LAN adapter with a default gateway found — connect this PC to the same Wi-Fi router as the phone.");
        Port = port;

        // remove leftover hotspot-join profiles (from killed runs) so dead
        // "CatShare-*" networks don't linger in the Windows Wi-Fi list
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
            CatShareReceiveGatt.Stop();
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
                    try { await CatShareReceiveGatt.StartAsync(Lan.MacColonLower); } catch (Exception ex) { Log.Warn($"RX CatShare restart failed: {ex.Message}"); }
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
        _catShareReceiveCts?.Cancel();
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
        CatShareReceiveGatt.Dispose();
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
        Log.Info($"staged {task.FileCount} file(s), {task.TotalSize} bytes, taskId={task.TaskId}");
        foreach (var f in task.Files)
            Log.Info($"  {f}");
        return task;
    }

    public TransferTask StageUrl(string url)
    {
        if (!TransferTask.TryNormalizeUrl(url, out var normalized, out var error))
            throw new ArgumentException(error, nameof(url));
        var task = new TransferTask
        {
            SharedUrl = normalized,
            SenderName = Advertiser.DeviceName,
            SenderId = SenderId,
        };
        task.ComputeSize();
        _staged = task;
        Server.SetTask(task);
        Log.Info($"staged URL ({task.TotalSize} bytes), taskId={task.TaskId}, host={task.FirstFileName}");
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
        if (_staged is null)
            throw new InvalidOperationException("Nothing is staged to send.");

        if (Lan is null)
            throw new InvalidOperationException("LAN adapter is not ready.");

        using var transferCts = new CancellationTokenSource();
        _sendCts = transferCts;
        var ct = transferCts.Token;
        try
        {
        TransferStateChanged?.Invoke(_staged.TaskId, $"connecting to {device.Name} ({device.AddressStr})…");

        // GattLink treats Auto as "enumerate BOTH services (9999 + 9955)"; the
        // capability-based resolution below then picks the flow.
        using var link = await GattLink.ConnectAsync(device.Address, flow, 3, s => TransferStateChanged?.Invoke(_staged.TaskId, s), ct);
        _crypto ??= new OShareCrypto();

        var resolvedFlow = flow switch
        {
            SendFlow.OConnectLan => SendFlow.OConnectLan,
            SendFlow.CatShareHotspot => SendFlow.CatShareHotspot,
            _ => (link.OConnectReadChar != null && link.OConnectWriteChar != null)
                ? SendFlow.OConnectLan
                : SendFlow.CatShareHotspot,
        };

        if (_staged.IsUrl && resolvedFlow != SendFlow.OConnectLan)
            throw new InvalidOperationException("URL sharing test currently requires the stock OConnect/互传 receiver path.");

        if (resolvedFlow == SendFlow.OConnectLan)
        {
            if (link.OConnectReadChar == null || link.OConnectWriteChar == null)
                throw new InvalidOperationException("Phone does not expose the OConnect (0x9999) service.");

            TransferStateChanged?.Invoke(_staged.TaskId, _staged.IsUrl ? "OConnect URL share starting…" : "OConnect transfer starting…");
            Server.PeerLooksStock = true;   // OConnect peers are stock 互传 receivers
            await link.OConnectLanSendAsync(
                Lan,
                Port,
                _crypto,
                Advertiser.DeviceName,
                _staged.FileCount,
                s => TransferStateChanged?.Invoke(_staged.TaskId, s),
                phoneConnected: () => Server.WsConnected || _staged.Complete,
                ct: ct);
            TransferStateChanged?.Invoke(_staged.TaskId, $"credentials sent to {device.Name} via LAN — waiting for the phone to connect");
            return;
        }

        var endpoint = link.Endpoints.FirstOrDefault(e => e.IsCatShare) ?? link.Endpoints.FirstOrDefault();
        if (endpoint is null)
            throw new InvalidOperationException("Phone exposed no compatible GATT endpoint (neither CatShare nor Alliance).");

        TransferStateChanged?.Invoke(_staged.TaskId, "starting Wi-Fi hotspot…");
        // The CatShare app parses every WebSocket frame strictly and dies on the raw
        // 'files' trigger — only stock peers may receive it.
        Server.PeerLooksStock = !endpoint.IsCatShare;
        await Hotspot.EnsureStartedAsync();
        TransferStateChanged?.Invoke(_staged.TaskId,
            $"hotspot '{Hotspot.Ssid}' up — the phone will switch Wi-Fi to it");

        await link.SendCredentialsAsync(
            endpoint, CredentialMode.CatShareLan, endpoint.Status, Lan, Port, _crypto, SenderId,
            freq: 0,
            ssidOverride: Hotspot.Ssid,
            pskOverride: Hotspot.Psk,
            macOverride: Hotspot.Bssid,
            ct: ct);
        }
        finally
        {
            _sendCts = null;
        }
        TransferStateChanged?.Invoke(_staged.TaskId, $"credentials sent to {device.Name} — waiting for the phone to connect");
    }

    public async Task DisposeAsync()
    {
        _devicePruneCts?.Cancel();
        _devicePruneCts?.Dispose();
        _devicePruneCts = null;
        Scanner.Dispose();
        Advertiser.Dispose();
        Receiver.Dispose();
        LanDisc?.Dispose();
        CatShareReceiveGatt.Dispose();
        FallbackAdvertiser.Dispose();
        await Hotspot.StopAsync();
        await Server.DisposeAsync();
    }

    private async Task PullIncomingCatShareAsync(CatShareP2pOffer offer)
    {
        using var receiveCts = new CancellationTokenSource();
        _catShareReceiveCts = receiveCts;
        try
        {
            TransferStateChanged?.Invoke("", $"joining Wi-Fi Direct group '{offer.Ssid}'…");
            using var connection = await CatShareWifiDirectConnection.ConnectAsync(
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
                metadata => ReceiveMetadataUpdated?.Invoke(metadata),
                url => ReceiveUrlReceived?.Invoke(offer.SenderId, url));
            if (files.Count > 0)
                ReceiveCompleted?.Invoke(offer.SenderId, files);
            TransferStateChanged?.Invoke("", files.Count > 0 ? $"received {files.Count} file(s)" : "received URL");
        }
        catch (OperationCanceledException)
        {
            TransferStateChanged?.Invoke("", "receive cancelled");
        }
        catch (Exception ex)
        {
            Log.Error("RX: CatShare transfer failed", ex);
            ReceiveFailed?.Invoke(offer.SenderId, ex.Message);
            TransferStateChanged?.Invoke("", $"receive failed: {ex.Message}");
        }
        finally { _catShareReceiveCts = null; }
    }

    public void Dispose()
    {
        _ = Task.Run(async () =>
        {
            try { await DisposeAsync(); }
            catch (Exception ex) { Log.Warn($"DisposeAsync faulted: {ex.Message}"); }
        });
    }
}
