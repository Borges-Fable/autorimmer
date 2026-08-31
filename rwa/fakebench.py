#!/usr/bin/env python3
"""A synthetic AutoRimmer bench: the protocol without the game.

`rwa` is a protocol client, and almost everything worth testing about a
protocol client is a failure mode — a stale heartbeat, a live heartbeat over a
frame-starved sim, a timeout, each error code in the taxonomy, a mangled result
file. Provoking those on a real bench means killing and restarting a 40-second
game boot and hoping the timing lands; here each one is a flag, and the run is
deterministic.

What it emulates is `Source/AutoRimmer/Poller.cs`, faithfully enough that the
client cannot tell the difference:

  * `status.json` rewritten every second, same field set, same `ts` format
    (.NET's "o" — 7 fractional digits), written tmp+rename.
  * the inbox scanned every 500ms, files younger than 250ms skipped (the
    writer-finished heuristic), consumed into `commands/done/` BEFORE the verb
    runs, exactly one `results/<id>.json` per consumed command, never dropped.
  * the same result envelope — id/op/ok/data|error/state/sid/ts — and the same
    id sanitisation, so a client that gets ids wrong misses its result here too.
  * `stale-on-restart` for whatever was left in the inbox at startup.

It is NOT a game: `data` payloads are canned, `advance` counts wall-clock
seconds rather than ticks. Nothing here belongs on a bench; it exists so the
client's error paths can be exercised without one.

  fakebench.py serve  --root DIR [--answer normal|mangle|silent|error:CODE]
                                 [--advance-secs N] [--fps N] [--tick N]
  fakebench.py status --root DIR --state ok|menu|stalled|starved|stale|down
"""

import json
import os
import re
import shutil
import signal
import sys
import time
from pathlib import Path

SID = time.strftime("%Y%m%dT%H%M%S")
MOD_VERSION = "0.1.0"
POLL_MS = 500
MIN_FILE_AGE_MS = 250

VERBS = ["advance", "catalog-dump", "digest", "find-rect", "journal", "journal-selftest",
         "landmark", "map-dump", "map-view", "nearest", "path-cost", "pause", "ping",
         "reachable", "room-at", "status", "unpause", "version"]

# Poller.cs writes DateTime.UtcNow.ToString("o"): 7 fractional digits, trailing Z.
def now_o():
    t = time.time()
    return time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(t)) + f".{int((t % 1) * 1e7):07d}Z"


def sanitize(cid):
    return re.sub(r"[^A-Za-z0-9_-]", "_", cid) or "unnamed"


def atomic(path, text):
    tmp = Path(str(path) + ".tmp")
    tmp.write_text(text)
    tmp.replace(path)


BASE_OX, BASE_OZ, BASE_W, BASE_H = 100, 100, 48, 36

# --- canned map-dump (spec 2.5) ---------------------------------------------
# A mountain base: a natural rock mass in the east with a room carved into it, a
# built four-room structure in the west, six doors, a stockpile, a growing zone,
# seven pawns of five kinds, and a band of fog along the south edge.
#
# It is deliberately HARD, and specifically hard in the ways the first live
# render failed while the original two-room fixture said everything was fine:
#   * natural rock AND constructed wall, so "is that the colony or the
#     mountain" is a real question;
#   * six doors, which means six single-cell doorway Rooms in the palette on
#     top of the five real ones — the exact thing that made the ROOMS block
#     answer "6" to a map with 5 rooms;
#   * WALL appearing twice (wood and stone) and URN three times (three stuffs),
#     so legend de-duplication is exercised;
#   * `Wardrobe` alongside `Wall`, which both yield the code WA under baseviz's
#     glyph rule — a genuine collision the renderer must resolve;
#   * ~20 thing types, enough that a truncating legend leaves codes on the map
#     unkeyed.
# The rule for changing it is the orchestrator's: make it harder, never easier.
CANNED_CATALOG = {}


