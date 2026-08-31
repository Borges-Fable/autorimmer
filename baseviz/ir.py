"""Convert between KCSG StructureLayoutDef XML and the baseviz IR (grid JSON).

The IR mirrors the KCSG structure so conversion is lossless and round-trips:

    {
      "defName": str,
      "size": [w, h],
      "spawnConduits": bool,
      "layers":  [ [[token, ...], ...rows], ...layers ],   # things, by z-layer
      "terrain": [[token, ...], ...rows],
      "roof":    [[0|1, ...], ...rows],
      "modRequirements": [str, ...],
      "extension": {... BaseLayoutExtension fields ...} | None,
      "animalCells": [{"kind": str, "offset": [x, y]}, ...],
    }

A "token" is a raw layout cell string (e.g. ``Wall_WoodLog`` or ``.``); the
catalog's token parser interprets it for rendering. Grids are row-major; row 0 is
the top row as written in the XML.

Vendored from rimworld-tools/baseviz @ eabba3e by spec 2.5, unchanged. See
README.md in this directory. Note for whoever takes spec 3.3: XML -> IR is
lossy at the edges — `_EXT_SCALAR`/`_EXT_LIST` are hardcoded whitelists,
`size` is derived from row lengths rather than read, and `to_xml` does no XML
escaping. Worth pinning with a written schema before it becomes a contract.
"""

from __future__ import annotations

import json
import xml.etree.ElementTree as ET
from pathlib import Path

LAYOUT_DEF_TAG = "KCSG.StructureLayoutDef"
EXTENSION_CLASS = "BetterBases.BaseLayoutExtension"

# BaseLayoutExtension fields and whether each is a list (<li> children).
_EXT_SCALAR = ("size", "techLevel", "weight", "maxPawns", "familyId",
               "stage", "tierMin", "tierMax")
_EXT_LIST = ("factionTags", "requiredMods", "optionalMods")


def _rows(elem: ET.Element) -> list[list[str]]:
    """Read a grid element's <li> rows into lists of comma-split tokens."""
    out = []
    for li in elem.findall("li"):
        text = li.text or ""
        out.append([t.strip() for t in text.split(",")] if text.strip() else [])
    return out


def parse_xml(path: str | Path) -> dict:
    root = ET.parse(path).getroot()
    node = root if root.tag == LAYOUT_DEF_TAG else root.find(LAYOUT_DEF_TAG)
    if node is None:
        node = next((e for e in root.iter(LAYOUT_DEF_TAG)), None)
    if node is None:
        raise ValueError(f"no {LAYOUT_DEF_TAG} in {path}")
    return _parse_node(node)


def _parse_node(node: ET.Element) -> dict:
    ir: dict = {"defName": node.findtext("defName", "").strip()}
    sc = node.findtext("spawnConduits")
    ir["spawnConduits"] = (sc or "").strip().lower() == "true"

    layouts_el = node.find("layouts")
    layers = [_rows(layer) for layer in layouts_el.findall("li")] if layouts_el is not None else []
    ir["layers"] = layers

    terrain_el = node.find("terrainGrid")
    ir["terrain"] = _rows(terrain_el) if terrain_el is not None else []

    roof_el = node.find("roofGrid")
    roof_rows = _rows(roof_el) if roof_el is not None else []
    ir["roof"] = [[1 if c == "1" else 0 for c in row] for row in roof_rows]

    # Dimensions from the first non-empty grid we can find.
    ref = (layers[0] if layers else None) or ir["terrain"] or roof_rows
    h = len(ref) if ref else 0
    w = max((len(r) for r in ref), default=0) if ref else 0
    ir["size"] = [w, h]

    req = node.find("modRequirements")
    ir["modRequirements"] = [li.text.strip() for li in req.findall("li")
                             if li.text and li.text.strip()] if req is not None else []

    ir["extension"], ir["animalCells"] = _parse_extension(node)
    return ir


