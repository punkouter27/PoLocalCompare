using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Prompts;

public static class PromptsEndpoints
{
    public static IEndpointRouteBuilder MapPromptsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/prompts").WithTags("Prompts").RequireAuthorization();

        // Paid inference, so it carries the same two guards as starting a duel: the per-user
        // rate limit and the daily spend cap. With Features:UseRealAi off it refuses rather
        // than calling Foundry — the MOCK chip says remote responses are simulated, and there
        // is no honest simulated answer to "rewrite my prompt".
        group.MapPost("/refine", async (
            [FromBody] RefinePromptRequest request,
            [FromServices] IValidator<RefinePromptRequest> validator,
            [FromServices] RefinePromptHandler handler,
            [FromServices] SpendGuard spend,
            [FromServices] IConfiguration configuration,
            CancellationToken ct) =>
        {
            var validation = await validator.ValidateAsync(request, ct);
            if (!validation.IsValid)
            {
                return Results.ValidationProblem(validation.ToDictionary());
            }

            var prompt = request.Prompt?.Trim() ?? string.Empty;

            if (!configuration.GetValue("Features:UseRealAi", false))
            {
                return Results.Problem(
                    title: "Unavailable in mock mode",
                    detail: "Sharpening a prompt calls a real model, and this instance is running with mock inference.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (await spend.IsOverBudgetAsync(ct)) return SpendGuard.Refusal();

            var refined = await handler.HandleAsync(prompt, ct);
            return refined is null
                ? Results.Problem(
                    title: "Could not sharpen the prompt",
                    detail: "The model did not answer. Your prompt is unchanged; try again or run it as it is.",
                    statusCode: StatusCodes.Status502BadGateway)
                : Results.Ok(new RefinePromptResponse(refined));
        })
        .RequireRateLimiting(DuelsEndpoints.StartRateLimitPolicy)
        .WithName("RefinePrompt")
        .WithSummary("Rewrites a rough prompt into a brief with explicit, checkable requirements.")
        .Produces<RefinePromptResponse>()
        .ProducesValidationProblem()
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status429TooManyRequests)
        .Produces(StatusCodes.Status502BadGateway);

        return app;
    }
}
