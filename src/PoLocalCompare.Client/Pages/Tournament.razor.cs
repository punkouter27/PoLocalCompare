using System.Net.Http;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using PoLocalCompare.Client.Components;
using PoLocalCompare.Client.Services;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Models;
using PoLocalCompare.Shared.Presentation;
using PoLocalCompare.Shared.Prompts;
using PoLocalCompare.Shared.Tournaments;

namespace PoLocalCompare.Client.Pages;

/// <summary>
/// The code behind <c>Tournament.razor</c>: state, lifecycle and handlers. The markup stays in the
/// .razor file, along with the two Razor templates (RenderSlot, RenderPair) that are markup themselves.
/// </summary>
public partial class Tournament
{
    [Parameter] public string? TournamentIdText { get; set; }

    // ── Draw state ───────────────────────────────────────────────────────────
    /// <summary>
    /// The only bracket the page offers. The API still accepts 2 (a plain 1v1) — see
    /// <see cref="BracketPlanner.SupportedSizes"/> — but picking a size was a decision nobody
    /// wanted to make before writing a prompt, so the page draws a full bracket and gets on
    /// with it.
    /// </summary>
    private const int FieldSize = 8;
    private List<TournamentEntrantDto> _entrants = [];
    private readonly List<ModelId> _selected = [];
    private bool _choosingEntrants;

    /// <summary>Bracket by default; Swiss is the one other format, chosen beside Start.</summary>
    private TournamentFormat _format = TournamentFormat.Bracket;
    private string _prompt = string.Empty;
    private bool _loadingEntrants = true;
    private bool _starting;
    private string? _error;
    private List<TournamentDto> _recent = [];

    // Set while polls are failing, cleared by the next one that lands. A poll failure used to
    // stop polling for good, freezing the bracket with no message.
    private bool _pollFailing;

    // Dispose can run while a poll is mid-await; this stops that poll from opening a hub
    // connection after the page is gone, which nothing would ever have closed.
    private bool _disposed;

    /// <summary>
    /// The bracket the server says is already in flight when the page loads — used to gate the
    /// Start button and to deep-link the user to it from the disabled-hint line.
    /// </summary>
    /// <remarks>
    /// Detected from the recent-tournaments list the draw view already loads: a Pending or
    /// Running row at the top is the one blocking a new draw. The API also rejects a second
    /// create with a 409, so this is purely a UX gate — the authoritative check is on the server.
    /// </remarks>
    private TournamentDto? _inFlight =>
        _recent.FirstOrDefault(t =>
            t.Status is TournamentStatus.Pending or TournamentStatus.Running);

    // ── Bracket state ────────────────────────────────────────────────────────
    private TournamentDto? _tournament;
    private bool _loadingTournament;
    private PeriodicTimer? _pollTimer;
    private CancellationTokenSource? _pollCts;

    // ── Browser-model matches ────────────────────────────────────────────────
    // A WebGPU model cannot run on the server, so for those matches this page plays the role
    // the Arena plays for a single duel: join the running duel's hub group, answer
    // StartLocalInference, and POST the result back. The hub follows the running match rather
    // than the tournament, because StartLocalInference is addressed to `duel:{duelId}`.
    private SignalRDuelClient? _hub;
    private DuelId? _hubDuelId;
    private LocalInferenceDriver? _localInference;
    private bool _drivingLocalMatch;

    /// <summary>
    /// How often the bracket is re-read while it runs. The server owns the run, so this is a
    /// viewer refresh rather than a control loop — slow enough not to hammer storage, fast
    /// enough that a match finishing is visible while you are still looking at it.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private const int MinPromptLength = 10;

    private ElementReference _promptBox;

    // Set by a Start click that had no usable prompt; PromptFlagged drops on its own once the
    // prompt is long enough, so nothing has to remember to clear it.
    private bool _promptNudged;

    private bool PromptReady => _prompt.Trim().Length >= MinPromptLength;

    private bool PromptFlagged => _promptNudged && !PromptReady;

    /// <summary>
    /// Whether the Start button is pressable. A missing prompt is deliberately NOT part of this.
    /// </summary>
    /// <remarks>
    /// The button used to be disabled for an empty prompt too, with the reason in a muted hint
    /// beside it — and a disabled button cannot be clicked to find out why. The things the user
    /// cannot fix from the button (field not full, a bracket already running) still disable it;
    /// a missing prompt is fixed one field up, so the click goes there instead.
    /// </remarks>
    private bool CanAttemptStart => _selected.Count == FieldSize
        && _inFlight is null
        && !_starting;

