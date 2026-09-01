# 3.5 dialog + interaction verbs (`20e5cda`) — the first live run of the POSIX suite

`accept/3.5-dialog-verbs.py`, `_RimWorld-Agent` session `20260901T195344`,
assembly `1.0.0+094b14a` (main's `3dc858d` artifact). Bench watched on
workspace 3 at 1920x1200 fullscreen.

**`3.5-full5.log`: 226 checks, 0 failed, 0 skipped, exit 0** — read directly
from `$?`, not through a pipe.

Every log in this directory is a real run, kept in order, because the sequence
is the evidence:

| log | result | what it bought |
|---|---|---|
| `3.5-dialog-verbs.log` | 101 PASS, skip at 3.2pre | phases 0–2 green on the first try; the caravan had not settled |
| `3.5-phase3.log` | skip at 3.4d | nothing sellable — the first wrong staging |
| `3.5-phase3b.log` | 3 FAIL in phase 0 | **the skip had left a trade session open** |
| `3.5-phase3c/d.log` | skip at 3.4d | goods spawned on the trader's own cell count as zero |
| `3.5-phase3e.log` | **2 FAIL: 3.9g, 3.9h** | the real finding |
| `3.5-full.log` | 1 FAIL: 3.9g2 | the first repair asserted `delta == 0`; the next deal paid silver |
| `3.5-phase3g.log` | 1 FAIL: 3.9g2 | `in (0, line)` is also wrong — 69 of 80 |
| `3.5-full3.log` | 8 FAIL in phase 5 | solar generator, sunset |
| `3.5-full5.log` | **226 PASS, exit 0** | |

## The finding: eight driver defects, zero mod defects

3.9g and 3.9h asserted that a bought good is colony stock the moment the deal
executes. **It is not, and the mod was right.**

`RimWorld/Tradeable.ResolveTrade` hands a buy to
`TransferableUtility.TransferNoSplit(thingsTrader, …)`, which walks the trader's
stack LIST and calls `RimWorld/Pawn_TraderTracker.GiveSoldThingToPlayer` once
per stack. That method places each stack with
`GenPlace.TryPlaceThing(thing, toGive.PositionHeld, mapHeld, ThingPlaceMode.Near)`
— at **whichever caravan member was carrying it**, not near the negotiator —
and then `pawn.GetLord()?.extraForbiddenThings.Add(thing)`.

Meanwhile the colony-side count of the rebuilt deal is
`ColonyThingsWillingToBuy`, whose test is
`Home[pos] || IsInAnyStorage()` (plus unfogged and reachable). A caravan parked
outside the home area satisfies neither, so bought goods are legitimately not
colony stock until somebody hauls them. Vanilla's own `Dialog_Trade` prints the
same optimistic post-deal column.

Three consecutive live deals, all correct and all different:

| deal | currency line | colony-side delta |
|---|---|---|
| paid for components | −360 | **−360** (exact) |
| sold components | +83 | **0** |
| sold pemmican | +80 | **69** |

An OUTFLOW is exact — the stacks are taken *from* colony scope. An INFLOW is
neither exact nor all-or-nothing: it is however many carriers happened to be
standing inside the home area. So the suite now asserts an **equality on the
outflow and a bound on the inflow**, and proves delivery where it is decidable —
the trader's own holdings (`972 → 889`, `103 → 93`, exact both times) and an
independent whole-map `things {def}` read.

`extraForbiddenThings` does NOT forbid the goods to colonists:
`RimWorld/ForbidUtility.IsForbidden(Thing, Pawn)` consults `pawn.GetLord()` —
the lord of the pawn *asking* — and a colonist has none. It stops the caravan
re-collecting what it just sold. Both delivered stacks read `forbidden: false`.

## What else the run repaired, all driver-side

- **A `precondition` skip left a trade session open.** `precondition()` calls
  `sys.exit(2)`, and phase 3 has five of them after `trade-start`. The next
  run's phase 0 then failed 0.8a/0.8b/0.11 — three red checks with nothing to
  do with the mod. `send()` now tracks the session and an `atexit` hook closes
  it however the process leaves.
- **The fixture assumed `traders[0]` was willing.** `CanTradeNow` is per-trader;
  a departing caravan and an arriving one coexisted and the arriving one sorted
  first. It now opens sessions until one takes, which is what a player does.
- **3.4d's guidance named a "trade radius" that does not exist.** It cost a
  staging round: 2000 silver spawned on the trader's own cell counted as ZERO,
  and the same 2000 at the negotiator counted in full.
- **An unpowered console presented as eight red checks.** A solar generator
  staged at 16:00 stops at sunset, so a suite green at 16:00 goes red at 22:00
  with nothing about the mod having changed. Now a named precondition that
  tells you to advance to daylight.

## Fixture, for the next runner

`--quicktest`, then: `dev:spawn-thing` Silver / ComponentIndustrial /
MealSurvivalPack at `pos:"pawn:<negotiator>"` (the HOME AREA is the test, not
distance to the trader) · `dev:spawn-thing CommsConsole` + `SolarGenerator`
within 6 cells (`PowerConnectionMaker.ConnectMaxDist = 6`) and one advance so
the power net forms · `dev:incident GiveQuest_Random` a few times until
`quests.counts.available >= 1` · run in daylight.
