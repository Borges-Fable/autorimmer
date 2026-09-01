---
name: weapons-have-no-alert
trigger: day 0, and after any roster change, raid, or colonist death
severity: Critical
confidence: verified-in-source
source: session 9 sweep of every vanilla Alert_ class, recounted session 10; IncidentWorker_Raid.TryExecuteWorker
---

**What.** Count armed colonists against the roster and act on the gap. Nothing in
the game will ever tell you about it.

**Why.** Across every vanilla alert class, **none covers armament.** The count,
because "133" was quoted here as a census and is a count of DECLARATIONS:
**133 `class Alert_*` declarations, 7 of them abstract, so 126 concrete
alerts.** (The 133rd is `Alert_PermitAvailable`, which the decompiler emits
nested inside `Alert_UnusableMeditationFocus.cs` rather than in a file of its
own — 132 `Alert_*.cs` files, one extra class inside one of them.) The
abstract seven are `Alert_Critical`, `Alert_Precept`, `Alert_Thought`,
`Alert_Scenario`, `Alert_ActionDelay`, `Alert_JoyBuildingNoChairs`,
`Alert_Analyzable`.

The **three** weapon-related alerts are all per-pawn mismatches, never
coverage — name them, because this file previously said "three" and listed
two:

1. `Alert_ShieldUserHasRangedWeapon`
2. `Alert_HunterHasShieldAndRangedWeapon`
3. `Alert_BrawlerHasRangedWeapon` — `HasTrait(TraitDefOf.Brawler) &&
   equipment.Primary != null && equipment.Primary.def.IsRangedWeapon`

None of them says "you have six colonists and two guns". `Alert_NeedDefenses` is
buildings-only and self-silences on day 6. Meanwhile vanilla's own tutor treats
pre-combat equipping as `OpportunityType.Critical` on EVERY raid — the developers
consider it critical and still ship no standing signal for it.

Evan's framing, which is why this is the first item on the checklist: the alert
system is largely good enough *except* for stocking up on food, medicine and
especially weapons, "since raids will knock you out."

**How to apply.** Equipped weapons come from a pawn scan; spare weapons from
`things {category:"weapons"}` — the two populations are disjoint, so a spare is
only useful if someone can reach it (see [[unforbid-before-expecting-pickup]]).
Target at least one usable weapon per non-disabled colonist before day 5, and
re-check after every raid, because drops and deaths both move the number.

**Retire when.** A vanilla or modded alert covers armament count, or the digest
carries `weapons_per_colonist` and the checklist reads it directly.
