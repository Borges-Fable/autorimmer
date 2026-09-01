#!/usr/bin/env python3
"""Acceptance runner for 1adc737 — `place-layout` / `cancel-layout`.

Same protocol, helpers and exit codes as `accept/4087644-order-honesty.py`; read
that file's header first, especially the SHAPE CONTRACT note — `eq(..., None)`
passes on an absent key, so phase 0 proves every dig path before any later phase
leans on it.

    ./accept/1adc737-place-layout.py             # everything
    ./accept/1adc737-place-layout.py --phase 2   # one phase (0 always runs)
    ./accept/1adc737-place-layout.py --dry-run   # print the plan, send nothing

Start the bench first (`_RimWorld-Agent/run-agent.sh`) with a colony on a map
that has open buildable ground, and leave it paused.

WHAT THIS IS TESTING, in one sentence: a whole room goes down in ONE
TRANSACTION — every cell preflighted before anything is placed, nothing placed
at all when one cell refuses, and the north-west/centre/south-west corner
arithmetic proved with real coordinates rather than trusted.

THE FOUR CLAIMS, and which phase settles each:

  * ATOMICITY (phase 1). A layout with one refusing cell places NOTHING, and
    `construction` over the layout's own rect is the independent witness — not
    the verb's own `placed_count`, which is the thing under test.
  * THE CORNER CONTRACT (phase 2). `templates/INDEX.md` pin 1 says the token
    sits in the footprint's NORTH-WEST cell; the mod converts that to the
    game's placement CENTRE. The 1x2 `Bed_South` is the element that proves it,
    because for a 1x2 def the north-west cell and the south-west corner differ
    by exactly one, so a wrong conversion is one cell off and visible.
  * INSTANT ≡ BLUEPRINT (phase 4), by `site-audit` rather than by an undefined
    "things-dump diff modulo construction byproducts" — an instant placement
    that passes the validator is, by construction, a state blueprint mode could
    have produced (git-bug 1adc737 #7, 3a5ff6c).
  * THE UNDO (phase 3). `cancel-layout` by layout id, and the second call
    proving `cancelled` and `already-cancelled` are different answers rather
    than the same absence.

WHAT THIS SUITE DELIBERATELY DOES NOT PROVE. Whether pawns then BUILD the
blueprints — that needs `advance` over game-days and is 4.2's play loop, not a
placement check. Whether the room reports `role: "Bedroom"` — it will not, and
correctly: `RoomRoleWorker_Bedroom` reads `bed.OwnersForReading` and an unowned
bed scores as Barracks, so the role is checked as a NOTE with the reason
(git-bug 1adc737 #4). And the roof: `AutoBuildRoofAreaSetter.TryGenerateAreaFor`
only QUEUES, and `TryGenerateAreaNow` runs next tick, so a same-call read sees
nothing and would report a correct implementation as broken.

IT LEAVES A BUILDING ON THE BENCH. Phase 4 spawns a real 5x7 room in instant
mode and does not remove it, because removing it would destroy the very evidence
`site-audit` was asked about. Run it on a bench you are willing to dirty.

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

# The 5x7 rehearsal, INLINE. Every file in accept/ stands alone and runs from a
# bare checkout — that is what makes acceptance portable across two benches — so
# the grid is here rather than read out of templates/. Check 0.6 then compares it
# against `templates/bedroom.ir.json` when that file is reachable, so the copy
# cannot drift from the template it is supposed to rehearse.
#
# Row 0 is NORTH (baseviz/ir.py's pinned docstring, templates/INDEX.md pin 0).
BEDROOM = [
    ["Wall", "Wall", "Wall", "Wall", "Wall"],
    ["Wall", ".", "Bed_South", ".", "Wall"],
    ["Wall", ".", ".", ".", "Wall"],
    ["Wall", ".", ".", ".", "Wall"],
    ["Wall", ".", ".", ".", "Wall"],
    ["Wall", "TorchLamp", ".", ".", "Wall"],
    ["Wall", "Wall", "Door", "Wall", "Wall"],
]
BW, BH = 5, 7
MARGIN = 2                    # clear ground the search demands around the room
STUFF = "WoodLog"             # cheap, always present, and MadeFromStuff
ROT_WORDS = ("North", "East", "South", "West")


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=240):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acc1adc737-%03d-%s" % (SEQ, slug)
    envelope = json.dumps({"id": cid, "op": op, "args": args or {}},
                          separators=(",", ":"))
    if ARGS.dry_run:
        print("    would send: %s" % envelope[:220])
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
                    print("    <- %s" % json.dumps(env, separators=(",", ":"))[:1200])
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
    """dig() cannot tell `absent` from `present and null`, and this suite cares:
    `layout_id` is DELIBERATELY null on a refused or rolled-back call."""
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
    return "null" if v is None else json.dumps(v, separators=(",", ":"))


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
    print("          This is a FIXTURE gap, not a failure of 1adc737.")
    sys.exit(2)


def shape(num, verb, env, path, kind=None):
    ok = has_key(env, path)
    want = "the key to be present"
    if ok and kind is not None:
        ok = isinstance(dig(env, path), kind)
        want += " and a %s" % kind.__name__
    check(num, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


# ------------------------------------------------------------ the expansion --

def split_token(tok):
    """`Bed_South` -> ("Bed", "South"). ONLY the four Rot4 words are a suffix
    (templates/INDEX.md pin 2: a rotation suffix is the Rot4 value verbatim)."""
    if "_" in tok:
        head, _, tail = tok.rpartition("_")
        if head and tail in ROT_WORDS:
            return head, tail
    return tok, None


def expand(grid, origin, w, h):
    """The grid, resolved against a SOUTH-WEST origin, as the verb's `elements`.

    Row 0 is north, so grid row r is map z = oz + h - 1 - r. Each element's `at`
    is the footprint's north-west cell; the mod does north-west -> south-west ->
    centre, because that needs the def's rotated size.

    This mirrors `rwa place-layout`'s expander and is deliberately a SECOND
    copy: this file must run from a bare checkout with no import of rwa, and the
    two agreeing is itself worth something — a drift in either shows up as a
    coordinate check failing in phase 2 with a real number attached.
    """
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


def place(origin, mode="blueprint", **extra):
    args = {
        "elements": expand(BEDROOM, origin, BW, BH),
        "origin": list(origin),
        "size": [BW, BH],
        "mode": mode,
        "name": "acc-bedroom",
        "stuff_map": {"*": STUFF},
    }
    args.update(extra)
    return send("place-layout", args)


def find_site(num, what):
    """A clear box big enough for the room PLUS margin, which is the siting
    discipline this project settled on after the granite bench: look at an area
    two to three times bigger than the thing you are placing (git-bug c718e4a,
    session 13). The origin is the inner south-west corner."""
    e = send("find-rect", {"w": BW + 2 * MARGIN, "h": BH + 2 * MARGIN, "max": 3})
    at = dig(e, "data.candidates.0.at")
    if ARGS.dry_run:
        at = [100, 100]
    precondition(num, what, isinstance(at, list) and len(at) == 2,
                 "find-rect found no clear %dx%d box — load a colony with open "
                 "buildable ground" % (BW + 2 * MARGIN, BH + 2 * MARGIN))
    origin = [at[0] + MARGIN, at[1] + MARGIN]
    print("  %ssite: rect at %s, layout origin %s%s" % (DIM, at, origin, OFF))
    return origin


def rect_of(origin):
    return [origin[0], origin[1], BW, BH]


def blueprint_count(origin):
    e = send("construction", {"rect": rect_of(origin)})
    return dig(e, "data.blueprints", 0) or 0


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 - the bench, the verbs, and THE SHAPE CONTRACT")
    e = send("status")
    precondition("0.1", "the bench answers `status`",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "no status envelope - start _RimWorld-Agent/run-agent.sh")

    # THE WATERMARK, and the obvious call gives the wrong answer.
    # JournalVerbs.Read updates last_seq BEFORE the `seq <= since_seq` skip and
    # breaks on `events.Count >= limit` BEFORE appending, so `{limit:1}` reports
    # the SECOND row's seq. Pushing since_seq past the end reads the file to the
    # end and yields the true maximum. `accept/s13-mod-surface.py` was the one
    # suite that never got this fix and scored two zero-failure runs only
    # because nothing happened to precede them (RUNLOG session 16).
    e = send("journal", {"since_seq": 999999999, "limit": 1})
    shape("0.2a", "journal", e, "data.last_seq")
    S["seq0"] = dig(e, "data.last_seq") or 0
    print("  %sjournal watermark: seq0=%s%s" % (DIM, S["seq0"], OFF))

    e = send("verbs")
    words = json.dumps(dig(e, "data") or {})
    check("0.3a", "`verbs` lists place-layout", "place-layout" in words,
          "place-layout in the registry", None if "place-layout" in words else words[:200])
    check("0.3b", "`verbs` lists cancel-layout", "cancel-layout" in words,
          "cancel-layout in the registry", None if "cancel-layout" in words else words[:200])

    # A DRY RUN AGAINST A REAL SITE proves every dig path the later phases use
    # without changing the map. It is not a substitute for a live send — the
    # header of 4087644's driver is the argument — it is how the SHAPES get
    # proved before anything is placed.
    S["origin"] = find_site("0.4", "a clear box for the rehearsal room")
    e = place(S["origin"], dry_run=True)
    S["dry"] = e
    eq("0.5a", "a dry run reports ok and places nothing", e, "data.ok", True)
    eq("0.5b", "and says so", e, "data.placed_count", 0)
    shape("0.5c", "place-layout", e, "data.gate")
    shape("0.5d", "place-layout", e, "data.anchor")
    shape("0.5e", "place-layout", e, "data.rect", list)
    shape("0.5f", "place-layout", e, "data.origin", list)
    shape("0.5g", "place-layout", e, "data.requested")
    shape("0.5h", "place-layout", e, "data.preflight", dict)
    shape("0.5i", "place-layout", e, "data.preflight.ok")
    shape("0.5j", "place-layout", e, "data.preflight.checked")
    shape("0.5k", "place-layout", e, "data.preflight.failed")
    shape("0.5l", "place-layout", e, "data.preflight.failures", list)
    shape("0.5m", "place-layout", e, "data.materials", list)
    shape("0.5n", "place-layout", e, "data.shortfall", list)
    shape("0.5o", "place-layout", e, "data.stuff_defaulted")
    shape("0.5p", "place-layout", e, "data.layout_id")
    shape("0.5q", "place-layout", e, "data.rolled_back")
    eq("0.5r", "`at` is documented as the north-west cell", e, "data.anchor", "north-west")
    eq("0.5s", "22 tokens in the 5x7 grid", e, "data.requested", 22)
    eq("0.5t", "the preflight sees the whole layout", e, "data.preflight.checked", 22)
    eq("0.5u", "and the site is clear", e, "data.preflight.failed", 0)

    # THE INLINE GRID MUST BE THE TEMPLATE. A rehearsal of a layout that has
    # drifted from the one templates/ ships is a rehearsal of nothing.
    path = os.path.join(REPO, "templates", "bedroom.ir.json")
    if os.path.exists(path) and not ARGS.dry_run:
        with open(path, encoding="utf-8") as fh:
            ir = json.load(fh)
        same = (ir.get("layers") or [None])[0] == BEDROOM and ir.get("size") == [BW, BH]
        check("0.6", "this suite's inline grid IS templates/bedroom.ir.json", same,
              "the template's layer 0 and size",
              None if same else {"template": (ir.get("layers") or [None])[0],
                                 "size": ir.get("size")})
    else:
        note("0.6", "templates/bedroom.ir.json not reachable from %s - the inline "
                    "grid was not checked against it" % REPO)


# ------------------------------------------------------------------- phase 1 --

def phase1():
    banner("PHASE 1 - THE INVARIANT: one refusal places NOTHING")
    origin = S["origin"]

    # (a) SELF-OVERLAP, answered with no map at all. Two walls on one cell is a
    # duplicate token, and `GenConstruct.CanPlaceBlueprintOver` — the game's own
    # def-level predicate — is what says so. This is the half of the preflight
    # the map cannot answer, because the map does not yet contain the layout.
    els = expand(BEDROOM, origin, BW, BH)
    dupe = dict(els[0])
    dupe["label"] = "DUPLICATE"
    e = send("place-layout", {"elements": els + [dupe], "origin": list(origin),
                              "size": [BW, BH], "mode": "blueprint",
                              "stuff_map": {"*": STUFF}})
    eq("1.1a", "a self-overlapping layout is refused", e, "data.ok", False)
    eq("1.1b", "and nothing is placed", e, "data.placed_count", 0)
    eq("1.1c", "and it gets no layout id", e, "data.layout_id", None)
    ge("1.1d", "the preflight names the offending element", e, "data.preflight.failed", 1)
    rows = [r for r in as_list(dig(e, "data.preflight.failures"))
            if isinstance(r, dict) and r.get("stage") == "self-check"]
    check("1.1e", "the failure is attributed to the SELF-check, not to the ground",
          len(rows) >= 1, "at least one failure with stage 'self-check'",
          [r.get("stage") for r in as_list(dig(e, "data.preflight.failures"))])
    if rows:
        eq("1.1f", "and it names the other element by index", {"r": rows[0]},
           "r.with_index", 0)
        check("1.1g", "with the game's own vocabulary in the reason",
              "IdenticalThingExists" in str(rows[0].get("reason", "")),
              "a reason naming IdenticalThingExists", rows[0].get("reason"))
    eq("1.1h", "nothing was placed, per `construction` rather than per the verb",
       {"n": blueprint_count(origin)}, "n", 0)

    # (b) A REFUSING CELL. One wall is placed by hand first; the layout's own
    # wall for that cell is then an identical blueprint and the game refuses it.
    # The point is not the refusal, it is that the OTHER TWENTY-ONE elements do
    # not go down either.
    corner = [origin[0], origin[1]]
    e = send("build", {"def": "Wall", "stuff": STUFF, "at": corner})
    eq("1.2a", "a wall is placed by hand at the layout's south-west corner",
       e, "data.placed", True)
    S["hand"] = dig(e, "data.placement_id")
    check("1.2b", "and it has a placement id", isinstance(S["hand"], str),
          "a pl-N id", S["hand"])

    e = place(origin)
    eq("1.3a", "the layout is refused for that one cell", e, "data.ok", False)
    eq("1.3b", "and places NOTHING - the whole invariant", e, "data.placed_count", 0)
    eq("1.3c", "no layout id for a transaction that did not happen",
       e, "data.layout_id", None)
    eq("1.3d", "exactly one element refused", e, "data.preflight.failed", 1)
    row = dig(e, "data.preflight.failures.0")
    check("1.3e", "the refusal names the refusing CELL",
          isinstance(dig({"r": row}, "r.cell"), list),
          "a [x,z] cell", dig({"r": row}, "r.cell"))
    check("1.3f", "and carries Blockers' {removal, reason} shape, which is what "
                  "turns 'clear it' and 'site elsewhere' into a decision",
          has_key({"r": row}, "r.blocker.removal") or dig({"r": row}, "r.blocker") is None,
          "blocker.removal present (or an explicitly null blocker)",
          dig({"r": row}, "r.blocker"))
    check("1.3g", "the reason is the game's own sentence",
          "Identical" in str(dig({"r": row}, "r.reason", "")),
          "a reason naming an identical blueprint/thing", dig({"r": row}, "r.reason"))
    eq("1.3h", "which half refused: the ground, not the architect menu",
       {"r": row}, "r.half", "verdict")
    eq("1.3i", "still exactly one blueprint on that ground - ours",
       {"n": blueprint_count(origin)}, "n", 1)

    # (c) The hand-placed wall is a `build` placement and belongs to NO layout.
    # `cancel-layout {placement_id}` still takes it, and reports the absence of a
    # layout as a fact rather than as an error.
    e = send("cancel-layout", {"placement_id": S["hand"]})
    eq("1.4a", "cancel-layout takes a single placement id", e, "data.cancelled", 1)
    eq("1.4b", "and says it belonged to no layout", e, "data.layout_id", None)
    eq("1.4c", "scope names what was asked", e, "data.scope", "placement")
    eq("1.4d", "the ground is clear again", {"n": blueprint_count(origin)}, "n", 0)


# ------------------------------------------------------------------- phase 2 --

def phase2():
    banner("PHASE 2 - blueprint mode, and THE CORNER CONTRACT in real numbers")
    origin = S["origin"]
    ox, oz = origin

    e = place(origin)
    S["place"] = e
    eq("2.1a", "the whole room goes down", e, "data.ok", True)
    eq("2.1b", "all 22 elements", e, "data.placed_count", 22)
    eq("2.1c", "with nothing refused", e, "data.preflight.failed", 0)
    eq("2.1d", "and nothing rolled back", e, "data.rolled_back", False)
    lid = dig(e, "data.layout_id")
    S["layout"] = lid
    check("2.1e", "it gets a layout id", isinstance(lid, str) and lid.startswith("ly-"),
          "an ly-N id", lid)
    eq("2.1f", "the layout's rect is the 5x7 the caller asked for",
       e, "data.rect", [ox, oz, BW, BH])
    ge("2.1g", "and the whole transaction is journaled once", e, "data.journal_seq", 1)

    rows = [r for r in as_list(dig(e, "data.placed")) if isinstance(r, dict)]
    by_label = {r.get("label"): r for r in rows}

    # THE SOUTH-WEST CORNER OF THE GRID. Grid row 6 column 0 is the layout's own
    # origin cell, and for a 1x1 Wall the north-west cell, the south-west corner
    # and the centre are all the same cell — so this check proves the ORIGIN
    # mapping without the rotation arithmetic in the way.
    w = by_label.get("[6,0] Wall")
    check("2.2a", "grid [6,0] lands exactly on the origin", w is not None,
          "a placed row labelled [6,0] Wall", sorted(by_label))
    if w:
        eq("2.2b", "…its `at` is the origin", {"r": w}, "r.at", [ox, oz])
        eq("2.2c", "…and for a 1x1 def `pos` is the same cell", {"r": w}, "r.pos", [ox, oz])
        eq("2.2d", "…footprint [x,z,w,h] anchored south-west", {"r": w},
           "r.footprint", [ox, oz, 1, 1])

    # THE NORTH ROW. Grid row 0 must be the HIGHEST z, which is the whole of
    # "row 0 is north" reduced to one number.
    n = by_label.get("[0,0] Wall")
    if n:
        eq("2.3", "grid row 0 is NORTH: z = oz + h - 1", {"r": n}, "r.at",
           [ox, oz + BH - 1])

    # THE BED, AND IT IS THE ONLY ELEMENT THAT CAN CATCH A WRONG CONVERSION.
    # `Bed` is size (1,2) and `Rot4.South` is not horizontal, so the occupied
    # rect stays 1 wide and 2 tall. The token is at grid [1,2] = (ox+2, oz+5),
    # which is the NORTH-WEST cell, so the footprint must run SOUTH from there:
    # south-west corner (ox+2, oz+4), height 2. A conversion that treated the
    # token as the south-west corner would put the bed at oz+5..oz+6 — through
    # the north wall — and every other check in this suite would still pass.
    bed = by_label.get("[1,2] Bed_South")
    check("2.4a", "the bed is placed", bed is not None,
          "a placed row labelled [1,2] Bed_South", sorted(by_label))
    if bed:
        eq("2.4b", "its `at` is the token cell, the footprint's NORTH-WEST",
           {"r": bed}, "r.at", [ox + 2, oz + 5])
        eq("2.4c", "the rotation suffix is the Rot4 value verbatim",
           {"r": bed}, "r.rot", "South")
        eq("2.4d", "and the 1x2 footprint runs SOUTH from the token, not north",
           {"r": bed}, "r.footprint", [ox + 2, oz + 4, 1, 2])
        check("2.4e", "`pos` is the game's centre and is published beside both",
              isinstance(dig({"r": bed}, "r.pos"), list),
              "a [x,z] centre", dig({"r": bed}, "r.pos"))

    # STUFF IS EXPLICIT, WHICH IS THE INVARIANT. Every wall came from the
    # stuff-map's `*`, and the row SAYS so rather than leaving a substitute
    # silent.
    if w:
        eq("2.5a", "the material is the one the stuff-map named",
           {"r": w}, "r.stuff", STUFF)
        eq("2.5b", "and its provenance is published, not implied",
           {"r": w}, "r.stuff_source", "stuff_map:*")
    eq("2.5c", "nothing fell through to the game's default",
       e, "data.stuff_defaulted", 0)

    # THE BILL. `place-layout` reports what the room costs and what is in
    # stockpiles — `map.resourceCounter`, which walks SlotGroup haul
    # destinations, so goods on unzoned ground read as ZERO. That caveat is why
    # `shortfall` is a fact to act on and not an alarm.
    mats = [m for m in as_list(dig(e, "data.materials")) if isinstance(m, dict)]
    wood = [m for m in mats if m.get("def") == STUFF]
    check("2.6a", "the material bill names the wood the room needs", len(wood) == 1,
          "one %s row" % STUFF, [m.get("def") for m in mats])
    if wood:
        ge("2.6b", "and the count is a real total", {"m": wood[0]}, "m.count", 100)
        check("2.6c", "with what is in stockpiles beside it",
              has_key({"m": wood[0]}, "m.in_stockpiles"),
              "in_stockpiles present", wood[0])

    # THE INDEPENDENT WITNESS. `construction` is a different verb reading the
    # map, so it can disagree with `place-layout`'s own count - and that is the
    # only reason to ask it.
    e2 = send("construction", {"rect": rect_of(origin)})
    eq("2.7a", "`construction` sees 22 blueprints on that ground", e2,
       "data.blueprints", 22)
    eq("2.7b", "and no frames yet", e2, "data.frames", 0)

    # EVERY PLACEMENT ID ANSWERS. `construction {placement_id}` is what makes
    # completion readable at all, because a finished build and a cancelled one
    # are the same absence.
    ids = [r.get("placement_id") for r in rows]
    check("2.8a", "every element got its own placement id",
          len(set(ids)) == 22 and all(isinstance(i, str) for i in ids),
          "22 distinct pl-N ids", ids[:5])
    if ids and isinstance(ids[0], str):
        e3 = send("construction", {"placement_id": ids[0]})
        eq("2.8b", "and one of them reads back as a blueprint", e3,
           "data.state", "blueprint")
        eq("2.8c", "naming the layout's own material", e3, "data.stuff", STUFF)

    # THE JOURNAL. ONE row for the whole transaction, carrying every placement
    # id — not 22 rows, which would bury the journal, and not zero, which would
    # leave the ids with no durable record at all.
    e4 = send("journal", {"since_seq": S["seq0"], "types": ["action"], "limit": 60})
    evs = [x for x in as_list(dig(e4, "data.events"))
           if isinstance(x, dict) and dig(x, "payload.verb") == "place-layout"]
    check("2.9a", "exactly one `place-layout` action row for this run",
          len(evs) == 1, "one row", len(evs))
    if evs:
        row = {"e": evs[0]}
        eq("2.9b", "carrying the layout id", row, "e.payload.layout_id", lid)
        eq("2.9c", "and the mode", row, "e.payload.mode", "blueprint")
        eq("2.9d", "and every placement id in it",
           {"n": len(as_list(dig(evs[0], "payload.placements")))}, "n", 22)


# ------------------------------------------------------------------- phase 3 --

def phase3():
    banner("PHASE 3 - cancel-layout, and the second call")
    origin = S["origin"]
    lid = S.get("layout")
    precondition("3.0", "phase 2 left a layout id", isinstance(lid, str) or ARGS.dry_run,
                 "run phase 2 first (or the whole suite)")

    e = send("cancel-layout", {"layout_id": lid, "dry_run": True})
    eq("3.1a", "a dry run reports what it would take", e, "data.targets", 22)
    eq("3.1b", "and cancels nothing", e, "data.cancelled", 0)
    eq("3.1c", "the blueprints are still there",
       {"n": blueprint_count(origin)}, "n", 22)

    e = send("cancel-layout", {"layout_id": lid})
    eq("3.2a", "the whole layout comes back up", e, "data.cancelled", 22)
    eq("3.2b", "nothing was rejected", e, "data.rejected", 0)
    eq("3.2c", "and every target was a live blueprint", e,
       "data.by_state.blueprint", 22)
    eq("3.2d", "the gate is the game's own cancel designator", e, "data.gate",
       "RimWorld/Designator_Cancel.CanDesignateThing")
    ge("3.2e", "and the undo is journaled", e, "data.journal_seq", 1)
    eq("3.2f", "the ground is clear, per `construction`",
       {"n": blueprint_count(origin)}, "n", 0)

    # THE PER-ROW OUTCOME IS WHAT THE DESIGNATOR SAID, not what the verb
    # intended to ask it. `DesignateEngine.RunThings` can refuse a target it was
    # handed (fogged, not on this map, or `Designator_Cancel.CanDesignateThing`
    # false), and a row that still read `cancelling` while `rejected` counted it
    # would be green while asserting nothing.
    rows = [r for r in as_list(dig(e, "data.placements")) if isinstance(r, dict)]
    outcomes = sorted({r.get("outcome") for r in rows})
    check("3.2g", "every row says `cancelled`, decided after the designator ran",
          outcomes == ["cancelled"], "every outcome 'cancelled'", outcomes)
    check("3.2h", "and no row is left on the provisional value",
          "pending" not in outcomes, "no outcome 'pending'", outcomes)

    # THE SECOND CALL. `cancelled` and `already gone` must be different answers,
    # because a finished build and a cancelled one are the same absence and the
    # placement id is the only thing that can tell them apart.
    e = send("cancel-layout", {"layout_id": lid})
    eq("3.3a", "cancelling twice cancels nothing the second time",
       e, "data.cancelled", 0)
    eq("3.3b", "and every placement now reads `cancelled`, not `built`",
       e, "data.by_state.cancelled", 22)
    rows = [r for r in as_list(dig(e, "data.placements")) if isinstance(r, dict)]
    outcomes = sorted({r.get("outcome") for r in rows})
    check("3.3c", "each row says why there was nothing to do",
          outcomes == ["not-present"], "every outcome 'not-present'", outcomes)

    e = send("cancel-layout", {"layout_id": "ly-999999"})
    eq("3.4a", "an unknown layout id is a bad-args, not a silent no-op",
       e, "ok", False)
    check("3.4b", "and the message says ids are session-scoped",
          "session-scoped" in json.dumps(dig(e, "error") or {}),
          "an error naming the session scope", dig(e, "error"))


# ------------------------------------------------------------------- phase 4 --

def phase4():
    banner("PHASE 4 - instant mode, and site-audit as the proof of equivalence")
    origin = find_site("4.0", "a second clear box, for the instant room")
    S["instant_origin"] = origin
    ox, oz = origin

    e = place(origin, mode="instant")
    eq("4.1a", "instant mode places the whole room", e, "data.ok", True)
    eq("4.1b", "all 22 elements", e, "data.placed_count", 22)
    eq("4.1c", "through the SAME gate, not the god-hand", e, "data.gate",
       "site-gate/1")
    rows = [r for r in as_list(dig(e, "data.placed")) if isinstance(r, dict)]
    kinds = sorted({r.get("kind") for r in rows})
    check("4.1d", "and every element is a standing building, not a blueprint",
          kinds == ["built"], "every kind 'built'", kinds)
    modes = sorted({r.get("mode") for r in rows})
    check("4.1e", "each row says which branch placed it", modes == ["instant"],
          "every mode 'instant'", modes)

    # WHAT IT ERASED, IF ANYTHING. `WipeMode.VanishOrMoveAside` runs
    # CheckMoveItemsAside first, so a wood stack a colonist would have HAULED
    # away is moved rather than destroyed - which is what keeps instant and
    # blueprint mode equivalent. Anything actually destroyed is listed, so
    # "nothing was wiped" is a measurement.
    wiped = as_list(dig(e, "data.wiped"))
    if wiped:
        note("4.2", "instant mode destroyed %d thing(s) the blueprint path would "
                    "have had a colonist deal with: %s"
                    % (len(wiped), json.dumps(wiped[:3])))
    else:
        check("4.2", "nothing was wiped, and that is measured rather than assumed",
              True, "an empty wiped list", wiped)

    eq("4.3", "there is nothing left to build", {"n": blueprint_count(origin)}, "n", 0)

    # THE EQUIVALENCE CLAIM, and the instrument that makes it decidable.
    # `site-audit` re-runs GenConstruct.CanPlaceBlueprintAt over every player
    # building in a rect. Zero hits means every one of these 22 buildings is
    # somewhere a blueprint would have been accepted - i.e. a state blueprint
    # mode could have produced. That replaces the issue's original
    # "things-dump diff modulo construction byproducts", which its own
    # verification comment showed was not a decidable predicate.
    e = send("site-audit", {"rect": rect_of(origin)})
    shape("4.4a", "site-audit", e, "data.hit_count")
    eq("4.4b", "INSTANT MODE PRODUCED A STATE THE VALIDATOR ACCEPTS - "
               "zero placements the game would have refused", e, "data.hit_count", 0)

    # THE ROOM. `room-at` on the interior: five interior columns minus two
    # walls, seven rows minus two walls = 3 x 5 = 15 cells, and it must not be
    # psychologically outdoors.
    inside = [ox + 2, oz + 3]
    e = send("room-at", {"at": inside})
    eq("4.5a", "the placed walls really do enclose a room", e, "data.outdoors", False)
    eq("4.5b", "of exactly the interior's 15 cells", e, "data.cells", 15)
    role = dig(e, "data.role")
    note("4.5c", "room role is %s. `Bedroom` needs a bed OWNER - "
                 "RoomRoleWorker_Bedroom reads bed.OwnersForReading and an "
                 "unowned bed with bed_emptyCountsForBarracks scores as "
                 "Barracks - and colonists claim beds by SLEEPING in them, so "
                 "this is not something a verb sets (git-bug 1adc737 #4)."
                 % show(role))
    note("4.5d", "the roof was NOT checked. AutoBuildRoofAreaSetter"
                 ".TryGenerateAreaFor only QUEUES; TryGenerateAreaNow runs from "
                 "AutoBuildRoofAreaSetterTick_First, i.e. NEXT TICK - a "
                 "same-call read sees nothing and would report a correct "
                 "implementation as broken. Advance one tick, then read.")

    # A LAYOUT ON TOP OF A BUILT ONE. Every cell now holds the very thing it
    # asked for, so the preflight must refuse ALL of it - the same
    # IdenticalThingExists that phase 1 saw for one cell, at full width.
    e = place(origin, mode="blueprint")
    eq("4.6a", "re-placing over the built room is refused outright",
       e, "data.ok", False)
    eq("4.6b", "and places nothing", e, "data.placed_count", 0)
    ge("4.6c", "with the walls and furniture all named", e,
       "data.preflight.failed", 20)


# ------------------------------------------------------------------- phase 5 --

def phase5():
    banner("PHASE 5 - --partial, and the rollback that keeps one call one "
           "transaction")
    origin = find_site("5.0", "a third clear box, for the partial/rollback tests")
    ox, oz = origin

    # --partial IS THE ONLY DOOR OUT OF THE INVARIANT, so it is checked that it
    # opens exactly one cell wide: 21 placed, 1 skipped, and `ok` still false
    # because something the caller asked for did not happen.
    e = send("build", {"def": "Wall", "stuff": STUFF, "at": [ox, oz]})
    eq("5.1a", "a wall in the way, placed by hand", e, "data.placed", True)
    hand = dig(e, "data.placement_id")

    e = place(origin, partial=True)
    eq("5.2a", "with --partial the rest of the room still goes down",
       e, "data.placed_count", 21)
    eq("5.2b", "one element skipped", e, "data.skipped", 1)
    eq("5.2c", "and `ok` is still false - something asked for did not happen",
       e, "data.ok", False)
    eq("5.2d", "the caller's opt-out is echoed, so the envelope says which "
               "contract was in force", e, "data.partial", True)
    lid = dig(e, "data.layout_id")
    eq("5.2e", "`construction` counts 21 new plus the hand-placed one",
       {"n": blueprint_count(origin)}, "n", 22)

    if isinstance(lid, str):
        send("cancel-layout", {"layout_id": lid})
    if isinstance(hand, str):
        send("cancel-layout", {"placement_id": hand})
    eq("5.3", "cleared again before the rollback test",
       {"n": blueprint_count(origin)}, "n", 0)

    # THE ROLLBACK, and the fixture is chosen so it MUST fire.
    #
    # `TrapSpike` carries `PlaceWorker_NeverAdjacentTrap`, whose AllowsPlacing
    # walks its own occupied rect ExpandedBy(1) and refuses on any trap
    # BUILDING, BLUEPRINT OR FRAME it finds. Two spike traps in adjacent cells
    # therefore: (a) do not overlap, so the self-overlap check is silent; (b)
    # have no interaction cells, so the interaction check is silent; (c) each
    # pass the gate individually against an empty map. The refusal can only
    # appear AFTER the first one is placed - which is precisely the case no
    # preflight against the pre-placement map can see, and precisely what the
    # rollback exists for.
    e = send("place-layout", {
        "elements": [
            {"def": "TrapSpike", "at": [ox + 2, oz + 2], "label": "trap-a"},
            {"def": "TrapSpike", "at": [ox + 3, oz + 2], "label": "trap-b"},
        ],
        "origin": [ox, oz], "size": [BW, BH], "mode": "blueprint",
        "stuff_map": {"*": STUFF}, "name": "acc-traps"})
    ok_pre = dig(e, "data.preflight.failed")
    if ok_pre not in (0, None):
        note("5.4", "the two traps were refused at PREFLIGHT (failed=%s), so the "
                    "late-refusal path was not exercised. That is a legitimate "
                    "outcome on a bench whose ground refuses TrapSpike; the "
                    "rollback claim is then UNPROVEN by this run, not failed."
                    % ok_pre)
        return
    eq("5.4a", "both traps pass the preflight individually",
       e, "data.preflight.failed", 0)
    eq("5.4b", "and the SECOND is then refused by the game", e,
       "data.rolled_back", True)
    eq("5.4c", "so the call placed nothing at all", e, "data.placed_count", 0)
    eq("5.4d", "and got no layout id", e, "data.layout_id", None)
    late = as_list(dig(e, "data.late_refusals"))
    check("5.4e", "the late refusal is reported as its own thing", len(late) >= 1,
          "at least one late_refusals row", late)
    if late:
        check("5.4f", "carrying the game's own sentence",
              "Trap" in str(dig(late[0], "reason", "")) or
              "trap" in str(dig(late[0], "reason", "")),
              "a reason about adjacent traps", dig(late[0], "reason"))
    eq("5.4g", "the ROLLBACK is what the map agrees with, not the envelope",
       {"n": blueprint_count(origin)}, "n", 0)
    shape("5.4h", "place-layout", e, "data.rollback", dict)
    eq("5.4i", "and it says the first trap was taken back up",
       e, "data.rollback.destroyed", 1)

    # WITH --partial the same layout is a legal half-placement, which is the
    # difference the flag is for.
    e = send("place-layout", {
        "elements": [
            {"def": "TrapSpike", "at": [ox + 2, oz + 2], "label": "trap-a"},
            {"def": "TrapSpike", "at": [ox + 3, oz + 2], "label": "trap-b"},
        ],
        "origin": [ox, oz], "size": [BW, BH], "mode": "blueprint",
        "stuff_map": {"*": STUFF}, "partial": True, "name": "acc-traps-partial"})
    eq("5.5a", "with --partial the first trap stays", e, "data.placed_count", 1)
    eq("5.5b", "and the second is skipped rather than rolling the call back",
       e, "data.rolled_back", False)
    lid = dig(e, "data.layout_id")
    if isinstance(lid, str):
        e = send("cancel-layout", {"layout_id": lid})
        eq("5.5c", "and cancel-layout takes the one it placed", e, "data.cancelled", 1)

    e = send("journal", {"since_seq": S["seq0"], "types": ["red_error"], "limit": 50})
    eq("5.6", "no red errors across the whole run", e, "data.count", 0)


# ---------------------------------------------------------------------- main --

PHASES = {1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5}


def main():
    global ARGS
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--root", default=DEFAULT_ROOT, help="the protocol root")
    p.add_argument("--phase", type=int, action="append", choices=[0, 1, 2, 3, 4, 5],
                   help="run only these phases (repeatable); phase 0 always runs")
    p.add_argument("--dry-run", action="store_true",
                   help="print the plan and every expectation, send nothing")
    p.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = p.parse_args()

    print("AutoRimmer acceptance - place-layout / cancel-layout (1adc737)")
    print("protocol root: %s" % ARGS.root)
    if not ARGS.dry_run and not os.path.exists(os.path.join(ARGS.root, "status.json")):
        print("%sno status.json under %s - start the bench with "
              "_RimWorld-Agent/run-agent.sh, or pass --root%s" % (RED, ARGS.root, OFF))
        sys.exit(2)
    os.makedirs(os.path.join(ARGS.root, "commands"), exist_ok=True)

    wanted = sorted(set(ARGS.phase or [1, 2, 3, 4, 5]) - {0})
    print("phases: 0 + %s" % ", ".join(str(x) for x in wanted))

    phase0()
    for n in wanted:
        PHASES[n]()

    print("")
    print("=" * 78)
    if ARGS.dry_run:
        # A dry-run SENDS NOTHING, so every expectation above was printed and no
        # expectation was evaluated. Saying "passed" here is the exact
        # green-while-asserting-nothing failure phase 0 exists to prevent, one
        # level up.
        print("%sRESULT: --dry-run printed %d expectations and asserted NONE of "
              "them. Nothing was sent; no dig path was proved. Run it live.%s"
              % (YELLOW, CHECKS, OFF))
        sys.exit(0)
    if FAILS:
        print("%sRESULT: %d FAILED of %d checks - %s%s"
              % (RED, len(FAILS), CHECKS, ", ".join(FAILS), OFF))
        sys.exit(1)
    print("%sRESULT: all %d checks passed%s" % (GREEN, CHECKS, OFF))
    sys.exit(0)


if __name__ == "__main__":
    main()
