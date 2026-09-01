# 261f2e9 — the temperature target, the room that is watched, and the rot term

Runner: `accept/261f2e9-temperature.py`. **Nothing below has met a bench**;
every result in this file is a plan, not a measurement.

```
./accept/261f2e9-temperature.py --selftest   # offline, no bench, no game
./accept/261f2e9-temperature.py --dry-run    # print the plan, send nothing
./accept/261f2e9-temperature.py              # the sweep: phases 0,1,2,3,4,5
./accept/261f2e9-temperature.py --phase 6    # opt-in: the COLD half, slow
```

Exit `0` all passed · `1` at least one FAIL · `2` a fixture precondition could
not be met, which is not a spec failure.

**Read the exit code from `$?`, not from a pipe.** Session 12 reported `EXIT=0`
for a command it had piped to `tail`, and read `tail`'s status.

## What shipped

Three things, and the middle one is the one the issue did not ask for.

1. **`temp-set`** — the actuator. `temp-set {room|things|rect|cells|filter,
   target_c, dry_run?}`. Celsius only.
2. **`temp-control`** — the read. Same addressing, plus no-argument = the whole
   map. Mutates nothing.
3. **`digest.temperature`** — a registered predicate section that answers "is
   any room I care about out of range" at every read, and
   **`digest.resources.food_rot`** — the rot term `food_days` never had.

## The fixture the orchestrator must build

The default sweep (phases 0–5) needs **one enclosed, roofed, non-outdoor room of
at least 4 cells** and nothing else; it stages its own heater and its own food
with `dev:spawn-thing` if the map has neither. Phase 6 is the one that needs a
real freezer.

### Why the room must be sealed and roofed, and it is not a nicety

`GenTemperature.ControlTemperatureTempChange` opens with

```
Room room = cell.GetRoom(map);
if (room == null || room.UsesOutdoorTemperature) return 0f;
```

so a controller serving an open room is not weak — it is a **no-op**, and every
temperature verdict in phases 4 and 6 is undecidable there. The suite refuses to
guess: it reads `rooms`' own `uses_outdoor_temp` / `indoors` / `doorway` fields
and exits 2 with this paragraph if it cannot find one.

### Phase 6 — the cooler that actually freezes

This is session 18's own criterion ("cold that is checked") and it is the half a
bench does rather than a suite. `templates/freezer-kitchen.ir.json` is the
fixture; `accept/runs/s18-20260901/` is the run that found the gap.

1. **A cooler in a wall.** `Cooler` is `passability: Impassable` with
   `building.canPlaceOverWall: true` — it is meant to sit *in* the wall between
   the cold room and somewhere else.
2. **Both of its cells passable.** `Building_Cooler.TickRare` moves no heat at
   all unless `!intVec2.Impassable(Map) && !intVec.Impassable(Map)` — the
   south-rotated (cold) cell **and** the north-rotated (hot) cell. A cooler
   walled in on its exhaust side draws idle power forever and cools nothing.
   `temp-control` publishes `cold_side_blocked` and `hot_side_blocked` so this
   is visible rather than mysterious.
3. **Power, and a net that has formed.** The freezer template's own lesson is
   "400 W that has to come from somewhere". Two notes the orchestrator has paid
   for before:
   - **A solar generator stops at sunset.** `SolarGenerator`'s output follows
     the light level, so a phase-6 run started in the evening measures a cooler
     that is off. Use a wood-fired generator with fuel, or a battery bank with
     charge, or start in the morning.
   - **A newly wired net needs one advance to form.** The power net is rebuilt
     on the game's own cadence, not on the tick the last conduit is built, so
     `digest.power.draw_w` reads 0 immediately after wiring. Advance once, then
     read. This is the same "comms-console-style power net" note the orchestrator
     carries for `comms-call`.
4. **Read `temp-control` BEFORE assuming the fixture is good.** Every one of the
   four failure modes above has its own published field —
   `powered`, `effective`, `cold_side_blocked`, `hot_side_blocked`,
   `serves.uses_outdoor_temp`, `broken_down`, `switch_on` — plus an `advisory`
   sentence naming the fix. Phase 6's precondition prints all of them.

### What the suite leaves behind

