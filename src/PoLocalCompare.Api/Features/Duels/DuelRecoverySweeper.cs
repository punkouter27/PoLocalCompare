using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

/// <summary>Source-generated log messages for the startup recovery sweep.</summary>
internal static partial class DuelRecoveryLog
{
    [LoggerMessage(EventId = 1120, Level = LogLevel.Information,
        Message = "Recovery sweep: re-judging duel {DuelId} whose execution outlived the process (results complete).")]
    public static partial void ReJudging(ILogger logger, DuelId duelId);

    [LoggerMessage(EventId = 1121, Level = LogLevel.Information,
        Message = "Recovery sweep: voided duel {DuelId} — {Reason}.")]
    public static partial void Voided(ILogger logger, DuelId duelId, string reason);

    [LoggerMessage(EventId = 1122, Level = LogLevel.Warning,
        Message = "Recovery sweep: could not settle duel {DuelId}.")]
    public static partial void SettleFailed(ILogger logger, Exception ex, DuelId duelId);
}

/// <summary>
/// Settles duels the process died in the middle of.
/// </summary>
/// <remarks>
/// Everything a duel needs to finish — the background queue slot, the hub groups, the in-memory
/// inference tasks — dies with the process. Nothing used to look at the leftovers: a duel that
/// was mid-flight at shutdown stayed <see cref="DuelVerdict.Pending"/> forever, the Arena showed
/// it with live winner buttons (and — before the verdict guard existed — accepted votes on it),
/// and the Archive listed it as Pending until the storage was wiped.
///
/// Two shapes of leftover exist, and each gets the honest ending:
///
/// • Both result rows present — execution actually finished, only the judge (or the completion
///   write) was cut off. The completion timestamp is stamped and the auto-judge is run inline,
///   which resolves the duel exactly as it would have before the restart: walkover, tie, judge
///   decision, or void.
///
/// • Any result row missing — inference never finished and nothing will ever finish it. The
///   duel is voided: terminal, banks nothing, drops out of every awaiting-judgment projection.
///
/// Runs at startup in every environment — an App Service restart is the production case, and it
/// is exactly the case where duels are most likely to have been mid-flight (deployments).
/// Scoped to the current and previous month partitions: a duel older than that predates any
/// plausible restart and has already been settled by an earlier sweep.
/// </remarks>
public sealed class DuelRecoverySweeper(
    IServiceScopeFactory scopeFactory,
    ILogger<DuelRecoverySweeper> logger)
{
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var duelRepository = scope.ServiceProvider.GetRequiredService<IDuelRepository>();
        var duelResultRepository = scope.ServiceProvider.GetRequiredService<IDuelResultRepository>();
        var autoJudge = scope.ServiceProvider.GetRequiredService<AutoJudge>();
        var recordVerdict = scope.ServiceProvider.GetRequiredService<RecordVerdictHandler>();

        var thisMonth = DateTimeOffset.UtcNow.ToString("yyyyMM");
        var lastMonth = DateTimeOffset.UtcNow.AddMonths(-1).ToString("yyyyMM");

        // `beforeMonth` is `PartitionKey le`, so the two windows overlap — everything from last
        // month also appears in the this-month query. Dedupe by id or the same orphan gets
        // settled twice (the second pass would 409 against the first one's verdict — harmless,
        // but it would spam the log and read as a sweep failure).
        var orphans = (await duelRepository.ListAsync(100, thisMonth))
            .Concat(await duelRepository.ListAsync(100, lastMonth))
            .Where(d => d.Verdict == DuelVerdict.Pending)
            .GroupBy(d => d.DuelId)
            .Select(g => g.First())
            .ToList();

        foreach (var duel in orphans)
        {
            try
            {
                var results = await duelResultRepository.GetByDuelIdAsync(duel.DuelId);
                var left = results.FirstOrDefault(r => r.ModelId == duel.LeftModelId);
                var right = results.FirstOrDefault(r => r.ModelId == duel.RightModelId);

                if (left is null || right is null)
                {
                    await recordVerdict.HandleAsync(new RecordVerdictCommand(
                        duel.DuelId,
                        DuelVerdict.Voided,
                        VerdictSource.Constraint,
                        "Abandoned before every model reported — the application restarted mid-duel."));
                    DuelRecoveryLog.Voided(logger, duel.DuelId,
                        $"missing result row for '{(left is null ? duel.LeftModelId : duel.RightModelId)}'");
                    continue;
                }

                // Results are complete: let the ordinary judge finish what was interrupted.
                // RunAsync is never-throwing and re-checks Pending itself, so a verdict that
                // somehow landed between the read above and here is respected, not overwritten.
                duel.CompletedAt ??= DateTimeOffset.UtcNow;
                await duelRepository.UpdateAsync(duel);
                DuelRecoveryLog.ReJudging(logger, duel.DuelId);
                await autoJudge.RunAsync(duel.DuelId, cancellationToken, delaySecondsOverride: 0);
            }
            catch (Exception ex)
            {
                // One unreadable duel must not stop the sweep — the rest are equally stranded.
                DuelRecoveryLog.SettleFailed(logger, ex, duel.DuelId);
            }
        }
    }
}
