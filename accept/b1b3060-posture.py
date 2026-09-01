#!/usr/bin/env python3
"""Acceptance runner for b1b3060 — the posture verb and `digest.posture`.

Same protocol, helpers and exit codes as `accept/fc287ba-until-state.py` and
`accept/s13-mod-surface.py`; read those headers first, especially the SHAPE
CONTRACT note — `eq(..., None)` passes on an ABSENT key, so phase 0 proves every
dig path exists before any later phase leans on it, and phase 10 proves that
this file's own assertions can FAIL.

    ./accept/b1b3060-posture.py               # the bench sweep (1-5, 9)
    ./accept/b1b3060-posture.py --phase 3     # one phase (0 always runs)
    ./accept/b1b3060-posture.py --selftest    # phase 10 only: NO bench needed
    ./accept/b1b3060-posture.py --dry-run     # print the plan, send nothing

Phases:
    0   the bench, the registry, THE SHAPE CONTRACT, and the refusals  (always)
    1   posture with NO lever is a pure READ — nothing mutates
    2   ONE VERB SETS ALL THREE, and reports per pawn what it applied
    3   a pawn INCAPABLE OF VIOLENCE is refused BY NAME (fixture may SKIP)
    4   digest.posture: n/m as strings AND integers, and on_contact complete
    5   dry_run decides and writes nothing
    6   the mechanism, LIVE: hostility Flee beats seek   (opt-in; spawns a raid)
    7   save half   (opt-in; sets a posture, then asks a human to save + load)
    8   load half   (opt-in; proves all three settings survived the round trip)
    9   the whole run's standing invariants
    10  the suite's OWN assertion machinery (offline, no bench, no game)

WHAT THIS IS TESTING, in one sentence: a colony's combat posture is one state
you set with one call and read at every glance, and the read says what the pawns
will DO rather than what a field says.

WHY PHASE 6 IS OPT-IN AND WHY IT USES A TICK COUNT. It is the empirical half of
the finding that inverts this issue's own premise: `hostility_response` is
decided ABOVE seek (`JobGiver_ConfigurableHostilityResponse` is in the
`HumanlikeConstant` tree, which SeekAndKill never injects into and which
`Pawn_JobTracker` runs first), so `Flee` BEATS seek rather than being shadowed by
it. Proving that live means watching a seek-ON colonist run from a hostile — and
**no predicate expresses that wait**: `advance {until:{condition}}` compares with
`< <= > >= == !=` only, and the only published field that would answer is
`colonists.list[*].job`, a TRUNCATED driver report string that is not a stable
contract to compare with `==`. So phase 6 is a bounded `advance {ticks:…}` with
the reason stated, rather than a predicate that would be a lie. It also SPAWNS A
RAID at your colony; that is why it is not in the default sweep.

PHASES 7 AND 8 are the save/load bullet and cannot be automated end to end: 7
sets a posture and stops, you save and load in the game, 8 checks what survived.
Run `--phase 7`, save + load, then `--phase 8`.

WHAT THIS SUITE DELIBERATELY DOES NOT PROVE. That an area actually confines a
pawn's pathing — that is vanilla `ForbidUtility` and not ours; what is asserted
is that the digest counts a zero-cell area as NOT binding, which is the game's
own `TrueCount > 0` short-circuit. And that `on_contact` predicts a sleeping or
force-jobbed pawn: it deliberately models the STANDING posture only, and says so
in its own note.

Exit 0 = every check passed · 1 = at least one FAIL · 2 = a fixture precondition
could not be met, which is NOT a spec failure and says so.
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
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
DEFAULT_STATE = os.path.join("/tmp", "b1b3060-posture-state.json")

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
CAPTURE = None  # phase 10 only; see probe()

AREA_NAME = "acc-posture"

# The closed verdict vocabulary, from SeekVerbs.ContactVerdicts. Every one is
# published at every read, zero included, so a predicate over
# `posture.on_contact.flee` keeps arming on a colony that has no fleer today.
CONTACT_VERDICTS = ["downed", "mental-break", "player-controlled", "flee",
                    "attack-then-seek", "attack-nearby", "seek-only", "ignore"]

# The digest block's whole field set. Asserted by NAME in phase 0 and again as a
# set in phase 4, so a renamed field cannot go green.
POSTURE_KEYS = [
    "ok", "will_seek", "will_seek_n", "will_seek_of",
    "area_bound", "area_bound_n", "area_bound_of",
    "attack", "attack_n", "attack_of",
    "colonists", "areas", "on_contact", "flee_risk",
    "seek_mod", "seek_mod_missing", "denominators", "note",
]

# Worker B's `advance` contract (git-bug 722c951): the driver REFUSES to start
# while the journal holds an unread delta, and HALTS on an own-faction casualty.
# Both escapes are required non-empty strings and both are passed here for the
# same reason: this suite reads the journal on its own schedule (phase 9's
# red-error sweep is the read that matters, and it happens at the END), and
# phase 6 deliberately spawns a raid at a colony whose colonists are set to
# FLEE — a casualty is a possible outcome of the fixture, not a signal to stop.
ADVANCE_ESCAPES = {
    "unread_ok": "accept/b1b3060-posture.py reads the journal at phase 9, not per advance",
    "through_casualties": "accept/b1b3060-posture.py phase 6 spawns a raid on purpose; "
                          "a casualty is the fixture, not a halt condition",
}


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=240):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "accb1b3060-%03d-%s" % (SEQ, slug)
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


def advance(args):
    """Every advance in this file goes through here, so the two escapes cannot
    be forgotten on one call and turn a fixture into a driver refusal."""
    merged = dict(ADVANCE_ESCAPES)
    merged.update(args or {})
    return send("advance", merged)


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
    `seek_mod_missing` is DELIBERATELY null when the mod is loaded."""
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
    """Never raise from a FAILURE PRINTER. `json.dumps` throws on a set, and
    this file compares sets (`levers_refused`), so a printer that assumed JSON
    would turn a red check into a traceback — losing every check after it,
    including the exit code. Found by running phase 10, which is why phase 10
    exists."""
    if v is None:
        return "null"
    if isinstance(v, (set, frozenset)):
        v = sorted(v, key=str)
    try:
        return json.dumps(v, separators=(",", ":"))[:400]
    except (TypeError, ValueError):
        return repr(v)[:400]


