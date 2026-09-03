# openrun-20260902 — FINAL: the colony is dead

**Ended:** Spring 8, year 5503, tick **10,569,958**. Final save
`FINAL-andbourne-wiped-spring-5503`. **Colonists: 0.**

Andbourne was wiped by a single mechanoid pod raid — two scythers and one lancer
— on Spring 5. Deaths in order: **Anon, Gauss, Sinn, John, Ludo, Niklas**, the
Empire guest **Kiozeas**, the man-in-black **Ignat**, the crash-landed refugee
**Raccoon** (never reached), and finally **Dilly**, of malnutrition, immobile
with nobody left alive to feed him.

Everything below the line is the record of the run that got there. The full
causal chain is `checklist.ndjson`, entries `THE-WIPE-how-Andbourne-ended`,
`WHAT-THE-DEFENCE-GOT-WRONG-STRUCTURALLY` and
`AGENT-ERROR-I-paused-and-handed-the-decision-back`. The three that matter most:

1. **A perimeter that only faces outward is half a defence.** Six mini-turrets
   and two autocannons behind the funnel ring beat a scyther, a lancer+militor
   pair, eight shamblers, a cultist raid and twelve manhunter vultures without a
   single death. The lancer that killed the colony spent ~200,000 ticks *inside*
   the walls unopposed, because every gun points at a gate.
2. **Re-count every turret def by name after a raid.** The vultures destroyed
   two mini-turrets *and* the autocannon at [98,114]. I rebuilt the mini-turrets,
   read `Turret_MiniTurret: 6`, and called the north approach restored. It had no
   autocannon and I never asked.
3. **Do not feed colonists into a fight one at a time, and never gamble the best
   one for a single kill.** I pressed Gauss at 64% in melee against two scythers
   for "one more burst"; he dropped at 16%. With no adults standing I then sent a
   ten-year-old to carry a bleeding man out of the open. Both were my calls and
   both were wrong.

---

**Ended:** Fall, year 5502 · saves `fall-d7-before-assault`, `fall-d6-mech-cluster-accepted`,
`summer-d17-mech-raid`, `summer-d8-shamblers`, `spring-d11-mech-raid-incoming`
**Journal:** page with `rwa send journal --since_seq <n>`, never a bare read — a
limit-only read restarts at seq 1, truncates, and moves the watermark **not at all**.
**Ledger:** `checklist.ndjson`, 149 lines.

## Roster — 5 alive, 0 lost this session

| pawn | weapon | armour | role |
|---|---|---|---|
| **Gauss** 45740 | LMG (good) | marine armour + helmet | Shooting **20**, Bloodlust. The gun line. Doctor permanently disabled. |
| **Niklas** 45747 | chain shotgun | marine armour + helmet + smokepop | Crafting **17** — the armourer. Wimp + Delicate: keep him behind. |
| **Anon** 31054 | assault rifle | flak vest/pants/helmet | Shooting 10, Crafting 7, Construction 8 |
| **Ludo** 41346 | bolt-action | flak vest/pants/helmet | Shooting 6. Depressive: sits near the break line permanently. |
| **John** 18294 | plasteel gladius | flak vest + parka + flak helmet | **Brawler**, Melee 16 — a rifle in his hands is a downgrade. Only real doctor (Medicine 16). |

Gauss and Niklas arrived mid-session with the usual joiner defects — Firefighter at 3,
a dozen work types at 0, Niklas with **Doctor at 0** and no food policy. All repaired.
Doctor coverage is now 3 (John, Anon, Niklas).

## What got built

- **Six mini-turrets + one autocannon** at [98,114]; a second autocannon is framed at [85,75].
- **The funnel ring is closed** — north z=120 (gap x97-99), west x=74, east x=132,
  south z=70 (gap x86-88), with jogs around a soft-sand patch. Two openings, each
  covered by a turret cluster. `PLAN-funnel-and-expansion.md` has the coordinates
  and the honest caveat: gaps are the *cheapest* path, not a forced one.
- **Freezer rebuilt** — both coolers destroyed, re-blueprinted at [96,107] and
  [99,107] rot NORTH, holding −5 °C. *Currently switched OFF to save 400 W; turn
  them back on when the outdoor temperature climbs above freezing.*
- **Comms console + two orbital trade beacons.** One trade executed.
- Hi-tech research bench. Research done: FlakArmor, SmokepopBelt, GasOperation,
  **MicroelectronicsBasics**, PrecisionRifling, **HeavyTurrets**. Now on Mortars (2000).

## Do these first

1. **FOOD is solved — Dorian sent 411 survival meals** (369.9 nutrition map-wide,
   ~48 days for five). Do NOT plan a crop around a low `food_days`: that field is
   STOCKPILE-ONLY, and `food_rot` is evaluated on a predicate cadence so it reads
   stale for a few thousand ticks after a spawn. Advance a little, then re-read.
   And check the season — I nearly sowed 550 cells of rice on **Fall 14**, which
   winter would have killed before harvest. Dorian caught both.
