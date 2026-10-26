// SOLID: Single Responsibility — verdict recording coordinates ELO + persistence only
using Azure;
using Microsoft.Extensions.Caching.Hybrid;
using PoLocalCompare.Api.Auth;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public sealed class RecordVerdictHandler
{
    private readonly IDuelRepository _duelRepository;
    private readonly IModelRepository _modelRepository;
    private readonly IEloHistoryRepository _eloHistoryRepository;
    private readonly IDuelResultRepository? _duelResultRepository;
    private readonly HybridCache? _cache;
    private readonly LobbyNotifier? _lobby;
    private readonly double _kFactor;

    /// <param name="cache">
    /// Optional so pure-logic unit tests can construct the handler without a cache.
    /// When supplied (always, under DI) the leaderboard is invalidated here rather than at
    /// the HTTP endpoint — <see cref="AutoJudge"/> calls this handler directly, bypassing the
    /// endpoint, and would otherwise leave a stale leaderboard behind.
    /// </param>
    /// <param name="lobby">
    /// Optional for the same reason. Announcing from here rather than from the two callers is
    /// what makes the activity ticker complete: this handler is the only path by which ELO
    /// moves, so a verdict that reaches storage cannot fail to reach the ticker.
    /// </param>
    /// <param name="duelResultRepository">
    /// Optional for the same reason as the two above. Supplied under DI, where it enforces the
    /// no-evidence rule described on <see cref="HandleAsync"/>.
    /// </param>
    public RecordVerdictHandler(
        IDuelRepository duelRepository,
        IModelRepository modelRepository,
        IEloHistoryRepository eloHistoryRepository,
        double kFactor = 32.0,
        HybridCache? cache = null,
        LobbyNotifier? lobby = null,
        IDuelResultRepository? duelResultRepository = null)
    {
        _duelRepository = duelRepository;
        _modelRepository = modelRepository;
        _eloHistoryRepository = eloHistoryRepository;
        _kFactor = kFactor;
        _cache = cache;
        _lobby = lobby;
        _duelResultRepository = duelResultRepository;
    }

    /// <summary>
    /// <see cref="HandleAsync"/>, retried once when it loses an optimistic-concurrency race.
    /// </summary>
    /// <remarks>
    /// ELO moves only through this handler but it now has two callers — the verdict endpoint and
    /// <see cref="AutoJudge"/> — which both have to survive a 412 the same way. Keeping the retry
    /// here means a third verdict writer inherits it instead of pasting the policy a third time.
    /// </remarks>
    public async Task<VerdictResponseDto?> HandleWithRetryAsync(RecordVerdictCommand command)
    {
        try
        {
            return await HandleAsync(command);
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            // Lost an optimistic-concurrency race (standards §5.5). The naive retry —
            // "HandleAsync re-reads everything, so one retry resolves against the fresh
            // state" — was wrong, and wrong in a way that corrupts ratings.
            //
            // HandleAsync updates the winner model, then the loser model, and only THEN the
            // duel. A 412 comes from that third write, by which point both models have
            // already banked a DuelCount (and the winner a WinCount, and both an ELO shift).
            // Re-running from the top increments all of it a second time. An integration test
            // that ran three duels saw a duelCount of six.
            //
            // The race is real and got more likely when AiJudge:DelaySeconds dropped from 60
            // to 10: DuelExecutionService stamps CompletedAt on the duel at almost the moment
            // a verdict is being written against it.
            //
            // So: re-read first. If a verdict actually landed, the work is done — report it
            // rather than doing it again. Only a genuinely still-Pending duel is retried.
            var settled = await _duelRepository.GetByIdAsync(command.DuelId);
            if (settled is not null && settled.Verdict != DuelVerdict.Pending)
                return DescribeAlreadyRecorded(settled);

            return await HandleAsync(command);
        }
    }

    /// <summary>
    /// Reports a verdict that is already on the duel, without touching ratings again.
    /// </summary>
    /// <remarks>
    /// Used only on the 412 path above. The counters and ELO were applied by the attempt that
    /// won the race, so this reconstructs the response from what was stored rather than
    /// recomputing anything.
    /// </remarks>
    private static VerdictResponseDto DescribeAlreadyRecorded(Duel duel) => new()
    {
        DuelId = duel.DuelId,
        Verdict = duel.Verdict,
        WinnerModelId = duel.WinnerModelId,
        LoserModelId = duel.LoserModelId,
        EloShiftWinner = duel.EloShiftWinner,
        EloShiftLoser = duel.EloShiftLoser,
        Source = duel.VerdictSource,
        JudgeRationale = duel.JudgeRationale,
        JudgeAnalysis = duel.JudgeAnalysis,
    };

    /// <summary>
    /// Returns null if duel not found.
    /// Throws <see cref="InvalidOperationException"/> if verdict already recorded (caller maps to 409).
    /// Throws <see cref="ArgumentException"/> if verdict is Pending or Expired (caller maps to 422).
    /// </summary>
    public async Task<VerdictResponseDto?> HandleAsync(RecordVerdictCommand command)
    {
        if (command.Verdict == DuelVerdict.Pending)
            throw new ArgumentException("Verdict cannot be Pending.", nameof(command));

        var duel = await _duelRepository.GetByIdAsync(command.DuelId);
        if (duel is null) return null;

        if (duel.Verdict != DuelVerdict.Pending)
            throw new InvalidOperationException("Verdict has already been recorded for this duel.");

        // A void is the no-evidence terminal, so it dispatches before the guard below — the
        // guard exists to stop ELO moving on missing evidence, and a void moves nothing.
        if (command.Verdict == DuelVerdict.Voided)
            return await RecordVoidedAsync(command, duel);

        await GuardAgainstNoEvidenceAsync(duel);

        var leftModel = await _modelRepository.GetByIdAsync(duel.LeftModelId)
            ?? throw new KeyNotFoundException($"Model '{duel.LeftModelId}' not found.");
        var rightModel = await _modelRepository.GetByIdAsync(duel.RightModelId)
            ?? throw new KeyNotFoundException($"Model '{duel.RightModelId}' not found.");

        if (command.Verdict == DuelVerdict.Tie)
            return await RecordTieAsync(command, duel, leftModel, rightModel);

        // Determine winner/loser based on verdict (Left or Right)
        Model winner;
        Model loser;

        if (command.Verdict == DuelVerdict.Left)
        {
            winner = leftModel;
            loser = rightModel;
        }
        else
        {
            winner = rightModel;
            loser = leftModel;
        }

        var winnerEloBefore = winner.CurrentElo;
        var loserEloBefore = loser.CurrentElo;

        // Winner = outcome 1.0, Loser = outcome 0.0
        var (newWinnerElo, newLoserElo) = EloCalculator.Calculate(
            winnerEloBefore,
            loserEloBefore,
            _kFactor,
            outcomeA: 1.0);

        var eloShiftWinner = Math.Round(newWinnerElo - winnerEloBefore, 1);
        var eloShiftLoser = Math.Round(newLoserElo - loserEloBefore, 1);

        // ── Write order matters, and this order is the fix for a real corruption bug ──
        //
        // The duel goes FIRST, before either model. It is the idempotency guard: a duel accepts
        // exactly one verdict, so once it is written, any second pass through this method hits
        // the "already recorded" check at the top and stops.
        //
        // It used to be last, after both model updates. That meant an optimistic-concurrency
        // 412 on either model write left one model already incremented, and the retry in
        // HandleWithRetryAsync re-ran the whole method and incremented it a second time. The
        // symptom was subtle because EloHistoryRepository.SaveAsync swallows a 409 as an
        // idempotent append: history stayed correct while DuelCount and WinCount silently
        // doubled. An integration test running three duels measured duelCount=6, winCount=4,
        // eloHistoryRows=3 — the history column is what gives the mechanism away.
        //
        // The residual risk is the mirror image and is deliberately preferred: if a model write
        // fails after the duel is written, that model's rating does not move. That is visible
        // (the duel names a winner whose rating did not change), it is recoverable — the
        // remapper's recompute-from-history rebuilds aggregates — and it under-reports rather
        // than inventing rating that was never earned. A doubled rating is silent and permanent.
        duel.Verdict = command.Verdict;
        duel.VerdictSource = command.Source;
        duel.JudgeRationale = command.JudgeRationale;
        duel.JudgeAnalysis = command.JudgeAnalysis;
        duel.JudgeCostUsd = command.JudgeCostUsd;
        duel.JudgeModel = command.JudgeModel;
        duel.WinnerModelId = winner.ModelId;
        duel.LoserModelId = loser.ModelId;
        duel.EloShiftWinner = eloShiftWinner;
        duel.EloShiftLoser = eloShiftLoser;
        duel.CompletedAt ??= DateTimeOffset.UtcNow;
        // For Human verdicts, record who clicked. The AI judge passes null Actor and its
        // decision is then attributed via JudgeModel rather than VerdictBy.
        if (command.Source == VerdictSource.Human)
            duel.VerdictBy = command.Actor ?? IdentityResolver.AnonymousActor;
        await _duelRepository.UpdateAsync(duel);

        // Now the aggregates. Reaching here means the duel is terminal and this is the only
        // pass that will ever apply them.
        winner.CurrentElo = newWinnerElo;
        winner.DuelCount++;
        winner.WinCount++;
        await _modelRepository.UpdateAsync(winner);

        loser.CurrentElo = newLoserElo;
        loser.DuelCount++;
        await _modelRepository.UpdateAsync(loser);

        // Persist EloRecord snapshots for both models
        var winnerRecord = new EloRecord(
            winner.ModelId,
            command.DuelId,
            eloAfter: newWinnerElo,
            eloBefore: winnerEloBefore,
            outcome: "Win",
            opponentModelId: loser.ModelId,
            opponentEloBefore: loserEloBefore);

        var loserRecord = new EloRecord(
            loser.ModelId,
            command.DuelId,
            eloAfter: newLoserElo,
            eloBefore: loserEloBefore,
            outcome: "Loss",
            opponentModelId: winner.ModelId,
            opponentEloBefore: winnerEloBefore);

        await _eloHistoryRepository.SaveAsync(winnerRecord);
        await _eloHistoryRepository.SaveAsync(loserRecord);

        // ELO moved ⇒ every cached leaderboard projection is stale.
        if (_cache is not null)
            await _cache.RemoveByTagAsync(CacheTags.Leaderboard);

        var response = new VerdictResponseDto
        {
            DuelId = command.DuelId,
            Verdict = command.Verdict,
            WinnerModelId = winner.ModelId,
            LoserModelId = loser.ModelId,
            EloShiftWinner = eloShiftWinner,
            EloShiftLoser = eloShiftLoser,
            WinnerEloAfter = newWinnerElo,
            LoserEloAfter = newLoserElo,
            Source = command.Source,
            JudgeRationale = command.JudgeRationale,
            JudgeAnalysis = command.JudgeAnalysis,
        };

        // Announced last, after everything durable has landed. LobbyNotifier swallows its own
        // failures, so a broken ticker cannot undo a recorded verdict.
        if (_lobby is not null)
        {
            await _lobby.VerdictRecordedAsync(
                duel, leftModel.DisplayName, rightModel.DisplayName, winner.DisplayName, response);
        }

        return response;
    }

    /// <summary>
    /// Records a judged draw: both models bank a duel and a draw, neither rating moves.
    /// </summary>
    /// <remarks>
    /// A tie is evidence — the judge read both outputs and found them equivalent — so it is a
    /// terminal verdict rather than a return to Pending. It still moves no ELO, which keeps the
    /// "ratings never move on no evidence" invariant intact from the other direction: equal
    /// evidence is not a reason to separate two models. History rows are written for both sides
    /// with <c>eloAfter == eloBefore</c> so the sparkline shows a flat step rather than a gap,
    /// and so the head-to-head record can count the meeting at all.
    /// </remarks>
    private async Task<VerdictResponseDto?> RecordTieAsync(
        RecordVerdictCommand command,
        Duel duel,
        Model leftModel,
        Model rightModel)
    {
        var leftElo = leftModel.CurrentElo;
        var rightElo = rightModel.CurrentElo;

        // Duel first, for the same reason as the decisive path above: it is the idempotency
        // guard, and writing the model rows ahead of it meant a 412 on either one left a
        // partial increment that the retry then applied a second time. A tie touches
        // DuelCount and DrawCount but not ELO, so the corruption showed up purely as inflated
        // counts — one duel short of a doubling per tie, which is exactly how it was found.
        //
        // No winner, no loser, no shift. The nullable winner/loser fields on the duel stay null
        // so nothing downstream can mistake one side for having won.
        duel.Verdict = DuelVerdict.Tie;
        duel.VerdictSource = command.Source;
        duel.JudgeRationale = command.JudgeRationale;
        duel.JudgeAnalysis = command.JudgeAnalysis;
        duel.JudgeCostUsd = command.JudgeCostUsd;
        duel.JudgeModel = command.JudgeModel;
        duel.WinnerModelId = null;
        duel.LoserModelId = null;
        duel.EloShiftWinner = 0;
        duel.EloShiftLoser = 0;
        duel.CompletedAt ??= DateTimeOffset.UtcNow;
        if (command.Source == VerdictSource.Human)
            duel.VerdictBy = command.Actor ?? IdentityResolver.AnonymousActor;
        await _duelRepository.UpdateAsync(duel);

        // Aggregates after the guard. Both models bank a duel and a draw; neither rating moves.
        leftModel.DuelCount++;
        leftModel.DrawCount++;
        await _modelRepository.UpdateAsync(leftModel);

        rightModel.DuelCount++;
        rightModel.DrawCount++;
        await _modelRepository.UpdateAsync(rightModel);

        await _eloHistoryRepository.SaveAsync(new EloRecord(
            leftModel.ModelId, command.DuelId,
            eloAfter: leftElo, eloBefore: leftElo,
            outcome: "Draw",
            opponentModelId: rightModel.ModelId,
            opponentEloBefore: rightElo));

        await _eloHistoryRepository.SaveAsync(new EloRecord(
            rightModel.ModelId, command.DuelId,
            eloAfter: rightElo, eloBefore: rightElo,
            outcome: "Draw",
            opponentModelId: leftModel.ModelId,
            opponentEloBefore: leftElo));

        // Duel counts and the W/L column changed even though ELO did not, so the cached
        // leaderboard projections are stale exactly as they are for a decisive verdict.
        if (_cache is not null)
            await _cache.RemoveByTagAsync(CacheTags.Leaderboard);

        var response = new VerdictResponseDto
        {
            DuelId = command.DuelId,
            Verdict = DuelVerdict.Tie,
            WinnerModelId = null,
            LoserModelId = null,
            EloShiftWinner = 0,
            EloShiftLoser = 0,
            WinnerEloAfter = null,
            LoserEloAfter = null,
            Source = command.Source,
            JudgeRationale = command.JudgeRationale,
            JudgeAnalysis = command.JudgeAnalysis,
        };

        if (_lobby is not null)
        {
            await _lobby.VerdictRecordedAsync(
                duel, leftModel.DisplayName, rightModel.DisplayName, winnerModelName: null, response);
        }

        return response;
    }

    /// <summary>
    /// Refuses a verdict that could move ELO without complete evidence.
    /// </summary>
    /// <remarks>
    /// Two refusal rules, both enforced here because this handler is the single gate every
    /// ELO-moving write passes through:
    ///
    /// 1. Both results exist and both are failures — nothing to compare. (The auto-judge voids
    /// this shape instead of ever reaching here; the guard remains for direct endpoint writers.)
    ///
    /// 2. A result row is missing entirely. This is the second half of a hole found live: a duel
    /// whose slow model was still generating showed winner buttons, and a vote WAS accepted with
    /// <c>CompletedAt</c> null and one result missing — ELO moved on half a duel. The endpoint
    /// used to check neither. The only legitimate one-result shape is the walkover (one result
    /// + one failure row), which still passes because both rows exist.
    /// </remarks>
    private async Task GuardAgainstNoEvidenceAsync(Duel duel)
    {
        if (_duelResultRepository is null) return;

        var results = await _duelResultRepository.GetByDuelIdAsync(duel.DuelId);
        var left = results.FirstOrDefault(r => r.ModelId == duel.LeftModelId);
        var right = results.FirstOrDefault(r => r.ModelId == duel.RightModelId);

        if (left is null || right is null)
        {
            throw new InvalidOperationException(
                "This duel is still running — a model has not reported a result yet.");
        }

        if (left.IsFailure && right.IsFailure)
        {
            throw new ArgumentException(
                "Both models failed, so there is nothing to judge. Retry the duel instead.",
                nameof(duel));
        }
    }

    /// <summary>
    /// Terminalises a duel that can never be judged: both models failed, or the run was
    /// abandoned before every model reported (startup recovery).
    /// </summary>
    /// <remarks>
    /// Banks nothing — no duel count, no draw, no ELO, no history rows — because there is no
    /// evidence to bank. The duel-first write order still applies: <c>Verdict != Pending</c> is
    /// the idempotency guard every other path checks, so a void racing a human vote resolves
    /// exactly like two competing verdicts (first write wins, loser gets 409).
    /// </remarks>
    private async Task<VerdictResponseDto> RecordVoidedAsync(RecordVerdictCommand command, Duel duel)
    {
        duel.Verdict = DuelVerdict.Voided;
        duel.VerdictSource = command.Source;
        duel.JudgeRationale = command.JudgeRationale;
        duel.JudgeAnalysis = command.JudgeAnalysis;
        duel.JudgeCostUsd = command.JudgeCostUsd;
        duel.JudgeModel = null;
        duel.WinnerModelId = null;
        duel.LoserModelId = null;
        duel.EloShiftWinner = 0;
        duel.EloShiftLoser = 0;
        duel.CompletedAt ??= DateTimeOffset.UtcNow;
        await _duelRepository.UpdateAsync(duel);

        var response = new VerdictResponseDto
        {
            DuelId = duel.DuelId,
            Verdict = DuelVerdict.Voided,
            WinnerModelId = null,
            LoserModelId = null,
            EloShiftWinner = 0,
            EloShiftLoser = 0,
            Source = command.Source,
            JudgeRationale = command.JudgeRationale,
            JudgeAnalysis = command.JudgeAnalysis,
        };

        if (_lobby is not null)
        {
            var leftModel = await _modelRepository.GetByIdAsync(duel.LeftModelId);
            var rightModel = await _modelRepository.GetByIdAsync(duel.RightModelId);
            await _lobby.VerdictRecordedAsync(
                duel,
                leftModel?.DisplayName ?? duel.LeftModelId.ToString(),
                rightModel?.DisplayName ?? duel.RightModelId.ToString(),
                winnerModelName: null,
                response);
        }

        return response;
    }
}