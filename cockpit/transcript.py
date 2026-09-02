"""The cockpit's read side: a transcript chain, its steps, and the journal file.

Nothing here writes, opens a socket, or sends a verb. `journal` would move
`Journal.ReadWatermark` (JournalVerbs.cs:152) and let the driving agent's next
`advance` run past unread events; `digest` would claim a step directory inside
the driver's own run. Reading the files they already wrote has neither property.

Stdlib only, no Textual: replay and follow share this code, and it is testable
without a terminal.
"""

import glob as globlib
import json
import os
import re
import time
from pathlib import Path

TICKS_PER_DAY = 60000          # Verse/GenDate.TicksPerDay; ColonyRates.cs:284
SEGMENT_RE = re.compile(r"(.*)-s(\d+)")
STEP_RE = re.compile(r"(\d+)-(.+)")


# --------------------------------------------------------------- segment chain
# A run longer than 999 calls is a chain of `<run>`, `<run>-s01`, … directories:
# `rwa` rotates at the cap rather than dying (git-bug 5eba561). seg_meta/seg_key/
# follow_chain are accept/4.2-play-loop.py's, unchanged — two consumers of one
# on-disk format should not drift.

def seg_meta(path):
    try:
        m = json.loads((Path(path) / "meta.json").read_text(encoding="utf-8"))
        return m if isinstance(m, dict) else {}
    except (OSError, json.JSONDecodeError):
        return {}


def seg_key(path):
    """(base, index, name) — the order the segments ran in. From meta.json where
    the client wrote it, from the directory name where it did not."""
    meta = seg_meta(path)
    base, idx = meta.get("base"), meta.get("segment")
    if not isinstance(base, str) or not isinstance(idx, int):
        m = SEGMENT_RE.fullmatch(path.name)
        base, idx = (m.group(1), int(m.group(2))) if m else (path.name, 0)
    return base, idx, path.name


def follow_chain(path, found):
    """A segment and everything meta.json links it to, both directions."""
    path = Path(path)
    if path.name in found or not path.is_dir():
        return
    found[path.name] = path
    meta = seg_meta(path)
    for key in ("prev", "next"):
        nxt = meta.get(key)
        if isinstance(nxt, str) and nxt:
            follow_chain(path.parent / nxt, found)


def sibling_segments(path, found):
    """The rest of the run, found by NAME where the links are not there yet.

    THE LINK WALK ALONE IS NOT ENOUGH FOR THE RUN THIS TOOL EXISTS TO SHOW.
    m1-20260901's five segments predate git-bug 5eba561 and carry no prev/next
    at all; `rwa` derives them on first open, which is a WRITE the cockpit will
    not do. Walking links alone would silently show 999 of 4,599 steps — 21.7%,
    stopping before the day-31 collapse. The derivation is rwa's own
    (`Transcript.segments`), anchored on `-s` + digits so a run named `m1` does
    not swallow one named `m1-retry`.
    """
    path = Path(path)
    base = seg_key(path)[0]
    cands = [path.parent / base]
    cands += [p for p in path.parent.glob(base + "-s*")
              if re.fullmatch(re.escape(base) + r"-s\d+", p.name)]
    for c in cands:
        if c.is_dir() and c.name not in found:
            found[c.name] = c
            follow_chain(c, found)


def resolve_chain(specs, root=None):
    """Run names, directories, globs and chains -> (ordered segments, unmatched)."""
    found, missing = {}, []
    for spec in specs or []:
        p = Path(spec)
        if not p.exists() and root and os.sep not in str(spec):
            p = Path(root) / spec
        hits = [p] if p.is_dir() else [Path(h) for h in sorted(globlib.glob(str(p)))
                                       if Path(h).is_dir()]
        if not hits:
            missing.append(str(spec))
        for h in hits:
            follow_chain(h, found)
            sibling_segments(h, found)
    return sorted(found.values(), key=seg_key), missing


# ------------------------------------------------------------------- one step

