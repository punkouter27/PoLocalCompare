using System.Collections.Concurrent;
using Azure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PoLocalCompare.Api.Common.Background;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Judging;

internal static partial class AutoJudgeLog
{
    [LoggerMessage(EventId = 1210, Level = LogLevel.Information,
        Message = "Auto-judge stood down for duel {DuelId}: {Reason}")]
    public static partial void StoodDown(ILogger logger, DuelId duelId, string reason);

    [LoggerMessage(EventId = 1211, Level = LogLevel.Information,
        Message = "Auto-judge recorded {Verdict} for duel {DuelId}.")]
    public static partial void Recorded(ILogger logger, DuelId duelId, DuelVerdict verdict);

    [LoggerMessage(EventId = 1212, Level = LogLevel.Error, Message = "Auto-judge failed for duel {DuelId}.")]
    public static partial void Failed(ILogger logger, Exception ex, DuelId duelId);

    [LoggerMessage(EventId = 1213, Level = LogLevel.Warning,
        Message = "Auto-judge rate-limited for duel {DuelId}, re-queuing in {DelaySeconds:F0}s (attempt {Attempt}/{Max}).")]
    public static partial void RateLimited(ILogger logger, DuelId duelId, double delaySeconds, int attempt, int max);
}

/// <summary>
/// Decides a duel that no human judged within the grace window.
/// </summary>
/// <remarks>
/// This reverses the original human-only-verdict rule (PRD §9 item 7) and is why every verdict
/// now carries a <see cref="VerdictSource"/>. Four invariants keep it honest:
/// a human who picks inside the window always wins the race; a judge that cannot reach the LLM
/// (unreachable, unparseable) leaves the duel Pending rather than guessing — ELO never moves on
/// no evidence; a one-sided execution failure is awarded to the survivor as a walkover (item 20,
/// one side failed — the survivor IS the evidence), stamped <see cref="VerdictSource.Constraint"/>
/// because no model ever read the outputs; and when BOTH sides failed there is nothing to act
/// on at all, so the duel is voided — terminal, banks nothing, and stops haunting the lobby's
/// "awaiting judgment" counter forever.
///
/// A fifth behaviour complements those: a judge that *temporarily* cannot reach the model —
/// a 429 with a <c>Retry-After</c> header, mostly — is re-scheduled after the requested delay
/// on a detached timer rather than stood down, so a Foundry rate-limit burst does not silently
/// turn a long unattended run into a one-judge-recorded one.
/// </remarks>
public sealed class AutoJudge
{
    private readonly IDuelRepository _duelRepository;
    private readonly IDuelResultRepository _duelResultRepository;
    private readonly RecordVerdictHandler _recordVerdict;
    private readonly IDuelJudge _judge;
    private readonly IHubContext<DuelHub> _hubContext;
    private readonly AutoJudgeOptions _options;
    private readonly IBackgroundTaskQueue _backgroundQueue;
    private readonly ILogger<AutoJudge> _logger;

    /// <summary>
    /// Per-duel re-queue attempt counter. Process-wide because <see cref="AutoJudge"/> is
    /// scoped per call, but the lifetime we care about (a finished duel) is bounded by the
    /// verdict deadline (24 h default). The eviction is opportunistic — the entries are
    /// tiny integers — and the worst case is a future duel arriving with a duplicate id, which
    /// is impossible since <see cref="DuelId"/> is a ULID.
    /// </summary>
    private static readonly ConcurrentDictionary<DuelId, int> RateLimitAttempts = new();

    public AutoJudge(
        IDuelRepository duelRepository,
        IDuelResultRepository duelResultRepository,
        RecordVerdictHandler recordVerdict,
        IDuelJudge judge,
        IHubContext<DuelHub> hubContext,
        IOptions<AutoJudgeOptions> options,
        IBackgroundTaskQueue backgroundQueue,
        ILogger<AutoJudge> logger)
    {
        _duelRepository = duelRepository;
        _duelResultRepository = duelResultRepository;
        _recordVerdict = recordVerdict;
        _judge = judge;
        _hubContext = hubContext;
        _options = options.Value;
        _backgroundQueue = backgroundQueue;
        _logger = logger;
    }

