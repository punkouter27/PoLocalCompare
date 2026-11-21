using Microsoft.Playwright;

namespace PoLocalCompare.E2EUI;

/// <summary>
/// Header contract, routing, and the accessibility affordances that only exist at runtime
/// (focus order, the skip link, geometry).
///
/// Trimmed to the AGENTS.md §8 ratio in the 2026-08-13 prune: assertions that used to sit in
/// their own method are folded into the journey that already navigates there, so coverage is
/// kept while the case count drops. Trimmed again on 2026-09-10 for the same reason — the
/// first-load console check now rides along with the route journey that already visited those
/// pages. 4 viewport-paired methods + 1 viewport-independent = 9 cases.
/// </summary>
[Trait("Category", "UI")]
public sealed class NavigationUiTests : UiTestBase
{
    [UiTheory]
    [MemberData(nameof(Viewports))]
    public async Task Header_LaysOutWithoutOverlappingOrLeavingTheViewport(int width, int height)
    {
        // The header's items are nowrap. Any that is allowed to shrink is squeezed narrower
        // than its own label, the label spills out of the box, and the next item paints on top
        // of it. Nothing else in the suite looks at geometry, so this is the only place that can
        // catch it.
        const string signedInEmail = "e2e.judge@example.com";
        var page = await SignedInPageAsync(width, height, user: signedInEmail);

        // The Po shell (po-tokens.css): the app name upper-left, the signed-in identity
        // upper-right, at every width. No settings gear since /settings was removed (2026-11-19).
        var title = await page.Locator(".po-header__title").BoundingBoxAsync();
        var actions = await page.Locator(".po-header__actions").BoundingBoxAsync();
        Assert.NotNull(title);
        Assert.NotNull(actions);
        Assert.True(title!.X < actions!.X, "The app name should sit left of the session controls.");
        await Assertions.Expect(page.Locator(".po-header__title")).ToContainTextAsync("PoLocalCompare");

        var email = page.Locator(".po-header__email");
        await Assertions.Expect(email).ToBeVisibleAsync();
        // The test sign-in's address, exactly, in the header's top-right corner.
        await Assertions.Expect(email).ToHaveTextAsync(signedInEmail);
        var emailBox = await email.BoundingBoxAsync();
        Assert.True(emailBox!.Y < 56 && emailBox.X + emailBox.Width > width / 2.0, "The signed-in email should sit top right.");
        await Assertions.Expect(page.Locator(".po-header__settings")).ToHaveCountAsync(0);

        // The email is the account menu: opening it shows Sign out, inside the viewport.
        var signOut = page.Locator(".navmenu__account-menu").GetByRole(AriaRole.Button, new() { Name = "Sign out" });
        await Assertions.Expect(signOut).ToBeHiddenAsync();
        await email.ClickAsync();
        await Assertions.Expect(signOut).ToBeVisibleAsync();
        var signOutBox = await signOut.BoundingBoxAsync();
        Assert.True(signOutBox!.X >= 0 && signOutBox.X + signOutBox.Width <= width + 1, "Sign out should open inside the viewport.");
        await email.ClickAsync();

        // One build stamp, vYYMMDD.
        await Assertions.Expect(page.Locator(".po-version")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".po-version")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^v[0-9]{6}$"));

        var problems = await page.EvaluateAsync<string[]>(
            """
            () => {
                const sel = '.po-header__title, .navmenu__mock-chip, .navmenu__link, .po-header__email';
                const els = [...document.querySelectorAll(sel)].filter(e => e.offsetParent !== null);
                const label = e => (e.textContent || e.className).trim().slice(0, 24);
                const bad = [];
                for (let i = 0; i < els.length; i++) {
                    const a = els[i].getBoundingClientRect();
                    if (a.right > window.innerWidth + 1)
                        bad.push('clipped by the viewport: ' + label(els[i]));
                    for (let j = i + 1; j < els.length; j++) {
                        if (els[i].contains(els[j]) || els[j].contains(els[i])) continue;
                        const b = els[j].getBoundingClientRect();
                        if (Math.min(a.right, b.right) - Math.max(a.left, b.left) > 1 &&
                            Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top) > 1)
                            bad.push('overlap: ' + label(els[i]) + ' / ' + label(els[j]));
                    }
                }
                return bad;
            }
            """);

        Assert.True(problems.Length == 0, string.Join(" | ", problems));
    }

    [UiTheory]
    [MemberData(nameof(Viewports))]
    public async Task Tab_OpensTheDeveloperWindow_AndTheSkipLinkMovesFocusToMain(int width, int height)
    {
        // Po web standard: Tab with nothing focused opens /po-dev.html in its own window
        // (js/po-devwindow.js). A fresh load leaves focus on the document — FocusOnNavigate only
        // runs after the first in-app navigation — so the first Tab is the developer window's.
        var page = await SignedInPageAsync(width, height);
        await page.EvaluateAsync("() => document.activeElement?.blur()");
        var popupTask = page.Context.WaitForPageAsync(new() { Timeout = 10_000 });
        await page.Keyboard.PressAsync("Tab");
        var popup = await popupTask;
        await popup.WaitForLoadStateAsync();
        Assert.EndsWith("/po-dev.html", popup.Url);
        await Assertions.Expect(popup.Locator("#live")).ToHaveTextAsync("UP", new() { Timeout = 15_000 });
        await popup.CloseAsync();

        // SC 2.4.1 Bypass Blocks — the link is visually hidden until focused, and Enter moves
        // focus to main.
        await page.FocusAsync(".skip-link");
        await page.Keyboard.PressAsync("Enter");

        var focusedId = await page.EvaluateAsync<string>("() => document.activeElement?.id ?? ''");
        Assert.Equal("main-content", focusedId);
    }

