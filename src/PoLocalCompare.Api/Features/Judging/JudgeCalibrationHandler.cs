using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Judging;

/// <param name="Human">What the person picked.</param>
/// <param name="Judge">What the judge picks today, or null when it could not decide.</param>
public sealed record JudgeCalibrationRow(string DuelId, DuelVerdict Human, DuelVerdict? Judge, string? Rationale);

/// <param name="AgreementRate">Agreed / Decided, or null when the judge decided nothing.</param>
/// <param name="Kappa">
/// Cohen's κ over the decided rows: agreement corrected for what the two raters' own Left / Right /
/// Tie frequencies would produce by chance. Raw agreement flatters a judge on a lopsided sample —
/// if humans picked Left 80% of the time, a judge that always says Left agrees 80% and is
/// worthless (κ = 0). Read it as ≥ 0.6 substantial, 0.4–0.6 moderate, below that weak. Null when
/// nothing was decided or chance agreement is total (every label the same).
/// </param>
public sealed record JudgeCalibrationReport(
    string JudgeModel,
    bool Vision,
    int Sampled,
    int Decided,
    int Agreed,
    double? AgreementRate,
    double? Kappa,
    double? JudgeCostUsd,
    IReadOnlyList<JudgeCalibrationRow> Rows);

/// <summary>
/// Replays the current judge over duels a human decided and reports how often it agrees.
/// </summary>
/// <remarks>
/// Human verdicts are the only labelled data this app has, so they are the only way to tell
/// whether a judge change — a new prompt, both-orderings agreement, screenshots, a cheaper
/// deployment — made verdicts better or merely different. Read-only: the replay goes through
/// <see cref="IDuelJudge"/> directly, never <see cref="AutoJudge"/> or RecordVerdictHandler,
/// so nothing it produces can reach a duel row or ELO. Calls run one at a time so a sample
/// does not trip the judge's rate limit; a 429 ends the sample early with what it has.
/// </remarks>
public sealed class JudgeCalibrationHandler(
    IDuelRepository duels,
    IDuelResultRepository results,
    IDuelJudge judge,
    Microsoft.Extensions.Options.IOptions<AutoJudgeOptions> options)
{
    public const int MaxTake = 50;

    /// <param name="deployment">A candidate judge to replay the sample through; null uses the configured one.</param>
    /// <param name="vision">Force screenshots on or off for this run; null uses <c>AiJudge:VisionEnabled</c>.</param>
    public async Task<JudgeCalibrationReport> HandleAsync(
        int take, string? deployment, bool? vision, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, MaxTake);
        var useVision = vision ?? options.Value.VisionEnabled;
        var overriding = !string.IsNullOrWhiteSpace(deployment) || vision.HasValue;
        double cost = 0;

        // Newest decided duels; the source filter is client-side because the list query only
        // filters on verdict. 500 is a scan cap, not a sample size.
        var decided = await duels.ListAsync(500, beforeMonth: null,
            verdicts: [DuelVerdict.Left, DuelVerdict.Right, DuelVerdict.Tie]);

        var rows = new List<JudgeCalibrationRow>();
        foreach (var duel in decided.Where(d => d.VerdictSource == VerdictSource.Human))
        {
            if (rows.Count >= take) break;

            var pair = await results.GetByDuelIdAsync(duel.DuelId);
            var left = pair.FirstOrDefault(r => r.ModelId == duel.LeftModelId);
            var right = pair.FirstOrDefault(r => r.ModelId == duel.RightModelId);
            // A walkover is arithmetic, not a judgment — nothing to calibrate against.
            if (left is null || right is null || left.IsFailure || right.IsFailure
                || string.IsNullOrWhiteSpace(left.HtmlOutputRaw) || string.IsNullOrWhiteSpace(right.HtmlOutputRaw))
                continue;

            JudgeDecision? decision;
            try
            {
                var prompt = string.IsNullOrWhiteSpace(duel.PromptFull) ? duel.PromptText : duel.PromptFull;
                // The override lives on the concrete judge, not on IDuelJudge: it is a
                // calibration tool, and a test double has no second deployment to switch to.
                var leftPage = new JudgedPage(left.HtmlOutputRaw, left.WasTruncated);
                var rightPage = new JudgedPage(right.HtmlOutputRaw, right.WasTruncated);
                decision = overriding && judge is FoundryDuelJudge foundry
                    ? await foundry.JudgeAsync(prompt, leftPage, rightPage, deployment, vision, cancellationToken)
                    : await judge.JudgeAsync(prompt, leftPage, rightPage, cancellationToken);
                cost += decision?.CostUsd ?? 0;
            }
            catch (JudgeRateLimitedException)
            {
                break;
            }

            rows.Add(new JudgeCalibrationRow(duel.DuelId.ToString(), duel.Verdict, decision?.Verdict, decision?.Rationale));
        }

        var judged = rows.Count(r => r.Judge is not null);
        var agreed = rows.Count(r => r.Judge == r.Human);
        return new JudgeCalibrationReport(
            string.IsNullOrWhiteSpace(deployment) ? options.Value.EffectiveDeployment(useVision) : deployment.Trim(),
            useVision,
            rows.Count, judged, agreed,
            judged == 0 ? null : Math.Round((double)agreed / judged, 3),
            Kappa(rows.Where(r => r.Judge is not null).Select(r => (r.Human, r.Judge!.Value)).ToList()),
            cost > 0 ? Math.Round(cost, 6) : null,
            rows);
    }

    /// <summary>
    /// Cohen's κ = (p_o − p_e) / (1 − p_e): observed agreement against the agreement two raters
    /// with these label frequencies would reach by chance. See <see cref="JudgeCalibrationReport.Kappa"/>.
    /// </summary>
    internal static double? Kappa(IReadOnlyList<(DuelVerdict Human, DuelVerdict Judge)> pairs)
    {
        if (pairs.Count == 0) return null;
        double n = pairs.Count;
        var observed = pairs.Count(p => p.Human == p.Judge) / n;
        var chance = pairs.Select(p => p.Human).Concat(pairs.Select(p => p.Judge)).Distinct()
            .Sum(v => pairs.Count(p => p.Human == v) / n * (pairs.Count(p => p.Judge == v) / n));
        return chance >= 1 ? null : Math.Round((observed - chance) / (1 - chance), 3);
    }
}
