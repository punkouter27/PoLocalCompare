using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Presentation;

namespace PoLocalCompare.Unit;

/// <summary>
/// The race's words were private members of Arena.razor.cs until 2026-10-26, so nothing pinned
/// the two rules a live region depends on: a pace gap below 15% is not "faster", and once a side
/// finishes the sentence says so whatever the rates are.
/// </summary>
public class RaceCommentaryTests
{
    [Fact]
    public void Commentary_CallsPaceOnlyPastTheNoiseAndFinishingOverridesIt()
    {
        var left = new SideMetrics { Status = DuelStatus.Generating, TokenVelocity = 30, TokenCount = 400 };
        var right = new SideMetrics { Status = DuelStatus.Generating, TokenVelocity = 20, TokenCount = 200 };

        Assert.Equal("A is 1.5× faster right now", RaceCommentary.Ticker(left, right, "A", "B"));
        Assert.Equal(Side.Left, RaceCommentary.Leader(left, right));
        // The leader's marker stops short of the end, so it still looks like it is moving.
        Assert.Equal(88, RaceCommentary.Progress(Side.Left, left, right));
        Assert.Equal(44, RaceCommentary.Progress(Side.Right, left, right));

        // 10% apart: no lead, and the ticker falls back to the token gap.
        right.TokenVelocity = 27;
        Assert.Null(RaceCommentary.Leader(left, right));
        Assert.Equal("Dead heat on pace — A leads by 200 tokens", RaceCommentary.Ticker(left, right, "A", "B"));

        right.Status = DuelStatus.Done;
        Assert.Equal("B finished first — A still generating", RaceCommentary.Ticker(left, right, "A", "B"));
        Assert.Equal("B finished first — A still generating", RaceCommentary.Announcement(left, right, "A", "B", Side.Left));
        Assert.Equal(100, RaceCommentary.Progress(Side.Right, left, right));
    }
}
