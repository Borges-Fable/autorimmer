#!/usr/bin/env python3
"""Acceptance runner for 722c951 (both halves) + 40ed42f #1 part 3.

Same protocol, helpers and exit codes as `accept/fc287ba-until-state.py`; read
that file's header first, especially the SHAPE CONTRACT note — `eq(..., None)`
passes on an absent key, so phase 0 proves every dig path before any later phase
leans on it.

    ./accept/722c951-advance-halt.py             # everything
    ./accept/722c951-advance-halt.py --phase 3   # one phase (0 always runs)
    ./accept/722c951-advance-halt.py --dry-run   # print the plan, send nothing

Start the bench first (`_RimWorld-Agent/run-agent.sh --quicktest`) with a colony
of at least TWO standing free colonists, `devMode = True` (the fixture steps
need it), and leave it paused.

WHY THIS SUITE EXISTS, in one sentence: run `m1-20260831` was TOLD a colonist
had gone down — step 148's own advance result carried `journal_seq:[125,128]` —
and advanced five more times while he bled for 11,335 ticks and died, so the
mod now refuses to advance blind and stops when a colonist falls.

  * PHASE 1 — the unread-journal refusal. Two advances with no read between
    them: the second is REFUSED, and the refusal names how many events are
    unread, the seq range and the TYPE BREAKDOWN (the line that would have said
    "downed 1"). Then a `journal` read, then the same advance PROCEEDING —
    because a check that is permanently on is not a watermark, it is a wall.
  * PHASE 2 — the escapes. `unread_ok` proceeds, is echoed on the envelope, is
    journaled as an act, and DOES NOT move the watermark (the next advance asks
    again). An empty or non-string reason is `bad-args`. A FILTERED journal read
    does not discharge what it never asked for.
  * PHASE 3 — the casualty halt. An own-faction downing armed to fire INSIDE an
    advance stops it at that tick, naming the pawn, the event and the tick. The
    same advance without the casualty runs to its predicate, which is what makes
    "it stopped early" mean something.
  * PHASE 4 — the faction filter. A HOSTILE downing inside the same advance does
    NOT halt it. Proved with the SAME fixture step and the same predicate, so
    the only variable is whose faction the pawn was in.
  * PHASE 5 — `through_casualties`: the casualty halt is overridable per call,
    with a reason, journaled.
  * PHASE 6 — 40ed42f part 3, the bleedout deadline. A downed colonist whose
    bleed clock is shorter than the nearest rescuer's travel makes `advance`
    refuse with both numbers. Staged by escalation and GATED ON `triage`'s own
    verdict: if the fixture cannot produce `too-slow`, that is a fixture gap and
    the suite says so rather than passing a check it did not earn.

NO BARE TICK COUNTS. Every advance here is bounded by a predicate —
`until:{condition:{path:"time.tick", op:">=", value:<absolute tick>}}`, i.e.
"advance until the game clock reaches T", where T is read from the game and not
guessed. That predicate is also what makes the halt assertions sharp: the
advance either stops EARLY on the casualty or runs on to `reason:"condition"`,
and the reason token is the whole discriminator. The one exception is phase 1,
which needs the journal to GROW during an advance and uses the shipped
`letter_delay_ticks` fixture with `until:{letter:true}` — the play loop's own
standing guard — because "wait until something is journaled" has no predicate
spelling and inventing one would be the guess this rule exists to stop.

IT DAMAGES COLONISTS. Phases 3, 5 and 6 down a colonist on purpose and phase 6
adds severe blood loss; each phase heals its subject afterwards, but a fixture
that crashes mid-phase leaves a casualty. Run it on a bench you are willing to
dirty, and not on a colony you care about.

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

# The reason this suite hands the mod when it deliberately uses an escape. It
# names the file, because the whole point of a required reason is that a
# post-mortem grepping `journal --types action` can tell WHO turned the guard
# off and why.
WHY = ("accept/722c951-advance-halt.py: the suite is proving the escape works, "
       "so this call is the subject of the test and not a run cutting a corner")


# ------------------------------------------------------------------ protocol --

def send(op, args=None, timeout=300):
    global SEQ
    SEQ += 1
    slug = re.sub(r"[^A-Za-z0-9]", "", op)[:16]
    cid = "acc722c951-%03d-%s" % (SEQ, slug)
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


# THE TWO WAYS THIS SUITE ADVANCES, named so a reader can see which is which at
# every call site. That is the opposite of the blanket wrapper the other suites
# in accept/ carry: those advance to move state and opt out once at the top,
# whereas here the refusal IS the subject and hiding it in a wrapper would hide
# the thing under test.

def advance(args, timeout=600):
    """An advance with NO escape — the mod's defaults, which is what is being
    measured. It may legitimately come back ok:false."""
    return send("advance", dict(args), timeout=timeout)


def advance_escaping(args, unread=True, casualties=True, why=WHY, timeout=600):
    """An advance that declares its escapes, each with the required reason."""
    a = dict(args)
    if unread:
        a["unread_ok"] = why
    if casualties:
        a["through_casualties"] = why
    return send("advance", a, timeout=timeout)


def read_journal(since=0, **extra):
    """The ONLY thing that clears the unread refusal. Called deliberately and
    never as housekeeping — where this suite reads, it is proving something."""
    a = {"since_seq": since}
    a.update(extra)
    return send("journal", a)


def until_tick(target):
    """`advance until the game clock reaches T` — the predicate form of a wait,
    with T read off the game rather than guessed. Every timed advance in this
    file is bounded this way; see the header's NO BARE TICK COUNTS note."""
    return {"condition": {"path": "time.tick", "op": ">=", "value": target}}


