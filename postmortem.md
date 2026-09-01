# postmortem.md — turning a run into artifacts

The pump of the learning system: journal history in, playbook artifacts out,
each landed at the lowest rung of the escalation ladder that removes the
cause. Run it after any colonist death, colony loss, or near-miss that cost
real recovery; run a lighter pass ("what nearly bit") at the end of every run
regardless. The outputs are for the NEXT colony — after a loss, run this
before acting on a new one, or the new one starts unlearned
(`playbook/SESSION-START.md`, position 9).

## Inputs

- the journal (`journal` verb mid-session; the session's `.ndjson` files
  after) — deaths, downed, breaks, letters, alerts, red errors, each with
  tick and seq
- the run dir (`RUNS/<run>/`): transcript, daily digest snapshots, and
  `checklist.ndjson` — the ledger of what the checklists said and did
- the save, for state the journal cannot answer
- once sampling lands (git-bug `2d9a1da`): the sample series. This is the
  step that upgrades every conclusion below from "we died to a raid" to "we
  died to a raid at 14 food-days and 0.3 weapons per colonist" — the
  tradeoff and its cost, which is what makes a lesson converge instead of
  oscillate. Until then, quote single-point readings and SAY they are
  points.

## The procedure

1. **Timeline of harm.** Pull every `death`, `downed`, `mental_break`,
   `red_error`, `dialog` from the journal with ticks; around each, the
   letters, alerts and dev events that preceded it. Alert ticks trail their
   cause by up to ~2,000 ticks during an advance (JOURNAL.md's quantified
   cadence) — treat alert times as "noticed", never "happened".

