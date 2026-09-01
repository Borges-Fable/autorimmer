#!/usr/bin/env python3
"""Acceptance runner for 261f2e9 — the temperature actuator, the digest section
that watches rooms, and the rot term `food_days` never had.

Same protocol, helpers and exit codes as `accept/722c951-advance-halt.py` and
`accept/fc287ba-until-state.py`; read either file's header first, especially the
SHAPE CONTRACT note — `eq(..., None)` PASSES on an absent key, so phase 0 proves
every dig path exists before any later phase leans on it.

    ./accept/261f2e9-temperature.py --selftest   # offline: no bench, no game
    ./accept/261f2e9-temperature.py --dry-run    # print the plan, send nothing
    ./accept/261f2e9-temperature.py              # the sweep: 0,1,2,3,4,5
    ./accept/261f2e9-temperature.py --phase 6    # opt-in: the COLD half, slow

Exit 0 = every check passed · 1 = at least one FAIL · 2 = a fixture
precondition could not be met, which is NOT a spec failure and says so.
**Read the exit code from `$?`, not from a pipe** — session 12 reported EXIT=0
for a command it had piped to `tail`, and read `tail`'s status.

WHY THIS SUITE EXISTS, in one sentence: session 18 built a freezer, wired it,
watched `digest.power.draw_w` go 0 -> 40, and read 14.6 C — because a `Cooler`
holds `CompTempControl.Props.defaultTargetTemperature` of 21 C and no verb could
tell it otherwise, while nothing in the observation surface said the freezer was
at room temperature and `resources.food_days` has no rot term at all.

  * PHASE 1 — `temp-control` READS and mutates nothing. Every documented field
    is present on a real controller; two identical reads produce identical rows.
  * PHASE 2 — `temp-set` sets a target and it READS BACK, from the comp and not
    from a projection. Journaled with before/after. `dry_run` changes nothing.
  * PHASE 3 — THE REFUSALS. A building with no `CompTempControl` is refused BY
    NAME with the game's own reason (a `Vent` if the map has one: it is in the
    Temperature build category and has no target at all). A target outside the
    game's real clamp is `bad-args`. A target outside the DEF's advertised range
    is NOT refused — it is set with an advisory, because nothing in the 1.6 tree
    reads `CompProperties_TempControl.minTargetTemperature` and a verb that
    refused there would refuse what a player can do with the -10 button.
  * PHASE 4 — `digest.temperature` SHOWS AN OUT-OF-RANGE ROOM. Deterministic and
    instant: drive a controller's target far past its room's temperature and the
    section's `ok` goes false, `out_of_range` names the room id, the row's
    `drift_c` matches, and putting the target back makes it true again. No
    power and no advance required — the verdict is temperature versus target.
  * PHASE 5 — THE ROT HALF, which is the one that matters. Food in a warm room
    is visibly deteriorating where before it was invisible: `food_rot` publishes
    the bands, the clock and `spoiled_stacks`, `soonest_rot_days` FALLS across
    an advance while `food_days` does not move, and the new field is reachable
    as an `advance until:{condition:…}` predicate — which is the proof that it
    is a registered predicate section.
  * PHASE 6 — OPT-IN, SLOW, and the only phase that needs power: a real cooler
    driving a real room below its starting temperature. This is the session-18
    fixture's own criterion and it is where the orchestrator's bench does the
    work; see the .md for the recipe.

IT SPAWNS THINGS. Phases 2-5 may `dev:spawn-thing` a `Heater` and a stack of
`MealSimple` if the map has none, and it does NOT clean them up (there is no
`dev:destroy` call here; the heater is left at its vanilla 21 C default and the
meals are left where they fell). Run it on a bench you are willing to dirty.

NO BARE TICK COUNTS. The one advance in phase 5 is bounded by a predicate over
the field under test — `resources.food_rot.soonest_rot_days <= <read value>` —
with a `timeout_ticks` ceiling, because an `until` whose predicate is already
true at arm time runs UNBOUNDED (git-bug 1113019). That predicate is not a
convenience: arming it is how this suite proves `temperature`/`food_rot` are
addressable at all.
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
SEQ = 0

# The reason handed to `advance`'s escapes. It names the file, because the whole
# point of a required reason is that a post-mortem grepping
# `journal --types action` can tell WHO turned a guard off and why.
WHY = ("accept/261f2e9-temperature.py: this suite advances to let rot progress, "
       "and reads the journal only where it is proving something — the unread "
       "watermark and the casualty halt are not the subject here")

# The game's own clamp, from RimWorld/CompTempControl.InterfaceChangeTargetTemperature.
CLAMP_MIN_C = -273.15
CLAMP_MAX_C = 1000.0
# CompProperties_TempControl's advertised, UNENFORCED range. Phase 3 proves this
# is an advisory rather than a refusal.
DEF_MIN_C = -50.0
# TemperatureVerbs.ToleranceC — ours, and published on every read so a suite
# never has to hardcode it blind. Read back in phase 0 and compared.
TOLERANCE_C = 2.0

UNTIL_TIMEOUT_TICKS = 20000

# The fixture defs. Heater rather than Cooler for the spawned fallback: a
# `Cooler` is `passability: Impassable` and expects to sit IN a wall, and
# `Building_Cooler.TickRare` does nothing unless BOTH its north and south cells
# are passable — so a bare-ground cooler is the one thing that would make phase
# 4's verdict undecidable. A `Heater` acts on its own room and has no such
# geometry (RimWorld/Building_Heater.TickRare).
HEATER_DEF = "Heater"
FOOD_DEF = "MealSimple"
FOOD_COUNT = 20


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=300):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acc261f2e9-%03d-%s" % (SEQ, slug)
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


def advance(args, timeout=900):
    """Every advance in this suite declares both escapes.

    Neither guard is the subject here (that is `accept/722c951-advance-halt.py`),
    and an unread-journal refusal or a stray casualty halt in the middle of a rot
    measurement would present as a spec failure it is not. `timeout_ticks` is
    forced on any `until`: without one, an `until` whose predicate is already
    true at arm time runs UNBOUNDED — git-bug 1113019, found on 2026-09-01 by a
    suite that then burned three in-game days."""
    a = dict(args)
    if "until" in a and "timeout_ticks" not in a:
        a["timeout_ticks"] = UNTIL_TIMEOUT_TICKS
    a["unread_ok"] = WHY
    a["through_casualties"] = WHY
    return send("advance", a, timeout=timeout)


# ------------------------------------------------------------------- digging --

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
    `soonest_rot_days` is DELIBERATELY null when nothing is rotting, and
    `target_c` is deliberately null when a room's controllers disagree. Both are
    meaningful values, and both are indistinguishable from a typo'd path under
    dig() alone."""
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
    return "null" if v is None else json.dumps(v, separators=(",", ":"))[:500]


def num(v):
    return v if isinstance(v, (int, float)) and not isinstance(v, bool) else None


# ------------------------------------------------------------------- asserts --

