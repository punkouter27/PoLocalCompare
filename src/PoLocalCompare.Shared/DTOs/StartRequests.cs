using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Shared.DTOs;

/// <summary>Body of <c>POST /api/duels</c>. Validated by <c>CommenceDuelRequestValidator</c>.</summary>
/// <param name="AutoJudgeDelaySeconds">
/// Optional per-duel grace window. Omit to use <c>AiJudge:DelaySeconds</c>; a tournament sends 0
/// so an unattended run is never waiting on a human pick that will not come.
/// </param>
public sealed record CommenceDuelRequest(
    ModelId LeftModelId,
    ModelId RightModelId,
    string PromptText,
    int? AutoJudgeDelaySeconds = null);

/// <summary>Body of <c>POST /api/tournaments</c>. Validated by <c>CreateTournamentRequestValidator</c>.</summary>
/// <param name="ModelIds">
/// The field, in any order — the handler seeds it by rating. Must hold 2, or 9 to 16, distinct ids.
/// </param>
/// <param name="PromptText">The single prompt every match in the bracket receives.</param>
public sealed record CreateTournamentRequest(
    IReadOnlyList<ModelId>? ModelIds,
    string? PromptText);
