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
    private static readonly string[] Statuses = ["Healthy", "Degraded", "Unhealthy", "Off"];
    private static readonly string[] Groups = ["api", "data", "service"];

    private IntegrationHost _host = null!;
    private HttpClient Client => _host.Client;

    public Task InitializeAsync()
    {
        _host = new IntegrationHost(azurite.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [DockerTheory]
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

    [DockerFact]
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

    [DockerFact]
    public async Task DiagJson_FollowsTheStandardShape()
    {
        // The Po /diag contract: DiagReportDto with an "api" check first, then one check per
        // dependency grouped "data" or "service", statuses Healthy|Degraded|Unhealthy.
        using var anonymous = _host.CreateAnonymousClient();

        var body = await anonymous.GetFromJsonAsync<JsonElement>("/api/diag");

        Assert.Contains(body.GetProperty("status").GetString(), Statuses);
        Assert.True(body.GetProperty("checkedMs").GetDouble() >= 0);
        var checks = body.GetProperty("checks").EnumerateArray().ToList();
        Assert.Equal("api", checks[0].GetProperty("group").GetString());
        Assert.All(checks, c =>
        {
            Assert.Contains(c.GetProperty("group").GetString(), Groups);
            Assert.Contains(c.GetProperty("status").GetString(), Statuses);
            Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("label").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(c.GetProperty("purpose").GetString()));
        });
        var table = checks.Single(c => c.GetProperty("label").GetString() == "Table Storage");
        Assert.Equal("data", table.GetProperty("group").GetString());
        Assert.Equal("Healthy", table.GetProperty("status").GetString());
        var blob = checks.Single(c => c.GetProperty("label").GetString() == "Blob Storage");
        Assert.Equal("data", blob.GetProperty("group").GetString());
        Assert.Equal("Healthy", blob.GetProperty("status").GetString());
        var vault = checks.Single(c => c.GetProperty("label").GetString() == "Azure Key Vault");
        Assert.Equal("service", vault.GetProperty("group").GetString());
        // No vault configured in this host: switched off on purpose reads "Off" (neutral), not Healthy, and
        // does not drag the overall status down.
        Assert.Equal("Off", vault.GetProperty("status").GetString());
        Assert.NotEqual("Off", body.GetProperty("status").GetString());
        // The API row is measured, not a constant.
        Assert.True(checks[0].GetProperty("latencyMs").GetDouble() > 0);

        // Every external service the app calls is listed, configured here or not.
        string[] services = ["Azure AI Foundry", "Microsoft Entra ID", "Application Insights", "Hugging Face Hub", "Ollama"];
        foreach (var label in services)
            Assert.Equal("service", checks.Single(c => c.GetProperty("label").GetString() == label).GetProperty("group").GetString());
    }

    [DockerFact]
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
        var checks = body.GetProperty("checks").EnumerateArray().ToList();
        Assert.Contains(checks, c => c.GetProperty("label").GetString() == "Table Storage" && c.GetProperty("group").GetString() == "data");
        var foundry = checks.Single(c => c.GetProperty("label").GetString() == "Azure AI Foundry");
        Assert.Equal("service", foundry.GetProperty("group").GetString());
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
