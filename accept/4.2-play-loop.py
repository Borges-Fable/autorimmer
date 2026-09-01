#!/usr/bin/env python3
"""4.2 play-loop: run-dir compliance auditor (the mechanical half of acceptance).

Audits a finished run against playbook/PLAY-LOOP.md's auditable invariants:

    python accept/4.2-play-loop.py RUNS/<run> \
        [--journal journal/<sid>.ndjson] [--transcript transcripts/<run>] \
        [--repo <repo-root>]
    python accept/4.2-play-loop.py --selftest

Checks (PASS/FAIL/WARN per check, exit 0 iff no FAIL):
  ledger-valid        checklist.ndjson parses; keys + verdict enum per 4.1's schema
  item-ids-known      every ledger item id exists in checklists/*.md (### ids,
                      or colony-start-<n> within the colony-start step count)
  daily-coverage      for every day snapshot digests/day-<N>.json, every daily.md
                      item has a ledger line that day (silent skip = missing line)
  action-taken        >=1 verdict:"action" line with a non-empty note
  summary-written     RUNS/<run>/summary.md exists, non-empty
  final-undrafted     digests/final.json: no colonist drafted, and no hostile
                      left unpardoned (threats.hostiles_unpardoned, falling
                      back to threats.hostiles on a pre-pardon run)
  advance-invariants  from transcript result envelopes: timeout_ticks <= 60000,
                      ticks_elapsed <= 60000 + the envelope's own overshoot_bound,
                      halt_on_error never false, no two consecutive 0-tick advances
  advance-discipline  git-bug 722c951. Tells a BLIND-ADVANCE REFUSAL
                      (ok:false, unread-journal) from a CASUALTY HALT
                      (ok:true, reason:"casualty") from a BLEEDOUT REFUSAL
                      (ok:false, bleedout-deadline) from any other early
                      return, and audits what the loop did about each:
                      any blind-advance refusal FAILs, advancing with zero
                      `journal` ops FAILs (m1-20260831's 27-to-0 shape),
                      re-advancing straight off a casualty halt FAILs, and
                      every escape (`unread_ok` / `through_casualties`) is
                      surfaced with its reason for the human gate
  transcript-journal  every journal action/dev verb appears among transcript ops,
                      with journal count <= transcript count per op — minus the
                      lines a composite verb declares via caused_seqs, which are
                      attributed to that composite's own op
  zero-red-errors     no red_error events in the journal window

What this does NOT audit (the labelled human gate — Dorian reads the
transcript against PLAY-LOOP.md): trigger firings matching events, the
drafted-gate honoured per-advance, drill-down discipline, judgement quality.

The journal file passed in should be the session's own journal/<sid>.ndjson
(one sid = one session = one window). Stdlib only, like rwa.
"""

import argparse
import json
import re
import sys
import tempfile
from pathlib import Path

VERDICTS = {"ok", "action", "blocked", "n/a"}
MAX_ADVANCE_TICKS = 60000

results = []  # (status, check, detail)


def report(status, check, detail=""):
    results.append((status, check, detail))
    print(f"{status:4s}  {check:20s}  {detail}")


def load_ndjson(path):
    out = []
    for i, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = line.strip()
        if not line:
            continue
        try:
            out.append(json.loads(line))
        except json.JSONDecodeError as e:
            raise ValueError(f"{path.name}:{i}: {e}")
    return out


def checklist_item_ids(repo):
    """### ids from the three checklist files, + the colony-start step count."""
    ids = set()
    for name in ("turn.md", "triggered.md", "daily.md"):
        text = (repo / "checklists" / name).read_text(encoding="utf-8")
        ids |= set(re.findall(r"^### +(\S+)", text, re.M))
    trig = (repo / "checklists" / "triggered.md").read_text(encoding="utf-8")
    m = re.search(r"^## Colony start\n(.*?)(?=^## )", trig, re.M | re.S)
    steps = len(re.findall(r"^\d+\. ", m.group(1), re.M)) if m else 0
    return ids, steps


def daily_item_ids(repo):
    text = (repo / "checklists" / "daily.md").read_text(encoding="utf-8")
    return set(re.findall(r"^### +(\S+)", text, re.M))


def transcript_steps(tr):
    """Every command dir in order as (name, op, cmd, res). op comes from
    cmd.json when it is there and from the directory name otherwise, because a
    client that died before writing cmd.json still left the directory — and
    which op it WAS is the thing the adjacency rules below turn on."""
    out = []
    for d in sorted(tr.iterdir()):
        if not d.is_dir():
            continue
        m = re.fullmatch(r"\d+-(.+)", d.name)
        if not m:
            continue
        cmd = res = None
        for fn, slot in (("cmd.json", "cmd"), ("result.json", "res")):
            p = d / fn
            if p.is_file():
                try:
                    v = json.loads(p.read_text(encoding="utf-8"))
                except (OSError, json.JSONDecodeError):
                    v = None
                if slot == "cmd":
                    cmd = v
                else:
                    res = v
        op = (cmd or {}).get("op") or m.group(1)
        out.append((d.name, op, cmd, res))
    return out


