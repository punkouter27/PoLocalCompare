using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PoLocalCompare.Api.Features.Tournaments;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Integration;

/// <summary>
/// Tournament draw and validation against real storage.
/// </summary>
/// <remarks>
/// Everything asserted here is settled by the time <c>POST /api/tournaments</c> responds — the
/// bracket is drawn and persisted before the runner is queued. That is deliberate: the runner
/// then plays matches in the background for as long as the host lives, so any assertion about
/// how far it has got would race it. What the runner does is covered by unit tests over
/// <c>BracketPlanner</c> and <c>Tournament.RecordWinner</c>, which need no storage at all.
/// </remarks>
[Collection("Integration")]
public sealed class TournamentTests(AzuriteFixture azurite) : IAsyncLifetime
{
    private IntegrationHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = new IntegrationHost(azurite.ConnectionString);
        _client = _host.Client;
        await AbandonInFlightAsync();
    }

    /// <summary>
    /// Only one tournament may be in flight, and with the judge off here an earlier test's
    /// bracket never finishes on its own — it waits for the next host's startup sweep to void
    /// its duel, which is a race against this test's first draw. So every test starts clean.
    /// </summary>
    private async Task AbandonInFlightAsync()
    {
        using var scope = _host.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<TournamentRepository>();
        foreach (var t in await repository.ListRecentAsync(50))
        {
            if (t.Status is not (TournamentStatus.Pending or TournamentStatus.Running)) continue;
            t.Abandon("Left in flight by an earlier test.");
            t.ETag = null; // unconditional: a resumed runner may have touched it since the read
            await repository.UpdateAsync(t);
        }
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private const string Prompt = "Build a self-contained single HTML file with a click counter.";

    private Task<string> RegisterModelAsync(string name, string modelType = "Remote") =>
        modelType == "Local" ? TestModels.LocalAsync(_host.Services, name) : TestModels.RemoteAsync(_host.Services, name);

    private async Task<string[]> RegisterFieldAsync(string prefix, int count) =>
        await Task.WhenAll(Enumerable.Range(1, count).Select(i => RegisterModelAsync($"{prefix} {i}")));

    private async Task<HttpResponseMessage> DrawAsync(IEnumerable<string> modelIds, string prompt = Prompt) =>
        await _client.PostAsJsonAsync("/api/tournaments", new { ModelIds = modelIds, PromptText = prompt });

    private static JsonElement[] MatchesOf(JsonElement tournament) =>
        tournament.GetProperty("matches").EnumerateArray().ToArray();

    // ── Auth ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Entrants_Anonymous_Returns401()
    {
        using var client = _host.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tournaments/entrants")).StatusCode);
    }

    // ── Entrants ──────────────────────────────────────────────────────────

    /// <summary>
    /// The field is offered strongest-first because that ordering IS the seeding — the form
    /// shows the draw before the user commits to it.
    /// </summary>
    [Fact]
    public async Task Entrants_AreListedStrongestFirst()
    {
        await RegisterFieldAsync("TE Seed", 3);

        var entrants = (await (await _client.GetAsync("/api/tournaments/entrants"))
            .Content.ReadFromJsonAsync<JsonElement[]>())!;

        var elos = entrants.Select(e => e.GetProperty("currentElo").GetDouble()).ToArray();
        Assert.Equal(elos.OrderByDescending(e => e), elos);
    }

    /// <summary>
    /// Browser models entered the field on 2026-08-23, reversing PRD §9 item 21. The caveat is
    /// on the page, not in the catalog: the Tournament tab drives WebGPU matches itself.
    /// </summary>
    [Fact]
    public async Task Entrants_IncludeBrowserModels()
    {
        var browser = await RegisterModelAsync("TE Browser", modelType: "Local");

        var entrants = (await (await _client.GetAsync("/api/tournaments/entrants"))
            .Content.ReadFromJsonAsync<JsonElement[]>())!;

        Assert.Contains(entrants, e => e.GetProperty("modelId").GetString() == browser);
    }
    [Theory]
    [InlineData(8)]
    public async Task Draw_AcceptsEverySupportedSize(int count)
    {
        var field = await RegisterFieldAsync($"TE Ok{count}", count);

        var response = await DrawAsync(field);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Draw_RejectsAModelEnteredTwice()
    {
        var field = await RegisterFieldAsync("TE Dupe", 2);

        var response = await DrawAsync([field[0], field[0]]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Draw_AcceptsAMixedRemoteAndBrowserField()
    {
        var remote = await RegisterModelAsync("TE Mixed Remote");
        var browser = await RegisterModelAsync("TE Mixed Browser", modelType: "Local");

        var response = await DrawAsync([remote, browser]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>4 was a supported size until 2026-08-23; only 2 and 8 are now.</summary>
    [Fact]
    public async Task Draw_RejectsAFourModelField()
    {
        var field = await RegisterFieldAsync("TE Four", 4);

        var response = await DrawAsync(field);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A short prompt is a length-validation failure and an unknown model id is a catalog
    /// failure — both pass through the same validation path, so they share the assertion.
    /// </summary>
    [Theory]
    [InlineData("hi", false)]   // prompt under minimum length
    [InlineData(null, true)]    // one of the model ids is unknown to the catalog
    public async Task Draw_RejectsARequestThatFailsBasicValidation(string? badPrompt, bool useUnknownModelId)
    {
        // The model-name suffix is unique per InlineData so a run against a hot Azurite cannot
        // collide with the other Draw tests that share the "TE Bad" prefix.
        var suffix = useUnknownModelId ? "Unknown" : "Short";
        var field = await RegisterFieldAsync($"TE Bad {suffix} {Guid.NewGuid():N}", 2);
        var ids = useUnknownModelId ? [field[0], "01NOSUCHMODELIDXXXXXXXXXXX"] : field;

        var response = await DrawAsync(ids, badPrompt ?? Prompt);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    // ── Persistence ───────────────────────────────────────────────────────

    [Fact]
    public async Task Draw_IsReadableBackByItsId()
    {
        var field = await RegisterFieldAsync("TE Persist", 8);

        var drawn = await (await DrawAsync(field)).Content.ReadFromJsonAsync<JsonElement>();
        var id = drawn.GetProperty("tournamentId").GetString()!;

        var fetched = await (await _client.GetAsync($"/api/tournaments/{id}")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(id, fetched.GetProperty("tournamentId").GetString());
        Assert.Equal(8, fetched.GetProperty("size").GetInt32());
        Assert.Equal(7, MatchesOf(fetched).Length);
        Assert.Equal(Prompt, fetched.GetProperty("promptText").GetString());
    }

    [Fact]
    public async Task Get_UnknownTournament_Returns404()
    {
        var response = await _client.GetAsync("/api/tournaments/01NOSUCHTOURNAMENTIDXXXXXX");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Swiss ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A Swiss run played to the end through storage, re-read before every round the way the
    /// stateless runner does: each round is paired from the stored results alone, the format
    /// survives the round trip, and the API serves the final table with the leader crowned.
    /// Results are written straight to the aggregate because the judge is off in this host —
    /// the duel side is the same as a bracket's and is covered there.
    /// </summary>
    [Fact]
    public async Task Swiss_PlayedThroughStorage_CompletesWithStandings()
    {
        var field = await RegisterFieldAsync($"TE Swiss {Guid.NewGuid():N}", 8);

        TournamentId id;
        using (var scope = _host.Services.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateTournamentHandler>();
            var drawn = await handler.HandleAsync(
                field.Select(f => ModelId.From(f)).ToList(), Prompt, actor: null, TournamentFormat.Swiss);
            id = drawn.TournamentId;
        }

        for (var round = 0; round < 3; round++)
        {
            using var scope = _host.Services.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<TournamentRepository>();
            var tournament = (await repository.GetByIdAsync(id))!;

            Assert.Equal(TournamentFormat.Swiss, tournament.Format);
            foreach (var m in tournament.Matches.Where(m => m.Round == round).ToList())
            {
                if (m.Index == 3) tournament.RecordTie(round, m.Index);   // one draw a round
                else tournament.RecordWinner(round, m.Index, m.SlotAModelId, m.SlotAName);
            }
            await repository.UpdateAsync(tournament);
        }

        var dto = await (await _client.GetAsync($"/api/tournaments/{id}")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Swiss", dto.GetProperty("format").GetString());
        Assert.Equal("Complete", dto.GetProperty("status").GetString());
        Assert.Equal(12, MatchesOf(dto).Length);

        var standings = dto.GetProperty("standings").EnumerateArray().ToArray();
        Assert.Equal(8, standings.Length);
        Assert.Equal(standings[0].GetProperty("modelId").GetString(), dto.GetProperty("championModelId").GetString());
        Assert.Equal(12.0, standings.Sum(s => s.GetProperty("points").GetDouble()));   // a point per game
    }

    // ── One tournament at a time ───────────────────────────────────────────

    /// <summary>
    /// The runner holds the single-consumer BackgroundTaskService slot for the whole life of a
    /// bracket. A second create would queue behind it and look stuck from the page. The
    /// endpoint surfaces that as a 409 with the running bracket's id, so the page can link the
    /// user to it instead of letting them stare at "Starting the next match…" forever.
    /// </summary>
    [Fact]
    public async Task Create_WhileAnotherIsInFlight_Returns409WithTheRunningBracketId()
    {
        var field = await RegisterFieldAsync("TE InFlight", 2);

        var first = await DrawAsync(field);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await DrawAsync(field);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        // The conflict body hands the page the id of the running bracket. Without it, the
        // 409 is just a message the user has nowhere to act on.
        Assert.True(body.TryGetProperty("tournamentId", out var runningId));
        Assert.False(string.IsNullOrWhiteSpace(runningId.GetString()));
    }

}
