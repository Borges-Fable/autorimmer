"""The cockpit's terminal. Textual — one focal element, one accent, no emoji.

REPLAY AND FOLLOW ARE THE SAME CODE PATH. `Run.refresh` appends whatever landed
on disk since the last poll; the renderer never learns which mode it is in.
`--follow` is replay with a 500ms timer and the cursor pinned to the tail.

The palette is terminal background, one muted foreground with a dimmer tier for
fog and labels, ONE accent for what changed at this step, and one warning colour
for what is going wrong in the colony. A cockpit that colours every def is a
christmas tree, and a christmas tree is a cockpit nobody opens.
"""

from rich.table import Table
from rich.text import Text
from textual.app import App, ComposeResult
from textual.binding import Binding
from textual.containers import Container, VerticalScroll
from textual.css.query import NoMatches
from textual.widgets import Static

import fold as F
import surface as S

FG, BRIGHT, DIM, FAINT = "#b6b8b3", "#e8e9e4", "#5d6169", "#3a3e44"
ACCENT = "#d1a054"      # spent ONLY on what changed at this step
WARN = "#cf6a5a"
MAP_FG = "#7d817b"

# Spatial.CropRenderer: pawns get the bright tier so a body on the ground is
# visible in a field of terrain; `!` is hostile, and a hostile is an alert.
PAWNS, HOSTILE, FOG = "@$^&;", "!", "?"
NARROW_AT, TINY_AT = 118, 86

HELP = [("←  →   h  l", "step back / forward"),
        ("[   ]", "jump to the previous / next in-game day"),
        ("↑  ↓", "move the section cursor"),
        ("enter", "expand or fold the section under the cursor"),
        ("pgup  pgdn", "scroll the map, or the verb surface"),
        ("tab", "flight deck / rig — the agent's own instruments"),
        ("f", "follow the tail of a growing run"),
        ("g", "jump to the newest step"),
        ("?  q", "this / quit"),
        ("", ""),
        ("", "This cockpit sends nothing. It issues no verb, not even an"),
        ("", "observer: calling `journal` would move the driving agent's"),
        ("", "read watermark and let its next advance run past unread"),
        ("", "events. Everything here is read off disk.")]


def grid(left, right):
    t = Table.grid(expand=True)
    t.add_column(justify="left", ratio=1)
    t.add_column(justify="right")
    t.add_row(left, right)
    return t


