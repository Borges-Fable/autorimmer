# bedroom — 5×7, one occupant

The minimal room that reads as a Bedroom, sized to 3.3's own acceptance
rehearsal ("parametric 5×7 bedroom"). Interior 3×5; bed head against the
north wall, torch by the door, twelve free cells for the comfort pass later.

    W W W W W        row 0 = north (PINNED — templates/INDEX.md)
    W . B . W        B = Bed_South (head at wall, faces its foot)
    W . b . W        b = the bed's second cell — '.' in the IR (anchor rule)
    W . . . W
    W . . . W
    W t . . W        t = TorchLamp
    W W D W W        D = Door
    (sketch; the .ir.json is the artifact)

## Lessons baked in

- **A perfect bedroom reads "Barracks" until someone owns the bed.**
  `RoomRoleWorker_Bedroom` → `IsBedroomHelper` reads `bed.OwnersForReading`;
  an unowned bed scores as Barracks (3.3 verification, 2f2796e). The
  template's last step is therefore not placement: assign the occupant, or
  don't file a bug when `room` says Barracks. (Bed assignment is a known verb
  gap — flagged on `e1c072e`; until it ships, expect Barracks on staged
  rooms and log it as such.)
- **The roof builds itself — next tick, not this one.** 35 cells, no map
  edge: `AutoBuildRoofAreaSetter.TryGenerateAreaNow` auto-roofs enclosed
  player rooms ≤ 320 cells / ≤ 26 regions — and it runs from a QUEUE, so the
  roof area does not exist synchronously after the walls close (3.3
  verification). The `roof` grid here matters for instant mode; in blueprint
  mode the game does this itself.
- **Torch by the door, not by the bed.** Light is for the awake; darkness
  costs nothing to a sleeper. Keeping the flame cell away from the bedding
  is cheap caution *(proposed — placement taste, not a cited mechanism)*.
- **Fire prep is a siting rule, not a cell in this grid**: vanilla's
  `FirePreparation` concept teaches gaps between buildings, less-flammable
  materials, 3–4 cell firebreaks (96d9315 comment 1). Site bedrooms
  wall-sharing into a stone core or free-standing with a gap — never
  wood-to-wood in a row.

## Parameters and constraints

- stuff-map: `Wall`, `Door`, `Bed` take any stuff. No constraint — a wooden
  bedroom is legitimate wealth-cheapness; the fire answer here is siting (see
  above), not material.
- variants: a double (`Bed` → `DoubleBed`, same footprint) is the only
  expected one. Bigger rooms are impressiveness work for the comfort pass,
  not new geometry.
- research: none. Everything here is buildable from a crashlanded start.

## Placement

`place-layout templates/bedroom.ir.json --origin <find-rect result> --mode
blueprint` once 3.3 lands (until then: 4.3 stages shelter with dev verbs and
this file is the shape it stages). Blueprint mode reports the material bill
against stockpiles — wood for walls comes from the standing designation loop,
not from hope ([[materials-are-a-standing-loop]]).
