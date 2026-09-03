# openrun-20260902 — audit

An audit input for the session that will rewrite the filed issues into plans. It is
evidence, not a plan. It files nothing, edits nothing, and closes nothing.

## Read in this order

| file | what it is | size |
|---|---|---|
| **`themes.md`** | **19 themes: cause, occurrences, cost, confirmation, COVERED/PARTIAL/UNFILED.** The file to reason over. | 8.6k words |
| `issues.md` | Every open issue in `autorimmer` (76) and `seekandkill` (8), bucketed by area, with a closed-issue appendix | 84 open |
| `tables.md` | The Step-1 arithmetic — op census, failure taxonomy, time budget, the journals | 613 lines |
| `findings.md` | All 360 Pass-A findings, slice-ordered, undeduplicated | 360 |
| `spine.ndjson` | The merged timeline: 6,674 transcript steps + 7,039 journal rows, UTC-ordered | 13,713 rows |

---

## ⚠ Before you write a single issue body

**`git-bug` permanently drops any line beginning with `#` from a body passed via `-F`.**
Silently, at commit time. Your entire job is writing plans full of markdown headings.

Every `# Heading`, `## Section`, `### Subsection` will vanish and you will not be told.
Use `-m` with a quoted heredoc, or indent headings, or use a different heading style, and
**read the issue back with `git-bug bug show <id>` before you move on.**

Two more `git-bug` hazards this run actually hit:

- **Shell backtick substitution ate a line** of an issue body mid-filing, producing
  garbled text that needed a repair edit (F-S02-9). Single-quote your heredocs.
- **`git-bug bug | head -1` grabbed the wrong bug.** The agent retitled and relabelled
  `3fa4cf5` — an existing **closed** spec issue — before catching it and reverting
  (F-S03-7). `git-bug bug new` prints the id; capture that.

---

## Step 0 — the harness session logs exist

**Yes, and they cover the whole window.** Four JSONL transcripts in
`~/.claude/projects/-home-dorian-projects-rimworld-autorimmer/`, contiguous with small
overlaps. Local time is EDT (UTC−4) throughout this audit, matching the transcript `ts`
field.

| session | local span | lines | assistant prose |
|---|---|---:|---:|
| `fea0608c-6821-4f5d-b8f8-d9c8e890d7fc` | Sep 02 13:49 → 19:16 | 4,104 | 97 KB |
| `eb9a93ab-6a59-472e-b54a-6d17dfa6ee95` | Sep 02 19:11 → 22:29 | 3,603 | 121 KB |
| `61360a58-65ba-42eb-af23-60b177f51e52` | Sep 02 21:26 → Sep 03 11:53 | 4,316 | 95 KB |
| `276caf13-1071-450d-bc5f-137d3dc8a004` | Sep 03 11:53 → 17:16 | 3,637 | 57 KB |

15,660 lines, ~27 MB raw, ~370 KB of assistant prose. `276caf13` carries
`session_0125Xp6g5h7Ac47j5vbV3vjD`, the id given in the task, confirming the mapping.
No gaps between 13:49 and 15:47.

**Two caveats that shape every "why" claim downstream:**

1. **No extended thinking was recorded** — `thinking_chars = 0` in all four. What survives
   is the agent's *visible narration to Dorian*: an account addressed to an audience, not
   private reasoning. Findings are therefore intentional as well as behavioural, with the
   standing qualifier that a stated reason is a claim about intent, not proof of it.
2. **Prose density is inverted against importance.** 121 KB for a 3.3-hour session versus
   57 KB for the 5.4-hour final one. The stretch containing both fatal raids and the wipe
   is the *thinnest* per hour, so slice S12 leans hardest on the transcript and journal.

**A third axis-specific fact:** the harness log is the **only** place the agent's own
failures are recorded. Five `API Error` events (three `529 Overloaded`, two mid-response
truncations) appear in no `log.ndjson` row, no envelope, and no journal row. Two of them
land inside the 21.8-minute window when Dorian took over the game by hand.

---

## How this was built

**Step 1 — quantitative, no subagents.** 6,658 `log.ndjson` rows and 7,039 journal rows,
by script. Output is `tables.md` verbatim.

**Step 2 — the spine.** `spine.ndjson`, 13,713 rows, UTC-ordered, journal row before
transcript row on a tie. Join keys, in the order the task specified — with a correction:

| key | rows joined | note |
|---|---:|---|
| `journal_seq` echoed in the result | 1,022 (15%) | **The task brief overstates this.** Only `advance` (659/659) and `cancel-layout` (6/6) *always* echo it. `build` 316/525, `place-layout` 19/37, `construction` 17/85. **`designate`, `zone`, `storage-set`, `area`, `prioritize`, `orders`, `draft`, `wear`, `equip` and `posture` echo it never.** |
| `sid` from `result.json` | 6,648 | Not in `meta.json`, not in `log.ndjson` — the task was right about this |
| `ts`/`wall` proximity | 5,652 (85%) | Everything else |

**Step 3 — Pass A.** Twelve contiguous, overlapping slices, sized by command density and
cut at natural boundaries. Each went to a `sonnet` subagent with the slice's spine rows, a
compact digest of them, and the harness prose for the same window — **and no issue list**,
deliberately. Readers could open any `result.json` on demand.

