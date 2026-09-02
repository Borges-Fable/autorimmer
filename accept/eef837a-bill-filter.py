#!/usr/bin/env python3
"""Acceptance runner for eef837a (the butcher bill that matched nothing) and
d9d6c12 (the bill-asleep flag) — the defect that ended run m1-20260901.

    ./accept/eef837a-bill-filter.py              # everything except 9
    ./accept/eef837a-bill-filter.py --phase 3    # one phase (0 always runs)
    ./accept/eef837a-bill-filter.py --dry-run    # print the plan, send nothing
    ./accept/eef837a-bill-filter.py --selftest   # phase 9 only: NO bench needed
    ./accept/eef837a-bill-filter.py --rot        # add phase 7 (advances 2.6 days)

Read `accept/1adc737-place-layout.py`'s header for the protocol and the exit
codes, `accept/61794cd-bleed-triage.py`'s for the shape-contract rule this file
obeys twice over: **`eq(..., None)` passes on an ABSENT key**, and the whole of
eef837a item 3 is about a key that was absent reading as a key that was null.
Every assertion about a null here is preceded by a `shape()`.

WHAT THE ISSUE SAID, AND WHAT THE ARTIFACTS SAY — because the two differ, and a
suite that asserted the issue's own story would have gone green on a mod that
still starved the colony. Established from `RUNS/m1-20260901/saves/` and from
the game's source, and re-derived offline in phase 9 so it cannot be argued
away:

  * **`bill-add` was never broken.** `RimWorld/BillUtility.cs MakeNewBill` ends
    in the `Bill(RecipeDef, Precept_ThingStyle)` ctor, whose body is
    `ingredientFilter = new ThingFilter(); ingredientFilter.CopyAllowancesFrom(
    recipe.defaultIngredientFilter);`. `day-46.rws` — the save from the day the
    bill was created — has that bill at **115 allowed defs, every one an animal
    corpse**: `Corpse_WildBoar` in, `Corpse_Human` out, no mechanoid corpse.
    That is `ButcherCorpseFlesh.defaultIngredientFilter` (`CorpsesAnimal`)
    exactly. The filter was right for the entire run.
  * **`bill-set` PARTLY persisted, and the part that did not is item 2.** The
    verb reported `defs_delta: 39` and `allowed_defs: 154` over that base of
    115 (115+39=154, so the report was internally consistent). `day-66.rws`
    holds **127**: human and unnatural corpses survived, every mechanoid corpse
    def was gone. `Bill.ExposeData`'s SAVING-pass narrowing had deleted
    **27 of the 39**. So item 2's premise is real and its magnitude is 27.
  * **What actually killed the colony** was
    `ButcherCorpseFlesh.fixedIngredientFilter`'s
    `<specialFiltersToDisallow><li>AllowRotten</li>`, evaluated PER THING by
    `Verse/ThingFilter.cs Allows(Thing)`'s last clause and consulted by
    `Bill.IsFixedOrAllowedIngredient` BEFORE the bill's own filter.
    `day-62.rws` has `Corpse_WildBoar` standing on the butcher spot's own cell
    (114,138), unforbidden, at `rotProg 183767` — past `CompRottable`'s
    150,000-tick rot start. The agent read "hp 95%" and called it fresh. No
    `bill-set` could ever have fixed it, and the def-level filter summary said
    `Corpse_WildBoar` was allowed the whole time, because at the DEF level it
    was.

So this suite tests the fix that follows from the artifacts, not from the
issue's diagnosis: **the observation surface must give a thing-level answer and
name the clause that rejected each candidate.**

  * PHASE 1 — eef837a item 1. `bill-add` on a butcher spot: `filter_defs`
    EQUALS `recipe_default_defs`, `filter_state` is `published`, and the
    filter summary carries BOTH special-filter lists with `AllowRotten` in the
    universe's. The equality is the assertion the issue asked for, in the form
    that survives a modded `MakeNewBill` override.
  * PHASE 2 — eef837a item 3. `filter_state` distinguishes `published` from
    `empty`, both proved by staging them, and `ingredient_filter` is a DICT in
    both — never the bare `null` that used to mean two different things. The
    key's presence is asserted before its value, per the shape contract.
  * PHASE 3 — eef837a item 2. `bill-set {allow:["Corpses"]}` on a fixed-filter
    recipe: `defs_withheld > 0`, the withheld defs appear in `refused` with the
    mechanism named, `will_not_persist` is EMPTY, and — the actual acceptance
    bullet — the filter READS BACK through `bills` at the same size the write
    reported. A separate read verb, never the mutation's own echo.
  * PHASE 4 — eef837a item 1's "proving it" half and the thing-level answer. A
    fresh animal corpse in reach: `health:"workable"`, `usable >= 1`, and the
    sample row's `rot_stage` is `Fresh`. Then the corpse is FORBIDDEN and the
    same read must flip to `usable:0` with `rejected.forbidden` and a `remedy`
    naming `unforbid` — the diagnosis the run never got.
  * PHASE 5 — d9d6c12, all three items. The bill is starved and time is run
    until `WorkGiver_DoBill` actually fails a search on it, then:
    `ingredient_search.state:"asleep"` with a wake tick and a positive
    `consecutive_failed_searches`; `health` is
    `asleep-no-matching-ingredient`; and after the ingredient is restored the
    same bill reads `workable` or `asleep-will-retry`. Item 3's requirement —
    that "asleep and will retry" not present identically to "asleep against a
    filter that can never match" — is asserted as an INEQUALITY between the two
    staged states, which is the only form that cannot be satisfied by a
    constant.
  * PHASE 6 — the colony-level claim, and the only one that answers "would this
    have saved them". `consume` is sent at a raw corpse and must refuse
    (`cannot-eat`), which is what makes butchering the only route; a colonist is
    starved with `dev:set-need`; the butcher bill is left live with a fresh
    corpse in reach; time runs; and the colonist's Food need must RISE. Meat
    appearing is checked too, but the need is the claim.
  * PHASE 7 — the rotten case, ON A REAL CLOCK, and OFF BY DEFAULT (`--rot`).
    It advances past `CompRottable.TicksToRotStart` (150,000) so the staged
    corpse genuinely rots, then asserts the rejection reason is
    `recipe-fixed:special:AllowRotten` and the remedy says no bill lever fixes
    it. Off by default because it is 2.6 game days of wall clock; phase 9
    proves the same mechanism from the defs and from the run's own save.
  * PHASE 8 — the standing invariant: no red errors across the whole run.
  * PHASE 9 — offline. THE EVIDENCE, not a mock:
      - `Core/Defs/RecipeDefs/Recipes_Butchery.xml` still carries
        `specialFiltersToDisallow: AllowRotten` and
        `defaultIngredientFilter: CorpsesAnimal`;
      - the decompiled 1.6 source still has `Bill(RecipeDef, …)`'s
        `CopyAllowancesFrom(recipe.defaultIngredientFilter)`,
        `Bill.ExposeData`'s SAVING-pass narrowing, and
        `ReCheckFailedBillTicksRange = new IntRange(500, 600)`;
      - `BillWatch.Cadence` is BELOW that minimum, which is the whole
        correctness argument for the failure count;
      - `RUNS/m1-20260901/saves/day-46.rws` has the bill at 121 defs and
        `day-62.rws` has the boar on the spot's own cell already rotting.
    Each of those is a claim this suite makes elsewhere, checked against the
    thing it is a claim about. Plus the usual helper self-checks over
    deliberately broken envelopes.

WHAT THIS SUITE DOES NOT PROVE, in those words:

  * **That a colonist will path to and butcher a corpse under arbitrary
    conditions.** Phase 6 proves it for its own fixture — one fresh corpse, one
    hungry colonist with Cooking enabled, a butcher spot in reach. Reachability,
    reservation and work-priority scheduling are not this issue's surface and
    the diagnosis block says so in its own `clauses_not_checked`.
  * **The rotten-corpse rejection on a live bench**, unless `--rot` is passed.
    By default the mechanism is proved from the game's own def and from the
    run's save, and the LIVE assertion is the forbidden case, which stages
    instantly and exercises the identical code path.
  * **Anything about `Bill.ExposeData` observed through a save.** DESIGN's
    2026-08-31 entry rules that reader out for bill filters: saving PERTURBS
    the filter it would report. Phase 3 uses a live read plus a game-acted
    change, and `will_not_persist` runs ExposeData's predicate as a question.

IT WRECKS THE COLONY IT RUNS ON: it spawns and kills animals, starves a
colonist, forbids things and leaves a butcher spot and bills behind.

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

# The two boxes this repo runs on put the decompiled source and the game data in
# different places (repo CLAUDE.md), and neither is guaranteed present. Phase 9
# NOTES rather than FAILS when a source is missing: a check that cannot be run
# is not a check that passed.
DECOMP_CANDIDATES = [
    os.environ.get("RIMWORLD_DECOMP"),
    "/home/dorian/projects/rimworld-tools/Info/decompiled/RimWorldBase",
    os.path.join(VAULT, "..", "misc", "rimworld", "reference", "decompiled", "RimWorldBase"),
]
DATA_CANDIDATES = [
    os.environ.get("RIMWORLD_DATA"),
    os.path.join(VAULT, "_RimWorld-Agent", "Data"),
    os.path.expanduser("~/.local/share/Steam/steamapps/common/RimWorld/Data"),
    os.path.expanduser("~/.steam/steam/steamapps/common/RimWorld/Data"),
]

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
CAPTURE = None  # phase 9 only

# Asserted registered in phase 0: a verb that failed to register produces
# downstream failures indistinguishable from a bad fixture.
OPS = ["bills", "bill-add", "bill-set", "bill-remove", "bill-options",
       "things", "pawns", "pawn", "prioritize", "consume", "forbid", "unforbid",
       "advance", "journal", "status", "pause", "unpause", "find-rect",
       "dev:spawn-thing", "dev:spawn-pawn", "dev:damage", "dev:set-need"]

# The fixture, all of it from vanilla Core.
BENCH_DEF = "ButcherSpot"
RECIPE = "ButcherCorpseFlesh"
WORKGIVER = "DoBillsButcherFlesh"       # a WorkGiverDef, NOT a work type
ANIMAL_KIND = "Deer"
CORPSE_DEF = "Corpse_Deer"
MEAT_DEF = "Meat_Deer"
COOKING_WORK = "Cooking"

# `Verse/CompProperties_Rottable.cs` via `RimWorld/ThingDefGenerator_Corpses.cs`:
# a flesh corpse gets daysToRotStart 2.5 -> 150,000 ticks. Phase 7 must cross it
# and phase 9 re-derives it from the decompiled source.
TICKS_TO_ROT_START = 150000

# `RimWorld/WorkGiver_DoBill.cs ReCheckFailedBillTicksRange`. Phase 5 must run
# time past it for a real failed search to register, and phase 9 re-derives it.
RECHECK_MAX = 600

# What phase 9 reads out of the run's own saves. Both counts are `<allowedDefs>`
# entries on `ButcherSpot103250`'s bill, measured, not inferred:
#   day-46 — the bill `bill-add` created (loadID 6): 115 defs, every one a
#            Corpse_* of an ANIMAL. No Corpse_Human, no mech corpse. That is
#            `ButcherCorpseFlesh.defaultIngredientFilter` (CorpsesAnimal).
#   day-66 — after the re-add and the `allow:["Corpses"]` write (loadID 8):
#            127, now including Corpse_Human and UnnaturalCorpse_Human and still
#            no mechanoid corpse. The verb reported `defs_delta: 39` over a base
#            of 115, i.e. 154; 127 survived. TWENTY-SEVEN of the 39 evaporated.
RUN_SAVES = os.path.join(REPO, "RUNS", "m1-20260901", "saves")
DAY46_DEFS = 115
DAY66_DEFS = 127
RUN_REPORTED_DELTA = 39   # what bill-set told the run
RUN_REPORTED_TOTAL = 154  # …and the allowed_defs it printed beside it
BOAR_CELL = "(114, 0, 138)"

# The verdicts BillIngredients.Diagnose can publish. Asserted as a closed set
# wherever `health` is read, so a new value has to be looked at rather than
# sliding through a `one_of` that was never updated.
HEALTH_VALUES = ["workable", "asleep-will-retry", "asleep-no-matching-ingredient",
                 "no-matching-ingredient", "filter-empty", "no-ingredient-filter",
                 "suspended", "research-missing", "unknown"]

# BillIngredients.Diagnose's key set, asserted as a SET: a field appearing here
# is either a deliberate addition or something leaking, and both must be looked
# at. Phase 9 re-derives it from the shipped source.
DIAG_KEYS = ["filter_state", "ingredient_filter", "recipe_default_defs", "filter_defs",
             "ingredient_search", "ingredient_match", "health", "remedy", "health_note"]

# BillWatch.Block's key set, minus the `asleep_for_ticks` it adds only when it
# has actually observed a streak (asserted separately in phase 5).
SEARCH_KEYS = ["state", "wakes_tick", "wakes_in_ticks", "consecutive_failed_searches",
               "asleep_since_tick", "observed", "observed_since_tick", "note"]


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=240):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acceef837a-%03d-%s" % (SEQ, slug)
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


# THE ESCAPES, AND WHY THEY ARE ON. `722c951` makes `advance` refuse while the
# journal carries an unread delta and halt on an own-faction downing. This
# suite's fixture is a colonist starved to the edge with `dev:set-need`, and
# every verb it sends journals — so without the escapes not one advance here
# would start. Injected in ONE place with a reason naming this file, never per
# call site where they would accumulate silently.
ESCAPE = ("accept/eef837a-bill-filter.py: this suite starves a colonist on "
          "purpose (eef837a phase 6) and journals on every verb")


def advance(args, timeout=600):
    a = dict(args)
    a.setdefault("unread_ok", ESCAPE)
    a.setdefault("through_casualties", ESCAPE)
    return send("advance", a, timeout=timeout)


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
    """dig() cannot tell `absent` from `present and null`, and eef837a item 3 is
    exactly that distinction: `filter: null` used to mean both "this bill has no
    ingredientFilter" and "the summary threw and the key was never written"."""
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
    return "null" if v is None else json.dumps(v, separators=(",", ":"))[:500]


