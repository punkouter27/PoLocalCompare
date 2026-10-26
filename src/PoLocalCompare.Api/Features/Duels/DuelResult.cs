// GoF: Entity
namespace PoLocalCompare.Api.Features.Duels;

public sealed class DuelResult
{
    public DuelId DuelId { get; init; }
    public ModelId ModelId { get; init; }
    public long WarmUpDurationMs { get; set; }
    public long GenerationDurationMs { get; set; }
    public long TotalDurationMs { get; set; }
    public int TokenCount { get; set; }
    public int? PromptTokenCount { get; set; }
    public int? ReasoningTokenCount { get; set; }
    public double TokenVelocity { get; set; }
    public string? FinishReason { get; set; }
    public bool WasTruncated { get; set; }
    public string HtmlOutputRaw { get; set; }
    public long HtmlOutputSizeBytes { get; set; }
    public double CharacterDensityRatio { get; set; }
    public int OutputQualityScore { get; set; }
    public bool IsFailure { get; set; }
    public string? FailureReason { get; set; }

    // Local models only
    public double? EnergyWh { get; set; }
    public double? EnergyCostUsd { get; set; }

    // Remote models only
    public double? ApiCostUsd { get; set; }

    /// <summary>The model the provider reported serving — not always the one the deployment is named for.</summary>
    public string? ServedModel { get; set; }


    public DuelResult(DuelId duelId, ModelId modelId)
    {
        DuelId = duelId;
        ModelId = modelId;
        HtmlOutputRaw = string.Empty;
    }

    // Parameterless constructor for Azure Table Storage deserialization
    public DuelResult()
    {
        HtmlOutputRaw = string.Empty;
    }
}
