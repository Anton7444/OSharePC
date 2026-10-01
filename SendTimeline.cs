using System.Diagnostics;
using System.Text;

namespace OShareSender;

/// <summary>Per-send phase timestamps. <see cref="Mark"/> logs "TIMING[label] +ms phase" and, when the send ends,
/// one summary line lists every phase with its duration, so slow phases can be compared across many sends.
/// The current timeline flows through async calls (AsyncLocal), so GATT code can mark phases without plumbing.</summary>
internal sealed class SendTimeline : IDisposable
{
    private static readonly AsyncLocal<SendTimeline?> CurrentSlot = new();

    private readonly string _label;
    private readonly SendTimeline? _previous;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly List<(string Phase, long Ms)> _marks = new();
    private string _outcome = "failed";
    private int _ended;

    private SendTimeline(string label)
    {
        _label = label;
        _previous = CurrentSlot.Value;
    }

    /// <summary>Starts a timeline that stays current for the calling async flow until disposed.</summary>
    public static SendTimeline Start(string label)
    {
        var timeline = new SendTimeline(label);
        CurrentSlot.Value = timeline;
        Log.Info($"TIMING[{label}] start");
        return timeline;
    }

    /// <summary>The timeline of the calling async flow; capture it for callbacks that run outside that flow.</summary>
    public static SendTimeline? Current => CurrentSlot.Value;

    /// <summary>Records a phase on the current timeline (no-op outside a timed flow).</summary>
    public static void Mark(string phase) => CurrentSlot.Value?.Add(phase);

    public void Add(string phase)
    {
        long ms;
        lock (_marks)
        {
            ms = _watch.ElapsedMilliseconds;
            _marks.Add((phase, ms));
        }
        Log.Info($"TIMING[{_label}] +{ms}ms {phase}");
    }

    public void Succeeded() => _outcome = "ok";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        CurrentSlot.Value = _previous;
        var sb = new StringBuilder();
        lock (_marks)
        {
            sb.Append($"TIMING[{_label}] SUMMARY {_outcome} total={_watch.ElapsedMilliseconds}ms");
            long prev = 0;
            foreach (var (phase, ms) in _marks)
            {
                sb.Append($" | {phase} {ms - prev}ms");
                prev = ms;
            }
        }
        Log.Info(sb.ToString());
    }
}
