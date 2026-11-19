using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices.JavaScript;
using PoLocalCompare.Shared.Presentation;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// Read-only view of the fixed feature switches in <c>wwwroot/js/prefs.js</c>.
/// </summary>
/// <remarks>
/// Bound with [JSImport] to the <c>window.poPrefs</c> global prefs.js defines in &lt;head&gt;:
/// synchronous, so markup can ask <see cref="IsOn"/> inline, and trim-safe, where
/// IJSInProcessRuntime.Invoke&lt;T&gt; is not. prefs.js stays the single source because the
/// effects switch has to be on <c>&lt;html&gt;</c> before Blazor boots.
/// </remarks>
[SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Injected Razor service API")]
public sealed partial class PreferencesService
{
    public bool IsOn(Preference key)
    {
        try
        {
            return Get(Preferences.StorageKey(key));
        }
        catch (JSException)
        {
            // prefs.js failed to load: every extra stays off, so no paid call can start by accident.
            return false;
        }
    }

    [JSImport("globalThis.poPrefs.get")]
    private static partial bool Get(string key);
}
