// GoF: Repository pattern
using Azure;
using Azure.Data.Tables;
using NUlid;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public sealed class DuelRepository : IDuelRepository
{
    private const string TableName = "Duels";

    private readonly TableClient _tableClient;

    public DuelRepository(TableServiceClient tableServiceClient)
    {
        _tableClient = tableServiceClient.GetTableClient(TableName);
    }

    public async Task<Duel?> GetByIdAsync(DuelId duelId)
    {
        // PartitionKey is YYYYMM derived from ULID timestamp
        var partitionKey = GetPartitionKey(duelId);
        try
        {
            var response = await _tableClient.GetEntityAsync<TableEntity>(partitionKey, duelId);
            return MapToDuel(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // If the ULID-derived partition key doesn't work, search all partitions
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(e => e.RowKey == duelId))
            {
                return MapToDuel(entity);
            }
            return null;
        }
    }

    public async Task SaveAsync(Duel duel)
    {
        var entity = MapToEntity(duel);
        try
        {
            await _tableClient.AddEntityAsync(entity);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // Idempotent create (standards §5.5): the duel already exists, e.g. a retried request.
        }
    }

    public async Task UpdateAsync(Duel duel)
    {
        var entity = MapToEntity(duel);
        // ETag-conditional replace (standards §5.5): a concurrent writer surfaces as 412 instead of a lost update.
        await _tableClient.UpdateEntityAsync(entity, TableETag.Parse(duel.ETag), TableUpdateMode.Replace);
    }

    public async Task<IEnumerable<Duel>> ListAsync(
        int limit,
        string? beforeMonth,
        DuelId? before = null,
        IReadOnlyCollection<DuelVerdict>? verdicts = null)
    {
        limit = Math.Clamp(limit, 1, 100);
        var duels = new List<Duel>();

        // Keyset cursor. Ids are ULIDs, so ordinal id order IS creation order and "strictly
        // older than the last row the client has" is `RowKey lt cursor` — in the cursor's own
        // month partition and every earlier one. The old cursor was a bare yyyyMM `le`, which
        // re-included the whole current month: Load More returned the same rows forever.
        var filter = before is { IsEmpty: false } cursor
            ? TableClient.CreateQueryFilter($"PartitionKey le {GetPartitionKey(cursor)} and RowKey lt {cursor.Value}")
            : string.IsNullOrEmpty(beforeMonth)
                ? null
                : TableClient.CreateQueryFilter($"PartitionKey le {beforeMonth}");

        // Table Storage doesn't order across partitions, so every matching row is read and the
        // newest `limit` taken in memory. The verdict filter runs here rather than in OData
        // because MapToDuel reads a row with no Verdict column as Pending, and an OData
        // `Verdict eq 'Pending'` would miss exactly those rows.
        // ponytail: full scan per page (~1 KB a row); walk month partitions newest-first and
        // stop at `limit` once the table holds more than a few thousand duels.
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(filter: filter, maxPerPage: 1000))
        {
            var duel = MapToDuel(entity);
            if (verdicts is not { Count: > 0 } || verdicts.Contains(duel.Verdict))
                duels.Add(duel);
        }

        // Newest first by id, the same key the cursor pages on and the Archive sorts by. It was
        // `CompletedAt ?? StartedAt` while the client sorted by StartedAt, so page boundaries
        // and on-screen order disagreed whenever a duel finished out of start order.
        return duels
            .OrderByDescending(d => d.DuelId)
            .Take(limit);
    }

    /// <remarks>
    /// Same shape as <c>DuelResultRepository.SumApiCostAsync</c>: a duel id is a ULID, so a time
    /// window is a row-key range, here inside the one or two yyyyMM partitions it can touch.
    /// Only the cost column crosses the wire.
    /// </remarks>
    public async Task<double> SumJudgeCostAsync(DateTimeOffset from, DateTimeOffset to)
    {
        var lower = Ulid.NewUlid(from).ToString()[..10];
        var upper = Ulid.NewUlid(to).ToString()[..10];
        var firstMonth = from.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
        var lastMonth = to.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);

        var total = 0.0;
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: TableClient.CreateQueryFilter(
                $"PartitionKey ge {firstMonth} and PartitionKey le {lastMonth} and RowKey ge {lower} and RowKey lt {upper}"),
            select: ["JudgeCostUsd"]))
        {
            total += entity.GetDouble("JudgeCostUsd") ?? 0;
        }
        return total;
    }

    private static string GetPartitionKey(DuelId duelId)
    {
        try
        {
            var ulid = Ulid.Parse(duelId);
            return ulid.Time.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return DateTimeOffset.UtcNow.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static TableEntity MapToEntity(Duel duel)
    {
        var partitionKey = GetPartitionKey(duel.DuelId);
        var entity = new TableEntity(partitionKey, duel.DuelId)
        {
            ["PromptText"] = duel.PromptText,
            ["PromptFull"] = duel.PromptFull,
            ["LeftModelId"] = duel.LeftModelId.Value,
            ["RightModelId"] = duel.RightModelId.Value,
            ["LeftModelName"] = duel.LeftModelName,
            ["RightModelName"] = duel.RightModelName,
            ["StartedAt"] = duel.StartedAt,
            ["CompletedAt"] = duel.CompletedAt,
            ["Verdict"] = duel.Verdict.ToString(),
            ["WinnerModelId"] = duel.WinnerModelId?.Value,
            ["LoserModelId"] = duel.LoserModelId?.Value,
            ["EloShiftWinner"] = duel.EloShiftWinner,
            ["EloShiftLoser"] = duel.EloShiftLoser,
            ["IsPartial"] = duel.IsPartial,
            ["VerdictSource"] = duel.VerdictSource.ToString(),
            ["JudgeRationale"] = duel.JudgeRationale,
            ["JudgeModel"] = duel.JudgeModel,
            ["JudgeAnalysis"] = duel.JudgeAnalysis,
            ["JudgeCostUsd"] = duel.JudgeCostUsd,
            ["JudgeStoodDownReason"] = duel.JudgeStoodDownReason,
            ["OwnerId"] = duel.OwnerId,
            ["VerdictBy"] = duel.VerdictBy,
        };
        return entity;
    }

    private static Duel MapToDuel(TableEntity entity)
    {
        var duel = new Duel
        {
            DuelId = DuelId.FromOrDefault(entity.RowKey),
            PromptText = entity.GetString("PromptText") ?? string.Empty,
            PromptFull = entity.GetString("PromptFull") ?? string.Empty,
            LeftModelId = ModelId.FromOrDefault(entity.GetString("LeftModelId")),
            RightModelId = ModelId.FromOrDefault(entity.GetString("RightModelId")),
            // Null on rows written before the snapshot existed; readers fall back to the
            // live catalog and then to a neutral label, so old rows are no worse than before.
            LeftModelName = entity.GetString("LeftModelName"),
            RightModelName = entity.GetString("RightModelName"),
            StartedAt = entity.GetDateTimeOffset("StartedAt") ?? DateTimeOffset.MinValue,
            CompletedAt = entity.GetDateTimeOffset("CompletedAt"),
            Verdict = Enum.TryParse<DuelVerdict>(entity.GetString("Verdict"), out var v) ? v : DuelVerdict.Pending,
            WinnerModelId = ModelId.FromOrNull(entity.GetString("WinnerModelId")),
            LoserModelId = ModelId.FromOrNull(entity.GetString("LoserModelId")),
            EloShiftWinner = entity.GetDouble("EloShiftWinner"),
            EloShiftLoser = entity.GetDouble("EloShiftLoser"),
            IsPartial = entity.GetBoolean("IsPartial") ?? false,
            // Rows written before the auto-judge existed have no VerdictSource — they were
            // all human decisions, which is what the Human fallback says.
            VerdictSource = Enum.TryParse<VerdictSource>(entity.GetString("VerdictSource"), out var vs)
                ? vs
                : VerdictSource.Human,
            JudgeRationale = entity.GetString("JudgeRationale"),
            JudgeModel = entity.GetString("JudgeModel"),
            JudgeAnalysis = entity.GetString("JudgeAnalysis"),
            JudgeCostUsd = entity.GetDouble("JudgeCostUsd"),
            JudgeStoodDownReason = entity.GetString("JudgeStoodDownReason"),
            // Both nullable so rows written before the schema addition still deserialise.
            OwnerId = entity.GetString("OwnerId"),
            VerdictBy = entity.GetString("VerdictBy"),
            ETag = entity.ETag.ToString(),
        };
        return duel;
    }
}