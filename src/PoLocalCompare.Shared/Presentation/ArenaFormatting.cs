using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Shared.Presentation;

/// <summary>
/// Pure presentation helpers used across the Arena page (and the share-card renderer that
/// runs the same numbers). They were a flock of private statics on <c>Arena.razor</c>, which
/// made them un-shareable and easy to drift between call sites — the share-card stat string
/// had to repeat the rules for ELO formatting and cost rounding to render identically.
/// </summary>
public static class ArenaFormatting
{
    /// <summary>
    /// Currency-style cost string with four decimals. A <c>null</c> value renders as
    /// <c>"0 (no rate)"</c> because a "free" model and a model without a price record must
    /// read differently — the latter is unknown, not zero.
    /// </summary>
    public static string FormatCost(double? value) =>
        value.HasValue ? value.Value.ToString("F4") : "0 (no rate)";

    /// <summary>
    /// ELO deltas are stored with a tenth to leave room for fractional ratings, but a typical
    /// shift is a whole number (K=32, draw or near-draw outcomes). Showing "16.0" instead of
    /// "16" was a presentation bug — the badge looked like a remaining-decimal continuation,
    /// not a finished number. Whole values render as an integer; everything else keeps one
    /// decimal so the precision of the original record is visible.
    /// </summary>
    public static string FormatElo(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.05
            ? ((int)Math.Round(value)).ToString()
            : value.ToString("F1");
}