def _parse_extension(node: ET.Element):
    ext_root = node.find("modExtensions")
    if ext_root is None:
        return None, []
    ext_el = next((li for li in ext_root.findall("li")
                   if li.get("Class") == EXTENSION_CLASS), None)
    if ext_el is None:
        return None, []
    ext: dict = {}
    for tag in _EXT_SCALAR:
        v = ext_el.findtext(tag)
        if v is not None:
            ext[tag] = v.strip()
    for tag in _EXT_LIST:
        el = ext_el.find(tag)
        if el is not None:
            ext[tag] = [li.text.strip() for li in el.findall("li")
                        if li.text and li.text.strip()]
    animals = []
    ac = ext_el.find("animalCells")
    if ac is not None:
        for li in ac.findall("li"):
            kind = li.findtext("kind", "").strip()
            off = (li.findtext("offset") or "").strip().strip("()")
            nums = [int(n) for n in off.replace(" ", "").split(",") if n.lstrip("-").isdigit()]
            animals.append({"kind": kind, "offset": nums[:2] if len(nums) >= 2 else [0, 0]})
    return ext, animals


# ---- IR -> XML ----------------------------------------------------------
def to_xml(ir: dict) -> str:
    sb: list[str] = ['<?xml version="1.0" encoding="utf-8"?>', "<Defs>",
                     f"<{LAYOUT_DEF_TAG}>"]
    sb.append(f"  <defName>{ir['defName']}</defName>")
    sb.append(f"  <spawnConduits>{'true' if ir.get('spawnConduits') else 'false'}</spawnConduits>")

    sb.append("  <layouts>")
    for layer in ir.get("layers", []):
        sb.append("    <li>")
        for row in layer:
            sb.append(f"      <li>{','.join(row)}</li>")
        sb.append("    </li>")
    sb.append("  </layouts>")

    roof = ir.get("roof", [])
    if roof:
        sb.append("  <roofGrid>")
        for row in roof:
            sb.append(f"      <li>{','.join('1' if c else '.' for c in row)}</li>")
        sb.append("  </roofGrid>")

    terrain = ir.get("terrain", [])
    if terrain:
        sb.append("  <terrainGrid>")
        for row in terrain:
            sb.append(f"      <li>{','.join(row)}</li>")
        sb.append("  </terrainGrid>")

    req = ir.get("modRequirements", [])
    if req:
        sb.append("  <modRequirements>")
        sb.extend(f"    <li>{m}</li>" for m in req)
        sb.append("  </modRequirements>")

    ext = ir.get("extension")
    if ext is not None:
        sb.append("  <modExtensions>")
        sb.append(f'    <li Class="{EXTENSION_CLASS}">')
        for tag in _EXT_SCALAR:
            if tag in ext:
                sb.append(f"      <{tag}>{ext[tag]}</{tag}>")
        for tag in _EXT_LIST:
            if ext.get(tag):
                sb.append(f"      <{tag}>")
                sb.extend(f"        <li>{v}</li>" for v in ext[tag])
                sb.append(f"      </{tag}>")
        animals = ir.get("animalCells", [])
        if animals:
            sb.append("      <animalCells>")
            for a in animals:
                ox, oy = (a.get("offset") or [0, 0])[:2]
                sb.append("        <li>")
                sb.append(f"          <kind>{a['kind']}</kind>")
                sb.append(f"          <offset>({ox},{oy})</offset>")
                sb.append("        </li>")
            sb.append("      </animalCells>")
        sb.append("    </li>")
        sb.append("  </modExtensions>")

    sb.append(f"</{LAYOUT_DEF_TAG}>")
    sb.append("</Defs>")
    return "\n".join(sb) + "\n"


def save_ir(ir: dict, path: str | Path):
    Path(path).write_text(json.dumps(ir, separators=(",", ":")))


def load_ir(path: str | Path) -> dict:
    return json.loads(Path(path).read_text())
