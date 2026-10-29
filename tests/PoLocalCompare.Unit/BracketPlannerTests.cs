using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Tournaments;

namespace PoLocalCompare.Unit;

public class BracketPlannerTests
{
    /// <summary>Contenders named by seed, strongest first — "S1" is the top seed.</summary>
    private static List<BracketSlot> Contenders(int size) =>
        Enumerable.Range(1, size)
            .Select(n => new BracketSlot(ModelId.From($"model-{n}"), $"S{n}"))
            .ToList();

    // ── Sizes ─────────────────────────────────────────────────────────────

    /// <summary>The last round is "Final" whatever the bracket size, because it counts back.</summary>
    [Theory]
    [InlineData(2, 0, "Final")]
    [InlineData(8, 0, "Quarter-finals")]
    [InlineData(8, 2, "Final")]
    public void RoundName_CountsBackFromTheFinal(int size, int round, string expected)
    {
        Assert.Equal(expected, BracketPlanner.RoundName(round, size));
    }

    // ── Seeding ───────────────────────────────────────────────────────────

    [Fact]
    public void SeedOrder_ForEight_IsTheStandardLayout()
    {
        // The exact layout subsumes "uses every seed exactly once": a permutation of 1..8 has
        // eight distinct entries by construction, so asserting the sequence is the stronger
        // statement and the count check adds nothing to it.
        Assert.Equal([1, 8, 4, 5, 2, 7, 3, 6], BracketPlanner.SeedOrder(8));
    }

    /// <summary>
    /// The property the whole seeding exists for: a random draw would routinely knock the two
    /// best models out against each other in round one, producing a winner that says nothing.
    /// </summary>
    [Theory]
    [InlineData(8)]
    public void TopTwoSeeds_CannotMeetBeforeTheFinal(int size)
    {
        var bracket = BracketPlanner.Build(Contenders(size), size);
        var finalRound = BracketPlanner.RoundCount(size) - 1;

        // Play the bracket through with the better seed always winning.
        var current = bracket;
        for (var round = 0; round < finalRound; round++)
        {
            foreach (var match in current.Where(m => m.Round == round).ToList())
            {
                var winner = Seed(match.SlotA) < Seed(match.SlotB) ? match.SlotA : match.SlotB;
                current = BracketPlanner.Advance(current, match.Round, match.Index, winner, size);
            }
        }

        var decider = current.Single(m => m.Round == finalRound);
        Assert.Equal([1, 2], new[] { Seed(decider.SlotA), Seed(decider.SlotB) }.OrderBy(x => x));
    }

    // ── Shape ─────────────────────────────────────────────────────────────

    [Fact]
    public void Build_LeavesLaterRoundsEmptyUntilWinnersArrive()
    {
        var bracket = BracketPlanner.Build(Contenders(8), 8);

        Assert.All(bracket.Where(m => m.Round == 0), m => Assert.True(m.IsReady));
        Assert.All(bracket.Where(m => m.Round > 0), m => Assert.False(m.IsReady));
    }

    // ── Advancing ─────────────────────────────────────────────────────────

    /// <summary>
    /// The parity rule is what keeps a bracket readable: the winner of the top match has to
    /// stay on top rather than being appended wherever there is room.
    /// </summary>
    [Fact]
    public void NextSlot_SendsEvenMatchesToSlotAAndOddToSlotB()
    {
        Assert.Equal((1, 0, true), BracketPlanner.NextSlot(0, 0, 8));
        Assert.Equal((1, 0, false), BracketPlanner.NextSlot(0, 1, 8));
        Assert.Equal((1, 1, true), BracketPlanner.NextSlot(0, 2, 8));
        Assert.Equal((1, 1, false), BracketPlanner.NextSlot(0, 3, 8));
    }

    [Fact]
    public void NextSlot_OfTheFinalIsNothing()
    {
        // Both endpoints of the bracket advance past anything: 8-model round 2 (the final)
        // and a 2-model round 0 (also the final) report no next slot.
        Assert.Null(BracketPlanner.NextSlot(2, 0, 8));
        Assert.Null(BracketPlanner.NextSlot(0, 0, 2));
    }

    private static int Seed(BracketSlot slot) => int.Parse(slot.DisplayName[1..]);
}
