#!/usr/bin/env python3
"""A synthetic AutoRimmer bench: the protocol without the game.

`rwa` is a protocol client, and almost everything worth testing about a
protocol client is a failure mode — a stale heartbeat, a live heartbeat over a
frame-starved sim, a timeout, each error code in the taxonomy, a mangled result
file. Provoking those on a real bench means killing and restarting a 40-second
game boot and hoping the timing lands; here each one is a flag, and the run is
deterministic.

What it emulates is `Source/AutoRimmer/Poller.cs`, faithfully enough that the
client cannot tell the difference:

  * `status.json` rewritten every second, same field set, same `ts` format
    (.NET's "o" — 7 fractional digits), written tmp+rename.
  * the inbox scanned every 500ms, files younger than 250ms skipped (the
    writer-finished heuristic), consumed into `commands/done/` BEFORE the verb
    runs, exactly one `results/<id>.json` per consumed command, never dropped.
  * the same result envelope — id/op/ok/data|error/state/sid/ts — and the same
    id sanitisation, so a client that gets ids wrong misses its result here too.
  * `stale-on-restart` for whatever was left in the inbox at startup.

It is NOT a game: `data` payloads are canned, `advance` counts wall-clock
seconds rather than ticks. Nothing here belongs on a bench; it exists so the
client's error paths can be exercised without one.

  fakebench.py serve  --root DIR [--answer normal|mangle|silent|error:CODE]
                                 [--advance-secs N] [--fps N] [--tick N]
  fakebench.py status --root DIR --state ok|menu|stalled|starved|stale|down
"""

import json
import os
import re
import shutil
import signal
import sys
import time
from pathlib import Path

SID = time.strftime("%Y%m%dT%H%M%S")
MOD_VERSION = "0.1.0"
POLL_MS = 500
MIN_FILE_AGE_MS = 250

VERBS = ["advance", "digest", "find-rect", "journal", "journal-selftest", "landmark",
         "map-view", "nearest", "path-cost", "pause", "ping", "reachable", "room-at",
         "status", "unpause", "version"]

# Poller.cs writes DateTime.UtcNow.ToString("o"): 7 fractional digits, trailing Z.
def now_o():
    t = time.time()
    return time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(t)) + f".{int((t % 1) * 1e7):07d}Z"


def sanitize(cid):
    return re.sub(r"[^A-Za-z0-9_-]", "_", cid) or "unnamed"


def atomic(path, text):
    tmp = Path(str(path) + ".tmp")
    tmp.write_text(text)
    tmp.replace(path)


