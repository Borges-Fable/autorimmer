#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Acceptance runner for spec 3.5 - Dialog + interaction verbs (git-bug 20e5cda)
    and its hard dependency, the quest observer (git-bug 548ef48).

.DESCRIPTION
    The worker that wrote these may never launch RimWorld, so this is the
    executable form of both issues' Acceptance sections: numbered protocol
    envelopes with the exact expected result for each, so a mismatch is visible
    without judgement.

    IT SPEAKS THE RAW FILE PROTOCOL, not `rwa`, for the reason 3.4's runner
    already records: BORGES has no python (Store stub only), so `rwa` cannot run
    on this box and an unrunnable acceptance script is worse than none.
    PowerShell 7 is what is actually here. On a box that HAS python,
    `rwa <op> --args-json '<json>'` sends the identical envelopes.

    THIS FILE IS THE BORGES HALF OF A TWIN. `accept/3.5-dialog-verbs.py` is the
    POSIX half and is kept in step with it check for check - the check NUMBERS
    are deliberately identical, so a failure reported from one bench can be
    looked up in the other file. This script CANNOT run on the linux box: there
    is no pwsh there, and even with pwsh its `-Root` default is
    `$env:USERPROFILE\...` and every path it builds is
    `Join-Path $Root "commands\$id.json"`, which on POSIX yields a filename
    containing a literal backslash rather than a directory. Precedent for
    shipping both: `accept/3.4-pawn-orders`.

    THE FIXTURE IS NAMED, not discovered at acceptance time. 3.4's comment #4
    is explicit that a "dev-spawned trader" is not a thing that exists:
    `dev:spawn-pawn` makes a pawn, not a trade partner, and
    `TradeSession.SetupWith` Log.Warnings unless `ITrader.CanTradeNow`, which
    for a map trader needs a `LordJob_TradeWithColony`. So phase 3 stages its
    trader with `dev:incident {def:"TraderCaravanArrival"}` and then ADVANCES
    until the caravan is willing to trade. If no trader materialises inside the
    budget the phase reports a FIXTURE GAP and exits 2 - which is a staging
    problem, not a 3.5 failure, and the two are never conflated.

    Start the bench first (`_RimWorld-Agent\run-agent.ps1`), load a colony with
    at least two colonists (one Social-capable), and leave it paused.

.PARAMETER Root
    The protocol root. Defaults to the BORGES bench's -savedatafolder location.

.PARAMETER Phase
    Run only these phases (repeatable). Phase 0 always runs first.
      1  quest log         - 548ef48 acceptance, all five bullets
      2  quest accept      - 3.5 bullet 2, the red-error trap
      3  trade             - 3.5 bullet 1, buy + sell + verify + cancel
      4  letters + dialogs - 3.5 bullet 3, opaque + dismiss + the 1.7 un-wedge
      5  comms             - the headless DiaNode walker
      6  invariants        - zero red errors, clean stack, journal provenance

.EXAMPLE
    pwsh accept\3.5-dialog-verbs.ps1
    pwsh accept\3.5-dialog-verbs.ps1 -Phase 3 -Echo
    pwsh accept\3.5-dialog-verbs.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [string]$Root = "$env:USERPROFILE\misc\rimworld\_RimWorld-Agent\SaveData\AutoRimmer",
    [int[]]$Phase = @(1, 2, 3, 4, 5, 6),
    [switch]$DryRun,
    [switch]$Echo
)

$ErrorActionPreference = 'Stop'
$script:Fails = @()
$script:Checks = 0
$script:S = @{}
$script:Seq = 0

# ------------------------------------------------------------------ protocol --
# commands/<id>.json in, results/<id>.json out. The poller runs a 500 ms cycle
# and ignores inbox files younger than its MinFileAgeMs, so a round trip has a
# floor of roughly 0.25-1 s even for a trivial verb; that is the protocol, not
# this script being slow. `advance` is a DEFERRED result - its file appears only
# when the advance finishes - hence the generous per-call timeout.
#
# Ids are kept to [A-Za-z0-9-] so Poller.Sanitize leaves them alone and the
# result filename is exactly <id>.json.

function Send-Cmd {
    param(
        [Parameter(Mandatory)][string]$Op,
        [hashtable]$Args = @{},
        [int]$TimeoutSec = 300
    )
    $script:Seq++
    $slug = ($Op -replace '[^A-Za-z0-9]', '')
    if ($slug.Length -gt 16) { $slug = $slug.Substring(0, 16) }
    $id = "acc35-{0:d3}-{1}" -f $script:Seq, $slug
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
            Start-Sleep -Milliseconds 60
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
            detail = "no results\$id.json within ${TimeoutSec}s - is the bench running?" } }
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

function NotNull {
    param([string]$Num, [string]$What, $Env, [string]$Path)
    $got = Dig $Env $Path
    Check $Num "$What ($Path)" ($null -ne $got) 'present and non-null' $got
}

function Contains {
    param([string]$Num, [string]$What, $Haystack, [string]$Needle)
    $s = if ($null -eq $Haystack) { '' } else { [string]$Haystack }
    Check $Num $What ($s -like "*$Needle*") "contains '$Needle'" $Haystack
}

function Has {
    param([string]$Num, [string]$What, $Haystack, $Needle)
    $list = @($Haystack)
    Check $Num $What ([bool]($list -contains $Needle)) "contains '$Needle'" $Haystack
}

# EQ/GE TAKE (Env, Path). PASSING A COMPUTED VALUE WHERE $Env BELONGS SHIFTS
# EVERY LATER PARAMETER, and PowerShell fills the missing $Want with $null. For
# Eq that is `$null -eq $null` -> TRUE, so the check goes GREEN WHILE ASSERTING
# NOTHING; for Ge, [double]$Want defaults to 0 and Dig on a non-collection
# returns $null, so it goes RED, always. Eight call sites in this file did
# exactly that (five silently green, three spuriously red) until the audit of
# 2026-08-31. Use these two whenever the value is already in hand.
function EqVal {
    param([string]$Num, [string]$What, $Got, $Want)
    Check $Num $What ([bool](($null -eq $Want -and $null -eq $Got) -or ($Got -eq $Want))) (Show $Want) $Got
}

function GeVal {
    param([string]$Num, [string]$What, $Got, [double]$Want)
    $ok = ($Got -is [int] -or $Got -is [long] -or $Got -is [double] -or $Got -is [decimal]) -and ([double]$Got -ge $Want)
    Check $Num $What $ok ">= $Want" $Got
}

# ------------------------------------------------------------ shape contract --
# THE ROUND'S CENTRAL LESSON, and it is not a nicety. `Dig` returns $Default for
# an ABSENT key and for one that is present-and-null alike, so `Eq ... $null`
# passes either way: a driver whose dig paths are WRONG does not fail, it goes
# GREEN WHILE ASSERTING NOTHING, which is strictly worse than a loud abort
# because nobody investigates a pass. There are nine `... 'data.action.journal_seq'
# $null` assertions in this file; every one of them is backed by a real key
# today, so the hazard here is LATENT rather than live - and it is one
# serializer edit away from live.
#
# HasKey is the predicate Dig cannot be: it distinguishes absent from null.
# Phase 0 uses it to PROVE every envelope key the later phases dig on, naming
# the verb and the key, so a shape change fails there - loudly, at a check that
# says which verb moved - instead of downstream, or not at all.
function HasKey {
    param($Obj, [string]$Path)
    $parts = $Path.Split('.')
    $cur = $Obj
    for ($i = 0; $i -lt $parts.Count - 1; $i++) {
        if ($cur -is [System.Collections.IList]) {
            $n = 0
            if (-not [int]::TryParse($parts[$i], [ref]$n)) { return $false }
            if ($n -ge $cur.Count) { return $false }
            $cur = $cur[$n]
            continue
        }
        if ($cur -is [System.Collections.IDictionary]) {
            if (-not $cur.Contains($parts[$i])) { return $false }
            $cur = $cur[$parts[$i]]
            continue
        }
        return $false
    }
    return [bool](($cur -is [System.Collections.IDictionary]) -and $cur.Contains($parts[-1]))
}

function Shape {
    param([string]$Num, [string]$Verb, $Env, [string]$Path, [string]$Kind = $null)
    $ok = HasKey $Env $Path
    $want = 'the key to be PRESENT (absent != null)'
    if ($ok -and $Kind) {
        $v = Dig $Env $Path
        $ok = switch ($Kind) {
            'list' { $v -is [System.Collections.IList] }
            'map' { $v -is [System.Collections.IDictionary] }
            'number' { $v -is [int] -or $v -is [long] -or $v -is [double] -or $v -is [decimal] }
            default { $true }
        }
        $want += " and a $Kind"
    }
    Check $Num "``$Verb`` publishes $Path" ([bool]$ok) $want (Dig $Env $Path)
}

function Absent {
    param([string]$Num, [string]$What, $Env, [string]$Path)
    Check $Num $What (-not (HasKey $Env $Path)) 'the key to be ABSENT' (Dig $Env $Path)
}

function Note { param([string]$Num, [string]$Text) Write-Host ("  {0,-7} NOTE    {1}" -f $Num, $Text) -ForegroundColor DarkYellow }

