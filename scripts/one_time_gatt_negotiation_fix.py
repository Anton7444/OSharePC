from pathlib import Path


def replace_once(path, old, new, label):
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if old not in text:
        raise SystemExit(f"missing anchor: {label}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


# PC -> Android: establish a real GATT session first, then query only the
# protocol service. The OnePlus Pad accepts the physical connection but the
# old full uncached database walk immediately ends in Unreachable.
gl = Path("GattLink.cs")
text = gl.read_text(encoding="utf-8")
old = '''                // MaintainConnection forces the stack to actually establish the LE link
                try
                {
                    session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
                    if (session is not null) session.MaintainConnection = true;
                }
                catch (Exception ex) { Log.Warn($"BLE: GattSession unavailable ({ex.Message})"); }

                var svcResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
                if (svcResult.Status != GattCommunicationStatus.Success)
                    throw new InvalidOperationException($"service discovery failed ({svcResult.Status})");

                var link = new GattLink();
                link._device = device;
                link._session = session;
                link._services.AddRange(svcResult.Services);
                device = null;       // ownership moved to the link
                session = null;
                Log.Info($"BLE: GATT connected to {PhoneDevice.FormatAddress(bluetoothAddress)} '{link._device.Name}' (addressType={addressType}, flow={flow}, attempt {attempt})");

                try
                {
                    await link.EnumerateAsync(flow, svcResult.Services);
                    return link;
                }
'''
new = '''                // Match the stock Android client lifecycle: establish a real LE/GATT
                // session first, then perform protocol service discovery. Do not race
                // a full uncached database walk against the physical link coming up.
                try
                {
                    session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
                    if (session is not null)
                    {
                        Log.Info($"BLE: GATT session created status={session.SessionStatus} pdu={session.MaxPduSize} canMaintain={session.CanMaintainConnection}");
                        if (session.CanMaintainConnection)
                        {
                            session.MaintainConnection = true;
                            var activeDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
                            while (session.SessionStatus != GattSessionStatus.Active && DateTimeOffset.UtcNow < activeDeadline)
                            {
                                ct.ThrowIfCancellationRequested();
                                await Task.Delay(25, ct);
                            }
                        }
                        Log.Info($"BLE: GATT session ready status={session.SessionStatus} pdu={session.MaxPduSize}");
                    }
                }
                catch (Exception ex) { Log.Warn($"BLE: GattSession unavailable ({ex.Message})"); }

                var discoveredServices = new List<GattDeviceService>();
                GattCommunicationStatus discoveryStatus;
                string discoveryLabel;

                if (flow == SendFlow.CatShareHotspot)
                {
                    Log.Info("BLE: discovering target alliance service 9955 only");
                    var result = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
                    discoveryStatus = result.Status;
                    discoveryLabel = "9955";
                    if (result.Status == GattCommunicationStatus.Success)
                        discoveredServices.AddRange(result.Services);
                }
                else
                {
                    Log.Info("BLE: discovering target OConnect service 9999 only");
                    var oconnect = await device.GetGattServicesForUuidAsync(OConnectServiceUuid, BluetoothCacheMode.Uncached);
                    discoveryStatus = oconnect.Status;
                    discoveryLabel = "9999";
                    if (oconnect.Status == GattCommunicationStatus.Success)
                        discoveredServices.AddRange(oconnect.Services);

                    // Auto can also target CatShare. Probe 9955 only if 9999 was
                    // queried successfully and is genuinely absent. Never start a
                    // second discovery after an already-failed physical link.
                    if (flow == SendFlow.Auto &&
                        discoveryStatus == GattCommunicationStatus.Success &&
                        discoveredServices.Count == 0)
                    {
                        Log.Info("BLE: service 9999 absent; probing 9955 fallback");
                        var alliance = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
                        discoveryStatus = alliance.Status;
                        discoveryLabel = "9955";
                        if (alliance.Status == GattCommunicationStatus.Success)
                            discoveredServices.AddRange(alliance.Services);
                    }
                }

                if (discoveryStatus != GattCommunicationStatus.Success)
                    throw new InvalidOperationException($"target service {discoveryLabel} discovery failed ({discoveryStatus})");
                if (discoveredServices.Count == 0)
                    throw new InvalidOperationException($"target service {discoveryLabel} is not exposed by the device");

                var link = new GattLink();
                link._device = device;
                link._session = session;
                link._services.AddRange(discoveredServices);
                device = null;       // ownership moved to the link
                session = null;
                Log.Info($"BLE: GATT connected to {PhoneDevice.FormatAddress(bluetoothAddress)} '{link._device.Name}' (addressType={addressType}, flow={flow}, target={discoveryLabel}, attempt {attempt})");

                try
                {
                    await link.EnumerateAsync(flow, discoveredServices);
                    return link;
                }
'''
if old not in text:
    raise SystemExit("missing anchor: GattLink discovery lifecycle")
gl.write_text(text.replace(old, new, 1), encoding="utf-8")


# Android -> PC: create the final local GATT database before advertising the
# connectable 8881 beacon, and make the whole OShare control plane explicitly
# Plain/pairless.
rg = Path("ReceiveGattServer.cs")
text = rg.read_text(encoding="utf-8")
start_marker = "        // 1. Discovery beacon: 00008881 (standard base)"
end_marker = "        State($\"Receive server active (beacon 8881 + service 9999), BT Name='{BluetoothName(DeviceName)}'\");"
start = text.index(start_marker)
end = text.index(end_marker, start) + len(end_marker)
replacement = '''        // Build the complete protocol GATT database BEFORE opening the connectable
        // discovery beacon. Android caches GATT aggressively; exposing 8881 while 9999
        // is still being added can leave a peer with a transient/incomplete database.
        // All OShare control characteristics are deliberately Plain: this protocol is
        // pairless and must never require a Bluetooth bond.
        var svc = await GattServiceProvider.CreateAsync(
            new Guid("00009999-0000-1000-8000-00805f9b34fb"));
        if (svc.Error != BluetoothError.Success)
            throw new InvalidOperationException($"RX: 9999 provider failed: {svc.Error}");

        var hs = await svc.ServiceProvider.Service.CreateCharacteristicAsync(
            new Guid("00009898-0000-1000-8000-00805f9b34fb"),
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                ReadProtectionLevel = GattProtectionLevel.Plain,
                WriteProtectionLevel = GattProtectionLevel.Plain,
            });
        if (hs.Error != BluetoothError.Success)
            throw new InvalidOperationException($"RX: 0x9898 create failed: {hs.Error}");
        _handshake = hs.Characteristic;
        _handshake.ReadRequested += OnHandshakeRead;

        var cmd = await svc.ServiceProvider.Service.CreateCharacteristicAsync(
            new Guid("00009896-0000-1000-8000-00805f9b34fb"),
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse,
                ReadProtectionLevel = GattProtectionLevel.Plain,
                WriteProtectionLevel = GattProtectionLevel.Plain,
            });
        if (cmd.Error != BluetoothError.Success)
            throw new InvalidOperationException($"RX: 0x9896 create failed: {cmd.Error}");
        _cmd = cmd.Characteristic;
        _cmd.WriteRequested += OnCommandWrite;

        var notify = await svc.ServiceProvider.Service.CreateCharacteristicAsync(
            new Guid("00009895-0000-1000-8000-00805f9b34fb"),
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Notify,
                ReadProtectionLevel = GattProtectionLevel.Plain,
                WriteProtectionLevel = GattProtectionLevel.Plain,
            });
        if (notify.Error != BluetoothError.Success)
            throw new InvalidOperationException($"RX: 0x9895 create failed: {notify.Error}");
        _notify = notify.Characteristic;
        _notify.SubscribedClientsChanged += (_, _) =>
            State($"0x9895 subscribers = {_notify?.SubscribedClients.Count ?? 0}");

        _service = svc.ServiceProvider;
        _service.AdvertisementStatusChanged += (_, e) => State($"9999 adv -> {e.Status}");
        _service.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsDiscoverable = false,
            IsConnectable = false,
        });

        // Open the discoverable/connectable 8881 beacon only after 9999 is ready.
        var beacon = await GattServiceProvider.CreateAsync(
            new Guid("00008881-0000-1000-8000-00805f9b34fb"));
        if (beacon.Error != BluetoothError.Success)
            throw new InvalidOperationException($"RX: 8881 provider failed: {beacon.Error}");

        var beaconChar = await beacon.ServiceProvider.Service.CreateCharacteristicAsync(
            new Guid("00008882-0000-1000-8000-00805f9b34fb"),
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read,
                ReadProtectionLevel = GattProtectionLevel.Plain,
                WriteProtectionLevel = GattProtectionLevel.Plain,
            });
        if (beaconChar.Error != BluetoothError.Success)
            throw new InvalidOperationException($"RX: 0x8882 create failed: {beaconChar.Error}");

        beacon.ServiceProvider.AdvertisementStatusChanged += (_, e) =>
        {
            State($"8881 beacon -> {e.Status}");
            try
            {
                if (e.Status == GattServiceProviderAdvertisementStatus.Started)
                {
                    BeaconStarted?.Invoke();
                }
                else if (e.Status == GattServiceProviderAdvertisementStatus.Aborted)
                {
                    BeaconAborted?.Invoke();
                    RetryBeaconAsync(beacon.ServiceProvider);
                }
            }
            catch { }
        };

        IsRunning = true;
        _beacon = beacon.ServiceProvider;
        State("Local GATT database ready (8881 + 9999, pairless/plain); starting connectable beacon");
        beacon.ServiceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsDiscoverable = true,
            IsConnectable = true,
        });

        State($"Receive server active (beacon 8881 + service 9999), BT Name='{BluetoothName(DeviceName)}'");'''
rg.write_text(text[:start] + replacement + text[end:], encoding="utf-8")
print("GATT negotiation compatibility patch applied")
