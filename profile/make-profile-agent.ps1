#!/usr/bin/env pwsh
# Build/refresh the _RimWorld-Agent profile on Windows (BORGES) — the AutoRimmer
# agent bench. Windows equivalent of make-profile-agent.sh, built per the muster's
# environment note when spec 1.1 first needed in-game acceptance on this machine.
# Same intent, adapted mechanisms:
#
#   symlink farm        -> junctions (dirs) + hardlinks/copies (files); no admin
#   XDG_CONFIG_HOME     -> -savedatafolder (run-agent.ps1); ModsConfig + Prefs
#                          live under SaveData\Config\
#   engine stub copy    -> RimWorldWin64.exe / UnityPlayer.dll / Version.txt are
#                          REAL COPIES, so the game stays rooted in the profile
#                          (the spike's /proc/self/exe re-rooting lesson, applied
#                          preemptively; the isolation assert in run-agent.ps1 is
#                          what proves it held)
#   mod sources         -> this machine has no sibling repo checkouts; own mods
#                          are the real copies in the Steam install's Mods\
#                          (verified byte-identical to the MP pack's, i.e. the
#                          dorian-published set), and the third-party cluster
#                          comes from the MP pack itself — dorian-pinned and
#                          version-coherent with Guests. NOT the Steam Workshop:
#                          workshop mods auto-update past what our mods patch
#                          (found live: Aug-3 workshop Storefront grew a
#                          TryGiveJob overload and Guests' Harmony PatchAll died
#                          AmbiguousMatch on it; the pack's Jun-16 Storefront is
#                          the one Guests is built against). The pack is a
#                          receive-only Syncthing mirror: junction TARGETS only,
#                          nothing is ever written inside it.
#                          Anything absent is reported MISSING, never guessed.
#
# The ONLY RimWorld install the agent may ever launch. Never the user's Steam
# launches, never the MP pack at C:\RimWorldPack. Reads from the Steam install
# are fine (junction targets); NOTHING is ever written into it.
#
# Env overrides (defaults are this box's paths):
#   RIMWORLD_VAULT  profile parent dir  (default C:\Users\EvanKornoelje\misc\rimworld)
#   RIMWORLD_STEAM  Steam RimWorld dir  (default C:\Program Files (x86)\Steam\steamapps\common\RimWorld)
#   RIMWORLD_PACK   MP pack mirror      (default C:\RimWorldPack\mp) — READ ONLY
$ErrorActionPreference = 'Stop'

