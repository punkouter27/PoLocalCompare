using Microsoft.JSInterop;
using PoLocalCompare.Shared.Presentation;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// Thin wrapper over <c>wwwroot/js/prefs.js</c>, which owns the state for the same reason
/// theme.js does: the effects switch has to be on <c>&lt;html&gt;</c> before Blazor boots.
/// </summary>
/// <remarks>
/// Reads are synchronous (<see cref="IJSInProcessRuntime"/>, which is what a WebAssembly host
/// hands out) so markup can ask <see cref="IsOn"/> inline, and are cached per session because
/// only <see cref="SetAsync"/> changes them.
/// </remarks>
public sealed class PreferencesService(IJSRuntime js)
{
    private readonly Dictionary<Preference, bool> _cache = [];

    /// <summary>Raised after <see cref="SetAsync"/>, so a page already on screen can re-render.</summary>
    public event Action? OnChanged;

    public bool IsOn(Preference key)
    {
        if (_cache.TryGetValue(key, out var on)) return on;
        try
        {
            on = ((IJSInProcessRuntime)js).Invoke<bool>("poPrefs.get", Preferences.Info(key).StorageKey);
        }
        catch (JSException)
        {
            // prefs.js failed to load: every extra stays off, which is the default anyway.
            on = false;
        }
        return _cache[key] = on;
    }

    public async Task SetAsync(Preference key, bool on)
    {
        try
        {
            _cache[key] = await js.InvokeAsync<bool>("poPrefs.set", Preferences.Info(key).StorageKey, on);
        }
        catch (JSException)
        {
            _cache[key] = on;
        }
        OnChanged?.Invoke();
    }
}
