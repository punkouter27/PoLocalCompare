using Microsoft.Playwright;

namespace PoLocalCompare.E2EUI;

/// <summary>
/// Header contract, routing, and the accessibility affordances that only exist at runtime
/// (focus order, the skip link, geometry).
///
/// Trimmed to the AGENT.MD §8 ratio in the 2026-08-13 prune: assertions that used to sit in
/// their own method are folded into the journey that already navigates there, so coverage is
/// kept while the case count drops. Trimmed again on 2026-09-10 for the same reason — the
/// first-load console check now rides along with the route journey that already visited those
/// pages. 4 viewport-paired methods + 1 viewport-independent = 9 cases.
/// </summary>
[Trait("Category", "UI")]
public sealed class NavigationUiTests : UiTestBase
{
    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task Header_LaysOutWithoutOverlappingOrLeavingTheViewport(int width, int height)
    {
        // The header's items are nowrap. Any that is allowed to shrink is squeezed narrower
        // than its own label, the label spills out of the box, and the next item paints on top
        // of it. Nothing else in the suite looks at geometry, so this is the only place that can
        // catch it.
        var page = await SignedInPageAsync(width, height);

        // Standards §4 layout contract: Left (branding) | Centre (actions) | Right (session).
        // Branding and session share the bar's first row at every width — below 768px the
        // links drop to a second row beneath them rather than into a drawer — so the same
        // horizontal assertion holds on a phone and on a desktop.
        var brand = await page.Locator(".navmenu__brand").BoundingBoxAsync();
        var auth = await page.Locator(".navmenu__auth").BoundingBoxAsync();
        Assert.NotNull(brand);
        Assert.NotNull(auth);
        Assert.True(brand!.X < auth!.X, "Branding should sit left of the session controls.");

        // The signed-in identity is part of that right-hand cluster.
        await Assertions.Expect(page.Locator(".navmenu__user-badge")).ToBeVisibleAsync();

        var problems = await page.EvaluateAsync<string[]>(
            """
            () => {
                const sel = '.navmenu__brand, .navmenu__mock-chip, .navmenu__link, .navmenu__menu-trigger';
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

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task SkipLink_IsTheFirstTabStopAndMovesFocusToMain(int width, int height)
    {
        // SC 2.4.1 Bypass Blocks — the link is visually hidden until focused.
        var page = await SignedInPageAsync(width, height);

        // Blazor's <FocusOnNavigate Selector="h1"> parks focus on the heading after routing,
        // so tabbing from there lands *past* the skip link. Reset to the document start —
        // which is where a person arriving at the page actually begins.
        await page.EvaluateAsync("() => document.activeElement?.blur()");
        await page.Keyboard.PressAsync("Tab");

        var focusedClass = await page.EvaluateAsync<string>(
            "() => document.activeElement?.className ?? ''");
        Assert.Contains("skip-link", focusedClass);

        await page.Keyboard.PressAsync("Enter");

        var focusedId = await page.EvaluateAsync<string>("() => document.activeElement?.id ?? ''");
        Assert.Equal("main-content", focusedId);
    }

    [Theory]
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

    [Theory]
    [MemberData(nameof(Viewports))]
    public async Task DiagPage_RendersServerSideButIsNotLinkedFromTheApp(int width, int height)
    {
        // Two halves of one contract. /diag is server-rendered on purpose — it is the page you
        // open when the client is the thing that is broken, and index.html's boot fallback links
        // to it — but standards also say /health and /diag carry no UI link.
        var page = await NewPageAsync(width, height);
        await page.GotoAsync("/diag");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        await Assertions.Expect(page.Locator("h1")).ToContainTextAsync("Diagnostics");

        await page.GotoAsync(SeedAuthUrl("/"));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        var diagLinks = await page.Locator("nav a[href='/diag'], nav a[href='/health']").CountAsync();
        Assert.Equal(0, diagLinks);
    }

    [Fact]
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
