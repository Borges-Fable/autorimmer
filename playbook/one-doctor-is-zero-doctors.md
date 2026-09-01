# One doctor is zero doctors

- **severity**: Critical
- **confidence**: observed-at-bench (M1 `m1-20260831` — both deaths ran through
  this), verified-in-source for the mechanism
- **bites when**: nothing, any more. **This lesson is now CODE.** `40ed42f`
  moved the check into the mod: `digest.work_coverage` publishes Doctor's floor
  of 2 at every read and `work-cover` repairs it. What is left here is the WHY —
  read it once so the number is not a house rule you are tempted to argue with.

## Why the floor is TWO, and why it is on AVAILABILITY

`Verse/WorkTypeDef.requireCapableColonist` is RimWorld's own "somebody must be
able to do this", and **`Doctor.requireCapableColonist` is FALSE.** The game does
not require a doctor to start a colony at all.

The 2 comes from the game's own doctor test instead.
`RimWorld/Alert_NeedDoctor.Patients`:

    (item.Spawned || item.BrieflyDespawned()) && !item.Downed
    && item.workSettings != null
    && item.workSettings.WorkIsActive(WorkTypeDefOf.Doctor)

**`!item.Downed` is in the game's own predicate.** One doctor's coverage is
therefore zero the moment that doctor is the patient — so the floor is on
AVAILABILITY, not capability, and the number that survives one casualty is two.
Coverage is not a count of one.

## What that cost, once

M1 day 1: three colonists, none with any Medicine skill. Table was set to
Medicine 7 and Doctor turned ON for him — and for nobody else, because he was
plainly the best and "every essential work type covered by someone capable"
reads as satisfied when exactly one capable pawn is on.

Day 4: a manhunter crow downed Table. The colony's only doctor was now the
patient. `Alert_NeedDoctor` fired at tick 229,044 — **after** he was already
down, which is far too late to be a signal, and is structural rather than
unlucky: the alert needs zero non-downed doctors AND a patient, so it cannot
warn about a single point of failure, only about its arrival. He bled out.
Captain, mauled in the same fight, went down into the same empty hole and died
too. Cause on both letters: *blood loss*. Neither died of the crow.

**And a doctor is a disproportionately likely casualty**, because the pawn you
gave the medical skill to is in the same fight as everyone else.

## The trap one level down: enabled is not capable

Every vanilla Doctor work-giver except `VisitSickPawn` requires
`Manipulation` — `DoctorTendEmergency`, `DoctorTendToHumanlikes`,
`DoctorRescue`, `DoBillsMedicalHumanOperation` and eight more
(`RimWorld/WorkGiver.MissingRequiredCapacity`). A doctor with no hands has the
work type ON, undisabled, and cannot tend anybody. `work_coverage` separates
`enabled` from `available` and names the missing capacity for exactly this
reason: the fix there is surgery, not a work priority.

## The verb trap that wastes the minutes you do not have

With a colonist down and bleeding, the obvious move is `tend`. It refuses:

    {"gate": "drafted-only",
     "reason": "this order is only offered to a drafted pawn
                (FloatMenuOptionProvider.Undrafted is false for it)"}

`tend` reproduces the float-menu gate, and vanilla only offers a manual tend
order on a **drafted** pawn. Know that before the emergency.

The response to a downed colonist is **`rescue`**, which forces the job through
`Pawn_JobTracker.TryTakeOrderedJob` and interrupts `LayDown`. The M1 run reached
for a work-priority flip instead — an adjustment to what a pawn *might* choose
next — and the chosen rescuer stayed asleep for ~6,100 ticks. `triage` publishes
the exact `rescue` envelope on every casualty row so that choice is not made
again.

Related: [[who-will-actually-do-it]], [[seek-off-is-a-decision-to-flee]],
[[read-every-return-or-lose-a-colonist]].
