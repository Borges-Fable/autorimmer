#!/usr/bin/env python3
"""Acceptance runner for 8b0b88f — a REDUNDANT designate order is no longer
indistinguishable from an IMPOSSIBLE one.

Same protocol, helpers and exit codes as `accept/70ac258-things-stable-order.py`
and `accept/4087644-order-honesty.py`; read either header first. There is no
`.ps1` twin: this box has no pwsh and the bench lives here.

    ./accept/8b0b88f-already-designated.py                # everything safe
    ./accept/8b0b88f-already-designated.py --phase 3      # one phase (0 always runs)
    ./accept/8b0b88f-already-designated.py --dry-run      # print the plan, send nothing
    ./accept/8b0b88f-already-designated.py --stage-animal # + phase 5 (SPAWNS A WILD HARE)

Start the bench first (`_RimWorld-Agent/run-agent.sh`), load a colony, and leave
it **PAUSED** — phase 0 makes that a precondition, because an unpaused colonist
can walk over and finish the mining or cutting job between the two calls that
this whole suite compares, which deletes the designation and makes the second
call ACCEPT for a fixture reason that looks exactly like the defect.

WHAT THIS IS TESTING, in one sentence: the game's own gates refuse an
already-designated target with an EMPTY report (`Designator_Mine
.CanDesignateCell` returns `AcceptanceReport.WasRejected`, reason `""`;
`Designator_Plants.CanDesignateThing` and `Designator_Hunt.CanDesignateThing`
return a bare `false`), `DesignateEngine.ReasonOf` correctly refuses to invent
words the game did not say, so every one of those used to arrive as
`{why:"not-designatable", reason:null}` — the same envelope as "this rock is not
mineable" — and `DesignateEngine.AlreadyDesignated` now re-keys it to
`already-designated` on the reject path only.

THE CHECK THAT IS THE WHOLE ISSUE is 4.1c: the reject for an already-designated
plant and the reject for an item that is not a plant at all are IDENTICAL in
every published field — same `at` shape, same `reason` (null), same `removal`
("none") — and differ in `why` alone. Before the fix they did not differ at all.

THE HAZARD THE FIX HAD TO SURVIVE, and phase 3 is the reason this file exists:
`DesignationManager.DesignationOn(Thing, def)` `Log.Error`s on a Cell-targeted
def and `DesignationManager.DesignationAt(IntVec3, def)` `Log.Error`s on a
Thing-targeted one (Verse/DesignationManager.cs, both members). The table is NOT
uniform — Mine and MineVein are `TargetType.Cell`, Hunt/CutPlant/HarvestPlant/
Haul are `TargetType.Thing` (Core/Defs/Misc/Designations/Designations.xml) — so
a per-verb accessor would be a red error waiting for the wrong verb, and a red
error breaches the zero-red-errors invariant. `AlreadyDesignated` dispatches on
`def.targetType`, the game's own discriminator. Phase 3 drives all FOUR
quadrants of that dispatch — Cell-def/cell-route, Cell-def/thing-route,
Thing-def/thing-route, Thing-def/cell-route — and reads the journal for
`red_error` immediately afterwards, not only at the end, so a swapped pair fails
at the phase that caused it.

--dry-run PROVES THE PLAN, NEVER THE PATHS. It sends nothing, so every envelope
is empty, every shape check is skipped and every dig() path looks fine. Only a
live run tells you whether the envelopes are the shape the assertions assume,
which is what phase 0's shape contract is for. A green --dry-run is evidence of
nothing except that the file parses and the phases are wired.

THIS SUITE MUTATES, deliberately and reversibly:
  * it adds Mine and CutPlant designations to ONE rock cell and up to THREE
    plants, and removes them in a teardown that runs even when a check fails or
    a precondition aborts.
  * phase 5 (`--stage-animal`, off by default) spawns one wild Hare, hunts it on
    paper, and destroys it in its own teardown. Without the flag phase 5 hunts
    a wild animal that is already on the map, or soft-skips if there is none.
  * the teardown is `designate cancel` over the fixture CELLS, which is the only
    universal route (`Designator_Cancel.CanDesignateCell` covers both the cell's
    own designations and the things standing in it). It therefore also clears
    any OTHER cancelable designation the player had on those exact cells. Phase
    0 only ever picks cells whose dry-run designate was ACCEPTED, and the game's
    gates reject an already-designated target, so a cell this suite touches
    carried no designation of that def before the run.

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

# The two reject keys the whole issue is about. `DesignateEngine.WhyAlready` is
# the new one; "not-designatable" is the literal in RunCells/RunThings that it
# is carved out of. They are spelled here exactly once so a rename in the mod
# fails every check in this file rather than half of them.
WHY_ALREADY = "already-designated"
WHY_NOT = "not-designatable"

# The verbs, and WHY these two.
#
#   `mine`  -> DesignationDefOf.Mine,     targetType CELL   -> DesignationAt
#   `cut`   -> DesignationDefOf.CutPlant, targetType THING  -> DesignationOn
#
# One from each side of the dispatch, both available on any colony that has
# rock and plants, and both with a gate that refuses an already-designated
# target with an EMPTY report — which is precisely the case that used to be
# unreadable. `cut` rather than `chop`, because `Designator_PlantsCut
# .CanDesignateThing` accepts ANY plant once `isOrder` is set (DesignationVerbs
# sets it), while `Designator_PlantsHarvestWood` demands a harvestable tree.
V_CELL = "mine"
V_THING = "cut"
DEF_CELL = "Mine"
DEF_THING = "CutPlant"

# Phase 5's fallback fixture. `Hare` is Core and its PawnKindDef has no
# defaultFactionDef, so `dev:spawn-pawn` gives it a null faction — which is what
# `Designator_Hunt.CanDesignateThing` requires (`pawn.Faction == null ||
# !pawn.Faction.def.humanlikeFaction`). A tame colony animal is NOT huntable.
STAGE_KIND = "Hare"

# The mineable-cell search, as half-widths around the anchor. The last one is
# 141x141 = 19881 cells, just under `DesignateEngine.MaxCellsCeiling` (20000),
# so it is one envelope rather than a tiling.
SEARCH_HALVES = (10, 35, 70)
MAX_CELLS_CEILING = 20000

# `DesignateEngine.ListCap` — `data.cells` and `data.ids` are capped at this.
LIST_CAP = 64
# `DesignateEngine.RejectCap` — `data.rejects` is capped at this; the TALLY is
# not, which is the truncation contract phase 4 leans on.
REJECT_CAP = 24


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=240):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acc8b0b88f-%03d-%s" % (SEQ, slug)
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
    """dig() cannot tell `absent` from `present and null`, and this suite cares
    more than most: `rejects[i].reason` is PRESENT AND NULL on every
    already-designated reject, because the game's gate gave no words and
    `DesignateEngine.ReasonOf` refuses to invent any. That null IS the
    assertion, and `eq(..., None)` would pass just as happily if the key had
    been dropped from the serializer altogether."""
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


def rows(env, path):
    return [r for r in as_list(dig(env, path)) if isinstance(r, dict)]


def show(v):
    return "null" if v is None else json.dumps(v, separators=(",", ":"))


def xz(at):
    """`Positions.Out` publishes [x, z]; `Positions.Resolve` accepts "x,z". The
    two are not the same shape and confusing them is a bad-args, not a silent
    miss (Spatial.cs, both members)."""
    return "%d,%d" % (int(at[0]), int(at[1]))


def same_cell(at, cell):
    return (isinstance(at, list) and len(at) == 2
            and [int(at[0]), int(at[1])] == [int(cell[0]), int(cell[1])])


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


def eq_int(num, what, env, path, want):
    """eq() against a value this suite READ FROM AN EARLIER ENVELOPE is the
    green-while-asserting-nothing trap wearing a different hat: if the key ever
    disappears, `want` is None and `got` is None and the check passes. Both
    sides must be integers before the comparison counts."""
    got = dig(env, path)
    ok = (isinstance(got, int) and not isinstance(got, bool)
          and isinstance(want, int) and not isinstance(want, bool)
          and got == want)
    check(num, "%s (%s)" % (what, path), ok,
          "the integer %s" % show(want), got)


def ge(num, what, env, path, want):
    got = dig(env, path)
    ok = isinstance(got, (int, float)) and not isinstance(got, bool) and got >= want
    check(num, "%s (%s)" % (what, path), ok, ">= %s" % want, got)


def null_at(num, what, env, path):
    """PRESENT AND NULL, asserted as two facts on one line. This is the shape
    `reason` takes on every already-designated reject, and `eq(..., None)` is
    exactly the wrong tool for it — see has_key()."""
    present = has_key(env, path)
    got = dig(env, path)
    check(num, "%s (%s)" % (what, path), present and got is None,
          "the key PRESENT and null",
          "ABSENT — the serializer dropped it" if not present else got)


def nonempty(num, what, env, path):
    got = dig(env, path)
    ok = isinstance(got, str) and got.strip() != ""
    check(num, "%s (%s)" % (what, path), ok, "a non-empty string", got)


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
    print("          This is a FIXTURE gap, not a failure of 8b0b88f.")
    teardown()
    sys.exit(2)


def soft_skip(num, what, detail):
    """A phase the OPERATOR chose not to arm, or one whose fixture is simply not
    on this map. Distinct from precondition(): it skips ONE phase and the run
    carries on, because an unarmed optional phase must not take the exit code
    with it."""
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
# THIS FILE IS THE WORST CASE FOR THAT, which is why phase 0 is long. The
# defect under test is EXACTLY "a reject whose `reason` is null", so half the
# assertions here are about a null. Get one dig path wrong — `data.rejects` vs
# `data.rejected`, `rejects_by_reason` vs `rejected_by_reason` (both spellings
# exist in the shipped envelope, on the data block and inside
# `data.action` respectively, DesignationVerbs.Designate) — and this suite
# reports a clean pass on a mod that never shipped the fix.
#
# So phase 0 PROVES every envelope key the later phases dig on, naming the verb
# and the key. A shape change then fails HERE, loudly, at a check that says
# which verb moved — instead of downstream, or not at all.
#
# PER-DRIVER ON PURPOSE, not a shared accept/_shapes.py. Every file in accept/
# stands alone and runs from a bare checkout, which is what makes acceptance
# portable across two benches with different tooling.
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
    """The mirror of shape(). The only way to prove a TALLY KEY is not there —
    `rejects_by_reason` is a dict built from the rejects actually seen, so
    "not-designatable is absent" is a real claim about this call and
    `eq(..., 0)` would be a false one."""
    check(num, "`%s` does NOT publish %s — %s" % (verb, path, why),
          not has_key(env, path), "the key to be ABSENT", dig(env, path))


def dev_gate_shut(env):
    """`Dev.Gate` throws a VerbArgsException naming devMode, which the executor
    turns into a bad-args result. That is a CLOSED GATE, not a defect, and the
    two must never be reported the same way."""
    return (dig(env, "ok") is False
            and "devMode" in str(dig(env, "error.detail", "")))


def red_errors(num, what, since):
    """The zero-red-errors invariant, scoped to a window. Phase 3 calls this
    right after the four dispatch quadrants so a swapped `DesignationOn`/
    `DesignationAt` pair fails AT the phase that caused it; the finale calls it
    again over the whole run."""
    e = send("journal", {"since_seq": since, "types": ["red_error"], "limit": 50})
    shape(num + "a", "journal", e, "data.count")
    eq(num + "b", what, e, "data.count", 0)
    if dig(e, "data.count"):
        for ev in rows(e, "data.events")[:5]:
            note(num, show(dig(ev, "payload")))
    return e


# ------------------------------------------------------------------ fixtures --

def designate(dtype, targets, dry=False, extra=None):
    """One `designate` envelope. `targets` is the single target-set key —
    `DesignateEngine.Resolve` treats rect/cells/things/filter as mutually
    exclusive and errors if more than one is given, so this takes a dict of
    exactly one."""
    args = {"type": dtype}
    args.update(targets)
    if dry:
        args["dry_run"] = True
    if extra:
        args.update(extra)
    return send("designate", args)


def already(env, n=1):
    """`rejects_by_reason` counted this call's redundancies and nothing else."""
    return dig(env, "data.rejects_by_reason." + WHY_ALREADY) == n


