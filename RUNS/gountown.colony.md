# Gountown — colony notes

The between-session state that is derivable from nothing else. State and intent
only, never lessons (`PLAY-LOOP.md` §Artifacts). A page, overwritten in place.

**Faction** Enterean · **Settlement** Gountown (both named by Evan at the
`Dialog_NamePlayerFactionAndSettlement` prompt, which the protocol cannot answer
— `5cb1f9f`). Run dir `RUNS/m1-20260901`, sid `20260902T002505`,
`RWA_RUN=m1-20260901`. Tile 35914, TemperateForest, Cassandra Classic / Rough.

## Roster — REWRITTEN after the day 32-34 collapse

**Dead:** Wouter (tick 2,028,803), Serenity (2,043,293). **Kidnapped:** Jimmy, by
faction Buoink at 1,942,678, carried off with the bolt-action rifle — the letter
said an opportunity to rescue him may present itself, and it is worth taking:
Lacey carries *"My fiance Jimmy lost −18"* and *"My friend Jimmy lost −10"*.

**Trev** (46) is the colony. Shooting 11, Melee 12, Cooking 10, Plants 10,
Social 10; traits Lazy, Gourmand, Body purist. Doctor 1, holds the bolt-action
rifle, `attack-then-seek`. Everything runs through him.

**Marco** is bedridden with **paralytic abasia** — and this was investigated
rather than operated on. `surgery-options` offers `InstallPegLeg`, which would
have been useless surgery on a healthy man: the Royalty def says abasia
*"interferes with the motor cortex"*, i.e. it is a BRAIN condition, and that
*"patients recover naturally as the brain rewires itself, but the process is
slow. There is also a chance of a lucky early recovery."* The full cure needs
**glitterworld medicine**, which the colony does not have. **The correct action
is to wait.** When he stands up the colony gains Plants 12 and Shooting 5.

**Lacey** is the fragile one: mood reached 0. Her negatives are almost all grief
(−18/−14/−10/−10) which only time removes; Painism gives her `Blind +15` and
`Intense pain +7` back. Watch her Food need — she hit 20% while downed because
nobody was assigned to feed her.

## Roster

| | Lacey 310 | Wouter 313 | Jimmy 323 |
|---|---|---|---|
| role | crafter, cook, **doctor**, grower | **doctor**, researcher, grower | builder, **miner**, sculptor |
| violence | yes (revolver) | **no — Shooting and Melee both disabled** | yes (bolt-action rifle + full flak) |
| standing injuries | 2 × MissingBodyPart, bite scars | recovered from a wound infection that reached 0.93 | 2 × MissingBodyPart, bite scars |
| watch | Nudist — every garment is a mood cost | mood runs 35–55; Painism costs him −6 *Not blind* and −5 *No Blindfolder*, neither addressable | Misandrist |

Wouter is the whole medical system and cannot fight. Lacey is the second doctor
and must never be given a priority-1 job that outranks Doctor — that mistake
already cost ~8,000 ticks of untended infection.

## Geography

Plaza spine **x113–115**, the stockpile and the conduit run; every door faces it.

| building | interior cell | room | role |
|---|---|---|---|
| Barracks | 108,139 | 38 | Barracks — 3 beds, one owner each |
| Kitchen | 120,139 | 54 | Kitchen — FueledStove, no eating surface on purpose |
| Workshop | 108,129 | 53 | Workshop — sculpting + stonecutter + hand tailoring |
| Laboratory | 120,129 | 52 | Laboratory — SimpleResearchBench |
| Rec hall | 108,119 | 64 | RecRoom — 2 horseshoe pins, chess table, table + stools |
| Power room | 120,119 | 69 | generator at (118,120) |
| Freezer | 120,149 | — | **shell built, ONE cooler outstanding** — 90 steel + 3 components |

**Perimeter wall** `ly-14`, x102–128 z110–156, wood, with **exactly one gap at
(114,110)**. Killbox: barricade firing line `ly-15` along z113 with the firing
step at z114 and wing walls at x105/x123. The field is z111–112 — three cells,
which is all the ground allows without moving the wall further south.

Farms are deliberately **outside** the wall: rice zones 1 (x94–102 z118–131) and
4 (x94–102 z104–116), cotton zone 2 (x94–102 z133–142). Crops are re-sowable;
colonists are not.

**Never touched:** the void monolith at (101,168). The ancient ruin at x100–101
z144–151 is harmless scenery (open to the sky, a stele and a table) and the
three "ancient security turrets" at x86–90 are looted props, not weapons.

## Standing intents

1. **The scanner chain is the spine of the run.** `MicroelectronicsBasics`
   (3000) → build `HiTechResearchBench` (100 steel + 150 stuff + **10
   components**) → `DeepDrilling` (1000) → `GroundPenetratingScanner` (1000).
   Total **350 steel + 16 components**.
2. **Components are the constraint, not steel.** 21 on hand; ~5,120 steel
   minable from 128 `MineableSteel` cells at 40 each, 20 cells east. The
   component answer is **30 cells of compacted machinery at (139,132), 2
   components each** — designated. Spend no components on anything but the
   chain and the freezer's last cooler.
3. **Food is the chronic weakness.** `food_days` has sat between 1.8 and 4.5
   for the whole run against a floor of 6. The field is ample (243 rice cells,
   ~22 nutrition/day of capacity against 4.8 consumed); the bottleneck is
   harvest labour. Growing is priority 1 on Lacey and Wouter.
4. **Fire is the standing existential risk.** 109 fires burned to within 16
   cells of the base on day 8 and only rain stopped them, and the perimeter wall
   is wood. The driver re-issues a `cut` firebreak ring on all four sides every
   day boundary.
5. Warm clothes: parka ×3 and tuque ×3 are queued at the tailoring bench against
   381 cloth. `Alert_NeedWarmClothes` is **muted with a reason** and must be
   released the moment a parka exists.

## Decisions deferred

- **Machining** (Smithing 700 → Machining 1000) for a component supply and steel
  weapons. Deliberately *after* the scanner: it costs 5 components for the table
  and 1,700 RP, and the mined machinery covers the chain without it.
- **Turrets** need `GunTurrets`, which needs `BlowbackOperation` — not started.
  The killbox is two rifles behind barricades until then.
- **Hidden conduits.** `ShortCircuitUtility.GetShortCircuitablePowerConduits`
  reads `ThingsOfDef(PowerConduit)` only, so `HiddenConduit` is *structurally*
  Zzzt-immune. The 60-cell grid `ly-13` is ordinary `PowerConduit` and should be
  deconstructed and rebuilt hidden (2 steel/cell, 120 steel) — **but only once
  the freezer's cooler is in**, and there is no Zzzt exposure until the generator
  actually runs, because the incident needs `PowerNet.HasActivePowerSource`.
- The generator has read `gen_w: 0` throughout: unfuelled. Refuelling is Hauling
  work and Hauling is priority 2.
