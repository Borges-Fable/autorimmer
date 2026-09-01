#!/usr/bin/env python3
"""Acceptance runner for fc287ba / 36999fd / 54b0c9a — the session-19 round.

Same protocol, helpers and exit codes as `accept/1adc737-place-layout.py`; read
that file's header first, especially the SHAPE CONTRACT note — `eq(..., None)`
passes on an absent key, so phase 0 proves every dig path before any later phase
leans on it.

    ./accept/fc287ba-until-state.py             # everything
    ./accept/fc287ba-until-state.py --phase 4   # one phase (0 always runs)
    ./accept/fc287ba-until-state.py --dry-run   # print the plan, send nothing

Start the bench first (`_RimWorld-Agent/run-agent.sh --quicktest`) with a colony
on a map that has open buildable ground and loose wood, and leave it paused.

WHAT THIS IS TESTING, in one sentence: an agent can say "advance until it's
done" — and every number in the transcript is either the agent's own bound or
the game's own answer, never a guess.

THREE ISSUES, ONE ROUND, because they interlock. `36999fd` gives a layout a
scope an observer can address; `fc287ba` halts on it; `54b0c9a` is the material
answer both of them report. They are one DLL and one branch, so they are one
suite.

  * PHASE 1 — 36999fd. `construction {layout_id}` answers for one transaction,
    proven with THREE layouts in flight on one map: one built (instant), one
    cancelled, one live. The built/cancelled pair is the case a rect-scoped read
    cannot answer at all, because both are empty ground. Plus the refusals: an
    unknown id, a second scope, and the near-miss spelling.
  * PHASE 2 — 54b0c9a. The bill on a map with loose, unzoned wood: `shortfall`
    EMPTY, `available` and `in_stockpiles` both published and different.
    Forbidden material is a shortfall that SAYS forbidden; a def with nothing on
    the map at all is a shortfall whose `short_by` is the whole bill.
  * PHASE 3 — fc287ba, the clock. `advance until time.hour` halts on a real
    crossing, and the EDGE is proved both ways on a predicate that is true the
    moment it is armed.
  * PHASE 4 — fc287ba, the headline. Place the M2 bedroom, prove
    `construction.frames == 0` is TRUE at that moment (the false-at-start case,
    measured rather than argued), then `advance {until:{layout}}` and show it
    did not halt there — it halted with 22 elements built.
  * PHASE 5 — fc287ba, the failure report. A layout whose material does not
    exist on the map times out and names every unresolved element AND its state,
    which is what a fixed-tick advance never gave.

WHAT THIS SUITE DELIBERATELY DOES NOT PROVE. That the room reports
`role: "Bedroom"` — that is M2 and was met in session 18. That a predicate over
`resources.food_days` leads `Alert_LowFood` — that needs a colony driven into
starvation over 2.5 in-game days, which is a play-loop fixture and not a
placement one; the divisor arithmetic it turns on is recorded in `DigestVerb`'s
`ResourceSection` header instead.

IT LEAVES BUILDINGS ON THE BENCH. Phases 1 and 4 place and build real rooms.
Run it on a bench you are willing to dirty.

PHASE 4 IS SLOW BY CONSTRUCTION — it waits for colonists to build 22 elements,
which took 3,180 ticks in session 18. That is the point: the suite does not know
how long, and does not have to.

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

# Row 0 is NORTH (baseviz/ir.py's pinned docstring, templates/INDEX.md pin 0).
# The same 5x7 rehearsal accept/1adc737 uses, inline for the same reason: every
# file in accept/ stands alone and runs from a bare checkout.
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

# A 2x2 of walls: small enough to place four of them on one map, big enough that
# `built` vs `cancelled` vs `live` are four elements each and not one.
TINY = [["Wall", "Wall"], ["Wall", "Wall"]]
TW, TH = 2, 2

MARGIN = 2
STUFF = "WoodLog"
# The material with nothing of it on a temperate-forest quicktest map. Phase 2's
# "genuinely absent" case and phase 5's never-finishing layout both turn on it,
# and phase 2 CHECKS the premise rather than assuming it.
ABSENT_STUFF = "Plasteel"
ROT_WORDS = ("North", "East", "South", "West")


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=240):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "accfc287ba-%03d-%s" % (SEQ, slug)
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
    `short_by` is DELIBERATELY absent when there is no shortfall."""
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
    return "null" if v is None else json.dumps(v, separators=(",", ":"))[:400]


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


def bad_args(num, what, env, needle=None):
    """A REFUSAL IS THE ASSERTION. `ok:false` with code bad-args, and when a
    needle is given, a sentence that actually names the problem — a refusal with
    a useless message is only half the fix."""
    code = dig(env, "error.code")
    ok = dig(env, "ok") is False and code == "bad-args"
    if ok and needle is not None:
        detail = dig(env, "error.detail") or ""
        ok = needle.lower() in detail.lower()
    check(num, what, ok, "ok:false, code bad-args%s"
          % ("" if needle is None else ", detail naming %r" % needle),
          {"ok": dig(env, "ok"), "code": code, "detail": (dig(env, "error.detail") or "")[:300]})


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
    if "_" in tok:
        head, _, tail = tok.rpartition("_")
        if head and tail in ROT_WORDS:
            return head, tail
    return tok, None