def stage_mine():
    """Put the Mine designation on the fixture ROCK CELL, idempotently.

    Phases 3 and 4 need it standing whether or not phase 1 ran — `--phase 3`
    alone has to be a real run, not a run that quietly measures an undesignated
    cell. Idempotent means: accepted:1 on the first call, and
    already-designated on any later one, both of which leave the world in the
    state the phase needs."""
    if S.get("mine_staged"):
        return True
    S["cancel_cells"].append(S["cell"])   # registered BEFORE the mutation
    e = designate(V_CELL, {"cells": [xz(S["cell"])]})
    ok = ARGS.dry_run or dig(e, "data.accepted") == 1 or already(e)
    if not ok:
        note("F.1", "staging `%s` on %s neither accepted nor reported %s (%s) — "
                    "the phases that follow are measuring an UNDESIGNATED cell "
                    "and prove nothing"
             % (V_CELL, xz(S["cell"]), WHY_ALREADY,
                show(dig(e, "data.rejects_by_reason") or dig(e, "error"))))
    S["mine_staged"] = True
    return ok


def stage_cut(ids=None):
    """The same, for the fixture PLANTS and the CutPlant designation."""
    want = ids if ids is not None else [S["plant"]]
    fresh = [i for i in want if i not in S["cut_staged"]]
    if not fresh:
        return True
    for i in fresh:
        at = S["plant_at"].get(i)
        if at:
            S["cancel_cells"].append(at)
    e = designate(V_THING, {"things": fresh})
    accepted = dig(e, "data.accepted") or 0
    redundant = dig(e, "data.rejects_by_reason." + WHY_ALREADY) or 0
    ok = ARGS.dry_run or accepted + redundant == len(fresh)
    if not ok:
        note("F.2", "staging `%s` on %s put %s of %d plants under a designation "
                    "(%s) — the phases that follow prove less than they say"
             % (V_THING, fresh, accepted + redundant, len(fresh),
                show(dig(e, "data.rejects_by_reason") or dig(e, "error"))))
    S["cut_staged"].update(fresh)
    return ok


