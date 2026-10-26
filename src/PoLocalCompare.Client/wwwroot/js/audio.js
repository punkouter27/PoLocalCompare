/**
 * audio.js — synthesised Web Audio for the duel's moments: the versus slam, each model's
 * signature motif, the race heard as two panned grain streams, the first-token spark, the
 * judge's deliberation, the verdict impact, the ELO odometer, the upset stinger and the
 * champion's fanfare. Plus the original UI tick and the judge window's countdown.
 *
 * Every sound is SYNTHESISED at play time. There are no audio assets, deliberately: an earlier
 * version fetched two WAV files that were 44-byte stubs and played silence for as long as they
 * existed. Synthesis means there is nothing that can be present-but-empty. Noise and plucked
 * strings are computed into buffers here, not fetched.
 *
 * It runs on the browser's audio thread, so it never contends with the WebGPU device WebLLM
 * is running inference on and cannot skew the tok/s figure. The race scheduler is the one
 * main-thread piece: a 60 ms timer that queues the next 150 ms of grains.
 *
 * (The 2026-09-26 prune cut an earlier drone, token melodies and fanfare as decoration nobody
 * asked for. These were asked for on 2026-10-26 — HISTORY.md, "G1–G10".)
 */

let ctx = null;
let master = null;
let muted = false;

const MUTE_KEY = 'polocalcompare.muted';

/** Ceiling on the master bus. Cues are mixed well below unity so two overlapping never clip. */
const MASTER_GAIN = 0.28;

/**
 * Browsers refuse to start an AudioContext outside a user gesture, and a context created
 * before one starts 'suspended'. Every cue routes through here, so the first sound that
 * follows a click resumes it rather than silently doing nothing.
 */
function ensureCtx() {
    if (ctx) {
        if (ctx.state === 'suspended') ctx.resume().catch(() => { });
        return ctx;
    }

    try {
        const Ctor = window.AudioContext || window.webkitAudioContext;
        if (!Ctor) return null;
        ctx = new Ctor();
        master = ctx.createGain();
        master.gain.value = muted ? 0 : MASTER_GAIN;
        // A gentle limiter on the bus: the verdict stacks a thump, a motif and odometer clicks.
        const limiter = ctx.createDynamicsCompressor();
        limiter.threshold.value = -10;
        limiter.ratio.value = 8;
        master.connect(limiter);
        limiter.connect(ctx.destination);
    } catch {
        ctx = null;
        master = null;
    }
    return ctx;
}

try {
    muted = window.localStorage.getItem(MUTE_KEY) === 'true';
} catch {
    // Storage blocked (private mode). Default to audible.
}

export function isMuted() {
    return muted;
}

export function setMuted(value) {
    muted = !!value;
    try {
        window.localStorage.setItem(MUTE_KEY, muted ? 'true' : 'false');
    } catch {
        // Non-fatal: the setting simply will not survive a reload.
    }
    if (master && ctx) master.gain.setTargetAtTime(muted ? 0 : MASTER_GAIN, ctx.currentTime, 0.01);
    return muted;
}

// ── Building blocks ──────────────────────────────────────────────────────────

/** A stereo position, or the master bus itself for a centred sound. Released with the source. */
function output(audio, pan) {
    if (!pan || !audio.createStereoPanner) return { node: master, release: () => { } };
    const panner = audio.createStereoPanner();
    panner.pan.value = Math.max(-1, Math.min(1, pan));
    panner.connect(master);
    return { node: panner, release: () => { try { panner.disconnect(); } catch { } } };
}

/**
 * One shaped note. setTargetAtTime for the tail so the decay sounds exponential — a linear
 * fade reads as a synthetic "cut" rather than a note ending. `glide` bends the pitch to that
 * frequency over the note.
 */
function tone({ freq, type = 'sine', at = 0, dur = 0.3, gain = 0.5, pan = 0, glide = 0 }) {
    const audio = ensureCtx();
    if (!audio || !master) return;

    const out = output(audio, pan);
    const t0 = audio.currentTime + at;
    const osc = audio.createOscillator();
    const amp = audio.createGain();

    osc.type = type;
    osc.frequency.setValueAtTime(freq, t0);
    if (glide) osc.frequency.exponentialRampToValueAtTime(glide, t0 + dur);

    // 8ms attack: fast enough to feel instant, slow enough to avoid a click transient.
    amp.gain.setValueAtTime(0.0001, t0);
    amp.gain.exponentialRampToValueAtTime(gain, t0 + 0.008);
    amp.gain.setTargetAtTime(0.0001, t0 + 0.008, dur / 3);

    osc.connect(amp);
    amp.connect(out.node);
    osc.start(t0);
    osc.stop(t0 + dur + 0.1);
    // Oscillators are one-shot; releasing the graph keeps a long session from accumulating nodes.
    osc.onended = () => { try { amp.disconnect(); } catch { } out.release(); };
}

