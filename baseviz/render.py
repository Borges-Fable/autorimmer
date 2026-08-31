"""Render a `map-dump` result to a PNG. Pure function of dump + catalog.

New in this repo (spec 2.5) — not vendored; baseviz had no raster code at all.

This is the client half of spec 2.5. The game does no image work — it publishes
per-cell planes (`Source/AutoRimmer/MapDumpVerbs.cs`) and this draws them, so
the same dump always produces the same bytes and a render can be re-run offline
from a saved dump with no bench running.

WHAT THE FIRST LIVE RENDER GOT WRONG
------------------------------------
A fresh agent shown only the PNG of a real colony answered 1 of the spec's 3
questions. Doors: all six, with coordinates. Room count: unanswerable. Which
room holds the stove: unanswerable. The synthetic two-room fixture this was
tuned against had hidden every one of the causes, and they were not the cause
predicted (a room-tint palette cycling at 12). They were:

1. **Structure was never rendered.** Every thing was drawn as a block inset by
   1px in its catalog colour. An inset block does not touch its neighbours, so
   two adjacent wall cells had a 2px gap between them and a reader could not
   tell a doorway from a wall whose label had been dropped. Walls now fill the
   whole cell and form a continuous mass; see `_structure_pass`.
2. **Constructed wall and natural rock were the same grey.** Both are
   impassable grey buildings and nothing distinguished them, so a base did not
   stand out from the mountain it was dug into. The dump now carries
   `impassable` and `natural_rock` and they are drawn differently.
3. **The rooms block enumerated door pockets.** A door cell is its own
   single-cell Room, so on a six-door map the ROOMS legend listed six unnamed
   1-cell "rooms" — a confident wrong answer to the exact question being asked,
   pointing at swatches too small to see. Doorways and non-proper rooms are
   excluded now, from the tint, the boundary and the legend alike.
4. **The legend truncated below the map's own content.** "... 23 MORE" hid 23
   of ~41 thing types, and codes visible on the map were among the hidden, so a
   stove — if present — was an unkeyable token. The legend is exhaustive now and
   flows into columns; it is never truncated while anything on the map is
   unkeyed. That is what makes question 3 answerable, and it is a legend fix,
   not a map fix.
5. **A third of the legend was duplicate rows** — WALL twice, DOOR twice, URN
   three times — because the palette keys on def+stuff and the legend did not
   merge them. Merged by label now.
6. **Two things could share a code**, and the code scheme has two competing
   rules (first-two-letters vs CamelCase initials), so an unlisted or duplicated
   token was unguessable. Codes are made unique per image now and the header
   says they are legend keys rather than mnemonics. The underlying scheme is
   still baseviz's `catalog._glyph`; no third alphabet is introduced.
7. **Pawn colours collided with steel, wood and the stockpile zone.** Seven
   pawns were listed by name and none could be found. They have their own
   reserved palette now and are drawn last, on top of everything.

The axes were the one part that unambiguously worked and are unchanged.

DETERMINISM
-----------
Everything drawn is a function of (dump, catalog, options). Palettes arrive as
ordered lists and are consumed in index order; nothing iterates a dict or a set;
no time, no randomness. Code disambiguation and label-overlap suppression both
resolve in palette order, so they are stable for a given dump.
"""

from __future__ import annotations

from .png import FONT_H, Image, text_width

# --------------------------------------------------------------------------
# palette
# --------------------------------------------------------------------------
BG = (14, 15, 17)
FOG = (30, 33, 39)
FOG_HATCH = (52, 57, 66)
INK = (232, 234, 238)
DIM = (128, 134, 144)
RULE = (58, 62, 70)
DOOR = (255, 206, 64)
LANDMARK = (63, 207, 106)

# Natural rock: flat, dark, desaturated, and deliberately DULLER than anything
# built. The mountain is the backdrop; the colony is the subject.
ROCK = (58, 56, 54)
ROCK_EDGE = (44, 42, 41)
# Constructed impassable: the catalog colour is used for the fill, but every
# built wall also gets this outline where it faces open ground, which is what
# turns a run of cells into a visible perimeter.
WALL_EDGE = (16, 16, 18)

