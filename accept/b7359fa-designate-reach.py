#!/usr/bin/env python3
"""Acceptance runner for b7359fa — `designate` no longer answers `accepted: N`
about targets no colonist is allowed to walk to.

    ./accept/b7359fa-designate-reach.py                 # everything safe
    ./accept/b7359fa-designate-reach.py --phase 3       # one phase (0 always runs)
    ./accept/b7359fa-designate-reach.py --dry-run       # print the plan, send nothing
    ./accept/b7359fa-designate-reach.py --selftest      # offline; no bench, nothing sent

Same protocol, helpers and exit codes as `accept/8b0b88f-already-designated.py`
and `accept/eef837a-bill-filter.py`; read either header first. There is no
`.ps1` twin: this box has no pwsh and the bench lives here.

Start the bench (`_RimWorld-Agent/run-agent.sh`), load a colony, leave it
**PAUSED** — phase 0 makes that a precondition. An unpaused colonist walks over
and finishes the mining between the two calls this suite compares, which deletes
the designation and makes the second call ACCEPT for a fixture reason that looks
exactly like the defect.

THE BENCH ACCEPTANCE THE ISSUE ASKS FOR, in one line: *`designate hunt` a target
outside every allowed area, and the refusal or the report says so rather than
`accepted: N` and silence.* That is phase 3, and phase 2 is the same claim on
`mine`, which is available on every map and needs no wildlife.

WHAT THIS IS TESTING, in one paragraph. `RimWorld/ForbidUtility.InAllowedArea`
reads `forPawn.playerSettings.EffectiveAreaRestrictionInPawnCurrentMap`, so the
allowed area is PER PAWN and there is no colony-wide area to compare a
designation against. `designate` now runs that member per (target, capable
colonist), splits `accepted` into `accepted_actionable` +
`accepted_unreachable`, publishes a `reach` block naming the roster and the
areas, and REFUSES a batch where not one target is workable by any capable
colonist — from a dry preflight, so nothing is written before the refusal.
`allow_unreachable:true` is the override.

THE THREE WAYS TO GET THIS WRONG, each of which has a check:
  * testing ONE area instead of every capable pawn's (phase 2 stages a
    restriction on the whole roster, then a second area on ONE pawn that DOES
    cover the target: `unreachable` must fall to 0);
  * treating "has an area" as "is restricted" — `InAllowedArea` ignores an area
    whose `TrueCount` is 0 (phase 4 binds the roster to an EMPTY area and
    asserts `restricted:false` and nothing unreachable);
  * refusing after designating (phase 3 asserts `designations_now ==
    designations_before` across a refusal, read from the envelope's own
    independent count).

THIS SUITE MUTATES, deliberately and reversibly. It creates one or two
`Area_Allowed`s, binds the colonist roster to them, adds Mine designations to a
handful of rock cells, and puts all of it back in a teardown that runs even when
a check fails or a precondition aborts. The teardown restores each colonist's
ORIGINAL area (recorded in phase 0 from `posture`'s read-only form) and cancels
the fixture cells. **It cannot restore an area it could not read**, so phase 0
aborts rather than proceed if `posture` will not answer.

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
DECOMP_CANDIDATES = [
    os.environ.get("RIMWORLD_DECOMP"),
    "/home/dorian/projects/rimworld-tools/Info/decompiled/RimWorldBase",
    os.path.join(VAULT, "..", "misc/rimworld/reference/decompiled/RimWorldBase"),
]

GREEN, RED, YELLOW, CYAN, DIM, OFF = (
    "\033[32m", "\033[31m", "\033[33m", "\033[36m", "\033[2m", "\033[0m",
)
if not sys.stdout.isatty():
    GREEN = RED = YELLOW = CYAN = DIM = OFF = ""

ARGS = None
FAILS = []
CHECKS = 0
CAPTURE = None
S = {}
SEQ = 0

# Every envelope key this suite digs, spelled once. A rename in the mod then
# fails phase 0 loudly instead of turning half this file green-and-empty.
K_ACCEPTED = "data.accepted"
K_ACTIONABLE = "data.accepted_actionable"
K_UNREACHABLE = "data.accepted_unreachable"
K_DESIGNATED = "data.designated"
K_REACH = "data.reach"
K_REFUSED = "data.refused"

# `DesignateReach`'s own constants and vocabulary.
REACH_KEYS = {"applies", "gate", "test", "roster", "work_type", "work_source",
              "capable", "enabled", "unrestricted", "considered", "scored",
              "actionable", "unreachable", "pawns", "pawns_more", "areas",
              "unreachable_targets", "unreachable_more"}
PAWN_KEYS = {"pawn", "name", "enabled", "priority", "downed", "area", "area_id",
             "area_cells", "restricted", "can_work"}
AREA_KEYS = {"id", "label", "cells", "pawns", "excludes"}
REFUSAL_CODES = ["outside-every-allowed-area", "no-capable-pawn"]
TARGET_CAP = 24          # DesignateReach.TargetCap
MAX_CELLS_CEILING = 20000  # DesignateEngine.MaxCellsCeiling

# The fixture verbs. `mine` because every map has rock and Mining is a vanilla
# work type on every colony; `hunt` because it is the verb that killed Marco and
# the one the issue's bench line names.
V_CELL = "mine"
DEF_CELL = "Mine"
WORK_CELL = "Mining"
V_HUNT = "hunt"
DEF_HUNT = "Hunt"
WORK_HUNT = "Hunting"

SEARCH_HALVES = (12, 40, 80)


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=240):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "accb7359fa-%03d-%s" % (SEQ, slug)
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
    """dig() cannot tell ABSENT from PRESENT-AND-NULL, and this suite cares:
    `accepted_actionable` is present and NULL for a designator with no work
    giver (claim, smooth, cancel), which is a real assertion — and
    `eq(..., None)` would pass just as happily if the key had been dropped from
    the serializer altogether."""
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
    if isinstance(cur, list):
        try:
            return 0 <= int(parts[-1]) < len(cur)
        except ValueError:
            return False
    return isinstance(cur, dict) and parts[-1] in cur


def as_list(v):
    if v is None:
        return []
    return v if isinstance(v, list) else [v]


def rows(env, path):
    return [r for r in as_list(dig(env, path)) if isinstance(r, dict)]


def show(v):
    return "null" if v is None else json.dumps(v, separators=(",", ":"))


def num(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


# ------------------------------------------------------------------- asserts --

def check(n, what, ok, expected, actual):
    global CHECKS
    CHECKS += 1
    if CAPTURE is not None:
        CAPTURE.append(bool(ok))
        return
    if ARGS.dry_run:
        print("  %-7s EXPECT  %s: %s" % (n, what, expected))
        return
    if ok:
        print("  %s%-7s PASS    %s%s" % (GREEN, n, what, OFF))
        return
    print("  %s%-7s FAIL    %s%s" % (RED, n, what, OFF))
    print("          expected: %s" % expected)
    print("          actual:   %s" % show(actual))
    FAILS.append(n)


def eq(n, what, env, path, want):
    got = dig(env, path)
    ok = (want is None and got is None) or got == want
    check(n, "%s (%s)" % (what, path), ok, show(want), got)


def eq_val(n, what, got, want):
    check(n, what, got == want, show(want), got)


def eq_int(n, what, env, path, want):
    """eq() against a value READ FROM AN EARLIER ENVELOPE is the
    green-while-asserting-nothing trap in a different hat: if the key vanishes,
    `want` is None and `got` is None and the check passes. Both sides must be
    integers before the comparison counts."""
    got = dig(env, path)
    ok = (num(got) and not isinstance(got, bool)
          and num(want) and not isinstance(want, bool) and got == want)
    check(n, "%s (%s)" % (what, path), ok, "the integer %s" % show(want), got)


def ge(n, what, env, path, want):
    got = dig(env, path)
    ok = num(got) and got >= want
    check(n, "%s (%s)" % (what, path), ok, ">= %s" % want, got)


def ge_val(n, what, got, want):
    check(n, what, num(got) and got >= want, ">= %s" % want, got)


def one_of(n, what, env, path, allowed):
    got = dig(env, path)
    check(n, "%s (%s)" % (what, path), got in allowed, "one of %s" % (allowed,), got)


def nonempty(n, what, env, path):
    got = dig(env, path)
    ok = isinstance(got, str) and got.strip() != ""
    check(n, "%s (%s)" % (what, path), ok, "a non-empty string", got)


def contains(n, what, env, path, needle):
    got = dig(env, path)
    ok = isinstance(got, str) and needle in got
    check(n, "%s (%s)" % (what, path), ok, "a string containing %r" % needle, got)


def null_at(n, what, env, path):
    """PRESENT AND NULL, asserted as two facts on one line. `eq(..., None)` is
    exactly the wrong tool for it — see has_key()."""
    present = has_key(env, path)
    got = dig(env, path)
    check(n, "%s (%s)" % (what, path), present and got is None,
          "the key PRESENT and null",
          "ABSENT — the serializer dropped it" if not present else got)


def shape(n, verb, env, path, kind=None):
    """Assert a key EXISTS, independently of its value. Returns the truth of it
    so a caller can branch; the check is recorded either way."""
    ok = has_key(env, path)
    want = "the key to be present"
    if ok and kind is not None:
        ok = isinstance(dig(env, path), kind)
        want += " and a %s" % kind.__name__
    check(n, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def keys_at_least(n, what, env, path, want):
    got = dig(env, path)
    if not isinstance(got, dict):
        check(n, "%s (%s)" % (what, path), False, "a dict at that path", got)
        return
    missing = sorted(set(want) - set(got))
    check(n, "%s (%s)" % (what, path), not missing,
          "at least %s" % sorted(want), {"missing": missing})


def note(n, text):
    if CAPTURE is not None:
        return
    print("  %s%-7s NOTE    %s%s" % (YELLOW, n, text, OFF))


def finding(n, text):
    if CAPTURE is not None:
        return
    print("  %s%-7s FINDING %s%s" % (CYAN, n, text, OFF))


def precondition(n, what, ok, detail):
    if ARGS.dry_run:
        print("  %s%-7s NEEDS   %s%s" % (CYAN, n, what, OFF))
        return
    if ok:
        print("  %s%-7s OK      precondition: %s%s" % (DIM, n, what, OFF))
        return
    print("  %s%-7s SKIP    precondition NOT MET: %s%s" % (YELLOW, n, what, OFF))
    print("          %s" % detail)
    print("          This is a FIXTURE gap, not a failure of b7359fa.")
    teardown()
    sys.exit(2)


def soft_skip(n, what, detail):
    print("  %s%-7s SKIP    %s%s" % (YELLOW, n, what, OFF))
    print("          %s" % detail)


def banner(t):
    if CAPTURE is not None:
        return
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


# ------------------------------------------------------------------- fixture --

def roster():
    e = send("pawns", {"filter": "colonists", "cap": 40})
    return [p.get("id") for p in as_list(dig(e, "data.pawns"))
            if isinstance(p, dict) and p.get("id") is not None]


def read_posture():
    """`posture` with NO levers is a pure read (its own header says so), which
    is what makes it safe to use as the teardown's memory."""
    return send("posture", {})


