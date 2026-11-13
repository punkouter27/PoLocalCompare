/**
 * T050 — WebLLM Web Worker
 * Loaded by webllm-interop.js via new Worker(). Receives {modelId, prompt} via postMessage.
 * Emits typed messages back to the main thread.
 */

import * as webllm from '/js/web-llm.js';

let engine = null;
let cachedModelId = null;

/**
 * Tears down the live engine and drops the cache marker.
 * Safe to call when nothing is loaded. Failures are swallowed: a device that is already
 * lost throws on unload, and we still need the references cleared so the next run starts
 * from a clean slate rather than reusing a dead GPU context.
 */
async function releaseEngine(why) {
    if (!engine) return;
    try {
        console.log(`[WebLLM Worker] Releasing engine for "${cachedModelId}" — ${why}`);
        await engine.unload();
    } catch (err) {
        console.warn('[WebLLM Worker] Engine unload failed (device may already be lost):', err);
    } finally {
        engine = null;
        cachedModelId = null;
    }
}

// ---------------------------------------------------------------------------
// HTML stats helpers
// ---------------------------------------------------------------------------
// Scans with bounded lookahead only. Slicing the remainder of the document at every
// '<' made this O(n²) — on the same worker thread that drives WebGPU inference, and
// re-run every 500ms over the whole accumulated output.
function computeHtmlStats(text) {
    let tagCount = 0, depth = 0, styleRules = 0;
    let inStyle = false;
    for (let i = 0; i < text.length; i++) {
        if (text[i] === '<') {
            const next = text[i + 1];
            if (next === '/') {
                depth--;
                if (text.slice(i + 1, i + 7).toLowerCase() === '/style') inStyle = false;
            } else if (next !== '!') {
                tagCount++;
                depth++;
                if (text.slice(i + 1, i + 6).toLowerCase() === 'style') inStyle = true;
            }
        } else if (inStyle && text[i] === '{') {
            styleRules++;
        }
    }
    return { tagCount, openDepth: Math.max(0, depth), styleRules };
}

function computeRepetitionScore(text) {
    const words = text.trim().split(/\s+/);
    if (words.length < 10) return 0;
    const n = 5;
    const seen = new Set();
    let duplicates = 0;
    for (let i = 0; i <= words.length - n; i++) {
        const gram = words.slice(i, i + n).join(' ');
        if (seen.has(gram)) duplicates++;
        else seen.add(gram);
    }
    const total = words.length - n + 1;
    return total > 0 ? Math.round((duplicates / total) * 100) / 100 : 0;
}

// ---------------------------------------------------------------------------
// Error classification — turn cryptic WebGPU/Dawn messages into a clear,
// actionable reason plus the phase it happened in, so failures are legible.
// ---------------------------------------------------------------------------
function classifyWebLlmError(rawMessage, phase) {
    const m = (rawMessage || '').toLowerCase();
    const where = phase === 'load' ? ' while loading the model'
                : phase === 'generate' ? ' during generation'
                : '';
    if (m.includes('external instance reference') || m.includes('device was lost') || m.includes('device is lost') || m.includes('device lost')) {
        return `GPU device was lost${where}. This usually means the GPU ran out of memory or a previous model's GPU context is still held. Hard-refresh (Ctrl+Shift+R) to clear WebGPU state, run one model at a time, and try a smaller model if it recurs.`;
    }
    if (m.includes('out of memory') || m.includes('oom') || m.includes('vram') || m.includes('failed to allocate') || m.includes('allocation')) {
        return `Ran out of GPU memory${where}. This model needs more VRAM than is available — pick a smaller model, or close other GPU-heavy tabs and retry.`;
    }
    if (m.includes('requestadapter') || m.includes('no adapter') || m.includes('adapter is null') || m.includes('gpu adapter') || (m.includes('webgpu') && m.includes('not'))) {
        return `WebGPU is not available in this browser${where}. Use desktop Chrome or Edge with hardware acceleration enabled.`;
    }
    if (m.includes('shader-f16') || m.includes('shader f16') || m.includes('f16')) {
        return `Your GPU lacks the shader-f16 feature this model requires${where}. Try a different device or browser.`;
    }
    if (m.includes('importscripts') || m.includes('failed to fetch') || m.includes('err_') || m.includes('networkerror') || m.includes('load failed')) {
        return `Could not download the model files${where}. Models stream from HuggingFace/CDN on first run — check your internet connection and retry.`;
    }
    if (m.includes('shader') || m.includes('compil')) {
        return `GPU shader compilation failed${where}. Your GPU or driver may not support the features this model needs.`;
    }
    if (m.includes('model_lib') || m.includes('model lib') || m.includes('cannot find model')) {
        return `This model isn't supported by the in-browser runner (missing model library)${where}.`;
    }
    return `${rawMessage || 'Unknown WebLLM error'}${where}.`;
}