def advance_discipline(tr):
    """git-bug 722c951 — THE DISCRIMINATOR.

    `advance` now has three distinguishable early returns that all used to be
    "an advance came back", and the whole point of the issue is that the loop's
    response to each is different:

      * BLIND-ADVANCE REFUSAL — ok:false, error.code "unread-journal". NO TICKS
        RAN. The loop tried to advance while the previous advance's journal
        delta was unread. This is m1-20260831's exact failure and it is a
        compliance FAIL of the loop, not of the mod: PLAY-LOOP §read step 1 is
        unconditional, and the mod is only saying so out loud.
      * BLEEDOUT REFUSAL — ok:false, error.code "bleedout-deadline". NO TICKS
        RAN. WARN, not FAIL, and the split is deliberate: the loop could not
        have known this without running a pathfinder itself, so being told is
        the mod doing its job. What matters is what happened next, which is the
        human gate's read — the reason and the pawn are in error.detail.
      * CASUALTY HALT — ok:true, data.reason "casualty". Ticks DID run and
        stopped at the downing. The loop must DO something before it advances
        again; re-advancing straight off one is OUT-1's failure with the halt
        added, and that is mechanical, so it FAILs here.

    Plus the check M1 could not have failed because nothing measured it: 27
    advances against ZERO `journal` calls. Any advancing at all with no journal
    op in the transcript is the shape, escape or no escape.
    """
    steps = transcript_steps(tr)
    if not steps:
        report("WARN", "advance-discipline", "no command dirs in transcript — skipped")
        return

    kinds = {}
    problems = []
    escapes = []
    journal_ops = sum(1 for _, op, _, _ in steps if op == "journal")
    n_adv = 0

    for i, (name, op, cmd, res) in enumerate(steps):
        if op != "advance":
            continue
        n_adv += 1
        args = (cmd or {}).get("args") or {}
        # The escape is declared in the ARGS and echoed on the DATA. Read both:
        # args alone misses an envelope replayed from elsewhere, data alone
        # misses an advance whose result never came back.
        data = (res or {}).get("data") or {}
        for key in ("unread_ok", "through_casualties"):
            why = args.get(key) or data.get(key)
            if why:
                escapes.append(f"{name}: {key}={why!r}")

        if res is None:
            kinds["no-result"] = kinds.get("no-result", 0) + 1
            continue
        if res.get("ok") is False:
            code = ((res.get("error") or {}).get("code")) or "?"
            kinds[f"refused:{code}"] = kinds.get(f"refused:{code}", 0) + 1
            detail = ((res.get("error") or {}).get("detail")) or ""
            if code == "unread-journal":
                problems.append(
                    f"{name}: ADVANCED BLIND — the mod refused with unread-journal "
                    f"({detail[:160]})")
            elif code == "bleedout-deadline":
                report("WARN", "advance-discipline",
                       f"{name}: bleedout-deadline refusal — {detail[:200]}")
            continue

        reason = data.get("reason")
        kinds[f"halt:{reason}"] = kinds.get(f"halt:{reason}", 0) + 1
        if reason != "casualty":
            continue
        halted = data.get("halted_on") or {}
        who = halted.get("pawn", "?")
        # The response test. The next command must not be another advance: a
        # casualty halt with nothing in between is the loop being told a
        # colonist went down and immediately burning more time.
        nxt = steps[i + 1] if i + 1 < len(steps) else None
        if nxt is None:
            report("WARN", "advance-discipline",
                   f"{name}: casualty halt on {who} was the LAST command — the run ended "
                   "there, which is a legitimate stop but is not a response")
        elif nxt[1] == "advance":
            problems.append(
                f"{name}: casualty halt on {who} (tick {halted.get('tick')}) and the very "
                f"next command was {nxt[0]} — the loop rode past it")

    if n_adv and journal_ops == 0:
        problems.append(
            f"{n_adv} advances and ZERO `journal` calls — m1-20260831's shape exactly "
            "(27 to 0), and the run that produced it lost two colonists to news it was "
            "handed and never read")

    for e in escapes:
        report("WARN", "advance-discipline", "escape used — " + e)

    tally = ", ".join(f"{k} {v}" for k, v in sorted(kinds.items())) or "none"
    if problems:
        report("FAIL", "advance-discipline", "; ".join(problems) + f" [{tally}]")
    elif n_adv:
        report("PASS", "advance-discipline",
               f"{n_adv} advances, {journal_ops} journal calls, {len(escapes)} escapes [{tally}]")
    else:
        report("WARN", "advance-discipline", "no advances in transcript")


