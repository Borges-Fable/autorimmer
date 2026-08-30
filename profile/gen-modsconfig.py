#!/usr/bin/env python3
"""Generate ModsConfig.xml for the _RimWorld-Agent bench profile.

Adapted from _RimWorld-Test/gen-modsconfig.py (copied per spec 0.1 — copy,
don't symlink across repos). Scans Mods/*/About/About.xml for packageIds,
places DLCs + infrastructure + the visitor cluster in dependency order, then
appends remaining mods alphabetically for determinism.

Run from inside the built profile (make-profile-agent.sh copies this file
there): writes config/unity3d/.../Config/ModsConfig.xml matching the
XDG_CONFIG_HOME isolation set by run-agent.sh.
"""
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

AGENT_ROOT = Path(__file__).parent.resolve()
MODS_DIR = AGENT_ROOT / "Mods"
CONFIG_OUT = AGENT_ROOT / "config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config/ModsConfig.xml"

# Read the game version to write into ModsConfig (RimWorld validates this)
VERSION_TXT = AGENT_ROOT / "Version.txt"
game_version = VERSION_TXT.read_text().strip().split(" ")[0] if VERSION_TXT.exists() else "1.6.0"

# Canonical load order — early entries load first.
# Items not in this list go alphabetically in the middle.
#
# Ordering constraints baked in (from the mods' About.xml, resolved 2026-08-30):
#   Orion.CashRegister    before Gastronomy + Storefront (modDependency)
#   Orion.Hospitality     before Gastronomy? (no) / before Guests + Music (dep/loadAfter)
#   Orion.Gastronomy      before Storefront? (no) / before Guests + RegisterLanes
#   Adamas.Storefront     before Guests + RegisterLanes
#   Dorian.Guests         before Dorian.Factions (Factions loadAfter Guests) —
#                         alphabetical middle would put factions first, so Guests
#                         is pinned here.
LOAD_ORDER_HEAD = [
    "ludeon.rimworld",
    "ludeon.rimworld.royalty",
    "ludeon.rimworld.ideology",
    "ludeon.rimworld.biotech",
    "ludeon.rimworld.anomaly",
    "ludeon.rimworld.odyssey",
    "brrainz.harmony",
    "dorian.logrelay",            # error-triage relay — early to catch startup errors
    "orion.cashregister",         # visitor-cluster transitive dep (Gastronomy, Storefront)
    "orion.hospitality",
    "orion.gastronomy",
    "adamas.storefront",
    "dorian.guests",              # before Factions (Factions loadAfter Guests)
]

# These load LAST: perf analyzer + its bridge, then AutoRimmer (observes
# everything; its patches must see the final state of the mod stack).
LOAD_ORDER_TAIL = [
    "dubwise.dubsperformanceanalyzer",
    "dubwise.dubsperformanceanalyzer.steam",
    "dorian.analyzerbridge",
    "dorian.autorimmer",
]

def read_package_id(about_xml: Path) -> str | None:
    """Extract the top-level <packageId> from <ModMetaData>.

    Must NOT pick up nested <packageId> tags inside <modDependencies><li>...
    which list the mod's *dependencies*, not its own ID.
    """
    text = about_xml.read_text(encoding="utf-8", errors="replace").lstrip("﻿")
    try:
        root = ET.fromstring(text)
    except ET.ParseError:
        # Fallback: regex but only match packageId NOT inside a <li> block.
        cleaned = re.sub(r"<modDependencies\b.*?</modDependencies>", "", text, flags=re.S | re.I)
        cleaned = re.sub(r"<loadAfter\b.*?</loadAfter>", "", cleaned, flags=re.S | re.I)
        cleaned = re.sub(r"<loadBefore\b.*?</loadBefore>", "", cleaned, flags=re.S | re.I)
        cleaned = re.sub(r"<incompatibleWith\b.*?</incompatibleWith>", "", cleaned, flags=re.S | re.I)
        m = re.search(r"<packageId>(.*?)</packageId>", cleaned, re.S | re.I)
        return m.group(1).strip().lower() if m else None
    pkg_el = root.find("packageId")
    if pkg_el is not None and pkg_el.text:
        return pkg_el.text.strip().lower()
    return None

def find_about(mod_dir: Path) -> Path | None:
    for sub in mod_dir.iterdir():
        if sub.is_dir() and sub.name.lower() == "about":
            for f in sub.iterdir():
                if f.name.lower() == "about.xml":
                    return f
    return None

# Scan all mod directories
discovered: dict[str, str] = {}  # packageId -> mod folder name
for mod_dir in sorted(MODS_DIR.iterdir()):
    if not mod_dir.is_dir():
        continue
    about = find_about(mod_dir)
    if not about:
        print(f"  WARN no About.xml: {mod_dir.name}", file=sys.stderr)
        continue
    pkg = read_package_id(about)
    if not pkg:
        print(f"  WARN no packageId: {mod_dir.name}", file=sys.stderr)
        continue
    if pkg in discovered:
        print(f"  WARN duplicate packageId {pkg}: {mod_dir.name} (already have {discovered[pkg]})", file=sys.stderr)
        continue
    discovered[pkg] = mod_dir.name

print(f"Found {len(discovered)} packageIds in {MODS_DIR}", file=sys.stderr)

# Build ordered list: HEAD first (only if present), then alpha middle, then TAIL (only if present)
ordered: list[str] = []
seen: set[str] = set()

for pkg in LOAD_ORDER_HEAD:
    # DLCs are always added even if not discovered (they live in Data/, not Mods/)
    if pkg.startswith("ludeon.rimworld") or pkg in discovered:
        ordered.append(pkg)
        seen.add(pkg)

middle = sorted(p for p in discovered if p not in seen and p not in LOAD_ORDER_TAIL)
ordered.extend(middle)
seen.update(middle)

for pkg in LOAD_ORDER_TAIL:
    if pkg in discovered:
        ordered.append(pkg)
        seen.add(pkg)

# Build XML
lines = [
    '<?xml version="1.0" encoding="utf-8"?>',
    '<ModsConfigData>',
    f'  <version>{game_version}</version>',
    '  <activeMods>',
]
for pkg in ordered:
    lines.append(f'    <li>{pkg}</li>')
lines.append('  </activeMods>')
lines.append('  <knownExpansions>')
for pkg in LOAD_ORDER_HEAD[:6]:  # the six Ludeon entries
    lines.append(f'    <li>{pkg}</li>')
lines.append('  </knownExpansions>')
lines.append('</ModsConfigData>')

CONFIG_OUT.parent.mkdir(parents=True, exist_ok=True)
CONFIG_OUT.write_text("\n".join(lines) + "\n", encoding="utf-8")

print(f"\nWrote {CONFIG_OUT}")
print(f"  Active mods: {len(ordered)}")
print(f"  Game version: {game_version}")
print("\nLoad order:")
for i, pkg in enumerate(ordered, 1):
    src = discovered.get(pkg, "[Data/]" if pkg.startswith("ludeon") else "?")
    print(f"  {i:3d}. {pkg:55s} {src}")
