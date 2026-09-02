#!/usr/bin/env python3
"""Acceptance runner for f9dadc7 (B-1) — a blueprint that never completes was
invisible, because a scalar tells you the size of a set and never its age.

    ./accept/f9dadc7-stalled-build.py            # everything (this is the one command)
    ./accept/f9dadc7-stalled-build.py --quick    # skip phase 5's 30k-tick tail
    ./accept/f9dadc7-stalled-build.py --phase 3  # one phase (0 always runs)
    ./accept/f9dadc7-stalled-build.py --dry-run  # print the plan, send nothing
    ./accept/f9dadc7-stalled-build.py --selftest # phase 9 only: NO bench needed

Read `accept/1adc737-place-layout.py`'s header for the protocol and the exit
codes, and `accept/eef837a-bill-filter.py`'s for the shape-contract rule this
file obeys throughout: **`eq(..., None)` passes on an ABSENT key**, and the
whole of f9dadc7 is about a reading that must be able to say "I do not know".
Every assertion about a null here is preceded by a `shape()`.

IT COSTS ABOUT 2.8 IN-GAME DAYS OF WALL CLOCK — ~165,000 ticks, most of it in
phase 3, and that is not avoidable: the acceptance bullet IS "leave a blueprint
short of materials across two day boundaries". The threshold is compiled into
the mod (`ConstructionWatch.StallTicks`, 120,000) and no flag can lower it.
`--quick` drops phase 5's 30,000-tick tail and nothing else.

WHAT THE ISSUE SAID, AND WHAT THE ARTIFACTS SAY. Both were checked before this
suite was written, and the artifacts are the stronger version of the story:

  * The issue says `awaiting_materials` "sat at exactly 22 for fifteen straight
    in-game days". Measured over all 55 `RUNS/m1-20260901/digests/day-*.json`,
    it was **22 for TWENTY consecutive days — 38 through 57 inclusive**, with
    no gap. Phase 9 re-derives that from the files.
  * That window carries **no `more`, no `cap` and no `cap_note`**, with 22-25
    elements against a 60-item scan cap, so all twenty readings were a true
    CENSUS and not a floor. The number was right. The number was the problem.
  * The same directory shows a flat 60 across days 13-18 and a flat 24 across
    21-24 and again 31-36. This is the shape of the whole run, not one incident.
  * `digests/day-1.json` is a mislabelled day-62-era snapshot (tick 3719149,
    one colonist) and days 2-9, 28, 32 and 35 are absent, so 12 of 66
    day-boundary digests are effectively missing. Phase 9 asserts that too, so
    nobody quotes that directory as complete again.

WHAT IS BEING TESTED, phase by phase:

  * PHASE 1 — the SHAPE, item 1. Two starved `Wall` blueprints are placed and
    `construction {id}` must publish all six age keys — `state_since_tick`,
    `state_age_ticks`, `state_age_days`, `age_basis`, `stalled`,
    `tracked_since_tick` — with `stalled` NOT true. `digest.construction` must
    publish `stalled` (a LIST), `stalled_count`, `stall_after_ticks`,
    `tracked_since_tick`, `no_builder` and `skill_blocked`. Presence asserted
    before value, every time.
  * PHASE 2 — the THIRD STATE, and it is the whole point. After one sampler
    cadence the element is tracked with `age_basis: "since-first-seen"` and
    `stalled: null` — *not known yet*, which must never read as "clean". The
    `stalled_note` on the section is asserted CONDITIONALLY against
    `tracked_since_tick`, so the assertion holds on a fresh bench and on one
    that has been up for a week.
  * PHASE 3 — THE ACCEPTANCE BULLET. Two in-game days pass with the blueprints
    still short. `digest.construction.stalled[]` must carry a row NAMING the
    def and the missing material, with `state_age_days >= 2`, and the element's
    own `stalled` must be `true`.
  * PHASE 4 — the tri-state proved by STAGING, not by a constant. One blueprint
    is blocked deliberately; its state changes, so its clock resets and it must
    report `stalled: false` with `age_basis: "observed-transition"`. The three
    legs are then asserted as INEQUALITIES against each other, which is the only
    form a constant cannot satisfy.
  * PHASE 5 — the second half of the bullet: supply the material, run time, and
    the element leaves `stalled[]`.
  * PHASE 6 — the standing invariant: no red errors.
  * PHASE 9 — offline. THE EVIDENCE, not a mock: the run's own digests
    re-derived, the shipped `ConstructionWatch` constants read out of the source
    (the cadence must be BELOW the threshold, which is the correctness argument
    for the whole sampler), and helper self-checks over deliberately broken
    envelopes.

WHAT THIS SUITE DOES NOT PROVE, in those words:

  * **That stall tracking survives a save/load.** It does not, by design — the
    resolution on the issue forbids buying durability with new scribed state
    (git-bug d16a463). What is asserted instead is that a reload is VISIBLE:
    `tracked_since_tick` moves and `stalled` answers `null` rather than `false`.
    Nothing here reloads the bench; that is the orchestrator's to run by hand if
    they want it.
  * **That the age is exact across a state change nobody watched.** Between two
    samples 2,500 ticks apart the mod cannot see a flicker, and
    `age_basis: "since-first-seen"` is the honest label for the resulting floor.
  * **Anything about a multi-map colony.** `Stalled()` filters by map id and
    that filter is not exercised here.

IT WRECKS THE COLONY IT RUNS ON: it leaves two unbuildable wall blueprints, may
leave a spawned blocker, and runs nearly three in-game days.

Exit 0 = every check passed · 1 = at least one FAIL · 2 = a fixture
precondition could not be met, which is NOT a spec failure and says so.
"""