let noise = null;

/** One second of white noise, computed once and reused by every whoosh, click and crowd. */
function noiseBuffer(audio) {
    if (noise) return noise;
    noise = audio.createBuffer(1, audio.sampleRate, audio.sampleRate);
    const data = noise.getChannelData(0);
    for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
    return noise;
}

/**
 * Filtered noise with an envelope — the whoosh, the impact's click and the crowd. `sweepTo`
 * moves the band-pass centre across the burst, which is what makes noise read as motion.
 */
function noiseBurst({ at = 0, dur = 0.3, gain = 0.2, freq = 1000, sweepTo = 0, q = 1, type = 'bandpass', attack = 0.01, pan = 0 }) {
    const audio = ensureCtx();
    if (!audio || !master) return;

    const out = output(audio, pan);
    const t0 = audio.currentTime + at;
    const src = audio.createBufferSource();
    src.buffer = noiseBuffer(audio);
    src.loop = true;

    const filter = audio.createBiquadFilter();
    filter.type = type;
    filter.Q.value = q;
    filter.frequency.setValueAtTime(freq, t0);
    if (sweepTo) filter.frequency.exponentialRampToValueAtTime(sweepTo, t0 + dur);

    const amp = audio.createGain();
    amp.gain.setValueAtTime(0.0001, t0);
    amp.gain.exponentialRampToValueAtTime(gain, t0 + attack);
    amp.gain.setTargetAtTime(0.0001, t0 + attack, Math.max(0.01, (dur - attack) / 3));

    src.connect(filter);
    filter.connect(amp);
    amp.connect(out.node);
    src.start(t0, Math.random() * 0.5);
    src.stop(t0 + dur + 0.2);
    src.onended = () => { try { filter.disconnect(); amp.disconnect(); } catch { } out.release(); };
}

const plucks = new Map();

/**
 * A plucked string by Karplus–Strong: a noise burst circulating through an averaging delay
 * line, computed into a buffer rather than built from a DelayNode loop — a feedback cycle in
 * Web Audio costs a 128-sample block of latency, which caps the pitch at ~375 Hz.
 */
function pluckBuffer(audio, freq) {
    const key = Math.round(freq);
    if (plucks.has(key)) return plucks.get(key);

    const rate = audio.sampleRate;
    const length = Math.floor(rate * 0.9);
    const period = Math.max(2, Math.round(rate / freq));
    const buffer = audio.createBuffer(1, length, rate);
    const data = buffer.getChannelData(0);
    const ring = new Float32Array(period);
    for (let i = 0; i < period; i++) ring[i] = Math.random() * 2 - 1;

    let index = 0;
    for (let i = 0; i < length; i++) {
        const next = (index + 1) % period;
        const sample = ring[index];
        ring[index] = 0.996 * 0.5 * (sample + ring[next]);
        data[i] = sample;
        index = next;
    }
    plucks.set(key, buffer);
    return buffer;
}

function pluck({ freq, at = 0, gain = 0.4, pan = 0 }) {
    const audio = ensureCtx();
    if (!audio || !master) return;

    const out = output(audio, pan);
    const src = audio.createBufferSource();
    src.buffer = pluckBuffer(audio, freq);
    const amp = audio.createGain();
    amp.gain.value = gain;
    src.connect(amp);
    amp.connect(out.node);
    src.start(audio.currentTime + at);
    src.onended = () => { try { amp.disconnect(); } catch { } out.release(); };
}

