using PoLocalCompare.Api.Features.Tournaments;
using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Tournaments;

namespace PoLocalCompare.Unit;

/// <summary>
/// The two rules that decide what a bracket may contain, both changed on 2026-08-23.
/// </summary>
/// <remarks>
/// Pinned here because both are reversals of recorded decisions and the reasoning is easy to
/// re-derive backwards. Browser models were excluded (PRD §9 item 21) on the grounds that a
/// bracket outlives the tab and WebGPU inference does not; they are allowed now because the
/// Tournament page drives those matches itself, and the tab-open requirement moved from a
/// prohibition to a warning on the page. The 4-model bracket was dropped as a strictly worse 8,
/// and on 2026-11-07 the 8 became a 16 that a short field fills with byes.
/// </remarks>
public class TournamentFieldTests
{
    [Theory]
    [InlineData(ModelType.Remote)]
    [InlineData(ModelType.LocalService)]
    [InlineData(ModelType.Local)]
    public void EveryModelType_MayNowEnterABracket(ModelType modelType)
    {
        Assert.True(CreateTournamentHandler.IsEligible(modelType));
    }

    [Fact]
    public void SupportedSizes_AreTheSinglesMatchAndTheFullBracket()
    {
        Assert.Equal([2, 16], BracketPlanner.SupportedSizes);
    }

    /// <summary>
    /// A bye is decided at the draw, so the runner never sees a match with one model in it: the
    /// top seed is already waiting in round two, and only the real first-round matches are playable.
    /// </summary>
    [Fact]
    public void Draw_WithTwelve_SendsTheTopFourSeedsStraightToRoundTwo()
    {
        var field = Enumerable.Range(1, 12)
            .Select(n => new BracketSlot(ModelId.From($"model-{n}"), $"S{n}"))
            .ToList();

        var tournament = Tournament.Draw(TournamentId.New(), 16, field, "Build a counter page.", ownerId: null);

        var roundTwoSeeds = tournament.Matches.Where(m => m.Round == 1)
            .SelectMany(m => new[] { m.SlotASeed, m.SlotBSeed })
            .Where(seed => seed > 0);
        Assert.Equal([1, 2, 3, 4], roundTwoSeeds.Order());
        Assert.Equal(4, tournament.AllPlayable().Count());
        Assert.All(tournament.AllPlayable(), m => Assert.Equal(0, m.Round));
    }
}
