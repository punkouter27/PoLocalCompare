namespace PoLocalCompare.Api.Features.Leaderboard;

/// <summary>
/// Pure arithmetic for the leaderboard's win-rate column.
/// </summary>
/// <remarks>
/// Win rate is <c>WinCount / DuelCount</c> — judged draws are not wins, so a model that drew
/// every duel shows 0% even though its W/L/T reads 0/0/N. A model that never competed shows
/// 0 (not NaN) so the column renders cleanly. It exists as a named helper because the
/// <see cref="GetLeaderboardHandler"/> projection was silently dropping the field (the
/// <c>LeaderboardEntryDto.WinRate</c> property stayed at its C# default of 0) and that bug
/// recurred every time the handler was edited — a named, unit-tested helper pins the
/// behaviour so the next refactor can't quietly remove it again.
/// </remarks>
public static class WinRateCalculator
{
    /// <returns>
    /// <c>0.0</c> when <paramref name="duelCount"/> is non-positive (never-competed safety),
    /// otherwise <c>winCount / duelCount</c> as a fraction in [0, 1].
    /// </returns>
    public static double Calculate(int winCount, int duelCount)
    {
        if (duelCount <= 0) return 0.0;
        return (double)winCount / duelCount;
    }
}
