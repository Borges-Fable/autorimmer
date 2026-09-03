# Take over the open-ended run — colony "Andbourne", day 8 of Fall, year 5500

You are continuing a run already in progress. The bench is live and PAUSED at
tick 1,594,005. Do not relaunch anything.

    export RWA_RUN=openrun-20260902          # every call must carry this
    cd /home/dorian/projects/rimworld/autorimmer
    ./rwa/rwa status                          # expect: ok, paused, tick ~1594005

Dorian is watching. The game window is on Hyprland workspace 3; a read-only
cockpit follows the transcript on workspace 2. **The cockpit's map panel only
redraws on a `map-view` call** — issue one every few days so his screen stays
current. Take a `render --around base-center` picture every few days too and
send it to him; do not drive the colony off numbers alone.

## Read these first, in this order

1. `RUNS/openrun-20260902/RUN-CONTRACT-open-ended.md` — the six goals and the
   eleven standing rules. This is the contract; it has not changed.
2. `playbook/SESSION-START.md`, `checklists/turn.md`, `checklists/triggered.md`,
   `checklists/daily.md` — the loop and its trip-wires. `turn.md` loads whole.
3. `RUNS/openrun-20260902/PLAN-fort.md`, `PLAN-production-hall.md`,
   `PLAN-food-and-export.md` — the strategy, with the arithmetic worked out.
4. `RUNS/openrun-20260902/andbourne-ii.md` + `andbourne-ii.ir.json` — the base
   blueprint and every constraint behind it.
5. `RUNS/openrun-20260902/checklist.ndjson` — the run ledger. Keep appending.

## The colony

Three colonists. Three are dead (Aaron, Kelsey, Fitz — all blood loss).

| | id | role | key skills |
|---|---|---|---|
| **Bonnie** | 15239 | researcher / doctor / the militia | **Shooting 20★★**, Social 16★★, Medicine 3, Intellectual 4. **Night owl** — her schedule is inverted (sleeps 08–15). Holds the bolt-action rifle. |
| **Ellis** | 1022 | grower, and the only builder-grade Construction | Plants 6★, Construction 5★, Animals 9★★, Cooking 4★, Crafting 4★. `Quick sleeper`. |
| **Walton** | 1014 | cook / construction / crafting / hauling / cleaning | Artistic 7★★, Social 6★★, Mining 4★. **Violence-disabled (permanent)** — never counts toward militia. |

Everyone is healthy. Moods 32–74 and recovering from a bad stretch.

## State that matters

- **Food is solved.** 16.7 days stockpiled, 18.7 map-wide, off a ~490-cell rice
  farm plus 80 cells of cotton. Ellis is the sole grower and is off hauling and
  cleaning deliberately so his Plants keeps levelling.
- **Winter is close.** Outdoor 2 °C and falling; `Alert_NeedWarmClothes` is up
  and we have no cold gear. Cotton is growing for cloth. A `HandTailoringBench`
  is 75 stone, no research, no power — that is the parka route.
- Materials: **690 sandstone blocks, 1,515 granite, 742 + 741 chunks**
  (~20 blocks per chunk, so ~30,000 blocks available), 1,042 steel, 800 silver.
- **Components are down to 6.** The goals need ~41. There are 22
  `MineableComponentsIndustrial` cells on the map (~2 each) around (199,212).
  Mining them is the only non-trade source. This is a real constraint — treat it
  as one.
- Medicine 12 and falling, with no way to make more until Plants 8 unlocks
  healroot. Dorian's standing intent: **promote a second grower when Ellis hits
  Plants 8.**
- Research: `PackagedSurvivalMeal` 119/500. Nine projects done, including
  Electricity, Batteries, SolarPanels, Stonecutting, AirConditioning.

## What is in flight — watch it to completion

**`ly-7` is placed: 365 elements, the south band of the new base** (production
hall + laboratory) at origin 82,74 on clear ground. 60 blueprints are
`awaiting-materials` on sandstone; Walton is building, Ellis and Walton are
cutting blocks. Your first job is to see this through.

The full base is `andbourne-ii.ir.json`, origin **82,74**, 42×34. It cannot be
placed in one call — `place-layout` caps at 600 elements and the design is
1,673 — so it is being built in bands. `andbourne-ii-south.ir.json` is the
placed one; slice the rest the same way (grid row 0 is NORTH).

**Build order, and the reason for it:**