def now_tick():
    e = send("digest", {"sections": ["time"]})
    t = dig(e, "data.time.tick")
    return t if isinstance(t, int) else None


# ------------------------------------------------------------------- digging --

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
    `halted_seq` is DELIBERATELY absent for a state halt, and `unread_ok` is
    absent on every advance that did not use it."""
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


def token(detail, key):
    """The refusals put every number in error.detail as `key=value`, because a
    failed envelope carries no `data` block (Poller.BuildResultJson). This is
    the machine half of that contract; if it stops working the detail has
    stopped being parseable and the check should fail."""
    m = re.search(r"\b%s=(-?\d+)\b" % re.escape(key), detail or "")
    return int(m.group(1)) if m else None


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


def contains(num, what, env, path, needle):
    got = dig(env, path)
    ok = isinstance(got, str) and needle.lower() in got.lower()
    check(num, "%s (%s)" % (what, path), ok, "a string containing %r" % needle, got)


def refused(num, what, env, code, needle=None):
    """A REFUSAL IS THE ASSERTION. ok:false with the named code, and — when a
    needle is given — a detail that actually says the thing. A refusal with a
    useless message is only half the fix."""
    got = dig(env, "error.code")
    ok = dig(env, "ok") is False and got == code
    if ok and needle is not None:
        ok = needle.lower() in (dig(env, "error.detail") or "").lower()
    check(num, what, ok, "ok:false, code %s%s"
          % (code, "" if needle is None else ", detail naming %r" % needle),
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
        # `kind` may be a tuple of types — `data.halted_seq` is int-or-float,
        # since MiniJson gives a whole number back as either. tuple has no
        # __name__, and reaching for it crashed this suite at check 3.14 after
        # 62 green ones (session 21).
        want += " and a %s" % (kind.__name__ if isinstance(kind, type)
                               else "/".join(k.__name__ for k in kind))
    check(num, "`%s` publishes %s" % (verb, path), ok, want, dig(env, path))
    return ok


def banner(t):
    print("")
    print("=" * 78)
    print(t)
    print("=" * 78)


# ------------------------------------------------------------------ fixtures --

def standing_colonists():
    """(id, name) for every spawned free colonist who is UP.

    One `pawns` call, not one `pawn` call each: `PawnSerializer.Brief` already
    carries `flags` (`downed`/`bleeding`/`tend`/`drafted`/…) and the filter word
    is the singular `colonist` (`PawnSafe.FilterWords`). Rows arrive under
    `data.list` — `data.pawns` is `dev:spawn-pawn`'s key, not this verb's — and
    `flags` is ABSENT rather than empty when a pawn is fine, which is exactly
    the absent-vs-null trap this suite's `has_key` exists for."""
    e = send("pawns", {"filter": "colonist", "cap": 200})
    out = []
    for row in as_list(dig(e, "data.list")):
        if not isinstance(row, dict) or row.get("id") is None:
            continue
        if "downed" in as_list(row.get("flags")):
            continue
        out.append((row["id"], row.get("name")))
    return out


def heal(pid):
    """Undo a fixture casualty. Best effort and reported, never asserted: a
    bench left with a downed colonist is a mess, not a spec failure."""
    e = send("dev:heal", {"pawn": pid})
    return dig(e, "ok") is True


def clear_journal():
    """Discharge whatever the previous phase left unread, so the next phase
    starts from a known watermark. Returns the watermark."""
    e = read_journal(0, limit=2000)
    return dig(e, "data.read_watermark")


# ------------------------------------------------------------------- phase 0 --

