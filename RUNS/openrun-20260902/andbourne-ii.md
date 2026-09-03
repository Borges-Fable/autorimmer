# Andbourne II — the annotated IR

Machine half: `andbourne-ii.ir.json` (baseviz IR, `baseviz/ir.py`).
Lesson half: this file. Convention is `templates/INDEX.md` §"Format: annotated
IR, in two halves" — every placement decision that came from a constraint is
here, with the def or the source member it came from.

    rwa place-layout RUNS/openrun-20260902/andbourne-ii.ir.json --origin 82,74

**Origin `82,74`** — the SOUTH-WEST corner, the corner `find-rect` returns as
`at`. Footprint **42 x 34**, game `x82..123 / z74..107`. Grid `(r,c)` maps to
game `(82 + c, 107 - r)`; **row 0 is north** (pin 0), an element's `at` is its
footprint's **north-west cell** (pin 1), and a `_North/_South/...` suffix is the
**`Rot4` value verbatim** (pin 2).

> `python3 -m baseviz view` prints this grid **upside down** — `canvas.py:245`
> iterates `range(H-1,-1,-1)` under a header that says "north up" (git-bug
> `8847053`). Read its output mirrored: the two `CO` cooler cells it prints on
> the bottom line are row 0, the NORTH wall.

---

## 1. The frame, and why it is where it is

Three constraints fixed the frame before any room was drawn.

**The geyser fixes the west.** `PlaceWorker_OnSteamGeyser` requires
`geyser.Position == the placement centre`. The geyser's `Position` in
`day-1-start.rws` is `(88,96)`; `GenAdj.OccupiedRect` gives a 6x6 the centre
`min + (size-1)/2 = min + 2`, so the generator's only legal footprint is
**x86..91 / z94..99** and nothing else places. That is grid rows 8..13, cols
4..9 — inside the west block, and inside the walls, which is the point: a
generator outside the perimeter is a free objective.

**The soil fixes the north.** The only fertile ground is the soil/gravel blob at
roughly x79..93 / z108..121. The north wall sits at **z107**, one cell clear of
it, so the farm gate at `(0,5)` = **x87, z107** opens directly onto `x87,z108`,
which the save's terrain grid says is `Soil`. Nothing is built on the blob.

**Buildable ground fixes the rest.** The terrain grid was decoded out of
`saves/day-1-start.rws` (`<terrainGrid><topGridDeflate>`, raw-deflate base64,
one `ushort` per cell, index `z*250+x`) and every one of the 1,428 cells under
this footprint is `Sand` or `Gravel` — both carry `Light/Medium/Heavy/Diggable`.
That matters twice: `Wall` is `useStuffTerrainAffordance` +
`terrainAffordanceNeeded Heavy`, and stone stuff needs `Heavy`; and `SoftSand`
(`Light, Diggable` only, **no Heavy**) exists on this map at z≤72, x83..92 —
about four rows south of the wall. A frame two rows taller would have put the
south perimeter on ground that refuses stone walls.

## 2. Rooms

| room | grid rows | grid cols | game x | game z | cells |
|---|---|---|---|---|---|
| bulk store / farm entry | 1..6 | 1..10 | 83..92 | 101..106 | 60 |
| power room | 8..17 | 1..10 | 83..92 | 90..99 | 100 |
| memorial garden | 19..25 | 1..10 | 83..92 | 82..88 | 70 |
| gate hall | 27..32 | 1..10 | 83..92 | 75..80 | 60 |
| freezer | 1..6 | 12..19 | 94..101 | 101..106 | 48 |
| **kitchen** | 1..6 | 21..29 | 103..111 | 101..106 | 54 |
| **PLAZA** | 8..23 | 12..29 | 94..111 | 84..99 | **288** |
| production hall | 25..32 | 12..22 | 94..104 | 75..82 | 88 |
| laboratory | 25..32 | 24..29 | 106..111 | 75..82 | 48 |
| residential corridor | 1..32 | 31 | 113 | 75..106 | 32 |
| bedrooms 1–8 | see below | 33..40 | 115..122 | — | 24 each (8th: 32) |

