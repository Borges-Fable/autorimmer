# The second night of rounds — 2026-09-09

Three Fable rounds and two Opus implementations, run overnight while Dorian
slept, all read-only except the two implementations. `ROUNDS.md` holds the first
night; nothing here repeats it.

**The headline: this night corrected the audit's own arithmetic, and corrected a
fix made the night before.**

---

## Round 3 — time outside `advance`

### The audit's central number for this theme was double-counted

F-XC-4 reported 1,935,033 ticks — 18.3% — moving outside any returned advance,
by summing consecutive `state.tick` deltas. Recomputed as an **interval union of
every returned advance** (`[tick − ticks_elapsed, tick]`, 659 results):

| | ticks | share |
|---|---:|---:|
| inside a returned advance | 8,956,219 | **84.7%** |
| outside | 1,613,739 | **15.3%** |

**368,518 ticks the audit called unwitnessed were inside advances that did return
their results** (167,698 and 200,820 in two sids). The backgrounded loops of
F-S10-3 returned to the transcript; foreground reads merely observed them
mid-flight and the delta method counted that as lost time. Every number derived
from 18.3% is wrong by that much, here and in `COCKPIT.md`.

### The mod was not the problem

**659 of 659 returned advances ended paused** (`paused_on_exit: true`), 0
`pause_refused`, 9 `stalled` and all nine `cause: external-pause` — a keypress.
The mod never left the clock running. The design's mechanism held.

### What the 15.3% actually is

| cause | ticks | share | fault |
|---|---:|---:|---|
| **A. Human at the controls**, 11 windows | 1,445,452 | 89.6% | legitimate |
| **B. Orphaned advances ticking after the client died** | 62,709 | 3.9% | client + harness |
| **C. Client declared the bench down while its own advance ran** | 97,505 | 6.0% | client + a mod race |
| D. The agent's own `unpause` | ~1,626 | 0.1% | legitimate |
| E. Boot and reload | 2,378 | 0.1% | legitimate |
| F. Small and unexplained, 6 intervals | 4,007 | 0.2% | UNKNOWN |

Nine tenths of it was Dorian playing, which the design already rules must stay
possible. **C is a real client bug with a mod-side race behind it:** `rwa`'s
`health()` samples `status.json` once per poll with no retry, while
`Poller.AtomicWrite` does `File.Delete` then `File.Move` every second — so the
client can declare a live bench dead while its own advance is in flight, and the
very next call comes back `busy` naming the same command id. F-S09-2's `quest` is
the same race.

### F-XC-5 is a non-finding

The 6h03m "no explicit `pause`" window: every outside tick in it is already
counted in A/B/C, and the other ~5.3M ticks were inside ~300 returned advances
each ending paused. **A turn-based loop does not need `pause`.** Recorded so it
is not re-derived as a defect a third time.

### The phenomenon the design does not name

**174 non-advance replies carried `state.paused: false`** — `things` 25, `zone`
22, `build` 21, `pawn` 19, `journal` 18. Roughly 580,000 ticks moved under the
agent's own calls after a human left the clock running, and it paused exactly
twice in the whole run. The envelope said so every single time. Theme T0 again,
in a place nobody had looked.

One caveat the round found and states: `state` is a frame-top snapshot taken
before `TimeDriver.FrameStep`, so an advance's own reply can show `paused:false`
one frame stale — 22 did. `data.paused_on_exit` is the truthful field.

### A mechanism worth knowing before the next run

`TickManager.TogglePaused` restores `prePauseTimeSpeed`. The mod's exit `Pause()`
is a `TogglePaused` from Ultrafast, so **after every advance, one spacebar tap
runs the colony at ~900 tps.** Measured at 858–887 tps in three human windows.

### Verdict on `65e7cf9`

**Not implementable as written.** "No tick without an advance in flight" is the
1.3 re-pin the mod deliberately deleted: the mod can call `tm.Pause()` but cannot
stop a human's `TogglePaused` on the next frame except by pinning every frame,
which makes Dorian's nine days impossible. Tested against the run it would have
blocked 1.45M ticks of legitimate play, done nothing for B or C, and never fired
for the 580,000-tick case.

**Its second half is implementable in about twenty lines**: in
`TimeDriver.FrameStep`, diff `TicksGame` and `CurTimeSpeed` against the last
frame, open a `clock` span on the first moved tick with `by: external|mod`,
close it on pause or advance start, and journal it. No auto-pause. Non-advance
journal rows are precedented by `Patch_WindowAdd`'s `dialog` rows.

### The smallest sufficient change

**Anchor "since you last looked" to the last delivered screen, not to the
advance's start.** Everything it needs already exists. It also closes a mod-side
hole the round found: rows journaled between advances fall in
`(lastAdvanceEndSeq, startSeq]` and are claimed by no advance's `journal_seq` —
Tony's and Tanya's deaths and the raid letter were gated by nothing.

---

## Round 4 — the chores' four loose ends

