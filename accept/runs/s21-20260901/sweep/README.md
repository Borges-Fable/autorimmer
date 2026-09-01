# s21 sweep — the four never-benched merges meet a bench

Bench `_RimWorld-Agent`, `--quicktest`, assembly `1.0.0+397cc42` then
`3158c5a`. Orchestrator-run.

## Results

| suite | result |
|---|---|
| `fc287ba --phase 3` | 51 PASS / **1 FAIL** — `0.7c`, the clock moved 390 ticks across nine refusals |
| `722c951` | 80 PASS / 0 FAIL / 1 SKIP — phase 6 could not stage `too-slow` |
| `261f2e9` temperature | 71 PASS / 0 FAIL / 1 SKIP — no sealed room on the map |

## `0.7c` is a real regression and running the regression branch FIRST is why it was caught

`0.7a` and `0.7b` pass — every refusal leaves the game paused, at speed
`Paused`, not merely force-paused. So the refusal cleans up correctly. But the
tick went 849 → 1239 across nine refusals, ~43 per refusal, and the invariant
`fc287ba` exists to protect is that **a refusal leaves the clock exactly where
it was**. `TimeDriver` has a comment saying a check must run BEFORE `SetSpeed`
for that reason.

Attribution is not established here: `fix/advance-bound` (1113019) added an
arm-time refusal, and `spec/wake-halts` (280fb78) edited the same function and
merged after it. Handed to the worker that owns the first, with instructions to
determine which before changing anything.

## Two fixture facts that each cost a cycle

**`dev:unfog` needs `{all:true}`.** The bare call has a narrower scope and left
5,440 cells fogged; `rooms` then reported `total: 0` with
`skipped.fogged: 5`, so a room built in unexplored ground is invisible to the
room reader. With `{all:true}`: `cells_cleared: 5440`, `fogged_after: 0`, and
four rooms appeared.

**A bare `--quicktest` map's only sealed, roofed, non-outdoor room is the
ANCIENT DANGER.** After unfogging, the four rooms were a 156-cell `Tomb` with
`uses_outdoor_temp: false`, and three small rooms all with
`uses_outdoor_temp: TRUE` — i.e. unroofed. An instant `place-layout` of the
bedroom template puts walls down but does not roof them, so it does not produce
a temperature-testable room. `GenTemperature.ControlTemperatureTempChange`
returns 0 for a room that is null or `UsesOutdoorTemperature`, which is exactly
why the suite refuses to grade without one.

So any suite needing a sealed room on a quicktest map must either use the
ancient danger, or build and ROOF one deliberately.
