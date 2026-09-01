# One doctor is zero doctors

- **severity**: Critical
- **confidence**: observed-at-bench (M1 `m1-20260831` — both deaths ran through
  this), verified-in-source for the mechanism
- **bites when**: the colony has exactly one pawn with Doctor enabled; that is
  the DEFAULT after any deliberate work-priority tidy-up

## The failure

`who-will-actually-do-it` already says the patient never counts as the worker:
`WorkGiver_DoBill.ShouldSkip` requires `billGiver != pawn`. That lesson is about
one bench and one bill. This is its roster-scale twin, and it kills faster.

M1 day 1: three colonists, none with any Medicine skill. I set Table to
Medicine 7 and turned Doctor ON for him — and for nobody else, because he was
plainly the best and the checklist item ("every essential work type covered by
someone capable") reads as satisfied when exactly one capable pawn is on.

Day 4: a manhunter crow downed Table. The colony's only doctor was now the
patient, and there was no second doctor to tend him. `Alert_NeedDoctor` fired at
tick 229044 — **after** he was already down, which is far too late to be a
signal. He bled out. Captain, mauled in the same fight, then went down into the
same empty hole and died too. Cause on both letters: *blood loss*. Neither died
of the crow.

## Why the checklist did not catch it

`triggered.md`'s colony-start item 7 asks that every essential work type be
"covered by someone capable, and no bill-relevant type checked only on its
likely patient". Both clauses passed. One doctor IS coverage, right up until the
doctor is the casualty — and a doctor is a disproportionately likely casualty,
because the pawn you gave the medical skill to is in the same fight as everyone
else.

Coverage is not a count of one. **For Doctor specifically, coverage means two.**

## What to do

- At colony start, enable Doctor on **at least two** pawns, and give the second
  one enough Medicine to matter. On a three-pawn colony that is two of three.
  The cost is nothing: Doctor is a checkbox, and the second doctor still does
  their day job.
- Re-run the check on every roster change — `triggered.md`'s `roster-change`
  trigger already fires on death; make Doctor its first question, not one of a
  list. In M1 the fix (`work-priorities` Chili Doctor 0->3) was applied
  *after* the first death, which was too late for the second.
- Do not wait for `Alert_NeedDoctor`. Measured in this run: it fires when a
  patient already needs tending and nobody can, i.e. once the emergency exists.

## The verb trap that wastes the minutes you do not have

With Captain down and bleeding at 3.46/day, the obvious move is `tend`. It
refuses:

    {"gate": "drafted-only",
     "reason": "this order is only offered to a drafted pawn
                (FloatMenuOptionProvider.Undrafted is false for it)"}

`tend` reproduces the float-menu gate, and vanilla only offers a manual tend
order on a **drafted** pawn. The undrafted route — the one that actually saves a
patient — is not a verb at all: it is `work-priorities`, enabling Doctor so the
game's own `WorkGiver_Tend` picks the job up. Know that before the emergency,
because discovering it during one costs a round trip you are paying for in
blood.

Related: [[who-will-actually-do-it]], [[seek-off-is-a-decision-to-flee]],
[[read-every-return-or-lose-a-colonist]].
