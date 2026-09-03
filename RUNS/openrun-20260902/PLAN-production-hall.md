# The production hall — sited once, filled over the whole run

Outer 15x11 at **x96..110, z83..93**; interior 13x9. South-east corner of the
compound: maximum distance from the freezer (north-west), because the smelter
and the smithy are heat sources and this biome already runs 27 C indoors in
spring.

    999900000000001
    678901234567890
 93 |###############|
 92 |#111 222 333ss#|   1,2,3  bench slots (3x1), rot NORTH -> interaction z91
 91 |#             #|   aisle
 90 |#  TT    TT   #|   T  tool cabinets (2x1)
 89 |#             #|   aisle
 88 |#FFFFF  444 ss#|   F  FabricationBench (5x2)   4  bench slot
 87 |#FFFFF        #|
 86 |#             #|   aisle
 85 |#555 666 777ss#|   5,6,7 bench slots, rot SOUTH -> interaction z86
 84 |#             #|
 83 |#######+#######|   door, south wall

`s` = Shelf (2x1, 20 stuff) — input buffer for steel, components and cloth, so
a crafter never walks to the bulk store mid-bill.

## Why this shape

- **Two bench rows facing a shared aisle.** Every bench's interaction cell
  lands in an aisle, never in another bench or a wall. Rotation is the trap:
  `rot North` puts the interaction cell to the SOUTH (measured this run — a
  research bench placed `rot South` had its interaction cell in the north wall
  and the whole placement silently failed). North row is `rot North`, south
  row is `rot South`.
- **The room must stay role=Workshop.** Every production bench in the game
  carries `workTableRoomRole: Workshop` and `workTableNotInRoomRoleFactor: 0.8`
  — a flat **20% work-speed penalty** for a bench in a room the game scores as
  something else. Our current Laboratory holds two research benches AND the
  stonecutter, so the stonecutter is taking that penalty right now. **No
  research bench, no bed, no dining table goes in this hall**, or it stops
  being a Workshop and every bench in it loses 20%.
- **Tool cabinets on the centreline.** `CompProperties_Facility.maxDistance`
  defaults to **8 cells** and ToolCabinet does not override it, so both cabinet
  positions reach every slot in the room (worst case 5.2 cells). But
  `maxSimultaneous: 2` means **one cabinet serves only two benches** — two
  cabinets is +6% on four benches, not on seven. A third and fourth cabinet are
  200 steel each and are a late luxury, not part of the first build.

## Fill order — what goes in each slot, and what unlocks it

| slot | building | gate | cost | power |
|---|---|---|---|---|
| 5 | **TableStonecutter** (MOVE it out of the Laboratory) | have | already built | – |
| 6 | **HandTailoringBench** | have `ComplexClothing` | 75 stuff, **no steel** | – |
| 7 | **ElectricSmelter** | have `Electricity` | 170 steel, 2 comp | 700 W |
| 1 | **ElectricSmithy** | `Smithing` 700 | 100 steel, 3 comp | 210 W |
| 2 | **TableMachining** | `Machining` 1,000 | 150 steel, 5 comp | 350 W |
| 6 | ElectricTailoringBench (replaces the hand bench) | have, needs power | 50 steel, 2 comp, 75 stuff | 120 W |
| F | **FabricationBench** | `Fabrication` 4,000 | 200 steel, 12 comp, **2 ComponentSpacer** | 250 W |
| 3, 4 | spare — second machining table, drug lab, crematorium | — | — | — |

Full-build draw: 700+350+210+120+250 = **1,630 W**, against geothermal's 3,600.

**Build the hand tailoring bench first.** It is 75 stone blocks, needs no steel
and no power, and the Summer letter's warning was explicit: *"buy, steal, or
make some parkas, or you'll freeze when you step outside."* A parka is 80
leather or cloth and no research. Winter is roughly 30 days out and we have
neither.

## The two things this hall is really for

**1. G3's third gun.** Three violence-capable colonists, two firearms. A
revolver is 30 steel + 2 components at Crafting 3 — trivial — but it is made at
the **machining table**, so the real price is `Smithing` 700 + `Machining`
1,000 = **1,700 research**. A bolt-action rifle is 60 steel + 3 comp at
Crafting 5. `FlakArmor` (1,200, after Machining) then makes flak vests at
30 cloth + 60 steel + 1 comp, which is the only route to G3's armour half
scaling past the three pieces we landed with.

**2. Component independence.** `Make_ComponentIndustrial` and
`Make_ComponentSpacer` exist on exactly one building, the FabricationBench, and
nowhere else — verified against the RecipeDefs (`recipeUsers` is empty on both;
they attach only through the bench's own `recipes` list). The chain:

    MicroelectronicsBasics 3,000  -> HiTechResearchBench   100 steel, 10 comp, 250 W
    MultiAnalyzer          4,000  -> MultiAnalyzer bldg     40 steel,  8 comp,
                                                            50 PLASTEEL, 20 GOLD
    Fabrication            4,000  -> FabricationBench      200 steel, 12 comp,
                                                            2 COMPONENT SPACER
    then Make_ComponentIndustrial, which needs **Crafting 8**

11,000 research beyond the G1/G2 chain, and it terminates in three materials
this map cannot produce: **plasteel, gold and advanced components — zero of
each on the ground, and no gold or plasteel ore anywhere.** All three are
trade-only.

## Therefore: the colony needs an export, and it does not have one

Shopping list to reach component independence *and* finish G1:

| item | qty | why | rough silver |
|---|---|---|---|
| ComponentSpacer | 3 | 2 for the FabricationBench, 1 for the ground-penetrating scanner | ~840 at trader markup |
| Plasteel | 50 | MultiAnalyzer | ~275 |
| Gold | 20 | MultiAnalyzer | ~110 |

That is **~1,200+ silver against the 800 we hold**, before buying a single
component or medicine. The colony has to sell something.

The only high-value thing this roster can make is **art** — Walton is
Artistic 7 with a major passion, and nobody else on the map is above 1. A
sculpture does not need to be *installed* to be sold; it sells fine as a
minified thing, so the missing install verb (git-bug `6d4ca8a`) blocks G5 but
does **not** block art as an export. Slots 3 and 4 can take an art bench, or it
can go back in the Laboratory once the stonecutter moves out.

**This is a decision for you, because you told me to stop making art.** Art as
a G5 objective is dead until the install verb ships. Art as the colony's only
export is a different question and it is currently the only answer I have to
the 1,200-silver gap. The alternatives are thinner: crafted guns sell well but
sit behind the same 1,700 research, and bulk steel and stone blocks are close
to worthless per unit of hauling.

## On making the base look good

Floors are cheap and should go down everywhere: `TileGranite` is 4 blocks per
cell and we hold 817 granite chunks (~16,000 blocks). The hall's 117 interior
cells cost 468 blocks — free, and it makes the room clean and fast to walk.

Beauty is the harder half and worth being straight about: stone tile is
beauty-neutral. The floors that actually raise a room's impressiveness are
`GoldTile` (+11) and `SilverTile` (+4), at 70 gold or 70 silver **per cell** —
unaffordable while silver is earmarked for the shopping list above. So the
honest ceiling right now is *orderly and clean*, not *beautiful*: coherent
rectangles, floored rooms, nothing deteriorating outdoors, symmetry in the
bench rows. Real beauty arrives with the install verb or with gold, and both
are on the far side of a trader.
