# Post-mortem — run `m1-20260831`

Two colonist deaths (Table, tick 225,934; Captain, tick 241,007), both
**blood loss, untended**. Run stopped at Evan's call on day 6 of a planned
ten. `postmortem.md`'s procedure, run in full for the first time.

Written on branch `s13-postmortem` as the run's only new file. **Step 5's
outputs are NAMED here, not landed** — the orchestrator lands them.

## Inputs and what they can bear

| input | what it is | caveat |
|---|---|---|
| journal `20260901T022324.ndjson` | 175 events, 0 `red_error`, 0 `dialog` | ticks are exact for `death`/`downed`/`letter`/`message`; `alert_on`/`alert_off` are **noticed**, not happened |
| `RUNS/m1-20260831/checklist.ndjson` | 53 ledger lines, days 1–5 | 3 of the 30 item names in it (`postmortem-trigger`, `time-control-drift`, `barracks-heat`) are defined in no checklist file |
| `digests/day-1..5.json`, `final.json` | 6 digests | **single points.** Colony sampling (git-bug `2d9a1da`) has NOT landed; every digest number below is one reading, not a series |
| `transcripts/m1-20260831/` | 195 ops with `cmd.json`/`result.json` each | the authority on what was actually read |
| `saves/Autosave-1..5.rws` | the game's own autosaves | Autosave-5 is tick 300,000 |

**Alert lag.** `JOURNAL.md` §Alert timing: up to 24 frames of readout
round-robin plus 30 frames of scan cadence = 54 frames, and at the ~33
ticks/frame a budgeted advance delivers that is **800–2,000 ticks**. Every
`alert_on` tick below is a ceiling on when the state changed, never the
state change itself. Where that matters it is said in place.

**One series is NOT a single point, and it is not sampling either.** The
wealth and threat-point numbers in §4 are the game's own `HistoryAutoRecorder`
arrays, base64-decoded out of `saves/Autosave-5.rws` after the fact
(`Wealth_Total`, `Wealth_Items`, `Wealth_Buildings`, `Wealth_Pawns`,
`FreeColonists`, `ThreatPoints`, all `recordTicksFrequency` 30,000 —
`Data/Core/Defs/Misc/HistoryAutoRecording/HistoryAutoRecorders.xml`; 11
samples at ticks 0, 30,000 … 300,000). The loop could not see them while
playing. That is exactly the gap `2d9a1da` exists to close.

---

## 1. Timeline of harm

Every `death`, `downed`, `mental_break`, `red_error` and `dialog` in the
journal, with the letters, alerts, messages and dev events around each.

**`red_error`: 0. `dialog`: 0.** Both for the whole run.

**`mental_break`: 1**, and it is not a colonist — seq 121, tick 204,000,
`{"pawn":"Crow","faction":null,"state":"Manhunter","causedByMood":false}`.
That is the incident source, not a symptom of it. No colonist broke all run.

**`downed`: 5** — seq 86 Wolverine (36,278), seq 109 Raccoon (121,232),
seq 119 Hare (187,785), seq 125 **Table** (214,599), seq 143 **Captain**
(229,014). **`death`: 12**, of which 2 are colonists (seq 135 Table 225,934;
seq 155 Captain 241,007); the other 10 are wildlife (Rat 25,673; Wolverine
44,018; Raccoon 75,387; Squirrel 80,310; Raccoon 121,832; Squirrel 181,890;
Hare 187,875; Sparrow 240,672; Quail 301,287; Rat 320,700) and none is
attributable to the colony.

### The colonist thread, seq by seq

