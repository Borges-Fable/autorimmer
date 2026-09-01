# git-bug 8b0b88f acceptance — a redundant designate order is no longer an impossible one

Run by the ORCHESTRATOR against the live `_RimWorld-Agent` bench. The worker
wrote this and the driver beside it and never launched the game.

There is a RUNNABLE driver: **`accept/8b0b88f-already-designated.py`**. It is
the primary artifact and this file is its reading. Everything below is one
protocol envelope — write it to `commands/<id>.json`, read `results/<id>.json`,
or use the `rwa` line beside it. The driver speaks the same file protocol
directly and needs no `rwa` on the box.

```
./accept/8b0b88f-already-designated.py                # phases 0-5, ~123 checks
./accept/8b0b88f-already-designated.py --phase 3      # one phase (0 always runs)
./accept/8b0b88f-already-designated.py --dry-run      # print the plan, send nothing
./accept/8b0b88f-already-designated.py --stage-animal # arm phase 5 with a spawned Hare
```

**Preconditions.** A loaded colony on `_RimWorld-Agent`, **PAUSED**; at least
one spawned colonist; exposed mineable rock within 70 tiles of a colonist; at
least three undesignated plants; one ordinary haulable item on the ground. For
phase 5, a wild animal on the map — or `devMode` on and `--stage-animal`, which
spawns one `Hare` and destroys it in teardown. Phase 0 discovers every one of
those and aborts with **exit 2** naming the missing one; a fixture gap is not a
spec failure and the driver says so in those words.

**Paused is a fixture condition, not a nicety.** Every phase designates a target
and then re-designates it. On a running bench a colonist can walk over and
finish the mining or plant-cutting job between the two calls; the game drops the
designation when the job completes; the second call is then correctly ACCEPTED.
On the console that reads exactly like "the fix is not in the assembly". Phase
0.1c makes it a precondition for that reason.

**What is being asserted.** Four claims, four places:

| claim | proved by |
|---|---|
| an already-designated CELL rejects as `already-designated`, not `not-designatable` | 1.2d |
| an already-designated THING does the same, through the other accessor | 2.2d |
| the Cell/Thing dispatch is right in all four quadrants and logs NO RED ERROR | 3.1–3.5 |
| the two keys are genuinely distinct — one call, one verb, both keys | 4.1c, 4.1e |
| Hunt's wrong `reason` is kept verbatim under a correct `why` | 5.3a, 5.3b |

**Standing assertions on every step**: `ok:true`, and no `red_error` in the
journal — checked twice, once inside phase 3 over a window containing only the
four dispatch calls, and once at the end over the whole run.

---

## The defect, in the game's own words

Every gate in the designate table refuses an already-designated target with an
EMPTY report:

- `RimWorld/Designator_Mine.CanDesignateCell` and `.CanDesignateThing` return
  `AcceptanceReport.WasRejected` — which is `new AcceptanceReport("")`, reason
  the empty string.
- `RimWorld/Designator_MineVein` does the same with its own def.
- `RimWorld/Designator_Plants.CanDesignateThing` (the base of `_PlantsCut`,
  `_PlantsHarvest`, `_PlantsHarvestWood`) returns a bare `false`.
- `RimWorld/Designator_Hunt.CanDesignateThing` returns a bare `false`.

`DesignateEngine.ReasonOf` turns `""` into `null` — correctly; it refuses to
invent words the game did not say. So all of it used to arrive as
`{why:"not-designatable", reason:null}`, which is byte-identical to the envelope
for "this rock is not mineable" and "this animal is not huntable". Those two
call for OPPOSITE corrections: one says stop asking, the other says aim
somewhere else.

`DesignateEngine.AlreadyDesignated` now asks the designation manager on the
REJECT PATH ONLY, and `DesignateEngine.RunCells` / `.RunThings` re-key the
rejection to `already-designated`. The game's gate stays the sole authority on
what may be designated; the accept path is untouched; and
`rejects_by_reason.already-designated` becomes a free per-call count of wasted
orders.

---

## A — phase 0: the bench, the fixtures, and the shape contract

### A0. The journal watermark (discovery)
```json
{"id":"a0","op":"journal","args":{"since_seq":999999999,"limit":1}}
```
```
rwa journal --since_seq 999999999 --limit 1
```
Read `data.last_seq` -> **`<SEQ0>`**. The red-error checks read forward from it,
so a red error that predates this run is not charged to it.

