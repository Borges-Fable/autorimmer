#!/usr/bin/env python3
"""The bench-down retry, tested against real files and no game.

openrun-20260902 cause C: `rwa`'s `health()` sampled `status.json` ONCE per
poll while `Poller.AtomicWrite` did `File.Delete` then `File.Move` about once a
second, so the client could — and three times did — declare a live bench dead
while its own advance was still running. 97,505 ticks moved unwitnessed, and
the very next call each time came back `busy` naming the same command id.

Both halves of that are fixed. This file tests the CLIENT half, because the
client half is the one that can be tested without RimWorld: `health()` must
require at least DOWN_SAMPLES consecutive failed samples spanning more than
DOWN_SPAN_S seconds before it says `down`.

Four cases, all against a real directory on disk:

  1. control        — ONE sample of a vanished path still says `down`, so the
                      fixture really does reproduce the old verdict and the
                      retry is what changed the answer.
  2. vanishes once  — the path disappears and comes back inside one heartbeat.
                      `health()` must NOT say down.
  3. stays gone     — the path never comes back. `health()` must say down, and
                      must have spent >DOWN_SPAN_S seconds over >=DOWN_SAMPLES
                      samples getting there.
  4. the real race  — a writer thread doing exactly what the old AtomicWrite
                      did, at speed, while health() is called in a loop. Counts
                      what a single sample would have seen and what the retry
                      actually returns.

  usage:  python3 rwa/healthtest.py         (temp dir, cleaned up)
"""

import importlib.machinery
import importlib.util
import json
import os
import shutil
import sys
import tempfile
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent

# `rwa` has no .py extension on purpose (it is a command, not a module), so it
# is loaded by path. Its __main__ guard keeps the import side-effect-free.
_loader = importlib.machinery.SourceFileLoader("rwa_client", str(HERE / "rwa"))
_spec = importlib.util.spec_from_loader("rwa_client", _loader)
rwa = importlib.util.module_from_spec(_spec)
_loader.exec_module(rwa)

PASS, FAIL = 0, 0


def ok(msg):
    global PASS
    PASS += 1
    print(f"  PASS  {msg}")


def bad(msg):
    global FAIL
    FAIL += 1
    print(f"  FAIL  {msg}")


def check(cond, msg):
    (ok if cond else bad)(msg)


def now_o():
    """Poller.cs writes DateTime.UtcNow.ToString('o'): 7 fractional digits."""
    t = time.time()
    return time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(t)) + f".{int((t % 1) * 1e7):07d}Z"


def status_bytes(tick=1000, advance=None):
    st = {"ts": now_o(), "sid": "20260909T000000", "mod": "0.1.0",
          "gameLoaded": True, "paused": True, "speed": "Paused",
          "tick": tick, "fps": 60.0, "activeOp": None}
    if advance:
        st["advance"] = {"id": advance, "ticks_done": 4004, "target": 60000}
    return json.dumps(st)


def write_status(root, **kw):
    (root / "status.json").write_text(status_bytes(**kw))


def old_atomic_write(path, text):
    """Poller.AtomicWrite as it was: the window this whole fix is about."""
    tmp = Path(str(path) + ".tmp")
    tmp.write_text(text)
    if path.exists():
        os.unlink(path)          # <- status.json does not exist from here...
    os.rename(tmp, path)         # <- ...to here


def new_atomic_write(path, text):
    """Poller.AtomicWrite as it is now: File.Replace, i.e. one rename()."""
    tmp = Path(str(path) + ".tmp")
    tmp.write_text(text)
    os.rename(tmp, path)         # overwrites; the path is never absent


# --- 1. control: one sample of a vanished path is still `down` ---------------

def case_control(root):
    print("\n1. control — a single sample still condemns a vanished path")
    write_status(root)
    h = rwa.health_sample(root)
    check(h["state"] == "ok", f"with status.json present, one sample says ok ({h['state']})")
    os.unlink(root / "status.json")
    h = rwa.health_sample(root)
    check(h["state"] == "down",
          f"with status.json gone, one sample says down: {h['why']!r}")


# --- 2. a path that vanishes for one sample and comes back -------------------

def case_vanishes_once(root):
    print("\n2. the path vanishes for one sample and returns")
    write_status(root)
    os.unlink(root / "status.json")          # gone when health() first looks

    restored = threading.Event()

    def restore():
        # Back inside one heartbeat — well before health() has taken its three
        # samples, exactly like the poller finishing an AtomicWrite.
        time.sleep(rwa.DOWN_RETRY_S * 0.5)
        write_status(root)
        restored.set()

    t = threading.Thread(target=restore)
    t.start()
    t0 = time.time()
    h = rwa.health(root)
    elapsed = time.time() - t0
    t.join()

    check(restored.is_set(), "the fixture did restore status.json mid-verdict")
    check(h["state"] != "down",
          f"health() does NOT say down — it says {h['state']!r} ({h['why']})")
    check(h.get("missed_samples") == 1,
          f"…and it reports the one missed sample it rode out "
          f"(missed_samples={h.get('missed_samples')!r})")
    check(elapsed < rwa.DOWN_SPAN_S + rwa.DOWN_RETRY_S,
          f"it returned as soon as the file came back ({elapsed:.2f}s)")


# --- 3. a path that stays gone ----------------------------------------------