Bedroom bands, north to south: rows 1..3, 5..7, 9..11, 13..15, 17..19, 21..23,
25..27, 29..32 — game z 104..106, 100..102, 96..98, 92..94, 88..90, 84..86,
80..82, 75..78. Each is 8x3 with a `Bed_South` and a `StandingLamp`, one door
onto the corridor at col 32.

Every one of the eighteen enclosed regions was flood-filled and every one is
reachable from the plaza through doors. There are no dead pockets.

## 3. The plaza — 288 cells, and why not 289

`AutoBuildRoofAreaSetter.TryGenerateAreaNow` bails on
`room.RegionCount > 26 || room.CellCount > 320`. The plaza is
**18 x 16 = 288 cells**, all of it in one room: everything standing in it
(`Column`, `Table2x2c`, `DiningChair`, `ChessTable`, `Stool`, `HorseshoesPin`,
`StandingLamp`) is `PassThroughOnly` or better, so no cell drops out of
`Room.CellCount` and no cell needs a hand-drawn roof area. Region count is 6
(the room spans three 12-wide region columns and two region rows). 288 leaves 32
cells of headroom; it is deliberately not pushed to 319.

It is the geometric centre of the frame: cols 12..29 centre on 20.5, rows 8..23
on 15.5, against a frame centre of 20.5 / 16.5. Everything in it is mirrored
about the col-20/21 axis — 12 `Column`s in two colonnades on rows 9 and 22, four
`StandingLamp`s, two `Table2x2c` with four `DiningChair` each, two `ChessTable`
with two `Stool` each, two `HorseshoesPin`.

**The recreation buildings are placed to their own rules, not by eye.**
`HorseshoesPin` carries `PlaceWorker_WatchArea` and
`watchBuildingStandDistanceRange 5~5`, `watchBuildingStandRectWidth 3`,
`watchBuildingInSameRoom true`. `WatchBuildingUtility.GetWatchCellRect` therefore
wants three standable cells at **exactly** distance 5 in some cardinal
direction, **in the pin's own room**. Both pins are on row 11: the pin at col 15
is used from col 20 (rows 10..12), the pin at col 26 from col 21 (rows 10..12) —
both inside the plaza. Their other three rects mostly land in *other* rooms: for
the col-15 pin the north rect is in the freezer and the west rect in the power
room; for the col-26 pin the north rect is in the kitchen and the east rect in
the residential corridor. `EverPossibleToWatchFrom` checks
`room.ContainsCell(watchCell)` and rejects all of those. The placement survives
because one legal direction is enough, and it was checked rather than assumed.

`ChessTable`'s description says it "requires adjacent chairs or stools to use",
so each has a `Stool` on its cardinal east and west — `Stool_East` on the west
side and `Stool_West` on the east side, i.e. each stool faces the table.

The `DiningChair`s are the same rule read through pin 2: a chair's `Rot4` **is**
its facing, so the row-13 chairs (north of a table on rows 14..15) are
`DiningChair_South` and the row-16 chairs are `DiningChair_North`. This is not
the same reading as a workbench's suffix — see §5.

## 4. The kitchen — the room the run cannot afford to get wrong

Seven food poisonings came from cooking at a campfire on open sand. The
mechanism, read rather than remembered:

`CompFoodPoisonable` (line 38) rolls
`Rand.Chance(pawn.GetRoom()?.GetStat(RoomStatDefOf.FoodPoisonChance) ?? FoodPoisonChance.roomlessScore)`.
`FoodPoisonChance` (`Core/Defs/Rooms/RoomStats.xml:292`) is a
`RoomStatWorker_FromStatByCurve` over `Cleanliness` with `roomlessScore 0.02`
and the curve

    Cleanliness  -5    -> 0.05
    Cleanliness  -3.5  -> 0.025
    Cleanliness  -2    -> 0.00

**So the chance is exactly ZERO at Cleanliness ≥ −2**, and the 2% the colony has
been eating is the flat roomless fallback. An enclosed kitchen is the whole fix
— but only if its Cleanliness stays above −2, and that is a floor decision.