def area_of(pid):
    e = read_posture()
    for row in as_list(dig(e, "data.pawns")) + as_list(dig(e, "data.accepted")):
        if not isinstance(row, dict):
            continue
        if row.get("pawn") == pid or row.get("id") == pid:
            return dig(row, "before.area_id"), dig(row, "before.area")
    return None, None


def mineable_cells(want):
    """Cells the CELL verb's own gate accepts, found by dry run rather than by
    guessing at rock. Widening halves around a colonist until enough turn up —
    the last is under DesignateEngine.MaxCellsCeiling as one envelope."""
    anchor = S.get("anchor")
    if anchor is None:
        return []
    for half in SEARCH_HALVES:
        x, z = anchor[0] - half, anchor[1] - half
        w = h = half * 2 + 1
        if w * h > MAX_CELLS_CEILING:
            continue
        e = send("designate", {"type": V_CELL, "rect": [x, z, w, h],
                               "max_cells": min(MAX_CELLS_CEILING, w * h),
                               "dry_run": True, "allow_unreachable": True})
        cells = [c for c in as_list(dig(e, "data.cells")) if isinstance(c, list)]
        if len(cells) >= want:
            return cells[:want]
    return []


def make_area(label, rect):
    e = send("area", {"kind": "allowed", "op": "create", "label": label})
    aid = dig(e, "data.id")
    if aid is None:
        aid = dig(e, "data.area.id")
    if aid is None:
        return None
    send("area", {"kind": "allowed", "op": "add", "id": aid, "rect": rect})
    return aid


