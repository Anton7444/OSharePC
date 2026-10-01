using System.Security.Cryptography;
using System.Text.Json;

namespace OShareSender;

internal sealed record SavedSettings(
    string? DeviceName,
    string? SaveDirectory,
    bool? ReceiveEnabled,
    int? ThemeMode = null,
    int? QuickSaveMode = null,
    bool? MinimizeToTray = null,
    bool? CloseToTray = null,
    string? OppoSsoid = null,
    string? OppoBleDeviceId = null,
    string? OppoAccountName = null,
    string? OppoAvatarUrl = null);

/// <summary>Persistent backend settings kept beside sender.log.</summary>
internal static class SettingsStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OSharePC");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    public static SavedSettings Load()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = doc.RootElement;
            // The account identity (ssoid, nickname, avatar URL) is stored DPAPI-encrypted ("<name>Protected").
            // Files from older versions hold it in plain text ("<name>"); take it and rewrite the file encrypted.
            var hadPlaintext = false;
            string? ReadAccountField(string name)
            {
                var protectedValue = ReadString(root, name + "Protected");
                if (protectedValue is not null) return SecretProtector.TryUnprotect(protectedValue);
                var plain = ReadString(root, name);
                if (plain is not null) hadPlaintext = true;
                return plain;
            }
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
                ReadBool(root, "closeToTray"),
                ReadAccountField("oppoSsoid"),
                ReadString(root, "oppoBleDeviceId"),
                ReadAccountField("oppoAccountName"),
                ReadAccountField("oppoAvatarUrl"));
            if (hadPlaintext)
                MigratePlaintextAccountFields();
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

    /// <summary>Rewrites a settings file that still holds account fields in plain text, so they only exist
    /// encrypted.</summary>
    private static void MigratePlaintextAccountFields()
    {
        SaveGate.Wait();
        try
        {
            WriteCurrentToDisk();
            Log.Info("Settings: the OPPO account id, nickname and avatar are now stored encrypted (Windows DPAPI)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Warn($"Settings: encrypting the stored account fields failed: {ex.Message}");
        }
        finally { SaveGate.Release(); }
    }

    /// <summary>Clears the logged-in OPPO account's identity (name, ssoid, avatar) —
    /// used by the Settings "Log out" action. Deliberately separate from <see
    /// cref="Save"/>, whose null-means-"keep existing" parameters can't express
    /// clearing a field. Leaves <c>OppoBleDeviceId</c> alone — it's just a random id for
    /// this PC's own BLE advertising, not part of the account identity.</summary>
    public static void ClearOppoAccount()
    {
        SaveGate.Wait();
        try
        {
            Current = Current with { OppoSsoid = null, OppoAccountName = null, OppoAvatarUrl = null };
            WriteCurrentToDisk();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Warn($"Settings: clearing OPPO account failed: {ex.Message}");
        }
        finally { SaveGate.Release(); }
    }

    public static void Save(
        string? deviceName = null,
        string? saveDirectory = null,
        bool? receiveEnabled = null,
        int? themeMode = null,
        int? quickSaveMode = null,
        bool? minimizeToTray = null,
        bool? closeToTray = null,
        string? oppoSsoid = null,
        string? oppoBleDeviceId = null,
        string? oppoAccountName = null,
        string? oppoAvatarUrl = null)
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
                closeToTray ?? Current.CloseToTray,
                oppoSsoid ?? Current.OppoSsoid,
                oppoBleDeviceId ?? Current.OppoBleDeviceId,
                oppoAccountName ?? Current.OppoAccountName,
                oppoAvatarUrl ?? Current.OppoAvatarUrl);
            WriteCurrentToDisk();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Warn($"Settings: save failed: {ex.Message}");
        }
        finally { SaveGate.Release(); }
    }

    /// <summary>Serializes <see cref="Current"/> to disk. Caller must hold <see
    /// cref="SaveGate"/>.</summary>
    private static void WriteCurrentToDisk()
    {
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
            // Account identity is never written in plain text: DPAPI-encrypted for the current Windows user.
            oppoSsoidProtected = ProtectOrNull(Current.OppoSsoid),
            oppoBleDeviceId = Current.OppoBleDeviceId,
            oppoAccountNameProtected = ProtectOrNull(Current.OppoAccountName),
            oppoAvatarUrlProtected = ProtectOrNull(Current.OppoAvatarUrl),
        },
            new JsonSerializerOptions { WriteIndented = true });
        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        if (File.Exists(FilePath)) File.Replace(tempPath, FilePath, null);
        else File.Move(tempPath, FilePath);
    }

    private static string? ProtectOrNull(string? value) =>
        string.IsNullOrEmpty(value) ? null : SecretProtector.Protect(value);

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
