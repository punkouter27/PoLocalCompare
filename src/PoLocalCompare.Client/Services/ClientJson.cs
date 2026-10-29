using System.Text.Json;
using System.Text.Json.Serialization;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// Source-generated JSON metadata for every body the client sends or reads.
/// </summary>
/// <remarks>
/// Replaces a reflection-based <c>JsonSerializerOptions</c>: the serializer no longer discovers
/// DTO shapes at runtime, which is what the 24 IL2026 trim warnings on DuelApiClient were about.
/// Same contract as before — web defaults (camelCase, case-insensitive reads) and enums as
/// strings — declared once here, so enum casing still cannot drift between endpoints. A new DTO
/// the client reads or posts needs a <c>[JsonSerializable]</c> line below; without one the call
/// does not compile, rather than failing at runtime.
/// </remarks>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(CommenceDuelBody))]
[JsonSerializable(typeof(LocalResultBody))]
[JsonSerializable(typeof(CreateTournamentBody))]
[JsonSerializable(typeof(RefinePromptRequest))]
[JsonSerializable(typeof(RefinePromptResponse))]
[JsonSerializable(typeof(VerdictRequestDto))]
[JsonSerializable(typeof(VerdictResponseDto))]
[JsonSerializable(typeof(AddDiscoveredModelRequest))]
[JsonSerializable(typeof(DuelDto))]
[JsonSerializable(typeof(IReadOnlyList<DuelSummaryDto>))]
[JsonSerializable(typeof(IReadOnlyList<LeaderboardEntryDto>))]
[JsonSerializable(typeof(ModelProfileDto))]
[JsonSerializable(typeof(ScoutingReportDto))]
[JsonSerializable(typeof(IReadOnlyList<TournamentEntrantDto>))]
[JsonSerializable(typeof(TournamentDto))]
[JsonSerializable(typeof(IReadOnlyList<TournamentDto>))]
[JsonSerializable(typeof(ModelDto))]
[JsonSerializable(typeof(IReadOnlyList<ModelDto>))]
[JsonSerializable(typeof(IReadOnlyList<ModelAvailabilityDto>))]
[JsonSerializable(typeof(ModelDiscoveryDto))]
[JsonSerializable(typeof(DuelApiClient.ProblemDetailsLite))]
[JsonSerializable(typeof(DuelApiClient.SmokeSnapshotDto))]
[JsonSerializable(typeof(BffAuthenticationStateProvider.AuthMe))]
internal sealed partial class ClientJson : JsonSerializerContext;

/// <summary><c>POST /api/duels</c>.</summary>
internal sealed record CommenceDuelBody(string LeftModelId, string RightModelId, string PromptText, int? AutoJudgeDelaySeconds);

/// <summary><c>POST /api/duels/{id}/local-result</c> — a browser model's finished run.</summary>
internal sealed record LocalResultBody(
    ModelId ModelId,
    string HtmlOutputRaw,
    int TokenCount,
    long TotalDurationMs,
    long WarmUpDurationMs,
    bool IsFailure,
    string? FailureReason);

/// <summary><c>POST /api/tournaments</c>.</summary>
internal sealed record CreateTournamentBody(IReadOnlyList<ModelId> ModelIds, string PromptText, TournamentFormat Format);
