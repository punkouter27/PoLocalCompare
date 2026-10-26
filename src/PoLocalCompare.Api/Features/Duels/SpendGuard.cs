using Microsoft.Extensions.Caching.Hybrid;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

/// <summary>
/// The daily ceiling on paid inference. <c>Limits:DailySpendCapUsd</c> is compared against the
/// sum of today's (UTC) <c>ApiCostUsd</c>; 0 or absent switches the cap off.
/// </summary>
/// <remarks>
/// Checked where spend is *started* — <c>POST /api/duels</c> and <c>POST /api/tournaments</c> —
/// not inside <see cref="CommenceDuelHandler"/>, which <c>TournamentRunner</c> also calls: a
/// bracket refused mid-run is Abandoned, and seven duels is a bounded overshoot. The total is
/// read from storage rather than counted in memory because the Free-tier App Service unloads
/// when idle, and a counter that resets on every cold start is not a cap.
///
/// Three sources: contestants (<c>ApiCostUsd</c>), the judge (<c>JudgeCostUsd</c>, its checklist
/// included) and the <see cref="SpendLedger"/> for calls with no duel row. One known gap, and it
/// is deliberate: the total is cached for the default 30 s, so duels started inside that window
/// after the cap is crossed still run.
/// </remarks>
public sealed class SpendGuard(
    IDuelResultRepository results,
    IDuelRepository duels,
    IModelRepository models,
    SpendLedger ledger,
    HybridCache cache,
    IConfiguration configuration)
{
    public const string Message =
        "Today's API budget is spent, so remote models are paused until midnight UTC. Browser and Ollama models still run.";

    public async Task<bool> IsOverBudgetAsync(CancellationToken cancellationToken = default)
    {
        var cap = configuration.GetValue("Limits:DailySpendCapUsd", 0.0);
        if (cap <= 0) return false;

        var day = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var spent = await cache.GetOrCreateAsync(
            $"spend:{day:yyyyMMdd}",
            // Contestants and the judge. The judge's calls were not counted until
            // 2026-10-11 — two per duel, on the vision deployment when screenshots are on.
            async _ => await results.SumApiCostAsync(day, day.AddDays(1))
                + await duels.SumJudgeCostAsync(day, day.AddDays(1))
                + await ledger.SumAsync(day),
            cancellationToken: cancellationToken);

        return spent >= cap;
    }

    /// <summary>True when the budget is spent and at least one of these models bills per token.</summary>
    public async Task<bool> BlocksAsync(ModelId left, ModelId right, CancellationToken cancellationToken = default)
    {
        if (!await IsOverBudgetAsync(cancellationToken)) return false;

        return (await models.GetByIdAsync(left))?.ModelType == ModelType.Remote
            || (await models.GetByIdAsync(right))?.ModelType == ModelType.Remote;
    }

    public static IResult Refusal() => Results.Problem(
        title: "Daily budget reached",
        detail: Message,
        statusCode: StatusCodes.Status429TooManyRequests);
}