    private string StartHint
    {
        get
        {
            if (_inFlight is { } live)
                return $"A tournament is already running — finish or abandon it before starting a new one.";

            return (_selected.Count == FieldSize, PromptReady) switch
            {
                (false, _) when _entrants.Count < FieldSize => "Not enough models registered to fill a bracket.",
                (false, _) => $"Choose exactly {FieldSize} entrants ({_selected.Count} selected).",
                (true, false) when _prompt.Trim().Length == 0 => "Write a prompt above to start the bracket.",
                (true, false) => $"The prompt needs at least {MinPromptLength} characters.",
                _ when _format == TournamentFormat.Swiss =>
                    $"{FieldSize} models · {SwissPlanner.RoundCount} rounds · {SwissPlanner.MatchCount} duels · judged automatically.",
                _ => $"{FieldSize} models · {FieldSize - 1} duels · judged automatically.",
            };
        }
    }

    /// <summary>
    /// Accessible label for a match card. Spells out who is playing and the link's action so a
    /// screen reader user hears what a sighted user sees, rather than a bare "link" announcement.
    /// </summary>
    private static string MatchAriaLabel(TournamentMatchDto match) => match.DuelId is null
        ? $"{match.SlotAName} versus {match.SlotBName}"
        : match.IsDecided
            ? $"{match.SlotAName} versus {match.SlotBName} — view the decided duel"
            : match.IsRunning
                ? $"{match.SlotAName} versus {match.SlotBName} — watch the live duel"
                : $"{match.SlotAName} versus {match.SlotBName} — view the duel";

    /// <summary>The drawn field, in draw order, resolved back to entrant rows.</summary>
    private IEnumerable<TournamentEntrantDto> DrawnField =>
        _selected.Select(id => _entrants.First(e => e.ModelId == id));

    /// <summary>One first-round pairing: the two entrants and their seed numbers.</summary>
    private sealed record Pairing(
        int SeedA, TournamentEntrantDto A,
        int SeedB, TournamentEntrantDto B);

    /// <summary>
    /// The four first-round matches, in bracket order.
    /// </summary>
    /// <remarks>
    /// Mirrors what <c>CreateTournamentHandler</c> will do on the server: seed the field by
    /// rating, strongest first, then lay it out through <see cref="BracketPlanner.SeedOrder"/>
    /// (or, for Swiss, <see cref="SwissPlanner.FirstRoundSeeds"/>). Reusing the planners rather
    /// than hard-coding 1v8/4v5/2v7/3v6 is the point — if the seeding convention ever changes,
    /// this preview cannot drift away from the draw it previews.
    /// </remarks>
    private IReadOnlyList<Pairing> FirstRoundPairs
    {
        get
        {
            if (_selected.Count < FieldSize) return [];

            var seeded = DrawnField
                .OrderByDescending(e => e.CurrentElo)
                .ThenBy(e => e.DisplayName, StringComparer.Ordinal)
                .ToList();

            IEnumerable<(int SeedA, int SeedB)> seeds;
            if (_format == TournamentFormat.Swiss)
            {
                seeds = SwissPlanner.FirstRoundSeeds();
            }
            else
            {
                var order = BracketPlanner.SeedOrder(FieldSize);
                seeds = Enumerable.Range(0, order.Count / 2).Select(i => (order[i * 2], order[i * 2 + 1]));
            }

            // Seeds are 1-based; the seeded list is 0-based.
            return seeds
                .Select(s => new Pairing(s.SeedA, seeded[s.SeedA - 1], s.SeedB, seeded[s.SeedB - 1]))
                .ToList();
        }
    }

    private bool IsSwiss => _tournament?.Format == TournamentFormat.Swiss;

    private int RoundCount => _tournament is null
        ? 0
        : IsSwiss ? SwissPlanner.RoundCount : BracketPlanner.RoundCount(_tournament.Size);

    /// <summary>Swiss rounds not yet paired — each is drawn by score when the one before it finishes.</summary>
    private IEnumerable<int> UnpairedSwissRounds => IsSwiss
        ? Enumerable.Range(0, SwissPlanner.RoundCount).Where(r => _tournament!.Matches.All(m => m.Round != r))
        : [];

