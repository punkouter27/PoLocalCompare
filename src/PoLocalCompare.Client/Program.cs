using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.Authorization;
using PoLocalCompare.Client;
using PoLocalCompare.Client.Services;
using System;

// WebAssembly only: lets the [JSImport] bindings (PreferencesService, Sparkline) be called
// without a platform guard at every call site.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("browser")]

try
{
    var builder = WebAssemblyHostBuilder.CreateDefault(args);
    builder.RootComponents.Add<App>("#app");
    builder.RootComponents.Add<HeadOutlet>("head::after");

    builder.Services.AddScoped(sp => new HttpClient(new CorrelationHandler { InnerHandler = new HttpClientHandler() })
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
    });

    // ─── BFF auth: server owns the session; client only reads /auth/me. No tokens in WASM. ──
    builder.Services.AddAuthorizationCore();
    builder.Services.AddScoped<BffAuthenticationStateProvider>();
    builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
        sp.GetRequiredService<BffAuthenticationStateProvider>());

    // ─── DuelApiClient (T098) ────────────────────────────────────────────────────
    builder.Services.AddScoped<DuelApiClient>();

    // ─── Phase 3 client services ─────────────────────────────────────────────────
    builder.Services.AddScoped<AudioService>();
    builder.Services.AddScoped<ThemeService>();
    builder.Services.AddScoped<FxService>();
    builder.Services.AddScoped<PreferencesService>();
    builder.Services.AddScoped<WebLlmService>();
    builder.Services.AddTransient<SignalRDuelClient>();

    // Single-flight WebGPU capability probe, so
    // only one adapter/device request is made even when both mount on the same page.
    builder.Services.AddScoped<WebGpuCapability>();
    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CLIENT_STARTUP_FATAL: {ex.GetType().FullName}: {ex.Message}");
}
