# AGENTS.md — PoLocalCompare

Read this before changing anything; CLAUDE.md only points here. The first part is the Po web app
standard this repo follows; everything after "This repo" is specific to PoLocalCompare.

## 1. Standard

> This repo owns every file in it. Nothing here is generated from, synced with, or required to stay identical to
> another repository. Headers like "SAME FILE IN EVERY PO …", "Generated from po-shared … DO NOT EDIT", and scripts
> that copy files into or check other repos are forbidden. Only Azure resources in the `PoShared` resource group
> may be shared.

### Po Web App Standard

This repo is one of several independent Po apps. It follows the conventions below, but it owns every file:
nothing is generated from, synced with, or required to stay identical to another repository. Change anything
here freely; only the *conventions* are common, never the code. Azure resources may be shared through the
`PoShared` resource group; code may not.

### Repo skeleton
```
README.md          what the app is, live URL, how to run locally, how it deploys
AGENTS.md          this standard + repo-specific notes (CLAUDE.md just points here)
CLAUDE.md          one line: "Read AGENTS.md."  (plus repo-specific notes only if unavoidable)
<App>.slnx  global.json  Directory.Build.props  Directory.Packages.props  .editorconfig
src/<App>.Api  src/<App>.Client  src/<App>.Shared  [src/<App>.Mobile]
tests/<App>.Unit  tests/<App>.Integration  [tests/<App>.E2E...]
infra/main.bicep  infra/main.bicepparam  [infra/modules/*.bicep]
scripts/           setup / helper scripts (no infra/*.ps1)
docs/              lowercase; everything else that is prose
.github/workflows/deploy.yml   the ONLY workflow
```
No `.cursorrules`, `.copilotignore`, `SPEC.md`, `tasks/`, `DOCS/` at the root (fold useful content into docs/).
`SCREENSHOTS/` is allowed only if README references it.

### Page shell (every page, signed in or out, including the sign-in page)
- Header `.po-header`, height 56px, sticky: app name top-left (`.po-header__title`, links to `/`).
- Top-right `.po-header__actions`: signed-in user's **email** (`.po-header__email`, full email, ellipsis at 16ch on
  phones) — or, signed out, one **Sign in** button `class="po-link-btn filled small"` (same style on every page) —
  then the settings gear ONLY if the app has a settings page (the owner removed settings from some apps — don't
  re-add it). Apps without OAuth show nothing in place of the email (no fake identity chips).
- Version badge `.po-version` fixed bottom-right showing `vYYMMDD`. It must never cover content: `main` gets
  `padding-bottom: calc(var(--po-space-6) + env(safe-area-inset-bottom))`, and when a mobile bottom nav exists the
  badge sits above it (`--po-stamp-lift: <nav height>`).
- Colours only from `wwwroot/css/po-tokens.css` (`--po-*`). All text ≥ 4.5:1 contrast in light AND dark mode
  (status banners: use `--po-text` on `--po-*-soft` backgrounds, never warning-on-peach).
- Login-only data must not be requested for signed-out visitors (no 401s in the console on public pages), and
  actions a signed-out visitor cannot perform are hidden.

### Version
- `Directory.Build.props` stamps ONE assembly metadata key: `BuildVersion` = UTC `yyMMdd`. No `BuildStamp`/`BuildDate`
  aliases. Every reader (header badge component, `/api/diag`, `/health` headers) reads `BuildVersion`.

### Health + /diag contract
- `GET /health/live` (liveness, returns header `X-Commit: <sha>`), `GET /health` (readiness), `GET /api/diag` →
  ```
  DiagReportDto(string Status, string Environment, string Version, DateTimeOffset CheckedAt, double CheckedMs,
                DiagCheckDto[] Checks)
  DiagCheckDto(string Name, string Group /* "api" | "data" | "service" */, string Label, string Status
               /* Healthy|Degraded|Unhealthy */, string Purpose, string Detail, double LatencyMs, string? Problem)
  ```
- UI route `/diag` ONLY (delete other diagnostics pages/routes such as `/system`, `/diagnostics`, `/status`). Page
  `<title>` and `h1`: **System status**. Layout: overall banner in plain words ("Everything is working" /
  "Working, with N things degraded" / "Not working: …"), then three sections in this order: **API**, **Data**,
  **Services**; each row = label, purpose, status pill, latency ms, problem/detail text. Every external dependency
  the app really calls appears as a check. Anonymous, nothing secret in the payload.
- Here: `/health` and `/api/diag` read ONE cached report (`CachedHealthReport`, 60 s TTL, concurrent callers share a
  run), so the probes run at most once a minute; `CheckedAt`/`CheckedMs` are when the probes last ran and how long.

### Workflow (`.github/workflows/deploy.yml`, the only one)
push to master → format check → build (Release, warnings as errors) → Unit tests → Integration tests (Testcontainers /
Azurite; Docker is available on ubuntu-latest) → publish → deploy → confirm `/health/live` X-Commit = this SHA.
If the repo has a MAUI `src/<App>.Mobile`, a second job in the SAME file (`mobile`, `needs: []`, not blocking deploy)
installs the `maui-android` workload and builds the Android target, so the client is always compiled.

### Tests
- Unit tier: fast, no I/O, runs in CI.
- Integration tier: runs in CI; locally it SKIPS with a clear message when Docker is not running (never a wall of
  `DockerUnavailableException` failures).
- E2E/UI tiers: run locally against a running app; when no app URL / browser is available they SKIP with a clear
  message. They are not part of CI.
- Docs must state real counts, not caps that are out of date.

### Azure naming
App Service `app-<repo lowercase>`; per-app resources named `<type>-<repo lowercase>`; shared things only in `PoShared`.

## 2. Concurrent work

> Several sessions (people and agents) commit to this repo, sometimes at the same time.
> - Before every commit and every push: `git fetch origin && git rebase --autostash origin/master`.
> - Stage only the files you changed (`git add <paths>`); never `git add -A` / `git add .`; check `git diff --cached`.
> - Never revert, reformat or overwrite changes you didn't make; keep the owner's recent decisions (e.g. features
>   removed on purpose stay removed — here: the `/settings` page and its gear, removed 2026-11-19).
> - Don't commit or sync from the IDE while an agent is working in this folder.
> - Pushing `master` deploys production; changes to `.github/workflows/*` need a token with the `workflow` scope.

## 3. Verification

One command sequence verifies the repo (build, Unit, then `ci-checks.ps1`: hygiene, catalog, ruff, Integration,
E2E API; then the browser tier on the Release publish):

```powershell
dotnet build PoLocalCompare.slnx -c Release && dotnet test tests/PoLocalCompare.Unit -c Release --no-build && pwsh -NoProfile -File ./scripts/ci-checks.ps1 && pwsh -NoProfile -File ./scripts/run-e2eui.ps1
```

Every command below was run on 2026-11-21 from the repo root (Windows, PowerShell or Git Bash).