def _cat(name, label, kind="thing", cat="Building", desig="Structure",
         size=(1, 1), color=None, stuff_color=None, is_stuff=False,
         stuffable=False):
    CANNED_CATALOG[name] = {
        "kind": kind, "thingCategory": cat if kind == "thing" else "",
        "designationCategory": desig, "size": list(size), "rotatable": False,
        "stuffable": stuffable, "isStuff": is_stuff, "color": color,
        "stuffColor": stuff_color, "mod": "ludeon.rimworld", "label": label,
    }


_cat("Wall", "wall", stuffable=True)
_cat("Wardrobe", "wardrobe", desig="Furniture", stuffable=True)
_cat("Door", "door", stuffable=True)
_cat("MineableSandstone", "sandstone", color=[105, 96, 84])
_cat("ElectricStove", "electric stove", desig="Production", size=(2, 1))
_cat("Bed", "bed", desig="Furniture", size=(1, 2), stuffable=True)
_cat("Table2x2c", "table", desig="Furniture", size=(2, 2), stuffable=True)
_cat("DiningChair", "dining chair", desig="Furniture", stuffable=True)
_cat("SimpleResearchBench", "simple research bench", desig="Production", size=(2, 1))
_cat("Urn", "urn", desig="Furniture", stuffable=True)
_cat("Column", "column", stuffable=True)
_cat("StandingLamp", "standing lamp", desig="Furniture")
_cat("Battery", "battery", desig="Power", size=(2, 1))
_cat("SolarGenerator", "solar generator", desig="Power", size=(4, 4))
_cat("Steel", "steel", cat="Item", desig="", color=[130, 140, 150], is_stuff=True,
     stuff_color=[130, 140, 150])
_cat("WoodLog", "wood", cat="Item", desig="", is_stuff=True, stuff_color=[133, 97, 67])
_cat("BlocksSandstone", "sandstone blocks", cat="Item", desig="", is_stuff=True,
     stuff_color=[126, 104, 94])
_cat("Plant_Cactus", "saguaro cactus", cat="Plant", desig="", color=[70, 130, 70])
_cat("MealSimple", "simple meal", cat="Item", desig="", color=[190, 170, 120])
_cat("Soil", "soil", kind="terrain", desig="Floors", color=[95, 90, 80])
_cat("WoodPlankFloor", "wooden floor", kind="terrain", desig="Floors", color=[108, 78, 55])
_cat("RoughStone", "rough stone", kind="terrain", desig="Floors", color=[88, 82, 76])
_cat("Sand", "sand", kind="terrain", desig="Floors", color=[142, 128, 100])


def rle(vals):
    """MapDumpVerbs.Plane's encoding: bare index for one cell, else count:index."""
    out, cur, n = [], None, 0
    for v in vals:
        if v == cur:
            n += 1
            continue
        if n:
            out.append(str(cur) if n == 1 else f"{n}:{cur}")
        cur, n = v, 1
    if n:
        out.append(str(cur) if n == 1 else f"{n}:{cur}")
    return ",".join(out)


# things palette indices
T_WALL_W, T_WALL_S, T_DOOR, T_ROCK, T_STOVE, T_BED, T_TABLE, T_CHAIR = 1, 2, 3, 4, 5, 6, 7, 8
T_RESEARCH, T_URN_A, T_URN_B, T_URN_C, T_COL_A, T_COL_B = 9, 10, 11, 12, 13, 14
T_LAMP, T_BATTERY, T_STEEL, T_WOOD, T_CACTUS, T_MEAL, T_WARDROBE = 15, 16, 17, 18, 19, 20, 21


