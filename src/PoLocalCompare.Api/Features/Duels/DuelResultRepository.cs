// GoF: Repository pattern
using Azure.Data.Tables;
using Azure.Storage.Blobs;

namespace PoLocalCompare.Api.Features.Duels;

public sealed class DuelResultRepository : IDuelResultRepository
{
    private const string TableName = "DuelResults";
    private const string BlobContainerName = "duel-html-outputs";
    private const long MaxTablePropertyBytes = 64 * 1024; // 64KB

    /// <summary>
    /// Column holding an overflow blob's path *relative to the container*. Only this repository
    /// ever writes it, which is the whole point: the pointer lives outside the content column, so
    /// nothing a caller supplies is ever interpreted as one.
    /// </summary>
    private const string BlobPathColumn = "HtmlOutputBlobPath";

    /// <summary>
    /// Legacy in-band overflow marker. Rows written before 2026-09-02 stored the pointer *inside*
    /// <c>HtmlOutputRaw</c> as <c>blob://{absolute uri}</c>, and the read path dereferenced any
    /// value carrying that prefix. Because an output under the 64KB threshold is persisted
    /// verbatim, a caller could post the literal string <c>blob://http://10.0.0.5/…</c> and turn
    /// the next read into a server-side GET of an attacker-chosen host, with the response body
    /// handed back through <c>GET /api/duels/{id}</c>. The prefix is still recognised so existing
    /// rows keep resolving, but only after the URI is proven to address our own container.
    /// </summary>
    private const string LegacyBlobPrefix = "blob://";

    private readonly TableClient _tableClient;
    private readonly BlobContainerClient _blobContainerClient;
    private readonly ILogger<DuelResultRepository> _logger;

    public DuelResultRepository(
        TableServiceClient tableServiceClient,
        BlobServiceClient blobServiceClient,
        ILogger<DuelResultRepository> logger)
    {
        _tableClient = tableServiceClient.GetTableClient(TableName);
        _blobContainerClient = blobServiceClient.GetBlobContainerClient(BlobContainerName);
        _logger = logger;
    }

    public async Task SaveAsync(DuelResult result)
    {
        var htmlOutput = result.HtmlOutputRaw;
        var htmlBytes = System.Text.Encoding.UTF8.GetByteCount(htmlOutput);
        string? blobPath = null;

        // If output exceeds 64KB, store in Blob Storage and keep only the container-relative
        // path — in its own column, never spliced into the content the caller supplied.
        if (htmlBytes > MaxTablePropertyBytes)
        {
            await _blobContainerClient.CreateIfNotExistsAsync();
            blobPath = $"{result.DuelId}/{result.ModelId}.html";
            var blobClient = _blobContainerClient.GetBlobClient(blobPath);
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(htmlOutput));
            await blobClient.UploadAsync(stream, overwrite: true);
            htmlOutput = string.Empty;
        }