function Precondition {
    param([string]$Num, [string]$What, [bool]$Ok, [string]$Detail)
    if ($DryRun) { Write-Host ("  {0,-7} NEEDS   {1}" -f $Num, $What) -ForegroundColor DarkCyan; return }
    if ($Ok) { Write-Host ("  {0,-7} OK      precondition: {1}" -f $Num, $What) -ForegroundColor DarkGreen; return }
    Write-Host ("  {0,-7} SKIP    precondition NOT MET: {1}" -f $Num, $What) -ForegroundColor Yellow
    Write-Host "          $Detail"
    Write-Host '          This is a FIXTURE gap, not a 3.5 failure. Stage it and re-run.'
    exit 2
}

function NoRedErrors { param([string]$Num, [string]$What)
    $e = Send-Cmd journal @{ since_seq = $S.seq0; types = @('red_error'); limit = 50 }
    Eq $Num $What $e 'data.count' 0
}

function StackClear { param([string]$Num, [string]$What)
    # HasKey, not `$null -eq Dig`: `status` OMITS forcePause when the stack is
    # clear (CoreVerbs.Status adds it only `if (snap.forcePause != null)`), and
    # absent is what we are asserting. Dig cannot tell absent from null.
    $e = Send-Cmd status
    Check $Num $What (-not (HasKey $e 'data.forcePause')) 'status.forcePause absent' (Dig $e 'data.forcePause')
}

function PhaseBanner { param([string]$T) Write-Host ''; Write-Host ('=' * 78); Write-Host $T; Write-Host ('=' * 78) }

# ------------------------------------------------------------------- phase 0 --

$NewOps = @(
    'quests', 'quest', 'quest-accept', 'quest-dismiss',
    'letter-read', 'letter-choose', 'letter-dismiss',
    'dialog-choose', 'dialog-dismiss',
    'trade-start', 'trade', 'trade-set', 'trade-confirm', 'trade-cancel',
    'comms-targets', 'comms-call', 'comms-choose', 'comms-hang-up'
)

