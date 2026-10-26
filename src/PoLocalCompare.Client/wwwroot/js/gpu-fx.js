/**
 * gpu-fx.js — the app's two shader effects, and the only render loops it has:
 *
 *   nebula()        the verdict: 1.9 s of domain-warped smoke blooming from the winning panel.
 *                   WebGPU, with a WebGL2 fallback running the same shader in GLSL.
 *   championStorm() the champion: up to 100,000 particles simulated in a WebGPU compute shader
 *                   swarm into the champion's name, hold, then burst and fall, colliding with
 *                   the bracket's match cards. No fallback here — the caller fires the Canvas2D
 *                   cannons instead when this returns false.
 *
 * Why these exist despite the rule against WebGL/WebGPU render loops (CLAUDE.md, "Motion is
 * compositor-only"): they were asked for on 2026-10-26, and they are built so that the reason
 * for that rule still holds. Browser models run WebLLM over WebGPU in this tab and the tok/s
 * the race reports is measured while they do; a loop competing for the GPU would make that
 * number wrong. So both effects:
 *
 *   1. start only when the GPU lease (window.poGpuLease, util.js) is free, after every await
 *      as well as before — a worker can start while an adapter is being requested;
 *   2. subscribe to the lease and tear down on the very next acquire, mid-effect;
 *   3. are one-shot and bounded (1.9 s and 7.2 s), stop when the tab is hidden, create their
 *      own GPUDevice and destroy it when done — nothing stays resident on the GPU;
 *   4. fire only at moments when nothing is being timed: a verdict exists only once both
 *      sides have finished, and a champion only once every match has.
 *
 * Both do nothing under prefers-reduced-motion. Each draws into a fixed, aria-hidden,
 * pointer-events: none canvas that is removed afterwards.
 */

let running = false;

function gpuBusy() {
    try {
        return !!window.poGpuLease?.busy();
    } catch {
        return false;
    }
}

// Effects are opt-in (Settings → Visual effects; prefs.js stamps <html data-fx="on">). Off,
// they stand down exactly as they do under reduced motion.
function prefersReducedMotion() {
    if (document.documentElement.dataset.fx !== 'on') return true;
    try {
        return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    } catch {
        return false;
    }
}

function mayRun() {
    return !running && !gpuBusy() && !prefersReducedMotion() && !document.hidden && !!document.body;
}

/** A design token as linear-ish [r, g, b] in 0…1, normalised through a 2D context. */
function tokenRgb(name, fallback) {
    let value = fallback;
    try {
        value = getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
    } catch {
        // Keep the fallback.
    }
    const probe = document.createElement('canvas').getContext('2d');
    if (!probe) return [1, 1, 1];
    probe.fillStyle = fallback;
    probe.fillStyle = value;
    const normalised = probe.fillStyle;
    if (normalised.startsWith('#')) {
        const n = parseInt(normalised.slice(1, 7), 16);
        return [(n >> 16 & 255) / 255, (n >> 8 & 255) / 255, (n & 255) / 255];
    }
    const parts = normalised.match(/[\d.]+/g)?.map(Number) ?? [255, 255, 255];
    return [parts[0] / 255, parts[1] / 255, parts[2] / 255];
}

/** True when the page behind is dark — additive light reads as glow there and as nothing on white. */
function darkBackdrop() {
    try {
        const [r, g, b] = tokenRgb('--surface-base', '#ffffff');
        return 0.2126 * r + 0.7152 * g + 0.0722 * b < 0.4;
    } catch {
        return false;
    }
}

function overlay(scale, layerClass) {
    const canvas = document.createElement('canvas');
    canvas.className = `fx-overlay ${layerClass}`;
    canvas.setAttribute('aria-hidden', 'true');
    const dpr = Math.min(window.devicePixelRatio || 1, 2) * scale;
    canvas.width = Math.max(1, Math.floor(window.innerWidth * dpr));
    canvas.height = Math.max(1, Math.floor(window.innerHeight * dpr));
    document.body.appendChild(canvas);
    return canvas;
}

