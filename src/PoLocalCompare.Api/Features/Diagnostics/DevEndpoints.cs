namespace PoLocalCompare.Api.Features.Diagnostics;

/// <summary>
/// Development-only tooling. Gated twice on purpose: Program.cs maps these only in Development,
/// and RequireAuthorization puts them behind the same session every other write needs — they were
/// AllowAnonymous until 2026-08-23. Locally the fake-auth header satisfies it.
/// </summary>
/// <remarks>
/// Judge calibration is the one tool left. Reset and cost repricing went on 2026-10-26; this was
/// cut the same day and restored within hours, because measuring an LLM judge against human
/// labels is how a benchmark whose ratings come from that judge keeps itself honest.
/// </remarks>
public static class DevEndpoints
{
    public static IEndpointRouteBuilder MapDevEndpoints(this IEndpointRouteBuilder app)
    {
        // Judge-vs-human agreement (raw and Cohen's κ) over the newest human-decided duels. Every
        // sampled duel costs real judge calls, so it is dev-only. Run before and after any judge
        // change. deployment / vision replay the same sample through a different judge, so
        // candidates can be compared on agreement before AiJudge:* is changed.
        app.MapPost("/api/dev/judge-calibration", async (
            JudgeCalibrationHandler handler, int? take, string? deployment, bool? vision, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(take ?? 20, deployment, vision, ct))).RequireAuthorization();

        return app;
    }
}
