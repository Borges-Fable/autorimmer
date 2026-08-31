"""A deterministic PNG encoder and a minimal RGB drawing surface, stdlib only.

New in this repo (spec 2.5) — not vendored; baseviz had no raster code at all.

WHY THIS FILE EXISTS AT ALL
---------------------------
Spec 2.5 asked for "PNG via baseviz-style canvas", and DESIGN.md calls baseviz
a "colored-grid renderer". Neither was true. `canvas.py`'s own docstring says
"Two surfaces, both ASCII"; the colored grid was `static/viewer.js`, an HTML5
canvas in a browser page, and the three `final_render.png` files upstream were
browser screenshots (three differently-sized layouts, all exactly 1400x1100 —
nothing in the tree could regenerate them). So the raster encoder had to be
written, whichever way we went. This is it.

WHY NOT PILLOW
--------------
Two reasons, and the second is the one that matters:

1. `rwa`'s house rule (rwa:49-51, rwa/README.md:20) is "stdlib only, python3,
   no third-party deps — this has to run from a bare shell on either bench."
   Pillow happens to be installed on this box; it is not on the other.

2. The acceptance is "same dump twice -> byte-identical PNG". Pillow's encoder
   output is not pinned across its own versions, so with Pillow that acceptance
   would be an article of faith. Here it is a property of ~60 lines we control:
   fixed filter rule, fixed compression level, no timestamp, no text chunks.

Determinism caveat, stated rather than hidden: the IDAT bytes come from zlib,
so two DIFFERENT zlib builds could in principle deflate the same input to
different (equally valid) bytes. The acceptance compares two renders of one
dump on one machine, which this satisfies absolutely. Cross-machine byte
equality is not claimed and is not what the spec asks for.
"""

from __future__ import annotations

import struct
import zlib

__all__ = ["Image", "write_png", "encode_png", "text_width", "FONT_W", "FONT_H"]

_SIG = b"\x89PNG\r\n\x1a\n"


# --------------------------------------------------------------------------
# encoder
# --------------------------------------------------------------------------
def _chunk(tag: bytes, data: bytes) -> bytes:
    return (struct.pack(">I", len(data)) + tag + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))


def encode_png(width: int, height: int, rgb: bytes | bytearray, *, level: int = 9) -> bytes:
    """Encode a raw RGB buffer (3 bytes per pixel, row-major) as PNG bytes.

    Filter rule is FIXED, not adaptive: scanline 0 uses None(0), every other
    scanline uses Up(2). Adaptive filtering would pick per-row minimum-sum
    filters and compress a little better, but the choice would then depend on
    pixel content in a way that is tedious to reason about; a fixed rule is
    trivially deterministic and happens to suit this content, since a grid
    render is full of vertically-uniform bands that Up collapses to zeroes.
    """
    if len(rgb) != width * height * 3:
        raise ValueError(f"buffer is {len(rgb)} bytes, expected {width * height * 3}")
    stride = width * 3
    raw = bytearray()
    prev = bytes(stride)
    for y in range(height):
        row = bytes(rgb[y * stride:(y + 1) * stride])
        if y == 0:
            raw.append(0)
            raw += row
        else:
            raw.append(2)
            raw += bytes((row[i] - prev[i]) & 0xFF for i in range(stride))
        prev = row

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return (_SIG
            + _chunk(b"IHDR", ihdr)
            + _chunk(b"IDAT", zlib.compress(bytes(raw), level))
            + _chunk(b"IEND", b""))


def write_png(path, width: int, height: int, rgb: bytes | bytearray, *, level: int = 9) -> int:
    """Write a PNG and return the byte count."""
    data = encode_png(width, height, rgb, level=level)
    with open(path, "wb") as fh:
        fh.write(data)
    return len(data)


