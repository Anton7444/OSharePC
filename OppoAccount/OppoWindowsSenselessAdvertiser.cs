using System.Text;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace OShareSender.OppoAccount;

/// <summary>
/// Broadcasts OPPO/OnePlus's actual native "Windows PC, senseless discovery" BLE
/// advertisement — service UUID 0xAFAF, parsed by com.oplus.pantaconnect's own
/// `toWindowsDiscoveredResult`/`packWindowsSenselessBlePacket`/
/// `toSenselessDiscoveryPacket` chain (decompiled from the com.heytap.accessory
/// system service — see OPPO_ACCOUNT_API_FINDINGS.md section 5k). This is the real,
/// first-class discovery channel OPPO built for actual Windows devices — distinct
/// from both the legacy Alliance/o.java BLE scheme (OppoAccountBleAdvertiser) and the
/// iOS-emulation OConnect GATT flow (GattLink.cs) already in this codebase.
///
/// Wire format (all offsets relative to the start of the 0xAFAF service-data payload):
///   [0..3)   magic probe: 0x9A 0x07 0xAF (com.oplus.pantaconnect.discovery.bleprovider.advertise.b#f11466e)
///   [3]      packed: version(bits 6-7, must be 0) | BleAdvertiseType(bits 2-5, SENSELESS=12) | flag(bit 0)
///            = 0x30 for version=0, type=SENSELESS, flag=0
///   [4..7)   modelId, 3 bytes (arbitrary — real devices' OEM model code; using ASCII "PC0" as a placeholder)
///   [7]      flags byte: bit7=unknown, bit6=unknown (both observed cleared in this build)
///   [8..20)  the "senseless info" sub-packet (yg/b.java#L, "toSenselessDiscoveryPacket"):
///     [8]     ServiceAdvType (bits 5-7, must be 2 = SERVICE_ADV_TYPE_ABILITY) -> 0x40
///     [9]     AbilityAdvType (bits 0-3, must be 6 = ABILITY_ADV_TYPE_SENSELESS) -> 0x06
///     [10]    AbilityAuthorizeType(bits 5-7) | flag(bit4) | flag(bit3) -- all 0 (best-effort default)
///     [11..15) "ability set" bitmask, 4 bytes -- 0 (best-effort default, meaning unconfirmed)
///     [15]    1 byte, purpose unconfirmed -- 0
///     [16..19) accountDigest, 3 bytes == OppoAccountBleHash.ComputeDsfAccountHash(ssoid, 3)
///              (confirmed correct: produces "39154E" for the account tested against a
///              real captured device on this account -- see findings doc section 5f/5h)
///     [19]    senselessDeviceId, 1 byte (matches the single-byte "senselessDeviceId=NN"
///              field observed in real `adb logcat` DisplayDevice dumps)
///
/// Total payload: 20 bytes + 2-byte UUID header = 22 bytes -- comfortably fits a single
/// legacy BLE advertising PDU (unlike the older o.java scheme, which needed two
/// separate publishers to work around the 31-byte limit).
///
/// UNVERIFIED bit-level guesses: the AbilityAuthorizeType/flag bits in byte [10], the
/// 4-byte ability-set bitmask, and byte [15] were not validated against anything in the
/// parser we read (they're stored but not checked), so defaulting them to 0 should be
/// safe, but their real semantics (what capabilities a real Windows companion app
/// advertises here) are unconfirmed. If this doesn't work, that's the first place to
/// revisit.
/// </summary>
public sealed class OppoWindowsSenselessAdvertiser : IDisposable
{
    private const ushort ServiceUuid16 = 0xAFAF;
    private static readonly byte[] MagicProbe = { 0x9A, 0x07, 0xAF };
    private const byte SenselessAdvertiseType = 0x30; // version=0, BleAdvertiseType.SENSELESS(12)<<2, flag=0
    private const byte ServiceAdvTypeAbility = 0x40;  // ServiceAdvType.SERVICE_ADV_TYPE_ABILITY(2)<<5
    private const byte AbilityAdvTypeSenseless = 0x06; // AbilityAdvType.ABILITY_ADV_TYPE_SENSELESS(6)

    private BluetoothLEAdvertisementPublisher? _publisher;

    /// <summary>This PC's own 1-byte "senselessDeviceId" — arbitrary but stable.</summary>
    public byte DeviceIdByte { get; }

    public OppoWindowsSenselessAdvertiser(byte deviceIdByte)
    {
        DeviceIdByte = deviceIdByte;
    }

    public void Start(string ssoid)
    {
        Stop();
        var accountDigest = OppoAccountBleHash.ComputeDsfAccountHash(ssoid, 3);
        var payload = BuildPayload(accountDigest, DeviceIdByte);

        var adv = new BluetoothLEAdvertisement();
        var data = new byte[2 + payload.Length];
        data[0] = (byte)(ServiceUuid16 & 0xFF);
        data[1] = (byte)(ServiceUuid16 >> 8);
        Array.Copy(payload, 0, data, 2, payload.Length);
        adv.DataSections.Add(new BluetoothLEAdvertisementDataSection { DataType = 0x16, Data = ToBuffer(data) });

        _publisher = new BluetoothLEAdvertisementPublisher(adv);
        _publisher.StatusChanged += (_, e) =>
            Log.Info($"OppoWindowsSenseless: 0xAFAF advert -> {e.Status} ({e.Error})");
        _publisher.Start();
        Log.Info($"OppoWindowsSenseless: advertising (accountDigest={Convert.ToHexString(accountDigest)}, deviceIdByte={DeviceIdByte:X2})");
    }

    public void Stop()
    {
        try { _publisher?.Stop(); } catch { }
        _publisher = null;
    }

    private static byte[] BuildPayload(byte[] accountDigest, byte deviceIdByte)
    {
        var buf = new byte[20];
        Array.Copy(MagicProbe, 0, buf, 0, 3);
        buf[3] = SenselessAdvertiseType;
        Encoding.ASCII.GetBytes("PC0").CopyTo(buf, 4); // modelId placeholder
        buf[7] = 0x00; // flags byte, unconfirmed semantics -- cleared
        buf[8] = ServiceAdvTypeAbility;
        buf[9] = AbilityAdvTypeSenseless;
        // AbilityAuthorizeType lives in bits 5-7 (com.oplus.pantaconnect.discovery.model.
        // AbilityAuthorizeType: 1=NO_ACCOUNT, 2=SAME_ACCOUNT, 3=FAMILY_ACCOUNT_GROUP; 0 is
        // not a valid enum value -- confirmed live via adb logcat, real devices reject our
        // old buf[10]=0x00 with "toSenselessDiscoveryPacket error. Unknown AbilityAuthorizeType 0"
        // and silently drop the whole advertisement, which very likely also hid us from
        // their connectable-device list. We always advertise our OPPO account's digest here,
        // so SAME_ACCOUNT(2) is the correct value: 2 << 5 = 0x40.
        buf[10] = 0x40;
        // buf[11..15) ability-set bitmask -- best-effort default (zeroed)
        buf[15] = 0x00; // unconfirmed 1-byte field
        Array.Copy(accountDigest, 0, buf, 16, 3);
        buf[19] = deviceIdByte;
        return buf;
    }

    private static IBuffer ToBuffer(byte[] data)
    {
        var w = new DataWriter();
        w.WriteBytes(data);
        return w.DetachBuffer();
    }

    public void Dispose() => Stop();
}
