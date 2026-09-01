# Acceptance — 4087644, order honesty

Runnable driver: `accept/4087644-order-honesty.py` — six phases, phase 0 being
the shape contract that asserts every envelope key the later phases dig on. No
`.ps1` twin. **No check count is recorded here on purpose:** it went stale
within one session last time, and the tables below are the contract, not a
tally.

Portability: the driver needs python and a bench. On this box it has both and
runs end to end. On a box with pwsh but no python, **phases 5 and 6 are driven
BY HAND from the tables below** — that is how the first acceptance run went.

    ./accept/4087644-order-honesty.py --dry-run   # the plan, sends nothing
    ./accept/4087644-order-honesty.py             # against a live bench

**Fixture:** the agent bench (`_RimWorld-Agent/run-agent.sh`) and a colony with
two or more colonists **and at least one animal on the map** — tame or wild,
either satisfies 6.15 (`PawnSafe.FilterClasses` maps `filter:"animal"` onto both
`ClassAnimal` and `ClassWildlife`); with none, 6.15 exits 2 as a fixture gap
rather than failing. Two loose apparel items are wanted and one is
required; with none, phase 0 stages two with `dev:spawn-thing
{def:"Apparel_Parka", count:2}` and says so in its output. With only one,
phase 3's queued order falls back to a second `move-to` so the check is still
exercised rather than skipped, and phase 3's collision check is fixture-free
either way. Paused is fine; the
driver advances where it needs to.

**Exit codes:** 0 all passed · 1 at least one FAIL · 2 a fixture precondition
could not be met, which is not a spec failure.

**`--dry-run` proves the plan, never the paths.** It sends nothing, so every
envelope is empty, every shape check is skipped, and every wrong `dig()` path
looks fine — which is why it now refuses to print the word *passed*. The first
draft of this suite passed `--dry-run` with eight wrong arg names and paths in
it, and the **first live run** (2026-08-31, 92 PASS / 5 FAIL, zero red errors)
found three more that a dry-run cannot reach. All five failures were driver
defects; no mod change was owed by any of them. See the table under *This suite
is the worked example*.

---

## Phase 0 — the shape contract, and why it is the reusable part

`eq()` cannot tell an **absent** key from one that is **present and null**:
`dig()` returns `None` for both, so `eq(..., None)` passes either way. A driver
whose dig paths are wrong therefore does not fail — it goes **green while
asserting nothing**, which is strictly worse than a loud abort, because nobody
investigates a pass. Every driver in `accept/` is built on that helper, so every
driver inherits the defect, and 3.4's and 5.1's inherited it by copying the
file. *A suite that cannot distinguish absent from null is not a test.*

So phase 0 asserts the **existence** of every envelope key the later phases dig
on, naming the verb and the key: `pawns` → `data.list`; `things` →
`data.things`; `journal` → `data.last_seq`, `data.events`, `data.count`, and —
against a real row, from a second read — `data.events[0].type`,
`data.events[0].payload`, `data.events[0].payload.verb`; `pawn` → `data.state`,
`data.state.job_queue`, `data.apparel`, `data.apparel.worn`. A shape change then
fails *here*, at a check that says which verb moved.

**The row-shape read has to be a second call.** The watermark read pushes
`since_seq` past the end on purpose, so it returns *zero* events — it can prove
`data.events` is a list and nothing whatever about what is inside one. That gap
is exactly what 6.5 fell through (below).

**Per-driver on purpose, not a shared `accept/_shapes.py`.** Every file in
`accept/` stands alone and runs from a bare checkout — that portability is what
makes acceptance work across two benches with different tooling, and it is why
the `.py`/`.ps1` twins duplicate deliberately. And a shared module would let a
shape change made for one spec silently update every other driver, when what you
want is 3.4's driver failing loudly when 3.4's own contract changes.

### This suite is the worked example

Its first draft shipped eight wrong arg names and dig paths. It reached none of
them, because the preflight happened to die first:

| # | wrong | right |
|---|---|---|
| 1 | `pawns {filter:"colonists"}` | the filter word is singular, `colonist` |
| 2 | `data.pawns` | `data.list` |
| 3 | `things {filter:…}` | **there is no `filter` arg** — an unknown key is ignored and the query falls through to `category:"haulable"` |
| 4 | `data.things` with no `detail:true` | rollup rows are BY DEF and carry no `id`; `detail:true` adds the addressable list (issue `70ac258`) |
| 5 | `things {category:"apparel"}` | `by_location` **defaults true** for apparel on the map, so rows sit under `data.by_location.{stockpiled,worn,loose}` and there is no top-level `data.things` — pass `by_location:false` |
| 6 | `pawn {pawn:…}` | that verb takes `id` (`IntReq("id")`). The **job** verbs take `pawn`/`pawns`; do not unify them with a sed |
| 7 | `journal {limit:1}` → `data.next_seq` | neither key exists, and `{limit:1}` reports the *second* line's seq — use `{since_seq:999999999, limit:1}` → `data.last_seq` |
| 8 | `data.ok` on `work-priorities` | `Outcome.Result` publishes no `ok`; only hand-built envelopes (prioritize, research-set) carry `data.ok`. Assert the **envelope's** `ok` |

`has_key()` was already in the file, for `job_start_tick`. The distinction was
known, the tool was built, and it was applied in exactly one place. That is the
lesson worth carrying: the next person will also know, and also forget.

**And the first live run found three more, in a driver that had already been
audited for exactly this.** 92 PASS / 5 FAIL, zero red errors, and *every one of
the five was a driver defect — no mod change was owed*:

| # | wrong | right |
|---|---|---|
| 9 | `data.rows` / `data.entries` on `journal` (6.5) | `data.events` — the key this same file's 0.2b asserts and 1.4b reads. Both attempted paths missed, `as_list(None)` gave `[]`, and it reported `actual: []` against a journal holding every row |
| 10 | a journal row read **flat** for `verb` (6.5) | `Journal.Emit` writes `{seq, tick, wall, type, payload}`; `PawnActs.Act` puts `{verb, step, target}` under `payload`. It is `payload.verb`. Also `"man"` was accepted for `"man-turret"` — that is the **step**, not the verb |
| 11 | `prioritize {work:"Warden_DeliverFood"}` (6.9) | that is the `<giverClass>`; the `<defName>` is `DeliverFoodToPrisoner`. `Dev.Named` throws first, so the reply had **no `data` block at all** |

Plus one that was not a path at all but a target: 6.15 aimed `attack` at a
colonist, and `Attack` returns at `cannot-target` before it ever reads `queue`
— unpassable on any save. See 6C.

**The pattern across all four is one thing: `eq(..., None)` and a missing block
are indistinguishable, and so are a missing key and a nested one.** 6.5 survived
only because it happened to be phrased as *positive membership* (`want <=
verbs`), which fails red; 6.9's two `eq`/`ge` checks against an error envelope
reported "wrong gate" and "missing seq" when the truth was "the verb never ran".
Phase 0 now proves the journal **row** shape against a real row, from a second
read — the watermark read returns zero events by design and so could never have
caught this.

### And a second failure mode the contract does not catch

Two of this suite's own checks were wrong not because a path was wrong but
because **the state they asserted against was never staged**:

* Phase 3 queued an `equip` **of apparel**. `EquipmentUtility.CanEquip` refuses
  apparel outright, so that order could only ever be rejected `cannot-equip` and
  the queue would never move — which reads as a broken queue rather than a wrong
  call. It now queues a `wear` of a second item.
* Phase 3's collision check ran *after* phase 2's advance, by which point the
  pawn was back on its own think tree — so the "colliding" order would simply
  have **enqueued**, and the check would have gone green having proved the
  opposite of its claim. It now re-establishes a known `curJob` first.

A shape contract proves the envelope. It cannot prove the fixture. Both of these
were found by re-reading assertions against the verb sources, which is the only
method that worked on any of it.

---

## The one-sentence claim under test

`Verse.AI/Pawn_JobTracker.cs` `TryTakeOrderedJob` opens with

```csharp
job.playerForced = true;
if (curJob != null && curJob.JobIsSameAs(pawn, job))
{
    return true;
}
```

so a pawn already running an equivalent job makes the call return **true having
done nothing** — our Job, the one carrying `playerForced`, is discarded and
`curJob` keeps `playerForced == false`. Every `accepted` reported in that case
was a lie, and `job_def` corroborated it, because it is re-read *after* the call
and therefore names the job we did not cause.

---

## Phase 1 — `already-doing-it`

