# workshop — 9×7 general work room

The lesson half of `workshop.ir.json`. Authored 2026-09-02 during run
`openrun-20260902`, whose colony start needed a room for a research bench and
`bedroom.ir.json` was the only shell in the corpus — a 5×7 whose 3×5 interior
a 3×2 `SimpleResearchBench` blocks the beds with.

    footprint   9×7 outer, 7×5 interior (35 cells)
    gate        none — Wall and Door carry no researchPrerequisites
    stuff       bind at placement; nothing here constrains material

## What it carries

- **Benches go indoors** ([[benches-go-indoors]]). A 7×5 interior takes a
  3×2 research bench, a 3×1 sculptor's table and a 3×1 stove with a walking
  lane left over — which the 5×7 bedroom shell does not, and that is the
  whole reason this template exists.
- **The door is on the SOUTH wall, centre** (row 6, col 4). Row 0 is north
  (`templates/INDEX.md`, pinned `bac4eba`), so the door faces the same way
  `bedroom`'s does and two of these placed north/south of each other do not
  open into one another's wall.
- **One `TorchLamp`, at the north-west interior corner** (row 1, col 1), not
  the centre: the centre is the lane. It is the only heat source the template
  places, and it is deliberately one — `daily.md §barracks-heat` measured a
  49-cell room at 28.2 °C off a campfire and two torch lamps, and 35 cells is
  smaller than that. Read the room's `temp_c` after the first day and pull the
  torch if it climbs; a workshop is not a bedroom and 26 °C here costs work
  speed rather than sleep.
- **The roof grid is all 1 and is reported, not sent** — an enclosed player
  room under 320 cells roofs itself (`AutoBuildRoofAreaSetter`), and 63 cells
  is well under. `uses_outdoor_temp` is still the read that proves it
  (`turn.md §room-that-is-not-a-room`); `enclosed` alone is not.

## What it deliberately does not carry

No bench placements. Benches are `build` calls after the shell stands, so the
same shell serves a research room, a kitchen and a crafting room, and so a
bench's own stuff and rotation are chosen against the room that exists rather
than baked into a grid. `FueledStove` is the one token that would need a
rotation argument here (`INDEX.md` pin 2, the interaction-cell bug), which is
a second reason to keep stoves out of the shell.
