using PoLocalCompare.TestSupport;
using Testcontainers.Azurite;

namespace PoLocalCompare.Integration;

/// <summary>
/// Shared Azurite container for all integration tests in the "Integration" collection.
/// Starting one container instead of one-per-class cuts container spin-up overhead in half.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
        .Build();

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Without Docker every test here is skipped (DockerFact); starting nothing keeps it quiet.
        if (!DockerAvailability.IsAvailable)
            return;

        await _azurite.StartAsync();
        ConnectionString = _azurite.GetConnectionString();
    }

    public async Task DisposeAsync() => await _azurite.DisposeAsync();
}

[CollectionDefinition("Integration")]
public sealed class IntegrationCollection : ICollectionFixture<AzuriteFixture> { }
