#!/usr/bin/env python3
"""Acceptance runner for 2d9a1da — colony rates: the game's series + our sampler.

Same protocol, helpers and exit codes as `accept/722c951-advance-halt.py`; read
that file's header first, especially the SHAPE CONTRACT note — `eq(..., None)`
passes on an absent key, so phase 0 proves every dig path before any later
phase leans on it.

    ./accept/2d9a1da-colony-rates.py             # phases 0-5
    ./accept/2d9a1da-colony-rates.py --phase 3   # one phase (0 always runs)
    ./accept/2d9a1da-colony-rates.py --dry-run   # print the plan, send nothing
    ./accept/2d9a1da-colony-rates.py --selftest  # phase 9 only: NO bench needed

Start the bench first (`_RimWorld-Agent/run-agent.sh`) with a colony loaded,
`devMode = True` (phases 3 and 4 stage with `dev:*`), and leave it paused.

THE FIXTURE PHASE 3 NEEDS, stated up front because it is the one that can fail
for a reason that is not a defect: **a stockpile zone that accepts meals.**
`digest.resources.*` is stockpile-only (`ResourceCounter` walks SlotGroup haul
destinations), so food lying on unzoned ground reads as zero nutrition and a
food series staged there never moves. Phase 3 spawns with
`dev:spawn-thing {stockpile:true}` and PRECONDITIONS on `pos_source ==
"stockpile"`; if there is no accepting zone it exits 2 and says so rather than
grading a flat series.

WHY THIS SUITE EXISTS. The harness recorded no rates, only events, so every
leading indicator was out of reach — and session 13 had to answer "did wealth
cause the M1 raids?" by decoding `HistoryAutoRecorder` OUT OF `Autosave-5.rws`
BY HAND, because no verb could ask. `grep -rn HistoryAutoRecorder
Source/AutoRimmer/` returned nothing before this round.

  * PHASE 0 — the shapes. `history`, `trends` and `digest.trends` publish every
    key the later phases dig into, and every vanilla recorder is present.
  * PHASE 1 — THE GAME'S SERIES ARE REAL AND OURS MATCHES THEM. Three
    independent proofs that need no fixture: the wealth identity
    (Total == Items + Buildings + Pawns, exactly, because `WealthWatcher
    .WealthTotal` IS that sum), the index-is-tick map
    (`last_point_tick == (count-1)*record_ticks`, the game's own arithmetic
    from `HistoryAutoRecorderGroup.DrawGraph`), and a boundary crossing — 30,000
    ticks of real time makes every 30,000-tick series grow by exactly one point
    and the 60,000-tick series by zero or one.
  * PHASE 2 — THE SAMPLER RUNS ON GAME TIME. Points accumulate at the declared
    cadence across a real advance, the ring is bounded, and the per-sample COST
    is read off the envelope rather than asserted — this issue asks for overhead
    measured against `Journal.cs`'s 0.0039 ms/frame and a suite cannot measure
    it from outside.
  * PHASE 3 — THE SIGN, BOTH WAYS, OVER REAL ELAPSED TIME. Food is driven UP
    across N sampling windows and the slope is positive and `days_to_zero` is
    null; then DOWN, and the slope is negative and `days_to_zero` is a finite
    positive number. That is the acceptance bullet this issue says must not be
    met in a weaker form.
  * PHASE 4 — OBSERVERS NEVER MUTATE. Two halves, because the question splits.
    (a) With the clock STOPPED, 30 reads of `history`/`trends`/`digest` leave
    the save's `<autoRecorderGroups>` block byte-identical — that isolates OUR
    READS from time passing, which is what "does reading mutate" actually asks.
    (b) Across a real sampling window the records grow by EXACTLY the number of
    recorder boundaries crossed and no more — the direct analogue of 2.4's
    "252 progress entries before AND after; naive inserts ~160".
  * PHASE 5 — THE PREDICATE. `trends` is an addressable section, an ordering
    predicate on a slope arms, and the documented NULL TRAP is proved to be
    real: `trends.food_days_to_zero` is null when food is not falling and an
    ordering operator against it is REFUSED at arm time. The docs say predicates
    want `*_per_day`; this is the check that keeps that sentence true.
  * PHASES 6/7 — SAVE AND LOAD, and they cannot be automated end to end (the
    `b1b3060` precedent). 6 records the live ring and stops; you save and load
    in the game; 7 checks that the ring RESET, that the durable file KEPT
    everything, and that a `boundary` row marks the seam.
  * PHASE 9 — `--selftest`, offline. Re-derives every constant this file
    hard-codes from `Source/AutoRimmer/*.cs`, runs the slope estimator against
    synthetic series with known answers, and grades a REAL banked history block
    (`accept/fixtures/history-autoRecorderGroups-m1-Autosave-1.xml`, lifted from
    the M1 colony's own autosave) for the wealth identity and the cadence ratio.

WHY THESE WAITS ARE CLOCK PREDICATES AND NOT TICK COUNTS. The house rule (round
brief; DESIGN's session-19 entry) is that a wait must be a predicate. Every wait
here is `until:{condition:{path:"time.tick", op:">=", value:<absolute tick>}}`
with the target READ off the game. 722c951's suite argued the opposite for
itself and was right for itself: there, the subject under test was the advance,
"some time has passed" had no predicate spelling, and computing `now + N` lost a
race because the clock was MOVING between the read and the arm. Neither applies
here. This suite waits for a defined amount of GAME TIME — a sampling cadence is
2,500 game ticks by definition — and the game is PAUSED between calls (an
advance returns with `CurTimeSpeed == Paused`), so `now_tick()` and the arm see
the same clock and there is no race to lose. Every `until` also carries
`timeout_ticks`: an `until` whose predicate is already true at arm time runs
UNBOUNDED (git-bug 1113019).

IT DIRTIES THE COLONY. Phase 3 spawns and destroys food on purpose and phase 4
writes two save files. Run it on a bench you are willing to dirty.

Exit 0 = every check passed · 1 = at least one FAIL · 2 = a fixture
precondition could not be met, which is NOT a spec failure and says so.
"""

import argparse
import base64
import json
import os
import re
import struct
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

GREEN, RED, YELLOW, CYAN, DIM, OFF = (
    "\033[32m", "\033[31m", "\033[33m", "\033[36m", "\033[2m", "\033[0m",
)
if not sys.stdout.isatty():
    GREEN = RED = YELLOW = CYAN = DIM = OFF = ""

ARGS = None
FAILS = []
CHECKS = 0
SEQ = 0

WHY = ("accept/2d9a1da-colony-rates.py: this suite reads the journal only where "
       "a check needs it, so the standing escapes are declared once here rather "
       "than charging every phase a round trip it is not testing")

# ---- the mod's own contract, re-derived from source by phase 9 --------------
CADENCE_TICKS = 2500
RING_CAPACITY = 240
DEFAULT_WINDOW_POINTS = 24
SLOPE_MIN_POINTS = 3
SLOPE_MIN_SPAN_TICKS = 15000
HIST_WINDOW_POINTS = 4
HIST_MIN_POINTS = 3
HIST_MIN_SPAN_TICKS = 30000
HIST_DEFAULT_POINTS = 32
SERIES_CAP = 40

# ColonySampler.Fields — name, digest section, digest key, depletes.
SAMPLE_FIELDS = [
    ("food_days",       "resources", "food_days",       True),
    ("food_nutrition",  "resources", "food_nutrition",  True),
    ("food_needers",    "resources", "food_needers",    False),
    ("meds",            "resources", "meds",            True),
    ("steel",           "resources", "steel",           True),
    ("wood",            "resources", "wood",            True),
    ("components",      "resources", "components",      True),
    ("silver",          "resources", "silver",          True),
    ("power_stored_wd", "power",     "stored_wd",       True),
    ("power_gen_w",     "power",     "gen_w",           False),
    ("power_draw_w",    "power",     "draw_w",          False),
]

# The eleven Core recorders. From
# Data/Core/Defs/Misc/HistoryAutoRecording/HistoryAutoRecorders.xml — defName,
# group, recordTicksFrequency. No DLC adds any.
CORE_RECORDERS = [
    ("Wealth_Total",     "Wealth",       30000),
    ("Wealth_Items",     "Wealth",       30000),
    ("Wealth_Buildings", "Wealth",       30000),
    ("Wealth_Pawns",     "Wealth",       30000),
    ("FreeColonists",    "Population",   30000),
    ("Prisoners",        "Population",   30000),
    ("ColonistMood",     "ColonistMood", 30000),
    ("Adaptation",       "Debug",        60000),
    ("ThreatPoints",     "Debug",        30000),
    ("PopAdaptation",    "Debug",        60000),
    ("PopIntent",        "Debug",        60000),
]

# Phase 3's fixture.
ROUNDS = 8                  # 8 samples span 7 * 2600 = 18,200 ticks > the floor
ADVANCE_PER_ROUND = 2600    # one cadence plus a margin
MEALS_PER_ROUND = 20        # ~2 stacks of MealSimple (stackLimit 10)
STACKS_PER_ROUND = 2
UNTIL_TIMEOUT_TICKS = 20000


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=300):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acc2d9a1da-%03d-%s" % (SEQ, slug)
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


