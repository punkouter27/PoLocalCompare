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

namespace PoLocalCompare.Client.Pages;

/// <summary>
/// The code behind <c>Arena.razor</c>: state, lifecycle and handlers. The markup stays in the
/// .razor file.
/// </summary>
public partial class Arena
{
    // Blazor route parameters must be one of its supported primitive types, so the route binds
    // text and it is narrowed to DuelId here instead of at every call site.
    [Parameter] public string DuelIdText { get; set; } = string.Empty;

    // Derived, never cached in a field. It used to matter because the load ran in
    // OnInitializedAsync, which Blazor runs *before* OnParametersSet — a field set there was
    // still default() during the initial load, which sent GET /api/duels/ with an empty id,
    // matched the list route, and blew up deserializing a JSON array into one DuelDto. The load
    // now runs in OnParametersSetAsync (see there), and derived is still the safe shape: a
    // cached copy is one more thing a duel-id change would have to remember to reset.
    private DuelId _duelId => DuelId.From(DuelIdText);

    /// <summary>
    /// The route id the page last loaded. "Retry duel" navigates to /arena/{new id} — the same
    /// route — so Blazor keeps this component and only sets parameters again; OnInitializedAsync
    /// never re-runs. Loading is keyed on a change of this instead.
    /// </summary>
    private string? _loadedDuelIdText;

    /// <summary>
    /// The bracket this duel was opened from, carried as <c>?tournament=</c> by the Tournament
    /// page's match links. A duel row does not record its tournament, so without the query the
    /// Arena cannot know it is a bracket match — opened from the Archive it is just a duel.
    /// </summary>
    [SupplyParameterFromQuery(Name = "tournament")] public string? TournamentIdText { get; set; }

    private TournamentId? _tournamentId => TournamentId.FromOrNull(TournamentIdText);

    /// <summary>"Quarter-final 2" etc., once the bracket has been read; null until then or if it cannot be.</summary>
    private string? _tournamentMatchLabel;

    private DuelDto? _duel;
    private DuelResultDto? _leftResult;
    private DuelResultDto? _rightResult;
    private bool _loading = true;
    private bool _submitting;
    private bool _verdictRecorded;

    /// <summary>
    /// Which output the mobile focus switcher is showing: "both" (default), "left" or "right".
    /// Purely presentational — it drives a modifier class that hides a slot below 640px and has
    /// no effect at all on wider screens, where both panels always render side by side. Both
    /// panels stay mounted in every state so switching cannot interrupt a stream.
    /// </summary>
    private string _mobileFocus = "both";

    /// <summary>
    /// Options for the focus switcher. Visible labels are short; each button's accessible
    /// name (and title) carries the model's display name, which is what a person is actually
    /// choosing between.
    /// </summary>
    private static readonly (string Value, string Label)[] MobileFocusOptions =
    [
        ("both", "Both"),
        ("left", "L"),
        ("right", "R"),
    ];
    private bool _retrying;
    private string? _retryError;

    private ModelId? _winnerModelId;
    private ModelId? _loserModelId;
    private double? _eloShiftLeft;
    private double? _eloShiftRight;

    // ── Optimistic verdict ────────────────────────────────────────────────────
    // The click flips the UI immediately and the server confirms afterwards. _optimistic marks
    // the window where what is on screen is a prediction, so a losing race can be corrected
    // rather than left standing as if it were the recorded outcome.
    private bool _optimistic;
    private string? _overriddenNotice;

    /// <summary>
    /// The verdict landed while this page was watching, so the panels play their one-shot
    /// reveal. False for a duel opened already decided: that is a record, not a moment.
    /// </summary>
    private bool _revealVerdict;

    // ELO odometer. The badges render their reels at zero for one frame, then at the real
    // digits; the CSS transition between the two is the roll (SandboxedViewport.razor.css).
    // This replaced a 24-step count-up that re-rendered the whole page every 16 ms.
    private bool _eloRolled;
    private CancellationTokenSource? _eloAnimationCts;

    /// <summary>
    /// How far the loser was rated above the winner before this duel, when it was — the UPSET
    /// stamp. Only known from a verdict this page received (the response carries the after
    /// ratings); a duel opened already decided does not stamp.
    /// </summary>
    private double? _upsetGap;

    /// <summary>
    /// Below this the "upset" is noise: a 3-point gap stamped UPSET on screen during
    /// verification. 25 points is a winner the formula gave ~46% going in.
    /// </summary>
    private const double UpsetMinimumGap = 25;

    /// <summary>The prompt line is unclamped ("more").</summary>
    private bool _promptOpen;

    /// <summary>Roughly one desktop line of prompt; anything longer gets the "more" toggle.</summary>
    private const int PromptClampChars = 90;

    // ── Moments (G1–G7, 2026-10-26) ──────────────────────────────────────────
    /// <summary>The duel was live when the page opened it: the VS mark slams once.</summary>
    private bool _versusSlam;

    /// <summary>Each side's first token has arrived — its panel's light streak plays once.</summary>
    private readonly bool[] _spark = new bool[2];

    /// <summary>The race sonification is told at most this often, plus whenever a side finishes.</summary>
    private static readonly TimeSpan RaceCueInterval = TimeSpan.FromMilliseconds(250);
    private DateTimeOffset _lastRaceCueAt;
    private (bool Left, bool Right) _raceDoneCued;

    /// <summary>
    /// Every model's type, read once per page, for the winner's motif voice. The duel row does
    /// not carry types; null until the catalog lands, and the motif then falls back.
    /// </summary>
    private Dictionary<ModelId, ModelType>? _modelTypes;

    // ── Auto-judge ────────────────────────────────────────────────────────────
    // The server owns the real clock (it starts when the duel completes); this countdown
    // only mirrors it so the user can see how long they have. Whoever writes the verdict
    // first wins — a human click inside the window makes the server-side judge stand down.
    private VerdictSource _verdictSource = VerdictSource.Human;

    /// <summary>
    /// The recorded verdict itself. Needed alongside <see cref="_winnerModelId"/> because a tie
    /// is a real verdict with no winner — keying "was this decided?" off the winner id alone
    /// treats a tie as still-pending and restarts the judge countdown on a finished duel.
    /// </summary>
    private DuelVerdict _verdictValue = DuelVerdict.Pending;
    private string? _judgeRationale;
    private string? _judgeAnalysis;
    private int? _autoJudgeRemaining;
    private bool _autoJudgeDeciding;
    private CancellationTokenSource? _autoJudgeCts;
    /// <summary>The initial load threw — a transient failure, distinct from a 404.</summary>
    private bool _loadFailed;
    private bool _reloading;

    /// <summary>The judge-view endpoint failed (vision is off) — stop asking for the screenshots.</summary>
    private bool _judgeEyeUnavailable;

    // Who is ahead on tok/s, for the race announcement. Held 3 s against jitter.
    private Side? _leader;
    private DateTimeOffset _leaderChangedAt;

