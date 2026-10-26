using Moq;
using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Api.Features.Leaderboard;
using PoLocalCompare.Api.Features.Models;

using PoLocalCompare.Shared.Enums;

using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Unit;

/// <summary>
/// Elo moves only through this handler, and it has two callers (the verdict endpoint and the
/// AI judge). It also carries the no-evidence rule — a judge that can't decide must leave the
/// duel <c>Pending</c> rather than guess. These tests pin the most behaviour-covering
/// branches, not every individual scenario.
/// </summary>
public class RecordVerdictTests
{
    private static Model MakeLocalModel(ModelId id) =>
        new(id, $"Model {id}", ModelType.Local, tdpWatts: 115.0, webLlmModelId: "llm-1");

    private static Duel MakePendingDuel(DuelId duelId, ModelId leftId, ModelId rightId) =>
        new(duelId, "Test prompt", "Test prompt (full)", leftId, rightId);

    private static (Mock<IDuelRepository> duelRepo, Mock<IModelRepository> modelRepo, Mock<IEloHistoryRepository> eloRepo, RecordVerdictHandler handler)
        MakeHandler(DuelId duelId, Model left, Model right, Duel duel)
    {
        var duelRepo = new Mock<IDuelRepository>();
        duelRepo.Setup(r => r.GetByIdAsync(duelId)).ReturnsAsync(duel);

        var modelRepo = new Mock<IModelRepository>();
        modelRepo.Setup(r => r.GetByIdAsync(left.ModelId)).ReturnsAsync(left);
        modelRepo.Setup(r => r.GetByIdAsync(right.ModelId)).ReturnsAsync(right);

        var eloRepo = new Mock<IEloHistoryRepository>();

        var handler = new RecordVerdictHandler(duelRepo.Object, modelRepo.Object, eloRepo.Object, kFactor: 32);
        return (duelRepo, modelRepo, eloRepo, handler);
    }

    [Fact]
    public async Task HandleAsync_LeftWins_UpdatesBothModelsElo()
    {
        var left = MakeLocalModel(ModelId.From("left-1"));
        var right = MakeLocalModel(ModelId.From("right-1"));
        var duel = MakePendingDuel(DuelId.From("duel-1"), left.ModelId, right.ModelId);
        var (_, _, _, handler) = MakeHandler(duel.DuelId, left, right, duel);

        var result = await handler.HandleAsync(new RecordVerdictCommand(duel.DuelId, DuelVerdict.Left));

        Assert.NotNull(result);
        Assert.Equal(left.ModelId, result!.WinnerModelId);
        Assert.Equal(right.ModelId, result.LoserModelId);
        Assert.True(result.EloShiftWinner > 0);
        Assert.True(result.EloShiftLoser < 0);
    }

    [Fact]
    public async Task HandleAsync_Tie_MovesNoEloAndNamesNoWinner()
    {
        // Equal evidence is not a reason to separate two models: ratings must not move.
        var left = MakeLocalModel(ModelId.From("left-t"));
        var right = MakeLocalModel(ModelId.From("right-t"));
        var duel = MakePendingDuel(DuelId.From("duel-t"), left.ModelId, right.ModelId);
        var (_, _, _, handler) = MakeHandler(duel.DuelId, left, right, duel);

        var result = await handler.HandleAsync(new RecordVerdictCommand(duel.DuelId, DuelVerdict.Tie));

        Assert.Equal(DuelVerdict.Tie, result!.Verdict);
        Assert.Null(result.WinnerModelId);
        Assert.Equal(1200, left.CurrentElo);
        Assert.Equal(1200, right.CurrentElo);
    }