`RoomStatWorker_Cleanliness` sums the `Cleanliness` stat of every contained and
adjacent building/item/filth/plant plus the terrain of every cell, and divides by
`room.CellCount`. Numbers that decide the design:

| | Cleanliness |
|---|---|
| `Sand` (bare ground) | **−1 per cell** |
| `TileSandstone` / `TileGranite` | **0** (no `Cleanliness` statBase) |
| `SterileTile` | +0.6 |
| `Filth_Dirt` / `Filth_Sand` (tracked in) | −5 each (`BaseFilth`) |

The kitchen is **54 cells, enclosed, floored in `TileSandstone`**. Floored, it
starts at 0 and needs a summed −108 to reach the threshold — **22 pieces of
tracked-in dirt**. Unfloored on sand it would start at −1 and need only 11. The
floor is worth exactly a doubling of the filth budget, and it is why the
kitchen's 216 sandstone blocks are not optional.

Two further deliberate choices:

- **54 cells rather than a tight 20.** Cleanliness is an *average*, so a larger
  room dilutes each piece of filth. A 20-cell kitchen crosses −2 on 8 pieces of
  dirt; this one takes 22.
- **Two interior doors from the nearest outdoor door.** The route is
  farm gate → bulk store → freezer → kitchen. Boots shed most of their filth
  before the third room. The kitchen's other door goes to the plaza, so meals
  reach the two dining tables in about ten cells and nobody crosses the kitchen
  to get anywhere except the freezer.

`ElectricStove` sits at grid `(2,22)` (game x104..106, z105) with its
interaction cell at `(3,23)` on open floor. Its `workTableRoomRole` is
**`Kitchen`**, which is why the kitchen is its own room and not a corner of the
plaza — see §5.

Upgrade path, stated because it is affordable-ish and I did not take it:
`SterileTile` is +0.6/cell but costs 3 Steel + 12 Silver per cell and needs
`SterileMaterials`. Flooring these 54 cells is 162 steel and **648 silver** of
the 800 on hand, and PLAN-fort has that silver earmarked for the
`ComponentSpacer`. Stone tile already reaches 0% poisoning; sterile tile only
buys filth headroom. Not worth the spacer.

## 5. Three rooms, because room role is mechanical

Every production bench in this layout carries `workTableRoomRole` and
`workTableNotInRoomRoleFactor 0.8` — a flat 20% speed loss in the wrong room:

- `TableStonecutter`, `ElectricSmithy`, `TableMachining`,
  `ElectricTailoringBench`, `ElectricSmelter`, `FabricationBench` → **Workshop**
- `SimpleResearchBench`, `HiTechResearchBench` → **Laboratory**
- `ElectricStove` → **Kitchen**

So the production hall (rows 25..32, cols 12..22), the laboratory (rows 25..32,
cols 24..29) and the kitchen (rows 1..6, cols 21..29) are three separate walled
rooms. They are not three rooms for looks; merging any two costs 20% of the
throughput of whichever benches lose the role.

### Every interaction cell, walked

`ThingUtility.InteractionCell` is `interactionCellOffset.RotatedBy(rot) + centre`,
and every bench here is `(0,0,-1)` at `Rot4.North`, so the cell is one row SOUTH
of the footprint at its middle column. For a 2-tall bench the centre row is the
**south** row (`minZ = centre.z - (size.z-1)/2`, and `(2-1)/2 == 0`), so the
interaction cell is still one clear row south of the whole footprint — not
inside it.

| bench | grid `at` | size | interaction cell | what is there |
|---|---|---|---|---|
| `TableStonecutter` | (25,12) | 3x1 | (26,13) | aisle |
| `ElectricSmithy` | (25,16) | 3x1 | (26,17) | aisle |
| `TableMachining` | (25,20) | 3x1 | (26,21) | aisle |
| `ElectricTailoringBench` | (27,12) | 3x1 | (28,13) | aisle |
| `ElectricSmelter` | (27,16) | 3x1 | (28,17) | aisle |
| `FabricationBench` | (29,12) | 5x2 | (31,14) | aisle |
| `ElectricStove` | (2,22) | 3x1 | (3,23) | kitchen floor |
| `SimpleResearchBench` | (25,24) | 3x2 | (27,25) | lab aisle |
| `HiTechResearchBench` | (28,24) | 5x2 | (30,26) | lab aisle |
| `SimpleResearchBench` | (30,27) | 3x2 | (32,28) | lab aisle |

