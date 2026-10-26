using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.Presentation;

/// <summary>
/// What the Arena says about a race in progress: where each marker sits on its HUD track, the
/// status band's sentence, the live region's announcement, who counts as ahead, and the note
/// that explains a browser model's silence before its first token.
/// </summary>
/// <remarks>
/// Lifted out of Arena.razor.cs on 2026-10-26, where it was private members the Unit tier could
/// not reach. The page keeps the state these read — the two <see cref="SideMetrics"/>, and the
/// leader it holds for 3 s against jitter — and the timing.
/// </remarks>
public static class RaceCommentary
{
    /// <summary>
    /// The leader's marker sits here rather than at 100, so it keeps visibly moving instead of
    /// looking finished.
    /// </summary>
    private const double LeaderPosition = 88;

    /// <summary>A pace ratio below this is not "faster", it is noise.</summary>
    private const double FasterRatio = 1.15;

    /// <summary>A lead must be this share of the combined rate before it is a lead.</summary>
    private const double ClearLead = 0.1;

    /// <summary>
    /// Where <paramref name="side"/>'s marker sits on its HUD track, 0–100. Scaled to whoever
    /// is ahead so both move as the leader advances, rather than creeping toward a fixed token
    /// target neither may reach.
    /// </summary>
    public static double Progress(Side side, SideMetrics left, SideMetrics right)
    {
        var metrics = side == Side.Left ? left : right;
        if (metrics.IsFinished) return 100;
        var frontRunner = Math.Max(left.TokenCount, right.TokenCount);
        return frontRunner <= 0 ? 0 : Math.Clamp(metrics.TokenCount / (double)frontRunner * LeaderPosition, 0, LeaderPosition);
    }

    /// <summary>One sentence on the race for the status band — who is ahead, or who finished.</summary>
    public static string Ticker(SideMetrics left, SideMetrics right, string leftName, string rightName)
    {
        if (FinishLine(left, right, leftName, rightName) is { } finished) return finished;

        var l = left.TokenVelocity ?? 0;
        var r = right.TokenVelocity ?? 0;
        if (l <= 0 && r <= 0) return WarmingUp;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // Speed comparison only means something once both have a real rate; before that a
        // "10× faster" reading is just one model having started earlier.
        if (l > 0 && r > 0)
        {
            var ratio = l >= r ? l / r : r / l;
            if (ratio >= FasterRatio)
                return $"{(l >= r ? leftName : rightName)} is {ratio.ToString("F1", inv)}× faster right now";

            var gap = Math.Abs(left.TokenCount - right.TokenCount);
            if (gap == 0) return "Neck and neck";
            return $"Dead heat on pace — {(left.TokenCount > right.TokenCount ? leftName : rightName)} leads by {gap:N0} tokens";
        }

        return l > 0
            ? $"{leftName} away first at {l.ToString("F0", inv)} tok/s"
            : $"{rightName} away first at {r.ToString("F0", inv)} tok/s";
    }

    /// <summary>
    /// What the status band's live region says during the race. <see cref="Ticker"/> re-words
    /// itself on every coalesced render ("1.3× faster", "1.4× faster"…), and a polite region
    /// re-reads the whole sentence each time. This changes only when the story does: warm-up, a
    /// lead change (<paramref name="leader"/>, already held against jitter), a side finishing.
    /// </summary>
    public static string Announcement(SideMetrics left, SideMetrics right, string leftName, string rightName, Side? leader)
    {
        if (FinishLine(left, right, leftName, rightName) is { } finished) return finished;
        if (leader is { } ahead) return $"{(ahead == Side.Left ? leftName : rightName)} is ahead";
        return (left.TokenVelocity ?? 0) > 0 || (right.TokenVelocity ?? 0) > 0 ? "Neck and neck" : WarmingUp;
    }

    /// <summary>
    /// Who is clearly ahead on tok/s — by at least a tenth of the combined rate — or null.
    /// The caller decides how long a new leader must last before it counts.
    /// </summary>
    public static Side? Leader(SideMetrics left, SideMetrics right)
    {
        var l = left.TokenVelocity ?? 0;
        var r = right.TokenVelocity ?? 0;
        if (l + r <= 0) return null;
        var lead = (l - r) / (l + r);
        return lead > ClearLead ? Side.Left : lead < -ClearLead ? Side.Right : null;
    }

    /// <summary>
    /// Explains pre-first-token silence for browser models. Server-side models stream steadily,
    /// so null here is the normal case and the note simply does not render.
    /// </summary>
    public static string? StallDetail(SideMetrics metrics, DateTimeOffset now, string name)
    {
        if (!metrics.IsBrowserModel || metrics.Status != DuelStatus.Generating || metrics.TokenCount > 0) return null;

        var secondsSince = (int)(now - metrics.LastLocalUpdateAt).TotalSeconds;
        if (secondsSince < 3) return null;   // suppress for the first few seconds

        return $"⏳ {name}: GPU compiling shaders / pre-filling — last worker signal {secondsSince}s ago. Waiting for first token…";
    }

    private const string WarmingUp = "Warming up — waiting for the first tokens…";

    /// <summary>
    /// The sentence once either side is done. DuelComplete, not "both done", ends the race:
    /// with both finished the results are still being read.
    /// </summary>
    private static string? FinishLine(SideMetrics left, SideMetrics right, string leftName, string rightName) =>
        (left.IsFinished, right.IsFinished) switch
        {
            (true, true) => "Both finished — loading the results…",
            (true, false) => $"{leftName} finished first — {rightName} still generating",
            (false, true) => $"{rightName} finished first — {leftName} still generating",
            _ => null,
        };
}
