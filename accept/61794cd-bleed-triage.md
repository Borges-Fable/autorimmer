# Acceptance — 61794cd (the bleed-out clock) and 40ed42f (`work_coverage`, `work-cover`, `triage`)

Runnable driver: `accept/61794cd-bleed-triage.py` — eight bench phases plus
phase 0 (the shape contract) and phase 9 (offline). **No check count is recorded
here on purpose:** counts go stale within a session, and the tables below are
the contract.

    ./accept/61794cd-bleed-triage.py --selftest   # offline; no bench, no game
    ./accept/61794cd-bleed-triage.py --dry-run    # the plan, sends nothing
    ./accept/61794cd-bleed-triage.py              # against a live bench
    ./accept/61794cd-bleed-triage.py --phase 6    # one phase (0 always runs)

Both issues shipped in session 20 (`7b034a3`, `52606d1`) and **neither had ever
touched a bench.** The orchestrator's pre-suite smoke is banked at
`accept/runs/s21-20260901/` and phase 9 asserts against those raw envelopes, so
this file is not the first contact — but it is the first thing that proves the
acceptance sections.

**Exit codes:** 0 all passed · 1 at least one FAIL · 2 a fixture precondition
could not be met, which is not a spec failure.

**`--dry-run` proves the plan, never the paths.** It sends nothing, so every
envelope is empty and every wrong `dig()` path looks fine — which is why it
refuses to print the word *passed*. `--selftest` is the one that has teeth
off-bench: it runs the real assertion helpers over the s21 envelopes and over
deliberately broken copies, and fails if a broken one passes.

---

## Before you start

**It wrecks the colony.** It damages a colonist to 20+ hediffs, destroys another
one's hands, anaesthetises a third, gives a fourth influenza and downs everyone
else. Reload before running it twice.

**It pauses the game at the top of phase 0 and keeps it paused, and asserts
that it did.** Two reasons, both measured:

1. The fixture is perishable. Phase 2 leaves a colonist on a ~7,000-tick bleed
   clock and then asks five more phases' worth of questions. At Normal speed the
   subject dies somewhere in phase 5.
2. `triage`'s `act` is a **snapshot**. On the s21 bench the orchestrator read
   `verdict:"in-time"` with a populated `act`, sent it verbatim, and got
   `cannot-rescue` — because the rescuer had already carried the patient to the
   bed in the seconds between the read and the send, and
   `HealthAIUtility.CanRescueNow` is false for a patient already in one. The
   refusal was the game being right. Every triage read here happens with the
   clock stopped, and `6.10a` re-asserts the pause immediately before the act is
   sent.

**The phases are a sequence, not a menu.** Phase 0 assigns every subject once;
1→2 build one pawn into the Captain shape; 3→4 stage the coverage rows on that
roster; 6 needs 5's repair; 7 needs 6's casualties. `--phase N` alone is for
re-running after a full sweep.

**`advance` escapes are ON, deliberately, and phase 7b is where that is paid
for.** `722c951` makes `advance` refuse on an unread journal delta and halt on
an own-faction downing. This suite's whole fixture is own-faction downings, so
the module-level `advance()` wrapper injects `unread_ok` and
`through_casualties` with a reason naming this file. `raw_advance()` is the
un-escaped form and phase 7b is its only caller.

---

## The fixture, in the order the driver builds it

Everything below is done BY THE DRIVER. Nothing here is a manual step except
"start the bench" and the in-game readout comparison at the end.