### 1. Dormancy: the answer was already on the wire

`GenHostility.HostileTo(Thing, Faction)`'s only dormancy clause is
`IsActivityDormant`, which short-circuits for Anomaly entities only and
deliberately keeps a `CompCanBeDormant`-asleep mech hostile by faction. But the
game's own "the fight is over" rule —
`AutoUndrafter.AnyHostilePreventingAutoUndraft` — uses
`GenHostility.IsActiveThreatToPlayer`, which returns false on `!Awake()`, on
`CompCanBeDormant != null && !Awake`, on `ThreatDisabled` (which `SleepForever`
duty sets), on `CompMechanoid.Deactivated` and on `Fogged()`.

**Measured, not inferred: across 419 stored digest envelopes, 31 read
`danger: "None"` beside `hostiles: 5`.** The mod already publishes
`threats.danger` from `map.dangerWatcher.DangerRating`. The dormancy-aware zero
was one field over for the entire run.

Recommended: publish `hostiles_active` (over
`map.attackTargetsCache.TargetsHostileToColony`, a maintained cache) and
`hostiles_dormant` beside `hostiles`; trigger `after a fight` on the
`hostiles_active > 0 → 0` edge. `threat-pardon` keeps a narrower real job — the
awake, non-approaching hostile — and stops being load-bearing for the trigger.

### 2. The symptom/cause layer

- **`tend until stable` ← `a doctor gone`: stays two rows**, because the run
  needed both — F-S09-16 for the roster, F-S03-16/17 for the stall. But the row
  must lose "repeat until tending is no longer needed": that clause reproduces
  **F-S03-17 verbatim — twelve `prioritize` calls, each restarting the running
  tend and resetting its progress while Aaron bled to 1.0.** Gate the repeat on
  "no `TendPatient` job already targeting this patient", which `JobLine` answers.
- **`research queue`: the layer collapses** once the cause is removed (§4).
- **`butcher` → `corpse care`.** `Pawn.Kill` calls
  `corpse.SetForbiddenIfOutsideHomeArea()` unless the killer was a hunter, so a
  colony kill by a drafted pawn or a turret lies forbidden and no bill can
  consume it. The cause-layer trigger is computable today: a fresh non-pet
  animal corpse, forbidden or unhauled, with the rot clock running. Act
  unforbid → haul → ensure the bill.

### 3. The empty knobs, filled

- **`a doctor gone`** — count is the `WorkCoverage` Doctor `available` row
  against `DoctorFloor = 2`; target priority is the promoted pawn's own minimum
  enabled priority, because `JobGiver_Work` walks by numeric priority first and a
  Doctor at 3 behind any 1 or 2 is exactly `aa4391b`'s outranked doctor. Not the
  agent's to set.
- **`tend until stable`** — medicine is already the patient's `medCare` lever,
  not the chore's. **The route in the sketch was wrong** (see corrections below).
  The one real knob is "wake a sleeper for a non-urgent tend", default on.
- **`research queue`** — head blocked by `prerequisites` → expand them in
  dependency order; by any of the six unclosable reasons → skip with a light.
  Moot if §4 is taken.

### 4. The auto-picker is Dorian's own mod

`Dorian.RandomResearch`, source at `~/projects/rimworld/randomresearch/`,
installed in the bench at `_RimWorld-Agent/Mods/RandomResearch/`, active in
`ModsConfig.xml`. The string is `RandomResearch_NowResearching` =
"Auto-selected research: {0}", emitted with `MessageTypeDefOf.TaskCompletion` —
matching the journal rows' `"def":"TaskCompletion"`. It was never in the 76
decompiled mods because it is never decompiled: its source is local.

By design it picks randomly from the **lowest** `techLevel` with `CanStartNow`,
which is why the run kept getting Greatbow, Harpsichord and Carpet-making, and
its 60-tick poller refills within a second of any `research-stop`.

`RandomResearchSettings.enabled` defaults **true**. The bench config written the
evening before the run set only `skipCompletionPopup`. **Nothing was changed
here — the mod is Dorian's, deliberately installed, and whether random research
is a defect or the point is his call.**

---

## Round 5 and 6 — the two implementations

Both landed on `main` and are **unbenched**. Neither worker launched anything;
neither committed an assembly, so the single rebuild on the merged tree
(`562f553`) is the only artifact.

### `4950f14` — MiniJson stops lying quietly (`1ec3e5b`)

`EnclosureReport.Gaps` was declared `List<Dictionary<string,object>>`;
`MiniJson.Write` matches `case List<object>` — a closed type — so it fell to the
default arm and shipped as a .NET type name, beside a `first_gap` that was
correct. Fixed at the declaration, matching its two siblings.

**The general `IEnumerable` arm was refused, for a good reason:** `Write` runs on
the poller thread, and enumerating a Verse collection there is the file half of
the bridge touching Verse, which CLAUDE.md forbids outright — and some Verse
enumerables rebuild caches or throw if mutated mid-iteration, which would end
"the writer never throws" in the one place that owes every command a result file.
The default arm is now loud instead: `Journal.EmitWarning` naming the type and
the key, deduped.

