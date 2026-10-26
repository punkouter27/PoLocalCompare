# HISTORY

The reasoning behind the rules in [CLAUDE.md](CLAUDE.md), newest first. CLAUDE.md has cited this
file since well before it existed — it was never committed (`git log --all -- HISTORY.md` is
empty as of 2026-10-26), so the incidents behind the older rules live only in code comments and
commit messages. Add an entry whenever a rule is added, changed or broken on purpose.

## 2026-10-26 — A1–A10: AI pipeline, judge quality and cost

**Asked for:** ten AI options from an audit of the inference, judge and cost paths — then "do all".

**Found first, and the most expensive:** the production text-only judge never worked. The Foundry
deployments named `gpt-4.1-nano`, `gpt-5-nano` and `gpt-5.4-nano` all serve **gpt-5.4-mini**, and
`gpt-5.4` serves **gpt-5-mini** (`az cognitiveservices account deployment list`); `gpt-5.5` is
seeded but not deployed. The subscription has zero quota for the named models — only gpt-5-mini
and gpt-5.4-mini have any — so correctly backed deployments could not be created; that needs a
quota request in the portal. `FoundryChatRequest` decided the request shape from the NAME, sent
`gpt-4.1-nano` `max_tokens` + `temperature`, and got "Unsupported parameter: 'max_tokens'" on
every call, so with vision off (production) every duel stayed Pending. The "GPT-4.1 Nano"
contestant failed the same way. Now: `TryLearnShape` learns the reasoning shape from that 400
(once per deployment), costs price by the `model` the reply reports (`ServedModel`, stored per
result), and `/diag` names the mismatch. Leaderboard rows for those four entries rate the
model actually served, not the one on the label.

**What shipped:**
- **A1** A duel with a browser side leaves `BackgroundTaskService`'s single consumer as soon as it
  is dequeued (the same reasoning that detached the judge on 2026-09-26), and its wait wakes on
  `LocalResultSignal` instead of reading storage every 500 ms. Integration test pins it: a server
  duel completes while a browser duel is still waiting for its tab.
- **A2** A split climbs a ladder: `AiJudge:EscalationDeployment` (gpt-5.4-mini, floor effort)
  before `TiebreakDeployment`. Observed live: split → escalation split → tie-break agreed.
- **A3** One checklist per prompt (`JudgeRubricProvider`, cached a day by hash), and a `checks`
  array in the judge schema between the analysis and the winner. Two orderings that read a short
  prompt differently were comparing the pages against different targets. **Watch:** two pages
  that both meet every checklist item now tie — measure the tie rate with judge calibration.
- **A4** The judge is told when a page hit its output-token limit, and the source-only window is
  40,000 chars a side (was 12,000, which dropped the middle — where the `<script>` usually is).
- **A5** The second ordering is read by `AiJudge:CrossCheckDeployment` (Gemini 3.5 Flash-Lite), so
  a verdict survives a change of judge family — the self-preference correction the judge's own
  remarks said it lacked. A failed or rate-limited cross-check is re-read by the primary at once:
  one ordering left standing alone would silently drop the position check. **State on the day:**
  the Gemini key was copied from PoFightJudge's (`PoLocalCompare--Gemini--ApiKey`); its project's
  prepaid credits are depleted (HTTP 402), and Gemini 2.5 Flash-Lite is closed to new projects
  (404), so every cross-check currently falls back to the primary until the credit is topped up.
- **A6** Production vision was declined (it needs Chromium outside the Free App Service — a new
  Container App, outside the NET_RULES stack). The cheaper-vision half is the cross-check: on the
  Development vision path one of the two calls moves from gpt-5.4-mini to Flash-Lite.
- **A7** Browser seeds 013 Qwen2.5 Coder 3B and 014 Qwen3 4B (WebLLM low-resource builds); Ollama
  seeds 015 Qwen3 Coder and 016 GPT-OSS, with gpt-oss pinned to `reasoning_effort: low`. Qwen2.5
  Coder 7B (5.1 GB, not low-resource) is left to /catalog. DeepSeek V4 Flash was already seeded —
  the audit was wrong to list it as missing. No Python on the build machine, so the plan script's
  check was reproduced by hand: both new ids resolve to their own repo and lib.
- **A8** `TokenCost.OfCompletion` is the one cost formula for one-shot calls (the judge's private
  copy priced by deployment name and skipped xAI reasoning tokens). Prompt refine and scouting
  reports go to a new `SpendLedger` table that `SpendGuard` sums. The Batch-API half (50% off for
  calibration replays) was not built: it needs a GlobalBatch deployment and returns hours later,
  for a dev endpoint whose whole sample costs cents.
- **A9** Home's "Suggest matchup" picks the pair maximising p(1−p)·(σ²ᵢ+σ²ⱼ) on the leaderboard's
  Bradley–Terry fit (`Shared/Presentation/MatchupPicker`). Browser models are excluded unless one
  is already picked. On a phone the action row now wraps to two lines — accepted rather than
  shortening the starter-duel label.
- **A10** Scouting report on the profile, from the last 20 AI-judged analyses; never feeds ELO.
  The optional "redemption round" was not built.

## 2026-10-26 — U1–U10: UI compaction

**Asked for:** ten UI/UX options from a runtime audit (every route × 360/390/768/1024/1440/1920,
measuring overflow, scroll height, CLS, target size, truncation, console errors), then "do all".
The audit found no horizontal overflow and no target under 24px; the work is density, dead space
and edge cases. Before/after: `SCREENSHOTS/261026-ui-compaction.html`.