import argparse
import glob
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
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

GREEN, RED, YELLOW, CYAN, DIM, OFF = (
    "\033[32m", "\033[31m", "\033[33m", "\033[36m", "\033[2m", "\033[0m",
)
if not sys.stdout.isatty():
    GREEN = RED = YELLOW = CYAN = DIM = OFF = ""

ARGS = None
FAILS = []
FINDINGS = []
CHECKS = 0
S = {}
SEQ = 0
CAPTURE = None

OPS = ["construction", "digest", "build", "things", "find-rect", "advance",
       "journal", "status", "pause", "unpause", "dev:spawn-thing", "dev:destroy"]

# The fixture def. `Wall` is stuff-based, 1x1, needs no research and has
# `constructionSkillPrerequisite 0`, so nothing about e08c3e5's skill branch can
# contaminate a MATERIAL stall.
FIXTURE_DEF = "Wall"
# Stuffs tried in order for one the colony has NONE of. A blueprint of an
# absent material starves deterministically and disturbs nothing else on the
# map — strictly better than forbidding the colony's own steel, which is a
# second mutation with its own failure modes.
STUFF_CANDIDATES = ["Plasteel", "Gold", "Silver", "Jade"]
# What the mod compiles in. Phase 9 re-reads both out of the shipped source, so
# a change there fails here rather than silently loosening the suite.
STALL_TICKS = 120000
CADENCE = 2500
# The six age keys every unresolved element must publish. Asserted as a set: a
# field appearing here is either deliberate or leaking, and both want looking at.
AGE_KEYS = ["state_since_tick", "state_age_ticks", "state_age_days",
            "age_basis", "stalled", "tracked_since_tick"]
AGE_BASIS_VALUES = ["observed-transition", "since-first-seen", "not-tracked"]
# What the digest's construction section owes after f9dadc7 + e08c3e5.
SECTION_KEYS = ["blueprints", "frames", "awaiting_materials", "ready", "in_progress",
                "blocked", "no_builder", "skill_blocked", "work_left",
                "stalled", "stalled_count", "stall_after_ticks", "tracked_since_tick"]

RUN_DIGESTS = os.path.join(REPO, "RUNS", "m1-20260901", "digests")
# Measured, not inferred — see this file's header.
FLAT_VALUE = 22
FLAT_FIRST, FLAT_LAST = 38, 57
MISSING_DAYS = [28, 32, 35]

WATCH_SRC = os.path.join(REPO, "Source", "AutoRimmer", "ConstructionWatch.cs")
VERBS_SRC = os.path.join(REPO, "Source", "AutoRimmer", "ConstructionVerbs.cs")


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=240):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "accf9dadc7-%03d-%s" % (SEQ, slug)
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
                    print("    <- %s" % json.dumps(env, separators=(",", ":"))[:1600])
                return env
            except (ValueError, OSError):
                time.sleep(0.12)
                continue
        time.sleep(0.2)
    return {"ok": False, "op": op,
            "error": {"code": "acc-timeout",
                      "detail": "no results/%s.json within %ss" % (cid, timeout)}}


# `722c951` makes `advance` refuse while the journal carries an unread delta and
# halt on an own-faction downing. Every verb this suite sends journals, and it
# runs nearly three in-game days on a live colony, so both escapes are injected
# in ONE place with a reason naming this file.
ESCAPE = ("accept/f9dadc7-stalled-build.py: this suite runs ~2.8 in-game days "
          "past a deliberately starved blueprint and journals on every verb")


