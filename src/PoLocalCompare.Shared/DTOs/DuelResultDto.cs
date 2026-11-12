using PoLocalCompare.Shared.Ids;
namespace PoLocalCompare.Shared.DTOs;

public sealed class DuelResultDto
{
    public DuelId DuelId { get; init; }
    public ModelId ModelId { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public long WarmUpDurationMs { get; init; }
    public long GenerationDurationMs { get; init; }
    public long TotalDurationMs { get; init; }
    public int TokenCount { get; init; }
    public int? PromptTokenCount { get; init; }
    public int? ReasoningTokenCount { get; init; }
    public double TokenVelocity { get; init; }
    public string? FinishReason { get; init; }
    public bool WasTruncated { get; init; }
    public string HtmlOutputRaw { get; init; } = string.Empty;
    public long HtmlOutputSizeBytes { get; init; }
    public double CharacterDensityRatio { get; init; }
    public int OutputQualityScore { get; init; }
    public bool IsFailure { get; init; }
    public string? FailureReason { get; init; }
    public double? EnergyWh { get; init; }
    public double? EnergyCostUsd { get; init; }
    public double? ApiCostUsd { get; init; }
}