def bind(pids, aid):
    return send("posture", {"pawns": pids, "area": aid,
                            "seek": "auto", "hostility": "Attack"})


def designate(kind, cells=None, things=None, extra=None):
    a = {"type": kind}
    if cells is not None:
        a["cells"] = ["%d,%d" % (int(c[0]), int(c[1])) for c in cells]
    if things is not None:
        a["things"] = things
    if extra:
        a.update(extra)
    return send("designate", a)


def teardown():
    if ARGS is None or ARGS.dry_run or ARGS.selftest:
        return
    print("")
    print("%steardown%s" % (DIM, OFF))
    try:
        # Areas FIRST — a colonist bound to an area we are about to delete is
        # the one state this suite must never leave behind.
        for pid, aid in (S.get("orig_area") or {}).items():
            send("posture", {"pawns": [pid], "area": aid,
                             "seek": "auto", "hostility": "Attack",
                             "allow_empty_area": True})
        cells = S.get("fixture_cells") or []
        if cells:
            send("designate", {"type": "cancel",
                               "cells": ["%d,%d" % (int(c[0]), int(c[1])) for c in cells],
                               "allow_unreachable": True})
        for aid in (S.get("made_areas") or []):
            send("area", {"kind": "allowed", "op": "delete", "id": aid})
    except Exception as exc:                                  # noqa: BLE001
        print("  %steardown itself threw: %s%s" % (YELLOW, exc, OFF))


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 - the shape contract, and the fixture")

    e = send("status", {})
    precondition("0.1", "the bench answers `status`",
                 dig(e, "ok") is True or ARGS.dry_run,
                 "status returned %s" % show(dig(e, "error")))
    ops = [str(o) for o in as_list(dig(e, "data.verbs"))]
    need = ["designate", "posture", "area", "pawns", "journal", "pause"]
    missing = [o for o in need if o not in ops]
    check("0.2", "every verb this suite drives is registered",
          not missing or ARGS.dry_run, "no missing ops", {"missing": missing})

    send("pause", {})
    S["watermark"] = dig(send("journal", {"limit": 1}), "data.seq", 0) or 0

    ids = roster()
    precondition("0.3", "at least one colonist", len(ids) >= 1 or ARGS.dry_run,
                 "`pawns {filter:'colonists'}` returned %d" % len(ids))
    S["pawns"] = ids

    # The teardown's memory. Recorded BEFORE anything is bound, and a read that
    # fails aborts rather than proceeding — this suite must be able to put the
    # colony back.
    pe = read_posture()
    orig = {}
    for row in rows(pe, "data.pawns") + rows(pe, "data.accepted"):
        pid = row.get("pawn", row.get("id"))
        if pid in ids:
            orig[pid] = dig(row, "before.area_id")
    precondition("0.4", "`posture` reports every colonist's current area, so teardown "
                        "can restore it",
                 (len(orig) >= len(ids)) or ARGS.dry_run,
                 "read %d of %d colonists' areas from `posture`" % (len(orig), len(ids)))
    S["orig_area"] = orig
    S["made_areas"] = []
    S["fixture_cells"] = []

    pe = send("pawn", {"id": ids[0] if ids else 0, "sections": ["basics"]})
    at = dig(pe, "data.at")
    S["anchor"] = at if isinstance(at, list) else None
    precondition("0.5", "a colonist position to search rock around",
                 S["anchor"] is not None or ARGS.dry_run,
                 "`pawn {sections:['basics']}` published no `at`")

    # --------- THE SHAPE CONTRACT ------------------------------------------
    # Every dig path the later phases use, proved to EXIST on a live envelope.
    # A wrong path would otherwise return None and pass every assertion below.
    e = designate(V_CELL, cells=[S["anchor"]], extra={"dry_run": True,
                                                     "allow_unreachable": True})
    shape("0.6", "designate", e, K_ACCEPTED, int)
    shape("0.7", "designate", e, K_ACTIONABLE)
    shape("0.8", "designate", e, K_UNREACHABLE)
    shape("0.9", "designate", e, K_DESIGNATED, int)
    shape("0.10", "designate", e, "data.designated_from", str)
    shape("0.11", "designate", e, "data.allow_unreachable", bool)
    if shape("0.12", "designate", e, K_REACH, dict):
        keys_at_least("0.13", "the reach block publishes its whole documented field set",
                      e, K_REACH, REACH_KEYS)
    eq("0.14", "the reach block APPLIES to mine (WorkGiver_Miner is claimed by a "
               "WorkGiverDef in this game)", e, "data.reach.applies", True)
    eq("0.15", "…and names the work type from the game's own data",
       e, "data.reach.work_type", WORK_CELL)
    contains("0.16", "…citing the WorkGiverDef it read it from",
             e, "data.reach.work_source", "giverClass")
    contains("0.17", "the gate cites ForbidUtility.InAllowedArea, not a re-derivation",
             e, "data.reach.gate", "ForbidUtility.InAllowedArea")
    contains("0.18", "…and the effective getter, not the plain one",
             e, "data.reach.gate", "EffectiveAreaRestrictionInPawnCurrentMap")
    contains("0.19", "the block says out loud that it is NOT a pathing test",
             e, "data.reach.test", "not a pathing test")
    eq("0.20", "the roster source is named so the caller knows whose reach was computed",
       e, "data.reach.roster", "map.mapPawns.FreeColonistsSpawned")
    if rows(e, "data.reach.pawns"):
        keys_at_least("0.21", "each roster row publishes its documented fields",
                      {"r": rows(e, "data.reach.pawns")[0]}, "r", PAWN_KEYS)

    # A designator with NO work giver must say `applies:false` WITH A REASON —
    # not silently omit the block, and not claim a work type it cannot know.
    e = designate("claim", cells=[S["anchor"]], extra={"dry_run": True})
    eq("0.22", "`claim` produces no pawn work, and the reach block says so",
       e, "data.reach.applies", False)
    nonempty("0.23", "…with a reason rather than a bare false", e, "data.reach.why")
    null_at("0.24", "…and the split counts are PRESENT AND NULL, never 0",
            e, K_ACTIONABLE)
    null_at("0.25", "…both of them", e, K_UNREACHABLE)

    cells = mineable_cells(4)
    precondition("0.26", "at least 4 mineable cells the gate accepts",
                 len(cells) >= 4 or ARGS.dry_run,
                 "found %d — this colony may be on open ground; try a mountain map"
                 % len(cells))
    S["rock"] = cells
    print("  %sfixture: %d colonists, rock at %s%s"
          % (DIM, len(ids), show(cells[:4]), OFF))


