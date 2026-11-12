// SOLID: Single Responsibility — pure ELO formula only
namespace PoLocalCompare.Api.Features.Leaderboard;

public static class EloCalculator
{
    /// <summary>
    /// Calculates updated ELO ratings for both players after a match.
    /// Standard ELO formula: E_a = 1/(1+10^((Rb-Ra)/400)), R'_a = Ra + K*(Sa - Ea)
    /// </summary>
    /// <param name="ratingA">Current ELO of player A (winner if outcomeA = 1)</param>
    /// <param name="ratingB">Current ELO of player B</param>
    /// <param name="k">K-factor controlling rating change magnitude</param>
    /// <param name="outcomeA">Score for player A: 1.0 = win, 0.0 = loss, 0.5 = draw</param>
    /// <returns>New ELO ratings rounded to 1 decimal place</returns>
    public static (double NewRatingA, double NewRatingB) Calculate(
        double ratingA,
        double ratingB,
        double k,
        double outcomeA)
    {
        // Clamp expected probability to avoid division issues with extreme ratings
        var expectedA = Math.Clamp(1.0 / (1.0 + Math.Pow(10, (ratingB - ratingA) / 400.0)), 0.001, 0.999);
        var expectedB = 1.0 - expectedA;
        var outcomeB = 1.0 - outcomeA;

        var newRatingA = Math.Round(ratingA + k * (outcomeA - expectedA), 1);
        var newRatingB = Math.Round(ratingB + k * (outcomeB - expectedB), 1);

        // Ensure minimum 0.1 point shift for decisive outcomes (prevents rounding deadlocks)
        if (outcomeA is 1.0 or 0.0)
        {
            var shiftA = newRatingA - ratingA;
            var shiftB = newRatingB - ratingB;
            if (Math.Abs(shiftA) < 0.1 && Math.Abs(shiftA) > 0)
                newRatingA = ratingA + (shiftA >= 0 ? 0.1 : -0.1);
            if (Math.Abs(shiftB) < 0.1 && Math.Abs(shiftB) > 0)
                newRatingB = ratingB + (shiftB >= 0 ? 0.1 : -0.1);
        }

        return (newRatingA, newRatingB);
    }
}
