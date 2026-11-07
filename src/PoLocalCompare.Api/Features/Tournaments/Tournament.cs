// GoF: Aggregate Root
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Tournaments;

namespace PoLocalCompare.Api.Features.Tournaments;

/// <summary>One match position inside a persisted bracket.</summary>
/// <remarks>
/// Mutable, unlike <see cref="BracketMatch"/>: the planner's record is the pure shape of a
/// bracket, and this is that shape plus what happened when it was played. They are kept apart
/// so the seeding and advancement rules stay unit-testable without a storage account.
/// </remarks>
public sealed class TournamentMatch
{
    public int Round { get; set; }
    public int Index { get; set; }

    public ModelId SlotAModelId { get; set; }
    public string SlotAName { get; set; } = string.Empty;

    /// <summary>1-based seed, carried with the model as it advances. See <see cref="BracketSlot"/>.</summary>
    public int SlotASeed { get; set; }

    public ModelId SlotBModelId { get; set; }
    public string SlotBName { get; set; } = string.Empty;

    /// <inheritdoc cref="SlotASeed"/>
    public int SlotBSeed { get; set; }

    /// <summary>The duel this match ran as. Null until the runner starts it.</summary>
    public DuelId? DuelId { get; set; }

    public ModelId? WinnerModelId { get; set; }
    public string? WinnerName { get; set; }
    public int WinnerSeed { get; set; }

    /// <summary>
    /// Set when the AI judge returned a draw and the better seed advanced on the tie-break,
    /// so the bracket can say so rather than presenting it as a won match.
    /// </summary>
    public bool WonOnSeedTieBreak { get; set; }

    /// <summary>A game of a stored Swiss run that the judge called a draw. Brackets never set it.</summary>
    public bool IsDraw { get; set; }

    /// <summary>Why this match could not be decided. Set only on a terminal failure.</summary>
    public string? FailureReason { get; set; }

    public bool IsReady => !SlotAModelId.IsEmpty && !SlotBModelId.IsEmpty;
    public bool IsDecided => WinnerModelId is not null || IsDraw;
}

/// <summary>
/// A tournament run — a seeded single-elimination bracket: one prompt, a field of up to
/// sixteen, and the duels that decide it.
/// </summary>
/// <remarks>
/// Persisted as one row with the matches serialised into a single column rather than as a row
/// per match. A bracket is at most fifteen matches and is only ever read whole, so a multi-row
/// aggregate would buy nothing but the risk of a half-written bracket.
///
/// Bracket matches are ordinary duels — they persist, they are judged, and they move ELO,
/// exactly as a hand-started duel does. That is deliberate: a tournament that did not count
/// would be a simulation of the thing rather than the thing.
/// </remarks>
public sealed class Tournament
{
    public TournamentId TournamentId { get; init; }

    /// <summary>
    /// Bracket, or a Swiss run stored before that format was retired on 2026-11-07. Swiss rows are
    /// read-only: the runner, the resume sweep and the one-at-a-time gate all ignore them, and
    /// <see cref="TournamentRepository"/> reads an unfinished one as Abandoned.
    /// </summary>
    public TournamentFormat Format { get; init; } = TournamentFormat.Bracket;

    /// <summary>
    /// The bracket's size — 2 or 16 (8 before 2026-11-07), validated through
    /// <see cref="BracketPlanner.IsSupportedSize"/>. A short field is still a 16: its missing
    /// seeds are byes.
    /// </summary>
    public int Size { get; init; }

    /// <summary>
    /// The prompt every match in the bracket receives, as typed. Stored once rather than per
    /// match — a bracket compares models, so varying the task between rounds would make the
    /// winner a function of the draw. The self-contained suffix each duel actually sends is appended by
    /// <see cref="CommenceDuelHandler"/>; keeping a second copy here would let the two drift.
    /// </summary>
    public string PromptText { get; init; } = string.Empty;

    public TournamentStatus Status { get; set; } = TournamentStatus.Pending;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Forensic only, never used for authorization — same rule as <c>Duel.OwnerId</c>.</summary>
    public string? OwnerId { get; set; }

    public ModelId? ChampionModelId { get; set; }
    public string? ChampionName { get; set; }

    /// <summary>Why the run stopped short. Set together with <see cref="TournamentStatus.Abandoned"/>.</summary>
    public string? AbandonedReason { get; set; }

    public List<TournamentMatch> Matches { get; set; } = [];

    /// <summary>Storage concurrency token; set when loaded from Table Storage (standards §5.5).</summary>
    public string? ETag { get; set; }

