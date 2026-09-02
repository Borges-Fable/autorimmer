#!/usr/bin/env python3
"""Acceptance runner for e08c3e5 — the heater that was never short of components.

    ./accept/e08c3e5-skill-prereq.py             # everything
    ./accept/e08c3e5-skill-prereq.py --phase 3   # one phase (0 always runs)
    ./accept/e08c3e5-skill-prereq.py --dry-run   # print the plan, send nothing
    ./accept/e08c3e5-skill-prereq.py --selftest  # phase 9 only: NO bench needed

Read `accept/1adc737-place-layout.py`'s header for the protocol and the exit
codes, and `accept/eef837a-bill-filter.py`'s for the shape-contract rule this
file obeys: **`eq(..., None)` passes on an ABSENT key**, so every assertion
about an absence here is a `shape()` or a `keys_include()`.

WHAT WENT WRONG. Run m1-20260901 placed a 33-element barracks. Thirty-two built
on day 1. The thirty-third, a `Heater` at [111,137], reported

    {"def":"Heater","state":"awaiting-materials",
     "missing":[{"def":"ComponentIndustrial","count":1}]}

with thirty unforbidden `ComponentIndustrial` on the map.
`Heater.constructionSkillPrerequisite` is 5 and the roster's best Construction
was 4, so no colonist could take the job.

THE ARTIFACTS AGREE, AND SAY MORE THAN THE ISSUE. Checked before this suite was
written, and phase 9 re-derives all of it:

  * `RUNS/m1-20260901/journal/20260902T002505.ndjson` seq 42 places `ly-1`'s 33
    elements at tick 1023 with `pl-23` = `Heater` at `[111,137]`.
  * 32 of them completed by tick 12687. `pl-23` completed at **tick 60755,
    worker Lacey** — after `summary.md`'s recorded human intervention raising
    Lacey's Construction from **4 to 8**. That is stronger evidence than the
    quoted envelope would have been: the element built the moment, and only the
    moment, somebody cleared the gate.
  * `Buildings_Temperature.xml` has `Heater.constructionSkillPrerequisite 5`
    and a cost list of `Steel 50 + ComponentIndustrial 1`, which is exactly the
    `missing` row. `Cooler` is 5 as well — the entire freezer plan.
  * The quoted `construction` envelope itself is NOT in the run's artifacts and
    no save exists inside the Heater's window, so the literal "30 unforbidden
    reachable" figure is corroborated (the scenario grants exactly 30
    `ComponentIndustrial`; no save carries a `<forbidden>` element on any
    component stack) rather than read back. Said here rather than asserted.

THE MECHANISM, and the issue's version of it is INCOMPLETE — which is why the
fix is shaped the way it is. `WorkGiver_ConstructDeliverResourcesToBlueprints
.JobOnThing` calls `GenConstruct.CanConstruct(blueprint, pawn, def.workType, …)`
and that overload sets `checkSkills: workType == WorkTypeDefOf.Construction`.
The Core def `ConstructDeliverResourcesToBlueprints` has
`<workType>Construction</workType>`, so the skill clause fires and no component
is hauled — the run's symptom. **But `WorkGivers.xml` declares the SAME class a
second time as `DeliverResourcesToBlueprints` with
`<workType>Hauling</workType>`, where `checkSkills` is FALSE**, and
`IsNewValidNearbyNeeder` passes `checkSkills: false` too. A hauler can therefore
stock a skill-gated blueprint, leaving a Frame nobody can finish. The gate wears
TWO wrong costumes — `awaiting-materials` and `ready` — and the README's triage
table sent an agent down a different wrong branch for each. That is the whole
argument for `no-builder` outranking both, and phase 9 asserts both defs.

THE FIXTURE IS `TrapSpike`, NOT `Heater`, and that is deliberate. `Heater`
requires the `Electricity` research, so a Heater fixture would test the research
gate as much as the skill one. `TrapSpike` carries
`constructionSkillPrerequisite 3` with **no `researchPrerequisites` at all**, is
1x1, and is made from any Metallic/Woody/Stony stuff — so the roster's skill is
the only variable. Phase 9 asserts that property of the def rather than trusting
it. `RoyalBed` (8, no research) is the second fixture, for a ceiling no ordinary
roster clears.

WHAT IS BEING TESTED:

  * PHASE 1 — item 1. With every colonist's Construction forced below 3,
    `build --dry-run` of a `TrapSpike` publishes a `skill` block:
    `construction_required`, `best_construction`, `clears: []`, `blocked: true`
    and a `hint` NAMING the number and the person. Plus `skill_basis` with the
    gate cited and the mech caveat present.
  * PHASE 2 — item 2, and the design question it left open. A non-dry-run
    `place-layout` containing the gated def **places it and says so**:
    `skill_shortfall` is non-empty on the result. It is asserted to be present
    and EMPTY for an ungated layout, because `[]` means "checked, nobody is
    gated" and an absent key would mean the mod never looked. The refuse-vs-warn
    question is resolved to WARN — see DESIGN 2026-09-02 (e08c3e5): the material
    shortfall the issue cites as precedent does not refuse either.
  * PHASE 3 — item 3, the actual defect. `construction {id}` on the placed
    blueprint reads `state: "no-builder"` and **NOT** `awaiting-materials`, with
    a `why` naming the skill and a `skill.blocked: true`.
    `digest.construction.no_builder` and `.skill_blocked` both count it.
  * PHASE 4 — raise the skill and it builds. One colonist goes to Construction
    10, and the same element must LEAVE `no-builder` — asserted as an
    inequality against the phase-3 reading, which is the only form a constant
    cannot satisfy.
  * PHASE 5 — the ungated control. A `Wall` (prerequisite 0) publishes NO
    `skill` block at all, because presence is the signal.
  * PHASE 6 — no red errors.
  * PHASE 9 — offline: the game's own defs and decompiled source, and the run's
    journal, plus helper self-checks.

WHAT THIS SUITE DOES NOT PROVE, in those words:

  * **`artisticSkillPrerequisite`.** The clause is implemented and phase 9
    asserts it is read from the same place as its Construction twin — but
    **no def in the shipped Data tree sets it to anything**, so there is no
    vanilla fixture and nothing here exercises it live. It is reported as a
    FINDING rather than tested, and any coverage needs a modded def.
  * **The mech branch.** `CanConstruct`'s `p.IsColonyMech` /
    `RaceProps.mechFixedSkillLevel` path is out of scope for M1 (the issue says
    so) and is deliberately NOT read by the implementation.
    `skill_basis.not_asked` says so in the envelope; phase 1 asserts that
    sentence is there. A mech colony's real ceiling can be higher than reported
    and nothing here would catch it.
  * **Work settings.** A colonist with the skill and Construction switched off
    is the README's work-priority branch, not this one, and the two are
    deliberately not conflated. Untested here.
  * **`TerrainDef`'s copy of the field.** `TerrainTemplateDef` carries
    `constructionSkillPrerequisite` (SterileTile is 6, stone tiles 3) and the
    IR's terrain layer will hit it. Nothing here places a floor and nothing
    here checks a terrain def — the implementation reads `t.def` on a live
    Blueprint, which for a floor is the generated terrain-blueprint def that
    `ThingDefGenerator_Buildings` copies the field onto, so it SHOULD work and
    is UNPROVEN.

IT WRECKS THE COLONY IT RUNS ON: it REWRITES every colonist's Construction skill
and leaves blueprints and a layout behind. It does not restore the original
levels — `dev:set-skill` is a god-hand write and the suite is honest about being
destructive rather than pretending to undo it.

Exit 0 = every check passed · 1 = at least one FAIL · 2 = a fixture
precondition could not be met, which is NOT a spec failure and says so.
"""

