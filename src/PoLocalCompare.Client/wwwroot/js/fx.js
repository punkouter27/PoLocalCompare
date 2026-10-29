/**
 * fx.js — the one-shot confetti burst for a verdict or a champion, with rigid-body collisions:
 * pieces land on, bounce off and come to rest on the elements the caller names (the Arena's
 * two panels, the bracket's match cards). Hand-rolled — a few dozen axis-aligned boxes do not
 * need a physics library, and Rapier would be a WASM download for one effect. Also the
 * verdict's shockwave ring and haptic pulse, and the champion's two corner cannons. The two
 * shader effects — the verdict nebula and the champion storm — are gpu-fx.js.
 * (The photo-finish strip, shard/mote/pyrotechnic effects and the WebGL backdrop were cut on
 * 2026-09-26 as decoration; a second WebGL backdrop was added and removed again on 2026-10-11.)
 *
 * Browser models run WebLLM inference over WebGPU in this same tab, and the tok/s the race
 * reports is measured while that is happening. A render loop stealing GPU would not merely
 * look bad, it would make the number the app exists to report wrong — and slow a browser
 * model's own generation while it is being timed. Two rules follow from that:
 *
 *   1. Every effect here checks the GPU lease (window.poGpuLease, util.js) and does nothing
 *      while it is held. webllm-interop.js holds it for exactly the life of a WebLLM worker.
 *   2. The burst is Canvas2D, runs for under three seconds and removes its canvas.
 */

/** How much speed a piece keeps when it hits something. Paper, not rubber. */
const RESTITUTION = 0.42;
/** Below this downward speed (px/s) a landing piece stays down instead of bouncing again. */
const REST_SPEED = 70;

/**
 * The boxes pieces collide with, as viewport rectangles measured once at burst time. A page
 * scrolled during the two seconds a burst lasts leaves them slightly stale, which reads as
 * confetti settling a little off an edge — not worth a per-frame layout read to correct.
 */
function collidersFrom(selector) {
    if (!selector) return [];
    try {
        return Array.from(document.querySelectorAll(selector))
            .slice(0, 24)
            .map(el => el.getBoundingClientRect())
            .filter(r => r.width > 0 && r.height > 0);
    } catch {
        return [];
    }
}

/** Hard ceiling on concurrent bursts, so a fast click-through cannot stack canvases. */
const MAX_ACTIVE = 2;
let active = 0;

/** True while a WebLLM worker holds the GPU — see util.js. */
function gpuBusy() {
    try {
        return !!window.poGpuLease?.busy();
    } catch {
        return false;
    }
}

// Off unless Settings → Visual effects is on and the OS is not asking for reduced motion
// (prefs.js owns that rule).
function motionOff() {
    return !window.poPrefs?.motionAllowed();
}

/**
 * Reads a design token off the document so particles inherit the live theme — including the
 * user's explicit [data-theme] override, which is why this reads computed style rather than
 * hard-coding a palette.
 */
function token(name, fallback) {
    try {
        const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
        return value || fallback;
    } catch {
        return fallback;
    }
}

function createOverlay() {
    const canvas = document.createElement('canvas');
    // Fixed and non-interactive: the burst is decoration over whatever is underneath, and must
    // never swallow a click meant for the verdict buttons behind it.
    canvas.className = 'fx-overlay';
    canvas.setAttribute('aria-hidden', 'true');

    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = Math.floor(window.innerWidth * dpr);
    canvas.height = Math.floor(window.innerHeight * dpr);

    document.body.appendChild(canvas);

    const ctx = canvas.getContext('2d');
    if (ctx) ctx.scale(dpr, dpr);

    return { canvas, ctx };
}

