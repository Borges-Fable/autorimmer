#!/usr/bin/env python3
"""Acceptance runner for spec 3.4 — Pawn orders + policies (git-bug 39c9db7).

This is the Linux/python twin of `accept/3.4-pawn-orders.ps1`. Same numbered
checks, same envelopes, same exit codes — the two drivers are meant to stay
readable side by side, so the check ids here are the ones in
`accept/3.4-pawn-orders.md`.

WHY BOTH EXIST. The `.ps1` was written on BORGES, which has no python (Store
stub only), and an acceptance script that cannot run on the box holding the
bench is worse than none. The bench now lives on dorian's Linux box, which has
no pwsh. Neither driver is redundant; each is the only one that runs where it
lives. Both speak the raw file protocol — `commands/<id>.json` in,
`results/<id>.json` out — so neither depends on `rwa` being installed.

Start the bench first (`_RimWorld-Agent/run-agent.sh`), load a colony with at
least two colonists and a stockpile, and leave it paused.

    ./accept/3.4-pawn-orders.py                 # everything
    ./accept/3.4-pawn-orders.py --phase 3       # one phase (phase 0 always runs)
    ./accept/3.4-pawn-orders.py --dry-run       # print the plan, send nothing
    ./accept/3.4-pawn-orders.py --root <path>   # a protocol root elsewhere

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
    "\033[32m",
    "\033[31m",
    "\033[33m",
    "\033[36m",
    "\033[2m",
    "\033[0m",
)
if not sys.stdout.isatty():
    GREEN = RED = YELLOW = CYAN = DIM = OFF = ""

ARGS = None          # argparse namespace, set in main()
FAILS = []
CHECKS = 0
S = {}               # cross-step state (pawn ids, thing ids, watermarks)
SEQ = 0


# ------------------------------------------------------------------ protocol --
# commands/<id>.json in, results/<id>.json out. The poller runs a 500 ms cycle
# and ignores inbox files younger than its MinFileAgeMs, so a round trip has a
# floor of roughly 0.25-1 s even for a trivial verb; that is the protocol, not
# this script being slow. `advance` is a DEFERRED result — its file appears only
# when the advance finishes — hence the generous per-call timeout.
#
# Ids are kept to [A-Za-z0-9-] so Poller.Sanitize leaves them alone and the
# result filename is exactly <id>.json.

def send(op, args=None, timeout=240):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acc34-%03d-%s" % (SEQ, slug)
    envelope = json.dumps(
        {"id": cid, "op": op, "args": args or {}}, separators=(",", ":")
    )

    if ARGS.dry_run:
        print("    would send: %s" % envelope)
        return {"ok": True, "op": op, "data": {}, "_dry": True}

    inbox = os.path.join(ARGS.root, "commands", cid + ".json")
    result = os.path.join(ARGS.root, "results", cid + ".json")
    if os.path.exists(result):
        os.remove(result)
    # Write whole-file: the poller's min-age gate tolerates a partial write, but
    # not writing one at all is cheaper than relying on it.
    with open(inbox, "w", encoding="utf-8", newline="") as fh:
        fh.write(envelope)

    deadline = time.time() + timeout
    while time.time() < deadline:
        if os.path.exists(result):
            time.sleep(0.06)          # let the write land
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
    return {
        "ok": False,
        "op": op,
        "error": {
            "code": "acc-timeout",
            "detail": "no results/%s.json within %ss — is the bench running "
                      "and unpaused-capable?" % (cid, timeout),
        },
    }


# ---------------------------------------------- git-bug 722c951: the escape --
#
# `advance` has TWO new default-on guards, and both are right for a play loop
# and wrong for a fixture harness:
#
#   * it REFUSES (ok:false, error.code "unread-journal") when the previous
#     advance journaled events that no `journal` call has read, and
#   * it HALTS (reason:"casualty") when an own-faction pawn goes down or dies
#     while time is running.
#
# This suite is not a play loop. It advances to MOVE GAME STATE so the next
# assertion has something to assert on, it never reads the journal in between,
# and its advances exist to let ordered jobs actually RUN so the
# result can be checked against what the pawn did. Without an
# opt-out the second advance onwards would come back refused and every check
# below would be measuring the refusal instead of the thing it names.
#
# So the opt-out lives HERE, in ONE wrapper, and not at the call sites: a
# `unread_ok` sprinkled inline is indistinguishable to the next reader from one
# somebody added to get a red check green. The reason string names this file, so
# `journal --types action` on the bench says which harness turned the guard off
# and why. Both escapes are per-call and journaled as an act by the mod
# (session 13's threat-pardon precedent).
ESCAPE = ("accept/3.4-pawn-orders.py: fixture harness, not a play loop — it advances to move "
          "game state and asserts on the result, and does not read the journal "
          "between advances")


def advance(args=None, **kw):
    a = dict(args or {})
    a.setdefault("unread_ok", ESCAPE)
    a.setdefault("through_casualties", ESCAPE)
    return send("advance", a, **kw)


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
    """dig() cannot tell `absent` from `present and null`, and this driver
    cares. `eq(…, None)` passes just as happily on a key that was never emitted
    as on one deliberately published as null, so a MISTYPED DIG PATH goes green
    while asserting nothing — the same class of bug as a check that was already
    true before the act under test. has_key is the predicate that can tell
    them apart; `shape` and `is_null` below are the two assertions built on it.

    PER-DRIVER ON PURPOSE, not a shared accept/_shapes.py: every file in
    accept/ stands alone and runs from a bare checkout, and a shared module
    would let a shape change made for one spec silently update this one."""
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
    """PowerShell's @() wrap. Deviates from PS on one point deliberately:
    `@($null)` is a one-element array there, which makes an absent list look
    non-empty; here a missing value is an EMPTY list, which is what every
    caller actually means."""
    if v is None:
        return []
    if isinstance(v, list):
        return v
    return [v]


def pluck(items, key):
    return [x.get(key) if isinstance(x, dict) else None for x in as_list(items)]


def show(v):
    return "null" if v is None else json.dumps(v, separators=(",", ":"))


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


def has(num, what, haystack, needle):
    lst = as_list(haystack)
    check(num, what, needle in lst, "contains '%s'" % needle, haystack)


def shape(num, verb, env, path, kind=None):
    """Assert a key EXISTS, independently of its value — the claim `eq` cannot
    make. Returns the truth of it so a caller can branch, but the check is
    recorded either way."""
    ok = has_key(env, path)
    want = "the key to be PRESENT (absent is not null)"
    if ok and kind is not None:
        got = dig(env, path)
        ok = isinstance(got, kind) and not (kind is not bool and isinstance(got, bool))
        want += " and a %s" % "/".join(
            k.__name__ for k in (kind if isinstance(kind, tuple) else (kind,)))
    check(num, "`%s` publishes %s" % (verb, path), ok, want,
          dig(env, path) if has_key(env, path) else "<absent>")
    return ok


def is_null(num, what, env, path):
    """PRESENT and null — "this setting is deliberately cleared", which is a
    different claim from "this key was never emitted". `eq(…, None)` conflates
    the two and passes on both."""
    there = has_key(env, path)
    check(num, "%s (%s)" % (what, path), there and dig(env, path) is None,
          "the key to be PRESENT and null", dig(env, path) if there else "<absent>")


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
    print("          This is a FIXTURE gap, not a 3.4 failure. Stage it and re-run.")
    sys.exit(2)


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


def no_red_errors(num, what):
    e = send("journal", {"since_seq": S["seq0"], "types": ["red_error"], "limit": 50})
    eq(num, what, e, "data.count", 0)


# ------------------------------------------------------------------- phase 0 --

NEW_OPS = [
    "draft", "undraft", "move-to", "attack", "orders", "prioritize",
    "clear-priority-work", "rescue", "capture", "arrest", "carry",
    "equip", "wear", "drop", "consume",
    "extinguish", "beat-fire", "tend", "repair", "man-turret",
    "rest-until-healed", "fire-at-will",
    "work-priorities", "schedule", "assign",
    "policy-new", "policy-edit", "policy-delete", "policy-default",
    "warden", "surgery-options", "surgery-add", "surgery-remove",
    "research-set", "research-stop",
]


def phase0():
    banner("PHASE 0 - preflight: the bench is live and 3.4's 35 verbs registered")

    e = send("status")
    eq("0.1a", "status answered", e, "ok", True)
    eq("0.1b", "a game is loaded", e, "data.gameLoaded", True)
    eq("0.1c", "the game is paused (the agent owns time)", e, "data.paused", True)
    check("0.1d", "no force-pausing modal is up (spec 1.7 would wedge every advance)",
          dig(e, "data.forcePause") is None, "absent", dig(e, "data.forcePause"))

    verbs = as_list(dig(e, "data.verbs"))
    missing = [o for o in NEW_OPS if o not in verbs]
    check("0.2", "all 35 of 3.4's ops registered", not missing, "no missing ops", missing)

    e = send("pawns", {"filter": "colonist"})
    eq("0.3a", "pawns answered", e, "ok", True)
    roster = as_list(dig(e, "data.list"))
    precondition(
        "0.3b", "at least two visible colonists", len(roster) >= 2,
        "the roster has %d; 3.4's acceptance needs an actor and a patient. "
        "Stage with dev:starter-kit or load a bigger save." % len(roster))

    # The roster order is now stable - `pawns` emits by thingIDNumber ascending
    # and says so in `data.order` (git-bug 1eb2262) - so an index is at least
    # REPRODUCIBLE. It is still the wrong way to pick A. A is not "some
    # colonist", it is "a colonist who can haul and can doctor": bullet 2 orders
    # a haul and bullet 4 needs an operator, and a pawn with either work type
    # disabled fails six checks (2.3b, 2.4a/c/d/e, 2.5b) whose only clue is a
    # NOTE about a disabled work type. Stability makes an index reproducible; it
    # does not make it meaningful. So: SELECT BY PREDICATE, and let the stable
    # order decide only which of the qualifying colonists we take.
    eq("0.3c", "the roster order is the documented stable one", e, "data.order", "id-asc")
    eq("0.3d", "and the cap still selects by attention", e, "data.selected_by", "attention-desc")

    if ARGS.dry_run:
        S.update(A=1001, Aname="<A>", B=1002, Bname="<B>", seq0=0)
        print("          actor  A = %s (%s)" % (S["A"], S["Aname"]))
        print("          target B = %s (%s)" % (S["B"], S["Bname"]))
        print("          (live: one `pawn {sections:[\"work\"]}` per colonist "
              "picks the first with Hauling AND Doctor enabled)")
        phase0_journal()
        return

    ids = [int(r["id"]) for r in roster]
    if ids != sorted(ids):
        note("0.3e", "roster ids are NOT ascending (%s) - the loaded assembly "
                     "predates git-bug 1eb2262; A/B below are not reproducible" % ids)

    # One `pawn {sections:["work"]}` per colonist. Cheap (~600 bytes) and it is
    # the only place the Work tab's disabled set is published.
    WANT = ("Hauling", "Doctor")
    able, why_not = [], []
    for r in roster:
        w = send("pawn", {"id": r["id"], "sections": ["work"]})
        row = dig(w, "data.work") or {}
        if not row.get("initialized"):
            why_not.append("%s: no work settings" % r.get("name"))
            continue
        off = [k for k in WANT if k in as_list(row.get("disabled"))]
        if off:
            why_not.append("%s: %s disabled" % (r.get("name"), "+".join(off)))
            continue
        able.append(r)

    precondition(
        "0.3f", "a colonist with Hauling AND Doctor enabled (the actor A)",
        len(able) >= 1,
        "no visible colonist can do both. Rejected: %s. This is the fixture gap "
        "that cost four runs on 2026-08-31 - see the fixture table in "
        "accept/3.4-pawn-orders.md." % ("; ".join(why_not) or "none"))

    S["A"], S["Aname"] = able[0]["id"], able[0].get("name")
    # B is the patient and only has to be somebody else - 4.9 needs the DOCTOR
    # (A) to be a different pawn from the patient, which this guarantees by
    # construction. Taken in the stable id order so the pick is reproducible.
    others = [r for r in roster if r["id"] != S["A"]]
    precondition(
        "0.3g", "a second colonist to be the patient B", len(others) >= 1,
        "every visible colonist is the actor; 3.4 needs a distinct patient.")
    S["B"], S["Bname"] = others[0]["id"], others[0].get("name")

    print("          actor  A = %s (%s) - selected by predicate, "
          "%d of %d colonists qualified" % (S["A"], S["Aname"], len(able), len(roster)))
    print("          target B = %s (%s)" % (S["B"], S["Bname"]))
    if why_not:
        note("0.3h", "not eligible as actor: %s" % "; ".join(why_not))

    phase0_journal()


def phase0_journal():
    # since_seq past the end, NOT limit:1 — see the .ps1 for why (last_seq is
    # updated before the since_seq skip; the limit break precedes the append).
    e = send("journal", {"since_seq": 999999999, "limit": 1})
    S["seq0"] = int(dig(e, "data.last_seq", 0) or 0)
    eq("0.4", "journal readable (watermark recorded)", e, "ok", True)
    print("          journal watermark seq0 = %s" % S["seq0"])

    # 0.5  MANUAL WORK PRIORITIES — the fixture precondition 4.7 and 5.1 need,
    #      and the one this driver could not satisfy until git-bug e8f2c32.
    #      PlaySettings.useWorkPriorities scribes defaultValue:false, so on any
    #      colony the agent staged itself the Work tab is a checkbox column,
    #      work-priorities correctly refuses priorities 1/2/4, and eight checks
    #      (4.7a-e, 5.1a-c) were unreachable. The checkbox now belongs to
    #      work-priorities itself, because MainTabWindow_Work draws it in the
    #      same window as the matrix. Staged, not asserted: the verb's own
    #      acceptance is accept/manual-work-priorities.md.
    e = send("work-priorities", {"manual": True})
    precondition(
        "0.5", "manual work priorities are ON (4.7 and 5.1 use priorities 1 and 2)",
        dig(e, "data.manual.after") is True,
        "work-priorities {manual:true} did not take: %s. Without it 4.7 and 5.1 "
        "are refused by design, not by defect." % show(e))
    print("          manual priorities: %s -> %s (%s pawn work rows notified)"
          % (dig(e, "data.manual.before"), dig(e, "data.manual.after"),
             dig(e, "data.manual.pawns_notified")))

    # 0.6  THE SHAPE CONTRACT for phase 3's dig paths, and it is here rather
    #      than in phase 3 for one reason: all three are read with a DEFAULT
    #      (`dig(…, 0)`, `pluck(dig(…))`), so a renamed or mistyped path
    #      answers "0" and "[]" — an unclothed pawn with no insulation, which
    #      is a perfectly plausible reading and a completely silent lie. These
    #      three checks fail LOUDLY on absence instead, and they are `shape`
    #      rather than `eq(…, None)` because eq passes on an absent key exactly
    #      as happily as on a null one.
    e = send("pawn", {"id": S["B"], "sections": ["apparel"]})
    shape("0.6a", "pawn{apparel}", e, "data.apparel.worn", list)
    shape("0.6b", "pawn{apparel}", e, "data.apparel.insulation_cold_total", (int, float))
    shape("0.6c", "pawn{apparel}", e, "data.apparel.forced_count", int)
    if has_key(e, "data.apparel.worn.0.def"):
        shape("0.6d", "pawn{apparel}", e, "data.apparel.worn.0.def", str)
    else:
        note("0.6d", "B wears nothing, so worn[].def has no row to shape-check "
                     "here; phase 3 stages clothes onto B and reads it there")


# ------------------------------------------------------------------- phase 1 --

def phase1():
    banner("PHASE 1 - ACCEPTANCE BULLET 1: draft + move + undraft round-trip, "
           "and the drafted pawn HOLDS position")
    A = S["A"]

    # 1.1 a standable, reachable destination. find-rect's `buildable` requirement
    #     implies standable and unblocked, so this cannot pick a wall.
    e = send("find-rect", {"w": 1, "h": 1, "near": "pawn:%s" % A, "max": 5,
                           "require": ["buildable", "reachable-from:pawn:%s" % A]})
    cands = as_list(dig(e, "data.candidates"))
    precondition("1.1", "a buildable, reachable 1x1 cell near the actor", len(cands) > 0,
                 "find-rect found none within 80 rings; the actor may be sealed in.")
    dest = cands[-1]["at"] if not ARGS.dry_run else [0, 0]
    S["dest"] = dest
    print("          destination = %s" % show(dest))

    # 1.2 draft
    e = send("draft", {"pawns": [A]})
    eq("1.2a", "draft accepted exactly one pawn", e, "data.counts.accepted", 1)
    eq("1.2b", "no rejections", e, "data.counts.rejected", 0)
    eq("1.2c", "the pawn is drafted", e, "data.accepted.0.drafted", True)
    eq("1.2d", "it was not already drafted", e, "data.accepted.0.drafted_before", False)
    eq("1.2e", "the setter's priorityWork clear is disclosed", e,
       "data.accepted.0.priority_work_cleared", True)
    ge("1.2f", "an `action` journal row was written", e, "data.action.journal_seq", 1)
    act = dig(e, "data.action")
    check("1.2g", "the action row is NOT stamped as a cheat (it is a player verb)",
          not (isinstance(act, dict) and "cheat" in act), "no `cheat` key", act)

    # 1.3 the observer agrees with the actor - one vocabulary, not two
    e = send("pawn", {"id": A, "sections": ["state"]})
    eq("1.3", "`pawn` reports drafted:true", e, "data.state.drafted", True)

    # 1.4 move
    e = send("move-to", {"pawns": [A], "to": dest})
    eq("1.4a", "move-to accepted the pawn", e, "data.counts.accepted", 1)
    eq("1.4b", "the ordered job is Goto", e, "data.accepted.0.job_def", "Goto")
    ge("1.4c", "an `action` row was written", e, "data.action.journal_seq", 1)
    S["standable"] = dig(e, "data.standable_near")

    # 1.5 walk there
    e = advance({"ticks": 2000, "max_tps": 600})
    eq("1.5a", "advance ran to its tick budget (no dialog, no red error)", e,
       "data.reason", "ticks")

    # 1.6 arrived, and STILL DRAFTED
    e = send("pawn", {"id": A, "sections": ["state"]})
    at = as_list(dig(e, "data.state.at"))
    near = as_list(S.get("standable"))
    if len(near) != 2:
        near = as_list(dest)
    dist = max(abs(at[0] - near[0]), abs(at[1] - near[1])) if len(at) == 2 and len(near) == 2 else 99
    check("1.6a", "the pawn arrived within 3 cells of the standable destination",
          dist <= 3, "|at - %s| <= 3" % show(near), "at=%s dist=%s" % (show(at), dist))
    eq("1.6b", "still drafted", e, "data.state.drafted", True)
    S["heldAt"] = at

    # 1.7 IT HOLDS. A drafted pawn does not wander off to eat, haul or sleep -
    #     which is the half of the round trip that makes undraft necessary.
    e = advance({"ticks": 4000, "max_tps": 600})
    eq("1.7a", "advance ran to its tick budget", e, "data.reason", "ticks")
    e = send("pawn", {"id": A, "sections": ["state"]})
    at2 = as_list(dig(e, "data.state.at"))
    check("1.7b", "the drafted pawn HELD position across 4000 further ticks",
          len(at2) == 2 and len(S["heldAt"]) == 2 and at2 == S["heldAt"],
          "at == %s" % show(S["heldAt"]), at2)
    eq("1.7c", "still drafted after holding", e, "data.state.drafted", True)
    jd = dig(e, "data.state.job_def")
    check("1.7d", "and it is idle-in-combat-stance, not working",
          jd is None or jd in ("Wait_Combat", "Wait", "AttackStatic", "AttackMelee", "Goto"),
          "null | Wait_Combat | Wait | Attack* | Goto", jd)

    # 1.8 undraft - ITS OWN VERB, because the game's own tutorial has a dedicated
    #     UndraftAll instruction and warns that colonists left drafted starve.
    e = send("undraft", {"pawns": [A]})
    eq("1.8a", "undraft accepted the pawn", e, "data.counts.accepted", 1)
    eq("1.8b", "the pawn is undrafted", e, "data.accepted.0.drafted", False)
    ge("1.8c", "an `action` row was written", e, "data.action.journal_seq", 1)

    # 1.9 the whole-roster form is ONE call - the loop-closing shape
    e = send("undraft", {"pawns": "colonists"})
    eq("1.9a", '`undraft {pawns:"colonists"}` is a legal whole-roster call', e, "ok", True)
    gates = pluck(dig(e, "data.rejected"), "gate")
    check("1.9b", "and it rejects the already-undrafted with a reason, not an error",
          all(g == "already" for g in gates), "every rejection gate == 'already'", gates)

    # 1.10 the journal carries the whole round trip as `action` rows
    e = send("journal", {"since_seq": S["seq0"], "types": ["action"], "limit": 200})
    verbs = [dig(ev, "payload.verb") for ev in as_list(dig(e, "data.events"))]
    has("1.10a", "journal has an `action` row for draft", verbs, "draft")
    has("1.10b", "journal has an `action` row for move-to", verbs, "move-to")
    has("1.10c", "journal has an `action` row for undraft", verbs, "undraft")

    no_red_errors("1.11", "ZERO red errors across phase 1")


# ------------------------------------------------------------------- phase 2 --

def phase2():
    banner("PHASE 2 - ACCEPTANCE BULLET 2: `prioritize haul` on a specific stack "
           "makes that pawn haul it NEXT")
    A = S["A"]

    # 2.1 hauling needs somewhere to haul TO.
    e = send("zones", {"kind": "stockpile"})
    total = int(dig(e, "data.stockpiles.total", 0) or 0)
    precondition(
        "2.1", "the colony has at least one stockpile zone", total > 0,
        "WorkGiver_HaulGeneral produces no job with no storage destination, so "
        "`prioritize haul` would correctly report 'no empty place'. Stage one "
        "with 3.2's zone verbs or load a save that has one.")

    # 2.2 a specific stack, ON THE GROUND, so hauling it is a real job
    e = send("dev:spawn-thing", {"def": "Steel", "count": 75, "pos": "pawn:%s" % A,
                                 "stockpile": False})
    eq("2.2a", "dev:spawn-thing placed the steel", e, "ok", True)
    ge("2.2b", "at least one stack landed", e, "data.placed", 1)
    steel = dig(e, "data.spawned.0.id")
    check("2.2c", "a thing id came back for the stack", steel is not None, "an id", steel)
    S["steel"] = steel
    print("          steel stack = %s" % steel)

    # 2.3 THE PARITY LIST. WorkGiverDef.directOrderable defaults true, so this is
    #     as wide as the bench's WorkGiver set; HaulGeneral must be in it.
    e = send("orders", {"pawn": A, "thing": steel})
    eq("2.3a", "orders answered", e, "ok", True)
    avail = pluck(dig(e, "data.available"), "work")
    has("2.3b", "HaulGeneral is offered on the steel stack", avail, "HaulGeneral")
    blocked = [b for b in as_list(dig(e, "data.blocked"))
               if isinstance(b, dict) and b.get("work") == "HaulGeneral"]
    if blocked:
        note("2.3c", "HaulGeneral also appears blocked: %s" % blocked[0].get("reason"))

    # 2.4 the order itself
    e = send("prioritize", {"pawn": A, "work": "HaulGeneral", "thing": steel})
    eq("2.4a", "prioritize succeeded", e, "data.ok", True)
    eq("2.4b", "the work giver is the one asked for", e, "data.work", "HaulGeneral")
    jd = str(dig(e, "data.job_def"))
    check("2.4c", "the ordered job is a haul", jd.startswith("HaulTo"),
          "HaulToCell | HaulToContainer", jd)
    ge("2.4d", "an `action` row was written", e, "data.action.journal_seq", 1)
    # ECHO THE DURABLE STATE, NOT A HOPE: HaulGeneral does not set
    # prioritizeSustains in Core, so nothing durable is written and the result
    # must SAY so rather than claim a standing order.
    sust = dig(e, "data.sustains")
    check("2.4e", "`sustains` reports whether mindState.priorityWork was written",
          isinstance(sust, bool), "a bool", sust)
    if sust is True:
        eq("2.4f", "priorityWork is live (sustains was true)", e,
           "data.priority_work.active", True)
        eq("2.4g", "and names this work giver", e,
           "data.priority_work.work_giver", "HaulGeneral")
    else:
        note("2.4f", "sustains:false - this work giver writes no durable priorityWork, "
                     "and the result says so rather than claiming a standing order")

    # 2.5 "that pawn hauls it NEXT" - the bullet, literally. TryTakeOrderedJob
    #     ends the current job and enqueues first, so the haul is the job the
    #     pawn takes on the next tick.
    e = advance({"ticks": 10, "max_tps": 60})
    eq("2.5a", "advance ran", e, "data.reason", "ticks")
    e = send("pawn", {"id": A, "sections": ["state"]})
    jd = str(dig(e, "data.state.job_def"))
    check("2.5b", "the pawn's CURRENT job is the haul", jd.startswith("HaulTo"),
          "HaulToCell | HaulToContainer", jd)
    print("          job report: %s" % dig(e, "data.state.job"))

    # 2.6 and it finishes
    e = advance({"ticks": 4000, "max_tps": 600})
    eq("2.6a", "advance ran", e, "data.reason", "ticks")
    e = send("pawn", {"id": A, "sections": ["state"]})
    jd = str(dig(e, "data.state.job_def"))
    check("2.6b", "the haul is no longer the current job (it completed)",
          not jd.startswith("HaulTo"), "not HaulTo*", jd)

    # 2.7 clear-priority-work answers either way
    e = send("clear-priority-work", {"pawns": [A]})
    eq("2.7", "clear-priority-work answered (accepted, or refused with a reason)",
       e, "ok", True)

    no_red_errors("2.8", "ZERO red errors through phase 2")


# ------------------------------------------------------------------- phase 3 --
#
# THE START STATE IS STAGED, NOT INHERITED — and that is what this phase was
# missing until 2026-08-31.
#
# It ran live that night: 147 PASS / 3 FAIL, zero red errors, and all three
# failures were one fact. The bench colonist ALREADY wore exactly
# Apparel_Parka + Apparel_Tuque, which is exactly what the `cold` policy
# allows. Nothing was disallowed, so RemoveApparel had nothing to remove
# (3.6c); nothing changed, so insulation could not rise (3.6b); and 3.2's "not
# already wearing the parka" was plainly false.
#
# The FOURTH check is the one this rewrite exists for. 3.6a — "the pawn is
# WEARING the parka" — PASSED, and it asserted nothing whatsoever: it was
# already true before `policy-new` was ever sent. A green that was true before
# the act under test is not evidence of the act; it is a red hat mistaken for
# a green one. Fixing the three reds without fixing that green would have left
# the phase exactly as blind as it was.
#
# So the driver drives the pawn OUT of the warm apparel first, proves it is
# out, and only then makes the claim. Two policies, two advances, one bullet.
#
# THE FOUR DEFS, from Core's own XML (Data/Core/Defs/ThingDefs_Misc/
# Apparel_Various.xml and Apparel_Headgear.xml):
#
#   def                 layer   body-part groups          StuffEffectMult_Cold
#   Apparel_Pants       OnSkin  Legs                                     0.20
#   Apparel_BasicShirt  OnSkin  Torso, Shoulders                         0.22
#   Apparel_Parka       Shell   Torso, Neck, Shoulders, Arms             2.00
#   Apparel_Tuque       OnHead  (head)                                   0.50
#
# Cloth's StuffPower_Insulation_Cold is 18 (Items_Resource_Stuff.xml), so the
# staged pair insulates 3.6 + 4.0 = 7.6 and the warm pair 36.0 + 9.0 = 45.0.
# 45.0 is EXACTLY what the 2026-08-31 live run read off the unstaged colonist,
# which is the arithmetic checking out against a real observation rather than
# against a guess. The gap is 37 points, so 3.6b is not a rounding argument.
#
# No layer collides: PLAIN is OnSkin and WARM is Shell + OnHead, so
# ApparelUtility.CanWearTogether would allow all four at once. The pawn ends up
# in WARM only because the `cold` filter STRIPPED the plain pair, never because
# it ran out of body parts — which is what makes 3.6c a statement about
# RemoveApparel rather than about wardrobe geometry.
PLAIN = ["Apparel_Pants", "Apparel_BasicShirt"]
WARM = ["Apparel_Parka", "Apparel_Tuque"]


def read_apparel(pid):
    """The three dig paths phase 3 leans on, read in one place. All three take
    a DEFAULT, so a renamed path answers `[]` and `0` — a naked pawn, silently.
    0.6 is the shape contract that makes that impossible; this is just the
    reader."""
    e = send("pawn", {"id": pid, "sections": ["apparel"]})
    return (e,
            pluck(dig(e, "data.apparel.worn"), "def"),
            float(dig(e, "data.apparel.insulation_cold_total", 0) or 0),
            int(dig(e, "data.apparel.forced_count", 0) or 0))


def free_to_think(pid):
    """JobGiver_OptimizeApparel fires at a FREE think tick and nowhere else, so
    a pawn asleep or mid-meal simply does not re-dress. That is what the .md's
    "rested pawns" fixture row was asking a human to arrange by hand; the
    driver stages it itself now. Rest and Food are plain Needs, not
    Need_Seekers, so dev:set-need's value STICKS rather than drifting back (the
    verb publishes `data.volatile` to say which is which).

    STAGED AND PRINTED, NEVER ASSERTED: a pawn that has no Rest need is not a
    3.4 failure, and 3.0j's precondition is where an un-thinking pawn surfaces."""
    out = []
    for need in ("Rest", "Food"):
        e = send("dev:set-need", {"pawn": pid, "need": need, "val": 1.0})
        out.append("%s=%s" % (need, dig(e, "data.pct_after")
                              if dig(e, "ok") is not False else "no such need"))
    return ", ".join(out)