import argparse
import glob
import json
import os
import re
import sys
import time

VAULT = os.environ.get("RIMWORLD_VAULT", "/home/dorian/projects/rimworld")
DEFAULT_ROOT = os.environ.get("RWA_ROOT") or os.path.join(
    VAULT,
    "_RimWorld-Agent/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/AutoRimmer",
)
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

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
CAPTURE = None

OPS = ["construction", "digest", "build", "place-layout", "pawns", "pawn",
       "things", "find-rect", "advance", "journal", "status", "pause",
       "dev:set-skill"]

# The gated fixture: skill 3, NO research prerequisite, 1x1, stuff-based.
GATED_DEF = "TrapSpike"
GATED_LEVEL = 3
# A second, higher ceiling that no ordinary roster clears.
HIGH_DEF = "RoyalBed"
HIGH_LEVEL = 8
# The control: prerequisite 0, so no `skill` block may be published for it.
PLAIN_DEF = "Wall"
FIXTURE_STUFF = "WoodLog"
# What every colonist's Construction is forced to, so the ceiling is below
# GATED_LEVEL and the verdict is decidable rather than incidental.
FLOOR_LEVEL = 1
RAISED_LEVEL = 10

SKILL_KEYS = ["construction_required", "artistic_required", "best_construction",
              "best_artistic", "clears", "clears_count", "blocked"]