All ten land on empty floor, and no two coincide —
`PlaceWorker_PreventInteractionSpotOverlap` refuses only when two benches want
the *same* cell (it scans ±1 around the cell but compares for equality), so
adjacent interaction cells like (26,17) and (28,17) are legal.

### Tool cabinets, and a correction to the brief

The brief says `ToolCabinet`'s `maxSimultaneous: 2` means "one cabinet serves
only TWO benches". **It is the other way round.** The cap is tested in
`CompAffectedByFacilities.CanPotentiallyLinkTo`, on the *bench's* side:

    for each linkedFacilities[i] with def == facilityDef: num++
    if (num + 1 > compProperties.maxSimultaneous) return false;

`linkedFacilities` is the bench's list, so `maxSimultaneous 2` means **a
workbench benefits from at most two tool cabinets**. `CompFacility.LinkToNearbyBuildings`
puts no cap on the other side at all — one cabinet boosts every bench that can
link to it. The distance test is exactly as briefed: `Vector3.Distance` between
`GenThing.TrueCenter`s, `maxDistance` defaulting to `8f` in
`CompProperties_Facility`, with no room or LOS test.

Three cabinets at (26,19), (28,19) and (30,18) put **all three** in range of
**all six** Workshop benches (measured centre-to-centre: worst case 7.43 for the
stonecutter). Each bench takes the best two and ignores the third, so the third
cabinet is redundant-by-design coverage rather than a required one — the brief
asked for three and three fit, but two at (26,19) and (28,19) would already
cover every bench and save 200 steel.

## 6. Freezer and coolers — the rotation, and why

`Building_Cooler.TickRare` cools `Position + IntVec3.South.RotatedBy(Rotation)`
and pushes the heat to `Position + IntVec3.North.RotatedBy(Rotation)`.
`IntVec3Utility.RotatedBy(Rot4)` is `0 => orig; 1 => (z,-x); 2 => (-x,-z); 3 => (-z,x)`,
so with `South = (0,-1)`:

| rotation | cools | vents |
|---|---|---|
| `North` | south | north |
| `East` | west | east |
| `South` | north | south |
| `West` | east | west |

The freezer is the **north-centre** room, so its outer wall is the north
perimeter and the coolers go in it as **`Cooler_North`** at grid `(0,14)` and
`(0,17)` = game (96,107) and (99,107). Cold cell is the row-1 freezer interior;
hot cell is z108, outdoors, over the gravel north of the wall. Reversed
(`Cooler_South`) they would refrigerate the desert.

`PlaceWorker_Cooler.AllowsPlacing` additionally requires **both** the cold and
the hot cell to be non-`Impassable`, blueprints included. Both are open ground
here, so the pair places even when the whole layout goes down as blueprints in
one transaction.

The freezer has **no door to the outdoors** — only west to the bulk store and
east to the kitchen. Every cold-chain door in this base opens onto another
indoor room.

## 7. Power

`GeothermalGenerator` at grid `(8,4)` — the north-west cell of the only legal
footprint, x86..91 / z94..99. It is `Impassable` 6x6, so the power room is drawn
around it: a 3-wide aisle to its west (cols 1..3) and a 1-wide aisle to its east
(col 10), with the room's south third (rows 14..17) free for the bank.

- **3 x `Battery_South`** at (16,2), (16,5), (16,8), spaced one cell apart so a
  short-circuit blast has to jump a gap.
- **`FirefoamPopper`** at (15,4), centred over the bank —
  `templates/power-room.md`'s lesson made geometry.
- The power room's door to the plaza is at **(15,11)**, not (11,11): col 10 is
  free floor only on rows 14..17, because rows 8..13 of cols 4..9 are the
  generator. A door at row 11 would have opened into an impassable machine.

