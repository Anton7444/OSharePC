using System.Collections.Concurrent;
using OShareSender.Ui;

namespace OShareSender;

public sealed partial class SenderEngine
{
    // Contacts-mode (聯絡人) send support: pre-warmed links, beacon-driven connect and wake/retry.
    private readonly SemaphoreSlim _contactsGate = new(1, 1);
    private CancellationTokenSource? _prewarmCts;

    /// <summary>Stops a running pre-warm and waits until it has released the gate, so a send never runs
    /// concurrently with one (its wake read makes the receiver restart and drop the send's link).</summary>
    private async Task<bool> TakeContactsGateAsync(CancellationToken ct)
    {
        try { _prewarmCts?.Cancel(); } catch (ObjectDisposedException) { }
        return await _contactsGate.WaitAsync(TimeSpan.FromSeconds(15), ct);
    }
    /// <summary>How long a pre-warmed link (and a successful pre-warm check) is trusted.</summary>
    private static readonly TimeSpan WarmLinkLifetime = TimeSpan.FromSeconds(230);
    private readonly Dictionary<string, DateTimeOffset> _prewarmedAt = new();
    private readonly Dictionary<string, (GattLink Link, DateTimeOffset At)> _warmLinks = new();
    private readonly object _prewarmTasksGate = new();
    private readonly HashSet<Task> _prewarmTasks = new();

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
            await Task.Delay(WarmLinkLifetime);
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
            if (DateTimeOffset.UtcNow - w.At < WarmLinkLifetime && w.Link.IsConnected) return w.Link;
            try { w.Link.Dispose(); } catch { }
            return null;
        }
    }

    private bool HasWarmLink(string key)
    {
        lock (_warmLinks) return _warmLinks.TryGetValue(key, out var w) && w.Link.IsConnected && DateTimeOffset.UtcNow - w.At < WarmLinkLifetime;
    }

    /// <summary>Called when files get staged: quietly checks each nearby Contacts device and wakes any whose receive
    /// service is missing or dead, so it is ready by the time the user clicks a device.</summary>
    public void PrewarmContactsDevices()
    {
        if (string.IsNullOrWhiteSpace(SettingsStore.Current.OppoSsoid)) return;
        foreach (var d in Scanner.Devices.Where(x => x.Kind == PhoneKind.Lan && x.LanPdid.StartsWith("FC70", StringComparison.Ordinal)).ToList())
        {
            var dev = d;
            var task = Task.Run(() => PrewarmOneAsync(dev));
            lock (_prewarmTasksGate) _prewarmTasks.Add(task);
            _ = task.ContinueWith(t =>
            {
                lock (_prewarmTasksGate) _prewarmTasks.Remove(t);
                _ = t.Exception;
            }, TaskScheduler.Default);
        }
    }

    public async Task StopContactsAsync()
    {
        Task[] running;
        lock (_prewarmTasksGate)
        {
            try { _prewarmCts?.Cancel(); } catch (ObjectDisposedException) { }
            running = _prewarmTasks.ToArray();
        }
        if (running.Length > 0)
        {
            try { await Task.WhenAll(running); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn($"CONTACTS: prewarm shutdown failed: {ex.Message}"); }
        }
        DropWarmLinks();
    }

    /// <summary>Closes every pre-warmed link and forgets which devices were checked.</summary>
    private void DropWarmLinks()
    {
        lock (_warmLinks)
        {
            foreach (var (_, warm) in _warmLinks)
            {
                try { warm.Link.Dispose(); } catch { }
            }
            _warmLinks.Clear();
        }
        lock (_prewarmedAt) _prewarmedAt.Clear();
    }

    /// <summary>Called when the OPPO account changes (login, switch, logout): links and checks made for the
    /// previous account must not be reused, and must not keep the devices connected.</summary>
    private void ResetContactsForAccountChange()
    {
        try { _prewarmCts?.Cancel(); } catch (ObjectDisposedException) { }
        DropWarmLinks();
    }

    private static string? CurrentContactsDigest() =>
        string.IsNullOrWhiteSpace(SettingsStore.Current.OppoSsoid)
            ? null
            : OppoAccount.OppoAccountBleHash.ComputeDsfAccountIdHex(SettingsStore.Current.OppoSsoid);

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
                using var namePause = Scanner.PauseContactNameLookups();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(75));
                _prewarmCts = cts;
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
                        if (await link.ProbeReceiverAsync(ct))
                        {
                            // The account may have changed while this ran; a link for the old account is useless.
                            if (!string.Equals(CurrentContactsDigest(), digest, StringComparison.OrdinalIgnoreCase)) return;
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
            finally { _prewarmCts = null; _contactsGate.Release(); }
        }
        catch (OperationCanceledException) { Log.Info($"PREWARM: {device.Name}: stopped"); }
        catch (Exception ex) { Log.Info($"PREWARM: {device.Name}: {ex.Message}"); }
    }

    /// <summary>Restarts this device's receive service through a beacon it sent just now. Beacon addresses
    /// rotate, and any earlier beacon may belong to another same-account device, so never reuse an old one.</summary>
    private async Task<bool> WakeContactsDeviceAsync(PhoneDevice device, CancellationToken ct)
    {
        var digest = OppoAccount.OppoAccountBleHash.ComputeDsfAccountIdHex(SettingsStore.Current.OppoSsoid!);
        var beacon = await Scanner.WaitForContactsBeaconAsync((byte)device.LanDeviceType, digest,
            DateTimeOffset.UtcNow - TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(30), ct);
        if (beacon is null)
        {
            Log.Info($"CONTACTS: no fresh {device.Name} beacon to wake its receive service");
            return false;
        }
        await GattLink.WakeContactsReceiverAsync(beacon.Address, beacon.AddressType, ct);
        return true;
    }

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
