// Utility helpers used by Blazor JS interop

/**
 * Moves keyboard focus to an element by id.
 *
 * Needed for the skip link: Blazor intercepts same-document anchor clicks and handles them as
 * router navigation, so the browser's native "scroll to the fragment target and focus it"
 * behaviour never runs. Without this the link scrolls but leaves focus in the nav, which
 * defeats the entire point of SC 2.4.1 Bypass Blocks for a keyboard user.
 *
 * @param {string} id - Target element id. The element needs tabindex="-1" to accept focus.
 * @param {string} [scrollId] - Scroll this element to the top instead of the focused one — for
 *   a field whose surrounding block (its heading, the controls above it) should come into view
 *   with it. Home passes its console so the two model slots arrive along with the prompt.
 */
window.focusElement = function (id, scrollId) {
    const el = document.getElementById(id);
    if (el) {
        const frame = (scrollId && document.getElementById(scrollId)) || el;
        el.focus({ preventScroll: true });
        // Glide only for the framed case, and never under reduced motion; the skip link jumps.
        const glide = frame !== el && !window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        frame.scrollIntoView({ block: 'start', behavior: glide ? 'smooth' : 'auto' });
    }
};

/**
 * Whether a media query matches right now. The Leaderboard asks once, at first render, to
 * decide whether a model name opens the side panel (wide) or the model's own page (narrow).
 * @param {string} query
 */
window.poMatches = query => window.matchMedia(query).matches;

/**
 * "Your duel/bracket finished" while the tab is in the background. ask() runs from the Compare
 * and Start clicks because browsers ignore a permission request without a user gesture; notify()
 * stays silent when the tab is visible, since the page itself is already showing the result.
 * ponytail: page-level Notification only — Android Chrome throws on the constructor (it requires
 * ServiceWorkerRegistration.showNotification), so mobile gets nothing; route through the service
 * worker if that matters.
 */
window.poNotify = {
    ask() {
        try {
            if ('Notification' in window && Notification.permission === 'default') Notification.requestPermission();
        } catch { /* decoration */ }
    },
    notify(title, body) {
        try {
            if (document.visibilityState !== 'hidden' || Notification.permission !== 'granted') return;
            const n = new Notification(title, { body, icon: '/favicon.png' });
            n.onclick = () => { window.focus(); n.close(); };
        } catch { /* decoration */ }
    }
};

/**
 * The GPU lease: the single answer to "may anything draw right now?".
 *
 * Browser models run WebLLM over WebGPU in this tab, and the tok/s the race reports is measured
 * while that happens. Any render loop that competes for the GPU makes that number wrong, so
 * every canvas effect in the app (fx.js's confetti burst) asks this first and does nothing
 * while it is held.
 *
 * webllm-interop.js acquires it when a worker starts and releases it when that worker completes,
 * errors or is terminated, so the Arena and the Tournament page (the two places a browser model
 * runs) are covered by the same hook and neither has to remember to pause anything. Holders are
 * counted, not flagged: two browser models in one duel hold two leases, and the GPU is free only
 * when both have finished.
 *
 * Classic script rather than a module so it exists before the first worker is created;
 * webllm-interop.js is a classic script too and cannot import one synchronously.
 */
window.poGpuLease = (() => {
    const holders = new Set();
    const listeners = new Set();

    const publish = () => {
        const busy = holders.size > 0;
        // Mirrored onto <html> so CSS can switch GPU-heavy treatments off with no JS per frame.
        try { document.documentElement.dataset.gpu = busy ? 'busy' : 'free'; } catch { /* pre-DOM */ }
        for (const fn of listeners) {
            try { fn(busy); } catch { /* a broken listener must not block the others */ }
        }
    };

    return {
        acquire(owner) { holders.add(String(owner)); publish(); },
        release(owner) { if (holders.delete(String(owner))) publish(); },
        busy() { return holders.size > 0; },
        /** Calls fn(busy) on every change. Returns an unsubscribe function. */
        subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); },
    };
})();

// Light dismiss for the nav's native <details> user menu: a click anywhere outside it closes
// it. Without this the panel only closed on Escape, navigation or the trigger, and otherwise
// floated over the page after a theme or sound toggle.
document.addEventListener('click', (e) => {
    for (const menu of document.querySelectorAll('details.navmenu__menu[open]')) {
        if (!menu.contains(e.target)) menu.removeAttribute('open');
    }
});
