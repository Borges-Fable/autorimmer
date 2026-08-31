"""Hand-editable ASCII canvas <-> IR, plus a read-only composite view.

Two surfaces, both ASCII, both north-up (row 0 = south = bottom, matching the game
and the viewer):

- **canvas** (`to_canvas`/`from_canvas`): the EDIT surface. A self-contained text
  file - a META header (the non-grid IR fields), a LEGEND (2-char code <-> token),
  and one @LAYER grid per layer plus @TERRAIN and @ROOF. Each grid line is prefixed
  with its true grid-row index. Round-trips losslessly. You place every tile by hand
  here; nothing in this module decides where anything goes.

- **composite_view**: the read-only JUDGE surface. Flattens the layers to whatever is
  on top per cell (furniture over conduit over structure over terrain), so one map
  reads like the rendered viewer - without a browser.

Vendored from rimworld-tools/baseviz @ eabba3e by spec 2.5, unchanged. See
README.md in this directory. Note that despite spec 2.5 and DESIGN.md both
describing baseviz as a "colored-grid renderer", BOTH surfaces here are ASCII;
the raster channel is png.py + render.py, which are new.
"""
from __future__ import annotations

import json
import re
from collections import Counter

EMPTY = "."
EMPTY_CELL = ".."


# --- code assignment (unique, reversible; readable for dominant structure) -----
def _assign_codes(ir: dict) -> dict:
    cnt: Counter = Counter()
    for layer in ir["layers"]:
        for row in layer:
            for t in row:
                if t != EMPTY:
                    cnt[t] += 1
    for row in ir["terrain"]:
        for t in row:
            if t != EMPTY:
                cnt[t] += 1
    code: dict = {}
    used = {EMPTY_CELL}

    def most(pred):
        cands = [t for t in cnt if pred(t)]
        return max(cands, key=lambda t: cnt[t]) if cands else None

    for sym, tok in [("##", most(lambda t: t.startswith("Wall"))),
                     ("++", most(lambda t: t.startswith("Door"))),
                     ("[]", most(lambda t: t.startswith("FenceGate"))),
                     ("==", most(lambda t: t.startswith("Fence")
                                 and not t.startswith("FenceGate")))]:
        if tok and tok not in code:
            code[tok] = sym
            used.add(sym)

    alph = "0123456789abcdefghijklmnopqrstuvwxyz"
    for t in sorted(cnt, key=lambda t: (-cnt[t], t)):
        if t in code:
            continue
        base = re.split(r"_", t)[0]
        cand = (base[:2] if len(base) >= 2 else base + "X")
        cand = cand[0].upper() + cand[1].lower()
        if cand in used:
            i = 0
            while True:
                cand = base[0].upper() + alph[i]
                i += 1
                if cand not in used:
                    break
        used.add(cand)
        code[t] = cand
    return code


# --- canvas (edit surface) ----------------------------------------------------
def to_canvas(ir: dict) -> str:
    W, H = ir["size"]
    code = _assign_codes(ir)
    meta = {k: ir.get(k) for k in
            ("defName", "spawnConduits", "size", "modRequirements",
             "extension", "animalCells")}
    meta["nlayers"] = len(ir["layers"])

    out = ["#!baseviz-canvas v1",
           "#META " + json.dumps(meta, separators=(",", ":")),
           "#LEGEND  (2-char code = token; add a line here before using a new code)",
           "#  .. = (empty)"]
    for t, c in sorted(code.items(), key=lambda kv: kv[1]):
        out.append(f"#  {c} = {t}")
    out.append("# north is up. left number = grid row (row 0 = south).")
    out.append("# edit the @ planes by hand; every cell is exactly 2 chars wide.")

    ruler = "    " + "".join(f"{x%10} " for x in range(W))

    def grid(rows):
        lines = [ruler]
        for r in range(H - 1, -1, -1):
            row = rows[r] if r < len(rows) else []
            cells = "".join(code.get(row[x], EMPTY_CELL) if x < len(row)
                            and row[x] != EMPTY else EMPTY_CELL for x in range(W))
            lines.append(f"{r:3d}|{cells}")
        return lines

    for li, layer in enumerate(ir["layers"]):
        out.append(f"@LAYER{li}")
        out += grid(layer)
    out.append("@TERRAIN")
    out += grid(ir["terrain"])
    out.append("@ROOF")
    out.append("# roof plane: ## roofed / .. open")
    out.append(ruler)
    roof = ir["roof"]
    for r in range(H - 1, -1, -1):
        row = roof[r] if r < len(roof) else []
        cells = "".join("##" if (x < len(row) and row[x]) else ".."
                        for x in range(W))
        out.append(f"{r:3d}|{cells}")
    return "\n".join(out) + "\n"


