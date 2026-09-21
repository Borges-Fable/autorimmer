# classroom — 9×9, a Progression: Education classroom for four

The smallest room that runs a Progression: Education class for up to four students:
one learning board, four desks, the bell PE refuses to start without, one light.
Built for the Languages mod's class tests (languages test run, 2026-09-21), and
generic: any PE class uses the same room.

    # # # # # # # # #      row 0 = north (PINNED — templates/INDEX.md)
    # . . B B . . . #      B = Blackboard_South (2×1), the learning board
    # . t t t t . . #      t = the teacher's strip — must stay clear
    # . k k . k k . #      k = SchoolDesk_North (2×1)
    # . s s . s s . #      s = a desk's seats — must stay clear
    # . k k . k k . #
    # . s s . s s . #
    # L . . . . . b #      L = TorchLamp, b = PE_TribalBell_South
    # # # # D # # # #      D = Door
    (sketch; the .ir.json is the artifact)

Mod gate: Biotech (`Blackboard`, `SchoolDesk`) and `ferny.ProgressionEducation`.
Research gate: `ComplexFurniture` (board and desks), `Stonecutting` (the chimes).

## Lessons baked in

- **A board makes the classroom; desks only count if they link to it.**
  `CompLearningBoard.InitializeClassroom` creates the classroom when a board
  spawns in a room. PE's `Startup` gives every learning board a
  `CompProperties_Facility` (maxDistance 100, maxSimultaneous 1) and every desk a
  `CompAffectedByFacilities` for the boards at runtime, and
  `ClassSubjectLogic.BenchCount` counts only desks LINKED to the board. One board
  per room (`PlaceWorker_SingleLearningBoard`); a second room needs its own.
- **PE will not start a class with more students than linked desks, or with no
  bell anywhere on the map** (`StudyGroup.AreWorkspacesAvailable`: `PE_NotEnoughBenches`,
  `PE_NoBell`). Four desks, four students; the bell is in the room so the
  template is self-sufficient. `PE_TribalBell` is the cheapest bell to research
  (`Stonecutting`, vanilla).
- **The seats are the desks' interaction cells, not the desks.**
  `JobDriver_AttendClass.DeskSpotForStudent` sends the student to
  `InteractionCells[0]`. `SchoolDesk` resolves at runtime to TWO interaction cells,
  `[1,-1]` and `[0,-1]` at Rot North — one south of each desk cell — though its own
  XML carries none (it comes through inheritance; the catalog now dumps the
  resolved value, git-bug 3b9ec3b). `SchoolDesk_North` puts the seats on the row
  below each desk and faces the student at the board.
- **The teacher paces the row in front of the board, one cell wider each side.**
  `EducationUtility.GetWaypointsInFrontOfBoard`: every occupied cell +
  `Rotation.FacingCell`, ± `RighthandCell`, filtered to building-free walkable
  cells. `Blackboard_South` on the north wall faces its strip into the room; the
  whole of row 2 is left empty for it.
- **Placing the board opens a rename dialog.** `InitializeClassroom` pushes
  `Dialog_RenameClassroom` onto the window stack; an advance can halt on it until
  it is dismissed (`dialog-dismiss`).
- **Aisles in columns 1, 4 and 7** connect every seat to the door without
  passing through a desk (desks are `PassThroughOnly`, so they are passable, but
  slow).

## Parameters and constraints

- Footprint 9×9, interior 7×7 = 49 cells: under the 320-cell auto-roof limit.
- Materials: walls, door, board and desks are stuffable; bind them with `--stuff`
  or accept the game's defaults (reported as `stuff_defaulted`).
