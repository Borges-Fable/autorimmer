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

1. **Stockpile** — `zone add stockpile` near the future kitchen; configure the
   filter at creation (the same discipline as growing zones).
2. **Unforbid starting resources** — `unforbid` over the landing scatter.
   Drop-pod starts land with their own gear forbidden
   (`ScenPart_PlayerPawnsArriveMethod.DoDropPods` passes `forbid: true`);
   the tutorial's own second step is `UnforbidStartingResources`.
   [[unforbid-before-expecting-pickup]]
3. **Shelter** — walls, door, beds, light. Until 3.3 lands this is staged per
   4.3's fixture (dev-conjured, journaled); after 3.3:
   `place-layout templates/bedroom.ir.json`. Verdict `blocked` until then, not
   a silent skip.
4. **Growing zone, plant named in the same call** — `zone add growing
   {rect, plant}`. The verb refuses a plantless zone (ZoneVerbs guard); the
   tutorial's `SetToGrowRice` is its own step because the default is a real
   crop, not a blank. [[growing-zone-default-is-potato]]
5. **Equip weapons** — count armed vs violence-capable roster
   (`pawn {sections:["equipment"]}` per pawn; spares via
   `things {category:"weapons"}`), then `equip`/`assign {auto_arm:true}`.
   No alert will ever prompt this. [[weapons-have-no-alert]]
6. **Meal source and its bill** — a stove is not food: `Alert_NeedMealSource`
   tests only that a `isMealSource` building exists, and is silent before
   day 2. The stove build waits on 3.3; the BILL waits on 3.6 (`48f666c`) —
   log `blocked` with those ids. The read half (`bills`) works today.
7. **Work priorities roster scan** — `pawn {sections:["work"]}` across the
   roster: every essential work type (Doctor, Cook, Grow, Construct, Haul)
   covered by someone capable, and no bill-relevant type checked only on its
   likely patient. [[who-will-actually-do-it]]
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

## On your own acts

### bill-who-will-do-it
- when: act: after queuing any bill (`surgery-add` today; 3.6's bills later)
- read: `pawn {sections:["work"]}` across the roster
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
- read: none needed at creation — `zone add growing` refuses without `plant`.
  To inspect an existing zone use `zones` (the guarded backing-field route),
  never anything that touches the live getter.
- flag: a zone whose plant reads null from the backing field — genuinely
  unconfigured, and one read of the raw getter away from being potatoes
  forever (the getter assigns AND scribes on first touch).
- act: `zone edit {plant}` now, while the choice is still free.
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
  at −5, and a flat 2% when cooking roomless (`CompFoodPoisonable.cs:38`;
  `RoomStats.xml`). Cooking outside poisons one meal in fifty regardless of
  the cook.
- retire-when: benches are placed from `templates/`, which guarantee the room.

### home-area-after-build
- when: act: after `place-layout`, any build batch, or dev-spawn staging
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
- read: seek posture (`seek-at-will` state), armament
  (`pawn {sections:["equipment"]}`), the two shield-belt alerts in
  `digest.alerts`
- flag: unarmed violence-capable colonists; any shield belt paired with a
  ranged weapon (`Alert_ShieldUserHasRangedWeapon`,
  `Alert_HunterHasShieldAndRangedWeapon` — the only two weapon alerts that
  exist, and they are per-pawn mismatches, not coverage)
- act: arm from spares (unforbid first — drops are forbidden), fix pairings
  (melee for the belt wearer or shed the belt); then let SeekAndKill fight,
  draft by exception only
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
  (`pawn {sections:["work"]}`)
- flag: armed count below violence-capable count; an essential work type now
  uncovered (the doctor just died)
- act: re-arm, re-assign roles (traits → passion → skill), re-check work
  coverage
- why: both armament and coverage are defined RELATIVE to the roster, so any
  roster change silently moves them. [[weapons-have-no-alert]],
  [[combat-role-passion-over-skill]], [[who-will-actually-do-it]]
- retire-when: armament and coverage become sampled/derived fields with their
  own trip-wires.
