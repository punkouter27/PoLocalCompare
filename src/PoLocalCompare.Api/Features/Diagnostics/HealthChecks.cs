using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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
/// Probes a configured endpoint's reachability without spending an inference call. An unset
/// endpoint reports Healthy with a "NotConfigured" note rather than Unhealthy — the app runs
/// without Foundry (mock mode, Ollama-only), so absence is a configuration state, not a fault.
/// </summary>
public sealed class ConfiguredEndpointHealthCheck(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    string configurationKey,
    string displayName) : IHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IConfiguration _configuration = configuration;
    private readonly string _configurationKey = configurationKey;
    private readonly string _displayName = displayName;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var endpoint = _configuration[_configurationKey];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return HealthCheckResult.Healthy(
                $"{_displayName} is not configured.",
                new Dictionary<string, object> { ["state"] = "NotConfigured" });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var http = _httpClientFactory.CreateClient("OllamaStatus");
            // Any HTTP response proves reachability. A 401/404 from a bare endpoint probe is
            // expected — it still means DNS, TLS and routing all work, which is what is asked.
            using var response = await http.GetAsync(endpoint, cts.Token);

            sw.Stop();
            return HealthCheckResult.Healthy(
                $"{_displayName} reachable.",
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
            // diag-only sign-in probe) reads that way rather than as a hard failure.
            return new HealthCheckResult(
                context.Registration?.FailureStatus ?? HealthStatus.Unhealthy,
                $"{_displayName} unreachable.",
                ex,
                new Dictionary<string, object> { ["latencyMs"] = sw.ElapsedMilliseconds });
        }
    }
}