    /// <summary>"2", "1.5" — Swiss points and Buchholz are whole or half numbers.</summary>
    private static string FormatPoints(double points) =>
        points.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private TournamentMatchDto? RunningMatch => _tournament?.Matches.FirstOrDefault(m => m.IsRunning);

    private bool OnChampionPath(TournamentMatchDto match) =>
        _tournament is { Status: TournamentStatus.Complete, ChampionModelId: { } champion }
        && match.WinnerModelId == champion;

    /// <summary>
    /// A match's Arena link, carrying the bracket it came from. A duel row does not record its
    /// tournament, so the query string is the only way the Arena knows to offer the way back.
    /// </summary>
    private string DuelUrl(DuelId duelId) =>
        $"/arena/{duelId}?tournament={Uri.EscapeDataString(_tournament!.TournamentId.Value)}";

    private static string SizeLabel(TournamentDto tournament) => tournament switch
    {
        { Format: TournamentFormat.Swiss } => $"{tournament.Size}-model Swiss",
        { Size: 2 } => "1v1",
        _ => $"{tournament.Size}-model bracket",
    };

    private static string StatusLabel(TournamentDto tournament) => tournament.Status switch
    {
        TournamentStatus.Pending => "Drawn",
        TournamentStatus.Running => "Running",
        TournamentStatus.Complete => "Complete",
        _ => "Stopped",
    };