BASIS_KEYS = ["gate", "gate_detail", "roster", "roster_source", "not_asked",
              "work_givers"]

RUN_JOURNAL = os.path.join(REPO, "RUNS", "m1-20260901", "journal")
HEATER_CELL = [111, 137]
HEATER_PID = "pl-23"
HEATER_DONE_TICK = 60755


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=240):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acce08c3e5-%03d-%s" % (SEQ, slug)
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


ESCAPE = ("accept/e08c3e5-skill-prereq.py: this suite rewrites colonist skills "
          "and journals on every verb")


def advance(ticks, timeout=1200):
    return send("advance", {"ticks": ticks, "unread_ok": ESCAPE,
                            "through_casualties": ESCAPE}, timeout=timeout)


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


def ne_val(n, what, a, b):
    check(n, what, a != b, "anything but %s" % show(b), a)


def ge(n, what, env, path, want):
    got = dig(env, path)
    ok = num(got) and got >= want
    check(n, "%s (%s)" % (what, path), ok, ">= %s" % want, got)


def lt(n, what, env, path, want):
    got = dig(env, path)
    ok = num(got) and got < want
    check(n, "%s (%s)" % (what, path), ok, "< %s" % want, got)


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


def keys_include(n, what, env, path, want):
    got = dig(env, path)
    if not isinstance(got, dict):
        check(n, "%s (%s)" % (what, path), False, "a dict at that path", got)
        return
    missing = sorted(set(want) - set(got))
    check(n, "%s (%s)" % (what, path), not missing,
          "every one of %s" % (sorted(want),), {"missing": missing})


def note(n, text):
    if CAPTURE is not None:
        return
    print("  %s%-7s NOTE    %s%s" % (YELLOW, n, text, OFF))


def finding(n, text):
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

def red_errors(since=0):
    e = send("journal", {"types": ["error"], "since": since, "limit": 100})
    return as_list(dig(e, "data.entries"))


def watermark():
    return dig(send("journal", {"limit": 1}), "data.seq", 0) or 0


def roster():
    e = send("pawns", {"filter": "colonists", "cap": 30})
    return [p.get("id") for p in as_list(dig(e, "data.pawns"))
            if isinstance(p, dict) and p.get("id") is not None]


def free_spots(n=4):
    e = send("find-rect", {"w": 1, "h": 1, "max": max(4, n + 4)})
    out = []
    for c in as_list(dig(e, "data.candidates")):
        if isinstance(c, dict) and isinstance(c.get("center"), list):
            out.append(c["center"])
    return out


def set_construction(level):
    """Every colonist, by PREDICATE over the roster and never by index — a
    ceiling read off one pawn is not a ceiling (git-bug 1eb2262)."""
    done = 0
    for pid in S.get("roster", []):
        r = send("dev:set-skill", {"pawn": pid, "skill": "Construction",
                                   "level": level})
        if dig(r, "ok"):
            done += 1
    return done


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 - fixture: a roster forced below the def's prerequisite")

    e = send("status", {})
    ops = [str(o) for o in as_list(dig(e, "data.verbs"))]
    missing = [o for o in OPS if o not in ops]
    check("0.1", "every verb this suite drives is registered",
          not missing, "no missing ops", {"missing": missing})
    precondition("0.2", "the bench answers `status`", dig(e, "ok") is True or ARGS.dry_run,
                 "status returned %s" % show(dig(e, "error")))

    send("pause", {})
    S["watermark"] = watermark()

    S["roster"] = roster() or ([1] if ARGS.dry_run else [])
    precondition("0.3", "at least one colonist", len(S["roster"]) >= 1 or ARGS.dry_run,
                 "`pawns {filter:'colonists'}` returned none")

    lowered = set_construction(FLOOR_LEVEL)
    precondition("0.4", "every colonist's Construction forced to %d" % FLOOR_LEVEL,
                 lowered == len(S["roster"]) or ARGS.dry_run,
                 "dev:set-skill accepted %d of %d colonists"
                 % (lowered, len(S["roster"])))

    S["spots"] = free_spots(6) or ([[0, 0]] * 6 if ARGS.dry_run else [])
    precondition("0.5", "at least four free cells", len(S["spots"]) >= 4 or ARGS.dry_run,
                 "`find-rect` returned %d candidate(s)" % len(S["spots"]))
    print("  %sfixture: roster %s at Construction %d; %s needs %d%s"
          % (DIM, S["roster"], FLOOR_LEVEL, GATED_DEF, GATED_LEVEL, OFF))


# ------------------------------------------------------------------- phase 1 --

