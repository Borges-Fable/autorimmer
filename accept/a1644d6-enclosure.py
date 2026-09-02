#!/usr/bin/env python3
"""Acceptance runner for a1644d6 (B-3) — a layout that never encloses.

Same protocol, helpers and exit codes as `accept/1adc737-place-layout.py` and
`accept/280fb78-wake-halts.py`; read either header first, especially the SHAPE
CONTRACT note — `eq(..., None)` passes on an absent key, so phase 0 proves every
dig path before any later phase leans on it, and `shape()` is what closes it.

    ./accept/a1644d6-enclosure.py              # phases 0-6
    ./accept/a1644d6-enclosure.py --phase 2    # one phase (0 always runs)
    ./accept/a1644d6-enclosure.py --dry-run    # print the plan, send nothing
    ./accept/a1644d6-enclosure.py --selftest   # phase 9 only: NO bench needed

Start the bench first (`_RimWorld-Agent/run-agent.sh`) with a colony on a map
that has open buildable ground, `devMode = True` (phases 2-3 use `dev:destroy`
and `dev:spawn-thing`), and leave it paused.

WHAT THIS IS TESTING, in one sentence: run `m1-20260901` placed a freezer, built
it, never CLOSED it, and no read the agent had any reason to make said so — so
the colony had no larder and starved, and this suite proves the agent is now
told, without asking cell by cell.

THE TWO MECHANISMS, AND THEY ARE PROVED SEPARATELY. That separation is the
point of the fixture, not a nicety:

  1. NOT ENCLOSED — `Verse/Room.cs` `ProperRoom` is false whenever the space
     leaks to the map edge. That is the literal `outdoors: true, cells: 60082`
     the run measured: the whole outdoors, not a 60,000-cell freezer.
  2. ENCLOSED AND THERMALLY OUTDOORS — `UsesOutdoorTemperature` is
     `TouchesMapEdge || OpenRoofCount >= CeilToInt(CellCount * 0.25f)`, so a
     SEALED room missing a quarter of its roof sits on the outdoor temperature
     with `ProperRoom` still true. For a freezer that is the same dead colony,
     and a check reporting only (1) would pass it clean.

  PHASE 1 stages (2) ALONE: an instant-mode room is sealed the moment it is
  placed and completely unroofed, so `enclosed:true` and `uses_outdoor_temp:true`
  arrive in the same envelope with no wall gap anywhere. PHASE 2 then stages (1)
  alone by destroying one declared wall cell, and PHASE 3 restores it and asserts
  `enclosed` flips back while `uses_outdoor_temp` does NOT. Three states off one
  room, and the second mechanism is observable with the first satisfied.

THE FIXTURE IS DELIBERATELY REUSABLE. `261f2e9` phase 6 has never had a sealed
roofed room to read a temperature off; phase 4 builds exactly that and reads
`room {id}` on it, so the same bench run answers both.

WHY ONE WALL IS DESTROYED RATHER THAN LEFT UNBUILT. The check keys on WHAT
STANDS AT THE CELL, not on why nothing does — a cancelled element, a destroyed
wall and a wall nobody has built yet are the same hole in the same room. Leaving
an element out of the `elements` list instead would not test this at all: it
would leave the cell UNDECLARED, and an undeclared cell is not a gap, it is a
layout that never asked for a wall there. Phase 5 covers the literally-unbuilt
reading with a blueprint-mode layout, whose gaps report `standing: "blueprint"`.

PHASE 6 IS THE CRY-WOLF GUARD and it is the check most worth keeping. A
structural-regression report that fires on things that are not regressions is
worse than silence, because the agent learns to ignore it. A straight defensive
wall is not a room that failed, so it must NOT appear in the roll-up: its
declared shell encloses nothing, `intends_room` is false, and it stays false on
day 40 as surely as on day 1.

IT LEAVES A BUILDING ON THE BENCH. Phases 1-4 spawn a real room in instant mode
and do not remove it — that room is the fixture. Phase 5 cancels its own
blueprints; phase 6 leaves a short wall. Run it on a bench you are willing to
dirty.

Exit 0 = every check passed · 1 = at least one FAIL · 2 = a fixture
precondition could not be met, which is NOT a spec failure and says so.
"""

import argparse
import json
import os
import re
import sys
import time

# --------------------------------------------------------------------- setup --

VAULT = os.environ.get("RIMWORLD_VAULT", "/home/dorian/projects/rimworld")
DEFAULT_ROOT = os.environ.get("RWA_ROOT") or os.path.join(
    VAULT,
    "_RimWorld-Agent/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/AutoRimmer",
)
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

GREEN, RED, YELLOW, CYAN, DIM, OFF = (
    "\033[32m", "\033[31m", "\033[33m", "\033[36m", "\033[2m", "\033[0m",
)
if not sys.stdout.isatty():
    GREEN = RED = YELLOW = CYAN = DIM = OFF = ""

ARGS = None
FAILS = []
CHECKS = 0
S = {}
SEQ = 0

# The room, INLINE. Every file in accept/ stands alone and runs from a bare
# checkout, so the grid is here rather than read out of templates/; check 0.6
# then compares it against `templates/bedroom.ir.json` when that file is
# reachable, so the copy cannot drift. Row 0 is NORTH (templates/INDEX.md pin 0).
#
# It is the 5x7 bedroom shell with the bed and lamp dropped: this suite is about
# the SHELL, and furniture is one more thing that can refuse a cell.
ROOM = [
    ["Wall", "Wall", "Wall", "Wall", "Wall"],
    ["Wall", ".", ".", ".", "Wall"],
    ["Wall", ".", ".", ".", "Wall"],
    ["Wall", ".", ".", ".", "Wall"],
    ["Wall", ".", ".", ".", "Wall"],
    ["Wall", ".", ".", ".", "Wall"],
    ["Wall", "Wall", "Door", "Wall", "Wall"],
]
RW, RH = 5, 7
# The interior the DECLARATION encloses: 3 wide by 5 tall = 15 cells. Named here
# because phase 1 asserts on it and a number the suite computed from the same
# code under test would prove nothing.
ROOM_INTERIOR = 15
ROOM_SHELL = 20        # 5*7 rect perimeter = 20 cells, all Wall or Door

