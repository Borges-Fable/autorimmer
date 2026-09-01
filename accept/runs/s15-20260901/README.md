# Session 15 — the session-A bench pass

Bench `_RimWorld-Agent`, `--quicktest` 250x250 desert map, assembly built at
`25b65ff` (the session-A merge). Watched run: Dorian on Hyprland workspace 3,
prefs at 2560x1600 fullscreen.

**The mod loaded the right IL, structurally rather than by inspection.** The
bench's `Mods/AutoRimmer` is a symlink straight to this repo, so it loads
`Assemblies/AutoRimmer.dll` from the working tree — no copy step to go stale.
Confirmed live anyway: `rwa verbs` reported **120 verbs including
`site-survey`**, which did not exist before this merge.

## Suite

`accept/s13-mod-surface.py` — **174 PASS / 0 FAIL / 1 SKIP / 3 NOTE**
(`s13-mod-surface.txt`). Session 13 scored 169; the +5 are session A's added
checks.

**EXIT=2 does not mean a failure.** `precondition()` calls `sys.exit(2)` when a
fixture is unavailable, so the run ABORTS at the first unmet precondition rather
than finishing. Here that is 5.1, the last check of the last phase, so no
coverage was lost — but "1 SKIP" means "it stopped there", not "one was skipped
and the rest ran". A future session reading `EXIT=2` will otherwise call this a
failure. Session 13's 169/0/1 will have exited 2 for the same reason.

The SKIP is the same deliberate one as session 13: `threat-pardon` has no
hostile to pardon on a fresh map.

## What was proven by hand, beyond the suite

| | |
|---|---|
| refusal names the REFUSING cell | wall on the interaction cell → `blocker: Wall at [130,134]`, `removal: deconstruct`, `cell_role: interaction`, against an asked-for cell of `[130,135]`. Session 13's banked failure named a `WoodLog` on the target cell with `removal: none`. |
| survey verdict ≡ spawn refusal | both returned *"Interaction spot is blocked by wooden wall."* verbatim — one routine, two callers |
| corner↔centre | `pos [135,145]` → footprint corner `[134,145]`; candidate `at [124,125] 3x2` spawned at its own `pos [125,125]` read back `rect [124,125,3,2]` — **exact**, the one-cell-west bug closed |
| even-axis rotation shift | `pos [125,125] rot South` for a 3x2 → footprint corner `[124,124]`, i.e. `AdjustForRotation`'s z shift applied |
| the widget gate | `find-rect --def HiTechResearchBench` refused **before searching** (`examined: 0, gate_calls: 0`) with `clause: "research"`, `detail: "needs research 'MicroelectronicsBasics'"` |
| margin tier | 3x2 footprint → 9x8 surveyed rect, 65 cells, 60 standable, `notable: 5`, and reachability of the interaction cell from the first free colonist |
| chair tolerated | chair on the interaction cell → `verdict.ok: true` |
| interaction-spot OVERLAP | two benches wanting `[125,126]` → *"Interaction spot overlaps with the interaction spot of wooden simple research bench."*, `reason: interaction-overlap` |
| NotBlockingAnyInteractionCells | a footprint over a neighbour's interaction cell → *"Simple research bench would block simple research bench's interaction spot."* — the SEVENTH `CanSpawnAt` branch session A found `WhyNoSpawn` was missing |
| finding K, with a human | `speed_changes: [{tick: 36926, from: Ultrafast, to: Superfast, by: "external"}]`, `overshoot_bound: 30` from **Ultrafast** while the advance EXITED at Superfast |

## Three things the pass did NOT prove, and one it disproved

1. **The chair is not LISTED.** `c718e4a`'s acceptance says the tolerated chair
   appears "under `tiers.interaction` as tolerated". The cell reports
   `{ok: true, blocker: null, standable: true}` and names no occupant, so an
   agent cannot tell "a chair is there, which is fine" from "it is empty" — and
   the difference matters, because that chair is what the NEXT bench's overlap
   check will trip on.
2. **"the verdict is IDENTICAL to what `build` returns"** cannot be tested:
   `build` and `dev:spawn-thing {buildable:true}` are session B.
3. **`7382bdd` bullet 1 is NOT met**, measured rather than asserted:
   `dev:spawn-thing --at` is refused with a suggestion (bullet 2, met), but
   `dev:spawn-thing --wibble 3` **places anyway**, and `things`, `find-rect`,
   `site-survey`, `map-view`, `nearest`, `reachable`, `pawns` and `digest` all
   accept an unknown key silently. What shipped is a near-miss detector on a
   known alias list, not unknown-argument rejection.
4. **DISPROVED, and it is `3a5ff6c`'s thesis reproduced in thirty seconds:**
   `site-survey` refused the overlapping bench, and `dev:spawn-thing --mode
   direct` **placed it anyway** (`placed: 1`). `GenSpawn.CanSpawnAt` runs no
   PlaceWorker, so the god-hand cannot see what the survey sees. Two benches
   then sat on one standing square — a state no colonist could build. Working
   as designed (Evan, 2026-09-01: the god-hand stays default); `buildable:true`
   is what closes it. `evidence/survey-refuses-overlap.json`.

## A methodological note worth keeping

Two ad-hoc reads in this session dug the wrong path and printed a confident
zero: `things --def X` reports under `rollups`, not `list`, so a one-liner
looking for `list` said "0 benches" when there were two. And the suite tally
was first taken from output piped through `tail -30`, which threw away four
phases — the exact trap `RUNLOG.md` records against session 12 ("reported
EXIT=0 by piping to tail and reading tail's status"). Both were caught, but
neither by the suite; the shape discipline that `accept/` enforces on checks
does not apply to the orchestrator's own shell one-liners, and it should.