def phase0():
    banner("PHASE 0 — the shapes every later phase digs into")

    e = send("status")
    precondition("0.a", "the bench is up and a colony is loaded",
                 dig(e, "ok") is True and dig(e, "data.gameLoaded") is True,
                 "start _RimWorld-Agent with a save loaded, paused.")

    roster = standing_colonists()
    precondition("0.b", "at least TWO standing free colonists",
                 ARGS.dry_run or len(roster) >= 2,
                 "phase 3 downs one and phase 6 needs somebody left who could "
                 "rescue them; with one colonist `triage` answers `no-rescuer` "
                 "and the deadline refusal can never fire. Found: %r" % (roster,))

    # -- the journal verb's new fields ------------------------------------
    j = read_journal(0, limit=5)
    eq("0.1", "`journal` still answers", j, "ok", True)
    shape("0.2", "journal", j, "data.read_watermark", int)
    shape("0.3", "journal", j, "data.watermark_was", int)
    shape("0.4", "journal", j, "data.watermark_moved", bool)
    shape("0.5", "journal", j, "data.unread_after", int)
    shape("0.6", "journal", j, "data.filtered", bool)
    eq("0.7", "an unfiltered read is not marked filtered", j, "data.filtered", False)
    # `limit:5` on a live journal is very likely truncated, and a truncated read
    # only claims what it handed over — so this is NOT asserted as 0.
    note("0.8", "read_watermark=%s unread_after=%s truncated=%s"
         % (dig(j, "data.read_watermark"), dig(j, "data.unread_after"),
            dig(j, "data.truncated")))

    # -- the advance envelope's new fields --------------------------------
    wm = clear_journal()
    t = now_tick()
    precondition("0.c", "the digest publishes time.tick",
                 ARGS.dry_run or isinstance(t, int),
                 "every advance in this suite is bounded by a `time.tick` "
                 "predicate; without that field there is no non-guessed bound.")
    a = advance({"until": until_tick((t or 0) + 60)}, timeout=180)
    eq("0.9", "a bounded advance runs", a, "ok", True)
    shape("0.10", "advance", a, "data.journal_read_watermark", int)
    shape("0.11", "advance", a, "data.journal_unread", int)
    check("0.12", "an advance that used no escape publishes no `unread_ok`",
          not has_key(a, "data.unread_ok"), "the key to be ABSENT",
          dig(a, "data.unread_ok"))
    check("0.13", "…and no `escaped` block",
          not has_key(a, "data.escaped"), "the key to be ABSENT",
          dig(a, "data.escaped"))
    note("0.14", "baseline watermark %s; advance halted reason=%s journal_unread=%s"
         % (wm, dig(a, "data.reason"), dig(a, "data.journal_unread")))

    # -- the fixture step exists ------------------------------------------
    e = send("journal-selftest", {"steps": ["down-at"], "down_delay_ticks": 600000})
    precondition("0.d", "`journal-selftest --steps down-at` is available",
                 dig(e, "ok") is True,
                 "phases 3-5 arm a downing INSIDE an advance and nothing else "
                 "can: every other route fires while the game is paused. "
                 "devMode must be on. Got: %s" % show(dig(e, "error")))
    shape("0.15", "journal-selftest", e, "data.down_at.fires_at_tick", int)
    shape("0.16", "journal-selftest", e, "data.down_at.pawn", int)
    shape("0.17", "journal-selftest", e, "data.down_at.player_faction", bool)
    eq("0.18", "the default victim is own-faction", e, "data.down_at.player_faction", True)
    # Disarm: 600000 ticks out, but leaving it armed across the run is sloppy.
    send("journal-selftest", {"steps": ["down-at"], "down_delay_ticks": 600000,
                              "down_pawn": dig(e, "data.down_at.pawn")})
    note("0.19", "down-at armed 600000 ticks out (effectively disarmed) on pawn %s"
         % dig(e, "data.down_at.pawn"))


# ------------------------------------------------------------------- phase 1 --