| # | call | expect |
|---|---|---|
| 1.1a/b | `wear {pawn:A, thing:AP}` | `counts.accepted == 1`, `action.journal_seq >= 1` |
| 1.2a | `wear {pawn:A, thing:AP}` again | `counts.accepted == 0` |
| 1.2b | ″ | `rejected[0].gate == "already-doing-it"` |
| 1.2c | ″ | `rejected[0].queue == false` |
| 1.2d | ″ | the reason names `Job.JobIsSameAs`, not a phrase of ours |
| 1.3a | ″ | **`action.journal_seq >= 1`** — the wasted order still journals |
| 1.3b | ″ | and is not disguised as `"not applicable — nothing was mutated"` |
| 1.4b | `journal {types:["action"]}` | at least one `data.events[]` row with **`payload.verdict.by_gate["already-doing-it"]`** — the emitted payload is nested under `payload` |
| 1.5a/b/c | `wear {pawn:A, thing:AP, queue:true}` | same gate, `queue == true`, reason says nothing was enqueued |
| 1.6 | `draft {pawns:[A]}` then `move-to {pawns:[A], to:<A's own cell>}` | `gate == "already-there"` — move-to's shipped gate is untouched |

**1.3 is the amended contract.** The issue body's Acceptance bullet 1 said a
redundant order "writes no `action` row"; comment #1 says it must write one
carrying its verdict, because the wasted orders are exactly the ones invisible
to the ledger and `journal {types:["action"]}` is the aggregate the agent learns
from. Comment #1 supersedes — confirmed with the orchestrator before building.

---

## Phase 2 — attribution

| # | call | expect |
|---|---|---|
| 2.1a–g | `pawn {id:A, sections:["state"]}` | keys present: `job_id`, `player_forced`, `job_giver`, `work_giver`, `job_start_tick`, `ordered`, `order_kind` |
| 2.2a | `wear {pawn:A, thing:AP}` then state | `job_giver == "ThinkNode_QueuedJob"` |
| 2.2b | ″ | `work_giver == null` — a `wear` order has no WorkGiver |
| 2.2c | ″ | `ordered == false`, **because** the triple requires a WorkGiver |
| 2.2d | ″ | `order_kind == "direct"` — the field that *does* answer "did I cause this" |
| 2.3 | ″ | `player_forced == true` |
| 2.4 | ″ | `job_start_tick >= 0` |
| 2.5 | ″ | `job_id >= 0` |
| 2.6a | `advance {ticks:2500}` then state | a think-tree job reads `ordered == false` |
| 2.6b | ″ | and `order_kind` present and **null** — neither work nor direct |

`player_forced` **alone is not the discriminator** and 2.6 is what proves the
suite knows that: `RimWorld/JobGiver_Work.cs` `TryIssueJobPackage` sets
`playerForced = true` autonomously on its emergency-prioritized branch. The
unambiguous signature is the triple — `jobGiver is ThinkNode_QueuedJob &&
workGiverDef != null && playerForced` — published as `ordered`.

### 2.2b WAS WRONG AND THE CODE WAS RIGHT — amended 2026-08-31 (session 9)

This row asserted `ordered == true` after a plain `wear`. The orchestrator's
acceptance run measured `ordered FALSE`, `work_giver null`, and stopped there
rather than "fixing" the code. It was right to: `PawnActs.JobFacts` computes
`ordered = queuedNode && workGiver != null && forced` — **exactly the triple
this document defines in the paragraph above the table** — and a `wear` order
has no WorkGiver, so `ordered` is false by construction. The script
contradicted its own prose. The row now asserts what the definition implies,
and 2.2b/2.2c split it so the *reason* is asserted beside the value: a false
`ordered` is only honest when `work_giver` is genuinely null.

Where a `true` comes from, for whoever wants the positive case: a `prioritize`
order, which is the only kind that carries a `workGiverDef` (the provider
stamps `job.workGiverDef = scanner.def`, and `TryTakeOrderedJobPrioritizedWork`
stamps it again). Not driven here because phase 2's fixture is an apparel
order.

### `order_kind` — 2.2d, added by git-bug ac407f1

The paragraph that used to sit here recommended keeping `ordered` and letting
readers assemble the answer themselves from `player_forced` + `job_giver`.
That recommendation is now implemented as a field rather than left as advice.
`PawnActs.JobFacts` publishes **`order_kind`** beside `ordered`:

| `order_kind` | means | who produces it |
|---|---|---|
| `"work"` | the triple — `queuedNode && workGiver != null && playerForced` | `prioritize`, or a right-click work option |
| `"direct"` | `queuedNode && playerForced`, no WorkGiver | `wear`, `equip`, `move-to`, `tend`, `carry`, … |
| `null` | no evidence of an order | a think-tree job, **and** the unresolvable-`jobGiver` case |

`ordered == (order_kind == "work")` by construction. `ordered` is *not*
redefined — 3.4 and 2.2b/2.2c assert it by name and the meaning they assert is
correct — it simply gains a companion that answers the question the name
invites. A rename is now unnecessary rather than merely out of scope.

