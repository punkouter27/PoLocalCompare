using PoLocalCompare.Api.Features.Tournaments;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Tournaments;

namespace PoLocalCompare.Unit;

/// <summary>The Swiss format added 2026-10-29: opening pairs, later pairings, the table, the run.</summary>
public class SwissPlannerTests
{
    /// <summary>Seeded field, strongest first — "S1" is the top seed.</summary>
    private static List<BracketSlot> Field() =>
        Enumerable.Range(1, SwissPlanner.FieldSize)
            .Select(n => new BracketSlot(ModelId.From($"model-{n}"), $"S{n}", n))
            .ToList();

    private static SwissResult Win(BracketMatch m, bool slotAWins) =>
        new(m.SlotA.ModelId, m.SlotB.ModelId, slotAWins ? m.SlotA.ModelId : m.SlotB.ModelId);

    private static SwissResult Draw(BracketMatch m) => new(m.SlotA.ModelId, m.SlotB.ModelId, null);

    /// <summary>Top half against bottom half, never shuffled — the Dutch opening.</summary>
    [Fact]
    public void FirstRound_PairsTopHalfAgainstBottomHalf()
    {
        var round = SwissPlanner.FirstRound(Field());

        Assert.Equal(
            [(1, 5), (2, 6), (3, 7), (4, 8)],
            round.Select(m => (m.SlotA.Seed, m.SlotB.Seed)));
    }

    /// <summary>
    /// Exhaustive over every outcome of rounds one and two (win, loss or draw on each of eight
    /// boards — 6,561 histories): every later round seats all eight exactly once and never
    /// repeats a meeting. That is the property the backtracking exists for; greedy pairing
    /// alone strands a rematch on the last board in some of these.
    /// </summary>
    [Fact]
    public void LaterRounds_SeatEveryoneOnceAndNeverRepeatAMeeting()
    {
        var field = Field();
        var round1 = SwissPlanner.FirstRound(field);

        foreach (var outcomes1 in Outcomes())
        {
            var results = round1.Select((m, i) => Outcome(m, outcomes1[i])).ToList();
            var round2 = SwissPlanner.PairRound(field, results, 1);
            AssertValidRound(round2, results);

            foreach (var outcomes2 in Outcomes())
            {
                var after2 = results.Concat(round2.Select((m, i) => Outcome(m, outcomes2[i]))).ToList();
                AssertValidRound(SwissPlanner.PairRound(field, after2, 2), after2);
            }
        }
    }

    /// <summary>Better seeds all win round one: the four winners and four losers pair among themselves by seed.</summary>
    [Fact]
    public void SecondRound_PairsEqualScoresBySeed()
    {
        var field = Field();
        var results = SwissPlanner.FirstRound(field).Select(m => Win(m, slotAWins: true)).ToList();

        var round2 = SwissPlanner.PairRound(field, results, 1);

        Assert.Equal(
            [(1, 2), (3, 4), (5, 6), (7, 8)],
            round2.Select(m => (m.SlotA.Seed, m.SlotB.Seed)));
    }

    /// <summary>
    /// Points first, then Buchholz (opponents' points), then seed. A draw is half a point each.
    /// In the two-round case, seed 8 and seed 7 each finish above a better seed on equal points
    /// because they met stronger opponents.
    /// </summary>
    [Fact]
    public void Standings_RankByPointsThenBuchholzThenSeed()
    {
        var field = Field();
        var id = (int seed) => field[seed - 1].ModelId;
        SwissResult Game(int a, int b, int? winner) => new(id(a), id(b), winner is { } w ? id(w) : null);

        var results = new List<SwissResult>
        {
            Game(1, 5, 1), Game(2, 6, 2), Game(3, 7, 3), Game(4, 8, null),
        };

        var table = SwissPlanner.Standings(field, results);

        Assert.Equal([1, 2, 3, 4, 8, 5, 6, 7], table.Select(s => s.Entrant.Seed));
        Assert.Equal((0.5, 0.5), (table[3].Points, table[3].Buchholz));   // seed 4: drew seed 8
        Assert.Equal((1, 0, 0), (table[0].Wins, table[0].Draws, table[0].Losses));

        // Round 1: 8 upsets 4. Round 2: 1>2, 3>8, 5>4, 6>7. On one point, 2 (Buchholz 3) leads
        // 5 and 8 (both 2, so seed) and then 6 (1); on zero, 7 (3) ranks above 4 (2).
        table = SwissPlanner.Standings(field,
        [
            Game(1, 5, 1), Game(2, 6, 2), Game(3, 7, 3), Game(4, 8, 8),
            Game(1, 2, 1), Game(3, 8, 3), Game(4, 5, 5), Game(6, 7, 6),
        ]);

        Assert.Equal([1, 3, 2, 5, 8, 6, 7, 4], table.Select(s => s.Entrant.Seed));
        Assert.Equal([2.0, 2.0, 1.0, 1.0, 1.0, 1.0, 0.0, 0.0], table.Select(s => s.Points));
        Assert.Equal([2.0, 1.0, 3.0, 2.0, 2.0, 1.0, 3.0, 2.0], table.Select(s => s.Buchholz));
    }

    /// <summary>
    /// The aggregate end to end, with draws: each round is paired only once the previous one is
    /// fully decided, the run completes after twelve games, and the champion is the top of the
    /// table rather than whoever won the last game.
    /// </summary>
    [Fact]
    public void SwissTournament_PairsRoundByRoundAndCrownsTheTableLeader()
    {
        var t = Tournament.Draw(TournamentId.New(), 8, Field(), "Build a counter.", null, TournamentFormat.Swiss);

        for (var round = 0; round < SwissPlanner.RoundCount; round++)
        {
            Assert.Equal(4 * (round + 1), t.Matches.Count);
            var games = t.Matches.Where(m => m.Round == round).ToList();

            // Board 0 is drawn; everywhere else the better seed wins.
            t.RecordTie(round, 0);
            Assert.Equal(4 * (round + 1), t.Matches.Count);   // not paired mid-round
            foreach (var g in games.Skip(1))
            {
                var (winner, name) = g.SlotASeed < g.SlotBSeed ? (g.SlotAModelId, g.SlotAName) : (g.SlotBModelId, g.SlotBName);
                t.RecordWinner(round, g.Index, winner, name);
            }
        }

        Assert.Equal(TournamentStatus.Complete, t.Status);
        Assert.Equal(SwissPlanner.MatchCount, t.Matches.Count);
        Assert.Equal(t.SwissStandings()[0].Entrant.ModelId, t.ChampionModelId);
        Assert.False(t.RecordTie(2, 0));   // a replayed result changes nothing
    }

    private static IEnumerable<int[]> Outcomes()
    {
        for (var code = 0; code < 81; code++)
            yield return [code % 3, code / 3 % 3, code / 9 % 3, code / 27 % 3];
    }

    private static SwissResult Outcome(BracketMatch m, int outcome) =>
        outcome == 2 ? Draw(m) : Win(m, slotAWins: outcome == 0);

    private static void AssertValidRound(IReadOnlyList<BracketMatch> round, IReadOnlyList<SwissResult> played)
    {
        var seated = round.SelectMany(m => new[] { m.SlotA.ModelId, m.SlotB.ModelId }).ToList();
        Assert.Equal(SwissPlanner.FieldSize, seated.Distinct().Count());

        Assert.DoesNotContain(round, m => played.Any(r =>
            (r.ModelIdA == m.SlotA.ModelId && r.ModelIdB == m.SlotB.ModelId) ||
            (r.ModelIdA == m.SlotB.ModelId && r.ModelIdB == m.SlotA.ModelId)));
    }
}
