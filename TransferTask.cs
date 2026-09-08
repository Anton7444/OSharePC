using System.Text;

namespace CatShareSender;

/// <summary>A pending or running send task: files streamed from /download, or an
/// experimental OShare URL payload exposed through /bigmessage.</summary>
public sealed class TransferTask
{
    public string TaskId { get; init; } = Guid.NewGuid().ToString("N");
    public string MessageId { get; init; } = CreateMessageId();
    public List<string> Files { get; init; } = new();
    public string? SharedUrl { get; init; }
    public string SenderName { get; init; } = Environment.MachineName;
    public string SenderId { get; init; } = "0000";

    public long TotalSize { get; private set; }
    public long SentBytes;
    public bool IsUrl => !string.IsNullOrEmpty(SharedUrl);
    public int FileCount => IsUrl ? 1 : Files.Count;
    public bool Complete;
    public bool Refused;

    public string FirstFileName
    {
        get
        {
            if (!IsUrl) return Files.Count > 0 ? Path.GetFileName(Files[0]) : "file";
            return Uri.TryCreate(SharedUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? uri.Host
                : "link";
        }
    }

    public string MimeType => IsUrl
        ? "http/*"
        : Files.Count > 0 && Files.All(f => IsImage(f)) ? "image/*" : "file/*";

    private static readonly string[] ImageExt =
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif" };

    private static bool IsImage(string path) =>
        ImageExt.Contains(Path.GetExtension(path).ToLowerInvariant());

    public void ComputeSize()
    {
        if (IsUrl)
        {
            TotalSize = Encoding.UTF8.GetByteCount(SharedUrl!);
            return;
        }

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
    }

    public static bool TryNormalizeUrl(string? input, out string normalized, out string error)
    {
        normalized = (input ?? "").Trim();
        error = "";
        if (normalized.Length == 0)
        {
            error = "URL is required.";
            return false;
        }
        if (normalized.Length > 8192)
        {
            error = "URL is too long.";
            return false;
        }
        if (normalized.Any(char.IsWhiteSpace) || normalized.Any(char.IsControl))
        {
            error = "URL cannot contain whitespace or control characters.";
            return false;
        }
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "Enter a valid http:// or https:// URL.";
            return false;
        }
        normalized = uri.AbsoluteUri;
        return true;
    }

    /// <summary>sendRequest body per pa/d.java (TaskInfo) + CatShare receiver expectations.</summary>
    public Dictionary<string, object> BuildSendRequest() => new()
    {
        ["id"] = TaskId,
        ["taskId"] = TaskId,                 // CatShare receiver reads taskId first
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

    /// <summary>
    /// OEM Mac/Windows peers use a compact sendRequest that points at /bigmessage.
    /// The Android URL path identifies URL payloads as http/* and stores the text in
    /// mShareText; keep shareText as the canonical field and include the standard
    /// TaskInfo metadata around it for the OConnect receiver.
    /// </summary>
    public Dictionary<string, object> BuildUrlBigMessage()
    {
        if (!IsUrl) throw new InvalidOperationException("Task is not a URL transfer.");
        return new Dictionary<string, object>
        {
            ["taskId"] = TaskId,
            ["senderId"] = SenderId,
            ["senderName"] = SenderName,
            ["fileName"] = FirstFileName,
            ["mimeType"] = "http/*",
            ["fileCount"] = 1,
            ["totalSize"] = TotalSize,
            ["thumbnail_height"] = 0,
            ["thumbnail_width"] = 0,
            ["shareText"] = SharedUrl!,
        };
    }

    private static string CreateMessageId()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        Span<char> chars = stackalloc char[16];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        return new string(chars);
    }
}
