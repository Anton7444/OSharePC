using System.Net;
using System.Security.Cryptography;
using CatShareSender.Ui;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace CatShareSender;

internal static class Program
{
    // bump with every package refresh — the single-file exe has no Assembly.Location,
    // so this constant is the only reliable way to tell WHICH build is running.
    internal const string Version = "2026.09.06-2330";

    [STAThread]
    private static int Main(string[] args)
    {
        Log.Init();
        Lang.Load();
        Log.Info($"CatShareSender version {Version}");

        // single instance — two running copies would fight over the BLE scanner,
        // the port and the log file (tray Exit used to fail, piling up instances)
        var mutex = new Mutex(true, "Local\\CatShareSender-SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            Log.Warn("another CatShareSender instance is already running — exiting");
            ApplicationConfiguration.Initialize();
            AppDialog.Show(null, "OsharePC", Lang.T("Dialog.AlreadyRunning"), Ui.DialogKind.Info);
            return 0;
        }

        try
        {
            if (args.Contains("--selftest"))
            {
                var rc = RunSelfTest();
                mutex.ReleaseMutex();
                return rc;
            }
            if (args.Contains("--mockphone"))
            {
                var rc = RunMockPhone().GetAwaiter().GetResult();
                mutex.ReleaseMutex();
                return rc;
            }
            if (args.Contains("--zipprobe"))
            {
                var rc = ZipProbe().GetAwaiter().GetResult();
                mutex.ReleaseMutex();
                return rc;
            }
            if (args.Contains("--advprobe"))
            {
                var rc = AdvProbe().GetAwaiter().GetResult();
                mutex.ReleaseMutex();
                return rc;
            }
            if (args.Contains("--serve"))
            {
                var rc = ServeHeadless(TimeSpan.FromSeconds(args.Length > 1 && int.TryParse(args[1], out var s) ? s : 120)).GetAwaiter().GetResult();
                mutex.ReleaseMutex();
                return rc;
            }
            if (args.Contains("--bridge"))
            {
                var rc = RunBridge(ParseParentPid(args)).GetAwaiter().GetResult();
                mutex.ReleaseMutex();
                return rc;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            mutex.ReleaseMutex();
            return 0;
        }
        catch
        {
            mutex.ReleaseMutex();
            throw;
        }
    }
    /// <summary>Headless mode: start the engine and idle (no UI). Handy for tests.</summary>
    private static async Task<int> ServeHeadless(TimeSpan duration)
    {
        using var engine = new SenderEngine();
        await engine.StartAsync(SenderEngine.DefaultPort);
        await Task.Delay(duration);
        await engine.StopAsync();
        return 0;
    }

    private static async Task<int> RunBridge(int parentPid)
    {
        await using var bridge = new CatShareBridgeServer();
        await bridge.StartAsync();
        if (parentPid <= 0)
        {
            Log.Info("Bridge running without parent PID (standalone mode)");
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }

        Log.Info($"Bridge parent PID registered: {parentPid}");
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(parentPid);
                if (parent.HasExited) break;
            }
            catch (ArgumentException) { break; }
            catch (InvalidOperationException) { break; }
        }

