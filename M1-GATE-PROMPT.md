AutoRimmer — the round that unblocks the M1 re-run

`main` is clean and pushed at `3dc858d`, no build debt: the assembly's pdb path
names this worktree and Release, and the last `Build:` commit is the tip.
Session 19 shipped `advance until:{condition|layout}`, `construction
--layout_id`, and a material bill that asks the builder's question. **Use them
— see "What is new since M1" below, because a worker who does not know they
exist will guess tick counts, and that is the habit this round is built on
top of.**

Read first, in order: `CLAUDE.md` (this repo) and
`/home/dorian/projects/rimworld/CLAUDE.md`; `RUNLOG.md` § Session 19, then
§ Session 13 (the M1 ruling); `DESIGN.md`'s decisions log from 2026-09-01
onward; then `git-bug bug show <id>` **and every comment** for 61794cd,
40ed42f, 722c951, 20e5cda.

## Why this round

Session 13 ruled that M1 does not close: two of three colonists were dead on
day 4 and the run stopped on day 6 of 10. The platform passed; the colony
failed. **Every cause was a thing the agent could not see, or had to
remember**, and the four fixes the run itself named are filed as specs. Three
of them are this round. Re-running M1 before they land would spend ten in-game
days reproducing a known result.

  - Captain bled for 11,993 ticks and died. His health was read four times
    while he was dying and `BloodLoss` was absent from all four.
  - The only pawn with Doctor enabled was the first casualty.
  - The run made 27 advances and ZERO `journal` calls. Table's downing was
    announced inside an advance result and the run advanced again.

## Item 0 first, and it is short: VERIFY TWO PREMISES

Neither of these is a judgement call and both are cheap. Do them before writing
any code, and comment the result on the issue whichever way it goes.

**0a. `61794cd` claims the 20-row hediff cap cut `BloodLoss`. That premise may
already be false.** `PawnSerializer.Rank` bands bleeding FIRST
(`BleedRate > 0.0001f` -> 4, then life-threatening, then tendable) and
`Capped(..., "urgency-desc")` uses it — and `git log -S'BleedRate > 0.0001f'`
puts that in `c85189d`, dated 2026-08-30, which is BEFORE the M1 run of
2026-08-31. So either the cap was not the mechanism and the post-mortem's
diagnosis is wrong, or something else truncated the row. `RUNS/m1-20260831/`
has the actual envelopes; Captain's four reads are in there with
`hediffs_more` of 7, 16, 19, 19. Find out which read dropped it and why.
**If the cap is innocent, say so on the issue and narrow its scope to the
clock** — do not "fix" a working sort to satisfy a sentence.

**0b. `20e5cda` (3.5 dialog + interaction verbs) is `state:doing`, and the
reason is one line in its own last comment: "No verb was run against a bench."**
The code shipped, the acceptance suite was repaired (nine broken checks, eight
of them computed-value sites that were silently green asserting nothing), and
nobody has run it. `accept/3.5-dialog-verbs.py` is the POSIX twin. Run it. It
is an M1 blocker — once 1.7 halts an advance on a modal, something has to
answer the modal — and it costs a bench run you are making anyway.

## Then, in this order, because they feed each other

**1. `61794cd` — the bleed-out clock.** The second half of that issue is
unambiguously missing: `grep -rn ticks_until_bleedout Source/` returns nothing.
The decision the run actually faced at tick 231,968 was "is there time to walk
a rescuer 118 cells" — roughly a 2,810-tick walk against a ~9,040-tick clock —
and **nothing published either number**. Publish the GAME's number, not our
arithmetic: `Verse/HealthUtility.TicksUntilDeathDueToBloodLoss(Pawn)` is the
member, and `RimWorld/HealthCardUtility` is the game's own readout calling it
beside `BleedingRate`. That is the trailhead, not the answer — read both before
you decide where it lands in the pawn surface and what it reads for a pawn who
is not bleeding.

