"""Render a `map-dump` result to a PNG. Pure function of dump + catalog.

New in this repo (spec 2.5) — not vendored; baseviz had no raster code at all.

This is the client half of spec 2.5. The game does no image work at all — it
publishes per-cell planes (`Source/AutoRimmer/MapDumpVerbs.cs`) and this draws
them, so the same dump always produces the same bytes and a render can be
re-run offline from a saved dump with no bench running.

Drawing rules are carried over from baseviz's browser viewer
(`static/viewer.js:90-144`), which was the only renderer upstream had: terrain
as the fill, things inset on top of it in their catalog colour, a 2-char glyph
token when the cells are big enough to hold one. What is new here is the room
tint, which exists because the acceptance asks an agent to count rooms and say
which one holds the stove — a question the browser viewer never had to answer.

DETERMINISM
-----------
Everything drawn is a function of (dump, catalog, options). Palettes arrive as
ordered lists and are consumed in index order; nothing iterates a dict or a
set; no time, no randomness; room tints are chosen by palette INDEX, not by
room id or by hash, so they are stable for a given dump and do not drift when
the colony renumbers a room. See `png.encode_png` for the encoder's half.
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
DOOR = (250, 208, 96)
LANDMARK = (63, 207, 106)

# Room tints, deliberately low-saturation: they sit UNDER the things layer and
# must not be mistaken for a floor or a building. Twelve is enough that two
# adjacent rooms almost never collide; the sequence repeats past that.
ROOM_TINTS = [
    (72, 92, 128), (128, 84, 72), (76, 116, 92), (116, 96, 132),
    (132, 116, 72), (72, 116, 124), (124, 76, 100), (92, 108, 72),
    (84, 88, 136), (136, 100, 84), (68, 104, 112), (112, 88, 116),
]

PAWN_MARKS = {
    "colonist": (96, 200, 255),
    "prisoner": (232, 160, 64),
    "hostile": (240, 80, 72),
    "neutral/guest": (200, 160, 240),
    "tame animal": (128, 208, 128),
    "wild animal": (120, 140, 110),
}


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
# rendering
# --------------------------------------------------------------------------
def render(dump: dict, catalog=None, *, scale: int = 12, layers=None,
           landmarks: dict | None = None, legend: bool = True,
           ruler: bool = True, max_side: int = 8000) -> Image:
    """Draw a `map-dump` result. Returns an `Image`; caller writes it.

    `catalog` is a `baseviz.catalog.Catalog` or None. Without one, everything
    falls back to category-neutral greys and the legend says so — the render
    still answers the geometry questions, it just cannot colour a modded wall
    as itself.
    """
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

    legend_rows = _legend_rows(dump, pal, catalog) if legend else []
    legend_h = (len(legend_rows) * 18 + 14) if legend_rows else 0
    # The legend is set at text scale 2 and can easily be wider than the grid
    # (a 24-cell rect is 480px; the header alone is wider). Sizing the canvas
    # to the grid alone silently truncated it off the right edge.
    legend_w = max((text_width(r[2], 2) + _LG_LABEL for r in legend_rows), default=0) + 12

    iw = max(gutter_l + w * scale + pad, legend_w)
    ih = gutter_t + h * scale + pad + legend_h
    if max(iw, ih) > max_side:
        raise RenderError(
            f"render would be {iw}x{ih}px, over the {max_side}px guard. "
            f"Lower --scale (currently {scale}) or ask for a smaller rect. "
            "Note that a very large PNG is also the WRONG thing to hand an "
            "agent: it gets downsampled and the glyphs stop being readable."
        )

    im = Image(iw, ih, BG)

    def px(cx: int, cz: int) -> tuple[int, int]:
        """Cell (map x, map z) -> top-left pixel. The ONE place z flips to y."""
        return gutter_l + (cx - ox) * scale, gutter_t + (oz + h - 1 - cz) * scale

    # Planes are stored north-up row-major: row 0 is z = oz + h - 1.
    def cell_index(plane: str, col: int, row: int) -> int:
        return planes[plane][row * w + col]

    def entry(kind: str, idx: int):
        lst = pal.get(kind) or []
        return lst[idx] if 0 < idx < len(lst) else None

    fogged = planes.get("fog")

    # ---- 1. terrain fill ------------------------------------------------
    for row in range(h):
        for col in range(w):
            x = gutter_l + col * scale
            y = gutter_t + row * scale
            if fogged and fogged[row * w + col]:
                # Hatched, not merely dark. Fog is the honesty layer — it is
                # what tells a reader "the colony has not been here", and the
                # first render made it a flat tone two shades off the page
                # background, which read as empty space rather than as
                # unexplored ground. The hatch is a fixed diagonal, so it stays
                # deterministic.
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
                e = entry("terrain", cell_index("terrain", col, row))
                if e is not None:
                    rgb = _terrain_color(catalog, e)
            im.fill_rect(x, y, scale, scale, rgb)

    # ---- 2. room tint ----------------------------------------------------
    if on("rooms"):
        for row in range(h):
            for col in range(w):
                idx = cell_index("rooms", col, row)
                if not idx:
                    continue
                e = entry("rooms", idx)
                if e is None or e.get("outdoors"):
                    continue
                x = gutter_l + col * scale
                y = gutter_t + row * scale
                im.fill_rect(x, y, scale, scale,
                             _blend(_room_tint(idx), _px(im, x, y), 0.34))

        # Then a boundary stroke, in each room's own tint brightened.
        #
        # The tint alone is not enough and the first render proved it: against
        # the fallback greys two rooms were obvious, but with the real catalog
        # loaded a wood floor is brown, the bedroom tint is brown, and the two
        # rooms nearly merged. The acceptance turns on counting rooms, so the
        # separation cannot depend on the floor happening to contrast. An
        # outline where a room cell abuts anything that is not the same room is
        # crisp whatever the floor is.
        for row in range(h):
            for col in range(w):
                idx = cell_index("rooms", col, row)
                if not idx:
                    continue
                e = entry("rooms", idx)
                if e is None or e.get("outdoors"):
                    continue
                edge = _blend(_room_tint(idx), (255, 255, 255), 0.55)
                x = gutter_l + col * scale
                y = gutter_t + row * scale
                t = 2 if scale >= 12 else 1
                if row == 0 or cell_index("rooms", col, row - 1) != idx:
                    im.fill_rect(x, y, scale, t, edge)
                if row == h - 1 or cell_index("rooms", col, row + 1) != idx:
                    im.fill_rect(x, y + scale - t, scale, t, edge)
                if col == 0 or cell_index("rooms", col - 1, row) != idx:
                    im.fill_rect(x, y, t, scale, edge)
                if col == w - 1 or cell_index("rooms", col + 1, row) != idx:
                    im.fill_rect(x + scale - t, y, t, scale, edge)

    # ---- 3. zone hatch ---------------------------------------------------
    if on("zones") and scale >= 4:
        for row in range(h):
            for col in range(w):
                idx = cell_index("zones", col, row)
                if not idx:
                    continue
                e = entry("zones", idx) or {}
                tint = (86, 132, 196) if e.get("kind") == "stockpile" else (104, 160, 88)
                x = gutter_l + col * scale
                y = gutter_t + row * scale
                # A thin per-cell outline, not a corner tick and not a wash. A
                # wash would hide the floor; the corner tick the first draft
                # used was a small filled square that read as a PAWN, which is
                # also a small filled square. Contiguous zone cells outlined
                # this way form a mesh that is unmistakably a region marking,
                # and a thing sitting on a zone cell still shows the ring
                # around it.
                im.stroke_rect(x, y, scale, scale, tint, 1)

    # ---- 4. roof edge ----------------------------------------------------
    if on("roof") and scale >= 6:
        for row in range(h):
            for col in range(w):
                if not cell_index("roof", col, row):
                    continue
                x = gutter_l + col * scale
                y = gutter_t + row * scale
                im.fill_rect(x + scale - 2, y, 2, scale, (44, 46, 52))

    # ---- 5. things -------------------------------------------------------
    if on("things"):
        for row in range(h):
            for col in range(w):
                idx = cell_index("things", col, row)
                if not idx:
                    continue
                e = entry("things", idx)
                if e is None:
                    continue
                x = gutter_l + col * scale
                y = gutter_t + row * scale
                colr = _thing_color(catalog, e)
                inset = 1 if scale >= 6 else 0
                im.fill_rect(x + inset, y + inset,
                             scale - 2 * inset, scale - 2 * inset, colr)
                if e.get("door"):
                    im.stroke_rect(x, y, scale, scale, DOOR,
                                   2 if scale >= 8 else 1)

    # ---- 6. glyph labels, one per building instance -----------------------
    if on("things") and scale >= 12:
        for lab in dump.get("labels") or []:
            e = entry("things", lab.get("p", 0))
            if e is None:
                continue
            gx, gz = lab["at"][0], lab["at"][1]
            sw, sh = (lab.get("size") or [1, 1])[:2]
            if lab.get("rot") in ("East", "West"):
                sw, sh = sh, sw
            # `at` is the building's own origin cell; centre the token on its
            # footprint, then clip to the rendered rect.
            if not (ox <= gx < ox + w and oz <= gz < oz + h):
                continue
            x, y = px(gx, gz)
            cx = x + (sw * scale) // 2
            cy = y + scale // 2 - ((sh - 1) * scale) // 2
            glyph = _glyph_for(catalog, e)
            if glyph:
                im.text_centered(cx, cy, glyph, _ink_on(_thing_color(catalog, e)),
                                 scale=2 if scale >= 20 else 1)

    # ---- 7. pawns --------------------------------------------------------
    if on("pawns"):
        for row in range(h):
            for col in range(w):
                idx = cell_index("pawns", col, row)
                if not idx:
                    continue
                e = entry("pawns", idx) or {}
                mark = PAWN_MARKS.get(e.get("kind"), (240, 240, 240))
                x = gutter_l + col * scale
                y = gutter_t + row * scale
                r = max(2, scale // 2)
                im.fill_rect(x + (scale - r) // 2, y + (scale - r) // 2, r, r, mark)
                im.stroke_rect(x + (scale - r) // 2, y + (scale - r) // 2, r, r, (16, 16, 18), 1)

    # ---- 8. landmarks ----------------------------------------------------
    if landmarks:
        for name in sorted(landmarks):
            lx, lz = landmarks[name][0], landmarks[name][1]
            if not (ox <= lx < ox + w and oz <= lz < oz + h):
                continue
            x, y = px(lx, lz)
            im.stroke_rect(x, y, scale, scale, LANDMARK, 2 if scale >= 8 else 1)
            if scale >= 8:
                im.text(x + scale + 2, y + scale // 2 - FONT_H // 2,
                        name.upper(), LANDMARK, 1)

    # ---- 9. rulers -------------------------------------------------------
    if ruler:
        _draw_rulers(im, ox, oz, w, h, scale, gutter_l, gutter_t)

    # ---- 10. legend ------------------------------------------------------
    if legend_rows:
        _draw_legend(im, legend_rows, 4, gutter_t + h * scale + pad + 6, iw)

    return im


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


def _glyph_for(catalog, e: dict) -> str:
    if catalog is not None:
        try:
            return catalog.spec_for(e["def"], e.get("stuff"))["glyph"]
        except Exception:
            pass
    name = e["def"]
    name = name.split("_", 1)[-1] if "_" in name else name
    caps = [c for c in name if c.isupper()]
    return ("".join(caps[:2]) if len(caps) >= 2 else name[:2]).upper()


def _ink_on(rgb) -> tuple[int, int, int]:
    """Black or white, whichever reads on `rgb`. viewer.js:121's rule."""
    lum = 0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2]
    return (17, 17, 17) if lum > 140 else (238, 238, 238)


