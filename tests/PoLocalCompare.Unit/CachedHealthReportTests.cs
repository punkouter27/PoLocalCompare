using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoLocalCompare.Api.Features.Diagnostics;

namespace PoLocalCompare.Unit;

public class CachedHealthReportTests
{
    [Fact]
    public async Task Probes_RunOncePerTtl_SharedByConcurrentCallers()
    {
        var probes = new CountingHealthChecks();
        var clock = new ManualClock();
        var cache = new CachedHealthReport(probes, clock);

        // Ten callers at once (think /health and /diag together): one probe run, one report.
        var first = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => cache.GetAsync(CancellationToken.None)));
        Assert.Equal(1, probes.Runs);
        Assert.All(first, r => Assert.Same(first[0].Report, r.Report));
        Assert.Equal(clock.Now, first[0].CheckedAt);

        // Still inside the TTL: served from the cache.
        clock.Now += CachedHealthReport.Ttl - TimeSpan.FromSeconds(1);
        var cached = await cache.GetAsync(CancellationToken.None);
        Assert.Equal(1, probes.Runs);
        Assert.Same(first[0].Report, cached.Report);

        // Past the TTL: the probes run again and the new report carries the new time.
        clock.Now += TimeSpan.FromSeconds(2);
        var refreshed = await cache.GetAsync(CancellationToken.None);
        Assert.Equal(2, probes.Runs);
        Assert.Equal(clock.Now, refreshed.CheckedAt);
    }

    private sealed class CountingHealthChecks : HealthCheckService
    {
        private int _runs;
        public int Runs => _runs;

        public override async Task<HealthReport> CheckHealthAsync(
            Func<HealthCheckRegistration, bool>? predicate, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _runs);
            await Task.Delay(20, cancellationToken);
            return new HealthReport(new Dictionary<string, HealthReportEntry>(), TimeSpan.FromMilliseconds(20));
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