| step | phase | call | why this exact form |
|---|---|---|---|
| pause | 0 | `pause` | the fixture is perishable and `act` is a snapshot — see above |
| roster to 6 | 0 | `dev:spawn-pawn {kind:"Colonist", count:N, spread:6}` | a `--quicktest` map starts with **three** and the fixture needs six: a bleeder, a handless doctor, an anaesthetised patient, a sick one, a rescuer and a spare |
| roles | 0 | — | assigned once, by roster id order, and printed. Every later `precondition` names the pawn it wanted |
| light wound | 1 | `dev:damage {mode:"amount", hits:2, amount:2}` | a small bleed, to reach the `WontBleedOutSoon` band (finite clock, `game_shows_clock:false`) |
| heavy bleed | 1 | `dev:damage {mode:"amount", hits:8, amount:2}` × up to 8 | **small `amount`, repeated calls.** The verb's loop is `i < hits && !pawn.Downed && !pawn.Dead`, so one call of `hits:20 amount:6` downs the subject early and lands FEWER injuries. Three calls of `hits:8 amount:2` got the s21 bench to 53 hediffs on a standing pawn |
| the Captain shape | 2 | more `dev:damage`, then `dev:add-hediff {def:"BloodLoss", severity:0.478}` | 0.478 is the severity the M1 post-mortem back-solved for Captain at tick 231,968. It sits BELOW BloodLoss's fifth stage (`minSeverity 0.60`), which is what makes check 2.6a possible |
| one doctor | 3 | `work-priorities {set:[{pawns:[…], works:["Doctor"], priority:0}]}` then `priority:3` on one | the UNDER row is the subject, so it is constructed rather than hoped for |
| the handless doctor | 4 | `dev:damage {mode:"manipulation", allow_bleeding:false}`, then Doctor priority 3 | `HealthUtility.DamageLimbsUntilIncapableOfManipulation`. `allow_bleeding:false` so the subject stays STANDING — a bleeding one goes down and lands in a different bucket |
| the union | 6 | `dev:add-hediff {def:"Anesthetic", severity:1}` on the patient; `{def:"Flu", severity:0.4}` on the sick one | see "the union" below |
| the bed | 6 | `dev:spawn-thing {def:"Bed", stuff:"WoodLog", pos:"pawn:<bleeder>", mode:"direct"}` | see "the bed" below |
| the M1 end-state | 7 | `dev:damage {mode:"until-downed", allow_bleeding:false}` on everyone but two | the only arrangement in which `work-cover` can refuse |

### `--quicktest` map vs a real colony

| phase | needs |
|---|---|
| 0, 1, 2, 3, 4, 5 | **either.** A bare `--quicktest` map is fine; the driver spawns its own colonists |
| 6 | **either, but the no-bed half only fires on a bedless map.** On a `--quicktest` map checks 6.4a–6.4f assert the `no-bed` refusal live; on a colony that already has beds they degrade to a NOTE and phase 9 asserts the banked version instead |
| 7 | **either** |
| 9 | **no bench at all** |

A `--quicktest` map is the better bench for this suite, precisely because it has
no beds.

### The union, and why each pawn carries exactly one clause

`triage` treats a casualty as **downed OR needs-tend OR bleeding**, because
`RimWorld/Alert_ColonistNeedsTend` INVERTS: its getter excludes pawns needing
rescue, so the alert goes OFF the moment the patient goes DOWN. A verb keyed on
the alert loses the case that matters. The strongest proof is three pawns each
satisfying exactly ONE clause:

| pawn | fixture | downed | needs_tend | clock | verdict |
|---|---|---|---|---|---|
| patient | `Anesthetic` severity 1 | **true** | false | `null` | `no-deadline` |
| sick | `Flu` severity 0.4 | false | **true** | `null` | `no-deadline` |
| bleeder | phase 2's Captain shape | false | true | **finite** | `in-time` / `too-slow` |

`Anesthetic` is the right downing agent because it is `isBad:false` with
`initialSeverity 1` and zeroes Consciousness: it downs **without wounding**, so
there is nothing to tend and no bleed rate, which is the only way to get a
downed-and-nothing-else row. `dev:damage {mode:"until-downed"}` cannot produce
it — the injuries it leaves are tendable.

### The bed, and the trap it closes

**`triage`'s `act` path is unreachable on a bare `--quicktest` map.** There is
no bed, `TakeToBedGate("rescue", …)` ends in `RestUtility.FindBedFor`, and every
candidate comes back:

    {"gate": "no-bed",
     "reason": "No reachable, un-reserved non-prisoner bed in safe temperature."}

…so every verdict is `no-rescuer` and `act` is never published. Measured on the
s21 bench (`accept/runs/s21-20260901/18-triage-downed.json`), and it would cost
a session to discover from inside a suite. The driver asserts that state
deliberately at 6.4, THEN spawns one bed.

**`no-bed` is not what EVERY candidate reads, and 6.4 used to assume it was.**
`TakeToBedGate("rescue", …)` opens on `HealthAIUtility.CanRescueNow` ->
`WantsToBeRescued`, whose FIRST clause is `!pawn.Downed`; the bed lookup is its
LAST. So the refusal depends on the PATIENT:

| patient | what every candidate that clears `ProviderGate` reads |
|---|---|
| DOWNED, not in bed (the anaesthetised one) | `no-bed` — the sentence above |
| STANDING (the bleeder, the sick one) | `cannot-rescue` — nobody carries a pawn who is on their feet |

Both are `no-rescuer` verdicts and both are honest answers to "why is nobody
coming"; they are simply different answers. 6.4a asserts the verdict across
**every** casualty, 6.4b2 asserts the standing half by name, and 6.4c/6.4d pick
the DOWNED row rather than `casualties[0]` — which this fixture makes the
standing bleeder, and which is how the pair failed on the s21 bench while the
mod was correct.

The bed goes on the **bleeder's** cell (`pos:"pawn:<id>"`, `mode:"direct"`),
because `in-time` is only an honest verdict when the carry leg is real, and
`mode:"near"` would slide the bed somewhere nobody asked about (GenPlace's
radial search).

---

## What each phase settles

| acceptance bullet | issue | phase | how it is settled |
|---|---|---|---|
| `BloodLoss` appears in a read from a pawn with >20 hediffs, **proved by construction not inspection** | 61794cd | 2 | 2.4a–2.4d prove the truncation is real (`hediffs_more >= 1`, `total = rows + more`), 2.5a–2.5b prove `BloodLoss` is row 0 anyway |
| `hediffs_more` still honest | 61794cd | 2 | 2.4c/2.4d — the arithmetic closes against the capped list |
| `ticks_until_bleedout` for a bleeder, omitted or nulled for a non-bleeder | 61794cd | 1 | 1.1a–1.1d and 1.6a–1.6c, **on one pawn in both states**, with `shape()` before every null assertion |
| the number matches the game's own readout | 61794cd | 1 + manual | 1.1j / 1.3c / 1.6d assert the GATE three ways; the in-game text comparison is the manual step below |
| Captain's four reads replayed | 61794cd | 2 | **not decidable** — a shape reconstruction, stated as one in the suite's own `note("2.0")`. See below |
| `work_coverage` with an enabled-and-capable count against a stated floor; Doctor's floor is 2 | 40ed42f | 3 | 3.3b/3.3c/3.3d, plus 3.4b for the nine `requireCapableColonist` types at floor 1 |
| the block distinguishes "enabled but incapable" from "capable", **proved with a real pawn** | 40ed42f | 4 | 4.4b–4.4d (`enabled > available`, `capable` unchanged) and 4.5a–4.5e (the row, and the capacity it names) |
| a roster change below the floor promotes the best capable pawn, journalled as an act | 40ed42f | 5 | 5.3a–5.3h for the promotion and the independent digest; **5.4a–5.4d read the act out of the LEDGER**, not out of the verb's own stamp |
| when NO capable pawn remains the verb refuses explicitly | 40ed42f | 7 | 7.2 (`too-few-candidates`) and 7.3 (`no-candidate`), each with the counts that decide the follow-up |
| a downed colonist with a capable rescuer causes a forced `rescue`, journalled, not a priority change | 40ed42f | 6 | 6.9 asserts `act`'s shape, **6.10 SENDS IT VERBATIM** and asserts `job_def:"Rescue"` and a journal seq |
| no capable rescuer: the procedure says so explicitly | 40ed42f | 6 | 6.4a–6.4f — `no-bed` on the downed row in the game's own sentence, `cannot-rescue` on the standing one |
| `checklists/` item 7 shrinks; `[[one-doctor-is-zero-doctors]]` keeps only the WHY | 40ed42f | — | done in this branch, not by the suite |
| the advance refusal when travel exceeds the clock | 40ed42f | — | **not built** — `52606d1` deliberately left part 3 to `722c951`'s machinery. `triage` publishes both numbers and the verdict; phase 6.7 asserts the comparison |
| replay tick 231,968 and show the procedure reaches for `rescue` | 40ed42f | 6 | reconstructed, not replayed — same reason as 61794cd's replay bullet |

---

## The three things this suite does NOT prove, and why

### 1. The in-game text — ORCHESTRATOR, MANUAL