def advance_for_apparel(pid, done, nums, why):
    """One 5000-tick advance, then the documented 15000-tick fallback (20000
    total) if `done(worn)` is not yet true. Returns the last read plus the
    window it took, so the caller can say which one it needed.

    Notify_OutfitChanged (fired by the apparel-policy setter) sets
    nextApparelOptimizeTick to NOW, so the pawn is eligible on its next free
    think tick instead of in 6000-9000. It still has to BE free, hence the
    fallback. And JobGiver_OptimizeApparel calls SetNextOptimizeTick ONLY on
    the paths that return null, never after issuing a job — so one advance
    covers the whole cascade of removes and wears, not one garment per 6000."""
    n_run, n_note, n_fallback = nums
    e = advance({"ticks": 5000, "max_tps": 600})
    eq(n_run, "advance ran", e, "data.reason", "ticks")
    e, worn, cold, forced = read_apparel(pid)
    window = 5000
    if not done(worn):
        note(n_note, "%s within 5000 ticks; advancing the documented fallback "
                     "window (the pawn may have been asleep, eating or mid-job)" % why)
        f = advance({"ticks": 15000, "max_tps": 600})
        eq(n_fallback, "fallback advance ran", f, "data.reason", "ticks")
        e, worn, cold, forced = read_apparel(pid)
        window = 20000
    return e, worn, cold, forced, window


