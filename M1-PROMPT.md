AutoRimmer session 12 — M1: PLAY. Ten in-game days, agent-driven.

GOAL: run 4.3 (`664e9b9`), the survive-10-days demo. This is the ONLY job. No
feature work. If you find yourself specifying new behaviour, you have left the
round — file it and carry on playing.

Session 11 spent itself proving the platform. Everything M1 depends on has now
been RUN, not merely written. Your job is to use it.

=== READ FIRST, in this order ===
1. `playbook/PLAY-LOOP.md` — 336 lines, load-whole. This IS the procedure. It
   gives the loop its verbs, halt handling, cadence, artifacts and its ten
   auditable invariants. You will be audited against it.
2. `playbook/SESSION-START.md` — the ordered load list, position by position,
   no reordering.
3. `git-bug bug show 664e9b9` — 4.3. **Read the COMMENTS, not the body**; the
   body's dependency line is stale and comments #1–#5 amend it. #4 and #5 are
   Evan's two resolutions and neither is open.
4. `git-bug bug show 65b03c2` — session 11's ledger. Body is state, comments
   are events; the last two comments are the close-out.
5. `RUNLOG.md`'s Session 11 section.
6. `DESIGN.md` — outranks any spec body that disagrees.

=== STATE ===
`main` = `ba4e978`, pushed, tree clean, no worktrees, no agent branches, NO
BUILD OWED (last `Build:` `2a31cb9` at `cd9f390`; zero compiler-visible `.cs`
change since). The git-bug store is healthy on BOTH machines — session 11 fixed
the unreadable-merge breakage and verified it from a fresh clone.

=== WHAT IS PROVEN, SO YOU DO NOT RE-PROVE IT ===
Five suites finished GREEN against a live bench, zero red errors in every run:
3.4 pawn orders 159/159 · 4087644 order honesty 100/100 · 8b0b88f designate
guard 123/123 · 70ac258 things order 99/99 · 3.6 bills+storage 116/116. 3.5 is
89/0 through quests/letters/dialogs and 101/102 in trade.

**Dialog routing works.** That was the blocker that made M1 impossible: since
1.7 an advance halts `reason:"dialog"` under any force-pausing modal and ~54
vanilla windows force-pause, so without it every advance after the first halted
at 0 ticks. It is proven. A ten-day run WILL hit timed letters; you can answer
them.

**`accept/4.2-play-loop.py --selftest` PASSED** on this box (2026-08-31). Its
doc said not to trust the auditor until it did. That caveat is discharged — the
auditor is trustworthy.

**Hauling works, and `unforbid` → pickup is proven.** Measured: a parka spawned
forbidden at `[126,120]`, unforbid, 3000 ticks, hauled to a stockpile at
`[136,134]` with the flag cleared.

=== THE FIXTURE ===
Launch: `_RimWorld-Agent/run-agent.sh --quicktest`. Verified at source
(`Verse/Root_Play.SetupForQuickTestPlay`) to give EXACTLY what 4.3 asks for:
`ScenarioDefOf.Crashlanded` (3 colonists), **Cassandra on Rough**, a random
seeded world at 250x250, random starting tile.

- **The tile is RANDOM, so the biome is a coin flip.** 4.3 wants temperate. If
  you land on ice sheet or extreme desert, RELAUNCH to reroll rather than
  playing a fixture the spec did not ask for — and say in the run report how
  many rerolls it took.
