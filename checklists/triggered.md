# triggered.md — checks that fire on a moment, not a cadence

Three trigger classes: **colony start** (once), **your own acts** (verify what
you just did, before advancing), and **the world's events** (respond at the
letter, not the casualty). Cost is one drill-down per firing — a turn where
nothing triggers pays nothing. Ledger: every firing logs, whatever the verdict.

## Colony start

The game ships its own ordered curriculum —
`Data/Core/Defs/Tutor/Instructions.xml`, the scripted tutorial, defName
sequence verified on 96d9315. This is that sequence with the camera steps
dropped and our verbs substituted. Two of its steps were also the two flagship
gaps the action-surface audit found independently (`AddBillSimpleMeal`,
`SelectResearchProject`) — two methods converging is why this list is trusted.

Run it top to bottom on any fresh colony; every line logs a verdict.

**Before the fresh colony exists at all**: a new bench colony comes from a
`--quicktest` launch, and that launch fails deterministically — map gen dies,
`status` reads `menu`, the launcher's stdout carries no stack — for as long as
a previous session's `autostart.rws` sits in the bench's `Saves/`. Park the
save first; relaunching without doing so also destroys the only copy of the
stack (`Player.log` is truncated at process start).
[[quicktest-and-autostart-collide]] Launching is the orchestrator's act, never
a worker's (`CLAUDE.md`), so this line is a precondition to hand over, not one
to execute.

1. **Stockpile** — `zone {op:"add", kind:"stockpile", rect:[x,z,w,h]}` near
   the future kitchen; configure the filter at creation (the same discipline
   as growing zones). `op` and `kind` are ARGS, not words in the op name:
   `ZoneVerbs.Zone` reads `a.Str("op","add")` then `a.Str("kind")`, and
   `rwa zone add stockpile …` dies in `parse_op_args` on the bare word `add`
   (exit 2) before the mod ever sees it.
2. **Unforbid starting resources** — `unforbid {rect:[x,z,w,h]}` over the
   landing scatter. **Name the arg**, and on the CLI force the type:
   `rwa unforbid --rect:json '[x,z,w,h]'`. A bare `--rect 10,20,5,5`
   autotypes to the STRING `"10,20,5,5"` (`rwa`'s `autotype` leaves anything
   non-numeric alone) and comes back `rect must be [x,z,w,h]` from
   `DesignateEngine.ReadRect`. Drop-pod starts land with their own gear
   forbidden (`ScenPart_PlayerPawnsArriveMethod.DoDropPods` passes
   `forbid: true`); the tutorial's own second step is
   `UnforbidStartingResources`. [[unforbid-before-expecting-pickup]]
3. **Shelter** — walls, door, beds, light. Until 3.3 lands this is staged per
   4.3's fixture (dev-conjured, journaled); after 3.3:
   `place-layout templates/bedroom.ir.json`. Verdict `blocked` until then, not
   a silent skip.
4. **Growing zone, plant named in the same call** —
   `zone {op:"add", kind:"growing", rect:[x,z,w,h], plant:"Plant_Rice"}`.
   The verb refuses a plantless zone (ZoneVerbs guard) unless you pass the
   escape hatch `allow_unset_plant:true`; the tutorial's `SetToGrowRice` is
   its own step because the default is a real crop, not a blank.
   [[growing-zone-default-is-potato]]
5. **Equip weapons** — count armed vs violence-capable roster
   (`pawn {id:<n>, sections:["equipment"]}` — `pawn` is SINGLE-PAWN and `id`
   is required, so this is one call per colonist, N round-trips; take the
   roster from `pawns` first. Spares via `things {category:"weapons"}`), then
   `equip`/`assign {auto_arm:true}`. No alert will ever prompt this.
   [[weapons-have-no-alert]]
6. **Meal source and its bill** — a stove is not food: `Alert_NeedMealSource`
   tests only that a `isMealSource` building exists, and is silent before
   day 2. The stove build waits on 3.3; the BILL waits on 3.6 (`48f666c`) —
   log `blocked` with those ids. The read half (`bills`) works today.
