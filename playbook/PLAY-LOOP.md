# PLAY-LOOP.md — running a colony, as a procedure (spec 4.2)

The play loop as a reusable skill: what a Claude session actually DOES to run
a colony through `rwa`, turn by turn. DESIGN.md gives the loop its one line —

    act → advance(until …) → read journal delta + digest → think → act

— and this file gives each word its verbs, its checks, its halt handling and
its exits. It packages what 4.1 shipped; where 4.1's files already argue a
shape (`checklists/README.md`) or fix an order (`playbook/SESSION-START.md`),
this file defers to them rather than restating them. Like `turn.md`, this is
a load-whole file: the session runs it, so it earns its bytes.

**Packaging note — why this is a prompt doc, not a `.claude` skill.** The
spec allowed either. In this repo `.claude/` is gitignored, and the learning
system's core property is git inheritance — "every agent instance starts with
everything ever learned" (DESIGN §Learning). A skill file that git does not
carry is invisible to a fresh clone and to Dorian's diffs, so it fails the
property this playbook exists for. The body below is runner-agnostic
markdown; any box that wants a `/play-colony` slash command can point a
one-line local skill at this file. Invocation is simply: *read this file and
follow it, with args: which colony (save or new), and the session's tick or
wall budget.*

Constants marked *(proposed)* are the author's calibration awaiting Evan or
awaiting run data — the repo's standing discipline.

## Session start

`playbook/SESSION-START.md` is the ordered load list and it is this spec's
contract — follow it position by position, no reordering, nothing extra
loaded eagerly. Two mechanics around it:

- **Before position 6** (`rwa status`), set the run name so every command
  from the first probe onward lands in one transcript dir:
  `export RWA_RUN=<colony>-<yyyymmdd>` (e.g. `swanpoint-20260901`).
- **Position 6's five verdicts** (`rwa/README.md` §Liveness) map to actions:

  | verdict | do |
  |---|---|
  | `ok` | proceed |
  | `menu` | the right save must be loaded. There is no load verb — loading is the LAUNCHER's job (`run-agent.sh` / the autostart pattern), deliberately outside the protocol. Relaunch with the target save where this machine's rules allow launching the bench; otherwise escalate |
  | `down` | start the bench via `run-agent.sh` — `_RimWorld-Agent` only, the standing carve-out (DESIGN §Non-goals). On a machine whose rules forbid the agent launching anything (BORGES), escalate instead. **A `--quicktest` launch needs `autostart.rws` parked out of `Saves/` first** — otherwise map gen fails every time and lands right back on `menu`, which reads as bad luck with a seed ([[quicktest-and-autostart-collide]]). `run-agent.sh` now REFUSES that launch and names the `mv` — but **the bench's `run-agent.sh` is a COPY, not a symlink** (`make-profile-agent.sh` installs it; the script refuses to run outside `_RimWorld-Agent`, which a symlink would break). So a fix to `profile/run-agent.sh` does NOT reach the bench until it is re-installed. Measured 2026-09-01: the bench's copy was two days stale and had none of the guard. `install -m 0755 profile/run-agent.sh $BENCH/run-agent.sh` before any launch that depends on a change to it |
  | `stalled` / `starved` | remediate per `rwa/README.md` (windowrules, `rwa watch on`, relaunch through the launcher); never work around it by hammering commands |

- **The session's first read is a day boundary** — `daily.md` runs at it, on
  a new colony or a resumed one, and its snapshot is written like any other
  day's (§read, step 5).
- **On a NEW colony**: `triggered.md`'s colony-start section runs now, top to
  bottom, before the first advance — every line logging a verdict, `blocked`
  lines naming their issue ids (3.3, 3.6). This is SESSION-START position 3's
  own instruction; it is repeated here because skipping it is the single
  costliest silent skip the ledger can show. Ledger ids for these steps —
  which are a numbered list, not `###` items — are `colony-start-<n>` in
  file order, so the no-silent-skips diff covers them too.

Then check `rwa watch` once and remember the answer — it decides whether
advances are announced (§Cadence).

## The turn

One turn = one pass through act → advance → read → think. The moments inside
it are exactly the three `checklists/README.md` names: an act you are about
to take, an event that halted the advance, and the read you perform every
time control returns. Nothing below invents a fourth.