    /// <summary>
    /// The single state this page is in, derived from the flags rather than re-branched on
    /// them at each use site. See ArenaPhaseResolver (Shared/Presentation) for the precedence
    /// and for why it is not a chain of @if arms in this file any more.
    /// </summary>
    private ArenaPhase Phase => ArenaPhaseResolver.Resolve(new ArenaPhaseInputs
    {
        DuelStillRunning = _duelStillRunning,
        VerdictRecorded = _verdictRecorded,
        Optimistic = _optimistic,
        Verdict = _verdictValue,
        VerdictSource = _verdictSource,
        DuelVerdict = _duel?.Verdict ?? DuelVerdict.Pending,
        DuelCompleted = _duel?.CompletedAt is not null,
        LeftFailed = _leftResult?.IsFailure == true,
        RightFailed = _rightResult?.IsFailure == true,
        AnyResultPresent = _leftResult is not null || _rightResult is not null,
        AutoJudgeDeciding = _autoJudgeDeciding,
        AutoJudgeRemaining = _autoJudgeRemaining,
    });

    /// <summary>
    /// The settled-verdict band, as a selector for the confetti burst in fx.js. Named rather
    /// than written inline at each call site so the two uses cannot drift apart, and so a
    /// rename has one place to land — fx.js silently does nothing when a selector misses.
    /// </summary>
    private const string VerdictBandSelector = ".arena-band__box--verdict";

    /// <summary>
    /// What the verdict confetti lands on and bounces off — the two panels. The same trap as
    /// the selector above: fx.js treats a selector that matches nothing as "no surfaces".
    /// </summary>
    private const string VerdictColliderSelector = ".arena-panel__box";

    /// <summary>
    /// The winning panel, where the shockwave and the nebula start. Another CSS class used as a
    /// JS selector (ArenaViewportPanel.razor): a rename leaves both effects with no origin.
    /// </summary>
    private const string WinnerPanelSelector = ".arena-panel__box--winner";

    /// <summary>The winner's side of the stereo field; centred when there is no winner.</summary>
    private double WinnerPan =>
        _duel is null || _winnerModelId is null ? 0
        : _winnerModelId == _duel.LeftModelId ? -0.6 : 0.6;

    /// <summary>The grace window is running or the AI judge is deciding: the reticle is up.</summary>
    private bool JudgeIsLooking => ArenaPhaseResolver.JudgeIsLooking(Phase);

    // Both models failed → there's nothing to judge; the UI prompts a fresh duel instead.
    private bool BothSidesFailed() =>
        _leftResult?.IsFailure == true && _rightResult?.IsFailure == true;

    /// <summary>Gates "Retry duel" — a failure on either side is the case it exists for.</summary>
    private bool EitherSideFailed() =>
        _leftResult?.IsFailure == true || _rightResult?.IsFailure == true;

    private bool BothOutputsPresent() =>
        !string.IsNullOrWhiteSpace(_leftResult?.HtmlOutputRaw)
        && !string.IsNullOrWhiteSpace(_rightResult?.HtmlOutputRaw);

    /// <summary>
    /// Whether a human vote can still land. Gates the vote buttons out of the DOM rather than
    /// disabling them: once the verdict is in they can never become live again, and a dead
    /// control in the primary position reads as a broken page.
    /// </summary>
    /// <remarks>
    /// Requires the duel to be terminal (<c>CompletedAt</c> set). One side finishing early used
    /// to light the buttons while the other model was still generating — and the server, before
    /// the missing-result guard, ACCEPTED that vote and moved ELO on half a duel.
    /// </remarks>
    private bool CanStillVote =>
        !_verdictRecorded
        && !_autoJudgeDeciding
        && !BothSidesFailed()
        && !_duelStillRunning
        && _duel?.CompletedAt is not null;

    /// <summary>The model's name.</summary>
    /// <remarks>
    /// Resolution order: result row's <c>ModelName</c> (only present once inference has
    /// landed), then the snapshotted <c>LeftModelName</c> on the duel DTO (always present for
    /// duels created after the snapshot was added; the server resolves through the catalog
    /// too), then the id as a last-ditch fallback so the page never renders a ULID on its own
    /// for a side whose name the user has no way to look up.
    /// </remarks>
    private string LeftDisplayName =>
        _leftResult?.ModelName
        ?? (string.IsNullOrEmpty(_duel?.LeftModelName) ? null : _duel!.LeftModelName)
        ?? _duel?.LeftModelId.ToString()
        ?? "Left";

    /// <inheritdoc cref="LeftDisplayName"/>
    private string RightDisplayName =>
        _rightResult?.ModelName
        ?? (string.IsNullOrEmpty(_duel?.RightModelName) ? null : _duel!.RightModelName)
        ?? _duel?.RightModelId.ToString()
        ?? "Right";

    /// <summary>
    /// Sums the two sides' API costs. Models without a Foundry rate return null and contribute
    /// 0, so a "free" local model paired with a paid remote just shows the remote's share.
    /// </summary>
    private double DuelTotalCost() =>
        (_leftResult?.ApiCostUsd ?? 0) + (_rightResult?.ApiCostUsd ?? 0);

    /// <summary>Null when only one side has results or neither has a rate; the page then
    /// shows no "Duel cost" line.</summary>
    private double? DuelTotalCostParam =>
        BothOutputsPresent() && (_leftResult?.ApiCostUsd.HasValue == true || _rightResult?.ApiCostUsd.HasValue == true)
            ? DuelTotalCost()
            : (double?)null;

    // ── Live duel state ───────────────────────────────────────────────────────
    // The streaming half of this page used to be the /processing page. It lives here because a
    // duel is one thing: it streams, then it is judged, and swapping pages between those two
    // moments cost a full remount of both preview iframes. Running a browser model is no longer
    // part of it — that moved to LocalInferenceDriver, leaving only the view state behind.
    private string? _leftStreamingHtml;
    private string? _rightStreamingHtml;
    private bool _duelStillRunning;
    private SignalRDuelClient? _signalR;

    /// <summary>
    /// Streaming telemetry per side, indexed by <see cref="Side"/>. Replaces the row of
    /// parallel <c>_leftXxx</c> / <c>_rightXxx</c> fields that read identically but had to
    /// be updated through a <c>Side == "Left"</c> branch. Marked non-readonly because the
    /// side elements are mutated in place; only the array reference itself is fixed.
    /// </summary>
    private readonly SideMetrics[] _sides =
    [
        new SideMetrics(),
        new SideMetrics(),
    ];

    private string DisplayName(Side side) => side == Side.Left ? LeftDisplayName : RightDisplayName;

