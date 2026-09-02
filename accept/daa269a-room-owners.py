#!/usr/bin/env python3
"""Acceptance runner for daa269a — `owners_total` on a multi-owner barracks.

Same protocol, helpers and exit codes as `accept/1adc737-place-layout.py`; read
that header first, especially the SHAPE CONTRACT note — `eq(..., None)` passes
on an absent key, and this suite is ABOUT a field that read 0 for the wrong
reason, so `shape()` runs before any value is asserted.

    ./accept/daa269a-room-owners.py             # phases 0-3
    ./accept/daa269a-room-owners.py --phase 2   # one phase (0 always runs)
    ./accept/daa269a-room-owners.py --dry-run   # print the plan, send nothing
    ./accept/daa269a-room-owners.py --selftest  # phase 9 only: NO bench needed

Start the bench first (`_RimWorld-Agent/run-agent.sh`) with a colony loaded and
`devMode = True` (phase 2 stages beds), and leave it paused.

WHAT THIS IS TESTING, in one sentence: `room {id:38}` on run m1-20260901's
barracks published three beds with three named owners and `owners_total: 0` in
the same envelope, and the zero was indistinguishable from an unclaimed room.

THE CAUSE IS VANILLA'S OWN SEMANTICS, and knowing that is what makes the fix
checkable rather than a patch. `Verse/Room.cs` `Owners`:

    if (TouchesMapEdge || IsHuge || (Role != Bedroom && Role != PrisonCell
        && Role != Barracks && Role != PrisonBarracks)) yield break;
    var beds = ContainedBeds.Where(x => x.def.building.bed_humanlike);
    if (beds.Count() > 1 && (Role == Barracks || Role == PrisonBarracks)
        && beds.Where(b => b.OwnersForReading.Any()).Count() > 1) yield break;

The gate is not the Barracks ROLE, it is **more than one OWNED bed** — so a
barracks with one owner answers normally and the defect only appears once a
third colonist claims a bed. Vanilla is answering "whose room is this", an
ownership question with an honest empty answer for a shared barracks. The mod
was asking "who sleeps in here". Two questions that agree on every room with
fewer than two owned beds, which is why this survived to a real run.

  * PHASE 1 — THE INVARIANT, over whatever rooms the bench already has:
    `owners_total` equals the distinct pawns across `beds[].owners` in the same
    envelope, room by room. The two routes cannot silently disagree again
    because there is only one route.
  * PHASE 2 — THE BUG'S OWN SHAPE. A room with MORE THAN ONE OWNED BED is the
    exact condition vanilla's `Owners` bails on, and it is the only fixture
    that would have caught this. Staged if the bench does not already have one.
  * PHASE 3 — THE GENUINE ZEROES, so the fix is not hardcoded agreement: beds
    that nobody owns read 0 WITH `beds_total` and `beds_owned` saying why, and
    a room with no beds at all reads 0 for a different, visible reason.
  * PHASE 9 — `--selftest`, offline, over the envelope the ISSUE banked. The
    old `room {id:38}` disagrees with itself, and this suite's own invariant
    fails on it — which is the only honest way to show the check works.

Exit 0 = every check passed · 1 = at least one FAIL · 2 = a fixture
precondition could not be met, which is NOT a spec failure and says so.
"""

import argparse
import json
import os
import re
import sys
import time

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

STUFF = "WoodLog"
# `PlaceVerbs.OwnerCap`. Both the `owners` list and the `beds` list are cut at
# it, and the identity phase 1 asserts only holds exactly when neither was —
# which is why `owners_more` and `beds_more` exist and are read here.
OWNER_CAP = 6
# Phase 2's wait for pawns to claim beds. Colonists auto-claim on the way to
# sleep, so this is real colony time and is bounded.
CLAIM_BUDGET = 90000
CLAIM_STEP = 15000

WHY_CASUALTY = ("accept/daa269a-room-owners.py: a casualty halt would end the "
                "advance this suite uses to let colonists claim beds; 722c951 "
                "owns the casualty halt, this suite owns bed ownership")


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=300):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "accdaa269a-%03d-%s" % (SEQ, slug)
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


