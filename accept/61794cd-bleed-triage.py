#!/usr/bin/env python3
"""Acceptance runner for 61794cd (the bleed-out clock) and 40ed42f (work
coverage, work-cover, triage) — the two halves session 20 shipped and NEITHER
of which had touched a bench.

    ./accept/61794cd-bleed-triage.py              # everything except 9
    ./accept/61794cd-bleed-triage.py --phase 6    # one phase (0 always runs)
    ./accept/61794cd-bleed-triage.py --dry-run    # print the plan, send nothing
    ./accept/61794cd-bleed-triage.py --selftest   # phase 9 only: NO bench needed

Read `accept/1adc737-place-layout.py`'s header first for the protocol and the
exit codes, and `accept/fc287ba-until-state.py`'s for the `advance {until:…}`
idiom. The SHAPE CONTRACT note there applies here twice over: `eq(..., None)`
passes on an ABSENT key, and half of what 61794cd asks to be proved is a key
that is present and null. Every null assertion in this file is preceded by a
`shape()`.

WHAT THIS IS TESTING, in one sentence: the two numbers the M1 run needed at tick
231,968 — "how long has this colonist got" and "can anybody reach them" — are
both published, both the game's own answer, and the row that names the cause of
death is no longer truncated away.

  * PHASE 1 — 61794cd, the clock. `health.ticks_until_bleedout` present-and-null
    for a non-bleeder, finite for a bleeder, on ONE pawn so the key's existence
    is proved in both states. `health.bleedout`'s whole field set. And
    `game_shows_clock` as a THREE-WAY: RimWorld/HealthCardUtility draws the
    line only above `bleedRateTotal > 0.01f` and prints "WontBleedOutSoon"
    instead of a number at `>= 60000` ticks — so a pawn can have a perfectly
    finite clock that the game does not show, and this phase stages exactly
    that case before staging the one where it does.
  * PHASE 2 — 61794cd, the headline: the Captain SHAPE. 20+ bleeding injuries
    plus `BloodLoss` at 0.478, and `BloodLoss` comes back as ROW 0 while
    `hediffs_more` reports honestly what was dropped. THIS IS A SHAPE
    RECONSTRUCTION, NOT A REPLAY — see the note in phase 2 and on the issue.
    It also settles the Scope's own rejected alternative by MEASUREMENT: the
    row's `life_threatening` is false below severity 0.60, so "life-threatening
    first" would have dropped it exactly as the old sort did.
  * PHASE 3 — 40ed42f, `digest.work_coverage`. A FINE row's three fields
    against an UNDER row's full diagnosis, Doctor's floor of 2 on `available`,
    the nine `requireCapableColonist` types at floor 1 on `capable`, and the
    invariant that matters: an under-covered row is NEVER truncated.
  * PHASE 4 — 40ed42f, "enabled but incapable", with a REAL pawn.
    `dev:damage {mode:"manipulation"}` is `HealthUtility
    .DamageLimbsUntilIncapableOfManipulation`, and every vanilla Doctor
    work-giver except `VisitSickPawn` requires Manipulation — so the row must
    separate `enabled` from `available` and name the capacity.
  * PHASE 5 — 40ed42f, `work-cover`. The dry run that writes nothing (proved
    from the LEDGER and from an independent digest, not from the verb's own
    stamp), the real promotion that reaches the journal as an `action`, and the
    already-covered answer. `data.ok` is the verdict; the envelope stays
    `ok:true`.
  * PHASE 6 — 40ed42f, `triage`. The casualty UNION proved with a pawn in each
    of the three states, the clock block proved identical to `pawn`'s (one
    builder, two callers), the NO-BED refusal asserted deliberately BEFORE a bed
    is staged (on a bare `--quicktest` map `TakeToBedGate` refuses everyone and
    `act` is never published — measured, and the fixture requirement that would
    otherwise cost a session), the estimate arithmetic checked against the rows
    it is derived from, and — the point of the whole issue — `act` SENT
    VERBATIM and shown to start a real rescue. THE CLOCK IS STOPPED FOR ALL OF
    IT: `act` is a snapshot, and on the s21 bench an act sent against a running
    world came back `cannot-rescue` because the rescuer had already carried the
    patient to the bed. The pause is asserted, not assumed.
  * PHASE 7 — THE M1 END-STATE, which is one fixture answering three questions.
    Down every colonist but one and: `work-cover` refuses with BOTH gates in
    turn (`too-few-candidates`, then `no-candidate`) carrying the counts that
    decide the follow-up; `advance {until:{condition:{path:"work_coverage.ok"}}}`
    is armed while coverage holds and HALTS once it does not; and the seam with
    `722c951` — with the escape ABSENT an advance across this suite's own downed
    colonists must not silently complete. Nobody else writes that last part:
    `722c951` proves the halt, this proves the halt is what an unescaped caller
    actually gets.
  * PHASE 8 — the standing invariant: no red errors across the whole run.
  * PHASE 9 — the suite's OWN machinery, offline. It runs the helpers over the
    twenty-four RAW envelopes banked at `accept/runs/s21-20260901/` — the
    orchestrator's pre-suite bench smoke — and over deliberately broken copies,
    and fails if a broken one passes. It also re-asserts, from those envelopes,
    the two findings this suite reports rather than checks (the `order` string
    and the dry run's `coverage_after`, git-bug 58794e4), so neither can be
    argued away. It also re-derives every
    constant this file hard-codes (the hediff cap, Doctor's floor, the row cap,
    the widget's two thresholds) out of the shipped source, so a change there
    fails HERE rather than making phases 1-6 assert the wrong numbers quietly.

WHAT THIS SUITE DELIBERATELY DOES NOT PROVE, and what is somebody else's job:

  * **The in-game text.** 61794cd's fourth acceptance bullet is "the number
    matches the game's own readout … compare against the in-game text". A
    suite cannot read RimWorld's UI. What it CAN do is assert that
    `game_shows_clock` reproduces `HealthCardUtility.DrawHediffListing`'s gate
    clause for clause, which phase 1 does in all three of its branches. The
    text comparison itself is an ORCHESTRATOR step and the exact recipe —
    which pawn, which tab, which number — is in
    `accept/61794cd-bleed-triage.md`. This file never claims it made that
    comparison.
  * **Captain's four reads, replayed.** Not decidable: the transcript records
    the OUTPUT of a pawn state, not the state, and `RUNS/m1-20260831/` has no
    save. 61794cd's own comment #2 says so. Phase 2 builds the SHAPE instead
    and says so in a `note()`.
  * **A Deathless pawn's `coma` outcome, and the `(Deathless)` disagreement
    between `outcome` and `game_shows_clock`.** It needs a Biotech pawn with
    the Deathless gene and then the same pawn with a destroyed brain, and
    there is no `dev:` verb that adds a gene. Reported as a NOTE, never as a
    check.
  * **The row cap actually truncating a work_coverage row.** `WorkCoverage
    .RowCap` is 14 and a full-DLC bench publishes 12 essential types, so the
    cap CANNOT fire. Phase 3 proves the invariant that survives that ("every
    name in `under` appears in `rows`") and phase 9 re-derives both numbers
    from the source so the day a mod pushes the count past 14, the claim is
    re-examined rather than assumed.

IT WRECKS THE COLONY IT RUNS ON. By design: it damages colonists, destroys a
pawn's hands, anaesthetises one, gives another influenza, downs the rest and
leaves them down. Run it on a bench you are willing to throw away, and expect to
reload before running it again.

THE PHASES ARE A SEQUENCE, NOT A MENU. Phase 0 assigns every subject its role
once; 1 and 2 build one pawn into the Captain shape; 3 and 4 stage the coverage
rows on top of that roster; 6 needs 5's repair; 7 needs 6's casualties. `--phase
N` on its own is for RE-RUNNING one phase after a full sweep, and each phase's
`precondition()` names the pawn it wanted rather than failing obscurely.

IT PAUSES THE GAME AT THE TOP OF PHASE 0 AND KEEPS IT PAUSED. Two reasons, both
paid for on a bench: the fixture is a colonist on a ~7,000-tick bleed clock and
a running bench kills him mid-suite, and `triage`'s `act` is a SNAPSHOT that a
running world invalidates between the read and the send. Both are asserted, not
assumed — see `pause()`.

THE `advance` ESCAPES ARE ON, AND THAT IS THE POINT OF PHASE 7. `722c951` makes
`advance` refuse on an unread journal delta and halt on an own-faction downing.
This suite's whole fixture is own-faction downings, so every `advance` it sends
would be refused. The module-level `advance()` wrapper injects both escapes with
a reason naming this file — see its comment. Phase 7 is the one place that sends
an advance WITHOUT them, because a suite that turns the discipline off silently
is the exact failure `722c951` exists to stop.

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
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
# The orchestrator's pre-suite bench smoke. Phase 9 runs the assertion machinery
# over these RAW envelopes rather than over fixtures this file invented, which
# is the difference between "the helpers agree with themselves" and "the helpers
# agree with the game".
S21 = os.path.join(HERE, "runs", "s21-20260901")

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
CAPTURE = None  # phase 9 only; see probe()

# The verbs these two issues touch. 0.3 asserts every one is registered: a verb
# that failed to register produces downstream failures indistinguishable from a
# bad fixture, which is what cost accept/s13-mod-surface.py two red checks.
OPS = ["digest", "pawn", "pawns", "triage", "work-cover", "work-priorities",
       "rescue", "advance", "journal", "status", "things",
       "dev:damage", "dev:add-hediff", "dev:heal", "dev:spawn-pawn",
       "dev:spawn-thing"]

# PawnSerializer.Bleedout's dictionary, exactly. Asserted as a SET, not merely
# per member: a field appearing here is either a deliberate addition or
# something leaking out of the health tracker, and both must be looked at.
# Phase 9 re-derives this list out of PawnSerializer.cs.
BLEEDOUT_KEYS = ["ticks", "blood_loss_severity", "outcome", "deathless_gene",
                 "prevents_death", "game_shows_clock", "cite"]

# WorkCoverage.Section's FINE row: three fields, and the claim "a row that is
# FINE costs three fields" is only worth anything if it is checked as a set.
FINE_ROW_KEYS = ["work", "floor", "have"]
# …and the UNDER row's full diagnosis. `enabled_but_incapable` IS in this list
# as of the session-21 fix: it used to be emitted only when the row had an
# impaired pawn, so an absent key meant both "nobody here is impaired" and
# "this build does not publish that key" — the conflation 61794cd already ruled
# against for `ticks_until_bleedout`. It is now an always-present, possibly
# empty list, like its `available_pawns` and `candidates` siblings. 3.3p asserts
# the EMPTY case, which is the half that proves the emission is unconditional.
UNDER_ROW_KEYS = ["work", "floor", "floor_on", "floor_by", "have", "short_by",
                  "capable", "enabled", "available", "available_pawns", "candidates",
                  "enabled_but_incapable"]

# PawnSerializer.HediffCap. Phase 9 re-parses it from the source.
HEDIFF_CAP = 20
# WorkCoverage.DoctorFloor and WorkCoverage.RowCap. Same.
DOCTOR_FLOOR = 2
WORK_ROW_CAP = 14
# PawnActs.PathCandidateCap and CasualtyCap. Same.
PATH_CANDIDATE_CAP = 3

# RimWorld/HealthCardUtility.DrawHediffListing's two thresholds, transcribed.
# The line is drawn at all only above 0.01 (NOT the 0.0001f the ESTIMATOR uses,
# which is the whole reason this is a separate number), and a clock of a full
# day or more prints "WontBleedOutSoon" instead of a figure. Phase 9 re-parses
# both out of PawnSerializer.cs's reproduction of them.
CARD_BLEED_FLOOR = 0.01
CARD_WONT_BLEED_SOON = 60000

# The severity the M1 post-mortem back-solved for Captain at tick 231,968. Used
# verbatim so the fixture is anchored to the run it reconstructs, and because it
# sits BELOW BloodLoss's fifth stage (`minSeverity 0.60`, the only one carrying
# `lifeThreatening`) — which is what makes check 2.6 possible.
CAPTAIN_SEVERITY = 0.478

# Doctor is the only floor this suite can move, because it is the only one on
# AVAILABILITY. The nine `requireCapableColonist` types floor on CAPABILITY, and
# making one of those unmet would need every colonist incapable of, say,
# Cleaning — not stageable, and phase 3 asserts their floor rather than breaking
# it.
DOCTOR = "Doctor"

# How many colonists the fixture needs. Six: a bleeder, a handless doctor, a
# downed patient, a sick-but-standing patient and two intact rescuers. A
# `--quicktest` map starts with three, so `ensure_roster` tops up.
WANT_COLONISTS = 6
WANT_BEDS = 2


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=240):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acc61794cd-%03d-%s" % (SEQ, slug)
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


# THE ESCAPE, AND WHY IT IS ON.
#
# `722c951` (worker B, this same round) makes `advance` REFUSE to start while
# the journal carries an unread delta, and HALT on an own-faction downing or
# death. Both are right, and both are aimed at exactly the failure this suite
# reconstructs: the M1 run advanced past its own casualty.
#
# This suite's ENTIRE FIXTURE is own-faction casualties. It damages colonists
# on purpose, destroys a pawn's hands on purpose, and downs pawns on purpose,
# because that is the only way to produce the states 61794cd and 40ed42f
# describe. Every one of its verbs also writes a journal row. So without the
# escapes not one `advance` in this file would start.
#
# The escapes are therefore injected here, in one place, with a reason that
# NAMES THIS FILE — never per call site, where they would accumulate silently
# and where a later reader could not tell which advances were deliberately
# unguarded. `raw_advance()` below is the un-escaped form, and phase 7 is the
# only caller: an acceptance suite that turns the discipline off without ever
# proving the discipline is on has removed the thing it was meant to test.
ESCAPE = ("accept/61794cd-bleed-triage.py: this suite's fixture IS own-faction "
          "casualties (61794cd/40ed42f), and every verb it sends journals — "
          "phase 7 proves the un-escaped refusal")


def advance(args, timeout=300):
    a = dict(args)
    a.setdefault("unread_ok", ESCAPE)
    a.setdefault("through_casualties", ESCAPE)
    return send("advance", a, timeout=timeout)


def raw_advance(args, timeout=300):
    """No escapes. Phase 7 only."""
    return send("advance", args, timeout=timeout)


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
    """dig() cannot tell `absent` from `present and null`, and this suite is
    ABOUT that distinction: `ticks_until_bleedout` is deliberately present and
    null for a pawn who is not bleeding, and a serializer that dropped the key
    entirely would pass every `eq(..., None)` in this file."""
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
    return "null" if v is None else json.dumps(v, separators=(",", ":"))[:400]


def num(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


# ------------------------------------------------------------------- asserts --

def check(num_, what, ok, expected, actual):
    global CHECKS
    CHECKS += 1
    if CAPTURE is not None:
        CAPTURE.append(ok)
        if not ok:
            FAILS.append(num_)
        return
    if ARGS.dry_run:
        print("  %-7s EXPECT  %s: %s" % (num_, what, expected))
        return
    if ok:
        print("  %s%-7s PASS    %s%s" % (GREEN, num_, what, OFF))
        return
    print("  %s%-7s FAIL    %s%s" % (RED, num_, what, OFF))
    print("          expected: %s" % expected)
    print("          actual:   %s" % show(actual))
    FAILS.append(num_)


def eq(num_, what, env, path, want):
    got = dig(env, path)
    ok = (want is None and got is None) or got == want
    check(num_, "%s (%s)" % (what, path), ok, show(want), got)


def eq_val(num_, what, got, want):
    check(num_, what, got == want, show(want), got)


def ge(num_, what, env, path, want):
    got = dig(env, path)
    ok = num(got) and got >= want
    check(num_, "%s (%s)" % (what, path), ok, ">= %s" % want, got)


def lt(num_, what, env, path, want):
    got = dig(env, path)
    ok = num(got) and got < want
    check(num_, "%s (%s)" % (what, path), ok, "< %s" % want, got)


def contains(num_, what, env, path, needle):
    got = dig(env, path)
    ok = isinstance(got, str) and needle.lower() in got.lower()
    check(num_, "%s (%s)" % (what, path), ok, "a string containing %r" % needle, got)


def one_of(num_, what, env, path, allowed):
    got = dig(env, path)
    check(num_, "%s (%s)" % (what, path), got in allowed, "one of %s" % (allowed,), got)


def shape(num_, verb, env, path, kind=None):
    ok = has_key(env, path)
    want = "the key to be present"
    if ok and kind is not None:
        ok = isinstance(dig(env, path), kind)
        want += " and a %s" % kind.__name__
    check(num_, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def absent(num_, what, env, path):
    ok = not has_key(env, path)
    check(num_, "%s (%s)" % (what, path), ok, "the key to be ABSENT", dig(env, path))


def keys_exactly(num_, what, env, path, want):
    got = dig(env, path)
    if not isinstance(got, dict):
        check(num_, "%s (%s)" % (what, path), False, "a dict at that path", got)
        return
    extra = sorted(set(got) - set(want))
    missing = sorted(set(want) - set(got))
    check(num_, "%s (%s)" % (what, path), not extra and not missing,
          "exactly %s" % (sorted(want),),
          {"extra": extra, "missing": missing})


def bad_args(num_, what, env, needle=None):
    """A REFUSAL IS THE ASSERTION. `ok:false` with code bad-args, and when a
    needle is given, a sentence that actually names the problem — a refusal with
    a useless message is only half the fix."""
    code = dig(env, "error.code")
    ok = dig(env, "ok") is False and code == "bad-args"
    if ok and needle is not None:
        detail = dig(env, "error.detail") or ""
        ok = needle.lower() in detail.lower()
    check(num_, what, ok, "ok:false, code bad-args%s"
          % ("" if needle is None else ", detail naming %r" % needle),
          {"ok": dig(env, "ok"), "code": code,
           "detail": (dig(env, "error.detail") or "")[:300]})


def note(num_, text):
    if CAPTURE is not None:
        return
    print("  %s%-7s NOTE    %s%s" % (YELLOW, num_, text, OFF))


def finding(num_, text):
    """A DEFECT IN THE SHIPPED MOD, reported rather than asserted.

    Deliberately not a `check`: an acceptance suite's exit code answers "were
    the spec's acceptance bullets met", and a suite that goes permanently red
    over a metadata string teaches the next session to ignore its own colour.
    Every FINDING here is also filed as a comment on its issue, which is where a
    defect is actionable. The summary line repeats them so they cannot scroll
    away."""
    FINDINGS.append((num_, text))
    if CAPTURE is not None:
        return
    print("  %s%-7s FINDING %s%s" % (CYAN, num_, text, OFF))


def precondition(num_, what, ok, detail):
    if ARGS.dry_run:
        print("  %s%-7s NEEDS   %s%s" % (CYAN, num_, what, OFF))
        return
    if ok:
        print("  %s%-7s OK      precondition: %s%s" % (DIM, num_, what, OFF))
        return
    print("  %s%-7s SKIP    precondition NOT MET: %s%s" % (YELLOW, num_, what, OFF))
    print("          %s" % detail)
    print("          This is a FIXTURE gap, not a failure of the spec.")
    # NOTHING STATEFUL IS OPEN AT ANY precondition() IN THIS FILE, deliberately.
    # Session 20 lost a whole run to a `precondition` that exited mid-trade and
    # poisoned the NEXT run's phase 0 with three red checks. This suite opens no
    # trade, no comms call, no dialog and no layout transaction — the only
    # durable thing it leaves is damaged pawns, which cannot leak into another
    # run's protocol state.
    sys.exit(2)


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


# ------------------------------------------------- the widget gate, in python --

def game_shows_clock(bleed_rate, ticks, deathless):
    """RimWorld/HealthCardUtility.DrawHediffListing's own three-way, reproduced
    here so the suite can assert the MOD's reproduction against a SECOND
    reproduction rather than against itself:

        if (bleedRateTotal > 0.01f)                    // else no line at all
            deathless ? "(Deathless)"                  // a word, not a number
          : num >= 60000 ? "(WontBleedOutSoon)"        // a word, not a number
          : "(TimeToDeath …)"                          // THE NUMBER

    This is what "the number matches the game's own readout" can honestly mean
    inside a suite. The TEXT comparison is the orchestrator's step and lives in
    the .md — see the module docstring."""
    if not num(bleed_rate) or bleed_rate <= CARD_BLEED_FLOOR:
        return False
    if deathless:
        return False
    if ticks is None or not num(ticks):
        return False
    return ticks < CARD_WONT_BLEED_SOON


def assert_card_gate(num_, env, base):
    """`game_shows_clock` at `base` agrees with the widget gate recomputed from
    the same envelope's own `bleed_rate`, `bleedout.ticks` and
    `bleedout.deathless_gene`. Asserted on EVERY health read this suite takes,
    not once, because the three branches are reached by different fixtures."""
    rate = dig(env, base + ".bleed_rate")
    ticks = dig(env, base + ".bleedout.ticks")
    deathless = dig(env, base + ".bleedout.deathless_gene")
    want = game_shows_clock(rate, ticks, bool(deathless))
    got = dig(env, base + ".bleedout.game_shows_clock")
    check(num_, "game_shows_clock reproduces HealthCardUtility's gate "
                "(rate=%s ticks=%s deathless=%s)" % (rate, ticks, deathless),
          got is want, want, got)


# ------------------------------------------------------------------ fixtures --

def health(pid, sections=("health",)):
    """`pawn` TAKES `id`, NOT `pawn`. Measured on the s21 smoke: `--pawn` comes
    back bad-args, "missing required arg 'id' (number)"."""
    return send("pawn", {"id": pid, "sections": list(sections)})


def roster():
    e = send("pawns", {"filter": "colonist", "cap": 200, "order": "id"})
    rows = [r for r in as_list(dig(e, "data.list")) if isinstance(r, dict)]
    return e, rows


def ensure_roster(num_):
    """Top the colony up to WANT_COLONISTS. A `--quicktest` map starts with
    three and this fixture needs six; spawning them is `dev:spawn-pawn`, whose
    default `pos` is the colony anchor and whose default kind is Colonist."""
    if ARGS.dry_run:
        return [901, 902, 903, 904, 905, 906]
    e, rows = roster()
    precondition(num_ + "a", "`pawns` answers with a colonist roster",
                 dig(e, "ok") is True and len(rows) > 0,
                 "pawns returned %s" % show(e))
    if len(rows) < WANT_COLONISTS:
        need = WANT_COLONISTS - len(rows)
        print("  %sroster is %d, spawning %d more colonists%s"
              % (DIM, len(rows), need, OFF))
        e = send("dev:spawn-pawn", {"kind": "Colonist", "count": need, "spread": 6})
        precondition(num_ + "b", "dev:spawn-pawn topped the roster up",
                     dig(e, "ok") is True,
                     "dev:spawn-pawn refused: %s — is the dev god-hand on? "
                     "(Dev.Gate)" % show(dig(e, "error")))
        e, rows = roster()
    precondition(num_ + "c", "at least %d colonists" % WANT_COLONISTS,
                 len(rows) >= WANT_COLONISTS,
                 "only %d colonists after topping up; this fixture needs %d "
                 "(a bleeder, a handless doctor, a downed patient, a sick one "
                 "and two rescuers)" % (len(rows), WANT_COLONISTS))
    eq("%s.0" % num_, "…and none of them is cut off by the roster cap",
       {"data": {"more": dig(e, "data.more")}}, "data.more", 0)
    ids = [r["id"] for r in rows if "id" in r]
    print("  %sroster: %s%s" % (DIM, [(r.get("id"), r.get("name")) for r in rows], OFF))
    return ids


def bed_count():
    """How many beds are on the map, from the game rather than from memory.

    `TakeToBedGate("rescue", …)` ends in `RestUtility.FindBedFor`, and with NO
    bed on the map every verdict is `no-rescuer` and `act` is never published —
    measured on the s21 bench, and the fixture requirement that would otherwise
    cost a session (40ed42f #3). A bare `--quicktest` map has no buildings at
    all.

    WHICH REFUSAL a candidate gets depends on the PATIENT, not on the bed:
    `no-bed` is the LAST clause of the rescue branch and only a DOWNED,
    not-in-bed patient reaches it, because `HealthAIUtility.CanRescueNow` ->
    `WantsToBeRescued` opens on `!pawn.Downed` and answers `cannot-rescue`
    first. Phase 6 asserts both halves against the row each one belongs to."""
    e = send("things", {"def": "Bed"})
    have = dig(e, "data.total")
    if not num(have):
        have = len(as_list(dig(e, "data.list")))
    return have or 0


def spawn_bed(num_, near_pawn):
    """One bed, ON the casualty's own cell, exact-or-refuse.

    `pos:"pawn:<id>"` puts it where the patient already is, which is what makes
    the carry leg short enough for `in-time` to be an honest verdict rather than
    an artefact of where the map's furniture happened to be. `mode:"direct"`
    because `near` slides: GenPlace's radial search would land the bed on a cell
    nobody asked about."""
    e = send("dev:spawn-thing", {"def": "Bed", "stuff": "WoodLog",
                                 "pos": "pawn:%s" % near_pawn,
                                 "mode": "direct"})
    precondition(num_, "a bed for RestUtility.FindBedFor to find",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "dev:spawn-thing could not stage a bed (%s). Place one by hand "
                 "beside the casualty and re-run — with no bed the rescue gate "
                 "refuses every rescuer (`no-bed` for a downed patient, "
                 "`cannot-rescue` for a standing one) and this whole phase "
                 "proves nothing." % show(dig(e, "error")))
    return e


def pause(num_=None, what=None):
    """PAUSE, AND SAY SO.

    Two reasons, and both were paid for on a bench.

    1. THE FIXTURE IS PERISHABLE. This suite deliberately puts a colonist on a
       7,000-tick bleed clock and then asks four more phases' worth of
       questions. At Normal speed the bench runs while it is being interrogated,
       and the subject of phase 2 dies somewhere in phase 5. The colony is
       paused at the top of phase 0 and kept paused.
    2. `triage`'s `act` IS A SNAPSHOT AND THE WORLD MOVES UNDER IT. On the s21
       bench the orchestrator read `verdict:"in-time"` with a populated `act`,
       sent it verbatim, and got `cannot-rescue` — not a gate mismatch (triage
       and `rescue` call the same two predicates in the same order) but the
       rescuer having already carried the patient to the bed in the seconds
       between the read and the send. `HealthAIUtility.CanRescueNow` is false
       for a patient already in a bed, so the refusal was the game being right.

    The pause is ASSERTED rather than assumed, because a `pause` that silently
    no-ops (`TickManager.TogglePaused` -> `PlayerCanControl`, false during a
    fade or a cutscene) would put both hazards straight back."""
    e = send("pause")
    if num_ is not None:
        eq(num_, what or "the clock is stopped — the fixture is perishable and "
                         "`act` is a snapshot", e, "data.paused", True)
    return e


def bleed_up(num_, pid, want_rate, cap=8, hits=8, amount=2):
    """Damage `pid` in SMALL repeated bites until `bleed_rate` reaches
    `want_rate`, and return the last damage envelope.

    THE TRAP THIS ROUTINE EXISTS FOR: `dev:damage {mode:"amount"}`'s loop is
    `for (i < hits && !pawn.Downed && !pawn.Dead)` — it stops the instant the
    pawn goes down. One call of `hits:20 amount:6` therefore downs the subject
    early and lands FEWER injuries than three calls of `hits:8 amount:2`, which
    is how the s21 smoke got a standing pawn to 53 hediffs. `hits` is capped
    1..20 by the verb.

    It also returns as soon as the pawn is DOWNED, because a downed subject is
    a different fixture and the caller must be told rather than looped at."""
    last = None
    for _ in range(cap):
        last = send("dev:damage", {"pawn": pid, "mode": "amount",
                                   "hits": hits, "amount": amount})
        if ARGS.dry_run:
            return {"ok": True, "data": {"bleed_rate": want_rate, "downed": False}}
        if dig(last, "ok") is not True:
            precondition(num_, "dev:damage lands on the subject",
                         False, "dev:damage refused: %s" % show(dig(last, "error")))
        rate = dig(last, "data.bleed_rate") or 0
        if dig(last, "data.downed") is True or rate >= want_rate:
            break
    return last


def find_row(rows, work):
    for r in rows:
        if isinstance(r, dict) and r.get("work") == work:
            return r
    return None


def doctor_row(env, base="data.work_coverage"):
    return find_row(as_list(dig(env, base + ".rows")), DOCTOR)


def set_doctor(pids, priority):
    return send("work-priorities",
                {"set": [{"pawns": list(pids), "works": [DOCTOR],
                          "priority": priority}]})


def journal_since(seq, types=None, limit=200):
    args = {"since_seq": seq, "limit": limit}
    if types:
        args["types"] = list(types)
    return send("journal", args)


def watermark():
    return dig(send("journal", {"since_seq": 999999999, "limit": 1}),
               "data.last_seq") or 0


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 - the bench, the verbs, and THE SHAPE CONTRACT")

    e = send("status")
    precondition("0.1", "the bench answers `status`",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "no status envelope - start _RimWorld-Agent/run-agent.sh "
                 "(the ORCHESTRATOR does this; a worker never launches)")
    precondition("0.1b", "a game is loaded",
                 ARGS.dry_run or dig(e, "data.gameLoaded") is True,
                 "status says gameLoaded=%s — load a colony first"
                 % show(dig(e, "data.gameLoaded")))
    # FIRST ACT: STOP THE CLOCK. See pause()'s header — the fixture this suite
    # builds is a colonist on a bleed clock, and a running bench kills him
    # somewhere around phase 5.
    pause("0.1c", "the very first act is to stop the clock: this suite's fixture "
                  "is a colonist bleeding out, and a running bench would kill "
                  "him mid-suite")

    # THE WATERMARK. JournalVerbs.Read updates last_seq BEFORE the
    # `seq <= since_seq` skip, so `{limit:1}` reports the SECOND row's seq;
    # pushing since_seq past the end reads to the end and yields the true max.
    e = send("journal", {"since_seq": 999999999, "limit": 1})
    shape("0.2", "journal", e, "data.last_seq")
    S["seq0"] = dig(e, "data.last_seq") or 0
    print("  %sjournal watermark: seq0=%s%s" % (DIM, S["seq0"], OFF))

    e = send("status")
    registry = as_list(dig(e, "data.verbs"))
    check("0.3", "`status` publishes the registry", len(registry) > 0,
          "a non-empty data.verbs list", registry[:5])
    for i, verb in enumerate(OPS):
        ok = ARGS.dry_run or verb in registry
        check("0.3%s" % "abcdefghijklmnopqrstuvwxyz"[i],
              "the registry lists %s" % verb, ok,
              "%s in the registry" % verb, None if ok else len(registry))

    # ---- the fixture, and every subject named ONCE --------------------------
    ids = ensure_roster("0.4")
    S["ids"] = ids
    # ROLES ARE ASSIGNED HERE AND NOWHERE ELSE. Each phase's wreckage lands on a
    # named subject, so a later phase's precondition can say WHICH pawn it
    # wanted rather than "the roster is wrong". The three casualty roles are
    # chosen so each satisfies exactly ONE clause of triage's union — see
    # phase 6.
    S["bleeder"] = ids[0]    # phases 1-2: the Captain shape. Bleeding, standing.
    S["handless"] = ids[1]   # phase 4: Doctor enabled, Manipulation destroyed.
    S["patient"] = ids[2]    # phase 6: anaesthetised. Downed, NOT bleeding.
    S["sick"] = ids[3]       # phase 6: influenza. Needs tending, NOT downed.
    S["rescuer"] = ids[4]    # phase 6: kept intact, and the one who carries.
    S["spare"] = ids[5:]     # phase 7: the rest of the roster, downed.
    print("  %sroles: bleeder=%s handless=%s patient=%s sick=%s rescuer=%s "
          "spare=%s%s" % (DIM, S["bleeder"], S["handless"], S["patient"],
                          S["sick"], S["rescuer"], S["spare"], OFF))
    # BEDS ARE NOT STAGED HERE. Phase 6 asserts the no-bed refusal DELIBERATELY
    # before it stages one, because "every rescuer was gated out, and here is
    # the game's own sentence for why" is a real answer about the colony and is
    # the state a bare `--quicktest` map is actually in.
    S["beds0"] = 0 if ARGS.dry_run else bed_count()
    print("  %sbeds on the map at start: %s%s" % (DIM, S["beds0"], OFF))

    # ---- 61794cd's dig paths, proved to EXIST --------------------------------
    # THE SHAPE CONTRACT AT ITS SHARPEST. Every null assertion later in this
    # file is `eq(..., None)`, which PASSES on an absent key — so a serializer
    # that dropped `ticks_until_bleedout` entirely would go green on the very
    # bullet 61794cd asks to be proved ("omits or nulls it"). It does not get
    # to omit it: `shape()` here says the key is there.
    e = health(S["bleeder"])
    S["h0"] = e
    precondition("0.5", "`pawn {id, sections:[health]}` answers",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "pawn refused: %s (NOTE: the arg is `id`, not `pawn`)"
                 % show(dig(e, "error")))
    shape("0.5a", "pawn", e, "data.health", dict)
    shape("0.5b", "pawn", e, "data.health.bleed_rate")
    shape("0.5c", "pawn", e, "data.health.ticks_until_bleedout")
    shape("0.5d", "pawn", e, "data.health.bleedout", dict)
    for i, k in enumerate(BLEEDOUT_KEYS):
        shape("0.5e%d" % (i + 1), "pawn", e, "data.health.bleedout." + k)
    keys_exactly("0.5f", "health.bleedout publishes exactly PawnSerializer"
                         ".Bleedout's field set", e, "data.health.bleedout",
                 BLEEDOUT_KEYS)
    shape("0.5g", "pawn", e, "data.health.hediffs", list)
    shape("0.5h", "pawn", e, "data.health.hediffs_total")
    shape("0.5i", "pawn", e, "data.health.hediffs_more")
    contains("0.5j", "…and the block cites the game member it reads", e,
             "data.health.bleedout.cite", "TicksUntilDeathDueToBloodLoss")
    contains("0.5k", "…and the widget whose gate game_shows_clock reproduces", e,
             "data.health.bleedout.cite", "HealthCardUtility")

    # ---- 40ed42f's dig paths ------------------------------------------------
    e = send("digest")
    S["d0"] = e
    precondition("0.6", "`digest` answers", ARGS.dry_run or dig(e, "ok") is True,
                 "digest refused: %s" % show(dig(e, "error")))
    shape("0.6a", "digest", e, "data.work_coverage", dict)
    for i, k in enumerate(["ok", "under", "rows", "total", "more", "order", "note"]):
        kind = list if k in ("under", "rows") else None
        shape("0.6%s" % "bcdefghi"[i], "digest", e, "data.work_coverage." + k, kind)
    absent("0.6j", "no `error` key — the section computed rather than caught", e,
           "data.work_coverage.error")

    e = send("triage")
    S["t0"] = e
    precondition("0.7", "`triage` answers", ARGS.dry_run or dig(e, "ok") is True,
                 "triage refused: %s" % show(dig(e, "error")))
    for i, k in enumerate(["verb", "casualties", "total", "more", "counts",
                           "path_candidates", "action", "note"]):
        kind = list if k == "casualties" else None
        shape("0.7%s" % "abcdefgh"[i], "triage", e, "data." + k, kind)
    eq("0.7i", "triage names itself", e, "data.verb", "triage")
    eq("0.7j", "…and reports the pathfinding cap it used", e,
       "data.path_candidates", PATH_CANDIDATE_CAP)
    # AN OBSERVER, PROVED RATHER THAN INFERRED. `action.journal_seq` is present
    # and NULL with a provenance sentence, which is the shape PawnActs.NoStamp
    # exists to publish: "nothing was mutated" is a claim, not a silence.
    shape("0.7k", "triage", e, "data.action.journal_seq")
    eq("0.7l", "…and it is null: triage mutates nothing", e,
       "data.action.journal_seq", None)
    contains("0.7m", "…and says so in words", e, "data.action.provenance",
             "not applicable")

    # ---- the refusals, which are cheap and prove the guard rails -------------
    banner("PHASE 0b - THE REFUSALS: an ignored argument is a bug, not a default")

    e = send("triage", {"path_candidates": 0})
    bad_args("0.8a", "triage refuses a pathfinding cap below 1", e, "path_candidates")
    e = send("triage", {"path_candidates": 13})
    bad_args("0.8b", "…and above 12, naming the cost that makes it a cap", e,
             "FindPathNow")

    e = send("work-cover", {"work": "NotAWorkTypeAtAll"})
    bad_args("0.9a", "work-cover refuses an unknown work type", e,
             "not an essential work type")
    contains("0.9b", "…and LISTS the set it does cover", e, "error.detail", DOCTOR)
    # The essential set, taken from the refusal, cross-checked against the
    # digest's own row count. Two independent walks of EssentialTypes(); if a
    # `visible` filter or a sort ever drops one, the two disagree here.
    detail = dig(e, "error.detail") or ""
    m = re.search(r"plus Doctor:\s*([^.]+)\.", detail)
    named = [w.strip() for w in m.group(1).split(",")] if m else []
    S["essential"] = named
    total = dig(S["d0"], "data.work_coverage.total")
    check("0.9c", "the set work-cover names is the set the digest counts",
          ARGS.dry_run or (len(named) > 0 and len(named) == total),
          "%s names == digest total %s" % (len(named), total), named)
    check("0.9d", "…and Doctor is in it", ARGS.dry_run or DOCTOR in named,
          "Doctor among %s" % (named,), named)

    e = send("work-cover", {"priority": 0})
    bad_args("0.9e", "work-cover refuses priority 0 — it ENABLES a work type", e,
             "work-priorities")
    e = send("work-cover", {"priority": 9})
    bad_args("0.9f", "…and a priority outside 1..4", e, "priority")

    # 40ed42f's last claim in part 1: `work_coverage` is a PREDICATE SECTION.
    # A path that does not resolve is refused AT ARM TIME and names the keys the
    # section really publishes — which is also the proof the section is
    # registered, since an unregistered one refuses with a different sentence.
    # THROUGH THE ESCAPED WRAPPER, deliberately. These are arm-time parse
    # refusals and the escapes cannot change them — but `dev:spawn-pawn` has
    # already written journal rows by now, so an UNESCAPED advance here could
    # come back refused by `722c951`'s unread-delta guard instead of by the
    # parse, and the check would be asserting the wrong refusal. The wrapper
    # removes that ordering dependency; `raw_advance` has exactly ONE caller in
    # this file and it is phase 7b.
    e = advance({"until": {"condition": {"path": "work_coverage.okk",
                                         "op": "==", "value": False}}})
    bad_args("0.10a", "a near-miss path inside work_coverage is refused at ARM "
                      "time", e, "okk")
    contains("0.10b", "…and names the keys the section really publishes", e,
             "error.detail", "under")
    e = advance({"until": {"condition": {"path": "work_coverage.ok",
                                         "op": "<", "value": False}}})
    bad_args("0.10c", "`<` on a bool is refused rather than coerced", e, "bool")

    # THE CLOCK MUST NOT HAVE MOVED. Every refusal above happens at arm time,
    # AFTER TimeDriver.Start has already set the game's speed — a refusal that
    # forgot to put it back leaves the colony running unattended.
    e = send("digest")
    eq("0.11a", "every refusal left the game PAUSED", e, "data.time.paused", True)
    eq("0.11b", "…at speed Paused, not merely force-paused", e,
       "data.time.speed", "Paused")


# ------------------------------------------------------------------- phase 1 --

def phase1():
    banner("PHASE 1 - 61794cd: the clock, NULL BOTH WAYS on one pawn, and the "
           "widget's three-way")

    pid = S["bleeder"]

    # ---- (a) the NON-BLEEDER ------------------------------------------------
    # Both halves of the acceptance bullet on ONE pawn, which is stronger than
    # two: the key's EXISTENCE is proved in the state where its value is null,
    # so the later finite reading cannot be a key that simply appeared.
    e = health(pid)
    rate = dig(e, "data.health.bleed_rate")
    precondition("1.0", "the subject is not bleeding to start with",
                 ARGS.dry_run or (num(rate) and rate <= CARD_BLEED_FLOOR),
                 "colonist %s already has bleed_rate %s — pick a fresh bench or "
                 "`dev:heal {mode:\"full\"}` first (mode:\"injuries\" does NOT "
                 "clear BloodLoss)" % (pid, rate))
    shape("1.1a", "pawn", e, "data.health.ticks_until_bleedout")
    eq("1.1b", "…and it is NULL for a pawn who is not bleeding, never "
               "int.MaxValue", e, "data.health.ticks_until_bleedout", None)
    shape("1.1c", "pawn", e, "data.health.bleedout.ticks")
    eq("1.1d", "…and the block's own copy agrees", e,
       "data.health.bleedout.ticks", None)
    eq("1.1e", "no blood loss", e, "data.health.bleedout.blood_loss_severity", 0)
    eq("1.1f", "…no Deathless gene", e, "data.health.bleedout.deathless_gene", False)
    eq("1.1g", "…nothing preventing death", e,
       "data.health.bleedout.prevents_death", False)
    # THE RULING ON `outcome`, made rather than queued (worker A, this round;
    # commented on 61794cd). `outcome` is populated even when `ticks` is null,
    # and that is CORRECT AS DESIGNED: the field answers "what happens WHEN the
    # clock runs out", which is a property of the pawn's DEATH PATH — true of a
    # Deathless pawn who is not bleeding at all — and `ticks` is the only field
    # that says whether there IS a clock. Nulling `outcome` alongside `ticks`
    # would make `null` mean both "no deadline" and "could not compute", which
    # is exactly the ambiguity the block exists to remove.
    one_of("1.1h", "…and `outcome` is still answered for a pawn with no clock: "
                   "it is a property of the death path, not of the deadline", e,
           "data.health.bleedout.outcome", ["none", "coma", "death"])
    eq("1.1i", "…and for a plain colonist that is `death`", e,
       "data.health.bleedout.outcome", "death")
    # THE FIRST OF THE WIDGET'S THREE BRANCHES: below 0.01 bleed the game draws
    # NO bleeding line at all.
    eq("1.1j", "the game shows no clock, because it draws no line below 0.01 "
               "bleed", e, "data.health.bleedout.game_shows_clock", False)
    assert_card_gate("1.1k", e, "data.health")

    # ---- (b) A FINITE CLOCK THE GAME DOES NOT SHOW --------------------------
    # The branch a naive reading gets wrong. `TicksUntilDeathDueToBloodLoss`
    # returns a real number for any bleed above 0.0001, but the health tab
    # prints "WontBleedOutSoon" instead of a figure at >= 60000 ticks. So a
    # finite `ticks_until_bleedout` with `game_shows_clock:false` is CORRECT and
    # is the case that makes publishing both fields worth anything.
    e = send("dev:damage", {"pawn": pid, "mode": "amount", "hits": 2, "amount": 2})
    precondition("1.2", "a light wound landed and left the subject standing",
                 ARGS.dry_run or (dig(e, "ok") is True
                                  and dig(e, "data.downed") is not True),
                 "dev:damage: %s" % show(e))
    e = health(pid)
    rate = dig(e, "data.health.bleed_rate")
    ticks = dig(e, "data.health.ticks_until_bleedout")
    print("  %slight wound: bleed_rate=%s ticks=%s%s" % (DIM, rate, ticks, OFF))
    if ARGS.dry_run or (num(rate) and rate > CARD_BLEED_FLOOR
                        and num(ticks) and ticks >= CARD_WONT_BLEED_SOON):
        shape("1.3a", "pawn", e, "data.health.ticks_until_bleedout")
        ge("1.3b", "a light bleed has a FINITE clock", e,
           "data.health.ticks_until_bleedout", 1)
        eq("1.3c", "…which the game does NOT show, because a clock of a day or "
                   "more prints WontBleedOutSoon instead of a number", e,
           "data.health.bleedout.game_shows_clock", False)
    else:
        note("1.3", "the light wound bled %s/day for a %s-tick clock, which is "
                    "not the WontBleedOutSoon band (needs 0.01 < rate and ticks "
                    ">= 60000), so that branch was not staged live. Phase 9's "
                    "9.4e/9.4f assert both sides of the 60000 boundary offline."
             % (rate, ticks))
    assert_card_gate("1.3d", e, "data.health")

    # ---- (c) A CLOCK THE GAME DOES SHOW -------------------------------------
    last = bleed_up("1.4", pid, want_rate=2.0)
    e = health(pid)
    S["h_bleeding"] = e
    rate = dig(e, "data.health.bleed_rate")
    ticks = dig(e, "data.health.ticks_until_bleedout")
    sev = dig(e, "data.health.bleedout.blood_loss_severity")
    print("  %sheavy bleed: rate=%s ticks=%s blood_loss=%s downed=%s%s"
          % (DIM, rate, ticks, sev, dig(last, "data.downed"), OFF))
    precondition("1.5", "the subject now bleeds hard enough for the game to "
                        "print a figure",
                 ARGS.dry_run or (num(rate) and num(ticks)
                                  and ticks < CARD_WONT_BLEED_SOON),
                 "after eight rounds of dev:damage the rate is %s and the clock "
                 "is %s; the fixture could not reach the sub-60000 band"
                 % (rate, ticks))
    shape("1.6a", "pawn", e, "data.health.ticks_until_bleedout")
    ge("1.6b", "a bleeding pawn has a finite clock", e,
       "data.health.ticks_until_bleedout", 1)
    lt("1.6c", "…under one in-game day", e,
       "data.health.ticks_until_bleedout", CARD_WONT_BLEED_SOON)
    eq("1.6d", "…and the game DOES show it: this is the number on the health "
               "tab", e, "data.health.bleedout.game_shows_clock", True)
    assert_card_gate("1.6e", e, "data.health")
    # ONE BUILDER, TWO PUBLISHES. `health.ticks_until_bleedout` is literally
    # `bleedout["ticks"]`; if they ever disagree, something re-derived it.
    eq_val("1.6f", "health.ticks_until_bleedout IS health.bleedout.ticks — one "
                   "builder, so the two cannot drift",
           dig(e, "data.health.ticks_until_bleedout"),
           dig(e, "data.health.bleedout.ticks"))

    # ---- (d) THE GAME'S OWN ARITHMETIC, cross-checked ----------------------
    # NOT "our formula against itself": `ticks`, `bleed_rate` and
    # `blood_loss_severity` are three INDEPENDENTLY published readings, and
    # `Verse/HealthUtility.TicksUntilDeathDueToBloodLoss` relates them as
    # `(1 - severity) / bleedRateTotal * 60000`. If the mod ever computed the
    # clock itself instead of asking the game, this is where the three stop
    # agreeing. The tolerance is dictated by the ROUNDING the serializer does
    # (bleed_rate to 2dp, severity to 3dp), which is why the check is gated on a
    # rate high enough for 2dp to be worth about a percent.
    if ARGS.dry_run:
        note("1.7", "the arithmetic cross-check needs live numbers")
    elif num(rate) and rate >= 1.0 and num(ticks) and num(sev):
        want = (1.0 - sev) / rate * 60000.0
        err = abs(want - ticks) / max(1.0, want)
        check("1.7", "the published clock reproduces the game's own formula from "
                     "the OTHER two published readings: (1 - %s) / %s * 60000 = "
                     "%.0f vs %s (%.2f%%)" % (sev, rate, want, ticks, err * 100),
              err < 0.05, "within 5%% of %.0f" % want, ticks)
    else:
        note("1.7", "bleed_rate %s is too low for the 2dp rounding to leave the "
                    "arithmetic cross-check meaningful; skipped rather than "
                    "asserted with a tolerance wide enough to pass anything"
             % rate)

    note("1.8", "the `coma` outcome and the (Deathless) branch of the widget "
                "are NOT staged: they need a Biotech pawn carrying the Deathless "
                "gene, and no dev: verb adds a gene. `deathless_gene` is asserted "
                "false throughout, so the branch is unexercised rather than "
                "silently assumed. The pawn on which `outcome` and "
                "`game_shows_clock` deliberately DISAGREE — Deathless with a "
                "destroyed brain — is unreachable for the same reason.")


# ------------------------------------------------------------------- phase 2 --

def phase2():
    banner("PHASE 2 - 61794cd: the CAPTAIN SHAPE, and the alternative the Scope "
           "proposed, refuted by measurement")

    note("2.0", "THIS IS A SHAPE RECONSTRUCTION, NOT A REPLAY. 61794cd's fifth "
                "acceptance bullet asks for Captain's four reads from "
                "RUNS/m1-20260831/ replayed against the new surface. That is NOT "
                "DECIDABLE and the issue's own comment #2 says so: the transcript "
                "records the OUTPUT of a pawn state, not the state, and the run "
                "left no save to reload. What is reconstructed here is his SHAPE "
                "— 20+ bleeding injuries plus BloodLoss at the severity the "
                "post-mortem back-solved (%s) — and what is asserted is that "
                "BloodLoss survives the truncation his five reads lost it to."
         % CAPTAIN_SEVERITY)

    pid = S["bleeder"]
    # Phase 1 already put a heavy bleed on this pawn. Keep going until the
    # VISIBLE hediff count is past the cap — `hediffs_total` counts the rows the
    # serializer would publish, so ">20" here means the cap really has to cut.
    for _ in range(6):
        e = health(pid)
        total = dig(e, "data.health.hediffs_total") or 0
        if ARGS.dry_run or total > HEDIFF_CAP:
            break
        r = send("dev:damage", {"pawn": pid, "mode": "amount",
                                "hits": 8, "amount": 2})
        if dig(r, "ok") is not True:
            break
    e = health(pid)
    total = dig(e, "data.health.hediffs_total") or 0
    precondition("2.1", "the subject carries more than %d visible hediffs, so "
                        "the cap MUST cut" % HEDIFF_CAP,
                 ARGS.dry_run or total > HEDIFF_CAP,
                 "hediffs_total is %s after repeated dev:damage. Small `amount` "
                 "and repeated calls is the route (the loop stops at Downed); "
                 "if the pawn went down early, pick a tougher colonist."
                 % total)

    # BEFORE the BloodLoss row is added: the clock exists WITHOUT it. This is
    # the "independent of the hediff list" decision, demonstrated. BloodLoss
    # stage 0 sets `becomeVisible false`, so at low severity the row is
    # LEGITIMATELY absent from `hediffs` while the clock reads fine.
    rows = as_list(dig(e, "data.health.hediffs"))
    bl_before = [r for r in rows if isinstance(r, dict) and r.get("def") == "BloodLoss"]
    ge("2.2a", "the clock is finite with the hediff list already truncated", e,
       "data.health.ticks_until_bleedout", 1)
    ge("2.2b", "…and the truncation is real", e, "data.health.hediffs_more", 1)
    if not ARGS.dry_run and not bl_before:
        note("2.2c", "BloodLoss is absent from the rows at severity %s and the "
                     "clock still reads %s — Hediff.Visible returns "
                     "CurStage.becomeVisible, and BloodLoss stage 0 sets it "
                     "false, so below 0.15 the row is legitimately not there. "
                     "That is the decision 'the clock is independent of the "
                     "hediff list' demonstrated rather than argued."
             % (dig(e, "data.health.bleedout.blood_loss_severity"),
                dig(e, "data.health.ticks_until_bleedout")))

    # ---- the row that killed him, put back ---------------------------------
    e = send("dev:add-hediff", {"pawn": pid, "def": "BloodLoss",
                                "severity": CAPTAIN_SEVERITY})
    precondition("2.3", "BloodLoss at %s landed on the subject" % CAPTAIN_SEVERITY,
                 ARGS.dry_run or dig(e, "ok") is True,
                 "dev:add-hediff refused: %s" % show(dig(e, "error")))

    e = health(pid)
    S["h_captain"] = e
    rows = as_list(dig(e, "data.health.hediffs"))
    total = dig(e, "data.health.hediffs_total")
    more = dig(e, "data.health.hediffs_more")
    print("  %scaptain shape: total=%s rows=%s more=%s clock=%s%s"
          % (DIM, total, len(rows), more,
             dig(e, "data.health.ticks_until_bleedout"), OFF))

    ge("2.4a", "the pawn carries more than the cap", e,
       "data.health.hediffs_total", HEDIFF_CAP + 1)
    eq_val("2.4b", "…the list is capped at exactly PawnSerializer.HediffCap",
           len(rows) if not ARGS.dry_run else HEDIFF_CAP, HEDIFF_CAP)
    ge("2.4c", "…and `hediffs_more` reports honestly what was dropped", e,
       "data.health.hediffs_more", 1)
    if not ARGS.dry_run and num(total) and num(more):
        eq_val("2.4d", "…and the arithmetic closes: total = rows + more",
               total, len(rows) + more)

    # THE HEADLINE. Captain was read five times while dying and BloodLoss
    # appears in none of them: 17 bleeding Bites and 3 bleeding Scratches are
    # exactly 20 rank-4 rows against a cap of 20, and `Verse/Hediff.BleedRate`
    # is `=> 0f` for BloodLoss, so under "bleeding first" the CONSEQUENCE
    # ranked below every WOUND that produced it.
    bl = [r for r in rows if isinstance(r, dict) and r.get("def") == "BloodLoss"]
    check("2.5a", "BloodLoss is IN the read despite a real truncation — the row "
                  "that named the cause of death, which was cut from all five of "
                  "Captain's reads",
          ARGS.dry_run or len(bl) == 1, "exactly one BloodLoss row",
          [r.get("def") for r in rows][:6])
    check("2.5b", "…and it is ROW 0: Hediff.IsLethal is a band ABOVE bleeding, "
                  "so a hediff on its own lethal clock outranks every wound",
          ARGS.dry_run or (rows and isinstance(rows[0], dict)
                           and rows[0].get("def") == "BloodLoss"),
          "rows[0].def == BloodLoss",
          rows[0].get("def") if rows and isinstance(rows[0], dict) else None)
    check("2.5c", "…while the rows it beat are the bleeding wounds that produced "
                  "it (Hediff.BleedRate is 0f for BloodLoss, so it can never be "
                  "rank 4)",
          ARGS.dry_run or sum(1 for r in rows[1:]
                              if isinstance(r, dict)
                              and r.get("bleeding") is not None) >= 10,
          ">= 10 of the other 19 rows carry a non-null `bleeding`",
          sum(1 for r in rows[1:]
              if isinstance(r, dict) and r.get("bleeding") is not None))

    # THE SCOPE'S OWN ALTERNATIVE, REFUTED BY MEASUREMENT AND NOT BY ARGUMENT.
    # 61794cd's Scope offered "a severity/lethality ordering that puts
    # life-threatening hediffs first" as the generalising fix. RimWorld puts
    # `lifeThreatening` on BloodLoss's FIFTH stage (`minSeverity 0.60`), and
    # Captain died at ~0.478 — so on this row, at this severity, the flag the
    # alternative would have keyed on is FALSE, and the alternative drops the
    # row exactly as the old sort did.
    sev = dig(e, "data.health.bleedout.blood_loss_severity")
    row = bl[0] if bl else {}
    if ARGS.dry_run:
        note("2.6", "the life_threatening measurement needs a live severity")
    elif num(sev) and sev < 0.60:
        eq_val("2.6a", "at severity %s the BloodLoss row's `life_threatening` is "
                       "FALSE — RimWorld puts that flag on the fifth stage "
                       "(minSeverity 0.60), so 'life-threatening first' would "
                       "have dropped this row exactly as the old sort did" % sev,
               row.get("life_threatening"), False)
        check("2.6b", "…and it is in the read anyway, because the band is "
                      "IsLethal (lethalSeverity > 0, true for BloodLoss from "
                      "0.01) and not lifeThreatening",
              len(bl) == 1, "the row present with life_threatening false", row)
    else:
        note("2.6", "blood_loss_severity read back as %s, at or above the 0.60 "
                    "stage boundary, so `life_threatening` is legitimately true "
                    "and the refutation cannot be measured on this read. The "
                    "banked s21 envelopes 09/11 measure it on both sides of the "
                    "boundary; phase 9's 9.5d/9.5e assert them." % sev)

    ge("2.7a", "…and the clock is finite the whole time", e,
       "data.health.ticks_until_bleedout", 1)
    assert_card_gate("2.7b", e, "data.health")


# ------------------------------------------------------------------- phase 3 --

def phase3():
    banner("PHASE 3 - 40ed42f: digest.work_coverage, a FINE row against an "
           "UNDER row")

    # STAGE IT. The under-covered Doctor row is the whole subject, so it is
    # constructed rather than hoped for: Doctor OFF everywhere, then ON for
    # exactly one pawn. `have` is then 1 against a floor of 2.
    ids = S["ids"]
    e = set_doctor(ids, 0)
    precondition("3.0a", "Doctor can be turned off across the roster",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "work-priorities refused: %s" % show(dig(e, "error")))
    solo = None
    for pid in ids:
        e = set_doctor([pid], 3)
        if ARGS.dry_run or as_list(dig(e, "data.accepted")):
            solo = pid
            break
    precondition("3.0b", "exactly one colonist has Doctor enabled",
                 solo is not None or ARGS.dry_run,
                 "no colonist accepted Doctor priority 3 — every one of them has "
                 "the work type disabled by backstory or trait, which is a "
                 "roster this fixture cannot use")
    S["solo_doctor"] = solo
    print("  %ssole doctor: %s%s" % (DIM, solo, OFF))

    e = send("digest")
    S["d_under"] = e
    rows = as_list(dig(e, "data.work_coverage.rows"))
    under = as_list(dig(e, "data.work_coverage.under"))
    print("  %sunder=%s total=%s more=%s%s"
          % (DIM, under, dig(e, "data.work_coverage.total"),
             dig(e, "data.work_coverage.more"), OFF))

    eq("3.1a", "with one doctor the block says so", e, "data.work_coverage.ok",
       False)
    check("3.1b", "…and `under` names Doctor — names only, so a caller can branch "
                  "without walking rows",
          ARGS.dry_run or DOCTOR in under, "Doctor in %s" % (under,), under)
    check("3.1c", "…and `under` really is names, not rows",
          ARGS.dry_run or all(isinstance(x, str) for x in under),
          "every entry a string", under)

    # ---- the UNDER row: the whole diagnosis ---------------------------------
    dr = doctor_row(e)
    precondition("3.2", "the Doctor row is present in `rows`",
                 ARGS.dry_run or dr is not None,
                 "no Doctor row among %s"
                 % [r.get("work") for r in rows if isinstance(r, dict)])
    dr = dr or {}
    di = rows.index(dr) if dr in rows else 0
    base = "data.work_coverage.rows.%d" % di
    keys = sorted(set(UNDER_ROW_KEYS))
    got = sorted(set(dr))
    check("3.3a", "an UNDER row carries the whole diagnosis",
          ARGS.dry_run or got == keys, "exactly %s" % keys, got)
    eq("3.3b", "Doctor's floor is 2", e, base + ".floor", DOCTOR_FLOOR)
    # THE ONE DEVIATION FROM THE GAME'S OWN LIST, and it is a floor on a
    # different quantity. `Doctor.requireCapableColonist` is FALSE — the game
    # does not require a doctor to start a colony at all — but
    # `RimWorld/Alert_NeedDoctor.Patients` tests `!item.Downed` in its own
    # predicate, so one doctor's coverage is zero the moment that doctor is the
    # patient. That is the M1 death exactly.
    eq("3.3c", "…and it is a floor on AVAILABILITY, not capability, because "
               "Alert_NeedDoctor.Patients has `!item.Downed` in it", e,
       base + ".floor_on", "available")
    eq("3.3d", "…and the block says the floor is OURS and names the lesson", e,
       base + ".floor_by", "autorimmer:one-doctor-is-zero-doctors")
    eq("3.3e", "…one available doctor", e, base + ".available", 1)
    eq("3.3f", "…one short", e, base + ".short_by", 1)
    eq("3.3g", "…and `have` is the AVAILABLE count, not the capable one", e,
       base + ".have", 1)
    shape("3.3h", "digest", e, base + ".capable")
    shape("3.3i", "digest", e, base + ".enabled")
    shape("3.3j", "digest", e, base + ".available_pawns", list)
    shape("3.3k", "digest", e, base + ".candidates", list)
    if not ARGS.dry_run and as_list(dr.get("candidates")):
        c0 = base + ".candidates.0"
        shape("3.3l", "digest", e, c0 + ".pawn")
        shape("3.3m", "digest", e, c0 + ".id")
        shape("3.3n", "digest", e, c0 + ".skill")
        skills = [c.get("skill") for c in dr["candidates"]]
        check("3.3o", "…and the candidates are RANKED by the game's own order "
                      "(Pawn_WorkSettings.EnableAndInitialize sorts by "
                      "AverageOfRelevantSkillsFor descending)",
              skills == sorted(skills, reverse=True), "skill descending", skills)

    # THE EMPTY CASE, WHICH IS THE ONE THAT PROVES THE KEY IS UNCONDITIONAL.
    # 3.0b left exactly one colonist with Doctor enabled and 3.3e says that one
    # is AVAILABLE, so `Impaired` is arithmetically empty here (enabled 1 =
    # available 1 + impaired + non-responders). Before the session-21 fix the
    # key was simply absent in this state, and `shape()` at 4.5a only ever saw
    # the populated case — which is how the same omission survived into
    # `work-cover` and failed acceptance 7.2h there instead.
    shape("3.3p", "digest", e, base + ".enabled_but_incapable", list)
    eq_val("3.3q", "…and it is EMPTY rather than absent when nobody enabled is "
                   "missing a capacity — an absent key would mean both 'nobody "
                   "is impaired' and 'this build does not publish it'",
           dr.get("enabled_but_incapable"), [])

    # ---- a FINE row: three fields and no more -------------------------------
    fine = [(i, r) for i, r in enumerate(rows)
            if isinstance(r, dict) and r.get("work") not in under]
    precondition("3.4", "at least one covered work type to compare against",
                 ARGS.dry_run or len(fine) > 0,
                 "every essential work type is under its floor; there is no FINE "
                 "row to check the three-field claim against")
    if not ARGS.dry_run:
        fi, _fr = fine[0]
        keys_exactly("3.4a", "a FINE row costs exactly three fields — the cap "
                             "only ever eats these", e,
                     "data.work_coverage.rows.%d" % fi, FINE_ROW_KEYS)
        eq_val("3.4b", "…and every non-Doctor floor is 1, from the game's own "
                       "requireCapableColonist list",
               sorted({r.get("floor") for r in rows
                       if isinstance(r, dict) and r.get("work") != DOCTOR}), [1])

    # ---- THE INVARIANT THAT ACTUALLY MATTERS --------------------------------
    # "The cap only ever drops rows that are fine." The cap cannot be exercised
    # on this bench (see 3.6), so what is asserted is the property that survives
    # it: every name in `under` is present in `rows`, and `total` accounts for
    # everything.
    names = [r.get("work") for r in rows if isinstance(r, dict)]
    missing = [u for u in under if u not in names]
    check("3.5a", "EVERY under-covered row is present in `rows` — an "
                  "under-covered row is never truncated",
          ARGS.dry_run or not missing, "no name in `under` missing from `rows`",
          missing)
    if not ARGS.dry_run:
        eq_val("3.5b", "…and the counts close: rows + more == total",
               len(rows) + (dig(e, "data.work_coverage.more") or 0),
               dig(e, "data.work_coverage.total"))

    total = dig(e, "data.work_coverage.total")
    if not ARGS.dry_run and num(total):
        note("3.6", "WorkCoverage.RowCap is %d and this bench publishes %d "
                    "essential work types, so THE CAP CANNOT FIRE HERE and 3.5a "
                    "is proved by construction rather than by a live truncation. "
                    "Phase 9 re-derives both numbers from the source, so the day "
                    "a mod set pushes the count past the cap this note fails "
                    "instead of quietly becoming false."
             % (WORK_ROW_CAP, total))
        eq("3.6a", "nothing was dropped, as the arithmetic requires", e,
           "data.work_coverage.more", 0)

    # ---- FINDING: `order` describes an order the rows are not in ------------
    if not ARGS.dry_run and under and rows:
        first_under = next((i for i, r in enumerate(rows)
                            if isinstance(r, dict) and r.get("work") in under), None)
        if first_under not in (None, 0):
            finding("3.7", "digest.work_coverage.order says %r, but the first "
                           "UNDER row is at index %d: WorkCoverage.Section emits "
                           "rows in ONE pass over the natural-priority-sorted "
                           "list and appends under-rows inline, so the real order "
                           "is natural-priority-desc with under rows wherever "
                           "they fall. A caller trusting the string would read "
                           "rows[0] as the worst problem; here rows[0] is %r and "
                           "is fine. Filed on 40ed42f."
                    % (dig(e, "data.work_coverage.order"), first_under,
                       rows[0].get("work")))


# ------------------------------------------------------------------- phase 4 --

def phase4():
    banner("PHASE 4 - 40ed42f: ENABLED BUT INCAPABLE, with a real pawn")

    # THE FIXTURE IS THE GAME'S OWN. `dev:damage {mode:"manipulation"}` is
    # `Verse/HealthUtility.DamageLimbsUntilIncapableOfManipulation`, and every
    # vanilla Doctor work-giver except `VisitSickPawn` requires Manipulation —
    # `DoctorTendEmergency`, `DoctorTendToHumanlikes`, `DoctorRescue`,
    # `DoBillsMedicalHumanOperation` and eight more. A doctor with no hands has
    # the work type ON, undisabled, and cannot tend anybody.
    #
    # `allow_bleeding:false` deliberately: the subject has to survive to be a
    # capable, enabled, non-downed pawn who is nevertheless useless. A bleeding
    # one goes down and lands in a different bucket.
    victim = S["handless"]

    e = send("dev:damage", {"pawn": victim, "mode": "manipulation",
                            "allow_bleeding": False})
    precondition("4.1", "the subject is now incapable of manipulation",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "dev:damage mode:manipulation refused: %s" % show(dig(e, "error")))
    e = health(victim)
    caps = as_list(dig(e, "data.health.capacities"))
    man = next((c for c in caps if isinstance(c, dict)
                and c.get("def") == "Manipulation"), None)
    precondition("4.2", "the pawn's Manipulation really is gone",
                 ARGS.dry_run or (man is not None and (man.get("pct") or 0) == 0),
                 "Manipulation reads %s; DamageLimbsUntilIncapableOfManipulation "
                 "did not take it to zero on this pawn" % show(man))

    # …and now give them Doctor. ENABLED, UNDISABLED, USELESS.
    e = set_doctor([victim], 3)
    acc = as_list(dig(e, "data.accepted"))
    precondition("4.3", "Doctor is enabled on the handless pawn",
                 ARGS.dry_run or len(acc) == 1,
                 "work-priorities would not enable Doctor on %s: %s — a pawn "
                 "whose backstory disables Doctor cannot demonstrate "
                 "'enabled but incapable'" % (victim, show(e)))

    e = send("digest")
    S["d_impaired"] = e
    dr = doctor_row(e) or {}
    rows = as_list(dig(e, "data.work_coverage.rows"))
    di = rows.index(dr) if dr in rows else 0
    base = "data.work_coverage.rows.%d" % di
    print("  %sdoctor row: %s%s" % (DIM, show(dr), OFF))

    # The row must still be UNDER — one available doctor plus one impaired is
    # still one available doctor, which is the entire point.
    eq("4.4a", "the colony still has one AVAILABLE doctor", e,
       base + ".available", 1)
    ge("4.4b", "…but two with the work type ENABLED", e, base + ".enabled", 2)
    check("4.4c", "…so `enabled` and `available` have separated, which is the "
                  "trap one level below 'is Doctor covered'",
          ARGS.dry_run or ((dig(e, base + ".enabled") or 0)
                           > (dig(e, base + ".available") or 0)),
          "enabled > available",
          (dig(e, base + ".enabled"), dig(e, base + ".available")))
    ge("4.4d", "…and the handless pawn is still CAPABLE — losing hands is not "
               "WorkTypeIsDisabled", e, base + ".capable", 2)

    shape("4.5a", "digest", e, base + ".enabled_but_incapable", list)
    imp = as_list(dr.get("enabled_but_incapable"))
    check("4.5b", "…and it names a pawn", ARGS.dry_run or len(imp) >= 1,
          "at least one entry", imp)
    if not ARGS.dry_run and imp:
        shape("4.5c", "digest", e, base + ".enabled_but_incapable.0.pawn")
        eq("4.5d", "…and the CAPACITY the game's own work-givers require "
                   "(WorkGiver.MissingRequiredCapacity over the union of the "
                   "type's work-givers)", e,
           base + ".enabled_but_incapable.0.missing_capacity", "Manipulation")
        names = [x.get("pawn") for x in imp if isinstance(x, dict)]
        avail = as_list(dr.get("available_pawns"))
        check("4.5e", "…and that pawn is NOT counted as available",
              all(n not in avail for n in names),
              "no impaired name in available_pawns", (names, avail))

    # `work-cover` sees the same thing, from the same builder, and says the fix
    # is surgery rather than a work priority.
    e = send("work-cover", {"work": DOCTOR, "dry_run": True})
    su = as_list(dig(e, "data.still_under"))
    if not ARGS.dry_run and su:
        shape("4.6a", "work-cover", e, "data.still_under.0.enabled_but_incapable",
              list)
        eq("4.6b", "…naming the same capacity, from the same builder", e,
           "data.still_under.0.enabled_but_incapable.0.missing_capacity",
           "Manipulation")
    else:
        note("4.6", "work-cover planned a full repair in the dry run, so no "
                    "`still_under` row carried the impaired list on this call — "
                    "the digest row at 4.5 is the witness the acceptance bullet "
                    "asks for.")

    finding("4.7", "`enabled_but_incapable` is unreachable on a COVERED row: "
                   "WorkCoverage.Section builds the whole diagnosis inside the "
                   "`if (r.Under)` branch, so a colony with two available doctors "
                   "and a handless third is told nothing about the third. The "
                   "block is a diagnosis and there is arguably no problem to "
                   "diagnose — but 'you have a doctor who cannot tend' is the "
                   "kind of thing the M1 post-mortem exists about. Filed on "
                   "40ed42f, not asserted either way. NARROWER THAN IT WAS: the "
                   "INNER guard `if (r.Impaired.Count > 0)` was a separate "
                   "defect and is fixed — the list is now always present and "
                   "empty when nobody is impaired (3.3q, 7.2h, 9.8d, 9.11a). "
                   "What is left is whether a FINE row should stop costing three "
                   "fields, which is the digest's byte budget and a design "
                   "decision rather than a shape bug.")


# ------------------------------------------------------------------- phase 5 --

def phase5():
    banner("PHASE 5 - 40ed42f: work-cover, the dry run that writes nothing and "
           "the promotion that reaches the ledger")

    seq_before = 0 if ARGS.dry_run else watermark()

    # ---- (a) THE DRY RUN, which writes nothing ------------------------------
    e = send("work-cover", {"work": DOCTOR, "dry_run": True})
    S["cover_dry"] = e
    eq("5.1a", "the envelope stays ok:true — a refusal is information, not "
               "breakage", e, "ok", True)
    eq("5.1b", "…and the verb reports it planned in dry-run", e,
       "data.dry_run", True)
    shape("5.1c", "work-cover", e, "data.ok")
    shape("5.1d", "work-cover", e, "data.repaired", list)
    shape("5.1e", "work-cover", e, "data.still_under", list)
    shape("5.1f", "work-cover", e, "data.already_covered", list)
    shape("5.1g", "work-cover", e, "data.coverage_after", dict)
    # `coverage_after`'s VALUE IS NOW ASSERTED FOR A DRY RUN — git-bug 58794e4,
    # fixed. It used to report the coverage BEFORE the repair (`ok:false`,
    # `under:["Doctor"]`) while `repaired` in the SAME envelope named the
    # promotion that fixes it, so the envelope contradicted itself and the field
    # answered "is it fixed", which in a dry run is always no by construction.
    # The dry-run arm now PROJECTS the planned promotions onto a fresh snapshot,
    # so it answers the question a dry run is asked. The suite could not assert
    # either value while the defect stood; now the value is the check.
    eq("5.1g2", "…and in a DRY RUN `coverage_after` is the PROJECTED coverage: "
                "the question a dry run asks is 'would this fix it', not 'is it "
                "fixed'", e, "data.coverage_after.ok", True)
    if not ARGS.dry_run:
        proj_under = as_list(dig(e, "data.coverage_after.under"))
        check("5.1g3", "…so Doctor is not in the projected `under`, even though "
                       "nothing has been written",
              DOCTOR not in proj_under,
              "Doctor absent from %s" % (proj_under,), proj_under)
    shape("5.1h", "work-cover", e, "data.priority_set")
    shape("5.1i", "work-cover", e, "data.use_priorities")
    rep = as_list(dig(e, "data.repaired"))
    check("5.1j", "…and it NAMES the pawn it would promote",
          ARGS.dry_run or len(rep) >= 1, "at least one planned promotion", rep)
    if not ARGS.dry_run and rep:
        for k in ("work", "pawn", "name", "before", "after", "skill", "chosen_by"):
            shape("5.1k-%s" % k, "work-cover", e, "data.repaired.0." + k)
        contains("5.1l", "…and cites the game's own ordering", e,
                 "data.repaired.0.chosen_by", "AverageOfRelevantSkillsFor")
        eq("5.1m", "…promoting TO the priority it was asked for", e,
           "data.repaired.0.after", 3)

    # THE DRY RUN MUTATED NOTHING, PROVED FROM OUTSIDE THE VERB.
    e2 = send("digest")
    dr = doctor_row(e2) or {}
    eq_val("5.2a", "the colony still has one available doctor after the dry run",
           dr.get("available") if not ARGS.dry_run else 1, 1)
    e3 = journal_since(seq_before, types=["action"])
    acts = [a for a in as_list(dig(e3, "data.events"))
            if isinstance(a, dict) and dig(a, "payload.verb") == "work-cover"]
    check("5.2b", "…and wrote NO `action` row: a plan is not an act",
          ARGS.dry_run or len(acts) == 0,
          "no work-cover action row since seq %s" % seq_before, acts)
    if not ARGS.dry_run and dig(e, "data.action.journal_seq") == 0:
        finding("5.2c", "a clean dry run's `action.journal_seq` is 0 and carries "
                        "Stamp(0)'s provenance sentence, 'NOT WRITTEN — the "
                        "journal writer is closed, so this mutation has no "
                        "journal line'. The writer is not closed and there was no "
                        "mutation: the emit guard is "
                        "`(repaired>0 && !dryRun) || stillUnder>0`, so a dry run "
                        "that plans a clean repair legitimately emits nothing and "
                        "then reports it in the words of a failure. NoStamp() is "
                        "the shape this case wants. Filed on 40ed42f.")

    # ---- (b) THE REAL PROMOTION, and it reaches the JOURNAL ------------------
    e = send("work-cover", {"work": DOCTOR})
    S["cover_real"] = e
    eq("5.3a", "the envelope is ok:true", e, "ok", True)
    eq("5.3b", "…the verb's own verdict is that the floor is now met", e,
       "data.ok", True)
    eq("5.3c", "…and it is not a dry run", e, "data.dry_run", False)
    rep = as_list(dig(e, "data.repaired"))
    check("5.3d", "…exactly one promotion, because the floor was one short",
          ARGS.dry_run or len(rep) == 1, "one repaired row", rep)
    ge("5.3e", "…and it reached the journal as an ACT", e,
       "data.action.journal_seq", 1)
    eq("5.3f", "…and `coverage_after` agrees the floor is met, re-read rather "
               "than assumed", e, "data.coverage_after.ok", True)
    if not ARGS.dry_run:
        after_under = as_list(dig(e, "data.coverage_after.under"))
        check("5.3g", "…with Doctor no longer in `under`", DOCTOR not in after_under,
              "Doctor absent from %s" % (after_under,), after_under)
    # …and a SEPARATE digest agrees, which is the difference between a verb
    # reporting what it intended and the state afterwards.
    e2 = send("digest")
    if not ARGS.dry_run:
        check("5.3h", "…and an INDEPENDENT digest read agrees",
              DOCTOR not in as_list(dig(e2, "data.work_coverage.under")),
              "Doctor absent from the digest's own `under`",
              dig(e2, "data.work_coverage.under"))

    e3 = journal_since(seq_before, types=["action"])
    acts = [a for a in as_list(dig(e3, "data.events"))
            if isinstance(a, dict) and dig(a, "payload.verb") == "work-cover"]
    check("5.4a", "the journal carries the work-cover act — 40ed42f's "
                  "'journalled as an act', proved from the LEDGER and not from "
                  "the verb's own stamp",
          ARGS.dry_run or len(acts) == 1, "exactly one work-cover action row",
          [dig(a, "payload") for a in acts])
    if not ARGS.dry_run and acts:
        eq_val("5.4b", "…and it records the decision, not a silent exemption",
               dig(acts[0], "payload.step"), "cover")
        check("5.4c", "…and the row carries what was repaired",
              isinstance(dig(acts[0], "payload.repaired"), list),
              "a repaired[] in the journal payload", dig(acts[0], "payload"))
        eq_val("5.4d", "…and the verb's journal_seq joins to it",
               dig(S["cover_real"], "data.action.journal_seq"),
               dig(acts[0], "seq"))

    # An already-covered work type is ANSWERED, not refused.
    e = send("work-cover", {"work": DOCTOR})
    ok2 = as_list(dig(e, "data.already_covered"))
    check("5.5a", "a work type that is already covered is ANSWERED — "
                  "'Doctor is already covered' is the answer to the question "
                  "asked, and an empty repair list would read as a failure",
          ARGS.dry_run or (len(ok2) == 1 and ok2[0].get("work") == DOCTOR),
          "one already_covered row for Doctor", ok2)
    eq("5.5b", "…and the verdict is true", e, "data.ok", True)
    S["seq_cover"] = seq_before


# ------------------------------------------------------------------- phase 6 --

def triage_read(pause_num=None, what=None):
    """Pause, then read. Never the other way round — see pause()'s header."""
    pause(pause_num, what)
    return send("triage")


def phase6():
    banner("PHASE 6 - 40ed42f: triage, the casualty UNION, and `act` EXECUTED")

    # THE UNION, STAGED SO EACH PAWN SATISFIES EXACTLY ONE CLAUSE. A casualty is
    # downed OR needs-tend OR bleeding, deliberately — `RimWorld
    # /Alert_ColonistNeedsTend` INVERTS, because its getter excludes pawns
    # needing rescue, so it goes OFF the moment the patient goes DOWN. That is
    # the post-mortem's sharpest signal finding, and a verb that keyed on the
    # alert would lose the case that matters. Three single-clause pawns is the
    # strongest form of the proof:
    #
    #   patient  — ANAESTHETISED. Downed, no injuries, nothing to tend, no
    #              bleed clock. `Anesthetic` zeroes Consciousness and is
    #              `isBad:false`, so it downs without wounding.
    #   sick     — INFLUENZA. Standing, not bleeding, tendable.
    #   bleeder  — phase 2's Captain shape. Standing, bleeding, on a clock.
    patient, sick, bleeder = S["patient"], S["sick"], S["bleeder"]
    if not ARGS.dry_run:
        e = send("dev:add-hediff", {"pawn": patient, "def": "Anesthetic",
                                    "severity": 1})
        precondition("6.0a", "the patient is anaesthetised — downed with nothing "
                             "to tend and no bleed clock, so `downed` is the ONLY "
                             "clause of the union it satisfies",
                     dig(e, "ok") is True and dig(e, "data.downed") is True,
                     "dev:add-hediff Anesthetic left downed=%s: %s"
                     % (dig(e, "data.downed"), show(dig(e, "error"))))
        e = send("dev:add-hediff", {"pawn": sick, "def": "Flu", "severity": 0.4})
        precondition("6.0b", "the sick pawn has influenza — tendable, standing, "
                             "not bleeding",
                     dig(e, "ok") is True and dig(e, "data.downed") is not True,
                     "dev:add-hediff Flu: %s" % show(e))
    print("  %sunion: patient(anaesthetised)=%s sick(flu)=%s bleeder=%s "
          "rescuer=%s handless=%s%s"
          % (DIM, patient, sick, bleeder, S["rescuer"], S["handless"], OFF))

    e = triage_read("6.0c")
    S["triage"] = e
    cas = as_list(dig(e, "data.casualties"))
    precondition("6.0d", "triage sees at least one casualty",
                 ARGS.dry_run or len(cas) > 0,
                 "triage found no casualty on a map where this suite has downed, "
                 "sickened and bled colonists: %s" % show(e))
    print("  %s%d casualt(ies), counts=%s%s"
          % (DIM, len(cas), show(dig(e, "data.counts")), OFF))

    # ---- the row shape ------------------------------------------------------
    # `act` is deliberately NOT in this list: it is published only when a
    # rescuer survived the gate, and on a bedless map that is nobody. Its
    # presence is asserted where it is EARNED, at 6.9.
    base = "data.casualties.0"
    for i, k in enumerate(["pawn", "name", "at", "downed", "needs_tend", "clock",
                           "rescuers", "rescuers_gated_out", "candidates_total",
                           "candidates_pathed", "candidates_not_pathed",
                           "verdict", "margin_ticks"]):
        kind = list if k in ("rescuers", "rescuers_gated_out", "at") else None
        shape("6.1%s" % "abcdefghijklm"[i], "triage", e, base + "." + k, kind)
    one_of("6.1n", "…and the verdict is one of the five named", e,
           base + ".verdict",
           ["in-time", "too-slow", "no-rescuer", "no-path", "no-deadline"])

    def by_id(pid):
        return next((r for r in cas if isinstance(r, dict)
                     and r.get("pawn") == pid), None)

    if not ARGS.dry_run:
        d_row, b_row, s_row = by_id(patient), by_id(bleeder), by_id(sick)
        check("6.2a", "a DOWNED colonist is a casualty — and this one is downed "
                      "and NOTHING else: no wound, no bleed, nothing to tend",
              d_row is not None and d_row.get("downed") is True
              and d_row.get("clock") is None,
              "a row for %s with downed:true and clock:null" % patient, d_row)
        check("6.2b", "a STANDING colonist who is bleeding out is a casualty — "
                      "the case Alert_ColonistNeedsTend goes OFF for",
              b_row is not None and b_row.get("downed") is not True
              and b_row.get("clock") is not None,
              "a row for %s with downed:false and a clock" % bleeder, b_row)
        check("6.2c", "a colonist who merely NEEDS TENDING is a casualty — "
                      "standing, not bleeding, and still reported",
              s_row is not None and s_row.get("needs_tend") is True
              and s_row.get("downed") is not True
              and s_row.get("clock") is None,
              "a row for %s with needs_tend:true, downed:false, clock:null" % sick,
              s_row)
        eq_val("6.2d", "…and the three are three distinct rows, one per clause "
                       "of the union",
               len({id(x) for x in (d_row, b_row, s_row) if x is not None}), 3)

        # ---- the clock block is THE SAME BLOCK `pawn` publishes -------------
        # PawnSerializer.BleedoutBlock is internal and triage calls it, so the
        # clock an agent reads on a pawn and the clock a rescue is judged
        # against are ONE BUILDER. Proved by reading both and comparing, not by
        # reading the comment that says so. The reads are taken while paused, so
        # they cannot differ merely because time passed between them.
        if b_row is not None:
            hp = health(bleeder)
            eq_val("6.3a", "triage's clock IS pawn.health.bleedout — one builder, "
                           "two callers, and they cannot drift",
                   dig(b_row, "clock.ticks"),
                   dig(hp, "data.health.bleedout.ticks"))
            eq_val("6.3b", "…including the outcome",
                   dig(b_row, "clock.outcome"),
                   dig(hp, "data.health.bleedout.outcome"))
            eq_val("6.3c", "…and the widget gate",
                   dig(b_row, "clock.game_shows_clock"),
                   dig(hp, "data.health.bleedout.game_shows_clock"))
            keys_exactly("6.3d", "…and it is the whole block, not a subset",
                         {"c": b_row.get("clock")}, "c", BLEEDOUT_KEYS)

    # ---- (b) THE NO-BED REFUSAL, ASSERTED RATHER THAN TRIPPED OVER ----------
    # Measured on the s21 bench and filed on 40ed42f #3: on a bare `--quicktest`
    # map there is no bed, `TakeToBedGate` ends in `RestUtility.FindBedFor`,
    # every candidate is refused with the GAME's own sentence, every verdict is
    # `no-rescuer` and `act` is absent. That is not a bug and it is not a
    # fixture accident — it is the answer to "why is nobody coming", which is
    # the question the M1 run never got to ask. So it is asserted FIRST.
    #
    # ============ WHICH ROW CAN REACH `no-bed`, AND WHICH CANNOT =============
    # THIS BLOCK USED TO ASSERT `no-bed` ON `cas[0]` AND WAS WRONG TO. Measured
    # on the s21 bench, 2026-09-01: `cas[0]` is the BLEEDER, who is STANDING,
    # and `TakeToBedGate("rescue", …)` opens on
    # `HealthAIUtility.CanRescueNow` -> `WantsToBeRescued`, whose FIRST clause is
    # `!pawn.Downed`. `no-bed` (`RestUtility.FindBedFor`) is its LAST. So for a
    # standing patient every candidate that clears `ProviderGate` is refused
    # `cannot-rescue` and the bed clause is unreachable BY CONSTRUCTION — no
    # amount of staging produces `no-bed` there, and the mod is right about it
    # (TriageVerbs.cs's "THE OTHER BOUNDARY, STATED" says so in the source).
    #
    # `no-bed` belongs to a DOWNED, not-yet-in-bed patient, which is exactly
    # what the anaesthetised one staged at 6.0a is. The banked bench envelope
    # `18-triage-downed.json` is that row and phase 9's 9.6i-9.6k assert it.
    #
    # The other refusal on `cas[0]` — `manipulation` on the handless pawn — is
    # NOT fixture leakage either: 6.8d requires phase 4's handless doctor to be
    # in the gated list on purpose. The wreckage is carried between phases
    # deliberately; only this assertion picked the wrong row to read it off.
    if not ARGS.dry_run and S.get("beds0", 0) == 0 and cas:
        row = cas[0]
        gated = as_list(row.get("rescuers_gated_out"))
        verdicts = [r.get("verdict") for r in cas if isinstance(r, dict)]
        check("6.4a", "with NO bed on the map EVERY casualty reads `no-rescuer` "
                      "— with no bed to carry anyone to, nobody clears the gate "
                      "on anybody",
              verdicts == ["no-rescuer"] * len(verdicts),
              "every verdict no-rescuer", verdicts)
        eq_val("6.4b", "…with nothing pathed, because nothing survived the gate",
               (row.get("candidates_total"), row.get("candidates_pathed")), (0, 0))
        # THE STANDING CASE, NAMED. This is the half that used to be asserted as
        # `no-bed` and is not: it is the gate order, measured. The row is chosen
        # by `downed:false` rather than by index, because casualty order follows
        # `FreeColonistsSpawned` and is not this suite's to predict.
        std = [r for r in cas if isinstance(r, dict) and r.get("downed") is not True]
        if std:
            sgates = {g.get("gate") for g in as_list(std[0].get("rescuers_gated_out"))
                      if isinstance(g, dict)}
            check("6.4b2", "a STANDING casualty's candidates stop at "
                           "`cannot-rescue`, not at `no-bed`: WantsToBeRescued's "
                           "first clause is `!pawn.Downed` and the bed lookup is "
                           "TakeToBedGate's last",
                  "cannot-rescue" in sgates and "no-bed" not in sgates,
                  "cannot-rescue present and no-bed absent on standing pawn %s"
                  % std[0].get("pawn"), sorted(sgates))
        else:
            note("6.4b2", "every casualty on this read is DOWNED, so the "
                          "`cannot-rescue` half of the gate order could not be "
                          "shown against a standing patient.")
        # …and the DOWNED one, which is where `no-bed` lives.
        dwn = [r for r in cas if isinstance(r, dict) and r.get("downed") is True]
        if dwn:
            drow = dwn[0]
            dgated = as_list(drow.get("rescuers_gated_out"))
            check("6.4c", "…while a DOWNED casualty's refusal names `no-bed`, the "
                          "last clause of TakeToBedGate",
                  any(g.get("gate") == "no-bed" for g in dgated
                      if isinstance(g, dict)),
                  "a gated-out row with gate no-bed on pawn %s" % drow.get("pawn"),
                  dgated[:3])
            check("6.4d", "…in the GAME's own words, not ours "
                          "(NoNonPrisonerBed.Translate)",
                  any("bed" in (g.get("reason") or "").lower() for g in dgated
                      if isinstance(g, dict)),
                  "a reason naming a bed", [g.get("reason") for g in dgated[:3]])
        else:
            note("6.4c", "no casualty is DOWNED on this read, so the `no-bed` "
                         "clause could not be reached live — it is the last "
                         "clause of TakeToBedGate and CanRescueNow refuses a "
                         "standing patient before it. 9.6i-9.6k assert it from "
                         "the banked bench envelope instead.")
        check("6.4e", "…and NO `act` is published, because there is nobody to "
                      "name in it",
              all("act" not in r for r in cas if isinstance(r, dict)),
              "no act on any casualty row",
              [r.get("act") for r in cas if isinstance(r, dict)])
        eq_val("6.4f", "…and `margin_ticks` is null, because there is nothing to "
                       "measure the clock against", row.get("margin_ticks"), None)
    else:
        note("6.4", "the map already had %s bed(s) when this suite started, so "
                    "the no-bed refusal could not be staged live. It is banked at "
                    "accept/runs/s21-20260901/18-triage-downed.json and phase 9's "
                    "9.6i-9.6k assert it there." % S.get("beds0"))

    # ---- (c) a bed, ON the bleeder, and the estimate becomes real -----------
    # The bed goes where the pawn WITH A CLOCK is, because `in-time` is only an
    # honest verdict when the carry leg is real. `pos:"pawn:<id>"`,
    # `mode:"direct"` — exact-or-refuse, never GenPlace's sliding search.
    spawn_bed("6.5a", bleeder)

    e = triage_read("6.5b")
    S["triage_bed"] = e
    cas = as_list(dig(e, "data.casualties"))
    with_r = [r for r in cas if isinstance(r, dict) and as_list(r.get("rescuers"))]
    precondition("6.5c", "at least one casualty now has a pathed rescuer",
                 ARGS.dry_run or len(with_r) > 0,
                 "even with a bed on the map every candidate was gated out: %s"
                 % show(dig(cas[0], "rescuers_gated_out") if cas else None))

    if not ARGS.dry_run:
        row = with_r[0]
        ri = cas.index(row)
        rb = "data.casualties.%d.rescuers.0" % ri
        for i, k in enumerate(["pawn", "name", "at", "cells", "travel_ticks",
                               "carry_ticks", "total_ticks", "bed", "doing",
                               "drafted"]):
            shape("6.6%s" % "abcdefghij"[i], "triage", e, rb + "." + k)
        r0 = row["rescuers"][0]
        if num(r0.get("travel_ticks")) and num(r0.get("carry_ticks")):
            eq_val("6.6k", "total_ticks is the WHOLE journey: walk to the patient "
                           "plus carry to the bed TakeToBedGate actually chose — "
                           "not just the half that is easy to measure",
                   r0.get("total_ticks"),
                   r0["travel_ticks"] + r0["carry_ticks"])
        check("6.6l", "…and only the nearest few were pathed, because a "
                      "FindPathNow per candidate is the expensive part",
              (row.get("candidates_pathed") or 0) <= PATH_CANDIDATE_CAP,
              "<= %d pathed" % PATH_CANDIDATE_CAP, row.get("candidates_pathed"))
        eq_val("6.6m", "…and the arithmetic on what was NOT pathed closes",
               row.get("candidates_total"),
               (row.get("candidates_pathed") or 0)
               + (row.get("candidates_not_pathed") or 0))

        # THE VERDICT IS THE DECISION THE M1 RUN FACED, and it is derivable from
        # the row it is published beside. A ~9,040-tick clock against a
        # ~2,810-tick walk: that comparison, made by the mod instead of by
        # nobody, is this field.
        totals = [x.get("total_ticks") for x in row["rescuers"]
                  if num(x.get("total_ticks"))]
        best = min(totals) if totals else None
        clock = dig(row, "clock.ticks")
        if best is not None and num(clock):
            eq_val("6.7a", "`margin_ticks` is the clock minus the BEST total — "
                           "the whole decision, in one number (%s - %s)"
                   % (clock, best), row.get("margin_ticks"), clock - best)
            eq_val("6.7b", "…and the verdict is that comparison's answer",
                   row.get("verdict"), "in-time" if best <= clock else "too-slow")
        elif best is not None:
            eq_val("6.7a", "a casualty with a rescuer and NO clock is "
                           "`no-deadline` — named rather than folded into "
                           "in-time, because the two want different follow-ups",
                   row.get("verdict"), "no-deadline")
            eq_val("6.7b", "…and there is no margin to report",
                   row.get("margin_ticks"), None)

        # `no-deadline` on its own row: a casualty with a rescuer and no clock.
        # The anaesthetised patient is exactly that, which is why it was staged.
        nd = [r for r in cas if isinstance(r, dict) and r.get("clock") is None
              and as_list(r.get("rescuers"))]
        if nd:
            eq_val("6.7c", "the anaesthetised patient reads `no-deadline`, which "
                           "does NOT mean no hurry — a downed pawn still starves "
                           "and still needs tending",
                   nd[0].get("verdict"), "no-deadline")
            eq_val("6.7d", "…and its clock really is absent, not zero",
                   nd[0].get("clock"), None)
            eq_val("6.7e", "…and so is its margin",
                   nd[0].get("margin_ticks"), None)
        else:
            note("6.7c", "no casualty had a rescuer AND no clock, so the "
                         "`no-deadline` verdict was not staged on a live row — "
                         "the anaesthetised patient must have been gated out on "
                         "this map.")

        # ---- the gate, in the game's own words -----------------------------
        gated = []
        for r in cas:
            gated.extend(as_list(r.get("rescuers_gated_out")))
        check("6.8a", "candidates that cannot rescue are REPORTED with the gate "
                      "that refused them — 'why is nobody coming' is the "
                      "question, and a silent absence does not answer it",
              len(gated) > 0, "at least one gated-out row", gated[:3])
        if gated:
            for k in ("pawn", "name", "gate", "reason"):
                check("6.8b-%s" % k, "…and the row names %s" % k,
                      k in gated[0], "the key present", gated[0])
            gates = {g.get("gate") for g in gated if isinstance(g, dict)}
            check("6.8c", "…and every gate is one TakeToBedGate/ProviderGate "
                          "really publishes",
                  gates <= {"manipulation", "cannot-rescue", "no-bed", "no-path",
                            "will-join", "not-rescuable", "hostile", "baby",
                            "no-rest-needed", "no-care", "undrafted-only",
                            "drafted-only", "mechanoid", "null", "exception"},
                  "only gates named in the shipped switch", sorted(gates))
            hg = [g for g in gated if isinstance(g, dict)
                  and g.get("pawn") == S["handless"]]
            check("6.8d", "…and the handless doctor is among them: "
                          "ProviderGate's requiresManipulation clause is the same "
                          "capacity every Doctor work-giver wants",
                  bool(hg), "a gated-out row for %s" % S["handless"],
                  sorted(gates))
            if hg:
                eq_val("6.8e", "…on the `manipulation` gate specifically",
                       hg[0].get("gate"), "manipulation")

    # ---- (d) `act` IS THE POINT, AND IT IS EXECUTABLE -----------------------
    # `rescue` was SHIPPED, it FORCES the job through
    # Pawn_JobTracker.TryTakeOrderedJob, it INTERRUPTS LayDown — and it was
    # called ZERO times in 195 ops while the response actually tried was a
    # work-priority flip whose chosen rescuer stayed asleep for ~6,100 ticks.
    # So `act` is not advice: it is the exact envelope, and this check SENDS IT
    # VERBATIM, WITH THE CLOCK STILL STOPPED.
    actrow = None
    if not ARGS.dry_run:
        for r in cas:
            if isinstance(r, dict) and isinstance(r.get("act"), dict):
                actrow = r
                break
    precondition("6.9", "some casualty carries an executable `act`",
                 ARGS.dry_run or actrow is not None,
                 "no casualty row published `act`, so no candidate survived the "
                 "gate on any of them even with a bed staged. gated_out: %s"
                 % show(dig(cas[0], "rescuers_gated_out") if cas else None))
    if not ARGS.dry_run and actrow:
        act = actrow["act"]
        ai = cas.index(actrow)
        ab = "data.casualties.%d.act" % ai
        eq("6.9a", "`act` names the verb that FORCES the job", e, ab + ".op",
           "rescue")
        shape("6.9b", "triage", e, ab + ".args.pawn")
        shape("6.9c", "triage", e, ab + ".args.target")
        eq_val("6.9d", "…with the patient's id filled in, not a placeholder",
               dig(act, "args.target"), actrow.get("pawn"))
        check("6.9e", "…and the rescuer it names is one it actually pathed",
              dig(act, "args.pawn") in [x.get("pawn")
                                        for x in as_list(actrow.get("rescuers"))],
              "act.args.pawn among the pathed rescuers",
              (dig(act, "args.pawn"),
               [x.get("pawn") for x in as_list(actrow.get("rescuers"))]))
        contains("6.9f", "…and says WHY it is not a work-priority flip", e,
                 ab + ".why", "TryTakeOrderedJob")

        # THE RACE THIS GUARDS AGAINST, asserted where it bites. On the s21
        # bench this exact send came back `cannot-rescue` — because the rescuer
        # had carried the patient to the bed between the read and the send, and
        # `HealthAIUtility.CanRescueNow` is false for a patient already in one.
        # The clock has been stopped since 6.5b and is re-asserted here, because
        # a `pause` that no-opped would put the race straight back.
        d = send("digest")
        eq("6.10a", "the clock is STILL stopped between the triage read and the "
                    "act — `act` is a snapshot, and on the s21 bench a send "
                    "against a running world came back `cannot-rescue` because "
                    "the rescuer got there first", d, "data.time.paused", True)

        # SENT VERBATIM. Nothing is rebuilt, renamed or defaulted: whatever
        # `act` says is what goes on the wire.
        print("  %ssending triage's own act verbatim: %s%s"
              % (DIM, json.dumps(act, separators=(",", ":"))[:200], OFF))
        r = send(act["op"], act["args"])
        S["rescue"] = r
        eq("6.10b", "the act triage published is ACCEPTED as sent", r, "ok", True)
        acc = as_list(dig(r, "data.accepted"))
        rej = as_list(dig(r, "data.rejected"))
        check("6.10c", "…and the rescuer took the job rather than being refused",
              len(acc) >= 1, "at least one accepted doer",
              {"accepted": acc, "rejected": rej})
        if acc:
            eq_val("6.10d", "…and it is a Rescue job, forced, not a hope",
                   dig(acc[0], "job_def"), "Rescue")
            eq_val("6.10e", "…on the pawn `act` named",
                   acc[0].get("pawn"), dig(act, "args.pawn"))
            shape("6.10f", "rescue", r, "data.accepted.0.bed")
        eq_val("6.10g", "…and the patient `act` named is the target the verb "
                        "echoes back",
               dig(r, "data.target.id"), dig(act, "args.target"))
        ge("6.10h", "…and the rescue reached the journal", r,
           "data.action.journal_seq", 1)

        # …and the world agrees, read back rather than inferred from the echo.
        e2 = send("triage")
        doer = dig(act, "args.pawn")
        doing = None
        for r2 in as_list(dig(e2, "data.casualties")):
            for x in as_list(r2.get("rescuers")):
                if isinstance(x, dict) and x.get("pawn") == doer:
                    doing = x.get("doing")
        if doing is not None:
            contains("6.10i", "…and a second triage read sees the rescuer doing "
                              "it — `doing` is the field that exists because the "
                              "M1 rescuer stayed asleep for ~6,100 ticks",
                     {"d": doing}, "d", "rescu")
        else:
            note("6.10i", "the rescuer no longer appears as a candidate on any "
                          "casualty, which is what happens once it is carrying "
                          "one. 6.10d is the witness that the job is Rescue.")


# ------------------------------------------------------------------- phase 7 --

def phase7():
    banner("PHASE 7 - THE M1 END-STATE: work-cover's two refusals, the predicate "
           "that halts on them, and the seam with 722c951")

    # ---- (a) the predicate ARMED while coverage still holds -----------------
    # Phase 5 repaired Doctor, so `work_coverage.ok == false` is FALSE at arm
    # time. `edge` defaults to true, so this proves the predicate is armed,
    # resolves against the section, and is evaluated — without halting.
    e = send("digest")
    covered = dig(e, "data.work_coverage.ok")
    if ARGS.dry_run or covered is True:
        e = advance({"until": {"condition": {"path": "work_coverage.ok",
                                             "op": "==", "value": False}},
                     "timeout_ticks": 400, "speed": "fast"}, timeout=300)
        shape("7.1a", "advance", e, "data.until", dict)
        eq("7.1b", "the predicate is ARMED against the section", e,
           "data.until.path", "work_coverage.ok")
        eq("7.1c", "…and resolved to the section, not guessed", e,
           "data.until.section", "work_coverage")
        eq("7.1d", "…it was not already true when armed", e,
           "data.until.true_when_armed", False)
        ge("7.1e", "…it was actually evaluated", e, "data.until.evaluations", 1)
        eq("7.1f", "…and with coverage holding it did not halt", e,
           "data.reason", "timeout")
        shape("7.1g", "advance", e, "data.until.eval_ms_avg")
        print("  %spredicate cost: %s ms/eval over %s evaluations%s"
              % (DIM, dig(e, "data.until.eval_ms_avg"),
                 dig(e, "data.until.evaluations"), OFF))
    else:
        note("7.1", "work_coverage.ok was already false when phase 7 began "
                    "(%s), so the armed-and-holding case could not be staged. "
                    "7.4 still proves the halt." % covered)

    # ---- (b) THE M1 END-STATE ----------------------------------------------
    # Down everybody except one promotable pawn and the handless one. That is
    # the colony M1 ended with: the pawns who could doctor are the patients.
    # It is also the only arrangement in which `work-cover` can refuse, because
    # a refusal needs `ranked.Count < shortBy` and a healthy roster always has
    # somebody to promote.
    set_doctor(S["ids"], 0)
    keep = S["rescuer"]
    victims = [i for i in S["ids"] if i not in (keep, S["handless"])]
    if not ARGS.dry_run:
        for pid in victims:
            send("dev:damage", {"pawn": pid, "mode": "until-downed",
                                "allow_bleeding": False})
    S["downed"] = list(victims)
    print("  %sdowned: %s — standing: %s (promotable) and %s (handless)%s"
          % (DIM, victims, keep, S["handless"], OFF))

    seq_before = 0 if ARGS.dry_run else watermark()

    # ---- (b2) THE PROJECTION ON A COLONY SHORT BY TWO — git-bug 58794e4 ----
    # 58794e4's second acceptance bullet asks for exactly this state, because
    # phase 5's colony cannot produce it: there, one promotion clears the only
    # shortfall, so `still_under:[]` is right whether the verb PROJECTS or
    # merely REPORTS. Here Doctor is off across the roster (`available:0`
    # against a floor of 2) and only ONE colonist can be promoted — everybody
    # else is downed, and the handless one is excluded by `MissingCapacity`. So
    # a projection that is honest has to come back saying the repair is still
    # short, which is the opposite of the optimism a naive projection would
    # produce and the opposite of the pre-repair reading the old code gave.
    #
    # It runs BEFORE (c)'s real call and mutates nothing, so (c) still meets the
    # state it was written for.
    e = send("work-cover", {"work": DOCTOR, "dry_run": True})
    S["cover_few_dry"] = e
    dsu = as_list(dig(e, "data.still_under"))
    drep = as_list(dig(e, "data.repaired"))
    check("7.2p1", "the dry run plans the ONE promotion the roster allows",
          ARGS.dry_run or len(drep) == 1, "one planned promotion", drep)
    check("7.2p2", "…and `still_under` is NOT empty, because one promotion "
                   "against a floor of two is a PARTIAL repair — the case that "
                   "tells projecting apart from reporting",
          ARGS.dry_run or len(dsu) == 1, "one still_under row", dsu)
    if not ARGS.dry_run and dsu:
        eq("7.2p3", "…and `have` is the PROJECTED count, not the pre-repair 0",
           e, "data.still_under.0.have", 1)
        eq("7.2p4", "…one short of the floor rather than two", e,
           "data.still_under.0.short_by", 1)
        eq("7.2p5", "…and `available` no longer contradicts the `have` it is the "
                    "floor for: both are post-call", e,
           "data.still_under.0.available", 1)
        eq("7.2p6", "…so the PROJECTED coverage says the floor is still unmet — "
                    "a projection that lied would say the opposite, and the "
                    "pre-repair reading this replaces would have said it for the "
                    "wrong reason", e, "data.coverage_after.ok", False)
        cu = as_list(dig(e, "data.coverage_after.under"))
        check("7.2p7", "…with Doctor still named in it", DOCTOR in cu,
              "Doctor in %s" % (cu,), cu)
        # THE OTHER SIDE OF 6fc75e3's GUARD. 5.2c is a dry run that owed no
        # journal line; this one falls short, so `stillUnder > 0` and it DOES
        # owe one — and `step:"plan"` is reachable only here.
        ge("7.2p8", "…and THIS dry run does owe a journal line, because a "
                    "decision that could not be carried out is exactly the one "
                    "the ledger must carry", e, "data.action.journal_seq", 1)

    # ---- (c) `too-few-candidates` ------------------------------------------
    e = send("work-cover", {"work": DOCTOR})
    S["cover_few"] = e
    eq("7.2a", "the envelope is STILL ok:true — the call was processed", e,
       "ok", True)
    eq("7.2b", "…while the verb's own verdict is false", e, "data.ok", False)
    su = as_list(dig(e, "data.still_under"))
    check("7.2c", "…and it says so explicitly rather than succeeding with nobody "
                  "assigned", ARGS.dry_run or len(su) == 1,
          "one still_under row", su)
    if not ARGS.dry_run and su:
        eq("7.2d", "…naming the gate: one candidate against a floor of two",
           e, "data.still_under.0.gate", "too-few-candidates")
        for k in ("work", "floor", "floor_on", "have", "short_by",
                  "enabled_this_call", "candidates_offered", "capable",
                  "enabled", "available", "reason"):
            shape("7.2e-%s" % k, "work-cover", e, "data.still_under.0." + k)
        eq("7.2f", "…with every count that decides the follow-up: `enabled` "
                   "short means promote, `capable` short means recruit", e,
           "data.still_under.0.floor", DOCTOR_FLOOR)
        eq("7.2g", "…and the promotion it COULD make was still made", e,
           "data.still_under.0.enabled_this_call", 1)
        # THE ELEVENTH KEY, AND THE ONE THAT WAS CONDITIONAL. `work-cover`'s own
        # note tells a caller to branch on this list — "`enabled` short means
        # promote, `capable` short means recruit, a non-empty
        # `enabled_but_incapable` list means the fix is surgery" — and until the
        # session-21 fix it was emitted only `if (r.Impaired.Count > 0)`. This
        # fixture has Doctor OFF across the whole roster (`set_doctor(ids, 0)`
        # above), so nobody is enabled-but-incapable and the OLD build published
        # nothing at all here. That is why this is the check that caught it: an
        # absent key is indistinguishable from a wrong dig path.
        shape("7.2h", "work-cover", e, "data.still_under.0.enabled_but_incapable",
              list)
        imp = as_list(dig(e, "data.still_under.0.enabled_but_incapable"))
        en = dig(e, "data.still_under.0.enabled")
        check("7.2i", "…and the impaired are a SUBSET of the enabled, which is "
                      "what the field means: WorkCoverage.Compute only reaches "
                      "`Impaired` for a pawn already in `Enabled`",
              ARGS.dry_run or (num(en) and len(imp) <= en),
              "len(enabled_but_incapable) <= enabled", (len(imp), en))

    # ---- (d) `no-candidate` -------------------------------------------------
    # Now the one promotable pawn is enabled, so there is nobody left at all.
    # `enabled` short means promote; `capable` short means recruit; this is the
    # third answer, and it is a fact about the roster rather than a refused
    # write.
    e = send("work-cover", {"work": DOCTOR})
    S["cover_none"] = e
    su = as_list(dig(e, "data.still_under"))
    if not ARGS.dry_run and su:
        eq("7.3a", "with nobody left to promote the gate is `no-candidate`", e,
           "data.still_under.0.gate", "no-candidate")
        eq("7.3b", "…and it offered nobody", e,
           "data.still_under.0.candidates_offered", 0)
        contains("7.3c", "…and says it is a fact about the ROSTER, not a refused "
                         "write", e, "data.still_under.0.reason", "roster")
        eq("7.3d", "…and the envelope is still ok:true", e, "ok", True)
        eq("7.3e", "…and the verdict still false", e, "data.ok", False)
        eq("7.3f", "…and nothing was promoted this call", e,
           "data.still_under.0.enabled_this_call", 0)
    else:
        note("7.3", "the second work-cover call found candidates again "
                    "(gate=%s), so `no-candidate` was not staged."
             % dig(e, "data.still_under.0.gate"))

    # A refusal is journalled too, because a decision that changed nothing is
    # exactly the one a post-mortem needs (4087644's finding, one level up).
    e3 = journal_since(seq_before, types=["action"])
    acts = [a for a in as_list(dig(e3, "data.events"))
            if isinstance(a, dict) and dig(a, "payload.verb") == "work-cover"]
    check("7.3g", "both refusals reached the journal — a wasted decision is "
                  "exactly the one the ledger must carry",
          ARGS.dry_run or len(acts) >= 2,
          ">= 2 work-cover action rows since seq %s" % seq_before, len(acts))

    # ---- (e) the predicate HALTS on the state the refusals just described ---
    e = send("digest")
    if ARGS.dry_run or dig(e, "data.work_coverage.ok") is False:
        e = advance({"until": {"condition": {"path": "work_coverage.ok",
                                             "op": "==", "value": False,
                                             "edge": False}},
                     "timeout_ticks": 20000, "speed": "fast"}, timeout=300)
        eq("7.4a", "with the colony's doctor coverage gone the predicate HALTS", e,
           "data.reason", "condition")
        eq("7.4b", "…naming the path that tripped it", e,
           "data.halted_on.path", "work_coverage.ok")
        eq("7.4c", "…identifying itself as a state halt", e,
           "data.halted_on.kind", "condition")
        eq("7.4d", "…reporting the value it read", e,
           "data.halted_on.observed", False)
        check("7.4e", "…and publishing NO halted_seq, because a state halt names "
                      "no journal line",
              not has_key(e, "data.halted_seq"), "the key absent",
              dig(e, "data.halted_seq"))
        lt("7.4f", "…and it halted at once rather than running the budget", e,
           "data.ticks_elapsed", 5000)
    else:
        note("7.4", "coverage did not go under on this roster (ok=%s), so the "
                    "halt could not be staged."
             % dig(e, "data.work_coverage.ok"))

    # ---- (f) THE SEAM with 722c951 -----------------------------------------
    banner("PHASE 7b - THE SEAM: what an UNESCAPED caller gets")

    # NOBODY ELSE WRITES THIS. `722c951` proves that `advance` halts on an
    # own-faction casualty; this proves that the halt is what a caller who did
    # not ask for the escape actually receives — and, symmetrically, that the
    # escape every other advance in this file leans on really does something.
    #
    # ============ THE RULING THIS PHASE RESTS ON (2026-09-01) ================
    # THE CASUALTY HALT IS ON THE TRANSITION, NOT ON THE STATE. This phase used
    # to read `722c951`'s "an advance SPANNING an own-faction downing stops at
    # it" as "an advance made while a colonist is down is refused", staged four
    # already-downed colonists, and asserted that an unescaped advance across
    # them could not complete. On the s21 bench it completed — `ok:true
    # reason:"ticks" ticks:300` — and the mod was right.
    #
    # `JournalHooks.Patch_MakeDowned` and `Patch_SetDead` are POSTFIXES ON THE
    # TRANSITION (`Pawn_HealthTracker.MakeDowned` / `SetDead`), so a pawn
    # already down when the advance starts emits nothing and the halt does not
    # fire. That is deliberate and 722c951 #2 says so in as many words: "the
    # hooks are on the transition, so this cannot re-fire for the same pawn and
    # cannot wedge." A state halt would mean that once a colonist is down and
    # cannot be rescued — which on a bedless map is EVERY casualty, verdict
    # `no-rescuer`, see 6.4a — every subsequent advance refuses forever with no
    # act that clears it. That is the same argument `40ed42f` part 3 already
    # used to make the bleedout deadline refuse on `too-slow` and on nothing
    # else, and the escape does not save it: `through_casualties` is per-call
    # and not a mode (722c951's 2.17/2.18), so a state halt would force the
    # escape onto every advance for the rest of the run and train the agent to
    # pass it blind — which is precisely the guard being switched off.
    #
    # 722c951's own words carry the transition reading everywhere they are
    # specific: "stop early when an own-faction pawn goes down or dies DURING
    # the advance, returning what happened AND THE TICK IT HAPPENED AT" (a state
    # has no such tick); "an advance spanning a HOSTILE downing does NOT stop —
    # prove the filter is on faction, NOT ON THE EVENT" (a downing is an event);
    # "replay the M1 shape: an advance ACROSS TICK 214,599 stops there".
    #
    # Recorded in DESIGN's decisions log and on 722c951. This phase now asserts
    # BOTH halves of it: 7.5a that the state does not halt (so a run cannot
    # wedge), 7.6 that the transition does.
    precondition("7.5", "there is a downed own-faction colonist to advance past",
                 ARGS.dry_run or len(S.get("downed") or []) > 0,
                 "phase 7 downed nobody, so the seam cannot be tested")

    # Clear the unread journal delta first, so the only live guard is the
    # casualty one and the two cannot be mistaken for each other.
    if not ARGS.dry_run:
        journal_since(max(0, watermark() - 1), limit=5)

    # ---- (f1) THE STATE DOES NOT HALT — the anti-wedge half -----------------
    # A bare `{"ticks": 300}` and not a predicate, for `722c951`'s own reason
    # (`pass_time()`'s note): the subject under test is the advance itself, not
    # a state the game reaches, and there is no predicate spelling for "some
    # time passed". 300 is short on purpose — the only bleeder left is phase 2's
    # subject, whose clock read ~12,500 ticks at 2.4, so nothing NEW can go down
    # or die inside this window and the completion cannot be luck.
    e = raw_advance({"ticks": 300, "speed": "normal"}, timeout=240)
    S["state_advance"] = e
    print("  %sunescaped advance across %d ALREADY-downed colonist(s): "
          "ok=%s reason=%s ticks=%s%s"
          % (DIM, len(S.get("downed") or []), dig(e, "ok"), dig(e, "data.reason"),
             dig(e, "data.ticks_elapsed"), OFF))
    eq("7.5a", "an UNESCAPED advance made while own-faction colonists are "
               "ALREADY down COMPLETES — the halt is on the transition, so a "
               "colony that cannot rescue its casualty is not wedged", e,
       "data.reason", "ticks")
    ge("7.5b", "…and it really ran the ticks it was asked for", e,
       "data.ticks_elapsed", 300)

    # ---- (f2) THE TRANSITION DOES HALT, unescaped ---------------------------
    # `journal-selftest --steps down-at` is 722c951's fixture and the ONLY way
    # to drive this from outside the game: it arms from the command drain and
    # FIRES from GameComponentTick, i.e. inside DoSingleTick, inside the
    # advance. A `dev:damage` sent over the protocol lands while the game is
    # paused and would prove nothing about an advance halting.
    standing = []
    if not ARGS.dry_run:
        r = send("pawns", {"filter": "colonist", "cap": 200, "order": "id"})
        for row in as_list(dig(r, "data.list")):
            if not isinstance(row, dict) or row.get("id") is None:
                continue
            if "downed" in as_list(row.get("flags")):
                continue
            standing.append(row["id"])
    print("  %sstanding colonists for the two arms: %s%s" % (DIM, standing, OFF))
    have_fixture = ARGS.dry_run or len(standing) >= 2
    precondition("7.5c", "two standing own-faction colonists — one per arm of "
                         "the escaped/unescaped experiment",
                 have_fixture,
                 "found %s standing; 7.6 downs one INSIDE an unescaped advance "
                 "and 7.7 downs the other inside an escaped one, and the pair "
                 "is the whole point: same fixture, same span, the escape the "
                 "only variable" % len(standing))

    DOWN_DELAY = 400          # ticks into the advance the downing fires
    SPAN = 3000               # the advance's OWN bound, well past it

    if have_fixture:
        victim_a = standing[0] if standing else 0
        e = send("journal-selftest", {"steps": ["down-at"],
                                      "down_delay_ticks": DOWN_DELAY,
                                      "down_pawn": victim_a})
        precondition("7.5d", "`journal-selftest --steps down-at` can arm a "
                             "downing INSIDE an advance",
                     ARGS.dry_run or dig(e, "ok") is True,
                     "down-at refused: %s — it is dev-gated like every other "
                     "step and 722c951 owns it" % show(dig(e, "error")))
        eq("7.5e", "…on an OWN-FACTION pawn, which is what the filter keys on",
           e, "data.down_at.player_faction", True)
        fires = dig(e, "data.down_at.fires_at_tick")
        # Arming journals a `dev` row of its own; discharge it so the unescaped
        # advance below meets the CASUALTY guard and not the unread one.
        if not ARGS.dry_run:
            journal_since(max(0, watermark() - 1), limit=5)
        t0 = dig(send("digest", {"sections": ["time"]}), "data.time.tick") or 0
        # THE ADVANCE'S OWN BOUND IS A PREDICATE — "until the clock reaches T",
        # T read off the game — so "it stopped early" is a measurement and not
        # an interpretation. `timeout_ticks` bounds it regardless (git-bug
        # 1113019: an `until` with no bound whose predicate is already true at
        # arm time runs unbounded).
        e = raw_advance({"until": {"condition": {"path": "time.tick",
                                                 "op": ">=",
                                                 "value": t0 + SPAN}},
                         "timeout_ticks": SPAN + 2000, "speed": "fast"},
                        timeout=300)
        S["unescaped"] = e
        ok = dig(e, "ok")
        reason = dig(e, "data.reason")
        ticks = dig(e, "data.ticks_elapsed")
        print("  %sunescaped advance ACROSS a downing (armed %s, bound %s): "
              "ok=%s reason=%s ticks=%s error=%s%s"
              % (DIM, fires, t0 + SPAN, ok, reason, ticks,
                 show(dig(e, "error")), OFF))
        # THE INVARIANT FIRST, and it is deliberately not a spelling: what
        # cannot be up for grabs is that an advance which crosses an own-faction
        # downing does not come back looking like one that ran to its own bound.
        innocuous = (ok is True and reason in ("ticks", "condition", "timeout"))
        check("7.6a", "an advance across an own-faction downing does not come "
                      "back as an ordinary completed advance — it is refused, "
                      "or it halts (722c951)",
              ARGS.dry_run or not innocuous,
              "ok:false, or a reason that is not the advance's own bound",
              {"ok": ok, "reason": reason, "ticks_elapsed": ticks,
               "error": dig(e, "error")})
        eq("7.6b", "…and the spelling 722c951 shipped and proved on a bench is "
                   "`casualty`", e, "data.reason", "casualty")
        eq("7.6c", "…identifying itself as a casualty halt", e,
           "data.halted_on.kind", "casualty")
        eq("7.6d", "…naming the event class", e, "data.halted_on.event", "downed")
        eq("7.6e", "…and the pawn, so the response is one `rescue` call away", e,
           "data.halted_on.pawn_id", victim_a)
        eq("7.6f", "…and that it was OUR faction", e,
           "data.halted_on.player_faction", True)
        check("7.6g", "…and it stopped EARLY — the M1 shape (step 148 crossed a "
                      "downing at 214,599 and ran on), halted",
              ARGS.dry_run or (num(ticks) and ticks < SPAN - 1000),
              "ticks_elapsed well under the predicate's %s" % SPAN, ticks)
        eq("7.6h", "…and whatever happened, the game is left PAUSED",
           send("digest"), "data.time.paused", True)

        # ---- (f3) THE SAME EXPERIMENT WITH THE ESCAPE ON --------------------
        # Same fixture step, same span, a second victim: the escape is the ONLY
        # variable, which is the shape 722c951's phases 3 and 4 use for the
        # faction filter.
        victim_b = standing[1] if len(standing) > 1 else 0
        e = send("journal-selftest", {"steps": ["down-at"],
                                      "down_delay_ticks": DOWN_DELAY,
                                      "down_pawn": victim_b})
        precondition("7.7", "a second downing armed for the escaped arm",
                     ARGS.dry_run or dig(e, "ok") is True,
                     "down-at refused for %s: %s" % (victim_b, show(dig(e, "error"))))
        t0 = dig(send("digest", {"sections": ["time"]}), "data.time.tick") or 0
        e = advance({"until": {"condition": {"path": "time.tick", "op": ">=",
                                             "value": t0 + SPAN}},
                     "timeout_ticks": SPAN + 2000, "speed": "fast"}, timeout=300)
        S["escaped"] = e
        print("  %sESCAPED advance across the same fixture: ok=%s reason=%s "
              "ticks=%s%s" % (DIM, dig(e, "ok"), dig(e, "data.reason"),
                              dig(e, "data.ticks_elapsed"), OFF))
        eq("7.7a", "…and with `through_casualties` given a reason it proceeds",
           e, "ok", True)
        eq("7.7b", "…to its OWN bound rather than the casualty — the escape is "
                   "the only thing that changed", e, "data.reason", "condition")
        ge("7.7c", "…having actually run PAST the downing it was armed for", e,
           "data.ticks_elapsed", DOWN_DELAY)
        shape("7.7d", "advance", e, "data.through_casualties")
        eq("7.7e", "…and it still leaves the game paused", send("digest"),
           "data.time.paused", True)
    else:
        note("7.6", "the down-at fixture could not be staged, so neither arm of "
                    "the seam ran. 7.5a still proves the anti-wedge half: an "
                    "unescaped advance across ALREADY-downed colonists "
                    "completes.")

    note("7.8", "7.6a asserts an INVARIANT and 7.6b-7.6f the spelling. The "
                "invariant is what the seam owns: an advance crossing an "
                "own-faction downing does not return as an ordinary completed "
                "advance. The spelling is 722c951's and is now merged and "
                "bench-proved, so asserting it here is a join between the two "
                "suites rather than a guess. 7.5a is the OTHER half of the "
                "2026-09-01 ruling and the one this phase used to get wrong: "
                "the halt is on the transition, so a colony that is already "
                "carrying a casualty it cannot rescue still advances.")


# ------------------------------------------------------------------- phase 8 --

def phase8():
    banner("PHASE 8 - the standing invariant")

    e = journal_since(S["seq0"], types=["red_error"], limit=50)
    eq("8.1", "no red errors across the whole run", e, "data.count", 0)
    if not ARGS.dry_run and (dig(e, "data.count") or 0) > 0:
        print("  %s%s%s" % (RED, show(dig(e, "data.events")), OFF))

    e = send("digest")
    eq("8.2", "the game is left paused", e, "data.time.paused", True)
    shape("8.3", "digest", e, "data.work_coverage", dict)
    shape("8.4", "digest", e, "data.work_coverage.ok")


# ------------------------------------------------------------------- phase 9 --

def probe(fn):
    """Run one assertion in isolation and report whether it PASSED, without
    letting it touch the real counters or print."""
    global CHECKS, CAPTURE
    c0, f0 = CHECKS, len(FAILS)
    CAPTURE = []
    try:
        fn()
    finally:
        passed = len(FAILS) == f0
        del FAILS[f0:]
        CHECKS = c0
        CAPTURE = None
    return passed


def _s21(name):
    p = os.path.join(S21, name)
    if not os.path.exists(p):
        return None
    with open(p, encoding="utf-8") as fh:
        return json.load(fh)


def _src(fname):
    p = os.path.join(REPO, "Source", "AutoRimmer", fname)
    if not os.path.exists(p):
        return None
    with open(p, encoding="utf-8") as fh:
        return fh.read()


def phase9():
    banner("PHASE 9 - the suite's OWN machinery (offline; no bench, no game)")

    # THE ENVELOPES ARE REAL. Phase 9 does not invent its fixtures: it runs the
    # helpers over the RAW envelopes the orchestrator banked at
    # accept/runs/s21-20260901/, so "the assertions work" means "the assertions
    # work on what the mod actually emitted", not "the assertions agree with a
    # dict this file wrote."
    healthy = _s21("05-pawn-healthy.json")
    bleeding = _s21("07-pawn-bleeding.json")
    captain = _s21("09-pawn-captain-shape.json")
    healed = _s21("11-pawn-healed.json")
    digest = _s21("03-digest.json")
    if healthy is None:
        note("9.0", "accept/runs/s21-20260901/ is not in this checkout, so the "
                    "envelope half of phase 9 cannot run. The source-derivation "
                    "checks below still do.")
    else:
        # 9.1 shape() is the predicate dig() cannot be.
        check("9.1a", "shape() PASSES on a key that is present",
              probe(lambda: shape("x", "pawn", captain, "data.health.bleedout.ticks")),
              "pass", "fail")
        check("9.1b", "shape() PASSES on a PRESENT-AND-NULL key — the whole "
                      "reason 61794cd's 'omits or nulls it' bullet needs it",
              probe(lambda: shape("x", "pawn", healthy,
                                  "data.health.ticks_until_bleedout")),
              "pass", "fail")
        check("9.1c", "shape() FAILS on a renamed key",
              not probe(lambda: shape("x", "pawn", healthy,
                                      "data.health.ticks_till_bleedout")),
              "fail", "pass")
        check("9.1d", "shape(kind=) FAILS when the type is wrong",
              not probe(lambda: shape("x", "pawn", captain,
                                      "data.health.hediffs", dict)),
              "fail", "pass")

        # 9.2 THE TRAP, demonstrated rather than described.
        check("9.2a", "eq(...,None) PASSES on an ABSENT key — the hazard this "
                      "suite's phase 0 exists to close",
              probe(lambda: eq("x", "t", healthy,
                               "data.health.no_such_field", None)),
              "pass (and that is the hazard)", "fail")
        check("9.2b", "…which is why shape() runs first: it fails on that path",
              not probe(lambda: shape("x", "t", healthy,
                                      "data.health.no_such_field")),
              "fail", "pass")
        check("9.2c", "eq() correctly reads the healthy pawn's null clock",
              probe(lambda: eq("x", "t", healthy,
                               "data.health.ticks_until_bleedout", None)),
              "pass", "fail")
        check("9.2d", "…and FAILS if it were asserted null on the bleeding one",
              not probe(lambda: eq("x", "t", captain,
                                   "data.health.ticks_until_bleedout", None)),
              "fail", "pass")

        # 9.3 keys_exactly() over the shipped bleedout block.
        check("9.3a", "keys_exactly() PASSES on the shipped bleedout field set",
              probe(lambda: keys_exactly("x", "t", captain,
                                         "data.health.bleedout", BLEEDOUT_KEYS)),
              "pass", "fail")
        drift = json.loads(json.dumps(captain))
        drift["data"]["health"]["bleedout"]["ticks_until_death"] = 7223
        check("9.3b", "…and FAILS when a field is added under a second spelling",
              not probe(lambda: keys_exactly("x", "t", drift,
                                             "data.health.bleedout", BLEEDOUT_KEYS)),
              "fail", "pass")
        gone = json.loads(json.dumps(captain))
        del gone["data"]["health"]["bleedout"]["game_shows_clock"]
        check("9.3c", "…and FAILS when one is dropped",
              not probe(lambda: keys_exactly("x", "t", gone,
                                             "data.health.bleedout", BLEEDOUT_KEYS)),
              "fail", "pass")

        # 9.4 THE WIDGET GATE, against all four banked reads.
        for tag, env, want in (("9.4a", healthy, False),
                               ("9.4b", bleeding, True),
                               ("9.4c", captain, True),
                               ("9.4d", healed, False)):
            h = dig(env, "data.health") or {}
            got = game_shows_clock(h.get("bleed_rate"),
                                   dig(env, "data.health.bleedout.ticks"),
                                   bool(dig(env, "data.health.bleedout.deathless_gene")))
            eq_val(tag, "the python gate agrees with the shipped "
                        "game_shows_clock on banked read %s (rate=%s ticks=%s)"
                   % (tag[-1], h.get("bleed_rate"),
                      dig(env, "data.health.bleedout.ticks")),
                   got, dig(env, "data.health.bleedout.game_shows_clock"))
            eq_val(tag + "-w", "…and it is %s, as the bench recorded" % want,
                   got, want)
        # The WontBleedOutSoon branch, which the banked set does not contain:
        # constructed here so the third arm of the three-way is exercised at all.
        eq_val("9.4e", "a real bleed with a clock of a full day or more does NOT "
                       "show: HealthCardUtility prints WontBleedOutSoon at "
                       ">= 60000", game_shows_clock(0.4, 90000, False), False)
        eq_val("9.4f", "…and one tick under the boundary DOES",
               game_shows_clock(0.4, 59999, False), True)
        eq_val("9.4g", "…and the line is not drawn at all at or below 0.01 bleed",
               game_shows_clock(0.01, 100, False), False)
        eq_val("9.4h", "…and a Deathless pawn gets a word, never a number",
               game_shows_clock(3.0, 100, True), False)

        # 9.5 THE HEADLINE ASSERTIONS, run against the banked Captain shape.
        rows = dig(captain, "data.health.hediffs") or []
        eq_val("9.5a", "banked read 09: BloodLoss is row 0 under a real 34-row "
                       "truncation", rows[0].get("def") if rows else None, "BloodLoss")
        eq_val("9.5b", "…the list is capped at HediffCap", len(rows), HEDIFF_CAP)
        eq_val("9.5c", "…and the arithmetic closes",
               dig(captain, "data.health.hediffs_total"),
               len(rows) + dig(captain, "data.health.hediffs_more"))
        # THE REFUTATION, on both sides of the stage boundary, from two reads of
        # one pawn. This is the single sharpest measurement either issue has.
        eq_val("9.5d", "…and at severity 0.562 that row's `life_threatening` is "
                       "FALSE, so 'life-threatening first' would have dropped it",
               rows[0].get("life_threatening") if rows else None, False)
        hrows = dig(healed, "data.health.hediffs") or []
        eq_val("9.5e", "…while the SAME pawn at 0.623, over BloodLoss's fifth "
                       "stage (minSeverity 0.60), reads TRUE — the flag is a "
                       "stage property and the band that works is IsLethal",
               hrows[0].get("life_threatening") if hrows else None, True)
        eq_val("9.5f", "…and the clock is independent of the hediff list: banked "
                       "read 07 has BloodLoss ABSENT at severity 0.007 and a "
                       "16,317-tick clock anyway",
               [r.get("def") for r in (dig(bleeding, "data.health.hediffs") or [])
                if r.get("def") == "BloodLoss"], [])
        ge("9.5g", "…with the clock finite regardless", bleeding,
           "data.health.ticks_until_bleedout", 1)

        # 9.6 work_coverage, over the banked digest.
        wc = dig(digest, "data.work_coverage") or {}
        wrows = as_list(wc.get("rows"))
        eq_val("9.6a", "banked digest: ok is false with Doctor under",
               (wc.get("ok"), wc.get("under")), (False, ["Doctor"]))
        dr = find_row(wrows, DOCTOR) or {}
        eq_val("9.6b", "…Doctor's floor is 2 on AVAILABILITY",
               (dr.get("floor"), dr.get("floor_on")), (DOCTOR_FLOOR, "available"))
        eq_val("9.6c", "…the FINE rows really do carry exactly three fields",
               sorted({tuple(sorted(r)) for r in wrows if r.get("work") != DOCTOR}),
               [tuple(sorted(FINE_ROW_KEYS))])
        eq_val("9.6d", "…and every non-Doctor floor is 1",
               sorted({r.get("floor") for r in wrows if r.get("work") != DOCTOR}), [1])
        eq_val("9.6e", "…and nothing was dropped, so 3.5a's invariant holds "
                       "vacuously on this read", wc.get("more"), 0)
        # THE FINDING, measured on the banked envelope so it cannot be argued
        # away: the published `order` is not the order the rows are in.
        fu = next((i for i, r in enumerate(wrows)
                   if r.get("work") in as_list(wc.get("under"))), None)
        check("9.6f", "the FINDING at 3.7 is real on the banked envelope: `order` "
                      "claims under-first and the only UNDER row is at index %s"
                      % fu, fu != 0,
              "the under row NOT at index 0, contradicting `order`", fu)

    # ---- 9.6b the triage/work-cover half of the smoke ----------------------
    tri_nobed = _s21("18-triage-downed.json")
    tri_bed = _s21("20-triage-with-bed.json")
    tri_tend = _s21("16-triage-nocasualty.json")
    cover_dry = _s21("12-workcover-dryrun.json")
    cover_real = _s21("13-workcover-real.json")
    act_exec = _s21("21-act-executed.json")
    if tri_bed is None:
        note("9.6g", "the triage envelopes (12-23) are not in this checkout; the "
                     "banked half of the triage assertions could not run.")
    else:
        # THE UNION, on the banked reads: one pawn, four states, four verdicts.
        r = dig(tri_tend, "data.casualties.0")
        eq_val("9.6h", "banked read 16: a STANDING pawn who merely needs tending "
                       "is a casualty — the Alert_ColonistNeedsTend inversion "
                       "routed around",
               (r.get("downed"), r.get("needs_tend"), r.get("verdict")),
               (False, True, "no-rescuer"))
        r = dig(tri_nobed, "data.casualties.0")
        eq_val("9.6i", "banked read 18: DOWNED with no bed on the map is "
                       "`no-rescuer` with nothing pathed",
               (r.get("verdict"), r.get("candidates_total")), ("no-rescuer", 0))
        eq_val("9.6j", "…and the refusal is `no-bed`, in the game's own sentence "
                       "— the last clause of TakeToBedGate",
               dig(r, "rescuers_gated_out.0.gate"), "no-bed")
        check("9.6k", "…and no `act` is published when nobody survived the gate",
              "act" not in r, "act absent from the row", r.get("act"))
        r = dig(tri_bed, "data.casualties.0")
        eq_val("9.6l", "banked read 20: one bed turns the same casualty into "
                       "`in-time` with a populated act",
               (r.get("verdict"), dig(r, "act.op")), ("in-time", "rescue"))
        eq_val("9.6m", "…margin_ticks is the clock minus the best total_ticks",
               r.get("margin_ticks"),
               dig(r, "clock.ticks") - min(x["total_ticks"] for x in r["rescuers"]))
        eq_val("9.6n", "…total_ticks is travel plus carry, both legs",
               r["rescuers"][0]["total_ticks"],
               r["rescuers"][0]["travel_ticks"] + r["rescuers"][0]["carry_ticks"])
        eq_val("9.6o", "…and act names the patient and a rescuer that was pathed",
               (dig(r, "act.args.target") == r["pawn"],
                dig(r, "act.args.pawn") in [x["pawn"] for x in r["rescuers"]]),
               (True, True))
        keys_exactly("9.6p", "…and triage's clock block is the WHOLE bleedout "
                             "block, byte for byte the one `pawn` publishes",
                     {"c": r.get("clock")}, "c", BLEEDOUT_KEYS)
        # THE RACE, banked. 21 is the act sent verbatim and refused, and the
        # refusal is the GAME being right — the rescuer got there first. This
        # is why triage_read() pauses.
        eq_val("9.6q", "banked read 21: the act sent against a MOVING world came "
                       "back `cannot-rescue` — which is why this suite pauses "
                       "before it reads and sends",
               (dig(act_exec, "ok"), dig(act_exec, "data.rejected.0.gate")),
               (True, "cannot-rescue"))
        eq_val("9.6r", "…and the envelope stayed ok:true, because a gate refusal "
                       "is information", dig(act_exec, "ok"), True)
        # work-cover, banked.
        eq_val("9.6s", "banked read 13: the real work-cover promoted one pawn "
                       "and coverage_after agrees the floor is met",
               (dig(cover_real, "data.ok"),
                dig(cover_real, "data.coverage_after.ok"),
                len(dig(cover_real, "data.repaired") or [])), (True, True, 1))
        ge("9.6t", "…and it journalled as an act", cover_real,
           "data.action.journal_seq", 1)
        contains("9.6u", "…citing the game's own promotion order", cover_real,
                 "data.repaired.0.chosen_by", "AverageOfRelevantSkillsFor")
        # 58794e4, banked rather than argued: the dry run's coverage_after is
        # the coverage BEFORE the repair it just described.
        # THE OTHER TWO DEFECTS, PRESERVED FOR THE SAME REASON. Banked pre-fix,
        # kept as the filed evidence for 58794e4 and 6fc75e3; 5.1g2/5.1g3 and
        # 5.2c assert the fixed behaviour live.
        eq_val("9.6v", "the banked PRE-FIX read 12 shows git-bug 58794e4 as "
                       "filed: the dry run names the promotion in `repaired` AND "
                       "reports coverage_after.ok false in the SAME envelope",
               (len(dig(cover_dry, "data.repaired") or []),
                dig(cover_dry, "data.coverage_after.ok")), (1, False))
        eq_val("9.6w", "…and shows git-bug 6fc75e3 beside it: action.journal_seq "
                       "is 0 carrying Stamp(0)'s 'writer is closed' sentence, "
                       "which is not why it is 0 — the guard is "
                       "`repaired>0 && !dryRun`, so nothing was OWED",
               (dig(cover_dry, "data.action.journal_seq"),
                "writer is closed" in (dig(cover_dry, "data.action.provenance") or "")),
               (0, True))

    # ---- 9.7+ every constant this file hard-codes, re-derived from source ----
    src = _src("PawnSerializer.cs")
    if src is None:
        note("9.7", "Source/AutoRimmer/PawnSerializer.cs is not in this checkout; "
                    "HEDIFF_CAP and the two widget thresholds could not be "
                    "re-derived.")
    else:
        m = re.search(r"public const int HediffCap\s*=\s*(\d+)", src)
        eq_val("9.7a", "this file's HEDIFF_CAP matches PawnSerializer.HediffCap",
               int(m.group(1)) if m else None, HEDIFF_CAP)
        m = re.search(r'game_shows_clock"\]\s*=\s*rate\s*>\s*([0-9.]+)f'
                      r'\s*&&\s*!deathless\s*&&\s*ticks\s*<\s*(\d+)', src)
        eq_val("9.7b", "…and the widget's bleed floor matches the shipped "
                       "reproduction", float(m.group(1)) if m else None,
               CARD_BLEED_FLOOR)
        eq_val("9.7c", "…and so does the WontBleedOutSoon boundary",
               int(m.group(2)) if m else None, CARD_WONT_BLEED_SOON)
        # The block's field set, re-derived the way s13 re-derives WorldSafe.Site.
        m = re.search(r"private static Dictionary<string, object> Bleedout\("
                      r"Pawn pawn, Pawn_HealthTracker h\)\s*\{(.*?)\n            \};",
                      src, re.S)
        keys = re.findall(r'\["(\w+)"\]\s*=', m.group(1)) if m else []
        eq_val("9.7d", "…and BLEEDOUT_KEYS matches PawnSerializer.Bleedout's "
                       "dictionary literal", keys, BLEEDOUT_KEYS)

    src = _src("WorkCoverage.cs")
    if src is None:
        note("9.8", "Source/AutoRimmer/WorkCoverage.cs is not in this checkout; "
                    "DOCTOR_FLOOR and WORK_ROW_CAP could not be re-derived.")
    else:
        m = re.search(r"public const int DoctorFloor\s*=\s*(\d+)", src)
        eq_val("9.8a", "this file's DOCTOR_FLOOR matches WorkCoverage.DoctorFloor",
               int(m.group(1)) if m else None, DOCTOR_FLOOR)
        m = re.search(r"private const int RowCap\s*=\s*(\d+)", src)
        eq_val("9.8b", "…and WORK_ROW_CAP matches WorkCoverage.RowCap. THIS IS "
                       "THE CHECK THAT MATTERS: phase 3.6 argues the cap cannot "
                       "fire because the cap exceeds the essential-type count, "
                       "and that argument is only sound while this number is what "
                       "it says", int(m.group(1)) if m else None, WORK_ROW_CAP)
        # …and the FINE row really is three fields in the source, not just on
        # the one bench that happened to be running.
        m = re.search(r"list\.Add\(new Dictionary<string, object>\s*\{\s*"
                      r'\["work"\][^}]*?\}\);\s*\}', src, re.S)
        keys = re.findall(r'\["(\w+)"\]\s*=', m.group(0)) if m else []
        eq_val("9.8c", "…and the FINE row's field list matches FINE_ROW_KEYS",
               keys, FINE_ROW_KEYS)
        # THE SESSION-21 SHAPE FIX, RE-DERIVED. `enabled_but_incapable` used to
        # be emitted only `if (r.Impaired.Count > 0)`, which made an absent key
        # mean both "nobody is impaired" and "this build does not publish it".
        # 3.3q asserts the empty case live; this asserts that the SOURCE cannot
        # quietly go back to conditional, which is the half a live suite cannot
        # see (a fixture with an impaired pawn passes either way — that is
        # exactly how the same omission survived into `work-cover`).
        # The guard is looked for as CODE — a line that STARTS with the `if`,
        # not the string anywhere — because the source comment that records the
        # fix quotes the old guard verbatim, and a substring search would read
        # its own changelog as a regression.
        guard = re.search(r"^\s*if \(r\.Impaired\.Count > 0\)", src, re.M)
        check("9.8d", "WorkCoverage.Section publishes `enabled_but_incapable` "
                      "UNCONDITIONALLY on an under-covered row",
              'd["enabled_but_incapable"] = imp;' in src and not guard,
              "the assignment present and not guarded by "
              "`if (r.Impaired.Count > 0)`",
              "guard still present" if guard else "assignment missing")

    src = _src("TriageVerbs.cs")
    if src is None:
        note("9.9", "Source/AutoRimmer/TriageVerbs.cs is not in this checkout; "
                    "PATH_CANDIDATE_CAP could not be re-derived.")
    else:
        m = re.search(r"private const int PathCandidateCap\s*=\s*(\d+)", src)
        eq_val("9.9a", "this file's PATH_CANDIDATE_CAP matches "
                       "PawnActs.PathCandidateCap",
               int(m.group(1)) if m else None, PATH_CANDIDATE_CAP)
        check("9.9b", "…and triage still routes its clock through "
                      "PawnSerializer.BleedoutBlock, which is what makes 6.3's "
                      "'one builder' claim true",
              "PawnSerializer.BleedoutBlock(p)" in src,
              "BleedoutBlock called from TriageVerbs", None)
        check("9.9c", "…and still gates every candidate through TakeToBedGate, "
                      "which is what makes 6.8 the GAME's refusal and not ours",
              'TakeToBedGate("rescue"' in src,
              "TakeToBedGate called from TriageVerbs", None)

    src = _src("DigestVerb.cs")
    if src is None:
        note("9.10", "Source/AutoRimmer/DigestVerb.cs is not in this checkout.")
    else:
        m = re.search(r"PredicateSections\s*=\s*\{(.*?)\};", src, re.S)
        secs = re.findall(r'"([a-z_]+)"', m.group(1)) if m else []
        check("9.10a", "`work_coverage` is registered as a predicate section, "
                       "which is what phase 7b arms against",
              "work_coverage" in secs, "work_coverage among %s" % (secs,), secs)

    src = _src("PawnManageVerbs.cs")
    if src is None:
        note("9.11", "Source/AutoRimmer/PawnManageVerbs.cs is not in this "
                     "checkout; work-cover's refusal shape could not be "
                     "re-derived.")
    else:
        # The same fix in its second home — and the one acceptance 7.2h caught.
        guard = re.search(r"^\s*if \(r\.Impaired\.Count > 0\)", src, re.M)
        check("9.11a", "`work-cover`'s `still_under` row publishes "
                       "`enabled_but_incapable` UNCONDITIONALLY",
              'why["enabled_but_incapable"] = imp;' in src and not guard,
              "the assignment present and not guarded by "
              "`if (r.Impaired.Count > 0)`",
              "guard still present" if guard else "assignment missing")

    src = _src("JournalHooks.cs")
    if src is None:
        note("9.12", "Source/AutoRimmer/JournalHooks.cs is not in this checkout; "
                     "the transition ruling could not be re-derived.")
    else:
        # THE 2026-09-01 RULING, RE-DERIVED FROM THE SOURCE. 7.5a asserts on a
        # bench that an advance across an ALREADY-downed colonist completes; it
        # only stays true while the casualty journal rows come from POSTFIXES ON
        # THE TRANSITION. A prefix, a poll, or a hook moved onto `Pawn.Downed`
        # would turn the halt into a state halt and wedge a ten-day run on the
        # first casualty nobody can rescue — and 7.5a would then fail, correctly,
        # but only on a bench. This is the same claim, checkable offline.
        check("9.12a", "the casualty journal rows are POSTFIXES on the "
                       "TRANSITIONS `MakeDowned` and `SetDead` — the whole basis "
                       "of the transition-not-state ruling (722c951 #2, DESIGN "
                       "2026-09-01)",
              'nameof(Pawn_HealthTracker.SetDead)' in src
              and 'HarmonyPatch(typeof(Pawn_HealthTracker), "MakeDowned")' in src
              and src.count("public static void Postfix") >= 2,
              "both transitions patched, both as Postfix", None)


# ---------------------------------------------------------------------- main --

PHASES = {1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5,
          6: phase6, 7: phase7, 8: phase8, 9: phase9}
DEFAULT_PHASES = [1, 2, 3, 4, 5, 6, 7, 8]   # 9 never needs a bench; run it alone


def main():
    global ARGS
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--root", default=DEFAULT_ROOT, help="the protocol root")
    p.add_argument("--phase", type=int, action="append",
                   choices=[0, 1, 2, 3, 4, 5, 6, 7, 8, 9],
                   help="run only these phases (repeatable); phase 0 always runs "
                        "unless --selftest")
    p.add_argument("--dry-run", action="store_true",
                   help="print the plan and every expectation, send nothing")
    p.add_argument("--selftest", action="store_true",
                   help="phase 9 only: the suite's own assertions, offline")
    p.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = p.parse_args()

    print("AutoRimmer acceptance - the bleed-out clock (61794cd)")
    print("                        work_coverage / work-cover / triage (40ed42f)")

    if ARGS.selftest:
        print("mode: --selftest (offline; no bench, no game, nothing sent)")
        phase9()
        return summarise(selftest=True)

    print("protocol root: %s" % ARGS.root)
    if not ARGS.dry_run and not os.path.exists(os.path.join(ARGS.root, "status.json")):
        print("%sno status.json under %s - the ORCHESTRATOR starts the bench with "
              "_RimWorld-Agent/run-agent.sh, or pass --root%s"
              % (RED, ARGS.root, OFF))
        sys.exit(2)
    if not ARGS.dry_run:
        os.makedirs(os.path.join(ARGS.root, "commands"), exist_ok=True)

    wanted = sorted(set(ARGS.phase or DEFAULT_PHASES) - {0})
    print("phases: 0 + %s" % ", ".join(str(x) for x in wanted))
    print("%sTHIS SUITE WRECKS THE COLONY IT RUNS ON: it damages, dismembers and "
          "downs colonists on purpose, because that is the fixture. Reload before "
          "running it twice.%s" % (YELLOW, OFF))

    phase0()
    for n in wanted:
        PHASES[n]()
    return summarise()


def summarise(selftest=False):
    print("")
    print("=" * 78)
    if FINDINGS:
        print("%sFINDINGS (shipped-mod defects, reported not asserted — see the "
              "issue comments):%s" % (CYAN, OFF))
        for n, t in FINDINGS:
            print("  %s%-7s %s%s" % (CYAN, n, t, OFF))
        print("")
    if ARGS.dry_run:
        print("%sRESULT: --dry-run printed %d expectations and asserted NONE of "
              "them. Nothing was sent; no dig path was proved. Run it live.%s"
              % (YELLOW, CHECKS, OFF))
        sys.exit(0)
    if FAILS:
        print("%sRESULT: %d FAILED of %d checks - %s%s"
              % (RED, len(FAILS), CHECKS, ", ".join(FAILS), OFF))
        sys.exit(1)
    if selftest:
        print("%sRESULT: all %d self-checks passed. This proves the ASSERTIONS "
              "work, not the mod: no bench was touched.%s" % (GREEN, CHECKS, OFF))
    else:
        print("%sRESULT: all %d checks passed%s" % (GREEN, CHECKS, OFF))
    sys.exit(0)


if __name__ == "__main__":
    main()
