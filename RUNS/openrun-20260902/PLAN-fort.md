# Andbourne — fort plan, resource budget, build order

Written day 6 (Summer 1, tick 275,890), game paused, no world changes pending
beyond one blueprint noted below. IR: `andbourne-core.ir.json` (27x28, origin
x85/z84). Render of the CURRENT state: `day6-fort.png`.

## 1. What is wrong with the fort now

Measured, not impressions:

| symptom | number |
|---|---|
| enclosed rooms | 4, none sharing a wall with another |
| enclosed floor | 80 cells for 4 colonists |
| perimeter | **none** — every room is a free-standing box on open sand |
| wall cells built | 99, all of them **steel** |
| stockpile | outdoors, unroofed, 8x8 at (104,104) — everything in it deteriorates |
| kitchen | does not exist |
| freezer | does not exist |
| geyser (88,96) | 12 cells outside everything, undefended |
| longest internal haul | rice field (87,112) to butcher spot (106,112) = 19 cells, all outdoors |

**The expensive mistake is the material.** 99 wall cells at 5 steel = ~500 steel
spent on walls, while 1,594 stone chunks sit unused on the map. Stone blocks
cost the same 5 stuff, are free (chunks are already mined), are non-flammable,
and granite has ~1.7x the hit points of steel. Every wall from here is stone.

## 2. Resource reality

| resource | on map | recoverable | verdict |
|---|---|---|---|
| Steel | 827 (720 forbidden in 3 far caches) | +5,160 from 129 ore cells @ 40 | labour-limited, not scarce |
| ComponentIndustrial | 56 | +44 from 22 ore cells @ 2 (at ~199,212) | enough, IF we mine them |
| **ComponentSpacer** | **0** | **trade only** — 200 silver market value | **the hard gate on G1** |
| Silver | 800 | — | buys the spacer 4x over |
| Stone chunks | 1,594 | ~31,000 blocks | effectively unlimited, free |
| Wood | 32 | ~nil (6 trees, all >100 cells out) | dead resource; do not design around it |

