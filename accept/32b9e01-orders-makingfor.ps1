#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Acceptance runner for git-bug 32b9e01 — `orders` claimed read-only but
    mutated (missing FloatMenuMakerMap.makingFor).

.DESCRIPTION
    `accept/32b9e01-orders-makingfor.md` is the same acceptance in prose, with
    every envelope written out. This is its executable form.

    IT SPEAKS THE RAW FILE PROTOCOL, not `rwa` — BORGES has no python (Store
    stub only; RUNLOG session 5 records it), so `rwa` cannot run here and an
    unrunnable acceptance script is worse than none. On a box that HAS python,
    `rwa <op> --args-json '<json>'` sends the identical envelopes.

    THE PHASES SPAN THREE DIFFERENT BUILDS. That is the whole shape of this
    acceptance and the script cannot hide it — pass -Build so the assertions
    match the binary that is actually loaded:

      -Phase M -Build main     main's SHIPPED assembly. The starved bill gets
                               NO entry at all. Records the baseline.
      -Phase R -Build repro    this branch with the one-line fix disabled (see
                               the .md). Proves the 500-600 tick write.
      -Phase M -Build branch   this branch, clean. The starved bill comes back
                               in `blocked` with the game's MissingMaterials.
      -Phase F -Build branch   the write is gone and stays gone.
      -Phase D -Build any      the disclosure is true: one `orders` call flips
                               a bill's SCRIBED `paused` flag, either build.

    Every phase stages its OWN fresh fixture bench, because a bill whose
    nextTickToSearchForIngredients is already in the future is skipped by the
    `TicksGame <= ...` clause before it can be re-tested — a reused bench passes
    for the wrong reason.

    Raw result envelopes are written to -Evidence so headline numbers can be
    re-derived from artifacts rather than from this script's own summary.

    Start the bench first (`_RimWorld-Agent\run-agent.ps1 -Quicktest`) and LEAVE
    IT PAUSED. `advance` is never called here; TicksGame must not move between
    the two `bills` reads or the delta is not the delta.

.EXAMPLE
    pwsh accept\32b9e01-orders-makingfor.ps1 -Phase M -Build main
    pwsh accept\32b9e01-orders-makingfor.ps1 -Phase R -Build repro
    pwsh accept\32b9e01-orders-makingfor.ps1 -Phase M,F,D -Build branch
#>
[CmdletBinding()]
param(
    [string]$Root = "$env:USERPROFILE\misc\rimworld\_RimWorld-Agent\SaveData\AutoRimmer",
    [ValidateSet('M', 'R', 'F', 'D')][string[]]$Phase = @('M'),
    [ValidateSet('main', 'branch', 'repro')][string]$Build = 'branch',
    [string]$Evidence = "$PSScriptRoot\..\.accept-evidence",
    [switch]$DryRun,
    [switch]$Echo
)

$ErrorActionPreference = 'Stop'
$script:Fails = @()
$script:Checks = 0
$script:Seq = 0
$script:Skipped = @()

# The bill work giver whose reason went missing.
#
# Matched as the `DoBills` PREFIX, not a specific defName. The giver that
# actually fires for `world-fixture`'s TableButcher + ButcherCorpseFlesh is
# `DoBillsButcherFlesh` — guessing `DoBillsButcherTable` from the building's
# def cost a full round and reported a real pass as a failure. The target is
# always the butcher table, so only its own bill givers can appear here.
$BUTCHER_GIVER = 'DoBills'

if (-not $DryRun) { New-Item -ItemType Directory -Force $Evidence | Out-Null }
$script:RunTag = "{0}-{1}" -f $Build, (Get-Date -Format 'HHmmss')

# ------------------------------------------------------------------ protocol --
# commands/<id>.json in, results/<id>.json out. The poller runs a 500 ms cycle
# and ignores inbox files younger than its MinFileAgeMs, so a round trip has a
# floor of roughly 0.25-1 s even for a trivial verb; that is the protocol, not
# this script being slow.
#
# Ids stay in [A-Za-z0-9-] so Poller.Sanitize leaves them alone and the result
# filename is exactly <id>.json.

