using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace OShareSender;

/// <summary>What the phone told us on the 0x9954 status read.</summary>
public sealed partial class GattLink
{
    /// <summary>Contacts-only receivers run their 9999 server on demand. The trigger is a client reading the DCP
    /// (0xBB15) characteristic 0x9996 (their OShare app then restarts its GATT server, which also drops this link).
    /// Best effort: errors are expected and ignored.</summary>
    public static async Task WakeContactsReceiverAsync(ulong bluetoothAddress, BluetoothAddressType addressType, CancellationToken ct)
    {
        try
        {
            using var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress, addressType).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (dev is null) return;
            using var session = await GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(4), ct);
            if (session.CanMaintainConnection) session.MaintainConnection = true;
            var svcs = await dev.GetGattServicesForUuidAsync(new Guid("0000bb15-0000-1000-8000-00805f9b34fb"), BluetoothCacheMode.Uncached)
                .AsTask(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (svcs.Status != GattCommunicationStatus.Success || svcs.Services.Count == 0) { Log.Info($"CONTACTS: wake: bb15 lookup {svcs.Status}"); return; }
            var chars = await svcs.Services[0].GetCharacteristicsForUuidAsync(new Guid("00009996-0000-1000-8000-00805f9b34fb"), BluetoothCacheMode.Uncached)
                .AsTask(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) { Log.Info($"CONTACTS: wake: 9996 lookup {chars.Status}"); return; }
            try
            {
                var r = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(3), ct);
                Log.Info($"CONTACTS: wake: 9996 read status {r.Status}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Info($"CONTACTS: wake: 9996 read ended with {ex.GetType().Name} (expected — the tablet restarts its GATT server)");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log.Info($"CONTACTS: wake failed: {ex.Message}"); }
    }

    /// <summary>Checks that the receiver's 9999 service really answers (read 0x9897), then leaves it clean (the read starts
    /// a pending receive task on the phone, which its own cancel command clears). False = listed but not answering.</summary>
    public async Task<bool> ProbeReceiverAsync(CancellationToken ct)
    {
        if (OConnectReadChar is null) return false;
        try
        {
            var r = await OConnectReadChar.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(3), ct);
            if (r.Status != GattCommunicationStatus.Success) return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
        try
        {
            if (OConnectWifiChar is not null && OConnectCancelChar is not null)
            {
                await OConnectWifiChar.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(3), ct);
                await OConnectCancelChar.WriteValueAsync(ToBuffer(new byte[] { 2 }), GattWriteOption.WriteWithResponse).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(3), ct);
                await Task.Delay(800, ct); // let the receiver finish clearing the task before the next 9897 read
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        return true;
    }
}
