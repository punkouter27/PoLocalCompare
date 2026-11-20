using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.Presentation;

/// <summary>
/// What the Arena's status band is saying right now — the single question the page answers
/// between the two viewport panels and the vote buttons.
/// </summary>
/// <remarks>
/// <para>
/// This replaces nine independent booleans and nullables on <c>Arena.razor</c> —
/// <c>_duelStillRunning</c>, <c>_verdictRecorded</c>, <c>_optimistic</c>, <c>_verdictValue</c>,
/// <c>_verdictSource</c>, <c>_autoJudgeDeciding</c>, <c>_autoJudgeRemaining</c>,
/// <c>WaitingForOtherModel</c> and <c>BothSidesFailed()</c> — which the markup read across
/// roughly a dozen mutually exclusive <c>@if</c>/<c>else if</c> arms in two separate blocks.
/// </para>
/// <para>
/// Nine flags describe 2^9 combinations; the page has thirteen states. Most of that space was
/// unreachable, but nothing said which parts, and the arms that kept it consistent did so by
/// ORDER rather than by construction — the same trap already documented on Home's
/// <c>CompareHint</c>, where checking the arms in the obvious sequence told a user who had
/// picked two models that they had picked none. Ordering is a property you cannot test and
/// cannot see; an enum is both.
/// </para>
/// <para>
/// It lives in Shared rather than beside the page for the reason AGENTS.md gives: the Unit
/// tier references only the Api project, so anything pure under <c>PoLocalCompare.Client/</c>
/// is reachable by no suite except E2E-UI, which CI never runs. A resolver that is a pure
/// function of thirteen inputs is exactly the shape that wants a table test.
/// </para>
/// </remarks>
public enum ArenaPhase
{
    /// <summary>Nothing to report — no result yet, no judging, no verdict.</summary>
    Idle,

    /// <summary>Inference is still streaming into one or both panels.</summary>
    Running,

    /// <summary>
    /// One side crossed the line and the duel is not terminal: the other model is still
    /// working. Distinct from <see cref="AwaitingHumanPick"/> because no judging can start
    /// yet — this state used to fall through to the countdown, under vote buttons that the
    /// server answered with a 409.
    /// </summary>
    WaitingForOtherModel,

    /// <summary>Both models failed. There is nothing to judge and no rating can move.</summary>
    BothSidesFailed,

    /// <summary>The grace window expired and the AI judge is deciding now.</summary>
    JudgeDeciding,

    /// <summary>The grace window is running; a human pick inside it still wins.</summary>
    JudgeCountdown,

    /// <summary>A result is in and a human may pick, with no countdown running.</summary>
    AwaitingHumanPick,

    /// <summary>A click has flipped the UI and the server has not confirmed it yet.</summary>
    VerdictOptimistic,

    /// <summary>Terminal with nothing to judge: no winner, no rating moved.</summary>
    VerdictVoided,

    /// <summary>Both outputs judged equivalent. No winner to name, no rating moved.</summary>
    VerdictTie,

    /// <summary>
    /// Won by walkover — <see cref="VerdictSource.Constraint"/>, which means no model's output
    /// was read at all. Said plainly so it is never mistaken for a judgement about quality.
    /// </summary>
    VerdictWalkover,

    /// <summary>Decided by the AI judge.</summary>
    VerdictByAi,

    /// <summary>Decided by a human inside the grace window.</summary>
    VerdictByHuman,
}

