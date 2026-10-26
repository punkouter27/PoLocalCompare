# AGENT.MD — PoLocalCompare AI Coding Agent Context

> **Read this first.** This file is the primary context document for any AI coding agent working in this repository. Start here before touching any code.
>
> Then check for a **`DOCS/`** folder in the repository root — if one exists, read it for the project overview before making changes. (There is none today: `docs/PRD_Master.md` was deleted from the repository on 2026-09-02 (commit `54036ec`). If no such folder is present, say so rather than inferring the overview from code. [CLAUDE.md](CLAUDE.md) holds the cross-file rules and [HISTORY.md](HISTORY.md) the reasoning behind them.)

---

## 1. What Is This Project?

**PoLocalCompare** is a real-time LLM benchmarking platform that pits **local browser-based LLMs** (WebLLM / WebGPU) against **remote cloud models** (Azure AI Foundry) and **Ollama local models** in timed HTML-generation duels. A human judge picks the winner. An Elo (K=32) rating system tracks relative model performance across all duels.

- **Live URL:** `https://app-polocalcompare-<suffix>.azurewebsites.net` in the punkouter27@gmail.com
  subscription since 2026-10-25 (§9). The `<suffix>` is derived per subscription by
  `infra/main.bicep`; the repo variable `AZURE_WEBAPP_NAME` is the source of truth. The previous
  host, `app-polocalcompare-win2` in the punkouter26@outlook.com subscription, keeps running until
  it is deleted by hand. `polocalcompare.azurewebsites.net` has **no DNS record at all** and never
  was the real host, so do not probe it — a smoke test against it looks exactly like an outage.
- **Solution file:** `PoLocalCompare.slnx`
- **Master prefix rule:** Every namespace, Azure resource, and Aspire resource uses `PoLocalCompare` as its prefix.

### The Core Loop
1. User selects two models (Local WebLLM / Remote Azure Foundry / Ollama)
2. User enters a prompt — models race to generate HTML
3. Both outputs stream live via SignalR
4. `AutoJudge` is the default: after `AiJudge:DelaySeconds` it asks a Foundry model which output
   implements the prompt better. The Arena offers a quick human override while that ten-second
   window is open; the controls disappear when the judge starts, and a pick inside the window wins
   the race (PRD §9 item 9).
5. Elo ratings update, stored in Azure Table Storage. Every verdict carries a `VerdictSource`
   (`Human`/`Ai`) so the two rankings stay separable

---

## 2. Tech Stack

| Layer | Technology |
|---|---|
| **Runtime** | .NET 10 (pinned via `global.json`) |
| **Language** | C# `latest` (`Directory.Build.props`) — standards mandate C# 15; SDK 10 tops out at 14, so `latest` rolls forward automatically |
| **Client** | Blazor WASM hosted in `PoLocalCompare.Api` |
| **UI library** | none — plain Blazor components over design tokens (`.po-btn` / `.po-table` in `app.css`) |
| **In-browser AI** | WebLLM (WebGPU) via JS interop |
| **Remote AI** | Azure AI Foundry (Azure OpenAI) |
| **Local AI** | Ollama HTTP (`http://localhost:11434`) |
| **Real-time** | SignalR (`DuelHub`) |
| **Storage (local)** | Azurite Docker container (10000 blob, 10002 table) |
| **Storage (Azure)** | Azure Table Storage |
| **Secrets** | Azure Key Vault (`kv-poshared-<suffix>` in `PoShared`, **RBAC mode**) + App Service settings fallback |
| **Identity** | System-assigned Managed Identity (per §5.4); Bicep grants Storage Table/Blob RBAC + Key Vault Secrets User |
| **Auth** | Microsoft OAuth (Prod) + GUEST mode (Dev/Test) |
| **Logging** | Serilog → Console (all envs) + rolling File (Development only) |
| **Telemetry** | Serilog only; one App Insights sink for the shared sign-in record (OTel removed 2026-08-23) |
| **Versioning** | MinVer (Git tag driven, prefix `v`) |
| **Styling** | Scoped `.razor.css` + design tokens; system-aware light/dark with an explicit override |

---

## 3. Project Structure (Vertical Slice Architecture)

