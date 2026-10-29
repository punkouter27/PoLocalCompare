using PoLocalCompare.Api.Common.Background;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Tournaments;

/// <summary>
/// Plays a bracket from the draw to the final, one match at a time.
/// </summary>
/// <remarks>
/// Server-side rather than driven from the page: a bracket is eight models and up to seven
/// duels, and it has to survive the tab being closed. The runner owns no state — every step re-reads the tournament and writes the
/// result back — so a process restart mid-bracket resumes rather than losing the run, and
/// <see cref="Tournament.RecordWinner"/> refusing to decide an already-decided match makes a
/// replayed step a no-op rather than a double advance.
///
/// Matches are run strictly in sequence. Round 1 could in principle run in parallel, but the
/// duel queue is shared with everything else the app is doing and eight concurrent inference
/// jobs against one Foundry deployment is how you find its rate limit rather than a winner.
/// </remarks>
public sealed class TournamentRunner(
    IServiceScopeFactory scopeFactory,
    IBackgroundTaskQueue taskQueue,
    ILogger<TournamentRunner> logger)
{
    /// <summary>
    /// Bracket matches are judged with no grace window: nobody is voting on a run designed to
    /// proceed on its own, and a per-match human countdown would stall the whole bracket on an
    /// unattended tab.
    /// </summary>
    private const int AutoJudgeDelaySeconds = 0;

    /// <summary>
    /// How many independent matches may be in flight at once.
    /// </summary>
    /// <remarks>
    /// Two, not four. Each match is a duel, and each duel calls two models — so a batch of two
    /// is already four concurrent inference streams plus their judges. Fanning out a whole
    /// round of four would be eight, which on a shared Foundry resource finds the rate limit
    /// rather than the finish line, and a 429 mid-bracket abandons the run. Two roughly halves
    /// the wall-clock of an 8-model bracket while staying well inside the quota.
    /// </remarks>
    private const int MaxConcurrentMatches = 2;

    /// <summary>How long a single match may take before the run gives up on it.</summary>
    /// <remarks>
    /// Must sit ABOVE the duel execution watchdog (Duel:TimeLimitSeconds, 900 s): the duel's own
    /// watchdog is what converts a stalled model into a failure row + walkover, which this
    /// runner then folds into the bracket. At the previous 12-minute match timeout the two
    /// windows crossed — a duel legitimately finishing between 12 and 15 minutes got its
    /// bracket Abandoned while the duel itself sailed on to judge and move ELO with nobody home
    /// to receive the result.
    /// </remarks>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMinutes(16);

    /// <summary>Gap between verdict polls while a match is running.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Runs the bracket to completion. Every failure path ends with the tournament marked
    /// Complete or Abandoned — a run that stops silently would leave the page spinning forever.
    /// </summary>
    /// <remarks>
    /// Yields the queue slot between batches so a sibling tournament can start its own batch
    /// while this one is waiting on verdicts. <see cref="BackgroundTaskService"/> is a single
    /// consumer that awaits each work item before dequeuing the next; without yielding, a
    /// bracket's verdict-polling loop held the slot for the entire bracket and a fresh create
    /// looked stuck on "Starting the next match…" from the page. Now the runner kicks off a
    /// batch, enqueues <em>itself</em> to apply the results, and returns — a new tournament
    /// created between two batches can start in the slot this one just freed.
    /// </remarks>
    public async Task RunAsync(TournamentId tournamentId, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<TournamentRepository>();

            var tournament = await repository.GetByIdAsync(tournamentId);
            if (tournament is null)
            {
                logger.LogWarning("Tournament {TournamentId} vanished mid-run.", tournamentId);
                return;
            }

            if (tournament.Status is TournamentStatus.Complete or TournamentStatus.Abandoned)
                return;

            // Matches playable at the same moment are independent of one another — round
            // 1's quarter-finals share no state — so several run at once instead of each
            // waiting out the one before it. An 8-model bracket was 7 strictly serial
            // matches; this makes the first round finish in roughly the time of its
            // slowest match rather than the sum of all four.
            var batch = tournament.AllPlayable().Take(MaxConcurrentMatches).ToList();
            if (batch.Count == 0)
            {
                // Nothing playable and no champion: a match failed and the bracket cannot be
                // seeded any further. Bracket rounds depend on each other, so there is no
                // way to skip past it. A Swiss round is paired by the aggregate the moment
                // the round before it is fully decided, so the same holds there.
                tournament.Abandon("A match could not be decided, so the bracket could not be completed.");
                await repository.UpdateAsync(tournament);
                return;
            }

            await PlayBatchAsync(scope.ServiceProvider, repository, tournament, batch, cancellationToken);

            // Yield the queue slot: enqueue the next step so a sibling tournament can dequeue
            // and start while this one's batch finishes. The tournament is still ours — the
            // re-read at the top of the next call picks up where we left off — but the
            // single-consumer worker is free.
            taskQueue.QueueBackgroundWork(ct => RunAsync(tournamentId, ct));
        }
        catch (OperationCanceledException)
        {
            // Host shutting down. The tournament stays as it is on disk and resumes if re-run.
            logger.LogInformation("Tournament {TournamentId} run cancelled.", tournamentId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tournament {TournamentId} run failed.", tournamentId);
            await MarkAbandonedAsync(tournamentId, $"The run failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Plays a set of independent matches: start them all, wait for all of them, then apply
    /// the results.
    /// </summary>
    /// <remarks>
    /// The phase split is what makes concurrency safe here. Every match in the batch mutates
    /// the same <see cref="Tournament"/> aggregate, and Table Storage writes are ETag-guarded,
    /// so running whole matches in parallel would have them clobber each other's saves. Instead
    /// only the *waiting* overlaps — which is where all the time goes, since each match is a
    /// full duel plus a judge — while every write stays on this one sequential path.
    /// </remarks>
    private async Task PlayBatchAsync(
        IServiceProvider services,
        TournamentRepository repository,
        Tournament tournament,
        IReadOnlyList<TournamentMatch> batch,
        CancellationToken cancellationToken)
    {
        var duelRepository = services.GetRequiredService<IDuelRepository>();

        // ── Phase 1: start each match. Serial, because each one saves the aggregate. ──
        foreach (var match in batch)
            await StartMatchAsync(services, repository, tournament, match, cancellationToken);

        // ── Phase 2: wait for all of them at once. No writes happen in here. ──
        var duels = await Task.WhenAll(batch.Select(m =>
            WaitForVerdictAsync(duelRepository, m.DuelId!.Value, cancellationToken)));

        // ── Phase 3: apply results in bracket order, then save once. ──
        for (var i = 0; i < batch.Count; i++)
        {
            if (!ApplyResult(tournament, batch[i], duels[i])) break;   // bracket abandoned
        }

        await repository.UpdateAsync(tournament);
    }

    /// <summary>Creates the duel behind a match and runs it inline, if it does not already have one.</summary>
    /// <remarks>
    /// The duel is executed on a thread-pool task (<see cref="DuelExecutionService.RunAsync"/>)
    /// rather than via <c>EnqueueAsync</c>: <c>BackgroundTaskService</c> is single-consumer and
    /// awaits each work item before dequeuing the next, and the runner is itself holding that
    /// slot, so a re-queued duel would sit behind its own runner forever. The runner caps
    /// concurrency at <see cref="MaxConcurrentMatches"/> so the lack of a separate queue is
    /// intentional, not an oversight.
    /// </remarks>
    /// <param name="cancellationToken">
    /// Host/queue shutdown signal — fans into the detached duel so a host restart cancels
    /// in-flight matches instead of leaking them until the duel's own 15-minute watchdog trips.
    /// </param>
    private async Task StartMatchAsync(
        IServiceProvider services,
        TournamentRepository repository,
        Tournament tournament,
        TournamentMatch match,
        CancellationToken cancellationToken)
    {
        // A tournament re-read after a restart can find a match that already has a duel — that
        // one is waited on rather than re-run.
        if (match.DuelId is null)
        {
            // Double-commence guard: the check-then-act below is only race-free while exactly
            // one runner exists per tournament per process. The startup resume path can
            // legitimately run alongside a runner a client just triggered, and without this
            // re-read both would see DuelId == null and commence two duels for one match.
            var fresh = await repository.GetByIdAsync(tournament.TournamentId);
            var freshMatch = fresh?.Matches.FirstOrDefault(m => m.Round == match.Round && m.Index == match.Index);
            if (freshMatch?.DuelId is not null)
                return;

            var commence = services.GetRequiredService<CommenceDuelHandler>();
            var execution = services.GetRequiredService<DuelExecutionService>();

            var dto = await commence.HandleAsync(new CommenceDuelCommand(
                match.SlotAModelId,
                match.SlotBModelId,
                tournament.PromptText,
                AutoJudgeDelaySeconds,
                tournament.OwnerId));

            match.DuelId = dto.DuelId;
            if (tournament.Status == TournamentStatus.Pending)
                tournament.Status = TournamentStatus.Running;

            await repository.UpdateAsync(tournament);

            // Fire-and-forget on the thread pool: PlayBatchAsync then awaits the match's
            // WaitForVerdictAsync by DuelId, so the completion signal we wait on is the
            // persisted verdict, not this Task. The cancellationToken fans into the duel so
            // a host shutdown also cancels in-flight inference.
            _ = Task.Run(async () =>
            {
                try
                {
                    await execution.RunAsync(dto.DuelId, AutoJudgeDelaySeconds, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Tournament {TournamentId} duel {DuelId} failed during execution.",
                        tournament.TournamentId, dto.DuelId);
                }
            }, cancellationToken);

            logger.LogInformation(
                "Tournament {TournamentId} round {Round} match {Index}: {Left} vs {Right} as duel {DuelId}.",
                tournament.TournamentId, match.Round, match.Index, match.SlotAName, match.SlotBName, dto.DuelId);
        }
    }

    /// <summary>
    /// Folds one finished duel into the bracket. Returns false when the bracket was abandoned
    /// and the rest of the batch should not be applied.
    /// </summary>
    private static bool ApplyResult(Tournament tournament, TournamentMatch match, Duel? duel)
    {
        if (duel is null)
        {
            match.FailureReason = "The match did not finish in time.";
            tournament.Abandon($"{match.SlotAName} vs {match.SlotBName} did not finish in time.");
            return false;
        }

        switch (duel.Verdict)
        {
            case DuelVerdict.Left or DuelVerdict.Right when duel.WinnerModelId is { } winnerId:
            {
                var winnerName = winnerId == match.SlotAModelId ? match.SlotAName : match.SlotBName;
                tournament.RecordWinner(match.Round, match.Index, winnerId, winnerName);
                break;
            }

            case DuelVerdict.Tie:
            {
                // Bracket: the better seed advances (see Tournament.SeedTieBreak for why that
                // beats a coin toss or an abandoned bracket). Swiss: half a point each.
                tournament.RecordTie(match.Round, match.Index);
                break;
            }

            case DuelVerdict.Voided:
            {
                // Terminal with no winner and no draw to seed-break on: both models failed or
                // the run was abandoned. The bracket cannot be seeded through it, so this is
                // the honest Abandoned — naming a champion here would invent an outcome.
                match.FailureReason = duel.JudgeRationale ?? "The duel was voided — no result existed to judge.";
                tournament.Abandon($"{match.SlotAName} vs {match.SlotBName} was voided before a result existed.");
                return false;
            }

            default:
            {
                // Still Pending after the wait: the judge stood down and there is no evidence
                // to advance on. The bracket stops rather than picking a winner.
                match.FailureReason = duel.JudgeStoodDownReason
                    ?? "The judge could not decide this match.";
                tournament.Abandon($"{match.SlotAName} vs {match.SlotBName} could not be judged.");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Polls the duel until it carries a terminal verdict. Returns null on timeout.
    /// </summary>
    /// <remarks>
    /// Polling rather than subscribing to <see cref="DuelHub"/>: the hub broadcasts to connected
    /// clients, and the point of running the bracket server-side is that there may be none. A
    /// tie is terminal and carries no winner id, so it has to pass this gate on the verdict alone.
    /// </remarks>
    private static async Task<Duel?> WaitForVerdictAsync(
        IDuelRepository duelRepository,
        DuelId duelId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + MatchTimeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var duel = await duelRepository.GetByIdAsync(duelId);
            if (duel is not null && duel.Verdict != DuelVerdict.Pending)
                return duel;

            await Task.Delay(PollInterval, cancellationToken);
        }

        return null;
    }

    private async Task MarkAbandonedAsync(TournamentId tournamentId, string reason)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<TournamentRepository>();

            var tournament = await repository.GetByIdAsync(tournamentId);
            if (tournament is null) return;

            tournament.Abandon(reason);
            await repository.UpdateAsync(tournament);
        }
        catch (Exception ex)
        {
            // Last-ditch: the run has already failed, and failing to record that must not
            // replace the original error in the log.
            logger.LogError(ex, "Could not mark tournament {TournamentId} abandoned.", tournamentId);
        }
    }
}