def expand(grid, origin, w, h):
    """The grid, resolved against a SOUTH-WEST origin, as the verb's `elements`.
    Row 0 is north, so grid row r is map z = oz + h - 1 - r."""
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


def place(grid, origin, w, h, name, mode="blueprint", stuff=STUFF, **extra):
    args = {
        "elements": expand(grid, origin, w, h),
        "origin": list(origin),
        "size": [w, h],
        "mode": mode,
        "name": name,
        "stuff_map": {"*": stuff},
    }
    args.update(extra)
    return send("place-layout", args)


def find_site(num, what, w, h):
    e = send("find-rect", {"w": w + 2 * MARGIN, "h": h + 2 * MARGIN, "max": 3,
                           "require": ["buildable", "unroofed"]})
    at = dig(e, "data.candidates.0.at")
    if ARGS.dry_run:
        at = [100, 100]
    precondition(num, what, isinstance(at, list) and len(at) == 2,
                 "find-rect found no clear %dx%d box — load a colony with open "
                 "buildable ground" % (w + 2 * MARGIN, h + 2 * MARGIN))
    origin = [at[0] + MARGIN, at[1] + MARGIN]
    print("  %ssite: rect at %s, layout origin %s%s" % (DIM, at, origin, OFF))
    return origin


def sites(num, what, w, h, n):
    """N NON-OVERLAPPING sites, which phase 1 needs because "three layouts in
    flight on one map" is the whole assertion.

    `find-rect` spirals out from ONE point (`near`, default the map centre) and
    caps at 20 candidates, so a single call returns twenty boxes that all
    overlap each other. The probe therefore MOVES `near` and asks again, and
    rejects any answer that collides with one already taken."""
    if ARGS.dry_run:
        return [[100 + 20 * i, 100] for i in range(n)]
    box = max(w, h) + 2 * MARGIN
    step = box * 3
    taken = []
    probes = [None]
    for ring in (1, 2, 3):
        for dx, dz in ((1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1)):
            probes.append((dx * step * ring, dz * step * ring))
    anchor = None
    for probe in probes:
        args = {"w": box, "h": box, "max": 20, "require": ["buildable", "unroofed"]}
        if probe is not None:
            if anchor is None:
                break
            args["near"] = "%d,%d" % (anchor[0] + probe[0], anchor[1] + probe[1])
        e = send("find-rect", args)
        for c in as_list(dig(e, "data.candidates")):
            at = c.get("at") if isinstance(c, dict) else None
            if not (isinstance(at, list) and len(at) == 2):
                continue
            if anchor is None:
                anchor = at
            if any(abs(at[0] - t[0]) < box + 2 and abs(at[1] - t[1]) < box + 2 for t in taken):
                continue
            taken.append(at)
            break
        if len(taken) >= n:
            break
    precondition(num, what, len(taken) >= n,
                 "found %d non-overlapping %dx%d boxes, needed %d — the map is too "
                 "cluttered for this phase" % (len(taken), box, box, n))
    origins = [[t[0] + MARGIN, t[1] + MARGIN] for t in taken[:n]]
    print("  %s%d sites: %s%s" % (DIM, n, origins, OFF))
    return origins


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 - the bench, the verbs, and THE SHAPE CONTRACT")
    e = send("status")
    precondition("0.1", "the bench answers `status`",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "no status envelope - start _RimWorld-Agent/run-agent.sh")

    # THE WATERMARK. JournalVerbs.Read updates last_seq BEFORE the
    # `seq <= since_seq` skip, so `{limit:1}` reports the SECOND row's seq;
    # pushing since_seq past the end reads to the end and yields the true max.
    e = send("journal", {"since_seq": 999999999, "limit": 1})
    shape("0.2", "journal", e, "data.last_seq")
    S["seq0"] = dig(e, "data.last_seq") or 0
    print("  %sjournal watermark: seq0=%s%s" % (DIM, S["seq0"], OFF))

    # ---- the digest paths a predicate addresses, proved to EXIST -------------
    # This is the shape contract at its sharpest: a predicate over a path that
    # is not there would be refused at arm time (which is the fix), but a SUITE
    # that asserts against a path that is not there would go green forever.
    e = send("digest")
    S["digest"] = e
    shape("0.3a", "digest", e, "data.time.hour")
    shape("0.3b", "digest", e, "data.time.tick")
    shape("0.3c", "digest", e, "data.construction.frames")
    shape("0.3d", "digest", e, "data.construction.blueprints")
    shape("0.3e", "digest", e, "data.resources.food_days")
    shape("0.3f", "digest", e, "data.colonists.list", list)
    shape("0.3g", "digest", e, "data.power.gen_w")

    # THE REGISTRY IS `status.verbs`, NOT AN OP CALLED `verbs`. `rwa verbs` is a
    # CLI-side wrapper over `status`; sending the op raw answers `unknown-op`,
    # which is what accept/s13-mod-surface.py's two "missing verb" failures
    # actually were (RUNLOG session 18 confirmed them as the suite's own).
    e = send("status")
    registry = as_list(dig(e, "data.verbs"))
    check("0.4z", "`status` publishes the registry", len(registry) > 0,
          "a non-empty data.verbs list", registry[:5])
    for tag, verb in (("0.4a", "advance"), ("0.4b", "construction"),
                      ("0.4c", "place-layout"), ("0.4d", "cancel-layout")):
        check(tag, "the registry lists %s" % verb, verb in registry,
              "%s in the registry" % verb, None if verb in registry else len(registry))

    # ---- the refusals, which are cheap and prove the guard rails -------------
    banner("PHASE 0b - THE REFUSALS: an ignored argument is the bug being fixed")

    # 36999fd defect 2, the one that bites. `--layout_id` used to fall through
    # to whole-map with ok:true.
    e = send("construction", {"layout_id": "ly-nope-999"})
    bad_args("0.5a", "construction refuses an unknown layout id", e, "no layout")
    contains("0.5b", "…and names what this session DOES know", e, "error.detail", "session")
    e = send("construction", {"layout_id": "ly-1", "rect": [0, 0, 10, 10]})
    bad_args("0.5c", "layout_id beside rect is a refusal, not a precedence rule", e,
             "two different scopes")
    e = send("construction", {"layout": "ly-1"})
    bad_args("0.5d", "the near-miss spelling `layout` is refused", e, "did you mean")

    # fc287ba's own version of the same class, in the `until` parse.
    e = send("advance", {"until": {"conditon": {"path": "time.hour", "op": ">=", "value": 6}}})
    bad_args("0.6a", "a MISSPELLED matcher is refused, not silently ignored", e,
             "unknown key 'until.conditon'")
    e = send("advance", {"until": {"condition": {"path": "time.hour", "op": ">=", "value": 6},
                                   "layout": "ly-1"}})
    bad_args("0.6b", "two matchers in one advance is a refusal", e, "ONE matcher")
    e = send("advance", {"until": {"condition": {"path": "resources.food_dayz",
                                                 "op": "<", "value": 3}}})
    bad_args("0.6c", "a path that does not resolve is refused AT ARM TIME", e, "food_dayz")
    contains("0.6d", "…and names the keys that section really publishes", e,
             "error.detail", "food_days")
    e = send("advance", {"until": {"condition": {"path": "changed.since",
                                                 "op": "<", "value": 3}}})
    bad_args("0.6e", "`changed` is not a predicate section", e, "changed")
    e = send("advance", {"until": {"condition": {"path": "colonists[*].mood_pct",
                                                 "op": "<", "value": 50}}})
    bad_args("0.6f", "the issue's own example path is refused, because it is wrong", e,
             "not a list")
    e = send("advance", {"until": {"condition": {"path": "time.season",
                                                 "op": "<", "value": 3}}})
    bad_args("0.6g", "`<` on a string is refused rather than coerced", e, "not a number")
    e = send("advance", {"until": {"layout": "ly-nope-999"}})
    bad_args("0.6h", "an unknown layout id is refused before the clock is touched", e,
             "no layout")
    e = send("advance", {"until": {"condition": {"path": "time.hour", "op": ">=", "value": 6},
                                   "every_frames": 9999}})
    bad_args("0.6i", "an out-of-range cadence is refused", e, "every_frames")

    # THE CLOCK MUST NOT HAVE MOVED. Every refusal above happens at arm time,
    # AFTER TimeDriver.Start has already set the game's speed — so a refusal
    # that forgot to put it back would leave the colony running unattended,
    # which is the one failure the turn-based contract must not ship.
    e = send("digest")
    eq("0.7a", "every refusal left the game PAUSED", e, "data.time.paused", True)
    eq("0.7b", "…at speed Paused, not merely force-paused", e, "data.time.speed", "Paused")
    tick_before = dig(S["digest"], "data.time.tick")
    tick_after = dig(e, "data.time.tick")
    check("0.7c", "…and the tick did not advance across nine refusals",
          tick_before == tick_after, "tick unchanged at %s" % tick_before, tick_after)