def shape(num, verb, env, path, kind=None):
    ok = has_key(env, path)
    want = "the key to be present"
    if ok and kind is not None:
        ok = isinstance(dig(env, path), kind)
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
    print("          This is a FIXTURE gap, not a failure of daa269a.")
    sys.exit(2)


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


# --------------------------------------------------------------- the identity --

def bed_owner_ids(env):
    """Distinct pawn ids across `beds[].owners` — the substitute read run
    m1-20260901 used to grade T6 while the rollup was unreadable, and now the
    independent witness that the rollup agrees with it."""
    ids = set()
    for bed in as_list(dig(env, "data.beds")):
        if not isinstance(bed, dict):
            continue
        for o in as_list(bed.get("owners")):
            if isinstance(o, dict) and o.get("id") is not None:
                ids.add(o["id"])
    return ids


def owned_bed_count(env):
    return sum(1 for b in as_list(dig(env, "data.beds"))
               if isinstance(b, dict) and as_list(b.get("owners")))


def assert_identity(num, env, rid):
    """`owners_total` == |distinct pawns across beds[].owners|.

    Only asserted when NEITHER list was truncated: `beds` is capped at
    `OwnerCap` beds and `owners` at `OwnerCap` pawns, and `owners_total` is
    uncapped, so past the cap the two are legitimately different numbers and
    `beds_more`/`owners_more` are what say so."""
    seen = bed_owner_ids(env)
    total = dig(env, "data.owners_total")
    if dig(env, "data.beds_more") or dig(env, "data.owners_more"):
        note(num, "room %s truncated (beds_more=%s owners_more=%s) — the identity "
                  "is not asserted past the cap, which is what those keys are for"
             % (rid, dig(env, "data.beds_more"), dig(env, "data.owners_more")))
        return
    check(num, "room %s: owners_total == distinct pawns across beds[].owners" % rid,
          total == len(seen), "%d" % len(seen),
          {"owners_total": total, "distinct_in_beds": sorted(seen)})


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 — the bench, and THE SHAPE CONTRACT on the rollup")
    e = send("status")
    precondition("0.1", "the bench answers `status`",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "no status envelope — start _RimWorld-Agent/run-agent.sh")

    e = send("rooms", {"cap": 50})
    S["rooms"] = e
    rid = dig(e, "data.list.0.id")
    precondition("0.2", "the bench has at least one visible room",
                 rid is not None or ARGS.dry_run,
                 "`rooms` listed nothing — load a colony with a built room")
    S["rid0"] = rid if rid is not None else 1

    # Every new key, PRESENT, before a single value is read. `owners_total: 0`
    # already existed and already read as healthy; the keys that make a zero
    # legible are the deliverable, and an absent one must not pass as a null.
    shape("0.3a", "rooms", e, "data.list.0.owners_total", (int, float))
    shape("0.3b", "rooms", e, "data.list.0.owners", list)
    shape("0.3c", "rooms", e, "data.list.0.owners_more", (int, float))
    shape("0.3d", "rooms", e, "data.list.0.owners_source", str)
    shape("0.3e", "rooms", e, "data.list.0.beds_total", (int, float))
    shape("0.3f", "rooms", e, "data.list.0.beds_owned", (int, float))
    eq("0.3g", "the route is NAMED, so a reader never has to guess which of the "
               "game's two ownership questions this answers", e,
       "data.list.0.owners_source", "contained-beds")

    e = send("room", {"id": S["rid0"]})
    S["room0"] = e
    shape("0.4a", "room", e, "data.beds", list)
    shape("0.4b", "room", e, "data.beds_more", (int, float))
    shape("0.4c", "room", e, "data.beds_total", (int, float))
    shape("0.4d", "room", e, "data.owners_total", (int, float))
    shape("0.4e", "room", e, "data.owners_source", str)
    absent("0.5", "no owner enumeration threw on this room — the recording catch "
                  "publishes `owners_error` only when it FIRES, so its absence is "
                  "the news", e, "data.owners_error")