def check(numid, what, ok, expected, actual):
    global CHECKS
    CHECKS += 1
    if ARGS.dry_run:
        print("  %-7s EXPECT  %s: %s" % (numid, what, expected))
        return
    if ok:
        print("  %s%-7s PASS    %s%s" % (GREEN, numid, what, OFF))
        return
    print("  %s%-7s FAIL    %s%s" % (RED, numid, what, OFF))
    print("          expected: %s" % expected)
    print("          actual:   %s" % show(actual))
    FAILS.append(numid)


def eq(numid, what, env, path, want):
    got = dig(env, path)
    ok = (want is None and got is None) or got == want
    check(numid, "%s (%s)" % (what, path), ok, show(want), got)


def ge(numid, what, env, path, want):
    got = num(dig(env, path))
    ok = got is not None and got >= want
    check(numid, "%s (%s)" % (what, path), ok, ">= %s" % want, dig(env, path))


def close(numid, what, got, want, tol=0.15):
    ok = num(got) is not None and abs(got - want) <= tol
    check(numid, what, ok, "%s +/- %s" % (want, tol), got)


def contains(numid, what, env, path, needle):
    got = dig(env, path)
    ok = isinstance(got, str) and needle.lower() in got.lower()
    check(numid, "%s (%s)" % (what, path), ok, "a string containing %r" % needle, got)


def refused(numid, what, env, code, needle=None):
    """A REFUSAL IS THE ASSERTION. ok:false with the named code, and — when a
    needle is given — a detail that actually says the thing. A refusal with a
    useless message is only half the fix."""
    got = dig(env, "error.code")
    ok = dig(env, "ok") is False and got == code
    if ok and needle is not None:
        ok = needle.lower() in (dig(env, "error.detail") or "").lower()
    check(numid, what, ok, "ok:false, code %s%s"
          % (code, "" if needle is None else ", detail naming %r" % needle),
          {"ok": dig(env, "ok"), "code": got,
           "detail": (dig(env, "error.detail") or "")[:400]})


def note(numid, text):
    print("  %s%-7s NOTE    %s%s" % (YELLOW, numid, text, OFF))


def precondition(numid, what, ok, detail):
    if ARGS.dry_run:
        print("  %s%-7s NEEDS   %s%s" % (CYAN, numid, what, OFF))
        return
    if ok:
        print("  %s%-7s OK      precondition: %s%s" % (DIM, numid, what, OFF))
        return
    print("  %s%-7s SKIP    precondition NOT MET: %s%s" % (YELLOW, numid, what, OFF))
    print("          %s" % detail)
    print("          This is a FIXTURE gap, not a failure of the spec.")
    sys.exit(2)


