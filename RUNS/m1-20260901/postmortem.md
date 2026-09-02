# Post-mortem — run `m1-20260901`

**Full pass, not the light one.** The trigger is `postmortem-trigger`'s
near-miss clause, fired twice: Wouter went down at tick 244,343 with a bleeding
neck bite, and again at 268,000-odd with a wound infection at severity 0.93 — a
hair off the 1.0 that kills. Nobody died. The run reached day 20 with three of
three colonists alive and a **SURVIVED** verdict, and it is still running under a
superseding goal. This document exists because two of the four causes below are
mine and would repeat.

## Inputs and what they can bear

| input | what it can answer |
|---|---|
| `journal/<sid>.ndjson`, sid `20260902T002505`, 900+ rows | every `death`, `downed`, `mental_break`, `dialog`, `letter`, `action`, `alert_on/off` with ticks. **Zero `red_error`, zero `dev`.** |
| `digests/day-1.json`, `day-10..25.json`, `final.json` | state at each boundary — **days 2–9 are missing and that is a compliance finding, not a gap in the game** |
| `history-final.json`, 41 samples × 11 recorders | the only trend source. `trends` was useless here: `ready:null`, 24 points over 57,500 ticks, every slope null |
| `checklist.ndjson`, 17 rows | day 1 only. The ledger stops there, which is finding C5 |
| `saves/day-17..25.rws` | nearest-autosave copies. Not moments — see `bb931b9` |

## 1. Timeline of harm

| tick | day | event | payload |
|---|---|---|---|
| 204,000 | 3 | `mental_break` + `letter` ThreatSmall | **Mad rat** — a manhunter animal, the same class that killed M1's Captain |
| 215,074 | 3 | `alert_on` | `Alert_ColonistNeedsTend` — Wouter bitten, fleeing, **at (52,249)**, ~150 cells from base |
| 217,651 | 3 | `letter` **ThreatBig** | *Ancient danger* — fired because the fleeing Wouter passed an ancient wall |
| 221,238 | 3 | `death` | Rat — `player:false, kind:"animal"` |
| 244,343 | 4 | **`downed` Wouter** | `player:true, kind:"colonist"`. Blood loss 0.685 |
| 250,757 | 4 | `letter` | *Disease: Infection* — the neck bite went septic |
| ~255,000 | 4 | `dialog` | `Dialog_NamePlayerFactionAndSettlement`, re-raising every 1,000 ticks |
| ~268,000 | 4 | (read) | **WoundInfection severity 0.93**, `tend_quality` 0.38 — the closest call of the run |
| 503,376 / 514,806 | 8 | `death` ×2 | Rat, Squirrel — animals |
| 504,407 | 8 | `downed` | **Donkey**, `damage:"Burn"` — the wildfire |
| 517,868–523,253 | 8 | `alert_on/off` | `Alert_FireInHomeArea` — 109 fires burning, front at z≤100, base at z116+ |
| 1,151,979 | 19 | `mental_break` | Buck — animal |

No colonist ever died. Every `death` row in the entire run is `player:false,
kind:"animal"`: Bluebird, Hare, Rat, Squirrel, Rat.

## 2. The backward walk

### H1 — Wouter downed at 244,343, infection to 0.93

**What state made it possible.** He is the only pawn on the roster with
Shooting *and* Melee disabled, so `posture` cannot give him anything but
`Flee` — the verb said so by name: *"the game does not offer Attack to a pawn
incapable of violence (`HostilityResponseModeUtility.DrawResponseButton_GenerateMenu`
omits it)"*. A single manhunter rat therefore drove the colony's only doctor
150 cells into open ground, and `Flee` is decided in the `HumanlikeConstant`
tree above seek, so nothing the colony could set would have changed it.

**What signal existed, and when.** Three, and the ordering is the finding:

1. `Alert_ColonistNeedsTend` went ON at 215,074 — a **genuine leading signal**,
   29,269 ticks before the downing. It was read at the halt.
2. `advance` **halted on `reason:"casualty"`** naming Wouter at the downing.
   That is the mod doing exactly what `722c951` shipped it to do.
3. `triage` answered the whole question in one call: bleed clock 106,057 ticks,
   two rescuers pathed, `verdict:"in-time"`, `margin_ticks: 104,962`, and an
   `act` block with both ids filled in.

