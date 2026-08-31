#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Acceptance runner for spec 3.4 — Pawn orders + policies (git-bug 39c9db7).

.DESCRIPTION
    The worker that wrote 3.4 may never launch RimWorld, so this is the
    executable form of its acceptance: a numbered sequence of exact protocol
    envelopes with an exact expected result for each, so a mismatch is visible
    without judgement. `accept/3.4-pawn-orders.md` is the same sequence in
    prose, with the envelopes written out, for running by hand or from any
    other driver.

    IT SPEAKS THE RAW FILE PROTOCOL, not `rwa`, and that is deliberate: BORGES
    has no python (Store stub only — RUNLOG session 5 already records it), so
    `rwa` cannot run on this box, and an unrunnable acceptance script is worse
    than none. PowerShell 7 is what is actually here. On a box that HAS python,
    `rwa <op> --args-json '<json>'` sends the identical envelopes.

    Start the bench first (`_RimWorld-Agent\run-agent.ps1`), load or generate a
    colony with at least two colonists and a stockpile, and leave it paused.

.PARAMETER Root
    The protocol root. Defaults to the BORGES bench's -savedatafolder location.

.PARAMETER Phase
    Run only these phases (repeatable). Phase 0 always runs first.

.EXAMPLE
    pwsh accept\3.4-pawn-orders.ps1
    pwsh accept\3.4-pawn-orders.ps1 -Phase 3
    pwsh accept\3.4-pawn-orders.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [string]$Root = "$env:USERPROFILE\misc\rimworld\_RimWorld-Agent\SaveData\AutoRimmer",
    [int[]]$Phase = @(1, 2, 3, 4, 5),
    [switch]$DryRun,
    [switch]$Echo
)

$ErrorActionPreference = 'Stop'
$script:Fails = @()
$script:Checks = 0
$script:S = @{}          # cross-step state (pawn ids, thing ids, watermarks)
$script:Seq = 0

# ------------------------------------------------------------------ protocol --
# commands/<id>.json in, results/<id>.json out. The poller runs a 500 ms cycle
# and ignores inbox files younger than its MinFileAgeMs, so a round trip has a
# floor of roughly 0.25-1 s even for a trivial verb; that is the protocol, not
# this script being slow. `advance` is a DEFERRED result — its file appears only
# when the advance finishes — hence the generous per-call timeout.
#
# Ids are kept to [A-Za-z0-9-] so Poller.Sanitize leaves them alone and the
# result filename is exactly <id>.json.

function Send-Cmd {
    param(
        [Parameter(Mandatory)][string]$Op,
        [hashtable]$Args = @{},
        [int]$TimeoutSec = 240
    )
    $script:Seq++
    $slug = ($Op -replace '[^A-Za-z0-9]', '')
    if ($slug.Length -gt 16) { $slug = $slug.Substring(0, 16) }
    $id = "acc34-{0:d3}-{1}" -f $script:Seq, $slug
    $envelope = @{ id = $id; op = $Op; args = $Args } | ConvertTo-Json -Depth 20 -Compress

    if ($DryRun) {
        Write-Host "    would send: $envelope"
        return @{ ok = $true; op = $Op; data = @{}; _dry = $true }
    }

    $inbox = Join-Path $Root "commands\$id.json"
    $result = Join-Path $Root "results\$id.json"
    if (Test-Path $result) { Remove-Item $result -Force }
    # Write whole-file: the poller's min-age gate tolerates a partial write, but
    # not writing one at all is cheaper than relying on it.
    [System.IO.File]::WriteAllText($inbox, $envelope, [System.Text.UTF8Encoding]::new($false))

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $result) {
            Start-Sleep -Milliseconds 60      # let the write land
            try {
                $env = Get-Content $result -Raw | ConvertFrom-Json -AsHashtable
                if ($Echo) { Write-Host "    <- $($env | ConvertTo-Json -Depth 8 -Compress)" }
                return $env
            }
            catch { Start-Sleep -Milliseconds 120 }
        }
        Start-Sleep -Milliseconds 200
    }
    return @{ ok = $false; op = $Op
        error = @{ code = 'acc-timeout'
            detail = "no results\$id.json within ${TimeoutSec}s — is the bench running and unpaused-capable?" } }
}

function Dig {
    param($Obj, [string]$Path, $Default = $null)
    $cur = $Obj
    foreach ($part in $Path.Split('.')) {
        if ($null -eq $cur) { return $Default }
        if ($cur -is [System.Collections.IList]) {
            $i = 0
            if (-not [int]::TryParse($part, [ref]$i)) { return $Default }
            if ($i -ge $cur.Count) { return $Default }
            $cur = $cur[$i]
            continue
        }
        if ($cur -is [System.Collections.IDictionary]) {
            if (-not $cur.Contains($part)) { return $Default }
            $cur = $cur[$part]
            continue
        }
        return $Default
    }
    if ($null -eq $cur) { return $Default }
    return $cur
}

function Show { param($v) if ($null -eq $v) { 'null' } else { ($v | ConvertTo-Json -Depth 6 -Compress) } }

# ------------------------------------------------------------------- asserts --

function Check {
    param([string]$Num, [string]$What, [bool]$Ok, [string]$Expected, $Actual)
    $script:Checks++
    # Writes only — never returns to the pipeline, so a caller does not have to
    # remember Out-Null and a dry run does not print stray booleans.
    if ($DryRun) { Write-Host ("  {0,-7} EXPECT  {1}: {2}" -f $Num, $What, $Expected); return }
    if ($Ok) { Write-Host ("  {0,-7} PASS    {1}" -f $Num, $What) -ForegroundColor Green; return }
    Write-Host ("  {0,-7} FAIL    {1}" -f $Num, $What) -ForegroundColor Red
    Write-Host ("          expected: {0}" -f $Expected)
    Write-Host ("          actual:   {0}" -f (Show $Actual))
    $script:Fails += $Num
}

function Eq {
    param([string]$Num, [string]$What, $Env, [string]$Path, $Want)
    $got = Dig $Env $Path
    Check $Num "$What ($Path)" ([bool](($null -eq $Want -and $null -eq $got) -or ($got -eq $Want))) (Show $Want) $got
}

function Ge {
    param([string]$Num, [string]$What, $Env, [string]$Path, [double]$Want)
    $got = Dig $Env $Path
    $ok = ($got -is [int] -or $got -is [long] -or $got -is [double] -or $got -is [decimal]) -and ([double]$got -ge $Want)
    Check $Num "$What ($Path)" $ok ">= $Want" $got
}

function Has {
    param([string]$Num, [string]$What, $Haystack, $Needle)
    $list = @($Haystack)
    Check $Num $What ([bool]($list -contains $Needle)) "contains '$Needle'" $Haystack
}