$HERE = Split-Path -Parent $PSCommandPath                     # autorimmer/profile
$VAULT = if ($env:RIMWORLD_VAULT) { $env:RIMWORLD_VAULT } else { 'C:\Users\EvanKornoelje\misc\rimworld' }
$STEAM = if ($env:RIMWORLD_STEAM) { $env:RIMWORLD_STEAM } else { 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld' }
$PACK = if ($env:RIMWORLD_PACK) { $env:RIMWORLD_PACK } else { 'C:\RimWorldPack\mp' }
$ROOT = Join-Path $VAULT '_RimWorld-Agent'
$STEAM_MODS = Join-Path $STEAM 'Mods'
$PACK_MODS = Join-Path $PACK 'Mods'

New-Item -ItemType Directory -Force $ROOT | Out-Null

$script:missing = 0

# Junction (dirs): remove-and-recreate is idempotent. A junction is removed with
# rmdir, which deletes ONLY the reparse point — never Remove-Item -Recurse, which
# can walk into the target (the Steam install) and delete real files.
function Set-Junction([string]$DestRel, [string]$Src) {
    $dest = Join-Path $ROOT $DestRel
    if (-not (Test-Path $Src)) {
        Write-Host "  MISSING SRC: $DestRel  ($Src)"
        $script:missing++
        return
    }
    if (Test-Path $dest) {
        $item = Get-Item $dest -Force
        if ($item.LinkType) { cmd /c rmdir "$dest" | Out-Null }
        else { throw "refusing: $dest exists and is a real directory, not a junction" }
    }
    New-Item -ItemType Junction -Path $dest -Target $Src | Out-Null
    Write-Host "  linked $DestRel"
}

# Files: hardlink (same volume, zero space), copy as fallback.
function Set-FileLink([string]$DestRel, [string]$Src) {
    $dest = Join-Path $ROOT $DestRel
    if (-not (Test-Path $Src)) {
        Write-Host "  MISSING SRC: $DestRel  ($Src)"
        $script:missing++
        return
    }
    if (Test-Path $dest) { Remove-Item -Force $dest }
    try {
        New-Item -ItemType HardLink -Path $dest -Target $Src | Out-Null
        Write-Host "  hardlinked $DestRel"
    } catch {
        Copy-Item -Force $Src $dest
        Write-Host "  copied $DestRel (hardlink failed)"
    }
}

Write-Host '== engine + Data =='
# Real copies keep the game rooted in the profile; everything bulky rides on the
# Steam install via junctions. steam_appid.txt is deliberately NOT brought over —
# its absence (plus run-agent.ps1 unsetting the Steam env vars) is the detach.
foreach ($f in 'RimWorldWin64.exe', 'UnityPlayer.dll', 'UnityCrashHandler64.exe', 'Version.txt') {
    Copy-Item -Force (Join-Path $STEAM $f) (Join-Path $ROOT $f)
    Write-Host "  copied $f (real file: keeps the game rooted here)"
}
Set-Junction 'MonoBleedingEdge' (Join-Path $STEAM 'MonoBleedingEdge')
Set-Junction 'Data' (Join-Path $STEAM 'Data')

# RimWorldWin64_Data: real dir, junctioned subdirs + hardlinked files, so the
# dataPath string stays inside the profile while the bytes stay on Steam's copy.
$dataDir = Join-Path $ROOT 'RimWorldWin64_Data'
if ((Test-Path $dataDir) -and (Get-Item $dataDir -Force).LinkType) { cmd /c rmdir "$dataDir" | Out-Null }
New-Item -ItemType Directory -Force $dataDir | Out-Null
$dataChildren = 0
foreach ($child in Get-ChildItem (Join-Path $STEAM 'RimWorldWin64_Data') -Force) {
    $rel = "RimWorldWin64_Data\$($child.Name)"
    if ($child.PSIsContainer) { Set-Junction $rel $child.FullName } else { Set-FileLink $rel $child.FullName }
    $dataChildren++
}
Write-Host "  built RimWorldWin64_Data (real dir, $dataChildren linked children)"

New-Item -ItemType Directory -Force (Join-Path $ROOT 'Mods') | Out-Null

Write-Host '== infra =='
Set-Junction 'Mods\Harmony'                 (Join-Path $PACK_MODS 'Harmony')
Set-Junction 'Mods\LogRelay'                (Join-Path $STEAM_MODS 'LogRelay')
Set-Junction 'Mods\AnalyzerBridge'          (Join-Path $STEAM_MODS 'AnalyzerBridge')
Set-Junction 'Mods\DubsPerformanceAnalyzer' (Join-Path $PACK_MODS 'DubsPerformanceAnalyzer')
# BaseVizCatalogDumper is GONE as a separate mod (spec 2.5) — its 143 lines are
# now Source\AutoRimmer\CatalogDump.cs and its startup file-write is the
# `catalog-dump` verb. Removed rather than merely unlinked, for the reason the
# Linux twin gives: an existing bench already has the junction, and leaving it
# keeps a second mod whose startup dump nothing reads.
$bvcd = Join-Path $ROOT 'Mods\BaseVizCatalogDumper'
if (Test-Path $bvcd) {
    if ((Get-Item $bvcd -Force).LinkType) { cmd /c rmdir "$bvcd" | Out-Null }
    else { Remove-Item $bvcd -Recurse -Force }
    Write-Host '  removed Mods\BaseVizCatalogDumper (folded into AutoRimmer, spec 2.5)'
}
# AutoRimmer itself: the repo root IS the mod folder (spec 1.1). Guarded on
# About\About.xml exactly like the Linux script: an About-less folder in Mods\
# draws a red-error-adjacent log line, and this bench holds zero-red-errors.
$repoRoot = Split-Path -Parent $HERE
if (Test-Path (Join-Path $repoRoot 'About\About.xml')) {
    Set-Junction 'Mods\AutoRimmer' $repoRoot
} else {
    $ar = Join-Path $ROOT 'Mods\AutoRimmer'
    if ((Test-Path $ar) -and (Get-Item $ar -Force).LinkType) { cmd /c rmdir "$ar" | Out-Null }
    Write-Host '  skipped Mods\AutoRimmer (no About\About.xml yet — spec 1.1 creates it)'
}

Write-Host '== own vanilla+DLC mods (real copies in the Steam install Mods\) =='
foreach ($m in 'Factions', 'SeekAndKill', 'FindSuitableWeaponAndAmmo', 'RandomResearch',
               'MechPatrol', 'JoyVariety', 'FuzzyRoomRequirements', 'RetryFailedSurgery',
               'Church', 'CruelAndUnusualPunishment', 'Fingerkill', 'AutoQuest',
               'CoherentBionics', 'Nepobaby', 'DisableLeaveBadWeather', 'NoMoreAlarms',
               'RealisticGasses', 'RealisticHeatDeath', 'SuperHotFire',
               'WirelessChargingMech', 'Music') {
    Set-Junction "Mods\$m" (Join-Path $STEAM_MODS $m)
}

Write-Host '== visitor cluster + dependents (pack-pinned third-party + Steam Mods) =='
Set-Junction 'Mods\CashRegister' (Join-Path $PACK_MODS 'CashRegister')
Set-Junction 'Mods\Hospitality'  (Join-Path $PACK_MODS 'Hospitality')
Set-Junction 'Mods\Gastronomy'   (Join-Path $PACK_MODS 'Gastronomy')
Set-Junction 'Mods\Storefront'   (Join-Path $PACK_MODS 'Storefront')
Set-Junction 'Mods\Guests'        (Join-Path $STEAM_MODS 'Guests')
Set-Junction 'Mods\RegisterLanes' (Join-Path $STEAM_MODS 'RegisterLanes')

Write-Host '== dependency check (modDependencies of everything linked) =='
# The Linux script RESOLVES missing deps from a library; here every candidate
# source is already linked explicitly, so this pass only VERIFIES the closure
# and reports holes. Unresolved deps count as missing, never guessed at.
function Get-AboutXml([string]$modDir) {
    $about = Get-ChildItem $modDir -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ieq 'About' } | Select-Object -First 1
    if (-not $about) { return $null }
    Get-ChildItem $about.FullName -File -Force | Where-Object { $_.Name -ieq 'About.xml' } | Select-Object -First 1
}
$present = @{}
$deps = @{}   # depId -> first mod that needs it
foreach ($modDir in Get-ChildItem (Join-Path $ROOT 'Mods') -Directory -Force) {
    $aboutFile = Get-AboutXml $modDir.FullName
    if (-not $aboutFile) { continue }
    try { $xml = [xml](Get-Content $aboutFile.FullName -Raw) } catch { continue }
    $pkg = "$($xml.ModMetaData.packageId)".Trim().ToLower()
    if ($pkg) { $present[$pkg] = $modDir.Name }
    foreach ($li in $xml.SelectNodes('//modDependencies/li/packageId')) {
        $dep = "$($li.InnerText)".Trim().ToLower()
        if ($dep -and -not $deps.ContainsKey($dep)) { $deps[$dep] = $modDir.Name }
    }
}
foreach ($dep in $deps.Keys | Sort-Object) {
    if ($dep.StartsWith('ludeon.rimworld') -or $present.ContainsKey($dep)) { continue }
    Write-Host "  UNRESOLVED dep: $dep (needed by $($deps[$dep]))"
    $script:missing++
}