function Send-Cmd {
    param(
        [Parameter(Mandatory)][string]$Op,
        [hashtable]$Args = @{},
        [int]$TimeoutSec = 120
    )
    $script:Seq++
    $slug = ($Op -replace '[^A-Za-z0-9]', '')
    if ($slug.Length -gt 16) { $slug = $slug.Substring(0, 16) }
    $id = "mf-{0}-{1:d3}-{2}" -f $Build, $script:Seq, $slug
    $envelope = @{ id = $id; op = $Op; args = $Args } | ConvertTo-Json -Depth 20 -Compress

    if ($DryRun) {
        Write-Host "    would send: $envelope"
        return @{ ok = $true; op = $Op; data = @{}; _dry = $true }
    }

    $inbox = Join-Path $Root "commands\$id.json"
    $result = Join-Path $Root "results\$id.json"
    if (Test-Path $result) { Remove-Item $result -Force }
    [System.IO.File]::WriteAllText($inbox, $envelope, [System.Text.UTF8Encoding]::new($false))

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $result) {
            Start-Sleep -Milliseconds 60      # let the write land
            try {
                $raw = Get-Content $result -Raw
                $env = $raw | ConvertFrom-Json -AsHashtable
                # Evidence: the raw envelope, not this script's reading of it.
                Copy-Item $result (Join-Path $Evidence "$script:RunTag-$id.json") -Force
                if ($Echo) { Write-Host "    <- $($env | ConvertTo-Json -Depth 8 -Compress)" }
                return $env
            }
            catch { Start-Sleep -Milliseconds 120 }
        }
        Start-Sleep -Milliseconds 200
    }
    return @{ ok = $false; op = $Op
        error = @{ code = 'acc-timeout'
            detail = "no results\$id.json within ${TimeoutSec}s — is the bench running?" }
    }
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

# ------------------------------------------- git-bug 722c951: the escape ----
#
# `advance` has TWO new default-on guards, and both are right for a play loop
# and wrong for a fixture harness:
#
#   * it REFUSES (ok:false, error.code "unread-journal") when the previous
#     advance journaled events that no `journal` call has read, and
#   * it HALTS (reason:"casualty") when an own-faction pawn goes down or dies
#     while time is running.
#
# This suite is not a play loop. Its one advance exists to let the resource
# counter rebuild so a `bills` read has something to count,
# and it never reads the journal in between. Without an opt-out every advance
# after the first that journaled anything would come back refused and every
# check below would be measuring the refusal instead of the thing it names.
#
# So the opt-out lives HERE, in ONE wrapper, and not at the call sites: an
# inline `unread_ok` is indistinguishable to the next reader from one somebody
# added to get a red check green. The reason string names this file, so
# `journal --types action` on the bench says which harness turned the guard off
# and why. Both escapes are per-call and journaled as an act by the mod
# (session 13's threat-pardon precedent).
$script:Escape = "accept/32b9e01-orders-makingfor.ps1: fixture harness, not a play loop - it advances to move game state and asserts on the result, and does not read the journal between advances"

function Send-Advance {
    param([hashtable]$Args = @{}, [int]$TimeoutSec = 120)
    $a = $Args.Clone()
    if (-not $a.ContainsKey('unread_ok')) { $a['unread_ok'] = $script:Escape }
    if (-not $a.ContainsKey('through_casualties')) { $a['through_casualties'] = $script:Escape }
    return Send-Cmd advance $a $TimeoutSec
}

# ------------------------------------------------------------------- asserts --

