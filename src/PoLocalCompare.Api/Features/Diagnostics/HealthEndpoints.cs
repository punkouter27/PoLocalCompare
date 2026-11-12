using Azure.Data.Tables;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Diagnostics;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Backed by the standard health-check pipeline (services.AddHealthChecks). The JSON
        // shape is preserved from the hand-rolled version this replaced, so existing probes
        // and the /diag page keep working.
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthResponseAsync,
        })
        .WithName("Health")
        .WithTags("Health")
        .WithSummary("Health check for all dependencies")
        .AllowAnonymous();

        app.MapGet("/api/diag/smoke", async (
            TableServiceClient tableServiceClient,
            IModelRepository modelRepository,
            IWebHostEnvironment env,
            HttpContext context) =>
        {
            var checks = new Dictionary<string, object>();
            var overallHealthy = true;

            try
            {
                await tableServiceClient.GetPropertiesAsync();
                checks["azureTableStorage"] = new { status = "Healthy" };
            }
            catch (Exception ex)
            {
                checks["azureTableStorage"] = new { status = "Unhealthy", error = ex.Message };
                overallHealthy = false;
            }

            var allModels = (await modelRepository.GetAllAsync()).ToList();
            var localServiceCount = allModels.Count(m => m.ModelType == ModelType.LocalService);

            var result = new
            {
                status = overallHealthy ? "Healthy" : "Unhealthy",
                environment = env.EnvironmentName,
                models = new
                {
                    total = allModels.Count,
                    localService = localServiceCount,
                    cloudMode = !env.IsDevelopment() && !env.IsEnvironment("Test") && localServiceCount == 0
                },
                checks
            };

            context.Response.StatusCode = overallHealthy ? 200 : 503;
            return Results.Json(result);
        })
        .WithName("DiagnosticsSmoke")
        .WithTags("Health")
        .WithSummary("Quick smoke check for runtime dependencies and model source behavior")
        .AllowAnonymous();

        return app;
    }

    /// <summary>
    /// Renders <see cref="HealthReport"/> in the shape the hand-rolled /health used to emit:
    /// <c>{ status, checks: { name: { status, latencyMs?, error? } } }</c>. Kept byte-compatible
    /// on purpose — App Service probes and any saved dashboards parse it.
    /// </summary>
    private static Task WriteHealthResponseAsync(HttpContext context, HealthReport report)
    {
        var checks = new Dictionary<string, object>();

        foreach (var (name, entry) in report.Entries)
        {
            var detail = new Dictionary<string, object>
            {
                // A check that reported "NotConfigured" in its data is Healthy to the pipeline
                // but should still read as NotConfigured to a human looking at the payload.
                ["status"] = entry.Data.TryGetValue("state", out var state)
                    ? state.ToString() ?? entry.Status.ToString()
                    : entry.Status.ToString(),
            };

            if (entry.Data.TryGetValue("latencyMs", out var latency))
                detail["latencyMs"] = latency;

            if (entry.Status == HealthStatus.Unhealthy && entry.Exception is not null)
                detail["error"] = entry.Exception.Message;

            checks[name] = detail;
        }

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = report.Status == HealthStatus.Healthy
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;

        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks,
        });
    }
}