    /// <summary>
    /// Waits out the grace window, then decides the duel if it is still unjudged. Never throws —
    /// a failed auto-judge must leave the duel judgeable by hand, not break duel execution.
    /// </summary>
    /// <param name="delaySecondsOverride">
    /// Replaces <see cref="AutoJudgeOptions.DelaySeconds"/> for this duel. A tournament passes 0.
    /// Note this cannot enable the judge: <see cref="AutoJudgeOptions.Enabled"/> is still the
    /// master switch, so <c>AiJudge:Enabled=false</c> genuinely restores human-only verdicts.
    /// </param>
    public async Task RunAsync(
        DuelId duelId,
        CancellationToken cancellationToken,
        int? delaySecondsOverride = null)
    {
        if (!_options.Enabled) return;

        try
        {
            var duel = await _duelRepository.GetByIdAsync(duelId);
            if (duel is null)
            {
                AutoJudgeLog.StoodDown(_logger, duelId, "duel not found");
                return;
            }

            // The human picked inside the window — their decision stands, no second opinion.
            if (duel.Verdict != DuelVerdict.Pending)
            {
                AutoJudgeLog.StoodDown(_logger, duelId, $"already judged ({duel.Verdict})");
                return;
            }

            var results = await _duelResultRepository.GetByDuelIdAsync(duelId);
            var left = results.FirstOrDefault(r => r.ModelId == duel.LeftModelId);
            var right = results.FirstOrDefault(r => r.ModelId == duel.RightModelId);

            // A duel where both models produced nothing can never be judged by a human —
            // RecordVerdictHandler rejects any vote on it with 422 — so there is no grace
            // window to wait out. Void it immediately rather than leaving it in the
            // awaiting-verdict list for DelaySeconds.
            var leftOk = left is not null && !left.IsFailure && !string.IsNullOrWhiteSpace(left.HtmlOutputRaw);
            var rightOk = right is not null && !right.IsFailure && !string.IsNullOrWhiteSpace(right.HtmlOutputRaw);
            var configuredDelay = (!leftOk && !rightOk) ? 0 : (delaySecondsOverride ?? _options.DelaySeconds);
            // Floor at 0 because a tournament legitimately wants 0 ("decide immediately, no human
            // is watching"). Negative values come from misconfigured appsettings and would
            // skip the delay entirely, which is benign but ugly in the logs.
            var delaySeconds = Math.Clamp(configuredDelay, 0, 3600);
            if (delaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);

                duel = await _duelRepository.GetByIdAsync(duelId);
                if (duel is null)
                {
                    AutoJudgeLog.StoodDown(_logger, duelId, "duel not found");
                    return;
                }

                // The human picked inside the window — their decision stands, no second opinion.
                if (duel.Verdict != DuelVerdict.Pending)
                {
                    AutoJudgeLog.StoodDown(_logger, duelId, $"already judged ({duel.Verdict})");
                    return;
                }
            }

            JudgeDecision? decision;
            string? standDownReason = null;
            try
            {
                decision = await DecideAsync(duel, left, right, cancellationToken);
            }
            catch (JudgeRateLimitedException rateLimit)
            {
                standDownReason = $"Rate-limited by judge endpoint; retrying in {rateLimit.RetryAfter.TotalSeconds:F0}s.";
                await RequeueAfterRateLimitAsync(duelId, rateLimit.RetryAfter, cancellationToken);
                await PersistStandDownReasonAsync(duel, standDownReason);
                return;
            }

            // Make the "could not decide" reason persistent on the duel so a human arriving at
            // the Arena later gets the same hint the judge had. This is now only the
            // judge-unreachable / unparseable-reply path — both-models-failed takes the Voided
            // decision inside DecideAsync, so it reaches RecordAsync, not this branch.
            if (decision is null)
            {
                standDownReason = SynthesizeStandDownReason(left, right);
                if (standDownReason is not null)
                    await PersistStandDownReasonAsync(duel, standDownReason);

                // Reset the rate-limit counter on a clean "no decision" so a subsequent 429
                // gets a fresh budget — the alternative is locking out the duel forever after
                // one transient and one permanent failure.
                RateLimitAttempts.TryRemove(duelId, out _);
                return;
            }

            // A successful decision means we never have to retry this duel; drop the counter
            // and clear the standing-down note so future reads do not show stale text.
            RateLimitAttempts.TryRemove(duelId, out _);
            if (!string.IsNullOrEmpty(duel.JudgeStoodDownReason))
            {
                duel.JudgeStoodDownReason = null;
                await SafeUpdateAsync(duel, duelId);
            }
            await RecordAsync(duelId, decision, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down — the duel stays Pending and can still be judged by hand.
        }
        catch (Exception ex)
        {
            AutoJudgeLog.Failed(_logger, ex, duelId);
        }
    }

