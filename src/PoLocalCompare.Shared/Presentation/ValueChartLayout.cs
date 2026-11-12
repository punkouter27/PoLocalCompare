using System.Globalization;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Shared.Presentation;

/// <summary>One model placed on the leaderboard's value chart, in SVG user units.</summary>
/// <param name="OnFrontier">No cheaper-or-equal model is rated higher — the best buy at its price.</param>
public sealed record ValueChartPoint(LeaderboardEntryDto Entry, double X, double Y, bool OnFrontier);

/// <summary>
/// Geometry for the leaderboard's ELO-against-cost scatter: where each model sits, the axis
/// ticks, and which models form the value frontier. Pure, so the component is only markup.
/// </summary>
/// <remarks>
/// Cost is on a log axis because it spans three orders of magnitude between a nano model and a
/// flagship; on a linear one every cheap model collapses onto the left edge. Models with no
/// cost (browser, Ollama) or a zero one (mock inference) cannot sit on a log axis at all, so
/// they get a lane of their own to the left of it rather than being dropped — local against
/// remote is the comparison the app exists for.
/// </remarks>
public sealed class ValueChartLayout
{
    public const double Width = 720;
    public const double Height = 320;

    private const double Left = 52, Right = 24, Top = 20, Bottom = 40, FreeLaneWidth = 84;

    public IReadOnlyList<ValueChartPoint> Points { get; private init; } = [];
    public IReadOnlyList<(string Label, double X)> CostTicks { get; private init; } = [];
    public IReadOnlyList<(int Elo, double Y)> EloTicks { get; private init; } = [];

    /// <summary>Centre of the free lane, or null when every plotted model has a price.</summary>
    public double? FreeLaneX { get; private init; }

    /// <summary>Left edge of the gridlines, which run under the free lane as well.</summary>
    public const double GridLeft = Left;

    public double PlotLeft { get; private init; }
    public const double PlotRight = Width - Right;
    public const double PlotTop = Top;
    public const double PlotBottom = Height - Bottom;

    /// <summary>Null when there is nothing to compare: fewer than two models, or none priced.</summary>
    public static ValueChartLayout? Build(IReadOnlyList<LeaderboardEntryDto> entries)
    {
        var priced = entries.Where(e => e.AvgApiCostPerDuel is > 0).ToList();
        if (entries.Count < 2 || priced.Count == 0) return null;

        var hasFree = priced.Count < entries.Count;
        var plotLeft = Left + (hasFree ? FreeLaneWidth : 0);
        var plotRight = Width - Right;

        var lo = Math.Floor(Math.Log10(priced.Min(e => e.AvgApiCostPerDuel!.Value)));
        var hi = Math.Ceiling(Math.Log10(priced.Max(e => e.AvgApiCostPerDuel!.Value)));
        if (hi <= lo) hi = lo + 1;

        var step = entries.Max(e => e.CurrentElo) - entries.Min(e => e.CurrentElo) > 400 ? 100 : 50;
        var eloMin = Math.Floor(entries.Min(e => e.CurrentElo) / step) * step;
        var eloMax = Math.Ceiling(entries.Max(e => e.CurrentElo) / step) * step;
        if (eloMax <= eloMin) eloMax = eloMin + step;

        double X(double cost) => plotLeft + (Math.Log10(cost) - lo) / (hi - lo) * (plotRight - plotLeft);
        double Y(double elo) => Height - Bottom - (elo - eloMin) / (eloMax - eloMin) * (Height - Bottom - Top);

        double? freeX = hasFree ? Left + FreeLaneWidth / 2 : null;

        // Walk cheapest-first; a model is on the frontier when it beats everything cheaper.
        var best = double.NegativeInfinity;
        var points = new List<ValueChartPoint>();
        foreach (var e in entries
                     .OrderBy(e => e.AvgApiCostPerDuel is { } c and > 0 ? c : 0)
                     .ThenByDescending(e => e.CurrentElo))
        {
            var onFrontier = e.CurrentElo > best;
            if (onFrontier) best = e.CurrentElo;
            var x = e.AvgApiCostPerDuel is { } cost and > 0 ? X(cost) : freeX!.Value;
            points.Add(new ValueChartPoint(e, x, Y(e.CurrentElo), onFrontier));
        }

        return new ValueChartLayout
        {
            Points = points,
            FreeLaneX = freeX,
            PlotLeft = plotLeft,
            CostTicks = Enumerable.Range((int)lo, (int)(hi - lo) + 1)
                .Select(p => ("$" + Math.Pow(10, p).ToString("0.######", CultureInfo.InvariantCulture), X(Math.Pow(10, p))))
                .ToList(),
            EloTicks = Enumerable.Range(0, (int)((eloMax - eloMin) / step) + 1)
                .Select(i => ((int)(eloMin + i * step), Y(eloMin + i * step)))
                .ToList(),
        };
    }
}
