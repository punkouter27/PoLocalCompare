using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Leaderboard;

/// <summary>
/// Assembles everything the model page shows for one model.
/// </summary>
/// <remarks>
/// Sits in the Leaderboard slice rather than a slice of its own because it is the same question
/// the leaderboard already answers, asked about one row instead of all of them: it reuses
/// <see cref="GetLeaderboardHandler"/> for the standing (so rank and ELO cannot disagree with
/// the table the user clicked through from) and <see cref="GetKillListHandler"/> for the
/// head-to-head record, which used to render inline on the leaderboard and now lives here.
///
/// The one genuinely new read is the gallery. It is bounded to
/// <see cref="ModelProfileDto.GalleryLimit"/> because every item is a whole HTML document that
/// a viewport will render — an unbounded version would ship a megabyte of markup to draw a grid.
/// </remarks>
public sealed class GetModelProfileHandler(
    IModelRepository modelRepository,
    IEloHistoryRepository eloHistoryRepository,
    IDuelRepository duelRepository,
    IDuelResultRepository duelResultRepository,
    GetLeaderboardHandler leaderboardHandler,
    GetKillListHandler killListHandler)
{
    /// <summary>Returns null when the id names no model in the catalog.</summary>
    public async Task<ModelProfileDto?> HandleAsync(ModelId modelId)
    {
        var model = await modelRepository.GetByIdAsync(modelId);
        if (model is null) return null;

        // Independent reads, so they overlap rather than costing four serial round-trips.
        var historyTask = eloHistoryRepository.GetAllByModelAsync(modelId);
        var killListTask = killListHandler.HandleAsync(modelId);
        var resultsTask = duelResultRepository.GetByModelIdAsync(modelId);
        var boardTask = leaderboardHandler.HandleAsync();

        var history = (await historyTask).OrderBy(h => h.RecordedAt).ToList();
        var killList = await killListTask;
        var results = (await resultsTask).ToList();
        var board = await boardTask;

        // Rank is the position among models that have played, in the leaderboard's own order —
        // the board lists only those, so this is the number its row shows. The projection's own
        // Rank counts the unplayed models too, and they all sit at 1200: a played model at 1198
        // came out #25 here beside a board that showed it #2 (seen once the profile opened in
        // the board's side panel). Zero means unranked.
        var played = board.Where(e => e.DuelCount > 0).ToList();
        var standing = board.FirstOrDefault(e => e.ModelId == modelId);
        var rank = played.FindIndex(e => e.ModelId == modelId) + 1;

        var opponentNames = await ResolveOpponentNamesAsync(history);

        var pricedResults = results.Where(r => r.ApiCostUsd.HasValue).ToList();

        return new ModelProfileDto
        {
            ModelId = model.ModelId,
            DisplayName = model.DisplayName,
            ModelType = model.ModelType,
            Rank = rank,
            CurrentElo = Math.Round(model.CurrentElo, 1),
            // From the board rather than a second fit: one Bradley–Terry pass over the whole
            // history is the only way a single model's interval means anything, and the board
            // has already paid for it.
            RatingInterval = standing?.RatingInterval ?? 0,
            Strength = standing?.Strength ?? Math.Round(model.CurrentElo, 1),
            IsProvisional = standing?.IsProvisional ?? true,
            DuelCount = model.DuelCount,
            WinCount = model.WinCount,
            DrawCount = model.DrawCount,
            WinRate = WinRateCalculator.Calculate(model.WinCount, model.DuelCount),
            OutputQualityAvg = results.Count > 0 ? results.Average(r => r.OutputQualityScore) : null,
            // Failures record a zero velocity that is not a measurement of anything, so they are
            // excluded rather than averaged in as "very slow".
            AvgTokenVelocity = results.Any(r => !r.IsFailure)
                ? Math.Round(results.Where(r => !r.IsFailure).Average(r => r.TokenVelocity), 1)
                : null,
            // Same convention as the leaderboard's cost column: unpriced results are excluded
            // from the sum AND the count, so free local runs cannot average a paid model down.
            AvgApiCostPerDuel = pricedResults.Count > 0 ? pricedResults.Average(r => r.ApiCostUsd!.Value) : null,
            TdpWatts = model.TdpWatts,
            WebLlmModelId = model.WebLlmModelId,
            ApiEndpointRef = model.ApiEndpointRef,
            EloHistory = history.Select(h => new EloPointDto
            {
                At = h.RecordedAt,
                Elo = Math.Round(h.EloAfter, 1),
                Shift = Math.Round(h.EloShift, 1),
                Outcome = h.Outcome,
                OpponentModelId = h.OpponentModelId,
                OpponentName = opponentNames.GetValueOrDefault(
                    h.OpponentModelId,
                    ModelDisplayName.ResolveForDisplay(null, null, h.OpponentModelId)),
                DuelId = h.DuelId,
            }).ToList(),
            KillList = killList,
            WinningOutputs = await BuildGalleryAsync(modelId, history, opponentNames),
            Highlights = ProfileHighlights.Compute(history, id => opponentNames.GetValueOrDefault(
                id,
                ModelDisplayName.ResolveForDisplay(null, null, id))),
        };
    }

    /// <summary>
    /// Display names for every opponent in the history, from one catalog read. A retired model
    /// is absent from the catalog and falls through to the shared placeholder, exactly as the
    /// kill list resolves it.
    /// </summary>
    private async Task<Dictionary<ModelId, string>> ResolveOpponentNamesAsync(List<EloRecord> history)
    {
        if (history.Count == 0) return [];

        var all = await modelRepository.GetAllAsync();
        return all.ToDictionary(m => m.ModelId, m => m.DisplayName);
    }

    /// <summary>
    /// The most recent wins, with the artifact each was won with.
    /// </summary>
    /// <remarks>
    /// Sourced from the ELO history rather than by scanning duels, because history rows are
    /// already partitioned by model and carry the outcome — finding this model's wins any other
    /// way means reading every duel it ever appeared in and discarding most of them. A result
    /// that failed or came back empty is skipped: the gallery renders documents, and a blank
    /// tile is worse than a shorter grid.
    /// </remarks>
    private async Task<IReadOnlyList<WinningOutputDto>> BuildGalleryAsync(
        ModelId modelId,
        List<EloRecord> history,
        Dictionary<ModelId, string> opponentNames)
    {
        // Over-fetch, because some candidates will be dropped below for having no renderable
        // output and we would rather return a full grid than a short one.
        var candidates = history
            .Where(h => string.Equals(h.Outcome, "Win", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(h => h.RecordedAt)
            .Take(ModelProfileDto.GalleryLimit * 2)
            .ToList();

        if (candidates.Count == 0) return [];

        var items = await StorageConcurrency.ReadAllAsync(candidates.Count, async index =>
        {
            var record = candidates[index];

            var duelTask = duelRepository.GetByIdAsync(record.DuelId);
            var result = await duelResultRepository.GetAsync(record.DuelId, modelId);
            var duel = await duelTask;

            if (duel is null || result is null || result.IsFailure) return null;
            if (string.IsNullOrWhiteSpace(result.HtmlOutputRaw)) return null;

            return new WinningOutputDto
            {
                DuelId = record.DuelId,
                PromptSummary = duel.PromptText,
                OpponentName = opponentNames.GetValueOrDefault(
                    record.OpponentModelId,
                    ModelDisplayName.ResolveForDisplay(null, null, record.OpponentModelId)),
                WonAt = record.RecordedAt,
                HtmlOutputRaw = result.HtmlOutputRaw,
            };
        });

        return items
            .Where(i => i is not null)
            .Select(i => i!)
            .OrderByDescending(i => i.WonAt)
            .Take(ModelProfileDto.GalleryLimit)
            .ToList();
    }
}
