// prefs.js — the optional extras the user switches on in /settings (window.poPrefs), applied
// before Blazor boots. Light and dark are not a choice any more: they follow the device's
// prefers-color-scheme through po-tokens.css alone (Po web standard, 2026-11-18), so the theme
// half that stamped <html data-theme> is gone.
//
// ── Optional extras ──────────────────────────────────────────────────────────────────────
// Optional features, switched on and off from /settings. Every key is OFF until the user turns it
// on — that one rule is the whole default, so an empty localStorage gets the simplest app and
// there is no table of per-key defaults to keep in step with the C# side (Preferences.All).
//
// Loaded in <head> with the theme, because "effects" is read by fx.js, gpu-fx.js and wow.js
// (through poPrefs.motionAllowed) and wow.js's navigation hook is listening before Blazor has
// booted. "sound" is read by audio.js.
(() => {
    const PREFIX = 'polocalcompare:pref-';
    // Fallback when storage is blocked (private mode): the choice holds for this page view.
    const memory = {};

    const get = key => {
        // A choice made this page view wins: storage can be readable yet refuse writes (full
        // quota, some private modes), and reading it back would undo the toggle.
        if (key in memory) return memory[key];
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
        // The one gate for decorative motion: Settings → Visual effects on, and the OS not asking
        // for reduced motion. fx.js, gpu-fx.js and wow.js all ask this rather than keep a copy.
        motionAllowed() {
            if (document.documentElement.dataset.fx !== 'on') return false;
            try {
                return !window.matchMedia('(prefers-reduced-motion: reduce)').matches;
            } catch {
                return true;
            }
        },
        set(key, on) {
            memory[key] = !!on;
            try {
                if (on) localStorage.setItem(PREFIX + key, 'true');
                else localStorage.removeItem(PREFIX + key);
            } catch {
                // Storage unavailable — the in-memory value above still applies.
            }
            paint();
            // audio.js listens, so turning sound on or off takes effect without a reload.
            window.dispatchEvent(new CustomEvent('po-pref', { detail: { key, on: memory[key] } }));
            return memory[key];
        },
    };

    // Another tab changed a setting: follow it here, and tell listeners (audio.js) as set() does.
    window.addEventListener('storage', e => {
        if (!e.key?.startsWith(PREFIX)) return;
        const key = e.key.slice(PREFIX.length);
        memory[key] = e.newValue === 'true';
        paint();
        window.dispatchEvent(new CustomEvent('po-pref', { detail: { key, on: memory[key] } }));
    });

    paint();
})();
