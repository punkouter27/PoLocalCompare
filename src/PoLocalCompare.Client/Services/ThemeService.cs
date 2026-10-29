using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

/// <summary>The three selectable theme states. <see cref="System"/> means "follow the OS".</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Thin wrapper over <c>wwwroot/js/prefs.js</c> (its theme half). The JS module owns the state — it has to, because
/// it runs in <c>&lt;head&gt;</c> before Blazor boots to avoid a wrong-palette flash — so this type
/// reads through to it rather than caching a copy that could drift.
/// </summary>
public sealed class ThemeService(IJSRuntime js)
{
    private readonly IJSRuntime _js = js;

    /// <summary>Raised after a successful <see cref="SetAsync"/> so the header can re-render.</summary>
    public event Action? OnChanged;

    public async Task<AppTheme> GetAsync()
    {
        try
        {
            var value = await _js.InvokeAsync<string>("poTheme.current");
            return Parse(value);
        }
        catch (JSException)
        {
            // prefs.js failed to load — the CSS defaults still render, so report the fallback
            // rather than surfacing an error the viewer cannot act on.
            return AppTheme.System;
        }
    }

    /// <summary>Applies a theme and returns what is now rendering (System resolves to Light/Dark).</summary>
    public async Task<AppTheme> SetAsync(AppTheme theme)
    {
        try
        {
            var effective = await _js.InvokeAsync<string>("poTheme.set", Serialize(theme));
            OnChanged?.Invoke();
            return Parse(effective);
        }
        catch (JSException)
        {
            return AppTheme.System;
        }
    }

    private static string Serialize(AppTheme theme) => theme switch
    {
        AppTheme.Light => "light",
        AppTheme.Dark => "dark",
        _ => "system",
    };

    private static AppTheme Parse(string? value) => value switch
    {
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => AppTheme.System,
    };
}