        var entity = MapToEntity(result, htmlOutput, blobPath);
        await _tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
    }

    public async Task<DuelResult?> GetAsync(DuelId duelId, ModelId modelId)
    {
        try
        {
            var response = await _tableClient.GetEntityAsync<TableEntity>(duelId, modelId);
            return await MapToResultAsync(response.Value);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IEnumerable<DuelResult>> GetByDuelIdAsync(DuelId duelId)
    {
        var results = new List<DuelResult>();
        // CreateQueryFilter escapes the value instead of pasting it into the OData expression.
        // Ids are not shape-validated (DuelId.From accepts any non-blank string so legacy rows
        // keep round-tripping), so a route-supplied id reaches here verbatim and a raw
        // interpolation would let `x' or PartitionKey ne 'x` widen the query to every partition.
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: TableClient.CreateQueryFilter($"PartitionKey eq {duelId.Value}")))
        {
            results.Add(await MapToResultAsync(entity));
        }
        return results;
    }

    public async Task<IEnumerable<DuelResult>> GetByModelIdAsync(ModelId modelId)
    {
        var results = new List<DuelResult>();
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: TableClient.CreateQueryFilter($"RowKey eq {modelId.Value}")))
        {
            results.Add(await MapToResultAsync(entity));
        }
        return results;
    }

    /// <remarks>
    /// The partition key is the duel id, a ULID, whose first ten characters are its creation
    /// time — so a time window is a key range and this never scans the table. Bounded above as
    /// well as below: legacy non-ULID ids sort after every ULID and would otherwise be counted
    /// into every day forever. Only the cost column crosses the wire.
    /// </remarks>
    public async Task<double> SumApiCostAsync(DateTimeOffset from, DateTimeOffset to)
    {
        var lower = NUlid.Ulid.NewUlid(from).ToString()[..10];
        var upper = NUlid.Ulid.NewUlid(to).ToString()[..10];

        var total = 0.0;
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
            filter: TableClient.CreateQueryFilter($"PartitionKey ge {lower} and PartitionKey lt {upper}"),
            select: ["ApiCostUsd"]))
        {
            total += entity.GetDouble("ApiCostUsd") ?? 0;
        }
        return total;
    }

    private static TableEntity MapToEntity(DuelResult result, string htmlOutput, string? blobPath)
    {
        return new TableEntity(result.DuelId, result.ModelId)
        {
            ["WarmUpDurationMs"] = result.WarmUpDurationMs,
            ["GenerationDurationMs"] = result.GenerationDurationMs,
            ["TotalDurationMs"] = result.TotalDurationMs,
            ["TokenCount"] = result.TokenCount,
            ["PromptTokenCount"] = result.PromptTokenCount,
            ["ReasoningTokenCount"] = result.ReasoningTokenCount,
            ["TokenVelocity"] = result.TokenVelocity,
            ["FinishReason"] = result.FinishReason,
            ["WasTruncated"] = result.WasTruncated,
            ["HtmlOutputRaw"] = htmlOutput,
            [BlobPathColumn] = blobPath,
            ["HtmlOutputSizeBytes"] = result.HtmlOutputSizeBytes,
            ["CharacterDensityRatio"] = result.CharacterDensityRatio,
            ["OutputQualityScore"] = result.OutputQualityScore,
            ["IsFailure"] = result.IsFailure,
            ["FailureReason"] = result.FailureReason,
            ["EnergyWh"] = result.EnergyWh,
            ["EnergyCostUsd"] = result.EnergyCostUsd,
            ["ApiCostUsd"] = result.ApiCostUsd,
            ["ServedModel"] = result.ServedModel
        };
    }

    private async Task<DuelResult> MapToResultAsync(TableEntity entity)
    {
        var htmlOutput = entity.GetString("HtmlOutputRaw") ?? string.Empty;

        // Resolve an overflow blob. The path comes from our own column on current rows, or from
        // the legacy in-band prefix on older ones — and either way it is resolved against
        // _blobContainerClient, so the account and container are fixed here in the server rather
        // than taken from the row.
        var blobPath = entity.GetString(BlobPathColumn);
        var isLegacyPointer = string.IsNullOrEmpty(blobPath)
            && htmlOutput.StartsWith(LegacyBlobPrefix, StringComparison.OrdinalIgnoreCase);

        if (isLegacyPointer)
            blobPath = ResolveLegacyBlobPath(htmlOutput[LegacyBlobPrefix.Length..]);

        if (!string.IsNullOrEmpty(blobPath))
        {
            htmlOutput = await DownloadAsync(blobPath, entity.PartitionKey, entity.RowKey);
        }
        else if (isLegacyPointer)
        {
            // The prefix is there but the URI does not address our container: a forged pointer,
            // or a row from a storage account this instance is not configured for. Never
            // dereference it — drop the value and let the row read as empty output.
            _logger.LogWarning(
                "Duel result {DuelId}/{ModelId} carries a blob pointer outside this account; ignoring it.",
                entity.PartitionKey,
                entity.RowKey);
            htmlOutput = string.Empty;
        }

        return new DuelResult
        {
            DuelId = DuelId.FromOrDefault(entity.PartitionKey),
            ModelId = ModelId.FromOrDefault(entity.RowKey),
            WarmUpDurationMs = entity.GetInt64("WarmUpDurationMs") ?? 0,
            GenerationDurationMs = entity.GetInt64("GenerationDurationMs") ?? 0,
            TotalDurationMs = entity.GetInt64("TotalDurationMs") ?? 0,
            TokenCount = entity.GetInt32("TokenCount") ?? 0,
            PromptTokenCount = entity.GetInt32("PromptTokenCount"),
            ReasoningTokenCount = entity.GetInt32("ReasoningTokenCount"),
            TokenVelocity = entity.GetDouble("TokenVelocity") ?? 0,
            FinishReason = entity.GetString("FinishReason"),
            WasTruncated = entity.GetBoolean("WasTruncated") ?? false,
            HtmlOutputRaw = htmlOutput,
            HtmlOutputSizeBytes = entity.GetInt64("HtmlOutputSizeBytes") ?? 0,
            CharacterDensityRatio = entity.GetDouble("CharacterDensityRatio") ?? 0,
            OutputQualityScore = entity.GetInt32("OutputQualityScore") ?? HtmlOutputQualityScorer.Score(htmlOutput),
            IsFailure = entity.GetBoolean("IsFailure") ?? false,
            FailureReason = entity.GetString("FailureReason"),
            ServedModel = entity.GetString("ServedModel"),
            EnergyWh = entity.GetDouble("EnergyWh"),
            EnergyCostUsd = entity.GetDouble("EnergyCostUsd"),
            ApiCostUsd = entity.GetDouble("ApiCostUsd")
        };
    }

    /// <summary>
    /// Maps a legacy <c>blob://{absolute uri}</c> pointer back to a container-relative path, or
    /// null when the URI does not address this repository's own container. Scheme, host, port and
    /// container path must all match — anything else is a pointer we did not write, and the one
    /// safe response is to refuse rather than fetch it.
    /// </summary>
    private string? ResolveLegacyBlobPath(string reference)
    {
        if (!Uri.TryCreate(reference, UriKind.Absolute, out var uri))
            return null;

        var container = _blobContainerClient.Uri;

        if (!string.Equals(uri.Scheme, container.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, container.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != container.Port)
        {
            return null;
        }

        // Azurite addresses containers path-style ("/devstoreaccount1/duel-html-outputs/…") and
        // Azure host-style ("/duel-html-outputs/…"), so compare against the container's own path
        // rather than assuming either shape.
        var prefix = container.AbsolutePath.TrimEnd('/') + "/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var path = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
        return string.IsNullOrEmpty(path) ? null : path;
    }

    private async Task<string> DownloadAsync(string blobPath, string duelId, string modelId)
    {
        try
        {
            var response = await _blobContainerClient.GetBlobClient(blobPath).DownloadContentAsync();
            return response.Value.Content.ToString();
        }
        catch (Azure.RequestFailedException ex)
        {
            // A missing or unreadable overflow blob must not take the whole duel down with it —
            // the timings and verdict inputs on the rest of the row are still worth returning.
            _logger.LogWarning(
                ex,
                "Overflow blob {BlobPath} for duel result {DuelId}/{ModelId} could not be read.",
                blobPath,
                duelId,
                modelId);
            return string.Empty;
        }
    }
}
