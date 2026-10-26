using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// The model profile assembles five separate reads — catalog, leaderboard, ELO history, results
/// and the winning-output gallery — into one row, so it only holds together against real storage.
/// It is also where the kill list now lives, having moved off the leaderboard's expanding drawer.
/// </summary>
[Collection("Integration")]
public sealed class ModelProfileTests(AzuriteFixture azurite) : IAsyncLifetime
{
    private IntegrationHost _host = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _host = new IntegrationHost(azurite.ConnectionString);
        _client = _host.Client;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private Task<string> RegisterRemoteModelAsync(string name) => TestModels.RemoteAsync(_host.Services, name);

    // Waits for both result rows before recording the verdict — see DuelTestFlow.
    private Task RunDuelAsync(string leftId, string rightId, string verdictSide) =>
        DuelTestFlow.RunDuelAsync(_client, leftId, rightId, verdictSide);

    private async Task<JsonElement> GetProfileAsync(string modelId) =>
        (await (await _client.GetAsync($"/api/leaderboard/{modelId}/profile"))
            .Content.ReadFromJsonAsync<JsonElement>())!;

    [Fact]
    public async Task Profile_Anonymous_Returns401()
    {
        using var client = _host.CreateAnonymousClient();

        var response = await client.GetAsync("/api/leaderboard/aaa/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Unlike the kill list — which degrades to "no history" for a retired id — the profile is
    /// about a model, so an id naming no model is genuinely not found rather than an empty page.
    /// </summary>
    [Fact]
    public async Task Profile_UnknownModel_Returns404()
    {
        var response = await _client.GetAsync("/api/leaderboard/01NOSUCHMODELIDXXXXXXXXXXX/profile");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Profile_ReportsTheRecordFromRealDuels()
    {
        var a = await RegisterRemoteModelAsync("MP Record A");
        var b = await RegisterRemoteModelAsync("MP Record B");

        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Right");

        var profile = await GetProfileAsync(a);

        Assert.Equal("MP Record A", profile.GetProperty("displayName").GetString());
        Assert.Equal(3, profile.GetProperty("duelCount").GetInt32());
        Assert.Equal(2, profile.GetProperty("winCount").GetInt32());
        Assert.Equal(1, profile.GetProperty("lossCount").GetInt32());
    }

    /// <summary>
    /// The rank is the number the board's row shows: the model's position among models that
    /// have played, in the board's order. The loser is the case that matters — at 1185 it sits
    /// under every unplayed 1200, and counting those put it at #25 beside a board row saying #2.
    /// </summary>
    [Fact]
    public async Task Profile_RankAgreesWithTheLeaderboard()
    {
        var a = await RegisterRemoteModelAsync("MP Rank A");
        var b = await RegisterRemoteModelAsync("MP Rank B");

        await RunDuelAsync(a, b, "Left");

        var board = (await (await _client.GetAsync("/api/leaderboard"))
            .Content.ReadFromJsonAsync<JsonElement[]>())!;
        var played = board.Where(r => r.GetProperty("duelCount").GetInt32() > 0)
            .Select(r => r.GetProperty("modelId").GetString())
            .ToList();

        foreach (var id in new[] { a, b })
        {
            var profile = await GetProfileAsync(id);
            Assert.Equal(played.IndexOf(id) + 1, profile.GetProperty("rank").GetInt32());
        }
    }

    [Fact]
    public async Task Profile_EloHistoryChronologicallyListsEachWinAgainstItsOpponent()
    {
        var a = await RegisterRemoteModelAsync("MP Elo A");
        var b = await RegisterRemoteModelAsync("MP Elo B");

        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Left");

        var history = (await GetProfileAsync(a)).GetProperty("eloHistory").EnumerateArray().ToArray();

        Assert.Equal(3, history.Length);

        // Chronological — the chart reads left to right and the user has to make sense of
        // it that way. Three wins in a row can only go up; the renderer relies on this to
        // draw the y-axis correctly.
        var timestamps = history.Select(h => h.GetProperty("at").GetDateTimeOffset()).ToArray();
        Assert.Equal(timestamps.OrderBy(t => t), timestamps);
        var ratings = history.Select(h => h.GetProperty("elo").GetDouble()).ToArray();
        Assert.True(ratings[^1] > ratings[0], "Rating should rise across three wins.");

        // Each point names its opponent and outcome — a squiggle with no causes on it tells
        // the user nothing about why the rating moved.
        var last = history[^1];
        Assert.Equal("Win", last.GetProperty("outcome").GetString());
        Assert.Equal("MP Elo B", last.GetProperty("opponentName").GetString());
        Assert.Equal(b, last.GetProperty("opponentModelId").GetString());
    }

    [Fact]
    public async Task Profile_AModelWithNoDuels_IsUnrankedRatherThanMissing()
    {
        var lonely = await RegisterRemoteModelAsync("MP Lonely");

        var profile = await GetProfileAsync(lonely);

        Assert.Equal("MP Lonely", profile.GetProperty("displayName").GetString());
        Assert.Equal(0, profile.GetProperty("duelCount").GetInt32());
        Assert.Empty(profile.GetProperty("eloHistory").EnumerateArray());
        Assert.Empty(profile.GetProperty("killList").EnumerateArray());
        Assert.Empty(profile.GetProperty("winningOutputs").EnumerateArray());
    }

}
