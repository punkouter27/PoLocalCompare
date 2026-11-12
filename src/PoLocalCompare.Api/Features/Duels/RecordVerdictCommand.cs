using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

/// <param name="Source">
/// Defaults to Human so every existing caller keeps its meaning; only the auto-judge passes Ai.
/// </param>
/// <param name="Actor">
/// preferred_username of the caller, or "anonymous" when the open gate is in effect. Stamped
/// onto <c>Duel.VerdictBy</c> for forensic audit. The AI judge always passes null (the
/// <c>JudgeModel</c> field already names the deployment that decided).
/// </param>
public sealed record RecordVerdictCommand(
    DuelId DuelId,
    DuelVerdict Verdict,
    VerdictSource Source = VerdictSource.Human,
    string? JudgeRationale = null,
    string? JudgeModel = null,
    string? Actor = null,
    string? JudgeAnalysis = null,
    double? JudgeCostUsd = null);