**What the loop did.** For (1), correctly: `move-to` home, `draft` Jimmy to
intercept, `undraft` when `threats.hostiles` hit 0. For (2) — **it advanced
twice more before reading the halt.** For (3), correctly: sent `triage`'s `act`
verbatim; Jimmy carried him to his own bed.

**Root cause: `execution-slip`, twice over.** The playbook was right, the mod
interrupted me, and I rode past it. `casualty-halt` says *"Only then advance
again"* and I did not. The margin that saved him was 104,962 ticks of
arithmetic, not judgement.

### H2 — nobody tended him for ~8,000 ticks after the rescue

**What state made it possible.** I had set **Lacey — the only available doctor —
to Mining priority 1** four in-game days earlier, to break a steel bottleneck.
`work_coverage.ok` stayed `true` the whole time, because coverage measures
*enabled and available*, not *out-competed*. Doctor sat at 2 behind Mining at 1
and lost every think tick.

**What signal existed.** None. `work_coverage` cannot see this: its floor test
is availability, and Lacey was available. The infection climbed 0.13 → 0.93
while the digest reported healthy coverage.

**Root cause: `no-signal`, caused by `policy-gap`.** The policy hole is mine —
I made the doctor the miner. But the reason it was invisible for four days is a
genuine no-signal: **nothing compares a work type's floor against its
*priority rank*.** `one-doctor-is-zero-doctors` graduated to code as an
availability check; this is the same lesson one level down.

### H3 — the run stopped for 1,000 ticks at a time, indefinitely

`Dialog_NamePlayerFactionAndSettlement`. `dialog-dismiss` reported
`removed: true` and the game re-raised it on a periodic check. Three consecutive
turns, 1,000 ticks each. **Root cause: `verb-gap`** — filed as `5cb1f9f` (p0),
with the colony-start half on `d2e1229`. Evan answered the dialog by hand.

### H4 — the freezer never finished, and T1(d) failed because of it

**What state made it possible.** `resources.steel` read **0 for five consecutive
in-game days** while `things {def:"Steel"}` reported **811 on the map**: 308
forbidden, the rest outside the colonists' `Area_Allowed`. I read the 0 and
designated more mining — four separate times.

**What signal existed.** A perfect one, and I had already been shown it.
`place-layout --dry-run` published, on day 1, for the barracks:

    "short_by":6,"available":204,"in_stockpiles":0,"forbidden":966,
    "hint":"there is enough of this on the map, but 966 of it is FORBIDDEN —
            `unforbid` is the fix, not mining"

I acted on that hint once, correctly, and then spent five days ignoring the same
instrument. **Root cause: `mistrusted-signal`** — `resources.*` is
stockpiles-only and says so in its own `scope` field, and I treated it as a
supply figure. Landed as the playbook lesson
[[stockpile-scope-hides-your-own-supplies]], cited from `turn.md`'s
`materials-designation-loop`.

### N1 — the trader (no casualty, real cost)

Day 14, inside a 3-day batch: *"Bulk goods trader from East Galer"* printed,
dismissed as a `PositiveEvent`, and the loop advanced ~45,000 ticks. 800 silver
unspent, steel 0, `food_days` 3.9 against a required 6. A bulk goods trader
sells exactly steel and food. **Root cause: `policy-gap`** — the batch broke on
threats and casualties and nothing else. Landed as
[[batching-turns-costs-you-the-trader]] plus a new `triggered.md` item,
`trade-opportunity`, and the retire-when names the mod rung: a trade halt
matcher, because the branch is computable.

## 3. Root-cause classification

| # | cause | class | evidence |
|---|---|---|---|
| C1 | Two advances issued after a `casualty` halt named Wouter, before it was read | **execution-slip** | `.day 3` batch, turns 3–4 after `reason:"casualty"` |
| C2 | The only available doctor was Mining 1 while a colonist bled with a 0.93 infection; `work_coverage.ok` stayed true | **no-signal** | `work-priorities` Lacey Mining 3→1; `tend_quality` 0.38; coverage `ok:true` throughout |
| C3 | `resources.steel` 0 read as a supply problem for five days; 811 steel on the map | **mistrusted-signal** | `things` total 811 / `forbidden` 308; four redundant mine designations |
| C4 | A trade letter dismissed inside a batch; caravan gone by the next read | **policy-gap** | day-14 log line; `by_class` shows no faction pawns after |
| C5 | Days 2–9 have no digest snapshot and no `daily.md` ledger rows | **execution-slip** | `digests/` jumps day-1 → day-10; `checklist.ndjson` stops at 17 rows |
| C6 | `Alert_ChessTableNoChairs` active and undecided for 13 in-game days | **no-signal** | present in every snapshot day 10→23; neither muted nor acted |
| C7 | Build preflight ignores `constructionSkillPrerequisite` and reports the skill gate as a material shortfall | **verb-gap** | `e08c3e5` |
| C8 | `dialog-dismiss` cannot answer a text-entry dialog | **verb-gap** | `5cb1f9f` |
| C9 | Four forced orders silently displaced each other, all `ok:true` | **verb-gap** | `253c694` |
| C10 | `owners_total` reads 0 against a populated `beds[].owners` — **T6's own grading field** | **verb-gap** | `daa269a` |
| C11 | Mine designations cannot be aimed; `map-view`'s `%` collapses ore into rock | **verb-gap** | `855117a` |
| C12 | No save verb; an unattended run cannot checkpoint before a raid | **verb-gap** | `bb931b9` |

