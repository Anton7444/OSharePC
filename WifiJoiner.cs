using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace CatShareSender;

internal static class WifiJoiner
{
    private static string? _staticAdapter;
    /// <summary>Deterministic temp-profile name: at most ONE can ever leak (a killed
    /// process skips the finally-delete), and CleanupStaleProfiles removes it on startup.
    /// The previous random per-transfer name left a new dead "CatShare-<guid>" network
    /// in the Wi-Fi list every time a delete failed.</summary>
    internal const string ProfileName = "CatShare-Link";

    /// <summary>Delete leftover temp profiles from killed runs so the Windows Wi-Fi
    /// list doesn't accumulate networks that don't exist.</summary>
    public static async Task CleanupStaleProfiles()
    {
        var staleXml = Path.Combine(Path.GetTempPath(), ProfileName + ".xml");
    try
    {
        if (File.Exists(staleXml))
        {
            File.Delete(staleXml);
            Log.Info($"RX: removed stale WLAN profile XML '{staleXml}'");
        }
    }
    catch (Exception ex)
    {
        Log.Warn($"RX: stale WLAN profile XML cleanup failed: {ex.Message}");
    }

    try
        {
            var shown = await Netsh("wlan show profiles");
            foreach (var line in shown.Stdout.Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    line, @"(?:All User Profile|所有用户配置文件)\s*:\s*(.+)$");
                if (!m.Success) continue;
                var profile = m.Groups[1].Value.Trim();
                if (profile.StartsWith("CatShare-", StringComparison.OrdinalIgnoreCase))
                    await Netsh($"wlan delete profile name=\"{profile}\"");
            }
        }
        catch (Exception ex) { Log.Warn($"RX: WLAN profile cleanup failed: {ex.Message}"); }
    }

    public static async Task ConnectAsync(string ssid, string psk, string hostIp, int port, Action<string> state, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ssid) || string.IsNullOrWhiteSpace(psk)) throw new InvalidOperationException("Phone WLAN offer has no SSID/password");
        if (!IPAddress.TryParse(hostIp, out var phoneIp)) throw new InvalidOperationException($"Invalid phone IP: {hostIp}");
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .Where(n => !n.Name.StartsWith("Local Area Connection*", StringComparison.OrdinalIgnoreCase))
            .Where(n => !n.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n.Description.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (nic is null) throw new InvalidOperationException("No physical Wi-Fi adapter found");
        var name = ProfileName;
        var file = Path.Combine(Path.GetTempPath(), name + ".xml");
        await Netsh($"wlan delete profile name=\"{name}\"");   // overwrite-safe reconnects
        state($"Wi-Fi association started on '{nic.Name}'");
        var errors = new List<string>();
        try
        {
            foreach (var auth in new[] { "WPA2PSK", "WPA3SAE" })
            {
                ct.ThrowIfCancellationRequested();
                await File.WriteAllTextAsync(file, Profile(name, ssid, psk, auth), Encoding.UTF8);
                var add = await Netsh($"wlan add profile filename=\"{file}\" user=current");
                if (!add.Success) { errors.Add($"{auth} profile: {add.Detail}"); continue; }
                state($"Wi-Fi association attempted ({auth})");
                var connect = await Netsh($"wlan connect name=\"{name}\" ssid=\"{ssid}\" interface=\"{nic.Name}\"");
                if (!connect.Success) { errors.Add($"{auth} connect: {connect.Detail}"); await Netsh($"wlan delete profile name=\"{name}\""); continue; }
                for (var i = 0; i < 120; i++)
                {
                    await Task.Delay(500, ct);
                    var current = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == nic.Id);
                    var ip = current?.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    var shown = await Netsh("wlan show interfaces");
                    if (current?.OperationalStatus == OperationalStatus.Up && ip is not null && shown.Stdout.Contains(ssid, StringComparison.OrdinalIgnoreCase))
                    {
                        state($"Wi-Fi associated; DHCP address acquired ({ip.Address})");
                        if (ip.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        {
                            SetTemporaryStaticAddress(current.Name, state);
                            current = NetworkInterface.GetAllNetworkInterfaces().First(n => n.Id == current!.Id);
                        }
                        // The on-link 10.102.161.0/24 route from DHCP (or the
                        // static fallback) already beats the Ethernet default
                        // route, so the host route is belt-and-braces only and
                        // needs admin rights — never fail the join over it.
                        try { AddHostRoute(hostIp, current); state($"Host route installed via {current.Name}"); }
                        catch (Exception ex) { Log.Warn($"RX: host route skipped: {ex.Message}"); }
                        if (await Reachable(phoneIp, port, state)) return;
                        errors.Add("Phone IP is not reachable after route installation");
                        break;
                    }
                    if (i % 10 == 9) state($"Waiting for phone hotspot association ({(i + 1) / 2}s)");
                }
                await Netsh($"wlan delete profile name=\"{name}\"");
            }
            throw new IOException("Phone hotspot join failed: " + string.Join(" | ", errors));
        }
        finally { try { await Netsh($"wlan delete profile name=\"{name}\""); } catch { } try { File.Delete(file); } catch { } }
    }

    public static void RestoreAdapter(Action<string> state)
    {
        if (_staticAdapter is null) return;
        try
        {
            var result = Process.Start(new ProcessStartInfo("netsh", $"interface ipv4 delete address name=\"{_staticAdapter}\" address=10.102.161.1") { CreateNoWindow = true, UseShellExecute = false });
            result?.WaitForExit(5000);
            state($"Temporary hotspot address removed from '{_staticAdapter}'");
        }
        catch (Exception ex) { state($"Wi-Fi DHCP restore failed: {ex.Message}"); }
        finally { _staticAdapter = null; }
    }

    private static void SetTemporaryStaticAddress(string adapter, Action<string> state)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh", $"interface ipv4 add address name=\"{adapter}\" address=10.102.161.1 mask=255.255.255.0 store=active") { CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(5000);
        if (p is null || p.ExitCode != 0) throw new IOException("Could not assign temporary hotspot address 10.102.161.1");
        _staticAdapter = adapter;
        state("DHCP unavailable; temporary hotspot address acquired (10.102.161.1/24)");
    }

    private static async Task<bool> Reachable(IPAddress ip, int port, Action<string> state)
    {
        using var c = new TcpClient();
        for (var i = 0; i < 20; i++)
        {
            try { await c.ConnectAsync(ip, port); state($"Phone endpoint reachable ({ip}:{port})"); return true; }
            catch { await Task.Delay(500); }
        }
        return false;
    }

    private static void AddHostRoute(string hostIp, NetworkInterface nic)
    {
        var gateway = nic.GetIPProperties().GatewayAddresses.Select(x => x.Address).FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
        using var del = Process.Start(new ProcessStartInfo("route", $"delete {hostIp}") { CreateNoWindow = true, UseShellExecute = false }); del?.WaitForExit(2000);
        var args = gateway is not null
            ? $"add {hostIp} mask 255.255.255.255 {gateway} metric 1"
            : $"add {hostIp} mask 255.255.255.255 0.0.0.0 metric 1 if {nic.GetIPProperties().GetIPv4Properties()?.Index}";
        using var add = Process.Start(new ProcessStartInfo("route", args) { CreateNoWindow = true, UseShellExecute = false }); add?.WaitForExit(3000);
        if (add is null || add.ExitCode != 0) throw new IOException($"Could not install route to {hostIp}");
    }

    private static string Profile(string name, string ssid, string psk, string auth) => $"<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>{Xml(name)}</name><SSIDConfig><SSID><name>{Xml(ssid)}</name></SSID><nonBroadcast>false</nonBroadcast></SSIDConfig><connectionType>ESS</connectionType><connectionMode>manual</connectionMode><MSM><security><authEncryption><authentication>{auth}</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption><sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{Xml(psk)}</keyMaterial></sharedKey></security></MSM></WLANProfile>";

    private static async Task<NetshResult> Netsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var stdout = await p.StandardOutput.ReadToEndAsync(); var stderr = await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
        return new NetshResult(p.ExitCode == 0, (stderr + " " + stdout).Trim().Replace(Environment.NewLine, " "), stdout);
    }
    private sealed record NetshResult(bool Success, string Detail, string Stdout);
    private static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? "";
}
