using PoLocalCompare.Api.Features.Leaderboard;

namespace PoLocalCompare.Unit;

/// <summary>
/// The leaderboard's "W/L/T (Win %)" column reads <see cref="WinRateCalculator.Calculate"/>.
/// These tests pin the cases the projection has to handle, including the
/// never-competed branch (must be 0, not NaN, so the column renders).
/// </summary>
public class WinRateCalculatorTests
{
    /// <summary>
    /// The DTO's WinRate is a plain double — NaN would print as "NaN%" on the leaderboard, which
    /// is why "never competed" must explicitly be 0. (The typical-record and negative-input
    /// cases made way on 2026-10-26 for the judge-shape and cost tests; one division is not
    /// worth a case of the 100.)
    /// </summary>
    [Fact]
    public void Calculate_NeverCompeted_IsZeroNotNaN()
    {
        Assert.Equal(0.0, WinRateCalculator.Calculate(winCount: 0, duelCount: 0), 10);
    }
}