**Peak draw 3,550 W against the geyser's 3,600 W** (2 x `Cooler` 200, `ElectricStove`
350, `ElectricSmelter` 700, `TableMachining` 350, `ElectricSmithy` 210,
`ElectricTailoringBench` 120, `FabricationBench` 250, `HiTechResearchBench` 250,
`MultiAnalyzer` 200, 24 x `StandingLamp` 30). That is 50 W of margin with
literally everything switched on at once, which is why the lamp count was
trimmed from 27 to 24 and why the three batteries are not optional. The smelter
alone is 700 W and is normally flicked off.

180 `PowerConduit` cells run in **layer 1** (pin 3), mostly under wall lines —
walls are `canBuildNonEdificesUnder`, which is what `templates/power-room` calls
"the one deliberate conduit". No conduit is written into a cell holding the
generator or a battery: `PlaceWorker_Conduit` refuses any cell that already
holds something with `EverTransmitsPower`.

## 8. Graves: the constraint that decides a floor

`Grave` has **`terrainAffordanceNeeded: Diggable`**. The stone tile floors
inherit `FloorBase`'s `affordances` — `Light, Medium, Heavy, Walkable` — and
**`Diggable` is not among them**. `Sand` has it. So the memorial garden (rows
19..25, cols 1..10) is the one room in this base with **no `terrain` entry at
all**: eight `Grave_South` and one `Sarcophagus_South` on bare sand, in two
banks — rows 20..21 holds graves at cols 1, 3, 7, 9 with the sarcophagus between
them at col 5, rows 23..24 holds four more graves at cols 1, 3, 7, 9 — with a
walk on row 22 and a `StandingLamp` at each end of the room. Floor it and the
graves stop placing.

`Sarcophagus` needs only `Light`, so it could sit on tile; it is on sand for
symmetry with the graves it stands among. It gates on `ComplexFurniture`.

The room reads as a walled garden, which is the intent — but be honest about the
cost: bare sand is Cleanliness −1/cell and Beauty-neutral, so this is the
dirtiest room in the base by construction. It holds no food and no work, so
nothing cares.

## 9. Defence

**One main gate, one farm gate, and they are not equals.**

The **main gate** is `(33,5)` = game x87, z74, in the south perimeter, opening
into the gate hall (rows 27..32, cols 1..10). A raider entering it walks north up
col 5 into a **granite `Barricade` line on row 30 spanning cols 1..4 and 6..10**
— nine barricades with a one-cell gap at col 5, dead in line with the gate.
Defenders stand on **row 29**, north of the line: the cover is between them and
anything coming from the south, and **col 5, the daily traffic lane, is clear**.
`Barricade` is `PassThroughOnly` with `pathCost 42` and `fillPercent 0.55`, so
climbing the line is slow and exposed rather than impossible, which is what
cover is for.

`Sandbags` was rejected outright: its `stuffCategories` are `Fabric, Leathery`
only, so it cannot be made of stone at all. `Barricade` takes
`Metallic/Woody/Stony`, 5 stuff, 300 base HP — granite's `MaxHitPoints 1.7`
takes that to 510.

The **farm gate** at `(0,5)` is the weaker entrance and it has to be: it exists
to open onto the soil blob, so it cannot be tucked behind anything. It gets a
two-cell post — barricades at (2,4) and (2,6) flanking a clear lane at col 5 —
and nothing more, because a full line across the bulk store would sit on the
harvest haul route to the freezer door at (3,11). **This is the one place where
the design accepts a real weakness rather than hiding it**; if raids start
choosing the north approach, the answer is a walled forecourt outside z107, not
barricades inside the store.

The perimeter itself is 144 granite `Wall` cells at 510 HP each. There are
exactly two ways through it.

## 10. Materials

Tokens in the grid are bare def names (`Wall`, never `Wall_Granite`), as
`templates/INDEX.md` requires; material binds at placement.

**Intended assignment**

