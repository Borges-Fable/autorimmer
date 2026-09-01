# Turning seek off is a decision to flee

- **severity**: Critical
- **confidence**: verified-in-source (RimWorld `HumanlikeConstant` think tree,
  `Pawn_JobTracker`, SeekAndKill `ThinkTreeInjector`), observed-at-bench
  (M1 run `m1-20260831`, two deaths)
- **bites when**: any threat arrives while the colony has no declared posture —
  which is the default state of a fresh colony

**The checking is now code.** `posture` sets all three settings in one call and
names what it refused; `digest.posture` publishes `will_seek`, `area_bound`,
`attack`, `on_contact` and `flee_risk` at every read. This note keeps only why
the posture is what it is, and the trade you are making when you choose it.

## Why this cost a colony

M1 `m1-20260831`, day 4. Seek was OFF — turned off on day 1 after enabling it at
colony start marched all three unarmed colonists sixty-plus cells at a fogged
insect hive. A single manhunter crow then arrived. Both armed colonists took the
flee branch: Table was bitten, downed, and bled out; Captain fled 150 cells into
unexplored ground, went down there, and died of blood loss before the last
colonist could cross the distance. A bolt-action rifle at Shooting 10 never
fired.

The trade was real in both directions, and neither decision was wrong on its
own. What was missing is that **"seek off" is not a neutral state — it is a
decision to flee** — and nothing in the surface said so at the moment it
mattered.

## The mechanism, corrected

This note previously said that with seek ON the vanilla flee node is unreachable
and `hostility_response` "describes a node nothing consults". **That is wrong,
and it is wrong in the direction that killed the colony.** Verified 2026-09-01
(`b1b3060`; the reasoning and every citation live in `SeekVerbs.cs`'s posture
header and DESIGN's decisions log):

`JobGiver_ConfigurableHostilityResponse` — the only producer of
`JobDefOf.FleeAndCower` for a sane colonist — is in the **`HumanlikeConstant`**
think tree, which `Pawn_JobTracker.DetermineNextJob` runs **before** the main
tree and re-runs every 30 ticks with `JobCondition.InterruptForced`. SeekAndKill
never injects into that tree: `ThinkTreeInjector` needs one of four anchor nodes
at the root and `HumanlikeConstant` has none of them.

So `hostility_response` is decided **above** seek, not beneath it. `Flee` beats
seek. The M1 evidence is the proof rather than the counter-example: op 109 had
seek ON, hostility `Flee`, and Captain in `JobDriver_FleeAndCower` — which is
impossible if the flee node is unreachable.

## The policy choice

**`hostility:"Attack"` is the load-bearing setting, not a backstop.** Seek is
what happens after the close-range node declines.

- **Seek ON with hostility `Flee` is the worst combination available**, and the
  flee branch is two separate tests that are easy to conflate. It *triggers* on
  a threat inside 8 cells — `SelfDefenseUtility.ShouldStartFleeing`, which is
  the gate `TryGetFleeJob` opens with, and the only place distance and sight are
  checked. Where it *runs to* is scored against **every** threat the caches
  hold, distance and sight both off (`TryGetFleeJob` ->
  `CellFinderLoose.GetFleeDest`). That gap is the 150 cells: one crow inside
  eight cells started it, the whole map chose the destination. Meanwhile seek
  marches at anything too far away to have triggered the flee. That is M1 day 1
  and M1 day 4 in a single state, and it is what `digest.posture.flee_risk`
  names.
- **Seek ON as a standing posture** — not switched on at the letter, because a
  threat letter can arrive mid-advance and the fight is decided before you read
  it. Prefer `seek:"auto"`, which declines a pawn that is unarmed and Melee < 6;
  that is the rule that would have kept three unarmed colonists home on day 1.
- **Bound where they go, so seek has nowhere bad to march.** An `Area_Allowed`
  over base + fields + cleared ground. Know the two things it does not bind:
  `RespectsAllowedArea` is false for a pawn in a Lord or with a `HostFaction`,
  and **fleeing ignores the area entirely** — `CellFinderLoose.GetFleeDestToolUser`
  scores cells on distance from the threat, terrain danger and room separation,
  with no area check anywhere in it. That is a second reason to hold `Attack`:
  a pawn that does not flee cannot leave the zone by fleeing.
- **A pawn incapable of Violent work will never fight**, whatever you set. The
  game refuses it both levers, `posture` names it, and the answer is cover — not
  a setting.

Related: [[weapons-have-no-alert]], [[unforbid-before-expecting-pickup]],
[[one-doctor-is-zero-doctors]], [[read-every-return-or-lose-a-colonist]].
