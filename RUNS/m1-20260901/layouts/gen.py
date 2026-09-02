#!/usr/bin/env python3
"""Generate the m1-20260901 compound layouts as baseviz IR.

Every building is a 9x7 shell (interior 7x5 = 35 cells, well under the 320-cell
auto-roof ceiling) with a single door facing the central plaza spine at x113-115.
ROW 0 IS NORTH (templates/INDEX.md pin, git-bug bac4eba); an element's `at` is
its footprint's NORTH-WEST cell.

Room roles are engineered, not hoped for (RoomRoleWorker_* GetScore, verified
against the 1.6 decompile this session):
  Barracks   nonMedicalBeds * 100100   -> 3 beds, no bed anywhere else
  Workshop   27 * benches with building.workTableRoomRole == Workshop
  Laboratory 60 * benches with workTableRoomRole == Laboratory
  Kitchen    28 * production buildings with a human-edible product recipe
  RecRoom     7 * buildings named by a JoyGiverDef (countsForRecRoom defaults true)
  DiningRoom 12 * buildings with surfaceType == Eat
  Room        0.99  (the floor any of the above beats)
The rec hall therefore needs >= 2 rec items before its table goes in, or
DiningRoom's 12 outscores a lone 7.
"""
import json, os

W, H = 9, 7          # shell footprint
OUT = os.path.dirname(os.path.abspath(__file__))


def shell(door_side, door_row=3):
    """9x7 wall ring with one door. door_side: 'E' or 'W' or 'S'."""
    g = [["." for _ in range(W)] for _ in range(H)]
    for c in range(W):
        g[0][c] = "Wall"
        g[H - 1][c] = "Wall"
    for r in range(1, H - 1):
        g[r][0] = "Wall"
        g[r][W - 1] = "Wall"
    if door_side == "E":
        g[door_row][W - 1] = "Door"
    elif door_side == "W":
        g[door_row][0] = "Door"
    elif door_side == "S":
        g[H - 1][door_row] = "Door"
    return g


def put(g, token, c, r):
    g[r][c] = token


def ir(defName, layers, note):
    return {
        "defName": defName,
        "size": [W, H],
        "spawnConduits": False,
        "layers": layers,
        "terrain": [],
        "roof": [[1] * W for _ in range(H)],
        "modRequirements": [],
        "extension": None,
        "animalCells": [],
        "_note": note,
    }


# ---------------------------------------------------------------- 1 BARRACKS
# origin (104,136) -> x104-112, z136-142. Door EAST (onto the plaza spine).
# Three beds on the north side, all >=2 cells clear of the door cell so no bed
# can register in the doorway's own region (RegionListersUpdater expands a
# thing's rect by 1 and Barracks scores 100100 a bed -- one leaked bed would
# outscore any other role in the room it leaked into).
g = shell("E")
put(g, "Bed_South", 1, 1)
put(g, "Bed_South", 3, 1)
put(g, "Bed_South", 5, 1)
put(g, "Heater", 7, 5)
put(g, "TorchLamp", 1, 5)
barracks = ir("AR_M1_Barracks_9x7", [g],
              "3 beds -> Barracks 300300. Heater on the plaza conduit spine; "
              "TorchLamp is the interim light and comes out when power lands.")

# ---------------------------------------------------------------- 2 KITCHEN
# origin (116,136). Door WEST. Stove only + shelves: no table here, so the room
# stays a Kitchen (28) and stays clean -- CompFoodPoisonable rolls against the
# room's FoodPoisonChance, and a room people eat in is a room people dirty.
g = shell("W")
put(g, "FueledStove_North", 2, 1)   # 3x1, pawn works it from the south (r2)
put(g, "Shelf_North", 1, 4)          # 2x1
put(g, "Shelf_North", 5, 4)
put(g, "TorchLamp", 7, 1)
kitchen = ir("AR_M1_Kitchen_9x7", [g],
             "FueledStove -> Kitchen 28. Deliberately no eating surface: the "
             "table lives in the rec hall.")