| tick | seq | event | note |
|---|---|---|---|
| 723 | 62 | `dev:set-skill` Table Medicine 0→7 (Minor passion) | Captain Medicine 0→4, Chili 0→3 — skill on three, **Doctor work enabled on one** (ledger `colony-start-7`) |
| 723 | 80 | `action seek-at-will` x3 ON `[992,995,998]` | colony-start posture |
| 3,224 | 82 | `action seek-at-will` x3 OFF | after `109-pawns` at 3,224 put Table [128,53] "watching for targets", Captain [127,63] "cowering", Chili [132,45] "fleeing" — 50–70 cells south of a base at z≈112–120 |
| 121,050 | 108 | `alert_on Alert_NeedDefenses` High | never answered; `colony-start-10` `blocked` twice |
| **204,000** | 121 | `mental_break` Crow → Manhunter | `causedByMood:false` |
| **204,000** | 122 | `letter ThreatSmall` "Mad crow" | same tick — the letter is the honest clock |
| 204,024 | — | digest day-4: `threats.hostiles 7`, kinds `crow x1`; `pawns{filter:"hostile"}` → **total 0** | the crow was never visible to the filtered read at any point in the run, including while killing |
| **205,979** | 123 | `alert_on Alert_ColonistNeedsTend` **High** | **the first and only pre-casualty signal.** 1,979 ticks after the letter — inside the 800–2,000 lag, so a colonist was already bleeding at ~204,000–205,979. *The record cannot answer which colonist*: no colonist read exists between 204,024 and 225,935 |
| **214,599** | 125 | **`downed` Table**, damage `Bite` | 8,620 ticks (3.4 h) after the tend alert |
| 214,643 | 126 | `message ThreatBig` "Critical alert: Colonist needs rescue" | a message, not an alert — no scan lag |
| 214,659 | 127/128 | `alert_on ColonistNeedsRescuing` (Critical) / **`alert_off ColonistNeedsTend`** | see §2 — the off is a deterioration, not a recovery |
| 216,840 | 129 | `alert_on Alert_ColonistNeedsTend` again | a **second** casualty now needs tending: Captain |
| 219,897 | 132 | `message ThreatBig` "Critical alert: Medical emergency" | |
| 219,923 | 133 | `alert_on Alert_LifeThreateningHediff` (Critical) | Table's `BloodLoss` crossed severity 0.60; he died 6,011 ticks later |
| **225,934** | 135 | **`death` Table** — letter `Death: Table`, "Cause: Blood loss." | down and bleeding for **11,335 ticks (4.5 h)** |
| 225,935 | 138/139/140 | `ColonistLeftUnburied` on; `ColonistNeedsRescuing` off; `LifeThreateningHediff` off | **both criticals cleared because the patient died** |
| 225,935 | 141/142 | `action draft` Captain; `action move-to (124,116)` | Captain was at **[10,159]**, hp 57%, `job_def FleeAndCower`, `job_giver JobGiver_ConfigurableHostilityResponse`, `seek.will_seek false`, flags `["bleeding","tend"]` (`155-pawns`, `157-pawn`) |
| **229,014** | 143 | **`downed` Captain**, damage `Bite` | caught at [43,147] while walking the ~118 cells home; `163-pawn` at 228,936 had him at [42,148], drafted, `Goto`, `player_forced`, moving capacity 27%, bleed 3.35 |
| 229,044 | 145/146/147 | `ColonistNeedsRescuing` on; **`Alert_NeedDoctor` on**; `ColonistNeedsTend` off | the doctor alert fires **3,110 ticks after the doctor died** |
| 231,968 | 148 | `action tend` **REJECTED** `by_gate:{"drafted-only":1}` | |
| 231,968 | 149 | `action work-priorities` 3 cells / 1 pawn — Chili Doctor 0→3 | Chili was **asleep** at [122,114] |
| 231,968 | 150 | `alert_off Alert_NeedDoctor` | cleared by the checkbox, not by treatment |
| 235,024 | 152 | `alert_on Alert_LifeThreateningHediff` | Captain's `BloodLoss` ≥ 0.60; he died 5,983 ticks later |
| **241,007** | 155 | **`death` Captain** — "Cause: Blood loss." | down and bleeding **11,993 ticks (4.8 h)**; Chili was at [81,130] "rescuing Captain" at 239,599, ~35 cells short |
| 241,049 | 159/160 | both criticals off | roster 3 → 1 |

---

## 2. The backward walk

