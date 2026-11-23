using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PoLocalCompare.Api.Features.Diagnostics;

/// <summary>
/// One health report shared by <c>/health</c> and <c>/api/diag</c>, refreshed at most once per <see cref="Ttl"/>.
/// </summary>
/// <remarks>
/// Every probe is a real call (Table/Blob GetProperties, one GET per external service), so running them on each
/// request let anyone hammering /diag or /health multiply the outbound calls. Concurrent callers during a refresh
/// share the one run. The probes run with their own 5 s timeouts, not the caller's token, so an impatient caller
/// cannot abort the report everyone else is waiting on.
/// </remarks>
public sealed class CachedHealthReport(HealthCheckService healthChecks, TimeProvider time) : IDisposable
{
    /// <summary>How long one report is served before the probes run again.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedReport? _last;

    public async Task<CachedReport> GetAsync(CancellationToken cancellationToken)
    {
        if (Fresh(_last) is { } hit)
            return hit;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Fresh(_last) is { } raced)
                return raced;

            var report = await healthChecks.CheckHealthAsync(CancellationToken.None);
            _last = new CachedReport(report, time.GetUtcNow());
            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private CachedReport? Fresh(CachedReport? cached) =>
        cached is not null && time.GetUtcNow() - cached.CheckedAt < Ttl ? cached : null;
}

/// <summary>A health report and when its probes ran.</summary>
public sealed record CachedReport(HealthReport Report, DateTimeOffset CheckedAt);