def case_stays_gone(root):
    print("\n3. the path stays gone")
    p = root / "status.json"
    if p.exists():
        os.unlink(p)
    t0 = time.time()
    h = rwa.health(root)
    elapsed = time.time() - t0

    check(h["state"] == "down", f"health() says down ({h['state']!r})")
    check(h.get("samples", 0) >= rwa.DOWN_SAMPLES,
          f"…after >= {rwa.DOWN_SAMPLES} consecutive failed samples "
          f"(samples={h.get('samples')!r})")
    check(h.get("span_s", 0) > rwa.DOWN_SPAN_S,
          f"…spanning more than {rwa.DOWN_SPAN_S:g}s (span_s={h.get('span_s')!r})")
    check(elapsed > rwa.DOWN_SPAN_S, f"and it really did take that long ({elapsed:.2f}s)")
    check("consecutive samples" in h["why"],
          f"the verdict carries its own evidence: {h['why']!r}")


# --- 4. the actual race, both AtomicWrite shapes -----------------------------

def race(root, writer, seconds=3.0, hz=200.0):
    """Beat on status.json with `writer` while grading the bench.

    Returns (single_sample_downs, health_downs, verdicts). The old poller wrote
    once a second; this writes far faster so the window is actually hit inside
    a few seconds of test time. Every miss it produces is a miss the old client
    could have taken on the bench — just rarer there.
    """
    p = root / "status.json"
    writer(p, status_bytes())
    stop = threading.Event()
    n = [0]

    def beat():
        while not stop.is_set():
            n[0] += 1
            writer(p, status_bytes(tick=1000 + n[0]))
            time.sleep(1.0 / hz)

    t = threading.Thread(target=beat)
    t.start()
    singles = downs = verdicts = 0
    end = time.time() + seconds
    try:
        while time.time() < end:
            if rwa.health_sample(root)["state"] == "down":
                singles += 1
            verdicts += 1
            if rwa.health(root)["state"] == "down":
                downs += 1
    finally:
        stop.set()
        t.join()
    return singles, downs, verdicts, n[0]


def case_the_race(root):
    print("\n4. the race itself — a writer doing what AtomicWrite did")
    # The window is a few microseconds wide, so catching it is a matter of
    # attempts. Up to three rounds; `downs == 0` is the hard assertion either
    # way, and a round that never catches the window says so rather than
    # failing — a flaky assertion here would be worse than no assertion.
    singles = downs = verdicts = writes = 0
    for _ in range(3):
        singles, downs, verdicts, writes = race(root, old_atomic_write)
        print(f"     delete+move writer: {writes} writes, {verdicts} verdicts, "
              f"single samples that read `down`: {singles}, "
              f"health() verdicts of `down`: {downs}")
        check(downs == 0, f"health() called this live bench dead {downs} time(s) — must be 0")
        if singles:
            break
    if singles:
        ok(f"the old one-sample rule WOULD have called this live bench dead "
           f"{singles} time(s), and the retry did not")
    else:
        print("     (inconclusive: the reader never landed inside the old window here;"
              " cases 1-3 still fix the verdict)")

    singles2, downs2, verdicts2, writes2 = race(root, new_atomic_write)
    print(f"     File.Replace writer: {writes2} writes, {verdicts2} verdicts, "
          f"single samples that read `down`: {singles2}, health() verdicts of `down`: {downs2}")
    check(singles2 == 0,
          f"with the mod-side fix there is no window to miss at all ({singles2} misses)")
    check(downs2 == 0, f"health() called it dead {downs2} time(s) — it must be 0")


# --- 5. an advance in flight is never reported down --------------------------

def case_advance_in_flight(root):
    print("\n5. an advance in flight is read off the heartbeat")
    write_status(root, advance="advance-093457-1234-1")
    h = rwa.health(root)
    check(rwa.advance_id(h["status"]) == "advance-093457-1234-1",
          f"advance_id() finds the in-flight command "
          f"({rwa.advance_id(h['status'])!r})")
    write_status(root)
    h = rwa.health(root)
    check(rwa.advance_id(h["status"]) is None,
          "…and None when nothing is advancing")


# --- 6. generated ids are unique, because their results are never unlinked ---

def case_ids(root):
    print("\n6. generated ids do not collide inside one process")
    ids = [rwa.new_id("ping") for _ in range(200)]
    check(len(set(ids)) == len(ids),
          f"200 ids for the same op in the same second are all distinct "
          f"({len(set(ids))} unique)")
    check(all(rwa.ID_RE.match(i) for i in ids),
          "…and every one of them is a legal Poller result filename")
    print(f"     e.g. {ids[0]}  {ids[1]}  {ids[-1]}")


def main():
    tmp = Path(tempfile.mkdtemp(prefix="rwa-healthtest."))
    root = tmp / "AutoRimmer"
    root.mkdir(parents=True)
    print(f"rwa health-retry test — synthetic root {root}")
    print(f"  DOWN_SAMPLES={rwa.DOWN_SAMPLES} DOWN_SPAN_S={rwa.DOWN_SPAN_S:g} "
          f"DOWN_RETRY_S={rwa.DOWN_RETRY_S:g} OWED_SECS={rwa.OWED_SECS:g}")
    try:
        case_control(root)
        case_vanishes_once(root)
        case_stays_gone(root)
        case_the_race(root)
        case_advance_in_flight(root)
        case_ids(root)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print(f"\n  {PASS} passed, {FAIL} failed\n")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
