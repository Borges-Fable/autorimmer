---
name: weapons-have-no-alert
trigger: day 0, and after any roster change, raid, or colonist death
severity: Critical
confidence: verified-in-source
source: session 9 sweep of all 133 vanilla Alert_ classes; IncidentWorker_Raid.cs:171
---

**What.** Count armed colonists against the roster and act on the gap. Nothing in
the game will ever tell you about it.

**Why.** Across all 133 vanilla alert classes, **none covers armament.** The three
weapon-related alerts are per-pawn mismatches (shield belt with a ranged weapon,
and its pair), not "you have six colonists and two guns". `Alert_NeedDefenses` is
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