## 3. Steel reserve — ring-fenced, do not spend on structure

    GroundPenetratingScanner   150 steel   4 comp   1 SPACER   (G1)
    DeepDrill                  100 steel   2 comp
    wiring / conduit buffer    200 steel                       (Dorian's floor)
    ---------------------------------------------------------------
    RESERVED SUBTOTAL          450 steel   6 comp   1 spacer

Everything else the six goals need, on top of that reserve:

    GeothermalGenerator        340 steel   8 comp              (G2)
    Battery x2                 140 steel   4 comp              (standing rule 8)
    HiTechResearchBench        100 steel  10 comp  +150 stone  (gates DeepDrilling)
    CommsConsole               120 steel   4 comp              (buys the spacer)
    OrbitalTradeBeacon          40 steel   1 comp
    ElectricStove               80 steel   2 comp              (kitchen)
    Cooler x2                  180 steel   6 comp              (freezer)
    conduit spine ~80 cells     80 steel
    ---------------------------------------------------------------
    GOAL TOTAL               1,530 steel  41 comp   1 spacer

Against 827 + 5,160 = 5,987 steel and 56 + 44 = 100 components, the budget
holds with ~4x headroom on steel and ~2.4x on components. **The binding
constraints are labour and the single ComponentSpacer, not tonnage.**

Structure cost, all stone: ~201 new wall cells (1,005 blocks) + 6 doors
(150 blocks) = **~1,155 blocks = 58 chunks of the 1,594 on hand.**

## 4. The design

Perimeter x85..111 by z84..111. 27x28, interior 25x26 = 650 cells, two doors.
It is drawn to ENCLOSE what already stands rather than demolish it, and to put
the geyser inside the wall.

Room relations, which is the part that actually matters:

- **Geothermal (x86..91, z94..99) sits ON the geyser, inside the west wall.**
  G2 is graded on it still producing; a generator outside the wall is a
  free objective for any raider who wanders past. Enclosing it costs ~12 extra
  wall cells = 60 free blocks.
- **Power room (x86..94, z85..93) shares a wall line with the generator**, so
  the battery bank is one conduit run from the source, and the firefoam popper
  covers both. Batteries before geothermal is standing rule 8; the room exists
  so that ordering is physical, not just remembered.
- **Freezer and kitchen share the z105 wall with a door at (107,105).** The cook
  never leaves cover to fetch an ingredient. The freezer is the FOOD store; the
  bulk store is for stone, steel and components.
- **Bulk store (x86..92, z101..110) is on the west**, where mining and the
  steel caches come from. Ore and blocks never cross the base.
- **Bedrooms stay central-north**, away from the workshop and the generator —
  the two heat sources — which matters in a desert (see section 6).
- **One main gate (111,88), one farm gate (85,106).** Raiders path to a door;
  two doors is two chokepoints and no more.

### The weak adjacency, named rather than hidden

Harvested rice enters at the **farm gate (85,106)** on the west and must cross
~20 cells to the **freezer (104..109, 106..109)** on the east. That is the one
bad haul in the plan and it exists because the only fertile soil on the map is
the patch at x80..93/z108..121 and the bedrooms already occupy the north-centre.

Two ways to fix it, your call:

- **Option A (drawn above)** — accept the haul. Zero demolition. 20 cells is a
  few seconds of walking and RimWorld hauling cost is dominated by trip count,
  not distance.
- **Option B** — move bedrooms A and B east and put kitchen+freezer in the
  north-centre next to the farm gate. Costs rebuilding two 5x7 rooms (~40 wall
  cells = 200 blocks, free material, roughly two of Ellis's days) and makes
  every food haul short for the rest of the run.

## 5. Build order

1. **Cancel `ly-6`.** Bedroom D was placed at x91..95/z93..99 and sits directly
   on the only 6x6 footprint that covers the geyser. It is blueprints only and
   is 58 steel short, so nothing is lost.
2. **Stone first.** Raise the stonecutter bill's target and put a second worker
   on Crafting. Nothing structural gets built until blocks are flowing; every
   block spent is a steel not spent.
3. **Perimeter wall + the two gates** (~104 cells). This is the single biggest
   survivability change and it needs no research and no steel.
4. **Freezer shell + kitchen shell** (stone). They can stand unpowered.
5. **Research finishes Batteries, then SolarPanels (600).** Solar is 100 steel
   + 3 comp for 1,700 W in a desert and it is the bridge: it powers the coolers
   and the electric stove long before GeothermalPower's 3,200 lands. Release
   the `Alert_NeedMealSource` mute the moment the stove is powered.
6. **Batteries, then coolers, then stove.** Freezer target -1C or below.
7. **GeothermalPower (3,200) -> generator on the geyser -> conduit spine.**
8. **MicroelectronicsBasics (3,000) -> HiTech bench + comms console + beacon.**
   The comms console is what buys the ComponentSpacer; until then G1 cannot be
   finished no matter how much steel we mine.
9. **DeepDrilling (1,000) -> GroundPenetratingScanner (1,000) -> build both.**

## 6. Two standing hazards this design answers

- **Heat.** Both existing rooms hit 27.0 and 27.1 C in SPRING, over the 26 C
  comfy max, in a biome whose annual mean is 2.8 C. Summer is worse and the
  Summer letter warns winter kills the crops. Coolers need power; that is the
  whole reason SolarPanels is inserted ahead of GeothermalPower rather than
  after it.
- **Defence.** `Alert_NeedDefenses` self-silences on day 6 regardless, so it is
  not the signal. The design's answer is the perimeter plus a barricade line at
  x106/z86..91 facing the main gate, giving the shooters cover at the one place
  raiders must come through. Barricades are 5 stuff each — stone, free.

## 7. Open, needs a decision

- Option A vs Option B in section 4.
- **G3 is short one ranged weapon.** Three violence-capable colonists (Aaron,
  Ellis, Fitz), two guns (bolt-action rifle, revolver). Fitz is Shooting 8 and
  should hold the rifle; Aaron (3) the revolver; Ellis (0) has nothing. A third
  gun means Smithing (700) + Machining (1,000) + a machining table, or buying
  one — which again waits on the comms console.
- **G5 is blocked by a missing verb, not by play** — nothing installs a
  MinifiedThing (git-bug `6d4ca8a`), so the one sculpture already made can
  never be placed and `MinifiedThing == 0` is unreachable. Art production is
  stopped per your instruction.

---

# REVISION — final layout (supersedes section 4's drawing)

Three changes after the design pass. Section 4's version is kept above so the
reasoning is auditable; **this is the layout to build.**

1. **Option C, not A or B.** Kitchen and freezer move to the WEST, at the farm
   gate; the bulk store moves EAST. Both were unbuilt, so this is a swap on
   paper with zero demolition, and it kills the 20-cell rice haul that Option A
   accepted and Option B was going to pay two of Ellis's days to fix. Ore and
   blocks still never cross the base — mining comes from the west and south-west
   but goes to the *hall*, which is now south-east and fed by its own shelves.
2. **A production hall** at x96..110/z83..93 — see `PLAN-production-hall.md`.
   The compound's south wall moves from z84 to z83 to give it depth.
3. **The main gate moves to the SOUTH wall at (95,83)**, opening into a 1-wide
   corridor at x95 running z84..z92 between the power room's east wall and the
   hall's west wall. That corridor is the chokepoint: raiders funnel single
   file. **Barricades go at the corridor's NORTH mouth, around x93..97/z94 —
   not inside the corridor.** Cover belongs on the defenders' side; barricades
   in the corridor itself would slow our own people on the main daily route
   for no gain.

```
     888889999999999000000000011
     567890123456789012345678901
111 |###########################|
109 |# #fffff#bbb##aaa##ssssss##|   f FREEZER   b bdrm B  a bdrm A  s BULK STORE
107 |# #fffff#bbb##aaa##ssssss##|
105 |# ###+###bbb##aaa#+ssssss##|
104 |# #kkkkk##+####+###ssssss##|   k KITCHEN + dining
102 |# #kkkkk+         #ssssss##|
100 |# #######  #rrrrrrr# ######|   r LABORATORY (exists)
 98 |# GGGGGG   #rrrrrrr# #ccc##|   G geothermal  @ geyser  c bdrm C
 96 |# G@@GGG   #rrrrrrr# #ccc##|
 94 |# GGGGGG             ##+###|
 93 |# ###+#### #######+########|
 91 |# #pppppp#x#MMMMMMMMMMMMM##|   p POWER   M PRODUCTION HALL
 89 |# #pppppp#x#MMMMMMMMMMMMM##|   x  <- corridor; cover goes at its NORTH mouth
 87 |# #pppppp#x#MMMMMMMMMMMMM##|
 85 |# ######## #MMMMMMMMMMMMM##|
 83 |##########+################|   <- MAIN GATE (95,83)
```

The existing art-room U (x96..104, z85..91 — west, south and east walls up, north
wall never built because steel ran out) falls **inside** the hall. Its west wall
at x96 is reused as the hall's west wall; its south and east walls become
internal and get deconstructed (~16 cells, ~40 steel back).

## Revised material call

Everything structural is **stone**, and that is the single biggest correction
from the first draft: 99 wall cells were built in steel at ~500 steel while
1,594 free chunks sat on the map. Floors are `TileGranite` at 4 blocks a cell
— 817 granite chunks is roughly 16,000 blocks, so floor every room.

Steel is now reserved for exactly the goal list in section 3 plus the hall's
benches (170+100+150+50+200 = 670 for smelter, smithy, machining, electric
tailoring and fabrication). Nothing else.
