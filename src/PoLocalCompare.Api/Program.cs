using System.Threading.RateLimiting;
using Azure.Data.Tables;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Polly;
using PoLocalCompare.Api;
using PoLocalCompare.Api.Auth;
using Scalar.AspNetCore;
using PoLocalCompare.Api.Platform;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.ApplicationInsights.TelemetryConverters;

// ─── Bootstrap logger (before DI) ───────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ─── Key Vault (T037) — must be FIRST before Serilog config ────────────
    // Capture what the local sources already provided. AddAzureKeyVault appends its source at
    // the END of the provider chain, giving it precedence over environment variables — so
    // without this capture, a PoLocalCompare--ConnectionStrings--* secret in the vault would
    // silently repoint a local dev run at production storage and beat any env-var override.
    var preKvTableConnection = builder.Configuration["ConnectionStrings:AzureTableStorage"];
    var preKvBlobConnection = builder.Configuration["ConnectionStrings:AzureBlobStorage"];

    var keyVaultUri = builder.Configuration["KeyVault:Uri"];
    if (!string.IsNullOrEmpty(keyVaultUri))
    {
        try
        {
            var credential = new DefaultAzureCredential();
            builder.Configuration.AddAzureKeyVault(
                new Uri(keyVaultUri),
                credential,
                new PrefixKeyVaultSecretManager("PoLocalCompare"));
        }
        catch (Exception ex) when (builder.Environment.IsDevelopment()
            && ex is CredentialUnavailableException or AuthenticationFailedException or Azure.RequestFailedException)
        {
            Log.Warning("Key Vault ({KeyVaultUri}) is unavailable in Development ({Reason}); continuing with local configuration.",
                keyVaultUri, ex.GetType().Name);
        }
    }

    // ─── Serilog (T018) — now has KV-provided connection strings ────────────
    builder.Host.UseSerilog((ctx, services, cfg) =>
    {
        cfg
            .ReadFrom.Configuration(ctx.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty(PoPlatform.ApplicationProperty, PoPlatform.AppName)
            .Enrich.WithProperty("Environment", ctx.HostingEnvironment.EnvironmentName)
            .WriteTo.Console();

        // File sinks are Development-only: on the Free (F1) App Service they consume the
        // limited app filesystem and don't survive restarts/redeploys. In Production the
        // console sink is what App Service's own log stream picks up.
        if (ctx.HostingEnvironment.IsDevelopment())
        {
            cfg
                .WriteTo.File(
                    path: "logs/polocalcompare-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7)
                .WriteTo.File(
                    path: "logs/polocalcompare-errors-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    restrictedToMinimumLevel: LogEventLevel.Error);
        }

        // The single App Insights exporter for this app. OpenTelemetry was removed on 2026-08-23
        // because TWO exporters were shipping to one App Insights resource on the F1 plan; this
        // sink is deliberately the only one, so that constraint still holds. It exists so the
        // UserSignedIn record (see Auth/SignInTelemetry.cs) lands where the cross-app sign-in
        // query can read it — without it the event would never leave the App Service log stream.
        // Absent connection string (local dev, tests) means the sink is simply not added.
        var appInsightsConnectionString = PoPlatform.ResolveAppInsightsConnectionString(ctx.Configuration);
        cfg.WriteTo.Conditional(
            _ => !string.IsNullOrWhiteSpace(appInsightsConnectionString),
            sink => sink.ApplicationInsights(appInsightsConnectionString!, TelemetryConverter.Traces));
    });

    // In Development, Key Vault holds production storage connection strings, and its config
    // source out-ranks everything the machine provided. Restore whatever the local sources
    // said (env var, appsettings.Development.json) — defaulting to Azurite when they said
    // nothing — so a local run can never write to the real storage account. Production is
    // untouched: there the vault-provided strings are the point.
    if (builder.Environment.IsDevelopment())
    {
        if (builder.Configuration["ConnectionStrings:AzureTableStorage"] != preKvTableConnection)
            builder.Configuration["ConnectionStrings:AzureTableStorage"] = preKvTableConnection ?? "UseDevelopmentStorage=true";

        if (builder.Configuration["ConnectionStrings:AzureBlobStorage"] != preKvBlobConnection)
            builder.Configuration["ConnectionStrings:AzureBlobStorage"] = preKvBlobConnection ?? "UseDevelopmentStorage=true";
    }

    if (builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(builder.Configuration["AzureAiFoundry:ApiKey"]))
    {
        Log.Warning("AzureAiFoundry:ApiKey is empty. Configure AzureAiFoundry__ApiKey in appsettings.Development.json or an environment variable for remote model duels.");
    }

    // No CORS: the Blazor WASM client is hosted from this same origin (single-origin
    // topology — the client assets are served from wwwroot by this API), so cross-origin
    // sharing is unnecessary and intentionally omitted.

    // ─── BFF authentication (cookie session + Microsoft OIDC + dev fake) ────────
    builder.AddBffAuthentication();

    // ─── SignalR ──────────────────────────────────────────────────────────────
    builder.Services.AddSignalR();

    // ─── OpenAPI (T020) ───────────────────────────────────────────────────────
    // Standards mandate OpenAPI 3.1; pinned explicitly rather than relying on the
    // SDK default so an SDK change can't silently downgrade the document version.
    builder.Services.AddOpenApi(options =>
        options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1);

    // ─── Razor pages (for /diag) ─────────────────────────────────────────────
    builder.Services.AddRazorPages();

    // ─── Health checks (standards §3) ────────────────────────────────────────
    // /health composes these; /diag renders the same report as the human-readable view,
    // so the two can never disagree about what is up.
    builder.Services.AddHealthChecks()
        .AddCheck<TableStorageHealthCheck>(
            "azureTableStorage",
            tags: [HealthCheckTags.Dependency])
        .AddTypeActivatedCheck<ConfiguredEndpointHealthCheck>(
            "azureAiFoundry",
            HealthStatus.Unhealthy,
            [HealthCheckTags.Dependency],
            "AzureAiFoundry:Endpoint", "Azure AI Foundry")
        .AddTypeActivatedCheck<ConfiguredEndpointHealthCheck>(
            "keyVault",
            HealthStatus.Unhealthy,
            [HealthCheckTags.Dependency],
            "KeyVault:Uri", "Key Vault");

    // RFC 7807 bodies for unhandled exceptions (UseExceptionHandler below), stamped with the
    // request id so a user-reported error can be found in the log.
    builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = c =>
        c.ProblemDetails.Extensions["correlationId"] = c.HttpContext.TraceIdentifier);

    // ─── Rate limit on starting paid inference ───────────────────────────────
    // One fixed window per signed-in user (per IP when the anonymous-writes gate is open),
    // applied only to POST /api/duels and POST /api/tournaments. 0 switches it off, which is
    // what the test hosts do — a suite starts far more duels a minute than a person can.
    var duelStartsPerMinute = builder.Configuration.GetValue("Limits:DuelStartsPerMinute", 10);
    builder.Services.AddRateLimiter(limiter =>
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        limiter.OnRejected = async (rejected, ct) =>
        {
            rejected.HttpContext.Response.Headers.RetryAfter = "60";
            await rejected.HttpContext.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Title = "Too many duels",
                Detail = $"You can start {duelStartsPerMinute} duels a minute. Give it a moment and try again.",
                Status = StatusCodes.Status429TooManyRequests,
            }, ct);
        };
        limiter.AddPolicy(DuelsEndpoints.StartRateLimitPolicy, http =>
        {
            if (duelStartsPerMinute <= 0)
                return RateLimitPartition.GetNoLimiter(string.Empty);

            var actor = IdentityResolver.ResolveActor(http.User);
            var key = actor == IdentityResolver.AnonymousActor
                ? http.Connection.RemoteIpAddress?.ToString() ?? actor
                : actor;
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = duelStartsPerMinute,
                Window = TimeSpan.FromMinutes(1),
            });
        });
    });

    // ─── JSON serialization — string enum values for API consumers ───────────
    builder.Services.ConfigureHttpJsonOptions(opts =>
    {
        opts.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

    // ─── Infrastructure (Phase 2 — T032–T037) ────────────────────────────────
    // OllamaStatus issues short, idempotent status pings — safe to wrap in a native
    // .NET resilience pipeline (retry + per-attempt timeout). The streaming inference
    // clients (typed Foundry/Ollama proxies in AddInfrastructure) get retry-only pipelines
    // because a per-attempt timeout would abort long SSE responses.
    builder.Services.AddHttpClient("OllamaStatus")
        .AddResilienceHandler("ollama-status", pipeline =>
        {
            pipeline.AddRetry(new Polly.Retry.RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = Polly.DelayBackoffType.Exponential,
                ShouldHandle = new Polly.PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .HandleResult(r => (int)r.StatusCode >= 500),
            });
            pipeline.AddTimeout(TimeSpan.FromSeconds(5));
        });
    // The Hub's public model API, for the catalog page's download/like counts. One short
    // idempotent GET, so it gets a per-attempt timeout like OllamaStatus; discovery degrades
    // to "no popularity" when it fails, so a slow Hub must not hold the page.
    builder.Services.AddHttpClient(DiscoverModelsHandler.HubClient, client =>
        {
            client.BaseAddress = new Uri("https://huggingface.co");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PoLocalCompare/1.0");
        })
        .AddResilienceHandler("huggingface-hub", pipeline =>
        {
            pipeline.AddRetry(new Polly.Retry.RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.FromMilliseconds(300),
                ShouldHandle = new Polly.PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .HandleResult(r => (int)r.StatusCode >= 500),
            });
            pipeline.AddTimeout(TimeSpan.FromSeconds(8));
        });
    builder.Services.AddInfrastructure(builder.Configuration);

    // ─── Application use cases (Phase 3 + 4 + 6) ────────────────────────────
    builder.Services.AddApplicationServices();

    // ─── Background task queue for reliable duel execution ──────────────────
    // Nothing resolves the concrete BackgroundTaskQueue, so the interface registration and the
    // hand-written hosted-service factory that used to sit here are both plain DI now.
    builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
    builder.Services.AddHostedService<BackgroundTaskService>();

    // ─── Build ───────────────────────────────────────────────────────────────
    var app = builder.Build();

    // Every table, every environment, before the first request — fail-fast (standards §5.6).
    await StorageTables.EnsureAllAsync(app.Services.GetRequiredService<TableServiceClient>());
    if (app.Environment.IsDevelopment() && !app.Configuration.GetValue<bool>("Testing:SkipSeeding"))
        await ModelSeeder.SeedAsync(app.Services);

    // ─── Startup recovery: settle duels the previous process left behind ──────
    // Runs in every environment — a mid-duel process death is the production case (App Service
    // deployments), and leaving those rows Pending forever is what stranded them. The sweeper
    // re-judges duels whose results are complete and voids the rest; per-duel failures are
    // swallowed inside, so one bad row cannot stop startup.
    using (var sweepScope = app.Services.CreateScope())
    {
        try
        {
            var sweeper = sweepScope.ServiceProvider.GetRequiredService<DuelRecoverySweeper>();
            await sweeper.SweepAsync(app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Duel recovery sweep failed at startup; affected duels stay judgeable by hand.");
        }
    }

    // ─── Middleware pipeline ─────────────────────────────────────────────────
    app.UseSerilogRequestLogging(opts =>
    {
        opts.GetLevel = (httpCtx, elapsed, ex) =>
        {
            if (ex is not null || httpCtx.Response.StatusCode >= 500)
                return LogEventLevel.Error;

            if (httpCtx.Response.StatusCode >= 400)
                return LogEventLevel.Warning;

            var path = httpCtx.Request.Path;
            if (path.StartsWithSegments("/_framework")
                || path.StartsWithSegments("/css")
                || path.StartsWithSegments("/js")
                || path.Value?.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase) == true)
            {
                return LogEventLevel.Debug;
            }

            return LogEventLevel.Information;
        };

        opts.EnrichDiagnosticContext = (diagCtx, httpCtx) =>
        {
            // Client-stamped correlation headers (standards §6.9); server ids are the fallback.
            diagCtx.Set("CorrelationId", httpCtx.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? httpCtx.TraceIdentifier);
            diagCtx.Set("UserId", httpCtx.User.Identity?.Name ?? "anonymous");
            diagCtx.Set("RequestPath", httpCtx.Request.Path.Value ?? string.Empty);
            diagCtx.Set("RequestMethod", httpCtx.Request.Method);
            var sessionId = httpCtx.Request.Headers["X-Session-ID"].FirstOrDefault()
                ?? httpCtx.Request.Cookies["X-Session-Id"]
                ?? httpCtx.TraceIdentifier;
            diagCtx.Set("SessionId", sessionId);
        };
    });

    // ─── Global exception handler — RFC 7807 problem+json via AddProblemDetails ──
    // A request the framework could not bind (`?verdict=Won`, an empty `?before=`) is the
    // caller's fault, so it keeps its 400 instead of falling through to a 500.
    app.UseExceptionHandler(new ExceptionHandlerOptions
    {
        StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError,
    });

    // ─── Content-Security-Policy: frame-ancestors 'self' (T089) ─────────────
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'self'";
        await next(ctx);
    });

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference("/scalar").AllowAnonymous();
    }

    // Force browsers to revalidate WASM framework assets on every load (UX #9)
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/_framework"))
            ctx.Response.Headers.CacheControl = "no-cache, must-revalidate";
        await next();
    });

    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();

    app.UseRouting();

    app.UseAuthentication();
    app.UseAuthorization();
    // After authentication: the policy partitions on the signed-in user.
    app.UseRateLimiter();

    // ─── BFF auth routes (/auth/login/microsoft, /auth/login/fake, /auth/logout, /auth/me) ──
    app.MapAuthEndpoints();

    app.MapRazorPages().AllowAnonymous();

    // ─── Health endpoint (T038) ──────────────────────────────────────────────
    app.MapHealthEndpoints();
    // Uniform cross-app liveness probe (see PoPlatform). Same shape in every Po app, which
    // is what lets the portfolio dashboard poll them all and render one uptime grid.
    app.MapPoLiveness();

    // ─── SignalR hubs (auth required) ──────────────────────────────────────────
    app.MapHub<DuelHub>("/hubs/duel").RequireAuthorization();

    // ─── API endpoints ────────────────────────────────────────────────────────
    app.MapModelsEndpoints();
    // Features:AllowAnonymousWrites opens the write endpoints (POST /api/duels,
    // POST /api/duels/{id}/verdict, POST /api/duels/{id}/local-result) for un-authenticated
    // callers — see DuelsEndpoints.cs. local-result was unconditionally anonymous until
    // 2026-09-02; the Blazor client posts it from the app origin with the session cookie
    // attached, so nothing needed the exemption and it now honours the flag like the rest.
    // Reads stay authenticated so the leaderboard and archive can't be scraped anonymously.
    // Production keeps the flag at its false default.
    var allowAnonymousWrites = builder.Configuration.GetValue("Features:AllowAnonymousWrites", false);
    app.MapDuelsEndpoints(allowAnonymousWrites: allowAnonymousWrites);
    app.MapArchiveEndpoints();
    app.MapLeaderboardEndpoints();
    app.MapOllamaEndpoints();
    app.MapPromptsEndpoints();
    app.MapTournamentsEndpoints(allowAnonymousWrites: allowAnonymousWrites);

    if (app.Environment.IsDevelopment())
        app.MapDevEndpoints();

    // ─── Blazor WASM static assets + fallback (T014) ─────────────────────────
    app.MapStaticAssets().AllowAnonymous();
    app.MapGet("/favicon.ico", () => Results.Redirect("/favicon.png", permanent: true)).AllowAnonymous();
    app.MapFallbackToFile("index.html").AllowAnonymous();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "PoLocalCompare.Api failed to start");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { }
