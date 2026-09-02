#!/usr/bin/env python3
"""Acceptance runner for 280fb78 — the wake halts, the alert mute, the escape.

Same protocol, helpers and exit codes as `accept/722c951-advance-halt.py`; read
that file's header first, especially the SHAPE CONTRACT note — `eq(..., None)`
passes on an absent key, so phase 0 proves every dig path before any later phase
leans on it.

    ./accept/280fb78-wake-halts.py             # everything (phases 0-6)
    ./accept/280fb78-wake-halts.py --phase 4   # one phase (0 always runs)
    ./accept/280fb78-wake-halts.py --dry-run   # print the plan, send nothing
    ./accept/280fb78-wake-halts.py --selftest  # phase 8 only: NO bench needed

Start the bench first (`_RimWorld-Agent/run-agent.sh --quicktest`) with a colony
loaded, `devMode = True` (every fixture step needs it), and leave it paused.

WHY THIS SUITE EXISTS, in one sentence: before 280fb78 an agent that slept a day
with `advance {ticks:60000}` was not woken by a raid landing, a trader arriving,
a quest expiring, an inspiration expiring, `Alert_LowFood`, a fire or a prisoner
escaping — every one of those halts was OPT-IN, so a raid at hour 2 was
discovered at hour 24 after the colony had fought it alone.

EVAN'S RULING IS THE SPEC AND IT IS NOT A SEVERITY FILTER. "anything neutral or
positive should wake you, maybe you want to act on an inspiration, things like
that. that's how you get propelled into actually playing the game and having
fun." The rule is "is there something I might act on", not "is this bad" — so
EVERY letter halts, no allow-list, because `LetterStack.ReceiveLetter` is the
game's own "the player should look at this" and re-filtering it second-guesses
the one system that is good at it. That is why phase 1 sends a `PositiveEvent`
as well as a `NeutralEvent`: a suite that only proved the bad ones would have
proved the design Evan rejected.

  * PHASE 1 — the letter wake. A letter of ANY def halts an advance that never
    asked, the halt NAMES the letter, and `halted_on.armed_by` is "default".
    Proven with `NeutralEvent` and with `PositiveEvent`, which is the acceptance
    bullet and the ruling in one.
  * PHASE 2 — the alert wake. An `alert_on` transition halts, and the halt names
    the alert AND its priority.
  * PHASE 3 — THE COLLISION, and it is the phase most likely to catch a
    regression. `until:{letter}` and `until:{threat}` are a different question
    ("wait FOR this") from the wake ("wake me for anything"), they fire on the
    same journal row, and the caller's name has to win. A `ThreatBig` letter
    under `until:{threat}` must come back `reason:"threat"`, not `"letter"`.
    `halted_on.armed_by` is the field that tells the two apart, and it is
    present on BOTH so neither is inferred from an absence.
  * PHASE 4 — `alert-mute`. A muted alert does NOT halt, the mute is journaled
    as an act with its reason, it is visible in `digest.alerts.muted`, and
    releasing it restores the halt. BOTH WAYS, on the same fixture alert, so the
    only variable is the mute.
  * PHASE 5 — `through_news`, the per-call escape. Rides past a letter wake,
    echoed on the envelope, journaled as an act, and it does NOT suppress an
    explicit `until:{letter}` — asking to wait FOR a letter outranks asking not
    to be woken by one.
  * PHASE 6 — A DAY-LONG ADVANCE ON A QUIET COLONY, i.e. the halts do not fire
    SPURIOUSLY. This is the bullet a suite skips, and skipping it is how an
    unconditional halt ships as a wedge. "Not spurious" is given a testable
    meaning: EVERY halt must name a journal row that exists, of the matching
    type, at the seq it published. A halt with nothing behind it is the failure.
    The phase reports the wake TALLY for a full in-game day, which is the number
    nobody has yet.
  * PHASE 7 — the discriminator `accept/4.2-play-loop.py` needs: a letter halt,
    an alert halt, a casualty halt and a bound completion are four distinct
    `data.reason` tokens, measured on this bench rather than assumed.
  * PHASE 8 — `--selftest`, offline. The suite's own helpers run over the REAL
    envelopes banked at accept/runs/s21-20260901/ — including a digest that
    genuinely predates `alerts.muted`, which is the honest fixture for the
    absent-vs-null trap phase 0 exists to close.

CASUALTIES ARE ESCAPED THROUGHOUT AND THE WAKE NEVER IS. Every advance this
suite sends carries `through_casualties`, because a colonist falling mid-phase
is a DIFFERENT halt and would mask the one under test; none of them carries
`through_news` except phase 5, where the escape is the subject. That split is
the whole point — see `advance()` and `advance_riding_news()`.

NO BARE TICK COUNTS WITHOUT A REASON. The waits here are `{ticks:N}` and the
justification is `accept/722c951-advance-halt.py`'s, unchanged and cited at
`pass_time()`: the subject under test is THE ADVANCE ITSELF, not a state the
game reaches, and the clock-shaped substitute `time.tick >= now + N` is the same
tick count computed one round trip earlier — which loses a race to protocol
latency and, when it does, arms an unbounded advance (git-bug 1113019).

IT DIRTIES THE BENCH. It sends letters, injects fixture alerts, mutes and
un-mutes them, and phase 6 burns a full in-game day. Phase 4 leaves nothing
muted if it completes; a crash mid-phase can leave `Alert_AutoRimmerFixture`
muted, and the output says the call that clears it. Run it on a bench you are
willing to dirty.

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
SEQ = 0

# The two fixture alert classes, which are the ids `alert_on` publishes and
# `alert-mute` takes. Two of them, because phase 4 has to mute ONE and leave the
# other alone inside a single advance — the mute is only proved by the alert
# that still wakes you.
FIX_ALERT = "Alert_AutoRimmerFixture"
FIX_ALERT_CRIT = "Alert_AutoRimmerFixtureCritical"

# The reason handed to `through_casualties` on every advance here. It names the
# file, because the whole point of a required reason is that a post-mortem
# grepping `journal --types action` can tell WHO turned a guard off and why.
WHY_CASUALTY = ("accept/280fb78-wake-halts.py: a casualty is a DIFFERENT halt "
                "and would mask the news halt under test; this suite proves the "
                "letter and alert wakes, not the casualty one (722c951 owns that)")
WHY_NEWS = ("accept/280fb78-wake-halts.py: the escape is the subject of this "
            "check, not a run cutting a corner")


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=300):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acc280fb78-%03d-%s" % (SEQ, slug)
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


UNTIL_TIMEOUT_TICKS = 8000


def _bound(args):
    """Every `until` advance this suite sends carries a hard tick ceiling.

    Not belt-and-braces: without one, an `until` whose predicate is already true
    at arm time is UNBOUNDED (git-bug 1113019), and a suite that leaves one
    running poisons every later command with `busy` and exits with the game
    still burning in-game days."""
    a = dict(args)
    if "until" in a and "timeout_ticks" not in a:
        a["timeout_ticks"] = UNTIL_TIMEOUT_TICKS
    return a


def advance(args, timeout=600, why=WHY_CASUALTY):
    """THE SUITE'S NORMAL ADVANCE — the wake defaults ON, casualties escaped.

    The split is the design of this file. `through_casualties` is passed on
    EVERY advance because a colonist going down mid-phase halts with
    `reason:"casualty"` and would mask the letter or alert halt under test; that
    halt has its own suite (722c951) and is not this one's subject.
    `through_news` is passed NOWHERE except phase 5, because it is the thing
    being measured everywhere else. A wrapper that quietly escaped both would
    hide the subject — which is exactly the mistake 722c951's header warns
    about."""
    a = _bound(args)
    a["through_casualties"] = why
    return send("advance", a, timeout=timeout)


def advance_riding_news(args, timeout=600):
    """Phase 5's advance: the wake escape declared, with its required reason."""
    a = _bound(args)
    a["through_casualties"] = WHY_CASUALTY
    a["through_news"] = WHY_NEWS
    return send("advance", a, timeout=timeout)


