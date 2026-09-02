#!/usr/bin/env python3
"""Acceptance runner for 855117a — a mine designation can be AIMED, and the
envelope says what it hit.

    ./accept/855117a-mine-vein.py                 # everything safe
    ./accept/855117a-mine-vein.py --phase 2       # one phase (0 always runs)
    ./accept/855117a-mine-vein.py --dry-run       # print the plan, send nothing
    ./accept/855117a-mine-vein.py --selftest      # offline; no bench, nothing sent

Same protocol, helpers and exit codes as `accept/8b0b88f-already-designated.py`
and `accept/eef837a-bill-filter.py`; read either header first.

Start the bench (`_RimWorld-Agent/run-agent.sh`), load a colony, leave it
**PAUSED** — phase 0 makes that a precondition. An unpaused miner finishes a
designated cell between the two calls this suite compares, which deletes the
designation and makes the second call ACCEPT for a fixture reason that looks
exactly like the defect.

THE ISSUE, in one paragraph. `designate {type:"mine", rect:[131,116,6,10]}`
answered `accepted: 14 of 60` and there was no field in the envelope that could
tell fourteen cells of ore from fourteen cells of worthless rock. `map-view`'s
`%` glyph collapses `sandstone | marble | compacted steel` into one character,
so the rect had been chosen off a view that cannot distinguish them. The verb
now publishes `composition` — a per-def rollup of what actually LANDED, with the
`mineable_thing` and yield an ore cell drops — and `mine-vein`, which was
registered and had never been exercised, is exercised here.

THREE THINGS THIS SUITE PROVES THAT NOTHING ELSE DOES:
  * **`mine-vein` works and FLOOD-FILLS.** `Designator_MineVein
    .DesignateSingleCell` calls `FloodFillDesignations`, so one accepted cell
    paints the whole contiguous vein — `designated` moves by more than
    `accepted`, and phase 2 asserts exactly that inequality. Every other suite
    in `accept/` would read `accepted: 1` and call it a one-cell job.
  * **The order between `mine` and `mine-vein` is NOT symmetric.**
    `mine` over vein-marked ground is REFUSED (`Designator_Mine
    .CanDesignateThing`'s third clause) and now reports
    `already-designated-other` with `designation_present: "MineVein"` instead of
    `not-designatable`, which is the residual `8b0b88f` recorded and left.
    `mine-vein` over mine-marked ground is ACCEPTED and REPLACES it —
    `FloodFillDesignations` calls `TryRemoveDesignation(c, Mine)` on every cell
    — now published as `replaced`. Phases 3 and 4.
  * **`composition` is keyed on what LANDED, not on `accepted`**, which is the
    only reading that survives the flood fill.

THIS SUITE MUTATES, reversibly: it adds Mine and MineVein designations to rock
and ore cells and cancels them in a teardown that runs even when a check fails
or a precondition aborts. `designate cancel` over the fixture CELLS is the
universal route, and it also clears any OTHER cancelable designation on those
exact cells — phase 0 only ever picks cells whose dry-run designate was
ACCEPTED, and the gates reject an already-designated target, so a cell this
suite touches carried none of that def before the run. **`Designator_Cancel`
cancels a MineVein designation CONTIGUOUSLY** (`RemoveContiguousDesignations`),
which is what makes the teardown able to undo a flood fill at all.

Exit 0 = every check passed · 1 = at least one FAIL · 2 = a fixture
precondition could not be met, which is NOT a spec failure and says so.
"""

import argparse
import base64
import json
import os
import re
import sys
import time
import zlib

# --------------------------------------------------------------------- setup --

VAULT = os.environ.get("RIMWORLD_VAULT", "/home/dorian/projects/rimworld")
DEFAULT_ROOT = os.environ.get("RWA_ROOT") or os.path.join(
    VAULT,
    "_RimWorld-Agent/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/AutoRimmer",
)
REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# The run's artifacts live in the repo. `RIMWORLD_RUN_ROOT` overrides the
# checkout they are read from, which is what makes phase 6 runnable from a
# sparse worktree that does not carry the 450MB of saves.
RUN_ROOT = os.environ.get("RIMWORLD_RUN_ROOT") or os.path.join(REPO, "RUNS", "m1-20260901")
RUN_SAVES = os.path.join(RUN_ROOT, "saves")
RUN_JOURNAL = os.path.join(RUN_ROOT, "journal")
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
FINDINGS = []
CHECKS = 0
CAPTURE = None
S = {}
SEQ = 0

V_MINE = "mine"
V_VEIN = "mine-vein"
DEF_MINE = "Mine"
DEF_VEIN = "MineVein"

# The reject keys, spelled once. `WHY_OTHER` is this issue's addition
# (DesignateEngine.WhyAlreadyOther); the other two are what it is carved out of.
WHY_ALREADY = "already-designated"
WHY_OTHER = "already-designated-other"
WHY_NOT = "not-designatable"

COMP_KEYS = {"def", "label", "by", "count"}
ORE_KEYS = {"mineable", "vein_mineable", "resource_rock", "natural_rock"}
BY_VALUES = ["mineable", "edifice", "terrain", "thing", "empty", "out-of-bounds"]
ROW_CAP = 24            # DesignateComposition.RowCap
MAX_CELLS_CEILING = 20000
SEARCH_HALVES = (12, 40, 80)

