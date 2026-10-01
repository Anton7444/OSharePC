using OShareSender.Ui;

namespace OShareSender;

public sealed partial class SenderEngine
{
    // Contacts-mode (聯絡人) send support: beacon-driven connect, in-link wake, and links kept warm for a quick send.
    private readonly SemaphoreSlim _contactsGate = new(1, 1);
    private CancellationTokenSource? _prewarmCts;

    /// <summary>Takes the Contacts gate, so a send never runs concurrently with a pre-warm (its wake read makes the
    /// receiver restart and drop the send's link). A pre-warm of the SAME device is doing exactly what the send
    /// needs, so the send waits for it and takes over its link: cancelling it throws away a link that may be
    /// moments from ready (seen: cancelled 67 ms before 9999 was up) and leaves a half-closed link on the only
    /// beacon address, which then blocks every reconnect until Windows' 30 s ATT timeout. Its own steps are
    /// bounded (beacon wait 20 s), so the wait is too. Any other device's pre-warm is stopped at once.</summary>
    private async Task<bool> TakeContactsGateAsync(CancellationToken ct, string? deviceKey = null)
    {
        if (deviceKey is not null && string.Equals(Volatile.Read(ref _prewarmDeviceKey), deviceKey, StringComparison.Ordinal))
        {
            Log.Info("CONTACTS: a pre-warm of this device is running; waiting for its link");
            if (await _contactsGate.WaitAsync(TimeSpan.FromSeconds(25), ct)) return true;
            Log.Info("CONTACTS: the pre-warm of this device is taking too long; stopping it");
        }
        try { _prewarmCts?.Cancel(); } catch (ObjectDisposedException) { }
        return await _contactsGate.WaitAsync(TimeSpan.FromSeconds(15), ct);
    }

    /// <summary>The device a running pre-warm is working on (null when none runs).</summary>
    private string? _prewarmDeviceKey;

    /// <summary>How long a pre-warmed link is trusted before it is replaced by a fresh one.</summary>
    private static readonly TimeSpan WarmLinkLifetime = TimeSpan.FromMinutes(5);
    /// <summary>How long after files were staged (or a send ended, or the GUI asked) the Contacts devices are kept
    /// warm. Short on purpose: each kept link costs the phone a little battery.</summary>
    private static readonly TimeSpan KeepWarmWindow = TimeSpan.FromMinutes(5);
    // A warm link is only parked after the full OConnect service has been
    // discovered.  Keeping a bare connected BLE link here caused sends to
    // rediscover 9999 (or hit a stale receiver) after the fast path had already
    // been selected.
    private readonly Dictionary<string, (GattLink Link, DateTimeOffset At)> _warmLinks = new();

