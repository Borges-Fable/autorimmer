# M1 in parallel, on BORGES — read this before starting

Evan wants a **second colony running M1 concurrently with dorian's box**, on a
different seed. This file is BORGES's brief. It is not a copy of
`M1-PROMPT.md`; read that one too — it is the actual procedure, and everything
in it applies unless contradicted here.

**Why parallel is worth doing.** One ten-day run is an anecdote. Two runs on
independent seeds, on different machines, with the same procedure, is the first
evidence this platform has ever had about *variance* — which failures are the
colony's luck and which are the loop's. It also exercises the cross-machine path
that has cost two sessions an hour each.

---

## TWO BLOCKERS. Read both before you launch anything.

### BLOCKER 1 — BORGES has no python, and `rwa` is python

`rwa/rwa` is stdlib-python3-only. BORGES has no python (Store stub only) — that
constraint is why `accept/*.ps1` exists at all. So today BORGES **cannot run the
CLI**, and that is not cosmetic:

- `playbook/PLAY-LOOP.md` **invariant 1** is "everything through `rwa`; never a
  direct write into the protocol root."
- The **transcript** (`transcripts/<RWA_RUN>/`) is written BY `rwa`. No CLI, no
  transcript.
- `accept/4.2-play-loop.py` audits `--transcript` and cross-checks per-op counts
  against the journal's own `action`/`dev` record. **No transcript, no audit** —
  and the audit is 4.3's acceptance, not a nicety.

Three ways out, with a recommendation:

| option | cost | verdict |
|---|---|---|
| **Install python 3 on BORGES** | minutes | **RECOMMENDED.** `rwa` then works unchanged, transcripts land in the same format, and the two runs are directly comparable — which is the entire point of running in parallel. |
| Write a pwsh twin of `rwa` | days, and permanent | A second implementation that must stay byte-compatible on transcript format forever. This project's recurring failure is two artifacts drifting apart silently; do not volunteer another pair. |
| Drive the raw file protocol from pwsh | hours | Breaks invariant 1, produces no transcript, cannot be audited. The run would not count. Do not. |

**Do not start the run until this is resolved.** A ten-day run that cannot be
audited is a ten-day run that proves nothing, and you will not know until the end.

### BLOCKER 2 — the loop currently forbids BORGES from launching

`playbook/PLAY-LOOP.md` line 43, the `down` verdict: *"start the bench via
`run-agent.sh` … On a machine whose rules forbid the agent launching anything
(BORGES), escalate instead."*

That rule is written down and you must not simply ignore it. **Evan is lifting it
for this run** — he asked for a parallel game on BORGES, which cannot happen
otherwise. Whoever starts the run should:

1. Confirm the lift with Evan in one line (it is his rule to lift, and the
   written text still says the opposite).
2. **Edit `PLAY-LOOP.md` line 43 in the same commit as the first run**, so the
   file and reality agree. A rule everyone knows is stale is worse than no rule —
   this repo has burned three sessions on exactly that shape.
3. Launch ONLY `_RimWorld-Agent`, via `profile/run-agent.ps1`. Never
   `_RimWorld-Testing`, never the MP install. That carve-out does not widen.

---

## THE HAZARD THAT PARALLEL RUNS CREATE, and it will bite

**Two machines both writing git-bug comments WILL diverge the bug store, and a
diverged pair is exactly what breaks it.**

Session 11 spent real time on this: `git-bug pull` produced **five unreadable
merge commits**, and every git-bug command panicked with `DFS failed` until each
ref was reset to a parent. The decisive detail — **only the merges were broken;
every parent read fine** — and the five affected bugs were precisely the ones
where both sides had diverged. One of those bad merges came FROM BORGES, so this
is not a dorian-box quirk. See `16b959a` (closed, with the full diagnosis and
every preserved sha).

Running two colonies means two sessions filing findings. Without a rule, you will
reproduce the breakage on day one.

**The rule for this round:**

- **BORGES does not write to git-bug during the run.** Findings go into
  `RUNS/<run>/summary.md` and the post-mortem. Dorian's box files the issues
  afterwards, from both reports.
- If BORGES must write, then before every `git-bug pull` and after it, run
  `git-bug bug > /dev/null 2>&1` and check the exit code. **That one command is
  the whole test.** If it fails, STOP and escalate — do not push a broken store.
- **Never `git push --force` a bug ref without first parking the old value**, e.g.
  `git push origin <oldsha>:refs/backup/<what>/<id>`. That is what made session
  11's repair non-destructive.
- Pull `main` before starting and push nothing to it during the run except your
  own `RUNS/` directory.

---

## What differs on BORGES, mechanically

- **Decompiled 1.6 lives at `misc/rimworld/reference/decompiled/RimWorldBase/`**,
  not the `rimworld-tools` path in `M1-PROMPT.md`. **Line offsets differ between
  the two trees — verify by MEMBER NAME, never by line number.**
- Launcher is `profile/run-agent.ps1`; the profile maker is
  `profile/make-profile-agent.ps1`.
- pwsh yes, python no (see Blocker 1).
- `profile/show-bench.sh` is a Hyprland script and is **dorian's box only** — it
  does not apply here. Put the window wherever Evan can see it by whatever means
  BORGES has.

## Keeping the two runs apart

- **Distinct run names.** Dorian's box uses `m1-<yyyymmdd>`; BORGES uses
  `m1-borges-<yyyymmdd>`. Set `RWA_RUN` before the first probe or the transcript
  is incomplete.
- **Distinct `RUNS/` directories** follow from that. They must not collide.
- **Do not copy the other machine's seed.** The point is two independent worlds.
  Record the seed and the landing tile's biome in your summary so the two runs
  can be compared honestly.
- Each run gets its own post-mortem. **Compare them only after both are done** —
  reading the other run's outcome first contaminates your triage, the same way
  2.5's legibility read disqualifies a reader who has seen the answer key.

## Everything else

`M1-PROMPT.md` is the brief: the fixture (`--quicktest` = Crashlanded +
Cassandra Rough + 3 colonists + 250x250, verified at source), the hard pass/fail
criteria (>=2 of 3 alive; the FULL draft -> fight -> undraft with 0 drafted at
the end; zero unexplained red errors), the advance cap, the wedge rule, the
no-`dev:*`-after-staging invariant, the deliverables, and the trade defects you
must not trust. Read it whole.

**And spend five minutes on `git log` before believing any of this.** Both this
file and `M1-PROMPT.md` were written on 2026-08-31 and will go stale. Taking a
handover at face value is the exact failure these rounds exist to catch.