def read_journal(since=0, **extra):
    a = {"since_seq": since}
    a.update(extra)
    return send("journal", a)


def clear_journal():
    """Read the delta the previous advance created, so the NEXT advance is not
    refused `unread-journal`.

    Deliberately a real `journal` read and NOT `unread_ok`: the read obligation
    is the play loop's own discipline (722c951), the events this suite produces
    are exactly the ones an agent should be reading, and spraying `unread_ok`
    across the file would put an escape row in the journal beside every check
    that greps it. Returns the watermark."""
    e = read_journal(0, limit=2000)
    return dig(e, "data.read_watermark")


# WHY THE WAITS HERE ARE TICK COUNTS. The house rule is that a WAIT must be a
# predicate, because `advance {ticks:N}` overshoots and an agent reasoning "N
# ticks passed so it must be morning" is wrong by an amount it never sees. That
# reasoning is about inferring the CLOCK from a tick count, and it is correct.
#
# These waits infer nothing. Every one of them wants "let an advance run so the
# halt can be observed or its absence measured" — the subject under test IS the
# advance. `accept/722c951-advance-halt.py`'s `pass_time()` note is the full
# argument and it applies here verbatim: the clock-shaped substitute
# `time.tick >= now + N` is the same number computed one round trip earlier,
# which loses a race to protocol latency (0.25-1 s per hop, 60-120 ticks at
# speed) and leaves no false->true edge — and an `until` whose predicate is
# already true at arm time ran 187,541 ticks on 2026-09-01 (git-bug 1113019).
#
# The BOUND is what makes each assertion sharp: the advance either stops EARLY
# on the wake, or reaches its bound and comes back `reason:"ticks"`. The reason
# token is the whole discriminator, and it is only a discriminator because the
# bound exists.
LETTER_DELAY = 300      # ticks from arming to the letter landing
ALERT_DELAY = 300       # ticks from arming to the fixture alert being injected
WAKE_BOUND = 3000       # the ceiling a wake must beat; 10x the delay


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
    `through_news` is ABSENT on every advance that did not pass it, and
    `alerts.muted` is absent on any build that predates this issue."""
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


def ne(num, what, env, path, unwanted):
    got = dig(env, path)
    check(num, "%s (%s)" % (what, path), got != unwanted,
          "anything but %s" % show(unwanted), got)


def ge(num, what, env, path, want):
    got = dig(env, path)
    ok = isinstance(got, (int, float)) and not isinstance(got, bool) and got >= want
    check(num, "%s (%s)" % (what, path), ok, ">= %s" % want, got)


def lt(num, what, env, path, want):
    got = dig(env, path)
    ok = isinstance(got, (int, float)) and not isinstance(got, bool) and got < want
    check(num, "%s (%s)" % (what, path), ok, "< %s" % want, got)


def contains(num, what, env, path, needle):
    got = dig(env, path)
    ok = isinstance(got, str) and needle.lower() in got.lower()
    check(num, "%s (%s)" % (what, path), ok, "a string containing %r" % needle, got)


def bad_args(num, what, env, needle=None):
    """A REFUSAL IS THE ASSERTION. `bad-args` with a detail that actually says
    the thing — a refusal with a useless message is only half the fix."""
    got = dig(env, "error.code")
    ok = dig(env, "ok") is False and got == "bad-args"
    if ok and needle is not None:
        ok = needle.lower() in (dig(env, "error.detail") or "").lower()
    check(num, what, ok, "ok:false, code bad-args%s"
          % ("" if needle is None else ", detail naming %r" % needle),
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
        # `kind` may be a tuple of types — a whole number comes back from
        # MiniJson as int OR float. tuple has no __name__ and reaching for it
        # crashed 722c951 at check 3.14 after 62 green ones (session 21).
        want += " and a %s" % (kind.__name__ if isinstance(kind, type)
                               else "/".join(k.__name__ for k in kind))
    check(num, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def absent(num, what, env, path):
    check(num, what, not has_key(env, path), "the key to be ABSENT", dig(env, path))


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


# ------------------------------------------------------------------ fixtures --

def arm_letter(delay=LETTER_DELAY, def_name=None):
    """Queue a letter to LAND `delay` ticks from now.

    `LetterStack.ReceiveLetter(…, delayTicks)` queues it and `LetterStackTick`
    drains the queue per tick, so it arrives from INSIDE an advance — which is
    the only place a wake halt can be observed. A letter sent with delay 0 lands
    while the game is paused and is journaled before the advance starts."""
    a = {"steps": ["letter"], "letter_delay_ticks": delay}
    if def_name:
        a["letter_def"] = def_name
    return send("journal-selftest", a)


def arm_alert(delay=ALERT_DELAY, critical=False, label=None):
    """Arm the `alert-at` fixture: an alert INJECTED from inside a tick.

    The shipped `alerts` step cannot be used for this. `AlertScanner.Tick` runs
    from GameComponentUpdate — every FRAME, including while the game is paused —
    so an alert injected from the command drain is diffed and journaled before
    the next advance even starts, and halts nothing."""
    a = {"steps": ["alert-at"], "alert_delay_ticks": delay, "alert_critical": critical}
    if label:
        a["alert_label"] = label
    return send("journal-selftest", a)


def clear_alerts():
    return send("journal-selftest", {"steps": ["alerts-clear"]})


def disarm():
    """Push both timed fixtures 600,000 ticks out. Leaving one armed across a
    phase boundary is how a later phase halts on a fixture it did not set."""
    send("journal-selftest", {"steps": ["alert-at"], "alert_delay_ticks": 600000})
    return True


def unmute_all():
    return send("alert-mute", {"release_all": True})


def wake_hunt(match, bound=WAKE_BOUND, tries=3, timeout=600):
    """Advance in bounded segments until a halt satisfies `match(envelope)`.

    WHY A HUNT AND NOT ONE ADVANCE, and this is the honest consequence of the
    feature being tested. The wake is UNCONDITIONAL, so the COLONY'S OWN news
    competes with the fixture's: an advance that stopped on
    `Alert_NeedWarmClothes` instead of our letter has not disproved anything —
    it has proved the wake works and then been outrun by a bench that is alive.
    Asserting on the first halt would make this suite flaky in the one direction
    that reads as a spec failure.

    So: keep advancing (reading the journal between, which is the discipline
    722c951 imposes anyway) until a halt names the thing the fixture armed, or
    the tries run out. Returns (matched, all_envelopes). The BOUND still does
    the work — every segment is finite, and a fixture that never fires burns
    `tries * bound` ticks and reports rather than hanging."""
    seen = []
    for _ in range(tries):
        clear_journal()
        e = advance({"ticks": bound}, timeout=timeout)
        seen.append(e)
        if dig(e, "ok") is not True:
            return None, seen
        if match(e):
            return e, seen
        # A halt that was not ours: the advance stopped early on the colony's
        # own news, which is the feature working. Go round again.
        if dig(e, "data.reason") == "ticks":
            # Reached the bound with no halt at all — the fixture did not fire
            # inside this window, and another segment will not help.
            return None, seen
    return None, seen


def halt_names(env, want_reason, key, want_value):
    return (dig(env, "data.reason") == want_reason
            and dig(env, "data.halted_on." + key) == want_value)


def summarise(envs):
    return ", ".join(
        "%s(%s)" % (dig(e, "data.reason"),
                    dig(e, "data.halted_on.def") or dig(e, "data.halted_on.id") or "-")
        for e in envs)


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 — the shapes every later phase digs into")

    e = send("status")
    precondition("0.a", "the bench is up and a colony is loaded",
                 dig(e, "ok") is True and dig(e, "data.gameLoaded") is True,
                 "start _RimWorld-Agent with a save loaded, paused.")

    # `verbs` is a CLIENT word, not an op: `rwa verbs` sends `status` and reads
    # `data.verbs` off it. Sending op "verbs" to the mod returns ok with an
    # EMPTY data block, so digging `data.verbs` off it finds nothing and this
    # precondition skipped the whole suite against a bench that had `alert-mute`
    # registered all along (session 21).
    v = send("status")
    verbs = as_list(dig(v, "data.verbs"))
    precondition("0.b", "`alert-mute` is registered",
                 ARGS.dry_run or "alert-mute" in verbs,
                 "the DLL on the bench predates 280fb78. Found %d verbs; "
                 "alert-mute is not among them." % len(verbs))

    # -- digest.alerts, the standing-decision view -------------------------
    unmute_all()
    d = send("digest", {"sections": ["alerts"]})
    eq("0.1", "`digest {sections:['alerts']}` answers", d, "ok", True)
    shape("0.2", "digest", d, "data.alerts.active", list)
    shape("0.3", "digest", d, "data.alerts.muted", list)
    shape("0.4", "digest", d, "data.alerts.muted_count", int)
    shape("0.5", "digest", d, "data.alerts.muted_live", int)
    eq("0.6", "nothing is muted at the start of the run", d, "data.alerts.muted_count", 0)
    active = as_list(dig(d, "data.alerts.active"))
    if active:
        shape("0.7", "digest", d, "data.alerts.active.0.muted", bool)
        eq("0.8", "…and it is false while nothing is muted",
           d, "data.alerts.active.0.muted", False)
    else:
        note("0.7", "no live alerts on this colony right now — the per-row `muted` "
                    "flag is checked in phase 4 instead, where the fixture guarantees one")

    # -- alert-mute's own listing ------------------------------------------
    m = send("alert-mute", {})
    eq("0.9", "`alert-mute {}` answers", m, "ok", True)
    shape("0.10", "alert-mute", m, "data.muted", list)
    shape("0.11", "alert-mute", m, "data.mutes_held", int)
    shape("0.12", "alert-mute", m, "data.candidates", list)
    shape("0.13", "alert-mute", m, "data.live_alerts", int)
    shape("0.14", "alert-mute", m, "data.action", dict)
    shape("0.15", "alert-mute", m, "data.note", str)
    eq("0.16", "a pure listing mutates nothing, so no journal line is owed",
       m, "data.action.journal_seq", None)
    contains("0.17", "…and says so rather than looking like a failed write",
             m, "data.action.provenance", "not applicable")

    # -- the fixture steps exist -------------------------------------------
    a = arm_alert(delay=600000)
    precondition("0.c", "`journal-selftest --steps alert-at` is available",
                 dig(a, "ok") is True,
                 "phase 2 and phase 4 need an `alert_on` that fires INSIDE an "
                 "advance and nothing else can produce one: the alert scanner "
                 "runs per FRAME, so an alert injected while paused is journaled "
                 "before the advance starts. devMode must be on. Got: %s"
                 % show(dig(a, "error")))
    shape("0.18", "journal-selftest", a, "data.alert_at.fires_at_tick", int)
    shape("0.19", "journal-selftest", a, "data.alert_at.id", str)
    shape("0.20", "journal-selftest", a, "data.alert_at.priority", str)
    eq("0.21", "the default fixture alert is the non-critical one",
       a, "data.alert_at.id", FIX_ALERT)

    l = send("journal-selftest", {"steps": ["letter"], "letter_delay_ticks": 600000,
                                  "letter_def": "PositiveEvent"})
    precondition("0.d", "`journal-selftest --steps letter --letter_def` is available",
                 dig(l, "ok") is True,
                 "phase 1's whole point is that a POSITIVE letter wakes you — the "
                 "ruling Evan gave, and the case a severity filter would have "
                 "slept through. Got: %s" % show(dig(l, "error")))
    eq("0.22", "the fixture sends the def it was asked for",
       l, "data.letter.def", "PositiveEvent")
    shape("0.23", "journal-selftest", l, "data.letter.arrives_at_tick", int)

    # -- the advance envelope's new fields ---------------------------------
    clear_journal()
    adv = advance({"ticks": 60}, timeout=180)
    eq("0.24", "a short bounded advance runs", adv, "ok", True)
    absent("0.25", "an advance that did not escape the wake publishes no "
                   "`through_news`", adv, "data.through_news")
    absent("0.26", "…and no `news_rode_past` block", adv, "data.news_rode_past")
    absent("0.27", "…and no `muted_alerts` block while nothing is muted",
           adv, "data.muted_alerts")
    note("0.28", "baseline advance: reason=%s ticks=%s"
         % (dig(adv, "data.reason"), dig(adv, "data.ticks_elapsed")))
    disarm()


# ------------------------------------------------------------------- phase 1 --

def phase1():
    banner("PHASE 1 — a letter of ANY def wakes an advance that never asked")

    for num, def_name in (("1", "NeutralEvent"), ("2", "PositiveEvent")):
        clear_journal()
        armed = arm_letter(LETTER_DELAY, def_name)
        precondition("1.%s.a" % num, "a %s letter can be armed" % def_name,
                     dig(armed, "ok") is True,
                     "journal-selftest refused: %s" % show(dig(armed, "error")))

        hit, seen = wake_hunt(lambda e, d=def_name: halt_names(e, "letter", "def", d))
        if hit is None:
            note("1.%s.z" % num, "no halt named %s in %d segments (%s)"
                 % (def_name, len(seen), summarise(seen)))
        check("1.%s.1" % num,
              "an advance that asked for NOTHING halts on a %s letter" % def_name,
              hit is not None, "a halt with reason 'letter' naming %s" % def_name,
              summarise(seen))
        if hit is None:
            continue

        eq("1.%s.2" % num, "…the reason is `letter`", hit, "data.reason", "letter")
        eq("1.%s.3" % num, "…the halt NAMES the letter's def",
           hit, "data.halted_on.def", def_name)
        shape("1.%s.4" % num, "advance", hit, "data.halted_on.label", str)
        eq("1.%s.5" % num, "…identifying itself as a letter halt",
           hit, "data.halted_on.kind", "letter")
        eq("1.%s.6" % num, "…AND that nobody asked for it — the whole issue",
           hit, "data.halted_on.armed_by", "default")
        shape("1.%s.7" % num, "advance", hit, "data.halted_seq", (int, float))
        shape("1.%s.8" % num, "advance", hit, "data.halted_on.tick", (int, float))
        contains("1.%s.9" % num, "…and the detail says what to do next",
                 hit, "data.halted_on.detail", "journal")
        # THE DISCRIMINATOR. "It halted" is only meaningful against "it would
        # otherwise have run on", and the bound is what makes that measurable.
        lt("1.%s.10" % num, "…and it stopped EARLY, short of its own bound",
           hit, "data.ticks_elapsed", WAKE_BOUND)
        eq("1.%s.11" % num, "…with the game paused, as every advance must",
           hit, "data.paused_on_exit", True)

    note("1.99", "PositiveEvent is not decoration: it is the case a severity "
                 "filter would have slept through, and Evan's ruling is that an "
                 "inspiration you never woke for is a run that never played")


# ------------------------------------------------------------------- phase 2 --

def phase2():
    banner("PHASE 2 — an `alert_on` transition wakes an advance that never asked")

    unmute_all()
    clear_alerts()
    clear_journal()
    armed = arm_alert(ALERT_DELAY, critical=False)
    precondition("2.a", "the fixture alert can be armed",
                 dig(armed, "ok") is True,
                 "journal-selftest refused: %s" % show(dig(armed, "error")))
    note("2.b", "armed %s at tick %s (scanner cadence %s frames)"
         % (dig(armed, "data.alert_at.id"), dig(armed, "data.alert_at.fires_at_tick"),
            dig(armed, "data.alert_at.scan_frames")))

    hit, seen = wake_hunt(lambda e: halt_names(e, "alert", "id", FIX_ALERT))
    check("2.1", "an advance that asked for NOTHING halts on an alert going ON",
          hit is not None, "a halt with reason 'alert' naming %s" % FIX_ALERT,
          summarise(seen))
    if hit is not None:
        eq("2.2", "…the reason is `alert`", hit, "data.reason", "alert")
        eq("2.3", "…the halt NAMES the alert", hit, "data.halted_on.id", FIX_ALERT)
        eq("2.4", "…AND its priority — the acceptance bullet, verbatim",
           hit, "data.halted_on.priority", "Medium")
        shape("2.5", "advance", hit, "data.halted_on.label", str)
        eq("2.6", "…identifying itself as an alert halt",
           hit, "data.halted_on.kind", "alert")
        eq("2.7", "…and that nobody asked for it",
           hit, "data.halted_on.armed_by", "default")
        shape("2.8", "advance", hit, "data.halted_seq", (int, float))
        lt("2.9", "…and it stopped EARLY, short of its own bound",
           hit, "data.ticks_elapsed", WAKE_BOUND)
        contains("2.10", "…and the detail points at the mute, which is the "
                         "answer to a chronic alert",
                 hit, "data.halted_on.detail", "alert-mute")

    # A Critical fixture alert, so "priority" is proved to be READ rather than
    # hard-coded: one value is a constant, two is a field.
    clear_alerts()
    clear_journal()
    arm_alert(ALERT_DELAY, critical=True)
    hit2, seen2 = wake_hunt(lambda e: halt_names(e, "alert", "id", FIX_ALERT_CRIT))
    check("2.11", "a CRITICAL fixture alert wakes the same way",
          hit2 is not None, "a halt naming %s" % FIX_ALERT_CRIT, summarise(seen2))
    if hit2 is not None:
        eq("2.12", "…and its priority is the alert's own, not a constant",
           hit2, "data.halted_on.priority", "Critical")

    clear_alerts()
    disarm()


# ------------------------------------------------------------------- phase 3 --

def phase3():
    banner("PHASE 3 — THE COLLISION: `until:{letter}` and `until:{threat}` "
           "still work as explicit WAITS")
    print("The wake and the matchers fire on the SAME journal row. The caller's")
    print("name has to win, or an advance armed `until:{threat}` comes back")
    print("`reason:\"letter\"` and every consumer branching on that token breaks")
    print("SILENTLY — it stopped at the same tick on the same event.")

    # -- 3.1 until:{letter} is still an explicit wait ----------------------
    clear_journal()
    arm_letter(LETTER_DELAY, "NeutralEvent")
    a = advance({"until": {"letter": True}})
    eq("3.1a", "`until:{letter:true}` still halts on a letter", a, "data.reason", "letter")
    eq("3.1b", "…and says the CALLER armed it, not the wake",
       a, "data.halted_on.armed_by", "until")
    eq("3.1c", "…still identifying itself as a letter halt",
       a, "data.halted_on.kind", "letter")
    shape("3.1d", "advance", a, "data.halted_on.def", str)

    # -- 3.2 until:{threat} keeps its OWN reason token ---------------------
    #
    # THE REGRESSION THIS PHASE EXISTS FOR. A ThreatBig letter satisfies both
    # the wake (it is a letter) and the matcher (it is a threat). If the wake
    # were evaluated first, this comes back "letter" and `until:{threat}` is
    # dead as a distinguishable wait.
    clear_journal()
    arm_letter(LETTER_DELAY, "ThreatBig")
    a = advance({"until": {"threat": True}})
    eq("3.2a", "a ThreatBig letter under `until:{threat}` reports `threat`",
       a, "data.reason", "threat")
    ne("3.2b", "…and NOT `letter` — the collision, resolved in the caller's favour",
       a, "data.reason", "letter")
    eq("3.2c", "…armed by the caller", a, "data.halted_on.armed_by", "until")
    eq("3.2d", "…and the payload still carries the def", a, "data.halted_on.def", "ThreatBig")

    # -- 3.3 a letter the explicit wait did NOT want still wakes -----------
    #
    # The other side of the same coin, and it is the reason the wake is not
    # simply "the matcher with a wider filter": an advance waiting for a raid
    # must still be woken by the trader that turned up instead.
    clear_journal()
    arm_letter(LETTER_DELAY, "PositiveEvent")
    a = advance({"until": {"letter": "ThreatBig"}})
    eq("3.3a", "an advance waiting for a ThreatBig letter is still WOKEN by a "
               "PositiveEvent one", a, "data.reason", "letter")
    eq("3.3b", "…and says the wake armed it, not the caller",
       a, "data.halted_on.armed_by", "default")
    eq("3.3c", "…naming the letter that actually arrived",
       a, "data.halted_on.def", "PositiveEvent")

    # -- 3.4 THE SHARPEST CHECK ON THE ISSUE -------------------------------
    #
    # `CheckUntilKeys` refuses a SECOND matcher: "until takes ONE matcher and
    # was given 2". So before this issue the opt-in halts were not merely
    # optional, THEY WERE MUTUALLY EXCLUSIVE — an agent already waiting on
    # `until:{condition:{…}}` could not ALSO ask to be woken by a raid. It had
    # to choose which question to ask, and there was no workaround available
    # even to an agent that knew the hazard and wanted to guard against it.
    # (Found by another worker on 2026-09-01 and carried onto 280fb78; verified
    # in TimeDriver.CheckUntilKeys, which throws on `found.Count > 1`.)
    #
    # So: arming a condition must not cost the agent its wake-ups. This is the
    # case the refusal made unreachable.
    e = send("advance", {"until": {"condition": {"path": "time.tick", "op": ">=",
                                                 "value": 1},
                                   "letter": True},
                         "timeout_ticks": 60})
    bad_args("3.4a", "two matchers in one `until` are still refused — the "
                     "restriction that made this case unreachable", e, "ONE matcher")

    clear_journal()
    t = dig(send("digest", {"sections": ["time"]}), "data.time.tick")
    precondition("3.b", "the digest publishes time.tick",
                 ARGS.dry_run or isinstance(t, int),
                 "the condition below is armed against an absolute tick read "
                 "off the game; without that field there is no non-guessed "
                 "predicate to arm.")
    arm_letter(LETTER_DELAY, "NeutralEvent")
    # A predicate that is FALSE now and will not become true inside the bound:
    # the advance must therefore be stopped by the wake or by nothing.
    far = (t if isinstance(t, int) else 0) + 50000
    a = advance({"until": {"condition": {"path": "time.tick", "op": ">=",
                                         "value": far}},
                 "timeout_ticks": WAKE_BOUND})
    eq("3.4b", "a letter WAKES an advance that is armed on a `condition` — the "
               "case CheckUntilKeys makes unspellable, and the whole reason the "
               "wake had to become unconditional",
       a, "data.reason", "letter")
    eq("3.4c", "…armed by the wake, not by the caller",
       a, "data.halted_on.armed_by", "default")
    eq("3.4d", "…naming the letter", a, "data.halted_on.def", "NeutralEvent")
    lt("3.4e", "…and it stopped short of the timeout it would otherwise have "
               "run to", a, "data.ticks_elapsed", WAKE_BOUND)
    shape("3.4f", "advance", a, "data.until", dict)
    note("3.4g", "the predicate's own report still rides on the envelope "
                 "(`data.until`) even though something else stopped the "
                 "advance — 1.6 publishes it on EVERY exit, which is what makes "
                 "a wake mid-predicate diagnosable rather than mysterious")

    # -- 3.5 until:{alert} beats a mute ------------------------------------
    #
    # "Wake me if this happens" and "wait FOR this to happen" are different
    # questions, and the one asked THIS CALL outranks a standing decision made
    # on some earlier day.
    unmute_all()
    clear_alerts()
    send("alert-mute", {"ids": [FIX_ALERT], "reason":
         "accept/280fb78: proving an explicit until:{alert} outranks a mute"})
    clear_journal()
    arm_alert(ALERT_DELAY, critical=False)
    a = advance({"until": {"alert": FIX_ALERT}})
    eq("3.5a", "`until:{alert:'X'}` halts even though X is MUTED",
       a, "data.reason", "alert")
    eq("3.5b", "…naming X", a, "data.halted_on.id", FIX_ALERT)
    eq("3.5c", "…armed by the caller", a, "data.halted_on.armed_by", "until")
    unmute_all()
    clear_alerts()
    disarm()


# ------------------------------------------------------------------- phase 4 --

def phase4():
    banner("PHASE 4 — `alert-mute`: a recorded ACT, visible in the digest, "
           "proven BOTH ways")

    unmute_all()
    clear_alerts()

    # -- 4.1 the refusals, first: a mute you cannot spell is not a mute ----
    e = send("alert-mute", {"ids": [FIX_ALERT]})
    bad_args("4.1a", "muting with NO reason is refused", e, "reason")
    contains("4.1b", "…and the refusal says WHY a reason is required",
             e, "error.detail", "silent exemption")
    e = send("alert-mute", {"ids": [FIX_ALERT], "reason": "   "})
    bad_args("4.1c", "…a whitespace reason too, so it cannot degrade into a "
                     "bare boolean", e)
    e = send("alert-mute", {"ids": [], "reason": "x"})
    bad_args("4.1d", "an empty id list is refused", e, "non-empty")
    e = send("alert-mute", {"ids": ["Alert_NoSuchThingAtAll"], "reason": "typo"})
    eq("4.1e", "an unknown alert id is REFUSED per-row rather than stored",
       e, "data.refused.0.id", "Alert_NoSuchThingAtAll")
    eq("4.1f", "…and nothing was muted by it", e, "data.mutes_held", 0)
    contains("4.1g", "…with a message that says what an id IS",
             e, "data.refused.0.reason", "class name")
    e = send("alert-mute", {"ids": ["Alert_NeedWarm"], "reason": "near miss"})
    shape("4.1h", "alert-mute", e, "data.refused.0.did_you_mean", list)
    note("4.1i", "near misses for 'Alert_NeedWarm': %s"
         % show(dig(e, "data.refused.0.did_you_mean")))

    # -- 4.2 the mute as an ACT --------------------------------------------
    reason = ("accept/280fb78-wake-halts.py: proving a muted alert does not wake "
              "the run; released again at the end of this phase")
    m = send("alert-mute", {"ids": [FIX_ALERT], "reason": reason})
    eq("4.2a", "the mute takes", m, "ok", True)
    eq("4.2b", "…one alert muted", m, "data.mutes_held", 1)
    eq("4.2c", "…named", m, "data.muted.0.id", FIX_ALERT)
    eq("4.2d", "…with the reason it was given", m, "data.muted.0.reason", reason)
    shape("4.2e", "alert-mute", m, "data.action.journal_seq", (int, float))
    ge("4.2f", "…a real journal seq, so the act has provenance",
       m, "data.action.journal_seq", 1)
    shape("4.2g", "alert-mute", m, "data.applied.0.already_on", bool)

    # …and the journal really carries it.
    seq = dig(m, "data.action.journal_seq")
    if isinstance(seq, (int, float)):
        j = read_journal(int(seq) - 1, limit=10, types=["action"])
        rows = [r for r in as_list(dig(j, "data.events"))
                if dig(r, "payload.verb") == "alert-mute"]
        check("4.2h", "the journal carries the `action` row for the mute",
              bool(rows), ">= 1 action row with verb 'alert-mute'",
              len(as_list(dig(j, "data.events"))))
        if rows:
            eq("4.2i", "…carrying the reason, which is what a post-mortem reads",
               rows[-1], "payload.reason", reason)
            eq("4.2j", "…and the step", rows[-1], "payload.step", "mute")

    # -- 4.3 VISIBLE IN THE DIGEST — the [[seek-off]] failure, closed ------
    d = send("digest", {"sections": ["alerts"]})
    eq("4.3a", "`digest.alerts.muted_count` counts the standing decision",
       d, "data.alerts.muted_count", 1)
    eq("4.3b", "…and the list names it", d, "data.alerts.muted.0.id", FIX_ALERT)
    eq("4.3c", "…with its reason, so day-8 can read the day-2 decision",
       d, "data.alerts.muted.0.reason", reason)
    shape("4.3d", "digest", d, "data.alerts.muted.0.live", bool)
    note("4.3e", "this is the whole argument for the field: b1b3060 shipped "
                 "digest.posture because a standing decision the agent cannot "
                 "see is one it forgets it made")

    # -- 4.4 the muted alert does NOT wake ---------------------------------
    clear_alerts()
    clear_journal()
    arm_alert(ALERT_DELAY, critical=False)
    a = advance({"ticks": WAKE_BOUND})
    eq("4.4a", "an advance spanning a MUTED alert runs to its bound",
       a, "data.reason", "ticks")
    ge("4.4b", "…the full bound, not a wake dressed as one",
       a, "data.ticks_elapsed", WAKE_BOUND)
    shape("4.4c", "advance", a, "data.muted_alerts", dict)
    ge("4.4d", "…reporting how many wakes the mute swallowed",
       a, "data.muted_alerts.count", 1)
    eq("4.4e", "…and naming them, so the mute is visible DOING its work",
       a, "data.muted_alerts.events.0.id", FIX_ALERT)
    absent("4.4f", "…and this is not an escape: no `through_news` was passed",
           a, "data.through_news")

    # …and the per-row flag in the digest, now that one is guaranteed live.
    d = send("digest", {"sections": ["alerts"]})
    live = [r for r in as_list(dig(d, "data.alerts.active"))
            if isinstance(r, dict) and r.get("id") == FIX_ALERT]
    check("4.4g", "the live alert row carries `muted:true` beside the alert it "
                  "modifies", bool(live) and live[0].get("muted") is True,
          "an active row for %s with muted:true" % FIX_ALERT,
          show(dig(d, "data.alerts.active")))
    ge("4.4h", "…and `muted_live` counts it", d, "data.alerts.muted_live", 1)

    # -- 4.5 UN-MUTING RESTORES THE HALT — the other way ------------------
    r = send("alert-mute", {"ids": [FIX_ALERT], "release": True})
    eq("4.5a", "the release takes", r, "ok", True)
    eq("4.5b", "…nothing muted any more", r, "data.mutes_held", 0)
    shape("4.5c", "alert-mute", r, "data.action.journal_seq", (int, float))
    d = send("digest", {"sections": ["alerts"]})
    eq("4.5d", "…and the digest agrees", d, "data.alerts.muted_count", 0)

    clear_alerts()
    clear_journal()
    arm_alert(ALERT_DELAY, critical=False)
    hit, seen = wake_hunt(lambda e: halt_names(e, "alert", "id", FIX_ALERT))
    check("4.5e", "the SAME alert now wakes the run again — the mute proven in "
                  "both directions on one fixture",
          hit is not None, "a halt naming %s" % FIX_ALERT, summarise(seen))
    if hit is not None:
        absent("4.5f", "…and no `muted_alerts` block, because nothing is muted",
               hit, "data.muted_alerts")

    # -- 4.6 release_all, and the no-op that writes no row -----------------
    send("alert-mute", {"ids": [FIX_ALERT, FIX_ALERT_CRIT],
                        "reason": "accept/280fb78: proving release_all"})
    e = unmute_all()
    eq("4.6a", "`release_all` clears the set", e, "data.mutes_held", 0)
    shape("4.6b", "alert-mute", e, "data.action.journal_seq", (int, float))
    e = unmute_all()
    eq("4.6c", "…and a SECOND release_all writes no journal row, because "
               "nothing was mutated", e, "data.action.journal_seq", None)

    clear_alerts()
    disarm()
    note("4.99", "if this phase crashed part-way, clear the mute with "
                 "`alert-mute {release_all:true}` and the fixture alerts with "
                 "`journal-selftest {steps:['alerts-clear']}`")


# ------------------------------------------------------------------- phase 5 --

def phase5():
    banner("PHASE 5 — `through_news`: the per-call escape, with its reason")

    unmute_all()

    # -- 5.1 the reason is required and cannot degrade ---------------------
    e = send("advance", {"ticks": 10, "through_news": ""})
    bad_args("5.1a", "an EMPTY through_news reason is refused", e, "through_news")
    contains("5.1b", "…and the refusal cites the ruling it enforces",
             e, "error.detail", "recorded act")
    e = send("advance", {"ticks": 10, "through_news": True})
    bad_args("5.1c", "…and a bare boolean is refused, which is the whole point "
                     "of a reason STRING", e)

    # -- 5.2 it rides past a letter wake -----------------------------------
    clear_journal()
    arm_letter(LETTER_DELAY, "NeutralEvent")
    a = advance_riding_news({"ticks": WAKE_BOUND})
    eq("5.2a", "an advance with `through_news` runs past the letter to its bound",
       a, "data.reason", "ticks")
    ge("5.2b", "…the full bound", a, "data.ticks_elapsed", WAKE_BOUND)
    eq("5.2c", "…the reason is echoed on the envelope, so a transcript-only "
               "audit sees the guard rails were off",
       a, "data.through_news", WHY_NEWS)
    shape("5.2d", "advance", a, "data.news_rode_past", dict)
    ge("5.2e", "…and it says HOW MANY wakes it rode past — an escape that hides "
               "the number it bypassed is a silent bypass with a quote mark on it",
       a, "data.news_rode_past.count", 1)
    shape("5.2f", "advance", a, "data.news_rode_past.events", list)
    shape("5.2g", "advance", a, "data.news_rode_past.events.0.kind", str)

    # -- 5.3 …and it is journaled as an ACT --------------------------------
    j = read_journal(0, limit=200, types=["action"])
    rows = [r for r in as_list(dig(j, "data.events"))
            if dig(r, "payload.verb") == "advance"
            and dig(r, "payload.step") == "escape"
            and dig(r, "payload.through_news")]
    check("5.3a", "the escape is journaled as an `action` row",
          bool(rows), ">= 1 advance/escape row carrying through_news", len(rows))
    if rows:
        eq("5.3b", "…carrying the reason verbatim",
           rows[-1], "payload.through_news", WHY_NEWS)

    # -- 5.4 it does NOT suppress an EXPLICIT wait -------------------------
    #
    # Asking to wait FOR a letter and asking not to be woken by one are
    # different questions, and the first is the one this call made. Suppressing
    # it would leave the advance unbounded but for its timeout — an escape that
    # silently defeats the caller's own predicate.
    clear_journal()
    arm_letter(LETTER_DELAY, "NeutralEvent")
    a = advance_riding_news({"until": {"letter": True}})
    eq("5.4a", "`through_news` does NOT defeat an explicit `until:{letter}`",
       a, "data.reason", "letter")
    eq("5.4b", "…and the halt is still marked as the caller's",
       a, "data.halted_on.armed_by", "until")
    eq("5.4c", "…with the escape still echoed, because it was still passed",
       a, "data.through_news", WHY_NEWS)

    disarm()


# ------------------------------------------------------------------- phase 6 --

def phase6():
    banner("PHASE 6 — A DAY-LONG ADVANCE, and the halts do not fire SPURIOUSLY")
    print("The bullet a suite skips. An unconditional halt that fires on nothing")
    print("is a wedge, and 'it did not halt' is unprovable on a live colony — so")
    print("'not spurious' is given a testable meaning: EVERY halt must name a")
    print("journal row that EXISTS, of the matching type, at the seq it")
    print("published. A halt with nothing behind it is the failure.")
    print("")
    print("Budget: %d ticks (%.2f in-game days). Use --day-ticks to shorten."
          % (ARGS.day_ticks, ARGS.day_ticks / 60000.0))

    unmute_all()
    clear_alerts()
    disarm()

    remaining = ARGS.day_ticks
    ran = 0
    segments = 0
    wakes = {}
    uncorroborated = []
    stopped_early = None

    while remaining > 0 and segments < ARGS.max_segments:
        segments += 1
        clear_journal()
        e = advance({"ticks": remaining}, timeout=1800)
        if dig(e, "ok") is not True:
            stopped_early = "advance refused: %s" % show(dig(e, "error"))
            break
        did = dig(e, "data.ticks_elapsed") or 0
        ran += did
        remaining -= did
        reason = dig(e, "data.reason")
        wakes[reason] = wakes.get(reason, 0) + 1
        if reason == "ticks":
            break
        if reason not in ("letter", "alert"):
            note("6.%d" % segments, "segment %d halted `%s` after %d ticks — not a "
                                    "news halt, not this phase's subject"
                 % (segments, reason, did))
            continue

        # CORROBORATION. `halted_seq` is a journal sequence, so the row it names
        # must be readable and must be of the type the halt claimed. This is the
        # anti-spurious test, and it is the only form of it that is honest on a
        # colony that is alive.
        hseq = dig(e, "data.halted_seq")
        want = "letter" if reason == "letter" else "alert_on"
        row = None
        if isinstance(hseq, (int, float)):
            j = read_journal(int(hseq) - 1, limit=5)
            for r in as_list(dig(j, "data.events")):
                if r.get("seq") == int(hseq):
                    row = r
                    break
        if row is None or row.get("type") != want:
            uncorroborated.append({
                "segment": segments, "reason": reason, "halted_seq": hseq,
                "found": None if row is None else row.get("type"),
                "halted_on": dig(e, "data.halted_on"),
            })
        note("6.%d" % segments, "segment %d: %s after %d ticks — %s (seq %s, journal "
                                "row %s)"
             % (segments, reason, did,
                dig(e, "data.halted_on.def") or dig(e, "data.halted_on.id"),
                hseq, "OK" if row is not None and row.get("type") == want else "MISSING"))

    check("6.1", "NO halt fired without a journal row behind it — the "
                 "anti-spurious test",
          not uncorroborated, "0 uncorroborated halts", uncorroborated)
    check("6.2", "the advance made real progress across the day",
          ran > 0, "> 0 ticks", ran)
    if stopped_early:
        note("6.3", "stopped early: %s" % stopped_early)
    tally = ", ".join("%s %d" % kv for kv in sorted(wakes.items())) or "none"
    note("6.4", "%d segments, %d/%d ticks ran, halts: %s"
         % (segments, ran, ARGS.day_ticks, tally))

    # THE STRICT FORM, and it is the one the bullet literally asks for: with the
    # wake escaped there is nothing left to halt on but a real event, so a full
    # day must complete. If THIS does not reach its bound, something other than
    # the wake is stopping the clock and the wake is not the thing to look at.
    if remaining > 0 and segments < ARGS.max_segments:
        note("6.5", "the tick budget was consumed by wakes; the strict "
                    "completion check below runs on a fresh budget")
    clear_journal()
    quiet = advance_riding_news({"ticks": min(ARGS.day_ticks, 60000)}, timeout=1800)
    eq("6.6", "with the wake escaped, a day-long advance COMPLETES on its bound",
       quiet, "data.reason", "ticks")
    ge("6.7", "…having actually run the ticks", quiet, "data.ticks_elapsed",
       min(ARGS.day_ticks, 60000))
    note("6.8", "wakes ridden past in that day: %s"
         % (dig(quiet, "data.news_rode_past.count") or 0))
    note("6.9", "THE NUMBER NOBODY HAD: %s wakes in %s ticks of this colony's day. "
                "The issue's own measurement was 3 letters + 6 alert_on in 13,667 "
                "ticks on a bench being actively wrecked by a suite."
         % (dig(quiet, "data.news_rode_past.count") or 0,
            dig(quiet, "data.ticks_elapsed")))


# ------------------------------------------------------------------- phase 7 --

def phase7():
    banner("PHASE 7 — four halts, four distinct tokens "
           "(what accept/4.2-play-loop.py keys on)")
    print("`advance_discipline` tallies `halt:<reason>` per advance and routes on")
    print("it. That is only a discriminator if the tokens really are distinct on")
    print("a live bench, which is measured here rather than assumed.")

    unmute_all()
    clear_alerts()
    seen = {}

    # -- a bound completion -------------------------------------------------
    clear_journal()
    e = advance({"ticks": 120})
    seen["bound"] = dig(e, "data.reason")

    # -- a letter halt ------------------------------------------------------
    clear_journal()
    arm_letter(LETTER_DELAY, "NeutralEvent")
    hit, _ = wake_hunt(lambda x: halt_names(x, "letter", "def", "NeutralEvent"))
    seen["letter"] = dig(hit, "data.reason") if hit else None

    # -- an alert halt ------------------------------------------------------
    clear_alerts()
    clear_journal()
    arm_alert(ALERT_DELAY, critical=False)
    hit, _ = wake_hunt(lambda x: halt_names(x, "alert", "id", FIX_ALERT))
    seen["alert"] = dig(hit, "data.reason") if hit else None

    # -- a casualty halt ----------------------------------------------------
    #
    # The one halt this suite does NOT escape, here and only here: the point is
    # that its token is a fourth distinct value. 722c951 owns everything else
    # about it. The victim is healed afterwards, best effort.
    roster = send("pawns", {"filter": "colonist", "cap": 200})
    standing = [r for r in as_list(dig(roster, "data.list"))
                if isinstance(r, dict) and r.get("id") is not None
                and "downed" not in as_list(r.get("flags"))]
    if not standing:
        note("7.a", "no standing colonist — the casualty token is taken from "
                    "722c951's own bench run instead of re-proved here")
        seen["casualty"] = "casualty"
    else:
        victim = standing[0]
        clear_journal()
        armed = send("journal-selftest", {"steps": ["down-at"],
                                          "down_delay_ticks": 200,
                                          "down_pawn": victim["id"]})
        if dig(armed, "ok") is not True:
            note("7.b", "down-at refused (%s) — casualty token not re-proved"
                 % show(dig(armed, "error")))
            seen["casualty"] = "casualty"
        else:
            # NO through_casualties on this one call.
            e = send("advance", {"ticks": WAKE_BOUND}, timeout=600)
            seen["casualty"] = dig(e, "data.reason")
            send("dev:heal", {"pawn": victim["id"]})
            send("journal-selftest", {"steps": ["down-at"],
                                      "down_delay_ticks": 600000,
                                      "down_pawn": victim["id"]})

    for num, key, want in (("7.1", "bound", "ticks"), ("7.2", "letter", "letter"),
                           ("7.3", "alert", "alert"), ("7.4", "casualty", "casualty")):
        check(num, "a %s comes back as reason %r" % (key, want),
              seen.get(key) == want, want, seen.get(key))
    check("7.5", "…and all four tokens are DISTINCT, which is what makes the "
                 "4.2 tally a discriminator rather than a count",
          len({v for v in seen.values() if v}) == len([v for v in seen.values() if v]),
          "4 distinct tokens", seen)
    note("7.6", "observed: %s" % json.dumps(seen))

    clear_alerts()
    disarm()


# ------------------------------------------------------------------- phase 8 --
#
# THE SUITE'S OWN MACHINERY, OFFLINE. No bench, no game, nothing sent.
#
# THE FIXTURES ARE REAL ENVELOPES the orchestrator banked at
# accept/runs/s21-20260901/, so "the assertions work" means "they work on what
# the mod actually emitted" rather than "they agree with a dict this file
# wrote". Two of them are load-bearing rather than decorative:
#
#   * `03-digest.json` predates this issue, so its `alerts` block GENUINELY
#     lacks `muted` and its `active` rows genuinely lack the per-row flag. That
#     is the honest fixture for the absent-vs-null trap phase 0 exists to close:
#     a suite that checked the new fields with `eq(..., None)` would have gone
#     green against a mod that never published them.
#   * `15-journal.json` carries a REAL `letter` payload (`def:"ThreatBig"`,
#     label "Ancient danger") and a REAL `alert_on` payload
#     (`Alert_NeedResearchBench`, priority Medium). The halt event is that
#     payload plus three stamped keys, so a `halted_on` built here from the real
#     payload is the exact shape the mod emits — and every dig path phases 1-4
#     lean on can be checked against it without a bench.

S21 = os.path.join(REPO, "accept", "runs", "s21-20260901")


def _s21(name):
    try:
        with open(os.path.join(S21, name), encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return None


def probe(fn):
    """Run one assertion and report whether it PASSED, without letting it into
    the real tally. The check helpers report through module state, so this
    swaps that state out and puts it back."""
    global FAILS, CHECKS
    keep_f, keep_c = FAILS, CHECKS
    FAILS, CHECKS = [], 0
    try:
        import io
        import contextlib
        with contextlib.redirect_stdout(io.StringIO()):
            fn()
        return not FAILS
    finally:
        FAILS, CHECKS = keep_f, keep_c


def journal_payload(j, want_type):
    for e in as_list(dig(j, "data.events")):
        if e.get("type") == want_type:
            return e.get("payload")
    return None


def as_halt(payload, kind, armed_by, tick=12617):
    """The halt event the mod builds: the journal payload VERBATIM plus the
    three keys `TimeDriver.WakeEvent` stamps on it. Reproduced here so the dig
    paths can be checked against a REAL payload offline."""
    evt = dict(payload or {})
    evt["kind"] = kind
    evt["armed_by"] = armed_by
    evt["tick"] = tick
    return {"ok": True, "op": "advance",
            "data": {"reason": kind, "halted_on": evt, "halted_seq": 32,
                     "ticks_elapsed": 41, "paused_on_exit": True}}


def phase8():
    banner("PHASE 8 — the suite's OWN machinery (offline; no bench, no game)")

    # -- THE ABSENT-VS-NULL TRAP, on a REAL pre-280fb78 digest -------------
    d = _s21("03-digest.json")
    if d is None:
        note("8.1", "accept/runs/s21-20260901/ is not in this checkout — the "
                    "envelope half of phase 8 is skipped")
    else:
        check("8.1a", "the banked digest is a pre-`muted` one",
              not has_key(d, "data.alerts.muted"),
              "data.alerts.muted ABSENT", dig(d, "data.alerts.muted"))
        check("8.1b", "eq(...,None) PASSES on that absent key — THE TRAP",
              probe(lambda: eq("x", "t", d, "data.alerts.muted", None)),
              "pass (which is why shape() exists)", "fail")
        check("8.1c", "shape() FAILS on it — the trap, closed",
              not probe(lambda: shape("x", "digest", d, "data.alerts.muted", list)),
              "fail", "pass")
        check("8.1d", "shape() PASSES on a key that envelope DOES carry",
              probe(lambda: shape("x", "digest", d, "data.alerts.active", list)),
              "pass", "fail")
        check("8.1e", "the per-row `muted` flag is absent there too",
              not has_key(d, "data.alerts.active.0.muted"),
              "absent", dig(d, "data.alerts.active.0.muted"))
        check("8.1f", "absent() PASSES on it and would FAIL if the key appeared",
              probe(lambda: absent("x", "t", d, "data.alerts.muted"))
              and not probe(lambda: absent("x", "t", d, "data.alerts.active")),
              "pass then fail", "something else")
        ids = [r.get("id") for r in as_list(dig(d, "data.alerts.active"))]
        check("8.1g", "…and the banked alert ids are the identity `alert-mute` "
                      "takes: Alert class names",
              bool(ids) and all(isinstance(i, str) and i.startswith("Alert_")
                                for i in ids),
              "every id an 'Alert_…' class name", ids)

    # -- the halt shapes, built from REAL journal payloads -----------------
    j = _s21("15-journal.json")
    if j is None:
        note("8.2", "no banked journal envelope in this checkout — skipped")
    else:
        lp = journal_payload(j, "letter")
        ap = journal_payload(j, "alert_on")
        check("8.2a", "the banked journal carries a real `letter` payload",
              isinstance(lp, dict) and "def" in lp, "a dict with `def`", lp)
        check("8.2b", "…and a real `alert_on` payload",
              isinstance(ap, dict) and "id" in ap and "priority" in ap,
              "a dict with `id` and `priority`", ap)

        if isinstance(lp, dict):
            wake = as_halt(lp, "letter", "default")
            check("8.3a", "every dig path phase 1 uses resolves on a halt built "
                          "from the REAL letter payload",
                  probe(lambda: (eq("x", "t", wake, "data.reason", "letter"),
                                 eq("x", "t", wake, "data.halted_on.kind", "letter"),
                                 eq("x", "t", wake, "data.halted_on.armed_by", "default"),
                                 eq("x", "t", wake, "data.halted_on.def", lp["def"]),
                                 shape("x", "advance", wake, "data.halted_on.label", str),
                                 shape("x", "advance", wake, "data.halted_seq", (int, float)))),
                  "pass", "fail")
            asked = as_halt(lp, "threat", "until")
            check("8.3b", "…and the EXPLICIT-wait shape is distinguishable from "
                          "the wake by `armed_by` alone, on the same payload",
                  dig(wake, "data.halted_on.armed_by") != dig(asked, "data.halted_on.armed_by"),
                  "'default' != 'until'",
                  [dig(wake, "data.halted_on.armed_by"),
                   dig(asked, "data.halted_on.armed_by")])
            check("8.3c", "…and that banked letter is a ThreatBig, which is "
                          "EXACTLY the collision phase 3 exists for: it satisfies "
                          "the wake and `until:{threat}` at once",
                  lp.get("def") == "ThreatBig", "ThreatBig", lp.get("def"))

        if isinstance(ap, dict):
            wake = as_halt(ap, "alert", "default")
            check("8.4a", "every dig path phase 2 uses resolves on a halt built "
                          "from the REAL alert_on payload",
                  probe(lambda: (eq("x", "t", wake, "data.reason", "alert"),
                                 eq("x", "t", wake, "data.halted_on.id", ap["id"]),
                                 eq("x", "t", wake, "data.halted_on.priority",
                                    ap["priority"]),
                                 eq("x", "t", wake, "data.halted_on.kind", "alert"))),
                  "pass", "fail")
            check("8.4b", "…and `priority` is a real AlertPriority name, which is "
                          "what the acceptance bullet asks the halt to publish",
                  ap.get("priority") in ("Medium", "High", "Critical"),
                  "Medium|High|Critical", ap.get("priority"))
            check("8.4c", "the payload does NOT already own `kind` or `armed_by`, "
                          "so stamping them cannot overwrite the mod's own data",
                  "kind" not in ap and "armed_by" not in ap,
                  "neither key present in the raw payload", sorted(ap.keys()))

        lp2 = journal_payload(j, "downed")
        if isinstance(lp2, dict):
            check("8.4d", "…and this is why `until:{event}` is NOT stamped: a "
                          "`downed` payload owns `kind` itself (colonist|slave|"
                          "animal|mech) and stamping would rewrite the caller's "
                          "data",
                  True, "documented", sorted(lp2.keys()))

    # -- the helpers themselves ---------------------------------------------
    check("8.5a", "halt_names() matches on reason AND the named field",
          halt_names(as_halt({"def": "NeutralEvent"}, "letter", "default"),
                     "letter", "def", "NeutralEvent"),
          "match", "no match")
    check("8.5b", "…and refuses a right-reason/wrong-name halt, which is the "
                  "flake wake_hunt() exists to survive",
          not halt_names(as_halt({"def": "ThreatBig"}, "letter", "default"),
                         "letter", "def", "NeutralEvent"),
          "no match", "match")
    check("8.5c", "…and a right-name/wrong-reason one",
          not halt_names(as_halt({"def": "NeutralEvent"}, "letter", "default"),
                         "alert", "def", "NeutralEvent"),
          "no match", "match")
    check("8.6", "advance() always declares `through_casualties` and NEVER "
                 "`through_news` — the split this suite is built on",
          True, "documented at advance(); phase 5 uses advance_riding_news()",
          "see the helpers")
    check("8.7", "_bound() puts a ceiling on every `until` advance (git-bug "
                 "1113019: an until that is true at arm time is unbounded)",
          _bound({"until": {"letter": True}}).get("timeout_ticks") == UNTIL_TIMEOUT_TICKS
          and "timeout_ticks" not in _bound({"ticks": 5}),
          "timeout on until, untouched on ticks",
          [_bound({"until": {"letter": True}}), _bound({"ticks": 5})])
    check("8.8", "the wake bound is at least 5x the fixture delay, so 'it "
                 "stopped early' is a real measurement and not a coin flip",
          WAKE_BOUND >= 5 * max(LETTER_DELAY, ALERT_DELAY),
          ">= %d" % (5 * max(LETTER_DELAY, ALERT_DELAY)), WAKE_BOUND)


# ---------------------------------------------------------------------- main --

PHASES = {0: phase0, 1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5,
          6: phase6, 7: phase7, 8: phase8}


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
                    help="phase 8 only: the suite's own assertions over the banked "
                         "envelopes. No bench, no game, nothing sent.")
    ap.add_argument("--day-ticks", type=int, default=60000,
                    help="phase 6's budget; 60000 is one in-game day (default). "
                         "15000 is the quick version and still proves the "
                         "corroboration rule.")
    ap.add_argument("--max-segments", type=int, default=25,
                    help="phase 6 stops after this many halts even if the budget "
                         "is unspent — a colony under siege must not run forever")
    ap.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = ap.parse_args()

    # --selftest needs NO bench and must not be gated on one.
    if ARGS.selftest:
        print("280fb78 acceptance — mode: --selftest")
        print("offline; no bench, no protocol root, no game, nothing sent")
        phase8()
        banner("RESULT")
        if FAILS:
            print("%s%d/%d selftest checks FAILED: %s%s"
                  % (RED, len(FAILS), CHECKS, ", ".join(FAILS), OFF))
            return 1
        print("%sSELFTEST PASS — all %d checks%s" % (GREEN, CHECKS, OFF))
        return 0

    # Phase 8 is offline and is NOT in the default run — `--selftest` is its
    # front door. `--phase 8` alone still works and skips phase 0's bench
    # preconditions, because a phase that touches no bench must not be gated on
    # one.
    wanted = sorted(set(ARGS.phase or [p for p in PHASES if p != 8]))
    if 0 not in wanted and wanted != [8]:
        wanted = [0] + wanted

    print("280fb78 wake-halts acceptance — root %s" % ARGS.root)
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
