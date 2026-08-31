# Acceptance — manual work priorities (git-bug `e8f2c32`)

`work-priorities` correctly refused priorities 1, 2 and 4 while the colony had
manual priorities off, and nothing in the ~90-verb surface could turn them on.
`PlaySettings.useWorkPriorities` scribes `defaultValue: false`
(`RimWorld/PlaySettings.cs` `ExposeData`), so eight of spec 3.4's acceptance
checks (4.7a-e, 5.1a-c) were unreachable on any colony the agent staged itself.

The lever is now an argument on `work-priorities` itself — the Work tab's own
checkbox, `RimWorld/MainTabWindow_Work.cs` `DoManualPrioritiesCheckbox`, drawn
in the same window as the priority matrix. **No new op is registered**; `verbs`
is unchanged. See DESIGN's decisions log, 2026-08-31, for why it is an argument
rather than a `play-settings` verb.

The worker never launched the bench. Everything below is unverified in-game.

## Running it

Twenty-one envelopes, in order — twenty if 0d is not needed — and each depends
on the one before. On this box:

```bash
rwa work-priorities --args-json '{"manual":true}' --json | jq .data.manual
```

On BORGES the same envelope is `commands/<id>.json` /`results/<id>.json`, per
`accept/3.4-pawn-orders.ps1`'s driver.

**Fixture:** a loaded, paused colony with at least one colonist who can Doctor.
Nothing else — that is the point of the issue. If the colony was staged by a
human who already ticked the box, step 1 reads `use_priorities:true` and step 2
reports `changed:false`; **run step 0d first in that case** (`{"manual":false}`)
so the fixture starts from the shipped default.

**State left behind:** manual priorities **ON**, and pawn **A**'s `Doctor`
priority at **2**. That is deliberate — it is the state 3.4's acceptance now
wants (`accept/3.4-pawn-orders.md` step 0.5 sets it itself). Step 16 turns the
lever back off as a side effect of the call it expects to fail, so **re-run
step 9 after it** to leave the fixture in that state.

---

## The sequence

| # | envelope | expected |
|---|---|---|
| 0a | `{"op":"status"}` | `ok:true`, `data.gameLoaded:true`, `data.paused:true`, **no** `data.forcePause`. Record `data.verbs`: it must **not** have grown — this issue adds no op |
| 0b | `{"op":"journal","args":{"limit":1}}` | `data.last_seq` → `seq0`, the watermark for 14 and 15 |
| 0c | `{"op":"pawns","args":{"filter":"colonist"}}` | ≥ 1 entry; `list[0].id` → **A** |
| 0d | *(only if 1 reads `true`)* `{"op":"work-priorities","args":{"manual":false}}` | resets the fixture to the shipped default so the flip is actually exercised |

### The flip, and that an independent reader agrees

| # | envelope | expected |
|---|---|---|
| 1 | `{"op":"pawn","args":{"id":A,"sections":["work"]}}` | `data.work.use_priorities:**false**` — the fresh-colony default, and the whole bug. Record the `Doctor` entry of `data.work.row` |
| 2 | `{"op":"work-priorities","args":{"manual":true}}` | `ok:true` · `data.mode:"manual"` · `data.manual.before:false` · **`data.manual.after:true`** · `data.manual.changed:true` · `data.manual.requested:true` · **`data.manual.pawns_notified` ≥ 1** · `data.use_priorities:true` · `data.action.journal_seq` ≥ 1 · `data.manual.note` says stored priorities were not altered |
| 3 | `{"op":"pawn","args":{"id":A,"sections":["work"]}}` | `data.work.use_priorities:**true**`. **The read-back, by a different reader** — `PawnSerializer.WorkRow` asks `Find.PlaySettings` itself, so this is not the write echoing itself |

`pawns_notified` is the half a naive field write would have skipped: it counts
the player-faction pawns whose `Pawn_WorkSettings.Notify_UseWorkPrioritiesChanged()`
was called, which is what marks `workGiversDirty` and makes
`WorkGiversInOrderNormal` — the list `JobGiver_Work` walks — rebuild. **`changed:true`
with `pawns_notified:0` on a colony that has colonists is a FAIL**, not a
cosmetic one: it means the flip is inert until something else dirties the cache.

### Priority 1 becomes reachable, and reads back as 1

| # | envelope | expected |
|---|---|---|
| 4 | `{"op":"work-priorities","args":{"set":[{"pawn":A,"work":"Doctor","priority":1}]}}` | `ok:true` · `data.cells:1` · `data.changes[0].after:1` · `data.use_priorities:true` · **`data.action.journal_seq` ≥ 1** (NOT `null`) · **no `data.manual` key** (it was not requested — the block is a write echo, not a status field). *This is 3.4 check 4.7, and 4.7e is the `journal_seq` clause: the matrix path stamped `action` off `Outcome.Accepted`, which it never fills, so every successful matrix write claimed "nothing was mutated". Fixed on this branch* |
| 5 | `{"op":"pawn","args":{"id":A,"sections":["work"]}}` | the `Doctor` entry of `data.work.row` has **`priority:1`**. Before this issue the same read returned `3` no matter what was stored |

### Turning it off: what `GetPriority` reports then

