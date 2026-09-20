using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace OShareSender;

public sealed record OShareP2pOffer(
    string SenderId,
    string Ssid,
    string Psk,
    string Mac,
    int Port,
    int? OShareVersion);

/// <summary>
/// OShare's account-free receive side. Android's P2pSenderService reads the
/// 9954 characteristic, then writes P2pInfo to 9953. No HeyTap/OConnect state is
/// involved in this service.
/// </summary>
public sealed class OShareReceiveGattServer : IDisposable
{
    public static readonly Guid ServiceUuid = new("00009955-0000-1000-8000-00805f9b34fb");
    public static readonly Guid StatusUuid = new("00009954-0000-1000-8000-00805f9b34fb");
    public static readonly Guid P2pUuid = new("00009953-0000-1000-8000-00805f9b34fb");

    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _status;
    private GattLocalCharacteristic? _p2p;
    private readonly OShareCrypto _crypto = new();
    private byte[] _statusBytes = [];
    private readonly object _writeGate = new();
    private readonly MemoryStream _writeBuffer = new();

    public bool IsRunning { get; private set; }
    public event Action<string>? StateChanged;
    public event Action<OShareP2pOffer>? OfferAccepted;

    /// <summary>Raised when the 9955 GATT advert starts/aborts — Windows only allows
    /// ONE connectable advert, so consumers must yield their own.</summary>
    public event Action? AdvertStarted;
    public event Action? AdvertAborted;
    /// <summary>Advert gave up after repeated aborts — other advertisers may claim the slot.</summary>
    public event Action? AdvertGaveUp;

    private int _advertRetrying;

    /// <summary>Set by the engine: returns true when no other advert owns the slot,
    /// so competing GATT adverts don't kick each other off in an endless loop.</summary>
    public Func<bool>? RetryGate { get; set; }

    private async void RetryAdvertAsync(GattServiceProvider? provider)
    {
        if (provider is null) return;
        if (Interlocked.CompareExchange(ref _advertRetrying, 1, 0) != 0) return;
        Log.Warn("RX: 9955 retry scheduled");
        try
        {
            // retry indefinitely — the 9955 advert is what makes the PC connectable
            // for the OShare app, and the slot usually frees up within seconds
            while (IsRunning)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (!IsRunning) return;
                if (provider.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started)
                    return;
                if (RetryGate?.Invoke() == false) continue;   // 8881 beacon owns the slot
                Log.Warn("RX: 9955 retry attempt");
                try { provider.StopAdvertising(); } catch { }
                try { provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
                {
                    IsDiscoverable = true,
                    IsConnectable = true,
                }); } catch (Exception ex) { Log.Warn($"RX: 9955 advert retry failed: {ex.Message}"); }
                await Task.Delay(1500);
                if (provider.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("RX: 9955 advertisement retry failed", ex); }
        finally { Interlocked.Exchange(ref _advertRetrying, 0); }
    }

    /// <summary>Return true after displaying a confirmation prompt to the user.</summary>
    public Func<OShareP2pOffer, Task<bool>>? ConfirmIncoming { get; set; }

    private void State(string message)
    {
        Log.Info($"RX: {message}");
        try { StateChanged?.Invoke(message); } catch { }
    }

    public async Task StartAsync(string deviceMac)
    {
        if (IsRunning) return;

        var result = await GattServiceProvider.CreateAsync(ServiceUuid);
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException($"OShare receive GATT provider failed: {result.Error}");

        var status = await result.ServiceProvider.Service.CreateCharacteristicAsync(StatusUuid,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                ReadProtectionLevel = GattProtectionLevel.Plain,
            });
        if (status.Error != BluetoothError.Success)
            throw new InvalidOperationException($"OShare 9954 creation failed: {status.Error}");

