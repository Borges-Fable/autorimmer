---
name: a-designation-outside-the-allowed-area-does-nothing
trigger: any designation whose targets can MOVE — hunt above all; also any `area` or `posture` change, which can make yesterday's designations inert without touching them
severity: Critical
confidence: observed-at-bench
source: run m1-20260901 — six deer designated for hunting, `accepted: 6`, and a colonist starved to death while the herd stood untouched
---

**What.** A designation on a target outside every capable colonist's
`Area_Allowed` is valid, permanent, and completely inert. **`designate` now says
so** (git-bug `b7359fa`) — read the two counts beside `accepted`, and act on
them:

    accepted             the gate took N targets
    accepted_actionable  …of which this many lie inside SOME capable pawn's area
    accepted_unreachable …and this many lie inside NOBODY's

A batch where **no** target is workable by **any** capable colonist is
**refused** outright, before anything is written, with the correction in
`refused.hint`; `allow_unreachable:true` overrides it when you mean to designate
ahead of an area expansion. A MIXED batch is never refused — it reports, and the
numbers are the whole point.

**What it cost.** `m1-20260901`, deep winter, crops dormant, `food_days` falling
through 3.5 → 1.2 → 0. Six deer were designated:

    designate {type:"hunt", things:[…]}  ->  {"ok":true,"accepted":6,"targeted":6}

Trev had Hunting priority 1 and Shooting 11. **Nothing happened for four in-game
days.** The herd had migrated from x170–182 to x146–155, z39–52 — outside the
allowed area — and a hunter will not step outside his area to reach a valid
hunt designation. `Marco` starved to death at tick 3,072,772 with five
designated deer standing on the map.

The read that finally showed it was not the designation at all. It was
comparing the herd's positions against the area rects by hand:

    pawns {filter:"wildlife"}  ->  Deer@146,41 … Deer@155,42
    area 6 covered x80–150 z95–160, x100–139 z58–97, x148–189 z100–134
    -> every deer was outside all three

**Why it was invisible.** Nothing reported it. `designate` echoed `accepted` and
`rejects`, and neither is about reachability-under-area. The pawn does not
refuse a job — it never generates one, so there is no refusal to read, no
`blocked` state on the designation, and no alert. `Alert_LowFood` fires about the
*symptom* days later. It is exactly the shape of
[[stockpile-scope-hides-your-own-supplies]]: a truthful number answering a
different question than the one you asked.

**Three things the new answer is NOT**, each of them a way to be wrong about it:

1. **It is not a pathing test**, and `reach.test` says so out loud. A target
   inside the area can still be unroutable — no path, a closed region, a locked
   door. `reachable {from, to, pawn}` is that question.
2. **"Has an area assigned" is not "is restricted".**
   `RimWorld/ForbidUtility.InAllowedArea` ignores an area whose `TrueCount` is
   0, so a pawn bound to an empty area is unrestricted. `reach.pawns[].restricted`
   is the honest bit; `area` is only the label.
3. **The franchise is CAPABILITY, not assignment.** `reach.capable` counts
   colonists who *could ever* do the work; `reach.enabled` counts those with the
   work type actually switched on. A clean area with `enabled: 0` is the other
   half of the same silence, and it is a `work-priorities` call, not an `area`
   one — see [[who-will-actually-do-it]]. Run m1-20260901 had both at once.

**How to apply.** After any designation whose targets can move, and after any
`area` or `posture` change:

1. Read `accepted_unreachable` on the designate envelope. Non-zero is the whole
   finding; `reach.areas[]` names the area, its id, and how many of the targets
   it shuts out, so the fix is one call away.
2. The fix is `area {kind:"allowed", op:"add", id:<id>, rect:[…]}` or
   `posture {pawns:[…], area:null}` — **not** re-designating, which will succeed
   again and still do nothing.
3. **For an area change AFTER the fact, nothing re-checks the designations
   already standing.** Re-run the same designate with `dry_run:true`: the `reach`
   block is computed on a dry run too, so it costs nothing and says whether
   yesterday's orders have come back to life.

The general rule: **a designation is a wish; the allowed area is the franchise.**
Hunting is the sharp case because herds migrate on their own between the
designation and the work, so a designation that was reachable when made can
become inert without anything changing on your side.

**Retire when.** The verb half is DONE — `designate` reports per target and
refuses an all-unreachable batch (`b7359fa`, shipped, `DesignateReach.cs`). What
is left is the STANDING check: nothing re-evaluates designations already on the
map when the area or the roster moves, which is why item 3 above is still
manual. Retire the rest when a watch reports inert designations at the turn
boundary the way `BillWatch` reports a starving bill.