/**
 * Resolves the WebGPU library URL for a model, preferring a locally served copy.
 *
 * WebLLM's prebuilt config hard-codes model_lib to raw.githubusercontent.com. Hosts
 * whose egress is filtered can serve the same ~5 MB .wasm from wwwroot/models/_libs/
 * instead; this returns that URL when it resolves and the upstream one otherwise.
 */
async function resolveModelLib(cdnModelLib, modelBaseUrl) {
    if (!cdnModelLib || !modelBaseUrl) return cdnModelLib;
    try {
        const libName = cdnModelLib.split('/').pop();
        // modelBaseUrl always carries a trailing slash (normalizeModelBaseUrl in
        // webllm-interop.js), so '../_libs/' resolves to the models root beside it.
        const base = modelBaseUrl.endsWith('/') ? modelBaseUrl : `${modelBaseUrl}/`;
        const localLib = new URL(`../_libs/${libName}`, new URL(base, self.location.href)).href;
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 3500);
        try {
            const response = await fetch(localLib, { method: 'HEAD', signal: controller.signal, cache: 'no-store' });
            if (response.ok) {
                console.log(`[WebLLM Worker] Using vendored model_lib: ${localLib}`);
                return localLib;
            }
        } finally {
            clearTimeout(timeout);
        }
    } catch (err) {
        console.warn(`[WebLLM Worker] Vendored model_lib probe failed, using CDN: ${err?.message ?? err}`);
    }
    return cdnModelLib;
}