def teardown():
    """Put the map back. Runs on the success path, on a failed check, and on a
    precondition abort — a suite that leaves a rock face marked for mining has
    changed the colony it was only supposed to observe.

    `designate cancel` over CELLS is the universal route: `Designator_Cancel
    .CanDesignateCell` clears the cell's own designations AND walks
    `GetThingList` clearing the things standing in it, so one call retires both
    a Mine (indexed by cell) and a CutPlant or Hunt (indexed by thing)."""
    cells = []
    for c in S.get("cancel_cells", []):
        s = xz(c)
        if s not in cells:
            cells.append(s)
    if cells:
        e = designate("cancel", {"cells": cells})
        print("  %steardown: cancel over %d cell(s) -> accepted=%s rejected=%s%s"
              % (DIM, len(cells), dig(e, "data.accepted"),
                 dig(e, "data.rejected"), OFF))
        S["cancel_cells"] = []
    if S.get("spawned"):
        e = send("dev:destroy", {"things": [S["spawned"]]})
        print("  %steardown: dev:destroy %s -> ok=%s%s"
              % (DIM, S["spawned"], dig(e, "ok"), OFF))
        if dig(e, "ok") is not True:
            note("teardown", "the staged %s (id %s) may still be on the map. "
                             "Remove it by hand: dev:destroy {things:[%s]}"
                 % (STAGE_KIND, S["spawned"], S["spawned"]))
        S["spawned"] = None


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 - the bench, the fixtures, and THE SHAPE CONTRACT")

    e = send("status")
    precondition("0.1a", "the bench answers `status`",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "no status envelope - start _RimWorld-Agent/run-agent.sh")
    precondition("0.1b", "a game is loaded",
                 ARGS.dry_run or dig(e, "data.gameLoaded") is True,
                 "status says gameLoaded=false - load a colony")
    # PAUSED IS A FIXTURE CONDITION, NOT A SPEC CLAIM, so it aborts with exit 2
    # rather than failing a check. Every phase here designates a target and then
    # re-designates it; on a running bench a colonist can complete the mining or
    # plant-cutting job in between, `DesignationManager` drops the designation
    # when the job finishes, and the second call is then legitimately ACCEPTED.
    # That reads on the console exactly like "the fix is not in the assembly".
    precondition("0.1c", "the bench is PAUSED (the agent owns time)",
                 ARGS.dry_run or dig(e, "data.paused") is True,
                 "status says paused=%s. Pause the bench (`pause`) and re-run: "
                 "a colonist who finishes the mine or cut job between two calls "
                 "removes the designation, and the redundant order this suite "
                 "sends is then correctly ACCEPTED."
                 % show(dig(e, "data.paused")))
    print("  %sbench: tick=%s paused=%s%s"
          % (DIM, dig(e, "data.tick"), dig(e, "data.paused"), OFF))

    # THE WATERMARK, and the obvious call gives the wrong answer.
    # JournalVerbs.Read updates last_seq BEFORE the `seq <= since_seq` skip and
    # breaks on `events.Count >= limit` BEFORE appending, so `{limit:1}` stops at
    # the SECOND line and reports ITS seq. Pushing since_seq past the end makes
    # every line fail the skip while still updating last_seq, so the file is read
    # to the end and the value is the true maximum. Getting it wrong is silent:
    # the watermark reads 0 and the red-error check scans the whole journal.
    e = send("journal", {"since_seq": 999999999, "limit": 1})
    shape("0.2a", "journal", e, "data.last_seq")
    shape("0.2b", "journal", e, "data.events", list)
    shape("0.2c", "journal", e, "data.count")
    S["seq0"] = dig(e, "data.last_seq") or 0
    print("  %sjournal watermark: seq0=%s%s" % (DIM, S["seq0"], OFF))

    # ---- the map, and the anchor the mineable search rings out from.
    e = send("map-dump", {"rect": [0, 0, 1, 1]})
    shape("0.3a", "map-dump", e, "data.map.w")
    shape("0.3b", "map-dump", e, "data.map.h")
    S["map"] = [dig(e, "data.map.w") or 0, dig(e, "data.map.h") or 0]

    e = send("pawns", {"filter": "colonist"})
    shape("0.4a", "pawns", e, "data.list", list)
    # `or ARGS.dry_run` on every row-conditional block below, so --dry-run
    # prints the WHOLE plan rather than the part that happens to have a row in
    # an empty envelope. Live, the guard still holds: a shape check on
    # `.0.something` of an empty list is noise, not evidence.
    if rows(e, "data.list") or ARGS.dry_run:
        shape("0.4b", "pawns", e, "data.list.0.id", int)
        shape("0.4c", "pawns", e, "data.list.0.at", list)
    colonists = rows(e, "data.list")
    if ARGS.dry_run:
        colonists = [{"id": 1001, "at": [125, 125]}]
        S["map"] = [250, 250]
    precondition("0.4", "at least one spawned colonist", bool(colonists),
                 "`pawns {filter:'colonist'}` returned an empty list - load a "
                 "colony with someone in it. Phase 4 also needs a colonist's "
                 "cell as its known-not-mineable control.")
    S["anchor"] = [int(colonists[0]["at"][0]), int(colonists[0]["at"][1])]
    S["floor"] = list(S["anchor"])
    print("  %sanchor: colonist %s at %s on a %sx%s map%s"
          % (DIM, colonists[0].get("id"), S["anchor"], S["map"][0], S["map"][1], OFF))

    # ---- THE DESIGNATE SHAPE CONTRACT, and the mineable-cell search in one.
    #
    # A dry-run `designate mine` IS the oracle for "which cells would the game
    # accept": DesignateEngine.RunCells calls Designator_Mine.CanDesignateCell
    # per cell and, with dry_run, never calls DesignateSingleCell. The accepted
    # set therefore excludes both non-mineable cells AND already-designated ones
    # (CanDesignateCell's first test after bounds is
    # `DesignationAt(c, Designation) != null`), which is exactly the fixture
    # this suite needs: a cell that carries no Mine designation yet.
    e = None
    cells = []
    for half in SEARCH_HALVES:
        w = 2 * half + 1
        e = designate(V_CELL,
                      {"rect": [S["anchor"][0] - half, S["anchor"][1] - half, w, w]},
                      dry=True, extra={"max_cells": MAX_CELLS_CEILING})
        cells = as_list(dig(e, "data.cells"))
        if cells or ARGS.dry_run:
            break

    shape("0.5a", "designate", e, "data.verb")
    shape("0.5b", "designate", e, "data.type")
    shape("0.5c", "designate", e, "data.designator")
    shape("0.5d", "designate", e, "data.gate")
    shape("0.5e", "designate", e, "data.designation")
    shape("0.5f", "designate", e, "data.targeted")
    shape("0.5g", "designate", e, "data.requested")
    shape("0.5h", "designate", e, "data.capped")
    shape("0.5i", "designate", e, "data.target_scope")
    shape("0.5j", "designate", e, "data.accepted")
    shape("0.5k", "designate", e, "data.dry_run")
    shape("0.5l", "designate", e, "data.cells", list)
    shape("0.5m", "designate", e, "data.cells_more")
    shape("0.5n", "designate", e, "data.rejected")
    shape("0.5o", "designate", e, "data.rejects", list)
    shape("0.5p", "designate", e, "data.rejects_more")
    shape("0.5q", "designate", e, "data.rejects_by_reason", dict)
    shape("0.5r", "designate", e, "data.designations_before")
    shape("0.5s", "designate", e, "data.designations_now")
    # `Echo` returns null when the crop rect misses the map or the renderer
    # throws, so this is a PRESENCE check and must never be eq(..., None).
    shape("0.5t", "designate", e, "data.crop")
    shape("0.5u", "designate", e, "data.action", dict)
    # PRESENT AND NULL on a dry run — `DesignationVerbs.NoAction` publishes
    # journal_seq:null because nothing was journalled because nothing was done.
    # This is the single clearest case of why has_key exists in this file.
    shape("0.5v", "designate", e, "data.action.journal_seq")
    # The identity of the verb under test, asserted once so a table edit that
    # repoints `mine` at another designator fails here rather than confusing
    # every later phase.
    eq("0.5w", "the cell-route fixture verb is Designator_Mine", e,
       "data.designator", "Designator_Mine")
    eq("0.5x", "and it adds the %s designation" % DEF_CELL, e,
       "data.designation", DEF_CELL)

    # ---- the REJECT ROW shape, on the CELL route. There will be rejects: the
    # ---- search rect is thousands of cells and almost none of them are rock.
    if rows(e, "data.rejects") or ARGS.dry_run:
        shape("0.6a", "designate", e, "data.rejects.0.at", list)
        shape("0.6b", "designate", e, "data.rejects.0.why")
        shape("0.6c", "designate", e, "data.rejects.0.reason")
        shape("0.6d", "designate", e, "data.rejects.0.removal")

    if ARGS.dry_run:
        cells = [[120, 130]]
    precondition("0.7", "a mineable cell within %d tiles of the anchor"
                 % SEARCH_HALVES[-1], bool(cells),
                 "no cell in a %dx%d box around %s passes "
                 "Designator_Mine.CanDesignateCell. Either the colony is on a "
                 "flat map with no exposed rock in range, or everything in "
                 "range is already designated for mining. Move the search by "
                 "moving a colonist nearer a rock face, or cancel the existing "
                 "mine designations."
                 % (2 * SEARCH_HALVES[-1] + 1, 2 * SEARCH_HALVES[-1] + 1, S["anchor"]))
    S["cell"] = [int(cells[0][0]), int(cells[0][1])]
    print("  %smineable fixture cell: %s%s" % (DIM, S["cell"], OFF))

    # ---- the ROCK's thingIDNumber, for the Cell-def / THING-route quadrant.
    #
    # `things {category:"all"}` is ThingRequestGroup.Everything, which DOES
    # include a Mineable (Verse/ListerThings.EverListable excludes only motes
    # and, in region listers, projectiles). Scoped to the single cell, then
    # sieved by the verb itself: whichever id `designate mine --things` accepts
    # on a dry run is the rock, and nothing else at that cell can be.
    e = send("things", {"category": "all", "in": "rect",
                        "rect": [S["cell"][0], S["cell"][1], 1, 1],
                        "detail": True, "detail_cap": 300, "by_location": False})
    shape("0.8a", "things", e, "data.things", list)
    if rows(e, "data.things") or ARGS.dry_run:
        shape("0.8b", "things", e, "data.things.0.id", int)
        shape("0.8c", "things", e, "data.things.0.def")
        shape("0.8d", "things", e, "data.things.0.at", list)
    at_cell = [r["id"] for r in rows(e, "data.things") if isinstance(r.get("id"), int)]
    capped = dig(e, "data.examine_capped")
    if ARGS.dry_run:
        at_cell = [555001, 555002]

    # FALLBACK, and it is not paranoia. `Everything` includes filth, and
    # `ThingVerbs.ExamineCap` stops the walk at 40000 things — a long-running
    # colony can exceed that before the walk reaches a rock face, and the
    # symptom would be an empty list with `examine_capped` set. The second
    # route goes by DEF instead of by pool: `map-dump` over the one cell
    # publishes the rock's defName in `palettes.things` (its `BestThing` ranks
    # building above item above plant, and a Mineable is a building), and
    # `nearest --def` then walks only that def's own lister list.
    if not at_cell and not ARGS.dry_run:
        note("0.8", "the map-wide `all` pool gave nothing at %s%s; falling back "
                    "to map-dump + nearest by def"
             % (S["cell"], " (examine_capped=%s)" % capped if capped else ""))
        md = send("map-dump", {"rect": [S["cell"][0], S["cell"][1], 1, 1],
                               "layers": ["things"]})
        pal = [p for p in as_list(dig(md, "data.palettes.things"))
               if isinstance(p, dict) and p.get("def")]
        if pal:
            nr = send("nearest", {"def": pal[0]["def"], "from": xz(S["cell"]),
                                  "max": 5})
            at_cell = [h["id"] for h in rows(nr, "data.hits")
                       if isinstance(h.get("id"), int)
                       and same_cell(h.get("at"), S["cell"])]

    rock = None
    if at_cell:
        probe = designate(V_CELL, {"things": at_cell}, dry=True)
        shape("0.8e", "designate", probe, "data.ids", list)
        shape("0.8f", "designate", probe, "data.ids_more")
        accepted_ids = [i for i in as_list(dig(probe, "data.ids")) if isinstance(i, int)]
        if accepted_ids:
            rock = accepted_ids[0]
    if ARGS.dry_run:
        rock = 555001
    precondition("0.8", "a mineable THING at %s" % S["cell"], rock is not None,
                 "neither `things {category:'all', in:'rect', rect:[%d,%d,1,1]}`"
                 "%s nor the map-dump/nearest fallback produced an id that "
                 "Designator_Mine.CanDesignateThing accepts (candidates: %s). "
                 "Phase 3's Cell-def / thing-route quadrant cannot run without "
                 "one."
                 % (S["cell"][0], S["cell"][1],
                    " (examine_capped=%s)" % capped if capped else "",
                    at_cell or "none"))
    S["rock"] = rock
    print("  %smineable fixture thing: id %s%s" % (DIM, S["rock"], OFF))

    # ---- the PLANT fixtures, for the Thing-def side. Three of them: phase 4
    # ---- counts a tally, and a tally of one is indistinguishable from a flag.
    e = send("things", {"category": "plants", "detail": True,
                        "detail_cap": 300, "by_location": False})
    shape("0.9a", "things", e, "data.things", list)
    shape("0.9b", "things", e, "data.things_total")
    plant_rows = rows(e, "data.things")
    ids = [r["id"] for r in plant_rows if isinstance(r.get("id"), int)][:LIST_CAP]
    S["plant_at"] = {}
    for r in plant_rows:
        if isinstance(r.get("id"), int) and isinstance(r.get("at"), list):
            S["plant_at"][r["id"]] = [int(r["at"][0]), int(r["at"][1])]
    if ARGS.dry_run:
        ids = [666001, 666002, 666003]
    plants = []
    if ids:
        probe = designate(V_THING, {"things": ids}, dry=True)
        eq("0.9c", "the thing-route fixture verb is Designator_PlantsCut",
           probe, "data.designator", "Designator_PlantsCut")
        eq("0.9d", "and it adds the %s designation" % DEF_THING,
           probe, "data.designation", DEF_THING)
        plants = [i for i in as_list(dig(probe, "data.ids")) if isinstance(i, int)]
        plants = [i for i in plants if i in S["plant_at"]]
    if ARGS.dry_run:
        plants = [666001, 666002, 666003]
        S["plant_at"] = {666001: [120, 131], 666002: [121, 131], 666003: [122, 131]}
    precondition("0.9", "at least three undesignated plants with known cells",
                 len(plants) >= 3,
                 "`designate %s --things` accepted %d of the %d plants on the "
                 "map. Phase 4 counts three redundancies in ONE call, and a "
                 "tally of one cannot be told from a boolean. Let some plants "
                 "grow, or cancel the standing CutPlant designations."
                 % (V_THING, len(plants), len(ids)))
    S["plants"] = plants[:3]
    S["plant"] = S["plants"][0]
    print("  %splant fixtures: %s at %s%s"
          % (DIM, S["plants"], [S["plant_at"][i] for i in S["plants"]], OFF))

    # ---- the CONTROL: a thing that is NOT a plant, so `cut` refuses it with a
    # ---- bare false and therefore with reason null — the same null the
    # ---- redundancy carries. Phase 4 puts the two side by side.
    e = send("things", {"category": "haulable", "detail": True,
                        "detail_cap": 300, "by_location": False})
    shape("0.10a", "things", e, "data.things", list)
    haulables = [r["id"] for r in rows(e, "data.things")
                 if isinstance(r.get("id"), int)][:REJECT_CAP]
    if ARGS.dry_run:
        haulables = [777001]
    control = None
    if haulables:
        probe = designate(V_THING, {"things": haulables}, dry=True)
        shape("0.10b", "designate", probe, "data.rejects", list)
        if rows(probe, "data.rejects") or ARGS.dry_run:
            shape("0.10c", "designate", probe, "data.rejects.0.id", int)
            shape("0.10d", "designate", probe, "data.rejects.0.def")
            shape("0.10e", "designate", probe, "data.rejects.0.label")
            shape("0.10f", "designate", probe, "data.rejects.0.removal")
        # `RejectOut` overwrites a null `reason` with Blockers' own when the
        # thing is a building it cannot clear, so the control has to be one the
        # taxonomy also has nothing to say about — otherwise phase 4 would be
        # comparing a null against a sentence and would pass for the wrong
        # reason.
        for r in rows(probe, "data.rejects"):
            if (r.get("why") == WHY_NOT and r.get("reason") is None
                    and r.get("removal") == "none" and isinstance(r.get("id"), int)):
                control = r["id"]
                break
    if ARGS.dry_run:
        control = 777001
    precondition("0.10", "a non-plant thing that `%s` refuses with no reason"
                 % V_THING, control is not None,
                 "no haulable on the map produces a {why:'%s', reason:null, "
                 "removal:'none'} reject from `designate %s`. Phase 4's whole "
                 "point is that such a reject and an already-designated one "
                 "differ in `why` ALONE; without one there is nothing to "
                 "compare against. Drop any ordinary item on the ground."
                 % (WHY_NOT, V_THING))
    S["control"] = control
    print("  %scontrol thing (not a plant): id %s%s" % (DIM, S["control"], OFF))

    # ---- phase 5's fixture, discovered here so the run says up front whether
    # ---- the Hunt misattribution will be exercised.
    e = send("pawns", {"filter": "wildlife"})
    shape("0.11a", "pawns", e, "data.list", list)
    wild = [r for r in rows(e, "data.list")
            if isinstance(r.get("id"), int) and isinstance(r.get("at"), list)]
    if ARGS.dry_run:
        wild = [{"id": 888001, "at": [124, 126]}]
    S["wild"] = wild
    if not wild and not ARGS.dry_run:
        note("0.11", "no wildlife on the map. Phase 5 (the documented "
                     "Designator_Hunt.CanDesignateCell misattribution) will "
                     "%s." % ("stage a %s" % STAGE_KIND if ARGS.stage_animal
                              else "SKIP - pass --stage-animal to arm it"))