# ------------------------------------------------------------------- phase 1 --

def phase1():
    banner("PHASE 1 - 36999fd: a layout is a SCOPE, and three of them prove it")

    origins = sites("1.0", "three non-overlapping sites for three layouts", TW, TH, 3)

    # (a) BUILT — instant mode resolves every element the moment it returns.
    e = place(TINY, origins[0], TW, TH, "acc-built", mode="instant")
    eq("1.1a", "the instant layout placed all four", e, "data.placed_count", 4)
    built_id = dig(e, "data.layout_id")
    precondition("1.1b", "the instant layout has an id", bool(built_id) or ARGS.dry_run,
                 "place-layout returned no layout_id: %s" % show(e))

    # (b) CANCELLED — placed, then undone.
    e = place(TINY, origins[1], TW, TH, "acc-cancelled")
    cancelled_id = dig(e, "data.layout_id")
    eq("1.2a", "the second layout placed all four", e, "data.placed_count", 4)
    e = send("cancel-layout", {"layout_id": cancelled_id})
    eq("1.2b", "and cancel-layout took all four", e, "data.cancelled", 4)

    # (c) LIVE — placed and left alone.
    e = place(TINY, origins[2], TW, TH, "acc-live")
    live_id = dig(e, "data.layout_id")
    eq("1.3a", "the third layout placed all four", e, "data.placed_count", 4)

    S["live_id"] = live_id

    # ---- the shape, once, on the live one -----------------------------------
    e = send("construction", {"layout_id": live_id})
    S["live_read"] = e
    shape("1.4a", "construction", e, "data.layout_id")
    shape("1.4b", "construction", e, "data.rect_source")
    shape("1.4c", "construction", e, "data.elements")
    shape("1.4d", "construction", e, "data.built")
    shape("1.4e", "construction", e, "data.cancelled")
    shape("1.4f", "construction", e, "data.resolved")
    shape("1.4g", "construction", e, "data.unresolved")
    shape("1.4h", "construction", e, "data.done")
    shape("1.4i", "construction", e, "data.by_state", dict)
    shape("1.4j", "construction", e, "data.items", list)
    shape("1.4k", "construction", e, "data.items.0.placement_id")
    shape("1.4l", "construction", e, "data.items.0.state")
    shape("1.4m", "construction", e, "data.items.0.resolved")
    eq("1.4n", "the answer NAMES the question it answered", e, "data.rect_source", "layout")

    # ---- and now the three answers, which must differ -----------------------
    e = send("construction", {"layout_id": built_id})
    eq("1.5a", "the instant layout reads DONE", e, "data.done", True)
    eq("1.5b", "with four built", e, "data.built", 4)
    eq("1.5c", "and none cancelled", e, "data.cancelled", 0)
    eq("1.5d", "and nothing outstanding", e, "data.unresolved", 0)

    e = send("construction", {"layout_id": cancelled_id})
    eq("1.6a", "the cancelled layout ALSO reads done", e, "data.done", True)
    eq("1.6b", "…but with four CANCELLED", e, "data.cancelled", 4)
    eq("1.6c", "…and zero built", e, "data.built", 0)
    note("1.6d", "built vs cancelled is the pair a rect-scoped read cannot tell "
                 "apart at all: both are empty ground")

    e = send("construction", {"layout_id": live_id})
    eq("1.7a", "the live layout is NOT done", e, "data.done", False)
    eq("1.7b", "with four unresolved", e, "data.unresolved", 4)
    eq("1.7c", "and zero built", e, "data.built", 0)
    ge("1.7d", "…and its blueprints are counted", e, "data.blueprints", 1)

    # THE SCOPE ACTUALLY FILTERED. The whole-map read sees the live layout's
    # blueprints; the BUILT layout's read must not — the number being right by
    # luck when only one layout is on the map is exactly the defect.
    whole = send("construction", {})
    eq("1.8a", "whole-map still says whole-map", whole, "data.rect_source", "whole-map")
    wb = dig(whole, "data.blueprints", 0)
    lb = dig(e, "data.blueprints", 0)
    check("1.8b", "the layout read is a SUBSET of the whole-map read",
          isinstance(wb, int) and isinstance(lb, int) and 0 < lb <= wb,
          "0 < layout blueprints <= whole-map blueprints", {"layout": lb, "whole_map": wb})

    e = send("construction", {"layout_id": built_id})
    eq("1.8c", "and the BUILT layout reports no blueprints of its own", e,
       "data.blueprints", 0)

    # Leave the map as we found it, so phase 4 sites cleanly.
    send("cancel-layout", {"layout_id": live_id})