function Check {
    param([string]$Num, [string]$What, [bool]$Ok, [string]$Expected, $Actual)
    $script:Checks++
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

function Note { param([string]$Num, [string]$Text) Write-Host ("  {0,-7} NOTE    {1}" -f $Num, $Text) -ForegroundColor DarkYellow }

function Skip {
    param([string]$Num, [string]$What, [string]$Detail)
    Write-Host ("  {0,-7} SKIP    {1}" -f $Num, $What) -ForegroundColor Yellow
    Write-Host "          $Detail"
    $script:Skipped += "$Num $What"
}

function Precondition {
    param([string]$Num, [string]$What, [bool]$Ok, [string]$Detail)
    if ($DryRun) { Write-Host ("  {0,-7} NEEDS   {1}" -f $Num, $What) -ForegroundColor DarkCyan; return }
    if ($Ok) { Write-Host ("  {0,-7} OK      precondition: {1}" -f $Num, $What) -ForegroundColor DarkGreen; return }
    Write-Host ("  {0,-7} STOP    precondition NOT MET: {1}" -f $Num, $What) -ForegroundColor Red
    Write-Host "          $Detail"
    Write-Host "          This is a FIXTURE gap, not a defect. Stage it and re-run."
    exit 2
}

function PhaseBanner { param([string]$T) Write-Host ''; Write-Host ('=' * 78); Write-Host $T; Write-Host ('=' * 78) }

# ------------------------------------------------------------- shared helpers --

# Fixture F3/F5/F6 in one call. Returns the new bench's thingIDNumber.
#   skill_min:0     F5 — the default of 4 makes PawnAllowedToStartAnew `continue`
#                   BEFORE the ingredient search, so the write never happens and
#                   the phase silently passes for the wrong reason.
#   target_count    F6 — a satisfied bill is another silent skip.
# TWO CALLS, NOT ONE, and the second names the bench explicitly.
#
# `world-fixture {steps:["bench","bill"]}` does NOT chain: the `bill` step calls
# WorldFixtureVerbs.FindBench, which with no `bench` arg returns the FIRST
# TableButcher in the thing lister — not the one the `bench` step just spawned.
# On a virgin map they are the same table and it looks fine. On the second run
# of this script in one session they are not, and the bills land on the OLD
# bench while `data.bench.id` reports the new one. `orders` then scans a bench
# with no bills, `total` comes back 0, and phase M1 passes vacuously.
#
# That happened on the first real run of this acceptance (bench 23492, bills on
# 23491). Filed separately; the acceptance does not depend on the fix.
function New-FixtureBench {
    param([string]$Num, [int]$TargetCount = 200, [int]$UnpauseWhen = 5)

    $e1 = Send-Cmd world-fixture @{ steps = @('bench') }
    Precondition $Num 'world-fixture spawned a butcher table' `
    ([bool](Dig $e1 'ok')) "world-fixture failed: $(Show (Dig $e1 'error'))"
    $id = Dig $e1 'data.bench.id'
    Precondition "$Num.b" 'the fixture published a bench id' ([bool]($null -ne $id)) `
    'no data.bench.id in the world-fixture result'
    $at = @(Dig $e1 'data.bench.at')

    $e2 = Send-Cmd world-fixture @{
        steps        = @('bill')
        bench        = [int]$id
        skill_min    = 0
        target_count = $TargetCount
        unpause_when = $UnpauseWhen
    }
    Precondition "$Num.c" 'world-fixture added the bill' `
    ([bool](Dig $e2 'ok')) "world-fixture bill step failed: $(Show (Dig $e2 'error'))"

    # The whole point of the two-call form. If this ever fails, the bills are on
    # a different table than the one under test and every number below is void.
    $billBench = Dig $e2 'data.bill.bench_id'
    Precondition "$Num.d" 'the bill landed on the bench we just spawned' `
    ([bool]([int]$billBench -eq [int]$id)) `
    "bench step made #$id but the bill step targeted #$billBench — the scan would run against an EMPTY bench and pass for the wrong reason"

    $radius = Dig $e2 'data.bill.expect_first.ingredient_radius' 24
    Write-Host ("          BENCH = #{0} at {1}, recipe {2}, bills {3}, ingredient_radius {4}" -f `
            $id, (Show $at), (Dig $e2 'data.bill.recipe'), (Dig $e2 'data.bill.expect_bills'), $radius)
    return @{ id = [int]$id; at = $at; radius = [double]$radius }
}