/**
 * Fires a particle burst from a point, or from several emitters at once.
 *
 * @param {object} options
 * @param {number} options.x        origin, CSS pixels (defaults to viewport centre)
 * @param {number} options.y        origin, CSS pixels
 * @param {number} options.count    particle count
 * @param {number} options.spread   initial speed, px/s
 * @param {{x: number, y: number, direction: number, cone: number, spread: number}[]} [options.emitters]
 *   replaces x/y/spread: piece i leaves emitter i mod n, inside its cone (radians; 0 points
 *   right, -π/2 straight up)
 * @param {string[]} options.colors palette; defaults to the theme's accent
 * @param {number} options.durationMs
 * @param {string} options.colliders selector for elements the pieces land on and bounce off
 */
function burst(options = {}) {
    if (gpuBusy()) return;
    // Respecting reduced-motion by not animating at all, rather than by animating faster.
    // A confetti burst has no non-moving equivalent worth substituting.
    if (motionOff()) return;
    if (active >= MAX_ACTIVE) return;
    if (!document.body) return;

    const {
        x = window.innerWidth / 2,
        y = window.innerHeight / 3,
        count = 90,
        spread = 520,
    } = options;
    // Biased upward: a full circle sprays as much into the floor as the air, which reads
    // as a puff rather than a celebration.
    const emitters = options.emitters?.length
        ? options.emitters
        : [{ x, y, direction: -Math.PI / 2, cone: Math.PI * 1.1, spread }];

    const boxes = collidersFrom(options.colliders);
    // Longer when there is something to land on: the bounce and the settle are the effect.
    const durationMs = options.durationMs ?? (boxes.length ? 2600 : 1400);
    const floor = window.innerHeight;
    const right = window.innerWidth;

    const colors = options.colors && options.colors.length
        ? options.colors
        : [
            token('--accent-green', '#2ddc84'),
            token('--accent-blue', '#53a6ff'),
            token('--accent-yellow', '#eab308'),
            token('--text', '#ffffff'),
        ];

    const overlay = createOverlay();
    const ctx = overlay.ctx;
    if (!ctx) {
        overlay.canvas.remove();
        return;
    }

    active++;

    const gravity = 900;      // px/s², enough that the arc reads as falling confetti
    const drag = 0.86;        // per second; without it everything exits the viewport at once

    const particles = [];
    for (let i = 0; i < count; i++) {
        const emitter = emitters[i % emitters.length];
        const angle = emitter.direction + (Math.random() - 0.5) * emitter.cone;
        const speed = emitter.spread * (0.35 + Math.random() * 0.65);

        particles.push({
            x: emitter.x, y: emitter.y,
            vx: Math.cos(angle) * speed,
            vy: Math.sin(angle) * speed,
            size: 3 + Math.random() * 5,
            spin: (Math.random() - 0.5) * 12,
            rot: Math.random() * Math.PI,
            resting: false,
            color: colors[i % colors.length],
        });
    }

    const started = performance.now();
    let previous = started;

    function frame(now) {
        // Real elapsed time rather than a fixed step, so the arc is the same on a 144Hz display
        // as on a 60Hz one. Clamped because a backgrounded tab resumes with a huge delta that
        // would teleport every particle off screen.
        const dt = Math.min((now - previous) / 1000, 0.05);
        previous = now;

        const elapsed = Math.max(0, now - started);
        const life = elapsed / durationMs;

        if (life >= 1) {
            overlay.canvas.remove();
            active--;
            return;
        }

        ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
        ctx.globalAlpha = 1 - life * life;   // hold opacity, then fade off quickly at the end

        const decay = Math.pow(drag, dt);

        for (const p of particles) {
            if (!p.resting) {
                const prevY = p.y;
                p.vx *= decay;
                p.vy = p.vy * decay + gravity * dt;
                p.x += p.vx * dt;
                p.y += p.vy * dt;
                p.rot += p.spin * dt;

                // Side walls: reflect, so a burst near an edge stays on screen.
                if (p.x < 0 || p.x > right) {
                    p.x = Math.min(Math.max(p.x, 0), right);
                    p.vx = -p.vx * RESTITUTION;
                }

                // Landing surfaces: the top edge of each box, and the bottom of the viewport.
                // Only a falling piece that crossed the edge this step collides — one moving
                // up passes through, which is what lets the burst rise from behind a panel.
                if (p.vy > 0) {
                    let surface = p.y >= floor ? floor : null;
                    for (const box of boxes) {
                        if (prevY <= box.top && p.y >= box.top && p.x >= box.left && p.x <= box.right) {
                            surface = box.top;
                            break;
                        }
                    }
                    if (surface !== null) {
                        p.y = surface;
                        if (p.vy < REST_SPEED) {
                            p.resting = true;
                        } else {
                            p.vy = -p.vy * RESTITUTION;
                            p.vx *= 0.7;      // friction on contact
                            p.spin *= 0.5;
                        }
                    }
                }
            }

            ctx.save();
            ctx.translate(p.x, p.y);
            ctx.rotate(p.rot);
            ctx.fillStyle = p.color;
            ctx.fillRect(-p.size / 2, -p.size / 2, p.size, p.size * 0.6);
            ctx.restore();
        }

        requestAnimationFrame(frame);
    }

    requestAnimationFrame(frame);
}