# ---------------------------------------------------------------- 3 WORKSHOP
# origin (104,126). Door EAST. Three Workshop-role benches -> 81, and none of
# them has a human-edible product, so Kitchen scores 0 here.
g = shell("E")
put(g, "TableSculpting_North", 1, 1)
put(g, "TableStonecutter_North", 5, 1)
put(g, "HandTailoringBench_North", 1, 4)
put(g, "TorchLamp", 7, 5)
workshop = ir("AR_M1_Workshop_9x7", [g],
              "Sculpting + stonecutter + hand tailoring -> Workshop 81. "
              "Sculpting is the T4 product line (Jimmy, Artistic 6 major).")

# ---------------------------------------------------------------- 4 LABORATORY
# origin (116,126). Door WEST. One research bench -> Laboratory 60.
g = shell("W")
put(g, "SimpleResearchBench_North", 3, 1)   # 3x2, pawn stands south at r3
put(g, "TorchLamp", 7, 5)
put(g, "Shelf_North", 1, 5)
laboratory = ir("AR_M1_Laboratory_9x7", [g],
                "SimpleResearchBench -> Laboratory 60. Silences "
                "Alert_NeedResearchBench and unblocks the research queue.")

# ---------------------------------------------------------------- 5 REC HALL
# origin (104,116). Door EAST. Four rec items (28) beat the table's
# DiningRoom 12, so this is the RecRoom *and* where the colony eats.
g = shell("E")
put(g, "HorseshoesPin", 1, 1)
put(g, "HoopstoneRing", 1, 5)
put(g, "GameOfUrBoard", 3, 1)
put(g, "ChessTable", 3, 5)
put(g, "Table2x2c_North", 5, 2)     # 2x2 over c5-c6, r2-r3
put(g, "Stool_North", 7, 2)
put(g, "Stool_North", 7, 3)
put(g, "Stool_South", 5, 4)
put(g, "TorchLamp", 7, 5)
rechall = ir("AR_M1_RecHall_9x7", [g],
             "4 rec items -> RecRoom 28 > DiningRoom 12. Order matters: two "
             "rec items must stand before the table, or a lone 7 loses to 12.")

# ---------------------------------------------------------------- 6 POWER ROOM
# origin (116,116). Door WEST. Steel walls: this is the one room whose walls
# must not burn (templates/power-room.md's constraint line).
g = shell("W")
put(g, "WoodFiredGenerator_North", 2, 1)   # 2x2 over c2-c3, r1-r2
put(g, "TorchLamp", 7, 5)
powerroom = ir("AR_M1_PowerRoom_9x7", [g],
               "WoodFiredGenerator, 1200W, fed by the standing chop loop. "
               "Battery bays left empty at c6 until Batteries is researched; "
               "FirefoamPopper goes in when Firefoam lands. Walls in Steel.")

# ---------------------------------------------------------------- 7 FREEZER
# origin (116,146). Door SOUTH so the haul from the kitchen is short.
# Building_Cooler.TickRare cools Position + South.RotatedBy(Rotation) and vents
# to Position + North.RotatedBy(Rotation): Cooler_North in the NORTH wall
# therefore chills row 1 (inside) and dumps its heat outdoors. Under south-up
# this would refrigerate the sky -- the same trap templates/INDEX.md pins.
g = shell("S", door_row=4)
put(g, "Cooler_North", 2, 0)
put(g, "Cooler_North", 6, 0)
put(g, "Shelf_North", 1, 2)
put(g, "Shelf_North", 5, 2)
put(g, "Shelf_North", 1, 4)
put(g, "Shelf_North", 5, 4)
freezer = ir("AR_M1_Freezer_9x7", [g],
             "Two wall-mounted coolers, target set with `temp-set` -- a cooler "
             "defaults to 21C and would rot exactly what it was built to save.")

for name, obj in [("barracks", barracks), ("kitchen", kitchen),
                  ("workshop", workshop), ("laboratory", laboratory),
                  ("rechall", rechall), ("powerroom", powerroom),
                  ("freezer", freezer)]:
    p = os.path.join(OUT, name + ".ir.json")
    with open(p, "w") as f:
        json.dump(obj, f, indent=2)
        f.write("\n")
    print("wrote", p)
