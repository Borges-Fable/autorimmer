---
name: who-will-actually-do-it
trigger: after queuing ANY bill — medical, production, research
severity: Critical
confidence: verified-in-source
source: Evan, session 7 — "noone is assigned doctor but rubio so he won't walk to the bed"; promoted to source-cited by the verification pass (2f2796e) — WorkGiver_DoBill.ShouldSkip, WidgetsWork.DrawWorkBoxFor
---

**What.** After queuing a bill, confirm some pawn who is NOT the patient or the
blocker has that work type enabled. Skill is not assignment.

**Why.** With manual priorities off, the Work tab is a checkbox column: `0` means
unchecked, `3` means checked. A colonist with maxed Medicine will not take a
surgery job if their Doctor box is unchecked. **And the patient does not count —
self-surgery is not a thing.** If the only pawn with Doctor checked is the one on
the table, the bill sits forever, the patient never even walks to the bed, and
nothing looks broken. There is no job for anyone to take, so there is no error,
no alert, and no stalled-job indicator.

Both halves are now source-cited, not just observed. The checkbox column is
literally priority 0/3: `WidgetsWork.DrawWorkBoxFor`'s
`!useWorkPriorities` branch writes `SetPriority(wType, 0)` on a checked box
and `SetPriority(wType, 3)` on an unchecked one. And the patient exclusion is
one clause: `WorkGiver_DoBill.ShouldSkip` scans bill givers with
`billGiver != pawn` — when the only pending bill giver IS the pawn, ShouldSkip
returns true, the scanner never runs, and no job is ever produced. "Nothing
looks broken, it just silently never starts" is now explained, not merely
observed.

This bit twice in one session: a bionic-leg install with the only doctor as the
patient, and 3.4's own acceptance bullet 4, which failed for the identical reason.

**How to apply.** `pawn {id:<n>, sections:["work"]}` returns the full row with
a priority per work type. **`pawn` is SINGLE-PAWN and `id` is required**
(`PawnVerbs.PawnDetail` → `ctx.Args.IntReq("id")`), so "read it for the whole
roster" is **one call per colonist — N round-trips**; take the roster from
`pawns` first. Confirm at least one non-patient pawn is non-zero for the
relevant work. Generalises past medicine: a bill on any
bench needs someone with that work checked who is not otherwise pinned.
**"Nobody is assigned" is a silent failure mode, not an error.**

**Retire when.** A verb reports, at bill-creation time, who is eligible to take
the job — at which point this becomes a result field rather than a checklist item.
