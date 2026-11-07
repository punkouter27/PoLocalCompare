using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Shared.Tournaments;

/// <summary>One decided Swiss game. <paramref name="WinnerModelId"/> is null for a draw.</summary>
public sealed record SwissResult(ModelId ModelIdA, ModelId ModelIdB, ModelId? WinnerModelId);

/// <summary>One row of a Swiss table.</summary>
public sealed record SwissStanding(
    int Rank,
    BracketSlot Entrant,
    double Points,
    double Buchholz,
    int Wins,
    int Draws,
    int Losses);

/// <summary>
/// The table of a stored Swiss run — read-only. Swiss could be played from 2026-10-29 until it
/// was retired on 2026-11-07; the pairing logic went with it, and this is what is left so those
/// runs still show their result.
/// </summary>
/// <remarks>
/// Points, then Buchholz (the sum of every opponent's points, uncut), then seed — the order the
/// runs were decided by, so a stored champion stays at the top of the table it is shown with.
/// A draw is half a point each.
/// </remarks>
public static class SwissStandings
{
    /// <summary>Ranks the field. Rank is 1-based and never shared.</summary>
    /// <param name="field">Every entrant with its seed stamped.</param>
    public static IReadOnlyList<SwissStanding> Compute(
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
}
