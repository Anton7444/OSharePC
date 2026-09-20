using System.Security.Cryptography;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace OShareSender;

public enum AllianceAdvertisementState
{
    Stopped,
    CompleteBle,
    LimitedBle,
    Unavailable
}

/// <summary>
/// Advertises this PC as an alliance (互传联盟) sender so stock ROM receivers list it.
///
/// Wire format expected by the phone's scanner (decompiled c8/b.java k()/l()/b()):
///   ADV:      flags + 128-bit service UUID 00003331-0000-1000-8000-008123456789
///             + 0x16 service data, 16-bit uuid (bleFlag&lt;&lt;8)|vender, 6-byte deviceId[0:6]
///   SCAN_RSP: 0x16 service data, 16-bit uuid built from swapped deviceType halves,
///             27 bytes = deviceId[6:16] + name(16) + version(1)
///   The concatenated ADV+SCAN_RSP record must exceed 61 bytes — which is exactly
///   why the old two-publisher layout never matched.
///
/// PC identity defaults to vender=100 (Lenovo — the alliance's PC vendor slot)
/// with deviceType "0065" (parses back to "065", the Lenovo PC subtype).
/// </summary>
public sealed class AllianceAdvertiser : IDisposable
{
    public int Vender { get; set; } = 100;
    public int BleFlag { get; set; } = 1;              // bit0: 5 GHz capable
    /// <summary>4-hex-nibble deviceType string, e.g. "0065" → on-air bytes 00 65.</summary>
    public string DeviceType { get; set; } = "0065";
    public string DeviceName { get; set; } = Environment.MachineName;
    public string DeviceId { get; private set; } = "";
    public byte ProtocolVersion { get; set; } = 1;

    private BluetoothLEAdvertisementPublisher? _publisher;

    public bool IsRunning { get; private set; }
    public AllianceAdvertisementState State { get; private set; } = AllianceAdvertisementState.Stopped;

    /// <summary>Hex dump of the reconstructed ADV + SCAN_RSP record, for verification.</summary>
    public string LastRecordHex { get; private set; } = "";

    /// <summary>
    /// Probes what this machine's Bluetooth stack actually supports and logs it.
    /// Findings on Win10 22H2 + Microsoft inbox driver for Intel AX201 (8087:0AA7):
    ///   - Publisher.ServiceUuids → E_INVALIDARG for ANY uuid (16/32/128-bit)
    ///   - raw 0x06/0x07 data sections → "unauthorized operation"
    ///   - data sections land in the scan response, capped at ~31 bytes
    ///   - UseExtendedAdvertisement → unsupported
    ///   - GattServiceProvider.StartAdvertising(custom uuid) → Aborted
    ///   ⇒ the 62-byte alliance record cannot be emitted on such stacks; a proper
    ///   vendor driver (Intel) or Windows 11 may lift these limits.
    /// </summary>
    public static void ProbeAndLog()
    {
        // (a) publisher with a 16-bit service uuid
        try
        {
            var a = new BluetoothLEAdvertisement();
            a.ServiceUuids.Add(new Guid("0000180a-0000-1000-8000-00805f9b34fb"));
            var p = new BluetoothLEAdvertisementPublisher(a);
            p.Start(); p.Stop();
            Log.Info("BLE probe: ServiceUuids advertising = SUPPORTED");
        }
        catch (Exception ex) { Log.Warn($"BLE probe: ServiceUuids advertising = UNSUPPORTED ({ex.Message.Split('\n')[0]})"); }

        // (b) publisher with a 27-byte service data section (scan response)
        try
        {
            var a = new BluetoothLEAdvertisement();
            var w = new DataWriter();
            w.WriteBytes(new byte[] { 0x00, 0x65 }.Concat(new byte[27]).ToArray());
            a.DataSections.Add(new BluetoothLEAdvertisementDataSection { DataType = 0x16, Data = w.DetachBuffer() });
            var p = new BluetoothLEAdvertisementPublisher(a);
            p.Start(); p.Stop();
            Log.Info("BLE probe: 27-byte service-data section = SUPPORTED");
        }
        catch (Exception ex) { Log.Warn($"BLE probe: 27-byte service-data section = UNSUPPORTED ({ex.Message.Split('\n')[0]})"); }
    }