def audit(run_dir, repo, journal_path=None, transcript_dir=None):
    run_dir, repo = Path(run_dir), Path(repo)

    # -- ledger-valid ------------------------------------------------------
    ledger_path = run_dir / "checklist.ndjson"
    ledger = []
    if not ledger_path.is_file():
        report("FAIL", "ledger-valid", f"missing {ledger_path}")
    else:
        try:
            ledger = load_ndjson(ledger_path)
            bad = [
                e for e in ledger
                if not {"day", "tick", "item", "verdict"} <= e.keys()
                or e["verdict"] not in VERDICTS
            ]
            if bad:
                report("FAIL", "ledger-valid", f"{len(bad)} malformed line(s), first: {bad[0]}")
            else:
                report("PASS", "ledger-valid", f"{len(ledger)} lines")
        except ValueError as e:
            report("FAIL", "ledger-valid", str(e))

    # -- item-ids-known ----------------------------------------------------
    known, start_steps = checklist_item_ids(repo)
    unknown = set()
    for e in ledger:
        item = str(e.get("item", ""))
        m = re.fullmatch(r"colony-start-(\d+)", item)
        if m:
            if not 1 <= int(m.group(1)) <= start_steps:
                unknown.add(item)
        elif item not in known:
            unknown.add(item)
    if unknown:
        report("FAIL", "item-ids-known", f"not in any checklist: {sorted(unknown)}")
    else:
        report("PASS", "item-ids-known", f"{start_steps} colony-start steps, {len(known)} ### ids")

    # -- daily-coverage ----------------------------------------------------
    daily = daily_item_ids(repo)
    days = sorted(
        int(m.group(1))
        for p in (run_dir / "digests").glob("day-*.json")
        if (m := re.fullmatch(r"day-(\d+)\.json", p.name))
    ) if (run_dir / "digests").is_dir() else []
    missing = [
        (d, item)
        for d in days
        for item in daily
        if not any(e.get("day") == d and e.get("item") == item for e in ledger)
    ]
    if missing:
        report("FAIL", "daily-coverage", f"missing (day, item): {missing}")
    elif not days:
        report("WARN", "daily-coverage", "no digests/day-*.json — no boundary crossed, or snapshots not written")
    else:
        report("PASS", "daily-coverage", f"days {days} x {len(daily)} items")

    # -- action-taken ------------------------------------------------------
    actions = [e for e in ledger if e.get("verdict") == "action" and str(e.get("note", "")).strip()]
    if actions:
        report("PASS", "action-taken", f'{len(actions)}, first: {actions[0]["item"]}: {actions[0]["note"]}')
    else:
        report("FAIL", "action-taken", "no action line with a note — the clause that makes this more than a document review")

    # -- summary-written ---------------------------------------------------
    summary = run_dir / "summary.md"
    if summary.is_file() and summary.read_text(encoding="utf-8").strip():
        report("PASS", "summary-written", summary.name)
    else:
        report("FAIL", "summary-written", f"{summary} missing or empty")

    # -- final-undrafted ---------------------------------------------------
    final = run_dir / "digests" / "final.json"
    if not final.is_file():
        report("FAIL", "final-undrafted", f"missing {final}")
    else:
        d = json.loads(final.read_text(encoding="utf-8"))
        d = d.get("data", d)  # accept a raw envelope or its data block
        colonists = d.get("colonists", {}).get("list", [])
        drafted = [c.get("name") for c in colonists if c.get("drafted")]
        threats = d.get("threats", {}) or {}
        # Evan's ruling (session 13): M1's insects "aren't hostile in the same
        # way a normal hostile is, since they won't attack at will", and the
        # run should have explicitly DECLARED it was not attacking them
        # because it wasn't ready. So the criterion is not "0 hostiles" but
        # "0 drafted, 0 hostiles that we haven't pardoned" — where a pardon is
        # a deliberate, journalled act (the `threat-pardon` verb) and never a
        # silent exemption. `hostiles` keeps its old meaning, the total.
        hostiles = threats.get("hostiles", 0)
        if "hostiles_unpardoned" in threats:
            standing, keyed = threats.get("hostiles_unpardoned", 0), "hostiles_unpardoned"
        else:
            # A run recorded before threat-pardon shipped declared no pardons
            # at all, so every hostile left standing is one nobody accounted
            # for. Falling back to the total is the STRICT reading and is
            # meant to be: m1-20260831 ends with six undeclared hostiles, and
            # that FAIL is the correct verdict on it, not a gap to paper over.
            standing, keyed = hostiles, "hostiles"
        if drafted:
            report("FAIL", "final-undrafted", f"drafted at end: {drafted}")
        elif standing and keyed == "hostiles_unpardoned":
            report("FAIL", "final-undrafted",
                   f"threats.hostiles_unpardoned == {standing} at end "
                   f"({hostiles} hostile(s), {threats.get('hostiles_pardoned', 0)} pardoned)")
        elif standing:
            report("FAIL", "final-undrafted",
                   f"threats.hostiles == {standing} at end, none declared — pre-pardon run, "
                   "no threats.hostiles_unpardoned field to key on")
        else:
            pardoned = threats.get("hostiles_pardoned", 0)
            report("PASS", "final-undrafted",
                   f"{len(colonists)} colonists, 0 drafted, 0 unpardoned hostiles"
                   + (f" ({pardoned} of {hostiles} pardoned)" if pardoned else ""))

    # -- advance-invariants (needs the transcript's result envelopes) ------
    if transcript_dir and Path(transcript_dir).is_dir():
        advances = []
        problems = []
        # One entry per advance DIRECTORY, in order: True/False for a readable
        # envelope, None for one we could not read. The None is load-bearing —
        # see the wedge check below.
        zeros = []
        for cmd_dir in sorted(Path(transcript_dir).iterdir()):
            if not cmd_dir.is_dir() or not re.search(r"-advance$", cmd_dir.name):
                continue
            cmd_file, res_file = cmd_dir / "cmd.json", cmd_dir / "result.json"
            # rwa writes cmd.json to disk BEFORE dispatching, so the two ways a
            # directory can come up short are distinguishable and they do not
            # mean the same thing.
            if not cmd_file.is_file():
                # Nothing on disk at all: a pre-fix recording artifact from
                # before rwa wrote cmd.json first. There is no verb, no args
                # and no envelope to name, so the cause is unrecoverable — all
                # this can honestly say is that an advance is missing here.
                report("WARN", "advance-invariants",
                       f"{cmd_dir.name}: empty command dir — cause unrecoverable, "
                       "the client wrote nothing before dying")
                zeros.append(None)
                continue
            try:
                cmd = json.loads(cmd_file.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError) as e:
                report("WARN", "advance-invariants", f"{cmd_dir.name}: cmd.json unreadable ({e})")
                zeros.append(None)
                continue
            if not res_file.is_file():
                # cmd.json on disk and no result: the client died MID-CALL.
                # The game kept running with nobody watching and no envelope
                # was ever written, so every invariant below is unenforceable
                # over that span. In m1-20260831 that cost ~60000 unobserved
                # ticks, more than once. This is a FAIL, not a WARN.
                problems.append(
                    f"{cmd_dir.name}: client died mid-call — cmd.json has verb "
                    f"'{cmd.get('op', '?')}' {cmd.get('args', {})}, no result.json "
                    "(the advance ran unobserved)")
                zeros.append(None)
                continue
            try:
                res = json.loads(res_file.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError) as e:
                report("WARN", "advance-invariants", f"{cmd_dir.name}: result.json unreadable ({e})")
                zeros.append(None)
                continue
            data = res.get("data", {}) or {}
            advances.append((cmd_dir.name, cmd.get("args", {}), data))
            zeros.append(data.get("ticks_elapsed", None) == 0)
        for name, args, data in advances:
            if args.get("timeout_ticks", 0) > MAX_ADVANCE_TICKS or args.get("ticks", 0) > MAX_ADVANCE_TICKS:
                problems.append(f"{name}: cap exceeded ({args})")
            if args.get("halt_on_error") is False:
                problems.append(f"{name}: halt_on_error disabled")
            # The declared args are not the whole story: TimeDriver.Start
            # (Source/AutoRimmer/TimeDriver.cs) applies a DEFAULT timeout when
            # `until` is set and timeout_ticks is omitted. That default was
            # 600000 — ten in-game days, 10x this policy's own cap — until
            # git-bug 1113019 cut it to 60000, one in-game day, which is this
            # cap exactly. The check stays, for two reasons: an advance that
            # actually ran past the cap is a real violation even when the
            # declared args look clean, and a transcript banked before that
            # change still carries the old default. Check what happened, not
            # just what was asked for. (The result now says which it was —
            # `timeout_ticks` beside `timeout_source`: caller | default | none.)
            #
            # But the cap is a TARGET, not a promise, and the driver says so.
            # TimeDriver's stop check runs once per FRAME, after vanilla has
            # already ticked up to TickRateMultiplier*2 times, so an advance
            # lands at its target or a little past — bounded by exactly one
            # frame's worth of ticks. TimeDriver.MaxTicksPerFrame publishes
            # that bound in every advance envelope as `overshoot_bound`
            # (30 at Ultrafast, 24 at Superfast, ...). Comparing elapsed to
            # the bare cap therefore FAILs the driver for behaviour it
            # documents: m1-20260831's 132-advance came in at 60021 against
            # a 60000 cap — 21 ticks, inside a bound of 30.
            #
            # Read `overshoot_bound`, NOT `overshoot`: TimeDriver only emits
            # `overshoot` when Target >= 0, i.e. for `advance {ticks:N}`. An
            # `until`+timeout advance — which is the shape that can reach the
            # cap at all — has no `overshoot` key to read.
            elapsed = data.get("ticks_elapsed", 0)
            bound = data.get("overshoot_bound", 0)
            if not isinstance(bound, (int, float)) or bound < 0:
                bound = 0
            if isinstance(elapsed, (int, float)) and elapsed > MAX_ADVANCE_TICKS + bound:
                problems.append(
                    f"{name}: ticks_elapsed {elapsed} exceeded cap "
                    f"{MAX_ADVANCE_TICKS}+{bound} overshoot_bound ({args})")
        # The wedge rule is about two advances that ACTUALLY ran back to back.
        # Building this list from readable envelopes alone closed the gap left
        # by a skipped directory and made two advances either side of it look
        # adjacent — so a run with an unreadable advance between them could
        # false-FAIL, and a real wedge hiding behind one could pass. None is
        # neither True nor False, so a gap breaks the adjacency run instead.
        if any(a is True and b is True for a, b in zip(zeros, zeros[1:])):
            problems.append("two consecutive 0-tick advances (wedge rule violated)")
        if problems:
            report("FAIL", "advance-invariants", "; ".join(problems))
        elif advances:
            report("PASS", "advance-invariants", f"{len(advances)} advances within policy")
        else:
            report("WARN", "advance-invariants", "no advance envelopes found in transcript")
        advance_discipline(Path(transcript_dir))
    else:
        report("WARN", "advance-invariants", "no transcript dir given — skipped")
        report("WARN", "advance-discipline", "no transcript dir given — skipped")

    # -- transcript-journal + zero-red-errors (need the journal) -----------
    if journal_path and Path(journal_path).is_file():
        events = load_ndjson(Path(journal_path))
        reds = [e for e in events if e.get("type") == "red_error"]
        if reds:
            report("FAIL", "zero-red-errors", f'{len(reds)}, first: {reds[0].get("payload", {}).get("msg", "")[:80]}')
        else:
            report("PASS", "zero-red-errors", f"{len(events)} events, 0 red")

        if transcript_dir and (Path(transcript_dir) / "log.ndjson").is_file():
            ops = {}
            for e in load_ndjson(Path(transcript_dir) / "log.ndjson"):
                ops[e.get("op")] = ops.get(e.get("op"), 0) + 1
            # "journal count <= transcript count per verb" holds only for verbs
            # the agent issued ITSELF. A composite verb calls other verbs
            # internally and each nested call journals its own line, so the
            # journal outruns the transcript with no op missing: m1-20260831's
            # dev:starter-kit (survival preset) fans out to seven
            # DevVerbs.SpawnThing calls, giving journal 46 vs transcript 39 for
            # dev:spawn-thing on a run where nothing was unlogged.
            #
            # The join to undo that already ships. StarterKit.cs publishes the
            # seqs it caused twice — `caused_seqs` on its own journal line and
            # `caused_journal_seqs` in its result envelope — for exactly this.
            # Attribute a caused line to the composite that caused it: drop it
            # from the per-verb tally, where the composite's own line already
            # stands against the composite's own transcript op.
            caused = set()
            for e in events:
                p = e.get("payload") or {}
                for key in ("caused_seqs", "caused_journal_seqs"):
                    v = p.get(key)
                    if isinstance(v, list):
                        caused.update(s for s in v if isinstance(s, int))
            jverbs = {}
            attributed = 0
            for e in events:
                if e.get("type") in ("action", "dev"):
                    if e.get("seq") in caused:
                        attributed += 1
                        continue
                    v = (e.get("payload") or {}).get("verb")
                    jverbs[v] = jverbs.get(v, 0) + 1
            gaps = [
                f"{v}: journal {n} > transcript {ops.get(v, 0)}"
                for v, n in sorted(jverbs.items())
                if n > ops.get(v, 0)
            ]
            note = f" (+{attributed} nested, attributed to their composite verb)" if attributed else ""
            if gaps:
                report("FAIL", "transcript-journal", "; ".join(gaps) + note)
            else:
                report("PASS", "transcript-journal",
                       f"{sum(jverbs.values())} journaled mutations all covered by transcript ops" + note)
        else:
            report("WARN", "transcript-journal", "no transcript log.ndjson — skipped")
    else:
        report("WARN", "zero-red-errors", "no journal given — skipped")
        report("WARN", "transcript-journal", "no journal given — skipped")


