using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// Reads the server-managed session via <c>/auth/me</c>. The WASM client holds no tokens —
/// the BFF owns the OIDC flow and the encrypted HttpOnly session cookie (sent automatically
/// on same-origin requests). See <c>BffAuthentication</c> on the API.
/// </summary>
public sealed class BffAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly HttpClient _http;
    private static readonly AuthenticationState Anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    public BffAuthenticationStateProvider(HttpClient http) => _http = http;

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var me = await _http.GetFromJsonAsync("auth/me", ClientJson.Default.AuthMe);
            if (me is null || !me.IsAuthenticated || string.IsNullOrEmpty(me.Name))
                return Anonymous;

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, me.Name),
                new("preferred_username", me.Name),
            };
            if (me.Roles is not null)
                claims.AddRange(me.Roles.Select(r => new Claim(ClaimTypes.Role, r)));

            var identity = new ClaimsIdentity(claims, authenticationType: "Bff", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role);
            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch
        {
            return Anonymous;
        }
    }

    /// <summary>Re-fetches <c>/auth/me</c> and notifies subscribers — call after login/logout.</summary>
    public void NotifyStateChanged()
        => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    internal sealed record AuthMe(bool IsAuthenticated, string? Name, string[]? Roles);
}
