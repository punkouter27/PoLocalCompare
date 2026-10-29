# PoLocalCompare — LLM Duel Arena

> **Live:** [app-polocalcompare-win2.azurewebsites.net](https://app-polocalcompare-win2.azurewebsites.net) · Agent context in [AGENTS.md](AGENTS.md) · Rules in [CLAUDE.md](CLAUDE.md) · Reasoning in [HISTORY.md](HISTORY.md)

**What.** PoLocalCompare is a real-time LLM benchmarking arena. Two models race to generate HTML from the same prompt: **Local** models run entirely in your browser via WebLLM/WebGPU, **Remote** models call Azure AI Foundry, and (locally) **Ollama** models run as a service. Both outputs stream live over SignalR with token velocity, GPU placement, and energy telemetry. You judge the winner in side-by-side sandboxed viewports — or, if you don't pick within `AiJudge:DelaySeconds` (10 by default), an AI judge decides which output followed the prompt more accurately and records the verdict itself (configurable under `AiJudge`; verdicts are stored with the source that produced them). If exactly one model failed to produce output, the survivor takes a walkover without a judge call; if both failed, the duel stays pending and no rating moves. An Elo system (K=32, start 1200) ranks every model, with per-duel history, head-to-head "kill lists," Green Score (tokens/Wh) energy metrics, and exportable self-contained HTML lab reports.

**Who.** A solo operator and invited guests, signing in with any Microsoft account through a BFF cookie session (the WASM client never touches tokens). The UI is mobile-portrait-first with an OLED dark theme.

**Why.** Cloud models cost money per token; browser models cost only watts. This app answers, with measured evidence rather than vibes, whether a free local model is actually competitive — in speed, output quality, and cost — with a paid cloud model for practical HTML-generation tasks.

## Local Setup (bare metal, Windows)

One command from the repo root — installs prerequisites via Winget, starts Docker/Azurite, configures local mock keys, and frees ports 5000/5001:

```powershell
pwsh SCRIPTS/setup.ps1
```

Then run the app (serves the Blazor WASM client at https://localhost:5001):

```powershell
dotnet run --project src/PoLocalCompare.Api --launch-profile https
```

Optional extras:

```powershell
python SCRIPTS/download-models.py                 # pre-download WebLLM browser model assets
$env:AzureAiFoundry__ApiKey = "<key>"   # enable remote duels (or appsettings.Development.json; never user-secrets)
```

## How the AI parts work

- **Contestants** run on Azure AI Foundry, Google's Gemini endpoint (with `Gemini:ApiKey`), a local Ollama, or the browser (WebGPU). A deployment whose *name* reads classic but whose model reasons is detected from its first 400 and switched to the reasoning request shape; costs are priced by the model the provider reports serving, not the deployment name. `/diag` flags a deployment that serves a different model than it is named for.
- **The judge** (`AiJudge:*`) reads both orderings — the second by `AiJudge:CrossCheckDeployment`, a model from another family — and scores both pages against one checklist written per prompt. A split is re-asked on `AiJudge:EscalationDeployment`, then `AiJudge:TiebreakDeployment`, and only a split at the top is a Tie.
- **Spend** is capped daily by `Limits:DailySpendCapUsd`, counting contestants, the judge, and the `SpendLedger` table (prompt refine, scouting reports).
- **Compare** can suggest the matchup the leaderboard learns most from, and each **model profile** carries a scouting report written from the judge's notes (`GET /api/leaderboard/{id}/scouting-report`). Suggest matchup is always there; the scouting report and Sharpen prompt are paid, so they are off until switched on in Settings.
- **Settings** (`/settings`, from the user menu) hold the theme, sound and the optional features — visual effects, Arena telemetry, the leaderboard chart, and the paid AI helpers. Every one is off by default, so a first visit gets the plain pick → duel → verdict flow. Saved per browser.
- **Tournaments** (`/tournament`, `POST /api/tournaments`) run eight models on one prompt, seeded by ELO and auto-judged on the server. `format` picks **Bracket** (the default: single elimination, 7 duels) or **Swiss** (3 rounds, everyone plays every round, paired by score: 12 duels, ranked by points, then Buchholz, then seed). Every match is an ordinary rated duel.
- **Judge calibration** (`POST /api/dev/judge-calibration`, Development only) replays the judge over human-decided duels and reports agreement and Cohen's κ. The other `/api/dev/*` endpoints (reset, cost repricing) were removed on 2026-10-26.

## Tests

`dotnet test` per project under `tests/`. **Unit, Integration and E2E-API gate the deploy** — [.github/workflows/deploy.yml](.github/workflows/deploy.yml) runs all three in a `test` job that `build` depends on. Integration and E2E-API need Docker (Testcontainers spins Azurite).

**E2E-UI is deliberately not in CI**: it drives a real headed Chrome against a running instance and exercises WebGPU paths a runner has no GPU for. It is the one suite that goes stale silently — run it locally after touching UI markup.

## Documentation

| File | What it covers |
|---|---|
| [AGENTS.md](AGENTS.md) | Living architectural contract — tech stack, structure, config keys, deployment, testing |
| [CLAUDE.md](CLAUDE.md) | The rules that span several files, one line each |
| [HISTORY.md](HISTORY.md) | Why each rule exists — the incident, measurement or dated decision behind it |
| [docs/20260821/](docs/20260821/) | Generated reports — 5 standalone HTML dashboards plus one interactive artifact page, all with inline Mermaid, dated 2026-08-21 |

### Generated reports (`docs/20260821/`)

Snapshots of the system as it stands on `master`. Each report carries three progressive tiers
(Executive 30 s → Architectural → Implementation) and uses inline Mermaid for all diagrams. Open
the HTML files directly — no build step, no external assets except the Mermaid CDN script tag.

| Report | Purpose |
|---|---|
| [AI_SERVICES_REPORT.html](docs/20260821/AI_SERVICES_REPORT.html) | The 21 seeded models (7 browser, 2 Ollama, 12 remote), the three inference paths (Remote, LocalService, Local/WebLLM), the auto-judge, and the per-token pricing wired to the seed list rates. |
| [ARCHITECTURE_REPORT.html](docs/20260821/ARCHITECTURE_REPORT.html) | C4 container + component view of the single-host topology, every Minimal API route, every Blazor `@page`, and the request lifecycle sequence diagram. |
| [ROLES_PERMISSIONS_MATRIX.html](docs/20260821/ROLES_PERMISSIONS_MATRIX.html) | Interactive Principal × Environment × Endpoint grid. Searchable, column-togglable. Flags every `AllowAnonymous()` endpoint and calls out the absence of role-based authorization. |
| [USER_WORKFLOW.html](docs/20260821/USER_WORKFLOW.html) | Four end-to-end traces (sign-in, standard duel, browser-model duel, unattended demo) with sequence diagrams and step-by-step file references. |
| [VISUAL_ARCHITECTURE_DASHBOARD.html](docs/20260821/VISUAL_ARCHITECTURE_DASHBOARD.html) | A one-page dashboard that fuses C4 components, three pipelines, the ERD, the duel state machine, and plain-English narrative cards per slice. |
| [INTERACTIVE_DASHBOARD.html](docs/20260821/INTERACTIVE_DASHBOARD.html) | The same ground in one interactive page, with a **reading-depth control** — Orientation / Systems / Implementation — that reveals or hides each chapter's deeper strata. Published as an artifact: **[Half Cloud, Half Tab](https://claude.ai/code/artifact/43d2e3da-8976-4225-bdef-7bdacd94b0c7)**. |

The first five reports are regenerated on demand from current `master`. Each is a single
self-contained HTML file — copy them into a wiki, share them as PR attachments, or open them
locally. `INTERACTIVE_DASHBOARD.html` is the artifact source: it carries no `<html>`/`<head>`
wrapper of its own (the artifact host supplies one) and its two CSV exports need the artifact
`downloads` capability, so the published link above is the way to read it.

### Diagram validation

Every inline Mermaid block in `docs/<YYYYMMDD>/*.html` is render-validated against the **same
Mermaid version the pages pin** (10.9.1) — a diagram that fails to parse renders as a red error box
in the browser and nothing else warns:

```powershell
npm --prefix SCRIPTS/mermaid-validate install          # once — pulls mermaid-cli + Chromium
npm --prefix SCRIPTS/mermaid-validate run validate     # newest docs/<YYYYMMDD>/
npm --prefix SCRIPTS/mermaid-validate run validate -- docs/20260821
```

The exit code is the number of blocks that failed, plus any CDN pin that has drifted away from the
version the validator installs. It is not wired into CI — run it after editing a report.
