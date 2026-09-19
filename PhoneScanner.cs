using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace CatShareSender;

public enum PhoneKind
{
    /// <summary>moe.reimu.catshare app — advertisement: 128-bit 3331 + 0xffff(27B)/0x01ff(6B) service data.</summary>
    CatShare,
    /// <summary>Stock alliance ROM (OPPO/OnePlus/Xiaomi/vivo/…) — 128-bit 3331 + vender/bleFlag service data.</summary>
    Alliance,
    /// <summary>Legacy OEM variants (0x3333/0x3334, 0x6666/0x6667, 0x8181/0x8182) — display only for now.</summary>
    Legacy
}

public sealed class PhoneDevice
{
    public ulong Address { get; init; }
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
    /// <summary>CatShare-style 4-hex sender id from the 27-byte payload.</summary>
    public string SenderId { get; set; } = "";
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
        PhoneKind.CatShare => SenderId.Length == 4,
        PhoneKind.Alliance => AllianceUuidSeen &&
                              DeviceIdPart1.Length == 6 &&
                              DeviceIdPart2.Length == 10 &&
                              DeviceId.Length == 16 &&
                              LastCompleteAdvertisement != default,
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
        PhoneKind.CatShare => "CatShare",
        PhoneKind.Alliance => $"Alliance ({BrandFromVender(Vender)})",
        _ => "Legacy OEM"
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
/// Scans for phones running 互传/CatShare in receive mode.
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
                return _devices.Values
                    .Where(device => device.HasCompleteIdentity)
                    .GroupBy(StableIdentity, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(device => device.LastCompleteAdvertisement).First())
                    .ToList();
            }
        }
    }

    public bool IsScanning { get; private set; }

    public void Start()
    {
        if (IsScanning) return;
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
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

        // Windows can deliver ADV and SCAN_RSP as separate events. Scan responses
        // carry the 27-byte name/id tail but not the 128-bit UUID AD, so recognise
        // the known service-data UUIDs only for accumulation. They are not enough
        // by themselves to make a connect candidate.
        static bool IsKnownSectionUuid(ushort u) =>
            u == 0xFFFF || u == 0x01FF ||
            u == 0x0703 || u == 0x0204 || u == 0x0704;

        PhoneDevice device;
        bool shouldPublish;
        bool shouldLogPending;
        lock (_gate)
        {
            if (!_devices.TryGetValue(args.BluetoothAddress, out device!))
            {
                var kind = sections.Any(s => s.Uuid16 is 0xFFFF or 0x01FF) ? PhoneKind.CatShare : PhoneKind.Alliance;
                ushort firstUuid = sections.Count > 0 ? sections[0].Uuid16 : (ushort)0;
                if (!hasAllianceUuid && !IsKnownSectionUuid(firstUuid))
                    return;
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
                    device.Kind = PhoneKind.CatShare;
                    device.SenderId = $"{payload[8]:x2}{payload[9]:x2}";
                    var n = DecodeName(payload, 10, 16);
                    if (n.Length > 0) device.Name = n;
                    device.Version = payload[26];
                    device.LastCompleteAdvertisement = now;
                }
                else if (s.Uuid16 == 0x01FF && payload.Length >= 2)
                {
                    device.Kind = PhoneKind.CatShare;
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
                Log.Info($"BLE: pending {device.KindLabel} advertisement {device.AddressStr} " +
                         $"idParts='{device.DeviceIdPart1}'+'{device.DeviceIdPart2}' rssi={device.Rssi} — waiting for complete scan response");
            return;
        }

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

        return a.SenderId.Length > 0 &&
               string.Equals(a.SenderId, b.SenderId, StringComparison.OrdinalIgnoreCase) &&
               a.Name.Length > 0 &&
               string.Equals(a.Name, b.Name, StringComparison.Ordinal);
    }

    public static string StableIdentity(PhoneDevice device) =>
        device.DeviceId.Length == 16
            ? $"deviceId:{device.DeviceId}"
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
