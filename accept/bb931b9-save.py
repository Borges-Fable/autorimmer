#!/usr/bin/env python3
"""Acceptance runner for bb931b9 — an unattended run can checkpoint before a raid.

    ./accept/bb931b9-save.py                 # everything safe
    ./accept/bb931b9-save.py --phase 2       # one phase (0 always runs)
    ./accept/bb931b9-save.py --dry-run       # print the plan, send nothing
    ./accept/bb931b9-save.py --selftest      # offline; no bench, nothing sent

Same protocol, helpers and exit codes as `accept/8b0b88f-already-designated.py`
and `accept/eef837a-bill-filter.py`; read either header first.

Start the bench (`_RimWorld-Agent/run-agent.sh`), load a colony, leave it
**PAUSED** — phase 0 makes that a precondition, and phase 4 unpauses
deliberately and pauses again.

THE BENCH ACCEPTANCE THE ISSUE ASKS FOR, in one line: *a `save` verb writes a
named save and returns its path and tick.* That is phase 1.

WHAT ELSE IS BEING PROVED, and why each needs its own phase:
  * **the autosave rotation is untouched** — the verb REFUSES a name the game's
    own `SaveGameFilesUtility.IsAutoSave` would classify as an autosave, and
    phase 3 asserts both the refusal AND that the five `Autosave-N` files are
    byte-for-byte where they were. `RimWorld/Autosaver.NewAutosaveFileName`
    rotates over exactly those names, so writing one would hand a slot a file
    the rotation later eats.
  * **a duplicate name is refused, `overwrite:true` accepted** — vanilla's
    dialog overwrites silently because the file list in front of the player IS
    the confirmation, and a program has no such list.
  * **the returned tick is the game's tick** — asserted against
    `digest.time.tick`, read from a DIFFERENT verb, because a claim about state
    is never read out of the envelope that claims to have made it.
  * **it journals as an `action`** — a save is a real side effect on disk and
    the transcript must show when the run took one.

THIS SUITE WRITES FILES INTO THE BENCH'S `Saves/` and does not delete them:
this driver has no filesystem verb and will not shell out to `rm` against a
directory the game owns. Every name it writes is prefixed `accbb931b9-`, so
they are trivially identifiable and trivially removed by hand. It writes at most
three.

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
DECOMP_CANDIDATES = [
    os.environ.get("RIMWORLD_DECOMP"),
    "/home/dorian/projects/rimworld-tools/Info/decompiled/RimWorldBase",
    os.path.join(VAULT, "..", "misc/rimworld/reference/decompiled/RimWorldBase"),
]

GREEN, RED, YELLOW, CYAN, DIM, OFF = (
    "\033[32m", "\033[31m", "\033[33m", "\033[36m", "\033[2m", "\033[0m",
)
if not sys.stdout.isatty():
    GREEN = RED = YELLOW = CYAN = DIM = OFF = ""

ARGS = None
FAILS = []
CHECKS = 0
CAPTURE = None
S = {}
SEQ = 0

# Every name this suite writes. Prefixed so a human can find and remove them.
PREFIX = "accbb931b9"
NAME_OK = PREFIX + "-one"
NAME_DUP = PREFIX + "-dup"
# Illegal per `Verse/GenText.IsValidFilename`: `/` is in
# `GetInvalidFilenameCharacters()`, which is also what makes `../` impossible.
NAME_BAD = "../" + PREFIX + "-escape"
NAME_LONG = "x" * 41            # GenText.IsValidFilename caps at 40
NAME_AUTOSAVE = "Autosave-3"    # SaveGameFilesUtility.IsAutoSave: prefix test
MAX_NAME = 40                   # SaveVerbs.MaxNameLength

SAVE_KEYS = {"verb", "name", "path", "tick", "sid", "written", "bytes",
             "overwrote", "bytes_before", "gate", "call", "note",
             "autosave_slots", "action"}
SLOT_KEYS = {"name", "exists", "bytes"}


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=300):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "accbb931b9-%03d-%s" % (SEQ, slug)
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
    """dig() cannot tell ABSENT from PRESENT-AND-NULL, and this suite cares:
    `bytes_before` is present and NULL on a fresh save (there was no previous
    file) and an integer on an overwrite. `eq(..., None)` would pass just as
    happily if the key had been dropped from the serializer."""
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


def num(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


# ------------------------------------------------------------------- asserts --

def check(n, what, ok, expected, actual):
    global CHECKS
    CHECKS += 1
    if CAPTURE is not None:
        CAPTURE.append(bool(ok))
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


def eq_int(n, what, got, want):
    """Both sides must be integers before the comparison counts — otherwise a
    vanished key makes `want` None, `got` None, and the check passes while
    asserting nothing."""
    ok = num(got) and num(want) and got == want
    check(n, what, ok, "the integer %s" % show(want), got)


def ge(n, what, env, path, want):
    got = dig(env, path)
    ok = num(got) and got >= want
    check(n, "%s (%s)" % (what, path), ok, ">= %s" % want, got)


def ge_val(n, what, got, want):
    check(n, what, num(got) and got >= want, ">= %s" % want, got)


def contains(n, what, env, path, needle):
    got = dig(env, path)
    ok = isinstance(got, str) and needle in got
    check(n, "%s (%s)" % (what, path), ok, "a string containing %r" % needle, got)


def nonempty(n, what, env, path):
    got = dig(env, path)
    ok = isinstance(got, str) and got.strip() != ""
    check(n, "%s (%s)" % (what, path), ok, "a non-empty string", got)


def null_at(n, what, env, path):
    present = has_key(env, path)
    got = dig(env, path)
    check(n, "%s (%s)" % (what, path), present and got is None,
          "the key PRESENT and null",
          "ABSENT — the serializer dropped it" if not present else got)


def shape(n, verb, env, path, kind=None):
    ok = has_key(env, path)
    want = "the key to be present"
    if ok and kind is not None:
        ok = isinstance(dig(env, path), kind)
        want += " and a %s" % kind.__name__
    check(n, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def keys_at_least(n, what, obj, want):
    if not isinstance(obj, dict):
        check(n, what, False, "a dict", obj)
        return
    missing = sorted(set(want) - set(obj))
    check(n, what, not missing, "at least %s" % sorted(want), {"missing": missing})


def bad_args(n, what, env, needle=None):
    """A REFUSAL, not a failure. `VerbArgsException` becomes an `ok:false`
    envelope with code `bad-args` (Runtime.Err.BadArgs), and a refusal that
    arrives as an EXCEPTION instead would be a different defect wearing the
    same colour — so the code is asserted, not just `ok:false`."""
    ok = dig(env, "ok") is False and dig(env, "error.code") == "bad-args"
    if ok and needle:
        ok = needle in str(dig(env, "error.detail", ""))
    check(n, what, ok,
          "ok:false, code bad-args%s" % (", detail naming %r" % needle if needle else ""),
          {"ok": dig(env, "ok"), "error": dig(env, "error")})


def note(n, text):
    if CAPTURE is not None:
        return
    print("  %s%-7s NOTE    %s%s" % (YELLOW, n, text, OFF))


def precondition(n, what, ok, detail):
    if ARGS.dry_run:
        print("  %s%-7s NEEDS   %s%s" % (CYAN, n, what, OFF))
        return
    if ok:
        print("  %s%-7s OK      precondition: %s%s" % (DIM, n, what, OFF))
        return
    print("  %s%-7s SKIP    precondition NOT MET: %s%s" % (YELLOW, n, what, OFF))
    print("          %s" % detail)
    print("          This is a FIXTURE gap, not a failure of bb931b9.")
    sys.exit(2)


def soft_skip(n, what, detail):
    print("  %s%-7s SKIP    %s%s" % (YELLOW, n, what, OFF))
    print("          %s" % detail)


def banner(t):
    if CAPTURE is not None:
        return
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


def slots(env):
    """The autosave rotation as the envelope reports it, as {name: bytes}."""
    return {r.get("name"): r.get("bytes")
            for r in rows(env, "data.autosave_slots.slots") if r.get("name")}


def game_tick():
    """The tick from a DIFFERENT verb. A claim about state is never read out of
    the envelope that claims to have made it."""
    return dig(send("digest", {}), "data.time.tick")


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 - the shape contract")

    e = send("status", {})
    precondition("0.1", "the bench answers `status`",
                 dig(e, "ok") is True or ARGS.dry_run,
                 "status returned %s" % show(dig(e, "error")))
    ops = [str(o) for o in as_list(dig(e, "data.verbs"))]
    check("0.2", "`save` is a registered verb — the whole issue is that it was not",
          "save" in ops or ARGS.dry_run, "save in status.verbs",
          [o for o in ops if "sav" in o.lower()])
    precondition("0.3", "`save` is registered", "save" in ops or ARGS.dry_run,
                 "`rwa verbs` still lists no save op")
    missing = [o for o in ["digest", "journal", "pause"] if o not in ops]
    check("0.4", "…and the verbs this suite reads back with are registered",
          not missing or ARGS.dry_run, "no missing ops", {"missing": missing})

    send("pause", {})
    S["watermark"] = dig(send("journal", {"limit": 1}), "data.seq", 0) or 0
    S["sid"] = dig(send("version", {}), "data.sid")

    # There is no load verb, and there must not be one. The asymmetry is the
    # design (PLAY-LOOP position 6), so it is asserted rather than assumed.
    banned = [o for o in ops if o in ("load", "load-game", "loadgame")]
    check("0.5", "there is deliberately NO load verb — loading is the launcher's job",
          not banned, "no load op registered", banned)


# --------------------------------------------------- 1: the acceptance line --

def phase1():
    banner("PHASE 1 - THE BENCH LINE: `save` writes a named save and returns path and tick")

    before_tick = game_tick()
    e = send("save", {"name": NAME_OK, "overwrite": True})
    check("1.1", "the call succeeds", dig(e, "ok") is True or ARGS.dry_run,
          "ok:true", dig(e, "error"))
    if not shape("1.2", "save", e, "data", dict):
        return
    keys_at_least("1.3", "the envelope publishes its whole documented field set",
                  dig(e, "data"), SAVE_KEYS)

    shape("1.4", "save", e, "data.path", str)
    contains("1.5", "…and it is under the bench's Saves/ directory", e, "data.path", "Saves")
    contains("1.6", "…named for what was asked, with the game's extension",
             e, "data.path", NAME_OK + ".rws")
    eq("1.7", "…and the name is echoed exactly, not sanitised into something else",
       e, "data.name", NAME_OK)

    shape("1.8", "save", e, "data.tick", int)
    # THE TICK, against an independent read. `digest.time.tick` cannot move
    # between the two calls because the game is paused, which is what phase 0's
    # pause is for.
    after_tick = game_tick()
    check("1.9", "the returned tick IS the game's tick (paused, read from `digest` "
                 "before and after)",
          num(dig(e, "data.tick")) and num(before_tick) and num(after_tick)
          and before_tick == after_tick == dig(e, "data.tick"),
          "digest.time.tick (%s) on both sides" % show(before_tick), dig(e, "data.tick"))

    eq("1.10", "the file actually landed", e, "data.written", True)
    ge("1.11", "…with real content in it", e, "data.bytes", 1024)
    eq_val("1.12", "…and the sid is this session's, from `version`",
           dig(e, "data.sid"), S.get("sid"))
    nonempty("1.13", "the verb cites the widget gate it reproduces", e, "data.gate")
    contains("1.14", "…which is the ESC menu's Save option", e, "data.gate",
             "MainMenuDrawer.MainMenuOnGUI")
    eq("1.15", "…and names the vanilla call it makes", e, "data.call",
       "Verse/GameDataSaveLoader.SaveGame")
    contains("1.16", "…and states the asymmetry in the envelope, not only in a comment",
             e, "data.note", "only the LAUNCHER may load")

    # A FRESH name has no previous file, and that is a present-null, not an
    # absent key or a zero.
    S["first"] = e


# ---------------------------------------- 2: duplicate refused, overwrite ok --

def phase2():
    banner("PHASE 2 - bb931b9 item 2b: a duplicate name is REFUSED, overwrite:true is not")

    # A name that does not exist yet, so the first write can assert the
    # fresh-file shape properly.
    e = send("save", {"name": NAME_DUP})
    if dig(e, "ok") is False and "already exists" in str(dig(e, "error.detail", "")):
        note("2.0", "%s already existed from an earlier run of this suite; the fresh-file "
                    "assertions below are skipped and the refusal is asserted instead"
             % NAME_DUP)
        bad_args("2.1", "…and the refusal names the collision", e, "already exists")
    else:
        check("2.1", "the first write of a new name succeeds",
              dig(e, "ok") is True or ARGS.dry_run, "ok:true", dig(e, "error"))
        eq("2.2", "…and reports that it did not overwrite anything",
           e, "data.overwrote", False)
        null_at("2.3", "…so bytes_before is PRESENT AND NULL, not 0 and not absent",
                e, "data.bytes_before")

    # THE REFUSAL.
    e = send("save", {"name": NAME_DUP})
    bad_args("2.4", "a second save under the same name is REFUSED", e, "already exists")
    contains("2.5", "…and the refusal names the argument that overrides it",
             e, "error.detail", "overwrite:true")
    contains("2.6", "…and says WHY vanilla differs, so this does not read as a bug",
             e, "error.detail", "overwrites")

    # THE OVERRIDE.
    e = send("save", {"name": NAME_DUP, "overwrite": True})
    check("2.7", "overwrite:true is accepted", dig(e, "ok") is True or ARGS.dry_run,
          "ok:true", dig(e, "error"))
    eq("2.8", "…and the envelope says it replaced a file", e, "data.overwrote", True)
    ge("2.9", "…reporting the previous size, so a caller has the same evidence the "
              "verb had", e, "data.bytes_before", 1)
    eq("2.10", "…and the new file is there", e, "data.written", True)


# ------------------------------------------- 3: the rotation is not consumed --

def phase3():
    banner("PHASE 3 - bb931b9 item 2a: the autosave rotation is not touched")

    e = send("save", {"name": NAME_OK, "overwrite": True})
    before = slots(e)
    shape("3.1", "save", e, "data.autosave_slots", dict)
    shape("3.2", "save", e, "data.autosave_slots.count", int)
    contains("3.3", "…citing where the slot names come from",
             e, "data.autosave_slots.source", "Autosaver.AutoSaveNames")
    rs = rows(e, "data.autosave_slots.slots")
    if rs:
        keys_at_least("3.4", "each slot row publishes its documented fields", rs[0], SLOT_KEYS)
        check("3.5", "…and every slot name is one the rotation owns",
              all(str(r.get("name", "")).startswith("Autosave-") for r in rs),
              "every name Autosave-N", [r.get("name") for r in rs])
    else:
        note("3.4", "no autosave slots reported (Prefs.AutosavesCount is 0?) — the "
                    "byte-for-byte comparison below proves nothing on this bench")

    # THE REFUSAL that makes consuming a slot impossible.
    e = send("save", {"name": NAME_AUTOSAVE})
    bad_args("3.6", "a name the game would classify as an autosave is REFUSED",
             e, "IsAutoSave")
    contains("3.7", "…citing the rotation that would otherwise eat it",
             e, "error.detail", "Autosaver")

    # AND THE SLOTS DID NOT MOVE. Read from a fresh save's own report, which is
    # a stat of each Autosave-N file at that moment.
    e = send("save", {"name": NAME_OK, "overwrite": True})
    after = slots(e)
    check("3.8", "every Autosave-N file is byte-for-byte where it was across three "
                 "saves and a refusal",
          before == after or ARGS.dry_run, show(before), after)


# ---------------------------------------- 4: it journals, and it refuses busy --

def phase4():
    banner("PHASE 4 - bb931b9 items 1 and 3: it journals as an `action`, and busy is free")

    e = send("save", {"name": NAME_OK, "overwrite": True})
    seq = dig(e, "data.action.journal_seq")
    shape("4.1", "save", e, "data.action", dict)
    ge_val("4.2", "…with a real journal seq, because a mutation with no journal line "
                  "cannot be traced", seq, 1)
    if not num(seq) or seq == 0:
        return note("4.3", "no journal seq — the row assertions were NOT run")

    j = send("journal", {"since_seq": seq - 1, "types": ["action"], "limit": 5})
    row = None
    for ev in rows(j, "data.events"):
        if ev.get("seq") == seq:
            row = ev
    check("4.3", "the journal row exists at the seq the envelope named",
          isinstance(row, dict), "an action row at seq %s" % show(seq), None)
    if isinstance(row, dict):
        eq_val("4.4", "…typed `action`, like every other player mutation — a save is a "
                      "real side effect on disk", row.get("type"), "action")
        eq_val("4.5", "…naming the verb", dig(row, "payload.verb"), "save")
        eq_val("4.6", "…and the save it wrote", dig(row, "payload.target"), NAME_OK)
        shape("4.7", "journal", {"r": row}, "r.payload.path")
        shape("4.8", "journal", {"r": row}, "r.payload.tick")
        check("4.9", "…and NOT flagged as a cheat, because a player verb is not one",
              "cheat" not in (dig(row, "payload") or {}),
              "no `cheat` key on the payload", dig(row, "payload"))

    # BUSY. `AgentGameComponent.DrainCommands` answers Err.Busy to every
    # main-thread verb except `pause` while an advance is in flight, so this
    # needs no code in the verb — but "needs no code" is a claim, and an
    # unasserted claim about a refusal path is how refusal paths rot.
    if ARGS.dry_run:
        return note("4.10", "--dry-run cannot race an advance")
    send("unpause", {})
    # Fire an advance we do NOT wait for, then hit `save` while it is in
    # flight. The advance is short so the suite is not left running the colony.
    adv_id = "accbb931b9-busy-advance"
    inbox = os.path.join(ARGS.root, "commands", adv_id + ".json")
    with open(inbox, "w", encoding="utf-8", newline="") as fh:
        fh.write(json.dumps({"id": adv_id, "op": "advance", "args": {"ticks": 2000}},
                            separators=(",", ":")))
    time.sleep(0.35)
    e = send("save", {"name": NAME_OK, "overwrite": True}, timeout=60)
    busy = dig(e, "error.code") == "busy"
    check("4.10", "`save` answers `busy` while an advance is in flight, which is the "
                  "existing convention and needs no code in the verb",
          busy or dig(e, "ok") is True,
          "code busy (or ok:true if the advance had already finished — the race is "
          "the fixture's, not the verb's)",
          {"ok": dig(e, "ok"), "error": dig(e, "error")})
    if not busy:
        note("4.11", "the advance finished before `save` was drained, so the busy path "
                     "was NOT exercised. That is a fixture race, not a result.")
    # Let the advance land, then put the colony back where we found it.
    deadline = time.time() + 120
    res = os.path.join(ARGS.root, "results", adv_id + ".json")
    while time.time() < deadline and not os.path.exists(res):
        time.sleep(0.2)
    send("pause", {})


# --------------------------------------------- 5: names, and no red errors --

def phase5():
    banner("PHASE 5 - the name gate, and the standing invariant")

    e = send("save", {"name": ""})
    bad_args("5.1", "an empty name is refused", e, "empty")
    e = send("save", {"name": NAME_BAD})
    bad_args("5.2", "a name with a path separator is refused — which is also what makes "
                    "`../escape` impossible", e, "IsValidFilename")
    e = send("save", {"name": NAME_LONG})
    bad_args("5.3", "a name past GenText.IsValidFilename's %d-character cap is refused"
             % MAX_NAME, e, str(MAX_NAME))
    contains("5.4", "…and the refusal says it REFUSES rather than sanitising, and why",
             e, "error.detail", "REFUSED rather than sanitised")

    e = send("journal", {"since_seq": S.get("watermark", 0), "types": ["red_error"],
                         "limit": 50})
    shape("5.5", "journal", e, "data.count")
    eq("5.6", "no red error was authored during this suite — a failed save would have "
              "logged one AND popped a forcePause dialog", e, "data.count", 0)
    for ev in rows(e, "data.events")[:5]:
        note("5.7", show(dig(ev, "payload")))


# --------------------------------------------------------------- 6: offline --

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


def _read(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as fh:
            return fh.read()
    except OSError:
        return None


def _first(paths):
    for p in paths:
        if p and os.path.isdir(p):
            return p
    return None


def phase6():
    banner("PHASE 6 - offline: the evidence behind every claim, and the helpers")

    src = _read(os.path.join(REPO, "Source", "AutoRimmer", "SaveVerbs.cs")) or ""
    check("6.1", "SaveVerbs.cs ships", len(src) > 0, "the file", len(src))
    check("6.2", "it makes the vanilla call and only that",
          "GameDataSaveLoader.SaveGame(trimmed)" in src, "the one-line call", None)
    # The header EXPLAINS why LongEventHandler is not used, so a bare substring
    # test fails on the comment that documents the decision. Comment lines are
    # stripped first — this is a claim about the code, not about the prose.
    code = "\n".join(l for l in src.splitlines() if not l.lstrip().startswith("//"))
    check("6.3", "…SYNCHRONOUSLY, not through LongEventHandler, because the verb must "
                 "return the tick the snapshot captured",
          "LongEventHandler" not in code, "no LongEventHandler call in the code", None)
    check("6.4", "…reproducing all three of the ESC menu's clauses",
          "ProgramState.Playing" in src and "SavingIsTemporarilyDisabled" in src
          and "permadeathMode" in src, "all three gates", None)
    check("6.5", "…refusing an autosave-classified name through the GAME's own classifier",
          "SaveGameFilesUtility.IsAutoSave" in src, "the IsAutoSave call", None)
    check("6.6", "…and an existing name through the game's own existence test",
          "SaveGameFilesUtility.SavedGameNamedExists" in src, "the exists call", None)
    check("6.7", "…validating rather than sanitising the name",
          "GenText.IsValidFilename" in src and "SanitizedFileName" not in src.split("//")[0],
          "IsValidFilename, and no silent sanitiser on the write path", None)
    check("6.8", "…and it journals as an `action`, because a save is a real side effect",
          'Journal.Emit("action"' in src, "the action emit", None)
    check("6.9", "…publishing the forcePause payload, because a FAILED save wedges every "
                 "later advance and the two facts belong in one envelope",
          "ForcePausePayload" in src, "the forcePause read", None)
    check("6.10", "…and there is no load verb anywhere in the mod",
          not re.search(r'\[Verb\("load', _all_sources()), "no [Verb(\"load…\")]", None)

    decomp = _first(DECOMP_CANDIDATES)
    if decomp is None:
        note("6.11", "no decompiled RimWorldBase found (set RIMWORLD_DECOMP) — the "
                     "source-derived checks below were NOT run, which is not the same as "
                     "passing")
    else:
        gdl = _read(os.path.join(decomp, "Verse", "GameDataSaveLoader.cs")) or ""
        check("6.11", "GameDataSaveLoader.SaveGame is still `void` and still swallows its "
                      "own exception — which is why this verb verifies the file afterwards",
              "public static void SaveGame(string fileName)" in gdl
              and "Log.Error(\"Exception while saving game: \"" in gdl,
              "a void SaveGame with a catch-and-log", None)
        check("6.12", "…and touches NOTHING of the autosave rotation: it sets lastSaveTick "
                      "and nothing else",
              "lastSaveTick = Find.TickManager.TicksGame" in gdl
              and "Autosave-" not in gdl,
              "no autosave name anywhere in GameDataSaveLoader", None)
        auto = _read(os.path.join(decomp, "RimWorld", "Autosaver.cs")) or ""
        check("6.13", "the rotation is NAME-BASED and lives in Autosaver, so refusing the "
                      "names is what makes a slot unconsumable",
              '"Autosave-" + i' in auto and "Prefs.AutosavesCount" in auto,
              "AutoSaveNames yielding Autosave-<1..AutosavesCount>", None)
        sgfu = _read(os.path.join(decomp, "Verse", "SaveGameFilesUtility.cs")) or ""
        check("6.14", "…and IsAutoSave is still the prefix test this verb refuses on",
              'fileName.Substring(0, 8) == "Autosave"' in sgfu, "the prefix test", None)
        gt = _read(os.path.join(decomp, "Verse", "GenText.cs")) or ""
        m = re.search(r"public static bool IsValidFilename\(string str\)\s*\{\s*"
                      r"if \(str\.Length > (\d+)\)", gt, re.S)
        check("6.15", "GenText.IsValidFilename still caps at %d characters" % MAX_NAME,
              m is not None and int(m.group(1)) == MAX_NAME, str(MAX_NAME),
              None if m is None else m.group(1))
        check("6.16", "…and its illegal set still contains the path separators, which is "
                      "what blocks `../`",
              "/\\\\{}<>:*|!@#$%^&*?" in gt or '"/\\\\' in gt,
              "the separators in GetInvalidFilenameCharacters", None)
        mm = _read(os.path.join(decomp, "RimWorld", "MainMenuDrawer.cs")) or ""
        flat = re.sub(r"\s+", " ", mm)
        check("6.17", "the ESC menu's Save option still carries all three clauses this "
                      "verb reproduces",
              "Current.ProgramState == ProgramState.Playing && "
              "!GameDataSaveLoader.SavingIsTemporarilyDisabled && "
              "!Current.Game.Info.permadeathMode" in flat,
              "the three-clause guard", None)
        ss = _read(os.path.join(decomp, "Verse", "SafeSaver.cs")) or ""
        check("6.18", "…and a failed save still pops GenUI.ErrorDialog, which is why "
                      "`force_pause` is in the envelope",
              "GenUI.ErrorDialog" in ss, "the error dialog", None)
        gfp = _read(os.path.join(decomp, "Verse", "GenFilePaths.cs")) or ""
        check("6.19", "FilePathForSavedGame is still name + \".rws\" under Saves/",
              'Path.Combine(SavedGamesFolderPath, gameName + ".rws")' in gfp,
              "the path composition", None)

    # ---- the docs -------------------------------------------------------
    pl = _read(os.path.join(REPO, "playbook", "PLAY-LOOP.md")) or ""
    check("6.20", "PLAY-LOOP §Artifacts names the save as an artifact",
          "`save {name}`" in pl and "## Artifacts" in pl, "the artifact entry", None)
    # Whitespace-normalised: the source wraps mid-phrase ("every casualty\n
    # halt"), and a raw substring test would report the doc missing when it is
    # only line-broken — a false FAIL is as corrosive as a false pass.
    plf = re.sub(r"\s+", " ", pl)
    missing = [m for m in ("every threat halt", "every casualty halt",
                           "every day boundary") if m not in plf]
    check("6.21", "…with the three moments it is owed at",
          not missing, "all three moments", {"missing": missing})
    check("6.22", "…and the write/load asymmetry",
          "only the launcher may load" in pl.lower(), "the asymmetry", None)

    # ---- the helpers ----------------------------------------------------
    good = {"data": {"bytes_before": 123}}
    nulled = {"data": {"bytes_before": None}}
    absent_env = {"data": {}}
    check("6.23", "shape() FAILS on an absent key",
          not probe(lambda: shape("x", "v", absent_env, "data.bytes_before")), "fail", None)
    check("6.24", "eq(..., None) would have PASSED on it, which is why the fresh-save "
                  "assertion uses null_at()",
          probe(lambda: eq("x", "w", absent_env, "data.bytes_before", None)),
          "pass (and that is the hazard)", None)
    check("6.25", "null_at() FAILS on the absent key and passes on the present null",
          not probe(lambda: null_at("x", "w", absent_env, "data.bytes_before"))
          and probe(lambda: null_at("x", "w", nulled, "data.bytes_before")),
          "fail then pass", None)
    check("6.26", "eq_int() FAILS when either side is not an integer",
          not probe(lambda: eq_int("x", "w", None, 3))
          and not probe(lambda: eq_int("x", "w", 3, None)),
          "fail on both", None)
    check("6.27", "bad_args() distinguishes a REFUSAL from an exception — a refusal that "
                  "arrived as code `exception` would be a different defect in the same "
                  "colour",
          probe(lambda: bad_args("x", "w", {"ok": False,
                                            "error": {"code": "bad-args", "detail": "z"}}))
          and not probe(lambda: bad_args("x", "w", {"ok": False,
                                                    "error": {"code": "exception",
                                                              "detail": "z"}})),
          "pass then fail", None)
    check("6.28", "keys_at_least() FAILS on a missing field",
          not probe(lambda: keys_at_least("x", "w", {"verb": "save"}, SAVE_KEYS)),
          "fail", None)


def _all_sources():
    d = os.path.join(REPO, "Source", "AutoRimmer")
    out = []
    try:
        for f in sorted(os.listdir(d)):
            if f.endswith(".cs"):
                out.append(_read(os.path.join(d, f)) or "")
    except OSError:
        pass
    return "\n".join(out)


# ---------------------------------------------------------------------- main --

PHASES = {1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5, 6: phase6}
DEFAULT_PHASES = [1, 2, 3, 4, 5, 6]


def main():
    global ARGS
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--root", default=DEFAULT_ROOT, help="the protocol root")
    p.add_argument("--phase", type=int, action="append", choices=[0, 1, 2, 3, 4, 5, 6],
                   help="run only these phases (repeatable); phase 0 always runs")
    p.add_argument("--dry-run", action="store_true",
                   help="print the plan and every expectation, send nothing")
    p.add_argument("--selftest", action="store_true",
                   help="phase 6 only: offline; no bench, no game, nothing sent")
    p.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = p.parse_args()

    print("AutoRimmer acceptance - the save verb an unattended run needs (bb931b9)")

    if ARGS.selftest:
        print("mode: --selftest (offline; no bench, no game, nothing sent)")
        phase6()
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
    print("%sThis suite WRITES up to three saves into the bench's Saves/, all prefixed "
          "`%s`, and does not delete them — it has no filesystem verb and will not shell "
          "out against a directory the game owns. Remove them by hand.%s"
          % (YELLOW, PREFIX, OFF))

    phase0()
    for n in wanted:
        PHASES[n]()
    return summarise()


def summarise(selftest=False):
    print("")
    print("=" * 78)
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

# ============================================================================
# WHAT THE MERGED CODE ACTUALLY DOES, read from Source/AutoRimmer/SaveVerbs.cs
# and cited by member, never by line.
#
# 1. The write is ONE LINE — `GameDataSaveLoader.SaveGame(trimmed)` —
#    synchronously on the main thread at the GameComponentUpdate safe point.
#    Vanilla wraps it in `LongEventHandler.QueueLongEvent(..., doAsynchronously:
#    false, ...)` purely for the progress screen; queuing it here would return
#    BEFORE the write, so the verb could not report the tick the snapshot
#    captured, which is the whole point of a checkpoint.
#
# 2. FOUR GATES, all reproduced and cited. Three from
#    `MainMenuDrawer.MainMenuOnGUI`'s Save option (ProgramState.Playing,
#    !SavingIsTemporarilyDisabled, !permadeathMode) and one from
#    `Dialog_FileList.DoTypeInField` (non-empty + GenText.IsValidFilename).
#    `GameDataSaveLoader.SaveGame` itself checks NOTHING, which is the usual
#    shape the gate-lives-in-the-widget rule exists for. The permadeath clause
#    gets NO bypass: the player cannot manually save in permadeath either, and
#    `dev:*` is the layer that may cheat.
#
# 3. THE NAME IS REFUSED, NOT SANITISED. Vanilla's
#    `Dialog_SaveFileList_Save.DoFileInteraction` calls
#    `GenFile.SanitizedFileName` and silently writes a different file — fine for
#    a human looking at a file list, wrong for a program that will go looking
#    for the path it asked for (git-bug acee526's exact-or-refuse rule).
#
# 4. TWO RULES THAT ARE OURS, NOT THE GAME'S, and both are named as such in the
#    verb's header: an `Autosave*` name is refused (the rotation is name-based,
#    `Autosaver.NewAutosaveFileName`, so this is what makes a slot
#    unconsumable), and an existing name is refused unless `overwrite:true`
#    (vanilla overwrites silently because the file list IS the confirmation).
#
# 5. `written` IS A STAT, NOT A RETURN VALUE, because there is no return value:
#    `SaveGame` is void and catches its own exception into a `Log.Error`. On a
#    FRESH name a non-empty file is proof; on an OVERWRITE it is not, and the
#    verb says so rather than inventing a success signal — `overwrote` and
#    `bytes_before` are published so a caller has the same evidence.
#
# 6. A FAILED SAVE WEDGES THE RUN, and that is in the same envelope. `SafeSaver`
#    pops `GenUI.ErrorDialog`, a `Dialog_MessageBox`, which sets `forcePause`,
#    and per spec 1.7 a force-pausing window halts every subsequent `advance` on
#    reason "dialog" and cannot be closed from here. `force_pause` carries
#    `TimeDriver.ForcePausePayload` — the same payload status.json publishes.
#
# 7. `busy` NEEDED NO CODE: `AgentGameComponent.DrainCommands` already answers
#    `Err.Busy` to every main-thread verb except `pause` while `TimeDriver
#    .Active`. Phase 4 asserts it anyway, because an unasserted claim about a
#    refusal path is how refusal paths rot — and it says out loud when the race
#    went the other way rather than passing quietly.
#
# 8. NOT DEMONSTRATED by this suite, and it cannot be without breaking the
#    bench: the FAILURE path (`written:false` + `force_pause`). It needs a save
#    that genuinely cannot be written — a full disk, a read-only Saves/ — and
#    staging one leaves the bench wedged on a modal dialog, which is exactly
#    what the play loop must not do to itself. The code path is asserted
#    statically in phase 6 instead.
# ============================================================================
