# turn.md — trip-wires over the read you already made

Evaluated against the digest at **every** return from `advance`. Nothing here
issues a query; this file is the *meaning* of numbers the loop is already
holding. **Every means every, and a targeted query is not a read**: M1 lost a
colonist inside a six-iteration polling loop whose only read was
`pawns {filter:"hostile"}` — the downing, two alerts and the medical
emergency were all in the journal delta and digest nobody asked for
([[read-every-return-or-lose-a-colonist]]). Every trip-wire is a state predicate — the `until:condition` matcher
spec 1.6 (`fc287ba`) will eventually take over; each item names the predicate
it becomes. Ledger: log only firings (`verdict:"action"`), per
`checklists/README.md`.

### food-days-floor
- when: turn
- read: `digest` → `resources.food_days` (and `food_needers`)
- flag: `food_days < 6` *(proposed)*
- act: grow the food supply at the right rung, and confirm a meal bill is
  live (`production-still-runs` in daily.md). **Crops are NOT a designation:**
  `WorkGiver_Grower.PotentialWorkCellsGlobal` walks `zoneManager.AllZones`
  with no designation check, so a growing zone sows and harvests itself —
  the act is `zone {op:"add", kind:"growing", rect:[x,z,w,h], plant:"…"}`, or
  `zone {op:"expand", id:N, rect:…}` on the zone you have, or fixing
  `allow_sow`/`allow_cut`. Wild plants and hunting DO need one:
  `designate {type:"harvest", rect:[x,z,w,h]}` /
  `designate {type:"hunt", …}`. Either way it is not a bill retry —
  ingredient-finders haul what is already harvested, nothing more
  ([[materials-are-a-standing-loop]]).
- why: three stacked lags. `Alert_LowFood` is muzzled for the first 150,000
  ticks — 2.5 game days — by `GetReport`'s
  `if (TicksGame < 150000f) return false;` (verified this session; it uses
  `TicksGame`, not `GenDate.DaysPassed`, so even the audit's grep for
  time-scoped alerts missed it). Its threshold, `NutritionThresholdPerColonist
  = 4f`, is *nutrition per person*, which the game's own explanation string
  calls "days" — but a colonist eats ~1.6 nutrition/day, so "4 days" is ~2.5
  eating-days (2d9a1da research). And the response takes days: sowing is not
  food until harvest. 6 buys roughly 3.7 real days of runway. Note the digest
  divides by colonists+prisoners; the alert's trigger divides by colonists
  only — the digest's figure is the conservative one.
- becomes: `advance until:{condition: food_days < 6}` when 1.6 lands.
- retire-when: 1.6 carries it, or sampling (2d9a1da) turns the floor into a
  slope with a computed zero-day.

### meds-floor
- when: turn
- read: `digest` → `resources.meds` vs colonist count
- flag: `meds < colonists` *(proposed — one unit each is Evan's "stocking up on
  medicine" made countable; calibrate from runs)*
- act: buy at next trader, craft herbal from a healroot zone, or harvest wild
  healroot. Stockpiles-only caveat: `resources.scope` says so — meds loose on
  the ground after a raid count zero until hauled
  ([[unforbid-before-expecting-pickup]], [[stockpile-scope-hides-your-own-supplies]]).
- why: one of Evan's three named gaps (food, medicine, weapons — 96d9315
  comment 7); no vanilla alert leads on medicine stock.
- retire-when: sampling carries a medicine series and a restock policy hardens
  into the mod or a template stockpile.

### power-deficit
- when: turn
- applies-when: any powered building exists
- read: `digest` → `power.battery_days`, `power.nets_with_generator`
- flag: `battery_days != null && battery_days < 1.5` *(proposed)*, or
  `nets_with_generator == 0` while anything draws
- act: refuel/repair the generator, or shed draw (`flick` off non-essential
  consumers), or build generation — noting new generators and batteries are
  wealth, and wealth prices the next raid ([[wealth-buys-bigger-raids]]).
