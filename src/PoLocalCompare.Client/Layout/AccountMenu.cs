using Microsoft.AspNetCore.Components.Authorization;

namespace PoLocalCompare.Client.Layout;

/// <summary>
/// The signed-in identity as the header shows it, and the dev personas its account menu
/// can switch between outside Production.
/// </summary>
internal static class AccountMenu
{
    public static readonly string[] DevPersonas =
    [
        "dev-reviewer",
        "dev-judge",
    ];

    /// <summary>
    /// The account's email (preferred_username, then the email claim, then the name), or null
    /// when signed out or when the session cannot be read.
    /// </summary>
    public static async Task<string?> ResolveIdentityAsync(AuthenticationStateProvider authState)
    {
        try
        {
            var user = (await authState.GetAuthenticationStateAsync()).User;
            if (user.Identity?.IsAuthenticated != true)
                return null;

            return user.FindFirst("preferred_username")?.Value
                ?? user.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                ?? user.Identity.Name;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