| | granite (`BlocksGranite`) | sandstone (`BlocksSandstone`) |
|---|---|---|
| walls | 144 perimeter cells (r0, r33, c0, c41) | 211 interior cells |
| doors | 2 gates (0,5) and (33,5) | 21 interior |
| furniture | 11 `Barricade` | 12 `Column`, 4 `Stool`, 2 `ChessTable`, 2 `HorseshoesPin`, 14 `Shelf`, 8 `Bed`, 2 `Table2x2c`, 1 `Sarcophagus` |
| floor | 212 cells — gate hall 60, plaza ring+cross 120, residential corridor 32 | 786 cells — every other floored room |

**Totals, split by stone**

    GRANITE    720 (walls) + 50 (gates) + 55 (barricades) + 848 (floor)   = 1,673 blocks
    SANDSTONE  1,055 (walls) + 525 (doors) + 1,300 (furniture) + 3,144 (floor) = 6,024 blocks

Against 1,160 granite blocks + 761 granite chunks and 65 sandstone blocks + 777
sandstone chunks on hand: at ~20 blocks a chunk that is roughly 16,400 granite
and 15,600 sandstone available once cut. **Material is not the constraint;
cutting labour is.** 6,024 sandstone blocks is ~301 chunks through the
stonecutter.

**Why granite outside and sandstone inside** — both stones inherit
`StoneBlocksBase`'s `MaxHitPoints x1.8` / `WorkToBuild x6.0` (+140 offset) /
`Flammability x0`, then override:

| | MaxHitPoints | WorkToBuild | Beauty |
|---|---|---|---|
| granite | **x1.7** | x6.0 (inherited) | x1.0 |
| sandstone | x1.4 | **x5.0** | **x1.1** |

A `Wall` is 300 HP / 135 work base, so a granite wall is **510 HP at 1,650
work** and a sandstone wall **420 HP at 1,375 work**. Granite buys 21% more hit
points where things get shot; sandstone builds 17% faster and carries a 10%
beauty bonus where they do not. The 144-cell perimeter is the only place the hit
points are worth the extra work.

**`DiningChair` cannot be stone.** Its `stuffCategories` are `Metallic, Woody`
— no `Stony`. Eight chairs at 45 stuff is **360 wood** of the 627 on hand, and
PLAN-fort calls wood "a dead resource; do not design around it". They are here
anyway because `DiningChair` has `Beauty 8` — the single largest beauty item in
the plaza — and the brief asked for them. If the wood is wanted elsewhere,
`Stool` is `Stony`, 25 stuff, and takes the same seats at the same tables.

`Bed` *is* `Metallic/Woody/Stony`, so all eight beds are sandstone. That costs
`BedRestEffectiveness x0.9` from `StoneBlocksBase` — a real 10% penalty, taken
knowingly, because 360 wood buys either eight beds or eight dining chairs and
not both.

**Steel: 2,880, against 1,497 on hand.** `GeothermalGenerator` 340,
3 x `Battery` 210, 3 x `ToolCabinet` 600, 2 x `Cooler` 180, `ElectricSmelter`
170, `TableMachining` 150, `FabricationBench` 200, `HiTechResearchBench` 100,
`ElectricSmithy` 100, `ElectricStove` 80, `ElectricTailoringBench` 50,
`MultiAnalyzer` 40, 24 x `StandingLamp` 480, 180 x `PowerConduit` 180. This is a
build target, not a single order: PLAN-fort measures +5,160 steel recoverable
from 129 ore cells. `FabricationBench` also wants **2 `ComponentSpacer`**, of
which the colony has zero, and `MultiAnalyzer` wants 50 Plasteel + 20 Gold —
both are drawn as reserved footprint, not as next week's work.

## 11. Research gates

Everything below is drawn and placeable only once its research lands. Read as
build order.

- **now**: `Wall`, `Door`, `Column`, `Barricade`, `Bed`, `StandingLamp`,
  `Table2x2c`, `Stool`, `HorseshoesPin`, `Shelf`, `Grave`, `SimpleResearchBench`
- `Stonecutting` — `TableStonecutter`, and **every floor in this layout**
  (`TileStoneBase` gates on it). Nothing tiles until this lands.