        var p2p = await result.ServiceProvider.Service.CreateCharacteristicAsync(P2pUuid,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Write |
                                           GattCharacteristicProperties.WriteWithoutResponse,
                WriteProtectionLevel = GattProtectionLevel.Plain,
            });
        if (p2p.Error != BluetoothError.Success)
            throw new InvalidOperationException($"OShare 9953 creation failed: {p2p.Error}");

        _status = status.Characteristic;
        _p2p = p2p.Characteristic;
        _status.ReadRequested += OnStatusRead;
        _p2p.WriteRequested += OnP2pWrite;
        _statusBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            state = 0,
            key = _crypto.PublicKeyB64,
            mac = string.IsNullOrWhiteSpace(deviceMac) ? "02:00:00:00:00:00" : deviceMac,
            oShare = 1,
        }));

        _provider = result.ServiceProvider;
        _provider.AdvertisementStatusChanged += (_, e) =>
        {
            State($"9955 advertisement -> {e.Status}");
            try
            {
                if (e.Status == GattServiceProviderAdvertisementStatus.Started) AdvertStarted?.Invoke();
                else if (e.Status == GattServiceProviderAdvertisementStatus.Aborted)
                {
                    // never retried by Windows on its own — keep trying to win the
                    // advertising slot back (the engine pauses the fallback on Aborted)
                    AdvertAborted?.Invoke();
                    RetryAdvertAsync(_provider);
                }
            }
            catch { }
        };
        // Mark running before StartAdvertising: Windows can raise Aborted
        // synchronously, and the retry loop must be allowed to recover it.
        IsRunning = true;
        _provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsDiscoverable = true,
            IsConnectable = true,
        });
        State("account-free OShare receive service is ready");
    }

    private async void OnStatusRead(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        try
        {
            var request = await args.GetRequestAsync();
            if (request is null) return;
            var offset = (int)request.Offset;
            var bytes = offset >= _statusBytes.Length ? [] : _statusBytes[offset..];
            request.RespondWithValue(ToBuffer(bytes));
            State($"9954 read served ({bytes.Length}B)");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"RX: 9954 read failed: {ex.Message}");
        }
    }

    private async void OnP2pWrite(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        GattWriteRequest? request = null;
        try
        {
            request = await args.GetRequestAsync();
            if (request is null) return;
            var reader = DataReader.FromBuffer(request.Value);
            var bytes = new byte[request.Value.Length];
            reader.ReadBytes(bytes);
            if (request.Offset == 0)
            {
                lock (_writeGate) _writeBuffer.SetLength(0);
            }
            lock (_writeGate)
            {
                if (_writeBuffer.Length + bytes.Length > 16 * 1024)
                    throw new InvalidOperationException("9953 payload is too large");
                _writeBuffer.Write(bytes);
            }
            try { request.Respond(); } catch { }

            string json;
            lock (_writeGate) json = Encoding.UTF8.GetString(_writeBuffer.ToArray()).Trim();
            if (json.Length == 0 || !json.EndsWith('}')) return;

            OShareP2pOffer offer;
            try { offer = ParseOffer(json); }
            catch (JsonException) { ResetWriteBuffer(); return; }
            catch (Exception ex)
            {
                ResetWriteBuffer();
                State($"rejected invalid 9953 offer: {ex.Message}");
                return;
            }
            ResetWriteBuffer();
            State($"incoming OShare offer from {offer.SenderId} ({offer.Ssid})");

            var accept = ConfirmIncoming is not null && await ConfirmIncoming(offer);
            State(accept ? "incoming offer accepted" : "incoming offer rejected");
            if (accept)
            {
                try { OfferAccepted?.Invoke(offer); }
                catch (Exception ex) { Log.Error("RX: accepted-offer handler failed", ex); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("RX: 9953 write handling failed", ex);
            try { request.Respond(); } catch { }
        }
    }

    private OShareP2pOffer ParseOffer(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = String(root, "id");
        var ssid = String(root, "ssid");
        var psk = String(root, "psk");
        var mac = String(root, "mac");
        var port = root.TryGetProperty("port", out var portValue) && portValue.TryGetInt32(out var p)
            ? p : 0;
        var key = root.TryGetProperty("key", out var keyValue) && keyValue.ValueKind == JsonValueKind.String
            ? keyValue.GetString() : null;

        if (!string.IsNullOrWhiteSpace(key))
        {
            var cipher = _crypto.DeriveSharedSecret(key);
            ssid = OShareCrypto.CtrDecryptFromB64(cipher, ssid);
            psk = OShareCrypto.CtrDecryptFromB64(cipher, psk);
            mac = OShareCrypto.CtrDecryptFromB64(cipher, mac);
        }

        if (string.IsNullOrWhiteSpace(ssid) || string.IsNullOrWhiteSpace(psk) ||
            string.IsNullOrWhiteSpace(mac) || port is < 1 or > 65535)
            throw new InvalidOperationException("offer is missing valid Wi-Fi Direct credentials");

        int? version = root.TryGetProperty("oShare", out var versionValue) &&
                       versionValue.TryGetInt32(out var v) ? v : null;
        return new OShareP2pOffer(id, ssid, psk, mac, port, version);
    }

    private static string String(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private void ResetWriteBuffer()
    {
        lock (_writeGate) _writeBuffer.SetLength(0);
    }

    private static IBuffer ToBuffer(byte[] bytes)
    {
        var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    /// <summary>Stops advertising without disposing the ECDH session, so the server
    /// can be started again later (Dispose kills the crypto and is irreversible).</summary>
    public void Stop()
    {
        try { _provider?.StopAdvertising(); } catch { }
        if (_status is not null) _status.ReadRequested -= OnStatusRead;
        if (_p2p is not null) _p2p.WriteRequested -= OnP2pWrite;
        _status = null;
        _p2p = null;
        _provider = null;
        IsRunning = false;
    }

    public void Dispose()
    {
        Stop();
        _crypto.Dispose();
    }
}
