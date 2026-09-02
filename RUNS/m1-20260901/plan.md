# m1-20260901 — the run plan

Written at tick 1023 (Spring 6, 5500, hour 6), game paused, before any mutating
verb. The contract is git-bug `664e9b9` comment #8 as amended by comment #9.
Nothing here is a commitment the tables grade; the tables grade the reads.

## 1. The cards as dealt

`Root_Play.SetupForQuickTestPlay` was read rather than assumed: it sets
`ScenarioDefOf.Crashlanded`, `Storyteller(Cassandra, Rough)`, `mapSize 250`,
`ChooseRandomStartingTile`. That is exactly #8's fixture line, so the fixture
is honoured with **zero rerolls** — Evan's "deal with the cards" ruling costs
nothing here.

| | |
|---|---|
| biome | TemperateForest, tile 35914, Flat, rainfall 1307, avg 10.1 °C, swampiness 0.18 |
| clock | Spring, day 6 of season, year 5500, hour 6, outdoor 3 °C |
| storyteller | Cassandra Classic, Rough, untouched |
| colonists | Lacey 310, Wouter 313, Jimmy 323 |
| violence-capable | 2 of 3 — Wouter has Shooting **and** Melee disabled |
| threats | 0 hostiles, `danger: None`. **No hive**, so `hostiles → 0` is a reachable predicate this run (M1's C10 cannot recur) |
| alerts at tick 1023 | NeedWarmClothes (High), NeedColonistBeds (High), NeedResearchBench (Medium) |
| animals | 45 wild incl. grizzly bear, 3 megasloth, 5 alpaca, 4 donkey, 2 boomrat; **Yukiko**, a tame starting pet |
| map furniture | ancient ruins (308 wall cells, 6 sarcophagi, 3 ancient security turrets, 2 mech drop beacons, razor wire, an exostrider wreck) and an Anomaly **void monolith** at (101,168). The ruins are scenery — `AncientSecurityTurret` is *"an ancient, broken security turret… the valuable parts have all been looted or smashed"*, a 1×1 prop with 100 HP, no turret comp and no power (Evan's correction, verified in `Buildings_Ancient_Indoors.xml`). **The monolith is the only real hazard**, and it is observed only |

### Roster

| | Lacey 310 | Wouter 313 | Jimmy 323 |
|---|---|---|---|
| age / traits | 28 F, **Nudist** | 47 M, Staggeringly ugly, Kind | 31 M, Misandrist |
| top skills | Animals 10‼ Crafting 9‼ Intellectual 8! Construction 4! | **Medicine 13!** Social 16‼ Intellectual 16! Crafting 8! | Construction 4‼ Intellectual 7‼ Artistic 6‼ Mining 3! |
| violence | yes (Shooting 1, Melee 2) | **no — both disabled** | yes (Shooting 2, Melee 2) |
| role | crafter, cook, second doctor, hunter | doctor, researcher, grower | builder, miner, sculptor |

`work_coverage.ok` is **false**: Doctor floor 2 on availability, have 1 (Wouter).
`work-cover {work:"Doctor"}` promotes Lacey (Medicine 3) — a mod-side repair, not
a `dev` row, so **the run still expects zero `dev` rows and a G2 watermark of
seq 0.** `dev:set-skill` is only reached if a `still_under` row appears.

`posture.ok` is **false**: `will_seek 0/2`, `area_bound 0/3`, and all three
colonists resolve to `flee` on contact — precisely M1's death posture
([[seek-off-is-a-decision-to-flee]]). Fixed on day 1, before anything can arrive.

## 2. Two corrections to comment #9, measured not assumed

**a. The scenario DOES ship wood and steel.** #9 says "It gives you no wood and
no steel. Go and get them." Measured with `things {detail:true}`: **300 wood in
6 stacks and 1,170 steel in 23 stacks**, all forbidden. The amendment's ruling
survives its own wrong premise — the colony still builds its own shelter, and
chopping and mining are still standing loops, because 300 wood is about two
buildings' worth and the steel is scattered across five clusters, three of them
60–130 cells away. But the claim itself was false and the plan is written
against the measurement.

| resource | total | near the base site | rest |
|---|---|---|---|
| steel | 1,170 / 23 stacks | **204** at (107,135) (108,135) (110,135) (108,132) | 246 at z≈103–109; 412 at z≈63–75; 206 in the NE corner; 102 at z≈22 |
| wood | 300 / 6 stacks | **197** at (98,127) (99,129) (102,132) (98,133) | 55 at (126,100), 48 at (119,102) |
| meals | 57 (50 usable) | 50 at x107–109, z120–122 | 4 stragglers north, 3 across the map |
| medicine | 30 | (108,114) 5, (109,113) 25 | — |
| components | 30 | (107,114) | — |
| silver | 800 | (107,113) 500, (108,113) 300 | — |
| weapons | rifle (109,122), revolver (108,112), knife (107,112) | | |
| flak | helmet (106,113), vest (109,114), pants (107,122) | | |

Renewable supply, which is what actually matters: **3,269 trees**,
**274 compacted-steel cells** at (133,122) — 20 cells east, ~40 steel each,
so ~11,000 steel — and **1,824 stone chunks** (932 sandstone, 892 marble) at
20 blocks each. Materials are not the constraint; hauling distance and the raid
clock are.

**b. Five research projects are already finished, and that changes the whole
build.** `research` reports `finished_count: 7` with `Batteries`, `SolarPanels`,
`Firefoam` and `Hydroponics` all *available*, which can only be true if
`Electricity` is done. The seven are exactly the seven `ClassicStart`-tagged
projects — `Electricity`, `AirConditioning`, `PassiveCooler`, `Stonecutting`,
`ComplexFurniture`, `ComplexClothing`, `NutrientPaste`. So on day 1, with no
research at all, the colony may build:

- **WoodFiredGenerator, Heater, PowerConduit, StandingLamp** (Electricity) — so
  the barracks gets a thermostat, not a campfire. `barracks-heat` was promoted to
  `daily.md` from M1 day 3, where a campfire and two torch lamps sealed in a
  49-cell room read 28.2 °C against 8 °C outdoors. This run does not repeat that
  shape: heat is electric and set with `temp-set`.
- **Cooler** (AirConditioning) — a real freezer with a target, which is what
  T1(d)'s `food_days >= 6` at day 20 actually depends on, since `food_days` is
  FRESH-ONLY and a simple meal rots in days at 10 °C.
- **TableStonecutter** (Stonecutting) — Workshop role **and** 36,000 stone
  blocks' worth of chunks already on the map.
- **ChessTable** (ComplexFurniture) — a fourth rec item for T5.
- **HandTailoringBench** (ComplexClothing) — Workshop role, and the answer to
  `Alert_NeedWarmClothes` once cotton is grown.

## 3. Grading map — every row to the thing that satisfies it

| row | what satisfies it | when |
|---|---|---|
| G1 nobody lost | `posture` set day 1; Doctor floor repaired day 1; `triage`'s `act` sent verbatim on any casualty halt | continuous |
| G2 no god-hand | no `dev:*` call is planned at all; watermark seq 0. `place-layout --mode instant` never used, and its absence stated (it journals as `action`, so G2 cannot see it) | continuous |
| G3 red errors | `halt_on_error` left on; each `red_error` triaged ours / mod-under-test / vanilla | continuous |
| G4 discipline | every `draft` paired with an `undraft`; escapes listed with reasons; `threat-pardon` unused unless something dormant appears | continuous |
| G5 a raid was met | `StorytellerComp_ClassicIntro` fires `RaidEnemy` ~tick 324,000 (day 6, 40 pts); Cassandra's ThreatBig cycle opens day 11 | day 6, day 11+ |
| T1 food cycle | rice zone (a,b) → FueledStove + meal bill (c) → freezer keeps (d) true at day 20 | sow day 1, harvest ~day 10 |
| T2 wealth grew | buildings, then sculptures off `TableSculpting` (Jimmy, Artistic 6 major) | continuous |
| T3 mood held | rec hall with 4 rec items, floors, a table to eat at, private-ish barracks, heat at a target | by day 12 |
| T4 workshop | `AR_M1_Workshop_9x7`: sculpting + stonecutter + tailoring = Workshop **81** | day 7–10 |
| T5 rec room | `AR_M1_RecHall_9x7`: 4 rec items = RecRoom **28** > DiningRoom 12 | day 10–14 |
| T6 barracks | `AR_M1_Barracks_9x7`: 3 beds = Barracks **300300**, all three assigned | day 1–2 |
| T7 militia | rifle → Jimmy (Shooting 2), revolver → Lacey; flak set on Jimmy; `posture` seek auto | day 1 |

Room roles are engineered off the decompiled `RoomRoleWorker_*.GetScore`, read
this session, not hoped for. Two facts that shape the geometry:

- **Barracks scores 100100 per non-medical bed.** `Room.ContainedAndAdjacentThings`
  is fed by `RegionListersUpdater`, which registers a thing in every passable
  region its rect expanded by 1 touches. One bed leaking into the workshop would
  outscore Workshop 81 by four orders of magnitude, so no bed sits within one
  cell of any door, and the buildings are detached with a plaza between them.
- **A table is DiningRoom 12.** The rec hall needs two rec items standing
  *before* its table goes in, or a lone HorseshoesPin's 7 loses to 12. The
  kitchen gets no eating surface at all, which also keeps it clean —
  `CompFoodPoisonable` rolls against the room's own `FoodPoisonChance`.

## 4. The base — "Newarrivals", x104–124, z116–152

Two columns of detached 9×7 buildings (interior 7×5 = 35 cells, well under the
320-cell auto-roof ceiling) either side of a 3-wide plaza spine at **x113–115**,
which is also the stockpile and the conduit run. Every door faces the spine.

```
            x104        x112 x113-115 x116        x124
   z152                    ▲                 ┌─────────┐
                           │                 │ FREEZER │   (116,146)
   z146                    │                 └────┬────┘
   z145        · · · · · · plaza / stockpile · · · · · ·
   z143
   z142   ┌─────────┐      │                 ┌─────────┐
          │ BARRACKS│──────┤                 │ KITCHEN │   (116,136)
   z136   └─────────┘      │                 └─────────┘
   z135        · · · steel 204 lies here · · · · · · · ·
   z133
   z132   ┌─────────┐      │                 ┌─────────┐
          │ WORKSHOP│──────┤                 │   LAB   │   (116,126)
   z126   └─────────┘      │                 └─────────┘
   z125        · · · · · · · · · · · · · · · · · · · · ·
   z123
   z122   ┌─────────┐      │                 ┌─────────┐
          │ REC HALL│──────┤                 │  POWER  │   (116,116)
   z116   └─────────┘      ▼                 └─────────┘
```

| # | building | origin (SW) | rect | role arithmetic |
|---|---|---|---|---|
| 1 | BARRACKS | (104,136) | x104–112 z136–142 | 3 Bed → Barracks 300300 |
| 2 | KITCHEN | (116,136) | x116–124 z136–142 | FueledStove → Kitchen 28; no table on purpose |
| 3 | WORKSHOP | (104,126) | x104–112 z126–132 | Sculpting + Stonecutter + Tailoring → Workshop 81 |
| 4 | LABORATORY | (116,126) | x116–124 z126–132 | SimpleResearchBench → Laboratory 60 |
| 5 | REC HALL | (104,116) | x104–112 z116–122 | 4 rec items → RecRoom 28 (> table's 12) |
| 6 | POWER ROOM | (116,116) | x116–124 z116–122 | WoodFiredGenerator 1200 W; **steel walls**, they must not burn |
| 7 | FREEZER | (116,146) | x116–124 z146–152 | 2 × `Cooler_North` in the north wall |

Each shell is 27 walls + 1 door = 4,495 work units and 160 stuff. The whole
seven-building base with all furniture is roughly **62,000 work units ≈ two
builder-days** at these skills, which is why the plan is ambitious: labour is
not the binding constraint on a 20-day run, hauling and the raid clock are.

`Cooler_North` is load-bearing and not a guess: `Building_Cooler.TickRare` cools
`Position + South.RotatedBy(Rotation)` and vents to
`Position + North.RotatedBy(Rotation)`, so a Rot4.North cooler in the north wall
chills row 1 inside and dumps its heat outdoors. Under a south-up reading it
would refrigerate the sky — the trap `templates/INDEX.md` pins.

Layouts are authored and offline-validated (`place-layout --print-payload`, all
seven resolve; barracks checked element by element: bed at grid (1,1) → (105,141),
door at (3,8) → (112,139)). They live in `layouts/` with the generator that wrote
them.

### Where the buildings sit relative to what is already there

The plaza spine at x113–115 was chosen so the 204 steel at z132–135 lands in the
open yard rather than under a wall, and so the wood at x98–102 is 2–6 cells off
the west edge. One steel stack, (108,132) ×53, does fall inside the workshop
footprint and will be hauled out first;
`GenConstruct.HandleBlockingThingJob` handles the rest.

**One thing on this map is never touched**: the void monolith at (101,168), 16
cells north of the freezer. The ruin whose wall runs x100–101, z144–151 with a
door at (101,150) is *not* a hazard — `room-at (99,147)` reads `outdoors`, room
17, so it is an open ruin rather than a sealed complex, and it holds a
`SteleLarge` (beauty), a `Table1x2c` and 19 wall cells of free deconstruction.
The freezer still went to the east column, but for haul distance from the
kitchen, not for fear of it.

## 5. The farm

Rich soil directly west of the compound, one cell off column A:

- **Rice** — x94–102, z118–131 (9 × 14 = 126 cells). `Plant_Rice` named in the
  `zone {op:"add"}` call itself: the getter ASSIGNS on first read and the field is
  scribed, so the first observer to touch an unset zone commits potato forever
  ([[growing-zone-default-is-potato]]).
- **Cotton** — x94–102, z133–142 (9 × 10 = 90 cells) → cloth → HandTailoringBench
  → the answer to `Alert_NeedWarmClothes` before autumn.

Three colonists eat ~4.8 nutrition/day. Rice at ~0.09 nutrition/plant/day needs
~53 plants to break even; 126 cells is the margin that makes T1(d)'s
`food_days >= 6` a harvest surplus rather than a rounding error.

Crops are **not** a designation — `WorkGiver_Grower.PotentialWorkCellsGlobal`
walks `zoneManager.AllZones` with no designation check. Wood and stone are:
`designate {type:"chop"}` and `{type:"mine"}` are the standing loop, and
`materials-designation-loop` pages when wood < 100 or steel < 50.

## 6. Work assignment

Checkbox mode today (`use_priorities: false`, every row 0 or 3). The gaps that
matter, all of them real:

| fix | why |
|---|---|
| `work-cover {work:"Doctor"}` | floor 2 on availability, have 1. Promotes Lacey (Medicine 3). This is the fix M1 landed *after* its first death |
| Construction **on** for Jimmy | Construction 4 with a **major** passion and the row reads 0 — the best builder on the roster is switched off |
| PlantCutting **on** for Lacey and Jimmy | only Wouter has it, and he is the doctor and the researcher. Nobody would fell a tree |
| Growing **on** for Lacey and Jimmy | same shape; Wouter alone cannot sow 126 cells |
| Mining **on** for Lacey | only Jimmy has it, and the steel vein is 274 cells |

After any bill is queued, `bill-who-will-do-it` fires:
`WorkGiver_DoBill.ShouldSkip` requires `billGiver != pawn`, so a bill whose only
eligible worker is its own patient produces no job and looks like nothing is wrong.

## 7. Research order

`current` is Pemmican with `bench_ok: false` — the game auto-selected it and
nothing can work it. Set deliberately once the laboratory stands:

1. **Batteries** (400) — buffer the generator so it is not a fuel furnace
2. **Firefoam** (600) — the popper the power room is shaped around
3. **Smithing** (700) — FueledSmithy: armour and weapons, wealth (T2) and militia (T7)
4. **SolarPanels** (600) — generation that needs no chopping
5. **Autodoors** / **CarpetMaking** / **Devilstrand** as time allows

Not GeothermalPower: 3,200 RP and the nearest geyser is (101,86), 45 cells out.

## 8. Timeline against the raid clock

| phase | days | what |
|---|---|---|
| 0 survive the first night | day 1 | stockpile → unforbid → work fixes → equip + flak → allowed area + `posture` → rice zone → chop/mine designations → BARRACKS shell + 3 beds, assigned |
| 1 the machine that feeds itself | 2–5 | POWER ROOM + generator + conduit spine + barracks Heater at a `temp-set` target; KITCHEN + stove + meal bill; LABORATORY + research bench; defensive cover **before day 6** — structural count, never `Alert_NeedDefenses`, which self-silences on day 6 whatever the state ([[alert-need-defenses-self-silences]]) |
| 2 meet the raid | 6 | 40-point `RaidEnemy`. Seek already on, armed already true. Intervene by exception only; `raid-end` in its written order |
| 3 build the colony out | 7–12 | WORKSHOP + three benches + sculpture bill; FREEZER + coolers + `temp-set`; first rice harvest ~day 10; cotton |
| 4 the nice base | 13–18 | REC HALL, floors, tailoring for winter, batteries, Cassandra's ThreatBig cycle from day 11 |
| 5 close the run | 19–20 | day-20 `room` read on each graded interior cell, `history {points:64, values:true}`, `trends`, final digest, the tables filled in |

## 9. What will probably go wrong

- **Hauling, not building.** 1,170 steel across five clusters and 1,824 chunks;
  three colonists; `resources.*` is stockpiles-only, so everything reads zero
  until it is hauled. Expect the material bill to say `short_by` on things the
  colony owns — `place-layout`'s `available` vs `in_stockpiles` split is the read
  that tells the two apart.
- **Nudist Lacey** wears a shirt and pants at 3 °C outdoors. Stripping her for
  the mood buff costs her `comfort_min_c 5.9`. She stays clothed; the mood is
  paid for out of the rec hall.
- **Wouter is the whole medical system** and cannot fight. If he goes down, the
  Doctor floor collapses to the pawn `work-cover` promoted. That is exactly the
  single point of failure `one-doctor-is-zero-doctors` names, and the reason the
  floor is repaired on day 1 rather than at the first casualty.
- **The void monolith,** and only it. My first pass listed the three ancient
  security turrets as a hazard and drew the allowed area to exclude them; Evan
  corrected it and the def agrees — they are looted props, deconstructable for
  scrap, with no turret comp. The ancient ruins on this map are scenery and a
  small steel supply. The monolith at (101,168) is the live one: Anomaly is
  active, the contract says observed only, and if it activates on its own that
  is a letter to read, not a project to take up.
- **Anomaly is live.** Contract says observed only. If the monolith activates on
  its own, that is a letter to read, not a project to take up.