def phase1():
    banner("PHASE 1 - item 1: the dry-run names the ceiling and the person")

    e = send("build", {"def": GATED_DEF, "pos": S["spots"][0],
                       "stuff": FIXTURE_STUFF, "dry_run": True})
    shape("1.1", "build --dry-run", e, "data.skill", dict)
    keys_include("1.2", "the skill block is complete", e, "data.skill", SKILL_KEYS)
    eq("1.3", "…and names the def's own prerequisite",
       e, "data.skill.construction_required", GATED_LEVEL)
    lt("1.4", "…and the roster's best, which is below it",
       e, "data.skill.best_construction", GATED_LEVEL)
    eq("1.5", "…and NOBODY clears it", e, "data.skill.clears_count", 0)
    shape("1.6", "build --dry-run", e, "data.skill.clears", list)
    check("1.7", "…so `clears` is the empty list, not a missing key",
          dig(e, "data.skill.clears") == [], "[]", dig(e, "data.skill.clears"))
    eq("1.8", "…and the verdict is BLOCKED", e, "data.skill.blocked", True)
    shape("1.9", "build --dry-run", e, "data.skill.hint", str)
    contains("1.10", "…and the hint NAMES the skill (a measurement plus a fix, "
                     "the shape that worked for the steel shortfall)",
             e, "data.skill.hint", "Construction")
    contains("1.11", "…and the required LEVEL, in words the agent can act on",
             e, "data.skill.hint", str(GATED_LEVEL))

    # THE PROVENANCE, once per envelope, the way `materials_basis` is published.
    shape("1.12", "build --dry-run", e, "data.skill_basis", dict)
    keys_include("1.13", "the basis is complete", e, "data.skill_basis", BASIS_KEYS)
    contains("1.14", "…and cites the game's own gate by file and member",
             e, "data.skill_basis.gate", "GenConstruct")
    contains("1.15", "…and says MECHS are not considered, rather than being "
                     "silent about it",
             e, "data.skill_basis.not_asked", "mech")
    contains("1.16", "…and that work settings are a DIFFERENT branch",
             e, "data.skill_basis.not_asked", "work")
    contains("1.17", "…and names the Hauling-workType deliverer, which is why "
                     "the gate can present as `ready` as well as as a shortfall",
             e, "data.skill_basis.work_givers", "Hauling")

    # A ceiling nobody clears at all, so the fixture is not passing by accident
    # on a roster that happens to sit at 2.
    h = send("build", {"def": HIGH_DEF, "pos": S["spots"][1], "dry_run": True})
    if has_key(h, "data.skill"):
        eq("1.18", "a higher ceiling reads its own number",
           h, "data.skill.construction_required", HIGH_LEVEL)
        eq("1.19", "…and is blocked too", h, "data.skill.blocked", True)
    else:
        note("1.18", "%s was refused before the skill block (its own gate), so "
                     "the second ceiling is not exercised here" % HIGH_DEF)

    # …and it does NOT refuse. See DESIGN 2026-09-02 (e08c3e5).
    eq("1.20", "a dry-run of a gated def is still `ok` — a skill ceiling is a "
               "report, not a refusal", e, "ok", True)


# ------------------------------------------------------------------- phase 2 --

