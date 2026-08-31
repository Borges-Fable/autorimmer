---
name: wealth-buys-bigger-raids
trigger: any decision that adds significant colony wealth — turrets, walls, gear hoards; and every post-mortem
severity: Important
confidence: verified-in-source
source: session 9 — StorytellerUtility.DefaultThreatPointsNow; History recorders
---

**What.** Raid size scales with colony wealth. Arming up raises the threat you
armed against.

**Why.** `StorytellerUtility.DefaultThreatPointsNow` evaluates
`PointsPerWealthCurve` against `PlayerWealthForStoryteller`. So every turret,
every wall, every hoarded rifle raises the points budget of the next raid.

**This is why the naive correction does not converge.** The tempting post-mortem
after a wipe is "we died to raiders, build more guns." That raises wealth, which
raises raid points, which raises the bar you just failed to clear — positive
feedback inside the correction. Left alone it produces the oscillation Evan
described: die to raids, over-arm, starve, over-farm, die to raids.

**How to apply.** Two things, and the second is the useful one:

1. A defensive investment is judged against the raid it *creates*, not the raid
   that killed you. Prefer cheap-per-wealth defence (terrain, chokepoints, a
   partial wall that funnels) over expensive-per-wealth defence (turret spam).
2. **The game already records both sides of this loop.** `HistoryAutoRecorder`
   keeps Scribe-persisted series for wealth AND for threat points. So a
   post-mortem can plot the actual curve rather than argue about it — this is
   the one lesson whose evidence is free.

**Retire when.** Never — but the CALIBRATION (how much defence per unit wealth)
should move from prose to a number once several runs have been plotted against
those two recorders.
