#!/usr/bin/env python3
"""Acceptance runner for spec 3.5 — dialog + interaction verbs (git-bug 20e5cda)
and its hard dependency, the quest observer (git-bug 548ef48).

The POSIX twin of `accept/3.5-dialog-verbs.ps1`, kept in step with it check for
check. Same protocol, helpers and exit codes as `accept/4087644-order-honesty.py`
and `accept/3.4-pawn-orders.py`; read either of those headers first.

WHY BOTH FILES EXIST, and it is not redundancy for its own sake. BORGES has no
python (Store stub only), so the `.ps1` is the only thing that can run there.
THIS box has no pwsh — and even with pwsh the `.ps1` could not run here, because
its `$Root` defaults to `$env:USERPROFILE\\...` and every path it builds is
`Join-Path $Root "commands\\$id.json"`, which on POSIX yields a filename with a
literal backslash in it rather than a directory. Precedent: `accept/3.4-pawn-orders`
ships both twins for the same reason. Keep them in step; the check numbers are
deliberately identical so a failure reported from one bench can be looked up in
the other file.

    ./accept/3.5-dialog-verbs.py                # everything
    ./accept/3.5-dialog-verbs.py --phase 3      # one phase (0 always runs)
    ./accept/3.5-dialog-verbs.py --dry-run      # print the plan, send nothing

Phases:
    1  quest log         — 548ef48 acceptance, all five bullets
    2  quest accept      — 3.5 bullet 2, the red-error trap
    3  trade             — 3.5 bullet 1, buy + sell + verify + cancel
    4  letters + dialogs — 3.5 bullet 3, opaque + dismiss + the 1.7 un-wedge
    5  comms             — the headless DiaNode walker
    6  invariants        — zero red errors, clean stack, journal provenance

Start the bench first (`_RimWorld-Agent/run-agent.sh`), load a colony with at
least two colonists (one Social-capable), turn DEV MODE ON (phase 0 checks, and
skips rather than failing if it is off), and leave it paused.

--dry-run PROVES THE PLAN, NEVER THE PATHS. It sends nothing, so every envelope
is empty, every shape check is skipped and every dig() path looks fine. Only a
live run tells you whether the envelopes are the shape the assertions assume,
which is what phase 0's shape contract is for.

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

# The 18 ops these two issues register. Phase 0.2 asserts every one of them is
# in `status.data.verbs`, which is the strongest single check in the file: a
# verb that failed to register produces downstream failures indistinguishable
# from a bad fixture.
NEW_OPS = [
    "quests", "quest", "quest-accept", "quest-dismiss",
    "letter-read", "letter-choose", "letter-dismiss",
    "dialog-choose", "dialog-dismiss",
    "trade-start", "trade", "trade-set", "trade-confirm", "trade-cancel",
    "comms-targets", "comms-call", "comms-choose", "comms-hang-up",
]


# ------------------------------------------------------------------ protocol --
# commands/<id>.json in, results/<id>.json out. The poller runs a 500 ms cycle
# and ignores inbox files younger than its MinFileAgeMs, so a round trip has a
# floor of roughly 0.25-1 s even for a trivial verb; that is the protocol, not
# this script being slow. `advance` is a DEFERRED result — its file appears only
# when the advance finishes — hence the generous per-call timeout.
#
# Ids are kept to [A-Za-z0-9-] so Poller.Sanitize leaves them alone and the
# result filename is exactly <id>.json.

def send(op, args=None, timeout=300):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acc35-%03d-%s" % (SEQ, slug)
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
                note_session(op, env)
                return env
            except (ValueError, OSError):
                time.sleep(0.12)
                continue
        time.sleep(0.2)
    return {"ok": False, "op": op,
            "error": {"code": "acc-timeout",
                      "detail": "no results/%s.json within %ss — is the bench running?"
                                % (cid, timeout)}}


# Session bookkeeping for close_any_session(). Kept HERE rather than at the
# three call sites, because the call site that matters is the one nobody
# remembers to add — phase 3 opens a session at 3.2 and again at 3.7h, and every
# `precondition` after either of those exits the process.
def note_session(op, env):
    if op == "trade-start":
        if dig(env, "data.ok") is True:
            S["session_open"] = True
    elif op in ("trade-cancel", "trade-confirm"):
        if dig(env, "data.session_closed") is True:
            S["session_open"] = False


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
    """The predicate dig() CANNOT be: it distinguishes an ABSENT key from one
    that is present and null. See the shape-contract note above phase 0."""
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
    return "null" if v is None else json.dumps(v, separators=(",", ":"))


# MiniJson serialises `(double)n` through ToString("0.####"), so a whole number
# arrives as a JSON integer and json.load gives an int -- but accept either, so
# a serializer change is a shape FAILURE at phase 0 rather than a spurious red
# on a type name.
NUM = (int, float)


def is_num(v):
    return isinstance(v, NUM) and not isinstance(v, bool)


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
    ok = is_num(got) and got >= want
    check(num, "%s (%s)" % (what, path), ok, ">= %s" % want, got)


# eq()/ge() take (env, path). PASSING A COMPUTED VALUE WHERE `env` BELONGS is
# the defect the 2026-08-31 audit found eight times in the .ps1 twin: in
# PowerShell it shifts every later parameter, `$Want` falls off the end as
# $null, and `$null -eq $null` is TRUE — so the check goes GREEN WHILE
# ASSERTING NOTHING. Python would raise instead of silently passing, but the two
# files are kept in step check for check, so the same two helpers exist here and
# the same call sites use them.
def eq_val(num, what, got, want):
    check(num, what, got == want, show(want), got)


def ge_val(num, what, got, want):
    check(num, what, is_num(got) and got >= want, ">= %s" % want, got)


def not_null(num, what, env, path):
    got = dig(env, path)
    check(num, "%s (%s)" % (what, path), got is not None, "present and non-null", got)


def contains(num, what, haystack, needle):
    s = "" if haystack is None else str(haystack)
    check(num, what, needle in s, "contains '%s'" % needle, haystack)


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
    print("          This is a FIXTURE gap, not a 3.5 failure. Stage it and re-run.")
    sys.exit(2)


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


# ------------------------------------------------------------ shape contract --
# THE ROUND'S CENTRAL LESSON, and it is not a nicety.
#
# `dig()` returns its default for an ABSENT key and for one that is
# present-and-null alike, so `eq(..., None)` passes either way. A driver whose
# dig paths are WRONG therefore does not fail — it goes GREEN WHILE ASSERTING
# NOTHING, which is strictly worse than a loud abort, because nobody
# investigates a pass. There are nine `data.action.journal_seq == null`
# assertions in this suite; every one of them is backed by a real key today, so
# the hazard here is LATENT rather than live — and it is one serializer edit
# away from live.
#
# has_key() is the predicate dig() cannot be. Phase 0 uses it to PROVE every
# envelope key the later phases dig on, naming the verb and the key, so a shape
# change fails THERE — loudly, at a check that says which verb moved — instead
# of downstream, or not at all.
#
# PER-DRIVER ON PURPOSE, not a shared accept/_shapes.py. Every file in accept/
# stands alone and runs from a bare checkout, which is what makes acceptance
# portable across two benches with different tooling and why the .py/.ps1 twins
# duplicate deliberately. A shared module would let a shape change made for one
# spec silently update every other driver, when what you want is 3.5's driver
# failing loudly when 3.5's own contract changes.
def shape(num, verb, env, path, kind=None):
    """Assert a key EXISTS, independently of its value."""
    ok = has_key(env, path)
    want = "the key to be PRESENT (absent != null)"
    if ok and kind is not None:
        ok = isinstance(dig(env, path), kind)
        names = kind if isinstance(kind, tuple) else (kind,)
        want += " and a %s" % "/".join(k.__name__ for k in names)
    check(num, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def absent(num, what, env, path):
    """The other half. `advance` does not publish `data.ticks`, and 4.8g read
    exactly that for a whole round."""
    check(num, "%s (%s)" % (what, path), not has_key(env, path),
          "the key to be ABSENT", dig(env, path))


# --------------------------------------------------------- standing invariants --

def no_red_errors(num, what):
    e = send("journal", {"since_seq": S.get("seq0", 0),
                         "types": ["red_error"], "limit": 50})
    eq(num, what, e, "data.count", 0)


def stack_clear(num, what):
    e = send("status")
    check(num, what, not has_key(e, "data.forcePause"),
          "status.forcePause absent", dig(e, "data.forcePause"))


def staged(num, defname, kind="3.5"):
    """Stage a fixture with dev:incident and ASSERT THE CALL WORKED rather than
    inferring it from whatever turns up later. Inferring conflates `the incident
    verb refused` with `the caravan is still walking in`, and those two need
    different answers from the operator."""
    e = send("dev:incident", {"def": defname})
    precondition(num, 'dev:incident {def:"%s"} was accepted' % defname,
                 dig(e, "ok") is True,
                 "the incident verb itself refused: code=%s detail=%s. That is a "
                 "FIXTURE problem (devMode off, or the incident cannot target this "
                 "map), not a %s failure."
                 % (dig(e, "error.code"), dig(e, "error.detail"), kind))
    return e


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 — the bench, the dev switch, and THE SHAPE CONTRACT")

    # 0.1  {"op":"status"}
    e = send("status")
    precondition("0.1", "the bench answers `status`",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "no status envelope — start _RimWorld-Agent/run-agent.sh")
    shape("0.1a", "status", e, "data.gameLoaded")
    shape("0.1b", "status", e, "data.verbs", list)
    eq("0.1c", "a game is loaded", e, "data.gameLoaded", True)
    # The whole point of this spec: a run must not START wedged either.
    absent("0.1d", "no force-pausing modal is up before we begin", e, "data.forcePause")

    # 0.2  every op these two issues register must be present.
    verbs = as_list(dig(e, "data.verbs"))
    missing = [o for o in NEW_OPS if o not in verbs]
    check("0.2", "all 18 ops of 20e5cda + 548ef48 registered",
          ARGS.dry_run or not missing, "no missing ops", missing)

    # 0.3  THE DEV SWITCH. Phases 1-4 stage their fixtures with `dev:incident`
    #      and `journal-selftest`, and BOTH throw on !Prefs.DevMode
    #      (DevVerbs.Dev.Gate, JournalVerbs.Selftest). Without this probe a
    #      devMode-off bench fails four phases later with "no trader
    #      materialised", which reads as a spec failure and is not one.
    #      `dev:incident` with NO args is the probe: `Dev.Gate(V)` runs BEFORE
    #      `Dev.CurrentMap` and before `a.StrReq("def")`, so it mutates nothing
    #      either way and the two outcomes are distinguishable by message.
    e = send("dev:incident")
    detail = str(dig(e, "error.detail") or "")
    precondition("0.3", "devMode is ON (the fixtures need it)",
                 ARGS.dry_run or "devMode" not in detail,
                 "dev:incident answered: %s  ->  Prefs.DevMode is FALSE on this bench. "
                 "dev:incident and journal-selftest are both gated on it, so phases 2-5 "
                 "cannot stage a quest, a trader or a dialog. Turn dev mode on in the "
                 "bench's options (the agent profile seeds it True — spike 0.1 FINDINGS) "
                 "and re-run." % detail)
    eq("0.3a", "and the gate refused for a REASON, not silently",
       e, "error.code", "bad-args")

    # 0.4  journal: the watermark every later no_red_errors is measured from.
    e = send("journal", {"limit": 1})
    shape("0.4a", "journal", e, "data.count")
    shape("0.4b", "journal", e, "data.last_seq")
    shape("0.4c", "journal", e, "data.events", list)
    S["seq0"] = dig(e, "data.last_seq") or 0
    print("  %sjournal watermark seq0 = %s%s" % (DIM, S["seq0"], OFF))

    # 0.5  interactions: 1.7's force-pause vocabulary, which phases 3-6 dig into
    #      by five different paths.
    e = send("interactions")
    shape("0.5a", "interactions", e, "data.force_pause", dict)
    shape("0.5b", "interactions", e, "data.force_pause.count", NUM)
    shape("0.5c", "interactions", e, "data.blocking")
    shape("0.5d", "interactions", e, "data.letters", list)
    shape("0.5e", "interactions", e, "data.windows", list)
    eq("0.5f", "and the stack is clear before we start", e, "data.force_pause.count", 0)

    # 0.6  THE ADVANCE ENVELOPE, AND THE KEY THAT WAS WRONG. Check 4.8g — the
    #      proof that un-wedging restored ticking, the single most load-bearing
    #      check in the suite — read `data.ticks`, which `TimeDriver.BuildData`
    #      does not and never did emit (it emits `["ticks_elapsed"] = ticks`).
    #      So 4.8g could ONLY fail. Both halves are asserted here so the mistake
    #      cannot be made again silently: the real key must be PRESENT and the
    #      wrong one must be ABSENT.
    #      One tick, deliberately: the smallest budget that still produces a
    #      real (non-refused) advance envelope, taken BEFORE phase 1's
    #      double-read proof, which needs the clock still.
    e = send("advance", {"ticks": 1, "max_tps": 400})
    shape("0.6a", "advance", e, "data.reason")
    shape("0.6b", "advance", e, "data.ticks_elapsed", NUM)
    absent("0.6c", "advance does NOT publish `data.ticks` (4.8g used to read it)",
           e, "data.ticks")

    # 0.7  the quest observer's shape, including the `action` block that nine
    #      later checks assert is present-with-a-null-seq. `eq(..., None)` alone
    #      would pass on an ABSENT action block just as happily, which is the
    #      whole reason this section exists.
    e = send("quests")
    shape("0.7a", "quests", e, "data.total", NUM)
    shape("0.7b", "quests", e, "data.counts", dict)
    shape("0.7c", "quests", e, "data.counts.available")
    shape("0.7d", "quests", e, "data.counts.ongoing")
    shape("0.7e", "quests", e, "data.counts.ended")
    shape("0.7f", "quests", e, "data.counts.dismissed")
    shape("0.7g", "quests", e, "data.counts.with_outstanding_choice")
    shape("0.7h", "quests", e, "data.quests", list)
    shape("0.7i", "quests", e, "data.action", dict)
    shape("0.7j", "quests", e, "data.action.journal_seq")
    eq("0.7k", "and an observer stamps it null (present AND null, both proved)",
       e, "data.action.journal_seq", None)

    # 0.8  the two refusal shapes phase 6 relies on, probed with NO fixture:
    #      both are the natural state of a bench that has not traded or called.
    e = send("trade")
    eq("0.8a", "`trade` with no session refuses at gate no-session", e, "data.gate",
       "no-session")
    shape("0.8b", "trade", e, "data.gate_cite")
    S["noSessionTrade"] = e          # 0.11 reads it back; see the note there
    e = send("comms-choose", {"option": 0})
    eq("0.8c", "`comms-choose` with no call refuses at gate no-call", e, "data.gate",
       "no-call")
    shape("0.8d", "comms-choose", e, "data.gate_cite")

    # 0.9  dialog-dismiss on a clear stack: the no-op shape 4.9 asserts.
    e = send("dialog-dismiss")
    shape("0.9a", "dialog-dismiss", e, "data.ok")
    shape("0.9b", "dialog-dismiss", e, "data.dismissed", list)
    eq_val("0.9c", "and it dismissed nothing (data.dismissed)",
           len(as_list(dig(e, "data.dismissed"))), 0)

    # 0.10  a Social-capable colonist to negotiate with. Every trade and comms
    #       gate runs through one, and picking a pawn with Social disabled fails
    #       six checks whose only clue is a gate string.
    e = send("pawns", {"filter": "colonist"})
    shape("0.10a", "pawns", e, "data.list", list)
    roster = [p for p in as_list(dig(e, "data.list")) if isinstance(p, dict)]
    precondition("0.10b", "at least one visible colonist",
                 ARGS.dry_run or len(roster) >= 1,
                 "the roster has %d." % len(roster))
    if ARGS.dry_run:
        S["N"], S["Nname"] = 1001, "<negotiator>"
    else:
        able, why_not = [], []
        for r in roster:
            w = send("pawn", {"id": r.get("id"), "sections": ["skills"]})
            skills = as_list(dig(w, "data.skills.list"))
            social = [s for s in skills if isinstance(s, dict) and s.get("def") == "Social"]
            if not social:
                why_not.append("%s: no Social row" % r.get("name"))
                continue
            if social[0].get("disabled") is True:
                why_not.append("%s: Social disabled" % r.get("name"))
                continue
            able.append(r)
        precondition("0.10c", "a colonist capable of Social (the negotiator)",
                     len(able) >= 1,
                     "no visible colonist can negotiate. Rejected: %s. "
                     "FloatMenuOptionProvider_Trade refuses on "
                     "skills.GetSkill(SkillDefOf.Social).TotallyDisabled, so trade and "
                     "comms are both unreachable without one."
                     % ("; ".join(why_not) if why_not else "none"))
        S["N"], S["Nname"] = able[0].get("id"), able[0].get("name")
    print("  %snegotiator N = %s (%s)%s" % (DIM, S["N"], S["Nname"], OFF))

    # 0.11  THE TRADE-TOTALS SHAPE CONTRACT — the half of it that CAN live here,
    #       and an explicit statement of why the other half cannot. Phase 3 digs
    #       `data.totals.colony_silver`, `.colony_silver_post_deal` and
    #       `.trader_silver`, and `TradeVerbs.AddDealTotals` writes the whole
    #       `totals` block off `TradeSession.deal` — so it exists only inside an
    #       open session, which phase 0 has no trader to open. The ABSENCE half
    #       is proved here on 0.8's own no-session refusal; the PRESENCE half is
    #       proved at 3.2i-3.2k and 3.8a1-3.8a3, at the first envelopes that can
    #       carry it. Written down rather than skipped: a shape contract with an
    #       undeclared hole in it is the exact failure this section exists to
    #       prevent, and 3.2d spent a whole round in one.
    absent("0.11", "a refused `trade` carries NO totals block, so the PRESENCE half of "
                   "the trade shape contract is deferred to 3.2i / 3.8a1 rather than "
                   "dropped",
           S.get("noSessionTrade") or {}, "data.totals")


# ------------------------------------------------------------------- phase 1 --
# 548ef48 ACCEPTANCE, all five bullets.

def phase1():
    banner("PHASE 1 — 548ef48: the quest log is readable, and reading it does not write")

    # 1.1 {"op":"quests"} — the list, with the counts rollup.
    e = send("quests")
    eq("1.1a", "quests answered", e, "ok", True)
    eq("1.1b", "and names itself", e, "data.verb", "quests")
    not_null("1.1c", "the state counts rollup is present", e, "data.counts")
    not_null("1.1d", "available count", e, "data.counts.available")
    not_null("1.1e", "ongoing count", e, "data.counts.ongoing")
    not_null("1.1f", "ended count", e, "data.counts.ended")
    # BULLET 3: dismissed is reported DISTINCTLY from declined/expired, and the
    # payload says what it is. A comment nobody reads would not satisfy this.
    not_null("1.1g", "BULLET 3 — dismissed is its own count, not a state",
             e, "data.counts.dismissed")
    contains("1.1h", "BULLET 3 — and the result SAYS dismissed is not a decline",
             dig(e, "data.dismissed_means"), "NOT a decline")
    eq("1.1i", "this call mutated nothing", e, "data.action.journal_seq", None)

    quests = [q for q in as_list(dig(e, "data.quests")) if isinstance(q, dict)]
    S["qTotal"] = dig(e, "data.total") or 0
    print("  %s%s quests: available=%s ongoing=%s ended=%s dismissed=%s with-choice=%s%s"
          % (DIM, S["qTotal"], dig(e, "data.counts.available"),
             dig(e, "data.counts.ongoing"), dig(e, "data.counts.ended"),
             dig(e, "data.counts.dismissed"),
             dig(e, "data.counts.with_outstanding_choice"), OFF))

    # 1.2 BULLET 4 — THE DOUBLE-READ PROOF. 2.4 used it for research progress;
    #     it is the same proof here, and it is what says the observer does not
    #     write. `quests` twice must be IDENTICAL over every field a
    #     write-on-read would move. (Ticks-since-* move with the clock, so the
    #     game must be paused for this, which phase 0 asserted.)
    def projection(rows):
        return "|".join(
            "%s:%s:%s:%s:%s:%s" % (r.get("id"), r.get("state"), r.get("dismissed"),
                                   r.get("acceptance_tick"), r.get("ticks_until_expiry"),
                                   dig(r, "choice.count"))
            for r in rows if isinstance(r, dict))

    e2 = send("quests")
    a = projection(quests)
    b = projection(as_list(dig(e2, "data.quests")))
    check("1.2a", "BULLET 4 — reading the quest log TWICE yields an identical projection",
          a == b, a, b)
    eq("1.2b", "and the totals match", e2, "data.total", S["qTotal"])
    note("1.2c", "the byte-identical-save half of bullet 4 is the ORCHESTRATOR's to "
                 "run: save, `quests`, `quest` on every id, save again, diff the "
                 "<quests> region. This script cannot save without perturbing the very "
                 "counters it is checking.")

    if S["qTotal"] == 0 and not ARGS.dry_run:
        note("1.3", 'no quests on this colony — staging with dev:incident '
                    '{def:"GiveQuest_Random"}')
        staged("1.3s", "GiveQuest_Random", "548ef48")
        e = send("quests")
        quests = [q for q in as_list(dig(e, "data.quests")) if isinstance(q, dict)]
        S["qTotal"] = dig(e, "data.total") or 0
    precondition("1.3a", "at least one quest to drill into",
                 ARGS.dry_run or S["qTotal"] >= 1,
                 'no quest exists and dev:incident {def:"GiveQuest_Random"} did not '
                 "produce one. Stage one and re-run.")

    # 1.4 BULLET 1 — each row carries state, demands, rewards and expiry.
    q = {"id": 1} if ARGS.dry_run else quests[0]
    S["q"] = q.get("id")
    e = send("quest", {"quest": S["q"]})
    eq("1.4a", "quest drill-down answered", e, "ok", True)
    not_null("1.4b", "BULLET 1 — state", e, "data.state")
    not_null("1.4c", "BULLET 1 — expiry (ticks_until_expiry; -1 means never)",
             e, "data.ticks_until_expiry")
    not_null("1.4d", "BULLET 1 — demands (from QuestPart_RequirementsToAccept)",
             e, "data.requirements")
    not_null("1.4e", "BULLET 1 — rewards", e, "data.rewards")
    not_null("1.4f", "the parts list", e, "data.parts")
    eq("1.4g", "and the drill-down mutated nothing", e, "data.action.journal_seq", None)
    # The sections shape 2.2/2.4 use.
    e = send("quest", {"quest": S["q"], "sections": ["head"]})
    eq("1.4h", "sections select a subset", e, "data.sections.0", "head")
    check("1.4i", "and a subset omits the rest", not has_key(e, "data.parts"),
          "data.parts absent", dig(e, "data.parts"))
    e = send("quest", {"quest": S["q"], "sections": ["nonsense"]})
    eq("1.4j", "an unknown section is bad-args, never a silent empty result",
       e, "error.code", "bad-args")

    # 1.5 BULLET 2 — a quest with an outstanding QuestPart_Choice reports the
    #     choice as outstanding AND enumerates the options. This is the exact
    #     read 3.5's accept verb needs in order to choose before accepting.
    with_choice = [x for x in quests if dig(x, "choice.outstanding") is True]
    if not with_choice and not ARGS.dry_run:
        note("1.5", "no quest on this colony has an outstanding QuestPart_Choice yet — "
                    "phase 2 stages one and re-checks this bullet there (2.2)")
    else:
        cq = {"id": 1} if ARGS.dry_run else with_choice[0]
        S["qChoice"] = cq.get("id")
        e = send("quest", {"quest": S["qChoice"], "sections": ["choice"]})
        eq("1.5a", "BULLET 2 — the choice is reported OUTSTANDING",
           e, "data.choice.outstanding", True)
        ge("1.5b", "BULLET 2 — with two or more options enumerated",
           e, "data.choice.count", 2)
        not_null("1.5c", "BULLET 2 — option 0 has an index (what quest-accept {choice:N} takes)",
                 e, "data.choice.options.0.index")
        not_null("1.5d", "BULLET 2 — and its rewards", e, "data.choice.options.0.rewards")
        contains("1.5e", "and the block warns what accepting without choosing would do",
                 dig(e, "data.choice.note"), "red error")

    # 1.6 the filters
    e = send("quests", {"state": "available"})
    eq("1.6a", "state:available filters", e, "data.filter.state", "available")
    e = send("quests", {"state": "nonsense"})
    eq("1.6b", "an unknown state is bad-args", e, "error.code", "bad-args")

    # 1.7 BULLET 5
    no_red_errors("1.7", "BULLET 5 — zero red errors across the whole quest read")


# ------------------------------------------------------------------- phase 2 --
# 3.5 ACCEPTANCE BULLET 2, as rewritten by the backlog verification pass:
# "a quest offer letter is listed and read; `quest accept {quest}` — choosing a
#  reward first where a QuestPart_Choice exists — makes the quest active; the
#  letter is separately dismissed; zero red errors, in particular no 'still has
#  a choice unresolved'."

def phase2():
    banner("PHASE 2 — 3.5 BULLET 2: a quest offer is read, accepted (choosing first), "
           "and its letter dismissed")

    # 2.1 stage a quest offer. dev:incident is the named fixture, per the
    #     verification pass's "name whichever it is IN the acceptance".
    before = as_list(dig(send("quests", {"state": "available"}), "data.quests"))
    if not before and not ARGS.dry_run:
        staged("2.1s", "GiveQuest_Random")
        before = as_list(dig(send("quests", {"state": "available"}), "data.quests"))
    precondition("2.1", "an available (NotYetAccepted) quest",
                 ARGS.dry_run or len(before) >= 1,
                 'dev:incident {def:"GiveQuest_Random"} produced no NotYetAccepted '
                 "quest. Stage one and re-run.")
    target = {"id": 1, "name": "<quest>"} if ARGS.dry_run else before[0]
    S["qa"] = target.get("id")
    print("  %starget quest = %s (%s)%s" % (DIM, S["qa"], target.get("name"), OFF))

    # 2.2 the letter side. `interactions` (2.4) is the list; `letter-read` is
    #     the drill-down. THE POINT OF THIS STEP is the NewQuestLetter finding:
    #     a quest offer letter has NO accept option, so the letter and the
    #     acceptance are two different acts.
    e = send("interactions")
    eq("2.2a", "interactions answered", e, "ok", True)
    letters = as_list(dig(e, "data.letters"))
    offer = [l for l in letters if isinstance(l, dict) and l.get("type") == "NewQuestLetter"]
    if offer:
        S["letter"] = offer[0].get("id")
        e = send("letter-read", {"letter": S["letter"]})
        eq("2.2b", "letter-read answered", e, "ok", True)
        eq("2.2c", "it is a NewQuestLetter", e, "data.type", "NewQuestLetter")
        not_null("2.2d", "and it names the quest it is about", e, "data.quest")
        labels = [o.get("label") for o in as_list(dig(e, "data.options"))
                  if isinstance(o, dict)]
        print("  %sits options: %s%s" % (DIM, " | ".join(str(x) for x in labels), OFF))
        # The finding, asserted rather than assumed: none of its options accepts.
        contains("2.2e", "the result SAYS a NewQuestLetter has no accept option",
                 dig(e, "data.note"), "NO accept option")
        eq("2.2f", "and reading it mutated nothing", e, "data.action.journal_seq", None)
    else:
        note("2.2", "no NewQuestLetter on the stack (the offer letter may already have "
                    "been reaped) — the letter half of bullet 2 is exercised in phase 4 "
                    "instead")

    # 2.3 THE RED-ERROR TRAP, asserted as a REFUSAL. This is the single most
    #     important check in the run: accepting with two or more choices
    #     outstanding runs QuestPart_Choice.PreQuestAccept, which Log.Errors
    #     "still has a choice unresolved" and auto-picks the first. The verb
    #     must refuse INSTEAD, and the refusal must carry the choice block.
    e = send("quest", {"quest": S["qa"], "sections": ["choice"]})
    outstanding = dig(e, "data.choice.outstanding") is True
    choice_count = dig(e, "data.choice.count") or 0
    if outstanding:
        print("  %sthis quest has %s outstanding reward choices%s"
              % (DIM, choice_count, OFF))
        e = send("quest-accept", {"quest": S["qa"]})
        eq("2.3a", "accept with a choice outstanding is REFUSED, not attempted",
           e, "data.ok", False)
        eq("2.3b", "and names the gate", e, "data.gate", "choice-outstanding")
        contains("2.3c", "the reason quotes the red error it is preventing",
                 dig(e, "data.reason"), "still has a choice")
        contains("2.3d", "and cites the widget clause it reproduces",
                 dig(e, "data.gate_cite"), "DoAcceptButton")
        ge("2.3e", "the refusal hands back the choice options so the caller can choose",
           e, "data.choice.count", 2)
        eq("2.3f", "and nothing was journaled (the refusal mutated nothing)",
           e, "data.action.journal_seq", None)
        no_red_errors("2.3g", "THE INVARIANT: the refused accept logged NO red error")
    else:
        note("2.3", "this quest has no outstanding QuestPart_Choice (count=%s) — the "
                    "red-error trap is not exercisable on it. Re-run against a quest "
                    "whose `quest {sections:['choice']}` reports outstanding:true; "
                    "`quests` names one under counts.with_outstanding_choice."
                    % choice_count)

    # 2.4 THE ACCEPT. Choose first where a choice exists; the verb does the
    #     Choose->Accept ordering internally, which is what makes PreQuestAccept
    #     unreachable.
    args = {"quest": S["qa"]}
    if outstanding:
        args["choice"] = 0
    e = send("quest-accept", args)
    if dig(e, "data.ok") is not True and dig(e, "data.gate") == "accepter-warnings":
        note("2.4", "the chosen accepter would take a royal favour they are a poor fit "
                    "for — the game raises a Dialog_MessageBox confirmation here and "
                    "this verb refuses instead. Re-sending with "
                    "confirm_accepter_warnings:true, the headless \"Confirm\".")
        args["confirm_accepter_warnings"] = True
        e = send("quest-accept", args)
    eq("2.4a", "quest-accept succeeded", e, "data.ok", True)
    check("2.4b", "the quest is ACTIVE (state read back, not assumed)",
          dig(e, "data.state") in ("Ongoing", "EndedSuccess", "EndedFailed",
                                   "EndedUnknownOutcome"),
          "Ongoing (or already ended)", dig(e, "data.state"))
    ge("2.4c", "and it carries an acceptance tick", e, "data.acceptance_tick", 0)
    not_null("2.4d", "the mutation is journaled (the action row join key)",
             e, "data.action.journal_seq")
    if outstanding:
        eq("2.4e", "the reward choice was recorded", e, "data.choice_taken", 0)
        ge("2.4f", "choices before", e, "data.choices_before", 2)
        eq("2.4g", "choices after Choose — exactly one remains", e, "data.choices_after", 1)
        contains("2.4h", "and the note states the ordering that avoided the red error",
                 dig(e, "data.note"), "BEFORE Accept")
    # 2.4i THE BULLET'S OWN INVARIANT, in its own words.
    e2 = send("journal", {"since_seq": S["seq0"], "types": ["red_error"], "limit": 50})
    unresolved = [r for r in as_list(dig(e2, "data.events"))
                  if "choice unresolved" in str(dig(r, "payload.msg") or "")]
    check("2.4i", 'ZERO "still has a choice unresolved" red errors',
          not unresolved, "none", unresolved)

    # 2.5 the state read back independently, through the observer.
    e = send("quest", {"quest": S["qa"], "sections": ["head"]})
    check("2.5a", "the observer agrees the quest is no longer NotYetAccepted",
          dig(e, "data.state") != "NotYetAccepted",
          "anything but NotYetAccepted", dig(e, "data.state"))
    eq("2.5b", "and Accept cleared the dismissed flag, as Quest.Accept does",
       e, "data.dismissed", False)

    # 2.6 the letter is dismissed SEPARATELY — they are independent acts.
    if S.get("letter"):
        e = send("letter-dismiss", {"letter": S["letter"]})
        eq("2.6a", "letter-dismiss succeeded", e, "data.ok", True)
        eq("2.6b", "and the letter is gone from the stack (write read back)",
           e, "data.removed", True)
        not_null("2.6c", "journaled", e, "data.action.journal_seq")
        e = send("interactions")
        still = [l for l in as_list(dig(e, "data.letters"))
                 if isinstance(l, dict) and l.get("id") == S["letter"]]
        check("2.6d", "interactions no longer lists it", not still, "absent", still)

    # 2.7 dismissed is cosmetic — the third acceptance bullet of 548ef48, acted.
    #     MainTabWindow_Quests.DoDismissButton is `selected.dismissed =
    #     !selected.dismissed` followed by a one-level walk over
    #     `selected.GetSubquests()`. Passing `dismissed` explicitly is the SET
    #     form; omitting it TOGGLES, as the click does. Both are exercised.
    e = send("quest-dismiss", {"quest": S["qa"], "dismissed": True})
    if dig(e, "data.ok") is True:
        eq("2.7a", "quest-dismiss set the flag", e, "data.dismissed", True)
        eq("2.7a1", "passing `dismissed` explicitly is the SET form", e, "data.mode", "set")
        shape("2.7a2", "quest-dismiss", e, "data.subquests", list)
        contains("2.7a3", "and the note names the subquest propagation the widget does",
                 dig(e, "data.note"), "subquest")
        contains("2.7b", "and says plainly it is NOT a decline",
                 dig(e, "data.note"), "does NOT decline")
        e = send("quests", {"include_dismissed": False})
        gone = [q for q in as_list(dig(e, "data.quests"))
                if isinstance(q, dict) and q.get("id") == S["qa"]]
        check("2.7c", "include_dismissed:false filters it out (cosmetic filtering, exactly)",
              not gone, "absent", gone)
        e = send("quest", {"quest": S["qa"], "sections": ["head"]})
        check("2.7d", "but its STATE is untouched — dismissed is orthogonal to state",
              dig(e, "data.state") != "NotYetAccepted",
              "still accepted/ongoing", dig(e, "data.state"))
        # The TOGGLE form, which is the click: no `dismissed` arg at all.
        e = send("quest-dismiss", {"quest": S["qa"]})
        eq("2.7e", "omitting `dismissed` toggles, as DoDismissButton does",
           e, "data.mode", "toggle")
        eq("2.7f", "and the toggle undid it", e, "data.dismissed", False)
    else:
        note("2.7", "quest-dismiss refused: %s" % dig(e, "data.reason"))

    no_red_errors("2.8", "zero red errors across the whole accept phase")


# ------------------------------------------------------------------- phase 3 --
# 3.5 ACCEPTANCE BULLET 1: "buy 10 meals + sell 5 shirts sight-unseen via verbs;
# silver and stock verified correct after confirm; cancel leaves state untouched."

def phase3():
    banner("PHASE 3 — 3.5 BULLET 1: a real trade, transacted against the model with "
           "no window")

    # 3.1 THE FIXTURE, NAMED. dev:spawn-pawn cannot make a trade partner:
    #     TradeSession.SetupWith Log.Warnings unless ITrader.CanTradeNow, and a
    #     map trader needs a LordJob_TradeWithColony. The incident is the route.
    def traders_now():
        e = send("pawns", {"filter": "all", "cap": 200})
        return [p for p in as_list(dig(e, "data.list"))
                if isinstance(p, dict)
                and (p.get("trader") is True or "Trader" in str(p.get("kind") or ""))]

    # WILLINGNESS IS PER-TRADER AND THE FIXTURE MUST MEASURE IT, NOT ASSUME IT.
    # This used to take `traders[0]` and hand it to `trade-start`. On 2026-09-01
    # a run skipped at 3.2pre with gate `cannot-trade-now` while a perfectly
    # willing caravan stood on the same map: the previous caravan had left, a
    # NEW one was still forming up, and it sorted first. `CanTradeNow` is a
    # property of one ITrader, so the fixture opens sessions until one takes —
    # which is what a player does, and it is the same "assert the state, do not
    # assume it" that `8b0b88f` taught the forbid check.
    #
    # A failed `trade-start` costs nothing (it refuses before SetupWith); a
    # SUCCEEDING one opens a session, so the loop stops at the first success and
    # close_any_session() covers every exit after it.
    def first_willing(cands):
        last = None
        for t in cands:
            env = send("trade-start", {"trader": t.get("id"), "negotiator": S["N"]})
            if dig(env, "data.ok") is True:
                return t, env
            last = env
        return None, last

    traders = [] if ARGS.dry_run else traders_now()
    picked, e = (traders[0] if traders else None, None) if ARGS.dry_run else (None, None)
    if not ARGS.dry_run:
        picked, e = first_willing(traders)
        if picked is None:
            note("3.1", "%s — staging with dev:incident "
                        '{def:"TraderCaravanArrival"} and advancing until one settles'
                        % ("no trader on the map" if not traders else
                           "%d trader(s) on the map, none willing to trade yet (last "
                           "gate: %s)" % (len(traders), dig(e, "data.gate"))))
            if not traders:
                staged("3.1s", "TraderCaravanArrival")
            for _ in range(8):
                a = send("advance", {"ticks": 2500, "max_tps": 400})
                if dig(a, "data.reason") == "dialog":
                    note("3.1x", "the advance HALTED on a dialog — clearing it with "
                                 "dialog-dismiss, which is exactly what this spec exists to do")
                    send("dialog-dismiss", {"all": True})
                picked, e = first_willing(traders_now())
                if picked is not None:
                    break
    precondition("3.1a", "a trader willing to trade now",
                 ARGS.dry_run or picked is not None,
                 'dev:incident {def:"TraderCaravanArrival"} + 8 x 2500 ticks produced no '
                 "WILLING trader (last gate: %s). A caravan can take most of a day to "
                 "arrive and settle and refuses while forming up or leaving; advance "
                 'further, or stage an orbital trader (dev:incident '
                 '{def:"OrbitalTraderArrival"}) and use the comms route in phase 5.'
                 % (None if ARGS.dry_run else dig(e, "data.gate")))
    S["trader"] = 2001 if ARGS.dry_run else picked.get("id")
    print("  %strader = %s%s" % (DIM, S["trader"], OFF))

    # 3.2 trade-start. The verb reproduces the widget gates and does NOT take
    #     the job — which is the decision this spec records in DESIGN. The
    #     session is ALREADY OPEN: `first_willing` above opened it, and its
    #     envelope is what the checks below read, so the phase does not pay a
    #     second `trade-start` (which would refuse at gate `session-open`).
    if ARGS.dry_run:
        e = send("trade-start", {"trader": S["trader"], "negotiator": S["N"]})
    eq("3.2a", "trade-start opened a session", e, "data.ok", True)
    eq("3.2b", "the session is active", e, "data.session.active", True)
    not_null("3.2c", "the trader is named", e, "data.session.trader")
    # THE INVARIANT THIS SPEC IS ABOUT: a trade with no window on the stack —
    # RETARGETED 2026-08-31, because until then it ASSERTED NOTHING. `trade-start`
    # does not publish a `force_pause` block at all: the only publishers in the
    # mod are `InteractionVerbs.Interactions` and `DialogVerbs`, and
    # `TradeVerbs.TradeStart` / `PawnActs.TradeStart` is not among them. The key
    # was therefore ABSENT, `dig()` returns its default for an absent key exactly
    # as it does for a present-and-null one, and `eq(..., 0)` was reading a hole
    # — which is why it reported `actual: null` against `expected: 0`.
    # It is now measured on `interactions`, the verb that DOES publish the block
    # and whose shape phase 0 proves at 0.5a/0.5b. Deleting it was the other
    # option — 3.3 already proves the same invariant through
    # `status.forcePause` — but two independent routes to "no window was
    # raised" is the point of a spec that is ABOUT the window stack, and this one
    # reads the richer of the two surfaces.
    fp = send("interactions")
    eq("3.2d", "ZERO force-pausing windows after trade-start — the deal is "
               "transacted, not driven", fp, "data.force_pause.count", 0)
    eq("3.2e", "and the verb says the negotiator did NOT walk",
       e, "data.negotiator_walked", False)
    not_null("3.2f", "the scribed-thing-id cost of opening a session is DISCLOSED",
             e, "data.session_cost.scribed_thing_id")
    not_null("3.2g", "journaled", e, "data.action.journal_seq")
    ge("3.2h", "the deal has tradeables", e, "data.tradeables_total", 1)
    # 0.11's deferred PRESENCE half, at the first envelope that can carry it.
    # 3.7i digs the first of these and 3.9g is built on the other two.
    shape("3.2i", "trade-start", e, "data.totals.colony_silver", NUM)
    shape("3.2j", "trade-start", e, "data.totals.colony_silver_post_deal", NUM)
    shape("3.2k", "trade-start", e, "data.totals.trader_silver", NUM)
    S["silver0"] = dig(e, "data.totals.colony_silver") or 0
    print("  %scolony silver at open = %s%s" % (DIM, S["silver0"], OFF))

    # 3.3 THE STACK IS STILL CLEAR — asserted independently, through status.
    stack_clear("3.3", "no Dialog_Trade was stacked (status.forcePause absent)")

    # 3.4 the summary read. Find something to BUY and something to SELL.
    e = send("trade")
    eq("3.4a", "trade summarised the session", e, "data.ok", True)
    contains("3.4b", "and states the sign convention in the game's own words",
             dig(e, "data.sign_note"), "POSITIVE BUYS")
    rows = [r for r in as_list(dig(e, "data.tradeables")) if isinstance(r, dict)]

    def usable(r):
        return (r.get("trader_will_trade") is True and r.get("interactive") is True
                and r.get("is_currency") is not True)

    buyable = [r for r in rows if usable(r) and (r.get("trader_has") or 0) >= 10]
    sellable = [r for r in rows if usable(r) and (r.get("colony_has") or 0) >= 5]
    precondition("3.4c", "something the trader has 10+ of, to buy",
                 ARGS.dry_run or len(buyable) >= 1,
                 'the trader stocks nothing in quantity 10+. The bullet says "10 meals"; '
                 "any 10-stack works. Re-run against a caravan with real stock.")
    precondition("3.4d", "something the colony has 5+ of, to sell",
                 ARGS.dry_run or len(sellable) >= 1,
                 "the colony has nothing the trader counts as colony stock. THE SCOPE IS "
                 "NOT A RADIUS, and calling it one costs a staging round: "
                 "`RimWorld/Pawn_TraderTracker.ColonyThingsWillingToBuy` filters "
                 "`map.listerThings.AllThings` on `def.category == Item && "
                 "TradeUtility.PlayerSellableNow && !Position.Fogged && "
                 "(map.areaManager.Home[pos] || IsInAnyStorage()) && ReachableForTrade`. "
                 "So the test is THE HOME AREA OR A STOCKPILE — distance to the trader "
                 "does not enter it. Measured 2026-09-01: 2000 silver spawned ON the "
                 "trader's own cell counted as ZERO, and the same 2000 spawned at the "
                 "negotiator counted in full. Stage with `dev:spawn-thing "
                 "{pos:\"pawn:<negotiator>\"}`, or paint home with "
                 "`area {kind:\"home\", op:\"add\", rect:[...]}`.")
    # THE TWO SIDES MUST BE DIFFERENT DEFS, and this is not a tidiness
    # preference — it is what makes the phase capable of failing.
    # `RimWorld/Transferable.CountToTransfer` is ONE signed field per row and
    # `RimWorld/Tradeable.ActionToDo` derives the side from its sign (None at 0,
    # PlayerSells when CountToTransferToDestination > 0, else PlayerBuys). So a
    # def that is bought AND sold is a SINGLE row, the two writes collapse, and
    # the last one wins. On 2026-08-31 `buyable[0]` and `sellable[0]` were both
    # ComponentIndustrial: the driver asked to buy 10 and sell 5, the row ended
    # at -5, colony stock went 50 -> 45, the buy never happened, and 3.9h and
    # 3.9i were reading THE SAME `data.after` object — `TradeVerbs.TradeConfirm`
    # keys `after` by ThingDef (`t2.ThingDef == def`), so one row answered both.
    #
    # The tell, preserved as check 3.6h: `lines_with_action` was 2, which was the
    # single item row PLUS THE SILVER ROW, not two item rows.
    #
    # Uniquely-named rows are preferred on top of distinctness, because 3.8
    # re-addresses by defName after the Reset and `data.after` is keyed by
    # ThingDef — a def carried by two rows (same def, different stuff) is
    # ambiguous to both. It is a preference, not a requirement: distinctness is
    # the hard constraint and the search below only breaks ties with it.
    if ARGS.dry_run:
        S["buy"] = {"index": 3, "thing": "MealSimple", "trader_has": 30, "colony_has": 0}
        S["sell"] = {"index": 7, "thing": "Apparel_Shirt", "colony_has": 12, "trader_has": 0}
    else:
        seen = {}
        for r in rows:
            seen[r.get("thing")] = seen.get(r.get("thing"), 0) + 1

        def solo(r):
            return seen.get(r.get("thing")) == 1

        best = None
        for b in buyable:
            for s in sellable:
                if s.get("thing") == b.get("thing"):
                    continue
                score = (0 if solo(b) else 1) + (0 if solo(s) else 1)
                if best is None or score < best[0]:
                    best = (score, b, s)
            if best is not None and best[0] == 0:
                break
        if best is None:
            note("3.4e", "every sellable row carries the SAME def as every buyable one "
                         "(%s). One def is ONE Tradeable row and CountToTransfer is a "
                         "single signed field, so the buy and the sell would collapse "
                         "into each other and this phase would prove nothing."
                         % buyable[0].get("thing"))
            precondition("3.4e", "a sellable def DISTINCT from the buyable one",
                         False,
                         "%d buyable row(s), %d sellable row(s), and no pair of them "
                         "names two different defs. Stage a second kind of good inside "
                         "the trader's radius (dev:spawn-thing) and re-run. This is a "
                         "FIXTURE gap, not a 3.5 failure."
                         % (len(buyable), len(sellable)))
        S["buy"], S["sell"] = best[1], best[2]
        if best[0]:
            note("3.4e", "the chosen pair is distinct but not uniquely named (%d of the "
                         "two defs appear on more than one row) — 3.8's defName "
                         "addressing may hit the ambiguity fallback" % best[0])
    print("  %sBUY  10 x %s (index %s, trader has %s)%s"
          % (DIM, S["buy"].get("thing"), S["buy"].get("index"),
             S["buy"].get("trader_has"), OFF))
    print("  %sSELL  5 x %s (index %s, colony has %s)%s"
          % (DIM, S["sell"].get("thing"), S["sell"].get("index"),
             S["sell"].get("colony_has"), OFF))
    S["buyColony0"] = S["buy"].get("colony_has") or 0
    S["sellColony0"] = S["sell"].get("colony_has") or 0
    S["buyTrader0"] = S["buy"].get("trader_has") or 0
    S["sellTrader0"] = S["sell"].get("trader_has") or 0

    # 3.5 THE RED-ERROR GUARD on trade-set, AND IT TAKES TWO CALLS TO REACH IT.
    #     `RimWorld/Transferable.AdjustTo` Log.Errors "Failed to adjust
    #     transferable counts" when `CanAdjustTo` refuses, so `TradeVerbs
    #     .TradeSet`'s gate 4 calls that same predicate and rejects first. But
    #     read `RimWorld/Transferable.CanAdjustTo`: it returns WasAccepted when
    #     `destination == CountToTransfer`, returns WasAccepted AGAIN whenever
    #     `ClampAmount(destination) != CountToTransfer`, and only falls through
    #     to `OverflowReport()` when the clamp EQUALS the current count. From a
    #     row sitting at `CountToTransfer == 0`, asking for 10030 against a
    #     maximum of 31 clamps to 31, and 31 != 0, SO THE GAME ITSELF ACCEPTS
    #     IT and `AdjustTo` silently writes the clamped 31.
    #
    #     The gate is therefore CORRECT and simply never entered, which is
    #     exactly why the 2026-08-31 run failed 3.5a-3.5d while 3.5's red-error
    #     invariant PASSED — there was no red error to catch, because the game
    #     was never asked for anything it considered out of range. Those checks
    #     were unreachable by construction, not evidence of a defect.
    #
    #     Sending the SAME line a SECOND time is what reaches the gate: by then
    #     `CountToTransfer` is 31, `ClampAmount(10030)` is 31, the clamp equals
    #     the count, and the report is an overflow. Both halves are asserted —
    #     call one accepted WITH A CLAMP, call two refused by name.
    #
    #     CALL ONE IS ASSERTED AGAINST WHAT SHIPS TODAY. The accepted line is
    #     `TradeVerbs.TradeableLine` plus `was`, so it carries `count` (the
    #     clamped result) and `was` (the count before) and does NOT carry
    #     `requested`/`clamped`. git-bug 7e8c969 proposes adding them; when it
    #     lands, 3.5d/3.5e get an explicit echo to read instead of inferring the
    #     clamp from `count == max`. Do not write that assertion before the
    #     issue ships — a check that fails until an unrelated issue lands is a
    #     check nobody can act on.
    over = (S["buy"].get("trader_has") or 0) + 9999

    # ---- call one: the game accepts it, and clamps ----
    e = send("trade-set", {"index": S["buy"].get("index"), "buy": over})
    shape("3.5a", "trade-set", e, "data.accepted.0.was", NUM)
    shape("3.5b", "trade-set", e, "data.accepted.0.max", NUM)
    eq("3.5c", "the FIRST out-of-range line is ACCEPTED — Transferable.CanAdjustTo "
               "returns WasAccepted while ClampAmount(dest) != CountToTransfer",
       e, "data.ok", True)
    eq("3.5d", "and it started from a ZERO row, which is WHY gate 4 could not fire",
       e, "data.accepted.0.was", 0)
    acc_count = dig(e, "data.accepted.0.count")
    acc_max = dig(e, "data.accepted.0.max")
    check("3.5e", "so AdjustTo SILENTLY CLAMPED it to the game's own maximum "
                  "(accepted.0.count == accepted.0.max, both far below the %d asked "
                  "for)" % over,
          is_num(acc_count) and is_num(acc_max) and acc_count == acc_max
          and acc_count < over,
          "count == max, and < %d" % over,
          {"count": acc_count, "max": acc_max, "requested": over})
    not_null("3.5f", "a clamp IS a write, so this call DID journal",
             e, "data.action.journal_seq")

    # ---- call two: the identical line, now refused ----
    e = send("trade-set", {"index": S["buy"].get("index"), "buy": over})
    shape("3.5g", "trade-set", e, "data.rejected.0.requested", NUM)
    # Proved PRESENT so 3.5m is not an eq(..., None) against a hole — the 3.2d
    # lesson. `PawnActs.NoStamp` publishes `journal_seq: null`.
    shape("3.5h", "trade-set", e, "data.action.journal_seq")
    eq("3.5i", "the SECOND, IDENTICAL line is REFUSED — ClampAmount(dest) now EQUALS "
               "CountToTransfer, so CanAdjustTo reaches OverflowReport()",
       e, "data.ok", False)
    eq("3.5j", "and names the gate", e, "data.rejected.0.gate", "out-of-range")
    rej_max = dig(e, "data.rejected.0.max")
    check("3.5k", "with the game's own bounds echoed — and the max it names is exactly "
                  "the count call one was clamped to (data.rejected.0.max)",
          is_num(rej_max) and rej_max == acc_count,
          show(acc_count), rej_max)
    eq("3.5l", "and the request echoed back verbatim", e, "data.rejected.0.requested",
       over)
    eq("3.5m", "nothing was journaled for a wholly refused call",
       e, "data.action.journal_seq", None)
    no_red_errors("3.5n", 'THE INVARIANT: no "Failed to adjust transferable counts" '
                          "red error, across BOTH calls")

    # 3.6 THE PLURAL FORM IS THE VERB — buy and sell in ONE call.
    e = send("trade-set", {"items": [
        {"index": S["buy"].get("index"), "buy": 10},
        {"index": S["sell"].get("index"), "sell": 5},
    ]})
    eq("3.6a", "trade-set accepted both lines in ONE call", e, "data.ok", True)
    eq_val("3.6b", "two accepted (data.accepted)",
           len(as_list(dig(e, "data.accepted"))), 2)
    eq_val("3.6c", "none rejected (data.rejected)",
           len(as_list(dig(e, "data.rejected"))), 0)
    # POSITIVE BUYS, NEGATIVE SELLS — the game's own convention, asserted.
    eq("3.6d", "the buy line reads +10", e, "data.accepted.0.count", 10)
    eq("3.6e", "and its action is PlayerBuys", e, "data.accepted.0.action", "PlayerBuys")
    eq("3.6f", "the sell line reads -5", e, "data.accepted.1.count", -5)
    eq("3.6g", "and its action is PlayerSells", e, "data.accepted.1.action", "PlayerSells")
    # THREE, NOT TWO — the two item rows PLUS the silver row.
    # `TradeVerbs.AddDealTotals` counts every tradeable whose
    # `RimWorld/Tradeable.ActionToDo` is not None, and `trade-set` ends in
    # `deal.UpdateCurrencyCount()`, which gives the currency row a non-zero
    # count. On 2026-08-31 this read 2 and PASSED while the buy had been
    # silently overwritten by the sell — that 2 was one item row plus silver.
    # Demanding 3 is what makes a future collapse visible instead of letting it
    # hide behind a count that happens to match. (The one benign way it can be
    # 2 is a deal whose net cost rounds to zero: `Tradeable.ActionToDo` returns
    # None at CountToTransfer == 0, so the silver row drops out.)
    eq("3.6h", "THREE lines carry an action — the buy, the sell, AND the silver row",
       e, "data.totals.lines_with_action", 3)
    not_null("3.6i", "journaled", e, "data.action.journal_seq")
    stack_clear("3.6j", "still no window on the stack")

    # 3.7 CANCEL LEAVES STATE UNTOUCHED — and "untouched" is DEFINED.
    #     Defined as: no Tradeable.ResolveTrade ran; colony silver and stock
    #     counts unchanged. NOT as "the statics are pristine", because
    #     TradeSession.Close() is `trader = null;` and nothing else.
    e = send("trade-cancel")
    eq("3.7a", "trade-cancel closed the session", e, "data.ok", True)
    eq("3.7b", "and TradeSession.Active is now false", e, "data.session_closed", True)
    # Three again, and for the same reason: `TradeVerbs.TradeCancel` walks
    # `deal.AllTradeables` and keeps every row whose ActionToDo is not None, the
    # currency row included. This read 2 and passed on 2026-08-31 — one item row
    # plus silver, not the two item rows the label claimed.
    eq_val("3.7c", "all THREE staged lines are reported as abandoned, silver row "
                   "included (data.abandoned)",
           len(as_list(dig(e, "data.abandoned"))), 3)
    contains("3.7d", 'and "untouched" is DEFINED in the result, not left to inference',
             dig(e, "data.untouched_means"), "ResolveTrade")
    not_null("3.7e", "journaled", e, "data.action.journal_seq")
    # The definition, ASSERTED: reopen and compare the counts.
    e = send("trade")
    eq("3.7f", "reading a closed session is refused, not an NRE", e, "data.ok", False)
    eq("3.7g", "and names the gate", e, "data.gate", "no-session")
    e = send("trade-start", {"trader": S["trader"], "negotiator": S["N"]})
    eq("3.7h", "a fresh session opens", e, "data.ok", True)
    eq("3.7i", "CANCEL WAS UNTOUCHED — colony silver is exactly what it was",
       e, "data.totals.colony_silver", S["silver0"])
    rows = [r for r in as_list(dig(e, "data.tradeables")) if isinstance(r, dict)]
    buy_row = [r for r in rows if r.get("thing") == S["buy"].get("thing")]
    sell_row = [r for r in rows if r.get("thing") == S["sell"].get("thing")]
    if buy_row:
        check("3.7j", "CANCEL WAS UNTOUCHED — trader stock unchanged",
              buy_row[0].get("trader_has") == S["buyTrader0"],
              S["buyTrader0"], buy_row[0].get("trader_has"))
    if sell_row:
        check("3.7k", "CANCEL WAS UNTOUCHED — colony stock unchanged",
              sell_row[0].get("colony_has") == S["sellColony0"],
              S["sellColony0"], sell_row[0].get("colony_has"))

    # 3.8 re-stage and CONFIRM. Indexes are NOT stable across a Reset, so
    #     address by defName this time — `trade` says so in index_note.
    e = send("trade-set", {"items": [
        {"thing": S["buy"].get("thing"), "buy": 10},
        {"thing": S["sell"].get("thing"), "sell": 5},
    ]})
    if dig(e, "data.ok") is not True and buy_row and sell_row:
        note("3.8", "defName addressing hit an ambiguity (%s) — falling back to the "
                    "fresh indexes, which is exactly what the ambiguity message tells "
                    "the caller to do" % dig(e, "data.rejected.0.reason"))
        e = send("trade-set", {"items": [
            {"index": buy_row[0].get("index"), "buy": 10},
            {"index": sell_row[0].get("index"), "sell": 5},
        ]})
    eq("3.8a", "both lines staged again", e, "data.ok", True)
    # 0.11's deferred PRESENCE half again, on the envelope 3.9g is built from.
    shape("3.8a1", "trade-set", e, "data.totals.colony_silver", NUM)
    shape("3.8a2", "trade-set", e, "data.totals.colony_silver_post_deal", NUM)
    shape("3.8a3", "trade-set", e, "data.totals.trader_silver", NUM)

    # THE PRE-CONFIRM SNAPSHOT. Taken from whichever `trade-set` last touched
    # the deal — 3.8f below re-stages it, and a snapshot taken only here would
    # be stale by the time 3.9g read it.
    def map_total(defname):
        """Every stack of `defname` on the map, summed — the read that is
        independent of TradeDeal entirely."""
        if ARGS.dry_run or not defname:
            return 0
        env = send("things", {"def": defname, "cap": 5})
        total = 0
        for r in as_list(dig(env, "data.rollups")):
            if isinstance(r, dict) and r.get("def") == defname:
                total += r.get("count") or 0
        return total

    def snap(env):
        S["buyValue"] = dig(env, "data.totals.buy_value") or 0
        S["sellValue"] = dig(env, "data.totals.sell_value") or 0
        S["silverPre"] = dig(env, "data.totals.colony_silver") or 0
        S["silverPost"] = dig(env, "data.totals.colony_silver_post_deal")
        S["traderSilver"] = dig(env, "data.totals.trader_silver")
        print("  %sbuy value %s, sell value %s; colony silver %s -> post-deal %s "
              "(the trader holds %s)%s"
              % (DIM, S["buyValue"], S["sellValue"], S["silverPre"],
                 show(S["silverPost"]), show(S["traderSilver"]), OFF))

    snap(e)

    # THE PHYSICAL BASELINE. `data.after` is deal-scoped on the colony side (see
    # 3.9g below), so "the goods arrived" has to be measured off the MAP, not
    # off the deal. `things {def}` rolls up every stack of that def on the map
    # regardless of area, forbidden flag or owner, which is exactly the read the
    # deal's own colony-side count cannot make.
    S["buyMap0"] = map_total(S["buy"].get("thing"))
    print("  %smap total of %s before confirm = %s%s"
          % (DIM, S["buy"].get("thing"), show(S["buyMap0"]), OFF))

    e = send("trade-confirm")
    if dig(e, "data.gate") == "colony-cannot-afford":
        # THE NRE THAT WOULD HAVE FIRED. This branch is a PASS, not a failure:
        # it proves the pre-check pre-empted TradeDeal.TryExecute's
        # WindowOfType<Dialog_Trade>().FlashSilver().
        eq("3.8b", "THE NRE GUARD held: cannot-afford was pre-empted, not entered",
           e, "data.gate", "colony-cannot-afford")
        contains("3.8c", "and the citation names the unguarded line",
                 dig(e, "data.gate_cite"), "FlashSilver")
        contains("3.8d", "the session is still open and nothing moved",
                 dig(e, "data.note"), "NOTHING was transacted")
        no_red_errors("3.8e", "and no NRE reached the log")
        note("3.8f", "the colony cannot afford 10 of that item — reducing the buy to "
                     "what the silver covers and retrying")
        snap(send("trade-set", {"items": [{"thing": S["buy"].get("thing"), "buy": 1}]}))
        e = send("trade-confirm")
    if dig(e, "data.gate") == "trader-short-funds":
        # Remembered for 3.9g: TryExecute runs `LimitCurrencyCountToFunds()`,
        # which caps what the colony can actually be paid.
        S["shortFunds"] = True
        eq("3.8g", "THE CONFIRMATION MODAL became an argument, not a window",
           e, "data.gate", "trader-short-funds")
        contains("3.8h", "and cites the Dialog_MessageBox it replaced",
                 dig(e, "data.gate_cite"), "ConfirmTraderShortFunds")
        stack_clear("3.8i", "no confirmation window was stacked")
        e = send("trade-confirm", {"allow_trader_short_funds": True})
    eq("3.9a", "trade-confirm executed", e, "data.ok", True)
    eq("3.9b", "and something actually moved", e, "data.actually_traded", True)
    eq("3.9c", "the session was closed by the verb (TradeSession.Close has no vanilla "
               "caller)", e, "data.session_closed", True)
    not_null("3.9d", "journaled", e, "data.action.journal_seq")
    not_null("3.9e", "the transacted lines are echoed as evidence", e, "data.transacted")
    not_null("3.9f", "and the post-trade counts are read BACK from the rebuilt deal",
             e, "data.after")
    # SILVER AND STOCK VERIFIED CORRECT — the bullet's own words.
    # SILVER VERIFIED AGAINST THE DEAL'S OWN POST-DEAL NUMBER — because
    # `sell_value - buy_value` IS STRUCTURALLY ZERO and the old expectation
    # could therefore never have been wrong. `TradeVerbs.AddDealTotals` sums
    # `CurTotalCurrencyCostForSource`/`ForDestination` over EVERY tradeable with
    # an action, and the silver row has one — whereas
    # `RimWorld/TradeDeal.UpdateCurrencyCount` sums only the rows where
    # `!tradeable.IsCurrency` and then `ForceToSource`s the silver row to absorb
    # exactly that difference. So the currency row makes the two totals equal by
    # construction. On 2026-08-31 it read sell(98.9) - buy(99) = 0 against a
    # real delta of 99, and would have read 0 for any deal that balances.
    #
    # `RimWorld/Tradeable.CountPostDealFor(Transactor.Colony)` is
    # `CountHeldBy(colony) + CountToTransferToSource`, read off the currency row
    # AFTER `trade-set`'s own `UpdateCurrencyCount()` — the same function
    # `TradeDeal.TryExecute` re-runs before resolving — so this is exact integer
    # arithmetic against the number the deal itself promised, not an estimate.
    delta = dig(e, "data.colony_silver_delta")
    expect = None
    if is_num(S.get("silverPost")):
        expect = S["silverPost"] - S["silverPre"]
        # ...unless the trader could not cover it. `TradeDeal.TryExecute` calls
        # `LimitCurrencyCountToFunds()`, which ForceToSource()s the currency row
        # down to `CountHeldBy(Transactor.Trader)` — the branch 3.8g took.
        if S.get("shortFunds") and expect > 0 and is_num(S.get("traderSilver")):
            expect = min(expect, S["traderSilver"])
    print("  %ssilver %s -> %s (delta %s); the deal's own pre-confirm post-deal "
          "number promised %s%s"
          % (DIM, S["silverPre"], show(dig(e, "data.colony_silver_after")),
             show(delta), show(expect), OFF))
    after = [r for r in as_list(dig(e, "data.after")) if isinstance(r, dict)]
    after_buy = [r for r in after if r.get("thing") == S["buy"].get("thing")]
    after_sell = [r for r in after if r.get("thing") == S["sell"].get("thing")]
    txn = [r for r in as_list(dig(e, "data.transacted")) if isinstance(r, dict)]
    txn_silver = [r for r in txn if r.get("is_currency") is True]
    txn_buy = [r for r in txn if r.get("thing") == S["buy"].get("thing")]
    after_silver = [r for r in after if r.get("thing") == "Silver"]

    # THE COLONY-SIDE NUMBER DOES NOT MOVE ON A BUY, AND THAT IS CORRECT.
    # Retargeted 2026-09-01 after the first live run of this phase since the
    # driver was repaired. 3.9g used to read
    # `data.colony_silver_delta == colony_silver_post_deal - colony_silver` and
    # FAILED with delta 0 against a promise of 83 — while `trader_now` had moved
    # 972 -> 889, exactly the 83. Both numbers were right; the assertion was
    # wrong, and it is the eighth defect of the family the 2026-08-31 round
    # fixed seven of.
    #
    # `RimWorld/Pawn_TraderTracker.GiveSoldThingToPlayer` places a bought thing
    # with `GenPlace.TryPlaceThing(thing, toGive.PositionHeld, mapHeld,
    # ThingPlaceMode.Near)` — at the CARAVAN CARRIER's own cell, NOT near the
    # negotiator — and then `pawn.GetLord()?.extraForbiddenThings.Add(thing)`.
    # Meanwhile the colony-side count of the rebuilt deal is
    # `ColonyThingsWillingToBuy`, whose test is `Home[pos] || IsInAnyStorage()`.
    # A caravan parked outside the home area therefore satisfies neither, so
    # bought goods are legitimately not colony stock until somebody HAULS them.
    # Vanilla's own Dialog_Trade prints the same optimistic post-deal column, so
    # this is a display convention of the game's, not a mod defect.
    #
    # (The `extraForbiddenThings` add does NOT forbid them to colonists:
    # `RimWorld/ForbidUtility.IsForbidden(Thing, Pawn)` consults
    # `pawn.GetLord()`, the lord of the pawn ASKING, and a colonist has none.
    # It stops the caravan re-collecting what it just sold. Verified live: both
    # delivered stacks read `forbidden: false`.)
    #
    # So the promise is checked where it is decidable — against the currency
    # line the deal actually executed — and DELIVERY is checked on the trader's
    # side and on the map, neither of which is deal-scoped.
    silver_moved = txn_silver[0].get("count") if txn_silver else None
    check("3.9g", "BULLET 1 — the deal's promised colony delta IS the currency line "
                  "it executed (post_deal - colony_silver == transacted silver count)",
          is_num(expect) and is_num(silver_moved) and expect == silver_moved,
          "transacted silver count == %s" % show(expect), silver_moved)
    trader_silver_now = after_silver[0].get("trader_now") if after_silver else None
    want_trader_silver = (S["traderSilver"] - silver_moved
                          if is_num(S.get("traderSilver")) and is_num(silver_moved)
                          else None)
    check("3.9g1", "BULLET 1 — THE MONEY MOVED: the trader's silver fell by exactly "
                   "the currency line (the side of the trade that is not deal-scoped)",
          is_num(trader_silver_now) and is_num(want_trader_silver)
          and trader_silver_now == want_trader_silver,
          "trader_now == %s" % show(want_trader_silver), trader_silver_now)
    # AND THE ASYMMETRY, WHICH IS THE WHOLE POINT AND IS DIRECTIONAL. Measured
    # across three consecutive live deals on 2026-09-01: a deal that PAID 360
    # silver moved the colony-side count by exactly -360; a deal that RECEIVED
    # 83 moved it by 0; a deal that RECEIVED 80 moved it by 69. All three are
    # correct, and the third is why this check is a BOUND and not an equality.
    #
    # OUTFLOW is exact because the stacks are taken FROM colony scope.
    # INFLOW is not, and it is not all-or-nothing either:
    # `RimWorld/Tradeable.ResolveTrade` hands the buy to
    # `TransferableUtility.TransferNoSplit(thingsTrader, …)`, which walks the
    # trader's stack LIST and calls `GiveSoldThingToPlayer` once per stack —
    # each landing at THAT stack's own `PositionHeld`, i.e. at whichever caravan
    # member was carrying it. Different carriers stand in different cells, so
    # the fraction that lands inside the home area is however many of them
    # happened to be standing in it. 69 of 80 is what that looks like.
    #
    # So the strongest TRUE statement about an inflow is that it is between
    # nothing and all of it, and the fraction is reported rather than asserted.
    if is_num(silver_moved) and silver_moved < 0:
        check("3.9g2", "and an OUTFLOW lands exactly — silver the colony pays is taken "
                       "FROM colony scope, so the deal-scoped count is authoritative",
              is_num(delta) and delta == silver_moved,
              "colony_silver_delta == %s" % show(silver_moved), delta)
    else:
        check("3.9g2", "and an INFLOW is bounded by the line, never beyond it — it is "
                       "placed per-stack at each carrier's own cell, so what counts is "
                       "however much of the caravan stood inside the home area",
              is_num(delta) and is_num(silver_moved) and 0 <= delta <= silver_moved,
              "0 <= colony_silver_delta <= %s" % show(silver_moved), delta)
        if is_num(delta) and is_num(silver_moved) and silver_moved:
            print("  %s%s of %s silver landed inside the colony's own scope; the rest is "
                  "at the caravan and needs hauling%s"
                  % (DIM, show(delta), show(silver_moved), OFF))
    # A MISSING ROW IS A FAILURE, NOT A SKIP. These two used to sit behind bare
    # `if after_buy:` / `if after_sell:` guards, so an absent row made the check
    # vanish from the run entirely — no PASS, no FAIL, no mention in the RESULT
    # line. That is the same green-while-asserting-nothing shape as 3.2d, one
    # level up: an assertion that did not run reads exactly like one that
    # passed. `data.after` is keyed by ThingDef, so with the buy and sell defs
    # now distinct (see 3.4) both rows must be there.
    # THE BOUGHT STOCK, PROVED TWICE AND ON NEITHER DEAL-SCOPED NUMBER. Same
    # retarget as 3.9g: this used to assert `colony_now > buyColony0` and read
    # 63 against 63 while the trader's own stock had fallen 103 -> 93 and a
    # brand-new 10-stack sat on the map at the caravan's cell.
    if after_buy and txn_buy:
        bought = txn_buy[0].get("count")
        want_trader = ((txn_buy[0].get("trader_has") or 0) - (bought or 0))
        check("3.9h", "BULLET 1 — THE GOODS MOVED: the trader's stock of the bought "
                      "def fell by exactly the line",
              is_num(bought) and after_buy[0].get("trader_now") == want_trader,
              "trader_now == %s" % show(want_trader), after_buy[0].get("trader_now"))
        after_map = map_total(S["buy"].get("thing"))
        check("3.9h1", "BULLET 1 — and it EXISTS: an independent `things {def}` read "
                       "over the whole map is up by exactly the bought count",
              is_num(bought) and is_num(after_map) and is_num(S.get("buyMap0"))
              and after_map == S["buyMap0"] + bought,
              "map total == %s + %s" % (show(S.get("buyMap0")), show(bought)), after_map)
        # HOW MUCH of it counts as colony stock is a function of where each
        # carrier was standing (see 3.9g2), so the bound is the assertion and
        # the fraction is reported. What it may NOT do is exceed the line or go
        # negative — either would mean the rebuilt deal is counting something
        # this trade did not move.
        colony_moved = ((after_buy[0].get("colony_now") or 0) - S["buyColony0"])
        check("3.9h2", "and the colony-side count moved by between nothing and the "
                       "whole line — the fraction is however much of the caravan stood "
                       "inside the home area, and is not the deal's to promise",
              is_num(bought) and 0 <= colony_moved <= bought,
              "0 <= colony_now - %s <= %s" % (S["buyColony0"], show(bought)),
              colony_moved)
        print("  %s%s of %s of the bought stock landed inside the colony's own scope; "
              "the rest is at the caravan and needs hauling%s"
              % (DIM, show(colony_moved), show(bought), OFF))
    else:
        check("3.9h", "BULLET 1 — THE GOODS MOVED: the trader's stock of the bought "
                      "def fell by exactly the line", False,
              "a `data.after` AND a `data.transacted` row for the bought def %s"
              % S["buy"].get("thing"),
              {"after": [r.get("thing") for r in after],
               "transacted": [r.get("thing") for r in txn]})
    if after_sell:
        check("3.9i", "BULLET 1 — the sold stock left the colony",
              (after_sell[0].get("colony_now") or 0) < S["sellColony0"],
              "< %s" % S["sellColony0"], after_sell[0].get("colony_now"))
    else:
        check("3.9i", "BULLET 1 — the sold stock left the colony", False,
              "a `data.after` row for the sold def %s" % S["sell"].get("thing"),
              [r.get("thing") for r in after])
    stack_clear("3.9j", "THE INVARIANT: the whole trade ran with no window on the stack")
    no_red_errors("3.9k", "zero red errors across the whole trade")


# ------------------------------------------------------------------- phase 4 --
# 3.5 ACCEPTANCE BULLET 3, plus amendment #2's real requirement: after answering,
# the run is UN-WEDGED.

def phase4():
    banner("PHASE 4 — 3.5 BULLET 3: an unknown dialog shows opaque, dismisses cleanly, "
           "and the run un-wedges")

    # 4.1 the read surface, first. `interactions` (2.4) is the list this spec's
    #     verbs address into; the three tiers are its contract.
    e = send("interactions")
    eq("4.1a", "interactions answered", e, "ok", True)
    not_null("4.1b", "it carries 1.7's force-pause payload verbatim", e, "data.force_pause")
    eq("4.1c", "and the stack is clear right now", e, "data.force_pause.count", 0)

    # 4.2 STAGE A FORCE-PAUSING DIALOG. 1.7 shipped the dev-gated escape hatch
    #     for exactly this, and its own comment says it is for fixtures ONLY —
    #     the real dismiss/choose verbs are 3.5's contract, which is what we are
    #     about to test. A timing-out letter opening ITSELF mid-advance is the
    #     production path; this is the deterministic stand-in.
    e = send("journal-selftest", {"steps": ["dialogs-clear"]})
    if dig(e, "ok") is not True and "devMode" in str(dig(e, "error.detail") or ""):
        precondition("4.2d", "devMode for journal-selftest", False,
                     "journal-selftest is gated on Prefs.DevMode and refused. Phase 0.3 "
                     "should have caught this — if it did not, the switch was turned off "
                     "mid-run.")
    note("4.2", "journal-selftest {steps:['dialogs-clear']} -> %s. If this bench's "
                "selftest has no dialog fixture, the phase falls through to the letter "
                "route below." % dig(e, "ok"))

    # 4.3 the production route: a real letter with real options.
    def choice_letters():
        env = send("interactions")
        return [l for l in as_list(dig(env, "data.letters"))
                if isinstance(l, dict) and l.get("kind") == "choice"
                and len(as_list(l.get("options"))) >= 1]

    choice = [] if ARGS.dry_run else choice_letters()
    if not choice and not ARGS.dry_run:
        note("4.3", 'no choice letter on the stack — staging one with dev:incident '
                    '{def:"VisitorGroup"} (ChoiceLetter_AcceptVisitors) and advancing')
        staged("4.3s", "VisitorGroup")
        send("advance", {"ticks": 600, "max_tps": 400})
        choice = choice_letters()
    precondition("4.3a", "a choice letter with at least one option",
                 ARGS.dry_run or len(choice) >= 1,
                 "no ChoiceLetter is on the stack. Stage one: dev:incident "
                 '{def:"VisitorGroup"} or {def:"TravelerGroup"} both send '
                 "ChoiceLetter_AcceptVisitors; a raid sends a plain letter, which has no "
                 "options.")
    L = ({"id": 42, "label": "<letter>",
          "options": [{"index": 0, "label": "Close", "disabled": False}]}
         if ARGS.dry_run else choice[0])
    S["L"] = L.get("id")
    print("  %sletter %s: %s — options: %s%s"
          % (DIM, S["L"], L.get("label"),
             " | ".join(str(o.get("label")) for o in as_list(L.get("options"))), OFF))

    # 4.4 letter-read is the drill-down; its option indexes ARE 2.4's.
    e = send("letter-read", {"letter": S["L"]})
    eq("4.4a", "letter-read answered", e, "ok", True)
    eq("4.4b", "the index space matches interactions exactly", e, "data.options.0.index", 0)
    not_null("4.4c", "and the label is the literal words on the button",
             e, "data.options.0.label")
    eq("4.4d", "read via the backing field, and it says so", e, "data.source",
       "backing-field")

    # 4.5 THE DISABLED GATE. DiaOption.Activate() does NOT check `disabled` —
    #     the UI checks it one line earlier as an argument to Widgets.ButtonText.
    disabled = [o for o in as_list(L.get("options"))
                if isinstance(o, dict) and o.get("disabled") is True]
    if disabled:
        e = send("letter-choose", {"letter": S["L"], "option": disabled[0].get("index")})
        eq("4.5a", "a DISABLED option is refused", e, "data.ok", False)
        eq("4.5b", "and names the gate", e, "data.gate", "option-disabled")
        contains("4.5c", "citing the Widgets.ButtonText argument that IS the gate",
                 dig(e, "data.gate_cite"), "ButtonText")
        eq("4.5d", "nothing was journaled", e, "data.action.journal_seq", None)
    else:
        note("4.5", "no disabled option on this letter — the disabled gate is exercised "
                    "in phase 5 instead (FactionDialogMaker disables MustBeAlly / "
                    "WaitTime / BadTemperature constantly)")

    # 4.6 OPEN THE LETTER, which is what wedges a run. The production trigger is
    #     a timing-out letter calling OpenLetter on itself from LetterStackTick;
    #     `advance` is what drives that, so advance until something halts.
    wedged = False
    if not ARGS.dry_run:
        for _ in range(4):
            a = send("advance", {"ticks": 5000, "max_tps": 400})
            if dig(a, "data.reason") == "dialog":
                wedged = True
                eq("4.6a", "1.7 halted the advance on a dialog", a, "data.reason", "dialog")
                ge("4.6b", "and named the window(s)", a, "data.halted_on.count", 1)
                print("  %shalted on: %s%s"
                      % (DIM, ", ".join(str(w.get("type")) for w in
                                        as_list(dig(a, "data.halted_on.windows"))), OFF))
                break
    if not wedged and not ARGS.dry_run:
        note("4.6", "nothing opened a force-pausing dialog inside 20000 ticks. The "
                    "remaining checks in this phase need one; a timing-out letter "
                    "(ChoiceLetter_AcceptVisitors has a timeout) opens itself on its LAST "
                    "tick, so a longer advance will get there. Skipping to 4.9.")
    else:
        # 4.7 BULLET 3 — the window is REPORTED, with a kind, before it is answered.
        e = send("interactions")
        ge("4.7a", "interactions reports the force-pausing window",
           e, "data.force_pause.count", 1)
        eq("4.7b", "and blocking is true", e, "data.blocking", True)
        w = [x for x in as_list(dig(e, "data.windows"))
             if isinstance(x, dict) and x.get("force_pause") is True]
        check("4.7c", "BULLET 3 — the window carries {type, type_full, kind}",
              bool(w) and w[0].get("type") is not None
              and w[0].get("type_full") is not None and w[0].get("kind") is not None,
              "type + type_full + kind present", w)
        if w:
            print("  %swindow: %s kind=%s%s" % (DIM, w[0].get("type"), w[0].get("kind"), OFF))

        # 4.8 BULLET 3 — `dialog dismiss` must ALWAYS work, whatever the class.
        #     It is the esc-equivalent, and it must un-wedge the run.
        e = send("dialog-dismiss")
        eq("4.8a", "BULLET 3 — dialog-dismiss succeeded", e, "data.ok", True)
        eq("4.8b", "and TryRemove's bool was READ, not discarded",
           e, "data.dismissed.0.removed", True)
        contains("4.8c", "the result discloses that dismissal is NOT inert "
                         "(PostClose -> closeAction)", dig(e, "data.note"), "closeAction")
        # The waiver, published rather than hidden: no player route closes a
        # node tree (Dialog_NodeTree sets closeOnCancel = false), and skipping
        # the option's action skips the RemoveLetter inside it.
        contains("4.8c1", "and names the widget gate it deliberately WAIVES",
                 dig(e, "data.note"), "closeOnCancel")
        contains("4.8c2", "and warns that a dismissed ChoiceLetter SURVIVES",
                 dig(e, "data.note"), "LETTER")
        eq("4.8d", "THE UN-WEDGE: force_pause is now zero", e, "data.force_pause.count", 0)
        not_null("4.8e", "journaled", e, "data.action.journal_seq")

        # THE ACCEPTANCE LINE FROM AMENDMENT #2, in its own words: after the
        # dialog is answered, the next advance RUNS ITS FULL BUDGET.
        a = send("advance", {"ticks": 500, "max_tps": 400})
        eq("4.8f", "THE POINT OF THIS SPEC: the next advance runs its FULL BUDGET",
           a, "data.reason", "ticks")
        # THE KEY IS `ticks_elapsed`. TimeDriver.BuildData emits
        # `["ticks_elapsed"] = ticks` and has never emitted `ticks`, so the
        # original `data.ticks` here could only ever fail — on the single most
        # load-bearing check in the suite. Phase 0.6 asserts both halves.
        ge("4.8g", "and did all 500 ticks, not 0", a, "data.ticks_elapsed", 500)

    # 4.9 dialog-dismiss with nothing up is a clean no-op, not an error.
    e = send("dialog-dismiss")
    eq("4.9a", "dismiss with a clear stack is ok:true, not an error", e, "data.ok", True)
    eq_val("4.9b", "with nothing dismissed (data.dismissed)",
           len(as_list(dig(e, "data.dismissed"))), 0)
    eq("4.9c", "and nothing journaled", e, "data.action.journal_seq", None)

    # 4.10 answering a letter from the letter side, and the honest report about
    #      what that does and does not clear.
    e = send("interactions")
    choice = [l for l in as_list(dig(e, "data.letters"))
              if isinstance(l, dict) and l.get("kind") == "choice"
              and len(as_list(l.get("options"))) >= 1]
    if choice:
        l2 = choice[0]
        enabled = [o for o in as_list(l2.get("options"))
                   if isinstance(o, dict) and o.get("disabled") is not True]
        if enabled:
            # Address BY LABEL — "options by label or index, exactly as 2.4
            # reports them" is the spec's own contract.
            e = send("letter-choose", {"letter": l2.get("id"),
                                       "option_label": enabled[0].get("label")})
            eq("4.10a", "letter-choose by LABEL succeeded", e, "data.ok", True)
            eq("4.10b", "the letter is gone", e, "data.letter_removed", True)
            not_null("4.10c", "the route taken is reported (open-window vs letter-choices)",
                     e, "data.via")
            print("  %svia = %s%s" % (DIM, dig(e, "data.via"), OFF))
            not_null("4.10d", "journaled", e, "data.action.journal_seq")
            eq("4.10e", "and the stack is clear after", e, "data.force_pause.count", 0)
    else:
        note("4.10", "no choice letter left to answer from the letter side")

    # 4.11 an unknown letter id is a helpful bad-args, never a crash.
    e = send("letter-choose", {"letter": 999999})
    eq("4.11a", "an unknown letter id is bad-args with a helpful message",
       e, "error.code", "bad-args")
    contains("4.11b", "and explains that a timed-out letter is reaped",
             dig(e, "error.detail"), "LetterStackUpdate")

    stack_clear("4.12a", "the stack is clear at the end of the phase")
    no_red_errors("4.12b", "zero red errors across the whole dialog phase")


# ------------------------------------------------------------------- phase 5 --
# The generic DiaNode walker, on a real faction tree, with no window.

def phase5():
    banner("PHASE 5 — comms: the DiaNode walker runs headless, and the disabled gate holds")

    e = send("comms-targets", {"negotiator": S["N"]})
    if dig(e, "data.ok") is not True and dig(e, "data.gate") == "no-console":
        precondition("5.1", "a comms console", False,
                     "%s Build one and re-run, or skip phase 5." % dig(e, "data.reason"))
    # A CONSOLE THAT EXISTS BUT CANNOT BE USED IS A FIXTURE GAP, NOT A SPEC
    # FAILURE, and until 2026-09-01 it presented as eight red checks in 5.3 and
    # 5.6. The commonest case by far is POWER, and the commonest cause of that
    # is the sun: a solar generator staged in the afternoon stops generating
    # after sunset, so a suite that ran green at 16:00 goes red at 22:00 with
    # nothing about the mod having changed. `comms-targets` publishes
    # `console_blocked` — RimWorld/Building_CommsConsole.GetFailureReason's own
    # sentence — so the skip can say which.
    if dig(e, "data.console.can_use_now") is False:
        precondition("5.1p", "a USABLE comms console", False,
                     "the console at %s exists but is not usable: %s. If that is power "
                     "and the generator is solar, advance to daylight — "
                     '`advance {until:{condition:{path:"time.hour",op:">=",value:9}}}` — '
                     "or stage a generator that runs at night."
                     % (dig(e, "data.console.at"), dig(e, "data.console_blocked")))
    eq("5.1a", "comms-targets answered", e, "ok", True)
    not_null("5.1b", "the console is identified", e, "data.console.id")
    targets = [t for t in as_list(dig(e, "data.targets")) if isinstance(t, dict)]
    ge_val("5.1c", "at least one comm target (data.targets)", len(targets), 1)
    callable_ = [t for t in targets
                 if t.get("callable") is True and t.get("kind") == "faction"]
    print("  %s%d targets, %d callable factions%s"
          % (DIM, len(targets), len(callable_), OFF))
    for t in targets:
        if t.get("callable") is not True:
            print("  %sblocked: %s — %s%s" % (DIM, t.get("name"), t.get("blocked"), OFF))
    eq("5.1d", "the read mutated nothing", e, "data.action.journal_seq", None)

    precondition("5.2", "a callable faction", ARGS.dry_run or len(callable_) >= 1,
                 "every faction is blocked — most likely LeaderUnavailableNoLeader, which "
                 "is exactly the gate that keeps FactionDialogMaker.FactionDialogFor's "
                 '"Faction ... has no leader" Log.Error unreachable. Not a 3.5 failure.')
    F = {"name": "<faction>"} if ARGS.dry_run else callable_[0]

    e = send("comms-call", {"target": F.get("name"), "negotiator": S["N"]})
    eq("5.3a", "comms-call opened a headless node tree", e, "data.ok", True)
    eq("5.3b", "kind is node-tree", e, "data.kind", "node-tree")
    # THE INVARIANT: Faction.TryOpenComms would have stacked a
    # Dialog_Negotiation, which is a Dialog_NodeTree and therefore forcePause.
    eq("5.3c", "ZERO force-pausing windows — no Dialog_Negotiation was stacked",
       e, "data.force_pause.count", 0)
    eq("5.3d", "the negotiator did not walk, and the verb says so",
       e, "data.negotiator_walked", False)
    opts = [o for o in as_list(dig(e, "data.node.options")) if isinstance(o, dict)]
    ge_val("5.3e", "the root node has options (data.node.options)", len(opts), 1)
    not_null("5.3f", "journaled", e, "data.action.journal_seq")
    print("  %soptions: %s%s"
          % (DIM, " | ".join("%s%s" % (o.get("label"),
                                       " [DISABLED: %s]" % o.get("disabled_reason")
                                       if o.get("disabled") else "")
                             for o in opts), OFF))

    # 5.4 THE DISABLED GATE, on a tree that uses it heavily. FactionDialogMaker
    #     disables MustBeAlly / BadTemperature / WaitTime / WorkTypeDisablesOption.
    dis = [o for o in opts if o.get("disabled") is True]
    if dis:
        e = send("comms-choose", {"option": dis[0].get("index")})
        eq("5.4a", "a DISABLED comms option is refused", e, "data.ok", False)
        eq("5.4b", "naming the gate", e, "data.gate", "option-disabled")
        contains("5.4c", "and citing the Widgets.ButtonText argument",
                 dig(e, "data.gate_cite"), "ButtonText")
        not_null("5.4d", "with the game's OWN disabled reason surfaced",
                 e, "data.disabled_reason")
        eq("5.4e", "nothing journaled", e, "data.action.journal_seq", None)
    else:
        note("5.4", "no disabled option on this faction tree (an ally with everything off "
                    "cooldown) — the gate is not exercisable here")

    # 5.5 walk one enabled option that OPENS A NODE rather than resolving.
    link = [o for o in opts if o.get("disabled") is not True and o.get("opens_node") is True]
    if link:
        e = send("comms-choose", {"option": link[0].get("index")})
        eq("5.5a", "comms-choose walked to the linked node", e, "data.ok", True)
        eq("5.5b", "and reports it went to a node", e, "data.went_to_node", True)
        ge_val("5.5c", "the new node has its own options (data.now_showing.options)",
               len(as_list(dig(e, "data.now_showing.options"))), 1)
        eq("5.5d", "still no window", e, "data.force_pause.count", 0)
        print("  %snow showing: %s%s"
              % (DIM, " | ".join(str(o.get("label")) for o in
                                 as_list(dig(e, "data.now_showing.options"))), OFF))
    else:
        note("5.5", "no enabled linking option on this tree")

    # 5.6 hang up. The model equivalent of the always-appended "(Disconnect)".
    e = send("comms-hang-up")
    eq("5.6a", "comms-hang-up ended the call", e, "data.ok", True)
    contains("5.6b", "and says no window was removed, because the call was headless",
             dig(e, "data.note"), "no window was removed")
    e = send("comms-choose", {"option": 0})
    eq("5.6c", "choosing with no call open is refused, not an NRE", e, "data.ok", False)
    eq("5.6d", "naming the gate", e, "data.gate", "no-call")

    stack_clear("5.7a", "the stack is clear at the end of the phase")
    no_red_errors("5.7b", "zero red errors across the whole comms phase")


# ------------------------------------------------------------------- phase 6 --

def phase6():
    banner("PHASE 6 — the whole run's standing invariants")

    # 6.1 THE invariant.
    no_red_errors("6.1", "ZERO red errors across the WHOLE run")

    # 6.2 no force-pausing modal left behind, which is 1.7's and this spec's
    #     shared contract.
    stack_clear("6.2a", "no force-pausing modal was left behind")
    e = send("interactions")
    eq("6.2b", "and interactions agrees", e, "data.force_pause.count", 0)
    eq("6.2c", "blocking is false", e, "data.blocking", False)

    # 6.3 no trade session and no comms call was left dangling. TradeSession is
    #     STATIC and TradeSession.Close() has no vanilla caller, so a leaked
    #     session would poison every later verb (Tradeable's price getters
    #     dereference TradeSession.trader).
    e = send("trade")
    eq("6.3a", "no trade session is left open", e, "data.gate", "no-session")
    e = send("comms-choose", {"option": 0})
    eq("6.3b", "no comms call is left open", e, "data.gate", "no-call")

    # 6.4 PROVENANCE. Every player mutation this run made landed as an `action`
    #     row, matching the shipped verbs' shape.
    e = send("journal", {"since_seq": S["seq0"], "types": ["action"], "limit": 200})
    ge("6.4a", "the run wrote action rows", e, "data.count", 1)
    rows = [r for r in as_list(dig(e, "data.events")) if isinstance(r, dict)]
    verbs = sorted({str(dig(r, "payload.verb")) for r in rows})
    print("  %saction verbs journaled: %s%s" % (DIM, ", ".join(verbs), OFF))
    shaped = [r for r in rows
              if dig(r, "payload.verb") is not None and dig(r, "payload.step") is not None]
    check("6.4b", "every action row carries {verb, step} — 3.4's shape, not a second one",
          len(shaped) == len(rows), "all %d rows" % len(rows),
          "%d of %d" % (len(shaped), len(rows)))

    # 6.5 and no `dev` row was written by a PLAYER verb. The dev/player split is
    #     the action model's first line; a player verb that journaled as `dev`
    #     would be claiming a cheat it did not commit.
    e = send("journal", {"since_seq": S["seq0"], "types": ["dev"], "limit": 200})
    dev_verbs = {str(dig(r, "payload.verb")) for r in as_list(dig(e, "data.events"))
                 if isinstance(r, dict)}
    leaked = sorted(v for v in dev_verbs if v in NEW_OPS)
    check("6.5", "no 3.5 verb journaled itself as a `dev` cheat row",
          not leaked, "none", leaked)


# ---------------------------------------------------------------------- main --

PHASES = {1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5, 6: phase6}


def main():
    global ARGS
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--root", default=DEFAULT_ROOT, help="protocol root")
    ap.add_argument("--phase", type=int, action="append", choices=sorted(PHASES),
                    help="run only these phases (repeatable); 0 always runs")
    ap.add_argument("--dry-run", action="store_true", help="print the plan, send nothing")
    ap.add_argument("--echo", action="store_true", help="print every result envelope")
    ARGS = ap.parse_args()

    print("AutoRimmer spec 3.5 acceptance — dialog + interaction verbs (git-bug 20e5cda)")
    print("                              + the quest observer         (git-bug 548ef48)")
    print("protocol root: %s" % ARGS.root)
    if not ARGS.dry_run and not os.path.exists(os.path.join(ARGS.root, "status.json")):
        print("%sno status.json under that root — start the bench with "
              "_RimWorld-Agent/run-agent.sh, or pass --root%s" % (RED, OFF))
        sys.exit(2)
    if not ARGS.dry_run:
        os.makedirs(os.path.join(ARGS.root, "commands"), exist_ok=True)

    wanted = sorted(set(ARGS.phase)) if ARGS.phase else sorted(PHASES)
    print("phases: 0 + %s" % ", ".join(str(p) for p in wanted))

    phase0()
    for p in wanted:
        PHASES[p]()

    print("")
    print("=" * 78)
    if ARGS.dry_run:
        # A dry-run SENDS NOTHING, so every expectation above was printed and
        # no expectation was evaluated. Saying "passed" here is the exact
        # green-while-asserting-nothing failure phase 0 exists to prevent, one
        # level up.
        print("%sRESULT: --dry-run printed %d expectations and asserted NONE of "
              "them. Nothing was sent; no dig path was proved. Run it live.%s"
              % (YELLOW, CHECKS, OFF))
        sys.exit(0)
    if FAILS:
        print("%sRESULT: %d FAILED of %d checks — %s%s"
              % (RED, len(FAILS), CHECKS, ", ".join(FAILS), OFF))
        sys.exit(1)
    print("%sRESULT: all %d checks passed%s" % (GREEN, CHECKS, OFF))
    sys.exit(0)


if __name__ == "__main__":
    main()
