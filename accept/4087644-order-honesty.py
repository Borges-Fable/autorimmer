#!/usr/bin/env python3
"""Acceptance runner for 4087644 — job-ordering verbs stop reporting success
for orders that did nothing.

Same protocol, helpers and exit codes as `accept/3.4-pawn-orders.py`; read that
file's header first. There is no `.ps1` twin: this box has no pwsh and the bench
now lives here.

    ./accept/4087644-order-honesty.py             # everything
    ./accept/4087644-order-honesty.py --phase 2   # one phase (0 always runs)
    ./accept/4087644-order-honesty.py --dry-run   # print the plan, send nothing

Start the bench first (`_RimWorld-Agent/run-agent.sh`), load a colony with at
least two colonists, some apparel and a weapon on the ground, and leave it
paused.

WHAT THIS IS TESTING, in one sentence: `Pawn_JobTracker.TryTakeOrderedJob`
returns TRUE without taking the order when the pawn is already running an
equivalent job, so every `accepted` this mod reported was, in that case, a lie —
and `job_def` corroborated it, because it is re-read AFTER the call and so names
the job we did not cause.

--dry-run PROVES THE PLAN, NEVER THE PATHS. It sends nothing, so every envelope
is empty, every shape check is skipped and every dig() path looks fine. The
first draft of this suite passed --dry-run with five wrong arg names in it. Only
a live run tells you whether the envelopes are the shape the assertions assume,
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

ATTRIBUTION = ["job_id", "player_forced", "job_giver", "work_giver",
               "job_start_tick", "ordered"]

# Staged only when the colony has no loose apparel. Vanilla core def, so it
# exists on any bench; the suite says out loud that it staged one.
STAGE_APPAREL = "Apparel_Parka"


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=240):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acc4087644-%03d-%s" % (SEQ, slug)
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
    """dig() cannot tell `absent` from `present and null`, and this suite cares:
    job_start_tick is DELIBERATELY null on a queued job."""
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
    print("          This is a FIXTURE gap, not a failure of 4087644.")
    sys.exit(2)


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


# ------------------------------------------------------------ shape contract --
# WHY THIS EXISTS, and it is the real finding of this round rather than a
# nicety.
#
# `eq()` cannot tell an ABSENT key from one that is present and null. dig()
# returns None for both, so `eq(..., None)` passes either way. A driver whose
# dig paths are wrong therefore does not fail — it goes GREEN WHILE ASSERTING
# NOTHING, which is strictly worse than the loud abort, because nobody
# investigates a pass. Every driver in accept/ is built on that helper, so every
# driver inherits the defect, and 3.4's and 5.1's inherit it by copying the
# file. A suite that cannot distinguish absent from null is not a test.
#
# THIS SUITE IS THE WORKED EXAMPLE. Its first draft shipped seven wrong arg
# names and dig paths — `pawns {filter:"colonists"}` (the filter is singular),
# `data.pawns` (it is `data.list`), `things {filter:…}` (there is no `filter`
# arg, so it silently answered a haulables query), `data.things` without
# `detail:true` (rollup rows have no id), `pawn {pawn:…}` (that verb takes
# `id`), a watermark read of two keys that do not exist, and journal rows read
# flat when the payload is nested. It was caught only because the preflight
# happened to die first. has_key() was already in this file, for
# job_start_tick: the distinction was known, the tool was built, and it was
# applied in exactly one place.
#
# So phase 0 PROVES every envelope key the later phases dig on, naming the verb
# and the key. A shape change then fails here, loudly, at a check that says
# which verb moved — instead of downstream, or not at all.
#
# PER-DRIVER ON PURPOSE, not a shared accept/_shapes.py. Every file in accept/
# stands alone and runs from a bare checkout, which is what makes acceptance
# portable across two benches with different tooling and why the .py/.ps1 twins
# duplicate deliberately. And a shared module would let a shape change made for
# one spec silently update every other driver, when what you want is 3.4's
# driver failing loudly when 3.4's own contract changes.
def shape(num, verb, env, path, kind=None):
    """Assert a key EXISTS, independently of its value. Returns the truth of it
    so a caller can branch, but the check is recorded either way."""
    ok = has_key(env, path)
    want = "the key to be present"
    if ok and kind is not None:
        ok = isinstance(dig(env, path), kind)
        want += " and a %s" % kind.__name__
    check(num, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def state_of(pid):
    # The `pawn` OBSERVATION verb takes `id` — PawnVerbs.PawnDetail opens with
    # `int id = ctx.Args.IntReq("id")`. The JOB verbs take `pawn`/`pawns`
    # (PawnActs.PawnList). They are different arguments on purpose and a sed
    # that unifies them breaks whichever half it did not mean to touch.
    return send("pawn", {"id": pid, "sections": ["state"]})


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 - the bench, the fixture, and THE SHAPE CONTRACT")
    e = send("status")
    precondition("0.1", "the bench answers `status`",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "no status envelope - start _RimWorld-Agent/run-agent.sh")

    # THE WATERMARK, and the obvious call gives the wrong answer.
    # JournalVerbs.Read updates last_seq BEFORE the `seq <= since_seq` skip and
    # breaks on `events.Count >= limit` BEFORE appending, so `{limit:1}` stops at
    # the SECOND line and reports ITS seq. Pushing since_seq past the end makes
    # every line fail the skip while still updating last_seq, so the file is read
    # to the end and the value is the true maximum. Same idiom as
    # accept/1.8-game-clock-advance.sh:165; session 8 fixed 3.4's driver this way
    # after the same trap. Getting it wrong is silent: the watermark reads 0 and
    # every since_seq assertion downstream quietly scans the whole journal.
    e = send("journal", {"since_seq": 999999999, "limit": 1})
    shape("0.2a", "journal", e, "data.last_seq")
    shape("0.2b", "journal", e, "data.events", list)
    shape("0.2c", "journal", e, "data.count")
    S["seq0"] = dig(e, "data.last_seq") or 0
    print("  %sjournal watermark: seq0=%s%s" % (DIM, S["seq0"], OFF))

    # `colonist` singular - `colonists` is rejected bad-args ("unknown filter") -
    # and the roster is `data.list`, not `data.pawns`. Either mistake alone
    # yields zero ids, which phase 0 then reports as a FIXTURE gap ("load a
    # colony with two or more colonists") on a colony that has four. The working
    # idiom is accept/3.4-pawn-orders.py:253-255.
    e = send("pawns", {"filter": "colonist"})
    shape("0.3a", "pawns", e, "data.list", list)
    ids = [p.get("id") for p in as_list(dig(e, "data.list")) if isinstance(p, dict)]
    if ARGS.dry_run:
        ids = ["<A>", "<B>"]          # --dry-run sends nothing, so stand in
    precondition("0.3b", "at least two colonists", len(ids) >= 2,
                 "found %d - load a colony with two or more colonists" % len(ids))
    S["A"], S["B"] = ids[0], ids[1]
    print("  %spawns: A=%s B=%s%s" % (DIM, S["A"], S["B"], OFF))

    # AN APPAREL ITEM WE CAN ADDRESS BY ID, and this call has three traps in it.
    #
    # 1. There is no `filter` arg on `things` (ThingVerbs.Things reads def,
    #    category, in, detail, cap, detail_cap, by_location). An unknown key is
    #    silently ignored, so `things {filter:"apparel"}` falls through to
    #    `category ?? "haulable"` and answers a HAULABLES query - which finds no
    #    apparel and reads as a fixture gap rather than a wrong call.
    # 2. `things` is a ROLLUP verb: rows are BY DEF and carry no `id`. Only
    #    `detail:true` adds the addressable `data.things[]`. Open issue 70ac258
    #    is about this.
    # 3. `by_location` DEFAULTS TO TRUE for category:"apparel" on the map, which
    #    puts everything under data.by_location.{stockpiled,worn,loose} and
    #    leaves NO top-level data.things at all. Forcing it false gives one
    #    predictable shape.
    e = send("things", {"category": "apparel", "detail": True, "by_location": False})
    shape("0.4a", "things", e, "data.things", list)
    aps = [t for t in as_list(dig(e, "data.things"))
           if isinstance(t, dict) and t.get("id") is not None and not t.get("held_by")]

    if not aps and not ARGS.dry_run:
        # Nothing loose to force onto anyone. Stage one rather than demand a
        # fixture the bench does not have - this colony has no loose apparel at
        # all. dev:spawn-thing is Dev.Gate'd, so with devMode off this is a
        # fixture SKIP with the reason, never a spec FAIL.
        # TWO, not one: phase 3 needs a second target to prove the queue grows
        # (see below). Apparel does not stack, so count:2 yields two separately
        # addressable things in data.spawned[].
        note("0.4", "no loose apparel on the map; staging two with "
                    "dev:spawn-thing {def:%s, count:2}" % STAGE_APPAREL)
        e = send("dev:spawn-thing", {"def": STAGE_APPAREL, "count": 2})
        aps = [t for t in as_list(dig(e, "data.spawned"))
               if isinstance(t, dict) and t.get("id") is not None]
        if aps:
            S["staged"] = STAGE_APPAREL
        else:
            precondition(
                "0.4b", "an apparel item to force onto a pawn", False,
                "no loose apparel on the map and dev:spawn-thing could not stage "
                "one: %s. Staging needs devMode on; otherwise drop a piece of "
                "apparel near the colonists and re-run."
                % show(dig(e, "error") or dig(e, "data.failed")))
    if ARGS.dry_run:
        aps = [{"id": "<apparel>", "def": "<def>"},
               {"id": "<apparel2>", "def": "<def2>"}]
    S["ap"] = aps[0].get("id")
    # A SECOND item, wanted but not required. Phase 3 needs an order that does
    # NOT collide with the running one, and "the same wear again" is by
    # definition the colliding case - so proving the queue GROWS takes a second
    # target. Without one, 3.2 is skipped with a note and 3.3 still runs.
    S["ap2"] = aps[1].get("id") if len(aps) > 1 else None
    print("  %sapparel: %s (%s)%s"
          % (DIM, S["ap"], aps[0].get("def"), OFF))
    if S["ap2"] is None:
        note("0.4c", "only one apparel item available; phase 3's queue-growth "
                     "check will be skipped (the collision check still runs)")
    else:
        print("  %ssecond:  %s (%s)%s" % (DIM, S["ap2"], aps[1].get("def"), OFF))

    # The `pawn` verb's own shape, which four later phases dig into. Sections
    # are top-level keys on the envelope (PawnVerbs.PawnDetail returns
    # PawnSerializer.Detail's dict directly), so it is data.state and
    # data.apparel, not data.pawn.state.
    e = send("pawn", {"id": S["A"], "sections": ["state", "apparel"]})
    shape("0.5a", "pawn", e, "data.state")
    shape("0.5b", "pawn", e, "data.state.job_queue")
    shape("0.5c", "pawn", e, "data.apparel")
    shape("0.5d", "pawn", e, "data.apparel.worn", list)


# ------------------------------------------------------------------- phase 1 --
# THE HEADLINE. A second identical order must not report `accepted`.

def phase1():
    banner("PHASE 1 - already-doing-it: the order that changed nothing says so")

    e = send("wear", {"pawn": S["A"], "thing": S["ap"]})
    if dig(e, "data.counts.accepted") != 1 and not ARGS.dry_run:
        note("1.0", "first `wear` was refused (%s) - phases 1 and 3 need it to "
                    "land; check reachability/forbidden state"
             % show(dig(e, "data.rejected")))
    eq("1.1a", "the FIRST wear is accepted", e, "data.counts.accepted", 1)
    ge("1.1b", "and it journalled", e, "data.action.journal_seq", 1)

    # The same order again, with the pawn now running it.
    e = send("wear", {"pawn": S["A"], "thing": S["ap"]})
    eq("1.2a", "the SECOND, identical wear is NOT accepted", e,
       "data.counts.accepted", 0)
    eq("1.2b", "it is gated as already-doing-it", e,
       "data.rejected.0.gate", "already-doing-it")
    eq("1.2c", "the line records that queueing was NOT asked for", e,
       "data.rejected.0.queue", False)
    check("1.2d", "and the reason names the game's own early-out rather than a "
                  "phrase of ours",
          "JobIsSameAs" in str(dig(e, "data.rejected.0.reason", "")),
          "a reason mentioning Job.JobIsSameAs", dig(e, "data.rejected.0.reason"))

    # THE AMENDED CONTRACT (issue comment #1, upheld over the body's stale
    # Acceptance bullet): a wasted order is exactly the one that used to be
    # invisible to the ledger, so it MUST still write a row.
    ge("1.3a", "the WASTED order still wrote an `action` row", e,
       "data.action.journal_seq", 1)
    # Note the `has_key` rather than a bare dig: an ABSENT `action` block digs to
    # None just as a null provenance does, so testing the value alone would pass
    # on an envelope that had no action block at all. 1.3a would catch that, but
    # a check that only passes because its neighbour fails is not a check.
    check("1.3b", "and the row is not disguised as 'nothing was mutated'",
          has_key(e, "data.action")
          and (not has_key(e, "data.action.provenance")
               or "not applicable" not in str(dig(e, "data.action.provenance"))),
          "an action block whose provenance is not 'not applicable'",
          dig(e, "data.action"))

    # The verdict has to be legible from the JOURNAL alone - that is the whole
    # point of writing the row.
    e = send("journal", {"since_seq": S["seq0"], "types": ["action"], "limit": 200})
    shape("1.4a", "journal", e, "data.events", list)
    # THE EMITTED PAYLOAD IS NESTED. Journal.cs writes each line as
    # {seq,tick,wall,type,payload}, so an action row's verdict is at
    # events[i].payload.verdict, not events[i].verdict. Reading it flat returns
    # None for every row, len(wasted) is 0, and the check FAILS — but the same
    # mistake against an `eq(..., None)` assertion would have PASSED while
    # proving nothing. That is the whole argument for phase 0's contract.
    wasted = [ev for ev in as_list(dig(e, "data.events"))
              if isinstance(ev, dict)
              and dig(ev, "payload.verdict.by_gate.already-doing-it")]
    check("1.4b", "the journal alone can count the wasted orders "
                  "(events[].payload.verdict.by_gate.already-doing-it)",
          len(wasted) >= 1, ">= 1 action row carrying the gate", len(wasted))

    # Same collision, but the caller asked to QUEUE. This is the sharper case:
    # TryTakeOrderedJob's early-out fires BEFORE requestQueueing is read, so
    # nothing is enqueued and vanilla still returns true.
    e = send("wear", {"pawn": S["A"], "thing": S["ap"], "queue": True})
    eq("1.5a", "a QUEUED redundant order is gated the same way", e,
       "data.rejected.0.gate", "already-doing-it")
    eq("1.5b", "and the line says queueing WAS asked for", e,
       "data.rejected.0.queue", True)
    check("1.5c", "with the reason spelling out that nothing was enqueued",
          "enqueued NOTHING" in str(dig(e, "data.rejected.0.reason", "")),
          "a reason saying nothing was enqueued", dig(e, "data.rejected.0.reason"))

    # move-to already shipped its own `already-there`; it must not have been
    # converted into the new gate.
    e = state_of(S["A"])
    at = dig(e, "data.state.at") or (ARGS.dry_run and "<at>")
    if at:
        e = send("draft", {"pawns": [S["A"]]})
        e = send("move-to", {"pawns": [S["A"]], "to": at})
        eq("1.6", "move-to's own `already-there` gate is untouched", e,
           "data.rejected.0.gate", "already-there")
        send("undraft", {"pawns": [S["A"]]})
    else:
        note("1.6", "no position read back for A; skipped the already-there check")


# ------------------------------------------------------------------- phase 2 --

def phase2():
    banner("PHASE 2 - attribution: who ordered this job, and when")

    e = state_of(S["A"])
    for i, f in enumerate(ATTRIBUTION):
        shape("2.1%s" % "abcdef"[i], "pawn", e, "data.state." + f)

    # A fresh ordered job, on A rather than B. B would need its own apparel
    # item: A is already holding S["ap"] under an order from phase 1, and two
    # pawns ordered onto the same piece contend for the reservation, so the
    # second order is refused for a reason that has nothing to do with this
    # issue. A re-order costs no fixture and is deterministic.
    send("wear", {"pawn": S["A"], "thing": S["ap"]})
    e = state_of(S["A"])
    jg = dig(e, "data.state.job_giver")
    if jg is None and not ARGS.dry_run:
        note("2.2", "job_giver is null - Job.jobGiver is resolved from a scribed "
                    "int key against the think tree, and a mod that adds or "
                    "removes a node shifts those keys (ThinkTreeKeyAssigner). "
                    "Best-effort by construction; the rest of phase 2 still runs.")
    else:
        eq("2.2a", "an ORDERED job names ThinkNode_QueuedJob as its giver", e,
           "data.state.job_giver", "ThinkNode_QueuedJob")
        eq("2.2b", "and the triple reads as ordered", e, "data.state.ordered", True)
    eq("2.3", "player_forced is true on an order we gave", e,
       "data.state.player_forced", True)
    ge("2.4", "a RUNNING job has a real start tick", e,
       "data.state.job_start_tick", 0)
    ge("2.5", "and a job id", e, "data.state.job_id", 0)

    # An autonomous job must NOT read as ordered. Let the pawn go back to its
    # own think tree first.
    e = send("advance", {"ticks": 2500, "max_tps": 600})
    e = state_of(S["A"])
    if dig(e, "data.state.job_giver") in (None, "ThinkNode_QueuedJob") \
            and not ARGS.dry_run:
        note("2.6", "A is still on a queued job after the advance; the "
                    "autonomous-job discriminator was not exercised")
    else:
        eq("2.6", "a think-tree job does NOT read as ordered", e,
           "data.state.ordered", False)


# ------------------------------------------------------------------- phase 3 --

def phase3():
    banner("PHASE 3 - the job queue: an order that queued, and one that did not")

    e = state_of(S["A"])
    shape("3.1", "pawn", e, "data.state.job_queue")

    # PUT A KNOWN JOB IN curJob FIRST. Phase 2 ended with an advance, so A is
    # back on its own think tree; without this, the "colliding" order below
    # would find nothing to collide with and would simply enqueue - which is a
    # green run that proved the opposite of what it claims.
    send("wear", {"pawn": S["A"], "thing": S["ap"]})
    e = state_of(S["A"])
    before = dig(e, "data.state.job_queue.total", 0)

    # Queue a DIFFERENT target, so JobIsSameAs does not match and the order is
    # genuinely enqueued behind the running one. NOT `equip` - that verb refuses
    # apparel outright (EquipmentUtility.CanEquip), so an equip of our fixture
    # would be rejected `cannot-equip` and the queue would never move, which
    # reads as the queue being broken rather than the call being wrong.
    queued_ok = False
    if S.get("ap2") or ARGS.dry_run:
        e = send("wear", {"pawn": S["A"], "thing": S["ap2"], "queue": True})
        queued_ok = dig(e, "data.counts.accepted") == 1 or ARGS.dry_run
        if not queued_ok:
            note("3.2", "the queued wear was refused (%s); queue growth "
                        "unexercised" % show(dig(e, "data.rejected")))
    else:
        note("3.2", "no second apparel item, so a non-colliding queued order "
                    "could not be staged")
    e = state_of(S["A"])
    if queued_ok:
        ge("3.2a", "the queued order appears in the queue", e,
           "data.state.job_queue.total", before + 1)
        rows = as_list(dig(e, "data.state.job_queue.list"))
        if ARGS.dry_run:
            rows = [{"job_start_tick": None, "job_def": "<def>"}]
        if rows:
            check("3.2b", "a QUEUED row publishes job_start_tick as null - a "
                          "queued job has not started, and -1 is not a tick",
                  rows[0].get("job_start_tick") is None
                  and "job_start_tick" in rows[0],
                  "present and null", rows[0].get("job_start_tick"))
            check("3.2c", "and the queued row names its own job_def, not the "
                          "running one",
                  rows[0].get("job_def") is not None, "a job_def",
                  rows[0].get("job_def"))
        else:
            note("3.2", "queue total moved but no rows came back")

    # THE POINT OF THE PHASE. A queued order that collides enqueues NOTHING, so
    # the queue cannot be read as proof the order landed - only the gate can.
    e = state_of(S["A"])
    depth = dig(e, "data.state.job_queue.total", 0)
    cur = dig(e, "data.state.job_def") or (ARGS.dry_run and "<jobdef>")
    if cur:
        # The SAME target as the running job, with queue:true. TryTakeOrderedJob
        # compares against curJob only - never against the queue - so this is
        # specifically a current-job collision swallowing a queue request.
        e = send("wear", {"pawn": S["A"], "thing": S["ap"], "queue": True})
        gated = dig(e, "data.rejected.0.gate") == "already-doing-it" or ARGS.dry_run
        e2 = state_of(S["A"])
        if gated:
            eq("3.3", "a COLLIDING queued order enqueued nothing - the queue is "
                      "unchanged, which is why an empty queue is not evidence "
                      "the order was refused",
               e2, "data.state.job_queue.total", depth)
        else:
            note("3.3", "no collision produced on the queued path this run "
                        "(current job %s); the gate was not exercised" % cur)


# ------------------------------------------------------------------- phase 4 --

def phase4():
    banner("PHASE 4 - forced apparel: the durable receipt for a forced wear")

    e = send("pawn", {"id": S["A"], "sections": ["apparel"]})
    worn0 = as_list(dig(e, "data.apparel.worn"))
    check("4.1", "every worn apparel row publishes `forced`",
          bool(worn0) and all(isinstance(r, dict) and "forced" in r for r in worn0),
          "at least one worn row, every one carrying the key",
          [r.get("def") for r in worn0] if worn0 else "no worn apparel")
    shape("4.2", "pawn", e, "data.apparel.forced_count")

    # Drive the forced wear to completion. JobDriver_Wear's FINAL toil is what
    # calls SetForced, so an unfinished job leaves no receipt - that is the
    # point of `forced`, not a flaw in it.
    send("wear", {"pawn": S["A"], "thing": S["ap"]})
    send("advance", {"ticks": 4000, "max_tps": 600})
    e = send("pawn", {"id": S["A"], "sections": ["apparel"]})
    worn = as_list(dig(e, "data.apparel.worn"))
    forced = [r for r in worn if r.get("forced") is True]
    unforced = [r for r in worn if r.get("forced") is False]
    if not worn and not ARGS.dry_run:
        note("4.3", "A is wearing nothing after the advance; the forced-wear "
                    "receipt could not be demonstrated")
        return
    check("4.3", "a COMPLETED forced wear leaves forced:true on that item "
                 "(JobDriver_Wear's final toil -> "
                 "OutfitForcedHandler.SetForced)",
          len(forced) >= 1, ">= 1 forced item", [r.get("def") for r in worn])
    check("4.4", "and apparel the pawn's own policy put on reads forced:false - "
                 "the two mechanisms are now distinguishable, which is the "
                 "question that could not be answered before",
          len(unforced) >= 1, ">= 1 unforced item",
          [r.get("def") for r in worn])
    ge("4.5", "forced_count agrees", e, "data.apparel.forced_count", 1)

    # The hazard this section was written around: IsForced(ap) Log.Errors and
    # MUTATES when the apparel is destroyed, so the shipped route reads the list.
    # `data.count` is the RETURNED-event count (JournalVerbs.Read), so 0 here
    # means no red errors since the watermark. Shape-checked first: a missing
    # `count` key would dig to None, and `eq(..., 0)` would then FAIL loudly
    # rather than pass — but only because the expected value is 0 and not None.
    # Prove the key rather than rely on that luck.
    e = send("journal", {"since_seq": S["seq0"], "types": ["red_error"], "limit": 50})
    shape("4.6a", "journal", e, "data.count")
    eq("4.6b", "reading forced apparel raised NO red error "
               "(ForcedApparel.Contains, never IsForced)", e, "data.count", 0)


# ------------------------------------------------------------------- phase 5 --
# The journal-rule change reaches every Outcome-based verb. These are the
# regression guards for the paths it touches but does not own.

def phase5():
    banner("PHASE 5 - the journal rule, and e8f2c32's matrix stamp")

    # An order refused on its widget gate now JOURNALS. This is the amended
    # contract and it is a DELIBERATE break of 3.4's check 5.12c, which asserts
    # the opposite; 5.12c encodes the pre-4087644 rule.
    send("undraft", {"pawns": [S["A"]]})
    e = send("tend", {"pawn": S["A"], "target": S["B"]})
    eq("5.1a", "an undrafted doctor is still refused by the drafted-only gate",
       e, "data.rejected.0.gate", "drafted-only")
    ge("5.1b", "and the refusal NOW writes an action row (supersedes 3.4's "
               "check 5.12c)", e, "data.action.journal_seq", 1)

    # e8f2c32's rule: the stamp - and now the journal row's verdict - must count
    # the same unit `counts` does. The matrix path never calls Outcome.Ok.
    e = send("work-priorities", {"manual": True})
    e = send("work-priorities",
             {"set": [{"pawn": S["A"], "work": "Doctor", "priority": 1}]})
    # ENVELOPE `ok`, not `data.ok`. Outcome.Result publishes verb/accepted/
    # rejected/counts/action and no `ok` at all — only the verbs that hand-build
    # their envelope (prioritize, research-set) carry `data.ok`. 3.4's check 4.7a
    # gets this right; my first draft did not, and it is the eighth path error in
    # this file, found by re-reading assertions against source rather than by
    # running anything.
    eq("5.2a", "the matrix form still answers ok", e, "ok", True)
    eq("5.2b", "the unit is still named", e, "data.counts.unit", "matrix cells")
    ge("5.2c", "and a successful matrix write still stamps a real journal_seq "
               "(e8f2c32 did not regress)", e, "data.action.journal_seq", 1)

    e = send("journal", {"since_seq": S["seq0"], "types": ["red_error"], "limit": 50})
    shape("5.3a", "journal", e, "data.count")
    eq("5.3b", "no red errors across the whole run", e, "data.count", 0)


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

    print("AutoRimmer acceptance - order honesty (4087644)")
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
    if FAILS:
        print("%sRESULT: %d FAILED of %d checks - %s%s"
              % (RED, len(FAILS), CHECKS, ", ".join(FAILS), OFF))
        sys.exit(1)
    print("%sRESULT: all %d checks passed%s" % (GREEN, CHECKS, OFF))
    sys.exit(0)


if __name__ == "__main__":
    main()