**Deliberately not claimed as causes.** The wildfire (109 fires, front 16 cells
south) never reached the base and rain ended it — luck, and recorded as luck.
G5 going unexercised is not a failure of the colony: `history` `ThreatPoints`
ran 3.5 → 4.18 (stored ÷10, so 35 → 42 real points) and Cassandra never had the
points to spend.

## 4. The wealth check

Mandatory, because two preventions add wealth. Read, not argued
(`history-final.json`, 41 samples):

| series | first | last |
|---|---|---|
| `Wealth_Total` | 13,843.7 | 18,584.2 |
| `Wealth_Buildings` | 2,168.6 | 6,973.1 |
| `Wealth_Items` | 9,130.3 | 9,096.4 |
| `ThreatPoints` (×10 = real) | 3.5 (35) | 4.18 (42) |

Wealth rose 34% and threat points rose 20% — a real but shallow coupling at
this scale, and 42 points is below a single raid's floor. **So the
wealth-damping argument did not bind on this run**, and one decision I made on
its strength was wrong: I set Jimmy's `Art` to priority 4 reasoning that
sculptures are wealth without defence. Evan corrected it — *"useful for art and
stuff, which you haven't placed"* — and the numbers agree with him. At 42 threat
points, beauty was free and I declined it while Wouter sat at
`Unsightly environment −5` and `Awful barracks −7`. **The damping term is real
and it is not a licence to skip mood.** Recorded here so the next run reads the
threat-points column before invoking it.

## 5. Outputs

| # | output | rung | covers |
|---|---|---|---|
| OUT-1 | [[stockpile-scope-hides-your-own-supplies]] + `turn.md` §materials-designation-loop and §meds-floor citations | **playbook, landed** | C3 |
| OUT-2 | [[batching-turns-costs-you-the-trader]] + `triggered.md` §trade-opportunity | **playbook + checklist, landed** | C4 |
| OUT-3 | `turn.md` trip-wire **alert-undecided** — flag any `alerts.active` id neither muted nor named in a ledger `action` within 3 day-boundary reads | **checklist, proposed on `d2e1229`** | C6 |
| OUT-4 | A work-coverage read that compares a floor against **priority rank**, not just availability — "the doctor exists and is outranked" | **mod rung (deterministic)**, needs a spec issue | C2 |
| OUT-5 | Six spec issues, all filed with Acceptance sections: `253c694`, `855117a`, `e08c3e5`, `daa269a`, `5cb1f9f`, `bb931b9` | **mod** | C7–C12 |
| OUT-6 | Compliance findings, recorded not artifacted | **4.2 log** | C1, C5 |

