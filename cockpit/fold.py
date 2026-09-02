"""Folding an envelope into something a person reads at a glance.

A `digest` result is 7,517 bytes over 13 sections. Dumped it is a wall; folded
it is thirteen lines, each saying whether it changed since the last digest. The
raw JSON stays one keypress away — the fold is a default, not a censor.

Pure: envelope in, rows out. `changed` and `warn` say what to emphasise; the
renderer owns the palette.
"""

import json
from collections import namedtuple

Section = namedtuple("Section", "label summary detail changed warn")
Row = namedtuple("Row", "label value warn")

DASH = "—"          # "there is no value", distinct from "0"


def num(v, unit="", nd=1):
    if v is None:
        return DASH
    if isinstance(v, bool):
        return "yes" if v else "no"
    if isinstance(v, (int, float)):
        return (f"{int(v):,}{unit}" if float(v).is_integer() and abs(v) < 1e15
                else f"{v:,.{nd}f}{unit}")
    return str(v)


def brief(v, width=40):
    """One line for any JSON value, sized for a sidebar."""
    if v is None or isinstance(v, (bool, int, float)):
        return num(v)
    if isinstance(v, str):
        s = " ".join(v.split())
        return s if len(s) <= width else s[:width - 1] + "…"
    if isinstance(v, list):
        if not v:
            return "none"
        if all(isinstance(x, (str, int, float, bool)) for x in v):
            joined = ", ".join(brief(x, 16) for x in v[:4])
            if len(v) > 4:
                joined += f", +{len(v) - 4}"
            if len(joined) <= width:
                return joined
        return f"{len(v)} items"
    if isinstance(v, dict):
        if not v:
            return "none"
        flat = [(k, x) for k, x in v.items() if not isinstance(x, (list, dict))]
        joined = " · ".join(f"{k} {brief(x, 12)}" for k, x in flat[:3])
        if len(flat) != len(v) or len(flat) > 3:
            joined += f" · +{len(v) - min(3, len(flat))}"
        return joined if flat and len(joined) <= width else f"{len(v)} keys"
    return str(v)


def raw(v):
    try:
        return json.dumps(v, indent=2, ensure_ascii=False)
    except (TypeError, ValueError):
        return repr(v)


def same(a, b):
    try:
        return json.dumps(a, sort_keys=True) == json.dumps(b, sort_keys=True)
    except (TypeError, ValueError):
        return a == b


def moved(data, prev, *keys):
    """`changed` is what the single accent colour is spent on, so it has to mean
    something: a field hardcoded true would light the sidebar up permanently and
    cost the accent its whole job."""
    return isinstance(prev, dict) and any(not same(data.get(k), prev.get(k))
                                          for k in keys)


# ------------------------------------------------- the digest's own sections
# A section with no summariser falls through to `brief`, which is why the fold
# covers all ~70 verbs and not only these.

def _alerts(v):
    act = v.get("active") or []
    if not act:
        return f"none active · {v.get('muted_count', 0)} muted"
    extra = len(act) - 2 + int(v.get("more") or 0)
    return (f"{v.get('total', len(act))} active · "
            + ", ".join(str(a.get("label", "?")) for a in act[:2])
            + (f", +{extra}" if extra > 0 else ""))


def _colonists(v):
    lst = v.get("list") or []
    moods = [c.get("mood_pct") for c in lst if isinstance(c.get("mood_pct"), (int, float))]
    drafted = sum(1 for c in lst if c.get("drafted"))
    return (f"{v.get('total', len(lst))}"
            + (f" · mood {min(moods)}-{max(moods)}%" if moods else "")
            + (f" · {drafted} drafted" if drafted else ""))


def _resources(v):
    rot = (v.get("food_rot") or {}).get("days")
    out = [f"food {num(v.get('food_days'), 'd')}"]
    if rot is not None and rot != v.get("food_days"):
        out.append(f"rot-honest {num(rot, 'd')}")
    out += [f"{lbl} {num(v[k])}" for k, lbl in
            (("meds", "meds"), ("steel", "steel"), ("wood", "wood"),
             ("silver", "silver")) if v.get(k) is not None]
    return " · ".join(out[:5])


def _trends(v):
    if not v.get("ready"):
        return "not ready · " + str(v.get("not_ready_why", ""))[:36]
    out = [f"food {num(v.get('food_days_per_day'), '/day', 2)}"]
    if v.get("food_days_to_zero") is not None:
        out.append(f"zero in {num(v['food_days_to_zero'], 'd')}")
    return " · ".join(out)