**Declined:** the brief asked for Radzen. It has been added and removed twice (`6e93265`,
`6050d24` → `5e3f5ee`); everything below uses `.po-btn` / `.po-table`.

**Decisions reversed on purpose, with the reason:**

- **Home's bounded picker (52dvh below 1080px)** existed because ~115px cards put the prompt a
  page and a half down. `ModelCard` is now a 44px row (U1), Compare is the fixed bar, and picking
  the second model scrolls to the prompt — so the nested scroll box, a thumb trap, went (U2).
- **Archive outcome chips** (removed 2026-09-30 "to keep the page simple") are back as the second
  half of one command bar rather than a row of their own, offering only outcomes present (U4).
- **The two-row phone nav (6.25rem).** One row fits a 360px bar with the logo mark, short labels
  "Bracket"/"Ranks" (exactly one form displayed, so the accessible name is the visible word,
  SC 2.5.3) and the identity as an initial. `--nav-height` is 3.5rem at every width (U9).
- **Home's Compare was disabled until the prompt was valid**; Tournament's Start had already moved
  to "disable only what the button cannot fix" (a disabled button cannot explain itself). Compare
  now follows the same rule (U7).

**Measured and fixed along the way:**

- **The profile's rank disagreed with the board** — #25 for the #2 model. `GetModelProfileHandler`
  used the projection's `Rank`, which counts every unplayed model at 1200; the board lists played
  models only. Visible the moment the profile opened beside the board (U3). Rank is now the
  position among played models; `Profile_RankAgreesWithTheLeaderboard` checks the loser too.
- **CLS:** Home at 768px 0.132 → ~0.05 (skeleton sized to the list), Home at 360px 0.043 → 0.003
  (the health pill's space is reserved), Catalog at 768px 0.095 → 0 (fixed column widths; the id
  column widest, because a wrapped id makes a row taller than `<Virtualize>`'s 44px).
- **The nav flashed Sign in + Guest** for signed-in visitors until `/auth/me` answered; session
  controls now render once it has, in a slot whose left edge does not move (U10).
- `/arena/not-a-guid` and `/model/not-a-guid` 404'd an API call before saying "not found";
  `RouteIdShape` rejects them first (loose: seeds are 25 characters). `/login` rendered "Page not
  found"; it now routes (sign-in card signed out, Compare signed in). The build stamp is absolute
  to the page instead of fixed over the last card on a phone (U10).

**Not achieved:** phone Archive cards are not meaningfully shorter — a 2-line prompt plus two
44px actions is the floor — but the prompt is no longer squeezed by the date (U5).

## 2026-10-26 — G1–G10: graphics and sound, and the first shader exception

**Asked for:** ten graphics/sound moments, explicitly including WebGPU/WebGL2 shaders and a
particle engine. That is the "user-facing reason" the prune rule requires, and it reverses two
earlier decisions on purpose:

- **The 2026-09-26 sound prune** cut a drone, token melodies and fanfares as decoration nobody asked
  for. Their successors were asked for: the judge's deliberation drone (G5), the race
  sonification (G3) and the champion fanfare (G8). All are still synthesised — no audio files.
- **"No WebGL or WebGPU render loop"** (a WebGL backdrop was added and removed on 2026-09-26 and
  again on 2026-10-11). The rule exists because browser models run WebLLM on WebGPU in the same
  tab and the tok/s the race reports is measured while they do. `gpu-fx.js` adds exactly two
  shader effects and keeps that reason intact: each starts only when the GPU lease is free
  (checked again after every await), tears down on the very next lease acquire, is one-shot and
  bounded (1.9 s, 7.2 s), and destroys its own `GPUDevice` afterwards. Both fire only when nothing
  is being timed — a verdict needs both sides finished, a champion needs every match finished.
  A backdrop, or any loop that runs while a duel streams, is still out.

**What shipped:** G1 versus slam · G2 per-model motifs (Karplus–Strong pluck / FM bell /
chiptune by model type, seeded by id) · G3 race grains panned per side, pitch and rate from tok/s ·
G4 first-token streak and chirp · G5 deliberation: two voices 3.5 Hz apart that slide to unison
when the judge's opposite-order calls agree, opposite-direction scan beams and a lease-gated
frost · G6 verdict shockwave rings, thump and haptic pulse · G7 ELO odometer (replaced a 24-step
whole-page re-render loop) and an UPSET stamp computed exactly from `VerdictResponseDto`
(rating before = after − shift) · G8 seeded fanfare, crowd swell, corner cannons, champion-path
glint · G9 verdict nebula · G10 100k-particle champion storm colliding with the bracket's cards.

**Measured during verification** (real Chrome, WebGPU, mock inference, human votes):

- The first nebula expanded past the viewport within ~1 s at 70% opacity and washed the whole
  page; its 4-px-cell sparkles read as white confetti. Retuned to a 0.85-height front, 45%
  opacity and 1-px sparkles.
- Additive blending of 100k particles clipped to white on a dark page. Particles now carry an
  intensity of 0.3 when blending additively, and blend normally on a light page, where additive
  light vanishes.
- An UPSET stamped on a 3-point rating gap. The minimum is 25 points (winner ≈ 46% expected).

**Also found:** `tests/PoLocalCompare.Unit/RecordVerdictTests.cs` did not compile at `735af7c`
(`LobbyEventKind` without `using PoLocalCompare.Shared.DTOs`), so the Unit tier — and with it
the deploy's `test` job — was red. Fixed in the same change.
