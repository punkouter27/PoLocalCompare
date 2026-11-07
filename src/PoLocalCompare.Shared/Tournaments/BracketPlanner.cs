using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Shared.Tournaments;

/// <summary>One slot in a bracket: filled from seeding, or later by a winner.</summary>
/// <param name="Seed">
/// 1-based seed number, stamped by <see cref="BracketPlanner.Build"/> and carried with the model
/// as it advances. Zero for an empty slot. It is what makes a drawn match resolvable without
/// inventing anything: a tie advances the better seed, which is an ordinary tournament rule
/// rather than a guess about which output was better.
/// </param>
public sealed record BracketSlot(ModelId ModelId, string DisplayName, int Seed = 0)
{
    public bool IsEmpty => ModelId.IsEmpty;

    public static BracketSlot Empty { get; } = new(default, string.Empty, 0);
}

/// <summary>A single match position in the bracket, addressed by round and index.</summary>
/// <param name="Round">0 = the first round played, counting up toward the final.</param>
/// <param name="Index">Position within the round, 0-based, top of the bracket first.</param>
public sealed record BracketMatch(
    int Round,
    int Index,
    BracketSlot SlotA,
    BracketSlot SlotB)
{
    /// <summary>Both contestants known, so this match can actually be run.</summary>
    public bool IsReady => !SlotA.IsEmpty && !SlotB.IsEmpty;
}

/// <summary>
/// Lays out a single-elimination bracket and works out where each winner goes next.
/// </summary>
/// <remarks>
/// Pure and separate from the Tournaments slice so the whole shape of a run is decidable — and
/// unit-testable — before anything is written, because bracket matches are real duels that
/// persist and move ELO.
///
/// Seeding is standard tournament seeding rather than a shuffle: the top two seeds are placed so
/// they can only meet in the final. A random draw would routinely knock the two best models out
/// against each other in round one, which produces a winner that says nothing.
/// </remarks>
public static class BracketPlanner
{
    /// <summary>
    /// Bracket sizes the app offers. 2 is a plain 1v1 run through the same machinery; 16 is the
    /// real bracket. 4 was removed on 2026-08-23 (a strictly worse 8); 8 became 16 on
    /// 2026-11-07, when the catalog grew past sixteen Foundry models. The maths below is
    /// size-generic, so changing the list is a one-line change.
    /// </summary>
    public static IReadOnlyList<int> SupportedSizes { get; } = [2, 16];

    public static bool IsSupportedSize(int size) => SupportedSizes.Contains(size);

    /// <summary>
    /// The smallest field a bracket of <paramref name="size"/> accepts. Below the full size the
    /// missing seeds are byes, and a bye can only face a real model: with more than half the
    /// field missing, a first-round match would have nobody in it. So 16 takes 9 to 16 entrants,
    /// and 2 takes exactly 2.
    /// </summary>
    public static int MinimumField(int size)
    {
        ThrowIfUnsupported(size);
        return size == 2 ? 2 : size / 2 + 1;
    }

    /// <summary>The bracket a field of this many entrants is drawn into, or null when none fits.</summary>
    public static int? BracketSizeFor(int fieldCount) =>
        SupportedSizes.Where(size => fieldCount >= MinimumField(size) && fieldCount <= size)
            .Select(size => (int?)size)
            .FirstOrDefault();

    /// <summary>Number of rounds a bracket of this size takes — 2 → 1, 8 → 3, 16 → 4.</summary>
    /// <remarks>
    /// Any power of two, not only <see cref="SupportedSizes"/>: drawing is limited to those, but
    /// the 8-model brackets drawn before 2026-11-07 are still stored and still have to render
    /// and, if one was mid-run, advance.
    /// </remarks>
    public static int RoundCount(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "A bracket's size is a power of two.");

