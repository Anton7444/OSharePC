using System.Text.RegularExpressions;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth;

namespace OShareSender;

/// <summary>
/// Visibility into the adapter's CURRENTLY-ESTABLISHED LE links. Needed because BLE
/// allows only ONE link between a given address pair — if the phone connected
/// inbound to our receive GATT server (its own "senseless" flow does this silently),
/// an outbound sender connect to the same phone can never complete, and
/// GetGattServicesForUuidAsync just times out. The failure is invisible in the
/// per-attempt logs (the remote never answers), so we enumerate the connected set
/// around every send instead and log it.
/// </summary>
internal static class BleLinkDiagnostics
{
    private static readonly Regex PeerAddressRegex = new(@"-((?:[0-9A-F]{2}:){5}[0-9A-F]{2})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<IReadOnlyList<(string Name, string Address, bool Paired)>> GetConnectedLeDevicesAsync()
    {
        var results = new List<(string, string, bool)>();
        // BluetoothLEDevice.GetDeviceSelector() is an AEP query (covers unpaired
        // devices too); AND it with the connected flag to keep only live links.
        var aqs = $"({BluetoothLEDevice.GetDeviceSelector()}) AND (System.Devices.Aep.IsConnected:=System.StructuredQueryType.Boolean#TRUE)";
        DeviceInformationCollection found;
        try
        {
            found = await DeviceInformation.FindAllAsync(aqs);
        }
        catch (Exception ex)
        {
            Log.Info($"BLEDIAG: connected-device query failed: {ex.Message}");
            return results;
        }

        foreach (var di in found)
        {
            // The AEP id embeds the peer address little-endian
            // (…-4F:EA:BA:3B:56:98 for a peer advertising as 98:56:3B:BA:EA:4F);
            // reverse it so the log matches the notation used everywhere else.
            var address = PeerAddressRegex.Match(di.Id) is { Success: true } m
                ? string.Join(":", m.Groups[1].Value.Split(':').Reverse())
                : di.Id;
            results.Add((string.IsNullOrWhiteSpace(di.Name) ? "(unnamed)" : di.Name, address, di.Pairing.IsPaired));
        }
        return results;
    }

    /// <summary>Logs every LE link the adapter currently holds. Called at send start
    /// and on connect failure: a link to the TARGET device listed here while its GATT
    /// discovery times out is the pre-existing-link/role-conflict signature.</summary>
    public static async Task LogConnectedDevicesAsync(string context, ulong? targetAddress = null)
    {
        try
        {
            var devices = await GetConnectedLeDevicesAsync();
            if (devices.Count == 0)
            {
                Log.Info($"BLEDIAG[{context}]: Windows reports no established LE links right now");
                return;
            }
            foreach (var d in devices)
            {
                var target = targetAddress.HasValue && FormatAddress(targetAddress.Value) == d.Address ? " <= SEND TARGET" : "";
                Log.Info($"BLEDIAG[{context}]: established LE link -> '{d.Name}' {d.Address} paired={d.Paired}{target}");
            }
        }
        catch (Exception ex)
        {
            Log.Info($"BLEDIAG[{context}]: enumeration failed: {ex.Message}");
        }
    }

    private static string FormatAddress(ulong address) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((address >> (40 - i * 8)) & 0xFF).ToString("X2")));
}
