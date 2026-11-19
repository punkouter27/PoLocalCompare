// prefs.js — the app's fixed feature switches (window.poPrefs), applied before Blazor boots.
// There is no settings page: everyone gets the values in ON below, and changing one is a code
// change here. Light and dark follow the device's prefers-color-scheme through po-tokens.css.
//
// Loaded in <head>, because "effects" is read by fx.js, gpu-fx.js and wow.js (through
// poPrefs.motionAllowed) and wow.js's navigation hook is listening before Blazor has booted.
// "sound" is read by audio.js; the rest by PreferencesService (C#), through poPrefs.get.
(() => {
    const ON = {
        'effects': true,            // page transitions, confetti, verdict nebula, champion storm
        'sound': true,              // cues when a duel starts, finishes and is judged
        'arena-details': true,      // live tokens, speed, cost and energy under each panel
        'leaderboard-chart': true,  // the rating-for-the-money chart beside the leaderboard
        'assistants': false,        // Sharpen prompt and scouting reports: each is a paid model call
    };

    const get = key => ON[key] === true;

    window.poPrefs = {
        get,
        // The one gate for decorative motion: effects on, and the OS not asking for reduced
        // motion. fx.js, gpu-fx.js and wow.js all ask this rather than keep a copy.
        motionAllowed() {
            if (document.documentElement.dataset.fx !== 'on') return false;
            try {
                return !window.matchMedia('(prefers-reduced-motion: reduce)').matches;
            } catch {
                return true;
            }
        },
    };

    // Choices saved by the old /settings page no longer apply; clear them so nothing stale lingers.
    try {
        Object.keys(localStorage)
            .filter(k => k.startsWith('polocalcompare:pref-'))
            .forEach(k => localStorage.removeItem(k));
    } catch {
        // Storage blocked (private mode): nothing was saved there either.
    }

    if (get('effects')) document.documentElement.setAttribute('data-fx', 'on');
})();
