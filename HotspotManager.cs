using System.Net.NetworkInformation;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace CatShareSender;

/// <summary>
/// Wraps Windows Mobile Hotspot (NetworkOperatorTetheringManager) so the PC hosts a
/// real Wi-Fi AP whose SSID/PSK we hand to the phone in the BLE credential JSON.
///
/// Why: the stock 互传 credential flow ends with
///   WifiP2pConfig.Builder().setNetworkName(ssid).setPassphrase(psk)   (v8/h.java A())
/// — the phone associates to the AP we name (a legacy join, no real Wi-Fi Direct GO
/// needed), gets DHCP from Windows ICS, and connects to the hotspot gateway (= this
/// PC) on the transfer port. Uses the supported Mobile Hotspot API, NOT the
/// WiFiDirectAdvertisementPublisher that previously caused kernel crashes.
/// </summary>
public sealed class HotspotManager
{
    // NOTE: must NOT end with "fastCon" — that suffix puts the phone into its
    // persistent-group-reinvoke branch, where it saves a zero peer address
    // (OShareServer.d: s0.x(ctx, "00:00:00:00:00:00")) and the association never
    // happens. A normal ssid takes the branch that saves our real BSSID as the
    // external peer address, which is what makes the legacy-AP join work.
    public const string DefaultSsid = "DIRECT-PC-AP";

    public string Ssid { get; private set; } = DefaultSsid;
    public string Psk { get; private set; } = "";
    public string GatewayIp { get; private set; } = "";
    public bool IsRunning { get; private set; }

    /// <summary>The SoftAP BSSID (MAC the phone will see). Falls back to the STA MAC.</summary>
    public string Bssid { get; private set; } = "";

    /// <summary>Starts (or reconfigures + starts) the mobile hotspot. Throws with a
    /// human-readable message when the machine/driver refuses.</summary>
    public async Task EnsureStartedAsync()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile()
            ?? throw new InvalidOperationException(Ui.Lang.T("Engine.NoNetProfile"));

        var tethering = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
        var config = tethering.GetCurrentAccessPointConfiguration();

        // capture what's currently running BEFORE we overwrite the config, so the
        // "already on with a different ssid/psk" restart check below can see it
        var currentSsid = config.Ssid;
        var currentPsk = config.Passphrase;

        // The SSID is stable for compatibility, but the passphrase is intentionally
        // fresh for every transfer start. A public source tree must not expose a
        // reusable hotspot credential. The exact generated PSK is sent to the selected
        // phone over the existing BLE credential channel.
        var desiredPsk = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
            .ToLowerInvariant();
        config.Ssid = DefaultSsid;
        config.Passphrase = desiredPsk;

        Ssid = config.Ssid;
        Psk = config.Passphrase;

        if (tethering.TetheringOperationalState != TetheringOperationalState.On)
        {
            await tethering.ConfigureAccessPointAsync(config);
            var result = await tethering.StartTetheringAsync();
            if (result.Status == TetheringOperationStatus.Success)
                Log.Info($"Hotspot: started ssid='{Ssid}' psk={Psk.Length} chars");
            else
                throw new InvalidOperationException(
                    $"移动热点启动失败({result.Status}): {result.AdditionalErrorMessage}");
        }
        else
        {
            // make sure the running hotspot actually uses our ssid/psk
            if (currentSsid != DefaultSsid || currentPsk != desiredPsk)
            {
                await tethering.ConfigureAccessPointAsync(config);
                await tethering.StopTetheringAsync();
                var result = await tethering.StartTetheringAsync();
                if (result.Status != TetheringOperationStatus.Success)
                    throw new InvalidOperationException(
                        $"移动热点重启失败({result.Status}): {result.AdditionalErrorMessage}");
            }
            Log.Info($"Hotspot: running ssid='{Ssid}'");
        }

