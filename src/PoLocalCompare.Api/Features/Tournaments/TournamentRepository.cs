// GoF: Repository pattern
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Tournaments;

public sealed class TournamentRepository
{
    private const string TableName = "Tournaments";

    /// <summary>
    /// One partition for every tournament. Unlike duels — which are partitioned by month because
    /// the archive pages through years of them — a bracket is always read by id, and the listing
    /// is a short "recent runs" strip. A single partition keeps that listing a single ordered
    /// query instead of a fan-out across months.
    /// </summary>
    private const string PartitionKey = "Tournament";

    /// <summary>
    /// Matches are stored as one JSON column. See <see cref="Tournament"/> for why a bracket is
    /// a single row; the practical consequence is that this converter set has to round-trip the
    /// strongly-typed ids, which it does because each carries its own JsonConverter attribute.
    /// </summary>
    private static readonly JsonSerializerOptions MatchSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly TableClient _tableClient;

    public TournamentRepository(TableServiceClient tableServiceClient)
    {
        _tableClient = tableServiceClient.GetTableClient(TableName);
    }

    /// <summary>
    /// Reads one bracket. A filtered single-partition query rather than a point read, because
    /// the RowKey is time-ordered rather than the id (see <see cref="RowKeyFor"/>) — the id
    /// alone does not name the row.
    /// </summary>
    public async Task<Tournament?> GetByIdAsync(TournamentId tournamentId)
    {
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: TableClient.CreateQueryFilter(
                $"PartitionKey eq {PartitionKey} and TournamentId eq {tournamentId.Value}"),
            maxPerPage: 1))
        {
            return MapToTournament(entity);
        }

        return null;
    }

    public async Task SaveAsync(Tournament tournament)
    {
        try
        {
            await _tableClient.AddEntityAsync(MapToEntity(tournament));
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Idempotent create (standards §5.5): a retried request, not a second bracket.
        }
    }

    public async Task UpdateAsync(Tournament tournament)
    {
        var entity = MapToEntity(tournament);
        var etag = string.IsNullOrEmpty(tournament.ETag) ? ETag.All : new ETag(tournament.ETag);

        var response = await _tableClient.UpdateEntityAsync(entity, etag, TableUpdateMode.Replace);
        tournament.ETag = response.Headers.ETag?.ToString();
    }

    /// <summary>Newest first — the RowKey is inverted ticks, so storage order is already right.</summary>
    public async Task<IEnumerable<Tournament>> ListRecentAsync(int limit)
    {
        var tournaments = new List<Tournament>();
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: TableClient.CreateQueryFilter($"PartitionKey eq {PartitionKey}"),
            maxPerPage: limit))
        {
            tournaments.Add(MapToTournament(entity));
            if (tournaments.Count >= limit) break;
        }

        return tournaments;
    }

    /// <summary>
    /// Inverted ticks then the id, so a plain partition scan reads newest-first without sorting —
    /// the same trick <see cref="EloRecord"/> uses for its history rows. The id is appended so
    /// two brackets drawn within the same tick cannot collide on the key.
    /// </summary>
    private static string RowKeyFor(Tournament tournament) =>
        $"{long.MaxValue - tournament.CreatedAt.Ticks:D19}_{tournament.TournamentId.Value}";

    private static TableEntity MapToEntity(Tournament tournament)
    {
        return new TableEntity(PartitionKey, RowKeyFor(tournament))
        {
            ["TournamentId"] = tournament.TournamentId.Value,
            ["Format"] = tournament.Format.ToString(),
            ["Size"] = tournament.Size,
            ["PromptText"] = tournament.PromptText,
            ["Status"] = tournament.Status.ToString(),
            ["CreatedAt"] = tournament.CreatedAt,
            ["CompletedAt"] = tournament.CompletedAt,
            ["OwnerId"] = tournament.OwnerId,
            ["ChampionModelId"] = tournament.ChampionModelId?.Value,
            ["ChampionName"] = tournament.ChampionName,
            ["AbandonedReason"] = tournament.AbandonedReason,
            ["MatchesJson"] = JsonSerializer.Serialize(tournament.Matches, MatchSerializerOptions),
        };
    }

    private static Tournament MapToTournament(TableEntity entity)
    {
        var matchesJson = entity.GetString("MatchesJson");
        var matches = string.IsNullOrWhiteSpace(matchesJson)
            ? []
            : JsonSerializer.Deserialize<List<TournamentMatch>>(matchesJson, MatchSerializerOptions) ?? [];

        return new Tournament
        {
            TournamentId = TournamentId.FromOrDefault(entity.GetString("TournamentId")),
            // Rows from before 2026-10-29 have no Format column; every one of them was a bracket.
            Format = Enum.TryParse<TournamentFormat>(entity.GetString("Format"), out var format)
                ? format
                : TournamentFormat.Bracket,
            Size = entity.GetInt32("Size") ?? 0,
            PromptText = entity.GetString("PromptText") ?? string.Empty,
            Status = Enum.TryParse<TournamentStatus>(entity.GetString("Status"), out var status)
                ? status
                : TournamentStatus.Pending,
            CreatedAt = entity.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue,
            CompletedAt = entity.GetDateTimeOffset("CompletedAt"),
            OwnerId = entity.GetString("OwnerId"),
            ChampionModelId = ModelId.FromOrNull(entity.GetString("ChampionModelId")),
            ChampionName = entity.GetString("ChampionName"),
            AbandonedReason = entity.GetString("AbandonedReason"),
            Matches = matches,
            ETag = entity.ETag.ToString(),
        };
    }
}