**A source correction the issue got backwards, and it is load-bearing for the
split.** ac407f1 says the `workGiverDef` clause is what excludes
`JobGiver_Work`'s autonomous `playerForced`. It is not.
`RimWorld/JobGiver_Work.cs` `TryIssueJobPackage`'s emergency-prioritized branch
calls `GiverTryGiveJobPrioritized`, which **sets `workGiverDef` on the job**
before the branch sets `playerForced = true` and returns
`new ThinkResult(job, this, tag)`. So that job carries *both* a `workGiverDef`
and `playerForced`; the only clause that rejects it is
`jobGiver is ThinkNode_QueuedJob`, because `this` is the `JobGiver_Work` node
and `Pawn_JobTracker.StartJob` assigns `curJob.jobGiver = jobGiver` from the
`ThinkResult`'s source node. That is precisely why splitting on `workGiverDef`
is safe: by the time either kind is decided, the autonomous case is already
gone, so `"direct"` cannot capture it. **Check 2.6b is the guard**: if
`order_kind` is ever widened to read off `playerForced` alone, 2.6b is what
fails.

The `queuedNode` clause carries a second load. `Verse.AI/JobQueue` is enqueued
by four *non-player* sites — `JobDriver_AttackStatic` and
`JobDriver_AttackMelee` (a follow-up attack), `JobInBedUtility` (`LayDown`),
and `Pawn_JobTracker`'s own `resumeCurJobAfterwards` path — so
`ThinkNode_QueuedJob` alone does not mean "the player asked". Every one of
those enqueues a job with `playerForced` **false**, so the *pair*
(`queuedNode && playerForced`) is the discriminator and the WorkGiver split is
a refinement on top of it.

2.2 degrades to a NOTE rather than a FAIL when `job_giver` is null. That is
honest, not lenient: `Job.jobGiver` is scribed as an int key resolved against
the think tree, and `ThinkTreeKeyAssigner` assigns those keys *before*
`ResolveParentNodes` links parents — so the hash collapses to the node's own
type name and same-type nodes are separated only by `num ^= Rand.Int` in
traversal order. Any mod adding a node of an already-used type shifts the key.
On a 38-mod bench a null is a real outcome, which is why nothing branches on
this field.

---

## Phase 3 — the job queue

| # | call | expect |
|---|---|---|
| 3.1 | `pawn {id:A, sections:["state"]}` | a `job_queue` block exists |
| 3.2z | `draft` + `move-to {to:P1}` then state | `job_def == "Goto"` — the stage really staged |
| 3.2d | `wear {pawn:A, thing:AP2, queue:true}` | the accepted line carries `order_effect` |
| 3.2e | ″ | `order_effect == "queued"` — measured, not the flag echoed back |
| 3.2a | ″ then state | `job_queue.total` grew by one |
| 3.2b | ″ | the queued row has `job_start_tick` **present and null** |
| 3.2c | ″ | the queued row names its own `job_def`, not the running one |
| 3.3a | `move-to {to:P1, queue:true}` colliding | `gate == "already-doing-it"` |
| 3.3 | ″ then state | `job_queue.total` **unchanged** |

### The stage was wrong, and it looked like a mod defect — git-bug ac407f1 (c)

This phase used to stage its running job with `wear {thing:AP}`. By the time
phase 3 runs, phase 2 has ended with `advance {ticks:2500}` — long enough for A
to **finish** wearing that item — and a worn apparel is unspawned, so it is not
in `map.listerThings` and `PawnActs.ThingArg` refuses it as "no visible thing".
The stage did nothing, silently, and left A **idle**.

An idle pawn is exactly the case where `queue:true` does not queue.
`Verse.AI/Pawn_JobTracker.cs` `TryTakeOrderedJob`:

    bool flag2 = mindState.IsIdle || CurJob == null || CurJob.def.isIdle;
    isDownEvent = KeyBindingDefOf.QueueOrder.IsDownEvent || requestQueueing;
    if (num2 && (!isDownEvent || flag2)) { ClearQueuedJobs();
        EnqueueFirst(job); curDriver.EndJobWith(InterruptForced); }

With `requestQueueing` the `!isDownEvent` half is false, so that branch is
reached **only** via `flag2` — and it `EnqueueFirst`s and ends the current job
in the same call, so `ThinkNode_QueuedJob` dequeues the order immediately and it
*runs*. `job_queue.total` then reads 0 because the order **started**, not
because it was lost. That is vanilla shift-click behaviour, not a dropped flag,
and it is the most likely reading of the measured "3.2a — a queued order does
not appear in the queue".