OUT-4 is the one that is *not yet filed* and should be, by the determinism rule
in step 5: every branch is computable from state the observers already publish
(`work_coverage.rows[].available_pawns` × each pawn's `work.row` priority).

## Compliance findings (4.2) — C1, C5

- **C1.** Two advances after a `casualty` halt, before the halt was read. The
  invariant is `casualty-halt`'s *"Only then advance again."* No artifact is
  owed; the item exists and was not followed.
- **C5.** `digests/day-2..9.json` absent and `checklist.ndjson` frozen at 17
  day-1 rows. `checklists/README.md` couples the two — a snapshot with no daily
  lines behind it is a failure, and so is the reverse. The cause is mechanical:
  I drove the first nine days off letter-halts and only built a
  day-boundary-aware driver on day 10. `accept/4.2-play-loop.py` should fail this
  run on both, and **T7 is graded FAIL on evidence because of exactly this** —
  the posture was right from day 2 and I cannot show eight of the snapshots that
  would prove it.

## What the record cannot answer

Whether the colony would have survived a raid. It never met one. Every defensive
conclusion in this document — posture, armament, the barricade line, flak on the
rifleman — is untested, and `history`'s `ThreatPoints` column says the
storyteller was never in a position to test it.

---

# Addendum — the colony died. Days 32–66.

The run continued past the graded 20 days under Evan's superseding goal and
**ended in a total wipe at tick 3,948,132**. This addendum records what killed
it, because the shape is not the one the M1 contract was built to catch.

## The colony was never killed by a threat

`history` closing window: `ThreatPoints` peaked at **6.86** (stored ÷10, so ~69
real points). One raid in 66 days. The gate row G5 spent the whole graded run
"not exercised" and, when the raid finally came, it took one colonist by
**kidnap**, not by kill.

What killed six colonists was **food that existed and could not be reached**,
three times over, by three unrelated mechanisms:

| # | mechanism | how it presented | cost |
|---|---|---|---|
| F1 | corpses and meals **forbidden** | `food_days: 0` with meat on the ground | near-miss ×3 |
| F2 | targets **outside the `Area_Allowed`** | `designate hunt` → `accepted: 6`, nothing happens, ever | **Marco** |
| F3 | the butcher bill **matched no ingredient** | `filter: null`, `suspended: false`, a corpse on the spot's own cell, `blocked: Missing 1x corpses` | **Lacey, Trev, Kepler** |

F3 is the one that ended it. `bill-set {allow:["Corpses"]}` reported success and
added 39 defs; the filter read back `null` on the next call — the
`Bill.ExposeData` narrowing the verb's own note warns about. With `consume`
answering **`cannot-eat: this pawn cannot ever eat this`** for raw carcasses,
roughly 600 meat of corpses was permanently inedible and the colony starved
surrounded by it.

**Root-cause class: `verb-gap`.** Not policy, not execution. Three colonists
died to a bill-filter defect with no protocol workaround.

## My own errors, separated from the defects

Two, and both cost lives:

- **I sent Lacey at the creepjoiner at 56% health.** I read its def — 55–80 years
  old, blind, a *poor* knife — and concluded she would win, ignoring that her 56%
  was raid damage over seven untended wounds. She was down in 980 ticks, and she
  was the second doctor. Wouter then fought it bare-handed because his hostility
  response is `Flee` and fleeing put him in reach. Two deaths follow directly.
- **I stalled the clock twice**, once by running duplicate drivers (each clearing
  the journal watermark while the other's advance journaled more, so every
  advance was refused `unread-journal`), once by editing a shell script *while it
  was executing*. The game sat frozen ~20 minutes across the two.

## The four instances of one defect

`Alert`/letter handling ate, in order: **a bulk-goods trader** (800 silver
unspent, steel and food short), **two expiring quests**, **Serenity's own joiner
letter** (rejected a free colonist), and finally — on the closing letter stack —
**`Quest failed: Breaking Jimmy Out`**. The rescue the kidnap letter promised did
arrive and expired unread inside a batched driver turn. Jimmy could have come
home. That is the strongest possible argument for the generalised lesson
[[batching-turns-costs-you-the-trader]]: the predicate is not the letter's
subject, it is whether the letter carries a choice.

## Why the run could not be restarted

The `GameEnded` letter offers **"Create new wanderers"**. Taking it opens
`Dialog_ChooseNewWanderers`, which `dialog-choose` cannot address (not a node
tree) and `dialog-dismiss` *cancels*. Filed as a second, terminal instance on
`5cb1f9f`. Per Evan's instruction the game was closed.

## Outputs owed by this addendum

| # | output | rung | covers |
|---|---|---|---|
| A-1 | [[a-designation-outside-the-allowed-area-does-nothing]] — **landed**, indexed Critical | playbook | F2 |
| A-2 | `5cb1f9f` comment: `Dialog_ChooseNewWanderers` is terminal, and `dialog-dismiss` destroys the offer | mod | the ending |
| A-3 | **A spec issue is owed for F3** — `bill-add` produces a butcher bill matching nothing and `bill-set` does not persist a filter. This killed three colonists and has no workaround | mod | F3 |
| A-4 | `PLAY-LOOP.md` needs a post-wipe branch: a wipe is not necessarily the end of a run, and `GameEnded` carries a recovery | checklist | the ending |
