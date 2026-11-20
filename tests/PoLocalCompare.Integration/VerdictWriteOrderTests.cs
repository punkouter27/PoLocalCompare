using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// Guards the write order in <c>RecordVerdictHandler</c>.
/// </summary>
/// <remarks>
/// The handler used to update both model rows before writing the duel. An optimistic-concurrency
/// 412 on either model write then left one of them already incremented, and the retry in
/// <c>HandleWithRetryAsync</c> re-ran the whole method and incremented it again — silently
/// doubling <c>DuelCount</c> and <c>WinCount</c> on persisted ratings.
///
/// What made it hard to see is the third assertion below. <c>EloHistoryRepository.SaveAsync</c>
/// swallows a 409 as an idempotent append, so history stayed correct while the aggregates
/// doubled: the observed state was duelCount=6, winCount=4, eloHistoryRows=3 for three duels.
/// Asserting the counters against the history they are derived from is what pins the bug — any
/// future reordering that reintroduces it shows up as counters disagreeing with history.
///
/// The trigger is a second host against the same storage, which is exactly what every test class
/// in this collection does in <c>InitializeAsync</c>, so this is not a contrived race.
/// </remarks>
[Collection("Integration")]
public sealed class VerdictWriteOrderTests(AzuriteFixture azurite) : IAsyncLifetime
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

    private Task<string> RegisterAsync(string name) => TestModels.RemoteAsync(_host.Services, name);

    // Waits for both result rows before recording the verdict — see DuelTestFlow.
    private Task RunDuelAsync(string a, string b, string side) =>
        DuelTestFlow.RunDuelAsync(_client, a, b, side);

    [DockerFact]
    public async Task AggregatesMatchHistory_WhenAnotherHostHasAlreadyWritten()
    {
        // An earlier test class's worth of traffic against the same storage.
        var x = await RegisterAsync("WO Prior X");
        var y = await RegisterAsync("WO Prior Y");
        await RunDuelAsync(x, y, "Left");
        await RunDuelAsync(x, y, "Left");

        // A fresh host, exactly as the next test class's InitializeAsync would create.
        await _host.DisposeAsync();
        _host = new IntegrationHost(azurite.ConnectionString);
        _client = _host.Client;

        var a = await RegisterAsync("WO Second A");
        var b = await RegisterAsync("WO Second B");
        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Left");
        await RunDuelAsync(a, b, "Right");

        var profile = await _client.GetFromJsonAsync<JsonElement>($"/api/leaderboard/{a}/profile");

        var duelCount = profile.GetProperty("duelCount").GetInt32();
        var winCount = profile.GetProperty("winCount").GetInt32();
        var historyRows = profile.GetProperty("eloHistory").GetArrayLength();

        Assert.Equal(3, duelCount);
        Assert.Equal(2, winCount);

        // The invariant that actually catches a regression: one history row per counted duel.
        Assert.Equal(historyRows, duelCount);
    }
}
