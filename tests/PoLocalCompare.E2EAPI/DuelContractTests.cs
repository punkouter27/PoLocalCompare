using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.E2EAPI;

/// <summary>
/// Black-box contract checks on the duel endpoints: status codes, validation shape, and the
/// verdict invariants. Everything here goes over HTTP with no reach into server internals, so
/// these are the tests that would catch a breaking change to the surface the client depends on.
/// </summary>
[Collection(DuelCollection.Name)]
public sealed class DuelContractTests(DuelApiFixture app)
{
    private Task<string> RegisterModelAsync(string prefix) =>
        TestModels.RemoteAsync(app.Services, $"{prefix} {Guid.NewGuid():N}");

    /// <summary>
    /// A browser (WebLLM) model — the only kind allowed to post its own result, since it is the
    /// only kind that runs in the client. <c>/local-result</c> checks the type, so the local-result
    /// tests need one of these rather than the Remote default above.
    /// </summary>
    private Task<string> RegisterLocalModelAsync(string prefix) =>
        TestModels.LocalAsync(app.Services, $"{prefix} {Guid.NewGuid():N}");

    /// <summary>
    /// Waits until both models have reported, which is what a verdict requires.
    /// </summary>
    /// <remarks>
    /// Duel execution is asynchronous: <c>POST /api/duels</c> returns 202 after enqueueing, and
    /// the two result rows are written later on the background queue. A verdict posted before
    /// they exist is correctly refused with 409 "This duel is still running — a model has not
    /// reported a result yet", because ELO must never move on missing evidence. Posting one on
    /// the line after commencing therefore made these tests depend on whether a local HTTP round
    /// trip beat a background write to Azurite — the same latent race that was costing 7-9 of the
    /// 50 integration cases per run until 2026-09-10. The guard stays; the tests wait.
    /// </remarks>
    private static async Task WaitForBothResultsAsync(HttpClient client, string duelId)
    {
        var deadline = Environment.TickCount64 + 20_000;

        while (true)
        {
            var duel = await client.GetFromJsonAsync<JsonElement>($"/api/duels/{duelId}");
            if (duel.TryGetProperty("results", out var results)
                && results.ValueKind == JsonValueKind.Array
                && results.GetArrayLength() >= 2)
            {
                return;
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    $"Duel '{duelId}' did not report both results within 20 s. " +
                    "Duel execution runs on the background queue — a timeout here means it never ran.");
            }

            await Task.Delay(50);
        }
    }

    private async Task<(string DuelId, string Left, string Right)> CommenceAsync(HttpClient client)
        => await CommenceAsync(client, leftIsLocal: false);

    /// <summary>
    /// Hands a browser model the result its duel is waiting for, so the duel can finish.
    /// </summary>
    /// <remarks>
    /// A duel whose left model is Local (browser) is not driven by the server: DuelExecutionService
    /// waits in <c>WaitForLocalModelResultAsync</c> until the client POSTs /local-result, or until
    /// the execution watchdog fires (<c>Duel:TimeLimitSeconds</c>, 900 s). Until 2026-10-26 that
    /// wait ran on BackgroundTaskService's single consumer, so a duel left waiting blocked every
    /// duel queued behind it — which is what made Verdict_Twice_Returns409 and friends fail with
    /// the rest of the suite but pass in isolation. The wait is detached from the queue now, but
    /// releasing still matters: an unreleased duel keeps a watchdog and a SignalR heartbeat
    /// running for fifteen minutes after the test that started it has finished.
    /// </remarks>
    private static async Task ReleaseLocalDuelAsync(HttpClient client, string duelId, string leftModelId)
    {
        var response = await PostLocalResultAsync(client, duelId, leftModelId);
        response.EnsureSuccessStatusCode();
    }

    private async Task<(string DuelId, string Left, string Right)> CommenceAsync(
        HttpClient client,
        bool leftIsLocal)
    {
        var left = leftIsLocal
            ? await RegisterLocalModelAsync("Left")
            : await RegisterModelAsync("Left");
        var right = await RegisterModelAsync("Right");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = right,
            PromptText = "Build a single-file HTML kanban board with drag and drop.",
        });
        response.EnsureSuccessStatusCode();
        var duelId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("duelId").GetString()!;
        return (duelId, left, right);
    }

    private static Task<HttpResponseMessage> PostLocalResultAsync(
        HttpClient client,
        string duelId,
        string modelId,
        string html = "<html><body>Local</body></html>") =>
        client.PostAsJsonAsync($"/api/duels/{duelId}/local-result", new
        {
            ModelId = modelId,
            HtmlOutputRaw = html,
            TokenCount = 55,
            TotalDurationMs = 900L,
            WarmUpDurationMs = 100L,
            IsFailure = false,
        });

    // ── Commence ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Commence_Returns202WithALocationHeader()
    {
        using var client = app.CreateAuthenticatedClient();
        var left = await RegisterModelAsync("Loc Left");
        var right = await RegisterModelAsync("Loc Right");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = right,
            PromptText = "Build an HTML stopwatch with lap times.",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
    }

    // The empty-prompt 400 is Integration's PostDuel_EmptyPrompt_Returns400 (deduplicated 2026-10-26).
    [Fact]
    public async Task Commence_UnknownOpponent_Returns404()
    {
        using var client = app.CreateAuthenticatedClient();
        var left = await RegisterModelAsync("Left For Reject");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = "01NOTAREALMODELID00000000",
            PromptText = "Build an HTML calculator.",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Commence_SameModelBothSides_IsRejected()
    {
        using var client = app.CreateAuthenticatedClient();
        var only = await RegisterModelAsync("Solo");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = only,
            RightModelId = only,
            PromptText = "Build an HTML calculator.",
        });

        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Commence_Anonymous_PassesTheOpenGate_ButFailsOnMissingModel()
    {
        // Features:AllowAnonymousWrites opens the gate for un-authenticated callers in dev/test.
        // The fixture sets the flag on, so anon posts pass auth and reach the handler — which
        // then fails on the missing model with 404. Tests that care about the response from a
        // fully-valid request should use CreateAuthenticatedClient().
        using var client = app.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = "01AAAAAAAAAAAAAAAAAAAAAAAA",
            RightModelId = "01BBBBBBBBBBBBBBBBBBBBBBBB",
            PromptText = "Build an HTML calculator.",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Read ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListDuels_ReturnsTheCommencedDuel()
    {
        using var client = app.CreateAuthenticatedClient();
        var (duelId, _, _) = await CommenceAsync(client);

        var duels = await client.GetFromJsonAsync<JsonElement[]>("/api/duels?limit=100");

        Assert.Contains(duels!, d => d.GetProperty("duelId").GetString() == duelId);
    }

    [Fact]
    public async Task ListDuels_Anonymous_Returns401()
    {
        using var client = app.CreateAnonymousClient();

        var response = await client.GetAsync("/api/duels");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Verdict ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Verdict_NamesTheSidesAndIsRecordedAsHuman()
    {
        // Standards invariant: every verdict carries a source, and one submitted over the
        // verdict endpoint is by definition a person's. The winner / loser ids are the
        // dominant the human picked, mirrored into the response so the client can refresh.
        using var client = app.CreateAuthenticatedClient();
        var (duelId, left, right) = await CommenceAsync(client);
        await WaitForBothResultsAsync(client, duelId);

        var response = await client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = "Right" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(right, body.GetProperty("winnerModelId").GetString());
        Assert.Equal(left, body.GetProperty("loserModelId").GetString());

        var duel = await client.GetFromJsonAsync<JsonElement>($"/api/duels/{duelId}");
        Assert.Equal("Human", duel.GetProperty("verdictSource").GetString());
    }

    // A second verdict's 409 is Integration's PostVerdict_DuplicateVerdict_Returns409
    // (deduplicated 2026-10-26).

    // ── Local (browser) result ingest ──────────────────────────────────────

    [Fact]
    public async Task LocalResult_WithoutAModelId_Returns400()
    {
        // Both sides are Remote here, so the duel finishes on its own and this test needs no
        // release step — only a duel with a browser (Local) side waits for a client.
        using var client = app.CreateAuthenticatedClient();
        var (duelId, _, _) = await CommenceAsync(client);

        var response = await client.PostAsJsonAsync($"/api/duels/{duelId}/local-result", new
        {
            ModelId = "",
            HtmlOutputRaw = "<html></html>",
            TokenCount = 1,
            TotalDurationMs = 10L,
            WarmUpDurationMs = 1L,
            IsFailure = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LocalResult_AppearsOnTheDuelAndIsNormalizedBeforeStorage()
    {
        // Browser-inference path: the server never saw the tokens, the client POSTs the
        // finished output. It has to converge with the server-side path — and the markdown
        // fence has to be stripped so the browser model is scored on the same basis.
        using var client = app.CreateAuthenticatedClient();
        var (duelId, left, _) = await CommenceAsync(client, leftIsLocal: true);

        await PostLocalResultAsync(client, duelId, left, "```html\n<html><body>Local</body></html>\n```");

        var duel = await client.GetFromJsonAsync<JsonElement>($"/api/duels/{duelId}");
        var result = duel.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("modelId").GetString() == left);

        Assert.DoesNotContain("```", result.GetProperty("htmlOutputRaw").GetString());
    }

    [Fact]
    public async Task LocalResult_ForAModelThatIsNotThisDuelsBrowserSide_Returns400()
    {
        // The (duelId, modelId) pair is the storage key. Unchecked, a caller picks both and can
        // write a result row into any duel for any model — which is what DuelExecutionService
        // hands to the judge, so it decides duels the caller is not part of.
        //
        // Both rejections are the same guard, so they are asserted together: a model that is in
        // no way part of this duel, and the duel's own non-browser side (only browser models run
        // in the client, so only they may report their own output).
        using var client = app.CreateAuthenticatedClient();
        var (duelId, left, right) = await CommenceAsync(client, leftIsLocal: true);
        var outsider = await RegisterLocalModelAsync("Outsider");

        var forOutsider = await PostLocalResultAsync(client, duelId, outsider);
        var forRemoteSide = await PostLocalResultAsync(client, duelId, right);

        Assert.Equal(HttpStatusCode.BadRequest, forOutsider.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, forRemoteSide.StatusCode);

        // Both were refused, so the browser side is still waiting; release the duel.
        await ReleaseLocalDuelAsync(client, duelId, left);
    }

    [Fact]
    public async Task LocalResult_PostedTwice_Returns409()
    {
        // The repository upserts with Replace, so a second post used to silently overwrite the
        // output the duel is being judged on — including after the fact, rewriting the archive.
        using var client = app.CreateAuthenticatedClient();
        var (duelId, left, _) = await CommenceAsync(client, leftIsLocal: true);

        var first = await PostLocalResultAsync(client, duelId, left);
        var second = await PostLocalResultAsync(client, duelId, left, "<html><body>Forged</body></html>");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task LocalResult_ForAnUnknownDuel_Returns404()
    {
        // It carried an unconditional AllowAnonymous() and never loaded the duel at all, so any
        // caller could mint rows under a duel id of their choosing.
        using var client = app.CreateAuthenticatedClient();
        var model = await RegisterLocalModelAsync("Orphan");

        var response = await PostLocalResultAsync(client, "01AAAAAAAAAAAAAAAAAAAAAAAA", model);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LocalResult_FollowsTheSameAnonymousGateAsTheOtherWrites()
    {
        // It used to opt out of authorization entirely, on the premise that the WebLLM worker
        // called it. The client posts it from the app origin with the session cookie attached,
        // so it now honours Features:AllowAnonymousWrites like POST /api/duels and /verdict —
        // which is true in dev/test, hence "not 401" rather than "401" here. The endpoint
        // returns 400 because ModelId is empty; the gate is the salient assertion.
        using var client = app.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/duels/01AAAAAAAAAAAAAAAAAAAAAAAA/local-result", new { ModelId = "" });

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Archive export ─────────────────────────────────────────────────────

    [Fact]
    public async Task Report_ForAJudgedDuel_ReturnsSelfContainedHtml()
    {
        using var client = app.CreateAuthenticatedClient();
        var (duelId, _, _) = await CommenceAsync(client);
        await WaitForBothResultsAsync(client, duelId);
        await client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = "Left" });

        var response = await client.GetAsync($"/api/duels/{duelId}/report");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
    }
}
