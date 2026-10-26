using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PoLocalCompare.Api.Common.Inference;
using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Api.Features.Judging;
using PoLocalCompare.Api.Features.Leaderboard;
using PoLocalCompare.Api.Features.Models;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;
using Testcontainers.Azurite;

using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.E2EAPI;

/// <summary>
/// Boots the real API (Blazor host) over an ephemeral Azurite container with AI inference mocked,
/// so E2E-API tests exercise the HTTP surface end-to-end as a black box.
/// </summary>
/// <remarks>
/// Not sealed: <see cref="DuelApiFixture"/> derives from it to run without the auto-judge, which
/// this host cannot do globally. DuelExecutionService awaits AutoJudge INLINE on the
/// single-consumer BackgroundTaskService queue when the grace window is 0 (it detaches the judge
/// otherwise, since 2026-09-26), and a duel suite races its own verdicts against the judge's.
/// A duel collection therefore wants the judge off, while a tournament collection cannot run
/// without it: TournamentRunner
/// passes autoJudgeDelaySeconds: 0 because the judge is the only thing that can decide a match,
/// so with no judge a bracket's first match stays Pending and holds that same queue forever.
/// </remarks>
public class ApiAppFixture : IAsyncLifetime
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;

    /// <summary>
    /// Whether the auto-judge runs in this host. Override to <c>false</c> for a collection that
    /// owns no tournaments — see the class remarks for why the two cannot share a setting.
    /// </summary>
    protected virtual bool EnableAutoJudge => true;

    public async Task InitializeAsync()
    {
        await _azurite.StartAsync();
        var connectionString = _azurite.GetConnectionString();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development"); // enables the fake-auth bypass
            builder.UseSetting("ConnectionStrings:AzureTableStorage", connectionString);
            builder.UseSetting("ConnectionStrings:AzureBlobStorage", connectionString);
            builder.UseSetting("Features:UseRealAi", "false");
            builder.UseSetting("KeyVault:Uri", "");
            builder.UseSetting("Testing:SkipSeeding", "true");
            builder.UseSetting("AiJudge:Enabled", EnableAutoJudge ? "true" : "false");
            // A suite starts more duels a minute than the per-user limit allows a person.
            builder.UseSetting("Limits:DuelStartsPerMinute", "0");
            // Safety net for the same class of problem: a duel left waiting on a browser result
            // blocks the single-consumer queue until its watchdog fires, and the app default is
            // 900 s. Clamped to the 30 s floor the execution service enforces. Tests should
            // release their duels explicitly (see DuelContractTests.ReleaseLocalDuelAsync); this
            // just stops a future mistake from hanging the run for fifteen minutes.
            builder.UseSetting("Duel:TimeLimitSeconds", "30");

            builder.ConfigureServices(services =>
            {
                // The auto-judge must be MOCKED, not switched off, and getting this wrong is
                // what made this assembly's duel tests fail whenever the tournament tests ran
                // first.
                //
                // TournamentRunner passes autoJudgeDelaySeconds: 0 precisely so an unattended
                // bracket never waits for a human — the judge is the only thing that can decide
                // a match. So with AiJudge:Enabled=false the first match stays Pending forever,
                // TournamentRunner sits on the single-consumer background queue waiting for it,
                // and every duel queued behind it never executes; three DuelContractTests then
                // timed out waiting for results that could not arrive. Mocking the judge keeps
                // brackets moving without a Foundry call, while disabling it stalls the queue.
                var judge = new Mock<IDuelJudge>();
                judge.Setup(j => j.JudgeAsync(
                        It.IsAny<string>(), It.IsAny<JudgedPage>(), It.IsAny<JudgedPage>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new JudgeDecision(DuelVerdict.Left, "E2E API judge mock.", IsWalkover: false));
                services.AddScoped(_ => judge.Object);

                services.AddLogging(l =>
                {
                    l.AddFilter("PoLocalCompare.Api.Common.Persistence", LogLevel.Warning);
                    l.AddFilter("Testcontainers", LogLevel.Warning);
                });

                // Keyed to match DuelExecutionService's GetRequiredKeyedService lookup, and
                // built per call from the real duel/model so the stored results belong to the
                // duel that produced them. As a plain AddScoped this mock was never resolved,
                // so every duel here ran the real Foundry proxy and failed.
                var mockProxy = new Mock<IRemoteInferenceProxy>();
                mockProxy
                    .Setup(p => p.RunInferenceAsync(
                        It.IsAny<Model>(), It.IsAny<DuelId>(), It.IsAny<string>(),
                        It.IsAny<Func<int, long, HtmlStreamStats?, Task>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((Model model, DuelId duelId, string _, Func<int, long, HtmlStreamStats?, Task> _, CancellationToken _) =>
                        new DuelResult(duelId, model.ModelId)
                        {
                            HtmlOutputRaw = "<html><body>E2E mock</body></html>",
                            TokenCount = 21,
                            TotalDurationMs = 250,
                            IsFailure = false,
                        });
                services.AddKeyedScoped<IRemoteInferenceProxy>("Remote", (_, _) => mockProxy.Object);
                services.AddKeyedScoped<IRemoteInferenceProxy>("LocalService", (_, _) => mockProxy.Object);
            });
        });
    }

    /// <summary>Services from the running host, for seeding the registry (see TestModels).</summary>
    public IServiceProvider Services => _factory.Services;

    /// <summary>Anonymous client — no auth header.</summary>
    public HttpClient CreateAnonymousClient()
        => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Client authenticated via the dev/test fake-auth scheme.</summary>
    public HttpClient CreateAuthenticatedClient(string user = "e2e-api", string roles = "User")
    {
        var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", user);
        client.DefaultRequestHeaders.Add("X-Fake-Roles", roles);
        return client;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _azurite.DisposeAsync();
    }
}

[CollectionDefinition("E2EAPI")]
public sealed class E2EApiCollection : ICollectionFixture<ApiAppFixture> { }