| What | Command | Needs / notes |
|---|---|---|
| Format | `dotnet format PoLocalCompare.slnx --verify-no-changes` | exit 0 = clean |
| Python lint + format | `uvx ruff check . && uvx ruff format --check .` | `ruff.toml`; scripts/*.py (CI and ci-checks.ps1 run both) |
| Build | `dotnet build PoLocalCompare.slnx -c Release` | warnings are errors |
| Unit (100) | `dotnet test tests/PoLocalCompare.Unit -c Release --no-build` | nothing |
| Integration (50) | `dotnet test tests/PoLocalCompare.Integration -c Release --no-build` | Docker (Testcontainers Azurite); without it every case skips with "Docker is not running…" |
| E2E API (22) | `dotnet test tests/PoLocalCompare.E2EAPI -c Release --no-build` | Docker; skips the same way |
| E2E UI (22) | `pwsh -NoProfile -File ./scripts/run-e2eui.ps1` (`-Headed` to watch) | Runs against the **Release publish output** (what deploys): publishes `src/PoLocalCompare.Api -c Release -p:WasmApplicationEnvironmentName=Development` (.NET 10 bakes the client environment at publish; a plain publish boots as Production with no guest sign-in), starts Azurite (Docker) and the published app on https://localhost:5001, runs the tier headless, stops both. Needs Docker, nothing on 5001, Playwright Chromium (`pwsh tests/PoLocalCompare.E2EUI/bin/Release/net10.0/playwright.ps1 install chromium`). Suggest-matchup needs runnable remote models, i.e. Key Vault through `az login` |
| Before/after page (rule 18) | `pwsh -NoProfile -File ./scripts/screenshots.ps1 -Feature <name> [-Before <ref>\|-BeforeDir <pngs>] [-After <ref>] [-Screens 'home=in:/','diag=out:/diag'] [-Notes '...']` | publishes `-Before` (default `HEAD~1`, temp worktree) and `-After` (default the working tree) like run-e2eui.ps1, runs each on https://localhost:5011, shoots 1366x768 + 390x844 (signed-in screens via the dev guest door), boxes changed pixels in red, writes `SCREENSHOTS/<yyMMdd>-<name>.html` + the single `.png` (≤300 KB). Never the live site. Docker, Playwright Chromium, `az login` |
| Repo hygiene | `pwsh -NoProfile -File ./scripts/check-clean.ps1` | test budgets, dead CSS, cache-busters |
| Run locally | `dotnet run --project src/PoLocalCompare.Api --launch-profile https` → https://localhost:5001 (and /diag) | Table/Blob storage: Azurite on 10000/10002 (`docker-compose.yml`), or point `ConnectionStrings__AzureTableStorage` / `ConnectionStrings__AzureBlobStorage` at another Azurite; Key Vault `kv-poshared2` through your `az login` |
| Deploy | push to `master` → `.github/workflows/deploy.yml` (format, ruff, build, Unit, Integration, publish, deploy) | `*.md`, `docs/**`, `SCREENSHOTS/**` pushes do not deploy |
| Deploy check | `curl -sI https://app-polocalcompare-win2.azurewebsites.net/health/live` | `X-Commit:` equals the pushed SHA |
| Live | https://app-polocalcompare-win2.azurewebsites.net and https://app-polocalcompare-win2.azurewebsites.net/diag | /diag banner "Everything is working" |
| Pinned versions | .NET SDK 10.0.400 (`global.json`, latestFeature roll-forward); Python 3.10+; ruff 0.17 (via `uvx`) | |

---

## This repo — PoLocalCompare

### Deliberate deviations

| Deviation | Why | Commit sha / date | How to undo |
|---|---|---|---|
| App Service `app-polocalcompare-win2` (repo variable `AZURE_WEBAPP_NAME`), not `app-polocalcompare` | renaming needs a new App Service | 2026-11-07 (checked with `az`) | owner-approved recreate |
| `tests/Shared/` (TestModels.cs, DockerFact.cs) linked into Integration and E2EAPI | one copy of the test seeding and the Docker skip | — | copy the files into each tier |
| /diag: signed in, a **Model health** panel follows API / Data / Services | one-prompt smoke test of every model; reads the model list, so never shown signed out | `30cb2cb` 2026-11-20 | remove the panel from `Diag.razor` |
| Header Sign in button hidden on `/login` only | the page's own "Sign in with Microsoft" card is the one sign-in control | `30cb2cb` 2026-11-20 | show it again in the header |
| Version badge: up to 768px it flows as the last item of `.page` (bottom-right, after content); fixed from 769px | a fixed badge sat on /diag cards on phones (overrides the older fixed-badge rule) | `44ad02b` 2026-11-21 | `git revert 44ad02b` |
| /diag services: Table + Blob (data); Foundry, Key Vault, Entra ID, App Insights, Hugging Face, Ollama. Only Table, Foundry and Key Vault feed `/health`. A service switched off on purpose (Ollama outside Development) shows **Off**, a neutral grey pill, never degraded | every service probed live without an optional one failing the deploy gate | `14c1d97`, `a6afde0` 2026-11-21 | `DiagEndpoints.cs` Off status |
| Radzen.Blazor 12.2.0 for every control; exceptions: read-only `<table class="po-table">` data tables (see "Layout and styling") | owner decision "Radzen wins, convert" (Profile A), reversing the old "no component library" rule | `c61e24d` 2026-11-21 | — (owner rule) |
| Client keeps `PublishTrimmed=false`, so the browser tier runs on the untrimmed Release publish (what deploys) | Router `NotFoundPage` blocks trimming (owner note in `PoLocalCompare.Client.csproj`) | — | fix the trim issue, then turn trimming on |
| `run-e2eui.ps1` publishes with `-p:WasmApplicationEnvironmentName=Development` | .NET 10 bakes the client environment at publish; Production has no guest sign-in | `731523a` 2026-11-21 | drop the flag (tests then need a real account) |

**Hosting:** App Service F1 (Free, Windows, `asp-PoLocalCompare-f1`): 60 CPU-minutes and a data-out quota per day
(Azure reported 2.4 GB on 2026-11-22; over it the site stops until 00:00 UTC).
Never run test suites against the live site; live checks = at most 6 page loads + /diag + X-Commit.

### 1. What Is This Project?

**PoLocalCompare** is a real-time LLM benchmarking platform that pits **local browser-based LLMs** (WebLLM / WebGPU) against **remote cloud models** (Azure AI Foundry) and **Ollama local models** in timed HTML-generation duels. A human judge picks the winner. An Elo (K=32) rating system tracks relative model performance across all duels.

- **Live URL:** https://app-polocalcompare-win2.azurewebsites.net — the repo variable
  `AZURE_WEBAPP_NAME` (`app-polocalcompare-win2`, resource group `PoLocalCompare`) is the source of
  truth for what deploys where. `polocalcompare.azurewebsites.net` has **no DNS record at all** and
  never was the real host, so do not probe it — a smoke test against it looks exactly like an outage.
- **Solution file:** `PoLocalCompare.slnx`
- **Master prefix rule:** Every namespace, Azure resource, and Aspire resource uses `PoLocalCompare` as its prefix.

### The Core Loop
1. User selects two models (Local WebLLM / Remote Azure Foundry / Ollama)
2. User enters a prompt — models race to generate HTML
3. Both outputs stream live via SignalR
4. `AutoJudge` is the default: after `AiJudge:DelaySeconds` it asks a Foundry model which output
   implements the prompt better — both orderings, the second by a cross-check model from another
   family, scored against one checklist per prompt, with a split escalated before it is a Tie. The Arena offers a quick human override while that ten-second
   window is open; the controls disappear when the judge starts, and a pick inside the window wins
   the race (PRD §9 item 9).
5. Elo ratings update, stored in Azure Table Storage. Every verdict carries a `VerdictSource`
   (`Human`/`Ai`) so the two rankings stay separable

---

### 2. Tech Stack

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
| **Secrets** | Azure Key Vault (`kv-poshared2` in `PoShared` — see §9)  + App Service settings fallback |
| **Identity** | System-assigned Managed Identity (per §5.4); Bicep grants Storage Table/Blob RBAC + Key Vault Secrets User |
| **Auth** | Microsoft OAuth (Prod) + GUEST mode (Dev/Test) |
| **Logging** | Serilog → Console (all envs) + rolling File (Development only) |
| **Telemetry** | Serilog only; one App Insights sink for the shared sign-in record (OTel removed 2026-08-23) |
| **Versioning** | One `BuildVersion` assembly metadata key (UTC `yyMMdd` of the HEAD commit) from `Directory.Build.props`; no MinVer |
| **Styling** | Scoped `.razor.css` over `po-tokens.css` (Po web standard: light/dark follow the device, one indigo accent, Inter + Space Grotesk) |

---

### 3. Project Structure (Vertical Slice Architecture)

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
│   │   │   │                             DuelExecutionService, DuelHub, LocalResultSignal,
│   │   │   │                             SpendGuard + SpendLedger (paid calls with no duel row)
│   │   │   ├── Leaderboard/            ← endpoints, handlers, EloRecord, EloCalculator, EloHistoryRepository,
│   │   │   │                             GetScoutingReportHandler (GET /api/leaderboard/{id}/scouting-report)
│   │   │   ├── Models/                 ← endpoints, handlers, Model entity, ModelRepository, ModelSeeder
│   │   │   ├── Archive/                ← lab-report export handler + HTML renderer
│   │   │   ├── Ollama/                 ← status/pull endpoints
│   │   │   ├── Tournaments/            ← Tournament aggregate, TournamentRunner (server-side
│   │   │   │                             bracket orchestration), repository, endpoints
│   │   │   └── Diagnostics/            ← /health, /api/diag (DiagReportDto), /api/diag/smoke, /api/dev/* (calibration, E2E seed-duel)
│   │   ├── Common/
│   │   │   ├── Inference/              ← IRemoteInferenceProxy + Foundry/Ollama typed-HttpClient proxies
│   │   │   ├── Background/             ← background task queue + hosted service
│   │   │   ├── KeyVault/               ← prefix-scoped Key Vault configuration
│   │   │   └── Persistence/            ← Azurite table bootstrap
│   │   │                                 (Domain/ was dissolved in the 2026-08-13 prune — all four
│   │   │                                  calculators had exactly one consuming slice each;
│   │   │                                  Telemetry/ went with OpenTelemetry on 2026-08-23)
│   │   ├── Auth/                       ← BFF cookie session + Microsoft OIDC + dev fake scheme
│   │   ├── GlobalUsings.cs             ← all slice namespaces project-wide
│   │   ├── Program.cs                  ← Composition root
│   │   ├── ServiceCollectionExtensions.cs
│   │   └── InfrastructureServiceExtensions.cs
│   ├── PoLocalCompare.Shared/          ← DTOs, Enums, strongly-typed ids, and the pure logic
│   │   │                                 both WASM and the API (or its tests) need
│   │   ├── DTOs/                       ← DuelDto, ModelDto, LeaderboardEntryDto, VerdictRequestDto, etc.
│   │   ├── Diagnostics/                ← DiagReportDto / DiagCheckDto (the /diag contract)
│   │   ├── Enums/
│   │   ├── Ids/                        ← DuelId / ModelId (readonly record struct + JSON converters)
│   │   ├── Analysis/                   ← OutputAnalysis, LineDiff, HtmlPreview — browser-side
│   │   │                                 comparison logic; lives here so PoLocalCompare.Unit
│   │   │                                 can test it without referencing the WASM project
│   │   ├── Prompts/                    ← PromptLibrary (curated starter prompts)
│   │   ├── Tournaments/                ← BracketPlanner (pure, seeded bracket layout; sizes 2/16,
│   │   │                                 9–16 entrants with byes for the top seeds)
│   │   └── Presentation/               ← view-models, enums and helpers the .razor files lean
│   │                                     on (ArenaPhase, ModelTypeGroup, FailureReasonText,
│   │                                     HealthBadgeText, ArenaFormatting, SideMetrics,
│   │                                     RenderCoalescer, RaceCommentary, Preference). Moved
│   │                                     out of Client/ so the Unit tier can reach them at all.
│   └── PoLocalCompare.Client/          ← Blazor WASM (hosted by Api)
│       ├── Features/                   ← Compare (Home), Arena, Leaderboard, Archive, Catalog, Tournament,
│       │                                 ModelProfile, Auth (/login), Diagnostics (/diag + Model health),
│       │                                 NotFound (Settings removed 2026-11-19)
│       │                                 (Arena both streams a running duel and judges it)
│       ├── Components/                 ← incl. PromptPicker, SandboxedViewport, ModelCard,
│       │                                 ArenaViewportPanel, ArenaVoteButtons, ArenaStatusBand,
│       │                                 ArenaFailureCard, TelemetryHud, ValueChart
│       ├── Services/                   ← DuelApiClient, BffAuthenticationStateProvider,
│       │                                 WebLlmService, SignalRDuelClient, PreferencesService,
│       │                                 LocalInferenceDriver, AudioService, FxService,
│       │                                 WebGpuCapability, CorrelationHandler
│       └── Layout/
├── tests/
│   ├── PoLocalCompare.Unit/            ← Pure logic, no I/O (xUnit + Moq; no Docker)
│   ├── PoLocalCompare.Integration/     ← Testcontainers Azurite behind WebApplicationFactory
│   ├── PoLocalCompare.E2EAPI/          ← Black-box HTTP journeys over Testcontainers + fake auth
│   ├── PoLocalCompare.E2EUI/           ← Playwright, headed Chrome, mobile + desktop viewports
│   └── Shared/                         ← TestModels.cs + DockerFact.cs, linked into Integration and E2EAPI
├── scripts/                            ← setup.ps1, download-models.py, plan-webllm-artifacts.py
├── infra/                              ← Bicep: main.bicep + main.bicepparam, modules/ (shared, app, keyvault-access)
├── Directory.Build.props               ← Global: TreatWarningsAsErrors, Nullable, LangVersion=latest
├── Directory.Packages.props            ← Central Package Management
├── global.json                         ← .NET 10 SDK pin
└── docker-compose.yml                  ← Azurite container
```

---

### 4. Key Architecture Decisions

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
| **Remote** | Always (Azure AI Foundry) | Requires `AzureAiFoundry:ApiKey` or Managed Identity. Google's Gemini endpoint was removed 2026-11-23 (its key answered 402) |
| **Browser** | When browser has WebGPU | WebLLM, runs in WASM, first-run download |
| **Ollama** | Only when running locally | `http://localhost:11434`, checked at runtime |

**Browser models on a filtered network.** `resolveBrowserModelAvailability` probes
`wwwroot/models/<webLlmModelId>/mlc-chat-config.json` first and only falls back to the CDN, so a
populated `wwwroot/models/` keeps weights off the network entirely. Weights are *not* the whole
story: `prebuiltAppConfig` resolves `model_lib` against **raw.githubusercontent.com**, a separate
host, so both fetch paths also vendor the `.wasm` libraries into `wwwroot/models/_libs/` and
`webllm-worker.js` prefers them. Populate both with `scripts/download-models.py` — that is the
only vendoring path this repo has, and it drives `curl` rather than `huggingface_hub` for the
reason PRD §9 item 8 records. (This paragraph used to point at a "Fetch WebLLM artifacts" GitHub
workflow and a `scripts/receive-artifacts.ps1`; neither has ever existed in this repo —
`.github/workflows/` holds only `deploy.yml` (Bicep runs from it by hand with the `infra` input). Don't go looking for them.)

### 4.4 Authentication (BFF)
The API is a **Backend-for-Frontend**: it owns the OIDC code flow and the session. The Blazor WASM
client holds **no tokens** — the session lives in an encrypted `HttpOnly`, `SameSite=Strict` cookie
(`PoLocalCompare.Session`) that the browser sends automatically on same-origin calls.

- **Server routes** (`src/PoLocalCompare.Api/Auth/BffAuthentication.cs`):
  `GET /auth/login/microsoft` (OIDC challenge), `GET /auth/login/fake` (dev/test guest bypass),
  `POST /auth/logout`, `GET /auth/me` (server auth state for the SPA).
- **Client** (`BffAuthenticationStateProvider`) reads `/auth/me`; `App.razor` gates every routed page
  with `<AuthorizeRouteView>` (`@attribute [Authorize]` in `_Imports.razor`) and sends a signed-out
  visitor to `/login?returnUrl=…` (`RedirectToLogin`). `/login` and `/diag` are the anonymous pages.
  So the user **must** sign in before the home page.
- **/login** is the title (its one `h1`) and exactly one button, Sign in with Microsoft, in every environment.
  **Dev:** sign in as a guest through the URL `/auth/login/fake?returnUrl=/` (`?user=` picks the identity), then
  switch dev personas from the account menu. **Prod:** Microsoft OAuth only (`/auth/login/fake` does not exist).
  **Test:** the `FakeAuthHandler` maps `X-Fake-User` / `X-Fake-Roles` headers to a principal — registered
  only outside Production (throws if constructed in Production).
