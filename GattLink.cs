using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace CatShareSender;

/// <summary>What the phone told us on the 0x9954 status read.</summary>
public sealed class PhoneStatus
{
    public int State;
    public string Mac = "";
    public string PublicKey = "";
    public int? CatShareVersion;   // present only for the CatShare app
}

/// <summary>One GATT 9955 service instance exposed by the phone. Both the stock 互传
/// app AND the CatShare app can host this same service UUID on one phone — the
/// 9954 payload (presence of "catShare") tells them apart.</summary>
public sealed class GattEndpoint
{
    public required GattCharacteristic StatusChar;
    public required GattCharacteristic P2pChar;
    public required PhoneStatus Status;
    public bool IsCatShare => Status.CatShareVersion is not null;
}

/// <summary>Which transfer flow to use — the two are fully isolated so neither
/// perturbs the other's phone-side state.</summary>
public enum SendFlow
{
    /// <summary>Decide from the device kind (which app is advertising).</summary>
    Auto,
    /// <summary>Stock 互传 via service 9999 (iOS-emulation, pure LAN, no hotspot).</summary>
    OConnectLan,
    /// <summary>CatShare app via service 9955 + hotspot AP join.</summary>
    CatShareHotspot,
}

/// <summary>Which credentials to write to 0x9953 (CatShare flow only).</summary>
public enum CredentialMode
{
    /// <summary>CatShare app (v7+): plain JSON, lanHost points at the PC over the router LAN.</summary>
    CatShareLan,
    /// <summary>Stock alliance: AES-CTR encrypted ssid/psk/mac + ECDH key.</summary>
    StockAlliance
}

