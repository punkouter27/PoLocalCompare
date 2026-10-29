# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Read first

[AGENT.MD](AGENT.MD) is the living architectural contract — tech stack, project structure, config keys,
deployment, testing strategy, and (§12) the workflow rules for every task. This file is the rules
that span several files; [HISTORY.md](HISTORY.md) is the reasoning behind each of them. Keep all
three current when you change architecture.

There is no PRD. `docs/PRD_Master.md` was deleted from the repository on 2026-09-02 (commit `54036ec`) and nothing
replaced it. Citations of the form "PRD §9 item N" in HISTORY.md and in code comments refer to that
file's decision log; `git show 54036ec^:docs/PRD_Master.md` still prints it. Record a new decision
in HISTORY.md.

If a **`DOCS/`** folder exists in the repository root, read it before making changes — it carries the
overall project summary. There is none today; say so rather than reconstructing an overview from the
code.

This repo is governed by the user's global **NET_RULES** ruleset for all `Po*` .NET solutions
(`Po` prefix everywhere, .NET 10, CPM, VSA, BFF auth, master-only branching, Azure App Service +
Table Storage). AGENT.MD documents where this repo deliberately deviates — check it before assuming
a rule is being violated.

## Working rules

- **[AGENT.MD](AGENT.MD) §12 is a checklist for every task, not background.** The ones that are
  easy to miss, because nothing fails when you skip them: a UI change gets an annotated
  before/after page at `SCREENSHOTS/YYMMDD-feature-name.html` and you give its full path (rule 18);
  a changed dependency, config key or major endpoint updates `README.md` in the same change (10); an answer over 100
  words ends with a 20-word TLDR (13); a git sync commits with a short message in casual
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

## Commands

```powershell
pwsh SCRIPTS/setup.ps1                                          # first-run machine setup (winget, Docker/Azurite, ports)
docker compose up -d azurite                                    # storage only
dotnet run --project src/PoLocalCompare.Api --launch-profile https   # app at https://localhost:5001
dotnet build PoLocalCompare.slnx                                # whole solution
docker compose down -v; docker compose up -d azurite            # wipe local tables (the only way to force a re-seed)
python SCRIPTS/download-models.py                               # vendor browser-model assets (~5 GB; MODELS=small ≈ 1 GB)
pwsh SCRIPTS/provision-azure.ps1                                # provision Azure + wire GitHub OIDC (once per subscription)
```

Ports **5000/5001 are fixed** — never change them without explicit instruction. The API hosts the
Blazor WASM client, so there is one process, not two.

