using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PoLocalCompare.Api.Common.Background;
using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Api.Features.Judging;
using PoLocalCompare.Api.Features.Leaderboard;
using PoLocalCompare.Api.Features.Models;
using PoLocalCompare.Shared.Enums;

using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Unit;

/// <summary>
/// The auto-judge moves ELO without a human, so these tests pin the three invariants that keep
/// that safe: a human decision always wins the race; a judge that cannot decide (or fails for a
/// non-rate-limit reason) leaves the duel Pending rather than guessing; and a judge that hits
/// a transient upstream rate-limit is re-queued once for the requested delay, so an unattended
/// demo does not silently produce a graveyard of "judge stood down" rounds.
/// </summary>
public class AutoJudgeTests
{
    private static Model MakeModel(ModelId id) =>
        new(id, $"Model {id}", ModelType.Local, tdpWatts: 115.0, webLlmModelId: "llm-1");

    private static DuelResult MakeResult(DuelId duelId, ModelId modelId, string html) =>
        new(duelId, modelId) { HtmlOutputRaw = html, IsFailure = false };

    private static DuelResult MakeFailure(DuelId duelId, ModelId modelId, string reason) =>
        new(duelId, modelId) { HtmlOutputRaw = string.Empty, IsFailure = true, FailureReason = reason };

    private sealed record Harness(
        AutoJudge Judge,
        Mock<IDuelJudge> Llm,
        Mock<IDuelRepository> DuelRepo,
        InMemoryBackgroundQueue Queue,
        Duel Duel);

    private sealed class InMemoryBackgroundQueue : IBackgroundTaskQueue
    {
        public ConcurrentBag<Func<CancellationToken, Task>> Work { get; } = new();
        public void QueueBackgroundWork(Func<CancellationToken, Task> workItem) => Work.Add(workItem);
        public Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by these tests.");
    }

