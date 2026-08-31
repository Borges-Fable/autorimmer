# HANDOFF — 2026-08-31, BORGES → next machine

Where to find things, and what is actually outstanding. Written at a hard stop
(usage limit), so this is deliberately short and points rather than explains.

## Read these, in this order

1. **`git-bug bug show fbb2c59`** — the session-9 **run ledger**. Body is current
   state, comments are events. **This is the freshest and most complete record**;
   two BORGES orchestrators appended to it today. Start here, not with RUNLOG.
2. **`git-bug bug show 01f0b85`** — the muster. Process law and per-session
   handovers.
3. **`RUNLOG.md`** — accurate through **session 6**. Sessions 7–9 are partly
   written; **today's later work is NOT in it.** Do not treat its tail as current.
   The ledger above supersedes it.
4. `DESIGN.md` — outranks any spec body that disagrees. Its decisions log grew a
   lot today (modal hazards, write-on-SAVE, verb ownership, the tick model).

## Unmerged branches, all pushed

| branch | state |
|---|---|
| `spec/3.5-dialog-verbs` | verbs COMPLETE (dialog, letters, trade, comms, quests + quest log). Acceptance script **partial and never run**. Also edits `DESIGN.md` — conflicts with `d180f57`. |
| `spec/3.6-bills-storage` | bills + storage + `WorldSafe.RecipeAvailableNow` COMPLETE. **No acceptance script.** Modifies `MedicalBillVerbs.cs` (3.4's shipped file). |
| `spec/4.2-play-loop` | complete; `playbook/PLAY-LOOP.md` + a Python auditor never run (no Python on BORGES). |

None compiled by an orchestrator. None run. **None merged — that is deliberate.**

## AUDIT BEFORE MERGING ANY OF IT

Evan's standing instruction. Full text is on the ledger and on each issue:

- **Prose audit** (4.1's playbook/checklists, 4.2's loop) — on `96d9315` / `d2e1229`.
- **Code audit** (3.5, 3.6) — on `20e5cda` / `48f666c`. **One agent each.**

Reason, in one line: a very large amount was authored very fast today, and the
project was bitten twice in one day by claims that were plausible, well-cited
and wrong (the "unset growing zone grows nothing" lesson; a modal-hazard grep
that searched the wrong symbol). Prose and uncompiled code both fail silently.

## Outstanding work

1. **Acceptance, unrun** — `accept/4087644-order-honesty.md` phases 5–6 (1–4
   passed on this bench earlier and are unaffected), then
   `accept/70ac258-things-stable-order.md` in full. Close `4087644`, `bc2250b`,
   `70ac258` **only if green**; all three are `state:doing` deliberately.
2. Finish and run 3.5's and 3.6's acceptance scripts.
3. `DESIGN.md` decisions-log entry for **PawnSafe Class I** (a vanilla helper
   that takes its key input from ambient state); suggested text is in
   `PawnSafe.cs`'s Class I block.
4. **`RUNLOG.md` session section is unwritten.**
5. Prune stale worktrees under `.claude/worktrees/`.

Carried debt, five sessions old and now more urgent: factor the four private
`Act` emitters into one helper — `4087644` just rewrote much of that surface.

## Machine facts

- **No Python on BORGES.** Acceptance is driven by hand from the tables;
  `accept/3.4-pawn-orders.ps1`'s `Send-Cmd`/`Dig`/`Eq`/`Ge` helpers are the
  working raw-protocol driver to reuse.
- Decompiled 1.6 source: `misc/rimworld/reference/decompiled/RimWorldBase/`.
  **Verify by member name — line offsets differ from dorian's tree.**
- Bench is `_RimWorld-Agent`. Saves here: `journal-accept`, `kit-accept`,
  `pawn-accept`, `Autosave-1..5`. **No `autostart.rws`** — that fixture is
  dorian's box. A bench may still be running; `run-agent.ps1` refuses a second.
- Treat every acceptance check as unexecuted code. Session 9's lesson:
  `eq(…, None)` passes just as happily on an absent key as on a null one.

## Two flags worth carrying

- `move-to` reported `accepted:1` for a pawn already walking to that exact cell.
  Now gated `already-doing-it`; check 6.13 is its only proof and **has never run**.
- The `ordered` field name invites misreading — a `wear` order reads
  `ordered:false`, because the triple requires a WorkGiver and a direct order has
  none. Not renamed (3.4 asserts on it by name). Cheapest fix suggested:
  publish `order_kind = work|direct|null` alongside it.