Write-Host '== launcher + modsconfig generator (copied from repo; repo is source of truth) =='
Copy-Item -Force (Join-Path $HERE 'gen-modsconfig.ps1') (Join-Path $ROOT 'gen-modsconfig.ps1')
Write-Host '  copied gen-modsconfig.ps1'
Copy-Item -Force (Join-Path $HERE 'run-agent.ps1') (Join-Path $ROOT 'run-agent.ps1')
Write-Host '  copied run-agent.ps1'

Write-Host '== seed SaveData\Config\Prefs.xml (only if absent) =='
$prefsDir = Join-Path $ROOT 'SaveData\Config'
New-Item -ItemType Directory -Force $prefsDir | Out-Null
$prefsPath = Join-Path $prefsDir 'Prefs.xml'
if (-not (Test-Path $prefsPath)) {
    @'
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
'@ | Set-Content -NoNewline -Encoding utf8NoBOM $prefsPath
    Write-Host '  wrote Prefs.xml (runInBackground=True, devMode=True, muted, windowed 1280x768)'
} else {
    Write-Host '  Prefs.xml exists, left untouched'
}

Write-Host ''
$modCount = (Get-ChildItem (Join-Path $ROOT 'Mods') -Directory -Force).Count
Write-Host "Done. $modCount mods linked, $($script:missing) missing source(s)."
Write-Host "Next: $ROOT\gen-modsconfig.ps1   then   $ROOT\run-agent.ps1"
