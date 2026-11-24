using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoLocalCompare.Shared.Diagnostics;

namespace PoLocalCompare.Api.Features.Diagnostics;

/// <summary>
/// <c>GET /api/diag</c> — the data source of the /diag "System status" page (Po web standard, AGENTS.md).
/// Anonymous on purpose: /diag is the page you open when sign-in is the broken thing.
/// </summary>
/// <remarks>
/// A projection of <see cref="HealthCheckService"/>, so it can never disagree with /health, with a fixed
/// allow-list of fields per check: a label, a purpose line, the status, latency and at most an exception
/// TYPE. Nothing read from configuration is echoed back — no endpoint, vault or storage URI (not even
/// masked), key, tenant or path. The integration tier asserts it.
/// </remarks>
public static class DiagEndpoints
{
    private sealed record Descriptor(string Group, string Label, string Purpose);

    // Keyed by the health-check registration names in Program.cs. A check missing from here is
    // still reported, under its registration name, so a new probe cannot silently vanish.
    private static readonly Dictionary<string, Descriptor> Descriptors = new(StringComparer.Ordinal)
    {
        ["azureTableStorage"] = new("data", "Table Storage", "Stores models, duels, ratings and tournaments."),
        ["azureBlobStorage"] = new("data", "Blob Storage", "Keeps duel pages too big for a table row."),
        ["azureAiFoundry"] = new("service", "Azure AI Foundry", "Runs the remote models and the AI judge."),
        ["keyVault"] = new("service", "Azure Key Vault", "Holds API keys and connection strings for the hosted app."),
        ["entraId"] = new("service", "Microsoft Entra ID", "Signs people in with a Microsoft account."),
        ["appInsights"] = new("service", "Application Insights", "Collects the app's logs and sign-in events."),
        ["huggingFace"] = new("service", "Hugging Face Hub", "Download and like counts on the model catalog."),
        ["ollama"] = new("service", "Ollama", "Runs local models on a developer's own machine."),
    };

    /// <summary>The build stamp (UTC yyMMdd) from the one BuildVersion assembly metadata key.</summary>
    public static string BuildVersion { get; } =
        typeof(DiagEndpoints).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildVersion")?.Value ?? "dev";

    public static IEndpointRouteBuilder MapDiagEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diag", async (CachedHealthReport healthChecks, IWebHostEnvironment env, CancellationToken ct) =>
                Results.Ok(await BuildAsync(healthChecks, env, ct)))
            .WithName("Diagnostics")
            .WithTags("Health")
            .WithSummary("System status for the /diag page — no secrets, endpoints or exception messages.")
            .Produces<DiagReportDto>()
            .AllowAnonymous();

        return app;
    }

    /// <remarks>
    /// The checks come from <see cref="CachedHealthReport"/> (60 s, shared with /health): <c>CheckedAt</c> and
    /// <c>CheckedMs</c> are when the probes last ran and how long they took, not this request.
    /// </remarks>
    public static async Task<DiagReportDto> BuildAsync(CachedHealthReport healthChecks, IWebHostEnvironment env, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var (report, checkedAt) = await healthChecks.GetAsync(ct);

        // The API row is this request, measured: the time the API took to answer it, checks included
        // (the /diag page replaces it with the round trip the browser saw).
        var apiMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
        var checks = new List<DiagCheckDto>
        {
            new("api", "api", "PoLocalCompare API", "Healthy",
                "Answers every page, duel and leaderboard request the app makes.", "Answered this request.", apiMs, null),
        };

        foreach (var (name, entry) in report.Entries)
        {
            var descriptor = Descriptors.GetValueOrDefault(name) ?? new Descriptor("service", name, "A dependency the app calls.");
            var notConfigured = entry.Data.TryGetValue("state", out var state) && state is "NotConfigured";
            var latency = entry.Data.TryGetValue("latencyMs", out var ms) && ms is long l
                ? l
                : Math.Round(entry.Duration.TotalMilliseconds, 1);

            // A service switched off here on purpose (no key, Development-only) is "Off": neutral, never
            // counted as Degraded or Unhealthy, so production can honestly read "Everything is working".
            checks.Add(new DiagCheckDto(
                name,
                descriptor.Group,
                descriptor.Label,
                notConfigured ? "Off" : entry.Status.ToString(),
                descriptor.Purpose,
                Detail(entry.Status, notConfigured),
                latency,
                notConfigured || entry.Status == HealthStatus.Healthy ? null : entry.Exception?.GetType().Name ?? "It did not answer as expected."));
        }

        var worst = checks.Any(c => c.Status == "Unhealthy") ? "Unhealthy"
            : checks.Any(c => c.Status == "Degraded") ? "Degraded"
            : "Healthy";

        return new DiagReportDto(
            worst,
            env.EnvironmentName,
            BuildVersion,
            checkedAt,
            Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            [.. checks]);
    }

    private static string Detail(HealthStatus status, bool notConfigured) => notConfigured
        ? "Not used in this environment."
        : status switch
        {
            HealthStatus.Healthy => "Working normally.",
            HealthStatus.Degraded => "Answering, but not fully.",
            _ => "Not answering.",
        };
}