# Phase 6's non-room: a straight wall, which declares a shell and encloses
# nothing. The cry-wolf guard.
WALL_RUN = 5

MARGIN = 3             # clear ground the search demands around the room
STUFF = "WoodLog"      # cheap, always present, and MadeFromStuff
ROT_WORDS = ("North", "East", "South", "West")

# Phase 4's roof wait. `AutoBuildRoofAreaSetter.TryGenerateAreaNow` queues the
# roof area for an enclosed player room by itself and colonists then build it,
# so this is real colonist work and is bounded rather than waited on forever.
ROOF_BUDGET = 30000
ROOF_STEP = 5000

WHY_CASUALTY = ("accept/a1644d6-enclosure.py: a casualty halt would end the "
                "advance this phase uses to let colonists roof the fixture room; "
                "722c951 owns the casualty halt, this suite owns the roof")


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=300):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acca1644d6-%03d-%s" % (SEQ, slug)
    envelope = json.dumps({"id": cid, "op": op, "args": args or {}},
                          separators=(",", ":"))
    if ARGS.dry_run:
        print("    would send: %s" % envelope[:260])
        return {"ok": True, "op": op, "data": {}, "_dry": True}

    inbox = os.path.join(ARGS.root, "commands", cid + ".json")
    result = os.path.join(ARGS.root, "results", cid + ".json")
    if os.path.exists(result):
        os.remove(result)
    with open(inbox, "w", encoding="utf-8", newline="") as fh:
        fh.write(envelope)

    deadline = time.time() + timeout
    while time.time() < deadline:
        if os.path.exists(result):
            time.sleep(0.06)
            try:
                with open(result, encoding="utf-8") as fh:
                    env = json.load(fh)
                if ARGS.echo:
                    print("    <- %s" % json.dumps(env, separators=(",", ":"))[:1600])
                return env
            except (ValueError, OSError):
                time.sleep(0.12)
                continue
        time.sleep(0.2)
    return {"ok": False, "op": op,
            "error": {"code": "acc-timeout",
                      "detail": "no results/%s.json within %ss" % (cid, timeout)}}


def dig(obj, path, default=None):
    cur = obj
    for part in path.split("."):
        if cur is None:
            return default
        if isinstance(cur, list):
            try:
                i = int(part)
            except ValueError:
                return default
            if i >= len(cur):
                return default
            cur = cur[i]
            continue
        if isinstance(cur, dict):
            if part not in cur:
                return default
            cur = cur[part]
            continue
        return default
    return default if cur is None else cur


def has_key(obj, path):
    """dig() cannot tell `absent` from `present and null`, and this suite cares
    more than most: `enclosed` is DELIBERATELY null when the question could not
    be answered (fog, no room), and `eq(..., None)` would pass on a build that
    never published the key at all."""
    parts = path.split(".")
    cur = obj
    for part in parts[:-1]:
        if isinstance(cur, list):
            try:
                cur = cur[int(part)]
            except (ValueError, IndexError):
                return False
        elif isinstance(cur, dict):
            if part not in cur:
                return False
            cur = cur[part]
        else:
            return False
    return isinstance(cur, dict) and parts[-1] in cur


def as_list(v):
    if v is None:
        return []
    return v if isinstance(v, list) else [v]


def show(v):
    return "null" if v is None else json.dumps(v, separators=(",", ":"))[:600]


# ------------------------------------------------------------------- asserts --

def check(num, what, ok, expected, actual):
    global CHECKS
    CHECKS += 1
    if ARGS.dry_run:
        print("  %-7s EXPECT  %s: %s" % (num, what, expected))
        return
    if ok:
        print("  %s%-7s PASS    %s%s" % (GREEN, num, what, OFF))
        return
    print("  %s%-7s FAIL    %s%s" % (RED, num, what, OFF))
    print("          expected: %s" % expected)
    print("          actual:   %s" % show(actual))
    FAILS.append(num)


def eq(num, what, env, path, want):
    got = dig(env, path)
    ok = (want is None and got is None) or got == want
    check(num, "%s (%s)" % (what, path), ok, show(want), got)


def ge(num, what, env, path, want):
    got = dig(env, path)
    ok = isinstance(got, (int, float)) and not isinstance(got, bool) and got >= want
    check(num, "%s (%s)" % (what, path), ok, ">= %s" % want, got)


def contains(num, what, env, path, needle):
    got = dig(env, path)
    ok = isinstance(got, str) and needle.lower() in got.lower()
    check(num, "%s (%s)" % (what, path), ok, "a string containing %r" % needle, got)