def _draw_rulers(im, ox, oz, w, h, scale, gl, gt):
    """Coordinate marks in MAP coordinates, every 10 cells plus both ends.

    DESIGN calls for "ASCII viewports with coordinate rulers"; the same applies
    here, and it is what lets a reader turn "the door is there" into a cell the
    agent can pass back to a verb.
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


def _legend_rows(dump: dict, pal: dict, catalog) -> list[tuple]:
    """(swatch_rgb | None, glyph, text) rows. Order is palette order — stable."""
    rows: list[tuple] = []
    hdr = f"{dump['w']}x{dump['h']} AT {dump['origin'][0]},{dump['origin'][1]}"
    fogged = dump.get("fogged_cells") or 0
    if fogged:
        hdr += f"  FOGGED {fogged}"
    hdr += f"  ALPHABET {dump.get('channel', {}).get('alphabet', '?').upper()}"
    if catalog is None:
        hdr += "  (NO CATALOG: COLOURS ARE FALLBACKS)"
    rows.append((None, "", hdr))

    room_entries = [(i, e) for i, e in enumerate(pal.get("rooms") or [])
                    if i and e and not e.get("outdoors")]
    if room_entries:
        rows.append((None, "", f"ROOMS ({len(room_entries)})"))
        for i, e in room_entries[:14]:
            role = (e.get("role") or "room").upper()
            rows.append((_room_tint(i), "", f"{role}  ID {e.get('id')}  {e.get('cells')} CELLS"))
        if len(room_entries) > 14:
            rows.append((None, "", f"... {len(room_entries) - 14} MORE"))

    things = [(i, e) for i, e in enumerate(pal.get("things") or []) if i and e]
    if things:
        rows.append((None, "", "THINGS"))
        for i, e in things[:18]:
            label = (e.get("label") or e.get("def") or "?").upper()
            if e.get("door"):
                label += "  (DOOR)"
            rows.append((_thing_color(catalog, e), _glyph_for(catalog, e), label))
        if len(things) > 18:
            rows.append((None, "", f"... {len(things) - 18} MORE"))

    zones = [(i, e) for i, e in enumerate(pal.get("zones") or []) if i and e]
    if zones:
        rows.append((None, "", "ZONES"))
        for i, e in zones[:8]:
            kind = (e.get("kind") or "zone").upper()
            lbl = (e.get("label") or "").upper()
            plant = e.get("plant")
            txt = f"{kind}  {lbl}"
            if plant:
                txt += f"  GROWING {plant.upper()}"
            elif e.get("kind") == "growing":
                txt += "  UNCONFIGURED"
            rows.append(((86, 132, 196) if e.get("kind") == "stockpile" else (104, 160, 88),
                         "", txt))

    pawns = [(i, e) for i, e in enumerate(pal.get("pawns") or []) if i and e]
    if pawns:
        rows.append((None, "", f"PAWNS ({len(pawns)})"))
        for i, e in pawns[:10]:
            rows.append((PAWN_MARKS.get(e.get("kind"), (240, 240, 240)), "",
                         f"{(e.get('name') or '?').upper()}  {(e.get('kind') or '').upper()}"))
        if len(pawns) > 10:
            rows.append((None, "", f"... {len(pawns) - 10} MORE"))
    return rows


# Legend column offsets from the strip's left edge: swatch, glyph, label.
# The glyph gets its OWN column rather than being drawn inside the 12px
# swatch, where two characters at text scale 1 were a smudge. Here it is set at
# scale 2 in the same alphabet the grid uses, which is the point — the legend
# is how a reader learns that `ES` on the map means the electric stove.
_LG_SWATCH, _LG_GLYPH, _LG_LABEL = 2, 20, 48


def _draw_legend(im, rows, x0, y0, iw):
    im.hline(x0, y0 - 4, iw - 2 * x0, RULE)
    y = y0
    for swatch, glyph, text in rows:
        if swatch is not None:
            im.fill_rect(x0 + _LG_SWATCH, y + 1, 14, 14, swatch)
            im.stroke_rect(x0 + _LG_SWATCH, y + 1, 14, 14, (10, 10, 12), 1)
            if glyph:
                im.text(x0 + _LG_GLYPH, y + 1, glyph, INK, 2)
            im.text(x0 + _LG_LABEL, y + 1, text, INK, 2)
        else:
            im.text(x0 + _LG_SWATCH, y + 1, text,
                    DIM if text.startswith("...") else INK, 2)
        y += 18