# F2 — an UNDRAFTED colonist. `orders` skips every giver whose
# canBeDoneWhileDrafted is false, which is nearly all of them.
#
# SELECT BY CAPABILITY, NOT BY ROSTER INDEX. `pawns` orders by a live attention
# score (`data.order` says so verbatim), so roster[0] is whoever is currently
# worst off — git-bug 1eb2262. The brief line carries no `drafted` boolean; it
# carries a `flags` list, and dead pawns never appear at all (the verb skips
# them before scoring).
function Get-Actor {
    param([string]$Num)
    $e = Send-Cmd pawns @{ filter = 'colonist' }
    Precondition $Num 'pawns answered' ([bool](Dig $e 'ok')) (Show (Dig $e 'error'))
    $roster = @(Dig $e 'data.list')
    $pick = $null
    foreach ($p in $roster) {
        $flags = @(Dig $p 'flags')
        if ($flags -contains 'downed') { continue }
        if ($flags -contains 'drafted') { continue }
        if ($flags -contains 'contained') { continue }
        $pick = $p; break
    }
    Precondition "$Num.b" 'at least one visible, undrafted, standing colonist' ([bool]($null -ne $pick)) `
    "roster of $($roster.Count): $(Show ($roster | ForEach-Object { @{ n = (Dig $_ 'name'); f = (Dig $_ 'flags') } }))"
    $id = Dig $pick 'id'
    Write-Host ("          A = #{0} {1}  flags={2}" -f $id, (Dig $pick 'name'), (Show (Dig $pick 'flags')))
    return [int]$id
}

# F4 — an ingredient search that SUCCEEDS produces a job, the giver lands in
# `available`, and there is nothing to measure. Butcher recipes take corpses.
#
# The test is RADIUS, not map-wide presence. `WorkGiver_DoBill
# .TryFindBestBillIngredients` searches within `bill.ingredientSearchRadius` of
# the BILL GIVER's position, so a corpse the far side of a 250x250 map cannot
# feed this bill and is not a fixture miss. A map-wide refusal would send the
# runner off to destroy corpses that were never in play — and the first run of
# this script did exactly that, on one corpse 53 cells out.
function Assert-NoButcherables {
    param([string]$Num, $Bench)
    $e = Send-Cmd things @{ category = 'corpses'; detail = $true }
    if (-not (Dig $e 'ok')) { Note $Num "could not read corpses ($(Show (Dig $e 'error'))) — F4 unverified"; return }
    $all = @(Dig $e 'data.things')
    if ($all.Count -eq 0) {
        Precondition $Num 'no butcherable corpse on the map at all (F4)' $true ''
        return
    }
    $bx = [double]$Bench.at[0]; $bz = [double]$Bench.at[1]
    $r = $Bench.radius
    $inRange = @()
    foreach ($c in $all) {
        $p = @(Dig $c 'at')
        if ($p.Count -lt 2) { continue }
        $d = [math]::Sqrt([math]::Pow([double]$p[0] - $bx, 2) + [math]::Pow([double]$p[1] - $bz, 2))
        if ($d -le $r) { $inRange += "#$(Dig $c 'id') $(Dig $c 'label') at $(Show $p) — $([math]::Round($d,1)) cells" }
    }
    Note $Num ("{0} corpse(s) on the map; {1} inside the bill's {2}-cell ingredient radius of the bench" -f $all.Count, $inRange.Count, $r)
    Precondition $Num "no butcherable corpse within the bill's ingredient radius (F4)" ([bool]($inRange.Count -eq 0)) `
    "in range: $($inRange -join '; '). The ingredient search would SUCCEED, the giver would land in ``available``, and the phase would pass for the wrong reason. dev:destroy them or re-site the bench."
}

function Get-Tick {
    param([string]$Num)
    $e = Send-Cmd digest
    Precondition $Num 'digest answered' ([bool](Dig $e 'ok')) (Show (Dig $e 'error'))
    return [int](Dig $e 'data.time.tick')
}

# The first bill on the bench, as `bills` reports it.
function Get-FirstBill {
    param([int]$Bench)
    $e = Send-Cmd bills @{ bench = $Bench }
    return @{ env = $e; bill = (Dig $e 'data.benches.0.bills.0') }
}

# Every entry in `available` + `blocked` whose `work` is the butcher giver.
function Find-ButcherEntries {
    param($OrdersEnv)
    $out = @()
    foreach ($listName in 'available', 'blocked') {
        foreach ($row in @(Dig $OrdersEnv "data.$listName")) {
            $w = Dig $row 'work'
            if ($w -and $w -like "$BUTCHER_GIVER*") {
                $out += @{ list = $listName; row = $row }
            }
        }
    }
    return $out
}

# ------------------------------------------------------------------- phase 0 --

function Phase0 {
    PhaseBanner "PHASE 0 - preflight (build under test: $Build)"

    $e = Send-Cmd status
    Precondition '0.1a' 'the bench answered `status`' ([bool](Dig $e 'ok')) (Show (Dig $e 'error'))
    Precondition '0.1b' 'a game is loaded' ([bool]((Dig $e 'data.gameLoaded') -eq $true)) `
    'load or generate a colony first (run-agent.ps1 -Quicktest)'
    Precondition '0.1c' 'the game is PAUSED — TicksGame must not move mid-phase' `
    ([bool]((Dig $e 'data.paused') -eq $true)) `
    "speed is $(Dig $e 'data.speed'); every delta in this acceptance is void while the clock runs"
    Check '0.1d' 'no force-pausing modal is up' `
    ($null -eq (Dig $e 'data.forcePause')) 'absent' (Dig $e 'data.forcePause')

    Note '0.2' ("mod {0}, sid {1}, tick {2}" -f (Dig $e 'data.mod'), (Dig $e 'data.sid'), (Dig $e 'data.tick'))

    # The instrument only exists on this branch. Its presence/absence is itself a
    # check that the binary loaded is the one the -Build flag claims.
    $verbs = @(Dig $e 'data.verbs')
    Precondition '0.3' '`bills` and `orders` are registered' `
    ([bool](($verbs -contains 'bills') -and ($verbs -contains 'orders'))) `
    "verbs: $(Show $verbs)"
}

# Guards against the single most expensive mistake available here: running a
# phase against the wrong assembly and believing the result. The instrument
# field exists ONLY on the branch, so it identifies the binary directly.
function Assert-BuildIdentity {
    param([int]$Bench)
    $b = (Get-FirstBill $Bench).bill
    $hasInstrument = ($null -ne $b) -and ($b.Contains('next_ingredient_search_tick'))
    if ($Build -eq 'main') {
        Precondition 'ID' 'the loaded assembly is main (no `next_ingredient_search_tick` on a bill)' `
        ([bool](-not $hasInstrument)) `
        "the bill HAS the instrument field, so this is a branch build, not main. -Build main is wrong."
    }
    else {
        Precondition 'ID' "the loaded assembly is the branch (`next_ingredient_search_tick` present)" `
        ([bool]$hasInstrument) `
        "the bill has NO instrument field, so this is main's assembly. Rebuild and restart the bench."
    }
}

# --------------------------------------------------------- phase M (no instr) --
# main vs branch, shipped binaries, no repro build. The consequence the agent
# actually feels: a reason where there was silence.

function PhaseM {
    PhaseBanner "PHASE M - the missing reasons ($Build)"

    $A = Get-Actor 'M.f2'
    $B = New-FixtureBench 'M.f3'
    $BENCH = $B.id
    Assert-NoButcherables 'M.f4' $B
    Assert-BuildIdentity $BENCH

    $e = Send-Cmd orders @{ pawn = $A; thing = $BENCH }
    Eq 'M.1' 'orders answered' $e 'ok' $true

    $hits = @(Find-ButcherEntries $e)
    $blockedTotal = Dig $e 'data.blocked_total'
    $availTotal = Dig $e 'data.available_total'
    $total = Dig $e 'data.total'
    Note 'M.2' ("available_total={0} blocked_total={1} total={2}; butcher-giver entries={3}" -f `
            $availTotal, $blockedTotal, $total, $hits.Count)

    # ON MAIN, total=0 IS THE CORRECT BASELINE — not a fixture miss.
    #
    # Read WorkGiver_DoBill.StartOrResumeBillJob's failure branch:
    #     if (FloatMenuMakerMap.makingFor != pawn)
    #         bill.nextTickToSearchForIngredients = TicksGame + ReCheckFailed...;
    #     else if (flag)
    #         JobFailReason.Is("MissingMaterials".Translate(text), bill.Label);
    # The two arms are mutually exclusive. main takes the WRITE arm and sets no
    # reason, so ScanWorkGivers' `if (!JobFailReason.HaveReason) continue;` drops
    # the giver and the whole scan can legitimately come back empty on a target
    # that offers nothing else. An earlier draft of this script demanded total>0
    # here and stopped a correct run; the demand was wrong.
    #
    # ShouldSkip does NOT gate this away — it returns false as soon as any bill
    # giver on the map has AnyShouldDoNow, which the fixture bill satisfies. So
    # the giver IS reached on both builds, and the branch run is the
    # discriminator: same map, same pawn, same recipe, one `if` apart.
    if ($Build -eq 'main') {
        Note 'M.0' "total=0 is EXPECTED here — main takes the nextTickToSearchForIngredients arm and sets no JobFailReason, so the giver is dropped by the `!HaveReason` continue"
    }
    else {
        Precondition 'M.0' 'the work-giver scan returned at least one giver' `
        ([bool]([int]$total -gt 0)) `
        "total=0 with unavailable_reason=$(Show (Dig $e 'data.unavailable_reason')). On THIS build the makingFor arm should have produced a MissingMaterials reason, so an empty scan means the giver never ran. Check that the bill landed on bench #$BENCH."
    }

    if ($Build -eq 'main') {
        # M1 — the baseline. The player right-clicking that table sees a greyed
        # "Cannot butcher: missing materials"; the agent saw nothing at all.
        Check 'M1' "no $BUTCHER_GIVER* entry in either list — the reason is missing entirely" `
        ([bool]($hits.Count -eq 0)) 'zero butcher-giver entries' `
        ($hits | ForEach-Object { "$($_.list): $(Show $_.row)" })
        Write-Host ''
        Write-Host "  BASELINE for M3 — record these and pass them to the branch run:" -ForegroundColor Cyan
        Write-Host "      blocked_total = $blockedTotal"
        Write-Host "      available_total = $availTotal"
    }
    else {
        # M2 — the fix. The entry is present, in `blocked`, carrying the game's
        # own MissingMaterials string.
        Check 'M2a' "a $BUTCHER_GIVER* entry now exists" ([bool]($hits.Count -ge 1)) `
        'at least one butcher-giver entry' $hits.Count
        if ($hits.Count -ge 1) {
            $h = $hits[0]
            Check 'M2b' 'it is in `blocked`, not `available` (no job was produced)' `
            ([bool]($h.list -eq 'blocked')) 'blocked' $h.list
            $reason = Dig $h.row 'reason'
            Check 'M2c' "its reason is the game's own MissingMaterials string" `
            ([bool]($reason -and ($reason -match 'issing'))) `
            "a reason matching /issing/ (English: 'Missing materials: ...')" $reason
            Note 'M2d' ("reason verbatim: {0}" -f (Show $reason))
            Note 'M2e' ("label verbatim: {0}" -f (Show (Dig $h.row 'label')))
        }
        Note 'M3' "compare blocked_total ($blockedTotal) against the main run's baseline by hand — it must be higher by exactly the number of givers that gained a reason"
    }
}