# ------------------------------------------------------------------- phase 2 --

def phase2():
    banner("PHASE 2 - 54b0c9a: short_by is the BUILDER's question")

    origin = find_site("2.0", "a clear box for the bill rehearsal", BW, BH)
    S["bill_origin"] = origin

    # ---- the premise, CHECKED rather than assumed ---------------------------
    # A quicktest map scatters its wood on unzoned ground AND FORBIDS it, which
    # is the fixture both halves of this phase want: forbidden first, then the
    # same map with the same wood unforbidden.
    e = send("things", {"def": STUFF, "detail": True, "detail_cap": 60})
    S["wood"] = e
    total = dig(e, "data.totals.count")
    forbidden = dig(e, "data.totals.forbidden")
    ids = [r.get("id") for r in as_list(dig(e, "data.things"))
           if isinstance(r, dict) and isinstance(r.get("id"), int)]
    zones = send("zones", {})
    stock = dig(zones, "data.stockpiles.total", 0)
    if ARGS.dry_run:
        total, forbidden, ids, stock = 300, 300, [1, 2], 0
    print("  %s%s on the map: %s in %s stacks, %s forbidden · stockpile zones: %s%s"
          % (DIM, STUFF, total, len(ids), forbidden, stock, OFF))
    precondition("2.1a", "there is loose %s on this map" % STUFF,
                 isinstance(total, int) and total > 0,
                 "no %s at all — this phase needs the material present. "
                 "`dev:starter-kit --preset survival` puts some down." % STUFF)
    precondition("2.1b", "this map has NO stockpile zone, as the M2 run's did not",
                 stock == 0,
                 "this map has %d stockpile zone(s); the finding is about a map "
                 "with none, where in_stockpiles is structurally zero" % stock)

    # ---- (a) FORBIDDEN is a shortfall, and it says which problem it is ------
    # STAGED, not observed: a quicktest map usually forbids its scattered wood,
    # but "usually" is not a fixture. Forbidding it here makes the case
    # deterministic whatever the map arrived in, and (b) unforbids it again.
    precondition("2.2z", "`things --def %s` returns addressable ids" % STUFF,
                 len(ids) > 0, "no ids in data.things")
    # ASSERT THE STATE, NOT THE DESIGNATOR'S DELTA. `Designator_Forbid
    # .CanDesignateThing` refuses a thing that is ALREADY forbidden, so on a map
    # whose quicktest debris arrived forbidden this call legitimately accepts
    # zero (git-bug 8b0b88f, which has a suite of its own). What this phase
    # needs is the state afterwards, so that is what it reads.
    send("forbid", {"things": ids})
    e = send("things", {"def": STUFF})
    fb, ct = dig(e, "data.totals.forbidden"), dig(e, "data.totals.count")
    check("2.2y", "every stack of %s is now forbidden" % STUFF,
          isinstance(fb, int) and fb == ct and ct > 0,
          "forbidden == count > 0", {"forbidden": fb, "count": ct})
    if True:
        e = place(BEDROOM, origin, BW, BH, "acc-bill-forbidden", dry_run=True)
        S["bill_forbidden"] = e
        shape("2.2a", "place-layout", e, "data.materials", list)
        shape("2.2b", "place-layout", e, "data.materials.0.count")
        shape("2.2c", "place-layout", e, "data.materials.0.available")
        shape("2.2d", "place-layout", e, "data.materials.0.in_stockpiles")
        shape("2.2e", "place-layout", e, "data.materials.0.availability_basis")
        shape("2.2f", "place-layout", e, "data.materials_basis.gate")
        eq("2.2g", "the bill cites the vanilla member it reproduces", e,
           "data.materials_basis.gate",
           "RimWorld/WorkGiver_ConstructDeliverResources.ResourceValidator")
        ge("2.2h", "…and names how many builders it asked", e,
           "data.materials_basis.builders", 1)
        sf = as_list(dig(e, "data.shortfall"))
        check("2.3a", "wood that is all FORBIDDEN is a shortfall", len(sf) > 0,
              "a non-empty shortfall[]", sf)
        if sf:
            ge("2.3b", "…and it says how much is forbidden", e, "data.shortfall.0.forbidden", 1)
            eq("2.3c", "…and available is zero", e, "data.shortfall.0.available", 0)
            contains("2.3d", "…and the hint names `unforbid`, not mining", e,
                     "data.shortfall.0.hint", "unforbid")

    # ---- (b) THE EXACT CASE THAT PRODUCED THE ISSUE -------------------------
    # Unforbidden, unzoned wood ten cells from the site. This is what reported
    # `short_by: 185` on the M2 run while the room was then built out of it.
    send("unforbid", {"things": ids})
    e = send("things", {"def": STUFF})
    eq("2.4b", "the wood is unforbidden again", e, "data.totals.forbidden", 0)

    e = place(BEDROOM, origin, BW, BH, "acc-bill", dry_run=True)
    S["bill"] = e
    need = dig(e, "data.materials.0.count")
    avail = dig(e, "data.materials.0.available")
    zoned = dig(e, "data.materials.0.in_stockpiles")
    print("  %sbill: needed=%s available=%s in_stockpiles=%s%s"
          % (DIM, need, avail, zoned, OFF))
    ge("2.5a", "the availability count now SEES the loose wood", e,
       "data.materials.0.available", 1)
    eq("2.5b", "…while the stockpile count is zero, as it was on the M2 run", e,
       "data.materials.0.in_stockpiles", 0)
    check("2.5c", "the two numbers are DIFFERENT, which is the whole finding",
          isinstance(avail, int) and avail != zoned,
          "available != in_stockpiles", {"available": avail, "in_stockpiles": zoned})
    eq("2.5d", "…and the basis says it asked a colonist, not a stockpile", e,
       "data.materials.0.availability_basis", "reachable-unforbidden-by-a-colonist")

    if isinstance(avail, int) and isinstance(need, int) and avail >= need:
        eq("2.5e", "with enough reachable wood, shortfall[] is EMPTY — the M2 case", e,
           "data.shortfall", [])
    else:
        note("2.5e", "only %s reachable %s against a bill of %s, so the "
                     "empty-shortfall case needs more material" % (avail, STUFF, need))
    S["bill_need"] = need
    S["bill_avail"] = avail

    # ---- (c) GENUINELY ABSENT is short by the whole bill --------------------
    e = send("things", {"def": ABSENT_STUFF})
    have = dig(e, "data.totals.count") or 0
    if ARGS.dry_run or have == 0:
        e = place(BEDROOM, origin, BW, BH, "acc-bill-absent", stuff=ABSENT_STUFF, dry_run=True)
        sf = as_list(dig(e, "data.shortfall"))
        check("2.6a", "a material with none on the map IS a shortfall", len(sf) > 0,
              "a non-empty shortfall[]", sf)
        if sf:
            eq("2.6b", "…and available is zero", e, "data.shortfall.0.available", 0)
            n = dig(e, "data.shortfall.0.needed")
            eq("2.6c", "…and short_by is the WHOLE bill", e, "data.shortfall.0.short_by", n)
            contains("2.6d", "…and the hint says genuinely short", e,
                     "data.shortfall.0.hint", "genuinely short")
            check("2.6e", "…and it is NOT reported as forbidden or unreachable",
                  not has_key(e, "data.shortfall.0.forbidden")
                  and not has_key(e, "data.shortfall.0.unreachable"),
                  "neither key present",
                  {"forbidden": dig(e, "data.shortfall.0.forbidden"),
                   "unreachable": dig(e, "data.shortfall.0.unreachable")})
    else:
        note("2.6", "this map has %s %s, so the absent case was not run"
                    % (have, ABSENT_STUFF))

    # ---- (d) the two verbs must AGREE ---------------------------------------
    e = place(BEDROOM, origin, BW, BH, "acc-bill-live")
    lid = dig(e, "data.layout_id")
    sf_place = as_list(dig(e, "data.shortfall"))
    c = send("construction", {"layout_id": lid})
    missing = as_list(dig(c, "data.missing"))
    print("  %splace-layout shortfall: %s · construction missing: %s%s"
          % (DIM, show(sf_place), show(missing), OFF))
    if missing:
        shape("2.7a", "construction", c, "data.missing.0.available")
        shape("2.7b", "construction", c, "data.missing.0.in_stockpiles")
        shape("2.7c", "construction", c, "data.materials_basis.gate")
        short_rows = [r for r in missing if isinstance(r, dict) and "short_by" in r]
        check("2.7d", "construction's missing[] agrees with place-layout's shortfall[]",
              (len(short_rows) > 0) == (len(sf_place) > 0),
              "both short or neither",
              {"place_layout": len(sf_place), "construction_short": len(short_rows)})
    else:
        note("2.7", "the blueprints are fully stocked, so `missing[]` is empty — "
                    "which is itself the agreement being asserted (shortfall[] is %s)"
                    % show(sf_place))
    send("cancel-layout", {"layout_id": lid})


