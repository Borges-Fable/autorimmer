---
name: who-will-actually-do-it
trigger: after queuing ANY bill — medical, production, research
severity: Critical
confidence: observed-at-bench
source: Evan, session 7 — "noone is assigned doctor but rubio so he won't walk to the bed"
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

This bit twice in one session: a bionic-leg install with the only doctor as the
patient, and 3.4's own acceptance bullet 4, which failed for the identical reason.

**How to apply.** `pawn {sections:["work"]}` returns the full row with a priority
per work type. Read it for the whole roster and confirm at least one non-patient
pawn is non-zero for the relevant work. Generalises past medicine: a bill on any
bench needs someone with that work checked who is not otherwise pinned.
**"Nobody is assigned" is a silent failure mode, not an error.**

**Retire when.** A verb reports, at bill-creation time, who is eligible to take
the job — at which point this becomes a result field rather than a checklist item.