# ------------------------------------------------------------------- phase 1 --
# THE CELL-TARGETED DEF ON THE CELL ROUTE. Mine is TargetType.Cell, so
# `DesignateEngine.AlreadyDesignated` takes its `TargetType.Cell` branch with
# thing == null and asks `DesignationManager.DesignationAt(cell, def)`.

def phase1():
    banner("PHASE 1 - `%s` on a cell: the first order lands, the second is a "
           "REDUNDANCY" % V_CELL)

    cell = xz(S["cell"])
    S["cancel_cells"].append(S["cell"])
    e = designate(V_CELL, {"cells": [cell]})
    eq("1.1a", "the envelope succeeded", e, "ok", True)
    eq("1.1b", "one cell accepted", e, "data.accepted", 1)
    eq("1.1c", "nothing rejected", e, "data.rejected", 0)
    eq("1.1d", "and the tally is empty", e, "data.rejects_by_reason", {})
    ge("1.1e", "the mutation is journalled", e, "data.action.journal_seq", 1)
    before = dig(e, "data.designations_before")
    now = dig(e, "data.designations_now")
    check("1.1f", "the map gained exactly one %s designation "
                  "(designations_before -> designations_now)" % DEF_CELL,
          ARGS.dry_run or (isinstance(before, int) and now == before + 1),
          "now == before + 1", {"before": before, "now": now})
    S["mine_staged"] = True
    S["mine_count"] = 7 if ARGS.dry_run else now

    # ---- THE ASSERTION. The same call again, on a cell that now carries the
    # ---- designation. `Designator_Mine.CanDesignateCell` returns
    # ---- AcceptanceReport.WasRejected — reason "" — so the game supplies the
    # ---- refusal and no words at all.
    e = designate(V_CELL, {"cells": [cell]})
    eq("1.2a", "the envelope still succeeds - a redundancy is not an error",
       e, "ok", True)
    eq("1.2b", "nothing accepted the second time", e, "data.accepted", 0)
    eq("1.2c", "one cell rejected", e, "data.rejected", 1)
    eq("1.2d", "THE FIX: `why` is %r, not %r" % (WHY_ALREADY, WHY_NOT),
       e, "data.rejects.0.why", WHY_ALREADY)
    null_at("1.2e", "and `reason` is the game's silence, kept as null - "
                    "AcceptanceReport.WasRejected carries reason \"\" and "
                    "DesignateEngine.ReasonOf refuses to invent words", e,
            "data.rejects.0.reason")
    check("1.2f", "the reject names the cell it was aimed at",
          ARGS.dry_run or same_cell(dig(e, "data.rejects.0.at"), S["cell"]),
          show(S["cell"]), dig(e, "data.rejects.0.at"))
    eq("1.2g", "the tally counts it under its own key",
       e, "data.rejects_by_reason." + WHY_ALREADY, 1)
    absent("1.2h", "designate", e, "data.rejects_by_reason." + WHY_NOT,
           "a redundancy is no longer filed as an impossibility")
    eq("1.2i", "the whole tally, so a second key cannot hide behind the first",
       e, "data.rejects_by_reason", {WHY_ALREADY: 1})
    eq_int("1.2j", "the redundant order added nothing to the map",
           e, "data.designations_now", S["mine_count"])
    eq_int("1.2k", "and the count did not move across the call",
           e, "data.designations_before", S["mine_count"])
    # Both spellings ship and they live in DIFFERENT PLACES, which is the whole
    # trap: `rejects_by_reason` on the RESPONSE data block
    # (DesignateEngine.PublishRejects), `rejected_by_reason` inside the JOURNAL
    # ROW's action payload (DesignationVerbs.Designate). The response's `action`
    # block is only `{journal_seq}` — measured live 2026-08-31, and an earlier
    # draft of these two checks dug `data.action.rejected_by_reason` and got
    # None for exactly that reason. Proving "the journalled action carries the
    # same tally" therefore means READING THE JOURNAL at the seq it names, which
    # is also the stronger check: it proves the row a later reader would find.
    jseq = dig(e, "data.action.journal_seq")
    payload = {}
    if not ARGS.dry_run and jseq:
        j = send("journal", {"since_seq": int(jseq) - 1, "types": ["action"],
                             "limit": 20})
        for ev in as_list(dig(j, "data.events")):
            if isinstance(ev, dict) and ev.get("seq") == jseq:
                payload = ev.get("payload") or {}
                break
    tally = payload.get("rejected_by_reason")
    check("1.2l", "the JOURNAL ROW at data.action.journal_seq carries the tally "
                  "under the action payload's own spelling `rejected_by_reason`",
          ARGS.dry_run or isinstance(tally, dict),
          "a dict on the row at seq %s" % show(jseq), tally)
    check("1.2m", "and the journalled tally agrees with the data block's",
          ARGS.dry_run or tally == {WHY_ALREADY: 1},
          show({WHY_ALREADY: 1}), tally)