> `{"limit":1}` alone is the WRONG envelope and it silently ruins the standing
> check — `JournalVerbs.Read` updates `last_seq` before the `since_seq` skip and
> breaks on `events.Count >= limit` before appending, so it stops at the SECOND
> line and reports that line's seq. See `accept/1eb2262-stable-pawn-order.md` A0.

### A0a. The anchor and the map
```json
{"id":"a0a","op":"pawns","args":{"filter":"colonist"}}
{"id":"a0b","op":"map-dump","args":{"rect":[0,0,1,1]}}
```
```
rwa pawns --filter colonist
rwa map-dump --rect:json '[0,0,1,1]'
```
Take `data.list[0].at` -> **`<ANCHOR>`**. It is two fixtures at once: the centre
of the mineable search, and phase 4.2's known-not-mineable control cell — a
colonist is standing on it, so it is unfogged, standable and certainly not a
rock face.

### A0c. Find a mineable cell — the verb is its own oracle
```json
{"id":"a0c","op":"designate","args":{"type":"mine","rect":[<AX-70>,<AZ-70>,141,141],"max_cells":20000,"dry_run":true}}
```
```
rwa designate --type mine --rect:json '[<AX-70>,<AZ-70>,141,141]' --max_cells 20000 --dry_run
```
`data.cells` is the accepted set -> take the first as **`<CELL>`**.

A dry-run `designate mine` IS the oracle for "which cells would the game
accept": `DesignateEngine.RunCells` runs `Designator_Mine.CanDesignateCell` per
cell and, under `dry_run`, never calls `DesignateSingleCell`. The accepted set
therefore excludes non-mineable cells AND already-designated ones — the gate's
first test after bounds is `DesignationAt(c, Designation) != null` — which is
exactly the fixture wanted: a cell carrying no `Mine` designation yet. The
driver rings out at half-widths 10, 35 and 70; 141x141 is 19881 cells, just
under `DesignateEngine.MaxCellsCeiling`.

### A0d. THE SHAPE CONTRACT

The same envelope proves every key the later phases dig on:

`data.verb` · `type` · `designator` · `gate` · `designation` · `targeted` ·
`requested` · `capped` · `target_scope` · `accepted` · `dry_run` · `cells` ·
`cells_more` · `rejected` · `rejects` · `rejects_more` · `rejects_by_reason` ·
`designations_before` · `designations_now` · `crop` · `action` ·
`action.journal_seq`, then `rejects[0].at` · `.why` · `.reason` · `.removal`,
and on the thing route `rejects[0].id` · `.def` · `.label`.

**Why this section is long, and why it opens the driver rather than closing it.**
`eq(..., None)` cannot tell an ABSENT key from one that is present and null:
`dig()` returns `None` for both, so a wrong dig path does not fail, it goes
GREEN WHILE ASSERTING NOTHING. This suite is the worst possible case for that,
because THE DEFECT UNDER TEST IS "a reject whose `reason` is null" — half the
assertions here are about a null. The driver uses a `has_key`-based `shape()`
predicate that is distinct from `eq()`, plus a `null_at()` that asserts PRESENT
and NULL as two facts on one line, plus an `eq_int()` for the two checks that
compare against a number read from an earlier envelope (where a vanished key
would make both sides `None` and pass).

Three specific traps this contract catches:

1. **`data.action.journal_seq` is PRESENT AND NULL on a dry run** —
   `DesignationVerbs.NoAction` publishes it deliberately, because "not
   applicable" and "zero" must not read alike. `eq(..., None)` would pass on a
   serializer that dropped it.
2. **TWO SPELLINGS OF THE TALLY SHIP, both correct.**
   `data.rejects_by_reason` on the data block
   (`DesignateEngine.PublishRejects`) and `data.action.rejected_by_reason`
   inside the journalled action payload (`DesignationVerbs.Designate`). A driver
   that digs the wrong one gets `None` and passes. 1.2l/1.2m prove the second
   exists and agrees with the first.
3. **`data.crop` can legitimately be null** (`DesignateEngine.Echo` returns null
   when the crop rect misses the map or the renderer throws), so it is a
   PRESENCE check and must never be an `eq`.

