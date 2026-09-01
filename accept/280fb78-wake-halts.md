# Acceptance — 280fb78 (the wake halts, `alert-mute`, `through_news`)

Runnable driver: `accept/280fb78-wake-halts.py` — seven bench phases plus
phase 0 (the shape contract) and phase 8 (offline). **No check count is recorded
here on purpose:** counts go stale within a session, and the tables below are the
contract.

    ./accept/280fb78-wake-halts.py --selftest    # offline; no bench, no game
    ./accept/280fb78-wake-halts.py --dry-run     # the plan, sends nothing
    ./accept/280fb78-wake-halts.py               # against a live bench
    ./accept/280fb78-wake-halts.py --phase 4     # one phase (0 always runs)
    ./accept/280fb78-wake-halts.py --day-ticks 15000   # the quick phase 6

**Exit codes:** 0 all passed · 1 at least one FAIL · 2 a fixture precondition
could not be met, which is not a spec failure.

**`--dry-run` proves the plan, never the paths.** It sends nothing, so every
envelope is empty and every wrong `dig()` path looks fine. `--selftest` is the
one with teeth off-bench: it runs the real assertion helpers over the envelopes
banked at `accept/runs/s21-20260901/`, including a digest that genuinely predates
`alerts.muted` and a journal carrying a real `letter` and a real `alert_on`
payload.

**Nothing in this file has met a bench.** It was written in a worktree by a
worker who is forbidden to launch the game.

---

## What changed, in one paragraph

Four halts fired unconditionally — `casualty`, `dialog`, `red_error`, and
`NoticeRedError`'s own path. Everything else sat inside `switch (until)`, so
`advance {ticks:60000}` slept through a raid landing, a trader arriving and
leaving, a quest expiring, an inspiration expiring, `Alert_LowFood`, a fire and a
prisoner escaping. Now every letter and every `alert_on` halts an advance whether
or not the caller asked, `until:{…}` still works as an explicit wait and wins the
naming when both would fire, `alert-mute` is how a chronic alert stops waking the
run, and `through_news` is the per-call escape.

**The table the issue's own measurement table got wrong.** It listed a `thermal`
halt among the four unconditional ones. There is no such halt: `grep -n 'Halt('
Source/AutoRimmer/TimeDriver.cs` returns eleven call sites and none of them names
`thermal`. The governor steps the SPEED down one notch (`OneNotchDown`,
`speedChanges[].by == "thermal"`) and never stops an advance. The fourth
unconditional halt is `red_error`'s own path in `NoticeRedError`, which sits
upstream of the journal's dedupe cap. Nothing turns on the correction — the four
that fire unconditionally are still four — but the issue's table should not be
quoted as source.

---

## Before you start

**devMode = True.** Every fixture step is dev-gated.

