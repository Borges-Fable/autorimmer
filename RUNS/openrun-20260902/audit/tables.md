# openrun-20260902 — audit tables (Step 1, quantitative)

Mechanically derived from `transcripts/<dir>/log.ndjson`, the step directories, and
the two bench journals. No interpretation; every number here is a count. Local time
is EDT (UTC−4) throughout, matching the transcript `ts` field.

## 0. Corrections to the brief and to AUDIT-INPUT.md

Three inherited figures do not survive checking. They are stated here first because
everything downstream would otherwise repeat them.

| claim | source | actual | why |
|---|---|---|---|
| 6,688 steps | AUDIT-INPUT §1, task brief | **6,674 steps** | Each transcript dir holds `meta.json` and `log.ndjson` alongside the step dirs. `ls \| wc -l` counts 1001; `find -type d` counts 999. 2 × 7 dirs = 14 phantom steps. |
| three substantive journals | AUDIT-INPUT §3, task brief | **two** | `20260902T002505` runs 2026-09-01 20:25 → 2026-09-02 01:27 local and ends **12h25m before the run's first command**. It belongs to the preceding `m1-20260901` run. Confirmed by `.sid` in every `result.json`: no step in any of the seven directories carries it. |
| 6,688 log rows of efficiency data | task brief | **6,658 log rows** | 6,674 steps − 26 steps that never reached `log.ndjson` (the orphans, §5) + 10 client-only `rwa:rotate` rows that have no step dir. |

The two journals that do cover the run:

| sid | rows | local span | tick span |
|---|---:|---|---|
| `20260902T175211` | 3,200 | Sep 02 13:52:11 → Sep 03 00:23:07 | 0 → 3,605,358 |
| `20260903T123633` | 3,839 | Sep 03 08:36:33 → Sep 03 15:47:08 | loaded at 3,605,358 → 10,569,958 |

They join at tick 3,605,358 exactly — the second launch loads the save the first left.
The run is one continuous colony: `session/newgame` at tick 0 (Sep 02 13:52), wipe at
tick 10,569,958. **176.2 in-game days.** 7,039 journal rows total.

Which sid each transcript directory ran against:

| dir | steps | sid(s) |
|---|---:|---|
| `openrun-20260902` | 999 | `20260902T175211`×996 |
| `openrun-20260902-s01` | 999 | `20260902T175211`×994 |
| `openrun-20260902-s02` | 999 | `20260902T175211`×994 |
| `openrun-20260902-s03` | 999 | `20260903T123633`×603, `20260902T175211`×386 |
| `openrun-20260902-s04` | 872 | `20260903T123633`×866 |
| `20260903T123633` | 999 | `20260903T123633`×999 |
| `20260903T123633-s01` | 807 | `20260903T123633`×804 |

`openrun-20260902-s03` straddles the relaunch: steps 001–391 on the first bench,
392–999 on the second. **A directory edge is not a bench edge either.**

## 1. Time budget

| bucket | hours | share |
|---|---:|---:|
| wall-clock span (first → last command) | 25.90 | 100% |
| bench command time (sum of `elapsed_s`) | 5.02 | 19.4% |
| &nbsp;&nbsp;· of which `advance` | 3.49 | 13.5% |
| &nbsp;&nbsp;· of which everything else | 1.53 | 5.9% |
| idle gaps > 5 min | 20.79 | 80.3% |
| gaps < 5 min (model thinking + harness) | 21.41 | 82.7% |

**The poll floor.** 5,937 non-`advance` commands, `elapsed_s` min 0.10 / median
0.91 / p95 1.38 / max 2.22; 99.7% land under 1.5 s.
The distribution is a spike at the ~1 Hz file-bridge poll interval, not a work
distribution — a `digest` and a 42×34 `map-dump` cost the same. Those 5,937 calls
spent **1.53 h** almost entirely waiting for the next poll tick. Batching reads
is worth roughly that hour; making any individual read faster is worth nothing.

Idle gaps over 5 minutes, all nine:

| minutes | after | → | local |
|---:|---|---|---|
| 494.3 | `openrun-20260902-s03/391-save` | `openrun-20260902-s03/392-pause` | 09-03 00:22:50 → 08:37:08 |
| 53.5 | `openrun-20260902/326-map-view` | `openrun-20260902/327-things` | 09-02 15:55:26 → 16:48:55 |
| 21.8 | `openrun-20260902-s04/321-things` | `openrun-20260902-s04/322-zones` | 09-03 10:24:08 → 10:45:53 |
| 12.9 | `openrun-20260902/031-research` | `openrun-20260902/032-research` | 09-02 14:10:13 → 14:23:05 |
| 10.7 | `openrun-20260902/325-map-dump` | `openrun-20260902/326-map-view` | 09-02 15:44:45 → 15:55:26 |
| 9.5 | `20260903T123633-s01/041-things` | `20260903T123633-s01/042-things` | 09-03 14:05:06 → 14:14:38 |
| 8.7 | `openrun-20260902/323-map-dump` | `openrun-20260902/324-cancel-layout` | 09-02 15:34:23 → 15:43:05 |
| 6.9 | `openrun-20260902-s02/346-things` | `openrun-20260902-s02/347-pawns` | 09-02 21:29:47 → 21:36:39 |
| 5.7 | `openrun-20260902-s02/699-research` | `openrun-20260902-s02/700-digest` | 09-02 22:24:36 → 22:30:19 |

The 494-minute gap is the overnight break. The 53.5-minute one at Sep 02 15:55 →
16:48 sits between a `map-view` and a `things`, mid-build.

## 2. Op frequency, failure rate, and wall time

All 6,658 logged commands. `source: mod` = the game answered; `source: rwa` = the
client failed before the game saw it.