    public void Start(string lanMacHex12)
    {
        if (IsRunning) return;
        EnsureDeviceId(lanMacHex12);

        var adv = new BluetoothLEAdvertisement();
        adv.ServiceUuids.Add(PhoneScanner.AllianceServiceUuid);

        // ADV service data: uuid=(bleFlag<<8)|vender → LE bytes [vender, bleFlag]
        var advSection = new BluetoothLEAdvertisementDataSection
        {
            DataType = 0x16,
            Data = BuildBuffer(new[] { (byte)Vender, (byte)BleFlag }, DeviceId[..6])
        };
        adv.DataSections.Add(advSection);

        // Scan response service data: deviceType halves swapped → uuid value
        //   uuidString = dt[2:4] + dt[0:2]; on-air LE bytes = [dt[0:2], dt[2:4]]
        var dtBytes = new[]
        {
            Convert.ToByte(DeviceType.Substring(0, 2), 16),
            Convert.ToByte(DeviceType.Substring(2, 2), 16)
        };
        var rspSection = new BluetoothLEAdvertisementDataSection
        {
            DataType = 0x16,
            // OShare's Android scanner accepts the scan-response section only
            // when its payload is exactly 27 bytes: id[6..16], name[16], version.
            // The old 10-byte payload made the PC visible to a sniffer but not to
            // ShareActivity.deviceScanner().
            Data = BuildBuffer(dtBytes, BuildScanResponsePayload())
        };
        adv.DataSections.Add(rspSection);

        LastRecordHex = BuildRecordHex(dtBytes);
        Log.Info($"BLE: expected on-air record (ADV+RSP, must be >61B): {LastRecordHex}");

        // Legacy publisher cannot accept a separate scan-response object. Sending
        // both sections in one BluetoothLEAdvertisement makes Windows reject the
        // payload with E_INVALIDARG, including on FastConnect 7800. Keep legacy
        // mode to the valid primary ADV section only.
        var legacyAdv = new BluetoothLEAdvertisement();
        // FastConnect/Windows rejects custom 128-bit ServiceUuids (E_INVALIDARG),
        // although raw service-data sections are supported. The phone parser keys
        // off the 0x16 alliance data, so omit the UUID in the legacy advertisement.
        legacyAdv.DataSections.Add(advSection);
        if (TryStartPublisher(legacyAdv, useExtended: false))
        {
            IsRunning = true;
            State = AllianceAdvertisementState.LimitedBle;
            Log.Info($"BLE: advertising (legacy) as '{DeviceName}' vender={Vender} bleFlag={BleFlag} " +
                     $"deviceType={DeviceType} deviceId={DeviceId}");
            return;
        }

        // Fallback: extended advertising (single packet up to 254B — no SCAN_RSP split needed).
        // Caveat: byte offsets in Android's ScanRecord may differ if the stack omits the
        // flags AD in extended PDUs — verify live with the phone before trusting it.
        if (TryStartPublisher(adv, useExtended: true))
        {
            IsRunning = true;
            State = AllianceAdvertisementState.CompleteBle;
            Log.Info($"BLE: advertising (EXTENDED) as '{DeviceName}' vender={Vender} bleFlag={BleFlag} " +
                     $"deviceType={DeviceType} deviceId={DeviceId}");
            return;
        }

        _publisher = null;
        IsRunning = false;
        State = AllianceAdvertisementState.Unavailable;
        Log.Warn(
            "BLE: Windows refused the alliance advertisement payload — stock phones will NOT see this PC. " +
            "The OShare-app flow does not need advertising and still works. " +
            "Fix options: install the vendor Bluetooth driver, use Windows 11 with an " +
            "extended-advertising-capable radio (e.g. Qualcomm FastConnect 7800), or use a BT 5.x USB dongle.");
    }

    /// <summary>Builds the phone-visible record without touching the Bluetooth adapter.</summary>
    public string PreviewRecordHex(string lanMacHex12)
    {
        EnsureDeviceId(lanMacHex12);
        var dtBytes = new[]
        {
            Convert.ToByte(DeviceType.Substring(0, 2), 16),
            Convert.ToByte(DeviceType.Substring(2, 2), 16)
        };
        return BuildRecordHex(dtBytes);
    }

