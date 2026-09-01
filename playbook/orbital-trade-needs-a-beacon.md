---
name: orbital-trade-needs-a-beacon
trigger: before calling any orbital trader, and before staging one in a suite
severity: Critical
confidence: verified-in-source
source: Evan, session 20 ("you need another object to actually trade… orbital trade beacon I think") — confirmed at RimWorld/TradeShip.cs, RimWorld/TradeUtility.AllLaunchableThingsForTrade, RimWorld/Building_OrbitalTradeBeacon.cs
---

**What.** An orbital trade needs **two** powered buildings, not one: a **comms
console** to reach the ship, and an **orbital trade beacon** to have anything to
sell. Without the beacon the call succeeds, the session opens, and the colony
appears to own nothing.

**Why.** They are different code paths and neither substitutes for the other.

- **Reaching the ship** is the console. `RimWorld/Building_CommsConsole
  .GetCommTargets` is `passingShipManager.passingShips.Cast<ICommunicable>()`
  plus factions, and the console's own `CanUseCommsNow` requires power. The
  ship's side is trivial — `RimWorld/TradeShip.CanTradeNow` is `!Departed`.
- **Having something to sell** is the beacon.
  `RimWorld/TradeShip.ColonyThingsWillingToBuy` is
  `TradeUtility.AllLaunchableThingsForTrade(map, this)`, which iterates
  **`Building_OrbitalTradeBeacon.AllPowered(map)`** and only the cells in each
  beacon's `TradeableCells`. **The home area does not enter it, and neither
  does storage.**

**The radius is 7.9 and it is NOT a circle.**
`Building_OrbitalTradeBeacon.TradeableCellsAround` runs
`RegionTraverser.BreadthFirstTraverse` with the predicate `r.door == null` and a
16-region cap, keeping cells `InHorDistOf(pos, 7.9f)`. So the traversal **will
not cross a door**, and a wall cuts the radius short. A beacon outside your
warehouse "covering" it on the map does not cover it in code. (The beacon's own
`MakeMatchingStockpile` gizmo exists precisely because players cannot eyeball
this — it paints a stockpile over the real cell set.)

`AllPowered` is `comp == null || comp.PowerOn`, so a modded beacon with no power
comp counts; vanilla's has one, so in practice: powered.

**How this differs from a ground caravan, which is the case most runs meet
first.** Same verbs, different geometry at both ends, and getting them confused
costs a whole staging round (session 20 spent one):

| | ground caravan | orbital ship |
|---|---|---|
| reached by | walking up to the pawn (`trade-start {trader:<pawn id>}`) | comms console (`trade-start {trader:"<ship name>"}` — a **string**, from `comms-targets`) |
| what the colony can sell | `Home[pos] \|\| IsInAnyStorage()`, unfogged, reachable (`Pawn_TraderTracker.ColonyThingsWillingToBuy`) | **powered-beacon cells only**, 7.9 radius, no door crossing |
| bought goods arrive | INSTANTLY, at **the carrying caravan member's own cell** (`GenPlace.TryPlaceThing(thing, toGive.PositionHeld, …)`) — scattered, possibly far from base | a **drop pod** at `DropCellFinder.TradeDropSpot`, which prefers an unroofed cell beside a beacon — i.e. at the colony |
| in-flight state | none | a pod in the air is on **neither** side for a few ticks |

**How to apply.** Stage with `dev:incident {def:"OrbitalTraderArrival"}`, and
before you call: a powered comms console, a powered beacon, and the goods you
mean to sell standing **inside the beacon's cells** — same room, no door
between. Then `comms-targets` for the ship's name, and `trade-start` with that
name as a string. If `tradeables` comes back with nothing of yours on it, the
beacon is the first thing to check, not the trader.

Pairs with [[unforbid-before-expecting-pickup]] for the far end: goods that
arrive by pod still have to be hauled, and `data.after`'s colony-side count is
deal-scoped, not an inventory (git-bug `7e8c969`).