| op | n | fail | fail% | mod fail | rwa fail | total s | median s | max s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `journal` | 903 | 1 | 0.1% | 0 | 1 | 534.3 | 0.60 | 1.0 |
| `things` | 820 | 42 | 5.1% | 42 | 0 | 808.3 | 0.91 | 2.2 |
| `advance` | 711 | 52 | 7.3% | 49 | 3 | 12550.1 | 8.72 | 150.8 |
| `build` | 544 | 19 | 3.5% | 18 | 1 | 516.7 | 0.91 | 1.4 |
| `pawn` | 506 | 19 | 3.8% | 19 | 0 | 497.3 | 0.91 | 1.4 |
| `digest` | 424 | 5 | 1.2% | 5 | 0 | 413.4 | 0.91 | 1.4 |
| `pawns` | 294 | 1 | 0.3% | 1 | 0 | 282.2 | 0.91 | 1.4 |
| `research` | 224 | 21 | 9.4% | 21 | 0 | 212.9 | 0.91 | 1.4 |
| `orders` | 163 | 1 | 0.6% | 1 | 0 | 157.0 | 0.91 | 1.4 |
| `prioritize` | 138 | 17 | 12.3% | 17 | 0 | 139.4 | 0.91 | 1.4 |
| `nearest` | 135 | 0 | 0.0% | 0 | 0 | 131.6 | 0.91 | 1.4 |
| `work-priorities` | 131 | 9 | 6.9% | 9 | 0 | 139.9 | 1.00 | 1.4 |
| `zone` | 95 | 3 | 3.2% | 3 | 0 | 92.6 | 0.91 | 1.4 |
| `designate` | 93 | 2 | 2.2% | 2 | 0 | 94.8 | 0.91 | 1.4 |
| `construction` | 86 | 1 | 1.2% | 1 | 0 | 88.2 | 1.00 | 1.4 |
| `save` | 82 | 1 | 1.2% | 1 | 0 | 98.9 | 1.08 | 1.7 |
| `bills` | 78 | 0 | 0.0% | 0 | 0 | 75.5 | 0.91 | 1.4 |
| `map-dump` | 71 | 28 | 39.4% | 28 | 0 | 73.0 | 1.00 | 1.4 |
| `zones` | 64 | 0 | 0.0% | 0 | 0 | 63.8 | 0.91 | 1.4 |
| `triage` | 60 | 0 | 0.0% | 0 | 0 | 58.1 | 0.91 | 1.4 |
| `bill-add` | 44 | 5 | 11.4% | 5 | 0 | 44.7 | 1.00 | 1.4 |
| `place-layout` | 40 | 3 | 7.5% | 3 | 0 | 41.7 | 1.00 | 1.4 |
| `bill-set` | 40 | 8 | 20.0% | 8 | 0 | 39.2 | 0.91 | 1.4 |
| `posture` | 37 | 5 | 13.5% | 5 | 0 | 38.2 | 1.00 | 1.4 |
| `schedule` | 37 | 6 | 16.2% | 6 | 0 | 35.3 | 0.91 | 1.4 |
| `fires` | 35 | 0 | 0.0% | 0 | 0 | 35.8 | 1.00 | 1.4 |
| `room-at` | 35 | 0 | 0.0% | 0 | 0 | 33.8 | 0.91 | 1.4 |
| `extinguish` | 34 | 6 | 17.6% | 6 | 0 | 32.8 | 0.91 | 1.4 |
| `find-rect` | 33 | 4 | 12.1% | 4 | 0 | 35.5 | 1.00 | 1.4 |
| `research-set` | 33 | 3 | 9.1% | 3 | 0 | 35.4 | 1.01 | 1.4 |
| `quest` | 33 | 7 | 21.2% | 6 | 1 | 32.9 | 1.00 | 1.4 |
| `draft` | 31 | 0 | 0.0% | 0 | 0 | 30.5 | 0.91 | 1.4 |
| `wear` | 30 | 2 | 6.7% | 2 | 0 | 28.1 | 0.90 | 1.4 |
| `bill-options` | 30 | 3 | 10.0% | 3 | 0 | 30.9 | 0.95 | 1.4 |
| `equip` | 29 | 3 | 10.3% | 3 | 0 | 28.9 | 1.00 | 1.4 |
| `move-to` | 28 | 1 | 3.6% | 1 | 0 | 27.8 | 0.91 | 1.4 |
| `rescue` | 27 | 1 | 3.7% | 1 | 0 | 26.5 | 0.91 | 1.4 |
| `storage-set` | 27 | 5 | 18.5% | 5 | 0 | 28.5 | 1.00 | 1.4 |
| `quests` | 26 | 0 | 0.0% | 0 | 0 | 26.4 | 1.00 | 1.4 |
| `undraft` | 24 | 0 | 0.0% | 0 | 0 | 23.2 | 0.91 | 1.4 |
| `flick` | 20 | 2 | 10.0% | 2 | 0 | 19.8 | 0.91 | 1.4 |
| `map-view` | 19 | 2 | 10.5% | 2 | 0 | 19.9 | 1.01 | 1.4 |
| `tend` | 19 | 4 | 21.1% | 4 | 0 | 18.8 | 1.00 | 1.4 |
| `landmark` | 18 | 4 | 22.2% | 4 | 0 | 17.6 | 0.91 | 1.4 |
| `rooms` | 17 | 0 | 0.0% | 0 | 0 | 18.5 | 1.00 | 1.4 |
| `unforbid` | 16 | 1 | 6.2% | 1 | 0 | 16.7 | 1.01 | 1.4 |
| `interactions` | 16 | 0 | 0.0% | 0 | 0 | 16.3 | 0.97 | 1.4 |
| `assign` | 15 | 4 | 26.7% | 4 | 0 | 15.8 | 1.00 | 1.4 |
| `bill-remove` | 14 | 0 | 0.0% | 0 | 0 | 15.1 | 1.01 | 1.4 |
| `area` | 13 | 4 | 30.8% | 4 | 0 | 14.5 | 1.01 | 1.4 |
| `alert-mute` | 12 | 1 | 8.3% | 1 | 0 | 13.4 | 1.16 | 1.4 |
| `seek-at-will` | 11 | 2 | 18.2% | 2 | 0 | 11.7 | 1.01 | 1.4 |
| `attack` | 11 | 0 | 0.0% | 0 | 0 | 11.2 | 1.00 | 1.4 |
| `pause` | 10 | 0 | 0.0% | 0 | 0 | 10.5 | 1.00 | 1.4 |
| `areas` | 9 | 0 | 0.0% | 0 | 0 | 9.1 | 0.91 | 1.4 |
| `quest-accept` | 9 | 0 | 0.0% | 0 | 0 | 9.5 | 1.00 | 1.4 |
| `letter-dismiss` | 9 | 0 | 0.0% | 0 | 0 | 8.7 | 0.91 | 1.2 |
| `reachable` | 8 | 1 | 12.5% | 1 | 0 | 8.9 | 1.08 | 1.4 |
| `carry` | 8 | 3 | 37.5% | 3 | 0 | 8.4 | 0.96 | 1.4 |
| `trade` | 8 | 0 | 0.0% | 0 | 0 | 8.7 | 1.08 | 1.4 |
| `consume` | 8 | 2 | 25.0% | 2 | 0 | 7.6 | 0.91 | 1.2 |
| `cancel-layout` | 7 | 1 | 14.3% | 1 | 0 | 7.7 | 1.16 | 1.4 |
| `bill-reorder` | 7 | 1 | 14.3% | 1 | 0 | 7.4 | 1.00 | 1.4 |
| `policies` | 6 | 0 | 0.0% | 0 | 0 | 6.8 | 1.08 | 1.4 |
| `quest-dismiss` | 6 | 1 | 16.7% | 1 | 0 | 6.2 | 1.08 | 1.2 |
| `dev:spawn-thing` | 6 | 3 | 50.0% | 3 | 0 | 5.8 | 1.00 | 1.0 |
| `beat-fire` | 6 | 6 | 100.0% | 6 | 0 | 6.0 | 0.95 | 1.2 |
| `temp-control` | 6 | 0 | 0.0% | 0 | 0 | 6.0 | 1.00 | 1.2 |
| `status` | 5 | 0 | 0.0% | 0 | 0 | 2.6 | 0.50 | 0.7 |
| `trade-start` | 5 | 2 | 40.0% | 2 | 0 | 5.0 | 1.00 | 1.2 |
| `drop` | 4 | 0 | 0.0% | 0 | 0 | 4.4 | 1.16 | 1.2 |
| `comms-targets` | 4 | 0 | 0.0% | 0 | 0 | 4.0 | 0.91 | 1.4 |
| `dialog-dismiss` | 3 | 0 | 0.0% | 0 | 0 | 3.5 | 1.16 | 1.2 |
| `letter-read` | 3 | 3 | 100.0% | 3 | 0 | 3.0 | 0.91 | 1.2 |
| `storage` | 3 | 0 | 0.0% | 0 | 0 | 2.6 | 0.90 | 0.9 |
| `trade-set` | 3 | 1 | 33.3% | 1 | 0 | 3.2 | 0.91 | 1.4 |
| `site-survey` | 2 | 2 | 100.0% | 2 | 0 | 1.9 | 0.95 | 1.0 |
| `work-cover` | 2 | 0 | 0.0% | 0 | 0 | 1.9 | 0.95 | 1.0 |
| `rest-until-healed` | 2 | 0 | 0.0% | 0 | 0 | 2.8 | 1.38 | 1.4 |
| `fire-at-will` | 2 | 0 | 0.0% | 0 | 0 | 1.8 | 0.90 | 0.9 |
| `comms-call` | 2 | 0 | 0.0% | 0 | 0 | 2.0 | 1.01 | 1.0 |
| `comms-hang-up` | 2 | 0 | 0.0% | 0 | 0 | 2.0 | 1.01 | 1.0 |
| `unpause` | 2 | 0 | 0.0% | 0 | 0 | 1.8 | 0.90 | 0.9 |
| `catalog-dump` | 1 | 0 | 0.0% | 0 | 0 | 1.2 | 1.16 | 1.2 |
| `pawn-fixture` | 1 | 0 | 0.0% | 0 | 0 | 1.0 | 1.00 | 1.0 |
| `dev:heal` | 1 | 0 | 0.0% | 0 | 0 | 1.2 | 1.15 | 1.2 |
| `dev:destroy` | 1 | 1 | 100.0% | 1 | 0 | 1.0 | 1.00 | 1.0 |
| `clear-priority-work` | 1 | 0 | 0.0% | 0 | 0 | 1.0 | 1.01 | 1.0 |
| `dev:set-need` | 1 | 0 | 0.0% | 0 | 0 | 1.0 | 1.01 | 1.0 |
| `site-audit` | 1 | 0 | 0.0% | 0 | 0 | 0.9 | 0.91 | 0.9 |
| `power` | 1 | 1 | 100.0% | 1 | 0 | 0.3 | 0.30 | 0.3 |
| `forbid` | 1 | 0 | 0.0% | 0 | 0 | 0.8 | 0.81 | 0.8 |
| `surgery-options` | 1 | 0 | 0.0% | 0 | 0 | 1.0 | 1.00 | 1.0 |
| `surgery-add` | 1 | 0 | 0.0% | 0 | 0 | 1.2 | 1.15 | 1.2 |
| `temp-set` | 1 | 0 | 0.0% | 0 | 0 | 1.0 | 1.00 | 1.0 |
| `comms-choose` | 1 | 1 | 100.0% | 1 | 0 | 1.2 | 1.16 | 1.2 |
| `trade-cancel` | 1 | 0 | 0.0% | 0 | 0 | 1.4 | 1.38 | 1.4 |
| `trade-confirm` | 1 | 0 | 0.0% | 0 | 0 | 0.9 | 0.90 | 0.9 |
| **TOTAL** | **6648** | **337** | **5.1%** | **331** | **6** | **18067.9** | | |

