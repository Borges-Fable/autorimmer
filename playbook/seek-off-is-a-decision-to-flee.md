# Turning seek off is a decision to flee, and the digest will not say so

- **severity**: Critical
- **confidence**: verified-in-source (SeekAndKill `ThinkTreeInjector`, vanilla
  `CellFinderLoose`), observed-at-bench (M1 run `m1-20260831`, two deaths)
- **bites when**: any threat arrives while `seek-at-will` is off — which is the
  default state of a fresh colony

## What actually decides whether a colonist fights or runs

It is not `hostilityResponse`. SeekAndKill never touches that field.
`ThinkTreeInjector` inserts

    ThinkNode_ConditionalSeekAndKill -> JobGiver_SquadSeek

**above** `ThinkNode_ConditionalColonist` in the colonist think root (it targets
that node's index, falling back to `ThinkNode_QueuedJob`, the lord-duty subtree,
then `ThinkNode_ConditionalRevenantState`). The vanilla flee reaction lives
inside the colonist subtree, i.e. *below* the insertion point. So when
`SeekRegistry.ShouldSeek` passes, seek is reached first and the pawn fights;
when it does not, control falls through to the flee reaction and the pawn runs.

`ShouldSeek` requires the per-pawn toggle AND `!pawn.Drafted`.

## The reporting trap, in our own verb

`seek-at-will` echoes `hostility_response` in both its `before` and `after`
blocks, and on a fresh colony it reads `"Flee"` — before and after turning seek
ON. That is truthful about the vanilla field and misleading about behaviour:
with seek on, the flee node is never reached, so `Flee` is a setting that no
longer decides anything. Do not read it as a prediction. The field that predicts
is `state.seek.will_seek` (`toggled` alone is not enough either).

## Why this cost a colony

M1 `m1-20260831`, day 4. Seek was OFF — I had turned it off on day 1 after
enabling it at colony start marched all three unarmed colonists sixty-plus cells
at a fogged insect hive. A single manhunter crow then arrived. With seek off,
both armed colonists took the flee branch: Table was bitten, downed, and bled
out; Captain fled 150 cells into unexplored ground, went down there, and died of
blood loss before the last colonist could cross the distance. A bolt-action
rifle at Shooting 10 never fired.

The trade was real in both directions. The resolution is not "seek always" or
"seek never":

- **Seek ON as a standing posture** sends colonists to any standing hostile on
  the map, including one they cannot see and should not walk to. `turn.md`'s fog
  caveat ("a standing hostile you cannot see is real and unreachable") is about
  reading, and seek turns that reading into a march.
- **Seek OFF when a threat lands** is a decision that your armed colonists will
  scatter, individually, away from help.

## What to do

1. **Bound where they go to work, so seek has nowhere bad to march.**
   `ForbidUtility.InAllowedArea` gates cells for ordinary work and job targets
   against `Pawn_PlayerSettings.EffectiveAreaRestrictionInPawnCurrentMap`, so an
   `Area_Allowed` covering base + fields + the ground you have cleared keeps
   routine movement inside it. The `area` verb ships `create`; `assign` binds
   pawns to it. Do this at colony start, before the first advance. (Evan,
   2026-08-31: "manipulate the allow zone, this way colonists won't go to an
   area no matter what".)
2. **Then run seek ON as the standing posture**, not as something switched on at
   the letter — a threat letter can arrive mid-advance and the fight is decided
   before you read it.
3. **Set `hostilityResponse` to Attack, not Flee** — `assign {pawns:[…],
   hostility:"Attack"}` (gate: `PawnColumnWorker_HostilityResponse.DoCell`,
   `RaceProps.Humanlike`; the menu omits `Attack` for a pawn with
   `WorkTags.Violent` disabled, and the verb reproduces that). A fresh colony
   defaults every pawn to `Flee`. Seek sits above the flee node and normally
   pre-empts it, so this is the backstop for every case where seek does not
   apply: the toggle is off, the pawn is drafted (`ShouldSeek` requires
   `!pawn.Drafted`), or SeekAndKill is absent. Belt and braces — Evan,
   2026-08-31: "and attack, not flee".
4. Know the two things the area does **not** bind, so you do not over-trust it:
   `RespectsAllowedArea` returns false for a pawn in a Lord or with a
   `HostFaction`; and **fleeing ignores the area entirely** —
   `CellFinderLoose.GetFleeDestToolUser` scores candidate cells on distance from
   the threat, terrain danger and room separation, with no area check anywhere
   in it. An allowed zone will not stop a fleeing pawn leaving it. That is a
   second reason to keep seek on: with seek on, they do not flee in the first
   place.

Related: [[weapons-have-no-alert]], [[unforbid-before-expecting-pickup]],
[[one-doctor-is-zero-doctors]], [[read-every-return-or-lose-a-colonist]].
