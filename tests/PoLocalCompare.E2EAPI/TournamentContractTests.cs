using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.E2EAPI;

/// <summary>
/// Black-box checks on the tournament HTTP surface — the contract a client can rely on, not
/// the arithmetic behind it. Kept to the most behaviour-covering cases per the
/// audit's test ratio; seeding and adjudication logic are covered by the unit tier.
/// </summary>
[Collection("E2EAPI")]
public sealed class TournamentContractTests(ApiAppFixture app)
{
    private const string Prompt = "Build a self-contained single HTML file with a click counter.";

    // ── Deny-by-default ───────────────────────────────────────────────────

    [Fact]
    public async Task NewReadEndpoints_AreClosedToAnonymousCallers()
    {
        // FallbackPolicy is RequireAuthenticatedUser, so a new endpoint is closed unless it
        // opts out. Both new reads are asserted here in one test: the policy is one decision,
        // and a path that opted out by accident is the same bug either way.
        using var client = app.CreateAnonymousClient();

        foreach (var path in new[] { "/api/tournaments/entrants", "/api/tournaments" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // ── Entrants ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Entrants_Returns200WithAnEntrantShape()
    {
        using var client = app.CreateAuthenticatedClient();
        await RegisterModelAsync("Entrant");

        var response = await client.GetAsync("/api/tournaments/entrants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetArrayLength() > 0);

        var first = body.EnumerateArray().First();
        Assert.True(first.TryGetProperty("modelId", out _));
        Assert.True(first.TryGetProperty("displayName", out _));
        Assert.True(first.TryGetProperty("currentElo", out _));
    }

    // ── Draw ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Draw_Returns201WithTheBracketAlreadyPopulated()
    {
        using var client = app.CreateAuthenticatedClient();
        var left = await RegisterModelAsync("T Left");
        var right = await RegisterModelAsync("T Right");

        var response = await client.PostAsJsonAsync("/api/tournaments", new
        {
            ModelIds = new[] { left, right },
            PromptText = Prompt,
        });

        // The bracket is drawn and persisted before the runner is queued, so the response is
        // the whole bracket rather than a bare id the client has to go and fetch.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("size").GetInt32());
        Assert.Equal(1, body.GetProperty("matches").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("tournamentId").GetString()));
    }

    // Draw validation (bad field size, short prompt) and the unknown-id 404 moved to
    // Integration's TournamentTests on 2026-10-26: each is one request and one status code, with
    // no journey for this tier to add.

    // ── Challenge surface — removed 2026-09-10 ────────────────────────────
    // Challenge mode (a duel carrying a ChallengeKind + budget) was reachable only by posting
    // a raw challengeKind/challengeThreshold body; no UI ever set one, and the Arena rendered a
    // budget line for a field the app could not produce. It was cut along with ChallengeRules,
    // ChallengeAdjudicator and the shared enum. The contract tests that asserted the echoed
    // challengeKind/challengeThreshold fields went with it.

    // ── Model profile surface ─────────────────────────────────────────────

    [Fact]
    public async Task Profile_ReturnsTheExpectedShapeForARealModelAnd404ForAnUnknownId()
    {
        using var client = app.CreateAuthenticatedClient();
        var modelId = await RegisterModelAsync("P Shape");

        var ok = await client.GetAsync($"/api/leaderboard/{modelId}/profile");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(modelId, body.GetProperty("modelId").GetString());
        Assert.True(body.TryGetProperty("eloHistory", out _));
        Assert.True(body.TryGetProperty("killList", out _));
        Assert.True(body.TryGetProperty("winningOutputs", out _));

        // The page contract differs from the kill-list: an unknown id there degrades to "no
        // history" because history is stored per-pair, while a profile is about a model and
        // a model the catalog does not know about is genuinely not found.
        var missing = await client.GetAsync("/api/leaderboard/01NOSUCHMODELIDXXXXXXXXXXX/profile");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private Task<string> RegisterModelAsync(string prefix) =>
        TestModels.RemoteAsync(app.Services, $"{prefix} {Guid.NewGuid():N}");
}
