using Microsoft.Playwright;
using PoLocalCompare.Shared.Presentation;

namespace PoLocalCompare.E2EUI;

/// <summary>
/// /settings: the theme, and the optional features that default to off.
///
/// The theme layer is the one piece of UI whose correctness cannot be asserted from markup
/// alone — it depends on cascade order between a <c>prefers-color-scheme</c> block and the
/// <c>[data-theme]</c> override, plus localStorage persistence across a reload. Only a real
/// browser can tell you whether that actually resolves the way it was written. Until 2026-10-26
/// these were ThemeUiTests and drove a cycling toggle in the user menu; the theme moved here
/// with the rest of the settings, as three chips.
/// </summary>
[Trait("Category", "UI")]
public sealed class SettingsUiTests : UiTestBase
{
    private const string DarkBackground = "rgb(0, 0, 0)";
    private const string LightBackground = "rgb(247, 248, 251)";

    private static Task<string> BodyBackgroundAsync(IPage page) =>
        page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor");

    private static Task<string?> ThemeAttributeAsync(IPage page) =>
        page.EvaluateAsync<string?>("() => document.documentElement.getAttribute('data-theme')");

    private static ILocator ThemeChip(IPage page, string label) =>
        page.Locator(".settings__theme .po-btn--chip", new() { HasText = label });

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Theme_OverridesThePreferenceInBothDirections_AndExtrasStartOff(int width, int height)
    {
        // The load-bearing case: an explicit choice must beat prefers-color-scheme. That only
        // works because the [data-theme] rules are declared after the media block — which is
        // why CLAUDE.md requires those blocks to stay last in app.css.
        var page = await SignedInPageAsync(width, height, "/settings", ColorScheme.Dark);

        // Starting state: no stored choice, so the attribute stays off and the media query rules.
        Assert.Null(await ThemeAttributeAsync(page));
        Assert.Equal(DarkBackground, await BodyBackgroundAsync(page));
        await Assertions.Expect(ThemeChip(page, "Auto")).ToHaveAttributeAsync("aria-pressed", "true");

        // The simplest app is the default: every optional feature, and sound, starts off.
        var switches = page.Locator(".settings__switch");
        await Assertions.Expect(switches).ToHaveCountAsync(Preferences.All.Count);
        for (var i = 0; i < Preferences.All.Count; i++)
            await Assertions.Expect(switches.Nth(i)).Not.ToBeCheckedAsync();
        Assert.Null(await page.EvaluateAsync<string?>("() => document.documentElement.getAttribute('data-fx')"));

        // SC 2.5.8 requires 24×24 CSS pixels.
        var box = await ThemeChip(page, "Light").BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box!.Width >= 24 && box.Height >= 24, $"Chip was {box.Width}×{box.Height}.");

        // Light: the explicit choice overriding a dark OS preference.
        await ThemeChip(page, "Light").ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
        Assert.Equal(LightBackground, await BodyBackgroundAsync(page));

        // Dark: the override works in the other direction too.
        await ThemeChip(page, "Dark").ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        Assert.Equal(DarkBackground, await BodyBackgroundAsync(page));

        // Auto: the attribute comes off and the media query is back in charge.
        await ThemeChip(page, "Auto").ClickAsync();
        Assert.Null(await ThemeAttributeAsync(page));
        Assert.Equal(DarkBackground, await BodyBackgroundAsync(page));
    }

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Settings_SurviveAReloadAndAreAppliedBeforeFirstPaint(int width, int height)
    {
        // prefs.js loads in <head> precisely so its attributes are already set by
        // the time the document finishes parsing — otherwise every navigation flashes the wrong
        // palette, and wow.js's navigation hook would read effects as off.
        var page = await SignedInPageAsync(width, height, "/settings", ColorScheme.Dark);

        await ThemeChip(page, "Light").ClickAsync();
        await page.Locator(".settings__switch[data-pref='effects']").CheckAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-fx", "on");

        await page.ReloadAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
        await Assertions.Expect(page.Locator(".settings__switch[data-pref='effects']")).ToBeCheckedAsync();

        await page.GotoAsync("/leaderboard");
        // DOMContentLoaded, not NetworkIdle: the assertion is that the attributes are present
        // before the app has finished booting, not merely afterwards.
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        Assert.Equal("light", await ThemeAttributeAsync(page));
        Assert.Equal("on", await page.EvaluateAsync<string?>("() => document.documentElement.getAttribute('data-fx')"));

        // And the tokens actually reach a rendered surface: the leaderboard grid is a plain
        // .po-table styled from the design tokens, so its computed colour must track the choice.
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });
        var header = page.Locator(".po-table th").First;
        await Assertions.Expect(header).ToBeVisibleAsync(new() { Timeout = 30_000 });
        var lightColor = await header.EvaluateAsync<string>("el => getComputedStyle(el).color");

        // Reached the way a user reaches it: from the user menu.
        await OpenUserMenuAsync(page);
        await page.Locator(".navmenu__menu-item", new() { HasText = "Settings" }).ClickAsync();
        await ThemeChip(page, "Dark").ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");

        await page.GotoAsync("/leaderboard");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });
        header = page.Locator(".po-table th").First;
        await Assertions.Expect(header).ToBeVisibleAsync(new() { Timeout = 30_000 });
        var darkColor = await header.EvaluateAsync<string>("el => getComputedStyle(el).color");

        Assert.NotEqual(lightColor, darkColor);
    }
}