ROOM_TINTS = [
    (72, 92, 128), (128, 84, 72), (76, 116, 92), (116, 96, 132),
    (132, 116, 72), (72, 116, 124), (124, 76, 100), (92, 108, 72),
    (84, 88, 136), (136, 100, 84), (68, 104, 112), (112, 88, 116),
    (96, 76, 128), (128, 128, 96), (80, 128, 116), (128, 96, 116),
]

# Reserved for pawns and used for nothing else. The first render gave colonists
# the same blue as steel, wood and a large stockpile zone, and animals the same
# green as a saguaro, so seven named pawns were unlocatable.
PAWN_MARKS = {
    "colonist": (255, 255, 255),
    "prisoner": (255, 138, 0),
    "hostile": (255, 40, 40),
    "neutral/guest": (214, 102, 255),
    "tame animal": (0, 224, 200),
    "wild animal": (150, 120, 90),
}
PAWN_RING = (10, 10, 12)

ZONE_TINTS = {"stockpile": (86, 132, 196), "growing": (104, 160, 88)}

LEGEND_ROW_H = 18
LEGEND_MAX_ROWS = 30          # per column before flowing into the next
_LG_SWATCH, _LG_CODE, _LG_LABEL = 2, 22, 52


class RenderError(Exception):
    """Raised for a dump this renderer cannot draw."""


# --------------------------------------------------------------------------
# plane decoding
# --------------------------------------------------------------------------
def decode_plane(encoded: str, cells: int) -> list[int]:
    """Decode an "rle-v1" plane to a flat list of palette indices.

    Tokens are comma-separated, each either a bare index (one cell) or
    ``count:index``. Runs cross row boundaries; the caller reshapes with `w`.
    """
    out: list[int] = []
    if encoded:
        for tok in encoded.split(","):
            if ":" in tok:
                n, _, i = tok.partition(":")
                out.extend([int(i)] * int(n))
            else:
                out.append(int(tok))
    if len(out) != cells:
        raise RenderError(
            f"plane decodes to {len(out)} cells, header says {cells} — "
            "the dump is truncated or was written by a different encoding"
        )
    return out


def _planes(dump: dict) -> dict[str, list[int]]:
    enc = dump.get("encoding")
    if enc != "rle-v1":
        raise RenderError(f"unsupported plane encoding {enc!r} (this renderer speaks rle-v1)")
    cells = int(dump["w"]) * int(dump["h"])
    return {k: decode_plane(v, cells) for k, v in (dump.get("planes") or {}).items()}


# --------------------------------------------------------------------------
# what counts as a room, and what counts as structure
# --------------------------------------------------------------------------
def proper_rooms(pal: dict) -> list[tuple[int, dict]]:
    """(index, entry) for rooms a reader would actually count.

    Excludes doorways (a door cell is its own single-cell Room), open ground
    that merely happens to be regioned, and anything psychologically outdoors.
    `proper`/`doorway` arrive from the dump; a dump predating them falls back to
    the old behaviour rather than crashing, which keeps saved dumps renderable.
    """
    out = []
    for i, e in enumerate(pal.get("rooms") or []):
        if not i or not e or e.get("outdoors"):
            continue
        if e.get("doorway"):
            continue
        if "proper" in e and not e.get("proper"):
            continue
        out.append((i, e))
    return out


def _is_structure(e: dict) -> bool:
    return bool(e.get("impassable")) and not e.get("door")


def _is_rock(e: dict) -> bool:
    return bool(e.get("natural_rock"))


# --------------------------------------------------------------------------
# codes
# --------------------------------------------------------------------------
def _base_code(catalog, e: dict) -> str:
    if catalog is not None:
        try:
            return catalog.spec_for(e["def"], e.get("stuff"))["glyph"]
        except Exception:
            pass
    name = e.get("def") or "?"
    name = name.split("_", 1)[-1] if "_" in name else name
    caps = [c for c in name if c.isupper()]
    return ("".join(caps[:2]) if len(caps) >= 2 else name[:2]).upper()


