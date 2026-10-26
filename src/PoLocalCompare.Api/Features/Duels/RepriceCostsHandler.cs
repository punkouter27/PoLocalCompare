using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Hybrid;

namespace PoLocalCompare.Api.Features.Duels;

public sealed record RepricedRow(string DuelId, string Model, double Stored, double Repriced);

public sealed record RepriceReport(
    bool Applied,
    int Scanned,
    int Mismatched,
    double StoredTotalUsd,
    double RepricedTotalUsd,
    IReadOnlyList<RepricedRow> Rows);

/// <summary>
/// Recomputes each stored result's <c>ApiCostUsd</c> from its own token counts at the model's
/// current rate, and reports (or, with <c>apply</c>, rewrites) the ones that disagree.
/// </summary>
/// <remarks>
/// <para>
/// A cost is written once, when the duel runs, at whatever rate the model row carried that
/// day. <see cref="DefaultPriceBook"/> matched the first prefix rather than the longest until
/// 2026-09-26, so results recorded before the fix priced the Mini and Nano tiers at their
/// parent model's rate. The fix corrected the rates and nothing went back for the rows: the
/// leaderboard's average cost, its Value column and the value chart were all still averaging
/// them in, which is how GPT-5.4 Nano came to look dearer per duel than GPT-5.4.
/// </para>
/// <para>
/// Token counts are the evidence here and are never touched; only the derived dollar figure
/// is. A row with no stored cost stays without one — it was unpriced when it ran, and
/// inventing a cost for it now would add spend to a day that did not have it. Dry run by
/// default, because it rewrites history.
/// </para>
/// </remarks>
public sealed class RepriceCostsHandler(
    TableServiceClient tables,
    IModelRepository models,
    HybridCache cache)
{
    /// <summary>Costs within this relative distance of each other are the same cost (float noise).</summary>
    private const double Tolerance = 0.005;

    public async Task<RepriceReport> HandleAsync(bool apply, CancellationToken cancellationToken)
    {
        var byId = (await models.GetAllAsync()).ToDictionary(m => m.ModelId.Value, StringComparer.Ordinal);
        var table = tables.GetTableClient("DuelResults");

        var rows = new List<RepricedRow>();
        var scanned = 0;
        double storedTotal = 0, repricedTotal = 0;

        await foreach (var entity in table.QueryAsync<TableEntity>(
            select: ["PartitionKey", "RowKey", "ApiCostUsd", "PromptTokenCount", "TokenCount", "ReasoningTokenCount"],
            cancellationToken: cancellationToken))
        {
            if (entity.GetDouble("ApiCostUsd") is not { } stored) continue;
            if (!byId.TryGetValue(entity.RowKey, out var model)) continue;

            scanned++;
            storedTotal += stored;

            // Same rule as FoundryInferenceProxy: the served model's rate wins when it is known.
            var served = DefaultPriceBook.Resolve(entity.GetString("ServedModel"));
            var repriced = TokenCost.Compute(
                served?.InputPerMillion ?? model.InputTokenPricePerMillion,
                served?.OutputPerMillion ?? model.OutputTokenPricePerMillion,
                entity.GetInt32("PromptTokenCount"), entity.GetInt32("TokenCount") ?? 0,
                FoundryChatRequest.ReportsReasoningSeparately(model.ApiEndpointRef) ? entity.GetInt32("ReasoningTokenCount") : null);

            if (repriced is not { } fresh || Math.Abs(fresh - stored) <= Tolerance * Math.Max(fresh, stored))
            {
                repricedTotal += stored;
                continue;
            }

            repricedTotal += fresh;
            rows.Add(new RepricedRow(entity.PartitionKey, model.DisplayName, stored, fresh));

            if (apply)
            {
                // Merge, so only this one column is written: the row also carries the output
                // itself (or its blob pointer), and a Replace built from a projected read
                // would drop every column the projection left out.
                var patch = new TableEntity(entity.PartitionKey, entity.RowKey) { ["ApiCostUsd"] = fresh };
                await table.UpdateEntityAsync(patch, Azure.ETag.All, TableUpdateMode.Merge, cancellationToken);
            }
        }

        if (apply && rows.Count > 0)
            await cache.RemoveByTagAsync(CacheTags.Leaderboard, cancellationToken);

        return new RepriceReport(apply, scanned, rows.Count, storedTotal, repricedTotal, rows);
    }
}
