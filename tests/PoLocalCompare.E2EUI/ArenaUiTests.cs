using Microsoft.Playwright;

namespace PoLocalCompare.E2EUI;

/// <summary>
/// The Arena — the screen a duel actually ends on. No UI test covered it before, which is how
/// it came to throw on every load: the page derived its DuelId in OnParametersSet, but Blazor
/// runs OnInitializedAsync first, so the initial fetch went to /api/duels/ with an empty id,
/// matched the list route, and failed deserializing an array into one DuelDto.
///
/// This asserts the page mounts, shows duel content, and exposes the Lab Report anchor, which is
/// what that bug broke. Self-sufficient since 2026-11-17: it seeds the duel it needs when the
/// store has none. See <see cref="UiTestBase"/> for prerequisites; not run in CI.
/// </summary>
[Trait("Category", "UI")]
public sealed class ArenaUiTests : UiTestBase
{
    /// <summary>
    /// The newest recorded duel, or — on a fresh store — one seeded through the
    /// Development/Test-only <c>POST /api/dev/seed-duel</c>, which writes a finished, Voided duel
    /// with two result rows and no inference. Until 2026-11-17 the suite needed a duel someone had
    /// run by hand and failed outright without one. Reusing an existing duel keeps repeated runs
    /// from filling the Archive with fixtures.
    /// </summary>
    private static async Task<string?> DuelIdAsync(IPage page)
    {
        var id = await page.EvaluateAsync<string?>(
            """
            async () => {
                const list = await fetch('/api/duels?limit=1');
                if (list.ok) {
                    const rows = await list.json();
                    if (Array.isArray(rows) && rows.length > 0) return rows[0].duelId;
                }
                const seeded = await fetch('/api/dev/seed-duel', { method: 'POST' });
                return seeded.ok ? (await seeded.json()).duelId : null;
            }
            """);
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    [UiTheory]
    [MemberData(nameof(Viewports))]
    public async Task Arena_LoadsTheRequestedDuel_AndTheArchiveOffersTheLabReport(int width, int height)
    {
        // Seeded (when needed) from a signed-in page first, so the Archive loads with it.
        var page = await SignedInPageAsync(width, height, "/");
        var duelId = await DuelIdAsync(page);
        Assert.True(duelId is not null, "Could not find or seed a duel (is the app running in Development?).");

        await page.GotoAsync("/archive");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        // The export slice has no UI of its own, so the anchor is the only thing making that
        // endpoint reachable — it just lives on the Archive row rather than in the Arena, where
        // the export disclosure was dropped in the 2026-08-23 control-count pass. Asserted here
        // while the Archive is on screen, before navigating away to the duel itself.
        var report = page.Locator($"a[href='/api/duels/{duelId}/report']");
        await Assertions.Expect(report).ToHaveAttributeAsync(
            "download", $"lab-report-{duelId}.html", new() { Timeout = width < 768 ? 30_000 : 10_000 });

        await page.GotoAsync($"/arena/{duelId}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });

        var timeout = width < 768 ? 30_000 : 10_000;

        // The heading proving the component mounted...
        await Assertions.Expect(page.Locator(".arena__title")).ToBeVisibleAsync(new() { Timeout = timeout });

        // ...and no error boundary, which is what the empty-id fetch used to trip.
        await Assertions.Expect(page.Locator(".dev-error-panel")).Not.ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".arena__error")).Not.ToBeVisibleAsync();

        // The prompt only renders once the duel itself deserialized, so it is the real signal
        // that the right id reached the API.
        await Assertions.Expect(page.Locator(".arena__prompt-label")).ToBeVisibleAsync(new() { Timeout = timeout });
    }
}
