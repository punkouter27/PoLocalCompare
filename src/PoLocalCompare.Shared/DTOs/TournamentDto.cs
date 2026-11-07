using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Shared.DTOs;

/// <summary>Where a tournament is in its life.</summary>
public enum TournamentStatus
{
    /// <summary>Bracket drawn, nothing run yet.</summary>
    Pending = 0,

    /// <summary>At least one match has been started.</summary>
    Running = 1,

    /// <summary>The final has been decided.</summary>
    Complete = 2,

    /// <summary>
    /// Stopped without a champion — a match could not be judged, so the bracket cannot advance.
    /// Distinct from Complete so a run that died is never displayed as a result.
    /// </summary>
    Abandoned = 3,
}

/// <summary>How a tournament's matches were drawn. Rows written before 2026-10-29 read as Bracket.</summary>
public enum TournamentFormat
{
    /// <summary>Single elimination, seeded so the top two can only meet in the final.</summary>
    Bracket = 0,

    /// <summary>
    /// Three rounds paired by score — retired on 2026-11-07. Stored runs are shown read-only,
    /// with their table; none can be drawn, run or resumed.
    /// </summary>
    Swiss = 1,
}

/// <summary>One row of a stored Swiss run's table, as the page renders it.</summary>
public sealed class SwissStandingDto
{
    public int Rank { get; init; }
    public ModelId ModelId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Seed { get; init; }
    public double Points { get; init; }
    public double Buchholz { get; init; }
    public int Wins { get; init; }
    public int Draws { get; init; }
    public int Losses { get; init; }
}

/// <summary>One match position in a bracket, as the page renders it.</summary>
public sealed class TournamentMatchDto
{
    public int Round { get; init; }
    public int Index { get; init; }

    /// <summary>"Round of 16" / "Quarter-finals" / "Semi-finals" / "Final", counted back from the last round.</summary>
    public string RoundName { get; init; } = string.Empty;

    public ModelId SlotAModelId { get; init; }
    public string SlotAName { get; init; } = string.Empty;

    /// <summary>1-based seed, carried with the model as it advances through the bracket.</summary>
    public int SlotASeed { get; init; }

    public ModelId SlotBModelId { get; init; }
    public string SlotBName { get; init; } = string.Empty;

    /// <inheritdoc cref="SlotASeed"/>
    public int SlotBSeed { get; init; }

    /// <summary>The duel this match was run as, once it has started. Links to the Arena.</summary>
    public DuelId? DuelId { get; init; }

    public ModelId? WinnerModelId { get; init; }
    public string? WinnerName { get; init; }

    /// <summary>A game of a stored Swiss run that the judge called a draw: half a point each.</summary>
    public bool IsDraw { get; init; }

    /// <summary>
    /// True when the judge called it a draw and the better seed advanced on the tie-break. The
    /// bracket says so rather than showing it as a won match.
    /// </summary>
    public bool WonOnSeedTieBreak { get; init; }

    /// <summary>Set when this match could not be decided; the run stops rather than guessing.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Both contestants known, so the match can be run.</summary>
    public bool IsReady => !SlotAModelId.IsEmpty && !SlotBModelId.IsEmpty;

    public bool IsDecided => WinnerModelId is not null || IsDraw;

    /// <summary>
    /// A first-round seed the field did not fill: its opponent went through at the draw, and no
    /// duel was ever run. Counted out of the page's "N of M matches" — nobody played it.
    /// </summary>
    public bool IsBye => IsDecided && DuelId is null && (SlotAModelId.IsEmpty || SlotBModelId.IsEmpty);

    /// <summary>Started but not yet decided — this is the match the page should point at.</summary>
    public bool IsRunning => DuelId is not null && !IsDecided && FailureReason is null;
}

/// <summary>A full bracket run.</summary>
public sealed class TournamentDto
{
    public TournamentId TournamentId { get; init; }
    public TournamentStatus Status { get; init; }

    /// <summary>Bracket, or a stored (read-only) Swiss run.</summary>
    public TournamentFormat Format { get; init; }

    /// <summary>
    /// 2 or 16 (8 before 2026-11-07). Two is a plain 1v1 through the same machinery; a 16 with
    /// fewer entrants fills the missing seeds with byes.
    /// </summary>
    public int Size { get; init; }

    /// <summary>Single-line rendering of the prompt every match in the bracket receives.</summary>
    public string PromptText { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? OwnerId { get; init; }

    public ModelId? ChampionModelId { get; init; }
    public string? ChampionName { get; init; }

    /// <summary>Why the run stopped without a champion, when <see cref="Status"/> is Abandoned.</summary>
    public string? AbandonedReason { get; init; }

    /// <summary>Every match, round 0 first, top of the bracket first within a round, byes included.</summary>
    public IReadOnlyList<TournamentMatchDto> Matches { get; init; } = [];

    /// <summary>A stored Swiss run's table, best first. Empty for a bracket.</summary>
    public IReadOnlyList<SwissStandingDto> Standings { get; init; } = [];

    /// <summary>Matches decided by a duel — byes are not counted.</summary>
    public int DecidedCount => Matches.Count(m => m.IsDecided && !m.IsBye);

    /// <summary>Matches that are actually played: the bracket minus its byes.</summary>
    public int TotalMatches => Matches.Count(m => !m.IsBye);
}

/// <summary>A model eligible to enter a bracket, as the setup form lists it.</summary>
/// <remarks>
/// Carries the standing so the form can show what the seeding will be before the user commits:
/// the bracket is seeded by ELO, so which models are picked determines who meets whom.
/// </remarks>
public sealed class TournamentEntrantDto
{
    public ModelId ModelId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public ModelType ModelType { get; init; }
    public double CurrentElo { get; init; }
}