def from_canvas(text: str) -> dict:
    meta = None
    code = {EMPTY_CELL: EMPTY}
    planes: dict = {}
    cur = None
    for line in text.splitlines():
        if line.startswith("#META "):
            meta = json.loads(line[6:])
            continue
        if line.startswith("#  ") and " = " in line:
            c, t = line[3:].split(" = ", 1)
            c, t = c.strip(), t.strip()
            code[c] = EMPTY if t == "(empty)" else t
            continue
        if line.startswith("#"):
            continue
        if line.startswith("@"):
            cur = line[1:].strip().split()[0]
            planes[cur] = {}
            continue
        m = re.match(r"\s*(\d+)\|(.*)$", line)
        if m and cur is not None:
            planes[cur][int(m.group(1))] = m.group(2)

    W, H = meta["size"]

    def build(name, default=EMPTY, roof=False):
        g = [[default for _ in range(W)] for _ in range(H)]
        for r, cells in planes.get(name, {}).items():
            if not (0 <= r < H):
                continue
            for x in range(W):
                cc = cells[x * 2:x * 2 + 2]
                if len(cc) < 2:
                    break
                g[r][x] = (1 if cc == "##" else 0) if roof else code.get(cc, EMPTY)
        return g

    n = meta.get("nlayers", 2)
    return {"defName": meta["defName"],
            "spawnConduits": meta.get("spawnConduits", False),
            "layers": [build(f"LAYER{i}") for i in range(n)],
            "terrain": build("TERRAIN"),
            "roof": build("ROOF", 0, roof=True),
            "size": [W, H],
            "modRequirements": meta.get("modRequirements", []),
            "extension": meta.get("extension"),
            "animalCells": meta.get("animalCells", [])}


# --- composite view (judge surface, read-only) --------------------------------
_DISP = [("Wall", "##"), ("Door", "++"), ("FenceGate", "[]"), ("Fence", "=="),
         ("Bedroll", "bd"), ("Bed", "BD"), ("Dresser", "DR"), ("EndTable", "et"),
         ("Table1", "TB"), ("Table2", "TB"), ("TableButcher", "BU"),
         ("TableMachining", "MC"), ("TableStonecutter", "SC"), ("Table", "TB"),
         ("DiningChair", "ch"), ("Chair", "ch"), ("Stool", "st"),
         ("ShelfSmall", "sh"), ("Shelf", "SH"), ("Cooler", "CO"), ("Vent", "VE"),
         ("Battery", "BT"), ("SolarGenerator", "SO"), ("WindTurbine", "WT"),
         ("Turret", "TU"), ("Sandbag", "sb"), ("Barricade", "sb"),
         ("FueledStove", "ST"), ("ElectricStove", "ST"), ("FueledSmithy", "SM"),
         ("ElectricSmithy", "SM"), ("SimpleResearchBench", "RE"),
         ("HiTechResearchBench", "RE"), ("ButcherSpot", "BU"),
         ("CraftingSpot", "cr"), ("Sculpture", "AR"),
         ("TorchLamp", "**"), ("StandingLamp", "**"), ("Campfire", "cf"),
         ("PlantPot", "pp"), ("Grave", "gv"), ("Chunk", "cs"), ("Filth", "::"),
         ("PenMarker", "PM"),
         ("Steel", "St"), ("Plasteel", "Pl"), ("Silver", "Si"), ("Gold", "Au"),
         ("ComponentSpacer", "Cs"), ("ComponentIndustrial", "Cp"),
         ("Component", "Cp"), ("Cloth", "Cl"), ("WoodLog", "Wd"),
         ("MedicineHerbal", "Me"), ("MedicineIndustrial", "Me"), ("Medicine", "Me"),
         ("Pemmican", "Pe"), ("Leather", "Le"), ("Meat", "mt"), ("Hay", "Ha"),
         ("RawRice", "rr"), ("GoJuice", "gj"), ("Tome", "Tm")]


def _disp(t: str) -> str:
    for k, c in _DISP:
        if t.startswith(k):
            return c
    return "??"


def _terr(t: str) -> str:
    if t in (".", ""):
        return "  "
    if "Soil" in t or "Grass" in t:
        return "''"
    if "Water" in t or "Marsh" in t:
        return "≈≈"
    if "Concrete" in t:
        return "``"
    if "Flagstone" in t or t.startswith("Tile"):
        return "~~"
    return ".."


def composite_view(ir: dict) -> str:
    W, H = ir["size"]
    layers, TER = ir["layers"], ir["terrain"]
    animals = {tuple(a["offset"]): a["kind"] for a in ir.get("animalCells", [])}

    def cell(x, y):
        if (x, y) in animals:
            return "()"
        nonc, cond = None, False
        for L in reversed(layers):
            t = L[y][x] if x < len(L[y]) else EMPTY
            if t == EMPTY:
                continue
            if "Conduit" in t:
                cond = True
                continue
            nonc = t
            break
        if nonc:
            return _disp(nonc)
        if cond:
            return ".."
        return _terr(TER[y][x] if y < len(TER) and x < len(TER[y]) else EMPTY)

    out = [f"{ir['defName']}  {W}x{H}  (composite; north up; top-of-stack, conduit underlaid)",
           "    " + "".join(f"{x%10} " for x in range(W))]
    for r in range(H - 1, -1, -1):
        out.append(f"{r:3d}|" + "".join(cell(x, r) for x in range(W)))
    out.append(
        "\nlegend  ## wall ++ door == fence [] gate  BD bed DR dresser TB table ch chair "
        "SH/sh shelf\n  ST stove SM smithy MC machining SC stonecut RE research BU butcher "
        "AR sculpture ** lamp\n  CO cooler VE vent BT battery SO solar TU turret sb sandbag "
        "cs chunk PM penmarker () animal\n  items: St Pl Si Cp Cl Wd Me Pe Le Ha  | terrain: "
        "'' grass  .. floor  `` concrete  ~~ flagstone  (blank) open")
    return "\n".join(out) + "\n"