- `Electricity` — `PowerConduit`, `ElectricStove`, `ElectricSmelter`
- `Batteries` — `Battery`; `Firefoam` — `FirefoamPopper`
- `AirConditioning` — `Cooler` (construction 5)
- `ComplexFurniture` — `DiningChair`, `ChessTable`, `ToolCabinet`, `Sarcophagus`
- `Smithing` — `ElectricSmithy`; `Machining` — `TableMachining`;
  `ComplexClothing` — `ElectricTailoringBench`
- `GeothermalPower` — `GeothermalGenerator` (construction 8)
- `MicroelectronicsBasics` — `HiTechResearchBench`; `MultiAnalyzer` —
  `MultiAnalyzer`; `Fabrication` — `FabricationBench` (construction 6)

## 12. Aesthetics — what is actually achievable

Be blunt about it: **stone tile is Beauty 1 per cell** (`TileStoneBase`
`statBases`), which the brief called beauty-neutral and which is near enough —
it is one point spread over a cell. `SilverTile` is Beauty 4 at 70 silver a
cell, `GoldTile` is Beauty 11 at 70 gold a cell. With 800 silver on hand,
`SilverTile` would floor eleven cells of a 288-cell plaza. Neither is a real
option, so the floor is a colour choice, not a beauty lever.

What is left is object beauty and order, and both are used:

- `DiningChair` is **Beauty 8** each, `Column` is **Beauty 5** at 20 stuff and
  750 work — the cheapest real beauty in the game's early list. Twelve columns
  and eight chairs are the plaza's whole beauty budget, deliberately concentrated
  there rather than spread thin.
- Sandstone's `Beauty x1.1` applies to the objects made of it, so the columns are
  5.5 each rather than 5. That is the only reason sandstone rather than granite
  furniture.
- Order is free. The plaza is exactly centred on the frame and everything in it
  is mirrored about the col-20/21 axis. Its floor is a `TileSandstone` field
  inside a `TileGranite` ring with a `TileGranite` cross on rows 15..16 and cols
  20..21 — two stones, one pattern, no cost beyond the blocks already counted.
  The eight bedrooms are identical and evenly spaced down one corridor.

## 13. What this design does not satisfy

Four things, stated rather than buried.

1. **The farm gate is a weak second entrance** (§9). It has a two-barricade post
   and no chokepoint. It exists because the brief requires a gate onto the soil.
2. **One `place-layout` call cannot give the walls two materials.** `--stuff-map`
   is keyed by **def name**, and perimeter and interior walls are both `Wall`.
   The mod already honours a per-element `stuff` field, but `ir_elements()` in
   `rwa/rwa` never emits one because the IR dialect has no channel for it. Filed
   as a git-bug issue. Until then: place with `--stuff Wall=BlocksGranite` and
   accept 1,055 blocks of the interior in granite instead of sandstone (+20% wall
   work, +21% HP), or place the shell and the interior as two passes.
3. **Steel is 2,880 against 1,497 on hand** (§10), and the `FabricationBench`
   needs 2 `ComponentSpacer` the colony does not have. The layout is a target.
4. **The plaza at 288 cells has 32 cells of auto-roof headroom.** Any later
   knock-through that merges it with a neighbour puts it over 320 and it stops
   roofing itself.

## 14. Placement recipe

    # offline review first
    rwa place-layout RUNS/openrun-20260902/andbourne-ii.ir.json --origin 82,74 \
        --print-payload            # 1,673 elements, x82..123 / z74..107

    # preflight against the live map, places nothing on any failure
    rwa place-layout RUNS/openrun-20260902/andbourne-ii.ir.json --origin 82,74 \
        --dry-run \
        --stuff-map '{"*":"BlocksSandstone","Wall":"BlocksGranite",
                      "Door":"BlocksGranite","Barricade":"BlocksGranite",
                      "DiningChair":"WoodLog"}'

The `*` default catches every `MadeFromStuff` def the map does not name.
`strict_stuff` is not set, so anything not stuffable falls through to its own
cost list. The roof grid is all 1s but is a **designation, not a placement** —
every room here is under 320 cells and roofs itself, so `--roof` is redundant.
