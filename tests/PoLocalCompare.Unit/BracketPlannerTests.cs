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
    [InlineData(16, 0, "Round of 16")]
    [InlineData(16, 3, "Final")]
    public void RoundName_CountsBackFromTheFinal(int size, int round, string expected)
    {
        Assert.Equal(expected, BracketPlanner.RoundName(round, size));
    }

    // ── Seeding ───────────────────────────────────────────────────────────

    [Fact]
    public void SeedOrder_ForSixteen_IsTheStandardLayout()
    {
        // The exact layout subsumes "uses every seed exactly once": a permutation of 1..16 has
        // sixteen distinct entries by construction, so asserting the sequence is the stronger
        // statement and the count check adds nothing to it.
        Assert.Equal([1, 16, 8, 9, 4, 13, 5, 12, 2, 15, 7, 10, 3, 14, 6, 11], BracketPlanner.SeedOrder(16));
    }

    /// <summary>
    /// The property the whole seeding exists for: a random draw would routinely knock the two
    /// best models out against each other in round one, producing a winner that says nothing.
    /// </summary>
    [Theory]
    [InlineData(16)]
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
        var bracket = BracketPlanner.Build(Contenders(16), 16);

        Assert.Equal(15, bracket.Count);
        Assert.All(bracket.Where(m => m.Round == 0), m => Assert.True(m.IsReady));
        Assert.All(bracket.Where(m => m.Round > 0), m => Assert.False(m.IsReady));
    }

    // ── Byes ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A short field fills a 16 with byes, and the byes are the top seeds' — the standard
    /// layout pairs seed s with 17 - s, so the missing bottom seeds sit opposite the best ones.
    /// </summary>
    [Fact]
    public void Build_WithTwelve_GivesTheFourTopSeedsTheByes()
    {
        var bracket = BracketPlanner.Build(Contenders(12), 16);

        var byes = bracket.Where(BracketPlanner.IsBye).ToList();
        Assert.Equal([1, 2, 3, 4], byes.Select(m => m.SlotA.Seed).Order());
        Assert.All(byes, m => Assert.True(m.SlotB.IsEmpty));
        Assert.Equal(4, bracket.Count(m => m.Round == 0 && m.IsReady));
    }

    /// <summary>
    /// 2 is a 1v1, 9 to 16 is the bracket with byes. 8 would leave a first-round match with
    /// nobody in it, and 17 has no bracket at all.
    /// </summary>
    [Theory]
    [InlineData(8, null)]
    [InlineData(9, 16)]
    [InlineData(17, null)]
    public void BracketSizeFor_TakesNineToSixteen(int fieldCount, int? expected)
    {
        Assert.Equal(expected, BracketPlanner.BracketSizeFor(fieldCount));
    }

    // ── Advancing ─────────────────────────────────────────────────────────

    /// <summary>
    /// The parity rule is what keeps a bracket readable: the winner of the top match has to
    /// stay on top rather than being appended wherever there is room.
    /// </summary>
    [Fact]
    public void NextSlot_SendsEvenMatchesToSlotAAndOddToSlotB()
    {
        Assert.Equal((1, 0, true), BracketPlanner.NextSlot(0, 0, 16));
        Assert.Equal((1, 0, false), BracketPlanner.NextSlot(0, 1, 16));
        Assert.Equal((1, 1, true), BracketPlanner.NextSlot(0, 2, 16));
        Assert.Equal((1, 1, false), BracketPlanner.NextSlot(0, 3, 16));
    }

    [Fact]
    public void NextSlot_OfTheFinalIsNothing()
    {
        // Both endpoints of the bracket advance past anything: 16-model round 3 (the final)
        // and a 2-model round 0 (also the final) report no next slot. A stored 8-model bracket
        // from before 2026-11-07 still ends at its round 2.
        Assert.Null(BracketPlanner.NextSlot(3, 0, 16));
        Assert.Null(BracketPlanner.NextSlot(0, 0, 2));
        Assert.Null(BracketPlanner.NextSlot(2, 0, 8));
    }

    private static int Seed(BracketSlot slot) => int.Parse(slot.DisplayName[1..]);
}