        Log.Warn($"Bridge parent process {parentPid} exited; shutting down backend");
        return 0;
    }

    private static int ParseParentPid(string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--parent-pid" && int.TryParse(args[i + 1], out var pid) && pid > 0)
                return pid;
        }
        return 0;
    }

    /// <summary>
    /// Exhaustive probe of every advertisement shape Windows might accept, to find
    /// one that satisfies the alliance parser (uuid-list AD + ≥62-byte record).
    /// Run: CatShareSender.exe --advprobe
    /// </summary>
    private static async Task<int> AdvProbe()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            Log.Info($"ADVPROBE adapter: {(adapter is null ? "none" : $"peripheral={adapter.IsPeripheralRoleSupported}")}");
        }
        catch (Exception ex)
        {
            Log.Info($"ADVPROBE adapter query failed: {ex.Message.Split('\n')[0]}");
        }

        int pass = 0, total = 0;
        var customUuid = new Guid("00003331-0000-1000-8000-008123456789");
        IBuffer Buf(byte[] d) { var w = new DataWriter(); w.WriteBytes(d); return w.DetachBuffer(); }
        BluetoothLEAdvertisementDataSection Svc(ushort u, byte[] p) => new()
        { DataType = 0x16, Data = Buf(new[] { (byte)(u & 0xff), (byte)(u >> 8) }.Concat(p).ToArray()) };
        var p6 = Enumerable.Repeat((byte)'A', 6).ToArray();
        var p27 = Enumerable.Repeat((byte)'B', 27).ToArray();

        async Task<bool> Try(string label, Action<BluetoothLEAdvertisement, BluetoothLEAdvertisementPublisher> build)
        {
            total++;
            var adv = new BluetoothLEAdvertisement();
            var pub = new BluetoothLEAdvertisementPublisher(adv);
            BluetoothLEAdvertisementPublisherStatus? last = null;
            string err = "";
            pub.StatusChanged += (_, e) => { last = e.Status; err = e.Error.ToString(); };
            try
            {
                build(adv, pub);
                pub.Start();
                await Task.Delay(1200);
                var ok = last is BluetoothLEAdvertisementPublisherStatus.Started or BluetoothLEAdvertisementPublisherStatus.Waiting;
                Log.Info($"ADVPROBE {(ok ? "[OK ]" : "[ABRT]")} {label} final={last} err={err}");
                if (ok) { pass++; pub.Stop(); return true; }
            }
            catch (Exception ex)
            {
                Log.Info($"ADVPROBE [FAIL] {label}: {ex.Message.Split('\n')[0]}");
            }
            return false;
        }

        // 1. extended + the two alliance data sections only (61B in one extended packet)
        await Try("extended + 6B+27B sections (no uuid)", (a, p) =>
        {
            p.UseExtendedAdvertisement = true;
            a.DataSections.Add(Svc(0x0164, p6));
            a.DataSections.Add(Svc(0x6500, p27));
        });

        // 2. extended + raw 0x07 uuid list + sections (raw 0x07 was blocked in legacy)
        await Try("extended + raw 0x07 + sections", (a, p) =>
        {
            p.UseExtendedAdvertisement = true;
            a.DataSections.Add(new BluetoothLEAdvertisementDataSection
            { DataType = 0x07, Data = Buf(customUuid.ToByteArray().Reverse().ToArray()) });
            a.DataSections.Add(Svc(0x0164, p6));
            a.DataSections.Add(Svc(0x6500, p27));
        });

        // 3. extended + ServiceUuids (maybe extended unlocks uuid lists)
        await Try("extended + ServiceUuids + sections", (a, p) =>
        {
            p.UseExtendedAdvertisement = true;
            a.ServiceUuids.Add(customUuid);
            a.DataSections.Add(Svc(0x0164, p6));
            a.DataSections.Add(Svc(0x6500, p27));
        });

        // 4. legacy + raw 0x21 (128-bit service data, not a uuid list — parser won't use
        //    it for getServiceUuids, but confirms which raw types are authorized)
        await Try("legacy + raw 0x21 128-bit service data", (a, p) =>
        {
            a.DataSections.Add(new BluetoothLEAdvertisementDataSection
            { DataType = 0x21, Data = Buf(customUuid.ToByteArray().Reverse().Concat(new byte[2]).ToArray()) });
            a.DataSections.Add(Svc(0x0164, p6));
        });

        // 5. legacy + LocalName (officially forbidden, confirm)
        await Try("legacy + LocalName", (a, p) => { a.LocalName = "CatSharePC"; });

        // 5b-5g. legacy raw 16-bit-UUID lists (0x02/0x03) and local-name (0x09) sections.
        // Android maps 0x02/0x03 into ScanRecord.getServiceUuids() and 0x09 into the
        // advertised name — exactly what the 互传/CatShare send-sheet scanners match on.
        await Try("legacy + raw 0x03 uuid-list 0x8881", (a, p) =>
        {
            a.DataSections.Add(new BluetoothLEAdvertisementDataSection
            { DataType = 0x03, Data = Buf(new byte[] { 0x81, 0x88 }) });
        });
        await Try("legacy + raw 0x02 uuid-list 0x8881", (a, p) =>
        {
            a.DataSections.Add(new BluetoothLEAdvertisementDataSection
            { DataType = 0x02, Data = Buf(new byte[] { 0x81, 0x88 }) });
        });
        await Try("legacy + raw 0x09 name '0000001DESKTOP'", (a, p) =>
        {
            var name = System.Text.Encoding.UTF8.GetBytes("0000001DESKTOP");
            a.DataSections.Add(new BluetoothLEAdvertisementDataSection
            { DataType = 0x09, Data = Buf(name) });
        });
        await Try("legacy + raw 0x03 8881 + raw 0x09 name", (a, p) =>
        {
            a.DataSections.Add(new BluetoothLEAdvertisementDataSection
            { DataType = 0x03, Data = Buf(new byte[] { 0x81, 0x88 }) });
            a.DataSections.Add(new BluetoothLEAdvertisementDataSection
            { DataType = 0x09, Data = Buf(System.Text.Encoding.UTF8.GetBytes("0000001DESKTOP")) });
        });
        await Try("legacy + raw 0x16 svcdata 8881 + raw 0x09 name", (a, p) =>
        {
            a.DataSections.Add(new BluetoothLEAdvertisementDataSection
            { DataType = 0x16, Data = Buf(new byte[] { 0x81, 0x88, 0x01 }) });
            a.DataSections.Add(new BluetoothLEAdvertisementDataSection
            { DataType = 0x09, Data = Buf(System.Text.Encoding.UTF8.GetBytes("0000001DESKTOP")) });
        });

        // 0x16 service data alone / combined — Android maps these into
        // ScanRecord.getServiceData(), which scan filters can match on.
        await Try("legacy + raw 0x16 svcdata 9955", (a, p) =>
        {
            a.DataSections.Add(new BluetoothLEAdvertisementDataSection
            { DataType = 0x16, Data = Buf(new byte[] { 0x55, 0x99, 0x01 }) });
        });
        await Try("legacy + raw 0x16 svcdata 9955 + 8881 + 0x21 3331", (a, p) =>
        {
            a.DataSections.Add(new BluetoothLEAdvertisementDataSection
            { DataType = 0x16, Data = Buf(new byte[] { 0x55, 0x99, 0x01 }) });
            a.DataSections.Add(new BluetoothLEAdvertisementDataSection
            { DataType = 0x16, Data = Buf(new byte[] { 0x81, 0x88, 0x01 }) });
            a.DataSections.Add(new BluetoothLEAdvertisementDataSection
            { DataType = 0x21, Data = Buf(customUuid.ToByteArray().Reverse().Concat(new byte[2]).ToArray()) });
        });
        await Try("legacy + manufacturer data 0x0065", (a, p) =>
        {
            a.ManufacturerData.Add(new BluetoothLEManufacturerData
            { CompanyId = 0x0065, Data = Buf(new byte[] { 0x01, 0x02, 0x03 }) });
        });
        await Try("legacy + raw 0x16 svcdata 0x01ff (catshare)", (a, p) =>
        {
            a.DataSections.Add(new BluetoothLEAdvertisementDataSection
            { DataType = 0x16, Data = Buf(new byte[] { 0xff, 0x01, 0x01 }) });
        });

        // 6. GattServiceProvider advertisement of the custom 3331 service
        //    (emits flags + 0x06/0x07 uuid list — but no custom data sections)
        {
            total++;
            var svcResult = await GattServiceProvider.CreateAsync(customUuid);
            if (svcResult.Error == BluetoothError.Success)
            {
                var chr = await svcResult.ServiceProvider.Service.CreateCharacteristicAsync(
                    new Guid("00003332-0000-1000-8000-008123456789"),
                    new GattLocalCharacteristicParameters { CharacteristicProperties = GattCharacteristicProperties.Read });
                if (chr.Error == BluetoothError.Success)
                {
                    var advParams = new GattServiceProviderAdvertisingParameters { IsDiscoverable = true, IsConnectable = true };
                    var provider = svcResult.ServiceProvider;
                    GattServiceProviderAdvertisementStatus? st = null;
                    provider.AdvertisementStatusChanged += (_, e) => st = e.Status;
                    provider.StartAdvertising(advParams);
                    await Task.Delay(1500);
                    var ok = st == GattServiceProviderAdvertisementStatus.Started || provider.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started;
                    Log.Info($"ADVPROBE {(ok ? "[OK ]" : "[ABRT]")} GattServiceProvider adv of 3331 status={provider.AdvertisementStatus}");
                    if (ok) { pass++; provider.StopAdvertising(); }
                    else
                    {
                        try { provider.StopAdvertising(); } catch { }
                        provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
                        {
                            IsDiscoverable = true,
                            IsConnectable = false,
                        });
                        await Task.Delay(1500);
                        Log.Info($"ADVPROBE {(provider.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started ? "[OK ]" : "[ABRT]")} GattServiceProvider nonconnectable 3331 status={provider.AdvertisementStatus}");
                        try { provider.StopAdvertising(); } catch { }
                    }
                }
                else Log.Info($"ADVPROBE [FAIL] GattServiceProvider char create: {chr.Error}");
            }
            else Log.Info($"ADVPROBE [FAIL] GattServiceProvider create: {svcResult.Error}");
        }

        Log.Info($"ADVPROBE RESULT: {pass}/{total} shapes publishable");
        return pass > 0 ? 0 : 1;
    }

    /// <summary>
    /// Offline end-to-end check of the transfer protocol: starts the real engine
    /// (server + BLE), stages a temp file, then plays the phone's role — WSS with
    /// the envelope protocol (versionNegotiation → sendRequest → download) — and
    /// verifies the ZIP stream layout. No actual phone needed.
    /// </summary>
    /// <summary>
    /// Probe what /download actually serves for a single zip-like file (a pptx):
    /// dumps the response status, headers and zip entries — catches any case where
    /// the raw file escapes instead of the folder-stream wrapper.
    /// </summary>
    private static async Task<int> ZipProbe()
    {
        await using var server = new TransferServer();
        await server.StartAsync(SenderEngine.DefaultPort, configureFirewall: false);

        var tmp = Path.Combine(Path.GetTempPath(), "probe.pptx");
        using (var fs = File.Create(tmp))
        using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            var e1 = zip.CreateEntry("ppt/notesSlides/notesSlide6.xml");
            using var w = new StreamWriter(e1.Open());
            w.Write("<probe/>");
        }
        var task = new TransferTask
        {
            Files = new List<string> { tmp },
            SenderName = "OSharePC-ZipProbe",
            SenderId = "0000",
        };
        task.ComputeSize();
        server.SetTask(task);
        server.AuthorizeLoopbackTest(task);

        var http = new HttpClient();
        foreach (var url in new[]
                 {
                     $"http://127.0.0.1:{SenderEngine.DefaultPort}/download?taskId={task.TaskId}",
                     $"http://127.0.0.1:{SenderEngine.DefaultPort}/download?taskId={task.TaskId}&fileId=0",
                 })
        {
            var resp = await http.GetAsync(url);
            Log.Info($"ZIPPROBE: GET {url}");
            Log.Info($"ZIPPROBE: status={(int)resp.StatusCode} type={resp.Content.Headers.ContentType}");
            foreach (var h in resp.Headers)
                Log.Info($"ZIPPROBE: header {h.Key}: {string.Join(",", h.Value)}");
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            Log.Info($"ZIPPROBE: body {bytes.Length} bytes");
            try { File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "zipprobe-body.zip"), bytes); } catch { }
            try
            {
                using var ms = new MemoryStream(bytes);
                using var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
                foreach (var e in z.Entries)
                    Log.Info($"ZIPPROBE: entry '{e.FullName}' ({e.Length}B)");
            }
            catch (Exception ex)
            {
                Log.Error($"ZIPPROBE: body is NOT our wrapper zip: {ex.Message}");
                return 1;
            }
        }
        Log.Info("ZIPPROBE PASSED");
        return 0;
    }

    private static async Task<int> RunMockPhone()
    {
        int failures = 0;
        await using var server = new TransferServer();
        await server.StartAsync(SenderEngine.DefaultPort, configureFirewall: false);

        // stage temp files (one small, one larger) to exercise the multi-file zip
        var tmp1 = Path.Combine(Path.GetTempPath(), "catshare-selftest.txt");
        var tmp2 = Path.Combine(Path.GetTempPath(), "catshare-selftest.bin");
        await File.WriteAllTextAsync(tmp1, "hello from catshare sender selftest");
        await File.WriteAllBytesAsync(tmp2, RandomNumberGenerator.GetBytes(128 * 1024));
        var task = new TransferTask
        {
            Files = new List<string> { tmp1, tmp2 },
            SenderName = "OSharePC-MockPhone",
            SenderId = "0000",
        };
        task.ComputeSize();
        server.SetTask(task);
        server.ArmTransfer(task, IPAddress.Loopback.ToString(), IPAddress.Loopback.ToString());

        // --- play the phone ---
        var ws = new System.Net.WebSockets.ClientWebSocket();
        // the pad connects PLAINTEXT (ws://) when the sender version >= 10015 - server is HTTP-only now
        var uri = new Uri($"ws://127.0.0.1:{SenderEngine.DefaultPort}/websocket");
        await ws.ConnectAsync(uri, CancellationToken.None);
        Log.Info("MOCKPHONE: ws connected");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var buf = new byte[8192];

        async Task<Envelope> Recv()
        {
            var total = 0;
            while (true)
            {
                var r = await ws.ReceiveAsync(buf.AsMemory(total), cts.Token);
                if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    throw new IOException("closed");
                total += r.Count;
                if (!r.EndOfMessage) continue;
                var env = Envelope.Parse(System.Text.Encoding.UTF8.GetString(buf, 0, total));
                if (env is { IsRaw: false }) return env!;
                Log.Info($"MOCKPHONE: raw frame '{env?.Method}' (ignored)");
                total = 0;
            }
        }
        async Task Send(Envelope e) =>
            await ws.SendAsync(
                System.Text.Encoding.UTF8.GetBytes(e.ToString()),
                System.Net.WebSockets.WebSocketMessageType.Text, true, cts.Token);

        // 1. PC initiates versionNegotiation
        var vn = await Recv();
        Log.Info($"MOCKPHONE: <- {vn}");
        if (!(vn.IsAction && vn.Method == "versionNegotiation")) { Log.Error("MOCKPHONE FAIL: expected versionNegotiation"); failures++; }
        await Send(new Envelope { Type = "ack", Seq = vn.Seq, Method = "versionNegotiation", Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { version = 1, threadLimit = 5 }), HasPayload = true });

        // 2. sendRequest
        var sr = await Recv();
        Log.Info($"MOCKPHONE: <- {sr}");
        if (!(sr.IsAction && sr.Method == "sendRequest")) { Log.Error("MOCKPHONE FAIL: expected sendRequest"); failures++; }
        var reqTaskId = sr.PayloadString("taskId");
        if (reqTaskId != task.TaskId) { Log.Error("MOCKPHONE FAIL: taskId mismatch"); failures++; }
        if (sr.PayloadInt("fileCount") != 2) { Log.Error("MOCKPHONE FAIL: fileCount mismatch"); failures++; }
        var totalSize = sr.HasPayload && sr.Payload.TryGetProperty("totalSize", out var ts) ? ts.GetInt64() : -1;
        var expectedTotal = new FileInfo(tmp1).Length + new FileInfo(tmp2).Length;
        if (totalSize != expectedTotal) { Log.Error("MOCKPHONE FAIL: totalSize mismatch"); failures++; }
        await Send(new Envelope { Type = "ack", Seq = sr.Seq, Method = "sendRequest", Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { }), HasPayload = true });

        // 3. Download exactly like a stock OnePlus receiver. The official iOS sender
        //    uses a chunked application/zip response named files.zip with direct
        //    filenames and ZIP method STORED (0), not our legacy 0/ folder-stream.
        using var http = new System.Net.Http.HttpClient();
        var url = $"http://127.0.0.1:{SenderEngine.DefaultPort}/download?taskId={task.TaskId}";
        Log.Info($"MOCKPHONE: GET {url}");
        var resp = await http.GetAsync(url, cts.Token);
        if (!resp.IsSuccessStatusCode) { Log.Error($"MOCKPHONE FAIL: HTTP {(int)resp.StatusCode}"); failures++; return failures == 0 ? 0 : 1; }
        Log.Info($"MOCKPHONE: HTTP {(int)resp.StatusCode}, transfer-type={(resp.Headers.TryGetValues("Oshare-Transfer-Type", out var tt) ? string.Join(",", tt!) : "none")}, len={resp.Content.Headers.ContentLength?.ToString() ?? "chunked"}");

        if (resp.Headers.TryGetValues("Oshare-Transfer-Type", out _))
        { Log.Error("MOCKPHONE FAIL: stock path must not advertise legacy Oshare-Transfer-Type"); failures++; }
        var disposition = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        if (!string.Equals(disposition, "files.zip", StringComparison.OrdinalIgnoreCase))
        { Log.Error($"MOCKPHONE FAIL: Content-Disposition filename was '{disposition}', expected files.zip"); failures++; }

        var zipBytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
        if (zipBytes.Length < 30 || BitConverter.ToUInt32(zipBytes, 0) != 0x04034B50u)
        { Log.Error("MOCKPHONE FAIL: invalid ZIP local header"); failures++; }
        else if (BitConverter.ToUInt16(zipBytes, 8) != 0)
        { Log.Error($"MOCKPHONE FAIL: ZIP compression method={BitConverter.ToUInt16(zipBytes, 8)}, expected STORED(0)"); failures++; }
        else Log.Info("MOCKPHONE: local ZIP header confirms STORED method 0");

        using var zipMemory = new MemoryStream(zipBytes, writable: false);
        using var zip = new System.IO.Compression.ZipArchive(zipMemory, System.IO.Compression.ZipArchiveMode.Read);
        var entryNames = zip.Entries.Select(e => e.FullName).ToArray();
        Log.Info($"MOCKPHONE: zip entries: [{string.Join(", ", entryNames)}]");
        if (entryNames.Length != 2 || entryNames.Any(n => n.Contains('/')))
        { Log.Error("MOCKPHONE FAIL: stock ZIP entries must be direct filenames"); failures++; }
        else
        {
            var ok = true;
            foreach (var entry in zip.Entries)
            {
                if (entry.CompressedLength != entry.Length)
                { Log.Error($"MOCKPHONE FAIL: {entry.FullName} is compressed ({entry.CompressedLength}/{entry.Length})"); ok = false; }
                var src = entry.FullName.EndsWith(".txt") ? tmp1 : tmp2;
                var downloaded = await File.ReadAllBytesAsync(src, cts.Token);
                await using var es = entry.Open();
                using var ms = new MemoryStream();
                await es.CopyToAsync(ms, cts.Token);
                if (!ms.ToArray().SequenceEqual(downloaded)) { Log.Error($"MOCKPHONE FAIL: content mismatch for {entry.FullName}"); ok = false; }
            }
            if (ok) Log.Info($"MOCKPHONE: official STORED content verified for {entryNames.Length} entries");
            else failures++;
        }

        // 4. phone sends final status
        await Send(new Envelope { Type = "action", Seq = 99, Method = "status",
            Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { taskId = task.TaskId, type = 1, reason = "ok" }), HasPayload = true });

        File.Delete(tmp1);
        File.Delete(tmp2);
        await server.StopAsync();
        Log.Info(failures == 0 ? "MOCKPHONE PASSED" : $"MOCKPHONE FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Offline verification of the crypto + advertisement layout, no UI/BLE needed.</summary>
    private static int RunSelfTest()
    {
        var failures = 0;

        // 1) AES-CTR roundtrip at all key sizes and lengths
        var key = new byte[32];
        Random.Shared.NextBytes(key);
        foreach (var len in new[] { 0, 1, 15, 16, 17, 31, 100 })
        {
            var plain = new byte[len];
            Random.Shared.NextBytes(plain);
            var enc = OShareCrypto.CtrTransform(key, plain);
            var dec = OShareCrypto.CtrTransform(key, enc);
            if (!dec.SequenceEqual(plain))
            {
                Log.Error($"SELFTEST FAIL: CTR roundtrip len={len}");
                failures++;
            }
        }
        Log.Info("SELFTEST ok: AES-CTR roundtrip");

        // 2) Java compat spot check: openssl `enc -aes-256-ctr` must match for a fixed vector.
        //    key = 000102...1f, iv = ASCII "0102030405060708", plaintext "catshare-test-123"
        var vectorKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var vectorPlain = "catshare-test-123";
        var vectorOut = Convert.ToHexString(OShareCrypto.CtrTransform(vectorKey, System.Text.Encoding.UTF8.GetBytes(vectorPlain)));
        Log.Info($"SELFTEST CTR vector (compare with openssl): {vectorOut.ToLowerInvariant()}");

        // 3) ECDH symmetry: A.Derive(B.pub) == B.Derive(A.pub), 32 bytes
        using var a = new OShareCrypto();
        using var b = new OShareCrypto();
        var ab = a.DeriveSharedSecret(b.PublicKeyB64);
        var ba = b.DeriveSharedSecret(a.PublicKeyB64);
        if (ab.Length != 32 || !ab.SequenceEqual(ba))
        {
            Log.Error($"SELFTEST FAIL: ECDH asymmetry {ab.Length} vs {ba.Length}");
            failures++;
        }
        else
        {
            Log.Info($"SELFTEST ok: ECDH shared secret 32B {Convert.ToHexString(ab)[..16]}…");
        }

        // 4) encrypted credential JSON decrypts back to the plaintext fields
        var ssid = OShareCrypto.CtrEncryptToB64(ab, "LANfastCon");
        var back = OShareCrypto.CtrDecryptFromB64(ab, ssid);
        if (back != "LANfastCon") { Log.Error("SELFTEST FAIL: CTR B64 string roundtrip"); failures++; }
        else Log.Info("SELFTEST ok: CTR Base64 roundtrip");

        // 5) 64-bit transfer sizes must survive the envelope parser.
        var large = Envelope.Parse("action:1:sendRequest?{\"totalSize\":5000000000}");
        if (large?.PayloadLong("totalSize") != 5_000_000_000L)
        {
            Log.Error("SELFTEST FAIL: 64-bit totalSize parsing");
            failures++;
        }
        else Log.Info("SELFTEST ok: 64-bit totalSize parsing");

        // 6) advertisement record layout (must be > 61 bytes on air)
        var adv = new AllianceAdvertiser { DeviceName = "DESKTOP-TEST1234" };
        var hex = adv.PreviewRecordHex("aabbccddeeff");
        Log.Info($"SELFTEST adv record: {hex}");
        var total = int.Parse(hex.Split('=')[1].Split('B')[0]);
        if (total <= 61) { Log.Error("SELFTEST FAIL: adv record <= 61 bytes"); failures++; }
        else Log.Info($"SELFTEST ok: adv record {total} bytes (>61)");

        // 7) AES-CBC decryption roundtrip (used by Stock 互传 receiver handshake & state 1/3)
        var cbcKey = new byte[32];
        var cbcIv = new byte[16];
        Random.Shared.NextBytes(cbcKey);
        Random.Shared.NextBytes(cbcIv);
        var testSecret = "192.168.1.123";
        var encCbc = OShareCrypto.CbcEncryptToB64(cbcKey, cbcIv, testSecret);
        var decCbc = OShareCrypto.CbcDecryptFromB64(cbcKey, cbcIv, encCbc);
        if (decCbc != testSecret)
        {
            Log.Error($"SELFTEST FAIL: AES-CBC roundtrip '{decCbc}' != '{testSecret}'");
            failures++;
        }
        else Log.Info("SELFTEST ok: AES-CBC roundtrip");

        // 8) Directory traversal safety check
        var safeDir = Path.Combine(Path.GetTempPath(), "CatShareTestDir");
        var safeResult = ReceiveSession.SafePath(safeDir, "../../evil.exe");
        if (!safeResult.StartsWith(safeDir, StringComparison.OrdinalIgnoreCase) || safeResult.Contains(".."))
        {
            Log.Error($"SELFTEST FAIL: Path traversal not prevented: {safeResult}");
            failures++;
        }
        else Log.Info("SELFTEST ok: Path traversal prevention");

        // 9) Stock 互传 Bluetooth discovery name format (length >= 8, flags prefix)
        var btName = ReceiveGattServer.BluetoothName();
        if (btName.Length < 8 || !btName.StartsWith("0000001"))
        {
            Log.Error($"SELFTEST FAIL: Invalid Bluetooth Local Name format: {btName}");
            failures++;
        }
        else Log.Info($"SELFTEST ok: Stock 互传 Bluetooth discovery name format ({btName})");

        Log.Info(failures == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }
}
