using System.Text;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace OShareSender.OppoAccount;

/// <summary>
/// Broadcasts the two BLE service-data blocks OPPO/OnePlus Share's native scanner
/// (com.oplus.oshare.ble.impl.o#q, service UUIDs 0x3333 + 0x6667) reads to decide
/// whether a discovered device is on the "same account" — see
/// OPPO_ACCOUNT_API_FINDINGS.md section 5f for the full reverse-engineering trail.
///
/// Wire format (each field is fixed-width; the real app pads short strings with
/// trailing NUL bytes and trims them back out on receive):
///
///   Service data for 0x3333 (24 bytes):
///     [0]      commProtocolVersion (byte)      -- kept &lt;17 so the peer expects the
///                                                   minimal (4-byte) 0x6667 block below,
///                                                   not the 20-byte one with accountName
///     [1..17)  deviceName, UTF-8, NUL-padded to 16 bytes
///     [17..23) deviceId, 6 raw bytes (this device's own public identity)
///     [23]     advBrandType (byte) -- 0 (unused by us; only meaningful to OPPO's own vendor icons)
///
///   Service data for 0x6667 (4 bytes, since commProtocolVersion &lt; 17):
///     [0]      advBrandType (byte)
///     [1..4)   accountId, 3 ASCII chars -- OppoAccountBleHash.ComputeAccountId(ssoid, deviceId)
///
/// Each block is broadcast from its OWN BluetoothLEAdvertisementPublisher: combined,
/// the two service-data structures (32 bytes) plus mandatory flags would not fit in a
/// single legacy 31-byte advertising PDU (real devices split this across the primary
/// advertisement and the scan-response PDU, which Windows' public advertising API
/// does not let us address directly) — two independent publishers is the practical
/// equivalent and keeps each comfortably under the single-PDU limit.
/// </summary>
public sealed class OppoAccountBleAdvertiser : IDisposable
{
    public const byte ProtocolVersion = 16; // < 17: peer expects the short 0x6667 block

    private BluetoothLEAdvertisementPublisher? _deviceInfoPublisher;
    private BluetoothLEAdvertisementPublisher? _accountPublisher;

    /// <summary>This PC's own public BLE identity for this scheme — 6 raw bytes,
    /// persisted (see SettingsStore.OppoBleDeviceId) so it's stable across restarts;
    /// both this PC and any peer use it as the AES key input.</summary>
    public string DeviceId { get; }

    /// <summary>Display name advertised in the clear (truncated/padded to 16 UTF-8 bytes).</summary>
    public string DeviceName { get; }

    public OppoAccountBleAdvertiser(string deviceId, string deviceName)
    {
        if (deviceId.Length != 6)
            throw new ArgumentException("deviceId must be exactly 6 characters (used as 6 raw bytes)", nameof(deviceId));
        DeviceId = deviceId;
        DeviceName = deviceName;
    }

    /// <summary>Starts (or restarts) both advertisements for the given account.
    /// <paramref name="ssoid"/> is the real, logged-in OPPO/HeyTap account id (see
    /// OPPO_ACCOUNT_API_FINDINGS.md section 5e for how to obtain it).</summary>
    public void Start(string ssoid)
    {
        Stop();
        var accountId = OppoAccountBleHash.ComputeAccountId(ssoid, DeviceId);
        if (accountId.Length != 3)
        {
            Log.Warn($"OppoBle: computed accountId has unexpected length {accountId.Length} — not advertising");
            return;
        }

        var deviceInfoPayload = BuildDeviceInfoPayload();
        var accountPayload = BuildAccountPayload(accountId);

        _deviceInfoPublisher = MakePublisher(0x3333, deviceInfoPayload, "0x3333 (deviceName/deviceId)");
        _accountPublisher = MakePublisher(0x6667, accountPayload, "0x6667 (accountId)");
        _deviceInfoPublisher.Start();
        _accountPublisher.Start();
        Log.Info($"OppoBle: advertising same-account beacon (deviceId={DeviceId}, accountId={accountId})");
    }

    public void Stop()
    {
        try { _deviceInfoPublisher?.Stop(); } catch { }
        try { _accountPublisher?.Stop(); } catch { }
        _deviceInfoPublisher = null;
        _accountPublisher = null;
    }

    private byte[] BuildDeviceInfoPayload()
    {
        var buf = new byte[24];
        buf[0] = ProtocolVersion;
        WriteFixedUtf8(buf, 1, 16, DeviceName);
        var idBytes = Encoding.UTF8.GetBytes(DeviceId);
        Array.Copy(idBytes, 0, buf, 17, Math.Min(6, idBytes.Length));
        buf[23] = 0; // advBrandType — unused
        return buf;
    }

    private static byte[] BuildAccountPayload(string accountId)
    {
        var buf = new byte[4];
        buf[0] = 0; // advBrandType
        var idBytes = Encoding.UTF8.GetBytes(accountId);
        Array.Copy(idBytes, 0, buf, 1, Math.Min(3, idBytes.Length));
        return buf;
    }

    private static void WriteFixedUtf8(byte[] dest, int offset, int width, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var n = Math.Min(width, bytes.Length);
        Array.Copy(bytes, 0, dest, offset, n);
        // remaining bytes stay 0x00 (NUL padding), matching what the real app trims on receive
    }

    private static BluetoothLEAdvertisementPublisher MakePublisher(ushort uuid16, byte[] payload, string label)
    {
        var adv = new BluetoothLEAdvertisement();
        var data = new byte[2 + payload.Length];
        data[0] = (byte)(uuid16 & 0xFF);
        data[1] = (byte)(uuid16 >> 8);
        Array.Copy(payload, 0, data, 2, payload.Length);
        adv.DataSections.Add(new BluetoothLEAdvertisementDataSection { DataType = 0x16, Data = ToBuffer(data) });

        var publisher = new BluetoothLEAdvertisementPublisher(adv);
        publisher.StatusChanged += (sender, e) =>
            Log.Info($"OppoBle: {label} advert -> {e.Status} ({e.Error})");
        return publisher;
    }

    private static IBuffer ToBuffer(byte[] data)
    {
        var w = new DataWriter();
        w.WriteBytes(data);
        return w.DetachBuffer();
    }

    public void Dispose() => Stop();
}