def synthetic_dump(rect, layers=None):
    ox, oz, w, h = (int(v) for v in rect[:4])
    w, h = max(1, w), max(1, h)
    terr = [[1] * w for _ in range(h)]
    things = [[0] * w for _ in range(h)]
    rooms = [[0] * w for _ in range(h)]
    zones = [[0] * w for _ in range(h)]
    roof = [[0] * w for _ in range(h)]
    pawns = [[0] * w for _ in range(h)]
    fog = [[0] * w for _ in range(h)]

    def put(r, c, v, g):
        if 0 <= r < h and 0 <= c < w:
            g[r][c] = v

    def get(r, c, g):
        return g[r][c] if 0 <= r < h and 0 <= c < w else 0

    # 1. the mountain: natural rock over the east third, rough stone under it
    for r in range(h):
        for c in range(w):
            if c >= int(w * 0.62):
                put(r, c, T_ROCK, things)
                put(r, c, 3, terr)
                put(r, c, 1, roof)

    # 2. a room carved into the rock (rock walls, no built wall)
    def carve(r0, c0, r1, c1, room_idx):
        for r in range(r0, r1 + 1):
            for c in range(c0, c1 + 1):
                put(r, c, 0, things)
                put(r, c, 3, terr)
                put(r, c, room_idx, rooms)
                put(r, c, 1, roof)

    carve(8, int(w * 0.68), 15, w - 4, 5)

    # 3. the built structure: four rooms sharing walls
    def build(r0, c0, r1, c1, room_idx, wall, floor):
        for r in range(r0, r1 + 1):
            for c in range(c0, c1 + 1):
                if r in (r0, r1) or c in (c0, c1):
                    put(r, c, wall, things)
                    put(r, c, 0, rooms)
                else:
                    put(r, c, 0, things)
                    put(r, c, floor, terr)
                    put(r, c, room_idx, rooms)
                    put(r, c, 1, roof)

    build(4, 2, 14, 14, 1, T_WALL_W, 2)     # kitchen
    build(4, 14, 14, 26, 2, T_WALL_W, 2)    # bedroom (shares col 14)
    build(14, 2, 25, 14, 3, T_WALL_S, 4)    # workshop (shares row 14)
    build(14, 14, 25, 26, 4, T_WALL_S, 4)   # storage

    # 4. doors — each becomes its own single-cell room in the palette, which is
    #    the trap the ROOMS block fell into.
    doors = [(14, 7, 6), (14, 20, 7), (9, 14, 8), (19, 14, 9), (4, 8, 10), (25, 20, 11)]
    for r, c, room_idx in doors:
        put(r, c, T_DOOR, things)
        put(r, c, room_idx, rooms)
        put(r, c, 1, roof)

    # 5. furniture
    put(6, 4, T_STOVE, things); put(6, 5, T_STOVE, things)
    put(6, 8, T_TABLE, things); put(6, 9, T_TABLE, things)
    put(7, 8, T_TABLE, things); put(7, 9, T_TABLE, things)
    put(8, 8, T_CHAIR, things); put(8, 10, T_CHAIR, things)
    put(11, 4, T_MEAL, things)
    put(6, 17, T_BED, things); put(7, 17, T_BED, things)
    put(6, 21, T_BED, things); put(7, 21, T_BED, things)
    put(10, 24, T_WARDROBE, things)
    put(9, 19, T_LAMP, things)
    put(17, 5, T_RESEARCH, things); put(17, 6, T_RESEARCH, things)
    put(21, 4, T_BATTERY, things); put(21, 5, T_BATTERY, things)
    put(23, 9, T_COL_A, things)
    put(23, 12, T_COL_B, things)
    put(11, int(w * 0.72), T_URN_A, things)
    put(12, int(w * 0.74), T_URN_B, things)
    put(13, int(w * 0.71), T_URN_C, things)
    for r in range(17, 22):
        for c in range(17, 23):
            put(r, c, T_STEEL if (r + c) % 3 else T_WOOD, things)
            put(r, c, 1, zones)
    # a few cacti and loose wood outdoors
    for r, c in ((28, 4), (29, 9), (30, 15), (27, 20), (31, 6)):
        put(r, c, T_CACTUS, things)
    for r, c in ((27, 3), (28, 12)):
        put(r, c, T_WOOD, things)
    for r in range(28, 31):
        for c in range(24, 28):
            put(r, c, 2, zones)

    # 6. pawns
    for r, c, p in ((6, 6, 1), (7, 12, 2), (17, 8, 3), (11, int(w * 0.72) + 1, 4),
                    (29, 10, 5), (20, 25, 6), (30, 2, 7)):
        put(r, c, p, pawns)

    # 7. fog along the south edge
    for r in range(h - 4, h):
        for c in range(w):
            fog[r][c] = 1
    for r in range(h):
        for c in range(w):
            if fog[r][c]:
                for g in (terr, things, rooms, zones, roof, pawns):
                    g[r][c] = 0

    flat = lambda g: [v for row in g for v in row]
    planes = {"terrain": terr, "things": things, "zones": zones, "rooms": rooms,
              "roof": roof, "pawns": pawns}
    want = set(layers) if layers else set(planes)
    out_planes = {k: rle(flat(v)) for k, v in planes.items() if k in want}
    out_planes["fog"] = rle(flat(fog))

    def th(defn, stuff, label, cat="Building", door=False, size=(1, 1),
           impassable=False, rock=False):
        return {"def": defn, "stuff": stuff, "label": label, "category": cat,
                "door": door, "size": list(size), "impassable": impassable,
                "natural_rock": rock}

    def room(rid, cells, role, doorway=False, proper=True):
        return {"id": rid, "outdoors": False, "cells": cells, "role": role,
                "doorway": doorway, "proper": proper}

    pal = {
        "terrain": [None,
                    {"def": "Soil", "label": "soil"},
                    {"def": "WoodPlankFloor", "label": "wooden floor"},
                    {"def": "RoughStone", "label": "rough stone"},
                    {"def": "Sand", "label": "sand"}],
        "things": [None,
                   th("Wall", "WoodLog", "wall", impassable=True),
                   th("Wall", "BlocksSandstone", "wall", impassable=True),
                   th("Door", "WoodLog", "door", door=True),
                   th("MineableSandstone", None, "sandstone", impassable=True, rock=True),
                   th("ElectricStove", None, "electric stove", size=(2, 1)),
                   th("Bed", "WoodLog", "bed", size=(1, 2)),
                   th("Table2x2c", "WoodLog", "table", size=(2, 2)),
                   th("DiningChair", "WoodLog", "dining chair"),
                   th("SimpleResearchBench", None, "simple research bench", size=(2, 1)),
                   th("Urn", "WoodLog", "urn"),
                   th("Urn", "BlocksSandstone", "urn"),
                   th("Urn", "Steel", "urn"),
                   th("Column", "WoodLog", "column"),
                   th("Column", "BlocksSandstone", "column"),
                   th("StandingLamp", None, "standing lamp"),
                   th("Battery", None, "battery", size=(2, 1)),
                   th("Steel", None, "steel", cat="Item"),
                   th("WoodLog", None, "wood", cat="Item"),
                   th("Plant_Cactus", None, "saguaro cactus", cat="Plant"),
                   th("MealSimple", None, "simple meal", cat="Item"),
                   th("Wardrobe", "WoodLog", "wardrobe")],
        "zones": [None,
                  {"id": 3, "label": "Stockpile 1", "kind": "stockpile"},
                  {"id": 4, "label": "Rice patch", "kind": "growing", "plant": "rice plant"}],
        "rooms": [None,
                  room(11, 117, "Kitchen"), room(12, 117, "Bedroom"),
                  room(13, 120, "Workshop"), room(14, 120, "Storage"),
                  room(15, 96, None),
                  room(21, 1, None, doorway=True), room(22, 1, None, doorway=True),
                  room(23, 1, None, doorway=True), room(24, 1, None, doorway=True),
                  room(25, 1, None, doorway=True), room(26, 1, None, doorway=True)],
        "roof": [None, {"def": "RoofConstructed", "label": "constructed roof",
                        "thick": False, "natural": False}],
        "pawns": [None,
                  {"id": 215, "name": "Yun", "kind": "colonist"},
                  {"id": 218, "name": "Foxy", "kind": "colonist"},
                  {"id": 221, "name": "Slick", "kind": "colonist"},
                  {"id": 240, "name": "Barrow", "kind": "prisoner"},
                  {"id": 251, "name": "Muffalo", "kind": "tame animal"},
                  {"id": 262, "name": "Raider", "kind": "hostile"},
                  {"id": 273, "name": "Boomrat", "kind": "wild animal"}],
    }

    # labels: one anchor per non-structural building instance, exactly as
    # MapDumpVerbs does — walls and rock deliberately get none.
    labels = []
    seen = set()
    for r in range(h):
        for c in range(w):
            p = things[r][c]
            if not p or p in (T_WALL_W, T_WALL_S, T_ROCK):
                continue
            e = pal["things"][p]
            if e["category"] != "Building":
                continue
            key = (p, r // 2, c // 2) if p in (T_STOVE, T_TABLE, T_BED,
                                               T_RESEARCH, T_BATTERY) else (p, r, c)
            if key in seen:
                continue
            seen.add(key)
            labels.append({"p": p, "at": [ox + c, oz + h - 1 - r],
                           "size": e["size"], "rot": "North"})

    return {
        "channel": {"name": "map-dump", "alphabet": "baseviz-catalog/1",
                    "distinct_from": "map-view/ascii-1",
                    "note": "colours and 2-char glyphs resolve from the def catalog"},
        "origin": [ox, oz], "w": w, "h": h, "north_up": True, "clipped": False,
        "map": {"w": 250, "h": 250},
        "cells": w * h, "fogged_cells": sum(flat(fog)), "fog_respected": True,
        "encoding": "rle-v1",
        "palettes": {k: v for k, v in pal.items() if k in want},
        "planes": out_planes,
        "runs": {k: len(v.split(",")) if v else 0 for k, v in out_planes.items()},
        "labels": labels,
    }


