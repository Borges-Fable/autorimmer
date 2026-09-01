---
name: growing-zone-default-is-potato
trigger: creating any growing zone; reading one that has never been configured
severity: Important
confidence: verified-in-source
source: backlog verification pass (2f2796e) CORRECTING an earlier version of this lesson; Zone_Growing.PlantDefToGrow
---

**What.** Set the plant in the same call that creates the zone. Never read the
property to find out whether it is set.

**Why — and note this lesson previously said the opposite.** An earlier version of
this playbook entry claimed "an unset zone grows nothing". That is **false**, and
the correction matters more than the original:

    if (plantDefToGrow != null) return plantDefToGrow;
    if (PollutionUtility.SettableEntirelyPolluted(this)) plantDefToGrow = Plant_Toxipotato;
    else plantDefToGrow = Plant_Potato;
    return plantDefToGrow;

An unset zone grows **potatoes** — toxipotatoes when polluted. Two consequences:

1. **The symptom is wrong in the old lesson.** An agent watching for "nothing is
   growing" will never catch this. The real symptom is *potatoes growing where
   rice was wanted*, which no nothing-is-happening check detects.
2. **There is a deadline.** The getter ASSIGNS on first read and the field is
   scribed, so the first thing that touches it — any observer, any UI draw, any
   save — permanently commits potato. Setting the plant at creation is not
   tidiness; it is the only window in which the choice is still free.

**How to apply.** `zone {op:"add", kind:"growing", rect:[x,z,w,h],
plant:"Plant_Rice"}` — `op` and `kind` are ARGS, not words in the op name
(`ZoneVerbs.Zone` reads `a.Str("op","add")` then `a.Str("kind")`), and the verb
refuses a plantless growing zone for exactly this reason. The refusal has an
escape hatch, **`allow_unset_plant:true`**, which creates the zone anyway; the
result says so, because `zones` will then report it unconfigured.

To read what a zone grows, use `zones`, which takes the guarded backing-field
route. Three fields, and the names matter:

- **`plant`** — the defName, or null.
- **`plant_configured`** — the boolean for "was this ever set". This is the
  unset test; `plant == null` is the same answer but `plant_configured` is the
  one that says so out loud.
- **`plant_source`** — `"backing-field"` when the guarded route worked,
  `"unavailable"` when it did not. *(An earlier version of this lesson told
  you to check `source:"backing-field"`. There is no `source` key. `VerbArgs`
  reads only the keys a verb asks for and results are plain dictionaries, so a
  wrong key name here is a silent `None`, not an error — which is exactly how
  an instruction like that survives review.)*

A null `plant` means genuinely unconfigured, which the raw getter can no longer
tell you once it has been asked.

**Retire when.** Never — but note this file as the standing example of a lesson
that was wrong in a way that pointed the agent at a symptom that does not occur.