def shape(num, verb, env, path, kind=None):
    ok = has_key(env, path)
    want = "the key to be present"
    if ok and kind is not None:
        ok = isinstance(dig(env, path), kind)
        # `kind` may be a tuple — a whole number comes back from MiniJson as int
        # OR float, and tuple has no __name__ (the crash 722c951 took at 3.14).
        want += " and a %s" % (kind.__name__ if isinstance(kind, type)
                               else "/".join(k.__name__ for k in kind))
    check(num, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def absent(num, what, env, path):
    check(num, what, not has_key(env, path), "the key to be ABSENT", dig(env, path))


def note(num, text):
    print("  %s%-7s NOTE    %s%s" % (YELLOW, num, text, OFF))


def precondition(num, what, ok, detail):
    if ARGS.dry_run:
        print("  %s%-7s NEEDS   %s%s" % (CYAN, num, what, OFF))
        return
    if ok:
        print("  %s%-7s OK      precondition: %s%s" % (DIM, num, what, OFF))
        return
    print("  %s%-7s SKIP    precondition NOT MET: %s%s" % (YELLOW, num, what, OFF))
    print("          %s" % detail)
    print("          This is a FIXTURE gap, not a failure of a1644d6.")
    sys.exit(2)


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


# ------------------------------------------------------------ the expansion --

def split_token(tok):
    """`Cooler_North` -> ("Cooler", "North"). ONLY the four Rot4 words are a
    suffix (templates/INDEX.md pin 2: the suffix is the Rot4 value verbatim)."""
    if "_" in tok:
        head, _, tail = tok.rpartition("_")
        if head and tail in ROT_WORDS:
            return head, tail
    return tok, None


def expand(grid, origin, w, h):
    """The grid resolved against a SOUTH-WEST origin, as the verb's `elements`.

    Row 0 is north, so grid row r is map z = oz + h - 1 - r. Each element's `at`
    is the footprint's north-west cell and the mod converts. A second copy of
    `rwa place-layout`'s expander on purpose: this file must run from a bare
    checkout with no import of rwa."""
    ox, oz = origin
    els = []
    for r, row in enumerate(grid):
        for c, tok in enumerate(row):
            tok = (tok or "").strip()
            if not tok or tok == ".":
                continue
            defname, rot = split_token(tok)
            el = {"def": defname, "at": [ox + c, oz + h - 1 - r],
                  "label": "[%d,%d] %s" % (r, c, tok)}
            if rot:
                el["rot"] = rot
            els.append(el)
    return els


# ---- the DECLARED-INTERIOR model, reimplemented here on purpose --------------
#
# `LayoutEnclosure.FloodInterior` decides whether a layout DECLARES a room by
# flooding its own rect, blocked by the declared wall and door cells: a component
# that never reaches the rect perimeter is a declared interior. This is a second
# implementation of that rule in Python, so phase 9 can check the mod's answer
# against something that was not compiled from the same source — and can run the
# rule over the SHIPPED templates with no bench at all.

SHELL_TOKENS = ("Wall", "Door", "Cooler", "Vent", "Autodoor")


def is_shell_token(tok):
    """`ThingDef.IsDoor`, or `Fillage == Full`. Cooler and Vent are fillPercent
    1.0 / Impassable in Core's Buildings_Temperature.xml and DO seal a wall
    slot; Heater is 0.4 / PassThroughOnly and does not."""
    head, _ = split_token((tok or "").strip())
    return head in SHELL_TOKENS


def declared_interior(grid, w, h):
    """(components, interior_cells, shell_cells) for a token grid.

    Returns grid coordinates (r, c); the caller does not need map cells for the
    intent question, which is exactly the point — no game state is involved."""
    shell = {(r, c) for r, row in enumerate(grid)
             for c, tok in enumerate(row) if is_shell_token(tok)}
    seen, comps, interior = set(), 0, []
    for r in range(h):
        for c in range(w):
            if (r, c) in shell or (r, c) in seen:
                continue
            stack, comp, escapes = [(r, c)], [], False
            seen.add((r, c))
            while stack:
                cr, cc = stack.pop()
                comp.append((cr, cc))
                if cr in (0, h - 1) or cc in (0, w - 1):
                    escapes = True
                for dr, dc in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    nr, nc = cr + dr, cc + dc
                    if not (0 <= nr < h and 0 <= nc < w):
                        continue
                    if (nr, nc) in shell or (nr, nc) in seen:
                        continue
                    seen.add((nr, nc))
                    stack.append((nr, nc))
            if escapes:
                continue
            comps += 1
            interior.extend(comp)
    return comps, interior, shell


# ---------------------------------------------------------------- the fixture --

def place(grid, origin, w, h, mode="blueprint", name="acc-enclosure", **extra):
    args = {
        "elements": expand(grid, origin, w, h),
        "origin": list(origin),
        "size": [w, h],
        "mode": mode,
        "name": name,
        "stuff_map": {"*": STUFF},
    }
    args.update(extra)
    return send("place-layout", args)


def find_site(num, what, w, h):
    """A clear box big enough for the room PLUS margin — the siting discipline
    this project settled on after the granite bench (git-bug c718e4a)."""
    e = send("find-rect", {"w": w + 2 * MARGIN, "h": h + 2 * MARGIN, "max": 3})
    at = dig(e, "data.candidates.0.at")
    if ARGS.dry_run:
        at = [100, 100]
    precondition(num, what, isinstance(at, list) and len(at) == 2,
                 "find-rect found no clear %dx%d box — load a colony with open "
                 "buildable ground" % (w + 2 * MARGIN, h + 2 * MARGIN))
    origin = [at[0] + MARGIN, at[1] + MARGIN]
    print("  %ssite: rect at %s, layout origin %s%s" % (DIM, at, origin, OFF))
    return origin


def enclosure(layout_id):
    """`construction {layout_id}` -> the whole envelope, so a caller can assert
    on the enclosure block AND on the element rollup it sits beside."""
    return send("construction", {"layout_id": layout_id})


def wall_at(layout_id, cell):
    """The thingIDNumber of the layout element standing at `cell`.

    Taken from the layout's OWN placement rows (`thing_id`, which
    `Placements.Answer` fills from the completion hook or from the cell), so the
    suite never has to guess which wall it is about to destroy."""
    e = enclosure(layout_id)
    for row in as_list(dig(e, "data.items")):
        if not isinstance(row, dict):
            continue
        if row.get("at") == list(cell) and row.get("def") == "Wall":
            return row.get("thing_id")
    return None


def advance(ticks):
    return send("advance", {"ticks": ticks, "through_casualties": WHY_CASUALTY},
                timeout=600)


def drain_journal():
    """Read the delta so the NEXT advance is not refused `unread-journal`."""
    return dig(send("journal", {"since_seq": 0, "limit": 2000}), "data.read_watermark")


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 — the bench, and THE SHAPE CONTRACT on the roll-up")
    e = send("status")
    precondition("0.1", "the bench answers `status`",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "no status envelope — start _RimWorld-Agent/run-agent.sh")

    e = send("journal", {"since_seq": 999999999, "limit": 1})
    shape("0.2", "journal", e, "data.last_seq")
    S["seq0"] = dig(e, "data.last_seq") or 0

    # THE TURN-LEVEL READ. Every key here is asserted PRESENT before anything is
    # placed, because the whole defect was a question that could not be asked:
    # an absent `layouts_unenclosed` and an empty one must not look alike.
    e = send("rooms")
    S["rooms0"] = e
    shape("0.3a", "rooms", e, "data.layouts_unenclosed", list)
    shape("0.3b", "rooms", e, "data.layouts_total", (int, float))
    shape("0.3c", "rooms", e, "data.layouts_checked", (int, float))
    shape("0.3d", "rooms", e, "data.layouts_failing", (int, float))
    shape("0.3e", "rooms", e, "data.layouts_cap", (int, float))
    check("0.3f", "`rooms` still answers its own question too",
          isinstance(dig(e, "data.list"), list),
          "data.list a list", dig(e, "data.list"))

    S["origin"] = find_site("0.4", "a clear box for the fixture room", RW, RH)

    # THE INLINE GRID IS THE TEMPLATE'S SHELL. A rehearsal of a layout that has
    # drifted from the one templates/ ships is a rehearsal of nothing.
    path = os.path.join(REPO, "templates", "bedroom.ir.json")
    if os.path.exists(path) and not ARGS.dry_run:
        with open(path, encoding="utf-8") as fh:
            ir = json.load(fh)
        tmpl = (ir.get("layers") or [None])[0]
        same_shell = (ir.get("size") == [RW, RH] and isinstance(tmpl, list)
                      and len(tmpl) == RH
                      and all(is_shell_token(a) == is_shell_token(b)
                              for tr, rr in zip(tmpl, ROOM)
                              for a, b in zip(tr, rr)))
        check("0.5", "this suite's shell IS templates/bedroom.ir.json's shell",
              same_shell, "the same wall/door cells", tmpl)
    else:
        note("0.5", "templates/bedroom.ir.json not reachable from %s" % REPO)


# ------------------------------------------------------------------- phase 1 --

def phase1():
    banner("PHASE 1 — MECHANISM 2 ALONE: sealed, and thermally outdoors")

    # Instant mode: every wall stands the moment the call returns, so the room
    # is SEALED with no gap anywhere — and it is completely unroofed, because a
    # roof is a designation the colony has not built yet
    # (`AutoBuildRoofAreaSetter.TryGenerateAreaNow` runs NEXT tick and only
    # QUEUES the work). That is `ProperRoom` true and `UsesOutdoorTemperature`
    # true in one envelope, which is the whole of mechanism 2.
    e = place(ROOM, S["origin"], RW, RH, mode="instant", name="acc-enclosure-room")
    eq("1.1a", "the fixture room places", e, "data.ok", True)
    ge("1.1b", "every shell cell went down", e, "data.placed_count", ROOM_SHELL)
    lid = dig(e, "data.layout_id")
    precondition("1.2", "the placement minted a layout id", bool(lid) or ARGS.dry_run,
                 "place-layout returned no layout_id; nothing downstream can be asked")
    S["lid"] = lid or "ly-dry"

    # One tick, so the region rebuild the placement dirtied has certainly run.
    # `Map.MapUpdate` calls TryRebuildDirtyRegionsAndRooms every frame and the
    # bench is PAUSED, so this is not superstition — it is the same
    # next-tick discipline 1adc737's header spells out for the roof.
    advance(1)
    drain_journal()

    e = enclosure(S["lid"])
    S["e1"] = e
    # --- shapes first, every one of them, before a single value is read -------
    shape("1.3a", "construction", e, "data.enclosure", dict)
    shape("1.3b", "construction", e, "data.enclosure.intends_room")
    shape("1.3c", "construction", e, "data.enclosure.enclosed")
    shape("1.3d", "construction", e, "data.enclosure.uses_outdoor_temp")
    shape("1.3e", "construction", e, "data.enclosure.open_shell_cells", (int, float))
    shape("1.3f", "construction", e, "data.enclosure.shell_complete")
    shape("1.3g", "construction", e, "data.enclosure.gaps", list)
    shape("1.3h", "construction", e, "data.enclosure.unroofed_cells", (int, float))
    shape("1.3i", "construction", e, "data.enclosure.unroofed", list)
    shape("1.3j", "construction", e, "data.enclosure.rooms", list)
    shape("1.3k", "construction", e, "data.enclosure.failing")
    shape("1.3l", "construction", e, "data.enclosure.interior_cells", (int, float))
    shape("1.3m", "construction", e, "data.enclosure.shell_cells", (int, float))
    shape("1.3n", "construction", e, "data.enclosure.declared_rooms", (int, float))
    shape("1.3o", "construction", e, "data.enclosure.rooms.0.proper")
    shape("1.3p", "construction", e, "data.enclosure.rooms.0.uses_outdoor_temp")
    shape("1.3q", "construction", e, "data.enclosure.rooms.0.open_roof_cells", (int, float))

    # --- the declaration -----------------------------------------------------
    eq("1.4a", "the layout declares a room", e, "data.enclosure.intends_room", True)
    eq("1.4b", "one declared room", e, "data.enclosure.declared_rooms", 1)
    eq("1.4c", "and the flood found the 3x5 interior", e,
       "data.enclosure.interior_cells", ROOM_INTERIOR)
    eq("1.4d", "…over the 20-cell declared shell", e,
       "data.enclosure.shell_cells", ROOM_SHELL)

    # --- MECHANISM 1 SATISFIED ----------------------------------------------
    eq("1.5a", "ProperRoom: the room IS enclosed", e, "data.enclosure.enclosed", True)
    eq("1.5b", "no wall or door cell is open", e, "data.enclosure.open_shell_cells", 0)
    eq("1.5c", "…and the shell is complete", e, "data.enclosure.shell_complete", True)
    eq("1.5d", "no gap is named, because there is none", e, "data.enclosure.gaps", [])

    # --- MECHANISM 2, WITH MECHANISM 1 SATISFIED — the acceptance bullet ----
    eq("1.6a", "UsesOutdoorTemperature: it is thermally OUTDOORS", e,
       "data.enclosure.uses_outdoor_temp", True)
    ge("1.6b", "…and the roof holes are named, not merely counted", e,
       "data.enclosure.unroofed_cells", 1)
    check("1.6c", "the two flags DISAGREE in this envelope — which is the whole "
                  "of the second mechanism, and a check reporting only "
                  "ProperRoom would have passed this room clean",
          dig(e, "data.enclosure.enclosed") is True
          and dig(e, "data.enclosure.uses_outdoor_temp") is True,
          "enclosed:true AND uses_outdoor_temp:true",
          [dig(e, "data.enclosure.enclosed"),
           dig(e, "data.enclosure.uses_outdoor_temp")])
    eq("1.6d", "so the layout is FAILING despite being sealed", e,
       "data.enclosure.failing", True)

    # --- the room's own reading, as the game gives it ------------------------
    eq("1.7a", "the room row agrees the space is proper", e,
       "data.enclosure.rooms.0.proper", True)
    eq("1.7b", "…and that it is on outdoor temperature", e,
       "data.enclosure.rooms.0.uses_outdoor_temp", True)
    ge("1.7c", "…with an OpenRoofCount to match", e,
       "data.enclosure.rooms.0.open_roof_cells", 1)

    # --- and the element rollup did not move ---------------------------------
    eq("1.8", "`done` still says every element resolved — which is exactly the "
              "reading that hid this for forty days", e, "data.done", True)


# ------------------------------------------------------------------- phase 2 --

def phase2():
    banner("PHASE 2 — MECHANISM 1: one wall cell gone, and the cell is NAMED")

    ox, oz = S["origin"]
    # The middle of the west wall: a cell that is unambiguously shell, is not
    # the door, and is not a corner (a corner would open two runs at once and
    # make `open_shell_cells: 1` an accident rather than an assertion).
    hole = [ox, oz + 3]
    S["hole"] = hole
    tid = wall_at(S["lid"], hole) if not ARGS.dry_run else 1
    precondition("2.1", "the layout names a Wall element at %s" % hole, bool(tid),
                 "no `Wall` row in `construction {layout_id}` whose `at` is %s — "
                 "the fixture room is not where this suite thinks it is" % hole)

    e = send("dev:destroy", {"thing": tid})
    eq("2.2", "the wall is destroyed", e, "ok", True)
    advance(1)
    drain_journal()

    e = enclosure(S["lid"])
    S["e2"] = e
    # THE HEADLINE. This is the run's own failure, reproduced.
    eq("2.3a", "ProperRoom: the room is NOT enclosed", e,
       "data.enclosure.enclosed", False)
    eq("2.3b", "exactly one declared shell cell is open", e,
       "data.enclosure.open_shell_cells", 1)
    eq("2.3c", "…so the shell is not complete", e,
       "data.enclosure.shell_complete", False)

    # ACCEPTANCE ITEM 2 — a bare `enclosed:false` repeats the defect one level
    # up. The gap is NAMED, at the cell, with what is standing there.
    eq("2.4a", "the gap names the cell", e, "data.enclosure.gaps.0.at", hole)
    eq("2.4b", "…and the def that was supposed to be there", e,
       "data.enclosure.gaps.0.def", "Wall")
    eq("2.4c", "…and that nothing is standing on it", e,
       "data.enclosure.gaps.0.standing", "missing")
    eq("2.4d", "…and the game's own verdict for the cell", e,
       "data.enclosure.gaps.0.region_type", "Normal")
    shape("2.4e", "construction", e, "data.enclosure.gaps.0.placement_id", str)
    eq("2.4f", "a door is not the hole here", e,
       "data.enclosure.gaps.0.is_door", False)

    # The room the interior actually joined: the great outdoors. `cells: 60082`
    # in the run's own envelope; here it is simply "much bigger than 15".
    ge("2.5a", "the interior now belongs to a room far larger than the layout", e,
       "data.enclosure.rooms.0.cells", ROOM_INTERIOR * 10)
    eq("2.5b", "…which touches the map edge, which is WHY ProperRoom is false", e,
       "data.enclosure.rooms.0.touches_map_edge", True)
    eq("2.5c", "…and is therefore also on outdoor temperature", e,
       "data.enclosure.uses_outdoor_temp", True)

    # ACCEPTANCE ITEM 4 — how long has it been like that. In memory, with
    # `tracked_since`, per f9dadc7's settled resolution.
    shape("2.6a", "construction", e, "data.enclosure.unenclosed_for", dict)
    shape("2.6b", "construction", e, "data.enclosure.unenclosed_for.since_tick", (int, float))
    shape("2.6c", "construction", e, "data.enclosure.unenclosed_for.ticks", (int, float))
    shape("2.6d", "construction", e, "data.enclosure.unenclosed_for.day_boundaries", (int, float))
    shape("2.6e", "construction", e, "data.enclosure.unenclosed_for.tracked_since", (int, float))
    eq("2.6f", "…and it is not stale yet, because it just happened", e,
       "data.enclosure.unenclosed_for.stale", False)

    # ACCEPTANCE ITEM 3 — reachable from a TURN-LEVEL read, without already
    # suspecting this layout. This is the check that decides whether the agent
    # would have found it.
    e = send("rooms")
    S["rooms2"] = e
    ge("2.7a", "`rooms` reports a failing layout", e, "data.layouts_failing", 1)
    rows = [r for r in as_list(dig(e, "data.layouts_unenclosed"))
            if isinstance(r, dict) and r.get("layout_id") == S["lid"]]
    check("2.7b", "…and it is THIS layout", len(rows) == 1,
          "one row for %s" % S["lid"], dig(e, "data.layouts_unenclosed"))
    if rows:
        row = {"data": rows[0]}
        eq("2.7c", "the roll-up row carries the enclosed flag", row, "data.enclosed", False)
        eq("2.7d", "…and the thermal flag", row, "data.uses_outdoor_temp", True)
        eq("2.7e", "…and ONE NAMED CELL, so the glance is actionable", row,
           "data.first_gap.at", S["hole"])
        shape("2.7f", "rooms", row, "data.rect", list)
        shape("2.7g", "rooms", row, "data.name", str)

    # …and on the digest, which is the read the play loop makes unconditionally.
    e = send("digest")
    S["dig2"] = e
    shape("2.8a", "digest", e, "data.construction.layouts_unenclosed", list)
    ids = [r.get("layout_id") for r in as_list(dig(e, "data.construction.layouts_unenclosed"))
           if isinstance(r, dict)]
    check("2.8b", "the digest names the layout at the next turn, with no query "
                  "the agent had to think to make",
          S["lid"] in ids or ARGS.dry_run, "%s in %s" % (S["lid"], ids), ids)


# ------------------------------------------------------------------- phase 3 --

def phase3():
    banner("PHASE 3 — the wall goes back: mechanism 1 clears, mechanism 2 DOES NOT")

    hole = S["hole"]
    # `pos`, not `at`: Dev.PosArg reads `pos` and lists `at` as a NEAR MISS, so
    # the wrong key is a refusal rather than a silent default to the colony
    # anchor (git-bug 7382bdd's class).
    e = send("dev:spawn-thing", {"def": "Wall", "pos": hole, "stuff": STUFF,
                                 "buildable": True})
    eq("3.1", "the wall is rebuilt on the same cell", e, "ok", True)
    advance(1)
    drain_journal()

    e = enclosure(S["lid"])
    S["e3"] = e
    eq("3.2a", "ProperRoom is true again", e, "data.enclosure.enclosed", True)
    eq("3.2b", "no shell cell is open", e, "data.enclosure.open_shell_cells", 0)
    eq("3.2c", "…and no gap is named", e, "data.enclosure.gaps", [])

    # THE SEPARATION, ASSERTED. The first mechanism cleared and the second did
    # not: a reader that took `enclosed:true` for "the freezer is fine" is wrong
    # in exactly the way that would have killed the colony a second time.
    eq("3.3a", "the room is STILL thermally outdoors", e,
       "data.enclosure.uses_outdoor_temp", True)
    eq("3.3b", "…so it is still failing", e, "data.enclosure.failing", True)
    check("3.3c", "MECHANISM 2 IS OBSERVABLE WITH MECHANISM 1 SATISFIED — the "
                  "acceptance bullet, on the same room, one call apart",
          dig(S["e2"], "data.enclosure.enclosed") is False
          and dig(e, "data.enclosure.enclosed") is True
          and dig(e, "data.enclosure.uses_outdoor_temp") is True,
          "enclosed false -> true while uses_outdoor_temp stays true",
          [dig(S["e2"], "data.enclosure.enclosed"),
           dig(e, "data.enclosure.enclosed"),
           dig(e, "data.enclosure.uses_outdoor_temp")])

    # It is still on the roll-up, for the thermal reason alone.
    e = send("rooms")
    rows = [r for r in as_list(dig(e, "data.layouts_unenclosed"))
            if isinstance(r, dict) and r.get("layout_id") == S["lid"]]
    check("3.4a", "`rooms` still lists it — a sealed room that cannot hold cold "
                  "is not a finished freezer", len(rows) == 1 or ARGS.dry_run,
          "one row for %s" % S["lid"], dig(e, "data.layouts_unenclosed"))
    if rows:
        row = {"data": rows[0]}
        eq("3.4b", "…with no gap to name, because the hole is the ROOF", row,
           "data.first_gap", None)
        ge("3.4c", "…and the unroofed count carries the news instead", row,
           "data.unroofed_cells", 1)


# ------------------------------------------------------------------- phase 4 --

def phase4():
    banner("PHASE 4 — the roof goes on: the fixture 261f2e9 phase 6 never had")

    # No designation call is needed and that is deliberate:
    # `AutoBuildRoofAreaSetter.TryGenerateAreaNow` roofs any enclosed,
    # non-map-edge, non-fogged player room of <=26 regions and <=320 cells by
    # itself — a 3x5 interior qualifies — and colonists then build it. So this
    # phase is real colonist work on a bounded budget, not a god-hand shortcut,
    # and a colony with nobody free is a FIXTURE gap rather than a failure.
    spent, roofed = 0, False
    while spent < ROOF_BUDGET and not roofed:
        advance(ROOF_STEP)
        drain_journal()
        spent += ROOF_STEP
        e = enclosure(S["lid"])
        roofed = dig(e, "data.enclosure.uses_outdoor_temp") is False
        print("  %s+%d ticks: unroofed_cells=%s uses_outdoor_temp=%s%s"
              % (DIM, ROOF_STEP, dig(e, "data.enclosure.unroofed_cells"),
                 dig(e, "data.enclosure.uses_outdoor_temp"), OFF))
        if ARGS.dry_run:
            break
    precondition("4.1", "the colony roofed the fixture room within %d ticks" % ROOF_BUDGET,
                 roofed or ARGS.dry_run,
                 "the room is still on outdoor temperature after %d ticks — no "
                 "colonist was free to build the roof, or Construction is off in "
                 "work priorities. Not a spec failure." % spent)

    e = enclosure(S["lid"])
    S["e4"] = e
    eq("4.2a", "sealed", e, "data.enclosure.enclosed", True)
    eq("4.2b", "and roofed", e, "data.enclosure.uses_outdoor_temp", False)
    eq("4.2c", "no roof hole is named", e, "data.enclosure.unroofed_cells", 0)
    eq("4.2d", "so the layout is no longer failing", e, "data.enclosure.failing", False)
    absent("4.2e", "and the age block is GONE, not zeroed — presence is the signal",
           e, "data.enclosure.unenclosed_for")

    e = send("rooms")
    rows = [r for r in as_list(dig(e, "data.layouts_unenclosed"))
            if isinstance(r, dict) and r.get("layout_id") == S["lid"]]
    check("4.3a", "`rooms` drops it from the roll-up", not rows,
          "no row for %s" % S["lid"], dig(e, "data.layouts_unenclosed"))
    e = send("digest")
    ids = [r.get("layout_id") for r in
           as_list(dig(e, "data.construction.layouts_unenclosed")) if isinstance(r, dict)]
    check("4.3b", "…and so does the digest", S["lid"] not in ids,
          "%s absent from %s" % (S["lid"], ids), ids)

    # THE SHARED FIXTURE. A sealed, roofed room with a real interior
    # temperature, which is what 261f2e9 phase 6 has never been able to read.
    rid = dig(S["e4"], "data.enclosure.rooms.0.id")
    if rid is None:
        note("4.4", "no room id on the enclosure block — the temperature half of "
                    "the fixture is skipped")
        return
    e = send("room", {"id": rid})
    S["room4"] = e
    eq("4.4a", "`room` agrees it is proper", e, "data.proper", True)
    eq("4.4b", "…and indoors", e, "data.indoors", True)
    eq("4.4c", "…and NOT on outdoor temperature", e, "data.uses_outdoor_temp", False)
    eq("4.4d", "…with no open roof cells", e, "data.open_roof_cells", 0)
    shape("4.4e", "room", e, "data.temp_c", (int, float))
    print("  %sfixture room %s: temp_c=%s — hand this id to 261f2e9 phase 6%s"
          % (DIM, rid, dig(e, "data.temp_c"), OFF))


# ------------------------------------------------------------------- phase 5 --

def phase5():
    banner("PHASE 5 — a layout nobody has BUILT yet: gaps say `blueprint`")

    origin = find_site("5.1", "a second clear box, for the blueprint room", RW, RH)
    e = place(ROOM, origin, RW, RH, mode="blueprint", name="acc-enclosure-bp")
    eq("5.2a", "the blueprint room places", e, "data.ok", True)
    lid = dig(e, "data.layout_id") or "ly-dry"
    S["lid_bp"] = lid
    advance(1)
    drain_journal()

    e = enclosure(lid)
    eq("5.3a", "it declares a room all the same — the DECLARATION is what "
               "`intends_room` reads, and nothing is built", e,
       "data.enclosure.intends_room", True)
    eq("5.3b", "…and it is not enclosed", e, "data.enclosure.enclosed", False)
    eq("5.3c", "every declared shell cell is open", e,
       "data.enclosure.open_shell_cells", ROOM_SHELL)
    eq("5.4a", "and the gap says somebody is ON it", e,
       "data.enclosure.gaps.0.standing", "blueprint")
    ge("5.4b", "the gap list is capped and the count is not", e,
       "data.enclosure.gaps_more", ROOM_SHELL - 12)

    # A DOOR DOES NOT BREAK ENCLOSURE, AN UNBUILT DOOR DOES — the nuance that
    # bites an implementer, asserted on the one cell that can show it.
    doors = [g for g in as_list(dig(e, "data.enclosure.gaps"))
             if isinstance(g, dict) and g.get("is_door")]
    check("5.5", "the unbuilt DOOR cell is a gap too, and is flagged as a door "
                 "rather than quietly excluded",
          bool(doors) or ARGS.dry_run, "at least one gap with is_door:true",
          dig(e, "data.enclosure.gaps"))

    e = send("cancel-layout", {"layout_id": lid})
    eq("5.6", "the blueprint room is cancelled again", e, "ok", True)


# ------------------------------------------------------------------- phase 6 --

def phase6():
    banner("PHASE 6 — THE CRY-WOLF GUARD: a wall is not a room that failed")

    origin = find_site("6.1", "a clear box for a straight wall", WALL_RUN, 1)
    grid = [["Wall"] * WALL_RUN]
    e = place(grid, origin, WALL_RUN, 1, mode="instant", name="acc-enclosure-wall")
    eq("6.2a", "the wall run places", e, "data.ok", True)
    lid = dig(e, "data.layout_id") or "ly-dry"
    advance(1)
    drain_journal()

    e = enclosure(lid)
    eq("6.3a", "it declares NO room — its shell encloses nothing", e,
       "data.enclosure.intends_room", False)
    eq("6.3b", "…no declared interior at all", e, "data.enclosure.interior_cells", 0)
    eq("6.3c", "…so it is not failing, today or on day 40", e,
       "data.enclosure.failing", False)
    eq("6.3d", "…and the enclosure question is not answered for it, rather than "
               "answered `false` — an unanswered question must never read as a "
               "verdict", e, "data.enclosure.enclosed", None)

    e = send("rooms")
    rows = [r for r in as_list(dig(e, "data.layouts_unenclosed"))
            if isinstance(r, dict) and r.get("layout_id") == lid]
    check("6.4", "and it NEVER appears in the roll-up — a report that cries wolf "
                 "is worse than the silence it replaced, because the agent learns "
                 "to ignore it", not rows,
          "no row for %s" % lid, dig(e, "data.layouts_unenclosed"))


# ------------------------------------------------------------------- phase 9 --

def probe(fn):
    """Run an assertion and report whether it PASSED, without polluting the
    real tally — how phase 9 tests the suite's own helpers."""
    global CHECKS, FAILS
    c, f = CHECKS, list(FAILS)
    buf, sys.stdout = sys.stdout, open(os.devnull, "w")
    try:
        fn()
    finally:
        sys.stdout.close()
        sys.stdout = buf
    passed = len(FAILS) == len(f)
    CHECKS, FAILS[:] = c, f
    return passed


# The envelope run m1-20260901 actually got, trimmed to the keys this suite digs
# on. Kept verbatim as the honest fixture for the absent-vs-null trap: it is a
# real pre-a1644d6 `construction {layout_id}` and it carries NO enclosure block
# at all, which is exactly the shape `eq(..., None)` cannot tell from a `false`.
M1_SHAPED = {
    "ok": True, "op": "construction",
    "data": {"layout_id": "ly-1", "elements": 22, "built": 22, "cancelled": 0,
             "resolved": 22, "unresolved": 0, "done": True,
             "rect_source": "layout", "rect": [100, 100, 5, 7],
             "by_state": {"built": 22}, "items": []},
}


def phase9():
    banner("PHASE 9 — the suite's OWN machinery and the DECLARED-INTERIOR rule "
           "(offline; no bench, no game)")

    # -- THE ABSENT-VS-NULL TRAP, on a REAL pre-a1644d6 envelope --------------
    check("9.1a", "the banked envelope predates the enclosure block",
          not has_key(M1_SHAPED, "data.enclosure"),
          "data.enclosure ABSENT", dig(M1_SHAPED, "data.enclosure"))
    check("9.1b", "eq(...,None) PASSES on that absent key — THE TRAP",
          probe(lambda: eq("x", "t", M1_SHAPED, "data.enclosure.enclosed", None)),
          "pass (which is why shape() exists)", "fail")
    check("9.1c", "shape() FAILS on it — the trap, closed",
          not probe(lambda: shape("x", "construction", M1_SHAPED,
                                  "data.enclosure.enclosed")),
          "fail", "pass")
    check("9.1d", "shape() PASSES on a key the old envelope DOES carry",
          probe(lambda: shape("x", "construction", M1_SHAPED, "data.done")),
          "pass", "fail")
    check("9.1e", "and `done:true` is what the run READ while the freezer was "
                  "the great outdoors — the reading this issue exists to fix",
          dig(M1_SHAPED, "data.done") is True, "true", dig(M1_SHAPED, "data.done"))

    # -- THE DECLARED-INTERIOR RULE, over the shipped templates ---------------
    # A second implementation of LayoutEnclosure.FloodInterior, run over the
    # templates the mod is actually asked about. No bench, no game.
    comps, interior, shell = declared_interior(ROOM, RW, RH)
    check("9.2a", "this suite's own room declares exactly one interior",
          comps == 1, "1 component", comps)
    check("9.2b", "…of %d cells" % ROOM_INTERIOR, len(interior) == ROOM_INTERIOR,
          ROOM_INTERIOR, len(interior))
    check("9.2c", "…behind a %d-cell shell" % ROOM_SHELL, len(shell) == ROOM_SHELL,
          ROOM_SHELL, len(shell))

    comps, interior, _ = declared_interior([["Wall"] * WALL_RUN], WALL_RUN, 1)
    check("9.3", "a straight wall declares NO interior — the cry-wolf guard, "
                 "decided from the declaration alone with no game state in it",
          comps == 0 and not interior, "0 components, 0 cells", (comps, len(interior)))

    for name, want_comps in (("freezer-kitchen", 2), ("bedroom", 1), ("power-room", 1)):
        path = os.path.join(REPO, "templates", "%s.ir.json" % name)
        if not os.path.exists(path):
            note("9.4-%s" % name, "templates/%s.ir.json not reachable" % name)
            continue
        with open(path, encoding="utf-8") as fh:
            ir = json.load(fh)
        w, h = ir["size"]
        comps, interior, shell = declared_interior(ir["layers"][0], w, h)
        check("9.4-%s" % name,
              "templates/%s.ir.json declares %d enclosed room(s), %d interior "
              "cells behind %d shell cells" % (name, comps, len(interior), len(shell)),
              comps == want_comps, "%d component(s)" % want_comps, comps)
        if name == "freezer-kitchen":
            check("9.5", "…and the freezer template — THE ONE THAT SHIPPED THIS "
                         "FAILURE — is checkable at all, because its Cooler cells "
                         "are fillPercent 1.0 / Impassable and so seal the wall "
                         "slot exactly as a Wall does (Core Buildings_Temperature"
                         ".xml). Were they not, the flood would escape through "
                         "the north wall and this template would be silently "
                         "exempt from the whole check.",
                  comps == 2 and len(interior) > 0,
                  "two rooms, a non-empty interior", (comps, len(interior)))

    check("9.6a", "Heater is NOT a shell def (fillPercent 0.4, PassThroughOnly)",
          not is_shell_token("Heater"), "not shell", "shell")
    check("9.6b", "Cooler and Vent ARE (fillPercent 1.0, Impassable)",
          is_shell_token("Cooler_North") and is_shell_token("Vent"),
          "both shell", "not both")
    check("9.6c", "a rotation suffix does not change the answer",
          is_shell_token("Wall") == is_shell_token("Wall_South") is True,
          "both shell", "disagree")


# ---------------------------------------------------------------------- main --

PHASES = {0: phase0, 1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5,
          6: phase6, 9: phase9}


def main():
    global ARGS
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", default=DEFAULT_ROOT, help="protocol root")
    ap.add_argument("--phase", type=int, action="append",
                    help="run only these phases (0 always runs first)")
    ap.add_argument("--dry-run", action="store_true",
                    help="print the plan and every expectation, send nothing")
    ap.add_argument("--selftest", action="store_true",
                    help="phase 9 only: the suite's own assertions and the "
                         "declared-interior rule over templates/. No bench.")
    ap.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = ap.parse_args()

    if ARGS.selftest:
        print("a1644d6 acceptance — mode: --selftest")
        print("offline; no bench, no protocol root, no game, nothing sent")
        phase9()
        banner("RESULT")
        if FAILS:
            print("%s%d/%d selftest checks FAILED: %s%s"
                  % (RED, len(FAILS), CHECKS, ", ".join(FAILS), OFF))
            return 1
        print("%sSELFTEST PASS — all %d checks%s" % (GREEN, CHECKS, OFF))
        return 0

    # Phase 9 is offline and is NOT in the default run — `--selftest` is its
    # front door. `--phase 9` alone still works and skips phase 0's bench
    # preconditions, because a phase that touches no bench must not be gated on
    # one.
    wanted = sorted(set(ARGS.phase or [p for p in PHASES if p != 9]))
    if 0 not in wanted and wanted != [9]:
        wanted = [0] + wanted

    print("a1644d6 enclosure acceptance — root %s" % ARGS.root)
    if ARGS.dry_run:
        print("DRY RUN: nothing is sent; every check prints what it would expect.")
    for p in wanted:
        PHASES[p]()

    banner("RESULT")
    if ARGS.dry_run:
        print("dry run: %d checks planned across phases %s" % (CHECKS, wanted))
        return 0
    if FAILS:
        print("%s%d/%d checks FAILED: %s%s" % (RED, len(FAILS), CHECKS, ", ".join(FAILS), OFF))
        return 1
    print("%sall %d checks passed%s" % (GREEN, CHECKS, OFF))
    print("NOTHING HERE HAS MET A BENCH UNTIL THE ORCHESTRATOR RUNS IT.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
