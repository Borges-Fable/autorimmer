# checklists/ — when the agent looks, and why

The spec said "morning checklist". This directory is the deliberate answer to
Evan's question about that phrase: **a morning is the wrong unit for this
agent, and here is what replaced it.**

## The shape decision

The agent has no mornings. Its life is `act → advance(until …) → read journal
delta + digest → think → act`, and the only moments that exist inside that loop
are: an act it is about to take, an event that halted the advance, and the read
it performs every time control returns. A once-a-day sweep is a *player's*
habit — a player visits the running game each day. This agent is visited BY the
game every time an advance returns, with a fresh digest in hand. Bolting a
daily ritual onto that rhythm produces exactly the failures a checklist exists
to prevent:

1. **It pays for what is already free.** Food-days, power margin, meds count —
   the digest carries these at every read. A morning item that queries them
   once a day is slower AND more expensive than the loop's own habit.
2. **It is a day late for act-keyed checks.** "Queued a bill → confirm someone
   other than the patient can work it" is worthless on a cadence; the mistake
   happens at act time and sits silent (see [[who-will-actually-do-it]]). In
   practice such checks get remembered at act time or not at all — meaning they
   were never cadence items to begin with.
3. **It is worse than late for event responses.** Vanilla's own tutor teaches
   ForbiddingDoors/AllowedAreas at Critical **the moment** toxic fallout or a
   manhunter pack begins (`GameCondition_ToxicFallout.Init`,
   `IncidentWorker_AggressiveAnimals` — severity per call site). The game is
   proactive exactly here; a daily sweep would respond more slowly than the
   system the checklist exists to out-run.

So the checklist is three files keyed to the moments the loop actually has:

| file | evaluated | recurring cost | holds |
|---|---|---|---|
| `turn.md` | every read | zero extra queries | thresholds over the digest already in hand |
| `triggered.md` | when its trigger fires | one drill-down per firing | act-keyed checks, event-keyed responses, colony start |
| `daily.md` | first read after a day boundary, and the session's first read | the only unconditional queries | slow drift with no event, no alert, and no digest field |

**Why a daily rung survives at all.** Three reasons, all mechanical. The
variables it watches (freezer temperature, armament vs roster, apparel decay)
move on a scale of days, so polling faster buys nothing. The 4.2 loop already
caps early advances at about one in-game day, so a day boundary is a return
the loop was going to make anyway — the sweep piggybacks on an existing pause
and never forces one. And one verdict per item per day is exactly the evidence
row 4.4's retirement ledger needs. Detect a boundary from the read in hand:
`digest.time.day_of_season` differs from the last read's. No new mechanism.
**A session's first read is also a boundary** — there is nothing for it to
differ from, and the loop snapshots that day like any other, so the sweep is
owed there too. (Under the older wording M1 ran no sweep at all on day 1,
which `accept/4.2-play-loop.py` correctly failed: the auditor keys coverage
on the presence of the day snapshot. Resolved in the auditor's favour,
2026-09-01 — `postmortem.md` §Compliance findings.)

**The checklist is the missing `condition` matcher, run by hand.** `advance
until:` halts only on events — things that HAPPEN. Nothing fires when a
continuous value crosses a line; that is spec 1.6 (`fc287ba`), unbuilt. Until
it lands, every `turn.md` trip-wire is a state predicate the agent evaluates
itself at each return. Each one names the predicate it will become. That is
the file's exit strategy, not a footnote.

**Checks are supposed to leave these files.** The escalation ladder
(`postmortem.md`) runs prose → checklist → template/policy → mod procedure →
rwtest assertion. A check that hardens graduates out: post-raid response is
already decided INTO the mod (git-bug `cc8988c`; DESIGN 2026-08-31,
"deterministic goes in the mod"), popper coverage is baked into
`templates/power-room`, and 4.4's final rung moves proven invariants to
rwtest. A checklist that only grows is failing.

## Item grammar

