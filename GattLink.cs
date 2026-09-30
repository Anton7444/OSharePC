using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace OShareSender;

/// <summary>What the phone told us on the 0x9954 status read.</summary>
public sealed class PhoneStatus
{
    public int State;
    public string Mac = "";
    public string PublicKey = "";
    public int? OShareVersion;   // present only for the OShare app
}

/// <summary>One GATT 9955 service instance exposed by the phone. Both the stock 互传
/// app AND the OShare app can host this same service UUID on one phone — the
/// 9954 payload (presence of "oShare") tells them apart.</summary>
public sealed class GattEndpoint
{
    public required GattCharacteristic StatusChar;
    public required GattCharacteristic P2pChar;
    public required PhoneStatus Status;
    public bool IsOShare => Status.OShareVersion is not null;
}

/// <summary>Which transfer flow to use — the two are fully isolated so neither
/// perturbs the other's phone-side state.</summary>
public enum SendFlow
{
    /// <summary>Decide from the device kind (which app is advertising).</summary>
    Auto,
    /// <summary>Stock 互传 via service 9999 (iOS-emulation, pure LAN, no hotspot).</summary>
    OConnectLan,
    /// <summary>OShare app via service 9955 + hotspot AP join.</summary>
    OShareHotspot,
}

/// <summary>Which credentials to write to 0x9953 (OShare flow only).</summary>
public enum CredentialMode
{
    /// <summary>OShare app (v7+): plain JSON, lanHost points at the PC over the router LAN.</summary>
    OShareLan,
    /// <summary>Stock alliance: AES-CTR encrypted ssid/psk/mac + ECDH key.</summary>
    StockAlliance
}

/// <summary>
/// GATT client link to a phone in receive mode. The phone may expose the alliance
/// service 00009955-0000-1000-8000-00805f9b34fb more than once (stock app + OShare
/// app each register their own); every instance is enumerated and classified so the
/// credentials land in the right app.
/// It ALSO enumerates service 00009999 (OPlus Connect / iOS 互传) whose
/// read-0x9998 → write-0x9896 state machine supports a pure-LAN flow:
/// state 4 {"ip","port"} makes the phone connect wss://ip:port directly.
/// </summary>
public sealed partial class GattLink : IDisposable
{
    public static readonly Guid ServiceUuid = new("00009955-0000-1000-8000-00805f9b34fb");
    public static readonly Guid CharStatusUuid = new("00009954-0000-1000-8000-00805f9b34fb");
    public static readonly Guid CharP2pUuid = new("00009953-0000-1000-8000-00805f9b34fb");

    public static readonly Guid OConnectServiceUuid = new("00009999-0000-1000-8000-00805f9b34fb");
    /// <summary>iOS/OConnect handshake read: responds {"state","key","version"} and
    /// arms the phone's state machine (N=1, transferType=1). NOT 0x9998 — that
    /// characteristic has no read handler (30s ATT timeout).</summary>
    public static readonly Guid OConnectReadUuid = new("00009897-0000-1000-8000-00805f9b34fb");
    public static readonly Guid OConnectWriteUuid = new("00009896-0000-1000-8000-00805f9b34fb");
    /// <summary>Phone → sender notifications: the account challenge (N=6) and the
    /// wlan-ip message after account ok.</summary>
    public static readonly Guid OConnectNotifyUuid = new("00009898-0000-1000-8000-00805f9b34fb");
    public static readonly Guid CccdUuid = new("00002902-0000-1000-8000-00805f9b34fb");

    /// <summary>com.heytap.accessory's PantaConnect "OSHARE_IBEACON_BUSINESS" GATT
    /// characteristic (k9.c.C in the decompiled OShare app) — an unencrypted JSON
    /// write channel where a peer just declares {"is_same_account": true, ...} and
    /// the receiver takes it at face value (see com.oplus.oshare.ble.impl.w9.b#b /
    /// #e, and OPPO_ACCOUNT_API_FINDINGS.md section 5g/5h). Only present on the
    /// phone's GATT table while its own iBeacon/cross-device-link subsystem is
    /// actively running — writing here is a best-effort opportunistic attempt, not
    /// something we can rely on being available every session.</summary>
    public static readonly Guid IBeaconCharUuid = new("00009892-0000-1000-8000-00805f9b34fb");

    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private readonly List<Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceService> _services = new();
    public List<GattEndpoint> Endpoints { get; } = new();
    public GattCharacteristic? OConnectReadChar { get; private set; }
    public GattCharacteristic? OConnectWriteChar { get; private set; }
    public GattCharacteristic? OConnectNotifyChar { get; private set; }
    public GattCharacteristic? IBeaconChar { get; private set; }
    /// <summary>True while the underlying LE link is still up.</summary>
    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
    /// <summary>The peer GAP device name as reported by Windows (may be empty).</summary>
    public string DeviceName => _device?.Name ?? "";
    /// <summary>0x9996 (receiver status/public key) and 0x9995 (cancel) of the 9999 service — used to
    /// clear a stale receive task on the phone.</summary>
    public GattCharacteristic? OConnectWifiChar { get; private set; }
    public GattCharacteristic? OConnectCancelChar { get; private set; }

