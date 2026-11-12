using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Presentation;

namespace PoLocalCompare.Unit;

/// <summary>
/// Pins the precedence the Arena's status band depends on. It used to be encoded by the order
/// of a dozen @if arms, which nothing could test; these are the orderings that were load-bearing.
/// </summary>
public class ArenaPhaseResolverTests
{
    private static readonly ArenaPhaseInputs FinishedUnjudged = new()
    {
        DuelCompleted = true,
        AnyResultPresent = true,
        DuelVerdict = DuelVerdict.Pending,
    };

    private static ArenaPhaseInputs Scenario(string name) => name switch
    {
        // A recorded verdict outranks a duel that still looks like it is streaming.
        "human verdict while streaming" => FinishedUnjudged with
        { DuelStillRunning = true, VerdictRecorded = true, Verdict = DuelVerdict.Left, VerdictSource = VerdictSource.Human },
        // Unconfirmed: no attribution until the server says who decided.
        "optimistic ai verdict" => FinishedUnjudged with
        { VerdictRecorded = true, Optimistic = true, Verdict = DuelVerdict.Left, VerdictSource = VerdictSource.Ai },
        // No model's output was read — never presented as a judgement.
        "constraint verdict" => FinishedUnjudged with
        { VerdictRecorded = true, Verdict = DuelVerdict.Right, VerdictSource = VerdictSource.Constraint },
        "both failed during countdown" => FinishedUnjudged with
        { LeftFailed = true, RightFailed = true, AutoJudgeRemaining = 5 },
        // One result in and not terminal: no countdown, however long the window claims to be.
        "one side done, not completed" => FinishedUnjudged with
        { DuelCompleted = false, AutoJudgeRemaining = 5 },
        "judge deciding beats countdown" => FinishedUnjudged with
        { AutoJudgeDeciding = true, AutoJudgeRemaining = 1 },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [InlineData("human verdict while streaming", ArenaPhase.VerdictByHuman)]
    [InlineData("optimistic ai verdict", ArenaPhase.VerdictOptimistic)]
    [InlineData("constraint verdict", ArenaPhase.VerdictWalkover)]
    [InlineData("both failed during countdown", ArenaPhase.BothSidesFailed)]
    [InlineData("one side done, not completed", ArenaPhase.WaitingForOtherModel)]
    [InlineData("judge deciding beats countdown", ArenaPhase.JudgeDeciding)]
    public void Resolve_AppliesThePrecedence(string scenario, ArenaPhase expected) =>
        Assert.Equal(expected, ArenaPhaseResolver.Resolve(Scenario(scenario)));
}
