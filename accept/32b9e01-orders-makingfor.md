# Acceptance — `orders` mutated while claiming read-only (git-bug `32b9e01`)

The worker that wrote this fix may never launch RimWorld, so this is the
acceptance in executable form: envelopes and expectations, hand-drivable, `rwa`
-drivable, or drivable by the raw file protocol
(`commands/<id>.json` → `results/<id>.json`) on a box with no python.

Every envelope below is `rwa <op> --args-json '<json>'` where python exists.

## What is being proved

| # | claim | phase |
|---|---|---|
| 1 | Before the fix, one `orders` call MOVES a bill's `nextTickToSearchForIngredients` forward by 500–600 | **R** (instrumented build) |
| 2 | After the fix, the same call does not move it at all | **F** |
| 3 | After the fix, an ingredient-starved bill comes back with the game's own `MissingMaterials` reason where before it produced NO entry at all | **M** (runs against the SHIPPED `main` binary — no instrumentation) |
| 4 | Nothing red, and the disclosure is in the envelope | **F** |
| 5 | The disclosure is not a hedge: one `orders` call flips a bill's SCRIBED `paused` flag, before and after the fix alike | **D** |

**Phase M is the cheap one and it needs no special build.** If you only run one
phase, run M: it is a straight before/after against `main`'s assembly and this
branch's, and it is the consequence the agent actually feels.

---

## Read this first: the field this needs did not exist

`nextTickToSearchForIngredients` is a plain public field on `RimWorld/Bill.cs`
and **nothing in AutoRimmer published it before this branch.** `bills` now emits
it per bill, as `next_ingredient_search_tick` (raw) and
`ingredient_search_cooldown` (`max(0, that − TicksGame)`), so claim 1 and 2 are
observable at all. That is why phase R cannot be run against `main`'s shipped
DLL — the fix and the instrument landed in the same branch — and why phase R
asks for a **deliberately broken build of THIS branch** instead.

The field is worth having on its own account and is not scaffolding: a bill can
report `state:"active"` and be worked by nobody while that tick is in the
future, because `WorkGiver_DoBill.StartOrResumeBillJob` skips it for **every**
pawn until then. It is invisible in the game's own UI.

---

## Fixture

| # | needs | why | stage it with |
|---|---|---|---|
| F1 | `devMode=True` on the bench | `world-fixture` refuses otherwise | bench profile |
| F2 | ≥ 1 visible, **undrafted** colonist | `orders` skips every giver whose `canBeDoneWhileDrafted` is false | any colony |
| F3 | a butcher table with an **unsatisfiable** bill | the write only happens when `TryFindBestBillIngredients` FAILS | `world-fixture` below |
| F4 | **no butcherable corpse or animal within 24 cells of the table** | if the ingredient search SUCCEEDS a job is produced, the giver lands in `available`, and there is nothing to measure | check `things` near the table; move the table or haul the corpse away |
| F5 | the colonist's Cooking skill inside the bill's `allowedSkillRange` | `Bill.PawnAllowedToStartAnew` `continue`s the bill BEFORE the ingredient search, so the write never happens and the phase silently passes for the wrong reason | pass `skill_min:0` to the fixture (below) — do not rely on the default of 4 |
| F6 | the bill not already satisfied | `Bill_Production.ShouldDoNow` false is another silent skip | `target_count:200` |

Stage F3/F5/F6 in one call:

```json
{"op":"world-fixture","args":{"steps":["bench","bill"],"skill_min":0,"target_count":200}}
```

→ `data.bench.id` becomes **BENCH**, `data.bill.expect_bills` is 2 (the second
is suspended and is not part of this test). Take an undrafted colonist id from
`{"op":"pawns","args":{"filter":"colonist"}}` as **A**.

> **Keep the game PAUSED for the whole run.** `TicksGame` must not move between
> the two `bills` reads or the delta is not the delta. `advance` is never called
> in this acceptance.

---

## Phase R — reproduce (instrumented build, expected to FAIL the fix)

