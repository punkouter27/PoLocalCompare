namespace PoLocalCompare.E2EUI;

/// <summary>
/// Whether this tier can run here, asked once per test run: a Playwright Chromium must be installed
/// and the app must answer at BASE_URL (default https://localhost:5001). When either is missing every
/// UI test SKIPS with one clear reason instead of failing (Po web standard, AGENTS.md). This tier is
/// local only; CI does not run it.
/// </summary>
public static class UiAvailability
{
    private static readonly Lazy<string?> Reason = new(Probe);

    /// <summary>Null when the tier can run, otherwise why not.</summary>
    public static string? SkipReason => Reason.Value;

    private static string? Probe()
    {
        if (!ChromiumInstalled())
        {
            return "No Playwright Chromium installed. Run: pwsh tests/PoLocalCompare.E2EUI/bin/Debug/net10.0/playwright.ps1 install chromium";
        }

        var baseUrl = Environment.GetEnvironmentVariable("BASE_URL")?.TrimEnd('/') ?? "https://localhost:5001";
        try
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var response = http.GetAsync(new Uri($"{baseUrl}/health/live")).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode
                ? null
                : $"The app at {baseUrl} answered /health/live with {(int)response.StatusCode}. Start it (dotnet run --project src/PoLocalCompare.Api) or set BASE_URL.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"No app answers at {baseUrl}. Start it (dotnet run --project src/PoLocalCompare.Api) or set BASE_URL.";
        }
    }

    private static bool ChromiumInstalled()
    {
        var root = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (string.IsNullOrEmpty(root) || root == "0")
        {
            root = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright")
                : OperatingSystem.IsMacOS()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "ms-playwright")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ms-playwright");
        }

        return Directory.Exists(root)
            && Directory.EnumerateDirectories(root, "chromium*").Any();
    }
}

/// <summary>A <see cref="FactAttribute"/> that skips when <see cref="UiAvailability.SkipReason"/> says the tier cannot run.</summary>
public sealed class UiFactAttribute : FactAttribute
{
    public UiFactAttribute()
    {
        if (UiAvailability.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that skips when <see cref="UiAvailability.SkipReason"/> says the tier cannot run.</summary>
public sealed class UiTheoryAttribute : TheoryAttribute
{
    public UiTheoryAttribute()
    {
        if (UiAvailability.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}
