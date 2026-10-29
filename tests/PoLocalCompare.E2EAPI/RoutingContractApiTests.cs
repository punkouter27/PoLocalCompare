using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PoLocalCompare.E2EAPI;

/// <summary>
/// Pins the anonymous surface. Deny-by-default only holds while the opt-outs are deliberate, and
/// an accidental AllowAnonymous answers 200 to everyone without failing any behaviour test.
/// </summary>
[Collection("E2EAPI")]
public sealed class RoutingContractApiTests(ApiAppFixture app)
{
    // Every route a signed-out caller may reach. A new public route fails this test until it is
    // added here on purpose. Static files and the Blazor host itself are public by design and
    // are identified by their metadata, not listed. The host runs as Development, so the
    // Development-only routes (OpenAPI, Scalar) are listed too, and so are the duel/tournament
    // writes that Development opens with Features:AllowAnonymousWrites (false in Production).
    private static readonly string[] AnonymousAllowList =
    [
        "/Diag",
        "/api/diag/smoke",
        "/api/duels/",
        "/api/duels/{duelId}/local-result",
        "/api/duels/{duelId}/verdict",
        "/api/models/download-status/{webLlmModelId}",
        "/api/tournaments/",
        "/auth/login",
        "/auth/login/fake",
        "/auth/login/microsoft",
        "/auth/logout",
        "/auth/me",
        "/favicon.ico",
        "/health",
        "/health/live",
        "/openapi/{documentName}.json",
        "/scalar/favicon.svg",
        "/scalar/scalar.aspnetcore.js",
        "/scalar/scalar.js",
        "/scalar/{documentName?}",
        "/{*path:nonfile}",
    ];

    [Fact]
    public void Anonymous_surface_is_exactly_the_allow_list()
    {
        var anonymous = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Where(e => !IsHostOrStaticAsset(e))
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal(AnonymousAllowList, anonymous);
    }

    [Fact]
    public void Every_other_endpoint_requires_a_signed_in_user()
    {
        var policy = app.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy;

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
    }

    private static bool IsHostOrStaticAsset(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.RawText!.TrimStart('/').StartsWith("_framework", StringComparison.Ordinal)
        || endpoint.Metadata.Any(m => m.GetType().Name is "StaticAssetDescriptor" or "ComponentTypeMetadata" or "RootComponentMetadata");
}