**Real execution evidence, without the game:** `MiniJson.cs` was compiled
verbatim into a scratch console project alongside the shipped version and both
run over a 36-value corpus. 36 checks, 0 failures, **bit-identical output on
every value that already worked** — escapes, NaN/Infinity, nested containers,
self-referential list and dict, an object whose `ToString()` throws, the depth
guard.

**And a finding worth more than the fix:** `accept/a1644d6-enclosure.py` already
asserts `gaps` is a list, in five checks. Every one fails against shipped code —
so that suite was never run green.

### `c519477` — a bundling verb refuses rather than widens (`7e7dec5`)

The stray-key refusal now covers all five named verbs. **Only `posture` needed a
scope refusal** — one grep shows it was the only verb in the tree that ever
defaulted a pawn set. Read-mode `posture` still reads the whole roster;
write-mode with no scope is refused, and `pawns:"colonists"` remains available to
say colony-wide deliberately. The refusal fires before any lever is written and
after the shape checks, so a malformed lever still reports its own fault.

The precondition it cites, verified by member name:
`PawnColumnWorker_AllowedArea.DoCell` paints one pawn's row, and its only
all-pawns write needs `Event.current.shift`;
`PawnColumnWorker_HostilityResponse` overrides no `HeaderClicked` at all. **The
old default was wider than any click a player can make.**

`levers` is no longer a constant — it is the union of the per-pawn applied rows,
beside `levers_asked`, `levers_unasked` and `also_changed`, in the envelope and
in the journal `action` payload (the audit read `levers` off the journal).

Deliberately not done, both recorded in DESIGN.md: `assign`'s `levers` (it
bundles nine but never widens), and `attack`'s `mode:"auto"` (not a scoping key;
its own ruling).

---

## Corrections this night forced on earlier work

1. **18.3% → 15.3%**, everywhere. The audit's F-XC-4 summed consecutive deltas;
   the interval union is the right method and 368,518 ticks were double-counted.
2. **`tend until stable`'s route was wrong, and I wrote it in.** Last night's
   commit named `DoctorTendEmergency`. Its class is `WorkGiver_TendOtherUrgent`,
   which refuses unless `TicksUntilDeathDueToBloodLoss < 45000` — so it **cannot
   tend an infection**, which is what killed Ellis. The route is
   `DoctorTendToHumanlikes`, after a rescue to bed, because
   `WorkGiver_Tend.GoodLayingStatusForTend` requires `InBed()` for humanlikes.
3. **Two of the sketch's five "default mutates" verbs are misdescribed.**
   `trade-start` does **not** default a trader — `TraderArg` refuses a missing
   one and always has; the defaulting key is `negotiator`. `carry` has **no
   destination argument at all**; the `FindBedFor` default belongs to
   rescue/capture/arrest via the shared `TakeToBed`, which is what the guard
   covers. F-S05-24 says the same.
4. **F-XC-5 is a non-finding**, recorded above so it is not re-derived.

---

## What is waiting for Dorian, and nothing here was applied

**All three of the design calls below were RULED on 2026-09-09 by a focused Fable
round and are now in `COCKPIT.md`. They are recorded here as decided, not open.**

- **RULED — no turret range rings in the ASCII crop; the gauge prints SIGHT.**
  `Building_TurretGun.DrawExtraSelectionOverlays` draws range with no line-of-sight
  term, and on this map the seven discs cover essentially the whole courtyard — so
  the ring would have read green over the exact cells the lancer used for ~200,000
  unopposed ticks while four turrets sat at the south gate unable to see him. The
  replacement is proved against that lancer screen by screen in the round's report.
- **RULED — the ASCII crop is the map; the PNG is Dorian's**, and is not named on
  the agent's screen until `f7b6207`'s fresh-reader acceptance passes. Its only
  reading so far miscounted six rooms as zero and was quoted onward as an answer.
  Reversible on a stated condition.
- **RULED — the research chore is keyed on the agent's QUEUE, not on the picker.**
  `RandomResearch` fills only an empty slot and respects a set project, so the two
  compose rather than fight: keep the queue full and the picker never gets a turn.
  The chore is correct with the mod on, off or absent and never checks for it. This
  supersedes "delete the chore and disable the mod" — **neither**. Whether random
  research is wanted is still Dorian's, and nothing on the bench was touched.
- **Rename `butcher` to `corpse care`** and move it to the cause layer.
- **Rewrite the mod half of `65e7cf9`** to the clock-span rule, and move B and C
  to `70ee75e` as client bugs.
- **Add the 580,000-tick case as its own defence**: the clock running under a
  non-advance reply, which no defence currently names.
- **`rwa` must stop declaring a live bench dead off one file sample**, and
  `Poller.AtomicWrite` should stop deleting before it moves.
