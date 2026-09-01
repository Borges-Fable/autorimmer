---
name: materials-are-a-standing-loop
trigger: any material count trending toward zero; any bill stalled for want of ingredients
severity: Important
confidence: evan-stated
source: Evan, session 5 — "colonists don't put down trees themselves"; scope corrected against WorkGiver_Grower (session 10)
---

**What.** Wood, stone and wild plants only flow while a DESIGNATION exists —
supply is a standing loop you keep feeding. **Crops are the exception: a
growing zone works itself.** For crops the standing act is creating and sizing
the ZONE, not designating each harvest.

**Why.** Colonists do not fell trees or mine on their own initiative, and a
bill's ingredient-finder hauls what has **already been harvested** and nothing
more. So a colony with a full forest and no chop designations has, as far as
every bill is concerned, no wood at all. `WorkGiver_PlantsCut` iterates
`designationManager.designationsByDef[DesignationDefOf.CutPlant]` and
`[HarvestPlant]` and skips out entirely when neither exists;
`WorkGiver_Miner.ShouldSkip` does the same for `Mine`/`MineVein`.

**And this lesson previously said "harvest" alongside them, which is wrong.**
`WorkGiver_GrowerHarvest` and `WorkGiver_GrowerSow` both extend
`WorkGiver_Grower`, whose `PotentialWorkCellsGlobal` enumerates every
`Zone_Growing` in `zoneManager.AllZones` and yields its cells with **no
designation check anywhere in the path**. `GrowerHarvest.HasJobOnCell` gates on
`plant.HarvestableNow`, `LifeStage == Mature`, `CanYieldNow`,
`def.plant.autoHarvestable` and the zone's own `allowCut` — and nothing else.
`GrowerSow` gates on `allowSow` and a resolved `wantedPlantDef`. The mod's own
`DesignationVerbs.Table()` has no `sow` type at all, which is the same fact
seen from our side of the bridge.

So the failure mode for crops is not "nobody was told to harvest". It is an
undersized zone, `allowSow`/`allowCut` off, the wrong plant
([[growing-zone-default-is-potato]]), or nobody with Grow enabled
([[who-will-actually-do-it]]).

**How to apply.** When WOOD or STONE drops below the checklist threshold,
designate the next batch: `designate {type:"chop", rect:[x,z,w,h]}` or
`designate {type:"mine", rect:[x,z,w,h]}` — one call for N targets. Wild food
plants are the same shape (`type:"harvest"`). For raw CROP food, do not reach
for `designate` at all: check the growing zone's size, its plant, and its
`allowSow`/`allowCut` flags with `zones`, and grow the zone if the answer is
"not enough land". A bill that stalls with its material at zero should page
the checklist, **not retry the bill**: retrying a bill whose input does not
exist is the wrong correction and hides the real one.

**Retire when.** Never — this is how the game works. It escalates instead: once
`condition` halting (1.6) exists, "wood below N" becomes a halt predicate rather
than a thing to remember to look at.
