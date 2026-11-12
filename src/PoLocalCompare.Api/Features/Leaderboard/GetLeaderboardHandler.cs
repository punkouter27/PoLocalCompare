using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Leaderboard;

public sealed class GetLeaderboardHandler(
    IModelRepository modelRepository,
    IEloHistoryRepository eloHistoryRepository,
    IDuelResultRepository duelResultRepository)
{
    public async Task<IReadOnlyList<LeaderboardEntryDto>> HandleAsync(string sortBy = "Elo")
    {
        var models = (await modelRepository.GetAllAsync()).ToList();

        // Each model's history and results are independent reads, so they overlap rather than
        // costing 2N serial round-trips per cache miss (paid three times over on a cold cache —
        // there are three cached sort variants). Bounded, because a model's result set can pull
        // a blob per oversized output and the roster grows without a ceiling.
        //
        // The full history (not just the last 20 for the sparkline) because the Bradley–Terry
        // fit below needs every judged duel. It is the same partition query, unbounded.
        var histories = new List<EloRecord>[models.Count];
        var rows = (await StorageConcurrency.ReadAllAsync(models.Count, async index =>
        {
            var model = models[index];
            var historyTask = eloHistoryRepository.GetAllByModelAsync(model.ModelId);
            var modelResults = (await duelResultRepository.GetByModelIdAsync(model.ModelId)).ToList();
            var history = (await historyTask).ToList();
            histories[index] = history;
            var sparkline = history
                .OrderByDescending(x => x.RecordedAt)
                .Take(20)
                .Reverse()
                .Select(x => Math.Round(x.EloAfter, 1))
                .ToArray();

            // Avg API cost per duel. Only priced duels contribute; unpriced (local/Ollama,
            // or priced-model duels from before a rate was assigned) are excluded from the
            // sum AND the count, so a model that has run only $0.0001 duels doesn't average
            // down to $0.00005 just because we threw in five freebies.
            var pricedResults = modelResults.Where(r => r.ApiCostUsd.HasValue).ToList();
            double? avgCost = pricedResults.Count > 0
                ? pricedResults.Average(r => r.ApiCostUsd!.Value)
                : null;

            // Value = ELO earned per dollar of API spend, with the divisor floored so a
            // "$0.0001 per duel" model doesn't yield a 15-million Value that swamps every
            // useful comparison. The floor matches the precision we render at (4 d.p., i.e.
            // 0.0001) — anything cheaper is effectively free in this UI. Null when there is
            // no average cost at all (Local/Ollama / unpriced remote, same convention as the
            // AvgApiCostPerDuel column it sits next to).
            const double minCostFloor = 0.0001;
            double? value = avgCost.HasValue
                ? Math.Round(Math.Round(model.CurrentElo, 1) / Math.Max(avgCost.Value, minCostFloor), 2)
                : null;

            return new LeaderboardEntryDto
            {
                ModelId = model.ModelId,
                DisplayName = model.DisplayName,
                ModelType = model.ModelType,
                CurrentElo = Math.Round(model.CurrentElo, 1),
                DuelCount = model.DuelCount,
                WinCount = model.WinCount,
                WinRate = WinRateCalculator.Calculate(model.WinCount, model.DuelCount),
                DrawCount = model.DrawCount,
                InputTokenPricePerMillion = model.InputTokenPricePerMillion,
                OutputTokenPricePerMillion = model.OutputTokenPricePerMillion,
                AvgApiCostPerDuel = avgCost,
                Value = value,
                OutputQualityAvg = modelResults.Count > 0
                        ? modelResults.Average(r => r.OutputQualityScore)
                        : null,
                // Only runs that actually produced a token. A failure records a short duration,
                // so counting one would make crashing look like the fastest possible start.
                // The MEDIAN, despite the property's name. Warm-up is measured from the first
                // send, so one duel that sat through a 429's Retry-After counts that wait as
                // "time to first token": Grok 4.6 showed 147.3 s from four duels, three of
                // them under a second. A mean reports the rate limit; a median reports the model.
                AvgFirstTokenMs = Median(modelResults
                        .Where(r => !r.IsFailure && r.WarmUpDurationMs > 0)
                        .Select(r => (double)r.WarmUpDurationMs)),
                EloSparkline = sparkline,
            };
        })).ToList();

        var estimates = BradleyTerry.Fit(
            models.Select(m => m.ModelId),
            BradleyTerry.GamesFrom(histories.SelectMany(h => h)));

        // Sort: any "Value" branch must respect the same nullable convention as the Cost branch
        // (priced rows first, then null-rows tail — not floating to whichever ELO they happen
        // to carry, which is meaningless against a score unit the missing row can't compute).
        var sorted = string.Equals(sortBy, "Quality", StringComparison.OrdinalIgnoreCase)
            ? rows
                .OrderByDescending(x => x.OutputQualityAvg.HasValue)
                .ThenByDescending(x => x.OutputQualityAvg)
                .ThenByDescending(x => x.CurrentElo)
                .ThenBy(x => x.DisplayName)
                .ToList()
            : string.Equals(sortBy, "Cost", StringComparison.OrdinalIgnoreCase)
            ? rows
                .OrderByDescending(x => x.AvgApiCostPerDuel.HasValue)
                .ThenBy(x => x.AvgApiCostPerDuel ?? double.MaxValue) // cheapest first when sorting
                .ThenByDescending(x => x.CurrentElo)
                .ThenBy(x => x.DisplayName)
                .ToList()
            // Ascending: unlike every other dimension here, lower is better.
            : string.Equals(sortBy, "Speed", StringComparison.OrdinalIgnoreCase)
                ? rows
                    .OrderByDescending(x => x.AvgFirstTokenMs.HasValue)
                    .ThenBy(x => x.AvgFirstTokenMs ?? double.MaxValue)
                    .ThenByDescending(x => x.CurrentElo)
                    .ToList()
            : string.Equals(sortBy, "Value", StringComparison.OrdinalIgnoreCase)
            ? rows
                .OrderByDescending(x => x.Value.HasValue)
                .ThenByDescending(x => x.Value ?? double.MinValue) // highest ELO/$ first
                .ThenByDescending(x => x.CurrentElo)
                .ThenBy(x => x.DisplayName)
                .ToList()
            : rows
                .OrderByDescending(x => x.CurrentElo)
                .ThenBy(x => x.DisplayName)
                .ToList();

        return sorted
            .Select((entry, index) =>
            {
                var estimate = estimates[entry.ModelId];
                return new LeaderboardEntryDto
                {
                    Rank = index + 1,
                    ModelId = entry.ModelId,
                    DisplayName = entry.DisplayName,
                    ModelType = entry.ModelType,
                    CurrentElo = entry.CurrentElo,
                    DuelCount = entry.DuelCount,
                    WinCount = entry.WinCount,
                    WinRate = entry.WinRate,
                    DrawCount = entry.DrawCount,
                    InputTokenPricePerMillion = entry.InputTokenPricePerMillion,
                    OutputTokenPricePerMillion = entry.OutputTokenPricePerMillion,
                    AvgApiCostPerDuel = entry.AvgApiCostPerDuel,
                    Value = entry.Value,
                    OutputQualityAvg = entry.OutputQualityAvg,
                    // Was missing from this copy, so the "First token" column always read "—"
                    // even though the value was computed above.
                    AvgFirstTokenMs = entry.AvgFirstTokenMs,
                    EloSparkline = entry.EloSparkline,
                    RatingInterval = estimate.Interval95,
                    Strength = estimate.Rating,
                    IsProvisional = estimate.IsProvisional,
                };
            })
            .ToList();
    }

    /// <summary>Middle value (mean of the middle two for an even count), or null when empty.</summary>
    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return null;
        var mid = sorted.Length / 2;
        return Math.Round(sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2);
    }
}
