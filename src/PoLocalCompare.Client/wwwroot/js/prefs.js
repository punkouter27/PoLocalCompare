// prefs.js — everything the user chooses in /settings, applied before Blazor boots: the theme
// (window.poTheme) and the optional extras (window.poPrefs). One <head> script instead of two;
// both halves have to run before first paint, and neither needs anything else on the page.
//
// ── Theme ────────────────────────────────────────────────────────────────────────────────
// Theme switching. Three states are possible and they are not the same thing:
//   "system" — no stored choice; follow prefers-color-scheme and keep following it as it changes.
//   "light" / "dark" — an explicit user choice, stamped onto <html data-theme> so the
//   [data-theme] rules in app.css override the prefers-color-scheme block.
//
// Everything the theme touches is a CSS custom property in app.css, so stamping the
// attribute is the whole job -- there is no per-theme stylesheet left to swap.
(() => {
    const KEY = 'po-theme';

    const systemPrefersDark = () =>
        window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;

    const stored = () => {
        try {
            const v = localStorage.getItem(KEY);
            return v === 'light' || v === 'dark' ? v : null;
        } catch {
            // Private mode / blocked storage: fall back to following the system.
            return null;
        }
    };

    // The theme actually being rendered, resolving "system" to a concrete value.
    const effective = () => stored() ?? (systemPrefersDark() ? 'dark' : 'light');

    const paint = () => {
        const theme = effective();
        const root = document.documentElement;

        // Only stamp the attribute for an explicit choice. Leaving it off when following the
        // system lets the prefers-color-scheme block stay in charge, so a viewer who changes
        // their OS setting mid-session sees it apply without a reload.
        if (stored()) {
            root.setAttribute('data-theme', theme);
        } else {
            root.removeAttribute('data-theme');
        }
    };

    window.poTheme = {
        // "light" | "dark" | "system"
        current: () => stored() ?? 'system',
        effective,
        set(theme) {
            try {
                if (theme === 'system') {
                    localStorage.removeItem(KEY);
                } else {
                    localStorage.setItem(KEY, theme);
                }
            } catch {
                // Storage unavailable — the choice still applies for this page view.
            }
            paint();
            return effective();
        },
    };

    paint();

    // Keep following the OS while no explicit choice is stored.
    if (window.matchMedia) {
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
            if (!stored()) paint();
        });
    }
})();

// ── Optional extras ──────────────────────────────────────────────────────────────────────
// Optional features, switched on and off from /settings. Every key is OFF until the user turns it
// on — that one rule is the whole default, so an empty localStorage gets the simplest app and
// there is no table of per-key defaults to keep in step with the C# side (Preferences.All).
//
// Loaded in <head> with the theme, because "effects" is read by fx.js, gpu-fx.js and wow.js
// (through poPrefs.motionAllowed) and wow.js's navigation hook is listening before Blazor has
// booted. "sound" is read by audio.js.
(() => {
    const PREFIX = 'po-pref-';
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