- API endpoint groups and SignalR hubs are protected with `.RequireAuthorization()`; `/health`, `/health/live`,
  `/api/diag`, `/auth/*`, and static assets stay anonymous.
- **Config:** `AzureAd:{Authority,ClientId,AllowedTenants}` in the API appsettings; `AzureAd:ClientSecret`
  from Key Vault / App Service settings. Issuer is validated against `AllowedTenants`.

### 4.5 Real-Time (SignalR)
- `DuelHub` at `/hubs/duel`, one group per duel: `duel:{id}`, joined by `JoinDuel`. The hub method
  only *subscribes* — every server→client push goes through `IHubContext`, so no client can inject
  into another duel's feed.
- Client: `SignalRDuelClient.cs` (per duel, transient). The app-wide `lobby` group, its
  `LobbyNotifier` and the nav-bar activity ticker were removed on 2026-10-26 — the Archive shows
  the same running and unjudged duels.
- Token streaming and model status updates flow through SignalR.

### 4.6 Judge and tournament UX
- Vision judging renders both outputs with network blocked, captures motion frames, then activates
  up to three visible enabled buttons and records text/pixel changes. Both opposite-order judge
  calls receive the same evidence; they still must agree or the verdict is a Tie.
- Arena source inspection is an inline native disclosure. It never opens a popup or gives model
  output an app-origin browsing context.
- The tournament is a 16-model bracket (15 duels). The draw is automatic by default — sixteen
  models at random, remote first — and the draw page can optionally pick 9 to 16 entrants before
  the server seeds them by ELO. Seeds the field leaves empty are first-round byes for the top
  seeds, decided at the draw (`Tournament.Draw`), so the runner never sees a one-sided match and
  the page counts them out of "N of M matches".
- Swiss can no longer be drawn (since 2026-11-07). Stored Swiss rows (`Format` = `Swiss`) are
  read-only: listed and served with their standings (`SwissStandings`, Shared), never run. One
  left Pending/Running reads as Abandoned (in memory, never written back), so it is not resumed
  and does not block a new bracket.
- The runner plays `Tournaments:MaxConcurrentMatches` matches at once (default 4, clamped 1–8).
- Catalog browser models that the current GPU cannot run remain visible for discovery, but cannot
  be registered from that device.

### 4.7 Elo System
- K-factor: 32, Starting rating: 1200
- Updated after each completed duel (verdict recorded)

---

### 5. Critical Configuration Keys

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
| `AiJudge:TiebreakDeployment` | `appsettings.json` | — (`gpt-5-mini` since 2026-11-07 — the `gpt-5.4` deployment it used to name served gpt-5-mini until then, and now serves the pricier real gpt-5.4; re-judges a split in both orders at medium effort; empty = off; must accept images when vision is on) |
| `AiJudge:VisionEnabled` | `appsettings.json` | — (false; true in Development — needs headless Chromium) |
| `AiJudge:TextOnlyDeployment` | `appsettings.json` | — (`gpt-4.1-nano`; used, and stamped as `JudgeModel`, whenever vision is off. In po-aiservices-shared2 this deployment SERVES gpt-5.4-mini — see HISTORY 2026-10-26) |
| `AiJudge:CrossCheckDeployment` | `appsettings.json` | — (empty since 2026-11-23, when Gemini was removed; reads the second ordering so a verdict survives a change of judge family; empty or a failed call = the primary reads it; must accept images when vision is on) |
| `AiJudge:EscalationDeployment` | `appsettings.json` | — (`gpt-5.4-mini`; first re-ask of a split, both orders at floor effort, before `TiebreakDeployment`; empty = skip) |
| `AiJudge:RubricEnabled` | `appsettings.json` | — (true; one checklist per prompt, cached a day, that every judge call scores against) |
| `AiJudge:MaxOutputChars` | `appsettings.json` | — (**40,000** per side on the source-only path; was 12,000) |
| `Tournaments:MaxConcurrentMatches` | `appsettings.json` | — (**4**; clamped 1–8; was a hard-coded 2 until 2026-11-07). Matches a tournament runs at once — each is a duel calling two models, so lower it first if the Foundry quota returns 429s |
| `Limits:DuelStartsPerMinute` | `appsettings.json` | — (**10**; 120 in Development, 0 in the test hosts; 0 = off). Fixed window per signed-in user on `POST /api/duels` and `POST /api/tournaments` |
| `Limits:DailySpendCapUsd` | `appsettings.json` | — (**5**; 0 = off). Compared with today's (UTC) contestant `ApiCostUsd` + `JudgeCostUsd` + the `SpendLedger` table (prompt refine, scouting reports); once reached, Remote duels and all tournaments get a 429 and no new scouting report is written |