# ---------------------------------------------------------------- selftest
def build_fixture(root, repo):
    """A minimal PASSING run: pinned synthetic, the postmortem.md discipline."""
    run = root / "RUNS" / "selftest-run"
    (run / "digests").mkdir(parents=True)
    daily = sorted(daily_item_ids(repo))
    ledger = [
        {"day": 1, "tick": 500, "item": "colony-start-1", "verdict": "ok", "reading": "stockpile placed"},
        {"day": 2, "tick": 61000, "item": "food-days-floor", "verdict": "action",
         "reading": "4.9", "note": "designate harvest 12x8 at farm"},
    ]
    ledger += [
        {"day": 2, "tick": 61000, "item": item, "verdict": "n/a" if item == "apparel-margin" else "ok",
         "reading": "-"}
        for item in daily
    ]
    (run / "checklist.ndjson").write_text(
        "\n".join(json.dumps(e) for e in ledger) + "\n", encoding="utf-8")
    (run / "digests" / "day-2.json").write_text("{}", encoding="utf-8")
    (run / "digests" / "final.json").write_text(json.dumps({
        "colonists": {"list": [{"name": "A", "drafted": False}, {"name": "B", "drafted": False}]},
        # Hostiles STANDING at the end and the run still green, because every
        # one of them was pardoned by a deliberate act. That is the shape
        # Evan's ruling makes legal, so it is the shape the clean fixture
        # pins — a check that only ever saw hostiles == 0 would not prove it.
        "threats": {"hostiles": 6, "hostiles_pardoned": 6, "hostiles_unpardoned": 0},
    }), encoding="utf-8")
    (run / "summary.md").write_text("# selftest run\nlast seq read: 7\n0 drafted, 0 red.\n", encoding="utf-8")

    tr = root / "transcripts" / "selftest-run"
    for i, (op, args, data) in enumerate([
        # A `journal` read BEFORE the first advance, and one after every
        # advance that produced anything. git-bug 722c951 made that the mod's
        # rule; the clean fixture is the shape a compliant run has, so it has
        # to carry the reads or `advance-discipline` would be asserting on a
        # fixture nobody could actually produce.
        ("journal", {"since_seq": 0}, {"count": 2, "read_watermark": 2, "unread_after": 0}),
        ("advance", {"until": {"letter": True}, "timeout_ticks": 60000},
         {"reason": "letter", "ticks_elapsed": 41000, "journal_unread": 1}),
        ("journal", {"since_seq": 2}, {"count": 1, "read_watermark": 3, "unread_after": 0}),
        ("designate", {"kind": "harvest"}, {}),
        # A timeout advance that lands PAST the cap by less than the bound the
        # envelope itself publishes. This is legal — the stop check is per
        # frame — and the clean fixture must stay green on it.
        ("advance", {"until": {"letter": True}, "timeout_ticks": 60000},
         {"reason": "timeout", "ticks_elapsed": 60021, "overshoot_bound": 30}),
    ], 1):
        d = tr / f"{i:03d}-{op}"
        d.mkdir(parents=True)
        (d / "cmd.json").write_text(json.dumps({"id": f"c{i}", "op": op, "args": args}), encoding="utf-8")
        (d / "result.json").write_text(json.dumps({"id": f"c{i}", "op": op, "ok": True, "data": data}), encoding="utf-8")
    (tr / "log.ndjson").write_text(
        "\n".join(json.dumps({"op": op, "ok": True})
                  for op in ("journal", "advance", "journal", "designate", "advance",
                             "dev:starter-kit")) + "\n",
        encoding="utf-8")

    journal = root / "journal.ndjson"
    journal.write_text("\n".join(json.dumps(e) for e in [
        {"seq": 1, "tick": 0, "type": "session", "payload": {"kind": "boot"}},
        {"seq": 2, "tick": 20000, "type": "letter", "payload": {"def": "NeutralEvent", "label": "Wanderer"}},
        {"seq": 3, "tick": 61000, "type": "action", "payload": {"verb": "designate", "step": "harvest"}},
        # One composite verb and the two lines it caused. There is no
        # dev:spawn-thing op in the transcript and there should not be: the
        # agent issued one op, dev:starter-kit, which spawned internally. The
        # clean fixture must stay green, so a per-verb tally that counts the
        # nested lines against a missing op is caught here.
        {"seq": 4, "tick": 700, "type": "dev", "payload": {"verb": "dev:spawn-thing", "step": "spawn-thing"}},
        {"seq": 5, "tick": 700, "type": "dev", "payload": {"verb": "dev:spawn-thing", "step": "spawn-thing"}},
        {"seq": 6, "tick": 700, "type": "dev",
         "payload": {"verb": "dev:starter-kit", "step": "starter-kit", "caused_seqs": [4, 5]}},
    ]) + "\n", encoding="utf-8")
    return run, journal, tr