# The m1-20260901 numbers this suite re-derives offline in phase 6, so the
# claims in the issue and in DESIGN.md are asserted rather than remembered.
RUN_TARGETED = 60
RUN_ACCEPTED = 14
RUN_RECT = (131, 116, 6, 10)
RUN_ACCEPTED_CELLS = [(134, 117), (135, 117), (136, 117), (134, 118), (134, 119),
                      (133, 120), (134, 120), (133, 121), (132, 122), (133, 122),
                      (132, 123), (132, 124), (131, 125), (132, 125)]
# The four cells `nearest {def:"MineableSteel"}` named in the issue.
RUN_STEEL_CELLS = [(133, 122), (133, 121), (134, 120), (134, 119)]
MAP_SIZE_X = 250


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=240):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acc855117a-%03d-%s" % (SEQ, slug)
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
    """dig() cannot tell ABSENT from PRESENT-AND-NULL, and this suite cares: an
    ore row's `mineable_thing` is ABSENT on plain rock (a row of nulls would
    read as "this produces nothing" rather than "this is not ore"), and
    `eq(..., None)` cannot tell those apart."""
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


def eq_int(n, what, got, want):
    ok = num(got) and num(want) and got == want
    check(n, what, ok, "the integer %s" % show(want), got)


def ge(n, what, env, path, want):
    got = dig(env, path)
    ok = num(got) and got >= want
    check(n, "%s (%s)" % (what, path), ok, ">= %s" % want, got)


def ge_val(n, what, got, want):
    check(n, what, num(got) and got >= want, ">= %s" % want, got)


def gt_val(n, what, got, want):
    check(n, what, num(got) and num(want) and got > want, "> %s" % show(want), got)


def one_of(n, what, env, path, allowed):
    got = dig(env, path)
    check(n, "%s (%s)" % (what, path), got in allowed, "one of %s" % (allowed,), got)


def contains(n, what, env, path, needle):
    got = dig(env, path)
    ok = isinstance(got, str) and needle in got
    check(n, "%s (%s)" % (what, path), ok, "a string containing %r" % needle, got)


