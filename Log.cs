namespace OShareSender;

/// <summary>Simple timestamped logger with an in-memory ring, file output and UI event.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly List<string> Ring = new();
    private const int RingSize = 800;
    private static StreamWriter? _writer;

    public static event Action<string>? Logged;

    public static string LogFilePath { get; private set; } = "";

    public static void Init()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSharePC");
            Directory.CreateDirectory(dir);
            LogFilePath = Path.Combine(dir, "sender.log");
            _writer = new StreamWriter(LogFilePath, append: true) { AutoFlush = true };
        }
        catch
        {
            // logging must never take the app down
        }
        Info("==== OShareSender started ====");
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERR ", msg);

    public static void Error(string msg, Exception ex) => Write("ERR ", $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
        lock (Gate)
        {
            Ring.Add(line);
            if (Ring.Count > RingSize) Ring.RemoveAt(0);
            try { _writer?.WriteLine(line); } catch { }
        }
        try { Logged?.Invoke(line); } catch { }
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate) return Ring.ToArray();
    }
}