/**
 * Burst centred on an element — used for the verdict panel and the champion banner, so the
 * effect originates from the thing being celebrated rather than from the middle of the screen.
 * Falls back to the viewport centre when the selector matches nothing.
 */
export function burstFrom(selector, options = {}) {
    let origin = {};

    try {
        const element = document.querySelector(selector);
        if (element) {
            const rect = element.getBoundingClientRect();
            origin = { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
        }
    } catch {
        // Bad selector — fall through to the default centre.
    }

    burst({ ...origin, ...options });
}

/**
 * The champion's cannons: two jets from the bottom corners, angled inward so they cross over
 * the bracket and rain onto its cards. One burst with two emitters rather than two bursts, so
 * it takes one slot of MAX_ACTIVE and the champion burst still fits beside it.
 */
export function cannons(colliders) {
    const w = window.innerWidth;
    const h = window.innerHeight;
    // Fast enough to reach two-thirds of the way up a tall window before gravity wins.
    const spread = Math.min(1500, 900 + h * 0.6);
    burst({
        count: 180,
        colliders,
        durationMs: 3200,
        emitters: [
            { x: 0, y: h, direction: -Math.PI / 2 + 0.42, cone: 0.45, spread },
            { x: w, y: h, direction: -Math.PI / 2 - 0.42, cone: 0.45, spread },
        ],
    });
}

/**
 * The verdict's impact: two rings expanding from the winning panel, and a short haptic pulse
 * on phones. The rings are DOM elements animated by transform and opacity — the compositor
 * does all of it — and they remove themselves when done.
 */
export function impact(selector) {
    // The haptic is decoration too, so it obeys the same switch as the rings.
    if (motionOff()) return;
    try {
        navigator.vibrate?.([28, 40, 64]);
    } catch {
        // No vibration API, or no user activation yet: the rings alone.
    }

    if (gpuBusy() || !document.body) return;

    let rect = null;
    try {
        rect = document.querySelector(selector)?.getBoundingClientRect() ?? null;
    } catch {
        // Bad selector — no rings.
    }
    if (!rect) return;

    const size = Math.max(rect.width, rect.height) * 1.6;
    for (const delay of [0, 140]) {
        const ring = document.createElement('div');
        ring.className = 'fx-ring';
        ring.setAttribute('aria-hidden', 'true');
        ring.style.setProperty('--fx-x', `${rect.left + rect.width / 2}px`);
        ring.style.setProperty('--fx-y', `${rect.top + rect.height / 2}px`);
        ring.style.setProperty('--fx-size', `${size}px`);
        document.body.appendChild(ring);
        ring.animate(
            [
                { transform: 'translate(-50%, -50%) scale(0.15)', opacity: 0.9 },
                { transform: 'translate(-50%, -50%) scale(1)', opacity: 0 },
            ],
            { duration: 820, delay, easing: 'cubic-bezier(.15,.75,.3,1)', fill: 'both' })
            // Cancelled (navigated away) rejects `finished`; remove the ring either way.
            .finished.then(() => ring.remove(), () => ring.remove());
    }
}