1. **South band (in progress)** — clear ground, no collisions.
2. **The bedroom column**, so people have somewhere to sleep.
3. **Move everyone into the new bedrooms.**
4. **Only then demolish the old rooms and build the north band.** The kitchen
   and freezer sit on top of where two colonists currently sleep, and it is
   winter. Do not invert this order.

Place with `--stuff '*=BlocksSandstone'` and `--stuff 'ElectricTailoringBench=Steel'`.
Single-material sandstone is deliberate: it carries `Beauty ×1.1`, builds faster
than granite, and we have far more of it than the design needs.

**The kitchen is the highest-value room in the plan.** Cooking currently happens
at a campfire in the open, which has caused seven food poisonings and two
colonists downed. `FoodPoisonChance` is a curve over room Cleanliness that
reaches exactly zero at ≥ −2, and a roomless bench eats a flat 2% instead. The
designed kitchen is 54 cells, floored, two doors deep from outside — build it as
drawn; the size is deliberate, because cleanliness is an average and a bigger
room dilutes tracked-in filth.

## Goals — where they stand

- **G1 deep scanner** — needs MicroelectronicsBasics (3,000) → HiTech bench →
  DeepDrilling (1,000) → GroundPenetratingScanner (1,000), plus **one
  ComponentSpacer that cannot be crafted at any tech we can reach.** It must be
  bought. That needs a comms console, which needs Microelectronics. This is the
  long pole of the whole run.
- **G2 geothermal** — GeothermalPower (3,200). The generator has exactly ONE
  legal footprint, x86..91/z94..99, and the blueprint already places it there,
  inside the walls.
- **G3 militia** — Bonnie is armed. Ellis is violence-capable and has a revolver;
  Walton is violence-disabled and never counts. A third firearm needs Smithing
  (700) + Machining (1,000) + a machining table. Armour beyond what we hold needs
  `FlakArmor` (1,200).
- **G4 base growth** — the rebuild is this goal. Record room count and enclosed
  cells at each day boundary; `rooms` gives both.
- **G5 art** — **blocked, not deferred.** Nothing in the verb surface installs a
  `MinifiedThing`, so sculptures can be made but never placed. Do not spend
  labour on art for G5. Art remains viable as an export good.
- **G6 strangers** — answer every joiner/wanderer/refugee offer **on purpose**
  and write the reason in the ledger. Accepting is usually right: labour is the
  binding constraint on everything else.

## Standing rules that have already cost this colony

- **Never set a work priority to 0; use 4.** Every joiner arrives with a dozen
  work types at 0. Check `pawn {sections:["work"]}` for zeros the moment anyone
  joins, before anything else.
- **A forced job is interrupted and restarted by re-forcing it.** Issue a
  `prioritize` once and let it finish. Repeatedly re-forcing a tend prevents the
  tend from ever completing.
- **Read the journal and digest on every single return from `advance`.** Never
  loop advances without reading between them.
- **The doctor cannot treat themselves.** When a casualty is the colony's
  medic, force a *different* pawn onto the tend immediately — do not adjust
  priorities and hope.
- **Filth drives mood.** "Hideous environment" was the final straw on both
  mental breaks this run. Mining rubble reached 4,162 pieces. Check filth and
  keep Cleaning at priority 1 for someone.
- **Extra sleeping furniture turns a bedroom into a barracks** (−7 mood). One
  bed per room.
- Butcher within a day of a kill or do not hunt. Check `MinifiedThing` and
  unworn armour daily. `find-rect` before you place. Read `uses_outdoor_temp`,
  not just `enclosed`.

## Hunting, with the numbers

`manhunterOnDamageChance`: **Emu 1.00, Ostrich 1.00** — both retaliate every
time and both move faster than a colonist. Iguana, tortoise and monitor lizard
have no manhunter field and are safe. Nothing publishes this; it is read from
`Data/*/Defs`. An ostrich downed a colonist this run and a friendly rifle shot
another during the swarm — do not put a rifleman behind melee fighters.

## Cadence

`advance --until.letter --timeout_ticks 60000` is the default. Read the journal
through the mod (`journal --verb`) — that is what clears the advance gate. Save
at every day boundary and before every threat. Never advance past a letter that
carries a choice. Log a ledger line per checklist evaluation to
`RUNS/openrun-20260902/checklist.ndjson`.

The run ends when the colony ends, when Dorian calls it, or when all six goals
hold together for ten consecutive in-game days. The value of this run is the
record, not the score.
