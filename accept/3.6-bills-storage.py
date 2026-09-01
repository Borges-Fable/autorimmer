#!/usr/bin/env python3
"""Acceptance runner for spec 3.6 — production bills + storage settings
(git-bug 48f666c).

Same protocol, helpers and exit codes as `accept/4087644-order-honesty.py`; read
that file's header first. There is no `.ps1` twin: this box has python 3.14 and
the bench, and no pwsh.

    ./accept/3.6-bills-storage.py             # everything
    ./accept/3.6-bills-storage.py --phase 4   # one phase (0 always runs)
    ./accept/3.6-bills-storage.py --dry-run   # print the plan, send nothing

Start the bench first (`_RimWorld-Agent/run-agent.sh`), load a colony with at
least one colonist who is not disabled at Cooking, leave it paused, and leave
dev mode ON — phase 0 stages a stove, fuel, raw food, a stockpile and two
shelves through `dev:spawn-thing`, which is Dev.Gate'd.

WHAT THIS IS TESTING, in one sentence: the nine verbs of spec 3.6 reproduce
the widget's gates rather than the model's silence — a bill the game would not
offer is refused by NAME, a reorder past the end is `bad-args` with the bill
still in the stack, a write to a GROUPED shelf is visible through every member
of the group, and a rejected call changes NOTHING.

--dry-run PROVES THE PLAN, NEVER THE PATHS. It sends nothing, so every envelope
is empty, every shape check is skipped and every dig() path looks fine. Only a
live run tells you whether the envelopes are the shape the assertions assume,
which is what phase 0's shape contract is for.

WHAT THIS SUITE DELIBERATELY DOES NOT DO: it never saves the game and reads the
Scribe XML to prove a filter claim. `Bill.ExposeData` NARROWS `ingredientFilter`
during the SAVING pass for any recipe with a `fixedIngredientFilter` (DESIGN
decisions log, 2026-08-31), so that reader perturbs what it measures. Every
filter assertion here is a live read.

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

# The nine ops this spec ships. `status.data.verbs` is a flat list of op-name
# strings (CoreVerbs.Status -> VerbRegistry.Ops), so this is a subset test.
OPS_3_6 = ["bill-options", "bill-add", "bill-set", "bill-reorder", "bill-remove",
           "storage", "storage-set", "storage-link", "storage-unlink"]

# The fixture. A FueledStove and NOT `world-fixture {steps:["bench"]}`, which
# hardcodes ThingDefOf.TableButcher (WorldFixtureVerbs.Bench) — a butcher table
# has no CookMealSimple, so the full-loop phase would have nothing to prove.
STOVE_DEF = "FueledStove"
COOK_RECIPE = "CookMealSimple"
MEAL_DEF = "MealSimple"
FUEL_DEF = "WoodLog"
INGREDIENT_DEF = "RawPotatoes"
SHELF_DEF = "Shelf"
GRAVE_DEF = "Grave"
COOKING_WORK = "Cooking"

# Benches probed, in order, for a recipe the colony's research has not unlocked.
# The stove first (it is already staged); the rest are spawned only if needed.
RESEARCH_BENCHES = [None, "TableMachining", "FabricationBench", "TableSmithing",
                    "HandTailoringBench", "ElectricSmelter"]


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=240):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acc36-%03d-%s" % (SEQ, slug)
    envelope = json.dumps({"id": cid, "op": op, "args": args or {}},
                          separators=(",", ":"))
    if ARGS.dry_run:
        print("    would send: %s" % envelope)
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
                    print("    <- %s" % json.dumps(env, separators=(",", ":")))
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
    a REFUSED bill-add publishes `action.journal_seq` as an explicit null, and
    proving that is a different claim from proving the key was never emitted."""
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
    s = "null" if v is None else json.dumps(v, separators=(",", ":"))
    return s if len(s) <= 400 else s[:397] + "..."


def fields_of(rows):
    """The `field` names out of a `changed` / `refused` / `config_refused` list.
    Every entry in all three is {field, …}, so one helper reads them all."""
    return [r.get("field") for r in as_list(rows) if isinstance(r, dict)]


def entry_for(rows, field):
    for r in as_list(rows):
        if isinstance(r, dict) and r.get("field") == field:
            return r
    return None


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


def eqv(num, what, got, want):
    """eq() for a value already dug out — a storage row read back through the
    OBSERVER, or one entry of a `refused` list. Same recorded check, no fake
    envelope wrapped around it just to satisfy a path."""
    ok = ARGS.dry_run or ((want is None and got is None) or got == want)
    check(num, what, ok, show(want), got)


def ge(num, what, env, path, want):
    got = dig(env, path)
    ok = isinstance(got, (int, float)) and not isinstance(got, bool) and got >= want
    check(num, "%s (%s)" % (what, path), ok, ">= %s" % want, got)


def contains(num, what, haystack, needle):
    ok = isinstance(haystack, str) and needle in haystack
    check(num, what, ok, "a string containing %r" % needle, haystack)


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
    print("          This is a FIXTURE gap, not a failure of 3.6.")
    sys.exit(2)


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