        var rounds = 0;
        for (var remaining = size; remaining > 1; remaining /= 2) rounds++;
        return rounds;
    }

    /// <summary>
    /// Human name for a round, counting back from the final so the last round is always "Final"
    /// regardless of bracket size.
    /// </summary>
    public static string RoundName(int round, int size)
    {
        var fromEnd = RoundCount(size) - 1 - round;
        return fromEnd switch
        {
            0 => "Final",
            1 => "Semi-finals",
            2 => "Quarter-finals",
            3 => "Round of 16",
            _ => $"Round {round + 1}",
        };
    }

    /// <summary>
    /// Standard seeding order: the sequence of seed numbers, top of the bracket first, such that
    /// seeds 1 and 2 can only meet in the final.
    /// </summary>
    /// <remarks>
    /// Built by repeated reflection — each round doubles the field, and a seed <c>s</c> in a
    /// bracket of <c>n</c> is paired with <c>n + 1 - s</c>. For 16 this yields
    /// <c>1,16,8,9,4,13,5,12,2,15,7,10,3,14,6,11</c>: the classic layout where the top seed meets
    /// the bottom one first — and, in a short field, gets the first bye.
    /// </remarks>
    public static IReadOnlyList<int> SeedOrder(int size)
    {
        ThrowIfUnsupported(size);

        var seeds = new List<int> { 1, 2 };

        while (seeds.Count < size)
        {
            var field = seeds.Count * 2;
            var next = new List<int>(field);
            foreach (var seed in seeds)
            {
                next.Add(seed);
                next.Add(field + 1 - seed);
            }
            seeds = next;
        }

        return seeds;
    }

    /// <summary>
    /// Builds the full bracket. Round 0 is seeded from <paramref name="contenders"/>; every later
    /// round is laid out with empty slots for the winners to land in.
    /// </summary>
    /// <param name="contenders">
    /// Strongest first — the caller sorts, because "strongest" is a leaderboard question and this
    /// type does not know about ELO. Between <see cref="MinimumField"/> and <paramref name="size"/>
    /// entries; each seed past the last contender is a bye, an empty slot opposite a top seed.
    /// The caller advances a bye's model (see <see cref="IsBye"/>) — this type only lays it out.
    /// </param>
    public static IReadOnlyList<BracketMatch> Build(IReadOnlyList<BracketSlot> contenders, int size)
    {
        if (contenders.Count < MinimumField(size) || contenders.Count > size)
        {
            throw new ArgumentException(
                $"A bracket of {size} needs {MinimumField(size)} to {size} contenders, got {contenders.Count}.",
                nameof(contenders));
        }

        var order = SeedOrder(size);
        var matches = new List<BracketMatch>();

        // Round 0 — seeded pairs, taken two at a time from the seed order.
        for (var i = 0; i < size / 2; i++)
        {
            // Seed numbers are 1-based; the contenders list is strongest-first, so seed n is at
            // index n - 1. The seed is stamped on here rather than expected from the caller —
            // "strongest first" is the caller's contract, and the number that follows from it
            // is this type's to assign. A seed with no contender is the bye.
            matches.Add(new BracketMatch(
                Round: 0,
                Index: i,
                SlotA: SeedSlot(contenders, order[i * 2]),
                SlotB: SeedSlot(contenders, order[i * 2 + 1])));
        }

        // Later rounds — placeholders that Advance fills in as results land.
        var rounds = RoundCount(size);
        for (var round = 1; round < rounds; round++)
        {
            var inRound = size / (1 << (round + 1));
            for (var i = 0; i < inRound; i++)
                matches.Add(new BracketMatch(round, i, BracketSlot.Empty, BracketSlot.Empty));
        }

        return matches;
    }

    /// <summary>A first-round match with one contender and a bye: decided at the draw, never played.</summary>
    public static bool IsBye(BracketMatch match) =>
        match.Round == 0 && match.SlotA.IsEmpty != match.SlotB.IsEmpty;

    private static BracketSlot SeedSlot(IReadOnlyList<BracketSlot> contenders, int seed) =>
        seed <= contenders.Count ? contenders[seed - 1] with { Seed = seed } : BracketSlot.Empty;

    private static void ThrowIfUnsupported(int size)
    {
        if (!IsSupportedSize(size))
            throw new ArgumentOutOfRangeException(nameof(size), size, "Bracket size must be 2 or 16.");
    }

    /// <summary>
    /// Where the winner of a match goes. Returns null for the final, which feeds nothing.
    /// </summary>
    /// <remarks>
    /// Two matches feed each match in the next round, so match <c>i</c> advances to match
    /// <c>i / 2</c> — into slot A when <c>i</c> is even and slot B when it is odd. That parity is
    /// what keeps the bracket readable: the winner of the top match stays on top.
    /// </remarks>
    public static (int Round, int Index, bool IntoSlotA)? NextSlot(int round, int index, int size)
    {
        if (round >= RoundCount(size) - 1) return null;
        return (round + 1, index / 2, index % 2 == 0);
    }

    /// <summary>
    /// Places a winner into its next slot and returns the updated bracket. A win in the final
    /// changes nothing structurally — the tournament is simply over.
    /// </summary>
    public static IReadOnlyList<BracketMatch> Advance(
        IReadOnlyList<BracketMatch> bracket,
        int round,
        int index,
        BracketSlot winner,
        int size)
    {
        var target = NextSlot(round, index, size);
        if (target is not var (nextRound, nextIndex, intoSlotA)) return bracket;

        return bracket.Select(m =>
            m.Round == nextRound && m.Index == nextIndex
                ? m with
                {
                    SlotA = intoSlotA ? winner : m.SlotA,
                    SlotB = intoSlotA ? m.SlotB : winner,
                }
                : m).ToList();
    }
}