def strip_journal_ops(tr):
    """Delete every `journal` step, leaving the advances with nothing read
    between them — m1-20260831's 27-advances-to-zero-journal-calls shape."""
    import shutil
    for d in list(tr.iterdir()):
        if d.is_dir() and d.name.endswith("-journal"):
            shutil.rmtree(d)
    log = tr / "log.ndjson"
    lines = [l for l in log.read_text(encoding="utf-8").splitlines()
             if l.strip() and json.loads(l).get("op") != "journal"]
    log.write_text("\n".join(lines) + "\n", encoding="utf-8")


def ride_past_casualty(tr):
    """Advance 002 halts on an own-faction downing; the very next command is
    another advance instead of a response."""
    (tr / "002-advance" / "result.json").write_text(json.dumps(
        {"id": "c2", "op": "advance", "ok": True,
         "data": {"reason": "casualty", "ticks_elapsed": 1837,
                  "halted_on": {"kind": "casualty", "event": "downed", "pawn": "Table",
                                "pawn_id": 4211, "player_faction": True,
                                "pawn_kind": "colonist", "tick": 214599}}}), encoding="utf-8")
    src = tr / "003-journal"
    dst = tr / "003-advance"
    src.rename(dst)
    (dst / "cmd.json").write_text(json.dumps(
        {"id": "c3", "op": "advance", "args": {"ticks": 2500}}), encoding="utf-8")
    (dst / "result.json").write_text(json.dumps(
        {"id": "c3", "op": "advance", "ok": True,
         "data": {"reason": "ticks", "ticks_elapsed": 2500}}), encoding="utf-8")