/** A bell by two-operator FM. The 1.4 ratio is inharmonic on purpose: that is what rings. */
function bell({ freq, at = 0, dur = 0.9, gain = 0.3, pan = 0 }) {
    const audio = ensureCtx();
    if (!audio || !master) return;

    const out = output(audio, pan);
    const t0 = audio.currentTime + at;
    const carrier = audio.createOscillator();
    const modulator = audio.createOscillator();
    const depth = audio.createGain();
    const amp = audio.createGain();

    carrier.frequency.value = freq;
    modulator.frequency.value = freq * 1.4;
    // The brightness decays faster than the note — a struck bell, not an organ.
    depth.gain.setValueAtTime(freq * 2.5, t0);
    depth.gain.setTargetAtTime(0, t0, dur / 5);
    amp.gain.setValueAtTime(0.0001, t0);
    amp.gain.exponentialRampToValueAtTime(gain, t0 + 0.004);
    amp.gain.setTargetAtTime(0.0001, t0 + 0.004, dur / 3);

    modulator.connect(depth);
    depth.connect(carrier.frequency);
    carrier.connect(amp);
    amp.connect(out.node);
    modulator.start(t0);
    carrier.start(t0);
    modulator.stop(t0 + dur + 0.1);
    carrier.stop(t0 + dur + 0.1);
    carrier.onended = () => { try { depth.disconnect(); amp.disconnect(); } catch { } out.release(); };
}

/** A brass-like voice: a sawtooth behind a low-pass that opens with the attack. */
function brass({ freq, at = 0, dur = 0.4, gain = 0.12, pan = 0 }) {
    const audio = ensureCtx();
    if (!audio || !master) return;

    const out = output(audio, pan);
    const t0 = audio.currentTime + at;
    const osc = audio.createOscillator();
    const filter = audio.createBiquadFilter();
    const amp = audio.createGain();

    osc.type = 'sawtooth';
    osc.frequency.value = freq;
    filter.type = 'lowpass';
    filter.Q.value = 2;
    filter.frequency.setValueAtTime(freq * 1.5, t0);
    filter.frequency.exponentialRampToValueAtTime(freq * 6, t0 + 0.06);
    filter.frequency.setTargetAtTime(freq * 2.5, t0 + 0.06, dur / 2);
    amp.gain.setValueAtTime(0.0001, t0);
    amp.gain.exponentialRampToValueAtTime(gain, t0 + 0.03);
    amp.gain.setValueAtTime(gain, t0 + Math.max(0.03, dur - 0.08));
    amp.gain.setTargetAtTime(0.0001, t0 + Math.max(0.03, dur - 0.08), 0.06);

    osc.connect(filter);
    filter.connect(amp);
    amp.connect(out.node);
    osc.start(t0);
    osc.stop(t0 + dur + 0.4);
    osc.onended = () => { try { filter.disconnect(); amp.disconnect(); } catch { } out.release(); };
}

/** The impact under a slam or a verdict: a falling sub-bass thump and a short noise click. */
function thump(at = 0, gain = 0.6) {
    tone({ freq: 140, type: 'sine', at, dur: 0.32, gain, glide: 42 });
    noiseBurst({ at, dur: 0.05, gain: gain * 0.25, freq: 3200, type: 'highpass', attack: 0.002 });
}

const semitone = n => Math.pow(2, n / 12);

/** Major pentatonic, two octaves: every note of a motif or a race grain is consonant with every other. */
const PENTATONIC = [0, 2, 4, 7, 9, 12, 14, 16, 19, 21];

/** FNV-1a: a stable 32-bit hash of a model id or a tournament id. */
function hash(text) {
    let h = 0x811c9dc5;
    for (let i = 0; i < text.length; i++) {
        h ^= text.charCodeAt(i);
        h = Math.imul(h, 0x01000193);
    }
    return h >>> 0;
}