/**
 * Drives one effect: frame(seconds, dt) each animation frame until durationMs, then teardown.
 * Ends early — and still tears down — on a lease acquire, a hidden tab, or a frame that throws.
 */
function runLoop(durationMs, frame, teardown) {
    let raf = 0;
    let done = false;
    const unsubscribe = window.poGpuLease?.subscribe(busy => { if (busy) finish(); }) ?? (() => { });
    const onHide = () => { if (document.hidden) finish(); };
    document.addEventListener('visibilitychange', onHide);

    const start = performance.now();
    let previous = start;

    function finish() {
        if (done) return;
        done = true;
        cancelAnimationFrame(raf);
        unsubscribe();
        document.removeEventListener('visibilitychange', onHide);
        try { teardown(); } catch { /* the canvas is going regardless */ }
        running = false;
    }

    function tick(now) {
        if (done) return;
        const elapsed = now - start;
        // Clamped like fx.js: a stalled frame must not teleport the simulation.
        const dt = Math.min((now - previous) / 1000, 1 / 30);
        previous = now;
        if (elapsed >= durationMs) {
            finish();
            return;
        }
        try {
            frame(elapsed / 1000, dt);
        } catch {
            finish();
            return;
        }
        raf = requestAnimationFrame(tick);
    }

    running = true;
    raf = requestAnimationFrame(tick);
}

/** A WebGPU device and a configured canvas context, or null. Re-checks the lease after each await. */
async function webGpu(canvas) {
    if (!navigator.gpu) return null;
    const adapter = await navigator.gpu.requestAdapter();
    if (!adapter || gpuBusy()) return null;
    const device = await adapter.requestDevice();
    if (gpuBusy()) {
        device.destroy();
        return null;
    }
    const context = canvas.getContext('webgpu');
    if (!context) {
        device.destroy();
        return null;
    }
    const format = navigator.gpu.getPreferredCanvasFormat();
    context.configure({ device, format, alphaMode: 'premultiplied' });
    return { device, context, format };
}

// ── G9: the verdict nebula ───────────────────────────────────────────────────

const NEBULA_MS = 1900;

/*
 * The smoke: fbm value noise, domain-warped by two more fbm lookups (q), swirled by an angle
 * that falls off with radius so it curls as it spreads, and shaped into an expanding shell
 * whose front is roughened by the warp. Sparks ride the shell. Output is premultiplied.
 */
const NEBULA_WGSL = /* wgsl */ `
struct U {
    res: vec2f,
    origin: vec2f,
    time: f32,
    life: f32,
    pad: vec2f,
    c1: vec4f,
    c2: vec4f,
    c3: vec4f,
};
@group(0) @binding(0) var<uniform> u: U;

@vertex fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4f {
    let p = array<vec2f, 3>(vec2f(-1.0, -1.0), vec2f(3.0, -1.0), vec2f(-1.0, 3.0));
    return vec4f(p[i], 0.0, 1.0);
}

fn hash2(p: vec2f) -> f32 {
    return fract(sin(dot(p, vec2f(127.1, 311.7))) * 43758.5453);
}

fn vnoise(p: vec2f) -> f32 {
    let i = floor(p);
    let f = fract(p);
    let s = f * f * (3.0 - 2.0 * f);
    let a = hash2(i);
    let b = hash2(i + vec2f(1.0, 0.0));
    let c = hash2(i + vec2f(0.0, 1.0));
    let d = hash2(i + vec2f(1.0, 1.0));
    return mix(mix(a, b, s.x), mix(c, d, s.x), s.y);
}

fn fbm(p0: vec2f) -> f32 {
    var p = p0;
    var v = 0.0;
    var a = 0.5;
    let rot = mat2x2f(0.8, 0.6, -0.6, 0.8);
    for (var i = 0; i < 5; i++) {
        v += a * vnoise(p);
        p = rot * p * 2.02;
        a *= 0.5;
    }
    return v;
}

@fragment fn fs(@builtin(position) frag: vec4f) -> @location(0) vec4f {
    let d = (frag.xy - u.origin) / u.res.y;
    let r = length(d);
    let t = u.time;

    let swirl = 2.2 * exp(-r * 2.5) * t;
    let s = vec2f(cos(swirl) * d.x - sin(swirl) * d.y, sin(swirl) * d.x + cos(swirl) * d.y);
    let q = vec2f(fbm(s * 3.0 + vec2f(0.0, t * 0.5)), fbm(s * 3.0 + vec2f(5.2, 1.3 - t * 0.4)));
    let w = fbm(s * 2.2 + q * 2.0 + vec2f(t * 0.15, 0.0));

    let k = min(t / 1.6, 1.0);
    let front = 0.1 + 0.85 * (1.0 - (1.0 - k) * (1.0 - k) * (1.0 - k));
    let rr = r + (w - 0.5) * 0.45;
    let shell = (1.0 - smoothstep(front - 0.15, front + 0.05, rr)) * smoothstep(front - 0.6, front - 0.1, rr);
    let core = exp(-r * 5.0) * (1.0 - 0.6 * k);
    let density = clamp(shell * (0.25 + w * 1.3) + core, 0.0, 1.0);

    var col = mix(u.c1.rgb, u.c2.rgb, smoothstep(0.3, 0.7, w));
    col = mix(col, u.c3.rgb, smoothstep(0.6, 0.9, q.x));
    col *= 0.9 + 0.9 * w;

    let sparkle = step(0.9985, hash2(floor(frag.xy) + floor(t * 12.0))) * shell;
    let fade = 1.0 - smoothstep(0.5, 1.0, u.life);
    let a = clamp(density * 0.45 + sparkle, 0.0, 1.0) * fade;
    return vec4f(min(col + vec3f(sparkle), vec3f(1.0)) * a, a);
}
`;