---

### 6. Running Locally

```bash
# 1. Start Azurite
docker run -p 10000:10000 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite

# 2. (Optional) Download browser model assets - weights + .wasm libs, ~5 GB.
#     Retries/resumes through the reset-prone TLS path; MODELS=small for a 1 GB subset.
python scripts/download-models.py

# 2b. There is no offline/air-gapped workflow in this repo. The "Fetch WebLLM artifacts"
#     workflow and scripts/receive-artifacts.ps1 that older revisions of this file mention
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

### 7. Development Standards (Quick Reference)

> The cross-file rules are in "Working in this repo" below. Their dated reasoning was in HISTORY.md,
> deleted in commit `addd5d3` (`git show addd5d3^:HISTORY.md` still prints it).

- **No AOT.** `RunAOTCompilation=false` — keeps dev builds fast.
- **Master only.** All work lands on `master`. Do not create, switch to, or push a feature branch unless the request explicitly asks for one.
- **No `dotnet user-secrets`.** Local configuration goes in `appsettings.Development.json` (or an `AzureAiFoundry__ApiKey`-style environment variable); anything genuinely secret goes in **Azure Key Vault**, which is already wired up via `KeyVault:Uri`. The `UserSecretsId` in `PoLocalCompare.Api.csproj` is legacy — do not add new values to that store. Secrets still never appear in code, logs or committed files.
- **Restart and verify after every code change.** Stop the running app, `dotnet run --project src/PoLocalCompare.Api --launch-profile https`, and confirm it actually came up (build succeeded, no startup exception, `https://localhost:5001` responds) before reporting the change as done. A change that compiles but breaks DI wiring or startup config only shows up here.
- **Warnings are errors.** `TreatWarningsAsErrors=true` globally.
- **Nullable enabled.** No null reference warnings tolerated.
- **SOLID + GoF.** Use `<remarks>` XML tags to explain pattern choices.
- **Zero-waste C#.** Delete unused files immediately. Optimize for human readability and LLM context.
- **Diagnostics endpoints:** `/health/live`, `/health` (JSON, automated pings), `/api/diag` and the `/diag` "System status" page (anonymous, unlinked). **No UI links to these.**
- **Mock visibility:** When `Features:UseRealAi` is disabled (AI responses simulated), display `USING MOCK DATA` in the top nav (see `NavMenu.razor`).
- **Audio:** Web Audio API for in-app sounds, all synthesised at play time (`audio.js`, no assets).
- **Effects:** compositor-only CSS/WAAPI plus Canvas2D confetti (`fx.js`); the only GPU render loops are the two one-shot, lease-gated shader effects in `gpu-fx.js` (verdict nebula, champion storm).
- **API docs:** OpenAPI **3.1** (pinned explicitly in `Program.cs`) + Scalar UI at `/scalar`; both dev-only and anonymous. Guarded by `Tests.E2E/Api/OpenApiContractTests`.
- **Caching & resilience:** `HybridCache` (30s TTL, tag-invalidated on verdict) fronts leaderboard and model-availability reads; HTTP callers use `AddResilienceHandler` pipelines (retry-only for SSE streams).
- **Accessibility: WCAG 2.2 Level AA.** Enforced in `wwwroot/css/app.css` (`:focus-visible` ring, 24×24 minimum target size, skip link, `scroll-margin` clearance under the sticky nav, `prefers-reduced-motion`) and in markup: clickable cards/steps are keyboard-operable with `role`/`aria-pressed`, decorative glyphs are `aria-hidden`, and status changes use `role="status"`. Colour-contrast ratios are **not** automatically verified — check new palette tokens by hand.

---

### 8. Testing Strategy

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
E2E tier ≈ 25%. Current (2026-11-23): **100 unit / 50 integration / 22 E2E-API / 22 E2E-UI**,
all passing, and `scripts/check-clean.ps1` fails the build if a tier goes over. Counts are test *cases*, so a `[Theory]`
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
dotnet test tests/PoLocalCompare.Integration   # Testcontainers Azurite (needs Docker; skips without it)
dotnet test tests/PoLocalCompare.E2EAPI        # API journeys (needs Docker; skips without it)
dotnet test tests/PoLocalCompare.E2EUI         # Playwright (needs the running app + Chromium; skips without them)
```

- Integration / E2E-API tests run in a **non-Production** host so the `X-Fake-User` guest bypass works.
- E2E-UI runs against the Release publish output through `scripts/run-e2eui.ps1` (publishes, starts
  Azurite and the published app, runs headless, stops both); by hand it needs a running app and
  `playwright install`, and runs **headed** by default (`HEADLESS=1` to suppress), across a 390×844
  mobile and 1440×900 desktop viewport. It is **not** run in CI.
- CI (deploy.yml) runs the format check, ruff, the build and the **Unit and Integration** tiers before it
  publishes, so a red suite blocks the deploy. E2E-API runs locally (also through `scripts/ci-checks.ps1`);
  E2E-UI is local only (no GPU on the runner). Without Docker, Integration and E2E-API skip with one
  message (`[DockerFact]`, tests/Shared/DockerFact.cs); without a running app or Chromium, E2E-UI skips
  (`[UiFact]`, UiAvailability.cs).

---

### 9. Deployment (Azure)

```powershell
# Provision (subscription owner, once per subscription; re-runnable)
pwsh scripts/provision-azure.ps1

# Deploy code: push to master — deploy.yml checks, tests (Unit + Integration), builds and zip-deploys
```

- **deploy.yml is the only workflow** (Po web standard shape, owned by this repo). On a push to
  master it checks formatting, runs ruff, builds, runs the Unit and Integration tiers, publishes,
  deploys and waits until `/health/live` answers with this commit's `X-Commit`. No infra job, no
  rollback, no PR runs. This repo's one hook is `scripts/ci-package.ps1` (Git LFS pull + re-publish so
  `web-llm.js` is the real bundle).
  `scripts/ci-checks.ps1` (check-clean, the browser-model catalog check, ruff, Integration, E2EAPI) and
  `scripts/post-deploy-smoke.ps1` (SPA mount, cache-buster freshness, anonymous 401s) are run by hand.
  It reads repo variables `AZURE_WEBAPP_NAME` and `AZURE_RESOURCE_GROUP`. Bicep is applied by hand
  (`pwsh scripts/provision-azure.ps1`), never by a deploy.

- **Subscription:** "Azure subscription 1" (punkouter27@gmail.com). The App Service deployed to is
  `app-polocalcompare-win2` (repo variable `AZURE_WEBAPP_NAME`); the standard name would be
  `app-polocalcompare` (see Deviations).
- **What is actually there (checked 2026-11-07 with `az`, signed in as punkouter27@gmail.com):**
  "Azure subscription 1" (tenant `28e2074a-…`, MFA required — `az login --tenant <id>`) holds
  resource group `PoShared` with Key Vault **`kv-poshared2`** and the Foundry account
  **`po-aiservices-shared2`** (eastus2). No `kv-poshared-<suffix>` vault exists in it. Both
  appsettings files point at `kv-poshared2`, which this account can read, so that is the vault
  in use; the bicep description below is the plan, not the current state.
- **`infra/main.bicep` is subscription-scoped and creates everything** — nothing is `existing`
  any more. It creates two resource groups (`westus2`):
  - `PoShared` (`infra/modules/shared.bicep`) — Key Vault `kv-poshared-<suffix>` (**RBAC mode**),
    App Insights `appi-poshared` over Log Analytics `log-poshared`.
  - `PoLocalCompare` (`infra/modules/app.bicep`) — storage `polocalcompare<suffix>` (shared-key auth off),
    F1 plan `asp-PoLocalCompare-f1`, App Service `app-polocalcompare-<suffix>`.
- **`<suffix>` is `take(uniqueString(subscription().id), 6)`.** Web app hosts, storage accounts and
  vaults are global names and the outlook account still holds the unsuffixed ones.
- **Managed Identity:** system-assigned on the App Service. Bicep grants Storage Table/Blob Data
  Contributor on the account (the app connects via `AzureStorage__AccountName`, no shared-key
  string) and Key Vault Secrets User on the vault (`infra/modules/keyvault-access.bicep`). There is no
  access-policy fallback because the vault is RBAC-only.
- **CI identity:** app registration `PoLocalCompare-GitHub` with a federated credential for
  `repo:punkouter27/PoLocalCompare:ref:refs/heads/master`, holding **Website Contributor on the
  `PoLocalCompare` RG only**. That is enough to deploy and deliberately not enough to run the
  template: provisioning is `scripts/provision-azure.ps1`, never a deploy. The script
  sets the repo variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` and
  `AZURE_WEBAPP_NAME`; deploy.yml also needs `AZURE_RESOURCE_GROUP` (`PoLocalCompare`).
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

