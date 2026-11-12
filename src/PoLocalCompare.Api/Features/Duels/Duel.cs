// GoF: Aggregate Root
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public sealed class Duel
{
    public DuelId DuelId { get; init; }
    public string PromptText { get; init; }
    public string PromptFull { get; init; }
    public ModelId LeftModelId { get; init; }
    public ModelId RightModelId { get; init; }

    /// <summary>
    /// Display names captured when the duel was created. A duel is a historical record, so it
    /// has to stay readable after a model is retired from the catalog — without this the
    /// Archive resolved names by looking the IDs up live and rendered "[Deleted Model]" for
    /// most of its rows. Readers still prefer the live catalog name (a rename should show
    /// through) and fall back to the snapshot.
    /// </summary>
    public string? LeftModelName { get; set; }

    /// <inheritdoc cref="LeftModelName"/>
    public string? RightModelName { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DuelVerdict Verdict { get; set; }
    public ModelId? WinnerModelId { get; set; }
    public ModelId? LoserModelId { get; set; }
    public double? EloShiftWinner { get; set; }
    public double? EloShiftLoser { get; set; }
    /// <summary>
    /// Who decided this duel. Defaults to <see cref="VerdictSource.Human"/> so duels recorded
    /// before the auto-judge existed read back as human-judged, which is what they were.
    /// </summary>
    public VerdictSource VerdictSource { get; set; } = VerdictSource.Human;

    /// <summary>The auto-judge's one-line reason, when <see cref="VerdictSource"/> is Ai.</summary>
    public string? JudgeRationale { get; set; }

    /// <summary>Deployment name of the model that judged, when <see cref="VerdictSource"/> is Ai.</summary>
    public string? JudgeModel { get; set; }

    /// <summary>The judge's element-by-element checklist behind <see cref="JudgeRationale"/>, when it gave one.</summary>
    public string? JudgeAnalysis { get; set; }

    /// <summary>
    /// What the judge's own calls for this duel cost. Kept on the duel rather than on a result
    /// row because it belongs to neither model; <see cref="SpendGuard"/> counts it toward the
    /// daily cap, which until 2026-10-11 saw only the contestants' spend.
    /// </summary>
    public double? JudgeCostUsd { get; set; }

    /// <summary>
    /// Last standing-down reason recorded while the duel was still <see cref="DuelVerdict.Pending"/>.
    /// Set when <see cref="AutoJudge"/> cannot decide (rate-limit, both sides failed, etc.) and
    /// later cleared when a verdict lands.
    /// </summary>
    public string? JudgeStoodDownReason { get; set; }

    /// <summary>True when only one model completed (partial duel).</summary>
    public bool IsPartial { get; set; }

    /// <summary>
    /// Actor that created the duel (preferred_username claim, or "anonymous" when the gate
    /// is open). Forensically stamped, never used for authorization. Nullable so existing
    /// rows deserialise cleanly after the schema addition.
    /// </summary>
    public string? OwnerId { get; set; }

    /// <summary>
    /// Actor that recorded the verdict (preferred_username claim, or "anonymous"). For the
    /// AI judge this is null — <see cref="JudgeModel"/> names the deployment instead. First
    /// write wins; the second write throws inside <c>RecordVerdictHandler</c>.
    /// </summary>
    public string? VerdictBy { get; set; }

    /// <summary>Storage concurrency token; set when loaded from Table Storage (standards §5.5).</summary>
    public string? ETag { get; set; }

    public Duel(
        DuelId duelId,
        string promptText,
        string promptFull,
        ModelId leftModelId,
        ModelId rightModelId)
    {
        if (string.IsNullOrWhiteSpace(promptText))
            throw new ArgumentException("PromptText cannot be empty.", nameof(promptText));

        if (leftModelId == rightModelId)
            throw new ArgumentException("LeftModelId and RightModelId must differ — a model cannot duel itself.");

        DuelId = duelId;
        PromptText = promptText;
        PromptFull = promptFull;
        LeftModelId = leftModelId;
        RightModelId = rightModelId;
        StartedAt = DateTimeOffset.UtcNow;
        Verdict = DuelVerdict.Pending;
    }

    // Parameterless constructor for Azure Table Storage deserialization
    public Duel()
    {
        PromptText = string.Empty;
        PromptFull = string.Empty;
    }
}
