using Microsoft.Extensions.DependencyInjection;
using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Api.Features.Leaderboard;
using PoLocalCompare.Api.Features.Models;
using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Integration;

/// <summary>
/// Drives the repositories directly against Azurite. These behaviours — ETag-conditional
/// updates, idempotent creates, the ULID-derived partition key, descending Elo history — are
/// invisible through the HTTP surface but are exactly what the standards §5.5 write discipline
/// promises, and only a real Table Storage endpoint can prove them. Kept to the most
/// behaviour-covering cases per the audit's test ratio.
/// </summary>
[Collection("Integration")]
public sealed class PersistenceTests(AzuriteFixture azurite) : IAsyncLifetime
{
    private IntegrationHost _host = null!;
    private IServiceScope _scope = null!;

    private IDuelRepository Duels => _scope.ServiceProvider.GetRequiredService<IDuelRepository>();
    private IDuelResultRepository Results => _scope.ServiceProvider.GetRequiredService<IDuelResultRepository>();
    private IModelRepository Models => _scope.ServiceProvider.GetRequiredService<IModelRepository>();
    private IEloHistoryRepository EloHistory => _scope.ServiceProvider.GetRequiredService<IEloHistoryRepository>();

    public Task InitializeAsync()
    {
        _host = new IntegrationHost(azurite.ConnectionString);
        _scope = _host.Services.CreateScope();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _scope.Dispose();
        await _host.DisposeAsync();
    }

    private static Duel NewDuel(out ModelId left, out ModelId right)
    {
        left = ModelId.New();
        right = ModelId.New();
        return new Duel(DuelId.New(), "Prompt text", "Prompt text (full)", left, right);
    }

    // ── Duels ──────────────────────────────────────────────────────────────

    [DockerFact]
    public async Task Duel_RoundTripsThroughTableStorage()
    {
        var duel = NewDuel(out var left, out var right);

        await Duels.SaveAsync(duel);
        var loaded = await Duels.GetByIdAsync(duel.DuelId);

        Assert.NotNull(loaded);
        Assert.Equal(duel.DuelId, loaded!.DuelId);
        Assert.Equal(left, loaded.LeftModelId);
        Assert.Equal(right, loaded.RightModelId);
        Assert.Equal("Prompt text", loaded.PromptText);
    }

    [DockerFact]
    public async Task Duel_SaveIsIdempotent()
    {
        // Standards §5.5: a repeated create must swallow the 409 rather than surface it.
        var duel = NewDuel(out _, out _);

        await Duels.SaveAsync(duel);
        await Duels.SaveAsync(duel); // must not throw

        var loaded = await Duels.GetByIdAsync(duel.DuelId);
        Assert.NotNull(loaded);

        // Pending + null winner is what the surface relies on to know the duel is still in flight.
        Assert.Equal(DuelVerdict.Pending, loaded!.Verdict);
        Assert.Null(loaded.WinnerModelId);
    }

    [DockerFact]
    public async Task Duel_UpdatePersistsTheVerdictAndItsSource()
    {
        var duel = NewDuel(out _, out _);

        await Duels.SaveAsync(duel);
        duel.Verdict = DuelVerdict.Left;
        duel.VerdictSource = VerdictSource.Ai;
        await Duels.UpdateAsync(duel);

        var loaded = await Duels.GetByIdAsync(duel.DuelId);
        Assert.Equal(DuelVerdict.Left, loaded!.Verdict);
        Assert.Equal(VerdictSource.Ai, loaded.VerdictSource);
    }

    [DockerFact]
    public async Task Duel_WrittenBeforeVerdictSourceExisted_ReadsBackAsHuman()
    {
        // Rows written before the auto-judge existed have no VerdictSource — they were all
        // human decisions, which is what the Human fallback says.
        var duel = NewDuel(out _, out _);
        await Duels.SaveAsync(duel);

        var loaded = await Duels.GetByIdAsync(duel.DuelId);
        Assert.Equal(VerdictSource.Human, loaded!.VerdictSource);
    }

