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
count sandbags, turrets and wall coverage — rather than waiting to be told. The
count matters because the alert's own structural test is satisfied by a SINGLE
qualifying building anywhere on the map (one sandbag silences it permanently,
even inside the day-2–5 window — verification pass, 2f2796e). Same discipline
applies to `Alert_FireInHomeArea`, which is home-area-scoped, so "no alert"
does not mean "no fire".

**The class is bigger than this alert, and bigger than one grep.** The audit
named two time-scoped alerts; the verification pass found a third by reading
(`Alert_NeedMealSource` is silent before day 2); and 4.1's authoring found a
fourth that even the recommended `GenDate.DaysPassed` grep would miss —
`Alert_LowFood.GetReport` opens with `if (TicksGame < 150000f) return false;`,
2.5 days of silence keyed on ticks, not days. Any future mining pass sweeps
`Alert_*` for BOTH idioms before trusting an alert's early-game silence.

**And there is a THIRD idiom, which is GLOBAL — no per-alert grep can find
it, because it is not in any alert.** `AlertsReadout.AlertsReadoutUpdate`
opens:

    if (Mathf.Max(Find.TickManager.TicksGame, Find.TutorialState.endTick) < 600)
        return;
    if (Find.Storyteller.def.disableAlerts)
    {
        activeAlerts.Clear();
        return;
    }

So **every alert in the game is suppressed for the first 600 ticks** (longer
if a tutorial is running — the `endTick` term), and a storyteller with
`disableAlerts` set silences the readout permanently, clearing the active
list rather than skipping the scan. Neither fact is discoverable by sweeping
`Alert_*` at all. The sweep has to include the readout that drives them.

Practical consequences, both small and both real: a colony-start checklist
that reads `digest.alerts` before tick 600 is reading an empty list and must
not treat that as "all clear" (`checklists/triggered.md`'s colony-start
section runs structurally for exactly this reason); and `turn.md`'s trust
table is void wholesale under a `disableAlerts` storyteller, which is a
one-time read at colony start, not a per-turn check.

**Companion signals that ARE trustworthy**, so nobody re-derives them:
`Alert_NeedWarmClothes` genuinely forecasts up to three twelfths ahead via
`GenTemperature.AverageTemperatureAtTileForTwelfth`, and
`Alert_MajorOrExtremeBreakRisk` is threshold-based ahead of an actual break.
Lean on both; do not rebuild them.

**Retire when.** Never — the day range is in the alert's own source.
