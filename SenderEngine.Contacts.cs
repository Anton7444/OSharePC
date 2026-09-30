using System.Collections.Concurrent;
using OShareSender.Ui;

namespace OShareSender;

public sealed partial class SenderEngine
{
    // Contacts-mode (聯絡人) send support: pre-warmed links, beacon-driven connect and wake/retry.
    private readonly SemaphoreSlim _contactsGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _prewarmedAt = new();
    private readonly Dictionary<string, (GattLink Link, DateTimeOffset At)> _warmLinks = new();

    private void StoreWarmLink(string key, GattLink link)
    {
        lock (_warmLinks)
        {
            if (_warmLinks.TryGetValue(key, out var old)) { try { old.Link.Dispose(); } catch { } }
            _warmLinks[key] = (link, DateTimeOffset.UtcNow);
        }
        // let go of it after ~4 min if nobody used it
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(240));
            lock (_warmLinks)
            {
                if (_warmLinks.TryGetValue(key, out var cur) && ReferenceEquals(cur.Link, link))
                {
                    _warmLinks.Remove(key);
                    try { link.Dispose(); } catch { }
                }
            }
        });
    }

    /// <summary>A verified, still-open link left by the pre-warm (removed from the store when taken).</summary>
    private GattLink? TakeWarmLink(string key)
    {
        lock (_warmLinks)
        {
            if (!_warmLinks.TryGetValue(key, out var w)) return null;
            _warmLinks.Remove(key);
            if (DateTimeOffset.UtcNow - w.At < TimeSpan.FromSeconds(240) && w.Link.IsConnected) return w.Link;
            try { w.Link.Dispose(); } catch { }
            return null;
        }
    }

    private bool HasWarmLink(string key)
    {
        lock (_warmLinks) return _warmLinks.TryGetValue(key, out var w) && w.Link.IsConnected && DateTimeOffset.UtcNow - w.At < TimeSpan.FromSeconds(230);
    }

    /// <summary>Called when files get staged: quietly checks each nearby Contacts device and wakes any whose receive
    /// service is missing or dead, so it is ready by the time the user clicks a device.</summary>
    public void PrewarmContactsDevices()
    {
        if (string.IsNullOrWhiteSpace(SettingsStore.Current.OppoSsoid)) return;
        foreach (var d in Scanner.Devices.Where(x => x.Kind == PhoneKind.Lan && x.LanPdid.StartsWith("FC70", StringComparison.Ordinal)).ToList())
        {
            var dev = d;
            _ = Task.Run(() => PrewarmOneAsync(dev));
        }
    }

    private async Task PrewarmOneAsync(PhoneDevice device)
    {
        try
        {
            if (_sendGate.CurrentCount == 0) return; // a send is running
            if (HasWarmLink(device.LanPdid)) return;
            lock (_prewarmedAt)
                if (_prewarmedAt.TryGetValue(device.LanPdid, out var t) && DateTimeOffset.UtcNow - t < TimeSpan.FromSeconds(230)) return;
            if (!await _contactsGate.WaitAsync(0)) return;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(75));
                var ct = cts.Token;
                var digest = OppoAccount.OppoAccountBleHash.ComputeDsfAccountIdHex(SettingsStore.Current.OppoSsoid!);
                var since = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(6);
                for (var round = 1; round <= 3; round++)
                {
                    var beacon = await Scanner.WaitForContactsBeaconAsync((byte)device.LanDeviceType, digest, since, TimeSpan.FromSeconds(25), ct);
                    if (beacon is null) return;
                    since = beacon.SeenAt;
                    GattLink? link = null;
                    var keepLink = false;
                    var needWake = false;
                    try
                    {
                        link = await GattLink.ConnectAsync(beacon.Address, SendFlow.OConnectLan, 1, null, ct, beacon.AddressType, fast: true);
                        _lastContactsBeacon = beacon; // lets the send path re-wake the receiver if this link dies
                        if (await link.ProbeReceiverAsync(ct))
                        {
                            lock (_prewarmedAt) _prewarmedAt[device.LanPdid] = DateTimeOffset.UtcNow;
                            Scanner.RememberContactName(device.LanDeviceType, digest, link.DeviceName);
                            Log.Info($"PREWARM: {device.Name} receive service is ready (link kept open for a quick send)");
                            StoreWarmLink(device.LanPdid, link);
                            keepLink = true;
                            return;
                        }
                        needWake = true; // listed but not answering
                        Log.Info($"PREWARM: {device.Name} receive service listed but not answering; restarting it");
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        needWake = ex.Message.Contains("not exposed", StringComparison.OrdinalIgnoreCase);
                        if (needWake) Log.Info($"PREWARM: {device.Name} has no receive service running; waking it");
                    }
                    finally { if (!keepLink) { try { link?.Dispose(); } catch { } } }
                    if (!needWake) continue; // plain connect failure: wait for the next beacon
                    await Task.Delay(500, ct);
                    await GattLink.WakeContactsReceiverAsync(beacon.Address, beacon.AddressType, ct);
                    await Task.Delay(1500, ct);
                }
            }
            finally { _contactsGate.Release(); }
        }
        catch (Exception ex) { Log.Info($"PREWARM: {device.Name}: {ex.Message}"); }
    }

    private PhoneScanner.ContactsBeacon? _lastContactsBeacon;

    private async Task<GattLink?> TryContactsBeaconConnectAsync(PhoneDevice device, CancellationToken ct)
    {
        var digest = OppoAccount.OppoAccountBleHash.ComputeDsfAccountIdHex(SettingsStore.Current.OppoSsoid!);
        // A beacon is only connectable while its advertising burst lasts (seconds), so only ever connect to one
        // seen moments ago; an older address just costs a long timeout. Never retry the same address after a
        // plain connection failure: wait for the next fresh beacon instead.
        var since = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(6);
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            TransferStateChanged?.Invoke(_staged!.TaskId, attempt == 1
                ? $"waiting for {device.Name} to be reachable…"
                : $"retrying {device.Name} (attempt {attempt}/5)…");
            var beacon = await Scanner.WaitForContactsBeaconAsync((byte)device.LanDeviceType, digest, since, TimeSpan.FromSeconds(attempt == 1 ? 75 : 90), ct);
            if (beacon is null)
            {
                Log.Info($"CONTACTS: no {device.Name} beacon (type {device.LanDeviceType}, digest {digest}) within the wait window");
                return null;
            }
            since = beacon.SeenAt;
            _lastContactsBeacon = beacon;
            Log.Info($"CONTACTS: connecting to {device.Name} beacon {PhoneDevice.FormatAddress(beacon.Address)} (attempt {attempt}, beacon {(DateTimeOffset.UtcNow - beacon.SeenAt).TotalSeconds:0.0}s old)");
            for (var inner = 1; inner <= 2; inner++)
            {
                try
                {
                    var link = await GattLink.ConnectAsync(beacon.Address, SendFlow.OConnectLan, 1,
                        s => TransferStateChanged?.Invoke(_staged!.TaskId, s), ct, beacon.AddressType, fast: true);
                    Log.Info($"CONTACTS: connected to {device.Name} via beacon (attempt {attempt}.{inner})");
                    Scanner.RememberContactName(device.LanDeviceType, digest, link.DeviceName);
                    return link;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warn($"CONTACTS: attempt {attempt}.{inner} failed ({ex.Message})");
                    // Wake only when the device answered but simply has no receive service running yet.
                    var notExposed = ex.Message.Contains("not exposed", StringComparison.OrdinalIgnoreCase);
                    if (inner == 1 && notExposed)
                    {
                        TransferStateChanged?.Invoke(_staged!.TaskId, $"waking {device.Name} receive service…");
                        await GattLink.WakeContactsReceiverAsync(beacon.Address, beacon.AddressType, ct);
                        await Task.Delay(1000, ct);
                        continue;
                    }
                    break;
                }
            }
        }
        return null;
    }
}
