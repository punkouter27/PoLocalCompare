using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;

namespace PoLocalCompare.Api.Pages;

/// <summary>
/// /diag. This is a server-rendered Razor Page rather than a Blazor route on purpose: it has to
/// work when the WASM client is the thing that is broken — <c>index.html</c>'s boot-timeout
/// fallback links here for exactly that. It wears the app's own shell (the Po header, gear and
/// build stamp, from the same po-tokens.css) so it reads as a page of the app, not a server
/// dump. The live status cards are drawn by <c>diag-health.js</c> from <c>GET /api/diag</c>; the
/// page model supplies only the signed-in email, the stamp and a few harmless tuning values.
/// </summary>
/// <remarks>
/// Until 2026-11-17 it also rendered the Key Vault and Foundry endpoints (masked to their first
/// and last four characters) and raw exception messages. The diagnostics standard allows
/// neither, so no configured URI, key or message reaches this page any more.
/// </remarks>
public class DiagModel(IConfiguration configuration) : PageModel
{
    private readonly IConfiguration _configuration = configuration;

    /// <summary>YYMMDD from the BuildVersion assembly metadata Directory.Build.props writes.</summary>
    public static readonly string Stamp =
        typeof(DiagModel).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildVersion")?.Value
        ?? DateTimeOffset.UtcNow.ToString("yyMMdd", System.Globalization.CultureInfo.InvariantCulture);

    public IReadOnlyList<KeyValuePair<string, string>> ConfigItems { get; private set; } = [];

    /// <summary>The signed-in account as the app header shows it, or null when signed out.</summary>
    public string? Email { get; private set; }

    public void OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            Email = User.FindFirst("preferred_username")?.Value
                ?? User.FindFirst(ClaimTypes.Email)?.Value
                ?? User.Identity.Name;
        }

        ConfigItems =
        [
            new("Rating step (Elo:KFactor)", _configuration["Elo:KFactor"] ?? "not set"),
            new("Duel time limit, seconds (Duel:TimeLimitSeconds)", _configuration["Duel:TimeLimitSeconds"] ?? "not set"),
            new("Real AI calls (Features:UseRealAi)", _configuration["Features:UseRealAi"] ?? "not set"),
        ];
    }
}