### A0e. The remaining fixtures
```json
{"id":"a0e","op":"things","args":{"category":"all","in":"rect","rect":[<CX>,<CZ>,1,1],"detail":true,"detail_cap":300,"by_location":false}}
{"id":"a0f","op":"things","args":{"category":"plants","detail":true,"detail_cap":300,"by_location":false}}
{"id":"a0g","op":"things","args":{"category":"haulable","detail":true,"detail_cap":300,"by_location":false}}
```
- **`<ROCK>`** — the mineable THING at `<CELL>`, for phase 3's Q2.
  `category:"all"` is `ThingRequestGroup.Everything`, which does include a
  `Mineable` (`Verse/ListerThings.EverListable` excludes only motes and, in
  region listers, projectiles). Scoped to the one cell, then sieved by the verb
  itself: whichever id a dry-run `designate mine --things` accepts is the rock.
- **`<PLANTS>`** — three plants a dry-run `designate cut --things` accepts.
  Three, not one: phase 4 asserts a TALLY, and a tally of one is
  indistinguishable from a boolean.
- **`<CONTROL>`** — a haulable that `designate cut` refuses with
  `{why:"not-designatable", reason:null, removal:"none"}`. The `removal:"none"`
  filter is load-bearing: `DesignateEngine.RejectOut` OVERWRITES a null `reason`
  with `Blockers.Classify`'s own when the target is a building the taxonomy has
  words for, and phase 4.1e would then be comparing a null against a sentence
  and would fail for a fixture reason.

`cut` and not `chop` for the thing side: `Designator_PlantsCut.CanDesignateThing`
accepts ANY plant once `isOrder` is set (`DesignationVerbs.Designate` sets it
before the first gate call), while `Designator_PlantsHarvestWood` demands a
harvestable tree.

---

## B — phase 1: the Cell-targeted def on the cell route

`Mine` is `targetType Cell` (`Core/Defs/Misc/Designations/Designations.xml`), so
`DesignateEngine.AlreadyDesignated` takes its `TargetType.Cell` branch with
`thing == null` and asks `DesignationManager.DesignationAt(cell, def)`.

### B1. The first order lands
```json
{"id":"b1","op":"designate","args":{"type":"mine","cells":["<CELL>"]}}
```
```
rwa designate --type mine --cells:json '["<CELL>"]'
```
Expect `data.accepted` == 1, `data.rejected` == 0, `data.rejects_by_reason` ==
`{}`, `data.action.journal_seq` >= 1, and
`data.designations_now == data.designations_before + 1`.

Those last two are the independent witness. `designations_before` /
`designations_now` are the count of that def STANDING ON THE MAP
(`DesignateEngine.CountOf` over `SpawnedDesignationsOfDef`), not "how many we
added" — a designator that merges, replaces or flood-fills moves the number by
something other than `accepted`, and that is the truth.

### B2. The same order again — THE FIX
```json
{"id":"b2","op":"designate","args":{"type":"mine","cells":["<CELL>"]}}
```
Expect:

- `ok` still `true`. **A redundancy is not an error.** The envelope succeeds and
  the per-target reject carries the news.
- `data.accepted` == 0, `data.rejected` == 1.
- `data.rejects[0].why` == **`"already-designated"`** — this is the whole issue.
  Before the fix it was `"not-designatable"`.
- `data.rejects[0].reason` **present and null**. `AcceptanceReport.WasRejected`
  carries reason `""`; `DesignateEngine.ReasonOf` refuses to promote that to a
  sentence.
- `data.rejects[0].at` == `<CELL>`.
- `data.rejects_by_reason` == `{"already-designated": 1}` — asserted as a WHOLE
  dict, so a second key cannot hide behind the first, and `not-designatable`
  asserted ABSENT rather than zero (the tally is built from the rejects actually
  seen, so "absent" is the true claim and `== 0` would be a false one).
- `data.designations_now` == `data.designations_before` == B1's value. The
  redundant order added nothing.
- `data.action.rejected_by_reason` == the same dict, under the other spelling.

---

## C — phase 2: the Thing-targeted def on the thing route

`CutPlant` is `targetType Thing`, so `AlreadyDesignated` takes its
`TargetType.Thing` branch with a non-null thing and asks
`DesignationManager.DesignationOn(thing, def)`.

### C1. Designate one plant
```json
{"id":"c1","op":"designate","args":{"type":"cut","things":[<PLANT>]}}
```
Expect `data.accepted` == 1 and `data.ids` == `[<PLANT>]`.

