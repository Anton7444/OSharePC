$ErrorActionPreference = 'Stop'
$path = 'SenderEngine.cs'
$text = Get-Content $path -Raw
$old = @'
            Server.PeerLooksStock = true;   // OConnect peers are stock 互传 receivers
            var armed = false;
            string? expectedPeerIp = null;
            if (device.DeviceId.Length >= 12)
                _lanPeerIps.TryGetValue(device.DeviceId[..12].ToUpperInvariant(), out expectedPeerIp);

            await link.OConnectLanSendAsync(
                Lan,
                Port,
                _crypto,
                Advertiser.DeviceName,
                _staged.FileCount,
                s => TransferStateChanged?.Invoke(_staged.TaskId, s),
                phoneConnected: () =>
                {
                    if (!armed)
                    {
                        Server.ArmTransfer(_staged, Lan.IpString, expectedPeerIp);
                        armed = true;
                    }
                    return Server.WsConnected || _staged.Complete;
                },
                ct: ct);
'@
$new = @'
            Server.PeerLooksStock = true;   // OConnect peers are stock 互传 receivers
            string? expectedPeerIp = null;
            if (device.DeviceId.Length >= 12)
                _lanPeerIps.TryGetValue(device.DeviceId[..12].ToUpperInvariant(), out expectedPeerIp);

            // Arm only after a real GATT session has been established and the stock
            // OConnect path was selected, but before state1/state3 can make the phone
            // open the WebSocket. This removes the race where the phone could reach
            // /websocket before the old lazy phoneConnected callback armed the task.
            Server.ArmTransfer(_staged, Lan.IpString, expectedPeerIp);

            await link.OConnectLanSendAsync(
                Lan,
                Port,
                _crypto,
                Advertiser.DeviceName,
                _staged.FileCount,
                s => TransferStateChanged?.Invoke(_staged.TaskId, s),
                phoneConnected: () => Server.WsConnected || _staged.Complete,
                ct: ct);
'@
if (-not $text.Contains($old)) { throw 'OConnect arm block not found' }
$text = $text.Replace($old, $new)
Set-Content $path $text -NoNewline
if ((Get-Content $path -Raw) -notmatch 'Arm only after a real GATT session') { throw 'patch verification failed' }