class Step:
    """One `NNN-<op>/` directory. Envelopes load LAZILY: `index_scan` takes
    ok/tick/ts/sid from a few hundred bytes at each end of result.json, which is
    0.04s against 2.39s for parsing all 4,599 of m1-20260901's."""

    __slots__ = ("dir", "seg", "n", "op", "key", "ok", "tick", "ts", "sid",
                 "has_result", "has_cmd", "_cmd", "_result", "_loaded")

    HEAD_OK = re.compile(rb'"ok"\s*:\s*(true|false)')
    TAIL = re.compile(rb'"state"\s*:\s*\{(?P<state>[^{}]*)\}\s*,\s*"sid"\s*:\s*'
                      rb'"(?P<sid>[^"]*)"\s*,\s*"ts"\s*:\s*"(?P<ts>[^"]*)"\s*\}\s*$')
    TICK = re.compile(rb'"tick"\s*:\s*(-?\d+)')

    def __init__(self, directory, seg, n, op, multi):
        self.dir, self.seg, self.n, self.op = directory, seg, n, op
        self.key = f"{seg}/{directory.name}" if multi else directory.name
        self.ok = self.tick = self.ts = self.sid = None
        self.has_result = self.has_cmd = self._loaded = False
        self._cmd = self._result = None

    def index_scan(self):
        rp = self.dir / "result.json"
        self.has_cmd = (self.dir / "cmd.json").is_file()
        try:
            with open(rp, "rb") as f:
                size = os.fstat(f.fileno()).st_size
                head = f.read(min(size, 512))
                if size <= 512:
                    tail = head
                else:
                    f.seek(max(512, size - 320))
                    tail = f.read()
        except OSError:
            return                       # no result.json — see `in_flight`
        self.has_result = True
        m = self.HEAD_OK.search(head)
        if m:
            self.ok = m.group(1) == b"true"
        m = self.TAIL.search(tail)
        if m:
            t = self.TICK.search(m.group("state"))
            self.tick = int(t.group(1)) if t else None
            self.sid = m.group("sid").decode("utf-8", "replace")
            self.ts = m.group("ts").decode("utf-8", "replace")
            return
        r = self.result                  # shape the fast path did not expect
        if isinstance(r, dict):
            self.ok, self.sid, self.ts = r.get("ok", self.ok), r.get("sid"), r.get("ts")
            st = r.get("state") or {}
            if isinstance(st.get("tick"), (int, float)):
                self.tick = int(st["tick"])

    def _load(self):
        if self._loaded:
            return
        self._loaded = True
        for name, slot in (("cmd.json", "_cmd"), ("result.json", "_result")):
            try:
                setattr(self, slot,
                        json.loads((self.dir / name).read_text(encoding="utf-8")))
            except (OSError, json.JSONDecodeError, ValueError):
                setattr(self, slot, None)

    @property
    def cmd(self):
        self._load()
        return self._cmd

    @property
    def result(self):
        self._load()
        return self._result

    def forget(self):
        self._cmd = self._result = None
        self._loaded = False

    @property
    def in_flight(self):
        """cmd.json with no result.json. `rwa` writes cmd.json BEFORE dispatch,
        so this is a command that never came back — killed, disconnected, or
        still running. It is how a wedged run looks on disk and must not render
        as an empty envelope. m1-20260901 has three."""
        return self.has_cmd and not self.has_result

    @property
    def day(self):
        return None if self.tick is None else self.tick // TICKS_PER_DAY


# ------------------------------------------------------------------- the run