// ---------------------------------------------------------------------------
// Main message handler
// ---------------------------------------------------------------------------
self.onmessage = async (event) => {
    const { modelId, webLlmModelId: wlmId, prompt, localModelBaseUrl } = event.data;
    // wlmId is the actual WebLLM model identifier (e.g. "Phi-3.5-mini-instruct-q4f32_1-MLC");
    // modelId is the internal ULID used only for routing status callbacks back to Blazor.
    const effectiveModelId = wlmId || modelId;
    let phase = 'init';

    console.log(`[WebLLM Worker] ▶ Start inference — modelId="${modelId}" effectiveModelId="${effectiveModelId}" localModelBaseUrl="${localModelBaseUrl}"`);

    // Log the GPU environment up-front so device-loss / OOM failures are diagnosable.
    try {
        if (typeof navigator !== 'undefined' && navigator.gpu) {
            const ad = await navigator.gpu.requestAdapter();
            if (ad) {
                let info = {};
                try { info = ad.info || (ad.requestAdapterInfo ? await ad.requestAdapterInfo() : {}); } catch (_) { /* ignore */ }
                const lim = ad.limits || {};
                console.log(`[WebLLM Worker] 🖥 GPU adapter — vendor="${info.vendor || '?'}" arch="${info.architecture || '?'}" device="${info.device || '?'}" ` +
                    `maxBufferSize=${lim.maxBufferSize ?? '?'} maxStorageBufferBindingSize=${lim.maxStorageBufferBindingSize ?? '?'} ` +
                    `features=[${ad.features ? [...ad.features].join(', ') : ''}]`);
            } else {
                console.warn('[WebLLM Worker] ⚠ navigator.gpu.requestAdapter() returned no adapter.');
            }
        } else {
            console.warn('[WebLLM Worker] ⚠ navigator.gpu not available in this worker context.');
        }
    } catch (probeErr) {
        console.warn(`[WebLLM Worker] ⚠ GPU probe failed: ${probeErr?.message ?? probeErr}`);
    }

    const startMs = performance.now();

    try {
        phase = 'load';
        self.postMessage({ type: 'status', status: 'Initializing', tokenCount: 0, elapsedMs: 0, loadProgress: 0 });

        const isAlreadyCached = cachedModelId === effectiveModelId;
        console.log(`[WebLLM Worker] Cache check — cachedModelId="${cachedModelId}" isAlreadyCached=${isAlreadyCached}`);

        if (!engine || !isAlreadyCached) {
            // Release the previous model's GPU memory FIRST. Reassigning `engine` does not
            // free anything — the old MLCEngine keeps its WebGPU device and multi-GB weight
            // buffers alive, so switching models used to stack VRAM until an allocation
            // failed with "device was lost while loading".
            await releaseEngine('switching model');

            // Look up model_lib (WASM URL) from prebuilt config so local models get the correct WASM library
            const prebuiltEntry = webllm.prebuiltAppConfig?.model_list?.find(m => m.model_id === effectiveModelId);
            // Serving weights from wwwroot/models is not enough on a locked-down network:
            // prebuiltAppConfig points model_lib at raw.githubusercontent.com, a separate
            // host from huggingface.co that a proxy can block on its own. Prefer a vendored
            // copy at models/_libs/<name>.wasm (installed by scripts/download-models.py,
            // the only vendoring path this repo has)
            // and fall back to the upstream URL when it is absent. The probe also fails
            // harmlessly when localModelBaseUrl is a CDN URL, which keeps the CDN path intact.
            const modelLib = await resolveModelLib(prebuiltEntry?.model_lib, localModelBaseUrl);
            // localModelBaseUrl already points at this model's root (resolveBrowserModelAvailability
            // probed <baseUrl>mlc-chat-config.json) — do not append the model id again.
            const appConfig = localModelBaseUrl
                ? { model_list: [{ model: localModelBaseUrl, model_id: effectiveModelId, model_lib: modelLib }] }
                : undefined;

            console.log(`[WebLLM Worker] Creating MLCEngine — effectiveModelId="${effectiveModelId}" appConfig=`, appConfig ?? '(none, using CDN)');

            engine = await webllm.CreateMLCEngine(effectiveModelId, {
                ...(appConfig ? { appConfig } : {}),
                initProgressCallback: (progress) => {
                    const elapsedMs = Math.round(performance.now() - startMs);
                    const loadProgress = Math.round((progress.progress ?? 0) * 100);
                    console.log(`[WebLLM Worker] Load progress ${loadProgress}% (${elapsedMs}ms) — ${progress.text}`);
                    self.postMessage({
                        type: 'status',
                        status: 'Initializing',
                        tokenCount: 0,
                        elapsedMs,
                        detail: progress.text,
                        loadProgress,
                    });
                },
            });
            cachedModelId = effectiveModelId;
            console.log(`[WebLLM Worker] ✅ Engine ready — model="${effectiveModelId}" warmUpMs=${Math.round(performance.now() - startMs)}`);
        } else {
            console.log(`[WebLLM Worker] ⚡ Using cached engine for "${effectiveModelId}"`);
        }

        const warmUpMs = Math.round(performance.now() - startMs);
        const cacheHit = isAlreadyCached; // weights were already in memory

        // Parse prefill speed from runtimeStatsText if available
        let prefillSpeedTps = null;
        try {
            const statsText = await engine.runtimeStatsText();
            // Format: "prefill: 123.4 tok/s, decoding: 56.7 tok/s"
            const prefillMatch = statsText.match(/prefill[:\s]+([\d.]+)\s*tok\/s/i);
            if (prefillMatch) prefillSpeedTps = parseFloat(prefillMatch[1]);
        } catch (_) { /* runtimeStatsText not always available pre-generation */ }

        console.log(`[WebLLM Worker] 🚀 Generating — warmUpMs=${warmUpMs} cacheHit=${cacheHit} prefillSpeedTps=${prefillSpeedTps}`);
        self.postMessage({
            type: 'status', status: 'Generating', tokenCount: 0, elapsedMs: warmUpMs,
            cacheHit, prefillSpeedTps,
        });

        phase = 'generate';
        let tokenCount = 0;
        let generatedContent = '';
        let lastStatusMs = 0;
        // Rolling window of last 200 chars for repetition detection
        const repWindow = [];
        const REP_WINDOW_CHARS = 200;
        const PREVIEW_MAX_CHARS = 5000;

        // Must stay identical to InferencePrompt.System on the server (see
        // Api/Common/Inference/InferencePrompt.cs) — a duel is only fair if all three runners
        // brief the model the same way. Bump this worker's ?v= in its callers when it changes.
        // The HTML-forcing half is load-bearing for small local models, which otherwise answer
        // conversationally ("I'm sorry, but as an AI…") instead of emitting a page.
        const stream = await engine.chat.completions.create({
            messages: [
                { role: 'system', content: [
                    'You are an expert HTML/CSS coder. Return only valid HTML5 with inline CSS.',
                    'No markdown, no explanation, no code fences.',
                    '',
                    'The page is displayed in a fixed 320x180 pixel frame that cannot scroll.',
                    'Design for exactly that canvas:',
                    '- Start the stylesheet with html,body{margin:0;padding:0;width:100%;height:100%;overflow:hidden}',
                    '- Every element must fit inside 320x180. Nothing may overflow in either',
                    '  direction, and no scrollbar may appear.',
                    '- Scale type and spacing to suit it: small font sizes, tight padding, no large',
                    '  fixed widths or heights, and no min-width or min-height that exceeds the frame.',
                    '- Prefer a single screen of content. Drop anything that will not fit rather than',
                    '  letting it spill.',
                    '',
                    'Behaviour belongs in the page too:',
                    '- Use an inline <script> for anything the request needs to move, update or respond.',
                    '- No external files of any kind (scripts, stylesheets, fonts, images, CDNs). The page',
                    '  runs with no network, so anything it has to fetch will be missing.',
                    '- Anything that moves must start by itself when the page loads; do not wait for a',
                    '  click or a key press to begin.',
                    '- Visible controls may be activated during judging. Requested interactions must work',
                    '  immediately and provide a visible result.',
                ].join('\n') },
                { role: 'user', content: prompt },
            ],
            stream: true,
            max_tokens: 4096,
            // 0.2, matching FoundryInferenceProxy and OllamaInferenceProxy. Without it WebLLM
            // uses each model's own config default (often 0.6–1.0), so a browser model was
            // sampling at a different temperature from its remote opponent in the same duel.
            temperature: 0.2,
            // Qwen3 thinks by default — hundreds of tokens on the GPU being timed before the
            // first character of HTML. WebLLM implements enable_thinking=false by prefilling an
            // empty <think></think> block for ANY model, so send it only to Qwen3; on a model
            // without a thinking template that block is just noise in its reply.
            ...(/^qwen3/i.test(effectiveModelId) ? { extra_body: { enable_thinking: false } } : {}),
        });

        for await (const chunk of stream) {
            const delta = chunk.choices[0]?.delta?.content ?? '';
            if (delta) {
                generatedContent += delta;
                tokenCount++;
                const elapsedMs = Math.round(performance.now() - startMs);

                // Emit status every 500ms
                if (elapsedMs - lastStatusMs >= 500) {
                    lastStatusMs = elapsedMs;

                    const htmlStats = computeHtmlStats(generatedContent);

                    // Repetition on last ~200 chars
                    const repSample = generatedContent.slice(-REP_WINDOW_CHARS * 4);
                    const repetitionScore = computeRepetitionScore(repSample);

                    self.postMessage({
                        type: 'status', status: 'Generating',
                        tokenCount, elapsedMs,
                        htmlTagCount: htmlStats.tagCount,
                        openTagDepth: htmlStats.openDepth,
                        styleRuleCount: htmlStats.styleRules,
                        repetitionScore,
                        cacheHit,
                        prefillSpeedTps,
                        // Preview only — the pane renders a screenful. Shipping the whole
                        // document twice a second structured-clones it across the worker
                        // boundary and marshals it through JS interop. Matches the 5000-char
                        // cap the server-side proxies use.
                        htmlPreview: generatedContent.slice(0, PREVIEW_MAX_CHARS),
                    });
                }
            }
        }

        // Final prefill stats after generation
        try {
            const statsText = await engine.runtimeStatsText();
            const prefillMatch = statsText.match(/prefill[:\s]+([\d.]+)\s*tok\/s/i);
            if (prefillMatch) prefillSpeedTps = parseFloat(prefillMatch[1]);
        } catch (_) { }

        const totalMs = Math.round(performance.now() - startMs);
        console.log(`[WebLLM Worker] ✅ Complete — tokenCount=${tokenCount} totalMs=${totalMs} warmUpMs=${warmUpMs} prefillSpeedTps=${prefillSpeedTps}`);
        self.postMessage({
            type: 'complete',
            htmlOutput: generatedContent,
            tokenCount,
            totalMs,
            warmUpMs,
            prefillSpeedTps,
            cacheHit,
        });
    } catch (err) {
        const raw = err?.message ?? String(err);
        const friendly = classifyWebLlmError(raw, phase);
        const elapsedMs = Math.round(performance.now() - startMs);
        // Full context to the dev console…
        console.error(
            `[WebLLM Worker] ❌ FAILED (phase="${phase}") — model="${effectiveModelId}" ` +
            `cachedModel="${cachedModelId}" elapsedMs=${elapsedMs}\n` +
            `  reason: ${friendly}\n  raw: ${raw}`,
            err);
        // …and a clear, actionable reason to the UI, with the raw string preserved for
        // the "Show technical details" expander.
        self.postMessage({
            type: 'error',
            reason: `${friendly}\n\n[technical] phase=${phase} · model=${effectiveModelId} · ${raw}`,
            rawError: raw,
            phase,
        });

        // A failed load can leave `engine` pointing at the PREVIOUS model (the assignment
        // never happened), and a device lost mid-generation leaves a dead engine whose
        // cache marker still matches. Either way the next run must not reuse it.
        await releaseEngine(`run failed in phase=${phase}`);
    }
};