### act

Player verbs only: `designate`, `zone`, `equip`/`wear`/`drop`, `forbid`/
`unforbid`, `work-priorities`, `schedule`, `policies`/`policy-*`, `assign`,
`research-set`, `surgery-add`/`surgery-remove`, `draft`/`undraft`, `move-to`,
`attack`, `rescue`, `capture`, `tend`, `seek-at-will`, `fire-at-will`,
`flick`, `repair`-class designations, `landmark`, and 3.3's `place-layout`
when it ships — each its own independent `[Verb(...)]`, not subcommands of
anything. `orders` (`PawnOrderVerbs.cs`, member `Orders`) is a different,
diagnostic verb — the right-click-menu probe for "what work could this pawn
do here" — and is NOT read-only: it can delete an incompletable bill
(`BillStack.RemoveIncompletableBills`), consumes a scribed job id per
candidate, and rewrites every bill's scribed `paused` flag on the map (see
its own header comment and DESIGN.md's decisions log). Batch by the plural
form — one rect, one call (DESIGN §Action model); a shell loop over `rwa` is
the ragged-tail escape hatch, at 0.25–1s per round trip.

- **`dev:*` is forbidden during play.** Fixture staging before the session
  proper is the only sanctioned use, and it journals as cheats either way.
  If play appears to NEED a god-hand, that is a verb-gap: file the spec issue
  (postmortem.md's ladder, verb-gap row), log the checklist line `blocked`,
  and route around or escalate. Never cheat past it.
- **After every act, its act-keyed trigger fires before any advance**:
  queued a bill → `bill-who-will-do-it`; made/edited a growing zone →
  `plant-set-at-creation`; sited a bench or stockpile → `bench-siting`;
  placed a layout or build batch → `home-area-after-build`. One ledger line
  per firing (`triggered.md`).
- Every mutating verb echoes evidence and journals as `action`; trust the
  result envelope, and on `ok:false` read `error.code` before deciding — a
  refusal is a widget-gate answer (research missing, precondition failed),
  which is information, not breakage.

### advance

    rwa advance --until.letter --timeout_ticks 60000

is the default: any-letter guard, hard cap one in-game day (60,000 ticks).
The guard set is exactly `{ticks | until:{letter|threat|alert|event}}` — all
journal taps over discrete events; there is no `condition` matcher until 1.6
lands, which is why `turn.md` exists (its README: "the missing condition
matcher, run by hand"). Narrow the guard when the turn is ABOUT something:
`--until.threat` when a raid is due, `--until.alert Alert_X` when watching
one alert, `--until.event.type death --until.event.contains <name>` when a
specific pawn is the question.

- **The one-day cap is a standing policy, not a tuning default** — §Cadence
  says why, and what raising it would cost.
- `halt_on_error` stays at its default (true): a red error halts the advance,
  per the standing zero-red-errors invariant.
- `max_tps` is left to the mod's thermal cap; never raise it for a
  fast-forward (the cap exists because this hardware trips — DESIGN §Time
  model, and FINDINGS §6 measured ~1392 tps at a 30fps cap anyway).
- **Pre-advance gate — time control, checked FIRST.** Read `rwa status` and
  look at `paused` before EVERY advance. If it is false the game has been
  running unobserved: `pause`, then log `time-control-drift` (`triggered.md`)
  with the SIZE of the window, then read the whole journal delta across it
  before advancing again. A dead `rwa` client does not stop the game — the
  mod keeps advancing toward its target through a tool error, an interrupt or
  a timeout, and M1 lost ~60,000 ticks that way, more than once, with
  `pause` afterwards reporting `was_advancing:true, speed_before:Ultrafast`.
  `status` is a read of the heartbeat file and costs the game nothing. This
  gate goes first because a running game makes every other read stale,
  including the drafted check below.
- **Pre-advance gate — the undraft discipline.** Before EVERY advance: if any
  colonist shows `drafted` and `threats.hostiles` is 0 with no threat being
  actively responded to, `undraft` first. Read it from the digest's colonist
  flags; if `colonists.more > 0`, confirm against `pawns {cap:200}` — the
  digest's cap is attention-ordered and an idle drafted pawn can fall below
  it. A drafted pawn neither eats, sleeps nor works (vanilla's own `Drafting`
  concept text), and `SeekRegistry.ShouldSeek` requires `!pawn.Drafted` — so
  a forgotten draft is both a slow starvation and a suppressed defender. The
  gate lives HERE, on the loop, so it survives `raid-end`'s planned
  retirement into cc8988c's mod procedure.
- **Announce when watched**: if `rwa watch` showed the window revealed,
  say what is being advanced and why before issuing anything longer than
  10,000 ticks *(proposed)* — "advancing until next letter, cap 1 day" — so
  the human watching the window knows the fast-forward is intentional.
  Re-check `rwa watch` at day boundaries; it is a client-side read and costs
  the game nothing.

`advance` always returns with the game paused. Paused is normal; the agent
owns time.

### read

In this order, every return — **unconditional and not parameterised.** A
targeted query never substitutes for steps 1 and 2, however tight the loop
feels: M1's six back-to-back 2,500-tick advances read only
`pawns {filter:"hostile"}`, and a colonist was downed, alerted on twice, and
left bleeding inside that window ([[read-every-return-or-lose-a-colonist]]).
If you are waiting for one specific thing, that is an argument for an
`until:` guard, not for a poll.

1. `rwa journal --since <last_seq>` — the delta. What happened while time
   ran, including the `action` echoes of your own acts. Any `red_error`:
   stop advancing, triage (our verb? our mod under test? vanilla?), and
   escalate if unexplained — zero red errors is standing.
2. `rwa digest` — ONE digest per return, not one per thought. The digest is
   cheap in bytes and not uniformly cheap in work (its per-colonist `room`
   field recomputes room stats on read), so re-reads inside a single turn
   are waste. Everything else is drill-down (below).
3. `turn.md` trip-wires over the digest in hand — zero extra queries; log
   only firings.
4. `triggered.md` event triggers over the journal delta — threat letters,
   raid-end transitions, Zzzt, roster changes; one drill-down per firing,
   every firing logged.
5. **Day boundary** — `digest.time.day_of_season` differs from the last
   read's, **or this is the session's first read**, which counts as a
   boundary because there is no last read to differ from and the loop
   snapshots that day anyway: run `daily.md` top to bottom, one ledger line
   per item per day whatever the verdict (`n/a` for off-cadence or
   inapplicable items, so coverage stays a diff, not a judgement), and
   snapshot the digest to `RUNS/<run>/digests/day-<N>.json`. The rule is
   keyed to the SNAPSHOT: a `digests/day-<N>.json` with no full set of daily
   lines behind it is a compliance failure, which is exactly what
   `accept/4.2-play-loop.py` checks. On a new colony `triggered.md`'s
   colony-start section runs first, and a daily item it already answered
   logs `ok` naming the colony-start line — not nothing. (M1 day 1 missed
   all four items to this ambiguity; `postmortem.md` §Compliance findings.)

### think

Decide the next act from: the halt reason (§Halts), checklist verdicts owed
an action, the emergency posture if armed, and the colony notes' standing
intents. Drill-down discipline (measured, 2.2): `pawns` is the per-turn
staple (~120 bytes/line, attention-ordered); `pawn {id}` is for the pawn the
turn is about, and asks for the sections the decision needs —
`sections:["needs","mood"]` for a triage glance, `["state"]` for
draft/seek posture, `["equipment"]` for armament — never all 13.
`opinions` stays off in routine turns. Spatial questions go to the
query verbs (`find-rect`, `nearest`, `reachable`, `room-at`) — the model
does topology, the game does geometry.

## Halts — what the advance's `reason` makes of the next turn

| reason | it means | the loop does |
|---|---|---|
| `ticks` / `timeout` | the guard didn't fire inside the cap | normal read; the cap firing on `until.letter` is common and fine — a quiet day |
| `letter` / `alert` / `event` | the guard fired; `halted_on` carries it | the event IS the turn input; run its `triggered.md` entry if one matches |
| `threat` | ThreatBig/ThreatSmall letter | emergency posture, below |
| `dialog` | a force-pausing modal is up; `halted_on.letters` names the decision owed | first-class turn input — see the dialog rule below |
| `red_error` | halt_on_error tripped | triage; zero-red-errors invariant; escalate if unexplained |
| `stalled` | the game stopped for a reason that is not ours | `rwa status`, remediate per its verdict, else escalate — never re-advance into it |
| `interrupted` | something else took the driver | read the envelope, surface it; do not silently retry |

**The dialog rule.** Until a timed letter or modal is cleared, EVERY
subsequent advance halts at 0 ticks — the run looks alive and is wedged.
So `reason:"dialog"` is handled deliberately, exactly once:

- If dialog verbs exist (3.5: `letter choose/dismiss`, `dialog dismiss`),
  read `halted_on` — the windows and letters are named — decide, act,
  log the decision like any other act, and continue.
- If they do not (3.5 unshipped or the window is one it cannot address):
  do NOT retry the advance. Write the end-of-session summary, note the
  bench is left paused-with-modal — visible-and-stuck is 1.7's designed
  failure mode, not a corruption — and escalate to Dorian with the
  `halted_on` payload verbatim.

**The wedge rule, whatever the reason:** two consecutive advances returning
`ticks_elapsed: 0` end the session and escalate. A 0-tick return demands a
state change before the next advance; a second one proves there wasn't one.
Without this rule the transcript fills with identical turns and the run
looks alive — the verification pass's exact warning.

## Emergency posture

Entered on a threat letter (`reason:"threat"`, or a threat letter in the
delta) or `threats.hostiles > 0` on any read.

1. **Respond at the letter, not the casualty**: run `raid-letter`
   (`triggered.md`) — arm from spares (unforbid first), fix shield/ranged
   pairings, check the two shield alerts.
2. **Verify the delegation before touching anyone**: emergencies belong to
   the colony brain (DESIGN layer 4 — SeekAndKill fights, FSWA arms,
   medical/firefighting ride work priorities). Per violence-capable
   colonist, `pawn {sections:["state"]}` → `seek.will_seek` — the field that
   answers "will this pawn actually fight" (`toggled` alone does not).
   `seek-at-will {pawns:[…], on:true}` where eligible and off; its per-pawn
   refusals are diagnoses, not failures.
3. **Intervene by exception only**: `draft` a pawn only against an observed
   failure of the delegation — seek refused for a reason you can't fix this
   turn, a pawn fleeing into danger, a turret unmanned (`man-turret`). Say
   why in the same breath (the transcript is the record). A drafted pawn has
   seek suppressed — drafting the whole roster turns the autocombat OFF.
4. **Advance short while it burns**: `--ticks 2500` *(proposed — one in-game
   hour)* or `--until.event.type death`; never the one-day default into a
   live fight.
5. **Exit when `threats.hostiles == 0`** — the standing-hostiles count is
   the fight-over predicate (not `danger`; the two disagree). Fog caveat:
   `threats.hostiles` does not fog-filter but `pawns {filter:"hostile"}`
   does, so a nonzero count with an empty roster read means a real hostile
   you cannot see — stay armed, do not hunt blind.
6. **Then `raid-end` runs in its written order** — rescue, finish/capture,
   **undraft everyone**, unforbid the field, re-read armament — and the
   pre-advance gate (§advance) backstops the undraft even if this item
   retires into the mod.

## Cadence — and the assumption daily.md stands on

**The advance cap is one in-game day, and it is load-bearing.**
`checklists/README.md` justifies the daily rung's existence on "the 4.2 loop
already caps early advances at about one in-game day, so a day boundary is a
return the loop was going to make anyway". This file honours that: the cap
doubles as the daily sweep's SAMPLING INTERVAL — daily items run at the
first read after the boundary, so their staleness is bounded by the longest
advance. At a 1-day cap, every daily item is at most ~1 day stale, which is
what "moves on a scale of days" tolerates.

Raising the cap is therefore not a tuning knob, it is a policy change with a
named cost: a 3-day advance makes the freezer check up to 3 days late.
Whoever raises it must, in the same commit, either re-justify `daily.md`'s
items at the new latency or move the intolerant ones onto `until:` guards
(1.6's condition matcher is the designed successor — most `turn.md` items
already name their predicate). Until then: **cap 60,000 ticks, every
advance, not just "early on"** *(proposed as standing policy — Evan or run
data may relax it once trust is earned; the daily.md revisit above is the
price printed on the knob)*.

Within the day: any-letter default; short advances during emergencies and
staged sequences; there is no minimum — act-heavy turns that advance zero
ticks while a build queue settles are normal.

## Artifacts — what a run leaves behind

    transcripts/<RWA_RUN>/            every command + result, automatic (rwa)
    RUNS/<run>/checklist.ndjson       the ledger — one line per evaluation
    RUNS/<run>/digests/day-<N>.json   the digest at each day boundary
    RUNS/<run>/digests/final.json     the digest at session end
    RUNS/<run>/summary.md             always written, last act of the session
    RUNS/<colony>.colony.md           colony notes, updated at session end

- **The ledger** is 4.1's schema (`checklists/README.md` §run ledger),
  appended from the first evaluation on. A silent skip is a missing line.
- **The summary** states, minimum: colony + save name, session tick span,
  the last journal seq read (the next session's `since_seq`), turns taken,
  letters/threats handled, checklist `action` lines and what was done,
  anything `blocked` and on which issue, drafted count at end (must be 0 —
  say so explicitly), red-error count (must be 0), where the colony notes
  live, and — if the session ended abnormally — the halt payload and the
  escalation posted.
- **Post-mortem**: any colonist death, colony loss, or near-miss that cost
  real recovery → run `postmortem.md` (the full procedure); every session
  end regardless → its light pass ("what nearly bit"). After a loss, the
  NEXT session runs it before acting — SESSION-START position 9's rule.
- **Colony notes** (`RUNS/<colony>.colony.md`) are the state that persists
  between sessions and is derivable from nothing else: current intents and
  half-done projects (the house going up, the research rationale), the
  landmark names registered and why, decisions deferred. A page,
  overwritten in place — git history is the archive.
  **Never lessons**: anything learned goes through the post-mortem into the
  playbook, or it becomes a shadow playbook nobody audits. SESSION-START
  position 10 loads these.

## Escalation to Dorian

Escalate — a git-bug comment on the relevant issue (the muster if none
fits), plus the session summary — for exactly: colony-ending events (with
the post-mortem attached or committed); spec gaps found in play (file the
issue first, then reference it); a dialog wedge while 3.5 is unshipped;
anything requiring installs, ModsConfig edits, or bench changes the loop
must not touch; red errors that survive triage; a bench that is `down` or
`menu` on a machine whose rules forbid launching. An escalation is a
deliverable, not a failure — the failure mode it replaces is a wedged run
imitating a live one.

## Invariants — the auditable list

1. Everything through `rwa`; never a direct write into the protocol root.
   (`rwa journal`'s direct FILE READ of the journal is the documented,
   sanctioned path — reads through the tool are not pokes.)
2. Every checklist evaluation produces a ledger line — `ok`, `action` (with
   what was done), `blocked` (with the issue id), or `n/a`. A missing line
   is a compliance failure, findable by diff.
3. Zero red errors, standing; `halt_on_error` stays on.
4. No advance while any colonist is drafted, absent a live emergency.
5. A 0-tick advance is never retried unchanged; two consecutive end the
   session and escalate.
6. Advance cap 60,000 ticks until the daily.md coupling is re-justified.
7. `dev:*` never during play; fixture staging only, journaled.
8. Only `_RimWorld-Agent` is ever launched, only via the launcher, only
   where machine rules allow.
9. Long advances are announced when the window is watched.
10. The session always ends with a summary — normal end, wedge, or loss.
11. `status.paused` is read before EVERY advance; a game found running is
    paused first, its window logged as `time-control-drift`, and its journal
    delta read before the next advance. The agent owns time only while its
    client lives.

## Acceptance (4.3 exercises this; the mechanical half is scripted)

The spec's acceptance, with the verification pass's definitions adopted:
a scripted one-day session on a fixture colony **that includes a threat**,
producing: a ledger whose lines diff clean against the item set (no silent
skips); at least one checklist-driven `action` whose note names the act
taken; a complete transcript — every `rwa` call present, and per-op counts
consistent with the journal's own `action`/`dev` record for the window; a
summary; **zero drafted colonists at end** (the undraft amendment's loop,
closed); zero red errors. `accept/4.2-play-loop.py` audits a finished run
dir against every mechanical clause above; "reviewed against the skill text
for compliance" is a HUMAN gate on top of it — Dorian reads the transcript
against this file — and is labelled as such, the way 4.3 labels its
sign-off.