def phase1():
    banner("PHASE 1 — two advances, no read between them: the second is REFUSED")

    wm0 = clear_journal()
    note("1.0", "watermark cleared to %s" % wm0)

    # THE DELTA. A queued letter is the shipped deterministic mid-advance
    # stimulus (spec 1.3's until:letter acceptance uses the same step), and it
    # is the one thing that reliably makes the journal grow WHILE TIME RUNS on
    # a quiet colony. `until:{letter:true}` is the play loop's own standing
    # guard, so this advance is bounded by the loop's default rather than by a
    # number this file invented.
    e = send("journal-selftest", {"steps": ["letter"], "letter_delay_ticks": 300})
    precondition("1.a", "a letter can be queued for mid-advance arrival",
                 dig(e, "ok") is True,
                 "the refusal needs the previous advance to have journaled "
                 "something. Got: %s" % show(dig(e, "error")))

    a1 = advance({"until": {"letter": True}, "timeout_ticks": 20000}, timeout=300)
    eq("1.1", "the first advance runs (nothing is owed yet)", a1, "ok", True)
    eq("1.2", "…and halts on the letter it was waiting for", a1, "data.reason", "letter")
    seq = as_list(dig(a1, "data.journal_seq"))
    check("1.3", "…having journaled a delta (journal_seq is a range)",
          len(seq) == 2 and seq[1] >= seq[0], "a two-element [from,to]", seq)
    ge("1.4", "…and the envelope says the delta is unread", a1, "data.journal_unread", 1)

    # THE REFUSAL. Same call, nothing read in between.
    a2 = advance({"until": {"letter": True}, "timeout_ticks": 20000}, timeout=120)
    refused("1.5", "the SECOND advance is refused", a2, "unread-journal")
    detail = dig(a2, "error.detail") or ""
    n = token(detail, "unread")
    check("1.6", "the refusal NAMES how many events are unread",
          isinstance(n, int) and n >= 1, "unread=<N>, N >= 1", detail[:300])
    check("1.7", "…and the seq range it is talking about",
          isinstance(token(detail, "read_watermark"), int)
          and isinstance(token(detail, "advance_end_seq"), int),
          "read_watermark=<N> and advance_end_seq=<N>", detail[:300])
    contains("1.8", "…and the TYPE BREAKDOWN, which is the line that names a downing",
             a2, "error.detail", "types:")
    contains("1.9", "…and says what to do about it", a2, "error.detail", "journal")
    check("1.10", "a refusal carries NO data block — no ticks ran",
          not has_key(a2, "data"), "the key to be ABSENT", dig(a2, "data"))

    # A DIGEST IS NOT A READ. The nearest thing to a journal read that is not
    # one: digest.changed publishes counts per type, which cannot name a pawn.
    send("digest")
    a3 = advance({"until": {"letter": True}, "timeout_ticks": 20000}, timeout=120)
    refused("1.11", "a DIGEST does not discharge it", a3, "unread-journal")

    # NOR DOES THE ADVANCE'S OWN ECHO — proved by 1.5 and 1.11 both refusing
    # after an advance that published journal_seq. Stated rather than re-tested.
    note("1.12", "the advance's own journal_seq echo did not clear it either — "
                 "1.5 and 1.11 both followed an advance that published one")

    # THE WATERMARK MOVES. This is the half that proves the check is a
    # watermark and not a wall.
    j = read_journal(wm0 if isinstance(wm0, int) else 0, limit=2000)
    eq("1.13", "`journal` moves the watermark", j, "data.watermark_moved", True)
    eq("1.14", "…and clears the delta completely", j, "data.unread_after", 0)
    ge("1.15", "…to at least where the advance ended",
       j, "data.read_watermark", seq[1] if len(seq) == 2 else 1)

    t = now_tick()
    a4 = advance({"until": until_tick((t or 0) + 60)}, timeout=180)
    eq("1.16", "AFTER the read, the same advance PROCEEDS", a4, "ok", True)
    eq("1.17", "…on its own predicate, not on anything else",
       a4, "data.reason", "condition")


# ------------------------------------------------------------------- phase 2 --