    /// <summary>
    /// Schedules a fresh attempt after <paramref name="retryAfter"/> via the same task queue the
    /// duels themselves use, then returns so the next queued duel does not stall behind a
    /// long <c>Retry-After</c>.
    /// </summary>
    private Task RequeueAfterRateLimitAsync(DuelId duelId, TimeSpan retryAfter, CancellationToken cancellationToken)
    {
        var max = Math.Max(0, _options.RateLimitRetryMax);
        var attempt = RateLimitAttempts.AddOrUpdate(duelId, 1, (_, prev) => prev + 1);
        var cappedDelay = TimeSpan.FromSeconds(
            Math.Min(retryAfter.TotalSeconds, Math.Max(1, _options.RateLimitRetryMaxDelaySeconds)));

        if (attempt > max)
        {
            // Out of retries. Leave the duel Pending so a human can finish it; the queue item
            // is the only thing standing in the way of the next duel, so dropping it is
            // essential and "judge stood down (rate-limit)" still appears in the Arena for
            // anyone who walks back to this duel. The attempt counter is dropped with it —
            // it used to leak here (only the success and no-decision paths cleared it), so a
            // duel re-arrived at later started life with a spent budget.
            RateLimitAttempts.TryRemove(duelId, out _);
            AutoJudgeLog.StoodDown(_logger, duelId,
                $"rate-limit retries exhausted ({attempt - 1} attempts)");
            return Task.CompletedTask;
        }

        AutoJudgeLog.RateLimited(_logger, duelId, cappedDelay.TotalSeconds, attempt, max);

        // Detached timer, not a queue item. The retry used to occupy a BackgroundTaskService
        // slot for the whole capped delay (up to RateLimitRetryMaxDelaySeconds) — and because
        // that host is single-consumer, every duel queued behind it stalled with the duel that
        // got rate-limited, the opposite of what the old doc comment promised. A detached task
        // frees the slot immediately; a host shutdown kills the wait, which leaves the duel
        // Pending and hand-judgeable — the same outcome the old path had on shutdown.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(cappedDelay);
                await RunAsync(duelId, CancellationToken.None, delaySecondsOverride: 0);
            }
            catch (OperationCanceledException) { /* host shutting down */ }
            catch (Exception ex)
            {
                AutoJudgeLog.Failed(_logger, ex, duelId);
            }
        });

        return Task.CompletedTask;
    }

    private async Task<JudgeDecision?> DecideAsync(
        Duel duel,
        DuelResult? left,
        DuelResult? right,
        CancellationToken cancellationToken)
    {
        var leftOk = left is not null && !left.IsFailure && !string.IsNullOrWhiteSpace(left.HtmlOutputRaw);
        var rightOk = right is not null && !right.IsFailure && !string.IsNullOrWhiteSpace(right.HtmlOutputRaw);

        // Nothing to compare. There is no evidence and none is coming — both result rows exist
        // and both are failures — so the duel is VOIDED rather than left Pending. Leaving it
        // Pending stranded it forever: the Arena offered winner buttons the server would reject,
        // the lobby counted it as "awaiting judgment" until the end of time, and the only exit
        // was a human reading two failure banners. A void is the honest terminal: it banks no
        // duel count, no draw, no ELO, and drops out of every awaiting-judgment projection.
        // Recording happens inside RecordAsync, which logs once on success — the void case used
        // to log here too, leaving two Recorded lines for the same duel.
        if (!leftOk && !rightOk)
        {
            return new JudgeDecision(
                DuelVerdict.Voided,
                "Neither model produced usable output — the duel was voided with no ELO change.",
                IsWalkover: true);
        }

        // One side produced nothing. The survivor is direct model-quality evidence, not the
        // absence of it — recording a walkover moves the survivor's rating and reflects the
        // loser's failure in their DuelCount. PRD §9 item 20 reverses the "leave it Pending"
        // decision from the prior pass once a batch of Kimi-vs-* duels surfaced the gap; the
        // no-evidence guard in RecordVerdictHandler is unchanged because the survivor IS the
        // evidence (one result row, one failure row).
        if (leftOk != rightOk)
        {
            var failedSide = leftOk ? "right" : "left";
            var survivorSide = leftOk ? DuelVerdict.Left : DuelVerdict.Right;
            var failed = leftOk ? right : left;
            // Trim the trailing period: the reason text usually ends in one, and the rationale
            // template appends its own — the Arena used to show "(timeout or user abort).).".
            var why = string.IsNullOrWhiteSpace(failed?.FailureReason)
                ? "produced no output"
                : failed.FailureReason!.Split('\n', 2)[0].Trim().TrimEnd('.');
            // Caller (RecordAsync) logs Recorded once the verdict is on disk; no log here to
            // avoid double-logging a walkover as both "decided" and "stood down".
            return new JudgeDecision(
                survivorSide,
                $"Walkover: opponent ({failedSide}) failed to produce output ({why}).",
                IsWalkover: true);
        }

        return await _judge.JudgeAsync(
            string.IsNullOrWhiteSpace(duel.PromptFull) ? duel.PromptText : duel.PromptFull,
            left!.HtmlOutputRaw,
            right!.HtmlOutputRaw,
            cancellationToken);
    }

    /// <summary>
    /// Best-effort persistence of a standing-down reason on the duel. A 412 here means a human
    /// or another auto-judge race has moved the row; in that case the next reader still gets
    /// a useful value (whatever won the race) and we drop the write without surfacing a false
    /// error.
    /// </summary>
    private async Task PersistStandDownReasonAsync(Duel duel, string reason)
    {
        try
        {
            duel.JudgeStoodDownReason = reason;
            await _duelRepository.UpdateAsync(duel);
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            // Lost the optimistic-concurrency race; nothing we can do without re-read + retry
            // and the reason is informational, not load-bearing.
        }
    }

    private static string? SynthesizeStandDownReason(DuelResult? left, DuelResult? right)
    {
        var leftOk = left is not null && !left.IsFailure && !string.IsNullOrWhiteSpace(left.HtmlOutputRaw);
        var rightOk = right is not null && !right.IsFailure && !string.IsNullOrWhiteSpace(right.HtmlOutputRaw);
        if (!leftOk && !rightOk) return "Neither model produced output.";
        return "One model did not produce usable output; no rating was recorded.";
    }

    /// <summary>
    /// Quietly clears stale state on the duel, swallowing the optimistic-concurrency races
    /// that happen when the human path finishes a verdict between our reads and our writes.
    /// </summary>
    private async Task SafeUpdateAsync(Duel duel, DuelId duelId)
    {
        try { await _duelRepository.UpdateAsync(duel); }
        catch (RequestFailedException ex) when (ex.Status == 412) { /* race lost */ }
    }

    private async Task RecordAsync(DuelId duelId, JudgeDecision decision, CancellationToken cancellationToken)
    {
        // A walkover or a void is arithmetic over the result rows, not an LLM opinion: it must
        // never carry VerdictSource.Ai or the deployment name, or every downstream breakdown
        // ("how often does the AI judge decide duels?") counts forfeits as judgments.
        var (source, judgeModel) = decision.IsWalkover
            ? (VerdictSource.Constraint, null)
            // The model that actually judged: with vision off that is TextOnlyDeployment, and
            // stamping Deployment credited gpt-5.4-mini with every gpt-4.1-nano verdict. A split
            // settled by the tie-break judge names that model instead.
            : (VerdictSource.Ai, decision.Model ?? _options.EffectiveDeployment(_options.VisionEnabled));

        var command = new RecordVerdictCommand(
            duelId,
            decision.Verdict,
            source,
            decision.Rationale,
            judgeModel,
            JudgeAnalysis: decision.Analysis,
            JudgeCostUsd: decision.CostUsd);

        VerdictResponseDto? response;
        try
        {
            response = await _recordVerdict.HandleWithRetryAsync(command);
        }
        catch (InvalidOperationException ex)
        {
            // A human landed a verdict between our Pending check and this write. Theirs wins.
            AutoJudgeLog.StoodDown(_logger, duelId, ex.Message);
            return;
        }

        if (response is null) return;

        AutoJudgeLog.Recorded(_logger, duelId, decision.Verdict);

        await _hubContext.Clients
            .Group($"duel:{duelId}")
            .SendAsync("VerdictRecorded", response, cancellationToken);
    }
}