### C2. The same order again
```json
{"id":"c2","op":"designate","args":{"type":"cut","things":[<PLANT>]}}
```
Expect `data.rejects[0].why` == `"already-designated"`, `.id` == `<PLANT>`, and
`.reason` present and null — `Designator_Plants.CanDesignateThing` returns a
bare `false`, not even the empty `AcceptanceReport` Mine gives, so `why` carries
the entire distinction on this route.

---

## D — phase 3: all four dispatch quadrants, and NO RED ERROR

**This phase is why the driver exists.**

`Verse/DesignationManager.DesignationOn(Thing, DesignationDef)` `Log.Error`s
*"Designations of type X are indexed by location only and you are trying to get
one on a Thing"*; `Verse/DesignationManager.DesignationAt(IntVec3,
DesignationDef)` `Log.Error`s the mirror image. Neither returns a silent null. A
red error breaches the zero-red-errors invariant, so the WRONG check here would
have been worse than no check.

The table is not uniform. `Mine` and `MineVein` are `targetType Cell`; `Hunt`,
`CutPlant`, `HarvestPlant`, `Haul` and `Flick` are `targetType Thing`. A
per-verb accessor is a red error waiting for the wrong verb.
`DesignateEngine.AlreadyDesignated` dispatches on `DesignationDef.targetType` —
the game's OWN discriminator, the one `DesignationManager.AddDesignation` and
`IndexDesignation` switch on — which makes a swapped pair UNREPRESENTABLE rather
than merely untested. This phase drives every quadrant of that dispatch so a
regression that reintroduces a per-verb accessor fails loudly instead of
scrolling past in red.

Open a fresh journal watermark **`<SEQ3>`** first, so the red-error read below is
charged to these four calls and to nothing before them.

| # | call | branch taken | accessor |
|---|---|---|---|
| Q1 | `designate mine --cells <CELL>` | `TargetType.Cell`, thing null | `DesignationAt(cell, Mine)` |
| Q2 | `designate mine --things <ROCK>` | `TargetType.Cell`, thing given | `DesignationAt(thing.Position, Mine)` |
| Q3 | `designate cut --things <PLANT>` | `TargetType.Thing`, thing given | `DesignationOn(thing, CutPlant)` |
| Q4 | `designate cut --rect [<PX>,<PZ>,1,1]` | `TargetType.Thing`, thing null | `GetThingList` walk, `DesignationOn` each |

```json
{"id":"d1","op":"designate","args":{"type":"mine","cells":["<CELL>"]}}
{"id":"d2","op":"designate","args":{"type":"mine","things":[<ROCK>]}}
{"id":"d3","op":"designate","args":{"type":"cut","things":[<PLANT>]}}
{"id":"d4","op":"designate","args":{"type":"cut","rect":[<PX>,<PZ>,1,1]}}
```

All four expect `data.rejects[0].why` == `"already-designated"`. Q2 additionally
expects `.id` == `<ROCK>`, `.reason` present and null, and `.removal` == `"mine"`
— `Blockers.Classify` still says the rock clears by mining, so the removal
taxonomy is untouched by the re-key.

**Q2 and Q4 are the quadrants a naive fix gets wrong**, and neither is a corner
case. Q2 is `designate mine --things` on a rock the agent addressed by id; Q4 is
`designate cut --rect` over an already-marked patch, which is the most ordinary
call in the whole verb. Both are the case where the def's `targetType` and the
caller's target shape disagree, and both would be a red error under a per-verb
accessor.

### D5. The point of the phase
```json
{"id":"d5","op":"journal","args":{"since_seq":<SEQ3>,"types":["red_error"],"limit":50}}
```
```
rwa journal --since_seq <SEQ3> --types red_error
```
Expect `data.count` == 0. A swapped pair lands here.

---

## E — phase 4: the two keys are distinct

### E1. One call, one verb, TWO kinds of no — THE ISSUE IN ONE ASSERTION
```json
{"id":"e1","op":"designate","args":{"type":"cut","things":[<P1>,<P2>,<P3>,<CONTROL>]}}
```
```
rwa designate --type cut --things:json '[<P1>,<P2>,<P3>,<CONTROL>]'
```
Expect `data.rejected` == 4, `data.accepted` == 0, and

```json
"rejects_by_reason": {"already-designated": 3, "not-designatable": 1}
```

**Before the fix this answered `{"not-designatable": 4}`** and the agent had no
way to tell three wasted orders from one impossible one. The tally is now a free
per-call count of redundancy — the same ledger goal 4087644's comment #1 argues
for on the job side.

### E1e. And the two rows are otherwise IDENTICAL

Take the `already-designated` row and the `not-designatable` row out of
`data.rejects` and compare them field by field. Expect: `reason` null on BOTH,
`removal` equal on both, the same key set on both, and `why` the only thing that
differs.

That is the argument for why this had to be a KEY and not a sentence in
`reason`. There was no sentence to add — the game gave none, and inventing one
is the thing `DesignateEngine`'s REJECTIONS contract forbids.

### E2. A genuinely impossible order still carries the game's own words
```json
{"id":"e2","op":"designate","args":{"type":"mine","cells":["<ANCHOR>"],"dry_run":true}}
```
Expect `data.rejects[0].why` == `"not-designatable"`, `.reason` a NON-EMPTY
string (`Designator_Mine.CanDesignateCell` answers
`"MessageMustDesignateMineable".Translate()` for a cell with no `Mineable` in
it), `already-designated` ABSENT from the tally, and the tally ==
`{"not-designatable": 1}`.

> **Sent as a dry run on purpose, and not for tidiness.**
> `Designator_Mine.CanDesignateCell` returns TRUE for a FOGGED cell — it tests
> `c.Fogged(map)` *before* it looks for a `Mineable` — so a live call aimed at a
> cell the suite believes is ordinary floor would, if that belief were ever
> wrong, actually paint a Mine designation on it. The classification runs on the
> reject path, which `dry_run` does not skip, so the dry run proves exactly the
> same thing and can mutate nothing.

### E3. A dry run classifies identically
Repeat E1 with `dry_run:true`. Expect the same split, `data.dry_run` == true,
and `data.action.journal_seq` present and **null**. An agent can therefore count
its own wasted orders BEFORE spending them.

---

## F — phase 5: Hunt's wrong reason, kept verbatim under a correct `why`

`RimWorld/Designator_Hunt.CanDesignateCell` answers
`"MessageMustDesignateHuntable"` when the true cause is already-designated,
because its `HuntablesInCell` filters through `CanDesignateThing`, which drops
animals that already carry the `Hunt` designation — so a cell whose animals are
all marked looks to it like a cell with no huntables in it.

**The merged code does NOT correct that string, and this phase asserts that it
does not.** `DesignateEngine.RunCells` carries a KNOWN MISATTRIBUTION comment at
the call site saying so: `why` becomes the correct classification, and `reason`
is kept verbatim on the stated ground that a reason we deleted or invented would
be worse than the game's own inaccurate one. "Documented at the call site" is
only true if the envelope behaves the way the comment says, so:

```json
{"id":"f1","op":"designate","args":{"type":"hunt","things":[<ANIMAL>]}}
{"id":"f2","op":"designate","args":{"type":"hunt","things":[<ANIMAL>]}}
{"id":"f3","op":"designate","args":{"type":"hunt","rect":[<AX>,<AZ>,1,1]}}
{"id":"f4","op":"designate","args":{"type":"hunt","rect":[<AX>,<AZ>,1,1]}}
```

- **f1** — `data.accepted` == 1, `data.gate` ==
  `"RimWorld/Designator_Hunt.CanDesignateThing"`, `data.designation` == `"Hunt"`.
- **f2** (thing route) — `why` == `"already-designated"`, `reason` present and
  **null**: `CanDesignateThing` returns a bare `false`.
- **f3** is not an assertion. It marks any OTHER huntable sharing the cell — a
  herd tile would otherwise leave one unmarked and make f4 accept.
- **f4** (cell route) — **`why` == `"already-designated"` AND `reason` a
  NON-EMPTY string.** The classification is right and the sentence is the game's
  own wrong one, kept rather than deleted. `data.rejects_by_reason` ==
  `{"already-designated": 1}`: the tally keys on `why`, so the wrong sentence
  costs the ledger nothing.

`<ANIMAL>` is a wild animal that a dry-run `designate hunt --things` accepts.
`Designator_Hunt.CanDesignateThing` requires `pawn.Faction == null ||
!pawn.Faction.def.humanlikeFaction`, so a TAME colony animal is not huntable and
`pawns {filter:"wildlife"}` is the right roster. With no wildlife on the map,
`--stage-animal` spawns one `Hare` (Core; its `PawnKindDef` has no
`defaultFactionDef`, so `FactionUtility.DefaultFactionFrom(null)` gives it a
null faction) and `dev:destroy`s it in teardown. Without the flag the phase
soft-skips and says out loud that the Hunt misattribution is then UNPROVEN and
is part of this issue's acceptance.

---

## G — standing check

```json
{"id":"g1","op":"journal","args":{"since_seq":<SEQ0>,"types":["red_error"],"limit":50}}
```
Expect `data.count` == 0. Every `designate` in the run passed through
`DesignateEngine.AlreadyDesignated`, and the whole hazard is that the wrong
accessor `Log.Error`s instead of returning null.

---

## Teardown

`designate cancel` over every fixture CELL, plus `dev:destroy` on a staged
animal. The driver runs it on the success path, on a failed check, and on a
precondition abort.

```json
{"id":"z1","op":"designate","args":{"type":"cancel","cells":["<CELL>","<P1 cell>","<P2 cell>","<P3 cell>","<ANIMAL cell>"]}}
```

Cancel by CELLS is the universal route: `Designator_Cancel.CanDesignateCell`
clears the cell's own designations AND walks `GetThingList` clearing the things
standing in it, so one call retires a `Mine` (indexed by cell) and a `CutPlant`
or `Hunt` (indexed by thing) alike.

It therefore also clears any OTHER cancelable designation the player had on
those exact cells, and destroys player blueprints and frames there
(`Designator_Cancel.DesignateSingleCell`). Phase 0 only ever picks cells whose
dry-run designate was ACCEPTED, and the game's gates reject an already-designated
target, so a cell this suite touches carried no designation of that def before
the run — but a blueprint on a plant's cell would be collateral. On a bench
fixture that is acceptable; on a colony you care about, read the fixture line
the driver prints before letting it finish.

---

## What this does NOT prove

- **Nothing here was executed by the worker.** The bench belongs to the
  orchestrator. Every expectation is derived from `Source/AutoRimmer/
  DesignateEngine.cs`, `DesignationVerbs.cs` and `Blockers.cs`, and from the
  decompiled 1.6 source (`RimWorld/Designator_Mine.cs`, `Designator_Hunt.cs`,
  `Designator_Plants.cs`, `Designator_PlantsCut.cs`, `Designator_Cancel.cs`,
  `Verse/DesignationManager.cs`, `Verse/ListerThings.cs`,
  `Core/Defs/Misc/Designations/Designations.xml`). The first real evidence is
  this run.
- **A `--dry-run` proves the plan and never the paths.** It sends nothing, so
  every envelope is empty, every shape check is skipped and every dig path looks
  fine. The driver reports the count as expectations PRINTED, in yellow, and
  says nothing was sent — see commit `61e3fc1`, which fixed exactly the opposite
  behaviour in five drivers.
- **The MINE-VEIN residual is deliberately untested**, because it is not the
  fixed behaviour. `designate mine` over a cell carrying a `MineVein`
  designation still reports `not-designatable`, because
  `Designator_Mine.CanDesignateThing` rejects on `DesignationAt(t.Position,
  DesignationDefOf.MineVein)` — a def that is not this entry's. Telling that
  apart means re-implementing the widget's second clause, which the gate rule
  forbids doing blind. Recorded in `DesignationVerbs.Designate`; if a future
  round takes it on, the check belongs in this file.
- **Only two of the ~30 designate types are driven.** `mine` for the Cell side
  and `cut` for the Thing side, chosen because they exist on any colony and
  because both gates refuse with an empty report. The dispatch is on
  `def.targetType` and not on the verb, so a third type adds coverage of the
  TABLE, not of the mechanism — but a table edit that repoints `mine` or `cut`
  at a different designator is caught by 0.5w/0.5x and 0.9c/0.9d, which assert
  the designator class and the designation def by name.
- **`designate mine --rect` over a MineVein-flooded face is not exercised**, and
  neither is `MineVein` itself. `mine-vein` is the other `targetType Cell` entry
  and would exercise the same branch as Q1/Q2.
- **Nothing here proves the accept path is unchanged**, beyond B1 and C1
  answering `accepted:1`. The check runs only after the game's gate has said no,
  so there is nothing on the accept path to observe; the argument for that is
  the code's shape, not a measurement.