# ------------------------------------- 1: the clean case, and it stays clean --

def phase1():
    banner("PHASE 1 - a target INSIDE the area is clean, and the report says nothing else")

    cells = S.get("rock") or []
    if len(cells) < 1:
        return soft_skip("1.0", "no rock fixture", "phase 0 found none")

    # An area that COVERS the fixture, bound to the whole roster: the control.
    xs = [int(c[0]) for c in cells]
    zs = [int(c[1]) for c in cells]
    rect = [min(xs) - 6, min(zs) - 6, (max(xs) - min(xs)) + 13, (max(zs) - min(zs)) + 13]
    aid = make_area("accB7-wide", rect)
    precondition("1.1", "an Area_Allowed covering the fixture",
                 aid is not None or ARGS.dry_run,
                 "`area {op:'create'}` published no id")
    if aid is not None:
        S["made_areas"].append(aid)
    bind(S["pawns"], aid)

    e = designate(V_CELL, cells=cells[:2])
    S["fixture_cells"].extend(cells[:2])
    ge("1.2", "the gate accepted the rock", e, K_ACCEPTED, 1)
    acc = dig(e, K_ACCEPTED)
    eq_int("1.3", "every accepted target is ACTIONABLE inside a covering area",
           e, K_ACTIONABLE, acc)
    eq("1.4", "…and none is unreachable", e, K_UNREACHABLE, 0)
    eq("1.5", "…so the envelope carries no refusal", e, K_REFUSED, None)
    ge("1.6", "the roster has someone who can do the work", e, "data.reach.capable", 1)
    eq("1.7", "`designated` equals `accepted` for a plain (non-flood-fill) designator",
       e, K_DESIGNATED, acc)
    contains("1.8", "…and says which reading that was",
             e, "data.designated_from", "designation delta")
    # The independent witness: the count STANDING on the map moved by what we
    # were told, read from a field this issue did not touch.
    b, a = dig(e, "data.designations_before"), dig(e, "data.designations_now")
    check("1.9", "the map's own designation count moved by `designated`",
          num(b) and num(a) and (a - b) == dig(e, K_DESIGNATED),
          "now - before == %s" % show(dig(e, K_DESIGNATED)), {"before": b, "now": a})
    warn = dig(e, "data.reach.warning")
    check("1.10", "a clean batch raises no area warning",
          warn is None or "outside EVERY" not in str(warn),
          "no allowed-area warning", warn)


# --------------------------------------------- 2: the mixed batch REPORTS --

