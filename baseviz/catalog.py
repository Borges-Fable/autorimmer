"""Build a catalog of placeable RimWorld defs from loose Defs XML.

The catalog feeds the viewer (colors, footprints) and gives Claude the real
def names to author with. It also carries the token parser that turns a layout
cell token like ``DiningChair_WoodLog_Totemic_North`` back into
def + stuff + style + rotation.

Usage:
    from baseviz.catalog import Catalog
    cat = Catalog.build(scope="vanilla")
    cat.save("catalog.json")

Vendored from rimworld-tools/baseviz @ eabba3e by spec 2.5. Changes are marked
`Vendored-in fix/change (2.5)` at their sites; see README.md in this directory.
"""

from __future__ import annotations

import json
import os
import re
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field, asdict
from pathlib import Path

def rw_root() -> Path:
    """The RimWorld instance to scan, from ``RIMWORLD_ROOT``. No default.

    Vendored-in change (2.5): upstream defaulted this to a hardcoded
    ``_RimWorld-Testing`` path. In this repo that install is forbidden to every
    agent (CLAUDE.md: the agent bench is ``_RimWorld-Agent`` and nothing else),
    and a default pointing at a forbidden install is worse than no default —
    it works quietly until it does the wrong thing to the wrong game. So this
    fails loudly and names the variable instead.

    Only the OFFLINE XML path needs it (``Catalog.build``, ``server.py``).
    ``Catalog.load`` takes an explicit path and never calls this, which is why
    the render channel — the one thing 2.5 actually ships — needs no env at all.
    """
    val = os.environ.get("RIMWORLD_ROOT")
    if not val:
        raise RuntimeError(
            "RIMWORLD_ROOT is not set. It must point at a RimWorld instance "
            "directory (the one containing Data/ and Mods/). There is "
            "deliberately no default: the upstream default was the "
            "_RimWorld-Testing install, which this repo forbids touching. "
            "The agent bench is _RimWorld-Agent. Note that `rwa render` does "
            "not need this variable — it reads the catalog the `catalog-dump` "
            "verb writes into the protocol root."
        )
    return Path(val)


ROTATIONS = ("North", "South", "East", "West")

# DesignationCategoryDef order (from Core) -> used to order the tree top level.
# Items/Terrain get synthetic top-level buckets since they are not buildable.
KNOWN_CATEGORY_ORDER = [
    "Structure", "Production", "Furniture", "Power", "Security",
    "Misc", "Floors", "Joy", "Ship", "Temperature", "Zone", "Orders",
]

# Fallback render color per top-level category, RGB 0-255.
CATEGORY_COLORS = {
    "Structure": (120, 120, 120),
    "Furniture": (150, 111, 67),
    "Production": (170, 120, 60),
    "Power": (200, 180, 60),
    "Security": (170, 70, 60),
    "Misc": (130, 130, 130),
    "Floors": (95, 90, 80),
    "Joy": (90, 150, 150),
    "Ship": (110, 120, 150),
    "Temperature": (90, 130, 170),
    "Items": (90, 120, 180),
    "Plants": (70, 130, 70),
    "Other": (110, 110, 110),
}


def _parse_color(text: str | None):
    """Parse a RimWorld ``(r,g,b)`` color (0-255 or 0-1 floats) to RGB ints."""
    if not text:
        return None
    nums = re.findall(r"[-\d.]+", text)
    if len(nums) < 3:
        return None
    vals = [float(n) for n in nums[:3]]
    if all(v <= 1.0 for v in vals):  # normalized floats
        vals = [v * 255 for v in vals]
    return tuple(int(round(max(0, min(255, v)))) for v in vals)


def _clean_color(c):
    """Drop RimWorld's untinted-white default so it falls back to a category color."""
    if not c:
        return None
    return None if list(c) == [255, 255, 255] else list(c)