There is no linter or formatter step — `TreatWarningsAsErrors` is the whole gate. (`validate-standards.ps1`
used to sit here and was deleted in the 2026-08-13 prune: it still expected the pre-VSA `src/Client/...`
layout and failed on a healthy tree. Don't reintroduce it.)

```powershell
pwsh SCRIPTS/check-clean.ps1    # repo-hygiene gate: budgets, dead/unused CSS, unreferenced types, cache-busters, one motion gate
```

`check-clean.ps1` is not a linter — it is the things that had already gone wrong silently and that
nothing else watches: a tier over its test budget; a class applied in markup that no stylesheet
defines (the element just renders unstyled); a class a stylesheet defines that nothing applies; a
C# type named nowhere but its own declaration; a stylesheet or script loaded without a `?v=`
cache-buster; and an effect script that reads `data-fx` or queries reduced motion itself instead
of calling `poPrefs.motionAllowed()`. It reads source only, so it is deterministic and cheap, and the `hygiene` job in
[deploy.yml](.github/workflows/deploy.yml) runs it beside the suites.

### Tests

Four projects, one per tier. Unit needs nothing; Integration and E2EAPI need Docker
(Testcontainers spins Azurite) and E2EUI needs a running app.

```powershell
dotnet test tests/PoLocalCompare.Unit          # pure logic, no Docker
dotnet test tests/PoLocalCompare.Integration   # Testcontainers Azurite
dotnet test tests/PoLocalCompare.E2EAPI        # API journeys
dotnet test tests/PoLocalCompare.E2EUI         # Playwright (app must be running)
dotnet test tests/PoLocalCompare.Unit --filter FullyQualifiedName~EloCalculatorTests
```

UI tests run **headed Chrome by default** across a mobile and a desktop viewport; set `HEADLESS=1`
to suppress windows and `BASE_URL` to point elsewhere. Browsers install via
`pwsh tests/PoLocalCompare.E2EUI/bin/Debug/net10.0/playwright.ps1 install chromium`.

Unit, Integration and E2EAPI **gate the deploy** — [.github/workflows/deploy.yml](.github/workflows/deploy.yml)
runs them in a `test` job that `build` depends on. **E2EUI is not in CI** (real headed Chrome, WebGPU
paths a runner has no GPU for), so it is the one suite that goes stale silently; run it locally after
touching UI markup.

AGENT.MD §8 fixes a **ratio contract of 100 / 50 / 25 / 25** — integration ≈ half of unit, each E2E
tier ≈ a quarter. Counts are test *cases*, so a `[Theory]` contributes one per `InlineData` and each
UI method counts twice (two viewports). Keep new tests inside those proportions rather than piling
onto whichever tier is easiest to write. **Unit and E2EUI sit exactly at the cap; E2EAPI has five
cases spare** (100/45/20/25 as of 2026-10-29) and `SCRIPTS/check-clean.ps1` fails when a tier
exceeds its budget, so the contract is enforced rather than remembered. A new Unit test has to
displace a weaker one — that is how `ArenaPhaseResolverTests` got in on 2026-09-26 (six trivial
id/label cases made way), and how the judge-shape, cost and matchup tests got in on 2026-10-26
(two `WinRateCalculator` arithmetic cases and two redundant stream-usage inline cases made way),
and how `SwissPlannerTests` got in on 2026-10-29 (five `BracketPlannerTests` cases made way).
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

## Architecture rules

Each rule is one line. **The why — the incident, the measurement, the date — is in
[HISTORY.md](HISTORY.md)**; search it for the rule's key phrase before changing something a rule
protects. A rule with no obvious reason usually has an expensive one.

### Duel flow

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

### Verdicts and the judge

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

### Prompts, models and money

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
  fallback, Gemini included). `FoundryInferenceProxy` keeps its own streaming version.
- **The model catalog is spread across three files that must agree:** `ModelSeeder.cs`,
  `prebuiltAppConfig` in `web-llm.js`, and `SCRIPTS/plan-webllm-artifacts.py` (run it after any
  catalog edit). Retired seed ids are never reused. Ollama seeds in Development only; Gemini only
  with `Gemini:ApiKey`. `DefaultPriceBook` matches the longest key.
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
  runner holds no state; two matches run at once and not four, because of the rate limit.
  `autoJudgeDelaySeconds` is a per-duel override whose only caller is the tournament (0). The draw
  is automatic by default, with an optional exactly-eight entrant picker before server-side seeding.
  **Swiss is the other format** (`SwissPlanner`, 8 only): 3 rounds, round 1 is 1v5 2v6 3v7 4v8, each
  later round is paired from stored results when the last one is fully decided (score, then seed,
  no rematch); a draw is ½ each, not a seed tie-break; champion = top of points → Buchholz → seed.

### Persistence and server structure

- **Vertical slices:** `Features/<Feature>/`, flat. `Common/` only for cross-slice code. A new
  folder needs its namespace in `GlobalUsings.cs`.
- **Persistence details that bite:** writes are idempotent and ETag-safe; `HybridCache` is
  tag-invalidated (invalidate it from any new write path); typed HttpClients are retry-only — a
  per-attempt timeout aborts SSE streams; `StorageTables.EnsureAllAsync` creates every table.
- **The Archive pages by duel id** (`before=<duelId>`).
- **The leaderboard's `±N` is a Bradley–Terry interval,** read-side only; ranking is `CurrentElo`.
  **"First token" is a median.** The value chart is `ValueChart` over `ValueChartLayout`.
- **Integration tests share one Azurite,** so scope assertions to the models the test created.
- **`/api/dev/judge-calibration` is the only `/api/dev/*` endpoint,** Development-only and behind a
  session (reset and cost repricing were removed 2026-10-26).