function Phase0 {
    PhaseBanner 'PHASE 0 - the bench, the dev switch, and THE SHAPE CONTRACT'

    # 0.1  {"op":"status"}
    $e = Send-Cmd status
    Precondition '0.1' 'the bench answers `status`' ($DryRun -or (Dig $e 'ok') -eq $true) `
        'no status envelope - start the bench first.'
    Shape '0.1a' 'status' $e 'data.gameLoaded'
    Shape '0.1b' 'status' $e 'data.verbs' 'list'
    Eq '0.1c' 'a game is loaded' $e 'data.gameLoaded' $true
    # The whole point of this spec: a run must not START wedged either.
    Absent '0.1d' 'no force-pausing modal is up before we begin' $e 'data.forcePause'

    # 0.2  every op these two issues register must be present. The strongest
    #      check in the file: it fails on a verb that did not register at all,
    #      which no downstream assertion can distinguish from a bad fixture.
    $verbs = @(Dig $e 'data.verbs')
    $missing = @($NewOps | Where-Object { $verbs -notcontains $_ })
    Check '0.2' 'all 18 ops of 20e5cda + 548ef48 registered' ($missing.Count -eq 0) 'no missing ops' $missing

    # 0.3  THE DEV SWITCH. Phases 1-4 stage their fixtures with `dev:incident`
    #      and `journal-selftest`, and BOTH throw on !Prefs.DevMode
    #      (DevVerbs.Dev.Gate, JournalVerbs.Selftest). Without this probe a
    #      devMode-off bench fails four phases later with "no trader
    #      materialised", which reads as a spec failure and is not one.
    #      `dev:incident` with NO args is the probe: Dev.Gate(V) runs BEFORE
    #      Dev.CurrentMap and before a.StrReq("def"), so it mutates nothing
    #      either way and the two outcomes are distinguishable by message.
    $e = Send-Cmd 'dev:incident'
    $detail = "$(Dig $e 'error.detail')"
    $devOff = $detail -like '*devMode*'
    Precondition '0.3' 'devMode is ON (the fixtures need it)' (-not $devOff) `
        ("dev:incident answered: $detail  ->  Prefs.DevMode is FALSE on this bench. dev:incident and journal-selftest are both gated on it, so phases 2-5 cannot stage a quest, a trader or a dialog. Turn dev mode on in the bench's options (the agent profile seeds it True - see spike 0.1 FINDINGS) and re-run.")
    Check '0.3a' 'and the gate refused for a REASON, not silently' `
    ((Dig $e 'error.code') -eq 'bad-args') 'bad-args (the missing `def` arg)' (Dig $e 'error.code')

    # 0.4  journal: the watermark every later NoRedErrors is measured from.
    $e = Send-Cmd journal @{ limit = 1 }
    Shape '0.4a' 'journal' $e 'data.count'
    Shape '0.4b' 'journal' $e 'data.last_seq'
    Shape '0.4c' 'journal' $e 'data.events' 'list'
    $S.seq0 = [int](Dig $e 'data.last_seq' 0)
    Write-Host "          journal watermark seq0 = $($S.seq0)"

    # 0.5  interactions: 1.7's force-pause vocabulary, which phases 3-6 dig into
    #      by five different paths.
    $e = Send-Cmd interactions
    Shape '0.5a' 'interactions' $e 'data.force_pause' 'map'
    Shape '0.5b' 'interactions' $e 'data.force_pause.count' 'number'
    Shape '0.5c' 'interactions' $e 'data.blocking'
    Shape '0.5d' 'interactions' $e 'data.letters' 'list'
    Shape '0.5e' 'interactions' $e 'data.windows' 'list'
    Eq '0.5f' 'and the stack is clear before we start' $e 'data.force_pause.count' 0

    # 0.6  THE ADVANCE ENVELOPE, and the key that was wrong. 4.8g - the proof
    #      that un-wedging restored ticking, the single most load-bearing check
    #      in the suite - read `data.ticks`, which TimeDriver.BuildData does not
    #      and never did emit. Both halves are asserted here so the mistake
    #      cannot be made again silently: the real key must be PRESENT and the
    #      wrong one must be ABSENT.
    #      One tick, deliberately: it is the smallest budget that still produces
    #      a real (non-refused) advance envelope, and it is taken BEFORE phase
    #      1's double-read proof, which needs the clock still.
    $e = Send-Cmd advance @{ ticks = 1; max_tps = 400 }
    Shape '0.6a' 'advance' $e 'data.reason'
    Shape '0.6b' 'advance' $e 'data.ticks_elapsed' 'number'
    Absent '0.6c' 'advance does NOT publish `data.ticks` (4.8g used to read it)' $e 'data.ticks'

    # 0.7  the quest observer's shape, including the `action` block that nine
    #      later checks assert is present-with-a-null-seq. `Eq ... $null` alone
    #      would pass on an ABSENT action block just as happily, which is the
    #      whole reason this section exists.
    $e = Send-Cmd quests
    Shape '0.7a' 'quests' $e 'data.total' 'number'
    Shape '0.7b' 'quests' $e 'data.counts' 'map'
    Shape '0.7c' 'quests' $e 'data.counts.available'
    Shape '0.7d' 'quests' $e 'data.counts.ongoing'
    Shape '0.7e' 'quests' $e 'data.counts.ended'
    Shape '0.7f' 'quests' $e 'data.counts.dismissed'
    Shape '0.7g' 'quests' $e 'data.counts.with_outstanding_choice'
    Shape '0.7h' 'quests' $e 'data.quests' 'list'
    Shape '0.7i' 'quests' $e 'data.action' 'map'
    Shape '0.7j' 'quests' $e 'data.action.journal_seq'
    Eq '0.7k' 'and an observer stamps it null (present AND null, both proved)' $e 'data.action.journal_seq' $null

    # 0.8  the two refusal shapes phase 6 relies on, probed with NO fixture:
    #      both are the natural state of a bench that has not traded or called.
    $e = Send-Cmd trade
    Eq '0.8a' '`trade` with no session refuses at gate no-session' $e 'data.gate' 'no-session'
    Shape '0.8b' 'trade' $e 'data.gate_cite'
    $e = Send-Cmd comms-choose @{ option = 0 }
    Eq '0.8c' '`comms-choose` with no call refuses at gate no-call' $e 'data.gate' 'no-call'
    Shape '0.8d' 'comms-choose' $e 'data.gate_cite'

    # 0.9  dialog-dismiss on a clear stack: the no-op shape 4.9 asserts.
    $e = Send-Cmd dialog-dismiss
    Shape '0.9a' 'dialog-dismiss' $e 'data.ok'
    Shape '0.9b' 'dialog-dismiss' $e 'data.dismissed' 'list'
    EqVal '0.9c' 'and it dismissed nothing (data.dismissed)' (@(Dig $e 'data.dismissed')).Count 0

    # 0.10  a Social-capable colonist to negotiate with. Every trade and comms
    #      gate runs through one, and picking a pawn with Social disabled fails
    #      six checks whose only clue is a gate string.
    $e = Send-Cmd pawns @{ filter = 'colonist' }
    Shape '0.10a' 'pawns' $e 'data.list' 'list'
    $roster = @(Dig $e 'data.list')
    Precondition '0.10b' 'at least one visible colonist' ($DryRun -or $roster.Count -ge 1) `
        "the roster has $($roster.Count)."
    if ($DryRun) { $S.N = 1001; $S.Nname = '<negotiator>' }
    else {
        $able = @(); $whyNot = @()
        foreach ($r in $roster) {
            $w = Send-Cmd pawn @{ id = $r.id; sections = @('skills') }
            $skills = @(Dig $w 'data.skills.list')
            $social = $skills | Where-Object { $_.def -eq 'Social' }
            if (-not $social) { $whyNot += "$($r.name): no Social row"; continue }
            if ($social.disabled -eq $true) { $whyNot += "$($r.name): Social disabled"; continue }
            $able += $r
        }
        Precondition '0.10c' 'a colonist capable of Social (the negotiator)' ($able.Count -ge 1) `
        ("no visible colonist can negotiate. Rejected: {0}. FloatMenuOptionProvider_Trade refuses on skills.GetSkill(SkillDefOf.Social).TotallyDisabled, so trade and comms are both unreachable without one." -f $(if ($whyNot.Count) { $whyNot -join '; ' } else { 'none' }))
        $S.N = $able[0].id; $S.Nname = $able[0].name
    }
    Write-Host "          negotiator N = $($S.N) ($($S.Nname))"
}

# ------------------------------------------------------------------- phase 1 --
# 548ef48 ACCEPTANCE, all five bullets.

function Phase1 {
    PhaseBanner 'PHASE 1 - 548ef48: the quest log is readable, and reading it does not write'

    # 1.1 {"op":"quests"} - the list, with the counts rollup.
    $e = Send-Cmd quests
    Eq '1.1a' 'quests answered' $e 'ok' $true
    Eq '1.1b' 'and names itself' $e 'data.verb' 'quests'
    NotNull '1.1c' 'the state counts rollup is present' $e 'data.counts'
    NotNull '1.1d' 'available count' $e 'data.counts.available'
    NotNull '1.1e' 'ongoing count' $e 'data.counts.ongoing'
    NotNull '1.1f' 'ended count' $e 'data.counts.ended'
    # BULLET 3: dismissed is reported DISTINCTLY from declined/expired, and the
    # payload says what it is. A comment nobody reads would not satisfy this.
    NotNull '1.1g' 'BULLET 3 - dismissed is its own count, not a state' $e 'data.counts.dismissed'
    Contains '1.1h' 'BULLET 3 - and the result SAYS dismissed is not a decline' `
    (Dig $e 'data.dismissed_means') 'NOT a decline'
    Eq '1.1i' 'this call mutated nothing' $e 'data.action.journal_seq' $null

    $quests = @(Dig $e 'data.quests')
    $S.qTotal = [int](Dig $e 'data.total' 0)
    Write-Host "          $($S.qTotal) quests: available=$(Dig $e 'data.counts.available') ongoing=$(Dig $e 'data.counts.ongoing') ended=$(Dig $e 'data.counts.ended') dismissed=$(Dig $e 'data.counts.dismissed') with-choice=$(Dig $e 'data.counts.with_outstanding_choice')"

    # 1.2 BULLET 4 - THE DOUBLE-READ PROOF. 2.4 used it for research progress;
    #     it is the same proof here, and it is what says the observer does not
    #     write. `quests` twice must be IDENTICAL over every field that a
    #     write-on-read would move - id, state, expiry-derived counts, the
    #     choice count. (Ticks-since-* move with the clock, so the game must be
    #     paused for this, which phase 0 asserted.)
    $e2 = Send-Cmd quests
    $a = ($quests | ForEach-Object { "$($_.id):$($_.state):$($_.dismissed):$($_.acceptance_tick):$($_.ticks_until_expiry):$(Dig $_ 'choice.count')" }) -join '|'
    $b = (@(Dig $e2 'data.quests') | ForEach-Object { "$($_.id):$($_.state):$($_.dismissed):$($_.acceptance_tick):$($_.ticks_until_expiry):$(Dig $_ 'choice.count')" }) -join '|'
    Check '1.2a' 'BULLET 4 - reading the quest log TWICE yields an identical projection' ($a -eq $b) $a $b
    Eq '1.2b' 'and the totals match' $e2 'data.total' $S.qTotal
    Note '1.2c' 'the byte-identical-save half of bullet 4 is the ORCHESTRATOR''s to run: save, `quests`, `quest` on every id, save again, diff the <quests> region. This script cannot save without perturbing the very counters it is checking.'

    if ($S.qTotal -eq 0) {
        Note '1.3' 'no quests on this colony - staging one with dev:incident {def:"GiveQuest_Random"}'
        $stage = Send-Cmd 'dev:incident' @{ def = 'GiveQuest_Random' }
        Precondition '1.3s' 'dev:incident {def:"GiveQuest_Random"} was accepted' `
        ((Dig $stage 'ok') -eq $true) `
        ("the incident verb itself refused: code=$(Dig $stage 'error.code') detail=$(Dig $stage 'error.detail'). FIXTURE problem, not a 548ef48 failure.")
        $e = Send-Cmd quests
        $quests = @(Dig $e 'data.quests')
        $S.qTotal = [int](Dig $e 'data.total' 0)
    }
    Precondition '1.3a' 'at least one quest to drill into' ($S.qTotal -ge 1 -or $DryRun) `
        'no quest exists and dev:incident {def:"GiveQuest_Random"} did not produce one. Stage one and re-run.'

    # 1.4 BULLET 1 - each row carries state, demands, rewards and expiry.
    $q = if ($DryRun) { @{ id = 1 } } else { $quests[0] }
    $S.q = $q.id
    $e = Send-Cmd quest @{ quest = $S.q }
    Eq '1.4a' 'quest drill-down answered' $e 'ok' $true
    NotNull '1.4b' 'BULLET 1 - state' $e 'data.state'
    NotNull '1.4c' 'BULLET 1 - expiry (ticks_until_expiry; -1 means never)' $e 'data.ticks_until_expiry'
    NotNull '1.4d' 'BULLET 1 - demands (the requirements list, from QuestPart_RequirementsToAccept)' $e 'data.requirements'
    NotNull '1.4e' 'BULLET 1 - rewards' $e 'data.rewards'
    NotNull '1.4f' 'the parts list' $e 'data.parts'
    Eq '1.4g' 'and the drill-down mutated nothing' $e 'data.action.journal_seq' $null
    # The sections shape 2.2/2.4 use.
    $e = Send-Cmd quest @{ quest = $S.q; sections = @('head') }
    Eq '1.4h' 'sections select a subset' $e 'data.sections.0' 'head'
    Check '1.4i' 'and a subset omits the rest' ($null -eq (Dig $e 'data.parts')) 'data.parts absent' (Dig $e 'data.parts')
    $e = Send-Cmd quest @{ quest = $S.q; sections = @('nonsense') }
    Eq '1.4j' 'an unknown section is bad-args, never a silent empty result' $e 'error.code' 'bad-args'

    # 1.5 BULLET 2 - a quest with an outstanding QuestPart_Choice reports the
    #     choice as outstanding AND enumerates the options. This is the exact
    #     read 3.5's accept verb needs in order to choose before accepting.
    $withChoice = @($quests | Where-Object { (Dig $_ 'choice.outstanding') -eq $true })
    if ($withChoice.Count -eq 0 -and -not $DryRun) {
        Note '1.5' 'no quest on this colony has an outstanding QuestPart_Choice yet - phase 2 stages one and re-checks this bullet there (2.2)'
    }
    else {
        $cq = if ($DryRun) { @{ id = 1 } } else { $withChoice[0] }
        $S.qChoice = $cq.id
        $e = Send-Cmd quest @{ quest = $S.qChoice; sections = @('choice') }
        Eq '1.5a' 'BULLET 2 - the choice is reported OUTSTANDING' $e 'data.choice.outstanding' $true
        Ge '1.5b' 'BULLET 2 - with two or more options enumerated' $e 'data.choice.count' 2
        NotNull '1.5c' 'BULLET 2 - option 0 has an index (what quest-accept {choice:N} takes)' $e 'data.choice.options.0.index'
        NotNull '1.5d' 'BULLET 2 - and its rewards' $e 'data.choice.options.0.rewards'
        Contains '1.5e' 'and the block warns what accepting without choosing would do' `
        (Dig $e 'data.choice.note') 'red error'
    }

    # 1.6 the filters
    $e = Send-Cmd quests @{ state = 'available' }
    Eq '1.6a' 'state:available filters' $e 'data.filter.state' 'available'
    $e = Send-Cmd quests @{ state = 'nonsense' }
    Eq '1.6b' 'an unknown state is bad-args' $e 'error.code' 'bad-args'

    # 1.7 BULLET 5
    NoRedErrors '1.7' 'BULLET 5 - zero red errors across the whole quest read'
}

# ------------------------------------------------------------------- phase 2 --
# 3.5 ACCEPTANCE BULLET 2, as rewritten by the backlog verification pass:
# "a quest offer letter is listed and read; `quest accept {quest}` - choosing a
#  reward first where a QuestPart_Choice exists - makes the quest active; the
#  letter is separately dismissed; zero red errors, in particular no 'still has
#  a choice unresolved'."

