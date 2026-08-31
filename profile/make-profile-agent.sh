#!/usr/bin/env bash
# Build/refresh the _RimWorld-Agent profile: the AutoRimmer agent bench.
# Spec 0.1 (git-bug 3fa4cf5); pattern copied from _RimWorld-Test/make-profile.sh.
#
# The ONLY RimWorld install the agent may ever launch. Never _RimWorld-Testing,
# never the MP install, never Steam-attached.
#
# Scope: DESIGN.md §Bench v1 mod set — infra + all own vanilla+DLC mods +
# visitor cluster (Hospitality/Gastronomy/Storefront) + Guests/RegisterLanes
# (flagged deviation, see FINDINGS.md) + transitive deps resolved from
# About.xml at build time. All DLCs. No CE, no PerspectiveShift (SeekAndKill
# verified standalone — PSInterop.Init returns early when PS is absent).
#
# Machine-portable via three env overrides, all with the defaults this box uses:
#   RIMWORLD_VAULT  the mod-repo workspace   (default /home/dorian/projects/rimworld)
#   RIMWORLD_STEAM  the Steam RimWorld dir   (default $HOME/.steam/.../common/RimWorld)
#   RIMWORLD_TOOLS  the rimworld-tools repo  (default <vault>/../rimworld-tools)
# Anything it cannot find is reported as MISSING SRC and counted, never guessed.
#
# How curation works: gen-modsconfig.py activates whatever is present in Mods/,
# so the active set IS the symlink set created below. Engine + Data are
# symlinked from the Steam install (tiny, auto-tracks game updates). Re-running
# is idempotent (ln -sfn). gen-modsconfig.py + run-agent.sh are COPIED in from
# this repo's profile/ dir — the repo is the source of truth, the profile is a
# disposable build artifact.
set -u

HERE="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"   # autorimmer/profile
REPOS="${RIMWORLD_VAULT:-/home/dorian/projects/rimworld}"
ROOT="$REPOS/_RimWorld-Agent"
STEAM="${RIMWORLD_STEAM:-$HOME/.steam/steam/steamapps/common/RimWorld}"
TOOLS="${RIMWORLD_TOOLS:-$(dirname "$REPOS")/rimworld-tools}"
MP="$REPOS/_upstream/Mods"               # canonical shared infra copies
TESTING="$REPOS/_RimWorld-Testing/Mods"  # local library for third-party content

mkdir -p "$ROOT"

missing=0
link() {  # link <dest-relpath> <source-abspath>
    local dest="$ROOT/$1" src="$2"
    if [ ! -e "$src" ]; then
        echo "  MISSING SRC: $1  ($src)" >&2
        missing=$((missing + 1))
        return
    fi
    ln -sfn "$src" "$dest"
    echo "  linked $1"
}

echo "== engine + Data =="
# The player stub must be a REAL COPY, not a symlink: Unity roots the game dir
# via /proc/self/exe, which canonicalizes symlinks — a linked RimWorldLinux
# re-roots the whole game at the Steam install, whose Mods/ (a farm of dev
# symlinks, no Harmony) silently replaces this profile's Mods/. Found the hard
# way in spike 0.1 (git-bug 3fa4cf5); _RimWorld-Test has the same latent bug,
# _RimWorld-Testing avoids it by owning full real copies of everything.
cp -f "$STEAM/RimWorldLinux" "$ROOT/RimWorldLinux" && chmod +x "$ROOT/RimWorldLinux" \
    && echo "  copied RimWorldLinux (real file: keeps the game rooted here)"