# ------------------------------------------------------------------- phase 1 --

def phase1():
    banner("PHASE 1 — THE INVARIANT, over every room the bench has")

    e = send("rooms", {"cap": 50})
    ids = [r.get("id") for r in as_list(dig(e, "data.list")) if isinstance(r, dict)]
    precondition("1.1", "rooms to check", bool(ids) or ARGS.dry_run, "no rooms listed")
    if ARGS.dry_run:
        ids = [S["rid0"]]

    with_beds = 0
    multi_owned = []
    for i, rid in enumerate(ids[:20]):
        env = send("room", {"id": rid})
        if dig(env, "ok") is not True:
            continue
        if dig(env, "data.beds_total"):
            with_beds += 1
        assert_identity("1.2.%d" % i, env, rid)
        # `beds_total` and the `beds` list are two enumerations of the same
        # room one call apart; if they disagree the snapshot discipline broke.
        listed = len(as_list(dig(env, "data.beds")))
        more = dig(env, "data.beds_more") or 0
        check("1.3.%d" % i, "room %s: beds_total == len(beds) + beds_more" % rid,
              dig(env, "data.beds_total") == listed + more,
              "%d" % (listed + more), dig(env, "data.beds_total"))
        if owned_bed_count(env) > 1:
            multi_owned.append(rid)

    print("  %s%d room(s) with beds; %d with MORE THAN ONE owned bed: %s%s"
          % (DIM, with_beds, len(multi_owned), multi_owned, OFF))
    S["multi_owned"] = multi_owned
    check("1.4", "every room the bench has satisfies the invariant — which is "
                 "the whole of acceptance item 2: there is one route, so the two "
                 "readings cannot disagree",
          True, "asserted above, room by room", "see 1.2.*")


# ------------------------------------------------------------------- phase 2 --

def stage_barracks():
    """Put three beds in one enclosed room and let the colony claim them.

    Colonists auto-claim a bed on the way to sleep — no `assign` call is made,
    and none is available: bed ownership has no verb, which is exactly how run
    m1-20260901 ended up with three auto-claimed beds and a rollup that
    reported none of them."""
    e = send("rooms", {"cap": 50})
    room = None
    for r in as_list(dig(e, "data.list")):
        if not isinstance(r, dict):
            continue
        if r.get("proper") is True and (r.get("cells") or 0) >= 12 \
                and not r.get("prison_cell"):
            room = r
            break
    if room is None:
        return None
    rid = room["id"]
    det = send("room", {"id": rid})
    ext = dig(det, "data.extents")
    if not isinstance(ext, list) or len(ext) != 4:
        return None
    x0, z0, w, h = [int(v) for v in ext]
    placed = 0
    for z in range(z0, z0 + h):
        for x in range(x0, x0 + w):
            if placed >= 3:
                break
            # Only cells the game agrees are IN this room: the extents rect is a
            # bounding box and can include wall cells on a non-rectangular room.
            at = send("room-at", {"at": [x, z]})
            if dig(at, "data.id") != rid or dig(at, "data.blocker") is not None:
                continue
            sp = send("dev:spawn-thing", {"def": "Bed", "pos": [x, z],
                                          "stuff": STUFF, "buildable": True})
            if dig(sp, "ok") is True and dig(sp, "data.placed"):
                placed += 1
    return rid if placed >= 2 else None


