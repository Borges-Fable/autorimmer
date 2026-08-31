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

**How to apply.** `zone add growing` refuses without `plant` for exactly this
reason. To read what a zone grows, use the guarded backing-field route and check
for `source:"backing-field"` in the result; a null answer means genuinely
unconfigured, which the raw getter can no longer tell you once it has been asked.

**Retire when.** Never — but note this file as the standing example of a lesson
that was wrong in a way that pointed the agent at a symptom that does not occur.