# ------------------------------------------------------------------- phase 3 --

def phase3():
    banner("PHASE 3 - fc287ba: the CLOCK, and the edge")

    e = send("digest")
    hour = dig(e, "data.time.hour")
    tick0 = dig(e, "data.time.tick")
    precondition("3.0", "the digest publishes an hour", isinstance(hour, int) or ARGS.dry_run,
                 "digest.time.hour is %s" % show(hour))
    print("  %sclock: hour=%s tick=%s%s" % (DIM, hour, tick0, OFF))

    # ---- (a) THE EDGE, on a predicate that is true the moment it is armed ----
    # `hour >= 0` is true always. With the edge required it can NEVER fire, so
    # the advance must exit on its own timeout — and say that it was already
    # true when it was armed, which is the whole diagnosis.
    e = send("advance", {"until": {"condition": {"path": "time.hour", "op": ">=", "value": 0}},
                         "timeout_ticks": 400, "speed": "fast"}, timeout=300)
    S["edge_true"] = e
    shape("3.1a", "advance", e, "data.until", dict)
    shape("3.1b", "advance", e, "data.until.path")
    shape("3.1c", "advance", e, "data.until.true_when_armed")
    shape("3.1d", "advance", e, "data.until.evaluations")
    shape("3.1e", "advance", e, "data.until.eval_ms_avg")
    shape("3.1f", "advance", e, "data.until.eval_ms_per_frame")
    shape("3.1g", "advance", e, "data.until.every_frames")
    eq("3.1h", "an always-true predicate with the edge required does NOT halt on it",
       e, "data.reason", "timeout")
    eq("3.1i", "…and says it was already true when it was armed", e,
       "data.until.true_when_armed", True)
    eq("3.1j", "…and that it never saw a false reading", e, "data.until.saw_false", False)
    ge("3.1k", "…having actually evaluated it", e, "data.until.evaluations", 1)
    print("  %seval cost: %s ms/eval, %s ms/frame over %s frames%s"
          % (DIM, dig(e, "data.until.eval_ms_avg"), dig(e, "data.until.eval_ms_per_frame"),
             dig(e, "data.until.frames"), OFF))

    # ---- (b) the SAME predicate with edge:false halts at once ---------------
    e = send("advance", {"until": {"condition": {"path": "time.hour", "op": ">=", "value": 0,
                                                 "edge": False}},
                         "timeout_ticks": 20000, "speed": "fast"}, timeout=300)
    eq("3.2a", "…and with edge:false the same predicate halts immediately", e,
       "data.reason", "condition")
    eq("3.2b", "…naming the path that tripped it", e, "data.halted_on.path", "time.hour")
    shape("3.2c", "advance", e, "data.halted_on.observed")
    eq("3.2d", "…and identifying itself as a state halt", e, "data.halted_on.kind", "condition")
    check("3.2e", "a state halt publishes NO halted_seq (it names no journal line)",
          not has_key(e, "data.halted_seq"), "the key to be absent", dig(e, "data.halted_seq"))
    ticks = dig(e, "data.ticks_elapsed")
    check("3.2f", "…and it halted at once, not after the timeout",
          isinstance(ticks, int) and ticks < 2000, "< 2000 ticks", ticks)

    # ---- (c) A REAL CROSSING, with no tick count anywhere in the call --------
    e = send("digest")
    hour = dig(e, "data.time.hour")
    target = (hour + 1) % 24
    print("  %sadvancing until hour %s (it is %s) — no tick count is passed%s"
          % (DIM, target, hour, OFF))
    e = send("advance", {"until": {"condition": {"path": "time.hour", "op": "==",
                                                 "value": target}},
                         "timeout_ticks": 6000, "speed": "superfast"}, timeout=600)
    S["clock"] = e
    eq("3.3a", "the clock predicate halts on the crossing", e, "data.reason", "condition")
    eq("3.3b", "…reporting the value that tripped it", e, "data.halted_on.observed", target)
    eq("3.3c", "…and the operator it was asked with", e, "data.halted_on.op", "==")
    eq("3.3d", "…and it had to see a false reading first", e, "data.until.saw_false", True)
    e2 = send("digest")
    eq("3.3e", "the game's own clock agrees it is that hour", e2, "data.time.hour", target)
    eq("3.3f", "…and the advance left the game paused", e2, "data.time.paused", True)
    print("  %sit took %s ticks. THE SUITE NEVER NAMED THAT NUMBER.%s"
          % (DIM, dig(e, "data.ticks_elapsed"), OFF))


