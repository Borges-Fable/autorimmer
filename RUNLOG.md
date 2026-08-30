# AutoRimmer — orchestration run log

Append-only. One section per orchestrator session.

## Session 1 — 2026-08-30

Opus orchestrator. Wave 0 only; stopped at the post-0.1 hard gate.

### Issues

| issue | model | wall time | outcome |
|---|---|---|---|
| 0.1 Spike: profile, headless boot, throughput (3fa4cf5) | fable | 52 min worker + ~25 min verify | merged; **not closed** — one acceptance line blocked |

**0.1 — what landed.** `profile/make-profile-agent.sh`, `profile/run-agent.sh`,
`profile/gen-modsconfig.py`, `FINDINGS.md` (439 lines). Bench
`_RimWorld-Agent` built and booting: 38 active mods (6 DLC + 32), 0 red errors,
9 known-benign warnings. Merged to main **fast-forward** (26b638c..57263e7); no
assemblies in the range, so no `Build:` commit owed.

**Verification.** Ran the profile build, booted the bench myself, and re-derived
every headline number from the raw artifacts rather than trusting the worker's
table. Boot: `Mono path[0]` proves isolation; `gameLoaded:true` at 60.8 tps
sustained; LogRelay 0 errors / 9 warnings, matching FINDINGS section 5 exactly,
with the four first-run tutor-key warnings correctly absent on a second boot.
tps: 1026 samples recomputed across 8 phases, every row within a few tps of the
published table. Thermals: 239 samples, Tctl median 93.4 / max 94.1, **zero
samples at or above 95C**. Per-frame update set checked line by line against
decompiled `Game.UpdatePlay` and `Map.MapUpdate` — correct, in order, including
the draw-half gate.

**One commit added by the orchestrator: 57263e7.** The script linked
`Mods/AutoRimmer` at `autorimmer/Mod`; spec 1.1 owns that decision and states
the layout root-relative, matching every sibling mod repo. Repointed at the repo
root so 1.1's worker inherits a bench that expects the layout its own spec
describes.

**Oddities.**
- The worker's Player.log was gone from disk by the time I verified, so its
  zero-errors evidence was uncheckable. My own boot settled it — same result.
  Worth remembering that the bench's Player.log does not survive a later
  `--batchmode` run.
- Two small overstatements in FINDINGS, neither load-bearing: batchmode frame
  rate quoted as "~2860 fps" against a measured median ~2100 (max 3162), and
  thermal minimum quoted 82.5 against a measured 80.8.
- The spike surfaced two platform bugs nobody had specced for: a symlinked
  engine stub re-roots the game at the Steam install via `/proc/self/exe`
  (silently loading Steam's Mods/, ~40 TypeLoad errors), and Hyprland delivers
  no frames to a hidden special workspace, which stops RimWorld's frame-bound
  tick loop dead (1-2 fps ~= 4 tps) while the process looks healthy. Both fixed
  in the profile scripts; both amended onto the specs that need to know.

### Hard gate: discharged

FINDINGS conclusions posted as amendments on **8 of 14** wave 1-3 specs — 1.1,
1.2, 1.3, 1.4, 2.1, 2.3, 2.4, 3.1. The remaining six (2.2, 2.5, 3.2, 3.3, 3.4,
3.5) have nothing in FINDINGS bearing on them; a decision, not an omission.
Amendments only — no spec body was rewritten.

### Needs Dorian

1. **Install `xorg-server-xvfb x11vnc`** (`sudo pacman -S --needed
   xorg-server-xvfb x11vnc`), or waive the Xvfb acceptance line. It is the only
   thing between 0.1 and `state:done`; verification afterwards is one 3-minute
   run of `./run-agent.sh --xvfb --vnc --quicktest`.
2. **Confirm the modlist deviation.** Guests + RegisterLanes were added to the
   v1 bench (DESIGN omits both from its own-mods list, but justifies the visitor
   cluster as required *by Guests*, and Factions/Guests is the M3 target).
   CashRegister added as a transitive dep. HugsLib and PerspectiveShift dropped
   as unnecessary, both verified. FINDINGS section 2.
3. **FYI, untouched:** `_RimWorld-Test/make-profile.sh` has the same latent
   engine-symlink re-rooting bug. It works today only because Steam's Mods/
   happens to carry most of the same symlinks. Not this run's scope.

### State at session end

- **Done:** none closed.
- **In flight:** none.
- **Blocked:** 0.1 (3fa4cf5) — `state:next`, BLOCKED on the Xvfb package
  install; all other work merged.
- **Next pick:** 1.1 (097f33a, agent:fable) once Dorian lifts the gate. It
  depends only on 0.1 and gates every other wave-1+ spec. 1.4 is the natural
  parallel second but depends on 1.1's protocol, so nothing runs alongside 1.1
  at the start.
