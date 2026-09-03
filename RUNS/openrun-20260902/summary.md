# openrun-20260902 — session summary (takeover session)

**Ended:** tick **2,846,481**, day **14 of Winter, year 5500**, outdoor −4 °C.
**Journal watermark:** `last_seq 2009` — page from here, not from 0.
**Save:** `spring-d1-survived-winter` (also `spring-eve-all-downed` = the near-loss).
**Colony notes:** this file; ledger is `checklist.ndjson` (appended throughout).

Started at tick 1,594,005 (day 8 Fall). **1.25 million ticks / ~21 in-game days.**

## Roster — 5 alive, 0 deaths this session

| pawn | id | role | notes |
|---|---|---|---|
| **John** | 18294 | everything | ALL skills 19–20. Brawler/Ascetic/Fast walker/Industrious. Holds a revolver (Brawler mood penalty). The colony's engine. |
| **Anarchist** | 19848 | hunter/medic | Man-in-black rescue spawn. Shooting 11, Social 10, Melee 9, Medicine 3. Good revolver. |
| **Ellis** | 1022 | builder | **Construction 10**, Animals 9, Plants 7. 4 permanent frostbites. |
| **Walton** | 1014 | miner/hauler | Mining 5, Artistic 7. **Violence-disabled.** 6 permanent frostbites. Lover of Maxim. |
| **Maxim** | 16188 | NONE | Quest lodger, `Hospitality_Joiners` quest 7 still **Ongoing**. 2 of 26 work types. Arm infection. Pure upkeep — but killing him arms the revenge clause that killed Fitz. |

Bonnie left (CreepJoiner departure, not preventable) and took the bolt-action rifle.
Shiro (pod-crash refugee) died before rescue and was butchered and eaten.

## State

- **food_days 1.2** (3.0 map-wide). 175 deer meat; **6 deer alive ~18 cells out at (82,99)** — hunt them first.
- **steel 1,875** · components **4** · silver 800 (unspendable, no caravans) · **meds 0**
- **Power redundant:** 2 solar, 2 batteries, 3 heaters, + 2 campfires as non-electric backup (proved essential during an eclipse).
- Rooms: lab + 2 bedrooms heated 18–21 °C. One cold room (108,95) free for hydroponics.
- **Research: Machining 73/1000.** Smithing DONE. 11 projects finished. Path: Machining → FlakArmor (1,200) for G3.
- ly-7 (south band) still ~92 open shell cells; ~290 blueprints stalled all winter.

## THE FIRST THING TO DO

**Sowing is disabled on all 7 growing zones.** I turned it off when winter froze the crops.
Spring is 1 day away. **Re-enable `allow_sow` on the 490-cell rice field the moment
Spring begins** — that is the colony's whole food economy and nothing else replaces it.

## Hard-won facts (don't re-derive)

- **Every joiner arrives with work types at 0.** John 13, Anarchist 9 — including Cooking
  and Doctor. Check `pawn {sections:["work"]}` before anything else. Universal, not occasional.
- **Equal priority is broken by the work tab's natural ORDER.** Research is dead last, so any
  other priority-1 job starves it. Mining sits below Cooking/Construction/Growing. This cost
  three separate stalls (Bonnie's research, Walton's mining, Walton's research).
- **Conduits connect CARDINALLY ONLY** — no diagonals. A diagonal "link" left the paste
  dispenser unpowered for hours. Use `HiddenConduit` (2 steel), not PowerConduit — a Zzzt
  started 4 fires on the exposed run.
- **Pair every `build` with an immediate `prioritize`.** Blueprints sit forever otherwise.
- **`beat-fire` is for burning PAWNS**; ground fires need `extinguish --at [x,z]`.
- **`prioritize` work-giver names:** `HunterHunt` (not Hunt), `DoBillsButcherFlesh`,
  `ConstructDeliverResourcesToBlueprints`, `Deconstruct`, `Mine`, `HaulGeneral`.
  Standing the pawn next to the target makes the scanner pick it.
- **Cold preserves corpses.** `rot_stage: Fresh` on a man 8 days dead at −12 °C. Read
  `ingredient_match.rejected_sample` per corpse — the bill's blanket remedy
  ("NO BILL LEVER FIXES THIS") was true for dessicated animals and FALSE for him.
- **Manhunter check before every hunt:** deer/dromedary/iguana safe (no field);
  **emu, ostrich, THRUMBO = 1.00**. Thrumbo also has baseHealthScale 8.0 and MoveSpeed 5.5.
- **Meat yields (measured):** goat 27, deer ~31–43. My 105/goat estimate was 4× too high.
- **Cotton/flak is dead until spring** — flak costs the literal def `Cloth`; the crop froze
  below `harvestMinGrowth` 0.40 and yielded nothing. **PlateArmor is the alternative**
  (Smithing+PlateArmor 600, 170 stuff, ZERO components) but needs **Crafting 7** — John has 20.
- **Off-map is unreachable by design** (`TradeVerbs.cs:98`: caravans/settlements are v1
  non-goals). 800 silver cannot be spent; a comms console needs Microelectronics 3,000.

## Known surface gaps hit this session (reported in chat, filed by others)

- `HediffCap = 20` is hard-coded (`PawnSerializer.cs:80`) with no override — John has 40
  hediffs, so all 20 visible rows were MissingBodyPart and **her bionics were unreadable**.
- No `abilities` section and no gizmo access (`9717e52`) — psylink unreadable/unusable,
  bed assignment and auto-refuel toggles unreachable.
- **No halt when power dies.** A battery hitting zero produces no alert; the run only learns
  via a downstream `Alert_Hypothermia` hours later. `battery_days` is null when gen ≥ draw,
  i.e. exactly the daytime reading, so night failure is invisible until it has happened.
- `pawn-fixture` with NO args mutates (git-bug `e1a9542`); dev-suite toggle is `29824e4`.

## The lesson that cost the most

The colony went to **zero food with all four pawns downed** (Moving 0, Eating 0) while 8 deer
grazed 18 cells away. It survived only because the game's man-in-black fired and Evan healed
John. Root cause: I spent six days hunting a food supply that did not exist on an emptied map
instead of cutting the population while the survivors were still strong enough to hunt.
Evan asked "who is the least skilled?" on day 5 and I argued to wait. Spring's deer arrived
~12 hours after the last capable pawn collapsed.
