#!/usr/bin/env python3
"""Acceptance runner for git-bug 1113019 — the unbounded `until` advance.

Same protocol, helpers and exit codes as `accept/722c951-advance-halt.py`; read
that file's header first, and `accept/1adc737-place-layout.py`'s SHAPE CONTRACT
note (`eq(..., None)` passes on an absent key, which is why `shape()` exists and
why phase 0 proves the two new keys before any later phase leans on them).

    ./accept/1113019-until-bound.py             # everything (phase 4 is SLOW)
    ./accept/1113019-until-bound.py --phase 1   # one phase (0 always runs)
    ./accept/1113019-until-bound.py --dry-run   # print the plan, send nothing
    ./accept/1113019-until-bound.py --selftest  # phase 9 only: NO bench needed

WHAT THIS IS TESTING, in one sentence: an `advance {until:{condition}}` always
has an exit — either a halt it can actually reach, or a bound somebody can see
in the envelope — and the one call that had neither is refused with the call the
caller meant.

THE DEFECT, from a live bench on 2026-09-01 (banked, partial, in
`accept/runs/s21-20260901/1113019/`). A caller did exactly what this project's
rules tell it to — no guessed tick counts, read the clock off the game — and
sent `advance {until:{condition:{path:"time.tick", op:">=", value:949}}}` with
the map at tick 949. Session 19's rule is that a `condition` requires a
false->true EDGE, and `time.tick >= N` is MONOTONE: once true there is no
crossing left, so the halt could never fire. With no `timeout_ticks` there was
no other exit either. It ran **187,541 ticks**, a bit over three in-game days,
and was stopped only by `722c951`'s own-faction casualty halt, which had shipped
hours earlier the same session. `ok: true`.

AND THE MOD WAS NOT BLIND TO IT, which is what makes the fix small. The envelope
said `true_when_armed: true, saw_false: false, first_false_tick: null` — session
19 put those fields there for exactly this case. So this is an ENFORCEMENT gap,
not an observation one, and the enforcement is derived from `true_when_armed`
rather than recomputing it.

WHAT SHIPPED, and therefore what this file proves:

  * PHASE 0 — the bench, and THE SHAPE CONTRACT. `data.timeout_ticks` and
    `data.timeout_source` exist on every advance, including one with no `until`
    at all, where they read 0 / "none".
  * PHASE 1 — THE REFUSAL. An already-true predicate with the edge required and
    no bound is `ok:false, error.code "unreachable-halt"`, the clock never
    moved, and the detail NAMES `edge:false` — then the suite sends that exact
    call and proves the advice works. Plus the widening: a supplied
    `timeout_ticks:0` is the same unreachable halt and is refused too.
  * PHASE 2 — THE REGRESSION BRANCH, and it must not move. The identical
    already-true predicate WITH a `timeout_ticks` still behaves exactly as
    session 19 specified: it does not halt, it runs to the caller's bound, and
    it says the bound was the caller's. Proved on `time.hour >= 0` (the case
    `accept/fc287ba-until-state.py` phase 3a already owns) AND on the MONOTONIC
    `time.tick >= <now>`, which that suite does not have and which is the shape
    with no second edge.
  * PHASE 3 — a predicate FALSE at arm time still halts on the edge, unchanged:
    once with the caller's bound, once with the default applied.
  * PHASE 4 — THE DEFAULT BOUND, measured. An `until` with no bound and a
    satisfiable-but-far-off predicate ends at 60,000 ticks — one in-game day —
    and the envelope says `timeout_source: "default"`. SLOW BY CONSTRUCTION: it
    runs a full in-game day.
  * PHASE 9 — `--selftest`, offline. The suite's own helpers over the banked
    defect envelope, plus the constant read out of the C# so that changing the
    number without changing this file is a FAIL.

WHAT THE NUMBER MEANS, because it changes how phase 4 should be read. Evan's
ruling, 2026-09-01: "a full day without doing anything while you're fully set is
pretty typical. Lots of things the colony does itself day to day and ideally if
something bad happens, you'll be woken up, you won't have to check." So the
bound is NOT a safety net on an error path — a quiet day is the normal idle unit
of the play loop, and an advance that runs a day and comes back
`reason:"timeout"` is the system working. Which makes the HALTS the wake-up
mechanism, and `722c951`'s casualty halt the primary interrupt.

IT MOVES A LOT OF GAME TIME. Phase 4 alone advances one full in-game day; phases
1-3 add a few thousand ticks. Run it on a bench you are willing to age.

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
FIXTURES = os.path.join(REPO, "accept", "runs", "s21-20260901", "1113019")
TIMEDRIVER = os.path.join(REPO, "Source", "AutoRimmer", "TimeDriver.cs")

GREEN, RED, YELLOW, CYAN, DIM, OFF = (
    "\033[32m", "\033[31m", "\033[33m", "\033[36m", "\033[2m", "\033[0m",
)
if not sys.stdout.isatty():
    GREEN = RED = YELLOW = CYAN = DIM = OFF = ""

ARGS = None
FAILS = []
CHECKS = 0
SEQ = 0
S = {}

# ONE IN-GAME DAY. The number Evan ruled and the number `TimeDriver` applies;
# phase 9 reads it back out of the C# rather than trusting this line, so a change
# to the constant that does not come here is a FAIL and not a silent drift.
DEFAULT_UNTIL_TIMEOUT = 60000

# The bound this harness adds to its OWN state-moving advances. Small, and
# nothing to do with the default under test.
SUITE_BOUND = 5000

# The lead used where a predicate must be FALSE at arm time and then become
# true. It is a tick count, and the house rule says a tick count in a suite is a
# defect unless the file says why no predicate expresses the wait. Here the tick
# count IS the predicate — `time.tick >= t0 + LEAD` is the monotonic shape under
# test — and LEAD's only job is to clear the protocol race that produced this
# issue: reading the clock and arming the advance are two round trips at a
# 0.25-1 s floor each (rwa/README.md), so at ~30 tps the clock moves 60-120
# ticks in between. 3000 is more than an order of magnitude past that window, so
# the predicate is false at arm whether or not the bench was paused, and it is
# far under the 60,000 default so the halt — not the bound — is what fires.
LEAD = 3000

# How late a state halt may be, on top of the advance's own `overshoot_bound`.
# `Config.ConditionScanFrames` is 15 frames and a frame runs up to
# `TickManager.TickRateMultiplier * 2` ticks — 30 at Ultrafast — so the cadence
# alone can carry an advance ~450 ticks past the crossing. Rounded up, and
# stated rather than assumed, because a check that pretended a halt is
# tick-exact would FAIL the driver for behaviour it documents.
CADENCE_SLACK = 600

# The reason this suite hands the mod when it uses an escape. It names the file,
# because the whole point of a required reason is that a post-mortem grepping
# `journal --types action` can tell WHO turned the guard off and why.
WHY = ("accept/1113019-until-bound.py: the subject under test is the advance's own "
       "bound and its arm-time refusal, so the journal and casualty guards must not "
       "preempt it; this harness does not read the journal between advances")


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=300):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acc1113019-%03d-%s" % (SEQ, slug)
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


# THE TWO WAYS THIS SUITE ADVANCES, named at every call site rather than hidden
# in one wrapper — the same discipline `accept/722c951-advance-halt.py` adopted,
# and for a sharper reason here: whether a `timeout_ticks` was supplied is the
# thing under test. A harness that quietly added one to every call would make
# every check in phase 1 unreachable, and a harness that added one to none of
# them would leave a runaway advance on the bench. So the choice is spelled out
# once per call.
#
# BOTH carry the escapes. `advance` refuses on an unread journal delta and on a
# bleedout deadline, and halts on an own-faction casualty (722c951 / 40ed42f#3);
# this file is not a play loop, it advances to reach the state its next
# assertion needs, and a refusal fired by a guard that is not the subject would
# have every check below measuring the wrong refusal.

def _escaped(args):
    a = dict(args)
    a.setdefault("unread_ok", WHY)
    a.setdefault("through_casualties", WHY)
    return a


def advance(args, timeout=300):
    """An advance the harness BOUNDS. For moving game state only."""
    a = _escaped(args)
    if "until" in a and "timeout_ticks" not in a:
        a["timeout_ticks"] = SUITE_BOUND
    return send("advance", a, timeout=timeout)


def advance_verbatim(args, timeout=300):
    """The caller's arguments EXACTLY — no bound added, ever. This is the shape
    that produced 1113019, and it is the subject of phases 1, 3b and 4. It may
    legitimately come back ok:false; that is usually the point."""
    return send("advance", _escaped(args), timeout=timeout)


def now_tick():
    e = send("digest", {"sections": ["time"]})
    t = dig(e, "data.time.tick")
    return t if isinstance(t, int) else None


def paused_clock():
    """Pause, then read the clock. Both halves matter: paused, `time.tick` is
    stable across the round trip that follows, so an `>=` predicate built on it
    is exactly-true at arm rather than probably-true."""
    send("pause")
    return now_tick()


def until_tick(target):
    return {"condition": {"path": "time.tick", "op": ">=", "value": target}}


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
    twice over: `first_false_tick` is DELIBERATELY null on an already-true
    predicate, and the two keys this issue adds were ABSENT before it."""
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


