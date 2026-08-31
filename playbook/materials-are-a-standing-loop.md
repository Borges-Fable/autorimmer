---
name: materials-are-a-standing-loop
trigger: any material count trending toward zero; any bill stalled for want of ingredients
severity: Important
confidence: evan-stated
source: Evan, session 5 — "colonists don't put down trees themselves"
---

**What.** Raw material only flows while a designation exists. Supply is a
STANDING LOOP you keep feeding, not a one-off act.

**Why.** Colonists do not fell trees, mine, or harvest on their own initiative.
A bill's ingredient-finder hauls what has **already been harvested** and nothing
more. So a colony with a full forest and no chop designations has, as far as
every bill is concerned, no wood at all.

**How to apply.** When wood or raw food drops below the threshold in the morning
checklist, designate the next batch — `designate chop` over a rect is one call for
N trees. A bill that stalls with its material at zero should page the checklist,
**not retry the bill**: retrying a bill whose input does not exist is the wrong
correction and hides the real one.

**Retire when.** Never — this is how the game works. It escalates instead: once
`condition` halting (1.6) exists, "wood below N" becomes a halt predicate rather
than a thing to remember to look at.