def escape_fixture(tr):
    """An advance carrying both per-call escapes. Not a FAIL — the escape is
    legal by design — but it must never pass in SILENCE, so it is asserted as
    a WARN naming the reason. An opt-out nobody can see in the audit is the
    silent bypass the issue exists to prevent."""
    why = "burning 3 days unattended to reach the caravan window"
    (tr / "005-advance" / "cmd.json").write_text(json.dumps(
        {"id": "c5", "op": "advance",
         "args": {"ticks": 60000, "unread_ok": why, "through_casualties": why}}), encoding="utf-8")
    (tr / "005-advance" / "result.json").write_text(json.dumps(
        {"id": "c5", "op": "advance", "ok": True,
         "data": {"reason": "ticks", "ticks_elapsed": 60000,
                  "unread_ok": why, "through_casualties": why}}), encoding="utf-8")
    return why


def bleedout_fixture(tr):
    """A bleedout-deadline refusal. WARN, not FAIL: the loop could not have
    known without running a pathfinder itself, so being told is the mod doing
    its job — but the pawn and both numbers must reach the audit."""
    (tr / "005-advance" / "result.json").write_text(json.dumps(
        {"id": "c5", "op": "advance", "ok": False,
         "error": {"code": "bleedout-deadline",
                   "detail": "Captain bleeds out in 9040 ticks and the nearest capable "
                             "rescuer needs 12100 ticks — 3060 ticks short. "
                             "bleedout_ticks=9040 rescue_ticks=12100 margin_ticks=-3060"}}),
        encoding="utf-8")


