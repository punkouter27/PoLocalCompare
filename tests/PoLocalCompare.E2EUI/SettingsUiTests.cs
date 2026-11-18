using Microsoft.Playwright;
using PoLocalCompare.Shared.Presentation;

namespace PoLocalCompare.E2EUI;

/// <summary>
/// /settings: the gear's destination, the account, and the optional features that default to off.
///
/// Light and dark follow the device and nothing else since the Po web standard (2026-11-18): the
/// three theme chips and the <c>[data-theme]</c> override they drove are gone. What only a real
/// browser can tell you is that the palette really does follow <c>prefers-color-scheme</c>, and
/// that prefs.js applies the extras before first paint and keeps them across a reload.
/// </summary>
[Trait("Category", "UI")]
public sealed class SettingsUiTests : UiTestBase
{
    // --po-bg from po-tokens.css, light and dark.
    private const string DarkBackground = "rgb(15, 17, 23)";
    private const string LightBackground = "rgb(246, 247, 251)";

    private static Task<string> BodyBackgroundAsync(IPage page) =>
        page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor");

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Theme_FollowsTheDevice_AndExtrasStartOff(int width, int height)
    {
        var dark = await SignedInPageAsync(width, height, "/settings", ColorScheme.Dark);
        Assert.Equal(DarkBackground, await BodyBackgroundAsync(dark));
        Assert.Null(await dark.EvaluateAsync<string?>("() => document.documentElement.getAttribute('data-theme')"));
        await Assertions.Expect(dark.Locator(".settings .po-btn--chip")).ToHaveCountAsync(0);

        // The simplest app is the default: every optional feature, and sound, starts off.
        var switches = dark.Locator(".settings__switch");
        await Assertions.Expect(switches).ToHaveCountAsync(Preferences.All.Count);
        for (var i = 0; i < Preferences.All.Count; i++)
            await Assertions.Expect(switches.Nth(i)).Not.ToBeCheckedAsync();
        Assert.Null(await dark.EvaluateAsync<string?>("() => document.documentElement.getAttribute('data-fx')"));

        var light = await SignedInPageAsync(width, height, "/settings", ColorScheme.Light);
        Assert.Equal(LightBackground, await BodyBackgroundAsync(light));
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Settings_AreReachedFromTheGear_SurviveAReload_AndAreAppliedBeforeFirstPaint(int width, int height)
    {
        // Reached the way a user reaches it: the gear on the header.
        var page = await SignedInPageAsync(width, height, "/leaderboard");
        await page.Locator(".po-header__settings").ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/settings$"));

        // The account lives here now, not in a header menu.
        await Assertions.Expect(page.Locator(".settings__email")).ToContainTextAsync(GuestIdentity);
        await Assertions.Expect(page.Locator(".settings").GetByRole(AriaRole.Button, new() { Name = "Sign out" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".settings__build")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^v[0-9]{6}$"));

        await page.Locator(".settings__switch[data-pref='effects']").CheckAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-fx", "on");

        await page.ReloadAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });
        await Assertions.Expect(page.Locator(".settings__switch[data-pref='effects']")).ToBeCheckedAsync();

        await page.GotoAsync("/leaderboard");
        // DOMContentLoaded, not NetworkIdle: the assertion is that the attribute is present
        // before the app has finished booting, not merely afterwards.
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        Assert.Equal("on", await page.EvaluateAsync<string?>("() => document.documentElement.getAttribute('data-fx')"));
    }
}