Build this branch with the fix disabled. One line, in
`Source/AutoRimmer/PawnOrderVerbs.cs` `ScanWorkGivers`:

```
-            FloatMenuMakerMap.makingFor = pawn;
+            FloatMenuMakerMap.makingFor = prevMakingFor;   // REPRO ONLY — git-bug 32b9e01
```

then `dotnet build -c Release -p:RimWorldManaged=…/_RimWorld-Agent/RimWorldLinux_Data/Managed`
and restart the bench. **Revert the line afterwards; it must never be committed.**

| # | envelope | expected |
|---|---|---|
| R0 | `{"op":"status"}` | `ok:true`, `gameLoaded:true`, `paused:true`, no `forcePause` |
| R1 | `{"op":"digest"}` | note `data.time.tick` as **T0** |
| R2 | `{"op":"bills","args":{"bench":BENCH}}` | `benches[0].bills[0].next_ingredient_search_tick` — note as **N0**. On a fresh fixture this is `0`; any value ≤ T0 works, the test is the DELTA |
| R3 | `{"op":"orders","args":{"pawn":A,"thing":BENCH}}` | `ok:true`. **No entry anywhere in `available` or `blocked` whose `work` is the butcher `DoBills…` giver** — that absence IS defect 4, the missing reason |
| R4 | `{"op":"bills","args":{"bench":BENCH}}` | `next_ingredient_search_tick` — call it **N1**. **`N1 − T0` is in `[500,600]`.** That is `ReCheckFailedBillTicksRange` (`RimWorld/WorkGiver_DoBill.cs`), and it is the bug: a read-only verb wrote simulation state |
| R5 | `{"op":"digest"}` | `time.tick` still **T0** — nothing advanced; the move in R4 was the verb, not the clock |

`N1 − T0` landing anywhere in `[500,600]` is also the RNG evidence: the value is
`Rand.RangeInclusive(500,600)` off the shared stream (`Verse/IntRange.cs`
`RandomInRange`). **Re-running R2–R4 on a fresh fixture bench gives a
DIFFERENT number in that window**, which is the only direct way to see the burn
from outside — there is no verb that reports `Rand`'s position, and there is no
way to prove the burn's *consequences* without a determinism harness this
project does not have. Two fixtures, two different deltas, is the evidence
available.

---

## Phase F — fixed (this branch, unmodified)

Rebuild without the repro edit, restart the bench, **and stage a FRESH fixture
bench** — the one from phase R is still holding its cooldown, and a bill whose
`nextTickToSearchForIngredients` is already in the future is skipped by the
`TicksGame <= …` clause before it can be re-tested. Call the new one **BENCH2**.

