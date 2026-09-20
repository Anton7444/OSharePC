using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;

namespace OShareSender;

/// <summary>Connects Windows to the Android-created, temporary Wi-Fi Direct group.</summary>
public sealed class OShareWifiDirectConnection : IDisposable
{
    private readonly WiFiDirectDevice _device;
    public string RemoteHost { get; }

    private OShareWifiDirectConnection(WiFiDirectDevice device, string remoteHost)
    {
        _device = device;
        RemoteHost = remoteHost;
    }

    public static async Task<OShareWifiDirectConnection> ConnectAsync(
        string remoteMac, Action<string>? state = null, CancellationToken ct = default)
    {
        var selector = WiFiDirectDevice.GetDeviceSelector();
        var devices = await DeviceInformation.FindAllAsync(selector).AsTask(ct);
        if (devices.Count == 0)
            throw new InvalidOperationException("Windows found no Wi-Fi Direct device. Enable Wi-Fi and try again.");

        var wanted = Normalize(remoteMac);
        var candidates = devices.Where(d => wanted.Length == 0 ||
            Normalize(d.Id).Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
            candidates = devices.Count == 1 ? [devices[0]] : devices.ToList();

        Exception? last = null;
        foreach (var info in candidates.Take(4))
        {
            ct.ThrowIfCancellationRequested();
            state?.Invoke($"connecting Wi-Fi Direct device '{info.Name}'…");
            try
            {
                var device = await WiFiDirectDevice.FromIdAsync(info.Id).AsTask(ct);
                var host = await WaitForRemoteHostAsync(device, ct);
                state?.Invoke($"Wi-Fi Direct connected ({host})");
                return new OShareWifiDirectConnection(device, host);
            }
            catch (Exception ex)
            {
                last = ex;
                state?.Invoke($"Wi-Fi Direct candidate failed: {ex.Message}");
            }
        }
        throw new InvalidOperationException("Could not join the Android Wi-Fi Direct group.", last);
    }

    private static async Task<string> WaitForRemoteHostAsync(WiFiDirectDevice device, CancellationToken ct)
    {
        for (var i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pair = device.GetConnectionEndpointPairs().FirstOrDefault();
            var host = pair?.RemoteHostName?.CanonicalName ?? pair?.RemoteHostName?.RawName;
            if (!string.IsNullOrWhiteSpace(host)) return host;
            await Task.Delay(200, ct);
        }
        throw new TimeoutException("Wi-Fi Direct connected but did not expose a remote endpoint.");
    }

    private static string Normalize(string text) =>
        new(text.Where(char.IsLetterOrDigit).ToArray());

    public void Dispose()
    {
        try { _device.Dispose(); } catch { }
    }
}
