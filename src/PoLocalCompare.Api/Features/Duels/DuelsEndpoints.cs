using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Azure;
using PoLocalCompare.Api.Auth;
using static PoLocalCompare.Api.Auth.AnonymousWrites;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public static class DuelsEndpoints
{
    /// <summary>
    /// Rate-limit policy on the two endpoints that start paid inference (this one and
    /// <c>POST /api/tournaments</c>). Defined in Program.cs from <c>Limits:DuelStartsPerMinute</c>.
    /// </summary>
    public const string StartRateLimitPolicy = "duel-start";

    /// <summary>
    /// Reads (GET /api/duels, GET /api/duels/{id}) stay authenticated
    /// so the leaderboard and archive can't be scraped anonymously. Writes — including
    /// <c>local-result</c>, which used to opt out entirely — are opened only when
    /// <c>Features:AllowAnonymousWrites</c> is true (Development default). The flag is read
    /// once at startup; flipping it requires a restart.
    /// </summary>
    public static IEndpointRouteBuilder MapDuelsEndpoints(this IEndpointRouteBuilder app, bool allowAnonymousWrites = false)
    {
        var group = app.MapGroup("/api/duels").WithTags("Duels").RequireAuthorization();

        var commenceDuel = OpenIf(allowAnonymousWrites, group.MapPost("/", async (
            [FromBody] CommenceDuelRequest request,
            HttpContext httpContext,
            [FromServices] CommenceDuelHandler handler,
            [FromServices] SpendGuard spendGuard,
            [FromServices] DuelExecutionService executionService) =>
        {
            if (string.IsNullOrWhiteSpace(request.PromptText))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["PromptText"] = ["PromptText is required."]
                });

            if (await spendGuard.BlocksAsync(request.LeftModelId, request.RightModelId))
                return SpendGuard.Refusal();

            try
            {
                // Clamped to the same range AutoJudge itself enforces, so a caller cannot park a
                // duel's judge an hour out. Passing 0 only skips the asker's own grace window.
                var delayOverride = request.AutoJudgeDelaySeconds is { } requested
                    ? Math.Clamp(requested, 0, 3600)
                    : (int?)null;

                // Resolve the actor forensically — IdentityResolver returns "anonymous" when no
                // preferred_username claim is present. The handler stamps it onto Duel.OwnerId.
                var actor = IdentityResolver.ResolveActor(httpContext.User);

                var dto = await handler.HandleAsync(new CommenceDuelCommand(
                    request.LeftModelId,
                    request.RightModelId,
                    request.PromptText,
                    delayOverride,
                    actor));

                await executionService.EnqueueAsync(dto.DuelId, delayOverride);

                return Results.Accepted($"/api/duels/{dto.DuelId}", dto);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["Request"] = [ex.Message]
                });
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return Results.Problem(
                    title: "Storage not ready",
                    detail: "Duel storage is initializing. Please retry in a few seconds.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
        .RequireRateLimiting(StartRateLimitPolicy)
        .WithName("CommenceDuel")
        .WithSummary("Starts a new duel between two models.")
        .Produces<DuelDto>(StatusCodes.Status202Accepted)
        .ProducesValidationProblem()
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status429TooManyRequests));

        group.MapGet("/{duelId}", async (
            DuelId duelId,
            [FromServices] GetDuelHandler handler) =>
        {
            var dto = await handler.HandleAsync(duelId);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        })
        .WithName("GetDuel")
        .WithSummary("Gets a duel by ID including full telemetry results.")
        .Produces<DuelDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        // The Arena's "what the judge sees" strip: the same 320×180 screenshot FoundryDuelJudge
        // attaches for one side, from the same raw output and the same renderer. 404 whenever
        // the judge would not have had one either — vision off (always, in Production: the
        // Free-tier App Service has no browser), no output yet, or a render that failed — and
        // the client hides the strip on that. Cached because a page view asks for both sides
        // and the output is immutable once written; rendering is ~1 s of headless Chromium.
        group.MapGet("/{duelId}/judge-view/{side}", async (
            DuelId duelId,
            string side,
            [FromServices] IDuelRepository duels,
            [FromServices] IDuelResultRepository results,
            [FromServices] HtmlScreenshotRenderer renderer,
            [FromServices] HybridCache cache,
            [FromServices] Microsoft.Extensions.Options.IOptions<AutoJudgeOptions> judgeOptions,
            CancellationToken cancellationToken) =>
        {
            if (!judgeOptions.Value.VisionEnabled) return Results.NotFound();

            var isLeft = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase);
            if (!isLeft && !string.Equals(side, "right", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();

            var png = await cache.GetOrCreateAsync(
                $"judge-view:{duelId}:{(isLeft ? "left" : "right")}",
                async token =>
                {
                    var duel = await duels.GetByIdAsync(duelId);
                    if (duel is null) return null;

                    var result = await results.GetAsync(duelId, isLeft ? duel.LeftModelId : duel.RightModelId);
                    if (result is null || result.IsFailure) return null;

                    return await renderer.RenderAsync(result.HtmlOutputRaw, token);
                },
                new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(10) },
                cancellationToken: cancellationToken);

            return png is null ? Results.NotFound() : Results.File(png, "image/png");
        })
        .WithName("GetJudgeView")
        .WithSummary("Renders one side of a duel as the vision judge sees it (PNG). 404 when vision is off.")
        .Produces(StatusCodes.Status200OK, contentType: "image/png")
        .Produces(StatusCodes.Status404NotFound);

        // Called by the Blazor client after local (WebLLM) inference completes. It carried an
        // unconditional AllowAnonymous() until 2026-09-02, on the premise that "the browser worker
        // invoking this endpoint is not authenticated" — which was never true: the POST comes from
        // DuelApiClient on the app origin, so the BFF session cookie rides along exactly as it does
        // for POST /api/duels. It now follows the same gate as the other writes.
        //
        // Authorization alone would not be enough. The row this writes is what
        // DuelExecutionService returns as the model's official result, and ChallengeAdjudicator
        // and AutoJudge decide the duel — and move persistent ELO — from it. So the submission is
        // also checked against the duel it claims to belong to: without that, the (duelId, modelId)
        // key is caller-chosen and any row in the table can be written or overwritten.
        OpenIf(allowAnonymousWrites, group.MapPost("/{duelId}/local-result", async (
            DuelId duelId,
            [FromBody] LocalResultRequest request,
            [FromServices] IDuelRepository duelRepo,
            [FromServices] IDuelResultRepository duelResultRepo,
            [FromServices] IModelRepository modelRepo,
            [FromServices] LocalResultSignal localResults,
            [FromServices] IConfiguration configuration) =>
        {
            if (request.ModelId.IsEmpty)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["ModelId"] = ["ModelId is required."]
                });

            var duel = await duelRepo.GetByIdAsync(duelId);
            if (duel is null)
                return Results.NotFound(new { error = $"Duel '{duelId}' not found." });

            // Only the two models actually fighting this duel may report into it.
            if (request.ModelId != duel.LeftModelId && request.ModelId != duel.RightModelId)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["ModelId"] = ["ModelId is not one of this duel's models."]
                });

            // Only browser models run client-side, so only they legitimately report their own
            // result. A server-side model's output must come from the server that produced it.
            var model = await modelRepo.GetByIdAsync(request.ModelId);
            if (model is null || model.ModelType != ModelType.Local)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["ModelId"] = ["Only browser (Local) models report their result from the client."]
                });

            // A finished duel's record is the evidence its verdict was based on; reopening it for
            // writes would let the archive be rewritten after the fact.
            if (duel.CompletedAt is not null || duel.Verdict != DuelVerdict.Pending)
                return Results.Conflict(new { error = $"Duel '{duelId}' is no longer accepting results." });

            // One result per model per duel. The repository upserts with Replace, so without this
            // a second post would silently overwrite the output the duel is being judged on.
            if (await duelResultRepo.GetAsync(duelId, request.ModelId) is not null)
                return Results.Conflict(new { error = $"A result for model '{request.ModelId}' is already recorded." });

            var normalizedHtml = HtmlOutputNormalizer.Normalize(request.HtmlOutputRaw);
            var isEmptyOutput = string.IsNullOrWhiteSpace(normalizedHtml);

            var result = new DuelResult(duelId, request.ModelId)
            {
                HtmlOutputRaw = normalizedHtml,
                HtmlOutputSizeBytes = System.Text.Encoding.UTF8.GetByteCount(normalizedHtml),
                TokenCount = request.TokenCount,
                TotalDurationMs = request.TotalDurationMs,
                WarmUpDurationMs = request.WarmUpDurationMs,
                GenerationDurationMs = Math.Max(0, request.TotalDurationMs - request.WarmUpDurationMs),
                TokenVelocity = request.TotalDurationMs > 0
                    ? request.TokenCount / (request.TotalDurationMs / 1000.0)
                    : 0,
                IsFailure = request.IsFailure || isEmptyOutput,
                FailureReason = request.FailureReason ?? (isEmptyOutput ? "Browser inference completed without output." : null),
            };

            // Apply the shared Domain enrichment policy (density, quality, GreenStats).
            var electricityRate = configuration.GetValue("GreenStats:ElectricityRateUsd", 0.12);
            DuelResultEnricher.Enrich(result, model, electricityRate);

            await duelResultRepo.SaveAsync(result);
            localResults.Notify(duelId, request.ModelId);

            return Results.Ok();
        })
        .WithName("PostLocalResult")
        .WithSummary("Receives the HTML output from a client-side (WebLLM) local model inference.")
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesValidationProblem());

        var recordVerdict = OpenIf(allowAnonymousWrites, group.MapPost("/{duelId}/verdict", async (
            DuelId duelId,
            [FromBody] VerdictRequestDto request,
            HttpContext httpContext,
            [FromServices] RecordVerdictHandler handler) =>
        {
            try
            {
                // Voided is system-reserved: the auto-judge and the startup recovery sweeper
                // reach it through the handler directly, so an HTTP caller bidding a duel they
                // dislike out of existence must get a 422, not a voided duel.
                if (request.Verdict == DuelVerdict.Voided)
                    return Results.UnprocessableEntity(new { error = "Voided is a system verdict and cannot be submitted manually." });

                var actor = IdentityResolver.ResolveActor(httpContext.User);
                var response = await handler.HandleWithRetryAsync(
                    new RecordVerdictCommand(duelId, request.Verdict, VerdictSource.Human, Actor: actor));

                if (response is null)
                    return Results.NotFound(new { error = $"Duel '{duelId}' not found." });

                return Results.Ok(response);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        })
        .WithName("RecordVerdict")
        .WithSummary("Records the winner verdict and updates ELO ratings.")
        .Produces<VerdictResponseDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status422UnprocessableEntity));

        // T077 — GET /api/duels (archive listing with pagination)
        group.MapGet("/", async (
            [FromQuery] int? limit,
            [FromQuery] DuelId? before,
            [FromQuery] DuelVerdict[]? verdict,
            [FromServices] ListDuelsHandler handler) =>
        {
            // `limit` is optional: an absent (or 0) value defaults to 20. Declaring it
            // non-nullable/required previously returned 500 when callers omitted it.
            var clampedLimit = Math.Clamp(limit is null or 0 ? 20 : limit.Value, 1, 100);

            // `before` is a keyset cursor — the oldest duel id the caller already holds — and
            // only strictly older duels come back. It was a yyyyMM partition stamp, which paged
            // with `le` and so re-served the current month on every Load More. The repository
            // binds it as an escaped literal. `verdict` repeats (?verdict=Left&verdict=Right) and
            // filters before the limit, so "Unjudged" pages through unjudged duels rather than
            // filtering whichever twenty happened to load.
            var results = await handler.HandleAsync(clampedLimit, before, verdict);
            return Results.Ok(results);
        })
        .WithName("ListDuels")
        .WithSummary("Lists duel summaries in reverse chronological order.")
        .Produces<IReadOnlyList<DuelSummaryDto>>(StatusCodes.Status200OK)
        .ProducesValidationProblem();

        return app;
    }
}

/// <param name="AutoJudgeDelaySeconds">
/// Optional per-duel grace window. Omit to use <c>AiJudge:DelaySeconds</c>; a tournament sends 0
/// so an unattended run is never waiting on a human pick that will not come.
/// </param>
public sealed record CommenceDuelRequest(
    ModelId LeftModelId,
    ModelId RightModelId,
    string PromptText,
    int? AutoJudgeDelaySeconds = null);

public sealed record LocalResultRequest(
    ModelId ModelId,
    string? HtmlOutputRaw,
    int TokenCount,
    long TotalDurationMs,
    long WarmUpDurationMs,
    bool IsFailure,
    string? FailureReason);