# --------------------------------------------------------------------------
# a 5x7 bitmap font
# --------------------------------------------------------------------------
# Uppercase, digits and a little punctuation. Lowercase is deliberately absent:
# the two consumers are catalog glyph tokens (already uppercase by
# `catalog._glyph`) and the legend strip, which upcases its labels. Carrying 26
# more hand-drawn glyphs to set a legend in mixed case is not worth the bytes.
FONT_W, FONT_H = 5, 7

_FONT = {
    " ": (0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000),
    "A": (0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001),
    "B": (0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110),
    "C": (0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110),
    "D": (0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110),
    "E": (0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111),
    "F": (0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000),
    "G": (0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111),
    "H": (0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001),
    "I": (0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110),
    "J": (0b00111, 0b00010, 0b00010, 0b00010, 0b00010, 0b10010, 0b01100),
    "K": (0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001),
    "L": (0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111),
    "M": (0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001),
    "N": (0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001),
    "O": (0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110),
    "P": (0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000),
    "Q": (0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101),
    "R": (0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001),
    "S": (0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110),
    "T": (0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100),
    "U": (0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110),
    "V": (0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100),
    "W": (0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b11011, 0b10001),
    "X": (0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001),
    "Y": (0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100),
    "Z": (0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111),
    "0": (0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110),
    "1": (0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110),
    "2": (0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111),
    "3": (0b11111, 0b00010, 0b00100, 0b00010, 0b00001, 0b10001, 0b01110),
    "4": (0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010),
    "5": (0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110),
    "6": (0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110),
    "7": (0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000),
    "8": (0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110),
    "9": (0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100),
    ".": (0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b01100, 0b01100),
    ",": (0b00000, 0b00000, 0b00000, 0b00000, 0b01100, 0b00100, 0b01000),
    ":": (0b00000, 0b01100, 0b01100, 0b00000, 0b01100, 0b01100, 0b00000),
    ";": (0b00000, 0b01100, 0b01100, 0b00000, 0b01100, 0b00100, 0b01000),
    "-": (0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000),
    "+": (0b00000, 0b00100, 0b00100, 0b11111, 0b00100, 0b00100, 0b00000),
    "=": (0b00000, 0b00000, 0b11111, 0b00000, 0b11111, 0b00000, 0b00000),
    "/": (0b00001, 0b00010, 0b00010, 0b00100, 0b01000, 0b01000, 0b10000),
    "#": (0b01010, 0b01010, 0b11111, 0b01010, 0b11111, 0b01010, 0b01010),
    "?": (0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b00000, 0b00100),
    "!": (0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00000, 0b00100),
    "@": (0b01110, 0b10001, 0b10111, 0b10101, 0b10110, 0b10000, 0b01110),
    "$": (0b00100, 0b01111, 0b10100, 0b01110, 0b00101, 0b11110, 0b00100),
    "&": (0b01100, 0b10010, 0b10100, 0b01000, 0b10101, 0b10010, 0b01101),
    "^": (0b00100, 0b01010, 0b10001, 0b00000, 0b00000, 0b00000, 0b00000),
    "%": (0b11000, 0b11001, 0b00010, 0b00100, 0b01000, 0b10011, 0b00011),
    "*": (0b00000, 0b10101, 0b01110, 0b11111, 0b01110, 0b10101, 0b00000),
    "_": (0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b11111),
    "~": (0b00000, 0b00000, 0b01000, 0b10101, 0b00010, 0b00000, 0b00000),
    "(": (0b00010, 0b00100, 0b01000, 0b01000, 0b01000, 0b00100, 0b00010),
    ")": (0b01000, 0b00100, 0b00010, 0b00010, 0b00010, 0b00100, 0b01000),
    "[": (0b01110, 0b01000, 0b01000, 0b01000, 0b01000, 0b01000, 0b01110),
    "]": (0b01110, 0b00010, 0b00010, 0b00010, 0b00010, 0b00010, 0b01110),
    "'": (0b00100, 0b00100, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000),
    '"': (0b01010, 0b01010, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000),
}
_MISSING = (0b11111, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11111)


