using Microsoft.Playwright;

namespace PoLocalCompare.E2EUI;

/// <summary>
/// The Compare page at "/" — the app's primary journey. See <see cref="UiTestBase"/> for the
/// prerequisites; this suite is not run in CI.
/// </summary>
[Trait("Category", "UI")]
public sealed class HomeUiTests : UiTestBase
{
    [UiTheory]
    [MemberData(nameof(Viewports))]
    public async Task Unauthenticated_Root_ShowsLogin(int width, int height)
    {
        var page = await NewPageAsync(width, height);
        await page.GotoAsync("/");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        // The auth gate sends a signed-out visitor to /login, which wears the Po header like every
        // page; its card is the one sign-in control, so the header shows no second Sign in there.
        await Assertions.Expect(page.GetByText("Sign in with Microsoft")).ToBeVisibleAsync(
            new() { Timeout = 15_000 });
        await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/login[?]returnUrl="));
        await Assertions.Expect(page.Locator(".po-header__title")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".po-header .po-link-btn")).ToHaveCountAsync(0);

        // Po standard sign-in page: the title (the page's one h1) and exactly one button, nothing else.
        await Assertions.Expect(page.Locator("main h1")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator("main h1")).ToContainTextAsync("PoLocalCompare");
        await Assertions.Expect(page.Locator("main button, main a, main input")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator("main").GetByRole(AriaRole.Button)).ToHaveTextAsync("Sign in with Microsoft");

        // Signed out, no navigation: neither the header links nor the phone bottom nav.
        await Assertions.Expect(page.Locator(".navmenu__nav")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".bottom-nav")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator("nav")).ToHaveCountAsync(0);
    }

    [UiTheory]
    [MemberData(nameof(Viewports))]
    public async Task Login_ThenHome_ShowsHeadingAndDisabledCompare(int width, int height)
    {
        var page = await SignedInPageAsync(width, height, "/");

        // Headless Chromium on a narrow viewport is the slowest first paint in this matrix:
        // mobile network-idle takes longer and Blazor WASM download + boot adds seconds on top.
        // The default 5 s assertion timeout is too tight there; the same code on desktop has
        // headroom to spare. Generous enough to be reliable, narrow enough to still fail on a
        // genuine regression (a heading that does not render at all will not appear in 30 s).
        var timeout = width < 768 ? 30_000 : 10_000;
        await Assertions.Expect(page.Locator(".home__title")).ToContainTextAsync(
            "Compare two models", new() { Timeout = timeout });

        // The Compare CTA is always visible and stays disabled until two models and a prompt
        // exist. (The old selector looked for .home__btn--primary, a class removed when the
        // per-surface button classes were folded into .po-btn, so it had stopped running.)
        var compare = page.Locator(".home__compare .po-btn");
        await Assertions.Expect(compare).ToBeVisibleAsync();
        await Assertions.Expect(compare).ToBeDisabledAsync();
    }

    [UiTheory]
    [MemberData(nameof(Viewports))]
    public async Task Home_RendersTheModelGrid(int width, int height)
    {
        var page = await SignedInPageAsync(width, height);

        await Assertions.Expect(page.Locator(".home__grid").First).ToBeVisibleAsync(
            new() { Timeout = 20_000 });
    }

    [UiTheory]
    [MemberData(nameof(Viewports))]
    public async Task ModelCards_AreKeyboardOperableAndExposeTheirState(int width, int height)
    {
        // SC 2.1.1: the card is a custom toggle button, so it must be reachable by keyboard.
        // SC 4.1.2 Name, Role, Value: aria-pressed has to track the visual selection.
        var page = await SignedInPageAsync(width, height);

        var card = page.Locator(".model-card").First;
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = 20_000 });

        await Assertions.Expect(card).ToHaveAttributeAsync("role", "button");
        await Assertions.Expect(card).ToHaveAttributeAsync("tabindex", "0");

        // Lowercase "false": the value is rendered from a string property. Binding the bool
        // directly made Blazor omit the attribute entirely when unselected.
        await Assertions.Expect(card).ToHaveAttributeAsync("aria-pressed", "false");
    }

    [UiTheory]
    [MemberData(nameof(Viewports))]
    public async Task SuggestMatchup_FillsBothSlots_AndSaysWhy(int width, int height)
    {
        // A9: one click picks the pair the leaderboard learns most from. Both slots fill, the
        // button goes (there is nothing left for it to fill), and the picker's status line —
        // a live region — explains the choice. Shown with every setting at its default: it is
        // free, and settling ratings is the app's job.
        var page = await SignedInPageAsync(width, height);

        var suggest = page.Locator(".home__picker-actions .po-btn", new() { HasText = "Suggest matchup" });
        await Assertions.Expect(suggest).ToBeEnabledAsync(new() { Timeout = 20_000 });
        await suggest.ClickAsync();

        await Assertions.Expect(page.Locator(".home__slot--set")).ToHaveCountAsync(2, new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator(".home__full-hint")).ToContainTextAsync("moves the leaderboard most");
        await Assertions.Expect(suggest).ToHaveCountAsync(0);
    }
}
