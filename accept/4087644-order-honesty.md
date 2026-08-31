# Acceptance — 4087644, order honesty

Runnable driver: `accept/4087644-order-honesty.py` (54 checks, 6 phases —
phase 0 is the shape contract). No `.ps1` twin — this box has no pwsh and the
bench lives here.

    ./accept/4087644-order-honesty.py --dry-run   # the plan, sends nothing
    ./accept/4087644-order-honesty.py             # against a live bench

**Fixture:** the agent bench (`_RimWorld-Agent/run-agent.sh`) and a colony with
two or more colonists. Two loose apparel items are wanted and one is required;
with none, phase 0 stages two with `dev:spawn-thing {def:"Apparel_Parka",
count:2}` and says so in its output. With only one, phase 3's queue-growth check
is skipped by note and its collision check still runs. Paused is fine; the
driver advances where it needs to.

**Exit codes:** 0 all passed · 1 at least one FAIL · 2 a fixture precondition
could not be met, which is not a spec failure.

**`--dry-run` proves the plan, never the paths.** It sends nothing, so every
envelope is empty, every shape check is skipped, and every wrong `dig()` path
looks fine. The first draft of this suite passed `--dry-run` with eight wrong
arg names and paths in it.

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
`data.things`; `journal` → `data.last_seq`, `data.events`, `data.count`; `pawn`
→ `data.state`, `data.state.job_queue`, `data.apparel`, `data.apparel.worn`. A
shape change then fails *here*, at a check that says which verb moved.

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
| 2.1a–f | `pawn {id:A, sections:["state"]}` | keys present: `job_id`, `player_forced`, `job_giver`, `work_giver`, `job_start_tick`, `ordered` |
| 2.2a | `wear {pawn:A, thing:AP}` then state | `job_giver == "ThinkNode_QueuedJob"` |
| 2.2b | ″ | `ordered == true` (the triple) |
| 2.3 | ″ | `player_forced == true` |
| 2.4 | ″ | `job_start_tick >= 0` |
| 2.5 | ″ | `job_id >= 0` |
| 2.6 | `advance {ticks:2500}` then state | a think-tree job reads `ordered == false` |

`player_forced` **alone is not the discriminator** and 2.6 is what proves the
suite knows that: `RimWorld/JobGiver_Work.cs` `TryIssueJobPackage` sets
`playerForced = true` autonomously on its emergency-prioritized branch. The
unambiguous signature is the triple — `jobGiver is ThinkNode_QueuedJob &&
workGiverDef != null && playerForced` — published as `ordered`.

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
| 3.2a | `wear {pawn:A, thing:AP2, queue:true}` then state | `job_queue.total` grew by one |
| 3.2b | ″ | the queued row has `job_start_tick` **present and null** |
| 3.2c | ″ | the queued row names its own `job_def`, not the running one |
| 3.3 | `wear {pawn:A, thing:AP, queue:true}` colliding, then state | `job_queue.total` **unchanged** |

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

### 5.1b DELIBERATELY BREAKS 3.4's CHECK 5.12c

`accept/3.4-pawn-orders.py:905` asserts the opposite:

```python
eq("5.12c", "and nothing was journalled", e, "data.action.journal_seq", None)
```

That check encodes the pre-4087644 rule — `Outcome.Result` stamped off
`Accepted.Count`, so a call that refused every target wrote no row at all. Under
comment #1 it now writes one. **5.12c is the only assertion in the repo that
contradicts the new contract**, verified by grepping every `journal_seq"`,
`provenance` and `not applicable` assertion across `accept/`. It needs its one
line amended to `ge(..., 1)` with a note pointing here. `accept/3.4-*` is
outside this cluster's territory, so that edit is left to the orchestrator
rather than made here.

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
