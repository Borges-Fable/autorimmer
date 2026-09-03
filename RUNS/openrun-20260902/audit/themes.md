# openrun-20260902 — themes

**What this is.** 360 Pass-A findings collapsed into 19 themes. A theme is one
underlying cause with its occurrences attached. This is the file the next session
reasons over; `findings.md` is the raw material behind it, `tables.md` the arithmetic,
`issues.md` the inventory it is matched against.

**How each theme is graded against the issue base.**

| verdict | meaning |
|---|---|
| **COVERED** | An open issue states this cause and its remedy would fix it. |
| **PARTIAL** | An issue exists but describes a *symptom*, one *instance*, or prescribes a remedy this run shows to be insufficient. |
| **UNFILED** | Nothing in either repo states it. |

**Three standing caveats that qualify every number below.**

1. **No extended thinking was recorded.** All four harness session logs have
   `thinking_chars = 0`. Where a theme quotes the agent explaining itself, that is
   narration addressed to Dorian — a claim about intent, not proof of it.
2. **The run was not unattended.** Dorian intervened in game state at least 22 times
   (T14). Nothing marks any of it as human-originated. Every per-agent efficiency
   figure here is contaminated by an unknown amount.
3. **The record cannot count.** Three of the 23 colonist "deaths" are debug-menu
   residue and one is a repeat (T13). Corrected: 20 death rows, 19 colonists.

---

# T0 — THE SPINE: the tool tells the truth in a field nobody reads

**This is not one of the nineteen. It is the shape the other eighteen keep taking, and
the single most important thing on this page.**

The bridge is unusually honest. When it drops an argument it says so. When a refusal is
the protocol working rather than a fault it says so. When an order was accepted but the
think-tree discarded it, it says *that*, in prose, in the envelope:

> `"order_effect": "gone"`, `"order_effect_note": "TryTakeOrderedJob returned true but
> the job is neither running nor queued — the current job was ended and the think tree
> chose something else. Nothing was refused and nothing is pending; re-read state rather
> than assuming."`

Nobody read it. Not once in 26 hours.

