using System.Text;
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
    public string AddressStr => FormatAddress(Address);
    public string Name { get; set; } = "";
    public PhoneKind Kind { get; set; }
    public int Vender { get; set; }
    public int BleFlag { get; set; }
    /// <summary>16-char alliance deviceId (12 hex MAC + 4 hex suffix), when parseable.</summary>
    public string DeviceId { get; set; } = "";
    /// <summary>deviceId[0:6] from the ADV service data (may arrive in a separate event).</summary>
    public string DeviceIdPart1 { get; set; } = "";
    /// <summary>deviceId[6:16] from the scan-response service data.</summary>
    public string DeviceIdPart2 { get; set; } = "";
    /// <summary>CatShare-style 4-hex sender id from the 27-byte payload.</summary>
    public string SenderId { get; set; } = "";
    public int Version { get; set; }
    public short Rssi { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public int SeenCount { get; set; }

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
/// The critical fix vs. previous attempts: the alliance scanner on the phone
/// (d8/o.java + c8/b.java) only accepts advertisements carrying the 128-bit
/// service UUID 00003331-0000-1000-8000-008123456789 (custom base) — a plain
/// 16-bit 0x3331 AD (standard base) never matches. The same UUID is what the
/// phones themselves broadcast, so here we scan for it and parse the service
/// data sections structurally (no raw-packet offset guessing on Windows).
/// </summary>
public sealed class PhoneScanner : IDisposable
{
    public static readonly Guid AllianceServiceUuid = new("00003331-0000-1000-8000-008123456789");

    private BluetoothLEAdvertisementWatcher? _watcher;
    private readonly Dictionary<ulong, PhoneDevice> _devices = new();
    private readonly object _gate = new();

    public event Action<PhoneDevice>? DeviceSeen;
    public event Action<ulong>? DeviceExpired;

    public IReadOnlyList<PhoneDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                // A watcher can report the same advertisement more than once.
                // Expose one freshest record per stable identity to the bridge.
                return _devices.Values
                    .GroupBy(StableIdentity, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(device => device.LastSeen).First())
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

    /// <summary>The phone rotates its BLE address; find the freshest record matching
    /// the known device (by deviceId/senderId/name) so connect attempts use a current
    /// address. Returns the same instance when nothing fresher exists.</summary>
    public PhoneDevice? FindFresh(PhoneDevice known)
    {
        lock (_gate)
        {
            PhoneDevice? best = null;
            foreach (var dev in _devices.Values)
            {
                if (dev.Address == known.Address) { best = dev; continue; }
                bool sameId = known.DeviceId.Length > 0 && dev.DeviceId == known.DeviceId;
                bool sameSender = known.SenderId.Length > 0 && dev.SenderId == known.SenderId;
                bool sameName = known.Name.Length > 0 && dev.Name == known.Name && dev.Kind == known.Kind;
                if (!sameId && !sameSender && !sameName) continue;
                if (best is null || dev.LastSeen > best.LastSeen) best = dev;
            }
            if (best is not null && best.LastSeen > known.LastSeen) return best;
            return known;
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

        // Windows can deliver the ADV and the SCAN_RSP as separate events. Scan
        // responses carry the 27-byte name section but NOT the 128-bit uuid AD,
        // so also recognise known section uuids and — above all — keep merging
        // into the device we already track for this address instead of replacing it.
        static bool IsKnownSectionUuid(ushort u) =>
            u == 0xFFFF || u == 0x01FF ||          // CatShare app
            u == 0x0703 || u == 0x0204 || u == 0x0704; // OPPO / OnePlus / realme deviceType halves

        PhoneDevice device;
        lock (_gate)
        {
            if (!_devices.TryGetValue(args.BluetoothAddress, out device!))
            {
                var kind = sections.Any(s => s.Uuid16 is 0xFFFF or 0x01FF) ? PhoneKind.CatShare : PhoneKind.Alliance;
                ushort firstUuid = sections.Count > 0 ? sections[0].Uuid16 : (ushort)0;
                if (!hasAllianceUuid && !IsKnownSectionUuid(firstUuid))
                    return;   // not a 互传/CatShare device
                device = new PhoneDevice { Address = args.BluetoothAddress, Kind = kind };
                _devices[device.Address] = device;
            }

            device.Rssi = args.RawSignalStrengthInDBm;
            device.LastSeen = DateTimeOffset.Now;
            device.SeenCount++;

            foreach (var s in sections)
            {
                var payload = s.Payload;

                if (s.Uuid16 == 0xFFFF && payload.Length == 27)
                {
                    // CatShare app scan response: [0..7]=0, [8..9]=senderId, [10..25]=name, [26]=status
                    device.Kind = PhoneKind.CatShare;
                    device.SenderId = $"{payload[8]:x2}{payload[9]:x2}";
                    var n = DecodeName(payload, 10, 16);
                    if (n.Length > 0) device.Name = n;
                    device.Version = payload[26];
                }
                else if (s.Uuid16 == 0x01FF && payload.Length >= 2)
                {
                    device.Kind = PhoneKind.CatShare;
                }
                else if ((hasAllianceUuid || device.Kind == PhoneKind.Alliance) && payload.Length is 6 or 27)
                {
                    // Stock alliance: 6B section uuid = (bleFlag<<8)|vender → [vender, bleFlag]
                    device.Kind = PhoneKind.Alliance;
                    if (payload.Length == 6)
                    {
                        device.Vender = s.Raw![0];
                        device.BleFlag = s.Raw[1];
                        device.DeviceIdPart1 = Encoding.ASCII.GetString(payload).Trim('\0');
                    }
                    else
                    {
                        // 27B section uuid bytes = deviceType halves (e.g. 0x00 0x65 → "065")
                        device.DeviceIdPart2 = Encoding.ASCII.GetString(payload[0..10]).Trim('\0');
                        var n = DecodeName(payload, 10, 16);
                        if (n.Length > 0) device.Name = n;
                        device.Version = payload[26];
                    }
                    device.DeviceId = (device.DeviceIdPart1 + device.DeviceIdPart2).Trim('\0');
                }
            }

            // stock alliance phones don't broadcast a LocalName AD — but other
            // advertisers do, so keep it as a fallback when no section name arrived
            if (string.IsNullOrWhiteSpace(device.Name) && !string.IsNullOrWhiteSpace(adv.LocalName))
                device.Name = adv.LocalName;

            // Merge a rotated BLE address into the current record when the
            // advertisement contains a stable application identity. Never use
            // the display name alone: two phones can legitimately share it.
            device = MergeRotatedAddressLocked(device);
        }

        Log.Info($"BLE: seen {device.KindLabel} '{device.Name}' {device.AddressStr} rssi={device.Rssi} " +
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
            if (current.DeviceId.Length == 0) current.DeviceId = old.DeviceId;
            if (current.SenderId.Length == 0) current.SenderId = old.SenderId;
            if (current.Vender == 0) current.Vender = old.Vender;
            if (current.BleFlag == 0) current.BleFlag = old.BleFlag;
            _devices.Remove(old.Address);
            Log.Info($"BLE: merged rotated address {old.AddressStr} into {current.AddressStr} " +
                     $"identity={StableIdentity(current)}");
        }
        return current;
    }

    private static bool SameStableIdentity(PhoneDevice a, PhoneDevice b)
    {
        if (a.Kind != b.Kind) return false;
        if (a.DeviceId.Length > 0 && b.DeviceId.Length > 0)
            return string.Equals(a.DeviceId, b.DeviceId, StringComparison.OrdinalIgnoreCase);

        // CatShare's sender id is short, so require the advertised name too.
        // This handles address rotation without merging unrelated same-name
        // devices that do not expose a stronger identity.
        return a.SenderId.Length > 0 &&
               string.Equals(a.SenderId, b.SenderId, StringComparison.OrdinalIgnoreCase) &&
               a.Name.Length > 0 &&
               string.Equals(a.Name, b.Name, StringComparison.Ordinal);
    }

    public static string StableIdentity(PhoneDevice device) =>
        device.DeviceId.Length > 0
            ? $"deviceId:{device.DeviceId}"
            : device.SenderId.Length > 0
                ? $"senderId:{device.SenderId}"
                : $"address:{device.AddressStr}";

    private static string DecodeName(byte[] payload, int offset, int maxLen)
    {
        var raw = payload.Skip(offset).Take(maxLen).ToArray();
        var text = Encoding.UTF8.GetString(raw).Trim('\0');
        if (text.EndsWith('\t'))
        {
            text = text[..^1];
            // cut back to a valid UTF-8 boundary the same way the app does
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