- **One map-generation failure was seen on 2026-08-31** ("error generating a
  map", the `GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap` path).
  Evan's call: relaunch and ignore it unless it recurs. If it recurs, THEN dig.
- Then stage with dev verbs, all journaled: minimal shelter, starter kit, sane
  skills. `dev:starter-kit` now lands its gear FORBIDDEN by design (`091e3f0`).
- **PLACE THE KIT AWAY FROM STORAGE if you want the unforbid rehearsal to mean
  anything.** On the old bench save the kit landed INSIDE a 144-cell stockpile,
  so nothing ever needed hauling and the rehearsal was vacuous. `dev:spawn-thing`
  takes `pos` (NOT `at` — that arg is silently ignored) and an opt-in `forbid`.

=== THE RULES YOU WILL BE AUDITED ON ===
- **Everything through `rwa`.** `./rwa/rwa <op> --arg value`. Do NOT write into
  the protocol root directly — that is invariant 1, and it is also how the
  transcript gets written. `export RWA_RUN=m1-<yyyymmdd>` BEFORE the first
  probe, or the transcript is incomplete and the audit fails.
- **NO `dev:*` after staging.** The journal is the proof. If play seems to need
  a god-hand, that is a verb gap: file it, log the checklist line `blocked`,
  route around. Never cheat past it.
- **Advance cap 60,000 ticks (one in-game day), every advance.** Default
  `advance --until.letter true --timeout_ticks 60000`. `halt_on_error` stays on.
  Never raise `max_tps`.
- **Pre-advance undraft gate.** Before EVERY advance: if any colonist is drafted
  and `threats.hostiles` is 0 with no live response, `undraft` first.
- **The wedge rule.** Two consecutive advances returning `ticks_elapsed: 0` END
  THE SESSION and escalate. Never retry a 0-tick advance unchanged.
- **Every checklist evaluation writes a ledger line** — `ok`, `action`,
  `blocked` (with issue id) or `n/a`. A silent skip is a compliance failure and
  the auditor finds it by diff.

=== HARD PASS/FAIL — not report lines ===
- **>=2 of 3 colonists alive at day 10** without post-staging dev intervention.
- **The FULL draft -> fight -> undraft cycle, with a completed undraft.**
  Evan resolved this explicitly (comment #5 on 664e9b9): a drafted colonist does
  not eat, sleep or work and NO ALERT NAMES IT, so a forgotten undraft is a
  slow-motion wipe. It compounds — `SeekRegistry.ShouldSeek` requires
  `!pawn.Drafted`, so the colonist also drops out of the seek posture. The final
  digest must read 0 drafted, 0 hostiles.
- **Zero unexplained red errors**, each triaged as ours / mod-under-test /
  vanilla.
- Handle at minimum: food, one raid, one medical event, mood, and the clothes
  check firing at least once. If Cassandra sends no raid, `dev:incident` it
  during STAGING at a scheduled tick and journal the cheat — that is sanctioned
  and provable.

=== DELIVERABLES ===
`RUNS/m1-<date>/` — `checklist.ndjson`, `digests/day-<N>.json` for every day,
`digests/final.json`, `summary.md`, plus the save and the transcript under
`transcripts/<RWA_RUN>/`. Then a post-mortem regardless of outcome, >=1 new
playbook lesson, and spec-gap findings filed as comments on the issues they
belong to.

**Then run the auditor against that run** — it has only ever seen a synthetic
fixture, because no `RUNS/` has ever existed:

    python3 accept/4.2-play-loop.py RUNS/<run> \
        --journal <protocol-root>/journal/<sid>.ndjson \
        --transcript transcripts/<run>

That run is what closes `d2e1229`, and it is 4.3's own acceptance too.

=== KNOWN HAZARDS, ALREADY PAID FOR ===
- `acee526` (1.9, p1) — placement is NOT exact-or-refuse; things slide silently,
  and the proposed fix route DELETES buildings in the target cell. You stage with
  dev verbs, so this can bite. Do NOT attempt the fix mid-run; journal it and
  work around it.
- `0d9cbd7` — world-fixture steps do not chain. Address every target by an id
  read back from the previous step.
- `be75bc4` / `7e8c969` — three real defects on the TRADE surface, filed and
  unfixed. If the colony trades: a duplicate line silently collapses, an
  over-large order is silently clamped and reported as full success, and
  `data.after` does not see goods it just bought. **Do not trust a trade echo;
  verify with an independent `things` read.**
- The bench has 33 mods. A red error may be a mod under test, not ours — triage
  says which, and that is a deliverable, not an excuse.

=== MACHINE FACTS (dorian's Linux box) ===
- **Put the game on workspace 3 and take Evan there: `./profile/show-bench.sh
  --wait 60`.** Do this after EVERY launch. It is his standing preference and he
  asked for it again this session. Prefs are already 1920x1200 fullscreen, so it
  will not show the tiny-viewport problem.
- Build (if you ever need one): `dotnet build -c Release` from `Source/AutoRimmer/`,
  NOT the repo root. `Build:` commits stand alone.
- Python 3.14. NO pwsh — `accept/*.ps1` cannot run here; every suite has a `.py`.
- Commit sha in a DLL: `grep -aoE '[0-9]+\.[0-9]+\.[0-9]+\+[0-9a-f]{40}'` on raw
  bytes. **An ASCII grep CANNOT see a .NET string literal** — they live in the
  UTF-16 `#US` heap; count as UTF-16LE or you will "prove" a present string absent.
- `_RimWorld-Agent` is the ONLY install anyone may launch, ever.
- Decompiled 1.6: `/home/dorian/projects/rimworld-tools/Info/decompiled/RimWorldBase/`.
  CITE FILE + MEMBER, never line numbers.

=== RULES THAT OUTRANK CONVENIENCE ===
- Observers never mutate. `PawnSafe.cs` / `WorldSafe.cs` hold the guarded routes.
  Note `orders` is NOT read-only despite looking it — see its header comment.
- The gate lives in the widget: every player verb cites its precondition
  (file + member). `dev:*` may bypass.
- Ambiguity is RESOLVED by investigation against the decompiled source, not
  queued for Evan. Ask him AT MOST ONE QUESTION PER MESSAGE, and only when the
  answer changes what gets done.
- No Claude branding in commits, merges or issue text.
- **Before dispatching or assuming anything, spend five minutes on `git log`
  checking what this brief claims is outstanding but is already done.** Session
  10 dispatched eight agents against a stale list; session 11 caught a stale
  bench that would have made every measurement a phantom. Taking a handover at
  face value is the exact failure these rounds exist to catch — including this
  handover.

=== HARD GATE ===
Evan signs off after reviewing the run. STOP there. An escalation is a
deliverable, not a failure — a wedged run imitating a live one is the failure.
