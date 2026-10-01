namespace OShareSender;

/// <summary>Simple timestamped logger with an in-memory ring, file output and UI event.
/// The on-disk log is capped: sender.log (max 2.5 MB) plus one rotated backup sender.log.1 (max 2.5 MB),
/// so it never exceeds about 5 MB in total.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly List<string> Ring = new();
    private const int RingSize = 800;
    private const long MaxFileBytes = 2_500_000;
    private static StreamWriter? _writer;
    private static long _written;

    public static event Action<string>? Logged;

    public static string LogFilePath { get; private set; } = "";

    public static void Init()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSharePC");
            Directory.CreateDirectory(dir);
            LogFilePath = Path.Combine(dir, "sender.log");
            TrimOversizedLog(LogFilePath);
            OpenWriter();
        }
        catch
        {
            // logging must never take the app down
        }
        Info("==== OShareSender started ====");
    }

    /// <summary>An existing oversized log (from older versions) keeps only its last 2 MB, as sender.log.1.</summary>
    private static void TrimOversizedLog(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxFileBytes) return;
            const int keep = 2_000_000;
            var buf = new byte[keep];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(-keep, SeekOrigin.End);
                var read = fs.Read(buf, 0, keep);
                // drop the (probably partial) first line
                var start = Array.IndexOf(buf, (byte)'\n', 0, read);
                start = start < 0 ? 0 : start + 1;
                File.WriteAllBytes(path + ".1", buf.AsSpan(start, read - start).ToArray());
            }
            File.Delete(path);
        }
        catch { }
    }

    private static void OpenWriter()
    {
        _writer = new StreamWriter(LogFilePath, append: true) { AutoFlush = true };
        _written = new FileInfo(LogFilePath).Length;
    }

    private static void RotateIfNeeded()
    {
        if (_writer is null || _written < MaxFileBytes) return;
        try
        {
            _writer.Dispose();
            var backup = LogFilePath + ".1";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(LogFilePath, backup);
            OpenWriter();
        }
        catch
        {
            try { OpenWriter(); } catch { _writer = null; }
        }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERR ", msg);

    public static void Error(string msg, Exception ex) => Write("ERR ", $"{msg}: {ex}");

    private static readonly Dictionary<string, DateTime> Throttle = new();

    /// <summary>True at most once per <paramref name="every"/> for the same key (used to keep repetitive
    /// BLE scanner lines from flooding the log).</summary>
    public static bool Every(string key, TimeSpan every)
    {
        lock (Throttle)
        {
            var now = DateTime.UtcNow;
            if (Throttle.TryGetValue(key, out var last) && now - last < every) return false;
            Throttle[key] = now;
            if (Throttle.Count > 2000) Throttle.Clear();
            return true;
        }
    }

    private static void Write(string level, string msg)
    {
        // Repetitive discovery chatter: each distinct line at most once every 20 s.
        if ((msg.StartsWith("DISC[", StringComparison.Ordinal) || msg.StartsWith("SCAN: LAN device", StringComparison.Ordinal)) &&
            !Every("chat:" + msg[..Math.Min(msg.Length, 60)], TimeSpan.FromSeconds(20)))
            return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
        lock (Gate)
        {
            Ring.Add(line);
            if (Ring.Count > RingSize) Ring.RemoveAt(0);
            try
            {
                _writer?.WriteLine(line);
                _written += line.Length + 2;
                RotateIfNeeded();
            }
            catch { }
        }
        try { Logged?.Invoke(line); } catch { }
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate) return Ring.ToArray();
    }
}