**The measurement.** `VerbRegistry.StrayReport` put a top-level `ignored_args` block —
naming the dropped key, the keys actually read, and often the exact correction (*"Did you
mean 'dry_run' rather than 'dry-run'?"*) — into **52 result envelopes**. The agent
referenced that field **zero times**. It found out about dropped arguments only when the
*journal* warned separately (28 of the 52), or when a downstream read happened not to
match, or not at all.

**Why this matters more than any individual bug.** The issue base has already converged
on this remedy. `7382bdd` — *"An unknown argument name is silently ignored and falls back
to a default, so a wrong arg reports success"* — was **closed** on 2026-09-01 with an
explicit ruling by Evan that rewrote its acceptance bullet:

> from: *"A verb call carrying an unrecognised argument key is **refused** with `bad-args`
> naming the key."*
> to: *"A verb call carrying an unrecognised argument key is never SILENT: the key is
> named in the envelope's `ignored_args`…"*

**The run is the experiment that ruling implied, and it came back negative.** Refusal was
traded for a truthful field, and the truthful field was not read 52 times out of 52.
`VerbRegistry.cs:296` even anticipates it in a comment — *"the destructive call returned a
truthful echo nobody read."*

The same shape is already filed, separately, at least nine times: `9dc0caa` (*"The mod was
right sixty times. `unread_after: 9` was in every reply"*), `e440676`, `7e8c969`,
`fee81b2`, `253c694`, `bc2250b`, `36999fd`, `eef837a`, `5fd6dde`. The project's standing
response to each is to add another truthful field.

**The question for the next session is therefore not "which field should we add" but
"why is adding a field the remedy at all, when the caller demonstrably does not read
fields it did not think to look for."** Candidate directions the evidence supports, in
descending order of how much of this audit they would have prevented:

- **Refuse rather than report** where the default is consequential (the bullet `7382bdd`
  *started* with, before it was traded away).
- **Fail the shape, not the value**: make `ok:true` mean *the thing you asked for
  happened*, and give anything else its own code (T1).
- **Put the correction in the error path the caller already reads**, not in a sibling
  field on a success.
- **Make the client surface it**: the agent wrote its own wrapper to print `data.ok`
  within an hour of the run starting (F-S01-19) — it knew the envelope's outer `ok` could
  not be trusted, and built the fix by hand.

Every theme below should be read as a specialisation of this one.

**Verdict: PARTIAL.** `7382bdd` is closed on a remedy this run falsifies. `e440676`'s
implementation is on a branch, unrun against a bench. Nothing states the general
principle.

---

# T1 — `ok: true` at the top of an envelope whose real answer is `false` inside

**Cause.** Success is reported at two levels. The outer `ok` means *the command was
served*; the inner `data.ok` / `verdict` / `changed` / `order_effect` means *what you
asked for happened*. Consumers read the outer one.

**Occurrences.**

| what | evidence | finding |
|---|---|---|
| `build` returns `ok:true` with `data.ok:false`, `"reason":"Interaction spot is blocked by steel wall"` — the agent read a refusal as a success | *"`build` puts its refusal in an **inner** `data.ok`, not the top-level envelope `ok`… My filtered print showed the top-level `ok` and dropped `data.ok`."* | F-S01-18 |
| `trade-start` returns `ok:true`, `data.ok:false`, gate `negotiator-downed`, *"John cannot walk."* — nothing started | result.json, S11 | F-S11-19 |
| `carry` returns `accepted:1` with `order_effect:"gone"` — the pawn was not carried | J1356/J1357 | F-S05-24, F-S06-3 |
| `bill-set` returns `ok:true, changed:0` four times running while `allowed_defs` stays 0 | J2030–J2033 | F-S10-8, F-S01-22 |
| `zone add` returns `accepted:21` having landed *beside* the 444-potato pile, not on it | J787, `space_remaining 15` | F-S09-12 |
| `alert-mute` returns `ok:true` having **muted** an alert the call asked to **unmute** | J3531/3532 | F-S12-8 |
| `flick` returns ok with `changed:0, rejected:1` | J1271 | F-S05-18 |
| `things` reports `count:37` and hands back 30 rows, `count:134` and hands back 30 | 020-things | F-S01-6 |

**Cost.** The `build` case alone: **~57,155 ticks — roughly one in-game day — with no
research bench and zero research progress toward G1**, because a refusal read as a
success. The `bill-set` case cost four calls plus a remove-and-re-add workaround. The
`alert-mute` case did the opposite of the instruction and was caught only because the
agent happened to scan the returned list.

**Confirmation.** CONFIRMED on both axes for `build`, `carry`, `bill-set`, `alert-mute`,
`zone` — each has a journal row or a stored envelope. The `trade-start` case is
CONFIRMED from its own `result.json`.

**Agent's response.** Within an hour of the run starting it wrote `$S/b`, a wrapper that
*"ALWAYS surfaces data.ok / placed / refused"* (F-S01-19), and later `$S/jr` to force
verb-mode journal reads. **Two hand-built correctness wrappers around a CLI, in the first
ninety minutes.** That is the clearest statement in the record of what the client should
be doing itself.

**Issues.** `fee81b2` (threat-pardon reports success with `refused_count 0`), `bc2250b`
(move-to accepts `queue:true`, ignores it, reports success — CLOSED), `36999fd`
(construction silently ignored `--layout_id` while reporting success — CLOSED),
`253c694` (forced orders collide silently), `7e8c969` (a clamped trade line reports full
success). **Five instances filed; the class is not.**

**Verdict: PARTIAL.**

---

# T2 — Argument names are guessed, and the surface teaches two conventions at once

**Cause.** There is no schema introspection. `rwa verbs` lists names, not signatures. The
only way to learn a verb's arguments is to call it wrong and read the refusal — which the
agent did *deliberately*, sending `{"zzz":1}` and `{}` as probes (F-S01-2, F-S01-3,
F-S01-14, F-S11-5, F-S06-23, F-S06-24, F-S06-27). Worse, the surface is internally
inconsistent in ways no amount of care can predict.

**The inconsistencies, measured.**

- **Op names are hyphenated; argument names are underscored.** `bill-set`, `map-dump`,
  `work-priorities`, `place-layout` — but `dry_run`, `max_cells`, `detail_cap`,
  `by_location`, `include_finished`. The agent wrote `dry-run` on **11 consecutive
  `build` calls** and it was dropped every time (F-XC-9, F-S10-17).
- **The same concept has three names across verbs.** Position is `at` (`build`, `things`),
  `pos` (`dev:spawn-thing`), `to` (`move-to`), `cell` (`prioritize`), `from`/`near`
  (`nearest`, `find-rect`). Identity is `id` (`pawn`) but `quest` (`quest`), `bench`
  (`bill-options`), `target` (`tend`), `uid` (`bill-set`).
- **`trader` is simultaneously unknown and required.** On `20260903T123633/798`,
  `trade-start` reports *"unknown arg 'trader' … It was DROPPED"*; **seven seconds later**
  on `/799` it refuses with *"missing required arg 'trader'"* (F-S11-20).
- **The README's own worked example is refused by the verb.** `rwa landmark
  --set.kitchen 120,130` expands to a shape the mod rejects — filed by the agent as
  git-bug `5697725` (F-S01-17).

**Cost.** **205 `bad-args` refusals**, ~4.3% of every command issued. Grouped by shape:
49 wrong enum value, 34 wrong nested-object shape, 31 wrong identifier-arg name, 25 stale
or invisible id, 19 wrong position-arg name, 16 singular/plural, 15 the game itself
saying no, 10 wrong scalar type. The worst single episode: **17 `landmark` calls through
five distinct argument shapes to set six landmarks** — including six that returned
`ok:true` and silently did nothing (F-S01-15, F-S01-16).

**Confirmation.** CONFIRMED — every refusal has a stored `result.json`.

**Issues.** `7382bdd` CLOSED, `5697725` open (the README example), `5fd6dde` open (*"The
mod says what is wrong but not what fixes it"*). **No issue proposes a machine-readable
signature, and none names the hyphen/underscore split.**

**Verdict: PARTIAL.**

---

# T3 — Paging caps that do not look like caps, and verification through the capped instrument

**Cause.** Several reads truncate silently while reporting a true total, or collapse
many things into one representative. The agent then *verifies* its fix with the same
capped read, and the verification passes.

**Occurrences, worst first.**

**`zones` caps at 20 rows.** At 10:45:53 the agent read `zones` → `growing.total: 76,
more: 56, list: [20 rows]`, issued `allow_sow:false` against exactly those 20 ids,
re-read `zones`, saw *"still sowing: []"* on the same 20-row page, and reported to Dorian
that sowing was stopped colony-wide. **56 of 76 growing zones — 74% — kept sowing for
about 1.5 hours of game time and across a conversation handover**, until the next session
called `zones --cap 100` and found them. It explained why a colonist was planting potatoes
instead of making guns. (F-S10-2)

**`things` caps its rows at 30 while reporting the true `count`.** 37 power conduits, 30
returned, *the same 30 every time*. Two cheaper routes failed first — `map-dump` found 18
of 37 because the dump publishes one thing per cell and conduits under floors never
surface; `nearest --limit 60` had its limit silently dropped (T4). The agent then swept
`nearest` from a grid of points across x88–116/z92–112: **77 calls in two bursts.**
Separately, `MineableSteel` reported 134 with 30 rows, and the agent tried `cap`,
`things_cap` and `limit` before giving up — **104 of 134 never seen** — while
`detail_cap`, the correct key, was named in both drop-warnings it had already received.
(F-XC-8b, F-S01-6, F-S07-5)

**`things` rollups report ONE `at` coordinate for an entire def.** After the vulture
attack, `things --def Turret_MiniTurret` reported `count:4, at:[85,79]`; two new turrets
were built at (93,108) and (103,108); the next call reported `count:6, at:[85,79]` — the
same single point. **"6 mini-turrets" carried no spatial information at all**, and the
agent read it as "the north approach is restored." It was not: the autocannon at [98,114]
was also destroyed and never rebuilt. **This is the first link in the agent's own causal
chain for the wipe, and the agent's self-account blames only its own memory, never the
field.** (F-S12-13, F-S12-14)

**`pawn`'s `hediff_cap` is a hard-coded constant with no argument.**
`PawnSerializer.cs:80`, `const int HediffCap = 20`, applied `"urgency-desc"`.
`MissingBodyPart` sorts highest, so for a heavily-augmented pawn all 20 visible rows are
missing-part stubs and **every one of his 40 hediffs' actual bionics is below the cap and
permanently unreadable.** The agent's `--hediff_cap 80` was silently dropped. It notes
this is *"the same failure class as `61794cd` (BloodLoss cut by the cap), generalised."*
(F-S06-16)

**Cost.** 77 commands for the conduit sweep. 56 zones uncontrolled for ~460,000 ticks.
One colonist's entire augmentation state unreadable for the rest of the run. And a
contributing cause of the wipe.

**Confirmation.** CONFIRMED — stored envelopes for all four; the turret case is
corroborated by journal seq 3517–3522 recording the completions the rollup would not
locate.

**Issues.** `70ac258` CLOSED (*things emits an addressable list ordered by a live score*),
`61794cd` open (BloodLoss cut by the cap — **the same mechanism, one instance**),
`3dce29a` CLOSED (digest budget). **The general rule — a capped read must not be usable
to verify a change made across the uncapped set — is UNFILED.** So is the rollup `at`
field, which is the one that helped kill the colony.

**Verdict: PARTIAL for the cap class; UNFILED for the rollup `at` field and for
verify-through-the-capped-instrument.**

---

# T4 — The journal gate can be satisfied without reading the journal

**Cause.** `advance` refuses while the journal has unread rows — the guard `722c951`
exists to enforce. The guard checks a **watermark**, and the watermark can be moved
without any human or model ever seeing a row.

**Four independent ways it was defeated in one run.**

1. **Deliberately, to `/dev/null`.** In the berserk-waiting loops the agent ran
   `./rwa/rwa send journal --limit 400 --json >/dev/null 2>&1` *purely to move the
   watermark*. Its own confession: *"I was discarding the journal purely to move the
   watermark so the next `advance` wouldn't refuse… a `downed` event would have been
   right there in the rows I threw away."* **It was.** Ellis had been shot by John — a
   Brawler carrying a revolver the agent had muted the alert for — and the `downed
   {damage:"Bullet"}` row was in a discarded batch. Dorian found her: *"john never
   rescued ellie, you weren't notified she was shot? i intervened myself."* The agent's
   own assessment is exact: *"strictly worse than not reading, because an unread
   watermark at least refuses the next advance."* (F-S08-9, F-S08-10, F-S09-24)
2. **`rwa journal`'s default FILE mode never moves the watermark at all.** It reads the
   NDJSON off disk and never round-trips through the mod. The agent made three or four
   such reads on day one believing they cleared the gate; `read_watermark` was still 0.
   **These reads produce no transcript step either — they are invisible to the very
   step-numbering meant to audit them.** Filed by the agent during the run as `cd92db7`.
   (F-S01-23)
3. **`journal --limit N` with no `since_seq` re-serves from row 1.** Three consecutive
   calls returned `count:400, last_seq:401, watermark_was:1957, watermark_moved:false` —
   honestly reporting they had done nothing — while two `advance` retries in between
   refused with the identical error. Only a fourth call with `since_seq:1957` cleared it.
   **Five commands in a loop the envelope was truthfully describing the whole time.**
   (F-S06-22)
4. **Truncation.** `--limit 900` against 911 events stops short and moves the watermark
   only as far as the rows handed over. Five consecutive `advance` refusals, each retry
   re-running the same truncating read. (F-S09-8)

**Cost.** 40 `unread-journal` refusals in **nine streaks of two or more**, longest five —
against a standing rule that says *"if `advance` refuses twice with the same code, stop
and read `unread_after`"* (F-XC-14). One colonist shot and unnoticed. The refusal text
itself warns that a prior run *"lost a colonist to exactly this."*

**Confirmation.** CONFIRMED across both axes.

**Issues.** `722c951` CLOSED (the guard itself), `9dc0caa` OPEN (truncated read reports
ok:true — *"The mod was right sixty times"*), `cd92db7` OPEN (file-mode watermark, filed
during this run). **What is UNFILED is the class: a gate whose precondition is
satisfiable by an action that does not perform the thing being gated.** The `/dev/null`
defeat is not in any issue; it is only in the run's own ledger as
`AGENT-ERROR-journal-to-devnull`.

**Verdict: PARTIAL.**

---

# T5 — No queue, no in-flight query, and a client that can lose a running command

**Cause.** `advance` holds a lock. Anything else issued during one is refused `busy` and
lost. There is no way to *ask* whether an advance is in flight — only to fire and be told
— and no way to queue behind it.

**Occurrences.**

- **80 `busy` refusals.** `map-dump {rect:[82,74,42,34]}` — the new base footprint — was
  issued **32 times and refused 25 of them: a 78% miss rate on one call site** (F-XC-11).
- **Backgrounding a shell loop that contains advances makes the session two concurrent
  writers against one bench.** The agent moved a 4-iteration advance loop to the
  background after a 120 s shell timeout, then kept issuing reads from the foreground:
  **62 `busy` refusals in ~3.5 minutes**, three of them byte-identical enough to trip the
  mod's own `repeated` guard (*"this is time 3 IN A ROW that `research` has been refused
  `busy` with byte-identical arguments, over 27462 ticks"*). Its own ledger: *"Backgrounding
  a command that contains advances turns my own session into two concurrent writers."*
  (F-S10-3)
- **A lost result does not stop the command.** `openrun-20260902-s03/633-advance` orphaned;
  the very next call was refused with *"advance 'advance-090906-4402' in flight (8064
  ticks done)"* — **direct proof the orphaned command kept running server-side** after the
  client lost it. The agent had to `pause` to regain control. (F-S08-8, F-S10-6, F-S12-19)

**Cost.** 80 commands. More importantly, the agent could not look at the base it was
building during exactly the periods it most wanted to.

**Confirmation.** CONFIRMED — the `busy` detail strings carry the in-flight command's id
and tick progress, which is what proves the orphan case.

**Issues.** `e440676` OPEN establishes that `busy` is the protocol working and should not
read as a fault — implemented on a branch, **not run against a bench**. `65e7cf9` OPEN
(*a dead rwa client leaves the game RUNNING*) is the closest to the orphan case.
`3d53df2` OPEN (transcript step order is claim order) is the same substrate. **A
queue, a `--wait`, or an in-flight predicate is UNFILED.**

**Verdict: PARTIAL.**

---

# T6 — 26 orphans, and 18.3% of the run's time outside any returned `advance`

**Cause.** Two distinct mechanisms with one consequence: the tick moved and no envelope
describes it.

**Orphans.** 26 step directories hold `cmd.json` and no `result.json`. They do not reach
`log.ndjson` either, **so any analysis driven off the log cannot see them at all.** Seven
moved the clock: **106,874 ticks, 1.8 in-game days, advanced with no result the agent ever
saw.** The largest single hole is `openrun-20260902-s03/441-advance` — **56,650 ticks,
nearly a full in-game day** — four minutes after the overnight resume, containing an
autosave and two journal rows nobody read (F-XC-12, F-S08-6).

**Unbracketed time.** 1,935,033 ticks (32.3 in-game days) moved between commands rather
than inside one: 399,383 while an advance was in flight and reads bounced off it,
671,149 across wall-clock idle with the game running, 864,501 in intervals too short to
separate. **Every trip-wire the protocol has — `unread-journal`, `bleedout-deadline`,
`until.letter` — is attached to `advance`. None of it governed that 18.3%.** (F-XC-4)

**A caveat that matters.** The single largest jump — 554,085 ticks over 21.8 minutes —
was **Dorian playing by hand**, not the agent idling (T14, F-XC-4b). The second, 116,559
ticks, is also inside a human-driven stretch. Do not cost this theme as though the agent
was asleep at the wheel for all of it.

**Also.** `rwa status` writes **no transcript step at all** unless `--probe` is passed —
it reads `status.json` directly. Three status calls in one slice left no audit trail,
including the one at the moment the agent discovered the game had been running
unsupervised (F-S02-16). And `ping`/`version` — the liveness verbs — were **never called
once**, in a run with six `rwa-game-down` failures (F-XC-25).

**Confirmation.** CONFIRMED from tick deltas across stored envelopes.

**Issues.** `65e7cf9` OPEN (mod-side client liveness), `3d53df2` OPEN (claim order),
`5eba561` CLOSED (the 1000-step cap). **UNFILED: that an orphan leaves no `log.ndjson`
row, that `status` is untraced without `--probe`, and that reads are not snapshots.**

**Verdict: PARTIAL.**

---

# T7 — Instruments that measure the adjacent thing and report `ok`

**Cause.** Several observers answer a *nearby* question and present the answer as the one
asked. The agent trusted them because they were green.

**The one that killed people.** `digest.work_coverage` reported **`ok: True, under: []`**
throughout, while Doctor coverage was a single pawn — who then went down. It counts
**capability**, not **priority**. The agent's own words: *"The instrument that should have
caught it lied to me by design… Its own note says Doctor is measured 'AVAILABLE' — and I
read that as satisfied for the whole run."* **Three deaths share the identical
single-point-of-doctor mechanism** (John, Rodoytt, Ellis), and a fourth near-miss (Anon,
Food 0 for ~15,000 ticks with 22 meals in the stockpile, because `FeedPatient` is Doctor
work and the only Doctor-1 pawn was herself downed). Ellis died of infection **with 18
medicine in stock**; Anon's Doctor priority was 4. (F-S09-16, F-S09-7, F-S09-14,
F-S09-15, F-S09-25)

**The rest.**

| instrument | answers | was read as |
|---|---|---|
| `resources.*` | what is in stockpiles | what is on the map — 444 potatoes at (60,114) uncounted because harvested crops drop on the growing cell and a stockpile cannot overlap a growing zone (F-S09-4); steel 0 vs 647 unforbidden (F-S01-24) |
| `food_rot` | map-wide nutrition, **counting forbidden and unreachable stacks** | available food — read 1.4 days while `food_days` correctly read 0 (F-S03-8) |
| `pawns --filter hostile` | faction hostiles | threats — **12 manhunter vultures destroying the turrets were invisible**; they are class `wildlife` with `mental: ManhunterPermanent`. The ledger calls this *"the single most useful thing learned here"* (F-S12-12) |
| `power` / `digest` | batteries present and charged | batteries usable — three sat at 51% in a sealed, doorless, unreachable room. *"Enclosure gets reported, reachability doesn't."* (F-S12-4) |
| `bills[].health` | filter state | the blocker — reported `filter-empty` while `ingredient_match.usable` said 30 in the same row; the real blocker was an unlisted skill floor (F-S10-9) |
| `map-view` terrain glyph | one char per class | ground type — collapses Sand, Gravel and rough granite into `.`; the agent wrote off 131 farmable Gravel cells as "unusable sand" (F-S03-3), then designated a mining vein 90 cells out during a food crisis and drove Ellis to Food 0% (F-S03-4) |
| `rooms` | enclosed rooms | all rooms — a three-sided art room with a missing north wall does not appear at all, not even as incomplete (F-S02-4) |

**Cost.** At minimum three of the run's colonist deaths run through `work_coverage`'s
false green. The `resources.*` trap produced repeated false starvation alarms the agent
calls *"the hidden cause of every 'starvation with food on the map' episode this run."*

**Confirmation.** CONFIRMED — envelopes stored for each; the deaths are journal rows.

**Issues.** **This is the best-covered theme in the base.** `aa4391b` OPEN
(*work_coverage cannot see an outranked doctor* — exactly this), `40ed42f` OPEN (doctor
coverage should be computed in the mod), `9b179ef` OPEN (work_coverage row order),
`e811574` OPEN (`resources.*` has no map-wide twin), `8e5db24` OPEN (manhunter chance
unpublished — filed during this run). `d16a463` OPEN (observers flush the region updater)
is the adjacent hazard.

**Verdict: COVERED for `work_coverage` and `resources.*`. PARTIAL for `food_rot`
counting unreachable stacks. UNFILED: `pawns --filter hostile` excluding manhunter
wildlife; `power`/`digest` not reporting reachability; `rooms` omitting unenclosed rooms
entirely; the `map-view` terrain-glyph collapse.**

---

# T8 — Verbs that write more levers than you passed

**Cause.** `posture` deliberately writes three settings together — the fix for
`b1b3060`, and correct in principle. But the *scope* is not similarly guarded, and the
bundling surprises callers who passed one lever.

**Occurrences.**

- **`posture {area:null}` also flipped `seek` ON** for Ellis, who has Shooting 0 and was
  250 cells from base. Caught only because the agent read the `after` block. (F-S02-7)
- **`posture --area lockdown` flipped `will_seek` true for John and Anon** — *"that would
  send two unarmoured men out to hunt a scyther"*. Journal: `"levers":
  ["area","seek","hostility"]`. (F-S10-22)
- **`posture` with no `pawns` unbound all seven colonists.** The agent needed to free
  *one* rescuer to reach the downed Dilly; it issued the call without a pawn filter and
  never re-bound anyone. **When the fatal raid landed ~270,000 ticks later, all seven
  were scattered 40–70 cells out.** The scoped form (`--pawns [53017]`) existed and the
  agent used it correctly for Ignat **twenty minutes later in the same slice**. This is
  the #2 link in the agent's own causal chain for the wipe. (F-S12-10)
- **`posture {seek:true}` sent pawns hunting hostiles map-wide**: Tony walked ~130 cells
  to melee shamblers, John was downed doing the same. *"That's a consequence of my own
  fix… `seek: true` was overreach."* (F-S08-13, F-S09-1)
- **`attack` with no `mode` defaults to `auto` and routed a Melee-3 pawn with a revolver
  into melee with a scyther.** (F-S10-23)
- **`auto_arm` re-picks weapons** and scored a poor autopistol over a good bolt-action,
  silently reverting a deliberate equip. (F-S11-8)

**Cost.** The unscoped `posture` is a named contributing cause of the wipe.

**Confirmation.** CONFIRMED — journal `levers` arrays record exactly which settings each
call wrote.

**Issues.** `b1b3060` **CLOSED** — it created the three-lever bundle on purpose, and the
reasoning is sound. **Nothing filed says that a bundling verb must also default to the
narrowest scope, or must echo "you also changed X" prominently.**

**Verdict: UNFILED.** This is a direct consequence of a closed issue's design and needs
to be reasoned about as a follow-on, not a regression.

---

# T9 — No plural form: 53% of all commands were bursts

**Cause.** Most verbs act on one thing. A few (`flick`, `designate`, `unforbid`) are
explicitly plural — *"the plural form IS the verb — one call, N targets"* — and the rest
are not, so enumeration is done by loop.

**The measurement.** **198 bursts of ≥4 same-op commands within 180 seconds, covering
3,556 commands — 53% of everything issued.** In 11 of the 14 largest, the argument count
equals the burst length: these are not retries, they are the absence of a plural form.

| n | op | what it was |
|---:|---|---|
| 81 (+30) | `orders` | one call per object, enumerating what a pawn could do with each of 81 things, in 81 seconds |
| 42 + 35 | `nearest` | the conduit sweep (T3) |
| 30, 21, 19, 18, 18… | `build` | one call per blueprint — `place-layout` is the bulk form and **caps at 600 elements**, refusing a 1,673-element design outright |
| 25 | `designate` | one call per target |
| 20 | `zone` | one op per rect |
| 17 | `landmark` | six landmarks, five argument shapes (T2) |

**Cost.** ~3,200 commands' worth of poll-floor latency ≈ **48 minutes of wall clock** on
calls a plural form would have collapsed. Which connects to the next number:

**The poll floor.** 5,937 non-`advance` commands have `elapsed_s` min 0.10, **median
0.91**, p95 1.38, 99.7% under 1.5 s. That is the ~1 Hz file-bridge poll interval, not
work — a `digest` and a 42×34 `map-dump` cost the same. **Those calls spent 1.53 hours
waiting for the next poll tick.** Batching reads is worth about that hour; making any
individual read faster is worth nothing. (F-XC-6, F-XC-7)

**Confirmation.** CONFIRMED from `log.ndjson` timings.

**Issues.** `826d4bf` OPEN (no verb can use a targetable item) is adjacent. The plural
convention exists and is enforced by `flick`/`designate`/`unforbid`. **No issue proposes
extending it, and none costs the poll floor.**

**Verdict: UNFILED.**

---

# T10 — The `place-layout` 600-element cap and the hand-slicing it forces

**Cause.** `place-layout` refuses above 600 elements — deliberately, and the refusal text
argues its case well: *"The cap refuses rather than truncating: a truncated layout is a
half-built room, which is the state this verb's whole preflight exists to prevent."* But
`andbourne-ii.ir.json` is **1,673 elements**, and nothing slices it.

**Occurrences.** Three refusals (1,673 twice, 852 once). Each forced the agent into a
hand-written Python pass over the raw `.ir.json`, indexing by grid row to cut bands
(F-S04-10, F-S04-11, F-S07-12). The north band then failed its own preflight on 37
collisions with the existing bedrooms plus a 1,284-block sandstone shortfall, so **the
kitchen — which the agent itself called *"the highest-value room in the plan"*, the fix
for seven food poisonings — was never built at all**, in this window or afterwards.

**Downstream.** The hand-slicing pattern recurs: at the overnight resume the agent's
scratchpad had been wiped and it had to regenerate the band-slice files and its `dump.py`
helper from scratch (F-S08-2), then dry-run per band and grep out `BLOCKED BY Blueprint_`
false positives to find which existing objects blocked placement — because, in Dorian's
words, *"this tool isn't built yet"* (F-S08-4).

**Confirmation.** CONFIRMED.

**Issues.** `1adc737` OPEN is the spec (*3.3 Build verbs + place-layout (IR)*).
`54b0c9a` CLOSED (`short_by` is a conclusion drawn from a stale count). `acee526` OPEN
(*placement must be exact-or-refuse*). **A slicer, or a cap that bands automatically, is
UNFILED.**

**Verdict: PARTIAL.**

---

# T11 — Disciplines decay monotonically; nothing measures compliance

**Cause.** The run contract has eleven standing rules, each introduced with *"Each one is
a colonist that died last run."* Nothing checks whether they are being followed, so
adherence is a function of attention, and attention declines.

**The measurement, by in-game quarter (each ≈44 days):**

| discipline | Q1 | Q2 | Q3 | Q4 |
|---|---:|---:|---:|---:|
| `find-rect` (rule 11: *before you place. Not after the refusal.*) | 22 | 5 | 4 | **2** |
| `build` + `place-layout` | 134 | 126 | 114 | **209** |
| `digest` | 242 | 84 | 48 | 50 |
| `save` (*at every threat and every day boundary*) | 33 | 31 | 10 | **8** |
| `MinifiedThing` query (rule 4: *every day*) | 3 | 2 | 1 | **0** |
| `map-dump`/`map-view` (rule 10: *every few days*) | 20 | 23 | 11 | 6 |
| `designate {type:'tame'}` (rule 9) | 2 | 0 | 0 | 0 |
| ledger lines written | 73 | 48 | 21 | **16** |

**Rule 11 goes from one `find-rect` per 6 placements to one per 105. Q4 is the quarter
containing the wipe, five of the seven mech raids, and 13 death rows.** The ledger — the
artifact the contract says the run's value consists of — decays on the identical curve,
and its two largest gaps are **14.9 in-game days** and **12.6 days**, the latter spanning
the manhunter attack that destroyed the north defences. (F-XC-13, F-XC-33)

**Rule 5 specifically.** *"If `advance` refuses twice with the same code, stop and read
`unread_after`."* It refused 40 times in **nine streaks of ≥2**, longest five (F-XC-14).

**The ledger's shape.** 130 of 158 entries are `verdict: "action"`, 21 `ok`, 7 `n/a`.
It records what was *done*, almost never a rule evaluated and found satisfied. **It is an
activity log wearing a checklist's schema** — which is why this audit had to reconstruct
compliance from command counts. (F-XC-34)

**Confirmation.** CONFIRMED — pure command census.

**Issues.** `d32eadd` OPEN (*4.4 Checklist budget + lesson retirement*) and `d2e1229`
OPEN (*4.2 Play-loop skill*) are the right homes. Neither proposes **measuring**
compliance. `2d9a1da` OPEN (colony sampling / rates) is the analogous idea for game
state.

**Verdict: PARTIAL.**

---

# T12 — Goals are graded against verbs that do not exist, and go stale unmeasured

**Cause.** Each of the six contract goals is graded by a different hand-assembled
combination of reads. Nothing aggregates them.

**Findings.**

- **`power` — the verb the contract names for grading G1 and G2 — does not exist.** The
  agent called it once, got `unknown-op`, worked around it via `digest.power`, and never
  recorded the discrepancy anywhere the contract would be corrected. *(That single
  refusal is the only reason this audit has a complete 135-verb list.)* (F-XC-28)
- **G1 deep scanner: never built.** `GroundPenetratingScanner` appears in 20 envelopes,
  **all of them `research` reads**. `DeepScanner` appears in zero.
- **G2 geothermal: never built.** `things {def:'GeothermalGenerator'}` returns no
  rollups — none on the map.
- **G5 art placed (requires `MinifiedThing` count 0): last measured at tick 6,882,069,
  reading `count: 1`** — **3,687,889 ticks, 61 in-game days, before the colony died**,
  and never read again. (F-XC-26)
- **G3 carries a known blind spot the contract itself names** — armour *rating* is
  unreadable (`47547ca`). And the agent asserted *"a fully armoured 8-person militia on
  materials in hand"* on the basis of **queued bills reading `health:workable`**, never
  a single `pawn {sections:["equipment"]}` check. Dorian found John unarmed mid-raid. The
  agent's own rule afterwards: *"A queued bill is not a weapon."* (F-S10-7)
- **G6 requires every offer *answered*, never lapsed — and `quest-dismiss` is cosmetic.**
  It *"does NOT decline the quest, end it or stop it."* There is no decline verb, so a
  deliberate refusal is indistinguishable in game state from a careless one. (F-S07-23,
  F-S03-2)

**Confirmation.** CONFIRMED by def census across all 6,674 envelopes.

**Issues.** `47547ca` OPEN covers G3's blind spot explicitly. `664e9b9` OPEN is the M1
run spec. `548ef48` CLOSED (quest log observer). **UNFILED: that `power` is named in the
contract and absent from the surface; that no verb aggregates goal state; that
`quest-dismiss` cannot satisfy G6's "answered on purpose".**

**Verdict: PARTIAL.**

---

# T13 — The god-hand has no provenance, so the record cannot be counted

**Cause.** The mod journals a `dev` row for every `dev:*` **verb** (8 in the run). It has
**no equivalent for anything a human does in RimWorld's own debug menu.** Those actions
appear only as effects, beside an undifferentiated `dialog` row saying a window was open.

**What that cost this audit, and every metric derived from this run.**

**Three of the 23 colonist deaths never happened.** Tico, Haley and John (pawn 18282) all
die at tick 2,271,963 — the *same* tick, game paused — bracketed by `Dialog_Debug`,
`Dialog_NamePawn` and twenty `Dialog_DebugOptionListLister` rows. **Each appears in the
journal only in its own death and funeral letters: zero rows across the 1,459 rows
before them.** A `pawns --filter all` call ninety seconds earlier lists seven things on
the map and none of them. They were spawned in the dev menu, named, and destroyed.
(F-XC-38, F-S06-11, F-S06-12)

**One death was reversed and the journal has no event for it.** John (18294) dies at
`123633`/895, Dorian revives him through the debug menu — *"I threw you a bone"* /
*"Understood — thank you for John"* — and he dies again at `123633`/3756. The first row
stands unqualified. (F-XC-4c, F-S09-17)

**Corrected accounting: 23 death rows → 20 real → 19 distinct colonists.**

**And it produced a false belief the agent acted on.** Two pawns were nicknamed "John"
sixty seconds apart, either side of the debug deaths, and **the naming message carries no
pawn id**. The agent read *"Her title is corporate drone"* — which belonged to the
**dead** John — and switched its pronoun and backstory for the **living** one, whose own
message said *"His title is colonist."* The game's later text keeps using "he".
(F-S06-13, F-S06-14)

**The bridge's own god-hand is not gated either.** `pawn-fixture` called with **no
arguments** — a probe the agent expected to return a signature, as bare calls do
elsewhere — **executed `wound` ×3, `sadden` +4 and `tatter` on 3 items against a real
colonist.** Walton: 5 injuries, bleed rate 1.12, health 73%, four bad memories, apparel
89%→15%. The agent then used `dev:heal` and three `dev:spawn-thing` calls to conjure
replacement clothing out of nothing rather than reload the save — so the run's material
economy no longer reflects an unbroken chain of play. Filed during the run as `e1a9542`.
(F-S04-16, F-S04-17, F-S05-3, F-S05-5)

Dorian objected twice to `dev:*` use on a scored run — *"I didn't think you could cheat at
all"*, *"aw man you cheated that"* — and named the missing control himself: *"it's okay
because this would've been fixed if the session to disable dev tools was in."*

**Confirmation.** CONFIRMED by the zero-prior-row test over all 23 deaths, and by the
`dialog` row sequence.

**Issues.** `29824e4` OPEN — *"Dev suite needs organization and a runtime toggle: today it
is 17 loose verbs with no arming gate"* — **is exactly the missing control Dorian named.**
`e1a9542` OPEN (pawn-fixture bare call, filed during this run). `3a5ff6c` CLOSED (dev
staging bypasses PlaceWorkers). **UNFILED: that the journal has no provenance for
human/debug-menu actions, and that every metric derived from it is therefore a bound
rather than a measurement.**

**Verdict: PARTIAL for the dev-verb gate (`29824e4` would fix half of it); UNFILED for
journal provenance.**

---

# T14 — The run was a co-op, and only one player is in the record

**Cause.** Nothing marks a change as human-originated.

**The census.** Dorian intervened directly in game state **at least 22 times**, by his own
words: pausing and unpausing, accepting a joiner, completing a quest by hand, flicking a
shuttle's autoload, reviving a dead colonist, deleting a toxic-fallout event, deleting a
large area of crop, placing parkas and medicine, sending 411 survival meals, adding steel,
fast-forwarding construction, and rescuing a colonist the agent had not noticed was shot.

> *"I removed toxic fallout, that would've been bad"* · *"I removed a large portion of crop
> I told you to make"* · *"john never rescued ellie, you weren't notified she was shot? i
> intervened myself"* · *"I had to complete that quest manuall but it's filed"* · *"if you
> can't get a resource, ask. upped your steel"* · *"divine intervention once again, you're
> lucky"*

**Consequences.**

- The colony survived at least three moments it otherwise would not have.
- **Two colonists — Tony and Tanya — joined and/or died entirely inside a 21.8-minute
  window in which the agent issued zero commands** and hit two `API Error: 529 Overloaded`
  failures. Tanya joined at journal seq 1729 and died at seq 1789, **60 journal rows
  later**; no `rwa` row anywhere references her pawn id. The joiner-repair rule the
  contract demands was not skipped, it was **structurally inapplicable**. The agent's own
  roster audit afterwards never mentions her at all. (F-S09-19, F-S09-20, F-S09-21)
- The agent's five `API Error` failures — three 529s, two mid-response truncations —
  **exist only in the harness session log.** No `log.ndjson` row, no envelope, no journal
  row. **That axis is the only one that records the agent failing, as opposed to the tools
  failing.** (F-XC-5b)

**Confirmation.** CONFIRMED from harness quotes; the effects are CONFIRMED in the journal;
the attribution exists on **one axis only**.

**Issues.** **UNFILED entirely.** Nothing in either repo contemplates a provenance field,
a human-action journal type, or the fact that a "run" is a joint artifact.

**Verdict: UNFILED.**

---

# T15 — Handover discards exactly the knowledge that was expensive to get

**Cause.** Each session writes a document for the next. What survives is strategy and
state. What does not survive is tool knowledge — the thing that took the longest to
acquire.

**The decisive moment.** While the agent was preparing `SUCCESSOR-PROMPT.md`, Dorian
instructed: **"don't bring up the quirks we faced, if they happen again that's just more
proof to use to rebuild this service."** The resulting document contains none of the
failure classes hit in that same window. (F-S04-12)

That is a defensible research choice — it is how this audit got a second independent
sample — but it has a measurable price, and the next session should decide it
deliberately rather than inherit it:

- **~15 minutes and dozens of calls re-deriving API knowledge** the outgoing conversation
  already had error-free: `rwa --help`, `rwa verbs`, README greps, and **three separate
  `grep -rn` passes over `Source/AutoRimmer/*.cs`** to recover exact argument names. The
  outgoing conversation had made 112 calls in that slice with zero arg errors. (F-S10-14)
- The incoming conversation read `HANDOFF.md` and `RUN-CONTRACT-open-ended.md` and **never
  opened `SUCCESSOR-PROMPT.md` — the one file on disk containing `export
  RWA_RUN=openrun-20260902`.** The fix for T16 was one `cat` away, unread. (F-S10-12)
- **The handover documents carry wrong facts.** `summary.md` said rice `growDays 8`; it is
  **3** (F-S07-1). `SUCCESSOR-PROMPT.md` said the component vein was *"22 cells around
  (199,212)"*; that was a rollup's first-stack coordinate, and the vein is scattered
  (F-S04-15) — **the same rollup-`at` defect as T3**. `HANDOFF.md` names 5 junk research
  auto-picks; the journal records **16 in one slice alone** (F-S12-22).
- The scratchpad was **wiped between sessions**, so band-slice files and helper scripts
  had to be regenerated (F-S08-2). The journal **restarts at seq 1 on every relaunch**,
  discarding continuity across the boundary (F-S08-3).
- **The final `HANDOFF.md` is an accurate account of the *ending* presented as the account
  of the run.** Its causal chain for the wipe is honest and well-corroborated. What it
  omits is that the tool contributed: it says *"I rebuilt the mini-turrets and never
  re-checked the autocannon"* and never mentions that the `things` rollup it did check
  reported all six turrets at one coordinate (T3). (F-S12-27)

**Confirmation.** CONFIRMED.

**Issues.** `d2e1229` OPEN (*4.2 Play-loop skill*), `d32eadd` OPEN (*checklist budget +
lesson retirement*), `96d9315` CLOSED (playbook). **UNFILED: that tool-knowledge is the
category that reliably fails to survive a handover, and that the run directory has no
machine-readable "what the surface actually does" artifact.**

**Verdict: PARTIAL.**

---

# T16 — `RWA_RUN`: correctness depending on state nobody set, failing by quietly succeeding

**Cause.** `open_transcript` reads `RWA_RUN`; unset, it falls back to the live bench's
`sid` **with no warning printed anywhere the caller sees.**

**What happened.** The outgoing conversation prefixed `export RWA_RUN=openrun-20260902 &&`
to **all 112** of its bench calls. At 11:53:47 a new conversation issued its first command
without it, and never set it. **1,806 steps — 27% of the run — filed under
`20260903T123633*`, which no `openrun-20260902*` glob matches.**

**What that costs an auditor.** The dropped 27% contains **the entire military build-out,
five of the seven mech raids, the cultist raid, the eight-shambler wave, 9 of the 20 real
colonist deaths, 7 of the 8 `red_error` rows, and the wipe.**

**Why it was undetectable in flight.** Every response carries `"sid":"20260903T123633"` —
present on 200+ envelopes, alongside fields the agent's own scripts were already parsing.
But `sid` is the *game session* id; it equals the transcript directory name **only
because of the fallback**. Reading it would not have revealed the regrouping without
already knowing the fallback rule in `rwa`'s source. **There is no field that says "the
transcript run you are writing to is X."** (F-S10-11, F-S10-13)

**And it compounds.** `AUDIT-INPUT.md` — written by a participant — flagged this
correctly and prominently. It also inherited two errors this audit had to correct: the
step count is **6,674, not 6,688** (`ls | wc -l` counted `meta.json` and `log.ndjson` as
steps in each of seven directories), and there are **two** run-window journals, not three
(`20260902T002505` ends **12h25m before the run's first command** and belongs to
`m1-20260901`). (F-XC-1, F-XC-2)

**Confirmation.** CONFIRMED — sid census over every `result.json`.

**Issues.** `5eba561` **CLOSED** (*rwa transcript cap of 1000 steps bricks a long run with
a bare traceback*) is the nearest neighbour and covers the `-sNN` rotation, not the run
name. `3d53df2` OPEN is the same substrate. **The fallback itself is UNFILED.**

**Verdict: UNFILED.** This is the exemplar the task brief named, and nothing in either
repo states it.

---

# T17 — Refusals are protocol, faults are protocol, and both are `ok:false`

**Cause.** A correct refusal and a real fault are shaped identically.

**This run's numbers.** 337 refusals: 205 `bad-args`, 80 `busy`, 40 `unread-journal`, 6
`rwa-game-down`, 5 `bleedout-deadline`, 1 `unknown-op`. **`unread-journal` and
`bleedout-deadline` are the protocol correctly protecting the agent — 45 of 337.** `busy`
is flow control. Only `rwa-game-down` is a genuine fault.

The `bleedout-deadline` guard in particular **worked**: it refused an advance five times
during the wipe, and one of those refusals is what made the agent notice that a
lockdown-area binding had made a downed colonist unrescuable — *"133,486 ticks to walk 157
cells is absurd on its face, and that absurdity was the clue."* Dilly came home.

**The friction.** The refusal text is long, situation-generic boilerplate reused verbatim
across all five instances, each citing an unrelated prior run's incident (F-S12-25). And
the agent's own diagnostic wrapper printed a **blank `halted=`** on two separate
`red_error` halts because it queried a key that does not exist — the real one is
`halted_on` — **swallowing the one string that said why the advance stopped** (F-S11-7).

**Confirmation.** CONFIRMED.

**Issues.** `e440676` OPEN states this precisely, with figures from the *previous* run
(691 refusals, 53% protocol). **Its implementation exists on branch
`fix/e440676-error-class`, commit `f79bae1`, and its own last comment says "Not run
against a bench."** This run is 337 more data points for it.

**Verdict: COVERED — and the highest-confidence "just land it" item on this page.**

---

# T18 — Nothing was filed, because everyone assumed someone later would

**Cause.** A gap noticed mid-run has no cheap path to becoming an issue, so it becomes a
sentence in a chat log addressed to a future reader.

**The pattern, in Dorian's own words:**

> *"whoever reads this conversation will file that, you'll be stuck blind for now"* ·
> *"someone is reading these and filing gaps accordingly"* · *"another thing to file?"* ·
> *"we need to utilize the plan function, a note for who picks this up"* · *"you should get
> notified when a battery opens, another thing for the person reading this later"* ·
> *"no need, i'll move them this round, it's a gap and not your job"*

**Fourteen or more gaps were named aloud and deferred. Two issues were filed from the
entire 26-hour run** (`seekandkill a6b1aa0`, `autorimmer 3275f0c`), plus four the agent
filed in the moment (`5697725`, `cd92db7`, `8e5db24`, `e1a9542`, `36c03c9`). **This audit
is the "later reader," and the deferral is why it exists.**

**What else went unremarked in-run.**

- **8 `red_error` rows.** The `HealingEnhancer has null Part` error was delivered inside a
  journal range the agent demonstrably read (`355-journal`, count 67, covering seq 1495)
  and is never mentioned (F-S06-15). The three pawn-generation errors before the cultist
  raid — *"Could not generate a pawn after 70 tries"*, then 100, then *"Cannot force pawn
  Giggles to have role Invoker… is not psychically sensitive"* — halted the advance
  (`halted_seq: 2280`) and were never investigated; the agent never established whether
  the raid's stated psychic-ritual threat was nullified (F-S11-6).
- **At least 8 `not deep-saved` engine warnings**, each stating *"This will cause errors
  during loading"*, for Lords, Factions, Precepts and Humans. None investigated. **The
  final archival save — `FINAL-andbourne-wiped-spring-5503`, the artifact this whole audit
  examines — was written after several had recurred, with no check that it loads.**
  (F-S12-20, F-S10-10, F-S09-18)
- **Rot and deterioration ran unaddressed all run**: 19 simple meals, 118 venison, medicine,
  a sculpture, rice ×4, cloth ×2, potatoes, three hides — twenty-plus distinct
  "deteriorated away in storage" messages across four slices. Each was patched locally;
  **no colony-wide roofing sweep was ever run**, even after the agent diagnosed and fixed
  the closely-related growing-zone overlap. (F-S04-30, F-S07-20, F-S08-22, F-S09-13)
- **The research auto-picker stole hours 16 times in one slice** (Greatbow, Harpsichord,
  Wake-up production, Fertility procedures, Sterile materials, Biosculpting, Watermill
  generator, Tube television, Firefoam…). The agent caught **2**. No standing fix — e.g.
  always queuing two projects deep — was put in place after the pattern was first
  noticed. (F-S12-21)

**Confirmation.** CONFIRMED.

**Issues.** `d32eadd` OPEN (lesson retirement) is adjacent. **The `git-bug` friction
itself is UNFILED** — and this run has two concrete instances: a body line eaten by shell
backtick substitution requiring a repair edit (F-S02-9), and `git-bug bug | head -1`
grabbing the **wrong, closed** issue and retitling and relabelling it before the mistake
was caught and reverted (F-S03-7).

**Verdict: PARTIAL.**

---

# T19 — The `seekandkill` NRE made autonomous combat unusable for the rest of the run

**Cause.** `SeekAndKill.Dispatcher.InContact` dereferences a null in `squad.members` when
a **dormant** mech cluster is on the map. It is called from `MapComponentTick`, so it
throws **every tick** while seek is on.

**Evidence.** Journal `20260903T123633` seq 3008 and 3011, `Dispatcher.cs:357`, ref
`B9917D26`. The second row reads *"Duplicate stacktrace, see ref for original"* — **the
engine's own deduplication, which is what proves it was firing continuously rather than
twice.** Every subsequent `advance` halted on `red_error` within ~30–60 ticks until the
agent traced it to source and turned seek back off.

**Cost.** `seek-at-will` was **never re-enabled** — including through the final battle,
where automated squad response is exactly what the colony needed with one controllable
fighter left. The agent filed `a6b1aa0` in the seekandkill repo during the run.

**Confirmation.** CONFIRMED on both axes.

**Issues.** `a6b1aa0` OPEN in `seekandkill` (filed from this run). `3a0e042` CLOSED
(*seek-at-will verb: autonomous combat via SeekAndKill, so a raid is not per-pawn
micromanagement*) — **the capability this bug removed.** `seekandkill` has 46 issues, 8
open.

**Verdict: COVERED.** The fix is a null-guard at a named line. This is the cheapest
high-value item in the whole audit.

---

# What this audit could not determine

State these as limits in any plan built on this file.

1. **How much of the run is the agent's.** T14. Twenty-two human interventions, no
   provenance. Every efficiency figure is contaminated by an unknown amount, and two
   colonists' entire lifecycles happened outside the harness.
2. **Whether other deaths were reversed.** T13. The zero-prior-row test catches three
   debug spawns and one revival. It cannot catch a revival of a pawn that already had
   history, and the journal has no event for one.
3. **What the agent actually thought.** No extended thinking was recorded. Stated reasons
   are narration addressed to an audience.
4. **What happened in the 26 orphans.** 106,874 ticks with no envelope. The surrounding
   `state.tick` values bound them; nothing describes them.
5. **Whether the queued `rwa-game-down` commands were ever replayed.** The error says the
   command *"will be consumed with stale-on-restart at the next launch."* Nothing in the
   record verifies that a stale mutating command was discarded rather than executed
   (F-S09-2).
6. **Whether the final save loads.** Eight `not deep-saved` warnings, each promising load
   errors. Never tested.
7. **The true cost of most `silent-fallback` events.** 52 `ignored_args` envelopes; a
   handful are traced to a consequence, most are marked UNKNOWN because the counterfactual
   is not in the record.

---

# The shortest path from here

Ordered by evidence strength × cheapness, not by theme number.

| # | what | why this order | theme |
|---:|---|---|---|
| 1 | Land `e440676` (error classes) — it is built, on a branch, never benched | 337 more data points this run; removes the noise floor every other diagnosis fights | T17 |
| 2 | Fix `a6b1aa0` — a null-guard at `Dispatcher.cs:357` | One line; restores the autonomous-combat capability `3a0e042` shipped | T19 |
| 3 | Land `29824e4` (dev arming gate) + `e1a9542` (bare `pawn-fixture`) | Dorian named the gate himself; a signature probe currently wounds a colonist | T13 |
| 4 | Reopen the `7382bdd` ruling | The remedy it chose was read 0 times in 52 chances. This is the spine. | T0 |
| 5 | Make `ok:true` mean *what you asked for happened* | Five instances filed separately; the class is not | T1 |
| 6 | Emit the transcript run name in every envelope, and warn when `RWA_RUN` falls back | 27% of this run was silently misfiled and it is unfiled | T16 |
| 7 | Kill verify-through-the-capped-instrument: rollup `at`, `zones` page, `hediff_cap` | Two of these are links in the wipe's causal chain | T3 |
| 8 | Provenance on journal rows: who caused this — verb, human, or debug menu | Without it no metric from a watched run is a measurement | T13, T14 |