def phase3():
    banner("PHASE 3 - ACCEPTANCE BULLET 3: a 'cold' apparel policy assigned, and "
           "the pawn RE-DRESSES under advance")
    B = S["B"]

    # 3.0 STAGE THE PRECONDITION. See the block comment above: without this the
    #     whole phase is a coin toss on what the save's colonists happen to be
    #     wearing, and on 2026-08-31 the coin came up "already dressed for the
    #     answer".
    e = send("dev:spawn-thing", {"def": "Apparel_Pants", "stuff": "Cloth", "count": 1,
                                 "stockpile": True, "quality": "Normal"})
    ge("3.0a", "plain pants landed (the staged wardrobe)", e, "data.placed", 1)
    note("3.0b", "pants storage: %s" % dig(e, "data.stockpile"))
    e = send("dev:spawn-thing", {"def": "Apparel_BasicShirt", "stuff": "Cloth", "count": 1,
                                 "stockpile": True, "quality": "Normal"})
    ge("3.0c", "a plain shirt landed too", e, "data.placed", 1)

    e, orig_worn, orig_cold, orig_forced = read_apparel(B)
    print("          original: worn=%s insulation_cold_total=%s forced=%d"
          % (show(orig_worn), orig_cold, orig_forced))

    # Already in a usable start state? Then do not burn 5000 ticks proving it.
    # "Usable" is the exact negation of what 3.6 needs: not wearing anything the
    # `cold` policy allows (so 3.6a cannot be pre-true) and wearing SOMETHING
    # (so 3.6c cannot be vacuous).
    staged = (not [d for d in orig_worn if d in WARM]) and len(orig_worn) > 0
    if staged and not ARGS.dry_run:
        note("3.0d", "the bench already supplies the start state (%s) - skipping "
                     "the undress leg" % show(orig_worn))
        worn, cold, forced = orig_worn, orig_cold, orig_forced
        window = 0
    else:
        note("3.0d", "needs = %s" % free_to_think(B))
        # A policy that allows ONLY the plain pair. Every warm garment the pawn
        # has on is then disallowed, and JobGiver_OptimizeApparel's first loop
        # issues RemoveApparel for each one unconditionally - no score maths, no
        # season, no weather. This is the same mechanism 3.5 tests, run in the
        # opposite direction to get to a known floor.
        e = send("policy-new", {"kind": "apparel", "label": "acc34-plain",
                                "disallow_all": True, "allow": PLAIN})
        plain = dig(e, "data.id")
        check("3.0e", "a throwaway plain-clothes policy was created",
              dig(e, "ok") is True and plain is not None, "ok:true and an id", e.get("error") or plain)
        S["plain_policy"] = plain
        e = send("assign", {"pawns": [B], "apparel_policy": plain})
        eq("3.0f", "the plain policy was assigned to B", e, "data.counts.accepted", 1)
        e, worn, cold, forced, window = advance_for_apparel(
            B, lambda w: not [d for d in w if d in WARM] and len(w) > 0,
            ("3.0g", "3.0h", "3.0i"), "B has not settled into plain clothes")

    # 3.0j THE PRECONDITION ITSELF, and the reason it is a precondition rather
    #      than a check: an unmet fixture is exit 2 by this driver's own
    #      contract, and three cascading FAILs for one unstaged wardrobe is
    #      precisely the defect this rewrite is repairing.
    still_warm = [d for d in worn if d in WARM]
    precondition(
        "3.0j",
        "B is out of every garment the `cold` policy allows, and is wearing "
        "something it will have to strip",
        not still_warm and len(worn) > 0,
        "after %d ticks B wears %s (cold=%s). Still allowed by `cold`: %s.\n"
        "          forced_count=%d now - a force-worn garment is NEVER auto-dropped "
        "(JobGiver_OptimizeApparel skips it via "
        "Pawn_OutfitForcedHandler.AllowedToAutomaticallyDrop), and nothing in "
        "3.4's surface un-forces one; a `wear` order is what sets it.\n"
        "          READ THIS BEFORE FILING IT AS A FIXTURE GAP: exit 2 says "
        "'could not stage', and the honest cases are a pawn that never got a "
        "free think tick (asleep, drafted, downed, mental state, unreachable "
        "storage) or plain apparel that did not land inside a stockpile. If B "
        "was FREE and UNFORCED and still did not undress, this is not a fixture "
        "gap at all - RemoveApparel did not run and bullet 3 is broken, which "
        "exit 2 understates. Re-run with --echo and read B's job."
        % (window, show(worn), cold, show(still_warm), forced))

    # 3.1 warm apparel, IN STORAGE. JobGiver_OptimizeApparel skips any candidate
    #     failing IsInAnyStorage(), so `stockpile:true` is not a nicety. Spawned
    #     AFTER the undress leg on purpose: these two are the only parka and
    #     tuque guaranteed to be unforbidden and in a stockpile at the moment
    #     the `cold` policy lands. B's own originals may be lying wherever
    #     JobDriver_RemoveApparel's haul left them.
    e = send("dev:spawn-thing", {"def": "Apparel_Parka", "stuff": "Cloth", "count": 1,
                                 "stockpile": True, "quality": "Normal"})
    eq("3.1a", "a parka was spawned", e, "ok", True)
    ge("3.1b", "the parka landed", e, "data.placed", 1)
    note("3.1c", "dev:spawn-thing storage note: %s at %s (not a check - the real "
                 "proof is 3.6a, since JobGiver_OptimizeApparel skips anything not "
                 "in storage)" % (dig(e, "data.stockpile"), show(dig(e, "data.at"))))
    e = send("dev:spawn-thing", {"def": "Apparel_Tuque", "stuff": "Cloth", "count": 1,
                                 "stockpile": True, "quality": "Normal"})
    ge("3.1d", "a tuque landed too", e, "data.placed", 1)

    # 3.2 the BEFORE picture, in 2.2's apparel vocabulary. It is a re-read
    #     rather than a reuse of 3.0's, because 3.1 sat between them.
    e, before_worn, before_cold, before_forced = read_apparel(B)
    print("          before: worn=%s insulation_cold_total=%s (nothing here is "
          "allowed by `cold`, proved at 3.0j)" % (show(before_worn), before_cold))
    # 3.2 IS NO LONGER "the pawn is not already wearing the parka". That claim
    #     is 3.0j's, it exits 2 rather than FAILing, and re-asserting a proven
    #     precondition as a PASS is a green that cannot go red — the habit this
    #     phase is being rewritten to break.
    #
    #     What 3.2 asserts instead is the one thing 3.0j does NOT cover and that
    #     would otherwise surface as a baffling 3.6c: a FORCE-worn garment is
    #     never auto-dropped. JobGiver_OptimizeApparel's remove loop skips any
    #     item failing Pawn_OutfitForcedHandler.AllowedToAutomaticallyDrop, so
    #     one forced pair of pants makes 3.6c unsatisfiable no matter how well
    #     the loop works. B can arrive forced from the save or from a `wear`
    #     order in an earlier session — nothing in 3.4's surface un-forces one.
    check("3.2", "no staged garment is force-worn, so the `cold` filter is free "
                 "to strip all of them",
          before_forced == 0, "forced_count 0", before_forced)

    # 3.3 the policy. THE MECHANISM IS THE FILTER, NOT THE WEATHER - see the .md:
    #     JobGiver_OptimizeApparel's `neededWarmth` comes from
    #     GenTemperature.AverageTemperatureAtTileForTwelfth, i.e. the tile's
    #     SEASONAL average, so dev:weather cannot move it. A policy that
    #     disallows what they wear and allows only warm apparel drives the loop
    #     deterministically through RemoveApparel + ApparelScoreGain.
    e = send("policy-new", {"kind": "apparel", "label": "cold", "disallow_all": True,
                            "allow": ["Apparel_Parka", "Apparel_Tuque"]})
    eq("3.3a", "policy-new succeeded", e, "ok", True)
    eq("3.3b", "the label stuck", e, "data.label", "cold")
    ge("3.3c", "an `action` row was written", e, "data.action.journal_seq", 1)
    pol = dig(e, "data.id")
    check("3.3d", "a policy id came back", pol is not None, "an id", pol)
    has("3.3e", "the parka was allowed", dig(e, "data.edits"), "allow:Apparel_Parka")
    check("3.3f", "nothing was refused by the apparel global filter",
          len(as_list(dig(e, "data.refused"))) == 0, "no refusals", dig(e, "data.refused"))
    S["policy"] = pol

    # 3.4 assign it - plural verb, one pawn is the degenerate case
    e = send("assign", {"pawns": [B], "apparel_policy": pol})
    eq("3.4a", "assign accepted the pawn", e, "data.counts.accepted", 1)
    has("3.4b", "the apparel lever applied", dig(e, "data.accepted.0.applied"),
        "apparel_policy")
    eq("3.4c", "the AFTER read shows the new policy", e,
       "data.accepted.0.after.apparel", "cold")
    eq("3.4d", "and it was read through PawnSafe's guarded backing-field route", e,
       "data.accepted.0.after.source", "backing-field")
    ge("3.4e", "an `action` row was written", e, "data.action.journal_seq", 1)

    # 3.5 the clothes loop. Notify_OutfitChanged sets nextApparelOptimizeTick to
    #     NOW, so the pawn re-optimizes at its next free think tick rather than
    #     in 6000-9000. It still has to BE free: asleep, eating or in a mental
    #     state all delay it, hence the documented fallback window.
    note("3.5", "needs = %s" % free_to_think(B))
    e, worn, after_cold, after_forced, window = advance_for_apparel(
        B, lambda w: "Apparel_Parka" in w, ("3.5a", "3.5b", "3.5c"),
        "B is not re-dressed")

    # 3.6 THE BULLET — and every one of these three is now a claim about what
    #     the ADVANCE did, because 3.0j proved the start state was the negation
    #     of all three.
    #
    #     3.6a cannot be pre-true: at 3.0j B provably wore no Apparel_Parka, and
    #     the only thing between 3.0j and here that can put one on a pawn is
    #     JobDriver_Wear. This phase issues no `wear` order, so the only issuer
    #     is JobGiver_OptimizeApparel. If the loop does not run, B is still in
    #     the staged plain pair and 3.6a is RED.
    has("3.6a", "the pawn is WEARING the parka - it was NOT at 3.0j, so this can "
                "only be JobGiver_OptimizeApparel (within %d ticks)" % window,
        worn, "Apparel_Parka")
    check("3.6b", "cold insulation went UP", after_cold > before_cold,
          "> %s (staged plain pair; cloth parka+tuque is 45.0)" % before_cold,
          after_cold)
    # STRICT, and provably so rather than leniently. TryGiveJob's remove loop
    # RETURNS on the first disallowed worn item, so no Wear job is ever issued
    # while ANY disallowed garment is still on. Every staged garment is
    # disallowed by `cold` (3.0j) and none is forced (3.2), so "B has the parka"
    # entails "B has none of the staged pair" — which makes the strict form the
    # honest one. The old `or len(before_worn) == 0` escape is gone: it made a
    # naked pawn pass a check about taking clothes OFF.
    kept = [d for d in before_worn if d in worn]
    check("3.6c", "EVERY previously-worn garment was taken off "
                  "(JobGiver_OptimizeApparel's RemoveApparel pass)",
          len(before_worn) > 0 and not kept,
          "none of %s still worn" % show(before_worn),
          "before=%s after=%s still-on=%s" % (show(before_worn), show(worn), show(kept)))
    print("          after: worn=%s insulation_cold_total=%s (+%s) forced=%d"
          % (show(worn), after_cold, round(after_cold - before_cold, 1), after_forced))

    # 3.7 the policy database round-trips through 2.4's observer
    e = send("policies")
    labels = pluck(dig(e, "data.outfits.list"), "label")
    has("3.7a", "2.4's `policies` observer sees the new policy", labels, "cold")
    mine = [a for a in as_list(dig(e, "data.assignments"))
            if isinstance(a, dict) and a.get("id") == B]
    check("3.7b", "and sees the assignment",
          bool(mine) and mine[0].get("apparel") == "cold", "'cold'", pluck(mine, "apparel"))

    # 3.8 delete refuses while a live pawn uses it, IN THE GAME'S OWN WORDS
    e = send("policy-delete", {"kind": "apparel", "policy": pol})
    eq("3.8a", "policy-delete refused (a live pawn is using it)", e, "data.ok", False)
    check("3.8b", "and the refusal is the game's own AcceptanceReport string",
          bool(str(dig(e, "data.reason") or "")), "a non-empty reason", dig(e, "data.reason"))

    # 3.8c the OTHER half of the same gate, and the staging's own cleanup: B
    #      moved off the plain policy at 3.4, so TryDelete's AcceptanceReport
    #      now accepts. Deleting it keeps a re-run from stacking an
    #      "acc34-plain" per run in the outfit database.
    if S.get("plain_policy") is not None or ARGS.dry_run:
        e = send("policy-delete", {"kind": "apparel", "policy": S.get("plain_policy")})
        eq("3.8c", "the staging policy deletes cleanly now that nobody uses it",
           e, "data.ok", True)

    no_red_errors("3.9", "ZERO red errors through phase 3")


