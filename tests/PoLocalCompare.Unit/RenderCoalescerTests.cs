using Microsoft.Extensions.Time.Testing;
using PoLocalCompare.Shared.Presentation;

namespace PoLocalCompare.Unit;

/// <summary>
/// The Arena feeds this from a SignalR handler that fires per token
/// batch. The contract that matters is that a burst costs one render, that an update arriving
/// after a flush is not swallowed, and that a disposed coalescer never touches a component that
/// has gone away.
/// </summary>
/// <remarks>
/// Time is a <see cref="FakeTimeProvider"/>, never the wall clock. These tests used to sleep
/// 6 intervals and assert; on a busy CI runner (run 45298257591) the timer had not fired yet
/// and the burst counted 0 renders, and a slow 200-call loop could just as well straddle the
/// interval and count 2. With a fake clock nothing fires until the test advances it, and the
/// test then waits for the render itself (a signal), not for a guessed amount of time.
/// </remarks>
public class RenderCoalescerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(30);

    /// <summary>Upper bound on waiting for a render the fake clock has already released. Only hit
    /// when the code is broken; a passing run never waits on it.</summary>
    private static readonly TimeSpan Hang = TimeSpan.FromSeconds(30);

    private sealed class Counter
    {
        private int _renders;
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Renders => Volatile.Read(ref _renders);
        public Task FirstRender => _first.Task;
        public Task SecondRender => _second.Task;

        public Task Render()
        {
            var n = Interlocked.Increment(ref _renders);
            if (n == 1) _first.TrySetResult();
            if (n == 2) _second.TrySetResult();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ABurstOfRequests_CostsASingleRender()
    {
        var clock = new FakeTimeProvider();
        var counter = new Counter();
        using var coalescer = new RenderCoalescer(counter.Render, Interval, clock);

        for (var i = 0; i < 200; i++) coalescer.Request();

        // Nothing renders inside the window, however long the loop took in real time.
        clock.Advance(Interval - TimeSpan.FromTicks(1));
        Assert.Equal(0, counter.Renders);

        clock.Advance(TimeSpan.FromTicks(1));
        await counter.FirstRender.WaitAsync(Hang);

        // Well past the window: the burst never schedules a second render.
        clock.Advance(Interval * 10);
        Assert.Equal(1, counter.Renders);

        // An update after the flush is not swallowed: it gets a render of its own.
        coalescer.Request();
        clock.Advance(Interval);
        await counter.SecondRender.WaitAsync(Hang);
        Assert.Equal(2, counter.Renders);
    }

    [Fact]
    public async Task FlushAsync_RendersImmediately()
    {
        var clock = new FakeTimeProvider();
        var counter = new Counter();
        using var coalescer = new RenderCoalescer(counter.Render, Interval, clock);

        // Terminal state (a duel finishing) must not wait out an interval: the clock never moves.
        await coalescer.FlushAsync();

        Assert.Equal(1, counter.Renders);
    }

    [Fact]
    public void AfterDispose_RequestNeverRenders()
    {
        var clock = new FakeTimeProvider();
        var counter = new Counter();
        var coalescer = new RenderCoalescer(counter.Render, Interval, clock);

        coalescer.Request();
        coalescer.Dispose();
        clock.Advance(Interval * 10);

        // The component is gone; rendering into it would throw on a disposed dispatcher.
        // The pending delay was cancelled, so advancing the clock has nothing left to release.
        Assert.Equal(0, counter.Renders);

        coalescer.Request();
        clock.Advance(Interval * 10);
        Assert.Equal(0, counter.Renders);
    }

    [Fact]
    public async Task ARenderThatThrowsObjectDisposed_DoesNotBringDownTheHandler()
    {
        // Real failure mode: the duel finishes and the Arena is disposed while a coalesced
        // render is still in flight. The exception surfaces on a background task with nobody
        // to catch it, so it has to be swallowed here.
        var clock = new FakeTimeProvider();
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coalescer = new RenderCoalescer(() =>
        {
            attempted.TrySetResult();
            throw new ObjectDisposedException("renderer");
        }, Interval, clock);

        coalescer.Request();
        clock.Advance(Interval);
        await attempted.Task.WaitAsync(Hang);

        await coalescer.FlushAsync();
    }
}