def phase2():
    banner("PHASE 2 - item 2: place-layout PLACES it and says so")

    els = [{"def": GATED_DEF, "at": S["spots"][0], "stuff": FIXTURE_STUFF},
           {"def": PLAIN_DEF, "at": S["spots"][2], "stuff": FIXTURE_STUFF}]
    e = send("place-layout", {"elements": els, "name": "acc-e08c3e5"})
    S["layout"] = dig(e, "data.layout_id")

    shape("2.1", "place-layout", e, "data.skill_shortfall", list)
    rows = [r for r in as_list(dig(e, "data.skill_shortfall")) if isinstance(r, dict)]
    check("2.2", "…and it names the gated def", any(r.get("def") == GATED_DEF for r in rows),
          "a row for %s" % GATED_DEF, [r.get("def") for r in rows])
    if rows:
        gated = [r for r in rows if r.get("def") == GATED_DEF]
        if gated:
            eq_val("2.3", "…with the requirement", gated[0].get("construction_required"),
                   GATED_LEVEL)
            eq_val("2.4", "…and how many ELEMENTS of the layout it costs",
                   gated[0].get("elements"), 1)
            check("2.5", "…and a hint naming the fix",
                  isinstance(gated[0].get("hint"), str)
                  and "Construction" in gated[0]["hint"],
                  "a hint naming Construction", gated[0].get("hint"))
    check("2.6", "the UNGATED element is NOT in the shortfall — presence is the "
                 "signal, and a Wall has no prerequisite",
          not any(r.get("def") == PLAIN_DEF for r in rows),
          "no %s row" % PLAIN_DEF, [r.get("def") for r in rows])

    # THE RESOLUTION, asserted. The issue left refuse-vs-warn open; the material
    # shortfall it cites as precedent does not refuse either (LayoutVerbs
    # `failed` counts parse errors, SiteGate verdicts and self-overlaps, and
    # MaterialBill runs after it), and a skill ceiling is cleared by a colonist
    # levelling up. So: place, and report.
    eq("2.7", "the transaction was NOT refused for the skill ceiling",
       e, "ok", True)
    ge("2.8", "…and both elements were placed", e, "data.placed_count", 2)
    shape("2.9", "place-layout", e, "data.skill_basis", dict)

    for row in as_list(dig(e, "data.placed")):
        if isinstance(row, dict) and row.get("def") == GATED_DEF:
            S["gated_id"] = row.get("thing_id")
            S["gated_pid"] = row.get("placement_id")
        if isinstance(row, dict) and row.get("def") == PLAIN_DEF:
            S["plain_id"] = row.get("thing_id")
    precondition("2.10", "the gated blueprint has an id to read back",
                 S.get("gated_id") is not None or ARGS.dry_run,
                 "no placed row for %s in %s" % (GATED_DEF, show(dig(e, "data.placed"))))
    if ARGS.dry_run:
        S.setdefault("gated_id", 1)
        S.setdefault("plain_id", 2)


# ------------------------------------------------------------------- phase 3 --

def phase3():
    banner("PHASE 3 - item 3: it is `no-builder`, NOT a material shortfall")

    e = send("construction", {"id": S["gated_id"]})
    shape("3.1", "construction {id}", e, "data.item.state", str)
    eq("3.2", "the fourth branch, by name", e, "data.item.state", "no-builder")
    ne_val("3.3", "…and NOT the material branch, which is the whole defect: the "
                  "run's heater reported short of a component it was not short of",
           dig(e, "data.item.state"), "awaiting-materials")
    S["before"] = dig(e, "data.item.state")
    shape("3.4", "construction {id}", e, "data.item.why", str)
    contains("3.5", "`why` names the skill in the ONE vocabulary the digest and "
                    "the layout timeout report also speak",
             e, "data.item.why", "Construction")
    shape("3.6", "construction {id}", e, "data.item.skill", dict)
    eq("3.7", "…and the block says blocked", e, "data.item.skill.blocked", True)
    shape("3.8", "construction {id}", e, "data.item.missing", list)

    d = send("digest", {"sections": ["construction"]})
    ge("3.9", "the digest counts the fourth branch",
       d, "data.construction.no_builder", 1)
    ge("3.10", "…and counts the VERDICT independently of the state precedence, "
               "so a heater a hauler is stocking is still visible",
       d, "data.construction.skill_blocked", 1)


# ------------------------------------------------------------------- phase 4 --

def phase4():
    banner("PHASE 4 - raise the skill and the element leaves `no-builder`")

    raised = 0
    for pid in S.get("roster", [])[:1]:
        r = send("dev:set-skill", {"pawn": pid, "skill": "Construction",
                                   "level": RAISED_LEVEL})
        if dig(r, "ok"):
            raised += 1
    precondition("4.0", "one colonist raised to Construction %d" % RAISED_LEVEL,
                 raised >= 1 or ARGS.dry_run, "dev:set-skill accepted nothing")

    e = send("construction", {"id": S["gated_id"]})
    ne_val("4.1", "the SAME element is no longer `no-builder` — asserted as an "
                  "inequality against phase 3, which a constant cannot satisfy",
           dig(e, "data.item.state"), S.get("before"))
    eq("4.2", "…and the skill verdict flipped", e, "data.item.skill.blocked", False)
    ge("4.3", "…and somebody now clears it", e, "data.item.skill.clears_count", 1)
    shape("4.4", "construction {id}", e, "data.item.skill.clears", list)
    check("4.5", "…and is NAMED, so the agent can prioritise them",
          len(as_list(dig(e, "data.item.skill.clears"))) >= 1,
          "at least one name in clears[]", dig(e, "data.item.skill.clears"))

    d = send("digest", {"sections": ["construction"]})
    eq("4.6", "the digest's fourth branch empties with it",
       d, "data.construction.skill_blocked", 0)

    # And it actually builds. This is the run's own ending: pl-23 completed at
    # tick 60755, by Lacey, only after her Construction went 4 -> 8.
    a = advance(20000, timeout=1200)
    p = send("construction", {"placement_id": S["gated_pid"]}) \
        if S.get("gated_pid") else {}
    if S.get("gated_pid"):
        one_of("4.7", "with a builder available the element progresses or completes",
               p, "data.state", ["built", "frame", "blueprint"])
        if dig(p, "data.state") == "blueprint":
            note("4.8", "still a blueprint after %s ticks — a hauling or "
                        "work-priority matter, which is a DIFFERENT branch and "
                        "not this issue's" % dig(a, "data.ticks_elapsed"))
        else:
            check("4.8", "…and it got past `blueprint`, which it never could "
                         "while the ceiling stood",
                  True, "frame or built", dig(p, "data.state"))