_ALT = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"


def assign_codes(pal_things: list, catalog) -> tuple[dict, list]:
    """Map palette index -> unique 2-char code, merging def+stuff variants.

    Returns (index_to_code, types), where `types` is one entry per distinct
    LABEL in palette order — which is what the legend lists, and is why WALL no
    longer appears twice because it was built of two materials.

    Codes are made unique across the image. baseviz's `_glyph` can collide (two
    defs whose labels both start "WA"), and a collision is unresolvable for a
    reader when the same token means two things. On collision the second letter
    is walked through `_ALT` deterministically in palette order, so the mapping
    is stable for a dump and the legend is always the authority.
    """
    by_label: dict[str, dict] = {}
    order: list[str] = []
    for i, e in enumerate(pal_things or []):
        if not i or not e:
            continue
        label = (e.get("label") or e.get("def") or "?")
        t = by_label.get(label)
        if t is None:
            by_label[label] = t = {
                "label": label, "indices": [], "entry": e,
                "structure": _is_structure(e), "rock": _is_rock(e),
                "door": bool(e.get("door")), "cells": 0,
            }
            order.append(label)
        t["indices"].append(i)

    taken: set[str] = set()
    index_to_code: dict[int, str] = {}
    for label in order:
        t = by_label[label]
        code = _base_code(catalog, t["entry"])
        if code in taken:
            head = code[0] if code else "X"
            for c in _ALT:
                if head + c not in taken:
                    code = head + c
                    break
            else:
                code = "??"
        taken.add(code)
        t["code"] = code
        for i in t["indices"]:
            index_to_code[i] = code
    return index_to_code, [by_label[l] for l in order]