# ------------------------------------------------------------------- phase 2 --
# THE THING-TARGETED DEF ON THE THING ROUTE. CutPlant is TargetType.Thing, so
# AlreadyDesignated takes its `TargetType.Thing` branch with a non-null thing
# and asks `DesignationManager.DesignationOn(thing, def)`.

def phase2():
    banner("PHASE 2 - `%s` on a thing: the same distinction, the other "
           "accessor" % V_THING)

    p = S["plant"]
    at = S["plant_at"][p]
    S["cancel_cells"].append(at)
    e = designate(V_THING, {"things": [p]})
    eq("2.1a", "the envelope succeeded", e, "ok", True)
    eq("2.1b", "the plant is accepted", e, "data.accepted", 1)
    eq("2.1c", "and named by id", e, "data.ids", [p])
    eq("2.1d", "nothing rejected", e, "data.rejected", 0)
    ge("2.1e", "the mutation is journalled", e, "data.action.journal_seq", 1)
    S["cut_staged"].add(p)
    S["cut_count"] = 11 if ARGS.dry_run else dig(e, "data.designations_now")

    # `Designator_Plants.CanDesignateThing` returns a BARE FALSE for a plant
    # that already carries the designation - not even the empty
    # AcceptanceReport Mine gives - so `reason` is null on this route too, and
    # `why` carries the entire distinction.
    e = designate(V_THING, {"things": [p]})
    eq("2.2a", "the envelope still succeeds", e, "ok", True)
    eq("2.2b", "nothing accepted the second time", e, "data.accepted", 0)
    eq("2.2c", "one thing rejected", e, "data.rejected", 1)
    eq("2.2d", "THE FIX, on the DesignationOn side: `why` is %r" % WHY_ALREADY,
       e, "data.rejects.0.why", WHY_ALREADY)
    eq("2.2e", "the reject names the thing", e, "data.rejects.0.id", p)
    null_at("2.2f", "`reason` is null - Designator_Plants.CanDesignateThing "
                    "returns a bare false and says nothing at all", e,
            "data.rejects.0.reason")
    eq("2.2g", "the tally", e, "data.rejects_by_reason", {WHY_ALREADY: 1})
    eq_int("2.2h", "the redundant order added nothing",
           e, "data.designations_now", S["cut_count"])
    # Kept for phase 4's side-by-side: the published shape of a redundancy.
    S["redundant_row"] = dig(e, "data.rejects.0") if not ARGS.dry_run else None


