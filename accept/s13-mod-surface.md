# `s13-mod-surface` — acceptance for the five session-13 mod findings

Proves M1 findings **A, J, K, I1** and Evan's **D** ruling against a live bench.
Protocol, helpers and exit codes follow `accept/3.5-dialog-verbs.py`; the runner's
own header carries the phase list and the transport notes.

    ./accept/s13-mod-surface.py             # bench sweep
    ./accept/s13-mod-surface.py --selftest  # phase 9 only, NO bench needed
    ./accept/s13-mod-surface.py --phase 3   # one phase (0 always runs)

**Run `--selftest` before taking it to a bench.** Phase 9 runs the real
assertion helpers over canned envelopes — correct ones and deliberately broken
ones — and fails if a broken one passes. It asserts nothing about the mod, and
says so.

## Result — bench `20260901T121508`, assembly `1.0.0+ad29d6d`

**169 PASS · 0 FAIL · 1 SKIP.** Banked at `accept/runs/s13-20260901/`.

The skip is `2.6a`, and it is CORRECT rather than a gap to paper over: a 3x2
`HiTechResearchBench` could not be sited, because `find-rect` approves a rect on
its own cells while a workbench also needs its **interaction spot** — a cell
outside the footprint — and on a mountainous map that cell was granite. A retry
loop over more candidates was written and deliberately DISCARDED: it would have
hidden the finding. Filed separately as the siting/survey work.

## Two things this suite is built to resist

**A wrong dig path must not go green.** `eq(dig(resp, "a.b.c"), None)` passes on
an absent key, so phase 0 asserts SHAPE — that each key exists at the path a
later phase will read — before any phase asserts a value. Phase 0 is 57 checks
for that reason, and it also asserts keys that must be ABSENT: the four lazy
getters `WorldSafe.Site` deliberately skips, and the position/def that
`threat-pardon`'s candidate listing must never leak.

**A refusal must not be reported as a fact about the map.** Two failures during
development came from digging a value out of an envelope without checking `ok`
first: a `bad-args` refusal became "find-rect found no buildable cell near the
map centre", which is a claim nothing measured. Check `ok`, then dig.

## Known fixture dependencies

- **Prerequisite chains are read, not hardcoded.** `BENCH_PROJECT_PREREQ` is a
  hint; the runner clears the chain by reading `prerequisites` off each
  `blocked_by:"prerequisites"` refusal. A hardcoded name failed against a stock
  1.6 map (`MoisturePump` needs `Machining`, not `MicroelectronicsBasics`).
- Phases 6 and 7 (pardon set across a save/load round trip) are opt-in: 6 stops
  and asks a human to save and load, 7 is meaningless until that has happened.
- The pardon of a genuinely DORMANT hostile is still unproven — a `--quicktest`
  map has no sleeping cluster and one could not be constructed on it.