function Phase2 {
    PhaseBanner 'PHASE 2 - 3.5 BULLET 2: a quest offer is read, accepted (choosing first), and its letter dismissed'

    # 2.1 stage a quest offer. dev:incident is the named fixture, per the
    #     verification pass's "name whichever it is IN the acceptance".
    $before = @(Dig (Send-Cmd quests @{ state = 'available' }) 'data.quests')
    if ($before.Count -eq 0) {
        $stage = Send-Cmd 'dev:incident' @{ def = 'GiveQuest_Random' }
        Precondition '2.1s' 'dev:incident {def:"GiveQuest_Random"} was accepted' `
        ((Dig $stage 'ok') -eq $true) `
        ("the incident verb itself refused: code=$(Dig $stage 'error.code') detail=$(Dig $stage 'error.detail'). FIXTURE problem, not a 3.5 failure.")
        $before = @(Dig (Send-Cmd quests @{ state = 'available' }) 'data.quests')
    }
    Precondition '2.1' 'an available (NotYetAccepted) quest' ($before.Count -ge 1 -or $DryRun) `
        'dev:incident {def:"GiveQuest_Random"} produced no NotYetAccepted quest. Stage one and re-run.'
    $target = if ($DryRun) { @{ id = 1; name = '<quest>' } } else { $before[0] }
    $S.qa = $target.id
    Write-Host "          target quest = $($S.qa) ($($target.name))"

    # 2.2 the letter side. `interactions` (2.4) is the list; `letter-read` is
    #     the drill-down. THE POINT OF THIS STEP is the NewQuestLetter finding:
    #     a quest offer letter has NO accept option, so the letter and the
    #     acceptance are two different acts.
    $e = Send-Cmd interactions
    Eq '2.2a' 'interactions answered' $e 'ok' $true
    $letters = @(Dig $e 'data.letters')
    $offer = @($letters | Where-Object { $_.type -eq 'NewQuestLetter' })
    if ($offer.Count -gt 0) {
        $S.letter = $offer[0].id
        $e = Send-Cmd letter-read @{ letter = $S.letter }
        Eq '2.2b' 'letter-read answered' $e 'ok' $true
        Eq '2.2c' 'it is a NewQuestLetter' $e 'data.type' 'NewQuestLetter'
        NotNull '2.2d' 'and it names the quest it is about' $e 'data.quest'
        # The finding, asserted rather than assumed: none of its options accepts.
        $labels = @(Dig $e 'data.options') | ForEach-Object { $_.label }
        Write-Host "          its options: $($labels -join ' | ')"
        Contains '2.2e' 'the result SAYS a NewQuestLetter has no accept option' `
        (Dig $e 'data.note') 'NO accept option'
        Eq '2.2f' 'and reading it mutated nothing' $e 'data.action.journal_seq' $null
    }
    else {
        Note '2.2' 'no NewQuestLetter on the stack (the offer letter may already have been reaped) - the letter half of bullet 2 is exercised in phase 4 instead'
    }

    # 2.3 THE RED-ERROR TRAP, asserted as a REFUSAL. This is the single most
    #     important check in the run: accepting with two or more choices
    #     outstanding runs QuestPart_Choice.PreQuestAccept, which Log.Errors
    #     "still has a choice unresolved" and auto-picks the first. The verb
    #     must refuse INSTEAD, and the refusal must carry the choice block.
    $e = Send-Cmd quest @{ quest = $S.qa; sections = @('choice') }
    $outstanding = (Dig $e 'data.choice.outstanding') -eq $true
    $choiceCount = [int](Dig $e 'data.choice.count' 0)
    if ($outstanding) {
        Write-Host "          this quest has $choiceCount outstanding reward choices"
        $e = Send-Cmd quest-accept @{ quest = $S.qa }
        Eq '2.3a' 'accept with a choice outstanding is REFUSED, not attempted' $e 'data.ok' $false
        Eq '2.3b' 'and names the gate' $e 'data.gate' 'choice-outstanding'
        Contains '2.3c' 'the reason quotes the red error it is preventing' `
        (Dig $e 'data.reason') 'still has a choice'
        Contains '2.3d' 'and cites the widget clause it reproduces' `
        (Dig $e 'data.gate_cite') 'DoAcceptButton'
        Ge '2.3e' 'the refusal hands back the choice options so the caller can choose' $e 'data.choice.count' 2
        Eq '2.3f' 'and nothing was journaled (the refusal mutated nothing)' $e 'data.action.journal_seq' $null
        NoRedErrors '2.3g' 'THE INVARIANT: the refused accept logged NO red error'
    }
    else {
        Note '2.3' "this quest has no outstanding QuestPart_Choice (count=$choiceCount) - the red-error trap is not exercisable on it. Re-run against a quest whose `quest {sections:['choice']}` reports outstanding:true; `quests` names one under counts.with_outstanding_choice."
    }

    # 2.4 THE ACCEPT. Choose first where a choice exists; the verb does the
    #     Choose->Accept ordering internally, which is what makes PreQuestAccept
    #     unreachable.
    $args = @{ quest = $S.qa }
    if ($outstanding) { $args.choice = 0 }
    $e = Send-Cmd quest-accept $args
    if ((Dig $e 'data.ok') -ne $true -and (Dig $e 'data.gate') -eq 'accepter-warnings') {
        Note '2.4' 'the chosen accepter would take a royal favour they are a poor fit for - the game raises a Dialog_MessageBox confirmation here and this verb refuses instead. Re-sending with confirm_accepter_warnings:true, which is the headless "Confirm".'
        $args.confirm_accepter_warnings = $true
        $e = Send-Cmd quest-accept $args
    }
    Eq '2.4a' 'quest-accept succeeded' $e 'data.ok' $true
    Check '2.4b' 'the quest is ACTIVE (state read back, not assumed)' `
    ((Dig $e 'data.state') -in @('Ongoing', 'EndedSuccess', 'EndedFailed', 'EndedUnknownOutcome')) `
        'Ongoing (or already ended)' (Dig $e 'data.state')
    Ge '2.4c' 'and it carries an acceptance tick' $e 'data.acceptance_tick' 0
    NotNull '2.4d' 'the mutation is journaled (the action row join key)' $e 'data.action.journal_seq'
    if ($outstanding) {
        Eq '2.4e' 'the reward choice was recorded' $e 'data.choice_taken' 0
        Ge '2.4f' 'choices before' $e 'data.choices_before' 2
        Eq '2.4g' 'choices after Choose - exactly one remains' $e 'data.choices_after' 1
        Contains '2.4h' 'and the note states the ordering that avoided the red error' `
        (Dig $e 'data.note') 'BEFORE Accept'
    }
    # 2.4i THE BULLET'S OWN INVARIANT, in its own words.
    $e2 = Send-Cmd journal @{ since_seq = $S.seq0; types = @('red_error'); limit = 50 }
    $unresolved = @(Dig $e2 'data.events') | Where-Object { "$($_.payload.msg)" -like '*choice unresolved*' }
    Check '2.4i' 'ZERO "still has a choice unresolved" red errors' ($unresolved.Count -eq 0) 'none' $unresolved

    # 2.5 the state read back independently, through the observer.
    $e = Send-Cmd quest @{ quest = $S.qa; sections = @('head') }
    Check '2.5a' 'the observer agrees the quest is no longer NotYetAccepted' `
    ((Dig $e 'data.state') -ne 'NotYetAccepted') 'anything but NotYetAccepted' (Dig $e 'data.state')
    Eq '2.5b' 'and Accept cleared the dismissed flag, as Quest.Accept does' $e 'data.dismissed' $false

    # 2.6 the letter is dismissed SEPARATELY - they are independent acts.
    if ($S.letter) {
        $e = Send-Cmd letter-dismiss @{ letter = $S.letter }
        Eq '2.6a' 'letter-dismiss succeeded' $e 'data.ok' $true
        Eq '2.6b' 'and the letter is gone from the stack (write read back)' $e 'data.removed' $true
        NotNull '2.6c' 'journaled' $e 'data.action.journal_seq'
        $e = Send-Cmd interactions
        $still = @(Dig $e 'data.letters') | Where-Object { $_.id -eq $S.letter }
        Check '2.6d' 'interactions no longer lists it' ($still.Count -eq 0) 'absent' $still
    }

    # 2.7 dismissed is cosmetic - the third acceptance bullet of 548ef48, acted.
    $e = Send-Cmd quest-dismiss @{ quest = $S.qa; dismissed = $true }
    if ((Dig $e 'data.ok') -eq $true) {
        Eq '2.7a' 'quest-dismiss set the flag' $e 'data.dismissed' $true
        # The widget is `selected.dismissed = !selected.dismissed` and then a
        # one-level walk over `selected.GetSubquests()`. Passing `dismissed`
        # explicitly is the SET form; omitting it toggles, as the click does.
        Eq '2.7a1' 'passing `dismissed` explicitly is the SET form' $e 'data.mode' 'set'
        Shape '2.7a2' 'quest-dismiss' $e 'data.subquests' 'list'
        Contains '2.7a3' 'and the note names the subquest propagation the widget does' `
        (Dig $e 'data.note') 'subquest'
        Contains '2.7b' 'and says plainly it is NOT a decline' (Dig $e 'data.note') 'does NOT decline'
        $e = Send-Cmd quests @{ include_dismissed = $false }
        $gone = @(Dig $e 'data.quests') | Where-Object { $_.id -eq $S.qa }
        Check '2.7c' 'include_dismissed:false filters it out (cosmetic filtering, exactly)' ($gone.Count -eq 0) 'absent' $gone
        $e = Send-Cmd quest @{ quest = $S.qa; sections = @('head') }
        Check '2.7d' 'but its STATE is untouched - dismissed is orthogonal to state' `
        ((Dig $e 'data.state') -ne 'NotYetAccepted') 'still accepted/ongoing' (Dig $e 'data.state')
        # The TOGGLE form, which is the click: no `dismissed` arg at all.
        $e = Send-Cmd quest-dismiss @{ quest = $S.qa }
        Eq '2.7e' 'omitting `dismissed` toggles, as MainTabWindow_Quests.DoDismissButton does' $e 'data.mode' 'toggle'
        Eq '2.7f' 'and the toggle undid it' $e 'data.dismissed' $false
    }
    else { Note '2.7' "quest-dismiss refused: $(Dig $e 'data.reason')" }

    NoRedErrors '2.8' 'zero red errors across the whole accept phase'
}