    private void EnsureDeviceId(string lanMacHex12)
    {
        if (!string.IsNullOrEmpty(DeviceId)) return;
        // deviceId = 12 hex chars (MAC-like) + 4 hex chars (account hash)
        DeviceId = lanMacHex12.Length == 12 ? lanMacHex12 : "000000000000";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(DeviceId));
        DeviceId += $"{hash[0]:x2}{hash[1]:x2}";
    }

    private bool TryStartPublisher(BluetoothLEAdvertisement advertisement, bool useExtended)
    {
        var publisher = new BluetoothLEAdvertisementPublisher(advertisement);
        publisher.StatusChanged += (_, e) =>
            Log.Info($"BLE: publisher ({(useExtended ? "extended" : "legacy")}) status {e.Status} (error={e.Error})");
        if (useExtended)
        {
            try { publisher.UseExtendedAdvertisement = true; }
            catch (Exception ex)
            {
                Log.Warn($"BLE: extended advertising unavailable ({ex.Message.Split('\n')[0]})");
                return false;
            }
        }
        try
        {
            publisher.Start();
            _publisher = publisher;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"BLE: {(useExtended ? "extended" : "legacy")} advertising rejected by Windows ({ex.Message.Split('\n')[0]})");
            return false;
        }
    }

    /// <summary>128-bit Bluetooth UUID as it goes on air: little-endian of the canonical
    /// big-endian byte form. (Guid.ToByteArray() is mixed-endian and must not be used raw.)</summary>
    internal static byte[] GuidToAirBytes(Guid guid)
    {
        var b = guid.ToByteArray();
        Array.Reverse(b, 0, 4);   // first group  BE -> matches canonical
        Array.Reverse(b, 4, 2);
        Array.Reverse(b, 6, 2);
        Array.Reverse(b);         // full reverse: canonical BE -> on-air LE
        return b;
    }

    private static IBuffer BuildBuffer(byte[] uuidLe, string asciiPayload)
    {
        var payload = Encoding.ASCII.GetBytes(asciiPayload);
        return BuildBuffer(uuidLe, payload);
    }

    private static IBuffer BuildBuffer(byte[] uuidLe, byte[] payload)
    {
        var buf = new byte[2 + payload.Length];
        buf[0] = uuidLe[0];
        buf[1] = uuidLe[1];
        Array.Copy(payload, 0, buf, 2, payload.Length);
        return ToBuffer(buf);
    }

    private byte[] BuildScanResponsePayload()
    {
        var payload = new byte[27];
        Encoding.ASCII.GetBytes(DeviceId[6..16]).CopyTo(payload, 0);
        BuildNameBytes().CopyTo(payload, 10);
        payload[26] = ProtocolVersion;
        return payload;
    }

    private static IBuffer ToBuffer(byte[] data)
    {
        var w = new DataWriter();
        w.WriteBytes(data);
        return w.DetachBuffer();
    }

    /// <summary>Reconstructs what Android would concatenate (flags + ADV, then scan response)
    /// and hex-dumps it so the ≥62-byte requirement can be checked without a sniffer.</summary>
    private string BuildRecordHex(byte[] dtBytes)
    {
        // ADV: flags(02 01 06) + 0x07/complete-128uuid + 0x16(6B payload)
        // AD length counts type+data, excluding the length byte itself.
        var uuidBytes = GuidToAirBytes(PhoneScanner.AllianceServiceUuid);
        var adv = new List<byte> { 0x02, 0x01, 0x06 };
        adv.Add((byte)(1 + 16));
        adv.Add(0x07);
        adv.AddRange(uuidBytes);
        adv.Add((byte)(1 + 2 + 6));
        adv.Add(0x16);
        adv.Add((byte)Vender);
        adv.Add((byte)BleFlag);
        adv.AddRange(Encoding.ASCII.GetBytes(DeviceId[..6]));

        // SCAN_RSP: 0x16(2+27B payload)
        var rsp = new List<byte> { (byte)(1 + 2 + 27), 0x16 };
        rsp.AddRange(dtBytes);
        rsp.AddRange(BuildScanResponsePayload());

        var all = adv.Concat(rsp).ToArray();
        return $"{adv.Count}+{rsp.Count}={all.Length}B :: {Convert.ToHexString(all)}";
    }

    private byte[] BuildNameBytes()
    {
        var name = Encoding.UTF8.GetBytes(DeviceName);
        if (name.Length <= 16)
        {
            var nb = new byte[16];
            Array.Copy(name, nb, name.Length);
            return nb;
        }
        // truncate at a UTF-8 boundary, mark with '\t' like the app does
        var take = 15;
        while (take > 0 && (name[take] & 0xC0) == 0x80) take--;
        var text = Encoding.UTF8.GetString(name, 0, take);
        var marked = Encoding.UTF8.GetBytes(text.TrimEnd() + "\t");
        var outBuf = new byte[16];
        Array.Copy(marked, outBuf, Math.Min(16, marked.Length));
        return outBuf;
    }

    public void Stop()
    {
        if (_publisher is not null)
        {
            try { _publisher.Stop(); } catch { }
            _publisher = null;
        }
        IsRunning = false;
        State = AllianceAdvertisementState.Stopped;
        Log.Info("BLE: advertising stopped");
    }

    public void Dispose() => Stop();
}
