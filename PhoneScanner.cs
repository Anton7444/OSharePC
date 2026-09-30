using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace OShareSender;

public enum PhoneKind
{
    /// <summary>moe.reimu.oshare app — advertisement: 128-bit 3331 + 0xffff(27B)/0x01ff(6B) service data.</summary>
    OShare,
    /// <summary>Stock alliance ROM (OPPO/OnePlus/Xiaomi/vivo/…) — 128-bit 3331 + vender/bleFlag service data.</summary>
    Alliance,
    /// <summary>Legacy OEM variants (0x3333/0x3334, 0x6666/0x6667, 0x8181/0x8182) — display only for now.</summary>
    Legacy,
    /// <summary>Discovered via the SSDP-style LAN ANNOUNCE (LanDiscovery.cs), not BLE
    /// at all — see OPPO_ACCOUNT_API_FINDINGS.md section 5l. This is the one channel
    /// confirmed (via a real adb logcat capture) to make a phone report
    /// accountState=SAME_ACCOUNT for this PC without needing "All" BLE visibility, so
    /// it's the most promising path for the reverse direction (PC discovering a
    /// phone in "Contacts only" mode) too.</summary>
    Lan
}

public sealed class PhoneDevice
{
    public ulong Address { get; set; }
    public BluetoothAddressType AddressType { get; set; } = BluetoothAddressType.Unspecified;
    public string AddressStr => FormatAddress(Address);
    public string Name { get; set; } = "";
    public PhoneKind Kind { get; set; }
    public int Vender { get; set; }
    public int BleFlag { get; set; }
    /// <summary>16-char alliance deviceId, assembled from the 6-byte ADV half + 10-byte SCAN_RSP half.</summary>
    public string DeviceId { get; set; } = "";
    /// <summary>deviceId[0:6] from the ADV service data.</summary>
    public string DeviceIdPart1 { get; set; } = "";
    /// <summary>deviceId[6:16] from the scan-response service data.</summary>
    public string DeviceIdPart2 { get; set; } = "";
    /// <summary>OShare-style 4-hex sender id from the 27-byte payload.</summary>
    public string SenderId { get; set; } = "";
    /// <summary>Legacy (0x6666/0x6667) kind only: this device's stable id, taken
    /// directly from the trailing 12 hex-ASCII characters of the 0x6666 section —
    /// self-contained, no multi-fragment address-rotation reconstruction needed
    /// (unlike the Alliance kind's split 6+10 byte deviceId).</summary>
    public string LegacyDeviceId { get; set; } = "";
    /// <summary>Legacy kind only: the 3-character "same account" proof from the
    /// 0x6667 section (com.oplus.oshare.utils.AccountManger#D) — see
    /// OPPO_ACCOUNT_API_FINDINGS.md section 5f.</summary>
    public string LegacyAccountId { get; set; } = "";
    /// <summary>Lan kind only: the phone's "PDID" (protocol device id) from its
    /// SSDP-style ANNOUNCE, and the IP it announced from.</summary>
    public string LanPdid { get; set; } = "";
    public string LanIp { get; set; } = "";
    /// <summary>Lan kind only: the announce carried our own account digest.</summary>
    public bool LanSameAccount { get; set; }
    /// <summary>Lan kind only: the "DT" numeric device-type from the SERVICE-PUBLISH
    /// packet (OPPO's DeviceType enum: 6=PC, 8=PHONE, 10=PAD, 1=SMART_WATCH,
    /// 3/4/16=headphones, 7=BRACELET, ...). Used to filter accessories (earbuds,
    /// watches) out of the shareable-device list, matching what OShare's own UI does.</summary>
    public int LanDeviceType { get; set; } = -1;
    public int Version { get; set; }
    public short Rssi { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public int SeenCount { get; set; }

    // Android's ScanRecord gives OnePlus Share a merged ADV+SCAN_RSP. Windows emits
    // those pieces separately, so remember when every official field arrived and
    // only permit GATT after a coherent complete record has been reconstructed.
    public bool AllianceUuidSeen { get; set; }
    public DateTimeOffset AllianceUuidSeenAt { get; set; }
    public DateTimeOffset DeviceIdPart1SeenAt { get; set; }
    public DateTimeOffset DeviceIdPart2SeenAt { get; set; }
    public DateTimeOffset LastIdentityFragmentSeen { get; set; }
    public DateTimeOffset LastCompleteAdvertisement { get; set; }

    public bool HasCompleteIdentity => Kind switch
    {
        PhoneKind.OShare => SenderId.Length == 4,
        PhoneKind.Alliance => AllianceUuidSeen &&
                              DeviceIdPart1.Length == 6 &&
                              DeviceIdPart2.Length == 10 &&
                              DeviceId.Length == 16 &&
                              LastCompleteAdvertisement != default,
        PhoneKind.Legacy => LegacyDeviceId.Length == 12,
        PhoneKind.Lan => LanPdid.Length > 0,
        _ => false,
    };

    /// <summary>
    /// True only when the newest identity fragment belongs to a complete reconstructed
    /// advertisement. A newer partial fragment means the phone has probably restarted
    /// or rotated its advertiser and the old BLE address must not be connected.
    /// </summary>
    public bool IsConnectReady => HasCompleteIdentity &&
                                  (Kind != PhoneKind.Alliance ||
                                   LastCompleteAdvertisement >= LastIdentityFragmentSeen);

    public string KindLabel => Kind switch
    {
        PhoneKind.OShare => "OShare",
        PhoneKind.Alliance => $"Alliance ({BrandFromVender(Vender)})",
        PhoneKind.Legacy => $"Legacy ({BrandFromVender(Vender)})",
        PhoneKind.Lan => LanPdid.StartsWith("FC70", StringComparison.Ordinal) ? "Contacts" : "LAN",
        _ => "Legacy OEM"
    };

    /// <summary>OPPO's DeviceType enum (com/oplus/pantaconnect/discovery/model/DeviceType.java):
    /// wearables/audio accessories can't receive a file share, so LAN entries reporting
    /// one of these types are filtered out of the shareable device list — matching what
    /// OShare's own nearby-share UI does (it doesn't list your earbuds either).</summary>
    private static readonly HashSet<int> AccessoryDeviceTypes = new() { 1, 3, 4, 7, 15, 16 };

    public bool IsShareableLanDevice => Kind != PhoneKind.Lan ||
                                         LanDeviceType < 0 ||
                                         !AccessoryDeviceTypes.Contains(LanDeviceType);

    public string LanTypeLabel => LanDeviceType switch
    {
        5 => "TV",
        6 => "PC",
        8 => "Phone",
        10 => "Pad",
        11 => "MacBook",
        12 => "iMac",
        13 => "iPhone",
        17 => "Camera",
        51 => "NAS",
        _ => "Device",
    };

    public static string BrandFromVender(int v) => v switch
    {
        0 or 10 or 11 => "OPPO/realme",
        >= 20 and <= 39 => "Xiaomi",
        >= 41 and <= 45 => "OnePlus",
        >= 50 and <= 59 => "Meizu",
        >= 60 and <= 69 => "Honor/Huawei",
        >= 70 and <= 75 => "Samsung",
        >= 100 and <= 109 => "Lenovo/PC",
        >= 110 and <= 119 => "Motorola",
        >= 161 and <= 169 => "ASUS",
        >= 170 and <= 179 => "Hisense",
        _ => $"vender {v}"
    };

    public static string FormatAddress(ulong a) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((byte)(a >> (i * 8))).ToString("x2")));
}

