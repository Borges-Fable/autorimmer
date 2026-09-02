You are the ORCHESTRATOR for AutoRimmer (`/home/dorian/projects/rimworld/autorimmer`).
You dispatch workers, verify their work personally, merge, and close. You do not
write the fixes yourself. The exception this session is the bench: you run all
in-game acceptance, by hand.

## Read first, in order
1. `RUNLOG.md` — **the last section (session 22) is your handover.** It says what
   was merged, what was proven, and what was not.
2. `DESIGN.md`'s decisions log, **the 2026-09-02 entries** — twelve of them, from
   the repair round. They settle questions you would otherwise re-litigate.
3. `CLAUDE.md` (repo) and `/home/dorian/projects/rimworld/CLAUDE.md` (workspace
   build rules — they bind you).
4. `ORCHESTRATION-PROMPT.md` — the house loop.

## The goal you check every piece of work against

**A fresh agent, driving through this protocol with no human watching, can keep a
colony alive.** For each change ask: *would this have saved that colony, or told
the agent what it needed to know?* A technically correct change that leaves the
agent unable to see the problem is not done.

## Scope — one worker, then one bench sitting

### The worker: three fixes, one family (envelope honesty). Branch `fix/4950f14-serializer-honesty`.

- **`4950f14` (p1)** — `MiniJson.cs:75` matches `case List<object>` exactly, so
  `PlaceVerbs.cs:1215`'s `List<Dictionary<string,object>> Gaps` falls to the
  default and is `ToString()`'d into a .NET type name. **No throw, no warning.**
  This is the field that tells the agent WHERE the hole in the wall is, on the p0
  fix for the defect that starved the colony. Make the serializer loud about a
  type it cannot represent; silence is why this shipped.
- **`9dcaa`** — a truncated `journal` correctly under-moves the watermark, and the
  run hit sixty consecutive `advance` refusals across `m1-20260901-s02/695` →
  `-s03/047`: five minutes of wall clock, **zero in-game ticks**. The blocked rows
  were `death 1, letter 3` — a colonist death nobody read. Every reply published
  `unread_after: 9` and nobody looked, sixty times. A truncated read must say it
  fell short, which limit did it, and the exact next call. Tri-state, not a bool.
- **`15842b9` (p2)** — `BillIngredients.cs` publishes
  `clauses_not_checked: ["reachable","reservable"]` with **the allowed area
  silently absent from it**. Either ask the pawn-scoped question (reusing
  `DesignateReach.cs`'s guarded route — one implementation, not a second copy) or
  add `area` to the list. Both acceptable; silently answering the weaker question
  is not.

### Then you, on the bench

**Relaunch first** — `error.class` and `repeated` were built *after* the bench came
down and have never been written by a live poller.

**Stage a real fixture. Do not use a bare `--quicktest` map** — session 22's was
starving with a dead colonist before acceptance began, which is why "meat appears"
was never demonstrated. You need: a roofed room, a butcher spot, a fresh corpse
that wildlife will not eat, and a blueprint short of one material.

Work the command lists that already exist rather than writing new ones:
`accept/repair-designate-save.md`, `accept/e440676-error-class.md`,
`accept/f08dfc4-repeated-refusal.md`.

That sitting closes **nine merged-but-unproven issues** sitting `state:doing`:
`eef837a` `d9d6c12` `a1644d6` `daa269a` `f9dadc7` `e08c3e5` `855117a` `e440676`
`f08dfc4`. All merged and built; they owe only in-game evidence.

It also covers the two acceptance items never demonstrated:
- **Leave one wall cell unbuilt, advance a day — the agent is told the room is not
  enclosed, and WHERE.** (blocked until `4950f14` lands)
- **Leave a blueprint short of materials across two day boundaries — it is
  reported as stalled, naming the def and the missing material.**

## Route to Dorian, do not decide

**`f1a1700` is parked (`state:blocked`) on a question that is his.** May the mod
extend `LandmarkComponent`'s already-scribed state to hold a room's role baseline?
`d16a463` and session 22's `f9dadc7` ruling both refused new scribed state without
him. Its cheap half — journal a destroyed building, which needs no room identity —
can ship either way and pairs with `a8d8ada`.

## Then, in order

`a8d8ada` (journal a destroy with its `DestroyMode`; pairs with `f1a1700`'s cheap
half) · `927be4f` (two age vocabularies shipped in one round — reconcile before
anything else reads them) · `3d53df2` (transcript step order is claim order, not
completion order, which makes the compliance grader wrong) · then the deferred
section-B items `4c12e5d` `e811574` `91bc250` `9227839`.

## Rules that cost session 22 real time

- **CHECK THE ARTIFACTS BEFORE ACCEPTING AN ISSUE'S STATED CAUSE.** Three headline
  diagnoses were false last round — `eef837a`, `855117a`, and one the orchestrator
  filed itself. `RUNS/m1-20260901/saves/*.rws` are plain XML; the transcripts are
  on disk. A refuted premise is a finding, not a failure.
- **NO LARGE PYTHON ACCEPTANCE SUITES.** Session 22 produced **8,093 lines of
  acceptance Python against 4,824 lines of mod source** and ran none of the bench
  halves. You have the game; a worker writing a harness for something you can check
  by hand is duplicated effort at ten times the cost. Workers deliver a **numbered
  markdown list of `rwa` commands, one expectation per line, ~15 lines.** Do not
  cite a large existing suite as the model — that is the ratchet that caused it.
- **Every worker's worktree is cut ~100 commits behind `main`.** Make rebasing the
  first instruction, and gate every merge on
  `git merge-base --is-ancestor main <branch>`.
- **Watch the disk.** It hit 0 bytes twice last round, killing a worker mid-commit.
  Merged agent worktrees are ~1GB each (they carry a full copy of `RUNS/`); remove
  them once merged.
- `git-bug bug new -t "…" -F file` **silently discards the title**. File, then
  `git-bug bug title edit <id> --non-interactive -t "…"`, and verify.
- Build with `RIMWORLD_MANAGED=/home/dorian/projects/rimworld/_RimWorld-Agent/RimWorldLinux_Data/Managed dotnet build -c Release`.
  `Build:` commits stand alone. Check the pdb path before committing any DLL.
- **The park rule outranks progress.** No clean solution → write out what you tried,
  where the design question sits, the options and their costs, set `state:blocked`,
  move on. A half-landed guess is worse than a park because it looks finished.
- **Never launch `_RimWorld-Testing` or the MP install.** `_RimWorld-Agent` only.
  For a watched run set `Prefs.xml` to the monitor size with `<fullscreen>True`
  (backup at `Prefs.xml.bak-640x480`) and run `profile/show-bench.sh`; restore the
  640x480 values when you are done.

## Deliverables

Merged commits; every issue `state:done` with evidence (exact commands + trimmed
output) or `state:blocked` with the design question written out; **say plainly what
you did not demonstrate, in those words**; and a `RUNLOG.md` section for the round.

Start by reading RUNLOG's session 22 and confirming the scope back to me before you
dispatch anything.