    /// <summary>
    /// Wires a real <see cref="RecordVerdictHandler"/> behind the auto-judge so the ELO write
    /// path is genuinely exercised rather than mocked away. DelaySeconds is 0 so tests do not
    /// sit through the grace window.
    /// </summary>
    private static Harness BuildHarness(
        Duel duel,
        IEnumerable<DuelResult> results,
        JudgeDecision? llmDecision,
        bool enabled = true,
        int rateLimitRetryMax = 0)
    {
        var duelRepo = new Mock<IDuelRepository>();
        duelRepo.Setup(r => r.GetByIdAsync(duel.DuelId)).ReturnsAsync(duel);

        var modelRepo = new Mock<IModelRepository>();
        modelRepo.Setup(r => r.GetByIdAsync(duel.LeftModelId)).ReturnsAsync(MakeModel(duel.LeftModelId));
        modelRepo.Setup(r => r.GetByIdAsync(duel.RightModelId)).ReturnsAsync(MakeModel(duel.RightModelId));

        var resultRepo = new Mock<IDuelResultRepository>();
        resultRepo.Setup(r => r.GetByDuelIdAsync(duel.DuelId)).ReturnsAsync(results);

        var llm = new Mock<IDuelJudge>();
        llm.Setup(j => j.JudgeAsync(
                It.IsAny<string>(), It.IsAny<JudgedPage>(), It.IsAny<JudgedPage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(llmDecision);

        var recordVerdict = new RecordVerdictHandler(
            duelRepo.Object, modelRepo.Object, new Mock<IEloHistoryRepository>().Object, kFactor: 32);

        var clientProxy = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<DuelHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var options = Options.Create(new AutoJudgeOptions
        {
            Enabled = enabled,
            DelaySeconds = 0,
            Deployment = "judge-model",
            RateLimitRetryMax = rateLimitRetryMax,
        });

        var queue = new InMemoryBackgroundQueue();

        var autoJudge = new AutoJudge(
            duelRepo.Object,
            resultRepo.Object,
            recordVerdict,
            llm.Object,
            hub.Object,
            options,
            queue,
            NullLogger<AutoJudge>.Instance);

        return new Harness(autoJudge, llm, duelRepo, queue, duel);
    }

    private static Duel MakePendingDuel() =>
        new(DuelId.From("duel-aj"), "Build a page", "Build a page (full)", ModelId.From("left-aj"), ModelId.From("right-aj"));

    // ── The judge decides an unjudged duel ────────────────────────────────────

    [Fact]
    public async Task RunAsync_BothProducedOutput_RecordsJudgeVerdictAsAi()
    {
        var duel = MakePendingDuel();
        var harness = BuildHarness(
            duel,
            [MakeResult(duel.DuelId, ModelId.From("left-aj"), "<p>left</p>"), MakeResult(duel.DuelId, ModelId.From("right-aj"), "<p>right</p>")],
            new JudgeDecision(DuelVerdict.Right, "Right implemented every requested element."));

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        Assert.Equal(DuelVerdict.Right, duel.Verdict);
        Assert.Equal(VerdictSource.Ai, duel.VerdictSource);
        Assert.Equal(ModelId.From("right-aj"), duel.WinnerModelId);
        Assert.Equal("Right implemented every requested element.", duel.JudgeRationale);
        Assert.Equal("judge-model", duel.JudgeModel);
    }

    // ── A human decision always wins the race ─────────────────────────────────

    [Fact]
    public async Task RunAsync_HumanAlreadyJudged_LeavesVerdictAloneAndNeverCallsJudge()
    {
        var duel = MakePendingDuel();
        duel.Verdict = DuelVerdict.Left;
        duel.VerdictSource = VerdictSource.Human;
        duel.WinnerModelId = ModelId.From("left-aj");

        var harness = BuildHarness(
            duel,
            [MakeResult(duel.DuelId, ModelId.From("left-aj"), "<p>left</p>"), MakeResult(duel.DuelId, ModelId.From("right-aj"), "<p>right</p>")],
            new JudgeDecision(DuelVerdict.Right, "should never be used"));

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        Assert.Equal(DuelVerdict.Left, duel.Verdict);
        Assert.Equal(VerdictSource.Human, duel.VerdictSource);
        harness.Llm.Verify(j => j.JudgeAsync(
            It.IsAny<string>(), It.IsAny<JudgedPage>(), It.IsAny<JudgedPage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── No decision ⇒ no ELO movement ─────────────────────────────────────────

    [Fact]
    public async Task RunAsync_JudgeCannotDecide_LeavesDuelPending()
    {
        var duel = MakePendingDuel();
        var harness = BuildHarness(
            duel,
            [MakeResult(duel.DuelId, ModelId.From("left-aj"), "<p>left</p>"), MakeResult(duel.DuelId, ModelId.From("right-aj"), "<p>right</p>")],
            llmDecision: null);

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        Assert.Equal(DuelVerdict.Pending, duel.Verdict);
        Assert.Null(duel.WinnerModelId);
    }

    [Fact]
    public async Task RunAsync_BothModelsFailed_VoidsDuelAndNeverCallsJudge()
    {
        var duel = MakePendingDuel();
        var harness = BuildHarness(
            duel,
            [MakeFailure(duel.DuelId, ModelId.From("left-aj"), "OOM"), MakeFailure(duel.DuelId, ModelId.From("right-aj"), "Watchdog timeout")],
            new JudgeDecision(DuelVerdict.Left, "should never be used"));

        // Pass a positive delay override to prove a both-failed duel skips the grace window
        // instead of holding the duel in awaiting-verdict for DelaySeconds.
        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None, delaySecondsOverride: 60).WaitAsync(TimeSpan.FromSeconds(2));

        // Both-failed was the original "leave Pending and stand down" branch (PRD §9 item 9).
        // That left the duel haunting the lobby's awaiting-judgment counter forever — nothing
        // would ever move ELO on it, and the Arena still offered vote buttons the server would
        // reject. Voiding is the honest terminal: Verdict != Pending, no winner, no judge.
        Assert.Equal(DuelVerdict.Voided, duel.Verdict);
        Assert.Equal(VerdictSource.Constraint, duel.VerdictSource);
        Assert.Null(duel.WinnerModelId);
        Assert.Null(duel.JudgeModel);
        harness.Llm.Verify(j => j.JudgeAsync(
            It.IsAny<string>(), It.IsAny<JudgedPage>(), It.IsAny<JudgedPage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── A one-sided failure is now a walkover (PRD §9 item 20) ───────────────

    /// <summary>
    /// A one-sided failure is now a walkover (PRD §9 item 20). The survivor IS the
    /// model-quality evidence, the judge LLM is never invoked, and the rationale names both
    /// the survivor and the failure reason. Test both directions to catch a hard-coded
    /// "Left wins" regression.
    /// </summary>
    [Theory]
    [InlineData(false)]  // Right survives, Left failed
    public async Task RunAsync_OneModelFailed_RecordsWalkoverToTheSurvivor(bool leftSurvives)
    {
        var leftId = ModelId.From("left-aj");
        var rightId = ModelId.From("right-aj");
        var failureReason = leftSurvives ? "Watchdog timeout (900s)" : "HTTP 422";
        var results = leftSurvives
            ? new[] { MakeResult(DuelId.New(), leftId, "<p>left</p>"), MakeFailure(DuelId.New(), rightId, failureReason) }
            : new[] { MakeFailure(DuelId.New(), leftId, failureReason), MakeResult(DuelId.New(), rightId, "<p>right</p>") };

        var duel = MakePendingDuel();
        var harness = BuildHarness(duel, results,
            new JudgeDecision(leftSurvives ? DuelVerdict.Right : DuelVerdict.Left, "should never be used"));

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        var expectedVerdict = leftSurvives ? DuelVerdict.Left : DuelVerdict.Right;
        var expectedWinner = leftSurvives ? leftId : rightId;
        var expectedLoser = leftSurvives ? rightId : leftId;

        Assert.Equal(expectedVerdict, duel.Verdict);
        Assert.Equal(expectedWinner, duel.WinnerModelId);
        Assert.Equal(expectedLoser, duel.LoserModelId);
        // A walkover is arithmetic over the result rows — nothing read the outputs — so it must
        // NOT carry VerdictSource.Ai or the LLM deployment name. Otherwise every analytics
        // breakdown conflates "the AI judge decided" with "the opponent failed".
        Assert.Equal(VerdictSource.Constraint, duel.VerdictSource);
        Assert.Null(duel.JudgeModel);
        Assert.NotNull(duel.JudgeRationale);
        Assert.Contains("Walkover", duel.JudgeRationale);
        Assert.Contains(failureReason, duel.JudgeRationale);
        Assert.Null(duel.JudgeStoodDownReason);
        harness.Llm.Verify(j => j.JudgeAsync(
            It.IsAny<string>(), It.IsAny<JudgedPage>(), It.IsAny<JudgedPage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── The kill switch genuinely restores human-only verdicts ────────────────

    [Fact]
    public async Task RunAsync_Disabled_DoesNothing()
    {
        var duel = MakePendingDuel();
        var harness = BuildHarness(
            duel,
            [MakeResult(duel.DuelId, ModelId.From("left-aj"), "<p>left</p>"), MakeResult(duel.DuelId, ModelId.From("right-aj"), "<p>right</p>")],
            new JudgeDecision(DuelVerdict.Right, "should never be used"),
            enabled: false);

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        Assert.Equal(DuelVerdict.Pending, duel.Verdict);
        harness.DuelRepo.Verify(r => r.GetByIdAsync(It.IsAny<DuelId>()), Times.Never);
    }

    // ── A judge hit by a rate-limit retries on a detached timer, not the shared queue ─────────

    [Fact]
    public async Task RunAsync_RateLimitWithinRetryBudget_PersistsReasonAndDoesNotReEnqueue()
    {
        var duel = MakePendingDuel();
        var harness = BuildHarness(
            duel,
            [MakeResult(duel.DuelId, ModelId.From("left-aj"), "<p>left</p>"),
             MakeResult(duel.DuelId, ModelId.From("right-aj"), "<p>right</p>")],
            llmDecision: null,
            rateLimitRetryMax: 2);

        // Make IDuelJudge throw the rate-limit exception instead of returning null — that is
        // the contract the production judge now uses.
        harness.Llm
            .Setup(j => j.JudgeAsync(It.IsAny<string>(), It.IsAny<JudgedPage>(), It.IsAny<JudgedPage>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new JudgeRateLimitedException(TimeSpan.FromSeconds(15), "HTTP 429 from judge endpoint"));

        await harness.Judge.RunAsync(duel.DuelId, CancellationToken.None);

        // The duel stays Pending while we wait; nothing here moves ELO. The retry used to
        // occupy a BackgroundTaskService slot for the full retry delay, which would freeze
        // every duel queued behind it — BackgroundTaskService is single-consumer. The fix
        // detaches the retry on the thread pool, so the shared queue is empty (no `Work`
        // items) and the next caller can run immediately.
        Assert.Equal(DuelVerdict.Pending, duel.Verdict);
        Assert.Empty(harness.Queue.Work);
        Assert.NotNull(duel.JudgeStoodDownReason);
        Assert.Contains("Rate-limited", duel.JudgeStoodDownReason!);
    }
}
