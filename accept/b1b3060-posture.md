# b1b3060 — the posture verb and `digest.posture`

Runner: `accept/b1b3060-posture.py`. Nothing below has met a bench; every result
in this file is a plan, not a measurement.

```
./accept/b1b3060-posture.py --selftest    # offline, no bench, no game
./accept/b1b3060-posture.py --dry-run     # print the plan, send nothing
./accept/b1b3060-posture.py               # the sweep: phases 0,1,2,3,4,5,9
./accept/b1b3060-posture.py --phase 6     # opt-in: SPAWNS A RAID
./accept/b1b3060-posture.py --phase 7     # opt-in: save half
./accept/b1b3060-posture.py --phase 8     # opt-in: load half
```

Exit `0` all passed · `1` at least one FAIL · `2` a fixture precondition could
not be met, which is not a spec failure.

**Read the exit code from `$?`, not from a pipe.** Session 12 reported `EXIT=0`
for a command it had piped to `tail`, and read `tail`'s status.

## The fixture the orchestrator must build

One bench (`_RimWorld-Agent/run-agent.sh`), a loaded colony, paused, with:

1. **Open buildable ground** — `find-rect` needs a clear 12×12 to paint the
   suite's `Area_Allowed` over. The suite creates and paints it itself
   (`acc-posture`), plus a second empty one (`acc-posture-empty`) for the
   zero-cell refusal. Both are left behind; delete them with
   `area {kind:"allowed", op:"delete", id:…}` if the map is at its ten-area cap.
2. **At least two colonists.** Phase 2 needs two so that "some applied, some
   refused" is distinguishable from "all refused".
3. **A colonist INCAPABLE OF VIOLENT WORK** — phase 3, and this is the one that
   needs a recipe rather than a colony.
4. **SeekAndKill loaded**, for the seek third of the posture. The suite reads
   `posture.seek_mod` and does not assume: on a bench without it, every seek
   refusal is checked to name the mod instead, and phase 6 skips with exit 2.

### How to make a violence-incapable pawn

There is no deterministic shipped route, and the suite says so rather than
pretending. **`dev:spawn-pawn {violence_capable:false}` does NOT force
incapability** — the flag maps to `PawnGenerationRequest.mustBeCapableOfViolence`,
so `false` merely stops *requiring* capability and lets the generator roll a
backstory freely.

What is left, checked against the game's own defs:

- **A backstory whose `workDisables` carries `Violent`.** This is the only route
  a shipped verb can reach, and it is a dice roll. Phase 3 rolls it: it spawns
  colonists in batches of ten, up to forty, checking `posture`'s
  `violence_capable` on every row, and SKIPS with exit 2 and this recipe if none
  appears. **Those pawns stay on the map** — run this phase on a bench you are
  willing to leave with extra colonists, or expect the digest's colonist counts
  to move.