def text_width(s: str, scale: int = 1, tracking: int = 1) -> int:
    """Pixel width of `s` as `Image.text` would draw it."""
    if not s:
        return 0
    return (len(s) * (FONT_W + tracking) - tracking) * scale


# --------------------------------------------------------------------------
# drawing surface
# --------------------------------------------------------------------------
class Image:
    """A mutable RGB raster. Origin top-left, y down — PNG's own convention.

    Callers working in RimWorld coordinates (z up) convert on the way in; see
    `render.py`, which does it in exactly one place.
    """

    def __init__(self, width: int, height: int, bg=(0, 0, 0)):
        if width <= 0 or height <= 0:
            raise ValueError(f"degenerate image {width}x{height}")
        self.w = width
        self.h = height
        self.buf = bytearray(bytes(bg) * (width * height))

    # -- primitives --
    def set_px(self, x: int, y: int, rgb) -> None:
        if 0 <= x < self.w and 0 <= y < self.h:
            i = (y * self.w + x) * 3
            self.buf[i] = rgb[0]
            self.buf[i + 1] = rgb[1]
            self.buf[i + 2] = rgb[2]

    def fill_rect(self, x: int, y: int, w: int, h: int, rgb) -> None:
        """Filled axis-aligned rectangle, clipped to the surface."""
        if w <= 0 or h <= 0:
            return
        x0, y0 = max(0, x), max(0, y)
        x1, y1 = min(self.w, x + w), min(self.h, y + h)
        if x0 >= x1 or y0 >= y1:
            return
        row = bytes(rgb) * (x1 - x0)
        for yy in range(y0, y1):
            i = (yy * self.w + x0) * 3
            self.buf[i:i + len(row)] = row

    def stroke_rect(self, x: int, y: int, w: int, h: int, rgb, width: int = 1) -> None:
        """Rectangle outline drawn INSIDE the given bounds."""
        if w <= 0 or h <= 0:
            return
        width = max(1, min(width, w, h))
        self.fill_rect(x, y, w, width, rgb)
        self.fill_rect(x, y + h - width, w, width, rgb)
        self.fill_rect(x, y, width, h, rgb)
        self.fill_rect(x + w - width, y, width, h, rgb)

    def hline(self, x: int, y: int, w: int, rgb) -> None:
        self.fill_rect(x, y, w, 1, rgb)

    def vline(self, x: int, y: int, h: int, rgb) -> None:
        self.fill_rect(x, y, 1, h, rgb)

    # -- text --
    def text(self, x: int, y: int, s: str, rgb, scale: int = 1, tracking: int = 1) -> int:
        """Draw `s` with its top-left at (x, y). Returns the advance in pixels.

        Unknown characters draw as a filled box rather than vanishing, so a
        missing glyph is visible in the output instead of silently shortening
        the label.
        """
        cx = x
        for ch in s:
            rows = _FONT.get(ch)
            if rows is None:
                rows = _FONT.get(ch.upper(), _MISSING)
            for ry, bits in enumerate(rows):
                if not bits:
                    continue
                for rx in range(FONT_W):
                    if bits & (1 << (FONT_W - 1 - rx)):
                        if scale == 1:
                            self.set_px(cx + rx, y + ry, rgb)
                        else:
                            self.fill_rect(cx + rx * scale, y + ry * scale,
                                           scale, scale, rgb)
            cx += (FONT_W + tracking) * scale
        return cx - x

    def text_centered(self, cx: int, cy: int, s: str, rgb, scale: int = 1) -> None:
        """Draw `s` centered on (cx, cy)."""
        self.text(cx - text_width(s, scale) // 2, cy - (FONT_H * scale) // 2,
                  s, rgb, scale)

    # -- output --
    def write(self, path, *, level: int = 9) -> int:
        return write_png(path, self.w, self.h, self.buf, level=level)

    def encode(self, *, level: int = 9) -> bytes:
        return encode_png(self.w, self.h, self.buf, level=level)
