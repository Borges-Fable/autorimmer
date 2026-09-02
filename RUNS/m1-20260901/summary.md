# m1-20260901 — run summary

**Status: IN PROGRESS.** Written incrementally so the record survives a session
boundary. The verdict line at the bottom is filled in at day 20 or at wipe.

## Fixture, as dealt

`_RimWorld-Agent --quicktest`, session `20260902T002505`, launched 2026-09-01
21:25. `Root_Play.SetupForQuickTestPlay` read rather than assumed: it sets
`ScenarioDefOf.Crashlanded`, `Storyteller(Cassandra, Rough)`, `mapSize 250`,
`ChooseRandomStartingTile`.

| | |
|---|---|
| **rerolls** | **0** — Evan's "deal with the cards" ruling. The tile dealt was TemperateForest anyway, which is what #8's reroll clause was reaching for |
| biome / tile | TemperateForest, 35914, Flat, rainfall 1307, avg 10.1 °C |
| start | Spring day 6, 5500, hour 6, tick 1023, outdoor 3 °C |
| colonists | Lacey 310, Wouter 313, Jimmy 323 |
| violence-capable | **2 of 3** — Wouter has Shooting *and* Melee disabled, so T7's denominator is 2 |
| map | ancient ruins (scenery — `AncientSecurityTurret` is a looted 1×1 prop, Evan's correction, verified in `Buildings_Ancient_Indoors.xml`) and an Anomaly **void monolith** at (101,168), observed only |

## Staging

**There was none.** Comment #9 withdrew the dev-placed base room and
`dev:starter-kit`; the colony built its own shelter and gathered its own
materials from tick 1023 on.

| G2 evidence | |
|---|---|
| `dev` rows in the journal | **0** |
| staging watermark | **seq 0** |
| `dev:set-skill` uses | **0** — `work-cover {work:"Doctor"}` reported `still_under: []` and `coverage_after.ok: true`, so the sanctioned exception was never reached |
| `place-layout --mode instant` | **never used.** Every placement was `--mode blueprint` (the default). Stated explicitly because instant mode journals as an `action`, not a `dev` row, so G2 cannot catch it — this line is the discipline, not the gate |

## Human input beyond watching — three interventions, all recorded

`664e9b9`'s invariants say there should be none. There were three, all Evan's,
all forced by defects now filed:

1. **Construction skill raised on Lacey (4 → 8).** The `Heater` in the barracks
   layout has `constructionSkillPrerequisite 5` against a roster best of 4, and
   the preflight does not look at that field — it reported the element as
   `awaiting-materials, missing 1 ComponentIndustrial` while 30 unforbidden
   components sat reachable. Filed **`e08c3e5`** (p1).
2. **The faction/settlement naming dialog answered by hand.** `dialog-dismiss`
   closes a text-entry window without answering it, so the prompt re-raised
   every 1,000 ticks and wedged the run. Filed **`5cb1f9f`** (p0) plus a
   colony-start comment on `d2e1229`.
3. **Research-completion popups disabled in the bench settings**, to stop them
   force-pausing every advance.

## Spec gaps filed from play

| id | pri | what |
|---|---|---|
| `253c694` | p1 | forced orders collide silently (4 of Jimmy's 5 equip/wear orders replaced each other, all `ok:true`), and order completion is only pollable |
| `855117a` | p2 | mine designations cannot be aimed — `map-view`'s `%` glyph collapses ore into rock, and `mine-vein` is unreachable from `turn.md`'s checklist line |
| `e08c3e5` | p1 | build preflight ignores `constructionSkillPrerequisite`, and reports the skill gate as a material shortfall |
| `daa269a` | p1 | `room`/`rooms` report `owners_total: 0` while `beds[].owners` in the same envelope names all three owners — **T6 is graded on that field** |
| `5cb1f9f` | p0 | `dialog-dismiss` cannot answer a text-entry dialog; the naming prompt wedges the run |
| comment on `d2e1229` | — | colony start has an unwritten 13th step: the colony has to be named, and it falls due days after the section is logged complete |
| comment on `664e9b9` ×2 | — | the amendment itself, and the correction that the scenario ships 300 wood / 1,170 steel and five finished `ClassicStart` research projects |

## Compliance failures of this run, owned

- **`digests/day-N.json` is missing for days 2–9.** The day-boundary snapshot
  and the `daily.md` sweep were not run at those boundaries; the loop was being
  driven by letter-halts rather than day boundaries and the coupling
  `checklists/README.md` states — a snapshot with no full set of daily lines
  behind it, and vice versa — was broken in the second direction. Day 1 and
  days 10 onward are present. `accept/4.2-play-loop.py`'s keyed check will fail
  on the gap and should.
- **Two advances were issued after a `casualty` halt named Wouter, before the
  halt was read.** That is precisely the M1 failure the halt exists to prevent
  (`read-every-return-or-lose-a-colonist`). He survived; the margin was
  104,962 ticks, not judgement.
- **The only available doctor was assigned Mining at priority 1** while a
  colonist lay bleeding with a wound infection, so nobody tended him until the
  priority was put back. Self-inflicted, and the mirror image of M1's C1.

---

# The M1 tables, at the 20-day mark

The contract's clock ran out at **tick 1,201,023** (start 1,023 + 1,200,000).
Reads below were taken at **tick 1,200,000**, Summer day 11 of year 5500, with
all three colonists alive. The run then continued under Evan's superseding goal
(survive indefinitely, unlock the full research tree, deep scanner before the
steel runs out), so this is a checkpoint rather than an ending.

## Gate — all five, or FAILED

| # | criterion | the read that decided it | verdict |
|---|---|---|---|
| G1 | nobody lost | `history` `FreeColonists` **3 → 3**, flat across all 41 samples. Journal `death` rows: Bluebird, Hare, Rat, Squirrel — every one `player:false, kind:"animal"` | **PASS** |
| G2 | no god-hand after staging | **0 `dev` rows** in the whole journal. Staging watermark **seq 0** — there was no staging. `place-layout --mode instant` **never used**; every one of 13 layouts went in as blueprints | **PASS** |
| G3 | red errors | **0 `red_error` rows.** 13 `warning` rows, all of them the mod's own `ignored_args` reports on my wrong argument names, plus 8 mod-load warnings from other mods at tick 0 (Guests, SimpleSidearms, a VFE gas tank) — none ours, none errors | **PASS** |
| G4 | discipline | 0 drafted at end; `threats.hostiles_unpardoned` 0. One `draft` in the run (Jimmy, to intercept the mad rat) and it has its `undraft`. **Zero escapes used** — no `unread_ok`, no `through_casualties`, no `through_news`. Two `alert-mute`s, each with a written reason: `Alert_NeedWarmClothes` (the fix was a cotton→tailoring supply chain, not an act) and `Alert_RolesEmpty` (Ideology is an explicit non-goal) | **PASS** |
| G5 | a raid was met | **not exercised.** No `RaidEnemy` letter arrived in 1.2M ticks. `history` `ThreatPoints` ran **3.5 → 4.18** (stored ÷10, so 35 → 42 real points) — the storyteller never had the points to spend. The only `ThreatBig` letter was *Ancient danger*, which fired because Wouter fled past a ruin, and I am **not** counting it: G5's read is `letter` with `def:"ThreatBig"` and that letter satisfies the read without a raid happening. Filed as a hole in the criterion | **not exercised** (does not fail the run) |

## Thrive — all seven is THRIVED, otherwise SURVIVED

| # | criterion | the read that decided it | verdict |
|---|---|---|---|
| T1 | a real food cycle | (a) zone 1 `plant_configured:true` reached `harvestable` **97** at 99% growth ✓ (b) **`RawRice` 178** in `things`, and no `trade-confirm` row exists anywhere in the run — there were no trades at all ✓ (c) `MealSimple` rose **0 → 8** with `CookMealSimpleBulk` live and unsuspended ✓ (d) **`food_days` 4.5, needed ≥ 6** ✗ | **FAIL on (d)** |
| T2 | wealth grew | `Wealth_Total` **13,843.7 → 18,584.2** (slope positive); `Wealth_Buildings` **2,168.6 → 6,973.1**, last ≫ first | **PASS** |
| T3 | mood held | `ColonistMood` last four samples **[68.12, 62.53, 62.56, 62.60], mean 63.95** ≥ 50. `mental_break` rows in the entire run: a **Rat** (day 3) and a **Buck** (day 19), both `player:null` — no player-faction break at all, let alone in days 16–20 | **PASS** |
| T4 | workshop | room **53** at interior cell (108,129): `role:"Workshop"`, `proper:true`, `open_roof_cells:0`, 35 cells. Three Workshop-role benches inside (TableSculpting, TableStonecutter, HandTailoringBench = 27×3 = 81). Live bill: `Make_StoneBlocksAny`, unsuspended. Product from 0: **BlocksSandstone 230, BlocksMarble 100** | **PASS** |
| T5 | rec room | room **64** at interior cell (108,119): `role:"RecRoom"`, `proper:true`, `open_roof_cells:0`, impressiveness **44.35** | **PASS** |
| T6 | barracks | room **38** at interior cell (108,139): `role:"Barracks"`, `proper:true`, `open_roof_cells:0`. Three beds, `beds[].owners` = **Wouter, Lacey, Jimmy** — one each. **Graded off `beds[].owners`, not `owners_total`**, which reads 0 in the same envelope and is filed as `daa269a` | **PASS** |
| T7 | militia | Both violence-capable colonists hold a `primary`: Jimmy `Gun_BoltActionRifle` + full flak (vest, helmet, pants), Lacey `Gun_Revolver`. But `posture.ok` is **false in the day-1 snapshot** — `seek:"auto"` correctly declined to switch seek on while both were unarmed ("unarmed and Melee 2 < 6 — seeking would make this pawn a casualty") — and days 2–9 have no snapshot at all, so "at every daily snapshot" is **unprovable from this run's evidence** | **FAIL on evidence** |

# VERDICT: **SURVIVED**

Gate rows G1–G4 hold, G5 was never exercised, and **five of seven** thrive rows
pass. T1 fails on one clause of four — the day-20 food buffer — and T7 fails on
evidence I did not collect rather than on a posture that was wrong.

Both failures are mine and neither is bad luck:

- **T1(d)** — the freezer was designed, authored and placed, and its shell stood
  30 of 32 built at day 17. The two missing elements are its `Cooler`s, 180
  steel, and steel sat at 0 in stockpile for the last five days while 811 steel
  lay on the map outside the allowed area. Food was made, eaten, and never
  banked. A colony that eats what it grows the day it grows it reads 4.5 forever.
- **T7** — the posture was right from day 2 onward and I have the reads to prove
  it only from day 10, because I drove the loop off letter-halts instead of day
  boundaries for the first nine days. The criterion says *every* daily snapshot;
  I cannot show eight of them.
