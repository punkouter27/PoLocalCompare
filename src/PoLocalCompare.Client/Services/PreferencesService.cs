using System.Runtime.InteropServices.JavaScript;
using PoLocalCompare.Shared.Presentation;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// Thin wrapper over <c>wwwroot/js/prefs.js</c>, which owns the state for the same reason
/// the theme does: the effects switch has to be on <c>&lt;html&gt;</c> before Blazor boots.
/// </summary>
/// <remarks>
/// Bound with [JSImport] to the <c>window.poPrefs</c> global prefs.js defines in &lt;head&gt;:
/// synchronous, so markup can ask <see cref="IsOn"/> inline, and trim-safe, where
/// IJSInProcessRuntime.Invoke&lt;T&gt; is not. Deliberately not cached: prefs.js follows other
/// tabs through the <c>storage</c> event, and a C# copy would keep answering with the old value
/// — for the paid helpers, that is a paid call the user turned off.
/// </remarks>
public sealed partial class PreferencesService
{
    public bool IsOn(Preference key)
    {
        try
        {
            return Get(Preferences.Info(key).StorageKey);
        }
        catch (JSException)
        {
            // prefs.js failed to load: every extra stays off, which is the default anyway.
            return false;
        }
    }

    public Task SetAsync(Preference key, bool on)
    {
        try
        {
            Set(Preferences.Info(key).StorageKey, on);
        }
        catch (JSException)
        {
            // prefs.js failed to load: nothing to persist to, and IsOn already answers off.
        }
        return Task.CompletedTask;
    }

    [JSImport("globalThis.poPrefs.get")]
    private static partial bool Get(string key);

    [JSImport("globalThis.poPrefs.set")]
    private static partial bool Set(string key, bool on);
}
