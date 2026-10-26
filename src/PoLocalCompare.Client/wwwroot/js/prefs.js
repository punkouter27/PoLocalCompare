// Optional features, switched on and off from /settings. Every key is OFF until the user turns it
// on — that one rule is the whole default, so an empty localStorage gets the simplest app and
// there is no table of per-key defaults to keep in step with the C# side (UserPreferences).
//
// Loaded in <head> like theme.js, because "effects" is read by fx.js, gpu-fx.js and wow.js as
// <html data-fx="on">, and wow.js's navigation hook is listening before Blazor has booted.
(() => {
    const PREFIX = 'po-pref-';
    // Fallback when storage is blocked (private mode): the choice holds for this page view.
    const memory = {};

    const get = key => {
        try {
            return localStorage.getItem(PREFIX + key) === 'true';
        } catch {
            return memory[key] === true;
        }
    };

    const paint = () => {
        const root = document.documentElement;
        if (get('effects')) root.setAttribute('data-fx', 'on');
        else root.removeAttribute('data-fx');
    };

    window.poPrefs = {
        get,
        set(key, on) {
            memory[key] = !!on;
            try {
                if (on) localStorage.setItem(PREFIX + key, 'true');
                else localStorage.removeItem(PREFIX + key);
            } catch {
                // Storage unavailable — the in-memory value above still applies.
            }
            paint();
            return get(key);
        },
    };

    paint();
})();