    private void StoreWarmLink(string key, GattLink link)
    {
        if (!link.IsConnected || !link.HasOConnect)
        {
            Log.Warn($"CONTACTS: refusing to park an incomplete link for {key}");
            link.DisposeInBackground();
            return;
        }
        // A parked link does not need the fast interval; it is requested again when a send takes the link.
        link.RelaxConnectionParameters();
        lock (_warmLinks)
        {
            if (_warmLinks.TryGetValue(key, out var old) && !ReferenceEquals(old.Link, link)) old.Link.DisposeInBackground();
            _warmLinks[key] = (link, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>A still-open link left by the pre-warm (removed from the store when taken).</summary>
    private GattLink? TakeWarmLink(string key)
    {
        lock (_warmLinks)
        {
            if (!_warmLinks.TryGetValue(key, out var w))
            {
                SendTimeline.Mark("warm-link-miss");
                return null;
            }
            _warmLinks.Remove(key);
            if (DateTimeOffset.UtcNow - w.At < WarmLinkLifetime && w.Link.IsConnected)
            {
                w.Link.UseFastConnectionParameters();
                SendTimeline.Mark($"warm-link-age-{(DateTimeOffset.UtcNow - w.At).TotalSeconds:0}");
                return w.Link;
            }
            Log.Info($"CONTACTS: discarded expired/disconnected warm link for {key}");
            SendTimeline.Mark("warm-link-expired");
            w.Link.DisposeInBackground();
            return null;
        }
    }

    private bool HasWarmLink(string key)
    {
        lock (_warmLinks) return _warmLinks.TryGetValue(key, out var w) && w.Link.IsConnected && DateTimeOffset.UtcNow - w.At < WarmLinkLifetime;
    }

    /// <summary>Releases warm links whose connection dropped or that are too old, so the keeper replaces them.</summary>
    private void PruneWarmLinks()
    {
        lock (_warmLinks)
        {
            foreach (var key in _warmLinks.Keys.ToArray())
            {
                var w = _warmLinks[key];
                if (w.Link.IsConnected && DateTimeOffset.UtcNow - w.At < WarmLinkLifetime) continue;
                Log.Info($"CONTACTS: warm link {key} {(w.Link.IsConnected ? "expired" : "dropped")}; it will be replaced");
                _warmLinks.Remove(key);
                w.Link.DisposeInBackground();
            }
        }
    }

    // Beacon addresses whose link stopped answering (an unanswered request blocks it until the 30 s ATT timeout)
    // or is stuck established: connecting to them again fails the same way, so wait for another address.
    private readonly Dictionary<ulong, DateTimeOffset> _avoidAddresses = new();

    private void AvoidAddress(ulong address, TimeSpan duration)
    {
        lock (_avoidAddresses) _avoidAddresses[address] = DateTimeOffset.UtcNow + duration;
        Log.Info($"CONTACTS: avoiding {PhoneDevice.FormatAddress(address)} for {duration.TotalSeconds:0}s");
    }

    private bool IsAvoided(ulong address)
    {
        lock (_avoidAddresses)
        {
            if (!_avoidAddresses.TryGetValue(address, out var until)) return false;
            if (until > DateTimeOffset.UtcNow) return true;
            _avoidAddresses.Remove(address);
            return false;
        }
    }

    // Keep-warm loop: while files are staged (and for a while after a send) every nearby Contacts device holds an
    // open, ready link, so a send skips the whole BLE connect (the receiver also goes cold after each transfer,
    // which the keeper then undoes in the background).
    private readonly object _keeperGate = new();
    private CancellationTokenSource? _keeperCts;
    private Task? _keeperTask;
    private DateTimeOffset _keepWarmUntil;
    private DateTimeOffset _lastContactsSendEnded;
    /// <summary>Minimum gap between the end of one Contacts send and the start of the next.</summary>
    private static readonly TimeSpan ContactsSendSettle = TimeSpan.FromSeconds(1);
    private readonly Dictionary<string, (DateTimeOffset NextTry, int Failures)> _prewarmBackoff = new();

    /// <summary>Called when files get staged and after a Contacts send: keeps each nearby Contacts device connected
    /// and its receive service running for the next few minutes.</summary>
    public void PrewarmContactsDevices()
    {
        if (string.IsNullOrWhiteSpace(SettingsStore.Current.OppoSsoid)) return;
        lock (_keeperGate)
        {
            _keepWarmUntil = DateTimeOffset.UtcNow + KeepWarmWindow;
            if (_keeperTask is { IsCompleted: false }) return;
            _keeperCts?.Dispose();
            _keeperCts = new CancellationTokenSource();
            var token = _keeperCts.Token;
            _keeperTask = Task.Run(() => KeepContactsWarmAsync(token));
        }
    }

    private async Task KeepContactsWarmAsync(CancellationToken ct)
    {
        Log.Info("CONTACTS: keeping Contacts devices warm");
        try
        {
            while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < _keepWarmUntil &&
                   !string.IsNullOrWhiteSpace(SettingsStore.Current.OppoSsoid))
            {
                PruneWarmLinks();
                // Leave a finishing transfer alone for a moment before reconnecting to that receiver.
                if (_sendGate.CurrentCount > 0 && DateTimeOffset.UtcNow - _lastContactsSendEnded > ContactsSendSettle)
                {
                    foreach (var d in Scanner.Devices.Where(x => x.Kind == PhoneKind.Lan && x.LanPdid.StartsWith("FC70", StringComparison.Ordinal)).ToList())
                    {
                        if (ct.IsCancellationRequested || _sendGate.CurrentCount == 0) break;
                        if (Scanner.ShouldAvoidContactsWarm(d.LanDeviceType))
                        {
                            // This device is (or may be) in Everyone mode: a Contacts link to it would block the
                            // connection an Everyone-mode send needs. Hold none.
                            TakeWarmLink(d.LanPdid)?.DisposeInBackground();
                            continue;
                        }
                        if (HasWarmLink(d.LanPdid)) continue;
                        lock (_prewarmBackoff)
                            if (_prewarmBackoff.TryGetValue(d.LanPdid, out var b) && b.NextTry > DateTimeOffset.UtcNow) continue;
                        await PrewarmOneAsync(d, ct);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"CONTACTS: keep-warm loop failed: {ex.Message}"); }
        finally
        {
            DropWarmLinks();
            Log.Info("CONTACTS: stopped keeping Contacts devices warm");
        }
    }

    public async Task StopContactsAsync()
    {
        Task? keeper;
        lock (_keeperGate)
        {
            try { _keeperCts?.Cancel(); } catch (ObjectDisposedException) { }
            keeper = _keeperTask;
        }
        try { _prewarmCts?.Cancel(); } catch (ObjectDisposedException) { }
        if (keeper is not null)
        {
            try { await keeper; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn($"CONTACTS: prewarm shutdown failed: {ex.Message}"); }
        }
        DropWarmLinks();
    }

    /// <summary>Before an Everyone-mode send: closes every kept Contacts link and waits (briefly) until they are
    /// really down. The target phone may be one of those devices, reachable under another address, and an open
    /// Contacts link to it makes the Everyone-mode connection fail.</summary>
    private async Task ReleaseWarmLinksForSendAsync(CancellationToken ct)
    {
        List<GattLink> links;
        lock (_warmLinks)
        {
            links = _warmLinks.Values.Select(w => w.Link).ToList();
            _warmLinks.Clear();
        }
        if (links.Count == 0) return;
        Log.Info($"SEND: releasing {links.Count} kept Contacts link(s) before an Everyone-mode send");
        foreach (var l in links) l.DisposeInBackground();
        await Task.WhenAll(links.Select(l => GattLink.WaitForLinkDownAsync(l.Address, l.AddressType, TimeSpan.FromSeconds(2), ct)));
        SendTimeline.Mark("warm-links-released");
    }

    /// <summary>Closes every pre-warmed link and forgets the retry back-off.</summary>
    private void DropWarmLinks()
    {
        lock (_warmLinks)
        {
            foreach (var (_, warm) in _warmLinks) warm.Link.DisposeInBackground();
            _warmLinks.Clear();
        }
        lock (_prewarmBackoff) _prewarmBackoff.Clear();
    }

    /// <summary>Called when the OPPO account changes (login, switch, logout): links made for the previous account
    /// must not be reused, and must not keep the devices connected.</summary>
    private void ResetContactsForAccountChange()
    {
        try { _prewarmCts?.Cancel(); } catch (ObjectDisposedException) { }
        DropWarmLinks();
    }

    private static string? CurrentContactsDigest() =>
        string.IsNullOrWhiteSpace(SettingsStore.Current.OppoSsoid)
            ? null
            : OppoAccount.OppoAccountBleHash.ComputeDsfAccountIdHex(SettingsStore.Current.OppoSsoid);

    private async Task PrewarmOneAsync(PhoneDevice device, CancellationToken keeperCt)
    {
        if (_sendGate.CurrentCount == 0 || HasWarmLink(device.LanPdid)) return;
        if (!await _contactsGate.WaitAsync(0, keeperCt)) return;
        var ok = false;
        try
        {
            using var namePause = Scanner.PauseContactNameLookups();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(keeperCt);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            _prewarmCts = cts;
            Volatile.Write(ref _prewarmDeviceKey, device.LanPdid);
            using var timeline = SendTimeline.Start($"prewarm -> {device.Name}");
            var digest = CurrentContactsDigest();
            var link = await ConnectContactsAsync(device, null, TimeSpan.FromSeconds(20), forceWake: false, cts.Token);
            if (link is null) return;
            // The account may have changed while this ran; a link for the old account is useless.
            if (digest is null || !string.Equals(CurrentContactsDigest(), digest, StringComparison.OrdinalIgnoreCase))
            {
                link.DisposeInBackground();
                return;
            }
            Scanner.RememberContactName(device.LanDeviceType, digest, link.DeviceName);
            StoreWarmLink(device.LanPdid, link);
            ok = true;
            timeline.Succeeded();
            Log.Info($"PREWARM: {device.Name} is ready (link kept open for a quick send)");
        }
        catch (OperationCanceledException)
        {
            // Stopped for a send or shutdown, not a failure: no back-off.
            ok = true;
            Log.Info($"PREWARM: {device.Name}: stopped");
        }
        catch (Exception ex) { Log.Info($"PREWARM: {device.Name}: {ex.Message}"); }
        finally
        {
            _prewarmCts = null;
            Volatile.Write(ref _prewarmDeviceKey, null);
            _contactsGate.Release();
            lock (_prewarmBackoff)
            {
                if (ok) _prewarmBackoff.Remove(device.LanPdid);
                else
                {
                    // 10 s, 20 s, 40 s, then every 60 s: a device that is out of range must not keep the radio busy.
                    var failures = (_prewarmBackoff.TryGetValue(device.LanPdid, out var b) ? b.Failures : 0) + 1;
                    var wait = TimeSpan.FromSeconds(Math.Min(60, 10 * Math.Pow(2, failures - 1)));
                    _prewarmBackoff[device.LanPdid] = (DateTimeOffset.UtcNow + wait, failures);
                }
            }
        }
    }

    /// <summary>Opens a link to a Contacts receiver that is ready for the OConnect handshake, over its newest beacon
    /// address (addresses rotate, and an older one may belong to another same-account device):
    /// 9999 present → done; otherwise start the receiver's server over this same link and wait for 9999
    /// (reconnecting at once to the same address if the receiver drops the link while restarting), then cancel the
    /// receive task the wake read opened so the handshake does not find it busy. A stuck or unanswering address is
    /// avoided instead of being retried. <paramref name="forceWake"/>: 9999 is listed but did not answer, restart it.</summary>
    private async Task<GattLink?> ConnectContactsAsync(PhoneDevice device, Action<string>? status, TimeSpan firstBeaconWait, bool forceWake, CancellationToken ct)
    {
        var digest = OppoAccount.OppoAccountBleHash.ComputeDsfAccountIdHex(SettingsStore.Current.OppoSsoid!);
        var type = (byte)device.LanDeviceType;
        Log.Info($"CONTACTS: {device.Name} beacons: {Scanner.DescribeContactsBeacons(type, digest)}");
        // The receiver keeps one beacon address for minutes while Windows reports its packets only every few
        // seconds, so the newest address heard recently is taken at once (requiring one heard in the last 6 s cost
        // up to several seconds of waiting). Addresses that just failed are skipped through IsAvoided instead.
        var since = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(45);
        var woke = false;
        var wakeTaskPending = false;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            status?.Invoke(attempt == 1 ? $"waiting for {device.Name} to be reachable…" : $"retrying {device.Name} (attempt {attempt}/4)…");
            var wait = attempt == 1 ? firstBeaconWait : TimeSpan.FromSeconds(30);
            // Prefer an address that is not avoided, but a receiver may keep one address for many minutes: after a
            // few seconds take the avoided one again (its bad link has usually dropped by then).
            var preferWait = TimeSpan.FromSeconds(4) < wait ? TimeSpan.FromSeconds(4) : wait;
            var beacon = await Scanner.WaitForContactsBeaconAsync(type, digest, since, preferWait, ct, IsAvoided)
                         ?? await Scanner.WaitForContactsBeaconAsync(type, digest, since, wait - preferWait, ct);
            if (beacon is null)
            {
                Log.Info($"CONTACTS: no usable {device.Name} beacon (type {type}, digest {digest}) within the wait window");
                return null;
            }
            SendTimeline.Mark("beacon");
            if (IsAvoided(beacon.Address))
            {
                Log.Info($"CONTACTS: no other address; retrying {PhoneDevice.FormatAddress(beacon.Address)} once its old link is down");
                await GattLink.WaitForLinkDownAsync(beacon.Address, beacon.AddressType, TimeSpan.FromSeconds(3), ct);
            }
            // `since` stays put: Windows reports this beacon only every few seconds (up to ~20 s seen), so waiting
            // for a newer packet before a retry costs more than it saves; the newest address is picked anyway.
            Log.Info($"CONTACTS: connecting to {device.Name} beacon {PhoneDevice.FormatAddress(beacon.Address)} (attempt {attempt}, beacon {(DateTimeOffset.UtcNow - beacon.SeenAt).TotalSeconds:0.0}s old)");
            GattLink? link = null;
            try
            {
                // Up to three links to this address: the receiver may drop the link while it restarts its server.
                for (var hop = 1; hop <= 3; hop++)
                {
                    link = await GattLink.ConnectAsync(beacon.Address, SendFlow.OConnectLan, 1, status, ct, beacon.AddressType,
                        fast: true, allowMissingTarget: true);
                    GattLink.WakeOutcome outcome;
                    if (link.HasOConnect && !(forceWake && !woke))
                    {
                        // A wake on the previous (dropped) link left its receive task open: cancel it from here.
                        if (wakeTaskPending) await link.ClearWakeTaskAsync(ct);
                        Log.Info($"CONTACTS: connected to {device.Name} (attempt {attempt}.{hop})");
                        return TakeLink(ref link);
                    }
                    if (!woke)
                    {
                        status?.Invoke($"waking {device.Name} receive service…");
                        Log.Info($"CONTACTS: {(forceWake ? "restarting" : "starting")} the {device.Name} receive service over this link");
                        woke = true;
                        outcome = await link.WakeOnLinkAsync(ct);
                    }
                    else outcome = await link.WaitForOConnectServiceAsync(TimeSpan.FromSeconds(3), ct);
                    Log.Info($"CONTACTS: wake outcome {outcome}");
                    if (outcome == GattLink.WakeOutcome.ServiceUp)
                    {
                        if (wakeTaskPending) await link.ClearWakeTaskAsync(ct);
                        Log.Info($"CONTACTS: connected to {device.Name} after waking it (attempt {attempt}.{hop})");
                        return TakeLink(ref link);
                    }
                    wakeTaskPending |= link.WakeTaskPending;
                    link.DisposeInBackground();
                    link = null;
                    if (outcome != GattLink.WakeOutcome.LinkDropped)
                    {
                        if (outcome == GattLink.WakeOutcome.NotResponding) AvoidAddress(beacon.Address, TimeSpan.FromSeconds(35));
                        break;
                    }
                    // The receiver restarted its GATT server and dropped us; the beacon address stays valid.
                    await GattLink.WaitForLinkDownAsync(beacon.Address, beacon.AddressType, TimeSpan.FromSeconds(1.5), ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (StaleLinkException ex)
            {
                Log.Warn($"CONTACTS: attempt {attempt} hit a stale link ({ex.Message})");
                // That link only drops once Windows' 30 s ATT timeout for the unanswered requests expires, and every
                // retry over it queues more of them and pushes the drop further out (seen: 3 retries, 30 s). So leave
                // it alone and reconnect the moment it is down.
                status?.Invoke($"waiting for the old link to {device.Name} to close…");
                var down = await GattLink.WaitForLinkDownAsync(beacon.Address, beacon.AddressType, TimeSpan.FromSeconds(32), ct);
                SendTimeline.Mark("stale-link-dropped");
                if (!down) AvoidAddress(beacon.Address, TimeSpan.FromSeconds(35));
            }
            catch (LinkNotRespondingException ex)
            {
                Log.Warn($"CONTACTS: attempt {attempt}: {ex.Message}");
                AvoidAddress(ex.Address, TimeSpan.FromSeconds(35));
            }
            catch (Exception ex)
            {
                Log.Warn($"CONTACTS: attempt {attempt} failed ({ex.Message})");
            }
            finally
            {
                link?.DisposeInBackground();
            }
        }
        return null;
    }

    private static GattLink TakeLink(ref GattLink? link)
    {
        var taken = link!;
        link = null;
        return taken;
    }
}