def is_num(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


# ------------------------------------------------------------------- asserts --

def check(num, what, ok, expected, actual):
    global CHECKS
    CHECKS += 1
    if CAPTURE is not None:          # phase 10: verdict as data, no accounting
        CAPTURE.append(bool(ok))
        CHECKS -= 1
        return
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
    ok = is_num(got) and got >= want
    check(num, "%s (%s)" % (what, path), ok, ">= %s" % want, got)


def contains(num, what, env, path, needle):
    got = dig(env, path)
    ok = isinstance(got, str) and needle.lower() in got.lower()
    check(num, "%s (%s)" % (what, path), ok, "a string containing %r" % needle, got)


def absent(num, what, env, path):
    ok = not has_key(env, path)
    check(num, "%s (%s absent)" % (what, path), ok, "the key NOT to be present",
          dig(env, path))


def shape(num, verb, env, path, kind=None):
    ok = has_key(env, path)
    want = "the key to be present"
    if ok and kind is not None:
        ok = isinstance(dig(env, path), kind)
        want += " and a %s" % kind.__name__
    check(num, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def bad_args(num, what, env, needle=None):
    """A REFUSAL IS THE ASSERTION. `ok:false` with code bad-args, and when a
    needle is given, a sentence that actually names the problem."""
    code = dig(env, "error.code")
    ok = dig(env, "ok") is False and code == "bad-args"
    if ok and needle is not None:
        detail = dig(env, "error.detail") or ""
        ok = needle.lower() in detail.lower()
    check(num, what, ok, "ok:false, code bad-args%s"
          % ("" if needle is None else ", detail naming %r" % needle),
          {"ok": dig(env, "ok"), "code": code,
           "detail": (dig(env, "error.detail") or "")[:300]})


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
    # Exit 2 rather than sys.exit inside a phase leaking a half-open fixture:
    # this suite creates one area and one posture, both idempotent and both
    # harmless to leave. Session 20's leaked-trade-session defect was a MODAL
    # left open; nothing here opens one.
    sys.exit(2)


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


# ------------------------------------------------------------------ fixtures --

def pawn_rows(env):
    """Every per-pawn row the verb produced, accepted and rejected alike. A
    refusal is information, so a row that landed in `rejected` is still an
    answer about that pawn and must be searchable."""
    return ([r for r in as_list(dig(env, "data.accepted")) if isinstance(r, dict)]
            + [r for r in as_list(dig(env, "data.rejected")) if isinstance(r, dict)])


def row_for(env, name):
    for r in pawn_rows(env):
        if r.get("name") == name:
            return r
    return None


def levers_refused(row):
    return {x.get("lever") for x in as_list((row or {}).get("refused"))
            if isinstance(x, dict)}


def ensure_area(num):
    """An Area_Allowed with CELLS IN IT. The verb refuses a zero-cell area on
    purpose (ForbidUtility.InAllowedArea short-circuits on TrueCount > 0), so
    the fixture has to paint one — creating it is not enough, and phase 0
    asserts that refusal against a freshly created empty one."""
    if ARGS.dry_run:
        S["area_id"], S["area_empty_id"] = 1, 2
        return

    # `areas` returns its rows under `data.list`, NOT `data.areas` — the
    # digest's posture block is the one that publishes `areas`, and reading the
    # wrong one here would silently create a second area on every run.
    e = send("areas")
    for a in as_list(dig(e, "data.list")):
        if isinstance(a, dict) and a.get("label") == AREA_NAME:
            S["area_id"] = a.get("id")
    if S.get("area_id") is None:
        e = send("area", {"kind": "allowed", "op": "create", "name": AREA_NAME})
        S["area_id"] = dig(e, "data.id")
    precondition(num, "an Area_Allowed named %r" % AREA_NAME,
                 S.get("area_id") is not None,
                 "could not create one — the map may already hold ten "
                 "(AreaManager.CanMakeNewAllowed).")

    # Paint it over a real box. `find-rect` is how every other suite picks
    # ground it can stand on; the area only has to be non-empty, not useful.
    e = send("find-rect", {"w": 12, "h": 12, "max": 1, "require": ["buildable"]})
    at = dig(e, "data.candidates.0.at")
    precondition(num + "a", "a 12x12 box to paint the area over",
                 isinstance(at, list) and len(at) == 2,
                 "find-rect found no clear 12x12 — load a colony with open ground.")
    send("area", {"kind": "allowed", "op": "add", "id": S["area_id"],
                  "rect": [at[0], at[1], 12, 12]})
    e = send("areas")
    cells = 0
    for a in as_list(dig(e, "data.list")):
        if isinstance(a, dict) and a.get("id") == S["area_id"]:
            cells = a.get("cells") or 0
    S["area_cells"] = cells
    precondition(num + "b", "the area actually holds cells",
                 cells > 0,
                 "painting the area left it at %d cells — `area {kind:\"allowed\", "
                 "op:\"add\"}` reported nothing, and the posture verb refuses a "
                 "zero-cell area on purpose." % cells)
    print("  %sarea %r id=%s cells=%s%s" % (DIM, AREA_NAME, S["area_id"], cells, OFF))


def roster(num):
    e = send("posture")
    rows = pawn_rows(e)
    precondition(num, "at least two colonists to posture",
                 ARGS.dry_run or len(rows) >= 2,
                 "the colony has %d colonist(s); this suite needs two so that "
                 "'some refused, some applied' is distinguishable from 'all "
                 "refused'." % len(rows))
    return e


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 - the bench, the registry, and THE SHAPE CONTRACT")

    e = send("status")
    precondition("0.1", "the bench answers `status`",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "no status envelope - start _RimWorld-Agent/run-agent.sh")

    registry = as_list(dig(e, "data.verbs"))
    check("0.2a", "`status` publishes the registry", len(registry) > 0 or ARGS.dry_run,
          "a non-empty data.verbs list", registry[:5])
    for tag, verb in (("0.2b", "posture"), ("0.2c", "digest"),
                      ("0.2d", "seek-at-will"), ("0.2e", "assign"),
                      ("0.2f", "area"), ("0.2g", "areas")):
        ok = ARGS.dry_run or verb in registry
        check(tag, "the registry lists `%s`" % verb, ok,
              "%s in the registry" % verb, None if ok else len(registry))

    # THE WATERMARK. JournalVerbs.Read updates last_seq BEFORE the
    # `seq <= since_seq` skip, so `{limit:1}` reports the SECOND row's seq;
    # pushing since_seq past the end reads to the end and yields the true max.
    e = send("journal", {"since_seq": 999999999, "limit": 1})
    shape("0.3", "journal", e, "data.last_seq")
    S["seq0"] = dig(e, "data.last_seq") or 0
    print("  %sjournal watermark: seq0=%s%s" % (DIM, S["seq0"], OFF))

    # ---- digest.posture, EVERY key proved to EXIST --------------------------
    # This is the shape contract at its sharpest. `eq(..., None)` passes on an
    # absent key, so without this block a renamed field would leave every later
    # assertion green forever.
    e = send("digest")
    S["digest0"] = e
    shape("0.4", "digest", e, "data.posture", dict)
    for i, k in enumerate(POSTURE_KEYS):
        shape("0.4%s" % chr(ord("a") + i), "digest", e, "data.posture." + k)
    shape("0.5a", "digest", e, "data.posture.on_contact", dict)
    for i, v in enumerate(CONTACT_VERDICTS):
        shape("0.5%s" % chr(ord("b") + i), "digest", e, "data.posture.on_contact." + v)
    shape("0.6a", "digest", e, "data.posture.flee_risk", list)
    shape("0.6b", "digest", e, "data.posture.areas", list)

    # THE TYPES, because n/m ships twice and the two halves must not swap.
    got = dig(e, "data.posture.will_seek")
    check("0.7a", "`will_seek` is the n/m STRING the issue asked for",
          isinstance(got, str) and "/" in got, "a string like '2/3'", got)
    check("0.7b", "…and `will_seek_n` is an INTEGER a predicate can compare "
                  "(session 19 refuses `<` on a string)",
          is_num(dig(e, "data.posture.will_seek_n")), "a number",
          dig(e, "data.posture.will_seek_n"))
    got = dig(e, "data.posture.area_bound")
    check("0.7c", "`area_bound` is the n/m string", isinstance(got, str) and "/" in got,
          "a string like '3/3'", got)
    check("0.7d", "…and `area_bound_n` is an integer",
          is_num(dig(e, "data.posture.area_bound_n")), "a number",
          dig(e, "data.posture.area_bound_n"))
    check("0.7e", "`ok` is a bool, not a count",
          isinstance(dig(e, "data.posture.ok"), bool), "a bool",
          dig(e, "data.posture.ok"))

    # ---- posture is a PREDICATE SECTION -------------------------------------
    # Proved by ARMING one, not by reading a list: `advance` validates the path
    # once at arm time, so a path that resolves is the whole assertion. `edge`
    # is false so a true-now predicate returns immediately instead of running to
    # a timeout, and `timeout_ticks` is tiny so a false one costs nothing.
    e = advance({"until": {"condition": {"path": "posture.will_seek_n",
                                         "op": ">=", "value": 0, "edge": False}},
                 "timeout_ticks": 60})
    check("0.8a", "`posture` is addressable as a predicate section",
          dig(e, "ok") is True, "ok:true (the path armed)",
          {"ok": dig(e, "ok"), "detail": dig(e, "error.detail")})
    e = advance({"until": {"condition": {"path": "posture.will_seek_nn",
                                         "op": ">=", "value": 0}},
                 "timeout_ticks": 60})
    bad_args("0.8b", "…and a MISSPELLED field in it is refused at arm time", e,
             "will_seek_nn")
    contains("0.8c", "…naming the keys the section really publishes", e,
             "error.detail", "will_seek")

    banner("PHASE 0b - THE REFUSALS: a posture with two of three settings is the bug")

    e = send("posture", {"seek": True})
    bad_args("0.9a", "a lever without `area` is refused — the whole point of the verb",
             e, "three settings")
    contains("0.9b", "…and the refusal says how to declare unrestricted", e,
             "error.detail", "area:null")
    e = send("posture", {"area": "no-such-area-999"})
    bad_args("0.9c", "an unknown area name is refused", e, "no area named")
    e = send("posture", {"area": AREA_NAME, "seek": "sometimes"})
    bad_args("0.9d", "seek must be true, false or \"auto\"", e, "auto")
    e = send("posture", {"area": AREA_NAME, "hostility": "Cower"})
    bad_args("0.9e", "an unknown hostility mode is refused", e, "Ignore|Attack|Flee")

    # THE ZERO-CELL AREA. This is the refusal that keeps the digest honest:
    # ForbidUtility.InAllowedArea short-circuits on `TrueCount > 0`, so binding
    # to an empty area restricts nothing while the verb would report every pawn
    # bound — the exact false report this issue exists to remove.
    if not ARGS.dry_run:
        e = send("area", {"kind": "allowed", "op": "create", "name": AREA_NAME + "-empty"})
        S["area_empty_id"] = dig(e, "data.id")
    if ARGS.dry_run or S.get("area_empty_id") is not None:
        e = send("posture", {"area": S.get("area_empty_id")})
        bad_args("0.10a", "a ZERO-CELL area is refused rather than silently bound", e,
                 "ZERO cells")
        contains("0.10b", "…citing the game's own short-circuit", e,
                 "error.detail", "InAllowedArea")
        contains("0.10c", "…and naming the escape hatch", e,
                 "error.detail", "allow_empty_area")
    else:
        note("0.10", "could not create a second area (map at 10) — zero-cell "
                     "refusal not exercised")

    # THE CLOCK MUST NOT HAVE MOVED across the refusals and the armed advance.
    e = send("digest")
    eq("0.11a", "the bench is still PAUSED after phase 0", e, "data.time.paused", True)
    eq("0.11b", "…at speed Paused, not merely force-paused", e, "data.time.speed", "Paused")


# ------------------------------------------------------------------- phase 1 --

def phase1():
    banner("PHASE 1 - NO LEVER IS A PURE READ: `posture` mutates nothing")

    before = send("digest")
    e = send("posture")
    S["read"] = e
    eq("1.1a", "the read succeeded", e, "ok", True)
    eq("1.1b", "…and says so", e, "data.mode", "read")
    # NO JOURNAL ROW IS OWED, and saying so is not the same as failing to write
    # one (PawnActs.NoStamp).
    eq("1.2a", "a read owes the journal nothing", e, "data.action.journal_seq", None)
    contains("1.2b", "…and says why in words", e, "data.action.provenance",
             "not applicable")
    eq("1.2c", "no lever was named", e, "data.levers", [])
    eq("1.2d", "no area was chosen", e, "data.area", None)
    eq("1.2e", "no hostility was chosen", e, "data.hostility", None)

    rows = pawn_rows(e)
    check("1.3a", "every colonist got a row", len(rows) > 0 or ARGS.dry_run,
          ">= 1 row", len(rows))
    if rows:
        r = rows[0]
        for tag, k in (("1.3b", "before"), ("1.3c", "on_contact"),
                       ("1.3d", "on_contact_why"), ("1.3e", "violence_capable"),
                       ("1.3f", "class")):
            check(tag, "a read row publishes `%s`" % k, k in r,
                  "the key present", sorted(r.keys()))
        check("1.3g", "`on_contact` is from the closed vocabulary",
              r.get("on_contact") in CONTACT_VERDICTS,
              "one of %s" % CONTACT_VERDICTS, r.get("on_contact"))
        check("1.3h", "…and `on_contact_why` names the member that decides it",
              isinstance(r.get("on_contact_why"), str) and len(r["on_contact_why"]) > 40,
              "a sentence citing a game member", r.get("on_contact_why"))
        b = r.get("before") or {}
        for tag, k in (("1.4a", "area"), ("1.4b", "area_binds"),
                       ("1.4c", "respects_area"), ("1.4d", "hostility_response"),
                       ("1.4e", "configurable_hostility"), ("1.4f", "seek_toggled"),
                       ("1.4g", "will_seek"), ("1.4h", "seek_eligible")):
            check(tag, "`before` publishes `%s`" % k, k in b,
                  "the key present", sorted(b.keys()))

    # NOTHING MOVED. The tick is the cheap half; the journal watermark is the
    # half that catches a read that wrote an `action` row it did not owe.
    after = send("digest")
    eq_val("1.5a", "the tick did not advance across the read",
           dig(after, "data.time.tick"), dig(before, "data.time.tick"))
    j = send("journal", {"since_seq": 999999999, "limit": 1})
    eq_val("1.5b", "…and the journal grew by nothing",
           dig(j, "data.last_seq"), dig(before, "data.changed.last_seq"))


# ------------------------------------------------------------------- phase 2 --

def phase2():
    banner("PHASE 2 - ONE VERB SETS ALL THREE, and names what it refused")

    ensure_area("2.0")
    roster("2.0b")

    # Set the WRONG posture first, deliberately, so phase 2's write has
    # something to change and "already" cannot masquerade as success.
    send("posture", {"area": None, "seek": False, "hostility": "Flee"})

    e = send("posture", {"area": AREA_NAME, "seek": True, "hostility": "Attack"})
    S["write"] = e
    eq("2.1a", "the write succeeded", e, "ok", True)
    eq("2.1b", "…and says which mode it ran in", e, "data.mode", "write")
    eq("2.1c", "all three levers are named", e, "data.levers",
       ["area", "seek", "hostility"])
    eq("2.1d", "the area it bound to is echoed", e, "data.area", AREA_NAME)
    eq("2.1e", "the hostility it set is echoed", e, "data.hostility", "Attack")
    ge("2.1f", "…and the area's cell count, so `bound` is checkable", e,
       "data.area_cells", 1)

    # THE JOURNAL ROW. A composite verb that mutated three settings per pawn and
    # left no `action` line would be exactly the unprovenanced partial mutation
    # the journal exists to prevent (4087644).
    shape("2.2a", "posture", e, "data.action.journal_seq")
    ge("2.2b", "…and it is a real seq, not the closed-writer zero", e,
       "data.action.journal_seq", 1)
    absent("2.2c", "…so no `provenance` warning rides along", e,
           "data.action.provenance")

    rows = pawn_rows(e)
    check("2.3a", "every colonist got a row", len(rows) > 0 or ARGS.dry_run,
          ">= 1 row", len(rows))

    applied_area = applied_seek = applied_host = 0
    for r in rows:
        ap = set(as_list(r.get("applied")))
        if "area" in ap:
            applied_area += 1
        if "seek" in ap:
            applied_seek += 1
        if "hostility" in ap:
            applied_host += 1
        # PER PAWN, WHAT IT DID AND WHAT IT REFUSED — b1b3060's first bullet.
        check("2.3b:%s" % r.get("name"), "row publishes both `applied` and `refused`",
              isinstance(r.get("applied"), list) and isinstance(r.get("refused"), list),
              "two lists", {k: r.get(k) for k in ("applied", "refused")})
        for x in as_list(r.get("refused")):
            check("2.3c:%s" % r.get("name"),
                  "every refusal names its lever AND a reason",
                  isinstance(x, dict) and x.get("lever") in ("area", "seek", "hostility")
                  and isinstance(x.get("reason"), str) and len(x["reason"]) > 10,
                  "{lever, reason}", x)

    ge("2.4a", "the area lever applied to somebody", {"n": applied_area}, "n", 1)
    ge("2.4b", "the hostility lever applied to somebody", {"n": applied_host}, "n", 1)
    # SEEK IS THE ONE THAT MAY LEGITIMATELY APPLY TO NOBODY: on a bench without
    # SeekAndKill every pawn is refused by name, which is the correct answer and
    # not a failure. Which case this bench is in is read, not assumed.
    seek_mod = dig(e, "data.posture.seek_mod")
    if seek_mod is True:
        ge("2.4c", "…and so did seek, since SeekAndKill is loaded",
           {"n": applied_seek}, "n", 1)
    else:
        note("2.4c", "SeekAndKill is NOT loaded (%s) — seek refusals are the "
                     "correct answer here" % dig(e, "data.posture.seek_mod_missing"))
        for r in rows:
            if "seek" in levers_refused(r):
                contains("2.4d", "…and the refusal names the mod, per pawn",
                         {"r": r}, "r.refused.0.reason", "SeekAndKill")
                break

    # THE AFTER STATE IS READ BACK, not projected: SyncedToggleSeek re-checks
    # its own gate and returns SILENTLY on failure.
    for r in rows:
        ap = set(as_list(r.get("applied")))
        a = r.get("after") or {}
        if "area" in ap:
            eq_val("2.5a:%s" % r.get("name"), "…area reads back as the one asked for",
                   a.get("area"), AREA_NAME)
            eq_val("2.5b:%s" % r.get("name"), "…and the game counts it as binding",
                   a.get("area_binds"), True)
        if "hostility" in ap:
            eq_val("2.5c:%s" % r.get("name"), "…hostility reads back as Attack",
                   a.get("hostility_response"), "Attack")
        if "seek" in ap:
            eq_val("2.5d:%s" % r.get("name"), "…seek reads back as toggled",
                   a.get("seek_toggled"), True)

    # THE ROLLUP RIDES ALONG, so a caller does not need a second round trip.
    shape("2.6a", "posture", e, "data.posture.ok")
    d = send("digest")
    eq_val("2.6b", "…and the digest agrees with the verb's own rollup",
           dig(d, "data.posture.attack_n"), dig(e, "data.posture.attack_n"))
    S["digest_after_write"] = d


# ------------------------------------------------------------------- phase 3 --

def phase3():
    banner("PHASE 3 - A PAWN INCAPABLE OF VIOLENCE IS REFUSED **BY NAME**")

    ensure_area("3.0")

    # THE FIXTURE. `dev:spawn-pawn {violence_capable:false}` does NOT force
    # incapability — the flag is `mustBeCapableOfViolence`, so false merely
    # stops REQUIRING it and the generator rolls a backstory freely. Vanilla has
    # no trait that disables Violent (the three that mention it list it under
    # `requiredWorkTags`); the routes are a backstory whose `workDisables`
    # carries it, Biotech's `ViolenceDisabled` gene, or a PawnKindDef's
    # `disabledWorkTags` — and only the first is reachable from a shipped verb.
    # So: spawn in batches and LOOK, rather than assert a pawn we cannot force.
    name = None
    e = send("posture")
    for r in pawn_rows(e):
        if r.get("violence_capable") is False:
            name = r.get("name")
            break

    spawned = 0
    while name is None and not ARGS.dry_run and spawned < 40:
        send("dev:spawn-pawn", {"kind": "Colonist", "faction": "player",
                                "count": 10, "violence_capable": False,
                                "spread": 4})
        spawned += 10
        e = send("posture")
        for r in pawn_rows(e):
            if r.get("violence_capable") is False:
                name = r.get("name")
                break

    precondition("3.1", "a colonist incapable of Violent work",
                 ARGS.dry_run or name is not None,
                 "rolled %d colonists with `dev:spawn-pawn "
                 "{violence_capable:false}` and none had a backstory that "
                 "disables Violent. Vanilla offers no trait or hediff that "
                 "does; the deterministic route is Biotech's `ViolenceDisabled` "
                 "gene, which no shipped verb can apply. Re-run, or hand-build "
                 "a colony with a nonviolent pawn at world-gen (the character "
                 "editor shows 'Incapable of: Violent')." % spawned)
    if ARGS.dry_run:
        name = "<nonviolent-pawn>"
    print("  %snonviolent colonist: %s (after %d extra spawns)%s"
          % (DIM, name, spawned, OFF))
    S["nonviolent"] = name

    e = send("posture", {"area": AREA_NAME, "seek": True, "hostility": "Attack"})

    # 3.2 THE HEADLINE. Branchable without walking rows — the same shape
    #     work_coverage.under has.
    named = [x.get("name") for x in as_list(dig(e, "data.incapable_of_violence"))
             if isinstance(x, dict)]
    check("3.2a", "`incapable_of_violence` names the pawn in the HEADLINE",
          ARGS.dry_run or name in named, "%r in %s" % (name, named), named)
    row0 = next((x for x in as_list(dig(e, "data.incapable_of_violence"))
                 if isinstance(x, dict) and x.get("name") == name), None)
    if row0 is not None:
        eq_val("3.2b", "…with the gate that refused it", row0.get("gate"),
               "violent-disabled")
        check("3.2c", "…and a reason citing BOTH widgets that refuse it",
              isinstance(row0.get("reason"), str)
              and "ShowsSeekGizmo" in row0["reason"]
              and "HostilityResponseModeUtility" in row0["reason"],
              "a reason naming ShowsSeekGizmo and HostilityResponseModeUtility",
              row0.get("reason"))
        check("3.2d", "…and its id, so the row joins to `pawn`",
              is_num(row0.get("pawn")), "a pawn id", row0.get("pawn"))

    # 3.3 AND THE PER-PAWN ROW, which is where "not skipped silently" is proved:
    #     the pawn is PRESENT, its refusals are named, and the area still bound.
    r = row_for(e, name)
    check("3.3a", "the pawn has a row of its own (not skipped)",
          ARGS.dry_run or r is not None, "a row for %r" % name,
          [x.get("name") for x in pawn_rows(e)])
    if r is not None:
        eq_val("3.3b", "…flagged violence_capable:false", r.get("violence_capable"), False)
        refused = levers_refused(r)
        check("3.3c", "…hostility REFUSED by name", "hostility" in refused,
              "hostility in refused", sorted(refused))
        reasons = {x.get("lever"): x.get("reason") for x in as_list(r.get("refused"))
                   if isinstance(x, dict)}
        check("3.3d", "…and the hostility reason is the game's own menu omission",
              "does not offer Attack" in (reasons.get("hostility") or ""),
              "a reason naming the omitted Attack option", reasons.get("hostility"))
        if dig(e, "data.posture.seek_mod") is True:
            check("3.3e", "…seek REFUSED by name too", "seek" in refused,
                  "seek in refused", sorted(refused))
            check("3.3f", "…citing the violence clause of ShowsSeekGizmo",
                  "Violent" in (reasons.get("seek") or ""),
                  "a reason naming Violent work", reasons.get("seek"))
        check("3.3g", "…while the AREA still applied — a bound pawn that will "
                      "never fight is the honest outcome",
              "area" in set(as_list(r.get("applied"))) or "area" in refused,
              "area either applied or refused with a reason, never absent",
              {"applied": r.get("applied"), "refused": r.get("refused")})
        # PARTIAL SUCCESS IS NEVER SILENT: the row is in `accepted` when
        # something applied and in `rejected` when nothing did, and either way it
        # carries the refusals.
        check("3.3h", "…and the row carries `on_contact` for it as well",
              r.get("on_contact") in CONTACT_VERDICTS, "a verdict",
              r.get("on_contact"))


# ------------------------------------------------------------------- phase 4 --

def phase4():
    banner("PHASE 4 - digest.posture: n/m both ways, and what they will DO")

    ensure_area("4.0")
    send("posture", {"area": AREA_NAME, "seek": True, "hostility": "Attack"})
    e = send("digest")
    p = dig(e, "data.posture") or {}

    # 4.1 THE FIELD SET IS EXACTLY WHAT IS DOCUMENTED — a new field is a change
    #     of contract and must be a deliberate edit to this list.
    if not ARGS.dry_run:
        extra = sorted(set(p.keys()) - set(POSTURE_KEYS))
        missing = sorted(set(POSTURE_KEYS) - set(p.keys()))
        check("4.1a", "digest.posture publishes exactly the documented field set",
              not extra and not missing, "no extras, nothing missing",
              {"extra": extra, "missing": missing})

    # 4.2 n/m AGREES WITH ITS OWN INTEGERS. The string is the glance and the
    #     integers are the predicate; a suite that checked only one would let
    #     them drift.
    for tag, s_key, n_key, of_key in (("4.2a", "will_seek", "will_seek_n", "will_seek_of"),
                                      ("4.2b", "area_bound", "area_bound_n", "area_bound_of"),
                                      ("4.2c", "attack", "attack_n", "attack_of")):
        s, n, m = p.get(s_key), p.get(n_key), p.get(of_key)
        check(tag, "`%s` == '%s/%s'" % (s_key, n_key, of_key),
              ARGS.dry_run or s == "%s/%s" % (n, m), "%s/%s" % (n, m), s)
        check(tag + "'", "…and n <= m", ARGS.dry_run or (is_num(n) and is_num(m) and n <= m),
              "n <= m", {"n": n, "m": m})

    # 4.3 THE DENOMINATORS ARE DIFFERENT ON PURPOSE, and the block says so in
    #     words rather than leaving a reader to infer it from two numbers.
    contains("4.3a", "the denominators are documented in the block itself",
             e, "data.posture.denominators", "violence-capable")
    contains("4.3b", "…including the zero-cell rule", e,
             "data.posture.denominators", "TrueCount")
    check("4.3c", "`will_seek_of` and `attack_of` share the violence-capable "
                  "denominator", ARGS.dry_run or p.get("will_seek_of") == p.get("attack_of"),
          "the same number", {"will_seek_of": p.get("will_seek_of"),
                              "attack_of": p.get("attack_of")})
    check("4.3d", "…and `area_bound_of` is its own, over area-capable pawns",
          ARGS.dry_run or is_num(p.get("area_bound_of")), "a number",
          p.get("area_bound_of"))

    # 4.4 ON_CONTACT: complete vocabulary, counts summing to the roster.
    oc = p.get("on_contact") or {}
    if not ARGS.dry_run:
        check("4.4a", "`on_contact` publishes every verdict, zeros included",
              sorted(oc.keys()) == sorted(CONTACT_VERDICTS),
              "the closed vocabulary", sorted(oc.keys()))
        check("4.4b", "…in the vocabulary's own order, not hash order",
              list(oc.keys()) == CONTACT_VERDICTS, CONTACT_VERDICTS, list(oc.keys()))
        total = sum(v for v in oc.values() if is_num(v))
        eq_val("4.4c", "…and the counts sum to the colonist roster",
               total, p.get("colonists"))

    # 4.5 THE POSTURE JUST SET IS VISIBLE, which is the whole point of the block:
    #     the standing state at every read instead of inferred from a field.
    ge("4.5a", "somebody is set to Attack after the posture ran", e,
       "data.posture.attack_n", 1)
    check("4.5b", "…and nobody is left in the M1 state (flee_risk empty)",
          ARGS.dry_run or dig(e, "data.posture.flee_risk") == [],
          "an empty flee_risk", dig(e, "data.posture.flee_risk"))
    ge("4.5c", "…and pawns are bound to the area", e, "data.posture.area_bound_n", 1)
    check("4.5d", "the area shows up by name in `areas`",
          ARGS.dry_run or any(isinstance(x, str) and x.startswith(AREA_NAME)
                              for x in as_list(dig(e, "data.posture.areas"))),
          "an entry starting %r" % AREA_NAME, dig(e, "data.posture.areas"))

    # 4.6 THE FAILING DIRECTION, measured rather than argued. Set Flee and watch
    #     the block name the pawns — `attack_n` falls, `flee_risk` fills, `ok`
    #     goes false. Without this the block could be hard-coded green.
    send("posture", {"area": AREA_NAME, "seek": True, "hostility": "Flee"})
    e = send("digest")
    eq("4.6a", "with Flee set, the posture is NOT ok", e, "data.posture.ok", False)
    ge("4.6b", "…and flee_risk names the violence-capable pawns in it",
       {"n": len(as_list(dig(e, "data.posture.flee_risk")))}, "n", 1)
    ge("4.6c", "…and on_contact counts them as `flee`", e,
       "data.posture.on_contact.flee", 1)
    fr = as_list(dig(e, "data.posture.flee_risk"))
    if fr and isinstance(fr[0], dict):
        for tag, k in (("4.6d", "name"), ("4.6e", "pawn"), ("4.6f", "will_seek")):
            check(tag, "a flee_risk row publishes `%s`" % k, k in fr[0],
                  "the key present", sorted(fr[0].keys()))
        if dig(e, "data.posture.seek_mod") is True:
            # THE FINDING THAT INVERTS THE ISSUE'S PREMISE, as a fact about the
            # published state: seek is ON and the pawn is still counted as a
            # fleer, because JobGiver_ConfigurableHostilityResponse is in the
            # constant tree and runs above seek.
            eq_val("4.6g", "…and seek is ON for one of them, which is the whole "
                           "finding: Flee BEATS seek", fr[0].get("will_seek"), True)

    # Put it back, so the bench is left in the posture the checklist asks for.
    send("posture", {"area": AREA_NAME, "seek": True, "hostility": "Attack"})
    e = send("digest")
    eq("4.7", "the repair is one call and the digest confirms it", e,
       "data.posture.on_contact.flee", 0)


# ------------------------------------------------------------------- phase 5 --

def phase5():
    banner("PHASE 5 - dry_run decides and reports, and writes NOTHING")

    ensure_area("5.0")
    send("posture", {"area": AREA_NAME, "seek": True, "hostility": "Attack"})
    before = send("digest")

    e = send("posture", {"area": AREA_NAME, "seek": False, "hostility": "Flee",
                         "dry_run": True})
    eq("5.1a", "the dry run succeeded", e, "ok", True)
    eq("5.1b", "…and says which mode it ran in", e, "data.mode", "dry-run")
    eq("5.1c", "…and owes the journal nothing", e, "data.action.journal_seq", None)
    ge("5.1d", "…while still deciding for every pawn", e, "data.counts.accepted", 1)
    rows = pawn_rows(e)
    if rows:
        check("5.1e", "…and says its `after` is the BEFORE state, not an observation",
              "after_is" in rows[0], "an `after_is` disclaimer", sorted(rows[0].keys()))

    after = send("digest")
    eq_val("5.2a", "hostility did NOT change", dig(after, "data.posture.attack_n"),
           dig(before, "data.posture.attack_n"))
    eq_val("5.2b", "seek did NOT change", dig(after, "data.posture.will_seek_n"),
           dig(before, "data.posture.will_seek_n"))
    eq_val("5.2c", "area binding did NOT change", dig(after, "data.posture.area_bound_n"),
           dig(before, "data.posture.area_bound_n"))
    eq_val("5.2d", "…and the tick did not move either",
           dig(after, "data.time.tick"), dig(before, "data.time.tick"))


# ------------------------------------------------------------------- phase 6 --

def phase6():
    banner("PHASE 6 - THE MECHANISM, LIVE: hostility Flee BEATS seek  (spawns a raid)")

    note("6.0", "this phase SPAWNS HOSTILES at your colony and advances. It is "
                "not in the default sweep. Run it on a bench you can lose.")
    ensure_area("6.0a")

    # Seek ON, hostility FLEE — the M1 state, declared deliberately.
    e = send("posture", {"area": AREA_NAME, "seek": True, "hostility": "Flee"})
    seek_mod = dig(e, "data.posture.seek_mod")
    precondition("6.1", "SeekAndKill loaded (this phase is about seek losing)",
                 ARGS.dry_run or seek_mod is True,
                 "SeekAndKill is not on this bench (%s), so there is no seek for "
                 "Flee to beat." % dig(e, "data.posture.seek_mod_missing"))
    ge("6.1a", "at least one colonist is seek-ON and Flee-set", e,
       "data.posture.on_contact.flee", 1)
    fr = as_list(dig(e, "data.posture.flee_risk"))
    seeking_fleers = [x for x in fr if isinstance(x, dict) and x.get("will_seek") is True]
    precondition("6.1b", "a colonist who is BOTH seeking and set to Flee",
                 ARGS.dry_run or len(seeking_fleers) > 0,
                 "no pawn is in both states, so the race cannot be observed.")

    d = send("digest")
    S["tick6"] = dig(d, "data.time.tick")
    hostiles_before = dig(d, "data.threats.hostiles") or 0

    e = send("dev:incident", {"def": "RaidEnemy"})
    if dig(e, "ok") is not True:
        note("6.2", "dev:incident RaidEnemy refused (%s) — falling back to "
                    "dev:spawn-pawn" % dig(e, "error.detail"))
        send("dev:spawn-pawn", {"kind": "Pirate", "faction": "Pirate", "count": 3,
                                "spread": 3})

    # NO PREDICATE EXPRESSES THIS WAIT, and that is stated rather than papered
    # over. `advance {until:{condition}}` compares with < <= > >= == != only, and
    # the field that would answer — `colonists.list[*].job` — is a TRUNCATED
    # driver report string (`Journal.Truncate(job, 48)`), so `==` against it is
    # not a contract anything should depend on. A raid also has to CLOSE before
    # anything fires: the flee trigger is SelfDefenseUtility.ShouldStartFleeing,
    # whose AttackTarget branch needs a threat within 8 cells AND in line of
    # sight (ShouldFleeFrom(checkDistance:true, checkLOS:true)) — and how long
    # raiders take to cover that ground is not something the suite can know.
    # So: a bounded tick budget, with the reason written down.
    advance({"ticks": 2500})

    d = send("digest")
    ge("6.3a", "the raid arrived", d, "data.threats.hostiles", hostiles_before + 1)
    jobs = [c.get("job") or "" for c in as_list(dig(d, "data.colonists.list"))
            if isinstance(c, dict)]
    fleeing = [j for j in jobs if "flee" in j.lower() or "cower" in j.lower()]
    check("6.3b", "a seek-ON colonist is FLEEING — the flee node is NOT "
                  "unreachable, and this is the whole finding",
          ARGS.dry_run or len(fleeing) > 0,
          "at least one colonist job reading as flee/cower", jobs)

    # THE REPAIR, live: one call, and the same colony fights instead.
    send("posture", {"area": AREA_NAME, "seek": True, "hostility": "Attack"})
    advance({"ticks": 2500})
    d = send("digest")
    eq("6.4a", "after the repair nobody is counted as a fleer", d,
       "data.posture.on_contact.flee", 0)
    jobs = [c.get("job") or "" for c in as_list(dig(d, "data.colonists.list"))
            if isinstance(c, dict)]
    print("  %sjobs after the repair: %s%s" % (DIM, show(jobs), OFF))
    note("6.4b", "what the colony did after the repair is READ, not asserted: "
                 "whether a specific pawn reaches a specific target in 2500 "
                 "ticks is a fixture question, not a spec one.")


# ----------------------------------------------------------------- phases 7/8 --

def phase7():
    banner("PHASE 7 - the SAVE half (a human saves and loads between 7 and 8)")

    ensure_area("7.0")
    e = send("posture", {"area": AREA_NAME, "seek": True, "hostility": "Attack"})
    eq("7.1a", "the posture was set", e, "ok", True)
    d = send("digest")
    j = send("journal", {"since_seq": 999999999, "limit": 1})
    p = dig(d, "data.posture") or {}
    state = {
        "root": ARGS.root,
        "seq": dig(j, "data.last_seq") or 0,
        "area": AREA_NAME,
        "posture": {k: p.get(k) for k in
                    ("will_seek_n", "will_seek_of", "area_bound_n", "area_bound_of",
                     "attack_n", "attack_of", "ok", "seek_mod")},
        "rows": [{"name": r.get("name"),
                  "area": dig(r, "after.area"),
                  "hostility": dig(r, "after.hostility_response"),
                  "seek": dig(r, "after.seek_toggled"),
                  "will_seek": dig(r, "after.will_seek")}
                 for r in pawn_rows(e)],
    }
    if not ARGS.dry_run:
        with open(ARGS.state, "w", encoding="utf-8") as fh:
            json.dump(state, fh)
    print("")
    print("  %sSTATE WRITTEN: %s%s" % (CYAN, ARGS.state, OFF))
    print("  %sposture now: %s%s" % (DIM, show(state["posture"]), OFF))
    print("  %sNOW, IN THE GAME: save the colony, then LOAD that save. Then run:%s"
          % (CYAN, OFF))
    print("  %s    %s --phase 8%s" % (CYAN, sys.argv[0], OFF))


def phase8():
    banner("PHASE 8 - the LOAD half: did all three settings survive?")

    precondition("8.1", "phase 7's state file exists",
                 ARGS.dry_run or os.path.exists(ARGS.state),
                 "no %s — run `--phase 7` first, then save and load in the game."
                 % ARGS.state)
    if ARGS.dry_run:
        return
    with open(ARGS.state, encoding="utf-8") as fh:
        state = json.load(fh)

    # 8.1a PROVE THE RELOAD HAPPENED. AgentGameComponent.LoadedGame emits a
    #      `session {kind:"loaded"}` row; without this check the phase would pass
    #      on a bench that never reloaded, which is the worst possible way for a
    #      persistence test to be green (s13-mod-surface phase 7's lesson).
    e = send("journal", {"since_seq": state["seq"], "types": ["session"], "limit": 50})
    loaded = [r for r in as_list(dig(e, "data.events"))
              if isinstance(r, dict) and dig(r, "payload.kind") == "loaded"]
    precondition("8.1a", "a `session {kind:\"loaded\"}` row since phase 7",
                 len(loaded) > 0,
                 "no load boundary in the journal since seq %s. The colony was "
                 "never reloaded, so there is nothing to prove." % state["seq"])
    check("8.1b", "the game really was reloaded between the halves", True,
          "a session row", loaded[0])

    d = send("digest")
    want = state["posture"]

    # 8.2 THE THREE SETTINGS, one at a time, because they persist by three
    #     different mechanisms and only one of them is ours to reason about:
    #       hostility  Pawn_PlayerSettings.ExposeData, Scribe_Values (default Flee)
    #       area       Pawn_PlayerSettings.ExposeData, Scribe_Collections by REFERENCE
    #       seek       SeekAndKillGameComponent.ExposeData, "SK_SeekPawns" — and
    #                  only when PSInterop.PsToggleShared is false, plus a
    #                  PruneStaleIds pass at LoadedGame.
    eq_val("8.2a", "hostility survived the round trip (Scribe_Values)",
           dig(d, "data.posture.attack_n"), want["attack_n"])
    eq_val("8.2b", "the area binding survived (Scribe_Collections, by reference)",
           dig(d, "data.posture.area_bound_n"), want["area_bound_n"])
    if want.get("seek_mod") is True:
        eq_val("8.2c", "the seek set survived (SeekAndKill's own GameComponent "
                       "scribe, then PruneStaleIds)",
               dig(d, "data.posture.will_seek_n"), want["will_seek_n"])
    else:
        note("8.2c", "SeekAndKill was absent before the save; nothing to prove.")
    eq_val("8.2d", "…and the rollup verdict is unchanged",
           dig(d, "data.posture.ok"), want["ok"])

    # 8.3 PER PAWN, BY NAME. A rollup that happens to match is not the same as
    #     the same pawns holding the same settings.
    e = send("posture")
    for n, want_row in enumerate(state["rows"]):
        r = row_for(e, want_row["name"])
        if r is None:
            note("8.3.%d" % n, "%r is not on the map after the load (dead, or "
                               "left) — skipped" % want_row["name"])
            continue
        b = r.get("before") or {}
        eq_val("8.3a%d" % n, "%s: area survived" % want_row["name"],
               b.get("area"), want_row["area"])
        eq_val("8.3b%d" % n, "%s: hostility survived" % want_row["name"],
               b.get("hostility_response"), want_row["hostility"])
        if want.get("seek_mod") is True:
            eq_val("8.3c%d" % n, "%s: seek toggle survived" % want_row["name"],
                   b.get("seek_toggled"), want_row["seek"])

    # 8.4 AND THE OTHER HALF OF THE ACCEPTANCE BULLET — "or the digest SAYS it
    #     did not". The block is built so a lost posture is visible rather than
    #     silent: it is counted from live state at every glance.
    contains("8.4a", "the block explains what it counts, so a drop is readable",
             d, "data.posture.denominators", "violence-capable")
    shape("8.4b", "digest", d, "data.posture.seek_mod")
    shape("8.4c", "digest", d, "data.posture.flee_risk", list)
    note("8.4d", "a posture that had evaporated would show here as a fallen "
                 "attack_n / area_bound_n / will_seek_n and a filled flee_risk — "
                 "the digest reports it either way, which is the bullet.")

    os.remove(ARGS.state)


# ------------------------------------------------------------------- phase 9 --

def phase9():
    banner("PHASE 9 - the whole run's standing invariants")

    e = send("journal", {"since_seq": S.get("seq0", 0), "types": ["red_error"],
                         "limit": 50})
    eq("9.1", "ZERO red errors across the whole run", e, "data.count", 0)

    # An observer that mutates is the standing hazard this project names first.
    # digest.posture reads EffectiveAreaRestrictionInPawnCurrentMap, GetLord()
    # and CombinedDisabledWorkTags — none of which writes a cache — so two reads
    # back to back must be identical with the clock stopped.
    a = send("digest")
    b = send("digest")
    for k in ("will_seek", "area_bound", "attack", "ok", "colonists"):
        eq_val("9.2:%s" % k, "digest.posture.%s is stable across two reads" % k,
               dig(b, "data.posture." + k), dig(a, "data.posture." + k))
    eq_val("9.2t", "…and the clock did not move between them",
           dig(b, "data.time.tick"), dig(a, "data.time.tick"))

    e = send("status")
    absent("9.3a", "no force-pausing modal was left behind", e, "data.forcePause")
    eq("9.3b", "the bench is left paused", send("digest"), "data.time.paused", True)


# ------------------------------------------------------------------ phase 10 --
# THE SUITE'S OWN ASSERTION MACHINERY. No bench, no game, no protocol root.
#
# A suite that cannot fail on a renamed field is not a suite, and the only way to
# know this one can is to feed it a renamed field and watch it fail. probe() runs
# a real assertion with the accounting redirected and returns its verdict as
# data; every check below asserts that verdict.

def probe(fn):
    global CAPTURE
    CAPTURE = []
    try:
        fn()
        got = list(CAPTURE)
    finally:
        CAPTURE = None
    return all(got) if got else False


# A canned envelope in the shipped shape (SeekVerbs.PostureSection), and its
# broken twins. Values are plausible, not real — what is under test is this
# file's assertions, not the mod.
GOOD_DIGEST = {"ok": True, "op": "digest", "data": {
    "time": {"tick": 120000, "paused": True, "speed": "Paused"},
    "posture": {
        "ok": False, "will_seek": "2/3", "will_seek_n": 2, "will_seek_of": 3,
        "area_bound": "3/4", "area_bound_n": 3, "area_bound_of": 4,
        "attack": "0/3", "attack_n": 0, "attack_of": 3,
        "colonists": 4, "areas": ["acc-posture x3"],
        "on_contact": {"downed": 0, "mental-break": 0, "player-controlled": 0,
                       "flee": 3, "attack-then-seek": 0, "attack-nearby": 0,
                       "seek-only": 0, "ignore": 1},
        "flee_risk": [{"name": "Captain", "pawn": 995, "will_seek": True}],
        "seek_mod": True, "seek_mod_missing": None,
        "denominators": "… violence-capable … TrueCount > 0 …",
        "note": "…"}}}

GOOD_POSTURE = {"ok": True, "op": "posture", "data": {
    "verb": "posture", "mode": "write", "levers": ["area", "seek", "hostility"],
    "area": "acc-posture", "area_id": 7, "area_cells": 144, "hostility": "Attack",
    "seek": True, "dry_run": False,
    "action": {"journal_seq": 412},
    "incapable_of_violence": [{"pawn": 42, "name": "Chili", "gate": "violent-disabled",
                               "reason": "incapable of Violent work: "
                                         "SeekAndKill/Patch_PawnGetGizmos.ShowsSeekGizmo "
                                         "refuses it and HostilityResponseModeUtility's "
                                         "own dropdown omits Attack for it."}],
    "accepted": [{"pawn": 995, "name": "Captain", "violence_capable": True,
                  "applied": ["area", "hostility", "seek"], "refused": [],
                  "before": {"area": None, "hostility_response": "Flee"},
                  "after": {"area": "acc-posture", "area_binds": True,
                            "hostility_response": "Attack", "seek_toggled": True},
                  "on_contact": "attack-then-seek", "on_contact_why": "…"}],
    "rejected": [{"pawn": 42, "name": "Chili", "violence_capable": False,
                  "applied": ["area"],
                  "refused": [{"lever": "hostility",
                               "reason": "the game does not offer Attack to a pawn "
                                         "incapable of violence"}],
                  "on_contact": "ignore", "gate": "all-refused"}],
    "counts": {"accepted": 1, "rejected": 1},
    "posture": {"ok": False, "seek_mod": True, "attack_n": 1}}}


def phase10():
    banner("PHASE 10 - the suite's OWN machinery (offline; proves it can FAIL)")

    # 10.1 shape() is the predicate dig() cannot be.
    check("10.1a", "shape() PASSES on a key that is present",
          probe(lambda: shape("x", "digest", GOOD_DIGEST, "data.posture.will_seek")),
          "pass", "fail")
    check("10.1b", "shape() FAILS on a RENAMED key — the whole point of phase 0",
          not probe(lambda: shape("x", "digest", GOOD_DIGEST, "data.posture.willSeek")),
          "fail", "pass")
    check("10.1c", "shape() PASSES on a present-and-NULL key (absent != null) — "
                   "which is exactly `seek_mod_missing` on a healthy bench",
          probe(lambda: shape("x", "d", GOOD_DIGEST, "data.posture.seek_mod_missing")),
          "pass", "fail")
    check("10.1d", "shape(kind=) FAILS when the type is wrong",
          not probe(lambda: shape("x", "d", GOOD_DIGEST, "data.posture.will_seek_n", str)),
          "fail", "pass")
    check("10.1e", "shape() FAILS on a path through a missing parent",
          not probe(lambda: shape("x", "d", GOOD_DIGEST, "data.nope.will_seek")),
          "fail", "pass")

    # 10.2 THE TRAP ITSELF, demonstrated rather than described.
    check("10.2a", "eq(...,None) PASSES on an ABSENT key — the hazard this suite "
                   "is built around",
          probe(lambda: eq("x", "t", GOOD_DIGEST, "data.posture.no_such_field", None)),
          "pass (and that is the hazard)", "fail")
    check("10.2b", "…which is why shape() is asserted FIRST: it fails on the same path",
          not probe(lambda: shape("x", "t", GOOD_DIGEST, "data.posture.no_such_field")),
          "fail", "pass")
    check("10.2c", "eq() FAILS on a wrong value",
          not probe(lambda: eq("x", "t", GOOD_DIGEST, "data.posture.will_seek", "9/9")),
          "fail", "pass")

    # 10.3 absent() — the other half, used for the NoStamp provenance check.
    check("10.3a", "absent() PASSES when the key really is absent",
          probe(lambda: absent("x", "t", GOOD_POSTURE, "data.action.provenance")),
          "pass", "fail")
    check("10.3b", "absent() FAILS when the key is present",
          not probe(lambda: absent("x", "t", GOOD_POSTURE, "data.action.journal_seq")),
          "fail", "pass")
    check("10.3c", "absent() FAILS when the key is present and NULL",
          not probe(lambda: absent("x", "t", GOOD_DIGEST,
                                   "data.posture.seek_mod_missing")),
          "fail", "pass")

    # 10.4 pawn_rows() / row_for() — phase 3's "refused BY NAME" rests entirely
    #      on these, and a version that only walked `accepted` would MISS the
    #      all-refused pawn, which is the pawn the bullet is about.
    names = sorted(r.get("name") for r in pawn_rows(GOOD_POSTURE))
    eq_val("10.4a", "pawn_rows() sees accepted AND rejected rows",
           names, ["Captain", "Chili"])
    eq_val("10.4b", "row_for() finds a pawn that landed in `rejected`",
           (row_for(GOOD_POSTURE, "Chili") or {}).get("gate"), "all-refused")
    eq_val("10.4c", "levers_refused() reads the lever off a refusal row",
           sorted(levers_refused(row_for(GOOD_POSTURE, "Chili"))), ["hostility"])
    eq_val("10.4d", "…and is empty, not None, for a pawn that refused nothing",
           levers_refused(row_for(GOOD_POSTURE, "Captain")), set())

    # 10.5 THE n/m CROSS-CHECK, proved to catch a drift between the string and
    #      its integers — the exact defect publishing both invites.
    drift = json.loads(json.dumps(GOOD_DIGEST))
    drift["data"]["posture"]["will_seek_n"] = 3
    p = drift["data"]["posture"]
    check("10.5a", "the n/m cross-check FAILS when the string and the integers drift",
          not probe(lambda: check("x", "n/m",
                                  p["will_seek"] == "%s/%s" % (p["will_seek_n"],
                                                               p["will_seek_of"]),
                                  "match", None)),
          "fail", "pass")
    g = GOOD_DIGEST["data"]["posture"]
    check("10.5b", "…and PASSES on the shipped shape",
          probe(lambda: check("x", "n/m",
                              g["will_seek"] == "%s/%s" % (g["will_seek_n"],
                                                           g["will_seek_of"]),
                              "match", None)),
          "pass", "fail")

    # 10.6 THE CLOSED VOCABULARY. A verdict added to the mod without being added
    #      here must fail, and a verdict silently dropped must fail too.
    oc = GOOD_DIGEST["data"]["posture"]["on_contact"]
    check("10.6a", "the vocabulary check PASSES on the shipped verdict set",
          sorted(oc.keys()) == sorted(CONTACT_VERDICTS), "equal sets", sorted(oc.keys()))
    short = {k: v for k, v in oc.items() if k != "flee"}
    check("10.6b", "…and FAILS when a verdict goes missing",
          sorted(short.keys()) != sorted(CONTACT_VERDICTS), "unequal", sorted(short.keys()))
    extra = dict(oc, **{"panic": 0})
    check("10.6c", "…and FAILS when an undocumented verdict appears",
          sorted(extra.keys()) != sorted(CONTACT_VERDICTS), "unequal", sorted(extra.keys()))

    # 10.7 bad_args() — every phase-0b refusal rests on it.
    refusal = {"ok": False, "op": "posture",
               "error": {"code": "bad-args",
                         "detail": "posture is THREE settings that must agree"}}
    check("10.7a", "bad_args() PASSES on a real refusal with the right needle",
          probe(lambda: bad_args("x", "t", refusal, "three settings")), "pass", "fail")
    check("10.7b", "…FAILS when the needle is absent, so a USELESS refusal cannot pass",
          not probe(lambda: bad_args("x", "t", refusal, "allow_empty_area")),
          "fail", "pass")
    check("10.7c", "…and FAILS on an ok:true envelope",
          not probe(lambda: bad_args("x", "t", GOOD_POSTURE, None)), "fail", "pass")

    # 10.8 ADVANCE ESCAPES. Worker B's contract (722c951): both are required
    #      non-empty strings, and a phase that forgot one would present as a
    #      driver refusal rather than a spec failure.
    merged = dict(ADVANCE_ESCAPES)
    merged.update({"ticks": 100})
    for k in ("unread_ok", "through_casualties"):
        check("10.8:%s" % k, "advance() always carries a non-empty %r" % k,
              isinstance(merged.get(k), str) and len(merged[k]) > 0,
              "a non-empty string", merged.get(k))
    eq_val("10.8c", "…and the caller's own args still win",
           merged.get("ticks"), 100)


# ---------------------------------------------------------------------- main --

PHASES = {1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5,
          6: phase6, 7: phase7, 8: phase8, 9: phase9, 10: phase10}
DEFAULT_SWEEP = [1, 2, 3, 4, 5, 9]


def main():
    global ARGS
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--root", default=DEFAULT_ROOT, help="the protocol root")
    p.add_argument("--state", default=DEFAULT_STATE,
                   help="where phase 7 leaves what phase 8 checks")
    p.add_argument("--phase", type=int, action="append",
                   choices=[0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
                   help="run only these phases (repeatable); phase 0 always runs "
                        "except under --selftest")
    p.add_argument("--selftest", action="store_true",
                   help="phase 10 only: the suite's own assertions, no bench")
    p.add_argument("--dry-run", action="store_true",
                   help="print the plan and every expectation, send nothing")
    p.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = p.parse_args()

    print("AutoRimmer acceptance - the posture verb and digest.posture (b1b3060)")

    # --selftest needs NO bench and must not be gated on one.
    if ARGS.selftest:
        phase10()
        print("")
        print("=" * 78)
        if FAILS:
            print("%sRESULT: %d FAILED of %d checks - %s%s"
                  % (RED, len(FAILS), CHECKS, ", ".join(FAILS), OFF))
            sys.exit(1)
        print("%sRESULT: selftest passed, all %d checks%s" % (GREEN, CHECKS, OFF))
        sys.exit(0)

    print("protocol root: %s" % ARGS.root)
    if not ARGS.dry_run and not os.path.exists(os.path.join(ARGS.root, "status.json")):
        print("%sno status.json under %s - start the bench with "
              "_RimWorld-Agent/run-agent.sh, or pass --root%s" % (RED, ARGS.root, OFF))
        sys.exit(2)
    if not ARGS.dry_run:
        os.makedirs(os.path.join(ARGS.root, "commands"), exist_ok=True)

    wanted = sorted(set(ARGS.phase or DEFAULT_SWEEP) - {0})
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