Two further consequences of the same three branches, both previously invisible
and both now published on the accepted line (`PawnActs.OrderEffect`):

* **Branches A and C call `ClearQueuedJobs()` first**, so an *unqueued* order
  silently destroys every order already stacked up — reported as
  `queue_dropped`.
* **An order the caller did not ask to queue can still be queued.** Branch C is
  reached when the current job is not `IsCurrentJobPlayerInterruptible` (or is
  `forceCompleteBeforeNextJob`): the job goes to the queue and **the pawn keeps
  doing what it was doing**, while the verb answers `accepted:1`. That is a
  false "it is doing it now", and `order_effect:"queued"` against
  `queue:false` is what exposes it.

So the stage is a **drafted Goto**: `JobDefOf.Goto` is not `isIdle` and is
player-interruptible, so the queued order takes the `EnqueueLast` branch. It
costs no fixture — a reachable cell, probed rather than assumed, the same idiom
phase 6C uses — and 3.2z asserts the stage actually staged, so a fixture gap
can no longer masquerade as a publication bug. The collision (3.3) is driven off
that same staged Goto rather than off whatever A happened to be doing, which is
what makes the gate exercised every run instead of most runs.

**3.3 is the sharpest check in the suite.** `requestQueueing` is not read until
`isDownEvent = isDownEvent || requestQueueing`, eight lines past the early
return — so a queued order that collides with the running job enqueues *nothing*
and still returns true. Publishing the queue does not rescue that case; there is
no entry to publish. Only the gate reports it. An empty queue is therefore
**not** evidence an order was refused, and the block's own `note` says so.

3.2b guards a sentinel: `Job.startTick` is assigned only in
`Pawn_JobTracker.StartJob`, so a queued job carries the uninitialised `-1`. It
publishes as `null`, never as `-1` dressed up as a tick.

---

## Phase 4 — forced apparel

| # | call | expect |
|---|---|---|
| 4.1 | `pawn {id:A, sections:["apparel"]}` | every worn row carries `forced` |
| 4.2 | ″ | the envelope carries `forced_count` |
| 4.3 | `wear {pawn:A, thing:AP}` + `advance {ticks:4000}` | at least one row `forced:true` |
| 4.4 | ″ | at least one row `forced:false` — policy-worn and force-worn are distinguishable |
| 4.5 | ″ | `forced_count >= 1` |
| 4.6 | `journal {types:["red_error"]}` | `count == 0` |

`RimWorld/JobDriver_Wear.cs`'s **final** toil does
`if (pawn.outfits != null && job.playerForced) pawn.outfits.forcedHandler.SetForced(apparel, forced: true);`
— so `forced:true` means *this piece is worn because it was forced*, it survives
save/load, and it is the only durable per-item trace any of the job verbs
leaves. 4.3 advances first because an unfinished wear leaves no receipt; that is
the field's meaning, not a gap in it.

**4.6 is the hazard check.** `OutfitForcedHandler.IsForced(ap)` calls
`Log.Error` **and** removes from the scribed list when the apparel is destroyed
— a red error out of something spelled as a predicate. The shipped route reads
`ForcedApparel` (an expression-bodied getter straight onto a field initialised
at declaration: never null, no lazy init) and calls `.Contains`. `forced` is
`null`, not `false`, for a pawn with no outfit tracker: "no forced-apparel state
exists here" is a different claim from "this was not forced".

---

## Phase 5 — the journal rule, and the e8f2c32 guard

| # | call | expect |
|---|---|---|
| 5.1a | `undraft {pawns:[A]}` then `tend {pawn:A, target:B}` | `rejected[0].gate == "drafted-only"` |
| 5.1b | ″ | **`action.journal_seq >= 1`** |
| 5.2a/b/c | `work-priorities {manual:true}`, then `work-priorities {set:[{pawn:A, work:"Doctor", priority:1}]}` | envelope `ok` (not `data.ok`), `counts.unit == "matrix cells"`, `action.journal_seq >= 1` |
| 5.3 | `journal {types:["red_error"]}` | `count == 0` |

### 5.1b DELIBERATELY BREAKS 3.4's CHECK 5.12c — now amended, all three twins

`accept/3.4-pawn-orders` asserted the opposite:

```python
eq("5.12c", "and nothing was journalled", e, "data.action.journal_seq", None)
```

