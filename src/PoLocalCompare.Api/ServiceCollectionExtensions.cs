using Microsoft.Extensions.Caching.Hybrid;

namespace PoLocalCompare.Api;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<RegisterModelHandler>();
        services.AddScoped<DiscoverModelsHandler>();
        services.AddScoped<AddDiscoveredModelHandler>();
        services.AddScoped<CommenceDuelHandler>();
        services.AddScoped<SpendGuard>();
        services.AddSingleton<SpendLedger>();
        services.AddScoped<RefinePromptHandler>();
        services.AddScoped<GetDuelHandler>();
        services.AddScoped<GetLeaderboardHandler>();
        services.AddScoped<GetKillListHandler>();
        services.AddScoped<GetModelProfileHandler>();
        services.AddScoped<GetScoutingReportHandler>();
        services.AddScoped<ListDuelsHandler>();
        services.AddScoped<ExportLabReportHandler>();
        services.AddScoped<GetModelAvailabilityHandler>();
        services.AddScoped<ListOllamaModelsHandler>();
        services.AddScoped<BenchmarkOllamaModelHandler>();
        services.AddSingleton<DuelExecutionService>();
        services.AddSingleton<LocalResultSignal>();
        services.AddScoped<AutoJudge>();
        services.AddScoped<JudgeCalibrationHandler>();
        services.AddScoped<DuelRecoverySweeper>();
        services.AddHostedService<TournamentResumeService>();
        services.AddScoped<CreateTournamentHandler>();
        // Singleton: it owns a Chromium process, launched lazily on first use and reused.
        services.AddSingleton<HtmlScreenshotRenderer>();
        // Singleton like DuelExecutionService, and for the same reason: it is queued from a
        // request but outlives it, so it resolves its own scope per step rather than capturing
        // the request's. Registered once — it was previously registered twice, which is a
        // silent no-op in MS.DI but reads as intent.
        services.AddSingleton<TournamentRunner>();

        services.AddScoped<RecordVerdictHandler>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var kFactor = cfg.GetValue<double>("Elo:KFactor", 32.0);
            return new RecordVerdictHandler(
                sp.GetRequiredService<IDuelRepository>(),
                sp.GetRequiredService<IModelRepository>(),
                sp.GetRequiredService<IEloHistoryRepository>(),
                kFactor,
                sp.GetRequiredService<HybridCache>(),
                sp.GetRequiredService<IDuelResultRepository>());
        });

        // HybridCache fronts read-heavy, slow-changing reads (leaderboard, live model-availability probes).
        // Short TTLs keep staleness bounded; the leaderboard is also tag-invalidated when a verdict lands.
#pragma warning disable EXTEXP0018 // HybridCache is released but still surfaces an experimental attribute.
        services.AddHybridCache(options =>
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromSeconds(30),
                LocalCacheExpiration = TimeSpan.FromSeconds(30),
            });
#pragma warning restore EXTEXP0018

        return services;
    }
}
