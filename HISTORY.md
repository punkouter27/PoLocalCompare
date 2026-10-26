# HISTORY

The reasoning behind the rules in [CLAUDE.md](CLAUDE.md), newest first. CLAUDE.md has cited this
file since well before it existed — it was never committed (`git log --all -- HISTORY.md` is
empty as of 2026-10-26), so the incidents behind the older rules live only in code comments and
commit messages. Add an entry whenever a rule is added, changed or broken on purpose.

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