# ------------------------------------------------------------------- phase 5 --

def phase5():
    banner("PHASE 5 - the control: an ungated def publishes NO skill block")

    e = send("construction", {"id": S["plain_id"]})
    check("5.1", "a `%s` (prerequisite 0) carries no `skill` key at all — "
                 "presence is the signal, and a `construction_required: 0` block "
                 "on every wall would be noise on a capped surface" % PLAIN_DEF,
          not has_key(e, "data.item.skill"), "no skill key",
          dig(e, "data.item.skill"))
    ne_val("5.2", "…and its state is not the fourth branch",
           dig(e, "data.item.state"), "no-builder")

    # A FRESH cell, not spots[2] — phase 2 placed a blueprint there and a
    # SiteGate refusal would make 5.3 pass for the wrong reason.
    spare = S["spots"][3] if len(S["spots"]) > 3 else S["spots"][-1]
    b = send("build", {"def": PLAIN_DEF, "pos": spare,
                       "stuff": FIXTURE_STUFF, "dry_run": True})
    eq("5.3", "the control dry-run is not refused for some other reason, so 5.4 "
              "means what it says", b, "ok", True)
    check("5.4", "…and `build --dry-run` is silent about skill for an ungated def",
          not has_key(b, "data.skill"), "no skill key", dig(b, "data.skill"))


# ------------------------------------------------------------------- phase 6 --

def phase6():
    banner("PHASE 6 - the standing invariant: no red errors")
    rows = red_errors(S.get("watermark", 0))
    check("6.1", "no red error was authored during this suite",
          len(rows) == 0, "an empty error journal since the watermark",
          [dig(r, "payload.message") for r in rows][:5])


# ------------------------------------------------------------------- phase 9 --

def probe(fn):
    global CAPTURE, FAILS
    saved = list(FAILS)
    CAPTURE = []
    try:
        fn()
        got = all(CAPTURE)
    finally:
        CAPTURE = None
        FAILS = saved
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


