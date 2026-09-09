$ErrorActionPreference = 'Stop'
$path = 'GattLink.cs'
$text = Get-Content $path -Raw

$old = @'
        var notifyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNotify(GattCharacteristic c, GattValueChangedEventArgs e)
        {
            try
            {
                var text = ReadString(e.CharacteristicValue);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("account_id", out _))
                {
                    Log.Info($"BLE: 9898 notify <- {text}");
                    notifyTcs.TrySetResult(text);
                }
                else
                {
                    Log.Info($"BLE: 9898 notify (pre-challenge, ignored) <- {text}");
                }
            }
            catch (Exception ex) { Log.Warn($"BLE: 9898 notify parse failed: {ex.Message}"); }
        }
        OConnectNotifyChar.ValueChanged += OnNotify;

        // enable notifications via the CCCD descriptor. MUST be Uncached: the cached
        // path returns an empty descriptor list on a fresh connection (false negative
        // that silently killed the whole notify path in earlier builds).
        var cccd = (await OConnectNotifyChar.GetDescriptorsAsync(BluetoothCacheMode.Uncached)).Descriptors
            .FirstOrDefault(d => d.Uuid == CccdUuid);
        Log.Info($"BLE: 0x9898 descriptors: [{string.Join(", ",
            (await OConnectNotifyChar.GetDescriptorsAsync()).Descriptors.Select(d => ShortUuid(d.Uuid)))}]");
        if (cccd is not null)
        {
            var w = await cccd.WriteValueAsync(ToBuffer(BitConverter.GetBytes((ushort)1)));
            Log.Info($"BLE: CCCD(9898) subscribed ({w})");
        }
        else
            Log.Warn("BLE: 0x9898 has no CCCD descriptor - notifications may not arrive");
'@

$new = @'
        var accountNotifyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wlanNotifyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNotify(GattCharacteristic c, GattValueChangedEventArgs e)
        {
            try
            {
                var text = ReadString(e.CharacteristicValue);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                Log.Info($"BLE: 9898 notify <- {text}");

                if (root.TryGetProperty("account_id", out _))
                    accountNotifyTcs.TrySetResult(text);

                if (root.TryGetProperty("wlan", out _) || root.TryGetProperty("ip", out _))
                    wlanNotifyTcs.TrySetResult(text);
            }
            catch (Exception ex) { Log.Warn($"BLE: 9898 notify parse failed: {ex.Message}"); }
        }
        OConnectNotifyChar.ValueChanged += OnNotify;

        // Prefer the characteristic-level WinRT CCCD API. Some OnePlus builds hide
        // the descriptor from enumeration even though the characteristic can notify.
        var notificationsSubscribed = false;
        try
        {
            var sub = await OConnectNotifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            notificationsSubscribed = sub == GattCommunicationStatus.Success;
            Log.Info($"BLE: 9898 notification subscription via WinRT = {sub}");
        }
        catch (Exception ex)
        {
            Log.Warn($"BLE: WinRT 9898 notification subscription failed: {ex.Message}");
        }

        if (!notificationsSubscribed)
        {
            var cccd = (await OConnectNotifyChar.GetDescriptorsAsync(BluetoothCacheMode.Uncached)).Descriptors
                .FirstOrDefault(d => d.Uuid == CccdUuid);
            Log.Info($"BLE: 0x9898 descriptors: [{string.Join(", ",
                (await OConnectNotifyChar.GetDescriptorsAsync()).Descriptors.Select(d => ShortUuid(d.Uuid)))}]");
            if (cccd is not null)
            {
                var w = await cccd.WriteValueAsync(ToBuffer(BitConverter.GetBytes((ushort)1)));
                notificationsSubscribed = w == GattCommunicationStatus.Success;
                Log.Info($"BLE: CCCD(9898) compatibility subscription = {w}");
            }
            else
            {
                Log.Warn("BLE: 0x9898 exposes no enumerable CCCD; official notify-driven flow unavailable on this Windows stack");
            }
        }
'@

if (-not $text.Contains($old)) { throw 'notification block not found' }
$text = $text.Replace($old, $new)
$text = $text.Replace('var challengeJson = await notifyTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);', 'var challengeJson = await accountNotifyTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);')

