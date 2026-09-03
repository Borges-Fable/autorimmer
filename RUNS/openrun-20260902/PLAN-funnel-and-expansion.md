# Summer plan — the funnel wall and the north expansion

Written Summer 3, year 5502, tick 7,589,091. Dorian's brief: *"one of your rooms
got destroyed! both freezers, we need to plan an expansion and a wall in the next
season, the wall serves to funnel people towards our turrets, open where they are."*

## What actually happened to the freezer

Room 83 — the 48-cell freezer at (94,101) with two coolers — is gone from
`digest.temperature.rooms`, and `room-at {at:[94,101]}` now returns **room 1,
outdoors, 58,581 cells**. `things {def:"Cooler"}` returns **none**: both coolers
destroyed. The 107 nutrition that used to be refrigerated now reads as sitting in
the great outdoors.

The hole is two cells wide. `map-view` over (82,96)–(101,109), reading the z=107
row `#####+########.##.##`, puts open sand at **(95,107)** and **(98,107)** in an
otherwise solid north wall. Two `Wall` blueprints in sandstone are placed there
as of this writing; 10 blocks against 2,467 in stock.

**Nothing is rotting.** `food_rot` reports 76.2 days, `rottable_stacks: 0`,
`imperishable: 228.6` — the colony's food is packaged survival meals and raw
plant food that does not spoil. Losing the freezer cost us the *capability*, not
the stores. The rebuild is therefore not an emergency, which is good, because it
is blocked on steel (below).

## The binding constraint is steel, not blocks or plans

- **Stone: 2,467 sandstone + 2,085 granite blocks.** ~900 wall segments' worth.
  Walls cost no steel. The wall is buildable *today*.
- **Steel: 1.** Two coolers cost 180 steel + 6 components. The freezer, more
  turrets, flak pants and plate armour are all queued behind ore.
- Sources: 30 slag chunks still on the map through the electric smelter at
  (99,80), and the compacted-steel cluster at (99–106, 27–28), 62 cells south,
  which Ludo is now mining.

So: **wall first (stone, available now), freezer second (steel-gated).**

## Phase 1 — seal the breach (in progress)

Wall at (95,107) and (98,107), sandstone. Restores the north wall and puts the
storeroom back indoors.

## Phase 2 — the north expansion

The exposed yard is not a cosmetic problem. The field-larder and potato-pile
stockpiles sit at roughly x92–104, z108–114, **outside the north wall**, and that
is exactly where the scyther caught Ludo alone on Spring 11 and took her to 30%
health. Every raid so far has found a colonist working outdoors.

Build a walled annex over that ground — **x 88–108, z 108–118** — containing:

1. **The rebuilt freezer.** Two coolers in its south wall, drawing off the
   existing net. 180 steel + 6 components, so this waits on the mine.
2. **The bulk stockpiles**, moved inside: field-larder and potato-pile.
3. **An armoury** — one shelf for the spare bolt-action rifles, the pump shotgun
   and the flak that is not being worn.

The point is that the annex removes the reason to stand outside, rather than
defending the standing-outside.

## Phase 3 — the funnel wall

An outer ring at standoff, with **exactly two openings, each already covered by a
turret cluster**:

| side | line | gap | covered by | range to gap |
|---|---|---|---|---|
| south | z = 70, x 76→130 | **x 86, 87, 88** | (85,79) (89,79) (84,80) (90,80) | ~10 cells |
| north | z = 120, x 76→130 | **x 97, 98, 99** | (93,108) (103,108) | ~12 cells |
| west | x = 76, z 70→120 | none | — | — |
| east | x = 130, z 70→120 | none | — | — |

Mini-turret range is 28.9 cells, so both clusters cover their own gap with room
to spare, and the south gap keeps the existing barricade line at z77 and the
perimeter door at (87,74) behind it — the funnel that already works, extended
outward.

**Size and cost.** Perimeter 2×(130−76) + 2×(120−70) = 208 cells, minus 6 gap
cells = **202 walls ≈ 1,010 stone blocks** against 4,552 in stock. The cost is
labour and hauling, not material: the blocks are at (61,117) and (57,111), 40+
cells west, so the haul is the real bill.

**Build order matters.** The north line first — that is the side the last two
raids came from and the side the expansion sits on — then west, then east, then
south last, because the south already has the barricade line and four turrets.

**One honest caveat.** A ring with two gaps does not force RimWorld's raid AI
through the gaps; it makes the gaps the *cheapest* path, and a raid whose pathing
cost through the gap is high enough will hit a wall instead. Sappers and drop
pods ignore it outright. Stone walls buy time under fire and hand the turrets a
predictable lane; they do not make the base sealed. Budget for the wall being
breached somewhere and keep the two-cell-repair habit.

## Sequencing against the research

`MicroelectronicsBasics` (3,000 points, set this session) is the single gate in
front of every remaining military upgrade: assault and sniper rifles via
`PrecisionRifling`, autocannon turrets via `HeavyTurrets`, EMP shells, the comms
console and orbital trade beacon — which is how the 800 silver, 500 gold and 381
plasteel currently doing nothing turn into equipment. John is the only colonist
who can research at all. He stays on it; Anon and Ludo build the wall.