**It dirties the bench.** It sends letters (including a `ThreatBig`, which is a
real letter and appears in the colony's own letter stack), injects fixture
alerts, mutes and un-mutes them, and phase 6 burns a full in-game day. Phase 7
downs one colonist on purpose and heals them afterwards.

**If it crashes mid-phase**, two calls clean up:

    alert-mute {release_all:true}
    journal-selftest {steps:["alerts-clear"]}

and `journal-selftest {steps:["alert-at"], alert_delay_ticks:600000}` disarms a
pending alert injection.

**Phase 6 is slow by construction** — it advances a full in-game day, in
halt-bounded segments, twice. `--day-ticks 15000` is the quick version and still
proves the corroboration rule; `--day-ticks 60000` is the acceptance bullet.

---

## The escape split, which is the design of the file

| helper | `through_casualties` | `through_news` | used by |
|---|---|---|---|
| `advance()` | **always** | never | every phase but 5 |
| `advance_riding_news()` | always | **always** | phase 5 only |

`through_casualties` rides on every advance because a colonist falling mid-phase
halts with `reason:"casualty"` and would mask the letter or alert halt under
test — and that halt has its own suite (`722c951`). `through_news` rides
*nowhere* else, because it is the thing being measured. A wrapper that quietly
escaped both would hide the subject, which is exactly what `722c951`'s header
warns about.

---

## The two fixtures, and why the shipped ones could not be used

**`alert-at`** — an alert injected from inside a TICK. The shipped `alerts` step
cannot drive this acceptance: `AlertScanner.Tick` runs from
`GameComponentUpdate`, i.e. every FRAME **including while the game is paused**,
so an alert injected from the command drain is diffed and journalled *before the
next advance even starts* and halts nothing. Armed like `722c951`'s `down-at`,
fired from `GameComponentTick`, so the injection lands mid-advance and the
scanner's next cadence frame emits `alert_on` there.

`alert_critical` selects `Alert_AutoRimmerFixtureCritical` instead of
`Alert_AutoRimmerFixture`. Two classes means **two distinct ids in one advance**,
which is what makes phase 4 a real proof: mute one, leave the other, and the halt
that does or does not arrive is the whole evidence.

**`letter_def`** on the existing `letter` step, defaulting to the `NeutralEvent`
it has always sent. The acceptance is "a letter of ANY def halts, including
`NeutralEvent` and `PositiveEvent`", and the ruling behind it is that a positive
letter is exactly the one a severity filter would have slept through. A fixture
that can only send one def cannot test the rule that says the def does not
matter. Resolved through the `DefDatabase` and **refused** on a miss: a
silently-wrong def would send a `NeutralEvent` while the suite believed it had
proved `PositiveEvent`.

---

## What each phase settles

| phase | settles |
|---|---|
| 0 | `digest.alerts.{muted,muted_count,muted_live}` and `active[*].muted` exist; `alert-mute {}` publishes its listing; both fixtures answer; a plain advance publishes **no** `through_news`, **no** `news_rode_past`, **no** `muted_alerts` |
| 1 | a `NeutralEvent` **and** a `PositiveEvent` letter each halt an advance that asked for nothing; the halt names the def, the label, the tick and the seq; `armed_by == "default"`; and it stopped **short of its own bound** |
| 2 | an `alert_on` halts; the halt names the id **and the priority**; proved with a Medium and a Critical fixture alert so `priority` is a field and not a constant |
| 3 | the collision. `until:{letter}` still halts with `armed_by:"until"`; a `ThreatBig` under `until:{threat}` reports **`threat`, not `letter`**; a letter the explicit wait did not want still wakes with `armed_by:"default"`; **a letter wakes an advance armed on a `condition`**; and `until:{alert:"X"}` fires even when X is muted |
| 4 | `alert-mute` both ways on one fixture: refusals (no reason, whitespace reason, empty ids, unknown id with `did_you_mean`), the act journalled with its reason, `digest.alerts.muted` carrying it, a muted alert **not** halting while `muted_alerts.count` reports what it swallowed, and the release restoring the halt |
| 5 | `through_news`: reason required and non-string refused, rides past a letter to the bound, echoed on the envelope, `news_rode_past.count` published, journalled as an `action` row — and it does **not** defeat an explicit `until:{letter}` |
| 6 | a day-long advance, and no halt fires spuriously (below) |
| 7 | four halts, four distinct `data.reason` tokens, measured on the bench |
| 8 | offline: the absent-vs-null trap on a real pre-`muted` digest, and every dig path phases 1–4 use, run against real banked `letter` and `alert_on` payloads |

### Phase 3.4 is the sharpest single check

`CheckUntilKeys` refuses a **second** matcher — "until takes ONE matcher and was
given 2". So before this issue the opt-in halts were not merely optional, **they
were mutually exclusive**: an agent already waiting on `until:{condition:{…}}`
could not *also* ask to be woken by a raid. It had to choose which question to
ask, and there was no workaround available even to an agent that knew the hazard
and wanted to guard against it. Phase 3.4 asserts the refusal still stands *and*
that a letter now wakes a condition-armed advance anyway.

### What "not spurious" is given to mean, in phase 6

"It did not halt" is unprovable on a colony that is alive, and asserting it would
make this suite flaky in the one direction that reads as a spec failure. So the
rule is testable instead: **every halt must name a journal row that exists, of
the matching type, at the seq it published.** The phase advances a full day in
halt-bounded segments, reads the journal after each (which is the discipline
`722c951` imposes anyway), and looks up `halted_seq`. A halt with nothing behind
it is the failure. It then runs the strict form — with the wake escaped, a
day-long advance must **complete on its bound** — and reports the wake tally,
which is a number nobody has yet. The issue's own measurement was 3 letters and 6
`alert_on` in 13,667 ticks on a bench being actively wrecked by a suite.

---

## Two other suites were edited, and why

Both are consequences of the wake being unconditional, not defects in either
change.

* **`accept/fc287ba-until-state.py`** is the most exposed file in `accept/`.
  Phase 4 waits ~3,180 ticks for colonists to build 22 elements and asserts
  `reason:"layout"`; any letter or alert during that build would come back
  `reason:"letter"` and fail a check about something else. Its single `advance()`
  wrapper already `setdefault`s the other two escapes; `through_news` joins them,
  with the reason stating that the wake is not that suite's subject.
* **`accept/722c951-advance-halt.py`** is 96 PASS / 0 FAIL on a bench and must
  not regress. Its checks `1.7` (`reason == "ticks"`), `3.4` (`"casualty"`), `4.4`
  and `5.3` (`"condition"`) are all beatable by a letter. `through_news` is set
  in `_bound()`, which every advance funnels through — the one blanket escape
  that file carries, and the header there says why it is the exception rather
  than a retreat: the news wake is not its subject. **It is safe for the one
  place that file genuinely wants a letter:** phase 1 arms `until:{letter:true}`,
  and an explicit matcher is evaluated before the wake and wins, so
  `through_news` cannot suppress it. Phase 5.4 of *this* suite asserts exactly
  that, so the assumption is tested rather than trusted.

`accept/4.2-play-loop.py` gains `through_news` in its escape list and a new
`advance-wakes` check: the alert or letter that woke each advance is reported by
name, and a `alert_on` swallowed by a standing mute is a WARN. **None of them
FAILs**: being woken is the system working, a mute is a recorded decision, and
the escape is legal per call. Re-advancing straight off a wake is deliberately
*not* a second FAIL — a letter is a journal event, so the next advance is already
refused `unread-journal` unless the loop reads or escapes, and double-charging
one mistake makes the audit worse.

---

## Traps

1. **The bench competes with the fixture.** The wake is unconditional, so the
   colony's own letters and alerts halt advances too. An assertion on "the first
   halt" is flaky in the direction that reads as a spec failure. `wake_hunt()`
   keeps advancing in bounded segments until a halt names the thing the fixture
   armed — the assertion stays sharp (a specific def or id) without pretending
   the bench is quiet.
2. **An alert injected while paused is journalled before the advance starts.**
   The scanner is a per-FRAME diff. This is the whole reason `alert-at` exists;
   using the shipped `alerts` step here proves nothing.
3. **Muting an alert that is already ON does nothing now and something later.**
   `alert_on` is a transition, so the current on-cycle has already emitted its
   event and there is no pending wake to cancel; the mute takes effect on the
   next on-cycle. The verb says so per row (`applied[*].already_on`) rather than
   leaving it to be inferred from an absence — phase 4 asserts the field exists.
4. **`SelftestArgs` is a declaration.** `722c951`'s own comment there records
   that a branch adding a step without adding its arguments refuses every call to
   that step *before any step runs*, and that the clean merge which produced it
   needed a human. `alert_delay_ticks`, `alert_critical`, `alert_label` and
   `letter_def` are all listed.

---

## A ruling this suite makes, rather than queueing

**`through_news` is a third argument, not an extension of `through_casualties`.**
They are two different decisions and one reason string cannot honestly cover
both: `through_casualties` says "my colonists may fall while this runs and I
accept that", `through_news` says "do not wake me for things I might act on". A
post-mortem grepping for who accepted casualties must not turn up every run that
only wanted to sleep through a trade caravan. They are also asymmetric in shape —
one bypasses an **arm-time refusal** and appears in `escaped`/`bypassed`, the
other suppresses a **during-advance halt** and reports what it cost in
`news_rode_past` — so folding them would fold two mechanisms as well as two
meanings.
