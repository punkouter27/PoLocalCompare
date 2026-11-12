using PoLocalCompare.Shared.Ids;
namespace PoLocalCompare.Shared.DTOs;

public sealed class HeadToHeadDto
{
    public ModelId ModelIdA { get; init; }
    public ModelId ModelIdB { get; init; }
    public ModelId OpponentModelId { get; init; }
    public string OpponentName { get; init; } = string.Empty;
    public int TotalDuels { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public int Draws { get; init; }
    public double AvgEloShiftA { get; init; }
    public double AvgEloShiftB { get; init; }
    public DuelId? LastDuelId { get; init; }
    public DateTimeOffset? LastDuelAt { get; init; }
}
