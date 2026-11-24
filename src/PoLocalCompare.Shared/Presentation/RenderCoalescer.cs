namespace PoLocalCompare.Shared.Presentation;

/// <summary>
/// Collapses a burst of re-render requests onto a fixed cadence.
/// </summary>
/// <remarks>
/// The Arena re-rendered once per token batch. Every one of those renders walked the whole page
/// — two sandboxed iframes, the scorecard, the telemetry HUD — to move a token counter, and a
/// fast remote model emits batches far quicker than a frame. <see cref="Request"/> is therefore
/// trailing-edge: the first call schedules a render one interval out and every call inside that
/// window is free, so N updates in an interval cost exactly one render instead of N.
///
/// Terminal events (a duel completing, a verdict landing) call <see cref="FlushAsync"/> instead,
/// which paints immediately — waiting out an interval to show a final result would be a visible
/// lag on the one frame that matters.
///
/// Lives in Shared rather than beside the Arena so it is reachable from the Unit tier; anything
/// under Client/ can only be tested by E2E-UI, which CI never runs.
/// </remarks>
public sealed class RenderCoalescer : IDisposable
{
    /// <summary>~60 fps. Fast enough that a token counter still looks live, slow enough that a
    /// model emitting hundreds of batches a second cannot saturate the render loop.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(16);

    private readonly Func<Task> _render;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>0 = no render scheduled, 1 = one is already pending.</summary>
    private int _pending;
    private bool _disposed;

    /// <param name="time">The clock the interval is measured on. Production passes nothing and
    /// gets the system clock; the Unit tier passes a fake clock so "one render per burst" is
    /// proven by moving time explicitly rather than by sleeping and hoping the timer fired.</param>
    public RenderCoalescer(Func<Task> render, TimeSpan? interval = null, TimeProvider? time = null)
    {
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _interval = interval ?? DefaultInterval;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Asks for a render soon. Cheap and safe to call from any thread on every update; calls
    /// that arrive while a render is already scheduled are absorbed into it.
    /// </summary>
    public void Request()
    {
        if (_disposed) return;

        // Only the caller that flips 0 → 1 schedules the flush. Everyone else has just been
        // folded into the render that caller already queued.
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;

        _ = FlushAfterDelayAsync();
    }

    private async Task FlushAfterDelayAsync()
    {
        try
        {
            await Task.Delay(_interval, _time, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Cleared before rendering, not after: an update that lands *during* the render must
        // schedule another one rather than being swallowed by the render already in flight.
        Interlocked.Exchange(ref _pending, 0);

        try
        {
            await _render().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The component went away mid-flush. Nothing to paint.
        }
    }

    /// <summary>
    /// Renders now, cancelling any pending coalesced render. For terminal state that must not
    /// wait out an interval.
    /// </summary>
    public async Task FlushAsync()
    {
        if (_disposed) return;

        Interlocked.Exchange(ref _pending, 0);

        try
        {
            await _render().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