function Note { param([string]$Num, [string]$Text) Write-Host ("  {0,-7} NOTE    {1}" -f $Num, $Text) -ForegroundColor DarkYellow }

function Precondition {
    param([string]$Num, [string]$What, [bool]$Ok, [string]$Detail)
    if ($DryRun) { Write-Host ("  {0,-7} NEEDS   {1}" -f $Num, $What) -ForegroundColor DarkCyan; return }
    if ($Ok) { Write-Host ("  {0,-7} OK      precondition: {1}" -f $Num, $What) -ForegroundColor DarkGreen; return }
    Write-Host ("  {0,-7} SKIP    precondition NOT MET: {1}" -f $Num, $What) -ForegroundColor Yellow
    Write-Host "          $Detail"
    Write-Host "          This is a FIXTURE gap, not a 3.4 failure. Stage it and re-run."
    exit 2
}

function PhaseBanner { param([string]$T) Write-Host ''; Write-Host ('=' * 78); Write-Host $T; Write-Host ('=' * 78) }

# ------------------------------------------------------------------- phase 0 --

$NewOps = @(
    'draft', 'undraft', 'move-to', 'attack', 'orders', 'prioritize',
    'clear-priority-work', 'rescue', 'capture', 'arrest', 'carry',
    'equip', 'wear', 'drop', 'consume',
    'extinguish', 'beat-fire', 'tend', 'repair', 'man-turret',
    'rest-until-healed', 'fire-at-will',
    'work-priorities', 'schedule', 'assign',
    'policy-new', 'policy-edit', 'policy-delete', 'policy-default',
    'warden', 'surgery-options', 'surgery-add', 'surgery-remove',
    'research-set', 'research-stop'
)

function Phase0 {
    PhaseBanner "PHASE 0 - preflight: the bench is live and 3.4's 35 verbs registered"

    # 0.1  {"op":"status"}
    $e = Send-Cmd status
    Eq '0.1a' 'status answered' $e 'ok' $true
    Eq '0.1b' 'a game is loaded' $e 'data.gameLoaded' $true
    Eq '0.1c' 'the game is paused (the agent owns time)' $e 'data.paused' $true
    Check '0.1d' 'no force-pausing modal is up (spec 1.7 would wedge every advance)' `
    ($null -eq (Dig $e 'data.forcePause')) 'absent' (Dig $e 'data.forcePause')

    # 0.2  every op this spec registers must be present
    $verbs = @(Dig $e 'data.verbs')
    $missing = @($NewOps | Where-Object { $verbs -notcontains $_ })
    Check '0.2' "all 35 of 3.4's ops registered" ($missing.Count -eq 0) 'no missing ops' $missing

    # 0.3  {"op":"pawns","args":{"filter":"colonist"}}
    $e = Send-Cmd pawns @{ filter = 'colonist' }
    Eq '0.3a' 'pawns answered' $e 'ok' $true
    $roster = @(Dig $e 'data.list')
    Precondition '0.3b' 'at least two visible colonists' ($roster.Count -ge 2) `
    "the roster has $($roster.Count); 3.4's acceptance needs an actor and a patient. Stage with dev:starter-kit or load a bigger save."
    if ($DryRun) { $S.A = 1001; $S.Aname = '<A>'; $S.B = 1002; $S.Bname = '<B>'; $S.seq0 = 0 }
    else { $S.A = $roster[0].id; $S.Aname = $roster[0].name; $S.B = $roster[1].id; $S.Bname = $roster[1].name }
    Write-Host "          actor  A = $($S.A) ($($S.Aname))"
    Write-Host "          target B = $($S.B) ($($S.Bname))"

    # 0.4  journal watermark, so every later assertion can name its own window
    $e = Send-Cmd journal @{ limit = 1 }
    $S.seq0 = [int](Dig $e 'data.last_seq' 0)
    Eq '0.4' 'journal readable (watermark recorded)' $e 'ok' $true
    Write-Host "          journal watermark seq0 = $($S.seq0)"

    # 0.5  MANUAL WORK PRIORITIES - the fixture precondition 4.7 and 5.1 need,
    #      and the one this driver could not satisfy until git-bug e8f2c32.
    #      PlaySettings.useWorkPriorities scribes defaultValue:false, so on any
    #      colony the agent staged itself the Work tab is a checkbox column,
    #      work-priorities correctly refuses priorities 1/2/4, and eight checks
    #      (4.7a-e, 5.1a-c) were unreachable. The checkbox now belongs to
    #      work-priorities itself, because MainTabWindow_Work draws it in the
    #      same window as the matrix. Staged, not asserted: the verb's own
    #      acceptance is accept\manual-work-priorities.md.
    $e = Send-Cmd work-priorities @{ manual = $true }
    Precondition '0.5' 'manual work priorities are ON (4.7 and 5.1 use priorities 1 and 2)' `
    ((Dig $e 'data.manual.after') -eq $true) `
        "work-priorities {manual:true} did not take: $(Show $e). Without it 4.7 and 5.1 are refused by design, not by defect."
    Write-Host "          manual priorities: $(Dig $e 'data.manual.before') -> $(Dig $e 'data.manual.after') ($(Dig $e 'data.manual.pawns_notified') pawn work rows notified)"
}

function NoRedErrors { param([string]$Num, [string]$What)
    $e = Send-Cmd journal @{ since_seq = $S.seq0; types = @('red_error'); limit = 50 }
    Eq $Num $What $e 'data.count' 0
}

# ------------------------------------------------------------------- phase 1 --