# ------------------------------------------------------------------- phase 4 --

def phase4():
    banner("PHASE 4 - fc287ba: advance until the ROOM IS BUILT")

    # ---- the fixture: colonists who can build, and material to build with ----
    e = send("pawns", {})
    colonists = [r for r in as_list(dig(e, "data.list"))
                 if isinstance(r, dict) and r.get("class") == "colonist"]
    precondition("4.0", "there are colonists on this map",
                 ARGS.dry_run or len(colonists) > 0,
                 "`pawns` reports %s of any class" % show(dig(e, "data.total")))
    print("  %scolonists: %s%s"
          % (DIM, ", ".join(str(c.get("name")) for c in colonists), OFF))

    # dev:*, and journaled as the god-hand it is. This is a FIXTURE — the spec
    # under test is the halt, not whether a colony can find its own wood — and
    # a run that spends itself on failed-construction rolls proves nothing
    # about `until`. Session 18 raised Construction to 8 for the same reason.
    e = send("dev:starter-kit", {"preset": "survival"})
    print("  %sstarter kit: ok=%s%s" % (DIM, dig(e, "ok"), OFF))
    send("unforbid", {"filter": {"category": "haulable", "forbidden": True}})
    for row in colonists:
        pid = row.get("id")
        if isinstance(pid, int):
            send("dev:set-skill", {"id": pid, "skill": "Construction", "level": 8})

    origin = find_site("4.1", "a clear box for the room", BW, BH)
    e = place(BEDROOM, origin, BW, BH, "acc-until-layout")
    lid = dig(e, "data.layout_id")
    eq("4.2a", "the bedroom went down as 22 blueprints", e, "data.placed_count", 22)
    precondition("4.2b", "the layout has an id", bool(lid) or ARGS.dry_run,
                 "place-layout returned no layout_id: %s" % show(e))
    eq("4.2c", "…with an empty shortfall, so it CAN be built", e, "data.shortfall", [])

    # ---- THE FALSE-AT-START CASE, MEASURED --------------------------------
    # This is the measurement the issue turns on: the natural spelling of
    # "wait until the build is done" is TRUE right now, on a room that does not
    # exist yet. A path predicate would halt here and report success.
    d = send("digest")
    frames = dig(d, "data.construction.frames")
    eq("4.3a", "RIGHT NOW `construction.frames == 0` is TRUE — the false-at-start case",
       d, "data.construction.frames", 0)
    ge("4.3b", "…while 22 blueprints stand on the map", d, "data.construction.blueprints", 22)
    c = send("construction", {"layout_id": lid})
    eq("4.3c", "…and the layout scope is NOT fooled: nothing is resolved", c,
       "data.unresolved", 22)
    eq("4.3d", "…and it is not done", c, "data.done", False)
    note("4.3e", "a `condition` on construction.frames would halt HERE, on a room "
                 "that does not exist. The layout scope is monotone; that path is not.")

    # ---- THE HALT ---------------------------------------------------------
    print("  %sadvancing until ly=%s is built. NO TICK COUNT IS PASSED — only a "
          "timeout bound.%s" % (DIM, lid, OFF))
    t0 = time.time()
    e = send("advance", {"until": {"layout": lid}, "timeout_ticks": 200000,
                         "speed": "superfast"}, timeout=1800)
    S["build"] = e
    eq("4.4a", "the advance halted on the LAYOUT, not on a timeout", e, "data.reason", "layout")
    eq("4.4b", "…and says which kind of halt it was", e, "data.halted_on.kind", "layout")
    eq("4.4c", "…with every element resolved", e, "data.halted_on.done", True)
    eq("4.4d", "…and all 22 of them BUILT", e, "data.halted_on.built", 22)
    eq("4.4e", "…none cancelled", e, "data.halted_on.cancelled", 0)
    ge("4.4f", "…and it did NOT halt at t=0", e, "data.ticks_elapsed", 500)
    shape("4.4g", "advance", e, "data.until.eval_ms_per_frame")
    print("  %sbuilt in %s ticks / %.1fs wall · predicate cost %s ms/frame over %s "
          "evaluations%s" % (DIM, dig(e, "data.ticks_elapsed"), time.time() - t0,
                             dig(e, "data.until.eval_ms_per_frame"),
                             dig(e, "data.until.evaluations"), OFF))

    # ---- the independent witness ------------------------------------------
    # Not the advance's own payload, which is the thing under test.
    c = send("construction", {"layout_id": lid})
    eq("4.5a", "an independent read agrees the layout is done", c, "data.done", True)
    eq("4.5b", "…with 22 built", c, "data.built", 22)
    eq("4.5c", "…and nothing outstanding", c, "data.unresolved", 0)
    eq("4.5d", "…and no blueprints of its own left", c, "data.blueprints", 0)

    # M2's own check, RECORDED rather than asserted: whether the game calls the
    # room a Bedroom is session 18's result and not this spec's. Reported by
    # `rooms` (which analyses every room) rather than `room-at`, whose argument
    # is `at` and which answers nothing for a cell inside a wall.
    r = send("rooms", {})
    mine = [x for x in as_list(dig(r, "data.list"))
            if isinstance(x, dict) and isinstance(x.get("at"), list)
            and origin[0] <= x["at"][0] < origin[0] + BW
            and origin[1] <= x["at"][1] < origin[1] + BH]
    if mine:
        m = mine[0]
        note("4.6", "the room the colonists built: role=%s proper=%s cells=%s "
                    "open_roof_cells=%s" % (m.get("role"), m.get("proper"),
                                            m.get("cells"), m.get("open_roof_cells")))
    else:
        note("4.6", "`rooms` found no enclosed room inside the layout's footprint")