    [UiTheory]
    [MemberData(nameof(Viewports))]
    public async Task CoreRoutes_RenderTheirHeadingAndDoNotScrollHorizontally(int width, int height)
    {
        // Route reachability, the horizontal-overflow check and the first-load console check were
        // three separate methods. They want the same navigation, so they share one: each route
        // must mount its own titled element (not merely return 200), must not shear its right
        // edge, and the journey must not log a single console error. Mobile portrait is the
        // primary target and the classic place overflow shows up.
        var routes = new (string Path, string TitleSelector)[]
        {
            ("/", ".home__title"),
            ("/leaderboard", ".leaderboard__title"),
            ("/archive", ".archive__title"),
        };

        var page = await NewPageAsync(width, height);

        // Registered before the first navigation, so the boot sequence is covered too.
        var errors = new List<string>();
        page.Console += (_, msg) =>
        {
            if (msg.Type == "error") errors.Add(msg.Text);
        };

        await page.GotoAsync(SeedAuthUrl("/"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        foreach (var (path, titleSelector) in routes)
        {
            await page.GotoAsync(path);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

            await Assertions.Expect(page.Locator(titleSelector)).ToBeVisibleAsync(
                new() { Timeout = 20_000 });

            var overflows = await page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1");
            Assert.False(overflows, $"{path} overflows horizontally at {width}×{height}.");
        }

        Assert.True(errors.Count == 0, "Console errors: " + string.Join(" | ", errors));
    }

    [UiTheory]
    [MemberData(nameof(Viewports))]
    public async Task DiagPage_ShowsSystemStatusButIsNotLinkedFromTheApp(int width, int height)
    {
        // Two halves of one contract. /diag is the standard "System status" page (Po web standard):
        // anonymous, so it works without a session — but /health and /diag carry no UI link.
        // Signed out on purpose, and the base class fails the case on any console error or failed
        // request (a 401 included), at both widths.
        var page = await NewPageAsync(width, height);
        await page.GotoAsync("/diag");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync("System status", new() { Timeout = 30_000 });
        await Assertions.Expect(page).ToHaveTitleAsync("System status");

        // Overall banner in plain words, then API, Data and Services, one card per check, all from
        // /api/diag, and a Check again that re-enables. The page wears the app's header and stamp.
        var banner = page.Locator("[data-testid=diag-banner]");
        await Assertions.Expect(banner).Not.ToContainTextAsync("Checking…", new() { Timeout = 30_000 });
        await Assertions.Expect(banner).ToContainTextAsync(new System.Text.RegularExpressions.Regex("^(Everything is working|Working, with [0-9]+ things? degraded|Not working)"));
        await Assertions.Expect(page.Locator("[data-group=api] .po-diag__card")).ToContainTextAsync("ms");
        await Assertions.Expect(page.Locator("[data-group=data] .po-diag__card[data-check=azureTableStorage]")).ToContainTextAsync("Table Storage");
        await Assertions.Expect(page.Locator("[data-group=data] .po-diag__card[data-check=azureBlobStorage]")).ToContainTextAsync("Blob Storage");
        await Assertions.Expect(page.Locator("[data-group=service]")).ToContainTextAsync("Azure AI Foundry");
        await Assertions.Expect(page.Locator(".po-header__title")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".po-header .po-link-btn.filled.small")).ToHaveTextAsync("Sign in");
        await Assertions.Expect(page.Locator(".po-version")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^v[0-9]{6}$"));
        await Assertions.Expect(page.Locator("[data-group=models]")).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Button, new() { Name = "Check again" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Check again" })).ToBeEnabledAsync(new() { Timeout = 30_000 });

        var overflows = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1");
        Assert.False(overflows, $"/diag overflows horizontally at {width}×{height}.");

        await page.GotoAsync(SeedAuthUrl("/"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        var diagLinks = await page.Locator("nav a[href='/diag'], nav a[href='/health']").CountAsync();
        Assert.Equal(0, diagLinks);
    }

    [UiFact]
    public async Task UnknownRoute_RendersTheNotFoundPage()
    {
        // Viewport-independent: this is Router.NotFoundPage resolving at all, which is also the
        // only reflective component instantiation left in the app and the reason PublishTrimmed
        // stays off. One viewport proves it; running it twice proves nothing extra.
        var page = await SignedInPageAsync(1440, 900, "/no-such-page");

        // Assert on the page's own element: body text also contains the skip link and nav, so
        // a substring match there passes for the wrong reasons and is slow to settle.
        await Assertions.Expect(page.Locator(".empty-state__title")).ToBeVisibleAsync(
            new() { Timeout = 20_000 });
    }
}
