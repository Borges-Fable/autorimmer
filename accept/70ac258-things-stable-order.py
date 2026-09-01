#!/usr/bin/env python3
"""Acceptance runner for 70ac258 — `things` and `fires` stop emitting an
addressable list ordered by a live score.

Same protocol, helpers and exit codes as `accept/4087644-order-honesty.py`; read
that file's header first. There is no `.ps1` twin: this box has no pwsh and the
bench lives here.

    ./accept/70ac258-things-stable-order.py               # everything safe
    ./accept/70ac258-things-stable-order.py --phase 2     # one phase (0 always runs)
    ./accept/70ac258-things-stable-order.py --dry-run     # print the plan, send nothing
    ./accept/70ac258-things-stable-order.py --churn       # + phase 4 (advances the clock)
    ./accept/70ac258-things-stable-order.py \\
        --fire-inside 40,55 --fire-outside 12,90          # + phase 6 (STARTS TWO FIRES)

Start the bench first (`_RimWorld-Agent/run-agent.sh`), load a colony, and leave
it PAUSED. Phase 2 is stronger evidence on an unpaused bench and the run says so
either way.

WHAT THIS IS TESTING, in one sentence: an index into a list ordered by a value
that moves at read time is not a handle, so the detail list and the fire list
now SELECT by the live score and EMIT by `thingIDNumber` ascending — two
different orders, two published fields, and `attention_rank` per line so urgency
survives the reorder instead of being destroyed by it.

THE ONE CHECK THAT IS THE WHOLE ISSUE is 3.2d: with `detail_cap:2` the item
whose `attention_rank` is 0 sits at INDEX 1, because it has the higher id. Rank
travels on the line; position is a register. Before the fix those were the same
number and there was no way to tell.

--dry-run PROVES THE PLAN, NEVER THE PATHS. It sends nothing, so every envelope
is empty, every shape check is skipped and every dig() path looks fine. Only a
live run tells you whether the envelopes are the shape the assertions assume,
which is what phase 0's shape contract is for. A green --dry-run is evidence of
nothing except that the file parses and the phases are wired.

THIS SUITE MUTATES, deliberately and reversibly:
  * phase 2/3 `forbid` ONE item and `unforbid` it in a teardown that runs even
    when a check fails or a precondition aborts. That is the score change with
    no add/remove the acceptance asks for.
  * phase 0 may `dev:spawn-thing` a resource pile if no def has three stacks.
  * phase 4 (`--churn`, off by default) advances the clock 2500 ticks.
  * phase 6 (two explicit cells, off by default) starts and then destroys two
    fires. It refuses to run on an unpaused bench.

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

# The published order words. `ThingVerbs` keeps the ARGUMENT vocabulary (`id`,
# `attention`) deliberately distinct from the PUBLISHED values (`id-asc`,
# `attention-desc`): the argument names the key, the value names the whole
# ordering. Both halves are part of "consistent with `pawns`", so both are
# asserted.
ID_ASC = "id-asc"
ATTENTION_DESC = "attention-desc"
# `fires` does NOT publish "attention-desc" for its ranked view, and that is not
# an oversight: `ThingVerbs.FireScan` computes no ThingAttention at all. Its
# rule is `fireSize + (outside home ? 10 : 0)` and it says so verbatim, because
# one sequence must not be given two names.
FIRE_RANK = "outside-home-then-size-desc"

# The staging fallback when no def on the map has three stacks. Vanilla Core,
# stackLimit 75, so `count:225` asks for three stacks — but `GenPlace
# .TryPlaceThing` in `Near` mode MERGES into an existing stack that has room, so
# the number of NEW addressable things is not the number of stacks requested.
# The suite therefore re-reads and asserts the population rather than trusting
# `data.spawned`.
STAGE_DEF = "Steel"
STAGE_COUNT = 225

# The detail list's cap ceiling. `ThingVerbs.Things` rejects anything outside
# 1..300 ("detail_cap must be 1..300"), so a fixture def with more than 300
# things of one def cannot be pinned and phase 2 would be measuring the cap
# rather than the order.
DETAIL_CAP_MAX = 300
ROLLUP_CAP_MAX = 200


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=240):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acc70ac258-%03d-%s" % (SEQ, slug)
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
# and its one advance exists only to let the resource counter and the spawn
# settle so the ORDER of a things read can be compared. Without an
# opt-out the second advance onwards would come back refused and every check
# below would be measuring the refusal instead of the thing it names.
#
# So the opt-out lives HERE, in ONE wrapper, and not at the call sites: a
# `unread_ok` sprinkled inline is indistinguishable to the next reader from one
# somebody added to get a red check green. The reason string names this file, so
# `journal --types action` on the bench says which harness turned the guard off
# and why. Both escapes are per-call and journaled as an act by the mod
# (session 13's threat-pardon precedent).
ESCAPE = ("accept/70ac258-things-stable-order.py: fixture harness, not a play loop — it advances to move "
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
    """dig() cannot tell `absent` from `present and null`, and this suite cares:
    `things {def:…}` publishes `query.category` as PRESENT AND NULL, which is
    the shape that says "this was a def query, not a category one"."""
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
    return "null" if v is None else json.dumps(v, separators=(",", ":"))


def ids_of(env, path="data.things"):
    return [r.get("id") for r in as_list(dig(env, path))
            if isinstance(r, dict) and r.get("id") is not None]


def ascending(ids):
    return all(ids[i] < ids[i + 1] for i in range(len(ids) - 1))


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
    """A fixture the bench cannot provide. Aborts the RUN, exit 2, and says out
    loud that it is not a spec failure."""
    if ARGS.dry_run:
        print("  %s%-7s NEEDS   %s%s" % (CYAN, num, what, OFF))
        return
    if ok:
        print("  %s%-7s OK      precondition: %s%s" % (DIM, num, what, OFF))
        return
    print("  %s%-7s SKIP    precondition NOT MET: %s%s" % (YELLOW, num, what, OFF))
    print("          %s" % detail)
    print("          This is a FIXTURE gap, not a failure of 70ac258.")
    teardown()
    sys.exit(2)


def soft_skip(num, what, detail):
    """A phase the OPERATOR chose not to arm (the churn advance, the fire
    staging), or one whose gate is shut (devMode). Distinct from
    precondition(): it skips ONE phase and the run carries on, because a closed
    dev gate must never be reported as a failed assertion and an unarmed
    optional phase must not take the exit code with it."""
    print("  %s%-7s SKIP    %s%s" % (YELLOW, num, what, OFF))
    print("          %s" % detail)


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


# ------------------------------------------------------------ shape contract --
# WHY THIS EXISTS, and it is the reason this driver was worth writing rather
# than a reason it is thorough.
#
# `eq()` cannot tell an ABSENT key from one that is present and null. dig()
# returns None for both, so `eq(..., None)` passes either way. A driver whose
# dig paths are wrong therefore does not fail — it goes GREEN WHILE ASSERTING
# NOTHING, which is strictly worse than a loud abort, because nobody
# investigates a pass.
#
# THIS FILE IS THE SECOND WORKED EXAMPLE. Its source, the hand-driven
# `70ac258-things-stable-order.md`, was written on another machine against
# envelope shapes nobody had observed, and it had never been run. Porting it
# against `Source/AutoRimmer/ThingVerbs.cs` turned up five places where the
# prose and the shipped serializer disagree — all of them corrected in the .md,
# all of them listed at the bottom of this file. Every one would have been
# invisible to a suite built on `eq()` alone.
#
# So phase 0 PROVES every envelope key the later phases dig on, naming the verb
# and the key. A shape change then fails HERE, loudly, at a check that says
# which verb moved — instead of downstream, or not at all.
#
# PER-DRIVER ON PURPOSE, not a shared accept/_shapes.py. Every file in accept/
# stands alone and runs from a bare checkout, which is what makes acceptance
# portable across two benches with different tooling. A shared module would let
# a shape change made for one spec silently update every other driver, when
# what you want is this driver failing loudly when 70ac258's own contract
# changes.
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


def absent(num, verb, env, path, why):
    """The mirror of shape(). Asserts a key is NOT there — the only way to
    prove `rollups` was deliberately left alone rather than quietly grown an
    `id` it cannot support."""
    check(num, "`%s` does NOT publish %s — %s" % (verb, path, why),
          not has_key(env, path), "the key to be ABSENT", dig(env, path))


def bad_args(num, what, env, needle):
    """An argument the verb must REFUSE. A failed envelope carries no `data` at
    all (Poller.BuildResultJson writes `data` or `error`, never both), so this
    reads `error.code` and `error.detail` and never a data path."""
    ok = (dig(env, "ok") is False
          and dig(env, "error.code") == "bad-args"
          and needle in str(dig(env, "error.detail", "")))
    if ARGS.dry_run:
        ok = True
    check(num, what, ok,
          "ok:false, error.code 'bad-args', detail naming %r" % needle,
          {"ok": dig(env, "ok"), "error": dig(env, "error")})


def dev_gate_shut(env):
    """`Dev.Gate` throws a VerbArgsException naming devMode, which the executor
    turns into a bad-args result. That is a CLOSED GATE, not a defect, and the
    two must never be reported the same way."""
    return (dig(env, "ok") is False
            and "devMode" in str(dig(env, "error.detail", "")))


# ------------------------------------------------------------------ fixtures --

def things_detail(cap=DETAIL_CAP_MAX, order=None, def_name=None):
    """The pinned detail read every phase is built on.

    THREE ARGUMENT TRAPS, all of them silent, all of them confirmed in
    `ThingVerbs.Things`:

    1. There is NO `filter` argument. The verb reads def, category, in, detail,
       cap, detail_cap, by_location and order. `VerbArgs` reads only the keys it
       asks for, so an unknown KEY is IGNORED — `things {filter:"steel"}` does
       not error, it falls through to `category ?? "haulable"` and answers a
       different question. Unknown VALUES are loud; unknown KEYS are silent.
    2. Rollup rows carry no `id` — they are keyed by def and there is no
       `thingIDNumber` to carry. `detail:true` is what produces the addressable
       `data.things[]`, and this whole issue is about that list.
    3. `by_location` DEFAULTS TRUE for `category:"apparel"` on the map, which
       moves everything under `data.by_location.{stockpiled,worn,loose}` and
       leaves NO top-level `data.things`. This suite queries by `def`, where
       `category` stays null and the default is false — but it passes the flag
       explicitly anyway, because a fixture that depends on a defaulted
       argument is a fixture that breaks when the default is reasonably
       changed.
    """
    args = {"def": def_name or S["def"], "detail": True,
            "detail_cap": cap, "by_location": False}
    if order is not None:
        args["order"] = order
    return send("things", args)


def stage_forbid():
    """The score change with NO add/remove, and the staging asserts that it
    staged.

    `ThingAttention` adds +100000 for `IsForbidden(Faction.OfPlayer)`. Every
    other term together tops out at 11000 (max(0,100-hp_pct)*100 <= 10000, plus
    min(stackCount,1000)), so one forbid moves the chosen item to attention rank
    0 and nothing else can outrank it. Nothing is added to or removed from the
    map, which is exactly the condition 70ac258's acceptance names.
    """
    if S.get("forbidden"):
        return True
    e = send("forbid", {"things": [S["loud"]]})
    # `forbid` publishes a bare `accepted` COUNT and an `ids` list — NOT the
    # `counts.accepted` shape the job verbs use (DesignationVerbs.ForbidCore).
    eq("F.1a", "the staging forbid was accepted", e, "data.accepted", 1)
    ids = as_list(dig(e, "data.ids"))
    check("F.1b", "and it names the item it forbade",
          S["loud"] in ids or ARGS.dry_run, "ids containing %s" % S["loud"], ids)
    ge("F.1c", "and it journalled the mutation", e, "data.action.journal_seq", 1)
    ok = ARGS.dry_run or (dig(e, "data.accepted") == 1 and S["loud"] in ids)
    if not ok:
        # A staging step that failed silently is indistinguishable from the
        # defect under test, so say it out loud and let the phase's own checks
        # fail honestly rather than pretend the fixture is built.
        note("F.1", "the forbid did NOT land (%s) — the phases that follow are "
                    "measuring an UNCHANGED score and prove much less"
             % show(dig(e, "data.rejects_by_reason") or dig(e, "error")))
    S["forbidden"] = True
    return ok


def teardown():
    """Put the map back. Runs on the success path, on a failed check, and on a
    precondition abort — a suite that leaves a stack forbidden has changed the
    colony it was only supposed to observe."""
    if S.get("forbidden"):
        e = send("unforbid", {"things": [S["loud"]]})
        S["forbidden"] = False
        print("  %steardown: unforbid %s -> accepted=%s%s"
              % (DIM, S["loud"], dig(e, "data.accepted"), OFF))
    if S.get("fires"):
        # Phase 6 owns the danger and puts its own fires out in a `finally`.
        # Reaching here with any left means it did not get that far.
        note("teardown", "fires %s may still be burning - phase 6 did not "
                         "reach its own teardown. Put them out by hand: "
                         "dev:destroy {things:%s}"
             % (S["fires"], S["fires"]))


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 - the bench, the fixture, and THE SHAPE CONTRACT")

    e = send("status")
    precondition("0.1", "the bench answers `status`",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "no status envelope - start _RimWorld-Agent/run-agent.sh")
    precondition("0.2", "a game is loaded",
                 ARGS.dry_run or dig(e, "data.gameLoaded") is True,
                 "status says gameLoaded=false - load a colony")
    S["paused"] = dig(e, "data.paused")
    print("  %sbench: tick=%s paused=%s%s"
          % (DIM, dig(e, "data.tick"), S["paused"], OFF))
    if S["paused"] is False:
        note("0.2", "the bench is RUNNING. Phase 2's stability checks are much "
                    "stronger evidence this way (hit points and stack counts "
                    "are genuinely moving between the two reads) — but phase 6 "
                    "will refuse to stage fires.")

    # THE WATERMARK, and the obvious call gives the wrong answer.
    # JournalVerbs.Read updates last_seq BEFORE the `seq <= since_seq` skip and
    # breaks on `events.Count >= limit` BEFORE appending, so `{limit:1}` stops at
    # the SECOND line and reports ITS seq. Pushing since_seq past the end makes
    # every line fail the skip while still updating last_seq, so the file is read
    # to the end and the value is the true maximum. Getting it wrong is silent:
    # the watermark reads 0 and the red-error check scans the whole journal.
    e = send("journal", {"since_seq": 999999999, "limit": 1})
    shape("0.3a", "journal", e, "data.last_seq")
    shape("0.3b", "journal", e, "data.events", list)
    shape("0.3c", "journal", e, "data.count")
    S["seq0"] = dig(e, "data.last_seq") or 0
    print("  %sjournal watermark: seq0=%s%s" % (DIM, S["seq0"], OFF))

    # ---- the ROLLUP shape, which discovery reads and phase 1 asserts is
    # ---- deliberately unchanged.
    e = send("things", {"category": "resources", "cap": 20})
    shape("0.4a", "things", e, "data.rollups", list)
    shape("0.4b", "things", e, "data.rollups_total")
    shape("0.4c", "things", e, "data.rollups_more")
    shape("0.4d", "things", e, "data.order")
    shape("0.4e", "things", e, "data.totals.stacks")
    shape("0.4f", "things", e, "data.hp_source")
    shape("0.4g", "things", e, "data.pool")
    shape("0.4h", "things", e, "data.skipped.fogged")
    rolls = [r for r in as_list(dig(e, "data.rollups")) if isinstance(r, dict)]
    if rolls:
        shape("0.4i", "things", e, "data.rollups.0.def")
        shape("0.4j", "things", e, "data.rollups.0.stacks")
        shape("0.4k", "things", e, "data.rollups.0.forbidden_stacks")

    # ---- pick the fixture def.
    #
    # THREE stacks minimum, not two: with two, any permutation is one swap and
    # half of them look like the identity (1eb2262's lesson, same words).
    #
    # AND ZERO ALREADY-FORBIDDEN STACKS, which the .md did not ask for and
    # needed to. `ThingAttention` ties break on id ASCENDING, so a second
    # forbidden stack with a lower id than ours would take attention rank 0 and
    # phase 3's cap checks would fail for a fixture reason while looking like a
    # spec failure. `Designator_Forbid.CanDesignateThing` also refuses an item
    # that is ALREADY forbidden, so `forbid` would answer accepted:0.
    def usable(r):
        return (r.get("stacks") or 0) >= 3 and (r.get("forbidden_stacks") or 0) == 0
    cands = [r for r in rolls if usable(r)]

    if not cands and not ARGS.dry_run:
        note("0.5", "no resource def has three unforbidden stacks; staging with "
                    "dev:spawn-thing {def:%s, count:%d}" % (STAGE_DEF, STAGE_COUNT))
        st = send("dev:spawn-thing", {"def": STAGE_DEF, "count": STAGE_COUNT})
        if dev_gate_shut(st):
            precondition(
                "0.5a", "a def with three unforbidden stacks", False,
                "no def qualifies and dev:spawn-thing is gated: %s. Turn devMode "
                "on, or drop three stacks of one resource on the map."
                % show(dig(st, "error.detail")))
        # `dev:spawn-thing` reports a REFUSED placement as ok:true with
        # placed:0 and a `failed[]` block. Checking only `ok` is how a staging
        # step becomes indistinguishable from the defect under test.
        ge("0.5b", "the staging spawn actually placed something",
           st, "data.placed", 1)
        if (dig(st, "data.placed") or 0) < 1:
            precondition("0.5c", "a def with three unforbidden stacks", False,
                         "dev:spawn-thing placed nothing: %s"
                         % show(dig(st, "data.failed")))
        e = send("things", {"category": "resources", "cap": 20})
        rolls = [r for r in as_list(dig(e, "data.rollups")) if isinstance(r, dict)]
        cands = [r for r in rolls if usable(r)]

    if ARGS.dry_run:
        cands = [{"def": "<DEF>", "stacks": 3, "forbidden_stacks": 0}]
    precondition("0.5", "a def with at least three unforbidden stacks",
                 bool(cands),
                 "no resource def on this map has three stacks with none "
                 "forbidden - drop a few stacks of one resource, or turn "
                 "devMode on so the suite can stage them")
    # Smallest qualifying population, so the pinned cap has the most headroom.
    cands.sort(key=lambda r: r.get("stacks") or 0)
    S["def"] = cands[0].get("def")
    print("  %sfixture def: %s (%s stacks in the rollup)%s"
          % (DIM, S["def"], cands[0].get("stacks"), OFF))

    # ---- the DETAIL shape: every key phases 1-4 dig on.
    e = things_detail()
    shape("0.6a", "things", e, "data.things", list)
    shape("0.6b", "things", e, "data.things_total")
    shape("0.6c", "things", e, "data.things_more")
    shape("0.6d", "things", e, "data.things_order")
    shape("0.6e", "things", e, "data.things_selected_by")
    shape("0.6f", "things", e, "data.query.def")
    # PRESENT AND NULL on a def query — the shape that says "this was not a
    # category query". `eq(..., None)` cannot tell that from the key being gone.
    shape("0.6g", "things", e, "data.query.category")
    shape("0.6h", "things", e, "data.query.category_source")
    rows = [r for r in as_list(dig(e, "data.things")) if isinstance(r, dict)]
    if rows:
        shape("0.6i", "things", e, "data.things.0.id", int)
        shape("0.6j", "things", e, "data.things.0.attention_rank", int)
        shape("0.6k", "things", e, "data.things.0.forbidden")
        shape("0.6l", "things", e, "data.things.0.def")
        shape("0.6m", "things", e, "data.things.0.count")
        shape("0.6n", "things", e, "data.things.0.at")

    S["total"] = dig(e, "data.things_total")
    ids0 = ids_of(e)
    if ARGS.dry_run:
        ids0 = [101, 102, 103]
        S["total"] = 3
    precondition("0.7a", "at least three addressable things of that def",
                 len(ids0) >= 3,
                 "the detail list came back with %d entries - the rollup said "
                 "three stacks, so either the def query and the resources "
                 "category disagree about the population or something moved "
                 "between the two reads" % len(ids0))
    # EVERYTHING IN PHASE 2 DEPENDS ON THIS. Above the cap, membership moves
    # with the score BY DESIGN (spec 2.6 - the cap must never hide the urgent
    # item), so a stability check run over a truncated list fails for the
    # correct reason and proves nothing. `detail_cap` cannot go above 300.
    precondition("0.7b", "the whole population fits under the cap "
                         "(things_more == 0)",
                 ARGS.dry_run or dig(e, "data.things_more") == 0,
                 "things_more is %s at detail_cap:%d, which is the ceiling "
                 "ThingVerbs.Things allows. Pick a narrower def: above the cap "
                 "the surviving SET moves with the score by design and this "
                 "suite would be testing 2.6, not 70ac258."
                 % (show(dig(e, "data.things_more")), DETAIL_CAP_MAX))
    S["ids0"] = ids0
    # The item to make urgent: the HIGHEST id. Deliberate - under `id-asc` it
    # sits LAST, so when attention lifts it to rank 0 the two orders cannot
    # coincide and 3.2d has something to prove.
    S["loud"] = max(ids0)
    print("  %s%d things, ids %s … %s; loud=%s%s"
          % (DIM, len(ids0), ids0[0], ids0[-1], S["loud"], OFF))

    # ---- the FIRE shape, on BOTH routes into FireScan. Phases 5 and 6 dig on
    # ---- these, and the two routes must answer to one contract.
    e = send("fires")
    shape("0.8a", "fires", e, "data.list", list)
    shape("0.8b", "fires", e, "data.count")
    shape("0.8c", "fires", e, "data.more")
    shape("0.8d", "fires", e, "data.order")
    shape("0.8e", "fires", e, "data.selected_by")
    shape("0.8f", "fires", e, "data.in_home_area")
    shape("0.8g", "fires", e, "data.outside_home_area")
    shape("0.8h", "fires", e, "data.fogged")
    shape("0.8i", "fires", e, "data.biggest_size")
    S["fires0"] = dig(e, "data.count") or 0
    if S["fires0"]:
        note("0.8", "%d fire(s) are ALREADY burning. Phase 5's counts are "
                    "stated against that baseline, not against zero."
             % S["fires0"])

    e = send("things", {"category": "resources"})
    shape("0.9a", "things", e, "data.fire", dict)
    shape("0.9b", "things", e, "data.fire.list", list)
    shape("0.9c", "things", e, "data.fire.order")
    shape("0.9d", "things", e, "data.fire.selected_by")
    shape("0.9e", "things", e, "data.fire.count")
    shape("0.9f", "things", e, "data.fire.more")


# ------------------------------------------------------------------- phase 1 --
# THE SHAPE OF THE ANSWER. Two orders, two fields, and a rollup list that was
# deliberately not touched.

def phase1():
    banner("PHASE 1 - two orders, two fields, and `rollups` left alone")

    e = things_detail()
    eq("1.1a", "the DEFAULT emit order is by id — 2.4 shipped 'attention-desc' "
               "here, so that word without having asked for it means the old "
               "assembly is loaded",
       e, "data.things_order", ID_ASC)
    eq("1.1b", "and the SELECTION rule is published separately: `things_order` "
               "is what position means, `things_selected_by` is what the cap "
               "kept",
       e, "data.things_selected_by", ATTENTION_DESC)
    ids = ids_of(e) or (ARGS.dry_run and S["ids0"]) or []
    check("1.1c", "the emitted ids are STRICTLY ASCENDING - this is the whole "
                  "fix",
          ascending(ids) and len(ids) >= 3, "a strictly ascending id sequence",
          ids)
    rows = [r for r in as_list(dig(e, "data.things")) if isinstance(r, dict)]
    if ARGS.dry_run:
        rows = [{"attention_rank": 0}]
    check("1.1d", "and every line carries an integer `attention_rank`, so "
                  "urgency survives the reorder instead of being destroyed by "
                  "it",
          bool(rows) and all(isinstance(r.get("attention_rank"), int)
                             and not isinstance(r.get("attention_rank"), bool)
                             for r in rows),
          "attention_rank on every row",
          [r.get("attention_rank") for r in rows])
    ranks = sorted(r.get("attention_rank") for r in rows) if rows else []
    check("1.1e", "the ranks are a permutation of 0..n-1 - a rank computed "
                  "before the cut, not an index computed after it",
          ranks == list(range(len(rows))) or ARGS.dry_run,
          "0..%d in some order" % (len(rows) - 1), ranks)

    # THE TWO LISTS ARE DIFFERENT OBJECTS WITH DIFFERENT CONTRACTS, and the
    # field names say so. `data.order` belongs to `rollups`; `data.things_order`
    # belongs to the detail list. Reading the bare `order` as the detail list's
    # order is the mistake the prefix exists to prevent.
    eq("1.2a", "`rollups` still says attention-desc — the def-keyed summary was "
               "deliberately left alone (the ruling, point 1)",
       e, "data.order", ATTENTION_DESC)
    check("1.2b", "so the bare `order` and `things_order` genuinely disagree, "
                  "which is the point of prefixing one of them",
          dig(e, "data.order") != dig(e, "data.things_order") or ARGS.dry_run,
          "data.order != data.things_order",
          [dig(e, "data.order"), dig(e, "data.things_order")])
    absent("1.2c", "things", e, "data.rollups.0.id",
           "a rollup is keyed by def and has no thingIDNumber to sort by")

    # THE RANKED VIEW STILL EXISTS. A caller that wants the ranking asks for it.
    e2 = things_detail(order="attention")
    eq("1.3a", "the explicit ranked view names its order", e2,
       "data.things_order", ATTENTION_DESC)
    eq("1.3b", "and still names its selection rule, which has not changed", e2,
       "data.things_selected_by", ATTENTION_DESC)
    rows2 = [r for r in as_list(dig(e2, "data.things")) if isinstance(r, dict)]
    if ARGS.dry_run:
        rows2 = [{"attention_rank": 0}]
    check("1.3c", "under THIS order the rank IS the index - which is the "
                  "definition of the two agreeing",
          all(r.get("attention_rank") == i for i, r in enumerate(rows2)),
          "attention_rank == index for every row",
          [r.get("attention_rank") for r in rows2])
    check("1.3d", "and the two views carry the SAME SET of ids - only the "
                  "sequence differs",
          set(ids_of(e2)) == set(ids) or ARGS.dry_run,
          "the same %d ids" % len(ids), sorted(ids_of(e2)))

    # A TYPO MUST NOT QUIETLY RETURN THE OTHER ORDER. `ThingVerbs.Things`
    # validates `order` before it even looks the def up, so this is a bad-args
    # whatever else is wrong with the call.
    e3 = send("things", {"def": S["def"], "order": "size"})
    bad_args("1.4a", "an unknown `order` on `things` is a bad-args naming the "
                     "legal words, never a silent fallback", e3, "id|attention")
    e4 = send("fires", {"order": "size"})
    bad_args("1.4b", "and `fires` refuses the same word the same way — one "
                     "vocabulary, two verbs", e4, "id|attention")


# ------------------------------------------------------------------- phase 2 --
# THE HEADLINE. The order does not move when the score does.

def phase2():
    banner("PHASE 2 - a score change of 100000 points does not permute the list")

    a = things_detail()
    b = things_detail()
    ids_a, ids_b = ids_of(a), ids_of(b)
    if ARGS.dry_run:
        ids_a = ids_b = S["ids0"]
    check("2.1a", "two IDENTICAL reads return the same ids in the same order",
          ids_a == ids_b, "%d ids, unchanged" % len(ids_a),
          {"first": ids_a, "second": ids_b})
    check("2.1b", "and the population did not move under us",
          dig(a, "data.things_total") == dig(b, "data.things_total"),
          "the same things_total",
          [dig(a, "data.things_total"), dig(b, "data.things_total")])
    if S.get("paused") is not False:
        note("2.1", "the bench is paused, so 2.1a is the WEAK form of the "
                    "check: nothing could have moved. 2.3a is the real one — "
                    "it reads across a deliberate 100000-point score change.")

    # THE STATE CHANGE. Forbidding moves the chosen item to attention rank 0
    # and adds and removes nothing.
    staged = stage_forbid()

    c = things_detail()
    ids_c = ids_of(c)
    if ARGS.dry_run:
        ids_c = S["ids0"]
    check("2.3a", "AFTER the score change the id sequence is byte-for-byte the "
                  "one from before it - this is the property the issue is "
                  "about, and it is what makes an id read from call 1 still "
                  "address the same thing in call 2",
          ids_c == ids_a, "the same %d ids in the same order" % len(ids_a),
          {"before": ids_a, "after": ids_c})
    eq("2.3b", "and nothing was added or removed", c, "data.things_total",
       S["total"])
    eq("2.3c", "the whole population is still under the cap, so 2.3a is a "
               "statement about the SEQUENCE and not about survivorship",
       c, "data.things_more", 0)

    rows = {r.get("id"): r for r in as_list(dig(c, "data.things"))
            if isinstance(r, dict)}
    loud = rows.get(S["loud"])
    if ARGS.dry_run:
        loud = {"attention_rank": 0, "forbidden": True}
    if loud is None:
        note("2.4", "the forbidden item is not in the list at all; 2.4a-c "
                    "cannot be judged")
    else:
        check("2.4a", "the forbidden item took attention rank 0 - the score "
                      "really did move",
              loud.get("attention_rank") == 0, "attention_rank 0",
              loud.get("attention_rank"))
        check("2.4b", "and the line says it is forbidden",
              loud.get("forbidden") is True, "forbidden true",
              loud.get("forbidden"))
        check("2.4c", "while it is STILL SITTING LAST in the emitted list - "
                      "urgency travels on the line, not in the position",
              ids_c and ids_c[-1] == S["loud"] or ARGS.dry_run,
              "the last id to be %s" % S["loud"], ids_c[-1] if ids_c else None)
    if not staged:
        note("2.4", "the forbid did not land, so 2.4a-c above are describing "
                    "an unchanged map")

    # AND THE RANKED VIEW DID MOVE. Without this the suite cannot distinguish
    # "the order held under a score change" from "the score never changed".
    d = things_detail(order="attention")
    ids_d = ids_of(d)
    if ARGS.dry_run:
        ids_d = list(reversed(S["ids0"]))
    check("2.5a", "the ranked view puts the forbidden item FIRST",
          bool(ids_d) and ids_d[0] == S["loud"],
          "things[0].id == %s" % S["loud"], ids_d[:1])
    check("2.5b", "so the two views read the same population milliseconds "
                  "apart and DISAGREE about position - one is a register, one "
                  "is a ranking, and each now says which it is",
          ids_d != ids_c or ARGS.dry_run,
          "the id order to differ between the two views",
          {"id-asc": ids_c, "attention-desc": ids_d})
    check("2.5c", "and they still carry the same set of ids",
          set(ids_d) == set(ids_c) or ARGS.dry_run,
          "the same %d ids" % len(ids_c), sorted(ids_d))


# ------------------------------------------------------------------- phase 3 --
# THE OTHER HALF. If the fix broke selection it would read as a fix.

def phase3():
    banner("PHASE 3 - the cap still cuts by attention, and does not reorder")

    staged = stage_forbid()
    if not staged:
        note("3.0", "the forbid did not land; every check in this phase is "
                    "measuring an unchanged score")

    # ---- cap of one. The urgent item survives, NOT the lowest id.
    e = things_detail(cap=1)
    rows = [r for r in as_list(dig(e, "data.things")) if isinstance(r, dict)]
    if ARGS.dry_run:
        rows = [{"id": S["loud"], "forbidden": True, "attention_rank": 0}]
    check("3.1a", "detail_cap:1 returns exactly one entry",
          len(rows) == 1, "1 row", len(rows))
    eq("3.1b", "and it is the FORBIDDEN one, not the lowest id - the id sort "
               "runs AFTER the cut, and a re-sort applied to the candidate set "
               "would quietly undo spec 2.6",
       e, "data.things.0.id", S["loud"])
    eq("3.1c", "the survivor's line says why it survived", e,
       "data.things.0.forbidden", True)
    eq("3.1d", "and it is rank 0", e, "data.things.0.attention_rank", 0)
    eq("3.1e", "the emit order is still id-asc even for a list of one - the "
               "two facts are independent of the cap",
       e, "data.things_order", ID_ASC)
    eq("3.1f", "and the selection rule is unchanged by the cap", e,
       "data.things_selected_by", ATTENTION_DESC)
    eq("3.1g", "everything else is reported as truncated, not silently "
               "dropped", e, "data.things_more", S["total"] - 1)
    eq("3.1h", "and the total still counts the whole scored population", e,
       "data.things_total", S["total"])

    # ---- cap of two. THIS IS THE CHECK THE WHOLE ISSUE IS ABOUT.
    e = things_detail(cap=2)
    ids = ids_of(e)
    if ARGS.dry_run:
        ids = [S["ids0"][0], S["loud"]]
    check("3.2a", "detail_cap:2 returns exactly two entries",
          len(ids) == 2, "2 rows", ids)
    check("3.2b", "the urgent item is one of them - selection is still by "
                  "attention",
          S["loud"] in ids, "the forbidden id %s among them" % S["loud"], ids)
    check("3.2c", "and they are emitted in ASCENDING id order",
          ascending(ids), "an ascending pair", ids)
    # `loud` is the HIGHEST id of the def (phase 0 chose it for this), so under
    # id-asc it must sit at index 1 while holding rank 0.
    rows = {r.get("id"): r for r in as_list(dig(e, "data.things"))
            if isinstance(r, dict)}
    loud = rows.get(S["loud"]) or (ARGS.dry_run and {"attention_rank": 0})
    check("3.2d", "THE WHOLE ISSUE IN ONE LINE: the entry the cap kept FIRST "
                  "(attention_rank 0) is emitted at INDEX 1, because its id is "
                  "higher. Rank travels on the line; position is a register. "
                  "Before the fix these were the same number.",
          bool(ids) and ids[-1] == S["loud"]
          and isinstance(loud, dict) and loud.get("attention_rank") == 0,
          "the last id to be %s and its attention_rank to be 0" % S["loud"],
          {"ids": ids, "rank_of_loud": (loud or {}).get("attention_rank")})
    eq("3.2e", "and the remainder is published", e, "data.things_more",
       S["total"] - 2)

    # ---- at the boundary. A cap exactly equal to the population must give
    # ---- back the whole list, unpermuted.
    e = things_detail(cap=S["total"])
    ids = ids_of(e)
    if ARGS.dry_run:
        ids = S["ids0"]
    check("3.3a", "detail_cap == things_total returns the whole list, in the "
                  "same order as the pinned read - the cap itself does not "
                  "reorder anything",
          ids == S["ids0"], "the %d ids from phase 0" % len(S["ids0"]),
          {"pinned": S["ids0"], "at-cap": ids})
    eq("3.3b", "with nothing left over", e, "data.things_more", 0)

    # ---- the cap's own edges are refused, not clamped. A clamped cap is a
    # ---- caller who asked for 500 rows and was handed 300 without being told.
    bad_args("3.4a", "detail_cap:0 is refused",
             send("things", {"def": S["def"], "detail": True, "detail_cap": 0}),
             "detail_cap must be 1..300")
    bad_args("3.4b", "detail_cap above the 300 ceiling is refused",
             send("things", {"def": S["def"], "detail": True,
                             "detail_cap": DETAIL_CAP_MAX + 1}),
             "detail_cap must be 1..300")
    bad_args("3.4c", "and the ROLLUP cap has its own, different ceiling",
             send("things", {"def": S["def"], "cap": ROLLUP_CAP_MAX + 1}),
             "cap must be 1..200")


# ------------------------------------------------------------------- phase 4 --
# The environmental version of phase 2. Opt-in: it advances the clock.

def phase4():
    banner("PHASE 4 - real churn (optional, weaker, and it moves the clock)")

    if not ARGS.churn:
        soft_skip("4.0", "not armed",
                  "pass --churn to advance 2500 ticks and re-read. It is off by "
                  "default because it ADVANCES THE GAME CLOCK on the "
                  "orchestrator's bench, and because it is not a pass/fail "
                  "gate: on a quiet colony it shows no churn at all, which "
                  "proves nothing either way. Phase 2 is the check that proves "
                  "the property.")
        return

    before = ids_of(things_detail())
    if ARGS.dry_run:
        before = S["ids0"]
    e = advance({"ticks": 2500, "max_tps": 600})
    if dig(e, "ok") is not True and not ARGS.dry_run:
        soft_skip("4.1", "the advance did not run",
                  "advance answered %s" % show(dig(e, "error")))
        return
    after_env = things_detail()
    after = ids_of(after_env)
    if ARGS.dry_run:
        after = S["ids0"]

    check("4.2a", "the list is still emitted ascending after 2500 ticks of "
                  "hauling and deterioration",
          ascending(after), "an ascending id sequence", after)
    # Stated on the INTERSECTION, deliberately: over 2500 ticks haulers merge
    # and split stacks (moving the stackCount term) and unroofed stock
    # deteriorates (moving the hp term), while things are also genuinely
    # created and destroyed. "The same set" would be a false claim; "the same
    # relative ORDER among the survivors" is the real one.
    keep = set(before) & set(after)
    check("4.2b", "and the survivors of the churn are in the SAME RELATIVE "
                  "ORDER as before it - not the same set, the same order",
          [i for i in after if i in keep] == [i for i in before if i in keep],
          "the intersection in one order", {"before": before, "after": after})
    gone = [i for i in before if i not in keep]
    born = [i for i in after if i not in keep]
    note("4.2", "%d of %d survived; %d destroyed, %d new"
         % (len(keep), len(before), len(gone), len(born)))
    if dig(after_env, "data.things_more"):
        note("4.2", "things_more is now %s - the population grew past the cap, "
                    "so 4.2b is a statement about survivors only"
             % show(dig(after_env, "data.things_more")))


# ------------------------------------------------------------------- phase 5 --
# `fires`, on both routes into FireScan, with no fire needed.

def phase5():
    banner("PHASE 5 - `fires`: two facts, two routes, one contract")

    e = send("fires")
    eq("5.1a", "the `fires` DEFAULT emit order is by id - it was "
               "'outside-home-then-size-desc' before this fix, so that word "
               "under the default means the old assembly is loaded",
       e, "data.order", ID_ASC)
    eq("5.1b", "and the selection rule states its REAL rule rather than "
               "borrowing the word 'attention' - FireScan computes no "
               "ThingAttention at all",
       e, "data.selected_by", FIRE_RANK)

    e2 = send("things", {"category": "resources"})
    eq("5.2a", "`things`' own embedded fire block answers the same contract - "
               "before this fix the two routes into FireScan could have "
               "answered different ones (the ruling, point 1)",
       e2, "data.fire.order", ID_ASC)
    eq("5.2b", "including the selection rule", e2, "data.fire.selected_by",
       FIRE_RANK)
    check("5.2c", "and it is the same list, not a second scan with the same "
                  "name",
          ids_of(e2, "data.fire.list") == ids_of(e, "data.list")
          or ARGS.dry_run,
          "the same ids as the `fires` verb returned",
          {"fires": ids_of(e, "data.list"),
           "things.fire": ids_of(e2, "data.fire.list")})

    # The `order` argument reaches the EMBEDDED list too. `ThingVerbs.Things`
    # passes it into FireScan, so a caller asking `things` for the ranked view
    # gets the ranked fire list with it.
    e3 = send("things", {"category": "resources", "order": "attention"})
    eq("5.3a", "the `order` argument on `things` reaches the embedded fire "
               "list", e3, "data.fire.order", FIRE_RANK)
    eq("5.3b", "and even the ranked view refuses to call itself 'attention'",
       e3, "data.fire.selected_by", FIRE_RANK)

    ids = ids_of(e, "data.list")
    check("5.4a", "whatever is burning now is emitted ascending",
          ascending(ids), "an ascending id sequence", ids)
    rows = [r for r in as_list(dig(e, "data.list")) if isinstance(r, dict)]
    if ARGS.dry_run:
        rows = [{"attention_rank": 0, "in_home_area": False}]
    if rows:
        check("5.4b", "and every fire line carries its rank",
              all(isinstance(r.get("attention_rank"), int)
                  and not isinstance(r.get("attention_rank"), bool)
                  for r in rows),
              "attention_rank on every row",
              [r.get("attention_rank") for r in rows])
        check("5.4c", "and its home-area flag, which is half the ranking rule",
              all("in_home_area" in r for r in rows),
              "in_home_area on every row", rows[0])
    else:
        note("5.4", "nothing is burning, so 5.4b-c have no rows to judge. "
                    "Phase 6 stages two fires; 5.1-5.3 prove the contract's "
                    "shape without them.")


# ------------------------------------------------------------------- phase 6 --
# The staged fire ordering. Two explicit cells, or it does not run.

def phase6():
    banner("PHASE 6 - two staged fires, and the one outside the home area")

    if not (ARGS.fire_inside and ARGS.fire_outside):
        soft_skip(
            "6.0", "not armed",
            "pass --fire-inside X,Z and --fire-outside X,Z to run it. The "
            "driver CANNOT pick these itself: `areas` publishes `home_cells` "
            "as a COUNT and no cell set, and no verb exposes the home area's "
            "footprint, so the two cells have to come from a human looking at "
            "the map. Pick STONE OR DIRT away from anything wooden. Without "
            "them, phase 5 has already proved the contract's shape; what is "
            "lost is only the live ordering claim.")
        return

    # A fire only spreads on a TICK: RimWorld/Fire.cs spreads at
    # `fireSize > 0.7f` inside Tick, and a fresh fire starts at
    # Fire.MinFireSize = 0.1f. On a paused bench two staged fires cannot grow
    # and cannot spread. On a running one they can do both, which is how a
    # fixture becomes a crater.
    st = send("status")
    if not ARGS.dry_run and dig(st, "data.paused") is not True:
        soft_skip("6.0b", "the bench is NOT paused",
                  "refusing to start a fire on a running colony. Pause the "
                  "bench and re-run with the same two cells.")
        return

    # INSIDE FIRST so it gets the LOWER id - that is what makes 6.2b
    # non-trivial. `Fire` carries <forceDebugSpawnable>true</forceDebugSpawnable>
    # in Core/Defs/ThingDefs_Misc/Things_Special.xml, which is the FIRST branch
    # of Verse/DebugThingPlaceHelper.IsDebugSpawnable, so no `force:true` is
    # needed even though its category (Attachment) is not one of the four the
    # guard otherwise admits.
    a = send("dev:spawn-thing", {"def": "Fire", "pos": ARGS.fire_inside,
                                 "mode": "direct", "count": 1})
    if dev_gate_shut(a):
        soft_skip("6.1", "dev:spawn-thing is gated",
                  "devMode is off: %s. A closed dev gate is a fixture gap, not "
                  "a failed assertion." % show(dig(a, "error.detail")))
        return
    # ok:true with placed:0 and a `failed[]` block is a REFUSED placement. A
    # staging step that fails silently is indistinguishable from the defect.
    ge("6.1a", "the INSIDE fire was actually placed", a, "data.placed", 1)
    fin = dig(a, "data.spawned.0.id")
    if fin is None and not ARGS.dry_run:
        soft_skip("6.1b", "the inside fire did not spawn",
                  "placed=%s failed=%s" % (show(dig(a, "data.placed")),
                                           show(dig(a, "data.failed"))))
        return
    S.setdefault("fires", []).append(fin)

    b = send("dev:spawn-thing", {"def": "Fire", "pos": ARGS.fire_outside,
                                 "mode": "direct", "count": 1})
    ge("6.1c", "the OUTSIDE fire was actually placed", b, "data.placed", 1)
    fout = dig(b, "data.spawned.0.id")
    if fout is not None:
        S["fires"].append(fout)
    if ARGS.dry_run:
        fin, fout = 900, 901
        S["fires"] = [fin, fout]
    check("6.1d", "and it got the HIGHER id, which is what makes the ordering "
                  "claim below non-trivial",
          isinstance(fin, int) and isinstance(fout, int) and fout > fin,
          "the second spawn's id to exceed the first's", [fin, fout])

    try:
        e = send("fires")
        ids = ids_of(e, "data.list")
        if ARGS.dry_run:
            ids = [fin, fout]
        check("6.2a", "the fire list is emitted ascending, so the inside fire "
                      "comes first", ascending(ids), "ascending ids", ids)
        check("6.2b", "and both staged fires are in it",
              fin in ids and fout in ids, "%s and %s present" % (fin, fout),
              ids)
        rows = {r.get("id"): r for r in as_list(dig(e, "data.list"))
                if isinstance(r, dict)}
        out = rows.get(fout) or (ARGS.dry_run and {"attention_rank": 0,
                                                   "in_home_area": False})
        if isinstance(out, dict):
            check("6.2c", "the fire OUTSIDE the home area holds rank 0 while "
                          "sitting SECOND - at equal size the +10f outside term "
                          "dominates (both fires are freshly spawned at "
                          "Fire.MinFireSize 0.1, and 10 > MaxFireSize 1.75, so "
                          "this does not depend on them being exactly equal), "
                          "because the inside one already has "
                          "Alert_FireInHomeArea and the outside one is a blind "
                          "spot. Position no longer carries that; the field "
                          "does.",
                  out.get("attention_rank") == 0
                  and ids and ids[-1] == fout,
                  "attention_rank 0 at the END of the list",
                  {"rank": out.get("attention_rank"), "ids": ids})
            check("6.2d", "and the line says which side of the home area it is "
                          "on", out.get("in_home_area") is False,
                  "in_home_area false", out.get("in_home_area"))
        # Stated against the BASELINE, not against zero: a colony that was
        # already burning when phase 0 ran does not make this a failure.
        eq("6.2e", "the count is the baseline plus the two we staged", e,
           "data.count", S["fires0"] + 2)

        e = send("fires", {"order": "attention"})
        ids = ids_of(e, "data.list")
        if ARGS.dry_run:
            ids = [fout, fin]
        check("6.3a", "the ranked view puts the outside fire FIRST",
              bool(ids) and ids[0] == fout, "list[0].id == %s" % fout, ids[:1])
        eq("6.3b", "and names the rule it actually used", e, "data.order",
           FIRE_RANK)
        rows = [r for r in as_list(dig(e, "data.list")) if isinstance(r, dict)]
        if ARGS.dry_run:
            rows = [{"attention_rank": 0}]
        check("6.3c", "under that order the rank IS the index",
              all(r.get("attention_rank") == i for i, r in enumerate(rows)),
              "attention_rank == index", [r.get("attention_rank") for r in rows])

        e = send("things", {"category": "resources"})
        check("6.4", "and the `things` route sees exactly the same two fires, "
                     "in the same order - the same list, reached the other way",
              ids_of(e, "data.fire.list") == sorted(ids_of(e, "data.fire.list"))
              and fin in ids_of(e, "data.fire.list")
              and fout in ids_of(e, "data.fire.list") or ARGS.dry_run,
              "an ascending list containing %s and %s" % (fin, fout),
              ids_of(e, "data.fire.list"))
    finally:
        # NOT OPTIONAL. A fire left burning on an unattended bench is how a
        # fixture becomes a crater. One call, both fires - `dev:destroy` takes
        # the plural, and `mode` defaults to vanish: no leavings, no letter.
        live = [f for f in S.get("fires", []) if f is not None]
        if live:
            d = send("dev:destroy", {"things": live})
            eq("6.5a", "both staged fires were destroyed", d, "data.count",
               len(live))
            S["fires"] = []
            f = send("fires")
            eq("6.5b", "and the map is back to the baseline it started at", f,
               "data.count", S["fires0"])


# ---------------------------------------------------------------- the finale --

def finale():
    banner("STANDING CHECK - no red errors across the whole run")
    # `data.count` is the RETURNED-event count (JournalVerbs.Read), so 0 here
    # means no red errors since the watermark. Shape-checked first: a missing
    # `count` key would dig to None and `eq(..., 0)` would then FAIL loudly -
    # but only because the expected value happens to be 0 and not None. Prove
    # the key rather than rely on that luck.
    e = send("journal", {"since_seq": S.get("seq0", 0), "types": ["red_error"],
                         "limit": 50})
    shape("E.1a", "journal", e, "data.count")
    eq("E.1b", "no red error since the watermark - `things` and `fires` are "
               "observers, and the only writes in this run were the deliberate "
               "forbid/unforbid and any dev staging",
       e, "data.count", 0)
    if dig(e, "data.count"):
        for ev in as_list(dig(e, "data.events"))[:5]:
            note("E.1", show(dig(ev, "payload")))


# ---------------------------------------------------------------------- main --

PHASES = {1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5, 6: phase6}


def main():
    global ARGS
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--root", default=DEFAULT_ROOT, help="the protocol root")
    p.add_argument("--phase", type=int, action="append",
                   choices=[0, 1, 2, 3, 4, 5, 6],
                   help="run only these phases (repeatable); phase 0 always runs")
    p.add_argument("--dry-run", action="store_true",
                   help="print the plan and every expectation, send nothing")
    p.add_argument("--echo", action="store_true", help="echo every result envelope")
    p.add_argument("--churn", action="store_true",
                   help="arm phase 4: advance 2500 ticks and re-read")
    p.add_argument("--fire-inside", metavar="X,Z",
                   help="arm phase 6: a cell INSIDE the home area, on bare "
                        "stone or dirt away from anything wooden")
    p.add_argument("--fire-outside", metavar="X,Z",
                   help="arm phase 6: a cell OUTSIDE the home area, same rules")
    ARGS = p.parse_args()

    print("AutoRimmer acceptance - stable thing/fire order (70ac258)")
    print("protocol root: %s" % ARGS.root)
    if not ARGS.dry_run and not os.path.exists(os.path.join(ARGS.root, "status.json")):
        print("%sno status.json under %s - start the bench with "
              "_RimWorld-Agent/run-agent.sh, or pass --root%s" % (RED, ARGS.root, OFF))
        sys.exit(2)
    if not ARGS.dry_run:
        os.makedirs(os.path.join(ARGS.root, "commands"), exist_ok=True)

    wanted = sorted(set(ARGS.phase or [1, 2, 3, 4, 5, 6]) - {0})
    print("phases: 0 + %s + the standing red-error check"
          % ", ".join(str(x) for x in wanted))

    try:
        phase0()
        for n in wanted:
            PHASES[n]()
        finale()
    finally:
        teardown()

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

# ============================================================================
# WHERE THE .md WAS WRONG, and what the shipped code says instead. Every one of
# these was taken from Source/AutoRimmer/ — cited by file and member, never by
# line — and every one is corrected in accept/70ac258-things-stable-order.md.
#
# 1. `forbid` publishes `data.accepted` (a COUNT) and `data.ids`, not the
#    `data.counts.accepted` shape the job verbs use.
#    DesignationVerbs.ForbidCore.
#
# 2. `<LOUD>` cannot just be "the highest id". `ThingAttention` ties break on id
#    ASCENDING (ThingVerbs.Rollups, the `if (detail)` branch), so a SECOND
#    already-forbidden stack with a lower id takes rank 0 and the cap checks
#    fail for a fixture reason. And Designator_Forbid.CanDesignateThing refuses
#    an item that is already forbidden, so the staging would answer accepted:0.
#    The def must have `forbidden_stacks == 0` before the run starts.
#
# 3. `rwa areas` does NOT give the home area. PlaceVerbs.Areas publishes
#    `home_cells` as a TrueCount — an integer — and no cell set. Nothing in the
#    verb surface exposes the home area's footprint, so the two fire cells
#    cannot be derived and must be supplied by a human.
#
# 4. D3's "UNVERIFIED, and the most likely step to fail" is RESOLVED, and the
#    answer is that it will not fail: `Fire` carries
#    <forceDebugSpawnable>true</forceDebugSpawnable> in
#    Core/Defs/ThingDefs_Misc/Things_Special.xml, which is the FIRST branch of
#    Verse/DebugThingPlaceHelper.IsDebugSpawnable. No `force:true` is needed.
#    The .md's guess that the guard keys on category was right about the
#    mechanism and wrong about this def: Fire's category is `Attachment`, which
#    is NOT one of the four the guard admits, so without the override it would
#    indeed have been refused.
#
# 5. D4/D6's `data.count == 2` and `== 0` are wrong on a colony that is already
#    burning. FireScan's `count` is `total - fogged` over EVERY fire on the map.
#    Both are stated against the phase-0 baseline here.
#
# 6. `things[i].at` is `[x, z]` — a two-element array — not the `"x,z"` string
#    the `pos` ARGUMENT accepts. Spatial.cs, Positions.Out vs Positions.Resolve.
#
# 7. A failed envelope carries NO `data` key at all: Poller.BuildResultJson
#    writes `data` or `error`, never both. A3/D0b's assertions have to read
#    `error.code` and `error.detail`.
# ============================================================================
