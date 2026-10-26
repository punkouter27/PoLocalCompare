using Microsoft.JSInterop;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// JS interop wrapper for the synthesised Web Audio in <c>wwwroot/js/audio.js</c>: the versus
/// slam, each model's motif, the race as two panned grain streams, the first-token spark, the
/// judge's deliberation, the verdict, the ELO odometer, the upset stinger, the champion's
/// fanfare, the click tick and the judge window's closing seconds.
/// </summary>
/// <remarks>
/// Every cue is synthesised at play time — there are no audio assets. The previous version
/// fetched two WAV files that were 44-byte stubs (a RIFF header with a zero-length data chunk),
/// so every "sound" the app played was silence and had been since the cues were added. Nothing
/// here can fail that way again: there is no file to be present-but-empty.
///
/// It failed a second way after that, just as silently: until 2026-09-22 every call was
/// <c>InvokeVoidAsync("import(&quot;/js/audio.js?v=N&quot;).then(m => …)")</c>, which Blazor does not
/// evaluate — it walks <c>window</c> for an identifier split on '.', throws "Could not find",
/// and the catch below swallowed it. No cue ever played and the mute toggle never persisted.
/// The module is now imported once as an <see cref="IJSObjectReference"/>.
///
/// Every method swallows its own failures. Audio is decoration — a browser with no
/// <c>AudioContext</c>, or a page the user has not yet interacted with (autoplay policy blocks
/// a context until a gesture), must degrade to silence rather than taking a duel down.
///
/// The <c>?v=</c> on the import is the same cache-buster trap the rest of this app's JS carries:
/// without bumping it, a browser that has the old module cached serves the old module and edits
/// here appear to do nothing.
///
/// The 2026-09-26 prune cut an earlier drone, token melodies and fanfares as decoration nobody
/// asked for. Their successors here were asked for on 2026-10-26 (HISTORY.md, "G1–G10").
/// </remarks>
public sealed class AudioService(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "/js/audio.js?v=10";

    private Task<IJSObjectReference>? _module;

    /// <summary>The Arena opening on a live duel: a whoosh into the VS slam's thump.</summary>
    public Task PlayVersusAsync() => CallAsync("playVersus");

    /// <summary>
    /// The model's signature: four notes derived from its id, voiced by how it runs — a plucked
    /// string (browser), an FM bell (remote) or a chiptune square (Ollama).
    /// </summary>
    public Task PlayMotifAsync(ModelId modelId, ModelType type, double pan) =>
        CallAsync("playMotif", modelId.Value, MotifVoice(type), pan);

    /// <summary>
    /// The race as two panned grain streams whose rate and pitch follow each side's tok/s. Call
    /// it as telemetry arrives (throttled); a side marked done plays its finish once.
    /// </summary>
    public Task RaceAsync(double leftTps, double rightTps, bool leftDone, bool rightDone) =>
        CallAsync("raceUpdate", leftTps, rightTps, leftDone, rightDone);

    /// <summary>Silences the race at once — the page is leaving or the duel completed.</summary>
    public Task StopRaceAsync() => CallAsync("raceStop");

    /// <summary>A side's first token: a rising chirp from that side.</summary>
    public Task PlaySparkAsync(double pan) => CallAsync("playSpark", pan);

    /// <summary>
    /// Two voices a few hertz apart, beating while the AI judge decides. Resolved by
    /// <see cref="PlayVerdictAsync"/>; stopped outright by <see cref="StopDeliberationAsync"/>.
    /// </summary>
    public Task StartDeliberationAsync() => CallAsync("deliberationStart");

    /// <summary>Fades the deliberation without resolving it (a human picked, or the judge declined).</summary>
    public Task StopDeliberationAsync() => CallAsync("deliberationStop");

    /// <summary>
    /// The verdict: resolves a running deliberation, then the impact and the winner's motif from
    /// its side (or the unresolved tie cue). Returns the lead-in in milliseconds — how long to
    /// hold the visual impact so it lands with the sound. 0 when audio is unavailable.
    /// </summary>
    public async Task<int> PlayVerdictAsync(bool tie, double pan, ModelId? winner, ModelType? winnerType)
    {
        try
        {
            return await (await ModuleAsync()).InvokeAsync<int>(
                "playVerdict", tie, pan, winner?.Value ?? string.Empty, MotifVoice(winnerType ?? ModelType.Remote));
        }
        catch (Exception ex) when (IsBenign(ex))
        {
            ResetIfFaulted();
            return 0;
        }
    }

    /// <summary>Clicks under the rolling ELO reels — rising on the winner's side, falling on the loser's.</summary>
    public Task PlayOdometerAsync(double winnerPan) => CallAsync("playOdometer", winnerPan);

    /// <summary>A lower-rated model won: a minor stab after the odometer settles.</summary>
    public Task PlayUpsetAsync() => CallAsync("playUpset");

    /// <summary>The champion's fanfare and crowd, seeded by the tournament id so each bracket's differs.</summary>
    public Task PlayFanfareAsync(string seed) => CallAsync("playFanfare", seed);

    /// <summary>One rising note for each of the judge window's last three seconds.</summary>
    public Task PlayCountdownAsync(int remaining) => CallAsync("playCountdown", remaining);

    /// <summary>Short UI tick for a click. Quiet on purpose — it fires often.</summary>
    public Task PlayTickAsync() => CallAsync("playTick");

    private static string MotifVoice(ModelType type) => type switch
    {
        ModelType.Local => "browser",
        ModelType.LocalService => "ollama",
        _ => "remote",
    };

    /// <summary>Reads the persisted mute preference.</summary>
    public async Task<bool> IsMutedAsync()
    {
        try
        {
            return await (await ModuleAsync()).InvokeAsync<bool>("isMuted");
        }
        catch (Exception ex) when (IsBenign(ex))
        {
            ResetIfFaulted();
            return false;
        }
    }

    /// <summary>Sets and persists the mute preference. Returns the value actually applied.</summary>
    public async Task<bool> SetMutedAsync(bool muted)
    {
        try
        {
            return await (await ModuleAsync()).InvokeAsync<bool>("setMuted", muted);
        }
        catch (Exception ex) when (IsBenign(ex))
        {
            ResetIfFaulted();
            return muted;
        }
    }

    private Task<IJSObjectReference> ModuleAsync() =>
        _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    private async Task CallAsync(string identifier, params object?[] args)
    {
        try
        {
            await (await ModuleAsync()).InvokeVoidAsync(identifier, args);
        }
        catch (Exception ex) when (IsBenign(ex))
        {
            // No AudioContext, autoplay not yet unlocked, or the component was disposed
            // mid-call. All three are "no sound", none is an error worth surfacing.
            ResetIfFaulted();
        }
    }

    private static bool IsBenign(Exception ex) =>
        ex is JSException or TaskCanceledException or InvalidOperationException or JSDisconnectedException;

    /// <summary>A failed import is retried on the next call rather than cached for the session.</summary>
    private void ResetIfFaulted()
    {
        if (_module is { IsFaulted: true }) _module = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is { IsCompletedSuccessfully: true })
        {
            try
            {
                await _module.Result.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Tab closing.
            }
        }
    }
}
