# New Arrivals — colony notes

State and intent only. Lessons live in `playbook/`.

- **Map**: temperate forest, 250x250, Spring of 5500, quicktest seed (not
  reproducible — the tile is random per launch).
- **Roster**: Chili (F24, Mining 11!!, Plants 7!, Construction 7!!, Medicine 3,
  revolver) is the only survivor. Table and Captain died day 4, both of blood
  loss after a manhunter crow; both corpses lie where they fell, unburied
  (`Alert_ColonistLeftUnburied` standing). There is no grave and no way to build
  one until 3.3 ships.
- **Built** (all dev-staged at tick 723, nothing since): 9x9 granite barracks at
  120-128 x 112-120, wooden door at (120,116), 3 beds, 2 torch lamps, campfire
  at (124,119). A `SimpleResearchBench` was attempted at (123,117) and never
  placed — `dev:spawn-thing` said ok with `placed:0`.
- **Zones**: stockpile "home stock" 42 cells at [120,122,7,6], filter=all,
  Normal. Growing zone "rice field" 64 cells at [130,112,8,8], Plant_Rice,
  sown, ~7% growth at day 2 and not yet harvested.
- **Standing intents, unfinished**
  - The rice is the only route to raw food; the campfire's meal bill
    ("simple meals to 40", TargetCount 40) has produced **zero** and will stay at
    zero until the rice is harvestable. Survival meals (95 at start, ~60 days
    for one colonist) are carrying the colony.
  - Research: Pemmican selected, 0 progress, and unreachable without a bench.
  - 50 chop designations were issued map-wide; wood is 675 and climbing.
- **Hazards left standing**
  - **6 fogged insects** (4 megascarab, locust, spelopede) have been on the map
    since generation and have never been engaged. `threats.hostiles` has read 6
    all run while `pawns {filter:"hostile"}` reads 0. Do not go looking for them.
  - The barracks runs hot — 28C against an 8C outdoor on day 3, because the
    campfire and two torch lamps sit in a sealed 49-cell room. Harmless in
    Spring; deconstruct a torch lamp before Summer.
  - `Alert_NeedDefenses` fired in its day 2-5 window and was never answerable.
- **Decisions deferred**
  - Whether to bury/haul the two colonist corpses (no grave, no verb).
  - Whether to run the colony on with one pawn or restart the fixture — the
    ten-day acceptance is already lost on this seed.