| # | envelope | expected |
|---|---|---|
| 6 | `{"op":"work-priorities","args":{"manual":false}}` | `data.manual.before:true` · **`data.manual.after:false`** · `changed:true` · `pawns_notified` ≥ 1 · `data.action.journal_seq` ≥ 1 · `data.manual.note` warns that stored numbers are not erased **but that a write made while off can only be 0 or 3 and does overwrite a stored 1/2/4** |
| 7 | `{"op":"pawn","args":{"id":A,"sections":["work"]}}` | `data.work.use_priorities:false`, and the `Doctor` entry now reads **`priority:3`** — **not 1**. This is `Pawn_WorkSettings.GetPriority`'s mask, demonstrated: `if (pawn.RaceProps.Humanlike && num > 0 && !Find.PlaySettings.useWorkPriorities) return 3;`. The stored 1 is untouched; the accessor is lying to you, on purpose, and `use_priorities:false` beside it is how you know |
| 8 | `{"op":"work-priorities","args":{"set":[{"pawn":A,"work":"Doctor","priority":1}]}}` | **`ok:false`**, `error.code:"bad-args"`, and `error.detail` still contains *"priority 1 is meaningless with manual priorities off: the Work tab is a checkbox then and GetPriority returns a flat 3"*. **The refusal is preserved** — it is what surfaced the bug and it must not have been weakened into a warning. The detail now also names the route out |

### The flip is lossless — measured, not argued

| # | envelope | expected |
|---|---|---|
| 9 | `{"op":"work-priorities","args":{"manual":true}}` | `before:false`, `after:true`, `changed:true` |
| 10 | `{"op":"pawn","args":{"id":A,"sections":["work"]}}` | the `Doctor` entry reads **`priority:1` again** — the value stored at step 4, through a full off/on cycle with no rewrite in between. **This is the claim DESIGN's entry makes and the only step that can falsify it.** A `3` here would mean the flip destroys data and the verb's `note` is lying |

### Idempotence, and the one-call form

| # | envelope | expected |
|---|---|---|
| 11 | `{"op":"work-priorities","args":{"manual":true}}` *(again)* | `changed:false` · `pawns_notified:0` · `data.action.journal_seq:null` with `data.action.provenance:"not applicable — nothing was mutated"` · `note` reads `already on; …`. A no-op writes no journal line and says so |
| 12 | `{"op":"work-priorities","args":{"manual":false}}` | back off, `changed:true` — setting up 13 |
| 13 | `{"op":"work-priorities","args":{"manual":true,"set":[{"pawn":A,"work":"Doctor","priority":2}]}}` | **`ok:true`** · `data.manual.changed:true` · `data.mode:"matrix"` · `data.cells:1` · `data.changes[0].before:1` · `data.changes[0].after:2` · `data.use_priorities:true`. **The ordering, demonstrated**: priority 2 is validated against the value THIS call installed. Sent from step 12's state without the `manual` key it is refused at step 8's error — that is the difference the one-call form buys, and it is the fixture-staging sequence the bug blocked |

### Provenance and the standing invariant

| # | envelope | expected |
|---|---|---|
| 14 | `{"op":"journal","args":{"since_seq":seq0,"types":["action"],"limit":200}}` | entries with `payload.verb:"work-priorities"` and `payload.step:"manual-priorities"`, carrying `payload.before`, `payload.after`, `payload.pawns_notified`, and `payload.target` reading e.g. `"off -> on"`. **Exactly five** such entries across the run — steps 2, 6, 9, 12 and 13's flip; step 11 changed nothing and correctly wrote none (six if the optional 0d ran). No `cheat` key: an `action` is not a `dev` row |
| 15 | `{"op":"journal","args":{"since_seq":seq0,"types":["red_error"],"limit":50}}` | **`data.count:0`** — the standing invariant. The fan-out walks every player pawn including any without work settings; `Notify_UseWorkPrioritiesChanged` does not route through `ConfirmInitializedDebug`, so none of them should have produced *"did not have work settings initialized"* |

---

### Known, and deliberate: an `ok:false` here does not mean nothing happened

| # | envelope | expected |
|---|---|---|
| 16 | `{"op":"work-priorities","args":{"manual":false,"set":[{"pawn":A,"work":"NoSuchWorkType","priority":3}]}}` | **`ok:false`**, `error.code:"bad-args"` naming the work type — **and then** `{"op":"pawn","args":{"id":A,"sections":["work"]}}` reads `use_priorities:**false**`. The flip ran before the args that failed, by design (that ordering is what makes `{manual:true, set:[…priority 1…]}` one call), so a rejected call can still have moved the lever. Re-running step 14 after this would show a sixth `manual-priorities` row written by a call that returned `ok:false`. Re-run step 9 afterwards to leave the fixture ON |

This step exists to make the behaviour a documented result rather than a
surprise. If the orchestrator would rather the verb be atomic, the fix is to
resolve every `set` block before the flip and re-check the refusal after it —
that is a restructure of the matrix loop, not a line, and it is filed on
`e8f2c32` rather than done here.

## What a human should also eyeball

Three things the protocol cannot assert, worth thirty seconds at the bench
window since it is already open:

1. **Open the Work tab after step 2.** The checkbox at its top-left should be
   ticked, and the columns should be numbers rather than checkmarks. If the
   window was already open when the verb ran, RimWorld redraws it next frame —
   no refresh call is owed and none is made.
2. **After step 6 it should be unticked** and the columns back to checkmarks,
   with the cells that read `1` now reading as plain checks.
3. **After step 9, colonists should actually re-prioritise.** `advance
   {ticks:2000}` and watch the pawn with `Doctor` at 1: the `pawns_notified`
   count is the mechanical claim, but a colonist visibly picking up doctoring
   over lower-priority work is the behavioural one, and it is the thing that
   would fail silently if the fan-out were skipped.

## Also verify: 3.4's eight blocked checks now pass

`accept/3.4-pawn-orders.py` step **0.5** (and the `.ps1`'s) now stages this
itself. The whole point of the issue is that these become reachable:

```bash
python3 accept/3.4-pawn-orders.py --phase 4 --phase 5
```

4.7a-e and 5.1a-c should be `PASS` where they previously failed with
`ok:false`. 0.5 prints the before/after and exits `2` — a fixture gap, not a
failure — if the flip did not take.
