# PoLocalCompare — LLM Duel Arena

> **Live:** [app-polocalcompare-win2.azurewebsites.net](https://app-polocalcompare-win2.azurewebsites.net) · System status at [/diag](https://app-polocalcompare-win2.azurewebsites.net/diag) · Agent context and rules in [AGENTS.md](AGENTS.md)

**What.** PoLocalCompare is a real-time LLM benchmarking arena. Two models race to generate HTML from the same prompt: **Local** models run entirely in your browser via WebLLM/WebGPU, **Remote** models call Azure AI Foundry, and (locally) **Ollama** models run as a service. Both outputs stream live over SignalR with token velocity, GPU placement, and energy telemetry. You judge the winner in side-by-side sandboxed viewports — or, if you don't pick within `AiJudge:DelaySeconds` (10 by default), an AI judge decides which output followed the prompt more accurately and records the verdict itself (configurable under `AiJudge`; verdicts are stored with the source that produced them). If exactly one model failed to produce output, the survivor takes a walkover without a judge call; if both failed, the duel stays pending and no rating moves. An Elo system (K=32, start 1200) ranks every model, with per-duel history, head-to-head "kill lists," Green Score (tokens/Wh) energy metrics, and exportable self-contained HTML lab reports.

**Who.** A solo operator and invited guests, signing in with any Microsoft account through a BFF cookie session (the WASM client never touches tokens). The UI is mobile-portrait-first; light and dark follow the device.

**Why.** Cloud models cost money per token; browser models cost only watts. This app answers, with measured evidence rather than vibes, whether a free local model is actually competitive — in speed, output quality, and cost — with a paid cloud model for practical HTML-generation tasks.

## Local Setup (bare metal, Windows)

One command from the repo root — installs prerequisites via Winget, starts Docker/Azurite, configures local mock keys, and frees ports 5000/5001:

```powershell
pwsh scripts/setup.ps1
```

Then run the app (serves the Blazor WASM client at https://localhost:5001):

```powershell
dotnet run --project src/PoLocalCompare.Api --launch-profile https
```

Optional extras:

```powershell
python scripts/download-models.py                 # pre-download WebLLM browser model assets
$env:AzureAiFoundry__ApiKey = "<key>"   # enable remote duels (or appsettings.Development.json; never user-secrets)
```

## How the AI parts work

- **Contestants** run on Azure AI Foundry, Google's Gemini endpoint (with `Gemini:ApiKey`), a local Ollama, or the browser (WebGPU). A deployment whose *name* reads classic but whose model reasons is detected from its first 400 and switched to the reasoning request shape; costs are priced by the model the provider reports serving, not the deployment name. `/diag` flags a deployment that serves a different model than it is named for.
- **The judge** (`AiJudge:*`) reads both orderings — the second by `AiJudge:CrossCheckDeployment`, a model from another family — and scores both pages against one checklist written per prompt. A split is re-asked on `AiJudge:EscalationDeployment`, then `AiJudge:TiebreakDeployment`, and only a split at the top is a Tie.
- **Spend** is capped daily by `Limits:DailySpendCapUsd`, counting contestants, the judge, and the `SpendLedger` table (prompt refine, scouting reports).
- **Compare** can suggest the matchup the leaderboard learns most from, and each **model profile** carries a scouting report written from the judge's notes (`GET /api/leaderboard/{id}/scouting-report`). Suggest matchup is always there; the scouting report and Sharpen prompt are paid, so they are off (fixed in `prefs.js`).
- **Fixed features** (no settings page since 2026-11-19): visual effects, sound, Arena telemetry and the leaderboard chart are on for everyone; the paid AI helpers (Sharpen prompt, scouting report) are off. They are set in `src/PoLocalCompare.Client/wwwroot/js/prefs.js`. Sign out is in the menu under your email (top right); Catalog is linked from Compare.
- **Tournaments** (`/tournament`, `POST /api/tournaments`) run a 16-model single-elimination bracket on one prompt, seeded by ELO and auto-judged on the server: 15 duels, the top two seeds meeting no earlier than the final. A field of 9 to 15 is still a 16: each missing seed is a first-round bye for a top seed. The API also takes 2 models (a 1v1). Every match is an ordinary rated duel, and `Tournaments:MaxConcurrentMatches` (default 4) sets how many run at once. Swiss can no longer be drawn (since 2026-11-07); stored Swiss runs open read-only with their standings.
- **Judge calibration** (`POST /api/dev/judge-calibration`, Development only) replays the judge over human-decided duels and reports agreement and Cohen's κ. `POST /api/dev/seed-duel` (Development/Test only) writes one finished, Voided fixture duel without inference, for the E2E-UI suite. The other `/api/dev/*` endpoints (reset, cost repricing) were removed on 2026-10-26.
- **System status** (`/diag`, unlinked, works signed out): an overall banner ("Everything is working" / "Working, with N things degraded" / "Not working: …"), then API, Data (Table Storage) and Services (Azure AI Foundry, Key Vault, Entra ID sign-in), one row per check with purpose, status, latency and detail — from anonymous `GET /api/diag` (`DiagReportDto`), which never carries endpoints, keys or exception messages. Signed in, a Model health panel below it runs one prompt through every registered model.
- **Shell** (every page, signed in and out, `/login` included): the name top-left; the email (opens Sign out) or the Sign in button, top-right; the build stamp `vYYMMDD` (UTC commit date) bottom-right. Pressing Tab before anything has focus opens a developer window (`/po-dev.html`) with live health and page stats.

## Tests

| Tier | Project | Cases | Needs | In CI |
|---|---|---|---|---|
| Unit | `tests/PoLocalCompare.Unit` | 99 | nothing | yes |
| Integration | `tests/PoLocalCompare.Integration` | 50 | Docker (Testcontainers Azurite); skips without it | yes |
| E2E API | `tests/PoLocalCompare.E2EAPI` | 22 | Docker; skips without it | no |
| E2E UI | `tests/PoLocalCompare.E2EUI` | 21 | the running app (`BASE_URL`, default https://localhost:5001) and Playwright Chromium; skips without them | no |

```powershell
dotnet test tests/PoLocalCompare.Unit
dotnet test tests/PoLocalCompare.Integration
dotnet test tests/PoLocalCompare.E2EAPI
dotnet test tests/PoLocalCompare.E2EUI      # app running first; HEADLESS=1 to hide the browser
pwsh scripts/ci-checks.ps1                  # check-clean, model catalog, ruff, Integration, E2EAPI
```

**E2E-UI is deliberately not in CI**: it drives a real Chrome against a running instance and exercises WebGPU paths a runner has no GPU for. Run it locally after touching UI markup.

## Deploy

A push to `master` runs [.github/workflows/deploy.yml](.github/workflows/deploy.yml), the only workflow: format check, ruff, build (warnings are errors), Unit and Integration tiers, publish, deploy to the App Service named by the repo variable `AZURE_WEBAPP_NAME` (`app-polocalcompare-win2`), then it waits until `/health/live` answers with the pushed commit in `X-Commit`. A red step stops the deploy and the live site keeps its last good build. Its hook [scripts/ci-package.ps1](scripts/ci-package.ps1) makes the package carry the real Git LFS `web-llm.js`. Bicep (`infra/main.bicep` + `main.bicepparam`, modules in `infra/modules/`) is applied by hand with `scripts/provision-azure.ps1`.

## Documentation

| File | What it covers |
|---|---|
| [AGENTS.md](AGENTS.md) | The Po web standard this repo follows, its deviations, and the architecture, config keys, deployment, testing and working rules |
| [scripts/README.md](scripts/README.md) | What each script does |
| [SCREENSHOTS/](SCREENSHOTS/) | Annotated before/after pages (`YYMMDD-feature.html` + one PNG) for each UI change (AGENTS.md §12 rule 18) |
