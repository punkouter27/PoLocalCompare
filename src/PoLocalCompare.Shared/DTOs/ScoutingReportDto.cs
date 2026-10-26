using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Shared.DTOs;

/// <summary>
/// What the judge's notes say about one model across its recent duels — habits, not verdicts.
/// </summary>
public sealed class ScoutingReportDto
{
    public ModelId ModelId { get; init; }

    /// <summary>One or two sentences.</summary>
    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<string> Strengths { get; init; } = [];

    public IReadOnlyList<string> Weaknesses { get; init; } = [];

    /// <summary>How many judged duels the report was written from.</summary>
    public int DuelsRead { get; init; }

    public DateTimeOffset WrittenAt { get; init; }
}
