using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// The client's whole view of the API. Every call goes through the same
/// <see cref="JsonOptions"/> so enum casing cannot drift between endpoints.
///
/// Kept as one type: this was five partial files totalling ~230 lines, which cost five
/// using-blocks and five namespace declarations to save nothing. Comment banners mark
/// the slices.
/// </summary>
public sealed class DuelApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<DuelApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public DuelApiClient(HttpClient http, ILogger<DuelApiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    // ── Duels ────────────────────────────────────────────────────────────────

    /// <param name="autoJudgeDelaySeconds">
    /// Per-duel grace window before the AI judge decides. Null keeps the server's configured
    /// value; a tournament passes 0 so an unattended run never stalls waiting for a human pick.
    /// </param>
    public async Task<DuelDto?> CommenceDuelAsync(
        string leftModelId,
        string rightModelId,
        string promptText,
        int? autoJudgeDelaySeconds = null)
    {
        var body = new
        {
            leftModelId,
            rightModelId,
            promptText,
            autoJudgeDelaySeconds,
        };
        var response = await _http.PostAsJsonAsync("/api/duels", body);
        // Surface the validation body rather than the raw `net_http_message_not_success…` reason
        // string. The endpoint returns RFC 7807 ProblemDetails; the per-field message is what the
        // user actually needs to read (e.g. "PromptText must be at least 10 characters.").
        if (!response.IsSuccessStatusCode)
        {
            var problem = await TryReadProblemAsync(response);
            throw new DuelStartException(response.StatusCode, problem);
        }
        return await response.Content.ReadFromJsonAsync<DuelDto>(JsonOptions);
    }

    /// <summary>
    /// Asks the server to rewrite a rough prompt into a brief with explicit requirements.
    /// Throws <see cref="DuelStartException"/> with the server's own sentence when it declines
    /// (mock mode, the spend cap, the rate limit) or the model does not answer.
    /// </summary>
    public async Task<string> RefinePromptAsync(string prompt)
    {
        var response = await _http.PostAsJsonAsync("/api/prompts/refine", new RefinePromptRequest(prompt), JsonOptions);
        if (!response.IsSuccessStatusCode)
            throw new DuelStartException(response.StatusCode, await TryReadProblemAsync(response));

        var refined = await response.Content.ReadFromJsonAsync<RefinePromptResponse>(JsonOptions);
        return refined?.Prompt ?? prompt;
    }

    /// <summary>
    /// Best-effort parse of an RFC 7807 ProblemDetails body. Returns null if the body is empty
    /// or not in the expected shape, so callers fall back to the status code's reason phrase.
    /// </summary>
    private static async Task<ProblemDetailsLite?> TryReadProblemAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemDetailsLite>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every error body this client reads, as far as it reads them: RFC 7807 problem details
    /// (Title/Detail, and Errors for a validation problem), the <c>{ error }</c> body of the API's
    /// 409s, and the running bracket's id on a tournament 409. Declared here rather than using
    /// <c>ValidationProblemDetails</c>, which lives in the MVC assembly the client does not reference.
    /// </summary>
    public sealed class ProblemDetailsLite
    {
        public string? Title { get; init; }
        public string? Detail { get; init; }
        // ASP.NET's ValidationProblemDetails nests per-field errors here.
        public Dictionary<string, string[]>? Errors { get; init; }
        public string? Error { get; init; }
        public string? TournamentId { get; init; }

        /// <summary>
        /// First non-empty field error if present (this is what the API surfaces for the
        /// PromptText length check), else <see cref="Detail"/>, else <see cref="Title"/>.
        /// </summary>
        public string? FirstMessage()
        {
            if (Errors is { Count: > 0 })
            {
                var first = Errors.Values.SelectMany(v => v).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
                if (first is not null) return first;
            }
            return Detail ?? Title;
        }
    }

    /// <summary>
    /// Thrown by <see cref="CommenceDuelAsync"/> when the server rejects the request. The
    /// message comes from the parsed <c>ProblemDetails</c> body so the user sees the actual
    /// reason — "PromptText must be at least 10 characters." — rather than the framework's
    /// "BadRequest" reason phrase.
    /// </summary>
    public sealed class DuelStartException : Exception
    {
        public System.Net.HttpStatusCode StatusCode { get; }

        public DuelStartException(System.Net.HttpStatusCode statusCode, ProblemDetailsLite? problem)
            : base(problem?.FirstMessage() ?? $"{statusCode} ({(int)statusCode})")
        {
            StatusCode = statusCode;
        }
    }

    public async Task<VerdictResponseDto?> RecordVerdictAsync(DuelId duelId, VerdictRequestDto request)
    {
        var response = await _http.PostAsJsonAsync($"/api/duels/{duelId}/verdict", request, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VerdictResponseDto>(JsonOptions);
    }

    /// <summary>
    /// Reads one duel, or null when the id names nothing.
    /// </summary>
    /// <remarks>
    /// Uses <c>GetAsync</c> rather than <c>GetFromJsonAsync</c> so a 404 becomes the null this
    /// signature already promises. The old version threw, which escaped past the Arena's
    /// "Duel not found" branch to the ErrorBoundary — a mistyped or deleted duel id rendered a
    /// raw <c>HttpRequestException</c> stack instead of a message. This is also the polling
    /// path used while awaiting a verdict, where a transient failure must not tear the page down.
    /// </remarks>
    public async Task<DuelDto?> GetDuelAsync(DuelId duelId)
    {
        var response = await _http.GetAsync($"/api/duels/{duelId}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DuelDto>(JsonOptions);
    }

    public async Task PostLocalResultAsync(
        DuelId duelId,
        ModelId modelId,
        string htmlOutputRaw,
        int tokenCount,
        long totalDurationMs,
        long warmUpDurationMs,
        bool isFailure = false,
        string? failureReason = null)
    {
        var body = new
        {
            modelId,
            htmlOutputRaw,
            tokenCount,
            totalDurationMs,
            warmUpDurationMs,
            isFailure,
            failureReason
        };
        var response = await _http.PostAsJsonAsync($"/api/duels/{duelId}/local-result", body);
        response.EnsureSuccessStatusCode();
    }

    /// <param name="before">Keyset cursor: the oldest duel id already loaded. Only strictly older duels come back.</param>
    public async Task<IReadOnlyList<DuelSummaryDto>?> ListDuelsAsync(
        int limit = 20,
        DuelId? before = null)
    {
        var url = $"/api/duels?limit={limit}";
        if (before is { IsEmpty: false } cursor)
            url += $"&before={Uri.EscapeDataString(cursor.Value)}";
        return await _http.GetFromJsonAsync<IReadOnlyList<DuelSummaryDto>>(url, JsonOptions);
    }

    // ── Leaderboard ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<LeaderboardEntryDto>?> GetLeaderboardAsync(string sortBy = "Elo")
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<LeaderboardEntryDto>>(
            $"/api/leaderboard?sortBy={Uri.EscapeDataString(sortBy)}", JsonOptions);
    }

    /// <summary>
    /// Reads one model's profile, or null when the id names nothing.
    /// </summary>
    /// <remarks>
    /// Uses <c>GetAsync</c> rather than <c>GetFromJsonAsync</c> for the same reason
    /// <see cref="GetDuelAsync"/> does: a retired or mistyped model id must surface as the null
    /// this signature promises, not as an exception that escapes to the ErrorBoundary.
    /// </remarks>
    public async Task<ModelProfileDto?> GetModelProfileAsync(ModelId modelId)
    {
        var response = await _http.GetAsync($"/api/leaderboard/{modelId}/profile");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ModelProfileDto>(JsonOptions);
    }

    /// <summary>
    /// The model's scouting report, or null when there is none to show — too few judged duels,
    /// a new one cannot be paid for right now, or the writer failed. Never throws for those:
    /// the report is an extra on the profile, and its absence is not an error.
    /// </summary>
    public async Task<ScoutingReportDto?> GetScoutingReportAsync(ModelId modelId)
    {
        var response = await _http.GetAsync($"/api/leaderboard/{modelId}/scouting-report");
        if (response.StatusCode != System.Net.HttpStatusCode.OK) return null;
        return await response.Content.ReadFromJsonAsync<ScoutingReportDto>(JsonOptions);
    }

    // ── Tournaments ──────────────────────────────────────────────────────────

    /// <summary>Models eligible to enter a bracket, strongest first — which is the seeding order.</summary>
    public async Task<IReadOnlyList<TournamentEntrantDto>?> GetTournamentEntrantsAsync()
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<TournamentEntrantDto>>(
            "/api/tournaments/entrants", JsonOptions);
    }

    /// <summary>
    /// Draws a bracket and starts it running server-side. The response is the drawn bracket, so
    /// the page can paint the whole thing before the first match has finished.
    /// </summary>
    /// <remarks>
    /// Reads the body on a 400 rather than throwing: every rejection here is a message the user
    /// needs (wrong field size, a browser model in the field, a prompt that is too short), and
    /// EnsureSuccessStatusCode would discard all of it. A 409 surfaces as
    /// <see cref="TournamentInFlightException"/> so the page can link to the running bracket
    /// rather than just showing the message.
    /// </remarks>
    public async Task<TournamentDto?> CreateTournamentAsync(IReadOnlyList<ModelId> modelIds, string promptText)
    {
        var body = new { modelIds, promptText };
        var response = await _http.PostAsJsonAsync("/api/tournaments", body, JsonOptions);

        // 429 is the per-minute limit or the daily spend cap; both carry a sentence to show.
        if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.TooManyRequests)
        {
            var problem = await TryReadProblemAsync(response);
            throw new InvalidOperationException(problem?.FirstMessage() ?? "That bracket could not be drawn.");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // The body is a small anonymous object the endpoint hands back to identify the
            // running bracket by id — that is what the page links the user to.
            throw new TournamentInFlightException(await TryReadProblemAsync(response));
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TournamentDto>(JsonOptions);
    }

    /// <summary>
    /// Thrown by <see cref="CreateTournamentAsync"/> when the server rejects the request
    /// because another tournament is already in flight. Carries the running bracket's id so
    /// the page can deep-link to it instead of just showing a message.
    /// </summary>
    public sealed class TournamentInFlightException : Exception
    {
        public string? RunningTournamentId { get; }

        public TournamentInFlightException(ProblemDetailsLite? payload)
            : base(payload?.Detail ?? payload?.Title ?? "Another tournament is already running.")
        {
            RunningTournamentId = payload?.TournamentId;
        }
    }

    /// <summary>Reads one bracket, or null when the id names nothing.</summary>
    public async Task<TournamentDto?> GetTournamentAsync(TournamentId tournamentId)
    {
        var response = await _http.GetAsync($"/api/tournaments/{tournamentId}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TournamentDto>(JsonOptions);
    }

    public async Task<IReadOnlyList<TournamentDto>?> ListTournamentsAsync(int limit = 10)
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<TournamentDto>>(
            $"/api/tournaments?limit={limit}", JsonOptions);
    }

    // ── Models ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ModelDto>?> GetModelsAsync()
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<ModelDto>>("/api/models", JsonOptions);
    }

    public async Task<IReadOnlyList<ModelAvailabilityDto>?> GetModelAvailabilityAsync()
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<ModelAvailabilityDto>>("/api/models/availability", JsonOptions);
    }

    /// <summary>Models that can be added at runtime and are not in the catalog yet.</summary>
    public async Task<ModelDiscoveryDto?> GetModelDiscoveryAsync()
    {
        return await _http.GetFromJsonAsync<ModelDiscoveryDto>("/api/models/discover", JsonOptions);
    }

    /// <summary>
    /// Adds one discovered model. Throws <see cref="InvalidOperationException"/> carrying the
    /// server's reason on a 400 or 409 — "no prebuiltAppConfig entry", "already in the
    /// catalog" — because that sentence is the whole of what the page needs to show.
    /// </summary>
    public async Task<ModelDto?> AddDiscoveredModelAsync(AddDiscoveredModelRequest request)
    {
        var response = await _http.PostAsJsonAsync("/api/models/discovered", request, JsonOptions);

        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var problem = await TryReadProblemAsync(response);
            throw new InvalidOperationException(problem?.FirstMessage() ?? "That model cannot be added.");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var conflict = await TryReadProblemAsync(response);
            throw new InvalidOperationException(conflict?.Error ?? "That model is already in the catalog.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ModelDto>(JsonOptions);
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────
    // Ollama's two probes are deliberately absent here. /diag drives them straight from
    // diag-models.js (fetch), because that page has to work when the WASM client is the broken
    // thing — see CLAUDE.md. Client-side wrappers for them had no caller and were removed
    // 2026-09-10.

    public async Task<SmokeSnapshotDto?> GetSmokeSnapshotAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<SmokeSnapshotDto>("/api/diag/smoke", JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Smoke snapshot fetch failed");
            return null;
        }
    }

    public sealed class SmokeSnapshotDto
    {
        public string Status { get; set; } = "Unknown";
        public string Environment { get; set; } = "Unknown";
        public SmokeModelsDto Models { get; set; } = new();
    }

    public sealed class SmokeModelsDto
    {
        public int Total { get; set; }
        public int LocalService { get; set; }
        public bool CloudMode { get; set; }
    }
}