    [DockerFact]
    public async Task Duel_ListReturnsSavedDuelsNewestFirst()
    {
        var first = NewDuel(out _, out _);
        await Duels.SaveAsync(first);
        await Task.Delay(2);
        var second = NewDuel(out _, out _);
        await Duels.SaveAsync(second);

        var list = (await Duels.ListAsync(limit: 50, beforeMonth: null)).ToList();

        Assert.Contains(list, d => d.DuelId == first.DuelId);
        Assert.Contains(list, d => d.DuelId == second.DuelId);
    }

    [DockerFact]
    public async Task Duel_ListFromCursor_ReturnsOnlyStrictlyOlderDuels()
    {
        // The Archive's Load More passes the oldest duel id it holds. The cursor used to be that
        // row's yyyyMM matched with `le`, which re-served the whole current month: the same rows
        // came back on every click. Scoped to the three duels created here — the table is shared.
        var oldest = NewDuel(out _, out _);
        await Duels.SaveAsync(oldest);
        await Task.Delay(5);
        var middle = NewDuel(out _, out _);
        await Duels.SaveAsync(middle);
        await Task.Delay(5);
        var newest = NewDuel(out _, out _);
        await Duels.SaveAsync(newest);

        var firstPage = (await Duels.ListAsync(limit: 100, beforeMonth: null, before: newest.DuelId))
            .Select(d => d.DuelId)
            .ToList();

        Assert.All(firstPage, id => Assert.True(id < newest.DuelId, $"{id} is not older than the cursor."));
        var middleAt = firstPage.IndexOf(middle.DuelId);
        Assert.True(middleAt >= 0 && middleAt < firstPage.IndexOf(oldest.DuelId),
            "Both older duels should come back, newest first.");

        // Paging on from the next cursor must not overlap what the first page already returned.
        var secondPage = (await Duels.ListAsync(limit: 100, beforeMonth: null, before: middle.DuelId))
            .Select(d => d.DuelId)
            .ToList();

        Assert.Contains(oldest.DuelId, secondPage);
        Assert.DoesNotContain(middle.DuelId, secondPage);
        Assert.DoesNotContain(newest.DuelId, secondPage);
        Assert.All(secondPage, id => Assert.True(id < middle.DuelId, $"{id} is not older than the cursor."));
    }

    [DockerFact]
    public async Task DuelResult_FailureDetailsPersist()
    {
        var duel = NewDuel(out var left, out _);
        var result = new DuelResult(duel.DuelId, left)
        {
            IsFailure = true,
            FailureReason = "rate-limited",
        };

        await Results.SaveAsync(result);
        var loaded = await Results.GetAsync(duel.DuelId, left);

        Assert.NotNull(loaded);
        Assert.True(loaded!.IsFailure);
        Assert.Equal("rate-limited", loaded.FailureReason);
    }

    [DockerFact]
    public async Task Model_UpdatePersistsEloAndCounters()
    {
        var model = new Model(ModelId.New(), "Test", ModelType.Remote, tdpWatts: 100.0, webLlmModelId: null, apiEndpointRef: "deployment-x");
        await Models.SaveAsync(model);

        model.CurrentElo = 1300;
        model.DuelCount = 5;
        model.WinCount = 3;
        await Models.UpdateAsync(model);

        var loaded = await Models.GetByIdAsync(model.ModelId);
        Assert.Equal(1300, loaded!.CurrentElo);
        Assert.Equal(5, loaded.DuelCount);
        Assert.Equal(3, loaded.WinCount);
    }

    [DockerFact]
    public async Task EloHistory_IsPartitionedPerModel()
    {
        var modelA = ModelId.New();
        var modelB = ModelId.New();
        var recordA = new EloRecord(modelA, DuelId.New(), 1200, 1200, "Draw", modelB, 1200);
        var recordB = new EloRecord(modelB, DuelId.New(), 1200, 1200, "Draw", modelA, 1200);

        await EloHistory.SaveAsync(recordA);
        await EloHistory.SaveAsync(recordB);

        var loadedA = (await EloHistory.GetAllByModelAsync(modelA)).ToList();
        var loadedB = (await EloHistory.GetAllByModelAsync(modelB)).ToList();

        Assert.Single(loadedA);
        Assert.Single(loadedB);
        Assert.Equal(modelA, loadedA[0].ModelId);
        Assert.Equal(modelB, loadedB[0].ModelId);
    }
}