It does **not** clean up. Phases 1–5 may spawn one `Heater` (left at the vanilla
21 C default) and 20 `MealSimple` (left where they fell), and phase 5
deliberately advances until food has measurably rotted. Run it on a bench you
are willing to dirty.

## What each phase proves, against the issue's Acceptance section

| Acceptance bullet | Phase | How | Verdict |
|---|---|---|---|
| A verb reads, for every temperature-controlled building: def, position, current target, the def's min/max clamp, whether it is powered, and the room it serves. Reading mutates nothing. | 1 | every field asserted present by `has_key` (checks 1.1–1.20); `serves.room_id` present and `serves_basis` naming the game's own `TickRare`; **two consecutive reads compared field for field** (1.24) — a measurement, not a claim | **covered** |
| Setting a target below the def's `minTemperature` is a REFUSAL naming the clamp and the value | 3 | **NOT covered as written, and deliberately: the issue's premise is false.** 3.13–3.17 assert the OPPOSITE — the target is accepted, set, flagged `outside_def_range`, and carries an advisory naming `minTargetTemperature`. See "the refusal that isn't" below. The refusal that IS real (the game's own −273.15…1000 clamp) is 3.10–3.12. | **resolved against the issue** |
| On the session-18 fixture: set the two coolers to a freezing target, advance, `room --id <freezer>` below zero while `draw_w` rises off its idle floor | 6 | opt-in; needs the freezer fixture above | **orchestrator-manual** |
| A cooler with no power refuses or reports `powered: false` rather than accepting a target that cannot be honoured | 1, 6 | REPORTS. `powered` is on every row (1.12) with an advisory citing `Building_Cooler.TickRare`'s `if (!compPowerTrader.PowerOn) return;`. It does not refuse, because `CompTempControl.CompGetGizmosExtra` has no power clause and a player can set an unpowered cooler's target. | **covered (as "reports", the branch the bullet allows)** |
| Journaled like any other player verb, with the before/after target | 2 | 2.16–2.17 assert a real `journal_seq`; 2.24–2.25 read the journal back and assert the `action` row carries `payload.targets[*].before_c`/`after_c` | **covered** |
| *(from the round brief)* the digest shows an out-of-range room | 4 | deterministic and instant, no power and no advance: drive the target 200 C past the room, `temperature.ok` goes false, `out_of_range` names the room id, `drift_c` equals temp − target, and putting the target back clears it | **covered** |
| *(from the round brief)* a building with no `CompTempControl` is refused by name | 3 | `rejects_by_reason.no-temp-control == 1`, the rejection carries the building's `id`, and its `reason` cites `CompTempControl`. A **Vent** is preferred as the subject and gets two extra checks (3.7, 3.8) naming `flick` and `Building_Vent.TickRare`. On a map with no Vent those two are a NOTE, not a pass. | **covered; the Vent-specific half is bench-dependent** |
| *(from the round brief)* the rot half — a warm room's food is visibly deteriorating where before it was not | 5 | `soonest_rot_days` falls and `worst_rot_pct` rises across an advance **while `food_days` does not move** (5.8–5.10). The advance is bounded by a predicate over the new field itself, which is simultaneously the proof that `resources.food_rot.*` is predicate-addressable (5.5). | **covered** |
| *(from the round brief)* say which predicate-cost class the new section is in | 0, 4 | phase 4's check 4.24 arms `until:{condition:{path:"temperature.ok"}}` — if `temperature` were absent from `DigestVerb.PredicateSections` this is a `bad-args` naming the section list, which is exactly how that omission would present | **covered** |

## The refusal that isn't, and why

The issue's second acceptance bullet asks for a refusal when a target is set
below "the def's `minTemperature`". Checked against the 1.6 tree rather than
assumed, three things in that sentence are wrong:

1. **There is no slider.** `CompTempControl.CompGetGizmosExtra` yields five
   `Command_Action` buttons: −10, −1, "reset to 21", +1, +10.
2. **The fields are named differently** —
   `CompProperties_TempControl.minTargetTemperature` (−50) and
   `.maxTargetTemperature` (50).
3. **Nothing in the tree reads either one.** Grepped unpiped over the whole
   decompiled source: the only two hits are the declarations. The *only* clamp
   in the game is `InterfaceChangeTargetTemperature`'s
   `Mathf.Clamp(TargetTemperature, -273.15f, 1000f)`.

So a player walks a cooler's target to −273 with the −10 button and the game
does not stop them. A verb that refused at −50 would refuse something a player
can do — DESIGN's Action model broken in the other direction, and the same class
of error as bypassing a gate. What ships instead: the def range is published on
every row as `def_min_c` / `def_max_c` with `def_clamp_enforced: false`, a target
outside it sets `outside_def_range: true` and produces an `advisory` saying why
it is not a refusal, and the **real** clamp is a `bad-args` refusal citing the
member it lives in. Recorded in DESIGN's decisions log and commented on the
issue.

## The vent

261f2e9 asks, as a "worth checking", whether `Building_Vent` belongs in the same
verb or is a `flick` case. **It is a `flick` case, from the def rather than from
judgement.** Core's `Buildings_Temperature.xml` gives `Vent` exactly one comp,
`CompProperties_Flickable`, and no `CompProperties_TempControl` — so
`Building_Vent.compTempControl` is null, the temperature gizmos never exist for
it, and `Building_Vent.TickRare` reads no target at all
(`GenTemperature.EqualizeTemperaturesThroughBuilding(this, 14f, twoWay: true)`).
`temp-set` refuses it by name with that reason and points at `flick`.

## Why `food_days` was not redefined

It is a shipped predicate target, `accept/` suites assert on it, and "what the
vanilla alert will do" is a real question with a real answer. Redefining it
would break every consumer in the direction that looks fine. What shipped:

- `resources.food_days_basis` — a sentence **in the data** saying it is
  stockpile-only, fresh-only, and has no rot term. (`Materials.cs` exists
  because a true warning in a source comment did not stop the code three lines
  below it from drawing a conclusion the agent could not check: git-bug
  `54b0c9a`. Same fix, on the field the agent reads.)
- `resources.food_rot` — map-wide, with the three bands
  `CompRottable.CompInspectStringExtra` itself uses, `spoiled_stacks` (which
  `ResourceCounter.ShouldCount` silently drops from `food_days`), a
  `soonest_rot_days` deadline, and its own `days` — the same division over
  map-wide fresh nutrition.

`Materials.Of` is deliberately **not** used: its own header says "no predicate
and no digest section reaches this file", it pathfinds per stack per builder, and
`resources` is evaluated once per predicate cadence window. The honest
consequence is published rather than hidden — `food_rot.nutrition` is an UPPER
bound (no reachability tested), `nutrition_in_stockpiles` is the LOWER bound, and
`nutrition_forbidden` narrows the gap for free.

## Known gaps in this suite

- **Nothing here has met a bench.** Every check is a plan.
- **Phase 6 is the only proof that a cooler cools**, and it is opt-in and
  fixture-dependent. Phases 1–5 prove the target is set, read back, refused,
  advertised and watched — not that the physics follows.
- **The Vent-specific refusal text (3.7, 3.8) is skipped on a map with no Vent**,
  and the suite says so as a NOTE rather than passing a check it did not earn.
- **The exhaust-only refusal (1.28–1.30) needs a cooler in a wall between two
  sealed rooms**, and is a NOTE otherwise. It guards a real bug found in audit:
  `temp-set {room:<kitchen>}` used to retarget every cooler that merely
  *exhausts* into the kitchen — i.e. the freezer's cooler — silently thawing the
  freezer. The read still lists it (the kitchen's temperature really is affected
  by it); only the actuator refuses. **This is the highest-value uncovered check
  in the file on a bench without that geometry.**
- **A cooler whose exhaust vents into the same room it cools** is a real and
  common mistake that this suite does not stage. `temp-control` publishes
  `serves.room_id` and `exhaust.room_id` so the two can be compared, but no
  check compares them.
- **`food_rot`'s 5000-stack ceiling is untested.** When it bites, `ok` goes false
  with `truncated: true` — an alarm that fails loud, because the scan produces an
  aggregate and cannot order by importance before it cuts. No fixture here
  reaches 5000 food stacks.
- **`digest.temperature.ok` uses a 2 °C tolerance that is OURS**, not the game's.
  It is published as `tolerance_c` on every read and check 0.19 pins the suite's
  expectation to the published value rather than to a literal, but the *choice*
  of 2 °C has never been measured against a real freezer's oscillation.