$old2 = @'
            else
            {
                // pv<5: the pad now shows the receive CARD. The user taps accept, which
                // moves the pad to N=3. state3 writes sent before that are silently
                // ignored by the pad (dispatch only handles N in {1,6,3,4}), so the
                // retry loop below simply waits for the accept.
                status?.Invoke("请在平板上点『接受』以确认接收…");
                Log.Info("BLE: state1 (card flow) sent - waiting for the user to accept on the pad");
            }

            // 6. state-3: offer our wlan (ip/port) - the phone connects wss://ip:port.
            //    60 x 3s = 3 minutes: enough time to accept the card on the pad.
            var encIp = OShareCrypto.CbcEncryptToB64(cbcKey, cbcIv, lan.IpString);
            var encPort = OShareCrypto.CbcEncryptToB64(cbcKey, cbcIv, serverPort.ToString());
            status?.Invoke("offering LAN address, waiting for phone connection...");
            for (int attempt = 1; attempt <= 60; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (phoneConnected?.Invoke() == true)
                {
                    Log.Info("BLE: phone connected to our server - handshake complete");
                    return;
                }
                var state3 = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["wlan"] = "wlan",
                    ["wlan_accept"] = true,
                    ["ip"] = encIp,
                    ["port"] = encPort,
                });
                Log.Info($"BLE: 9896 state3 <- {state3}");
                await WriteOConnectSingle(state3);
                await Task.Delay(3000, ct);
            }
            throw new InvalidOperationException(
                "BLE: phone never connected within 3 minutes of the wlan offer - " +
                "accept the receive card on the pad (互传), and keep both devices on the same Wi-Fi.");
'@

$new2 = @'
            else
            {
                // pv<5: OnePlus Share advances only after the user accepts the card,
                // then emits its WLAN transition on 9898.
                status?.Invoke("请在手机上点『接受』以确认接收…");
                Log.Info("BLE: state1 sent - waiting for official 9898 accept/WLAN transition");
            }

            var encIp = OShareCrypto.CbcEncryptToB64(cbcKey, cbcIv, lan.IpString);
            var encPort = OShareCrypto.CbcEncryptToB64(cbcKey, cbcIv, serverPort.ToString());
            var state3 = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["wlan"] = "wlan",
                ["wlan_accept"] = true,
                ["ip"] = encIp,
                ["port"] = encPort,
            });

            if (notificationsSubscribed)
            {
                status?.Invoke("waiting for phone acceptance/WLAN readiness...");
                var wlanNotify = await wlanNotifyTcs.Task.WaitAsync(TimeSpan.FromMinutes(3), ct);
                Log.Info($"BLE: official WLAN transition observed: {wlanNotify}");
                Log.Info($"BLE: 9896 state3 <- {state3}");
                await WriteOConnectSingle(state3);
            }
            else
            {
                // Compatibility only: Windows stacks that truly cannot subscribe to
                // 9898 still need a guarded probe, but it is no longer the main path.
                Log.Warn("BLE: using compatibility state3 probing because 9898 notifications are unavailable");
                status?.Invoke("waiting for phone acceptance (compatibility mode)...");
                await Task.Delay(2500, ct);
                var delivered = false;
                for (var attempt = 1; attempt <= 36; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (phoneConnected?.Invoke() == true) { delivered = true; break; }
                    Log.Info($"BLE: 9896 state3 compatibility probe {attempt}/36 <- {state3}");
                    await WriteOConnectSingle(state3);
                    await Task.Delay(5000, ct);
                }
                if (!delivered && phoneConnected?.Invoke() != true)
                    throw new InvalidOperationException("BLE: phone never entered the WLAN phase within 3 minutes; reopen 互传 and retry.");
            }

            status?.Invoke("waiting for phone LAN/WebSocket connection...");
            var connectionDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < connectionDeadline)
            {
                ct.ThrowIfCancellationRequested();
                if (phoneConnected?.Invoke() == true)
                {
                    Log.Info("BLE: phone connected to our server - handshake complete");
                    return;
                }
                await Task.Delay(200, ct);
            }
            throw new InvalidOperationException("BLE: phone accepted WLAN but did not connect to the transfer server within 20 seconds.");
'@

if (-not $text.Contains($old2)) { throw 'state3 block not found' }
$text = $text.Replace($old2, $new2)

$old3 = @'
            try
            {
                var cccdOff = (await OConnectNotifyChar.GetDescriptorsAsync(BluetoothCacheMode.Uncached)).Descriptors
                    .FirstOrDefault(d => d.Uuid == CccdUuid);
                if (cccdOff is not null)
                    await cccdOff.WriteValueAsync(ToBuffer(BitConverter.GetBytes((ushort)0)));
            }
            catch (Exception ex)
            {
                Log.Warn($"BLE: CCCD(9898) unsubscribe failed: {ex.Message}");
            }
'@
$new3 = @'
            try
            {
                await OConnectNotifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch (Exception ex)
            {
                Log.Warn($"BLE: 9898 unsubscribe failed: {ex.Message}");
            }
'@
if (-not $text.Contains($old3)) { throw 'unsubscribe block not found' }
$text = $text.Replace($old3, $new3)

Set-Content $path $text -NoNewline

$verify = Get-Content $path -Raw
if ($verify -notmatch 'official WLAN transition observed') { throw 'missing event-driven WLAN transition' }
if ($verify -notmatch 'WriteClientCharacteristicConfigurationDescriptorAsync') { throw 'missing WinRT CCCD subscription' }
if ($verify -match 'for \(int attempt = 1; attempt <= 60; attempt\+\+\)') { throw 'old blind state3 loop still present' }