Items are markdown with a fixed grammar, so the cap and the ledger are
readable without parsing prose (4.4's contract):

    ### <item-id>
    - when: turn | act:<moment> | event:<signal> | daily [every N days]
    - applies-when: <predicate>            (omit = always; drives the n/a verdict)
    - read: <rwa command and field>        (every item names its query — invariant)
    - flag: <the reading that warrants action>
    - act: <the response, and its damping caveat if it adds wealth>
    - why: <one line, with citation>
    - retire-when: <the evidence that ends the item>

`grep '^### '` over a file is its item set. Constants marked *(proposed)* are
the author's calibration awaiting Evan or awaiting run data — the same
discipline as the combat-role formula in the playbook.

## The run ledger

The loop (4.2) appends one NDJSON line per evaluation to
`RUNS/<run>/checklist.ndjson`:

    {"day":3,"tick":184200,"item":"freezer-below-zero","verdict":"ok","reading":"-4.5C"}

- `verdict`: `ok` | `action` (flag tripped; `note` says what was done) |
  `blocked` (query unavailable — say why) | `n/a` (applies-when false).
- A silent skip is a MISSING line — 4.2's "no silent skips" invariant becomes
  a diff between this file and the item set, not a judgement about prose.
- `turn.md` trip-wires log only when they fire (`action`); logging ~10 `ok`
  lines per read would bury the file. Daily and triggered items log every
  evaluation, whatever the verdict.
- 4.4's "when did this last fire" is then `grep <item-id> RUNS/*/checklist.ndjson`
  — a computation, not bookkeeping.

**The id space is closed, and that is why there is no incident class.** Every
`item` a run logs must be a `### <id>` in one of these three files, or
`colony-start-<n>` inside the colony-start step count:
`accept/4.2-play-loop.py`'s `item-ids-known` check enforces exactly that, and
its verdict enum is the four above and nothing else. M1 (`m1-20260831`)
invented three ids mid-run for real observations that had nowhere to go —
`barracks-heat`, `postmortem-trigger`, `time-control-drift` — and the run
FAILS `item-ids-known` on all three. Ruled 2026-09-01: **a run-level incident
does not get a new class; it gets the moment class it actually has.** Room
heat is slow, event-less drift → `daily.md`. A death and a lost time control
are events → `triggered.md`. A schema with a fifth verdict or a parallel
incident file would have bought a second, unaudited ledger; what session 12
actually lacked was the promotion pass below, without which any id invented
mid-run reads as a schema gap for as long as nobody lands it.

**Retirement pressure is not the same for every moment class** (recorded here
so 4.4 builds on it): a daily item costs its query every day whether or not it
fires, so silence accrues pressure against it. A turn trip-wire costs nothing
until it fires, so silence is *success* — pressure against it is only its
share of the reader's attention. An act/event item sits in between. 4.4's
pass should weigh recurring cost × silence, not silence alone.

## The cap

**The hard cap binds `daily.md` only** — it is the one file with
unconditional recurring cost. Cap: **7 items** *(proposed — 4.4 ratifies; what
fits in one attentive read)*. Current count: 5. Adding an item past the cap
forces a recorded merge-or-retire in the same commit, and the file names what
was displaced — the digest's own budget rule ("an uncapped section is the
defect"), applied to attention. `turn.md` is capped by the digest byte budget
it annotates; `triggered.md` is capped per trigger by the same attentive-read
bar, enforced at review rather than by a number.

## The promotion pass

4.4 (`d32eadd`) specifies a retirement pass and no promotion pass, so the
checklists can only ever shrink: nothing turns a repeated observation INTO an
item, and the thing that does happen instead — a session inventing an id
mid-run — lands nowhere and fails the audit (§The run ledger). The symmetric
half, ruled 2026-09-01 and recorded here for 4.4 to build on:

**Run it in the same pass as retirement** — at a post-mortem, or at the end of
a run — over the same evidence: `RUNS/*/checklist.ndjson` and the run
summaries.

- **The candidate set is computed, not remembered.** Every ledger `item` with
  no `### <id>` behind it, plus any check a summary describes in prose only:
  `grep` the ledgers, diff against `grep '^### '` over the three files. An id
  a session invented is a REQUEST for an item, filed at the moment the need
  was felt — the most reliable evidence this system produces, and today it is
  thrown away.
- **Two occasions, then promote or reject — in writing.** A candidate seen on
  two separate days or in two runs is landed as an item, or the file records
  in one line why not, in the same commit. Silence is the failure being
  fixed; it is the same rule as retirement's "recorded, not silent", pointed
  the other way.
- **It lands at its moment class**, and the ladder still applies: free on the
  digest → `turn.md`; keyed to an act or event → `triggered.md`; otherwise
  `daily.md` under the cap, where a promotion past the cap fires the
  merge-or-retire like any other add. If every branch of the response is
  computable from published state, it is not a checklist item at all — it
  goes to the mod rung (`postmortem.md` step 5).
- **Promotion is the cheap direction to be wrong in.** A promoted item starts
  logging immediately, so the retirement pass sees its rows from the next run
  onward and undoes a bad promotion on the same evidence. The asymmetry is
  deliberate: a missing item costs a colonist, a spurious one costs a line of
  attention per day until the next pass.

Worked example, and the pass's first output: the three M1 ids in §The run
ledger. Each fired at least twice, none had a file, and all three landed on
2026-09-01 — `barracks-heat` on `daily.md`, `postmortem-trigger` and
`time-control-drift` on `triggered.md`.

## Deliberately not here

- **Mood.** `Alert_MajorOrExtremeBreakRisk` is threshold-based AHEAD of a
  break, the digest publishes `mood_arrow` per colonist, and the colonist cap
  already sorts the roster by attention (mood included). Covered; a mood item
  would re-derive the one part of vanilla's attention model that genuinely
  leads. (Curriculum audit, 96d9315 comment 1 — confirmed in the verification
  pass.)
- **Seasonal clothing forecasting.** `Alert_NeedWarmClothes` looks 3 twelfths
  ahead via `GenTemperature.AverageTemperatureAtTileForTwelfth` — a real
  leading indicator; lean on it. Its one trap is inventory, not forecast: it
  counts only apparel `IsInAnyStorage()`, so a parka on the ground is
  invisible to it — which is the unforbid/haul sweep's job, not a new item.
- **Dialog handling, undraft discipline, advance sizing.** Loop mechanics, not
  checks — they belong to 4.2's skill text and are already amended there.
- **Anything needing a trend.** See the blocked section at the bottom of
  `daily.md`; nothing samples yet (git-bug `2d9a1da`), and a single-point read
  dressed up as a trend is a lie with a citation.