def selftest(repo):
    global results
    failures = []
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)
        run, journal, tr = build_fixture(root, repo)

        print("== selftest: clean fixture must pass every check ==")
        results = []
        audit(run, repo, journal, tr)
        if any(s == "FAIL" for s, _, _ in results):
            failures.append("clean fixture FAILed a check")

        print("\n== selftest: each mutation must fail its own check ==")
        for label, mutate, expect in [
            ("a daily item skipped silently",
             lambda: (run / "checklist.ndjson").write_text(
                "\n".join(l for l in (run / "checklist.ndjson").read_text(encoding="utf-8").splitlines()
                          if '"freezer-below-zero"' not in l) + "\n", encoding="utf-8"),
             "daily-coverage"),
            ("a colonist left drafted",
             lambda: (run / "digests" / "final.json").write_text(json.dumps({
                "colonists": {"list": [{"name": "A", "drafted": True}]},
                "threats": {"hostiles": 0}}), encoding="utf-8"),
             "final-undrafted"),
            # A hostile nobody pardoned. The pardon is the whole point: five
            # of six declared is still one left standing unaccounted for.
            ("a hostile left unpardoned",
             lambda: (run / "digests" / "final.json").write_text(json.dumps({
                "colonists": {"list": [{"name": "A", "drafted": False}]},
                "threats": {"hostiles": 6, "hostiles_pardoned": 5,
                            "hostiles_unpardoned": 1}}), encoding="utf-8"),
             "final-undrafted"),
            # The pre-pardon shape: no hostiles_unpardoned field at all, so
            # the fallback reads the total. This is m1-20260831's own shape
            # and it must stay red — a run that never declared a pardon
            # accounted for none of its hostiles.
            ("a pre-pardon run with hostiles and no declaration",
             lambda: (run / "digests" / "final.json").write_text(json.dumps({
                "colonists": {"list": [{"name": "A", "drafted": False}]},
                "threats": {"hostiles": 6}}), encoding="utf-8"),
             "final-undrafted"),
            ("two 0-tick advances back to back",
             lambda: [(tr / "005-advance" / "result.json").write_text(json.dumps(
                {"id": "c5", "op": "advance", "ok": True,
                 "data": {"reason": "dialog", "ticks_elapsed": 0}}), encoding="utf-8"),
                (tr / "002-advance" / "result.json").write_text(json.dumps(
                    {"id": "c2", "op": "advance", "ok": True,
                     "data": {"reason": "dialog", "ticks_elapsed": 0}}), encoding="utf-8")],
             "advance-invariants"),
            # cmd.json on disk with no result.json: rwa writes the command
            # before dispatching, so this shape means the client died mid-call
            # and the advance ran unobserved. FAIL, not WARN.
            ("the client died mid-call",
             lambda: (tr / "005-advance" / "result.json").unlink(),
             "advance-invariants"),
            # ---- git-bug 722c951: the three early returns, told apart -------
            # A BLIND ADVANCE. The mod refused; no ticks ran. The FAIL is on
            # the LOOP — PLAY-LOOP §read step 1 is unconditional and it was
            # skipped — and this is m1-20260831's exact failure, now
            # mechanically detectable for the first time.
            ("an advance the mod refused as blind",
             lambda: (tr / "005-advance" / "result.json").write_text(json.dumps(
                {"id": "c5", "op": "advance", "ok": False,
                 "error": {"code": "unread-journal",
                           "detail": "the previous advance journaled 4 event(s) that no "
                                     "`journal` call has read (seq 125..128; types: downed 1, "
                                     "letter 1, alert_on 2). unread=4"}}), encoding="utf-8"),
             "advance-discipline"),
            # 27 advances, ZERO journal calls. The number that named the M1
            # failure, and nothing in this auditor could see it until now.
            ("advances with no journal call at all",
             lambda: strip_journal_ops(tr),
             "advance-discipline"),
            # Told a colonist went down, and the very next command is more
            # time. OUT-1's failure with the halt already fired.
            ("a casualty halt ridden straight past",
             lambda: ride_past_casualty(tr),
             "advance-discipline"),
        ]:
            # rebuild clean, then break one thing
            for p in (root / "RUNS", root / "transcripts"):
                if p.exists():
                    import shutil
                    shutil.rmtree(p)
            run, journal, tr = build_fixture(root, repo)
            mutate()
            results = []
            audit(run, repo, journal, tr)
            got = {c for s, c, _ in results if s == "FAIL"}
            if expect not in got:
                failures.append(f"mutation '{label}' did not FAIL {expect} (FAILs: {sorted(got)})")
            print(f"-- mutation '{label}' -> {expect}: {'ok' if expect in got else 'MISSED'}\n")

        # ---- git-bug 722c951: the two shapes that must SURFACE, not FAIL ----
        #
        # A legal escape and a bleedout refusal are both allowed. What is NOT
        # allowed is either of them passing in silence, so these are asserted
        # on the WARN line and on its TEXT — a check that only counted FAILs
        # would let an escape through with nobody able to read its reason,
        # which is the silent bypass the issue is about.
        print("== selftest: escapes and bleedout refusals must SURFACE ==")
        for label, mutate, needle in [
            ("both escapes on one advance", escape_fixture,
             "burning 3 days unattended"),
            ("a bleedout-deadline refusal", bleedout_fixture,
             "bleedout_ticks=9040"),
        ]:
            for p in (root / "RUNS", root / "transcripts"):
                if p.exists():
                    import shutil
                    shutil.rmtree(p)
            run, journal, tr = build_fixture(root, repo)
            mutate(tr)
            results = []
            audit(run, repo, journal, tr)
            warns = [d for s, c, d in results
                     if s == "WARN" and c == "advance-discipline"]
            hit = any(needle in d for d in warns)
            fails = {c for s, c, _ in results if s == "FAIL"}
            if not hit:
                failures.append(
                    f"'{label}' produced no advance-discipline WARN naming {needle!r} "
                    f"(warns: {warns})")
            if "advance-discipline" in fails:
                failures.append(f"'{label}' FAILed advance-discipline; it is legal, not a defect")
            print(f"-- '{label}' -> WARN naming {needle!r}: {'ok' if hit else 'MISSED'}\n")

    if failures:
        print("SELFTEST FAIL:", *failures, sep="\n  ")
        return 1
    print("SELFTEST PASS")
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("run_dir", nargs="?", help="RUNS/<run> to audit")
    ap.add_argument("--journal", help="the session's journal/<sid>.ndjson")
    ap.add_argument("--transcript", help="transcripts/<run> dir")
    ap.add_argument("--repo", default=str(Path(__file__).resolve().parent.parent),
                    help="repo root (for checklists/)")
    ap.add_argument("--selftest", action="store_true", help="audit a pinned synthetic run, then prove each check can fail")
    a = ap.parse_args()

    if a.selftest:
        sys.exit(selftest(Path(a.repo)))
    if not a.run_dir:
        ap.error("run_dir required (or --selftest)")
    audit(a.run_dir, a.repo, a.journal, a.transcript)
    sys.exit(1 if any(s == "FAIL" for s, _, _ in results) else 0)


if __name__ == "__main__":
    main()