function Phase1 {
    PhaseBanner 'PHASE 1 - ACCEPTANCE BULLET 1: draft + move + undraft round-trip, and the drafted pawn HOLDS position'
    $A = $S.A

    # 1.1 a standable, reachable destination. find-rect's `buildable` requirement
    #     implies standable and unblocked, so this cannot pick a wall; move-to's
    #     own CellFinder.StandableCellNear would forgive a near miss but the
    #     arrival assertion below would not.
    $e = Send-Cmd find-rect @{ w = 1; h = 1; near = "pawn:$A"; max = 5
        require = @('buildable', "reachable-from:pawn:$A")
    }
    $cands = @(Dig $e 'data.candidates')
    Precondition '1.1' 'a buildable, reachable 1x1 cell near the actor' ($cands.Count -gt 0) `
        'find-rect found none within 80 rings; the actor may be sealed in.'
    $dest = $cands[$cands.Count - 1].at
    $S.dest = $dest
    Write-Host "          destination = $(Show $dest)"

    # 1.2 draft
    $e = Send-Cmd draft @{ pawns = @($A) }
    Eq '1.2a' 'draft accepted exactly one pawn' $e 'data.counts.accepted' 1
    Eq '1.2b' 'no rejections' $e 'data.counts.rejected' 0
    Eq '1.2c' 'the pawn is drafted' $e 'data.accepted.0.drafted' $true
    Eq '1.2d' 'it was not already drafted' $e 'data.accepted.0.drafted_before' $false
    Eq '1.2e' "the setter's priorityWork clear is disclosed" $e 'data.accepted.0.priority_work_cleared' $true
    Ge '1.2f' 'an `action` journal row was written' $e 'data.action.journal_seq' 1
    $act = Dig $e 'data.action'
    Check '1.2g' 'the action row is NOT stamped as a cheat (it is a player verb)' `
    (-not ($act -is [System.Collections.IDictionary] -and $act.Contains('cheat'))) 'no `cheat` key' $act

    # 1.3 the observer agrees with the actor - one vocabulary, not two
    $e = Send-Cmd pawn @{ id = $A; sections = @('state') }
    Eq '1.3' '`pawn` reports drafted:true' $e 'data.state.drafted' $true

    # 1.4 move
    $e = Send-Cmd move-to @{ pawns = @($A); to = $dest }
    Eq '1.4a' 'move-to accepted the pawn' $e 'data.counts.accepted' 1
    Eq '1.4b' 'the ordered job is Goto' $e 'data.accepted.0.job_def' 'Goto'
    Ge '1.4c' 'an `action` row was written' $e 'data.action.journal_seq' 1
    $S.standable = Dig $e 'data.standable_near'

    # 1.5 walk there
    $e = Send-Cmd advance @{ ticks = 2000; max_tps = 600 }
    Eq '1.5a' 'advance ran to its tick budget (no dialog, no red error)' $e 'data.reason' 'ticks'

    # 1.6 arrived, and STILL DRAFTED
    $e = Send-Cmd pawn @{ id = $A; sections = @('state') }
    $at = @(Dig $e 'data.state.at')
    $near = @($S.standable); if ($near.Count -ne 2) { $near = @($dest) }
    $dist = if ($at.Count -eq 2) { [Math]::Max([Math]::Abs($at[0] - $near[0]), [Math]::Abs($at[1] - $near[1])) } else { 99 }
    Check '1.6a' 'the pawn arrived within 3 cells of the standable destination' ($dist -le 3) `
        "|at - $(Show $near)| <= 3" "at=$(Show $at) dist=$dist"
    Eq '1.6b' 'still drafted' $e 'data.state.drafted' $true
    $S.heldAt = $at

    # 1.7 IT HOLDS. A drafted pawn does not wander off to eat, haul or sleep -
    #     which is the half of the round trip that makes undraft necessary.
    $e = Send-Cmd advance @{ ticks = 4000; max_tps = 600 }
    Eq '1.7a' 'advance ran to its tick budget' $e 'data.reason' 'ticks'
    $e = Send-Cmd pawn @{ id = $A; sections = @('state') }
    $at2 = @(Dig $e 'data.state.at')
    Check '1.7b' 'the drafted pawn HELD position across 4000 further ticks' `
    (($at2.Count -eq 2) -and ($at2[0] -eq $S.heldAt[0]) -and ($at2[1] -eq $S.heldAt[1])) `
        "at == $(Show $S.heldAt)" $at2
    Eq '1.7c' 'still drafted after holding' $e 'data.state.drafted' $true
    $jd = Dig $e 'data.state.job_def'
    Check '1.7d' 'and it is idle-in-combat-stance, not working' `
    (($null -eq $jd) -or ($jd -in @('Wait_Combat', 'Wait', 'AttackStatic', 'AttackMelee', 'Goto'))) `
        'null | Wait_Combat | Wait | Attack* | Goto' $jd

    # 1.8 undraft - ITS OWN VERB, because the game's own tutorial has a dedicated
    #     UndraftAll instruction and warns that colonists left drafted starve.
    $e = Send-Cmd undraft @{ pawns = @($A) }
    Eq '1.8a' 'undraft accepted the pawn' $e 'data.counts.accepted' 1
    Eq '1.8b' 'the pawn is undrafted' $e 'data.accepted.0.drafted' $false
    Ge '1.8c' 'an `action` row was written' $e 'data.action.journal_seq' 1

    # 1.9 the whole-roster form is ONE call - the loop-closing shape
    $e = Send-Cmd undraft @{ pawns = 'colonists' }
    Eq '1.9a' '`undraft {pawns:"colonists"}` is a legal whole-roster call' $e 'ok' $true
    $gates = @(@(Dig $e 'data.rejected') | ForEach-Object { $_.gate })
    Check '1.9b' 'and it rejects the already-undrafted with a reason, not an error' `
    (($gates.Count -eq 0) -or (@($gates | Where-Object { $_ -ne 'already' }).Count -eq 0)) `
        "every rejection gate == 'already'" $gates

    # 1.10 the journal carries the whole round trip as `action` rows
    $e = Send-Cmd journal @{ since_seq = $S.seq0; types = @('action'); limit = 200 }
    $verbs = @(@(Dig $e 'data.events') | ForEach-Object { $_.payload.verb })
    Has '1.10a' 'journal has an `action` row for draft' $verbs 'draft'
    Has '1.10b' 'journal has an `action` row for move-to' $verbs 'move-to'
    Has '1.10c' 'journal has an `action` row for undraft' $verbs 'undraft'

    NoRedErrors '1.11' 'ZERO red errors across phase 1'
}

# ------------------------------------------------------------------- phase 2 --

function Phase2 {
    PhaseBanner 'PHASE 2 - ACCEPTANCE BULLET 2: `prioritize haul` on a specific stack makes that pawn haul it NEXT'
    $A = $S.A

    # 2.1 hauling needs somewhere to haul TO.
    $e = Send-Cmd zones @{ kind = 'stockpile' }
    $total = [int](Dig $e 'data.stockpiles.total' 0)
    Precondition '2.1' 'the colony has at least one stockpile zone' ($total -gt 0) `
        "WorkGiver_HaulGeneral produces no job with no storage destination, so ``prioritize haul`` would correctly report 'no empty place'. Stage one with 3.2's zone verbs or load a save that has one."

    # 2.2 a specific stack, ON THE GROUND, so hauling it is a real job
    $e = Send-Cmd 'dev:spawn-thing' @{ def = 'Steel'; count = 75; pos = "pawn:$A"; stockpile = $false }
    Eq '2.2a' 'dev:spawn-thing placed the steel' $e 'ok' $true
    Ge '2.2b' 'at least one stack landed' $e 'data.placed' 1
    $steel = Dig $e 'data.spawned.0.id'
    Check '2.2c' 'a thing id came back for the stack' ($null -ne $steel) 'an id' $steel
    $S.steel = $steel
    Write-Host "          steel stack = $steel"

    # 2.3 THE PARITY LIST. WorkGiverDef.directOrderable defaults true, so this is
    #     as wide as the bench's WorkGiver set; HaulGeneral must be in it.
    $e = Send-Cmd orders @{ pawn = $A; thing = $steel }
    Eq '2.3a' 'orders answered' $e 'ok' $true
    $avail = @(@(Dig $e 'data.available') | ForEach-Object { $_.work })
    Has '2.3b' 'HaulGeneral is offered on the steel stack' $avail 'HaulGeneral'
    $blocked = @(Dig $e 'data.blocked') | Where-Object { $_.work -eq 'HaulGeneral' }
    if ($blocked) { Note '2.3c' "HaulGeneral also appears blocked: $($blocked[0].reason)" }

    # 2.4 the order itself
    $e = Send-Cmd prioritize @{ pawn = $A; work = 'HaulGeneral'; thing = $steel }
    Eq '2.4a' 'prioritize succeeded' $e 'data.ok' $true
    Eq '2.4b' 'the work giver is the one asked for' $e 'data.work' 'HaulGeneral'
    $jd = [string](Dig $e 'data.job_def')
    Check '2.4c' 'the ordered job is a haul' ($jd.StartsWith('HaulTo')) 'HaulToCell | HaulToContainer' $jd
    Ge '2.4d' 'an `action` row was written' $e 'data.action.journal_seq' 1
    # ECHO THE DURABLE STATE, NOT A HOPE: HaulGeneral does not set
    # prioritizeSustains in Core, so nothing durable is written and the result
    # must SAY so rather than claim a standing order.
    $sust = Dig $e 'data.sustains'
    Check '2.4e' '`sustains` reports whether mindState.priorityWork was written' ($sust -is [bool]) 'a bool' $sust
    if ($sust -eq $true) {
        Eq '2.4f' 'priorityWork is live (sustains was true)' $e 'data.priority_work.active' $true
        Eq '2.4g' 'and names this work giver' $e 'data.priority_work.work_giver' 'HaulGeneral'
    }
    else {
        Note '2.4f' 'sustains:false - this work giver writes no durable priorityWork, and the result says so rather than claiming a standing order'
    }

    # 2.5 "that pawn hauls it NEXT" - the bullet, literally. TryTakeOrderedJob
    #     ends the current job and enqueues first, so the haul is the job the
    #     pawn takes on the next tick.
    $e = Send-Cmd advance @{ ticks = 10; max_tps = 60 }
    Eq '2.5a' 'advance ran' $e 'data.reason' 'ticks'
    $e = Send-Cmd pawn @{ id = $A; sections = @('state') }
    $jd = [string](Dig $e 'data.state.job_def')
    Check '2.5b' "the pawn's CURRENT job is the haul" ($jd.StartsWith('HaulTo')) 'HaulToCell | HaulToContainer' $jd
    Write-Host "          job report: $(Dig $e 'data.state.job')"

    # 2.6 and it finishes
    $e = Send-Cmd advance @{ ticks = 4000; max_tps = 600 }
    Eq '2.6a' 'advance ran' $e 'data.reason' 'ticks'
    $e = Send-Cmd pawn @{ id = $A; sections = @('state') }
    $jd = [string](Dig $e 'data.state.job_def')
    Check '2.6b' 'the haul is no longer the current job (it completed)' (-not $jd.StartsWith('HaulTo')) 'not HaulTo*' $jd

    # 2.7 clear-priority-work answers either way
    $e = Send-Cmd clear-priority-work @{ pawns = @($A) }
    Eq '2.7' 'clear-priority-work answered (accepted, or refused with a reason)' $e 'ok' $true

    NoRedErrors '2.8' 'ZERO red errors through phase 2'
}