class Bench:
    def __init__(self, root, args):
        self.root = Path(root)
        self.inbox = self.root / "commands"
        self.done = self.inbox / "done"
        self.results = self.root / "results"
        self.journal = self.root / "journal"
        for d in (self.inbox, self.done, self.results, self.journal):
            d.mkdir(parents=True, exist_ok=True)
        self.answer = args.get("answer", "normal")
        self.advance_secs = float(args.get("advance_secs", 1.0))
        self.fps = float(args.get("fps", 30.0))
        self.tick = int(args.get("tick", 1000))
        self.game_loaded = args.get("game_loaded", True)
        self.paused = True
        self.speed = "Paused"
        self.active_advance = None
        self.seq = 0
        self.journal_file = self.journal / (SID + ".ndjson")
        self.emit("session", {"kind": "boot", "mod": MOD_VERSION,
                              "game": "fakebench", "bench": "synthetic"})
        # Poller.Init: anything left in the inbox from a previous process is
        # consumed with an explicit error, never replayed.
        for stale in sorted(self.inbox.glob("*.json")):
            cid = stale.stem
            self.consume(stale)
            self.write_result(cid, "?", False, code="stale-on-restart",
                              detail="command file predates this game session")

    # --- journal -------------------------------------------------------------
    def emit(self, etype, payload):
        self.seq += 1
        line = json.dumps({"seq": self.seq, "tick": self.tick, "wall": now_o(),
                           "type": etype, "payload": payload}, ensure_ascii=False)
        with self.journal_file.open("a") as fh:
            fh.write(line + "\n")

    # --- files ---------------------------------------------------------------
    def consume(self, path):
        dest = self.done / path.name
        if dest.exists():
            dest.unlink()
        shutil.move(str(path), str(dest))

    def write_status(self):
        # At the main menu the mod has never published a snapshot, so
        # Runtime.GameState is still its zero value — speed "" and tick 0. That
        # is exactly what distinguishes "no game loaded" from "a game was
        # loaded and the main thread has stopped", so it has to be faithful.
        st = {
            "ts": now_o(), "sid": SID, "mod": MOD_VERSION,
            "gameLoaded": bool(self.game_loaded), "paused": self.paused,
            "speed": self.speed if self.game_loaded else "",
            "tick": self.tick if self.game_loaded else 0,
            "fps": round(self.fps, 3) if self.game_loaded else 0,
            "activeOp": ("advance:" + self.active_advance["id"]) if self.active_advance else None,
        }
        if self.active_advance:
            a = self.active_advance
            st["advance"] = {"id": a["id"], "ticks_done": a["done"], "target": a["target"]}
        st["thermal"] = {"c": 75.5, "scale": 1}
        atomic(self.root / "status.json", json.dumps(st, separators=(",", ":")))

    def write_result(self, cid, op, ok, data=None, code=None, detail=None):
        env = {"id": cid, "op": op, "ok": ok}
        if ok:
            env["data"] = data if data is not None else {}
        else:
            env["error"] = {"code": code, "detail": detail}
        env["state"] = {"gameLoaded": bool(self.game_loaded), "tick": self.tick,
                        "paused": self.paused}
        env["sid"] = SID
        env["ts"] = now_o()
        path = self.results / (sanitize(cid) + ".json")
        if self.answer == "mangle":
            atomic(path, '{"id":"' + cid + '","op":"' + op + '","ok":tr')  # truncated on purpose
        else:
            atomic(path, json.dumps(env, ensure_ascii=False, separators=(",", ":")))

    # --- verbs ---------------------------------------------------------------
    # Poller.ScanInbox hands only MainThread verbs to the game component;
    # status/version/journal run on the poller thread and so are answerable
    # while an advance is in flight and at the main menu.
    OFF_THREAD = ("status", "version", "journal")

    def execute(self, cid, op, args):
        if self.active_advance and op not in self.OFF_THREAD and op != "pause":
            return self.write_result(cid, op, False, code="busy",
                                     detail=f"advance '{self.active_advance['id']}' in flight "
                                            f"({self.active_advance['done']} ticks done)")
        if self.answer.startswith("error:"):
            return self.write_result(cid, op, False, code=self.answer.split(":", 1)[1],
                                     detail="injected by fakebench --answer")
        if op == "ping":
            if not self.game_loaded:
                return self.write_result(cid, op, False, code="no-active-game",
                                         detail="load a save first; this verb runs at the "
                                                "in-game safe point")
            data = {"pong": True}
            if "echo" in args:
                data["echo"] = args["echo"]
            return self.write_result(cid, op, True, data)
        if op == "status":
            return self.write_result(cid, op, True, {
                "gameLoaded": bool(self.game_loaded), "paused": self.paused,
                "speed": self.speed, "tick": self.tick, "fps": round(self.fps, 3),
                "activeOp": None, "verbs": VERBS, "root": str(self.root)})
        if op == "version":
            return self.write_result(cid, op, True, {
                "game": "1.6.4871 rev600", "mod": MOD_VERSION,
                "bench": "fakebench", "sid": SID})
        if op == "journal":
            since = int(args.get("since_seq", 0))
            events, last = [], 0
            for line in self.journal_file.read_text().splitlines():
                evt = json.loads(line)
                last = max(last, evt["seq"])
                if evt["seq"] > since:
                    events.append(evt)
            return self.write_result(cid, op, True, {
                "file": str(self.journal_file), "count": len(events),
                "truncated": False, "last_seq": last, "events": events})
        if op == "digest":
            return self.write_result(cid, op, True, {
                "time": {"tick": self.tick, "paused": self.paused, "speed": self.speed,
                         "day_of_season": 11, "season": "Spring", "year": 5500,
                         "hour": 6, "weather": "clear", "outdoor_c": 10},
                "alerts": {"active": [{"id": "Alert_NeedColonistBeds",
                                       "label": "Need colonist beds", "priority": "High"}],
                           "more": 0},
                "colonists": [{"name": "Xitral", "job": "wandering.", "mood_pct": 57,
                               "mood_arrow": 1, "drafted": False, "room": "outside"}],
                "resources": {"food_days": 4.2, "meals": 12, "steel": 340},
                "power": {"net_w": 120}, "threats": {"danger": "None"},
                "changed": {"since": args.get("since", 0), "counts": {}}})
        if op == "advance":
            if not self.game_loaded:
                return self.write_result(cid, op, False, code="no-active-game",
                                         detail="load a save first")
            if self.active_advance:
                return self.write_result(cid, op, False, code="busy",
                                         detail=f"advance '{self.active_advance['id']}' in flight")
            if "ticks" not in args and "until" not in args:
                return self.write_result(cid, op, False, code="bad-args",
                                         detail="advance needs 'ticks' or 'until'")
            target = int(args.get("ticks", -1))
            self.active_advance = {"id": cid, "op": op, "target": target, "done": 0,
                                   "until": args.get("until"),
                                   "ends": time.time() + self.advance_secs,
                                   "start": time.time(), "start_tick": self.tick}
            return None  # deferred, exactly like TimeDriver
        if op == "pause":
            was = self.active_advance is not None
            if was:
                self.finish_advance("interrupted")
            return self.write_result(cid, op, True, {"was_advancing": was, "paused": True})
        if op == "landmark":
            # Listing only. `set`/`remove` would need persistent state this
            # bench deliberately does not keep.
            return self.write_result(cid, op, True, {
                "landmarks": {"base-center": [110, 116], "kitchen-door": [106, 111]}})
        if op == "catalog-dump":
            path = self.root / "catalog.json"
            atomic(path, json.dumps({"defs": CANNED_CATALOG}))
            return self.write_result(cid, op, True, {
                "path": str(path), "file": "catalog.json", "schema": 1,
                "defs": len(CANNED_CATALOG), "things": len(CANNED_CATALOG) - 2,
                "terrains": 2, "bytes": path.stat().st_size})
        if op == "map-dump":
            if "rect" not in args and "around" not in args and not args.get("whole_map"):
                return self.write_result(cid, op, False, code="bad-args",
                                         detail="map-dump needs 'rect', 'around' or whole_map:true")
            rect = args.get("rect") or [BASE_OX, BASE_OZ, BASE_W, BASE_H]
            return self.write_result(cid, op, True, synthetic_dump(rect, args.get("layers")))
        return self.write_result(cid, op, False, code="unknown-op",
                                 detail="known ops: " + ", ".join(VERBS))

    def finish_advance(self, reason):
        a = self.active_advance
        self.active_advance = None
        wall = time.time() - a["start"]
        self.write_result(a["id"], "advance", True, {
            "reason": reason, "tick": self.tick, "ticks_elapsed": a["done"],
            "wall_seconds": round(wall, 4),
            "avg_tps": round(a["done"] / wall, 4) if wall else 0,
            "max_tps_effective": 1000, "journal_seq": [], "slower_spans": [],
            "thermal_c": 75.5, "thermal_scale": 1})

    def step_advance(self):
        a = self.active_advance
        if not a:
            return
        # Ticks are faked from wall time; the point is that status.json shows
        # progress and the result arrives late, not that the number is real.
        elapsed = time.time() - a["start"]
        a["done"] = int(elapsed * 1000)
        self.tick = a["start_tick"] + a["done"]
        if a["target"] >= 0 and a["done"] >= a["target"]:
            a["done"] = a["target"]
            self.tick = a["start_tick"] + a["target"]
            return self.finish_advance("ticks")
        if time.time() >= a["ends"]:
            if a["until"]:
                self.emit("letter", {"def": "NeutralEvent", "label": "fakebench letter"})
                return self.finish_advance("letter")
            return self.finish_advance("ticks")

    # --- loop ----------------------------------------------------------------
    def scan(self):
        for path in sorted(self.inbox.glob("*.json")):
            age_ms = (time.time() - path.stat().st_mtime) * 1000
            if age_ms < MIN_FILE_AGE_MS:
                continue
            try:
                text = path.read_text()
            except OSError:
                continue
            cid = path.stem
            self.consume(path)
            try:
                obj = json.loads(text)
                if not isinstance(obj, dict):
                    raise ValueError("not an object")
            except ValueError:
                self.write_result(cid, "?", False, code="bad-json", detail=None)
                continue
            cid = obj.get("id", cid)
            op = obj.get("op")
            if op is None:
                self.write_result(cid, "?", False, code="unknown-op",
                                  detail="envelope has no 'op'")
                continue
            args = obj.get("args") or {}
            if not isinstance(args, dict):
                self.write_result(cid, op, False, code="bad-args",
                                  detail="'args' must be an object")
                continue
            if self.answer == "silent":
                continue  # consumed, never answered: the timeout path
            self.execute(cid, op, args)

    def run(self):
        last_status = 0.0
        while True:
            self.scan()
            self.step_advance()
            if time.time() - last_status >= 1.0:
                last_status = time.time()
                self.write_status()
            time.sleep(POLL_MS / 1000.0)


