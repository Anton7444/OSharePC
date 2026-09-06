using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CatShareSender;

/// <summary>
/// Picks the "real" LAN adapter: the active IPv4 interface that owns the default
/// gateway. Virtual adapters (vgate0, VPN/TAP, WSL/Hyper-V switches, loopback...)
/// are skipped so the phone can reach us on the router subnet.
/// </summary>
public sealed record LanInfo(string Name, IPAddress Ip, PhysicalAddress Mac, byte[] MacBytes)
{
    public string IpString => Ip.ToString();

    /// <summary>12 lowercase hex chars of the IPv4-facing adapter MAC (deviceId base).</summary>
    public string MacHex12 => string.Concat(MacBytes.Select(b => b.ToString("x2")));

    public string MacColonLower => string.Join(":", MacBytes.Select(b => b.ToString("x2")));

    public static LanInfo? Detect()
    {
        var candidates = new List<(LanInfo Info, int Score)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var name = $"{ni.Name} ({ni.Description})";
            if (IsVirtual(ni))
            {
                Log.Info($"LAN: skipping virtual adapter {name}");
                continue;
            }

            var ip = ni.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip is null) continue;

            var v4 = ni.GetIPProperties().UnicastAddresses
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (v4 is null) continue;

            var macBytes = ni.GetPhysicalAddress().GetAddressBytes();
            if (macBytes.Length != 6) continue;

            // Prefer Wi-Fi (phones and PCs usually sit on the same AP), then Ethernet.
            int score = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 2 :
                        ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 1 : 0;
            var info = new LanInfo(name, v4, ni.GetPhysicalAddress(), macBytes);
            candidates.Add((info, score + (ip.Equals(v4) ? 1 : 0)));
            Log.Info($"LAN: candidate {name} ip={v4} gw={ip} mac={info.MacColonLower} score={score}");
        }

        var best = candidates.OrderByDescending(c => c.Score).FirstOrDefault();
        if (best.Info is null)
        {
            Log.Warn("LAN: no adapter with an IPv4 default gateway found");
            return null;
        }
        Log.Info($"LAN: using {best.Info.Name} ip={best.Info.IpString} mac={best.Info.MacColonLower}");
        return best.Info;
    }

    private static bool IsVirtual(NetworkInterface ni)
    {
        string[] bad =
        {
            "vgate", "virtual", "vpn", "tap", "tun", "wsl", "hyper-v", "vethernet",
            "loopback", "teredo", "isatap", "bluetooth", "vmware", "virtualbox", "vbox",
            "zerotier", "tailscale", "clash", "mihomo", "sing-box", "wireguard", "openvpn",
            "ppp", "wan miniport", "microsoft wi-fi direct virtual", "hookndis", "npcap"
        };
        var hay = (ni.Name + " " + ni.Description).ToLowerInvariant();
        return bad.Any(hay.Contains);
    }
}
