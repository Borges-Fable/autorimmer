# freezer + kitchen — 11×6, one wall shared

Two 4×4 rooms and a doorway between them. The freezer (west) has no exterior
door at all; the kitchen (east) is its airlock. Coolers sit in the freezer's
north wall, hot side out.

    W W C C W W W W W W W      row 0 = north; C = Cooler_North (exhaust out)
    W . . . . W S s s . W      S = FueledStove_South (3×1, anchor at west cell)
    W . . . . W . . . . W
    W . . . . D . . . . W      D = the only freezer door — through the kitchen
    W . . . . W . . . . W
    W W W W W W W W D W W      kitchen's exterior door, offset from the axis
    (sketch; the .ir.json is the artifact)

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
- **A dirty kitchen poisons meals regardless of the cook.** A cooked meal
  rolls against the ROOM's `FoodPoisonChance` stat — a curve over room
  Cleanliness: 0% at ≥ −2, 2.5% at −3.5, 5% at −5; cooking roomless is a
  flat 2% (`CompFoodPoisonable.cs:38`, `RoomStats.xml`). So: constructed
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

`place-layout templates/freezer-kitchen.ir.json --mode blueprint` (3.3).
After the walls close: stockpile zone over the freezer interior (food filter,
Important priority — configure the filter at creation, the same discipline as
growing zones), then the landmark, then the meal bill (3.6). Zones and bills
are not IR — a layout places things; the template's .md is what remembers the
rest.