7. **Work priorities roster scan** — `pawn {id:<n>, sections:["work"]}`,
   **one call per colonist** (N round-trips; the roster comes from `pawns`):
   every essential work type (Doctor, Cook, Grow, Construct, Haul) covered by
   someone capable, and no bill-relevant type checked only on its likely
   patient. [[who-will-actually-do-it]] **Doctor is the exception to "someone
   capable": coverage means TWO.** One doctor reads as covered right up to
   the moment the doctor is the casualty, which is the likeliest casualty
   there is — M1 lost both its casualties that way, to blood loss, not to the
   animal. [[one-doctor-is-zero-doctors]]
8. **Research: select a project** — `research-set`. The model would accept any
   project (`SetCurrentProject` checks nothing); the verb reproduces the
   widget gate, so refusal here means prerequisites, not breakage.
9. **Home area + roof spot-check** — `areas`; home auto-expands on
   construction but dev-spawned structures and edge cases don't
   (96d9315 comment 1). Fire response and auto-roof both key off home.
10. **Defensive cover before the first raid** — structural count (turrets,
    sandbags, barricades via `things {category:"buildings"}`), never the
    alert: `Alert_NeedDefenses` fires only days 2–5 and one sandbag anywhere
    silences it for good. [[alert-need-defenses-self-silences]] Build waits
    on 3.3; log `blocked`.
11. **Combat roles** — traits first (FSWA already forces Brawlers melee-only),
    then passion, then skill. The weighting formula is a proposal awaiting
    Evan — apply it as a tiebreak, not a rule. [[combat-role-passion-over-skill]]
12. **Standing combat posture** — confirm the posture verb ran. Paint an
    `Area_Allowed` over base + fields + cleared ground (`area allowed create`,
    then `area {kind:"allowed", op:"add", rect:[…]}`), then one call:
    `posture {area:"<label>", seek:"auto"}`. It sets all three settings that
    must agree and names every pawn it refused. Verdict is
    `digest.posture.ok`; anything in `posture.flee_risk` is the M1 state and
    is not a start posture. [[seek-off-is-a-decision-to-flee]]

## On your own acts