def token(detail, key):
    """The refusals put every number in `error.detail` as `key=value`, because a
    failed envelope carries no `data` block (Poller.BuildResultJson). This is the
    machine half of that contract."""
    m = re.search(r"\b%s=(-?\d+)\b" % re.escape(key), detail or "")
    return int(m.group(1)) if m else None


def flag(detail, key):
    """`token()` for the BOOLEAN tokens. The unreachable-halt refusal quotes
    `true_when_armed=` and `edge=`, which are the two fields it is derived from,
    and a number-only parser reads both as absent."""
    m = re.search(r"\b%s=(true|false)\b" % re.escape(key), detail or "")
    return None if m is None else m.group(1) == "true"


def unreachable(env, timeout_given):
    """THE RULE, RESTATED IN THE SUITE, so the fixture can be judged by
    something other than the code that produced it: an `until.condition` that
    was already true when it armed, with the edge required, and with no bound
    the caller supplied, has no halt it can ever reach."""
    return (dig(env, "data.until.true_when_armed") is True
            and dig(env, "data.until.edge") is True
            and not timeout_given)


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


def between(num, what, env, path, lo, hi):
    got = dig(env, path)
    ok = (isinstance(got, (int, float)) and not isinstance(got, bool)
          and lo <= got <= hi)
    check(num, "%s (%s)" % (what, path), ok, "in [%s, %s]" % (lo, hi), got)