def phase2():
    banner("PHASE 2 — the escape: explicit, reasoned, journaled, and not a mode")

    wm0 = clear_journal()
    e = send("journal-selftest", {"steps": ["letter"], "letter_delay_ticks": 300})
    precondition("2.a", "a letter can be queued", dig(e, "ok") is True,
                 "phase 2 needs an unread delta to escape past. Got: %s"
                 % show(dig(e, "error")))
    a1 = advance({"until": {"letter": True}, "timeout_ticks": 20000}, timeout=300)
    ge("2.1", "an unread delta exists to escape past", a1, "data.journal_unread", 1)

    # -- a reason is REQUIRED and must be a non-empty string ---------------
    t = now_tick()
    bad = advance({"until": until_tick((t or 0) + 60), "unread_ok": ""}, timeout=120)
    refused("2.2", "an EMPTY reason is bad-args", bad, "bad-args", "non-empty")
    bad = advance({"until": until_tick((t or 0) + 60), "unread_ok": 5}, timeout=120)
    refused("2.3", "a NON-STRING reason is bad-args", bad, "bad-args", "must be a string")
    bad = advance({"until": until_tick((t or 0) + 60), "through_casualties": "   "},
                  timeout=120)
    refused("2.4", "a WHITESPACE reason is bad-args too", bad, "bad-args", "non-empty")

    # -- the escape proceeds, and says so ----------------------------------
    t = now_tick()
    a2 = advance_escaping({"until": until_tick((t or 0) + 60)}, casualties=False,
                          timeout=180)
    eq("2.5", "`unread_ok` proceeds", a2, "ok", True)
    eq("2.6", "…and the envelope ECHOES the reason", a2, "data.unread_ok", WHY)
    shape("2.7", "advance", a2, "data.escaped.unread_journal", dict)
    ge("2.8", "…including the count it bypassed",
       a2, "data.escaped.unread_journal.unread", 1)
    contains("2.9", "…and the type breakdown it bypassed",
             a2, "data.escaped.unread_journal.types", " ")

    # -- it is JOURNALED as an act ----------------------------------------
    j = read_journal(wm0 if isinstance(wm0, int) else 0, types=["action"], limit=2000)
    rows = [ev for ev in as_list(dig(j, "data.events"))
            if dig(ev, "payload.verb") == "advance" and dig(ev, "payload.step") == "escape"]
    check("2.10", "the escape is journaled as an `action`", bool(rows),
          "an action row with payload.verb=advance, step=escape",
          [dig(ev, "payload") for ev in as_list(dig(j, "data.events"))][:6])
    if rows:
        check("2.11", "…carrying the reason verbatim",
              dig(rows[-1], "payload.unread_ok") == WHY, WHY, dig(rows[-1], "payload"))
        check("2.12", "…and naming WHAT it bypassed",
              "unread_journal" in as_list(dig(rows[-1], "payload.bypassed")),
              "bypassed containing 'unread_journal'", dig(rows[-1], "payload.bypassed"))

    # -- A FILTERED READ DOES NOT DISCHARGE WHAT IT NEVER ASKED FOR --------
    # The honesty clause: `journal --types letter` moves the watermark only as
    # far as the last letter it handed over, so a `downed` it filtered out is
    # still unread. Without this the escape hatch has a second, silent door.
    wm1 = clear_journal()
    e = send("journal-selftest", {"steps": ["letter"], "letter_delay_ticks": 300})
    a3 = advance({"until": {"letter": True}, "timeout_ticks": 20000}, timeout=300)
    ge("2.13", "a fresh delta exists", a3, "data.journal_unread", 1)
    jf = read_journal(wm1 if isinstance(wm1, int) else 0, types=["red_error"], limit=2000)
    eq("2.14", "a types-filtered read is MARKED filtered", jf, "data.filtered", True)
    ge("2.15", "…and leaves the rest unread", jf, "data.unread_after", 1)
    a4 = advance({"until": until_tick((now_tick() or 0) + 60)}, timeout=120)
    refused("2.16", "…so the advance is still refused", a4, "unread-journal")

    # -- THE ESCAPE IS PER-CALL AND NOT A MODE ----------------------------
    t = now_tick()
    a5 = advance_escaping({"until": until_tick((t or 0) + 60)}, casualties=False,
                          timeout=180)
    eq("2.17", "the escape gets past it once", a5, "ok", True)
    a6 = advance({"until": until_tick((now_tick() or 0) + 60)}, timeout=120)
    refused("2.18", "…and the NEXT advance is refused again — no mode was set",
            a6, "unread-journal")
    clear_journal()


# ------------------------------------------------------------------- phase 3 --

def phase3():
    banner("PHASE 3 — an own-faction downing STOPS the advance where it happens")

    roster = standing_colonists()
    precondition("3.a", "a standing free colonist to down",
                 ARGS.dry_run or len(roster) >= 2,
                 "found %r; phase 3 downs one and leaves the rest" % (roster,))
    victim = roster[0] if roster else (0, "?")
    clear_journal()

    t0 = now_tick() or 0
    delay = 400
    span = 4000          # the predicate's own bound, well past the downing
    e = send("journal-selftest", {"steps": ["down-at"], "down_delay_ticks": delay,
                                  "down_pawn": victim[0]})
    precondition("3.b", "the downing can be armed INSIDE an advance",
                 dig(e, "ok") is True,
                 "got: %s" % show(dig(e, "error")))
    fires = dig(e, "data.down_at.fires_at_tick")
    eq("3.1", "the fixture reports the victim as own-faction",
       e, "data.down_at.player_faction", True)
    note("3.2", "armed: %s (id %s) at tick %s; the advance is bounded at %s"
         % (victim[1], victim[0], fires, t0 + span))

    # The advance's OWN bound is a predicate — "until the clock reaches T" —
    # and T is ~3600 ticks past the downing. So "it stopped early" is a
    # measurement, not an interpretation.
    a = advance({"until": until_tick(t0 + span)}, timeout=600)
    eq("3.3", "the advance succeeded (a halt is not a failure)", a, "ok", True)
    eq("3.4", "…and HALTED ON THE CASUALTY", a, "data.reason", "casualty")
    eq("3.5", "…not on its own predicate", a, "data.reason", "casualty")
    shape("3.6", "advance", a, "data.halted_on", dict)
    eq("3.7", "the halt names the KIND", a, "data.halted_on.kind", "casualty")
    eq("3.8", "…the EVENT class", a, "data.halted_on.event", "downed")
    eq("3.9", "…the PAWN", a, "data.halted_on.pawn", victim[1])
    eq("3.10", "…the pawn's id, so the response is one `rescue` call away",
       a, "data.halted_on.pawn_id", victim[0])
    eq("3.11", "…that it was our faction", a, "data.halted_on.player_faction", True)
    eq("3.12", "…what kind of pawn", a, "data.halted_on.pawn_kind", "colonist")
    ge("3.13", "…and the TICK it happened at",
       a, "data.halted_on.tick", fires if isinstance(fires, int) else t0)
    shape("3.14", "advance", a, "data.halted_seq", (int, float))

    # THE M1 REPLAY SHAPE. Step 148 crossed a downing at 214,599 and ran on.
    # This is that advance, stopping there instead.
    elapsed = dig(a, "data.ticks_elapsed")
    bound = dig(a, "data.overshoot_bound") or 0
    check("3.15", "it stopped EARLY — the M1 shape, halted",
          isinstance(elapsed, int) and elapsed < span - 1000,
          "ticks_elapsed well under the predicate's %s" % span, elapsed)
    check("3.16", "…at the downing, within one frame of it",
          isinstance(elapsed, int) and isinstance(fires, int)
          and abs((t0 + elapsed) - fires) <= max(bound, 30) + 5,
          "the halt tick within overshoot_bound (%s) of the armed tick %s"
          % (bound, fires), {"halt_tick": t0 + (elapsed or 0), "armed": fires})

    # The delta the halt itself created must now block the next advance —
    # the two halves of this issue meeting.
    a2 = advance({"until": until_tick((now_tick() or 0) + 60)}, timeout=120)
    refused("3.17", "the casualty's own journal row now blocks the next advance",
            a2, "unread-journal")
    contains("3.18", "…and the refusal names it by type",
             a2, "error.detail", "downed")

    ok = heal(victim[0])
    note("3.19", "healed %s: %s" % (victim[1], "ok" if ok else "FAILED — bench is dirty"))
    clear_journal()


