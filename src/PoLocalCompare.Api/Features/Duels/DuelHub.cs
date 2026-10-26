// GoF: Observer — server pushes state changes to subscribed clients
using Microsoft.AspNetCore.SignalR;

namespace PoLocalCompare.Api.Features.Duels;

/// <remarks>
/// Server→client pushes go through <c>IHubContext&lt;DuelHub&gt;</c> (see
/// <see cref="DuelExecutionService"/>); the hub itself exposes only the client-invokable
/// join. Broadcast methods must not live here — being hub methods would let any
/// authenticated client push arbitrary status into any duel's group.
/// </remarks>
public sealed class DuelHub : Hub
{
    /// <summary>Client calls this to join a duel's broadcast group.</summary>
    public async Task JoinDuel(DuelId duelId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"duel:{duelId}");
    }
}