def _glyph(defname: str) -> str:
    """Short label drawn on a cell: drop a common prefix, take initials/first chars."""
    name = defname.split("_", 1)[-1] if "_" in defname else defname
    # CamelCase initials (e.g. DiningChair -> DC); else first 2 chars.
    caps = [c for c in name if c.isupper()]
    return ("".join(caps[:2]) if len(caps) >= 2 else name[:2]).upper()


def _parse_size(text: str | None):
    if not text:
        return None
    nums = re.findall(r"\d+", text)
    if len(nums) >= 2:
        return [int(nums[0]), int(nums[1])]
    return None


@dataclass
class DefInfo:
    defName: str
    kind: str            # "thing" | "terrain"
    category: str = "Other"   # top-level tree bucket
    thingCategory: str = ""   # ThingDef.category (Building/Item/Plant/...)
    designationCategory: str = ""
    size: list = field(default_factory=lambda: [1, 1])
    rotatable: bool = False
    stuffable: bool = False
    isStuff: bool = False
    color: list | None = None     # explicit graphic/terrain color if any
    stuffColor: list | None = None  # if this def IS a stuff, its material color
    label: str = ""
    mod: str = ""


class Catalog:
    def __init__(self, defs: dict[str, DefInfo], scope: str):
        self.defs = defs
        self.scope = scope
        self._stuff_names = {d.defName for d in defs.values() if d.isStuff}
        # Sorted by length desc so the token parser prefers longer defName prefixes.
        # Vendored-in fix (2.5): the key was `len` with reverse=True. Python's
        # sort is STABLE, so equal-length defNames tie-broke on dict insertion
        # order — i.e. on the C# DefDatabase.AllDefs iteration order baked into
        # the dump. parse_token returns the FIRST prefix match, so two
        # equal-length names that both prefix a token could resolve differently
        # between two dumps of the same colony. Name is the tie-break now, which
        # makes resolution a pure function of the def SET, not of its order.
        self._names_by_len = sorted(defs.keys(), key=lambda n: (-len(n), n))

    # ---- loading --------------------------------------------------------
    @classmethod
    def default_dump_path(cls, root: Path | None = None) -> Path:
        root = root if root is not None else rw_root()
        return (root / "config" / "unity3d" / "Ludeon Studios"
                / "RimWorld by Ludeon Studios" / "Config" / "baseviz_catalog.json")

    @classmethod
    def load(cls, path: str | Path, scope: str = "full") -> "Catalog":
        """Load the authoritative catalog written by the in-game dumper.

        scope="vanilla" keeps only Core + DLC defs (by owning packageId), for
        designing portable bases with no mod dependencies.
        """
        raw = json.loads(Path(path).read_text())
        defs: dict[str, DefInfo] = {}
        for name, d in raw["defs"].items():
            if name.startswith(("Blueprint_", "Frame_")):
                continue  # construction artifacts, never placed in a layout
            if scope == "vanilla" and (d.get("mod") or "").lower() not in DLC_PACKAGE_IDS:
                continue
            info = DefInfo(
                defName=name, kind=d.get("kind", "thing"),
                thingCategory=d.get("thingCategory", ""),
                designationCategory=d.get("designationCategory", ""),
                size=d.get("size") or [1, 1],
                rotatable=bool(d.get("rotatable")),
                stuffable=bool(d.get("stuffable")),
                isStuff=bool(d.get("isStuff")),
                color=_clean_color(d.get("color")), stuffColor=_clean_color(d.get("stuffColor")),
                label=d.get("label", ""), mod=d.get("mod", ""),
            )
            info.category = _bucket(info.kind, info.thingCategory, info.designationCategory)
            defs[name] = info
        return cls(defs, scope="runtime-dump")

    # ---- building (offline XML fallback) --------------------------------
    @classmethod
    def build(cls, scope: str = "full", root: Path | None = None) -> "Catalog":
        paths = _scope_paths(scope, root if root is not None else rw_root())
        # Pass 1: collect every ThingDef/TerrainDef element (incl. abstracts)
        # keyed by Name (for inheritance) and accumulate concrete defNames.
        by_name: dict[str, ET.Element] = {}
        elements: list[tuple[ET.Element, str]] = []  # (elem, owning mod)
        for mod, defs_dir in paths:
            # Vendored-in fix (2.5): rglob is filesystem (scandir) order and the
            # merge below is last-writer-wins, so two machines could disagree on
            # a def. server.py:_discover_layouts already sorted its rglob; this
            # one did not.
            for xml_file in sorted(defs_dir.rglob("*.xml")):
                try:
                    root = ET.parse(xml_file).getroot()
                except ET.ParseError:
                    continue
                for elem in root.iter():
                    if elem.tag not in ("ThingDef", "TerrainDef"):
                        continue
                    name = elem.get("Name")
                    if name:
                        by_name[name] = elem
                    elements.append((elem, mod))

        defs: dict[str, DefInfo] = {}
        for elem, mod in elements:
            defname_el = elem.find("defName")
            if defname_el is None or not defname_el.text:
                continue  # abstract-only node
            merged = _resolve(elem, by_name)
            info = _extract(elem.tag, defname_el.text.strip(), merged, mod)
            defs[info.defName] = info
        return cls(defs, scope)

    def save(self, path: str | Path):
        out = {
            "scope": self.scope,
            "categoryOrder": KNOWN_CATEGORY_ORDER + ["Items", "Plants", "Other"],
            "categoryColors": CATEGORY_COLORS,
            "defs": {k: asdict(v) for k, v in sorted(self.defs.items())},
        }
        Path(path).write_text(json.dumps(out, separators=(",", ":")))

    def tree(self) -> dict:
        """Nested category -> designationCategory -> [defName] for the palette."""
        out: dict = {}
        for d in self.defs.values():
            sub = d.designationCategory or d.thingCategory or "_"
            out.setdefault(d.category, {}).setdefault(sub, []).append(d.defName)
        return out

    # ---- rendering ------------------------------------------------------
    def render_spec(self, token: str) -> dict | None:
        """Resolve a layout-cell TOKEN (``Def_Stuff_Style_Rot``) to a draw spec."""
        p = self.parse_token(token)
        if p is None:
            return None
        spec = self.spec_for(p["def"], p["stuff"], p["rot"])
        spec["token"] = token
        spec["style"] = p.get("style")
        spec["known"] = p["known"] and spec["known"]
        return spec

    def spec_for(self, defname: str, stuff: str | None = None,
                 rot: str | None = None) -> dict:
        """Resolve an ALREADY-SPLIT def (+ optional stuff, rotation) to a draw spec.

        Vendored-in addition (2.5). `render_spec` above takes a KCSG layout
        token and has to guess where the defName ends, because defNames may
        themselves contain underscores; that guess is where the token parser's
        longest-prefix ambiguity lives. The map dump has no such problem — the
        game hands us def and stuff as separate fields — so the render channel
        calls this and the ambiguity never enters the picture.
        """
        info = self.defs.get(defname)
        size = list(info.size) if info else [1, 1]
        if rot in ("East", "West") and len(size) == 2:
            size = [size[1], size[0]]
        return {
            "def": defname, "stuff": stuff, "rot": rot,
            "known": info is not None,
            "category": info.category if info else "Other",
            "color": self._color_for(info, stuff),
            "size": size,
            "glyph": _glyph(defname),
            "label": (info.label if info and info.label else defname),
        }

    def terrain_color(self, token: str) -> list:
        if not token or token == ".":
            return list(CATEGORY_COLORS["Floors"])
        info = self.defs.get(token)
        if info and info.color:
            return list(info.color)
        return list(CATEGORY_COLORS["Floors"])

    def _color_for(self, info: "DefInfo | None", stuff: str | None) -> list:
        if info and info.stuffable and stuff:
            sinfo = self.defs.get(stuff)
            if sinfo and (sinfo.stuffColor or sinfo.color):
                return list(sinfo.stuffColor or sinfo.color)
        if info and info.color:
            return list(info.color)
        cat = info.category if info else "Other"
        return list(CATEGORY_COLORS.get(cat, CATEGORY_COLORS["Other"]))

    def build_palettes(self, ir: dict) -> tuple[dict, dict]:
        """Distinct-token -> render spec, for things and terrain in an IR."""
        things, terrain = {}, {}
        for layer in ir.get("layers", []):
            for row in layer:
                for tok in row:
                    if tok and tok != "." and tok not in things:
                        spec = self.render_spec(tok)
                        if spec:
                            things[tok] = spec
        for row in ir.get("terrain", []):
            for tok in row:
                if tok and tok != "." and tok not in terrain:
                    terrain[tok] = {"color": self.terrain_color(tok),
                                    "label": (self.defs.get(tok).label
                                              if self.defs.get(tok) else tok)}
        return things, terrain

    # ---- token parsing --------------------------------------------------
    def parse_token(self, token: str) -> dict | None:
        """``Def[_Stuff][_Style][_Rotation]`` -> dict, or None for empty/'.'.

        Disambiguates against the catalog: defNames may contain underscores,
        so we take the longest known-defName prefix that leaves the rest as a
        valid stuff/style/rotation tail.
        """
        if not token or token == ".":
            return None
        rot = None
        body = token
        for r in ROTATIONS:
            if body.endswith("_" + r):
                rot, body = r, body[: -(len(r) + 1)]
                break
        # Try longest matching defName prefix.
        for name in self._names_by_len:
            if body == name:
                return {"def": name, "stuff": None, "style": None, "rot": rot,
                        "known": True}
            if body.startswith(name + "_"):
                tail = body[len(name) + 1:].split("_")
                stuff = None
                styles = []
                ok = True
                for seg in tail:
                    if seg in self._stuff_names and stuff is None:
                        stuff = seg
                    else:
                        styles.append(seg)
                style = "_".join(styles) if styles else None
                return {"def": name, "stuff": stuff, "style": style, "rot": rot,
                        "known": True}
        # Unknown def: return raw body so the caller can still render/flag it.
        return {"def": body, "stuff": None, "style": None, "rot": rot,
                "known": False}