    // The race's words and marker positions are RaceCommentary (Shared/Presentation), where the
    // Unit tier can reach them; these only hand it this page's state.
    /// <summary>
    /// Settings → Arena telemetry. Off, the panels get no live inputs, so the streaming render
    /// path skips the progress math for a HUD that is not on screen.
    /// </summary>
    private bool TelemetryOn => Prefs.IsOn(Preference.ArenaDetails);

    private double RaceProgress(Side side) =>
        RaceCommentary.Progress(side, _sides[(int)Side.Left], _sides[(int)Side.Right]);

    private string RaceTicker =>
        RaceCommentary.Ticker(_sides[(int)Side.Left], _sides[(int)Side.Right], LeftDisplayName, RightDisplayName);

    private string RaceAnnouncement =>
        RaceCommentary.Announcement(_sides[(int)Side.Left], _sides[(int)Side.Right], LeftDisplayName, RightDisplayName, _leader);

    // Owns running a WebGPU model in this tab and posting its result back. Per-Arena, because
    // its replay guard is per-duel bookkeeping.
    private LocalInferenceDriver _localInference = null!;
    private PeriodicTimer? _stallTimer;

    // Token-batch updates arrive far faster than a frame, and each one used to re-render the
    // whole page — both sandboxed iframes and the scorecard included — to move a counter. The
    // two streaming handlers ask this for a render instead; terminal events still paint at once.
    private RenderCoalescer? _streamRender;

    private IDisposable? _locationChangingReg;

    /// <summary>prefers-reduced-motion, read once — it gated every ELO count-up with a JS round-trip.</summary>
    private bool? _reduceMotion;

    protected override void OnInitialized()
    {
        // UX#7: Block navigation away when verdict is still pending. The prompt only fires
        // when the user has time to vote — AutoJudgeDelaySeconds > 0 means the auto-judge
        // will decide the duel on its own, so there is no reason to nag on the way out.
        // (The previous condition inverted this and prompted exactly when it shouldn't.)
        _locationChangingReg = Nav.RegisterLocationChangingHandler(async ctx =>
        {
            // Never for a bracket match: the tournament's judge decides it with no grace window,
            // and the viewer's vote is not what the bracket is waiting on.
            if (_duel?.Verdict == DuelVerdict.Pending && !_verdictRecorded && !_retrying
                && _duel.AutoJudgeDelaySeconds <= 0 && _tournamentId is null)
            {
                var confirmed = await JS.InvokeAsync<bool>("confirm", "You haven't picked a winner yet. Leave without voting?");
                if (!confirmed) ctx.PreventNavigation();
            }
        });
    }

    /// <summary>
    /// Loads the duel the route names, and again whenever that changes. OnParametersSetAsync
    /// rather than OnInitializedAsync, same as Tournament and ModelProfile: "Retry duel" goes to
    /// /arena/{new id}, Blazor reuses this instance, and an initialiser never ran for the rerun —
    /// the page kept the old duel, its hub group, its timers and <c>_retrying</c>, and a browser
    /// model in the rerun never got StartLocalInference. Keyed on the id so a query-only change
    /// (<c>?tournament=</c>) does not reload.
    /// </summary>
    protected override async Task OnParametersSetAsync()
    {
        if (DuelIdText == _loadedDuelIdText) return;
        var isReload = _loadedDuelIdText is not null;
        _loadedDuelIdText = DuelIdText;

        if (isReload)
        {
            // Detach first, synchronously, so no handler of the old duel can write into the
            // fresh state below; then reset, so the render at the first await shows "Loading".
            var previousHub = DetachFromDuel();
            ResetDuelState();
            if (previousHub is not null) await previousHub.DisposeAsync();
        }

        // Not shaped like any id: "Duel not found" without asking the API to 404 it first.
        if (!RouteIdShape.IsPlausible(DuelIdText))
        {
            _loading = false;
            return;
        }

        _ = LoadTournamentMatchLabelAsync();
        _ = LoadModelTypesAsync();

        var loading = DuelIdText;
        try
        {
            await LoadDuelAsync();
        }
        finally
        {
            // A superseded load must not clear the spinner of the one that replaced it.
            if (loading == DuelIdText) _loading = false;
        }
    }

    /// <summary>
    /// Stops everything the current duel has running — countdown, count-up, stall ticker,
    /// render coalescer, WebLLM and hub subscriptions — and hands back
    /// the hub connection for the caller to dispose (leaving the connection leaves its group).
    /// Shared by an id change and by DisposeAsync so the two cannot drift apart.
    /// </summary>
    private SignalRDuelClient? DetachFromDuel()
    {
        StopAutoJudgeCountdown();
        _eloAnimationCts?.Cancel();
        _eloAnimationCts?.Dispose();
        _eloAnimationCts = null;
        StopStallTimer();
        _streamRender?.Dispose();
        _streamRender = null;
        WebLlm.OnStatusUpdate -= OnLocalStatusUpdate;
        // Both idempotent in audio.js: silent when nothing is playing.
        _ = AudioService.StopRaceAsync();
        _ = AudioService.StopDeliberationAsync();

        var hub = _signalR;
        _signalR = null;
        if (hub is not null)
        {
            hub.OnModelStatusUpdate -= OnStatusUpdate;
            hub.OnDuelComplete -= OnDuelComplete;
            hub.OnVerdictRecorded -= OnVerdictRecorded;
            hub.OnStartLocalInference -= OnStartLocalInference;
        }
        return hub;
    }

    /// <summary>
    /// Returns every per-duel field to its initial value before a different duel loads. The
    /// child panels need no reset: <c>_loading</c> unmounts them, so the next duel gets fresh
    /// instances. <c>_mobileFocus</c> is a viewing preference, not duel state, and survives.
    /// </summary>
    private void ResetDuelState()
    {
        _loading = true;
        _loadFailed = false;
        _duel = null;
        _leftResult = null;
        _rightResult = null;
        _tournamentMatchLabel = null;
        _submitting = false;
        _retrying = false;
        _retryError = null;

        _verdictRecorded = false;
        _optimistic = false;
        _revealVerdict = false;
        _overriddenNotice = null;
        _verdictSource = VerdictSource.Human;
        _verdictValue = DuelVerdict.Pending;
        _judgeRationale = null;
        _judgeAnalysis = null;
        _winnerModelId = null;
        _loserModelId = null;
        _eloShiftLeft = null;
        _eloShiftRight = null;
        _eloRolled = false;
        _upsetGap = null;
        _judgeEyeUnavailable = false;

        _duelStillRunning = false;
        _completionHandled = false;
        _leftStreamingHtml = null;
        _rightStreamingHtml = null;
        _sides[(int)Side.Left] = new SideMetrics();
        _sides[(int)Side.Right] = new SideMetrics();
        _leader = null;
        _leaderChangedAt = default;

        _versusSlam = false;
        _spark[(int)Side.Left] = false;
        _spark[(int)Side.Right] = false;
        _lastRaceCueAt = default;
        _raceDoneCued = default;
    }