STATES = {
    # state -> (status.json fields, or None to remove the file entirely)
    "ok": {"gameLoaded": True, "paused": True, "speed": "Paused", "tick": 64607, "fps": 30.023},
    "menu": {"gameLoaded": False, "paused": False, "speed": "", "tick": 0, "fps": 0},
    "stalled": {"gameLoaded": False, "paused": True, "speed": "Paused", "tick": 12345, "fps": 1.2},
    "starved": {"gameLoaded": True, "paused": False, "speed": "Normal", "tick": 4102, "fps": 1.7},
    "stale": {"gameLoaded": True, "paused": True, "speed": "Paused", "tick": 64607,
              "fps": 30.023, "_age": 3600},
    "down": None,
}


def write_state(root, state):
    root = Path(root)
    root.mkdir(parents=True, exist_ok=True)
    path = root / "status.json"
    fields = STATES[state]
    if fields is None:
        if path.exists():
            path.unlink()
        return
    age = fields.pop("_age", 0)
    t = time.time() - age
    st = {"ts": time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(t)) + f".{int((t % 1) * 1e7):07d}Z",
          "sid": SID, "mod": MOD_VERSION}
    st.update(fields)
    st["activeOp"] = None
    st["thermal"] = {"c": 75.5, "scale": 1}
    atomic(path, json.dumps(st, separators=(",", ":")))


def main(argv):
    if not argv:
        print(__doc__)
        return 2
    mode, opts = argv[0], {}
    i = 1
    while i < len(argv):
        if argv[i].startswith("--"):
            key = argv[i][2:].replace("-", "_")
            if i + 1 < len(argv) and not argv[i + 1].startswith("--"):
                opts[key] = argv[i + 1]
                i += 2
            else:
                opts[key] = True
                i += 1
        else:
            i += 1
    root = opts.get("root")
    if not root:
        print("fakebench: --root DIR is required", file=sys.stderr)
        return 2
    if mode == "status":
        state = opts.get("state", "ok")
        if state not in STATES:
            print(f"fakebench: unknown state {state!r} ({'|'.join(STATES)})", file=sys.stderr)
            return 2
        write_state(root, state)
        return 0
    if mode == "serve":
        if opts.get("game_loaded") in ("false", "0"):
            opts["game_loaded"] = False
        bench = Bench(root, opts)
        signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))
        bench.write_status()
        print(f"fakebench serving {root} (sid {SID}, answer={bench.answer})", flush=True)
        bench.run()
        return 0
    print(f"fakebench: unknown mode {mode!r}", file=sys.stderr)
    return 2


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except KeyboardInterrupt:
        sys.exit(0)
