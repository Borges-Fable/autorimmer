#!/usr/bin/env pwsh
# Launch the _RimWorld-Agent bench profile on Windows (BORGES) with isolated
# saves/config. Windows equivalent of run-agent.sh; same refusals, adapted
# isolation:
#
#   XDG_CONFIG_HOME   -> -savedatafolder=<profile>\SaveData (config, saves, and
#                        the AutoRimmer/ protocol root all land there)
#   Player.log        -> -logfile <profile>\Player.log (Unity's default LocalLow
#                        location is shared machine-wide; redirecting it keeps
#                        bench runs from clobbering — or being clobbered by —
#                        anything else, and puts the isolation evidence in-profile)
#   window parking    -> none on Windows; the window opens normally (watchable by
#                        design). runInBackground=True in the seeded Prefs keeps
#                        the sim ticking unfocused; callers assert a TICK DELTA,
#                        not process-up, same as on Linux.
#   mangohud fps cap  -> none on Windows yet; acceptance runs are short, and the
#                        real throughput control is spec 1.3's max_tps governor.
#
# Refuses to launch: any install but _RimWorld-Agent; on battery (this laptop
# hard-kills under sustained load on battery); while another RimWorld runs.
#
# Flags (everything else is passed through to RimWorldWin64.exe):
#   -Quicktest      boot straight into a generated 250x250 test map
#   -NoModsConfig   skip the launch-time ModsConfig.xml regeneration
#   -Wait           block until the game exits (default: detach and print the PID)
param(
    [switch]$Quicktest,
    [switch]$NoModsConfig,
    [switch]$Wait,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Pass = @()
)
$ErrorActionPreference = 'Stop'

$GAME_DIR = Split-Path -Parent $PSCommandPath
if ((Split-Path -Leaf $GAME_DIR) -ne '_RimWorld-Agent') {
    Write-Error "refusing: run-agent.ps1 must live in the _RimWorld-Agent profile (got $GAME_DIR)`nthe agent bench is the only install this launcher may start"
    exit 1
}
$exe = Join-Path $GAME_DIR 'RimWorldWin64.exe'
if (-not (Test-Path $exe)) { Write-Error "no RimWorldWin64.exe in $GAME_DIR — run make-profile-agent.ps1 first"; exit 1 }

# --- machine rule: never launch on battery (hard-kills under load) -----------
$battery = (Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue).BatteryStatus
if ($null -ne $battery -and $battery -ne 2) {
    Write-Error "refusing: on battery (Win32_Battery.BatteryStatus=$battery, need 2 = on AC). Plug in first."
    exit 1
}

# --- one BENCH at a time. A RimWorld from anywhere else (the user's Steam
# session, the MP pack) is not ours: coexist, never touch it. Isolation holds —
# separate exe, save data, and log — so only note the resource sharing. --------
foreach ($running in Get-Process -Name 'RimWorldWin64' -ErrorAction SilentlyContinue) {
    if ($running.Path -eq $exe) {
        Write-Error "refusing: the bench is already running (pid $($running.Id)). One bench at a time."
        exit 1
    }
    Write-Warning "another RimWorld is running (pid $($running.Id), $($running.Path)) — not ours, leaving it alone; expect shared CPU/GPU"
}

# --- self-heal ModsConfig.xml (same rationale as the Linux launcher: RimWorld
# rewrites it whenever it prunes mods, and the damage persists) ---------------
if (-not $NoModsConfig -and (Test-Path (Join-Path $GAME_DIR 'gen-modsconfig.ps1'))) {
    try {
        & (Join-Path $GAME_DIR 'gen-modsconfig.ps1') *> $null
        $modCount = (Get-ChildItem (Join-Path $GAME_DIR 'Mods') -Directory -Force).Count
        Write-Host "modsconfig: regenerated from Mods\ ($modCount dirs)"
    } catch {
        Write-Warning "gen-modsconfig.ps1 failed; launching with existing ModsConfig.xml ($_)"
    }
}

# --- isolation: no Steam attach ----------------------------------------------
foreach ($v in 'SteamAppId', 'SteamGameId', 'SteamOverlayGameId') {
    Remove-Item "Env:$v" -ErrorAction SilentlyContinue
}

$saveData = Join-Path $GAME_DIR 'SaveData'
$playerLog = Join-Path $GAME_DIR 'Player.log'
New-Item -ItemType Directory -Force $saveData | Out-Null

# --quicktest and autostart.rws cannot both exist. Root_Entry and Root_Play race
# on Root.checkedAutostartSaveFile with a scene-targeted long event: the
# autostart load wins, the quicktest lambda then finds Current.Game != null and
# skips, and map generation fails. DETERMINISTIC, not flaky — it cost the M1 run
# two launches before anyone knew why. playbook/quicktest-and-autostart-collide.md,
# git-bug c8c0199. Refusing rather than warning: the launch cannot succeed.
$savesDir = Join-Path $saveData 'Saves'
$autostart = Join-Path $savesDir 'autostart.rws'
if ($Quicktest -and (Test-Path $autostart)) {
    Write-Error @"
refusing: --quicktest cannot run while Saves\autostart.rws exists.
  Map generation WILL fail (Root.checkedAutostartSaveFile race).
  Park it, then relaunch:
    New-Item -ItemType Directory -Force '$savesDir\pre-m1' | Out-Null
    Move-Item '$autostart' '$savesDir\pre-m1\'
  Standing decision: autostart.rws stays parked while --quicktest is the bench
  fixture. See playbook/quicktest-and-autostart-collide.md.
"@
    exit 1
}

$gameArgs = @("-savedatafolder=$saveData", '-logfile', $playerLog)
if ($Quicktest) { $gameArgs += '-quicktest' }
$gameArgs += $Pass

Write-Host "launching: RimWorldWin64.exe $($gameArgs -join ' ')"
$proc = Start-Process -FilePath $exe -ArgumentList $gameArgs -WorkingDirectory $GAME_DIR -PassThru
Write-Host "pid $($proc.Id); log $playerLog; protocol root $saveData\AutoRimmer"
if ($Wait) { $proc.WaitForExit(); exit $proc.ExitCode }
