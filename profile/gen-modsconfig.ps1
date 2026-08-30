#!/usr/bin/env pwsh
# Generate ModsConfig.xml for the _RimWorld-Agent bench profile — Windows port of
# gen-modsconfig.py (this machine has no python; the ordering rules are copied
# verbatim and the .py stays canonical for the Linux bench). One deliberate
# difference: output goes to SaveData\Config\ModsConfig.xml, matching the
# -savedatafolder isolation run-agent.ps1 uses (the Linux bench isolates via
# XDG_CONFIG_HOME, so its path is config/unity3d/...).
#
# Scans Mods/*/About/About.xml for packageIds, places DLCs + infrastructure +
# the visitor cluster in dependency order, then appends remaining mods
# alphabetically for determinism. Run from inside the built profile
# (make-profile-agent.ps1 copies this file there).
$ErrorActionPreference = 'Stop'

$AGENT_ROOT = Split-Path -Parent $PSCommandPath
$MODS_DIR = Join-Path $AGENT_ROOT 'Mods'
$CONFIG_OUT = Join-Path $AGENT_ROOT 'SaveData\Config\ModsConfig.xml'

# Game version for the <version> element (RimWorld validates this)
$versionTxt = Join-Path $AGENT_ROOT 'Version.txt'
$gameVersion = if (Test-Path $versionTxt) { ((Get-Content $versionTxt -Raw).Trim() -split ' ')[0] } else { '1.6.0' }

# Canonical load order — early entries load first. Items not listed go
# alphabetically in the middle. Constraints identical to gen-modsconfig.py:
#   CashRegister -> Hospitality -> Gastronomy -> Storefront -> Guests, all ahead
#   of the alphabetical middle; Guests pinned before Factions (loadAfter).
$LOAD_ORDER_HEAD = @(
    'ludeon.rimworld',
    'ludeon.rimworld.royalty',
    'ludeon.rimworld.ideology',
    'ludeon.rimworld.biotech',
    'ludeon.rimworld.anomaly',
    'ludeon.rimworld.odyssey',
    'brrainz.harmony',
    'dorian.logrelay',
    'orion.cashregister',
    'orion.hospitality',
    'orion.gastronomy',
    'adamas.storefront',
    'dorian.guests'
)

# These load LAST: perf analyzer + its bridge, then AutoRimmer (observes
# everything; its patches must see the final state of the mod stack).
$LOAD_ORDER_TAIL = @(
    'dubwise.dubsperformanceanalyzer',
    'dubwise.dubsperformanceanalyzer.steam',
    'dorian.analyzerbridge',
    'dorian.autorimmer'
)

function Read-PackageId([string]$aboutPath) {
    # Top-level <packageId> only — never the nested ones inside <modDependencies>.
    $text = (Get-Content $aboutPath -Raw).TrimStart([char]0xFEFF)
    try {
        $xml = [xml]$text
        $pkg = $xml.DocumentElement.SelectSingleNode('packageId')
        if ($pkg -and $pkg.InnerText) { return $pkg.InnerText.Trim().ToLower() }
        return $null
    } catch {
        # Fallback: regex, with dependency/order blocks stripped first (same as the .py)
        $cleaned = $text
        foreach ($block in 'modDependencies', 'loadAfter', 'loadBefore', 'incompatibleWith') {
            $cleaned = [regex]::Replace($cleaned, "<$block\b.*?</$block>", '', 'Singleline,IgnoreCase')
        }
        $m = [regex]::Match($cleaned, '<packageId>(.*?)</packageId>', 'Singleline,IgnoreCase')
        if ($m.Success) { return $m.Groups[1].Value.Trim().ToLower() }
        return $null
    }
}

# Scan all mod directories
$discovered = [ordered]@{}   # packageId -> mod folder name
foreach ($modDir in Get-ChildItem $MODS_DIR -Directory -Force | Sort-Object Name) {
    $about = Get-ChildItem $modDir.FullName -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ieq 'About' } |
        ForEach-Object { Get-ChildItem $_.FullName -File -Force | Where-Object { $_.Name -ieq 'About.xml' } } |
        Select-Object -First 1
    if (-not $about) { Write-Warning "no About.xml: $($modDir.Name)"; continue }
    $pkg = Read-PackageId $about.FullName
    if (-not $pkg) { Write-Warning "no packageId: $($modDir.Name)"; continue }
    if ($discovered.Contains($pkg)) {
        Write-Warning "duplicate packageId ${pkg}: $($modDir.Name) (already have $($discovered[$pkg]))"
        continue
    }
    $discovered[$pkg] = $modDir.Name
}
Write-Host "Found $($discovered.Count) packageIds in $MODS_DIR"

# HEAD first (DLCs always; others only if present), alpha middle, TAIL last
$ordered = [System.Collections.Generic.List[string]]::new()
$seen = [System.Collections.Generic.HashSet[string]]::new()
foreach ($pkg in $LOAD_ORDER_HEAD) {
    if ($pkg.StartsWith('ludeon.rimworld') -or $discovered.Contains($pkg)) {
        $ordered.Add($pkg) | Out-Null
        $seen.Add($pkg) | Out-Null
    }
}
foreach ($pkg in ($discovered.Keys | Where-Object { -not $seen.Contains($_) -and $LOAD_ORDER_TAIL -notcontains $_ } | Sort-Object)) {
    $ordered.Add($pkg) | Out-Null
    $seen.Add($pkg) | Out-Null
}
foreach ($pkg in $LOAD_ORDER_TAIL) {
    if ($discovered.Contains($pkg)) {
        $ordered.Add($pkg) | Out-Null
        $seen.Add($pkg) | Out-Null
    }
}

# Build XML
$lines = @(
    '<?xml version="1.0" encoding="utf-8"?>',
    '<ModsConfigData>',
    "  <version>$gameVersion</version>",
    '  <activeMods>'
)
$lines += $ordered | ForEach-Object { "    <li>$_</li>" }
$lines += '  </activeMods>'
$lines += '  <knownExpansions>'
$lines += $LOAD_ORDER_HEAD[0..5] | ForEach-Object { "    <li>$_</li>" }
$lines += '  </knownExpansions>'
$lines += '</ModsConfigData>'

New-Item -ItemType Directory -Force (Split-Path -Parent $CONFIG_OUT) | Out-Null
[System.IO.File]::WriteAllText($CONFIG_OUT, ($lines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host "Wrote $CONFIG_OUT"
Write-Host "  Active mods: $($ordered.Count)"
Write-Host "  Game version: $gameVersion"
Write-Host ''
Write-Host 'Load order:'
$i = 0
foreach ($pkg in $ordered) {
    $i++
    $src = if ($discovered.Contains($pkg)) { $discovered[$pkg] } elseif ($pkg.StartsWith('ludeon')) { '[Data/]' } else { '?' }
    Write-Host ('  {0,3}. {1,-55} {2}' -f $i, $pkg, $src)
}