        IsRunning = true;
        Bssid = await FindHotspotBssidAsync();
        GatewayIp = FindHotspotGatewayIp();
        Log.Info($"Hotspot: BSSID={Bssid}, gateway={GatewayIp}");
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is not null)
            {
                var tethering = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                if (tethering.TetheringOperationalState == TetheringOperationalState.On)
                    await tethering.StopTetheringAsync();
            }
        }
        catch (Exception ex) { Log.Warn($"Hotspot stop failed: {ex.Message}"); }
        IsRunning = false;
        GatewayIp = "";
    }

    /// <summary>The IPv4 address Windows assigns to the Mobile Hotspot virtual adapter.
    /// This is the address the phone uses as the transfer-server gateway.</summary>
    private static string FindHotspotGatewayIp()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var isApAdapter =
                    nic.Name.StartsWith("本地连接*") || nic.Name.StartsWith("Local Area Connection*");
                if (!isApAdapter) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    if (System.Net.IPAddress.IsLoopback(ua.Address)) continue;
                    var text = ua.Address.ToString();
                    if (!text.StartsWith("169.254.", StringComparison.Ordinal)) return text;
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Hotspot gateway lookup failed: {ex.Message}"); }
        Log.Warn("Hotspot: could not determine gateway IP; peer lock will still protect the transfer");
        return "";
    }

    /// <summary>The hotspot AP is hosted by a "Local Area Connection*" style adapter
    /// (Microsoft Wi-Fi Direct Virtual Adapter) that appears only while the hotspot is
    /// on — its MAC is the BSSID the phone associates to. We try, in order:
    /// 1. `netsh wlan show networks mode=bssid` — our own SSID with its BSSID
    /// 2. the newly appeared AP adapter (any up "Local Area Connection*"/本地连接*
    ///    wireless adapter whose MAC differs from the STA adapter)
    /// 3. the STA adapter MAC (many radios reuse it for the SoftAP)</summary>
    private async Task<string> FindHotspotBssidAsync()
    {
        var staMac = LanInfo.Detect()?.MacColonLower ?? "";

        // the AP adapter can take a moment to come up after StartTetheringAsync
        for (int attempt = 0; attempt < 10; attempt++)
        {
            // 1. netsh scan — most reliable when it lists our own network
            var bssid = FindBssidViaNetsh(Ssid);
            if (bssid.Length > 0) return bssid;

            // 2. the AP adapter
            bssid = FindApAdapterMac(staMac);
            if (bssid.Length > 0) return bssid;

            await Task.Delay(500);
        }

        Log.Warn("Hotspot: could not determine the real BSSID, falling back to the STA MAC");
        return staMac;
    }

    private static string FindBssidViaNetsh(string ssid)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "wlan show networks mode=bssid",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            // Read stderr too, otherwise a chatty netsh can deadlock the pipe.
            var output = p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(8000) && !p.HasExited) { try { p.Kill(); } catch { } }
            if (err.Length > 0) Log.Warn($"netsh bssid stderr: {err.Trim()}");

            // blocks look like:  SSID N : <name>  … BSSID 1 : xx:xx:xx:xx:xx:xx
            var lines = output.Split('\n');
            var ssidLine = new System.Text.RegularExpressions.Regex(
                $@"SSID\s+\d+\s*:\s*{System.Text.RegularExpressions.Regex.Escape(ssid)}\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!ssidLine.IsMatch(lines[i].TrimEnd()) &&
                    !lines[i].TrimEnd().EndsWith($": {ssid}", StringComparison.OrdinalIgnoreCase)) continue;

                for (int j = i; j < Math.Min(i + 12, lines.Length); j++)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        lines[j], @"BSSID\s*\d*\s*:\s*([0-9a-fA-F:]{17})");
                    if (m.Success)
                        return m.Groups[1].Value.ToLowerInvariant();
                }
            }
        }
        catch (Exception ex) { Log.Warn($"netsh bssid lookup failed: {ex.Message}"); }
        return "";
    }

    private static string FindApAdapterMac(string staMac)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                if (nic.OperationalStatus != OperationalStatus.Up) continue;

                var isApAdapter =
                    nic.Name.StartsWith("本地连接*") || nic.Name.StartsWith("Local Area Connection*");
                if (!isApAdapter) continue;

                var mac = nic.GetPhysicalAddress().GetAddressBytes();
                if (mac.Length != 6) continue;
                var macStr = string.Join(":", mac.Select(b => b.ToString("x2")));
                if (macStr == staMac) continue;   // that's the STA side, not the AP
                return macStr;
            }
        }
        catch (Exception ex) { Log.Warn($"AP adapter lookup failed: {ex.Message}"); }
        return "";
    }
}
