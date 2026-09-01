# daily.md — the day-boundary sweep

The polling residue: what is left after the digest's trip-wires (`turn.md`)
took everything free and the triggers (`triggered.md`) took everything with a
moment of its own. What remains is slow, event-less drift — state that decays
without ever emitting anything. These are the only checks with unconditional
recurring cost, so this is the one file 4.4's hard cap binds.

**Cap: 7 items** *(proposed — ratified or changed by 4.4, `d32eadd`)*.
**Current: 5** (`barracks-heat` promoted 2026-09-01 from M1's ledger — under
cap, so no merge-or-retire was owed; `checklists/README.md` §promotion).
Adding past the cap forces a recorded merge-or-retire in the same commit,
named in this file. Run at the first read after
`digest.time.day_of_season` changes **and at the session's first read**,
which is a boundary too (`checklists/README.md`); on a new colony,
`triggered.md`'s colony-start section runs first and an item it already
answered logs `ok` naming that line rather than nothing. Log every item,
every day, to the run ledger.

### freezer-below-zero
- when: daily
- applies-when: a freezer exists (else `n/a`)
- read: `room-at` at the `freezer` landmark → `temp_c`; cross-check no
  perishables sit unroofed (world serializer's roofed flag)
- flag: `temp_c > -1` *(proposed — margin so door traffic cannot cross 0)*
- act: check cooler power (`power-deficit` often explains it), doors standing
  open, or a breakdown; re-plan capacity if the room holds too much warm mass
- why: the game teaches `SpoilageAndFreezers` only inside the branch that
  DESTROYS the rotted food (`CompRottable`, the `rotDestroys` path), at
  GoodToKnow — the lowest tier. `food_days` is a nutrition snapshot blind to
  the spoilage timeline: 10 "days" of food at +5°C is not 10 days of food.
  This is the audit's case 1 — no vanilla leading signal exists, so the
  checklist originates one. (96d9315 comments 1 and 6.)
- retire-when: sampling (2d9a1da) carries freezer temperature and a slope, or
  1.6 watches `temp_c` as a condition.

### armed-roster
- when: daily (and re-fired by `roster-change` / `raid-end` triggers)
- read: `pawn {id:<n>, sections:["equipment"]}` per colonist — `pawn` is
  single-pawn and `id` is required, so this is N round-trips off a `pawns`
  roster, not one call (the `primary` flag);
  spares via `things {category:"weapons"}` — equipped and spare are disjoint
  populations, and a spare only counts if reachable and unforbidden
- flag: armed colonists < violence-capable colonists *(floor: one usable
  weapon each before day 5 — proposed)*
- act: `equip` / `assign {auto_arm:true}` from spares first — **guns you
  already own raise no wealth**; craft or buy only after the spares are worn,
  because every new weapon prices the next raid
  ([[wealth-buys-bigger-raids]])
- why: the confirmed total gap — across all 126 concrete vanilla alert classes
  (133 `Alert_*` declarations, 7 abstract), none
  covers armament; the game treats pre-combat equipping as Critical on every
  raid and never says a word in advance. Evan: raids are the thing that
  "will knock you out". [[weapons-have-no-alert]]
- retire-when: `weapons_per_colonist` ships in the sample row (2d9a1da names
  it a new derivation) — this item then collapses to a free trip-wire in
  turn.md and the daily slot is reclaimed.

### production-still-runs
- when: daily
- applies-when: any production bench exists
- read: `bills` — a meal bill exists, unsuspended; `next_ingredient_search_tick`
  moving proves something is actually working it (a bill can read `active`
  and be worked by nobody — that field is the only observable proof either
  way, DESIGN 2026-08-31)
- flag: no live meal bill; a bill starved of ingredients; a bill whose search
  tick never advances
- act: starved input → `materials-designation-loop` (designate, don't retry
  the bill); nobody working it → `bill-who-will-do-it`'s roster read; no bill
  at all → `blocked` on 3.6 (`48f666c`) until bill authoring ships
- why: `Alert_NeedMealSource` checks only that a `isMealSource` BUILDING
  exists and is silent before day 2 — a stove is not food being made. The
  read half ships today; only authoring waits on 3.6 (96d9315 verification:
  "the checklist item can be authored now").
- retire-when: 3.6's bill verbs report worker eligibility and ingredient
  reachability at creation, moving this to an act-keyed check.

### apparel-margin
- when: daily, every 3 days (decay is slow; a daily read buys nothing)
- read: `pawn {id:<n>, sections:["apparel"]}` per colonist (one call each)
  → worst `hp_pct` among
  worn items that use hit points
- flag: any worn item below 60% *(proposed — margin above the penalty line)*
- act: queue replacements (tailoring waits on 3.6 — log `blocked`), reassign
  from stockpiled spares, or strip a corpse the colony can stomach
- why: the mood penalty lands at fixed levels — worst worn item < 50% is
  "frayed", < 20% "tattered" (`ThoughtWorker_ApparelDamaged`,
  `MinForFrayed`/`MinForTattered`, verified this session) — and the alert is
  just that thought made visible (`Alert_TatteredApparel : Alert_Thought`),
  i.e. it fires when the penalty has already landed. Reading the level with a
  10-point margin is a leading indicator with no trend required. The SEASONAL
  half of clothing needs no item at all: `Alert_NeedWarmClothes` genuinely
  forecasts (turn.md's trust table).
- retire-when: sampling carries worst-apparel-HP, or an outfit policy plus
  tailoring bill makes replacement automatic (template/policy rung).

### barracks-heat
- when: daily
- applies-when: an indoor room with a heat source and occupants exists (else
  `n/a`)
- read: `rooms` → `temp_c` per row where `indoors` is true and
  `uses_outdoor_temp` is false (one call for every room; `Room.Temperature`
  is a plain field read of `RoomTempTracker.temperatureInt`, and
  `uses_outdoor_temp` is what says when the figure means nothing)
- flag: `temp_c > 26` in a room colonists sleep or work in *(proposed —
  a human's `ComfyTemperatureMax`, where the mood penalty lands)*; `> 36`
  is the health line, not the warning
- act: cheapest first — deconstruct or `flick` off a heat source (a torch
  lamp is the usual culprit), or open the room. Coolers are wealth
  ([[wealth-buys-bigger-raids]]) and the build verb waits on 3.3 anyway
- why: vanilla's signal here IS the harm. `Toils_LayDown` grants
  `SleptInHeat` the moment `AmbientTemperature > ComfyTemperatureMax`;
  heatstroke accrues only above `SafeTemperatureRange().max`, which is
  comfy max **+ 10** (`Verse/GenTemperature.SafeTemperatureRange`,
  `HediffGiver_Heat.OnIntervalPassed`); and `Alert_Heatstroke` fires only
  once a pawn already carries a visible Heatstroke hediff
  (`GetFirstHediffOfDef(HediffDefOf.Heatstroke, mustBeVisible: true)`) —
  the alert is the injury made visible, `Alert_TatteredApparel`'s pattern.
  Those 10°C are the entire warning and nothing publishes them. Observed:
  M1 `m1-20260831` day 3, room 57 at 28.2°C against 8°C outdoors — campfire
  plus two torch lamps sealed in a 49-cell barracks, Captain carrying
  SleptInHeat −4.
- retire-when: sampling (2d9a1da) carries per-room temperature, or 1.6
  watches `temp_c` as a condition — the same exit as `freezer-below-zero`,
  which reads the same field from the other end.

## Blocked on sampling — do not fake these

Every item below needs a TREND, and the harness records no rates — the journal
is events-only, and nothing samples (git-bug `2d9a1da`, filed as this spec's
pair). Writing them as single-point reads would dress a level up as a slope;
levels that ARE honest already live in turn.md. These unlock, verbatim, when
sampling lands:

- **food-slope** — `food_days` falling N days running, projected zero-day
  before the next harvest. The floor trip-wire cannot see "falling".
- **wealth-vs-readiness** — the damping loop: raid points scale with colony
  wealth (`StorytellerUtility.DefaultThreatPointsNow` evaluates
  `PointsPerWealthCurve`), so "we died, build more guns" raises the threat it
  answers. The game already records BOTH series (`HistoryAutoRecorder`:
  wealth and threat points, Scribe-persisted, never yet read) — the item is a
  ratio over two free columns, but reading them out is 2d9a1da's work.
  [[wealth-buys-bigger-raids]]
- **mood-drift** — colony average sliding over days while every single read
  stays above the break-risk alert's floor. The alert and `mood_arrow` cover
  the acute case; only a series shows the slow one.