# ------------------------------------------------------------------- phase R --
# REPRO. Needs this branch built with the fix disabled (one line, see the .md).
# Proves claim 1: one `orders` call moves the bill forward by 500-600 ticks.

function PhaseR {
    PhaseBanner "PHASE R - reproduce the write (INSTRUMENTED REPRO BUILD)"
    if ($Build -ne 'repro') {
        Skip 'R' 'phase R needs -Build repro' `
            'build this branch with FloatMenuMakerMap.makingFor = prevMakingFor in ScanWorkGivers, per the .md. Never commit that line.'
        return
    }

    $A = Get-Actor 'R.f2'
    $B = New-FixtureBench 'R.f3'
    $BENCH = $B.id
    Assert-NoButcherables 'R.f4' $B
    Assert-BuildIdentity $BENCH

    $T0 = Get-Tick 'R1'
    Note 'R1' "T0 = $T0"

    $r2 = Get-FirstBill $BENCH
    $N0 = Dig $r2.bill 'next_ingredient_search_tick'
    Precondition 'R2' 'the bill publishes next_ingredient_search_tick' ([bool]($null -ne $N0)) `
    'the instrument is missing — this is not the branch build'
    Note 'R2' "N0 = $N0 (any value <= T0 works; the test is the DELTA)"

    $e = Send-Cmd orders @{ pawn = $A; thing = $BENCH }
    Eq 'R3a' 'orders answered' $e 'ok' $true
    $hits = @(Find-ButcherEntries $e)
    Check 'R3b' "defect 4 visible: no $BUTCHER_GIVER* entry, the reason is missing" `
    ([bool]($hits.Count -eq 0)) 'zero butcher-giver entries' $hits.Count

    $r4 = Get-FirstBill $BENCH
    $N1 = Dig $r4.bill 'next_ingredient_search_tick'
    $delta = [int]$N1 - [int]$T0
    Check 'R4' "N1 - T0 is in [500,600] — ReCheckFailedBillTicksRange, written by a READ-ONLY verb" `
    ([bool]($delta -ge 500 -and $delta -le 600)) '500..600' "N1=$N1 T0=$T0 delta=$delta"
    Write-Host "          delta = $delta   <-- Rand.RangeInclusive(500,600); a FRESH fixture gives a DIFFERENT number in that window, which is the RNG-burn evidence" -ForegroundColor Cyan

    $T1 = Get-Tick 'R5'
    Check 'R5' 'the clock did not move — the write in R4 was the verb, not time' `
    ([bool]($T1 -eq $T0)) "tick still $T0" $T1
}

# ------------------------------------------------------------------- phase F --
# FIXED. Same measurement, clean branch build: the verb writes nothing, and
# keeps writing nothing across repeated asking.

function PhaseF {
    PhaseBanner "PHASE F - the write is gone (clean branch build)"
    if ($Build -ne 'branch') {
        Skip 'F' 'phase F needs -Build branch' 'rebuild without the repro edit and restart the bench'
        return
    }

    $A = Get-Actor 'F.f2'
    $B = New-FixtureBench 'F.f3'
    $BENCH2 = $B.id
    Assert-NoButcherables 'F.f4' $B
    Assert-BuildIdentity $BENCH2

    $T0 = Get-Tick 'F.1'
    Note 'F.1' "T0 = $T0"

    $b0 = (Get-FirstBill $BENCH2).bill
    $N0 = Dig $b0 'next_ingredient_search_tick'
    Precondition 'F.2a' 'the bill publishes next_ingredient_search_tick' ([bool]($null -ne $N0)) `
    'the instrument is missing — this is not the branch build'
    Eq 'F.2b' 'a fresh bill is on no cooldown' $b0 'ingredient_search_cooldown' 0
    Note 'F.2' "N0 = $N0"

    $e = Send-Cmd orders @{ pawn = $A; thing = $BENCH2 }
    Eq 'F.3' 'orders answered' $e 'ok' $true

    $b1 = (Get-FirstBill $BENCH2).bill
    Check 'F.4a' 'next_ingredient_search_tick is IDENTICAL to N0 — the verb wrote nothing' `
    ([bool]((Dig $b1 'next_ingredient_search_tick') -eq $N0)) "$N0" (Dig $b1 'next_ingredient_search_tick')
    Eq 'F.4b' 'still no cooldown' $b1 'ingredient_search_cooldown' 0

    # F.5 — the note stops claiming read-only and prices what asking costs.
    #
    # Test the OLD CLAIM VERBATIM, not the words in it. main's note ended
    # "Read-only: no job is taken."; the new one says "asking is not read-only",
    # which a bare /Read-only/ search flags as a failure — PowerShell's -match is
    # case-insensitive, so it hit the denial. A negative assertion has to name
    # the sentence it is banning, not a word that appears in the replacement.
    $note = [string](Dig $e 'data.note')
    Check 'F.5a' 'the result note no longer carries main''s "Read-only: no job is taken." claim' `
    ([bool]($note -notmatch 'Read-only: no job is taken')) `
    'the old claim absent' $note
    Check 'F.5b' 'it discloses that an incompletable bill may be deleted' `
    ([bool]($note -match 'delet')) 'a "delete" disclosure' $note
    Check 'F.5c' 'it discloses that a job id is consumed per candidate' `
    ([bool]($note -match 'job id')) 'a "job id" disclosure' $note
    Note 'F.5' ("note verbatim: {0}" -f $note)

    # F.6 — the fix holds across repeated asking, which is how an agent uses it.
    $null = Send-Cmd orders @{ pawn = $A; thing = $BENCH2 }
    $b2 = (Get-FirstBill $BENCH2).bill
    Check 'F.6' 'still identical to N0 after a SECOND orders call' `
    ([bool]((Dig $b2 'next_ingredient_search_tick') -eq $N0)) "$N0" (Dig $b2 'next_ingredient_search_tick')

    $T1 = Get-Tick 'F.6b'
    Check 'F.6c' 'the clock never moved across the whole phase' ([bool]($T1 -eq $T0)) "tick still $T0" $T1

    # F.7 — the standing invariant.
    $j = Send-Cmd journal @{ types = @('red_error') }
    Eq 'F.7' 'zero red errors' $j 'data.count' 0

    # F.8 — orders is still not an action; it is now an honestly-priced question.
    $a = Send-Cmd journal @{ types = @('action'); limit = 50 }
    $acts = @(Dig $a 'data.events')
    $ordersActs = @($acts | Where-Object { $_ -and ((Dig $_ 'verb') -eq 'orders' -or (Dig $_ 'op') -eq 'orders') })
    Check 'F.8' 'no `action` journal row for orders — it is a question, not an act' `
    ([bool]($ordersActs.Count -eq 0)) 'zero orders action rows' $ordersActs.Count
}

# ------------------------------------------------------------------- phase D --
# The disclosure is not a hedge. One `orders` call flips a bill's SCRIBED
# `paused` flag, on EITHER build — the fix does not change this and is not meant
# to. `bills` never calls ShouldDoNow (WorldSafe Class A), so any change is
# `orders`' doing.

# THE FIXTURE THE .md DOES NOT DESCRIBE, and it took four rounds to find.
#
# D needs a bill that ShouldDoNow will PAUSE, i.e. one whose product the colony
# already holds. Three things get in the way, none of them obvious:
#
#  1. The butcher recipe cannot do it. `ButcherCorpseFlesh` has no fixed product
#     (its output comes from the corpse), so RecipeWorkerCounter.CountProducts
#     returns 0 forever and `pauseWhenSatisfied && num >= targetCount` can never
#     fire. Same for `Make_StoneBlocksAny`. A stove's `CookMealSimple` has a
#     single fixed product, MealSimple, and does work.
#  2. CountProducts reads map.resourceCounter, which counts STOCKPILED things
#     only — meals dropped on bare ground count zero.
#  3. resourceCounter is rebuilt on a TICK. With the game paused for the whole
#     run (which every other phase requires) it never refreshes, so the count
#     stays 0 no matter what is spawned. The fixture has to advance the clock
#     BEFORE the bill exists, then stop it again.
#
# Order matters: warm the counter first, add the bill second. A bill that exists
# while the clock runs gets its flag flipped by an ordinary work-giver think
# tick, and then there is nothing left for `orders` to prove.
#
# THIS PHASE ADVANCES THE CLOCK. Run it after R and F, or accept that their T0
# is taken later; it never advances between a paired pair of `bills` reads.
function PhaseD {
    PhaseBanner "PHASE D - the disclosure, measured ($Build)"

    $A = Get-Actor 'D.f2'

    # (2) somewhere for the product to be stored, or resourceCounter ignores it.
    $null = Send-Cmd world-fixture @{ steps = @('stockpiles') }
    $sp = Send-Cmd dev:spawn-thing @{ def = 'MealSimple'; count = 20; stockpile = $true }
    Precondition 'D.f1' 'simple meals staged into a stockpile' ([bool](Dig $sp 'ok')) (Show (Dig $sp 'error'))
    Note 'D.f1' ("{0}" -f (Dig $sp 'data.stockpile'))

    # (3) let the resource counter rebuild, then stop the clock again.
    $adv = Send-Advance @{ ticks = 300 }
    Precondition 'D.f2b' 'the clock advanced so resourceCounter could rebuild' `
    ([bool](Dig $adv 'ok')) (Show (Dig $adv 'error'))
    Note 'D.f2b' ("tick {0} -> {1}, paused_on_exit={2}" -f `
        (([int](Dig $adv 'data.tick')) - ([int](Dig $adv 'data.ticks_elapsed'))), `
        (Dig $adv 'data.tick'), (Dig $adv 'data.paused_on_exit'))

    # (1) a stove, and only NOW the bill — so its flag is still virgin.
    $st = Send-Cmd dev:spawn-thing @{ def = 'FueledStove'; pos = @(125, 112); faction = 'player'; mode = 'near' }
    Precondition 'D.f3' 'a stove spawned' ([bool](Dig $st 'ok')) (Show (Dig $st 'error'))
    $BENCH3 = [int](Dig $st 'data.spawned.0.id')

    $bf = Send-Cmd world-fixture @{
        steps = @('bill'); bench = $BENCH3; target_count = 1; unpause_when = 0; skill_min = 0
    }
    Precondition 'D.f4' 'a target-count-1 bill on the stove' ([bool](Dig $bf 'ok')) (Show (Dig $bf 'error'))
    Note 'D.f4' ("stove #{0}, recipe {1}" -f $BENCH3, (Dig $bf 'data.bill.recipe'))

    $d1 = (Get-FirstBill $BENCH3).bill
    $paused0 = Dig $d1 'paused_stored'
    $count = Dig $d1 'current_count'
    Note 'D1' ("paused_stored={0} current_count={1}" -f (Show $paused0), (Show $count))

    if ($null -eq $count -or [int]$count -lt 1) {
        Skip 'D' 'the colony holds none of the bill product' `
            "current_count = $(Show $count) after warming the counter. ShouldDoNow only pauses a bill it considers SATISFIED. Fixture miss, not a failure — the .md says to skip with a NOTE."
        return
    }
    Check 'D1' 'the bill starts UNPAUSED (no think tick has touched it)' `
    ([bool]($paused0 -eq $false)) 'false' $paused0

    # The clock is stopped here, so nothing but this call can move the flag.
    $e = Send-Cmd orders @{ pawn = $A; thing = $BENCH3 }
    Eq 'D2' 'orders answered' $e 'ok' $true

    $d3 = (Get-FirstBill $BENCH3).bill
    Check 'D3' 'paused_stored flipped false->true — a SCRIBED field, written by a verb that called itself read-only' `
    ([bool]((Dig $d3 'paused_stored') -eq $true)) 'true' (Dig $d3 'paused_stored')
    Note 'D3' ("state is now {0}; the bills verb never calls ShouldDoNow (WorldSafe Class A), so this write is the orders verb's doing" -f (Dig $d3 'state'))
}

# ---------------------------------------------------------------------- main --

Write-Host ''
Write-Host "32b9e01 acceptance — orders/makingFor" -ForegroundColor White
Write-Host "  root     $Root"
Write-Host "  build    $Build"
Write-Host "  phases   $($Phase -join ', ')"
Write-Host "  evidence $Evidence  (tag $script:RunTag)"

Phase0
foreach ($p in $Phase) {
    switch ($p) {
        'M' { PhaseM }
        'R' { PhaseR }
        'F' { PhaseF }
        'D' { PhaseD }
    }
}

PhaseBanner 'SUMMARY'
Write-Host ("  build:   {0}" -f $Build)
Write-Host ("  checks:  {0}" -f $script:Checks)
Write-Host ("  failed:  {0}" -f $script:Fails.Count)
if ($script:Fails.Count) { Write-Host ("  failing: {0}" -f ($script:Fails -join ', ')) -ForegroundColor Red }
if ($script:Skipped.Count) { foreach ($s in $script:Skipped) { Write-Host ("  skipped: {0}" -f $s) -ForegroundColor Yellow } }
Write-Host ("  raw envelopes: {0}" -f $Evidence)
Write-Host ''
exit ([int]($script:Fails.Count -gt 0))
