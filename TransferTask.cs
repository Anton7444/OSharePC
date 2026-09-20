namespace OShareSender;

/// <summary>A pending or running send task: the files we advertise in sendRequest and
/// stream back on GET /download?taskId=...</summary>
public sealed class TransferTask
{
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N");
    public List<string> Files { get; init; } = new();
    public string SenderName { get; init; } = Environment.MachineName;
    public string SenderId { get; init; } = "0000";

    public long TotalSize { get; private set; }
    public long SentBytes;
    public int FileCount => Files.Count;
    public bool Complete;
    public bool Refused;

    public string FirstFileName => Files.Count > 0 ? Path.GetFileName(Files[0]) : "file";
    public string MimeType =>
        Files.Count > 0 && Files.All(f => IsImage(f)) ? "image/*" : "file/*";

    private static readonly string[] ImageExt =
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif" };

    private static bool IsImage(string path) =>
        ImageExt.Contains(Path.GetExtension(path).ToLowerInvariant());

    public void ComputeSize()
    {
        TotalSize = 0;
        foreach (var f in Files)
        {
            try
            {
                TotalSize += new FileInfo(f).Length;
            }
            catch (Exception ex)
            {
                Log.Warn($"staging: could not read size for '{f}': {ex.Message}");
            }
        }

        // STORED ZIP requires CRC/size in the local header before the first payload
        // byte. Start that work immediately after file selection so it overlaps BLE
        // discovery, GATT negotiation and the user's Accept interaction.
        PreparedCrcCache.Begin(Files);
    }

    /// <summary>sendRequest body per pa/d.java (TaskInfo) + OShare receiver expectations.</summary>
    public Dictionary<string, object> BuildSendRequest() => new()
    {
        ["id"] = TaskId,
        ["taskId"] = TaskId,                 // OShare receiver reads taskId first
        ["senderId"] = SenderId,
        ["senderName"] = SenderName,
        ["fileName"] = FirstFileName,
        ["mimeType"] = MimeType,
        ["fileCount"] = FileCount,
        ["totalSize"] = TotalSize,
        ["thumbnail"] = "",
        ["thumbnail_width"] = 0,
        ["thumbnail_height"] = 0,
    };
}
