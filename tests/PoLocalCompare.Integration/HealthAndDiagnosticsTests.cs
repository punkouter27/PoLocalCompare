using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// /health is what App Service probes to decide whether a deployment is live, so its status
/// code and payload shape are a deployment contract, not a convenience. Kept to the most
/// behaviour-covering cases per the audit's test ratio.
/// </summary>
[Collection("Integration")]
public sealed class HealthAndDiagnosticsTests(AzuriteFixture azurite) : IAsyncLifetime
{
    private IntegrationHost _host = null!;
    private HttpClient Client => _host.Client;

    public Task InitializeAsync()
    {
        _host = new IntegrationHost(azurite.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/diag")]
    [InlineData("/api/diag/smoke")]
    public async Task ProbeEndpoints_AreAnonymous(string path)
    {
        // Deny-by-default would otherwise make the platform probe fail on every request.
        // /diag/smoke has the same expectation — the diagnostics surface has to work
        // before anyone is signed in.
        using var anonymous = _host.CreateAnonymousClient();

        var response = await anonymous.GetAsync(path);

        Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_WithAzuriteUp_Returns200AndReportsKeyVaultAsNotConfigured()
    {
        // Combined: the platform probe must return 200 with Azurite up, AND the keyVault
        // probe must say NotConfigured (not Healthy — that would falsely claim we reached
        // a vault that was never configured) when the host has empty config.
        var response = await Client.GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var checks = body.GetProperty("checks");

        Assert.Equal("Healthy", checks.GetProperty("azureTableStorage").GetProperty("status").GetString());
        Assert.Equal("NotConfigured", checks.GetProperty("keyVault").GetProperty("status").GetString());
    }

    [Fact]
    public async Task DiagPage_RendersWithoutAnyConfiguredEndpoint()
    {
        using var anonymous = _host.CreateAnonymousClient();

        var response = await anonymous.GetAsync("/diag");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("Diagnostics", html);
        // Not even a masked form any more (2026-11-17): the page renders no configured URI.
        Assert.DoesNotContain("BlobEndpoint=", html);
        Assert.DoesNotContain("****", html);
    }

    [Fact]
    public async Task DiagJson_NeverExposesSecretsOrInternals()
    {
        // A host given secret-shaped config, with a Foundry endpoint that cannot resolve so
        // its probe fails and an exception (whose message names the host) is in play.
        const string foundryHost = "diag-secret-foundry.invalid";
        const string apiKey = "sk-diag-SECRET-KEY-0123456789";
        await using var host = new IntegrationHost(azurite.ConnectionString, settings: new Dictionary<string, string>
        {
            ["AzureAiFoundry:Endpoint"] = $"https://{foundryHost}/",
            ["AzureAiFoundry:ApiKey"] = apiKey,
        });
        using var anonymous = host.CreateAnonymousClient();

        var response = await anonymous.GetAsync("/api/diag");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(json).RootElement;
        Assert.Matches("^[0-9]{6}$", body.GetProperty("version").GetString());
        Assert.Equal("Development", body.GetProperty("environment").GetString());
        Assert.Contains(body.GetProperty("stores").EnumerateArray(), s => s.GetProperty("name").GetString() == "Table Storage");
        var foundry = body.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "Azure AI Foundry");
        Assert.Equal("Unhealthy", foundry.GetProperty("status").GetString());

        string[] forbidden =
        [
            foundryHost, apiKey, azurite.ConnectionString, "AccountKey", "AccountName", "devstoreaccount1",
            "://", "vault.azure.net", "login.microsoftonline", "68817373-07de-4686-9f67-6f8938cb3f4e",
            "@", "C:\\", "/home/", " at ", "Exception:",
        ];
        foreach (var needle in forbidden)
            Assert.DoesNotContain(needle, json, StringComparison.OrdinalIgnoreCase);
    }
}