> **Architecture note:** This solution uses **Vertical Slice Architecture (VSA)** per the global
> `Po*` mandate (§2.1). The former Clean/Onion layout (Domain/Application/Infrastructure projects)
> was collapsed into `PoLocalCompare.Api` on 2026-07-06 (recorded in the since-removed PRD's
> decision log, §9).
> New server code goes in `src/PoLocalCompare.Api/Features/<Feature>/` (flat: endpoint + handlers +
> entities + repository together); only genuinely cross-slice code goes in `Common/`.


```
PoLocalCompare.slnx
├── src/
│   ├── PoLocalCompare.Api/             ← ASP.NET Minimal API, SignalR, hosted Blazor WASM (VSA)
│   │   ├── Features/
│   │   │   ├── Duels/                  ← endpoints, commence/get/list/record-verdict handlers,
│   │   │   │                             Duel + DuelResult entities & repositories,
│   │   │   │                             DuelExecutionService, DuelHub
│   │   │   ├── Leaderboard/            ← endpoints, handlers, EloRecord, EloCalculator, EloHistoryRepository
│   │   │   ├── Models/                 ← endpoints, handlers, Model entity, ModelRepository, ModelSeeder
│   │   │   ├── Archive/                ← lab-report export handler + HTML renderer
│   │   │   ├── Ollama/                 ← status/pull endpoints
│   │   │   ├── Tournaments/            ← Tournament aggregate, TournamentRunner (server-side
│   │   │   │                             bracket orchestration), repository, endpoints
│   │   │   └── Diagnostics/            ← /health, /api/diag/*, E2E helpers
│   │   ├── Common/
│   │   │   ├── Inference/              ← IRemoteInferenceProxy + Foundry/Ollama typed-HttpClient proxies
│   │   │   ├── Background/             ← background task queue + hosted service
│   │   │   ├── KeyVault/               ← prefix-scoped Key Vault configuration
│   │   │   └── Persistence/            ← Azurite table bootstrap
│   │   │                                 (Domain/ was dissolved in the 2026-08-13 prune — all four
│   │   │                                  calculators had exactly one consuming slice each;
│   │   │                                  Telemetry/ went with OpenTelemetry on 2026-08-23)
│   │   ├── Auth/                       ← BFF cookie session + Microsoft OIDC + dev fake scheme
│   │   ├── Pages/                      ← /diag Razor page
│   │   ├── GlobalUsings.cs             ← all slice namespaces project-wide
│   │   ├── Program.cs                  ← Composition root
│   │   ├── ServiceCollectionExtensions.cs
│   │   └── InfrastructureServiceExtensions.cs
│   ├── PoLocalCompare.Shared/          ← DTOs, Enums, strongly-typed ids, and the pure logic
│   │   │                                 both WASM and the API (or its tests) need
│   │   ├── DTOs/                       ← DuelDto, ModelDto, LeaderboardEntryDto, VerdictRequestDto, etc.
│   │   ├── Enums/
│   │   ├── Ids/                        ← DuelId / ModelId (readonly record struct + JSON converters)
│   │   ├── Analysis/                   ← OutputAnalysis, LineDiff, HtmlPreview — browser-side
│   │   │                                 comparison logic; lives here so PoLocalCompare.Unit
│   │   │                                 can test it without referencing the WASM project
│   │   ├── Prompts/                    ← PromptLibrary (curated starter prompts)
│   │   ├── Tournaments/                ← BracketPlanner (pure, seeded bracket layout; sizes 2/8)
│   │   └── Presentation/               ← view-models, enums and helpers the .razor files lean
│   │                                     on (ArenaPhase, ModelTypeGroup, FailureReasonText,
│   │                                     HealthBadgeText, ArenaFormatting, SideMetrics,
│   │                                     RenderCoalescer). Moved out of Client/ so the Unit
│   │                                     tier can reach them at all.
│   └── PoLocalCompare.Client/          ← Blazor WASM (hosted by Api)
│       ├── Pages/                      ← Home, Arena, Leaderboard, Archive, Catalog, Tournament,
│       │                                 ModelProfile, Login, NotFound
│       │                                 (model health moved to the /diag Razor Page 2026-08-23)
│       │                                 (Arena both streams a running duel and judges it)
│       ├── Components/                 ← incl. PromptPicker, SandboxedViewport, ModelCard,
│       │                                 ArenaViewportPanel, ArenaVoteButtons, ArenaStatusBand,
│       │                                 ArenaFailureCard, TelemetryHud, ValueChart
│       ├── Services/                   ← DuelApiClient, BffAuthenticationStateProvider,
│       │                                 WebLlmService, SignalRDuelClient, LobbyTickerService,
│       │                                 LocalInferenceDriver, AudioService, FxService,
│       │                                 WebGpuCapability, CorrelationHandler, ThemeService
│       └── Layout/
├── tests/
│   ├── PoLocalCompare.Unit/            ← Pure logic, no I/O (xUnit + Moq; no Docker)
│   ├── PoLocalCompare.Integration/     ← Testcontainers Azurite behind WebApplicationFactory
│   ├── PoLocalCompare.E2EAPI/          ← Black-box HTTP journeys over Testcontainers + fake auth
│   └── PoLocalCompare.E2EUI/           ← Playwright, headed Chrome, mobile + desktop viewports
├── SCRIPTS/                            ← setup.ps1, download-models.py, plan-webllm-artifacts.py
├── infra/                              ← Bicep (main.bicep / keyvault-access.bicep)
├── Directory.Build.props               ← Global: TreatWarningsAsErrors, Nullable, LangVersion=latest
├── Directory.Packages.props            ← Central Package Management
├── global.json                         ← .NET 10 SDK pin
└── docker-compose.yml                  ← Azurite container
```