A suite cannot read RimWorld's UI. What it CAN do is reproduce
`RimWorld/HealthCardUtility.DrawHediffListing`'s gate clause for clause and
assert the mod's reproduction against it, which `assert_card_gate()` does on
every health read. **The suite never claims it compared against the text.**

The comparison is yours. After a full run, with the game still paused:

1. Note the numbers the suite printed for the bleeder at check 1.6 —
   `bleed_rate` and `ticks_until_bleedout`. They are also in the phase-1 line
   `heavy bleed: rate=… ticks=…`.
2. In game, click the bleeding colonist (the driver prints its id and the
   roster's names at phase 0), open the **Health** tab.
3. The bottom line of the hediff listing reads
   `Bleeding: <pct>/day (<time> before death)`. It is drawn at all only when
   `bleedRateTotal > 0.01`.
4. **The percentage** should equal `bleed_rate × 100` — the mod publishes the
   same `BleedRateTotal`, rounded to 2dp.
5. **The time** is `ticks_until_bleedout` run through
   `ToStringTicksToPeriod()`: 2,500 ticks per hour, 60,000 per day. So
   `7,223` ticks reads as roughly `2.9 hours`, and `16,317` as about
   `6.5 hours`. Anything within a couple of minutes is the rounding.
6. Then take the SAME colonist after phase 1's light wound if you want the third
   branch: a clock above 60,000 ticks prints `(Won't bleed out soon)` and NOT a
   figure, while `ticks_until_bleedout` is still a real number. That divergence
   is `game_shows_clock:false` with a finite clock, and it is correct.

Record the two numbers and the tab text in the run log. **That is the fourth
acceptance bullet of `61794cd` and nothing in `accept/` can discharge it.**

### 2. Captain's four reads, replayed — NOT DECIDABLE

`61794cd`'s fifth bullet asks for the reads in `RUNS/m1-20260831/` replayed
against the new surface. The transcript records the **output** of a pawn state,
not the state; the run left no save; the pawn no longer exists. The issue's own
comment #2 says so. Phase 2 builds his **shape** — 20+ bleeding injuries plus
`BloodLoss` at 0.478 — and the suite says it is a reconstruction in
`note("2.0")` and in its own docstring. It is not claimed as a replay anywhere.

What the reconstruction adds over the transcript: the s21 bench dropped **34**
hediffs where Captain's worst read dropped 19, and `BloodLoss` came back at
**row 0** anyway.

### 3. The Deathless branches — NO ROUTE

`bleedout.outcome` can be `none` (a `HasPreventsDeath` hediff), `coma` (the
Deathless gene with an intact brain) or `death`. Only `death` is reachable: no
`dev:` verb adds a gene, and `61794cd` #2's most interesting pawn — Deathless
with a **destroyed brain**, where `outcome` says `death` and `game_shows_clock`
still says the widget prints `(Deathless)` — needs both. `deathless_gene` is
asserted false throughout, so the branch is **unexercised rather than silently
assumed**, and check 9.4h exercises the python side of the gate offline.

---

## Traps, each of which cost somebody something

* **`pawn` takes `id`, NOT `pawn`.** `--pawn` comes back `bad-args`, "missing
  required arg 'id' (number)". Cost the s21 smoke a call.
* **`dev:heal` does NOT clear `BloodLoss`** in its default `mode:"injuries"`:
  that is `HealthUtility.HealNonPermanentInjuriesAndRestoreLegs` and `BloodLoss`
  is not a `Hediff_Injury`, so it survives — and with the bleeding gone it keeps
  RISING. The s21 bench healed a pawn from 0.562 to **0.623**. `mode:"full"` is
  the one that removes it (it drops every `isBad` hediff). This suite never
  heals; it uses a fresh subject per role.
* **`dev:damage {mode:"amount"}` stops at Downed** — `i < hits && !pawn.Downed
  && !pawn.Dead`. Small `amount`, repeated calls. `hits` is capped 1..20.
* **No bed means no `act`.** See above.
* **`act` is a snapshot.** See above.
* **`eq(..., None)` passes on an ABSENT key.** Half of what `61794cd` asks to be
  proved is a key that is present and null, so every null assertion in the
  driver is preceded by a `shape()`, and phase 0 is a shape contract over every
  path the later phases lean on. Check 9.2a demonstrates the trap rather than
  describing it.
* **`work_coverage`'s cap cannot fire on this bench.** `WorkCoverage.RowCap` is
  14 and a full-DLC bench publishes 12 essential types (the nine
  `requireCapableColonist` ones, plus `Childcare` from Biotech, plus `Doctor`,
  plus any modded type that sets the flag — the s21 bench had `Diplomat`). So
  "the cap only ever drops rows that are fine" is proved **by construction**
  (3.5a: every name in `under` is present in `rows`; 3.6a: `more == 0`), and
  phase 9 re-derives both numbers from the source so the day a mod set pushes
  the count past 14 the argument is re-examined rather than assumed.
* **Manual priorities are OFF on a fresh bench** (`use_priorities:false`), so
  only priorities 0 and 3 are legal. The driver never sends anything else; a
  `work-cover {priority:2}` would be refused for that reason and not for the one
  you meant.

---

## Findings — shipped-mod defects this suite REPORTS rather than asserts

They print as `FINDING` lines and are repeated in the summary. They are
deliberately **not** checks: the exit code answers "were the acceptance bullets
met", and a suite that goes permanently red over a metadata string teaches the
next session to ignore its own colour. All three are filed on the issues.

| id | what |
|---|---|
| **3.7** | `digest.work_coverage.order` says `"under-first, then natural-priority-desc"`, but `WorkCoverage.Section` emits rows in ONE pass over the natural-priority-sorted list and appends under-rows **inline**. On the banked s21 digest the only UNDER row (`Doctor`) is at index **1**, behind `Firefighter`. A caller trusting the string reads `rows[0]` as the worst problem. Phase 9's 9.6f re-asserts it from the banked envelope so it cannot be argued away |
| **4.7** | `enabled_but_incapable` is built inside `if (r.Under)`, so a colony with two available doctors and a handless third is never told about the third. Arguably correct (it is a diagnosis of a problem, and there is no problem) — but "you have a doctor who cannot tend" is the class of thing the M1 post-mortem exists about. **Still open, and deliberately narrower than it was:** the INNER guard `if (r.Impaired.Count > 0)` was a different defect and is fixed — the list is now always present, empty when nobody is impaired (7.2h, 3.3q, 9.8d, 9.11a). What 4.7 still asks is whether a COVERED row should carry the diagnosis at all, which is the digest's three-field byte budget and is a design decision, not a shape bug |
| **5.2c** | a clean `dry_run` reports `action.journal_seq: 0` carrying `Stamp(0)`'s sentence *"NOT WRITTEN — the journal writer is closed"*. The writer is not closed and nothing was mutated: the emit guard is `(repaired>0 && !dryRun) \|\| stillUnder>0`. `NoStamp()` is the shape this case wants |

Plus one the orchestrator filed first and this suite deliberately does not
assert either way: **git-bug `58794e4`** — `work-cover {dry_run:true}` reports
`coverage_after` as the coverage BEFORE the repair. Asserting the current value
would make this suite go red the day it is fixed; asserting the fixed value
would make it red today. What IS asserted is the thing that is true either way
and that the bullet needs: **the dry run mutated nothing**, proved from an
independent `digest` (5.2a) and from the journal (5.2b).

---

## A ruling this suite makes, rather than queueing

**`bleedout.outcome` is populated even when `ticks` is null, and that is correct
as designed.** The orchestrator raised it off the s21 smoke (reads 05 and 11
both show `outcome:"death"` with `ticks:null`) and asked for a ruling.

The field answers *what happens WHEN the clock runs out* — a property of the
pawn's **death path**, computed from `Pawn_HealthTracker.ShouldBeDead` and the
Deathless branch above it. It is true of a pawn who is not bleeding at all: a
Deathless pawn with an intact brain reports `coma` whether or not anything is
currently killing it. `ticks` is the only field that says whether there IS a
deadline, and it says so unambiguously by being `null`.

Nulling `outcome` alongside `ticks` would make `null` mean both "no deadline"
and "could not compute", which is precisely the ambiguity the block exists to
remove — the same argument that made `ticks` `null` rather than `int.MaxValue`.

The suite therefore asserts it: **1.1h** requires `outcome` to be one of
`none`/`coma`/`death` on a pawn with no clock at all, and **1.1i** pins it to
`death` for a plain colonist. Recorded on `61794cd`.
