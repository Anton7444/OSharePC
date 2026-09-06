using System.Text.Json;

namespace CatShareSender;

/// <summary>
/// OShare WebSocket text-frame envelope, mirroring the decompiled ca/e.java:
///   "action:3:sendRequest?{...json...}"  /  "ack:3:sendRequest?{...}"
/// Bare frames without "?" and fewer than 3 colon parts (e.g. "files",
/// "download_start") are surfaced as Raw messages.
/// </summary>
public sealed class Envelope
{
    public string Type = "";      // "action" | "ack"
    public int Seq;               // message id
    public string Method = "";    // versionNegotiation | sendRequest | status | ...
    public JsonElement Payload;   // may be Undefined
    public bool HasPayload;

    public bool IsAction => Type == "action";
    public bool IsAck => Type == "ack";
    public bool IsRaw => Type.Length == 0;

    public override string ToString() => HasPayload
        ? $"{Type}:{Seq}:{Method}?{Payload.GetRawText()}"
        : $"{Type}:{Seq}:{Method}";

    public static Envelope Raw(string text) => new() { Type = "", Method = text };

    public static Envelope? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            if (!text.Contains('?'))
            {
                var parts = text.Split(':');
                if (parts.Length >= 3 &&
                    int.TryParse(parts[1], out var seq) &&
                    (parts[0] == "action" || parts[0] == "ack"))
                {
                    return new Envelope { Type = parts[0], Seq = seq, Method = parts[2] };
                }
                return Raw(text);
            }

            var head = text[..text.IndexOf('?')];
            var json = text[(text.IndexOf('?') + 1)..];
            var headParts = head.Split(':');
            if (headParts.Length < 3) return Raw(text);
            if (!int.TryParse(headParts[1], out var s)) return Raw(text);
            if (headParts[0] != "action" && headParts[0] != "ack") return Raw(text);

            using var doc = JsonDocument.Parse(json);
            return new Envelope
            {
                Type = headParts[0],
                Seq = s,
                Method = headParts[2],
                Payload = doc.RootElement.Clone(),
                HasPayload = true
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Envelope parse failed for '{text}': {ex.Message}");
            return Raw(text);
        }
    }

    public static string Build(string type, int seq, string method, object? payload)
    {
        var head = $"{type}:{seq}:{method}";
        if (payload is null) return head;
        var json = payload switch
        {
            string s => s,
            JsonElement je => je.GetRawText(),
            _ => JsonSerializer.Serialize(payload)
        };
        return $"{head}?{json}";
    }

    public string PayloadString(string key, string fallback = "")
    {
        if (!HasPayload) return fallback;
        try
        {
            if (Payload.ValueKind == JsonValueKind.Object &&
                Payload.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? fallback;
        }
        catch { }
        return fallback;
    }

    public int PayloadInt(string key, int fallback = 0)
    {
        if (!HasPayload) return fallback;
        try
        {
            if (Payload.ValueKind == JsonValueKind.Object &&
                Payload.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var j)) return j;
            }
        }
        catch { }
        return fallback;
    }

    public long PayloadLong(string key, long fallback = 0)
    {
        if (!HasPayload) return fallback;
        try
        {
            if (Payload.ValueKind == JsonValueKind.Object &&
                Payload.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i)) return i;
                if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var j)) return j;
            }
        }
        catch { }
        return fallback;
    }
}