# ------------------------------------------------------------------- phase 5 --

def phase5():
    banner("PHASE 5 - fc287ba: a layout that CANNOT finish, and what it says")

    e = send("things", {"def": ABSENT_STUFF})
    have = dig(e, "data.total") or dig(e, "data.count") or 0
    precondition("5.0", "there is no %s on this map" % ABSENT_STUFF,
                 ARGS.dry_run or have == 0,
                 "this map has %s %s, so a layout made of it could be built and "
                 "the timeout case cannot be staged" % (have, ABSENT_STUFF))

    origin = find_site("5.1", "a clear box for the impossible room", TW, TH)
    e = place(TINY, origin, TW, TH, "acc-impossible", stuff=ABSENT_STUFF)
    lid = dig(e, "data.layout_id")
    eq("5.2a", "the blueprints go down anyway — placement does not gate on material",
       e, "data.placed_count", 4)
    sf = as_list(dig(e, "data.shortfall"))
    check("5.2b", "…but the bill says it is short", len(sf) > 0,
          "a non-empty shortfall[]", sf)

    print("  %sadvancing until ly=%s finishes, which it never will%s" % (DIM, lid, OFF))
    e = send("advance", {"until": {"layout": lid}, "timeout_ticks": 4000,
                         "speed": "superfast"}, timeout=600)
    eq("5.3a", "a predicate that is never true exits on the TIMEOUT, not by hanging",
       e, "data.reason", "timeout")
    ge("5.3b", "…having run the full budget", e, "data.ticks_elapsed", 4000)
    # THE DIAGNOSIS, which is the reason this beats a fixed-tick advance.
    shape("5.4a", "advance", e, "data.until.unresolved_items", list)
    ge("5.4b", "…naming the elements that are still outstanding", e, "data.until.unresolved", 1)
    shape("5.4c", "advance", e, "data.until.unresolved_items.0.placement_id")
    shape("5.4d", "advance", e, "data.until.unresolved_items.0.state")
    eq("5.4e", "…and each one says WHY: it is waiting for materials", e,
       "data.until.unresolved_items.0.state", "awaiting-materials")
    eq("5.4f", "…and nothing was built", e, "data.until.built", 0)
    print("  %sunresolved: %s%s" % (DIM, show(dig(e, "data.until.unresolved_items")), OFF))

    send("cancel-layout", {"layout_id": lid})

    # ---- the standing invariant --------------------------------------------
    e = send("journal", {"since_seq": S["seq0"], "types": ["red_error"], "limit": 50})
    eq("5.5", "no red errors across the whole run", e, "data.count", 0)


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

    print("AutoRimmer acceptance - advance until:{condition|layout} (fc287ba),")
    print("                        construction --layout_id (36999fd),")
    print("                        short_by from availability (54b0c9a)")
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