def advance(ticks, timeout=1800):
    return send("advance", {"ticks": ticks, "unread_ok": ESCAPE,
                            "through_casualties": ESCAPE}, timeout=timeout)


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
    """dig() cannot tell `absent` from `present and null`, and f9dadc7 is
    exactly that distinction: `stalled: null` MEANS "tracking cannot answer
    yet", while an absent `stalled` would mean the mod never published it. A
    reader that cannot tell them apart is back where the issue started."""
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


def show(v):
    return "null" if v is None else json.dumps(v, separators=(",", ":"))[:500]


def num(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


# ------------------------------------------------------------------- asserts --

def check(n, what, ok, expected, actual):
    global CHECKS
    CHECKS += 1
    if CAPTURE is not None:
        CAPTURE.append(ok)
        if not ok:
            FAILS.append(n)
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


def ne_val(n, what, a, b):
    check(n, what, a != b, "anything but %s" % show(b), a)


def ge(n, what, env, path, want):
    got = dig(env, path)
    ok = num(got) and got >= want
    check(n, "%s (%s)" % (what, path), ok, ">= %s" % want, got)


def ge_val(n, what, got, want):
    check(n, what, num(got) and got >= want, ">= %s" % want, got)


def contains(n, what, env, path, needle):
    got = dig(env, path)
    ok = isinstance(got, str) and needle.lower() in got.lower()
    check(n, "%s (%s)" % (what, path), ok, "a string containing %r" % needle, got)


def one_of(n, what, env, path, allowed):
    got = dig(env, path)
    check(n, "%s (%s)" % (what, path), got in allowed, "one of %s" % (allowed,), got)


def shape(n, verb, env, path, kind=None):
    ok = has_key(env, path)
    want = "the key to be present"
    if ok and kind is not None:
        ok = isinstance(dig(env, path), kind)
        want += " and a %s" % kind.__name__
    check(n, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def keys_include(n, what, env, path, want):
    got = dig(env, path)
    if not isinstance(got, dict):
        check(n, "%s (%s)" % (what, path), False, "a dict at that path", got)
        return
    missing = sorted(set(want) - set(got))
    check(n, "%s (%s)" % (what, path), not missing,
          "every one of %s" % (sorted(want),), {"missing": missing})


def note(n, text):
    if CAPTURE is not None:
        return
    print("  %s%-7s NOTE    %s%s" % (YELLOW, n, text, OFF))


def finding(n, text):
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
    print("          This is a FIXTURE gap, not a failure of the spec.")
    sys.exit(2)


def banner(t):
    if CAPTURE is not None:
        return
    print("")
    print("%s== %s %s%s" % (CYAN, t, "=" * max(0, 74 - len(t)), OFF))


# ------------------------------------------------------------------- fixture --

def now_tick():
    return dig(send("digest", {"sections": ["time"]}), "data.time.tick", 0) or 0


def red_errors(since=0):
    e = send("journal", {"types": ["error"], "since": since, "limit": 100})
    return as_list(dig(e, "data.entries"))


def watermark():
    return dig(send("journal", {"limit": 1}), "data.seq", 0) or 0


def count_of(def_name):
    e = send("things", {"def": def_name, "detail": False, "by_location": False})
    return dig(e, "data.totals.count", 0) or 0


def free_spot(w=1, h=1):
    e = send("find-rect", {"w": w, "h": h, "max": 6})
    out = []
    for c in as_list(dig(e, "data.candidates")):
        if isinstance(c, dict) and isinstance(c.get("center"), list):
            out.append(c["center"])
    return out


def element(thing_id):
    """The element row through `construction {id}`, which is the READ. Never the
    placing verb's own echo — a claim about state is never read out of the
    envelope that claims to have caused it."""
    return send("construction", {"id": thing_id})


def section():
    return send("digest", {"sections": ["construction", "time"]})


def stalled_rows(env):
    return [r for r in as_list(dig(env, "data.construction.stalled"))
            if isinstance(r, dict)]


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 - fixture: two blueprints of a material the colony has none of")

    e = send("status", {})
    ops = [str(o) for o in as_list(dig(e, "data.verbs"))]
    missing = [o for o in OPS if o not in ops]
    check("0.1", "every verb this suite drives is registered",
          not missing, "no missing ops", {"missing": missing})
    precondition("0.2", "the bench answers `status`", dig(e, "ok") is True or ARGS.dry_run,
                 "status returned %s" % show(dig(e, "error")))

    send("pause", {})
    S["watermark"] = watermark()

    # A stuff the colony has NONE of. Starving a blueprint this way disturbs
    # nothing else on the map, which matters because a forbid-the-steel fixture
    # is a second mutation with its own failure modes and would also change the
    # `awaiting-materials` reading of every OTHER blueprint on the map.
    S["stuff"] = None
    for cand in STUFF_CANDIDATES:
        if ARGS.dry_run:
            S["stuff"] = cand
            break
        if count_of(cand) == 0:
            S["stuff"] = cand
            break
    precondition("0.3", "a stuff the colony has none of",
                 S["stuff"] is not None,
                 "the colony holds every one of %s, so no blueprint of them starves"
                 % STUFF_CANDIDATES)

    spots = free_spot()
    precondition("0.4", "two free 1x1 cells", len(spots) >= 2 or ARGS.dry_run,
                 "`find-rect` returned %d candidate(s)" % len(spots))

    S["ids"] = []
    S["cells"] = []
    for i, pos in enumerate((spots or [[0, 0], [0, 0]])[:2]):
        b = send("build", {"def": FIXTURE_DEF, "pos": pos, "stuff": S["stuff"]})
        tid = dig(b, "data.thing.id")
        if tid is None:
            continue
        S["ids"].append(tid)
        S["cells"].append(pos)
    precondition("0.5", "two blueprints placed", len(S["ids"]) == 2 or ARGS.dry_run,
                 "`build` produced %d blueprint(s): %s"
                 % (len(S["ids"]), show(S.get("ids"))))
    if ARGS.dry_run and not S["ids"]:
        S["ids"] = [1, 2]
        S["cells"] = [[0, 0], [0, 0]]

    S["t0"] = now_tick()
    print("  %sfixture: %s of %s at %s, ids %s, tick %s%s"
          % (DIM, FIXTURE_DEF, S["stuff"], S["cells"], S["ids"], S["t0"], OFF))


# ------------------------------------------------------------------- phase 1 --

def phase1():
    banner("PHASE 1 - the shape: six age keys, and `stalled` is not true yet")

    a = element(S["ids"][0])
    for i, k in enumerate(AGE_KEYS):
        shape("1.%d" % (i + 1), "construction {id}", a, "data.item." + k)
    keys_include("1.7", "the element row carries the whole age block",
                 a, "data.item", AGE_KEYS)
    one_of("1.8", "age_basis is one of the three tokens and nothing else",
           a, "data.item.age_basis", AGE_BASIS_VALUES)
    # NOT `eq(..., None)` — that passes on an absent key, which is the hazard
    # this whole file is about. The claim is "not true", asserted as a value.
    ne_val("1.9", "a blueprint placed seconds ago is NOT reported stalled",
           dig(a, "data.item.stalled"), True)
    eq("1.10", "…and it is the material branch, not the skill one",
       a, "data.item.state", "awaiting-materials")
    contains("1.11", "…and `why` names the missing material",
             a, "data.item.why", S["stuff"])

    d = section()
    keys_include("1.12", "digest.construction carries the whole rollup",
                 d, "data.construction", SECTION_KEYS)
    shape("1.13", "digest", d, "data.construction.stalled", list)
    shape("1.14", "digest", d, "data.construction.stalled_count", int)
    eq("1.15", "the compiled threshold is two in-game days",
       d, "data.construction.stall_after_ticks", STALL_TICKS)
    ge("1.16", "the two fixture blueprints are counted",
       d, "data.construction.awaiting_materials", 2)


# ------------------------------------------------------------------- phase 2 --

def phase2():
    banner("PHASE 2 - the THIRD state: tracked, and still not able to answer")

    # One sampler cadence plus slack, so ConstructionWatch has certainly seen
    # these elements at least once.
    advance(CADENCE * 2, timeout=600)
    a = element(S["ids"][0])
    eq("2.1", "the element is now tracked, and its age is a FLOOR",
       a, "data.item.age_basis", "since-first-seen")
    ge("2.2", "…with a real since-tick", a, "data.item.state_since_tick", 1)
    shape("2.3", "construction {id}", a, "data.item.stalled")
    eq_val("2.4", "`stalled` is NULL — tracking has not covered two days, which "
                  "is a different answer from `false` and must never read as clean",
           dig(a, "data.item.stalled"), None)
    S["leg_unknown"] = dig(a, "data.item.stalled")

    # The note is CONDITIONAL on the SESSION's tracking age, not the element's,
    # so it is asserted both ways rather than assumed. A bench that has been up
    # for days will not carry it; a freshly loaded one must.
    d = section()
    tracked = dig(d, "data.construction.tracked_since_tick")
    now = dig(d, "data.time.tick", 0) or 0
    young = num(tracked) and (now - tracked) < STALL_TICKS
    check("2.5", "`stalled_note` is present exactly while tracking is younger "
                 "than the threshold (presence is the signal)",
          has_key(d, "data.construction.stalled_note") == young,
          "note present == %s (tracked_since %s, now %s)" % (young, tracked, now),
          has_key(d, "data.construction.stalled_note"))
    if young:
        contains("2.6", "…and it says the list is a FLOOR, not a clean bill",
                 d, "data.construction.stalled_note", "floor")
    else:
        note("2.6", "this bench has been tracking for over %d ticks, so the "
                    "too-young note is correctly absent" % STALL_TICKS)


# ------------------------------------------------------------------- phase 3 --

def phase3():
    banner("PHASE 3 - THE BULLET: two day boundaries short of a material")

    before = now_tick()
    a = advance(STALL_TICKS + CADENCE * 2, timeout=2400)
    ran = dig(a, "data.ticks_elapsed", 0) or 0
    precondition("3.0", "two in-game days actually ran",
                 ran >= STALL_TICKS or ARGS.dry_run,
                 "the advance returned after %d ticks (%s) — a halt, not a "
                 "timeout, means the colony interrupted the fixture"
                 % (ran, dig(a, "data.reason")))

    e = element(S["ids"][0])
    eq_val("3.1", "the element reports STALLED — true, not null, not false",
           dig(e, "data.item.stalled"), True)
    ge("3.2", "…for at least two in-game days",
       e, "data.item.state_age_days", 2.0)
    ge("3.3", "…measured in ticks against the compiled threshold",
       e, "data.item.state_age_ticks", STALL_TICKS)
    S["leg_stalled"] = dig(e, "data.item.stalled")

    d = section()
    ge("3.4", "the digest counts it", d, "data.construction.stalled_count", 2)
    rows = stalled_rows(d)
    check("3.5", "…and publishes ROWS, because a count alone repeats the defect",
          len(rows) >= 2, ">= 2 rows in stalled[]", len(rows))

    mine = [r for r in rows if r.get("def") == FIXTURE_DEF]
    check("3.6", "a row NAMES the def", len(mine) >= 1,
          "a stalled row with def %r" % FIXTURE_DEF, [r.get("def") for r in rows][:6])
    if mine:
        row = mine[0]
        check("3.7", "…and NAMES the missing material in `why`",
              isinstance(row.get("why"), str)
              and S["stuff"].lower() in row["why"].lower(),
              "a why containing %r" % S["stuff"], row.get("why"))
        check("3.8", "…and the cell, so the agent can go and look",
              isinstance(row.get("at"), list) and len(row["at"]) == 2,
              "at [x, z]", row.get("at"))
        check("3.9", "…and the state, so the triage branches without a second read",
              row.get("state") == "awaiting-materials",
              "awaiting-materials", row.get("state"))
        check("3.10", "…and the age, oldest first",
              num(row.get("state_age_days")) and row["state_age_days"] >= 2.0,
              ">= 2.0 days", row.get("state_age_days"))
        check("3.11", "…and which KIND of age it is",
              row.get("age_basis") in AGE_BASIS_VALUES,
              "one of %s" % AGE_BASIS_VALUES, row.get("age_basis"))
    check("3.12", "the too-young note is GONE now that tracking has covered the "
                  "threshold — presence is the signal, both ways",
          not has_key(d, "data.construction.stalled_note"),
          "no stalled_note", dig(d, "data.construction.stalled_note"))


# ------------------------------------------------------------------- phase 4 --

def phase4():
    banner("PHASE 4 - the tri-state proved by staging, not by a constant")

    # Block the SECOND blueprint deterministically. A state change is a state
    # change however it is caused, and this one needs no colonist to cooperate,
    # which is what makes it an assertion rather than a hope.
    cell = S["cells"][1]
    sp = send("dev:spawn-thing", {"def": "Steel", "count": 30, "pos": cell,
                                  "mode": "direct"})
    S["blocker"] = dig(sp, "data.spawned.0.id")
    if S["blocker"] is None and not ARGS.dry_run:
        note("4.0", "dev:spawn-thing put nothing on the cell; the state change "
                    "below may not fire and 4.1 will say so")
    advance(CADENCE * 2, timeout=600)

    b = element(S["ids"][1])
    ne_val("4.1", "the blocked element's state CHANGED away from awaiting-materials",
           dig(b, "data.item.state"), "awaiting-materials")
    eq("4.2", "…so the transition was WATCHED and the age is now exact",
       b, "data.item.age_basis", "observed-transition")
    eq_val("4.3", "…and it reports NOT stalled — false, which is a different "
                  "answer from null",
           dig(b, "data.item.stalled"), False)
    S["leg_fresh"] = dig(b, "data.item.stalled")

    # THE THREE LEGS, as inequalities. A surface where all three collapse to one
    # value satisfies every individual assertion above and none of these.
    ne_val("4.4", "stalled(true) and stalled(false) are different answers",
           S.get("leg_stalled"), S.get("leg_fresh"))
    ne_val("4.5", "stalled(false) and stalled(null) are different answers — this "
                  "is the pair the original defect collapsed",
           S.get("leg_fresh"), S.get("leg_unknown"))
    ne_val("4.6", "stalled(true) and stalled(null) are different answers",
           S.get("leg_stalled"), S.get("leg_unknown"))

    d = section()
    ge("4.7", "the blocked element is still counted somewhere in the section",
       d, "data.construction.blueprints", 1)


# ------------------------------------------------------------------- phase 5 --

def phase5():
    banner("PHASE 5 - supply the material and it leaves stalled[]")

    before = section()
    was = dig(before, "data.construction.stalled_count", 0) or 0
    send("dev:spawn-thing", {"def": S["stuff"], "count": 60,
                             "pos": S["cells"][0], "mode": "near"})
    send("unforbid", {"rect": [max(0, S["cells"][0][0] - 4),
                               max(0, S["cells"][0][1] - 4), 9, 9]})
    a = advance(30000, timeout=1800)
    ran = dig(a, "data.ticks_elapsed", 0) or 0

    e = element(S["ids"][0])
    live = has_key(e, "data.item.state")
    if not live:
        # The blueprint became a building: the strongest possible "it left".
        check("5.1", "the element is gone from the live set — it was BUILT once "
                     "the material arrived",
              dig(e, "ok") is False or dig(e, "data.item") is None,
              "no live blueprint at that id", show(dig(e, "data")))
        return
    ne_val("5.1", "the element is no longer reported stalled",
           dig(e, "data.item.stalled"), True)
    after = section()
    check("5.2", "…and the digest's stalled count fell",
          (dig(after, "data.construction.stalled_count", 0) or 0) < was
          or dig(e, "data.item.state") != "awaiting-materials",
          "stalled_count below %d, or the element's state changed" % was,
          {"count": dig(after, "data.construction.stalled_count"),
           "state": dig(e, "data.item.state"), "ticks": ran})


# ------------------------------------------------------------------- phase 6 --

def phase6():
    banner("PHASE 6 - the standing invariant: no red errors")
    rows = red_errors(S.get("watermark", 0))
    check("6.1", "no red error was authored during this suite",
          len(rows) == 0, "an empty error journal since the watermark",
          [dig(r, "payload.message") for r in rows][:5])


# ------------------------------------------------------------------- phase 9 --

def probe(fn):
    """Run one assertion body with checks captured instead of printed, and
    return whether every check inside it passed. Phase 9 uses it to assert that
    a BROKEN fixture FAILS — a helper that cannot fail proves nothing."""
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


def phase9():
    banner("PHASE 9 - offline: the run's own digests and the shipped constants")

    # -- 9.1 the source, because the suite's numbers are claims about it -------
    src = _read(WATCH_SRC)
    if src is None:
        note("9.1", "%s not readable — the constant checks are SKIPPED, and a "
                    "check that cannot run is not a check that passed" % WATCH_SRC)
    else:
        # Parsed, never eval'd: the constant is written `2 * 60000` and the two
        # shapes this accepts are a bare integer and one multiplication. A
        # source file is still an input, and an input is never executed.
        m = re.search(r"public const int StallTicks\s*=\s*(\d+)\s*(?:\*\s*(\d+))?\s*;", src)
        val = None
        if m:
            val = int(m.group(1)) * (int(m.group(2)) if m.group(2) else 1)
        eq_val("9.1", "ConstructionWatch.StallTicks is two in-game days (60000 x 2)",
               val, STALL_TICKS)
        m2 = re.search(r"public const int Cadence\s*=\s*(\d+);", src)
        cad = int(m2.group(1)) if m2 else None
        eq_val("9.2", "…and the sampler's cadence is %d" % CADENCE, cad, CADENCE)
        check("9.3", "THE CORRECTNESS ARGUMENT: the cadence is far below the "
                     "threshold, so a two-day stall cannot be missed between two "
                     "samples",
              num(cad) and num(val) and cad * 8 <= val,
              "cadence * 8 <= threshold", {"cadence": cad, "threshold": val})
        # COMMENTS STRIPPED FIRST. This file's own header says "deliberately NO
        # GetStatValueAbstract", so a naive grep finds the word in the prose that
        # promises not to call it and reports a defect that is the opposite of
        # the truth. A claim about code is checked against code.
        code = "\n".join(re.sub(r"//.*$", "", ln) for ln in src.splitlines())
        check("9.4", "the sampler makes no GetStatValueAbstract call — that is "
                     "why it can run inside the tick loop at all",
              "GetStatValueAbstract" not in code, "no such call in the watch's CODE",
              [ln.strip() for ln in code.splitlines()
               if "GetStatValueAbstract" in ln][:3])
        check("9.5", "…and it is Reset() at a game boundary, so no age crosses a "
                     "reload",
              "public static void Reset()" in src, "a Reset entry point", None)
        agent = _read(os.path.join(REPO, "Source", "AutoRimmer", "AgentGameComponent.cs"))
        check("9.6", "AgentGameComponent still has NO ExposeData — the whole "
                     "reason this tracker is in memory (git-bug d16a463)",
              agent is not None and "ExposeData" not in agent,
              "no ExposeData in AgentGameComponent",
              agent is not None and "ExposeData" in agent)
        check("9.7", "…and it calls both ConstructionWatch entry points",
              agent is not None and "ConstructionWatch.Tick()" in agent
              and "ConstructionWatch.Reset()" in agent,
              "Tick() from the tick loop and Reset() from the game boundary", None)

    verbs = _read(VERBS_SRC)
    check("9.8", "the state precedence is ONE method, so the digest, the verb, "
                 "the halt report and the watch cannot disagree",
          verbs is not None and verbs.count("private static string State(") == 1,
          "exactly one State(...) definition",
          verbs.count("private static string State(") if verbs else None)

    # -- 9.9 the run's own digests --------------------------------------------
    files = sorted(glob.glob(os.path.join(RUN_DIGESTS, "day-*.json")))
    if not files:
        note("9.9", "%s has no day-*.json — the artifact checks are SKIPPED"
             % RUN_DIGESTS)
    else:
        days = {}
        for f in files:
            m = re.search(r"day-(\d+)\.json$", f)
            if not m:
                continue
            try:
                with open(f, encoding="utf-8") as fh:
                    env = json.load(fh)
            except (ValueError, OSError):
                continue
            days[int(m.group(1))] = dig(env, "data.construction") or {}

        run = [d for d in range(FLAT_FIRST, FLAT_LAST + 1)
               if days.get(d, {}).get("awaiting_materials") == FLAT_VALUE]
        check("9.9", "the run's OWN digests show awaiting_materials at %d for "
                     "days %d-%d — TWENTY consecutive in-game days, not the "
                     "fifteen the issue claims"
              % (FLAT_VALUE, FLAT_FIRST, FLAT_LAST),
              len(run) == (FLAT_LAST - FLAT_FIRST + 1),
              "%d days at %d" % (FLAT_LAST - FLAT_FIRST + 1, FLAT_VALUE),
              {"matched": len(run),
               "values": {d: days.get(d, {}).get("awaiting_materials")
                          for d in range(FLAT_FIRST, FLAT_LAST + 1)}})

        capped = [d for d in range(FLAT_FIRST, FLAT_LAST + 1)
                  if "cap" in days.get(d, {}) or "more" in days.get(d, {})]
        check("9.10", "…and NONE of those twenty readings was capped, so 22 was a "
                      "true census every time. The number was right; the number "
                      "was the problem.",
              not capped, "no cap/more key on days %d-%d" % (FLAT_FIRST, FLAT_LAST),
              capped)

        absent = [d for d in MISSING_DAYS if d not in days]
        check("9.11", "days %s are absent from that directory — worth knowing "
                      "before anybody quotes it as complete" % MISSING_DAYS,
              absent == MISSING_DAYS, "all of %s missing" % MISSING_DAYS,
              {"missing": absent, "present": sorted(days)[:8]})

        d1 = days.get(1, {})
        one = None
        try:
            with open(os.path.join(RUN_DIGESTS, "day-1.json"), encoding="utf-8") as fh:
                one = json.load(fh)
        except (ValueError, OSError):
            pass
        if one is not None:
            t = dig(one, "data.time.tick", 0) or 0
            check("9.12", "`day-1.json` is a MISLABELLED late-run snapshot (its "
                          "tick is deep into the run), so the real day-1 digest "
                          "is missing too",
                  t > 3000000, "a tick far past day 1", t)

    # -- 9.13 the helpers themselves ------------------------------------------
    good = {"data": {"item": {"stalled": None, "age_basis": "since-first-seen"}}}
    absent = {"data": {"item": {"age_basis": "since-first-seen"}}}

    check("9.13", "shape() passes on a present-but-NULL key, which is the value "
                  "f9dadc7 needs to be able to publish",
          probe(lambda: shape("x", "v", good, "data.item.stalled")),
          "pass", None)
    check("9.14", "shape() FAILS on an absent key",
          not probe(lambda: shape("x", "v", absent, "data.item.stalled")),
          "fail", None)
    check("9.15", "eq(..., None) would have PASSED on the absent key — which is "
                  "why every null assertion here uses eq_val() after a shape()",
          probe(lambda: eq("x", "w", absent, "data.item.stalled", None)),
          "pass (and that is the hazard)", None)
    check("9.16", "eq_val() FAILS when an absent key is read as null",
          not probe(lambda: eq_val("x", "w", dig(absent, "data.item.stalled"), False)),
          "fail", None)
    check("9.17", "keys_include() FAILS on a missing age key",
          not probe(lambda: keys_include("x", "w", absent, "data.item", AGE_KEYS)),
          "fail", None)
    check("9.18", "ne_val() FAILS when two legs of the tri-state present "
                  "identically — the form phase 4 needs",
          not probe(lambda: ne_val("x", "w", None, None)),
          "fail", None)


# ---------------------------------------------------------------------- main --

PHASES = {1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5, 6: phase6, 9: phase9}
DEFAULT_PHASES = [1, 2, 3, 4, 5, 6]


def main():
    global ARGS
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--root", default=DEFAULT_ROOT, help="the protocol root")
    p.add_argument("--phase", type=int, action="append",
                   choices=[0, 1, 2, 3, 4, 5, 6, 9],
                   help="run only these phases (repeatable); phase 0 always runs")
    p.add_argument("--quick", action="store_true",
                   help="skip phase 5's 30,000-tick tail")
    p.add_argument("--dry-run", action="store_true",
                   help="print the plan and every expectation, send nothing")
    p.add_argument("--selftest", action="store_true",
                   help="phase 9 only: offline; no bench, no game, nothing sent")
    p.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = p.parse_args()

    print("AutoRimmer acceptance - a blueprint that never completes (f9dadc7 / B-1)")

    if ARGS.selftest:
        print("mode: --selftest (offline; no bench, no game, nothing sent)")
        phase9()
        return summarise(selftest=True)

    print("protocol root: %s" % ARGS.root)
    if not ARGS.dry_run and not os.path.exists(os.path.join(ARGS.root, "status.json")):
        print("%sno status.json under %s - the ORCHESTRATOR starts the bench with "
              "_RimWorld-Agent/run-agent.sh, or pass --root%s" % (RED, ARGS.root, OFF))
        sys.exit(2)
    if not ARGS.dry_run:
        os.makedirs(os.path.join(ARGS.root, "commands"), exist_ok=True)

    default = [n for n in DEFAULT_PHASES if not (ARGS.quick and n == 5)]
    wanted = sorted(set(ARGS.phase or default) - {0})
    print("phases: 0 + %s" % ", ".join(str(x) for x in wanted))
    print("%sTHIS SUITE RUNS ~%d IN-GAME TICKS (~%.1f days) AND WRECKS THE COLONY "
          "IT RUNS ON: two unbuildable wall blueprints and a spawned blocker are "
          "left behind. Reload before running it twice.%s"
          % (YELLOW, STALL_TICKS + 40000, (STALL_TICKS + 40000) / 60000.0, OFF))

    phase0()
    for n in wanted:
        PHASES[n]()
    return summarise()


def summarise(selftest=False):
    print("")
    print("=" * 78)
    if FINDINGS:
        print("%sFINDINGS (shipped-mod defects, reported not asserted):%s" % (CYAN, OFF))
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
        print("%sRESULT: all %d self-checks passed. This proves the ASSERTIONS and "
              "the EVIDENCE behind them, not the mod: no bench was touched.%s"
              % (GREEN, CHECKS, OFF))
    else:
        print("%sRESULT: all %d checks passed%s" % (GREEN, CHECKS, OFF))
    sys.exit(0)


if __name__ == "__main__":
    main()
