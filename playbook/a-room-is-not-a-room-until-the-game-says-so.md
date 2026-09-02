# A room is not a room until the game says so — and a numbers-only read cannot see a hole in a wall

- **severity**: Critical
- **confidence**: observed-at-bench (M1 `m1-20260901`, colony wiped; one of the
  three defects named as the cause) + verified-in-source
- **bites when**: any `place-layout` whose whole point is an enclosed space —
  a freezer, a barracks, a hospital, a prison cell

## The failure

Day 1 of `m1-20260901`, the freezer went down as a layout. Every element built.
`construction {layout_id:"ly-1"}` answered `done: true`, `unresolved: 0`, zero
blockers, and kept answering that for forty days. The colony had no larder, ran
hand-to-mouth into winter, and starved.

The freezer never enclosed. `room-at` on its interior, when it was finally
asked, returned:

    "outdoors": true, "cells": 60082

60,082 cells is not a freezer. It is the map's outdoor room — the freezer's
interior was outdoors, connected to it through a hole in a wall, and had been
since the day it was built.

## Why nothing caught it

**`done` has never meant "the room encloses".** It means every element
resolved, and the two come apart at exactly the tick the last wall completes:
that is also the tick every construction count goes to zero, the layout stops
appearing in any outstanding-work read, and it becomes invisible. A layout that
is 95% built is loud. A layout that is 100% built with a hole in it is silent.

Nothing in the observation surface connected *a placed layout that was supposed
to be a room* to *is it a room yet*. `rooms` could not have shown it either,
and for a reason worth remembering: `rooms` lists rooms that EXIST and skips
outdoor ones, so an unclosed freezer is not a row in it — its cells belong to
the outdoor blob the verb filters out. The one structure the agent was waiting
on was the one structure `rooms` could not mention.

And C1 of the post-mortem is the other half: *"I never looked at the base after
day 1."* One `render` on the first morning would have shown a freezer with a
hole in it. **A numbers-only read cannot see a hole in a wall.**

## There are TWO ways to fail, and the second one looks fine

Both are the game's own members, in `Verse/Room.cs`:

    ProperRoom              → false whenever the space leaks to the map edge
    UsesOutdoorTemperature  → TouchesMapEdge
                              || OpenRoofCount >= CeilToInt(CellCount * 0.25f)

The second is the one that will get you after you have learned the first. **A
room can be perfectly sealed — `ProperRoom` true, no gap anywhere — and still
sit on the outdoor temperature, because a quarter of its roof is missing.** For
a freezer that is the same dead colony by a different mechanism, and a check
that reported only `ProperRoom` would pass it clean. An instant-mode layout is
in exactly that state the moment it is placed: sealed, and completely unroofed
until colonists build the roof.

(`PsychologicallyOutdoors` is a third reading with different thresholds. It is
mood-facing. It is not the enclosure question and it is not the cold question.)

## What to do

1. **After placing any layout that is meant to be a room, read
   `construction {layout_id}` → `enclosure` and check BOTH flags.** One call,
   at placement, before wiring or stockpiling anything.
2. **At every turn, read `digest.construction.layouts_unenclosed`.** The key is
   absent when there is nothing to report, so its presence is the signal
   (`checklists/turn.md` §room-that-is-not-a-room). `rooms` carries the same
   rows on `layouts_unenclosed`.
3. **`gaps[0].at` is the cell.** `standing: "blueprint"` or `"frame"` means a
   build that has not happened; `"missing"` means something destroyed or
   cancelled the wall and it needs replacing.
4. **No gap and `uses_outdoor_temp` still true means the hole is the ROOF.**
   The game roofs an enclosed player room by itself
   (`AutoBuildRoofAreaSetter.TryGenerateAreaNow`, ≤26 regions, ≤320 cells), so
   a roof that is not going on means nobody is free to build it — not that a
   designation is missing.
5. **`unenclosed_for.stale: true` means it has been like that across a day
   boundary.** `tracked_since` says when this process started watching:
   tracking is in memory and resets at a load, so a young `tracked_since` means
   the age is a FLOOR, not that the room is fine.
6. **Look at the base.** Not instead of the numbers — after them. `render` /
   `map-view` over a new layout on the morning after is the read that sees the
   thing no field was asked about.

## The general shape

A completion signal is not a correctness signal, and the moment a thing
finishes is the moment it stops being watched. Ask what the structure was FOR,
and read the field that answers that question — not the one that says the work
is done.

## Citations

- git-bug `a1644d6` (B-3), and DESIGN.md decisions log 2026-09-02
- `Verse/Room.cs` — `ProperRoom`, `UsesOutdoorTemperature`, `OpenRoofCount`
- `Verse/RegionTypeUtility.GetExpectedRegionType` — the per-cell test; a built
  door is `Portal` and closes a room, an unbuilt one is `Normal` and does not
- `RimWorld/ThingDefGenerator_Buildings.NewFrameDef_Thing` — a wall FRAME is an
  edifice and does not close a room, which is why "is something standing here"
  is the wrong question
- run `m1-20260901`, `RUNS/` and `next-run.md` B-3
