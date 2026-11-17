using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Diagnostics;

/// <summary>
/// Development-only tooling. Gated twice on purpose: Program.cs maps these only in Development,
/// and RequireAuthorization puts them behind the same session every other write needs — they were
/// AllowAnonymous until 2026-08-23. Locally the fake-auth header satisfies it.
/// </summary>
/// <remarks>
/// Judge calibration was the one tool left after reset and cost repricing went on 2026-10-26; it
/// was cut the same day and restored within hours, because measuring an LLM judge against human
/// labels is how a benchmark whose ratings come from that judge keeps itself honest.
/// The duel fixture joined it on 2026-11-17 so the E2E-UI suite can stand on its own.
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

        // A finished duel for the E2E-UI suite, written straight to storage with no inference,
        // no judge and no rating change. The Arena and Archive tests used to need a duel someone
        // had run by hand beforehand and failed on a fresh store; this makes the suite
        // self-sufficient. Voided on purpose: terminal (the startup sweeper never re-judges it)
        // and it banks nothing — no ELO, no duel count, no history row.
        app.MapPost("/api/dev/seed-duel", async (
            IModelRepository models,
            IDuelRepository duels,
            IDuelResultRepository results) =>
        {
            var pair = (await models.GetAllAsync())
                .Where(m => !ModelSeeder.RetiredIds.Contains(m.ModelId))
                .OrderBy(m => m.ModelId)
                .Take(2)
                .ToList();
            if (pair.Count < 2)
                return Results.Conflict(new { error = "Need at least two registered models to seed a duel." });

            var duel = DuelFixture.Build(pair[0], pair[1]);
            await duels.SaveAsync(duel);
            foreach (var model in pair)
                await results.SaveAsync(DuelFixture.Result(duel.DuelId, model.ModelId));

            return Results.Created($"/api/duels/{duel.DuelId}", new { duelId = duel.DuelId.ToString() });
        }).RequireAuthorization();

        return app;
    }
}

/// <summary>The rows <c>POST /api/dev/seed-duel</c> writes. Plainly labelled as a fixture.</summary>
internal static class DuelFixture
{
    public const string Prompt = "[E2E fixture] Build a single-page HTML counter with + and − buttons.";

    public static Duel Build(Model left, Model right)
    {
        var now = DateTimeOffset.UtcNow;
        return new Duel(DuelId.New(), Prompt, Prompt, left.ModelId, right.ModelId)
        {
            LeftModelName = left.DisplayName,
            RightModelName = right.DisplayName,
            CompletedAt = now,
            Verdict = DuelVerdict.Voided,
            VerdictSource = VerdictSource.Constraint,
            JudgeStoodDownReason = "Seeded by the E2E fixture endpoint; never judged.",
            OwnerId = "e2e-fixture",
        };
    }

    public static DuelResult Result(DuelId duelId, ModelId modelId)
    {
        const string html = "<!DOCTYPE html><html><body><button>−</button> <span>0</span> <button>+</button></body></html>";
        return new DuelResult(duelId, modelId)
        {
            HtmlOutputRaw = html,
            HtmlOutputSizeBytes = html.Length,
            TokenCount = 32,
            TotalDurationMs = 1000,
            GenerationDurationMs = 1000,
            TokenVelocity = 32,
            FinishReason = "stop",
        };
    }
}