# ------------------------------------------------------------------- phase 4 --

def phase4():
    banner("PHASE 4 - ACCEPTANCE BULLET 4: a surgery bill added, and a doctor "
           "PERFORMS it under advance")
    A, B = S["A"], S["B"]

    # 4.1 a medical bed. HospitalBed's def carries bed_defaultMedical:true, so a
    #     spawned one is Medical without a toggle verb.
    e = send("dev:spawn-thing", {"def": "HospitalBed", "count": 1,
                                 "pos": "pawn:%s" % B, "faction": "player"})
    eq("4.1a", "a hospital bed was spawned", e, "ok", True)
    ge("4.1b", "it landed", e, "data.placed", 1)
    bed = dig(e, "data.spawned.0.id")
    S["bed"] = bed
    print("          hospital bed = %s" % bed)

    # 4.2 medicine, in storage, so the recipe's ingredient exists
    e = send("dev:spawn-thing", {"def": "MedicineHerbal", "count": 10, "stockpile": True})
    ge("4.2", "medicine landed in storage", e, "data.placed", 1)

    # 4.3 a wounded-but-STANDING patient: HealthAIUtility.ShouldSeekMedicalRest
    #     must be true for rest-until-healed to be offered, and dev:damage's
    #     `amount` mode stops at downed by construction.
    e = send("dev:damage", {"pawn": B, "mode": "amount", "amount": 7, "hits": 2,
                            "def": "Cut"})
    eq("4.3a", "dev:damage landed", e, "ok", True)
    e = send("pawn", {"id": B, "sections": ["state", "health"]})
    eq("4.3b", "the patient is still standing", e, "data.state.downed", False)
    ge("4.3c", "and is wounded", e, "data.health.hediffs_total", 1)

    # 4.4 THE PARITY LIST for surgery. Anesthetize is Core, workAmount 0, one
    #     Medicine, no body part - the cheapest deterministic surgery there is.
    e = send("surgery-options", {"pawn": B, "cap": 200})
    eq("4.4a", "surgery-options answered", e, "ok", True)
    eq("4.4b", "research state was read through the guarded route, not GetProgress",
       e, "data.source", "backing-field")
    opts = as_list(dig(e, "data.options"))
    anes = [o for o in opts if isinstance(o, dict) and o.get("recipe") == "Anesthetize"]
    precondition(
        "4.4c", "Anesthetize is offered for this pawn", len(anes) == 1,
        "Core's Anesthetize should be available on any flesh humanlike. Offered: %s"
        % ", ".join(sorted(set(str(x) for x in pluck(opts, "recipe")))))
    # The precondition above exits 2 on a real run when this is empty; only
    # --dry-run reaches here with nothing, so index defensively rather than
    # relying on PowerShell's forgiving out-of-range read.
    a0 = anes[0] if anes else {}
    check("4.4d", "and it is ADDABLE (no reason, no missing ingredient)",
          a0.get("addable") is True, "addable:true",
          "addable=%s reason=%s missing=%s" % (a0.get("addable"),
                                               a0.get("reason"),
                                               show(a0.get("missing_ingredients"))))

    # 4.5 add the bill. BillStack.AddBill checks NOTHING; every check that
    #     happened is the widget gate this verb reproduces.
    e = send("surgery-add", {"pawn": B, "recipe": "Anesthetize"})
    eq("4.5a", "surgery-add succeeded", e, "data.ok", True)
    ge("4.5b", "an `action` row was written", e, "data.action.journal_seq", 1)
    recipes = pluck(dig(e, "data.bills"), "recipe")
    has("4.5c", "the bill is on the pawn's stack", recipes, "Anesthetize")
    check("4.5d", "the four CreateSurgeryBill warnings are RETURNED, not messaged "
                  "(one of them is a force-pausing Dialog_MessageBox)",
          dig(e, "data.warnings") is not None, "a warnings list", dig(e, "data.warnings"))
    w = as_list(dig(e, "data.warnings"))
    if w:
        print("          warnings: %s" % ", ".join(str(k) for k in pluck(w, "key")))

    # 4.6 2.4's observer sees the same bill in the same vocabulary
    e = send("bills", {"bench": B})
    benches = as_list(dig(e, "data.benches"))
    check("4.6a", "2.4's `bills` observer reports the pawn as a bill giver",
          len([b for b in benches if isinstance(b, dict) and b.get("kind") == "pawn"]) > 0,
          "a bench entry with kind:'pawn'", pluck(benches, "kind"))
    recipes = [r for b in benches for r in pluck(dig(b, "bills"), "recipe")]
    has("4.6b", "and lists the Anesthetize bill", recipes, "Anesthetize")

    # 4.7 a doctor. work-priorities is a MATRIX; one cell is its degenerate case.
    e = send("work-priorities", {"set": [{"pawns": [A], "work": "Doctor", "priority": 1}]})
    eq("4.7a", "work-priorities answered", e, "ok", True)
    eq("4.7b", "exactly one matrix cell changed", e, "data.cells", 1)
    eq("4.7c", "and the unit is named so `accepted` is not read as pawns", e,
       "data.counts.unit", "matrix cells")
    eq("4.7d", "the doctor priority is now 1", e, "data.changes.0.after", 1)
    ge("4.7e", "an `action` row was written", e, "data.action.journal_seq", 1)
    print("          use_priorities = %s (0.5 turned this on; with it OFF this "
          "call is REFUSED, because GetPriority would return a flat 3)"
          % dig(e, "data.use_priorities"))

    # 4.8 the patient into the medical bed. This is 3.4's own rest-until-healed,
    #     which sets job.restUntilHealed - a pawn with only a BILL will not go to
    #     bed on its own (WorkGiver_PatientGoToBedTreatment needs
    #     ShouldSeekMedicalRestUrgent).
    e = send("rest-until-healed", {"pawns": [B], "bed": bed})
    if dig(e, "data.counts.accepted") != 1:
        note("4.8", "rest-until-healed refused: %s" % show(dig(e, "data.rejected")))
    eq("4.8a", "rest-until-healed accepted the patient", e, "data.counts.accepted", 1)
    ge("4.8b", "an `action` row was written", e, "data.action.journal_seq", 1)

    # 4.9 THE BULLET: the doctor performs it under advance.
    e = advance({"ticks": 6000, "max_tps": 600})
    eq("4.9a", "advance ran", e, "data.reason", "ticks")
    e = send("bills", {"bench": B})
    recipes = [r for b in as_list(dig(e, "data.benches"))
               for r in pluck(dig(b, "bills"), "recipe")]
    if "Anesthetize" in recipes:
        note("4.9b", "not performed within 6000 ticks; advancing the documented "
                     "fallback window")
        e = advance({"ticks": 14000, "max_tps": 600})
        eq("4.9c", "fallback advance ran", e, "data.reason", "ticks")
        e = send("bills", {"bench": B})
        recipes = [r for b in as_list(dig(e, "data.benches"))
                   for r in pluck(dig(b, "bills"), "recipe")]
    check("4.9d", "the surgery bill is GONE - a doctor performed it "
                  "(Bill_Medical deletes itself on completion)",
          "Anesthetize" not in recipes, "no Anesthetize bill remains", recipes)

    # 4.10 and the effect landed on the patient
    e = send("pawn", {"id": B, "sections": ["health"]})
    hediffs = pluck(dig(e, "data.health.hediffs"), "def")
    has("4.10", "the patient carries the Anesthetic hediff", hediffs, "Anesthetic")

    # 4.11 remove is the other half
    e = send("surgery-add", {"pawn": B, "recipe": "Anesthetize"})
    if dig(e, "data.ok") is True:
        e = send("surgery-remove", {"pawn": B, "recipe": "Anesthetize"})
        eq("4.11a", "surgery-remove succeeded", e, "data.ok", True)
        ge("4.11b", "an `action` row was written", e, "data.action.journal_seq", 1)
    else:
        note("4.11", "re-add refused (already anesthetized); reason: %s"
             % dig(e, "data.reason"))

    no_red_errors("4.12", "ZERO red errors through phase 4")