    /// <summary>Reads every model's type once for the winner's motif. Decoration: failure is silence.</summary>
    private async Task LoadModelTypesAsync()
    {
        if (_modelTypes is not null) return;
        try
        {
            var models = await ApiClient.GetModelsAsync();
            _modelTypes = models?.DistinctBy(m => m.ModelId).ToDictionary(m => m.ModelId, m => m.ModelType);
        }
        catch (Exception)
        {
            // The motif falls back to the bell voice.
        }
    }

    /// <summary>The winner's type, from the catalog or — for a browser model watched live — from the race.</summary>
    private ModelType? WinnerType()
    {
        if (_winnerModelId is not { } winner) return null;
        if (_modelTypes?.TryGetValue(winner, out var type) == true) return type;
        return _sides[(int)SideOf(winner)].IsBrowserModel ? ModelType.Local : null;
    }

    /// <summary>
    /// Names the match in the back link ("Semi-final 1"). Decoration only and not awaited: the
    /// link works without it, so a bracket that fails to load must not hold up the duel.
    /// </summary>
    private async Task LoadTournamentMatchLabelAsync()
    {
        if (_tournamentId is not { } tournamentId) return;

        try
        {
            var tournament = await ApiClient.GetTournamentAsync(tournamentId);
            var match = tournament?.Matches.FirstOrDefault(m => m.DuelId == _duelId);
            if (match is null) return;

            var inRound = tournament!.Matches.Count(m => m.Round == match.Round);
            // "Quarter-finals" names the round; one match of it is "Quarter-final 2".
            _tournamentMatchLabel = inRound <= 1 ? match.RoundName
                : match.RoundName.EndsWith("finals", StringComparison.Ordinal)
                    ? $"{match.RoundName[..^1]} {match.Index + 1}"
                    : $"{match.RoundName}, match {match.Index + 1}";
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception)
        {
            // The plain "Back to bracket" link is already on screen.
        }
    }

    /// <summary>
    /// Loads the duel and wires the streaming path. Split from <see cref="OnParametersSetAsync"/>
    /// so a transient failure can be retried from the page instead of stranding it on the
    /// "duel no longer exists" branch — a fetch that failed says nothing about whether the
    /// duel exists.
    /// </summary>
    private async Task LoadDuelAsync()
    {
        var duelId = _duelId;
        try
        {
            var duel = await ApiClient.GetDuelAsync(duelId);
            // The route moved on while this was in flight; the newer load owns the page.
            // ponytail: only the first await is guarded — a change during ConnectAsync below
            // could still leave one extra hub connection until the page is disposed.
            if (duelId != _duelId) return;
            _duel = duel;
            if (_duel is not null)
            {
                _leftResult = _duel.Results.FirstOrDefault(r => r.ModelId == _duel.LeftModelId);
                _rightResult = _duel.Results.FirstOrDefault(r => r.ModelId == _duel.RightModelId);

                // If duel is still running, connect SignalR for the live race and preview.
                // "Running" is "not both results in yet", NOT "no results yet". The old test
                // required both rows to be missing, so opening a duel after one side had
                // finished or failed — routine when you click into a tournament match partway
                // through — treated it as over: no hub, no stream, and no DuelComplete, so the
                // other side sat at "Generating…" for good. The verdict still arrived through
                // the countdown's re-read, which is how a walkover winner ended up on screen
                // with no output under it.
                if (_duel.CompletedAt is null && (_leftResult is null || _rightResult is null))
                {
                    _duelStillRunning = true;

                    // The duel is live: the VS mark slams once between the panels, on the
                    // sound's thump (arena__vs in Arena.razor.css, playVersus in audio.js).
                    _versusSlam = true;
                    _ = AudioService.PlayVersusAsync();

                    // A side that already finished will send no more status updates, so seed
                    // the race from its stored row rather than leaving it at Initializing.
                    SeedFinishedSide(Side.Left, _leftResult);
                    SeedFinishedSide(Side.Right, _rightResult);

                    _streamRender = new RenderCoalescer(() => InvokeAsync(StateHasChanged));
                    _localInference = new LocalInferenceDriver(WebLlm, ApiClient);

                    _signalR = new SignalRDuelClient();
                    _signalR.OnModelStatusUpdate += OnStatusUpdate;
                    _signalR.OnDuelComplete += OnDuelComplete;
                    _signalR.OnVerdictRecorded += OnVerdictRecorded;
                    // Browser models run in this tab, not on the server — without this the duel
                    // would sit at Initializing forever for any WebGPU pairing.
                    _signalR.OnStartLocalInference += OnStartLocalInference;
                    WebLlm.OnStatusUpdate += OnLocalStatusUpdate;
                    await _signalR.ConnectAsync(Nav.BaseUri.TrimEnd('/'), duelId);

                    // Independent ticker: worker silence is exactly when the UI must keep talking.
                    // Started before the catch-up read below, so a duel that turns out to be
                    // complete already stops it in OnDuelComplete rather than leaving it running.
                    // Guarded because DuelComplete can already have landed during ConnectAsync.
                    if (_duelStillRunning)
                    {
                        _stallTimer = new PeriodicTimer(TimeSpan.FromSeconds(2));
                        _ = RunStallTimerAsync(_stallTimer);
                    }

                    // DuelComplete goes once, to whoever is in the group at that moment. A duel
                    // that finished between the read above and joining the group is never
                    // announced to this page, which then shows a race that never ends — most
                    // likely exactly when one side was already done at load. Re-read once now
                    // that the page is subscribed: any later completion will be pushed.
                    var current = await ApiClient.GetDuelAsync(duelId);
                    if (current?.CompletedAt is not null)
                        OnDuelComplete(current);
                }
                // If a verdict already exists, restore it; otherwise a finished duel resumes
                // the judge's countdown.
                if (!TryRestoreVerdict(_duel) && !_duelStillRunning)
                {
                    // Arrived at an already-finished duel: the server's clock has been running
                    // since CompletedAt, so resume from where it actually is, not from zero.
                    StartAutoJudgeCountdown();
                }
            }
        }
        catch (Exception)
        {
            // A failed fetch is NOT evidence that the duel is gone. Distinguish the two so a
            // network blip offers a retry instead of the misleading "wiped" message.
            if (duelId != _duelId) return;
            _duel = null;
            _loadFailed = true;
        }
    }

    private async Task RetryLoadAsync()
    {
        if (_reloading) return;
        _reloading = true;
        _loadFailed = false;
        try
        {
            await LoadDuelAsync();
        }
        finally
        {
            _reloading = false;
        }
    }