    public Tournament() { }

    /// <summary>
    /// Draws a bracket. The field must already be strongest-first — seeding is by ELO, and this
    /// type does not know about ratings. Each bye is decided here, before anything is saved, so
    /// the runner only ever sees matches with two models in them.
    /// </summary>
    public static Tournament Draw(
        TournamentId tournamentId,
        int size,
        IReadOnlyList<BracketSlot> seededField,
        string promptText,
        string? ownerId)
    {
        if (!BracketPlanner.IsSupportedSize(size))
            throw new ArgumentOutOfRangeException(nameof(size), size, "Bracket size must be 2 or 16.");

        if (string.IsNullOrWhiteSpace(promptText))
            throw new ArgumentException("A tournament needs a prompt.", nameof(promptText));

        var bracket = BracketPlanner.Build(seededField, size);

        var tournament = new Tournament
        {
            TournamentId = tournamentId,
            Size = size,
            PromptText = promptText,
            CreatedAt = DateTimeOffset.UtcNow,
            OwnerId = ownerId,
            Status = TournamentStatus.Pending,
            Matches = bracket.Select(m => new TournamentMatch
            {
                Round = m.Round,
                Index = m.Index,
                SlotAModelId = m.SlotA.ModelId,
                SlotAName = m.SlotA.DisplayName,
                SlotASeed = m.SlotA.Seed,
                SlotBModelId = m.SlotB.ModelId,
                SlotBName = m.SlotB.DisplayName,
                SlotBSeed = m.SlotB.Seed,
            }).ToList(),
        };

        foreach (var bye in bracket.Where(BracketPlanner.IsBye))
        {
            var through = bye.SlotA.IsEmpty ? bye.SlotB : bye.SlotA;
            tournament.RecordWinner(bye.Round, bye.Index, through.ModelId, through.DisplayName);
        }

        return tournament;
    }

    /// <summary>
    /// The next match to run: the earliest undecided one with both contestants known. Null when
    /// the bracket is finished or is waiting on a result that has not landed yet.
    /// </summary>
    public TournamentMatch? NextPlayable() => AllPlayable().FirstOrDefault();

    /// <summary>
    /// Every match that could be played right now, in bracket order.
    /// </summary>
    /// <remarks>
    /// Within a round these are genuinely independent — round 1's eight matches share no
    /// state and neither depends on another's result — so the runner can have several in flight
    /// at once instead of waiting out each in turn. Rounds still gate on each other, and that
    /// falls out of <c>IsReady</c> rather than needing a check here: a semi-final has no
    /// contestants until both its feeder matches have decided, so it simply is not playable yet.
    /// </remarks>
    public IEnumerable<TournamentMatch> AllPlayable() =>
        Matches
            .Where(m => !m.IsDecided && m.FailureReason is null && m.IsReady)
            .OrderBy(m => m.Round)
            .ThenBy(m => m.Index);

    /// <summary>
    /// Records a winner and moves it into the next round. Returns false when the match is
    /// unknown or already decided, so a replayed result cannot advance the bracket twice.
    /// </summary>
    public bool RecordWinner(
        int round,
        int index,
        ModelId winnerModelId,
        string winnerName,
        bool wonOnSeedTieBreak = false)
    {
        var match = Matches.FirstOrDefault(m => m.Round == round && m.Index == index);
        if (match is null || match.IsDecided) return false;

        var winnerSeed = winnerModelId == match.SlotAModelId ? match.SlotASeed : match.SlotBSeed;

        match.WinnerModelId = winnerModelId;
        match.WinnerName = winnerName;
        match.WinnerSeed = winnerSeed;
        match.WonOnSeedTieBreak = wonOnSeedTieBreak;

        var target = BracketPlanner.NextSlot(round, index, Size);
        if (target is var (nextRound, nextIndex, intoSlotA))
        {
            var next = Matches.FirstOrDefault(m => m.Round == nextRound && m.Index == nextIndex);
            if (next is not null)
            {
                if (intoSlotA)
                {
                    next.SlotAModelId = winnerModelId;
                    next.SlotAName = winnerName;
                    next.SlotASeed = winnerSeed;
                }
                else
                {
                    next.SlotBModelId = winnerModelId;
                    next.SlotBName = winnerName;
                    next.SlotBSeed = winnerSeed;
                }
            }
        }
        else
        {
            // No next slot means this was the final.
            ChampionModelId = winnerModelId;
            ChampionName = winnerName;
            Status = TournamentStatus.Complete;
            CompletedAt = DateTimeOffset.UtcNow;
        }

        return true;
    }

