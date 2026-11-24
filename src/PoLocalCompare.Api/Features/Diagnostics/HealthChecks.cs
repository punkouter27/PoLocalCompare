using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using PoLocalCompare.Api.Platform;

namespace PoLocalCompare.Api.Features.Diagnostics;

/// <summary>Tag applied to every dependency probe, so /health can select them as a set.</summary>
public static class HealthCheckTags
{
    public const string Dependency = "dependency";

    /// <summary>Reported by /api/diag but left out of /health (and so out of the deploy gate).</summary>
    public const string DiagOnly = "diag-only";
}

/// <summary>
/// Probes Azure Table Storage with a <em>data-plane</em> query. This is deliberate: the obvious
/// <c>GetPropertiesAsync</c> is a management-plane call needing Storage Account Contributor,
/// whereas the app's managed identity only holds Storage Table Data Contributor — so a
/// properties-based probe reports Unhealthy on a perfectly working deployment.
/// </summary>
public sealed class TableStorageHealthCheck(TableServiceClient client) : IHealthCheck
{
    private readonly TableServiceClient _client = client;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            await foreach (var _ in _client.QueryAsync(maxPerPage: 1, cancellationToken: cts.Token))
                break;

            sw.Stop();
            return HealthCheckResult.Healthy(
                "Table Storage reachable.",
                new Dictionary<string, object> { ["latencyMs"] = sw.ElapsedMilliseconds });
        }
        catch (Exception ex)
        {
            sw.Stop();
            return HealthCheckResult.Unhealthy(
                "Table Storage unreachable.",
                ex,
                new Dictionary<string, object> { ["latencyMs"] = sw.ElapsedMilliseconds });
        }
    }
}

/// <summary>
/// Probes the Blob container that holds duel pages too large for a table row (data plane, like
/// the table probe: the app's identity holds Storage Blob Data Contributor, not account rights).
/// A container that does not exist yet still proves the account answered.
/// </summary>
public sealed class BlobStorageHealthCheck(BlobServiceClient client) : IHealthCheck
{
    private readonly BlobServiceClient _client = client;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            await _client.GetBlobContainerClient(DuelResultRepository.BlobContainerName).ExistsAsync(cts.Token);

            sw.Stop();
            return HealthCheckResult.Healthy(
                "Blob Storage reachable.",
                new Dictionary<string, object> { ["latencyMs"] = sw.ElapsedMilliseconds });
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new HealthCheckResult(
                context.Registration?.FailureStatus ?? HealthStatus.Unhealthy,
                "Blob Storage unreachable.",
                ex,
                new Dictionary<string, object> { ["latencyMs"] = sw.ElapsedMilliseconds });
        }
    }
}

/// <summary>
/// Probes a configured endpoint's reachability without spending an inference call. An unset
/// endpoint reports Healthy with a "NotConfigured" note rather than Unhealthy — the app runs
/// without Foundry (mock mode, Ollama-only), so absence is a configuration state, not a fault.
/// </summary>
/// <param name="developmentOnly">The service is only called in Development (Ollama on localhost).</param>
public sealed class ConfiguredEndpointHealthCheck(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    string configurationKey,
    string displayName,
    bool developmentOnly) : IHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IConfiguration _configuration = configuration;
    private readonly IHostEnvironment _environment = environment;
    private readonly string _configurationKey = configurationKey;
    private readonly string _displayName = displayName;
    private readonly bool _developmentOnly = developmentOnly;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var endpoint = _configuration[_configurationKey];
        var inUse = !string.IsNullOrWhiteSpace(endpoint)
            && (!_developmentOnly || _environment.IsDevelopment());

        return inUse
            ? HttpReachability.ProbeAsync(_httpClientFactory, endpoint!, _displayName, context, cancellationToken)
            : Task.FromResult(HttpReachability.NotConfigured(_displayName));
    }
}

/// <summary>
/// Application Insights: probes the ingestion endpoint named in the connection string (never
/// echoed — the string carries the instrumentation key). No connection string: logs stay local.
/// </summary>
public sealed class AppInsightsHealthCheck(IHttpClientFactory httpClientFactory, IConfiguration configuration) : IHealthCheck
{
    private const string DefaultIngestion = "https://dc.services.visualstudio.com/";

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var connectionString = PoPlatform.ResolveAppInsightsConnectionString(configuration);
        if (string.IsNullOrWhiteSpace(connectionString))
            return Task.FromResult(HttpReachability.NotConfigured("Application Insights"));

        var ingestion = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(kv => kv.Length == 2 && kv[0].Equals("IngestionEndpoint", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv[1])
            .FirstOrDefault() ?? DefaultIngestion;

        return HttpReachability.ProbeAsync(httpClientFactory, ingestion, "Application Insights", context, cancellationToken);
    }
}

/// <summary>Hugging Face Hub: the model catalog reads download and like counts from its public API.</summary>
public sealed class HuggingFaceHubHealthCheck(IHttpClientFactory httpClientFactory) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        HttpReachability.ProbeAsync(
            httpClientFactory, "https://huggingface.co/api/models?author=mlc-ai&limit=1", "Hugging Face Hub", context, cancellationToken);
}

/// <summary>One short GET: any HTTP answer proves DNS, TLS and routing to the service work.</summary>
internal static class HttpReachability
{
    public static HealthCheckResult NotConfigured(string displayName) => HealthCheckResult.Healthy(
        $"{displayName} is not configured.",
        new Dictionary<string, object> { ["state"] = "NotConfigured" });

    public static async Task<HealthCheckResult> ProbeAsync(
        IHttpClientFactory httpClientFactory,
        string endpoint,
        string displayName,
        HealthCheckContext context,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var http = httpClientFactory.CreateClient("OllamaStatus");
            // Any HTTP response proves reachability. A 401/404 from a bare endpoint probe is
            // expected — it still means DNS, TLS and routing all work, which is what is asked.
            using var response = await http.GetAsync(new Uri(endpoint), cts.Token);

            sw.Stop();
            return HealthCheckResult.Healthy(
                $"{displayName} reachable.",
                new Dictionary<string, object>
                {
                    ["latencyMs"] = sw.ElapsedMilliseconds,
                    ["statusCode"] = (int)response.StatusCode,
                });
        }
        catch (Exception ex)
        {
            sw.Stop();
            // The registration's failure status, so a dependency registered as Degraded (the
            // diag-only probes) reads that way rather than as a hard failure.
            return new HealthCheckResult(
                context.Registration?.FailureStatus ?? HealthStatus.Unhealthy,
                $"{displayName} unreachable.",
                ex,
                new Dictionary<string, object> { ["latencyMs"] = sw.ElapsedMilliseconds });
        }
    }
}