/* The same shader for WebGL2. gl_FragCoord is y-up, so it is flipped to match WGSL's y-down. */
const NEBULA_VERT = `#version 300 es
void main() {
    vec2 p = vec2(gl_VertexID == 1 ? 3.0 : -1.0, gl_VertexID == 2 ? 3.0 : -1.0);
    gl_Position = vec4(p, 0.0, 1.0);
}`;

const NEBULA_FRAG = `#version 300 es
precision highp float;
uniform vec2 uRes;
uniform vec2 uOrigin;
uniform float uTime;
uniform float uLife;
uniform vec3 uC1;
uniform vec3 uC2;
uniform vec3 uC3;
out vec4 outColor;

float hash2(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }

float vnoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 s = f * f * (3.0 - 2.0 * f);
    float a = hash2(i);
    float b = hash2(i + vec2(1.0, 0.0));
    float c = hash2(i + vec2(0.0, 1.0));
    float d = hash2(i + vec2(1.0, 1.0));
    return mix(mix(a, b, s.x), mix(c, d, s.x), s.y);
}

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    mat2 rot = mat2(0.8, 0.6, -0.6, 0.8);
    for (int i = 0; i < 5; i++) {
        v += a * vnoise(p);
        p = rot * p * 2.02;
        a *= 0.5;
    }
    return v;
}

void main() {
    vec2 frag = vec2(gl_FragCoord.x, uRes.y - gl_FragCoord.y);
    vec2 d = (frag - uOrigin) / uRes.y;
    float r = length(d);
    float t = uTime;

    float swirl = 2.2 * exp(-r * 2.5) * t;
    vec2 s = vec2(cos(swirl) * d.x - sin(swirl) * d.y, sin(swirl) * d.x + cos(swirl) * d.y);
    vec2 q = vec2(fbm(s * 3.0 + vec2(0.0, t * 0.5)), fbm(s * 3.0 + vec2(5.2, 1.3 - t * 0.4)));
    float w = fbm(s * 2.2 + q * 2.0 + vec2(t * 0.15, 0.0));

    float k = min(t / 1.6, 1.0);
    float front = 0.1 + 0.85 * (1.0 - (1.0 - k) * (1.0 - k) * (1.0 - k));
    float rr = r + (w - 0.5) * 0.45;
    float shell = (1.0 - smoothstep(front - 0.15, front + 0.05, rr)) * smoothstep(front - 0.6, front - 0.1, rr);
    float core = exp(-r * 5.0) * (1.0 - 0.6 * k);
    float density = clamp(shell * (0.25 + w * 1.3) + core, 0.0, 1.0);

    vec3 col = mix(uC1, uC2, smoothstep(0.3, 0.7, w));
    col = mix(col, uC3, smoothstep(0.6, 0.9, q.x));
    col *= 0.9 + 0.9 * w;

    float sparkle = step(0.9985, hash2(floor(frag) + floor(t * 12.0))) * shell;
    float fade = 1.0 - smoothstep(0.5, 1.0, uLife);
    float a = clamp(density * 0.45 + sparkle, 0.0, 1.0) * fade;
    outColor = vec4(min(col + vec3(sparkle), vec3(1.0)) * a, a);
}`;

