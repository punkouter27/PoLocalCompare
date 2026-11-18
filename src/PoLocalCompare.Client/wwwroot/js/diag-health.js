/**
 * diag-health.js — the status half of /diag: the overall banner and one card per dependency in
 * three groups (API, Data, Services), with a Refresh that cannot be double-fired.
 *
 * Plain JS for the same reason as diag-models.js: /diag is a server-rendered Razor Page so it
 * still works when the WASM client is the broken thing. Everything comes from GET /api/diag,
 * which carries no endpoints, keys or exception messages; the API card is this fetch itself —
 * if it answers, the API is reachable, and its round trip is the time shown. Cards use the
 * shell's po-diag__card and po-badge--ok|warn|bad (po-tokens.css).
 */
(() => {
    'use strict';

    const byId = (id) => document.getElementById(id);
    const banner = byId('diag-banner');
    const refresh = byId('diag-refresh');
    const checked = byId('diag-checked');
    const apiCards = byId('diag-api-cards');
    const storeCards = byId('diag-store-cards');
    const serviceCards = byId('diag-service-cards');
    const environment = byId('diag-environment');

    const ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
    const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ESCAPES[c]);
    const time = (iso) => new Date(iso).toLocaleTimeString();

    const BANNER = {
        Healthy: ['ok', 'Everything is working.'],
        Degraded: ['warn', 'The app is up, but something it depends on is struggling.'],
        Unhealthy: ['bad', 'Something the app needs is down. See the red cards below.'],
    };

    // Plain words for the badge; the tone is what po-badge--ok|warn|bad colours.
    function badge(status, configured) {
        if (!configured) return '<span class="po-badge po-badge--warn">Not set up here</span>';
        if (status === 'Healthy') return '<span class="po-badge po-badge--ok">Working</span>';
        if (status === 'Degraded') return '<span class="po-badge po-badge--warn">Slow or partly down</span>';
        return '<span class="po-badge po-badge--bad">Not working</span>';
    }

    function card(name, purpose, status, configured, latencyMs, checkedAt) {
        const detail = [
            latencyMs == null ? null : `answered in ${esc(latencyMs)} ms`,
            checkedAt ? `checked ${esc(time(checkedAt))}` : null,
        ].filter(Boolean).join(' · ');
        return `
            <div class="po-diag__card">
                <span class="diag__card-name">${esc(name)}</span>
                ${badge(status, configured)}
                <span class="diag__card-purpose">${esc(purpose)}</span>
                <span class="diag__card-detail">${detail}</span>
            </div>`;
    }

    function cardsFor(checks, empty) {
        if (!checks.length) return `<p class="diag__empty">${esc(empty)}</p>`;
        return checks.map((c) => card(c.name, c.purpose, c.status, c.configured, c.latencyMs, c.checkedAt)).join('');
    }

    // Whole class names, not `--${tone}`: check-clean.ps1 reads an interpolated class as unused.
    const BANNER_CLASS = {
        ok: 'diag__banner diag__banner--ok',
        warn: 'diag__banner diag__banner--warn',
        bad: 'diag__banner diag__banner--bad',
        idle: 'diag__banner diag__banner--idle',
    };

    function setBanner(tone, text) {
        banner.className = BANNER_CLASS[tone];
        banner.textContent = text;
    }

    async function load() {
        if (refresh.disabled) return;          // busy guard: one probe at a time
        refresh.disabled = true;
        refresh.textContent = 'Refreshing…';
        setBanner('idle', 'Checking…');

        const started = performance.now();
        try {
            const response = await fetch('/api/diag', { cache: 'no-store', headers: { Accept: 'application/json' } });
            const elapsed = Math.round(performance.now() - started);
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const report = await response.json();

            const [tone, text] = BANNER[report.status] ?? ['warn', 'Some checks did not report a clear answer.'];
            setBanner(tone, text);
            apiCards.innerHTML = card('PoLocalCompare API', 'Serves this app, its data and the duels.', 'Healthy', true, elapsed, report.checkedAt);
            storeCards.innerHTML = cardsFor(report.stores, 'No data stores are registered.');
            serviceCards.innerHTML = cardsFor(report.services, 'No outside services are registered.');
            environment.textContent = report.environment;
            checked.textContent = `Last checked ${time(report.checkedAt)}`;
        } catch {
            setBanner('bad', 'Could not reach the API. The server may be down or restarting. Try Refresh.');
            apiCards.innerHTML = card('PoLocalCompare API', 'Serves this app, its data and the duels.', 'Unhealthy', true, null, new Date().toISOString());
            storeCards.innerHTML = '<p class="diag__empty">Unknown: the API did not answer.</p>';
            serviceCards.innerHTML = '<p class="diag__empty">Unknown: the API did not answer.</p>';
            checked.textContent = '';
        } finally {
            refresh.disabled = false;
            refresh.textContent = 'Refresh';
        }
    }

    refresh.addEventListener('click', load);
    load();
})();