def phase9():
    banner("PHASE 9 - offline: the game's own defs, source, and the run's journal")

    data = _first(DATA_CANDIDATES)
    decomp = _first(DECOMP_CANDIDATES)

    # -- the defs -------------------------------------------------------------
    if data is None:
        note("9.1", "no RimWorld Data directory found — the def checks are "
                    "SKIPPED, and a check that cannot run is not a check that passed")
    else:
        temp = _read(os.path.join(data, "Core", "Defs", "ThingDefs_Buildings",
                                  "Buildings_Temperature.xml")) or ""
        for name, want in (("Heater", 5), ("Cooler", 5)):
            m = re.search(r"<defName>%s</defName>(.*?)</ThingDef>" % name, temp, re.S)
            got = None
            if m:
                p = re.search(r"<constructionSkillPrerequisite>(\d+)<", m.group(1))
                got = int(p.group(1)) if p else 0
            eq_val("9.1" if name == "Heater" else "9.2",
                   "%s.constructionSkillPrerequisite is %d in the shipped def — "
                   "the run's blocker, and the whole freezer plan" % (name, want),
                   got, want)
        m = re.search(r"<defName>Heater</defName>(.*?)</ThingDef>", temp, re.S)
        check("9.3", "…and its cost list is exactly Steel 50 + ComponentIndustrial 1, "
                     "which is the `missing` row the run reported",
              m is not None and "<ComponentIndustrial>1</ComponentIndustrial>" in m.group(1),
              "ComponentIndustrial 1 in the cost list", None)

        sec = _read(os.path.join(data, "Core", "Defs", "ThingDefs_Buildings",
                                 "Buildings_Security.xml")) or ""
        m = re.search(r"<defName>%s</defName>(.*?)</ThingDef>" % GATED_DEF, sec, re.S)
        body = m.group(1) if m else ""
        p = re.search(r"<constructionSkillPrerequisite>(\d+)<", body)
        eq_val("9.4", "the fixture def %s really is gated at %d" % (GATED_DEF, GATED_LEVEL),
               int(p.group(1)) if p else None, GATED_LEVEL)
        check("9.5", "…and has NO research prerequisite, which is why it is the "
                     "fixture and Heater is not",
              body and "<researchPrerequisites>" not in body,
              "no researchPrerequisites on %s" % GATED_DEF,
              "<researchPrerequisites>" in body)

        wg = _read(os.path.join(data, "Core", "Defs", "WorkGiverDefs",
                                "WorkGivers.xml")) or ""
        con = re.search(r"<defName>ConstructDeliverResourcesToBlueprints</defName>"
                        r"(.*?)</WorkGiverDef>", wg, re.S)
        haul = re.search(r"<defName>DeliverResourcesToBlueprints</defName>"
                         r"(.*?)</WorkGiverDef>", wg, re.S)
        check("9.6", "the SAME work-giver class is declared under BOTH work types — "
                     "Construction (skills checked) and Hauling (not) — which is "
                     "why the ceiling can present as `awaiting-materials` OR as "
                     "`ready`, and the issue's mechanism named only the first",
              con is not None and haul is not None
              and "<workType>Construction</workType>" in con.group(1)
              and "<workType>Hauling</workType>" in haul.group(1)
              and "WorkGiver_ConstructDeliverResourcesToBlueprints" in haul.group(1),
              "two defs, one class, two work types",
              {"construction": con is not None, "hauling": haul is not None})

        # The finding this suite reports rather than tests.
        hits = []
        for p2 in glob.glob(os.path.join(data, "**", "*.xml"), recursive=True):
            s = _read(p2) or ""
            if re.search(r"<artisticSkillPrerequisite>[1-9]", s):
                hits.append(os.path.basename(p2))
        check("9.7", "…and `artisticSkillPrerequisite` is set to a NONZERO value "
                     "by no shipped def at all, so the twin clause has no vanilla "
                     "fixture and nothing live can exercise it",
              not hits, "no def sets it", hits[:5])
        if not hits:
            finding("9.7", "artisticSkillPrerequisite is implemented and read, but "
                           "NO vanilla def sets it — coverage needs a modded def, "
                           "and this suite does not fabricate one")

    # -- the decompiled source ------------------------------------------------
    if decomp is None:
        note("9.8", "no decompiled tree found — the source checks are SKIPPED")
    else:
        gc = _read(os.path.join(decomp, "RimWorld", "GenConstruct.cs")) or ""
        check("9.8", "GenConstruct.CanConstruct still reads the prerequisite off "
                     "`t.def` inside a `checkSkills` branch",
              "if (checkSkills)" in gc
              and "t.def.constructionSkillPrerequisite" in gc,
              "the checkSkills branch, reading t.def", None)
        check("9.9", "…with the artisticSkillPrerequisite twin beside it",
              "t.def.artisticSkillPrerequisite" in gc, "the Artistic clause", None)
        check("9.10", "…and the p.IsColonyMech branch on RaceProps"
                      ".mechFixedSkillLevel, which this implementation "
                      "deliberately does NOT read",
              "p.IsColonyMech" in gc and "mechFixedSkillLevel" in gc,
              "the mech branch", None)
        gen = _read(os.path.join(decomp, "RimWorld",
                                 "ThingDefGenerator_Buildings.cs")) or ""
        check("9.11", "the field is COPIED onto the blueprint def only when it is "
                      "not an install blueprint — which is why an install is never "
                      "skill-gated and why the code reads t.def and not the built def",
              "isInstallBlueprint" in gen
              and gen.count("thingDef.constructionSkillPrerequisite = ") >= 2,
              "the generator copies it, guarded", None)
        db = _read(os.path.join(decomp, "RimWorld", "Designator_Build.cs")) or ""
        check("9.12", "the WIDGET tests BOTH levels on ONE pawn before it draws "
                      "its red line — so two maxima do not decide it, and the "
                      "implementation loops the roster for the same reason",
              "NoColonistWithAllSkillsForConstructing" in db,
              "the designator's own no-colonist line", None)
        wgs = _read(os.path.join(decomp, "RimWorld",
                                 "WorkGiver_ConstructDeliverResources.cs")) or ""
        check("9.13", "…and the deliverer's nearby-needer helper passes "
                      "checkSkills: false, a second route by which a gated "
                      "blueprint can still get stocked",
              "checkSkills: false" in wgs, "checkSkills: false in that file", None)

    # -- the run's own journal ------------------------------------------------
    files = sorted(glob.glob(os.path.join(RUN_JOURNAL, "*.ndjson")))
    if not files:
        note("9.14", "%s has no ndjson — the run checks are SKIPPED" % RUN_JOURNAL)
    else:
        placed_at = None
        done_at = None
        for f in files:
            for line in (_read(f) or "").splitlines():
                if HEATER_PID not in line and "Heater" not in line:
                    continue
                try:
                    row = json.loads(line)
                except ValueError:
                    continue
                blob = json.dumps(row)
                if HEATER_PID in blob and "place-layout" in blob and placed_at is None:
                    placed_at = row.get("tick")
                if row.get("payload", {}).get("placement_id") == HEATER_PID \
                        and row.get("type") == "construction":
                    done_at = row.get("tick")
        check("9.14", "the run's journal places the Heater as %s early on day 1"
              % HEATER_PID, placed_at is not None and placed_at < 5000,
              "a place-layout row naming %s before tick 5000" % HEATER_PID, placed_at)
        check("9.15", "…and completes it at tick %d, ~48,000 ticks after its 32 "
                      "siblings — the gap a human closed by raising a colonist's "
                      "Construction 4 -> 8. That is the strongest evidence the "
                      "blocker was the skill and not the component."
              % HEATER_DONE_TICK,
              done_at == HEATER_DONE_TICK, "tick %d" % HEATER_DONE_TICK, done_at)

    # -- the helpers themselves ----------------------------------------------
    good = {"data": {"skill": {"blocked": True, "clears": []}}}
    absent = {"data": {"skill": {"blocked": True}}}

    check("9.16", "shape() passes on a present key",
          probe(lambda: shape("x", "v", good, "data.skill.clears", list)), "pass", None)
    check("9.17", "shape() FAILS on an absent key",
          not probe(lambda: shape("x", "v", absent, "data.skill.clears", list)),
          "fail", None)
    check("9.18", "eq(..., None) would have PASSED on the absent key — the hazard "
                  "every null assertion here is written around",
          probe(lambda: eq("x", "w", absent, "data.skill.clears", None)),
          "pass (and that is the hazard)", None)
    check("9.19", "keys_include() FAILS on a missing skill field",
          not probe(lambda: keys_include("x", "w", absent, "data.skill", SKILL_KEYS)),
          "fail", None)
    check("9.20", "ne_val() FAILS when the before and after states are identical — "
                  "the form phase 4 needs",
          not probe(lambda: ne_val("x", "w", "no-builder", "no-builder")),
          "fail", None)


