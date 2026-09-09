# The two rounds the walkthrough asked for

Run 2026-09-08, one agent each on Fable, both read-only. Round 2 was deliberately
blind: it was given the six honesty claims stripped of their justifications and
forbidden to open `COCKPIT.md`, because this project has twice been bitten by
conclusions that were plausible, well-cited and wrong, and an agent that reads the
argument agrees with it.

---

## Round 2 — the honesty claims, read blind

### The finding that reshapes the design

The audit's central number is that a truthful field the agent did not ask for went
unread, 52 of 52. The blind reader went to the raw harness transcripts and found the
mechanism: **`ignored_args` reached the agent's context in 3 of 2,550 tool results.**
The other 49 were never *displayed*. The agent piped almost everything it ran through
its own filter.

**Independently re-measured before this file was written** (counting Bash commands
mentioning `rwa` across the four session transcripts, a slightly different denominator
from the reader's 1,663 invocations, same conclusion):

| | count |
|---|---:|
| shell commands invoking `rwa` | 2,988 |
| through a `python3 -c` filter or the agent's own `$S/` wrapper | 2,546 |
| through `head` / `tail` | 246 |
| to `/dev/null` | 185 |
| **bare — no pipe, no redirect** | **167** |

So "unread 52 of 52" is mostly "filtered out 49 of 52". **The agent read its filter's
output, not the reply.**

This is not a small correction. It is the premise of claim 1, which is the keystone
of the whole design, and it means *the position of a field was never the variable*.
Within the first hour of the run the agent wrote a wrapper whose header says it
"ALWAYS surfaces data.ok / placed / refused" — and from then on it read that field
(F-S01-19). It read beside-success fields whenever its own print statement showed
them: `posture`'s `after` block caught the seek flip twice (F-S02-7, F-S10-22), the
returned mute list caught an inverted `alert-mute` (F-S12-8). And it missed
asked-for things its filter dropped: `halted_on` was shown 76 times and quoted 0.

**The consequence for the cockpit:** the mod ALREADY returns a proto-screen on
`advance` — `halted_on`, `muted_alerts`, `news_rode_past`, `letters`, `unread_after`
(TimeDriver.cs:1902-2061) — and it was filtered ~90% of the time. A bigger reply is a
bigger thing to pipe through `python3 -c`. Unless the screen is load-bearing for the
next command, pushing it changes nothing.

### The six verdicts

| # | claim | verdict |
|---|---|---|
| 1 | the screen is the reply, not a field beside it | **CONTRADICTED** as a description of the run — see above |
| 2 | decisions owed hold the clock | **CONTRADICTED** on both operative halves; premise supported |
| 3 | losses are levels, not events | **SUPPORTED** |
| 4 | gauges come from a full count | **SPLIT** — the verification rule holds, the diagnosis is mis-aimed |
| 5 | chores do not need attention | **CONTRADICTED** on "cost confined to judgement" |
| 6 | it is measured | **UNKNOWN** — every channel it names leaked this run |

**On 2.** "A screen that arrives as the reply cannot be discharged by discarding it"
is false: stdout is discardable and was discarded 185 times; 26 steps were orphaned
with no reply received at all, 7 of them advances moving 106,874 ticks. And 15.3% of
the run's ticks moved outside any returned advance, so a gate on `advance` governs
none of it. (This file first said 18.3%, quoting the audit; the interval-union
recount in `ROUNDS-2.md` corrected it, and found that 659 of 659 returned advances
ended paused — the mod never left the clock running.) Worse for the design: reasoned, journaled deferral
ALREADY EXISTS — `alert-mute` requires a non-empty reason and journals it — and two
of the run's ten mutes give as their reason that the alert was holding the clock
("cutting 45,000-tick advances down to 4,600"). It was the release valve, not a brake.
That is the mechanism element 3's mute list now proposes to generalise.

**On 4.** No gauge count in this run was capped. The counts were full; the *lists* and
the *coordinates* lied. What killed people was a full count of the WRONG SET:
`work_coverage ok:true, under:[]` (three deaths), `pawns --filter hostile` returning 5
sleeping mechs during a 12-vulture attack, batteries reported healthy in a doorless
room. "Full count" is necessary and nowhere near sufficient.

**On 5.** Every automation that wrote an unasked lever this run did harm: `auto_arm`
reverted a deliberate equip; `seek:true` walked Tony 130 cells and downed John,
starting the infection that killed him; `attack` mode `auto` routed a Melee-3 pawn
into a scyther; the research auto-picker took 16 projects in one slice. The *need*
for the chores is real; the safety assertion is not.

### The seventh defence — the most valuable thing either round returned

**"A verb writes only the levers it was passed, and defaults to the narrowest scope."**
Theme T8, UNFILED. None of the six touches it, and claim 5 pushes the other way.

`posture` with no `pawns` unbound all seven colonists to free one rescuer. They were
40 to 70 cells outside the walls when the fatal raid landed ~270,000 ticks later. It
is the agent's own #2 link in the wipe chain, and the correctly scoped form was used
twenty minutes later in the same slice. A screen shows this *after* it happened; this
defence stops the write.

### Two corrections to the audit's own facts

- **10 pictures were opened, not 7** — counted from `Read` calls in the harness logs.
  The distribution is worse than the total: six on day 1, four on day 2 before 12:50,
  and **zero across the session containing nine deaths**, **zero after 12:50** — which
  covers five mech raids and the wipe.
- 6,674 steps, not 6,688 (tables §0).

### What it confirmed is unknowable, and the measurement that would settle it

- Whether the lower panels get read: **confirmed unknowable** from this run. No
  extended thinking was recorded, and the harness log holds the filter's output.
  The proposed settlement is also a fix: **end the screen with the decision and alert
  ids it owes, and require the answering verb to cite an id that appears only there.**
  Compliance becomes a join between screen content and the next verb's arguments,
  computable from transcripts alone, and filtering the screen becomes self-defeating
  because the agent needs what it filtered out.
- Whether a pushed picture gets opened: **not unknowable** — it is measurable today
  from the harness, and only from the harness, because `render` writes no transcript
  step. Whether an opened picture is USED wants a nonce or legend key burned into the
  PNG that the next verb must echo.

---

## Round 1 — the chore list and the four categories

### The categories are not a partition

They are four values of one attribute — *what happens when a threshold on published
state is crossed* — of one kind of thing:

- **gauge** = a level, no threshold
- **light** = the threshold itself, and it is already a stop (`TimeDriver.cs:43-49`
  halts on every `alert_on`)
- **chore** = a light with a fixed response
- **decision** = a light whose response is left to the agent
- **stop** = a light with attention only

So the compound rows in the table ("an instrument bug, then a chore") are the pipeline
read in order, not evidence of a fifth category. `light` is not a sub-type of gauge
being smuggled in; it is the shared component the other three are built from.

**Four things fit none of the four,** each with a home the design already uses but the
header does not name: the verb reply or refusal (7 rows plus rules 1 and 11 land in
the honesty rules `27bf321`); the record (the human and mod rows are an event log that
neither stops nor levels); un-framed judgement (rules 8 and 9 — decisions the mod never
frames); and **time that moves outside any advance** (15.3% of this run — the 18.3%
this file first carried was the audit's delta-sum, corrected by interval union
in `ROUNDS-2.md`), which
produces no screen at all and which the table does not model.

### The defect that matters most

**"After a fight" would not have fired once in the final quarter.** Its trigger is
`threats.hostiles == 0`, and a dormant mech cluster counts as hostile by faction, with
no dormancy short-circuit. Every one of the 37 digest envelopes from tick 8,793,512 to
the wipe reads `hostiles: 5`. The chore built to rescue and tend after a fight is
inert exactly across the period that killed the colony. The escape hatch,
`threat-pardon`, is a judgement act and was called zero times.

### Other rows that hide a judgement

- **tend until stable** reproduces the death it is named for: `tend` is drafted-only
  and uses inventory medicine only, so a forced tend is bare-handed — Ellis died of
  infection with 18 medicine in stock. The route that actually worked in the run was
  `prioritize` on `DoctorTendEmergency`, which fetches medicine.
- **after a fight**: "finish off or capture" collapses to "finish off", because no
  verb writes `ForPrisoners` so the capture chain cannot complete; and "unforbid what
  the fight dropped" needs a definition of fight provenance that nothing publishes.
- **butcher**: "colony kill or hunt" is not published — the `death` journal row
  carries no instigator, though `DamageInfo.Instigator` is available in the hook the
  mod already has. The corpse rot clock is explicitly not published.
- **a joiner**: "the colony's posture" is not a fact — posture is per-pawn, and this
  run's joiner postures were deliberately non-uniform.
- **a doctor gone**: under-specified ("the only doctor" by which count?), and it
  quietly overturns a third shipped ruling that `§Where it lives` does not record.
- **research queue**: the auto-picker it reverts is **not vanilla** and is not in any
  of the 76 decompiled mods — its origin is unidentified.

### On roof and save

**`save` was correctly removed and no chore row sits on the wrong side of that seam.**
The working test — automatic means no branch needs a policy and none mutates colony
state — puts save cleanly on the automatic side. (One caveat: saving is disabled
during a force-pausing dialog, so a dialog stop cannot save.) The real defect there is
an incomplete knob column: the three rows with knob "—" each hide an undeclared policy.

**`roof` is an instance of a general defect**, not a one-off: *trigger on the late
symptom, act on the symptom, leave the cause*. Three of seven rows share it — tend
until stable (symptom layer of "no available doctor"), research queue (fires after the
picker already moved), and butcher (cause is "corpses lie forbidden where they fall").
It is a quality defect rather than a category defect: T11's point stands that a
symptom-chore still beats a decayed rule.

Also found: the roof procedure can designate cells nobody can build — `WorkGiver_BuildRoof`
requires a roof holder within 6.9 cells — and the screen would print "roofed stockpile
12 (8 cells designated)" regardless. That is T0's exact shape: ok means served.

---

## What neither round could settle

Round 2: whether the lower panels are read (unknowable from this run; the measurement
above settles it going forward). Round 1: whether `threats.hostiles` counted the 12
manhunter vultures (the code says yes; no digest envelope exists in the window to
prove it — a bench with a manhunter fixture settles it); the origin of the
"Auto-selected research" string; and how many of the run's deteriorated stacks lay
within range of a roof holder, which decides whether the roof chore would have done
anything at all.
