using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PoLocalCompare.Api.Features.Models;
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
    [InlineData(16)]
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

    /// <summary>
    /// 8 was the bracket until 2026-11-07. Drawn into a 16 it would leave a first-round match
    /// with nobody in it, so a field needs 2, or 9 to 16.
    /// </summary>
    [Fact]
    public async Task Draw_RejectsAnEightModelField()
    {
        var field = await RegisterFieldAsync("TE Eight", 8);

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
        var field = await RegisterFieldAsync("TE Persist", 16);

        var drawn = await (await DrawAsync(field)).Content.ReadFromJsonAsync<JsonElement>();
        var id = drawn.GetProperty("tournamentId").GetString()!;

        var fetched = await (await _client.GetAsync($"/api/tournaments/{id}")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(id, fetched.GetProperty("tournamentId").GetString());
        Assert.Equal(16, fetched.GetProperty("size").GetInt32());
        Assert.Equal(15, MatchesOf(fetched).Length);
        Assert.Equal(Prompt, fetched.GetProperty("promptText").GetString());
    }

    [Fact]
    public async Task Get_UnknownTournament_Returns404()
    {
        var response = await _client.GetAsync("/api/tournaments/01NOSUCHTOURNAMENTIDXXXXXX");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Byes ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A field short of sixteen goes through the endpoint and storage with its byes already
    /// decided: the API reads back a 16-model bracket whose four byes are not counted as matches
    /// to play, and whose top four seeds are waiting in round two.
    /// </summary>
    [Fact]
    public async Task Draw_OfTwelve_IsStoredWithTheTopSeedsThroughOnByes()
    {
        var field = await RegisterFieldAsync($"TE Byes {Guid.NewGuid():N}", 12);

        var drawn = await DrawAsync(field);
        Assert.Equal(HttpStatusCode.Created, drawn.StatusCode);
        var id = (await drawn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tournamentId").GetString()!;

        var fetched = await (await _client.GetAsync($"/api/tournaments/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        var matches = MatchesOf(fetched);

        Assert.Equal(16, fetched.GetProperty("size").GetInt32());
        Assert.Equal(15, matches.Length);
        Assert.Equal(4, matches.Count(m => m.GetProperty("isBye").GetBoolean()));
        Assert.Equal(11, fetched.GetProperty("totalMatches").GetInt32());
        Assert.Equal(4, matches.Where(m => m.GetProperty("round").GetInt32() == 1)
            .Count(m => !string.IsNullOrEmpty(m.GetProperty("slotAModelId").GetString())));
    }

    /// <summary>
    /// A retired seed keeps its row (and so its ELO and duels) but leaves the tournament draw
    /// and the model catalog — GPT-4.1 Nano was one model under two names until 2026-11-07.
    /// </summary>
    [Fact]
    public async Task RetiredSeed_IsKeptOutOfTheDrawAndTheCatalog()
    {
        var retired = ModelSeeder.RetiredIds.First(id => id.Value.EndsWith('C'));
        using (var scope = _host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IModelRepository>()
                .SaveAsync(new Model(retired, "GPT-4.1 Nano", PoLocalCompare.Shared.Enums.ModelType.Remote, apiEndpointRef: "gpt-4.1-nano"));
        }

        var entrants = await (await _client.GetAsync("/api/tournaments/entrants")).Content.ReadFromJsonAsync<JsonElement[]>();
        var catalog = await (await _client.GetAsync("/api/models")).Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.DoesNotContain(entrants!, e => e.GetProperty("modelId").GetString() == retired.Value);
        Assert.DoesNotContain(catalog!, m => m.GetProperty("modelId").GetString() == retired.Value);
    }

    // ── Retired Swiss runs ─────────────────────────────────────────────────

    /// <summary>
    /// Swiss was retired on 2026-11-07 and its stored runs are read-only. One left Running when
    /// the format went is served with its table but reads as Abandoned, so it is never resumed
    /// and never blocks a new bracket through the one-at-a-time gate.
    /// </summary>
    [Fact]
    public async Task StoredSwissRun_IsServedReadOnly_AndDoesNotBlockANewBracket()
    {
        var field = await RegisterFieldAsync($"TE SwissRO {Guid.NewGuid():N}", 8);
        var swiss = new Tournament
        {
            TournamentId = TournamentId.New(),
            Format = TournamentFormat.Swiss,
            Size = 8,
            PromptText = Prompt,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = TournamentStatus.Running,
            Matches = Enumerable.Range(0, 4).Select(i => new TournamentMatch
            {
                Round = 0,
                Index = i,
                SlotAModelId = ModelId.From(field[i]), SlotAName = $"A{i}", SlotASeed = i + 1,
                SlotBModelId = ModelId.From(field[i + 4]), SlotBName = $"B{i}", SlotBSeed = i + 5,
                // Board 0 drawn, board 1 won by A, the rest unplayed.
                IsDraw = i == 0,
                WinnerModelId = i == 1 ? ModelId.From(field[1]) : null,
            }).ToList(),
        };
        using (var scope = _host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<TournamentRepository>().SaveAsync(swiss);

        var dto = await (await _client.GetAsync($"/api/tournaments/{swiss.TournamentId}")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Swiss", dto.GetProperty("format").GetString());
        Assert.Equal("Abandoned", dto.GetProperty("status").GetString());
        var standings = dto.GetProperty("standings").EnumerateArray().ToArray();
        Assert.Equal(8, standings.Length);
        Assert.Equal(2.0, standings.Sum(s => s.GetProperty("points").GetDouble()));

        Assert.Equal(HttpStatusCode.Created, (await DrawAsync(await RegisterFieldAsync("TE AfterSwiss", 2))).StatusCode);
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