| # | envelope | expected |
|---|---|---|
| F.1 | `{"op":"digest"}` | `time.tick` = **T0** |
| F.2 | `{"op":"bills","args":{"bench":BENCH2}}` | `bills[0].next_ingredient_search_tick` = **N0**, `ingredient_search_cooldown` = `0` |
| F.3 | `{"op":"orders","args":{"pawn":A,"thing":BENCH2}}` | `ok:true` |
| F.4 | `{"op":"bills","args":{"bench":BENCH2}}` | **`next_ingredient_search_tick` is IDENTICAL to N0**, `ingredient_search_cooldown` still `0`. The verb wrote nothing |
| F.5 | (F.3's result) | `data.note` does NOT contain the string `Read-only`; it DOES say a bill that can never be completed may be deleted and that a job id is consumed per candidate |
| F.6 | repeat F.3 then F.4 | still identical to N0 — the fix holds across repeated asking, which is how an agent actually uses this verb |
| F.7 | `{"op":"journal","args":{"types":["red_error"]}}` | `data.count:0` — the standing invariant |
| F.8 | `{"op":"journal","args":{"types":["action"],"limit":50}}` | no `action` row for `orders`. It is still not an action; it is now an honestly-priced question |

---

## Phase M — the missing reasons (no instrumentation; `main` vs this branch)

Run M1 on the bench with **`main`'s shipped assembly**, then swap in this
branch's and run M2. Same fixture recipe, a fresh bench for each.

| # | envelope | expected |
|---|---|---|
| M1 | `{"op":"orders","args":{"pawn":A,"thing":BENCH}}` on `main` | **no** entry whose `work` is the butcher `DoBills…` giver, in either list. The player right-clicking that table sees a greyed "Cannot butcher: missing materials"; the agent saw nothing at all |
| M2 | the same envelope on this branch | an entry in **`blocked`** whose `work` is that giver and whose `reason` is the game's own `MissingMaterials` string (English: `Missing materials: …`), carrying the bill label |
| M3 | (M2's result) | `blocked_total` is ≥ M1's by exactly the number of givers that gained a reason |

The same gate governs `RimWorld/WorkGiver_ConstructDeliverResources.cs`
`JobOnThing`: without `makingFor` it `break`s at the FIRST unavailable resource
and emits no reason; with it, it collects every missing def and produces the
"missing 30 steel" string. If the bench has an unaffordable blueprint standing,
`orders` against that blueprint is a second, independent M-phase pair.

---

## Phase D — the disclosure is true, measured (no instrumentation, either build)

Not a before/after: this shows what the corrected header now ADMITS, and it is
the cheapest hard evidence that `orders` was never read-only. The fix does not
change it and is not meant to.

`RimWorld/WorkGiver_DoBill.cs` `ShouldSkip` asks every potential bill giver on
the map for `BillStack.AnyShouldDoNow`, which is `Bill_Production.ShouldDoNow`
per bill, which writes the **scribed** `paused` flag (`RimWorld/
Bill_Production.cs`). `bills` publishes that flag as `paused_stored` and — by
design, WorldSafe Class A — never calls the method that writes it. So `bills`
before and after one `orders` call reads the flag without disturbing it, and any
change is `orders`' doing.

Stage a bench whose bill will PAUSE on evaluation:

```json
{"op":"world-fixture","args":{"steps":["bench","bill"],"skill_min":0,"target_count":1,"unpause_when":0}}
```

| # | envelope | expected |
|---|---|---|
| D1 | `{"op":"bills","args":{"bench":BENCH3}}` | `bills[0].paused_stored:false`, `bills[0].current_count` ≥ 1 — **if `current_count` is 0 or null the colony has none of that product; skip this phase with a NOTE, it is a fixture miss, not a failure** |
| D2 | `{"op":"orders","args":{"pawn":A,"thing":BENCH3}}` | `ok:true` |
| D3 | `{"op":"bills","args":{"bench":BENCH3}}` | **`bills[0].paused_stored:true`** — a scribed field, flipped by a verb that used to call itself read-only, and it stays flipped in the save |

The flip is genuine click-parity: right-clicking that bench as a player does the
same. D3 is the point of the whole documentation half of this change — the note
in F.5 is not a hedge, it is a description of D3.

---

## What this acceptance does NOT prove

- **The RNG burn's downstream effect.** Two fixtures giving two different deltas
  shows the draw happened. Showing that it *desynced* something would need a
  paired-run determinism harness, which does not exist here. The argument that
  it matters is `_mp/DETERMINISM.md` and `WorldSafe`'s Class R, not a reading.
- **The danger-threshold divergence (consequence 3).** `DangerUtility
  .NormalMaxDanger` returning `Deadly` under `makingFor` changes what the
  scanners consider reachable. Constructing a target that is reachable at
  `Deadly` and unreachable at `Some` — a fire or a deadly-toxic region between
  pawn and target — is a real test and a fiddly fixture; it was not built. What
  is proved is that the flag is now set for the whole scan, which is what makes
  the threshold match the menu's.
- **Third-party work givers.** `directOrderable` defaults true, so this verb
  runs 38 mods' `JobOnThing` implementations. Vanilla's own worst case was
  destructive; nothing here audits the rest. That is why the cost is disclosed
  in the verb's own header and result note rather than declared absent.
