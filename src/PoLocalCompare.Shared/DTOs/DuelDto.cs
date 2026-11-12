using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.DTOs;

public sealed class DuelDto
{
    public DuelId DuelId { get; init; }
    public string PromptText { get; init; } = string.Empty;
    public string PromptFull { get; init; } = string.Empty;
    public ModelId LeftModelId { get; init; }
    public ModelId RightModelId { get; init; }

    /// <summary>
    /// Display name of the left model, snapshotted on the duel row when it was created. Falls
    /// back to <see cref="LeftModelId"/> when the duel predates the snapshot. The Arena uses
    /// this rather than the id so a duel that has not produced any result rows yet (which
    /// means the model name is not on the result DTOs) still renders something human-readable
    /// on the race lanes and the failure card.
    /// </summary>
    public string LeftModelName { get; init; } = string.Empty;

    /// <inheritdoc cref="LeftModelName"/>
    public string RightModelName { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DuelVerdict Verdict { get; init; }
    public ModelId? WinnerModelId { get; init; }
    public ModelId? LoserModelId { get; init; }
    public double? EloShiftWinner { get; init; }
    public double? EloShiftLoser { get; init; }
    public IReadOnlyList<DuelResultDto> Results { get; init; } = [];
    /// <summary>True when only one model completed (partial duel).</summary>
    public bool IsPartial { get; init; }

    /// <summary>Whether a person or the auto-judge decided this duel.</summary>
    public VerdictSource VerdictSource { get; init; }

    /// <summary>The auto-judge's stated reason, when <see cref="VerdictSource"/> is Ai.</summary>
    public string? JudgeRationale { get; init; }

    /// <summary>Deployment name of the model that judged, when <see cref="VerdictSource"/> is Ai.</summary>
    public string? JudgeModel { get; init; }

    /// <summary>The judge's checklist behind <see cref="JudgeRationale"/>: each requested element and which page delivered it.</summary>
    public string? JudgeAnalysis { get; init; }

    /// <summary>
    /// Seconds the Arena waits for a human pick before the auto-judge decides; 0 means the
    /// auto-judge is off. The window is measured from <see cref="CompletedAt"/>, not from when
    /// the client read this — a page opened late must resume the clock, not restart it.
    /// </summary>
    public int AutoJudgeDelaySeconds { get; init; }

    /// <summary>
    /// Reason the auto-judge stood down on a still-<see cref="DuelVerdict.Pending"/> duel, if
    /// it has. Carries rate-limit notes ("HTTP 429") and per-side failure notes that are
    /// genuinely interesting to the human asked to finish the duel by hand; absent on duels
    /// that were judged, or never tried.
    /// </summary>
    public string? JudgeStoodDownReason { get; init; }

    /// <summary>
    /// preferred_username of whoever created the duel, or <c>"anonymous"</c> when the open
    /// gate was in effect and no claim was present. Forensic — never used for authorization.
    /// Null on rows written before the schema added this field.
    /// </summary>
    public string? OwnerId { get; init; }

    /// <summary>
    /// preferred_username of whoever clicked the verdict, or <c>"anonymous"</c> when the gate
    /// was open. Null for AI verdicts (use <see cref="JudgeModel"/> instead) and for duels
    /// written before the schema added this field.
    /// </summary>
    public string? VerdictBy { get; init; }
}
