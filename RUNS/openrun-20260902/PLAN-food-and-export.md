# Food, cold, and the colony's export — answers with the numbers

## 1. Can anyone make a freezer now? No, and the fallback is right

- `Cooler` — 90 steel, 3 comp, **200 W**. No generator exists and none can be
  built: `WoodFiredGenerator` burns wood (32 on the map, no trees), solar needs
  `SolarPanels` 600, geothermal needs 3,200.
- `PassiveCooler` — the only unpowered option, and it rules itself out in its
  own description: **"Not efficient enough to refrigerate food."** It also costs
  **50 WoodLog** against the 32 that exist, and "must be regularly replenished
  with wood."

Everything converges on power. **SolarPanels (600) is the cheapest route to a
freezer, a stove, and a lit base**, and it goes immediately after Batteries.

## 2. Survival packs are the right answer, and the recipe is better than I thought

    CookMealSurvivalBulk    research PackagedSurvivalMeal (500), work 1440
      in :  1.20 nutrition protein (MeatRaw / AnimalProductRaw)
          + 1.20 nutrition PlantFoodRaw
      out:  4 x MealSurvivalPack @ 0.9 nutrition = 3.6 nutrition

**2.4 nutrition in, 3.6 out — a 1.5x multiplier — and the output never rots.**
Our starting 57 packs still read `imperishable: 51.3` in `food_rot` after six
days at 25 C, which is the proof. No freezer, no rot clock, no waste.

It still needs a stove, so the order is fixed:

    Batteries 400  ->  SolarPanels 600  ->  PackagedSurvivalMeal 500
                       + SolarGenerator (100 steel, 3 comp, 1700 W)
                       + ElectricStove   (80 steel, 2 comp, 350 W)

1,500 research points to permanent food security. That is cheap next to
GeothermalPower's 3,200 and it should come first.

**Winter arithmetic**, one quadrum, four colonists:

    96 nutrition needed  =  107 survival packs
    raw input required   =  64 nutrition  (32 protein + 32 plant)

## 3. Farmland: we are using exactly half of it

Counted off the terrain scan, not estimated:

    fertile soil cells on the map : 123
    inside the current rice zone  :  62
    UNUSED                        :  61   (50%)

One harvest of the current 62 cells is **18.6 nutrition**. The full 123 cells
is **36.9** — which covers the entire 32-nutrition plant half of winter in a
single harvest. Expanding the zone costs nothing but a `zone` call: the soil is
already there, in a blob running x79..93 / z108..121.

Rice, not corn, until winter is past: rice matures in ~3.4 days against corn's
~11.6, and the Summer letter's warning is that nothing grows in winter cold.
Corn is the better crop per unit of work and is worth switching to in spring.

## 4. Protein: tame, do not hunt — and tame the right bird

The numbers that decide it (`race.manhunterOnTameFailChance`):

| animal | tame-fail manhunter | body size | egg interval | verdict |
|---|---|---|---|---|
| **Ostrich** | **0.10** | 1.0 | 3.33 days | **tame these** |
| Emu | **1.00** | 0.6 | 3.33 days | never tame, never hunt |
| Iguana | – | 0.4 | 5.66 days | safe, small |
| Tortoise | – | 0.5 | 6.66 d, 1-3 eggs | safe, slow |
| Monitor lizard | – | 0.6 | 6 days | safe |

A failed tame on an **emu is a 100% manhunter roll**; on an **ostrich it is
10%**. Ellis is Animals 9 with a major passion, which is as good as this
mechanic gets. And the payoff is not just meat: **eggs are
`AnimalProductRaw`**, which is exactly the protein half of the survival-pack
recipe — so a tamed ostrich flock feeds the colony **without killing
anything**, on a 3.33-day cycle, forever.

This is standing rule 9 ("Tame animals... renewable meat that walks itself
home") with the risk numbers attached. Hunting stays for safe targets only:
iguana, monitor lizard, tortoise.

## 5. The export: stone sculptures

`SculptureSmall` is **50 stuff and no research**; large is 100, grand is 400.
Stuff can be stone blocks, and the map holds **1,594 chunks ~ 31,000 blocks**,
free, already on the ground, replenished by every mining operation. Walton is
Artistic 7 with a major passion and nobody else on the roster is above 1.

Art is therefore the only high-value thing this colony can make from a material
it has in unlimited supply, and it is the answer to the ~1,200 silver of
plasteel, gold and advanced components that component independence needs.

The install-verb gap (git-bug `6d4ca8a`) blocks G5 because a sculpture cannot
be **placed** — but it does not block **selling** one, which is what a minified
thing is already fit for. So art-for-export and art-for-G5 are separable, and
only the second is dead.

## 6. Revised priority order

1. **Expand the rice zone to all 123 fertile cells** — free, immediate, doubles
   the harvest.
2. **Tame ostriches** — renewable protein, no combat, Ellis is the right pawn.
3. **Batteries -> SolarPanels -> PackagedSurvivalMeal** (1,500 points) — this is
   the food-security spine and it outranks geothermal.
4. Solar generator + battery + electric stove; release the
   `Alert_NeedMealSource` mute the moment the stove is powered.
5. Hand tailoring bench (75 stone, no steel, no power) — parkas before winter.
6. Everything else per `PLAN-fort.md` and `PLAN-production-hall.md`.

---

## Standing intents (added day 13 Summer, after the famine)

- **Ellis is the sole grower**, Plants 6 with a minor passion and the
  `Quick sleeper` trait (fewer sleep hours = more working hours). He is off
  hauling and cleaning entirely — Dorian's point being that those two eat the
  most time and are what stops a specialist levelling. Walton absorbed cooking,
  construction, smithing, tailoring, crafting, hauling and cleaning; Fitz keeps
  research and doctor.
- **A SECOND GROWER is owed once Ellis reaches Plants 8.** Two reasons beyond
  redundancy: 8 is `Plant_Healroot`'s `sowMinSkill` (our own medicine supply,
  and we are down to 25 industrial medicine with no way to make more), and one
  grower is a single point of failure — which this colony has already learned
  once, when its only Medicine-4 pawn was the patient and died.
- **Psychoid is now sowable.** `Plant_Psychoid` has `sowMinSkill: 6` and Ellis
  just crossed it. That is the export crop the silver gap needs — the shopping
  list in this file is ~1,200 silver against 800 held, and drugs are the
  highest value per cell this roster can produce.