# ------------------------------------------------------------------- phase 3 --

function Phase3 {
    PhaseBanner "PHASE 3 - ACCEPTANCE BULLET 3: a 'cold' apparel policy assigned, and the pawn RE-DRESSES under advance"
    $B = $S.B

    # 3.1 warm apparel, IN STORAGE. JobGiver_OptimizeApparel skips any candidate
    #     failing IsInAnyStorage(), so `stockpile:true` is not a nicety.
    $e = Send-Cmd 'dev:spawn-thing' @{ def = 'Apparel_Parka'; stuff = 'Cloth'; count = 1; stockpile = $true; quality = 'Normal' }
    Eq '3.1a' 'a parka was spawned' $e 'ok' $true
    Ge '3.1b' 'the parka landed' $e 'data.placed' 1
    Note '3.1c' "dev:spawn-thing storage note: $(Dig $e 'data.stockpile') at $(Show (Dig $e 'data.at')) (not a check - the real proof is 3.6a, since JobGiver_OptimizeApparel skips anything not in storage)"
    $e = Send-Cmd 'dev:spawn-thing' @{ def = 'Apparel_Tuque'; stuff = 'Cloth'; count = 1; stockpile = $true; quality = 'Normal' }
    Ge '3.1d' 'a tuque landed too' $e 'data.placed' 1

    # 3.2 the BEFORE picture, in 2.2's apparel vocabulary
    $e = Send-Cmd pawn @{ id = $B; sections = @('apparel') }
    $beforeWorn = @(@(Dig $e 'data.apparel.worn') | ForEach-Object { $_.def })
    $beforeCold = [double](Dig $e 'data.apparel.insulation_cold_total' 0)
    Write-Host "          before: worn=$(Show $beforeWorn) insulation_cold_total=$beforeCold"
    Check '3.2' 'the pawn is not already wearing the parka' ($beforeWorn -notcontains 'Apparel_Parka') 'no Apparel_Parka worn' $beforeWorn

    # 3.3 the policy. THE MECHANISM IS THE FILTER, NOT THE WEATHER - see the .md:
    #     JobGiver_OptimizeApparel's `neededWarmth` comes from
    #     GenTemperature.AverageTemperatureAtTileForTwelfth, i.e. the tile's
    #     SEASONAL average, so dev:weather cannot move it. A policy that
    #     disallows what they wear and allows only warm apparel drives the loop
    #     deterministically through RemoveApparel + ApparelScoreGain.
    $e = Send-Cmd policy-new @{ kind = 'apparel'; label = 'cold'; disallow_all = $true
        allow = @('Apparel_Parka', 'Apparel_Tuque')
    }
    Eq '3.3a' 'policy-new succeeded' $e 'ok' $true
    Eq '3.3b' 'the label stuck' $e 'data.label' 'cold'
    Ge '3.3c' 'an `action` row was written' $e 'data.action.journal_seq' 1
    $pol = Dig $e 'data.id'
    Check '3.3d' 'a policy id came back' ($null -ne $pol) 'an id' $pol
    Has '3.3e' 'the parka was allowed' (@(Dig $e 'data.edits')) 'allow:Apparel_Parka'
    Check '3.3f' 'nothing was refused by the apparel global filter' (@(Dig $e 'data.refused').Count -eq 0) 'no refusals' (Dig $e 'data.refused')
    $S.policy = $pol

    # 3.4 assign it - plural verb, one pawn is the degenerate case
    $e = Send-Cmd assign @{ pawns = @($B); apparel_policy = $pol }
    Eq '3.4a' 'assign accepted the pawn' $e 'data.counts.accepted' 1
    Has '3.4b' 'the apparel lever applied' (@(Dig $e 'data.accepted.0.applied')) 'apparel_policy'
    Eq '3.4c' 'the AFTER read shows the new policy' $e 'data.accepted.0.after.apparel' 'cold'
    Eq '3.4d' "and it was read through PawnSafe's guarded backing-field route" $e 'data.accepted.0.after.source' 'backing-field'
    Ge '3.4e' 'an `action` row was written' $e 'data.action.journal_seq' 1

    # 3.5 the clothes loop. Notify_OutfitChanged sets nextApparelOptimizeTick to
    #     NOW, so the pawn re-optimizes at its next free think tick rather than
    #     in 6000-9000. It still has to BE free: asleep, eating or in a mental
    #     state all delay it, hence the documented fallback window.
    $e = Send-Cmd advance @{ ticks = 5000; max_tps = 600 }
    Eq '3.5a' 'advance ran' $e 'data.reason' 'ticks'
    $e = Send-Cmd pawn @{ id = $B; sections = @('apparel') }
    $worn = @(@(Dig $e 'data.apparel.worn') | ForEach-Object { $_.def })
    $window = 5000
    if ($worn -notcontains 'Apparel_Parka') {
        Note '3.5b' 'not re-dressed within 5000 ticks; advancing the documented fallback window (the pawn may have been asleep or eating)'
        $e = Send-Cmd advance @{ ticks = 15000; max_tps = 600 }
        Eq '3.5c' 'fallback advance ran' $e 'data.reason' 'ticks'
        $e = Send-Cmd pawn @{ id = $B; sections = @('apparel') }
        $worn = @(@(Dig $e 'data.apparel.worn') | ForEach-Object { $_.def })
        $window = 20000
    }

    # 3.6 THE BULLET
    Has '3.6a' "the pawn is WEARING the parka (within $window ticks)" $worn 'Apparel_Parka'
    $afterCold = [double](Dig $e 'data.apparel.insulation_cold_total' 0)
    Check '3.6b' 'cold insulation went UP' ($afterCold -gt $beforeCold) "> $beforeCold" $afterCold
    $dropped = @($beforeWorn | Where-Object { $worn -notcontains $_ })
    Check '3.6c' "the disallowed apparel was taken off (JobGiver_OptimizeApparel's RemoveApparel pass)" `
    (($dropped.Count -gt 0) -or ($beforeWorn.Count -eq 0)) `
        'at least one previously-worn item removed, or it started naked' "before=$(Show $beforeWorn) after=$(Show $worn)"
    Write-Host "          after: worn=$(Show $worn) insulation_cold_total=$afterCold"

    # 3.7 the policy database round-trips through 2.4's observer
    $e = Send-Cmd policies
    $labels = @(@(Dig $e 'data.outfits.list') | ForEach-Object { $_.label })
    Has '3.7a' "2.4's `policies` observer sees the new policy" $labels 'cold'
    $mine = @(Dig $e 'data.assignments') | Where-Object { $_.id -eq $B }
    Check '3.7b' 'and sees the assignment' ($mine -and $mine[0].apparel -eq 'cold') "'cold'" ($mine | ForEach-Object { $_.apparel })

    # 3.8 delete refuses while a live pawn uses it, IN THE GAME'S OWN WORDS
    $e = Send-Cmd policy-delete @{ kind = 'apparel'; policy = $pol }
    Eq '3.8a' 'policy-delete refused (a live pawn is using it)' $e 'data.ok' $false
    Check '3.8b' "and the refusal is the game's own AcceptanceReport string" `
    (-not [string]::IsNullOrEmpty([string](Dig $e 'data.reason'))) 'a non-empty reason' (Dig $e 'data.reason')

    NoRedErrors '3.9' 'ZERO red errors through phase 3'
}

# ------------------------------------------------------------------- phase 4 --

function Phase4 {
    PhaseBanner 'PHASE 4 - ACCEPTANCE BULLET 4: a surgery bill added, and a doctor PERFORMS it under advance'
    $A = $S.A; $B = $S.B

    # 4.1 a medical bed. HospitalBed's def carries bed_defaultMedical:true, so a
    #     spawned one is Medical without a toggle verb (toggling Medical on an
    #     ordinary bed is not 3.4's surface).
    $e = Send-Cmd 'dev:spawn-thing' @{ def = 'HospitalBed'; count = 1; pos = "pawn:$B"; faction = 'player' }
    Eq '4.1a' 'a hospital bed was spawned' $e 'ok' $true
    Ge '4.1b' 'it landed' $e 'data.placed' 1
    $bed = Dig $e 'data.spawned.0.id'
    $S.bed = $bed
    Write-Host "          hospital bed = $bed"

    # 4.2 medicine, in storage, so the recipe's ingredient exists
    $e = Send-Cmd 'dev:spawn-thing' @{ def = 'MedicineHerbal'; count = 10; stockpile = $true }
    Ge '4.2' 'medicine landed in storage' $e 'data.placed' 1

    # 4.3 a wounded-but-STANDING patient: HealthAIUtility.ShouldSeekMedicalRest
    #     must be true for rest-until-healed to be offered, and dev:damage's
    #     `amount` mode stops at downed by construction.
    $e = Send-Cmd 'dev:damage' @{ pawn = $B; mode = 'amount'; amount = 7; hits = 2; def = 'Cut' }
    Eq '4.3a' 'dev:damage landed' $e 'ok' $true
    $e = Send-Cmd pawn @{ id = $B; sections = @('state', 'health') }
    Eq '4.3b' 'the patient is still standing' $e 'data.state.downed' $false
    Ge '4.3c' 'and is wounded' $e 'data.health.hediffs_total' 1

    # 4.4 THE PARITY LIST for surgery. Anesthetize is Core, workAmount 0, one
    #     Medicine, no body part - the cheapest deterministic surgery there is.
    $e = Send-Cmd surgery-options @{ pawn = $B; cap = 200 }
    Eq '4.4a' 'surgery-options answered' $e 'ok' $true
    Eq '4.4b' 'research state was read through the guarded route, not GetProgress' $e 'data.source' 'backing-field'
    $opts = @(Dig $e 'data.options')
    $anes = @($opts | Where-Object { $_.recipe -eq 'Anesthetize' })
    Precondition '4.4c' 'Anesthetize is offered for this pawn' ($anes.Count -eq 1) `
        "Core's Anesthetize should be available on any flesh humanlike. Offered: $(($opts | ForEach-Object { $_.recipe } | Sort-Object -Unique) -join ', ')"
    Check '4.4d' 'and it is ADDABLE (no reason, no missing ingredient)' ($anes[0].addable -eq $true) `
        'addable:true' "addable=$($anes[0].addable) reason=$($anes[0].reason) missing=$(Show $anes[0].missing_ingredients)"

    # 4.5 add the bill. BillStack.AddBill checks NOTHING; every check that
    #     happened is the widget gate this verb reproduces.
    $e = Send-Cmd surgery-add @{ pawn = $B; recipe = 'Anesthetize' }
    Eq '4.5a' 'surgery-add succeeded' $e 'data.ok' $true
    Ge '4.5b' 'an `action` row was written' $e 'data.action.journal_seq' 1
    $recipes = @(@(Dig $e 'data.bills') | ForEach-Object { $_.recipe })
    Has '4.5c' "the bill is on the pawn's stack" $recipes 'Anesthetize'
    Check '4.5d' 'the four CreateSurgeryBill warnings are RETURNED, not messaged (one of them is a force-pausing Dialog_MessageBox)' `
    ($null -ne (Dig $e 'data.warnings')) 'a warnings list' (Dig $e 'data.warnings')
    $w = @(Dig $e 'data.warnings'); if ($w.Count) { Write-Host "          warnings: $(($w | ForEach-Object { $_.key }) -join ', ')" }

    # 4.6 2.4's observer sees the same bill in the same vocabulary
    $e = Send-Cmd bills @{ bench = $B }
    $benches = @(Dig $e 'data.benches')
    Check '4.6a' "2.4's `bills` observer reports the pawn as a bill giver" `
    (@($benches | Where-Object { $_.kind -eq 'pawn' }).Count -gt 0) "a bench entry with kind:'pawn'" (@($benches | ForEach-Object { $_.kind }))
    $recipes = @($benches | ForEach-Object { @($_.bills) | ForEach-Object { $_.recipe } })
    Has '4.6b' 'and lists the Anesthetize bill' $recipes 'Anesthetize'

    # 4.7 a doctor. work-priorities is a MATRIX; one cell is its degenerate case.
    $e = Send-Cmd work-priorities @{ set = @(@{ pawns = @($A); work = 'Doctor'; priority = 1 }) }
    Eq '4.7a' 'work-priorities answered' $e 'ok' $true
    Eq '4.7b' 'exactly one matrix cell changed' $e 'data.cells' 1
    Eq '4.7c' 'and the unit is named so `accepted` is not read as pawns' $e 'data.counts.unit' 'matrix cells'
    Eq '4.7d' 'the doctor priority is now 1' $e 'data.changes.0.after' 1
    Ge '4.7e' 'an `action` row was written' $e 'data.action.journal_seq' 1
    Write-Host "          use_priorities = $(Dig $e 'data.use_priorities') (0.5 turned this on; with it OFF this call is REFUSED, because GetPriority would return a flat 3)"

    # 4.8 the patient into the medical bed. This is 3.4's own rest-until-healed,
    #     which sets job.restUntilHealed - a pawn with only a BILL will not go to
    #     bed on its own (WorkGiver_PatientGoToBedTreatment needs
    #     ShouldSeekMedicalRestUrgent).
    $e = Send-Cmd rest-until-healed @{ pawns = @($B); bed = $bed }
    if ((Dig $e 'data.counts.accepted') -ne 1) { Note '4.8' "rest-until-healed refused: $(Show (Dig $e 'data.rejected'))" }
    Eq '4.8a' 'rest-until-healed accepted the patient' $e 'data.counts.accepted' 1
    Ge '4.8b' 'an `action` row was written' $e 'data.action.journal_seq' 1

    # 4.9 THE BULLET: the doctor performs it under advance.
    $e = Send-Cmd advance @{ ticks = 6000; max_tps = 600 }
    Eq '4.9a' 'advance ran' $e 'data.reason' 'ticks'
    $e = Send-Cmd bills @{ bench = $B }
    $recipes = @(@(Dig $e 'data.benches') | ForEach-Object { @($_.bills) | ForEach-Object { $_.recipe } })
    if ($recipes -contains 'Anesthetize') {
        Note '4.9b' 'not performed within 6000 ticks; advancing the documented fallback window'
        $e = Send-Cmd advance @{ ticks = 14000; max_tps = 600 }
        Eq '4.9c' 'fallback advance ran' $e 'data.reason' 'ticks'
        $e = Send-Cmd bills @{ bench = $B }
        $recipes = @(@(Dig $e 'data.benches') | ForEach-Object { @($_.bills) | ForEach-Object { $_.recipe } })
    }
    Check '4.9d' 'the surgery bill is GONE - a doctor performed it (Bill_Medical deletes itself on completion)' `
    ($recipes -notcontains 'Anesthetize') 'no Anesthetize bill remains' $recipes

    # 4.10 and the effect landed on the patient
    $e = Send-Cmd pawn @{ id = $B; sections = @('health') }
    $hediffs = @(@(Dig $e 'data.health.hediffs') | ForEach-Object { $_.def })
    Has '4.10' 'the patient carries the Anesthetic hediff' $hediffs 'Anesthetic'

    # 4.11 remove is the other half
    $e = Send-Cmd surgery-add @{ pawn = $B; recipe = 'Anesthetize' }
    if ((Dig $e 'data.ok') -eq $true) {
        $e = Send-Cmd surgery-remove @{ pawn = $B; recipe = 'Anesthetize' }
        Eq '4.11a' 'surgery-remove succeeded' $e 'data.ok' $true
        Ge '4.11b' 'an `action` row was written' $e 'data.action.journal_seq' 1
    }
    else { Note '4.11' "re-add refused (already anesthetized); reason: $(Dig $e 'data.reason')" }

    NoRedErrors '4.12' 'ZERO red errors through phase 4'
}

