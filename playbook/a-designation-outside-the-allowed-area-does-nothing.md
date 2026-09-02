---
name: a-designation-outside-the-allowed-area-does-nothing
trigger: any designation whose targets can MOVE — hunt above all; also chop/mine/harvest near the area edge
severity: Critical
confidence: observed-at-bench
source: run m1-20260901 — six deer designated for hunting, `accepted: 6`, and a colonist starved to death while the herd stood untouched
---

**What.** `designate` reports whether the DESIGNATION was accepted. It says
nothing about whether any colonist is *allowed to go there*. A designation on a
target outside every pawn's `Area_Allowed` is valid, permanent, and completely
inert.

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

**Why it is invisible.** Nothing reports it. `designate` echoes `accepted` and
`rejects`, and neither is about reachability-under-area. The pawn does not
refuse a job — it never generates one, so there is no refusal to read, no
`blocked` state on the designation, and no alert. `Alert_LowFood` fires about the
*symptom* days later. It is exactly the shape of
[[stockpile-scope-hides-your-own-supplies]]: a truthful number answering a
different question than the one you asked.

**How to apply.** After any designation whose targets can move, and after any
`area` change, check the two against each other:

1. `pawns {filter:"wildlife"}` (or `things`) for the targets' CURRENT positions.
2. `areas` for the allowed rects.
3. If the targets are outside, the fix is `area {kind:"allowed", op:"add"}` —
   **not** re-designating, which will succeed again and still do nothing.

The general rule: **a designation is a wish; the allowed area is the franchise.**
Hunting is the sharp case because herds migrate on their own between the
designation and the work, so a designation that was reachable when made can
become inert without anything changing on your side.

**Retire when.** `designate` reports, per target, whether any pawn's effective
allowed area contains it — the same `ForbidUtility.InAllowedArea` test `posture`
already uses for `area_bound`. Until then this is a manual cross-check, and it is
worth doing every time food depends on it.
