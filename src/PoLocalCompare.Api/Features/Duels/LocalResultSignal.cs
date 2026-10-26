// GoF: Observer — the local-result endpoint wakes the one wait that is listening for it
using System.Collections.Concurrent;

namespace PoLocalCompare.Api.Features.Duels;

/// <summary>
/// Wakes the server-side wait for a browser model's result the moment the tab posts it.
/// </summary>
/// <remarks>
/// Until 2026-10-26 <see cref="DuelExecutionService"/> found a browser result by reading Table
/// Storage every 500 ms for as long as the tab took — minutes, for a small model on a cold GPU —
/// and found it up to half a second late. Now the endpoint signals and the wait reads storage
/// once. In-process only: a post that lands on another instance is still picked up by the
/// wait's five-second storage check, so the signal makes the common case fast and the check
/// keeps the rare one correct.
/// </remarks>
public sealed class LocalResultSignal
{
    private readonly ConcurrentDictionary<(DuelId, ModelId), TaskCompletionSource> _waiters = new();

    /// <summary>A task that completes when <see cref="Notify"/> is called for this pair.</summary>
    public Task Register(DuelId duelId, ModelId modelId) =>
        _waiters.GetOrAdd((duelId, modelId),
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public void Notify(DuelId duelId, ModelId modelId)
    {
        if (_waiters.TryRemove((duelId, modelId), out var waiter)) waiter.TrySetResult();
    }

    public void Forget(DuelId duelId, ModelId modelId) => _waiters.TryRemove((duelId, modelId), out _);
}