That check encoded the pre-4087644 rule — `Outcome.Result` stamped off
`Accepted.Count`, so a call that refused every target wrote no row at all. Under
comment #1 it now writes one. **5.12c was the only assertion in the repo that
contradicted the new contract**, verified by grepping every `journal_seq"`,
`provenance` and `not applicable` assertion across `accept/`. It is now
`>= 1` in all three twins — `.py:913`, `.md` phase 5's table row, and
`.ps1:783`, which had been left behind when the `.py` was amended and would
have failed a pwsh run of the same suite.

### 5.2 is the e8f2c32 regression guard

`work-priorities`' matrix path never calls `Outcome.Ok` — its unit is matrix
*cells*, not pawns — so `Accepted.Count` is always 0 there and `Outcome.Result`
would stamp `journal_seq: null, "nothing was mutated"` over a call that had just
journalled. e8f2c32 fixed that by overriding `result["action"]` after the fact.
The journal-rule change touches the same code, so this phase re-asserts it, and
the matrix path deliberately does **not** route through the new `ActOn` helper:
`ActOn`'s verdict is built from `Accepted.Count` and would have re-introduced
exactly the same false negative one level down, inside the journal row.

---

## Phase 6 — the remainder: every refusal journals, and `move-to` queues

**Added session 9 for the 4087644 remainder and git-bug bc2250b. UNEXECUTED —
this section is the exact call list for the orchestrator's in-game run; the
worker who wrote it never launched anything.** Phases 1–4 already passed on the
bench and are unaffected; run 5 and 6.