---

## 4. Key Architecture Decisions

### 4.1 Vertical Slice Architecture
- Each `Features/<Feature>/` folder owns its endpoint, handlers, entities, and repository (flat).
- `Common/` holds only genuinely cross-slice code; `GlobalUsings.cs` exposes every slice namespace.
- Server authorization is deny-by-default: `FallbackPolicy = RequireAuthenticatedUser` (§4.5);
  public endpoints opt out explicitly with `.AllowAnonymous()`.
- Table Storage writes are idempotent + ETag-safe (§5.5): creates swallow 409, updates are
  If-Match conditional (entities carry `ETag`), and duel writers re-read + reapply on 412.
- Telemetry: Serilog. The OTel tracing/metrics pipeline and its exporters were removed on
  2026-08-23; the one exporter left is a `Serilog.Sinks.ApplicationInsights` sink, added only when
  a connection string is configured, so the cross-app `UserSignedIn` record reaches App Insights.
- HttpClients (§5.4/§5.6): Foundry/Ollama proxies are typed clients with retry-only resilience
  (no per-attempt timeout — it would abort SSE streams); named "Foundry"/"Ollama"/"OllamaStatus"
  clients cover the remaining callers. Startup creates every table (`StorageTables`) and fail-fasts
  on unreachable Table Storage in every environment.
- The client stamps `X-Session-ID`/`X-Correlation-ID` on every request (§6.9) via `CorrelationHandler`.

### 4.2 Storage Strategy
```csharp
// Local dev → Azurite Docker
"UseDevelopmentStorage=true"

// Azure → Azure Table Storage via Key Vault
AzureStorage__AccountName = polocalcompare<suffix>   // managed identity, set by Bicep
```
Dynamic switching is automatic based on `ASPNETCORE_ENVIRONMENT`.

### 4.3 AI Model Categories
| Category | When Available | Notes |
|---|---|---|
| **Remote** | Always (Azure AI Foundry) | Requires `AzureAiFoundry:ApiKey` or Managed Identity. A `gemini-*` `ApiEndpointRef` routes to Google's OpenAI-compatible endpoint instead (`Gemini:ApiKey`); Gemini models seed only when that key is set |
| **Browser** | When browser has WebGPU | WebLLM, runs in WASM, first-run download |
| **Ollama** | Only when running locally | `http://localhost:11434`, checked at runtime |