# ------------------------------------------------------------ shape contract --
# WHY THIS EXISTS, and it is the standing lesson of accept/ rather than a
# nicety.
#
# `eq()` cannot tell an ABSENT key from one that is present and null. dig()
# returns None for both, so `eq(..., None)` passes either way. A driver whose
# dig paths are wrong therefore does not fail — it goes GREEN WHILE ASSERTING
# NOTHING, which is strictly worse than a loud abort, because nobody
# investigates a pass. 4087644's driver is the worked example: its first draft
# shipped seven wrong arg names and dig paths and passed --dry-run.
#
# So phase 0 PROVES every envelope key the later phases dig on, naming the verb
# and the key. `shape()` asks has_key(), never a value. A shape change then
# fails HERE, loudly, at a check that says which verb moved.
#
# PER-DRIVER ON PURPOSE, not a shared accept/_shapes.py. Every file in accept/
# stands alone and runs from a bare checkout; a shared module would let a shape
# change made for one spec silently update every other driver, when what you
# want is THIS driver failing loudly when 3.6's own contract changes.
def shape(num, verb, env, path, kind=None):
    """Assert a key EXISTS, independently of its value. Returns the truth of it
    so a caller can branch, but the check is recorded either way."""
    ok = has_key(env, path)
    want = "the key to be present"
    if ok and kind is not None:
        ok = isinstance(dig(env, path), kind)
        want += " and a %s" % kind.__name__
    check(num, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


# ------------------------------------------------------------------- fixture --

def thing_ids(def_name):
    """Every spawned thing of one def, by id. `things` is a ROLLUP verb — rows
    are BY DEF and carry no id — so `detail:true` is mandatory, and
    `by_location:false` forces the flat `data.things` shape rather than the
    nested `data.by_location.*` one."""
    e = send("things", {"def": def_name, "detail": True, "by_location": False,
                        "cap": 50, "detail_cap": 50})
    return [t.get("id") for t in as_list(dig(e, "data.things"))
            if isinstance(t, dict) and t.get("id") is not None]


def count_of(def_name):
    """Total item COUNT (not stacks) of one def on the map. `data.totals.count`
    sums the rollup, and is 0 rather than absent when nothing matches."""
    e = send("things", {"def": def_name, "detail": False, "by_location": False})
    return dig(e, "data.totals.count", 0) or 0


def free_spot(near):
    """A buildable 3x3 whose CENTRE is somewhere a building can be dropped.
    `find-rect` returns `candidates[i].at` (the origin) and `.center`; direct
    spawn wants a cell, so the centre is the one to take."""
    e = send("find-rect", {"w": 3, "h": 3, "near": near, "max": 3})
    for c in as_list(dig(e, "data.candidates")):
        if isinstance(c, dict) and isinstance(c.get("center"), list):
            return c["center"]
    return None


def spawn_building(def_name, near):
    """One building, reusing an existing one of that def if the colony has it.
    mode:"direct" is not optional for a building: DevVerbs.SpawnThing says a
    building placed "near" fails against its own footprint, so direct + a
    probed cell is the only route that lands."""
    existing = thing_ids(def_name)
    if existing:
        return existing[0], False
    spot = free_spot(near)
    if spot is None:
        return None, False
    e = send("dev:spawn-thing", {"def": def_name, "pos": spot, "mode": "direct"})
    if not dig(e, "ok"):
        print("          %sdev:spawn-thing %s: %s%s"
              % (DIM, def_name, show(dig(e, "error")), OFF))
        return None, False
    return dig(e, "data.spawned.0.id"), True


def pick_cook(ids):
    """A colonist who can COOK, chosen BY PREDICATE and never by roster index.
    git-bug 1eb2262 settled this: `pawns` emits in a stable order, which makes
    an index REPRODUCIBLE and does not make it MEANINGFUL — the pawn at [0] is
    not the pawn who can cook. `pawn {sections:["work"]}` publishes
    `data.work.disabled`, a flat list of WorkTypeDef defNames; a work type the
    pawn cannot do is IN that list and ABSENT from `data.work.row`."""
    for pid in ids:
        e = send("pawn", {"id": pid, "sections": ["work"]})
        if not dig(e, "data.work.initialized"):
            continue
        disabled = [str(d) for d in as_list(dig(e, "data.work.disabled"))]
        if COOKING_WORK not in disabled:
            return pid
    return None


def bench_bill_count(bench):
    return dig(send("bill-options", {"bench": bench, "cap": 1}), "data.bills_total", 0) or 0


def bills_on(bench):
    """The bill rows for one bench, through 2.4's observer rather than through
    the mutation verb's own echo — so a claim about state is never read out of
    the envelope that claims to have changed it."""
    e = send("bills", {"bench": bench})
    for b in as_list(dig(e, "data.benches")):
        if isinstance(b, dict) and b.get("id") == bench:
            return as_list(b.get("bills"))
    return []


def ensure_bill(bench, recipe=COOK_RECIPE):
    """A bill to operate on, for the phases that do not create one. Returns the
    uid, or None."""
    rows = bills_on(bench)
    for r in rows:
        if isinstance(r, dict) and r.get("recipe") == recipe:
            return r.get("uid")
    e = send("bill-add", {"bench": bench, "recipe": recipe, "repeat": "forever"})
    if ARGS.dry_run:
        return "<uid>"
    return dig(e, "data.uid")


def storage_row(token):
    """One storage's row from the READ verb, addressed by its own target token
    ("zone:<id>" / "thing:<id>")."""
    e = send("storage", {"id": token})
    return dig(e, "data.storages.0")


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 - the bench, the fixture, and THE SHAPE CONTRACT")
    e = send("status")
    precondition("0.1a", "the bench answers `status`",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "no status envelope - start _RimWorld-Agent/run-agent.sh")
    precondition("0.1b", "a game is loaded",
                 ARGS.dry_run or dig(e, "data.gameLoaded") is True,
                 "status says no game is loaded - load a colony and leave it paused")

    # `data.verbs` is a FLAT LIST OF OP-NAME STRINGS, not objects. All nine of
    # this spec's ops, as a subset test: a registry that dropped one is the
    # first thing to know, and it is one call.
    verbs = as_list(dig(e, "data.verbs"))
    missing = [] if ARGS.dry_run else [op for op in OPS_3_6 if op not in verbs]
    check("0.1c", "`status` registers all nine 3.6 verbs", not missing,
          "every one of %s" % ", ".join(OPS_3_6), missing or verbs[:12])

    # THE WATERMARK, and the obvious call gives the wrong answer.
    # JournalVerbs.Read updates last_seq BEFORE the `seq <= since_seq` skip and
    # breaks on `events.Count >= limit` BEFORE appending, so `{limit:1}` stops
    # at the SECOND line and reports ITS seq. Pushing since_seq past the end
    # makes every line fail the skip while still updating last_seq, so the file
    # is read to the end and the value is the true maximum.
    e = send("journal", {"since_seq": 999999999, "limit": 1})
    shape("0.2a", "journal", e, "data.last_seq")
    shape("0.2b", "journal", e, "data.events", list)
    shape("0.2c", "journal", e, "data.count")
    S["seq0"] = dig(e, "data.last_seq") or 0
    print("  %sjournal watermark: seq0=%s%s" % (DIM, S["seq0"], OFF))

    # ---- the actor, by predicate ------------------------------------------
    e = send("pawns", {"filter": "colonist", "cap": 30})
    shape("0.3a", "pawns", e, "data.list", list)
    ids = [p.get("id") for p in as_list(dig(e, "data.list")) if isinstance(p, dict)]
    if ARGS.dry_run:
        ids = ["<A>"]
    precondition("0.3b", "at least one colonist", len(ids) >= 1,
                 "found %d - load a colony with a colonist" % len(ids))
    S["cook"] = ids[0] if ARGS.dry_run else pick_cook(ids)
    precondition("0.3c", "a colonist who is not disabled at Cooking",
                 ARGS.dry_run or S["cook"] is not None,
                 "every colonist has %s in pawn{sections:[\"work\"]}.work.disabled; "
                 "the full-loop phase cannot run" % COOKING_WORK)
    # Undrafted, or nothing will pick the bill up.
    send("undraft", {"pawns": [S["cook"]]})
    e = send("pawn", {"id": S["cook"], "sections": ["state"]})
    S["at"] = dig(e, "data.state.at") or [100, 100]
    print("  %scook: pawn %s at %s%s" % (DIM, S["cook"], S["at"], OFF))

    # ---- the stove, its fuel, its ingredients, somewhere to put the food ---
    stove, made = spawn_building(STOVE_DEF, S["at"])
    if ARGS.dry_run:
        stove = "<stove>"
    precondition("0.4a", "a %s on the map" % STOVE_DEF, stove is not None,
                 "no %s and dev:spawn-thing could not place one - is dev mode on?"
                 % STOVE_DEF)
    S["stove"] = stove
    print("  %s%s: thing %s (%s)%s"
          % (DIM, STOVE_DEF, stove, "spawned" if made else "reused", OFF))

    # A FueledStove has a CompRefuelable and WorkGiver_DoBill yields a REFUEL
    # job before a bill job when it is empty, so the wood is not decoration.
    send("dev:spawn-thing", {"def": FUEL_DEF, "count": 150, "pos": S["at"]})
    send("dev:spawn-thing", {"def": INGREDIENT_DEF, "count": 75, "pos": S["at"]})

    e = send("zones", {"kind": "stockpile"})
    zid = dig(e, "data.stockpiles.list.0.id")
    if zid is None and not ARGS.dry_run:
        spot = free_spot(S["at"])
        if spot is not None:
            e = send("zone", {"op": "add", "kind": "stockpile",
                              "rect": [int(spot[0]) - 1, int(spot[1]) - 1, 3, 3]})
            zid = dig(e, "data.zones.0.id")
    if ARGS.dry_run:
        zid = "<zone>"
    precondition("0.4b", "a stockpile zone", zid is not None,
                 "no stockpile and `zone {op:\"add\"}` could not make one")
    S["zone"] = "zone:%s" % zid
    print("  %sstockpile: %s%s" % (DIM, S["zone"], OFF))

    # ---- SHAPE: bill-options ----------------------------------------------
    e = send("bill-options", {"bench": S["stove"], "cap": 60})
    shape("0.5a", "bill-options", e, "data.options", list)
    shape("0.5b", "bill-options", e, "data.total")
    shape("0.5c", "bill-options", e, "data.more")
    shape("0.5d", "bill-options", e, "data.addable")
    shape("0.5e", "bill-options", e, "data.bills_total")
    shape("0.5f", "bill-options", e, "data.bill_slots_free")
    shape("0.5g", "bill-options", e, "data.bill_cap")
    shape("0.5h", "bill-options", e, "data.source")
    if as_list(dig(e, "data.options")) or ARGS.dry_run:
        shape("0.5i", "bill-options", e, "data.options.0.recipe")
        shape("0.5j", "bill-options", e, "data.options.0.addable")
        shape("0.5k", "bill-options", e, "data.options.0.reason")
        shape("0.5l", "bill-options", e, "data.options.0.can_target_count")
    else:
        note("0.5i", "the stove published no option rows; the per-row shape "
                     "checks were not driven")

    # ---- SHAPE: bill-add / bill-set / bill-remove --------------------------
    # Phase 0 adds a bill purely to prove the envelope's shape, then removes it,
    # so phase 1 still gets to prove the full loop from an empty stack.
    e = send("bill-add", {"bench": S["stove"], "recipe": COOK_RECIPE,
                          "repeat": "forever"})
    shape("0.6a", "bill-add", e, "data.ok")
    shape("0.6b", "bill-add", e, "data.uid")
    shape("0.6c", "bill-add", e, "data.index")
    shape("0.6d", "bill-add", e, "data.configured", list)
    shape("0.6e", "bill-add", e, "data.config_refused", list)
    shape("0.6f", "bill-add", e, "data.warnings", list)
    shape("0.6g", "bill-add", e, "data.bills", list)
    shape("0.6h", "bill-add", e, "data.action.journal_seq")
    uid = dig(e, "data.uid") or ("<uid>" if ARGS.dry_run else None)
    precondition("0.6i", "the shape-contract bill was added", uid is not None,
                 "bill-add returned no uid: %s" % show(e.get("error") or dig(e, "data.reason")))

    e = send("bill-set", {"bench": S["stove"], "uid": uid, "suspended": False})
    shape("0.7a", "bill-set", e, "data.targets", list)
    shape("0.7b", "bill-set", e, "data.targets.0.changed", list)
    shape("0.7c", "bill-set", e, "data.targets.0.refused", list)
    shape("0.7d", "bill-set", e, "data.counts.targeted")
    shape("0.7e", "bill-set", e, "data.counts.changed")

    e = send("bill-remove", {"bench": S["stove"], "uid": uid})
    shape("0.7f", "bill-remove", e, "data.removed", list)
    shape("0.7g", "bill-remove", e, "data.bills", list)

    # ---- SHAPE: storage ----------------------------------------------------
    e = send("storage", {"cap": 40})
    shape("0.8a", "storage", e, "data.storages", list)
    shape("0.8b", "storage", e, "data.groups", list)
    shape("0.8c", "storage", e, "data.order")
    shape("0.8d", "storage", e, "data.skipped.tab_hidden")
    shape("0.8e", "storage", e, "data.skipped.fogged")
    if as_list(dig(e, "data.storages")) or ARGS.dry_run:
        shape("0.8f", "storage", e, "data.storages.0.target")
        shape("0.8g", "storage", e, "data.storages.0.settings")
        shape("0.8h", "storage", e, "data.storages.0.priority")
        shape("0.8i", "storage", e, "data.storages.0.priority_settable")
    else:
        note("0.8f", "no storages on this map; the per-row shape checks were "
                     "not driven")

    # ---- SHAPE: storage-set ------------------------------------------------
    # A no-op write (the priority it already has) so the shape is proved
    # without moving the colony.
    row = storage_row(S["zone"])
    keep = dig(row, "priority") or "Normal"
    e = send("storage-set", {"targets": [S["zone"]], "priority": keep})
    shape("0.9a", "storage-set", e, "data.accepted", list)
    shape("0.9b", "storage-set", e, "data.rejected", list)
    shape("0.9c", "storage-set", e, "data.bills_invalidated", list)
    shape("0.9d", "storage-set", e, "data.counts.targeted")
    shape("0.9e", "storage-set", e, "data.counts.changed")
    shape("0.9f", "storage-set", e, "data.counts.rejected")

    # ---- the two shelves, and SHAPE: storage-link --------------------------
    # Storage GROUPS are buildings only: Zone_Stockpile is not an
    # IStorageGroupMember, so the shadowed-settings trap the spec's third
    # acceptance bullet is about cannot be staged with zones.
    # NOT spawn_building() here: that one reuses an existing thing of the def,
    # which is right for a single stove and wrong for the SECOND shelf. Each
    # pass re-probes for a free cell, so shelf B lands somewhere shelf A does
    # not, and re-reads the map rather than trusting the spawn's own echo.
    shelves = [] if ARGS.dry_run else thing_ids(SHELF_DEF)
    for _ in range(3):
        if ARGS.dry_run or len(shelves) >= 2:
            break
        spot = free_spot(S["at"])
        if spot is None:
            break
        send("dev:spawn-thing", {"def": SHELF_DEF, "pos": spot, "mode": "direct"})
        grown = thing_ids(SHELF_DEF)
        if len(grown) <= len(shelves):
            break
        shelves = grown
    if ARGS.dry_run:
        shelves = ["<shelfA>", "<shelfB>"]
    have_shelves = len(shelves) >= 2
    if not have_shelves:
        note("0.10", "fewer than two %s could be placed; storage-link's shape "
                     "and the group-trap phase cannot be driven" % SHELF_DEF)
        S["shelfA"] = S["shelfB"] = None
        return
    S["shelfA"], S["shelfB"] = "thing:%s" % shelves[0], "thing:%s" % shelves[1]
    print("  %sshelves: %s %s%s" % (DIM, S["shelfA"], S["shelfB"], OFF))

    e = send("storage-link", {"targets": [S["shelfA"], S["shelfB"]]})
    shape("0.10a", "storage-link", e, "data.group.id")
    shape("0.10b", "storage-link", e, "data.created")
    shape("0.10c", "storage-link", e, "data.linked")
    shape("0.10d", "storage-link", e, "data.storages", list)
    # Left UNLINKED for phase 4, which proves the behaviour rather than the
    # shape and needs to do the linking itself.
    send("storage-unlink", {"targets": [S["shelfA"], S["shelfB"]]})


# ------------------------------------------------------------------- phase 1 --

def phase1():
    banner("PHASE 1 - THE FULL LOOP: a bill, an advance, and meals that did "
           "not exist before")
    bench = S["stove"]

    # The bill must be one the game would actually OFFER. `bill-options` is
    # ITab_Bills.FillTab's own options maker as data, so `addable:true` here is
    # "the float-menu row exists", not "AddBill would accept it" — AddBill
    # accepts anything.
    e = send("bill-options", {"bench": bench, "cap": 100})
    row = None
    for r in as_list(dig(e, "data.options")):
        if isinstance(r, dict) and r.get("recipe") == COOK_RECIPE:
            row = r
            break
    if ARGS.dry_run:
        row = {"addable": True, "reason": None}
    precondition("1.1a", "%s is on the %s" % (COOK_RECIPE, STOVE_DEF),
                 row is not None,
                 "bill-options lists no %s row for this bench" % COOK_RECIPE)
    check("1.1b", "`bill-options` reports %s as addable" % COOK_RECIPE,
          row.get("addable") is True, "addable:true", row)

    # THE COLLAPSED GATE IS GONE. Before this round every non-research clause
    # of RecipeDef.AvailableNow was reported as one "ideo-or-faction" gate with
    # a sentence listing all three possibilities. Each is now named separately,
    # so no row may carry the old one.
    stale = [r.get("recipe") for r in as_list(dig(e, "data.options"))
             if isinstance(r, dict) and isinstance(r.get("reason"), str)
             and r["reason"].startswith("ideo-or-faction")]
    check("1.1c", "no option row still carries the collapsed `ideo-or-faction` "
                  "gate (meme / faction-recipe-tag / ideo-precept are named "
                  "separately now)", not stale, "no such row", stale)

    # A mechanitor-only recipe with a mechanitor PRESENT must still report the
    # skill warning: FillTab's mechanitor CONDITION includes !Any(IsMechanitor),
    # so the else-if is only skipped when that branch actually fired. Only
    # drivable on a Biotech colony that has such a recipe on this bench.
    mech_rows = [r.get("recipe") for r in as_list(dig(e, "data.options"))
                 if isinstance(r, dict) and r.get("mechanitor_only") is True]
    if not mech_rows:
        note("1.1d", "no mechanitor-only recipe on this bench (Biotech off, or "
                     "the wrong bench), so AddWarnings' fall-through to the "
                     "skill branch is STATIC-ONLY on this run")

    # ---- COUNT THE MEALS, not the bill object ------------------------------
    before = 0 if ARGS.dry_run else count_of(MEAL_DEF)
    print("  %s%s before: %s%s" % (DIM, MEAL_DEF, before, OFF))

    e = send("bill-add", {"bench": bench, "recipe": COOK_RECIPE,
                          "repeat": "forever"})
    eq("1.2a", "the cook bill is added", e, "data.ok", True)
    eq("1.2b", "at repeat:forever", e, "data.bills.0.repeat_mode", "Forever")
    ge("1.2c", "and it journals", e, "data.action.journal_seq", 1)
    S["uid"] = dig(e, "data.uid")

    # The bill is not a job. A colonist picks it up on a think tick, and on a
    # FueledStove the first job is usually a REFUEL. Advanced in chunks so the
    # run stops as soon as a meal exists rather than always paying the worst
    # case, and so a fixture problem is visible in the printed trail.
    after = before
    for i in range(6):
        if ARGS.dry_run:
            after = before + 1
            break
        send("advance", {"ticks": 4000})
        after = count_of(MEAL_DEF)
        print("  %sadvance %d: %s = %s%s" % (DIM, i + 1, MEAL_DEF, after, OFF))
        if after > before:
            break

    check("1.3", "a %s exists that did not before - THE FULL LOOP, not just "
                 "that the bill object appeared" % MEAL_DEF,
          after > before,
          "%s count > %d" % (MEAL_DEF, before), after)
    if after <= before and not ARGS.dry_run:
        print("          %sif the bill is present and unworked, read `bills "
              "{bench:%s}`: next_ingredient_search_tick in the future means a "
              "failing ingredient search ran, and an unfuelled stove yields a "
              "refuel job first.%s" % (DIM, bench, OFF))


# ------------------------------------------------------------------- phase 2 --

def phase2():
    banner("PHASE 2 - THE RESEARCH REFUSAL, and then the same call succeeding")

    # Labels first, so the refusal's reason can be matched against a project
    # that DEMONSTRABLY exists rather than against "some non-empty string".
    e = send("research", {"cap": 200, "include_finished": True})
    label_to_def = {}
    for key in ("data.available.list", "data.finished"):
        for p in as_list(dig(e, key)):
            if isinstance(p, dict) and p.get("label") and p.get("def"):
                label_to_def.setdefault(p["label"], p["def"])
    unfinished = {}
    for p in as_list(dig(e, "data.available.list")):
        if isinstance(p, dict) and p.get("label") and p.get("def"):
            unfinished[p["label"]] = p["def"]
    if ARGS.dry_run:
        unfinished = {"<Project>": "<ProjectDef>"}

    # Find a bench with a recipe gated on one of THOSE projects. The stove
    # first; the rest are spawned only if the stove has none.
    found = None
    for bench_def in RESEARCH_BENCHES:
        if ARGS.dry_run:
            found = ("<bench>", "<recipe>", "<Project>", "research: <Project>")
            break
        if bench_def is None:
            bench = S["stove"]
        else:
            bench, _ = spawn_building(bench_def, S["at"])
            if bench is None:
                continue
        opts = send("bill-options", {"bench": bench, "cap": 200})
        for r in as_list(dig(opts, "data.options")):
            if not isinstance(r, dict) or r.get("addable") is not False:
                continue
            reason = r.get("reason") or ""
            if not reason.startswith("research:"):
                continue
            for label in unfinished:
                if label in reason:
                    found = (bench, r.get("recipe"), label, reason)
                    break
            if found:
                break
        if found:
            break

    precondition("2.1", "a recipe blocked by an UNFINISHED research project",
                 found is not None,
                 "no bench among %s offered a research-gated recipe whose "
                 "project is still unfinished. On a colony that has already "
                 "finished everything this phase cannot run."
                 % ", ".join(str(b) for b in RESEARCH_BENCHES))
    bench, recipe, label, _ = found
    print("  %sgated: %s on bench %s, blocked by %r%s"
          % (DIM, recipe, bench, label, OFF))

    bills_before = 0 if ARGS.dry_run else bench_bill_count(bench)

    e = send("bill-add", {"bench": bench, "recipe": recipe})
    eq("2.2a", "the bill is REFUSED, not queued", e, "data.ok", False)
    eq("2.2b", "the gate is named `research` (not a collapsed catch-all)",
       e, "data.gate", "research")
    contains("2.2c", "the reason names the blocking project's LabelCap (%r)" % label,
             dig(e, "data.reason"), label)
    # Vanilla authors NO string here - it omits the row entirely - so the
    # refusal must say the words are ours. git-bug 48f666c comment #2,
    # correction 1 is exactly this point.
    contains("2.2d", "and marks itself MOD-AUTHORED, because vanilla omits the "
                     "row rather than explaining it",
             dig(e, "data.reason"), "MOD-AUTHORED")
    # NOT a Stamp with seq 0 - an explicit null, from NoAction().
    check("2.2e", "nothing was journalled (`action.journal_seq` is an explicit "
                  "null, not a seq)",
          has_key(e, "data.action.journal_seq")
          and dig(e, "data.action.journal_seq") is None or ARGS.dry_run,
          "the key present and null", dig(e, "data.action"))
    after = bills_before if ARGS.dry_run else bench_bill_count(bench)
    check("2.2f", "and the bench's bill count did not move",
          after == bills_before, "still %d" % bills_before, after)

    # ---- finish it, and re-run THE SAME CALL -------------------------------
    # This is what proves the gate was the research and not a typo in the
    # recipe name: only the research state changed between the two calls.
    if not ARGS.dry_run:
        for lbl, defname in unfinished.items():
            if lbl in (dig(e, "data.reason") or ""):
                send("dev:finish-research", {"project": defname})
    e = send("bill-add", {"bench": bench, "recipe": recipe})
    eq("2.3a", "with the research finished, THE SAME CALL succeeds",
       e, "data.ok", True)
    ge("2.3b", "and it journals", e, "data.action.journal_seq", 1)
    if dig(e, "data.ok") is True:
        send("bill-remove", {"bench": bench, "uid": dig(e, "data.uid")})


# ------------------------------------------------------------------- phase 3 --

def phase3():
    banner("PHASE 3 - `bad-args`, NOT A THROW - and a rejected call changes "
           "NOTHING")
    bench = S["stove"]
    uid = ensure_bill(bench)
    precondition("3.1", "a bill on the stove to move", uid is not None,
                 "could not add a %s bill to bench %s" % (COOK_RECIPE, bench))

    rows = bills_on(bench)
    count = len(rows) if not ARGS.dry_run else 1
    uids_before = sorted(str(r.get("uid")) for r in rows) if not ARGS.dry_run else ["<uid>"]

    def still_there(num, what):
        """The REAL assertion. BillStack.Reorder removes the bill and THEN
        calls List.Insert, which throws past the end - with the bill already
        gone. `bad-args` is only half the claim; `did not lose the bill` is the
        other half and it is the one that would have caught the vanilla bug."""
        now = sorted(str(r.get("uid")) for r in bills_on(bench)) \
            if not ARGS.dry_run else uids_before
        check(num, what, now == uids_before,
              "the same %d bill(s): %s" % (len(uids_before), uids_before), now)

    # to == Count: one past the last valid index. This is the exact value that
    # makes vanilla's Insert(Count, ...) throw after the Remove.
    e = send("bill-reorder", {"bench": bench, "index": 0, "to": count})
    eq("3.2a", "`to` one past the end is bad-args", e, "error.code", "bad-args")
    still_there("3.2b", "AND THE BILL IS STILL IN THE STACK")

    e = send("bill-reorder", {"bench": bench, "index": 0, "offset": 99})
    eq("3.3a", "a wild `offset` is bad-args", e, "error.code", "bad-args")
    still_there("3.3b", "and the bill is still in the stack")

    e = send("bill-reorder", {"bench": bench, "index": 0, "to": -1})
    eq("3.4a", "a negative `to` is bad-args", e, "error.code", "bad-args")
    still_there("3.4b", "and the bill is still in the stack")

    e = send("bill-reorder", {"bench": bench, "index": 0, "to": 0})
    eq("3.5a", "a reorder to where it already is reports moved:false",
       e, "data.moved", False)
    ge("3.5b", "and STILL journals - a redundant order the ledger cannot see is "
               "a redundant order nobody learns from (git-bug 4087644)",
       e, "data.action.journal_seq", 1)

    # ---- the same assertion shape for the two verbs that used to mutate ----
    # BEFORE reporting bad-args. bill-add ran AddBill and then parsed its
    # config args, so a typo left the bill in the stack and reported a clean
    # rejection; the agent retried and had two bills.
    before = bench_bill_count(bench)
    e = send("bill-add", {"bench": bench, "recipe": COOK_RECIPE,
                          "repeat": "forver"})
    eq("3.6a", "`bill-add` with a misspelled repeat mode is bad-args",
       e, "error.code", "bad-args")
    after = before if ARGS.dry_run else bench_bill_count(bench)
    check("3.6b", "AND NO BILL WAS ADDED - the parse happens before AddBill now",
          after == before, "still %d bill(s)" % before, after)

    e = send("bill-add", {"bench": bench, "recipe": COOK_RECIPE,
                          "ingredient_radius": 1})
    eq("3.7a", "`bill-add` with an out-of-slider ingredient_radius is bad-args",
       e, "error.code", "bad-args")
    after = before if ARGS.dry_run else bench_bill_count(bench)
    check("3.7b", "and no bill was added", after == before,
          "still %d bill(s)" % before, after)

    # storage-set wrote `priority` and THEN parsed `quality_range`, so a bad
    # quality name left the priority changed with NO JOURNAL ROW at all - an
    # unprovenanced state change, which is what Stamp/NoAction exist to stop.
    row = storage_row(S["zone"])
    keep = dig(row, "priority") if not ARGS.dry_run else "Normal"
    target_pr = "Critical" if keep != "Critical" else "Low"
    e = send("storage-set", {"targets": [S["zone"]], "priority": target_pr,
                             "quality_range": ["Bogus", "Normal"]})
    eq("3.8a", "`storage-set` with a bad quality name is bad-args",
       e, "error.code", "bad-args")
    now = dig(storage_row(S["zone"]), "priority") if not ARGS.dry_run else keep
    check("3.8b", "AND THE PRIORITY DID NOT MOVE - it used to be written before "
                  "the quality parse threw, with no journal row",
          now == keep, "still %r" % keep, now)

    e = send("storage-set", {"targets": [S["zone"]], "priority": target_pr,
                             "filter": "most"})
    eq("3.9a", "`storage-set` with an unknown filter word is bad-args",
       e, "error.code", "bad-args")
    now = dig(storage_row(S["zone"]), "priority") if not ARGS.dry_run else keep
    check("3.9b", "and the priority did not move", now == keep,
          "still %r" % keep, now)


# ------------------------------------------------------------------- phase 4 --

def phase4():
    banner("PHASE 4 - THE GROUP TRAP: a write to one shelf is a write to all "
           "of them")
    precondition("4.1", "two %s buildings" % SHELF_DEF,
                 ARGS.dry_run or (S.get("shelfA") and S.get("shelfB")),
                 "phase 0 could not place two shelves; the shadowed-settings "
                 "bullet needs BUILDINGS - Zone_Stockpile is not an "
                 "IStorageGroupMember and cannot be grouped at all")
    a, b = S["shelfA"], S["shelfB"]

    # A single target cannot be linked: the gizmo is disabled below two
    # members ("LinkStorageDisabledSelectTwo").
    e = send("storage-link", {"targets": [a]})
    eq("4.2a", "linking ONE storage is refused", e, "data.ok", False)
    eq("4.2b", "with the gizmo's own gate", e, "data.gate", "select-two")

    e = send("storage-link", {"targets": [a, b]})
    eq("4.3a", "linking two shelves succeeds", e, "data.ok", True)
    eq("4.3b", "and reports both linked", e, "data.linked", 2)
    ge("4.3c", "and it journals", e, "data.action.journal_seq", 1)
    gid = dig(e, "data.group.id")

    # BOTH now answer GetStoreSettings() with the GROUP's object. That is the
    # whole trap: `Building_Storage.settings` is still there, still public,
    # and nothing reads it any more.
    eqv("4.4a", "shelf A now reads its settings from the GROUP's object",
        dig(storage_row(a), "settings"), "group")
    eqv("4.4b", "and so does shelf B", dig(storage_row(b), "settings"), "group")

    # THE SPEC'S THIRD ACCEPTANCE BULLET. Write A, read B.
    b_before = dig(storage_row(b), "priority") if not ARGS.dry_run else "Normal"
    want = "Critical" if b_before != "Critical" else "Low"
    e = send("storage-set", {"targets": [a], "priority": want})
    eq("4.5a", "setting a priority on shelf A is accepted", e,
       "data.counts.changed", 1)
    eq("4.5b", "and A's own row says the write went to the GROUP's object",
       e, "data.accepted.0.settings", "group")
    b_now = dig(storage_row(b), "priority") if not ARGS.dry_run else want
    check("4.5c", "AND SHELF B'S PRIORITY MOVED TOO - the shadowed-object trap "
                  "was avoided (a write to Building_Storage.settings would have "
                  "gone nowhere)",
          b_now == want, "%r" % want, b_now)

    # Unlinking one dissolves the group, because a group of one is not a group.
    e = send("storage-unlink", {"targets": [a]})
    eq("4.6a", "unlinking shelf A succeeds", e, "data.ok", True)
    dissolved = [d.get("group") for d in as_list(dig(e, "data.groups_dissolved"))
                 if isinstance(d, dict)]
    check("4.6b", "and `groups_dissolved` reports the group going - "
                  "StorageGroupManager.Notify_MemberRemoved dissolves at "
                  "MemberCount <= 1 and unlinks the LAST member too",
          gid in dissolved or ARGS.dry_run, "group %s in %s" % (gid, dissolved),
          dig(e, "data.groups_dissolved"))
    b_row = storage_row(b)
    eqv("4.7a", "the survivor is back on its OWN settings object",
        dig(b_row, "settings"), "own")
    kept = dig(b_row, "priority") if not ARGS.dry_run else want
    check("4.7b", "and KEPT what it had inside the group (SetStorageGroup "
                  "copies the group's settings back before clearing Group)",
          kept == want, "%r" % want, kept)

    # An already-linked pair takes the gizmo's other disable.
    send("storage-link", {"targets": [a, b]})
    e = send("storage-link", {"targets": [a, b]})
    eq("4.8a", "re-linking an already-linked pair is refused", e, "data.ok", False)
    eq("4.8b", "with the gizmo's `AlreadyLinked` gate", e, "data.gate",
       "already-linked")
    check("4.8c", "and nothing was journalled",
          has_key(e, "data.action.journal_seq")
          and dig(e, "data.action.journal_seq") is None or ARGS.dry_run,
          "the key present and null", dig(e, "data.action"))
    send("storage-unlink", {"targets": [a, b]})


# ------------------------------------------------------------------- phase 5 --

def phase5():
    banner("PHASE 5 - THE LEVERS, including the coupling a half-verb would "
           "pass without")
    bench = S["stove"]
    uid = ensure_bill(bench)
    precondition("5.1", "a bill on the stove to configure", uid is not None,
                 "could not add a %s bill to bench %s" % (COOK_RECIPE, bench))

    # Dialog_BillConfig.DoWindowContents runs, right after the target IntEntry:
    #   bill.unpauseWhenYouHave = Mathf.Max(0, bill.unpauseWhenYouHave
    #                                          + (bill.targetCount - old));
    # A verb that writes only targetCount leaves a threshold the player never
    # chose. A SUITE THAT CHECKS ONLY `target` PASSES AGAINST THAT HALF-VERB,
    # which is why both are asserted here.
    e = send("bill-set", {"bench": bench, "uid": uid,
                          "repeat": "target", "target": 20})
    changed = fields_of(dig(e, "data.targets.0.changed"))
    check("5.2a", "`target` is reported as configured", "target" in changed,
          "'target' among the changed fields", changed)
    check("5.2b", "AND SO IS `unpause_when_you_have` - the Dialog_BillConfig "
                  "coupling is part of the click, not an extra",
          "unpause_when_you_have" in changed,
          "'unpause_when_you_have' among the changed fields", changed)
    eq("5.2c", "and the bill is in TargetCount mode", e,
       "data.bills.0.repeat_mode", "TargetCount")

    # A lever whose widget is not drawn for THIS recipe is refused BY NAME, not
    # written behind the game's back: includeTainted is still READ by
    # RecipeWorkerCounter.CountValidThing, so a silent write changes what
    # "currently have" means.
    e = send("bill-set", {"bench": bench, "uid": uid, "include_tainted": True})
    refused = fields_of(dig(e, "data.targets.0.refused"))
    changed = fields_of(dig(e, "data.targets.0.changed"))
    check("5.3a", "`include_tainted` on a non-apparel recipe is REFUSED",
          "include_tainted" in refused, "'include_tainted' among refused", refused)
    eqv("5.3b", "with the gate named after the widget's own condition",
        dig(entry_for(dig(e, "data.targets.0.refused"), "include_tainted"), "gate"),
        "not-tainting-apparel")
    check("5.3c", "and it is ABSENT from `changed` - nothing was written",
          "include_tainted" not in changed, "not among changed", changed)

    # The "Include from" lever. It was the one Dialog_BillConfig control this
    # file neither implemented nor refused by name; silence is the option that
    # does not match the file's pattern, so the assertion is that the call is
    # never silent about it.
    e = send("bill-set", {"bench": bench, "uid": uid, "include_from": "all"})
    changed = fields_of(dig(e, "data.targets.0.changed"))
    check("5.4a", "`include_from:\"all\"` is written (the IncludeFromAll option, "
                  "SetIncludeGroup(null))",
          "include_from" in changed, "'include_from' among changed", changed)
    eq("5.4b", "and reads back as no specific include group",
       e, "data.bills.0.include_group", None)

    e = send("bill-set", {"bench": bench, "uid": uid, "include_from": S["zone"]})
    changed = fields_of(dig(e, "data.targets.0.changed"))
    refused = fields_of(dig(e, "data.targets.0.refused"))
    check("5.4c", "`include_from:<a stockpile>` is either written or refused BY "
                  "NAME - never silently dropped",
          "include_from" in changed or "include_from" in refused or ARGS.dry_run,
          "'include_from' in changed or refused",
          {"changed": changed, "refused": dig(e, "data.targets.0.refused")})

    # Back to something a colony can actually work.
    send("bill-set", {"bench": bench, "uid": uid, "include_from": "all"})
    send("bill-set", {"bench": bench, "uid": uid, "repeat": "forever"})


# ------------------------------------------------------------------- phase 6 --

def phase6():
    banner("PHASE 6 - THE INVARIANT: zero red errors, after deliberately "
           "walking every dodged path")
    bench = S["stove"]
    uid = ensure_bill(bench)

    # Bill_Production.SetStoreMode Log.ErrorOnce's on a mode/group mismatch AND
    # STORES THE VALUES ANYWAY, so the pairing has to be validated before the
    # call or the verb authors a red error on a plain typo.
    e = send("bill-set", {"bench": bench, "uid": uid, "store_mode": "specific"})
    refused = fields_of(dig(e, "data.targets.0.refused"))
    check("6.1a", "`store_mode:\"specific\"` with no `store_target` is refused, "
                  "not passed to SetStoreMode",
          "store_mode" in refused, "'store_mode' among refused", refused)
    eqv("6.1b", "with the gate that names the missing half",
        dig(entry_for(dig(e, "data.targets.0.refused"), "store_mode"), "gate"),
        "missing-store-target")

    # A PAWN as the bill giver. ThingRequestGroup.PotentialBillGiver is
    # `!def.AllRecipes.NullOrEmpty()`, which INCLUDES humanlike race defs, so a
    # pawn id resolves here and the ROW verbs work on a surgery queue. The ADD
    # path does not, because ITab_Bills.SelTable is a hard cast to
    # Building_WorkTable and there is no widget to reproduce.
    e = send("bill-options", {"bench": S["cook"]})
    eq("6.2a", "`bill-options` on a PAWN is refused", e, "data.ok", False)
    eq("6.2b", "with the work-table gate", e, "data.gate", "not-a-work-table")
    contains("6.2c", "and it names 3.4's surgery route rather than shrugging",
             dig(e, "data.reason"), "surgery-")

    # A Bill_Medical through bill-set: the row buttons (suspend / reorder / X)
    # ARE drawn in the Health tab, so they are reachable; every
    # Dialog_BillConfig lever is refused by name because
    # Bill_Production.GetBillDialog() is the only thing that opens that window.
    e = send("surgery-options", {"pawn": S["cook"], "cap": 60})
    surg = None
    for r in as_list(dig(e, "data.options")):
        if isinstance(r, dict) and r.get("addable") is True and r.get("recipe"):
            surg = r
            break
    if ARGS.dry_run:
        surg = {"recipe": "<surgery>", "part_def": None}
    if surg is None:
        note("6.3", "no addable surgery on the cook, so the Bill_Medical branch "
                    "of `bill-set` was not driven on this run")
    else:
        # `part` matters: a part-scoped surgery added without one is a
        # different bill, or a refusal.
        add_args = {"pawn": S["cook"], "recipe": surg["recipe"]}
        if surg.get("part_def"):
            add_args["part"] = surg["part_def"]
        added = send("surgery-add", add_args)
        if dig(added, "data.ok") is not True and not ARGS.dry_run:
            note("6.3", "surgery-add refused (%s), so the Bill_Medical branch "
                        "was not driven" % show(dig(added, "data.gate")))
        else:
            e = send("bill-set", {"bench": S["cook"], "index": 0,
                                  "suspended": True, "repeat": "forever"})
            changed = fields_of(dig(e, "data.targets.0.changed"))
            refused = fields_of(dig(e, "data.targets.0.refused"))
            check("6.3a", "`suspended` DOES apply to a medical bill - Bill.cs "
                          "DoInterface draws that button in every listing",
                  "suspended" in changed, "'suspended' among changed", changed)
            check("6.3b", "and `repeat` is refused by name on a non-production "
                          "bill rather than written behind the game's back",
                  "repeat" in refused, "'repeat' among refused", refused)
            eqv("6.3c", "with the gate that names the missing dialog",
                dig(entry_for(dig(e, "data.targets.0.refused"), "repeat"), "gate"),
                "not-production")
            send("surgery-remove", {"pawn": S["cook"], "all": True})

    # A GRAVE. Building_Grave.StorageTabVisible is
    # `base.StorageTabVisible ? AssignedPawn == null : false`, over
    # Building_CorpseCasket's `!HasCorpse` - so an occupied or assigned grave
    # has no storage tab at all, and this verb must not write one anyway.
    grave, _ = spawn_building(GRAVE_DEF, S["at"]) if not ARGS.dry_run else ("<grave>", False)
    if grave is None:
        note("6.4", "no %s could be placed, so the corpse-casket tab gate was "
                    "not driven" % GRAVE_DEF)
    else:
        token = "thing:%s" % grave
        e = send("storage", {"id": token})
        shape("6.4a", "storage", e, "data.storages.0.priority_settable")
        e = send("storage-set", {"targets": [token], "priority": "Low"})
        eq("6.4b", "`storage-set` on a grave answers rather than throwing",
           e, "data.ok", True)
        acc = as_list(dig(e, "data.accepted"))
        rej = as_list(dig(e, "data.rejected"))
        check("6.4c", "and the grave is either accepted or rejected with a NAMED "
                      "gate (an occupied or assigned grave has no storage tab)",
              len(acc) + len(rej) == 1
              and (not rej or isinstance(rej[0], dict) and rej[0].get("gate"))
              or ARGS.dry_run,
              "exactly one outcome, gated if rejected",
              {"accepted": acc, "rejected": rej})

    # ---- THE INVARIANT ------------------------------------------------------
    e = send("journal", {"since_seq": S["seq0"], "types": ["red_error"],
                         "limit": 50})
    eq("6.5", "ZERO RED ERRORS across the whole run", e, "data.count", 0)
    if dig(e, "data.count"):
        for ev in as_list(dig(e, "data.events"))[:5]:
            print("          %s%s%s" % (RED, show(ev), OFF))


# ---------------------------------------------------------------------- main --

PHASES = {1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5, 6: phase6}


def main():
    global ARGS
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--root", default=DEFAULT_ROOT, help="the protocol root")
    p.add_argument("--phase", type=int, action="append",
                   choices=[0, 1, 2, 3, 4, 5, 6],
                   help="run only these phases (repeatable); phase 0 always runs")
    p.add_argument("--dry-run", action="store_true",
                   help="print the plan and every expectation, send nothing")
    p.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = p.parse_args()

    print("AutoRimmer acceptance - production bills + storage settings (48f666c)")
    print("protocol root: %s" % ARGS.root)
    if not ARGS.dry_run and not os.path.exists(os.path.join(ARGS.root, "status.json")):
        print("%sno status.json under %s - start the bench with "
              "_RimWorld-Agent/run-agent.sh, or pass --root%s" % (RED, ARGS.root, OFF))
        sys.exit(2)
    os.makedirs(os.path.join(ARGS.root, "commands"), exist_ok=True)

    wanted = sorted(set(ARGS.phase or [1, 2, 3, 4, 5, 6]) - {0})
    print("phases: 0 + %s" % ", ".join(str(x) for x in wanted))

    phase0()
    for n in wanted:
        PHASES[n]()

    print("")
    print("=" * 78)
    if ARGS.dry_run:
        # NOT "passed". --dry-run sent nothing, so no dig path was exercised and
        # no expectation was evaluated. Saying "passed" here is the exact
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
