using System.Text.Json;

namespace CatShareSender;

internal sealed record SavedSettings(
    string? DeviceName,
    string? SaveDirectory,
    bool? ReceiveEnabled,
    int? ThemeMode = null,
    int? QuickSaveMode = null,
    bool? MinimizeToTray = null,
    bool? CloseToTray = null);

/// <summary>Persistent backend settings kept beside sender.log.</summary>
internal static class SettingsStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CatShareSender");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    public static SavedSettings Load()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;
            Current = new SavedSettings(
                ReadString(root, "deviceName"),
                ReadString(root, "saveDirectory"),
                root.TryGetProperty("receiveEnabled", out var enabled) &&
                (enabled.ValueKind == JsonValueKind.True || enabled.ValueKind == JsonValueKind.False)
                    ? enabled.GetBoolean()
                    : null,
                ReadInt(root, "themeMode"),
                ReadInt(root, "quickSaveMode"),
                ReadBool(root, "minimizeToTray"),
                ReadBool(root, "closeToTray"));
            return Current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Settings: load failed, using defaults: {ex.Message}");
            Current = new SavedSettings(null, null, null);
            return Current;
        }
    }

    public static SavedSettings Current { get; private set; } = new(null, null, null);

    public static void Save(
        string? deviceName = null,
        string? saveDirectory = null,
        bool? receiveEnabled = null,
        int? themeMode = null,
        int? quickSaveMode = null,
        bool? minimizeToTray = null,
        bool? closeToTray = null)
    {
        SaveGate.Wait();
        try
        {
            Current = new SavedSettings(
                deviceName ?? Current.DeviceName,
                saveDirectory ?? Current.SaveDirectory,
                receiveEnabled ?? Current.ReceiveEnabled,
                themeMode ?? Current.ThemeMode,
                quickSaveMode ?? Current.QuickSaveMode,
                minimizeToTray ?? Current.MinimizeToTray,
                closeToTray ?? Current.CloseToTray);
            Directory.CreateDirectory(DirectoryPath);
            var json = JsonSerializer.Serialize(new
            {
                deviceName = Current.DeviceName,
                saveDirectory = Current.SaveDirectory,
                receiveEnabled = Current.ReceiveEnabled,
                themeMode = Current.ThemeMode,
                quickSaveMode = Current.QuickSaveMode,
                minimizeToTray = Current.MinimizeToTray,
                closeToTray = Current.CloseToTray,
            },
                new JsonSerializerOptions { WriteIndented = true });
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(FilePath)) File.Replace(tempPath, FilePath, null);
            else File.Move(tempPath, FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Settings: save failed: {ex.Message}");
        }
        finally { SaveGate.Release(); }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;
}