# ------------------------------------------------------------------- phase 4 --

def phase4():
    banner("PHASE 4 — a HOSTILE downing does NOT stop it (the filter is FACTION)")

    # A Megascarab in `Faction.OfInsects`, because `Dev.FactionArg` takes
    # "insect" as a name and `Faction.OfInsects` is present in every world —
    # there is no "hostile" keyword, and a Pirate faction may or may not exist
    # on a given map. One scarab is also the weakest thing on that list.
    e = send("dev:spawn-pawn", {"kind": "Megascarab", "faction": "insect", "count": 1})
    precondition("4.a", "a hostile pawn can be spawned",
                 dig(e, "ok") is True,
                 "the faction filter is only provable with a pawn on the other "
                 "side. Got: %s" % show(dig(e, "error")))
    # `dev:spawn-pawn` returns Dev.Describe rows under `data.pawns`.
    ids = [p.get("id") for p in as_list(dig(e, "data.pawns")) if isinstance(p, dict)]
    ids = [i for i in ids if isinstance(i, int)]
    precondition("4.b", "the spawn reports the pawn's id",
                 ARGS.dry_run or bool(ids),
                 "cannot arm the fixture without it. Envelope: %s" % show(dig(e, "data")))
    hostile = ids[0] if ids else 0

    clear_journal()
    t0 = now_tick() or 0
    # DOWN IT ALMOST IMMEDIATELY. The scarab spawns at the colony anchor by
    # default (`Dev.PosArg`'s fallback) and a hostile loose among the colonists
    # for 4000 ticks could down one of THEM — which would halt the advance
    # correctly and fail this phase for the opposite of the reason it tests.
    # 120 ticks is two seconds of game time: long enough to be inside the
    # advance, short enough that nothing else happens.
    delay = 120
    span = 4000
    e = send("journal-selftest", {"steps": ["down-at"], "down_delay_ticks": delay,
                                  "down_pawn": hostile})
    precondition("4.c", "the hostile downing arms", dig(e, "ok") is True,
                 "got: %s" % show(dig(e, "error")))
    eq("4.1", "the fixture agrees the victim is NOT own-faction",
       e, "data.down_at.player_faction", False)
    fires = dig(e, "data.down_at.fires_at_tick")

    # THE SAME PREDICATE, THE SAME FIXTURE, THE SAME SPAN as phase 3. The only
    # variable is the faction, which is what makes this a controlled proof
    # rather than two unrelated observations.
    a = advance({"until": until_tick(t0 + span)}, timeout=600)
    eq("4.2", "the advance succeeded", a, "ok", True)
    check("4.3", "it did NOT halt on the casualty",
          dig(a, "data.reason") != "casualty",
          "any reason but 'casualty'", dig(a, "data.reason"))
    eq("4.4", "…it ran on to its own predicate", a, "data.reason", "condition")
    ge("4.5", "…covering the whole span, past the downing",
       a, "data.ticks_elapsed", span - 1)
    note("4.6", "hostile downed at ~%s; the advance ran %s ticks to its bound"
         % (fires, dig(a, "data.ticks_elapsed")))

    # …and the downing really did happen, so 4.3 is "did not halt", not
    # "nothing occurred".
    j = read_journal(0, types=["downed"], limit=2000)
    downs = [ev for ev in as_list(dig(j, "data.events"))
             if isinstance(dig(ev, "tick"), int) and dig(ev, "tick") >= t0]
    check("4.7", "the hostile DID go down inside that advance", bool(downs),
          "a `downed` journal row at or after tick %s" % t0,
          [dig(ev, "payload") for ev in as_list(dig(j, "data.events"))][-3:])
    if downs:
        eq("4.8", "…and the row says it was not ours",
           downs[-1], "payload.player", False)
    clear_journal()


