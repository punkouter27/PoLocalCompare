using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Shared.Tournaments;

/// <summary>One played Swiss game. <paramref name="WinnerModelId"/> is null for a draw.</summary>
public sealed record SwissResult(ModelId ModelIdA, ModelId ModelIdB, ModelId? WinnerModelId);

/// <summary>One row of the Swiss table, best first.</summary>
/// <param name="Rank">1-based position after every tie-break.</param>
/// <param name="Buchholz">Sum of every opponent's points — how hard the schedule was.</param>
public sealed record SwissStanding(
    int Rank,
    BracketSlot Entrant,
    double Points,
    double Buchholz,
    int Wins,
    int Draws,
    int Losses);

/// <summary>
/// Pairs and scores an eight-model, three-round Swiss tournament.
/// </summary>
/// <remarks>
/// The alternative format to <see cref="BracketPlanner"/>, added 2026-10-29. Every entrant plays
/// all three rounds, so a Swiss run is twelve rated duels where a bracket is seven, and a model
/// knocked out in round one of a bracket still contributes two more comparisons — which is what
/// a benchmark wants from the same prompt.
///
/// Pure, like the bracket planner, and deliberately stateless: the next round is a function of
/// the field and the results so far, so the server re-derives it from storage on every step and
/// never needs to remember a draw.
///
/// <list type="bullet">
/// <item>Round 1 is top half against bottom half by seed — 1v5, 2v6, 3v7, 4v8 (the Dutch
/// system's opening). Never shuffled, same as the bracket.</item>
/// <item>Later rounds sort by points, then seed, and pair each player with the highest-placed
/// opponent it has not met, backtracking when that would strand a later pair with a rematch.
/// Eight players and three rounds always admit such a pairing.</item>
/// <item>Win 1, draw ½, loss 0. Standings: points, then Buchholz, then seed.</item>
/// </list>
/// </remarks>
public static class SwissPlanner
{
    /// <summary>The only Swiss field the app runs.</summary>
    public const int FieldSize = 8;

    /// <summary>Three rounds separate eight players fully: 2³ = 8.</summary>
    public const int RoundCount = 3;

    /// <summary>Four games a round, three rounds.</summary>
    public const int MatchCount = FieldSize / 2 * RoundCount;

    public static string RoundName(int round) => $"Round {round + 1}";

    /// <summary>Round-one seed pairs, in board order: (1,5), (2,6), (3,7), (4,8).</summary>
    public static IReadOnlyList<(int SeedA, int SeedB)> FirstRoundSeeds() =>
        Enumerable.Range(1, FieldSize / 2).Select(s => (s, s + FieldSize / 2)).ToList();

    /// <summary>Round one for a field ordered strongest first; seeds are stamped here.</summary>
    public static IReadOnlyList<BracketMatch> FirstRound(IReadOnlyList<BracketSlot> contenders)
    {
        if (contenders.Count != FieldSize)
            throw new ArgumentException($"A Swiss field needs exactly {FieldSize} contenders, got {contenders.Count}.", nameof(contenders));

        return FirstRoundSeeds()
            .Select((pair, i) => new BracketMatch(
                Round: 0,
                Index: i,
                SlotA: contenders[pair.SeedA - 1] with { Seed = pair.SeedA },
                SlotB: contenders[pair.SeedB - 1] with { Seed = pair.SeedB }))
            .ToList();
    }

    /// <summary>
    /// Pairs the next round from everything played so far. Within each game the better-placed
    /// player is slot A.
    /// </summary>
    /// <param name="field">Every entrant with its seed stamped.</param>
    /// <exception cref="InvalidOperationException">No rematch-free pairing exists.</exception>
    public static IReadOnlyList<BracketMatch> PairRound(
        IReadOnlyList<BracketSlot> field,
        IReadOnlyList<SwissResult> results,
        int round)
    {
        var points = PointsByModel(field, results);
        var met = results
            .SelectMany(r => new[] { (r.ModelIdA, r.ModelIdB), (r.ModelIdB, r.ModelIdA) })
            .ToHashSet();

        var order = field
            .OrderByDescending(s => points[s.ModelId])
            .ThenBy(s => s.Seed)
            .ToList();

        var pairs = new List<(BracketSlot A, BracketSlot B)>();
        if (!TryPair(order, met, pairs))
            throw new InvalidOperationException("No pairing avoids a rematch.");

        return pairs
            .Select((p, i) => new BracketMatch(round, i, p.A, p.B))
            .ToList();
    }

    /// <summary>
    /// Depth-first: the top unpaired player takes the best-placed opponent it has not met, and
    /// the next candidate only when the rest of the field cannot be paired after that choice.
    /// The search is tiny (at most 7 × 5 × 3 branches), and its first answer is the one a
    /// tournament director would pick by hand.
    /// </summary>
    private static bool TryPair(
        List<BracketSlot> unpaired,
        HashSet<(ModelId, ModelId)> met,
        List<(BracketSlot, BracketSlot)> pairs)
    {
        if (unpaired.Count == 0) return true;

        var top = unpaired[0];
        for (var i = 1; i < unpaired.Count; i++)
        {
            var opponent = unpaired[i];
            if (met.Contains((top.ModelId, opponent.ModelId))) continue;

            var rest = unpaired.Where((_, j) => j != 0 && j != i).ToList();
            pairs.Add((top, opponent));
            if (TryPair(rest, met, pairs)) return true;
            pairs.RemoveAt(pairs.Count - 1);
        }

        return false;
    }

    /// <summary>The table: points, then Buchholz, then seed. Rank is 1-based and never shared.</summary>
    public static IReadOnlyList<SwissStanding> Standings(
        IReadOnlyList<BracketSlot> field,
        IReadOnlyList<SwissResult> results)
    {
        var points = PointsByModel(field, results);

        return field
            .Select(entrant =>
            {
                var games = results.Where(r => r.ModelIdA == entrant.ModelId || r.ModelIdB == entrant.ModelId).ToList();
                var wins = games.Count(r => r.WinnerModelId == entrant.ModelId);
                var draws = games.Count(r => r.WinnerModelId is null);
                var buchholz = games.Sum(r => points[r.ModelIdA == entrant.ModelId ? r.ModelIdB : r.ModelIdA]);
                return (entrant, wins, draws, losses: games.Count - wins - draws, buchholz);
            })
            .OrderByDescending(x => points[x.entrant.ModelId])
            .ThenByDescending(x => x.buchholz)
            .ThenBy(x => x.entrant.Seed)
            .Select((x, i) => new SwissStanding(
                i + 1, x.entrant, points[x.entrant.ModelId], x.buchholz, x.wins, x.draws, x.losses))
            .ToList();
    }

    private static Dictionary<ModelId, double> PointsByModel(
        IReadOnlyList<BracketSlot> field,
        IReadOnlyList<SwissResult> results)
    {
        var points = field.ToDictionary(s => s.ModelId, _ => 0.0);
        foreach (var r in results)
        {
            if (r.WinnerModelId is { } winner)
            {
                points[winner] += 1;
            }
            else
            {
                points[r.ModelIdA] += 0.5;
                points[r.ModelIdB] += 0.5;
            }
        }
        return points;
    }
}
