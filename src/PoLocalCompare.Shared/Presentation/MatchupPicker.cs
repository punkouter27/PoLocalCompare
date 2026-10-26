using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Shared.Presentation;

/// <summary>
/// Picks the duel that would teach the leaderboard the most.
/// </summary>
/// <remarks>
/// A duel's information about the ranking is roughly how unsure its outcome is — p(1 − p), the
/// Bradley–Terry win probability from the two strengths — times how unsettled the two ratings
/// are (σ²ᵢ + σ²ⱼ, from the leaderboard's ±N). Two models far apart teach nothing (the favourite
/// wins); two well-measured models teach little (the ratings barely move). Picking duels by this
/// score settles the catalog in fewer paid duels than picking them at random. It is the active
/// sampling Chatbot Arena uses, on the same fit the leaderboard already draws its ±N from.
/// </remarks>
public static class MatchupPicker
{
    /// <param name="Interval95">The leaderboard's ±N, the half-width of a 95% interval.</param>
    public sealed record Candidate(ModelId ModelId, double Strength, double Interval95);

    /// <summary>Expected information of one duel between <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static double Score(Candidate a, Candidate b)
    {
        var p = 1 / (1 + Math.Pow(10, (b.Strength - a.Strength) / 400));
        var sa = a.Interval95 / 1.96;
        var sb = b.Interval95 / 1.96;
        return p * (1 - p) * (sa * sa + sb * sb);
    }

    /// <summary>
    /// The highest-scoring pair, or the best opponent for <paramref name="anchor"/> when one model
    /// is already picked. Null with fewer than two candidates. Ties break on id, so the same board
    /// always suggests the same duel.
    /// </summary>
    public static (ModelId Left, ModelId Right)? Suggest(IReadOnlyList<Candidate> candidates, ModelId? anchor = null)
    {
        (Candidate A, Candidate B, double Score)? best = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            for (var j = i + 1; j < candidates.Count; j++)
            {
                var (a, b) = (candidates[i], candidates[j]);
                if (a.ModelId == b.ModelId) continue;
                if (anchor is { } fixedId && a.ModelId != fixedId && b.ModelId != fixedId) continue;

                var score = Score(a, b);
                if (best is null || score > best.Value.Score
                    || (score == best.Value.Score && string.CompareOrdinal(Key(a, b), Key(best.Value.A, best.Value.B)) < 0))
                    best = (a, b, score);
            }
        }

        if (best is not { } pick) return null;
        // The anchor keeps its slot on the left; the rest is order-free.
        return pick.B.ModelId == anchor ? (pick.B.ModelId, pick.A.ModelId) : (pick.A.ModelId, pick.B.ModelId);

        static string Key(Candidate x, Candidate y) => x.ModelId.Value + "|" + y.ModelId.Value;
    }
}