def shape(n, verb, env, path, kind=None):
    ok = has_key(env, path)
    want = "the key to be present"
    if ok and kind is not None:
        ok = isinstance(dig(env, path), kind)
        want += " and a %s" % kind.__name__
    check(n, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def absent(n, verb, env, path, why):
    """The mirror of shape(), and the only way to prove a key is NOT there.
    `mineable_thing` on a plain-rock row is the case: `eq(..., None)` would be a
    false claim about it."""
    check(n, "`%s` does NOT publish %s — %s" % (verb, path, why),
          not has_key(env, path), "the key to be ABSENT", dig(env, path))


def keys_at_least(n, what, obj, want):
    if not isinstance(obj, dict):
        check(n, what, False, "a dict", obj)
        return
    missing = sorted(set(want) - set(obj))
    check(n, what, not missing, "at least %s" % sorted(want), {"missing": missing})


def note(n, text):
    if CAPTURE is not None:
        return
    print("  %s%-7s NOTE    %s%s" % (YELLOW, n, text, OFF))


def finding(n, text):
    """A defect or a discrepancy REPORTED rather than asserted — the exit code
    answers "were the acceptance bullets met", and a suite that goes permanently
    red over a metadata string teaches the next session to ignore its colour."""
    FINDINGS.append((n, text))
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
    print("          This is a FIXTURE gap, not a failure of 855117a.")
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

def cellarg(c):
    return "%d,%d" % (int(c[0]), int(c[1]))


def designate(kind, cells=None, extra=None):
    a = {"type": kind, "allow_unreachable": True}
    if cells is not None:
        a["cells"] = [cellarg(c) for c in cells]
    if extra:
        a.update(extra)
    return send("designate", a)


def remember(cells):
    for c in cells:
        S.setdefault("fixture_cells", []).append(c)


def find_cells(kind, want, veins_only=False):
    """Cells the named designator's own gate ACCEPTS, found by dry run over
    widening squares rather than by guessing at rock. `mine-vein` accepts ore
    only (`ThingDef.building.veinMineable`), so the same search doubles as the
    ore locator — and that is deliberate: this suite must not hardcode
    `MineableSteel`, which is not on every map."""
    anchor = S.get("anchor")
    if anchor is None:
        return []
    for half in SEARCH_HALVES:
        x, z = anchor[0] - half, anchor[1] - half
        w = h = half * 2 + 1
        if w * h > MAX_CELLS_CEILING:
            continue
        e = designate(kind, extra={"rect": [x, z, w, h], "dry_run": True,
                                   "max_cells": min(MAX_CELLS_CEILING, w * h)})
        cells = [c for c in as_list(dig(e, "data.cells")) if isinstance(c, list)]
        if len(cells) >= want:
            return cells[:want]
    return []


def standing(def_name):
    """How many designations of a def stand on the map, read INDEPENDENTLY of
    the envelope that claims to have changed it — a designate over one cell
    whose `designations_before` is the count. A claim about state is never read
    out of the mutation's own echo."""
    anchor = S.get("anchor")
    if anchor is None:
        return None
    verb = V_MINE if def_name == DEF_MINE else V_VEIN
    e = designate(verb, cells=[anchor], extra={"dry_run": True})
    return dig(e, "data.designations_before")


def teardown():
    if ARGS is None or ARGS.dry_run or ARGS.selftest:
        return
    print("")
    print("%steardown%s" % (DIM, OFF))
    try:
        cells = S.get("fixture_cells") or []
        if cells:
            # Designator_Cancel.DesignateSingleCell calls
            # Designator_MineVein.RemoveContiguousDesignations, so cancelling
            # one vein cell cancels the whole contiguous vein — which is the
            # only thing that makes a flood fill undoable from here.
            send("designate", {"type": "cancel",
                               "cells": [cellarg(c) for c in cells],
                               "allow_unreachable": True})
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
    missing = [o for o in ["designate", "pawns", "pawn", "nearest", "journal", "pause"]
               if o not in ops]
    check("0.2", "every verb this suite drives is registered",
          not missing or ARGS.dry_run, "no missing ops", {"missing": missing})

    send("pause", {})
    S["watermark"] = dig(send("journal", {"limit": 1}), "data.seq", 0) or 0
    S["fixture_cells"] = []

    pe = send("pawns", {"filter": "colonists", "cap": 5})
    pid = dig(pe, "data.pawns.0.id")
    at = dig(send("pawn", {"id": pid, "sections": ["basics"]}), "data.at") if pid else None
    S["anchor"] = at if isinstance(at, list) else None
    precondition("0.3", "a colonist position to search rock around",
                 S["anchor"] is not None or ARGS.dry_run,
                 "`pawn {sections:['basics']}` published no `at`")

    # ---- THE SHAPE CONTRACT ----------------------------------------------
    rock = find_cells(V_MINE, 6)
    precondition("0.4", "at least 6 mineable cells the `mine` gate accepts",
                 len(rock) >= 6 or ARGS.dry_run,
                 "found %d — this colony may be on open ground; try a mountain map"
                 % len(rock))
    S["rock"] = rock

    e = designate(V_MINE, cells=rock[:1], extra={"dry_run": True})
    shape("0.5", "designate", e, "data.composition", list)
    shape("0.6", "designate", e, "data.composition_more", int)
    shape("0.7", "designate", e, "data.composition_total", int)
    comp = rows(e, "data.composition")
    check("0.8", "a dry-run mine over rock produces at least one composition row",
          len(comp) >= 1 or ARGS.dry_run, "one or more rows", comp)
    if comp:
        keys_at_least("0.9", "each composition row publishes its documented fields",
                      comp[0], COMP_KEYS)
        one_of("0.10", "`by` names which grid answered", {"r": comp[0]}, "r.by", BY_VALUES)
        eq_val("0.11", "…and for a mine designation it is always the cell's MINEABLE, "
                       "because that is what Designator_Mine.CanDesignateCell gated on",
               comp[0].get("by"), "mineable")
        keys_at_least("0.12", "…carrying the mineable flags", comp[0], ORE_KEYS)
    check("0.13", "the composition total accounts for every cell the call would land",
          dig(e, "data.composition_total") == dig(e, "data.designated") or ARGS.dry_run,
          "composition_total == designated (%s)" % show(dig(e, "data.designated")),
          dig(e, "data.composition_total"))

    ore = find_cells(V_VEIN, 1)
    S["ore"] = ore
    if not ore:
        note("0.14", "no VEIN-MINEABLE cell within %d of the colonist — phases 2 and 4 "
                     "will soft-skip. `Designator_MineVein.CanDesignateThing` requires "
                     "ThingDef.building.veinMineable, which plain rock does not have, so "
                     "this is a map without exposed ore rather than a defect."
                     % SEARCH_HALVES[-1])
    else:
        print("  %sfixture: rock at %s, ore seed at %s%s"
              % (DIM, show(rock[:3]), show(ore[0]), OFF))


# ------------------------------------------ 1: the report the issue asked for --

def phase1():
    banner("PHASE 1 - 855117a item 1: `composition` says WHAT was designated")

    rock = S.get("rock") or []
    if len(rock) < 3:
        return soft_skip("1.0", "not enough rock", "phase 0 found %d" % len(rock))
    cells = rock[:3]

    e = designate(V_MINE, cells=cells)
    remember(cells)
    ge("1.1", "the gate accepted the rock", e, "data.accepted", 1)
    comp = rows(e, "data.composition")
    check("1.2", "the envelope carries a per-def rollup", len(comp) >= 1 or ARGS.dry_run,
          "one or more rows", comp)

    # THE HEADLINE: the counts add up to what landed, so a caller can trust the
    # rollup as a partition rather than as a sample.
    total = sum(r.get("count") or 0 for r in comp)
    eq_int("1.3", "the rollup partitions what LANDED — sum(count) == designated",
           total, dig(e, "data.designated"))
    eq_int("1.4", "…and composition_total agrees",
           dig(e, "data.composition_total"), total)
    check("1.5", "every row names a real def", all(isinstance(r.get("def"), str) for r in comp),
          "a def name on every row", [r.get("def") for r in comp])
    check("1.6", "…and is sorted largest-first, so a truncated list is the useful half",
          all((comp[i].get("count") or 0) >= (comp[i + 1].get("count") or 0)
              for i in range(len(comp) - 1)),
          "counts descending", [r.get("count") for r in comp])
    check("1.7", "the row cap is honoured and the OVERFLOW is counted, not dropped",
          len(comp) <= ROW_CAP and num(dig(e, "data.composition_more")),
          "<= %d rows plus a composition_more count" % ROW_CAP,
          {"rows": len(comp), "more": dig(e, "data.composition_more")})

    # THE ORE HALF. On a rock-only fixture `mineable_thing` is ABSENT, which is
    # a real claim and not the same as null.
    ore_rows = [r for r in comp if "mineable_thing" in r]
    if ore_rows:
        r = ore_rows[0]
        check("1.8", "an ore row names what the cell will drop",
              isinstance(r.get("mineable_thing"), str), "a def name", r.get("mineable_thing"))
        ge_val("1.9", "…with the def's own raw yield", r.get("mineable_yield"), 1)
        ge_val("1.10", "…AND this game's difficulty-scaled yield, published separately "
                       "because BuildingProperties.EffectiveMineableYield is not "
                       "mineableYield", r.get("yield_effective"), 0)
        eq_val("1.11", "…and an ore row is resource rock", r.get("resource_rock"), True)
    else:
        plain = comp[0] if comp else {}
        absent("1.8", "designate", {"r": plain}, "r.mineable_thing",
               "plain rock drops nothing, and an absent key says that where a null "
               "would read as 'produces nothing'")
        eq_val("1.9", "…and it is still flagged as mineable, which is why the gate took it",
               plain.get("mineable"), True)
        note("1.10", "no ore in the fixture rect — the yield fields are exercised by "
                     "phase 2 if there is a vein anywhere near the colony")

    # AND IN THE JOURNAL, because a post-mortem reads the row and not the
    # envelope the agent saw and discarded.
    seq = dig(e, "data.action.journal_seq")
    if not num(seq) or seq == 0:
        return note("1.11", "no journal seq on the action block — the journal assertion "
                            "was NOT run")
    j = send("journal", {"since_seq": seq - 1, "types": ["action"], "limit": 5})
    row = None
    for ev in rows(j, "data.events"):
        if ev.get("seq") == seq:
            row = ev
    check("1.12", "the journal row carries the composition too",
          isinstance(row, dict) and isinstance(dig(row, "payload.composition"), list),
          "a composition list on the action payload", dig(row, "payload.composition"))


# --------------------------------------------- 2: mine-vein, exercised at last --

def phase2():
    banner("PHASE 2 - 855117a item 2: `mine-vein` WORKS, and it flood-fills")

    ore = S.get("ore") or []
    if not ore:
        return soft_skip("2.0", "no vein-mineable cell near the colony",
                         "phase 0 found none; `Designator_MineVein.CanDesignateThing` "
                         "requires ThingDef.building.veinMineable")
    seed = ore[0]

    before = standing(DEF_VEIN)
    e = designate(V_VEIN, cells=[seed])
    remember([seed])
    eq("2.1", "the seed cell is accepted", e, "data.accepted", 1)
    eq("2.2", "…by Designator_MineVein, not by Designator_Mine",
       e, "data.designator", "Designator_MineVein")
    eq("2.3", "…adding the MineVein designation", e, "data.designation", DEF_VEIN)

    # THE FLOOD FILL, and the reason `designated` had to exist at all.
    des = dig(e, "data.designated")
    ge_val("2.4", "at least the seed cell landed", des, 1)
    b, a = dig(e, "data.designations_before"), dig(e, "data.designations_now")
    check("2.5", "the map's own MineVein count moved by `designated` — so `designated` "
                 "is the size of the job and `accepted` is not",
          num(b) and num(a) and (a - b) == des, "now - before == %s" % show(des),
          {"before": b, "now": a})
    contains("2.6", "…and the envelope says which reading that was",
             e, "data.designated_from", "designation delta")
    if num(des) and des > 1:
        gt_val("2.7", "ONE accepted cell painted the whole contiguous vein — the "
                      "FloodFillDesignations behaviour nothing had ever exercised",
               des, dig(e, "data.accepted"))
    else:
        note("2.7", "the vein was one cell wide, so designated == accepted == 1. The "
                    "flood fill ran (DesignateSingleCell has no other path); it simply "
                    "had nowhere to go. Not a failure, and not a demonstration either.")
        finding("2.7", "flood-fill NOT demonstrated: the fixture vein was a single cell")

    # The composition of a flood fill: every cell is the SAME def, because
    # FloodFillDesignations' validator is `c.GetEdifice(map)?.def != def -> stop`.
    comp = rows(e, "data.composition")
    check("2.8", "a flood fill is one def by construction — the validator stops at any "
                 "cell whose edifice def differs from the seed's",
          len(comp) == 1 or ARGS.dry_run, "exactly one composition row",
          [(r.get("def"), r.get("count")) for r in comp])
    if comp:
        eq_val("2.9", "…and it is vein-mineable, which is what mine-vein requires",
               comp[0].get("vein_mineable"), True)
        check("2.10", "…and it names what the vein will drop",
              isinstance(comp[0].get("mineable_thing"), str),
              "a mineable_thing def name", comp[0].get("mineable_thing"))
        eq_int("2.11", "…and the row count IS the flood fill's size",
               comp[0].get("count"), des)
    S["vein_seed"] = seed
    ge_val("2.12", "the standing MineVein count rose against an INDEPENDENT read",
           (standing(DEF_VEIN) or 0) - (before or 0), 1)


# -------------------------------------- 3: mine over vein — the residual reject --

def phase3():
    banner("PHASE 3 - 855117a item 2b: `mine` on a MineVein cell is a DIFFERENT no")

    seed = S.get("vein_seed")
    if seed is None:
        return soft_skip("3.0", "no vein designation staged", "phase 2 did not run")

    # `Designator_Mine.CanDesignateThing`'s third clause. Before this issue the
    # rejection arrived as `not-designatable` — the same envelope as "this rock
    # is not mineable", which is the OPPOSITE correction.
    e = designate(V_MINE, cells=[seed])
    eq("3.1", "the mine order is refused, as the game refuses it", e, "data.accepted", 0)
    rj = rows(e, "data.rejects")
    check("3.2", "…with a reject row for the cell", len(rj) >= 1 or ARGS.dry_run,
          "one reject", rj)
    if rj:
        eq_val("3.3", "…keyed as already-designated-under-ANOTHER-def, not as "
                      "not-designatable", rj[0].get("why"), WHY_OTHER)
        eq_val("3.4", "…naming the def that is standing there", rj[0].get("designation_present"),
               DEF_VEIN)
        check("3.5", "…and `reason` is still the GAME's own words or null, never a "
                     "phrase of ours dressed as the game's",
              rj[0].get("reason") is None or isinstance(rj[0].get("reason"), str),
              "null or the game's AcceptanceReport string", rj[0].get("reason"))
    eq("3.6", "…and the tally keys on the new word, so a caller can branch without "
              "walking the list", e, "data.rejects_by_reason.%s" % WHY_OTHER, 1)
    check("3.7", "…and NOT on not-designatable, which is the confusion this closes",
          not has_key(e, "data.rejects_by_reason.%s" % WHY_NOT),
          "the not-designatable bucket ABSENT", dig(e, "data.rejects_by_reason"))

    # The control: the SAME verb on a cell that carries its OWN designation is
    # still plain `already-designated`. The two must not have merged.
    rock = S.get("rock") or []
    if len(rock) < 4:
        return soft_skip("3.8", "no spare rock for the control", "phase 0 found %d" % len(rock))
    ctl = rock[3]
    designate(V_MINE, cells=[ctl])
    remember([ctl])
    e = designate(V_MINE, cells=[ctl])
    if rows(e, "data.rejects"):
        eq_val("3.8", "a cell carrying this designator's OWN def is still plain "
                      "already-designated — the two keys did not merge",
               rows(e, "data.rejects")[0].get("why"), WHY_ALREADY)
        absent("3.9", "designate", {"r": rows(e, "data.rejects")[0]}, "r.designation_present",
               "there is no OTHER designation involved, so the key must not appear")


# ----------------------------------- 4: mine-vein over mine — the silent replace --

def phase4():
    banner("PHASE 4 - 855117a item 2c: `mine-vein` REPLACES a Mine designation, "
           "and says so")

    ore = S.get("ore") or []
    if not ore:
        return soft_skip("4.0", "no vein-mineable cell", "phase 0 found none")

    # Clear the vein staged in phase 2 and put a plain Mine on the same cell,
    # so mine-vein has something to overwrite.
    seed = ore[0]
    send("designate", {"type": "cancel", "cells": [cellarg(seed)]})
    e = designate(V_MINE, cells=[seed])
    remember([seed])
    if dig(e, "data.accepted") != 1:
        return soft_skip("4.1", "could not stage a Mine designation on the ore cell",
                         "designate mine returned accepted %s" % show(dig(e, "data.accepted")))

    mine_before = standing(DEF_MINE)
    e = designate(V_VEIN, cells=[seed])
    eq("4.2", "mine-vein accepts a cell that already carries a Mine designation — "
              "its own gate has no such clause", e, "data.accepted", 1)
    rep = rows(e, "data.replaced")
    check("4.3", "…and the envelope NAMES what it removed", len(rep) >= 1 or ARGS.dry_run,
          "a `replaced` row", dig(e, "data.replaced"))
    if rep:
        eq_val("4.4", "…which is the Mine designation", rep[0].get("designation"), DEF_MINE)
        ge_val("4.5", "…and how many cells lost it", rep[0].get("removed"), 1)
        check("4.6", "…with the cells themselves, capped and counted",
              isinstance(rep[0].get("cells"), list) and num(rep[0].get("cells_more")),
              "a cells list plus a cells_more count", rep[0])
        contains("4.7", "…and why", {"r": rep[0]}, "r.why", "removes it where it lands")

    # The independent witness: the Mine count on the map really did fall, which
    # is what a caller reading `designations_now` for Mine would otherwise see
    # happen for no published reason.
    mine_after = standing(DEF_MINE)
    check("4.8", "the map's standing Mine count actually fell",
          num(mine_before) and num(mine_after) and mine_after < mine_before,
          "fewer Mine designations than before (%s)" % show(mine_before), mine_after)


# --------------------------------------------------------- 5: no red errors --

def phase5():
    banner("PHASE 5 - the standing invariant: no red errors")
    e = send("journal", {"since_seq": S.get("watermark", 0), "types": ["red_error"],
                         "limit": 50})
    shape("5.1", "journal", e, "data.count")
    eq("5.2", "no red error was authored during this suite", e, "data.count", 0)
    for ev in rows(e, "data.events")[:5]:
        note("5.3", show(dig(ev, "payload")))


# --------------------------------------------------------------- 6: offline --

def probe(fn):
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


def _short_hash(name):
    """`ShortHashGiver.GiveShortHash`: `(ushort)(GenText.StableStringHash(defName)
    % 65535)`, then +1 per collision until free. StableStringHash is
    `num = num * 31 + c` from 23, in C# int arithmetic — so it WRAPS, and a
    Python port that does not wrap gets a different number for any name past
    about six characters."""
    v = 23
    for ch in name:
        v = v * 31 + ord(ch)
    v = ((v + 2 ** 31) % 2 ** 32) - 2 ** 31          # C# int overflow
    return v % 65535


def _thing_grid(save_path):
    """The map's compressed thing grid: `MapFileCompressor.ExposeData` writes
    `compressedThingMap` through `DataExposeUtility.LookByteArray`, i.e. raw
    deflate + base64 with line breaks. `MapSerializeUtility.SerializeUshort`
    is little-endian, two bytes per cell, in `cellIndices` order (idx = z*sizeX
    + x). Returns a lookup (x, z) -> ushort, or None."""
    data = _read(save_path)
    if not data:
        return None
    m = re.search(r"<compressedThingMapDeflate>(.*?)</compressedThingMapDeflate>",
                  data, re.S)
    if not m:
        return None
    try:
        raw = zlib.decompressobj(-15).decompress(
            base64.b64decode("".join(m.group(1).split())))
    except Exception:                                         # noqa: BLE001
        return None

    def at(x, z):
        i = z * MAP_SIZE_X + x
        if (i * 2 + 1) >= len(raw):
            return None
        return raw[i * 2] | (raw[i * 2 + 1] << 8)
    return at


def phase6():
    banner("PHASE 6 - offline: the run's own artifacts, the game's source, and the helpers")

    # -- the shipped mod ----------------------------------------------------
    comp = _read(os.path.join(REPO, "Source", "AutoRimmer", "DesignateComposition.cs")) or ""
    check("6.1", "DesignateComposition.cs ships", len(comp) > 0, "the file", len(comp))
    check("6.2", "it asks the cell the same question the gate asked — first mineable, "
                 "then edifice, then terrain",
          "GetFirstMineable" in comp and "GetEdifice" in comp and "TerrainAt" in comp,
          "all three accessors", None)
    check("6.3", "…and publishes BOTH yields, because EffectiveMineableYield is not "
                 "mineableYield",
          "mineableYield" in comp and "EffectiveMineableYield" in comp,
          "both yield reads", None)
    m = re.search(r"public const int RowCap = (\d+);", comp)
    check("6.4", "RowCap is readable and matches this suite's constant",
          m is not None and int(m.group(1)) == ROW_CAP, str(ROW_CAP),
          None if m is None else m.group(1))
    eng = _read(os.path.join(REPO, "Source", "AutoRimmer", "DesignateEngine.cs")) or ""
    check("6.5", "the new reject key ships under the name this suite asserts",
          'WhyAlreadyOther = "%s"' % WHY_OTHER in eng,
          'WhyAlreadyOther = "%s"' % WHY_OTHER, None)
    verbs = _read(os.path.join(REPO, "Source", "AutoRimmer", "DesignationVerbs.cs")) or ""
    check("6.6", "…and `mine` is the entry that declares MineVein as a blocker",
          'Blocks = new[] { DesignationDefOf.MineVein }' in verbs,
          "the Blocks declaration on the mine entry", None)
    check("6.7", "…while `mine-vein` declares Mine as something it REPLACES",
          'Replaces = new[] { DesignationDefOf.Mine }' in verbs,
          "the Replaces declaration on the mine-vein entry", None)

    # -- the decompiled game ------------------------------------------------
    decomp = _first(DECOMP_CANDIDATES)
    if decomp is None:
        note("6.8", "no decompiled RimWorldBase found (set RIMWORLD_DECOMP) — the four "
                    "source-derived checks below were NOT run, which is not the same as "
                    "passing")
    else:
        mine = _read(os.path.join(decomp, "RimWorld", "Designator_Mine.cs")) or ""
        flat = re.sub(r"\s+", " ", mine)
        check("6.8", "Designator_Mine.CanDesignateThing still rejects on a MineVein "
                     "designation — the clause phase 3 is about",
              "DesignationAt(t.Position, DesignationDefOf.MineVein) != null" in flat,
              "the third clause", None)
        check("6.9", "…and DesignateSingleCell still removes SmoothWall where it lands",
              "TryRemoveDesignation(loc, DesignationDefOf.SmoothWall)" in flat,
              "the SmoothWall removal", None)
        vein = _read(os.path.join(decomp, "RimWorld", "Designator_MineVein.cs")) or ""
        vflat = re.sub(r"\s+", " ", vein)
        check("6.10", "Designator_MineVein still flood-fills at DESIGNATE time",
              "FloodFillDesignations" in vflat and "floodFiller.FloodFill" in vflat,
              "the flood fill", None)
        check("6.11", "…removing any Mine designation on the cells it paints, which is "
                      "what phase 4 asserts is now REPORTED",
              "TryRemoveDesignation(c, DesignationDefOf.Mine)" in vflat,
              "the Mine removal inside the fill", None)
        check("6.12", "…and it takes ORE only (veinMineable), which is why phase 0's "
                      "vein search doubles as the ore locator",
              "veinMineable" in vflat, "the veinMineable gate", None)
        check("6.13", "HAZARD, recorded: CanDesignateCell returns TRUE for a fogged cell "
                      "while DesignateSingleCell dereferences GetEdifice(...).def with no "
                      "null check — unreachable through `designate` ONLY because "
                      "DesignateEngine gates fog first",
              "c.Fogged(base.Map)) { return true; }" in re.sub(r"\s+", " ", vein)
              or "if (c.Fogged(base.Map))" in vein,
              "the fogged branch", None)
        check("6.14", "…and DesignateEngine really does gate fog before the designator",
              "c.Fogged(map)" in eng and "WhyFogged" in eng, "the fog gate", None)
        cancel = _read(os.path.join(decomp, "RimWorld", "Designator_Cancel.cs")) or ""
        check("6.15", "Designator_Cancel still removes vein designations CONTIGUOUSLY, "
                      "which is what makes this suite's teardown able to undo a flood fill",
              "RemoveContiguousDesignations" in cancel, "the contiguous removal", None)

    # -- the run's own artifacts, which is where the issue's diagnosis is tested --
    jdir = RUN_JOURNAL
    jfiles = sorted(os.listdir(jdir)) if os.path.isdir(jdir) else []
    # The FIRST such call, by seq. The run made more than one 60-cell mine
    # designate over that face and the later ones accepted 0 (every cell was
    # already designated), so taking the last match asserts the wrong row —
    # which is exactly what a first draft of this check did.
    call = None
    for f in jfiles:
        for line in (_read(os.path.join(jdir, f)) or "").splitlines():
            if '"verb":"designate"' not in line or '"step":"mine"' not in line:
                continue
            try:
                row = json.loads(line)
            except ValueError:
                continue
            if dig(row, "payload.counts.targeted") != RUN_TARGETED:
                continue
            if call is None or (row.get("seq") or 0) < (call.get("seq") or 0):
                call = row
    if call is None:
        note("6.16", "RUNS/m1-20260901/journal not present — the artifact checks were "
                     "NOT run")
    else:
        eq_int("6.16", "the run's own journal confirms `accepted 14 of 60` exactly",
               dig(call, "payload.counts.accepted"), RUN_ACCEPTED)
        eq_int("6.17", "…of 60 targeted", dig(call, "payload.counts.targeted"), RUN_TARGETED)
        got = [tuple(c) for c in as_list(dig(call, "payload.cells"))]
        check("6.18", "…and the fourteen cells are the ones this suite reasons about",
              sorted(got) == sorted(RUN_ACCEPTED_CELLS),
              "the recorded cell list", got[:4])

    save = os.path.join(RUN_SAVES, "day17-tick1020680-autosave.rws")
    at = _thing_grid(save) if os.path.exists(save) else None
    if at is None:
        note("6.19", "RUNS/m1-20260901/saves/day17-tick1020680-autosave.rws not present "
                     "(or its thing grid could not be decoded) — the composition checks "
                     "against the run's own map were NOT run")
    else:
        steel = _short_hash("MineableSteel")
        sand = _short_hash("Sandstone")
        marble = _short_hash("Marble")
        x0, z0, w, h = RUN_RECT
        vals = [at(x, z) for z in range(z0, z0 + h) for x in range(x0, x0 + w)]
        n_steel = sum(1 for v in vals if v in (steel, steel + 1))
        n_sand = sum(1 for v in vals if v in (sand, sand + 1))
        n_marble = sum(1 for v in vals if v in (marble, marble + 1))
        # THE ISSUE'S OWN DIAGNOSIS, TESTED. It says the face is "mostly
        # sandstone and marble". At day 17 the rect's still-unmined cells are
        # steel and marble, and there is NO sandstone rock in it at all.
        eq_int("6.19", "the rect holds NO sandstone rock — the issue's 'mostly sandstone' "
                       "is not supported by the run's own save", n_sand, 0)
        ge_val("6.20", "…while MineableSteel cells were still standing inside it twenty "
                       "in-game days later: the rect UNDER-covered the ore body, which is "
                       "the real aiming failure", n_steel, 1)
        ge_val("6.21", "…alongside marble, so the face genuinely was mixed", n_marble, 1)
        finding("6.22", "855117a's headline count (14 of 60) is exact; its headline "
                        "DIAGNOSIS ('mostly sandstone and marble') is contradicted by "
                        "day17-tick1020680-autosave.rws — %d steel, %d marble, %d "
                        "sandstone in the rect. The fix stands either way: nobody could "
                        "tell WHICH from `accepted: 14`."
                        % (n_steel, n_marble, n_sand))
        hit = [c for c in RUN_STEEL_CELLS if c in RUN_ACCEPTED_CELLS]
        eq_int("6.23", "and all four cells `nearest {def:'MineableSteel'}` had named were "
                       "among the fourteen accepted", len(hit), len(RUN_STEEL_CELLS))

    # -- the helpers themselves --------------------------------------------
    good = {"data": {"composition": [{"def": "MineableSteel", "count": 4,
                                      "mineable_thing": "Steel"}]}}
    plain = {"data": {"composition": [{"def": "Sandstone", "count": 8}]}}
    check("6.24", "shape() passes on a present key",
          probe(lambda: shape("x", "v", good, "data.composition.0.mineable_thing")),
          "pass", None)
    check("6.25", "shape() FAILS on an absent key",
          not probe(lambda: shape("x", "v", plain, "data.composition.0.mineable_thing")),
          "fail", None)
    check("6.26", "eq(..., None) would have PASSED on that absent key, which is why the "
                  "plain-rock branch uses absent()",
          probe(lambda: eq("x", "w", plain, "data.composition.0.mineable_thing", None)),
          "pass (and that is the hazard)", None)
    check("6.27", "absent() passes on the missing key and FAILS on the present one",
          probe(lambda: absent("x", "v", plain, "data.composition.0.mineable_thing", "w"))
          and not probe(lambda: absent("x", "v", good, "data.composition.0.mineable_thing",
                                       "w")),
          "pass then fail", None)
    check("6.28", "eq_int() FAILS when either side is not an integer — the form phase 1's "
                  "partition check needs",
          not probe(lambda: eq_int("x", "w", None, 3))
          and not probe(lambda: eq_int("x", "w", 3, None)),
          "fail on both", None)
    check("6.29", "the shortHash port wraps like C# int arithmetic — an unwrapped port "
                  "gets a different number for any name past six characters",
          _short_hash("MineableSteel") == 27292 and _short_hash("Sandstone") == 322,
          "MineableSteel 27292, Sandstone 322 (both +1 in-game after a collision bump)",
          (_short_hash("MineableSteel"), _short_hash("Sandstone")))


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

    print("AutoRimmer acceptance - a mine designation that can be aimed (855117a)")

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
    if FINDINGS:
        print("%sFINDINGS (reported, not asserted):%s" % (CYAN, OFF))
        for n, t in FINDINGS:
            print("  %s%-7s %s%s" % (CYAN, n, t, OFF))
        print("")
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
# 1. `DesignateComposition.Build` rolls up `DesignateEngine.Landed` — the
#    designation DELTA for a cell-targeted def, the accepted things otherwise —
#    by asking each cell the same question its gate asked: first mineable, else
#    edifice, else terrain, with `by` naming which answered. NOT by `accepted`,
#    which for mine-vein is one cell of a forty-cell job.
#
# 2. Ore rows carry `mineable_thing`, `mineable_yield` (the def's) and
#    `yield_effective` (`BuildingProperties.EffectiveMineableYield`, i.e. times
#    `Find.Storyteller.difficulty.mineYieldFactor`). Both, because the raw one
#    is what a wiki says and the effective one is what the colony gets. On a def
#    with no `mineableThing` the keys are ABSENT, not null — see phase 1's
#    absent() branch for why that distinction is load-bearing.
#
# 3. `DesignateEngine.WhyAlreadyOther` re-keys a rejection the game's gate had
#    already made, on the same reject path `WhyAlready` uses, and never touches
#    the accept path. The clause it names is quoted in the constant's comment
#    from `Designator_Mine.CanDesignateThing`. `designation_present` carries the
#    def NAME rather than a sentence, because `reason` is the game's own
#    AcceptanceReport string verbatim or null and this file's REJECTIONS
#    contract forbids inventing words the game did not say.
#
# 4. `DesignateComposition.Replaced` diffs the cell snapshot of every def in the
#    entry's `Replaces` array. `mine-vein` replaces `Mine`
#    (`FloodFillDesignations` -> `TryRemoveDesignation(c, Mine)`); `mine`
#    replaces `SmoothWall` (`DesignateSingleCell`). Both are the game's, both
#    were silent, both are now published.
#
# 5. `8b0b88f`'s trailing note item 7 recorded the mine/mine-vein reject as a
#    RESIDUAL and said "if a future round takes that on, this file is where the
#    check belongs". It was taken on here instead, because the fix needed the
#    table's new `Blocks` array and belongs with the rest of 855117a. That
#    file's item 7 now points here.
#
# 6. NOT DEMONSTRATED by this suite: a flood fill wider than one cell, unless
#    the bench colony happens to have a multi-cell exposed vein. Phase 2.7
#    reports that as a FINDING rather than passing quietly, because "designated
#    == accepted == 1" is exactly what a broken flood fill would also look like.
# ============================================================================