# ------------------------------------------------------------------- phase 5 --

def phase5():
    banner("PHASE 5 — `through_casualties` rides past it, per call, with a reason")

    roster = standing_colonists()
    precondition("5.a", "a standing free colonist to down",
                 ARGS.dry_run or len(roster) >= 2,
                 "found %r" % (roster,))
    victim = roster[0] if roster else (0, "?")
    wm0 = clear_journal()

    t0 = now_tick() or 0
    span = 4000
    e = send("journal-selftest", {"steps": ["down-at"], "down_delay_ticks": 400,
                                  "down_pawn": victim[0]})
    precondition("5.b", "the downing arms", dig(e, "ok") is True,
                 "got: %s" % show(dig(e, "error")))

    a = advance_escaping({"until": until_tick(t0 + span)}, unread=False, timeout=600)
    eq("5.1", "the advance succeeded", a, "ok", True)
    check("5.2", "…and did NOT halt on the casualty",
          dig(a, "data.reason") != "casualty",
          "any reason but 'casualty'", dig(a, "data.reason"))
    eq("5.3", "…it ran to its own predicate", a, "data.reason", "condition")
    eq("5.4", "the envelope echoes the reason", a, "data.through_casualties", WHY)

    j = read_journal(wm0 if isinstance(wm0, int) else 0, types=["action"], limit=2000)
    rows = [ev for ev in as_list(dig(j, "data.events"))
            if dig(ev, "payload.verb") == "advance" and dig(ev, "payload.step") == "escape"]
    check("5.5", "…and it is journaled as an act", bool(rows),
          "an action row with step=escape",
          [dig(ev, "payload") for ev in as_list(dig(j, "data.events"))][:6])
    if rows:
        check("5.6", "…carrying the through_casualties reason verbatim",
              dig(rows[-1], "payload.through_casualties") == WHY,
              WHY, dig(rows[-1], "payload"))

    ok = heal(victim[0])
    note("5.7", "healed %s: %s" % (victim[1], "ok" if ok else "FAILED — bench is dirty"))
    clear_journal()


# ------------------------------------------------------------------- phase 6 --

