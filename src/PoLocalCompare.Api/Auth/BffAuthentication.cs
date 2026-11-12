using PoLocalCompare.Api.Platform;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace PoLocalCompare.Api.Auth;

/// <summary>
/// Backend-for-Frontend (BFF) authentication. The Blazor WASM client never sees a token:
/// the server runs the OIDC code flow, stores the session in an encrypted HttpOnly cookie,
/// and the SPA only ever calls <c>/auth/*</c> and reads <c>/auth/me</c>.
/// </summary>
public static class BffAuthentication
{
    public const string MicrosoftScheme = "Microsoft";
    public const string FakeScheme = "Fake";
    // static readonly, not const: the name is computed from the shared convention. The cookie
    // is issued Secure unconditionally below, which is what makes the __Host- prefix legal.
    public static readonly string SessionCookieName = PoPlatform.SessionCookieName(secure: true);

    // Matches a v2.0 Microsoft Entra organizational issuer: https://login.microsoftonline.com/{tenantId}/v2.0
    private static readonly Regex EntraIssuerPattern =
        new(@"^https://login\.microsoftonline\.com/[0-9a-fA-F-]{36}/v2\.0$", RegexOptions.Compiled);

    public static WebApplicationBuilder AddBffAuthentication(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var config = builder.Configuration;
        var isProduction = builder.Environment.IsProduction();

        var authBuilder = services.AddAuthentication(options =>
        {
            // Cookie is the session of record. Unauthenticated API/SPA requests get a 401
            // (see cookie events) — never a 302 to an HTML login page. Interactive Microsoft
            // sign-in is invoked explicitly from /auth/login/microsoft.
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        });

        authBuilder.AddCookie(options =>
        {
            options.Cookie.Name = SessionCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        // ─── Microsoft OIDC (only when configured) ──────────────────────────────
        var clientId = config["AzureAd:ClientId"];
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            authBuilder.AddOpenIdConnect(MicrosoftScheme, options =>
            {
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.Authority = config["AzureAd:Authority"]
                    ?? "https://login.microsoftonline.com/common/v2.0";
                options.ClientId = clientId;
                var clientSecret = config["AzureAd:ClientSecret"];
                if (!string.IsNullOrWhiteSpace(clientSecret))
                    options.ClientSecret = clientSecret;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = true;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.MapInboundClaims = false;
                options.CallbackPath = "/signin-oidc";
                options.SignedOutCallbackPath = "/signout-callback-oidc";
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");

                // Multi-tenant OIDC: the /organizations authority issues tenant-specific tokens,
                // so issuer validation is shape-based (any well-formed Entra issuer) unless an
                // explicit tenant allow-list is configured.
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.NameClaimType = "preferred_username";
                var allowedTenants = config.GetSection("AzureAd:AllowedTenants").Get<string[]>() ?? [];
                if (allowedTenants.Length > 0)
                {
                    // Restrict sign-in to an explicit tenant allow-list.
                    options.TokenValidationParameters.IssuerValidator = (issuer, _, _) =>
                        allowedTenants.Any(t => issuer.Contains(t, StringComparison.OrdinalIgnoreCase))
                            ? issuer
                            : throw new SecurityTokenInvalidIssuerException(
                                $"Issuer '{issuer}' is not in the allowed tenant list.");
                }
                else
                {
                    // Accept any Microsoft account issuer: work/school Entra tenants AND personal
                    // Microsoft accounts (the MSA tenant 9188040d-... is itself a valid GUID), all of
                    // the form https://login.microsoftonline.com/{tenantId}/v2.0
                    options.TokenValidationParameters.IssuerValidator = (issuer, _, _) =>
                        EntraIssuerPattern.IsMatch(issuer)
                            ? issuer
                            : throw new SecurityTokenInvalidIssuerException(
                                $"Issuer '{issuer}' is not a recognized Microsoft account issuer.");
                }
                // Canonical UserSignedIn record. OnTokenValidated fires exactly once per interactive
                // sign-in — after validation, before the session cookie is issued — so it needs no
                // dedupe, and a rejected token never reaches it.
                options.Events.OnTokenValidated = ctx =>
                {
                    SignInTelemetry.TrackFrom(ctx.HttpContext, ctx.Principal, "PoLocalCompare");
                    return Task.CompletedTask;
                };
            });
        }

        // ─── Fake header auth (NON-PRODUCTION ONLY) ─────────────────────────────
        // Lets integration / E2E-API tests authenticate by sending X-Fake-User.
        if (!isProduction)
        {
            authBuilder.AddScheme<AuthenticationSchemeOptions, FakeAuthHandler>(FakeScheme, _ => { });
        }

        services.AddAuthorization(options =>
        {
            // Accept the cookie session, plus (outside Production) the fake test scheme.
            var schemes = isProduction
                ? new[] { CookieAuthenticationDefaults.AuthenticationScheme }
                : [CookieAuthenticationDefaults.AuthenticationScheme, FakeScheme];
            options.DefaultPolicy = new AuthorizationPolicyBuilder(schemes)
                .RequireAuthenticatedUser()
                .Build();
            // Deny-by-default (standards §4.5): every endpoint requires auth unless it
            // explicitly opts out with AllowAnonymous.
            options.FallbackPolicy = options.DefaultPolicy;
        });

        return builder;
    }

    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/auth").WithTags("Auth");
        var oidcConfigured = !string.IsNullOrWhiteSpace(app.Configuration["AzureAd:ClientId"]);
        var fakeEnabled = !app.Environment.IsProduction();

        // GET /auth/me — server auth state for the SPA. Never 401s.
        auth.MapGet("/me", async (HttpContext ctx) =>
        {
            var user = ctx.User;
            // AllowAnonymous means only the default (cookie) scheme ran. Consult the dev/test
            // fake scheme too so header-authenticated test clients see their identity here.
            if (user.Identity?.IsAuthenticated != true && fakeEnabled)
            {
                var fake = await ctx.AuthenticateAsync(FakeScheme);
                if (fake.Succeeded)
                    user = fake.Principal!;
            }
            if (user.Identity?.IsAuthenticated != true)
                return Results.Ok(new AuthStateDto(false, null, []));

            var name = user.FindFirst("preferred_username")?.Value
                ?? user.FindFirst(ClaimTypes.Email)?.Value
                ?? user.FindFirst("email")?.Value
                ?? user.Identity.Name;
            var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
            return Results.Ok(new AuthStateDto(true, name, roles));
        }).AllowAnonymous();

        // GET /auth/login — bare login entry point. Redirects to the configured provider
        // (fake in dev, Microsoft otherwise) so stale bookmarks like /auth/login?fakeReturnUrl=/ 
        // don't hit the SPA fallback and render a 404.
        auth.MapGet("/login", (HttpContext ctx, string? returnUrl, string? fakeReturnUrl) =>
        {
            var target = fakeEnabled ? "/login/fake" : "/login/microsoft";
            var safeReturn = SanitizeReturnUrl(returnUrl) ?? SanitizeReturnUrl(fakeReturnUrl);
            var query = !string.IsNullOrEmpty(safeReturn) && safeReturn != "/"
                ? $"?returnUrl={Uri.EscapeDataString(safeReturn)}"
                : string.Empty;
            return Results.Redirect(target + query);
        }).AllowAnonymous();

        // GET /auth/login/microsoft — start the server-side OIDC code flow.
        auth.MapGet("/login/microsoft", (string? returnUrl) =>
        {
            if (!oidcConfigured)
                return Results.BadRequest(new { error = "Microsoft sign-in is not configured." });
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = SanitizeReturnUrl(returnUrl) },
                [MicrosoftScheme]);
        }).AllowAnonymous();

        // GET /auth/login/fake — dev/test guest bypass; issues a guest session cookie.
        if (!app.Environment.IsProduction())
        {
            auth.MapGet("/login/fake", async (string? user, string? roles, string? returnUrl, HttpContext ctx) =>
            {
                var name = string.IsNullOrWhiteSpace(user)
                    ? $"GUEST{Random.Shared.Next(10_000_000, 99_999_999)}"
                    : user;
                var claims = new List<Claim>
                {
                    new(ClaimTypes.Name, name),
                    new("preferred_username", name),
                };
                foreach (var r in (roles ?? "Guest").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    claims.Add(new Claim(ClaimTypes.Role, r));

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
                return Results.Redirect(SanitizeReturnUrl(returnUrl));
            }).AllowAnonymous();
        }

        // POST /auth/logout — clears the session cookie.
        auth.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { signedOut = true });
        }).AllowAnonymous();

        return app;
    }

    /// <summary>Allows only local relative paths — blocks open-redirect via returnUrl.</summary>
    private static string SanitizeReturnUrl(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && Uri.IsWellFormedUriString(url, UriKind.Relative)
           && url.StartsWith('/')
           && !url.StartsWith("//", StringComparison.Ordinal)
            ? url
            : "/";

    public sealed record AuthStateDto(bool IsAuthenticated, string? Name, string[] Roles);
}

/// <summary>
/// Header-driven authentication for NON-PRODUCTION test runs only. Maps <c>X-Fake-User</c>
/// (and optional comma-separated <c>X-Fake-Roles</c>) onto a <see cref="ClaimsPrincipal"/>.
/// Throws if ever constructed in a Production host — defense in depth against misconfiguration.
/// </summary>
public sealed class FakeAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public FakeAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IWebHostEnvironment env)
        : base(options, logger, encoder)
    {
        if (env.IsProduction())
            throw new InvalidOperationException("FakeAuthHandler must never be registered in Production.");
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Fake-User", out var user) || string.IsNullOrWhiteSpace(user))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.ToString()),
            new("preferred_username", user.ToString()),
        };
        if (Request.Headers.TryGetValue("X-Fake-Roles", out var roles))
        {
            foreach (var r in roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                claims.Add(new Claim(ClaimTypes.Role, r));
        }

        var identity = new ClaimsIdentity(claims, BffAuthentication.FakeScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), BffAuthentication.FakeScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