class Cockpit(App):
    CSS_PATH = "cockpit.tcss"
    TITLE = "autorimmer cockpit"

    BINDINGS = [
        ("left,h", "step(-1)", "back"), ("right,l", "step(1)", "forward"),
        ("left_square_bracket", "day(-1)", "prev day"),
        ("right_square_bracket", "day(1)", "next day"),
        ("up", "cursor(-1)", "up"), ("down", "cursor(1)", "down"),
        ("enter,space", "expand", "expand"),
        ("pageup", "map_scroll(-1)", "map up"), ("pagedown", "map_scroll(1)", "map down"),
        # priority: Textual reserves tab for focus-next at the screen level,
        # and without this the page never turns.
        Binding("tab", "page", "page", priority=True),
        ("f", "follow", "follow"), ("g", "tail", "newest"),
        ("question_mark", "help", "help"), ("q,escape", "quit_or_help", "quit"),
    ]

    def __init__(self, run, journal, follow=False, at=None, specs=None,
                 root=None, source="", repo=None, run_dir=None, page=0):
        super().__init__()
        self.run, self.journal, self.follow = run, journal, follow
        self.specs, self.root, self.source = specs or [], root, source
        self.i = max(0, min(len(run.steps) - 1,
                            at if at is not None else (len(run.steps) - 1 if follow else 0)))
        self.cursor, self.expanded, self.status = 0, set(), ""
        self._sec_lines, self._nsec = [], 0
        # Page two's material, gathered once: the mod's whole verb registry, the
        # shelf of standing documents, and the driver's own checklist record.
        self.page = page
        self.verbs = S.verbs(repo) if repo else {}
        self.shelf = S.shelf(repo, run_dir) if repo else []
        self.checklist = S.checklist(run_dir)
        self.total_use = S.usage(run.steps)

    def compose(self) -> ComposeResult:
        yield Static(id="head")
        with Container(id="body"):
            with Container(id="mapwrap"):
                yield Static(id="legend")
                yield VerticalScroll(Static(id="map"), id="mapscroll")
            yield VerticalScroll(Static(id="sidebody"), id="side")
        yield Static(id="foot")
        with Container(id="helpwrap"):
            yield Static(id="help")

    def on_mount(self):
        self.query_one("#mapwrap").border_title = "map"
        self.query_one("#help").update(self._help())
        self._fit(self.size.width)
        self._page_class()
        if self.follow:
            self.set_interval(0.5, self._poll)
        self.render_all()
        # The caption states how much of the map is visible, which is not
        # knowable until the first layout has given #mapscroll a height.
        self.call_after_refresh(self.render_all)

    def on_resize(self, event):
        self._fit(event.size.width)
        self.render_all()
        self.call_after_refresh(self.render_all)

    def _fit(self, width):
        self.screen.set_class(width < NARROW_AT, "-narrow")
        self.screen.set_class(width < TINY_AT, "-tiny")

    def _page_class(self):
        # The map is centred because a 10x14 keyhole in a wide pane reads as
        # broken; a verb grid centred reads as misaligned. Same pane, two rules.
        self.screen.set_class(bool(self.page), "-rig")

    # ------------------------------------------------------------- actions
    def action_step(self, delta):
        self._goto(self.i + delta, drop_follow=True)

    def action_day(self, delta):
        self._goto(self.run.jump_day(self.i, delta), drop_follow=True)

    def action_tail(self):
        self._goto(len(self.run.steps) - 1)

    def action_cursor(self, delta):
        if self._nsec:
            self.cursor = max(0, min(self._nsec - 1, self.cursor + delta))
            self.render_side()
            self._scroll_to_cursor()

    def action_expand(self):
        secs = self._sections()
        if secs:
            self.expanded ^= {secs[min(self.cursor, len(secs) - 1)].label}
            self.render_side()
            self._scroll_to_cursor()

    def action_map_scroll(self, delta):
        self.query_one("#mapscroll").scroll_relative(y=delta * 8, animate=False)

    def action_page(self):
        self.page = 1 - self.page
        self._page_class()
        self.render_all()
        self.call_after_refresh(self.render_all)

    def action_follow(self):
        self.follow = not self.follow
        if self.follow:
            self._poll()
            self._goto(len(self.run.steps) - 1)
        else:
            self.status = "follow off"
            self.render_all()

    def action_help(self):
        self.screen.toggle_class("-help")

    def action_quit_or_help(self):
        if self.screen.has_class("-help"):
            self.screen.remove_class("-help")
        else:
            self.exit()

    def _goto(self, i, drop_follow=False):
        i = max(0, min(len(self.run.steps) - 1, i))
        if drop_follow and self.follow and i != len(self.run.steps) - 1:
            # Scrubbing away from the tail IS turning follow off; leaving it on
            # would yank the view back on the next poll.
            self.follow, self.status = False, "follow off — scrubbed"
        if i != self.i:
            self.run.steps[self.i].forget()
        self.i, self.cursor = i, 0
        self.render_all()

    def _poll(self):
        added = self.run.refresh(self.root, self.specs)
        if self.journal:
            self.journal.tail()
        if added:
            self.status = f"+{added} step{'s' if added > 1 else ''}"
        if self.follow:
            self.i = len(self.run.steps) - 1
        self.render_all()

    # ----------------------------------------------------------- renderers
    def _sections(self):
        s = self.run.steps[self.i]
        return F.sections(s, self.run.prev_of_op(self.i, s.op))

    def render_all(self):
        # The 500ms follow timer can fire once after the screen is gone (or
        # before compose has landed), and a torn-down app is not an error.
        try:
            self.query_one("#head")
        except NoMatches:
            return
        self.render_head()
        if self.page:
            self.render_rig()
            self.render_shelf()
        else:
            self.render_map()
            self.render_side()
        self.render_foot()

    # ------------------------------------------------- page two: the rig
    # Same two regions, same borders, different instruments. Page one is the
    # colony; this is the agent looking at its own hands.

    def render_rig(self):
        """Every verb the mod exposes, against the ones this run ever reached.

        Dim is never called in the whole run, muted is called later than here,
        and the accent is what the agent had actually used by THIS step — so
        scrubbing lights the surface up as the run learns its own tools, and
        what stays dark at the end is the answer to whether the tools make
        sense.
        """
        so_far = S.usage(self.run.steps, self.i)
        names = sorted(self.verbs) or sorted(self.total_use)
        pane = self.query_one("#map", Static)
        # Before the first layout a pane reports 0; the screen width is the
        # honest fallback, and 22 fits the longest verb (dev:faction-goodwill).
        avail = self.query_one("#mapscroll").size.width or (self.size.width - 56)
        cell = 22
        cols = max(1, avail // cell)
        t = Text()
        for n, name in enumerate(names):
            used_now, used_ever = so_far.get(name, 0), self.total_use.get(name, 0)
            t.append(f"{name:<{cell - 1}} ",
                     style=ACCENT if used_now else FG if used_ever else FAINT)
            if n % cols == cols - 1:
                t.append("\n")
        pane.update(t)
        self.query_one("#mapwrap").border_title = "verb surface"
        never = sum(1 for n in names if n not in self.total_use)
        top = ", ".join(f"{k} {v}" for k, v in self.total_use.most_common(5))
        self.query_one("#legend", Static).update(Text(
            f"{len(names)} verbs · {len(names) - never} used in this run · "
            f"{never} never called · {len(so_far)} by this step\n"
            f"most used   {top}", style=DIM))

    def render_shelf(self):
        """What the driver had to read, and what it wrote down.

        The playbook and the checklists are standing instructions; the colony
        notes are its memory across runs; `checklist.ndjson` is the one place it
        recorded a READING and a NOTE rather than a verb call, which makes it
        the closest thing on disk to what it was thinking.
        """
        side = self.query_one("#side")
        side.border_title = "shelf"
        t = Text()
        for label, files in self.shelf:
            t.append(f"\n  {label:<15}", style=DIM)
            t.append(f"{len(files)}\n", style=BRIGHT)
            for name, size in files[:6]:
                t.append(f"    {name[:26]:<27}", style=FG)
                t.append(f"{size // 1024 or 1}k\n", style=DIM)
            if len(files) > 6:
                t.append(f"    +{len(files) - 6} more\n", style=DIM)

        tick = self.run.steps[self.i].tick
        seen = [e for e in self.checklist
                if tick is None or (e.get("tick") or 0) <= tick]
        t.append("\n  checklist      ", style=DIM)
        t.append(f"{len(seen)} of {len(self.checklist)} by this step\n", style=BRIGHT)
        for e in seen[-8:]:
            t.append(f"    day {e.get('day', '?'):<3}", style=DIM)
            t.append(f"{str(e.get('item', '?'))[:18]:<19}", style=FG)
            t.append(f"{e.get('verdict', '')}\n",
                     style=WARN if e.get("verdict") == "action" else DIM)
            if e.get("reading"):
                t.append(f"      {str(e['reading'])[:44]}\n", style=DIM)
        self.query_one("#sidebody", Static).update(t)

    def render_head(self):
        s = self.run.steps[self.i]
        left = Text("autorimmer ", style=DIM)
        left.append(self.run.segments[0].name if self.run.segments else "?", style=BRIGHT)
        right = Text("step ", style=DIM)
        right.append(f"{self.i + 1:,}", style=BRIGHT)
        right.append(f" of {len(self.run.steps):,}", style=DIM)

        day = self.run.day_of(self.i)
        l2 = Text("day " + (str(day) if day is not None else "?"), style=FG)
        cal = self._calendar()
        if cal:
            l2.append("   " + cal, style=DIM)
        r2 = Text(f"tick {s.tick:,}" if s.tick is not None else "tick —", style=DIM)
        r2.append("    ")
        r2.append("FOLLOW" if self.follow else "replay",
                  style=ACCENT if self.follow else DIM)
        r2.append("    ")
        r2.append("rig" if self.page else "flight deck", style=DIM)
        t = Table.grid(expand=True)
        t.add_column(justify="left", ratio=1)
        t.add_column(justify="right")
        t.add_row(left, right)
        t.add_row(l2, r2)
        self.query_one("#head").update(t)

    def _calendar(self):
        """The colony's own reckoning, from the most recent digest. The tick
        count cannot give it — the local day boundary is offset by start hour
        and longitude — so it is shown when a digest has said it, and not
        otherwise."""
        for j in range(self.i, max(-1, self.i - 400), -1):
            st = self.run.steps[j]
            if st.op != "digest" or not st.has_result:
                continue
            tm = ((st.result or {}).get("data") or {}).get("time") or {}
            return (f"{tm.get('season', '?')} {tm.get('day_of_season', '?')}, "
                    f"{tm.get('year', '?')} · {tm.get('hour', '?')}h · "
                    f"{tm.get('weather', '?')} {tm.get('outdoor_c', '?')}°C") if tm else ""
        return ""

    def render_map(self):
        j, ms = self.run.last_map_at_or_before(self.i)
        pane, cap = self.query_one("#map", Static), self.query_one("#legend", Static)
        if ms is None:
            pane.update(Text("no map-view in this run yet", style=DIM))
            cap.update(Text(""))
            return
        data = (ms.result or {}).get("data") or {}
        rows, rulers = data.get("rows") or [], data.get("rulers") or {}
        body = Text()
        for r in ("x_tens", "x_units"):
            if rulers.get(r):
                body.append(str(rulers[r]) + "\n", style=FAINT)
        for r in rows:
            body.append(self._map_row(str(r)))
            body.append("\n")
        pane.update(body)
        stale = self.i - j
        self.query_one("#mapwrap").border_title = (
            f"map · {stale:,} steps ago" if stale else "map")
        cap.update(self._caption(data, ms, stale, len(rows)))

    def _map_row(self, row):
        t = Text()
        for ch in row:
            t.append(ch, style=(FAINT if ch == FOG else WARN if ch == HOSTILE
                                else BRIGHT if ch in PAWNS else MAP_FG))
        return t

    def _caption(self, data, ms, stale, nrows):
        """One dim line. m1-20260901 makes TEN map-view calls in 4,599 steps —
        nine before the first advance, one on the last day — so a hero panel
        showing day-0 ground under a day-62 step is a lie of omission unless the
        caption says where the grid came from."""
        rulers, legend = data.get("rulers") or {}, data.get("legend") or {}
        bits = [f"{data.get('w')}x{data.get('h')} at {tuple(data.get('origin') or ())}"]
        if rulers.get("z_top") is not None:
            bits.append(f"z {rulers.get('z_bottom')}-{rulers.get('z_top')}")
        shown = max(0, self.query_one("#mapscroll").size.height - 2)   # ruler rows
        bits.append(f"{shown} of {nrows} rows, pgup/pgdn"
                    if shown and nrows > shown else f"{nrows} rows")
        t = Text(" · ".join(bits) + "\n", style=DIM)
        if stale:
            t.append(f"from {ms.key} · ", style=DIM)
        t.append("   ".join(f"{g} {str(d).split('|')[0].strip()}"
                            for g, d in list(legend.items())[:6]), style=DIM)
        return t

    def render_side(self):
        s = self.run.steps[self.i]
        secs = self._sections()
        self._nsec = len(secs)
        self.cursor = min(self.cursor, self._nsec - 1) if self._nsec else 0
        self.query_one("#side").border_title = s.key.split("/")[-1]

        t, line = Text(), 0
        for r in F.head_rows(s, self.run, self.i):
            t.append("  ")
            t.append(f"{r.label:<8}", style=DIM)
            t.append(str(r.value) + "\n", style=WARN if r.warn else BRIGHT)
            line += 1
        t.append("\n")
        line += 1

        self._sec_lines = []
        for n, sec in enumerate(secs):
            self._sec_lines.append(line)
            here = n == self.cursor
            t.append("▸ " if here else "  ", style=ACCENT if here else DIM)
            t.append(f"{sec.label:<15} ", style=BRIGHT if here else DIM)
            t.append(sec.summary + "\n",
                     style=WARN if sec.warn else ACCENT if sec.changed else FG)
            line += 1
            if sec.label in self.expanded and sec.detail:
                for dl in sec.detail.splitlines():
                    t.append("    " + dl + "\n", style=DIM)
                    line += 1

        t.append("\n  journal delta\n", style=DIM)
        for jl in self._journal_lines():
            t.append(jl)
        self.query_one("#sidebody", Static).update(t)

    def _journal_lines(self):
        if not self.journal:
            return [Text("  no journal file found\n", style=DIM)]
        events = self.journal.between(*self.run.journal_window(self.i))
        if not events:
            return [Text("  nothing\n", style=DIM)]
        out = []
        for e in events[-40:]:
            _, typ, text, warn = F.journal_line(e)
            t = Text("  ")
            t.append(f"{typ:<13} ", style=DIM)
            t.append(text[:200] + "\n", style=WARN if warn else FG)
            out.append(t)
        if len(events) > 40:
            out.insert(0, Text(f"  +{len(events) - 40} earlier\n", style=DIM))
        return out

    def _scroll_to_cursor(self):
        if not self._sec_lines:
            return
        y = self._sec_lines[min(self.cursor, len(self._sec_lines) - 1)]
        side = self.query_one("#side")
        top, height = side.scroll_offset.y, side.size.height
        if y < top + 1:
            side.scroll_to(y=max(0, y - 1), animate=False)
        elif y > top + height - 3:
            side.scroll_to(y=y - height + 3, animate=False)

    def render_foot(self):
        keys = Text()
        for k, label in (("←→", "step"), ("[]", "day"), ("↑↓", "section"),
                         ("tab", "page"), ("f", "follow"), ("?", "help"),
                         ("q", "quit")):
            keys.append(k, style=FG)
            keys.append(f" {label}    ", style=DIM)
        right = Text()
        if self.status:
            right.append(self.status + "   ", style=ACCENT)
        # At 100 columns the key row already fills the line; the provenance is
        # the half that can go, and a footer that collides is worse than one
        # that says less.
        if self.size.width >= NARROW_AT:
            right.append(self.source, style=DIM)
        self.query_one("#foot").update(grid(keys, right))

    def _help(self):
        t = Text("cockpit\n\n", style=BRIGHT)
        for k, v in HELP:
            if not k and not v:
                t.append("\n")
                continue
            t.append(f"  {k:<14}", style=FG if k else DIM)
            t.append(v + "\n", style=DIM)
        return t
