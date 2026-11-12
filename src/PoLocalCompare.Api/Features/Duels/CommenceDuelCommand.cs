// SOLID: Single Responsibility
using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Prompts;

namespace PoLocalCompare.Api.Features.Duels;

/// <param name="AutoJudgeDelaySecondsOverride">
/// Replaces <c>AiJudge:DelaySeconds</c> for this duel only. A tournament passes 0 so the judge
/// decides the instant a match finishes; leaving it null keeps the configured grace window, which
/// is what a person judging by hand needs. It is never persisted — it governs this execution,
/// not the duel record.
/// </param>
/// <param name="Actor">
/// preferred_username of the caller, or "anonymous" when the open gate is in effect. Stamped
/// onto <c>Duel.OwnerId</c> for forensic audit. Never used for authorization.
/// </param>
public sealed record CommenceDuelCommand(
    ModelId LeftModelId,
    ModelId RightModelId,
    string PromptText,
    int? AutoJudgeDelaySecondsOverride = null,
    string? Actor = null)
{
    // Re-exported from PromptRules so existing call-sites keep their `CommenceDuelCommand.MinPromptLength`
    // shape — the canonical location is the shared one because the client also reads it.
    public const int MaxPromptLength = PromptRules.MaxPromptLength;
    public const int MinPromptLength = PromptRules.MinPromptLength;
}