def phase2():
    banner("PHASE 2 — MORE THAN ONE OWNED BED: the exact condition vanilla bails on")

    rid = (S.get("multi_owned") or [None])[0]
    if rid is None and not ARGS.dry_run:
        note("2.1", "no room on this bench has two owned beds — staging one")
        rid = stage_barracks()
        if rid is not None:
            spent, ok = 0, False
            while spent < CLAIM_BUDGET and not ok:
                send("advance", {"ticks": CLAIM_STEP,
                                 "through_casualties": WHY_CASUALTY}, timeout=600)
                send("journal", {"since_seq": 0, "limit": 2000})
                spent += CLAIM_STEP
                env = send("room", {"id": rid})
                ok = owned_bed_count(env) > 1
                print("  %s+%d ticks: beds_total=%s beds_owned=%s owners_total=%s%s"
                      % (DIM, CLAIM_STEP, dig(env, "data.beds_total"),
                         dig(env, "data.beds_owned"), dig(env, "data.owners_total"), OFF))
            if not ok:
                rid = None
    if ARGS.dry_run:
        rid = S["rid0"]

    precondition("2.2", "a room with more than one OWNED bed",
                 rid is not None,
                 "no room on this bench has two beds claimed by two different "
                 "colonists, and %d ticks of staging did not produce one. That is "
                 "the ONLY shape that reproduces this defect — vanilla's `Owners` "
                 "answers correctly for every room with fewer than two owned beds "
                 "— so the suite cannot prove the fix here." % CLAIM_BUDGET)

    e = send("room", {"id": rid})
    S["barracks"] = e
    seen = bed_owner_ids(e)

    ge("2.3a", "more than one bed is OWNED — vanilla's Room.Owners yields NOTHING "
               "from here, and the old rollup reported that as an empty room", e,
       "data.beds_owned", 2)
    ge("2.3b", "…and the rollup names them anyway", e, "data.owners_total", 2)
    check("2.3c", "owners_total == the distinct pawns in beds[].owners",
          dig(e, "data.owners_total") == len(seen), "%d" % len(seen),
          {"owners_total": dig(e, "data.owners_total"), "in_beds": sorted(seen)})
    check("2.3d", "…and `owners` NAMES them, not just counts them",
          len(as_list(dig(e, "data.owners"))) == min(len(seen), OWNER_CAP),
          "%d rows" % min(len(seen), OWNER_CAP), dig(e, "data.owners"))
    for i, o in enumerate(as_list(dig(e, "data.owners"))[:3]):
        row = {"data": o}
        shape("2.4.%d.a" % i, "room", row, "data.id", (int, float))
        shape("2.4.%d.b" % i, "room", row, "data.name", str)

    # The plural verb has to agree with the singular one: `rooms` was the other
    # half of the original report.
    e = send("rooms", {"cap": 50})
    row = next((r for r in as_list(dig(e, "data.list"))
                if isinstance(r, dict) and r.get("id") == rid), None)
    check("2.5a", "`rooms` reports the same room", row is not None or ARGS.dry_run,
          "a row for room %s" % rid, [r.get("id") for r in as_list(dig(e, "data.list"))
                                      if isinstance(r, dict)])
    if row:
        check("2.5b", "…with the SAME owners_total as `room` — the two verbs "
                      "shared the broken source and now share the fixed one",
              row.get("owners_total") == dig(S["barracks"], "data.owners_total"),
              dig(S["barracks"], "data.owners_total"), row.get("owners_total"))
        check("2.5c", "…and the same beds_owned",
              row.get("beds_owned") == dig(S["barracks"], "data.beds_owned"),
              dig(S["barracks"], "data.beds_owned"), row.get("beds_owned"))

    # T6 of 664e9b9's thrive table, read the way it was always meant to be.
    e = send("digest")
    n = dig(e, "data.colonists.total") or dig(e, "data.colonists.count")
    if isinstance(n, (int, float)):
        print("  %sT6 substitute read: owners_total=%s against %s colonists%s"
              % (DIM, dig(S["barracks"], "data.owners_total"), n, OFF))


# ------------------------------------------------------------------- phase 3 --

