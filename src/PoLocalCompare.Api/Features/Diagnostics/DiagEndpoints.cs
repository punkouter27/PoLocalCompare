using System.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Diagnostics;

/// <summary>
/// <c>GET /api/diag</c> — the data source of the /diag page. Anonymous on purpose: /diag is the
/// page you open when sign-in or the client is the broken thing.
/// </summary>
/// <remarks>
/// A projection of <see cref="HealthCheckService"/>, so it can never disagree with /health, but
/// with a fixed allow-list of fields per check: a name, a purpose line, the status, latency and
/// at most an exception TYPE. Nothing read from configuration is echoed back — no endpoint,
/// vault or storage URI (not even masked), key, tenant or path. The integration tier asserts it.
/// </remarks>
public static class DiagEndpoints
{
    private sealed record Descriptor(string Name, string Purpose, bool IsStore);

    // Keyed by the health-check registration names in Program.cs. A check missing from here is
    // still reported, under its registration name, so a new probe cannot silently vanish.
    private static readonly Dictionary<string, Descriptor> Descriptors = new(StringComparer.Ordinal)
    {
        ["azureTableStorage"] = new("Table Storage", "Stores models, duels, ratings and tournaments.", IsStore: true),
        ["azureAiFoundry"] = new("Azure AI Foundry", "Runs the remote models and the AI judge.", IsStore: false),
        ["keyVault"] = new("Azure Key Vault", "Holds API keys and connection strings for the hosted app.", IsStore: false),
        ["entraId"] = new("Microsoft Entra ID", "Signs people in with a Microsoft account.", IsStore: false),
    };

    private static readonly string Version =
        typeof(DiagEndpoints).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildVersion")?.Value ?? "unknown";

    public static IEndpointRouteBuilder MapDiagEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diag", async (HealthCheckService healthChecks, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var report = await healthChecks.CheckHealthAsync(ct);
            var checkedAt = DateTimeOffset.UtcNow;

            var rows = report.Entries
                .Select(e =>
                {
                    var descriptor = Descriptors.GetValueOrDefault(e.Key) ?? new Descriptor(e.Key, string.Empty, IsStore: false);
                    var configured = !(e.Value.Data.TryGetValue("state", out var state) && state is "NotConfigured");
                    var check = new DiagCheckDto(
                        descriptor.Name,
                        descriptor.Purpose,
                        e.Value.Status.ToString(),
                        configured,
                        e.Value.Data.TryGetValue("latencyMs", out var ms) && ms is long l ? l : null,
                        checkedAt,
                        e.Value.Status == HealthStatus.Healthy ? null : e.Value.Exception?.GetType().Name ?? "Unavailable");
                    return (descriptor.IsStore, check);
                })
                .ToList();

            return Results.Ok(new DiagReportDto(
                report.Status.ToString(),
                env.EnvironmentName,
                Version,
                checkedAt,
                [.. rows.Where(r => r.IsStore).Select(r => r.check)],
                [.. rows.Where(r => !r.IsStore).Select(r => r.check)]));
        })
        .WithName("Diagnostics")
        .WithTags("Health")
        .WithSummary("Dependency status for the /diag page — no secrets, endpoints or exception messages.")
        .Produces<DiagReportDto>()
        .AllowAnonymous();

        return app;
    }
}