# ---- inheritance --------------------------------------------------------
_FIELDS = (
    "size", "rotatable", "designationCategory", "category", "label",
    "stuffCategories", "graphicData", "stuffProps", "thingCategories", "color",
    "texturePath",
)


def _resolve(elem: ET.Element, by_name: dict[str, ET.Element], _seen=None) -> dict:
    """Merge a def with its ParentName chain. Child overrides parent."""
    _seen = _seen or set()
    parent_name = elem.get("ParentName")
    base: dict = {}
    if parent_name and parent_name in by_name and parent_name not in _seen:
        _seen.add(parent_name)
        base = _resolve(by_name[parent_name], by_name, _seen)
    for tag in _FIELDS:
        child = elem.find(tag)
        if child is not None:
            if child.get("Inherit") == "False" or tag not in base:
                base[tag] = child
            else:
                base[tag] = child  # scalar/element override
    return base


def _txt(merged: dict, tag: str):
    el = merged.get(tag)
    return el.text.strip() if el is not None and el.text else None


def _extract(kind_tag: str, defname: str, m: dict, mod: str) -> DefInfo:
    if kind_tag == "TerrainDef":
        return DefInfo(
            defName=defname, kind="terrain", category="Floors",
            designationCategory=_txt(m, "designationCategory") or "Floors",
            label=_txt(m, "label") or "",
            color=list(_parse_color(_txt(m, "color")) or ()) or None,
            mod=mod,
        )

    info = DefInfo(defName=defname, kind="thing", label=_txt(m, "label") or "", mod=mod)
    info.thingCategory = _txt(m, "category") or ""
    info.designationCategory = _txt(m, "designationCategory") or ""
    info.size = _parse_size(_txt(m, "size")) or [1, 1]
    rot = _txt(m, "rotatable")
    info.rotatable = (rot or "").lower() == "true"

    stuff_cats = m.get("stuffCategories")
    info.stuffable = stuff_cats is not None and len(list(stuff_cats)) > 0

    stuff_props = m.get("stuffProps")
    if stuff_props is not None:
        info.isStuff = True
        sc = stuff_props.find("color")
        info.stuffColor = list(_parse_color(sc.text if sc is not None else None) or ()) or None

    gd = m.get("graphicData")
    if gd is not None:
        col = gd.find("color")
        info.color = list(_parse_color(col.text if col is not None else None) or ()) or None

    info.category = _bucket(info.kind, info.thingCategory, info.designationCategory)
    return info


