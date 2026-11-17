/**
 * diag-health.js — the status half of /diag: overall banner, API, database/storage, services,
 * version and environment, with a Refresh that cannot be double-fired.
 *
 * Plain JS for the same reason as diag-models.js: /diag is a server-rendered Razor Page so it
 * still works when the WASM client is the broken thing. Everything comes from GET /api/diag,
 * which carries no endpoints, keys or exception messages; the API row is this fetch itself —
 * if it answers, the API is reachable, and its round trip is the response time shown.
 */
(() => {
    'use strict';

    const byId = (id) => document.getElementById(id);
    const banner = byId('diag-banner');
    const refresh = byId('diag-refresh');
    const checked = byId('diag-checked');
    const apiBody = byId('diag-api-rows');
    const storeBody = byId('diag-store-rows');
    const serviceBody = byId('diag-service-rows');
    const version = byId('diag-version');
    const environment = byId('diag-environment');

    const ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };
    const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ESCAPES[c]);
    const time = (iso) => new Date(iso).toLocaleTimeString();

    const BANNER = {
        Healthy: ['ok', 'All systems are working.'],
        Degraded: ['warn', 'The app is up, but something it depends on is struggling.'],
        Unhealthy: ['bad', 'Something the app needs is down — see below.'],
    };

    function chip(status, configured) {
        if (!configured) return '<span class="diag-status-chip diag-status-chip--muted">Not configured</span>';
        const tone = status === 'Healthy' ? 'ok' : status === 'Degraded' ? 'warn' : 'bad';
        return `<span class="diag-status-chip diag-status-chip--${tone}">${esc(status)}</span>`;
    }

    function rowsFor(checks, colspan, empty) {
        if (!checks.length) return `<tr><td colspan="${colspan}">${esc(empty)}</td></tr>`;
        return checks.map((c) => `
            <tr>
                <td>${esc(c.name)}<div class="diag-purpose">${esc(c.purpose)}</div></td>
                <td>${chip(c.status, c.configured)}${c.problem ? `<div class="diag-purpose">${esc(c.problem)}</div>` : ''}</td>
                <td>${c.latencyMs == null ? '—' : `${esc(c.latencyMs)} ms`}</td>
                <td>${esc(time(c.checkedAt))}</td>
            </tr>`).join('');
    }

    function setBanner(tone, text) {
        banner.className = `diag-banner diag-banner--${tone}`;
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

            const [tone, text] = BANNER[report.status] ?? ['warn', `Status: ${report.status}`];
            setBanner(tone, text);
            apiBody.innerHTML = `
                <tr>
                    <td>PoLocalCompare API<div class="diag-purpose">Serves this app, its data and the duels.</div></td>
                    <td>${chip('Healthy', true)}</td>
                    <td>${elapsed} ms</td>
                    <td>${esc(time(report.checkedAt))}</td>
                </tr>`;
            storeBody.innerHTML = rowsFor(report.stores, 4, 'No stores are registered.');
            serviceBody.innerHTML = rowsFor(report.services, 4, 'No external services are registered.');
            version.textContent = report.version;
            environment.textContent = report.environment;
            checked.textContent = `Last checked ${time(report.checkedAt)}`;
        } catch (err) {
            setBanner('bad', 'Could not reach the API. The server may be down or restarting — try Refresh.');
            apiBody.innerHTML = `<tr><td>PoLocalCompare API</td><td>${chip('Unhealthy', true)}</td><td>—</td><td>${esc(new Date().toLocaleTimeString())}</td></tr>`;
            storeBody.innerHTML = '<tr><td colspan="4">Unknown — the API did not answer.</td></tr>';
            serviceBody.innerHTML = '<tr><td colspan="4">Unknown — the API did not answer.</td></tr>';
            checked.textContent = '';
        } finally {
            refresh.disabled = false;
            refresh.textContent = 'Refresh';
        }
    }

    refresh.addEventListener('click', load);
    load();
})();