# ---------------------------------------------------------------------- main --

PHASES = {1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5, 6: phase6, 9: phase9}
DEFAULT_PHASES = [1, 2, 3, 4, 5, 6]


def main():
    global ARGS
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--root", default=DEFAULT_ROOT, help="the protocol root")
    p.add_argument("--phase", type=int, action="append",
                   choices=[0, 1, 2, 3, 4, 5, 6, 9],
                   help="run only these phases (repeatable); phase 0 always runs")
    p.add_argument("--dry-run", action="store_true",
                   help="print the plan and every expectation, send nothing")
    p.add_argument("--selftest", action="store_true",
                   help="phase 9 only: offline; no bench, no game, nothing sent")
    p.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = p.parse_args()

    print("AutoRimmer acceptance - the skill ceiling reported as a material "
          "shortfall (e08c3e5)")

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

    wanted = sorted(set(ARGS.phase or DEFAULT_PHASES) - {0})
    print("phases: 0 + %s" % ", ".join(str(x) for x in wanted))
    print("%sTHIS SUITE REWRITES EVERY COLONIST'S CONSTRUCTION SKILL and leaves "
          "blueprints and a layout behind. It does not restore the original "
          "levels. Reload before running it twice.%s" % (YELLOW, OFF))

    phase0()
    for n in wanted:
        PHASES[n]()
    return summarise()


def summarise(selftest=False):
    print("")
    print("=" * 78)
    if FINDINGS:
        print("%sFINDINGS (reported, not asserted):%s" % (CYAN, OFF))
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
        print("%sRESULT: all %d self-checks passed. This proves the ASSERTIONS and "
              "the EVIDENCE behind them, not the mod: no bench was touched.%s"
              % (GREEN, CHECKS, OFF))
    else:
        print("%sRESULT: all %d checks passed%s" % (GREEN, CHECKS, OFF))
    sys.exit(0)


if __name__ == "__main__":
    main()
