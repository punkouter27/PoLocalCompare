using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.DTOs;

public sealed class DuelSummaryDto
{
    public DuelId DuelId { get; init; }
    public string PromptText { get; init; } = string.Empty;
    public string PromptSummary { get; init; } = string.Empty;
    public ModelId LeftModelId { get; init; }
    public string LeftModelName { get; init; } = string.Empty;
    public ModelId RightModelId { get; init; }
    public string RightModelName { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DuelVerdict Verdict { get; init; }
    public ModelId? WinnerModelId { get; init; }
    public ModelId? LoserModelId { get; init; }
    public double? EloShiftWinner { get; init; }
    public double? EloShiftLoser { get; init; }
    public int? LeftOutputQualityScore { get; init; }
    public int? RightOutputQualityScore { get; init; }
    public double? AvgOutputQualityScore { get; init; }
    /// <summary>True when only one model completed (partial duel).</summary>
    public bool IsPartial { get; init; }
}