    /// <summary>One-time best-effort LE bond with the peer. Contacts (聯絡人)-mode
    /// receivers require an encrypted link before their OShare GATT server responds;
    /// Everyone mode serves pairless/plain. Returns true when the device ends up
    /// paired (already paired, or the pairing completed).
    /// NOTE: a pre-existing CLASSIC bond (IsPaired=true) does NOT carry LE keys when
    /// it was created as BR/EDR-only (manual Windows pairing bonds audio only) — the
    /// peer's stack still tries and fails to encrypt, and GATT stays silent. So when
    /// paired we unpair first and re-pair to force fresh keys with cross-transport
    /// derivation.</summary>
    private static async Task<bool> TryPairOnceAsync(BluetoothLEDevice device, Action<string>? status)
    {
        try
        {
            var pairing = device.DeviceInformation.Pairing;
            if (pairing.IsPaired)
            {
                // A paired LE node is a usable bond (BR/EDR pairing with cross-transport key
                // derivation yields LE keys). Unpairing it destroys the only working link key.
                Log.Info("BLE: device already paired — keeping the existing bond");
                return true;
            }
            if (!pairing.CanPair)
            {
                Log.Info("BLE: peer reports CanPair=false — skipping pairing fallback");
                return false;
            }
            Log.Info("BLE: service discovery failed — attempting LE pairing (not used on the Contacts beacon path)");
            status?.Invoke("pairing for encrypted link…");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            DevicePairingResult? result = null;
            foreach (var attempt in new Func<Task<DevicePairingResult>>[]
                     {
                         () => pairing.PairAsync().AsTask(cts.Token),
                         () => pairing.PairAsync(DevicePairingProtectionLevel.None).AsTask(cts.Token),
                     })
            {
                try
                {
                    result = await attempt();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    Log.Warn("BLE: this pairing protection level is not supported — trying the next");
                    continue;
                }
                Log.Info($"BLE: pairing finished with status {result.Status} (protection used: {result.ProtectionLevelUsed})");
                if (result.Status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired)
                    return true;
                // a plain "Failed" may mean the chosen protection level was refused —
                // fall through to the next level before giving up
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"BLE: pairing attempt failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Waits for the device to advertise, via a live DeviceInformation watcher,
    /// then opens the BluetoothLEDevice from the reported id. FromBluetoothAddressAsync
    /// resolves from the system cache only, and raw advertiser beacons (the tablet's
    /// connectable 0x3339/0x686b senseless advertisements in Contacts mode) never enter
    /// that cache on their own — but an UNFILTERED BLE AEP enumeration forces the system
    /// to materialize AEPs for currently-advertising devices, whose ids FromIdAsync
    /// accepts directly. The address is matched against both byte orders because the
    /// AEP id embeds the peer address little-endian.</summary>
    private static async Task<BluetoothLEDevice?> WaitForAdvertisementDeviceAsync(
        ulong bluetoothAddress, BluetoothAddressType addressType, TimeSpan timeout, CancellationToken ct)
    {
        var normalHex = bluetoothAddress.ToString("X12");
        var reversedHex = string.Create(12, bluetoothAddress, (span, addr) =>
        {
            for (var i = 0; i < 6; i++)
            {
                var b = (byte)(addr >> (8 * i));
                span[i * 2] = (char)((b >> 4) < 10 ? '0' + (b >> 4) : 'A' + (b >> 4) - 10);
                span[i * 2 + 1] = (char)((b & 0xF) < 10 ? '0' + (b & 0xF) : 'A' + (b & 0xF) - 10);
            }
        });
        Log.Info($"BLE: cache lookup missed {PhoneDevice.FormatAddress(bluetoothAddress)} — running unfiltered AEP enumeration to catch its current advertisement");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = DeviceInformation.CreateWatcher(BluetoothLEDevice.GetDeviceSelector());
        void OnAdded(DeviceWatcher sender, DeviceInformation info)
        {
            if (info is null) return;
            var tail = info.Id.Substring(info.Id.LastIndexOf('-') + 1).Replace(":", "").ToUpperInvariant();
            if (tail == normalHex || tail == reversedHex)
                tcs.TrySetResult(info.Id);
        }
        watcher.Added += OnAdded;
        watcher.Stopped += (_, _) => tcs.TrySetCanceled();
        try
        {
            watcher.Start();
            string deviceId;
            try
            {
                deviceId = await tcs.Task.WaitAsync(timeout, ct);
            }
            catch (TimeoutException)
            {
                Log.Warn($"BLE: no advertisement from {PhoneDevice.FormatAddress(bluetoothAddress)} within {timeout.TotalSeconds:0}s (AEP enumeration)");
                return null;
            }
            Log.Info($"BLE: AEP enumeration caught {PhoneDevice.FormatAddress(bluetoothAddress)} — opening device");
            using var openCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var openLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, openCts.Token);
            return await BluetoothLEDevice.FromIdAsync(deviceId).AsTask(openLinked.Token);
        }
        finally
        {
            watcher.Added -= OnAdded;
            try { watcher.Stop(); } catch { }
        }
    }

    public static Task<GattLink> ConnectAsync(ulong bluetoothAddress, SendFlow flow) =>
        ConnectAsync(bluetoothAddress, flow, retries: 3, status: null, CancellationToken.None);

    /// <summary>Connect with retries — 'Unreachable' from GetGattServicesAsync is
    /// usually transient (address rotation, advertisement timing, RF).</summary>
    public static async Task<GattLink> ConnectAsync(ulong bluetoothAddress, SendFlow flow, int retries, Action<string>? status, CancellationToken ct = default, BluetoothAddressType addressType = BluetoothAddressType.Unspecified, bool fast = false)
    {
        Exception? last = null;
        var sawPreExistingLink = false;
        for (int attempt = 1; attempt <= retries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            status?.Invoke($"BLE connecting (attempt {attempt}/{retries})…");
            BluetoothLEDevice? device = null;
            GattSession? session = null;
            try
            {
                // FromBluetoothAddressAsync has no built-in timeout and is known to hang
                // indefinitely on some adapters/driver states when the target never
                // responds (observed live: "attempt 1/1" stuck forever with zero further
                // log output) — wrap it so a bad attempt surfaces as a failure the retry
                // loop can act on, instead of hanging the whole send forever.
                using var connectTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(fast ? 7 : 8));
                using var connectLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, connectTimeoutCts.Token);
                var fromAddressTask = addressType == BluetoothAddressType.Unspecified
                    ? BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress).AsTask(connectLinkedCts.Token)
                    : BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress, addressType).AsTask(connectLinkedCts.Token);
                try
                {
                    device = await fromAddressTask;
                }
                catch (OperationCanceledException) when (connectTimeoutCts.IsCancellationRequested)
                {
                    throw new InvalidOperationException("FromBluetoothAddressAsync timed out after 8s — device not found — is the phone still advertising?");
                }
                if (device is null)
                {
                    // FromBluetoothAddressAsync resolves from the SYSTEM CACHE only, and
                    // raw BLE advertisements (e.g. the tablet's connectable 0xFCF1
                    // senseless beacon in Contacts mode) never enter that cache — so it
                    // returns null even while the device is actively advertising.
                    // Fallback: a live DeviceInformation watcher for exactly this address;
                    // it fires Added on the next advertisement, and FromIdAsync connects.
                    device = await WaitForAdvertisementDeviceAsync(bluetoothAddress, addressType, TimeSpan.FromSeconds(fast ? 3 : 8), ct)
                             ?? throw new InvalidOperationException("device not found — is the phone still advertising?");
                }

                // Match the stock Android client lifecycle: establish a real LE/GATT
                // session first, then perform protocol service discovery. Do not race
                // a full uncached database walk against the physical link coming up.
                try
                {
                    // Another unguarded WinRT call observed hanging well past any
                    // reasonable link-establishment time on a flaky attempt — same
                    // fix as FromBluetoothAddressAsync above.
                    using var sessionTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(fast ? 4 : 8));
                    using var sessionLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionTimeoutCts.Token);
                    try
                    {
                        session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(sessionLinkedCts.Token);
                    }
                    catch (OperationCanceledException) when (sessionTimeoutCts.IsCancellationRequested)
                    {
                        Log.Warn("BLE: GattSession.FromDeviceIdAsync timed out after 8s");
                        session = null;
                    }
                    if (session is not null)
                    {
                        Log.Info($"BLE: GATT session created status={session.SessionStatus} pdu={session.MaxPduSize} canMaintain={session.CanMaintainConnection}");
                        // pdu > 23 at creation means an ATT MTU exchange already happened on
                        // this pair — i.e. an established link existed BEFORE this attempt.
                        // Either the phone connected inbound to our receive GATT server (its
                        // senseless flow), or a prior attempt's link hasn't dropped. BLE
                        // forbids a second link between the same address pair in the opposite
                        // role, so outbound discovery against such a peer just times out.
                        if (session.SessionStatus == GattSessionStatus.Active && session.MaxPduSize > 23)
                        {
                            sawPreExistingLink = true;
                            Log.Warn("BLE: link to this device was ALREADY established before connecting " +
                                     $"(mtu={session.MaxPduSize}) — the phone likely connected inbound to our " +
                                     "receive GATT server, or a previous attempt's link hasn't dropped; " +
                                     "outbound discovery may be refused by the peer");
                        }
                        if (session.CanMaintainConnection)
                        {
                            session.MaintainConnection = true;
                            if (session.SessionStatus != GattSessionStatus.Active)
                            {
                                var activeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                                void OnStatusChanged(GattSession s, GattSessionStatusChangedEventArgs e)
                                {
                                    if (e.Status == GattSessionStatus.Active)
                                        activeTcs.TrySetResult(true);
                                }
                                session.SessionStatusChanged += OnStatusChanged;
                                try
                                {
                                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
                                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                                    await activeTcs.Task.WaitAsync(linkedCts.Token);
                                }
                                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                                {
                                    // Timeout reached; proceed to service discovery
                                }
                                finally
                                {
                                    session.SessionStatusChanged -= OnStatusChanged;
                                }
                            }
                        }
                        Log.Info($"BLE: GATT session ready status={session.SessionStatus} pdu={session.MaxPduSize}");
                    }
                }
                catch (Exception ex) { Log.Warn($"BLE: GattSession unavailable ({ex.Message})"); }

                var discoveredServices = new List<GattDeviceService>();
                GattCommunicationStatus discoveryStatus;
                string discoveryLabel;

                // GetGattServicesForUuidAsync has been observed hanging up to ~45s
                // before finally failing with "Unreachable" (a known WinRT GATT
                // caching quirk) — cap each discovery call so one bad attempt doesn't
                // eat the whole retry budget, and treat a timeout the same as an
                // Unreachable status so the outer retry loop moves on to a fresh
                // attempt/candidate quickly instead of stalling the entire send.
                async Task<(GattCommunicationStatus Status, IReadOnlyList<GattDeviceService> Services)> DiscoverUuidAsync(Guid uuid)
                {
                    using var discoTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(fast ? 5 : 10));
                    using var discoLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, discoTimeoutCts.Token);
                    try
                    {
                        var result = await device!.GetGattServicesForUuidAsync(uuid, BluetoothCacheMode.Uncached).AsTask(discoLinkedCts.Token);
                        return (result.Status, result.Services);
                    }
                    catch (OperationCanceledException) when (discoTimeoutCts.IsCancellationRequested)
                    {
                        Log.Warn($"BLE: GetGattServicesForUuidAsync({uuid}) timed out after 10s — treating as Unreachable");
                        return (GattCommunicationStatus.Unreachable, Array.Empty<GattDeviceService>());
                    }
                }

                if (flow == SendFlow.OShareHotspot)
                {
                    Log.Info("BLE: discovering target alliance service 9955 only");
                    var result = await DiscoverUuidAsync(ServiceUuid);
                    discoveryStatus = result.Status;
                    discoveryLabel = "9955";
                    if (result.Status == GattCommunicationStatus.Success)
                        discoveredServices.AddRange(result.Services);
                }
                else
                {
                    Log.Info("BLE: discovering target OConnect service 9999 only");
                    var oconnect = await DiscoverUuidAsync(OConnectServiceUuid);
                    discoveryStatus = oconnect.Status;
                    discoveryLabel = "9999";
                    if (oconnect.Status == GattCommunicationStatus.Success)
                        discoveredServices.AddRange(oconnect.Services);

                    // Auto can also target OShare. Probe 9955 only if 9999 was
                    // queried successfully and is genuinely absent. Never start a
                    // second discovery after an already-failed physical link.
                    if (flow == SendFlow.Auto &&
                        discoveryStatus == GattCommunicationStatus.Success &&
                        discoveredServices.Count == 0)
                    {
                        Log.Info("BLE: service 9999 absent; probing 9955 fallback");
                        var alliance = await DiscoverUuidAsync(ServiceUuid);
                        discoveryStatus = alliance.Status;
                        discoveryLabel = "9955";
                        if (alliance.Status == GattCommunicationStatus.Success)
                            discoveredServices.AddRange(alliance.Services);
                    }
                }

                if (discoveryStatus != GattCommunicationStatus.Success)
                {
                    // Live finding (2026-09-29): in Contacts (聯絡人) visibility the
                    // receiver's OShare GATT server answers ONLY over an encrypted link —
                    // the tablet's own stack logs an SMP encryption attempt for the peer
                    // and then serves nothing to an unencrypted client, which Windows
                    // reports as discovery Unreachable. Everyone (所有人) mode serves
                    // pairless/plain and needs none of this. Attempt a one-time LE bond
                    // so the peer can encrypt, then retry discovery.
                    // The fast (Contacts beacon) path never pairs: a bond makes the tablet drop Windows' first ATT
                    // request (30 s timeout) and each PairAsync attempt burns ~5 s; a failed attempt just retries
                    // against the next fresh beacon.
                    if (!fast && await TryPairOnceAsync(device!, status))
                    {
                        Log.Info("BLE: paired — retrying service discovery over the encrypted link");
                        if (flow == SendFlow.OShareHotspot)
                        {
                            var retryAlliance = await DiscoverUuidAsync(ServiceUuid);
                            discoveryStatus = retryAlliance.Status;
                            discoveryLabel = "9955";
                            if (retryAlliance.Status == GattCommunicationStatus.Success)
                                discoveredServices.AddRange(retryAlliance.Services);
                        }
                        else
                        {
                            var retryOConnect = await DiscoverUuidAsync(OConnectServiceUuid);
                            discoveryStatus = retryOConnect.Status;
                            discoveryLabel = "9999";
                            if (retryOConnect.Status == GattCommunicationStatus.Success)
                                discoveredServices.AddRange(retryOConnect.Services);
                            else
                            {
                                var retryAlliance = await DiscoverUuidAsync(ServiceUuid);
                                discoveryStatus = retryAlliance.Status;
                                discoveryLabel = "9955";
                                if (retryAlliance.Status == GattCommunicationStatus.Success)
                                    discoveredServices.AddRange(retryAlliance.Services);
                            }
                        }
                    }
                }

                if (discoveryStatus != GattCommunicationStatus.Success)
                    throw new InvalidOperationException($"target service {discoveryLabel} discovery failed ({discoveryStatus})");
                if (discoveredServices.Count == 0)
                    throw new InvalidOperationException($"target service {discoveryLabel} is not exposed by the device");

                var link = new GattLink();
                link._device = device;
                link._session = session;
                link._services.AddRange(discoveredServices);
                device = null;       // ownership moved to the link
                session = null;
                Log.Info($"BLE: GATT connected to {PhoneDevice.FormatAddress(bluetoothAddress)} '{link._device.Name}' (addressType={addressType}, flow={flow}, target={discoveryLabel}, attempt {attempt})");

                if (!fast) try
                {
                    var allServices = await link._device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                    if (allServices.Status == GattCommunicationStatus.Success)
                        Log.Info($"BLE: full service list: [{string.Join(", ", allServices.Services.Select(s => ShortUuid(s.Uuid)))}]");
                }
                catch (Exception ex) { Log.Warn($"BLE: full service dump failed: {ex.Message}"); }

                try
                {
                    await link.EnumerateAsync(flow, discoveredServices);
                    return link;
                }
                catch
                {
                    // the link already owns the device/session/services — don't leak them
                    try { link.Dispose(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                last = ex;
                Log.Warn($"BLE: connect attempt {attempt}/{retries} failed: {ex.Message}");
                // Release the keep-alive BEFORE disposing, or Windows holds the link
                // open and poisons the next attempt with a pre-existing connection.
                try { if (session is not null) session.MaintainConnection = false; } catch { }
                try { session?.Dispose(); } catch { }
                try { device?.Dispose(); } catch { }
                if (attempt < retries) await Task.Delay(2000, ct);
            }
        }
        var hint = sawPreExistingLink
            ? " A link to this device was already established before connecting (see BLEDIAG logs) — toggle the phone's Bluetooth off/on (or ignore/delete this PC from the phone's 互傳 device list) to drop it, then retry."
            : " Make sure the 互传/OShare receive screen stays open on the phone.";
        throw new InvalidOperationException($"BLE: connect failed after {retries} attempts — {last?.Message}.{hint}");
    }

    /// <summary>Enumerates the services needed by the selected flow. Called by
    /// ConnectAsync after a successful link; isolated per flow.</summary>
    private async Task EnumerateAsync(SendFlow flow, IReadOnlyList<Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceService> services)
    {
        // 0x9999 OPlus-Connect service (read 0x9897 / write 0x9896 / notify 0x9898) —
        // OConnect flow only. Isolation: never touched by the OShare flow.
        if (flow == SendFlow.OConnectLan || flow == SendFlow.Auto)
        foreach (var svc in services.Where(s => s.Uuid == OConnectServiceUuid))
        {
            try
            {
                // Single-shot range discovery: gets all characteristics in 1 BLE RTT instead of 3
                var charsResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (charsResult.Status == GattCommunicationStatus.Success)
                {
                    Log.Info($"BLE: service 9999 full characteristic list: [{string.Join(", ", charsResult.Characteristics.Select(c => ShortUuid(c.Uuid)))}]");
                    foreach (var c in charsResult.Characteristics)
                    {
                        if (c.Uuid == OConnectReadUuid) OConnectReadChar = c;
                        else if (c.Uuid == OConnectWriteUuid) OConnectWriteChar = c;
                        else if (c.Uuid == OConnectNotifyUuid) OConnectNotifyChar = c;
                        else if (c.Uuid == IBeaconCharUuid) IBeaconChar = c;
                        else if (c.Uuid == new Guid("00009996-0000-1000-8000-00805f9b34fb")) OConnectWifiChar = c;
                        else if (c.Uuid == new Guid("00009995-0000-1000-8000-00805f9b34fb")) OConnectCancelChar = c;
                    }
                    if (IBeaconChar is not null) Log.Info("BLE: 0x9892 iBeacon characteristic IS present this session");
                }

                // Fallback for drivers that don't return all characteristics in one range query
                if (OConnectReadChar is null)
                {
                    var r = await svc.GetCharacteristicsForUuidAsync(OConnectReadUuid, BluetoothCacheMode.Uncached);
                    if (r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0)
                        OConnectReadChar = r.Characteristics[0];
                }
                if (OConnectWriteChar is null)
                {
                    var w = await svc.GetCharacteristicsForUuidAsync(OConnectWriteUuid, BluetoothCacheMode.Uncached);
                    if (w.Status == GattCommunicationStatus.Success && w.Characteristics.Count > 0)
                        OConnectWriteChar = w.Characteristics[0];
                }
                if (OConnectNotifyChar is null)
                {
                    var n = await svc.GetCharacteristicsForUuidAsync(OConnectNotifyUuid, BluetoothCacheMode.Uncached);
                    if (n.Status == GattCommunicationStatus.Success && n.Characteristics.Count > 0)
                        OConnectNotifyChar = n.Characteristics[0];
                }

                if (OConnectReadChar != null && OConnectWriteChar != null && OConnectNotifyChar != null)
                {
                    Log.Info("BLE: OPlus-Connect service 9999 available (read 9897 / write 9896 / notify 9898)");
                }
            }
            catch (Exception ex) { Log.Warn($"BLE: 9999 service probe failed: {ex.Message}"); }
        }

        // The iBeacon business characteristic 0x9892 lives in the DCP service 0xBB15, not in 0x9999.
        if ((flow == SendFlow.OConnectLan || flow == SendFlow.Auto) && IBeaconChar is null && _device is not null)
        {
            try
            {
                var bb = await _device.GetGattServicesForUuidAsync(new Guid("0000bb15-0000-1000-8000-00805f9b34fb"), BluetoothCacheMode.Uncached);
                if (bb.Status == GattCommunicationStatus.Success)
                {
                    foreach (var svc in bb.Services)
                    {
                        _services.Add(svc);
                        var cr = await svc.GetCharacteristicsForUuidAsync(IBeaconCharUuid, BluetoothCacheMode.Uncached);
                        if (cr.Status == GattCommunicationStatus.Success && cr.Characteristics.Count > 0)
                        {
                            IBeaconChar = cr.Characteristics[0];
                            Log.Info("BLE: 0x9892 iBeacon characteristic IS present this session (service 0xBB15)");
                        }
                    }
                }
                else Log.Info($"BLE: BB15 service lookup status {bb.Status}");
            }
            catch (Exception ex) { Log.Warn($"BLE: BB15/9892 probe failed: {ex.Message}"); }
        }

        // 9955 alliance service — OShare flow only. Isolation: the OConnect flow
        // never reads 9954 here (a 0x9998 read arms the stock state machine; keep
        // the two flows from touching each other's phone-side state).
        if (flow != SendFlow.OConnectLan || flow == SendFlow.Auto)
        foreach (var svc in services.Where(s => s.Uuid == ServiceUuid))
        {
            try
            {
                GattCharacteristic? statusChar = null;
                GattCharacteristic? p2pChar = null;

                var charsResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (charsResult.Status == GattCommunicationStatus.Success)
                {
                    foreach (var c in charsResult.Characteristics)
                    {
                        if (c.Uuid == CharStatusUuid) statusChar = c;
                        else if (c.Uuid == CharP2pUuid) p2pChar = c;
                    }
                }

                if (statusChar is null)
                {
                    var statusResult = await svc.GetCharacteristicsForUuidAsync(CharStatusUuid, BluetoothCacheMode.Uncached);
                    if (statusResult.Status == GattCommunicationStatus.Success && statusResult.Characteristics.Count > 0)
                        statusChar = statusResult.Characteristics[0];
                }
                if (p2pChar is null)
                {
                    var p2pResult = await svc.GetCharacteristicsForUuidAsync(CharP2pUuid, BluetoothCacheMode.Uncached);
                    if (p2pResult.Status == GattCommunicationStatus.Success && p2pResult.Characteristics.Count > 0)
                        p2pChar = p2pResult.Characteristics[0];
                }

                if (statusChar is null || p2pChar is null)
                {
                    Log.Warn("BLE: found a 9955 service but its 9954/9953 characteristics are missing");
                    continue;
                }

                var read = await statusChar.ReadValueAsync(BluetoothCacheMode.Uncached);
                if (read.Status != GattCommunicationStatus.Success)
                {
                    Log.Warn($"BLE: read 9954 failed on one service instance ({read.Status})");
                    continue;
                }

                var json = ReadString(read.Value);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var status = new PhoneStatus
                {
                    State = root.TryGetProperty("state", out var st) ? st.GetInt32() : 0,
                    Mac = root.TryGetProperty("mac", out var mac) ? mac.GetString() ?? "" : "",
                    PublicKey = root.TryGetProperty("key", out var key) ? key.GetString() ?? "" : "",
                };
                if (root.TryGetProperty("oShare", out var cs) && cs.ValueKind == JsonValueKind.Number && cs.TryGetInt32(out var v))
                    status.OShareVersion = v;

                Endpoints.Add(new GattEndpoint
                {
                    StatusChar = statusChar,
                    P2pChar = p2pChar,
                    Status = status,
                });
                Log.Info($"BLE: 9954 ({(status.OShareVersion is null ? "stock 互传" : $"OShare v{status.OShareVersion}")}): {json}");
            }
            catch (Exception ex)
            {
                Log.Warn($"BLE: one 9955 service instance unusable: {ex.Message}");
            }
        }

        if (flow == SendFlow.OShareHotspot && Endpoints.Count == 0)
            throw new InvalidOperationException(
                "BLE: phone has no usable alliance service 9955 — open the 互传/OShare receive screen on the phone first.");
        if (Endpoints.Count > 1)
            Log.Info($"BLE: phone exposes {Endpoints.Count} alliance service instances " +
                     $"({string.Join(", ", Endpoints.Select(e => e.IsOShare ? "OShare" : "stock"))})");
    }

    /// <summary>Builds and writes the credential payload to the chosen endpoint.
    /// Returns the exact JSON sent.</summary>
    public async Task<string> SendCredentialsAsync(
        GattEndpoint endpoint, CredentialMode mode, PhoneStatus phone, LanInfo lan, int serverPort,
        OShareCrypto crypto, string senderId, int freq = 2412,
        string? ssidOverride = null, string? pskOverride = null, string? macOverride = null,
        CancellationToken ct = default)
    {
        string json;
        if (mode == CredentialMode.OShareLan)
        {
            // OShare app v7 (P2pReceiverService.runReceive): the phone joins the AP
            // named p2pInfo.ssid with p2pInfo.psk via WifiP2pManager.connect, then
            // connects to wss://<groupOwnerAddress>:<port>/websocket — so ssid/psk
            // MUST be the PC hotspot's. (v7 has no lanHost support; the field is
            // kept for any newer build that does.)
            var ssid = ssidOverride ?? "LAN";
            var psk = pskOverride ?? "LANLAN12";
            json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["id"] = senderId,
                ["ssid"] = ssid,
                ["psk"] = psk,
                ["mac"] = macOverride ?? lan.MacColonLower,
                ["port"] = serverPort,
                ["lanHost"] = lan.IpString,
                ["oShare"] = 7,
            });
        }
        else
        {
            // Stock alliance (ka/c.java i() + lb/a0.java): ECDH(P-256) with the phone's
            // public key from 9954, AES-CTR each of mac/ssid/psk, send our public key.
            // The phone then associates to the AP named ssid with psk (v8/h.java A():
            // WifiP2pConfig.Builder().setNetworkName(ssid).setPassphrase(psk)) and
            // connects to the hotspot gateway (= this PC) on serverPort.
            if (string.IsNullOrEmpty(phone.PublicKey))
                throw new InvalidOperationException("BLE: phone 9954 gave no public key");
            var sessionKey = crypto.DeriveSharedSecret(phone.PublicKey);
            var ssid = ssidOverride ?? "LANfastCon";
            var psk = pskOverride ?? "LANLAN12";
            var mac = macOverride ?? lan.MacColonLower;
            var encSsid = OShareCrypto.CtrEncryptToB64(sessionKey, ssid);
            var encPsk = OShareCrypto.CtrEncryptToB64(sessionKey, psk);
            var encMac = OShareCrypto.CtrEncryptToB64(sessionKey, mac);
            json = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["id"] = senderId,
                ["ssid"] = encSsid,
                ["psk"] = encPsk,
                ["mac"] = encMac,
                ["freq"] = freq,
                ["port"] = serverPort,
                ["key"] = crypto.PublicKeyB64,
            });
        }

        Log.Info($"BLE: 9953 <- {json}");
        var payload = System.Text.Encoding.UTF8.GetBytes(json);

        // Phones accumulate writes until the JSON parses; keep chunks ≤ MTU-3 (20 on default MTU 23).
        const int chunk = 18;
        for (int off = 0; off < payload.Length; off += chunk)
        {
            ct.ThrowIfCancellationRequested();
            var len = Math.Min(chunk, payload.Length - off);
            var slice = new byte[len];
            Array.Copy(payload, off, slice, 0, len);
            var result = await endpoint.P2pChar.WriteValueAsync(ToBuffer(slice), GattWriteOption.WriteWithResponse);
            if (result != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"BLE: 9953 write failed at offset {off} ({result})");
            await Task.Delay(30, ct);
        }
        Log.Info("BLE: credential JSON written completely");
        return json;
    }

    /// <summary>
    /// OPlus-Connect (iOS-style) LAN flow, mirroring d8/v.java's 0x9896 state machine:
    ///  1. read 0x9897 → {"state","key","version"} — phone marks us as the peer (N=1)
    ///  2. write 0x9896 state-1 (plain JSON, version ≥ 10302 keeps fields plaintext,
    ///     pv &lt; 5 skips account validation) → phone shows the receive card (N=2)
    ///  3. write 0x9896 state-3 WLAN offer {"ip","port"} (each value AES-CBC encrypted with the
    ///     ECDH session key, iOS variant) → phone connects wss://ip:port directly.
    /// The 5s timer after step 1 means state-1 must be written immediately.
    /// </summary>
    public async Task OConnectLanSendAsync(
        LanInfo lan, int serverPort, OShareCrypto crypto, string senderName, int fileCount,
        Action<string>? status = null, Func<bool>? phoneConnected = null,
        Func<TimeSpan, CancellationToken, Task<bool>>? waitForPhoneConnectedAsync = null,
        CancellationToken ct = default, string? oppoSsoid = null)
    {
        if (OConnectReadChar is null || OConnectWriteChar is null || OConnectNotifyChar is null)
            throw new InvalidOperationException(
                "BLE: phone does not expose the OPlus-Connect service 9999 (互传 receive screen must be open; " +
                "after a failed attempt the phone tears the service down until the screen is reopened).");

        // 0. Windows negotiates the ATT MTU automatically; read the effective PDU size from the active session
        int maxPdu = _session?.MaxPduSize ?? 247;
        Log.Info($"BLE: effective ATT MTU {maxPdu}");

        // Best-effort, independent of the rest of this flow: if the phone's iBeacon
        // business characteristic happened to be present this session, declare
        // same-account membership on it (see TryWriteIBeaconSameAccountAsync). This
        // cannot make anything worse — the write either succeeds or the
        // characteristic simply wasn't there, and the pv=1/pv=5 flow below runs
        // exactly as it would have anyway.
        // OFF by default: the pv=5 account proof below already makes the phone skip the
        // confirm card, and an unanswered 0x9892 write (the tablet's iBeacon handler only
        // answers while its share panel is active) blocks the whole ATT queue for 30 s.
        if (!string.IsNullOrEmpty(oppoSsoid) && Environment.GetEnvironmentVariable("OSHAREPC_IBEACON_WRITE") == "1")
        {
            var beaconDeviceId = SettingsStore.Current.OppoBleDeviceId ?? senderName;
            try { await TryWriteIBeaconSameAccountAsync(oppoSsoid, beaconDeviceId, senderName, ct); }
            catch (Exception ex) { Log.Warn($"BLE: iBeacon same-account write failed: {ex.Message}"); }
        }

        // 1. subscribe 0x9898 notifications (account challenge + wlan-ip messages).
        //    Every phone->PC message in the iOS-mode flow (account challenge, wlan
        //    offer, SoftAp info) is a notifyCharacteristicChanged on 0x9898 - without
        //    the CCCD write the phone silently drops all of them (Android 14+).
        var accountNotifyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wlanNotifyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNotify(GattCharacteristic c, GattValueChangedEventArgs e)
        {
            try
            {
                var text = ReadString(e.CharacteristicValue);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                Log.Info($"BLE: 9898 notify <- {text}");

                if (root.TryGetProperty("account_id", out _))
                    accountNotifyTcs.TrySetResult(text);

                if (root.TryGetProperty("wlan", out _) || root.TryGetProperty("ip", out _))
                    wlanNotifyTcs.TrySetResult(text);
            }
            catch (Exception ex) { Log.Warn($"BLE: 9898 notify parse failed: {ex.Message}"); }
        }
        OConnectNotifyChar.ValueChanged += OnNotify;

        // Prefer the characteristic-level WinRT CCCD API. Some OnePlus builds hide
        // the descriptor from enumeration even though the characteristic can notify.
        var notificationsSubscribed = false;
        try
        {
            var sub = await OConnectNotifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            notificationsSubscribed = sub == GattCommunicationStatus.Success;
            Log.Info($"BLE: 9898 notification subscription via WinRT = {sub}");
        }
        catch (Exception ex)
        {
            Log.Warn($"BLE: WinRT 9898 notification subscription failed: {ex.Message}");
        }

        if (!notificationsSubscribed)
        {
            var cccd = (await OConnectNotifyChar.GetDescriptorsAsync(BluetoothCacheMode.Uncached)).Descriptors
                .FirstOrDefault(d => d.Uuid == CccdUuid);
            Log.Info($"BLE: 0x9898 descriptors: [{string.Join(", ",
                (await OConnectNotifyChar.GetDescriptorsAsync()).Descriptors.Select(d => ShortUuid(d.Uuid)))}]");
            if (cccd is not null)
            {
                var w = await cccd.WriteValueAsync(ToBuffer(BitConverter.GetBytes((ushort)1)));
                notificationsSubscribed = w == GattCommunicationStatus.Success;
                Log.Info($"BLE: CCCD(9898) compatibility subscription = {w}");
            }
            else
            {
                Log.Warn("BLE: 0x9898 exposes no enumerable CCCD; official notify-driven flow unavailable on this Windows stack");
            }
        }

        try
        {
            // 2. read 0x9897 - phone replies {"state","key","version"} and arms N=1 (5s timer!)
            GattReadResult read;
            try
            {
                // A healthy receiver answers in well under a second. A listed-but-dead 9999 service (its app-side
                // server already closed) never answers; fail after 6s instead of the OS default of ~30s.
                read = await OConnectReadChar.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(4), ct);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("BLE: receiver not responding (read 9897 timed out)");
            }
            if (read.Status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"BLE: read 9897 failed ({read.Status})");
            var hsJson = ReadString(read.Value);
            Log.Info($"BLE: 9897 status: {hsJson}");
            using var hsDoc = JsonDocument.Parse(hsJson);
            var phonePub = hsDoc.RootElement.TryGetProperty("key", out var k) ? k.GetString() : null;
            var state = hsDoc.RootElement.TryGetProperty("state", out var st) ? st.GetInt32() : -1;
            if (string.IsNullOrEmpty(phonePub))
                throw new InvalidOperationException("BLE: 9897 reply has no key");
            if (state != 0 && OConnectWifiChar is not null && OConnectCancelChar is not null)
            {
                // A stale receive task on the phone (left by an aborted/timed-out earlier session, or by a
                // wake-up read) makes it answer "busy". Its own cancel command clears it: read 0x9996 first
                // (the phone only accepts writes from a device that has read), then write 02 to 0x9995.
                Log.Info($"BLE: phone reports busy (state={state}) — clearing its stale receive task (9996 read + 9995=02)");
                try
                {
                    await OConnectWifiChar.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(4), ct);
                    await OConnectCancelChar.WriteValueAsync(ToBuffer(new byte[] { 2 }), GattWriteOption.WriteWithResponse).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(4), ct);
                    await Task.Delay(700, ct);
                    read = await OConnectReadChar.ReadValueAsync(BluetoothCacheMode.Uncached);
                    if (read.Status == GattCommunicationStatus.Success)
                    {
                        hsJson = ReadString(read.Value);
                        Log.Info($"BLE: 9897 status after clear: {hsJson}");
                        using var hsDoc2 = JsonDocument.Parse(hsJson);
                        phonePub = hsDoc2.RootElement.TryGetProperty("key", out var k2) ? k2.GetString() : phonePub;
                        state = hsDoc2.RootElement.TryGetProperty("state", out var st2) ? st2.GetInt32() : state;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Log.Warn($"BLE: clearing the phone's stale receive task failed: {ex.Message}");
                }
            }
            if (state != 0)
                throw new InvalidOperationException($"BLE: phone busy (state={state}) - close other transfers and retry");

            var sessionKey = crypto.DeriveSharedSecret(phonePub);
            var (cbcKey, cbcIv) = OShareCrypto.CbcKeyFromSecret(sessionKey);

            // 3. state-1 (within the 5s timer): pv=5 selects the OConnect account path.
            //    Only announce pv=5 when we actually have both a real ssoid to offer AND
            //    a working 0x9898 notification subscription — otherwise stay on the
            //    proven pv=1 manual-accept path (see OConnectPv's own comment for why
            //    pv=5 was avoided by default: some builds expose no CCCD on 0x9898, and
            //    announcing pv=5 without being able to receive the phone's challenge
            //    would strand the transfer instead of falling back gracefully).
            var effectivePv = !string.IsNullOrEmpty(oppoSsoid) ? 5 : OConnectPv;
            Log.Info($"BLE: OConnect pv={effectivePv} (ssoid configured={!string.IsNullOrEmpty(oppoSsoid)}, notify subscribed={notificationsSubscribed})");
            var name = senderName ?? "PC";
            while (JsonSerializer.Serialize(BuildState1(crypto.PublicKeyB64, name, fileCount, effectivePv)).Length > maxPdu - 3 && name.Length > 4)
                name = name[..^2];
            var state1 = JsonSerializer.Serialize(BuildState1(crypto.PublicKeyB64, name, fileCount, effectivePv));
            Log.Info($"BLE: 9896 state1 <- {state1} ({state1.Length}B, mtu {maxPdu})");
            await WriteOConnectSingle(state1);

            // 4. (pv>=5 only) N=6: the phone notifies {"account_id":<enc>}. The phone's
            //    own handler (decompiled: com.oplus.oshare.ble.impl.w#j) decrypts OUR
            //    response and compares it against ITS OWN AccountManger.y() — i.e. the
            //    SHA-256 hex of the logged-in account's ssoid (see
            //    OPPO_ACCOUNT_API_FINDINGS.md section 5f/5g) — NOT against whatever it
            //    originally sent. Echoing the phone's own challenge back (the previous
            //    behavior here) can never match that check; it only silently falls
            //    through to the manual-accept path (com.oplus.oshare.ble.impl.w#A).
            //    Sending the real account's ssoid hash is what actually gets the phone
            //    to skip the manual "accept" tap.
            //    NOTE: the pad DOES send the challenge on the air (HCI log:09:59:46.614
            //    GATTS_HandleValueNotification) but Windows drops it - 0x9898 has no CCCD
            //    in the pad's GATT table so Windows never subscribes. With pv<5 we never
            //    need to receive anything: the card + accept path needs only writes.
            if (effectivePv >= 5)
            {
                status?.Invoke("waiting for phone account validation...");
                try
                {
                    // The phone's check (w#j) decrypts OUR reply and compares it with its own
                    // account id — the challenge content is irrelevant, and Windows can't even
                    // receive it (0x9898 has no CCCD). So wait just long enough for the phone to
                    // enter N=6 after state1, then answer with the uppercase account hash.
                    if (notificationsSubscribed)
                    {
                        try { await accountNotifyTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(1500), ct); } catch (TimeoutException) { }
                    }
                    else await Task.Delay(500, ct);

                    var proof = OppoAccount.OppoSsoidHash.Hash(oppoSsoid!).ToUpperInvariant();
                    var accountResp = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["account_id"] = OShareCrypto.CbcEncryptToB64(cbcKey, cbcIv, proof),
                    });
                    Log.Info($"BLE: 9896 account response <- {accountResp}");
                    await WriteOConnectSingle(accountResp);
                    await Task.Delay(300, ct);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    // The phone announced pv=5 support via our own state1, but never
                    // actually delivered the 0x9898 challenge in time (e.g. this specific
                    // build/session still can't reach the notify despite CCCD having
                    // subscribed OK) — fall through exactly like the pv=1 path rather
                    // than aborting the whole transfer. The phone likely still shows the
                    // manual accept prompt in this case.
                    Log.Warn("BLE: account challenge did not arrive in time — falling back to the manual accept prompt");
                }
            }
            else
            {
                // pv<5: OnePlus Share advances only after the user accepts the card,
                // then emits its WLAN transition on 9898.
                status?.Invoke("请在手机上点『接受』以确认接收…");
                Log.Info("BLE: state1 sent - waiting for official 9898 accept/WLAN transition");
            }

            var encIp = OShareCrypto.CbcEncryptToB64(cbcKey, cbcIv, lan.IpString);
            var encPort = OShareCrypto.CbcEncryptToB64(cbcKey, cbcIv, serverPort.ToString());
            var state3 = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["wlan"] = "wlan",
                ["wlan_accept"] = true,
                ["ip"] = encIp,
                ["port"] = encPort,
            });

            // OnePlus Share's real iOS peer writes the WLAN offer immediately after
  // state1, before the user presses Accept. The phone stores this state3 data
  // while the confirmation card is visible and consumes it after acceptance.
  // Waiting for a 9898 notification before the first state3 write creates a
  // Windows-only race because some stacks cannot expose/subscribe the CCCD.
  Log.Info($"BLE: 9896 state3 initial <- {state3}");
  await WriteOConnectSingle(state3);
  status?.Invoke("请在手机上点『接受』以确认接收…");

  // 9898 is diagnostic/observational for pv=1, not a prerequisite.
  if (notificationsSubscribed)
  {
      _ = wlanNotifyTcs.Task.ContinueWith(t =>
      {
          if (t.Status == TaskStatus.RanToCompletion)
              Log.Info($"BLE: official WLAN transition observed: {t.Result}");
      }, TaskScheduler.Default);
  }
  else
  {
      Log.Warn("BLE: 9898 notifications unavailable; continuing with official write-driven pv=1 flow");
  }

  // The real trace shows state3 being written again if the receiver has not
  // opened the WebSocket yet. Keep retries bounded and tied to this session.
  status?.Invoke("waiting for phone LAN/WebSocket connection...");
  var connectionDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(24);
  var nextState3Retry = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
  var state3RetryCount = 0;
  while (DateTimeOffset.UtcNow < connectionDeadline)
  {
      ct.ThrowIfCancellationRequested();
      if (phoneConnected?.Invoke() == true)
      {
          Log.Info("BLE: phone connected to our server - transfer session ready");
          return;
      }

      var maxWait = nextState3Retry - DateTimeOffset.UtcNow;
      if (maxWait <= TimeSpan.Zero) maxWait = TimeSpan.FromMilliseconds(50);
      else if (maxWait > TimeSpan.FromSeconds(3)) maxWait = TimeSpan.FromSeconds(3);

      if (waitForPhoneConnectedAsync != null)
      {
          var connected = await waitForPhoneConnectedAsync(maxWait, ct);
          if (connected || phoneConnected?.Invoke() == true)
          {
              Log.Info("BLE: phone connected to our server (signaled) - transfer session ready");
              return;
          }
      }
      else
      {
          await Task.Delay(maxWait < TimeSpan.FromMilliseconds(200) ? maxWait : TimeSpan.FromMilliseconds(200), ct);
          if (phoneConnected?.Invoke() == true)
          {
              Log.Info("BLE: phone connected to our server - transfer session ready");
              return;
          }
      }

      if (DateTimeOffset.UtcNow >= nextState3Retry && state3RetryCount < 4)
      {
          state3RetryCount++;
          Log.Info($"BLE: 9896 state3 retry {state3RetryCount}/4 <- {state3}");
          await WriteOConnectSingle(state3);
          nextState3Retry = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(4);
      }
  }
  throw new InvalidOperationException("BLE: phone did not establish the LAN/WebSocket transfer session after acceptance.");
        }
        finally
        {
            OConnectNotifyChar.ValueChanged -= OnNotify;
            try
            {
                await OConnectNotifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch (Exception ex)
            {
                Log.Warn($"BLE: 9898 unsubscribe failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Default OPlus-Connect protocol version when we announce state-1. pv&lt;5 makes
    /// the pad show the plain receive card (accept button) — the whole flow then needs
    /// BLE WRITES only. pv&gt;=5 uses the account challenge, whose challenge/wlan-offer
    /// messages arrive as notifications on 0x9898 — on some builds the pad's GATT table
    /// exposes no CCCD there and Windows cannot subscribe (HCI-verified: the pad puts
    /// the notification on the air but Windows drops it), so pv=5 is only announced
    /// (OConnectLanSendAsync's effectivePv) when a real ssoid is configured AND the
    /// 0x9898 subscription actually succeeded this session — otherwise this default
    /// (proven, manual-accept) value is used.
    /// </summary>
    private static readonly int OConnectPv = 1;

    private static Dictionary<string, object> BuildState1(string pubKey, string dname, int fileCount, int pv) => new()
    {
        ["key"] = pubKey,
        ["isFast"] = 0,
        ["version"] = "10302",
        ["pv"] = pv,
        ["type"] = "file/*",
        ["number"] = Math.Max(1, fileCount).ToString(),
        ["dname"] = dname,
        ["rdcode"] = "",
    };

    /// <summary>0x9896 messages are parsed per-write as complete JSON — single write only.</summary>
    private async Task WriteOConnectSingle(string json)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(json);
        var result = await OConnectWriteChar!.WriteValueAsync(ToBuffer(payload), GattWriteOption.WriteWithResponse);
        if (result != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"BLE: 9896 write failed ({result})");
    }

    /// <summary>Opportunistic write to the iBeacon business characteristic (0x9892),
    /// declaring same-account membership the way a real iPhone linked to the same
    /// account does (com.oplus.oshare.ble.impl.w9.b#e, method "iBeacon_advertise").
    /// The receiver (com.oplus.oshare.ble.impl.w9.b#b) trusts "is_same_account"
    /// verbatim with no further cryptographic check — see
    /// OPPO_ACCOUNT_API_FINDINGS.md section 5g/5h. No-op if 0x9892 wasn't found on
    /// this phone's GATT table this session (its iBeacon subsystem wasn't running).</summary>
    public async Task<bool> TryWriteIBeaconSameAccountAsync(string oppoSsoid, string deviceId, string deviceName, CancellationToken ct = default)
    {
        if (IBeaconChar is null)
        {
            Log.Info("BLE: 0x9892 not present this session — skipping iBeacon same-account write");
            return false;
        }

        var accountId = OppoAccount.OppoAccountBleHash.ComputeDsfAccountIdHex(oppoSsoid);
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["method"] = "iBeacon_advertise",
            ["data"] = new Dictionary<string, object>
            {
                ["cpv"] = "A",
                ["device_id"] = deviceId,
                ["device_name"] = deviceName,
                ["is_same_account"] = true,
                ["device_type"] = "6", // DeviceType.PC isn't in the observed set (4=iPhone); best-effort placeholder
                ["account_id"] = accountId,
            },
        });
        Log.Info($"BLE: 9892 iBeacon_advertise <- {json}");
        var payload = System.Text.Encoding.UTF8.GetBytes(json);
        var result = await IBeaconChar.WriteValueAsync(ToBuffer(payload), GattWriteOption.WriteWithResponse);
        Log.Info($"BLE: 9892 write result = {result}");
        return result == GattCommunicationStatus.Success;
    }

    private static string ReadString(IBuffer buffer)
    {
        var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static string ShortUuid(Guid uuid) => uuid.ToString()[..8];

    private static IBuffer ToBuffer(byte[] data)
    {
        var w = new DataWriter();
        w.WriteBytes(data);
        return w.DetachBuffer();
    }

    public void Dispose()
    {
        // MaintainConnection=true makes Windows keep re-establishing this link even
        // after the device object is gone; clearing it first is what actually lets
        // the link drop. Otherwise the NEXT connect attempt to the same phone sees
        // an already-established link and its discovery silently times out (BLE
        // forbids a second link between the same address pair in the other role).
        try { if (_session is not null) _session.MaintainConnection = false; } catch { }
        try { _session?.Dispose(); } catch { }
        _session = null;
        foreach (var svc in _services)
        {
            try { svc.Dispose(); } catch { }
        }
        _services.Clear();
        _device?.Dispose();
        _device = null;
    }
}
