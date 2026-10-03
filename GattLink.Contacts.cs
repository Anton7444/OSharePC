using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace OShareSender;

/// <summary>What the phone told us on the 0x9954 status read.</summary>
public sealed partial class GattLink
{
    // Contacts-only receivers run their 9999 server on demand; a client reading the DCP (0xBB15) characteristic
    // 0x9996 starts it (their OShare app then restarts its GATT server, which may also drop the reading link).

    /// <summary>Finds the DCP service 0xBB15 and its characteristics (from the database Windows has just read when
    /// possible). Best effort.</summary>
    private async Task EnumerateDcpAsync(TimeSpan timeout, bool fresh = false)
    {
        if (_device is null) return;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var svcs = await _device.GetGattServicesForUuidAsync(DcpServiceUuid, fresh ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached).AsTask(cts.Token);
            if (!fresh && (svcs.Status != GattCommunicationStatus.Success || svcs.Services.Count == 0))
                svcs = await _device.GetGattServicesForUuidAsync(DcpServiceUuid, BluetoothCacheMode.Uncached).AsTask(cts.Token);
            if (svcs.Status != GattCommunicationStatus.Success || svcs.Services.Count == 0)
            {
                Log.Info($"BLE: DCP 0xBB15 lookup {svcs.Status} ({svcs.Services.Count} service(s))");
                return;
            }
            foreach (var svc in svcs.Services)
            {
                if (!_services.Contains(svc)) _services.Add(svc);
                var chars = await svc.GetCharacteristicsAsync(fresh ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached).AsTask(cts.Token);
                if (!fresh && (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0))
                    chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cts.Token);
                if (chars.Status != GattCommunicationStatus.Success) continue;
                foreach (var c in chars.Characteristics)
                {
                    if (c.Uuid == WifiCharUuid) _dcpWifiChar = c;
                    else if (c.Uuid == CancelCharUuid) _dcpCancelChar = c;
                    else if (c.Uuid == IBeaconCharUuid) IBeaconChar ??= c;
                }
                if (Log.Every("dcp-bb15-chars", TimeSpan.FromMinutes(10)))
                    Log.Info($"BLE: DCP 0xBB15 characteristics: [{string.Join(", ", chars.Characteristics.Select(c => ShortUuid(c.Uuid)))}]");
            }
        }
        catch (Exception ex) { Log.Info($"BLE: DCP 0xBB15 enumeration failed: {ex.GetType().Name} {ex.Message}"); }
    }

    public enum WakeOutcome
    {
        /// <summary>9999 came up on this same link.</summary>
        ServiceUp,
        /// <summary>The receiver dropped the link while restarting its server: reconnect to the same address now.</summary>
        LinkDropped,
        /// <summary>A request went unanswered; the link is poisoned.</summary>
        NotResponding,
        Failed,
    }

    /// <summary>Starts the receiver's 9999 server over THIS link (no separate wake connection): read 0x9996 on the DCP
    /// service, then clear the receive task that read opens (the 9995 cancel is accepted right after our own read,
    /// otherwise the next handshake finds the receiver busy), then wait for 9999 to appear on the same link.</summary>
    public async Task<WakeOutcome> WakeOnLinkAsync(CancellationToken ct, bool clearViaDcp = false)
    {
        if (_dcpWifiChar is null) await EnumerateDcpAsync(TimeSpan.FromSeconds(3));
        if (_dcpWifiChar is null)
        {
            Log.Info("CONTACTS: wake: no 0x9996 on the DCP service of this link");
            return WakeOutcome.Failed;
        }
        SendTimeline.Mark("wake-read");
        try
        {
            var r = await _dcpWifiChar.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(4), ct);
            Log.Info($"CONTACTS: wake: 9996 read status {r.Status}");
            // What an Android sender gets here ("p2pMac&version&name&randomPort&..."), kept to study that path.
            if (r.Status == GattCommunicationStatus.Success && r.Value is { Length: > 0 } v)
            {
                using var reader = DataReader.FromBuffer(v);
                var bytes = new byte[v.Length];
                reader.ReadBytes(bytes);
                Log.Info($"CONTACTS: wake: 9996 value: {System.Text.Encoding.UTF8.GetString(bytes)}");
            }
            if (r.Status == GattCommunicationStatus.Success && IsConnected && _dcpCancelChar is not null)
            {
                var w = await _dcpCancelChar.WriteValueAsync(ToBuffer(new byte[] { 2 }), GattWriteOption.WriteWithResponse)
                    .AsTask(ct).WaitAsync(TimeSpan.FromSeconds(4), ct);
                Log.Info($"CONTACTS: wake: cleared the receive task the wake read opened ({w})");
                _wakeTaskPending = false;
            }
            else _wakeTaskPending = true;
        }
        catch (TimeoutException)
        {
            if (!IsConnected) return WakeOutcome.LinkDropped;
            Log.Info("CONTACTS: wake: the receiver did not answer on this link");
            return WakeOutcome.NotResponding;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Expected when the receiver restarts its GATT server right away (the request is aborted, and the link
            // may drop). The receiver still opened a receive task for the read.
            Log.Info($"CONTACTS: wake: 9996 read ended with {ex.GetType().Name}");
            _wakeTaskPending = true;
            if (!IsConnected) return WakeOutcome.LinkDropped;
        }
        var outcome = await WaitForOConnectServiceAsync(TimeSpan.FromSeconds(4), ct);
        if (outcome == WakeOutcome.ServiceUp && _wakeTaskPending) await ClearWakeTaskAsync(ct, clearViaDcp);
        return outcome;
    }

    /// <summary>The wake read left a receive task open on the receiver (its 9996 handler starts one), which makes the
    /// next handshake answer "busy". Our own read made this device the receiver's reader, so its 9995 cancel is
    /// accepted directly; the usual clear (another 9996 read first) hangs while that task is open (observed).</summary>
    private bool _wakeTaskPending;
    public bool WakeTaskPending => _wakeTaskPending;

    /// <summary>Receivers differ in which 0x9995 accepts this cancel (seen: the tablet answers the one on 9999, the
    /// phone never answers it), and an unanswered write poisons the link, so only one is tried per link;
    /// <paramref name="viaDcp"/> picks the one on the DCP service, re-read because the receiver restarted its GATT
    /// server after the wake read.</summary>
    public async Task ClearWakeTaskAsync(CancellationToken ct, bool viaDcp = false)
    {
        var cancel = OConnectCancelChar;
        if (viaDcp)
        {
            _dcpCancelChar = null;
            await EnumerateDcpAsync(TimeSpan.FromSeconds(3), fresh: true);
            cancel = _dcpCancelChar ?? cancel;
        }
        if (cancel is null) return;
        var via = ReferenceEquals(cancel, OConnectCancelChar) ? "9999" : "DCP";
        try
        {
            // A receiver that will answer this has always done so within ~1.3 s (observed); one that won't never
            // answers at all, so a short local timeout only trims wasted waiting — it does not affect correctness.
            // Cancel the underlying write on timeout so it does not stay queued and poison the link.
            using var clearCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            clearCts.CancelAfter(TimeSpan.FromSeconds(2));
            var w = await cancel.WriteValueAsync(ToBuffer(new byte[] { 2 }), GattWriteOption.WriteWithResponse)
                .AsTask(clearCts.Token).WaitAsync(TimeSpan.FromSeconds(2), ct);
            _wakeTaskPending = false;
            Log.Info($"CONTACTS: wake: cancelled the receive task the wake read opened via {via} 9995 ({w})");
            SendTimeline.Mark($"wake-task-cleared-{via}");
            await Task.Delay(300, ct);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new LinkNotRespondingException(Address, $"BLE: receiver not responding (cancelling the wake task via {via} 9995 timed out)")
            { DuringWakeClear = true };
        }
    }

    /// <summary>Re-discovers 0x9999 on this link until the receiver's server shows up (it starts a moment after a
    /// wake-up read).</summary>
    public async Task<WakeOutcome> WaitForOConnectServiceAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsConnected) return WakeOutcome.LinkDropped;
            try
            {
                // Cancel the discovery on this iteration's timeout instead of only abandoning the wait, so a
                // slow round cannot leave a pending uncached discovery queued on the link each pass.
                using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pollCts.CancelAfter(TimeSpan.FromSeconds(1.5));
                var svcs = await _device!.GetGattServicesForUuidAsync(OConnectServiceUuid, BluetoothCacheMode.Uncached)
                    .AsTask(pollCts.Token).WaitAsync(TimeSpan.FromSeconds(1.5), ct);
                if (svcs.Status == GattCommunicationStatus.Success && svcs.Services.Count > 0)
                {
                    _services.AddRange(svcs.Services);
                    await EnumerateAsync(SendFlow.OConnectLan, svcs.Services, fast: true);
                    if (HasOConnect)
                    {
                        SendTimeline.Mark("wake-service-up");
                        Log.Info("CONTACTS: wake: 9999 is up on the same link");
                        return WakeOutcome.ServiceUp;
                    }
                }
            }
            catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !ct.IsCancellationRequested)) { }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Info($"CONTACTS: wake: 9999 re-discovery: {ex.GetType().Name}");
            }
            await Task.Delay(250, ct);
        }
        return IsConnected ? WakeOutcome.Failed : WakeOutcome.LinkDropped;
    }

    /// <summary>Waits until Windows no longer holds an LE link to this address. True = it is down (or unknown to
    /// Windows); false = still up after <paramref name="timeout"/>.</summary>
    public static async Task<bool> WaitForLinkDownAsync(ulong address, BluetoothAddressType addressType, TimeSpan timeout, CancellationToken ct)
    {
        BluetoothLEDevice? dev = null;
        try
        {
            using var openCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            openCts.CancelAfter(TimeSpan.FromSeconds(2));
            dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType).AsTask(openCts.Token);
            if (dev is null) return true;
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (dev.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                if (DateTimeOffset.UtcNow >= deadline) return false;
                await Task.Delay(150, ct);
            }
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Info($"BLE: link state check failed: {ex.Message}"); return false; }
        finally { dev?.Dispose(); }
    }
}