# ------------------------------------------------------------------- phase 5 --

def phase5():
    banner("PHASE 5 - the rest of the surface: the plural forms, the gates that "
           "REFUSE, and research")
    A, B = S["A"], S["B"]

    # 5.1 THE MATRIX. One call, a cross product, over a pawn list.
    e = send("work-priorities", {"set": [{"pawns": [A, B],
                                          "works": ["Doctor", "Firefighter"],
                                          "priority": 2}]})
    eq("5.1a", "the matrix form accepted", e, "ok", True)
    ge("5.1b", "it wrote up to 4 cells in ONE call", e, "data.cells", 1)
    eq("5.1c", "and reports the mode", e, "data.mode", "matrix")

    # 5.2 THE COPY FORM - one call, not twenty (amendment item 7)
    e = send("work-priorities", {"copy_from": A, "to": [B]})
    eq("5.2a", "copy_from accepted", e, "ok", True)
    eq("5.2b", "mode is copy", e, "data.mode", "copy")
    ge("5.2c", "a whole row was written", e, "data.accepted.0.set", 1)

    # 5.3 THE DISABLED-WORK-TYPE GATE. Pawn_WorkSettings.SetPriority answers a
    #     disabled work type with Log.Error - a RED ERROR. This must refuse.
    e = send("pawn", {"id": A, "sections": ["work"]})
    disabled = as_list(dig(e, "data.work.disabled"))
    if disabled:
        e = send("work-priorities", {"set": [{"pawns": [A], "work": disabled[0],
                                              "priority": 3}]})
        eq("5.3a", "setting a DISABLED work type is refused, not attempted", e,
           "data.counts.accepted", 0)
        eq("5.3b", "with the work-disabled gate named", e,
           "data.rejected.0.gate", "work-disabled")
        no_red_errors("5.3c", "and NO red error was logged (the whole point of the "
                              "pre-check)")
    else:
        note("5.3", "this colonist has no disabled work type; the red-error "
                    "pre-check could not be exercised here")

    # 5.4 THE SPAN. A wrapping span is one call, not two.
    e = send("schedule", {"pawns": [A, B], "hours": "22-3", "assignment": "Sleep"})
    eq("5.4a", "a wrapping span accepted", e, "ok", True)
    hrs = as_list(dig(e, "data.hours"))
    check("5.4b", "six hours in the span (22,23,0,1,2,3)",
          ",".join(str(h) for h in hrs) == "22,23,0,1,2,3", "22,23,0,1,2,3", hrs)
    row = str(dig(e, "data.accepted.0.row") or "")
    check("5.4c", "the 24-char row uses PawnSerializer's own legend",
          len(row) == 24 and row[22] == "S" and row[0] == "S",
          "24 chars, S at hours 22 and 0", row)
    ge("5.4d", "an `action` row was written", e, "data.action.journal_seq", 1)

    # 5.5 the schedule copy form
    e = send("schedule", {"pawns": [B], "copy_from": A})
    eq("5.5", "schedule copy_from accepted", e, "ok", True)

    # 5.6 THE COLUMN STRIP, plural, any subset, one call
    e = send("assign", {"pawns": [A, B], "med_care": "NormalOrWorse", "self_tend": True,
                        "hostility": "Flee", "area": "Home"})
    eq("5.6a", "assign accepted both pawns", e, "data.counts.accepted", 2)
    eq("5.6b", "medical care took", e, "data.accepted.0.after.med_care", "NormalOrWorse")
    eq("5.6c", "hostility took", e, "data.accepted.0.after.hostility_response", "Flee")
    eq("5.6d", "the area took (Area_Home overrides AssignableAsAllowed to true)", e,
       "data.accepted.0.after.area", "Home")
    check("5.6e", "and whether the area is actually RESPECTED is published",
          isinstance(dig(e, "data.accepted.0.respects_area"), bool), "a bool",
          dig(e, "data.accepted.0.respects_area"))

    # 5.7 area:null is a real setting, not an omission — and the assertion has
    #     to be able to SAY that. `eq(…, None)` could not: it passes on an
    #     absent key exactly as happily as on a null one, so it would have gone
    #     green if `after.area` were renamed or dropped, which is the opposite
    #     of the claim ("unrestricted is a real setting, not an omission").
    #     is_null demands the key be PRESENT and null.
    e = send("assign", {"pawns": [A, B], "area": None})
    eq("5.7a", "area:null (unrestricted) accepted", e, "data.counts.accepted", 2)
    is_null("5.7b", "and the area is cleared - published as null, not omitted",
            e, "data.accepted.0.after.area")

    # 5.8 an unknown area names 3.2 rather than failing blankly
    e = send("assign", {"pawns": [A], "area": "no-such-area-xyz"})
    eq("5.8a", "an unknown area is a bad-args", e, "ok", False)
    check("5.8b", "and the error lists the assignable areas and says who owns creation",
          "3.2" in str(dig(e, "error.detail") or ""), "mentions spec 3.2",
          dig(e, "error.detail"))

    # 5.9 RESEARCH. The gate lives in the widget: SetCurrentProject checks only
    #     baseCost > 0.
    e = send("research", {"cap": 5})
    avail = as_list(dig(e, "data.available.list"))
    precondition("5.9", "at least one startable research project", len(avail) > 0,
                 "the colony has finished everything, or has no bench.")
    proj = avail[0]["def"] if not ARGS.dry_run else "<proj>"
    e = send("research-set", {"project": proj})
    eq("5.9a", "research-set succeeded", e, "data.ok", True)
    eq("5.9b", "the manager reads BACK as the new project (durable state, not a hope)",
       e, "data.current", proj)
    ge("5.9c", "an `action` row was written", e, "data.action.journal_seq", 1)
    eq("5.9d", "progress was read through the guarded route", e, "data.source",
       "backing-field")

    # 5.10 the gate refuses an unstartable project WITH THE CLAUSE THAT BLOCKED IT
    e = send("research-set", {"project": "ShipBasics"})
    if dig(e, "data.ok") is False:
        eq("5.10a", "an unstartable project is refused", e, "data.ok", False)
        clause = str(dig(e, "data.blocked_by"))
        check("5.10b", "with CanStartNow's own clause word (the same vocabulary 2.4's "
                       "blocked_by uses)",
              clause in ("finished", "prerequisites", "techprints", "no-bench",
                         "mechanitor", "analysis", "codex-hidden", "grav-engine"),
              "a CanStartNow clause word", clause)
        check("5.10c", "and it says SetCurrentProject would have accepted it",
              "baseCost" in str(dig(e, "data.note") or ""),
              "the note names SetCurrentProject's only check", dig(e, "data.note"))
    else:
        note("5.10", "ShipBasics was startable on this save; the refusal path was "
                     "not exercised here")

    # 5.11 research-stop
    e = send("research-stop")
    eq("5.11", "research-stop succeeded", e, "data.ok", True)

    # 5.12 THE DRAFT-STATE GATE. `tend` is drafted-only; an undrafted doctor must
    #      be REFUSED with the game's own reason, not silently no-op.
    send("undraft", {"pawns": [A]})
    e = send("tend", {"pawn": A, "target": B})
    eq("5.12a", "an UNDRAFTED doctor is refused by the drafted-only gate", e,
       "data.counts.accepted", 0)
    eq("5.12b", "with the gate named", e, "data.rejected.0.gate", "drafted-only")
    # 5.12c CHANGED by 4087644 (session 9). It used to assert journal_seq is
    #       None — the pre-comment-#1 contract, where an order that changed
    #       nothing wrote no `action` row at all. That is exactly the behaviour
    #       4087644 fixed: the wasted orders were the ones invisible to the
    #       journal, which is the aggregate the agent learns from, so "which of
    #       my instructions are redundant" was unanswerable at session end. A
    #       refusal now writes a row carrying its verdict. See
    #       accept/4087644-order-honesty.py phase 5 for the new contract.
    ge("5.12c", "and the refusal STILL wrote an action row carrying its verdict",
       e, "data.action.journal_seq", 1)

    # 5.13 the same order, drafted, is accepted or refused for a REAL reason
    send("draft", {"pawns": [A]})
    e = send("tend", {"pawn": A, "target": B})
    check("5.13", "drafted, the same call is either accepted or refused for a "
                  "substantive reason (never 'drafted-only')",
          dig(e, "data.counts.accepted") == 1
          or dig(e, "data.rejected.0.gate") != "drafted-only",
          "accepted, or a non-draft-state reason", dig(e, "data.rejected"))

    # 5.14 fire-at-will's gate (still drafted here)
    e = send("fire-at-will", {"pawns": [A], "on": False})
    check("5.14", "fire-at-will answers with a gate, never silently",
          dig(e, "data.counts.accepted") == 1
          or dig(e, "data.rejected.0.gate") in ("not-drafted", "no-ranged-weapon",
                                                "no-drafter", "already"),
          "accepted, or a named gate", dig(e, "data.rejected"))
    send("undraft", {"pawns": [A]})

    # 5.15 WARDEN. Crossing the exclusive/non-exclusive split is a RED ERROR in
    #      the game; here it must be a clean bad-args.
    e = send("warden", {"pawns": [B], "enable": ["AttemptRecruit"]})
    eq("5.15a", "passing an EXCLUSIVE mode to `enable` is a bad-args", e, "ok", False)
    check("5.15b", "and the error names the red error it prevented",
          "red error" in str(dig(e, "error.detail") or "").lower(),
          "the detail mentions the red error", dig(e, "error.detail"))

    # 5.16 a prisoner, then the warden verb for real
    e = send("dev:spawn-pawn", {"kind": "Colonist", "faction": "hostile",
                                "pos": "pawn:%s" % A})
    prisoner = dig(e, "data.pawns.0.id")
    if prisoner is None:
        note("5.16", "dev:spawn-pawn returned no id (no visible hostile faction?); "
                     "warden path not exercised. detail=%s" % dig(e, "error.detail"))
    else:
        send("dev:guest-status", {"pawn": prisoner, "status": "prisoner"})
        e = send("warden", {"pawns": [prisoner], "mode": "AttemptRecruit",
                            "recruitable": True})
        eq("5.16a", "warden accepted the prisoner", e, "data.counts.accepted", 1)
        eq("5.16b", "the exclusive interaction mode took", e,
           "data.accepted.0.after.interaction", "AttemptRecruit")
        ge("5.16c", "an `action` row was written", e, "data.action.journal_seq", 1)
        has("5.16d", "the catalog names the exclusive modes",
            dig(e, "data.modes_available.mode"), "Release")
        e = send("warden", {"pawns": [prisoner], "mode": "Release"})
        eq("5.16e", "RELEASE IS A MODE (not a Released flag write)", e,
           "data.accepted.0.after.interaction", "Release")

    # 5.17 THE DURABLE-STATE WRITE. HaulGeneral (phase 2) does not set
    #      prioritizeSustains, so that step could only verify the result's
    #      honesty about NOT writing. DoctorTendToHumanlikes DOES set it
    #      (Core/Defs/WorkGiverDefs/WorkGivers.xml), so this is the branch where
    #      TryTakeOrderedJobPrioritizedWork calls
    #      mindState.priorityWork.Set(cell, giverDef) — scribed state with a
    #      30000-tick timeout, not a one-shot job.
    send("dev:damage", {"pawn": B, "mode": "amount", "amount": 6, "hits": 2,
                        "def": "Cut"})
    send("undraft", {"pawns": [A]})
    e = send("prioritize", {"pawn": A, "work": "DoctorTendToHumanlikes", "thing": B})
    if dig(e, "data.ok") is True:
        eq("5.17a", "prioritize succeeded on a SUSTAINING work giver", e, "data.ok", True)
        eq("5.17b", "and it reports sustains:true", e, "data.sustains", True)
        eq("5.17c", "mindState.priorityWork is LIVE (the durable write happened)", e,
           "data.priority_work.active", True)
        eq("5.17d", "and names this work giver", e,
           "data.priority_work.work_giver", "DoctorTendToHumanlikes")
        eq("5.17e", "with the game's own 30000-tick timeout published", e,
           "data.priority_work.timeout_ticks", 30000)
        # And drafting CLEARS it - Pawn_DraftController's setter calls
        # priorityWork.ClearPrioritizedWorkAndJobQueue().
        send("draft", {"pawns": [A]})
        send("undraft", {"pawns": [A]})
        e = send("clear-priority-work", {"pawns": [A]})
        check("5.17f", "drafting cleared the durable priorityWork (so "
                       "clear-priority-work now has nothing to do)",
              dig(e, "data.counts.accepted") == 0
              or dig(e, "data.accepted.0.after.active") is False,
              "nothing left to clear, or after.active:false", dig(e, "data"))
    else:
        note("5.17", "DoctorTendToHumanlikes not offered here: %s - the durable "
                     "priorityWork write was not exercised" % dig(e, "data.reason"))

    # 5.18 the whole run's standing invariants
    no_red_errors("5.18a", "ZERO red errors across the WHOLE run")
    e = send("status")
    check("5.18b", "and no force-pausing modal was left behind (spec 1.7)",
          dig(e, "data.forcePause") is None, "absent", dig(e, "data.forcePause"))


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

    print("AutoRimmer spec 3.4 acceptance - pawn orders + policies (git-bug 39c9db7)")
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
        # A dry-run SENDS NOTHING, so every expectation above was printed and
        # no expectation was evaluated. Saying "passed" here is the exact
        # green-while-asserting-nothing failure phase 0 exists to prevent, one
        # level up.
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