**Browser models on a filtered network.** `resolveBrowserModelAvailability` probes
`wwwroot/models/<webLlmModelId>/mlc-chat-config.json` first and only falls back to the CDN, so a
populated `wwwroot/models/` keeps weights off the network entirely. Weights are *not* the whole
story: `prebuiltAppConfig` resolves `model_lib` against **raw.githubusercontent.com**, a separate
host, so both fetch paths also vendor the `.wasm` libraries into `wwwroot/models/_libs/` and
`webllm-worker.js` prefers them. Populate both with `SCRIPTS/download-models.py` — that is the
only vendoring path this repo has, and it drives `curl` rather than `huggingface_hub` for the
reason PRD §9 item 8 records. (This paragraph used to point at a "Fetch WebLLM artifacts" GitHub
workflow and a `SCRIPTS/receive-artifacts.ps1`; neither has ever existed in this repo —
`.github/workflows/` holds only `deploy.yml`. Don't go looking for them.)

### 4.4 Authentication (BFF)
The API is a **Backend-for-Frontend**: it owns the OIDC code flow and the session. The Blazor WASM
client holds **no tokens** — the session lives in an encrypted `HttpOnly`, `SameSite=Strict` cookie
(`PoLocalCompare.Session`) that the browser sends automatically on same-origin calls.

- **Server routes** (`src/PoLocalCompare.Api/Auth/BffAuthentication.cs`):
  `GET /auth/login/microsoft` (OIDC challenge), `GET /auth/login/fake` (dev/test guest bypass),
  `POST /auth/logout`, `GET /auth/me` (server auth state for the SPA).
- **Client** (`BffAuthenticationStateProvider`) reads `/auth/me`; `App.razor` gates every routed page
  with `<AuthorizeRouteView>` (`@attribute [Authorize]` in `_Imports.razor`), rendering `Login` when
  unauthenticated. So the user **must** sign in before the home page.
- **Dev:** Microsoft OAuth **and** "Continue as Guest". **Prod:** Microsoft OAuth only (guest hidden).
  **Test:** the `FakeAuthHandler` maps `X-Fake-User` / `X-Fake-Roles` headers to a principal — registered
  only outside Production (throws if constructed in Production).
- API endpoint groups and SignalR hubs are protected with `.RequireAuthorization()`; `/health`, `/diag`,
  `/auth/*`, and static assets stay anonymous.
- **Config:** `AzureAd:{Authority,ClientId,AllowedTenants}` in the API appsettings; `AzureAd:ClientSecret`
  from Key Vault / App Service settings. Issuer is validated against `AllowedTenants`.

### 4.5 Real-Time (SignalR)
- `DuelHub` at `/hubs/duel`, with two groups: `duel:{id}` (per-duel, joined by `JoinDuel`) and
  `lobby` (app-wide activity, joined by `JoinLobby`). Both hub methods only *subscribe* — every
  server→client push goes through `IHubContext`, so no client can inject into another duel's feed.
- Clients: `SignalRDuelClient.cs` (per duel, transient) and `LobbyTickerService.cs` (one connection
  for the whole app session, feeding the nav-bar ticker).
- Token streaming and model status updates flow through SignalR.
- Lobby events are published by `LobbyNotifier`. The verdict event is raised from inside
  `RecordVerdictHandler` — the single path by which ELO moves — so the feed is complete by
  construction. The notifier swallows its own failures: the ticker must never fail a duel.
- The lobby ticker starts only after `/auth/me` resolves an authenticated session. Login pages do
  not probe protected duel or hub endpoints.

### 4.6 Judge and tournament UX
- Vision judging renders both outputs with network blocked, captures motion frames, then activates
  up to three visible enabled buttons and records text/pixel changes. Both opposite-order judge
  calls receive the same evidence; they still must agree or the verdict is a Tie.
- Arena source inspection is an inline native disclosure. It never opens a popup or gives model
  output an app-origin browsing context.
- Tournament draws remain automatic by default, but the draw page can optionally select exactly
  eight entrants before the server seeds them by ELO.
- Catalog browser models that the current GPU cannot run remain visible for discovery, but cannot
  be registered from that device.

### 4.7 Elo System
- K-factor: 32, Starting rating: 1200
- Updated after each completed duel (verdict recorded)

---

## 5. Critical Configuration Keys

| Key | Primary Source | Fallback |
|---|---|---|
| `KeyVault:Uri` | App Service setting | `appsettings.json` |
| `ConnectionStrings:AzureTableStorage` | Key Vault | App Service setting |
| `ConnectionStrings:AzureBlobStorage` | Key Vault | App Service setting |
| `AzureAiFoundry:ApiKey` | Key Vault | `appsettings.Development.json` / `AzureAiFoundry__ApiKey` env var |
| `Elo:KFactor` | `appsettings.json` | — (default 32) |
| `Duel:TimeLimitSeconds` | `appsettings.json` | — (900; per-model execution watchdog) |
| `Ollama:BaseUrl` | `appsettings.json` | — (`http://localhost:11434`) |
| `AiJudge:Enabled` | `appsettings.json` | — (default `false` in code, `true` in appsettings) |
| `AiJudge:DelaySeconds` | `appsettings.json` | — (**10**; code default also 10) |
| `AiJudge:Deployment` | `appsettings.json` | — (`gpt-5.4-mini`; must accept image input when vision judging is on) |
| `AiJudge:TiebreakDeployment` | `appsettings.json` | — (`gpt-5.4`; re-judges a split in both orders at medium effort; empty = off; must accept images when vision is on) |
| `AiJudge:VisionEnabled` | `appsettings.json` | — (false; true in Development — needs headless Chromium) |
| `AiJudge:TextOnlyDeployment` | `appsettings.json` | — (`gpt-4.1-nano`; used, and stamped as `JudgeModel`, whenever vision is off) |
| `Limits:DuelStartsPerMinute` | `appsettings.json` | — (**10**; 120 in Development, 0 in the test hosts; 0 = off). Fixed window per signed-in user on `POST /api/duels` and `POST /api/tournaments` |
| `Limits:DailySpendCapUsd` | `appsettings.json` | — (**5**; 0 = off). Compared with today's (UTC) summed `ApiCostUsd`; once reached, Remote duels and all tournaments get a 429 |
| `Gemini:ApiKey` | Key Vault | `Gemini__ApiKey` env var. Optional — without it no Gemini models are seeded |
| `Gemini:Endpoint` | `appsettings.json` | — (`https://generativelanguage.googleapis.com/v1beta/openai`) |

---

## 6. Running Locally

```bash
# 1. Start Azurite
docker run -p 10000:10000 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite

# 2. (Optional) Download browser model assets - weights + .wasm libs, ~5 GB.
#     Retries/resumes through the reset-prone TLS path; MODELS=small for a 1 GB subset.
python SCRIPTS/download-models.py

# 2b. There is no offline/air-gapped workflow in this repo. The "Fetch WebLLM artifacts"
#     workflow and SCRIPTS/receive-artifacts.ps1 that older revisions of this file mention
#     do not exist; .github/workflows/ holds only deploy.yml. On a network that blocks
#     HuggingFace, browser models simply fall back to the CDN or fail to load.

# 3. (Optional) Set Azure AI Foundry key - appsettings.Development.json or an env var.
#     Do NOT use `dotnet user-secrets` (see §7) - Key Vault is the production source.
$env:AzureAiFoundry__ApiKey = "your-key"

# 4. Run the API (serves Blazor WASM at https://localhost:5001)
dotnet run --project src/PoLocalCompare.Api --launch-profile https
```

**Fixed ports:** HTTP `5000`, HTTPS `5001`

---

## 7. Development Standards (Quick Reference)

> The cross-file rules are in [CLAUDE.md](CLAUDE.md); the reasoning and dated decisions behind
> them are in [HISTORY.md](HISTORY.md).

- **No AOT.** `RunAOTCompilation=false` — keeps dev builds fast.
- **Master only.** All work lands on `master`. Do not create, switch to, or push a feature branch unless the request explicitly asks for one.
- **No `dotnet user-secrets`.** Local configuration goes in `appsettings.Development.json` (or an `AzureAiFoundry__ApiKey`-style environment variable); anything genuinely secret goes in **Azure Key Vault**, which is already wired up via `KeyVault:Uri`. The `UserSecretsId` in `PoLocalCompare.Api.csproj` is legacy — do not add new values to that store. Secrets still never appear in code, logs or committed files.
- **Restart and verify after every code change.** Stop the running app, `dotnet run --project src/PoLocalCompare.Api --launch-profile https`, and confirm it actually came up (build succeeded, no startup exception, `https://localhost:5001` responds) before reporting the change as done. A change that compiles but breaks DI wiring or startup config only shows up here.
- **Warnings are errors.** `TreatWarningsAsErrors=true` globally.
- **Nullable enabled.** No null reference warnings tolerated.
- **SOLID + GoF.** Use `<remarks>` XML tags to explain pattern choices.
- **Zero-waste C#.** Delete unused files immediately. Optimize for human readability and LLM context.
- **Diagnostics endpoints:** `/health` (JSON, automated pings) and `/diag` (dev+prod hidden page). **No UI links to these.**
- **Mock visibility:** When `Features:UseRealAi` is disabled (AI responses simulated), display `USING MOCK DATA` in the top nav (see `NavMenu.razor`).
- **Audio:** Web Audio API for in-app sounds, all synthesised at play time (`audio.js`, no assets).
- **Effects:** compositor-only CSS/WAAPI plus Canvas2D confetti (`fx.js`); the only GPU render loops are the two one-shot, lease-gated shader effects in `gpu-fx.js` (verdict nebula, champion storm).
- **API docs:** OpenAPI **3.1** (pinned explicitly in `Program.cs`) + Scalar UI at `/scalar`; both dev-only and anonymous. Guarded by `Tests.E2E/Api/OpenApiContractTests`.
- **Caching & resilience:** `HybridCache` (30s TTL, tag-invalidated on verdict) fronts leaderboard and model-availability reads; HTTP callers use `AddResilienceHandler` pipelines (retry-only for SSE streams).
- **Accessibility: WCAG 2.2 Level AA.** Enforced in `wwwroot/css/app.css` (`:focus-visible` ring, 24×24 minimum target size, skip link, `scroll-margin` clearance under the sticky nav, `prefers-reduced-motion`) and in markup: clickable cards/steps are keyboard-operable with `role`/`aria-pressed`, decorative glyphs are `aria-hidden`, and status changes use `role="status"`. Colour-contrast ratios are **not** automatically verified — check new palette tokens by hand.

---

## 8. Testing Strategy

Four test projects (standards §5), one per tier. Splitting them is what keeps `PoLocalCompare.Unit`
free of Testcontainers and `Microsoft.AspNetCore.Mvc.Testing`, so it runs without Docker by project
rather than by name filter. `PoLocalCompare.E2EUI` has no `ProjectReference` at all — it drives the
app purely over HTTP.

| Test Type | Location | Notes |
|---|---|---|
| Unit | `tests/PoLocalCompare.Unit` | Pure logic, no I/O, mock data (no real AI) |
| Integration | `tests/PoLocalCompare.Integration` | Testcontainers (Azurite), fake-auth header, dev env |
| E2E API | `tests/PoLocalCompare.E2EAPI` | Black-box HTTP journeys over Testcontainers + fake auth |
| E2E UI | `tests/PoLocalCompare.E2EUI` | Playwright, headed Chrome, mobile + desktop viewports |

**Ratio contract (100 / 50 / 25 / 25).** Unit is the baseline; integration ≈ 50% of unit, each
E2E tier ≈ 25%. Current (2026-10-11): **100 unit / 40 integration / 25 E2E-API / 23 E2E-UI**,
all passing, and `SCRIPTS/check-clean.ps1` fails the build if a tier goes over. Counts are test *cases*, so a `[Theory]`
contributes one per `InlineData`, and E2E-UI counts methods × 2 viewports.

The suite was 171/101/41 on 2026-08-22 and was cut back on 2026-08-23. Roughly a third of that
came free with the `/demo` and challenge-board deletions; the rest was thinning near-duplicate
`[InlineData]` rows and assertions already pinned by a neighbouring test. **Treat the contract as
a budget: a new test in an at-capacity tier means retiring one, not appending.** Note the unit
tier lands where it does *because* the logic lives in `Shared`: `PoLocalCompare.Unit` references
only the Api project, so anything left inside `PoLocalCompare.Client` is untestable by any tier
except E2E-UI, which is the one suite CI never runs. Put new browser-side logic in `Shared/`.

```bash
dotnet test tests/PoLocalCompare.Unit          # pure logic, no Docker
dotnet test tests/PoLocalCompare.Integration   # Testcontainers Azurite (needs Docker)
dotnet test tests/PoLocalCompare.E2EAPI        # API journeys (needs Docker)
dotnet test tests/PoLocalCompare.E2EUI         # Playwright (app must be running)
```

- Integration / E2E-API tests run in a **non-Production** host so the `X-Fake-User` guest bypass works.
- E2E-UI needs a running app and `playwright install`; it runs **headed** by default (`HEADLESS=1` to
  suppress) across a 390×844 mobile and 1440×900 desktop viewport, and is **not** run in CI.
- Unit / Integration / E2EAPI run in CI as a `test` job that `build` depends on, so a red suite blocks
  the deploy. E2E-UI is excluded (no GPU on the runner) and remains a local-only gate.

---

## 9. Deployment (Azure)

```powershell
# Provision (subscription owner, once per subscription; re-runnable)
pwsh SCRIPTS/provision-azure.ps1

# Deploy code: push to master — deploy.yml builds, tests and zip-deploys
```

- **Subscription:** punkouter27@gmail.com since 2026-10-25. The original punkouter26@outlook.com
  deployment (`app-polocalcompare-win2`, `polocalcomparedevsa2`, `kv-poshared2`) is left running and is
  no longer deployed to; delete it by hand once the new host is verified.
- **`infra/main.bicep` is subscription-scoped and creates everything** — nothing is `existing`
  any more. It creates two resource groups (`westus2`):
  - `PoShared` (`infra/shared.bicep`) — Key Vault `kv-poshared-<suffix>` (**RBAC mode**),
    App Insights `appi-poshared` over Log Analytics `log-poshared`.
  - `PoLocalCompare` (`infra/app.bicep`) — storage `polocalcompare<suffix>` (shared-key auth off),
    F1 plan `asp-PoLocalCompare-f1`, App Service `app-polocalcompare-<suffix>`.
- **`<suffix>` is `take(uniqueString(subscription().id), 6)`.** Web app hosts, storage accounts and
  vaults are global names and the outlook account still holds the unsuffixed ones.
- **Managed Identity:** system-assigned on the App Service. Bicep grants Storage Table/Blob Data
  Contributor on the account (the app connects via `AzureStorage__AccountName`, no shared-key
  string) and Key Vault Secrets User on the vault (`infra/keyvault-access.bicep`). There is no
  access-policy fallback because the vault is RBAC-only.
- **CI identity:** app registration `PoLocalCompare-GitHub` with a federated credential for
  `repo:punkouter27/PoLocalCompare:ref:refs/heads/master`, holding **Website Contributor on the
  `PoLocalCompare` RG only**. That is enough to deploy and deliberately not enough to run the
  template, so deploy.yml fails with a pointer to the script instead of provisioning. The script
  sets the repo variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` and
  `AZURE_WEBAPP_NAME`.
- **App-scoped secrets** use the `PoLocalCompare--` prefix (e.g. `PoLocalCompare--AzureAiFoundry--ApiKey`);
  `PrefixKeyVaultSecretManager("PoLocalCompare")` strips the prefix. The script copies every
  `PoLocalCompare--*` secret from the outlook `kv-poshared2` except `ConnectionStrings--*`, which
  point at the old storage. **BFF OIDC needs `PoLocalCompare--AzureAd--ClientSecret`**; it is copied
  only if the old vault had it.
- **Sign-in redirect URIs:** the `AzureAd:ClientId` app registration stays where it is; the script
  adds the new host's redirect URIs to it from whichever signed-in account can see it.
- **Local dev reads the same vault.** The script repoints `KeyVault:Uri` in both appsettings files
  and leaves the Azure CLI on the gmail account, whose user holds Key Vault Secrets Officer.
  `DefaultAzureCredential` uses the CLI's active account.

---

## 10. Available Copilot Skills

These skills are installed globally (`~/AppData/Roaming/Code/User/prompts/skills/`) and can be invoked in Copilot Chat:

| Phase | Skill | When to Use |
|---|---|---|
| Day 1 | `acquire-codebase-knowledge` | Map the codebase before making changes |
| Design | `architecture-blueprint-generator` | Define/review layer boundaries |
| Design | `folder-structure-blueprint-generator` | Validate file placement |
| Build | `dotnet-best-practices` | Writing any C# class (DI, async, config) |
| Build | `dotnet-design-pattern-review` | After writing a service or domain object |
| Build | `autoresearch` | Optimize measurable metrics (test pass rate, build size) |
| Security | `security-review` | Full OWASP scan before merging to `main` |
| Deploy | `azure-deployment-preflight` | Run immediately before `azd up` |
| Operate | `azure-resource-health-diagnose` | When something breaks in Azure |
| Document | `create-readme` | When the project is stable enough to describe |
| Milestone | `repo-story-time` | End of a release — generates `REPOSITORY_SUMMARY.md` |

---

## 11. Important Files to Know

| File | Purpose |
|---|---|
| [Program.cs](src/PoLocalCompare.Api/Program.cs) | Composition root, Key Vault bootstrap, Serilog |
| [ServiceCollectionExtensions.cs](src/PoLocalCompare.Api/ServiceCollectionExtensions.cs) | DI registrations |
| [InfrastructureServiceExtensions.cs](src/PoLocalCompare.Api/InfrastructureServiceExtensions.cs) | Storage clients, typed HttpClients + resilience, repository DI |
| [DuelHub.cs](src/PoLocalCompare.Api/Features/Duels/DuelHub.cs) | SignalR hub |
| [DuelExecutionService.cs](src/PoLocalCompare.Api/Features/Duels/DuelExecutionService.cs) | Core duel orchestration |
| [StorageTables.cs](src/PoLocalCompare.Api/Common/Persistence/StorageTables.cs) | Creates every table at startup (all environments) |
| [BffAuthentication.cs](src/PoLocalCompare.Api/Auth/BffAuthentication.cs) | BFF OIDC flow, session cookie, dev fake-auth guard |
| [BffAuthenticationStateProvider.cs](src/PoLocalCompare.Client/Services/BffAuthenticationStateProvider.cs) | Client auth state from `/auth/me` |
| [WebLlmService.cs](src/PoLocalCompare.Client/Services/WebLlmService.cs) | WebGPU browser inference |
| `DOCS/` (repo root) | Project overview — read it before changing anything, when present |
| [CLAUDE.md](CLAUDE.md) | The rules that span several files, one line each |
| [HISTORY.md](HISTORY.md) | Why each rule exists; record new decisions here |
| [SCRIPTS/setup.ps1](SCRIPTS/setup.ps1) | First-run machine setup |
| [SCRIPTS/plan-webllm-artifacts.py](SCRIPTS/plan-webllm-artifacts.py) | Single source of the browser-model list for local fetch and the catalog check |

---

## 12. LLM Workflow Rules

When asked to make changes to this codebase:

1. **Assumptions first:** If a task is unclear, stop and list assumptions before coding.
2. **Pro/Con for major changes:** One "Pro" (e.g., "Removes legacy boilerplate") and one "Con" (e.g., "Increases DLL count by 1").
3. **Zero-waste:** Delete unused code/files immediately. Do not leave commented-out code.
4. **Pattern remarks:** Use `<remarks>` XML tags on non-obvious pattern choices.
5. **No AOT.** Never enable `RunAOTCompilation=true`.
6. **Security:** OWASP Top 10 compliance. No secrets in code or logs.
7. **Ports:** Never change 5000/5001 without explicit instruction.
8. **Branching:** Work on `master`. Only use another branch when explicitly asked to.
9. **Restart & verify:** After a code change, restart the app and confirm it starts cleanly — build
   output, no startup exception, `https://localhost:5001` reachable. "It compiles" is not "it runs".
10. **Read the docs first, and keep them in sync:** Read the root `DOCS/` (or `docs/`) folder, if one
    exists, and `README.md` for the project overview before making changes — they outrank inference
    from the code. When a dependency, a configuration key or a major endpoint changes, update both in
    the same change, not later.
11. **No `dotnet user-secrets`:** Local config lives in `appsettings.Development.json` or environment
    variables; real secrets live in Azure Key Vault (`KeyVault:Uri`).
12. **Git sync:** When a git sync happens, commit with a short message in casual American slang —
    not technical, so it reads like a human wrote it — and push automatically.
13. **TLDR:** Any answer longer than 100 words ends with a 20-word TLDR summary.
14. **Targeted tests only:** Do not run every suite after a code change. Run only the tests related
    to the change, or none at all if the change is simple.
15. **Do it, don't delegate it:** Avoid making the user type CLI commands or click through a web GUI
    when the agent can do it automatically.
16. **Warnings are errors:** Treat compile warnings as errors and fix them (`TreatWarningsAsErrors`
    is global).
17. **Report big deletions:** If a prompt removes more than 100 lines of code overall, mention it.
18. **UI changes get a before/after screenshot:** Take an annotated screenshot showing the old and
    new UI with the changes marked up, save it as `SCREENSHOTS/YYMMDD-feature-name.html`, and give
    the valid full path. (Files before 2026-10-26 use `YYYY-MM-DD-`; they are not renamed.)
19. **Three-tier change summary:** For code changes over 20 lines, append a TL;DR of exactly three
    lines, one sentence each: "Toddler" for the core intuition, "Child" for the behavior, and
    "Adult" for the technical or architectural details.
20. **Follow-up questions for large changes:** After a code change over 100 lines, when the prompt
    contains no other questions, offer three optional high-level questions about the app's purpose
    that could guide further changes.
21. **Deployment build version:** Display the deployment build version in the app's bottom-right
    corner as `YYMMDD`, generated automatically from the current date or Git commit date. Already
    wired: `Directory.Build.props` stamps `BuildStamp` at build time and `MainLayout.razor` renders it
    as `.build-stamp`.