def contains(num, what, env, path, needle):
    got = dig(env, path)
    ok = isinstance(got, str) and needle.lower() in got.lower()
    check(num, "%s (%s)" % (what, path), ok, "a string containing %r" % needle, got)


def refused(num, what, env, code, needle=None):
    """A REFUSAL IS THE ASSERTION. ok:false with the named code, and — when a
    needle is given — a detail that actually says the thing. A refusal with a
    useless message is only half the fix, and this one's message is half the
    issue: it has to hand back the call the caller meant."""
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
        ok = isinstance(dig(env, path), kind)
        want += " and a %s" % (kind.__name__ if isinstance(kind, type)
                               else "/".join(k.__name__ for k in kind))
    check(num, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


def probe(fn):
    """Run one assertion and report whether it PASSED, without letting it into
    the real tally."""
    global FAILS, CHECKS
    keep_f, keep_c = FAILS, CHECKS
    FAILS, CHECKS = [], 0
    try:
        import contextlib
        import io as _io
        with contextlib.redirect_stdout(_io.StringIO()):
            fn()
        return not FAILS
    finally:
        FAILS, CHECKS = keep_f, keep_c


def fixture(name):
    try:
        with open(os.path.join(FIXTURES, name), encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return None


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 - the bench, and THE SHAPE CONTRACT for the two new keys")

    e = send("status")
    precondition("0.1", "the bench answers `status`",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "no status envelope - start _RimWorld-Agent/run-agent.sh")
    registry = as_list(dig(e, "data.verbs"))
    check("0.2", "`status` publishes the registry and it lists `advance`",
          ARGS.dry_run or "advance" in registry, "advance in data.verbs",
          registry[:6])

    # The digest paths every predicate in this file addresses, proved to EXIST.
    # A suite that asserts against a path that is not there goes green forever;
    # a predicate over one is refused at arm time, which is a different failure
    # and would be attributed to the wrong thing.
    e = send("digest")
    S["digest0"] = e
    shape("0.3a", "digest", e, "data.time.tick", int)
    shape("0.3b", "digest", e, "data.time.hour", int)
    shape("0.3c", "digest", e, "data.time.year", int)
    shape("0.3d", "digest", e, "data.time.paused", bool)

    # ---- THE TWO NEW KEYS, on an advance with no `until` at all -------------
    #
    # Deliberately the plainest advance there is. `timeout_ticks` and
    # `timeout_source` are published on EVERY advance, not only on the ones this
    # issue is about, because a key that appears only sometimes cannot be
    # asserted — `eq(..., None)` passes on an absent key, which is the trap
    # phase 9 demonstrates on the real pre-fix envelope.
    send("pause")
    e = advance({"ticks": 60})
    S["plain"] = e
    check("0.4a", "a plain `advance {ticks:N}` succeeds", ARGS.dry_run or dig(e, "ok") is True,
          "ok:true", {"ok": dig(e, "ok"), "error": dig(e, "error")})
    shape("0.4b", "advance", e, "data.timeout_ticks", int)
    shape("0.4c", "advance", e, "data.timeout_source", str)
    eq("0.4d", "…and with no `until` and no `timeout_ticks` the source is `none`",
       e, "data.timeout_source", "none")
    eq("0.4e", "…with the bound reading 0, because the tick target IS the bound",
       e, "data.timeout_ticks", 0)
    eq("0.4f", "…and it halted on the tick target", e, "data.reason", "ticks")
    shape("0.4g", "advance", e, "data.overshoot_bound", int)
    print("  %sovershoot_bound %s at speed %s%s"
          % (DIM, dig(e, "data.overshoot_bound"), dig(e, "data.speed"), OFF))


# ------------------------------------------------------------------- phase 1 --

def phase1():
    banner("PHASE 1 - THE REFUSAL: a halt that cannot happen, and the call that can")

    t0 = paused_clock()
    precondition("1.0", "the clock reads a tick on a paused bench",
                 ARGS.dry_run or isinstance(t0, int),
                 "digest.time.tick is %s" % show(t0))
    if ARGS.dry_run:
        t0 = 949
    print("  %spaused at tick %s; the predicate below is `time.tick >= %s`, which is "
          "true NOW and monotone — there is no crossing left, ever%s" % (DIM, t0, t0, OFF))

    # ---- (a) the shape that ran 187,541 ticks -------------------------------
    e = advance_verbatim({"until": until_tick(t0)})
    S["refusal"] = e
    d = dig(e, "error.detail") or ""
    refused("1.1a", "an already-true predicate with the edge required and NO bound is "
                    "refused AT ARM TIME", e, "unreachable-halt")
    check("1.1b", "…and the refusal NAMES `edge:false` — the call the caller meant",
          ARGS.dry_run or "edge:false" in d, "the detail to contain 'edge:false'", d[:400])
    check("1.1c", "…quoting `true_when_armed=true`, the field it is DERIVED from",
          ARGS.dry_run or flag(d, "true_when_armed") is True, True, flag(d, "true_when_armed"))
    check("1.1d", "…and `edge=true`, the other half of the derivation",
          ARGS.dry_run or flag(d, "edge") is True, True, flag(d, "edge"))
    check("1.1e", "…and says the bound was ABSENT rather than printing a number",
          ARGS.dry_run or "timeout_ticks=absent" in d,
          "the detail to contain 'timeout_ticks=absent'", d[:400])
    check("1.1f", "…and names the predicate it refused", ARGS.dry_run or "time.tick" in d,
          "the detail to name time.tick", d[:400])
    check("1.1g", "…and cites the 187,541-tick run that produced the issue",
          ARGS.dry_run or "187,541" in d, "the detail to cite 187,541", d[:400])
    check("1.1h", "…and does NOT come back as `bad-args`: every argument is well "
                  "formed and the same call is valid on a different world state",
          ARGS.dry_run or dig(e, "error.code") != "bad-args",
          "a code other than bad-args", dig(e, "error.code"))

    # THE CLOCK MUST NOT HAVE MOVED. The refusal fires AFTER TimeDriver.Start has
    # set the game's speed, so a refusal that forgot to put it back would leave
    # the colony running unattended — the one failure the turn-based contract
    # must not ship, and the failure mode this whole issue is about.
    e2 = send("digest")
    eq("1.2a", "the refusal left the game PAUSED", e2, "data.time.paused", True)
    eq("1.2b", "…at speed Paused, not merely force-paused", e2, "data.time.speed", "Paused")
    check("1.2c", "…and the tick did not advance across the refusal",
          ARGS.dry_run or dig(e2, "data.time.tick") == t0,
          "tick still %s" % t0, dig(e2, "data.time.tick"))

    # ---- (b) THE ADVICE, TAKEN. Same predicate, `edge:false`, still no bound.
    # Handing back a correct call is only worth something if the correct call
    # works, so the suite sends it rather than trusting the sentence.
    t1 = paused_clock()
    if ARGS.dry_run:
        t1 = 949
    e = advance_verbatim({"until": {"condition": {"path": "time.tick", "op": ">=",
                                                  "value": t1, "edge": False}}})
    S["edge_false"] = e
    check("1.3a", "the call the refusal recommended is ACCEPTED",
          ARGS.dry_run or dig(e, "ok") is True, "ok:true",
          {"ok": dig(e, "ok"), "error": dig(e, "error")})
    eq("1.3b", "…and halts on the predicate rather than running to a bound", e,
       "data.reason", "condition")
    eq("1.3c", "…naming the path that tripped it", e, "data.halted_on.path", "time.tick")
    eq("1.3d", "…and identifying itself as a state halt", e, "data.halted_on.kind", "condition")
    check("1.3e", "…publishing NO halted_seq, because a state halt names no journal line",
          ARGS.dry_run or not has_key(e, "data.halted_seq"),
          "the key to be absent", dig(e, "data.halted_seq"))
    eq("1.3f", "…still reporting it was true when armed", e,
       "data.until.true_when_armed", True)
    eq("1.3g", "…and that the edge was not required", e, "data.until.edge", False)
    eq("1.3h", "…while the DEFAULT bound was applied underneath it", e,
       "data.timeout_source", "default")
    eq("1.3i", "…at one in-game day", e, "data.timeout_ticks", DEFAULT_UNTIL_TIMEOUT)

    # ---- (c) THE WIDENING: a supplied `timeout_ticks:0` is the same hole -----
    # The literal ruling is "no `timeout_ticks` was supplied". A supplied
    # NON-POSITIVE one is the same unreachable halt with the default explicitly
    # switched off, which is strictly worse, so it is refused too. Recorded in
    # DESIGN's decisions log as a widening rather than smuggled in.
    t2 = paused_clock()
    if ARGS.dry_run:
        t2 = 949
    e = advance_verbatim({"until": until_tick(t2), "timeout_ticks": 0})
    d = dig(e, "error.detail") or ""
    refused("1.4a", "`timeout_ticks:0` beside an already-true predicate is refused too",
            e, "unreachable-halt")
    check("1.4b", "…and the detail reports the number it was given, not 'absent'",
          ARGS.dry_run or token(d, "timeout_ticks") == 0, 0, token(d, "timeout_ticks"))
    e2 = send("digest")
    check("1.4c", "…and this refusal also left the clock where it found it",
          ARGS.dry_run or dig(e2, "data.time.tick") == t2,
          "tick still %s" % t2, dig(e2, "data.time.tick"))


# ------------------------------------------------------------------- phase 2 --

def phase2():
    banner("PHASE 2 - THE REGRESSION BRANCH: session 19, bounded, UNCHANGED")

    note("2.0", "every check in this phase asserts behaviour that shipped in "
                "session 19 and must not have moved. `accept/fc287ba-until-state.py` "
                "phase 3a owns 2.1; it is repeated here so the branch that changed "
                "the driver proves it too.")

    # ---- (a) fc287ba phase 3a's own case, verbatim --------------------------
    # `time.hour >= 0` is true always. With the edge required it can NEVER fire,
    # so the advance must exit on the caller's own timeout and say it was already
    # true when armed. THAT IS THE CORRECT BEHAVIOUR and the refusal in phase 1
    # must not have swallowed it.
    send("pause")
    e = advance_verbatim({"until": {"condition": {"path": "time.hour", "op": ">=", "value": 0}},
                          "timeout_ticks": 400, "speed": "fast"})
    S["bounded_hour"] = e
    check("2.1a", "an always-true predicate WITH a bound is still ACCEPTED",
          ARGS.dry_run or dig(e, "ok") is True, "ok:true",
          {"ok": dig(e, "ok"), "error": dig(e, "error")})
    shape("2.1b", "advance", e, "data.until", dict)
    shape("2.1c", "advance", e, "data.until.path")
    shape("2.1d", "advance", e, "data.until.true_when_armed")
    shape("2.1e", "advance", e, "data.until.saw_false")
    shape("2.1f", "advance", e, "data.until.evaluations")
    shape("2.1g", "advance", e, "data.until.eval_ms_avg")
    shape("2.1h", "advance", e, "data.until.eval_ms_per_frame")
    shape("2.1i", "advance", e, "data.until.every_frames")
    shape("2.1j", "advance", e, "data.until.edge")
    eq("2.1k", "it does NOT halt on the already-true predicate", e, "data.reason", "timeout")
    eq("2.1l", "…and says it was already true when it was armed", e,
       "data.until.true_when_armed", True)
    eq("2.1m", "…and that it never saw a false reading", e, "data.until.saw_false", False)
    ge("2.1n", "…having actually evaluated it", e, "data.until.evaluations", 1)
    eq("2.1o", "…and the edge was required", e, "data.until.edge", True)

    # The keys 1113019 adds, on the branch it must not disturb.
    shape("2.2a", "advance", e, "data.timeout_ticks", int)
    shape("2.2b", "advance", e, "data.timeout_source", str)
    eq("2.2c", "the bound in force is the CALLER's, not ours", e, "data.timeout_source", "caller")
    eq("2.2d", "…and it is the number the caller passed", e, "data.timeout_ticks", 400)
    bound = dig(e, "data.overshoot_bound") or 0
    between("2.2e", "…and the advance stopped AT it, within one frame's overshoot",
            e, "data.ticks_elapsed", 400, 400 + bound)

    # ---- (b) THE MONOTONIC SHAPE, which fc287ba does not cover ---------------
    # `time.hour >= 0` is always-true but not monotone in the interesting sense:
    # the hour rolls. `time.tick >= <now>` can never be false again for the life
    # of the save, which is the shape that produced this issue, and it is the one
    # a caller reaches for when it wants to sleep a fixed interval.
    t = paused_clock()
    if ARGS.dry_run:
        t = 949
    e = advance_verbatim({"until": until_tick(t), "timeout_ticks": 400, "speed": "fast"})
    S["bounded_tick"] = e
    check("2.3a", "the MONOTONIC already-true predicate with a bound is accepted, "
                  "not refused — the refusal is scoped to callers that gave no bound",
          ARGS.dry_run or dig(e, "ok") is True, "ok:true",
          {"ok": dig(e, "ok"), "code": dig(e, "error.code")})
    eq("2.3b", "…it does not halt on it", e, "data.reason", "timeout")
    eq("2.3c", "…it was true when armed", e, "data.until.true_when_armed", True)
    eq("2.3d", "…it never saw a false reading", e, "data.until.saw_false", False)
    check("2.3e", "…and `first_false_tick` is PRESENT and null, not missing",
          ARGS.dry_run or (has_key(e, "data.until.first_false_tick")
                           and dig(e, "data.until.first_false_tick") is None),
          "the key present with value null", dig(e, "data.until.first_false_tick"))
    eq("2.3f", "…the bound was the caller's", e, "data.timeout_source", "caller")
    eq("2.3g", "…and it was honoured", e, "data.timeout_ticks", 400)
    bound = dig(e, "data.overshoot_bound") or 0
    between("2.3h", "…stopping at the bound within one frame's overshoot",
            e, "data.ticks_elapsed", 400, 400 + bound)
    check("2.3i", "…and this advance is exactly what `unreachable()` would flag "
                  "IF the caller had given no bound — the rule, restated",
          ARGS.dry_run or unreachable(e, timeout_given=False) is True,
          "the suite's own rule to agree", unreachable(e, timeout_given=False))
    check("2.3j", "…and does NOT flag it once the caller's bound is counted",
          ARGS.dry_run or unreachable(e, timeout_given=True) is False,
          False, unreachable(e, timeout_given=True))


# ------------------------------------------------------------------- phase 3 --

def phase3():
    banner("PHASE 3 - a predicate FALSE at arm time still halts on the EDGE")

    # ---- (a) the clock crossing, with the caller's own bound ----------------
    e = send("digest")
    hour = dig(e, "data.time.hour")
    precondition("3.0", "the digest publishes an hour",
                 ARGS.dry_run or isinstance(hour, int),
                 "digest.time.hour is %s" % show(hour))
    target = ((hour or 0) + 1) % 24
    print("  %sadvancing until hour %s (it is %s) — no tick count is passed%s"
          % (DIM, target, hour, OFF))
    e = advance_verbatim({"until": {"condition": {"path": "time.hour", "op": "==",
                                                  "value": target}},
                          "timeout_ticks": 6000, "speed": "superfast"}, timeout=600)
    S["crossing"] = e
    eq("3.1a", "the clock predicate halts on the crossing", e, "data.reason", "condition")
    eq("3.1b", "…reporting the value that tripped it", e, "data.halted_on.observed", target)
    eq("3.1c", "…it was NOT true when armed", e, "data.until.true_when_armed", False)
    eq("3.1d", "…and it had to see a false reading first", e, "data.until.saw_false", True)
    shape("3.1e", "advance", e, "data.until.first_false_tick", int)
    eq("3.1f", "…the bound it did not need was the caller's", e, "data.timeout_source", "caller")
    eq("3.1g", "…and it was 6000", e, "data.timeout_ticks", 6000)
    e2 = send("digest")
    eq("3.1h", "the game's own clock agrees it is that hour", e2, "data.time.hour", target)
    eq("3.1i", "…and the advance left the game paused", e2, "data.time.paused", True)

    # ---- (b) the same halt with NO bound at all: the default rides along -----
    # This is the case the default exists for. The predicate is monotone and
    # FALSE at arm — the LEAD constant's whole job — so the edge fires long
    # before the 60,000-tick default, and the default's only visible effect is
    # that the envelope names it.
    t0 = paused_clock()
    if ARGS.dry_run:
        t0 = 949
    e = advance_verbatim({"until": until_tick(t0 + LEAD), "speed": "superfast"}, timeout=600)
    S["default_halt"] = e
    check("3.2a", "an `until` with NO bound and a reachable halt is accepted",
          ARGS.dry_run or dig(e, "ok") is True, "ok:true",
          {"ok": dig(e, "ok"), "code": dig(e, "error.code")})
    eq("3.2b", "…and halts on the predicate, not on the default bound", e,
       "data.reason", "condition")
    eq("3.2c", "…having been false when armed", e, "data.until.true_when_armed", False)
    eq("3.2d", "…and seen that false reading", e, "data.until.saw_false", True)
    eq("3.2e", "the envelope says the bound was OURS, not the caller's", e,
       "data.timeout_source", "default")
    eq("3.2f", "…and that it was one in-game day", e, "data.timeout_ticks",
       DEFAULT_UNTIL_TIMEOUT)
    ge("3.2g", "…and the game really did reach the tick the predicate named", e,
       "data.tick", t0 + LEAD)
    # The UPPER bound is the assertion that matters here: the advance stopped
    # because the predicate fired, NOT because it sat out an in-game day. That
    # it reached the target at all is 3.2g, read off the game's own clock, so
    # this does not need a lower bound guessed against the arm-time race.
    slack = (dig(e, "data.overshoot_bound") or 0) + CADENCE_SLACK
    elapsed = dig(e, "data.ticks_elapsed")
    check("3.2h", "…and it ran the interval asked for and stopped — nowhere near the "
                  "60,000-tick default (within one frame's overshoot plus one cadence "
                  "window)",
          ARGS.dry_run or (isinstance(elapsed, int) and elapsed <= LEAD + slack),
          "<= %s ticks" % (LEAD + slack), elapsed)


# ------------------------------------------------------------------- phase 4 --

def phase4():
    banner("PHASE 4 - THE DEFAULT BOUND, MEASURED (this phase runs a full in-game day)")

    note("4.0", "SLOW BY CONSTRUCTION: this advance is meant to run 60,000 ticks. "
                "At Ultrafast's nominal 900 tps that is ~67s of wall clock, and "
                "measured throughput on a live colony is lower.")

    e = send("digest")
    year = dig(e, "data.time.year")
    precondition("4.1", "the digest publishes a year",
                 ARGS.dry_run or isinstance(year, int),
                 "digest.time.year is %s" % show(year))
    target = (year or 5500) + 1

    # A SATISFIABLE predicate that cannot be satisfied inside the bound. `year >=
    # <this year + 1>` is a perfectly ordinary thing to ask, is FALSE at arm (so
    # the phase-1 refusal correctly does not fire), and is a full in-game YEAR
    # away — 60 days — so the 60,000-tick bound is what ends the advance and
    # there is no boundary case to get wrong. An impossible predicate would prove
    # the same thing while being a call nobody would write.
    print("  %sadvancing until year >= %s with NO timeout_ticks; the bound under test "
          "is the mod's own%s" % (DIM, target, OFF))
    send("pause")
    e = advance_verbatim({"until": {"condition": {"path": "time.year", "op": ">=",
                                                  "value": target}},
                          "speed": "ultrafast"}, timeout=900)
    S["day"] = e
    check("4.2a", "the unbounded-looking advance was accepted",
          ARGS.dry_run or dig(e, "ok") is True, "ok:true",
          {"ok": dig(e, "ok"), "code": dig(e, "error.code"),
           "detail": (dig(e, "error.detail") or "")[:300]})
    shape("4.2b", "advance", e, "data.timeout_ticks", int)
    shape("4.2c", "advance", e, "data.timeout_source", str)
    eq("4.2d", "the bound was APPLIED by the mod, not requested by the caller", e,
       "data.timeout_source", "default")
    eq("4.2e", "…and it is one in-game day", e, "data.timeout_ticks", DEFAULT_UNTIL_TIMEOUT)

    reason = dig(e, "data.reason")
    if ARGS.dry_run or reason == "timeout":
        eq("4.3a", "…and the advance ended ON that bound", e, "data.reason", "timeout")
        bound = dig(e, "data.overshoot_bound") or 0
        between("4.3b", "…at 60,000 ticks, within one frame's overshoot",
                e, "data.ticks_elapsed", DEFAULT_UNTIL_TIMEOUT,
                DEFAULT_UNTIL_TIMEOUT + bound)
        eq("4.3c", "…and it never saw the predicate come true", e,
           "data.until.true_when_armed", False)
    else:
        # Not a FAIL and not silently skipped. A full in-game day on a live
        # colony can legitimately end on a halt that is not the bound — a
        # force-pausing dialog, a red error, the stall watchdog. The bound's
        # PRESENCE is asserted above regardless; only "it is what ended this
        # particular advance" is unproven, and the operator is told.
        note("4.3", "this advance ended on reason %r at %s ticks, not on the "
                    "default bound, so 4.3a-c were not asserted. 4.2d/4.2e still "
                    "prove the bound was applied and published. Re-run phase 4 on "
                    "a quieter colony to close it."
             % (reason, dig(e, "data.ticks_elapsed")))

    e2 = send("digest")
    eq("4.4", "…and the advance handed back a PAUSED game, as every advance must",
       e2, "data.time.paused", True)


# ------------------------------------------------------------------- phase 9 --

def phase9():
    banner("PHASE 9 — the suite's OWN machinery, offline (no bench, no game)")

    # -- flag(): the boolean half of a refusal that has no data block ---------
    d = ("true_when_armed=true edge=true timeout_ticks=absent "
         "predicate=time.tick >= 949 observed=1013.")
    check("9.1a", "flag() reads true_when_armed=true", flag(d, "true_when_armed") is True,
          True, flag(d, "true_when_armed"))
    check("9.1b", "…and edge=true", flag(d, "edge") is True, True, flag(d, "edge"))
    check("9.1c", "…and answers None for a token that is not there, not False",
          flag(d, "saw_false") is None, None, flag(d, "saw_false"))
    check("9.1d", "…and does not read `timeout_ticks=absent` as a number",
          token(d, "timeout_ticks") is None, None, token(d, "timeout_ticks"))
    check("9.1e", "token() DOES read it when the refusal prints a real one",
          token("timeout_ticks=0 edge=true", "timeout_ticks") == 0, 0,
          token("timeout_ticks=0 edge=true", "timeout_ticks"))
    # The trap the \b anchors exist for: `edge=` must not match inside a longer
    # token, and `true_when_armed=` must not be read as a bare `armed=`.
    check("9.1f", "…and `edge=` does not match inside `no_edge=false`",
          flag("no_edge=false", "edge") is None, None, flag("no_edge=false", "edge"))

    # -- THE CONSTANT, read out of the C# rather than trusted -----------------
    #
    # This file names 60,000 in six places. If `TimeDriver` changes the number
    # and this suite does not, every check above would still pass against a
    # different mod. So the number is read back from the source.
    try:
        with open(TIMEDRIVER, encoding="utf-8") as fh:
            src = fh.read()
    except OSError:
        src = None
    if src is None:
        note("9.2", "Source/AutoRimmer/TimeDriver.cs is not in this checkout — the "
                    "constant cross-check is skipped")
    else:
        m = re.search(r"DefaultUntilTimeoutTicks\s*=\s*(\d+)", src)
        check("9.2a", "TimeDriver declares DefaultUntilTimeoutTicks",
              m is not None, "a declaration", None if m is None else m.group(0))
        check("9.2b", "…and it is the number this suite asserts (%d)" % DEFAULT_UNTIL_TIMEOUT,
              m is not None and int(m.group(1)) == DEFAULT_UNTIL_TIMEOUT,
              DEFAULT_UNTIL_TIMEOUT, None if m is None else int(m.group(1)))
        check("9.2c", "…and the arm-time refusal ships its own error code",
              'ErrUnreachableHalt = "unreachable-halt"' in src,
              'ErrUnreachableHalt = "unreachable-halt"', "not found")
        check("9.2d", "…and the refusal is derived from the PUBLISHED field, not a "
                      "second computation — TrueWhenArmed / EdgeRequired",
              "pw.TrueWhenArmed" in src and "pw.EdgeRequired" in src,
              "both accessors used in TimeDriver", None)

    # -- THE BANKED DEFECT ENVELOPE ------------------------------------------
    #
    # It is a PARTIAL, reconstructed record — see its own README — and it is used
    # for exactly the two things it can honestly carry: the fields git-bug
    # 1113019 quotes verbatim, and the absence of the two keys this issue adds,
    # which cannot have been published because the code did not exist.
    w = fixture("00-runaway-advance.json")
    if w is None:
        note("9.3", "accept/runs/s21-20260901/1113019/ is not in this checkout — the "
                    "envelope half of phase 9 is skipped; the string half above ran")
        return
    check("9.3a", "the fixture declares itself INCOMPLETE rather than posing as a capture",
          w.get("_complete") is False, False, w.get("_complete"))
    env = w.get("envelope")
    cmd_args = dig(w, "_command_sent.args") or {}
    check("9.3b", "…and the command it records really carried no `timeout_ticks`",
          "timeout_ticks" not in cmd_args, "the key absent from the sent args",
          sorted(cmd_args))

    eq("9.4a", "the defect envelope reported SUCCESS — that is the defect", env, "ok", True)
    eq("9.4b", "…stopped only by the casualty halt", env, "data.reason", "casualty")
    eq("9.4c", "…after 187,541 ticks", env, "data.ticks_elapsed", 187541)
    eq("9.4d", "…on a predicate that was true the moment it armed", env,
       "data.until.true_when_armed", True)
    eq("9.4e", "…that never saw a false reading", env, "data.until.saw_false", False)
    check("9.4f", "…and whose first_false_tick is PRESENT and null",
          has_key(env, "data.until.first_false_tick")
          and dig(env, "data.until.first_false_tick") is None,
          "present, null", dig(env, "data.until.first_false_tick"))
    eq("9.4g", "…with the edge required", env, "data.until.edge", True)

    # -- THE RULE, applied to the record --------------------------------------
    check("9.5a", "`unreachable()` flags the banked defect — the suite's own "
                  "restatement of the rule agrees with the mod's",
          unreachable(env, timeout_given=False) is True, True,
          unreachable(env, timeout_given=False))
    check("9.5b", "…and would NOT have flagged it had a bound been supplied",
          unreachable(env, timeout_given=True) is False, False,
          unreachable(env, timeout_given=True))

    # -- THE ABSENT-VS-NULL TRAP, on the record that genuinely lacks the keys --
    check("9.6a", "the banked envelope predates `timeout_ticks` and cannot carry it",
          not has_key(env, "data.timeout_ticks"), "data.timeout_ticks ABSENT",
          dig(env, "data.timeout_ticks"))
    check("9.6b", "eq(...,None) PASSES on that absent key — THE TRAP",
          probe(lambda: eq("x", "t", env, "data.timeout_ticks", None)),
          "pass (which is why shape() exists)", "fail")
    check("9.6c", "shape() FAILS on it — the trap, closed",
          not probe(lambda: shape("x", "advance", env, "data.timeout_ticks", int)),
          "fail", "pass")
    check("9.6d", "…and the same for timeout_source",
          not probe(lambda: shape("x", "advance", env, "data.timeout_source", str)),
          "fail", "pass")
    check("9.6e", "shape() PASSES on a key the record DOES carry",
          probe(lambda: shape("x", "advance", env, "data.until.true_when_armed")),
          "pass", "fail")

    # -- refused(): the assertion that would have caught it -------------------
    check("9.7a", "refused() FAILS on the defect envelope, however good its data — "
                  "ok:true is the whole complaint",
          not probe(lambda: refused("x", "t", env, "unreachable-halt")), "fail", "pass")
    real = {"ok": False, "op": "advance",
            "error": {"code": "unreachable-halt", "detail": d}}
    check("9.7b", "…and PASSES on a refusal envelope of the shape this issue ships",
          probe(lambda: refused("x", "t", real, "unreachable-halt")), "pass", "fail")
    check("9.7c", "…and FAILS on the WRONG code, which is the discriminator",
          not probe(lambda: refused("x", "t", real, "bad-args")), "fail", "pass")
    check("9.7d", "…and FAILS on a needle the detail does not contain",
          not probe(lambda: refused("x", "t", real, "unreachable-halt", "bleedout")),
          "fail", "pass")

    note("9.8", "the harness's own bound is %d ticks and is added ONLY by "
                "`advance()`; `advance_verbatim()` adds nothing, which is what "
                "makes phases 1, 3b and 4 mean anything" % SUITE_BOUND)


# ---------------------------------------------------------------------- main --

PHASES = {0: phase0, 1: phase1, 2: phase2, 3: phase3, 4: phase4, 9: phase9}


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
                    help="phase 9 only: the suite's own assertions over the banked "
                         "envelope and the C# constant. No bench, nothing sent.")
    ap.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = ap.parse_args()

    # --selftest needs NO bench and must not be gated on one.
    if ARGS.selftest:
        print("1113019 acceptance — mode: --selftest")
        print("offline; no bench, no protocol root, no game, nothing sent")
        phase9()
        banner("RESULT")
        if FAILS:
            print("%s%d/%d selftest checks FAILED: %s%s"
                  % (RED, len(FAILS), CHECKS, ", ".join(FAILS), OFF))
            return 1
        print("%sSELFTEST PASS — all %d checks%s" % (GREEN, CHECKS, OFF))
        return 0

    # Phase 9 is offline and is NOT in the default run — `--selftest` is its
    # front door. `--phase 9` alone still works and skips phase 0's bench
    # preconditions, because a phase that touches no bench must not be gated on
    # one.
    wanted = sorted(set(ARGS.phase or [p for p in PHASES if p != 9]))
    if 0 not in wanted and wanted != [9]:
        wanted = [0] + wanted

    print("1113019 acceptance — root %s" % ARGS.root)
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