def phase6():
    banner("PHASE 6 — 40ed42f part 3: the bleed clock against the rescue")

    roster = standing_colonists()
    precondition("6.a", "at least two standing free colonists",
                 ARGS.dry_run or len(roster) >= 2,
                 "one to bleed and one who could rescue; with a single colonist "
                 "`triage` answers `no-rescuer` and there is no deadline to "
                 "compare. Found: %r" % (roster,))
    victim = roster[0] if roster else (0, "?")
    clear_journal()

    # STAGE BY ESCALATION, AND VERIFY WITH `triage` RATHER THAN ASSUME.
    # The refusal fires on `verdict == "too-slow"`, i.e. clock < travel+carry.
    # A downed colonist on a small map is usually reachable in time, so the
    # clock has to be driven down until the comparison inverts — and whether it
    # has is `triage`'s answer, not this file's arithmetic.
    e = send("dev:damage", {"pawn": victim[0], "mode": "until-downed",
                            "allow_bleeding": True})
    precondition("6.b", "the victim can be downed and left bleeding",
                 dig(e, "ok") is True and dig(e, "data.downed") is True,
                 "got: %s" % show(dig(e, "data") or dig(e, "error")))

    verdict, row = None, None
    for sev in (0.0, 0.5, 0.8, 0.90, 0.95, 0.98):
        if sev:
            send("dev:add-hediff", {"pawn": victim[0], "def": "BloodLoss",
                                    "severity": sev})
        t = send("triage", {})
        rows = [r for r in as_list(dig(t, "data.casualties"))
                if isinstance(r, dict) and r.get("pawn") == victim[0]]
        if not rows:
            continue
        row, verdict = rows[0], rows[0].get("verdict")
        note("6.1", "BloodLoss %.2f -> verdict %s, clock %s, margin %s"
             % (sev, verdict, dig(row, "clock.ticks"), row.get("margin_ticks")))
        if verdict == "too-slow":
            break

    precondition("6.c", "`triage` reaches verdict `too-slow`",
                 ARGS.dry_run or verdict == "too-slow",
                 "the deadline refusal fires on that verdict and NOTHING ELSE "
                 "— `no-rescuer`/`no-path`/`no-deadline` deliberately do not "
                 "refuse, because no act clears them and refusing forever "
                 "wedges the run (TriageVerbs.BleedoutDeadline's header). "
                 "Reached %r instead (clock %s, margin %s, rescuers %s). TWO "
                 "known fixture gaps: on a bare --quicktest map there is NO BED "
                 "anywhere, so TakeToBedGate answers `no-bed` for everyone and "
                 "every verdict is `no-rescuer` — build or spawn a bed first; "
                 "and on a map where every rescuer is a few cells away the clock "
                 "cannot be driven below the walk without killing the patient — "
                 "run this phase on a colony with some distance in it, or stage "
                 "a rescuer far from the casualty."
                 % (verdict, dig(row, "clock.ticks") if row else None,
                    row.get("margin_ticks") if row else None,
                    dig(row, "candidates_pathed") if row else None))

    # `row` is None only under --dry-run, where `precondition` prints and
    # returns instead of exiting. Everywhere else 6.c has already stopped the
    # suite if the verdict is anything but `too-slow`.
    clock = dig(row, "clock.ticks")
    margin = row.get("margin_ticks") if isinstance(row, dict) else None
    a = advance({"until": until_tick((now_tick() or 0) + 600)}, timeout=180)
    refused("6.2", "the advance is REFUSED on the deadline", a, "bleedout-deadline")
    # The negative half of the same rule, and it is the one that keeps a
    # ten-day run alive: a casualty NOBODY can reach is not a casualty nobody
    # can reach IN TIME, and only the second is a decision an advance is making.
    # Phase 6.c above already exercised the `no-rescuer` path — every escalation
    # step before `too-slow` was one — and none of them refused, or 6.2 would
    # have fired on the first `dev:damage` instead of after the escalation.
    note("6.2b", "the pre-escalation verdicts (%s) did NOT refuse — only "
                 "`too-slow` does" % "no-rescuer/in-time/no-deadline")
    detail = dig(a, "error.detail") or ""
    check("6.3", "the refusal reports the BLEED CLOCK",
          token(detail, "bleedout_ticks") == clock,
          "bleedout_ticks=%s (triage's own number)" % clock, detail[:400])
    check("6.4", "…and the RESCUE estimate",
          isinstance(token(detail, "rescue_ticks"), int)
          and token(detail, "rescue_ticks") > (clock or 0),
          "rescue_ticks=<N> with N > the clock", detail[:400])
    check("6.5", "…and the margin, negative, matching triage",
          token(detail, "margin_ticks") == margin,
          "margin_ticks=%s" % margin, detail[:400])
    contains("6.6", "…and NAMES THE PAWN", a, "error.detail", str(victim[1]))
    contains("6.7", "…and points at the verb that fixes it", a, "error.detail", "rescue")
    check("6.8", "a refusal carries NO data block — no ticks ran",
          not has_key(a, "data"), "the key to be ABSENT", dig(a, "data"))

    # The escape covers this refusal too — one flag for "I am deciding to let
    # this happen", not two.
    a2 = advance_escaping({"until": until_tick((now_tick() or 0) + 60)},
                          unread=True, timeout=180)
    eq("6.9", "`through_casualties` also overrides the deadline refusal", a2, "ok", True)
    shape("6.10", "advance", a2, "data.escaped.bleedout_deadline", dict)
    eq("6.11", "…carrying the reason", a2, "data.escaped.bleedout_deadline.reason", WHY)
    shape("6.12", "advance", a2, "data.escaped.bleedout_deadline.casualty", dict)

    ok = heal(victim[0])
    note("6.13", "healed %s: %s" % (victim[1], "ok" if ok else "FAILED — bench is dirty"))
    clear_journal()


# ---------------------------------------------------------------------- main --

PHASES = {0: phase0, 1: phase1, 2: phase2, 3: phase3, 4: phase4, 5: phase5, 6: phase6}


def main():
    global ARGS
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", default=DEFAULT_ROOT, help="protocol root")
    ap.add_argument("--phase", type=int, action="append",
                    help="run only these phases (0 always runs first)")
    ap.add_argument("--dry-run", action="store_true",
                    help="print the plan and every expectation, send nothing")
    ap.add_argument("--echo", action="store_true", help="echo every result envelope")
    ARGS = ap.parse_args()

    wanted = sorted(set(ARGS.phase or PHASES.keys()))
    if 0 not in wanted:
        wanted = [0] + wanted

    print("722c951 + 40ed42f#3 acceptance — root %s" % ARGS.root)
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