# ------------------------------------------------------------------- phase 3 --
# THE HAZARD, AND THE REASON THIS FILE EXISTS.
#
# `DesignationManager.DesignationOn(Thing, def)` Log.Errors "Designations of
# type X are indexed by location only and you are trying to get one on a Thing";
# `DesignationManager.DesignationAt(IntVec3, def)` Log.Errors the mirror image.
# Neither returns a silent null. A red error breaches the zero-red-errors
# invariant, so a per-verb accessor would have been worse than no check at all.
#
# `DesignateEngine.AlreadyDesignated` dispatches on `def.targetType` — the
# game's own discriminator, the one `DesignationManager.AddDesignation` and
# `IndexDesignation` switch on — which makes a swapped pair UNREPRESENTABLE
# rather than merely untested. This phase drives all four quadrants of that
# dispatch and then reads the journal, so a regression that reintroduces a
# per-verb accessor fails HERE and says which quadrant did it.

def phase3():
    banner("PHASE 3 - all four dispatch quadrants, and NO RED ERROR from any "
           "of them")

    # A window of its own, so the red-error check below is charged to these
    # four calls and to nothing that came before them.
    e = send("journal", {"since_seq": 999999999, "limit": 1})
    seq3 = dig(e, "data.last_seq") or S.get("seq0", 0)
    print("  %swindow opens at seq=%s%s" % (DIM, seq3, OFF))

    stage_mine()
    stage_cut([S["plant"]])
    cell = xz(S["cell"])
    at = S["plant_at"][S["plant"]]

    # ---- Q1. Cell-targeted def, CELL route.  AlreadyDesignated -> Cell branch,
    # ---- thing == null -> DesignationAt(cell, Mine).  (Phase 1 proved the
    # ---- reject key; this restates it as one of the four so the matrix is
    # ---- complete in one place.)
    e = designate(V_CELL, {"cells": [cell]})
    eq("3.1a", "Q1 Cell-def / cell route -> DesignationAt(cell): %r" % WHY_ALREADY,
       e, "data.rejects.0.why", WHY_ALREADY)
    eq("3.1b", "and the designation named is %s (targetType Cell)" % DEF_CELL,
       e, "data.designation", DEF_CELL)

    # ---- Q2. Cell-targeted def, THING route.  AlreadyDesignated -> Cell branch
    # ---- with thing != null -> DesignationAt(THING.Position, Mine), which is
    # ---- what Designator_Mine.CanDesignateThing itself does. Handing this
    # ---- thing to DesignationOn would be the red error.
    e = designate(V_CELL, {"things": [S["rock"]]})
    eq("3.2a", "Q2 Cell-def / THING route -> DesignationAt(thing.Position): %r"
       % WHY_ALREADY, e, "data.rejects.0.why", WHY_ALREADY)
    eq("3.2b", "the reject names the rock", e, "data.rejects.0.id", S["rock"])
    null_at("3.2c", "`reason` is null - Designator_Mine.CanDesignateThing "
                    "returns AcceptanceReport.WasRejected", e,
            "data.rejects.0.reason")
    eq("3.2d", "and Blockers still classifies it as clearable by mining",
       e, "data.rejects.0.removal", "mine")

    # ---- Q3. Thing-targeted def, THING route.  AlreadyDesignated -> Thing
    # ---- branch with thing != null -> DesignationOn(thing, CutPlant).
    e = designate(V_THING, {"things": [S["plant"]]})
    eq("3.3a", "Q3 Thing-def / thing route -> DesignationOn(thing): %r"
       % WHY_ALREADY, e, "data.rejects.0.why", WHY_ALREADY)
    eq("3.3b", "and the designation named is %s (targetType Thing)" % DEF_THING,
       e, "data.designation", DEF_THING)

    # ---- Q4. Thing-targeted def, CELL route.  AlreadyDesignated -> Thing
    # ---- branch with thing == null, so it walks IntVec3.GetThingList and asks
    # ---- DesignationOn per thing. Handing the CELL to DesignationAt would be
    # ---- the red error. This is the quadrant a naive fix gets wrong, because
    # ---- `designate cut --rect` over an already-marked patch is the common
    # ---- real call and the only one where the def's targetType and the
    # ---- caller's target shape disagree.
    e = designate(V_THING, {"rect": [at[0], at[1], 1, 1]})
    eq("3.4a", "Q4 Thing-def / CELL route -> GetThingList + DesignationOn: %r"
       % WHY_ALREADY, e, "data.rejects.0.why", WHY_ALREADY)
    check("3.4b", "the reject names the plant's cell",
          ARGS.dry_run or same_cell(dig(e, "data.rejects.0.at"), at),
          show(at), dig(e, "data.rejects.0.at"))
    eq("3.4c", "one target, one rejection", e, "data.rejected", 1)
    eq("3.4d", "the tally", e, "data.rejects_by_reason", {WHY_ALREADY: 1})

    # ---- THE POINT OF THE PHASE. Four quadrants, two accessors, zero red.
    red_errors("3.5", "NO RED ERROR from any of the four quadrants - a "
                      "DesignationOn/DesignationAt swap Log.Errors "
                      "(Verse/DesignationManager.cs, both members) and would "
                      "land here", seq3)


# ------------------------------------------------------------------- phase 4 --
# DISTINCT FROM `not-designatable`, WHICH IS THE ENTIRE POINT.