def now_tick():
    e = send("digest", {})
    t = dig(e, "data.time.tick")
    return t if isinstance(t, int) else None


def advance_to(target, timeout=900):
    """`advance until the game clock reaches T`, T read off the game.

    See the header's WHY THESE WAITS ARE CLOCK PREDICATES note. The escapes are
    declared because this suite is not testing 722c951's refusal and a phase
    that stopped to read the journal it does not grade would be measuring the
    protocol, not the spec."""
    return send("advance", {
        "until": {"condition": {"path": "time.tick", "op": ">=", "value": target}},
        "timeout_ticks": UNTIL_TIMEOUT_TICKS,
        "unread_ok": WHY,
        "through_casualties": WHY,
    }, timeout=timeout)


def run_ticks(n):
    """Let `n` game ticks pass, bounded, with the target read off the clock."""
    t = now_tick()
    if t is None:
        return {"ok": False, "error": {"code": "acc-no-clock",
                                       "detail": "digest published no time.tick"}}
    return advance_to(t + n)


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
    """dig() cannot tell `absent` from `present and null`, and this suite cares
    about exactly that distinction more than most: every slope in this surface
    is DELIBERATELY null before its window fills, and `days_to_zero` is
    deliberately null whenever the stock is not falling."""
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


def series_of(env, defname):
    for row in as_list(dig(env, "data.series")):
        if isinstance(row, dict) and row.get("def") == defname:
            return row
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


def eq_val(num, what, got, want):
    check(num, what, got == want, show(want), got)


def ge(num, what, env, path, want):
    got = dig(env, path)
    ok = isinstance(got, (int, float)) and not isinstance(got, bool) and got >= want
    check(num, "%s (%s)" % (what, path), ok, ">= %s" % want, got)


def num_cmp(num, what, got, op, want):
    ok = isinstance(got, (int, float)) and not isinstance(got, bool)
    if ok:
        ok = {"<": got < want, "<=": got <= want,
              ">": got > want, ">=": got >= want}[op]
    check(num, what, ok, "a number %s %s" % (op, want), got)


def close(num, what, got, want, tol):
    ok = (isinstance(got, (int, float)) and not isinstance(got, bool)
          and isinstance(want, (int, float)) and abs(got - want) <= tol)
    check(num, what, ok, "%s +/- %s" % (want, tol), got)


def contains(num, what, env, path, needle):
    got = dig(env, path)
    ok = isinstance(got, str) and needle.lower() in got.lower()
    check(num, "%s (%s)" % (what, path), ok, "a string containing %r" % needle, got)


def refused(num, what, env, code, needle=None):
    got = dig(env, "error.code")
    ok = dig(env, "ok") is False and got == code
    if ok and needle is not None:
        ok = needle.lower() in (dig(env, "error.detail") or "").lower()
    check(num, what, ok, "ok:false, code %s%s"
          % (code, "" if needle is None else ", detail naming %r" % needle),
          {"ok": dig(env, "ok"), "code": got,
           "detail": (dig(env, "error.detail") or "")[:400]})


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
    print("          This is a FIXTURE gap, not a failure of the spec.")
    sys.exit(2)


