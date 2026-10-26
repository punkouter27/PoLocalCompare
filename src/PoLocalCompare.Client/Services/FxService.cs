using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// JS interop wrapper for the visual effects: the confetti, cannons and verdict shockwave in
/// <c>wwwroot/js/fx.js</c>, and the two shader effects — the verdict nebula and the champion
/// storm — in <c>wwwroot/js/gpu-fx.js</c>.
/// </summary>
/// <remarks>
/// <para>
/// Browser models run WebLLM inference over WebGPU in this same tab, and the tok/s the race
/// reports is measured while that is happening. So the burst checks the GPU lease
/// (<c>window.poGpuLease</c>, held by <c>webllm-interop.js</c> for the life of a worker) and does
/// nothing while it is held. Callers do not gate that, nor reduced motion.
/// </para>
/// <para>
/// <b>The module is imported as an <see cref="IJSObjectReference"/>.</b> Until 2026-09-22
/// every call here was <c>InvokeVoidAsync("import(&quot;/js/fx.js?v=N&quot;).then(m => …)")</c>. Blazor does
/// not evaluate that: it splits the identifier on '.' and walks <c>window</c>, so it looked up
/// <c>window[&quot;import(&quot;/js/fx&quot;]</c>, threw "Could not find", and the catch below swallowed it.
/// No effect in the app had ever run from Blazor. The <c>?v=</c> cache-buster still applies.
/// </para>
/// <para>
/// <b>gpu-fx.js is a separate module, imported only when a shader effect is first asked for.</b>
/// It is the one place the app runs a WebGPU/WebGL2 render loop (CLAUDE.md, "Motion"): both
/// effects check the same lease, and also tear down mid-frame the moment a worker takes it.
/// </para>
/// </remarks>
public sealed class FxService(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "/js/fx.js?v=10";
    private const string GpuModulePath = "/js/gpu-fx.js?v=2";

    private Task<IJSObjectReference>? _module;
    private Task<IJSObjectReference>? _gpuModule;

    /// <summary>
    /// Fires a burst centred on the first element matching <paramref name="selector"/>, falling
    /// back to the viewport centre when nothing matches.
    /// </summary>
    /// <param name="count">Particle count. The default suits a duel verdict.</param>
    /// <param name="colliders">
    /// Selector for the elements the pieces land on and bounce off. Like <paramref name="selector"/>
    /// it is a CSS class used from JS: one that matches nothing just means nothing to land on.
    /// </param>
    public Task BurstFromAsync(string selector, int count = 90, string? colliders = null) =>
        CallAsync("burstFrom", selector, new { count, colliders });

    /// <summary>A larger burst for a tournament champion — the final should outweigh a duel.</summary>
    public Task ChampionBurstAsync(string selector, string? colliders = null) =>
        BurstFromAsync(selector, count: 160, colliders);

    /// <summary>Two confetti jets from the bottom corners, crossing over and landing on <paramref name="colliders"/>.</summary>
    public Task CannonsAsync(string colliders) => CallAsync("cannons", colliders);

    /// <summary>The verdict's shockwave rings from <paramref name="selector"/>, and a haptic pulse on phones.</summary>
    public Task ImpactAsync(string selector) => CallAsync("impact", selector);

    /// <summary>
    /// The verdict nebula — shader smoke blooming from <paramref name="selector"/>. WebGPU, else
    /// WebGL2, else nothing.
    /// </summary>
    public Task NebulaAsync(string selector) => CallGpuAsync("nebula", selector);

    /// <summary>
    /// The champion storm: particles swarm into <paramref name="name"/>, then burst onto
    /// <paramref name="colliders"/>. Returns false when it did not run (no WebGPU, the GPU lease
    /// is held, reduced motion) so the caller can fire the Canvas2D cannons instead.
    /// </summary>
    public Task<bool> ChampionStormAsync(string name, string colliders) =>
        CallGpuAsync("championStorm", name, colliders);

    private async Task<bool> CallGpuAsync(string identifier, params object?[] args)
    {
        try
        {
            _gpuModule ??= js.InvokeAsync<IJSObjectReference>("import", GpuModulePath).AsTask();
            var module = await _gpuModule;
            return await module.InvokeAsync<bool>(identifier, args);
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or InvalidOperationException or JSDisconnectedException)
        {
            // Decoration, same as below — and "it did not run" is the honest answer.
            if (_gpuModule is { IsFaulted: true }) _gpuModule = null;
            return false;
        }
    }

    private async Task CallAsync(string identifier, params object?[] args)
    {
        try
        {
            _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();
            var module = await _module;
            await module.InvokeVoidAsync(identifier, args);
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or InvalidOperationException or JSDisconnectedException)
        {
            // Decoration. A browser without Canvas2D, a failed module fetch, or a component
            // disposed mid-call simply does not celebrate. A failed import is not cached: the
            // next call retries rather than staying dark for the session.
            if (_module is { IsFaulted: true }) _module = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var module in new[] { _module, _gpuModule })
        {
            if (module is not { IsCompletedSuccessfully: true }) continue;
            try
            {
                await module.Result.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Tab closing.
            }
        }
    }
}
