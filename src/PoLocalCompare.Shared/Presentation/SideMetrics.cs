using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.Presentation;

/// <summary>
/// Streaming telemetry for one side of a duel. Both sides of the Arena used to carry the
/// same five parallel fields (<c>_leftStatus</c> / <c>_rightStatus</c>, etc.) — a row of
/// declarations that took 10 lines, plus a dozen update sites that wrote one side or the
/// other based on a <c>Side == "Left"</c> branch. Grouping the fields per side shrinks the
/// declaration and lets an update site pick its side with a single index.
/// </summary>
/// <remarks>
/// Mutable by design: status updates arrive many times per second and a per-update
/// allocation would be churn for nothing. The browser-model bookkeeping lives here too:
/// it used to be a second row of <c>_leftXxx</c> / <c>_rightXxx</c> fields on the Arena
/// beside this class, which kept the local status handler as two copy-pasted branches.
/// </remarks>
public sealed class SideMetrics
{
    public DuelStatus Status { get; set; } = DuelStatus.Initializing;

    /// <summary>Done or failed — either way this side's race is over.</summary>
    public bool IsFinished => Status is DuelStatus.Done or DuelStatus.Failed;

    public int TokenCount { get; set; }

    public double? TokenVelocity { get; set; }

    /// <summary>
    /// A browser model's warm-up, captured at the worker's Initializing → Generating edge and
    /// subtracted from its elapsed time so a slow model load does not read as slow generation.
    /// </summary>
    public long? WarmUpMs { get; set; }

    /// <summary>This side runs in the tab (WebGPU) — the only kind the stall note explains.</summary>
    public bool IsBrowserModel { get; set; }

    /// <summary>
    /// The last status the in-tab worker reported. Separate from <see cref="Status"/> because
    /// the server's hub sends its own placeholder <c>Generating</c> for a browser side every
    /// 500 ms, which would hide the worker's own warm-up edge.
    /// </summary>
    public DuelStatus LocalStatus { get; set; } = DuelStatus.Initializing;

    /// <summary>When the in-tab worker last reported anything — the stall note's clock.</summary>
    public DateTimeOffset LastLocalUpdateAt { get; set; } = DateTimeOffset.UtcNow;

    public string? StallDetail { get; set; }
}

/// <summary>Which side of the duel a piece of state belongs to.</summary>
public enum Side
{
    Left,
    Right,
}
