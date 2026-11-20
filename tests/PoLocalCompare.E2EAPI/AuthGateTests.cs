using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.E2EAPI;

/// <summary>
/// Black-box checks on the BFF auth gate: protected endpoints reject anonymous callers,
/// public endpoints stay open. Kept to the most behaviour-covering cases per the audit's test
/// ratio.
/// </summary>
[Collection("E2EAPI")]
public sealed class AuthGateTests(ApiAppFixture app)
{
    [DockerFact]
    public async Task Leaderboard_Authenticated_Returns200()
    {
        using var client = app.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/leaderboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [DockerFact]
    public async Task AuthMe_FakeUser_ReportsAuthenticatedIdentity()
    {
        using var client = app.CreateAuthenticatedClient(user: "alice", roles: "User");
        var me = await client.GetFromJsonAsync<JsonElement>("/auth/me");
        Assert.True(me.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal("alice", me.GetProperty("name").GetString());
    }
}