### 10. Available Copilot Skills

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

### 11. Important Files to Know

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
| [Diag.razor](src/PoLocalCompare.Client/Features/Diagnostics/Diag.razor) | The /diag "System status" page (plus `ModelHealthPanel`) |
| [DiagEndpoints.cs](src/PoLocalCompare.Api/Features/Diagnostics/DiagEndpoints.cs) | `GET /api/diag` → `DiagReportDto` |
| [scripts/setup.ps1](scripts/setup.ps1) | First-run machine setup |
| [scripts/plan-webllm-artifacts.py](scripts/plan-webllm-artifacts.py) | Single source of the browser-model list for local fetch and the catalog check |

---

### 12. LLM Workflow Rules

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
10. **Read the docs first, and keep them in sync:** Read `README.md` and this file for the project
    overview before making changes — they outrank inference
    from the code. When a dependency, a configuration key or a major endpoint changes, update both in
    the same change, not later.
11. **No `dotnet user-secrets`:** Local config lives in `appsettings.Development.json` or environment
    variables; real secrets live in Azure Key Vault (`KeyVault:Uri`).
12. **Git sync:** When a git sync happens, commit with a short message in casual American slang —
    not technical, so it reads like a human wrote it — and push automatically.
13. **TLDR:** Any answer longer than 100 words ends with a recap of under 20 words, at the very end.
14. **Targeted tests only:** Never run the whole test suite after a code change. Run only the tests
    related to what was touched, or none at all for a minor edit.
15. **Do it, don't delegate it:** Avoid making the user type CLI commands or click through a web GUI
    when the agent can do it automatically.
16. **Warnings are errors:** Treat compile warnings as errors and fix them (`TreatWarningsAsErrors`
    is global).
17. **Report big deletions:** If a prompt removes more than 100 lines of code overall, mention it.
18. **UI changes get a before/after screenshot:** Take an annotated screenshot showing the old and
    new UI with the changes marked up, save it as `SCREENSHOTS/YYMMDD-feature-name.html`, and give
    the valid full path. Also render the before and after, with the notes, into a **single PNG**
    beside it (`SCREENSHOTS/YYMMDD-feature-name.png`) and give that path too
    (`scripts/screenshots.ps1 -Feature feature-name` makes both; section 3). (Files before
    2026-10-26 use `YYYY-MM-DD-`; they are not renamed.)
