using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace CatShareSender;

/// <summary>
/// Many Windows BT stacks abort GattServiceProvider.StartAdvertising outright
/// (status Aborted — see --advprobe), so the hosted GATT services are invisible
/// to the phone's scanner and the PC never shows up in the send sheet.
///
/// Raw 0x16 service-data sections ARE publishable through
/// BluetoothLEAdvertisementPublisher (verified: 9955, 8881, 0x21 3331 all Started).
/// Android maps 0x16 into ScanRecord.getServiceData(), which the 互传/CatShare
/// scanners match on; the phone then connects to this address and the GATT
/// server (which works regardless of the provider's own advertising) answers.
/// </summary>
public sealed class GattServiceFallbackAdvertiser : IDisposable
{
    private BluetoothLEAdvertisementPublisher? _publisher;
    private int _restarts;
    private bool _startedOk;
    private bool _desired;   // false while paused (a connectable GATT advert owns the slot)
    private CancellationTokenSource? _delayCts;

    public event Action<string>? StatusChanged;

    /// <summary>Stop advertising and ignore recycling until Resume — the fallback advert
    /// is non-connectable, so a live connectable GATT advert always wins the slot.</summary>
    public void Pause()
    {
        _delayCts?.Cancel();
        _desired = false;
        Stop();
    }

    /// <summary>Resume advertising (when no connectable GATT advert is running).</summary>
    public void Resume()
    {
        ResumeAfter(TimeSpan.Zero);
    }

    /// <summary>Resume after a grace delay so a retrying GATT advert can claim the
    /// slot first; the fallback only comes back if the GATT adverts stay dead.</summary>
    public void ResumeAfter(TimeSpan delay)
    {
        _delayCts?.Cancel();
        _desired = true;
        if (delay <= TimeSpan.Zero) { Start(); return; }
        _delayCts = new CancellationTokenSource();
        var token = _delayCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, token); }
            catch (OperationCanceledException) { return; }
            if (_desired && _publisher is null) Start();
        });
    }

    public void Start()
    {
        Stop();
        _desired = true;
        var adv = new BluetoothLEAdvertisement();

        // 9955 — CatShare/alliance receive service (GattServerService.kt)
        adv.DataSections.Add(new BluetoothLEAdvertisementDataSection
        { DataType = 0x16, Data = Buf(new byte[] { 0x55, 0x99, 0x01 }) });

        // 8881 — stock 互传 PC discovery beacon
        adv.DataSections.Add(new BluetoothLEAdvertisementDataSection
        { DataType = 0x16, Data = Buf(new byte[] { 0x81, 0x88, 0x01 }) });

        // 3331 — alliance custom-base UUID as 128-bit service data (OEM variant parsers)
        adv.DataSections.Add(new BluetoothLEAdvertisementDataSection
        { DataType = 0x21, Data = Buf(AllianceAdvertiser.GuidToAirBytes(PhoneScanner.AllianceServiceUuid)) });

        _restarts = 0;
        _startedOk = false;
        _publisher = new BluetoothLEAdvertisementPublisher(adv);
        _publisher.StatusChanged += OnStatus;
        _publisher.Start();
        Log.Info("DISC: fallback service-data advert (9955 + 8881 + 3331) started");
    }

    private async void OnStatus(BluetoothLEAdvertisementPublisher sender, BluetoothLEAdvertisementPublisherStatusChangedEventArgs e)
    {
        Log.Info($"DISC: fallback service-data advert -> {e.Status} ({e.Error})");
        if (e.Status == BluetoothLEAdvertisementPublisherStatus.Started)
        {
            _startedOk = true;
            _restarts = 0;
        }
        else if (e.Status == BluetoothLEAdvertisementPublisherStatus.Waiting && e.Error == BluetoothError.ResourceInUse)
        {
            if (!_desired) return;   // paused — a connectable GATT advert owns the slot

            // the legacy advertising slot is held (leaked by a killed process, or
            // grabbed by another app). Recycling our publisher usually reclaims it.
            if (!_startedOk && _restarts < 6)
            {
                _restarts++;
                Log.Info($"DISC: fallback advert ResourceInUse — recycling (attempt {_restarts}/6)");
                try { sender.Stop(); } catch { }
                await Task.Delay(TimeSpan.FromSeconds(5 + 3 * _restarts));
                try { sender.Start(); } catch (Exception ex) { Log.Warn($"DISC: fallback advert restart failed: {ex.Message}"); }
            }
            else if (_startedOk)
            {
                // was running and lost the slot — keep trying to get it back
                Log.Info("DISC: fallback advert lost the slot — recycling");
                try { sender.Stop(); } catch { }
                await Task.Delay(TimeSpan.FromSeconds(5));
                try { sender.Start(); } catch { }
            }
            else
            {
                StatusChanged?.Invoke("discovery beacon blocked (ResourceInUse) — toggle Bluetooth off/on if the phone cannot find this PC");
            }
        }
        try { StatusChanged?.Invoke($"discovery beacon: {e.Status}"); } catch { }
    }

    public void Stop()
    {
        try { _publisher?.Stop(); } catch { }
        _publisher = null;
    }

    private static IBuffer Buf(byte[] d)
    {
        var w = new DataWriter();
        w.WriteBytes(d);
        return w.DetachBuffer();
    }

    public void Dispose() => Stop();
}