def phase2():
    banner("PHASE 2 - b7359fa item 2: outside every area is REFUSED; one pawn covering "
           "it makes the same batch a REPORT")

    cells = S.get("rock") or []
    if len(cells) < 3:
        return soft_skip("2.0", "not enough rock", "phase 0 found %d cells" % len(cells))
    target = cells[2:4] or cells[-1:]

    # An area that DELIBERATELY EXCLUDES the fixture: one cell, far from it.
    xs = [int(c[0]) for c in cells]
    zs = [int(c[1]) for c in cells]
    away = [max(0, min(xs) - 30), max(0, min(zs) - 30), 4, 4]
    aid = make_area("accB7-narrow", away)
    precondition("2.1", "a second Area_Allowed that excludes the fixture",
                 aid is not None or ARGS.dry_run, "`area {op:'create'}` published no id")
    if aid is not None:
        S["made_areas"].append(aid)
    S["narrow"] = aid
    bind(S["pawns"], aid)

    # THE HEADLINE. Every capable colonist is now restricted to ground that does
    # not contain the rock, so not one target is workable: a REFUSAL.
    e = designate(V_CELL, cells=target)
    eq("2.2", "the batch is refused rather than accepted-and-silent", e, K_ACCEPTED, 0)
    if shape("2.3", "designate", e, K_REFUSED, dict):
        one_of("2.4", "…with a code from the documented set",
               e, "data.refused.code", REFUSAL_CODES)
        eq("2.5", "…and it is the AREA code, not the no-capable-pawn one",
           e, "data.refused.code", "outside-every-allowed-area")
        nonempty("2.6", "…carrying a reason", e, "data.refused.reason")
        contains("2.7", "…and a hint that names the verb which fixes it",
                 e, "data.refused.hint", "area {kind:\"allowed\"")
    ge("2.8", "the reach block counted the unreachable targets", e, K_UNREACHABLE, 1)
    eq("2.9", "…and nothing was actionable", e, K_ACTIONABLE, 0)

    # NOTHING WAS WRITTEN. The refusal came off a dry preflight, so the map's
    # own count of this designation did not move — asserted from the envelope's
    # own independent pair, not from `accepted`.
    b, a = dig(e, "data.designations_before"), dig(e, "data.designations_now")
    check("2.10", "the refusal wrote NOTHING: the map's designation count did not move",
          num(b) and num(a) and a == b, "before == now", {"before": b, "now": a})
    eq("2.11", "…and `designated` says so in its own words", e, K_DESIGNATED, 0)

    # THE AREA IS NAMED. Acceptance item 2's second half.
    areas = rows(e, "data.reach.areas")
    check("2.12", "the block NAMES the area that excludes the targets",
          any(r.get("id") == S.get("narrow") for r in areas) or ARGS.dry_run,
          "a row for area id %s" % show(S.get("narrow")),
          [r.get("id") for r in areas])
    if areas:
        keys_at_least("2.13", "each area row publishes its documented fields",
                      {"r": areas[0]}, "r", AREA_KEYS)
        ge_val("2.14", "…and says how many targets it shuts out",
               max([r.get("excludes") or 0 for r in areas]), 1)

    # THE OVERRIDE. Same call, one argument, and it goes through.
    e = designate(V_CELL, cells=target, extra={"allow_unreachable": True})
    S["fixture_cells"].extend(target)
    ge("2.15", "allow_unreachable:true designates anyway", e, K_ACCEPTED, 1)
    eq("2.16", "…and does not pretend the targets became reachable",
       e, K_ACTIONABLE, 0)
    ge("2.17", "…still counting them as unreachable", e, K_UNREACHABLE, 1)
    eq("2.18", "…with no refusal block", e, K_REFUSED, None)
    contains("2.19", "…and a warning that names the correction",
             e, "data.reach.warning", "outside EVERY")

    # THE PER-PAWN HALF, which is the whole shape of the fix: bind ONE colonist
    # to a covering area and the SAME batch stops being all-unreachable. A check
    # written against a single colony-wide area cannot pass this.
    wide = None
    for a2 in S.get("made_areas") or []:
        if a2 != S.get("narrow"):
            wide = a2
    if wide is None or not S.get("pawns"):
        return soft_skip("2.20", "no covering area to bind one pawn to",
                         "phase 1 did not run")
    send("designate", {"type": "cancel",
                       "cells": ["%d,%d" % (int(c[0]), int(c[1])) for c in target]})
    bind([S["pawns"][0]], wide)
    e = designate(V_CELL, cells=target)
    ge("2.21", "ONE colonist with a covering area makes the batch actionable again "
               "— the area is PER PAWN", e, K_ACTIONABLE, 1)
    eq("2.22", "…and no target is left outside every area", e, K_UNREACHABLE, 0)
    eq("2.23", "…so it is not refused", e, K_REFUSED, None)
    S["fixture_cells"].extend(target)
    pawn_rows = rows(e, "data.reach.pawns")
    covered = [r for r in pawn_rows if (r.get("can_work") or 0) > 0]
    check("2.24", "…and exactly the pawns who can are listed as able",
          len(covered) >= 1 or ARGS.dry_run, "at least one row with can_work > 0",
          [(r.get("name"), r.get("can_work")) for r in pawn_rows])
    bind(S["pawns"], S["narrow"])


# ----------------------------------------------------- 3: the issue's line --