DIGEST = {
    "time": lambda v: (f"day {v.get('day_of_season', DASH)} of {v.get('season', '?')}"
                       f" {v.get('year', '')} · {v.get('hour', DASH)}h · "
                       f"{v.get('weather', '?')} {num(v.get('outdoor_c'), '°C')}"),
    "site": lambda v: (f"{v.get('biome_label') or v.get('biome', '?')} · "
                       f"{'x'.join(str(n) for n in v.get('map_size') or [])}"),
    "alerts": _alerts,
    "colonists": _colonists,
    "resources": _resources,
    "trends": _trends,
    "construction": lambda v: (f"{num(v.get('blueprints'))} blueprints · "
                               f"{num(v.get('frames'))} frames"
                               + (f" · {v['blocked']} blocked" if v.get("blocked") else "")),
    "work_coverage": lambda v: ("covered" if not v.get("under") else
                                "under: " + ", ".join(map(str, v["under"][:4]))),
    "posture": lambda v: (f"seek {v.get('will_seek', DASH)} · area "
                          f"{v.get('area_bound', DASH)} · attack {v.get('attack', DASH)}"),
    "power": lambda v: (f"{num(v.get('gen_w'), 'W')} gen · {num(v.get('draw_w'), 'W')}"
                        f" draw · {num(v.get('batteries'))} batt"),
    "temperature": lambda v: " · ".join(
        [f"{v.get('total', 0)} rooms"]
        + ([f"{v['out_of_range_rooms']} out of range"] if v.get("out_of_range_rooms") else [])
        + ([f"{v['food_rooms_unfrozen']} food rooms unfrozen"] if v.get("food_rooms_unfrozen") else [])
        + [f"outdoor {num(v.get('outdoor_c'), '°C')}"]),
    "threats": lambda v: (f"danger {v.get('danger', 'None')} · no hostiles"
                          if not v.get("hostiles") else
                          f"danger {v.get('danger', '?')} · {v['hostiles']} hostiles"),
    "changed": lambda v: (" · ".join(f"{n} {k}" for k, n in sorted(
        (v.get("counts") or {}).items(), key=lambda kv: -kv[1])[:4])
        or f"nothing since seq {v.get('since', 0)}"),
}

# One warning colour, and not for decoration.
WARN = {
    "alerts": lambda v: any(a.get("priority") in ("High", "Critical")
                            for a in (v.get("active") or [])),
    "threats": lambda v: bool(v.get("hostiles")),
    "resources": lambda v: isinstance(v.get("food_days"), (int, float)) and v["food_days"] < 2,
    "temperature": lambda v: bool(v.get("out_of_range_rooms") or v.get("food_rooms_unfrozen")),
}


# ------------------------------------------------------------ per-op folds

def _advance(d, p):
    """The verb the whole play loop turns on: git-bug 722c951's three
    distinguishable early returns must be legible without expanding anything."""
    unread, seq = d.get("journal_unread"), d.get("journal_seq")
    out = [
        Section("reason", str(d.get("reason", DASH)), None, moved(d, p, "reason"),
                d.get("reason") in ("casualty", "bleedout-deadline")),
        Section("ticks", f"{num(d.get('ticks_elapsed'))} to tick {num(d.get('tick'))}",
                None, moved(d, p, "ticks_elapsed"), False),
        Section("speed", f"{d.get('speed', DASH)} · {num(d.get('avg_tps'), ' tps', 0)}"
                + (" · thermal step-down" if d.get("thermal_step_down") else ""),
                None, moved(d, p, "speed"), bool(d.get("thermal_step_down"))),
        Section("journal",
                (f"seq {seq[0]}-{seq[1]}" if isinstance(seq, list) and len(seq) == 2 else DASH)
                + f" · watermark {num(d.get('journal_read_watermark'))}"
                + (f" · {num(unread)} UNREAD" if unread else ""),
                None, moved(d, p, "journal_read_watermark"), bool(unread)),
    ]
    for k, warn in (("speed_changes", False), ("slower_spans", True)):
        if d.get(k):
            out.append(Section(k, brief(d[k]), raw(d[k]), moved(d, p, k), warn))
    return out


def _journal_verb(d, p):
    return [
        Section("events", f"{num(d.get('count'))} returned · last seq "
                          f"{num(d.get('last_seq'))}"
                          + (" · truncated" if d.get("truncated") else ""),
                raw(d.get("events")), moved(d, p, "last_seq"), False),
        Section("watermark", f"{num(d.get('watermark_was'))} → {num(d.get('read_watermark'))}"
                + (" moved" if d.get("watermark_moved") else " unchanged"),
                None, bool(d.get("watermark_moved")), False),
        Section("unread_after", num(d.get("unread_after")), None, False,
                bool(d.get("unread_after"))),
    ]


def _map_view(d, p):
    ch = d.get("channel") or {}
    return [
        Section("viewport", f"{d.get('w')}x{d.get('h')} at {tuple(d.get('origin') or ())}"
                + (" · clipped" if d.get("clipped") else ""),
                None, moved(d, p, "origin", "w", "h"), bool(d.get("clipped"))),
        Section("alphabet", str(ch.get("alphabet", DASH)), raw(ch), False, False),
        Section("legend", f"{len(d.get('legend') or {})} glyphs",
                raw(d.get("legend")), moved(d, p, "legend"), False),
    ]


SPECIAL = {"advance": _advance, "journal": _journal_verb, "map-view": _map_view}

NO_RESULT = ("rwa writes cmd.json BEFORE dispatch and result.json when the result\n"
             "comes back, so this step is a command that was in flight when the\n"
             "client stopped -- killed, disconnected, or still running. It is how\n"
             "a wedged run looks on disk. What was asked for:\n\n")