# --------------------------------------------------------------------------
# rendering
# --------------------------------------------------------------------------
def render(dump: dict, catalog=None, *, scale: int = 12, layers=None,
           landmarks: dict | None = None, legend: bool = True,
           ruler: bool = True, max_side: int = 8000) -> Image:
    """Draw a `map-dump` result. Returns an `Image`; caller writes it."""
    w, h = int(dump["w"]), int(dump["h"])
    if w <= 0 or h <= 0:
        raise RenderError(f"degenerate dump {w}x{h}")
    ox, oz = (int(v) for v in dump["origin"])
    planes = _planes(dump)
    pal = dump.get("palettes") or {}
    want = set(layers) if layers else None

    def on(name: str) -> bool:
        return name in planes and (want is None or name in want)

    scale = max(1, int(scale))
    gutter_l = 42 if ruler else 4
    gutter_t = 22 if ruler else 4
    pad = 4

    def cell(plane: str, col: int, row: int) -> int:
        # Planes are north-up row-major: row 0 is z = oz + h - 1.
        return planes[plane][row * w + col]

    def entry(kind: str, idx: int):
        lst = pal.get(kind) or []
        return lst[idx] if 0 < idx < len(lst) else None

    codes, types = assign_codes(pal.get("things") or [], catalog)
    if on("things"):
        counts: dict[int, int] = {}
        for v in planes["things"]:
            if v:
                counts[v] = counts.get(v, 0) + 1
        for t in types:
            t["cells"] = sum(counts.get(i, 0) for i in t["indices"])

    rooms_list = proper_rooms(pal) if on("rooms") else []
    legend_rows = (_legend_rows(dump, pal, catalog, types, rooms_list, on)
                   if legend else [])
    cols = max(1, (len(legend_rows) + LEGEND_MAX_ROWS - 1) // LEGEND_MAX_ROWS)
    rows_per_col = (len(legend_rows) + cols - 1) // cols if legend_rows else 0
    col_w = (max((text_width(r[2], 2) for r in legend_rows), default=0)
             + _LG_LABEL + 18) if legend_rows else 0
    legend_h = (rows_per_col * LEGEND_ROW_H + 14) if legend_rows else 0

    iw = max(gutter_l + w * scale + pad, cols * col_w + 8)
    ih = gutter_t + h * scale + pad + legend_h
    if max(iw, ih) > max_side:
        raise RenderError(
            f"render would be {iw}x{ih}px, over the {max_side}px guard. "
            f"Lower --scale (currently {scale}) or ask for a smaller rect. "
            "Note that a very large PNG is also the WRONG thing to hand an "
            "agent: it gets downsampled and the codes stop being readable."
        )

    im = Image(iw, ih, BG)
    gx0, gy0 = gutter_l, gutter_t

    def px(cx: int, cz: int) -> tuple[int, int]:
        """Cell (map x, map z) -> top-left pixel. The ONE place z flips to y."""
        return gx0 + (cx - ox) * scale, gy0 + (oz + h - 1 - cz) * scale

    fogged = planes.get("fog")

    def is_fog(col, row):
        return bool(fogged and fogged[row * w + col])

    # ---- 1. terrain, and fog ---------------------------------------------
    for row in range(h):
        for col in range(w):
            x, y = gx0 + col * scale, gy0 + row * scale
            if is_fog(col, row):
                im.fill_rect(x, y, scale, scale, FOG)
                for k in range(0, scale * 2, 4):
                    for t in range(2):
                        hx, hy = x + k - t, y + t
                        if x <= hx < x + scale and y <= hy < y + scale:
                            im.set_px(hx, hy, FOG_HATCH)
                        hx2, hy2 = x + k - scale + t, y + scale - 1 - t
                        if x <= hx2 < x + scale and y <= hy2 < y + scale:
                            im.set_px(hx2, hy2, FOG_HATCH)
                continue
            rgb = (46, 48, 52)
            if on("terrain"):
                e = entry("terrain", cell("terrain", col, row))
                if e is not None:
                    rgb = _terrain_color(catalog, e)
            im.fill_rect(x, y, scale, scale, rgb)

    # ---- 2. rooms: tint, then a boundary in the room's own tint -----------
    if on("rooms") and rooms_list:
        keep = {i for i, _ in rooms_list}

        def rid(col, row):
            v = cell("rooms", col, row)
            return v if v in keep else 0

        for row in range(h):
            for col in range(w):
                idx = rid(col, row)
                if not idx:
                    continue
                x, y = gx0 + col * scale, gy0 + row * scale
                im.fill_rect(x, y, scale, scale,
                             _blend(_room_tint(idx), _px(im, x, y), 0.34))
        for row in range(h):
            for col in range(w):
                idx = rid(col, row)
                if not idx:
                    continue
                edge = _blend(_room_tint(idx), (255, 255, 255), 0.55)
                x, y = gx0 + col * scale, gy0 + row * scale
                t = 2 if scale >= 12 else 1
                if row == 0 or rid(col, row - 1) != idx:
                    im.fill_rect(x, y, scale, t, edge)
                if row == h - 1 or rid(col, row + 1) != idx:
                    im.fill_rect(x, y + scale - t, scale, t, edge)
                if col == 0 or rid(col - 1, row) != idx:
                    im.fill_rect(x, y, t, scale, edge)
                if col == w - 1 or rid(col + 1, row) != idx:
                    im.fill_rect(x + scale - t, y, t, scale, edge)

    # ---- 3. zones --------------------------------------------------------
    if on("zones") and scale >= 4:
        for row in range(h):
            for col in range(w):
                idx = cell("zones", col, row)
                if not idx:
                    continue
                e = entry("zones", idx) or {}
                tint = ZONE_TINTS.get(e.get("kind"), (140, 140, 140))
                x, y = gx0 + col * scale, gy0 + row * scale
                im.stroke_rect(x, y, scale, scale, tint, 1)

    # ---- 4. roof ---------------------------------------------------------
    if on("roof") and scale >= 6:
        for row in range(h):
            for col in range(w):
                if not cell("roof", col, row):
                    continue
                x, y = gx0 + col * scale, gy0 + row * scale
                im.fill_rect(x + scale - 2, y, 2, scale, (44, 46, 52))

    # ---- 5. structure, then everything else ------------------------------
    if on("things"):
        _structure_pass(im, dump, planes, pal, catalog, w, h, scale, gx0, gy0,
                        cell, entry, is_fog)

    # ---- 6. codes, then room names ---------------------------------------
    # Thing codes go down FIRST because they are anchored to an object and
    # cannot move; room names are placed afterwards and can fall back to
    # another cell of the same room if the centroid is taken.
    placed: list[tuple[int, int, int, int]] = []
    if on("things") and scale >= 12:
        _label_pass(im, dump, codes, entry, px, ox, oz, w, h, scale, gx0, gy0, placed)
    if on("rooms") and rooms_list and scale >= 10:
        _room_name_pass(im, planes, rooms_list, w, h, scale, gx0, gy0, placed)

    # ---- 7. pawns, last and on top ---------------------------------------
    if on("pawns"):
        for row in range(h):
            for col in range(w):
                idx = cell("pawns", col, row)
                if not idx:
                    continue
                e = entry("pawns", idx) or {}
                mark = PAWN_MARKS.get(e.get("kind"), (255, 255, 255))
                x, y = gx0 + col * scale, gy0 + row * scale
                r = max(3, int(scale * 0.62))
                bx, by = x + (scale - r) // 2, y + (scale - r) // 2
                im.fill_rect(bx - 1, by - 1, r + 2, r + 2, PAWN_RING)
                im.fill_rect(bx, by, r, r, mark)

    # ---- 8. landmarks ----------------------------------------------------
    if landmarks:
        for name in sorted(landmarks):
            lx, lz = landmarks[name][0], landmarks[name][1]
            if not (ox <= lx < ox + w and oz <= lz < oz + h):
                continue
            x, y = px(lx, lz)
            im.stroke_rect(x, y, scale, scale, LANDMARK, 2 if scale >= 8 else 1)
            if scale >= 8:
                im.text(min(x + scale + 2, gx0 + w * scale - text_width(name, 1) - 1),
                        y + scale // 2 - FONT_H // 2, name.upper(), LANDMARK, 1)

    # ---- 9. rulers (unchanged; the one part that worked) ------------------
    if ruler:
        _draw_rulers(im, ox, oz, w, h, scale, gx0, gy0)

    # ---- 10. legend ------------------------------------------------------
    if legend_rows:
        _draw_legend(im, legend_rows, 4, gy0 + h * scale + pad + 6,
                     cols, rows_per_col, col_w)
    return im


def _structure_pass(im, dump, planes, pal, catalog, w, h, scale, gx0, gy0,
                    cell, entry, is_fog):
    """Walls and rock full-bleed; everything else inset on top of them.

    The full-bleed half is the whole fix for "could not trace a perimeter": an
    inset block leaves a gap on every side, so a run of wall cells rendered as a
    dotted line of separate squares rather than as a wall. Structure is drawn
    first and flat, then a dark edge only where it faces open ground, which is
    what makes a built perimeter visible against a mountain.
    """
    tp = planes["things"]

    def kind(col, row):
        idx = tp[row * w + col]
        e = entry("things", idx)
        if e is None:
            return None, None
        return e, ("rock" if _is_rock(e) else "wall" if _is_structure(e) else "other")

    for row in range(h):
        for col in range(w):
            if is_fog(col, row):
                continue
            e, k = kind(col, row)
            if e is None or k == "other":
                continue
            x, y = gx0 + col * scale, gy0 + row * scale
            if k == "rock":
                im.fill_rect(x, y, scale, scale, ROCK)
            else:
                im.fill_rect(x, y, scale, scale, _thing_color(catalog, e))

    # Outline built structure where it meets anything that is not built
    # structure. Rock gets a much fainter version of the same treatment so the
    # mountain reads as a mass without competing with the colony.
    if scale >= 4:
        for row in range(h):
            for col in range(w):
                if is_fog(col, row):
                    continue
                e, k = kind(col, row)
                if k not in ("rock", "wall"):
                    continue
                edge = ROCK_EDGE if k == "rock" else WALL_EDGE
                t = 2 if (scale >= 12 and k == "wall") else 1
                x, y = gx0 + col * scale, gy0 + row * scale

                def same(c, r):
                    if not (0 <= c < w and 0 <= r < h) or is_fog(c, r):
                        return False
                    return kind(c, r)[1] == k

                if not same(col, row - 1):
                    im.fill_rect(x, y, scale, t, edge)
                if not same(col, row + 1):
                    im.fill_rect(x, y + scale - t, scale, t, edge)
                if not same(col - 1, row):
                    im.fill_rect(x, y, t, scale, edge)
                if not same(col + 1, row):
                    im.fill_rect(x + scale - t, y, t, scale, edge)

    # Non-structural things last, inset, so furniture reads as an object
    # sitting inside a room rather than as part of the fabric.
    for row in range(h):
        for col in range(w):
            if is_fog(col, row):
                continue
            e, k = kind(col, row)
            if e is None or k != "other":
                continue
            x, y = gx0 + col * scale, gy0 + row * scale
            inset = 1 if scale >= 6 else 0
            if e.get("door"):
                im.fill_rect(x, y, scale, scale, DOOR)
                im.stroke_rect(x, y, scale, scale, (60, 44, 8), 2 if scale >= 8 else 1)
            else:
                im.fill_rect(x + inset, y + inset,
                             scale - 2 * inset, scale - 2 * inset,
                             _thing_color(catalog, e))


def _room_name_pass(im, planes, rooms_list, w, h, scale, gx0, gy0, placed):
    """Write each enclosed room's name inside it, at or near its centroid.

    This is what makes "which room holds the stove" answerable without eye-
    matching a 14px legend swatch against a floor colour. The first live render
    made the reader do exactly that, and with a real catalog loaded a wood floor
    under a 34%-blended room tint is muddy enough that the match is guesswork.
    A word written in the room is not.

    Falls back through the room's own cells when the centroid is already taken
    by a thing code, and gives up rather than overlapping — a name on top of a
    code would cost both.
    """
    rp = planes["rooms"]
    cells: dict[int, list[tuple[int, int]]] = {}
    for i, _ in rooms_list:
        cells[i] = []
    for row in range(h):
        for col in range(w):
            v = rp[row * w + col]
            if v in cells:
                cells[v].append((col, row))

    tscale = 2 if scale >= 14 else 1
    for i, e in rooms_list:
        pts = cells.get(i) or []
        if not pts:
            continue
        name = (e.get("role") or f"ROOM {e.get('id')}").upper()
        tw, th = text_width(name, tscale), FONT_H * tscale
        acx = sum(p[0] for p in pts) / len(pts)
        acy = sum(p[1] for p in pts) / len(pts)
        # Nearest cells to the centroid first, then outwards — deterministic
        # because the tie-break is the cell's own coordinates.
        for col, row in sorted(pts, key=lambda p: ((p[0] - acx) ** 2 + (p[1] - acy) ** 2,
                                                   p[1], p[0]))[:24]:
            cx = gx0 + col * scale + scale // 2 - tw // 2
            cy = gy0 + row * scale + scale // 2 - th // 2
            cx = max(gx0, min(cx, gx0 + w * scale - tw - 1))
            cy = max(gy0, min(cy, gy0 + h * scale - th - 1))
            box = (cx - 2, cy - 2, cx + tw + 2, cy + th + 2)
            if any(not (box[2] <= o[0] or box[0] >= o[2]
                        or box[3] <= o[1] or box[1] >= o[3]) for o in placed):
                continue
            placed.append(box)
            im.fill_rect(box[0], box[1], box[2] - box[0], box[3] - box[1], (10, 10, 12))
            im.stroke_rect(box[0], box[1], box[2] - box[0], box[3] - box[1],
                           _blend(_room_tint(i), (255, 255, 255), 0.55), 1)
            im.text(cx, cy, name, INK, tscale)
            break


def _label_pass(im, dump, codes, entry, px, ox, oz, w, h, scale, gx0, gy0, placed):
    """Draw each building's code once, skipping overlaps and clamping to the grid.

    The first render drew labels wherever they landed, producing `A$IR` where
    two overlapped and glyphs pressed into the right border. Boxes are tracked
    and a label that would collide is dropped — in dump order, so deterministic.
    """
    tscale = 2 if scale >= 20 else 1
    for lab in dump.get("labels") or []:
        code = codes.get(lab.get("p", 0))
        if not code:
            continue
        gxc, gzc = lab["at"][0], lab["at"][1]
        if not (ox <= gxc < ox + w and oz <= gzc < oz + h):
            continue
        sw, sh = (lab.get("size") or [1, 1])[:2]
        if lab.get("rot") in ("East", "West"):
            sw, sh = sh, sw
        x, y = px(gxc, gzc)
        tw, th = text_width(code, tscale), FONT_H * tscale
        cx = x + (sw * scale) // 2 - tw // 2
        cy = y + scale // 2 - ((sh - 1) * scale) // 2 - th // 2
        cx = max(gx0, min(cx, gx0 + w * scale - tw - 1))
        cy = max(gy0, min(cy, gy0 + h * scale - th - 1))
        box = (cx - 1, cy - 1, cx + tw + 1, cy + th + 1)
        if any(not (box[2] <= o[0] or box[0] >= o[2]
                    or box[3] <= o[1] or box[1] >= o[3]) for o in placed):
            continue
        placed.append(box)
        im.fill_rect(box[0], box[1], box[2] - box[0], box[3] - box[1], (12, 12, 14))
        im.text(cx, cy, code, INK, tscale)


# --------------------------------------------------------------------------
# helpers
# --------------------------------------------------------------------------
def _px(im: Image, x: int, y: int) -> tuple[int, int, int]:
    i = (y * im.w + x) * 3
    return im.buf[i], im.buf[i + 1], im.buf[i + 2]


def _blend(a, b, t: float):
    return tuple(int(round(a[i] * t + b[i] * (1 - t))) for i in range(3))


def _room_tint(idx: int):
    return ROOM_TINTS[(idx - 1) % len(ROOM_TINTS)]


def _terrain_color(catalog, e: dict):
    if catalog is not None:
        try:
            return tuple(catalog.terrain_color(e["def"]))
        except Exception:
            pass
    return (58, 60, 64)


def _thing_color(catalog, e: dict):
    if catalog is not None:
        try:
            return tuple(catalog.spec_for(e["def"], e.get("stuff"))["color"])
        except Exception:
            pass
    return (150, 150, 150) if e.get("category") == "Building" else (110, 120, 150)


def _ink_on(rgb) -> tuple[int, int, int]:
    lum = 0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2]
    return (17, 17, 17) if lum > 140 else (238, 238, 238)


def _draw_rulers(im, ox, oz, w, h, scale, gl, gt):
    """Coordinate marks in MAP coordinates, every 10 cells plus both ends.

    Unchanged: the live read confirmed the axes were the one part that worked.
    """
    step = 10 if scale >= 8 else 20
    im.hline(gl, gt - 1, w * scale, RULE)
    im.vline(gl - 1, gt, h * scale, RULE)
    for col in range(w):
        x = ox + col
        if x % step and col not in (0, w - 1):
            continue
        sx = gl + col * scale
        im.vline(sx, gt - 4, 3, RULE)
        im.text(sx + 1, gt - 4 - FONT_H, str(x), DIM, 1)
    for row in range(h):
        z = oz + h - 1 - row
        if z % step and row not in (0, h - 1):
            continue
        sy = gt + row * scale
        im.hline(gl - 4, sy, 3, RULE)
        label = str(z)
        im.text(gl - 6 - text_width(label, 1), sy, label, DIM, 1)


def _legend_rows(dump, pal, catalog, types, rooms_list, on) -> list[tuple]:
    """(swatch | None, code, text) rows. Exhaustive: every code on the map is keyed."""
    rows: list[tuple] = []
    hdr = f"{dump['w']}X{dump['h']} AT {dump['origin'][0]},{dump['origin'][1]}"
    if dump.get("fogged_cells"):
        hdr += f"  FOGGED {dump['fogged_cells']}"
    hdr += f"  {dump.get('channel', {}).get('alphabet', '?').upper()}"
    rows.append((None, "", hdr))
    rows.append((None, "", "CODES ARE LEGEND KEYS, UNIQUE IN THIS IMAGE - NOT ABBREVIATIONS"))
    if catalog is None:
        rows.append((None, "", "NO CATALOG: COLOURS ARE FALLBACKS"))

    if rooms_list:
        rows.append((None, "", f"ENCLOSED ROOMS: {len(rooms_list)}"
                               "   (DOORWAYS AND OUTDOORS EXCLUDED)"))
        for i, e in sorted(rooms_list, key=lambda t: (-(t[1].get("cells") or 0), t[0])):
            role = (e.get("role") or "UNASSIGNED").upper()
            rows.append((_room_tint(i), "", f"{role}  ID {e.get('id')}  {e.get('cells')} CELLS"))

    struct = [t for t in types if t["structure"]]
    if struct:
        rows.append((None, "", "STRUCTURE  (DRAWN SOLID, NO CODE ON MAP)"))
        for t in sorted(struct, key=lambda t: -t["cells"]):
            sw = ROCK if t["rock"] else _thing_color(catalog, t["entry"])
            kind = "NATURAL ROCK" if t["rock"] else "BUILT"
            rows.append((sw, "", f"{t['label'].upper()}  {kind}  {t['cells']} CELLS"))

    rest = [t for t in types if not t["structure"]]
    if rest:
        rows.append((None, "", "THINGS  (EVERY CODE ON THE MAP IS LISTED)"))
        for t in sorted(rest, key=lambda t: (-t["cells"], t["label"])):
            sw = DOOR if t["door"] else _thing_color(catalog, t["entry"])
            txt = t["label"].upper()
            if t["cells"]:
                txt += f"  {t['cells']}"
            rows.append((sw, t["code"], txt))

    zones = [(i, e) for i, e in enumerate(pal.get("zones") or []) if i and e]
    if zones:
        rows.append((None, "", "ZONES"))
        for i, e in zones:
            kind = (e.get("kind") or "zone").upper()
            txt = f"{kind}  {(e.get('label') or '').upper()}"
            if e.get("plant"):
                txt += f"  GROWING {e['plant'].upper()}"
            elif e.get("kind") == "growing":
                txt += "  UNCONFIGURED"
            rows.append((ZONE_TINTS.get(e.get("kind"), (140, 140, 140)), "", txt))

    pawns = [(i, e) for i, e in enumerate(pal.get("pawns") or []) if i and e]
    if pawns:
        rows.append((None, "", f"PAWNS: {len(pawns)}"))
        for i, e in pawns:
            rows.append((PAWN_MARKS.get(e.get("kind"), (255, 255, 255)), "",
                         f"{(e.get('name') or '?').upper()}  {(e.get('kind') or '').upper()}"))
    return rows


def _draw_legend(im, rows, x0, y0, cols, rows_per_col, col_w):
    im.hline(x0, y0 - 4, im.w - 2 * x0, RULE)
    for n, (swatch, code, text) in enumerate(rows):
        c, r = divmod(n, rows_per_col) if rows_per_col else (0, 0)
        cx = x0 + c * col_w
        y = y0 + r * LEGEND_ROW_H
        if swatch is not None:
            im.fill_rect(cx + _LG_SWATCH, y + 1, 14, 14, swatch)
            im.stroke_rect(cx + _LG_SWATCH, y + 1, 14, 14, (10, 10, 12), 1)
            if code:
                im.text(cx + _LG_CODE, y + 1, code, INK, 2)
            im.text(cx + _LG_LABEL, y + 1, text, INK, 2)
        else:
            im.text(cx + _LG_SWATCH, y + 1, text, DIM, 2)