    /// <summary>
    /// Which contestant a drawn match awards. The better seed advances — an ordinary tournament
    /// tie-break, and the only resolution available that does not invent an opinion about which
    /// output was better. The alternative is abandoning a whole bracket over one draw.
    /// </summary>
    private static (ModelId ModelId, string Name) SeedTieBreak(TournamentMatch match) =>
        match.SlotASeed <= match.SlotBSeed
            ? (match.SlotAModelId, match.SlotAName)
            : (match.SlotBModelId, match.SlotBName);

    /// <summary>
    /// Records a match the judge called a draw: the better seed advances
    /// (<see cref="SeedTieBreak"/>). Returns false for an unknown or already-decided match.
    /// </summary>
    public bool RecordTie(int round, int index)
    {
        var match = Matches.FirstOrDefault(m => m.Round == round && m.Index == index);
        if (match is null || match.IsDecided) return false;

        var (winnerId, winnerName) = SeedTieBreak(match);
        return RecordWinner(round, index, winnerId, winnerName, wonOnSeedTieBreak: true);
    }

    /// <summary>
    /// Stops the run without a champion. A bracket cannot skip a match — every later round is
    /// seeded by the winner of an earlier one — so an undecidable match ends the tournament
    /// rather than being written off as a bye. (Byes exist only at the draw, for seeds the field
    /// did not fill — never for a match that was played and failed.)
    /// </summary>
    public void Abandon(string reason)
    {
        if (Status is TournamentStatus.Complete or TournamentStatus.Abandoned) return;

        Status = TournamentStatus.Abandoned;
        AbandonedReason = reason;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// The entrants of a stored Swiss run, read back from round one, where each appears once.
    /// </summary>
    private List<BracketSlot> SwissField() =>
        Matches
            .Where(m => m.Round == 0)
            .SelectMany(m => new[]
            {
                new BracketSlot(m.SlotAModelId, m.SlotAName, m.SlotASeed),
                new BracketSlot(m.SlotBModelId, m.SlotBName, m.SlotBSeed),
            })
            .OrderBy(s => s.Seed)
            .ToList();

    /// <summary>A stored Swiss run's table. Empty for a bracket.</summary>
    public IReadOnlyList<SwissStanding> SwissTable() =>
        Format != TournamentFormat.Swiss
            ? []
            : SwissStandings.Compute(
                SwissField(),
                Matches
                    .Where(m => m.IsDecided)
                    .Select(m => new SwissResult(m.SlotAModelId, m.SlotBModelId, m.IsDraw ? null : m.WinnerModelId))
                    .ToList());

    public TournamentDto ToDto() => new()
    {
        TournamentId = TournamentId,
        Status = Status,
        Format = Format,
        Size = Size,
        PromptText = PromptText,
        CreatedAt = CreatedAt,
        CompletedAt = CompletedAt,
        OwnerId = OwnerId,
        ChampionModelId = ChampionModelId,
        ChampionName = ChampionName,
        AbandonedReason = AbandonedReason,
        Matches = Matches
            .OrderBy(m => m.Round)
            .ThenBy(m => m.Index)
            .Select(m => new TournamentMatchDto
            {
                Round = m.Round,
                Index = m.Index,
                RoundName = Format == TournamentFormat.Swiss
                    ? $"Round {m.Round + 1}"
                    : BracketPlanner.RoundName(m.Round, Size),
                SlotAModelId = m.SlotAModelId,
                SlotAName = m.SlotAName,
                SlotASeed = m.SlotASeed,
                SlotBModelId = m.SlotBModelId,
                SlotBName = m.SlotBName,
                SlotBSeed = m.SlotBSeed,
                DuelId = m.DuelId,
                WinnerModelId = m.WinnerModelId,
                WinnerName = m.WinnerName,
                WonOnSeedTieBreak = m.WonOnSeedTieBreak,
                IsDraw = m.IsDraw,
                FailureReason = m.FailureReason,
            })
            .ToList(),
        Standings = SwissTable()
            .Select(s => new SwissStandingDto
            {
                Rank = s.Rank,
                ModelId = s.Entrant.ModelId,
                Name = s.Entrant.DisplayName,
                Seed = s.Entrant.Seed,
                Points = s.Points,
                Buchholz = s.Buchholz,
                Wins = s.Wins,
                Draws = s.Draws,
                Losses = s.Losses,
            })
            .ToList(),
    };
}