class Run:
    """The whole chain as one ordered step list, plus a day index."""

    def __init__(self, segments):
        self.segments = list(segments)
        self.steps, self.counts, self.day_starts, self._prev_by_ts = [], {}, [], {}
        self.scan()

    def _steps_in(self, seg, multi):
        out = []
        try:
            names = sorted(os.listdir(seg))
        except OSError:
            return out
        for name in names:
            m = STEP_RE.fullmatch(name)
            if m and (seg / name).is_dir():
                out.append(Step(seg / name, seg.name, int(m.group(1)), m.group(2), multi))
        return out

    def scan(self):
        """Segment-first: the step counter restarts at 001 in each segment, so
        flattening in segment order is what keeps "the very next command"
        meaning the very next command across a rotation."""
        multi = len(self.segments) > 1
        steps = []
        for seg in self.segments:
            got = self._steps_in(seg, multi)
            self.counts[seg.name] = len(got)
            steps.extend(got)
        for s in steps:
            s.index_scan()
        self.steps = steps
        self._reindex()

    def refresh(self, root=None, specs=None):
        """Follow's poll. Only the tail segment can grow (999 is the cap), so
        relisting all five would be 4,599 stats for news that can only be in the
        last one — but new segments do appear, so the parent is re-resolved."""
        if specs:
            segs, _ = resolve_chain(specs, root)
            if [s.name for s in segs] != [s.name for s in self.segments]:
                before = len(self.steps)
                self.segments = segs
                self.scan()
                return len(self.steps) - before
        if not self.segments:
            return 0
        tail = self.segments[-1]
        got = self._steps_in(tail, len(self.segments) > 1)
        known = self.counts.get(tail.name, 0)
        if len(got) > known:
            fresh = got[known:]
            for s in fresh:
                s.index_scan()
            self.steps.extend(fresh)
            self.counts[tail.name] = len(got)
            self._reindex()
            return len(fresh)
        if self.steps and not self.steps[-1].has_result \
                and (self.steps[-1].dir / "result.json").is_file():
            self.steps[-1].forget()      # the in-flight step just landed
            self.steps[-1].index_scan()
            self._reindex()
        return 0

    def _reindex(self):
        """Day anchors, and the order results actually returned in.

        THE TICK COLUMN IS NOT MONOTONIC. m1-20260901 steps backwards 67 times:
        `-s00/521-advance` ends at tick 2,276,023 and `522-pawn` reports
        2,223,181, and their `ts` fields say why — 04:04:50 against 04:02:59. A
        step directory is claimed when the command is SENT, so two clients
        against one bench interleave, and a 120s advance is overtaken by a verb
        that refused instantly. A strict maximum keeps "day N starts here" one
        anchor for `[`/`]` instead of 92 anchors for 68 days; ts order keeps each
        journal line on exactly one step.
        """
        out, high = [], None
        for i, s in enumerate(self.steps):
            if s.day is not None and (high is None or s.day > high):
                out.append((s.day, i))
                high = s.day
        self.day_starts = out
        order = sorted((i for i, s in enumerate(self.steps) if s.ts),
                       key=lambda i: (self.steps[i].ts, i))
        self._prev_by_ts = {i: (order[p - 1] if p else None)
                            for p, i in enumerate(order)}

    def journal_window(self, i):
        prev = self._prev_by_ts.get(i)
        return (self.steps[prev].ts if prev is not None else None), self.steps[i].ts

    def out_of_order(self, i):
        a, b = (self.steps[i - 1].ts, self.steps[i].ts) if i > 0 else (None, None)
        return bool(a and b and b < a)

    def day_of(self, i):
        for j in range(i, -1, -1):
            if self.steps[j].day is not None:
                return self.steps[j].day
        return None

    def jump_day(self, i, delta):
        starts = [ix for _, ix in self.day_starts]
        if not starts:
            return i
        if delta > 0:
            return next((ix for ix in starts if ix > i), starts[-1])
        cur = max((ix for ix in starts if ix < i), default=starts[0])
        return max((ix for ix in starts if ix < cur), default=cur)

    def prev_of_op(self, i, op):
        """The last earlier step with this op — the fold's baseline for what
        changed."""
        for j in range(i - 1, -1, -1):
            if self.steps[j].op == op and self.steps[j].has_result:
                return self.steps[j]
        return None

    def last_map_at_or_before(self, i):
        """m1-20260901 has TEN map-views in 4,599 steps — nine on day 0, one on
        day 67. Blanking the hero panel for the other 4,589 would hide that; the
        last map with a staleness caption states it."""
        for j in range(i, -1, -1):
            if self.steps[j].op == "map-view" and self.steps[j].has_result:
                return j, self.steps[j]
        return None, None

    @property
    def sid(self):
        return next((s.sid for s in self.steps if s.sid), None)


# ------------------------------------------------------------------- journal