/// <summary>
/// Everything <see cref="ArenaPhaseResolver.Resolve"/> reads. A struct of named fields rather
/// than a dozen positional parameters, so a call site cannot silently transpose two bools.
/// </summary>
public readonly record struct ArenaPhaseInputs
{
    /// <summary>Inference is still streaming (no <c>DuelComplete</c> yet).</summary>
    public bool DuelStillRunning { get; init; }

    /// <summary>A verdict has been written, or optimistically assumed.</summary>
    public bool VerdictRecorded { get; init; }

    /// <summary>The recorded verdict is a local prediction awaiting server confirmation.</summary>
    public bool Optimistic { get; init; }

    /// <summary>The recorded verdict itself. A tie is a real verdict with no winner.</summary>
    public DuelVerdict Verdict { get; init; }

    /// <summary>Who decided: human, AI, or constraint (walkover).</summary>
    public VerdictSource VerdictSource { get; init; }

    /// <summary>The duel row's own verdict field, which lags the optimistic flip.</summary>
    public DuelVerdict DuelVerdict { get; init; }

    /// <summary>Whether the duel has a <c>CompletedAt</c> — i.e. is terminal.</summary>
    public bool DuelCompleted { get; init; }

    /// <summary>Both sides returned a failure.</summary>
    public bool LeftFailed { get; init; }

    /// <summary>Both sides returned a failure.</summary>
    public bool RightFailed { get; init; }

    /// <summary>At least one result row has landed.</summary>
    public bool AnyResultPresent { get; init; }

    /// <summary>The AI judge is deciding right now.</summary>
    public bool AutoJudgeDeciding { get; init; }

    /// <summary>Seconds left in the grace window, or null when no window is running.</summary>
    public int? AutoJudgeRemaining { get; init; }
}

/// <summary>Turns <see cref="ArenaPhaseInputs"/> into the one state the page is in.</summary>
public static class ArenaPhaseResolver
{
    /// <summary>
    /// Resolves the Arena's status band. Pure, total, and deterministic: every input
    /// combination maps to exactly one <see cref="ArenaPhase"/>.
    /// </summary>
    /// <remarks>
    /// The precedence below is the same precedence the markup's arm order used to encode, with
    /// one difference: it is stated once, here, instead of being spread over two <c>@if</c>
    /// chains in a 1,388-line file where re-ordering an arm by accident was invisible.
    ///
    /// A recorded verdict outranks everything, including a duel that is somehow still
    /// streaming, because that is the app's central invariant — a human decision always wins
    /// the race, and once <c>RecordVerdictHandler</c> has written one nothing may present the
    /// duel as still open.
    /// </remarks>
    public static ArenaPhase Resolve(in ArenaPhaseInputs i)
    {
        if (i.VerdictRecorded)
        {
            // Optimistic first: what is on screen is a prediction, and saying which judge
            // decided would be inventing an attribution the server has not confirmed.
            if (i.Optimistic) return ArenaPhase.VerdictOptimistic;

            if (i.Verdict == DuelVerdict.Voided) return ArenaPhase.VerdictVoided;
            if (i.Verdict == DuelVerdict.Tie) return ArenaPhase.VerdictTie;

            return i.VerdictSource switch
            {
                VerdictSource.Constraint => ArenaPhase.VerdictWalkover,
                VerdictSource.Ai => ArenaPhase.VerdictByAi,
                _ => ArenaPhase.VerdictByHuman,
            };
        }

        if (i.DuelStillRunning) return ArenaPhase.Running;

        // Everything below is an unjudged, non-streaming duel. The markup guarded this whole
        // group on `_duel.Verdict == Pending`; anything else here is a duel whose verdict
        // exists on the server but has not reached this page yet, and the honest thing to
        // show for that is nothing rather than a countdown that cannot fire.
        if (i.DuelVerdict != DuelVerdict.Pending) return ArenaPhase.Idle;

        if (i.LeftFailed && i.RightFailed) return ArenaPhase.BothSidesFailed;

        // Before the countdown: a duel with one result and no CompletedAt has nothing to judge
        // yet, however long the grace window claims to be.
        if (!i.DuelCompleted) return ArenaPhase.WaitingForOtherModel;

        if (i.AutoJudgeDeciding) return ArenaPhase.JudgeDeciding;
        if (i.AutoJudgeRemaining is not null) return ArenaPhase.JudgeCountdown;
        if (i.AnyResultPresent) return ArenaPhase.AwaitingHumanPick;

        return ArenaPhase.Idle;
    }

    /// <summary>
    /// Whether the judge's reticle sweeps the panels: the grace window is running or the AI
    /// judge is deciding. Derived from the phase so it cannot disagree with the band.
    /// </summary>
    public static bool JudgeIsLooking(ArenaPhase phase) =>
        phase is ArenaPhase.JudgeCountdown or ArenaPhase.JudgeDeciding;
}
