---
name: stockpile-scope-hides-your-own-supplies
trigger: any `resources.*` count reading low or zero; any `place-layout` / `build` shortfall
severity: Critical
confidence: observed-at-bench
source: run m1-20260901 — `resources.steel` read 0 for five consecutive in-game days while 811 steel sat on the map
---

**What.** `digest.resources.*` is **stockpiles-only**, and it says so in its own
`scope` field. A count of zero means *nothing is in a haul destination*. It does
not mean the colony has none, and it does not mean mining or chopping is the
answer.

**Why it bites.** The field is the first number anyone reads and it is the number
that decides what you do next. In `m1-20260901` it read `steel: 0` from day 17 to
day 22 while `things {def:"Steel"}` reported **811 on the map**. The whole time the
real problem was neither supply nor hauling: 308 of it was **forbidden** and the
rest lay outside the colonists' `Area_Allowed`. Mining was designated, mined, and
still the number stayed at 0, because the reachable stacks were being consumed as
fast as they appeared.

Three distinct causes all present as `resources.X == 0`, and each has a different
fix:

| the number says 0 because | the fix | the read that proves it |
|---|---|---|
| it is not hauled yet | nothing — wait, or raise Hauling | `food_rot.nutrition` ≫ `nutrition_in_stockpiles` |
| it is forbidden | `unforbid` | `things` rollup `forbidden` > 0 |
| it is outside the allowed area | `area {kind:"allowed", op:"add"}` | `things` total ≫ `place-layout`'s `available` |
| there genuinely is none | designate / mine / chop / buy | `available` 0 **and** total 0 |

**The verb that already answers this, and it is not the digest.**
`place-layout --dry-run` publishes all three numbers per material and names the
cause:

    "def":"Steel","needed":210,"short_by":6,"available":204,"in_stockpiles":0,
    "forbidden":966,
    "hint":"there is enough of this on the map, but 966 of it is FORBIDDEN —
            `unforbid` is the fix, not mining"

`available` is *reachable-and-unforbidden by a builder's own test*;
`in_stockpiles` walks SlotGroups only. On a fresh map the second is 0 for
material the colony owns and is standing on. **`shortfall` is computed from
`available`, never from `in_stockpiles`** — which is exactly why the dry-run is
the honest instrument and `resources.*` is the misleading one.

**How to apply.** When a `resources.*` count is low, do not act on it. Ask the
three-way question first:

1. `things {def:"X", detail:true}` — how much exists, and how much is `forbidden`?
2. `place-layout … --dry-run` (or `construction`'s `missing[]`) — what is
   `available` versus `in_stockpiles`?
3. Only if `available` is 0 **and** the map total is 0 is this a supply problem.

The corollary is the expensive half: **a growing `available` with a flat
`in_stockpiles` is a consumption problem, not a supply problem.** The colony is
using the material the instant it lands, and designating more will not move the
number. That is what five days of mining bought in `m1-20260901`.

**Retire when.** `resources.*` publishes a second figure with map-wide scope the
way `food_rot` already does — it carries `nutrition` (upper bound, map-wide),
`nutrition_in_stockpiles` (lower bound) and a `scope` note, and is the model the
other resources should follow. Until then this is a standing reading skill.