# RimWorldLinux_Data: real dir, symlinked children — dataPath stays in the
# profile (in case Unity canonicalizes it) while the bulk stays on Steam's copy.
[ -L "$ROOT/RimWorldLinux_Data" ] && rm "$ROOT/RimWorldLinux_Data"
mkdir -p "$ROOT/RimWorldLinux_Data"
for d in "$STEAM/RimWorldLinux_Data"/*; do
    ln -sfn "$d" "$ROOT/RimWorldLinux_Data/$(basename "$d")"
done
echo "  built RimWorldLinux_Data (real dir, $(ls "$ROOT/RimWorldLinux_Data" | wc -l) symlinked children)"
for f in UnityPlayer.so Version.txt Data; do
    link "$f" "$STEAM/$f"
done

mkdir -p "$ROOT/Mods"

echo "== infra =="
link "Mods/Harmony"                 "$MP/Harmony"
link "Mods/LogRelay"                "$REPOS/logrelay"
link "Mods/AnalyzerBridge"          "$REPOS/analyzerbridge"
link "Mods/DubsPerformanceAnalyzer" "$TESTING/DubsPerformanceAnalyzer"
# BaseVizCatalogDumper is GONE as a separate mod (spec 2.5). Its 143 lines are
# now Source/AutoRimmer/CatalogDump.cs and its startup file-write is the
# `catalog-dump` verb, so the catalog is produced on request into the protocol
# root instead of appearing as a side effect of launching the game. This used
# to link $TOOLS/baseviz/BaseVizCatalogDumper, which was also the last live
# dependency this bench had on the unversioned rimworld-tools directory.
#
# Removed rather than merely unlinked: an existing bench already has the
# junction, and leaving it would keep a second mod in the modlist whose
# [StaticConstructorOnStartup] still writes a catalog nothing reads.
if [ -e "$ROOT/Mods/BaseVizCatalogDumper" ] || [ -L "$ROOT/Mods/BaseVizCatalogDumper" ]; then
    rm -f "$ROOT/Mods/BaseVizCatalogDumper"
    echo "  removed Mods/BaseVizCatalogDumper (folded into AutoRimmer, spec 2.5)"
fi
# AutoRimmer itself: the repo root IS the mod folder, matching every sibling
# mod repo (analyzerbridge, logrelay, Factions: About/ + Assemblies/ + Source/
# at root, alongside docs) and spec 1.1's root-relative paths.
#
# Guarded on About/ rather than on the directory: the repo root always exists,
# so an unguarded link would put an About-less folder in Mods/ the moment the
# path was corrected — RimWorld logs that, and this bench holds a zero-red-
# errors invariant. Until spec 1.1 writes About/, say so and link nothing.
if [ -f "$(dirname "$HERE")/About/About.xml" ]; then
    link "Mods/AutoRimmer"          "$(dirname "$HERE")"
else
    rm -f "$ROOT/Mods/AutoRimmer"
    echo "  skipped Mods/AutoRimmer (no About/About.xml yet — spec 1.1 creates it)"
fi

echo "== own vanilla+DLC mods (live repos) =="
link "Mods/Factions"                "$REPOS/Factions"
link "Mods/SeekAndKill"             "$REPOS/seekandkill"
link "Mods/FindSuitableWeaponAndAmmo" "$REPOS/findsuitableweaponandammo"
link "Mods/RandomResearch"          "$REPOS/randomresearch"
link "Mods/MechPatrol"              "$REPOS/mechpatrol"
link "Mods/JoyVariety"              "$REPOS/joyvariety"
link "Mods/FuzzyRoomRequirements"   "$REPOS/Rooms"
link "Mods/RetryFailedSurgery"      "$REPOS/retryfailedsurgery"
link "Mods/Church"                  "$REPOS/church"
link "Mods/CruelAndUnusualPunishment" "$REPOS/prisonsentenceslikehemocasket"
link "Mods/Fingerkill"              "$REPOS/fingerkill"
link "Mods/AutoQuest"               "$REPOS/autoquest"
link "Mods/CoherentBionics"         "$REPOS/CoherentBionics"
link "Mods/Nepobaby"                "$REPOS/nepo"
link "Mods/DisableLeaveBadWeather"  "$REPOS/disableleavebadweather"
link "Mods/NoMoreAlarms"            "$REPOS/nomorealarms"
link "Mods/RealisticGasses"         "$REPOS/gas"
link "Mods/RealisticHeatDeath"      "$REPOS/realisticheatdeath"
link "Mods/SuperHotFire"            "$REPOS/superhotfire"
link "Mods/WirelessChargingMech"    "$REPOS/wirelesschargingmech"
link "Mods/Music"                   "$REPOS/Music"

echo "== visitor cluster + dependents (Guests/RegisterLanes: flagged deviation) =="
link "Mods/Hospitality"             "$TESTING/Hospitality"
link "Mods/Gastronomy"              "$TESTING/Gastronomy"
link "Mods/Storefront"              "$TESTING/Storefront"
link "Mods/Guests"                  "$REPOS/Guests"
link "Mods/RegisterLanes"           "$REPOS/RegisterLanes"

echo "== transitive deps (resolved from About.xml at build time) =="
# Walk modDependencies of the whole third-party set + Guests/RegisterLanes,
# closing over a library index (_RimWorld-Testing/Mods, _upstream/Mods).
# Links anything required that isn't already in $ROOT/Mods. ludeon.* (DLCs)
# and already-linked packageIds are skipped.
python3 - "$ROOT/Mods" "$TESTING" "$MP" <<'EOF'
import sys, xml.etree.ElementTree as ET
from pathlib import Path

mods_dir = Path(sys.argv[1])
libraries = [Path(p) for p in sys.argv[2:]]

def about_of(d: Path):
    for sub in d.iterdir() if d.is_dir() else []:
        if sub.is_dir() and sub.name.lower() == "about":
            for f in sub.iterdir():
                if f.name.lower() == "about.xml":
                    return f
    return None

def parse(about: Path):
    try:
        root = ET.parse(about).getroot()
    except ET.ParseError:
        return None, []
    pkg = (root.findtext("packageId") or "").strip().lower()
    deps = []
    md = root.find("modDependencies")
    if md is not None:
        deps = [(li.findtext("packageId") or "").strip().lower()
                for li in md.findall("li")]
    return pkg, [d for d in deps if d]

# Index the libraries: packageId -> source dir (first hit wins)
index = {}
for lib in libraries:
    if not lib.is_dir():
        continue
    for d in sorted(lib.iterdir()):
        a = about_of(d)
        if not a:
            continue
        pkg, _ = parse(a)
        if pkg and pkg not in index:
            index[pkg] = d

# What's already linked (by packageId)
present = {}
for d in sorted(mods_dir.iterdir()):
    a = about_of(d)
    if a:
        pkg, _ = parse(a)
        if pkg:
            present[pkg] = d

# BFS over deps of everything present
queue = list(present.values())
seen_dirs = set()
missing = []
while queue:
    d = queue.pop(0)
    if d in seen_dirs:
        continue
    seen_dirs.add(d)
    a = about_of(d)
    if not a:
        continue
    _, deps = parse(a)
    for dep in deps:
        if dep.startswith("ludeon.rimworld") or dep in present:
            continue
        src = index.get(dep)
        if src is None:
            missing.append(dep)
            print(f"  UNRESOLVED dep: {dep} (needed by {d.name})", file=sys.stderr)
            continue
        dest = mods_dir / src.name
        if not dest.exists():
            dest.symlink_to(src)
            print(f"  linked Mods/{src.name}  (dep: {dep})")
        present[dep] = dest
        queue.append(dest)

if missing:
    sys.exit(1)
EOF
[ $? -ne 0 ] && missing=$((missing + 1))

echo "== launcher + modsconfig generator (copied from repo; repo is source of truth) =="
install -m 0755 "$HERE/gen-modsconfig.py" "$ROOT/gen-modsconfig.py" && echo "  copied gen-modsconfig.py"
install -m 0755 "$HERE/run-agent.sh"      "$ROOT/run-agent.sh"      && echo "  copied run-agent.sh"

echo "== seed Prefs.xml (only if absent) =="
PREFS_DIR="$ROOT/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config"
mkdir -p "$PREFS_DIR"
if [ ! -f "$PREFS_DIR/Prefs.xml" ]; then
    cat > "$PREFS_DIR/Prefs.xml" <<'PREFS'
<?xml version="1.0" encoding="utf-8"?>
<PrefsData>
  <volumeMaster>0</volumeMaster>
  <volumeGame>0</volumeGame>
  <volumeMusic>0</volumeMusic>
  <volumeAmbient>0</volumeAmbient>
  <volumeUI>0</volumeUI>
  <screenWidth>1280</screenWidth>
  <screenHeight>768</screenHeight>
  <fullscreen>False</fullscreen>
  <uiScale>1</uiScale>
  <customCursorEnabled>False</customCursorEnabled>
  <runInBackground>True</runInBackground>
  <edgeScreenScroll>False</edgeScreenScroll>
  <temperatureMode>Celsius</temperatureMode>
  <autosaveIntervalDays>1</autosaveIntervalDays>
  <maxNumberOfPlayerSettlements>1</maxNumberOfPlayerSettlements>
  <pauseOnLoad>False</pauseOnLoad>
  <automaticPauseMode>Never</automaticPauseMode>
  <adaptiveTrainingEnabled>False</adaptiveTrainingEnabled>
  <pauseOnError>False</pauseOnError>
  <devMode>True</devMode>
  <langFolderName>English</langFolderName>
</PrefsData>
PREFS
    echo "  wrote Prefs.xml (runInBackground=True, devMode=True, muted, windowed 1280x768)"
else
    echo "  Prefs.xml exists, left untouched"
fi

echo
echo "Done. $(find "$ROOT/Mods" -maxdepth 1 -mindepth 1 | wc -l) mods linked, $missing missing source(s)."
echo "Next: $ROOT/gen-modsconfig.py   then   $ROOT/run-agent.sh"
