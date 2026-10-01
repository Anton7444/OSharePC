using System.Runtime.InteropServices;

namespace OShareSender;

/// <summary>The user's real Downloads folder (honours a Downloads folder moved to another drive).</summary>
internal static class DownloadsFolder
{
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>Where incoming files go unless the user picked another folder.</summary>
    public static string DefaultSaveDirectory { get; } = Resolve();

    /// <summary>The previous default (a sub-folder of Downloads); a saved path equal to it is treated as unset.</summary>
    public static string LegacySaveDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "OShare");

    private static string Resolve()
    {
        try
        {
            if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out var pathPtr) == 0)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(pathPtr);
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
                finally { Marshal.FreeCoTaskMem(pathPtr); }
            }
        }
        catch (Exception ex) { Log.Warn($"Downloads folder lookup failed: {ex.Message}"); }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
