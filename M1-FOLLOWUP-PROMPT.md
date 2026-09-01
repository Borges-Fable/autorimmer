AutoRimmer session 13 — close out M1's loose ends, then orchestrate.

Session 12 ran M1 (`664e9b9`) on dorian's box. The colony died; the platform
did not. This is the clean-up brief. It has two halves and they are in order:

1. **VERIFY** every claim below. Do not take it at face value.
2. **ORCHESTRATE** a round over what survives verification.

=== READ FIRST, in this order ===
1. `RUNS/m1-20260831/summary.md` — what the run did and what it found.
2. `playbook/PLAY-LOOP.md` — the procedure session 12 was audited against.
3. `git-bug bug show 664e9b9` — comments #4 and #5 are Evan's two resolutions.
4. `RUNLOG.md` Session 11 — the last session with a written section. **There is
   no Session 12 section. Writing it is one of your jobs.**
5. `postmortem.md` — the procedure. Two colonists died; it is mandatory and it
   has NOT been run.

=== STATE, AS OF THIS BRIEF ===
`main` = `cc76e11`, tree clean, **1 commit unpushed** — push early so BORGES is
not stranded. No worktrees, no agent branches. NO BUILD OWED
(last `Build:` `2a31cb9` at `cd9f390`; zero compiler-visible `.cs` change since —
re-check with the workspace CLAUDE.md's diff command rather than believing this).
Bench is CLOSED. `_RimWorld-Agent` is the only install anyone may ever launch.

Run artifacts: `RUNS/m1-20260831/` (checklist.ndjson 53 lines, digests day-1..5
+ final, saves/Autosave-1..5.rws, summary.md, two Player.log captures),
`transcripts/m1-20260831/` (195 ops), journal
`<protocol-root>/journal/20260901T022324.ndjson` (175 events, **0 red errors**).

=== THE ONE THING THAT IS ALREADY PROVEN, SO DO NOT RE-PROVE IT ===
The auditor has now run against a real run for the first time in the project's
history. Reproduce it in one command before you touch anything else:

    R="/home/dorian/projects/rimworld/_RimWorld-Agent/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/AutoRimmer"
    python3 accept/4.2-play-loop.py RUNS/m1-20260831 \
        --journal "$R/journal/20260901T022324.ndjson" \
        --transcript transcripts/m1-20260831

Expected: 3 PASS, 2 WARN, 5 FAIL, non-zero exit. **Do not pipe it to `tail`** —
session 12 did and reported `EXIT=0`, which was tail's status, not the auditor's.

=== THE LOOSE ENDS — VERIFY EACH, THEN RULE IT ===

Each is stated with the evidence session 12 had. Your first job is to decide
whether the claim holds, whether it is a defect and whose, and at which rung it
belongs (`postmortem.md`'s ladder). Several are genuinely arguable. Where the
resolution changes another spec's contract, report BLOCKED rather than deciding.

**A. `dev:spawn-thing` returns `ok:true` with `placed:0`.**
Journal seq 66: `SimpleResearchBench x0 (WoodLog) @ 123,117`, `placed: 0`,
`ids: []` — yet the envelope was `ok:true` and carried `at:[123,117]`, which is
the TARGET cell, not proof of placement. The bench never existed;
`Alert_NeedResearchBench` never cleared and research could not progress for the
whole run. Session 12 misread it by printing `at` instead of `placed`, which is
the human half of the same defect. Verify against `DevVerbs.SpawnThing`'s
`failures` list and decide: should a zero-placement spawn be `ok:false`, or is
the caller obliged to read `placed`? This is the single most consequential
finding of the run — file it wherever it belongs.

**B. `advance` overshoots its own cap.** Auditor FAIL: `132-advance`
`ticks_elapsed 60021` against `timeout_ticks: 60000`. The envelope publishes
`overshoot: 1` and `overshoot_bound: 30`, so the driver appears to know it
overshoots. Decide which is wrong — `TimeDriver`'s cap arithmetic, or
`accept/4.2-play-loop.py`'s check — and cite the source either way. If the fix
is in the mod it needs `dotnet build -c Release` from `Source/AutoRimmer/` and a
standalone `Build:` commit; check the pdb path before committing any DLL.

**C. The auditor mis-counts composite verbs.** FAIL:
`dev:spawn-thing: journal 46 > transcript 39`. The difference is exactly 7 — the
`survival` preset's 7 items. `dev:starter-kit` calls `DevVerbs.SpawnThing`
internally and journals each, so a composite verb inflates the journal count with
no matching transcript op. The check's "journal count <= transcript count per op"
assumption does not hold. Note `StarterKit` already publishes `caused_seqs` /
`caused_journal_seqs` for exactly this join — the fix may be to use it.

**D. `final-undrafted` cannot pass on a map with a hive.** FAIL:
`threats.hostiles == 6 at end`. The 0-drafted half PASSED. Those six (4
megascarab, locust, spelopede) were map-generated, fogged from tick 0, and never
engaged; `pawns {filter:"hostile"}` read 0 all run. As written the check makes a
clean exit impossible unless the colony hunts a hive it cannot see, which
`turn.md`'s fog caveat explicitly forbids. Evan's resolution on `664e9b9`
(comment #5) says the final digest must read "0 drafted, 0 hostiles" — so
changing this touches a RESOLVED acceptance criterion. **Do not decide it
alone; this is the one to raise with Evan.**

**E. Three ledger ids exist in no checklist.** FAIL `item-ids-known`:
`barracks-heat`, `postmortem-trigger`, `time-control-drift`. Session 12 invented
all three for real observations that had nowhere to go: a room overheating, a
death requiring a post-mortem, and losing time control. The ledger schema has no
home for a run-level incident. Decide whether these become items (mind
`daily.md`'s cap of 7, currently 4, and 4.4's merge-or-retire rule), or whether
the schema needs an incident class.

**F. Silent skips — session 12's own compliance failure.** FAIL
`daily-coverage`: day 1 missed all four daily items (no sweep was run at all;
colony-start ran instead), and day 4 missed three while the deaths were being
handled. This is an `execution-slip` in `postmortem.md`'s taxonomy — a 4.2
compliance finding, not a new artifact. Record it; do not invent a checklist
item to paper over it.

**G. An `rwa advance` whose CLIENT dies leaves the game RUNNING.**
`rwa pause` reported `was_advancing:true, speed_before:Ultrafast` after a lost
tool result. Corroborated by the auditor's two WARNs — `136-advance` and
`187-advance` have no `cmd.json`, because those are the calls whose client died.
Consequence: ~60,000 ticks (a full in-game day) elapsed unobserved between the
day-3 read and the next advance, and it happened more than once. Session 12
first blamed a stray keypress on the watched window and **corrected that** in the
ledger; the corrected cause is client death. Mitigation adopted mid-run: read
`status.paused` before every advance. Decide whether the mod should also stop on
client disappearance, and whether `PLAY-LOOP.md` needs the pre-advance check as
an invariant.

**H. `seek-at-will` echoes a field that no longer decides anything.**
Its `before`/`after` blocks report `hostility_response: "Flee"` even when seek is
ON. Verified: SeekAndKill's `ThinkTreeInjector` inserts
`ThinkNode_ConditionalSeekAndKill -> JobGiver_SquadSeek` ABOVE
`ThinkNode_ConditionalColonist`, so with seek on the vanilla flee node is never
reached. The field is truthful and the reading is misleading. Related and
already fixed in the playbook, not the code: `[[seek-off-is-a-decision-to-flee]]`.

**I. Smaller, each verified once and none re-checked:**
- No observer surfaces **biome or tile**. 4.3 asks for a temperate fixture and it
  had to be inferred from `map-view` terrain glyphs. A `grep -rn 'Biome'` over
  `Source/AutoRimmer/` returned nothing.
- `rwa pawn --id N` collides with `rwa`'s own `--id` and silently becomes the
  command id, returning `bad-args: missing required arg 'id'`. `--args-json` is
  the workaround. `rwa/README.md` documents `pawn {id:<n>}` with no CLI warning.
- `tend` is **drafted-only** (`FloatMenuOptionProvider.Undrafted` is false for
  it), so the route to an untended dying patient is `work-priorities`, not a
  verb. Costly to discover mid-emergency.
- `things {category:"weapons"}` returned a **WoodLog rollup**. Unexplained;
  possibly a mod marking wood as a weapon. Not chased.
- **No build verb (3.3, `1adc737`) makes the work surface shallow.**
  `Alert_ColonistsIdle` was up for most of the run. Four dev-staged buildings
  were the entire colony for six days, and a destroyed research bench could not
  be replaced. Comment this on `1adc737` and on `664e9b9`.

=== WHAT IS OWED AS WORK, NOT AS A RULING ===
- **The post-mortem.** `postmortem.md` in full — two deaths. Its four prose
  lessons are ALREADY written and indexed
  (`quicktest-and-autostart-collide`, `one-doctor-is-zero-doctors`,
  `read-every-return-or-lose-a-colonist`, `seek-off-is-a-decision-to-flee`);
  what is missing is the procedure itself — timeline of harm, the backward walk,
  root-cause classification per its table, the wealth check, and verifying each
  output surfaces at its `SESSION-START.md` position.
- **`RUNLOG.md` Session 12 section.**
- **git-bug is empty of all of this.** `664e9b9` still has the same 6 comments it
  had before the run. Owed: the escalation for a colony-ending event (PLAY-LOOP
  §Escalation), a comment per finding on the issue it belongs to, and at least
  one new issue for the quicktest/autostart collision. **Do all git-bug writes
  SERIALLY from one place** — session 11's bug store broke on concurrent merges
  (`16b959a`), and every bug there was one git-bug itself had merged.
- **Push.**
- **Bench hygiene, needs a standing decision:** `autostart.rws` is parked in
  `Saves/pre-m1/` alongside the five pre-run autosaves. Restoring it re-breaks
  `--quicktest` deterministically — that is finding
  `[[quicktest-and-autostart-collide]]`, root-caused to `Root_Entry` and
  `Root_Play` racing on `Root.checkedAutostartSaveFile` with a scene-targeted
  long event. Either leave it parked and say so in the bench docs, or restore it
  and retire `--quicktest`.

=== THE TWO THINGS ONLY EVAN CAN ANSWER ===
Ask AT MOST ONE PER MESSAGE, and only when the answer changes what gets done.
1. **Does `664e9b9` close?** The run failed its hard criterion — 2 of 3 dead on
   day 4, stopped day 6 of 10 at Evan's call. Either it closes as evidence, or
   M1 re-runs on a fresh seed with the four fixes the run produced: an
   `Area_Allowed` bounding where colonists work, `seek-at-will` ON as a standing
   posture, `assign {hostility:"Attack"}`, and **two** doctors.
2. **D above** — relaxing `hostiles == 0` touches a criterion Evan already
   resolved in writing.

=== ORCHESTRATION ===
Once the findings are verified and filed as ruled issues, run the round. Use the
`orchestrate` skill. Constraints that outrank convenience:
- **Every parallel worker gets its own git worktree.** Disjoint files are not
  enough — a shared HEAD is the hazard (session 4).
- Workers never launch anything. The orchestrator runs all in-game acceptance
  personally, on `_RimWorld-Agent` only.
- Natural split, by the file each owns: `accept/4.2-play-loop.py` (C and D
  together — same file), `Source/AutoRimmer/` (A and B — both need a build, so
  one worker, one `Build:` commit), `checklists/*.md` (E), `playbook/` +
  `RUNLOG.md` (post-mortem, F, H). git-bug stays with the orchestrator.
- Specs are contracts: the Acceptance section is the definition of done.
- Ambiguity is RESOLVED by investigation against the decompiled source
  (`/home/dorian/projects/rimworld-tools/Info/decompiled/RimWorldBase/`, cite
  file + member, never line numbers), not queued for Evan.

=== THE WARNING THIS BRIEF INCLUDES ABOUT ITSELF ===
Session 10 dispatched eight agents against a stale list. Session 11 caught a
stale bench that would have made every measurement a phantom. Session 12 wrote
this list. Spend five minutes on `git log` and the auditor's own output checking
what it claims is outstanding but is already done — including this handover.