- why: `battery_days` is non-null exactly when draw exceeds generation — the
  digest's own field docs call it the checklist figure; it moved here because
  it is free at every read, not once a day. `nets` alone lies: the game counts
  a bare battery as a power source, so gate on `nets_with_generator`
  (DigestVerb's rider on 2.6 should-fix 5).
- becomes: `advance until:{condition: battery_days < 1.5}` when 1.6 lands.
- retire-when: 1.6 carries it.

### materials-designation-loop
- when: turn
- read: `digest` → `resources.wood`, `resources.steel`
- flag: `wood < 100` or `steel < 50` *(proposed)* with no matching designation
  batch outstanding
- act: **three different calls for three different jobs, and the wrong one is
  silent.** (`type` is an ARG: `DesignationVerbs.Designate` reads
  `a.StrReq("type")`, and `rwa designate chop …` dies on the bare word.)
  - **wood** — `designate {type:"chop", rect:[x,z,w,h]}` over the next batch;
    one rect, one call.
  - **ore** — `nearest {def:"MineableSteel", from:…}` FIRST, then
    `designate {type:"mine-vein", cells:[…]}` seeded off the cells it names.
    `mine-vein` flood-fills the whole contiguous vein
    (`Designator_MineVein.DesignateSingleCell`), so one seed does the body a
    rect can only nibble at, and it takes ore only
    (`ThingDef.building.veinMineable`). Read `designated`, not `accepted` —
    one accepted cell can paint forty.
  - **plain rock for blocks** — `designate {type:"mine", rect:[…]}`. On a
    map already carrying 1,800 stone chunks this is close to pure waste; on a
    mountain base it is the point. The verb cannot know which, so it reports:
    read `composition`.
  **`map-view` IS THE WRONG INSTRUMENT FOR SITING EITHER MINE CALL.** Its `%`
  glyph collapses `sandstone`, `marble` and `compacted steel` into one
  character, so a rect chosen off it cannot be aimed
  ([[a-glyph-is-topology-not-identity]]). m1-20260901 aimed at the steel face
  and designated 14 cells of whatever rock was exposed; 13 steel cells inside
  the same rect were still standing 20 days later. Use `map-view` to find the
  face, a def-keyed query to designate it.
  Wood and stone only flow while a designation exists; colonists never fell
  or mine on their own initiative ([[materials-are-a-standing-loop]], Evan:
  "colonists don't put down trees themselves"). Crops are the exception and
  belong to `food-days-floor`, not here.
- also: **read the designate envelope back before moving on.** Three fields,
  and each is a different correction:
  - `accepted_unreachable > 0` — those targets lie outside EVERY capable
    colonist's allowed area and the designations are inert. `reach.areas[]`
    names the area and its id; the fix is `area {kind:"allowed", op:"add"}`,
    NOT re-designating ([[a-designation-outside-the-allowed-area-does-nothing]]).
    A batch where nothing is workable is REFUSED outright, with the correction
    in `refused.hint`.
  - `reach.enabled == 0` while `reach.capable > 0` — the area is fine and
    every miner has Mining switched off. That is `work-priorities`, and it is
    the other half of the 128 designated `MineableSteel` cells m1-20260901
    never mined ([[who-will-actually-do-it]]).
  - `composition` — the per-def rollup of what actually landed, with
    `mineable_thing` and the yield each cell drops. `MineableSteel: 4,
    Marble: 8` is the answer `accepted: 14` never was.
- why: a bill stalling at zero input pages THIS item, not a bill retry — the
  retry hides the real correction. Stockpiles-only caveat applies here too, and
  it is the trap that cost m1-20260901 five days: `resources.steel` read 0 while
  811 steel lay on the map, and the fix was `unforbid` plus an allowed-area
  extension, NOT more mining. Read `things {def, detail:true}`'s `forbidden` and
  a `place-layout --dry-run`'s `available` vs `in_stockpiles` before designating
  anything ([[stockpile-scope-hides-your-own-supplies]]).
- becomes: nothing — the three readings above are facts about a CALL, not
  state predicates, so 1.6's `until:condition` cannot carry them. The standing
  half (are yesterday's designations still reachable today?) is a predicate and
  waits on a mod-side watch; see the lesson's retire-when.
- retire-when: a mod-side standing-designation policy (candidate for the
  ladder's mod rung once the policy is Evan-ratified). The AIMING half is done
  — `composition`, `accepted_unreachable` and `reach` ship (git-bug
  `855117a`, `b7359fa`) — so what is left here is the loop, not the reads.

### bill-produces-nothing
- when: turn
- applies-when: any production bill exists
- read: `bills` → every bill's **`health`**. One word per bill. The three that
  are never acceptable on a food or medicine bench:
  `no-matching-ingredient` · `asleep-no-matching-ingredient` · `filter-empty`.
- flag: any bill whose `health` is one of those three. Do the act in that bill's
  own **`remedy`** field, which names the verb — and when it says
  **NO BILL LEVER FIXES THIS**, believe it and stop sending `bill-set`.
- act: `remedy` names it. The four that occur:
  `unforbid {things:[…]}` (ids are in `ingredient_match.rejected_sample`) ·
  `bill-set {allow:[…]}` or `{special:{…}}` when the reason starts
  `bill-filter:` · `bill-set {ingredient_radius:"unlimited"}` · and for a
  `recipe-fixed:` reason, **nothing on the bill** — the recipe's own
  `fixedIngredientFilter` rejects those things and no lever widens past it.
  Change the ingredients or the recipe.
- why: **this is what killed run m1-20260901** (git-bug eef837a). The butcher
  bill read `suspended:false`, an unlimited radius, and a filter whose
  `allowed_defs` contained `Corpse_WildBoar` — with a wild boar corpse on the
  butcher spot's own cell — and it produced nothing for twenty days while three
  colonists starved. Every one of those readings was TRUE. The rejection was
  per THING and per SPECIAL FILTER: `ButcherCorpseFlesh.fixedIngredientFilter`
  disallows `AllowRotten`, and `Bill.IsFixedOrAllowedIngredient` consults the
  RECIPE's filter before the bill's, so a corpse past `CompRottable`'s 2.5-day
  rot start can never be butchered by that recipe. A def-level filter summary
  cannot say that; `health` and `ingredient_match.rejected` can.
- also: **`next_ingredient_search_tick` is still published and is still not the
  answer.** It is a 500–600 tick back-off that rearms on every failure, so a
  future value is normal for any starving bill and says nothing about how long.
  Read `ingredient_search.state` (`asleep`/`ready`) and
  `ingredient_search.consecutive_failed_searches` instead — and note that
  `asleep-will-retry` and `asleep-no-matching-ingredient` are DIFFERENT
  verdicts, because only the second needs you (git-bug d9d6c12).
- becomes: `advance until:{condition:{path:"…health", eq:"workable"}}` once 1.6
  can address a per-bill path.
- retire-when: never. This is a state predicate over a standing order and there
  is no alert for it — `Alert_NeedMealSource` tests only that a stove exists.
### room-that-is-not-a-room
- when: turn
- applies-when: any layout has been placed with `place-layout`
- read: `digest` → `construction.layouts_unenclosed` (absent when there is
  nothing to report — presence is the signal), or `rooms` →
  `layouts_unenclosed` / `layouts_failing` on any turn you called `rooms`
- flag: the key is present, or `layouts_failing > 0`. Each row carries
  **two flags and they are different failures**: `enclosed: false` is a hole in
  the shell (`Verse/Room.ProperRoom`), and `uses_outdoor_temp: true` is a hole
  in the ROOF on a room that may be perfectly sealed
  (`UsesOutdoorTemperature` = `TouchesMapEdge || OpenRoofCount >=
  CeilToInt(CellCount * 0.25f)`). A freezer dies of either.
- act: `first_gap.at` is the cell. If `standing` is `blueprint` or `frame`, it
  is a build that has not happened — that is `materials-designation-loop` or a
  stalled element, not a design problem. If it is `missing`, something
  destroyed or cancelled the wall and it must be replaced:
  `build {def:"Wall", at:[x,z], stuff:"…"}`. If there is no gap at all and
  `uses_outdoor_temp` is still true, the hole is the roof — the game roofs an
  enclosed player room by itself
  (`AutoBuildRoofAreaSetter.TryGenerateAreaNow`, ≤26 regions, ≤320 cells), so a
  roof that is not going on means nobody is free to build it, not that a
  designation is missing.
- why: run `m1-20260901` placed its freezer as a layout, built every element,
  and never closed it. `construction {layout_id}` read `done: true` for forty
  days while `room-at` on the interior read `outdoors: true, cells: 60082` —
  the whole outdoors. The colony had no larder and starved. **`done` means
  every element resolved; it has never meant the room encloses**, and the two
  come apart at exactly the tick the last wall completes, which is also the
  tick every construction count goes to zero and the layout becomes invisible
  ([[a-room-is-not-a-room-until-the-game-says-so]], git-bug `a1644d6`).
- and: a row that has been there **across a day boundary** carries
  `unenclosed_for.stale: true`. `unenclosed_for.tracked_since` says when this
  process started watching — tracking is in memory and resets at a load, so a
  young `tracked_since` means the age is a floor and NOT that the room is fine.
- retire-when: nothing retires this; it is the read that would have caught the
  wipe.

### construction-stalled
- when: turn
- applies-when: `digest.construction.blueprints + frames > 0`
- read: `digest` → **`construction.stalled`** (a LIST), `construction.stalled_count`,
  `construction.tracked_since_tick`, and `construction.no_builder`.
- flag: `stalled_count > 0`. Each row names `def`, `at`, `state`, `layout_id`,
  `state_age_days` and **`why`** — one sentence naming the cause in the same
  vocabulary `construction`'s items use.
- act: branch on the row's `state`, never on the count:
  `no-builder` → **the skill ceiling**, see `somebody-can-actually-build-it`
  in triggered.md; hauling and unforbidding change nothing ·
  `awaiting-materials` → `things {def, detail:true}` for `forbidden`, then
  `unforbid` / allowed-area / mine, per `materials-designation-loop` ·
  `blocked` → the row's `why` names the obstacle; `designate` it away ·
  `ready` → nobody has taken it: work priorities, reachability or a
  reservation. Then `cancel-layout` / `construction {placement_id}` if the
  element is no longer wanted at all.
- **Absence is not clean here.** `stalled` is empty *and*
  `construction.stalled_note` is present means tracking is younger than the
  two-day threshold and CANNOT answer yet — every reload restarts it, because
  the tracker is in memory by design (`AgentGameComponent` has no
  `ExposeData`; scribing from the observation surface is a separate hazard,
  git-bug d16a463). Per element, `stalled` is a tri-state: `true` · `false` ·
  `null` = *not known yet*. Never read `null` or an absent age as healthy.
- why: `awaiting_materials` sat at **22 for twenty consecutive in-game days**
  on run m1-20260901 — days 38 to 57, uncapped, a true census every time — and
  the agent read it every turn. A scalar tells you the size of a set and never
  its age, and fifteen identical reads and one read are the same envelope.
  Banking yesterday's count in the caller is the failure mode
  [[read-every-return-or-lose-a-colonist]] was written about: a derived fact
  that exists only if the caller remembers to derive it will eventually not be
  derived. The mod holds the tick, so the mod holds the transition. git-bug
  f9dadc7.
- becomes: `advance until:{condition:{path:"construction.stalled_count", eq:0}}`
  once 1.6 can address it.
- retire-when: never. There is no alert for a blueprint that never completes;
  `Alert_ColonistsIdle` fires for the opposite symptom and often not at all.

### hostiles-standing
- when: turn
- read: `digest` → `threats.hostiles_unpardoned` (fall back to
  `threats.hostiles` on a run predating the pardon verb)
- flag: `> 0` → emergency posture (4.2's: verify SeekAndKill engagement,
  intervene by exception); on the transition to `0`, the post-raid trigger in
  `triggered.md` fires.
- why: `hostiles` counts STANDING hostiles (`ThreatSection` skips downed/dead)
  — the correct fight-over predicate. Do not substitute `DangerRating`; the
  two disagree and only `hostiles` answers the question (DESIGN 2026-08-31).
  Known asymmetry: `threats.hostiles` does not fog-filter but
  `pawns {filter:"hostile"}` does, so the two can disagree on a part-fogged
  map — a standing hostile you cannot see is real and unreachable, not a
  contradiction (cc8988c verification, miss 1).
- **A nonzero `hostiles` on a map with a hive is the RESTING state, not an
  emergency.** M1 ran five days at `threats.hostiles` 6–7 — four megascarab, a
  locust and a spelopede, map-generated, dormant, fogged from tick 0, never
  approaching — while `pawns {filter:"hostile"}` read 0 throughout. Reading that
  as an active threat is what made the transition to `0` unreachable, so the
  post-raid trigger could never fire on that map from tick 1. Declare them:
  `threat-pardon {ids:[…], reason:"…"}` is a journalled act with a required
  reason, and `hostiles_unpardoned` is what this item flags on. A pardon is a
  DECISION, not a filter — "we are not ready to fight those" said out loud,
  where the next session can read what was decided and why.
- retire-when: cc8988c's mod procedure owns the transition; this trip-wire
  then only arms the emergency posture.

### repeated-refusal
- when: turn — and unlike everything above it, on **every envelope**, not only
  on the digest. This one is a fact about your own calls.
- read: the envelope's **`repeated`** block. Absent on a healthy call; present
  from the third consecutive identical refusal of the same verb onward, with
  `count`, `code`, `since_tick` and `ticks`.
- flag: the key is present. There is no threshold to apply here — the mod has
  already applied it (`RefusalStreak.Threshold`, 3 calls = 3 turns ≈ 15 in-game
  hours on m1-20260901's cadence).
- act: **escalate, do not retry.** The answer is deterministic in the arguments
  you are sending; a fourth identical call gets a fourth identical refusal.
  Three routes and the envelope tells you which:
  - `code: "bad-args"` — the call is malformed for this target and no amount of
    repetition fixes it. Read `error.detail`; it names the reason
    (`'DeepDrill' is not made from stuff` — drop `stuff`).
  - `code: "unread-journal"` — your `journal` calls are not reaching the tail.
    **Read `unread_after` on the `journal` reply**, not just `ok`: nonzero
    means the next advance is blocked and you already know it. The measured
    cause on m1-20260901 was `journal {since_seq:0, limit:2000}` sent every
    turn — a truncated read only moves the watermark as far as the rows it
    handed over, so it stopped nine rows short of the tail sixty turns running
    while `unread_after: 9` sat in every reply. Page from the LAST
    `read_watermark`, not from 0.
  - `code: "busy"` — an advance is genuinely in flight. This one is `flow` and
    retrying is correct; `count` is telling you the advance is longer than you
    think, not that you are doing something wrong.
- and: **`ticks: 0` beside a `count` in double figures is a WEDGE.** It says
  the colony clock has not moved since the streak began — you are burning wall
  clock and the world is frozen. Escalate to the human gate rather than
  continuing the turn script.
- why: run m1-20260901 sent `build {def:"DeepDrill", at:"122,130",
  stuff:"Steel"}` **238 times** and was told `'DeepDrill' is not made from
  stuff` 238 times. Every refusal was correct. Nothing stopped it, nothing
  escalated, and the repetition existed only in a transcript nobody read until
  the run was over. The same run also spent **60 consecutive advances** — five
  minutes of wall clock, `TicksGame` frozen at 3,704,384 — being refused
  `unread-journal` with a byte-identical detail while calling `journal` in
  between. Neither wedge was visible to the agent that was in it (git-bug
  f08dfc4; `5cb1f9f` §4 argues the same shape for a re-raising dialog, which
  the two-consecutive-0-tick rule cannot see because those advances DO run
  ticks).
- becomes: nothing. This is a fact about the protocol conversation, not a state
  predicate, so 1.6's `until:condition` cannot carry it.
- retire-when: never, while an unattended agent drives the loop. The mod holds
  the count precisely so the caller does not have to remember to derive it —
  the same argument `construction-stalled` makes about ages.

## How far to trust the alert readout

The digest's `alerts.active` is the game's own attention model, and it is
right most of the time — that is why it survives the byte budget uncut. The
exceptions are known, verified, and small enough to memorize:

| alert | trust | because |
|---|---|---|
| `Alert_NeedWarmClothes` | lean on it | genuinely forecasts 3 twelfths ahead (`AverageTemperatureAtTileForTwelfth`); but it only counts apparel in storage — sweep drops first |
| `Alert_MajorOrExtremeBreakRisk` | lean on it | threshold-based ahead of the break; pairs with `mood_arrow` |
| `Alert_NeedDefenses` | never wait for it | self-silences day 6 regardless; one sandbag anywhere silences it even in-window ([[alert-need-defenses-self-silences]]) |
| `Alert_FireInHomeArea` | scoped | home-area only, `ThingDefOf.Fire` only — `fires` verb is the honest read |
| `Alert_LowFood` | late twice | muzzled before tick 150,000; threshold overstates days ~1.6× (see `food-days-floor`) |
| `Alert_NeedMealSource` | building ≠ food | tests only that a stove EXISTS, and is silent before day 2 (`GetReport`) — see `production-still-runs` |
| `Alert_ColonistNeedsTend` | **inverts on the worst case** | its getter EXCLUDES pawns needing rescue, so it goes OFF the moment the patient goes DOWN — silence means tended OR collapsed. M1: it was the only pre-casualty signal, on at 205,979, and it self-silenced 8,620 ticks before the first death ([[read-every-return-or-lose-a-colonist]]) |
| `Alert_NeedDoctor` | structurally too late | fires when NOBODY can doctor, which on a one-doctor colony is the tick that doctor becomes the patient. It cannot warn about a single point of failure, only about its arrival ([[one-doctor-is-zero-doctors]]) |
| armament | no alert exists | all 126 concrete vanilla alerts checked (133 `Alert_*` declarations, 7 abstract); the gap is total ([[weapons-have-no-alert]]) |

**Two suppressions apply to the WHOLE table, not to any row in it**, and no
per-alert reading finds them — they live in
`AlertsReadout.AlertsReadoutUpdate`, which returns early below
`Mathf.Max(TicksGame, Find.TutorialState.endTick) < 600` and clears
`activeAlerts` outright when `Find.Storyteller.def.disableAlerts` is set. So
`alerts.active` is empty by construction for the first 600 ticks of a colony,
and permanently empty under such a storyteller. Read the storyteller once at
colony start; treat an empty list before tick 600 as no information.
([[alert-need-defenses-self-silences]])

**Silence from a scoped or time-limited alert is not safety.** That sentence
is the whole reason `daily.md` exists.

**Every alert on that list now WAKES you (git-bug 280fb78).** An `alert_on`
transition halts an `advance` whether or not it asked, exactly like a letter, on
Evan's rule — "is there something I might act on", not "is this bad". So a
chronic alert the colony has deliberately decided not to fix will flicker off and
on and wake the run each time for a decision already made. `alert-mute
{ids:["Alert_X"], reason:"…"}` is the answer, and it is a JOURNALLED ACT with a
required reason, not a filter: `digest.alerts` still lists every active alert,
and the mute itself shows up as `digest.alerts.muted` with the reason attached,
so day 8 can read what day 2 decided. Two things to hold:

- **Read `digest.alerts.muted` before trusting silence.** A muted alert is a
  standing decision, and one you have forgotten you made is the
  `[[seek-off-is-a-decision-to-flee]]` failure with a different subject. Release
  it — `alert-mute {ids:[…], release:true}` — the moment the reason expires;
  nothing lapses it for you, deliberately (DESIGN, 2026-09-01).
- **Mute the row, not the wake.** `advance {through_news:"<why>"}` rides past
  EVERY letter and alert for one call, which is the right tool for a deliberate
  three-day burn and the wrong one for a noisy alert. The first is a
  journalled admission per call; the second is a decision you can look up.
