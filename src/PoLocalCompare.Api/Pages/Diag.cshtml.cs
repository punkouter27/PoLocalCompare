using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;

namespace PoLocalCompare.Api.Pages;

/// <summary>
/// /diag. This is a server-rendered Razor Page rather than a Blazor route on purpose: it has to
/// work when the WASM client is the thing that is broken — <c>index.html</c>'s boot-timeout
/// fallback links here for exactly that. The live status (API, storage, services, version,
/// environment) is fetched by <c>diag-health.js</c> from <c>GET /api/diag</c>; the page model
/// renders only a few harmless tuning values.
/// </summary>
/// <remarks>
/// Until 2026-11-17 it also rendered the Key Vault and Foundry endpoints (masked to their first
/// and last four characters) and raw exception messages. The diagnostics standard allows
/// neither, so no configured URI, key or message reaches this page any more.
/// </remarks>
public class DiagModel(IConfiguration configuration) : PageModel
{
    private readonly IConfiguration _configuration = configuration;

    public IReadOnlyList<KeyValuePair<string, string>> ConfigItems { get; private set; } = [];

    public void OnGet()
    {
        ConfigItems =
        [
            new("Elo:KFactor", _configuration["Elo:KFactor"] ?? "(not set)"),
            new("Duel:TimeLimitSeconds", _configuration["Duel:TimeLimitSeconds"] ?? "(not set)"),
            new("Features:UseRealAi", _configuration["Features:UseRealAi"] ?? "(not set)"),
        ];
    }
}