    /// <summary>
    /// Starts a fresh duel with the same pairing and prompt. Model failures here are usually
    /// transient (model load, browser cache, rate limit), so re-running beats rebuilding the
    /// selection by hand. The original duel is left untouched and still judgeable.
    /// </summary>
    private async Task RetryDuelAsync()
    {
        if (_duel is null || _retrying || _submitting) return;

        _retrying = true;
        _retryError = null;

        try
        {
            // PromptText is what the user typed; PromptFull is that plus the self-contained instruction
            // CommenceDuelHandler appends to every duel. Re-sending PromptFull appended it again
            // on each retry, and could push a long prompt past the 10,000-character limit.
            var prompt = string.IsNullOrWhiteSpace(_duel.PromptText) ? _duel.PromptFull : _duel.PromptText;
            var rerun = await ApiClient.CommenceDuelAsync(_duel.LeftModelId, _duel.RightModelId, prompt);

            if (rerun is not null)
            {
                // Same route, so this component stays: OnParametersSetAsync sees the new id,
                // tears the old duel down and loads the rerun (which also clears _retrying).
                Nav.NavigateTo($"/arena/{rerun.DuelId}");
                return;
            }

            _retryError = "Could not start the re-run. Please try again.";
        }
        catch (Exception ex)
        {
            _retryError = $"Failed to start the re-run: {ex.Message}";
        }

        _retrying = false;
    }

    /// <summary>
    /// Commits the pick on screen before the network confirms it. The server remains the
    /// authority — every branch below either adopts what it recorded or rolls the guess back.
    /// </summary>
    private async Task RecordVerdictAsync(DuelVerdict verdict)
    {
        if (_verdictRecorded || _submitting || _duel is null) return;
        _submitting = true;
        _overriddenNotice = null;

        // Stop the visible countdown the moment they commit. The server-side judge is stopped
        // by the verdict itself — it re-reads the duel and stands down on anything but Pending.
        StopAutoJudgeCountdown();

        // Optimistic paint: winner/loser badges land on the frame of the click rather than
        // after a round-trip. ELO stays blank until the server says what it actually was —
        // inventing a number here would be a guess presented as a result.
        _verdictRecorded = true;
        _optimistic = true;
        _verdictSource = VerdictSource.Human;
        _judgeRationale = null;
        _judgeAnalysis = null;
        _winnerModelId = verdict == DuelVerdict.Left ? _duel.LeftModelId : _duel.RightModelId;
        _loserModelId = verdict == DuelVerdict.Left ? _duel.RightModelId : _duel.LeftModelId;
        _revealVerdict = true;
        StateHasChanged();
        _ = AudioService.PlayTickAsync();

        try
        {
            var response = await ApiClient.RecordVerdictAsync(_duelId, new VerdictRequestDto { Verdict = verdict });
            if (response is not null)
            {
                AdoptVerdict(response.Verdict, response.WinnerModelId, response.LoserModelId, response.Source, response.JudgeRationale, response.JudgeAnalysis);
                ApplyEloShifts(response.WinnerModelId, response.EloShiftWinner, response.EloShiftLoser);
                NoteUpset(response);
                await AnimateEloAsync();
            }
            else
            {
                // Most likely a 409: the auto-judge landed its verdict first. Show what was
                // actually recorded rather than leaving the optimistic guess standing.
                await ReconcileWithServerAsync(verdict);
            }
        }
        catch (Exception ex)
        {
            // The write may still have landed — ask before deciding the click did nothing.
            if (!await ReconcileWithServerAsync(verdict))
            {
                RollBackOptimisticVerdict();
                _retryError = $"Could not record the verdict: {ex.Message}";
            }
        }
        finally
        {
            _submitting = false;
        }
    }

    /// <summary>
    /// Re-reads the duel after an uncertain write and reconciles the optimistic paint with it.
    /// Returns true when a verdict exists server-side.
    /// </summary>
    private async Task<bool> ReconcileWithServerAsync(DuelVerdict attempted)
    {
        var predictedWinner = _winnerModelId;

        if (!await RefreshVerdictFromServerAsync()) return false;

        if (!string.Equals(predictedWinner, _winnerModelId, StringComparison.Ordinal))
        {
            _overriddenNotice = _verdictValue == DuelVerdict.Voided
                ? "The duel was voided before your pick landed — there was nothing to judge."
                : _verdictValue == DuelVerdict.Tie
                    ? "The AI judge recorded a tie first — it found both outputs equivalent. Your pick was not counted."
                    : _verdictSource == VerdictSource.Ai
                        ? $"The AI judge recorded its verdict first — {ResolveDisplayName(_winnerModelId)} won. Your pick was not counted."
                        : $"This duel was already decided — {ResolveDisplayName(_winnerModelId)} won.";
        }

        await AnimateEloAsync();
        return true;
    }

    private void RollBackOptimisticVerdict()
    {
        _verdictRecorded = false;
        _optimistic = false;
        _verdictValue = DuelVerdict.Pending;
        _winnerModelId = null;
        _loserModelId = null;
        _revealVerdict = false;
        _eloShiftLeft = null;
        _eloShiftRight = null;
        _eloRolled = false;
        _upsetGap = null;
        StartAutoJudgeCountdown();
    }

    private void AdoptVerdict(DuelVerdict verdict, ModelId? winner, ModelId? loser, VerdictSource source, string? rationale, string? analysis = null)
    {
        var wasAlreadyFinal = _verdictRecorded && !_optimistic;

        _verdictRecorded = true;
        _optimistic = false;
        _verdictValue = verdict;
        _winnerModelId = winner;
        _loserModelId = loser;
        _verdictSource = source;
        _judgeRationale = rationale;
        _judgeAnalysis = analysis;

        // Only on the transition into a settled verdict. Re-adopting the same verdict happens
        // on every reconcile and poll, and replaying the celebration each time would turn the
        // payoff moment into a stutter. A voided duel has nothing to celebrate.
        if (!wasAlreadyFinal && verdict != DuelVerdict.Voided)
        {
            _revealVerdict = true;
            _ = CelebrateVerdictAsync(verdict);
        }
    }

    /// <summary>
    /// The duel's payoff. A running deliberation resolves first (its beating slides into unison,
    /// or a third voice for a tie); then, on the sound's impact, the shockwave rings, the shader
    /// nebula and the confetti all start from the winner. Safe here because generation has
    /// finished — and gpu-fx.js still checks the GPU lease itself.
    /// </summary>
    private async Task CelebrateVerdictAsync(DuelVerdict verdict)
    {
        try
        {
            if (verdict == DuelVerdict.Voided)
            {
                await AudioService.StopDeliberationAsync();
                return;
            }

            if (verdict == DuelVerdict.Tie)
            {
                await AudioService.PlayVerdictAsync(tie: true, pan: 0, winner: null, winnerType: null);
                return;
            }

            // The winner's own motif, from its side of the stereo field.
            var leadMs = await AudioService.PlayVerdictAsync(tie: false, WinnerPan, _winnerModelId, WinnerType());
            if (leadMs > 0) await Task.Delay(leadMs);

            // CSS classes used as JS selectors: fx.js and gpu-fx.js answer a selector that
            // matches nothing by doing nothing at all, so a rename silently kills these.
            _ = Fx.ImpactAsync(WinnerPanelSelector);
            _ = Fx.NebulaAsync(WinnerPanelSelector);
            await Fx.BurstFromAsync(VerdictBandSelector, colliders: VerdictColliderSelector);
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or ObjectDisposedException)
        {
            // Decoration only — never let it reach the ErrorBoundary over a finished duel.
        }
    }