# ------------------------------------------------------------------- phase 3 --
# 3.5 ACCEPTANCE BULLET 1: "buy 10 meals + sell 5 shirts sight-unseen via verbs;
# silver and stock verified correct after confirm; cancel leaves state untouched."

function Phase3 {
    PhaseBanner 'PHASE 3 - 3.5 BULLET 1: a real trade, transacted against the model with no window'

    # 3.1 THE FIXTURE, NAMED. dev:spawn-pawn cannot make a trade partner:
    #     TradeSession.SetupWith Log.Warnings unless ITrader.CanTradeNow, and a
    #     map trader needs a LordJob_TradeWithColony. The incident is the route.
    $e = Send-Cmd pawns @{ filter = 'all'; cap = 200 }
    $traders = @(Dig $e 'data.list') | Where-Object { $_.trader -eq $true -or "$($_.kind)" -like '*Trader*' }
    if ($traders.Count -eq 0 -and -not $DryRun) {
        Note '3.1' 'no trader on the map - staging with dev:incident {def:"TraderCaravanArrival"} and advancing until it settles'
        # ASSERT THE STAGING WORKED. Inferring it from a trader turning up
        # later conflates "the incident was refused" with "the caravan is still
        # walking in", and the two need different answers from the operator.
        $stage = Send-Cmd 'dev:incident' @{ def = 'TraderCaravanArrival' }
        Precondition '3.1s' 'dev:incident {def:"TraderCaravanArrival"} was accepted' `
        ((Dig $stage 'ok') -eq $true) `
        ("the incident verb itself refused: code=$(Dig $stage 'error.code') detail=$(Dig $stage 'error.detail'). That is a FIXTURE problem (devMode, or the incident cannot target this map), not a 3.5 failure.")
        for ($i = 0; $i -lt 8; $i++) {
            $a = Send-Cmd advance @{ ticks = 2500; max_tps = 400 }
            if ((Dig $a 'data.reason') -eq 'dialog') {
                Note '3.1x' 'the advance HALTED on a dialog - clearing it with dialog-dismiss, which is exactly what this spec exists to do'
                Send-Cmd dialog-dismiss @{ all = $true } | Out-Null
            }
            $e = Send-Cmd pawns @{ filter = 'all'; cap = 200 }
            $traders = @(Dig $e 'data.list') | Where-Object { $_.trader -eq $true -or "$($_.kind)" -like '*Trader*' }
            if ($traders.Count -gt 0) { break }
        }
    }
    Precondition '3.1a' 'a trader pawn on the map' ($traders.Count -ge 1 -or $DryRun) `
        'dev:incident {def:"TraderCaravanArrival"} + 8 x 2500 ticks produced no trader. A caravan can take up to a day to arrive and settle; advance further, or stage an orbital trader (dev:incident {def:"OrbitalTraderArrival"}) and use the comms route in phase 5.'
    $S.trader = if ($DryRun) { 2001 } else { $traders[0].id }
    Write-Host "          trader = $($S.trader)"

    # 3.2 trade-start. The verb reproduces five widget gates and does NOT take
    #     the job - which is the decision this spec records in DESIGN.
    $e = Send-Cmd trade-start @{ trader = $S.trader; negotiator = $S.N }
    if ((Dig $e 'data.ok') -ne $true) {
        Note '3.2' "trade-start refused at gate '$(Dig $e 'data.gate')': $(Dig $e 'data.reason')"
        Precondition '3.2a' 'the trader is willing to trade now' $false `
        ("gate=$(Dig $e 'data.gate'). If it is cannot-trade-now the caravan has not settled (advance more); if no-path the negotiator is sealed off from it.")
    }
    Eq '3.2a' 'trade-start opened a session' $e 'data.ok' $true
    Eq '3.2b' 'the session is active' $e 'data.session.active' $true
    NotNull '3.2c' 'the trader is named' $e 'data.session.trader'
    # THE INVARIANT THIS SPEC IS ABOUT: a trade with no window on the stack.
    Eq '3.2d' 'ZERO force-pausing windows - the deal is transacted, not driven' $e 'data.force_pause.count' 0
    Eq '3.2e' 'and the verb says the negotiator did NOT walk' $e 'data.negotiator_walked' $false
    NotNull '3.2f' 'the scribed-thing-id cost of opening a session is DISCLOSED' $e 'data.session_cost.scribed_thing_id'
    NotNull '3.2g' 'journaled' $e 'data.action.journal_seq'
    Ge '3.2h' 'the deal has tradeables' $e 'data.tradeables_total' 1
    $S.silver0 = [int](Dig $e 'data.totals.colony_silver' 0)
    Write-Host "          colony silver at open = $($S.silver0)"

    # 3.3 THE STACK IS STILL CLEAR - asserted independently, through status.
    StackClear '3.3' 'no Dialog_Trade was stacked (status.forcePause absent)'

    # 3.4 the summary read. Find something to BUY and something to SELL.
    $e = Send-Cmd trade
    Eq '3.4a' 'trade summarised the session' $e 'data.ok' $true
    Contains '3.4b' 'and states the sign convention in the game''s own words' `
    (Dig $e 'data.sign_note') 'POSITIVE BUYS'
    $rows = @(Dig $e 'data.tradeables')
    $buyable = @($rows | Where-Object { $_.trader_has -ge 10 -and $_.trader_will_trade -eq $true -and $_.interactive -eq $true -and $_.is_currency -ne $true })
    $sellable = @($rows | Where-Object { $_.colony_has -ge 5 -and $_.trader_will_trade -eq $true -and $_.interactive -eq $true -and $_.is_currency -ne $true })
    Precondition '3.4c' 'something the trader has 10+ of, to buy' ($buyable.Count -ge 1 -or $DryRun) `
        'the trader stocks nothing in quantity 10+. The bullet says "10 meals"; any 10-stack works. Re-run against a caravan with real stock.'
    Precondition '3.4d' 'something the colony has 5+ of, to sell' ($sellable.Count -ge 1 -or $DryRun) `
        'the colony has nothing sellable in quantity 5+ within the trader''s reach. NOTE the scope: TradeDeal.AddAllTradeables walks ITrader.ColonyThingsWillingToBuy, i.e. the caravan''s trade radius, not the whole map. Move goods near the trader, or stage with dev:spawn-thing.'
    $S.buy = if ($DryRun) { @{ index = 3; thing = 'MealSimple'; trader_has = 30; colony_has = 0 } } else { $buyable[0] }
    $S.sell = if ($DryRun) { @{ index = 7; thing = 'Apparel_Shirt'; colony_has = 12; trader_has = 0 } } else { $sellable[0] }
    Write-Host "          BUY  10 x $($S.buy.thing) (index $($S.buy.index), trader has $($S.buy.trader_has))"
    Write-Host "          SELL  5 x $($S.sell.thing) (index $($S.sell.index), colony has $($S.sell.colony_has))"
    $S.buyColony0 = [int]$S.buy.colony_has
    $S.sellColony0 = [int]$S.sell.colony_has
    $S.buyTrader0 = [int]$S.buy.trader_has
    $S.sellTrader0 = [int]$S.sell.trader_has

    # 3.5 THE RED-ERROR GUARD on trade-set. Transferable.AdjustTo Log.Errors
    #     "Failed to adjust transferable counts" on an out-of-range count; the
    #     verb must refuse with the game's own overflow reason instead.
    $over = [int]$S.buy.trader_has + 9999
    $e = Send-Cmd trade-set @{ index = $S.buy.index; buy = $over }
    Eq '3.5a' 'an out-of-range buy is REFUSED, not attempted' $e 'data.ok' $false
    Eq '3.5b' 'and names the gate' $e 'data.rejected.0.gate' 'out-of-range'
    NotNull '3.5c' 'with the game''s own bounds echoed' $e 'data.rejected.0.max'
    Eq '3.5d' 'nothing was journaled for a wholly refused call' $e 'data.action.journal_seq' $null
    NoRedErrors '3.5e' 'THE INVARIANT: no "Failed to adjust transferable counts" red error'

    # 3.6 THE PLURAL FORM IS THE VERB - buy and sell in ONE call.
    $e = Send-Cmd trade-set @{ items = @(
            @{ index = $S.buy.index; buy = 10 },
            @{ index = $S.sell.index; sell = 5 }
        )
    }
    Eq '3.6a' 'trade-set accepted both lines in ONE call' $e 'data.ok' $true
    EqVal '3.6b' 'two accepted (data.accepted)' (@(Dig $e 'data.accepted')).Count 2
    EqVal '3.6c' 'none rejected (data.rejected)' (@(Dig $e 'data.rejected')).Count 0
    # POSITIVE BUYS, NEGATIVE SELLS - the game's own convention, asserted.
    Eq '3.6d' 'the buy line reads +10 and PlayerBuys' $e 'data.accepted.0.count' 10
    Eq '3.6e' 'and its action is PlayerBuys' $e 'data.accepted.0.action' 'PlayerBuys'
    Eq '3.6f' 'the sell line reads -5' $e 'data.accepted.1.count' -5
    Eq '3.6g' 'and its action is PlayerSells' $e 'data.accepted.1.action' 'PlayerSells'
    Eq '3.6h' 'two lines carry an action' $e 'data.totals.lines_with_action' 2
    NotNull '3.6i' 'journaled' $e 'data.action.journal_seq'
    StackClear '3.6j' 'still no window on the stack'

    # 3.7 CANCEL LEAVES STATE UNTOUCHED - and "untouched" is DEFINED.
    #     Defined as: no Tradeable.ResolveTrade ran; colony silver and stock
    #     counts unchanged. NOT as "the statics are pristine", because
    #     TradeSession.Close() is `trader = null;` and nothing else.
    $e = Send-Cmd trade-cancel
    Eq '3.7a' 'trade-cancel closed the session' $e 'data.ok' $true
    Eq '3.7b' 'and TradeSession.Active is now false' $e 'data.session_closed' $true
    EqVal '3.7c' 'the two staged lines are reported as abandoned (data.abandoned)' (@(Dig $e 'data.abandoned')).Count 2
    Contains '3.7d' 'and "untouched" is DEFINED in the result, not left to inference' `
    (Dig $e 'data.untouched_means') 'ResolveTrade'
    NotNull '3.7e' 'journaled' $e 'data.action.journal_seq'
    # The definition, ASSERTED: reopen and compare the counts.
    $e = Send-Cmd trade
    Eq '3.7f' 'reading a closed session is refused, not an NRE' $e 'data.ok' $false
    Eq '3.7g' 'and names the gate' $e 'data.gate' 'no-session'
    $e = Send-Cmd trade-start @{ trader = $S.trader; negotiator = $S.N }
    Eq '3.7h' 'a fresh session opens' $e 'data.ok' $true
    Eq '3.7i' 'CANCEL WAS UNTOUCHED - colony silver is exactly what it was' $e 'data.totals.colony_silver' $S.silver0
    $rows = @(Dig $e 'data.tradeables')
    $buyRow = @($rows | Where-Object { $_.thing -eq $S.buy.thing })
    $sellRow = @($rows | Where-Object { $_.thing -eq $S.sell.thing })
    if ($buyRow.Count -ge 1) { Check '3.7j' 'CANCEL WAS UNTOUCHED - trader stock unchanged' ([int]$buyRow[0].trader_has -eq $S.buyTrader0) $S.buyTrader0 $buyRow[0].trader_has }
    if ($sellRow.Count -ge 1) { Check '3.7k' 'CANCEL WAS UNTOUCHED - colony stock unchanged' ([int]$sellRow[0].colony_has -eq $S.sellColony0) $S.sellColony0 $sellRow[0].colony_has }

    # 3.8 re-stage and CONFIRM. Indexes are NOT stable across a Reset, so
    #     address by defName this time - `trade` says so in index_note.
    $e = Send-Cmd trade-set @{ items = @(
            @{ thing = $S.buy.thing; buy = 10 },
            @{ thing = $S.sell.thing; sell = 5 }
        )
    }
    if ((Dig $e 'data.ok') -ne $true) {
        Note '3.8' "defName addressing hit an ambiguity ($(Dig $e 'data.rejected.0.reason')) - falling back to the fresh indexes, which is exactly what the ambiguity message tells the caller to do"
        $e = Send-Cmd trade-set @{ items = @(
                @{ index = $buyRow[0].index; buy = 10 },
                @{ index = $sellRow[0].index; sell = 5 }
            )
        }
    }
    Eq '3.8a' 'both lines staged again' $e 'data.ok' $true
    $S.buyValue = [double](Dig $e 'data.totals.buy_value' 0)
    $S.sellValue = [double](Dig $e 'data.totals.sell_value' 0)
    $S.silverPre = [int](Dig $e 'data.totals.colony_silver' 0)
    Write-Host "          buy value $($S.buyValue), sell value $($S.sellValue), colony silver $($S.silverPre)"

    $e = Send-Cmd trade-confirm
    if ((Dig $e 'data.gate') -eq 'colony-cannot-afford') {
        # THE NRE THAT WOULD HAVE FIRED. This branch is a PASS, not a failure:
        # it proves the pre-check pre-empted TradeDeal.TryExecute's
        # WindowOfType<Dialog_Trade>().FlashSilver().
        Eq '3.8b' 'THE NRE GUARD held: cannot-afford was pre-empted, not entered' $e 'data.gate' 'colony-cannot-afford'
        Contains '3.8c' 'and the citation names the unguarded line' (Dig $e 'data.gate_cite') 'FlashSilver'
        Contains '3.8d' 'the session is still open and nothing moved' (Dig $e 'data.note') 'NOTHING was transacted'
        NoRedErrors '3.8e' 'and no NRE reached the log'
        Note '3.8f' 'the colony cannot afford 10 of that item - reducing the buy to what the silver covers and retrying'
        $e = Send-Cmd trade-set @{ items = @(@{ thing = $S.buy.thing; buy = 1 }) }
        $e = Send-Cmd trade-confirm
    }
    if ((Dig $e 'data.gate') -eq 'trader-short-funds') {
        Eq '3.8g' 'THE CONFIRMATION MODAL became an argument, not a window' $e 'data.gate' 'trader-short-funds'
        Contains '3.8h' 'and cites the Dialog_MessageBox it replaced' (Dig $e 'data.gate_cite') 'ConfirmTraderShortFunds'
        StackClear '3.8i' 'no confirmation window was stacked'
        $e = Send-Cmd trade-confirm @{ allow_trader_short_funds = $true }
    }
    Eq '3.9a' 'trade-confirm executed' $e 'data.ok' $true
    Eq '3.9b' 'and something actually moved' $e 'data.actually_traded' $true
    Eq '3.9c' 'the session was closed by the verb (TradeSession.Close has no vanilla caller)' $e 'data.session_closed' $true
    NotNull '3.9d' 'journaled' $e 'data.action.journal_seq'
    NotNull '3.9e' 'the transacted lines are echoed as evidence' $e 'data.transacted'
    NotNull '3.9f' 'and the post-trade counts are read BACK from the rebuilt deal' $e 'data.after'
    # SILVER AND STOCK VERIFIED CORRECT - the bullet's own words.
    $silverAfter = [int](Dig $e 'data.colony_silver_after' 0)
    $delta = [int](Dig $e 'data.colony_silver_delta' 0)
    Write-Host "          silver $($S.silverPre) -> $silverAfter (delta $delta); expected roughly sell($($S.sellValue)) - buy($($S.buyValue)) = $([math]::Round($S.sellValue - $S.buyValue,1))"
    Check '3.9g' 'BULLET 1 - colony silver moved, and the delta is the deal''s own arithmetic' `
    ([math]::Abs($delta - [math]::Round($S.sellValue - $S.buyValue)) -le 2) `
        "delta within 2 of $([math]::Round($S.sellValue - $S.buyValue))" $delta
    $afterBuy = @(Dig $e 'data.after') | Where-Object { $_.thing -eq $S.buy.thing }
    $afterSell = @(Dig $e 'data.after') | Where-Object { $_.thing -eq $S.sell.thing }
    if ($afterBuy.Count -ge 1) {
        Check '3.9h' 'BULLET 1 - the bought stock is now in the colony' `
        ([int]$afterBuy[0].colony_now -gt $S.buyColony0) "> $($S.buyColony0)" $afterBuy[0].colony_now
    }
    if ($afterSell.Count -ge 1) {
        Check '3.9i' 'BULLET 1 - the sold stock left the colony' `
        ([int]$afterSell[0].colony_now -lt $S.sellColony0) "< $($S.sellColony0)" $afterSell[0].colony_now
    }
    StackClear '3.9j' 'THE INVARIANT: the whole trade ran with no window on the stack'
    NoRedErrors '3.9k' 'zero red errors across the whole trade'
}

