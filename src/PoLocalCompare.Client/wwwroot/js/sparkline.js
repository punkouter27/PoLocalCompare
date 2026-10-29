/**
 * sparkline.js — the Arena telemetry HUD's tok/s history, drawn once per update into a small
 * canvas. Not a render loop: it paints when C# hands it new samples and is idle otherwise.
 *
 * Imported through [JSImport] (Services/Sparkline.cs), not IJSRuntime: the samples arrive as a
 * MemoryView over the C# buffer — no JSON array is built or parsed per update. The view is only
 * valid during this call, so it is copied out with slice() before anything else touches it.
 */

/**
 * @param {string} canvasId the HUD's canvas
 * @param {{ slice(): Float64Array }} view tok/s samples, oldest first
 */
export function draw(canvasId, view) {
    const samples = view.slice();
    const canvas = document.getElementById(canvasId);
    if (!(canvas instanceof HTMLCanvasElement) || samples.length < 2) return;

    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const width = Math.max(1, Math.round(canvas.clientWidth * dpr));
    const height = Math.max(1, Math.round(canvas.clientHeight * dpr));
    if (canvas.width !== width) canvas.width = width;
    if (canvas.height !== height) canvas.height = height;

    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    ctx.clearRect(0, 0, width, height);

    let max = 1;
    for (const s of samples) if (s > max) max = s;
    const step = width / (samples.length - 1);
    const y = v => height - (v / max) * (height - 2 * dpr) - dpr;

    // The stroke is the token colour the stylesheet gives the canvas, so both themes hold.
    const colour = getComputedStyle(canvas).color || '#12b8cf';
    ctx.beginPath();
    ctx.moveTo(0, y(samples[0]));
    for (let i = 1; i < samples.length; i++) ctx.lineTo(i * step, y(samples[i]));
    ctx.lineWidth = 1.5 * dpr;
    ctx.strokeStyle = colour;
    ctx.lineJoin = 'round';
    ctx.stroke();

    // A faint fill under the line, the same colour at low alpha.
    ctx.lineTo(width, height);
    ctx.lineTo(0, height);
    ctx.closePath();
    ctx.globalAlpha = 0.15;
    ctx.fillStyle = colour;
    ctx.fill();
    ctx.globalAlpha = 1;
}
