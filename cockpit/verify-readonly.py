#!/usr/bin/env python3
"""Proof that the cockpit writes nothing into a bench root. df378fa item 2.

    verify-readonly.py <run> [--root DIR] [--transcripts DIR] [--seconds 60]

Snapshots the protocol root, runs the cockpit headless in --follow for N
seconds under a `sys.addaudithook` that fails on any write, socket or
subprocess, then diffs the snapshot.

ON THE WATERMARK. `Journal.ReadWatermark` is a private static in Journal.cs; it
is never persisted, so it cannot be read from disk, and the only thing that
returns it is the `journal` verb — whose call MOVES it. The observation is
destructive, which is exactly why the cockpit does not make it. It is proved
statically instead, and the chain is airtight: `readWatermark` is moved only by
`Journal.NoteRead`, whose only caller is `JournalVerbs.Read` (the `journal`
verb), which runs only when `Poller.ScanInbox` finds a file in `commands/`.
No file in `commands/` therefore means the watermark did not move.
"""

import os
import sys
import time
from pathlib import Path

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

WRITE_MODES = set("wxa+")
violations = []


def audit(event, args):
    """Fail on anything that could leave the process. Reads are allowed."""
    if event == "open" and len(args) > 1 and args[1] and set(str(args[1])) & WRITE_MODES:
        violations.append(f"open for write: {args[0]}")
    elif event in ("socket.socket", "socket.connect", "socket.bind",
                   "subprocess.Popen", "os.system", "os.remove", "os.rename",
                   "os.mkdir", "os.rmdir", "shutil.copyfile", "shutil.move"):
        violations.append(f"{event}: {args!r:.120}")


def snap(root):
    """Every path under the bench root, with size — cheap and total."""
    out = {}
    for base, _, files in os.walk(root):
        for f in files:
            p = Path(base) / f
            try:
                out[str(p)] = p.stat().st_size
            except OSError:
                out[str(p)] = -1
    return out


def main(argv):
    import transcript as T
    opt, run_spec, i = {}, [], 0
    while i < len(argv):
        if argv[i].startswith("--") and i + 1 < len(argv):
            opt[argv[i]] = argv[i + 1]
            i += 2
        else:
            run_spec.append(argv[i])
            i += 1
    root = T.resolve_root(opt.get("--root"))
    secs = float(opt.get("--seconds", 60))
    troot = T.transcripts_root(opt.get("--transcripts"))
    if not root or not root.is_dir():
        print("no protocol root; pass --root", file=sys.stderr)
        return 2
    status = (root / "status.json")
    print(f"root         {root}")
    print(f"run          {' '.join(run_spec) or '(--live)'}")
    print(f"bench        "
          + (f"{status.read_text(encoding='utf-8')[:110]}…"
             if status.is_file() else "no status.json"))

    before = snap(root)
    segs, _ = T.resolve_chain(run_spec, troot)
    if not segs:
        print(f"no transcript matched {run_spec} under {troot}", file=sys.stderr)
        return 2
    run = T.Run(segs)
    jpath = T.find_journal(run.sid, [T.repo_root() / "RUNS" /
                                     T.seg_key(segs[0])[0], root])

    import ui
    app = ui.Cockpit(run, T.Journal(jpath) if jpath else None, follow=True,
                     specs=run_spec, root=troot, repo=T.repo_root())
    sys.addaudithook(audit)

    async def drive():
        async with app.run_test(size=(130, 40)) as pilot:
            end = time.monotonic() + secs
            while time.monotonic() < end:
                await pilot.pause(0.25)
    import asyncio
    asyncio.run(drive())
    after = snap(root)

    added = sorted(set(after) - set(before))
    changed = sorted(k for k in set(after) & set(before) if after[k] != before[k])
    rel = lambda p: str(Path(p).relative_to(root))                    # noqa: E731
    bad = [p for p in added if rel(p).startswith(("commands", "results"))]
    grew = [p for p in changed if not rel(p).startswith("journal")]

    print(f"\nran          {secs:g}s headless in --follow")
    print(f"files before {len(before)}   after {len(after)}")
    print(f"commands/ + results/ gained   {len(bad)} file(s)   "
          + ("PASS" if not bad else "FAIL " + ", ".join(map(rel, bad))))
    print(f"new transcript step dirs      "
          + ("PASS (0)" if not any(rel(p).startswith("transcripts") for p in added)
             else "FAIL"))
    print(f"non-journal files that grew   {len(grew)}   "
          + ("PASS" if not grew else "FAIL " + ", ".join(map(rel, grew))))
    print(f"audit hook violations         {len(violations)}   "
          + ("PASS" if not violations else "FAIL"))
    for v in violations[:10]:
        print("   " + v)
    other = [rel(p) for p in added if p not in bad]
    if other:
        print(f"other new files (the MOD's, not ours): {len(other)}")
        for p in other[:6]:
            print("   " + p)
    print("\nReadWatermark  unchanged by construction — commands/ gained nothing,\n"
          "               and Journal.NoteRead has exactly one caller "
          "(JournalVerbs.Read),\n"
          "               reached only through Poller.ScanInbox.")
    return 0 if not (bad or violations or grew) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