/// <summary>
/// GATT client link to a phone in receive mode. The phone may expose the alliance
/// service 00009955-0000-1000-8000-00805f9b34fb more than once (stock app + CatShare
/// app each register their own); every instance is enumerated and classified so the
/// credentials land in the right app.
/// It ALSO enumerates service 00009999 (OPlus Connect / iOS 互传) whose
/// read-0x9998 → write-0x9896 state machine supports a pure-LAN flow:
/// state 4 {"ip","port"} makes the phone connect wss://ip:port directly.
/// </summary>
public sealed class GattLink : IDisposable
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

    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private readonly List<Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceService> _services = new();
    public List<GattEndpoint> Endpoints { get; } = new();
    public GattCharacteristic? OConnectReadChar { get; private set; }
    public GattCharacteristic? OConnectWriteChar { get; private set; }
    public GattCharacteristic? OConnectNotifyChar { get; private set; }

    public static Task<GattLink> ConnectAsync(ulong bluetoothAddress, SendFlow flow) =>
        ConnectAsync(bluetoothAddress, flow, retries: 3, status: null, CancellationToken.None);

    /// <summary>Connect with retries — 'Unreachable' from GetGattServicesAsync is
    /// usually transient (address rotation, advertisement timing, RF).</summary>
    public static async Task<GattLink> ConnectAsync(ulong bluetoothAddress, SendFlow flow, int retries, Action<string>? status, CancellationToken ct = default, BluetoothAddressType addressType = BluetoothAddressType.Unspecified)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= retries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            status?.Invoke($"BLE connecting (attempt {attempt}/{retries})…");
            BluetoothLEDevice? device = null;
            GattSession? session = null;
            try
            {
                device = addressType == BluetoothAddressType.Unspecified
                    ? await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress)
                    : await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress, addressType);
                if (device is null)
                    throw new InvalidOperationException("device not found — is the phone still advertising?");

                // Match the stock Android client lifecycle: establish a real LE/GATT
                // session first, then perform protocol service discovery. Do not race
                // a full uncached database walk against the physical link coming up.
                try
                {
                    session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
                    if (session is not null)
                    {
                        Log.Info($"BLE: GATT session created status={session.SessionStatus} pdu={session.MaxPduSize} canMaintain={session.CanMaintainConnection}");
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

                if (flow == SendFlow.CatShareHotspot)
                {
                    Log.Info("BLE: discovering target alliance service 9955 only");
                    var result = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
                    discoveryStatus = result.Status;
                    discoveryLabel = "9955";
                    if (result.Status == GattCommunicationStatus.Success)
                        discoveredServices.AddRange(result.Services);
                }
                else
                {
                    Log.Info("BLE: discovering target OConnect service 9999 only");
                    var oconnect = await device.GetGattServicesForUuidAsync(OConnectServiceUuid, BluetoothCacheMode.Uncached);
                    discoveryStatus = oconnect.Status;
                    discoveryLabel = "9999";
                    if (oconnect.Status == GattCommunicationStatus.Success)
                        discoveredServices.AddRange(oconnect.Services);

                    // Auto can also target CatShare. Probe 9955 only if 9999 was
                    // queried successfully and is genuinely absent. Never start a
                    // second discovery after an already-failed physical link.
                    if (flow == SendFlow.Auto &&
                        discoveryStatus == GattCommunicationStatus.Success &&
                        discoveredServices.Count == 0)
                    {
                        Log.Info("BLE: service 9999 absent; probing 9955 fallback");
                        var alliance = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
                        discoveryStatus = alliance.Status;
                        discoveryLabel = "9955";
                        if (alliance.Status == GattCommunicationStatus.Success)
                            discoveredServices.AddRange(alliance.Services);
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
                try { session?.Dispose(); } catch { }
                try { device?.Dispose(); } catch { }
                if (attempt < retries) await Task.Delay(2000, ct);
            }
        }
        throw new InvalidOperationException($"BLE: connect failed after {retries} attempts — {last?.Message}. " +
            "Make sure the 互传/CatShare receive screen stays open on the phone.");
    }

    /// <summary>Enumerates the services needed by the selected flow. Called by
    /// ConnectAsync after a successful link; isolated per flow.</summary>
    private async Task EnumerateAsync(SendFlow flow, IReadOnlyList<Windows.Devices.Bluetooth.GenericAttributeProfile.GattDeviceService> services)
    {
        // 0x9999 OPlus-Connect service (read 0x9897 / write 0x9896 / notify 0x9898) —
        // OConnect flow only. Isolation: never touched by the CatShare flow.
        if (flow == SendFlow.OConnectLan || flow == SendFlow.Auto)
        foreach (var svc in services.Where(s => s.Uuid == OConnectServiceUuid))
        {
            try
            {
                // Single-shot range discovery: gets all characteristics in 1 BLE RTT instead of 3
                var charsResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (charsResult.Status == GattCommunicationStatus.Success)
                {
                    foreach (var c in charsResult.Characteristics)
                    {
                        if (c.Uuid == OConnectReadUuid) OConnectReadChar = c;
                        else if (c.Uuid == OConnectWriteUuid) OConnectWriteChar = c;
                        else if (c.Uuid == OConnectNotifyUuid) OConnectNotifyChar = c;
                    }
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

        // 9955 alliance service — CatShare flow only. Isolation: the OConnect flow
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
                if (root.TryGetProperty("catShare", out var cs) && cs.ValueKind == JsonValueKind.Number && cs.TryGetInt32(out var v))
                    status.CatShareVersion = v;

                Endpoints.Add(new GattEndpoint
                {
                    StatusChar = statusChar,
                    P2pChar = p2pChar,
                    Status = status,
                });
                Log.Info($"BLE: 9954 ({(status.CatShareVersion is null ? "stock 互传" : $"CatShare v{status.CatShareVersion}")}): {json}");
            }
            catch (Exception ex)
            {
                Log.Warn($"BLE: one 9955 service instance unusable: {ex.Message}");
            }
        }

        if (flow == SendFlow.CatShareHotspot && Endpoints.Count == 0)
            throw new InvalidOperationException(
                "BLE: phone has no usable alliance service 9955 — open the 互传/CatShare receive screen on the phone first.");
        if (Endpoints.Count > 1)
            Log.Info($"BLE: phone exposes {Endpoints.Count} alliance service instances " +
                     $"({string.Join(", ", Endpoints.Select(e => e.IsCatShare ? "CatShare" : "stock"))})");
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
        if (mode == CredentialMode.CatShareLan)
        {
            // CatShare app v7 (P2pReceiverService.runReceive): the phone joins the AP
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
                ["catShare"] = 7,
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
        CancellationToken ct = default)
    {
        if (OConnectReadChar is null || OConnectWriteChar is null || OConnectNotifyChar is null)
            throw new InvalidOperationException(
                "BLE: phone does not expose the OPlus-Connect service 9999 (互传 receive screen must be open; " +
                "after a failed attempt the phone tears the service down until the screen is reopened).");

        // 0. Windows negotiates the ATT MTU automatically; read the effective PDU size from the active session
        int maxPdu = _session?.MaxPduSize ?? 247;
        Log.Info($"BLE: effective ATT MTU {maxPdu}");

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
            var read = await OConnectReadChar.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (read.Status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"BLE: read 9897 failed ({read.Status})");
            var hsJson = ReadString(read.Value);
            Log.Info($"BLE: 9897 status: {hsJson}");
            using var hsDoc = JsonDocument.Parse(hsJson);
            var phonePub = hsDoc.RootElement.TryGetProperty("key", out var k) ? k.GetString() : null;
            var state = hsDoc.RootElement.TryGetProperty("state", out var st) ? st.GetInt32() : -1;
            if (string.IsNullOrEmpty(phonePub))
                throw new InvalidOperationException("BLE: 9897 reply has no key");
            if (state != 0)
                throw new InvalidOperationException($"BLE: phone busy (state={state}) - close other transfers and retry");

            var sessionKey = crypto.DeriveSharedSecret(phonePub);
            var (cbcKey, cbcIv) = OShareCrypto.CbcKeyFromSecret(sessionKey);

            // 3. state-1 (within the 5s timer): pv=5 selects the OConnect account path
            var name = senderName ?? "PC";
            while (JsonSerializer.Serialize(BuildState1(crypto.PublicKeyB64, name, fileCount)).Length > maxPdu - 3 && name.Length > 4)
                name = name[..^2];
            var state1 = JsonSerializer.Serialize(BuildState1(crypto.PublicKeyB64, name, fileCount));
            Log.Info($"BLE: 9896 state1 <- {state1} ({state1.Length}B, mtu {maxPdu})");
            await WriteOConnectSingle(state1);

            // 4. (pv>=5 only) N=6: the phone notifies {"account_id":<enc>} - decrypt it
            //    (encrypted with the same session key we hold) and echo it back.
            //    NOTE: the pad DOES send the challenge on the air (HCI log:09:59:46.614
            //    GATTS_HandleValueNotification) but Windows drops it - 0x9898 has no CCCD
            //    in the pad's GATT table so Windows never subscribes. With pv<5 we never
            //    need to receive anything: the card + accept path needs only writes.
            if (OConnectPv >= 5)
            {
                status?.Invoke("waiting for phone account validation...");
                var challengeJson = await accountNotifyTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
                using var challenge = JsonDocument.Parse(challengeJson);
                var encPhoneAccount = challenge.RootElement.TryGetProperty("account_id", out var ai)
                    ? ai.GetString() : null;
                if (string.IsNullOrEmpty(encPhoneAccount))
                    throw new InvalidOperationException("BLE: account challenge had no account_id");
                var phoneAccount = OShareCrypto.CbcDecryptFromB64(cbcKey, cbcIv, encPhoneAccount);
                Log.Info($"BLE: account challenge decrypted (account len {phoneAccount.Length})");

                var accountResp = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["account_id"] = OShareCrypto.CbcEncryptToB64(cbcKey, cbcIv, phoneAccount),
                });
                Log.Info($"BLE: 9896 account response <- {accountResp}");
                await WriteOConnectSingle(accountResp);

                // 5. (pv>=5 only) the phone validates, notifies its wlan offer and moves
                //    to N=3. We don't need that message - our state3 offer doesn't depend
                //    on it - so we skip waiting for it entirely.
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
    /// The OPlus-Connect protocol version we announce in state-1. pv&lt;5 makes the pad
    /// show the plain receive card (accept button) — the whole flow then needs BLE
    /// WRITES only. pv&gt;=5 would use the account challenge, whose challenge/wlan-offer
    /// messages arrive as notifications on 0x9898 — the pad's GATT table has no CCCD
    /// there, Windows cannot subscribe, and (HCI-verified) Windows drops those
    /// notifications even though the pad puts them on the air. So we stay on pv=1.
    /// </summary>
    private static readonly int OConnectPv = 1;

    private static Dictionary<string, object> BuildState1(string pubKey, string dname, int fileCount) => new()
    {
        ["key"] = pubKey,
        ["isFast"] = 0,
        ["version"] = "10302",
        ["pv"] = OConnectPv,
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
