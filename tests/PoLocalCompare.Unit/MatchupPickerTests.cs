using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Presentation;
using static PoLocalCompare.Shared.Presentation.MatchupPicker;

namespace PoLocalCompare.Unit;

/// <summary>
/// Home's "Suggest matchup": the duel the leaderboard learns most from is a close one between
/// two unsettled ratings — not a mismatch, and not two models it already knows well.
/// </summary>
public class MatchupPickerTests
{
    private static readonly Candidate SettledA = new(ModelId.From("settled-a"), 1200, 40);
    private static readonly Candidate SettledB = new(ModelId.From("settled-b"), 1210, 40);
    private static readonly Candidate Newcomer = new(ModelId.From("newcomer"), 1200, 784);
    private static readonly Candidate Newcomer2 = new(ModelId.From("newcomer-2"), 1190, 600);
    private static readonly Candidate Champion = new(ModelId.From("champion"), 1900, 784);

    [Fact]
    public void Suggest_PrefersTheCloseUnsettledPair_OverAMismatchOrTwoSettledModels()
    {
        var pick = MatchupPicker.Suggest([SettledA, SettledB, Newcomer, Newcomer2, Champion]);

        Assert.NotNull(pick);
        Assert.Equal(
            new[] { "newcomer", "newcomer-2" },
            new[] { pick.Value.Left.Value, pick.Value.Right.Value }.Order());
    }

    [Fact]
    public void Suggest_WithAnAnchor_FindsItsBestOpponent_AndKeepsItOnTheLeft()
    {
        var pick = MatchupPicker.Suggest([Newcomer, SettledA, SettledB, Champion], anchor: SettledB.ModelId);

        Assert.Equal((SettledB.ModelId, Newcomer.ModelId), pick);
    }
}
