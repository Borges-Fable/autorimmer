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
  final-undrafted     digests/final.json: no colonist drafted, hostiles == 0
  advance-invariants  from transcript result envelopes: timeout_ticks <= 60000,
                      halt_on_error never false, no two consecutive 0-tick advances
  transcript-journal  every journal action/dev verb appears among transcript ops,
                      with journal count <= transcript count per op
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
        hostiles = d.get("threats", {}).get("hostiles", 0)
        if drafted:
            report("FAIL", "final-undrafted", f"drafted at end: {drafted}")
        elif hostiles:
            report("FAIL", "final-undrafted", f"threats.hostiles == {hostiles} at end")
        else:
            report("PASS", "final-undrafted", f"{len(colonists)} colonists, 0 drafted, 0 hostiles")

    # -- advance-invariants (needs the transcript's result envelopes) ------
    if transcript_dir and Path(transcript_dir).is_dir():
        advances = []
        for cmd_dir in sorted(Path(transcript_dir).iterdir()):
            if not cmd_dir.is_dir() or not re.search(r"-advance$", cmd_dir.name):
                continue
            try:
                cmd = json.loads((cmd_dir / "cmd.json").read_text(encoding="utf-8"))
                res = json.loads((cmd_dir / "result.json").read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError) as e:
                report("WARN", "advance-invariants", f"{cmd_dir.name}: unreadable ({e})")
                continue
            advances.append((cmd_dir.name, cmd.get("args", {}), res.get("data", {})))
        problems = []
        for name, args, data in advances:
            if args.get("timeout_ticks", 0) > MAX_ADVANCE_TICKS or args.get("ticks", 0) > MAX_ADVANCE_TICKS:
                problems.append(f"{name}: cap exceeded ({args})")
            if args.get("halt_on_error") is False:
                problems.append(f"{name}: halt_on_error disabled")
            # The declared args are not the whole story: TimeDriver.Start
            # (Source/AutoRimmer/TimeDriver.cs) defaults timeout_ticks to
            # 600000 whenever `until` is set and timeout_ticks is omitted —
            # 10x this policy's own cap. An advance that actually ran past
            # the cap is a real violation even when the declared args look
            # clean, so check what happened, not just what was asked for.
            elapsed = data.get("ticks_elapsed", 0)
            if isinstance(elapsed, (int, float)) and elapsed > MAX_ADVANCE_TICKS:
                problems.append(f"{name}: ticks_elapsed {elapsed} exceeded cap ({args})")
        zeros = [d.get("ticks_elapsed", None) == 0 for _, _, d in advances]
        if any(a and b for a, b in zip(zeros, zeros[1:])):
            problems.append("two consecutive 0-tick advances (wedge rule violated)")
        if problems:
            report("FAIL", "advance-invariants", "; ".join(problems))
        elif advances:
            report("PASS", "advance-invariants", f"{len(advances)} advances within policy")
        else:
            report("WARN", "advance-invariants", "no advance envelopes found in transcript")
    else:
        report("WARN", "advance-invariants", "no transcript dir given — skipped")

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
            jverbs = {}
            for e in events:
                if e.get("type") in ("action", "dev"):
                    v = e.get("payload", {}).get("verb")
                    jverbs[v] = jverbs.get(v, 0) + 1
            gaps = [
                f"{v}: journal {n} > transcript {ops.get(v, 0)}"
                for v, n in sorted(jverbs.items())
                if n > ops.get(v, 0)
            ]
            if gaps:
                report("FAIL", "transcript-journal", "; ".join(gaps))
            else:
                report("PASS", "transcript-journal", f"{sum(jverbs.values())} journaled mutations all covered by transcript ops")
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
        "threats": {"hostiles": 0},
    }), encoding="utf-8")
    (run / "summary.md").write_text("# selftest run\nlast seq read: 7\n0 drafted, 0 red.\n", encoding="utf-8")

    tr = root / "transcripts" / "selftest-run"
    for i, (op, args, data) in enumerate([
        ("advance", {"until": {"letter": True}, "timeout_ticks": 60000}, {"reason": "letter", "ticks_elapsed": 41000}),
        ("designate", {"kind": "harvest"}, {}),
        ("advance", {"until": {"letter": True}, "timeout_ticks": 60000}, {"reason": "timeout", "ticks_elapsed": 60000}),
    ], 1):
        d = tr / f"{i:03d}-{op}"
        d.mkdir(parents=True)
        (d / "cmd.json").write_text(json.dumps({"id": f"c{i}", "op": op, "args": args}), encoding="utf-8")
        (d / "result.json").write_text(json.dumps({"id": f"c{i}", "op": op, "ok": True, "data": data}), encoding="utf-8")
    (tr / "log.ndjson").write_text(
        "\n".join(json.dumps({"op": op, "ok": True}) for op in ("advance", "designate", "advance")) + "\n",
        encoding="utf-8")

    journal = root / "journal.ndjson"
    journal.write_text("\n".join(json.dumps(e) for e in [
        {"seq": 1, "tick": 0, "type": "session", "payload": {"kind": "boot"}},
        {"seq": 2, "tick": 20000, "type": "letter", "payload": {"def": "NeutralEvent", "label": "Wanderer"}},
        {"seq": 3, "tick": 61000, "type": "action", "payload": {"verb": "designate", "step": "harvest"}},
    ]) + "\n", encoding="utf-8")
    return run, journal, tr


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
        for mutate, expect in [
            (lambda: (run / "checklist.ndjson").write_text(
                "\n".join(l for l in (run / "checklist.ndjson").read_text(encoding="utf-8").splitlines()
                          if '"freezer-below-zero"' not in l) + "\n", encoding="utf-8"),
             "daily-coverage"),
            (lambda: (run / "digests" / "final.json").write_text(json.dumps({
                "colonists": {"list": [{"name": "A", "drafted": True}]},
                "threats": {"hostiles": 0}}), encoding="utf-8"),
             "final-undrafted"),
            (lambda: [(tr / "003-advance" / "result.json").write_text(json.dumps(
                {"id": "c3", "op": "advance", "ok": True,
                 "data": {"reason": "dialog", "ticks_elapsed": 0}}), encoding="utf-8"),
                (tr / "001-advance" / "result.json").write_text(json.dumps(
                    {"id": "c1", "op": "advance", "ok": True,
                     "data": {"reason": "dialog", "ticks_elapsed": 0}}), encoding="utf-8")],
             "advance-invariants"),
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
                failures.append(f"mutation for {expect} did not FAIL it (FAILs: {sorted(got)})")
            print(f"-- mutation '{expect}': {'ok' if expect in got else 'MISSED'}\n")

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