def head_rows(step, run=None, i=None):
    """The four things that must be legible without reading anything else."""
    cmd = step.cmd or {}
    rows = [Row("op", step.op, False),
            Row("args", brief(cmd.get("args"), 34) if cmd.get("args") else DASH, False)]
    if step.in_flight:
        rows.append(Row("result", "NEVER RETURNED", True))
    elif step.ok is True:
        rows.append(Row("result", "ok", False))
    elif step.ok is False:
        rows.append(Row("result", "error · "
                        + str(((step.result or {}).get("error") or {}).get("code", "?")), True))
    else:
        rows.append(Row("result", "unreadable envelope", True))
    if step.tick is not None:
        rows.append(Row("tick", f"{step.tick:,}  (day {step.day})", False))
    if run is not None and i is not None and run.out_of_order(i):
        rows.append(Row("clock", "returned BEFORE the previous step", True))
    return rows


def sections(step, prev):
    """The envelope, folded. `prev` is the last step with the same op."""
    if step.in_flight:
        return [Section("no result.json", "cmd.json was written, result.json never was",
                        NO_RESULT + raw(step.cmd or {}), False, True)]
    res = step.result
    if not isinstance(res, dict):
        return [Section("unreadable", "result.json is not a JSON object", None, False, True)]

    if res.get("ok") is False:
        err = res.get("error") or {}
        out = [Section("error.code", str(err.get("code", DASH)), None, False, True)]
        for k, v in err.items():
            if k != "code":
                out.append(Section(f"error.{k}", brief(v), raw(v), False, True))
        return out

    data = res.get("data")
    pdata = ((prev.result or {}).get("data") if prev is not None else None)
    pdata = pdata if isinstance(pdata, dict) else {}
    if step.op in SPECIAL and isinstance(data, dict):
        return SPECIAL[step.op](data, pdata)
    if not isinstance(data, dict):
        return [Section("data", brief(data), raw(data),
                        prev is not None and not same(data, (prev.result or {}).get("data")),
                        False)]

    summ = DIGEST if step.op == "digest" else {}
    out = []
    for k, v in data.items():
        try:
            line = summ[k](v) if k in summ and isinstance(v, dict) else brief(v)
        except (KeyError, TypeError, ValueError, AttributeError):
            line = brief(v)
        warn = isinstance(v, dict) and v.get("ok") is False
        if k in WARN and isinstance(v, dict):
            try:
                warn = warn or bool(WARN[k](v))
            except (TypeError, ValueError, AttributeError):
                pass
        out.append(Section(k, line, raw(v),
                           bool(pdata) and k in pdata and not same(v, pdata[k]), warn))
    return out


# ------------------------------------------------------------ journal lines

def _pawn(p):
    if p.get("player"):
        return f"{p.get('pawn', '?')} (ours)"
    return f"{p.get('pawn', '?')}" + (f" ({p['faction']})" if p.get("faction") else "")


JOURNAL_TEXT = {
    "construction": lambda p: f"{p.get('kind', '?')} {p.get('def', '?')}"
                              + (f" at {p['at']}" if p.get("at") else "")
                              + (f" · {p['worker']}" if p.get("worker") else ""),
    "action": lambda p: f"{p.get('verb', '?')} {p.get('step', '')}".strip()
                        + (f" · {p['target']}" if p.get("target") else ""),
    "alert_on": lambda p: str(p.get("label", p.get("id", "?"))),
    "alert_off": lambda p: str(p.get("label", p.get("id", "?"))),
    "message": lambda p: str(p.get("text", "")),
    "letter": lambda p: f"{p.get('label', '?')} — {p.get('text', '')}",
    "session": lambda p: " ".join(str(p[k]) for k in ("kind", "game", "bench") if p.get(k)),
    "death": _pawn,
    "downed": _pawn,
    "warning": lambda p: str(p.get("msg", "")),
    "dialog": lambda p: (p.get("opened") or {}).get("type") or f"{p.get('count', 0)} open",
    "mental_break": lambda p: f"{_pawn(p)} · {p.get('state', '?')}",
}

# What went wrong for the COLONY. `alert_on` is deliberately absent: 216 of them
# in one run is a status feed, and colouring all of it hides the two real deaths.
JOURNAL_WARN = {"death", "downed", "mental_break", "warning"}


def journal_line(e):
    """One journal event as (tick, type, text, warn)."""
    p = e.get("payload") if isinstance(e.get("payload"), dict) else {}
    t = str(e.get("type", "?"))
    try:
        text = JOURNAL_TEXT[t](p) if t in JOURNAL_TEXT else brief(p, 60)
    except (TypeError, ValueError, AttributeError, KeyError):
        text = brief(p, 60)
    warn = t in JOURNAL_WARN
    if t == "death" and not p.get("player"):
        warn = False                                   # a mad rat dying is good news
    if t == "letter" and str(p.get("def", "")).startswith("Threat"):
        warn = True
    return e.get("tick"), t, " ".join(str(text or "").split()), warn