**2. `40ed42f` — doctor coverage, and it takes the clock as input.** Coverage
is computable, so the mod should compute it: `work_coverage` in the digest with
Doctor's floor at 2, plus the roster-change repair. `grep -rn work_coverage
Source/` returns nothing today.

  **These two issues share an undecided boundary and ONE worker must settle
  it.** `61794cd` says, in its own words: "Consider publishing it alongside a
  travel estimate for the nearest capable rescuer, since the comparison is the
  whole decision — but that may belong with the triage procedure (40ed42f)
  instead. Decide and say which." Decide it once, in one place, and record the
  decision on both issues. Splitting it across two workers is how it ends up in
  neither or in both.

**3. `722c951` — `advance` refuses while the journal has an unread delta.**
This is the one that makes the other two REACH the agent: a read surface nobody
reads is worth nothing, and 27 advances against zero journal calls is what that
looks like. Both halves of the issue are open — `grep -rniE "unread" Source/`
finds nothing resembling a gate, and `TimeDriver` has no downing/death auto-halt
either.

  **Mind the blast radius, and it is the reason this is third rather than
  first.** A refusal to advance changes every acceptance suite in `accept/` that
  advances without reading first, and this round's own suites are among them.
  Land it knowing that; do not land it and then discover it. If the refusal
  needs an explicit opt-out for a suite that genuinely does not care, design the
  opt-out as part of the spec rather than bolting it on when the first suite
  goes red — and say plainly on the issue what an opt-out costs, because the
  whole point is a discipline that cannot be forgotten.

## What is new since M1, and you are expected to use it

Session 19 removed the habit that made a ten-day run unbearable. Do not
reintroduce it in this round's acceptance suites.

  - `advance {until:{layout:"ly-N"}}` halts when every element of a
    `place-layout` transaction is built or cancelled. On a timeout it names each
    unresolved element AND its state.
  - `advance {until:{condition:{path,op,value,edge?}}}` over the digest's own
    field set — `{"path":"time.hour","op":">=","value":6}` is "advance until
    dawn". EDGE-required by default; `hour >= 6` is true all afternoon.
    `until.every_frames` is the cadence, and every advance publishes
    `until.eval_ms_per_frame`.
  - `construction {layout_id}` answers for one transaction, with `done`,
    `built`, `cancelled`, `unresolved` uncapped.
  - `place-layout`'s bill now separates `available` (reachable, unforbidden,
    by the builder's own test) from `in_stockpiles`, and a short row says which
    of forbidden / unreachable / genuinely-absent it is.

**A tick count in an acceptance suite is now a defect unless you can say why no
predicate expresses the wait.** `accept/fc287ba-until-state.py` is the worked
example; its phase 4 waits for colonists to build a room without naming a
number.

## Not in this round

`b1b3060` (the posture verb) is the fourth M1 gate item and is deliberately
held: it is the biggest new surface of the four and it is wave:3 while these
are wave:2. `039e359` (the four branches session 19's own suite never reaches)
is real and is not urgent. `bac4eba` (tiling) and `261f2e9` (temperature) are
the base-composition path and are a different round. Do not start M1's re-run:
it needs `b1b3060` too.

## Rules

Your own git worktree — disjoint files are not enough, a shared HEAD is the
hazard (session 4). You may launch `_RimWorld-Agent` and nothing else; it is the
single carve-out to the workspace's never-launch rule. `profile/show-bench.sh`
puts it on workspace 3, `Prefs.xml` to 2560x1600 fullscreen for a watched run,
and restore `Prefs.xml.bak-640x480` after. `dotnet build -c Release`; `Build:`
commits stand alone and the rebuild that ships happens on `main` so the pdb path
names the canonical worktree, not a branch worktree that is about to be deleted.
Resolve ambiguity by investigation against
`/home/dorian/projects/rimworld-tools/Info/decompiled/RimWorldBase/`, cited by
file + member, never line numbers; a resolution that would change another spec's
contract is reported BLOCKED. Observers never mutate. Assert key EXISTENCE in
acceptance — `eq(..., None)` passes on an absent key. A suite's green number is
an instrument reading, not a fact: verify headline claims from raw envelopes,
and never read an exit code through a pipe. Commit after each numbered item so a
rate limit costs nothing.

**And check before you build.** Session 19 listed four M1 blockers as open and
one of them was half-shipped; Dorian caught it by asking whether they had been
done and not closed. Grep the source for the thing an issue says is missing
before you write it, and say on the issue what you found.

## Done

`work_coverage` and a bleed-out clock are in the observation surface and a
replay of Captain's four M1 reads shows both present; `advance` refuses to run
past an unread journal delta and halts on an own-faction downing; the 3.5 suite
has been run against a live bench and is closed or its failures are filed; and
every acceptance suite this round adds waits on a predicate rather than a
guessed number of ticks.