def _bucket(kind: str, thing_category: str, designation_category: str) -> str:
    """Map a def to its top-level palette bucket."""
    if kind == "terrain":
        return "Floors"
    if designation_category:
        return designation_category
    if thing_category == "Plant":
        return "Plants"
    if thing_category == "Building":
        return "Misc"
    if thing_category:
        return "Items"
    return "Other"


# ---- scope / mod discovery ---------------------------------------------
DLC_PACKAGE_IDS = {
    "ludeon.rimworld", "ludeon.rimworld.royalty", "ludeon.rimworld.ideology",
    "ludeon.rimworld.biotech", "ludeon.rimworld.anomaly", "ludeon.rimworld.odyssey",
}


def _scope_paths(scope: str, rw_root: Path) -> list[tuple[str, Path]]:
    """Return [(modLabel, defsDir)] to scan for the given scope."""
    out: list[tuple[str, Path]] = []
    # DLCs / Core always included (needed for parent resolution too).
    data = rw_root / "Data"
    if data.is_dir():
        for dlc in sorted(data.iterdir()):
            defs = dlc / "Defs"
            if defs.is_dir():
                out.append((f"Core/{dlc.name}", defs))
    if scope == "vanilla":
        return out

    mods_dir = rw_root / "Mods"
    pkg_to_path = _discover_mod_paths(mods_dir)
    if scope == "full":
        wanted = _load_order(rw_root)
        for pkg in wanted:
            if pkg in DLC_PACKAGE_IDS:
                continue
            path = pkg_to_path.get(pkg)
            if path and (path / "Defs").is_dir():
                out.append((pkg, path / "Defs"))
        return out
    if scope == "packs":
        # Mods referenced by BetterBases layout packs: detect from loadAfter /
        # modDependencies in pack About.xml. Pragmatic default: scan all mods
        # that ship StructureLayoutDefs plus their declared deps.
        for pkg, path in pkg_to_path.items():
            if (path / "Defs").is_dir():
                out.append((pkg, path / "Defs"))
        return out
    raise ValueError(f"unknown scope: {scope!r}")


def _discover_mod_paths(mods_dir: Path) -> dict[str, Path]:
    pkg_to_path: dict[str, Path] = {}
    if not mods_dir.is_dir():
        return pkg_to_path
    # Vendored-in fix (2.5): iterdir is filesystem order and the map below is
    # last-writer-wins, so two mods declaring the same packageId resolved to
    # whichever the filesystem happened to yield last. Same class as the rglob
    # above.
    for mod in sorted(mods_dir.iterdir()):
        about = mod / "About" / "About.xml"
        if not about.is_file():
            continue
        try:
            root = ET.parse(about).getroot()
        except ET.ParseError:
            continue
        pkg = root.findtext("packageId")
        if pkg:
            pkg_to_path[pkg.strip().lower()] = mod
    return pkg_to_path


def _load_order(rw_root: Path) -> list[str]:
    cfg = (rw_root / "config" / "unity3d" / "Ludeon Studios"
           / "RimWorld by Ludeon Studios" / "Config" / "ModsConfig.xml")
    if not cfg.is_file():
        return []
    try:
        root = ET.parse(cfg).getroot()
    except ET.ParseError:
        return []
    return [li.text.strip().lower() for li in root.iter("li")
            if li.text and li.text.strip()]