### Ops that failed more often than they worked

| op | n | fail | what the failures were |
|---|---:|---:|---|
| `beat-fire` | 6 | 6 | see §4 |
| `letter-read` | 3 | 3 | see §4 |
| `dev:spawn-thing` | 6 | 3 | see §4 |
| `site-survey` | 2 | 2 | see §4 |

### The ten ops that consumed the most wall time

| op | total s | n | median s |
|---|---:|---:|---:|
| `advance` | 12550.1 | 711 | 8.72 |
| `things` | 808.3 | 820 | 0.91 |
| `journal` | 534.3 | 903 | 0.60 |
| `build` | 516.7 | 544 | 0.91 |
| `pawn` | 497.3 | 506 | 0.91 |
| `digest` | 413.4 | 424 | 0.91 |
| `pawns` | 282.2 | 294 | 0.91 |
| `research` | 212.9 | 224 | 0.91 |
| `orders` | 157.0 | 163 | 0.91 |
| `work-priorities` | 139.9 | 131 | 1.00 |

`advance` is 10.7% of commands and 69.5% of bench time. Nothing else is close, and
no read verb is worth optimising on latency (§1).
## 3. Failure taxonomy — 337 refused commands

| code | n | class | what it means |
|---|---:|---|---|
| `bad-args` | 205 | refused | The agent guessed an argument name, shape or enum value that does not exist. Refused, no state change. |
| `busy` | 80 | flow | A read or write was fired while an `advance` was still in flight. Refused, no state change. |
| `unread-journal` | 40 | refused | An `advance` was refused because the previous one left journal rows unread. The gate working as designed. |
| `rwa-game-down` | 6 | client | `status.json` missing — the command reached the inbox but the bench had stopped answering. |
| `bleedout-deadline` | 5 | refused | An `advance` was refused because a downed pawn would bleed out inside the window. The gate working as designed. |
| `unknown-op` | 1 | refused | The verb does not exist. |
| **TOTAL** | **337** | | |

Two of these are the protocol protecting the agent (`unread-journal`, `bleedout-deadline`),
one is a real outage (`rwa-game-down`), and the other two — **`bad-args` and `busy`,
285 of 337 — are pure loss.**

### 3a. `busy` — the advance-in-flight collision

| op refused | n | example: ticks already done when refused |
|---|---:|---|
| `map-dump` | 27 | 1761 |
| `research` | 21 | 59111 |
| `things` | 17 | 58356 |
| `digest` | 5 | 58182 |
| `advance` | 2 | 3090 |
| `pawn` | 2 | 2688 |
| `research-set` | 2 | 7902 |
| `construction` | 1 | 59253 |
| `pawns` | 1 | 8064 |
| `seek-at-will` | 1 | 27999 |
| `build` | 1 | 7542 |
| **TOTAL** | **80** | |

