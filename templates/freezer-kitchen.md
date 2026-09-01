# freezer + kitchen — 11×6, one wall shared

Two 4×4 rooms and a doorway between them. The freezer (west) has no exterior
door at all; the kitchen (east) is its airlock. Coolers sit in the freezer's
north wall, hot side out.

    W W C C W W W W W W W      row 0 = north (proposed — templates/INDEX.md)
    W . . . . W S s s . W      C = Cooler_North (exhaust out)
    W . . . . W . . . . W      S = FueledStove_South (3×1, anchor at west cell)
    W . . . . D . . . . W      D = the only freezer door — through the kitchen
    W . . . . W . . . . W
    W W W W W W W W D W W      kitchen's exterior door, offset from the axis
    (sketch; the .ir.json is the artifact)

Layer 1 is a `PowerConduit` spine down col 5 — under the shared wall, under
the freezer door, and under the south wall, where the run continues out to the
base grid. It is drawn separately because it shares cells with layer 0.

**`Cooler_North`'s correctness depends on row 0 being north**, which is a
PROPOSED dialect pin, not a settled one (`templates/INDEX.md`; 3.3 pins it).
`Building_Cooler.TickRare` cools `Position + IntVec3.South.RotatedBy(Rotation)`
and pushes the heat to `Position + IntVec3.North.RotatedBy(Rotation)`, so a
`Cooler_North` in row 0 chills whatever is at row 1. If row 0 turns out to be
SOUTH, these two coolers heat the freezer and refrigerate the outdoors.

## Lessons baked in

- **The freezer's only door opens into the kitchen.** Two effects, one wall:
  every haul from outside passes two doors (the buffering a dedicated airlock
  would cost extra footprint to get), and "cold storage nearby" is one step
  from the stove — Evan's siting rule made into geometry
  ([[benches-go-indoors]]). *(door-buffering rationale: proposed — community
  practice, not source-verified.)*
- **Cold is CHECKED, not assumed — this room is why `freezer-below-zero`
  exists** (`checklists/daily.md`). The game's only spoilage teaching fires
  inside the branch that destroys the rotted food, at GoodToKnow
  (`CompRottable`, `rotDestroys` path). Set coolers well below zero (target
  ≤ −5 *(proposed)*) so door traffic cannot cross the melting line; the daily
  item flags at −1. Two coolers is redundancy against one breakdown, not
  extra capacity.
- **Register the landmark the checklist reads.** After placement:
  `landmark {set:{name:"freezer", at:<freezer center cell>}}`. The daily item's read is
  `room-at` at that landmark; a template that creates the room also creates
  the name the playbook watches it by.
- **The coolers need POWER, and this template has no generator.** Two
  `Cooler`s draw 200 W each (`basePowerConsumption 200`) — **400 W standing**,
  before the base's lights. Nothing here produces it: the stove is
  `FueledStove` and burns wood, and the shell is unpowered. The layout is
  half a room until it is tied into a grid that has 400 W of headroom
  (`digest` → `power.gen_w` vs `power.draw_w`; see `power-deficit` in
  `checklists/turn.md`).
  **And the wiring rule here is the CONNECTOR rule, not the transmitter
  rule.** A `Cooler` carries `CompProperties_Power` with
  `compClass CompPowerTrader` and no `transmitsPower`, so `ThingDef
  .ConnectToPower` is true: it is an appliance, and
  `PowerConnectionMaker.BestTransmitterForConnector` finds it the nearest
  transmitter inside `CellRect.SingleCell(pos).ExpandedBy(6)`. **Within 6
  cells is enough; nothing has to touch.** That is the opposite of the rule
  for batteries and conduits, which are transmitters and must physically abut
  (`templates/power-room.md` §Why the bank needs the hidden conduit — the
  confusion between the two shipped a broken power room). The col-5 spine is
  inside 6 of both cooler cells with room to spare.
- **A dirty kitchen poisons meals regardless of the cook.** A cooked meal
  rolls against the ROOM's `FoodPoisonChance` stat — a curve over room
  Cleanliness: 0% at ≥ −2, 2.5% at −3.5, 5% at −5; cooking roomless is a
  flat 2% (`CompFoodPoisonable.Notify_RecipeProduced`, which rolls
  `pawn.GetRoom()?.GetStat(RoomStatDefOf.FoodPoisonChance)` and falls back to
  the stat's `roomlessScore`; `RoomStats.xml`). So: constructed
  floor before first meal, butchering OUT of this room (its filth is the
  cleanliness sink — give it an alcove or the freezer edge), and cleaning
  reaches here. "Clean kitchen" is a number the `room` verb can read.
- **The stove is not food.** `Alert_NeedMealSource` tests only that the
  building exists (and is silent before day 2). Placing this template
  completes exactly half the job; the bill and its worker are
  `production-still-runs` + `bill-who-will-do-it` in the checklists.

## Parameters and constraints

- constraint: freezer and shared walls take any stuff (cold does not care),
  but the FLOORS of both rooms must be constructed terrain before use — the
  cleanliness lesson above. The `terrain` grid is deliberately empty: floor
  choice is a stuff-level parameter and terrain tokens would pin it.
- constraint: nothing flammable stored against the stove's cell row
  *(proposed — cheap caution)*.
- variants: a deeper freezer extends south (both rooms together, keeping the
  shared wall); scaling must preserve all three invariants — no exterior
  freezer door, coolers' hot side out, stove within one door of the cold.
- research: Cooler needs **AirConditioning** (+ construction 5) — this
  template is honestly unplaceable before that research completes; the stove
  and shell are not gated. `FueledStove` burns wood (the standing designation
  loop feeds it); swap `ElectricStove` only after the power room exists.

## Placement

`place-layout templates/freezer-kitchen.ir.json --mode blueprint` (3.3 — the
verb does not exist yet; `1adc737` is open). After the walls close, in order:

1. **Wire it.** Extend the col-5 conduit spine south from the template's edge
   to a net that has a generator and 400 W of headroom. The coolers are
   connectors, so they need a transmitter within 6 cells — they do NOT need
   conduit in their own cell. Prove it landed: `power.gen_w` covers
   `power.draw_w`, and `power.nets == power.nets_with_generator` (a gap means
   some net has storage or appliances and no generator —
   `templates/power-room.md` §Placement carries the argument).
2. **Stockpile zone** over the freezer interior — food filter, Important
   priority, filter configured at creation, the same discipline as growing
   zones.
3. **Landmark** `freezer`, so `checklists/daily.md` §freezer-below-zero has
   something to read.
4. **Meal bill** (3.6, `48f666c`) — and the worker who will take it
   ([[who-will-actually-do-it]]).

Steps 1–4 are not IR: a layout places things; the template's .md is what
remembers the rest.