### bill-who-will-do-it
- when: act: after queuing any bill (`surgery-add` today; 3.6's bills later)
- read: `pawn {id:<n>, sections:["work"]}` — one call per colonist, roster
  from `pawns` (`pawn` is single-pawn; `id` is `IntReq`)
- flag: no pawn OTHER than the patient (or the bench's blocker) has the
  relevant work type non-zero
- act: `work-priorities` / enable the work type on a capable pawn
- why: `WorkGiver_DoBill.ShouldSkip` requires `billGiver != pawn` — if the
  only eligible doctor is on the table, no job is ever produced and nothing
  looks broken. Checkbox mode is priority 0/3 (`WidgetsWork.DrawWorkBoxFor`);
  skill is not assignment. [[who-will-actually-do-it]]
- retire-when: bill-creating verbs report eligible workers in their result.

### plant-set-at-creation
- when: act: creating or reconfiguring a growing zone
- read: none needed at creation — `zone {op:"add", kind:"growing", …}`
  refuses without `plant` (unless `allow_unset_plant:true`). To inspect an
  existing zone use `zones` (the guarded backing-field route), never anything
  that touches the live getter.
- flag: `plant_configured:false` in the `zones` row — genuinely unconfigured,
  and one read of the raw getter away from being potatoes forever (the getter
  assigns AND scribes on first touch). Sanity-check `plant_source` reads
  `"backing-field"` and not `"unavailable"`, which would mean the guarded
  route is gone and the answer is not trustworthy.
- act: `zone {op:"edit", id:<n>, plant:"Plant_Rice"}` now, while the choice is
  still free. `edit` needs `id` (`a.IntReq("id")`) — it is not optional.
- why: [[growing-zone-default-is-potato]] — and the wrong earlier version of
  this lesson taught a symptom that never occurs.
- retire-when: never; the deadline is structural.

### bench-siting
- when: act: placing any workbench, or the stockpile that feeds/drains one
- read: `room-at` the target cell → real room, roofed; kitchen additionally
  wants the room's cleanliness stat headroom (see below)
- flag: unroofed, roomless, or the wrong room for the job
- act: re-site before building — moving a blueprint is free, moving a built
  bench is not
- why: benches and product piles deteriorate unroofed, and room context is a
  rule the API cannot derive ([[benches-go-indoors]], Evan). For kitchens the
  rule has a number: a meal rolls poison against the ROOM's `FoodPoisonChance`
  stat — a curve over room Cleanliness reaching 0 at ≥ −2, 2.5% at −3.5, 5%
  at −5, and a flat 2% when cooking roomless
  (`CompFoodPoisonable.Notify_RecipeProduced` rolls
  `pawn.GetRoom()?.GetStat(RoomStatDefOf.FoodPoisonChance)` and falls back to
  the stat's `roomlessScore`; `RoomStats.xml`). Cooking outside poisons one
  meal in fifty regardless of the cook.
- retire-when: benches are placed from `templates/`, which guarantee the room.

### home-area-after-build
- when: act: after `place-layout` (3.3, `1adc737` — the verb does not exist
  yet; today this fires after dev-spawn staging and any hand-built batch)
- read: `areas` — the new footprint is inside Home
- flag: uncovered cells
- act: expand Home (`area` verbs)
- why: Home auto-expands on ordinary construction but not around dev-spawns;
  fire response, `Alert_FireInHomeArea`, and popper auto-rebuild all key off
  Home (96d9315 comment 1; `Building_FirefoamPopper.SpawnSetup` reads
  `areaManager.Home`).
- retire-when: staging tooling guarantees coverage.

## On the world's events

### threat-condition-letter
- when: event: letter for toxic fallout, aggressive animals, or any condition
  that punishes being outdoors
- read: the letter itself, then `areas`
- flag: colonists' allowed areas still include open ground
- act: tighten allowed areas and door policy NOW — at the letter, not after
  the first casualty
- why: this timing is vanilla's own: `GameCondition_ToxicFallout.Init()`
  teaches ForbiddingDoors + AllowedAreas at Critical the moment the condition
  begins; `IncidentWorker_AggressiveAnimals` teaches ForbiddingDoors at
  Critical, AllowedAreas at Important — severity is per call site, one concept
  can be taught at two tiers (96d9315 verification, correction 3).
- retire-when: a mod-side condition-response procedure takes it (ladder's mod
  rung — every branch here is computable, so it is a candidate).

### raid-letter
- when: event: `letter` with a threat def / `advance` halts on `threat`
- read: seek posture and armament per pawn —
  `pawn {id:<n>, sections:["state","equipment"]}`, one call per colonist
  (`PawnVerbs.PawnDetail` takes `ctx.Args.IntReq("id")`; take the roster from
  `pawns` first). `state.seek` is the posture. **Do NOT read it with
  `seek-at-will`:** that verb takes a pawn list, WRITES, and hard-refuses when
  SeekAndKill is absent (`SeekVerbs.SeekAtWill` throws on `!SeekMod.Present`).
  Then the three per-pawn weapon alerts in `digest.alerts`
- flag: unarmed violence-capable colonists; any shield belt paired with a
  ranged weapon (`Alert_ShieldUserHasRangedWeapon`,
  `Alert_HunterHasShieldAndRangedWeapon`) — and, from the same family,
  a Brawler holding a gun (`Alert_BrawlerHasRangedWeapon`). **Three** per-pawn
  weapon alerts, not two; all three are mismatches, none is coverage
  ([[weapons-have-no-alert]])
- act: arm from spares (unforbid first — drops are forbidden), fix pairings
  (melee for the belt wearer or shed the belt); then let SeekAndKill fight,
  draft by exception only. **Seek must already be on when the letter
  arrives** — switching it at the letter is too late, because the fight is
  decided inside the advance that carried it, and seek OFF is not neutral: it
  is a decision that armed colonists scatter individually away from help.
  M1's two deaths were both flee-branch deaths, one of them 150 cells from
  the base with a rifle at Shooting 10 unfired.
  [[seek-off-is-a-decision-to-flee]]
- why: vanilla treats pre-combat equipping as Critical on EVERY raid
  (`IncidentWorker_Raid` teaches EquippingWeapons and ShieldBelts at
  Critical) yet ships no standing armament signal. [[weapons-have-no-alert]]
- retire-when: armament joins the digest/sample row and the pairing check
  moves to a mod-side pre-raid procedure.

### raid-end
- when: event: `threats.hostiles` transitions to 0 (fog caveat in turn.md)
- read: `pawns {filter:"hostile"}` (downed+bleeding flags ship today),
  `pawns` for own downed, draft states
- act, in order — the order is the lesson:
  1. rescue downed colonists (bleeding is the only time-critical step);
  2. finish off or capture downed raiders — which one is a world fact, *is
     there a prison* (`rooms` publishes `prison_cell`);
  3. **undraft everyone** — a drafted pawn neither eats nor works, and
     `SeekRegistry.ShouldSeek` requires `!pawn.Drafted`, so your finishers
     have seek suppressed exactly when a raider might stand back up;
  4. `unforbid` the battlefield ([[unforbid-before-expecting-pickup]]);
  5. re-read armament — drops and deaths both moved it.
- why: this is the INTERIM manual form of git-bug `cc8988c`, which Evan has
  already decided belongs in the mod ("what can be in the mod should be");
  every branch above is computable from published state. It sits here only
  until that procedure ships.
- retire-when: cc8988c lands — this item then shrinks to "confirm the
  procedure ran and undraft count is zero".

### power-incident
- when: event: Zzzt letter, or any fire event near the power room
- read: `fires` (scoped alerts lie — this verb doesn't), then
  `things {def:"FirefoamPopper"}` once quiet
- flag: fewer poppers standing than the template placed, or an unbuilt popper
  blueprint
- act: ensure the rebuild happens — a popped popper is DESTROYED and
  auto-rebuild only queues a blueprint (75 steel + 1 component + construction
  5), and only if it stood in Home with Firefoam researched
  (`Building_FirefoamPopper.SpawnSetup`/`Destroy`)
- why: the popper is one-shot; the room's protection silently lapses after
  every save. `templates/power-room.md` carries the full mechanics.
- retire-when: a condition predicate (1.6) watches popper count, or rwtest
  asserts it (4.4's final rung).

### roster-change
- when: event: `death`, recruit, or any colonist joining/leaving
- read: armament vs the new roster; work coverage
  (`pawn {id:<n>, sections:["work"]}`, one call per colonist)
- flag: **Doctor first** — fewer than two pawns with Doctor enabled
  ([[one-doctor-is-zero-doctors]]); then armed count below violence-capable
  count; then any other essential work type now uncovered
- act: enable Doctor on a second pawn BEFORE anything else on this list (in
  M1 the fix landed after the first death and was too late for the second),
  then re-arm, re-assign roles (traits → passion → skill), re-check coverage
- why: both armament and coverage are defined RELATIVE to the roster, so any
  roster change silently moves them. [[weapons-have-no-alert]],
  [[combat-role-passion-over-skill]], [[who-will-actually-do-it]]
- retire-when: armament and coverage become sampled/derived fields with their
  own trip-wires.

### casualty-halt
- when: event: `advance` returns `reason:"casualty"` — an own-faction pawn
  went DOWN or DIED while time was running, and the mod stopped the advance
  at that tick rather than running to completion (git-bug 722c951)
- read: `halted_on` first — it names the pawn, `pawn_id`, the event class
  (`downed`/`death`), `pawn_kind` and the tick. Then `triage`, which is the
  whole answer in one call: the bleed clock, every candidate rescuer with the
  game's own refusal reason, `travel_ticks` + `carry_ticks`, a `verdict`, and
  `act` — the exact `rescue` envelope with both ids filled in
- flag: `verdict:"too-slow"` (the patient dies before anyone arrives — this is
  also what `advance` will REFUSE on next, code `bleedout-deadline`),
  `no-rescuer` (nobody is capable or the gates all refused — read
  `rescuers_gated_out`, it says why), `no-path` (reachability, not staffing)
- act: **send `triage`'s `act` verbatim.** `rescue` FORCES the job through
  `Pawn_JobTracker.TryTakeOrderedJob` and interrupts `LayDown`; a
  work-priority flip does not, and that is the single sharpest M1 finding —
  `rescue` was shipped, solved this exactly, and was called ZERO times in 195
  ops while the response actually tried was Chili's Doctor 0 → 3, after which
  she stayed asleep for ~6,100 ticks and Captain died. Then `tend`, then
  `roster-change` if the pawn is out of action. Only then advance again
- why: the M1 run was HANDED this news and rode past it. Table went down at
  214,599 inside step 148, whose own result carried
  `journal_seq:[125,128]`; the run advanced five more times and he bled for
  11,335 ticks. The halt is the mod refusing to let that be a silent choice.
  [[read-every-return-or-lose-a-colonist]], [[one-doctor-is-zero-doctors]]
- retire-when: never while an advance can cross a downing — this is the
  trigger the mod itself fires, and the checklist line is what makes the
  response a procedure rather than a reaction

### postmortem-trigger
- when: event: a colonist `death`, a colony loss, or a near-miss that cost
  real recovery (a downed colonist rescued late, a fire that reached a
  building)
- read: the journal delta around the event (`journal --since <seq>`) and the
  run's `checklist.ndjson`; `postmortem.md` §Inputs names the rest
- flag: any of the three. The near-miss case is a judgement call — log the
  reading that made it one
- act: run `postmortem.md` — the full procedure after a death or loss, the
  light pass otherwise. After a loss the NEXT session runs it before acting
  (`playbook/SESSION-START.md` position 9). A deterministic output goes to
  the mod rung, not into a note (postmortem.md step 5)
- why: `PLAY-LOOP.md` §Artifacts already owes a post-mortem on any death, but
  it owed it in prose — so nothing logged, and no auditor could tell whether
  it ran. M1 `m1-20260831` invented this ledger id mid-run for exactly that
  (two deaths, day 4) and it belonged to no file. The item adds no duty; it
  puts the existing duty in the ledger.
- retire-when: never while post-mortems are run by hand — this is the item
  whose output is the learning system itself.

### time-control-drift
- when: event: ticks passed that the loop did not order — seen at the read
  when `digest.time.tick` outruns the tick the last advance reported, or at
  the pre-advance gate when `status.paused` is false
  (`playbook/PLAY-LOOP.md` invariant 11)
- read: `rwa status` → `paused` and `tick`; then `journal --since <last_seq>`
  across the unobserved window
- flag: any unordered tick delta. Log its SIZE — that number is the whole
  evidence for how expensive this failure mode is
- act: `pause` first, then read the entire delta before acting on anything in
  it: the window is unobserved, not empty. Name the window in the session
  summary.
- why: a dead `rwa` client does not stop the game. The mod keeps advancing
  toward its target when the client dies (tool error, interrupt, timeout);
  M1 measured `was_advancing:true, speed_before:Ultrafast` after a lost tool
  result, and lost ~60,000 ticks — one full in-game day — in one window,
  more than once. "The agent owns time" holds only while the client lives.
  (M1's first entry blamed a stray keypress on the watched window; the day-5
  correction is the reading to trust.)
- retire-when: the mod stops advancing when its client goes away (a
  client-liveness deadline on `TimeDriver`), or `advance` becomes something
  a dead client rolls back.
