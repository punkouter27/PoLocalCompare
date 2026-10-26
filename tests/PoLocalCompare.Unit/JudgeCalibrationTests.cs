using PoLocalCompare.Api.Features.Judging;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Unit;

/// <summary>
/// The judge's agreement with humans is the benchmark's own quality measure, and raw agreement
/// flatters a judge on a lopsided sample. κ is what calibration reports for that reason.
/// </summary>
public class JudgeCalibrationTests
{
    [Fact]
    public void Kappa_IsZeroForAJudgeThatAlwaysSaysLeft_HoweverHighItsRawAgreement()
    {
        // Humans picked Left 8 times in 10. A judge that always answers Left agrees 80% of the
        // time and has learned nothing.
        var pairs = Enumerable.Repeat((DuelVerdict.Left, DuelVerdict.Left), 8)
            .Concat(Enumerable.Repeat((DuelVerdict.Right, DuelVerdict.Left), 2))
            .ToList();
        Assert.Equal(0, JudgeCalibrationHandler.Kappa(pairs));

        // Perfect agreement over mixed labels is 1; nothing decided is no measurement at all.
        Assert.Equal(1, JudgeCalibrationHandler.Kappa(
            [(DuelVerdict.Left, DuelVerdict.Left), (DuelVerdict.Right, DuelVerdict.Right), (DuelVerdict.Tie, DuelVerdict.Tie)]));
        Assert.Null(JudgeCalibrationHandler.Kappa([]));
    }
}
