using Xunit;

// Tests within the same [Collection] run sequentially; AzuriteFixture manages the shared container.
[assembly: CollectionBehavior(DisableTestParallelization = false)]
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Test classes dispose IAsyncDisposable hosts in IAsyncLifetime.DisposeAsync.")]