Substitute real ids: **A**, **B** = two colonists · **AP** = a piece of apparel
on the ground · **C0** = any cell where nothing is burning (A's own cell does) ·
**P1**, **P2** = two reachable cells far apart and in *different directions*
from A.

A pawn is a `Thing` and `PawnActs.ThingArg` resolves one, so **B doubles as the
non-apparel / non-equippable / non-mannable target** below — no hunting the
ground for a weapon that may not be on this colony. Deliberately NOT `AP`:
phases 1 and 4 leave it **worn**, a worn item is unspawned, and `ThingArg`
would refuse it as "no visible thing" before the gate under test was ever
reached — a check that fails for the wrong reason is worse than no check.

Record `seq0 = journal {limit:1}`'s highest seq before starting, so the
`journal` reads below can use `since_seq`.

### 6A — the four emergency-verb rulings (4087644 remainder)

| # | call | expect |
|---|---|---|
| 6.1a | `tend {pawn:A, target:B}` after `undraft {pawns:[A]}` | `rejected[0].gate == "drafted-only"` |
| 6.1b | ″ | **`action.journal_seq >= 1`** — this is 5.1b, the check that failed |
| 6.2a | `extinguish {pawns:[A], at:C0}` (nothing burning) | `counts.accepted == 0`, `rejected[0].gate == "not-burning"` |
| 6.2b | ″ | `action.journal_seq >= 1`, and `rejected[0].at` names C0 |
| 6.3a | `beat-fire {pawns:[A], target:B}` (B not on fire) | `rejected[0].gate == "not-burning"`, `rejected[0].thing == B` |
| 6.3b | ″ | `action.journal_seq >= 1` |
| 6.4a | `man-turret {pawns:[A], thing:B}` (a pawn has no CompMannable) | `rejected[0].gate == "not-mannable"` |
| 6.4b | ″ | `action.journal_seq >= 1` |
| 6.5 | `journal {since_seq:seq0, types:["action"]}` | `data.events[]` carries `payload.verb` for **all four** of `extinguish`, `beat-fire`, `man-turret`, `tend`, each with `verdict.by_gate` and its gate at count 1 |

6.5 is the one that matters. Per-call reporting was already correct for three
of these; what was missing is the **aggregate**, and the aggregate is what
comment #1 says the agent learns from.

**6.5 was the driver's own blind spot, found by the first live run and fixed
2026-08-31.** It read `data.rows` *or* `data.entries` — neither key exists;
`JournalVerbs.Read` returns `["events"]`, which this file's own 0.2b and 1.4a
assert and 1.4b reads correctly. Both digs missed, `as_list(None)` collapsed to
`[]`, and it reported `actual: []` against a journal that held every row. Two
more mistakes were stacked behind it: the verb was then read **flat**, when
`Journal.Emit` writes `{seq, tick, wall, type, payload}` and `PawnActs.Act` puts
`{verb, step, target}` under `payload`; and `"man"` was accepted as an
alternative to `"man-turret"`, when `PawnEmergencyVerbs.ManTurret` declares
`const string V = "man-turret"` and passes `"man"` as the **step**
(`ActOn(outcome, verb, "man", …)`) — a step satisfying a verb assertion. The
rows were there all along: `6.1b`/`6.2b`/`6.3b`/`6.4b` each read
`journal_seq >= 1` in the same run, a number that exists only because
`PawnActs.ActOn → Act → Journal.Emit("action", …)` wrote a line. **No mod change
was owed.** Note the asymmetry that saved it: 6.5 is a *positive membership*
assertion, so a wrong path fails red. Had it been an `eq(..., None)` it would
have gone green while proving nothing — the same defect phase 0 exists for.

### 6B — the same shape swept out of the order verbs

| # | call | expect |
|---|---|---|
| 6.6a | `wear {pawn:A, thing:B}` (a pawn is not apparel) | `rejected[0].gate == "not-apparel"` |
| 6.6b | ″ | `action.journal_seq >= 1` |
| 6.7a | `equip {pawn:A, thing:B}` (a pawn has no CompEquippable) | `rejected[0].gate == "not-equippable"` |
| 6.7b | ″ | `action.journal_seq >= 1` |
| 6.8a | `attack {pawns:[A], target:B}` (a colonist: not hostile, not an animal, not an attackable building) | `rejected[0].gate == "cannot-target"` |
| 6.8b | ″ | `action.journal_seq >= 1` |
| 6.9a | `prioritize {pawn:A, work:"DeliverFoodToPrisoner", thing:B}` — any work giver `orders {pawn:A, thing:B}` does **not** list | `ok == false`, `rejected[0].gate == "not-offered"` |
| 6.9b | ″ | `action.journal_seq >= 1` — a refused prioritize used to publish `"not applicable — nothing was mutated"` |

**`work` takes the defName, not the giverClass — fixed 2026-08-31 after the
first live run.** This said `Warden_DeliverFood`, which is the C# type named by
`<giverClass>` in `Core/Defs/WorkGiverDefs/WorkGivers.xml`; the `<defName>` on
that same def is `DeliverFoodToPrisoner`. `PawnOrderVerbs.Prioritize` opens with
`Dev.Named<WorkGiverDef>(workName, "work")` — *before* it constructs its
`Outcome` — and `DevVerbs.Dev.Named` throws `VerbArgsException` on a miss, so
the reply was a **bad-args error envelope with no `data` block at all**. Both
digs read null: not "empty and accepted", not "gate-less" — **absent**, which is
the `eq(..., None)` hazard arriving from the other direction. The mod-side
contract was already implemented and simply never reached: Prioritize's
`if (!matched)` exit stamps `Stamp(PrioritizeRow(…))` on the refusal path.

**Not staged, and say so rather than inventing a fixture:** `move-to`'s
`no-standable-cell` refusal needs a destination with no standable cell within
2.9, which cannot be produced reliably on an arbitrary colony. It takes the
same `NoAt` route as 6.2 and is covered by inspection only.

### 6C — `move-to {queue:true}` (git-bug bc2250b)

This is the issue's own reproduction, re-run.

| # | call | expect |
|---|---|---|
| 6.10 | `draft {pawns:[A]}`, then `move-to {pawns:[A], to:P1}` | `counts.accepted == 1`, `accepted[0].job_def == "Goto"`, `accepted[0].queue == false` |
| 6.11a | `move-to {pawns:[A], to:P2, queue:true}` | `counts.accepted == 1`, `accepted[0].queue == true` |
| 6.11b | then `pawn {pawn:A, sections:["state"]}` | `job_queue.total` **grew from 0 to 1** |
| 6.11c | ″ | `job_queue.list[0].job_def == "Goto"`, with its own `job_id` and `job_start_tick: null` |
| 6.11d | ″ | `state.job_def == "Goto"` still, and its `job_id` is the one from 6.10 — **the running job was not replaced** |
| 6.12 | `advance {ticks:60}` then `pawn {pawn:A, sections:["state"]}` | the position has moved toward **P1**, not P2 — the first destination is walked first, and `job_queue.total` is still 1 |
| 6.13a | `move-to {pawns:[A], to:P1}` while still walking to P1 | `counts.accepted == 0`, `rejected[0].gate == "already-doing-it"` |
| 6.13b | ″ | the reason names `PawnGotoAction`'s own clause, and `action.journal_seq >= 1` |
| 6.14 | `move-to {pawns:[A], to:<A's current cell>}` | `rejected[0].gate == "already-there"` — the shipped gate, untouched |
| 6.15a | `pawns {filter:"animal"}` → W, then `attack {pawns:[A], target:W, queue:true}` | `counts.accepted == 0`, `rejected[0].gate == "queue-unsupported"` |
| 6.15b | ″ | the reason names `FloatMenuUtility.GetRangedAttackAction`, and `action.journal_seq >= 1` |
| 6.16 | `journal {since_seq:seq0, types:["red_error"]}` | `count == 0` |

**6.15 targeted `B` and was therefore unpassable on any save — fixed
2026-08-31.** `B` is the very target 6.8a asserts is `cannot-target`, and in
`PawnOrderVerbs.Attack` the `CanDraftAttack` → `outcome.NoThing(target,
"cannot-target", …)` exit **returns before** the `ctx.Args.Bool("queue")` →
`queue-unsupported` block is ever reached. So the check measured the wrong gate
*by construction*: not a fixture problem, a wrong target choice, and it would
have failed identically on every colony in existence. `CanDraftAttack`'s third
accepting clause is `t is Pawn p && p.NonHumanlikeOrWildMan()`, so **any animal
qualifies regardless of hostility**, and the queue block is the next statement
after it. `filter:"animal"` selects both `ClassAnimal` and `ClassWildlife`
(`PawnSafe.FilterClasses`), so one probe covers tame and wild, and the roster
already excludes fogged and off-map pawns (`PawnSafe.Hidden`) so whatever comes
back is resolvable by `PawnActs.ThingArg`. Nothing is mutated: the
`queue-unsupported` branch refuses every pawn and returns without building a
job, so the colony animal is never actually attacked.

**The empty probe is the one legitimate fixture branch in phase 6, and it exits
2 — never a FAIL.** With no animal anywhere on the map, *nothing* reaches the
queue block, so the check cannot be staged at all. `6.15c` is downstream and
needed no separate fix.

**6.11b + 6.11d together are the whole bug.** The measured failure was
`accepted:1`, `job_queue.total 0`, and the pawn walking to the *queued*
destination — the running job replaced and success reported. Either half alone
would miss it: a grown queue with the running job also replaced is still wrong,
and an unchanged running job with an empty queue means the order vanished.

**Pick P1 and P2 at least ~10 cells out, and keep 6.12's advance short.** A
colonist covers a cell every ~13–20 ticks, so the issue's original 150 would
let it ARRIVE at a nearby P1, start the queued job, and make 6.13 test the
wrong thing. If `job_queue.total` has dropped to 0 before 6.13, the collision
was not staged — shorten the advance and re-run rather than recording a fail.

**6.13 is the second defect the fix uncovered**, and it was never in either
issue: `PawnGotoAction` returns `flag = true` for a pawn already walking to
that exact cell, so `move-to` reported `accepted:1` for an order that did
nothing. That is 4087644's family reached by a different road — the verb never
touched `TryTakeOrderedJob`, so `AlreadyDoing` never saw it.

**On 6.11a's `accepted: 1` for a queued order:** a queued order that is
genuinely enqueued is still an accepted order — `Outcome.Ok` means "the game
took this", not "the pawn is doing it now". `job_queue` is the evidence, which
is why 6.11b is a separate check and not a footnote. And note the honest
limit already stated in phase 3: an **idle** pawn takes a queued order
immediately, because `TryTakeOrderedJob`'s first branch fires when
`mindState.IsIdle || CurJob == null || CurJob.def.isIdle`. That is vanilla's
own shift-click behaviour, not a dropped flag — which is why 6.10 puts a
running Goto under it first.

---

## Not tested here, and why

**`attack`'s probe cannot be given a clean acceptance.** `attack` never builds
the Job — it calls `FloatMenuUtility.GetRangedAttackAction` /
`GetMeleeAttackAction`, which construct and take the order internally — so it
gets a before/after `curJob.loadID` + `jobQueue.Count` probe instead of the
pre-check. That probe is **behavioural, not predictive**: unchanged state cannot
distinguish "was already doing it" from "the game's delegate declined silently",
and the reason string says so rather than picking one. Staging a collision
reliably enough to assert on it would mean reproducing the game's own targeting,
which is the reimplementation the wrap-don't-reinvent rule forbids. Exercised by
hand instead; the limit is recorded at the call site so nobody unifies it with
`AlreadyDoing` later and loses the distinction.

**`already-doing-it` cannot answer "would they have done it anyway".** It
catches the collision only at the instant of the call. A pawn three ticks from
choosing the same job on its own still reads as a clean `accepted`. The only
rigorous answer is a hold-out — stop sending a class of instruction for N days
and compare — which is a playbook lesson, not a feature. Stated in the issue and
repeated here so a green run is not over-read.