/** The nebula's palette and origin, in the canvas's own pixels. */
function nebulaScene(canvas, selector) {
    let rect = null;
    try {
        rect = document.querySelector(selector)?.getBoundingClientRect() ?? null;
    } catch {
        // Bad selector — bloom from the middle.
    }
    const sx = canvas.width / window.innerWidth;
    const sy = canvas.height / window.innerHeight;
    const cx = rect ? rect.left + rect.width / 2 : window.innerWidth / 2;
    const cy = rect ? rect.top + rect.height / 2 : window.innerHeight / 2;
    return {
        origin: [cx * sx, cy * sy],
        c1: tokenRgb('--accent-green', '#2ddc84'),
        c2: tokenRgb('--accent-cyan', '#12b8cf'),
        c3: tokenRgb('--accent-purple', '#7d96ff'),
    };
}

async function nebulaWebGpu(canvas, scene) {
    const gpu = await webGpu(canvas);
    if (!gpu) return false;
    try {
        return await startNebula(gpu, canvas, scene);
    } catch (error) {
        gpu.device.destroy();
        throw error;
    }
}

async function startNebula({ device, context, format }, canvas, scene) {
    const module = device.createShaderModule({ code: NEBULA_WGSL });
    const pipeline = await device.createRenderPipelineAsync({
        layout: 'auto',
        vertex: { module, entryPoint: 'vs' },
        fragment: { module, entryPoint: 'fs', targets: [{ format }] },
        primitive: { topology: 'triangle-list' },
    });
    if (gpuBusy()) {
        device.destroy();
        return false;
    }

    const uniforms = new Float32Array(20);
    const buffer = device.createBuffer({ size: uniforms.byteLength, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    const bindGroup = device.createBindGroup({
        layout: pipeline.getBindGroupLayout(0),
        entries: [{ binding: 0, resource: { buffer } }],
    });
    uniforms.set([canvas.width, canvas.height, ...scene.origin], 0);
    uniforms.set([...scene.c1, 1, ...scene.c2, 1, ...scene.c3, 1], 8);

    runLoop(NEBULA_MS, t => {
        uniforms[4] = t;
        uniforms[5] = t / (NEBULA_MS / 1000);
        device.queue.writeBuffer(buffer, 0, uniforms);
        const encoder = device.createCommandEncoder();
        const pass = encoder.beginRenderPass({
            colorAttachments: [{
                view: context.getCurrentTexture().createView(),
                loadOp: 'clear',
                storeOp: 'store',
                clearValue: { r: 0, g: 0, b: 0, a: 0 },
            }],
        });
        pass.setPipeline(pipeline);
        pass.setBindGroup(0, bindGroup);
        pass.draw(3);
        pass.end();
        device.queue.submit([encoder.finish()]);
    }, () => {
        canvas.remove();
        device.destroy();
    });
    return true;
}

function nebulaWebGl(canvas, scene) {
    const gl = canvas.getContext('webgl2', { alpha: true, premultipliedAlpha: true, antialias: false });
    if (!gl) return false;

    const shader = (type, source) => {
        const s = gl.createShader(type);
        gl.shaderSource(s, source);
        gl.compileShader(s);
        return gl.getShaderParameter(s, gl.COMPILE_STATUS) ? s : null;
    };
    const vert = shader(gl.VERTEX_SHADER, NEBULA_VERT);
    const frag = shader(gl.FRAGMENT_SHADER, NEBULA_FRAG);
    if (!vert || !frag) return false;
    const program = gl.createProgram();
    gl.attachShader(program, vert);
    gl.attachShader(program, frag);
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) return false;

    gl.useProgram(program);
    gl.bindVertexArray(gl.createVertexArray());
    gl.viewport(0, 0, canvas.width, canvas.height);
    const at = name => gl.getUniformLocation(program, name);
    gl.uniform2f(at('uRes'), canvas.width, canvas.height);
    gl.uniform2f(at('uOrigin'), scene.origin[0], scene.origin[1]);
    gl.uniform3fv(at('uC1'), scene.c1);
    gl.uniform3fv(at('uC2'), scene.c2);
    gl.uniform3fv(at('uC3'), scene.c3);
    const time = at('uTime');
    const life = at('uLife');

    runLoop(NEBULA_MS, t => {
        gl.uniform1f(time, t);
        gl.uniform1f(life, t / (NEBULA_MS / 1000));
        gl.drawArrays(gl.TRIANGLES, 0, 3);
    }, () => {
        canvas.remove();
        gl.getExtension('WEBGL_lose_context')?.loseContext();
    });
    return true;
}

/**
 * The verdict nebula, blooming from the element `selector` names (the winning panel).
 * Resolves true if it ran.
 */
export async function nebula(selector) {
    if (!mayRun()) return false;
    running = true;

    // Half resolution: smoke has no edges to lose, and it quarters the fragment work.
    const canvas = overlay(0.5, 'fx-overlay--under');
    const scene = nebulaScene(canvas, selector);
    try {
        if (await nebulaWebGpu(canvas, scene)) return true;
    } catch {
        // No adapter, a lost device or a shader the driver refused: try WebGL2.
    }
    if (!gpuBusy()) {
        // A canvas that has had a 'webgpu' context cannot take a 'webgl2' one.
        canvas.remove();
        const fresh = overlay(0.5, 'fx-overlay--under');
        try {
            if (nebulaWebGl(fresh, scene)) return true;
        } catch {
            // Fall through to the clean-up below.
        }
        fresh.remove();
    }
    canvas.remove();
    running = false;
    return false;
}

// ── G10: the champion storm ──────────────────────────────────────────────────

const STORM_MS = 7200;
/** When the swarm lets go of the letters. Before it: springs. After it: gravity and collisions. */
const EXPLODE_AT = 3.0;
const MAX_BOXES = 32;

/*
 * Particle layout, 32 bytes: pos, vel, home (its point in the name), seed, state.
 * state 0 = swarming, 1 = flying after the burst, 2 = resting on a surface.
 * Sim is shared by both pipelines: 96 bytes, every field at its WGSL alignment.
 */
const STORM_COMMON = /* wgsl */ `
struct P {
    pos: vec2f,
    vel: vec2f,
    home: vec2f,
    seed: f32,
    state: f32,
};
struct Sim {
    res: vec2f,
    center: vec2f,
    time: f32,
    dt: f32,
    life: f32,
    count: u32,
    boxCount: u32,
    size: f32,
    pad: vec2f,
    c1: vec4f,
    c2: vec4f,
    c3: vec4f,
};
`;

const STORM_COMPUTE = STORM_COMMON + /* wgsl */ `
@group(0) @binding(0) var<storage, read_write> ps: array<P>;
@group(0) @binding(1) var<uniform> sim: Sim;
@group(0) @binding(2) var<storage, read> boxes: array<vec4f>;

fn hash1(n: f32) -> f32 {
    return fract(sin(n * 12.9898) * 43758.5453);
}

// A cheap divergence-free-looking swirl field: it bends paths without pulling them anywhere.
fn curl(p: vec2f) -> vec2f {
    return vec2f(cos(p.y * 1.7 + sin(p.x * 1.3)), -cos(p.x * 1.9 + sin(p.y * 1.1)));
}

@compute @workgroup_size(256)
fn simulate(@builtin(global_invocation_id) id: vec3u) {
    let i = id.x;
    if (i >= sim.count) {
        return;
    }
    var p = ps[i];
    let dt = sim.dt;
    let t = sim.time;

    if (t < ${EXPLODE_AT.toFixed(1)}) {
        // A damped spring to the particle's point in the name, stiffening as the swarm
        // gathers, with the swirl fading out so the letters settle crisp.
        let k = mix(10.0, 120.0, smoothstep(0.2, 2.2, t));
        let c = 1.8 * sqrt(k);
        let swirl = curl(p.pos * 0.006 + vec2f(t * 0.7, p.seed * 6.0)) * 900.0 * (1.0 - smoothstep(0.6, 2.0, t));
        p.vel += ((p.home - p.pos) * k - p.vel * c + swirl) * dt;
        p.pos += p.vel * dt;
    } else if (p.state < 1.5) {
        if (p.state < 0.5) {
            // The burst: outward from the name's centre, with a kick upward.
            let d = p.pos - sim.center;
            let n = d / max(length(d), 1.0);
            let speed = 350.0 + 1100.0 * hash1(p.seed * 91.7);
            p.vel = n * speed + vec2f((hash1(p.seed * 13.1) - 0.5) * 300.0, -450.0);
            p.state = 1.0;
        }
        let prev = p.pos;
        p.vel.y += 1500.0 * dt;
        p.vel *= pow(0.55, dt);
        p.pos += p.vel * dt;

        if (p.pos.x < 0.0 || p.pos.x > sim.res.x) {
            p.pos.x = clamp(p.pos.x, 0.0, sim.res.x);
            p.vel.x = -p.vel.x * 0.4;
        }
        // Landing: the top edge of a card crossed this step while falling, or the floor.
        // Rising particles pass through, which lets the burst leave from behind a card.
        if (p.vel.y > 0.0) {
            var surface = -1.0;
            if (p.pos.y >= sim.res.y) {
                surface = sim.res.y;
            }
            for (var b = 0u; b < sim.boxCount; b++) {
                let box = boxes[b];
                if (prev.y <= box.y && p.pos.y >= box.y && p.pos.x >= box.x && p.pos.x <= box.z) {
                    surface = box.y;
                    break;
                }
            }
            if (surface >= 0.0) {
                p.pos.y = surface;
                if (p.vel.y < 90.0) {
                    p.vel = vec2f(0.0);
                    p.state = 2.0;
                } else {
                    p.vel.y = -p.vel.y * 0.38;
                    p.vel.x *= 0.6;
                }
            }
        }
    }
    ps[i] = p;
}
`;

const STORM_RENDER = STORM_COMMON + /* wgsl */ `
@group(0) @binding(0) var<storage, read> ps: array<P>;
@group(0) @binding(1) var<uniform> sim: Sim;

struct VOut {
    @builtin(position) pos: vec4f,
    @location(0) uv: vec2f,
    @location(1) col: vec4f,
};

@vertex fn vs(@builtin(vertex_index) v: u32, @builtin(instance_index) i: u32) -> VOut {
    let corners = array<vec2f, 6>(
        vec2f(-1.0, -1.0), vec2f(1.0, -1.0), vec2f(-1.0, 1.0),
        vec2f(-1.0, 1.0), vec2f(1.0, -1.0), vec2f(1.0, 1.0));
    let p = ps[i];
    let c = corners[v];
    let speed = length(p.vel);
    // Fast particles draw larger and hotter: motion reads without a trail buffer.
    let radius = sim.size * (1.0 + min(speed / 1500.0, 1.0));
    let at = p.pos + c * radius;

    var out: VOut;
    out.pos = vec4f(at.x / sim.res.x * 2.0 - 1.0, 1.0 - at.y / sim.res.y * 2.0, 0.0, 1.0);
    out.uv = c;
    let tone = fract(p.seed * 7.31);
    var col = mix(sim.c1.rgb, sim.c2.rgb, smoothstep(0.0, 0.6, tone));
    col = mix(col, sim.c3.rgb, smoothstep(0.6, 1.0, tone));
    col = mix(col, vec3f(1.0), min(speed / 900.0, 1.0) * 0.3);
    // pad.x is the per-particle intensity: well under 1 when blending additively, so dense
    // regions build up through the palette instead of clipping straight to white.
    out.col = vec4f(col, sim.life * sim.pad.x);
    return out;
}

@fragment fn fs(frag: VOut) -> @location(0) vec4f {
    let a = (1.0 - smoothstep(0.35, 1.0, length(frag.uv))) * frag.col.a;
    return vec4f(frag.col.rgb * a, a);
}
`;

/**
 * The champion's name rasterised into target points, in CSS pixels. Only the band of rows
 * the text occupies is read back.
 */
function nameTargets(text, width, height) {
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    const g = canvas.getContext('2d', { willReadFrequently: true });
    if (!g) return null;

    const family = getComputedStyle(document.body).fontFamily || 'system-ui, sans-serif';
    let size = Math.min(height * 0.24, 220);
    g.font = `800 ${size}px ${family}`;
    const measured = g.measureText(text).width;
    if (measured > width * 0.9) size *= (width * 0.9) / measured;
    g.font = `800 ${size}px ${family}`;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const cy = height * 0.42;
    g.fillText(text, width / 2, cy);

    const top = Math.max(0, Math.floor(cy - size));
    const band = Math.min(height - top, Math.ceil(size * 2));
    const data = g.getImageData(0, top, width, band).data;
    const stride = size > 80 ? 2 : 1;
    const points = [];
    for (let y = 0; y < band; y += stride) {
        for (let x = 0; x < width; x += stride) {
            if (data[(y * width + x) * 4 + 3] > 128) points.push(x, y + top);
        }
    }
    return points.length ? { points, center: [width / 2, cy] } : null;
}

/** Card rectangles as [left, top, right, bottom] for the compute shader. */
function colliderBoxes(selector) {
    const boxes = new Float32Array(MAX_BOXES * 4);
    let count = 0;
    try {
        for (const el of Array.from(document.querySelectorAll(selector ?? '')).slice(0, MAX_BOXES)) {
            const r = el.getBoundingClientRect();
            if (r.width <= 0 || r.height <= 0) continue;
            boxes.set([r.left, r.top, r.right, r.bottom], count * 4);
            count++;
        }
    } catch {
        // No selector, or a bad one: the floor alone.
    }
    return { boxes, count };
}

/**
 * The champion storm. Resolves true if it started; false means the caller should fall back
 * to the Canvas2D cannons.
 *
 * @param {string} text       the champion's name
 * @param {string} colliders  selector for what the burst lands on (the match cards)
 */
export async function championStorm(text, colliders) {
    if (!mayRun() || !navigator.gpu || !text) return false;
    running = true;

    const width = window.innerWidth;
    const height = window.innerHeight;
    const targets = nameTargets(String(text), width, height);
    if (!targets) {
        running = false;
        return false;
    }

    const canvas = overlay(1, 'fx-overlay--storm');
    let gpu = null;
    try {
        gpu = await webGpu(canvas);
        if (!gpu) throw new Error('no device');
        startStorm(gpu, canvas, targets, colliderBoxes(colliders), width, height);
        return true;
    } catch {
        gpu?.device.destroy();
        canvas.remove();
        running = false;
        return false;
    }
}

function startStorm({ device, context, format }, canvas, targets, colliders, width, height) {
    const count = width >= 1024 ? 100_000 : 40_000;
    const { points, center } = targets;
    const targetCount = points.length / 2;

    // Launched from the two bottom corners, like the cannons, then pulled into the letters.
    const particles = new Float32Array(count * 8);
    for (let i = 0; i < count; i++) {
        const left = i % 2 === 0;
        const t = Math.floor(Math.random() * targetCount) * 2;
        const o = i * 8;
        particles[o] = (left ? -20 : width + 20) + (Math.random() - 0.5) * 80;
        particles[o + 1] = height + 20 + Math.random() * 60;
        particles[o + 2] = (left ? 1 : -1) * (300 + Math.random() * 700);
        particles[o + 3] = -(700 + Math.random() * 900);
        particles[o + 4] = points[t] + (Math.random() - 0.5) * 1.6;
        particles[o + 5] = points[t + 1] + (Math.random() - 0.5) * 1.6;
        particles[o + 6] = Math.random();
        particles[o + 7] = 0;
    }

    const particleBuffer = device.createBuffer({ size: particles.byteLength, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
    device.queue.writeBuffer(particleBuffer, 0, particles);
    const boxBuffer = device.createBuffer({ size: colliders.boxes.byteLength, usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST });
    device.queue.writeBuffer(boxBuffer, 0, colliders.boxes);

    const sim = new ArrayBuffer(96);
    const f = new Float32Array(sim);
    const u = new Uint32Array(sim);
    f.set([width, height, center[0], center[1]], 0);
    u[7] = count;
    u[8] = colliders.count;
    f[9] = width >= 1024 ? 1.3 : 1.6;
    f.set([...tokenRgb('--accent-yellow', '#eab308'), 1], 12);
    f.set([...tokenRgb('--accent-green', '#2ddc84'), 1], 16);
    f.set([...tokenRgb('--accent-cyan', '#12b8cf'), 1], 20);
    const simBuffer = device.createBuffer({ size: sim.byteLength, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });

    const computeModule = device.createShaderModule({ code: STORM_COMPUTE });
    const renderModule = device.createShaderModule({ code: STORM_RENDER });
    const compute = device.createComputePipeline({ layout: 'auto', compute: { module: computeModule, entryPoint: 'simulate' } });

    // Additive on a dark page, where overlapping light should bloom; ordinary "over" on a light
    // one, where additive light would vanish into the white.
    const dark = darkBackdrop();
    f[10] = dark ? 0.3 : 1;
    const blend = dark
        ? { color: { srcFactor: 'one', dstFactor: 'one' }, alpha: { srcFactor: 'one', dstFactor: 'one' } }
        : { color: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha' }, alpha: { srcFactor: 'one', dstFactor: 'one-minus-src-alpha' } };
    const render = device.createRenderPipeline({
        layout: 'auto',
        vertex: { module: renderModule, entryPoint: 'vs' },
        fragment: { module: renderModule, entryPoint: 'fs', targets: [{ format, blend }] },
        primitive: { topology: 'triangle-list' },
    });

    const computeGroup = device.createBindGroup({
        layout: compute.getBindGroupLayout(0),
        entries: [
            { binding: 0, resource: { buffer: particleBuffer } },
            { binding: 1, resource: { buffer: simBuffer } },
            { binding: 2, resource: { buffer: boxBuffer } },
        ],
    });
    const renderGroup = device.createBindGroup({
        layout: render.getBindGroupLayout(0),
        entries: [
            { binding: 0, resource: { buffer: particleBuffer } },
            { binding: 1, resource: { buffer: simBuffer } },
        ],
    });
    const workgroups = Math.ceil(count / 256);
    const seconds = STORM_MS / 1000;

    runLoop(STORM_MS, (t, dt) => {
        f[4] = t;
        f[5] = dt;
        // Fade in over 0.3 s, out over the last second.
        f[6] = Math.min(1, t / 0.3) * Math.min(1, (seconds - t) / 1);
        device.queue.writeBuffer(simBuffer, 0, sim);

        const encoder = device.createCommandEncoder();
        const step = encoder.beginComputePass();
        step.setPipeline(compute);
        step.setBindGroup(0, computeGroup);
        step.dispatchWorkgroups(workgroups);
        step.end();

        const pass = encoder.beginRenderPass({
            colorAttachments: [{
                view: context.getCurrentTexture().createView(),
                loadOp: 'clear',
                storeOp: 'store',
                clearValue: { r: 0, g: 0, b: 0, a: 0 },
            }],
        });
        pass.setPipeline(render);
        pass.setBindGroup(0, renderGroup);
        pass.draw(6, count);
        pass.end();
        device.queue.submit([encoder.finish()]);
    }, () => {
        canvas.remove();
        device.destroy();
    });
}
