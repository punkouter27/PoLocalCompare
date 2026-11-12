using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace PoLocalCompare.E2EUI;

/// <summary>
/// Shared Playwright harness. Drives a real browser against a running instance across the
/// mandated mobile and desktop viewports (standards §4) and fails on any browser console
/// error, page exception, failed request, HTTP >= 400 response, or visible #blazor-error-ui.
/// </summary>
public abstract class UiTestBase : IAsyncLifetime
{
    /// <summary>Every UI journey runs on both viewports; mobile portrait is the primary target.</summary>
    public static TheoryData<int, int> Viewports => new()
    {
        { 390, 844 },    // mobile portrait
        { 1440, 900 },   // desktop
    };

    protected static string BaseUrl =>
        Environment.GetEnvironmentVariable("BASE_URL")?.TrimEnd('/') ?? "https://localhost:5001";

    private static bool Headless =>
        Environment.GetEnvironmentVariable("HEADLESS") is "1" or "true";

    private IPlaywright _pw = null!;
    private IBrowser _browser = null!;
    private readonly List<IPage> _trackedPages = [];
    private readonly ConcurrentQueue<string> _browserErrors = new();

    public async Task InitializeAsync()
    {
        _pw = await Playwright.CreateAsync();
        _browser = await _pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = Headless });
    }

    public async Task DisposeAsync()
    {
        try
        {
            foreach (var page in _trackedPages)
            {
                try
                {
                    if (!page.IsClosed && await page.Locator("#blazor-error-ui").IsVisibleAsync())
                        _browserErrors.Enqueue($"{page.Url}: #blazor-error-ui became visible.");
                }
                catch (PlaywrightException)
                {
                    // Page already navigated away or closed.
                }
            }
        }
        finally
        {
            await _browser.DisposeAsync();
            _pw.Dispose();
        }

        if (!_browserErrors.IsEmpty)
            throw new InvalidOperationException("E2EUI detected browser console/network errors:\n - " + string.Join("\n - ", _browserErrors));
    }

    /// <param name="colorScheme">Emulates the OS light/dark preference for theming tests.</param>
    protected async Task<IPage> NewPageAsync(int width, int height, ColorScheme? colorScheme = null)
    {
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = width, Height = height },
            IsMobile = width < 768,
            HasTouch = width < 768,
            ColorScheme = colorScheme,
        });
        var page = await context.NewPageAsync();
        _trackedPages.Add(page);

        page.Console += (_, msg) =>
        {
            if (msg.Type == "error")
                _browserErrors.Enqueue($"Console error on {page.Url}: {msg.Text}");
        };
        page.PageError += (_, err) => _browserErrors.Enqueue($"PageError on {page.Url}: {err}");
        page.Crash += (_, _) => _browserErrors.Enqueue($"Page crashed on {page.Url}");
        page.RequestFailed += (_, req) =>
        {
            if (req.Failure?.Contains("ERR_ABORTED", StringComparison.Ordinal) != true)
                _browserErrors.Enqueue($"RequestFailed {req.Method} {req.Url}: {req.Failure}");
        };
        page.Response += (_, res) =>
        {
            if (res.Status >= 400 && !res.Url.EndsWith("/favicon.ico", StringComparison.OrdinalIgnoreCase))
                _browserErrors.Enqueue($"HTTP {res.Status} for {res.Request.Method} {res.Url}");
        };

        return page;
    }

    /// <summary>
    /// Opens the nav's user menu — a native &lt;details&gt; holding Settings, Catalog and Logout. There is no phone drawer to open first any more: since 2026-10-11
    /// the links and the session controls sit on the bar at every width.
    /// </summary>
    protected static async Task OpenUserMenuAsync(IPage page)
    {
        if (await page.Locator(".navmenu__menu[open]").CountAsync() == 0)
            await page.Locator(".navmenu__menu-trigger").ClickAsync();
    }

    /// <summary>Identity the dev-only fake-auth route signs the browser in as.</summary>
    protected const string GuestIdentity = "GUEST_E2E_TEST";

    /// <summary>Builds the dev-only fake-auth URL that establishes a BFF cookie and lands on <paramref name="path"/>.</summary>
    protected static string SeedAuthUrl(string path) =>
        $"/auth/login/fake?user={GuestIdentity}&returnUrl={Uri.EscapeDataString(path)}";

    /// <summary>Signs in as a guest and lands on <paramref name="path"/>.</summary>
    protected async Task<IPage> SignedInPageAsync(int width, int height, string path = "/", ColorScheme? colorScheme = null)
    {
        var page = await NewPageAsync(width, height, colorScheme);
        await page.GotoAsync(SeedAuthUrl(path));
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 45_000 });
        return page;
    }
}