2. **Walk each harm backward** until the question changes from "what
   happened" to "what would have had to be different". Three questions at
   every step:
   - What state made this possible? (the save and digests answer)
   - What signal existed, and when? — classify against the audit's three
     cases: a genuine leading signal that was ignored; a scoped/self-muzzled
     signal that was trusted (`turn.md`'s trust table); or no signal at all.
   - What did the loop do? `grep` the checklist ledger: did an item flag it
     (`action` with no follow-through?), sit `blocked`, or never exist?

3. **Name the root cause as one of these** — the output rung follows from
   the class:

   | class | meaning | typical output |
   |---|---|---|
   | no-signal | nothing watches this | new checklist item (originate the signal) |
   | mistrusted-signal | a scoped/muzzled alert read as safety | trust-table row + item |
   | policy-gap | signal read, no decided response | lesson deciding the policy — then push it down the ladder |
   | structural | geometry permitted it | template patch |
   | verb-gap | the response was unexpressible | **a spec issue, not a playbook file** — name the missing verb/read on git-bug |
   | knowledge-wrong | a lesson pointed at the wrong symptom | correction per the conflict rule below |
   | execution-slip | the playbook was right and unfollowed | 4.2 compliance finding, not a new artifact |

   **The class picks the typical output; determinism overrides it.** Where
   every branch of the response is computable from state the observers
   already publish, the output is a mod procedure regardless of class — see
   step 5. `no-signal` and `policy-gap` are the two rows that most often
   qualify.

4. **The wealth check — mandatory for any prevention that adds wealth.**
   Raid points scale with colony wealth (`StorytellerUtility
   .DefaultThreatPointsNow` over `PointsPerWealthCurve`): "we died, build
   more guns" raises the threat it answers. Any "build/buy more X"
   prevention records what it costs in wealth and prefers the
   cheap-per-wealth form (spares over new, terrain over turrets, policy over
   property). The game already records wealth AND threat points
   (`HistoryAutoRecorder`) — once 2d9a1da reads them out, plot the actual
   curve instead of arguing about it. [[wealth-buys-bigger-raids]]

5. **Land the outputs at the lowest rung that removes the cause — except
   that a DETERMINISTIC finding goes in the mod** (Evan, 2026-09-01; DESIGN
   decisions log). If every branch of the response is computable from state
   the observers already publish, the output is a mod procedure plus its
   spec issue, whatever lower rung would also have "worked". Notes get
   ignored: a rung that only asks the next session to remember is the
   failure this document exists to remove. The playbook keeps the WHY and
   the policy flags; the mod executes. Land it in one commit: the artifact,
   its INDEX or checklist line, and — if a daily item went over cap — the
   recorded merge-or-retire that made room (4.4). Then verify each output
   surfaces at its `playbook/SESSION-START.md` position; an output the next
   session won't load is not landed.

## The ladder, with promotion criteria

Each rung is a floor, not a queue — a finding may enter at any rung it
already qualifies for. Determinism is the one thing that is not a floor but
a *duty*: a computable response does not get to stop lower down (step 5).

- **observation → prose lesson** when it survives verification (source read
  or bench observation, cited), and is not derivable from what every session
  already loads. Confidence marks what kind of backing it has; `proposed`
  marks invented constants awaiting Evan.
- **prose → checklist item** when ALL of: it names a read and a flag
  (mechanically checkable); its trigger recurs in normal play; missing it
  costs more than watching it; and it fits a moment class — free on the
  digest → `turn.md`, keyed to an act or event → `triggered.md`, otherwise
  `daily.md` **under the cap**, where entry past the cap forces the
  merge-or-retire.
- **checklist → template/policy** when the item verifies the same structural
  property every time and geometry (or a standing policy) can make the
  property true by construction. Graduation DELETES the checklist line for
  template-built instances in the same commit — escalation removes (4.4).
  The popper is the worked example: `templates/power-room` carries it, and
  the daily check now applies only to rooms built otherwise.
- **checklist/procedure → mod** — MANDATORY, not merely available, when
  every branch is computable from state the observers already publish
  (DESIGN 2026-08-31: "deterministic goes in the mod; the playbook carries
  judgement"; ratified over the lowest-rung rule 2026-09-01). The playbook keeps the WHY and
  the policy flags; the mod executes. "Changing it needs a rebuild" is the
  rigour, not the cost. If one branch needs a read that doesn't exist,
  the answer is to NAME the missing read and file it — not to keep the
  procedure in prose. Post-raid (`cc8988c`) is this rung's first tenant.
- **→ rwtest assertion** (final rung, needs 5.1) when the property should
  hold in EVERY run: it becomes a regression test and leaves the agent's
  per-turn surface entirely. 4.4 owns the mechanics.

**Demotion runs the same ladder downhill**: 4.4's retirement pass flags items
not fired while applicable across N runs (the `applies-when` field is what
separates "idle" from "inapplicable"), weighted by each moment class's cost
structure (`checklists/README.md`). Flagged items are merged, demoted to
prose, or retired — recorded in the file, never silent.

**Promotion runs it uphill, in the same pass** (`checklists/README.md` §The
promotion pass, ruled 2026-09-01). Retirement alone can only shrink the
checklists; the pass's candidates are the ledger ids with no `### <id>`
behind them — checks a run invented mid-flight because it needed one — and
each is landed or rejected in writing. Step 2's "did an item flag it, sit
`blocked`, or never exist?" is where a post-mortem generates them, so the
two halves belong to the same procedure and the same commit.

## When lessons conflict

The rule the potato incident wrote:

1. **A contradiction is a verification event, not a vote.** Neither newer nor
   higher-confidence wins by default; the tie-break is INVESTIGATION — read
   the decompiled source for mechanism, run the bench for behaviour. (The
   backlog once held "an unset zone grows nothing" against DESIGN's own
   "assigns and scribes Plant_Potato"; recency favoured the wrong one.)
2. **Mechanism belongs to the source; policy belongs to Evan.** Where his
   stated rule rests on a mechanism the source contradicts, verify and put
   the finding in front of him — the combat-formula handling, generalized.
   Where lessons differ on what to WANT, there is nothing to verify; that is
   a policy not yet decided, and it goes to him as a question.
3. **One winner, one commit, correction kept visible.** The losing claim is
   deleted or folded into the winning file as a correction block — "this
   lesson previously said X; X is false because ⟨citation⟩" — whenever the
   old error TAUGHT something (a symptom that never occurs, a check that
   can't fire). `INDEX.md` never lists two live lessons that disagree.
4. **Check scope before declaring conflict**: two lessons with different
   `applies-when` are not in conflict, they are a map.

## Compliance findings — the execution-slip log

`execution-slip` is the one class in step 3's table with no artifact: the
playbook was right and unfollowed, so inventing an item would paper over the
miss. It still has to be RECORDED somewhere or the class means nothing, and
this is where. Findings are produced by running the auditor over a finished
run — `python3 accept/4.2-play-loop.py RUNS/<run> --repo .` — which is what
makes a silent skip a diff instead of a judgement.

- **M1 `m1-20260831`, day 4 — three daily items missed. A clean slip.**
  `freezer-below-zero`, `production-still-runs` and `apparel-margin` have no
  ledger line for day 4; `armed-roster` does. The sweep was dropped while two
  colonists were dying — `postmortem-trigger` and `roster-change` fired four
  times that day between them. The expensive kind of slip: the day the sweep
  is hardest to run is the day colony state is least likely to be normal. No
  new item is owed; the finding is that the sweep is not optional under load.
  It is also half the argument for the 2026-09-01 mod-rung decision — a
  checklist line executes only if the session has attention to spare, and mod
  code has no attention budget.
- **M1 `m1-20260831`, day 1 — NOT a slip. An auditor/PLAY-LOOP disagreement
  about when a day begins, fixed 2026-09-01.** No daily item logged on day 1;
  the colony-start section ran instead. `accept/4.2-play-loop.py` keys
  `daily-coverage` on the presence of `digests/day-<N>.json` and so demanded
  four day-1 lines, while `PLAY-LOOP.md` keyed the sweep on
  `digest.time.day_of_season` DIFFERING from the last read's — and on a
  session's first read there is no last read, so the sweep the auditor
  demanded could never fire on the opening day. The session obeyed the
  playbook and failed the audit; that is a contract defect, not a lapse.
  Resolved in the auditor's favour: **a session's first read is a day
  boundary.** `PLAY-LOOP.md`, `checklists/README.md` and `daily.md` now say
  so, and on a new colony the colony-start section runs first with any daily
  item it already answered logging `ok` and naming the colony-start line —
  never logging nothing.

The day-4 gap WIDENS in the current tree, deliberately: `barracks-heat` was
promoted into `daily.md` on 2026-09-01 (`checklists/README.md` §The promotion
pass), so the closed M1 ledger now also misses it on days 1, 2 and 4. A closed
run is a record, not something to re-run; a coverage count that moves when the
item set moves is the diff working, not a new failure.

## Worked example — the acceptance dry-run

Synthetic journal (constructed, not from a run — pinned synthetic per the
96d9315 verification; a real fire is 4.3's job). A wooden generator room,
batteries charged, no popper. Excerpts, schema per JOURNAL.md:

```
{"seq":41,"tick":301200,"type":"letter","payload":{"def":"NegativeEvent","label":"Short circuit","text":"A short circuit in an electrical conduit caused an explosion..."}}
{"seq":42,"tick":301200,"type":"message","payload":{"def":"NegativeEvent","text":"Explosion (flame)"}}
{"seq":43,"tick":303050,"type":"alert_on","payload":{"id":"Alert_FireInHomeArea","label":"Fire","priority":"Critical"}}
{"seq":44,"tick":304400,"type":"downed","payload":{"pawn":"Rubio","faction":"player","damage":"Burn"}}
{"seq":45,"tick":305900,"type":"death","payload":{"pawn":"Valentin","faction":"player"}}
{"seq":46,"tick":307000,"type":"alert_off","payload":{"id":"Alert_FireInHomeArea","label":"Fire"}}
```

Running the procedure:

1. *Timeline*: two casualties, ticks 304400–305900; first signal seq 41 at
   301200 (3 a.m. — everyone asleep); the fire ALERT trails the letter by
   ~1,850 ticks — inside the documented 800–2,000 scan lag, i.e. normal, not
   a bug.
2. *Walk back*: deaths ← fire spread through wooden walls ← flame explosion
   at a conduit inside the room ← Zzzt drained the battery bank
   (`DoShortCircuit`) ← room had conduit under its floor, wooden walls, no
   popper. Signal case: the LETTER was a genuine, immediate signal — read
   and not acted on; the alert was late by construction. Ledger shows no
   item watching structural fire coverage: **policy-gap + structural +
   no-signal**, one incident, three classes.
3. *Outputs, lowest rungs that remove the cause*:
   - **lesson** (policy decided): *a Zzzt letter is the ignition event itself
     — respond at the letter, not the alert; our own advance sizing puts the
     alert up to ~2,000 ticks of spread later.* (Would land as a prose file if
     the repo did not already encode the response — see below. Note the lag is
     OURS, not vanilla's: the readout re-checks within ~24 frames, and it is
     `Config.AlertScanFrames` × the ticks-per-frame of a budgeted advance that
     turns frames into thousands of ticks — [[zzzt-letter-is-a-fire-already-burning]].)
   - **checklist item** (signal originated): the `power-incident` event
     trigger — on Zzzt/fire, read `fires` (not the scoped alert), then
     confirm popper coverage standing.
   - **template patch** (structure): popper within fuse range of the
     ignition sources; interior conduit removed to a wall cell; stone
     constraint. Radius math: this bank held 4,800 Wd → blast radius
     `sqrt(4800)×0.05 = 3.5` — right at the Bomb-blast threshold.
4. *Wealth check*: the fix is a 75-steel popper and a relocated conduit —
   near-zero wealth; approved without a damping caveat.

All three outputs already exist in this repo — `checklists/triggered.md
§power-incident`, `templates/power-room.{ir.json,md}` — because this exact
loss is the community's pre-learned canonical case (DESIGN §Learning).
That is the dry-run's point: the procedure, given only the journal, converges
on what the platform already carries; and per `SESSION-START.md`, a fresh
session surfaces all three (positions 3, 5, and 1's index line). A loss the
playbook has NOT pre-learned lands its artifacts the same way — that is the
claim this document exists to make good.