def phase4():
    banner("PHASE 4 - the two keys are distinct, and one call can carry both")

    # Only the plants: 4.2 aims at a cell nobody has designated, on purpose, so
    # this phase has no reason to mark a rock face it never reads.
    stage_cut(S["plants"])

    # ---- 4.1 THE CHECK THAT IS THE WHOLE ISSUE.
    #
    # One call, one verb, one target list, TWO kinds of no: three plants that
    # already carry the designation, and one thing that is not a plant at all.
    # Before the fix this answered {"not-designatable": 4} and the agent had no
    # way to tell three wasted orders from one impossible one.
    targets = list(S["plants"]) + [S["control"]]
    e = designate(V_THING, {"things": targets})
    eq("4.1a", "four targets, four refusals", e, "data.rejected", 4)
    eq("4.1b", "nothing accepted", e, "data.accepted", 0)
    eq("4.1c", "THE ISSUE IN ONE ASSERTION: the tally splits 3 redundancies "
               "from 1 impossibility", e, "data.rejects_by_reason",
       {WHY_ALREADY: 3, WHY_NOT: 1})

    # ---- and the two rows are otherwise IDENTICAL, which is why the split had
    # ---- to be a new key rather than a new sentence in `reason`.
    got = rows(e, "data.rejects")
    red = [r for r in got if r.get("why") == WHY_ALREADY]
    imp = [r for r in got if r.get("why") == WHY_NOT]
    check("4.1d", "the reject list carries both kinds of row",
          ARGS.dry_run or (len(red) == 3 and len(imp) == 1),
          "3 rows keyed %r and 1 keyed %r" % (WHY_ALREADY, WHY_NOT),
          [r.get("why") for r in got])
    # UNCONDITIONAL, deliberately. Guarding this behind `if red and imp:` would
    # make the suite quietly run one check fewer exactly when the fix is
    # missing — a suite that reports 122 of 122 on a broken build is the same
    # failure as one that reports a pass on a wrong dig path.
    a = red[0] if red else {}
    b = imp[0] if imp else {}
    check("4.1e", "and they differ in `why` ALONE - same null `reason`, same "
                  "`removal`, same key set. That is exactly why this needed a "
                  "KEY and not a sentence: there was no sentence to add.",
          bool(red) and bool(imp)
          and a.get("reason") is None and b.get("reason") is None
          and a.get("removal") == b.get("removal")
          and set(a.keys()) == set(b.keys())
          and a.get("why") != b.get("why"),
          "reason null on both, removal equal, key sets equal, why different",
          {"redundant": {k: a.get(k) for k in ("why", "reason", "removal")},
           "impossible": {k: b.get(k) for k in ("why", "reason", "removal")}})

    # ---- 4.2 the OTHER shape of a refusal: the game DID give words. Re-keying
    # ---- must not have eaten them, and this reject must not be
    # ---- already-designated. A colonist is standing on this cell, so it is
    # ---- reachable, unfogged and certainly not a rock face.
    #
    # SENT AS A DRY RUN ON PURPOSE, and not for tidiness: `Designator_Mine
    # .CanDesignateCell` returns TRUE for a FOGGED cell — it tests
    # `c.Fogged(map)` before it looks for a Mineable — so a live call aimed at
    # a cell this suite believes is ordinary floor would, if that belief were
    # ever wrong, actually paint a Mine designation on it. The classification
    # runs on the reject path, which dry_run does not skip, so the dry run
    # proves exactly the same thing and can mutate nothing.
    e = designate(V_CELL, {"cells": [xz(S["floor"])]}, dry=True)
    eq("4.2a", "a genuinely impossible mine order is still %r" % WHY_NOT,
       e, "data.rejects.0.why", WHY_NOT)
    nonempty("4.2b", "and it still carries the game's OWN words verbatim - "
                     "Designator_Mine.CanDesignateCell answers "
                     "\"MessageMustDesignateMineable\" here", e,
             "data.rejects.0.reason")
    absent("4.2c", "designate", e, "data.rejects_by_reason." + WHY_ALREADY,
           "nothing is designated on a cell a colonist is standing in")
    eq("4.2d", "the tally", e, "data.rejects_by_reason", {WHY_NOT: 1})

    # ---- 4.3 a DRY RUN classifies identically. The check runs on the reject
    # ---- path, which dry_run does not skip, so an agent can count its own
    # ---- wasted orders BEFORE spending them.
    e = designate(V_THING, {"things": targets}, dry=True)
    eq("4.3a", "a dry run splits the tally the same way", e,
       "data.rejects_by_reason", {WHY_ALREADY: 3, WHY_NOT: 1})
    eq("4.3b", "and mutates nothing", e, "data.dry_run", True)
    shape("4.3c", "designate", e, "data.action.journal_seq")
    eq("4.3d", "so there is no journal line", e, "data.action.journal_seq", None)


# ------------------------------------------------------------------- phase 5 --
# THE DOCUMENTED MISATTRIBUTION.
#
# `Designator_Hunt.CanDesignateCell` answers "MessageMustDesignateHuntable" when
# the true cause is already-designated, because its `HuntablesInCell` filters
# through `CanDesignateThing`, which drops animals that already carry the Hunt
# designation - so a cell whose animals are all marked looks to it like a cell
# with no huntables in it. The merged code does NOT correct that string. It
# re-keys `why` and keeps `reason` verbatim, on the stated ground that a reason
# we deleted or invented would be worse than the game's own inaccurate one
# (DesignateEngine.RunCells, the KNOWN MISATTRIBUTION comment). This phase
# asserts that, because "documented at the call site" is only true if the
# envelope actually behaves the way the comment says.

def phase5():
    banner("PHASE 5 - Designator_Hunt.CanDesignateCell's wrong reason, kept "
           "verbatim under a correct `why`")

    animal = None
    at = None
    # ONE envelope for the whole roster, not one per animal: `designate hunt`
    # is plural by construction and its `data.ids` is the accepted subset in
    # target order, so the verb sieves the candidates in a single call.
    wild = S.get("wild", [])[:LIST_CAP]
    if wild:
        where = {r["id"]: [int(r["at"][0]), int(r["at"][1])] for r in wild}
        probe = designate("hunt", {"things": list(where)}, dry=True)
        for i in as_list(dig(probe, "data.ids")):
            if isinstance(i, int) and i in where:
                animal, at = i, where[i]
                break

    if animal is None and ARGS.stage_animal and not ARGS.dry_run:
        note("5.0", "staging a wild %s at the anchor" % STAGE_KIND)
        st = send("dev:spawn-pawn", {"kind": STAGE_KIND, "pos": xz(S["anchor"]),
                                     "count": 1, "spread": 3})
        if dev_gate_shut(st):
            soft_skip("5.0", "phase 5 needs a huntable wild animal",
                      "no wildlife on the map and dev:spawn-pawn is gated: %s. "
                      "Turn devMode on, or run this on a map with animals."
                      % show(dig(st, "error.detail")))
            return
        # `DevVerbs.SpawnPawn` publishes `data.pawns` as `Dev.Describe` rows —
        # id, kind, faction, and `at` ONLY when the pawn is spawned. A `Hare`
        # gets a null faction from `FactionUtility.DefaultFactionFrom(null)`,
        # which is what makes it huntable at all, so the gate itself is the
        # confirmation and not the roster classification.
        shape("5.0a", "dev:spawn-pawn", st, "data.pawns", list)
        made = [m for m in rows(st, "data.pawns")
                if isinstance(m.get("id"), int) and isinstance(m.get("at"), list)]
        if not made:
            soft_skip("5.0", "phase 5 needs a huntable wild animal",
                      "dev:spawn-pawn returned no spawned pawn: %s"
                      % show(dig(st, "data") or dig(st, "error")))
            return
        S["spawned"] = made[0]["id"]
        probe = designate("hunt", {"things": [S["spawned"]]}, dry=True)
        if S["spawned"] in [i for i in as_list(dig(probe, "data.ids"))
                            if isinstance(i, int)]:
            animal = S["spawned"]
            at = [int(made[0]["at"][0]), int(made[0]["at"][1])]
        else:
            soft_skip("5.0", "phase 5 needs a huntable wild animal",
                      "the staged %s (id %s) is refused by "
                      "Designator_Hunt.CanDesignateThing: %s"
                      % (STAGE_KIND, S["spawned"],
                         show(dig(probe, "data.rejects"))))
            return

    if ARGS.dry_run:
        animal, at = 888001, [124, 126]

    if animal is None:
        soft_skip("5.0", "phase 5 needs a huntable wild animal",
                  "`pawns {filter:'wildlife'}` offered %d candidate(s) and "
                  "Designator_Hunt.CanDesignateThing accepted none of them. "
                  "Re-run with --stage-animal (devMode required) to spawn a "
                  "%s, or run on a map with wildlife. THE HUNT MISATTRIBUTION "
                  "IS THEN UNPROVEN - it is part of 8b0b88f's acceptance."
                  % (len(S.get("wild", [])), STAGE_KIND))
        return

    print("  %shunt fixture: pawn %s at %s%s" % (DIM, animal, at, OFF))
    S["cancel_cells"].append(at)

    e = designate("hunt", {"things": [animal]})
    eq("5.1a", "the animal is marked for hunting", e, "data.accepted", 1)
    eq("5.1b", "the gate cited is the Thing one", e, "data.gate",
       "RimWorld/Designator_Hunt.CanDesignateThing")
    eq("5.1c", "and the designation is Hunt (targetType Thing)",
       e, "data.designation", "Hunt")

    # THING route: a bare false, so reason is null and `why` carries everything.
    e = designate("hunt", {"things": [animal]})
    eq("5.2a", "the thing route reports %r" % WHY_ALREADY,
       e, "data.rejects.0.why", WHY_ALREADY)
    null_at("5.2b", "with `reason` null - Designator_Hunt.CanDesignateThing "
                    "returns a bare false", e, "data.rejects.0.reason")

    # CELL route, twice: the first call marks any OTHER huntable sharing the
    # cell (there is usually none, but a herd tile would otherwise leave one
    # unmarked and make the second call accept), the second is the assertion.
    designate("hunt", {"rect": [at[0], at[1], 1, 1]})
    e = designate("hunt", {"rect": [at[0], at[1], 1, 1]})
    eq("5.3a", "THE MISATTRIBUTION, classified correctly: `why` is %r"
       % WHY_ALREADY, e, "data.rejects.0.why", WHY_ALREADY)
    nonempty("5.3b", "and `reason` is the game's OWN wrong words, kept verbatim "
                     "rather than deleted - Designator_Hunt.CanDesignateCell "
                     "answers \"MessageMustDesignateHuntable\" because "
                     "HuntablesInCell filters through CanDesignateThing, which "
                     "drops the animal it just marked", e,
             "data.rejects.0.reason")
    eq("5.3c", "the gate cited on this route is still the Thing one - the "
               "table cites one member per type", e, "data.gate",
       "RimWorld/Designator_Hunt.CanDesignateThing")
    eq("5.3d", "the tally keys on `why`, so the wrong sentence costs the ledger "
               "nothing", e, "data.rejects_by_reason", {WHY_ALREADY: 1})
    note("5.3", "`why` is the classification and `reason` answers a different "
                "question. That is deliberate and recorded at the call site "
                "(DesignateEngine.RunCells, KNOWN MISATTRIBUTION): a reason we "
                "deleted or invented would be worse than the game's own "
                "inaccurate one.")