- **Observability is Serilog and nothing else,** with one App Insights sink.
- **Model health lives on `/diag`, in vanilla JS,** because `/diag` must work when the WASM client
  is the broken thing.

### Client structure

- **Browser-side logic belongs in `PoLocalCompare.Shared`, not in the Client project** — the Unit
  tier cannot reach the Client. `Shared/Presentation` holds view-models and helpers;
  `Client/Services` holds only what needs the browser.
- **Large pages are markup plus code-behind:** `Arena.razor` / `Arena.razor.cs`,
  `Tournament.razor` / `Tournament.razor.cs`. Razor templates (C# that returns markup) stay in
  the `.razor` file, where alone they compile.
- **The Arena's status band is one region driven by one enum** (`ArenaPhaseResolver` →
  `ArenaStatusBand`). `.arena-band__box--verdict` is sticky; do not restate `position` on the base.
- **Optional features are off until /settings turns them on** — decoration and paid extras only:
  visual effects, sound, Arena telemetry, the leaderboard chart, and the paid AI helpers (Sharpen
  prompt, scouting report). What makes ratings converge (the ±, Suggest matchup, tournaments) is
  never optional — the app is a benchmark. `prefs.js` reads a missing key as off and stamps `<html data-fx="on">`;
  `fx.js`, `gpu-fx.js` and `wow.js` ask `poPrefs.motionAllowed()` (one gate, not three copies),
  `audio.js` reads `sound` and follows the `po-pref` event; Blazor asks `PreferencesService.IsOn`. A new extra is one
  entry in `Preferences.All` (Shared) and an `IsOn` check where it renders — never on by default.
- **Every surface owns exactly one BEM block, named after its file.**
- **Scoped CSS is per-`.razor`-file, and nothing warns when it isn't.** Moving markup into a child
  component means moving its rules too. `check-clean.ps1` cannot catch this.
- **A CSS class used as a JS selector is a rename that fails silently** (`fx.js`, `wow.js`), and a
  class built by interpolation reads as dead to a text scan.

### Layout and styling

- **There is a design-token scale, and raw values are the defect.** Missing a step? Add the step.
- **Three breakpoints: 640 / 1024 / 1400.** Exceptions: Home collapses at 1080; the nav at 768.
- **`.po-page` is the only page shell, and the gutter is declared exactly once.**
- **There is no hamburger, and the nav is one 3.5rem row at every width** — below 768px the
  logo without its wordmark, "Bracket"/"Ranks" as the short labels, and the badge as an initial.
  `--nav-height` is 3.5rem everywhere; only the signed-out bar wraps to two rows.
- **Status tints are the `--*-surface` / `--*-border` tokens, never an `rgba()` literal.**
- **There is no component library — `.po-btn` and `.po-table` are the primitives.** Radzen has
  been added and removed twice. **`.po-btn--chip` is the only filter chip** (with `aria-pressed`).
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
- **Fonts load from `index.html`, not from an `@import` in `app.css`.**
- **Motion is compositor-only, with exactly two shader exceptions in `gpu-fx.js`** (asked for on
  2026-10-26): the verdict nebula (WebGPU, WebGL2 fallback, 1.9 s) and the champion storm (WebGPU
  compute, 100k particles, 7.2 s). Both start only with Settings → Visual effects on and the GPU lease free, tear down mid-frame
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
- **`prefs.js` is the one `<head>` script for user choices** — theme (`poTheme`) and extras
  (`poPrefs`). `theme.js` was folded into it on 2026-10-29.
- **Do not reintroduce what the prunes removed** (2026-08-13, -08-23, -09-10, -09-26, -10-12, -10-26)
  without a user-facing reason: HISTORY.md lists each one. The Archive's outcome chips came back
  on 2026-10-26 with one (U4).

## Constraints worth knowing before you edit

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
- `/health` and `/diag` exist but must have **no UI links**. `/diag` masks secret values.
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
  styling itself stays in CSS. Colour tokens are declared for light, for `prefers-color-scheme: dark`,
  and again under `:root[data-theme=...]`; the `[data-theme]` blocks must stay last or the header's
  theme toggle cannot override the OS preference.
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