- **Biotech's `ViolenceDisabled` gene** (`Data/Biotech/Defs/GeneDefs/
  GeneDefs_Misc.xml`, `disabledWorkTags: Violent`). Deterministic, and no
  shipped verb can apply a gene. If the orchestrator wants a repeatable phase 3,
  a `dev:add-gene` verb is the missing piece — worth an issue, not worth a
  detour here.
- **A `PawnKindDef` with `disabledWorkTags: Violent`** — exists only on Anomaly
  entities, which are not colonists.
- **No vanilla trait and no vanilla hediff does it.** The three trait defs that
  mention `Violent` (`Bloodlust`, `Psychopath`-adjacent singulars, the
  `ShootingAccuracy` spectrum) list it under `requiredWorkTags`, which is the
  opposite.

The reliable alternative is world-gen: roll starting pawns until the character
editor shows **"Incapable of: Violent"**, and save that colony as the posture
fixture.

## What each phase proves, against the issue's Acceptance section

| Acceptance bullet | Phase | How |
|---|---|---|
| One verb sets all three and reports per-pawn what it did and refused | 2 | `posture {area, seek, hostility}` in one call; every row must publish both `applied` and `refused`; every refusal must name its lever AND a reason; `after` is read back, not projected |
| A pawn incapable of violence is refused **by name**, not skipped | 3 | the pawn appears in the `incapable_of_violence` headline with `gate: "violent-disabled"` and a reason naming **both** widgets; and it has its own row whose `refused` carries `hostility` (and `seek`, when the mod is present) while `area` still applied |
| `digest` publishes `posture` with `will_seek` and `area_bound` as n/m | 0, 4 | phase 0 proves every key EXISTS (a wrong dig path must not go green); phase 4 proves the n/m strings agree with their integers, the field set is exactly the documented one, and the denominators are published in words |
| The posture survives a save/load round trip, **or the digest says it did not** | 7, 8 | phase 7 records the state and stops; a human saves and loads; phase 8 proves a `session {kind:"loaded"}` row exists **first** (a persistence test that passes without a reload is the worst kind of green), then checks all three settings per pawn by name, then checks that the block would report a loss |
| `checklists/` item 12 shrinks to "confirm the posture verb ran" | — | a doc change; `checklists/triggered.md` item 12, verdict is `digest.posture.ok` |
| `[[seek-off-is-a-decision-to-flee]]` keeps only the WHY | — | a doc change; the mechanism half moved into `SeekVerbs.cs` and DESIGN, and was corrected on the way |

Phases the Acceptance section does not ask for, kept because they are cheap and
they catch the things that actually break:

- **0b, the refusals.** A lever without `area` (the whole point of the verb), an
  unknown area, a bad `seek`, a bad `hostility`, and — the sharp one — a
  **zero-cell area**, which is refused because `ForbidUtility.InAllowedArea`
  short-circuits on `TrueCount > 0`, so binding to it would restrict nothing
  while the verb reported every pawn bound.
- **0.8, `posture` as a predicate section**, proved by ARMING one rather than by
  reading a list — a path that does not resolve is a refusal at arm time
  (session 19), so an armed path is the assertion. A misspelled field must be
  refused and must name the real keys.
- **1, the pure read.** No lever at all mutates nothing: the tick does not move,
  the journal watermark does not move, and `action.journal_seq` is `null` with a
  `provenance` sentence rather than the closed-writer zero.
- **4.6, the FAILING direction.** Set `hostility:"Flee"` and watch `ok` go
  false, `flee_risk` fill with names, and `on_contact.flee` rise. Without this
  the block could be hard-coded green.
- **5, `dry_run`** decides for every pawn and changes none of the three
  settings, and says its `after` is the before state rather than an observation.
- **9, the standing invariants.** Zero red errors; two consecutive digests give
  identical posture numbers with the clock stopped (an observer that mutates is
  the hazard this project names first); the bench is left paused with no modal.
- **10, `--selftest`.** The suite's own assertions, offline, proved able to
  FAIL: `shape()` on a renamed key, the `eq(...,None)` trap demonstrated rather
  than described, `pawn_rows()` seeing REJECTED rows (phase 3's whole bullet
  rests on that — a helper that walked only `accepted` would miss the pawn the
  bullet is about), the n/m cross-check catching a drift, and the closed
  `on_contact` vocabulary catching both a missing and an extra verdict. Run it
  before taking this file to a bench.

## Phase 6, and why it is opt-in

It is the empirical half of the finding that **inverts this issue's own
premise**. The issue says `hostility_response` "describes a node nothing
consults" when seek is on. It is the other way round:
`JobGiver_ConfigurableHostilityResponse` lives in the **`HumanlikeConstant`**
think tree, which `SeekAndKill/ThinkTreeInjector` never injects into and which
`Verse.AI/Pawn_JobTracker.DetermineNextJob` runs **before** the main tree —
re-running it every 30 ticks with `JobCondition.InterruptForced`. So `Flee`
beats seek. Phase 6 sets seek ON with `Flee`, spawns a raid, and looks for a
colonist in a flee/cower job; then repairs with one call and shows
`on_contact.flee` back at 0.

Two costs, both stated rather than hidden:

- **It spawns hostiles at your colony.** Not in the default sweep.
- **It uses a bare tick budget, and that is deliberate.**
  `advance {until:{condition}}` compares with `< <= > >= == !=` only, and the
  only published field that would answer "is this pawn fleeing" is
  `colonists.list[*].job` — a *truncated* driver report string
  (`Journal.Truncate(job, 48)`), which is not a contract to compare with `==`.
  A raid also needs its pawns to walk into line of sight before any reaction
  fires, a distance the suite cannot know. So the wait is `advance {ticks:2500}`
  with the reason written down, rather than a predicate that would be a lie.

Every `advance` in this file goes through one helper that always carries
`unread_ok` and `through_casualties` (Worker B's `722c951` contract), both
naming this suite: the journal is read at phase 9 rather than per advance, and a
casualty in phase 6 is the fixture rather than a halt condition.

## What this suite deliberately does not prove

- **That an allowed area confines a pawn's pathing.** That is vanilla
  `ForbidUtility` and not ours. What is asserted is that the digest counts a
  zero-cell area as *not* binding, which is the game's own `TrueCount > 0`
  short-circuit.
- **That `on_contact` predicts a sleeping or force-jobbed pawn.** It models the
  STANDING posture only — `ThinkNode_ConditionalLyingDown` sits at root index 0
  and the constant tree's gate requires `pawn.Awake()`, so contact wakes the pawn
  first and the verdict then applies; `PawnUtility.PlayerForcedJobNowOrSoon`
  nulls the constant node while a forced job runs. Both are transient, and a
  field that flickers with the day/night cycle is not a posture. The block says
  so in its own note.
- **That the seek set survives a save/load when Perspective Shift is present.**
  `SeekAndKillGameComponent.ExposeData` scribes `SK_SeekPawns` only when
  `PSInterop.PsToggleShared` is false; with PS present, PS's own
  `seekAtWillPawns` is the source of truth. PS is out of the bench modlist, so
  phase 8 proves the S&K path and names the other one rather than testing it.
