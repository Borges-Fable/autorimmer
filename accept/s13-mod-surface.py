#!/usr/bin/env python3
"""Acceptance runner for the FIVE session-13 mod findings (M1 A, J, K, I1, D).

Same protocol, helpers and exit codes as `accept/3.5-dialog-verbs.py` and
`accept/3.6-bills-storage.py`; read 3.5's header first — this file follows it
check for check and deliberately does not re-explain the transport.

    ./accept/s13-mod-surface.py                  # the bench sweep (phases 1-5, 8)
    ./accept/s13-mod-surface.py --phase 3        # one phase (0 always runs)
    ./accept/s13-mod-surface.py --selftest       # phase 9 only: NO bench needed
    ./accept/s13-mod-surface.py --root <fakebench root>   # plumbing + phase 9
    ./accept/s13-mod-surface.py --dry-run        # print the plan, send nothing

Phases:
    0  the bench, the dev switch, and THE SHAPE CONTRACT   (always)
    1  A  — dev:spawn-thing: a refusal reaches the JOURNAL ROW
    2  J  — research-set / research: bench_ok is a bench, not a gate
    3  K  — advance: overshoot_bound from the FASTEST speed
    4  I1 — digest.site: where the colony is
    5  D  — threat-pardon: the recorded decision not to fight
    6  D  — save half   (opt-in; pardons, then asks for a save+load)
    7  D  — load half   (opt-in; proves the set survived the round trip)
    8  standing invariants across the whole run
    9  THE SUITE'S OWN ASSERTION MACHINERY (offline, no bench, no game)

Phases 6 and 7 are NOT in the default sweep: 6 stops and asks a human to save
and load the colony, and 7 is meaningless until that has happened. Run
`--phase 6`, save + load in the game, then `--phase 7`.

WHAT CAN BE PROVED WITHOUT A BENCH, honestly. `rwa/fakebench.py` emulates the
POLLER, not the game: it knows eighteen ops, none of them `threat-pardon`,
`research-set` or `dev:spawn-thing`, and its canned `digest` has no `site`
section and no `threats.hostiles_pardoned`. So every VALUE check in phases 1-8
is bench-only by construction, and teaching fakebench to answer them would
prove only that this file agrees with a fixture this file wrote. What IS
provable off-bench is (a) the protocol plumbing, which phase 0's first four
checks exercise against a fakebench root, and (b) THE ASSERTIONS THEMSELVES,
which is phase 9: it runs the real helpers over canned envelopes — correct ones
and deliberately broken ones — and fails if a broken one PASSES. Run
`--selftest` before you take this file to a bench.

Exit 0 = every check passed · 1 = at least one FAIL · 2 = a fixture
precondition could not be met, which is NOT a mod failure and says so.
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
DEFAULT_STATE = os.path.join("/tmp", "s13-mod-surface-pardons.json")

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
CAPTURE = None  # phase 9 only; see probe()

# The five verbs these findings touch. 0.2 asserts every one is in
# `status.data.verbs`: a verb that failed to register produces downstream
# failures indistinguishable from a bad fixture.
S13_OPS = ["dev:spawn-thing", "research-set", "research", "advance", "digest",
           "threat-pardon"]

# TimeDriver.MaxTicksPerFrame, transcribed. `TickManagerUpdate` runs at most
# TickRateMultiplier*2 ticks per frame and the stop check is once per frame, so
# this IS the published bound — and 9.7 re-parses it out of TimeDriver.cs so a
# change to the switch fails HERE rather than silently disagreeing.
MAX_TICKS_PER_FRAME = {
    "Paused": 0, "Normal": 2, "Fast": 6, "Superfast": 24, "Ultrafast": 30,
}
SPEED_ORDER = ["Paused", "Normal", "Fast", "Superfast", "Ultrafast"]

# WorldSafe.Site's field set, exactly. The key SET is asserted, not merely each
# member: the four getters that were deliberately skipped (Tile.Biomes,
# Max/MinTemperature, HillinessLabel, Landmark) are each a lazy-init write on an
# observer read, and a reintroduction under ANY spelling has to fail this suite.
SITE_KEYS = ["biome", "biome_label", "tile", "avg_temp_c", "rainfall",
             "elevation", "hilliness", "swampiness", "pollution", "map_size",
             "pocket_map"]
# The likeliest spellings of the four hazards, named one at a time so a failure
# says WHICH getter came back rather than only "the key set moved".
SITE_FORBIDDEN = {
    "biomes": "Tile.Biomes memoises tmpHasSecondaryBiome/tmpSecondaryBiome",
    "secondary_biome": "Tile.Biomes memoises tmpHasSecondaryBiome/tmpSecondaryBiome",
    "max_temp_c": "Tile.MaxTemperature caches cachedMaxTemp",
    "min_temp_c": "Tile.MinTemperature caches cachedMinTemp",
    "max_temp": "Tile.MaxTemperature caches cachedMaxTemp",
    "min_temp": "Tile.MinTemperature caches cachedMinTemp",
    "hilliness_label": "Tile.HillinessLabel caches hillinessLabelCached",
    "landmark": "Tile.Landmark is Find.World.landmarks[tile], Odyssey-gated",
}
HILLINESS = ["Undefined", "Flat", "SmallHills", "LargeHills", "Mountainous",
             "Impassable"]

# ThreatPardonVerbs.Listing's candidate row, exactly. Id, kind label and
# dormancy ONLY — the fog-discipline decision the implementer made deliberately
# (ThreatPardonVerbs.cs header: "NEVER a position, never a def, never anything
# spatial"). The key set is asserted so a later `at` or `def` fails here.
CANDIDATE_KEYS = ["id", "kind", "pardoned", "dormant", "reason", "lapsed"]
CANDIDATE_FORBIDDEN = ["at", "pos", "position", "def", "defName", "x", "z",
                       "cell", "map", "faction"]


# ------------------------------------------------------------------ protocol --
# commands/<id>.json in, results/<id>.json out. The poller runs a 500 ms cycle
# and ignores inbox files younger than its MinFileAgeMs, so a round trip has a
# floor of roughly 0.25-1 s even for a trivial verb. `advance` is a DEFERRED
# result — its file appears only when the advance finishes.

def send(op, args=None, timeout=300):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "accs13-%03d-%s" % (SEQ, slug)
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
                      "detail": "no results/%s.json within %ss — is the bench running?"
                                % (cid, timeout)}}


def advance(args, timeout=600):
    """`advance`, remembered. Phase 8 re-runs K's two invariants over EVERY
    advance the suite made, so a bound that is right on the one advance phase 3
    inspects and wrong everywhere else cannot pass."""
    e = send("advance", args, timeout=timeout)
    S.setdefault("advances", []).append((dict(args), e))
    return e


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


# MiniJson serialises `(double)n` through ToString("0.####"), so a whole number
# arrives as a JSON integer and json.load gives an int -- but accept either, so
# a serializer change is a shape FAILURE at phase 0 rather than a spurious red
# on a type name.
NUM = (int, float)


def is_num(v):
    return isinstance(v, NUM) and not isinstance(v, bool)


def pos(at):
    """[x,z] -> "x,z". Positions.Out publishes a two-element array; `pos:` takes
    the string form (Positions.Resolve)."""
    if isinstance(at, list) and len(at) >= 2:
        return "%d,%d" % (int(at[0]), int(at[1]))
    return None


# ------------------------------------------------------------------- asserts --

def check(num, what, ok, expected, actual):
    global CHECKS, CAPTURE
    if CAPTURE is not None:          # phase 9: the verdict is the datum
        CAPTURE.append(bool(ok))
        return
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


# eq()/ge() take (env, path). The _val forms take a COMPUTED value, and mixing
# them up is the defect the 2026-08-31 audit found eight times in 3.5's .ps1
# twin. Python raises rather than passing silently, but the two shapes are kept
# distinct here for the same reason.
def eq_val(num, what, got, want):
    check(num, what, got == want, show(want), got)


def ge_val(num, what, got, want):
    check(num, what, is_num(got) and got >= want, ">= %s" % want, got)


def le_val(num, what, got, want):
    check(num, what, is_num(got) and got <= want, "<= %s" % want, got)


def true_val(num, what, ok, expected="true", actual=None):
    check(num, what, bool(ok), expected, actual)


def not_null(num, what, env, path):
    got = dig(env, path)
    check(num, "%s (%s)" % (what, path), got is not None, "present and non-null", got)


def contains(num, what, haystack, needle):
    s = "" if haystack is None else str(haystack)
    check(num, what, needle in s, "contains '%s'" % needle, haystack)


def not_contains(num, what, haystack, needle):
    s = "" if haystack is None else str(haystack)
    check(num, what, needle not in s, "does NOT contain '%s'" % needle, haystack)


def one_of(num, what, got, allowed):
    check(num, what, got in allowed, "one of %s" % (allowed,), got)


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
    print("          This is a FIXTURE gap, not a session-13 failure. Stage it and re-run.")
    sys.exit(2)


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


# ------------------------------------------------------------ shape contract --
# THE TRAP THIS FILE IS BUILT AROUND, and it is not a nicety.
#
# `dig()` returns its default for an ABSENT key and for one that is
# present-and-null alike, so `eq(..., None)` passes either way. A driver whose
# dig paths are WRONG therefore does not fail — it goes GREEN WHILE ASSERTING
# NOTHING, which is strictly worse than a loud abort, because nobody
# investigates a pass. 3.5's `3.2d` spent a whole round in exactly that state,
# and this suite carries several deliberate `== null` assertions of its own
# (`bench_required: null`, `dormant: null`, `action.journal_seq: null`).
#
# has_key() is the predicate dig() cannot be. Phase 0 uses it to PROVE every
# envelope key the later phases dig, naming the verb and the key, so a shape
# change fails THERE — at a check that says which verb moved — instead of
# downstream, or not at all. Phase 9 then proves has_key() itself can fail.
#
# PER-DRIVER ON PURPOSE, not a shared accept/_shapes.py: every file in accept/
# stands alone and runs from a bare checkout.
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
    """The other half. 3.5's 4.8g read `advance`'s `data.ticks` — a key that has
    never existed — for a whole round, and could only fail."""
    check(num, "%s (%s)" % (what, path), not has_key(env, path),
          "the key to be ABSENT", dig(env, path))


def keys_exactly(num, what, env, path, expected):
    """The strongest form: the dict at `path` holds EXACTLY these keys. Used
    where a NEW field is itself the regression — `digest.site` (a reintroduced
    lazy getter) and a `threat-pardon` candidate row (a leaked position)."""
    d = dig(env, path)
    if not isinstance(d, dict):
        check(num, "%s (%s)" % (what, path), False, "an object with keys %s" % expected, d)
        return
    got = sorted(d.keys())
    missing = [k for k in expected if k not in d]
    extra = [k for k in got if k not in expected]
    check(num, "%s (%s)" % (what, path), not missing and not extra,
          "exactly %s" % sorted(expected),
          "missing=%s extra=%s" % (missing, extra) if (missing or extra) else got)


# --------------------------------------------------------- standing invariants --

def no_red_errors(num, what):
    e = send("journal", {"since_seq": S.get("seq0", 0),
                         "types": ["red_error"], "limit": 50})
    eq(num, what, e, "data.count", 0)


def journal_row(seq, types=None):
    """The journal row for one seq. `since_seq` is exclusive, so seq-1 puts the
    row we want first — and the row is then found BY SEQ rather than by
    position, because anything at all may have been journaled in between."""
    if seq is None:
        return None
    e = send("journal", {"since_seq": max(0, int(seq) - 1),
                         "limit": 200, "types": types or []})
    for row in as_list(dig(e, "data.events")):
        if isinstance(row, dict) and row.get("seq") == seq:
            return row
    return None


def ladder_ok(env):
    """K's arithmetic identity, and the one check that can be made on EVERY
    advance envelope: the published bound is the fastest speed's frame, not an
    unrelated number that happens to look plausible."""
    sp = dig(env, "data.overshoot_bound_speed")
    bound = dig(env, "data.overshoot_bound")
    return sp in MAX_TICKS_PER_FRAME and bound == MAX_TICKS_PER_FRAME[sp]


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 — the bench, the dev switch, and THE SHAPE CONTRACT")

    # 0.1  the transport. Works against fakebench too — this and 0.4 are the
    #      only checks in phases 0-8 that do.
    e = send("status")
    precondition("0.1", "the bench answers `status`",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "no status envelope — start _RimWorld-Agent/run-agent.sh")
    shape("0.1a", "status", e, "data.gameLoaded")
    shape("0.1b", "status", e, "data.verbs", list)
    verbs = as_list(dig(e, "data.verbs"))

    # 0.2  SYNTHETIC BENCH DETECTION, before anything asserts a game fact.
    #      fakebench answers eighteen ops and none of them is a session-13 verb,
    #      so the mod-surface phases are skipped rather than failed there.
    v = send("version")
    missing = [o for o in S13_OPS if o not in verbs]
    S["synthetic"] = (not ARGS.dry_run
                      and (dig(v, "data.bench") == "fakebench"
                           or len(missing) == len(S13_OPS)))
    if S["synthetic"]:
        note("0.2", "SYNTHETIC BENCH (%s) — it emulates Poller.cs, not the game. "
                    "Phases 1-8 are skipped; phase 9 still runs."
             % dig(v, "data.bench"))
        S["seq0"] = 0
        e = send("journal", {"limit": 1})
        shape("0.4a", "journal", e, "data.count")
        shape("0.4b", "journal", e, "data.last_seq")
        shape("0.4c", "journal", e, "data.events", list)
        return
    check("0.2", "all %d session-13 ops are registered" % len(S13_OPS),
          ARGS.dry_run or not missing, "no missing ops", missing)
    eq("0.2a", "a game is loaded", e, "data.gameLoaded", True)
    absent("0.2b", "no force-pausing modal is up before we begin", e, "data.forcePause")

    # 0.3  THE DEV SWITCH. Phases 1, 2 and 5 stage with `dev:spawn-thing`,
    #      `dev:destroy`, `dev:finish-research` and `dev:damage`, and every one
    #      of them throws on !Prefs.DevMode (Dev.Gate). `dev:incident` with NO
    #      args is the probe: Dev.Gate runs BEFORE Dev.CurrentMap and before the
    #      def lookup, so it mutates nothing either way.
    e = send("dev:incident")
    detail = str(dig(e, "error.detail") or "")
    precondition("0.3", "devMode is ON (every fixture below needs it)",
                 ARGS.dry_run or "devMode" not in detail,
                 "dev:incident answered: %s  ->  Prefs.DevMode is FALSE on this bench. "
                 "Turn dev mode on in the bench's options (the agent profile seeds it "
                 "True) and re-run." % detail)

    # 0.4  journal: the watermark every later no_red_errors is measured from.
    # `since_seq: 999999999` is NOT belt-and-braces — it is the fix for a
    # known defect, and this file is the one suite that never got it.
    # `JournalVerbs.Read` updates `last_seq` BEFORE the `since_seq` skip and
    # breaks on `events.Count >= limit` BEFORE the append, so a bare
    # `{limit: 1}` reports the SECOND row's seq, not the last. Session 11 found
    # this in `3.4-pawn-orders` and fixed it there, in `3.6`, in `4087644` and
    # in `1.8`; here it survived, and on 2026-09-01 it printed `seq0 = 2`
    # against a 108-row journal — so every red-error check below was counting
    # from the START of the session and charged a run for a DELIBERATE error
    # `journal-selftest` had emitted long before the suite began. Sessions 13
    # and 15 scored 0 FAIL only because nothing happened to precede them.
    e = send("journal", {"since_seq": 999999999, "limit": 1})
    shape("0.4a", "journal", e, "data.count")
    shape("0.4b", "journal", e, "data.last_seq")
    shape("0.4c", "journal", e, "data.events", list)
    S["seq0"] = dig(e, "data.last_seq") or 0
    print("  %sjournal watermark seq0 = %s%s" % (DIM, S["seq0"], OFF))

    # ---- I1: digest.site, every field at its exact path -------------------
    e = send("digest")
    S["digest0"] = e
    precondition("0.5", "`digest` answers",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "digest refused: %s" % show(dig(e, "error")))
    shape("0.5a", "digest", e, "data.site", dict)
    for i, k in enumerate(SITE_KEYS):
        kind = list if k == "map_size" else None
        shape("0.5%s" % "bcdefghijklmnop"[i], "digest", e, "data.site." + k, kind)
    # The key SET, which is the check that survives a rename: a new field here
    # is either one of the four skipped lazy getters coming back or an
    # undocumented addition, and both must be looked at.
    keys_exactly("0.5q", "digest.site publishes exactly WorldSafe.Site's fields",
                 e, "data.site", SITE_KEYS)
    # …and each hazard named individually, so a failure says WHICH getter.
    for i, (k, why) in enumerate(sorted(SITE_FORBIDDEN.items())):
        absent("0.5r%d" % (i + 1),
               "no `%s` — %s (observer-mutation hazard, deliberately skipped)" % (k, why),
               e, "data.site." + k)

    # ---- D: digest.threats, the three counters ----------------------------
    shape("0.6a", "digest", e, "data.threats.hostiles", NUM)
    shape("0.6b", "digest", e, "data.threats.hostiles_pardoned", NUM)
    shape("0.6c", "digest", e, "data.threats.hostiles_unpardoned", NUM)
    shape("0.6d", "digest", e, "data.threats.danger")
    eq_val("0.6e", "unpardoned = hostiles - pardoned, on the digest's own numbers",
           dig(e, "data.threats.hostiles_unpardoned"),
           (dig(e, "data.threats.hostiles") or 0) - (dig(e, "data.threats.hostiles_pardoned") or 0))

    # ---- D: the threat-pardon listing envelope ----------------------------
    e = send("threat-pardon")
    S["pardon0"] = e
    precondition("0.7", "`threat-pardon {}` answers with a listing",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "threat-pardon refused: %s" % show(dig(e, "error")))
    for i, k in enumerate(["verb", "ok", "hostiles", "hostiles_pardoned",
                           "hostiles_unpardoned", "pardons_held", "candidates",
                           "action", "note"]):
        shape("0.7%s" % "abcdefghi"[i], "threat-pardon", e, "data." + k)
    shape("0.7j", "threat-pardon", e, "data.action.journal_seq")
    eq("0.7k", "a bare listing mutated nothing, so no journal line is owed",
       e, "data.action.journal_seq", None)

    # ---- K: the advance envelope ------------------------------------------
    # One tick, deliberately: the smallest budget that still produces a real
    # (non-refused) advance envelope.
    e = advance({"ticks": 1, "speed": "normal"})
    shape("0.8a", "advance", e, "data.reason")
    shape("0.8b", "advance", e, "data.ticks_elapsed", NUM)
    shape("0.8c", "advance", e, "data.speed")
    shape("0.8d", "advance", e, "data.overshoot", NUM)
    shape("0.8e", "advance", e, "data.overshoot_bound", NUM)
    shape("0.8f", "advance", e, "data.overshoot_bound_speed")
    shape("0.8g", "advance", e, "data.max_ticks_in_frame", NUM)
    absent("0.8h", "advance does NOT publish `data.ticks` (3.5's 4.8g read it)",
           e, "data.ticks")
    # A DECLARED HOLE, in 3.5's `0.11` sense. TimeDriver never echoes
    # `timeout_ticks`, so phase 3's `ticks_elapsed <= timeout_ticks +
    # overshoot_bound` invariant is computed from what THIS FILE SENT — exactly
    # as accept/4.2-play-loop.py computes it from the transcript's cmd.json.
    absent("0.8i", "advance does NOT echo `timeout_ticks` — the invariant is "
                   "keyed on what the caller sent (4.2 does the same)",
           e, "data.timeout_ticks")

    # ---- J: the research observer -----------------------------------------
    # THE OTHER DECLARED HOLE, and it is a real asymmetry in the shipped code.
    # `bench_ok`/`bench_required` are published on research-set's SUCCESS
    # envelope and on `research`'s `current` block — and on NEITHER of
    # research-set's refusal envelopes. Phase 2 proves both halves where they
    # live; here we only prove the observer's, and only when a project is
    # current, because `current` is null when none is.
    e = send("research", {"cap": 1})
    precondition("0.9", "`research` answers",
                 ARGS.dry_run or dig(e, "ok") is True,
                 "research refused: %s" % show(dig(e, "error")))
    shape("0.9a", "research", e, "data.source")
    if has_key(e, "data.current") and dig(e, "data.current") is not None:
        shape("0.9b", "research", e, "data.current.bench_required")
        shape("0.9c", "research", e, "data.current.bench_ok", bool)
    else:
        note("0.9b", "no current research project, so `data.current` is null — "
                     "the observer's bench pair is shape-proved at 2.4 instead")


# ------------------------------------------------------------------- phase 1 --
# M1 FINDING A — a refusal now reaches the JOURNAL ROW.
#
# The defect: `dev:spawn-thing` seq 66 journaled `placed: 0, ids: []` and
# nothing else. The response carried `failed`; the row — the only durable record
# — carried no reason at all, and `SimpleResearchBench x0 (WoodLog) @ 123,117`
# reads like a success at a glance.
#
# `ok:true` with `placed:0` is CORRECT and is asserted as such: a refusal is
# information, not breakage (PawnActs.cs:288 — a reached-but-changed-nothing
# call journals its verdict rather than vanishing).

def phase1():
    banner("PHASE 1 — A: `dev:spawn-thing` refuses, and the JOURNAL says why")

    # 1.1 a free cell to work on. `find-rect {w:1,h:1}` with the default
    #     `buildable` requirement is the cheapest way to be handed one.
    if ARGS.pos:
        cell = ARGS.pos
    else:
        e = send("find-rect", {"w": 1, "h": 1, "max": 1,
                               "require": ["buildable", "unroofed"]})
        cell = pos(dig(e, "data.candidates.0.at"))
    precondition("1.1", "a free buildable cell to stage on",
                 ARGS.dry_run or cell is not None,
                 "find-rect found no 1x1 buildable+unroofed cell near the map centre. "
                 "Pass one with --pos x,z.")
    if ARGS.dry_run:
        cell = "100,100"
    print("  %sstaging cell: %s%s" % (DIM, cell, OFF))

    # 1.2 THE CONTROL, and it is not decoration: it is how "WhyNoSpawn returns
    #     null rather than fabricating a cause" is proved. A spawn the game
    #     accepts must publish NO failure anywhere — not in the response, not in
    #     the row. A verb that manufactured a reason when CanSpawnAt is
    #     satisfied would show it here.
    e = send("dev:spawn-thing", {"def": "WoodLog", "count": 1, "pos": cell,
                                 "mode": "direct"})
    precondition("1.2", "a plain WoodLog can be staged on that cell",
                 ARGS.dry_run or (dig(e, "ok") is True and dig(e, "data.placed") == 1),
                 "dev:spawn-thing refused the staging spawn: %s / %s"
                 % (show(dig(e, "error")), show(dig(e, "data"))))
    eq("1.2a", "the accepted spawn reports ok", e, "ok", True)
    absent("1.2b", "an ACCEPTED spawn publishes no `failed` list", e, "data.failed")
    absent("1.2c", "…and no `placed_is_floor`", e, "data.placed_is_floor")
    seq = dig(e, "data.dev.journal_seq")
    not_null("1.2d", "the accepted spawn journaled", e, "data.dev.journal_seq")
    row = journal_row(seq, ["dev"])
    check("1.2e", "its journal row exists at that seq", row is not None,
          "a row at seq %s" % seq, row)
    absent("1.2f", "and the ROW carries no `failed` either — WhyNoSpawn returned "
                   "null and nothing dressed that up as a cause", row, "payload.failed")
    absent("1.2g", "…and no `placed_is_floor`", row, "payload.placed_is_floor")
    not_contains("1.2h", "…and the row's label does not say REFUSED",
                 dig(row, "payload.target"), "REFUSED")

    # 1.3 THE ELSE BRANCH WITH A PLACEABLE TARGET CELL — i.e. WhyNoSpawn
    #     returning NULL on a call that still failed. Verse/GenPlace.
    #     TryPlaceDirect: for an ITEM, `num3 = GetMaxItemsAllowedInCell - items
    #     already there`, which is 0 on an ordinary floor cell that already holds
    #     one non-stackable item, and the spawn loop then never runs. The cell is
    #     walkable and GenSpawn.CanSpawnAt accepts it, so WhyNoSpawn has nothing
    #     to name and must say so by returning null — which the verb renders as
    #     "the target cell itself would take it".
    e = send("dev:spawn-thing", {"def": "Steel", "count": 1, "pos": cell,
                                 "mode": "direct"})
    precondition("1.3", "the second item on that cell is refused",
                 ARGS.dry_run or dig(e, "data.placed") == 0,
                 "Steel PLACED on a cell that already holds a WoodLog (placed=%s). "
                 "GetMaxItemsAllowedInCell was > 1 — the cell is inside a storage or a "
                 "shelf. Pass a plain floor cell with --pos x,z."
                 % show(dig(e, "data.placed")))
    # ok:true on a refusal is the contract, not a bug (PawnActs.cs:288).
    eq("1.3a", "a spawn that placed NOTHING still answers ok:true", e, "ok", True)
    eq("1.3b", "…and reports placed:0", e, "data.placed", 0)
    shape("1.3c", "dev:spawn-thing", e, "data.failed", list)
    shape("1.3d", "dev:spawn-thing", e, "data.placed_is_floor", bool)
    eq("1.3e", "placed is a FLOOR once anything failed", e, "data.placed_is_floor", True)
    shape("1.3f", "dev:spawn-thing", e, "data.failed.0.at", list)
    shape("1.3g", "dev:spawn-thing", e, "data.failed.0.reason")
    shape("1.3h", "dev:spawn-thing", e, "data.failed.0.blocker", dict)
    reason = dig(e, "data.failed.0.reason")
    contains("1.3i", "the reason names the verb that refused", reason,
             "GenPlace.TryPlaceThing found no spot")
    # THE NULL BRANCH, in the verb's own words.
    contains("1.3j", "WhyNoSpawn returned NULL, so the reason says the target cell "
                     "would have taken it", reason, "the target cell itself would take it")
    not_contains("1.3k", "…and does NOT invent a per-cell cause", reason,
                 "at the target cell:")
    # THE M1 REGRESSION ITSELF: the old code described the blocker with
    # `target.GetFirstBuilding(map)`, which is null for an item — so the blocker
    # was null for exactly this case. Blockers.At walks the edifice, then the
    # thing list.
    eq("1.3l", "Blockers.At found the ITEM holding the cell (GetFirstBuilding "
               "returned null here, which was M1 finding A)",
       e, "data.failed.0.blocker.def", "WoodLog")
    shape("1.3m", "dev:spawn-thing", e, "data.failed.0.blocker.label")
    eq("1.3n", "…classified as clearable by nothing (not a building, not mineable)",
       e, "data.failed.0.blocker.removal", "none")
    shape("1.3o", "dev:spawn-thing", e, "data.failed.0.blocker.at", list)
    eq("1.3p", "…and `refused` carries WHY THE CELL refused, not how the thing clears",
       e, "data.failed.0.blocker.refused", reason)
    # Shape FIRST, then the null: `eq(...,None)` would pass on an absent key, and
    # MiniJson writes every dictionary entry including the null ones, so the key
    # being missing here would be a real change and must fail.
    shape("1.3q", "dev:spawn-thing", e, "data.failed.0.blocker.reason")
    eq("1.3r", "…while `reason` stays Classify's answer (null for a plain item: not "
               "mineable, not a building, so the game has no sentence to give)",
       e, "data.failed.0.blocker.reason", None)
    # 1.3s-u THE 8b4839f KEYS on the branch where the refusing cell really IS
    #        the target. `at` echoes the caller's argument and always did;
    #        `cell` is the cell that refused and `cell_role` is which tier of
    #        the gate refused it. Here WhyNoSpawn named no branch at all —
    #        CanSpawnAt accepted this cell and GenPlace still found nowhere —
    #        so the pair must say exactly that rather than pointing at a tier.
    shape("1.3s", "dev:spawn-thing", e, "data.failed.0.cell", list)
    eq("1.3t", "with no branch to name, the refusing cell IS the target",
       e, "data.failed.0.cell", dig(e, "data.failed.0.at"))
    eq("1.3u", "…and cell_role says so instead of naming a tier",
       e, "data.failed.0.cell_role", "place-search")

    # 1.4 THE FINDING PROPER: the same facts in the JOURNAL ROW.
    seq = dig(e, "data.dev.journal_seq")
    not_null("1.4", "the refusal journaled at all", e, "data.dev.journal_seq")
    row = journal_row(seq, ["dev"])
    check("1.4a", "the row exists at that seq", row is not None,
          "a row at seq %s" % seq, row)
    eq("1.4b", "it is a `dev` row", row, "type", "dev")
    eq("1.4c", "for this verb", row, "payload.verb", "dev:spawn-thing")
    eq("1.4d", "and this step", row, "payload.step", "spawn-thing")
    eq("1.4e", "carrying placed:0", row, "payload.placed", 0)
    # Dev.Emit MERGES `extra` into the payload — it does not nest it under an
    # `extra` key — so these are payload.failed / payload.placed_is_floor.
    shape("1.4f", "the journal row", row, "payload.failed", list)
    eq("1.4g", "the ROW's `placed_is_floor` says the count is a floor",
       row, "payload.placed_is_floor", True)
    eq("1.4h", "the ROW's failure carries the same reason the response did",
       row, "payload.failed.0.reason", reason)
    shape("1.4i", "the journal row", row, "payload.failed.0.blocker", dict)
    eq("1.4j", "…including the blocker the response named",
       row, "payload.failed.0.blocker.def", "WoodLog")
    contains("1.4k", "and the row's own LABEL says REFUSED, so a human scanning "
                     "JOURNAL.md cannot read it as a success",
             dig(row, "payload.target"), "REFUSED")
    shape("1.4l", "the journal row", row, "payload.args", dict)
    eq("1.4m", "the row echoes the def that was asked for",
       row, "payload.args.def", "Steel")

    # 1.5 THE DIRECT/BUILDING BRANCH — GenSpawn.CanSpawnAt refuses, and
    #     WhyNoSpawn names WHICH clause. A building aimed at the cell we just
    #     filled is refused for the interaction cell or the terrain; a building
    #     aimed at a wall is refused for walkability. Either way the reason must
    #     be a NAMED branch and never the `??` fallback.
    e = send("dev:spawn-thing", {"def": "SimpleResearchBench", "count": 1,
                                 "pos": cell, "mode": "direct", "stuff": "WoodLog"})
    if dig(e, "data.placed") == 0:
        eq("1.5a", "the building spawn refused with ok:true", e, "ok", True)
        shape("1.5b", "dev:spawn-thing", e, "data.failed.0.reason")
        shape("1.5c", "dev:spawn-thing", e, "data.failed.0.blocker", dict)
        r = dig(e, "data.failed.0.reason")
        not_contains("1.5d", "WhyNoSpawn named a CLAUSE rather than falling back to "
                             "'CanSpawnAt refused this cell'", r,
                     "GenSpawn.CanSpawnAt refused this cell for")
        eq("1.5e", "and the blocker's `refused` is that same clause",
           e, "data.failed.0.blocker.refused", r)
        shape("1.5f", "dev:spawn-thing", e, "data.failed.0.blocker.removal")
        one_of("1.5g", "the blocker says HOW it clears (Blockers.Classify)",
               dig(e, "data.failed.0.blocker.removal"),
               ["mine", "deconstruct", "attack", "none"])
        # 1.5h-j THE DEFECT 8b4839f WAS FILED FOR. Bench 20260901T121508 refused
        #        a HiTechResearchBench for granite on the interaction cell one
        #        row SOUTH of the footprint, and then described a WoodLog on the
        #        target cell with removal:"none" — the opposite of the truth,
        #        since the granite clears by mining. The tier is what makes that
        #        readable without arithmetic.
        shape("1.5h", "dev:spawn-thing", e, "data.failed.0.cell", list)
        one_of("1.5i", "cell_role names which tier of the gate refused",
               dig(e, "data.failed.0.cell_role"),
               ["bounds", "footprint", "terrain", "interaction",
                "blocks-interaction", "def"])
        if dig(e, "data.failed.0.cell_role") == "interaction":
            check("1.5j", "the interaction tier reports the REFUSING cell, which is "
                          "OFF the footprint — the whole of 8b4839f",
                  dig(e, "data.failed.0.cell") != dig(e, "data.failed.0.at"),
                  "failed[0].cell != failed[0].at",
                  {"cell": dig(e, "data.failed.0.cell"), "at": dig(e, "data.failed.0.at")})
        else:
            note("1.5", "cell_role is %s, not 'interaction', so the off-footprint case "
                        "is not exercised on this cell. Stage rock one cell south of a "
                        "research bench's footprint to hit it."
                        % show(dig(e, "data.failed.0.cell_role")))
    else:
        note("1.5", "the research bench PLACED on that cell (direct mode wipes with "
                    "WipeMode.VanishOrMoveAside), so the CanSpawnAt branch was not "
                    "exercised here — 1.6 is the branch M1 took. Cleaning it up.")
        ids = [d.get("id") for d in as_list(dig(e, "data.spawned")) if isinstance(d, dict)]
        if ids:
            send("dev:destroy", {"things": ids})

    # 1.6 THE `near` BRANCH — the branch M1's seq 66 actually took, and the one
    #     that previously published NO blocker at all (the else branch had none:
    #     the old `Blockers.Describe(GetFirstBuilding(...))` lived only in the
    #     direct/building arm).
    #
    #     Verse/GenPlace.TryFindPlaceSpotNear searches GenRadial out to
    #     NumCellsInRadius(12.9) and every candidate is gated by
    #     GenSpawn.CanSpawnAt, whose first per-cell clause is `!c.Walkable`. So a
    #     near-mode failure needs ~520 unwalkable cells around the target: a cell
    #     deep inside solid rock, deep water, or any other impassable mass. That
    #     cannot be manufactured with a verb, so it is an operator-supplied cell.
    if not ARGS.near_cell:
        note("1.6", "no --near-cell given, so the mode:\"near\" arm is NOT exercised. "
                    "It shares its code with 1.3/1.4 (the else branch covers near for "
                    "every def AND direct for non-buildings), but the near WORDING is "
                    "only proved here. Find a cell >= 13 cells inside solid rock or deep "
                    "water (`rwa map-view --rect ...`) and pass --near-cell x,z.")
    else:
        e = send("dev:spawn-thing", {"def": "SimpleResearchBench", "count": 1,
                                     "pos": ARGS.near_cell, "mode": "near",
                                     "stuff": "WoodLog"})
        precondition("1.6", "--near-cell is walled in enough for `near` to fail",
                     ARGS.dry_run or dig(e, "data.placed") == 0,
                     "the bench PLACED (placed=%s) — the radial search found a spot "
                     "within 13 cells, so this cell is not deep enough inside the mass."
                     % show(dig(e, "data.placed")))
        eq("1.6a", "a `near` refusal is still ok:true", e, "ok", True)
        eq("1.6b", "…with placed:0", e, "data.placed", 0)
        eq("1.6c", "…and placed_is_floor", e, "data.placed_is_floor", True)
        r = dig(e, "data.failed.0.reason")
        contains("1.6d", "the reason names the mode", r, "in mode near")
        contains("1.6e", "…and reports the target cell's own refusal", r,
                 "at the target cell:")
        # THE FIX: this arm had no blocker at all before session 13.
        shape("1.6f", "dev:spawn-thing", e, "data.failed.0.blocker", dict)
        not_null("1.6g", "the `near` arm now names a blocker (M1 finding A: it had "
                         "none)", e, "data.failed.0.blocker.at")
        shape("1.6h", "dev:spawn-thing", e, "data.failed.0.blocker.removal")
        # `Blockers.At(map, target, branch ?? why)` is passed the CLAUSE on this
        # arm, while `reason` is the whole sentence with the clause appended —
        # so the sentence must end with what the blocker carries.
        refused = dig(e, "data.failed.0.blocker.refused")
        check("1.6i", "`refused` carries WhyNoSpawn's clause (the reason ends with it), "
                      "not the whole sentence",
              isinstance(refused, str) and len(refused) > 0
              and isinstance(r, str) and r.endswith(refused) and r != refused,
              "reason ends with blocker.refused and is longer than it",
              {"reason": r, "refused": refused})
        row = journal_row(dig(e, "data.dev.journal_seq"), ["dev"])
        shape("1.6j", "the journal row", row, "payload.failed.0.blocker", dict)
        eq("1.6k", "the row carries the near-arm reason too", row,
           "payload.failed.0.reason", r)
        # The ROW is the durable record, so the 8b4839f keys have to be in it
        # too — a response-only field is a field that is gone by the next run.
        shape("1.6l", "the journal row", row, "payload.failed.0.cell", list)
        shape("1.6m", "the journal row", row, "payload.failed.0.cell_role")

    # 1.8 THE TYPO THAT REPORTED SUCCESS (git-bug 7382bdd). `at` is
    #     dev:spawn-thing's own OUTPUT key and the obvious guess for its input;
    #     `Dev.PosArg` used to read `pos`, get null, and silently return the
    #     colony anchor, so three consecutive spawns aimed at [999,999],
    #     [107,119] and [90,90] all landed at [125,129] and all reported
    #     ok:true, placed:1. The refusal must NAME the key and SUGGEST the
    #     right one.
    e = send("dev:spawn-thing", {"def": "WoodLog", "count": 1, "at": cell,
                                 "mode": "direct"})
    eq("1.8a", "an unknown cell-argument name is refused, not defaulted",
       e, "ok", False)
    eq("1.8b", "…as bad-args", e, "error.code", "bad-args")
    detail = str(dig(e, "error.detail", ""))
    contains("1.8c", "the refusal names the key that was not read", detail, "'at'")
    contains("1.8d", "…and suggests the one that would have been", detail, "'pos'")
    # A refused call must not have MUTATED anything: a failed envelope carries
    # no data at all, and the guard fires before ThingMaker is ever reached.
    absent("1.8e", "a refused call publishes no data block", e, "data")

    # 1.9 AND WHEN THE DEFAULT LEGITIMATELY FIRES, IT SAYS SO. The narrow fix's
    #     other half: `pos_source` is in the envelope AND in the journal row, so
    #     "the caller's coordinates" and "the colony anchor" never read alike.
    e = send("dev:spawn-thing", {"def": "WoodLog", "count": 1, "pos": cell,
                                 "mode": "direct"})
    shape("1.9a", "dev:spawn-thing", e, "data.pos_source")
    eq("1.9b", "a cell that came from the argument says so",
       e, "data.pos_source", "arg")
    row = journal_row(dig(e, "data.dev.journal_seq"), ["dev"])
    eq("1.9c", "and the ROW carries it, because the row is the durable record",
       row, "payload.args.pos_source", "arg")
    ids = [d.get("id") for d in as_list(dig(e, "data.spawned")) if isinstance(d, dict)]
    if ids:
        send("dev:destroy", {"things": ids})

    e = send("dev:spawn-thing", {"def": "WoodLog", "count": 1, "mode": "direct"})
    eq("1.9d", "…and a cell that came from the colony anchor says THAT",
       e, "data.pos_source", "anchor-default")
    ids = [d.get("id") for d in as_list(dig(e, "data.spawned")) if isinstance(d, dict)]
    if ids:
        send("dev:destroy", {"things": ids})

    # 1.10 THE GENERAL CASE — an unknown argument NAME on any verb, which is
    #      this issue's bullet 1 and which the near-miss detector above does
    #      NOT satisfy. The 2026-09-01 bench pass measured the hole and said
    #      so: `dev:spawn-thing --def WoodLog --pos … --mode direct --wibble 3`
    #      returned ok:true and placed the log, and `things`, `find-rect`,
    #      `site-survey`, `map-view`, `nearest`, `reachable`, `pawns` and
    #      `digest` all took `--wibble 3` in silence.
    #
    #      What closes the SILENCE is VerbArgs' READ LOG, not a declaration:
    #      every accessor marks the key it was asked for, so after the handler
    #      returns, `supplied − queried` is the unknown-argument set — derived
    #      from the code that does the reading, covering all 120 verbs, needing
    #      no update when a verb gains an argument.
    #
    #      IT REPORTS, IT DOES NOT REFUSE, and that is measured: 73 keys across
    #      26 verbs are read only on some paths while the verb still succeeds
    #      (`zone {op:"add", plant, dry_run:true}`, `wear {queue:true}` when the
    #      gate refuses, every `bill-add` refusal that returns before its twenty
    #      levers are read). Refusing those would refuse legitimate calls
    #      mid-run. The colony-ending case is caught instead by the
    #      PRE-mutation guard in 1.11.
    e = send("digest", {"wibble": 3})
    eq("1.10a", "a junk key does not stop the verb — it is reported, not refused",
       e, "ok", True)
    shape("1.10b", "digest", e, "ignored_args.keys", list)
    eq_val("1.10c", "…and the key it names is the one that was never read",
           dig(e, "ignored_args.keys"), ["wibble"])
    contains("1.10d", "…with a sentence saying it was dropped",
             str(dig(e, "ignored_args.detail", "")), "'wibble'")

    # THE CONTROL, and it is the one that matters for every other suite: a
    # correct call carries no such field at all, so nothing that reads these
    # envelopes sees a new key until it makes this mistake.
    e = send("digest", {})
    absent("1.10e", "a clean call publishes NO ignored_args", e, "ignored_args")
    e = send("pawns", {"filter": "colonist", "cap": 5, "order": "id"})
    absent("1.10f", "…and neither does a fully-specified one", e, "ignored_args")
    eq("1.10g", "…which still succeeds", e, "ok", True)

    # The report names the keys the call DID read — the caller's route to the
    # right spelling, and available for every verb without a declaration.
    e = send("pawns", {"filter": "colonist", "wibble": 3})
    eq("1.10h", "a junk key ALONGSIDE valid args is reported too — the earlier "
                "sweep that missed this was confounded by the missing-arg error "
                "firing first",
       e, "ok", True)
    shape("1.10i", "pawns", e, "ignored_args.read", list)
    contains("1.10j", "…and lists the keys the verb actually read",
             str(dig(e, "ignored_args.read")), "filter")

    # The suggestion is derived from those same read keys — no alias table, so
    # it keeps working for arguments added after this check was written.
    e = send("pawns", {"filte": "colonist"})
    contains("1.10k", "a one-edit typo is suggested from the keys the verb read",
             str(dig(e, "ignored_args.detail", "")), "Did you mean 'filter'")

    # THE MUTATING CASE, which is why the report carries journal seqs: the verb
    # ran, so "what did that dropped argument cost me" has to be answerable
    # from the envelope. This is comment #6's exact bench call.
    e = send("dev:spawn-thing", {"def": "WoodLog", "count": 1, "pos": cell,
                                 "mode": "direct", "wibble": 3})
    eq("1.10l", "the bench-measured silent case now reports", e, "ok", True)
    eq_val("1.10m", "…naming the key", dig(e, "ignored_args.keys"), ["wibble"])
    shape("1.10n", "dev:spawn-thing", e, "ignored_args.journal_seq_from")
    shape("1.10o", "dev:spawn-thing", e, "ignored_args.journal_seq_to")
    ids = [d.get("id") for d in as_list(dig(e, "data.spawned")) if isinstance(d, dict)]
    if ids:
        send("dev:destroy", {"things": ids})

    # 1.11 THE DESTRUCTIVE INSTANCE (git-bug 7382bdd comment #7). `kind` is not
    #      a parameter of `journal-selftest`; on 2026-09-01 it was dropped
    #      without a word, the verb fell through to its default step list
    #      (letter, message, error, DOWNED, BREAK) and downed all three
    #      colonists and started a berserk while returning ok:true. Four calls
    #      went by before it was noticed.
    #
    #      Post-dispatch refusal is not enough here — it would refuse AFTER the
    #      colonists were on the ground. `VerbArgs.RefuseStray` runs before the
    #      first step, so this is the pre-mutation case and 1.11g is the part
    #      that proves it.
    e = send("journal", {"limit": 1})
    seq_before = dig(e, "data.last_seq", 0)
    e = send("journal-selftest", {"kind": "save"})
    eq("1.11a", "an unknown argument name on a MUTATING verb is refused",
       e, "ok", False)
    eq("1.11b", "…as bad-args", e, "error.code", "bad-args")
    detail = str(dig(e, "error.detail", ""))
    contains("1.11c", "…naming the unknown key", detail, "'kind'")
    contains("1.11d", "…and naming the arguments it DOES accept", detail, "'steps'")
    contains("1.11e", "…and saying plainly that nothing ran", detail,
             "nothing was mutated")
    absent("1.11f", "a refused call publishes no data block", e, "data")
    # The whole point: not one step executed. `dev` is the fixture's own
    # provenance row, `downed` and `mental_break` are what the default list did
    # to the colony last time.
    e = send("journal", {"since_seq": seq_before, "limit": 50,
                         "types": ["dev", "downed", "mental_break"]})
    eq("1.11g", "NOTHING WAS MUTATED — no dev, downed or mental_break row was "
                "written between the call and its refusal",
       e, "data.count", 0)

    # THE CONTROL for the guard, on the same verb: a legitimate explicit step
    # list still runs. `message` is the one step that mutates nothing.
    e = send("journal-selftest", {"steps": ["message"]})
    eq("1.11h", "…while a legitimate journal-selftest call still runs", e, "ok", True)

    # 1.7 tidy: the staged log is left where it is (it is one wood log), but the
    #     run must not have raised a red error doing any of this.
    no_red_errors("1.7", "zero red errors across the spawn-refusal phase")


# ------------------------------------------------------------------- phase 2 --
# M1 FINDING J — `bench_ok` is PlayerHasAnyAppropriateResearchBench alone.
#
# The defect: `bench_ok: true` on a map with no research bench at all, because
# the old expression short-circuited on `requiredResearchBuilding == null`. It
# hid a destroyed bench for five in-game days. `bench_required` now carries the
# gate's INPUT (the demanded defName, or null) and `bench_ok` means what its
# name says.
#
# THE SHIPPED ASYMMETRY, and it is not what the brief assumed: the pair is
# published on research-set's SUCCESS envelope and on `research`'s `current`
# block only. A project that NEEDS a bench with no bench present is refused by
# WorldSafe.CanStart before that dict is built, so the refusal envelope carries
# `blocked_by:"no-bench"` and no pair at all. All three shapes are proved below,
# each on the surface that actually publishes it.

BENCH_DEFS = ["SimpleResearchBench", "HiTechResearchBench"]
# Every vanilla bench-requiring project needs MicroelectronicsBasics or deeper
# (Data/Core/Defs/ResearchProjectDefs/ResearchProjects_3_Microelectronics.xml);
# MoisturePump is the cheapest of them that needs nothing else.
BENCH_PROJECT = "MoisturePump"
BENCH_PROJECT_PREREQ = "MicroelectronicsBasics"


def bench_count(num, expect_zero):
    total = 0
    for d in BENCH_DEFS:
        e = send("things", {"def": d, "detail": True, "detail_cap": 50})
        n = len(as_list(dig(e, "data.detail")))
        c = dig(e, "data.count")
        total += n if n else (c or 0)
    if expect_zero:
        eq_val(num, "no research bench of any kind is on the map", total, 0)
    return total


def phase2():
    banner("PHASE 2 — J: `bench_ok` is a BENCH, not a gate")

    proj_arg = ARGS.bench_project or BENCH_PROJECT
    # 2.1 stage: finish the prerequisite chain so a bench-requiring project is
    #     startable at all. ResearchManager.FinishProject recurses into
    #     prerequisites, so one call does the ladder.
    # BENCH_PROJECT_PREREQ is a HINT, not the answer: which project sits below
    # the bench project differs between versions and modlists, and hardcoding
    # one made this phase fail against a stock 1.6 map (MoisturePump needs
    # Machining, not MicroelectronicsBasics). Read the chain off the refusal
    # instead — `research-set` already publishes `prerequisites` on a
    # blocked_by:"prerequisites" refusal, so the game tells us what it wants.
    e = send("dev:finish-research", {"project": ARGS.bench_prereq or BENCH_PROJECT_PREREQ})
    for _ in range(6):
        probe = send("research-set", {"project": proj_arg})
        if dig(probe, "data.blocked_by") != "prerequisites":
            break
        pending = dig(probe, "data.prerequisites") or []
        if not pending:
            break
        for prereq in pending:
            e = send("dev:finish-research", {"project": prereq})
    precondition("2.1", "the bench-project's prerequisite chain is finished",
                 ARGS.dry_run
                 or dig(send("research-set", {"project": proj_arg}),
                        "data.blocked_by") != "prerequisites",
                 "could not clear %s's prerequisite chain; last refusal: %s. Pass "
                 "--bench-prereq/--bench-project for a modded ladder."
                 % (proj_arg, show(dig(e, "error"))))

    # 2.2 stage: NO research bench anywhere. This is the M1 state.
    send("dev:destroy", {"def": "SimpleResearchBench", "radius": 200})
    send("dev:destroy", {"def": "HiTechResearchBench", "radius": 200})
    left = bench_count("2.2", expect_zero=True)
    precondition("2.2a", "the map really has no research bench left",
                 ARGS.dry_run or left == 0,
                 "%d research bench(es) survived dev:destroy — they are outside the "
                 "200-cell radius of the colony anchor. Remove them and re-run; "
                 "bench_ok is a per-MAP-LIST question and one survivor makes it true."
                 % left)

    # 2.3 SHAPE THREE, and THE REGRESSION. With zero benches, WorldSafe.CanStart
    #     blocks every project that demands one — so anything still `available`
    #     demands none, i.e. `bench_required` MUST be null. The old code answered
    #     `bench_ok: true` here. It must now answer false.
    e = send("research", {"cap": 5})
    free = dig(e, "data.available.list.0.def")
    precondition("2.3", "at least one startable project that needs no bench",
                 ARGS.dry_run or free is not None,
                 "`research` lists no available project at all — everything is finished "
                 "or blocked. Load a colony with research left to do.")
    e = send("research-set", {"project": free})
    eq("2.3a", "a project needing no bench is accepted with no bench on the map",
       e, "data.ok", True)
    eq("2.3b", "…and it really became the current project", e, "data.current", free)
    shape("2.3c", "research-set", e, "data.bench_required")
    shape("2.3d", "research-set", e, "data.bench_ok", bool)
    eq("2.3e", "bench_required is NULL — vanilla demands no particular bench",
       e, "data.bench_required", None)
    eq("2.3f", "bench_ok is FALSE — there is no bench that can research it. THIS IS "
               "M1 FINDING J: the old expression short-circuited to true here",
       e, "data.bench_ok", False)
    contains("2.3g", "…and the note says the project will make no progress",
             dig(e, "data.note"), "bench_ok:false")

    # 2.4 the same pair on the OBSERVER, which is where a missing bench was
    #     supposed to be visible all along.
    e = send("research", {"cap": 1})
    shape("2.4a", "research", e, "data.current.bench_required")
    shape("2.4b", "research", e, "data.current.bench_ok", bool)
    eq("2.4c", "the observer agrees: no bench required", e, "data.current.bench_required", None)
    eq("2.4d", "the observer agrees: no bench available", e, "data.current.bench_ok", False)

    # 2.5 SHAPE ONE — a project that DEMANDS a bench, with none on the map. The
    #     gate refuses (unchanged and faithful: CanStartNow treats a null
    #     requiredResearchBuilding as satisfied and a non-null one as demanding).
    e = send("research-set", {"project": proj_arg})
    eq("2.5a", "a bench-requiring project is REFUSED when no bench exists",
       e, "data.ok", False)
    eq("2.5b", "…naming the clause", e, "data.blocked_by", "no-bench")
    contains("2.5c", "…in the game's own terms", dig(e, "data.reason"),
             "no research bench of the required kind")
    if proj_arg == BENCH_PROJECT:
        contains("2.5c2", "…and BlockedReason names the demanded def, which is the "
                          "only place a caller can read it on this path",
                 dig(e, "data.reason"), "HiTechResearchBench")
    shape("2.5d1", "research-set", e, "data.action.journal_seq")
    eq("2.5d", "nothing was journaled for a refusal", e, "data.action.journal_seq", None)
    # DECLARED, not assumed: the refusal path publishes neither field. Asserted
    # so that a later round which ADDS them has to come here and say so.
    absent("2.5e", "the refusal envelope carries no `bench_ok` (the pair lives on the "
                   "success envelope and on `research.current`)", e, "data.bench_ok")
    absent("2.5f", "…and no `bench_required`", e, "data.bench_required")

    # 2.6 SHAPE TWO — the same project with the bench present.
    # find-rect approves a rect on its own cells; a workbench ALSO needs its
    # INTERACTION SPOT clear, which sits outside the rect and which find-rect
    # does not model. On a mountainous map the first candidate is routinely a
    # pocket whose interaction cell is granite, so take several and try each —
    # one candidate is not a fixture, it is a coin flip.
    e = send("find-rect", {"w": 3, "h": 2, "max": 8, "require": ["buildable"]})
    cands = [pos(c.get("at")) for c in (dig(e, "data.candidates") or [])]
    precondition("2.6", "somewhere to put a HiTechResearchBench",
                 ARGS.dry_run or bool(cands),
                 "find-rect found no 3x2 buildable rect.")
    at, e = None, None
    for cand in (cands or []):
        e = send("dev:spawn-thing", {"def": "HiTechResearchBench", "count": 1,
                                     "pos": cand, "mode": "direct"})
        if dig(e, "data.placed") == 1:
            at = cand
            break
    if ARGS.dry_run:
        at = at or "0,0"
    precondition("2.6a", "a HiTechResearchBench was spawned",
                 ARGS.dry_run or dig(e, "data.placed") == 1,
                 "dev:spawn-thing placed %s benches: %s"
                 % (show(dig(e, "data.placed")), show(dig(e, "data.failed"))))
    S["bench_ids"] = [d.get("id") for d in as_list(dig(e, "data.spawned"))
                      if isinstance(d, dict)]
    e = send("research-set", {"project": proj_arg})
    eq("2.6b", "the bench-requiring project is now accepted", e, "data.ok", True)
    shape("2.6c", "research-set", e, "data.bench_required")
    demanded = dig(e, "data.bench_required")
    S["demanded"] = demanded
    check("2.6d", "bench_required names the demanded def — NOT null, which is the "
                  "whole distinction M1 finding J restored",
          isinstance(demanded, str) and len(demanded) > 0, "a bench defName", demanded)
    if proj_arg == BENCH_PROJECT:
        eq_val("2.6d2", "…and for MoisturePump that def is HiTechResearchBench",
               demanded, "HiTechResearchBench")
    eq("2.6e", "bench_ok is TRUE — a bench that can research THIS project exists",
       e, "data.bench_ok", True)

    # 2.7 the pair is about BENCHES, not about the gate: the no-bench-required
    #     project's `bench_ok` must now flip to true, because
    #     ResearchProjectDef.CanBeResearchedAt accepts any bench when
    #     requiredResearchBuilding is null.
    e = send("research-set", {"project": free})
    eq("2.7a", "bench_required is still null for that project",
       e, "data.bench_required", None)
    eq("2.7b", "…but bench_ok is now TRUE, because a bench exists. The field tracks "
               "benches, not the gate", e, "data.bench_ok", True)

    # 2.8 THE M1 SCENARIO, end to end: make the bench-requiring project current,
    #     destroy the bench, and read the observer. This is the state that hid
    #     for five in-game days.
    send("research-set", {"project": proj_arg})
    for tid in S.get("bench_ids", []):
        send("dev:destroy", {"thing": tid})
    bench_count("2.8", expect_zero=True)
    e = send("research", {"cap": 1})
    eq("2.8a", "the destroyed bench is VISIBLE on the observer: bench_ok false",
       e, "data.current.bench_ok", False)
    eq("2.8b", "…while bench_required still names what the project demands",
       e, "data.current.bench_required", S.get("demanded"))
    eq("2.8c", "…and the project is still selected (a lost bench does not deselect it)",
       e, "data.current.def", proj_arg)

    no_red_errors("2.9", "zero red errors across the research phase")


# ------------------------------------------------------------------- phase 3 --
# M1 FINDING K — `overshoot_bound` comes from a fastest-speed high-water mark.
#
# The defect: the bound was computed from `activeSpeed`, the speed the advance
# EXITED at. A 192-tick advance that ran Ultrafast and exited Superfast
# published 24 against a measured overshoot of 30. `fastestSpeed` is now a
# per-advance high-water mark fed by every write to activeSpeed (NoteActive),
# and `overshoot_bound_speed` names it.

def phase3():
    banner("PHASE 3 — K: the overshoot bound is the FASTEST speed's frame")

    # 3.1 the identity, on a plain advance. `overshoot` exists only when
    #     Target >= 0 (i.e. for `advance {ticks:N}`), which is why 4.2 reads
    #     `overshoot_bound` and never `overshoot`.
    e = advance({"ticks": 600, "speed": "superfast"})
    eq("3.1a", "the advance ran to its tick budget", e, "data.reason", "ticks")
    eq("3.1b", "…at the speed asked for", e, "data.speed", "Superfast")
    eq("3.1c", "the bound names the speed it came from", e,
       "data.overshoot_bound_speed", "Superfast")
    eq("3.1d", "…and equals TimeDriver.MaxTicksPerFrame for it", e,
       "data.overshoot_bound", MAX_TICKS_PER_FRAME["Superfast"])
    # THE INVARIANT THE BOUND EXISTS TO MAKE TRUE, and the one M1 broke.
    le_val("3.1e", "the measured overshoot is inside the published bound",
           dig(e, "data.overshoot"), dig(e, "data.overshoot_bound"))
    le_val("3.1f", "ticks_elapsed <= ticks + overshoot_bound",
           dig(e, "data.ticks_elapsed"), 600 + (dig(e, "data.overshoot_bound") or 0))
    le_val("3.1g", "…and no single frame ran more ticks than the bound",
           dig(e, "data.max_ticks_in_frame"), dig(e, "data.overshoot_bound"))

    # 3.2 THE PRE-ARM EXCLUSION, and it is documented behaviour rather than a
    #     guess: TimeDriver.Start clears `fastestSpeed = TimeSpeed.Paused` and
    #     then assigns `activeSpeed = tm.CurTimeSpeed` DIRECTLY — not through
    #     NoteActive — before SetSpeed takes the first reading. The comment says
    #     why: the pre-arm speed is "a snapshot of what the game was doing before
    #     this advance existed", and counting it would inflate the bound of a
    #     slow advance that happened to start while the clock was running.
    #
    #     So: leave the game running FAST, then advance at NORMAL. The bound must
    #     be Normal's 2, not Fast's 6.
    e = send("unpause", {"speed": "fast"})
    eq("3.2a", "the clock is running at Fast before the advance is armed",
       e, "data.speed", "Fast")
    st = send("status")
    if dig(st, "data.speed") != "Fast":
        note("3.2b", "the game left Fast before the advance was armed (speed=%s) — "
                     "something paused it, so this check is weaker than intended."
             % show(dig(st, "data.speed")))
    e = advance({"ticks": 180, "speed": "normal"})
    eq("3.2c", "the advance ran at Normal", e, "data.speed", "Normal")
    eq("3.2d", "the PRE-ARM Fast is excluded from the high-water mark", e,
       "data.overshoot_bound_speed", "Normal")
    eq("3.2e", "…so the bound is Normal's frame, not Fast's", e,
       "data.overshoot_bound", MAX_TICKS_PER_FRAME["Normal"])
    le_val("3.2f", "and the overshoot is inside it",
           dig(e, "data.overshoot"), dig(e, "data.overshoot_bound"))

    # 3.3 the invariant accept/4.2-play-loop.py keys on, in its own form: an
    #     until-advance with a timeout. `timeout_ticks` is not echoed by the
    #     envelope (0.8i), so it is taken from what we sent — exactly as 4.2
    #     takes it from the transcript's cmd.json.
    timeout = 2000
    e = advance({"until": {"letter": True}, "timeout_ticks": timeout,
                 "speed": "superfast"})
    one_of("3.3a", "the until-advance ended for a reason it names",
           dig(e, "data.reason"), ["timeout", "letter", "stalled", "dialog", "error"])
    shape("3.3b", "advance", e, "data.overshoot_bound", NUM)
    le_val("3.3c", "ticks_elapsed <= timeout_ticks + overshoot_bound (4.2's key)",
           dig(e, "data.ticks_elapsed"), timeout + (dig(e, "data.overshoot_bound") or 0))
    absent("3.3d", "an until-advance publishes no `overshoot` (Target < 0), which is "
                   "why 4.2 reads the BOUND", e, "data.overshoot")

    # 3.4 A SPEED CHANGE MID-FLIGHT. No verb can do this — DrainCommands answers
    #     `busy` to every main-thread verb except `pause` while an advance is in
    #     flight — and the thermal governor fires on its own schedule. So the
    #     only reproducible driver is a HUMAN on the speed keys, which is why
    #     this is bench-only and opt-in.
    if not ARGS.speed_change:
        note("3.4", "skipped: --speed-change not given. This is THE finding-K check. "
                    "Run `--phase 3 --speed-change`; the driver arms a long Ultrafast "
                    "advance and prints a countdown — press 3 (Superfast) in the bench "
                    "window while it runs. Nothing else on the bench can change the "
                    "speed mid-advance: DrainCommands answers `busy` to every "
                    "main-thread verb except `pause`.")
    else:
        print("  %s>>> arming a 20000-tick Ultrafast advance. PRESS `3` (Superfast) in "
              "the bench window in the next few seconds. <<<%s" % (YELLOW, OFF))
        e = advance({"ticks": 20000, "speed": "ultrafast"}, timeout=900)
        changes = as_list(dig(e, "data.speed_changes"))
        precondition("3.4", "a speed change was seen mid-advance",
                     ARGS.dry_run or len(changes) > 0,
                     "no `speed_changes` in the envelope — the key press did not land "
                     "inside the advance. Re-run --phase 3 --speed-change.")
        shape("3.4a", "advance", e, "data.speed_changes", list)
        shape("3.4b", "advance", e, "data.speed_changes.0.from")
        shape("3.4c", "advance", e, "data.speed_changes.0.to")
        shape("3.4d", "advance", e, "data.speed_changes.0.by")
        exit_speed = dig(e, "data.speed")
        fastest = dig(e, "data.overshoot_bound_speed")
        seen = [str(dig(e, "data.speed_changes.%d.from" % i)) for i in range(len(changes))]
        seen += [str(dig(e, "data.speed_changes.%d.to" % i)) for i in range(len(changes))]
        seen.append(str(exit_speed))
        want = max(seen, key=lambda s: SPEED_ORDER.index(s) if s in SPEED_ORDER else -1)
        eq_val("3.4e", "the bound's speed is the FASTEST the advance ran at, not the "
                       "one it exited at", fastest, want)
        eq("3.4f", "…and the bound is that speed's frame", e, "data.overshoot_bound",
           MAX_TICKS_PER_FRAME.get(want))
        if exit_speed != want:
            ge_val("3.4g", "the published bound is LARGER than the exit speed's frame — "
                           "the M1 case exactly (ran Ultrafast, exited Superfast, "
                           "published 24 against a measured 30)",
                   dig(e, "data.overshoot_bound"),
                   MAX_TICKS_PER_FRAME.get(exit_speed, 0) + 1)
        else:
            note("3.4g", "the advance exited at the fastest speed it ran at (%s), so the "
                         "step-DOWN half was not exercised. Press the key later next "
                         "time." % exit_speed)
        le_val("3.4h", "and the measured overshoot is still inside the bound",
               dig(e, "data.overshoot"), dig(e, "data.overshoot_bound"))

    send("pause")
    no_red_errors("3.5", "zero red errors across the time phase")


# ------------------------------------------------------------------- phase 4 --
# M1 FINDING I1 — `digest.site`. Shapes are phase 0's job (0.5a-0.5r8); this
# phase is the values, and the constancy the section claims for itself.

def phase4():
    banner("PHASE 4 — I1: `digest.site` says where the colony IS")

    e = S.get("digest0") or send("digest")
    site = dig(e, "data.site") or {}

    check("4.1a", "biome is a non-empty defName", isinstance(site.get("biome"), str)
          and len(site.get("biome") or "") > 0, "a defName", site.get("biome"))
    check("4.1b", "biome_label is a non-empty human label",
          isinstance(site.get("biome_label"), str) and len(site.get("biome_label") or "") > 0,
          "a label", site.get("biome_label"))
    check("4.1c", "the label is not just the defName echoed",
          site.get("biome_label") != site.get("biome")
          or site.get("biome") is None, "a real label", site.get("biome_label"))
    ge("4.1d", "tile is a world tile id", e, "data.site.tile", 0)
    check("4.1e", "avg_temp_c is a plausible annual average (-80..80 C)",
          is_num(site.get("avg_temp_c")) and -80 <= site["avg_temp_c"] <= 80,
          "-80..80", site.get("avg_temp_c"))
    check("4.1f", "rainfall is non-negative", is_num(site.get("rainfall"))
          and site["rainfall"] >= 0, ">= 0", site.get("rainfall"))
    check("4.1g", "elevation is a number", is_num(site.get("elevation")),
          "a number", site.get("elevation"))
    one_of("4.1h", "hilliness is one of Verse.Hilliness's names",
           site.get("hilliness"), HILLINESS)
    check("4.1i", "swampiness is a 0..1 unit", is_num(site.get("swampiness"))
          and 0 <= site["swampiness"] <= 1, "0..1", site.get("swampiness"))
    check("4.1j", "pollution is a 0..1 unit", is_num(site.get("pollution"))
          and 0 <= site["pollution"] <= 1, "0..1", site.get("pollution"))
    ms = site.get("map_size")
    check("4.1k", "map_size is [x, z], both a real map dimension",
          isinstance(ms, list) and len(ms) == 2 and all(is_num(v) and v >= 50 for v in ms),
          "[x,z] with both >= 50", ms)
    check("4.1l", "pocket_map is a bool", isinstance(site.get("pocket_map"), bool),
          "a bool", site.get("pocket_map"))

    # 4.2 the section claims to be constant for the life of the map — "a handful
    #     of plain field reads" — so a second digest must return the same object.
    #     A drift here means something in Site() is not the field read it says.
    e2 = send("digest")
    eq_val("4.2a", "site is byte-identical on a second read (it is field reads, not "
                   "a computation)", json.dumps(dig(e2, "data.site"), sort_keys=True),
           json.dumps(site, sort_keys=True))
    # And the key set again on THIS envelope, because 0.5q proved it on another.
    keys_exactly("4.2b", "…with the same exact key set", e2, "data.site", SITE_KEYS)
    for k in sorted(SITE_FORBIDDEN):
        if has_key(e2, "data.site." + k):
            check("4.2c", "no skipped lazy getter reappeared", False,
                  "no `%s`" % k, dig(e2, "data.site." + k))
            break
    else:
        check("4.2c", "no skipped lazy getter reappeared on the second read",
              True, "none of %s" % sorted(SITE_FORBIDDEN), "none")


# ------------------------------------------------------------------- phase 5 --
# M1 FINDING D — `threat-pardon`: a RECORDED decision not to fight.
#
# The four arg shapes, from ThreatPardonVerbs.cs:
#   {}                          -> list the set and the candidates
#   {ids:[…], reason:"…"}       -> pardon (reason REQUIRED)
#   {ids:[…], release:true}     -> release those
#   {release_all:true}          -> release everything

REASON = "acceptance suite: dormant, and the colony has no ranged weapons yet"


def phase5():
    banner("PHASE 5 — D: `threat-pardon`, the recorded decision not to fight")

    e = send("threat-pardon")
    cands = [c for c in as_list(dig(e, "data.candidates")) if isinstance(c, dict)]
    precondition("5.1", "at least one counted hostile to pardon",
                 ARGS.dry_run or len(cands) > 0,
                 "`threat-pardon {}` lists no candidates: this map has no spawned, "
                 "undowned, hostile pawn. Load the M1 colony (a dormant insect "
                 "cluster) or stage one with `dev:incident {def:\"RaidEnemy\"}` and "
                 "re-run. A raid will fight back; the insect cluster is the fixture "
                 "the finding came from.")
    if ARGS.dry_run:
        cands = [{"id": 1, "kind": "x", "pardoned": False, "dormant": True,
                  "reason": None, "lapsed": False}]
    hostiles0 = dig(e, "data.hostiles")
    held0 = dig(e, "data.pardons_held")
    print("  %s%d candidate(s), %s pardon(s) held%s" % (DIM, len(cands), held0, OFF))

    # 5.1 the listing carries the ids the verb cannot otherwise be given. Fog
    #     is why: `pawns {filter:"hostile"}` fog-filters and returned 0 for the
    #     whole M1 run, so the agent had no other route to these ids.
    for i, k in enumerate(CANDIDATE_KEYS):
        shape("5.1%s" % "abcdef"[i], "threat-pardon", e, "data.candidates.0." + k)
    ids = [c.get("id") for c in cands if is_num(c.get("id"))]
    eq_val("5.1g", "every candidate carries a numeric id", len(ids), len(cands))

    # 5.2 FOG DISCIPLINE — id, kind label and dormancy ONLY. The implementer
    #     resolved ids WITHOUT the fog filter precisely so fogged hostiles can be
    #     named, and paid for it by publishing nothing spatial. A position or a
    #     def here would be knowledge of unexplored ground the player-facing
    #     verbs refuse to give.
    for i, c in enumerate(cands[:5]):
        keys_exactly("5.2a%d" % (i + 1),
                     "candidate row %d publishes id/kind/dormancy only" % i,
                     {"row": c}, "row", CANDIDATE_KEYS)
    leaked = sorted({k for c in cands for k in CANDIDATE_FORBIDDEN if k in c})
    check("5.2b", "no candidate leaks a position or a def", not leaked,
          "none of %s" % CANDIDATE_FORBIDDEN, leaked)
    check("5.2c", "`kind` is a LABEL, not a defName (kindDef.label, e.g. "
                  "'megascarab')",
          all(isinstance(c.get("kind"), str) or c.get("kind") is None for c in cands),
          "a label string", [c.get("kind") for c in cands[:5]])

    # 5.3 THE GATE: `reason` is required on an add. A pardon with no stated
    #     reason is the silent exemption the verb exists to prevent.
    e = send("threat-pardon", {"ids": [ids[0]] if ids else [1]})
    eq("5.3a", "an add with no `reason` is REFUSED", e, "ok", False)
    eq("5.3b", "…as bad-args", e, "error.code", "bad-args")
    contains("5.3c", "…naming the missing arg", dig(e, "error.detail"), "'reason' is required")
    absent("5.3d", "a refused call has no data block at all", e, "data")
    e = send("threat-pardon", {"ids": [ids[0]] if ids else [1], "reason": "   "})
    eq("5.3e", "a whitespace-only reason is refused too", e, "ok", False)
    e = send("threat-pardon")
    eq_val("5.3f", "…and nothing was pardoned by either refusal",
           dig(e, "data.pardons_held"), held0)

    # 5.4 THE PARDON.
    target = ids[0] if ids else 1
    kind = cands[0].get("kind")
    e = send("threat-pardon", {"ids": [target], "reason": REASON})
    eq("5.4a", "the pardon was applied", e, "data.applied.0", target)
    eq("5.4b", "…and said so", e, "data.did", "pardoned 1")
    not_null("5.4c", "…and journaled", e, "data.action.journal_seq")
    seq = dig(e, "data.action.journal_seq")
    eq_val("5.4d", "`hostiles` is UNCHANGED — a pardon is a decision, not a filter",
           dig(e, "data.hostiles"), hostiles0)
    ge("5.4e", "hostiles_pardoned counts it", e, "data.hostiles_pardoned", 1)
    eq_val("5.4f", "unpardoned = hostiles - pardoned",
           dig(e, "data.hostiles_unpardoned"),
           (dig(e, "data.hostiles") or 0) - (dig(e, "data.hostiles_pardoned") or 0))
    eq_val("5.4g", "the set grew by one", dig(e, "data.pardons_held"), (held0 or 0) + 1)
    row = [c for c in as_list(dig(e, "data.candidates")) if isinstance(c, dict)
           and c.get("id") == target]
    check("5.4h", "the pardoned candidate now reads pardoned:true",
          bool(row) and row[0].get("pardoned") is True, "pardoned:true", row[:1])
    check("5.4i", "…and carries the stated reason back",
          bool(row) and row[0].get("reason") == REASON, REASON,
          row[0].get("reason") if row else None)

    # 5.5 THE JOURNAL ROW: an `action` row with the ids and the reason.
    jrow = journal_row(seq, ["action"])
    check("5.5a", "the pardon has an `action` row at its seq", jrow is not None,
          "a row at seq %s" % seq, jrow)
    eq("5.5b", "it is an `action` row, not a `dev` one — this is a player act",
       jrow, "type", "action")
    eq("5.5c", "for this verb", jrow, "payload.verb", "threat-pardon")
    eq("5.5d", "and this step", jrow, "payload.step", "pardon")
    shape("5.5e", "the journal row", jrow, "payload.ids", list)
    eq("5.5f", "carrying the id", jrow, "payload.ids.0", target)
    eq("5.5g", "…and THE REASON, which is the whole point of the verb",
       jrow, "payload.reason", REASON)
    shape("5.5h", "the journal row", jrow, "payload.refused_count", NUM)

    # 5.6 the digest agrees, and `hostiles` still means the total.
    e = send("digest")
    eq_val("5.6a", "digest.threats.hostiles is still the TOTAL, unchanged by the pardon",
           dig(e, "data.threats.hostiles"), hostiles0)
    ge("5.6b", "digest.threats.hostiles_pardoned counts it", e,
       "data.threats.hostiles_pardoned", 1)
    eq_val("5.6c", "digest.threats.hostiles_unpardoned = hostiles - pardoned",
           dig(e, "data.threats.hostiles_unpardoned"),
           (dig(e, "data.threats.hostiles") or 0)
           - (dig(e, "data.threats.hostiles_pardoned") or 0))
    p = send("threat-pardon")
    eq_val("5.6d", "…and the verb and the digest report the same three numbers",
           [dig(e, "data.threats.hostiles"), dig(e, "data.threats.hostiles_pardoned"),
            dig(e, "data.threats.hostiles_unpardoned")],
           [dig(p, "data.hostiles"), dig(p, "data.hostiles_pardoned"),
            dig(p, "data.hostiles_unpardoned")])

    # 5.7 THE OBSERVER NEVER PRUNES. ThreatPardonComponent.Pardoned is called
    #     from `digest` and deliberately does not remove a lapsed entry: an
    #     observer that pruned a scribed dictionary would be the exact
    #     mutation-on-read hazard WorldSafe exists to catalogue.
    for _ in range(3):
        send("digest")
    e = send("threat-pardon")
    eq_val("5.7a", "three digests later the set is untouched",
           dig(e, "data.pardons_held"), (held0 or 0) + 1)

    # 5.8 A LAPSE: a pardon whose subject wakes stops counting, is FLAGGED, and
    #     is NOT removed. Only the verb removes it. Needs a candidate with a
    #     dormancy predicate to wake — `dormant` is null when the pawn has none.
    dormant = [c for c in cands if c.get("dormant") is True]
    if not dormant or ARGS.dry_run:
        note("5.8", "no candidate with dormant:true, so the LAPSE half is not "
                    "exercised. It needs a dormant threat cluster (the M1 insect "
                    "hive); `dormant:null` means the pawn has no dormancy state to "
                    "read and its pardon can never lapse on its own.")
    else:
        t2 = dormant[0]["id"]
        send("threat-pardon", {"ids": [t2], "reason": REASON})
        e = send("dev:damage", {"thing": t2, "amount": 1})
        if dig(e, "ok") is not True:
            note("5.8", "dev:damage refused (%s) — the wake could not be staged."
                 % show(dig(e, "error.detail")))
        else:
            send("advance", {"ticks": 60, "speed": "normal"})
            e = send("threat-pardon")
            row = [c for c in as_list(dig(e, "data.candidates"))
                   if isinstance(c, dict) and c.get("id") == t2]
            check("5.8a", "the woken pawn reads dormant:false",
                  bool(row) and row[0].get("dormant") is False, "dormant:false", row[:1])
            check("5.8b", "…and its pardon is FLAGGED as lapsed",
                  bool(row) and row[0].get("lapsed") is True, "lapsed:true", row[:1])
            check("5.8c", "…and stopped counting as pardoned",
                  bool(row) and row[0].get("pardoned") is False, "pardoned:false", row[:1])
            ge("5.8d", "…but the ENTRY is still held: an observer may not prune it",
               e, "data.pardons_held", 2)
            send("threat-pardon", {"ids": [t2], "release": True})

    # 5.9 release, which is the only thing that removes an entry.
    e = send("threat-pardon", {"ids": [target], "release": True})
    eq("5.9a", "the release was applied", e, "data.applied.0", target)
    eq_val("5.9b", "the set shrank", dig(e, "data.pardons_held"), held0)
    not_null("5.9c", "a release journals too", e, "data.action.journal_seq")
    jrow = journal_row(dig(e, "data.action.journal_seq"), ["action"])
    eq("5.9d", "…as its own step", jrow, "payload.step", "release")
    eq("5.9e", "…with a null reason (a release states none)", jrow, "payload.reason", None)
    e = send("threat-pardon", {"ids": [target], "release": True})
    check("5.9f", "releasing something not held is refused, not silently accepted",
          any(isinstance(r, dict) and r.get("id") == target
              for r in as_list(dig(e, "data.refused"))),
          "a `refused` row for %s" % target, dig(e, "data.refused"))
    eq("5.9g", "…and nothing was journaled for it", e, "data.action.journal_seq", None)

    # 5.10 release_all on an empty set is a no-op that writes NO row: a journal
    #      line for a no-op dilutes the audit trail this verb exists to keep.
    e = send("threat-pardon", {"release_all": True})
    if (held0 or 0) == 0:
        eq("5.10a", "release_all on an empty set journals nothing",
           e, "data.action.journal_seq", None)
    eq("5.10b", "…and the set is empty afterwards", e, "data.pardons_held", 0)

    no_red_errors("5.11", "zero red errors across the pardon phase")


# ----------------------------------------------------------------- phases 6/7 --
# THE SAVE/LOAD ROUND TRIP. The set is scribed on a GameComponent by
# thingIDNumber (`Scribe_Collections.Look(ref pardons, "autoRimmerThreatPardons",
# LookMode.Value, LookMode.Value)`), so it must survive a save and a load — that
# is what makes a pardon auditable after the fact rather than a session's mood.
#
# There is NO save verb and NO load verb in the registry, so this is two halves
# with a human in between. `--phase 6`, save + load in the game, `--phase 7`.

def phase6():
    banner("PHASE 6 — D: the save half (a human saves and loads between 6 and 7)")

    e = send("threat-pardon")
    cands = [c for c in as_list(dig(e, "data.candidates")) if isinstance(c, dict)]
    precondition("6.1", "at least one counted hostile to pardon",
                 ARGS.dry_run or len(cands) > 0,
                 "no candidates — see phase 5's note on staging one.")
    ids = [c["id"] for c in cands if is_num(c.get("id"))][:3]
    e = send("threat-pardon", {"ids": ids, "reason": REASON})
    eq_val("6.1a", "every id was pardoned", len(as_list(dig(e, "data.applied"))), len(ids))
    ge("6.1b", "the set holds them", e, "data.pardons_held", len(ids))
    j = send("journal", {"limit": 1})
    state = {"ids": ids, "reason": REASON,
             "pardons_held": dig(e, "data.pardons_held"),
             "hostiles": dig(e, "data.hostiles"),
             "seq": dig(j, "data.last_seq") or 0,
             "root": ARGS.root}
    with open(ARGS.state, "w", encoding="utf-8") as fh:
        json.dump(state, fh)
    print("")
    print("  %sSTATE WRITTEN: %s%s" % (CYAN, ARGS.state, OFF))
    print("  %sNOW, IN THE GAME: save the colony, then load that save. Then run:%s"
          % (CYAN, OFF))
    print("  %s    %s --phase 7%s" % (CYAN, sys.argv[0], OFF))


def phase7():
    banner("PHASE 7 — D: the load half (did the scribed set survive?)")

    precondition("7.1", "phase 6's state file exists",
                 ARGS.dry_run or os.path.exists(ARGS.state),
                 "no %s — run `--phase 6` first, then save and load in the game."
                 % ARGS.state)
    if ARGS.dry_run:
        return
    with open(ARGS.state, encoding="utf-8") as fh:
        state = json.load(fh)

    # 7.1 PROVE THE RELOAD HAPPENED. AgentGameComponent.LoadedGame emits a
    #     `session` row with kind:"loaded"; without this check the phase would
    #     pass on a bench that never reloaded at all, which is the worst possible
    #     way for a persistence test to be green.
    e = send("journal", {"since_seq": state["seq"], "types": ["session"], "limit": 50})
    loaded = [r for r in as_list(dig(e, "data.events"))
              if isinstance(r, dict) and dig(r, "payload.kind") == "loaded"]
    precondition("7.1a", "a `session {kind:\"loaded\"}` row since phase 6",
                 len(loaded) > 0,
                 "no load boundary in the journal since seq %s. The colony was never "
                 "reloaded, so there is nothing to prove. Save AND load, then re-run."
                 % state["seq"])
    check("7.1b", "the game really was reloaded between the halves", True,
          "a session row", loaded[0])

    # 7.2 the set survived, by id and by reason.
    e = send("threat-pardon")
    eq_val("7.2a", "the same number of pardons is held after the load",
           dig(e, "data.pardons_held"), state["pardons_held"])
    rows = {c.get("id"): c for c in as_list(dig(e, "data.candidates"))
            if isinstance(c, dict)}
    for n, i in enumerate(state["ids"]):
        c = rows.get(i)
        check("7.2b%d" % (n + 1), "id %s is still pardoned after the round trip" % i,
              bool(c) and c.get("pardoned") is True, "pardoned:true", c)
        check("7.2c%d" % (n + 1), "…and its REASON survived (scribed by value)",
              bool(c) and c.get("reason") == state["reason"], state["reason"],
              c.get("reason") if c else None)
    eq_val("7.2d", "`hostiles` is unchanged across the round trip too",
           dig(e, "data.hostiles"), state["hostiles"])

    # 7.3 and the digest reads the reloaded set, not a stale one.
    d = send("digest")
    eq_val("7.3", "digest.threats.hostiles_pardoned agrees after the load",
           dig(d, "data.threats.hostiles_pardoned"), dig(e, "data.hostiles_pardoned"))

    send("threat-pardon", {"release_all": True})
    os.remove(ARGS.state)


# ------------------------------------------------------------------- phase 8 --

def phase8():
    banner("PHASE 8 — the whole run's standing invariants")

    no_red_errors("8.1", "ZERO red errors across the WHOLE run")

    # 8.2 K's two invariants over EVERY advance this run made, not only the one
    #     phase 3 inspected.
    adv = S.get("advances", [])
    bad_ladder = [a for a, e in adv if dig(e, "ok") is True and not ladder_ok(e)]
    check("8.2a", "every advance's bound is MaxTicksPerFrame(overshoot_bound_speed)",
          not bad_ladder, "all %d advances" % len(adv), bad_ladder)
    over = [(a, dig(e, "data.overshoot"), dig(e, "data.overshoot_bound"))
            for a, e in adv if has_key(e, "data.overshoot")]
    bad = [x for x in over if not (is_num(x[1]) and is_num(x[2]) and x[1] <= x[2])]
    check("8.2b", "…and every measured overshoot is inside its published bound",
          not bad, "overshoot <= overshoot_bound on all %d" % len(over), bad)
    budget = []
    for a, e in adv:
        cap = a.get("ticks", a.get("timeout_ticks"))
        if cap is None or not is_num(dig(e, "data.ticks_elapsed")):
            continue
        if dig(e, "data.ticks_elapsed") > cap + (dig(e, "data.overshoot_bound") or 0):
            budget.append((a, dig(e, "data.ticks_elapsed"), dig(e, "data.overshoot_bound")))
    check("8.2c", "…and ticks_elapsed <= (ticks|timeout_ticks) + overshoot_bound "
                  "(the invariant accept/4.2-play-loop.py keys on)",
          not budget, "no advance past its budget + bound", budget)

    # 8.3 PROVENANCE. Every player mutation this run made landed as an `action`
    #     row carrying {verb, step}; every dev mutation landed as a `dev` row.
    e = send("journal", {"since_seq": S["seq0"], "types": ["action"], "limit": 500})
    rows = [r for r in as_list(dig(e, "data.events")) if isinstance(r, dict)]
    shaped = [r for r in rows
              if dig(r, "payload.verb") is not None and dig(r, "payload.step") is not None]
    check("8.3a", "every action row carries {verb, step}",
          len(shaped) == len(rows), "all %d rows" % len(rows),
          "%d of %d" % (len(shaped), len(rows)))
    e = send("journal", {"since_seq": S["seq0"], "types": ["dev"], "limit": 500})
    devs = {str(dig(r, "payload.verb")) for r in as_list(dig(e, "data.events"))
            if isinstance(r, dict)}
    leaked = sorted(v for v in devs if v in ("threat-pardon", "research-set", "research"))
    check("8.3b", "no PLAYER verb journaled itself as a `dev` cheat row",
          not leaked, "none", leaked)
    if 1 in S.get("ran", []):
        check("8.3c", "…and phase 1's `dev:spawn-thing` calls journaled as `dev` rows",
              "dev:spawn-thing" in devs, "dev:spawn-thing among the dev verbs",
              sorted(devs))
    else:
        note("8.3c", "phase 1 was not run, so there is no dev:spawn-thing row to look "
                     "for. dev verbs seen: %s" % sorted(devs))

    # 8.4 the bench is left where a run expects it: paused, no modal.
    e = send("status")
    absent("8.4a", "no force-pausing modal was left behind", e, "data.forcePause")
    e = send("threat-pardon")
    eq("8.4b", "no pardon was left behind by this run", e, "data.pardons_held", 0)


# ------------------------------------------------------------------- phase 9 --
# THE SUITE'S OWN ASSERTION MACHINERY. No bench, no game, no protocol root.
#
# A suite that cannot fail on a renamed field is not a suite, and the only way
# to know this one can is to feed it a renamed field and watch it fail. probe()
# runs a real assertion with the accounting redirected, and returns its verdict
# as data; every check below asserts that verdict.

def probe(fn):
    global CAPTURE
    CAPTURE = []
    try:
        fn()
        got = list(CAPTURE)
    finally:
        CAPTURE = None
    return all(got) if got else False


# A canned `digest` envelope in the shipped shape (WorldSafe.Site, DigestVerb
# .ThreatSection), and its broken twin. Values are plausible, not real — what is
# being tested is this file's assertions, not the mod.
GOOD_DIGEST = {"ok": True, "op": "digest", "data": {"site": {
    "biome": "TemperateForest", "biome_label": "temperate forest", "tile": 4231,
    "avg_temp_c": 12.4, "rainfall": 1800, "elevation": 320, "hilliness": "SmallHills",
    "swampiness": 0.12, "pollution": 0.0, "map_size": [250, 250], "pocket_map": False},
    "threats": {"danger": "None", "hostiles": 6, "hostiles_pardoned": 6,
                "hostiles_unpardoned": 0, "kinds": ["megascarab x4"]}}}
GOOD_PARDON = {"ok": True, "op": "threat-pardon", "data": {
    "verb": "threat-pardon", "ok": True, "hostiles": 6, "hostiles_pardoned": 1,
    "hostiles_unpardoned": 5, "pardons_held": 1, "action": {"journal_seq": None},
    "note": "…", "candidates": [{"id": 42, "kind": "megascarab", "pardoned": True,
                                 "dormant": True, "reason": "why", "lapsed": False}]}}
GOOD_ADVANCE = {"ok": True, "op": "advance", "data": {
    "reason": "ticks", "ticks_elapsed": 192, "speed": "Superfast",
    "overshoot_bound": 30, "overshoot_bound_speed": "Ultrafast", "overshoot": 30,
    "max_ticks_in_frame": 30}}


def phase9():
    banner("PHASE 9 — the suite's OWN machinery (offline; proves it can FAIL)")

    # 9.1 shape() is the predicate dig() cannot be.
    check("9.1a", "shape() PASSES on a key that is present",
          probe(lambda: shape("x", "digest", GOOD_DIGEST, "data.site.biome")),
          "pass", "fail")
    check("9.1b", "shape() FAILS on a RENAMED key — the whole point of phase 0",
          not probe(lambda: shape("x", "digest", GOOD_DIGEST, "data.site.biome_defName")),
          "fail", "pass")
    check("9.1c", "shape() PASSES on a present-and-NULL key (absent != null)",
          probe(lambda: shape("x", "t", {"data": {"k": None}}, "data.k")),
          "pass", "fail")
    check("9.1d", "shape(kind=) FAILS when the type is wrong",
          not probe(lambda: shape("x", "t", GOOD_DIGEST, "data.site.tile", str)),
          "fail", "pass")
    check("9.1e", "shape() FAILS on a path through a missing parent",
          not probe(lambda: shape("x", "t", GOOD_DIGEST, "data.nope.biome")),
          "fail", "pass")

    # 9.2 THE TRAP ITSELF, demonstrated rather than described: eq(..., None)
    #     passes on an absent key. This is why phase 0 exists, and it is the
    #     reason every `== null` assertion in this file is preceded by a shape().
    check("9.2a", "eq(...,None) PASSES on an ABSENT key — the trap this suite is "
                  "built around",
          probe(lambda: eq("x", "t", GOOD_DIGEST, "data.site.no_such_field", None)),
          "pass (and that is the hazard)", "fail")
    check("9.2b", "…which is why shape() is asserted FIRST: it fails on the same path",
          not probe(lambda: shape("x", "t", GOOD_DIGEST, "data.site.no_such_field")),
          "fail", "pass")
    check("9.2c", "eq() FAILS on a wrong value",
          not probe(lambda: eq("x", "t", GOOD_DIGEST, "data.site.biome", "Desert")),
          "fail", "pass")

    # 9.3 absent() — the other half. 3.5's 4.8g read a key that never existed.
    check("9.3a", "absent() PASSES when the key really is absent",
          probe(lambda: absent("x", "t", GOOD_ADVANCE, "data.ticks")), "pass", "fail")
    check("9.3b", "absent() FAILS when the key is present",
          not probe(lambda: absent("x", "t", GOOD_ADVANCE, "data.ticks_elapsed")),
          "fail", "pass")
    check("9.3c", "absent() FAILS when the key is present and NULL",
          not probe(lambda: absent("x", "t", GOOD_PARDON, "data.action.journal_seq")),
          "fail", "pass")

    # 9.4 keys_exactly() — the guard on digest.site and on a candidate row.
    check("9.4a", "keys_exactly() PASSES on the shipped site field set",
          probe(lambda: keys_exactly("x", "t", GOOD_DIGEST, "data.site", SITE_KEYS)),
          "pass", "fail")
    extra = json.loads(json.dumps(GOOD_DIGEST))
    extra["data"]["site"]["max_temp_c"] = 31.0
    check("9.4b", "…and FAILS when a skipped lazy getter is reintroduced "
                  "(max_temp_c caches cachedMaxTemp)",
          not probe(lambda: keys_exactly("x", "t", extra, "data.site", SITE_KEYS)),
          "fail", "pass")
    check("9.4c", "…and the per-name absence check fails on the same envelope",
          not probe(lambda: absent("x", "t", extra, "data.site.max_temp_c")),
          "fail", "pass")
    leak = json.loads(json.dumps(GOOD_PARDON))
    leak["data"]["candidates"][0]["at"] = [120, 130]
    check("9.4d", "a candidate row that leaks a position FAILS the fog-discipline "
                  "key set",
          not probe(lambda: keys_exactly("x", "t", leak, "data.candidates.0",
                                         CANDIDATE_KEYS)),
          "fail", "pass")
    missing = json.loads(json.dumps(GOOD_PARDON))
    del missing["data"]["candidates"][0]["lapsed"]
    check("9.4e", "…and a row that DROPS a field fails it too",
          not probe(lambda: keys_exactly("x", "t", missing, "data.candidates.0",
                                         CANDIDATE_KEYS)),
          "fail", "pass")

    # 9.5 dig()/has_key() through list indices, which 5.1/5.2 rely on.
    eq_val("9.5a", "dig() indexes a list", dig(GOOD_PARDON, "data.candidates.0.id"), 42)
    eq_val("9.5b", "dig() returns the default past the end of a list",
           dig(GOOD_PARDON, "data.candidates.9.id", "gone"), "gone")
    check("9.5c", "has_key() is false past the end of a list",
          not has_key(GOOD_PARDON, "data.candidates.9"), "false", "true")
    check("9.5d", "has_key() is true for an index that exists",
          has_key(GOOD_PARDON, "data.candidates.0"), "true", "false")

    # 9.6 K's ladder identity, on the canned M1 envelope: bound 30 next to an
    #     exit speed of Superfast is the FIXED behaviour, and the pre-fix
    #     envelope (bound 24) must fail.
    check("9.6a", "ladder_ok() accepts a bound that matches its named speed",
          ladder_ok(GOOD_ADVANCE), "true", "false")
    stale = json.loads(json.dumps(GOOD_ADVANCE))
    stale["data"]["overshoot_bound"] = 24          # the pre-fix number
    check("9.6b", "…and rejects M1's actual envelope: bound 24 published against "
                  "overshoot_bound_speed Ultrafast",
          not ladder_ok(stale), "false", "true")
    check("9.6c", "…while the pre-fix pairing (bound from the EXIT speed) would have "
                  "left overshoot 30 > bound 24",
          stale["data"]["overshoot"] > stale["data"]["overshoot_bound"],
          "30 > 24", "no")

    # 9.7 THE LADDER TABLE ITSELF, re-parsed out of the shipped switch. A change
    #     to TimeDriver.MaxTicksPerFrame that this file does not follow makes
    #     phase 3 assert the wrong numbers, silently.
    src = os.path.join(REPO, "Source", "AutoRimmer", "TimeDriver.cs")
    if not os.path.exists(src):
        note("9.7", "Source/AutoRimmer/TimeDriver.cs not in this checkout — the ladder "
                    "table could not be re-derived from the shipped switch.")
    else:
        body = ""
        with open(src, encoding="utf-8") as fh:
            text = fh.read()
        m = re.search(r"MaxTicksPerFrame\(TimeSpeed s\)\s*\{(.*?)\n        \}",
                      text, re.S)
        if m:
            body = m.group(1)
        found = dict(re.findall(r"case TimeSpeed\.(\w+): return (\d+);", body))
        found = {k: int(v) for k, v in found.items()}
        found.setdefault("Paused", 0)
        eq_val("9.7", "this file's MAX_TICKS_PER_FRAME matches TimeDriver.cs's switch",
               found, MAX_TICKS_PER_FRAME)

    # 9.8 the site field list, re-derived from WorldSafe.Site the same way.
    src = os.path.join(REPO, "Source", "AutoRimmer", "WorldSafe.cs")
    if not os.path.exists(src):
        note("9.8", "Source/AutoRimmer/WorldSafe.cs not in this checkout — SITE_KEYS "
                    "could not be re-derived.")
    else:
        with open(src, encoding="utf-8") as fh:
            text = fh.read()
        m = re.search(r"public static Dictionary<string, object> Site\(Map map\)(.*?)\n        \}",
                      text, re.S)
        keys = re.findall(r'\["(\w+)"\] = ', m.group(1)) if m else []
        eq_val("9.8", "this file's SITE_KEYS matches WorldSafe.Site's dictionary",
               keys, SITE_KEYS)

    # 9.9 THE READ LOG'S ONE LOAD-BEARING INVARIANT (git-bug 7382bdd). The
    #     unknown-argument check is `supplied − queried`, and `queried` is
    #     marked by VerbArgs.Look(). An accessor added later that reads the
    #     backing dictionary DIRECTLY would not mark, so its key would look
    #     unread and a LEGITIMATE call would start being refused — the exact
    #     failure mode that made a 120-verb declaration unacceptable, sneaking
    #     back in through the mechanism that replaced it. It is checkable
    #     statically, so it is checked here rather than trusted.
    src = os.path.join(REPO, "Source", "AutoRimmer", "VerbRegistry.cs")
    if not os.path.exists(src):
        note("9.9", "Source/AutoRimmer/VerbRegistry.cs not in this checkout — the "
                    "read log's marking invariant could not be re-derived.")
    else:
        with open(src, encoding="utf-8") as fh:
            text = fh.read()
        m = re.search(r"public sealed class VerbArgs\b(.*?)\n    public static class VerbRegistry",
                      text, re.S)
        body = m.group(1) if m else ""
        # Two touches of the backing dict are legitimate and they are matched
        # WITH THEIR ARGUMENTS, not merely by member name: TryGetValue is the
        # one inside Look() itself, and ContainsKey is NearMiss probing an
        # ALIAS — which must NOT be marked, or a stray key would be masked by
        # the very check that names it. A third touch, or either of these with
        # a different argument, means an accessor is reading around Look().
        direct = re.findall(r"\braw\.\w+\([^)]*\)", body)
        eq_val("9.9a", "every VerbArgs accessor reaches the backing dict only via "
                       "Look(), so every read is marked",
               direct, ["raw.TryGetValue(key, out v)", "raw.ContainsKey(aliases[i])"])
        check("9.9b", "…and Look() is what marks it",
              re.search(r"private bool Look\(string key, out object v\)\s*\{[^}]*queried\.Add\(key\)",
                        body) is not None,
              "queried.Add inside Look()", "not found")

    # 9.10 THE THREE PER-SITE ARG LISTS, re-derived. `RefuseStray` names the
    #      arguments a verb accepts so the refusal can print them, and those
    #      three lists are the only hand-written argument declarations in the
    #      tree. A list that drifts costs a WORSE MESSAGE and never a refused
    #      legitimate call — the detection is the read log, which consults
    #      none of them — but "it only degrades the message" is a claim worth
    #      keeping true rather than asserting once.
    for num, fname, listname, verbmark in (
            ("9.10a", "JournalVerbs.cs", "SelftestArgs", '[Verb("journal-selftest")]'),
            ("9.10b", "PawnFixtureVerbs.cs", "FixtureArgs", None),
            ("9.10c", "WorldFixtureVerbs.cs", "FixtureArgs", None)):
        src = os.path.join(REPO, "Source", "AutoRimmer", fname)
        if not os.path.exists(src):
            note(num, "Source/AutoRimmer/%s not in this checkout." % fname)
            continue
        with open(src, encoding="utf-8") as fh:
            text = fh.read()
        m = re.search(r"string\[\]\s+" + listname + r"\s*=\s*\{(.*?)\};", text, re.S)
        declared = sorted(set(re.findall(r'"(\w+)"', m.group(1)))) if m else []
        # The keys the verb actually reads. JournalVerbs holds a second verb
        # (`journal`) ahead of the fixture, so scan from its marker down.
        scope = text
        if verbmark:
            scope = text[text.index(verbmark):] if verbmark in text else text
        read = sorted(set(re.findall(
            r"\.(?:Has|Raw|Str|StrReq|Bool|Num|NumReq|Int|IntReq|Long|StrList)"
            r'\("(\w+)"', scope)))
        eq_val(num, "%s's %s matches every argument the verb reads"
               % (fname, listname), declared, read)


# ---------------------------------------------------------------------- main --

PHASES = {1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5,
          6: phase6, 7: phase7, 8: phase8, 9: phase9}
DEFAULT_PHASES = [1, 2, 3, 4, 5, 8, 9]   # 6/7 are the save/load halves: opt-in
BENCH_PHASES = [1, 2, 3, 4, 5, 6, 7, 8]  # need a real game; 9 never does


def main():
    global ARGS
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--root", default=DEFAULT_ROOT, help="protocol root")
    ap.add_argument("--phase", type=int, action="append", choices=sorted(PHASES),
                    help="run only these phases (repeatable); 0 always runs")
    ap.add_argument("--selftest", action="store_true",
                    help="phase 9 only: no bench, no protocol root, no game")
    ap.add_argument("--dry-run", action="store_true", help="print the plan, send nothing")
    ap.add_argument("--echo", action="store_true", help="print every result envelope")
    ap.add_argument("--pos", help="x,z — a free floor cell for phase 1 to stage on")
    ap.add_argument("--near-cell", help="x,z — a cell >=13 cells inside rock or deep "
                                        "water, for phase 1's mode:\"near\" arm")
    ap.add_argument("--speed-change", action="store_true",
                    help="phase 3: arm a long Ultrafast advance and wait for a HUMAN "
                         "to press a slower speed key in the bench window")
    ap.add_argument("--bench-project", help="a research project that requires a bench "
                                            "(default MoisturePump)")
    ap.add_argument("--bench-prereq", help="the project to finish first "
                                           "(default MicroelectronicsBasics)")
    ap.add_argument("--state", default=DEFAULT_STATE,
                    help="where phase 6 leaves what phase 7 reads")
    ARGS = ap.parse_args()

    print("AutoRimmer session-13 acceptance — the five M1 mod findings")
    print("  A  dev:spawn-thing refusals reach the journal row  (DevVerbs.cs, Blockers.cs)")
    print("  J  bench_ok is a bench, not a gate                 (ResearchVerbs.cs, ColonyVerbs.cs)")
    print("  K  overshoot_bound from the fastest speed          (TimeDriver.cs)")
    print("  I1 digest.site                                     (WorldSafe.cs, DigestVerb.cs)")
    print("  D  threat-pardon                                   (ThreatPardonVerbs.cs, DigestVerb.cs)")

    if ARGS.selftest:
        print("selftest: phase 9 only — no bench is contacted.")
        phase9()
        return report(selftest=True)

    print("protocol root: %s" % ARGS.root)
    if not ARGS.dry_run and not os.path.exists(os.path.join(ARGS.root, "status.json")):
        print("%sno status.json under that root — start the bench with "
              "_RimWorld-Agent/run-agent.sh, or pass --root%s" % (RED, OFF))
        sys.exit(2)
    if not ARGS.dry_run:
        os.makedirs(os.path.join(ARGS.root, "commands"), exist_ok=True)

    wanted = sorted(set(ARGS.phase)) if ARGS.phase else DEFAULT_PHASES
    print("phases: 0 + %s" % ", ".join(str(p) for p in wanted))

    phase0()
    S["ran"] = wanted
    for p in wanted:
        if S.get("synthetic") and p in BENCH_PHASES:
            banner("PHASE %d — SKIPPED on a synthetic bench" % p)
            note("%d.0" % p, "fakebench emulates Poller.cs, not the game: it does not "
                             "register this phase's verbs and its canned payloads have "
                             "none of the fields under test. Nothing here was asserted.")
            continue
        PHASES[p]()
    report()


def report(selftest=False):
    print("")
    print("=" * 78)
    if ARGS.dry_run:
        # A dry-run SENDS NOTHING, so every expectation above was printed and no
        # expectation was evaluated. Saying "passed" here is the same
        # green-while-asserting-nothing failure phase 0 exists to prevent, one
        # level up.
        print("%sRESULT: --dry-run printed %d expectations and asserted NONE of them. "
              "Nothing was sent; no dig path was proved. Run it live.%s"
              % (YELLOW, CHECKS, OFF))
        sys.exit(0)
    if FAILS:
        print("%sRESULT: %d FAILED of %d checks — %s%s"
              % (RED, len(FAILS), CHECKS, ", ".join(FAILS), OFF))
        sys.exit(1)
    if selftest:
        print("%sRESULT: all %d checks passed — the ASSERTIONS work. Nothing about the "
              "mod was asserted; take this to a bench.%s" % (GREEN, CHECKS, OFF))
        sys.exit(0)
    if S.get("synthetic"):
        print("%sRESULT: all %d checks passed, but this was a SYNTHETIC bench: the "
              "protocol plumbing and this file's own assertions were proved and NOT ONE "
              "session-13 field was read. Run it against _RimWorld-Agent.%s"
              % (YELLOW, CHECKS, OFF))
        sys.exit(0)
    print("%sRESULT: all %d checks passed%s" % (GREEN, CHECKS, OFF))
    sys.exit(0)


if __name__ == "__main__":
    main()
