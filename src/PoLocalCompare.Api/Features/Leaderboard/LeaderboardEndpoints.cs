using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Leaderboard;

public static class LeaderboardEndpoints
{
    public static IEndpointRouteBuilder MapLeaderboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/leaderboard").WithTags("Leaderboard").RequireAuthorization();

        group.MapGet("/", async (
            [FromQuery] string? sortBy,
            [FromServices] GetLeaderboardHandler handler,
            [FromServices] HybridCache cache,
            CancellationToken cancellationToken) =>
        {
            var sort = sortBy ?? "Elo";
            var rows = await cache.GetOrCreateAsync(
                $"leaderboard:{sort}",
                async ct => (await handler.HandleAsync(sort)).ToArray(),
                tags: [CacheTags.Leaderboard],
                cancellationToken: cancellationToken);
            return Results.Ok(rows);
        })
        .WithName("GetLeaderboard")
        .WithSummary("Returns all models ranked by ELO, output quality or average cost.")
        .Produces<IReadOnlyList<LeaderboardEntryDto>>();

        // There is deliberately no /{modelId}/killlist here. GetModelProfileHandler already
        // composes GetKillListHandler into the profile payload's KillList field, so the separate
        // endpoint was a second route to the same rows with no caller in the client. Removed
        // 2026-09-10; the handler stays because the profile depends on it.

        group.MapGet("/{modelId}/profile", async (
            ModelId modelId,
            [FromServices] GetModelProfileHandler handler,
            [FromServices] HybridCache cache,
            CancellationToken cancellationToken) =>
        {
            // Tagged with the leaderboard tag rather than one of its own: every field on this
            // page moves for exactly the same reason the leaderboard does — a verdict — and
            // RecordVerdictHandler already invalidates that tag on the single path ELO moves.
            var profile = await cache.GetOrCreateAsync(
                $"model-profile:{modelId}",
                async _ => await handler.HandleAsync(modelId),
                tags: [CacheTags.Leaderboard],
                cancellationToken: cancellationToken);

            return profile is null ? Results.NotFound() : Results.Ok(profile);
        })
        .WithName("GetModelProfile")
        .WithSummary("Returns one model's standing, rating history, head-to-head record and winning outputs.")
        .Produces<ModelProfileDto>()
        .Produces(StatusCodes.Status404NotFound);

        // Paid inference when no report is cached, so it carries the two guards every such
        // endpoint does: the duel-start rate limit, and the spend cap (with mock inference off
        // too). Either one only stops a NEW report being written — a cached one is still served.
        group.MapGet("/{modelId}/scouting-report", async (
            ModelId modelId,
            [FromServices] GetScoutingReportHandler handler,
            [FromServices] SpendGuard spend,
            [FromServices] IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var mayWrite = configuration.GetValue("Features:UseRealAi", false)
                && !await spend.IsOverBudgetAsync(cancellationToken);
            var (outcome, report) = await handler.HandleAsync(modelId, mayWrite, cancellationToken);
            return outcome switch
            {
                GetScoutingReportHandler.Outcome.Written => Results.Ok(report),
                GetScoutingReportHandler.Outcome.UnknownModel => Results.NotFound(),
                // Not an error: the model simply has too little judged history, or a new report
                // cannot be paid for right now. The profile shows no section.
                GetScoutingReportHandler.Outcome.TooFewDuels or GetScoutingReportHandler.Outcome.NotAllowed => Results.NoContent(),
                _ => Results.Problem(
                    title: "Could not write the scouting report",
                    detail: "The model did not answer. Try again later.",
                    statusCode: StatusCodes.Status502BadGateway),
            };
        })
        .RequireRateLimiting(DuelsEndpoints.StartRateLimitPolicy)
        .WithName("GetScoutingReport")
        .WithSummary("Summarises what the judge's notes say a model is reliably good and bad at.")
        .Produces<ScoutingReportDto>()
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status429TooManyRequests)
        .Produces(StatusCodes.Status502BadGateway);

        // The /h2h/{a}/{b} endpoint and its handler are gone with the /h2h page. The kill-list
        // above already answers "how do these two compare" for every opponent at once, and it
        // does so without a second model lookup that 404s whenever either id has been retired.
        return app;
    }
}
