# Session start — the load order (4.2's contract)

The ordered list a play session loads before its first act. Positions are
named so the acceptance can be mechanical: a post-mortem's outputs surface at
the positions given at the bottom, or the post-mortem is not finished.

Files first (what the colony has learned), then reads (what the colony is):

1. `playbook/INDEX.md` — the recall surface. Lesson BODIES are drill-down:
   open a lesson when its "bites when" matches what the session is about to
   do, not all nine up front. The digest's own budget philosophy, applied to
   memory.
2. `checklists/turn.md` — into working context whole. It annotates every
   digest the loop will read; it is the one file that must never be
   drill-down.
3. `checklists/triggered.md` — the trigger table into working context; item
   bodies re-read on firing. On a NEW colony, its colony-start section runs
   now, top to bottom, before the first advance.
4. `checklists/daily.md` — the capped sweep; runs at each day boundary.
5. `templates/INDEX.md` — names and purposes only. A template's IR and notes
   load at build time.
6. `rwa status` — bench alive, right session, no stale inbox.
7. `landmark` — the names plans are written in (`freezer`, `base-center`);
   re-register any the templates expect that a wipe or new map lost.
8. `digest` — the first read; turn.md applies to it immediately.
9. `journal {since_seq: <last known>}` — what happened while nobody watched.
   If the previous session ended in a loss: run `postmortem.md` (repo root)
   BEFORE acting on the new colony — its outputs are for the next colony, and
   acting first means acting unlearned.
10. `RUNS/<colony>.colony.md` — the colony notes: intents, half-done
    projects, landmark rationale, decisions deferred. Found via the newest
    `RUNS/<run>/summary.md`, which names it (and carries position 9's
    `<last known>` seq); absent on a fresh colony — created at first
    session end. State and intent only, never lessons
    (`playbook/PLAY-LOOP.md` §Artifacts). *(Added by 4.2 — the loop's
    between-session state.)*

`RUNS/<run>/checklist.ndjson` is APPENDED from the first evaluation on —
position 2's trip-wires can fire on the very first digest.

## Where a post-mortem's outputs surface next session

| output | lands in | surfaces at position |
|---|---|---|
| prose lesson | `playbook/<name>.md` + one INDEX line | 1 |
| checklist item | `checklists/{turn,triggered,daily}.md` by moment | 2 / 3 / 4 |
| template patch | `templates/<room>.{ir.json,md}` | 5 (body at build time) |
| mod-rung promotion | a spec issue, not a playbook file | n/a — cite the issue id in the lesson it replaced |

Nothing else is loaded eagerly. `postmortem.md` is a procedure, not context;
lesson bodies, template bodies and the decompiled source are all
drill-on-demand. A session that starts by reading everything has already spent
the attention the checklist budget exists to protect.