class Bench:
    def __init__(self, root, args):
        self.root = Path(root)
        self.inbox = self.root / "commands"
        self.done = self.inbox / "done"
        self.results = self.root / "results"
        self.journal = self.root / "journal"
        for d in (self.inbox, self.done, self.results, self.journal):
            d.mkdir(parents=True, exist_ok=True)
        self.answer = args.get("answer", "normal")
        self.advance_secs = float(args.get("advance_secs", 1.0))
        self.fps = float(args.get("fps", 30.0))
        self.tick = int(args.get("tick", 1000))
        self.game_loaded = args.get("game_loaded", True)
        self.paused = True
        self.speed = "Paused"
        self.active_advance = None
        self.seq = 0
        self.journal_file = self.journal / (SID + ".ndjson")
        self.emit("session", {"kind": "boot", "mod": MOD_VERSION,
                              "game": "fakebench", "bench": "synthetic"})
        # Poller.Init: anything left in the inbox from a previous process is
        # consumed with an explicit error, never replayed.
        for stale in sorted(self.inbox.glob("*.json")):
            cid = stale.stem
            self.consume(stale)
            self.write_result(cid, "?", False, code="stale-on-restart",
                              detail="command file predates this game session")

    # --- journal -------------------------------------------------------------
    def emit(self, etype, payload):
        self.seq += 1
        line = json.dumps({"seq": self.seq, "tick": self.tick, "wall": now_o(),
                           "type": etype, "payload": payload}, ensure_ascii=False)
        with self.journal_file.open("a") as fh:
            fh.write(line + "\n")

    # --- files ---------------------------------------------------------------
    def consume(self, path):
        dest = self.done / path.name
        if dest.exists():
            dest.unlink()
        shutil.move(str(path), str(dest))

    def write_status(self):
        # At the main menu the mod has never published a snapshot, so
        # Runtime.GameState is still its zero value — speed "" and tick 0. That
        # is exactly what distinguishes "no game loaded" from "a game was
        # loaded and the main thread has stopped", so it has to be faithful.
        st = {
            "ts": now_o(), "sid": SID, "mod": MOD_VERSION,
            "gameLoaded": bool(self.game_loaded), "paused": self.paused,
            "speed": self.speed if self.game_loaded else "",
            "tick": self.tick if self.game_loaded else 0,
            "fps": round(self.fps, 3) if self.game_loaded else 0,
            "activeOp": ("advance:" + self.active_advance["id"]) if self.active_advance else None,
        }
        if self.active_advance:
            a = self.active_advance
            st["advance"] = {"id": a["id"], "ticks_done": a["done"], "target": a["target"]}
        st["thermal"] = {"c": 75.5, "scale": 1}
        atomic(self.root / "status.json", json.dumps(st, separators=(",", ":")))

    def write_result(self, cid, op, ok, data=None, code=None, detail=None):
        env = {"id": cid, "op": op, "ok": ok}
        if ok:
            env["data"] = data if data is not None else {}
        else:
            env["error"] = {"code": code, "detail": detail}
        env["state"] = {"gameLoaded": bool(self.game_loaded), "tick": self.tick,
                        "paused": self.paused}
        env["sid"] = SID
        env["ts"] = now_o()
        path = self.results / (sanitize(cid) + ".json")
        if self.answer == "mangle":
            atomic(path, '{"id":"' + cid + '","op":"' + op + '","ok":tr')  # truncated on purpose
        else:
            atomic(path, json.dumps(env, ensure_ascii=False, separators=(",", ":")))

    # --- verbs ---------------------------------------------------------------
    # Poller.ScanInbox hands only MainThread verbs to the game component;
    # status/version/journal run on the poller thread and so are answerable
    # while an advance is in flight and at the main menu.
    OFF_THREAD = ("status", "version", "journal")

    def execute(self, cid, op, args):
        if self.active_advance and op not in self.OFF_THREAD and op != "pause":
            return self.write_result(cid, op, False, code="busy",
                                     detail=f"advance '{self.active_advance['id']}' in flight "
                                            f"({self.active_advance['done']} ticks done)")
        if self.answer.startswith("error:"):
            return self.write_result(cid, op, False, code=self.answer.split(":", 1)[1],
                                     detail="injected by fakebench --answer")
        if op == "ping":
            if not self.game_loaded:
                return self.write_result(cid, op, False, code="no-active-game",
                                         detail="load a save first; this verb runs at the "
                                                "in-game safe point")
            data = {"pong": True}
            if "echo" in args:
                data["echo"] = args["echo"]
            return self.write_result(cid, op, True, data)
        if op == "status":
            return self.write_result(cid, op, True, {
                "gameLoaded": bool(self.game_loaded), "paused": self.paused,
                "speed": self.speed, "tick": self.tick, "fps": round(self.fps, 3),
                "activeOp": None, "verbs": VERBS, "root": str(self.root)})
        if op == "version":
            return self.write_result(cid, op, True, {
                "game": "1.6.4871 rev600", "mod": MOD_VERSION,
                "bench": "fakebench", "sid": SID})
        if op == "journal":
            since = int(args.get("since_seq", 0))
            events, last = [], 0
            for line in self.journal_file.read_text().splitlines():
                evt = json.loads(line)
                last = max(last, evt["seq"])
                if evt["seq"] > since:
                    events.append(evt)
            return self.write_result(cid, op, True, {
                "file": str(self.journal_file), "count": len(events),
                "truncated": False, "last_seq": last, "events": events})
        if op == "digest":
            return self.write_result(cid, op, True, {
                "time": {"tick": self.tick, "paused": self.paused, "speed": self.speed,
                         "day_of_season": 11, "season": "Spring", "year": 5500,
                         "hour": 6, "weather": "clear", "outdoor_c": 10},
                "alerts": {"active": [{"id": "Alert_NeedColonistBeds",
                                       "label": "Need colonist beds", "priority": "High"}],
                           "more": 0},
                "colonists": [{"name": "Xitral", "job": "wandering.", "mood_pct": 57,
                               "mood_arrow": 1, "drafted": False, "room": "outside"}],
                "resources": {"food_days": 4.2, "meals": 12, "steel": 340},
                "power": {"net_w": 120}, "threats": {"danger": "None"},
                "changed": {"since": args.get("since", 0), "counts": {}}})
        if op == "advance":
            if not self.game_loaded:
                return self.write_result(cid, op, False, code="no-active-game",
                                         detail="load a save first")
            if self.active_advance:
                return self.write_result(cid, op, False, code="busy",
                                         detail=f"advance '{self.active_advance['id']}' in flight")
            if "ticks" not in args and "until" not in args:
                return self.write_result(cid, op, False, code="bad-args",
                                         detail="advance needs 'ticks' or 'until'")
            target = int(args.get("ticks", -1))
            self.active_advance = {"id": cid, "op": op, "target": target, "done": 0,
                                   "until": args.get("until"),
                                   "ends": time.time() + self.advance_secs,
                                   "start": time.time(), "start_tick": self.tick}
            return None  # deferred, exactly like TimeDriver
        if op == "pause":
            was = self.active_advance is not None
            if was:
                self.finish_advance("interrupted")
            return self.write_result(cid, op, True, {"was_advancing": was, "paused": True})
        return self.write_result(cid, op, False, code="unknown-op",
                                 detail="known ops: " + ", ".join(VERBS))

    def finish_advance(self, reason):
        a = self.active_advance
        self.active_advance = None
        wall = time.time() - a["start"]
        self.write_result(a["id"], "advance", True, {
            "reason": reason, "tick": self.tick, "ticks_elapsed": a["done"],
            "wall_seconds": round(wall, 4),
            "avg_tps": round(a["done"] / wall, 4) if wall else 0,
            "max_tps_effective": 1000, "journal_seq": [], "slower_spans": [],
            "thermal_c": 75.5, "thermal_scale": 1})

    def step_advance(self):
        a = self.active_advance
        if not a:
            return
        # Ticks are faked from wall time; the point is that status.json shows
        # progress and the result arrives late, not that the number is real.
        elapsed = time.time() - a["start"]
        a["done"] = int(elapsed * 1000)
        self.tick = a["start_tick"] + a["done"]
        if a["target"] >= 0 and a["done"] >= a["target"]:
            a["done"] = a["target"]
            self.tick = a["start_tick"] + a["target"]
            return self.finish_advance("ticks")
        if time.time() >= a["ends"]:
            if a["until"]:
                self.emit("letter", {"def": "NeutralEvent", "label": "fakebench letter"})
                return self.finish_advance("letter")
            return self.finish_advance("ticks")

    # --- loop ----------------------------------------------------------------
    def scan(self):
        for path in sorted(self.inbox.glob("*.json")):
            age_ms = (time.time() - path.stat().st_mtime) * 1000
            if age_ms < MIN_FILE_AGE_MS:
                continue
            try:
                text = path.read_text()
            except OSError:
                continue
            cid = path.stem
            self.consume(path)
            try:
                obj = json.loads(text)
                if not isinstance(obj, dict):
                    raise ValueError("not an object")
            except ValueError:
                self.write_result(cid, "?", False, code="bad-json", detail=None)
                continue
            cid = obj.get("id", cid)
            op = obj.get("op")
            if op is None:
                self.write_result(cid, "?", False, code="unknown-op",
                                  detail="envelope has no 'op'")
                continue
            args = obj.get("args") or {}
            if not isinstance(args, dict):
                self.write_result(cid, op, False, code="bad-args",
                                  detail="'args' must be an object")
                continue
            if self.answer == "silent":
                continue  # consumed, never answered: the timeout path
            self.execute(cid, op, args)

    def run(self):
        last_status = 0.0
        while True:
            self.scan()
            self.step_advance()
            if time.time() - last_status >= 1.0:
                last_status = time.time()
                self.write_status()
            time.sleep(POLL_MS / 1000.0)