# ------------------------------------------------------------------- phase 4 --
# 3.5 ACCEPTANCE BULLET 3, plus amendment #2's real requirement: after answering,
# the run is UN-WEDGED.

function Phase4 {
    PhaseBanner 'PHASE 4 - 3.5 BULLET 3: an unknown dialog shows opaque, dismisses cleanly, and the run un-wedges'

    # 4.1 the read surface, first. `interactions` (2.4) is the list this spec's
    #     verbs address into; the three tiers are its contract.
    $e = Send-Cmd interactions
    Eq '4.1a' 'interactions answered' $e 'ok' $true
    NotNull '4.1b' 'it carries 1.7''s force-pause payload verbatim' $e 'data.force_pause'
    Eq '4.1c' 'and the stack is clear right now' $e 'data.force_pause.count' 0

    # 4.2 STAGE A FORCE-PAUSING DIALOG. 1.7 shipped the dev-gated escape hatch
    #     for exactly this, and its own comment says it is for fixtures ONLY -
    #     the real dismiss/choose verbs are 3.5's contract, which is what we are
    #     about to test. A timing-out letter opening ITSELF mid-advance is the
    #     production path; this is the deterministic stand-in.
    $e = Send-Cmd 'journal-selftest' @{ steps = @('dialogs-clear') }
    if ((Dig $e 'ok') -ne $true -and "$(Dig $e 'error.detail')" -like '*devMode*') {
        Precondition '4.2d' 'devMode for journal-selftest' $false `
            'journal-selftest is gated on Prefs.DevMode and refused. Phase 0.3 should have caught this - if it did not, the switch was turned off mid-run.'
    }
    Note '4.2' "journal-selftest {steps:['dialogs-clear']} -> $(Dig $e 'ok'). If this bench's selftest has no dialog fixture, the phase falls through to the letter route below."

    # 4.3 the production route: a real letter with real options.
    $e = Send-Cmd interactions
    $letters = @(Dig $e 'data.letters')
    $choice = @($letters | Where-Object { $_.kind -eq 'choice' -and @($_.options).Count -ge 1 })
    if ($choice.Count -eq 0) {
        Note '4.3' 'no choice letter on the stack - staging one with dev:incident {def:"VisitorGroup"} (ChoiceLetter_AcceptVisitors) and advancing'
        $stage = Send-Cmd 'dev:incident' @{ def = 'VisitorGroup' }
        Precondition '4.3s' 'dev:incident {def:"VisitorGroup"} was accepted' `
        ((Dig $stage 'ok') -eq $true) `
        ("the incident verb itself refused: code=$(Dig $stage 'error.code') detail=$(Dig $stage 'error.detail'). FIXTURE problem, not a 3.5 failure.")
        Send-Cmd advance @{ ticks = 600; max_tps = 400 } | Out-Null
        $e = Send-Cmd interactions
        $letters = @(Dig $e 'data.letters')
        $choice = @($letters | Where-Object { $_.kind -eq 'choice' -and @($_.options).Count -ge 1 })
    }
    Precondition '4.3a' 'a choice letter with at least one option' ($choice.Count -ge 1 -or $DryRun) `
        'no ChoiceLetter is on the stack. Stage one: dev:incident {def:"VisitorGroup"} or {def:"TravelerGroup"} both send ChoiceLetter_AcceptVisitors; a raid sends a plain letter, which has no options.'
    $L = if ($DryRun) { @{ id = 42; label = '<letter>'; options = @(@{ index = 0; label = 'Close'; disabled = $false }) } } else { $choice[0] }
    $S.L = $L.id
    Write-Host "          letter $($S.L): $($L.label) - options: $((@($L.options) | ForEach-Object { $_.label }) -join ' | ')"

    # 4.4 letter-read is the drill-down; its option indexes ARE 2.4's.
    $e = Send-Cmd letter-read @{ letter = $S.L }
    Eq '4.4a' 'letter-read answered' $e 'ok' $true
    Eq '4.4b' 'the index space matches interactions exactly' $e 'data.options.0.index' 0
    NotNull '4.4c' 'and the label is the literal words on the button' $e 'data.options.0.label'
    Eq '4.4d' 'read via the backing field, and it says so' $e 'data.source' 'backing-field'

    # 4.5 THE DISABLED GATE. DiaOption.Activate() does NOT check `disabled` -
    #     the UI checks it one line earlier as an argument to Widgets.ButtonText.
    #     Find a disabled option and assert the verb refuses it.
    $disabled = @($L.options) | Where-Object { $_.disabled -eq $true }
    if ($disabled.Count -ge 1) {
        $e = Send-Cmd letter-choose @{ letter = $S.L; option = $disabled[0].index }
        Eq '4.5a' 'a DISABLED option is refused' $e 'data.ok' $false
        Eq '4.5b' 'and names the gate' $e 'data.gate' 'option-disabled'
        Contains '4.5c' 'citing the Widgets.ButtonText argument that IS the gate' `
        (Dig $e 'data.gate_cite') 'ButtonText'
        Eq '4.5d' 'nothing was journaled' $e 'data.action.journal_seq' $null
    }
    else {
        Note '4.5' 'no disabled option on this letter - the disabled gate is exercised in phase 5 instead (FactionDialogMaker disables MustBeAlly / WaitTime / BadTemperature constantly)'
    }

    # 4.6 OPEN THE LETTER, which is what wedges a run. The production trigger is
    #     a timing-out letter calling OpenLetter on itself from LetterStackTick;
    #     `advance` is what drives that, so advance until something halts.
    $wedged = $false
    for ($i = 0; $i -lt 4 -and -not $wedged; $i++) {
        $a = Send-Cmd advance @{ ticks = 5000; max_tps = 400 }
        if ((Dig $a 'data.reason') -eq 'dialog') {
            $wedged = $true
            Eq '4.6a' '1.7 halted the advance on a dialog' $a 'data.reason' 'dialog'
            Ge '4.6b' 'and named the window(s)' $a 'data.halted_on.count' 1
            Write-Host "          halted on: $((@(Dig $a 'data.halted_on.windows') | ForEach-Object { $_.type }) -join ', ')"
        }
    }
    if (-not $wedged -and -not $DryRun) {
        Note '4.6' 'nothing opened a force-pausing dialog inside 20000 ticks. The remaining checks in this phase need one; a timing-out letter (ChoiceLetter_AcceptVisitors has a timeout) opens itself on its LAST tick, so a longer advance will get there. Skipping to 4.9.'
    }
    else {
        # 4.7 BULLET 3 - the window is REPORTED, with a kind, before it is answered.
        $e = Send-Cmd interactions
        Ge '4.7a' 'interactions reports the force-pausing window' $e 'data.force_pause.count' 1
        Eq '4.7b' 'and blocking is true' $e 'data.blocking' $true
        $w = @(Dig $e 'data.windows') | Where-Object { $_.force_pause -eq $true }
        Check '4.7c' 'BULLET 3 - the window carries {type, type_full, kind}' `
        ($w.Count -ge 1 -and $null -ne $w[0].type -and $null -ne $w[0].type_full -and $null -ne $w[0].kind) `
            'type + type_full + kind present' $w
        Write-Host "          window: $($w[0].type) kind=$($w[0].kind)"

        # 4.8 BULLET 3 - `dialog dismiss` must ALWAYS work, whatever the class.
        #     It is the esc-equivalent, and it must un-wedge the run.
        $e = Send-Cmd dialog-dismiss
        Eq '4.8a' 'BULLET 3 - dialog-dismiss succeeded' $e 'data.ok' $true
        Eq '4.8b' 'and TryRemove''s bool was READ, not discarded' $e 'data.dismissed.0.removed' $true
        Contains '4.8c' 'the result discloses that dismissal is NOT inert (PostClose -> closeAction)' `
        (Dig $e 'data.note') 'closeAction'
        Eq '4.8d' 'THE UN-WEDGE: force_pause is now zero' $e 'data.force_pause.count' 0
        NotNull '4.8e' 'journaled' $e 'data.action.journal_seq'

        # THE ACCEPTANCE LINE FROM AMENDMENT #2, in its own words: after the
        # dialog is answered, the next advance RUNS ITS FULL BUDGET.
        $a = Send-Cmd advance @{ ticks = 500; max_tps = 400 }
        Eq '4.8f' 'THE POINT OF THIS SPEC: the next advance runs its FULL BUDGET' $a 'data.reason' 'ticks'
        # THE KEY IS `ticks_elapsed`. TimeDriver.BuildData emits
        # `["ticks_elapsed"] = ticks` and has never emitted `ticks`, so the
        # original `data.ticks` here could only ever fail - on the single most
        # load-bearing check in the suite, the proof that un-wedging restored
        # ticking. Phase 0 now asserts both halves of that (0.5b/0.5c).
        Ge '4.8g' 'and did all 500 ticks, not 0' $a 'data.ticks_elapsed' 500
    }

    # 4.9 dialog-dismiss with nothing up is a clean no-op, not an error.
    $e = Send-Cmd dialog-dismiss
    Eq '4.9a' 'dismiss with a clear stack is ok:true, not an error' $e 'data.ok' $true
    EqVal '4.9b' 'with nothing dismissed (data.dismissed)' (@(Dig $e 'data.dismissed')).Count 0
    Eq '4.9c' 'and nothing journaled' $e 'data.action.journal_seq' $null

    # 4.10 answering a letter from the letter side, and the honest report about
    #      what that does and does not clear.
    $e = Send-Cmd interactions
    $choice = @(Dig $e 'data.letters') | Where-Object { $_.kind -eq 'choice' -and @($_.options).Count -ge 1 }
    if ($choice.Count -ge 1) {
        $l2 = $choice[0]
        $enabled = @($l2.options) | Where-Object { $_.disabled -ne $true }
        if ($enabled.Count -ge 1) {
            # Address BY LABEL - "options by label or index, exactly as 2.4
            # reports them" is the spec's own contract.
            $e = Send-Cmd letter-choose @{ letter = $l2.id; option_label = $enabled[0].label }
            Eq '4.10a' 'letter-choose by LABEL succeeded' $e 'data.ok' $true
            Eq '4.10b' 'the letter is gone' $e 'data.letter_removed' $true
            NotNull '4.10c' 'the route taken is reported (open-window vs letter-choices)' $e 'data.via'
            Write-Host "          via = $(Dig $e 'data.via')"
            NotNull '4.10d' 'journaled' $e 'data.action.journal_seq'
            Eq '4.10e' 'and the stack is clear after' $e 'data.force_pause.count' 0
        }
    }
    else { Note '4.10' 'no choice letter left to answer from the letter side' }

    # 4.11 the three letter classes that BREAK the options-by-label contract are
    #      refused BY NAME rather than crashing.
    $e = Send-Cmd letter-choose @{ letter = 999999 }
    Eq '4.11a' 'an unknown letter id is bad-args with a helpful message' $e 'error.code' 'bad-args'
    Contains '4.11b' 'and explains that a timed-out letter is reaped' (Dig $e 'error.detail') 'LetterStackUpdate'

    StackClear '4.12a' 'the stack is clear at the end of the phase'
    NoRedErrors '4.12b' 'zero red errors across the whole dialog phase'
}

# ------------------------------------------------------------------- phase 5 --
# The generic DiaNode walker, on a real faction tree, with no window.

function Phase5 {
    PhaseBanner 'PHASE 5 - comms: the DiaNode walker runs headless, and the disabled gate holds'

    $e = Send-Cmd comms-targets @{ negotiator = $S.N }
    if ((Dig $e 'data.ok') -ne $true -and (Dig $e 'data.gate') -eq 'no-console') {
        Precondition '5.1' 'a comms console' $false `
        "$(Dig $e 'data.reason') Build one and re-run, or skip phase 5."
    }
    Eq '5.1a' 'comms-targets answered' $e 'ok' $true
    NotNull '5.1b' 'the console is identified' $e 'data.console.id'
    $targets = @(Dig $e 'data.targets')
    GeVal '5.1c' 'at least one comm target (data.targets)' $targets.Count 1
    $callable = @($targets | Where-Object { $_.callable -eq $true -and $_.kind -eq 'faction' })
    Write-Host "          $($targets.Count) targets, $($callable.Count) callable factions"
    foreach ($t in $targets) { if ($t.callable -ne $true) { Write-Host "          blocked: $($t.name) - $($t.blocked)" } }
    Eq '5.1d' 'the read mutated nothing' $e 'data.action.journal_seq' $null

    Precondition '5.2' 'a callable faction' ($callable.Count -ge 1 -or $DryRun) `
        'every faction is blocked - most likely LeaderUnavailableNoLeader, which is exactly the gate that keeps FactionDialogMaker.FactionDialogFor''s "Faction ... has no leader" Log.Error unreachable. Not a 3.5 failure.'
    $F = if ($DryRun) { @{ name = '<faction>' } } else { $callable[0] }

    $e = Send-Cmd comms-call @{ target = $F.name; negotiator = $S.N }
    Eq '5.3a' 'comms-call opened a headless node tree' $e 'data.ok' $true
    Eq '5.3b' 'kind is node-tree' $e 'data.kind' 'node-tree'
    # THE INVARIANT: Faction.TryOpenComms would have stacked a Dialog_Negotiation,
    # which is a Dialog_NodeTree and therefore forcePause.
    Eq '5.3c' 'ZERO force-pausing windows - no Dialog_Negotiation was stacked' $e 'data.force_pause.count' 0
    Eq '5.3d' 'the negotiator did not walk, and the verb says so' $e 'data.negotiator_walked' $false
    GeVal '5.3e' 'the root node has options (data.node.options)' (@(Dig $e 'data.node.options')).Count 1
    NotNull '5.3f' 'journaled' $e 'data.action.journal_seq'
    $opts = @(Dig $e 'data.node.options')
    Write-Host "          options: $(($opts | ForEach-Object { $_.label + $(if($_.disabled){' [DISABLED: '+$_.disabled_reason+']'}) }) -join ' | ')"

    # 5.4 THE DISABLED GATE, on a tree that uses it heavily. FactionDialogMaker
    #     disables MustBeAlly / BadTemperature / WaitTime / WorkTypeDisablesOption.
    $dis = @($opts | Where-Object { $_.disabled -eq $true })
    if ($dis.Count -ge 1) {
        $e = Send-Cmd comms-choose @{ option = $dis[0].index }
        Eq '5.4a' 'a DISABLED comms option is refused' $e 'data.ok' $false
        Eq '5.4b' 'naming the gate' $e 'data.gate' 'option-disabled'
        Contains '5.4c' 'and citing the Widgets.ButtonText argument' (Dig $e 'data.gate_cite') 'ButtonText'
        Check '5.4d' 'with the game''s OWN disabled reason surfaced' `
        ($null -ne (Dig $e 'data.disabled_reason')) 'present' (Dig $e 'data.disabled_reason')
        Eq '5.4e' 'nothing journaled' $e 'data.action.journal_seq' $null
    }
    else { Note '5.4' 'no disabled option on this faction tree (an ally with everything off cooldown) - the gate is not exercisable here' }

    # 5.5 walk one enabled option that OPENS A NODE rather than resolving.
    $link = @($opts | Where-Object { $_.disabled -ne $true -and $_.opens_node -eq $true })
    if ($link.Count -ge 1) {
        $e = Send-Cmd comms-choose @{ option = $link[0].index }
        Eq '5.5a' 'comms-choose walked to the linked node' $e 'data.ok' $true
        Eq '5.5b' 'and reports it went to a node' $e 'data.went_to_node' $true
        GeVal '5.5c' 'the new node has its own options (data.now_showing.options)' (@(Dig $e 'data.now_showing.options')).Count 1
        Eq '5.5d' 'still no window' $e 'data.force_pause.count' 0
        Write-Host "          now showing: $((@(Dig $e 'data.now_showing.options') | ForEach-Object { $_.label }) -join ' | ')"
    }
    else { Note '5.5' 'no enabled linking option on this tree' }

    # 5.6 hang up. The model equivalent of the always-appended "(Disconnect)".
    $e = Send-Cmd comms-hang-up
    Eq '5.6a' 'comms-hang-up ended the call' $e 'data.ok' $true
    Contains '5.6b' 'and says no window was removed, because the call was headless' `
    (Dig $e 'data.note') 'no window was removed'
    $e = Send-Cmd comms-choose @{ option = 0 }
    Eq '5.6c' 'choosing with no call open is refused, not an NRE' $e 'data.ok' $false
    Eq '5.6d' 'naming the gate' $e 'data.gate' 'no-call'

    StackClear '5.7a' 'the stack is clear at the end of the phase'
    NoRedErrors '5.7b' 'zero red errors across the whole comms phase'
}

# ------------------------------------------------------------------- phase 6 --

function Phase6 {
    PhaseBanner 'PHASE 6 - the whole run''s standing invariants'

    # 6.1 THE invariant.
    NoRedErrors '6.1' 'ZERO red errors across the WHOLE run'

    # 6.2 no force-pausing modal left behind, which is 1.7's and this spec's
    #     shared contract.
    StackClear '6.2a' 'no force-pausing modal was left behind'
    $e = Send-Cmd interactions
    Eq '6.2b' 'and interactions agrees' $e 'data.force_pause.count' 0
    Eq '6.2c' 'blocking is false' $e 'data.blocking' $false

    # 6.3 no trade session and no comms call was left dangling. TradeSession is
    #     STATIC and TradeSession.Close() has no vanilla caller, so a leaked
    #     session would poison every later verb (Tradeable's price getters
    #     dereference TradeSession.trader).
    $e = Send-Cmd trade
    Eq '6.3a' 'no trade session is left open' $e 'data.gate' 'no-session'
    $e = Send-Cmd comms-choose @{ option = 0 }
    Eq '6.3b' 'no comms call is left open' $e 'data.gate' 'no-call'

    # 6.4 PROVENANCE. Every player mutation this run made landed as an `action`
    #     row, matching the shipped verbs' shape.
    $e = Send-Cmd journal @{ since_seq = $S.seq0; types = @('action'); limit = 200 }
    Ge '6.4a' 'the run wrote action rows' $e 'data.count' 1
    $verbs = @(Dig $e 'data.events') | ForEach-Object { $_.payload.verb } | Sort-Object -Unique
    Write-Host "          action verbs journaled: $($verbs -join ', ')"
    $rows = @(Dig $e 'data.events')
    $shaped = @($rows | Where-Object { $null -ne $_.payload.verb -and $null -ne $_.payload.step })
    Check '6.4b' 'every action row carries {verb, step} - 3.4''s shape, not a second one' `
    ($shaped.Count -eq $rows.Count) "all $($rows.Count) rows" "$($shaped.Count) of $($rows.Count)"

    # 6.5 and no `dev` row was written by a PLAYER verb. The dev/player split is
    #     the action model's first line; a player verb that journaled as `dev`
    #     would be claiming a cheat it did not commit.
    $e = Send-Cmd journal @{ since_seq = $S.seq0; types = @('dev'); limit = 200 }
    $devVerbs = @(Dig $e 'data.events') | ForEach-Object { $_.payload.verb } | Sort-Object -Unique
    $leaked = @($devVerbs | Where-Object { $_ -in $NewOps })
    Check '6.5' 'no 3.5 verb journaled itself as a `dev` cheat row' ($leaked.Count -eq 0) 'none' $leaked
}

# ---------------------------------------------------------------------- main --

Write-Host 'AutoRimmer spec 3.5 acceptance - dialog + interaction verbs (git-bug 20e5cda)'
Write-Host '                             + the quest observer         (git-bug 548ef48)'
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