Three questions at every step: what state made this possible; what signal
existed and when (classified against the audit's three cases); what the loop
did (`grep` of `checklist.ndjson`).

### H1 — Table: downed 214,599, dead 225,934

**Step 1 — why did he die rather than recover?** Nobody tended him.

- *State*: he was the only pawn with Doctor work enabled. Ledger
  `colony-start-7` (day 1, tick 723, `verdict:"action"`): *"Doctor covered by
  nobody with skill>4 … Table Doctor 0->3 (Med 7)"*. Captain and Chili got
  Medicine skill 4 and 3 (seq 63, 64) and **no Doctor checkbox**.
- *Signal* — **case 3, no signal at all**, and verified in source that none
  can exist. `Alert_NeedDoctor.Patients` requires BOTH that no non-downed
  colonist has `workSettings.WorkIsActive(WorkTypeDefOf.Doctor)` AND that a
  patient exists. A colony with exactly one doctor satisfies neither clause
  until that doctor is the casualty. It fired at 229,044 — after Table died.
- *Loop*: `colony-start-7` fired and passed. `triggered.md` item 7 asks that
  every essential type be "covered by someone capable"; one is a count.
  `roster-change` (`triggered.md`) fired correctly at 231,968 — **after the
  first death**, which is by construction too late for it.

**Step 2 — why was he alone in the fight, and hurt at all?** Seek was off.

- *State*: `seek-at-will` OFF for all three since tick 3,224 (seq 82). The
  direct proof is on Captain, not Table, because no colonist read exists
  during Table's window: `157-pawn` at 225,935 reads `job_def:"FleeAndCower"`,
  `job_giver:"JobGiver_ConfigurableHostilityResponse"`,
  `seek:{"toggled":false,"will_seek":false}`. That is the vanilla flee node,
  reached because the SeekAndKill node above it declined
  ([[seek-off-is-a-decision-to-flee]]).
- *Signal* — **case 1, a genuine leading signal, read and answered wrongly.**
  The `ThreatSmall` letter at 204,000 is immediate and exact. `raid-letter`
  fired on it.
- *Loop*: ledger day 4, tick 204,024, `raid-letter`, `verdict:"action"`:
  *"seek-at-will deliberately left OFF — with 6 fogged insects also standing,
  enabling it would repeat the day-1 march to the hive. Intervening by
  exception instead."* The item was run, the read was made, and the item
  **has no flag for seek posture** — its `flag:` line covers unarmed pawns and
  shield/ranged pairings only. A read with no decision attached.

**Step 3 — why was seek off in the first place?** The day-1 near-miss.

- *State*: at tick 723 the colony had no `Area_Allowed`. Ledger `colony-start-9`
  expanded **Home** (289→701 cells); Home is not a movement restriction.
  `ForbidUtility.InAllowedArea` gates work cells against
  `Pawn_PlayerSettings.EffectiveAreaRestrictionInPawnCurrentMap`, and no such
  area existed. With nothing bounding them, seek-on marched all three at a
  fogged hive 50–70 cells away (`109-pawns`, tick 3,224).
- *Signal*: none was needed — the ledger row at 3,224 records the observation
  directly.
- *Loop*: `hostiles-standing` fired, `verdict:"action"`, and chose the
  cheapest of two bad options. `triggered.md`'s colony-start list has **no
  area-restriction step**; the option that makes seek-on safe was never on
  the list.

**Step 4 — why was the downing not noticed for 11,335 ticks?**

- *State*: steps 140–153 of the transcript are **seven** back-to-back
  `advance {ticks:2500}` calls (140/142/144/146/148/150/152), covering
  204,024 → 221,529, each followed by exactly one
  `pawns {filter:"hostile"}` that returned `total: 0`. (The run's `summary.md`
  and [[read-every-return-or-lose-a-colonist]] both say *six*; the transcript
  shows seven.)
- *Signal* — **case 1 again, and it was in the agent's own hands.** `advance`
  returns `journal_seq`. Four of those seven returns carried unread events:
  140 → `[123,124]` (the tend alert), **148 → `[125,128]`** (Table's `downed`,
  the rescue message, the rescue alert), 150 → `[129,131]`, 152 → `[132,134]`
  (the medical-emergency message). The events were named in the return value
  of the very call that produced them.
- *Loop*: `PLAY-LOOP.md` §read mandates `journal --since` then one `digest`
  **every return**. Across the whole run: **27 `advance` ops, 0 `journal`
  ops, 10 `digest` ops.** The `journal` verb was never called once in 195
  ops. Ledger `postmortem-trigger` (day 4, 225,935) records the slip in the
  agent's own words: *"PLAY-LOOP read step skipped 6 times."*

**Step 5 — why did the emergency posture stand down while he lay bleeding?**

- *State*: ledger `hostiles-standing`, day 4, tick **221,529**,
  `verdict:"action"`: *"threats.hostiles 7 for 17,000 ticks with
  pawns{filter:hostile} always 0 — mad crow never approached … exited
  emergency posture without engaging."* Table had been down and bleeding for
  **6,930 ticks** at that moment.
- *Signal* — **case 2, a scoped signal trusted as coverage.** The exit
  predicate `threats.hostiles == 0` (`PLAY-LOOP.md` §Emergency posture step 5,
  `turn.md §hostiles-standing`) watches enemies only. Worse, it was
  unsatisfiable: `threats.hostiles` read **6, 6, 6, 7, 6, 6** at day-1 → final
  (ticks 723 → 322,314) because four megascarabs, a locust and a spelopede
  stood permanently fogged. `raid-end` triggers on the transition to 0, so
  `raid-end` — and the mod-side post-raid procedure `cc8988c` that inherits
  its predicate — **could not fire at any point in this run.**

**H1 stops here**: the question has become "what would have had to be
different", and the answer is a colony-start posture and a doctor count.

### H2 — Captain: downed 229,014, dead 241,007

Steps 2–5 of H1 apply unchanged (same flee, same absent doctor, same unread
loop). Three causes are H2's own.

**Step A — the walk home could not work, and the record says so numerically.**
At 225,935 Captain was at [10,159], hp 57%, bleed 2.4→2.97/day. He was
drafted and given `move-to (124,116)` — ~118 cells — with `Moving` capacity
40% falling to 27% (`161-pawn`, `163-pawn`). He covered 33 cells in 3,079
ticks and was downed at [43,147]. *Signal*: the `pawn health` read at 227,436
published `bleed_rate 2.97`, `summary_pct 46`, `Moving 40%`. *Loop*: no
checklist item covers "can this pawn survive the trip you just ordered"; the
draft was an intervention-by-exception under `PLAY-LOOP.md` §Emergency
posture step 3, which has no such test. **Case 3, no signal.**

**Step B — the read that would have shown the clock was cut by a cap.**
Four `pawn {sections:["health"]}` reads were made on Captain (225,935 /
227,436 / 230,442 / 231,968). **`BloodLoss` — the hediff named on his death
letter — appears in none of them.** `PawnSerializer.HediffCap` is 20 and
`Rank()` sorts `BleedRate > 0` (rank 4) above `CurStage.lifeThreatening`
(rank 3); Captain carried 17 bleeding bites and 3 bleeding scratches, so 20
rank-4 rows filled the cap exactly. `hediffs_total` / `hediffs_more` across
the four reads: **27/7 → 36/16 → 39/19 → 39/19.** And `BloodLoss` is
`lifeThreatening` only from severity 0.60 (`Hediffs_Global_Misc.xml`,
`lethalSeverity 1`), so it would not have out-ranked the bites even then.
The serializer's own comment says "the cap must never hide the wound" — it
hid the *cause of death*, not the wounds. *Signal*: **case 2, a self-muzzled
read trusted as complete.** The only tell was `hediffs_more: 19`.

**Step C — the rescue was winnable and was lost to autonomy latency.**
Arithmetic, all from the record:

- `HealthUtility.TicksUntilDeathDueToBloodLoss` = `(1 − BloodLoss.Severity) /
  BleedRateTotal × 60000`. At 231,968 Captain read `bleed_rate 3.46`,
  `Consciousness 28%`; he died 9,039 ticks later, which back-solves to
  severity ≈ 0.478 and a clock of **≈9,040 ticks**. The estimator agrees with
  the outcome to four ticks.
- Chili's observed pace: [122,114] at 238,074 → [81,130] at 239,599 = 41
  cells in 1,525 ticks = **37 ticks/cell**. [122,114] → [46,145] is ~76
  cells ≈ **2,810 ticks**.
- What actually happened: at 231,968 the response was `work-priorities`
  (Chili Doctor 0→3) — an *autonomy nudge*. She was `sleeping` at [122,114]
  at 233,497, 235,024 and 236,549; `consuming packaged survival meal` at
  238,074; `rescuing Captain` by 239,599; at [76,130] and "wandering" at
  241,105, having turned back. She set off **~6,100 ticks** after the clock
  was set.
- What was available: **`rescue` is a shipped player verb**
  (`PawnOrderVerbs.cs:1154`, `TakeToBed`), it routes through
  `Pawn_JobTracker.TryTakeOrderedJob`, which sets `job.playerForced = true`
  and calls `EndJobWith(JobCondition.InterruptForced)` when
  `IsCurrentJobPlayerInterruptible()`; `LayDown` sets only
  `casualInterruptible: false` and inherits `playerInterruptible: true`, and
  `JobDriver.PlayerInterruptable` is `true`. **A forced rescue wakes a
  sleeping pawn.** Issued at 231,968 it arrives at ≈234,780, leaving ≈6,200
  ticks of the clock to tend or carry.
- **`rescue` was called 0 times in 195 ops.** So were `attack`, `undraft` and
  `assign`.

*Signal*: **case 3 for the clock** (`ticks_until_bleedout` is published
nowhere; `bleed_rate` alone is a rate without a remaining volume) and
**case 1 for the emergency itself** (`pawns` published
`flags:["downed","bleeding","tend"]` on Captain at 233,497, 235,024, 236,549,
238,074 and 239,599 — five reads the agent made and did not act on beyond
advancing). *Loop*: ledger `roster-change` at 231,968 records the
work-priorities fix and the `tend` refusal; **no ledger line, and no
checklist item anywhere, covers "a colonist is bleeding out and the rescue
has not started".**

### N1 — the day-1 near-miss (no casualty)

Covered as H1 step 3. Recorded here as a near-miss in its own right because
it produced the decision that killed the colony, and because it is the one
harm the run *avoided*.

---

## 3. Root-cause classification

Per `postmortem.md`'s table. Both incidents carry several classes; each row
is one cause.

| # | cause | class | evidence |
|---|---|---|---|
| C1 | Doctor coverage of one reads as coverage; `Alert_NeedDoctor` cannot warn of it | **no-signal** | `Alert_NeedDoctor.Patients` needs zero non-downed doctors AND a patient; fired 229,044, after the doctor died 225,934 |
| C2 | Seek posture was read at the raid letter and no response was decided | **policy-gap** | ledger `raid-letter` 204,024 `action`; `triggered.md §raid-letter` `flag:` covers armament only |
| C3 | No `Area_Allowed` existed, so seek-on was unsafe and seek-off was the only move | **structural** | no `area create/assign` in 195 ops; `ForbidUtility.InAllowedArea` gates on `EffectiveAreaRestrictionInPawnCurrentMap`; ledger row at 3,224 |
| C4 | 7 advances, narrowed read, 11,335 ticks of bleeding unseen; `journal` never called | **execution-slip** | 27 advances / 0 `journal` / 10 `digest`; `journal_seq` `[125,128]` returned unread at step 148 |
| C5 | Nothing in the harness stops an advance on an own-faction casualty by default | **no-signal** | step 148 `{ticks:2500}` ran 2,502 ticks through the downing and returned `reason:"ticks"`; the `until.event.type=downed` guard was added by hand at step 154, after the death |
| C6 | `Alert_ColonistNeedsTend` clearing was read as improvement; it means the patient was downed | **mistrusted-signal** | `Alert_ColonistNeedsTend.NeedingColonists` excludes any pawn where `Alert_ColonistNeedsRescuing.NeedsRescue(item)` — off at 214,659 and 229,044, both times on the downing |
| C7 | `BloodLoss` invisible in every health read (20-row cap, bleeding out-ranks it) | **mistrusted-signal** | `hediffs_more` 7/16/19/19; `PawnSerializer.Rank()`; `BloodLoss.lifeThreatening` from 0.60 |
| C8 | No published time-to-bleed-out, so "can anyone get there in time" was unaskable as a number | **no-signal** | `health` publishes `bleed_rate`, `needs_tend`; `HealthUtility.TicksUntilDeathDueToBloodLoss` is one line and is published nowhere |
| C9 | The rescue was left to autonomy while the rescuer slept; `rescue` was never issued | **policy-gap** | Chili asleep 233,497–236,549; `rescue` verb shipped, 0 calls; forced jobs interrupt `LayDown` |
| C10 | `threats.hostiles == 0` is not reachable on a map with permanently fogged standing hostiles, so the fight-over predicate (and `raid-end`, and `cc8988c`) can never fire | **structural** | `threats.hostiles` = 6,6,6,7,6,6 across the entire run |
| C11 | Emergency posture exited on an enemy-only predicate while a colonist lay bleeding | **policy-gap** | ledger `hostiles-standing` 221,529; Table down 6,930 ticks |
| C12 | Day-1 and day-4 daily sweeps incomplete; day-3 rows duplicated | **execution-slip** | see §Compliance |

**Deliberately not claimed as causes.** `dev:spawn-thing` returning
`ok:true, placed:0` for the research bench (seq 66) and the lost-client
advance (`time-control-drift`) are real platform defects, already filed, and
neither is on the path to either death. `Alert_NeedDefenses` standing High
from 121,050 is not a cause either: the killer was a manhunter *animal*, which
sandbags and turret cover do not answer.

---

## 4. The wealth check

Mandatory for any prevention that adds wealth. Read, not argued.

**The actual series** (`saves/Autosave-5.rws`, `autoRecorderGroups`,
30,000-tick cadence; `ThreatPoints` is stored **÷10** —
`HistoryAutoRecorderWorker_ThreatPoints` returns
`DefaultThreatPointsNow(...) / 10f`, so the column below is ×10 back to real
points):

| tick | Wealth_Total | Items | Buildings | Pawns | FreeColonists | ThreatPoints |
|---|---|---|---|---|---|---|
| 0 | 15,274.1 | 9,343.9 | 3,675.2 | 2,255 | 3 | 35 |
| 30,000 | 20,392.1 | 13,577.7 | 4,194.4 | 2,620 | 3 | 49 |
| 60,000 | 20,598.0 | 13,783.6 | 4,194.4 | 2,620 | 3 | 50 |
| 90,000 | 21,168.8 | 14,354.4 | 4,194.4 | 2,620 | 3 | 52 |
| 120,000 | 21,870.0 | 15,055.6 | 4,194.4 | 2,620 | 3 | 55 |
| 150,000 | 22,514.7 | 15,690.3 | 4,194.4 | 2,620 | 3 | 58 |
| 180,000 | 22,530.7 | 15,706.3 | 4,194.4 | 2,620 | 3 | 58 |
| 210,000 | 22,443.9 | 15,654.5 | 4,194.4 | 2,595 | 3 | 55 |
| **240,000** | 21,003.3 | 15,538.9 | 4,194.4 | **1,270** | **2** | **35** |
| **270,000** | 20,366.8 | 15,077.4 | 4,194.4 | **1,095** | **1** | **35** |
| 300,000 | 20,386.5 | 15,097.1 | 4,194.4 | 1,095 | 1 | 35 |

**The slope.** `StorytellerUtility.PointsPerWealthCurve` is **flat zero below
14,000** and rises to 2,400 at 400,000 — a marginal
**0.00622 threat points per silver** above the floor, with buildings counted
at `BuildingWealthFactor 0.5`. `PointsPerColonistByWealthCurve` at this
wealth is ~19 points **per colonist**. The colony peaked at 22,530.7 — only
8,531 silver above the free floor, worth ~53 points, against ~57 from three
colonists. Below the clamp, `GlobalPointsMinRangeFloor` is 35, which is
exactly what the recorder shows before and after the incident. (The full
product also carries `IncidentPointsRandomFactorRange`, adaptation,
`threatScale` and `pointsFactorFromDaysPassed`, none of which the platform
publishes — so the recorded column is quoted, not re-derived.)

**The number that decides this postmortem.** The two deaths moved
`Wealth_Pawns` 2,595 → 1,270 → 1,095 and `ThreatPoints` **55 → 35 → 35**.
*The colony bought a 20-point threat reduction with two colonists.* At the
marginal slope, buying those same 20 points back through wealth reduction
would mean burning ~3,200 silver. Wealth was not the pressure here, and
spending against it is not the lesson.

**Verdict on the §5 outputs: every one of them costs ZERO wealth.**

| prevention | wealth added | why |
|---|---|---|
| second Doctor checkbox (`work-priorities`) | 0 | a work priority is not a `Thing` |
| `Area_Allowed` + `assign` + `seek-at-will on` + `hostility "Attack"` | 0 | areas and pawn settings are not `Thing`s |
| forced `rescue` order | 0 | a job |
| default casualty halt on `advance` | 0 | mod behaviour |
| `ticks_until_bleedout` field, `BloodLoss` cap exemption | 0 | a serializer field |
| trust-table row, checklist line | 0 | prose |

The cheap-per-wealth preference is therefore not a tie-break here, it is the
whole answer: **the prevention for both deaths is policy and reading, not
property.** For contrast, the wealth-adding prevention someone would reach
for — answering `Alert_NeedDefenses` with an improvised turret (100 steel +
30 components, a few hundred silver, halved by `BuildingWealthFactor`) —
costs on the order of **1.5 threat points**, which is cheap, *and would not
have saved either colonist*, because a manhunter crow walks past cover. It is
not named below.

---

## 5. The outputs — named, not landed

Lowest rung that removes the cause. **Standing ruling (Evan, 2026-09-01):
a deterministic finding goes in the MOD, not in a note — "notes get
ignored".** The evidence for that ruling is in this repo: **all four M1
lessons are orphans**, cited by zero lines in `checklists/` and `templates/`
(`grep` for all four names returns nothing). Where a branch is computable
from state the observers already publish, the rung below is MOD.

Each output names its target, its rung, and its `SESSION-START.md` position.
**An output the next session will not load is not landed.**

### OUT-1 — spec issue (MOD rung) · covers C4, C5

**`advance` halts by default on an own-faction `downed` or `death`.**

- Rung: *checklist/procedure → mod*. Every branch computable: `Journal`
  already emits `downed`/`death` with `faction`, and `TimeDriver` already
  evaluates `until:{event:{type:…}}`. The change is making own-faction
  casualties an **opt-out** halt rather than an opt-in guard, and naming the
  halting event in the return envelope.
- Acceptance sketch: an `advance {ticks:N}` with no `until` returns
  `reason:"event"` at the tick of the first own-faction `downed`/`death`; an
  explicit opt-out restores the old behaviour; the run's step-148 case
  (`{ticks:2500}` from 214,025) halts at 214,599 instead of running to
  216,527.
- **Surfaces at: n/a — mod rung.** Per `SESSION-START.md`'s table, cite the
  issue id in **`playbook/read-every-return-or-lose-a-colonist.md`**, whose
  INDEX line loads at **position 1**. That lesson's "Prefer a guard to a
  poll" bullet then describes a default rather than a discipline.

### OUT-2 — spec issue (MOD rung) · covers C7, C8

**Publish `health.ticks_until_bleedout`, and exempt `BloodLoss` from the
hediff cap.**

- Rung: *→ mod*. `HealthUtility.TicksUntilDeathDueToBloodLoss(pawn)` is one
  vanilla call; `PawnSerializer`'s health row already computes `bleed_rate`
  and `needs_tend` next to where it would go. Second half: give `BloodLoss`
  (or any hediff whose `def.lethalSeverity > 0`) a rank above bleeding wounds
  in `PawnSerializer.Rank()`, or exempt it from `HediffCap` outright.
- Acceptance sketch: replay Captain at tick 231,968 → `ticks_until_bleedout`
  ≈ 9,040 and a `BloodLoss` row present with `hediffs_total 39`.
- **Surfaces at: n/a — mod rung.** Cite the issue id in
  **`playbook/one-doctor-is-zero-doctors.md`** → **position 1**.

### OUT-3 — spec issue (MOD rung) · covers C1, C9, C11

**A casualty-triage procedure — the medical twin of post-raid (`cc8988c`),
the rung's existing tenant.**

- Rung: *checklist/procedure → mod*. Every branch is computable from
  published state: casualty and urgency from `digest.colonists[].flags` and
  `pawns` (`downed`, `bleeding`, `tend` all ship today); the clock from
  OUT-2; doctor coverage from `pawn {sections:["work"]}`; reachability and
  travel from `path-cost` / `nearest` / `reachable`. Its ordered body:
  1. **≥2 non-downed pawns with Doctor active** — the invariant, checked at
     colony start and re-checked on every roster change, not only when the
     count reaches zero.
  2. **Issue `rescue` as a forced order** to the nearest able pawn — do not
     leave it to work priorities. Verified: `TryTakeOrderedJob` sets
     `playerForced` and interrupts `LayDown`.
  3. **Refuse to advance** while `ticks_until_bleedout < travel_estimate` for
     every able rescuer, naming the pawn and the shortfall.
- Acceptance sketch: replay tick 231,968 → the procedure emits a `rescue`
  for Chili (clock 9,040, travel ~2,810) instead of a work-priority flip.
- **Surfaces at: n/a — mod rung.** Cite the issue id in
  **`playbook/one-doctor-is-zero-doctors.md`** → **position 1**.

### OUT-4 — new colony-start item (CHECKLIST rung) · covers C2, C3

**`checklists/triggered.md` §Colony start, new item 12 — "combat posture
bound before the first advance".** The only prose→checklist promotion here,
and it is owed: [[seek-off-is-a-decision-to-flee]] is verified-in-source,
cost a colony, and is cited by no checklist line.

- Body: create an `Area_Allowed` over base + fields + cleared ground and
  `assign` every pawn to it; `assign {hostility:"Attack"}` on every humanlike;
  `seek-at-will {on:true}` for every violence-capable pawn; **verify per pawn
  that `state.seek.will_seek == true`** — not `toggled`, and never by reading
  `hostility_response`, which echoes `"Flee"` either way.
- Meets the promotion criteria: names a read and a flag; its trigger recurs
  on every colony; missing it cost two colonists; it is act-keyed, so it
  lands in `triggered.md` and does not touch `daily.md`'s cap of 7
  (current 4) — no merge-or-retire is owed.
- Cites [[seek-off-is-a-decision-to-flee]] and
  [[combat-role-passion-over-skill]].
- **Surfaces at: position 3** — `SESSION-START.md`: *"`checklists/triggered.md`
  — the trigger table into working context; on a NEW colony, its colony-start
  section runs now, top to bottom, before the first advance."* Verified: this
  is the one list guaranteed to run before the first advance of a new colony,
  which is the only moment the fix is free.
- Follow-on (do not block on it): the *verification* half is computable and
  belongs in OUT-3's procedure; the area **geometry** is judgement and stays
  in the checklist.

### OUT-5 — two trust-table rows (CHECKLIST rung) · covers C1, C6

**`checklists/turn.md` §"How far to trust the alert readout" — two rows:**

| alert | trust | because |
|---|---|---|
| `Alert_ColonistNeedsTend` | **lean on it, and read its OFF as bad news** | it excludes any pawn where `Alert_ColonistNeedsRescuing.NeedsRescue` is true, so it clears when the patient goes DOWN, not when treated. In `m1-20260831` it cleared twice, at 214,659 and 229,044, both on a downing. It is also the run's only pre-casualty signal: on at 205,979, 8,620 ticks before the first downing |
| `Alert_NeedDoctor` | **never wait for it** | `Alert_NeedDoctor.Patients` requires zero non-downed colonists with Doctor active AND a patient already needing tend — it cannot warn that you have one doctor, only that you now have none. Fired 229,044, 3,110 ticks after the doctor died |

- **Surfaces at: position 2** — `SESSION-START.md`: *"`checklists/turn.md` —
  into working context whole … the one file that must never be drill-down."*
  Verified: the trust table lives inside that file, so both rows are in
  context before the first digest is read.

### OUT-6 — amendment, not a new artifact · covers C10, C11

**`threats.hostiles == 0` is not a usable fight-over predicate.** Two
targets, both existing:

1. **A comment on git-bug `cc8988c`** (the post-raid mod procedure): its
   entry/exit must key on *reachable, non-fogged* standing hostiles, and must
   **not** exit while an own-faction casualty is outstanding. Evidence:
   `threats.hostiles` never left the 6–7 band across 321,591 ticks, so
   `cc8988c` as currently predicated would not have fired once this run.
   *This worker does not write to git-bug — the orchestrator lands it.*
   **Surfaces at: n/a — mod rung.**
2. **One line in `turn.md §hostiles-standing`**, under the existing fog
   caveat: a permanently fogged hostile pins this trip-wire on forever, so it
   arms the posture and can never disarm it; the exit belongs to `cc8988c`'s
   predicate, not to this count. **Surfaces at: position 2.**

### Not an output

**`playbook/quicktest-and-autostart-collide`** stays an orphan by design: it
fires at launch, before any checklist is loaded, and the orchestrator does
all launching. Recorded so 4.4's retirement pass does not read its zero
citations as idleness.

---

## Compliance finding (4.2) — C12, recorded not artifacted

Per Evan's standing instruction this is an **execution-slip**, a 4.2
compliance finding, and **not** a new checklist item.

- **The read step.** `PLAY-LOOP.md` §read mandates `journal --since` then one
  `digest` on **every** return. Actual: **27 `advance`, 0 `journal`, 10
  `digest`** in 195 ops. The `journal` verb was never called during play.
- **Day 1.** `daily.md` has 4 items (`freezer-below-zero`, `armed-roster`,
  `production-still-runs`, `apparel-margin`). Day 1 logged **none** of them —
  21 ledger rows, all colony-start and turn items.
- **Day 4.** Logged **1 of 4** (`armed-roster`, at 241,007). No
  `freezer-below-zero`, `production-still-runs` or `apparel-margin` row
  exists for day 4, not even `n/a`. `checklists/README.md`'s rule is one line
  per item per day *whatever the verdict*, so coverage stays a diff rather
  than a judgement; on day 4 it became a judgement.
- **Day 3.** Five rows are duplicated verbatim at the same tick 125,767
  (`freezer-below-zero`, `armed-roster`, `production-still-runs`,
  `apparel-margin`, `barracks-heat`) — 11 rows for a 6-item day.
- **Ledger hygiene.** Three item names in the ledger are defined in no
  checklist file: `postmortem-trigger`, `time-control-drift`, `barracks-heat`
  (the last says so itself: *"NOT a checklist item — recorded as a standing
  watch"*). The ledger is the log of what ran, so ad-hoc names in it make
  coverage un-diffable against the files.

---

## What the record cannot answer

- **Which colonist `Alert_ColonistNeedsTend` was about at 205,979.** No
  colonist read exists between the day-4 digest (204,024) and `155-pawns`
  (225,935), and no autosave falls in the window (Autosave-3 is 180,000,
  Autosave-4 is 240,000).
- **Whether the three colonists were actually holding their weapons at tick
  3,224.** The equip jobs were accepted at 723 (seq 72–74) and the day-2
  `armed-roster` row confirms all three armed at 65,746, but `109-pawns` has
  no equipment field. The ledger's word "unarmed" at 3,224 is the agent's
  claim, not an observation in the record.
- **Whether the crow was ever killable.** It never appeared in
  `pawns {filter:"hostile"}` at any tick, including while it was killing;
  `threats.kinds` shows `crow x1` only in the day-4 digest and it is gone by
  day 5, with no `death` event for it. Whether it left or its manhunter state
  expired, the record does not say.
- **Whether a forced `rescue` at 231,968 would in fact have saved Captain.**
  The mechanism is verified in source (forced jobs interrupt `LayDown`) and
  the arithmetic clears by ~6,200 ticks, but no counterfactual was run.
  Stated as a bound, not a certainty.
- **Any trend.** Sampling (`2d9a1da`) has not landed. Six digests are six
  points. The only series in this document is the game's own
  `HistoryAutoRecorder`, recovered from an archived save after the run ended.