def phase3():
    banner("PHASE 3 - THE BENCH LINE: `designate hunt` a target outside every allowed area")

    # Every colonist is still bound to the narrow area from phase 2.
    if S.get("narrow") is None:
        return soft_skip("3.0", "phase 2 did not run", "the narrow area is the fixture")
    bind(S["pawns"], S["narrow"])

    e = send("pawns", {"filter": "wildlife", "cap": 40})
    animals = [p for p in rows(e, "data.pawns") if p.get("id") is not None]
    if not animals:
        return soft_skip("3.1", "no wildlife on this map",
                         "`pawns {filter:'wildlife'}` returned none; phase 2 makes the "
                         "same claim on `mine`, which needs no animals")

    # Aim at animals OUTSIDE the narrow area. Their positions come from the
    # observer, never from the designate envelope that is under test.
    ids = [p["id"] for p in animals[:6]]
    e = designate(V_HUNT, things=ids)
    one_of("3.2", "the hunt batch is answered, not silently accepted",
           e, "data.reach.applies", [True])
    eq("3.3", "…and it resolved Hunting from the game's own WorkGiverDef data",
       e, "data.reach.work_type", WORK_HUNT)

    refused = dig(e, K_REFUSED)
    if isinstance(refused, dict):
        # The exact failure the issue describes, now a refusal.
        one_of("3.4", "the refusal names a documented code",
               e, "data.refused.code", REFUSAL_CODES)
        eq("3.5", "…nothing was designated", e, K_ACCEPTED, 0)
        b, a = dig(e, "data.designations_before"), dig(e, "data.designations_now")
        check("3.6", "…and the Hunt count on the map did not move",
              num(b) and num(a) and a == b, "before == now", {"before": b, "now": a})
        ge("3.7", "…while the block says how many targets were unworkable",
           e, K_UNREACHABLE, 1)
    else:
        # Some animals were inside: a MIXED batch reports rather than refusing,
        # which is the other half of acceptance item 2 and equally the point.
        S["hunt_designated"] = True
        shape("3.4", "designate", e, K_ACTIONABLE, int)
        shape("3.5", "designate", e, K_UNREACHABLE, int)
        acc = dig(e, K_ACCEPTED)
        act, unr = dig(e, K_ACTIONABLE), dig(e, K_UNREACHABLE)
        check("3.6", "a MIXED hunt batch reports rather than refusing, and the two "
                     "halves account for what landed",
              num(act) and num(unr) and num(acc) and (act + unr) == dig(e, K_DESIGNATED),
              "actionable + unreachable == designated (%s)" % show(dig(e, K_DESIGNATED)),
              {"accepted": acc, "actionable": act, "unreachable": unr})
        ge("3.7", "…and at least one animal is outside every hunter's area, which is "
                  "the reading run m1-20260901 never got",
           e, K_UNREACHABLE, 0)
        # Put the designations back.
        send("designate", {"type": "cancel", "things": ids})

    # THE JOURNAL. A post-mortem reads the row, not the envelope the agent saw
    # and discarded — so the row must carry the split too.
    seq = dig(e, "data.action.journal_seq")
    if not num(seq) or seq == 0:
        return note("3.8", "no journal seq on the action block (dry run, or the writer "
                           "is closed) — the journal assertion was NOT run")
    j = send("journal", {"since_seq": seq - 1, "types": ["action"], "limit": 5})
    row = None
    for ev in rows(j, "data.events"):
        if ev.get("seq") == seq:
            row = ev
    check("3.8", "the journal row for this call exists at the seq the envelope named",
          isinstance(row, dict), "an action row at seq %s" % show(seq), None)
    if isinstance(row, dict):
        shape("3.9", "journal", {"r": row}, "r.payload.counts.designated")
        shape("3.10", "journal", {"r": row}, "r.payload.counts.actionable")
        shape("3.11", "journal", {"r": row}, "r.payload.counts.unreachable")


# ------------------------------------- 4: an EMPTY area is not a restriction --

def phase4():
    banner("PHASE 4 - TrueCount == 0: an area that exists and binds nothing")

    cells = S.get("rock") or []
    if not cells or not S.get("pawns"):
        return soft_skip("4.0", "no fixture", "phase 0 found none")

    # An Area_Allowed with NO cells painted. `ForbidUtility.InAllowedArea`
    # short-circuits on `TrueCount > 0`, so every cell counts as allowed — and a
    # report that read "has an area" as "is restricted" would flag this colony
    # as fully area-bound while nothing is.
    e = send("area", {"kind": "allowed", "op": "create", "label": "accB7-empty"})
    aid = dig(e, "data.id") or dig(e, "data.area.id")
    if aid is None:
        return soft_skip("4.1", "could not create an empty area",
                         "`area {op:'create'}` published no id")
    S["made_areas"].append(aid)
    r = bind(S["pawns"], aid)
    if dig(r, "ok") is False:
        # `posture` refuses a zero-cell area by design unless told otherwise —
        # that refusal is itself the same fact, from the other side.
        contains("4.2", "`posture` refuses a zero-cell area, citing the same short-circuit",
                 r, "error.detail", "TrueCount > 0")
        r = send("posture", {"pawns": S["pawns"], "area": aid, "seek": "auto",
                             "hostility": "Attack", "allow_empty_area": True})
    e = designate(V_CELL, cells=cells[:1], extra={"dry_run": True})
    pawn_rows = rows(e, "data.reach.pawns")
    bound = [p for p in pawn_rows if p.get("area_id") == aid]
    check("4.3", "the roster reports the pawns as bound to the empty area",
          len(bound) >= 1 or ARGS.dry_run, "at least one row with area_id %s" % show(aid),
          [(p.get("name"), p.get("area_id"), p.get("area_cells")) for p in pawn_rows])
    if bound:
        eq_val("4.4", "…with area_cells 0", bound[0].get("area_cells"), 0)
        eq_val("4.5", "…and `restricted` FALSE, because InAllowedArea ignores an "
                      "area whose TrueCount is 0", bound[0].get("restricted"), False)
    eq("4.6", "…so nothing is unreachable and the batch is not refused",
       e, K_UNREACHABLE, 0)
    ge("4.7", "…and every capable pawn counts as unrestricted",
       e, "data.reach.unrestricted", 1)


# --------------------------------------------------------- 5: no red errors --

def phase5():
    banner("PHASE 5 - the standing invariant: no red errors")
    e = send("journal", {"since_seq": S.get("watermark", 0), "types": ["red_error"],
                         "limit": 50})
    shape("5.1", "journal", e, "data.count")
    eq("5.2", "no red error was authored during this suite", e, "data.count", 0)
    for ev in rows(e, "data.events")[:5]:
        note("5.3", show(dig(ev, "payload")))


# ------------------------------------------------------------- 6: offline --

def probe(fn):
    """Run one assertion body with checks captured instead of printed, and
    return whether every check inside it passed. A helper that cannot fail
    proves nothing."""
    global CAPTURE, FAILS
    saved = list(FAILS)
    CAPTURE = []
    try:
        fn()
        got = all(CAPTURE)
    finally:
        CAPTURE = None
        FAILS = saved
    return got