# ------------------------------------------------------------------- phase 5 --

function Phase5 {
    PhaseBanner 'PHASE 5 - the rest of the surface: the plural forms, the gates that REFUSE, and research'
    $A = $S.A; $B = $S.B

    # 5.1 THE MATRIX. One call, a cross product, over a pawn list.
    $e = Send-Cmd work-priorities @{ set = @(@{ pawns = @($A, $B); works = @('Doctor', 'Firefighter'); priority = 2 }) }
    Eq '5.1a' 'the matrix form accepted' $e 'ok' $true
    Ge '5.1b' 'it wrote up to 4 cells in ONE call' $e 'data.cells' 1
    Eq '5.1c' 'and reports the mode' $e 'data.mode' 'matrix'

    # 5.2 THE COPY FORM - one call, not twenty (amendment item 7)
    $e = Send-Cmd work-priorities @{ copy_from = $A; to = @($B) }
    Eq '5.2a' 'copy_from accepted' $e 'ok' $true
    Eq '5.2b' 'mode is copy' $e 'data.mode' 'copy'
    Ge '5.2c' 'a whole row was written' $e 'data.accepted.0.set' 1

    # 5.3 THE DISABLED-WORK-TYPE GATE. Pawn_WorkSettings.SetPriority answers a
    #     disabled work type with Log.Error - a RED ERROR. This must refuse.
    $e = Send-Cmd pawn @{ id = $A; sections = @('work') }
    $disabled = @(Dig $e 'data.work.disabled')
    if ($disabled.Count -gt 0) {
        $e = Send-Cmd work-priorities @{ set = @(@{ pawns = @($A); work = $disabled[0]; priority = 3 }) }
        Eq '5.3a' 'setting a DISABLED work type is refused, not attempted' $e 'data.counts.accepted' 0
        Eq '5.3b' 'with the work-disabled gate named' $e 'data.rejected.0.gate' 'work-disabled'
        NoRedErrors '5.3c' 'and NO red error was logged (the whole point of the pre-check)'
    }
    else { Note '5.3' 'this colonist has no disabled work type; the red-error pre-check could not be exercised here' }

    # 5.4 THE SPAN. A wrapping span is one call, not two.
    $e = Send-Cmd schedule @{ pawns = @($A, $B); hours = '22-3'; assignment = 'Sleep' }
    Eq '5.4a' 'a wrapping span accepted' $e 'ok' $true
    $hrs = @(Dig $e 'data.hours')
    Check '5.4b' 'six hours in the span (22,23,0,1,2,3)' (($hrs -join ',') -eq '22,23,0,1,2,3') '22,23,0,1,2,3' $hrs
    $row = [string](Dig $e 'data.accepted.0.row')
    Check '5.4c' "the 24-char row uses PawnSerializer's own legend" `
    ($row.Length -eq 24 -and $row[22] -eq 'S' -and $row[0] -eq 'S') '24 chars, S at hours 22 and 0' $row
    Ge '5.4d' 'an `action` row was written' $e 'data.action.journal_seq' 1

    # 5.5 the schedule copy form
    $e = Send-Cmd schedule @{ pawns = @($B); copy_from = $A }
    Eq '5.5' 'schedule copy_from accepted' $e 'ok' $true

    # 5.6 THE COLUMN STRIP, plural, any subset, one call
    $e = Send-Cmd assign @{ pawns = @($A, $B); med_care = 'NormalOrWorse'; self_tend = $true; hostility = 'Flee'; area = 'Home' }
    Eq '5.6a' 'assign accepted both pawns' $e 'data.counts.accepted' 2
    Eq '5.6b' 'medical care took' $e 'data.accepted.0.after.med_care' 'NormalOrWorse'
    Eq '5.6c' 'hostility took' $e 'data.accepted.0.after.hostility_response' 'Flee'
    Eq '5.6d' 'the area took (Area_Home overrides AssignableAsAllowed to true)' $e 'data.accepted.0.after.area' 'Home'
    Check '5.6e' 'and whether the area is actually RESPECTED is published' `
    ((Dig $e 'data.accepted.0.respects_area') -is [bool]) 'a bool' (Dig $e 'data.accepted.0.respects_area')

    # 5.7 area:null is a real setting, not an omission
    $e = Send-Cmd assign @{ pawns = @($A, $B); area = $null }
    Eq '5.7a' 'area:null (unrestricted) accepted' $e 'data.counts.accepted' 2
    Eq '5.7b' 'and the area is cleared' $e 'data.accepted.0.after.area' $null

    # 5.8 an unknown area names 3.2 rather than failing blankly
    $e = Send-Cmd assign @{ pawns = @($A); area = 'no-such-area-xyz' }
    Eq '5.8a' 'an unknown area is a bad-args' $e 'ok' $false
    Check '5.8b' 'and the error lists the assignable areas and says who owns creation' `
    ([string](Dig $e 'error.detail')).Contains('3.2') 'mentions spec 3.2' (Dig $e 'error.detail')

    # 5.9 RESEARCH. The gate lives in the widget: SetCurrentProject checks only
    #     baseCost > 0.
    $e = Send-Cmd research @{ cap = 5 }
    $avail = @(Dig $e 'data.available.list')
    Precondition '5.9' 'at least one startable research project' ($avail.Count -gt 0) `
        'the colony has finished everything, or has no bench.'
    $proj = $avail[0].def
    $e = Send-Cmd research-set @{ project = $proj }
    Eq '5.9a' 'research-set succeeded' $e 'data.ok' $true
    Eq '5.9b' 'the manager reads BACK as the new project (durable state, not a hope)' $e 'data.current' $proj
    Ge '5.9c' 'an `action` row was written' $e 'data.action.journal_seq' 1
    Eq '5.9d' 'progress was read through the guarded route' $e 'data.source' 'backing-field'

    # 5.10 the gate refuses an unstartable project WITH THE CLAUSE THAT BLOCKED IT
    $e = Send-Cmd research-set @{ project = 'ShipBasics' }
    if ((Dig $e 'data.ok') -eq $false) {
        Eq '5.10a' 'an unstartable project is refused' $e 'data.ok' $false
        $clause = [string](Dig $e 'data.blocked_by')
        Check '5.10b' "with CanStartNow's own clause word (the same vocabulary 2.4's blocked_by uses)" `
        ($clause -in @('finished', 'prerequisites', 'techprints', 'no-bench', 'mechanitor', 'analysis', 'codex-hidden', 'grav-engine')) `
            'a CanStartNow clause word' $clause
        Check '5.10c' 'and it says SetCurrentProject would have accepted it' `
        ([string](Dig $e 'data.note')).Contains('baseCost') "the note names SetCurrentProject's only check" (Dig $e 'data.note')
    }
    else { Note '5.10' 'ShipBasics was startable on this save; the refusal path was not exercised here' }

    # 5.11 research-stop
    $e = Send-Cmd research-stop
    Eq '5.11' 'research-stop succeeded' $e 'data.ok' $true

    # 5.12 THE DRAFT-STATE GATE. `tend` is drafted-only; an undrafted doctor must
    #      be REFUSED with the game's own reason, not silently no-op.
    Send-Cmd undraft @{ pawns = @($A) } | Out-Null
    $e = Send-Cmd tend @{ pawn = $A; target = $B }
    Eq '5.12a' 'an UNDRAFTED doctor is refused by the drafted-only gate' $e 'data.counts.accepted' 0
    Eq '5.12b' 'with the gate named' $e 'data.rejected.0.gate' 'drafted-only'
    Eq '5.12c' 'and nothing was journalled' $e 'data.action.journal_seq' $null

    # 5.13 the same order, drafted, is accepted or refused for a REAL reason
    Send-Cmd draft @{ pawns = @($A) } | Out-Null
    $e = Send-Cmd tend @{ pawn = $A; target = $B }
    Check '5.13' "drafted, the same call is either accepted or refused for a substantive reason (never 'drafted-only')" `
    (((Dig $e 'data.counts.accepted') -eq 1) -or ((Dig $e 'data.rejected.0.gate') -ne 'drafted-only')) `
        'accepted, or a non-draft-state reason' (Dig $e 'data.rejected')

    # 5.14 fire-at-will's gate (still drafted here)
    $e = Send-Cmd fire-at-will @{ pawns = @($A); on = $false }
    Check '5.14' 'fire-at-will answers with a gate, never silently' `
    (((Dig $e 'data.counts.accepted') -eq 1) -or ((Dig $e 'data.rejected.0.gate') -in @('not-drafted', 'no-ranged-weapon', 'no-drafter', 'already'))) `
        'accepted, or a named gate' (Dig $e 'data.rejected')
    Send-Cmd undraft @{ pawns = @($A) } | Out-Null

    # 5.15 WARDEN. Crossing the exclusive/non-exclusive split is a RED ERROR in
    #      the game; here it must be a clean bad-args.
    $e = Send-Cmd warden @{ pawns = @($B); enable = @('AttemptRecruit') }
    Eq '5.15a' 'passing an EXCLUSIVE mode to `enable` is a bad-args' $e 'ok' $false
    Check '5.15b' 'and the error names the red error it prevented' `
    ([string](Dig $e 'error.detail')).ToLower().Contains('red error') 'the detail mentions the red error' (Dig $e 'error.detail')

    # 5.16 a prisoner, then the warden verb for real
    $e = Send-Cmd 'dev:spawn-pawn' @{ kind = 'Colonist'; faction = 'hostile'; pos = "pawn:$A" }
    $prisoner = Dig $e 'data.pawns.0.id'
    if ($null -eq $prisoner) {
        Note '5.16' "dev:spawn-pawn returned no id (no visible hostile faction?); warden path not exercised. detail=$(Dig $e 'error.detail')"
    }
    else {
        Send-Cmd 'dev:guest-status' @{ pawn = $prisoner; status = 'prisoner' } | Out-Null
        $e = Send-Cmd warden @{ pawns = @($prisoner); mode = 'AttemptRecruit'; recruitable = $true }
        Eq '5.16a' 'warden accepted the prisoner' $e 'data.counts.accepted' 1
        Eq '5.16b' 'the exclusive interaction mode took' $e 'data.accepted.0.after.interaction' 'AttemptRecruit'
        Ge '5.16c' 'an `action` row was written' $e 'data.action.journal_seq' 1
        Has '5.16d' 'the catalog names the exclusive modes' (@(Dig $e 'data.modes_available.mode')) 'Release'
        $e = Send-Cmd warden @{ pawns = @($prisoner); mode = 'Release' }
        Eq '5.16e' 'RELEASE IS A MODE (not a Released flag write)' $e 'data.accepted.0.after.interaction' 'Release'
    }

    # 5.17 THE DURABLE-STATE WRITE. HaulGeneral (phase 2) does not set
    #      prioritizeSustains, so that step could only verify the result's
    #      honesty about NOT writing. DoctorTendToHumanlikes DOES set it
    #      (Core/Defs/WorkGiverDefs/WorkGivers.xml), so this is the branch where
    #      TryTakeOrderedJobPrioritizedWork calls
    #      mindState.priorityWork.Set(cell, giverDef) — scribed state with a
    #      30000-tick timeout, not a one-shot job.
    Send-Cmd 'dev:damage' @{ pawn = $B; mode = 'amount'; amount = 6; hits = 2; def = 'Cut' } | Out-Null
    Send-Cmd undraft @{ pawns = @($A) } | Out-Null
    $e = Send-Cmd prioritize @{ pawn = $A; work = 'DoctorTendToHumanlikes'; thing = $B }
    if ((Dig $e 'data.ok') -eq $true) {
        Eq '5.17a' 'prioritize succeeded on a SUSTAINING work giver' $e 'data.ok' $true
        Eq '5.17b' 'and it reports sustains:true' $e 'data.sustains' $true
        Eq '5.17c' 'mindState.priorityWork is LIVE (the durable write happened)' $e 'data.priority_work.active' $true
        Eq '5.17d' 'and names this work giver' $e 'data.priority_work.work_giver' 'DoctorTendToHumanlikes'
        Eq '5.17e' 'with the game`s own 30000-tick timeout published' $e 'data.priority_work.timeout_ticks' 30000
        # And drafting CLEARS it - Pawn_DraftController's setter calls
        # priorityWork.ClearPrioritizedWorkAndJobQueue().
        Send-Cmd draft @{ pawns = @($A) } | Out-Null
        Send-Cmd undraft @{ pawns = @($A) } | Out-Null
        $e = Send-Cmd clear-priority-work @{ pawns = @($A) }
        Check '5.17f' 'drafting cleared the durable priorityWork (so clear-priority-work now has nothing to do)' `
        (((Dig $e 'data.counts.accepted') -eq 0) -or ((Dig $e 'data.accepted.0.after.active') -eq $false)) `
            'nothing left to clear, or after.active:false' (Dig $e 'data')
    }
    else {
        Note '5.17' "DoctorTendToHumanlikes not offered here: $(Dig $e 'data.reason') - the durable priorityWork write was not exercised"
    }

    # 5.18 the whole run's standing invariants
    NoRedErrors '5.18a' 'ZERO red errors across the WHOLE run'
    $e = Send-Cmd status
    Check '5.18b' 'and no force-pausing modal was left behind (spec 1.7)' `
    ($null -eq (Dig $e 'data.forcePause')) 'absent' (Dig $e 'data.forcePause')
}

# ---------------------------------------------------------------------- main --

Write-Host 'AutoRimmer spec 3.4 acceptance - pawn orders + policies (git-bug 39c9db7)'
Write-Host "protocol root: $Root"
if (-not $DryRun -and -not (Test-Path (Join-Path $Root 'status.json'))) {
    Write-Host "no status.json under $Root - start the bench with _RimWorld-Agent\run-agent.ps1, or pass -Root" -ForegroundColor Red
    exit 2
}
New-Item -ItemType Directory -Force -Path (Join-Path $Root 'commands') | Out-Null
$wanted = @($Phase | Sort-Object -Unique | Where-Object { $_ -ne 0 })
Write-Host "phases: 0 + $($wanted -join ', ')"

Phase0
foreach ($p in $wanted) { & "Phase$p" }

Write-Host ''
Write-Host ('=' * 78)
if ($script:Fails.Count -gt 0) {
    Write-Host "RESULT: $($script:Fails.Count) FAILED of $($script:Checks) checks - $($script:Fails -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "RESULT: all $($script:Checks) checks passed" -ForegroundColor Green
exit 0