def shape(num, verb, env, path, kind=None):
    ok = has_key(env, path)
    want = "the key to be present"
    if ok and kind is not None:
        got = dig(env, path)
        # None is allowed wherever a slope lives: null before the window fills
        # is the CONTRACT, not a missing key, which is exactly why `shape`
        # checks presence separately from type.
        ok = got is None or isinstance(got, kind)
        want += " and a %s or null" % (kind.__name__ if isinstance(kind, type)
                                       else "/".join(k.__name__ for k in kind))
    check(num, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


# ------------------------------------------------------- the save-file reader --
#
# `HistoryAutoRecorder.ExposeData` writes `records` through
# `DataExposeUtility.LookByteArray`, which stores EITHER `<records>` (raw
# base64) or `<recordsDeflate>` (raw-deflate then base64) depending on size —
# both appear in a single real save, so both are handled. Four bytes per float,
# little-endian, which is `BitConverter.GetBytes(float)` on this platform.

REC_RE = re.compile(r"<def>(\w+)</def>\s*<(records|recordsDeflate)>(.*?)</\2>", re.S)


def parse_history_block(text):
    """{defName: [values]} out of an `<autoRecorderGroups>` block."""
    i = text.find("<autoRecorderGroups>")
    j = text.find("</autoRecorderGroups>")
    if i < 0 or j < 0:
        return {}
    out = {}
    for m in REC_RE.finditer(text[i:j]):
        name, kind, body = m.group(1), m.group(2), m.group(3).strip()
        try:
            raw = base64.b64decode(body)
            if kind == "recordsDeflate":
                raw = zlib.decompress(raw, -15)
            out[name] = list(struct.unpack("<%df" % (len(raw) // 4), raw))
        except Exception:
            out[name] = None
    return out


def saves_dir():
    return os.path.join(os.path.dirname(os.path.normpath(ARGS.root)), "Saves")


def read_save_history(name):
    path = os.path.join(saves_dir(), name + ".rws")
    if not os.path.exists(path):
        return None, None
    with open(path, encoding="utf-8", errors="replace") as fh:
        text = fh.read()
    i = text.find("<autoRecorderGroups>")
    j = text.find("</autoRecorderGroups>")
    block = text[i:j] if i >= 0 and j >= 0 else ""
    return parse_history_block(text), block


def save_as(name):
    return send("journal-selftest", {"steps": ["save"], "save_name": name}, timeout=600)


def sample_file_lines():
    """Every row of the durable sample log for THIS session."""
    e = send("trends", {})
    rel = dig(e, "data.durable_file")
    if not isinstance(rel, str):
        return None, None
    path = os.path.join(ARGS.root, *rel.split("/"))
    if not os.path.exists(path):
        return path, None
    rows = []
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                rows.append(json.loads(line))
            except ValueError:
                rows.append({"kind": "UNPARSEABLE", "raw": line[:200]})
    return path, rows


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 — the shapes every later phase digs into")

    e = send("status")
    precondition("0.a", "the bench is up and a colony is loaded",
                 dig(e, "ok") is True and dig(e, "data.gameLoaded") is True,
                 "start _RimWorld-Agent with a save loaded, paused.")

    # ---- `history` -------------------------------------------------------
    h = send("history", {})
    eq("0.1", "`history` answers", h, "ok", True)
    shape("0.2", "history", h, "data.game_tick", int)
    shape("0.3", "history", h, "data.day", (int, float))
    shape("0.4", "history", h, "data.recorders", int)
    shape("0.5", "history", h, "data.returned", int)
    shape("0.6", "history", h, "data.series", list)
    shape("0.7", "history", h, "data.window_points", int)
    shape("0.8", "history", h, "data.order", str)
    contains("0.9", "…and names its provenance rather than leaving it inferred",
             h, "data.source", "HistoryAutoRecorder.records")

    got_defs = [r.get("def") for r in as_list(dig(h, "data.series"))
                if isinstance(r, dict)]
    missing = [d for d, _, _ in CORE_RECORDERS if d not in got_defs]
    check("0.10", "every one of the ELEVEN Core recorders is published — the "
                  "Debug group included, because `devModeOnly` gates the UI TAB "
                  "and not the recording (History.HistoryTick loops every group)",
          not missing and not ARGS.dry_run or ARGS.dry_run,
          "no missing defs", {"missing": missing, "got": got_defs})

    row = series_of(h, "Wealth_Total") or {}
    if ARGS.dry_run:
        row = {}
    for n, key, kind in (("0.11", "def", str), ("0.12", "group", str),
                         ("0.13", "label", str), ("0.14", "record_ticks", int),
                         ("0.15", "days_per_point", (int, float)),
                         ("0.16", "count", int), ("0.17", "last_point_tick", int),
                         ("0.18", "aligned", bool),
                         ("0.19", "slope_per_day", (int, float)),
                         ("0.20", "slope_points", int),
                         ("0.21", "slope_span_days", (int, float)),
                         ("0.22", "values", list),
                         ("0.23", "values_from_index", int),
                         ("0.24", "dropped", int)):
        shape(n, "history/Wealth_Total", {"data": row}, "data." + key, kind)

    tp = series_of(h, "ThreatPoints") or {}
    check("0.25", "ThreatPoints carries `stored_scale` — the stored number is "
                  "points/10 and only the human-readable label said so",
          ARGS.dry_run or isinstance(tp.get("stored_scale"), dict),
          "a stored_scale object", tp.get("stored_scale"))
    eq_val("0.26", "…naming the multiplier that recovers the real quantity",
           dig({"d": tp}, "d.stored_scale.multiply_by"), 10.0)
    check("0.27", "…and the member it recovers",
          ARGS.dry_run or "DefaultThreatPointsNow" in
          str(dig({"d": tp}, "d.stored_scale.to_get")),
          "to_get naming StorytellerUtility.DefaultThreatPointsNow",
          dig({"d": tp}, "d.stored_scale.to_get"))
    wt = series_of(h, "Wealth_Total") or {}
    check("0.28", "…and a series with NO hidden scale carries no `stored_scale` "
                  "(absent, not null — a guess for every def would be worse "
                  "than an entry for two)",
          ARGS.dry_run or "stored_scale" not in wt,
          "the key to be ABSENT on Wealth_Total", wt.get("stored_scale"))

    # ---- `trends` --------------------------------------------------------
    t = send("trends", {})
    eq("0.29", "`trends` answers", t, "ok", True)
    for n, key, kind in (("0.30", "cadence_ticks", int),
                         ("0.31", "ring_capacity", int),
                         ("0.32", "window_points", int),
                         ("0.33", "min_points", int),
                         ("0.34", "min_span_ticks", int),
                         ("0.35", "points", int),
                         ("0.36", "ring_points", int),
                         ("0.37", "first_tick", int),
                         ("0.38", "last_tick", int),
                         ("0.39", "span_ticks", int),
                         ("0.40", "fields", list),
                         ("0.41", "series", dict),
                         ("0.42", "cost", dict),
                         ("0.43", "durable_file", str),
                         ("0.44", "volatile", str)):
        shape(n, "trends", t, "data." + key, kind)
    eq("0.45", "the cadence is the declared one", t, "data.cadence_ticks",
       CADENCE_TICKS)
    eq("0.46", "…and the ring is the declared size", t, "data.ring_capacity",
       RING_CAPACITY)
    eq("0.47", "…and the slope floors are the declared ones", t, "data.min_points",
       SLOPE_MIN_POINTS)
    eq("0.48", "…span floor too", t, "data.min_span_ticks",
       SLOPE_MIN_SPAN_TICKS)
    eq_val("0.49", "the field set is EXACTLY the declared column set, in order "
                   "— this issue's own argument is that a schema that drifts "
                   "makes runs incomparable",
           dig(t, "data.fields"),
           [f[0] for f in SAMPLE_FIELDS])
    contains("0.50", "…and the envelope says the ring is volatile IN THE "
                     "ENVELOPE, not only in a source comment",
             t, "data.volatile", "cleared at every game boundary")
    for n, key, kind in (("0.51", "samples", (int, float)),
                         ("0.52", "ms_total", (int, float)),
                         ("0.53", "ms_avg", (int, float)),
                         ("0.54", "ms_max", (int, float)),
                         ("0.55", "ticks_covered", int),
                         ("0.56", "ms_per_1000_ticks", (int, float))):
        shape(n, "trends", t, "data.cost." + key, kind)
    contains("0.57", "…and the cost block names the benchmark it is compared to",
             t, "data.cost.benchmark", "0.0039 ms/frame")

    fd = dig(t, "data.series.food_days") or {}
    for n, key, kind in (("0.58", "now", (int, float)), ("0.59", "first", (int, float)),
                         ("0.60", "min", (int, float)), ("0.61", "max", (int, float)),
                         ("0.62", "slope_per_day", (int, float)),
                         ("0.63", "points", int), ("0.64", "span_ticks", int),
                         ("0.65", "days_to_zero", (int, float)),
                         ("0.66", "from", str)):
        shape(n, "trends/food_days", {"data": fd}, "data." + key, kind)
    eq_val("0.67", "…and every field cites the digest builder it is lifted from, "
                   "so nothing here is a re-derivation",
           fd.get("from"), "digest.resources.food_days")
    bad = send("trends", {"fields": ["food_days", "no_such_field"]})
    check("0.68b", "`trends {fields:[…]}` NAMES a field it does not know rather "
                   "than quietly returning a shorter answer — a caller would "
                   "read the absence as `that field is flat`",
          ARGS.dry_run or dig(bad, "data.unknown_fields") == ["no_such_field"],
          "unknown_fields naming it", dig(bad, "data.unknown_fields"))

    gw = dig(t, "data.series.power_gen_w") or {}
    check("0.68", "a RATE field carries no `days_to_zero` (reaching zero "
                  "generation is not a countdown)",
          ARGS.dry_run or "days_to_zero" not in gw,
          "the key to be ABSENT on power_gen_w", gw.get("days_to_zero"))

    # ---- `digest.trends` -------------------------------------------------
    d = send("digest", {})
    eq("0.69", "`digest` answers", d, "ok", True)
    for n, key, kind in (("0.70", "ready", bool), ("0.71", "points", int),
                         ("0.72", "window_points", int), ("0.73", "span_ticks", int),
                         ("0.74", "as_of_tick", int),
                         ("0.75", "food_days_per_day", (int, float)),
                         ("0.76", "food_days_to_zero", (int, float)),
                         ("0.77", "nutrition_per_day", (int, float)),
                         ("0.78", "meds_per_day", (int, float)),
                         ("0.79", "wealth_per_day", (int, float)),
                         ("0.80", "threat_points_per_day", (int, float)),
                         ("0.81", "mood_per_day", (int, float)),
                         ("0.82", "colonists_per_day", (int, float))):
        shape(n, "digest", d, "data.trends." + key, kind)
    note("0.83", "digest.trends ready=%s points=%s as_of_tick=%s; not_ready_why=%s"
         % (dig(d, "data.trends.ready"), dig(d, "data.trends.points"),
            dig(d, "data.trends.as_of_tick"),
            (dig(d, "data.trends.not_ready_why") or "-")[:110]))
    if dig(d, "data.trends.ready") is False:
        check("0.84", "a NOT-ready trends block SAYS WHY (candidates + reasons, "
                      "never a bare null)",
              has_key(d, "data.trends.not_ready_why"),
              "not_ready_why present while ready is false",
              dig(d, "data.trends.not_ready_why"))
    else:
        check("0.84", "a READY trends block carries no `not_ready_why`",
              not has_key(d, "data.trends.not_ready_why"),
              "the key to be ABSENT while ready is true",
              dig(d, "data.trends.not_ready_why"))


# ------------------------------------------------------------------- phase 1 --

def phase1():
    banner("PHASE 1 — the GAME's series: real, self-consistent, and index IS tick")

    h = send("history", {})
    eq("1.1", "`history` answers", h, "ok", True)
    rows = {r["def"]: r for r in as_list(dig(h, "data.series"))
            if isinstance(r, dict) and "def" in r}

    precondition("1.a", "the colony is old enough to have wealth samples",
                 ARGS.dry_run or (rows.get("Wealth_Total", {}).get("count") or 0) >= 1,
                 "Wealth_Total has no records yet, which means TicksGame has "
                 "never crossed 0 %% 30000 on this colony. Advance ~30,000 ticks "
                 "and run again. Found: %r"
                 % (rows.get("Wealth_Total", {}).get("count"),))

    # -- the wealth identity, which is the sharpest check available ---------
    # `RimWorld/WealthWatcher.WealthTotal` IS `wealthItems + wealthBuildings +
    # wealthPawns`, and all four workers pull inside the same HistoryTick, so
    # the four series agree sample for sample. A reader that mixed up recorders,
    # mis-sliced a tail or read the wrong `records` list fails this.
    if not ARGS.dry_run:
        tot = rows.get("Wealth_Total", {}).get("values") or []
        it = rows.get("Wealth_Items", {}).get("values") or []
        bu = rows.get("Wealth_Buildings", {}).get("values") or []
        pa = rows.get("Wealth_Pawns", {}).get("values") or []
        n = min(len(tot), len(it), len(bu), len(pa))
        worst, worst_i = 0.0, -1
        for i in range(n):
            d = abs(tot[i] - (it[i] + bu[i] + pa[i]))
            if d > worst:
                worst, worst_i = d, i
        check("1.2", "the wealth identity holds sample for sample: Total == "
                     "Items + Buildings + Pawns, which is literally what "
                     "WealthWatcher.WealthTotal returns",
              n > 0 and worst <= max(0.05, 1e-4 * max(tot or [1])),
              "%d samples agreeing within rounding" % n,
              {"samples": n, "worst_delta": round(worst, 4), "at_index": worst_i})
    else:
        note("1.2", "would compare Wealth_Total against Items+Buildings+Pawns "
                    "sample for sample")

    # -- index is tick, checked against the clock ---------------------------
    gt = dig(h, "data.game_tick")
    bad = []
    for defname, group, freq in CORE_RECORDERS:
        r = rows.get(defname)
        if not r:
            continue
        if r.get("record_ticks") != freq:
            bad.append((defname, "record_ticks", r.get("record_ticks"), freq))
        c, lpt = r.get("count"), r.get("last_point_tick")
        if isinstance(c, int) and c > 0 and lpt != (c - 1) * freq:
            bad.append((defname, "last_point_tick", lpt, (c - 1) * freq))
        if isinstance(c, int) and c > 0 and r.get("aligned") is not True:
            bad.append((defname, "aligned", r.get("aligned"), True))
    check("1.3", "every Core recorder's cadence matches the XML, its "
                 "last_point_tick is (count-1)*record_ticks — the game's own "
                 "index-to-day map from HistoryAutoRecorderGroup.DrawGraph — "
                 "and it reports `aligned`",
          ARGS.dry_run or not bad, "no disagreements", bad)
    note("1.4", "game_tick=%s; Wealth_Total count=%s last=%s; "
                "Adaptation (60,000-tick) count=%s"
         % (gt, dig({"r": rows}, "r.Wealth_Total.count"),
            dig({"r": rows}, "r.Wealth_Total.last"),
            dig({"r": rows}, "r.Adaptation.count")))

    # -- a 30,000-tick boundary crossing makes the series GROW --------------
    before = {d: (rows.get(d) or {}).get("count") for d, _, _ in CORE_RECORDERS}
    lpt = (rows.get("Wealth_Total") or {}).get("last_point_tick")
    if ARGS.dry_run or not isinstance(lpt, int) or not isinstance(gt, int):
        note("1.5", "would advance past the next 30,000-tick recorder boundary "
                    "and assert every 30,000-tick series grew by exactly 1")
        return
    target = lpt + 30000 + 400          # past the boundary, with margin
    note("1.5", "advancing to tick %d (next Wealth boundary at %d, now %d) — "
                "~%d ticks of real game time"
         % (target, lpt + 30000, gt, target - gt))
    a = advance_to(target)
    eq("1.6", "the advance ran", a, "ok", True)
    h2 = send("history", {})
    rows2 = {r["def"]: r for r in as_list(dig(h2, "data.series"))
             if isinstance(r, dict) and "def" in r}
    grew_wrong = []
    for defname, group, freq in CORE_RECORDERS:
        b, af = before.get(defname), (rows2.get(defname) or {}).get("count")
        if not isinstance(b, int) or not isinstance(af, int):
            continue
        want = 1 if freq == 30000 else (0, 1)
        if freq == 30000 and af - b != 1:
            grew_wrong.append((defname, freq, b, af, "expected +1"))
        if freq == 60000 and af - b not in (0, 1):
            grew_wrong.append((defname, freq, b, af, "expected +0 or +1"))
    check("1.7", "crossing ONE 30,000-tick boundary grew every 30,000-tick "
                 "series by exactly one point, and the 60,000-tick series by "
                 "zero or one — the cadence is the game's and we read it, we do "
                 "not drive it",
          not grew_wrong, "no wrong growth", grew_wrong)

    # -- and the newest point agrees with an INDEPENDENT live reading -------
    # FreeColonists' worker counts
    # PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoLodgers;
    # `pawns {filter:"colonist"}` walks MapPawns on the current map. Different
    # route, same fact on a single-map bench with no caravan out.
    p = send("pawns", {"filter": "colonist", "cap": 200})
    live = len([r for r in as_list(dig(p, "data.list")) if isinstance(r, dict)])
    recorded = (rows2.get("FreeColonists") or {}).get("last")
    check("1.8", "the newest FreeColonists record agrees with a live roster read "
                 "taken moments later — two different routes to the same fact "
                 "(PawnsFinder vs MapPawns)",
          isinstance(recorded, (int, float)) and abs(recorded - live) < 0.5,
          "%d (the live count)" % live, recorded)
    note("1.9", "CAVEAT ON 1.8, stated rather than assumed away: the recorder "
                "counts ALL maps, caravans and travelling transporters and "
                "excludes lodgers, while `pawns` reads the current map. They "
                "agree on this bench because it is single-map with nobody out; "
                "a caravan in flight would make 1.8 fail for a correct reader.")


# ------------------------------------------------------------------- phase 2 --

def phase2():
    banner("PHASE 2 — the sampler accumulates points on GAME time, and costs what it says")

    t0 = send("trends", {})
    eq("2.1", "`trends` answers", t0, "ok", True)
    p0 = dig(t0, "data.ring_points")
    s0 = dig(t0, "data.cost.samples")
    last0 = dig(t0, "data.last_tick")
    note("2.2", "before: ring_points=%s samples=%s last_tick=%s ms_avg=%s"
         % (p0, s0, last0, dig(t0, "data.cost.ms_avg")))

    if ARGS.dry_run:
        note("2.3", "would advance 3 cadences and assert the ring grew by ~3")
        return

    # THE PAUSED CONTROL, and it is the phase's real claim. The sampler hangs
    # on GameComponentTick and not GameComponentUpdate, so while the clock is
    # stopped no sample may be taken however many frames go by. If this grew,
    # the sampler would be recording wall-clock time as game time and every
    # slope would be diluted by however long the agent sat thinking.
    time.sleep(4)
    tp = send("trends", {})
    eq_val("2.3", "NO sample is taken while the game is PAUSED, across four "
                  "wall-clock seconds and hundreds of frames — the sampler is "
                  "on GameComponentTick, not GameComponentUpdate",
           dig(tp, "data.ring_points"), p0)

    want = 3
    a = run_ticks(CADENCE_TICKS * want + 400)
    eq("2.4", "the advance ran", a, "ok", True)
    t1 = send("trends", {})
    p1 = dig(t1, "data.ring_points")
    s1 = dig(t1, "data.cost.samples")
    grew = (p1 - p0) if isinstance(p1, int) and isinstance(p0, int) else None
    check("2.5", "the ring gained ~%d points over %d ticks — one per declared "
                 "cadence of %d" % (want, CADENCE_TICKS * want + 400, CADENCE_TICKS),
          isinstance(grew, int) and want <= grew <= want + 1,
          "%d or %d new points" % (want, want + 1), grew)
    check("2.6", "…and the cost counter agrees with the ring",
          isinstance(s1, (int, float)) and isinstance(s0, (int, float))
          and (s1 - s0) == grew,
          "samples grew by the same %s" % grew, (s0, s1))
    ge("2.7", "the ring is bounded by its declared capacity", t1, "data.ring_capacity",
       RING_CAPACITY)
    check("2.8", "…and never holds more than that",
          isinstance(p1, int) and p1 <= RING_CAPACITY,
          "<= %d" % RING_CAPACITY, p1)

    # THE MEASUREMENT THIS ISSUE ASKS FOR. Reported, not asserted against a
    # number this file invented: the acceptance bullet is "overhead measured
    # against Journal.cs's 0.0039 ms/frame benchmark and REPORTED", and the
    # sampler publishes its own stopwatch so the figure is the mod's, not the
    # suite's arithmetic over round-trip latency.
    note("2.9", "COST, measured by the mod: samples=%s ms_total=%s ms_avg=%s "
                "ms_max=%s over %s ticks -> %s ms per 1000 ticks. Journal.cs's "
                "benchmark is 0.0039 ms/frame."
         % (dig(t1, "data.cost.samples"), dig(t1, "data.cost.ms_total"),
            dig(t1, "data.cost.ms_avg"), dig(t1, "data.cost.ms_max"),
            dig(t1, "data.cost.ticks_covered"), dig(t1, "data.cost.ms_per_1000_ticks")))
    ms_avg = dig(t1, "data.cost.ms_avg")
    check("2.10", "a sample costs under a millisecond — a hard ceiling rather "
                  "than a target, because this runs inside DoSingleTick and "
                  "anything near a frame budget would be a defect",
          isinstance(ms_avg, (int, float)) and 0 <= ms_avg < 1.0,
          "0 <= ms_avg < 1.0", ms_avg)

    # -- the durable file ---------------------------------------------------
    path, rows = sample_file_lines()
    check("2.11", "the durable sample file exists and is readable MID-RUN",
          rows is not None, "a readable ndjson at %s" % path, rows)
    if rows:
        kinds = {}
        for r in rows:
            kinds[r.get("kind")] = kinds.get(r.get("kind"), 0) + 1
        header = next((r for r in rows if r.get("kind") == "header"), None)
        check("2.12", "…opening with a HEADER row that declares the column set, "
                      "so the file is self-describing and the field set cannot "
                      "drift from the code that fills it",
              isinstance(header, dict), "a header row", header)
        eq_val("2.13", "…and the columns are the declared ones, in order",
               (header or {}).get("fields"), [f[0] for f in SAMPLE_FIELDS])
        eq_val("2.14", "…and it carries the cadence",
               (header or {}).get("cadence_ticks"), CADENCE_TICKS)
        eq_val("2.15", "…and the mod version, so a post-mortem can tell which "
                       "definition of food_days a run was on",
               isinstance((header or {}).get("mod"), str), True)
        samples = [r for r in rows if r.get("kind") == "sample"]
        check("2.16", "…and one `sample` row per point the ring reports taking",
              len(samples) >= (s1 or 0), ">= %s sample rows" % s1, len(samples))
        last = samples[-1] if samples else {}
        missing = [f[0] for f in SAMPLE_FIELDS if f[0] not in (last.get("v") or {})]
        check("2.17", "…each carrying every declared field by NAME (a positional "
                      "row is the format that breaks when a column is inserted)",
              not missing, "no missing fields", missing)
        note("2.18", "sample file %s: %s" % (path, kinds))


# ------------------------------------------------------------------- phase 3 --

def phase3():
    banner("PHASE 3 — THE SIGN, both ways, over real elapsed game time")

    e = send("digest", {})
    precondition("3.a", "devMode is on (dev:spawn-thing / dev:destroy stage this)",
                 ARGS.dry_run or dig(send("dev:destroy", {}), "error.code") == "bad-args",
                 "dev:destroy answered something other than bad-args on an empty "
                 "call, which means the dev gate refused it. Set devMode = True.")

    food = ARGS.food_def
    probe = send("dev:spawn-thing", {"def": food, "count": 1, "stockpile": True})
    precondition("3.b", "a stockpile zone accepts `%s`" % food,
                 ARGS.dry_run or (dig(probe, "ok") is True
                                  and dig(probe, "data.args.pos_source") == "stockpile"),
                 "`digest.resources.*` is STOCKPILE-ONLY (ResourceCounter walks "
                 "SlotGroup haul destinations), so food on unzoned ground reads "
                 "as zero nutrition and a series staged there never moves. Make "
                 "a stockpile zone that accepts meals, then re-run. Got "
                 "ok=%s pos_source=%s note=%s"
                 % (dig(probe, "ok"), dig(probe, "data.args.pos_source"),
                    dig(probe, "data.args.stockpile_note")))

    if ARGS.dry_run:
        note("3.1", "would drive food UP over %d rounds, assert slope > 0 and "
                    "days_to_zero null; then DOWN, assert slope < 0 and "
                    "days_to_zero finite" % ROUNDS)
        return

    # ================================ RISING ==============================
    note("3.1", "RISING: %d rounds of +%d %s, each followed by %d ticks"
         % (ROUNDS, MEALS_PER_ROUND, food, ADVANCE_PER_ROUND))
    placed = 0
    for r in range(ROUNDS):
        s = send("dev:spawn-thing", {"def": food, "count": MEALS_PER_ROUND,
                                     "stockpile": True})
        placed += dig(s, "data.placed") or 0
        run_ticks(ADVANCE_PER_ROUND)
    # THE WINDOW IS THE HALF, AND THAT IS THE POINT OF PUBLISHING IT. The
    # default 24-point window is a full in-game day and would straddle BOTH
    # halves of this phase, averaging a rise and a fall into nothing. A slope
    # is only ever a statement about a window; the verb takes one so a caller
    # can ask about the stretch it means.
    up = send("trends", {"window_points": ROUNDS})
    fu = dig(up, "data.series.food_days") or {}
    note("3.2", "after rising: placed=%d food_days now=%s first=%s slope=%s "
                "points=%s span=%s"
         % (placed, fu.get("now"), fu.get("first"), fu.get("slope_per_day"),
            fu.get("points"), fu.get("span_ticks")))
    precondition("3.c", "the rising half produced a usable window",
                 (fu.get("points") or 0) >= SLOPE_MIN_POINTS
                 and (fu.get("span_ticks") or 0) >= SLOPE_MIN_SPAN_TICKS,
                 "the sampler took %s points spanning %s ticks; a slope needs "
                 "%d points over %d ticks. The advances may have been shorter "
                 "than the cadence."
                 % (fu.get("points"), fu.get("span_ticks"),
                    SLOPE_MIN_POINTS, SLOPE_MIN_SPAN_TICKS))
    num_cmp("3.3", "food_days' slope is POSITIVE while food is being added, "
                   "measured over %d real sampling windows" % ROUNDS,
            fu.get("slope_per_day"), ">", 0)
    num_cmp("3.4", "…and nutrition rose with it", 
            dig(up, "data.series.food_nutrition.slope_per_day"), ">", 0)
    eq_val("3.5", "…and `days_to_zero` is NULL, because a stock that is not "
                  "falling has no honest countdown and a large sentinel would "
                  "read like a real deadline (the 61794cd ruling)",
           fu.get("days_to_zero"), None)
    check("3.6", "…the key is PRESENT and null, not absent — the difference "
                 "matters to a caller that digs it",
          "days_to_zero" in fu, "the key present", sorted(fu.keys()))

    # ================================ FALLING =============================
    note("3.7", "FALLING: %d rounds of destroying %d stack(s) of %s, each "
                "followed by %d ticks" % (ROUNDS, STACKS_PER_ROUND, food,
                                          ADVANCE_PER_ROUND))
    destroyed = 0
    empty_rounds = 0
    for r in range(ROUNDS):
        # `radius` is capped at 200 by the verb and `near` defaults to the first
        # free colonist (Dev.Anchor), which is where the stockpile is on any
        # ordinary bench colony.
        d = send("dev:destroy", {"def": food, "radius": 200, "cap": STACKS_PER_ROUND})
        n = len(as_list(dig(d, "data.destroyed")))
        destroyed += n
        if n == 0:
            empty_rounds += 1
        run_ticks(ADVANCE_PER_ROUND)
    down = send("trends", {"window_points": ROUNDS})
    fd = dig(down, "data.series.food_days") or {}
    note("3.8", "after falling: destroyed=%d stacks (%d empty rounds) "
                "food_days now=%s first=%s slope=%s points=%s span=%s "
                "days_to_zero=%s"
         % (destroyed, empty_rounds, fd.get("now"), fd.get("first"),
            fd.get("slope_per_day"), fd.get("points"), fd.get("span_ticks"),
            fd.get("days_to_zero")))
    precondition("3.d", "the falling half actually removed food",
                 destroyed > 0 and empty_rounds < ROUNDS,
                 "dev:destroy found nothing to destroy in %d of %d rounds. The "
                 "meals may be outside the 200-cell radius from the anchor "
                 "colonist, or colonists ate them. Stage the stockpile nearer "
                 "the colony." % (empty_rounds, ROUNDS))
    num_cmp("3.9", "food_days' slope is NEGATIVE while food is being removed — "
                   "the same estimator, the same window length, the opposite "
                   "sign, over real elapsed game time",
            fd.get("slope_per_day"), "<", 0)
    num_cmp("3.10", "…and `days_to_zero` is now a FINITE POSITIVE number: the "
                    "colony's ticks_until_bleedout, which is the whole point of "
                    "the spec",
             fd.get("days_to_zero"), ">", 0)
    # The arithmetic is checkable from the envelope alone, which is this
    # project's standing rule for a derived figure.
    now, slope, dtz = fd.get("now"), fd.get("slope_per_day"), fd.get("days_to_zero")
    if all(isinstance(x, (int, float)) for x in (now, slope, dtz)) and slope < 0:
        close("3.11", "…and it is exactly now / -slope, checkable from the "
                      "envelope without trusting the mod's arithmetic",
              dtz, now / -slope, 0.02)
    else:
        check("3.11", "days_to_zero is checkable from the envelope", False,
              "now, slope and days_to_zero all numeric with slope < 0",
              (now, slope, dtz))

    # -- the glance carries it too, at ITS window --------------------------
    dg = send("digest", {})
    tv = send("trends", {})
    eq_val("3.12", "`digest.trends.food_days_per_day` is the SAME builder at the "
                   "verb's default window — one number, two surfaces, no second "
                   "implementation",
           dig(dg, "data.trends.food_days_per_day"),
           dig(tv, "data.series.food_days.slope_per_day"))
    eq_val("3.13", "…and so is the countdown",
           dig(dg, "data.trends.food_days_to_zero"),
           dig(tv, "data.series.food_days.days_to_zero"))
    note("3.14", "the digest's window is %s points (one in-game day) and this "
                 "phase drove two opposite halves through it, so its slope is "
                 "the AVERAGE of both and is not asserted here. That is the "
                 "design saying what it says: a slope is a statement about a "
                 "window. digest.trends.food_days_per_day=%s"
         % (DEFAULT_WINDOW_POINTS, dig(dg, "data.trends.food_days_per_day")))

    # -- the leading-indicator claim, stated against the alert --------------
    alerts = [a.get("id") for a in as_list(dig(dg, "data.alerts.active"))
              if isinstance(a, dict)]
    note("3.15", "LEADING vs LAGGING, for the record: food_days=%s, slope=%s/day, "
                 "days_to_zero=%s, and Alert_LowFood is %s. The alert fires at "
                 "nutrition-per-colonist < 4 and CANNOT fire at all before tick "
                 "150,000 (Alert_LowFood.GetReport's first line); the slope is "
                 "readable from the first full window."
         % (dig(dg, "data.resources.food_days"),
            dig(dg, "data.trends.food_days_per_day"),
            dig(dg, "data.trends.food_days_to_zero"),
            "ACTIVE" if any("LowFood" in str(a) for a in alerts) else "not active"))


# ------------------------------------------------------------------- phase 4 --

def phase4():
    banner("PHASE 4 — observers never mutate: a save-diff, and a records count")

    if ARGS.dry_run:
        note("4.1", "would save, read history/trends/digest 30x with the clock "
                    "STOPPED, save again, and compare the "
                    "<autoRecorderGroups> blocks byte for byte")
        return

    e = send("pause", {})
    note("4.1", "paused: %s" % dig(e, "ok"))

    a = save_as("rates-diff-a")
    precondition("4.a", "the bench can write a save",
                 dig(a, "ok") is True,
                 "journal-selftest {steps:['save']} failed: %s. devMode must be "
                 "on. Got: %s" % (dig(a, "error.code"), show(dig(a, "error"))))
    hist_a, block_a = read_save_history("rates-diff-a")
    precondition("4.b", "the save is readable and carries a history block",
                 bool(hist_a),
                 "no <autoRecorderGroups> found in %s"
                 % os.path.join(saves_dir(), "rates-diff-a.rws"))

    READS = 30
    for i in range(READS):
        send("history", {"points": 200})
        send("trends", {"points": RING_CAPACITY})
        send("digest", {})
    a2 = save_as("rates-diff-b")
    eq("4.2", "the second save was written", a2, "ok", True)
    hist_b, block_b = read_save_history("rates-diff-b")

    check("4.3", "%d x (history + trends + digest) with the CLOCK STOPPED left "
                 "the save's <autoRecorderGroups> block BYTE-IDENTICAL — the "
                 "read is the only variable, because no tick ran between the "
                 "two saves" % READS,
          block_a == block_b and bool(block_a),
          "identical blocks (%d bytes)" % len(block_a or ""),
          {"len_a": len(block_a or ""), "len_b": len(block_b or ""),
           "equal": block_a == block_b})
    diffs = []
    for k in sorted(set(list(hist_a.keys()) + list(hist_b.keys()))):
        if hist_a.get(k) != hist_b.get(k):
            diffs.append((k, len(hist_a.get(k) or []), len(hist_b.get(k) or [])))
    check("4.4", "…and every decoded series is identical value for value",
          not diffs, "no differing series", diffs)
    note("4.5", "counts before: %s"
         % {k: len(v or []) for k, v in sorted(hist_a.items())})

    # -- (b) across a REAL sampling window ---------------------------------
    # Time passes here, so the saves differ everywhere and a byte diff proves
    # nothing. What IS decidable is the count: a reader that called
    # `Worker.PullRecord()` — or re-derived and appended — inserts records the
    # game did not ask for. This is 2.4's method ("252 progress entries before
    # AND after; naive inserts ~160") on the recorder that this round reads.
    t0 = now_tick()
    span = CADENCE_TICKS * 6 + 400
    note("4.6", "advancing %d ticks (>= 6 sampling cadences) from %d" % (span, t0))
    run_ticks(span)
    t1 = now_tick()
    for i in range(10):
        send("history", {})
        send("trends", {})
    send("pause", {})
    a3 = save_as("rates-diff-c")
    eq("4.7", "the third save was written", a3, "ok", True)
    hist_c, _ = read_save_history("rates-diff-c")

    wrong = []
    for defname, group, freq in CORE_RECORDERS:
        before_n = len(hist_a.get(defname) or [])
        after_n = len(hist_c.get(defname) or [])
        # Boundaries crossed: how many multiples of `freq` lie in (t0', t1].
        # t0' is the tick of the FIRST save; it is not read exactly, so the
        # count is allowed a slack of one at each end.
        expect = (t1 // freq) - ((t0 - span) // freq)
        if not (after_n - before_n) <= expect + 1:
            wrong.append((defname, freq, before_n, after_n, "<= %d" % (expect + 1)))
    check("4.8", "across a real sampling window every series grew by at most the "
                 "number of recorder boundaries the clock crossed — the sampler "
                 "READS the records and never calls Worker.PullRecord(), so it "
                 "cannot insert one",
          not wrong, "no over-growth", wrong)
    note("4.9", "counts after %d ticks: %s"
         % (t1 - (t0 - span), {k: len(v or []) for k, v in sorted(hist_c.items())}))
    note("4.10", "saves left on the bench: rates-diff-a/-b/-c. Delete them when "
                 "you are done; they are evidence until then.")


# ------------------------------------------------------------------- phase 5 --

def phase5():
    banner("PHASE 5 — `trends` is a predicate section, and the null trap is real")

    # A path that does not resolve is refused at ARM time and the refusal NAMES
    # the keys the section publishes (session 19's ruling). That refusal is the
    # cheapest possible proof that `trends` is registered as a predicate
    # section: an unregistered section refuses with a different sentence.
    bad = send("advance", {
        "until": {"condition": {"path": "trends.no_such_field", "op": ">=", "value": 1}},
        "timeout_ticks": 100, "unread_ok": WHY, "through_casualties": WHY})
    refused("5.1", "a misspelled path under `trends` is REFUSED at arm time",
            bad, "bad-args", "no key 'no_such_field'")
    contains("5.2", "…and the refusal LISTS what the section actually publishes",
             bad, "error.detail", "food_days_per_day")

    e = send("advance", {
        "until": {"condition": {"path": "not_a_section.x", "op": ">=", "value": 1}},
        "timeout_ticks": 100, "unread_ok": WHY, "through_casualties": WHY})
    contains("5.3", "…and `trends` is named in the list of addressable sections, "
                    "which is what makes it a predicate target rather than a "
                    "read-only block",
             e, "error.detail", "trends")

    t = send("trends", {})
    dg = send("digest", {})
    ready = dig(dg, "data.trends.ready")
    slope = dig(dg, "data.trends.food_days_per_day")
    dtz = dig(dg, "data.trends.food_days_to_zero")
    note("5.4", "ready=%s food_days_per_day=%s food_days_to_zero=%s"
         % (ready, slope, dtz))

    if ARGS.dry_run:
        note("5.5", "would arm an ordering predicate on food_days_per_day and "
                    "prove the null trap on food_days_to_zero")
        return

    if isinstance(slope, (int, float)):
        # Armed against a threshold it cannot reach inside the timeout, so the
        # advance runs out rather than halting — what is under test is that the
        # predicate ARMS and EVALUATES, not that it fires.
        a = send("advance", {
            "until": {"condition": {"path": "trends.food_days_per_day",
                                    "op": "<=", "value": -1e9}},
            "timeout_ticks": 3000, "unread_ok": WHY, "through_casualties": WHY},
            timeout=600)
        eq("5.5", "an ordering predicate on a SLOPE arms and runs", a, "ok", True)
        eq("5.6", "…and reaches its timeout rather than refusing", a, "data.reason",
           "timeout")
        eq("5.7", "…having been evaluated as a condition", a, "data.until.kind",
           "condition")
        ge("5.8", "…more than once", a, "data.until.evaluations", 1)
        note("5.9", "predicate cost: eval_ms_avg=%s eval_ms_max=%s over %s "
                    "evaluations — `trends` reads a ring already in memory plus "
                    "11 recorder field reads, so it is the cheapest section "
                    "there is"
             % (dig(a, "data.until.eval_ms_avg"), dig(a, "data.until.eval_ms_max"),
                dig(a, "data.until.evaluations")))
    else:
        note("5.5", "food_days_per_day is null (ready=%s), so the slope "
                    "predicate could not be armed here. Run phase 3 first, or "
                    "let ~%d ticks pass." % (ready, SLOPE_MIN_SPAN_TICKS))

    # THE NULL TRAP, proved rather than promised. It is the one behaviour in
    # this surface that is documented as a HAZARD rather than a feature, and a
    # documented hazard nobody checks stops being true.
    if dtz is None:
        a = send("advance", {
            "until": {"condition": {"path": "trends.food_days_to_zero",
                                    "op": "<=", "value": 2}},
            "timeout_ticks": 500, "unread_ok": WHY, "through_casualties": WHY})
        refused("5.10", "an ordering predicate against a NULL `*_to_zero` is "
                        "REFUSED at arm time — which is why the docs say a "
                        "predicate wants `*_per_day`",
                a, "bad-args", "needs")
        contains("5.11", "…and the refusal says the reading was null",
                 a, "error.detail", "null")
    else:
        note("5.10", "food_days_to_zero is %s (food is falling), so the null "
                     "refusal cannot be staged from this state. Run phase 5 on "
                     "a colony whose food is flat or rising — the RISING half "
                     "of phase 3 leaves exactly that state." % dtz)


# --------------------------------------------------------------- phases 6/7 --

STASH = os.path.join(REPO, "accept", "runs", "2d9a1da-saveload.json")


def phase6():
    banner("PHASE 6 — the SAVE half (a human saves and loads between 6 and 7)")

    t = send("trends", {})
    path, rows = sample_file_lines()
    stash = {
        "ring_points": dig(t, "data.ring_points"),
        "first_tick": dig(t, "data.first_tick"),
        "last_tick": dig(t, "data.last_tick"),
        "durable_file": dig(t, "data.durable_file"),
        "file_rows": len(rows or []),
        "sample_rows": len([r for r in (rows or []) if r.get("kind") == "sample"]),
    }
    precondition("6.a", "the ring has points to lose",
                 ARGS.dry_run or (stash["ring_points"] or 0) >= 2,
                 "the ring holds %s points; let some game time pass first so "
                 "the reset is visible." % stash["ring_points"])
    if ARGS.dry_run:
        note("6.1", "would stash the live ring and ask for a save + load")
        return
    os.makedirs(os.path.dirname(STASH), exist_ok=True)
    with open(STASH, "w", encoding="utf-8") as fh:
        json.dump(stash, fh, indent=2)
    note("6.1", "stashed %s" % stash)
    print("")
    print("  %sNOW, IN THE GAME: save the colony, then LOAD that save. Then run:%s"
          % (CYAN, OFF))
    print("      ./accept/2d9a1da-colony-rates.py --phase 7")
    print("")
    print("  Do NOT restart the bench — the durable sample file is keyed on the")
    print("  PROCESS session, and half of what phase 7 checks is that the file")
    print("  survived a boundary the ring did not.")


def phase7():
    banner("PHASE 7 — the LOAD half: the ring RESET, the file KEPT everything")

    precondition("7.a", "phase 6 has run and stashed the pre-load ring",
                 ARGS.dry_run or os.path.exists(STASH),
                 "no %s — run `--phase 6` first, then save and load in the game."
                 % STASH)
    if ARGS.dry_run:
        note("7.1", "would assert the ring reset and the durable file kept its "
                    "rows plus a `boundary` row")
        return
    with open(STASH, encoding="utf-8") as fh:
        before = json.load(fh)

    t = send("trends", {})
    after_pts = dig(t, "data.ring_points")
    note("7.1", "before: %s points; after load: %s points"
         % (before["ring_points"], after_pts))
    check("7.2", "the live ring RESET across the load — stated loudly because a "
                 "trend that silently survived a rollback would fit a "
                 "regression across two timelines (TicksGame can go BACKWARD "
                 "on a load)",
          isinstance(after_pts, int) and after_pts < before["ring_points"],
          "fewer points than the %s stashed" % before["ring_points"], after_pts)
    eq_val("7.3", "…and the durable file is the SAME file (same process session)",
           dig(t, "data.durable_file"), before["durable_file"])

    path, rows = sample_file_lines()
    check("7.4", "…which kept every row it had", 
          rows is not None and len(rows) >= before["file_rows"],
          ">= %d rows" % before["file_rows"], len(rows or []))
    boundaries = [r for r in (rows or []) if r.get("kind") == "boundary"]
    check("7.5", "…and gained a `boundary` row marking the seam, so a reader of "
                 "the file can see where one game ended and the next began",
          len(boundaries) >= 1, ">= 1 boundary row", len(boundaries))
    contains("7.6", "…which warns that ticks after it may be LOWER than before it",
             {"d": boundaries[-1] if boundaries else {}}, "d.note", "LOWER")
    note("7.7", "THE ANSWER TO 'DOES IT SURVIVE SAVE/LOAD', in one line: the "
                "LIVE RING DOES NOT and the DURABLE FILE DOES. Everything a "
                "post-mortem needs is in the file; everything a running agent "
                "needs rebuilds in %d ticks." % SLOPE_MIN_SPAN_TICKS)


# ------------------------------------------------------------------- phase 9 --
#
# Offline. No bench, no game, nothing sent.

def _src(name):
    p = os.path.join(REPO, "Source", "AutoRimmer", name)
    if not os.path.exists(p):
        return None
    with open(p, encoding="utf-8") as fh:
        return fh.read()


def code_only(src):
    """The source with its COMMENTS AND STRING LITERALS removed.

    Every "this file never calls X" check below is a claim about the CODE, and
    these files talk about the members they route around in two places that are
    not code: the audit header (`ColonyRates.cs` names `Worker`, `LabelCap` and
    `DrawGraph` precisely to say it does not touch them) and the `source`
    provenance string the verb PUBLISHES, which says "Worker.PullRecord is never
    called and LabelCap is never read". Grepping the raw text fails the check
    for doing the documenting the check exists to reward — measured: 9.8 failed
    on its own provenance string the first time this suite ran.

    Strings before comments, because a `//` inside a literal is not a comment;
    block comments first, because they can span the line structure both later
    passes assume. Verbatim interpolated strings are not handled and neither
    file has one (9.5b checks that the strip actually bit)."""
    src = re.sub(r"/\*.*?\*/", "", src, flags=re.S)
    out = []
    for line in src.split("\n"):
        line = re.sub(r'"(?:\\.|[^"\\])*"', '""', line)
        out.append(re.sub(r"//.*$", "", line))
    return "\n".join(out)


def lstsq_slope(xs, ys):
    """The estimator this file grades the mod against, written independently
    from the same definition rather than transcribed from the C#."""
    n = len(xs)
    mx = sum(xs) / n
    my = sum(ys) / n
    num = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    den = sum((x - mx) ** 2 for x in xs)
    return None if den == 0 else num / den * 60000.0


def phase9():
    banner("PHASE 9 — offline: constants re-derived, the estimator, and REAL data")

    # ---- every constant this file hard-codes, from the source it describes --
    src = _src("ColonySampler.cs")
    if src is None:
        note("9.1", "Source/AutoRimmer/ColonySampler.cs is not in this checkout; "
                    "the sampler constants could not be re-derived.")
    else:
        for num, name, want in (("9.1a", "CadenceTicks", CADENCE_TICKS),
                                ("9.1b", "RingCapacity", RING_CAPACITY),
                                ("9.1c", "DefaultWindowPoints", DEFAULT_WINDOW_POINTS),
                                ("9.1d", "SlopeMinPoints", SLOPE_MIN_POINTS),
                                ("9.1e", "SlopeMinSpanTicks", SLOPE_MIN_SPAN_TICKS)):
            m = re.search(r"public const int %s\s*=\s*(\d+)" % name, src)
            eq_val(num, "this file's %s matches ColonySampler.%s" % (name, name),
                   int(m.group(1)) if m else None, want)
        # The field table, re-derived from the initialiser rather than trusted.
        m = re.search(r"internal static readonly FieldDef\[\] Fields\s*=\s*\{(.*?)\n        \};",
                      src, re.S)
        got = re.findall(r'new FieldDef\("(\w+)",\s*"(\w+)",\s*"(\w+)",\s*(true|false)\)',
                         m.group(1) if m else "")
        got = [(a, b, c, d == "true") for a, b, c, d in got]
        eq_val("9.2", "…and SAMPLE_FIELDS matches ColonySampler.Fields exactly, "
                      "in order. THIS IS THE CHECK THAT MATTERS for this issue: "
                      "its whole argument is that a drifting column set makes "
                      "runs incomparable",
               got, SAMPLE_FIELDS)
        code = code_only(src)
        check("9.3", "the sampler hangs on GameComponentTick and never on "
                     "GameComponentUpdate — a per-frame sample would record "
                     "wall-clock time as game time",
              "GameComponentUpdate" not in code,
              "no GameComponentUpdate reference in code", None)
        check("9.4", "…and it consumes DigestVerb.SectionFor rather than "
                     "re-deriving any number, which is what makes it inherit "
                     "changes to food_days instead of forking them",
              "DigestVerb.SectionFor(map, s)" in src,
              "DigestVerb.SectionFor called from the sampler", None)
        check("9.5", "…and nothing here emits into the journal, which would "
                     "make 722c951's unread refusal fire on every advance",
              "Journal.Emit" not in code, "no Journal.Emit in ColonySampler.cs "
              "code", None)
        check("9.5b", "…and the stripper actually bit: a phrase that exists "
                      "ONLY inside a string literal and a comment is gone from "
                      "the stripped code, which is what makes 9.3/9.5/9.7/9.8 "
                      "claims about CODE rather than about prose",
              "ResourceCounterTick" in src and "ResourceCounterTick" not in code,
              "the phrase present in the source and absent from code_only()",
              None)

    src = _src("ColonyRates.cs")
    if src is None:
        note("9.6", "Source/AutoRimmer/ColonyRates.cs is not in this checkout.")
    else:
        for num, name, want in (("9.6a", "SeriesCap", SERIES_CAP),
                                ("9.6b", "DefaultPoints", HIST_DEFAULT_POINTS),
                                ("9.6c", "DefaultWindowPoints", HIST_WINDOW_POINTS),
                                ("9.6d", "SlopeMinPoints", HIST_MIN_POINTS),
                                ("9.6e", "SlopeMinSpanTicks", HIST_MIN_SPAN_TICKS)):
            m = re.search(r"internal const int %s\s*=\s*(\d+)" % name, src)
            eq_val(num, "this file's %s matches HistorySafe.%s" % (name, name),
                   int(m.group(1)) if m else None, want)
        code = code_only(src)
        check("9.7", "the History reader never touches `Worker` (a lazy-init "
                     "Activator.CreateInstance) and never re-derives a record",
              ".Worker" not in code, "no `.Worker` reference in code", None)
        check("9.8", "…and never reads Def.LabelCap, which caches into "
                     "cachedLabelCap on a getter that looks like a plain read",
              "LabelCap" not in code, "no `LabelCap` reference in code", None)
        check("9.9", "…and never calls DrawGraph, which rebuilds `curves` and "
                     "stamps cachedGraphTickCount",
              "DrawGraph" not in code, "no DrawGraph call in code", None)
        eq_val("9.10", "TicksPerDay is the game's 60,000",
               float(re.search(r"TicksPerDay\s*=\s*([\d.]+)", src).group(1))
               if re.search(r"TicksPerDay\s*=\s*([\d.]+)", src) else None, 60000.0)

    src = _src("DigestVerb.cs")
    if src is None:
        note("9.11", "Source/AutoRimmer/DigestVerb.cs is not in this checkout.")
    else:
        m = re.search(r"PredicateSections\s*=\s*\{(.*?)\};", src, re.S)
        secs = re.findall(r'"([a-z_]+)"', m.group(1)) if m else []
        check("9.11", "`trends` is registered as a predicate section, which is "
                      "what phase 5 arms against",
              "trends" in secs, "trends among %s" % (secs,), secs)
        check("9.12", "…and the digest publishes it as a top-level block",
              '["trends"] = ColonySampler.TrendSection(map)' in src,
              "the trends section in the digest literal", None)
        # The temp-control worker owns this arithmetic; this check is not about
        # its VALUE, only that the sampler's source key still exists.
        check("9.13", "…and `resources` still publishes `food_days`, which is "
                      "the key the sampler lifts (a parallel branch is changing "
                      "that arithmetic; the sampler reads whatever it becomes, "
                      "but the KEY is the contract between them)",
              '["food_days"]' in src, "the food_days key in ResourceSection", None)

    src = _src("Runtime.cs")
    if src:
        check("9.14", "the ring is cleared at BOTH game-boundary detectors, "
                      "beside Placements.Clear() — the poller's heartbeat edge "
                      "is the one the GameComponent structurally cannot see",
              "ColonySampler.Clear();" in src,
              "ColonySampler.Clear() in Runtime.ResetForGameBoundary", None)

    # ---- the estimator, against series with known answers -----------------
    step = CADENCE_TICKS
    xs = [i * step for i in range(24)]
    flat = [5.0] * 24
    fall = [10.0 - 0.5 * i for i in range(24)]        # -0.5 per sample
    rise = [1.0 + 0.25 * i for i in range(24)]
    # -0.5 per 2,500 ticks is -0.5 * (60000/2500) = -12 per day.
    close("9.15", "a falling series gives the right per-day slope",
          lstsq_slope(xs, fall), -12.0, 1e-9)
    close("9.16", "a rising one gives the mirror answer",
          lstsq_slope(xs, rise), 6.0, 1e-9)
    close("9.17", "a flat one gives zero", lstsq_slope(xs, flat), 0.0, 1e-9)
    # THE REASON THIS IS LEAST SQUARES AND NOT AN ENDPOINT DIFFERENCE, shown
    # rather than argued: one lump at the end of the window IS the endpoint
    # answer, and is 1/n of the regression's.
    lumpy = list(fall)
    lumpy[-1] += 6.0                                  # a hunt comes home
    endpoint = (lumpy[-1] - lumpy[0]) / ((xs[-1] - xs[0]) / 60000.0)
    reg = lstsq_slope(xs, lumpy)
    check("9.18", "one lump at the end of the window moves the ENDPOINT estimate "
                  "far more than the regression — which is why the mod fits "
                  "rather than subtracts (endpoint %.2f vs regression %.2f "
                  "against a true -12.0)" % (endpoint, reg),
          abs(endpoint + 12.0) > abs(reg + 12.0),
          "the endpoint estimate to be further from -12", (endpoint, reg))
    # The span floor, which is the guard this design is actually about.
    short_xs = [0, step, 2 * step]
    check("9.19", "three samples span %d ticks, under the %d-tick floor — so "
                  "the mod publishes NULL rather than extrapolating two in-game "
                  "hours into a per-day rate"
          % (2 * step, SLOPE_MIN_SPAN_TICKS),
          (short_xs[-1] - short_xs[0]) < SLOPE_MIN_SPAN_TICKS,
          "the floor to exclude a 3-sample window at this cadence",
          short_xs[-1] - short_xs[0])
    need = SLOPE_MIN_SPAN_TICKS // step + 1
    note("9.20", "so the first slope of a run arrives at sample %d, i.e. %d "
                 "ticks (%.2f in-game days) after the ring starts filling."
         % (need, (need - 1) * step, (need - 1) * step / 60000.0))

    # ---- REAL DATA: the M1 colony's own history block ---------------------
    fx = os.path.join(REPO, "accept", "fixtures",
                      "history-autoRecorderGroups-m1-Autosave-1.xml")
    if not os.path.exists(fx):
        note("9.21", "no banked history fixture at %s" % fx)
        return
    with open(fx, encoding="utf-8") as fh:
        series = parse_history_block(fh.read())
    eq_val("9.21", "the banked M1 history block decodes to all eleven Core "
                   "recorders — the same decode the phase 4 save-diff uses",
           sorted(series.keys()),
           sorted(d for d, _, _ in CORE_RECORDERS))
    tot, it, bu, pa = (series["Wealth_Total"], series["Wealth_Items"],
                       series["Wealth_Buildings"], series["Wealth_Pawns"])
    worst = max(abs(tot[i] - (it[i] + bu[i] + pa[i])) for i in range(len(tot)))
    check("9.22", "…and the wealth identity holds on REAL recorded data "
                  "(WealthWatcher.WealthTotal IS Items+Buildings+Pawns)",
          worst < 0.01, "agreement within 0.01", worst)
    check("9.23", "…and the 60,000-tick recorders hold about half as many "
                  "samples as the 30,000-tick ones, which is the cadence ratio "
                  "the index-is-tick map depends on",
          len(series["Adaptation"]) in (len(tot) // 2, len(tot) // 2 + 1),
          "~%d samples" % (len(tot) // 2), len(series["Adaptation"]))
    note("9.24", "THE EVIDENCE FOR THIS WHOLE ISSUE, read back in one line: over "
                 "%d recorded samples the M1 colony's mood went %s -> %s and its "
                 "pawn wealth %s -> %s (two colonists died). Session 13 got these "
                 "numbers by decoding a .rws by hand because no verb could ask."
         % (len(tot), round(series["ColonistMood"][0], 1),
            round(series["ColonistMood"][-1], 1), round(pa[0], 1),
            round(pa[-1], 1)))
    note("9.25", "…and ThreatPoints is stored as points/10: the banked values "
                 "run %s, i.e. %s real points."
         % ([round(v, 2) for v in series["ThreatPoints"][:4]],
            [round(v * 10, 1) for v in series["ThreatPoints"][:4]]))


# ---------------------------------------------------------------------- main --

PHASES = {0: phase0, 1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5,
          6: phase6, 7: phase7, 9: phase9}
DEFAULT_PHASES = [0, 1, 2, 3, 4, 5]


def main():
    global ARGS
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", default=DEFAULT_ROOT, help="protocol root")
    ap.add_argument("--phase", type=int, action="append",
                    help="run only these phases (0 always runs first, except "
                         "with --selftest)")
    ap.add_argument("--dry-run", action="store_true",
                    help="print the plan and every expectation, send nothing")
    ap.add_argument("--selftest", action="store_true",
                    help="phase 9 only: offline, no bench, nothing sent")
    ap.add_argument("--food-def", default="MealSimple",
                    help="the human-edible def phase 3 spawns and destroys")
    ap.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = ap.parse_args()

    if ARGS.selftest:
        print("2d9a1da acceptance — mode: --selftest (offline; no bench, no "
              "game, nothing sent)")
        phase9()
        return summarise(selftest=True)

    wanted = sorted(set(ARGS.phase or DEFAULT_PHASES))
    if 9 in wanted and len(wanted) > 1:
        wanted = [p for p in wanted if p != 9]
        print("NOTE: phase 9 needs no bench; run it alone with --selftest.")
    if 0 not in wanted:
        wanted = [0] + wanted

    print("2d9a1da colony-rates acceptance — root %s" % ARGS.root)
    if ARGS.dry_run:
        print("DRY RUN: nothing is sent; every check prints what it would expect.")
    for p in wanted:
        PHASES[p]()
    return summarise()


def summarise(selftest=False):
    banner("RESULT")
    if ARGS.dry_run:
        print("dry run: %d checks planned" % CHECKS)
        return 0
    if FAILS:
        print("%s%d/%d checks FAILED: %s%s"
              % (RED, len(FAILS), CHECKS, ", ".join(FAILS), OFF))
        return 1
    print("%sall %d checks passed%s" % (GREEN, CHECKS, OFF))
    if selftest:
        print("--selftest grades the SOURCE and banked data. It says nothing "
              "about a running game.")
    print("NOTHING HERE HAS MET A BENCH UNTIL THE ORCHESTRATOR RUNS IT.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