def _read(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as fh:
            return fh.read()
    except OSError:
        return None


def _first(paths):
    for p in paths:
        if p and os.path.isdir(p):
            return p
    return None


def phase6():
    banner("PHASE 6 - offline: the evidence behind every claim, and the helpers themselves")

    src = _read(os.path.join(REPO, "Source", "AutoRimmer", "DesignateReach.cs")) or ""
    check("6.1", "DesignateReach.cs ships", len(src) > 0, "the file", len(src))
    check("6.2", "it CALLS the game's own extension method rather than indexing an "
                 "Area itself — the gate-lives-in-the-widget rule",
          ".InAllowedArea(" in src, "a call to InAllowedArea", None)
    check("6.3", "…and reads the EFFECTIVE area through the MapHeld guard "
                 "(PawnSafe Class D)",
          "EffectiveAreaRestrictionInPawnCurrentMap" in src and "MapHeld != null" in src,
          "the guarded effective getter", None)
    check("6.4", "…gating GetPriority/WorkIsActive on EverWork (PawnSafe Class B)",
          "EverWork" in src, "an EverWork gate", None)
    check("6.5", "…and snapshotting FreeColonistsSpawned rather than iterating it "
                 "(WorldSafe Class E)",
          "new List<Pawn>(map.mapPawns.FreeColonistsSpawned)" in src,
          "a snapshot copy", None)
    m = re.search(r"public const int TargetCap = (\d+);", src)
    check("6.6", "TargetCap is readable and matches this suite's constant",
          m is not None and int(m.group(1)) == TARGET_CAP,
          str(TARGET_CAP), None if m is None else m.group(1))

    verbs = _read(os.path.join(REPO, "Source", "AutoRimmer", "DesignationVerbs.cs")) or ""
    check("6.7", "the designator table carries a WorkGiver class per entry",
          "typeof(WorkGiver_Miner)" in verbs and "typeof(WorkGiver_HunterHunt)" in verbs,
          "typeof(WorkGiver_*) in the table", None)
    check("6.8", "the refusal runs off a DRY preflight, before any write",
          "RunCells(map, probe, targets.Cells, true," in verbs
          or "RunThings(map, probe, targets.Things, true," in verbs,
          "a dryRun:true preflight pass", None)

    decomp = _first(DECOMP_CANDIDATES)
    if decomp is None:
        note("6.9", "no decompiled RimWorldBase found (set RIMWORLD_DECOMP) — the "
                    "source-derived checks below were NOT run, which is not the same "
                    "as passing")
    else:
        fb = _read(os.path.join(decomp, "RimWorld", "ForbidUtility.cs")) or ""
        flat = re.sub(r"\s+", " ", fb)
        check("6.9", "InAllowedArea still reads the EFFECTIVE area restriction",
              "EffectiveAreaRestrictionInPawnCurrentMap" in fb,
              "the effective getter in ForbidUtility", None)
        check("6.10", "…and still short-circuits on TrueCount > 0, which is why an "
                      "empty area is not a restriction (phase 4)",
              "TrueCount > 0" in flat, "the TrueCount clause", None)
        ps = _read(os.path.join(decomp, "RimWorld", "Pawn_PlayerSettings.cs")) or ""
        check("6.11", "…and the effective getter still has NO null check on MapHeld, "
                      "which is why the roster guards it",
              "EffectiveAreaRestrictionInPawnCurrentMap" in ps, "the getter", None)
        mv = _read(os.path.join(decomp, "RimWorld", "Designator_MineVein.cs")) or ""
        check("6.12", "Designator_MineVein still FLOOD-FILLS at designate time, which is "
                      "why the reach report scores the designation DELTA and not "
                      "`accepted`",
              "FloodFillDesignations" in mv and "floodFiller.FloodFill" in mv,
              "the flood fill", None)

    data_dir = None
    for cand in (os.path.join(VAULT, "_RimWorld-Agent", "Data"),):
        if os.path.isdir(cand):
            data_dir = cand
    if data_dir is None:
        note("6.13", "no RimWorld Data directory found — the WorkGiverDef check was "
                     "NOT run")
    else:
        wg = _read(os.path.join(data_dir, "Core", "Defs", "WorkGiverDefs",
                                "WorkGivers.xml")) or ""
        block = wg.split("<giverClass>WorkGiver_Miner</giverClass>")[-1].split(
            "</WorkGiverDef>")[0]
        check("6.13", "WorkGiver_Miner's own def still declares workType Mining, which "
                      "is what DesignateReach resolves at runtime",
              "<workType>Mining</workType>" in block, "workType Mining", block[:200])
        block = wg.split("<giverClass>WorkGiver_HunterHunt</giverClass>")[-1].split(
            "</WorkGiverDef>")[0]
        check("6.14", "…and WorkGiver_HunterHunt's declares Hunting",
              "<workType>Hunting</workType>" in block, "workType Hunting", block[:200])

    # ---- the helpers themselves -------------------------------------------
    good = {"data": {"accepted": 6, "accepted_actionable": 0, "reach": {"applies": True}}}
    absent = {"data": {"accepted": 6}}
    nulled = {"data": {"accepted_actionable": None}}
    check("6.15", "shape() passes on a present key",
          probe(lambda: shape("x", "v", good, "data.accepted_actionable")), "pass", None)
    check("6.16", "shape() FAILS on an absent key — the whole point of the contract",
          not probe(lambda: shape("x", "v", absent, "data.accepted_actionable")),
          "fail", None)
    check("6.17", "eq(..., None) would have PASSED on that absent key, which is why "
                  "every null assertion here goes through null_at()",
          probe(lambda: eq("x", "w", absent, "data.accepted_actionable", None)),
          "pass (and that is the hazard)", None)
    check("6.18", "null_at() FAILS on an absent key and passes on a present null",
          not probe(lambda: null_at("x", "w", absent, "data.accepted_actionable"))
          and probe(lambda: null_at("x", "w", nulled, "data.accepted_actionable")),
          "fail then pass", None)
    check("6.19", "eq_int() FAILS when the expected value came from a vanished key",
          not probe(lambda: eq_int("x", "w", good, "data.accepted_actionable",
                                   dig(absent, "data.accepted_actionable"))),
          "fail", None)
    check("6.20", "keys_at_least() FAILS on a missing field",
          not probe(lambda: keys_at_least("x", "w", good, "data.reach", REACH_KEYS)),
          "fail", None)


# ---------------------------------------------------------------------- main --

PHASES = {1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5, 6: phase6}
DEFAULT_PHASES = [1, 2, 3, 4, 5, 6]


def main():
    global ARGS
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--root", default=DEFAULT_ROOT, help="the protocol root")
    p.add_argument("--phase", type=int, action="append", choices=[0, 1, 2, 3, 4, 5, 6],
                   help="run only these phases (repeatable); phase 0 always runs")
    p.add_argument("--dry-run", action="store_true",
                   help="print the plan and every expectation, send nothing")
    p.add_argument("--selftest", action="store_true",
                   help="phase 6 only: offline; no bench, no game, nothing sent")
    p.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = p.parse_args()

    print("AutoRimmer acceptance - a designation nobody is allowed to reach (b7359fa)")

    if ARGS.selftest:
        print("mode: --selftest (offline; no bench, no game, nothing sent)")
        phase6()
        return summarise(selftest=True)

    print("protocol root: %s" % ARGS.root)
    if not ARGS.dry_run and not os.path.exists(os.path.join(ARGS.root, "status.json")):
        print("%sno status.json under %s - the ORCHESTRATOR starts the bench with "
              "_RimWorld-Agent/run-agent.sh, or pass --root%s" % (RED, ARGS.root, OFF))
        sys.exit(2)
    if not ARGS.dry_run:
        os.makedirs(os.path.join(ARGS.root, "commands"), exist_ok=True)

    wanted = sorted(set(ARGS.phase or DEFAULT_PHASES) - {0})
    print("phases: 0 + %s" % ", ".join(str(x) for x in wanted))
    print("%sTHIS SUITE REBINDS EVERY COLONIST'S ALLOWED AREA and designates rock. "
          "The teardown restores both; run it on the bench, not on a colony you "
          "care about.%s" % (YELLOW, OFF))

    try:
        phase0()
        for n in wanted:
            PHASES[n]()
    finally:
        teardown()
    return summarise()


def summarise(selftest=False):
    print("")
    print("=" * 78)
    if ARGS.dry_run:
        print("%sRESULT: --dry-run printed %d expectations and asserted NONE of them. "
              "Nothing was sent; no dig path was proved. Run it live.%s"
              % (YELLOW, CHECKS, OFF))
        sys.exit(0)
    if FAILS:
        print("%sRESULT: %d FAILED of %d checks - %s%s"
              % (RED, len(FAILS), CHECKS, ", ".join(FAILS), OFF))
        sys.exit(1)
    if selftest:
        print("%sRESULT: all %d self-checks passed. This proves the ASSERTIONS and the "
              "EVIDENCE behind them, not the mod: no bench was touched.%s"
              % (GREEN, CHECKS, OFF))
    else:
        print("%sRESULT: all %d checks passed%s" % (GREEN, CHECKS, OFF))
    sys.exit(0)


if __name__ == "__main__":
    main()

# ============================================================================
# WHAT THE MERGED CODE ACTUALLY DOES, read from Source/AutoRimmer/ and cited by
# file and member, never by line.
#
# 1. `DesignateReach.Roster(map, giverClass)` resolves the WORK TYPE through the
#    game's own data — `DefDatabase<WorkGiverDef>` matched on `giverClass`, then
#    `workType` — and builds the capable roster from a SNAPSHOT of
#    `map.mapPawns.FreeColonistsSpawned`. `capable` is
#    `!WorkTypeIsDisabled(w)`; `enabled` is `WorkIsActive(w)`, EverWork-gated.
#    A pawn whose effective area read THROWS is dropped and named in
#    `unreadable_pawns` — never silently counted either way, because both
#    silences are wrong and one of them killed a colonist.
#
# 2. `DesignateReach.Score` calls `cell.InAllowedArea(pawn)` — the game's own
#    extension method — per (target, capable pawn). The cached `Area` on each
#    roster row is used for LABELS and the per-area rollup only, never for the
#    decision.
#
# 3. `DesignationVerbs.Designate` runs a DRY PREFLIGHT (a second designator
#    instance, `dryRun:true`) before touching anything, scores the reach over
#    the set the gate would accept, and returns `Unreachable(...)` when not one
#    target is workable. Phases 2 and 3 assert `designations_now ==
#    designations_before` across that refusal, which is the only proof that the
#    refusal preceded the write rather than followed it.
#
# 4. After a real run the reach is RE-SCORED over `DesignateEngine.LandedOf` —
#    the designation DELTA for a cell-targeted def, the accepted things
#    otherwise — because `Designator_MineVein.DesignateSingleCell` flood-fills
#    and `accepted` is then not the work set. `designated_from` names which
#    reading the caller got. This suite's phase 1 asserts the plain case
#    (`designated == accepted`); the flood-fill case belongs to 855117a's suite,
#    which is where `mine-vein` is exercised.
#
# 5. A dry run keeps the PREFLIGHT's score rather than re-scoring over an empty
#    landed set, which is what makes the lesson's item 3 ("re-run it as a
#    dry_run after an area change") work at all.
#
# 6. NOT TESTED, by the code and therefore by this suite: pathing. `reach.test`
#    says so in the envelope. A cell inside a pawn's area can still be
#    unroutable, and `reachable {from, to, pawn}` is that question.
# ============================================================================