`map-dump` alone accounts for 27. All 27 fire the identical rect `[82,74,42,34]` —
the new base footprint — during a long `advance`. The single worst instance:
`map-dump {rect:[82,74,42,34]}` was issued **32 times across the run and refused 25 of them**
(§6). The bridge has no queue and no wait: a read during an advance is simply lost,
and the agent has no way to ask "is one in flight" other than by firing and being told.

### 3b. `bad-args` — 205 commands lost to argument guessing

Grouped by what was actually wrong. The mod's error text is unusually good — it names
the legal set nearly every time — which is why almost none of these repeat verbatim.
They repeat *in shape*.

| family | n | representative |
|---|---:|---|
| singular/plural of the same arg | 16 | `tend` — missing required arg 'pawns' (an array of pawn ids or names; 'pawn' takes one) |
| wrong name for a position arg | 19 | `map-view` — map-view needs 'rect' or 'around' |
| wrong name for an identifier arg | 31 | `site-survey` — missing required arg 'def' (string) |
| stale or invisible id | 25 | `bill-set` — no bill with uid 'Bill_Make_SculptureSmall_0' on this bench |
| value outside the enum | 49 | `pawn` — unknown section 'traits' (identity|state|needs|mood|health|skills|apparel|equipment|inventory|schedule|wo |
| wrong scalar type | 10 | `map-view` — arg 'layers' must be an array of strings |
| shape of a nested object | 34 | `work-priorities` — pass 'set' (an array of {pawns|pawn, works|work, priority} blocks — the matrix form) or 'copy_from' with  |
| the game itself says no | 15 | `build` — 'SculptureSmall' is not BuildableByPlayer (Verse/BuildableDef: its designationCategory is null, so no Des |
| other | 6 | `advance` — advance needs 'ticks' or 'until' |
| **TOTAL** | **205** | |

### 3c. The single most repeated `bad-args` details

| n | op | detail |
|---:|---|---|
| 13 | `things` | unknown category 'foods' (food|meds|apparel|weapons|drugs|corpses|chunks|art|plants|beds|buildings|resources|haulable|all) |
| 10 | `things` | no ThingDef named 'Meat_Ostrich' |
| 9 | `build` | build needs 'pos' or 'at' |
| 8 | `pawn` | no visible pawn with id 1018 on the current map (pawns that are unspawned, on another map, or in unexplored ground are not reported) |
| 7 | `work-priorities` | pass 'set' (an array of {pawns|pawn, works|work, priority} blocks — the matrix form) or 'copy_from' with 'to' (the copy-a-whole-row form) or 'manual'  |
| 7 | `pawn` | unknown section 'traits' (identity|state|needs|mood|health|skills|apparel|equipment|inventory|schedule|work|area|relations) |
| 6 | `quest` | missing required arg 'quest' (quest id or name) |
| 5 | `bill-set` | pass index, uid, recipe, or all:true |
| 5 | `prioritize` | pass either 'thing' (a thing id) or 'cell' (a position) — the game's work-giver menu has both forms and they are different orders |
| 5 | `beat-fire` | 'beat-fire' targets a burning PAWN; use `extinguish` for a ground fire |
| 4 | `extinguish` | missing required arg 'at' (a position) |
| 3 | `build` | missing required arg 'def' (string) |
| 3 | `schedule` | missing 'hours' — a span like "0-5", a list like [22,23], one hour, or "all" |
| 3 | `equip` | no visible thing with id 4037 on the current map (things in unexplored ground are not reported) |
| 3 | `prioritize` | no WorkGiverDef named 'Growing' (arg 'work') |
| 3 | `posture` | posture is THREE settings that must agree, and a posture with two of them is the bug this verb exists to remove — pass `area` (an area id or label), o |
| 3 | `letter-read` | missing required arg 'letter' (the letter ID from `interactions`), or 'index' (its position on the stack) |
| 3 | `place-layout` | 1673 elements exceeds the 600 cap. The cap refuses rather than truncating: a truncated layout is a half-built room, which is the state this verb's who |

## 4. Silent fallback — the class Trap 1 belongs to

The bridge accepts a command carrying an argument it does not know, **drops the
argument, runs the verb anyway, and returns `ok: true`.** The only record is a
`warning` row in the journal. If the agent does not read the journal, the wrong
answer is indistinguishable from the right one.

28 events in the run window, 15 verbs:

| local | sid/seq | tick | verb | dropped | consequence |
|---|---|---:|---|---|---|
| 09-02 13:55:14 | `175211`/15 | 1,299 | `things` | 'near' and 'radius' |  |
| 09-02 13:57:11 | `175211`/16 | 1,299 | `things` | 'limit' and 'things_cap' |  |
| 09-02 14:23:04 | `175211`/22 | 1,299 | `research` | 'def' |  |
| 09-02 14:27:01 | `175211`/24 | 1,299 | `zone` | 'label' |  |
| 09-02 14:34:09 | `175211`/39 | 1,299 | `area` | 'label' |  |
| 09-02 14:35:26 | `175211`/43 | 1,299 | `nearest` | 'cap' |  |
| 09-02 14:37:34 | `175211`/53 | 1,299 | `landmark` | 'at' and 'name' | both halves of the landmark |
| 09-02 14:47:50 | `175211`/124 | 58,454 | `bill-set` | 'target_count' | the count the bill was being set to |
| 09-02 16:51:23 | `175211`/306 | 275,890 | `drop` | 'thing' |  |
| 09-02 16:51:24 | `175211`/308 | 275,890 | `drop` | 'thing' |  |
| 09-02 18:26:05 | `175211`/643 | 1,105,245 | `drop` | 'thing' |  |
| 09-02 18:47:48 | `175211`/820 | 1,369,000 | `storage-set` | 'clear' | the clear flag |
| 09-02 18:48:09 | `175211`/823 | 1,369,000 | `storage-set` | 'zzz' |  |
| 09-02 21:13:43 | `175211`/1354 | 2,205,945 | `carry` | 'to' | the destination was dropped; the pawn carried to a default target |
| 09-02 21:13:50 | `175211`/1357 | 2,205,945 | `carry` | 'to' | the destination was dropped; the pawn carried to a default target |
| 09-02 21:39:21 | `175211`/1513 | 2,272,468 | `pawn` | 'hediff_cap' |  |
| 09-02 22:41:09 | `175211`/2022 | 2,846,481 | `research` | 'limit' |  |
| 09-02 22:53:52 | `175211`/2226 | 2,948,675 | `nearest` | 'cap', 'count' and 'limit' | returned its default handful instead of 60; drove the 77-call conduit sweep (§6) |
| 09-02 23:04:07 | `175211`/2321 | 3,016,580 | `catalog-dump` | 'out' | the output path was dropped |
| 09-03 00:10:31 | `175211`/3025 | 3,424,737 | `carry` | 'to' | the destination was dropped; the pawn carried to a default target |
| 09-03 11:54:59 | `123633`/2148 | 6,882,069 | `research` | 'limit' |  |
| 09-03 11:57:58 | `123633`/2149 | 6,882,069 | `nearest` | 'count' |  |
| 09-03 11:58:54 | `123633`/2150 | 6,882,069 | `build` | 'dry-run' | **the hyphen form is not read — a call the agent believed was a dry run placed a real blueprint** |
| 09-03 11:59:29 | `123633`/2151 | 6,882,069 | `research` | 'max' |  |
| 09-03 12:07:11 | `123633`/2169 | 6,882,069 | `zone` | 'filter', 'label' and 'priority' |  |
| 09-03 13:38:37 | `123633`/2909 | 8,488,641 | `trade-start` | 'trader' | the verb's only meaningful argument; the trade opened against a default |
| 09-03 13:44:40 | `123633`/2942 | 8,512,596 | `carry` | 'to' | the destination was dropped; the pawn carried to a default target |
| 09-03 15:01:28 | `123633`/3532 | 10,045,005 | `alert-mute` | 'op' | the operation selector |

By verb: `research`×4, `carry`×4, `nearest`×3, `drop`×3, `things`×2, `zone`×2, `storage-set`×2, `area`×1, `landmark`×1, `bill-set`×1, `pawn`×1, `catalog-dump`×1, `build`×1, `trade-start`×1, `alert-mute`×1.

**Three of these are load-bearing.** `nearest` losing `limit`/`count`/`cap` is the
documented cause of the 77-call conduit sweep. `trade-start` losing `trader` removes
the only argument that identifies who is being traded with. `build` losing `dry-run`
means a call the agent issued to *check* a placement instead *made* one — and the
result envelope said `ok`.

Note the shape of the last: the agent wrote `dry-run`, the verb reads `dry_run`.
Hyphen-vs-underscore is not a typo class the agent can see, because the bridge's own
op names *are* hyphenated (`bill-set`, `map-dump`, `work-priorities`) while its
argument names are not.
## 5. Orphan steps — 26 commands with `cmd.json` and no `result.json`

A command was written to the inbox and the client died, was interrupted, or moved on
before the answer came back. These never reach `log.ndjson` either, so any analysis
driven off the log alone cannot see them at all.

| dir/step | local | args | tick before → after | Δ ticks |
|---|---|---|---|---:|
| `openrun-20260902/029-zone` | 14:08:26 | `{"op": "add", "kind": "stockpile", "rect": [104, 104, 8, 8],` | 1,299 → 1,299 | 0 |
| `openrun-20260902/580-things` | 17:34:43 | `{"def": "RawRice", "detail": true}` | 564,583 → 564,583 | 0 |
| `openrun-20260902/762-journal` | 17:51:57 | `{"since_seq": 494, "limit": 600}` | 711,034 → 711,034 | 0 |
| `openrun-20260902-s01/215-draft` | 18:36:59 | `{"pawns": [12659]}` | 1,210,193 → 1,210,193 | 0 |
| `openrun-20260902-s01/323-pawns` | 18:50:29 | `{"filter": "all"}` | 1,388,720 → 1,388,720 | 0 |
| `openrun-20260902-s01/349-research` | 18:52:42 | `{}` | 1,406,445 → 1,406,445 | 0 |
| `openrun-20260902-s01/533-quests` | 19:27:35 | `{}` | 1,601,544 → 1,601,544 | 0 |
| `openrun-20260902-s02/103-digest` | 21:01:41 | `{}` | 2,175,567 → 2,175,567 | 0 |
| `openrun-20260902-s02/138-research` | 21:07:32 | `{}` | 2,195,306 → 2,195,306 | 0 |
| `openrun-20260902-s02/262-draft` | 21:22:59 | `{"pawn": 1014}` | 2,222,277 → 2,222,277 | 0 |
| `openrun-20260902-s02/264-work-priorities` | 21:23:18 | `{"set": [{"pawn": 1022, "work": "Construction", "priority": ` | 2,222,277 → 2,222,277 | 0 |
| `openrun-20260902-s02/548-advance` | 22:03:11 | `{"until": {"letter": true}, "timeout_ticks": 30000}` | 2,463,833 → 2,473,671 | 9,838 |
| `openrun-20260902-s03/003-catalog-dump` | 23:04:07 | `{"out": "/tmp/claude-1000/-home-dorian-projects-rimworld-aut` | 3,016,580 → 3,016,580 | 0 |
| `openrun-20260902-s03/065-map-dump` | 23:28:15 | `{"rect": [88, 90, 36, 22]}` | 3,119,430 → 3,119,430 | 0 |
| `openrun-20260902-s03/084-things` | 23:34:04 | `{"def": "Blueprint_TableStonecutter", "detail": true}` | 3,119,430 → 3,119,430 | 0 |
| `openrun-20260902-s03/266-things` | 00:04:08 | `{"def": "HemogenPack", "detail": true}` | 3,290,256 → 3,290,256 | 0 |
| `openrun-20260902-s03/390-orders` | 00:22:18 | `{"pawn": 1022, "thing": 21175}` | 3,605,358 → 3,605,358 | 0 |
| `openrun-20260902-s03/441-advance` | 08:41:48 | `{"ticks": 6000}` | 3,640,926 → 3,697,576 | 56,650 |
| `openrun-20260902-s03/502-digest` | 08:48:27 | `{}` | 3,813,415 → 3,824,712 | 11,297 |
| `openrun-20260902-s03/595-journal` | 09:01:38 | `{"limit": 700}` | 4,107,584 → 4,107,584 | 0 |
| `openrun-20260902-s03/633-advance` | 09:09:06 | `{"until": {"letter": true}, "timeout_ticks": 60000}` | 4,262,320 → 4,269,937 | 7,617 |
| `openrun-20260902-s04/673-things` | 11:24:34 | `{"def": "MeleeWeapon_Mace"}` | 6,449,982 → 6,449,982 | 0 |
| `openrun-20260902-s04/706-advance` | 11:30:46 | `{"until": {"letter": true}, "timeout_ticks": 55000}` | 6,538,071 → 6,541,191 | 3,120 |
| `openrun-20260902-s04/710-advance` | 11:31:39 | `{"until": {"letter": true}, "timeout_ticks": 55000}` | 6,541,253 → 6,551,883 | 10,630 |
| `openrun-20260902-s04/714-pawn` | 11:32:15 | `{"id": 31054, "sections": ["equipment", "apparel", "state"]}` | 6,551,883 → 6,551,883 | 0 |
| `20260903T123633-s01/399-advance` | 15:11:21 | `{"until": {"letter": true}, "timeout_ticks": 58000}` | 10,213,013 → 10,220,735 | 7,722 |
| **TOTAL** | | | | **106,874** |

Seven of the 26 moved the clock: **106,874 ticks (1.8 in-game days) advanced with
no result the agent ever saw.** Six of those seven are `advance` calls. The remaining
19 are reads whose answers were simply lost; the agent re-issued most of them
(§6) without knowing the first attempt had happened.

`openrun-20260902-s03/441-advance` is the largest single hole at 56,650 ticks —
almost a full in-game day — and it sits at 08:41:48, four minutes after the run
resumed from the overnight break.

## 6. Repetition

### 6a. Identical `op` + identical `args`, issued 3 or more times

| n | fails | op | args | first → last |
|---:|---:|---|---|---|
| 424 | 5 | `digest` | `{}` | 13:53:44 → 15:47:09 |
| 184 | 9 | `research` | `{}` | 14:09:55 → 15:09:28 |
| 128 | 1 | `pawns` | `{}` | 13:54:57 → 15:43:52 |
| 108 | 7 | `advance` | `{"timeout_ticks": 55000, "until": {"letter": true}}` | 09:18:39 → 15:03:14 |
| 102 | 7 | `advance` | `{"timeout_ticks": 60000, "until": {"letter": true}}` | 14:43:03 → 13:21:22 |
| 98 | 0 | `journal` | `{"limit": 600, "since_seq": 0}` | 17:04:49 → 19:08:01 |
| 98 | 0 | `pawns` | `{"filter": "hostile"}` | 18:36:42 → 15:43:53 |
| 77 | 0 | `journal` | `{"limit": 900}` | 09:09:16 → 09:56:13 |
| 60 | 0 | `pawns` | `{"filter": "all"}` | 15:05:02 → 15:46:55 |
| 60 | 0 | `triage` | `{}` | 15:08:35 → 15:42:05 |
| 59 | 0 | `zones` | `{}` | 14:27:10 → 12:12:31 |
| 58 | 0 | `journal` | `{"limit": 5000}` | 09:56:54 → 10:19:02 |
| 45 | 5 | `advance` | `{"timeout_ticks": 40000, "until": {"letter": true}}` | 17:04:31 → 14:58:39 |
| 41 | 2 | `advance` | `{"timeout_ticks": 50000, "until": {"letter": true}}` | 20:31:09 → 15:16:31 |
| 40 | 0 | `things` | `{"def": "RawRice"}` | 16:56:03 → 09:57:30 |
| 38 | 0 | `construction` | `{}` | 14:44:49 → 15:40:55 |
| 35 | 0 | `fires` | `{}` | 18:58:54 → 13:03:50 |
| 35 | 1 | `advance` | `{"timeout_ticks": 58000, "until": {"letter": true}}` | 12:43:17 → 15:11:20 |
| 33 | 0 | `bills` | `{}` | 14:47:16 → 11:55:08 |
| 33 | 10 | `things` | `{"def": "ComponentIndustrial"}` | 14:57:49 → 13:42:05 |
| 32 | 0 | `things` | `{"def": "BlocksSandstone"}` | 14:57:45 → 12:51:05 |
| 32 | 25 | `map-dump` | `{"rect": [82, 74, 42, 34]}` | 23:17:37 → 11:12:31 |
| 31 | 2 | `advance` | `{"timeout_ticks": 45000, "until": {"letter": true}}` | 20:20:58 → 15:17:48 |
| 28 | 12 | `research` | `{"cap": 60}` | 14:10:13 → 15:11:52 |
| 28 | 0 | `journal` | `{"limit": 2000}` | 09:56:32 → 11:52:05 |
| 25 | 1 | `advance` | `{"ticks": 5000}` | 15:10:26 → 15:40:20 |
| 23 | 2 | `advance` | `{"timeout_ticks": 30000, "until": {"letter": true}}` | 16:57:14 → 15:04:30 |
| 23 | 5 | `advance` | `{"ticks": 8000}` | 17:36:19 → 10:57:01 |
| 22 | 0 | `journal` | `{"limit": 2000, "since_seq": 1951}` | 11:29:13 → 11:49:43 |
| 21 | 4 | `things` | `{"def": "Steel"}` | 14:44:44 → 12:59:33 |

`digest {}` 424 times, `research {}` 184, `pawns {}` 128. These are the daily-checklist
reads and their repetition is the loop working. The ones that are not:

| n | fails | what it actually was |
|---:|---:|---|
| 32 | 25 | `map-dump {rect:[82,74,42,34]}` — the new-base footprint, fired 25 times into an in-flight `advance` and refused. A 78% miss rate on one call site. |
| 33 | 10 | `things {def:'ComponentIndustrial'}` — components were the run's binding constraint; the agent re-read the count 33 times across 26 hours. |
| 28 | 12 | `research {cap:60}` — 43% refused. |
| 21 | 4 | `things {def:'Steel'}` |
| 16 | 1 | `pawn {id:1018, sections:['health']}` — Aaron, re-read 16 times; he died at tick 723,114. |

### 6b. Bursts — the same op 4+ times inside 180 s

198 such bursts. The largest, and what they were:

| n | op | fails | distinct args | span | where | what |
|---:|---|---:|---:|---:|---|---|
| 81 | `orders` | 0 | 81 | 81s | `openrun-20260902/866-orders` 18:02:05 | one `orders` call per thing, enumerating what a pawn could do with each of 81 objects |
| 42 | `nearest` | 0 | 42 | 53s | `openrun-20260902-s02/867-nearest` 22:53:42 | the conduit sweep — `nearest` called from a grid of points because `limit` was silently dropped |
| 35 | `nearest` | 0 | 35 | 34s | `openrun-20260902-s02/963-nearest` 23:01:18 | the conduit sweep — `nearest` called from a grid of points because `limit` was silently dropped |
| 32 | `things` | 6 | 31 | 66s | `openrun-20260902-s01/641-things` 19:48:55 | one `things` call per def |
| 30 | `orders` | 0 | 30 | 30s | `openrun-20260902/965-orders` 18:05:06 | one `orders` call per thing, enumerating what a pawn could do with each of 81 objects |
| 30 | `build` | 0 | 30 | 39s | `openrun-20260902-s02/831-build` 22:52:28 | one `build` call per blueprint — no bulk form except `place-layout`, which caps at 600 |
| 25 | `designate` | 0 | 25 | 24s | `openrun-20260902/626-designate` 17:40:58 | one `designate` call per target |
| 21 | `build` | 10 | 17 | 43s | `20260903T123633/037-build` 11:58:34 | one `build` call per blueprint — no bulk form except `place-layout`, which caps at 600 |
| 20 | `things` | 1 | 20 | 81s | `openrun-20260902-s02/781-things` 22:47:08 | one `things` call per def |
| 20 | `zone` | 0 | 20 | 19s | `openrun-20260902-s04/323-zone` 10:45:54 | one `zone` op per rect |
| 19 | `build` | 0 | 19 | 28s | `openrun-20260902-s03/223-build` 23:58:35 | one `build` call per blueprint — no bulk form except `place-layout`, which caps at 600 |
| 18 | `build` | 0 | 18 | 24s | `20260903T123633/635-build` 13:18:54 | one `build` call per blueprint — no bulk form except `place-layout`, which caps at 600 |
| 18 | `build` | 0 | 18 | 17s | `20260903T123633-s01/213-build` 14:42:42 | one `build` call per blueprint — no bulk form except `place-layout`, which caps at 600 |
| 17 | `landmark` | 4 | 17 | 64s | `openrun-20260902/098-landmark` 14:37:29 | landmark set, retried through four different arg shapes |

Total bursts of ≥4: **198**, covering **1,486 commands —
22% of everything issued.** Almost all of them have *distinct* args, which
is the point: this is not retry, it is **the absence of a plural form**. 81 `orders`
calls in 81 seconds is one call site that should have taken one command.
## 7. The journals — what the game actually did

7,039 rows across the two run-window journals.

| type | `20260902T175211` | `20260903T123633` | total |
|---|---:|---:|---:|
| `construction` | 1184 | 1098 | 2282 |
| `action` | 779 | 660 | 1439 |
| `alert_on` | 290 | 594 | 884 |
| `alert_off` | 281 | 592 | 873 |
| `message` | 248 | 380 | 628 |
| `letter` | 109 | 160 | 269 |
| `session` | 108 | 153 | 261 |
| `death` | 47 | 58 | 105 |
| `downed` | 39 | 43 | 82 |
| `dialog` | 48 | 32 | 80 |
| `warning` | 34 | 32 | 66 |
| `mental_break` | 23 | 30 | 53 |
| `dev` | 8 | 0 | 8 |
| `red_error` | 1 | 7 | 8 |
| `dialog_answered` | 1 | 0 | 1 |
| **TOTAL** | **3200** | **3839** | **7039** |

### 7a. Every `red_error` — 8 rows

| local | sid/seq | tick | message |
|---|---|---:|---|
| 09-02 21:34:11 | `175211`/1495 | 2,272,362 | HealingEnhancer has null Part. It should be set before PostAdd. |
| 09-03 12:31:55 | `123633`/2280 | 7,292,985 | Could not generate a pawn after 70 tries. Last error: Generated pawn incapable of violence. Ignoring scenario requirements. |
| 09-03 12:31:55 | `123633`/2281 | 7,292,985 | Could not generate a pawn after 100 tries. Last error: Generated pawn didn't pass validator check (post-gear). Ignoring validator. |
| 09-03 12:31:55 | `123633`/2283 | 7,292,985 | System.InvalidOperationException: Cannot force pawn Giggles to have role Invoker. Reason: (*Name)Giggles(/Name) is not psychically sensitive ⏎ [Ref A7A3A016] ⏎   at Verse.AI.Group.PsychicRitualRoleAssignments.AddForcedRole (Verse. |
| 09-03 13:41:52 | `123633`/2922 | 8,503,668 | RimWorld.Tradeable lacks AnyThing. |
| 09-03 13:41:52 | `123633`/2923 | 8,503,668 | RimWorld.Tradeable lacks AnyThing. |
| 09-03 13:52:03 | `123633`/3008 | 8,624,668 | System.NullReferenceException: Object reference not set to an instance of an object ⏎ [Ref B9917D26] ⏎   at SeekAndKill.Dispatcher.InContact (SeekAndKill.Squad squad, SeekAndKill.ThreatCluster cluster) [0x000ac] in /home/dorian/pr |
| 09-03 13:52:16 | `123633`/3011 | 8,624,728 | System.NullReferenceException: Object reference not set to an instance of an object ⏎ [Ref B9917D26] Duplicate stacktrace, see ref for original |

Two of the eight are the **`seekandkill` `Dispatcher.InContact` NRE** (git-bug `a6b1aa0`),
at seq 3008 and 3011 — the second row is the engine's duplicate-stacktrace suppression,
which means the exception was firing continuously, not twice. `Dispatcher.cs:357`.
Three more (seq 2280/2281/2283) are one cultist-raid pawn-generation cascade: 70 tries,
then 100 tries, then a forced psychic-ritual role on a pawn who is not psychically
sensitive. Two are `Tradeable lacks AnyThing` during the one executed trade.

### 7b. Colonist deaths — 23 player colonists, chronological

| # | local | sid/seq | tick | pawn |
|---:|---|---|---:|---|
| 1 | 09-02 17:56:56 | `175211`/512 | 723,114 | **Aaron** |
| 2 | 09-02 18:33:50 | `175211`/714 | 1,209,379 | **Kelsey** |
| 3 | 09-02 18:40:09 | `175211`/759 | 1,234,855 | **Fitz** |
| 4 | 09-02 21:30:48 | `175211`/1460 | 2,271,963 | **Tico** |
| 5 | 09-02 21:30:49 | `175211`/1463 | 2,271,963 | **Haley** |
| 6 | 09-02 21:30:50 | `175211`/1468 | 2,271,963 | **John** |
| 7 | 09-03 00:19:30 | `175211`/3192 | 3,601,410 | **Anarchist** |
| 8 | 09-03 08:41:24 | `123633`/33 | 3,628,891 | **Walton** |
| 9 | 09-03 09:53:46 | `123633`/895 | 4,866,218 | **John** |
| 10 | 09-03 10:11:08 | `123633`/1058 | 5,092,801 | **Rodoytt** |
| 11 | 09-03 10:17:37 | `123633`/1140 | 5,210,963 | **Ellis** |
| 12 | 09-03 10:23:07 | `123633`/1210 | 5,287,051 | **Kimmy** |
| 13 | 09-03 10:29:08 | `123633`/1267 | 5,336,367 | **Tony** |
| 14 | 09-03 10:45:34 | `123633`/1789 | 5,836,198 | **Tanya** |
| 15 | 09-03 15:28:32 | `123633`/3714 | 10,372,797 | **Anon** |
| 16 | 09-03 15:30:07 | `123633`/3721 | 10,373,943 | **Gauss** |
| 17 | 09-03 15:34:34 | `123633`/3745 | 10,387,964 | **Sinn** |
| 18 | 09-03 15:35:28 | `123633`/3756 | 10,393,379 | **John** |
| 19 | 09-03 15:36:37 | `123633`/3760 | 10,395,748 | **Ludo** |
| 20 | 09-03 15:37:32 | `123633`/3770 | 10,397,007 | **Niklas** |
| 21 | 09-03 15:38:12 | `123633`/3775 | 10,397,538 | **Kiozeas** |
| 22 | 09-03 15:43:03 | `123633`/3807 | 10,472,439 | **Ignat** |
| 23 | 09-03 15:46:53 | `123633`/3828 | 10,569,958 | **Dilly** |

> **A death row is not necessarily a death — twice over.**
>
> **(1) Three of the 23 are debug residue.** Tico, Haley and John (pawn 18282) all die at
> tick 2,271,963 — the *same* tick, with the game paused — bracketed by `Dialog_Debug`,
> `Dialog_NamePawn` and `Dialog_DebugOptionListLister` windows. Each appears in the
> journal **only in its own death and funeral letters: zero rows across the 1,459
> journal rows before them.** They were spawned in the dev menu and destroyed there.
>
> **(2) One of the remaining 20 is a repeat.** John's death at `123633`/seq 895 (tick
> 4,866,218) was **reversed by Dorian through RimWorld's own debug menu**, and the journal
> has no event type for a revival — so the row stands unqualified and the same pawn
> (18294) dies again at seq 3756.
>
> **Corrected accounting: 23 death rows → 20 real → 19 distinct colonists lost.** The
> table below counts rows, not colonists. See `findings.md` F-XC-38, F-XC-39, F-XC-4c.

All 151 deaths by kind: animal/other 48, colonist/player 23, mech/other 19, colonist/other 10, animal/player 5.

`downed` 82 rows, `mental_break` 53 rows.

### 7c. The threat timeline — every `ThreatBig`/`ThreatSmall` letter

| local | sid/seq | tick | label |
|---|---|---:|---|
| 09-02 17:20:20 | `175211`/378 | 481,800 | Manhunting guinea pig |
| 09-02 17:45:48 | `175211`/472 | 698,000 | Mad ostrich |
| 09-02 18:28:56 | `175211`/674 | 1,180,071 | Monitor lizard hunting Kelsey |
| 09-02 18:38:58 | `175211`/746 | 1,220,000 | Flashstorm |
| 09-02 18:49:17 | `175211`/830 | 1,386,000 | Mad emu |
| 09-02 18:58:32 | `175211`/897 | 1,448,941 | Berserk: Walton |
| 09-02 21:08:41 | `175211`/1334 | 2,200,000 | Manhunter pack |
| 09-02 22:11:47 | `175211`/1874 | 2,703,685 | Ancient danger |
| 09-03 00:16:59 | `175211`/3165 | 3,563,669 | Berserk: John |
| 09-03 08:46:39 | `123633`/82 | 3,788,915 | Berserk: Ellis |
| 09-03 08:56:35 | `123633`/250 | 4,039,000 | Toxic fallout |
| 09-03 09:00:37 | `123633`/322 | 4,106,000 | Mad dromedary |
| 09-03 09:12:32 | `123633`/514 | 4,346,000 | Heat wave |
| 09-03 09:25:24 | `123633`/653 | 4,599,000 | Shamblers approach |
| 09-03 10:45:50 | `123633`/1803 | 5,851,000 | Shamblers approach |
| 09-03 10:54:46 | `123633`/1926 | 6,078,000 | Flashstorm |
| 09-03 11:11:36 | `123633`/1950 | 6,290,000 | Volcanic winter |
| 09-03 11:30:06 | `123633`/2018 | 6,524,000 | Raid: Nyararm Mechhive |
| 09-03 12:20:48 | `123633`/2213 | 7,157,000 | Raid: Nyararm Mechhive |
| 09-03 12:31:55 | `123633`/2282 | 7,293,000 | Raid: Horax cultists |
| 09-03 12:58:53 | `123633`/2449 | 7,748,000 | Mad boomalope |
| 09-03 13:04:59 | `123633`/2524 | 7,826,000 | Shamblers approach |
| 09-03 13:28:40 | `123633`/2816 | 8,344,000 | Raid: Nyararm Mechhive |
| 09-03 13:33:09 | `123633`/2847 | 8,393,000 | Cold snap |
| 09-03 13:50:18 | `123633`/3002 | 8,594,614 | Mechanoids arrived |
| 09-03 14:12:53 | `123633`/3103 | 9,052,000 | Raid: Nyararm Mechhive |
| 09-03 14:18:23 | `123633`/3143 | 9,166,000 | Raid: Nyararm Mechhive |
| 09-03 14:53:26 | `123633`/3479 | 9,973,361 | Manhunter pack: Chasing Sinn |
| 09-03 15:17:33 | `123633`/3658 | 10,349,000 | Raid: Nyararm Mechhive |

### 7d. The wipe, located exactly

| what | sid/seq | tick | local |
|---|---|---:|---|
| `Raid: Nyararm Mechhive` — *arrived in transport pods nearby* | `123633`/3658 | 10,349,000 | 15:17:33 |
| death: **Anon** | `123633`/3714 | 10,372,797 | 15:28:32 |
| death: **Gauss** | `123633`/3721 | 10,373,943 | 15:30:07 |
| death: **Sinn** | `123633`/3745 | 10,387,964 | 15:34:34 |
| death: **John** | `123633`/3756 | 10,393,379 | 15:35:28 |
| death: **Ludo** | `123633`/3760 | 10,395,748 | 15:36:37 |
| death: **Niklas** | `123633`/3770 | 10,397,007 | 15:37:32 |
| death: **Kiozeas** (Empire guest) | `123633`/3775 | 10,397,538 | 15:38:12 |
| death: **Ignat** (man-in-black) | `123633`/3807 | 10,472,439 | 15:43:03 |
| death: **Raccoon** (crash-landed refugee, never reached) | `123633`/3818 | 10,503,117 | 15:44:31 |
| death: **Dilly** — malnutrition, immobile, nobody left alive to feed him | `123633`/3828 | 10,569,958 | 15:46:53 |

**5,558 ticks (92 in-game minutes) from the raid letter to the first death;
220,958 ticks (3.7 in-game days) from the letter to the last.** Every one of these
rows sits in `20260903T123633-s01` — the directory a `transcripts/openrun-20260902*`
glob does not match.

### 7e. What the glob drops

| | in `openrun-20260902*` | in `20260903T123633*` |
|---|---:|---:|
| steps | 4,868 | 1,806 (27%) |
| colonist deaths | 14 | 9 |
| ThreatBig/Small letters | 18 | 11 |
| red_error rows | 1 | 7 |
| `build` commands | 296 | 248 |

## 8. Spine join coverage

`spine.ndjson` is 13,713 rows: 7,039 journal + 6,674 transcript steps, ordered by UTC,
journal row before transcript row on a tie.

| join key | rows joined | note |
|---|---:|---|
| `journal_seq` echoed in the result | 1,022 | exact row-level join; `dev.journal_seq` for `dev:*`, top-level for player-action verbs |
| `sid` from `result.json` | 6,642 | not in `meta.json`, not in `log.ndjson` |
| `ts`/`wall` proximity only | 5,652 | read-only verbs echo neither |
| no result at all | 26 | timestamp from `log.ndjson` where present, else dir mtime |
