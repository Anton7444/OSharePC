using System.Text.Json;

namespace OShareSender;

/// <summary>Remembers the real names of same-account devices seen only through their Contacts-mode beacon
/// (which carries a device type and account digest but no name). Persisted next to settings.json.</summary>
public static class ContactNames
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSharePC", "contact-names.json");
    private static Dictionary<string, string>? _names;

    private static string Key(int deviceType, string digest) => $"{deviceType:X2}{digest.ToUpperInvariant()}";

    private static Dictionary<string, string> Load()
    {
        if (_names is not null) return _names;
        try
        {
            if (File.Exists(FilePath))
                _names = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
        }
        catch (Exception ex) { Log.Warn($"NAMES: could not read {FilePath}: {ex.Message}"); }
        return _names ??= new Dictionary<string, string>();
    }

    public static string? Get(int deviceType, string digest)
    {
        lock (Gate) return Load().TryGetValue(Key(deviceType, digest), out var n) && !string.IsNullOrWhiteSpace(n) ? n : null;
    }

    /// <summary>Stores a name; returns true when it changed.</summary>
    public static bool Set(int deviceType, string digest, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        lock (Gate)
        {
            var d = Load();
            var k = Key(deviceType, digest);
            if (d.TryGetValue(k, out var old) && old == name) return false;
            d[k] = name;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(d));
            }
            catch (Exception ex) { Log.Warn($"NAMES: could not save: {ex.Message}"); }
        }
        Log.Info($"NAMES: device type {deviceType} ({digest}) is called '{name}'");
        return true;
    }
}
