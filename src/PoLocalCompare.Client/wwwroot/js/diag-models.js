/**
 * diag-models.js — the Model health section on /diag.
 *
 * Replaces the Blazor `ModelHealthPanel` that used to sit on the Home page (removed
 * 2026-08-23). It is plain JS rather than a component because /diag is a server-rendered
 * Razor Page on purpose: it has to work when the WASM client is the thing that is broken,
 * which is what `index.html`'s boot-timeout fallback links here for.
 *
 * The probing itself is not reimplemented — `diag-interop.js` already owns `checkWebGpu`,
 * `checkWebNn` and the `runModelDiag` worker runner, and was always framework-free. This
 * file is the controller and the table.
 *
 * How each model type is actually tested, because they are genuinely different:
 *   remote  — GET /api/models/availability. That endpoint sends a real 16-token completion
 *             to each Foundry deployment, so a pass here means inference worked, not that a
 *             URL resolved.
 *   ollama  — POST /api/ollama/benchmark with the verification prompt, on the local daemon.
 *   browser — runModelDiag in this tab, over WebGPU, one at a time. Sequential is not a
 *             simplification: they share one GPU, and running two at once is how you get
 *             "Device was lost" instead of a result.
 */
(() => {
    'use strict';

    const TYPE_LABEL = { Remote: 'remote', LocalService: 'ollama', Local: 'browser' };

    let rows = [];
    let cdnTemplates = [];
    let running = false;
    let cancelled = false;

    const byId = (id) => document.getElementById(id);
    const elRows = byId('mh-rows');
    const elEnv = byId('mh-env');
    const elSummary = byId('mh-summary');
    const btnRun = byId('mh-run');
    const btnCancel = byId('mh-cancel');
    const btnRefresh = byId('mh-refresh');
    const inpPrompt = byId('mh-prompt');

    // ── Rendering ────────────────────────────────────────────────────────────

    const STATUS_CLASS = {
        ok: 'mh-st--ok',
        failed: 'mh-st--bad',
        running: 'mh-st--run',
        idle: 'mh-st--idle',
        skipped: 'mh-st--skip',
    };

    const ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };

    function esc(value) {
        return String(value === null || value === undefined ? '' : value)
            .replace(/[&<>"']/g, (c) => ESCAPES[c]);
    }

    function render() {
        if (rows.length === 0) {
            elRows.innerHTML = '<tr><td colspan="6">No models registered.</td></tr>';
            return;
        }
        elRows.innerHTML = rows.map((r) => {
            const cls = STATUS_CLASS[r.status] || 'mh-st--idle';
            const load = r.loadMs === null ? '—' : (r.loadMs / 1000).toFixed(1) + 's';
            const tps = r.tps === null ? '—' : String(r.tps);
            const type = TYPE_LABEL[r.model.modelType] || r.model.modelType;
            return '<tr>'
                + '<td>' + esc(r.model.displayName) + '</td>'
                + '<td>' + esc(type) + '</td>'
                + '<td class="' + cls + '">' + esc(r.status) + '</td>'
                + '<td class="mh-num">' + esc(load) + '</td>'
                + '<td class="mh-num">' + esc(tps) + '</td>'
                + '<td class="mh-detail">' + esc(r.note) + '</td>'
                + '</tr>';
        }).join('');
    }

    function summarise() {
        if (rows.length === 0) {
            elSummary.textContent = '';
            return;
        }
        const ok = rows.filter((r) => r.status === 'ok').length;
        const bad = rows.filter((r) => r.status === 'failed').length;
        const done = ok + bad;
        elSummary.textContent = done === 0
            ? ''
            : ok + ' of ' + done + ' tested models responded' + (bad ? ' · ' + bad + ' failed' : '') + '.';
    }

    function setRow(modelId, patch) {
        const row = rows.find((r) => r.model.modelId === modelId);
        if (!row) return;
        Object.assign(row, patch);
        render();
        summarise();
    }

    function setBusy(isBusy) {
        running = isBusy;
        btnRun.disabled = isBusy;
        btnRefresh.disabled = isBusy;
        btnCancel.disabled = !isBusy;
        btnRun.textContent = isBusy ? 'Testing…' : 'Test all models';
    }

    // ── Environment strip ────────────────────────────────────────────────────

    async function probeEnvironment() {
        const parts = [];
        try {
            const gpu = await window.checkWebGpu();
            if (gpu.supported) {
                const device = [gpu.vendor, gpu.architecture].filter(Boolean).join(' · ') || 'device';
                parts.push('<span class="mh-env__item mh-env__item--ok">WebGPU ✓</span>');
                parts.push('<span class="mh-env__item mh-env__item--muted">' + esc(device) + '</span>');
            } else {
                parts.push('<span class="mh-env__item mh-env__item--bad">WebGPU ✗</span>');
                parts.push('<span class="mh-env__item mh-env__item--muted">' + esc(gpu.reason) + '</span>');
            }
        } catch (e) {
            parts.push('<span class="mh-env__item mh-env__item--bad">WebGPU probe failed</span>');
        }

        try {
            const nn = await window.checkWebNn();
            parts.push(nn.supported
                ? '<span class="mh-env__item mh-env__item--ok">NPU (' + esc(nn.deviceType.toUpperCase()) + ')</span>'
                : '<span class="mh-env__item mh-env__item--muted">NPU: not detected</span>');
        } catch (e) {
            // WebNN is optional on every platform; absence is not a fault.
        }

        parts.push('<span class="mh-env__item">' + rows.length + ' registered</span>');
        const counts = {};
        rows.forEach((r) => {
            const key = TYPE_LABEL[r.model.modelType] || 'other';
            counts[key] = (counts[key] || 0) + 1;
        });
        Object.keys(counts).forEach((key) => {
            parts.push('<span class="mh-env__item mh-env__item--muted">' + counts[key] + ' ' + esc(key) + '</span>');
        });

        elEnv.innerHTML = parts.join('');
    }

    // ── Catalog ──────────────────────────────────────────────────────────────

    async function loadCatalog() {
        elRows.innerHTML = '<tr><td colspan="6">Loading catalog…</td></tr>';
        try {
            const response = await fetch('/api/models', { credentials: 'include', cache: 'no-store' });
            if (response.status === 401) {
                // /diag is anonymous but /api/models is not, and that asymmetry is deliberate —
                // say so rather than showing an empty table that reads as "no models".
                elRows.innerHTML = '<tr><td colspan="6">Not signed in — the model catalog needs a session. '
                    + '<a class="diag-header__back" href="/">Sign in</a>, then reload this page.</td></tr>';
                return;
            }
            if (!response.ok) throw new Error('HTTP ' + response.status);

            const models = await response.json();
            rows = models.map((m) => ({ model: m, status: 'idle', loadMs: null, tps: null, note: '', assetSource: null }));
            render();
            summarise();
            await probeEnvironment();
        } catch (e) {
            elRows.innerHTML = '<tr><td colspan="6">Could not load the catalog — ' + esc(e.message) + '</td></tr>';
        }
    }

    // ── Per-type test runners ────────────────────────────────────────────────

    async function testRemoteModels(targets) {
        if (targets.length === 0) return;
        targets.forEach((r) => setRow(r.model.modelId, { status: 'running', note: 'probing…' }));

        try {
            const response = await fetch('/api/models/availability', { credentials: 'include', cache: 'no-store' });
            if (!response.ok) throw new Error('HTTP ' + response.status);
            const availability = await response.json();

            targets.forEach((r) => {
                const entry = availability.find((a) => a.modelId === r.model.modelId);
                setRow(r.model.modelId, entry && entry.isAvailable
                    ? { status: 'ok', note: entry.servedModelMismatch
                        ? 'answered, but served by ' + entry.servedModelMismatch + ' — not the model this entry is named for'
                        : 'inference probe returned' }
                    : { status: 'failed', note: (entry && entry.reason) || 'deployment unreachable' });
            });
        } catch (e) {
            targets.forEach((r) => setRow(r.model.modelId, { status: 'failed', note: e.message }));
        }
    }

    async function testOllamaModel(row, prompt) {
        setRow(row.model.modelId, { status: 'running', note: 'running on the daemon…' });
        try {
            const response = await fetch('/api/ollama/benchmark', {
                method: 'POST',
                credentials: 'include',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ modelName: row.model.apiEndpointRef, prompt: prompt }),
            });
            if (!response.ok) throw new Error('HTTP ' + response.status);

            const b = await response.json();
            setRow(row.model.modelId, b.isFailure
                ? { status: 'failed', note: b.failureReason || 'Ollama reported a failure' }
                : { status: 'ok', loadMs: b.loadMs, tps: b.tokensPerSec, note: b.totalTokens + ' tokens' });
        } catch (e) {
            setRow(row.model.modelId, { status: 'failed', note: e.message });
        }
    }

    async function resolveAssets(row) {
        setRow(row.model.modelId, { status: 'running', note: 'locating weights…' });
        try {
            const found = await window.checkModelFile(row.model.webLlmModelId, cdnTemplates);
            row.assetSource = found && found.available ? (found.source || 'cdn') : null;
        } catch (e) {
            row.assetSource = null;
        }
    }

    /**
     * Runs one browser model through the WebLLM worker. `runModelDiag` was written to call
     * back into Blazor, so it is handed a duck-typed stand-in for the .NET object reference
     * rather than being forked — the Arena and this page then stay on one code path.
     */
    function testBrowserModel(row, prompt) {
        return new Promise((resolve) => {
            const id = row.model.modelId;
            setRow(id, { status: 'running', note: 'starting worker…' });

            const dotnetShim = {
                invokeMethodAsync: function (method, diagId) {
                    if (diagId !== id) return Promise.resolve();
                    const args = Array.prototype.slice.call(arguments, 2);

                    if (method === 'OnDiagStep') {
                        const step = args[0];
                        const detail = args[1];
                        setRow(id, { status: 'running', note: (step + ': ' + (detail || '')).trim() });
                    } else if (method === 'OnDiagResult') {
                        const loadMs = args[0];
                        const tps = args[2];
                        const totalTokens = args[3];
                        const error = args[5];
                        const warmCache = args[6];
                        setRow(id, error
                            ? {
                                status: 'failed', loadMs: null, tps: null,
                                note: error + (row.assetSource ? '' : ' (weights not found locally or on the CDN)'),
                            }
                            : {
                                status: 'ok',
                                loadMs: loadMs,
                                tps: tps,
                                note: totalTokens + ' tokens'
                                    + (row.assetSource ? ' · ' + row.assetSource : '')
                                    + (warmCache ? ' · warm cache' : ''),
                            });
                        resolve();
                    }
                    return Promise.resolve();
                },
            };

            Promise.resolve(window.runModelDiag(dotnetShim, id, row.model.webLlmModelId, prompt, cdnTemplates))
                .catch((e) => {
                    setRow(id, { status: 'failed', note: e.message || 'worker failed to start' });
                    resolve();
                });
        });
    }

    // ── Test all ─────────────────────────────────────────────────────────────

    async function testAll() {
        if (running || rows.length === 0) return;
        cancelled = false;
        setBusy(true);

        const prompt = inpPrompt.value.trim() || 'In one sentence, explain what an API is.';
        rows.forEach((r) => Object.assign(r, { status: 'idle', loadMs: null, tps: null, note: '', assetSource: null }));
        render();

        try {
            const remote = rows.filter((r) => r.model.modelType === 'Remote');
            const ollama = rows.filter((r) => r.model.modelType === 'LocalService');
            const browser = rows.filter((r) => r.model.modelType === 'Local');

            // Remote first: one request covers the whole set and it is the fastest to come
            // back, so the table has something in it before the slow GPU work starts.
            await testRemoteModels(remote);

            for (const row of ollama) {
                if (cancelled) break;
                await testOllamaModel(row, prompt);
            }

            for (const row of browser) {
                if (cancelled) break;
                if (!row.model.webLlmModelId) {
                    setRow(row.model.modelId, { status: 'skipped', note: 'no WebLLM model id' });
                    continue;
                }
                // Where the weights are coming from is the most useful thing to know when one
                // of these fails, and it is cheap — a 200 from the server for local, one HEAD
                // for the CDN. Reported either way; a CDN miss still gets attempted, because
                // the worker resolves independently and may succeed where the probe did not.
                await resolveAssets(row);
                await testBrowserModel(row, prompt);
            }

            if (cancelled) {
                rows.filter((r) => r.status === 'running' || r.status === 'idle')
                    .forEach((r) => setRow(r.model.modelId, { status: 'skipped', note: 'cancelled' }));
            }
        } finally {
            setBusy(false);
        }
    }

    function cancelAll() {
        cancelled = true;
        try {
            window.cancelAllModelDiags();
        } catch (e) {
            // Nothing running; the flag above is what stops the loop.
        }
        setBusy(false);
    }

    // ── Wire-up ──────────────────────────────────────────────────────────────

    btnRun.addEventListener('click', testAll);
    btnCancel.addEventListener('click', cancelAll);
    btnRefresh.addEventListener('click', () => { if (!running) loadCatalog(); });

    (async () => {
        // The CDN templates are the client's config, not the API's, so they are read from the
        // same file the WASM app reads rather than duplicated into this page.
        try {
            const response = await fetch('/appsettings.json', { cache: 'no-store' });
            const cfg = await response.json();
            const bm = (cfg && cfg.BrowserModels) || {};
            cdnTemplates = [bm.PrimaryCdnBaseUrlTemplate, bm.CdnBaseUrlTemplate, bm.BackupCdnBaseUrlTemplate]
                .filter(Boolean)
                .filter((v, i, a) => a.indexOf(v) === i);
        } catch (e) {
            // The worker falls back to its own default CDN resolution.
        }
        await loadCatalog();
    })();
})();