19. **Three-tier change summary:** For code changes over 20 lines, append a TL;DR of exactly three
    lines, one sentence each: "Toddler" for the core intuition, "Child" for what the app actually
    does now, and "Adult" for how it was built — plus a diagram of the change, drawn in the style of
    [diagram-design](https://github.com/cathrynlavery/diagram-design).
20. **Follow-up questions for large changes:** After a code change over 100 lines, when the prompt
    contains no other questions, offer three optional high-level questions about future direction
    or logic. When the work turns up problems, propose a fix for each and wait for approval rather
    than applying it unasked.
21. **Deployment build version:** Display the deployment build version in the app's bottom-right
    corner as `YYMMDD`, generated automatically from the current date or Git commit date. Already
    wired: `Directory.Build.props` writes `BuildVersion` (the UTC date of the HEAD commit) into both
    assemblies and `BuildVersion.razor` renders it as `<footer class="po-version">vYYMMDD</footer>` (a landmark, so axe "region" passes);
    /api/diag and /health/live report the same value.
22. **Consolidate into shared components:** Before adding markup or logic, look for an existing
    component to reuse or extend, and when two surfaces repeat the same UI or behaviour, pull it
    into one. UI goes in `src/PoLocalCompare.Client/Layout/` or the feature folder (its scoped `.razor.css` moves with
    it — nothing warns when it doesn't); browser-side logic goes in `PoLocalCompare.Shared`, where
    the Unit tier can test it.

---

## Working in this repo

### Working rules

- **§12 above is a checklist for every task, not background.** The ones that are
  easy to miss, because nothing fails when you skip them: a UI change gets an annotated
  before/after page at `SCREENSHOTS/YYMMDD-feature-name.html` plus one PNG of both, and you give
  their full paths (rule 18); a change over 20 lines gets the Toddler/Child/Adult lines and a diagram
  (19); problems found get a proposed fix, not an unasked one (20); repeated UI or logic is pulled
  into one shared component (22);
  a changed dependency, config key or major endpoint updates `README.md` in the same change (10); an answer over 100
  words ends with a recap of under 20 words (13); a git sync commits with a short message in casual
  American slang (12); say so when a change removes more than 100 lines (17); run only the
  tests related to the change (14); give one Pro and one Con for a major change (2). A whole
  session's work went out on 2026-10-11 without any of these because that section was not read.

- **`master` only.** All work lands on `master`. Do not create, check out or push a feature branch
  unless the request explicitly asks for one.
- **Restart the app and verify it came up after every code change.** Stop the running process,
  `dotnet run --project src/PoLocalCompare.Api --launch-profile https`, and confirm the build
  succeeded, no startup exception was thrown, and `https://localhost:5001` responds — before calling
  the change done. Startup is where DI registration, options binding and Key Vault wiring actually
  fail; none of that shows up in a successful compile.
- **No `dotnet user-secrets`.** Local values go in `appsettings.Development.json` or an environment
  variable (`AzureAiFoundry__ApiKey`); anything genuinely secret goes in **Azure Key Vault**, already
  wired through `KeyVault:Uri`. The legacy `UserSecretsId` was removed from the Api csproj on
  2026-09-26 — don't add it back. Secrets never go in code, logs or committed files.

### Commands

```powershell
pwsh scripts/setup.ps1                                          # first-run machine setup (winget, Docker/Azurite, ports)
docker compose up -d azurite                                    # storage only
dotnet run --project src/PoLocalCompare.Api --launch-profile https   # app at https://localhost:5001
dotnet build PoLocalCompare.slnx                                # whole solution
docker compose down -v; docker compose up -d azurite            # wipe local tables (the only way to force a re-seed)
python scripts/download-models.py                               # vendor browser-model assets (~5 GB; MODELS=small ≈ 1 GB)
pwsh scripts/provision-azure.ps1                                # provision Azure + wire GitHub OIDC (once per subscription)
```

Ports **5000/5001 are fixed** — never change them without explicit instruction. The API hosts the
Blazor WASM client, so there is one process, not two.

Gates: `dotnet format PoLocalCompare.slnx --verify-no-changes`, `TreatWarningsAsErrors`, and
`uvx ruff check .` for the Python scripts (ruff.toml). (`validate-standards.ps1` was deleted in the
2026-08-13 prune: it still expected the pre-VSA `src/Client/...` layout. Don't reintroduce it.)

```powershell
pwsh scripts/check-clean.ps1    # repo-hygiene gate: budgets, dead/unused CSS, unreferenced types, cache-busters, one motion gate
```

`check-clean.ps1` is not a linter — it is the things that had already gone wrong silently and that
nothing else watches: a tier over its test budget; a class applied in markup that no stylesheet
defines (the element just renders unstyled); a class a stylesheet defines that nothing applies; a
C# type named nowhere but its own declaration; a stylesheet or script loaded without a `?v=`
cache-buster; and an effect script that reads `data-fx` or queries reduced motion itself instead
of calling `poPrefs.motionAllowed()`. It reads source only, so it is deterministic and cheap. Run it
before a push, alone or through [scripts/ci-checks.ps1](scripts/ci-checks.ps1); CI does not run it.

#### Tests

Four projects, one per tier. Unit needs nothing; Integration and E2EAPI need Docker
(Testcontainers spins Azurite) and skip with one message without it; E2EUI needs a running app and
an installed Chromium and skips without them.

```powershell
dotnet test tests/PoLocalCompare.Unit          # pure logic, no Docker
dotnet test tests/PoLocalCompare.Integration   # Testcontainers Azurite
dotnet test tests/PoLocalCompare.E2EAPI        # API journeys
dotnet test tests/PoLocalCompare.E2EUI         # Playwright (app must be running)
pwsh -NoProfile -File ./scripts/run-e2eui.ps1   # the same tier against the Release publish (starts app + Azurite itself)
dotnet test tests/PoLocalCompare.Unit --filter FullyQualifiedName~EloCalculatorTests
```

UI tests run **headed Chrome by default** across a mobile and a desktop viewport; set `HEADLESS=1`
to suppress windows and `BASE_URL` to point elsewhere. Browsers install via
`pwsh tests/PoLocalCompare.E2EUI/bin/Debug/net10.0/playwright.ps1 install chromium`.

**CI runs the format check, ruff, and the Unit and Integration tiers; the E2E tiers run locally.**
[.github/workflows/deploy.yml](.github/workflows/deploy.yml) (the Po web standard shape; this repo owns
it) checks formatting, runs ruff, builds, runs Unit and Integration, publishes, deploys and waits for
`/health/live` to report the pushed commit (a red check stops the deploy): no infra job, no rollback,
no PR runs. Its one hook here is
[scripts/ci-package.ps1](scripts/ci-package.ps1), which pulls the Git LFS `web-llm.js` and re-publishes
so the package never ships the pointer. Before pushing, run Unit and the related tiers, and
[scripts/ci-checks.ps1](scripts/ci-checks.ps1) (check-clean, the catalog check, Integration, E2EAPI);
[scripts/post-deploy-smoke.ps1](scripts/post-deploy-smoke.ps1) checks the live site by hand. E2EUI
needs real headed Chrome with WebGPU; run it after touching UI markup.

AGENTS.md §8 fixes a **ratio contract of 100 / 50 / 25 / 25** — integration ≈ half of unit, each E2E
tier ≈ a quarter. Counts are test *cases*, so a `[Theory]` contributes one per `InlineData` and each
UI method counts twice (two viewports). Keep new tests inside those proportions rather than piling
onto whichever tier is easiest to write. **Unit has one case spare, Integration sits at its cap,
E2EAPI has three spare** (99/50/22/21 as of 2026-11-20; the /diag page test replaced the
server-page check one for one) and `scripts/check-clean.ps1` fails when a tier
exceeds its budget, so the contract is enforced rather than remembered. A new Unit test has to
displace a weaker one — that is how `ArenaPhaseResolverTests` got in on 2026-09-26 (six trivial
id/label cases made way), and how the judge-shape, cost and matchup tests got in on 2026-10-26
(two `WinRateCalculator` arithmetic cases and two redundant stream-usage inline cases made way),
and how `SwissPlannerTests` got in on 2026-10-29 (five `BracketPlannerTests` cases made way;
on 2026-11-07 Swiss was removed and its five slots went to the 16-bracket and bye tests).
Say so rather than quietly raising the cap.

**E2E-UI needs the machine to itself.** The suite drives a real browser against the one running dev
instance, and `SignedInPageAsync` waits for `NetworkIdle` with a 45-second budget. Anything else
loading the same server at the same time — a second Playwright script, a browser tab left open on
`/arena` with a live SignalR connection — pushes cases past that budget and they fail as assertion
timeouts, which reads exactly like a real regression. Observed on 2026-09-22: four cases failed in
a full run and every one of them passed in isolation, three of them in 8 seconds against 48. Re-run
a failure on its own before believing it.

Two async traps in the server-side suites, both of which were causing real failures until
2026-09-10:

- **Duel execution is asynchronous.** `POST /api/duels` returns 202 once enqueued; the two result
  rows land later. A verdict posted before they exist is refused with 409 ("This duel is still
  running") — correctly, because ELO must never move on missing evidence. Tests must wait for the
  rows (`DuelTestFlow.WaitForBothResultsAsync` in Integration, `WaitForBothResultsAsync` in
  `DuelContractTests`) instead of racing them. That race was costing 7–9 of the 50 integration
  cases per run.
- **`BackgroundTaskService` is a SINGLE consumer.** `AutoJudge` runs inline inside it only
  when the grace window is 0 (tournaments); with a window it is detached since 2026-09-26, so a
  standalone duel no longer holds the worker through `AiJudge:DelaySeconds` plus the judge call.
  A duel with a browser side leaves the queue once dequeued since 2026-10-26, so a client result a
  headless test never posts no longer stalls the run — but anything else that makes a queue item
  wait still does. That is why the E2EAPI fixture keeps a short watchdog and why its duel suite
  runs in its own collection with the judge off — tournaments cannot run without the judge and
  duels cannot run with it, so they get one host each.

### Architecture rules

Each rule is one line. **The why — the incident, the measurement, the date — was in HISTORY.md**
(deleted in `addd5d3`; `git show addd5d3^:HISTORY.md`); search it for the rule's key phrase before
changing something a rule protects. A rule with no obvious reason usually has an expensive one.

#### Duel flow

- **One host, three inference paths.** Remote and Ollama run server-side
  (`DuelExecutionService` → `IRemoteInferenceProxy` → `DuelHub`). Browser models run in the tab
  (`webllm-worker.js`) and POST to `/api/duels/{id}/local-result`. A change to duel flow must be
  checked on both paths.
- **The Arena is the whole duel and drives browser inference** (`OnStartLocalInference` →
  `LocalInferenceDriver`). `Tournament.razor` does the same for whichever match is running.
- **Duel execution is asynchronous.** `POST /api/duels` returns 202; a verdict before both result
  rows exist is a 409. Tests wait for the rows.
- **`BackgroundTaskService` is a single consumer.** Anything that waits inside a queue item stalls
  every item behind it — which is why a duel with a browser side is detached as soon as it is
  dequeued, and its wait wakes on `LocalResultSignal` rather than polling storage.
- **Streaming re-renders go through `RenderCoalescer`;** terminal events (`DuelComplete`, verdicts)
  paint immediately. `SandboxedViewport.ShouldRender` keeps `srcdoc` from being re-emitted.

#### Verdicts and the judge

- **AI judging is the default; the ten-second human path is a quick override.** A human pick inside
  `AiJudge:DelaySeconds` wins; when the timer closes the vote controls disappear and `AutoJudge`
  decides. ELO moves only through `RecordVerdictHandler`, and every verdict carries a
  `VerdictSource` (`Human`, `Ai`, `Constraint` = no output was read).
- **Three invariants:** a human decision always wins the race; a judge that cannot decide leaves
  the duel `Pending`; `AiJudge:Enabled=false` is genuinely human-only.
- **The verdict write order is load-bearing:** duel, then model aggregates, then ELO history.
- **Every verdict is two judge calls in opposite orders, and they must agree** or it is a Tie. The
  second ordering is read by `AiJudge:CrossCheckDeployment` (another family); a cross-check that
  fails or is rate-limited has its ordering re-read by the primary — never left standing alone.
  The schema is `analysis` → (`checks` →) `winner` → `reason`, in that order, on purpose. A split
  climbs `AiJudge:EscalationDeployment` (floor effort), then `AiJudge:TiebreakDeployment`
  (medium); each rung needs its own two orders to agree, a split at the top is the Tie, and it
  keeps both analyses. Calibration with a `deployment` override skips the cross-check and ladder.
- **Both orderings score one checklist** (`JudgeRubricProvider`, cached a day per prompt hash);
  without one the judge derives its own. `JudgeModel` names only the models that answered.
- **The judge looks at rendered screenshots and bounded interaction evidence** when
  `AiJudge:VisionEnabled` (Development only). The renderer blocks all network, captures motion,
  activates up to three visible enabled buttons, and records text/pixel changes. Either side
  failing to render drops both; every failure degrades to source-only.
- **The judge's analysis is kept, and its calls are counted** (`Duel.JudgeAnalysis`,
  `Duel.JudgeCostUsd` including the checklist, summed by `SpendGuard`). Paid calls with no duel
  row — prompt refine, scouting reports — go to the `SpendLedger` table, which `SpendGuard` sums too.
- **The scouting report reads stored analyses only, and never feeds ELO**
  (`GET /api/leaderboard/{id}/scouting-report`, cached per newest analysed duel, 204 below three
  AI-judged duels).
- **`POST /api/dev/judge-calibration`** (`take`, `deployment`, `vision`) replays the judge over
  human-decided duels, reports raw agreement and Cohen's κ, and never writes a verdict. It needs
  human-decided duels to mean anything — κ is the number to watch, raw agreement flatters.
- **The Arena's scorecard must never feed ELO.**

#### Prompts, models and money

- **Contestants are briefed in two places, and both say "self-contained":**
  `InferencePrompt.System` (copied into `webllm-worker.js` — change both and bump the worker's
  `?v=`) and `CommenceDuelHandler.SelfContainedSuffix`, which the judge reads too.
- **A reasoning model that is not sent an effort thinks at its default.** `FoundryChatRequest`
  pins each family to its floor; a new provider's model needs the same check (gpt-oss on Ollama
  is pinned `low` in `OllamaInferenceProxy`).
- **A deployment's name is not its model.** Several in po-aiservices-shared2 serve a different
  model (no quota for the named one). `FoundryChatRequest.TryLearnShape` switches a classic-named
  deployment to the reasoning shape on its first `max_tokens`/`temperature` 400; costs price by
  the reported `ServedModel`; `/diag` flags the mismatch. Never infer the model from the name alone.
- **Cost is `TokenCost.Compute`** (`TokenCost.OfCompletion` for one-shot calls). xAI reports
  reasoning tokens outside `completion_tokens` (`ReportsReasoningSeparately`).
- **Non-streaming Foundry calls go through `FoundryChatClient.PostAsync`** (the two-route
  fallback). `FoundryInferenceProxy` keeps its own streaming version.
- **The model catalog is spread across three files that must agree:** `ModelSeeder.cs`,
  `prebuiltAppConfig` in `web-llm.js`, and `scripts/plan-webllm-artifacts.py` (run it after any
  catalog edit). Retired seed ids are never reused, and are listed in `ModelSeeder.RetiredIds`:
  their rows (ELO, duels) stay, but the picker, catalog and tournament draw leave them out. Ollama
  seeds in Development only. `DefaultPriceBook` matches the
  longest key. **Before seeding a Foundry deployment, call it once and read the `model` it
  reports** — on 2026-11-07 three seeded "nano" names and `gpt-5.4` served other models, and
  `gpt-5.5` was not deployed at all. **A remote model is seeded only with a clear price** — an
  exact `DefaultPriceBook` entry or seed pricing, not a sibling's rate by prefix: an unpriced
  model counts as $0 against the daily spend cap (eight were removed for this on 2026-11-07).
- **Models can also be added at runtime from `/catalog`** (`POST /api/models/discovered`, which
  re-checks the id). That is the only way to add one outside the seeder. A browser build the
  current GPU cannot run stays visible but its Add action is disabled.
- **`web-llm.js` is a Git LFS object.** **Browser weights are optional but bimodal** — vendor all
  of a model's files or none.
- **`Features:UseRealAi` is real:** off, `MockInferenceProxy` replaces the keyed proxies, and the
  MOCK chip must render.
- **Starting paid inference has two guards, both on the endpoint:** the `duel-start` rate limit
  and `SpendGuard` (which reads storage, not memory). `/api/prompts/refine` and the scouting
  report carry the same two (a cached report is still served when they would refuse a new one).
  Both test hosts zero the limits.
- **The availability probe is cached ten minutes** (`CacheTags.ModelAvailability`) and dropped
  when a remote side fails a real duel. Home renders the catalog before the health probe lands.
- **Tournaments run on the server, except the browser matches.** Seeded, never shuffled. A bracket
  that cannot finish is Abandoned, never Complete; a drawn match advances the better seed; the
  runner holds no state; `Tournaments:MaxConcurrentMatches` matches run at once (4 since
  2026-11-07, was 2 — lower it first if the quota pushes back).
  `autoJudgeDelaySeconds` is a per-duel override whose only caller is the tournament (0). The
  bracket is **16** (8 until 2026-11-07); the draw is automatic by default, with an optional 9–16
  entrant picker before server-side seeding. **A short field gets byes, never a smaller bracket:**
  each empty seed faces a top seed and is decided at the draw. Round maths accepts any power of
  two, because stored 8-model brackets still render. **Swiss is read-only** (2026-11-07): stored
  runs show their table (`SwissStandings`), nothing draws, runs or resumes one, and an unfinished
  one reads as Abandoned without being written back.

#### Persistence and server structure

- **Vertical slices:** `Features/<Feature>/`, flat. `Common/` only for cross-slice code. A new
  folder needs its namespace in `GlobalUsings.cs`.
- **Persistence details that bite:** writes are idempotent and ETag-safe; `HybridCache` is
  tag-invalidated (invalidate it from any new write path); typed HttpClients are retry-only — a
  per-attempt timeout aborts SSE streams; `StorageTables.EnsureAllAsync` creates every table.
- **The Archive pages by duel id** (`before=<duelId>`).
- **The leaderboard's `±N` is a Bradley–Terry interval,** read-side only; ranking is `CurrentElo`.
  **"First token" is a median.** The value chart is `ValueChart` over `ValueChartLayout`.
- **Integration tests share one Azurite,** so scope assertions to the models the test created.
- **`/api/dev/*` is Development/Test-only and behind a session:** `judge-calibration`, and
  `seed-duel` (2026-11-17), which writes one finished, Voided duel with no inference so the E2E-UI
  Arena test can run on a fresh store — it banks no ELO and the startup sweeper never re-judges
  it. Reset and cost repricing were removed 2026-10-26.
- **Observability is Serilog and nothing else,** with one App Insights sink.
- **`/diag` is the standard client "System status" page** (`Features/Diagnostics/Diag.razor`,
  2026-11-20; it replaced the server Razor Page): an overall banner, then API / Data / Services, one
  `po-diag__card` per check, from anonymous `GET /api/diag` (`DiagReportDto`), which carries no URI,
  key, id or exception message — exception TYPE at most — and an integration test pins that.
  `/health` reports exception types too. The Entra sign-in probe is tagged `diag-only`, so it never
  fails `/health` or the deploy gate. Signed in, a **Model health** panel follows the standard
  sections (`ModelHealthPanel.razor`, driven by `js/diag-models.js` over `js/diag-interop.js`). If
  the WASM client itself will not boot, `index.html`'s fallback links the raw `/api/diag` report.

#### Client structure

- **Browser-side logic belongs in `PoLocalCompare.Shared`, not in the Client project** — the Unit
  tier cannot reach the Client. `Shared/Presentation` holds view-models and helpers;
  `Client/Services` holds only what needs the browser.
- **Large pages are markup plus code-behind:** `Arena.razor` / `Arena.razor.cs`,
  `Tournament.razor` / `Tournament.razor.cs`. Razor templates (C# that returns markup) stay in
  the `.razor` file, where alone they compile.
- **The Arena's status band is one region driven by one enum** (`ArenaPhaseResolver` →
  `ArenaStatusBand`). `.arena-band__box--verdict` is sticky; do not restate `position` on the base.
- **The header is the Po shell** (`NavMenu.razor`, `po-header` from `po-tokens.css`, 2026-11-18):
  the name upper-left; the email (`po-header__email`) or the one Sign in button
  (`po-link-btn filled small`, to `/login?returnUrl=…`; hidden on `/login` itself, whose card is the
  sign-in) upper-right, on every page.
  **No settings gear and no `/settings` page** (removed 2026-11-19 at the owner's request; the
  standard shows the gear only when a settings page exists). The email is the account menu (`navmenu__account`,
  a native `<details>`): Sign out, plus the dev personas outside Production. Catalog is linked from
  Compare. One `po-version` stamp
  (`BuildVersion.razor`, `vYYMMDD`, the UTC commit date from `Directory.Build.props`) on every
  page, /diag included. Tab with nothing focused opens `/po-dev.html` (`js/po-devwindow.js`),
  which is why `FocusOnNavigate` only runs after the first in-app navigation.
- **Every submitting form validates with a FluentValidation validator in
  `Shared/Validation`** that the endpoint also runs (`Results.ValidationProblem`): the prompt
  (`PromptFormValidator` / `CommenceDuelRequestValidator` / `CreateTournamentRequestValidator`)
  and the catalog rename (`AddDiscoveredModelRequestValidator`). Errors render inline under the
  field (`PromptError` + `PromptCheck` over the `FluentValidationValidator` EditContext bridge),
  never as a toast, and the start buttons stay disabled while a request is in flight.
- **Optional features are fixed switches in `prefs.js`'s `ON` table, not user choices**
  (2026-11-19, when /settings was removed): visual effects, sound, Arena telemetry and the
  leaderboard chart are **on**; the paid AI helpers (Sharpen prompt, scouting report) are **off**.
  Only decoration and paid extras are switchable; what makes ratings converge (the ±, Suggest
  matchup, tournaments) never is. `prefs.js` stamps `<html data-fx="on">` and clears any
  `polocalcompare:pref-*` keys the old page saved; `fx.js`, `gpu-fx.js` and `wow.js` ask
  `poPrefs.motionAllowed()` (one gate, not three copies), `audio.js` reads `sound` once; Blazor asks
  `PreferencesService.IsOn`. A new extra is a key in `ON`, a case in `Preferences.StorageKey`
  (Shared) and an `IsOn` check where it renders.
- **Every surface owns exactly one BEM block, named after its file.**
- **Scoped CSS is per-`.razor`-file, and nothing warns when it isn't.** Moving markup into a child
  component means moving its rules too. `check-clean.ps1` cannot catch this.
- **A CSS class used as a JS selector is a rename that fails silently** (`fx.js`, `wow.js`), and a
  class built by interpolation reads as dead to a text scan.

#### Layout and styling

- **There is a design-token scale, and raw values are the defect.** Missing a step? Add the step.
- **Three breakpoints: 640 / 1024 / 1400.** Exceptions: Home collapses at 1080; the nav at 768.
- **`.po-page` is the only page shell, and the gutter is declared exactly once.**
- **There is no hamburger, and the header is one 56px row at every width** — 640–768px the links
  take the short labels "Bracket"/"Ranks"; below 640px they leave the header for `BottomNav`.
  `--nav-height` is `--po-header-h`; the stamp sits above BottomNav through `--po-stamp-lift`.
- **Status tints are the `--*-surface` / `--*-border` tokens, never an `rgba()` literal.**
- **Radzen is the component library (owner decision 2026-11-21: "Radzen wins, convert").** This
  reverses the earlier rule "there is no component library — Radzen has been added and removed
  twice". Every button is a `RadzenButton`, every text box a `RadzenTextBox` / `RadzenTextArea`, the
  tournament entrant tick a `RadzenCheckBox`, the Archive a `RadzenDataGrid`, confirms go through
  Radzen's `DialogService` (never `confirm()`). No raw `<button>`, `<input>`, `<textarea>` or
  `<select>` in a `.razor` file. A control that keeps the app's look is
  `ButtonStyle="ButtonStyle.Base" Variant="Variant.Text" class="lc-plain po-btn …"`: Radzen's theme
  is imported once into the `radzen` cascade layer, by the first `<style>` in `index.html`'s `<head>`
  (before `po-tokens.css`/`app.css`; E2EUI `RadzenCss_LoadsOnceBeforeTheAppCss` guards it), and `:where(.lc-plain)` reverts its
  styling to the native control, so `.po-btn` / `.po-link-btn` and the scoped rules draw it exactly
  as before. A scoped `.razor.css` rule for a class on a Radzen control is written
  `::deep .class` (the child component's element carries no scope attribute). E2E hooks are the
  same classes as before. **Allowed exceptions (the only ones):** read-only data tables stay
  semantic `<table class="po-table">` (Catalog's three lists, Leaderboard, the model profile's kill
  list, the Tournament standings, /diag's Model health table); native file inputs for resumable
  uploads and native `<audio>`/`<video>` (none in this app today). Links styled as buttons stay
  `<a class="po-btn">`. **`.po-btn--chip` is the only filter chip** (with `aria-pressed`).
- **Every page title is `<PageHeader>`, and the four title hooks carry no styling.**
- **Wide tables become cards below 640px** (`.po-table--cards`). The Archive and the profile's
  kill list override it to one or two unlabelled lines; the Archive's grid places cells explicitly.
- **Above 1080px Home is exactly one screen, and picking the second model moves you to the
  prompt.** Below it, one column with Compare as a fixed bottom bar, and the picker flows with the
  page — no inner scroll box. `ModelCard` is a 44px row, not a card. Slots clear themselves; there
  is no replace popover. E2E-UI hooks: `home__title`, `home__grid`, `home__compare .po-btn`,
  `home__picker-actions .po-btn`, `home__slot--set`, `home__full-hint` (the picker's one status line).
- **Compare and Start tournament share one rule:** disabled only for what the button cannot fix
  (models, a running bracket); a missing prompt leaves it enabled and the click focuses the prompt.
- **From 1400px the leaderboard's chart sits beside the table; from 1024px a picked model
  (`?model=`) puts its profile there instead** — `ModelProfile Embedded`, laid out by container
  queries on `.profile`, never by the window. The profile's rank is its position among *played*
  models. The profile's upsets are a grid item under the stat tiles; **`/catalog` shows one list at
  a time** and its browser list needs a definite height and fixed column widths to virtualise.
- **The Archive's bar is search · outcome chips · count, on one line.** Chips filter loaded rows.
- **The Arena page is exactly as wide as its two panels;** `--preview-scale` is set on `.arena`.
- **A route id is shape-checked before it is fetched** (`RouteIdShape`, loose on purpose — seeds
  are 25 characters).
- **Inter (text) and Space Grotesk (headings) are the only text faces,** self-hosted in
  `wwwroot/fonts` and declared by `css/po-tokens.css` (2026-11-18; monospace is the platform's).
  Material Symbols stays as BottomNav's icon font. Self-hosted since 2026-11-17: the Google Fonts files were
  CORS-fetched, and any extra request header (dev/audit fake-auth) made gstatic refuse them,
  logging a console error on every page. No third-party font origin.
- **Motion is compositor-only, with exactly two shader exceptions in `gpu-fx.js`** (asked for on
  2026-10-26): the verdict nebula (WebGPU, WebGL2 fallback, 1.9 s) and the champion storm (WebGPU
  compute, 100k particles, 7.2 s). Both start only with the effects switch on (prefs.js) and the GPU lease free, tear down mid-frame
  the moment a worker takes it, and destroy their device afterwards. No other render loop, no
  Three.js, PixiJS or Rapier, no backdrop — a WebGL backdrop has been added and removed twice.
  The winning panel's travelling rim light is a term **inside** the nebula's fragment shader (a
  panel-rect uniform), not a third effect. The confetti — including the first-token burst — is
  one-shot and stands down while the GPU lease is held. The HUD's tok/s sparkline is a Canvas2D
  repaint per telemetry update, not a loop. The verdict reveal plays once, and the loser returns
  to full opacity.
- **Audio is synthesised, never a file** — including the race sonification (the leader's grains
  step toward the centre of the stereo field), the judge's deliberation drone, which
  `DetachFromDuel` must always stop, the launch cue on Compare / Start tournament and the
  leaderboard's rank-shift sting. With Sound off `ensureCtx` returns null: no context, no graph.
- **`[JSImport]` is for synchronous reads and buffers, `IJSRuntime` for everything else.**
  `PreferencesService` binds `globalThis.poPrefs` (sync, trim-safe) and `Sparkline` passes a
  `Span<double>` as a `MemoryView`. The Client assembly is `[SupportedOSPlatform("browser")]`, so
  call sites need no guard; `AllowUnsafeBlocks` is on only for the source generator.
- **`prefs.js` is the one `<head>` script for user choices** — the extras (`poPrefs`). There is no
  theme choice since 2026-11-18: light and dark follow `prefers-color-scheme` only.
- **Do not reintroduce what the prunes removed** (2026-08-13, -08-23, -09-10, -09-26, -10-12, -10-26)
  without a user-facing reason: the deleted HISTORY.md (`git show addd5d3^:HISTORY.md`) lists each one. The Archive's outcome chips came back
  on 2026-10-26 with one (U4).

### Constraints worth knowing before you edit

- `TreatWarningsAsErrors` is global — a new warning fails the build.
- A new `Features/<Feature>/` or `Common/<Area>/` folder needs its namespace added to
  [GlobalUsings.cs](src/PoLocalCompare.Api/GlobalUsings.cs); slices reference each other with no
  per-file `using`, so omitting it produces confusing "type not found" errors elsewhere.
- Packages are centrally managed — versions go in `Directory.Packages.props`, never in a `.csproj`.
- **No AOT.** Never set `RunAOTCompilation=true`.
- `LangVersion=latest` (standards mandate C# 15; SDK 10 tops out at 14 and rejects an explicit `15`).
- Work on `master`; no feature branches unless asked.
- Restart the app and verify it starts cleanly after a code change (see Working rules).
- Never store local config with `dotnet user-secrets` — `appsettings.Development.json`, an
  environment variable, or Azure Key Vault.
- `/health` and `/diag` exist but must have **no UI links**. `/diag` shows no secret values at
  all — not even masked (2026-11-17), and no settings values (the "Settings in effect" list went with
  the server page on 2026-11-20).
- When `Features:UseRealAi` is off, the `USING MOCK DATA` indicator must render (`NavMenu.razor` —
  a compact MOCK chip beside the brand since 2026-09-26, the full sentence in its title and a
  visually-hidden span).
- UI targets **WCAG 2.2 Level AA**: keyboard-operable custom controls, `:focus-visible` ring, 24×24
  minimum target size, `role="status"` for live updates, `aria-hidden` on decorative glyphs. Colour
  contrast is not automatically checked — verify new palette tokens by hand, **in both themes and
  against every surface the token lands on**. Four failures were found that way on 2026-09-22 and
  all four came from a literal rather than a token: `#fff` on `--accent-blue` (2.54:1 in dark — the
  app's *primary* button), `#fff` on `--accent-red` twice (3.76:1 in dark), and white on the
  `--remote` / `--localservice` type chips' gradients (3.68:1 and 2.43:1, judged at the light end,
  under a comment asserting they were "well past 4.5:1"). **`--on-accent` is what goes on an accent
  fill** — it is white in light and black in dark, and it already existed; the failing rules simply
  were not reading it. A gradient is judged at its lightest stop.
- **A focus ring is never replaced by a translucent shadow.** `.home__textarea` did
  `:focus { outline: none }` — `:focus`, not `:focus-visible`, so keyboard users lost it too — and
  substituted `0 0 0 3px rgba(18,184,207,0.18)`, which cannot reach the 3:1 of SC 1.4.11, on the
  primary input of the page the app exists for. A scoped `.class:focus` also outranks the global
  `:focus-visible`, so nothing put it back.
- Styling is scoped `.razor.css` + design tokens; there are **no** inline `style=` attributes or
  `<style>` blocks left. A genuinely dynamic value (a progress width, an animation stagger) is passed
  as a CSS custom property — `style="--fill: 42%"` — and consumed by a rule in the stylesheet, so the
  styling itself stays in CSS. Colours come from `css/po-tokens.css` (this app's Po shell tokens,
  linked first): `app.css` aliases its own names (`--surface-*`,
  `--text*`, `--accent-*`) to `--po-*`, so the one indigo accent and the device-following
  light/dark reach every scoped sheet. No `[data-theme]` block and no theme toggle.
- **`box-sizing: border-box` is global** (`app.css`, a `*, *::before, *::after` rule). It was absent
  until 2026-09-10, so every `width: 100%` + padding rule rendered wider than its container and the
  excess was silently **clipped** by `article.app-content { overflow-x: clip }` — `.po-page` resolved
  to 407px inside a 375px parent on a phone, which cut "ELO" off the model-profile header and defeated
  `.po-table--cards`. Don't remove it, and don't reintroduce a `width: 100%` box that relies on being
  able to overflow.
- **Nothing inside `main` may set `min-height: 100dvh`.** `main` already starts below the 56px sticky
  nav, so a `100dvh` minimum there made *every* route 72px taller than the viewport (measured:
  `scrollHeight` 972 against `innerHeight` 900 on all six routes). The viewport guarantee belongs to
  `.page` alone; a descendant that needs a floor must use `calc(100dvh - var(--nav-height))`.
- **`index.html` cache-busts the stylesheets with `?v=N`**, matching what the `<script src>` tags
  already did. Without it the browser serves the previous build's CSS from its HTTP cache (the
  `service-worker.js` WebLLM registers is a pass-through no-op) and a change simply does not appear — this cost real time when verifying the box-sizing fix, which
  was briefly "confirmed" against a stale sheet. **Bump the number on every CSS edit.**