    [Fact]
    public async Task HandleAsync_VerdictAlreadyRecorded_ThrowsInvalidOperation()
    {
        // First-write wins: a second verdict throws, the caller maps to 409.
        var left = MakeLocalModel(ModelId.From("left-3"));
        var right = MakeLocalModel(ModelId.From("right-3"));
        var duel = MakePendingDuel(DuelId.From("duel-3"), left.ModelId, right.ModelId);
        duel.Verdict = DuelVerdict.Left;
        var (_, _, _, handler) = MakeHandler(duel.DuelId, left, right, duel);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(new RecordVerdictCommand(duel.DuelId, DuelVerdict.Right)));
    }

    [Fact]
    public async Task HandleAsync_DuelNotFound_ReturnsNull()
    {
        var duelRepo = new Mock<IDuelRepository>();
        duelRepo.Setup(r => r.GetByIdAsync(It.IsAny<DuelId>())).ReturnsAsync((Duel?)null);
        var handler = new RecordVerdictHandler(duelRepo.Object, new Mock<IModelRepository>().Object, new Mock<IEloHistoryRepository>().Object);

        var result = await handler.HandleAsync(new RecordVerdictCommand(DuelId.From("missing"), DuelVerdict.Left));

        Assert.Null(result);
    }

    [Fact]
    public async Task HandleAsync_BothSidesFailed_ThrowsAndLeavesEloUntouched()
    {
        // No-evidence rule: a judge that cannot decide must leave the duel Pending rather than
        // guess. Elo must never move on no evidence.
        var left = MakeLocalModel(ModelId.From("left-9"));
        var right = MakeLocalModel(ModelId.From("right-9"));
        var duel = MakePendingDuel(DuelId.From("duel-9"), left.ModelId, right.ModelId);

        var duelRepo = new Mock<IDuelRepository>();
        duelRepo.Setup(r => r.GetByIdAsync(duel.DuelId)).ReturnsAsync(duel);

        var modelRepo = new Mock<IModelRepository>();
        modelRepo.Setup(r => r.GetByIdAsync(left.ModelId)).ReturnsAsync(left);
        modelRepo.Setup(r => r.GetByIdAsync(right.ModelId)).ReturnsAsync(right);

        var eloRepo = new Mock<IEloHistoryRepository>();

        var duelResultRepo = new Mock<IDuelResultRepository>();
        duelResultRepo.Setup(r => r.GetByDuelIdAsync(duel.DuelId)).ReturnsAsync(
        [
            new DuelResult(duel.DuelId, left.ModelId) { IsFailure = true },
            new DuelResult(duel.DuelId, right.ModelId) { IsFailure = true },
        ]);

        var handler = new RecordVerdictHandler(
            duelRepo.Object, modelRepo.Object, eloRepo.Object,
            kFactor: 32, duelResultRepository: duelResultRepo.Object);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync(new RecordVerdictCommand(duel.DuelId, DuelVerdict.Left)));

        Assert.Equal(1200, left.CurrentElo);
        Assert.Equal(1200, right.CurrentElo);
    }

    // ── Voiding a duel — terminal with no rating, even when result rows ARE complete ──────

    [Fact]
    public async Task HandleAsync_Voided_RecordsTerminalStateWithoutChangingElo()
    {
        // A void is the no-evidence terminal: the auto-judge or startup recovery sweeper writes
        // it through this handler when both models failed (or no result row exists). It must
        // never move ELO, must never count as a duel for the model aggregates, and must stamp
        // a null JudgeModel so the Arena's "Judge: gpt-5.4-mini" attribution only ever shows
        // for an actual LLM decision.
        var left = MakeLocalModel(ModelId.From("left-v"));
        var right = MakeLocalModel(ModelId.From("right-v"));
        var duel = MakePendingDuel(DuelId.From("duel-v"), left.ModelId, right.ModelId);
        var (duelRepo, modelRepo, eloRepo, _) = MakeHandler(duel.DuelId, left, right, duel);

        var lobbyProxy = new Mock<Microsoft.AspNetCore.SignalR.IClientProxy>();
        var clients = new Mock<Microsoft.AspNetCore.SignalR.IHubClients>();
        clients.Setup(c => c.Group(DuelHub.LobbyGroup)).Returns(lobbyProxy.Object);
        var hub = new Mock<Microsoft.AspNetCore.SignalR.IHubContext<DuelHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        var lobby = new LobbyNotifier(hub.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<LobbyNotifier>.Instance);
        var handler = new RecordVerdictHandler(duelRepo.Object, modelRepo.Object, eloRepo.Object, kFactor: 32, lobby: lobby);

        var result = await handler.HandleAsync(new RecordVerdictCommand(
            duel.DuelId, DuelVerdict.Voided, VerdictSource.Constraint, "Abandoned before every model reported.", JudgeModel: null));

        Assert.Equal(DuelVerdict.Voided, result!.Verdict);
        Assert.Null(result.WinnerModelId);
        Assert.Null(result.LoserModelId);
        Assert.Equal(0, result.EloShiftWinner);
        Assert.Equal(0, result.EloShiftLoser);
        Assert.Equal(VerdictSource.Constraint, result.Source);
        Assert.Equal(1200, left.CurrentElo);
        Assert.Equal(1200, right.CurrentElo);
        // No history rows written for a void.
        eloRepo.Verify(r => r.SaveAsync(It.IsAny<EloRecord>()), Times.Never);
        // Must notify the lobby ticker so the awaiting-verdict badge clears.
        lobbyProxy.Verify(p => p.SendCoreAsync(
            "LobbyEvent",
            It.Is<object?[]>(args => args.Length == 1
                && args[0] is PoLocalCompare.Shared.DTOs.LobbyEventDto
                && ((PoLocalCompare.Shared.DTOs.LobbyEventDto)args[0]!).Kind == LobbyEventKind.VerdictRecorded
                && ((PoLocalCompare.Shared.DTOs.LobbyEventDto)args[0]!).Verdict == DuelVerdict.Voided),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── A void is the no-evidence terminal, so it dispatches before the guard below — the
    //   guard exists to stop ELO moving on missing evidence, and a void moves nothing. ──────

    [Fact]
    public async Task HandleAsync_VoidedWithBothResultsFailing_StillSucceeds()
    {
        // The endpoint refuses Left/Right/Tie on a both-failed duel; a void must NOT — the
        // recovery sweeper arrives at this exact shape and needs a path through. The void
        // handler is the only one that is allowed to record a verdict with two failed
        // result rows on disk.
        var left = MakeLocalModel(ModelId.From("left-bf"));
        var right = MakeLocalModel(ModelId.From("right-bf"));
        var duel = MakePendingDuel(DuelId.From("duel-bf"), left.ModelId, right.ModelId);
        var (duelRepo, modelRepo, eloRepo, _) = MakeHandler(duel.DuelId, left, right, duel);

        var duelResultRepo = new Mock<IDuelResultRepository>();
        duelResultRepo.Setup(r => r.GetByDuelIdAsync(duel.DuelId)).ReturnsAsync(
        [
            new DuelResult(duel.DuelId, left.ModelId) { IsFailure = true, FailureReason = "OOM" },
            new DuelResult(duel.DuelId, right.ModelId) { IsFailure = true, FailureReason = "Watchdog" },
        ]);

        var handler = new RecordVerdictHandler(
            duelRepo.Object, modelRepo.Object, eloRepo.Object,
            kFactor: 32, duelResultRepository: duelResultRepo.Object);

        var result = await handler.HandleAsync(new RecordVerdictCommand(
            duel.DuelId, DuelVerdict.Voided, VerdictSource.Constraint, "Both models failed."));

        Assert.Equal(DuelVerdict.Voided, result!.Verdict);
        Assert.Equal(1200, left.CurrentElo);
        Assert.Equal(1200, right.CurrentElo);
    }

    // ── A verdict can never land on a still-running duel (one result row missing) ──────

    [Fact]
    public async Task HandleAsync_LeftWinsWithMissingOtherResult_Throws()
    {
        // The endpoint used to accept a vote with one result row still missing — and the
        // server recorded a winner on half a duel. The guard is now the second arm of the
        // no-evidence check; ELO must not move while a model is still generating.
        var left = MakeLocalModel(ModelId.From("left-m"));
        var right = MakeLocalModel(ModelId.From("right-m"));
        var duel = MakePendingDuel(DuelId.From("duel-m"), left.ModelId, right.ModelId);
        var (duelRepo, modelRepo, eloRepo, handler) = MakeHandler(duel.DuelId, left, right, duel);

        var duelResultRepo = new Mock<IDuelResultRepository>();
        duelResultRepo
            .Setup(r => r.GetByDuelIdAsync(duel.DuelId))
            .ReturnsAsync([new DuelResult(duel.DuelId, left.ModelId) { HtmlOutputRaw = "<p>left</p>" }]);

        var guarded = new RecordVerdictHandler(
            duelRepo.Object, modelRepo.Object, eloRepo.Object,
            kFactor: 32, duelResultRepository: duelResultRepo.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guarded.HandleAsync(new RecordVerdictCommand(duel.DuelId, DuelVerdict.Left)));
        Assert.Contains("not reported", ex.Message);
        Assert.Equal(1200, left.CurrentElo);
        Assert.Equal(1200, right.CurrentElo);
    }
}
