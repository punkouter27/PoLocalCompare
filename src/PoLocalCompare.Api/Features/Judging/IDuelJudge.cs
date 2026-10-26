// SOLID: Dependency Inversion — the auto-judge orchestration does not know how judging happens
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Judging;

/// <param name="Verdict"><see cref="DuelVerdict.Left"/>, <see cref="DuelVerdict.Right"/>, <see cref="DuelVerdict.Tie"/>, or <see cref="DuelVerdict.Voided"/>.</param>
/// <param name="Rationale">One sentence, shown in the Arena and stored on the duel.</param>
/// <param name="IsWalkover">
/// True when the decision is mechanical — an opponent that produced nothing, or a duel voided
/// for missing results — rather than the output of an LLM comparison. The auto-judge uses it to
/// stamp <see cref="VerdictSource.Constraint"/> and a null judge model, so a forfeit can never
/// masquerade in analytics as "the AI judge read both pages and preferred this one".
/// </param>
/// <param name="Analysis">
/// The judge's own checklist — each requested element and which page delivered it — written
/// before it picked a winner. It was scratch work, parsed and thrown away, until 2026-10-11;
/// it is the evidence behind <paramref name="Rationale"/> and already paid for, so it is kept
/// and shown. Null for a walkover. A tie called because the two orderings disagreed carries
/// both, each labelled with its order — they argue for different pages, and that is the why.
/// </param>
/// <param name="CostUsd">What the judge calls behind this decision cost, when the deployment is in the price book.</param>
/// <param name="Model">
/// The deployment that made the deciding call — "primary + cross-check" when two families agreed
/// on the first pass, the rung's deployment when a split was settled further up the ladder
/// (<see cref="AutoJudgeOptions.EscalationDeployment"/>, <see cref="AutoJudgeOptions.TiebreakDeployment"/>);
/// null when no model was involved.
/// </param>
public sealed record JudgeDecision(
    DuelVerdict Verdict,
    string Rationale,
    bool IsWalkover = false,
    string? Analysis = null,
    double? CostUsd = null,
    string? Model = null);

/// <summary>
/// Raised when a judge call hit a recoverable upstream failure — the most common case is an
/// HTTP 429 with a <c>Retry-After</c> header, but the same signal fits an empty response from
/// a temporarily-throttled deployment. Callers may choose to retry after <see cref="RetryAfter"/>
/// has elapsed; the judge itself has already exhausted its own fast retry policy.
/// </summary>
/// <remarks>
/// Distinct from a returned <c>null</c> decision, which signals "no evidence, do not move ELO".
/// A <see cref="JudgeRateLimitedException"/> is "evidence pending, please try again shortly".
/// AutoJudge translates the former into "leave the duel Pending"; the latter into "re-queue
/// after the requested delay".
/// </remarks>
public sealed class JudgeRateLimitedException : Exception
{
    public JudgeRateLimitedException(TimeSpan retryAfter, string reason)
        : base($"Judge rate-limited: {reason}")
    {
        RetryAfter = retryAfter;
    }

    /// <summary>Suggested wait before the next attempt. Zero or negative means "no hint".</summary>
    public TimeSpan RetryAfter { get; }
}

/// <summary>One side's output and the facts about it the judge cannot see in the markup.</summary>
/// <param name="WasTruncated">
/// The model hit its output-token limit, so the page stops mid-document. The telemetry HUD has
/// always shown this; until 2026-10-26 the judge was the one reader that never heard it.
/// </param>
public sealed record JudgedPage(string Html, bool WasTruncated = false);

/// <summary>
/// Decides which of two outputs follows the prompt more accurately.
/// </summary>
public interface IDuelJudge
{
    /// <summary>
    /// Returns null when no decision could be reached on the merits — an unparseable reply,
    /// or a genuinely undecidable pairing. Callers must leave the duel Pending in that case
    /// rather than guessing; a coin-flip verdict would move ELO on no evidence.
    /// </summary>
    /// <exception cref="JudgeRateLimitedException">
    /// Thrown when the upstream rate-limited the request. Distinct from a null return:
    /// a null says "no evidence", a rate limit says "evidence pending, please retry".
    /// </exception>
    Task<JudgeDecision?> JudgeAsync(
        string promptFull,
        JudgedPage left,
        JudgedPage right,
        CancellationToken cancellationToken);
}
