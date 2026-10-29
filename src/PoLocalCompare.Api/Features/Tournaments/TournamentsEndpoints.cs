using Microsoft.AspNetCore.Mvc;
using PoLocalCompare.Api.Auth;
using static PoLocalCompare.Api.Auth.AnonymousWrites;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Tournaments;

public static class TournamentsEndpoints
{
    /// <param name="allowAnonymousWrites">
    /// Mirrors the duels slice: creating a tournament creates duels, so it sits behind the same
    /// gate rather than a looser one of its own.
    /// </param>
    public static IEndpointRouteBuilder MapTournamentsEndpoints(
        this IEndpointRouteBuilder app,
        bool allowAnonymousWrites)
    {
        var group = app.MapGroup("/api/tournaments").WithTags("Tournaments").RequireAuthorization();

        OpenIf(allowAnonymousWrites, group.MapPost("/", async (
            [FromBody] CreateTournamentRequest request,
            HttpContext httpContext,
            [FromServices] CreateTournamentHandler handler,
            [FromServices] TournamentRepository repository,
            [FromServices] TournamentRunner runner,
            [FromServices] SpendGuard spendGuard,
            [FromServices] IBackgroundTaskQueue taskQueue) =>
        {
            // Whatever the field: a bracket cannot run without the judge, and the judge bills.
            if (await spendGuard.IsOverBudgetAsync())
                return SpendGuard.Refusal();

            try
            {
                var actor = IdentityResolver.ResolveActor(httpContext.User);

                // One tournament at a time. The runner holds the single-consumer
                // BackgroundTaskService slot for the whole life of a bracket, so a second
                // create would queue behind it and look stuck from the page — which is the
                // bug this guard prevents. The runner's own resume-on-restart re-queues any
                // non-terminal bracket via TournamentResumeService, so the only thing we lose
                // here is the ability to start a fresh bracket while another is still playing.
                var inFlight = await repository.ListRecentAsync(50);
                var blocking = inFlight.FirstOrDefault(t =>
                    t.Status is TournamentStatus.Pending or TournamentStatus.Running);
                if (blocking is not null)
                {
                    return Results.Conflict(new
                    {
                        title = "A tournament is already running.",
                        detail = $"Wait for '{blocking.TournamentId.Value}' to finish before drawing another.",
                        tournamentId = blocking.TournamentId.Value,
                    });
                }

                var dto = await handler.HandleAsync(
                    request.ModelIds ?? [],
                    request.PromptText ?? string.Empty,
                    actor,
                    request.Format ?? TournamentFormat.Bracket);

                // Queued rather than awaited: a bracket is up to seven duels and takes minutes.
                // The response carries the drawn bracket so the page can render it immediately
                // and then watch it fill in. Per-match items (queued by the runner itself) yield
                // the slot back to the queue between duels, so a sibling tournament created
                // moments later can start while this one's first match is judging.
                taskQueue.QueueBackgroundWork(ct => runner.RunAsync(dto.TournamentId, ct));

                return Results.Created($"/api/tournaments/{dto.TournamentId}", dto);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [ex.ParamName ?? "request"] = [ex.Message],
                });
            }
        }))
        .RequireRateLimiting(DuelsEndpoints.StartRateLimitPolicy)
        .WithName("CreateTournament")
        .WithSummary("Draws a seeded bracket (or, with format=Swiss, a three-round Swiss) and starts running it.")
        .Produces<TournamentDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesValidationProblem();

        group.MapGet("/entrants", async ([FromServices] CreateTournamentHandler handler) =>
            Results.Ok(await handler.ListEntrantsAsync()))
        .WithName("ListTournamentEntrants")
        .WithSummary("Lists the models eligible to enter a bracket, strongest first.")
        .Produces<IReadOnlyList<TournamentEntrantDto>>();

        group.MapGet("/{tournamentId}", async (
            TournamentId tournamentId,
            [FromServices] TournamentRepository repository) =>
        {
            var tournament = await repository.GetByIdAsync(tournamentId);
            return tournament is null ? Results.NotFound() : Results.Ok(tournament.ToDto());
        })
        .WithName("GetTournament")
        .WithSummary("Returns one bracket and the state of every match in it.")
        .Produces<TournamentDto>()
        .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/", async (
            [FromQuery] int? limit,
            [FromServices] TournamentRepository repository) =>
        {
            var tournaments = await repository.ListRecentAsync(Math.Clamp(limit ?? 10, 1, 50));
            return Results.Ok(tournaments.Select(t => t.ToDto()).ToList());
        })
        .WithName("ListTournaments")
        .WithSummary("Returns recent bracket runs, newest first.")
        .Produces<IReadOnlyList<TournamentDto>>();

        return app;
    }
}

/// <param name="ModelIds">
/// The field, in any order — the handler seeds it by rating. Must hold 2, 4 or 8 distinct ids.
/// </param>
/// <param name="PromptText">The single prompt every match in the bracket receives.</param>
/// <param name="Format">"Bracket" (the default when omitted) or "Swiss" (exactly 8 models).</param>
public sealed record CreateTournamentRequest(
    IReadOnlyList<ModelId>? ModelIds,
    string? PromptText,
    TournamentFormat? Format = null);