/// <summary>
/// Scans for phones running 互传/OShare in receive mode.
/// OnePlus Share 16.10.61's common parser (c8/b.b -> d8/o.n) accepts a device only
/// after a complete ScanRecord is present: the custom 128-bit 0x3331 UUID plus the
/// fixed vendor/flag, 6+10 byte device id, name and version fields. Android merges
/// ADV + SCAN_RSP into one ScanRecord; Windows does not, so this scanner accumulates
/// the two Windows events but never exposes/connects a half-built record.
/// </summary>
public sealed class PhoneScanner : IDisposable
{
    public static readonly Guid AllianceServiceUuid = new("00003331-0000-1000-8000-008123456789");
    private static readonly TimeSpan AllianceMergeWindow = TimeSpan.FromSeconds(3);

    private BluetoothLEAdvertisementWatcher? _watcher;
    private readonly Dictionary<ulong, PhoneDevice> _devices = new();
    // Keyed by PDID (a string, not a BLE address) since LAN devices arrive over UDP,
    // not BLE — kept separate from _devices rather than shoehorning a synthetic BLE
    // address in, and merged into the Devices getter below.
    private readonly Dictionary<string, PhoneDevice> _lanDevices = new(StringComparer.OrdinalIgnoreCase);
    // Throttles the paired-device lookup below to once per PDID per interval,
    // since UpsertLanDevice fires on every QUERY (roughly once a second).
    private readonly Dictionary<string, DateTimeOffset> _pairedLookupAttempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly object _signalGate = new();
    private TaskCompletionSource _advertisementSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void PulseAdvertisement()
    {
        TaskCompletionSource old;
        lock (_signalGate)
        {
            old = _advertisementSignal;
            _advertisementSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        old.TrySetResult();
    }

    public event Action<PhoneDevice>? DeviceSeen;
    public event Action<ulong>? DeviceExpired;

    public IReadOnlyList<PhoneDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                // Partial records are internal scanner state only. This mirrors the
                // official common parser returning null for an incomplete ScanRecord.
                var now = DateTimeOffset.UtcNow;
                var realSameAccountTypes = _lanDevices.Values
                    .Where(d => !d.LanPdid.StartsWith("FC70", StringComparison.Ordinal) && d.LanSameAccount &&
                                now - d.LastSeen < TimeSpan.FromMinutes(5))
                    .Select(d => d.LanDeviceType).ToHashSet();
                // A Contacts-beacon entry and a LAN-announced entry can be the same physical device. When a fresh
                // beacon entry has the same device type and name as a LAN entry, show only the beacon entry.
                var freshBeaconKeys = _lanDevices.Values
                    .Where(d => d.LanPdid.StartsWith("FC70", StringComparison.Ordinal) && now - d.LastSeen < TimeSpan.FromMinutes(5) &&
                                !string.IsNullOrWhiteSpace(d.Name))
                    .Select(d => (d.LanDeviceType, d.Name.Trim().ToLowerInvariant())).ToHashSet();
                return _devices.Values.Concat(_lanDevices.Values)
                    .Where(device => !device.LanPdid.StartsWith("FC70", StringComparison.Ordinal) ||
                                     (now - device.LastSeen < TimeSpan.FromMinutes(5) && !realSameAccountTypes.Contains(device.LanDeviceType)))
                    .Where(device => device.Kind != PhoneKind.Lan || device.LanPdid.StartsWith("FC70", StringComparison.Ordinal) ||
                                     !freshBeaconKeys.Contains((device.LanDeviceType, (device.Name ?? "").Trim().ToLowerInvariant())))
                    .Where(device => device.HasCompleteIdentity && device.IsShareableLanDevice)
                    .GroupBy(StableIdentity, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(device => device.LastCompleteAdvertisement).First())
                    .ToList();
            }
        }
    }

    /// <summary>Called from LanDiscovery's DeviceAnnounced event (see SenderEngine.cs)
    /// whenever the phone's SSDP-style ANNOUNCE arrives — a completely different,
    /// non-BLE channel that a real adb logcat capture confirmed the phone treats as
    /// SAME_ACCOUNT-eligible without needing "All" BLE visibility (see
    /// OPPO_ACCOUNT_API_FINDINGS.md section 5l). Self-contained per announce, no
    /// fragment reconstruction needed.</summary>
    public void UpsertLanDevice(string pdid, string ip, string deviceName, string? deviceType = null, bool sameAccount = false)
    {
        if (string.IsNullOrWhiteSpace(pdid)) return;
        lock (_gate)
        {
            if (!_lanDevices.TryGetValue(pdid, out var device))
            {
                device = new PhoneDevice
                {
                    Address = SyntheticAddressFromPdid(pdid),
                    Kind = PhoneKind.Lan,
                    LanPdid = pdid,
                };
                _lanDevices[pdid] = device;
            }
            device.LanIp = ip;
            if (sameAccount) device.LanSameAccount = true;
            if (int.TryParse(deviceType, out var dt)) device.LanDeviceType = dt;
            // If this same 12-hex device id has ever been seen over BLE (e.g. an
            // Alliance scan response from a prior "All"-mode window), borrow its real
            // Bluetooth hardware address instead of the synthetic one — GATT sends
            // need a real address, and the phone may still accept a direct connect
            // to a known address even while not currently advertising.
            var bleMatch = _devices.Values.FirstOrDefault(d =>
                d.DeviceId.Length == 16 &&
                d.DeviceId.StartsWith(pdid, StringComparison.OrdinalIgnoreCase));
            if (bleMatch is not null && device.Address != bleMatch.Address)
            {
                Log.Info($"SCAN: LAN device {pdid} adopting real BLE address {bleMatch.AddressStr} (was synthetic {device.AddressStr})");
                device.Address = bleMatch.Address;
                device.AddressType = bleMatch.AddressType;
            }
            // Still no real address (no live "All"-mode BLE advertisement ever seen
            // for this PDID) — fall back to a real Bluetooth MAC Windows already has
            // cached from a one-time manual pairing (Settings > Bluetooth & devices).
            // This works even in "Contacts only" mode since it doesn't depend on the
            // phone currently advertising at all. Throttled to avoid re-querying the
            // OS's device list on every ~1s QUERY packet.
            if (device.Kind == PhoneKind.Lan && device.Address == SyntheticAddressFromPdid(pdid) &&
                (!_pairedLookupAttempted.TryGetValue(pdid, out var last) || DateTimeOffset.UtcNow - last > TimeSpan.FromSeconds(30)))
            {
                _pairedLookupAttempted[pdid] = DateTimeOffset.UtcNow;
                var nameHint = deviceName;
                _ = ResolvePairedAddressAsync(pdid, nameHint);
            }
            if (!string.IsNullOrWhiteSpace(deviceName))
                device.Name = deviceName;
            else if (string.IsNullOrWhiteSpace(device.Name))
            {
                // The LAN SERVICE-PUBLISH broadcast carries no device name; borrow it
                // from the BLE match above if we have one, else fall back to the
                // account nickname.
                if (bleMatch is not null && !string.IsNullOrWhiteSpace(bleMatch.Name))
                    device.Name = bleMatch.Name;
                else if (sameAccount && !string.IsNullOrWhiteSpace(SettingsStore.Current.OppoAccountName))
                    // The protocol carries no device name at all over LAN; for a
                    // same-account device we at least know whose account it belongs
                    // to (from the login flow), so label it "<account>'s <type>"
                    // instead of a raw hex id.
                    device.Name = $"{SettingsStore.Current.OppoAccountName}'s {device.LanTypeLabel}";
            }
            device.LastSeen = DateTimeOffset.UtcNow;
            device.LastCompleteAdvertisement = device.LastSeen;
            device.SeenCount++;
        }
        Log.Info($"SCAN: LAN device upserted pdid={pdid} ip={ip} name={deviceName}");
        PulseAdvertisement();
    }

    private static ulong SyntheticAddressFromPdid(string pdid)
    {
        if (pdid.Length <= 16 && pdid.All(Uri.IsHexDigit) && ulong.TryParse(pdid, System.Globalization.NumberStyles.HexNumber, null, out var direct))
            return direct;
        // Non-hex or too-long PDIDs (unexpected, but be defensive): derive a stable
        // synthetic value instead of colliding everything onto 0.
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(pdid));
        return BitConverter.ToUInt64(hash, 0);
    }

    /// <summary>Looks up a real Bluetooth hardware address for a LAN-only device from
    /// Windows' own paired-device cache, matched by name. This is the fallback for
    /// phones kept in "Contacts only" OShare visibility, which never emit a live BLE
    /// Alliance advertisement our scanner can observe (confirmed via LAN packet
    /// capture: those phones only ever send bare QUERY packets with no bt_mac field
    /// anywhere). A one-time manual Bluetooth pairing via Windows Settings makes the
    /// phone's real address permanently resolvable here regardless of its current
    /// OShare-app visibility setting.</summary>
    private async Task ResolvePairedAddressAsync(string pdid, string nameHint)
    {
        try
        {
            var matches = new List<(ulong Address, BluetoothAddressType Type, string Name)>();

            // Paired classic Bluetooth (BR/EDR) devices — phones pair this way by
            // default via Settings > Bluetooth & devices > Add device.
            foreach (var selector in new[]
                     {
                         BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                         BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)
                     })
            {
                DeviceInformationCollection found;
                try { found = await DeviceInformation.FindAllAsync(selector); }
                catch (Exception ex) { Log.Warn($"SCAN: paired-device enumerate failed: {ex.Message}"); continue; }

                foreach (var di in found)
                {
                    if (string.IsNullOrWhiteSpace(di.Name)) continue;
                    if (!string.IsNullOrWhiteSpace(nameHint) &&
                        !di.Name.Contains(nameHint, StringComparison.OrdinalIgnoreCase) &&
                        !nameHint.Contains(di.Name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        if (selector == BluetoothDevice.GetDeviceSelectorFromPairingState(true))
                        {
                            using var bd = await BluetoothDevice.FromIdAsync(di.Id);
                            if (bd is not null)
                                matches.Add((bd.BluetoothAddress, BluetoothAddressType.Public, di.Name));
                        }
                        else
                        {
                            using var ble = await BluetoothLEDevice.FromIdAsync(di.Id);
                            if (ble is not null)
                                matches.Add((ble.BluetoothAddress, ble.BluetoothAddressType, di.Name));
                        }
                    }
                    catch (Exception ex) { Log.Warn($"SCAN: paired-device resolve '{di.Name}' failed: {ex.Message}"); }
                }
            }

            if (matches.Count == 0)
            {
                if (!pdid.StartsWith("FC70", StringComparison.Ordinal)) // Contacts beacon devices need no pairing
                    Log.Info($"SCAN: LAN device {pdid} ('{nameHint}') has no matching Windows-paired Bluetooth device (optional fallback)");
                return;
            }

            var (address, type, matchedName) = matches[0];
            lock (_gate)
            {
                if (_lanDevices.TryGetValue(pdid, out var device) && device.Address != address)
                {
                    Log.Info($"SCAN: LAN device {pdid} adopting real paired-Bluetooth address {PhoneDevice.FormatAddress(address)} for '{matchedName}' (was synthetic {device.AddressStr})");
                    device.Address = address;
                    device.AddressType = type;
                }
            }
            PulseAdvertisement();
        }
        catch (Exception ex) { Log.Warn($"SCAN: ResolvePairedAddressAsync({pdid}) failed: {ex.Message}"); }
    }

    public bool IsScanning { get; private set; }

    private readonly Dictionary<string, (DateTimeOffset Last, int Tries)> _nameResolveState = new();

    /// <summary>The beacon has no name. Read the standard GAP "Device Name" (0x2A00) with a short connection,
    /// a plain GAP read that does not touch OShare, at most a few times several minutes apart, until a name is known.</summary>
    private async Task TryResolveContactNameAsync(ulong address, BluetoothAddressType type, int deviceType, string digest)
    {
        var key = $"{deviceType:X2}{digest}";
        lock (_gate)
        {
            _nameResolveState.TryGetValue(key, out var st);
            if (st.Tries >= 3 || DateTimeOffset.UtcNow - st.Last < TimeSpan.FromMinutes(3)) return;
            _nameResolveState[key] = (DateTimeOffset.UtcNow, st.Tries + 1);
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address, type).AsTask(cts.Token);
            if (dev is null) return;
            var ok = Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success;
            var svcs = await dev.GetGattServicesForUuidAsync(new Guid("00001800-0000-1000-8000-00805f9b34fb"), BluetoothCacheMode.Uncached).AsTask(cts.Token);
            if (svcs.Status != ok || svcs.Services.Count == 0) return;
            var chars = await svcs.Services[0].GetCharacteristicsForUuidAsync(new Guid("00002a00-0000-1000-8000-00805f9b34fb"), BluetoothCacheMode.Uncached).AsTask(cts.Token);
            if (chars.Status != ok || chars.Characteristics.Count == 0) return;
            var read = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cts.Token);
            if (read.Status != ok) return;
            var reader = DataReader.FromBuffer(read.Value);
            var bytes = new byte[read.Value.Length];
            reader.ReadBytes(bytes);
            RememberContactName(deviceType, digest, Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) { Log.Info($"NAMES: name read for type {deviceType} did not work this time ({ex.GetType().Name})"); }
    }

    /// <summary>Saves a real device name and refreshes the listed entry.</summary>
    public void RememberContactName(int deviceType, string digest, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (ContactNames.Set(deviceType, digest, name))
            UpsertLanDevice($"FC70{deviceType:X2}{digest}", "", name.Trim(), deviceType.ToString());
    }

    public sealed record ContactsBeacon(ulong Address, BluetoothAddressType AddressType, byte DeviceType, string AccountDigest, DateTimeOffset SeenAt);

    private readonly Dictionary<ulong, ContactsBeacon> _contactsBeacons = new();

    /// <summary>Uppercase hex of our own DSF account digest (e.g. "39154E"); Contacts-mode beacons with
    /// this digest are same-account devices.</summary>
    public string? ContactsAccountDigest { get; set; }

    /// <summary>Waits for a Contacts-mode beacon of the given OPPO device type and account digest that is
    /// newer than <paramref name="newerThan"/>. Returns null on timeout.</summary>
    /// <summary>True when a beacon of this type/digest was seen within <paramref name="within"/>.</summary>
    public bool HasRecentContactsBeacon(byte deviceType, string accountDigestHex, TimeSpan within)
    {
        var cutoff = DateTimeOffset.UtcNow - within;
        lock (_gate)
            return _contactsBeacons.Values.Any(b => b.DeviceType == deviceType && b.SeenAt > cutoff &&
                string.Equals(b.AccountDigest, accountDigestHex, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ContactsBeacon?> WaitForContactsBeaconAsync(byte deviceType, string accountDigestHex, DateTimeOffset newerThan, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            ContactsBeacon? best = null;
            lock (_gate)
            {
                foreach (var b in _contactsBeacons.Values)
                {
                    if (b.DeviceType == deviceType &&
                        string.Equals(b.AccountDigest, accountDigestHex, StringComparison.OrdinalIgnoreCase) &&
                        b.SeenAt > newerThan && (best is null || b.SeenAt > best.SeenAt))
                        best = b;
                }
            }
            if (best is not null) return best;
            await Task.Delay(150, ct);
        }
        return null;
    }

    public void Start()
    {
        if (IsScanning) return;
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
            // The tablet's always-on beacon (service data 0xFC70) is an extended advertisement.
            AllowExtendedAdvertisements = true
        };
        _watcher.Received += OnReceived;
        _watcher.Stopped += (_, e) => Log.Info($"BLE: scanner stopped (error={e.Error})");
        _watcher.Start();
        IsScanning = true;
    }

    public void Stop()
    {
        if (!IsScanning) return;
        try { _watcher?.Stop(); } catch { }
        _watcher = null;
        IsScanning = false;
        PulseAdvertisement();
    }

    /// <summary>Forget devices not seen in the last 10 seconds.</summary>
    public void PruneStale()
    {
        List<ulong> gone = new();
        lock (_gate)
        {
            foreach (var (addr, dev) in _devices)
            {
                if (DateTimeOffset.Now - dev.LastSeen > TimeSpan.FromSeconds(10)) gone.Add(addr);
            }
            foreach (var addr in gone) _devices.Remove(addr);
        }
        foreach (var addr in gone) DeviceExpired?.Invoke(addr);
    }

    /// <summary>
    /// Find the freshest complete advertisement for a known device. If a newer
    /// partial record with the same stable identity prefix exists, return null:
    /// that is an advertiser/address rotation in progress, not a connectable peer.
    /// </summary>
    public PhoneDevice? FindFresh(PhoneDevice known) => FindFreshConnectable(known, null);

    public async Task<PhoneDevice> WaitForConnectableAsync(
        PhoneDevice known,
        TimeSpan timeout,
        DateTimeOffset? newerThan = null,
        CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var ready = FindFreshConnectable(known, newerThan);
            if (ready is not null) return ready;

            Task waitTask;
            lock (_signalGate) waitTask = _advertisementSignal.Task;

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            var waitDuration = remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200);
            await Task.WhenAny(waitTask, Task.Delay(waitDuration, ct));
        }

        throw new InvalidOperationException(
            "BLE: timed out waiting for a complete OnePlus/OPPO advertisement. " +
            "The phone is visible, but its current ADV + scan-response identity is not complete yet.");
    }

    private PhoneDevice? FindFreshConnectable(PhoneDevice known, DateTimeOffset? newerThan)
    {
        lock (_gate)
        {
            var related = _devices.Values
                .Where(candidate => MatchesKnownIdentity(known, candidate))
                .OrderByDescending(candidate => candidate.LastSeen)
                .ToList();

            if (related.Count == 0)
            {
                if (known.IsConnectReady &&
                    (!newerThan.HasValue || known.LastCompleteAdvertisement > newerThan.Value))
                    return known;
                return null;
            }

            var newest = related[0];
            var bestReady = related
                .Where(candidate => candidate.IsConnectReady &&
                                    (!newerThan.HasValue || candidate.LastCompleteAdvertisement > newerThan.Value))
                .OrderByDescending(candidate => candidate.LastCompleteAdvertisement)
                .FirstOrDefault();

            // A new address carrying only the 6-byte device-id prefix is exactly the
            // failure seen after OnePlus restarts advertising on USER_PRESENT. Do not
            // fall back to the old complete address while that newer generation is partial.
            if (!newest.IsConnectReady &&
                (bestReady is null || newest.LastSeen > bestReady.LastCompleteAdvertisement))
                return null;

            return bestReady;
        }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher _, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var adv = args.Advertisement;
        var sections = adv.DataSections.Where(s => s.DataType == 0x16)
            .Select(s => (Data: ToArray(s.Data), Section: s))
            .Where(x => x.Data is { Length: >= 2 })
            .Select(x => (Uuid16: (ushort)(x.Data![0] | (x.Data[1] << 8)), Payload: x.Data[2..], Raw: x.Data))
            .ToList();

        var hasAllianceUuid = adv.ServiceUuids.Any(u => u == AllianceServiceUuid);

        // Contacts-mode "always-on" beacon (service data 0xFC70): 00 15 06 13 <deviceType> 37 <3-byte
        // account digest> 84 ... Same-account OPPO/OnePlus devices in Contacts-only visibility emit it
        // from a connectable extended advertising set; connecting to that address wakes their OShare
        // GATT server (9999) on demand.
        foreach (var sec in sections)
        {
            if (sec.Uuid16 == 0xFC70 && sec.Payload.Length >= 9 &&
                sec.Payload[0] == 0x00 && sec.Payload[1] == 0x15 && sec.Payload[2] == 0x06 && sec.Payload[3] == 0x13 &&
                sec.Payload[5] == 0x37)
            {
                var digest = Convert.ToHexString(sec.Payload, 6, 3);
                lock (_gate)
                {
                    _contactsBeacons[args.BluetoothAddress] = new ContactsBeacon(
                        args.BluetoothAddress, args.BluetoothAddressType, sec.Payload[4], digest, DateTimeOffset.UtcNow);
                }
                // A same-account device in Contacts-only visibility: list it (keyed by type+digest, since the
                // beacon's address rotates) so it can be picked as a send target.
                if (!string.IsNullOrEmpty(ContactsAccountDigest) &&
                    string.Equals(digest, ContactsAccountDigest, StringComparison.OrdinalIgnoreCase))
                {
                    var dtName = ContactNames.Get(sec.Payload[4], digest) ??
                                 (sec.Payload[4] switch { 10 => "Tablet", 8 => "Phone", 6 => "PC", _ => $"Device type {sec.Payload[4]}" });
                    UpsertLanDevice($"FC70{sec.Payload[4]:X2}{digest}", "", dtName, sec.Payload[4].ToString());
                    if (ContactNames.Get(sec.Payload[4], digest) is null)
                        Task.Run(() => TryResolveContactNameAsync(args.BluetoothAddress, args.BluetoothAddressType, sec.Payload[4], digest));
                }
            }
        }

        // Windows can deliver ADV and SCAN_RSP as separate events. Scan responses
        // carry the 27-byte name/id tail but not the 128-bit UUID AD, so recognise
        // the known service-data UUIDs only for accumulation. They are not enough
        // by themselves to make a connect candidate.
        static bool IsKnownSectionUuid(ushort u) =>
            u == 0xFFFF || u == 0x01FF ||
            u == 0x0703 || u == 0x0204 || u == 0x0704 ||
            u == 0x6666 || u == 0x6667;

        PhoneDevice device;
        bool shouldPublish;
        bool shouldLogPending;
        lock (_gate)
        {
            if (!_devices.TryGetValue(args.BluetoothAddress, out device!))
            {
                var kind = sections.Any(s => s.Uuid16 is 0xFFFF or 0x01FF) ? PhoneKind.OShare
                    : sections.Any(s => s.Uuid16 is 0x6666 or 0x6667) ? PhoneKind.Legacy
                    : PhoneKind.Alliance;
                ushort firstUuid = sections.Count > 0 ? sections[0].Uuid16 : (ushort)0;
                if (!hasAllianceUuid && !IsKnownSectionUuid(firstUuid))
                {
                    // DIAGNOSTIC: log advertisements we'd otherwise silently discard,
                    // so we can see what a phone in "Contacts only" mode actually
                    // broadcasts (it may not use the legacy Alliance format at all —
                    // see OPPO_ACCOUNT_API_FINDINGS.md section 5k for the modern
                    // "senseless"/0xAFAF format this might turn out to match).
                    // Only the beacons this app actually uses are worth a log line, and each at most every 30 s
                    // per address (unthrottled this alone produced tens of MB of log).
                    if (sections.Count > 0 && sections.Any(x => x.Uuid16 is 0xFC70 or 0x3339) &&
                        Log.Every($"unrec:{args.BluetoothAddress:X12}", TimeSpan.FromSeconds(30)))
                        Log.Info($"BLE: unrecognized service-data {args.BluetoothAddress:X12} " +
                                 $"[{string.Join(", ", sections.Select(s => $"{s.Uuid16:X4}:{Convert.ToHexString(s.Payload)}"))}] " +
                                 $"rssi={args.RawSignalStrengthInDBm} allUuids=[{string.Join(",", adv.ServiceUuids)}]");
                    return;
                }
                device = new PhoneDevice { Address = args.BluetoothAddress, AddressType = args.BluetoothAddressType, Kind = kind };
                _devices[device.Address] = device;
            }

            var now = DateTimeOffset.UtcNow;
            var beforePart1 = device.DeviceIdPart1;
            var beforePart2 = device.DeviceIdPart2;

            device.AddressType = args.BluetoothAddressType;
            device.Rssi = args.RawSignalStrengthInDBm;
            device.LastSeen = now;
            device.SeenCount++;

            if (hasAllianceUuid)
            {
                device.AllianceUuidSeen = true;
                device.AllianceUuidSeenAt = now;
            }

            foreach (var s in sections)
            {
                var payload = s.Payload;

                if (s.Uuid16 == 0xFFFF && payload.Length == 27)
                {
                    device.Kind = PhoneKind.OShare;
                    device.SenderId = $"{payload[8]:x2}{payload[9]:x2}";
                    var n = DecodeName(payload, 10, 16);
                    if (n.Length > 0) device.Name = n;
                    device.Version = payload[26];
                    device.LastCompleteAdvertisement = now;
                }
                else if (s.Uuid16 == 0x01FF && payload.Length >= 2)
                {
                    device.Kind = PhoneKind.OShare;
                }
                else if ((hasAllianceUuid || device.Kind == PhoneKind.Alliance) && payload.Length is 6 or 27)
                {
                    device.Kind = PhoneKind.Alliance;
                    if (payload.Length == 6)
                    {
                        device.Vender = s.Raw![0];
                        device.BleFlag = s.Raw[1];
                        device.DeviceIdPart1 = Encoding.ASCII.GetString(payload).Trim('\0');
                        device.DeviceIdPart1SeenAt = now;
                        device.LastIdentityFragmentSeen = now;
                    }
                    else
                    {
                        device.DeviceIdPart2 = Encoding.ASCII.GetString(payload[0..10]).Trim('\0');
                        device.DeviceIdPart2SeenAt = now;
                        device.LastIdentityFragmentSeen = now;
                        var n = DecodeName(payload, 10, 16);
                        if (n.Length > 0) device.Name = n;
                        device.Version = payload[26];

                        // Some phones don't redeliver every AD fragment after a BLE
                        // address rotation - only the scan-response half (this one)
                        // shows up again, and the 6-byte primary-advertisement half
                        // + UUID-list AD never return for the new address, so the
                        // fresh 3-way correlation below can never succeed and the
                        // device gets stuck "pending" forever. If this exact 10-byte
                        // suffix was already fully resolved under a previous address,
                        // trust that inherited identity instead of waiting for a
                        // correlation that may never arrive again.
                        if (device.DeviceIdPart1.Length != 6)
                        {
                            var known = _devices.Values.FirstOrDefault(other =>
                                !ReferenceEquals(other, device) &&
                                other.DeviceIdPart1.Length == 6 &&
                                other.DeviceIdPart2 == device.DeviceIdPart2);
                            if (known is not null)
                            {
                                device.DeviceIdPart1 = known.DeviceIdPart1;
                                device.AllianceUuidSeen = true;
                                if (string.IsNullOrWhiteSpace(device.Name)) device.Name = known.Name;
                                if (device.Vender == 0) device.Vender = known.Vender;
                                if (device.BleFlag == 0) device.BleFlag = known.BleFlag;
                                device.DeviceId = device.DeviceIdPart1 + device.DeviceIdPart2;
                                device.LastCompleteAdvertisement = now;
                            }
                        }
                    }

                    if (device.DeviceIdPart1.Length == 6 && device.DeviceIdPart2.Length == 10)
                    {
                        device.DeviceId = device.DeviceIdPart1 + device.DeviceIdPart2;

                        // OnePlus sees both halves in one ScanRecord. On Windows accept
                        // the reconstructed record only when the split events arrived
                        // close enough to represent the same advertising generation.
                        var oldest = Min(device.AllianceUuidSeenAt, device.DeviceIdPart1SeenAt, device.DeviceIdPart2SeenAt);
                        var newest = Max(device.AllianceUuidSeenAt, device.DeviceIdPart1SeenAt, device.DeviceIdPart2SeenAt);
                        if (device.AllianceUuidSeenAt != default &&
                            oldest != default &&
                            newest - oldest <= AllianceMergeWindow)
                            device.LastCompleteAdvertisement = now;
                    }
                    else
                    {
                        // Never promote the 6-byte prefix (e.g. F6FBC3) to DeviceId.
                        device.DeviceId = "";
                    }
                }
                else if (s.Uuid16 == 0x6666 && payload.Length >= 12)
                {
                    // Self-contained: the last 12 bytes are the deviceId written out
                    // as its own hex-ASCII string (e.g. "F6FBC3C12E1D"), no split-
                    // fragment reconstruction needed. Whatever precedes it is a UTF-8
                    // device nickname.
                    device.Kind = PhoneKind.Legacy;
                    var idHex = Encoding.ASCII.GetString(payload[^12..]);
                    if (idHex.All(Uri.IsHexDigit))
                    {
                        device.LegacyDeviceId = idHex.ToUpperInvariant();
                        device.LastCompleteAdvertisement = now;
                        device.LastIdentityFragmentSeen = now;
                    }
                    if (payload.Length > 12)
                    {
                        var n = Encoding.UTF8.GetString(payload[..^12]).Trim('\0', ' ');
                        if (n.Length > 0) device.Name = n;
                    }
                }
                else if (s.Uuid16 == 0x6667 && payload.Length >= 4)
                {
                    // advBrandType(1) + accountId(3, ASCII) + accountName(remainder,
                    // UTF-8, NUL-padded) — see com.oplus.oshare.ble.impl.o#q and
                    // AccountManger#D (OPPO_ACCOUNT_API_FINDINGS.md section 5f).
                    device.Kind = PhoneKind.Legacy;
                    device.Vender = payload[0];
                    device.LegacyAccountId = Encoding.ASCII.GetString(payload[1..4]).Trim('\0');
                    if (payload.Length > 4)
                    {
                        var accountName = Encoding.UTF8.GetString(payload[4..]).Trim('\0', ' ');
                        if (accountName.Length > 0 && string.IsNullOrWhiteSpace(device.Name))
                            device.Name = accountName;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(device.Name) && !string.IsNullOrWhiteSpace(adv.LocalName))
                device.Name = adv.LocalName;

            if (device.IsConnectReady)
                device = MergeRotatedAddressLocked(device);

            shouldPublish = device.IsConnectReady;
            shouldLogPending = !device.HasCompleteIdentity &&
                               (device.SeenCount == 1 ||
                                beforePart1 != device.DeviceIdPart1 ||
                                beforePart2 != device.DeviceIdPart2);
        }

        PulseAdvertisement();

        if (!shouldPublish)
        {
            if (shouldLogPending)
                if (Log.Every($"pend:{device.AddressStr}", TimeSpan.FromSeconds(30)))
                Log.Info($"BLE: pending {device.KindLabel} advertisement {device.AddressStr} " +
                         $"idParts='{device.DeviceIdPart1}'+'{device.DeviceIdPart2}' rssi={device.Rssi} — waiting for complete scan response");
            return;
        }

        if (Log.Every($"seen:{device.AddressStr}", TimeSpan.FromSeconds(30)))
        Log.Info($"BLE: seen {device.KindLabel} '{device.Name}' {device.AddressStr} rssi={device.Rssi} " +
                 $"addrType={device.AddressType} advType={args.AdvertisementType} " +
                 $"id='{device.DeviceId}' senderId='{device.SenderId}' vender={device.Vender} flag={device.BleFlag}");

        DeviceSeen?.Invoke(device);
    }

    private PhoneDevice MergeRotatedAddressLocked(PhoneDevice current)
    {
        var matches = _devices.Values
            .Where(other => !ReferenceEquals(other, current) && SameStableIdentity(current, other))
            .ToList();

        foreach (var old in matches)
        {
            if (string.IsNullOrWhiteSpace(current.Name)) current.Name = old.Name;
            if (current.SenderId.Length == 0) current.SenderId = old.SenderId;
            if (current.Vender == 0) current.Vender = old.Vender;
            if (current.BleFlag == 0) current.BleFlag = old.BleFlag;
            if (current.LegacyAccountId.Length == 0) current.LegacyAccountId = old.LegacyAccountId;
            _devices.Remove(old.Address);
            Log.Info($"BLE: merged rotated address {old.AddressStr} into {current.AddressStr} " +
                     $"identity={StableIdentity(current)}");
        }
        return current;
    }

    private static bool MatchesKnownIdentity(PhoneDevice known, PhoneDevice candidate)
    {
        if (known.Kind != candidate.Kind) return false;
        if (known.Address == candidate.Address) return true;

        if (known.DeviceId.Length == 16 && candidate.DeviceId.Length == 16)
            return string.Equals(known.DeviceId, candidate.DeviceId, StringComparison.OrdinalIgnoreCase);

        // Windows may currently have only the ADV half of a newly rotated address.
        // Use the six-character prefix only to WAIT for the matching full record;
        // never use it to merge/promote the partial record itself.
        if (known.DeviceId.Length == 16 && candidate.DeviceIdPart1.Length == 6)
            return known.DeviceId.StartsWith(candidate.DeviceIdPart1, StringComparison.OrdinalIgnoreCase);
        if (candidate.DeviceId.Length == 16 && known.DeviceIdPart1.Length == 6)
            return candidate.DeviceId.StartsWith(known.DeviceIdPart1, StringComparison.OrdinalIgnoreCase);

        return known.SenderId.Length > 0 &&
               candidate.SenderId.Length > 0 &&
               string.Equals(known.SenderId, candidate.SenderId, StringComparison.OrdinalIgnoreCase) &&
               known.Name.Length > 0 &&
               string.Equals(known.Name, candidate.Name, StringComparison.Ordinal);
    }

    private static bool SameStableIdentity(PhoneDevice a, PhoneDevice b)
    {
        if (a.Kind != b.Kind) return false;
        if (a.DeviceId.Length == 16 && b.DeviceId.Length == 16)
            return string.Equals(a.DeviceId, b.DeviceId, StringComparison.OrdinalIgnoreCase);
        if (a.LegacyDeviceId.Length == 12 && b.LegacyDeviceId.Length == 12)
            return string.Equals(a.LegacyDeviceId, b.LegacyDeviceId, StringComparison.OrdinalIgnoreCase);

        return a.SenderId.Length > 0 &&
               string.Equals(a.SenderId, b.SenderId, StringComparison.OrdinalIgnoreCase) &&
               a.Name.Length > 0 &&
               string.Equals(a.Name, b.Name, StringComparison.Ordinal);
    }

    public static string StableIdentity(PhoneDevice device) =>
        device.DeviceId.Length == 16
            ? $"deviceId:{device.DeviceId}"
            : device.LegacyDeviceId.Length == 12
                ? $"legacyDeviceId:{device.LegacyDeviceId}"
                : device.SenderId.Length > 0
                    ? $"senderId:{device.SenderId}"
                    : $"address:{device.AddressStr}";

    private static DateTimeOffset Min(params DateTimeOffset[] values) =>
        values.Where(value => value != default).DefaultIfEmpty(default).Min();

    private static DateTimeOffset Max(params DateTimeOffset[] values) =>
        values.Where(value => value != default).DefaultIfEmpty(default).Max();

    private static string DecodeName(byte[] payload, int offset, int maxLen)
    {
        var raw = payload.Skip(offset).Take(maxLen).ToArray();
        var text = Encoding.UTF8.GetString(raw).Trim('\0');
        if (text.EndsWith('\t'))
        {
            text = text[..^1];
            text = text.TrimEnd() + "...";
        }
        return text.Trim();
    }

    private static byte[]? ToArray(IBuffer? buffer)
    {
        if (buffer is null || buffer.Length == 0) return null;
        var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    public void Dispose() => Stop();
}