/** mulberry32 — a seeded generator, so the same id always yields the same motif. */
function seeded(seed) {
    let a = hash(String(seed));
    return () => {
        a = (a + 0x6d2b79f5) >>> 0;
        let t = a;
        t = Math.imul(t ^ (t >>> 15), t | 1);
        t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

// ── G1: the versus slam ──────────────────────────────────────────────────────

/**
 * The Arena opening on a live duel: a rising whoosh, then the thump lands on the frame the
 * VS mark hits its full size (arena__vs-slam in Arena.razor.css peaks at ~560 ms).
 */
export function playVersus() {
    noiseBurst({ at: 0.18, dur: 0.38, gain: 0.22, freq: 280, sweepTo: 3600, q: 1.4, attack: 0.3 });
    thump(0.55, 0.7);
    tone({ freq: 1760, type: 'square', at: 0.55, dur: 0.05, gain: 0.04 });
}

// ── G2: each model's signature ───────────────────────────────────────────────

/**
 * Four notes derived from the model id, voiced by how the model runs: a plucked string for a
 * browser model, an FM bell for a remote one, a chiptune square for Ollama. The same model
 * always sounds the same, which is the point.
 *
 * @param {string} seed  model id
 * @param {'browser'|'remote'|'ollama'} kind
 * @param {number} pan   -1…1
 * @param {number} [at]  start offset, seconds
 * @param {number} [gain]
 */
function motif(seed, kind, pan, at = 0, gain = 1) {
    const rand = seeded(seed);
    const root = kind === 'browser' ? 392 : kind === 'ollama' ? 329.63 : 523.25;
    const steps = [0.09, 0.12, 0.16];

    let t = at;
    for (let i = 0; i < 4; i++) {
        // The last note resolves home (root or its octave), so every motif sounds finished.
        const degree = i === 3 ? (rand() < 0.5 ? 0 : 5) : Math.floor(rand() * PENTATONIC.length);
        const freq = root * semitone(PENTATONIC[degree]);
        if (kind === 'browser') pluck({ freq, at: t, gain: 0.5 * gain, pan });
        else if (kind === 'ollama') tone({ freq, type: 'square', at: t, dur: 0.11, gain: 0.07 * gain, pan });
        else bell({ freq, at: t, dur: 0.7, gain: 0.22 * gain, pan });
        t += steps[Math.floor(rand() * steps.length)];
    }
}

export function playMotif(seed, kind, pan = 0) {
    motif(seed, kind, pan);
}

// ── G3: the race, heard ──────────────────────────────────────────────────────

/**
 * Each side emits short grains panned to its side of the stereo field. Grain rate and pitch
 * both follow that side's tok/s, quantised to the pentatonic scale, so the faster model is
 * audibly busier and higher — you can hear who is ahead without looking.
 */
const race = {
    timer: 0,
    sides: [
        { tps: 0, done: false, next: 0, step: 0 },
        { tps: 0, done: false, next: 0, step: 0 },
    ],
};

function grainPitch(tps, step) {
    // log2 of the rate: 5 tok/s at the bottom of the range, ~300 at the top.
    const position = Math.max(0, Math.min(PENTATONIC.length - 2, Math.round(Math.log2(Math.max(tps, 5) / 5) * 1.6)));
    // Alternates with the note above, so a steady rate is a figure rather than a drone.
    return 330 * semitone(PENTATONIC[position + (step % 2)]);
}

function raceLoop() {
    race.timer = 0;
    const audio = ensureCtx();
    if (!audio) return;

    const horizon = audio.currentTime + 0.15;
    let live = false;
    race.sides.forEach((side, i) => {
        if (side.done || side.tps <= 0) return;
        live = true;
        if (side.next < audio.currentTime) side.next = audio.currentTime + 0.02;
        const interval = 1 / Math.max(2, Math.min(10, side.tps / 5));
        while (side.next < horizon) {
            if (!document.hidden) {
                tone({
                    freq: grainPitch(side.tps, side.step++),
                    type: 'triangle',
                    at: side.next - audio.currentTime,
                    dur: 0.05,
                    gain: 0.05,
                    pan: i === 0 ? -0.75 : 0.75,
                });
            }
            side.next += interval;
        }
    });
    if (live) race.timer = setTimeout(raceLoop, 60);
}

/**
 * The Arena calls this (throttled) as telemetry arrives. A side crossing the line plays its
 * finishing chime once; both done stops the scheduler.
 */
export function raceUpdate(leftTps, rightTps, leftDone, rightDone) {
    const values = [[leftTps, leftDone], [rightTps, rightDone]];
    values.forEach(([tps, done], i) => {
        const side = race.sides[i];
        // Only a side that was audibly racing gets a finish: one that had already finished
        // when the page opened arrives done with no pace behind it.
        if (done && !side.done && side.tps > 0) {
            tone({ freq: 1318.5, type: 'triangle', dur: 0.25, gain: 0.12, pan: i === 0 ? -0.75 : 0.75 });
            tone({ freq: 1975.5, type: 'sine', at: 0.07, dur: 0.3, gain: 0.08, pan: i === 0 ? -0.75 : 0.75 });
        }
        side.done = !!done;
        side.tps = Number(tps) || 0;
    });

    if (race.sides.every(s => s.done)) {
        raceStop();
        return;
    }
    if (!race.timer) raceLoop();
}

export function raceStop() {
    clearTimeout(race.timer);
    race.timer = 0;
    for (const side of race.sides) Object.assign(side, { tps: 0, done: false, next: 0, step: 0 });
}

// ── G4: first token ──────────────────────────────────────────────────────────

/** A side's first token: a quick rising chirp from its side, timed with the panel's light streak. */
export function playSpark(pan = 0) {
    tone({ freq: 880, type: 'sine', dur: 0.14, gain: 0.14, pan, glide: 2640 });
    tone({ freq: 2637, type: 'triangle', at: 0.1, dur: 0.18, gain: 0.05, pan });
}

// ── G5: the judge deliberating ───────────────────────────────────────────────

/**
 * The judge makes two calls with the outputs in opposite orders and they must agree. That is
 * the sound: two voices a few hertz apart, beating against each other while it decides. When
 * the calls agree they slide into unison and the beating stops; a tie brings in a third voice
 * a tritone away and fades unresolved.
 */
let deliberation = null;

export function deliberationStart() {
    const audio = ensureCtx();
    if (!audio || !master || deliberation) return;

    const t0 = audio.currentTime;
    const bus = audio.createGain();
    const filter = audio.createBiquadFilter();
    filter.type = 'lowpass';
    filter.frequency.value = 1100;
    bus.gain.setValueAtTime(0.0001, t0);
    bus.gain.exponentialRampToValueAtTime(0.16, t0 + 1.5);
    bus.connect(filter);
    filter.connect(master);

    const voice = (freq, type, level) => {
        const osc = audio.createOscillator();
        const amp = audio.createGain();
        osc.type = type;
        osc.frequency.value = freq;
        amp.gain.value = level;
        osc.connect(amp);
        amp.connect(bus);
        osc.start(t0);
        return osc;
    };

    // 3.5 Hz between the pair, and the same offset on the fifth above — the "two opinions".
    const a = voice(196, 'sine', 0.6);
    const b = voice(199.5, 'sine', 0.6);
    const a5 = voice(293.66, 'triangle', 0.18);
    const b5 = voice(297.16, 'triangle', 0.18);

    deliberation = { bus, filter, oscillators: [a, b, a5, b5], pairs: [[a, b], [a5, b5]] };
    // The Arena polls the judge for at most 45 s; never let a lost stop leave a drone running.
    deliberation.safety = setTimeout(() => deliberationStop(), 60000);
}

export function deliberationStop(release = 0.4) {
    const audio = ctx;
    const d = deliberation;
    if (!audio || !d) return;
    deliberation = null;
    clearTimeout(d.safety);

    const t = audio.currentTime;
    d.bus.gain.cancelScheduledValues(t);
    d.bus.gain.setValueAtTime(Math.max(0.0001, d.bus.gain.value), t);
    d.bus.gain.setTargetAtTime(0.0001, t, release / 4);
    for (const osc of d.oscillators) osc.stop(t + release + 0.2);
    d.oscillators[0].onended = () => { try { d.bus.disconnect(); d.filter.disconnect(); } catch { } };
}

/** Resolves a running deliberation; returns the seconds before the verdict should land. */
function deliberationResolve(agree) {
    const audio = ctx;
    const d = deliberation;
    if (!audio || !d) return 0;

    const t = audio.currentTime;
    if (agree) {
        for (const [a, b] of d.pairs) b.frequency.setTargetAtTime(a.frequency.value, t, 0.1);
        setTimeout(() => deliberationStop(0.5), 450);
        return 0.45;
    }

    tone({ freq: 196 * semitone(6), type: 'triangle', dur: 0.8, gain: 0.12 });
    setTimeout(() => deliberationStop(0.6), 600);
    return 0.6;
}

// ── G6: the verdict ──────────────────────────────────────────────────────────

/** A judged draw — deliberately unresolved, neither up nor down. */
function tieCue(at) {
    tone({ freq: 587.33, type: 'triangle', at, dur: 0.4, gain: 0.24 });
    tone({ freq: 587.33, type: 'triangle', at: at + 0.16, dur: 0.45, gain: 0.20 });
}

/**
 * The verdict, in one call so the timing is owned here: a running deliberation resolves
 * first, then the impact lands and the winner's own motif plays from its side, brightened
 * by a quiet fifth underneath. Returns the lead-in in ms so the caller can hold the visual
 * impact until the sound arrives.
 *
 * @param {boolean} tie
 * @param {number} pan          the winner's side, -1…1
 * @param {string} [seed]       the winner's model id
 * @param {string} [kind]       the winner's motif voice
 */
export function playVerdict(tie, pan = 0, seed = '', kind = 'remote') {
    const lead = deliberationResolve(!tie);
    if (tie) {
        tieCue(lead);
    } else {
        thump(lead, 0.55);
        motif(seed || 'winner', kind, pan, lead + 0.06, 1.2);
        tone({ freq: 523.25, type: 'sine', at: lead + 0.06, dur: 0.6, gain: 0.14 });
        tone({ freq: 783.99, type: 'sine', at: lead + 0.06, dur: 0.6, gain: 0.08 });
    }
    return Math.round(lead * 1000);
}

// ── G7: the odometer and the upset ───────────────────────────────────────────

/**
 * Clicks under the rolling ELO digits: the winner's side ticks upward in pitch, the loser's
 * downward, both decelerating with the reels (the badge transition starts ~350 ms in).
 *
 * @param {number} winnerPan -1…1
 */
export function playOdometer(winnerPan = 0) {
    const ticks = 14;
    for (let k = 0; k < ticks; k++) {
        const progress = k / ticks;
        // Ease-out spacing, matching the reels' cubic-bezier.
        const at = 0.35 + 0.95 * (1 - Math.pow(1 - progress, 2));
        tone({ freq: 1400 + 800 * progress, type: 'square', at, dur: 0.015, gain: 0.025, pan: winnerPan });
        tone({ freq: 1200 - 500 * progress, type: 'square', at: at + 0.01, dur: 0.015, gain: 0.02, pan: -winnerPan });
    }
}

/** A lower-rated model won: a minor stab and a falling sting, after the odometer settles. */
export function playUpset() {
    const at = 1.2;
    [220, 261.63, 329.63].forEach(freq => brass({ freq, at, dur: 0.32, gain: 0.07 }));
    brass({ freq: 440, at: at + 0.34, dur: 0.5, gain: 0.08 });
    tone({ freq: 880, type: 'triangle', at: at + 0.34, dur: 0.5, gain: 0.08, glide: 622.25 });
    thump(at, 0.35);
}

// ── G8: the champion ─────────────────────────────────────────────────────────

/**
 * A fanfare seeded by the tournament id — key, tempo and cadence differ per bracket — and a
 * crowd swell under it. Pickup triplet, I – IV – V – I, or I – ♭VI – ♭VII – I when the seed
 * picks the epic ending.
 */
export function playFanfare(seed) {
    const rand = seeded(seed);
    const roots = [261.63, 293.66, 329.63, 349.23, 392];
    const root = roots[Math.floor(rand() * roots.length)];
    const beat = 0.2 * (0.9 + rand() * 0.2);
    const epic = rand() < 0.4;

    // Pickup: three quick fifths into the downbeat.
    for (let i = 0; i < 3; i++) brass({ freq: root * semitone(7), at: i * beat * 0.5, dur: beat * 0.45, gain: 0.1 });

    const chords = epic ? [[0, 4, 7, 12], [-4, 0, 3, 8], [-2, 2, 5, 10], [0, 4, 7, 12]]
                        : [[0, 4, 7, 12], [5, 9, 12, 17], [7, 11, 14, 19], [0, 4, 7, 12]];
    let t = beat * 1.5;
    chords.forEach((chord, i) => {
        const last = i === chords.length - 1;
        const dur = last ? beat * 6 : beat * 1.6;
        chord.forEach((n, v) => brass({ freq: root * semitone(n), at: t, dur, gain: v === 3 ? 0.07 : 0.055, pan: (v - 1.5) * 0.25 }));
        if (last) {
            bell({ freq: root * 4, at: t, dur: 1.6, gain: 0.12 });
            thump(t, 0.5);
        }
        t += beat * 1.6;
    });

    // The crowd: two bands of noise swelling in under the final chord and roaring out.
    noiseBurst({ at: beat * 2, dur: 3.2, gain: 0.1, freq: 900, q: 0.6, attack: 1.4 });
    noiseBurst({ at: beat * 2.5, dur: 3.0, gain: 0.06, freq: 2600, q: 0.8, attack: 1.2, pan: 0.3 });
}

// ── The originals ────────────────────────────────────────────────────────────

/**
 * The last seconds of the judge's grace window: one note per second, rising, so the window
 * closing is audible without looking at the clock.
 *
 * @param {number} remaining seconds left (3, 2, 1)
 */
export function playCountdown(remaining) {
    const step = Math.max(0, 3 - Math.max(1, Math.min(3, remaining)));
    tone({ freq: 659.25 * semitone(step * 2), type: 'triangle', at: 0, dur: 0.14, gain: 0.16 });
}

/** UI tick for a click. Very short and quiet — it fires often. */
export function playTick() {
    tone({ freq: 1320, type: 'square', at: 0, dur: 0.035, gain: 0.07 });
}