def shape(numid, verb, env, path, kind=None):
    ok = has_key(env, path)
    want = "the key to be present"
    if ok and kind is not None:
        v = dig(env, path)
        # A null is an ALLOWED value for several of these fields; the shape
        # contract is about the KEY existing. `kind` narrows only a non-null.
        if v is not None:
            ok = isinstance(v, kind)
        want += " and (when non-null) a %s" % (
            kind.__name__ if isinstance(kind, type)
            else "/".join(k.__name__ for k in kind))
    check(numid, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


# ------------------------------------------------------------------ fixtures --

STATE = {}


def digest(**extra):
    a = {}
    a.update(extra)
    return send("digest", a)


def controllers(**args):
    e = send("temp-control", args)
    return e, [r for r in as_list(dig(e, "data.list")) if isinstance(r, dict)]


def indoor_rooms():
    """Enclosed, roofed, non-outdoor rooms — where a temperature target means
    anything at all.

    `GenTemperature.ControlTemperatureTempChange` returns 0f for a room that is
    null or `UsesOutdoorTemperature`, so a controller outdoors is not merely
    weak: it is a no-op, and phase 4's verdict would be undecidable there. The
    mod publishes `uses_outdoor_temp` on every `rooms` row precisely so a caller
    does not have to guess."""
    e = send("rooms", {"cap": 40, "min_cells": 4})
    out = []
    for row in as_list(dig(e, "data.list")):
        if not isinstance(row, dict):
            continue
        if row.get("uses_outdoor_temp") is True:
            continue
        if row.get("indoors") is not True:
            continue
        if row.get("doorway") is True:
            continue
        out.append(row)
    return out


def spawn_heater(at):
    """A heater on a named cell. `mode:"direct"` is the god-hand's own
    wipe-mode placement (Verse/GenSpawn with canWipeEdifices), which is what
    lets a fixture put a building on ground a PlaceWorker would argue about.
    It is a `dev:*` verb and is therefore allowed to bypass; this suite is
    staging a fixture, not exercising the build gate."""
    return send("dev:spawn-thing",
                {"def": HEATER_DEF, "count": 1, "pos": at, "mode": "direct"})


def spawn_food(at):
    return send("dev:spawn-thing",
                {"def": FOOD_DEF, "count": FOOD_COUNT, "pos": at, "mode": "near"})


def set_target(ids, c, **extra):
    a = {"things": ids, "target_c": c}
    a.update(extra)
    return send("temp-set", a)


def row_for(rows, tid):
    for r in rows:
        if r.get("id") == tid:
            return r
    return None


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 — the shapes every later phase digs into")

    e = send("status")
    precondition("0.a", "the bench is up and a colony is loaded",
                 dig(e, "ok") is True and dig(e, "data.gameLoaded") is True,
                 "start _RimWorld-Agent with a save loaded, paused.")

    # -- the digest's new section -----------------------------------------
    d = digest()
    eq("0.1", "`digest` still answers", d, "ok", True)
    shape("0.2", "digest", d, "data.temperature", dict)
    shape("0.3", "digest", d, "data.temperature.ok", bool)
    shape("0.4", "digest", d, "data.temperature.out_of_range", list)
    shape("0.5", "digest", d, "data.temperature.out_of_range_rooms", int)
    shape("0.6", "digest", d, "data.temperature.outdoor_c", (int, float))
    shape("0.7", "digest", d, "data.temperature.tolerance_c", (int, float))
    shape("0.8", "digest", d, "data.temperature.controllers", int)
    shape("0.9", "digest", d, "data.temperature.controllers_unpowered", int)
    shape("0.10", "digest", d, "data.temperature.controllers_switched_off", int)
    shape("0.11", "digest", d, "data.temperature.controllers_ineffective", int)
    shape("0.12", "digest", d, "data.temperature.food_rooms_uncontrolled", int)
    shape("0.13", "digest", d, "data.temperature.food_rooms_unfrozen", int)
    shape("0.14", "digest", d, "data.temperature.rooms", list)
    shape("0.15", "digest", d, "data.temperature.total", int)
    shape("0.16", "digest", d, "data.temperature.more", int)
    shape("0.17", "digest", d, "data.temperature.order", str)
    shape("0.18", "digest", d, "data.temperature.note", str)
    tol = num(dig(d, "data.temperature.tolerance_c"))
    if tol is not None:
        STATE["tolerance_c"] = tol
    check("0.19", "the published tolerance is the one this suite reasons with",
          ARGS.dry_run or tol == TOLERANCE_C,
          "tolerance_c == %s (TemperatureVerbs.ToleranceC)" % TOLERANCE_C, tol)

    # -- the rot block, and the note on the field it does NOT replace ------
    shape("0.20", "digest", d, "data.resources.food_days", (int, float))
    shape("0.21", "digest", d, "data.resources.food_days_basis", str)
    contains("0.22", "`food_days_basis` says it is stockpile-only",
             d, "data.resources.food_days_basis", "stockpile")
    contains("0.23", "…and that it has no rot term",
             d, "data.resources.food_days_basis", "rot")
    shape("0.24", "digest", d, "data.resources.food_rot", dict)
    for i, key in enumerate([
            "ok", "warn_days", "days", "days_frozen", "nutrition",
            "nutrition_in_stockpiles", "nutrition_forbidden", "frozen",
            "refrigerated", "unrefrigerated", "imperishable", "spoiled_stacks",
            "spoiled_nutrition", "soonest_rot_days", "soonest_rot_nutrition",
            "worst_rot_pct", "stacks", "rottable_stacks", "fogged_stacks",
            "corpse_stacks_excluded", "uncounted_stacks_excluded", "scope",
            "basis", "note"]):
        shape("0.%d" % (25 + i), "digest", d, "data.resources.food_rot." + key)
    eq("0.49", "the rot block says it is map-wide, not stockpile-scoped",
       d, "data.resources.food_rot.scope", "map-wide")
    eq("0.50", "…while the section around it still says stockpiles-only",
       d, "data.resources.scope", "stockpiles-only")
    check("0.51", "`food_days` is UNCHANGED in meaning — nutrition_in_stockpiles "
                  "is the same number the shipped field divides",
          ARGS.dry_run or (
              num(dig(d, "data.resources.food_nutrition")) is not None
              and num(dig(d, "data.resources.food_rot.nutrition_in_stockpiles")) is not None
              and abs(dig(d, "data.resources.food_nutrition")
                      - dig(d, "data.resources.food_rot.nutrition_in_stockpiles")) <= 1.0),
          "food_nutrition == food_rot.nutrition_in_stockpiles (+/- rounding)",
          {"food_nutrition": dig(d, "data.resources.food_nutrition"),
           "in_stockpiles": dig(d, "data.resources.food_rot.nutrition_in_stockpiles")})

    # -- the verbs exist --------------------------------------------------
    e, rows = controllers()
    eq("0.52", "`temp-control` answers with no arguments (whole map)", e, "ok", True)
    for i, key in enumerate([
            "verb", "gate", "target_scope", "list", "total", "more", "order",
            "tolerance_c", "clamp_c", "outdoor_c", "note",
            "rejected", "rejects", "rejects_more", "rejects_by_reason"]):
        shape("0.%d" % (53 + i), "temp-control", e, "data." + key)
    contains("0.68", "the read cites the WIDGET, not a field",
             e, "data.gate", "CompGetGizmosExtra")
    check("0.69", "the published clamp is the game's own",
          ARGS.dry_run or dig(e, "data.clamp_c") == [CLAMP_MIN_C, CLAMP_MAX_C],
          "[%s, %s]" % (CLAMP_MIN_C, CLAMP_MAX_C), dig(e, "data.clamp_c"))

    # `temp-set` with no target set is a REFUSAL, not a whole-map default.
    e = send("temp-set", {"target_c": 21})
    refused("0.70", "`temp-set` refuses with no target set", e, "bad-args", "target set")
    contains("0.71", "…and says why there is no whole-map default",
             e, "error.detail", "whole-map")

    STATE["rooms"] = indoor_rooms()
    note("0.72", "indoor rooms found: %s"
         % [r.get("id") for r in STATE["rooms"]][:8])


# ------------------------------------------------------------------- phase 1 --

def phase1():
    banner("PHASE 1 — `temp-control` READS, and reading mutates nothing")

    e, rows = controllers()
    if not rows and not ARGS.dry_run:
        # Stage one, so this phase is about the verb rather than about the map.
        room = pick_indoor_room()
        s = spawn_heater(room["at"])
        precondition("1.a", "a temperature-controlled building exists",
                     dig(s, "ok") is True,
                     "no CompTempControl building on the map and `dev:spawn-thing "
                     "{def:'Heater'}` failed: %s" % show(dig(s, "error")))
        e, rows = controllers()
    precondition("1.b", "at least one controller is readable",
                 ARGS.dry_run or len(rows) >= 1,
                 "`temp-control` listed nothing even after spawning a %s. Build a "
                 "heater or a cooler, or check that the def carries "
                 "CompProperties_TempControl." % HEATER_DEF)

    row = rows[0] if rows else {}
    if row:
        STATE["ctl"] = row.get("id")
    note("1.0", "subject: id=%s def=%s target=%s serves=%s"
         % (row.get("id"), row.get("def"), row.get("target_c"),
            dig(row, "serves.room_id")))

    # THE ISSUE'S FIRST ACCEPTANCE BULLET, field by field.
    env = {"data": {"row": row}}
    for i, (key, kind) in enumerate([
            ("id", int), ("def", str), ("label", str), ("at", (str, list)),
            ("kind", str), ("target_c", (int, float)),
            ("def_default_c", (int, float)), ("def_min_c", (int, float)),
            ("def_max_c", (int, float)), ("def_clamp_enforced", bool),
            ("outside_def_range", bool), ("powered", bool),
            ("switch_on", bool), ("broken_down", bool),
            ("operating_at_high_power", bool), ("effective", bool),
            ("serves_basis", str), ("serves", dict),
            ("energy_per_second", (int, float)), ("faction", str)]):
        shape("1.%d" % (1 + i), "temp-control row", env, "data.row." + key, kind)
    eq("1.21", "the def range is published as UNENFORCED",
       env, "data.row.def_clamp_enforced", False)
    check("1.22", "a heater/cooler row names the room it SERVES",
          ARGS.dry_run or (row.get("kind") == "other"
                           or dig(env, "data.row.serves.room_id") is not None),
          "serves.room_id present for a cooler/heater",
          dig(env, "data.row.serves"))
    check("1.23", "…and the basis cites the game's own TickRare",
          ARGS.dry_run or row.get("kind") == "other"
          or "TickRare" in (row.get("serves_basis") or ""),
          "serves_basis naming Building_Cooler.TickRare or Building_Heater.TickRare",
          row.get("serves_basis"))

    # READING MUTATES NOTHING. Not a claim — a measurement: two reads with no
    # intervening command, compared field for field on the same ids.
    e2, rows2 = controllers()
    same = True
    if not ARGS.dry_run:
        a = {r.get("id"): r for r in rows}
        b = {r.get("id"): r for r in rows2}
        same = set(a) == set(b)
        if same:
            for k in a:
                for f in ("target_c", "def_min_c", "def_max_c", "powered",
                          "switch_on", "kind", "effective"):
                    if a[k].get(f) != b[k].get(f):
                        same = False
                        break
                if not same:
                    break
    check("1.24", "two consecutive reads are identical — no lazy-init drift",
          ARGS.dry_run or same,
          "the same ids and the same target/power/effective on each",
          {"first": len(rows), "second": len(rows2)})

    # The `room:<id>` addressing, which is the form a freezer actually poses.
    rid = dig(row, "serves.room_id")
    if rid is not None and not ARGS.dry_run:
        e3, rows3 = controllers(room=rid)
        eq("1.25", "`temp-control {room:<id>}` answers", e3, "ok", True)
        eq("1.26", "…and scopes itself to that room", e3, "data.target_scope.kind", "room")
        check("1.27", "…and contains the controller that serves it",
              row_for(rows3, row.get("id")) is not None,
              "the id %s in the room-scoped list" % row.get("id"),
              [r.get("id") for r in rows3])
    else:
        note("1.25", "no serves.room_id on the subject — room addressing not exercised")

    # THE EXHAUST TRAP, and it is a regression guard for a bug found in audit
    # rather than a shape check. A cooler sits in a wall: it SERVES the room on
    # its south-rotated side and EXHAUSTS into the room on the north one
    # (`Building_Cooler.TickRare`). `temp-control {room:<kitchen>}` should list a
    # cooler that merely dumps heat there — the kitchen's temperature really is
    # affected by it — but `temp-set {room:<kitchen>}` must NOT retarget it,
    # because that silently rewrites the FREEZER's target and thaws it. Only
    # exercised when the map has a cooler whose two sides are different rooms.
    exhaust_id = None
    exhaust_room = None
    if not ARGS.dry_run:
        for r in rows:
            er = dig(r, "exhaust.room_id")
            sr = dig(r, "serves.room_id")
            if er is not None and sr is not None and er != sr:
                exhaust_id, exhaust_room = r.get("id"), er
                break
    if exhaust_id is None:
        note("1.28", "no cooler with a distinct exhaust room on this map — the "
                     "exhaust-only refusal is NOT exercised. Build a cooler in a "
                     "wall between two sealed rooms to cover it.")
    else:
        e4, rows4 = controllers(room=exhaust_room)
        check("1.28", "`temp-control {room:<exhaust room>}` DOES list the cooler "
                      "that dumps heat there",
              row_for(rows4, exhaust_id) is not None,
              "id %s in the read of room %s" % (exhaust_id, exhaust_room),
              [r.get("id") for r in rows4])
        e5 = send("temp-set", {"room": exhaust_room, "target_c": 20})
        check("1.29", "…while `temp-set` REFUSES it as exhaust-only — setting a "
                      "cooler from the room it heats would retarget the room it cools",
              dig(e5, "data.rejects_by_reason.exhaust-only") is not None,
              "rejects_by_reason.exhaust-only present",
              dig(e5, "data.rejects_by_reason"))
        contains("1.30", "…naming the room it actually serves",
                 e5, "data.rejects.0.reason", "SERVES room")


def pick_indoor_room():
    rooms = STATE.get("rooms") or indoor_rooms()
    STATE["rooms"] = rooms
    precondition("x.a", "one enclosed, roofed, non-outdoor room of >= 4 cells",
                 ARGS.dry_run or len(rooms) >= 1,
                 "GenTemperature.ControlTemperatureTempChange returns 0 for a room "
                 "that is null or UsesOutdoorTemperature, so a controller outdoors "
                 "is a no-op and every temperature verdict here is undecidable. "
                 "Build or load a colony with one sealed, roofed room.")
    return rooms[0] if rooms else {"id": None, "at": "0,0"}


# ------------------------------------------------------------------- phase 2 --

def phase2():
    banner("PHASE 2 — `temp-set` sets a target, and it READS BACK")

    tid = STATE.get("ctl")
    if tid is None and not ARGS.dry_run:
        _e, rows = controllers()
        precondition("2.a", "a controller to set", len(rows) >= 1,
                     "run phase 1 first, or put a heater/cooler on the map")
        tid = rows[0]["id"]
        STATE["ctl"] = tid

    before = None
    if not ARGS.dry_run:
        _e, rows = controllers(things=[tid])
        r = row_for(rows, tid)
        before = r.get("target_c") if r else None
    STATE["restore_c"] = before if before is not None else 21
    # Never the value already there: checks 2.10 and 2.15 assert that the call
    # CHANGED something, and a re-run of this suite after a crash could leave
    # the subject sitting on the probe value. Chosen off the current target
    # rather than hardcoded.
    probe = -7 if before != -7 else -8
    STATE["probe_c"] = probe
    note("2.0", "target before: %s C; probing with %s C" % (before, probe))

    # -- dry_run first: it must change nothing -----------------------------
    e = set_target([tid], probe, dry_run=True)
    eq("2.1", "a dry run answers", e, "ok", True)
    eq("2.2", "…is marked as one", e, "data.dry_run", True)
    eq("2.3", "…and journals nothing", e, "data.action.journal_seq", None)
    if not ARGS.dry_run:
        _e, rows = controllers(things=[tid])
        r = row_for(rows, tid) or {}
        check("2.4", "…and the comp has NOT moved", r.get("target_c") == before,
              "target still %s" % before, r.get("target_c"))

    # -- the real set ------------------------------------------------------
    e = set_target([tid], probe)
    eq("2.5", "`temp-set` answers", e, "ok", True)
    eq("2.6", "…with its own verdict true", e, "data.ok", True)
    eq("2.7", "…in Celsius, said out loud", e, "data.units", "celsius")
    eq("2.8", "…echoing the target", e, "data.target_c", probe)
    eq("2.9", "…accepting exactly one", e, "data.accepted", 1)
    eq("2.10", "…and changing it", e, "data.changed", 1)
    shape("2.11", "temp-set", e, "data.things.0.target_before_c", (int, float))
    shape("2.12", "temp-set", e, "data.things.0.target_after_c", (int, float))
    eq("2.13", "the row's AFTER is the value asked for", e, "data.things.0.target_after_c", probe)
    eq("2.14", "…and its BEFORE is what was there", e, "data.things.0.target_before_c", before)
    eq("2.15", "…marked changed", e, "data.things.0.changed", True)
    shape("2.16", "temp-set", e, "data.action.journal_seq", (int, float))
    ge("2.17", "…and the journal line was actually written", e, "data.action.journal_seq", 1)
    contains("2.18", "the verb cites the widget", e, "data.gate", "CompGetGizmosExtra")
    contains("2.19", "…and says the target is stored, not applied",
             e, "data.note", "stored")

    # READ BACK THROUGH A DIFFERENT VERB. `temp-set`'s own row could in
    # principle echo what it was told; `temp-control` re-reads the comp.
    if not ARGS.dry_run:
        _e, rows = controllers(things=[tid])
        r = row_for(rows, tid) or {}
        check("2.20", "`temp-control` reads the new target back off the comp",
              r.get("target_c") == probe, str(probe), r.get("target_c"))

    # -- idempotence: setting the same value twice is not a change ---------
    e = set_target([tid], probe)
    eq("2.21", "setting the same target again changes nothing", e, "data.changed", 0)
    eq("2.22", "…and says it was already there", e, "data.already_at_target", 1)
    eq("2.23", "…on the row too", e, "data.things.0.was_already", True)

    # -- the journal carries before/after, which is the issue's last bullet -
    if not ARGS.dry_run:
        j = send("journal", {"since_seq": 0, "types": ["action"], "limit": 200})
        rows = [r for r in as_list(dig(j, "data.events"))
                if isinstance(r, dict) and dig(r, "payload.verb") == "temp-set"]
        check("2.24", "the journal has a `temp-set` action row",
              len(rows) >= 1, "at least one action row with verb temp-set",
              len(rows))
        if rows:
            last = rows[-1]
            tgts = as_list(dig(last, "payload.targets"))
            check("2.25", "…carrying the per-building before/after target",
                  any(isinstance(t, dict) and "before_c" in t and "after_c" in t
                      for t in tgts),
                  "payload.targets[*] with before_c and after_c", tgts[:3])

    # Put it back, so phase 4 starts from a known place.
    set_target([tid], STATE["restore_c"])


# ------------------------------------------------------------------- phase 3 --

def phase3():
    banner("PHASE 3 — the refusals, and the one that is deliberately NOT a refusal")

    tid = STATE.get("ctl")
    if tid is None and not ARGS.dry_run:
        _e, rows = controllers()
        precondition("3.a", "a controller to set", len(rows) >= 1, "run phase 1 first")
        tid = rows[0]["id"]
        STATE["ctl"] = tid

    # -- (1) a building with NO CompTempControl, refused BY NAME -----------
    victim, victim_def, why = find_non_controller()
    precondition("3.b", "something on the map with no CompTempControl",
                 ARGS.dry_run or victim is not None,
                 "needed one building (ideally a Vent, else any wall) to prove the "
                 "named refusal. `things` found nothing: %s" % why)
    note("3.0", "non-controller subject: id=%s def=%s" % (victim, victim_def))

    e = set_target([victim, tid] if tid else [victim], 10)
    eq("3.1", "the call still SUCCEEDS at the envelope", e, "ok", True)
    eq("3.2", "…while the verb's own verdict is false", e, "data.ok", False)
    eq("3.3", "…with exactly one rejection", e, "data.rejected", 1)
    check("3.4", "the refusal is named `no-temp-control`",
          ARGS.dry_run or dig(e, "data.rejects_by_reason.no-temp-control") == 1,
          "rejects_by_reason.no-temp-control == 1", dig(e, "data.rejects_by_reason"))
    check("3.5", "…and it names the BUILDING, not just the cell",
          ARGS.dry_run or dig(e, "data.rejects.0.id") == victim
          or dig(e, "data.rejects.0.thing.id") == victim,
          "the rejected id %s on the rejection row" % victim,
          dig(e, "data.rejects.0"))
    contains("3.6", "…with the game's own reason",
             e, "data.rejects.0.reason", "CompTempControl")
    if victim_def == "Vent":
        contains("3.7", "a Vent's refusal points at `flick`",
                 e, "data.rejects.0.reason", "flick")
        contains("3.8", "…and cites Building_Vent.TickRare",
                 e, "data.rejects.0.reason", "Building_Vent.TickRare")
    else:
        note("3.7", "no Vent on the map — the vent-specific reason was not "
                    "exercised (build one to cover it; def `Vent`)")
    if tid:
        check("3.9", "…while the real controller in the SAME call still got set",
              ARGS.dry_run or dig(e, "data.accepted") == 1,
              "accepted == 1", dig(e, "data.accepted"))

    # -- (2) outside the GAME's clamp: bad-args ---------------------------
    e = set_target([tid] if tid else [victim], CLAMP_MIN_C - 1)
    refused("3.10", "a target below absolute zero is refused", e, "bad-args", "clamp")
    contains("3.11", "…citing the member the clamp lives in",
             e, "error.detail", "InterfaceChangeTargetTemperature")
    e = set_target([tid] if tid else [victim], CLAMP_MAX_C + 1)
    refused("3.12", "…and one above 1000 C too", e, "bad-args", "clamp")

    # -- (3) outside the DEF's advertised range: NOT a refusal -------------
    # THE ISSUE ASKED FOR A REFUSAL HERE AND THE ISSUE IS WRONG. Nothing in the
    # 1.6 tree reads CompProperties_TempControl.minTargetTemperature; a player
    # walks a cooler past -50 with the -10 button and the game does not stop
    # them. Refusing would refuse what a player can do. What ships is the
    # advisory, and this is the check that pins that decision down.
    if tid:
        e = set_target([tid], DEF_MIN_C - 10)
        eq("3.13", "a target below the def's advertised min is ACCEPTED", e, "ok", True)
        eq("3.14", "…and actually set", e, "data.things.0.target_after_c", DEF_MIN_C - 10)
        eq("3.15", "…flagged as outside the def range", e, "data.things.0.outside_def_range", True)
        shape("3.16", "temp-set", e, "data.advisories", list)
        contains("3.17", "…with an advisory that says it is not a refusal and why",
                 e, "data.advisories.0.advisory", "minTargetTemperature")
        set_target([tid], STATE.get("restore_c", 21))

    # -- (4) a wrong argument NAME, since the house refuses those ----------
    e = send("temp-set", {"things": [tid or victim], "target": 10})
    refused("3.18", "`target` is refused as a near-miss for `target_c`",
            e, "bad-args", "target_c")


def find_non_controller():
    """Something on the map with no CompTempControl. A `Vent` first, because it
    is the interesting case: it lives in the Temperature build category, it is
    called a temperature building, and Core's
    `Defs/ThingDefs_Buildings/Buildings_Temperature.xml` gives it only
    `CompProperties_Flickable` — so `Building_Vent.compTempControl` is null and
    it has no target at all. Any wall will do otherwise.

    `things` addresses by `def`, not `filter`, and its per-thing rows live under
    `data.things` (the DETAIL list) rather than `data.list` — `data.list` on
    this verb belongs to the fire scan. Both were checked against
    `ThingVerbs.Rollups` rather than guessed; a wrong key here would make this
    phase skip on a bench that has a Vent."""
    if ARGS.dry_run:
        return 1, "Vent", None
    err = None
    for want in ("Vent", "Wall", "Door"):
        e = send("things", {"def": want, "detail": True, "cap": 5, "detail_cap": 5})
        if dig(e, "ok") is not True:
            err = show(dig(e, "error"))
            continue
        for row in as_list(dig(e, "data.things")):
            if isinstance(row, dict) and row.get("id") is not None:
                return row["id"], want, None
    # Anything haulable, as the last resort — a steel stack has no
    # CompTempControl either, and the named refusal is the thing under test.
    e = send("things", {"category": "haulable", "detail": True, "detail_cap": 5})
    for row in as_list(dig(e, "data.things")):
        if isinstance(row, dict) and row.get("id") is not None:
            return row["id"], row.get("def"), None
    return None, None, err or show(dig(e, "error"))


# ------------------------------------------------------------------- phase 4 --

def phase4():
    banner("PHASE 4 — `digest.temperature` shows an out-of-range room")

    tid = STATE.get("ctl")
    if tid is None and not ARGS.dry_run:
        _e, rows = controllers()
        precondition("4.a", "a controller", len(rows) >= 1, "run phase 1 first")
        tid = rows[0]["id"]
        STATE["ctl"] = tid

    room_id = None
    kind = None
    if not ARGS.dry_run:
        _e, rows = controllers(things=[tid])
        r = row_for(rows, tid) or {}
        room_id = dig(r, "serves.room_id")
        kind = r.get("kind")
        precondition("4.b", "the controller serves a real indoor room",
                     room_id is not None and dig(r, "serves.uses_outdoor_temp") is not True,
                     "the subject controller serves %s (uses_outdoor_temp=%s). "
                     "GenTemperature.ControlTemperatureTempChange returns 0 for an "
                     "outdoor room, so `out_of_range` is undecidable there — put "
                     "the heater in a sealed, roofed room."
                     % (room_id, dig(r, "serves.uses_outdoor_temp")))
        precondition("4.c", "the controller is a heater or a cooler",
                     kind in ("heater", "cooler"),
                     "kind=%r has no direction, so there is no wrong side of the "
                     "target to be on" % kind)

    # DETERMINISTIC AND INSTANT: no power, no advance. `out_of_range` is a
    # comparison of the room's temperature against the target, so driving the
    # target far past the room forces it in one call. Direction depends on which
    # way the controller pushes — a heater is out of range when the room is
    # COLDER than its target, a cooler when the room is WARMER.
    room_temp = None
    if not ARGS.dry_run:
        d = digest()
        for row in as_list(dig(d, "data.temperature.rooms")):
            if isinstance(row, dict) and row.get("room_id") == room_id:
                room_temp = num(row.get("temp_c"))
                break
        if room_temp is None:
            e = send("room", {"id": room_id})
            room_temp = num(dig(e, "data.temp_c"))
    base = room_temp if room_temp is not None else 20.0
    # INTEGRAL, and clamped inside the game's own limits. Integral because the
    # mod rounds every published temperature to one decimal and this check
    # compares `target_c` for EQUALITY — a fractional room temperature plus 200
    # would round to a value the suite did not send, and the check would fail
    # for a reason that has nothing to do with the spec. Clamped because an
    # arctic room at -80 C minus 200 is below absolute zero, which `temp-set`
    # correctly refuses as bad-args and which would present here as a spec
    # failure rather than as the suite asking for something impossible.
    far = int(round(base)) + 200 if kind == "heater" else int(round(base)) - 200
    far = max(int(CLAMP_MIN_C) + 2, min(int(CLAMP_MAX_C) - 2, far))
    note("4.0", "room %s is at %s C; driving a %s to %s C"
         % (room_id, room_temp, kind, far))

    e = set_target([tid], far)
    eq("4.1", "the extreme target is accepted", e, "ok", True)

    d = digest()
    eq("4.2", "`digest.temperature.ok` goes FALSE", d, "data.temperature.ok", False)
    ge("4.3", "…with at least one out-of-range room", d, "data.temperature.out_of_range_rooms", 1)
    check("4.4", "…and the headline NAMES the room id",
          ARGS.dry_run or room_id in as_list(dig(d, "data.temperature.out_of_range")),
          "%s in temperature.out_of_range" % room_id,
          dig(d, "data.temperature.out_of_range"))

    row = None
    if not ARGS.dry_run:
        for r in as_list(dig(d, "data.temperature.rooms")):
            if isinstance(r, dict) and r.get("room_id") == room_id:
                row = r
                break
    env = {"data": {"row": row or {}}}
    check("4.5", "the room has a row at all", ARGS.dry_run or row is not None,
          "a rooms[] entry for room %s" % room_id,
          [r.get("room_id") for r in as_list(dig(d, "data.temperature.rooms"))])
    for i, (key, kind_) in enumerate([
            ("room_id", int), ("at", (str, list)), ("cells", int),
            ("temp_c", (int, float)), ("uses_outdoor_temp", bool),
            ("controllers", list), ("controllers_total", int),
            ("out_of_range", bool), ("target_c", (int, float)),
            ("drift_c", (int, float))]):
        shape("4.%d" % (6 + i), "digest.temperature room", env, "data.row." + key, kind_)
    eq("4.16", "the row is marked out of range", env, "data.row.out_of_range", True)
    eq("4.17", "…carrying the target we set", env, "data.row.target_c", far)
    if not ARGS.dry_run and row:
        close("4.18", "…and a drift equal to temp - target",
              num(row.get("drift_c")), (num(row.get("temp_c")) or 0) - far, tol=0.2)
        crow = None
        for c in as_list(row.get("controllers")):
            if isinstance(c, dict) and c.get("id") == tid:
                crow = c
                break
        check("4.19", "…and the controller line inside it names the same id",
              crow is not None, "a controllers[] entry for %s" % tid,
              [c.get("id") for c in as_list(row.get("controllers"))])
        if crow:
            eq("4.20", "…marked out of range too", {"c": crow}, "c.out_of_range", True)
            shape("4.21", "controller line", {"c": crow}, "c.operating_at_high_power", bool)
            shape("4.22", "controller line", {"c": crow}, "c.powered", bool)
            shape("4.23", "controller line", {"c": crow}, "c.effective", bool)

    # -- the predicate half: it is a REGISTERED predicate section ----------
    # Armed and immediately disarmed. `edge:false` so it reads the CURRENT
    # state; `timeout_ticks:1` so nothing runs. If `temperature` were not
    # registered, this is a bad-args refusal naming the section list — which is
    # exactly how a section that was added to the digest but not to
    # PredicateSections would present.
    e = advance({"until": {"condition": {"path": "temperature.ok", "op": "==",
                                         "value": False, "edge": False}},
                 "timeout_ticks": 1}, timeout=180)
    check("4.24", "`temperature` is addressable as a predicate section",
          ARGS.dry_run or dig(e, "error.code") != "bad-args",
          "not a bad-args refusal about unknown sections",
          {"ok": dig(e, "ok"), "error": dig(e, "error")})
    eq("4.25", "…and the predicate is TRUE at arm time, as the digest says",
       e, "data.until.true_when_armed", True)

    # -- and back: the section recovers ------------------------------------
    set_target([tid], STATE.get("restore_c", 21))
    d = digest()
    if not ARGS.dry_run:
        still = room_id in as_list(dig(d, "data.temperature.out_of_range"))
        check("4.26", "putting the target back clears the room from out_of_range",
              not still, "%s absent from out_of_range" % room_id,
              dig(d, "data.temperature.out_of_range"))


# ------------------------------------------------------------------- phase 5 --

def phase5():
    banner("PHASE 5 — THE ROT HALF: food that is visibly deteriorating")

    d = digest()
    stacks = num(dig(d, "data.resources.food_rot.rottable_stacks")) or 0
    if stacks < 1 and not ARGS.dry_run:
        room = pick_indoor_room()
        s = spawn_food(room.get("at") or "0,0")
        precondition("5.a", "some rottable human-edible food on the map",
                     dig(s, "ok") is True,
                     "no rottable food and `dev:spawn-thing {def:'%s'}` failed: %s"
                     % (FOOD_DEF, show(dig(s, "error"))))
        d = digest()
        stacks = num(dig(d, "data.resources.food_rot.rottable_stacks")) or 0
    precondition("5.b", "at least one rottable food stack",
                 ARGS.dry_run or stacks >= 1,
                 "food_rot.rottable_stacks is %s. Spawn %s x %s, or put meals on "
                 "the map." % (stacks, FOOD_COUNT, FOOD_DEF))

    unref = num(dig(d, "data.resources.food_rot.unrefrigerated")) or 0
    frozen = num(dig(d, "data.resources.food_rot.frozen")) or 0
    soonest = num(dig(d, "data.resources.food_rot.soonest_rot_days"))
    days = num(dig(d, "data.resources.food_rot.days"))
    food_days = num(dig(d, "data.resources.food_days"))
    note("5.0", "unrefrigerated=%s frozen=%s soonest_rot_days=%s "
                "food_rot.days=%s food_days=%s"
         % (unref, frozen, soonest, days, food_days))

    precondition("5.c", "the food is NOT frozen — a warm room is the fixture",
                 ARGS.dry_run or unref > 0,
                 "every food stack on this map is frozen or refrigerated "
                 "(unrefrigerated=%s), so nothing has a clock and there is no "
                 "deterioration to watch. Put food in a room above 10 C."
                 % unref)

    # THE HEADLINE FACT THE OLD SURFACE COULD NOT STATE: there is a deadline,
    # and it has a number.
    check("5.1", "the digest publishes a rot deadline at all",
          ARGS.dry_run or soonest is not None,
          "soonest_rot_days is a number, not null (null means nothing is rotting)",
          soonest)
    ge("5.2", "…and unrefrigerated nutrition is non-zero",
       d, "data.resources.food_rot.unrefrigerated", 0.01)
    check("5.3", "the MAP-WIDE figure is at least the stockpile-only one",
          ARGS.dry_run or (
              num(dig(d, "data.resources.food_rot.nutrition")) is not None
              and num(dig(d, "data.resources.food_rot.nutrition_in_stockpiles")) is not None
              and dig(d, "data.resources.food_rot.nutrition")
              >= dig(d, "data.resources.food_rot.nutrition_in_stockpiles") - 1.0),
          "food_rot.nutrition >= nutrition_in_stockpiles — the stockpile count is "
          "a LOWER bound and food on unzoned ground is exactly the gap",
          {"map": dig(d, "data.resources.food_rot.nutrition"),
           "stockpiles": dig(d, "data.resources.food_rot.nutrition_in_stockpiles")})

    # ADVANCE, BOUNDED BY THE FIELD UNDER TEST. This is the whole justification
    # for not using a tick count here: "wait until the food has measurably
    # deteriorated" IS expressible as a predicate over the new block, and arming
    # it is simultaneously the proof that `resources.food_rot.*` is reachable
    # from `until:{condition:…}`.
    target = round((soonest if soonest is not None else 1.0) - 0.05, 2)
    note("5.4", "advancing until soonest_rot_days <= %s (from %s), ceiling %s ticks"
         % (target, soonest, UNTIL_TIMEOUT_TICKS))
    a = advance({"until": {"condition": {"path": "resources.food_rot.soonest_rot_days",
                                         "op": "<=", "value": target}}})
    check("5.5", "`resources.food_rot.soonest_rot_days` is a usable predicate path",
          ARGS.dry_run or dig(a, "error.code") != "bad-args",
          "not a bad-args refusal about an unresolvable path",
          {"ok": dig(a, "ok"), "error": dig(a, "error")})
    eq("5.6", "the advance ran", a, "ok", True)
    check("5.7", "…and stopped on the CONDITION, not on its timeout",
          ARGS.dry_run or dig(a, "data.reason") == "condition",
          "reason == 'condition' (a timeout here means the clock did not move, "
          "not that the field is wrong)", dig(a, "data.reason"))

    d2 = digest()
    soonest2 = num(dig(d2, "data.resources.food_rot.soonest_rot_days"))
    worst2 = num(dig(d2, "data.resources.food_rot.worst_rot_pct"))
    worst1 = num(dig(d, "data.resources.food_rot.worst_rot_pct"))
    food_days2 = num(dig(d2, "data.resources.food_days"))

    check("5.8", "THE DEADLINE MOVED: soonest_rot_days fell",
          ARGS.dry_run or (soonest2 is not None and soonest is not None
                           and soonest2 < soonest),
          "soonest_rot_days strictly below %s" % soonest, soonest2)
    check("5.9", "…and rot progress rose",
          ARGS.dry_run or (worst2 is not None and worst1 is not None
                           and worst2 > worst1),
          "worst_rot_pct above %s" % worst1, worst2)
    # THE POINT, STATED AS A CHECK. Before this issue, the ONLY food number in
    # the digest was `food_days`, and it does not move while food deteriorates —
    # it holds its value and then falls off a cliff when ShouldCount's
    # IsNotFresh test starts dropping stacks. Deterioration was invisible.
    check("5.10", "…while `food_days` did NOT fall — which is the blind spot",
          ARGS.dry_run or (food_days is None or food_days2 is None
                           or food_days2 >= food_days - 0.05),
          "food_days unchanged (or higher) across the same window: it has no rot "
          "term, which is exactly why food_rot had to be added",
          {"before": food_days, "after": food_days2})
    note("5.11", "food_days %s -> %s   |   soonest_rot_days %s -> %s"
         % (food_days, food_days2, soonest, soonest2))

    # The per-room half: the warm room is named, with its own numbers.
    if not ARGS.dry_run:
        rooms = [r for r in as_list(dig(d2, "data.temperature.rooms"))
                 if isinstance(r, dict) and isinstance(r.get("food"), dict)]
        check("5.12", "`digest.temperature` names a room that HOLDS the food",
              len(rooms) >= 1, "at least one temperature.rooms[] row with a `food` block",
              [r.get("room_id") for r in as_list(dig(d2, "data.temperature.rooms"))])
        if rooms:
            fr = {"r": rooms[0]}
            for i, key in enumerate(["stacks", "nutrition", "frozen", "refrigerated",
                                     "unrefrigerated", "rot_rate", "worst_rot_pct",
                                     "soonest_rot_days"]):
                shape("5.%d" % (13 + i), "temperature room food", fr, "r.food." + key)
            ge("5.21", "…and the room reports a non-zero rot rate",
               fr, "r.food.rot_rate", 0.001)


# ------------------------------------------------------------------- phase 6 --

def phase6():
    banner("PHASE 6 — OPT-IN: a real cooler driving a real room COLD")

    note("6.0", "This is the session-18 criterion and the only phase that needs "
                "POWER and TIME. It is opt-in because it can take thousands of "
                "ticks and depends on a fixture this suite cannot build: see the "
                ".md's 'the fixture the orchestrator must build'.")

    _e, rows = controllers()
    coolers = [r for r in rows if r.get("kind") == "cooler"
               and r.get("powered") is True
               and r.get("effective") is True]
    precondition("6.a", "a POWERED, EFFECTIVE cooler serving an indoor room",
                 ARGS.dry_run or len(coolers) >= 1,
                 "found %d cooler(s), of which %d powered and effective. A cooler "
                 "needs: a wall to sit in, BOTH its north and south cells passable "
                 "(Building_Cooler.TickRare's own guard), a sealed roofed room on "
                 "the south side, and a live power net. `temp-control` publishes "
                 "`powered`, `effective`, `cold_side_blocked` and `hot_side_blocked` "
                 "so you can see which one is missing."
                 % (len([r for r in rows if r.get("kind") == "cooler"]), len(coolers)))

    c = coolers[0] if coolers else {}
    room_id = dig(c, "serves.room_id")
    start = num(dig(c, "serves.temp_c"))
    note("6.1", "cooler id=%s serving room %s at %s C" % (c.get("id"), room_id, start))

    e = set_target([c.get("id")], -10)
    eq("6.2", "the cooler takes a freezing target", e, "ok", True)
    eq("6.3", "…and reads it back", e, "data.things.0.target_after_c", -10)

    # Bounded by the room's OWN temperature — a predicate over the field under
    # test, not a tick count, and not the rot clock (which would confound the
    # cooling measurement with what happens to be in the room).
    goal = round((start if start is not None else 20.0) - 3.0, 1)
    a = advance({"until": {"condition": {
                    "path": "temperature.rooms[*].temp_c", "op": "<=",
                    "value": goal, "quantify": "any"}},
                 "timeout_ticks": 60000}, timeout=1800)
    eq("6.4", "the advance ran", a, "ok", True)
    check("6.5", "…and a room actually got colder",
          ARGS.dry_run or dig(a, "data.reason") == "condition",
          "reason == 'condition'; a timeout means the cooler is not moving heat — "
          "read `temp-control`'s advisory on it", dig(a, "data.reason"))

    d = digest()
    now = None
    if not ARGS.dry_run:
        for r in as_list(dig(d, "data.temperature.rooms")):
            if isinstance(r, dict) and r.get("room_id") == room_id:
                now = num(r.get("temp_c"))
                break
    check("6.6", "THE SESSION-18 CRITERION: the room is measurably colder",
          ARGS.dry_run or (now is not None and start is not None and now < start - 1.0),
          "room %s below %s C (was %s)" % (room_id, (start or 0) - 1.0, start), now)
    ge("6.7", "…and the draw came off its idle floor", d, "data.power.draw_w", 1)
    note("6.8", "room %s: %s C -> %s C, draw_w=%s"
         % (room_id, start, now, dig(d, "data.power.draw_w")))


# ------------------------------------------------------------------ selftest --

def selftest():
    """OFFLINE. No bench, no game, no protocol. It proves the helpers this suite
    reasons with, because a suite whose `has_key` is broken reports green on a
    dig path that does not exist — which is the exact failure the shape-contract
    rule was written for."""
    banner("SELFTEST — the runner's own helpers, offline")

    o = {"a": {"b": [{"c": 1}, {"c": None}]}, "n": None, "z": 0, "f": False}
    check("s.1", "dig walks dicts", dig(o, "a.b.0.c") == 1, "1", dig(o, "a.b.0.c"))
    check("s.2", "dig walks list indices", dig(o, "a.b.1.c") is None, "None", dig(o, "a.b.1.c"))
    check("s.3", "dig returns the default for a missing key",
          dig(o, "a.q", "dflt") == "dflt", "dflt", dig(o, "a.q", "dflt"))
    check("s.4", "dig CANNOT tell absent from null — the reason has_key exists",
          dig(o, "n") is None and dig(o, "nope") is None, "both None",
          (dig(o, "n"), dig(o, "nope")))
    check("s.5", "has_key CAN", has_key(o, "n") and not has_key(o, "nope"),
          "True, False", (has_key(o, "n"), has_key(o, "nope")))
    check("s.6", "has_key on a nested path",
          has_key(o, "a.b.1.c") and not has_key(o, "a.b.1.d"),
          "True, False", (has_key(o, "a.b.1.c"), has_key(o, "a.b.1.d")))
    check("s.7", "num() rejects bools, which are ints in python",
          num(o["f"]) is None and num(o["z"]) == 0, "None, 0", (num(o["f"]), num(o["z"])))
    check("s.8", "as_list wraps a scalar and passes a list",
          as_list(3) == [3] and as_list([3]) == [3] and as_list(None) == [],
          "[3], [3], []", (as_list(3), as_list([3]), as_list(None)))

    # The rot arithmetic this suite asserts on, checked against the game's own
    # RotRateAtTemperature so a wrong expectation here fails offline rather than
    # on a bench.
    def rot_rate(t):
        if t < 0:
            return 0.0
        if t >= 10:
            return 1.0
        return t / 10.0
    check("s.9", "RotRateAtTemperature: below zero is frozen",
          rot_rate(-1) == 0.0, "0.0", rot_rate(-1))
    check("s.10", "…at and above 10 C it is full speed",
          rot_rate(10) == 1.0 and rot_rate(21) == 1.0, "1.0", rot_rate(21))
    check("s.11", "…and linear between",
          abs(rot_rate(5) - 0.5) < 1e-9, "0.5", rot_rate(5))
    check("s.12", "the three bands the digest publishes use 0.001/0.999",
          rot_rate(-1) < 0.001 and 0.001 <= rot_rate(5) < 0.999 and rot_rate(21) >= 0.999,
          "frozen / refrigerated / unrefrigerated", None)
    check("s.13", "the clamp this suite pokes at is the game's own",
          CLAMP_MIN_C == -273.15 and CLAMP_MAX_C == 1000.0,
          "-273.15 .. 1000 (CompTempControl.InterfaceChangeTargetTemperature)",
          (CLAMP_MIN_C, CLAMP_MAX_C))
    check("s.14", "…and the def range it proves is NOT enforced is the real one",
          DEF_MIN_C == -50.0,
          "-50 (CompProperties_TempControl.minTargetTemperature)", DEF_MIN_C)


# ---------------------------------------------------------------------- main --

PHASES = {0: phase0, 1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5, 6: phase6}
DEFAULT_PHASES = [0, 1, 2, 3, 4, 5]


def main():
    global ARGS
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", default=DEFAULT_ROOT, help="protocol root")
    ap.add_argument("--phase", type=int, action="append",
                    help="run only these phases (0 always runs first). Phase 6 is "
                         "opt-in and never in the default sweep.")
    ap.add_argument("--dry-run", action="store_true",
                    help="print the plan and every expectation, send nothing")
    ap.add_argument("--selftest", action="store_true",
                    help="offline: the runner's own helpers, no bench needed")
    ap.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = ap.parse_args()

    # --selftest needs NO bench and must not be gated on one.
    if ARGS.selftest:
        selftest()
        banner("RESULT")
        if FAILS:
            print("%s%d/%d selftest checks FAILED: %s%s"
                  % (RED, len(FAILS), CHECKS, ", ".join(FAILS), OFF))
            return 1
        print("%sselftest passed, all %d checks%s" % (GREEN, CHECKS, OFF))
        return 0

    wanted = sorted(set(ARGS.phase or DEFAULT_PHASES))
    if 0 not in wanted:
        wanted = [0] + wanted

    print("261f2e9 temperature acceptance — root %s" % ARGS.root)
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