def phase3():
    banner("PHASE 3 — THE GENUINE ZEROES, so the fix is not hardcoded agreement")

    e = send("rooms", {"cap": 50})
    rows = [r for r in as_list(dig(e, "data.list")) if isinstance(r, dict)]

    no_beds = next((r for r in rows if r.get("beds_total") == 0), None)
    if no_beds is None:
        note("3.1", "every listed room has a bed — the no-bed zero is not "
                    "reachable on this bench")
    else:
        env = {"data": no_beds}
        eq("3.1a", "a room with no beds reads 0", env, "data.owners_total", 0)
        eq("3.1b", "…and says WHY: there are no beds", env, "data.beds_total", 0)
        eq("3.1c", "…and none owned", env, "data.beds_owned", 0)

    unclaimed = next((r for r in rows
                      if (r.get("beds_total") or 0) > 0 and r.get("beds_owned") == 0),
                     None)
    if unclaimed is None:
        note("3.2", "no room on this bench has beds that nobody owns — the "
                    "unclaimed zero is not reachable. THIS IS THE CASE THE OLD "
                    "FIELD WAS INDISTINGUISHABLE FROM, so its absence is worth "
                    "saying out loud rather than passing over.")
    else:
        env = {"data": unclaimed}
        eq("3.2a", "unclaimed beds read 0 owners", env, "data.owners_total", 0)
        ge("3.2b", "…with beds_total saying there ARE beds — which is the whole "
                   "difference between 'nobody owns these' and 'the rollup is "
                   "broken'", env, "data.beds_total", 1)
        eq("3.2c", "…and beds_owned agreeing", env, "data.beds_owned", 0)

    # Medical and prisoner beds: acceptance item 3's vocabulary. Reported rather
    # than staged — a prison cell needs a prisoner and this suite will not make
    # one.
    med, pris = [], []
    for r in rows[:20]:
        env = send("room", {"id": r.get("id")})
        for b in as_list(dig(env, "data.beds")):
            if not isinstance(b, dict):
                continue
            if b.get("medical"):
                med.append((r.get("id"), b.get("id"), len(as_list(b.get("owners")))))
            if b.get("for_prisoners"):
                pris.append((r.get("id"), b.get("id"), len(as_list(b.get("owners")))))
    print("  %smedical beds seen: %s%s" % (DIM, med or "none", OFF))
    print("  %sprisoner beds seen: %s%s" % (DIM, pris or "none", OFF))
    if med:
        check("3.3", "a medical bed is FLAGGED, so a zero on a hospital room is "
                     "readable rather than mysterious", True,
              "beds[].medical present and true", med)
    else:
        note("3.3", "no medical bed on this bench")
    if pris:
        check("3.4", "a prisoner bed is FLAGGED the same way", True,
              "beds[].for_prisoners present and true", pris)
    else:
        note("3.4", "no prisoner bed on this bench")


# ------------------------------------------------------------------- phase 9 --

def probe(fn):
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


# THE ENVELOPE THE ISSUE BANKED, verbatim from git-bug daa269a's body: `room
# {id:38}` on run m1-20260901's barracks. Three beds, three distinct owners, one
# per bed — and `owners_total: 0` in the same response.
ROOM_38 = {
    "ok": True, "op": "room",
    "data": {
        "id": 38, "role": "Barracks", "cells": 35, "proper": True,
        "open_roof_cells": 0,
        "beds": [
            {"id": 46166, "def": "Bed", "at": [105, 141], "for_prisoners": False,
             "medical": False, "owners": [{"id": 313, "name": "Wouter"}]},
            {"id": 46186, "def": "Bed", "at": [107, 141], "for_prisoners": False,
             "medical": False, "owners": [{"id": 310, "name": "Lacey"}]},
            {"id": 46139, "def": "Bed", "at": [109, 141], "for_prisoners": False,
             "medical": False, "owners": [{"id": 323, "name": "Jimmy"}]},
        ],
        "owners_total": 0,
        "owners": [],
    },
}


