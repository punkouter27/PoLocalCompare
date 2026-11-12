using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.DTOs;

public sealed class VerdictResponseDto
{
    public DuelId DuelId { get; init; }
    public DuelVerdict Verdict { get; init; }
    public ModelId? WinnerModelId { get; init; }
    public ModelId? LoserModelId { get; init; }
    public double? EloShiftWinner { get; init; }
    public double? EloShiftLoser { get; init; }
    public double? WinnerEloAfter { get; init; }
    public double? LoserEloAfter { get; init; }

    /// <summary>Whether a person or the auto-judge decided this duel.</summary>
    public VerdictSource Source { get; init; }

    /// <summary>The auto-judge's stated reason, when <see cref="Source"/> is Ai.</summary>
    public string? JudgeRationale { get; init; }

    /// <summary>The judge's checklist behind the reason, when it gave one.</summary>
    public string? JudgeAnalysis { get; init; }
}
