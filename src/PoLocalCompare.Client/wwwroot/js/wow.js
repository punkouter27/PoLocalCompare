/**
 * wow.js — DOM-level motion: route morphs, the leaderboard reshuffle, the bracket advance
 * and liquid glass.
 *
 * Everything here animates transform and opacity (or hands the work to the browser's own
 * View Transitions machinery), so it runs on the compositor and never contends with the WebGPU
 * device WebLLM generates on. The one GPU-heavier treatment — the liquid-glass refraction — is
 * switched off by CSS whenever the GPU lease is held (html[data-gpu="busy"], set by util.js).
 * The one canvas effect (confetti) lives in fx.js; this file owns no canvas.
 *
 * A classic script rather than a module because the navigation hook has to be listening before
 * the first click, and Blazor's interop reaches window.poWow.* directly.
 *
 * Everything respects prefers-reduced-motion by not animating at all.
 */
window.poWow = (() => {
    // Off unless Settings → Visual effects is on (prefs.js stamps <html data-fx="on">).
    const reduced = () => {
        if (document.documentElement.dataset.fx !== 'on') return true;
        try {
            return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        } catch {
            return false;
        }
    };

    // ── 1. View Transitions ──────────────────────────────────────────────────
    //
    // Blazor's router swaps the DOM on its own schedule, so a transition cannot just wrap
    // Blazor.navigateTo: the new page renders a frame or two later, and the destination
    // often shows a loading state first. The transition callback therefore navigates and then
    // waits for a selector that only exists once the destination has real content — capped,
    // because the browser freezes the old snapshot until the callback settles.

    const WAIT_CAP_MS = 1200;

    function waitFor(selector) {
        return new Promise(resolve => {
            if (!selector || document.querySelector(selector)) {
                requestAnimationFrame(() => resolve());
                return;
            }
            const done = () => {
                observer.disconnect();
                clearTimeout(timer);
                resolve();
            };
            const observer = new MutationObserver(() => {
                if (document.querySelector(selector)) done();
            });
            observer.observe(document.getElementById('app') ?? document.body, { childList: true, subtree: true });
            const timer = setTimeout(done, WAIT_CAP_MS);
        });
    }

    function blazorNavigate(url) {
        if (window.Blazor?.navigateTo) window.Blazor.navigateTo(url);
        else window.location.assign(url);
    }

    /**
     * Navigates with a morph. Elements sharing a view-transition-name across the two pages
     * (app.css) fly from where they were to where they land; everything else cross-fades.
     *
     * @param {string} url
     * @param {string} [waitForSelector] present only once the destination has content
     * @param {Element} [hero] marked as the shared element for this one navigation
     */
    function navigate(url, waitForSelector, hero) {
        if (!document.startViewTransition || reduced()) {
            blazorNavigate(url);
            return;
        }

        hero?.classList.add('po-vt-hero');
        const transition = document.startViewTransition(async () => {
            hero?.classList.remove('po-vt-hero');
            blazorNavigate(url);
            await waitFor(waitForSelector);
        });
        transition.finished.catch(() => { /* skipped or superseded: the navigation still happened */ });
    }

    // Links opt in with data-vt="<selector to wait for>"; the hero is the closest
    // [data-vt-hero] or the link itself. Capture phase + preventDefault: Blazor's own anchor
    // handler checks defaultPrevented and stands down, while @onclick handlers still run.
    document.addEventListener('click', event => {
        if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        const link = event.target instanceof Element ? event.target.closest('a[data-vt]') : null;
        if (!link || link.target === '_blank') return;

        const href = link.getAttribute('href');
        if (!href || !document.startViewTransition || reduced()) return;

        event.preventDefault();
        navigate(href, link.dataset.vt, link.closest('[data-vt-hero]') ?? link);
    }, true);

    // ── 2. Leaderboard reshuffle ─────────────────────────────────────────────

    const RANKS_KEY = 'polocalcompare.lastRanks';

    /**
     * Compares the board against what this viewer saw last time, stores the new standing, and
     * returns what moved: { modelId: { from, to, narrowedFrom } }. Per-viewer memory in
     * localStorage is the right store here — "since you last looked" is about one person.
     * First visit (or storage blocked) returns nothing, so nothing animates.
     *
     * @param {{id: string, rank: number, interval: number}[]} rows
     */
    function rankDiff(rows) {
        let previous = null;
        try {
            previous = JSON.parse(window.localStorage.getItem(RANKS_KEY) ?? 'null');
        } catch {
            previous = null;
        }

        const next = {};
        const moved = {};
        for (const row of rows) {
            next[row.id] = { rank: row.rank, interval: row.interval };
            const before = previous?.[row.id];
            if (!before) continue;
            const change = {};
            if (before.rank !== row.rank) {
                change.from = before.rank;
                change.to = row.rank;
            }
            if (row.interval < before.interval - 0.5) change.narrowedFrom = before.interval;
            if (Object.keys(change).length) moved[row.id] = change;
        }

        try {
            window.localStorage.setItem(RANKS_KEY, JSON.stringify(next));
        } catch {
            // Not remembered: the next visit simply has nothing to compare against.
        }
        return moved;
    }

    /**
     * FLIP: every moved row is already in its new place; play it in from where its old rank
     * sits on screen today. Measured from the rows themselves, so it is right in the table and
     * in the stacked card layout below 640px alike.
     *
     * @param {Record<string, {from?: number, to?: number}>} moved
     */
    function flipRows(moved) {
        if (reduced()) return;
        const rows = Array.from(document.querySelectorAll('tr[data-rank-row]'));
        const tops = rows.map(r => r.getBoundingClientRect().top);

        let delay = 0;
        for (const row of rows) {
            const change = moved[row.dataset.rankRow];
            if (!change?.from || !change.to) continue;

            const fromIndex = Math.min(rows.length - 1, change.from - 1);
            const offset = fromIndex >= 0 && fromIndex < tops.length
                ? tops[fromIndex] - tops[change.to - 1]
                : 24;

            row.animate(
                [
                    { transform: `translateY(${offset}px)`, opacity: 0.6 },
                    { transform: 'translateY(0)', opacity: 1 },
                ],
                { duration: 720, delay, easing: 'cubic-bezier(.2,.9,.25,1.15)', fill: 'backwards' });
            delay += 60;
        }
    }

    // ── 3. Bracket advance ───────────────────────────────────────────────────

    /**
     * A winner moving on: its slot in the next round plays in from the slot it won in. Both
     * carry data-slot="<modelId>" (Tournament.razor); rounds are columns in DOM order, so the
     * last two matches for a model are where it was and where it is now. FLIP again — the new
     * slot is already in place, and only its transform animates.
     *
     * @param {string[]} modelIds models that just appeared in a later-round slot
     */
    function advance(modelIds) {
        if (reduced()) return;

        let delay = 0;
        for (const id of modelIds ?? []) {
            const slots = Array.from(document.querySelectorAll(`[data-slot="${CSS.escape(id)}"]`));
            if (slots.length < 2) continue;

            const target = slots[slots.length - 1];
            const from = slots[slots.length - 2].getBoundingClientRect();
            const to = target.getBoundingClientRect();

            target.animate(
                [
                    { transform: `translate(${from.left - to.left}px, ${from.top - to.top}px)`, opacity: 0.5 },
                    { transform: 'translate(0, 0)', opacity: 1 },
                ],
                { duration: 680, delay, easing: 'cubic-bezier(.2,.9,.25,1.1)', fill: 'backwards' });
            delay += 90;
        }
    }

    // ── 4. Liquid glass ──────────────────────────────────────────────────────

    /**
     * backdrop-filter: url(#svg) is Chromium-only, and @supports cannot be trusted to say so —
     * other engines parse the declaration and then render nothing, which would drop the plain
     * blur too. So it is gated on the engine, as a class, and CSS keeps the plain blur for
     * everyone else. Pointer position drives the specular highlight via --gx/--gy.
     */
    function initGlass() {
        const brands = navigator.userAgentData?.brands ?? [];
        if (brands.some(b => /Chromium/i.test(b.brand))) {
            document.documentElement.classList.add('po-liquid');
        }
        if (reduced()) return;

        let frame = 0;
        let pending = null;
        document.addEventListener('pointermove', e => {
            pending = e;
            if (frame) return;
            frame = requestAnimationFrame(() => {
                frame = 0;
                const target = pending?.target instanceof Element ? pending.target.closest('.po-glass') : null;
                if (!target) return;
                const rect = target.getBoundingClientRect();
                target.style.setProperty('--gx', `${(((pending.clientX - rect.left) / rect.width) * 100).toFixed(1)}%`);
                target.style.setProperty('--gy', `${(((pending.clientY - rect.top) / rect.height) * 100).toFixed(1)}%`);
            });
        }, { passive: true });
    }

    initGlass();

    return { navigate, rankDiff, flipRows, advance };
})();