| slice | local window | findings |
|---|---|---:|
| S01 | Sep 02 13:49–15:05 · first contact with the verb surface | 29 |
| S02 | Sep 02 15:00–17:05 · first base plan, the 53-min gap | 20 |
| S03 | Sep 02 17:00–18:20 · farms, the 81-call `orders` burst, Aaron dies | 27 |
| S04 | Sep 02 18:10–19:40 · Kelsey and Fitz die; SUCCESSOR-PROMPT written | 31 |
| S05 | Sep 02 19:30–21:15 · winter, power, Shiro; manhunter pack | 28 |
| S06 | Sep 02 21:05–22:40 · the "triple death"; production hall | 27 |
| S07 | Sep 02 22:30–Sep 03 00:25 · takeover session; the overnight stop | 32 |
| S08 | Sep 03 08:35–09:35 · resume and relaunch; the conduit sweep | 25 |
| S09 | Sep 03 09:25–10:55 · six colonists die; Tanya joins and dies | 25 |
| S10 | Sep 03 10:45–12:25 · the `RWA_RUN` break; first two mech raids | 24 |
| S11 | Sep 03 12:15–14:00 · cultist raid, the trade, the seekandkill NRE | 26 |
| S12 | Sep 03 13:50–15:48 · the last two raids, and the wipe | 27 |
| **XC** | **whole run — the reduction step's own cross-cutting findings** | **39** |
| | | **360** |

**Every slice returned findings.** Coverage is complete: all 6,674 transcript steps fall
inside at least one slice.

**Step 4 — Pass B.** Join, dedupe, match. Only then were the issues loaded.

---

## Corrections to the inherited material

Three figures in the task brief and `AUDIT-INPUT.md` do not survive checking. They are
stated first in `tables.md` §0 because everything downstream would otherwise repeat them.

1. **6,674 steps, not 6,688.** Each transcript directory holds `meta.json` and
   `log.ndjson` beside the step directories. `ls | wc -l` returns 1001 where `find -type d`
   returns 999 — 2 phantom steps per directory, 14 across seven.
2. **Two run-window journals, not three.** `20260902T002505` runs Sep 01 20:25 → Sep 02
   01:27 local and ends **12h25m before the run's first command**. It belongs to
   `m1-20260901`. No `result.json` in any of the seven directories carries that sid.
   Following the brief would have merged 2,224 rows from a different colony into this
   timeline.
3. **6,658 `log.ndjson` rows**, not 6,688: 6,674 steps − 26 that never reached the log +
   10 client-only `rwa:rotate` rows with no step directory.

`AUDIT-INPUT.md` was right, and prominently so, about the thing that mattered most — the
`RWA_RUN` break and the 27% a glob drops. It is a participant's account and reads as one;
its §2 work-block table is wall-clock contiguity and was used for slicing only, never for
counting, exactly as the task warned.

**A fourth correction, to this audit's own first draft:** `tables.md` §7b originally
reported 23 colonist deaths. Three of them are debug-menu residue and one is a repeat.
**The corrected figure is 20 death rows and 19 distinct colonists.** See `themes.md` T13.

---

## Where this audit is weakest

Read these before trusting any number in it.

1. **The run is a co-op and only one player is in the record.** Dorian intervened in game
   state at least 22 times with no provenance anywhere. **This is the single largest
   threat to every quantitative claim here.** Two colonists joined and/or died entirely
   outside the harness. `themes.md` T14.
2. **"Cost" is mostly UNKNOWN and that is honest.** Of 360 findings, a minority carry a
   measured cost. Where a reader wrote UNKNOWN it means the counterfactual is not in the
   record — not that the cost was small. Do not read absence of a number as absence of a
   cost.
3. **Findings are single-slice by construction.** A reader saw 60–120 minutes and could
   not see that its "one-off" recurred five times elsewhere. The `F-XC-*` block exists to
   cover that, but it is one pass by one model and will have missed things twelve
   independent readers would not have.
4. **One briefing error, caught by the reader.** I told S08 the conduit sweep was in its
   window. It is not — journal seq 2226 is at 22:53 local on Sep 02, inside **S07**, whose
   reader covered it correctly (F-S07-5). S08's reader verified the mismatch and flagged
   it rather than confabulating. Treat that as a sample of the briefing quality, not an
   isolated slip: other slice briefings may carry similar errors that were silently
   absorbed rather than flagged.
5. **The XC block is not blind to the issues.** Slice readers were. I was not — I had read
   `HANDOFF.md`, `checklist.ndjson` and the run contract before writing F-XC-*. Those 39
   findings are more likely than the slice findings to rediscover what is already filed.
6. **The verb census counts calls, not capability.** "38 verbs never used" says nothing
   about whether they work.
7. **Confirmation status is per-theme, not per-finding.** Where a theme says CONFIRMED it
   means its central claim is corroborated on two axes; individual occurrences within it
   may be single-axis.
8. **Nothing here was re-run.** Every claim is read from the record. No verb was called, no
   bench launched, no issue touched.

---

## Reproducing or extending this

The working files are in `.work/` (gitignored): per-slice spines, digests, harness
extracts, the reader brief, the per-slice findings, and the issue dumps. The scripts that
built `tables.md` and `spine.ndjson` are there too.

Paths that are not obvious:

- **Journals** live at the protocol root, *not* in the run directory:
  `_RimWorld-Agent/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/AutoRimmer/journal/<sid>.ndjson`.
  `RUNS/openrun-20260902/journal/` is empty and always was.
- **Transcripts** are gitignored and local only: `transcripts/openrun-20260902*` **and**
  `transcripts/20260903T123633*` — both prefixes, or you drop 27%.
- **Issues** are git refs under `refs/bugs/<64-hex>`, not worktree files.
  `git-bug bug --status open`, `git-bug bug show <id> --format json`.