def num(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


# ------------------------------------------------------------------- asserts --

def check(n, what, ok, expected, actual):
    global CHECKS
    CHECKS += 1
    if CAPTURE is not None:
        CAPTURE.append(ok)
        if not ok:
            FAILS.append(n)
        return
    if ARGS.dry_run:
        print("  %-7s EXPECT  %s: %s" % (n, what, expected))
        return
    if ok:
        print("  %s%-7s PASS    %s%s" % (GREEN, n, what, OFF))
        return
    print("  %s%-7s FAIL    %s%s" % (RED, n, what, OFF))
    print("          expected: %s" % expected)
    print("          actual:   %s" % show(actual))
    FAILS.append(n)


def eq(n, what, env, path, want):
    got = dig(env, path)
    ok = (want is None and got is None) or got == want
    check(n, "%s (%s)" % (what, path), ok, show(want), got)


def eq_val(n, what, got, want):
    check(n, what, got == want, show(want), got)


def ge(n, what, env, path, want):
    got = dig(env, path)
    ok = num(got) and got >= want
    check(n, "%s (%s)" % (what, path), ok, ">= %s" % want, got)


def ge_val(n, what, got, want):
    check(n, what, num(got) and got >= want, ">= %s" % want, got)


def ne_val(n, what, a, b):
    check(n, what, a != b, "anything but %s" % show(b), a)


def contains(n, what, env, path, needle):
    got = dig(env, path)
    ok = isinstance(got, str) and needle.lower() in got.lower()
    check(n, "%s (%s)" % (what, path), ok, "a string containing %r" % needle, got)


def one_of(n, what, env, path, allowed):
    got = dig(env, path)
    check(n, "%s (%s)" % (what, path), got in allowed, "one of %s" % (allowed,), got)


def shape(n, verb, env, path, kind=None):
    ok = has_key(env, path)
    want = "the key to be present"
    if ok and kind is not None:
        ok = isinstance(dig(env, path), kind)
        want += " and a %s" % kind.__name__
    check(n, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def keys_exactly(n, what, env, path, want):
    got = dig(env, path)
    if not isinstance(got, dict):
        check(n, "%s (%s)" % (what, path), False, "a dict at that path", got)
        return
    extra = sorted(set(got) - set(want))
    missing = sorted(set(want) - set(got))
    check(n, "%s (%s)" % (what, path), not extra and not missing,
          "exactly %s" % (sorted(want),), {"extra": extra, "missing": missing})


def note(n, text):
    if CAPTURE is not None:
        return
    print("  %s%-7s NOTE    %s%s" % (YELLOW, n, text, OFF))


def finding(n, text):
    """A DEFECT IN THE SHIPPED MOD, reported rather than asserted — the exit
    code answers "were the acceptance bullets met", and a suite that goes
    permanently red over a metadata string teaches the next session to ignore
    its own colour."""
    FINDINGS.append((n, text))
    if CAPTURE is not None:
        return
    print("  %s%-7s FINDING %s%s" % (CYAN, n, text, OFF))


def precondition(n, what, ok, detail):
    if ARGS.dry_run:
        print("  %s%-7s NEEDS   %s%s" % (CYAN, n, what, OFF))
        return
    if ok:
        print("  %s%-7s OK      precondition: %s%s" % (DIM, n, what, OFF))
        return
    print("  %s%-7s SKIP    precondition NOT MET: %s%s" % (YELLOW, n, what, OFF))
    print("          %s" % detail)
    print("          This is a FIXTURE gap, not a failure of the spec.")
    sys.exit(2)


def banner(t):
    if CAPTURE is not None:
        return
    print("")
    print("%s== %s %s%s" % (CYAN, t, "=" * max(0, 74 - len(t)), OFF))


# ------------------------------------------------------------------- fixture --

def thing_ids(def_name):
    e = send("things", {"def": def_name, "detail": True, "by_location": False,
                        "cap": 50, "detail_cap": 50})
    return [t.get("id") for t in as_list(dig(e, "data.things"))
            if isinstance(t, dict) and t.get("id") is not None]


def count_of(def_name):
    e = send("things", {"def": def_name, "detail": False, "by_location": False})
    return dig(e, "data.totals.count", 0) or 0


def free_spot(near=None):
    a = {"w": 3, "h": 3, "max": 3}
    if near:
        a["near"] = near
    e = send("find-rect", a)
    for c in as_list(dig(e, "data.candidates")):
        if isinstance(c, dict) and isinstance(c.get("center"), list):
            return c["center"]
    return None


def roster():
    e = send("pawns", {"filter": "colonists", "cap": 30})
    return [p.get("id") for p in as_list(dig(e, "data.pawns"))
            if isinstance(p, dict) and p.get("id") is not None]


def pick_cook(ids):
    """A colonist who can COOK, BY PREDICATE and never by roster index
    (git-bug 1eb2262). A work type the pawn cannot do is in `work.disabled`."""
    for pid in ids:
        e = send("pawn", {"id": pid, "sections": ["work"]})
        if not dig(e, "data.work.initialized"):
            continue
        if COOKING_WORK not in [str(d) for d in as_list(dig(e, "data.work.disabled"))]:
            return pid
    return None


def bill_row(bench, uid=None):
    """The bill row through 2.4's OBSERVER, never the mutation verb's own echo —
    a claim about state is never read out of the envelope that claims to have
    changed it. That rule is the whole of eef837a item 2."""
    e = send("bills", {"bench": bench})
    for b in as_list(dig(e, "data.benches")):
        if not isinstance(b, dict) or b.get("id") != bench:
            continue
        for r in as_list(b.get("bills")):
            if not isinstance(r, dict):
                continue
            if uid is None or r.get("uid") == uid:
                return r
    return {}


def spawn_corpse(near):
    """A REAL corpse, made the way the game makes one. `dev:spawn-thing` refuses
    a corpse by name — DebugThingPlaceHelper.IsDebugSpawnable rejects them and
    forcing it produces a Corpse with no InnerPawn — so the honest route is to
    spawn the animal and kill it. Returns the corpse's thing id."""
    before = set(thing_ids(CORPSE_DEF))
    e = send("dev:spawn-pawn", {"kind": ANIMAL_KIND, "pos": near, "count": 1})
    pid = dig(e, "data.pawns.0.id")
    if pid is None:
        return None
    send("dev:damage", {"pawn": pid, "mode": "until-dead"})
    after = [i for i in thing_ids(CORPSE_DEF) if i not in before]
    return after[0] if after else None


def ensure_bill(bench):
    row = bill_row(bench)
    if row.get("uid"):
        return row["uid"], None
    e = send("bill-add", {"bench": bench, "recipe": RECIPE, "repeat": "forever"})
    if ARGS.dry_run:
        return "<uid>", e
    return dig(e, "data.uid"), e


def red_errors(since=0):
    e = send("journal", {"types": ["error"], "since": since, "limit": 100})
    return as_list(dig(e, "data.entries"))


def watermark():
    return dig(send("journal", {"limit": 1}), "data.seq", 0) or 0


def food_pct(pid):
    """The Food need as a percentage. `pawn {sections:["needs"]}` publishes
    PawnSerializer.Needs -> PawnSafe.Capped, i.e. `{list:[…], total, more,
    order}` sorted LOWEST FIRST — not a `needs.food` scalar — so the row is
    found by `def`, never by index. A starved colonist's Food row is at [0]
    precisely because it is the lowest, which is exactly the coincidence that
    would make an index-based read pass here and fail everywhere else."""
    e = send("pawn", {"id": pid, "sections": ["needs"]})
    for row in as_list(dig(e, "data.needs.list")):
        if isinstance(row, dict) and row.get("def") == "Food":
            return row.get("pct")
    return None


def pause(n=None):
    e = send("pause", {})
    if n:
        one_of(n, "the game is paused for the fixture", e, "data.paused", [True])
    return e


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 - fixture: a butcher spot, a bill, a corpse")

    e = send("status", {})
    ops = [str(o) for o in as_list(dig(e, "data.verbs"))]
    missing = [o for o in OPS if o not in ops]
    check("0.1", "every verb this suite drives is registered",
          not missing, "no missing ops", {"missing": missing})
    precondition("0.2", "the bench answers `status`", dig(e, "ok") is True or ARGS.dry_run,
                 "status returned %s" % show(dig(e, "error")))

    # PAUSED FOR THE WHOLE SUITE except where a phase runs time deliberately.
    # Two reasons, both paid for: a corpse rots on a running clock and phase 4's
    # "Fresh" assertion is on a 2.5-day fuse, and every read here is a snapshot
    # that a running world invalidates between the read and the next send.
    pause("0.3")

    S["watermark"] = watermark()

    ids = roster()
    precondition("0.4", "at least one colonist", len(ids) >= 1 or ARGS.dry_run,
                 "`pawns {filter:'colonists'}` returned %d" % len(ids))
    S["cook"] = pick_cook(ids) or (ids[0] if ids else None)
    precondition("0.5", "a colonist who can Cook", S["cook"] is not None or ARGS.dry_run,
                 "no colonist has Cooking enabled; ButcherCorpseFlesh's workSkill is Cooking")

    # The bench. Reuse one if the colony has it — a second ButcherSpot on the
    # same square is a fixture that reads as working and behaves oddly.
    existing = thing_ids(BENCH_DEF)
    if existing:
        S["bench"] = existing[0]
        S["bench_spawned"] = False
    else:
        anchor = None
        pe = send("pawn", {"id": S["cook"], "sections": ["basics"]}) if S["cook"] else {}
        at = dig(pe, "data.at")
        if isinstance(at, list):
            anchor = at
        spot = free_spot(anchor)
        se = send("dev:spawn-thing", {"def": BENCH_DEF, "pos": spot, "mode": "direct"}) \
            if spot else {}
        S["bench"] = dig(se, "data.spawned.0.id")
        S["bench_spawned"] = True
    precondition("0.6", "a %s to hang bills on" % BENCH_DEF,
                 S.get("bench") is not None or ARGS.dry_run,
                 "no %s on the map and dev:spawn-thing could not place one - is dev mode on?"
                 % BENCH_DEF)

    # Where the corpse goes: the bench's own cell region. `bills` reports the
    # bench row with `at`, which is the anchor every later spawn uses so the
    # search radius clause is never what fails.
    row = send("bills", {"bench": S["bench"]})
    S["bench_at"] = None
    for b in as_list(dig(row, "data.benches")):
        if isinstance(b, dict) and b.get("id") == S["bench"]:
            S["bench_at"] = b.get("at")
    if S["bench_at"] is None:
        S["bench_at"] = free_spot()
    print("  %sfixture: bench=%s at %s, cook=%s%s"
          % (DIM, S.get("bench"), show(S.get("bench_at")), S.get("cook"), OFF))


# ------------------------------------------------------- 1: the default filter --

def phase1():
    banner("PHASE 1 - eef837a item 1: bill-add gets the recipe's own default filter")

    # A clean slate: this phase is about what `bill-add` PRODUCES, so an
    # inherited bill would prove nothing.
    send("bill-remove", {"bench": S["bench"], "all": True})
    e = send("bill-add", {"bench": S["bench"], "recipe": RECIPE, "repeat": "forever"})
    S["uid"] = dig(e, "data.uid")
    eq("1.1", "bill-add succeeds", e, "data.ok", True)
    shape("1.2", "bill-add", e, "data.uid", str)

    # THE BLOCK. Asserted as a key SET first: a wrong dig path below would
    # otherwise go green on an absent key.
    if shape("1.3", "bill-add", e, "data.diagnosis", dict):
        keys_exactly("1.4", "diagnosis publishes exactly its documented fields",
                     e, "data.diagnosis", DIAG_KEYS)

    eq("1.5", "the new bill's filter is PUBLISHED, not empty and not absent",
       e, "data.diagnosis.filter_state", "published")
    shape("1.6", "bill-add", e, "data.diagnosis.filter_defs", int)
    shape("1.7", "bill-add", e, "data.diagnosis.recipe_default_defs", int)

    # THE ACCEPTANCE BULLET, in the form that survives a modded MakeNewBill:
    # not "the filter is non-empty" but "the filter IS the recipe's default".
    got = dig(e, "data.diagnosis.filter_defs")
    want = dig(e, "data.diagnosis.recipe_default_defs")
    check("1.8", "filter_defs == recipe_default_defs (the game's own Add Bill button)",
          num(got) and got == want, "equal to recipe_default_defs (%s)" % show(want), got)
    ge("1.9", "and it is not a trivially-empty default", e,
       "data.diagnosis.recipe_default_defs", 1)

    # The filter summary itself, including the two special-filter lists this
    # issue added. `AllowRotten` in the UNIVERSE's list is the fact that would
    # have told the run what was wrong.
    if shape("1.10", "bill-add", e, "data.diagnosis.ingredient_filter", dict):
        eq("1.11", "the summary names its denominator", e,
           "data.diagnosis.ingredient_filter.universe", "recipe-fixed")
        shape("1.12", "bill-add", e, "data.diagnosis.ingredient_filter.special_disallowed", list)
        shape("1.13", "bill-add", e,
              "data.diagnosis.ingredient_filter.universe_special_disallowed", list)
        uni = as_list(dig(e, "data.diagnosis.ingredient_filter.universe_special_disallowed"))
        check("1.14", "the RECIPE's fixed filter disallows AllowRotten, and says so",
              "AllowRotten" in uni,
              "AllowRotten in universe_special_disallowed", uni)

    # And the same three facts through the READ verb, so the claim does not
    # live only in the mutation's own echo.
    row = bill_row(S["bench"], S["uid"])
    eq_val("1.15", "`bills` reports the same filter_state", row.get("filter_state"), "published")
    eq_val("1.16", "`bills` reports the same filter size",
           row.get("filter_defs"), dig(e, "data.diagnosis.filter_defs"))


# ------------------------------------------ 2: published vs empty vs absent --

def phase2():
    banner("PHASE 2 - eef837a item 3: `filter: null` was two different facts")

    uid = S.get("uid") or ensure_bill(S["bench"])[0]

    row = bill_row(S["bench"], uid)
    # SHAPE BEFORE VALUE. The old build could leave `ingredient_filter` ABSENT
    # (FilterSummary threw inside a bare try/catch) and that read as `null` to
    # every consumer — including the agent who filed this issue.
    check("2.1", "`bills` publishes ingredient_filter as a KEY",
          "ingredient_filter" in row, "the key present", sorted(row.keys())[:20])
    check("2.2", "`bills` publishes filter_state as a KEY",
          "filter_state" in row, "the key present", sorted(row.keys())[:20])
    check("2.3", "a healthy bill's summary is a DICT, not null",
          isinstance(row.get("ingredient_filter"), dict),
          "a dict", row.get("ingredient_filter"))
    eq_val("2.4", "…and filter_state says published", row.get("filter_state"), "published")

    # NOW STAGE THE OTHER CASE. `filter:"none"` is ThingFilterUI's Clear All.
    send("bill-set", {"bench": S["bench"], "uid": uid, "filter": "none"})
    row = bill_row(S["bench"], uid)
    eq_val("2.5", "an emptied filter reads `empty`, NOT null and NOT absent",
           row.get("filter_state"), "empty")
    check("2.6", "…and the summary is STILL a dict (0 allowed defs is data)",
          isinstance(row.get("ingredient_filter"), dict),
          "a dict", row.get("ingredient_filter"))
    eq_val("2.7", "…with allowed_defs 0",
           dig(row, "ingredient_filter.allowed_defs"), 0)
    eq_val("2.8", "…and the verdict names it", row.get("health"), "filter-empty")
    ok = isinstance(row.get("remedy"), str) and "bill-set" in row.get("remedy", "")
    check("2.9", "…and the remedy names the verb that fixes it", ok,
          "a remedy mentioning bill-set", row.get("remedy"))

    # Restore, so the later phases start from the game's own default.
    send("bill-remove", {"bench": S["bench"], "uid": uid})
    S["uid"], _ = ensure_bill(S["bench"])
    row = bill_row(S["bench"], S["uid"])
    eq_val("2.10", "a freshly re-added bill is back to `published`",
           row.get("filter_state"), "published")


# --------------------------------------------- 3: the write that persists --

def phase3():
    banner("PHASE 3 - eef837a item 2: a filter edit that survives a read")

    uid = S.get("uid") or ensure_bill(S["bench"])[0]
    before = bill_row(S["bench"], uid).get("filter_defs")
    ge_val("3.1", "the bill has a filter to widen", before, 1)

    # The exact call from the issue.
    e = send("bill-set", {"bench": S["bench"], "uid": uid, "allow": ["Corpses"]})
    eq("3.2", "bill-set succeeds", e, "data.ok", True)
    if not shape("3.3", "bill-set", e, "data.targets.0.changed", list):
        return

    op = None
    for c in as_list(dig(e, "data.targets.0.changed")):
        if isinstance(c, dict) and c.get("field") == "filter":
            for o in as_list(dig(c, "value.ops")):
                if isinstance(o, dict) and o.get("def") == "Corpses":
                    op = o
    check("3.4", "the category op is reported", isinstance(op, dict),
          "an op row for the Corpses category", op)
    if not isinstance(op, dict):
        return

    shape("3.5", "bill-set", {"op": op}, "op.defs_delta", int)
    # THE NEW FIELD. `defs_withheld` is the count Bill.ExposeData would have
    # deleted at the next save and which this verb no longer writes.
    shape("3.6", "bill-set", {"op": op}, "op.defs_withheld", int)
    ge_val("3.7", "the fixed filter DOES exclude some of the Corpses category "
                  "(mechanoid/drone corpses) and the verb withheld them",
           op.get("defs_withheld"), 1)

    refused = [str(r.get("reason")) for r in as_list(dig(e, "data.targets.0.refused"))
               if isinstance(r, dict)]
    ok = any("fixedIngredientFilter" in r and "ExposeData" in r for r in refused)
    check("3.8", "…and the refusal names the mechanism (ExposeData + the fixed filter)",
          ok, "a refusal citing Bill.ExposeData and fixedIngredientFilter", refused)

    # `will_not_persist` runs ExposeData's own predicate as a QUESTION over the
    # filter as it now stands. Empty is the assertion.
    wnp = None
    for c in as_list(dig(e, "data.targets.0.changed")):
        if isinstance(c, dict) and c.get("field") == "filter":
            wnp = dig(c, "value.will_not_persist")
    check("3.9", "nothing was written that the next save would delete",
          isinstance(wnp, list) and len(wnp) == 0, "an empty list", wnp)

    # AND THE ACCEPTANCE BULLET ITSELF: read it back through `bills`.
    after_echo = dig(e, "data.targets.0.diagnosis.filter_defs")
    after_read = bill_row(S["bench"], uid).get("filter_defs")
    shape("3.10", "bill-set", e, "data.targets.0.diagnosis.filter_defs", int)
    eq_val("3.11", "the filter READS BACK at the size the write reported",
           after_read, after_echo)
    ge_val("3.12", "…and it actually grew", (after_read or 0) - (before or 0), 1)
    check("3.13", "the delta the verb reported IS the delta that landed",
          num(after_read) and num(before) and num(op.get("defs_delta"))
          and after_read - before == op["defs_delta"],
          "after - before == defs_delta (%s)" % show(op.get("defs_delta")),
          {"before": before, "after": after_read})


# ------------------------------------- 4: the thing-level answer, both ways --

def phase4():
    banner("PHASE 4 - the answer the run never got: usable now, and why not")

    uid = S.get("uid") or ensure_bill(S["bench"])[0]

    # A fresh corpse in reach. Made by killing a spawned animal, because
    # dev:spawn-thing refuses corpses by name.
    S["corpse"] = spawn_corpse(S["bench_at"])
    precondition("4.0", "a fresh %s near the bench" % CORPSE_DEF,
                 S.get("corpse") is not None or ARGS.dry_run,
                 "dev:spawn-pawn + dev:damage did not leave a %s on the map" % CORPSE_DEF)

    row = bill_row(S["bench"], uid)
    if shape("4.1", "`bills`", {"r": row}, "r.ingredient_match", dict):
        m = row["ingredient_match"]
        ge_val("4.2", "the scan saw the corpse", m.get("scanned"), 1)
        ge_val("4.3", "…and it is USABLE by this bill", m.get("usable"), 1)
        sample = as_list(m.get("usable_sample"))
        check("4.4", "the usable sample names a corpse",
              any(isinstance(s, dict) and str(s.get("def", "")).startswith("Corpse_")
                  for s in sample),
              "a Corpse_* row in usable_sample", sample[:2])
        check("4.5", "…and reports its ROT STAGE, which is the field the run needed",
              any(isinstance(s, dict) and s.get("rot_stage") == "Fresh" for s in sample),
              "rot_stage 'Fresh' on a usable row", sample[:2])
        check("4.6", "the block states the clauses it did NOT check",
              sorted([str(x) for x in as_list(m.get("clauses_not_checked"))])
              == ["reachable", "reservable"],
              "['reachable','reservable']", m.get("clauses_not_checked"))
    one_of("4.7", "the verdict is a known value", {"r": row}, "r.health", HEALTH_VALUES)
    one_of("4.8", "…and with a usable ingredient it is workable (or asleep-and-will-retry)",
           {"r": row}, "r.health", ["workable", "asleep-will-retry"])

    # NOW BREAK IT THE WAY THE RUN WAS BROKEN — a candidate that exists and is
    # rejected. `forbid` stages in one call what rot takes 2.5 days to do, and
    # it exercises the identical predicate path.
    send("forbid", {"things": [S["corpse"]]})
    row = bill_row(S["bench"], uid)
    eq_val("4.9", "a forbidden corpse leaves the bill with NOTHING usable",
           dig(row, "ingredient_match.usable"), 0)
    ge_val("4.10", "…counted under the reason that caused it",
           dig(row, "ingredient_match.rejected.forbidden"), 1)
    one_of("4.11", "…and the verdict says so rather than `workable`",
           {"r": row}, "r.health",
           ["no-matching-ingredient", "asleep-no-matching-ingredient"])
    ok = isinstance(row.get("remedy"), str) and "unforbid" in row["remedy"]
    check("4.12", "…and the remedy names `unforbid`, with the thing id in the sample",
          ok, "a remedy naming unforbid", row.get("remedy"))
    ids = [s.get("id") for s in as_list(dig(row, "ingredient_match.rejected_sample"))
           if isinstance(s, dict)]
    check("4.13", "the rejected sample names the actual corpse", S["corpse"] in ids,
          "corpse id %s in rejected_sample" % S.get("corpse"), ids)

    send("unforbid", {"things": [S["corpse"]]})
    row = bill_row(S["bench"], uid)
    ge_val("4.14", "unforbidding it restores the match", dig(row, "ingredient_match.usable"), 1)

    # The adjacent finding the issue asks to PRESERVE: `prioritize` takes a
    # WorkGiverDef, requires thing-or-cell, and its `blocked:` reason is how
    # this defect was cornered.
    e = send("prioritize", {"pawn": S["cook"], "work": COOKING_WORK, "thing": S["bench"]})
    check("4.15", "`prioritize` still refuses a WORK TYPE where a WorkGiverDef is wanted",
          dig(e, "ok") is False, "ok:false for work:'Cooking'", dig(e, "error"))
    e = send("prioritize", {"pawn": S["cook"], "work": WORKGIVER})
    check("4.16", "…and still requires a thing or a cell",
          dig(e, "ok") is False, "ok:false with no thing/cell", dig(e, "error"))
    e = send("prioritize", {"pawn": S["cook"], "work": WORKGIVER, "thing": S["bench"]})
    check("4.17", "…and with both it answers, carrying its `rejected` diagnostic",
          dig(e, "ok") is True and has_key(e, "data.rejected"),
          "ok:true with a `rejected` list", {"ok": dig(e, "ok"),
                                             "rejected": dig(e, "data.rejected")})


# ------------------------------------------------ 5: d9d6c12, the sleep state --

def phase5():
    banner("PHASE 5 - d9d6c12: the sleep state, named, counted, and NOT conflated")

    uid = S.get("uid") or ensure_bill(S["bench"])[0]
    row = bill_row(S["bench"], uid)
    if shape("5.1", "`bills`", {"r": row}, "r.ingredient_search", dict):
        got = set(row["ingredient_search"])
        missing = sorted(set(SEARCH_KEYS) - got)
        check("5.2", "the sleep block publishes its whole documented field set",
              not missing, "all of %s" % sorted(SEARCH_KEYS), {"missing": missing})
    one_of("5.3", "the state is a WORD, not a tick to compare against `now`",
           {"r": row}, "r.ingredient_search.state", ["asleep", "ready", "unknown"])
    check("5.4", "the raw field is still published beside it (32b9e01 keeps its proof)",
          "next_ingredient_search_tick" in row, "the key present", sorted(row.keys())[:20])

    # STARVE IT, then RUN TIME so a real failed search fires. WorkGiver_DoBill
    # only writes nextTickToSearchForIngredients from a pawn's own think tick —
    # `prioritize` deliberately does not (FloatMenuMakerMap.makingFor != pawn),
    # which is why this needs the clock and not another verb.
    if S.get("corpse"):
        send("forbid", {"things": [S["corpse"]]})
    send("unpause", {})
    advance({"ticks": max(2000, RECHECK_MAX * 3)})
    pause()

    row = bill_row(S["bench"], uid)
    S["asleep_health"] = row.get("health")
    eq_val("5.5", "a starved bill that a colonist tried is ASLEEP",
           dig(row, "ingredient_search.state"), "asleep")
    ge_val("5.6", "…with a wake tick in the future", dig(row, "ingredient_search.wakes_in_ticks"), 1)
    ge_val("5.7", "…and a COUNT of consecutive failed searches, not a bare tick",
           dig(row, "ingredient_search.consecutive_failed_searches"), 1)
    eq_val("5.8", "…observed by the mod's own watch, and it says so",
           dig(row, "ingredient_search.observed"), True)
    shape("5.9", "`bills`", {"r": row}, "r.ingredient_search.asleep_since_tick", int)
    shape("5.10", "`bills`", {"r": row}, "r.ingredient_search.asleep_for_ticks", int)
    eq_val("5.11", "d9d6c12 item 3, half one: asleep AND unable to match is its own state",
           row.get("health"), "asleep-no-matching-ingredient")

    # NOW THE OTHER HALF. Restore the ingredient and let it try again: the same
    # bill, still backing off, must NOT read the same.
    if S.get("corpse"):
        send("unforbid", {"things": [S["corpse"]]})
    row = bill_row(S["bench"], uid)
    one_of("5.12", "d9d6c12 item 3, half two: asleep-and-will-retry is a DIFFERENT word",
           {"r": row}, "r.health", ["asleep-will-retry", "workable"])
    ne_val("5.13", "…and the two states do not present identically",
           row.get("health"), S.get("asleep_health"))
    note("5.14", "the back-off is ReCheckFailedBillTicksRange = 500..600 TICKS and rearms on "
                 "each failure — it cannot 'sit in the future for days' as the issue and the "
                 "post-mortem both say; phase 9 re-derives the range from the source")


# ---------------------------------------------- 6: the colony actually eats --

def phase6():
    banner("PHASE 6 - the claim that matters: a starving colony with corpses EATS")

    uid = S.get("uid") or ensure_bill(S["bench"])[0]
    if not S.get("corpse"):
        S["corpse"] = spawn_corpse(S["bench_at"])
    precondition("6.0", "a corpse and a cook",
                 (S.get("corpse") is not None and S.get("cook") is not None) or ARGS.dry_run,
                 "phase 4's fixture is required")
    send("unforbid", {"things": [S["corpse"]]})

    # THE PREMISE, PROVED RATHER THAN ASSERTED: butchering is the ONLY route
    # from corpse to nutrition, because `consume` refuses a raw carcass.
    e = send("consume", {"pawn": S["cook"], "thing": S["corpse"]})
    ok = dig(e, "ok") is False or dig(e, "data.ok") is False
    detail = json.dumps({"error": dig(e, "error"), "data_ok": dig(e, "data.ok"),
                         "reason": dig(e, "data.reason")})
    check("6.1", "`consume` refuses a raw corpse — butchering is the only route",
          ok, "a refusal", detail[:400])
    check("6.2", "…and the refusal says the pawn can never eat it",
          "cannot-eat" in detail or "cannot ever eat" in detail,
          "a `cannot-eat` reason", detail[:400])

    # A hungry colonist. `dev:set-need` on Food is NOT a Need_Seeker, so unlike
    # mood it stays where it is put and drains from there.
    send("dev:set-need", {"pawn": S["cook"], "need": "Food", "val": 0.06})
    before_food = food_pct(S["cook"])
    ge_val("6.2b", "the fixture actually starved the colonist (Food is readable and low)",
           100 - (before_food or 100), 50)
    before_meat = count_of(MEAT_DEF)
    row = bill_row(S["bench"], uid)
    one_of("6.3", "the bill is workable before the clock runs", {"r": row}, "r.health",
           ["workable", "asleep-will-retry"])

    send("unpause", {})
    advance({"ticks": 20000})
    pause()

    after_meat = count_of(MEAT_DEF)
    after_food = food_pct(S["cook"])
    ge_val("6.4", "the corpse was butchered — meat exists that did not before",
           (after_meat or 0) - (before_meat or 0), 1)
    check("6.5", "…and the starving colonist's Food need ROSE",
          num(after_food) and num(before_food) and after_food > before_food,
          "food need above %s" % show(before_food), after_food)
    if not (num(after_food) and num(before_food) and after_food > before_food):
        note("6.5b", "if meat exists but the need did not rise, the gap is COOKING or "
                     "hauling, not this issue's surface — raw meat is DesperateOnly and a "
                     "colonist above the desperation threshold will wait for a meal")


# ------------------------------------------------- 7: the rotten case, live --

def phase7():
    banner("PHASE 7 - --rot: the clause that actually killed them, on a real clock")

    uid = S.get("uid") or ensure_bill(S["bench"])[0]
    if not S.get("corpse"):
        S["corpse"] = spawn_corpse(S["bench_at"])
    send("unforbid", {"things": [S["corpse"]]})
    # Suspend the bill first, or a colonist butchers the fixture before it rots.
    send("bill-set", {"bench": S["bench"], "uid": uid, "suspended": True})
    send("unpause", {})
    advance({"ticks": TICKS_TO_ROT_START + 5000}, timeout=1800)
    pause()
    send("bill-set", {"bench": S["bench"], "uid": uid, "suspended": False})

    row = bill_row(S["bench"], uid)
    eq_val("7.1", "a rotting corpse leaves the bill with nothing usable",
           dig(row, "ingredient_match.usable"), 0)
    reasons = dig(row, "ingredient_match.rejected") or {}
    check("7.2", "…rejected by the RECIPE's fixed filter, on the AllowRotten special filter",
          "recipe-fixed:special:AllowRotten" in reasons,
          "a `recipe-fixed:special:AllowRotten` bucket", reasons)
    sample = as_list(dig(row, "ingredient_match.rejected_sample"))
    check("7.3", "…and the sample says the corpse is Rotting or Dessicated",
          any(isinstance(s, dict) and s.get("rot_stage") in ("Rotting", "Dessicated")
              for s in sample),
          "a rot_stage past Fresh", sample[:2])
    ok = isinstance(row.get("remedy"), str) and "NO BILL LEVER" in row["remedy"]
    check("7.4", "…and the remedy says NO BILL LEVER FIXES THIS, which is the truth",
          ok, "a remedy that refuses to send the agent back to bill-set",
          row.get("remedy"))


# --------------------------------------------------------- 8: no red errors --

def phase8():
    banner("PHASE 8 - the standing invariant: no red errors")
    rows = red_errors(S.get("watermark", 0))
    check("8.1", "no red error was authored during this suite",
          len(rows) == 0, "an empty error journal since the watermark",
          [dig(r, "payload.message") for r in rows][:5])


# ------------------------------------------------------------ 9: offline --

def probe(fn):
    """Run one assertion body with checks captured instead of printed, and
    return whether every check inside it passed. Phase 9 uses it to assert that
    a BROKEN fixture FAILS — a helper that cannot fail proves nothing."""
    global CAPTURE, FAILS
    saved_fails = list(FAILS)
    CAPTURE = []
    try:
        fn()
        got = all(CAPTURE)
    finally:
        CAPTURE = None
        FAILS = saved_fails
    return got


def _first(paths):
    for p in paths:
        if p and os.path.isdir(p):
            return p
    return None


def _read(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as fh:
            return fh.read()
    except OSError:
        return None


def _src(name):
    return _read(os.path.join(REPO, "Source", "AutoRimmer", name)) or ""


def _bill_defs(save_text):
    """The `<allowedDefs>` of `ButcherSpot103250`'s bill in one m1-20260901 save.
    Deliberately NOT `<li>` over the whole `<ingredientFilter>`: that block also
    holds `<disallowedSpecialFilters>`, and counting both together is how this
    suite's first draft got 121 for a filter that holds 115."""
    seg = (save_text or "").split("ButcherSpot103250")[-1].split("</thing>")[0]
    if "<allowedDefs>" not in seg:
        return []
    body = seg.split("<allowedDefs>")[-1].split("</allowedDefs>")[0]
    return [x.split("</li>")[0] for x in body.split("<li>")[1:]]


def phase9():
    banner("PHASE 9 - offline: the evidence behind every claim this suite makes")

    # -- 9.1 the shipped source's own constants ---------------------------
    watch = _src("BillWatch.cs")
    m = re.search(r"public const int Cadence = (\d+);", watch)
    cadence = int(m.group(1)) if m else None
    check("9.1", "BillWatch.Cadence is readable from the shipped source",
          cadence is not None, "a Cadence constant", cadence)

    diag = _src("BillIngredients.cs")
    keys = re.findall(r'd\["([a-z_]+)"\]\s*=', diag)
    missing = [k for k in DIAG_KEYS if k not in keys]
    check("9.2", "every diagnosis key this suite asserts is written by Diagnose",
          not missing, "all of %s written in BillIngredients.cs" % sorted(DIAG_KEYS),
          {"missing": missing})

    # -- 9.3 the decompiled game source ------------------------------------
    decomp = _first(DECOMP_CANDIDATES)
    if decomp is None:
        note("9.3", "no decompiled RimWorldBase found (set RIMWORLD_DECOMP) — the four "
                    "source-derived checks below were NOT run, which is not the same as "
                    "passing")
    else:
        bill = _read(os.path.join(decomp, "RimWorld", "Bill.cs")) or ""
        check("9.3", "Bill(RecipeDef, …) still copies the recipe's DEFAULT filter — "
                     "this is why bill-add was never the defect",
              "CopyAllowancesFrom(recipe.defaultIngredientFilter)" in bill,
              "the ctor line", bill.count("CopyAllowancesFrom"))
        narrowing = re.search(
            r"Scribe\.mode == LoadSaveMode\.Saving && recipe\.fixedIngredientFilter != null",
            bill)
        check("9.4", "Bill.ExposeData still NARROWS the filter during the saving pass",
              narrowing is not None, "the ExposeData guard", bool(narrowing))
        check("9.5", "IsFixedOrAllowedIngredient still consults the RECIPE's fixed filter "
                     "before the bill's own",
              "if (recipe.fixedIngredientFilter.Allows(thing))" in bill.replace("\t", ""),
              "the fixed-filter branch", None)

        wg = _read(os.path.join(decomp, "RimWorld", "WorkGiver_DoBill.cs")) or ""
        m = re.search(r"ReCheckFailedBillTicksRange = new IntRange\((\d+), (\d+)\)", wg)
        lo = int(m.group(1)) if m else None
        hi = int(m.group(2)) if m else None
        check("9.6", "the ingredient-search back-off is still 500..600 ticks",
              (lo, hi) == (500, RECHECK_MAX), "(500, %d)" % RECHECK_MAX, (lo, hi))
        check("9.7", "…and BillWatch samples FASTER than the minimum back-off, which is "
                     "the whole correctness argument for the failure count",
              num(cadence) and num(lo) and cadence < lo,
              "Cadence < %s" % lo, cadence)

        gen = _read(os.path.join(decomp, "RimWorld", "ThingDefGenerator_Corpses.cs")) or ""
        m = re.search(r"daysToRotStart = ([0-9.]+)f", gen)
        days = float(m.group(1)) if m else None
        check("9.8", "a flesh corpse still rots at 2.5 days, so phase 7's tick budget is right",
              days is not None and abs(days * 60000 - TICKS_TO_ROT_START) < 1,
              "%d ticks" % TICKS_TO_ROT_START,
              None if days is None else days * 60000)

    # -- 9.9 the game's own def --------------------------------------------
    data = _first(DATA_CANDIDATES)
    if data is None:
        note("9.9", "no RimWorld Data directory found (set RIMWORLD_DATA) — the recipe "
                    "checks below were NOT run")
    else:
        xml = _read(os.path.join(data, "Core", "Defs", "RecipeDefs",
                                 "Recipes_Butchery.xml")) or ""
        block = xml.split("<defName>ButcherCorpseFlesh</defName>")[-1].split("</RecipeDef>")[0]
        fixed = block.split("<fixedIngredientFilter>")[-1].split("</fixedIngredientFilter>")[0]
        default = block.split("<defaultIngredientFilter>")[-1] \
                       .split("</defaultIngredientFilter>")[0]
        check("9.9", "ButcherCorpseFlesh's FIXED filter still disallows AllowRotten — "
                     "the clause that made 600 meat inedible",
              "AllowRotten" in fixed and "specialFiltersToDisallow" in fixed,
              "specialFiltersToDisallow: AllowRotten", fixed.strip()[:300])
        check("9.10", "…and its DEFAULT filter is CorpsesAnimal, which is what bill-add "
                      "reproduces",
              "CorpsesAnimal" in default, "CorpsesAnimal", default.strip()[:200])
        check("9.11", "…and the fixed filter excludes mechanoid corpses, which is what "
                      "phase 3's `defs_withheld` counts",
              "CorpsesMechanoid" in fixed and "disallowedCategories" in fixed,
              "disallowedCategories: CorpsesMechanoid", fixed.strip()[:300])

    # -- 9.12 the run's own saves ------------------------------------------
    d46 = os.path.join(RUN_SAVES, "day-46.rws")
    d62 = os.path.join(RUN_SAVES, "day-62.rws")
    d66 = os.path.join(RUN_SAVES, "day-66.rws")
    if not all(os.path.exists(p) for p in (d46, d62, d66)):
        note("9.12", "RUNS/m1-20260901/saves not present — the four artifact checks were "
                     "NOT run")
    else:
        d46_defs = _bill_defs(_read(d46))
        check("9.12", "the run's OWN day-46 save shows bill-add produced %d allowed defs, "
                      "not an empty filter — the issue's headline is falsified by the "
                      "artifact" % DAY46_DEFS,
              len(d46_defs) == DAY46_DEFS, "%d allowedDefs" % DAY46_DEFS, len(d46_defs))
        check("9.13", "…and they are the ANIMAL corpses and only those, which is what "
                      "`defaultIngredientFilter: CorpsesAnimal` means",
              "Corpse_WildBoar" in d46_defs and "Corpse_Human" not in d46_defs
              and not any("Mech" in d for d in d46_defs),
              "WildBoar in, Human out, no mech corpse",
              {"boar": "Corpse_WildBoar" in d46_defs, "human": "Corpse_Human" in d46_defs})

        d66_defs = _bill_defs(_read(d66))
        check("9.14", "eef837a item 2 is REAL and its size is 27, not 39: the verb "
                      "reported %d added over a base of %d (=%d) and the save holds %d"
                      % (RUN_REPORTED_DELTA, DAY46_DEFS, RUN_REPORTED_TOTAL, DAY66_DEFS),
              len(d66_defs) == DAY66_DEFS
              and DAY46_DEFS + RUN_REPORTED_DELTA == RUN_REPORTED_TOTAL,
              "%d allowedDefs after the write and the saves" % DAY66_DEFS, len(d66_defs))
        check("9.15", "…and what evaporated is exactly what the recipe's fixed filter "
                      "excludes: human corpses SURVIVED, mechanoid corpses did not",
              "Corpse_Human" in d66_defs and not any("Mech" in d for d in d66_defs),
              "Human in, no mech corpse",
              {"human": "Corpse_Human" in d66_defs,
               "mech": [d for d in d66_defs if "Mech" in d][:3]})

        s = _read(d62) or ""
        found = None
        for m in re.finditer(r'<thing Class="Corpse">(.{0,1600}?)</thing>', s, re.S):
            seg = m.group(1)
            if BOAR_CELL in seg:
                r = re.search(r"<rotProg>([0-9.]+)</rotProg>", seg)
                found = float(r.group(1)) if r else None
        check("9.16", "the corpse on the butcher spot's own cell was already ROTTING when "
                      "the run called it fresh — the actual cause of death",
              found is not None and found > TICKS_TO_ROT_START,
              "rotProg > %d at %s" % (TICKS_TO_ROT_START, BOAR_CELL), found)

    # -- 9.14 the helpers themselves ---------------------------------------
    good = {"data": {"diagnosis": {"filter_state": "published", "ingredient_filter": {},
                                   "health": "workable"}}}
    bad_absent = {"data": {"diagnosis": {"health": "workable"}}}
    bad_null = {"data": {"diagnosis": {"filter_state": None, "ingredient_filter": None,
                                       "health": "workable"}}}

    check("9.17", "shape() passes on a present key",
          probe(lambda: shape("x", "v", good, "data.diagnosis.filter_state", str)),
          "pass", None)
    check("9.18", "shape() FAILS on an absent key — the whole point of the shape contract",
          not probe(lambda: shape("x", "v", bad_absent, "data.diagnosis.filter_state", str)),
          "fail", None)
    check("9.19", "shape() FAILS on a present-but-null key where a str is wanted",
          not probe(lambda: shape("x", "v", bad_null, "data.diagnosis.filter_state", str)),
          "fail", None)
    check("9.20", "eq(..., None) would have passed on the ABSENT key, which is why "
                  "every null assertion in this file is preceded by a shape()",
          probe(lambda: eq("x", "w", bad_absent, "data.diagnosis.filter_state", None)),
          "pass (and that is the hazard)", None)
    check("9.21", "keys_exactly() FAILS on a missing field",
          not probe(lambda: keys_exactly("x", "w", bad_absent, "data.diagnosis", DIAG_KEYS)),
          "fail", None)
    check("9.22", "ne_val() FAILS when the two states present identically — the form "
                  "d9d6c12 item 3 needs",
          not probe(lambda: ne_val("x", "w", "asleep-will-retry", "asleep-will-retry")),
          "fail", None)


# ---------------------------------------------------------------------- main --

PHASES = {1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5,
          6: phase6, 7: phase7, 8: phase8, 9: phase9}
DEFAULT_PHASES = [1, 2, 3, 4, 5, 6, 8]      # 7 needs --rot; 9 never needs a bench


def main():
    global ARGS
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--root", default=DEFAULT_ROOT, help="the protocol root")
    p.add_argument("--phase", type=int, action="append",
                   choices=[0, 1, 2, 3, 4, 5, 6, 7, 8, 9],
                   help="run only these phases (repeatable); phase 0 always runs")
    p.add_argument("--rot", action="store_true",
                   help="add phase 7: advance %d ticks so the fixture corpse really rots"
                        % TICKS_TO_ROT_START)
    p.add_argument("--dry-run", action="store_true",
                   help="print the plan and every expectation, send nothing")
    p.add_argument("--selftest", action="store_true",
                   help="phase 9 only: offline; no bench, no game, nothing sent")
    p.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = p.parse_args()

    print("AutoRimmer acceptance - the butcher bill that matched nothing (eef837a)")
    print("                        the bill-asleep flag (d9d6c12)")

    if ARGS.selftest:
        print("mode: --selftest (offline; no bench, no game, nothing sent)")
        phase9()
        return summarise(selftest=True)

    print("protocol root: %s" % ARGS.root)
    if not ARGS.dry_run and not os.path.exists(os.path.join(ARGS.root, "status.json")):
        print("%sno status.json under %s - the ORCHESTRATOR starts the bench with "
              "_RimWorld-Agent/run-agent.sh, or pass --root%s" % (RED, ARGS.root, OFF))
        sys.exit(2)
    if not ARGS.dry_run:
        os.makedirs(os.path.join(ARGS.root, "commands"), exist_ok=True)

    wanted = sorted(set(ARGS.phase or (DEFAULT_PHASES + ([7] if ARGS.rot else []))) - {0})
    print("phases: 0 + %s" % ", ".join(str(x) for x in wanted))
    print("%sTHIS SUITE WRECKS THE COLONY IT RUNS ON: it spawns and kills animals, "
          "starves a colonist and leaves bills behind. Reload before running it twice.%s"
          % (YELLOW, OFF))

    phase0()
    for n in wanted:
        PHASES[n]()
    return summarise()


def summarise(selftest=False):
    print("")
    print("=" * 78)
    if FINDINGS:
        print("%sFINDINGS (shipped-mod defects, reported not asserted):%s" % (CYAN, OFF))
        for n, t in FINDINGS:
            print("  %s%-7s %s%s" % (CYAN, n, t, OFF))
        print("")
    if ARGS.dry_run:
        print("%sRESULT: --dry-run printed %d expectations and asserted NONE of them. "
              "Nothing was sent; no dig path was proved. Run it live.%s"
              % (YELLOW, CHECKS, OFF))
        sys.exit(0)
    if FAILS:
        print("%sRESULT: %d FAILED of %d checks - %s%s"
              % (RED, len(FAILS), CHECKS, ", ".join(FAILS), OFF))
        sys.exit(1)
    if selftest:
        print("%sRESULT: all %d self-checks passed. This proves the ASSERTIONS and the "
              "EVIDENCE behind them, not the mod: no bench was touched.%s"
              % (GREEN, CHECKS, OFF))
    else:
        print("%sRESULT: all %d checks passed%s" % (GREEN, CHECKS, OFF))
    sys.exit(0)


if __name__ == "__main__":
    main()
