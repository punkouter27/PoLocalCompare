// GoF: Strategy — inference execution varies by model type
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

/// <summary>Source-generated, allocation-free log messages for the duel execution hot path.</summary>
internal static partial class DuelExecutionLog
{
    [LoggerMessage(EventId = 1100, Level = LogLevel.Warning, Message = "Duel {DuelId} not found for execution.")]
    public static partial void DuelNotFound(ILogger logger, DuelId duelId);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Error, Message = "One or both models not found for duel {DuelId}.")]
    public static partial void ModelsNotFound(ILogger logger, DuelId duelId);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Error, Message = "Duel execution failed for {DuelId}.")]
    public static partial void ExecutionFailed(ILogger logger, Exception ex, DuelId duelId);
}

public sealed class DuelExecutionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<DuelHub> _hubContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DuelExecutionService> _logger;
    private readonly IBackgroundTaskQueue _taskQueue;
    private readonly LocalResultSignal _localResults;

    public DuelExecutionService(
        IServiceScopeFactory scopeFactory,
        IHubContext<DuelHub> hubContext,
        IConfiguration configuration,
        ILogger<DuelExecutionService> logger,
        IBackgroundTaskQueue taskQueue,
        LocalResultSignal localResults)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _configuration = configuration;
        _logger = logger;
        _taskQueue = taskQueue;
        _localResults = localResults;
    }

    /// <param name="autoJudgeDelaySecondsOverride">
    /// Replaces the configured grace window for this duel only — a tournament passes 0 so the
    /// bracket does not pause between rounds waiting for a human who is not there.
    /// </param>
    /// <remarks>
    /// A duel with a browser side leaves the queue as soon as it is dequeued. Its execution is
    /// mostly a wait on someone's tab — minutes, for a small model on a cold GPU — and
    /// <see cref="BackgroundTaskService"/> is single-consumer, so until 2026-10-26 every other
    /// user's duel queued behind that wait. The same reasoning detached the judge on 2026-09-26.
    /// Server-side duels stay on the queue: that is where their ordering and back-pressure live.
    /// </remarks>
    public Task EnqueueAsync(DuelId duelId, int? autoJudgeDelaySecondsOverride = null)
    {
        _taskQueue.QueueBackgroundWork(async ct =>
        {
            using var scope = _scopeFactory.CreateScope();
            if (!await HasBrowserSideAsync(scope.ServiceProvider, duelId))
            {
                await ExecuteAsync(scope.ServiceProvider, duelId, autoJudgeDelaySecondsOverride, ct);
                return;
            }

            // Own scope: the queue item's is disposed when it returns. ExecuteAsync never throws.
            _ = Task.Run(async () =>
            {
                using var own = _scopeFactory.CreateScope();
                await ExecuteAsync(own.ServiceProvider, duelId, autoJudgeDelaySecondsOverride, ct);
            }, CancellationToken.None);
        });
        return Task.CompletedTask;
    }

    private static async Task<bool> HasBrowserSideAsync(IServiceProvider services, DuelId duelId)
    {
        if (await services.GetRequiredService<IDuelRepository>().GetByIdAsync(duelId) is not { } duel) return false;
        var models = services.GetRequiredService<IModelRepository>();
        return (await models.GetByIdAsync(duel.LeftModelId))?.ModelType == ModelType.Local
            || (await models.GetByIdAsync(duel.RightModelId))?.ModelType == ModelType.Local;
    }

    /// <summary>
    /// Runs the duel end-to-end on the calling thread, without going through the background
    /// task queue. Used by the tournament runner, which already owns a long-lived wait loop
    /// and would otherwise deadlock: <see cref="BackgroundTaskService"/> is single-consumer
    /// and awaits each work item before dequeuing the next, so a runner that queues a duel
    /// behind itself stalls forever.
    /// </summary>
    /// <remarks>
    /// The runner used to share the queue with everything else and stalled at the first
    /// match; running inline keeps the bracket on its own thread but lets the duel inference
    /// finish, which is the whole point. The runner is responsible for its own concurrency
    /// limit (<see cref="Tournaments.TournamentRunner"/> caps it at two), so the single-
    /// threaded queue is not a bottleneck here.
    /// </remarks>
    public async Task RunAsync(DuelId duelId, int? autoJudgeDelaySecondsOverride, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        await ExecuteAsync(scope.ServiceProvider, duelId, autoJudgeDelaySecondsOverride, cancellationToken);
    }

    private async Task ExecuteAsync(
        IServiceProvider services,
        DuelId duelId,
        int? autoJudgeDelaySecondsOverride,
        CancellationToken cancellationToken)
    {
        var duelRepo = services.GetRequiredService<IDuelRepository>();
        var modelRepo = services.GetRequiredService<IModelRepository>();
        var duelResultRepo = services.GetRequiredService<IDuelResultRepository>();

        IRemoteInferenceProxy ResolveProxy(Model model) => model.ModelType switch
        {
            ModelType.LocalService => services.GetRequiredKeyedService<IRemoteInferenceProxy>("LocalService"),
            _ => services.GetRequiredKeyedService<IRemoteInferenceProxy>("Remote")
        };

        Duel? duel = null;
        try
        {
            duel = await duelRepo.GetByIdAsync(duelId);
            if (duel is null)
            {
                DuelExecutionLog.DuelNotFound(_logger, duelId);
                return;
            }

            var leftModel = await modelRepo.GetByIdAsync(duel.LeftModelId);
            var rightModel = await modelRepo.GetByIdAsync(duel.RightModelId);
            if (leftModel is null || rightModel is null)
            {
                DuelExecutionLog.ModelsNotFound(_logger, duelId);
                return;
            }

            var lobby = services.GetRequiredService<LobbyNotifier>();
            await lobby.DuelStartedAsync(duel, leftModel.DisplayName, rightModel.DisplayName, cancellationToken);

            // Watchdog — was a hardcoded 900 s while appsettings shipped Duel:TimeLimitSeconds =
            // 300 that nothing read, so the two numbers disagreed with each other and with every
            // DTO that echoed one or the other. One knob now rules them all: the config value,
            // defaulting to the 900 s the WebGPU JIT warm-up was sized for.
            var watchdogSeconds = Math.Clamp(
                _configuration.GetValue("Duel:TimeLimitSeconds", 900), 30, 3600);
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(watchdogSeconds));

            var leftTask = RunModelAsync(duelId, leftModel, "Left", duel.PromptFull,
                ResolveProxy(leftModel), duelResultRepo, watchdogSeconds, watchdog.Token);
            var rightTask = RunModelAsync(duelId, rightModel, "Right", duel.PromptFull,
                ResolveProxy(rightModel), duelResultRepo, watchdogSeconds, watchdog.Token);

            await Task.WhenAll(leftTask, rightTask);

            duel.CompletedAt = DateTimeOffset.UtcNow;
            try
            {
                await duelRepo.UpdateAsync(duel);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 412)
            {
                // A verdict landed while inference ran (standards §5.5): re-read and
                // reapply only the completion timestamp instead of clobbering the verdict.
                var fresh = await duelRepo.GetByIdAsync(duelId);
                if (fresh is not null)
                {
                    fresh.CompletedAt ??= duel.CompletedAt;
                    await duelRepo.UpdateAsync(fresh);
                    duel = fresh;
                }
            }

            await _hubContext.Clients
                .Group($"duel:{duelId}")
                .SendAsync("DuelComplete", new DuelDto
                {
                    DuelId = duel.DuelId,
                    PromptText = duel.PromptText,
                    PromptFull = duel.PromptFull,
                    LeftModelId = duel.LeftModelId,
                    RightModelId = duel.RightModelId,
                    LeftModelName = leftModel.DisplayName,
                    RightModelName = rightModel.DisplayName,
                    StartedAt = duel.StartedAt,
                    CompletedAt = duel.CompletedAt,
                    Verdict = DuelVerdict.Pending,
                    OwnerId = duel.OwnerId,
                    VerdictBy = duel.VerdictBy,
                });

            await lobby.DuelCompletedAsync(duel, leftModel.DisplayName, rightModel.DisplayName, cancellationToken);

            // Hand off to the auto-judge, which waits out the grace window before deciding.
            //
            // With no grace window (a tournament passes 0) the judge runs inline: the runner is
            // waiting on this verdict and nothing else is queued behind it. With one, the wait
            // and the judge call are DETACHED. They used to run inline too, and because
            // BackgroundTaskService is single-consumer that meant every standalone duel held the
            // only worker for inference + DelaySeconds + the judge call — a second user's duel
            // could not start until the first one was decided. The rate-limit retry in AutoJudge
            // was detached for the same reason. A host shutdown during the wait leaves the duel
            // Pending and hand-judgeable, the same outcome that path already accepts.
            // AutoJudge.RunAsync never throws.
            //
            // Challenge budgets used to be adjudicated here, ahead of the judge. Challenge mode
            // was removed on 2026-09-10, so the judge is now the only decider. Note that
            // VerdictSource.Constraint survives it: DuelRecoverySweeper and AutoJudge still
            // stamp it for abandoned duels and one-sided failures.
            var graceSeconds = autoJudgeDelaySecondsOverride
                ?? services.GetRequiredService<IOptions<AutoJudgeOptions>>().Value.DelaySeconds;
            if (graceSeconds <= 0)
            {
                await services.GetRequiredService<AutoJudge>()
                    .RunAsync(duelId, cancellationToken, autoJudgeDelaySecondsOverride);
            }
            else
            {
                // Own scope: the one this method was handed is disposed as soon as it returns.
                _ = Task.Run(async () =>
                {
                    using var judgeScope = _scopeFactory.CreateScope();
                    await judgeScope.ServiceProvider.GetRequiredService<AutoJudge>()
                        .RunAsync(duelId, cancellationToken, autoJudgeDelaySecondsOverride);
                }, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            DuelExecutionLog.ExecutionFailed(_logger, ex, duelId);
        }
    }

    private async Task RunModelAsync(
        DuelId duelId,
        Model model,
        string side,
        string promptFull,
        IRemoteInferenceProxy inferenceProxy,
        IDuelResultRepository duelResultRepo,
        long watchdogSeconds,
        CancellationToken cancellationToken)
    {
        await SendStatusAsync(duelId, model.ModelId, side, DuelStatus.Initializing, 0, 0);

        DuelResult result;

        // Defensive catch: every exit must produce a saved DuelResult row. Without this, an
        // exception thrown inside RunInferenceAsync (or WaitForLocalModelResultAsync) after the
        // proxy had already assigned `result.IsFailure = true` but before it returned would
        // surface from the task — `Task.WhenAll` aggregates and the outer catch is fine — but if
        // it left without writing the row, the duel would strand as "in progress, not judged"
        // forever (PRD §9: ELO cannot move on no evidence, and one-sided failures take the
        // walkover path inside AutoJudge, both of which require both result rows to exist).
        try
        {
            if (model.ModelType == ModelType.Local)
            {
                // Local model: client-side inference via SignalR; server waits for client to report results
                result = await WaitForLocalModelResultAsync(duelId, model, side, watchdogSeconds, cancellationToken);
            }
            else
            {
                // Remote model: server-side inference via Foundry proxy
                await SendStatusAsync(duelId, model.ModelId, side, DuelStatus.Generating, 0, 0);

                long? warmUpMs = null;
                double peakVelocity = 0;
                DateTimeOffset lastTokenAt = DateTimeOffset.UtcNow;

                result = await inferenceProxy.RunInferenceAsync(
                    model,
                    duelId,
                    promptFull,
                    async (tokenCount, elapsedMs, htmlStats) =>
                    {
                        // First token arrival = warm-up latency known
                        if (warmUpMs is null && tokenCount >= 1)
                            warmUpMs = elapsedMs;

                        // Gap to the PREVIOUS callback, not to now. This used to stamp
                        // lastTokenAt = UtcNow and then measure UtcNow - lastTokenAt, which is
                        // zero by construction — IsStalled was false in every frame it was
                        // ever sent. Measuring the gap between token arrivals is the actual
                        // "tokens stopped flowing" signal the client displays.
                        var secondsSincePreviousToken = (DateTimeOffset.UtcNow - lastTokenAt).TotalSeconds;
                        lastTokenAt = DateTimeOffset.UtcNow;

                        // Peak velocity (generation-phase only, excluding warm-up)
                        var genMs = warmUpMs.HasValue ? elapsedMs - warmUpMs.Value : elapsedMs;
                        var currentVelocity = genMs > 0 ? Math.Round(tokenCount / (genMs / 1000.0), 1) : 0;
                        if (currentVelocity > peakVelocity) peakVelocity = currentVelocity;

                        var isStalled = secondsSincePreviousToken > 2;

                        await SendStatusAsync(duelId, model.ModelId, side,
                            DuelStatus.Generating, elapsedMs, tokenCount,
                            warmUpMs: warmUpMs,
                            peakVelocity: peakVelocity,
                            isStalled: isStalled,
                            htmlStats: htmlStats);
                    },
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Watchdog tripped or host shutdown — synthesise a failure row so the duel can
            // still transition to Complete and the auto-judge can run (probably standing down
            // because one side has no usable output, which is the documented behaviour).
            result = new DuelResult(duelId, model.ModelId)
            {
                IsFailure = true,
                FailureReason = $"Inference cancelled by watchdog ({TimeSpan.FromSeconds(watchdogSeconds).TotalMinutes:F0} min).",
                TotalDurationMs = watchdogSeconds * 1000L,
            };
        }
        catch (Exception ex)
        {
            // Any other unexpected exception from inside inference must not strand the duel.
            // The catch in ExecuteAsync only sees exceptions surfaced through Task.WhenAll,
            // which is fine for completion; here we ensure every exit writes a row.
            result = new DuelResult(duelId, model.ModelId)
            {
                IsFailure = true,
                FailureReason = $"Inference crashed: {ex.GetType().Name}: {ex.Message}",
                TotalDurationMs = 0,
            };
            _logger.LogWarning(ex, "Inference crashed for {Side} ({ModelId}); recording failure row.", side, model.ModelId);
        }

        // The row is the whole point of everything above — a throw in the status ping, the
        // enrichment or the save used to escape past the defensive catches and strand the duel
        // as "in progress, not judged" even though inference itself had finished cleanly. So
        // the tail is its own guarded section: an enrichment failure degrades the row to a
        // failure row rather than losing it.
        try
        {
            if (result.IsFailure)
            {
                await SendStatusAsync(duelId, model.ModelId, side,
                    DuelStatus.Failed, result.TotalDurationMs, result.TokenCount);

                // A remote model that just failed a real duel is better evidence than a probe
                // from up to ten minutes ago: drop the cached availability so the next Home
                // load re-probes and can hold this model back.
                if (model.ModelType == ModelType.Remote)
                {
                    using var cacheScope = _scopeFactory.CreateScope();
                    await cacheScope.ServiceProvider
                        .GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>()
                        .RemoveByTagAsync(CacheTags.ModelAvailability);
                }
            }
            else
            {
                // The finished output rides out with the Done status rather than waiting for
                // DuelComplete: that message only fires once *both* sides are in, so until then a
                // model that crossed the line would still be showing its last mid-stream preview —
                // truncated ~25 tokens short of the ending, or blank if it never emitted one.
                await SendStatusAsync(duelId, model.ModelId, side,
                    DuelStatus.Done, result.TotalDurationMs, result.TokenCount,
                    finalHtml: Truncate(result.HtmlOutputRaw));
            }

            // Character density + quality + GreenStats enrichment (shared Domain policy).
            var electricityRate = _configuration.GetValue("GreenStats:ElectricityRateUsd", 0.12);
            DuelResultEnricher.Enrich(result, model, electricityRate);

            await duelResultRepo.SaveAsync(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Watchdog fired mid-tail — save the row un-enriched on a detached task so the
            // evidence still lands (fire-and-forget: execution is being torn down either way).
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!result.IsFailure)
                    {
                        var electricityRate = _configuration.GetValue("GreenStats:ElectricityRateUsd", 0.12);
                        DuelResultEnricher.Enrich(result, model, electricityRate);
                    }
                    using var scope = _scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IDuelResultRepository>().SaveAsync(result);
                }
                catch (Exception saved)
                {
                    _logger.LogWarning(saved, "Could not save the {Side} result row for duel {DuelId} after the watchdog fired.", side, duelId);
                }
            });
        }
        catch (Exception tail)
        {
            _logger.LogError(tail, "Finishing the {Side} result row for duel {DuelId} failed; recording a failure row instead.", side, duelId);
            result.IsFailure = true;
            result.FailureReason = $"Result could not be finalised: {tail.Message}";
            try
            {
                await duelResultRepo.SaveAsync(result);
            }
            catch (Exception save)
            {
                _logger.LogError(save, "Could not persist the {Side} failure row for duel {DuelId}.", side, duelId);
            }
        }
    }

    private async Task<DuelResult> WaitForLocalModelResultAsync(
        DuelId duelId,
        Model model,
        string side,
        long watchdogSeconds,
        CancellationToken cancellationToken)
    {
        var payload = new { duelId, modelId = model.ModelId, side, webLlmModelId = model.WebLlmModelId };

        // Listening before the client is told to start, so a result cannot be posted unheard.
        var arrived = _localResults.Register(duelId, model.ModelId);
        try
        {
            // Signal the client to start its Web Worker inference.
            // Retry every 5 s — the client may not have joined the SignalR group yet when the first send fires.
            await _hubContext.Clients
                .Group($"duel:{duelId}")
                .SendAsync("StartLocalInference", payload, cancellationToken);

            var started = DateTimeOffset.UtcNow;
            var lastSignalAt = started;
            var lastStorageCheckAt = started;
            var retryInterval = TimeSpan.FromSeconds(5);

            // One scope for the whole wait — it can run for the full 15-minute watchdog.
            using var pollScope = _scopeFactory.CreateScope();
            var duelResultRepo = pollScope.ServiceProvider.GetRequiredService<IDuelResultRepository>();

            while (!cancellationToken.IsCancellationRequested)
            {
                // Wakes on the 500 ms heartbeat or the moment the result is posted. Delay on a
                // bare token and check the flag — awaiting the cancellable token directly throws
                // the moment the watchdog fires, which made the post-loop failure unreachable
                // and stranded a stalled browser duel as Pending until a host restart swept it.
                await Task.WhenAny(arrived, Task.Delay(500, CancellationToken.None));

                if (cancellationToken.IsCancellationRequested)
                    break;

                // Storage is read when signalled, and otherwise every five seconds — the
                // fallback for a result posted to another instance (see LocalResultSignal).
                if (arrived.IsCompleted || DateTimeOffset.UtcNow - lastStorageCheckAt >= retryInterval)
                {
                    lastStorageCheckAt = DateTimeOffset.UtcNow;
                    if (await duelResultRepo.GetAsync(duelId, model.ModelId) is { } stored)
                        return stored;
                    if (arrived.IsCompleted) arrived = _localResults.Register(duelId, model.ModelId);
                }

                var elapsed = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
                await SendStatusAsync(duelId, model.ModelId, side, DuelStatus.Generating, elapsed, 0);

                // Resend StartLocalInference periodically so late-joining clients receive it
                if (DateTimeOffset.UtcNow - lastSignalAt >= retryInterval)
                {
                    lastSignalAt = DateTimeOffset.UtcNow;
                    await _hubContext.Clients
                        .Group($"duel:{duelId}")
                        .SendAsync("StartLocalInference", payload, cancellationToken);
                }
            }
        }
        finally
        {
            _localResults.Forget(duelId, model.ModelId);
        }

        // Watchdog expired.
        return new DuelResult(duelId, model.ModelId)
        {
            IsFailure = true,
            FailureReason = $"Watchdog timeout ({TimeSpan.FromSeconds(watchdogSeconds).TotalMinutes:F0} min)",
            TotalDurationMs = watchdogSeconds * 1000L,
        };
    }

    /// <summary>
    /// Ceiling on the completed document pushed over the hub. Generous next to the 5 KB
    /// mid-stream preview — this one is the finished render a person looks at — but still
    /// bounded, because a runaway model can emit far more than any preview needs to show.
    /// The persisted result is never truncated; only this hub frame is.
    /// </summary>
    private const int FinalHtmlMaxChars = 40_000;

    private static string? Truncate(string? html) =>
        html is { Length: > FinalHtmlMaxChars } ? html[..FinalHtmlMaxChars] : html;

    private Task SendStatusAsync(
        DuelId duelId,
        ModelId modelId,
        string side,
        DuelStatus status,
        long elapsedMs,
        int tokenCount,
        long? warmUpMs = null,
        double? peakVelocity = null,
        bool isStalled = false,
        HtmlStreamStats? htmlStats = null,
        string? finalHtml = null) =>
        _hubContext.Clients
            .Group($"duel:{duelId}")
            .SendAsync("ModelStatusUpdate", new ModelStatusUpdateDto
            {
                DuelId = duelId,
                ModelId = modelId,
                Side = side,
                Status = status,
                ElapsedMs = elapsedMs,
                TokenCount = tokenCount,
                WarmUpMs = warmUpMs,
                TokenVelocity = warmUpMs.HasValue && elapsedMs > warmUpMs
                    ? Math.Round(tokenCount / ((elapsedMs - warmUpMs.Value) / 1000.0), 1)
                    : (elapsedMs > 0 ? Math.Round(tokenCount / (elapsedMs / 1000.0), 1) : null),
                PeakTokenVelocity = peakVelocity,
                IsStalled = isStalled,
                HtmlTagCount = htmlStats?.TagCount,
                OpenTagDepth = htmlStats?.OpenDepth,
                StyleRuleCount = htmlStats?.StyleRules,
                RepetitionScore = htmlStats?.RepetitionScore,
                // The Done frame carries the whole output and no counters; the streaming frames
                // carry counters and a partial. Neither ever sets both.
                HtmlPreview = finalHtml ?? htmlStats?.HtmlPreview,
            });
}