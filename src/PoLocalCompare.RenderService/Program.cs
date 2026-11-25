using System.Security.Cryptography;
using System.Text;
using PoLocalCompare.Rendering;

// The Dockerfile runs "dotnet PoLocalCompare.RenderService.dll install-browsers" once at build
// time, so the image carries the Chromium build that matches this Playwright package exactly.
if (args is ["install-browsers"])
    return Microsoft.Playwright.Program.Main(["install", "--with-deps", "--only-shell", "chromium"]);

var builder = WebApplication.CreateBuilder(args);

// The one secret: the Api sends it as X-Render-Key. Without one the service refuses to start —
// an open endpoint that runs arbitrary HTML in a browser is not a state worth booting into.
var apiKey = builder.Configuration["Render:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 16)
    throw new InvalidOperationException("Render:ApiKey (env Render__ApiKey) must be set to at least 16 characters.");
var expectedKey = Encoding.UTF8.GetBytes(apiKey);

builder.Services.AddSingleton<PlaywrightHtmlRenderer>();
builder.Services.AddHostedService<BrowserWarmUp>();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 4 * RenderRequest.MaxHtmlChars);

var app = builder.Build();

// Each render holds a browser context (~50-100 MB) for 1-3 s. Both sides of a duel arrive
// together, so 4 leaves room for two duels at once inside the container's memory.
var renderSlots = new SemaphoreSlim(4, 4);

// Anonymous and cheap: the Api calls it when a duel starts, so a container scaled to zero is up
// (and its browser launched) by the time the judge needs it. Container Apps probes it too.
app.MapGet("/health", async (PlaywrightHtmlRenderer renderer, CancellationToken ct) =>
    await renderer.WarmUpAsync(ct) ? Results.Ok("ok") : Results.StatusCode(503));

app.MapPost("/render", async (HttpRequest http, RenderRequest request, PlaywrightHtmlRenderer renderer, CancellationToken ct) =>
{
    var presented = Encoding.UTF8.GetBytes(http.Headers["X-Render-Key"].ToString());
    if (!CryptographicOperations.FixedTimeEquals(presented, expectedKey))
        return Results.Unauthorized();

    if (request.Problem() is { } problem)
        return Results.BadRequest(problem);

    await renderSlots.WaitAsync(ct);
    try
    {
        // Null is "could not render this page" (it hung, the browser died): the Api falls back to
        // judging the source, exactly as it does when it renders locally and that fails.
        return await renderer.RenderAsync(request, ct) is { } evidence
            ? Results.Ok(evidence)
            : Results.StatusCode(422);
    }
    finally
    {
        renderSlots.Release();
    }
});

app.Run();
return 0;

/// <summary>Launches Chromium at startup, so a cold container's first render does not also pay for it.</summary>
internal sealed class BrowserWarmUp(PlaywrightHtmlRenderer renderer) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => renderer.WarmUpAsync(stoppingToken);
}