2. **Steel 0, components 3.** Everything queued stalls on these. Gauss is mining.
   Dorian's standing instruction: *if you can't get a resource, ask him* — don't grind
   a 62-cell haul.
3. **Power gain is thin (+184 W)** with almost no battery buffer. Two wind turbines are
   selectable and cost no fuel; they need steel.
4. **A dormant mech cluster sits at (166–171, 10–15)** — 2 militors, pikeman, scorcher,
   scyther, psychic suppressor, 3 mech nodes, capsule, birther, 2 mini-slugger turrets.
   Quest accepted for **Empire goodwill** (goodwill is the gate on trade caravans). It
   stays dormant until disturbed, so the assault happens on our terms.
5. **`seek-at-will` is unusable until seekandkill git-bug `a6b1aa0` is fixed** — it NREs
   every tick against that dormant cluster and freezes every advance.

## Rules this session paid for

- **`posture {area:…}` writes all three levers, and `seek` defaults to TRUE.** Binding
  colonists to an area silently ordered two unarmoured men to hunt a scyther. Always
  pass `area`, `seek` and `hostility` explicitly, every call.
- **`flick` is a TOGGLE, not a setter.** Calling it twice re-designates then
  un-designates and both replies read `changed: 5`. Call once; verify with `temp-control`.
- **Flick lives under BasicWorker**, which was priority 3 while construction was 1 — so
  five switched-off heaters stayed off through a −23 °C cold snap and John reached
  *extreme* hypothermia. Firefighter, Patient and BasicWorker stay at 1 for everybody.
- **Plate armour occupies BOTH Middle and Shell** (`layers` says so), so it displaces the
  parka. An armour upgrade can be a clothing downgrade.
- **`wear`/`equip` issue direct orders with `queue:false` — a second one CANCELS the
  first.** Two flak vests sat unworn for a day because I sent helmet orders straight
  after the vest orders. One order, advance, verify.
- **`auto_arm` re-picks weapons and scores a poor autopistol over a good bolt-action.**
  Turn it off once the militia is kitted, then assign by hand and verify per pawn.
- **`resources.*` is stockpile-scoped and it cost this run four times** — meds 0 vs 48
  glitterworld medicine on the floor, steel 1 vs 188 at the mine and 1 vs 1538 in the
  base. `construction.missing[].available` uses the honest basis
  (`reachable-unforbidden-by-a-colonist`); `things {def}` is the other check.
- **`zones` and `things` pages are capped and do not look capped.** The previous handoff
  said sowing was disabled colony-wide; `zones {cap:100}` showed 56 of 76 zones sowing.
- **When a placement refusal is about TERRAIN, dump the terrain plane.**
  `map-dump {layers:['terrain']}` drew the soft-sand patch in one call after ten
  single-cell probes had produced only confusion.
- **A blueprint you did not place is evidence about the map.** I cancelled the game's
  own rebuild blueprint for a destroyed cooler because both sides read "outdoors" —
  which was true *because* the wall was already holed.

## Blocked on things this map cannot make

- **Mortars: 2 reinforced barrels, `available: 0`.** `construction.missing[].hint`
  says it plainly — *"genuinely short: nothing else of this def is on the map within
  reach."* Not craftable in vanilla; traders or quest rewards only, and every trade
  route is goodwill-locked at 0. Both mortar blueprints are LEFT STANDING at [96,111]
  and [104,111] so they build the instant two barrels exist. Mortars are the textbook
  answer to the dormant cluster, whose mini-slugger turrets outrange our mini-turrets.
- **Trade needs faction goodwill.** Voidborn Syndicate's orbital trader is gated on
  "must be ally"; Teuay Nation's caravan on "must be close friends". We sit at 0 with
  everyone. The Empire quest accepted this session pays goodwill, which is why that
  reward was taken over 1,869 silver.
- **Off-map is out of scope, and most quests are off-map.** Three population/reward
  offers this session were all caravan quests and all declined for that reason:
  Chabreitraca (37 slave collars), Miñoca's Cache, and Hill's Salvation (a free
  colonist at a world object). On this map only the offers that come TO us can pay out.

## Research path in flight

DeepDrilling done; **Ground-penetrating scanner (1000) set** — that is contract goal
G1 (deep mineral scanner: researched, built, powered) *and* the permanent fix for
steel, since DeepDrill is already selectable and only needs a scanned deposit.
Watch the auto-picker: every time a project completes the game grabs a junk one
(Tree sowing, Cocoa, Carpet-making, Royal apparel, MultiAnalyzer all stole John's
hours this session). Re-set research the moment `finished_count` moves.
