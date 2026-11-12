using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.DTOs;

public sealed class LeaderboardEntryDto
{
    public int Rank { get; init; }
    public ModelId ModelId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public ModelType ModelType { get; init; }
    public double CurrentElo { get; init; }
    public int DuelCount { get; init; }
    public int WinCount { get; init; }

    /// <summary>Judged draws. Losses are <c>DuelCount - WinCount - DrawCount</c>.</summary>
    public int DrawCount { get; init; }
    public double WinRate { get; init; }
    public double? OutputQualityAvg { get; init; }
    public double? TdpWatts { get; init; }
    public string? WebLlmModelId { get; init; }
    public string? ApiEndpointRef { get; init; }
    public decimal? InputTokenPricePerMillion { get; init; }
    public decimal? OutputTokenPricePerMillion { get; init; }
    /// <summary>
    /// Average API cost (USD) per duel, aggregated across every result row the model has.
    /// Null when the model has no priced duels yet — distinct from zero, which would mean
    /// "ran and was free" (only possible for unpriced local/Ollama models).
    /// </summary>
    public double? AvgApiCostPerDuel { get; init; }
    /// <summary>
    /// ELO earned per dollar of API spend — <c>CurrentElo / AvgApiCostPerDuel</c>, with the
    /// divisor floored at $0.0001 so the column stays readable for near-free models instead of
    /// exploding into the millions. Null when <see cref="AvgApiCostPerDuel"/> is null (local,
    /// Ollama, or any model that has not yet run a priced duel). Higher = more bang per buck.
    /// </summary>
    public double? Value { get; init; }
    /// <summary>
    /// Mean time to first token, in milliseconds, across every non-failed duel this model has
    /// run. Null until it has one.
    /// </summary>
    /// <remarks>
    /// This is the number that actually predicts how fast a model *feels*: total duration is
    /// dominated by how much the model chose to write, and tok/s says nothing about the wait
    /// before anything appears at all. The app has been capturing it since the streaming reader
    /// existed — <c>DuelResult.WarmUpDurationMs</c> is set from first-token latency — but it was
    /// only ever shown per-duel in the Arena's telemetry, never aggregated or ranked. Failed
    /// runs are excluded: a run that crashed before first token would otherwise record a
    /// flatteringly small wait.
    /// </remarks>
    public double? AvgFirstTokenMs { get; init; }

    public double[]? EloSparkline { get; init; }

    /// <summary>
    /// Half-width of the 95% interval on this model's strength, in Elo points, from a
    /// Bradley–Terry fit over every judged duel. Shown as "±N" beside the ELO. Wide for a
    /// model with little evidence, narrow for one with a lot — the thing a bare rating hides.
    /// </summary>
    public double RatingInterval { get; init; }

    /// <summary>
    /// The Bradley–Terry strength itself (the centre of <see cref="RatingInterval"/>). Order-
    /// independent, unlike <see cref="CurrentElo"/>, which depends on the sequence duels were
    /// judged in. The board still ranks by ELO; this is the cross-check.
    /// </summary>
    public double Strength { get; init; }

    /// <summary>Fewer judged duels than the fit needs to say much. The UI marks the row.</summary>
    public bool IsProvisional { get; init; }
}