    /// <summary>
    /// Stamps UPSET when the winner was rated below the loser going in. The response carries the
    /// ratings after the verdict and the shifts, so the ratings before are exact — no second read.
    /// </summary>
    private void NoteUpset(VerdictResponseDto response)
    {
        if (response is not { Verdict: DuelVerdict.Left or DuelVerdict.Right,
                              WinnerEloAfter: { } winnerAfter, LoserEloAfter: { } loserAfter,
                              EloShiftWinner: { } winnerShift, EloShiftLoser: { } loserShift })
            return;

        var gap = (loserAfter - loserShift) - (winnerAfter - winnerShift);
        if (gap < UpsetMinimumGap) return;

        _upsetGap = Math.Round(gap);
        if (_revealVerdict) _ = AudioService.PlayUpsetAsync();
    }

    // ── ELO odometer ──────────────────────────────────────────────────────────

    /// <summary>
    /// Rolls both badges' reels from zero to the real shift: one render at zero, one at the
    /// digits, and the CSS transition between them is the motion, with clicks under it.
    /// Respecting reduced-motion is handled by settling instantly rather than by rolling faster.
    /// </summary>
    private async Task AnimateEloAsync()
    {
        _eloAnimationCts?.Cancel();
        _eloAnimationCts?.Dispose();
        _eloAnimationCts = new CancellationTokenSource();
        var token = _eloAnimationCts.Token;

        if (!_revealVerdict || await PrefersReducedMotionAsync())
        {
            SettleEloDisplay();
            StateHasChanged();
            return;
        }

        _eloRolled = false;
        await InvokeAsync(StateHasChanged);
        try
        {
            // One frame at zero is all the transition needs to have somewhere to start from.
            await Task.Delay(50, token);
            if (_eloShiftLeft.HasValue) _ = AudioService.PlayOdometerAsync(WinnerPan);
        }
        catch (OperationCanceledException)
        {
            // Superseded or navigating away — land on the true values below regardless.
        }

        SettleEloDisplay();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Read once per page and cached. No <c>eval</c>: matchMedia is called directly and its
    /// <c>matches</c> read through .NET 10's IJSObjectReference.GetValueAsync.
    /// </summary>
    private async Task<bool> PrefersReducedMotionAsync()
    {
        if (_reduceMotion is { } cached) return cached;
        bool reduce;
        try
        {
            await using var query = await JS.InvokeAsync<IJSObjectReference>("matchMedia", "(prefers-reduced-motion: reduce)");
            reduce = await query.GetValueAsync<bool>("matches");
        }
        catch (JSException)
        {
            reduce = false;
        }
        _reduceMotion = reduce;
        return reduce;
    }

    private void SettleEloDisplay() => _eloRolled = true;

    // ── Auto-judge countdown ──────────────────────────────────────────────────

    /// <summary>
    /// Mirrors the server's grace window. Started once the duel has finished and is still
    /// unjudged; a delay of 0 (auto-judge disabled) leaves the Arena exactly as it was.
    /// </summary>
    private void StartAutoJudgeCountdown()
    {
        if (_duel is null || _verdictRecorded || _autoJudgeCts is not null) return;
        if (_duel.Verdict != DuelVerdict.Pending) return;
        if (BothSidesFailed()) return;
        if (_duel.AutoJudgeDelaySeconds <= 0) return;

        // The server started counting at CompletedAt, so a page opened late must not restart
        // the clock — resume from whatever is genuinely left of the window.
        var elapsed = _duel.CompletedAt is { } completedAt
            ? (DateTimeOffset.UtcNow - completedAt).TotalSeconds
            : 0;
        var remaining = (int)Math.Ceiling(_duel.AutoJudgeDelaySeconds - elapsed);

        _autoJudgeCts = new CancellationTokenSource();
        _ = RunAutoJudgeCountdownAsync(Math.Max(0, remaining), _autoJudgeCts.Token);
    }

    private void StopAutoJudgeCountdown()
    {
        _autoJudgeCts?.Cancel();
        _autoJudgeCts?.Dispose();
        _autoJudgeCts = null;
        _autoJudgeRemaining = null;
        _autoJudgeDeciding = false;
    }

    private async Task RunAutoJudgeCountdownAsync(int seconds, CancellationToken cancellationToken)
    {
        try
        {
            for (var remaining = seconds; remaining > 0; remaining--)
            {
                _autoJudgeRemaining = remaining;
                await InvokeAsync(StateHasChanged);
                // The window closing, audibly: one rising note for each of the last three seconds.
                if (remaining <= 3) _ = AudioService.PlayCountdownAsync(remaining);
                await Task.Delay(1000, cancellationToken);
            }

            _autoJudgeRemaining = null;
            _autoJudgeDeciding = true;
            await InvokeAsync(StateHasChanged);
            // Two beating voices while it decides — resolved by the verdict cue.
            _ = AudioService.StartDeliberationAsync();

            // SignalR delivers the verdict when this page is connected, but the Arena only
            // connects for duels that were still running when it loaded. Poll as well so a
            // page opened after completion still reflects the decision. The judge call itself
            // is bounded by AiJudge:TimeoutSeconds, so this window is generous, not open-ended.
            for (var attempt = 0; attempt < 45 && !_verdictRecorded; attempt++)
            {
                await Task.Delay(1000, cancellationToken);
                bool landed;
                // One transient 5xx or network blip used to end this fire-and-forget loop
                // silently, leaving the page on "the AI judge is deciding…" for good.
                try { landed = await RefreshVerdictFromServerAsync(); }
                catch (HttpRequestException) { continue; }
                if (landed)
                {
                    await AnimateEloAsync();
                    return;
                }
            }

            // The judge declined to decide (no output to compare, or an unreachable judge).
            // Fall back to the plain "pick a winner" prompt — the duel is still judgeable.
            _autoJudgeDeciding = false;
            _ = AudioService.StopDeliberationAsync();
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException)
        {
            // Human picked, or the page went away.
        }
    }

    /// <summary>Re-reads the duel and adopts its verdict if one has landed. True when it had.</summary>
    private async Task<bool> RefreshVerdictFromServerAsync()
    {
        var fresh = await ApiClient.GetDuelAsync(_duelId);
        // A tie or a void is terminal but carries no winner id, so both have to pass this gate
        // on the verdict alone — otherwise the poll below never stops and the page waits out all
        // 45 attempts on a duel that was decided on the first one.
        if (fresh is null
            || fresh.Verdict == DuelVerdict.Pending
            || (fresh.Verdict is not (DuelVerdict.Tie or DuelVerdict.Voided) && fresh.WinnerModelId is null))
            return false;

        _duel = fresh;
        AdoptVerdict(fresh.Verdict, fresh.WinnerModelId, fresh.LoserModelId, fresh.VerdictSource, fresh.JudgeRationale, fresh.JudgeAnalysis);
        _autoJudgeRemaining = null;
        _autoJudgeDeciding = false;
        ApplyEloShifts(fresh);
        await InvokeAsync(StateHasChanged);
        return true;
    }

    private void OnVerdictRecorded(VerdictResponseDto response)
    {
        // A verdict the user just submitted comes back through the normal response path with
        // its ELO animation; adopting the broadcast too would restart that mid-flight.
        if (_verdictRecorded && !_optimistic) return;

        AdoptVerdict(response.Verdict, response.WinnerModelId, response.LoserModelId, response.Source, response.JudgeRationale, response.JudgeAnalysis);
        ApplyEloShifts(response.WinnerModelId, response.EloShiftWinner, response.EloShiftLoser);
        NoteUpset(response);

        StopAutoJudgeCountdown();
        InvokeAsync(async () =>
        {
            StateHasChanged();
            await AnimateEloAsync();
        });
    }

    private void ApplyEloShifts(DuelDto duel)
    {
        if (duel.WinnerModelId is null || duel.EloShiftWinner is null || duel.EloShiftLoser is null)
            return;

        ApplyEloShifts(duel.WinnerModelId, duel.EloShiftWinner, duel.EloShiftLoser);
    }

    /// <summary>
    /// The server reports shifts by outcome (winner/loser); the two panels need them by side.
    /// Every path that learns a verdict — human click, SignalR push, or a re-read of the duel —
    /// funnels through here so the badges can't disagree about which side gained.
    /// </summary>
    private void ApplyEloShifts(ModelId? winnerModelId, double? shiftWinner, double? shiftLoser)
    {
        _eloShiftLeft = _duel?.LeftModelId == winnerModelId ? shiftWinner : shiftLoser;
        _eloShiftRight = _duel?.RightModelId == winnerModelId ? shiftWinner : shiftLoser;
    }

    private string ResolveDisplayName(ModelId? modelId)
    {
        if (modelId is null || _duel is null) return modelId?.Value ?? string.Empty;
        if (modelId == _duel.LeftModelId)
            return _leftResult?.ModelName ?? _duel.LeftModelName;
        if (modelId == _duel.RightModelId)
            return _rightResult?.ModelName ?? _duel.RightModelName;
        return modelId.Value;
    }

    // ── SignalR handlers ──────────────────────────────────────────────────────
    private void OnStatusUpdate(ModelStatusUpdateDto update)
    {
        if (update.DuelId != _duelId) return;

        var side = ParseSide(update.Side);
        if (side is null) return;

        var metrics = _sides[(int)side.Value];

        // For a browser side the server only knows the model is running, so its 500 ms
        // heartbeat carries TokenCount 0. Once the in-tab worker has reported real figures,
        // that heartbeat must not overwrite them (the HUD flickered to 0 tok/s every tick).
        if (update.Status == DuelStatus.Generating && update.TokenCount == 0 && metrics.TokenCount > 0)
            return;

        var tokensBefore = metrics.TokenCount;
        metrics.Status = update.Status;
        metrics.TokenCount = update.TokenCount;
        if (update.TokenVelocity.HasValue) metrics.TokenVelocity = update.TokenVelocity;
        if (!string.IsNullOrEmpty(update.HtmlPreview)) SetStreamingHtml(side.Value, update.HtmlPreview);

        // Coalesced, not immediate: this fires once per token batch on both sides at once.
        _streamRender?.Request();
        NoteLead();
        NoteFirstToken(side.Value, tokensBefore);
        CueRace();
    }

    /// <summary>
    /// A side's first token: its panel's light streak plays once and a chirp comes from its
    /// side. Only while watching live — a side seeded from a stored row is not news.
    /// </summary>
    private void NoteFirstToken(Side side, int tokensBefore)
    {
        if (tokensBefore > 0 || _sides[(int)side].TokenCount <= 0 || _spark[(int)side]) return;
        _spark[(int)side] = true;
        _ = AudioService.PlaySparkAsync(side == Side.Left ? -0.6 : 0.6);
        // A small burst off the panel that spoke first. fx.js stands down while the GPU lease
        // is held, so a browser model mid-inference never pays for it; a remote side's tok/s
        // is timed on the server and cannot be skewed by a canvas here.
        _ = Fx.BurstFromAsync($".arena__viewport-slot--{(side == Side.Left ? "left" : "right")} .arena-panel__box", 24);
    }

    /// <summary>
    /// Tells the race sonification each side's pace — at most every 250 ms, and at once when a
    /// side finishes so its finishing chime is on time. Token batches arrive far faster than
    /// that; the grains themselves are scheduled in audio.js.
    /// </summary>
    private void CueRace()
    {
        var left = _sides[(int)Side.Left];
        var right = _sides[(int)Side.Right];
        var done = (left.IsFinished, right.IsFinished);
        var now = DateTimeOffset.UtcNow;
        if (done == _raceDoneCued && now - _lastRaceCueAt < RaceCueInterval) return;

        _raceDoneCued = done;
        _lastRaceCueAt = now;
        _ = AudioService.RaceAsync(left.TokenVelocity ?? 0, right.TokenVelocity ?? 0, done.Item1, done.Item2);
    }

    /// <summary>
    /// Tracks who is ahead on tok/s for <see cref="RaceAnnouncement"/>. A lead must be clear
    /// (<see cref="RaceCommentary.Leader"/>) and last 3 s before it changes hands, so jitter is not news.
    /// </summary>
    private void NoteLead()
    {
        var now = DateTimeOffset.UtcNow;
        if (RaceCommentary.Leader(_sides[(int)Side.Left], _sides[(int)Side.Right]) is not { } ahead
            || ahead == _leader || now - _leaderChangedAt < TimeSpan.FromSeconds(3)) return;

        _leader = ahead;
        _leaderChangedAt = now;
    }

    /// <summary>
    /// Adopts a verdict already stored on the duel. A tie or a void counts: both are terminal
    /// with no winner id, so testing the winner id alone would drop them back into the
    /// countdown branch and re-arm a judge that has already spoken (or offer vote buttons on a
    /// duel that can no longer be judged).
    /// </summary>
    private bool TryRestoreVerdict(DuelDto duel)
    {
        if (!(duel.Verdict is DuelVerdict.Tie or DuelVerdict.Voided
              || (duel.Verdict != DuelVerdict.Pending && duel.WinnerModelId is not null)))
            return false;

        _verdictRecorded = true;
        _verdictValue = duel.Verdict;
        _winnerModelId = duel.WinnerModelId;
        _loserModelId = duel.LoserModelId;
        _verdictSource = duel.VerdictSource;
        _judgeRationale = duel.JudgeRationale;
        _judgeAnalysis = duel.JudgeAnalysis;
        ApplyEloShifts(duel);
        SettleEloDisplay();
        return true;
    }

    private bool _completionHandled;

    /// <summary>Marks a side the page joined too late to watch as finished, from its result row.</summary>
    private void SeedFinishedSide(Side side, DuelResultDto? result)
    {
        if (result is null) return;

        var metrics = _sides[(int)side];
        metrics.Status = result.IsFailure ? DuelStatus.Failed : DuelStatus.Done;
        metrics.TokenCount = result.TokenCount;
        metrics.TokenVelocity = result.TokenVelocity;
    }

    /// <summary>Maps a SignalR side name to the <see cref="Side"/> enum, or null if unknown.</summary>
    private static Side? ParseSide(string side) =>
        side == "Left" ? Side.Left : side == "Right" ? Side.Right : null;

    /// <summary>
    /// The side a model id occupies. Anything that is not the left model counts as the right
    /// one — the rule the two copy-pasted branches this replaced already applied.
    /// </summary>
    private Side SideOf(ModelId modelId) => modelId == _duel?.LeftModelId ? Side.Left : Side.Right;

    private void SetStreamingHtml(Side side, string html)
    {
        if (side == Side.Left) _leftStreamingHtml = html;
        else _rightStreamingHtml = html;
    }

    /// <summary>
    /// Browser-model status arrives straight from the JS worker, bypassing the server hub, so
    /// velocity has to be derived here rather than read off the update.
    /// </summary>
    private void OnLocalStatusUpdate(ModelId modelId, WebLlmStatusUpdate update)
    {
        var side = SideOf(modelId);
        var metrics = _sides[(int)side];
        metrics.IsBrowserModel = true;

        var status = update.Status switch
        {
            "Generating" => DuelStatus.Generating,
            "Done" => DuelStatus.Done,
            "Failed" => DuelStatus.Failed,
            _ => DuelStatus.Initializing,
        };

        // No pause call here: webllm-interop.js holds the GPU lease for the life of the worker,
        // so the confetti burst stands down on its own — on this page and on Tournament alike.

        metrics.LastLocalUpdateAt = DateTimeOffset.UtcNow;
        // Warm-up is the elapsed time at the worker's own Initializing → Generating edge.
        if (metrics.LocalStatus == DuelStatus.Initializing && status == DuelStatus.Generating)
            metrics.WarmUpMs = update.ElapsedMs;
        metrics.LocalStatus = status;

        var tokensBefore = metrics.TokenCount;
        metrics.Status = status;
        metrics.TokenCount = update.TokenCount;
        metrics.TokenVelocity = GenerationVelocity(update, metrics.WarmUpMs);
        if (update.HtmlPreview is not null) SetStreamingHtml(side, update.HtmlPreview);

        // Same reasoning as the hub handler: the WebLLM worker reports per token batch.
        _streamRender?.Request();
        NoteLead();
        NoteFirstToken(side, tokensBefore);
        CueRace();
    }

    /// <summary>Tokens per second excluding warm-up, so a slow model load does not read as slow generation.</summary>
    private static double? GenerationVelocity(WebLlmStatusUpdate update, long? warmUpMs)
    {
        var generatingMs = warmUpMs.HasValue ? update.ElapsedMs - warmUpMs.Value : update.ElapsedMs;
        return generatingMs > 0 && update.TokenCount > 0
            ? Math.Round(update.TokenCount / (generatingMs / 1000.0), 1)
            : null;
    }

    private async Task RunStallTimerAsync(PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync())
        {
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < _sides.Length; i++)
                _sides[i].StallDetail = RaceCommentary.StallDetail(_sides[i], now, DisplayName((Side)i));
            await InvokeAsync(StateHasChanged);
        }
    }

    /// <summary>
    /// The ticker exists only for the race. Left running it re-rendered the whole Arena every
    /// 2 s for the life of the page.
    /// Disposing it ends the WaitForNextTickAsync loop above.
    /// </summary>
    private void StopStallTimer()
    {
        _stallTimer?.Dispose();
        _stallTimer = null;
    }

    // ── Browser-model inference ───────────────────────────────────────────────

    private void OnStartLocalInference(StartLocalInferencePayload payload)
    {
        if (payload.DuelId != _duelId) return;
        // A hub reconnect replays this signal; the driver owns the de-duplication.
        if (!_localInference.TryClaim(payload.ModelId)) return;

        _sides[(int)SideOf(payload.ModelId)].IsBrowserModel = true;

        // Fire-and-forget; every failure path is handled inside the driver and reported to
        // the server, so there is no outcome here worth awaiting.
        _ = Task.Run(() => _localInference.RunAsync(payload));
    }

    private void OnDuelComplete(DuelDto completedDuel)
    {
        // Reachable twice: the hub push and the post-connect catch-up read can both see it.
        if (_completionHandled) return;
        _completionHandled = true;

        // Only a duel that was running while this page watched is news; the catch-up read of an
        // already-finished one is not.
        var wasRunning = _duelStillRunning;
        _duelStillRunning = false;
        StopStallTimer();
        _ = AudioService.StopRaceAsync();
        var duelId = _duelId;
        InvokeAsync((Func<Task>)(async () =>
        {
            var fresh = await ApiClient.GetDuelAsync(duelId);
            if (duelId != _duelId) return;   // the page moved to another duel meanwhile
            _duel = fresh;
            if (_duel is not null)
            {
                _leftResult  = _duel.Results.FirstOrDefault(r => r.ModelId == _duel.LeftModelId);
                _rightResult = _duel.Results.FirstOrDefault(r => r.ModelId == _duel.RightModelId);
                _leftStreamingHtml  = null;
                _rightStreamingHtml = null;
                // The verdict may already exist — a walkover is decided the moment the duel
                // completes — and its push may have been missed for the same reason as this one.
                if (!_verdictRecorded && TryRestoreVerdict(_duel))
                    await AnimateEloAsync();
                else
                    StartAutoJudgeCountdown();
            }
            // Terminal: paint now rather than waiting out a coalescing interval, and drop any
            // pending streaming render so it cannot repaint stale mid-stream state afterwards.
            _streamRender?.Dispose();
            _streamRender = null;
            StateHasChanged();
            if (wasRunning)
                await JS.InvokeVoidAsync("poNotify.notify", "Duel finished",
                    $"{LeftDisplayName} vs {RightDisplayName} — both outputs are in.");
        }));
    }

    // UX#7: Dispose location-changing handler and SignalR connection.
    public async ValueTask DisposeAsync()
    {
        _locationChangingReg?.Dispose();
        var hub = DetachFromDuel();
        if (hub is not null) await hub.DisposeAsync();
    }
}