def parse_wall(text):
    """.NET's "o" format (7 fractional digits) as an epoch float.
    `fromisoformat` wants 3 or 6, so this does not go through it."""
    if not isinstance(text, str):
        return None
    m = re.match(r"(\d{4})-(\d\d)-(\d\d)[T ](\d\d):(\d\d):(\d\d)(?:\.(\d+))?", text)
    if not m:
        return None
    try:
        base = time.mktime(tuple(int(g) for g in m.groups()[:6]) + (0, 1, 0)) \
            - time.timezone
    except (ValueError, OverflowError):
        return None
    return base + float("0." + (m.group(7) or "0"))


class Journal:
    """`journal/<sid>.ndjson` read AS A FILE. The verb reads the same bytes
    (FileShare.ReadWrite) — what it also does is call `Journal.NoteRead`, the
    only thing in the mod that moves `ReadWatermark`."""

    def __init__(self, path):
        self.path = Path(path) if path else None
        self.entries, self._offset, self._partial, self.error = [], 0, b"", None
        self.tail()

    def tail(self):
        """Whatever was appended since the last call. A line that is not yet
        whole is held back — the mod appends while this reads."""
        if not self.path:
            return 0
        try:
            size = self.path.stat().st_size
        except OSError as e:
            self.error = str(e)
            return 0
        if size < self._offset:                       # truncated or rotated
            self.entries, self._offset, self._partial = [], 0, b""
        if size == self._offset:
            return 0
        try:
            with open(self.path, "rb") as f:
                f.seek(self._offset)
                chunk = f.read()
        except OSError as e:
            self.error = str(e)
            return 0
        self._offset += len(chunk)
        lines = (self._partial + chunk).split(b"\n")
        self._partial = lines.pop()
        n = 0
        for raw in lines:
            if not raw.strip():
                continue
            try:
                e = json.loads(raw.decode("utf-8", "replace"))
            except (json.JSONDecodeError, ValueError):
                continue
            if isinstance(e, dict):
                e["_at"] = parse_wall(e.get("wall"))
                self.entries.append(e)
                n += 1
        return n

    def between(self, lo, hi):
        """Entries in (lo, hi] by WALL CLOCK. Wall, not tick: `ts` and `wall`
        come from the same process, and most steps ran with the game paused, so
        a tick window would collapse them all onto one tick."""
        lo, hi = parse_wall(lo), parse_wall(hi)
        if hi is None:
            return []
        return [e for e in self.entries
                if e.get("_at") is not None and e["_at"] <= hi
                and (lo is None or e["_at"] > lo)]


def find_journal(sid, hints):
    """`<sid>.ndjson` under any of these, without asking the bench for it."""
    for h in hints:
        if not h:
            continue
        p = Path(h)
        if p.is_file():
            return p
        if not sid:
            continue
        for cand in (p / f"{sid}.ndjson", p / "journal" / f"{sid}.ndjson"):
            if cand.is_file():
                return cand
    return None


# ----------------------------------------------------------- protocol root
# Mirrors rwa's `root_candidates`: one right answer, two spellings would drift.
VAULT = Path(os.environ.get("RIMWORLD_VAULT", "/home/dorian/projects/rimworld"))
UNITY_REL = Path("unity3d/Ludeon Studios/RimWorld by Ludeon Studios/AutoRimmer")


def resolve_root(explicit=None):
    if explicit or os.environ.get("RWA_ROOT"):
        return Path(explicit or os.environ["RWA_ROOT"]).expanduser()
    bench = VAULT / "_RimWorld-Agent"
    cands = [bench / "config" / UNITY_REL, bench / "savedata/AutoRimmer",
             Path.home() / ".config" / UNITY_REL, Path.home() / UNITY_REL]
    return (next((p for p in cands if (p / "status.json").exists()), None)
            or next((p for p in cands if p.is_dir()), None))


def repo_root():
    return Path(__file__).resolve().parent.parent


def transcripts_root(explicit=None):
    return Path(explicit or os.environ.get("RWA_TRANSCRIPTS")
                or repo_root() / "transcripts").expanduser()
