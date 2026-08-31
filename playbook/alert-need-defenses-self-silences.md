---
name: alert-need-defenses-self-silences
trigger: the whole M1 window — this is a lesson about an alert you must NOT rely on
severity: Important
confidence: verified-in-source
source: curriculum audit, session 4; re-confirmed in the session-9 verification pass
---

**What.** `Alert_NeedDefenses` only fires on days 2 through 5. Do not build a
checklist item around watching for it.

**Why.** The alert tests `GenDate.DaysPassed` and silences itself on day 6
**whether or not any defences exist**. A checklist that watches for the alert
therefore stops working exactly when a slow build still needs it — the alert going
quiet reads as "resolved" when it means "expired".

This is the clearest instance of the dangerous middle case: not *no signal*
(which you notice), and not *a good signal* (which you lean on), but a signal
that exists, looks authoritative, and quietly stops being true.

**How to apply.** Check defences **structurally** across the whole M1 window —
count sandbags, turrets and wall coverage — rather than waiting to be told. Same
discipline applies to `Alert_FireInHomeArea`, which is home-area-scoped, so "no
alert" does not mean "no fire".

**Companion signals that ARE trustworthy**, so nobody re-derives them:
`Alert_NeedWarmClothes` genuinely forecasts up to three twelfths ahead via
`GenTemperature.AverageTemperatureAtTileForTwelfth`, and
`Alert_MajorOrExtremeBreakRisk` is threshold-based ahead of an actual break.
Lean on both; do not rebuild them.

**Retire when.** Never — the day range is in the alert's own source.