    private static string MatchClass(TournamentMatchDto match) =>
        match switch
        {
            { FailureReason: not null } => "tourney__match--failed",
            { IsDecided: true } => "tourney__match--decided",
            { IsRunning: true } => "tourney__match--running",
            // A ready match looks like the base card, so it gets no modifier at all — a class
            // whose rule restates the default is the kind of dead weight this codebase keeps
            // accumulating. Pending is the one that differs: it is dimmed, because it is
            // waiting on a winner that does not exist yet.
            { IsReady: true } => "",
            _ => "tourney__match--pending",
        };

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max].TrimEnd() + "…";

    protected override async Task OnParametersSetAsync()
    {
        // OnParametersSetAsync rather than OnInitializedAsync: navigating from the draw to a
        // bracket keeps this component instance, so an initialiser would never run for it.
        StopPolling();

        if (string.IsNullOrWhiteSpace(TournamentIdText))
        {
            _tournament = null;
            await LoadDrawAsync();
            return;
        }

        await LoadBracketAsync();
    }

    private async Task LoadBracketAsync()
    {
        StopPolling();
        _loadingTournament = true;
        _tournament = null;
        _error = null;

        try
        {
            _tournament = await ApiClient.GetTournamentAsync(TournamentId.From(TournamentIdText!));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _error = $"Could not load the bracket: {ex.Message}";
        }
        finally
        {
            _loadingTournament = false;
        }

        if (_tournament is { Status: TournamentStatus.Pending or TournamentStatus.Running })
            StartPolling();
    }

    private async Task LoadDrawAsync()
    {
        _loadingEntrants = true;

        try
        {
            var entrantsTask = ApiClient.GetTournamentEntrantsAsync();
            var recentTask = ApiClient.ListTournamentsAsync(5);

            _entrants = (await entrantsTask)?.ToList() ?? [];
            _recent = (await recentTask)?.ToList() ?? [];
            DrawField();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _error = $"Could not load the model list: {ex.Message}";
        }
        finally
        {
            _loadingEntrants = false;
        }
    }

    /// <summary>Draws eight models at random, preferring remote ones.</summary>
    /// <remarks>
    /// Remote first because they are the only type that needs nothing from this machine — no
    /// GPU, no daemon, no tab left open — so a random draw over them is a bracket that will
    /// actually finish. Ollama and browser models only top the field up when there are not
    /// eight remotes to draw from, and the page says so when that happens. Random rather than
    /// top-eight-by-rating: the seeding already decides who meets whom, so taking the top eight
    /// every time would just mean the same bracket over and over.
    /// </remarks>
    private void DrawField()
    {
        _selected.Clear();
        _choosingEntrants = false;
        if (_entrants.Count == 0) return;

        var remote = _entrants.Where(e => e.ModelType == ModelType.Remote).ToArray();
        var rest = _entrants.Where(e => e.ModelType != ModelType.Remote).ToArray();

        Random.Shared.Shuffle(remote);
        Random.Shared.Shuffle(rest);

        _selected.AddRange(remote.Concat(rest).Take(FieldSize).Select(e => e.ModelId));
    }

    private void SetEntrantSelected(ModelId modelId, bool selected)
    {
        if (selected)
        {
            if (_selected.Count < FieldSize && !_selected.Contains(modelId))
                _selected.Add(modelId);
            return;
        }

        _selected.Remove(modelId);
    }

    private async Task StartAsync()
    {
        if (!CanAttemptStart) return;

        if (!PromptReady)
        {
            _promptNudged = true;
            await _promptBox.FocusAsync();
            return;
        }

        _starting = true;
        _error = null;

        try
        {
            await JS.InvokeVoidAsync("poNotify.ask");
            _ = AudioService.PlayLaunchAsync();
            var drawn = await ApiClient.CreateTournamentAsync(_selected, _prompt.Trim(), _format);

            if (drawn is not null)
                Nav.NavigateTo($"/tournament/{drawn.TournamentId}");
            else
                _error = "Could not draw the bracket. Please try again.";
        }
        catch (DuelApiClient.TournamentInFlightException ex) when (ex.RunningTournamentId is { } running)
        {
            // The server has the authoritative "one at a time" gate. Surface the running
            // bracket as an error AND offer to take the user there, so the rejection is
            // navigable rather than just informative.
            _error = ex.Message;
            Nav.NavigateTo($"/tournament/{running}");
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _starting = false;
        }
    }

    // ── Polling ──────────────────────────────────────────────────────────────

    private void StartPolling()
    {
        _pollCts = new CancellationTokenSource();
        _pollTimer = new PeriodicTimer(PollInterval);
        _ = PollAsync(_pollTimer, _pollCts.Token);
    }

    /// <summary>
    /// Keeps the hub connection pointed at whichever match is currently running, so a browser
    /// model in that match gets driven from this tab. A no-op for a bracket of remote/Ollama
    /// models: the server never sends StartLocalInference for those, so the connection sits idle.
    /// </summary>
    /// <remarks>
    /// Reconnecting per match rather than holding one connection is deliberate — the server
    /// addresses StartLocalInference to the duel group, and a bracket is seven different duels.
    /// The server re-sends that signal every 5 s, so joining a match already in flight still
    /// picks it up rather than deadlocking on a missed first send.
    /// </remarks>
    private async Task EnsureHubForRunningMatchAsync()
    {
        var runningDuelId = RunningMatch?.DuelId;

        if (runningDuelId == _hubDuelId) return;

        await DisconnectHubAsync();
        if (runningDuelId is null) return;

        try
        {
            _localInference ??= new LocalInferenceDriver(WebLlm, ApiClient);
            _hub = new SignalRDuelClient();
            _hub.OnStartLocalInference += OnStartLocalInference;
            await _hub.ConnectAsync(Nav.BaseUri.TrimEnd('/'), runningDuelId.Value);
            _hubDuelId = runningDuelId;
            if (_disposed) await DisconnectHubAsync();
        }
        catch (Exception)
        {
            // A hub that will not connect costs browser matches, not the bracket. The next poll
            // retries; remote and Ollama matches finish regardless.
            await DisconnectHubAsync();
        }
    }

    private void OnStartLocalInference(StartLocalInferencePayload payload)
    {
        // A reconnect replays this signal, and so does the server's 5-second resend; the driver
        // owns the de-duplication.
        if (_localInference is null || !_localInference.TryClaim(payload.ModelId)) return;

        _drivingLocalMatch = true;
        _ = InvokeAsync(StateHasChanged);

        _ = Task.Run(async () =>
        {
            // Every failure path is handled inside the driver and reported to the server, so
            // there is no outcome here worth awaiting beyond clearing the banner.
            await _localInference.RunAsync(payload);
            _drivingLocalMatch = false;
            await InvokeAsync(StateHasChanged);
        });
    }

    private async Task DisconnectHubAsync()
    {
        if (_hub is not null)
        {
            _hub.OnStartLocalInference -= OnStartLocalInference;
            await _hub.DisposeAsync();
            _hub = null;
        }
        _hubDuelId = null;
    }

    private async Task PollAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                TournamentDto? fresh;
                try
                {
                    fresh = await ApiClient.GetTournamentAsync(TournamentId.From(TournamentIdText!));
                }
                catch (HttpRequestException)
                {
                    // A failed poll is not a failed run — the bracket is server-side. Keep the
                    // last good view, say so, and let the next tick retry.
                    if (!_pollFailing) { _pollFailing = true; await InvokeAsync(StateHasChanged); }
                    continue;
                }
                if (cancellationToken.IsCancellationRequested) return;
                _pollFailing = false;
                if (fresh is null) continue;

                var wasComplete = _tournament?.Status == TournamentStatus.Complete;
                var advanced = NewlyAdvanced(_tournament, fresh);
                _tournament = fresh;
                await EnsureHubForRunningMatchAsync();
                await InvokeAsync(StateHasChanged);

                // After the render, so the slot being animated INTO exists: each winner flies
                // from the match it just won to its place in the next round. Decoration — the
                // bracket is already correct on screen without it.
                if (advanced.Count > 0)
                {
                    _ = AudioService.PlayTickAsync();
                    try { await JS.InvokeVoidAsync("poWow.advance", cancellationToken, advanced); }
                    catch (Exception ex) when (ex is JSException or JSDisconnectedException) { }
                }

                if (fresh.Status is TournamentStatus.Complete or TournamentStatus.Abandoned)
                {
                    // One flourish on the transition only — a poll that re-reads a finished
                    // bracket must not replay it. Safe at this point by construction: the run
                    // is over, so nothing is competing for the GPU.
                    if (!wasComplete)
                    {
                        if (fresh.Status == TournamentStatus.Complete)
                        {
                            _ = AudioService.PlayFanfareAsync(fresh.TournamentId.ToString());
                            _ = CelebrateChampionAsync(fresh.ChampionName);
                            await JS.InvokeVoidAsync("poNotify.notify", "Tournament complete", $"{fresh.ChampionName} wins the {SizeLabel(fresh)}.");
                        }
                        else
                        {
                            await JS.InvokeVoidAsync("poNotify.notify", "Tournament abandoned", "The bracket could not finish.");
                        }
                    }

                    StopPolling();
                    await DisconnectHubAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Navigated away.
        }
        catch (Exception)
        {
            // Anything other than a network failure (those are retried above) is unexpected:
            // stop refreshing rather than replacing what is on screen with an error about it.
            StopPolling();
        }
    }

    /// <summary>
    /// The champion's moment, after the fanfare has started: the GPU storm swarms into the
    /// name and bursts onto the bracket's cards. Where it cannot run (no WebGPU, reduced
    /// motion, the GPU lease held) the Canvas2D corner cannons and the burst over the banner
    /// play instead — the cards are what the confetti lands on either way.
    /// </summary>
    private async Task CelebrateChampionAsync(string? championName)
    {
        try
        {
            if (championName is not null && await Fx.ChampionStormAsync(championName, ".tourney__match")) return;
            _ = Fx.CannonsAsync(".tourney__match");
            await Fx.ChampionBurstAsync(".tourney__champion", colliders: ".tourney__match");
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or ObjectDisposedException)
        {
            // Decoration only.
        }
    }

    /// <summary>
    /// The models that appeared in a later-round slot between two reads of the bracket — the
    /// winners that have just advanced. Keyed on the slot being filled rather than on the match
    /// being decided, so it is right whether the runner writes both in one step or two.
    /// </summary>
    private static List<string> NewlyAdvanced(TournamentDto? before, TournamentDto after)
    {
        // Nobody advances in Swiss: a new round is a fresh pairing, not a winner moving on.
        if (before is null || after.Matches.Count == 0 || after.Format == TournamentFormat.Swiss) return [];

        var firstRound = after.Matches.Min(m => m.Round);
        var filled = before.Matches
            .SelectMany(m => new[] { (m.Round, m.Index, Model: m.SlotAModelId), (m.Round, m.Index, Model: m.SlotBModelId) })
            .ToHashSet();

        return after.Matches
            .Where(m => m.Round > firstRound)
            .SelectMany(m => new[] { (m.Round, m.Index, Model: m.SlotAModelId), (m.Round, m.Index, Model: m.SlotBModelId) })
            .Where(slot => !slot.Model.IsEmpty && !filled.Contains(slot))
            .Select(slot => slot.Model.Value)
            .ToList();
    }

    private void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    public void Dispose()
    {
        _disposed = true;
        StopPolling();
        // Fire-and-forget: IDisposable cannot await, and the hub's own dispose is best-effort.
        // A browser match already in flight keeps running until its worker finishes, which is
        // what we want — navigating away mid-match should not strand the duel.
        _ = DisconnectHubAsync();
    }
}
