// GoF: Repository pattern
using Azure;
using Azure.Data.Tables;
using NUlid;

namespace PoLocalCompare.Api.Features.Duels;

/// <summary>
/// Paid model calls that have no duel row to carry their cost: prompt refinement and
/// scouting reports. <see cref="SpendGuard"/> adds today's total to the duel and judge spend.
/// </summary>
/// <remarks>
/// Before 2026-10-26 a refine call was "recorded nowhere, because there is no duel row yet to
/// carry it" — small per call, but invisible to the cap. One row per call, partitioned by UTC day,
/// so the guard's daily sum is a single-partition read. Recording is best-effort: a storage hiccup
/// must not fail the feature that already paid for its answer.
/// </remarks>
public sealed class SpendLedger(TableServiceClient tables, ILogger<SpendLedger> logger)
{
    public const string TableName = "SpendLedger";

    private readonly TableClient _table = tables.GetTableClient(TableName);

    public async Task RecordAsync(string purpose, string deployment, double? costUsd)
    {
        if (costUsd is not > 0) return;
        var now = DateTimeOffset.UtcNow;
        try
        {
            await _table.AddEntityAsync(new TableEntity(now.ToString("yyyyMMdd"), Ulid.NewUlid(now).ToString())
            {
                ["Purpose"] = purpose,
                ["Deployment"] = deployment,
                ["CostUsd"] = costUsd.Value,
            });
        }
        catch (RequestFailedException ex)
        {
            logger.LogWarning(ex, "Could not record {Purpose} spend of ${Cost} on {Deployment}.", purpose, costUsd, deployment);
        }
    }

    /// <summary>Total recorded for the UTC day that starts at <paramref name="day"/>.</summary>
    public async Task<double> SumAsync(DateTimeOffset day)
    {
        var total = 0.0;
        await foreach (var entity in _table.QueryAsync<TableEntity>(
            filter: TableClient.CreateQueryFilter($"PartitionKey eq {day.ToString("yyyyMMdd")}"),
            select: ["CostUsd"]))
        {
            total += entity.GetDouble("CostUsd") ?? 0;
        }
        return total;
    }
}
