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
  the ground after a raid count zero until hauled ([[unforbid-before-expecting-pickup]]).
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
- act: `designate {type:"chop", rect:[x,z,w,h]}` /
  `designate {type:"mine", rect:[x,z,w,h]}` over the next batch — one rect,
  one call. (`type` is an ARG: `DesignationVerbs.Designate` reads
  `a.StrReq("type")`, and `rwa designate chop …` dies on the bare word.)
  Wood and stone only flow while a designation exists; colonists never fell
  or mine on their own initiative ([[materials-are-a-standing-loop]], Evan:
  "colonists don't put down trees themselves"). Crops are the exception and
  belong to `food-days-floor`, not here.
- why: a bill stalling at zero input pages THIS item, not a bill retry — the
  retry hides the real correction. Stockpiles-only caveat applies here too.
- retire-when: 1.6 predicate, or a mod-side standing-designation policy
  (candidate for the ladder's mod rung once the policy is Evan-ratified).

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
