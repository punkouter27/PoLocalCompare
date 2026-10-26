using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Api.Features.Leaderboard;

using Microsoft.Extensions.DependencyInjection;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Ids;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// Integration tests for the Duels API endpoints.
/// Uses a real Azurite container for Table Storage and mocks AI inference.
/// </summary>
[Collection("Integration")]
public sealed class DuelsEndpointTests : IAsyncLifetime
{
    private readonly string _connectionString;
    private IntegrationHost _host = null!;
    private HttpClient _client = null!;

    public DuelsEndpointTests(AzuriteFixture azurite)
    {
        _connectionString = azurite.ConnectionString;
    }

    public Task InitializeAsync()
    {
        _host = new IntegrationHost(_connectionString);
        _client = _host.Client;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // ── Helpers ────────────────────────────────────────────────────────────

    private Task<string> SeedRemoteModelAsync(string modelId, string displayName) =>
        TestModels.RemoteAsync(_host.Services, displayName);

    // ── POST /api/duels → 202 Accepted ────────────────────────────────────

    [Fact]
    public async Task PostDuel_ValidRequest_Returns202WithDuelId()
    {
        var leftId = await SeedRemoteModelAsync(ModelId.From("left-int-1"), "Left Model");
        var rightId = await SeedRemoteModelAsync(ModelId.From("right-int-1"), "Right Model");

        var payload = new
        {
            LeftModelId = leftId,
            RightModelId = rightId,
            PromptText = "Build a simple HTML counter app.",
        };

        var response = await _client.PostAsJsonAsync("/api/duels", payload);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var duelId = body.GetProperty("duelId").GetString();
        Assert.NotNull(duelId);
        Assert.NotEmpty(duelId);
    }

    // ── A browser duel waits off the queue ─────────────────────────────────

    /// <summary>
    /// Until 2026-10-26 a duel with a browser side held <c>BackgroundTaskService</c>'s single
    /// worker for as long as the tab took, so a second user's server-side duel could not start.
    /// It now leaves the queue as soon as it is dequeued, and wakes when the tab posts.
    /// </summary>
    [Fact]
    public async Task BrowserDuel_WaitingForItsTab_DoesNotHoldUpAServerDuel()
    {
        var browser = await TestModels.LocalAsync(_host.Services, $"Tab {Guid.NewGuid():N}");
        var left = await TestModels.RemoteAsync(_host.Services, "Queue Left");
        var right = await TestModels.RemoteAsync(_host.Services, "Queue Right");

        var browserDuel = await DuelTestFlow.CommenceAsync(_client, browser, left);
        var serverDuel = await DuelTestFlow.CommenceAsync(_client, left, right);

        // Queued behind a tab that has not reported — this is what used to time out.
        await DuelTestFlow.WaitForBothResultsAsync(_client, serverDuel, timeoutMs: 10_000);

        var posted = await _client.PostAsJsonAsync($"/api/duels/{browserDuel}/local-result", new LocalResultRequest(
            ModelId.From(browser), "<html><body>from the tab</body></html>", 12, 900, 100, IsFailure: false, FailureReason: null));
        posted.EnsureSuccessStatusCode();

        var deadline = Environment.TickCount64 + 10_000;
        bool completed;
        do
        {
            await Task.Delay(100);
            var duel = await _client.GetFromJsonAsync<JsonElement>($"/api/duels/{browserDuel}");
            completed = duel.TryGetProperty("completedAt", out var at) && at.ValueKind == JsonValueKind.String;
        } while (!completed && Environment.TickCount64 < deadline);
        Assert.True(completed, "The browser duel did not complete after its tab posted a result.");
    }

    // ── POST /api/duels → 429 from the two guards on paid inference ───────

    private async Task<HttpResponseMessage> StartDuelAsync(IntegrationHost host)
    {
        var left = await TestModels.RemoteAsync(host.Services, "Guard Left");
        var right = await TestModels.RemoteAsync(host.Services, "Guard Right");
        return await host.Client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = right,
            PromptText = "Build a simple HTML counter app.",
        });
    }

    [Fact]
    public async Task PostDuel_DailySpendCapReached_Returns429ForRemoteModels()
    {
        await using var host = new IntegrationHost(_connectionString,
            settings: new Dictionary<string, string> { ["Limits:DailySpendCapUsd"] = "0.5" });

        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDuelResultRepository>().SaveAsync(
                new DuelResult(DuelId.New(), ModelId.New()) { ApiCostUsd = 1.0 });
        }

        var response = await StartDuelAsync(host);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("budget", body.GetProperty("detail").GetString());
    }

    /// <summary>
    /// Calls with no duel row (prompt refine, scouting reports) are recorded in the spend ledger,
    /// and the cap counts them. The cap is set far above anything the rest of this run records
    /// in duel results, so only the ledger row can push today over it.
    /// </summary>
    [Fact]
    public async Task PostDuel_LedgerSpendReachesTheCap_Returns429()
    {
        await using var host = new IntegrationHost(_connectionString,
            settings: new Dictionary<string, string> { ["Limits:DailySpendCapUsd"] = "100" });

        await host.Services.GetRequiredService<SpendLedger>().RecordAsync("prompt-refine", "test-deployment", 1000);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await StartDuelAsync(host)).StatusCode);
    }

    [Fact]
    public async Task PostDuel_OverThePerMinuteLimit_Returns429()
    {
        await using var host = new IntegrationHost(_connectionString,
            settings: new Dictionary<string, string> { ["Limits:DuelStartsPerMinute"] = "1" });

        Assert.Equal(HttpStatusCode.Accepted, (await StartDuelAsync(host)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await StartDuelAsync(host)).StatusCode);
    }

    // ── POST /api/duels → 400 when PromptText is empty ────────────────────

    [Fact]
    public async Task PostDuel_EmptyPrompt_Returns400()
    {
        var leftId = await SeedRemoteModelAsync(ModelId.From("left-int-2"), "Left Model 2");
        var rightId = await SeedRemoteModelAsync(ModelId.From("right-int-2"), "Right Model 2");

        var payload = new
        {
            LeftModelId = leftId,
            RightModelId = rightId,
            PromptText = "",
        };

        var response = await _client.PostAsJsonAsync("/api/duels", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── POST /api/duels then POST verdict then GET leaderboard full flow ──

    [Fact]
    public async Task FullFlow_CommenceDuel_RecordVerdict_LeaderboardReflectsEloChange()
    {
        var leftId = await SeedRemoteModelAsync(ModelId.From("left-flow-1"), "Flow Left");
        var rightId = await SeedRemoteModelAsync(ModelId.From("right-flow-1"), "Flow Right");

        // Step 1: Commence duel
        var commenceResponse = await _client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = leftId,
            RightModelId = rightId,
            PromptText = "Build a countdown timer in HTML.",
        });

        Assert.Equal(HttpStatusCode.Accepted, commenceResponse.StatusCode);

        var duelBody = await commenceResponse.Content.ReadFromJsonAsync<JsonElement>();
        var duelId = duelBody.GetProperty("duelId").GetString()!;

        // Duel execution is asynchronous: the 202 above only means the duel was enqueued. The
        // verdict is refused with 409 until both result rows exist, so wait for them.
        await DuelTestFlow.WaitForBothResultsAsync(_client, duelId);

        // Step 2: Record verdict (left wins)
        var verdictResponse = await _client.PostAsJsonAsync(
            $"/api/duels/{duelId}/verdict",
            new { Verdict = "Left" });

        Assert.Equal(HttpStatusCode.OK, verdictResponse.StatusCode);

        var verdictBody = await verdictResponse.Content.ReadFromJsonAsync<JsonElement>();
        var eloShiftWinner = verdictBody.GetProperty("eloShiftWinner").GetDouble();
        var eloShiftLoser = verdictBody.GetProperty("eloShiftLoser").GetDouble();

        Assert.True(eloShiftWinner > 0, "Winner should gain ELO.");
        Assert.True(eloShiftLoser < 0, "Loser should lose ELO.");

        // Step 3: Verify leaderboard shows updated ELOs
        var leaderboardResponse = await _client.GetAsync("/api/leaderboard");
        Assert.Equal(HttpStatusCode.OK, leaderboardResponse.StatusCode);

        var leaderboardBody = await leaderboardResponse.Content.ReadFromJsonAsync<JsonElement>();
        var entries = leaderboardBody.EnumerateArray().ToList();

        // Top-ranked model should have ELO > 1200 (starting ELO)
        var topEntry = entries.First();
        var topElo = topEntry.GetProperty("currentElo").GetDouble();
        Assert.True(topElo > 1200, $"Top leaderboard ELO should be > 1200 after a win, got {topElo}.");
    }

    // ── POST /api/duels/{id}/verdict → 409 on duplicate ──────────────────

    [Fact]
    public async Task PostVerdict_DuplicateVerdict_Returns409()
    {
        var leftId = await SeedRemoteModelAsync(ModelId.From("left-dup-1"), "Dup Left");
        var rightId = await SeedRemoteModelAsync(ModelId.From("right-dup-1"), "Dup Right");

        var commenceResponse = await _client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = leftId,
            RightModelId = rightId,
            PromptText = "Write a hello world app.",
        });
        var duelBody = await commenceResponse.Content.ReadFromJsonAsync<JsonElement>();
        var duelId = duelBody.GetProperty("duelId").GetString()!;

        await DuelTestFlow.WaitForBothResultsAsync(_client, duelId);

        // First verdict
        var first = await _client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = "Left" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Duplicate verdict → 409
        var second = await _client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = "Right" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }
}