# ---------------------------------------------------------------- the finale --

def finale():
    banner("STANDING CHECK - no red errors across the whole run")
    # `data.count` is the RETURNED-event count (JournalVerbs.Read), so 0 here
    # means no red errors since the watermark. Shape-checked first: a missing
    # `count` key would dig to None and `eq(..., 0)` would then FAIL loudly -
    # but only because the expected value happens to be 0 and not None. Prove
    # the key rather than rely on that luck.
    red_errors("E.1", "no red error since the watermark - every designate in "
                      "this run went through DesignateEngine.AlreadyDesignated, "
                      "and the whole hazard is that the wrong accessor "
                      "Log.Errors instead of returning null", S.get("seq0", 0))


# ---------------------------------------------------------------------- main --

PHASES = {1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5}


def main():
    global ARGS
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--root", default=DEFAULT_ROOT, help="the protocol root")
    p.add_argument("--phase", type=int, action="append",
                   choices=[0, 1, 2, 3, 4, 5],
                   help="run only these phases (repeatable); phase 0 always runs")
    p.add_argument("--dry-run", action="store_true",
                   help="print the plan and every expectation, send nothing")
    p.add_argument("--echo", action="store_true", help="echo every result envelope")
    p.add_argument("--stage-animal", action="store_true",
                   help="arm phase 5 on a map with no wildlife: dev:spawn-pawn "
                        "a %s, hunt it on paper, destroy it in teardown "
                        "(devMode required)" % STAGE_KIND)
    ARGS = p.parse_args()

    S["cancel_cells"] = []
    S["cut_staged"] = set()
    S["spawned"] = None

    print("AutoRimmer acceptance - already-designated is its own reject key (8b0b88f)")
    print("protocol root: %s" % ARGS.root)
    if not ARGS.dry_run and not os.path.exists(os.path.join(ARGS.root, "status.json")):
        print("%sno status.json under %s - start the bench with "
              "_RimWorld-Agent/run-agent.sh, or pass --root%s" % (RED, ARGS.root, OFF))
        sys.exit(2)
    if not ARGS.dry_run:
        os.makedirs(os.path.join(ARGS.root, "commands"), exist_ok=True)

    wanted = sorted(set(ARGS.phase or [1, 2, 3, 4, 5]) - {0})
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
# WHAT THE MERGED CODE ACTUALLY DOES, read from Source/AutoRimmer/ and cited by
# file and member, never by line. Every assertion above is keyed to one of
# these; where the issue text and the shipped code differ, the code wins and
# the difference is named.
#
# 1. `DesignateEngine.AlreadyDesignated(map, def, cell, thing)` runs ONLY on the
#    reject path, in `DesignateEngine.RunCells` and `DesignateEngine.RunThings`,
#    after the game's own gate has already said no. The accept path is
#    untouched, which is why 1.1c/2.1b can assert a plain accepted:1 and why
#    4.3a can assert the same tally on a dry run.
#
# 2. It dispatches on `DesignationDef.targetType`, not on the verb:
#      TargetType.Thing + a thing  -> DesignationOn(thing, def)
#      TargetType.Thing + a cell   -> IntVec3.GetThingList, DesignationOn each
#      TargetType.Cell  + a thing  -> DesignationAt(thing.Position, def)
#      TargetType.Cell  + a cell   -> DesignationAt(cell, def)
#    Those are phase 3's Q3, Q4, Q2, Q1 in that order. Mine and MineVein are
#    TargetType.Cell; Hunt, CutPlant, HarvestPlant, Haul and Flick are
#    TargetType.Thing (Core/Defs/Misc/Designations/Designations.xml).
#
# 3. The issue says "Hunt is targetType Thing - use DesignationOn(thing, def)",
#    which is true but incomplete: `designate hunt --rect` reaches RunCells with
#    a Thing-targeted def and NO thing, and that is the quadrant a per-verb
#    accessor gets wrong. Phase 3 Q4 is that case, on `cut` because it needs no
#    animal; phase 5 repeats it on `hunt` itself.
#
# 4. `reason` is the GAME's AcceptanceReport string verbatim or null, never a
#    phrase of ours (DesignateEngine, the REJECTIONS contract). On the
#    already-designated path it is null for Mine (`AcceptanceReport.WasRejected`,
#    reason ""), null for Plants and Hunt on the thing route (a bare `false`),
#    and NON-NULL and WRONG for Hunt on the cell route
#    ("MessageMustDesignateHuntable"). 1.2e, 2.2f, 3.2c, 5.2b assert the nulls;
#    5.3b asserts the wrong one is kept rather than deleted.
#
# 5. `RejectOut` will OVERWRITE a null `reason` with `Blockers.Classify`'s own
#    when the target is a building the taxonomy has words for. That is why
#    phase 0's control fixture is selected on {reason:null, removal:"none"} and
#    not merely on "the first haulable" - otherwise 4.1e would be comparing a
#    null against a sentence and would fail for a fixture reason.
#
# 6. TWO SPELLINGS OF THE TALLY SHIP, in TWO DIFFERENT PLACES, and a driver
#    that digs the wrong one gets None and passes. `rejects_by_reason` is on the
#    RESPONSE data block (DesignateEngine.PublishRejects).
#    `rejected_by_reason` is on the JOURNAL ROW's action payload
#    (DesignationVerbs.Designate) — NOT on the response's `action` block, which
#    carries `journal_seq` and nothing else. Measured live 2026-08-31: a first
#    draft of 1.2l/m dug `data.action.rejected_by_reason` and got None, which is
#    the absent-key trap this suite exists to close, committed inside the suite
#    itself. 1.2l/m now fetch the journal at the seq the action block names,
#    which also proves the row a later reader would actually find.
#
# 7. RESIDUAL, recorded in DesignationVerbs.Designate and deliberately NOT
#    asserted here because it is not the fixed behaviour: `designate mine` over
#    a cell that carries a MINE-VEIN designation still reports
#    not-designatable, because `Designator_Mine.CanDesignateThing` rejects on
#    `DesignationAt(t.Position, DesignationDefOf.MineVein)` - a def that is not
#    this entry's. Telling that one apart means re-implementing the widget's
#    second clause. If a future round takes that on, this file is where the
#    check belongs.
#
# 8. `designations_before` / `designations_now` are the count of that def
#    STANDING ON THE MAP (DesignateEngine.CountOf over
#    SpawnedDesignationsOfDef), not "how many we added". 1.1g and 1.2j read
#    them as the independent witness that a redundancy changed nothing.
# ============================================================================