def phase9():
    banner("PHASE 9 — the suite's OWN machinery, over the envelope the ISSUE "
           "banked (offline; no bench, no game)")

    ids = bed_owner_ids(ROOM_38)
    check("9.1a", "the banked room 38 names three distinct owners in beds[]",
          ids == {313, 310, 323}, "{310, 313, 323}", sorted(ids))
    check("9.1b", "…on three separately owned beds — which is the >1 OWNED BED "
                  "condition `Verse/Room.Owners` takes its second yield break on",
          owned_bed_count(ROOM_38) == 3, "3", owned_bed_count(ROOM_38))
    eq("9.1c", "…while the rollup published zero", ROOM_38, "data.owners_total", 0)

    check("9.2", "THE INVARIANT FAILS on that envelope — which is the only "
                 "honest way to show phase 1 is checking anything",
          not probe(lambda: assert_identity("x", ROOM_38, 38)),
          "fail", "pass")

    # The absent-vs-null trap, on the same real envelope: it predates every key
    # that makes a zero legible.
    check("9.3a", "the banked envelope carries no `owners_source`",
          not has_key(ROOM_38, "data.owners_source"), "absent",
          dig(ROOM_38, "data.owners_source"))
    check("9.3b", "eq(...,None) PASSES on it — THE TRAP",
          probe(lambda: eq("x", "t", ROOM_38, "data.owners_source", None)),
          "pass", "fail")
    check("9.3c", "shape() FAILS on it — the trap, closed",
          not probe(lambda: shape("x", "room", ROOM_38, "data.owners_source", str)),
          "fail", "pass")
    check("9.3d", "…and no `beds_total` either, which is the key that would have "
                  "made the zero readable at the time",
          not has_key(ROOM_38, "data.beds_total"), "absent",
          dig(ROOM_38, "data.beds_total"))
    check("9.3e", "…so `owners_total: 0` on that envelope is indistinguishable "
                  "from a barracks nobody has claimed. That is the defect, stated "
                  "as a property of the envelope rather than of the code.",
          dig(ROOM_38, "data.owners_total") == 0
          and not has_key(ROOM_38, "data.beds_owned"),
          "0 owners and no beds_owned to explain it",
          [dig(ROOM_38, "data.owners_total"), has_key(ROOM_38, "data.beds_owned")])

    # …and the invariant PASSES on the same envelope repaired, so 9.2 is not
    # merely "assert_identity always fails".
    fixed = json.loads(json.dumps(ROOM_38))
    fixed["data"]["owners_total"] = 3
    fixed["data"]["owners"] = [{"id": 313, "name": "Wouter"},
                               {"id": 310, "name": "Lacey"},
                               {"id": 323, "name": "Jimmy"}]
    check("9.4", "…and PASSES on the same envelope with the rollup repaired",
          probe(lambda: assert_identity("x", fixed, 38)), "pass", "fail")

    check("9.5", "bed_owner_ids() dedupes — a double bed lists the same two "
                 "owners on both halves and owners_total is a HEADCOUNT",
          bed_owner_ids({"data": {"beds": [
              {"owners": [{"id": 1}, {"id": 2}]},
              {"owners": [{"id": 1}, {"id": 2}]}]}}) == {1, 2},
          "{1, 2}", "something else")


# ---------------------------------------------------------------------- main --

PHASES = {0: phase0, 1: phase1, 2: phase2, 3: phase3, 9: phase9}


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
                    help="phase 9 only: the suite's own assertions over the "
                         "envelope git-bug daa269a banked. No bench.")
    ap.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = ap.parse_args()

    if ARGS.selftest:
        print("daa269a acceptance — mode: --selftest")
        print("offline; no bench, no protocol root, no game, nothing sent")
        phase9()
        banner("RESULT")
        if FAILS:
            print("%s%d/%d selftest checks FAILED: %s%s"
                  % (RED, len(FAILS), CHECKS, ", ".join(FAILS), OFF))
            return 1
        print("%sSELFTEST PASS — all %d checks%s" % (GREEN, CHECKS, OFF))
        return 0

    wanted = sorted(set(ARGS.phase or [p for p in PHASES if p != 9]))
    if 0 not in wanted and wanted != [9]:
        wanted = [0] + wanted

    print("daa269a room-owners acceptance — root %s" % ARGS.root)
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