STATES = {
    # state -> (status.json fields, or None to remove the file entirely)
    "ok": {"gameLoaded": True, "paused": True, "speed": "Paused", "tick": 64607, "fps": 30.023},
    "menu": {"gameLoaded": False, "paused": False, "speed": "", "tick": 0, "fps": 0},
    "stalled": {"gameLoaded": False, "paused": True, "speed": "Paused", "tick": 12345, "fps": 1.2},
    "starved": {"gameLoaded": True, "paused": False, "speed": "Normal", "tick": 4102, "fps": 1.7},
    "stale": {"gameLoaded": True, "paused": True, "speed": "Paused", "tick": 64607,
              "fps": 30.023, "_age": 3600},
    "down": None,
}


def write_state(root, state):
    root = Path(root)
    root.mkdir(parents=True, exist_ok=True)
    path = root / "status.json"
    fields = STATES[state]
    if fields is None:
        if path.exists():
            path.unlink()
        return
    age = fields.pop("_age", 0)
    t = time.time() - age
    st = {"ts": time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(t)) + f".{int((t % 1) * 1e7):07d}Z",
          "sid": SID, "mod": MOD_VERSION}
    st.update(fields)
    st["activeOp"] = None
    st["thermal"] = {"c": 75.5, "scale": 1}
    atomic(path, json.dumps(st, separators=(",", ":")))


def main(argv):
    if not argv:
        print(__doc__)
        return 2
    mode, opts = argv[0], {}
    i = 1
    while i < len(argv):
        if argv[i].startswith("--"):
            key = argv[i][2:].replace("-", "_")
            if i + 1 < len(argv) and not argv[i + 1].startswith("--"):
                opts[key] = argv[i + 1]
                i += 2
            else:
                opts[key] = True
                i += 1
        else:
            i += 1
    root = opts.get("root")
    if not root:
        print("fakebench: --root DIR is required", file=sys.stderr)
        return 2
    if mode == "status":
        state = opts.get("state", "ok")
        if state not in STATES:
            print(f"fakebench: unknown state {state!r} ({'|'.join(STATES)})", file=sys.stderr)
            return 2
        write_state(root, state)
        return 0
    if mode == "serve":
        if opts.get("game_loaded") in ("false", "0"):
            opts["game_loaded"] = False
        bench = Bench(root, opts)
        signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))
        bench.write_status()
        print(f"fakebench serving {root} (sid {SID}, answer={bench.answer})", flush=True)
        bench.run()
        return 0
    print(f"fakebench: unknown mode {mode!r}", file=sys.stderr)
    return 2


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except KeyboardInterrupt:
        sys.exit(0)
