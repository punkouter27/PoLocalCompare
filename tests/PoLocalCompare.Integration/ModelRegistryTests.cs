using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace PoLocalCompare.Integration;

/// <summary>
/// Registry round-trip over the real Table Storage layer. The registry is the one table a duel
/// cannot run without, so its round-trip fidelity is worth pinning against Azurite rather than a mock.
/// </summary>
[Collection("Integration")]
public sealed class ModelRegistryTests(AzuriteFixture azurite) : IAsyncLifetime
{
    private IntegrationHost _host = null!;

    public Task InitializeAsync()
    {
        _host = new IntegrationHost(azurite.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [DockerFact]
    public async Task LocalModel_RoundTripsWebLlmId()
    {
        var name = $"Local {Guid.NewGuid():N}";
        var id = await TestModels.LocalAsync(_host.Services, name);

        var models = await _host.Client.GetFromJsonAsync<JsonElement[]>("/api/models");
        var model = Assert.Single(models!, m => m.GetProperty("modelId").GetString() == id);

        Assert.Equal("Local", model.GetProperty("modelType").GetString());
        Assert.Equal($"test-webllm-{name.Replace(' ', '-').ToLowerInvariant()}", model.GetProperty("webLlmModelId").GetString());
    }

    [DockerFact]
    public async Task DownloadStatus_ReportsLocalWeightsWithoutAServerPath()
    {
        // The route is anonymous. Until 2026-11-17 a downloaded model's status carried the
        // absolute wwwroot path (`localPath`), exposing the host's directory layout.
        var env = _host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        var id = $"test-weights-{Guid.NewGuid():N}";
        var dir = Path.Combine(env.WebRootPath ?? "wwwroot", "models", id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "mlc-chat-config.json"), "{}");
        try
        {
            using var anonymous = _host.CreateAnonymousClient();
            var json = await anonymous.GetStringAsync($"/api/models/download-status/{id}");

            var body = JsonDocument.Parse(json).RootElement;
            Assert.True(body.GetProperty("downloaded").GetBoolean());
            Assert.False(body.TryGetProperty("localPath", out _));
            Assert.DoesNotContain(Path.GetFullPath(dir).Replace("\\", "\\\\"), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("wwwroot", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
