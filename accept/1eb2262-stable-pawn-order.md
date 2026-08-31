# git-bug 1eb2262 acceptance — `pawns` returns a stable, documented order

Run by the ORCHESTRATOR against the live `_RimWorld-Agent` bench. The worker
wrote and compiled this and never launched the game.

Every step is one protocol envelope. Write it to `commands/<id>.json` and read
`results/<id>.json`, or use the `rwa` line beside it — they are the same call.

**Preconditions.** A loaded colony on `_RimWorld-Agent`, at least **three**
visible colonists (so a re-order is actually observable — with two, any
permutation is one swap and half of them look like the identity), `devMode`
on for the `dev:*` staging, and no force-pausing window up (`rwa status`).

**What is being asserted.** Three separate claims, and they need three
different steps:

| claim | proved by |
|---|---|
| the emitted order is `thingIDNumber` ascending, and it says so | A1 |
| membership is still chosen by attention, not by id | B2, C4 |
| a new pawn does not permute the old ones | C1 → C3 |

**Standing assertions on every step**: `ok:true`, and no `red_error` in the
journal (checked once at the end, D1).

---

## A — the shape of the result

### A0. Journal watermark (discovery)
```json
{"id":"a0","op":"journal","args":{"since_seq":999999999,"limit":1}}
```
```
rwa journal --since_seq 999999999 --limit 1
```
Read `data.last_seq` -> **`<SEQ0>`**. D1 reads forward from it, so a red error
that predates this run is not charged to it.

> **`{"limit":1}` alone is the wrong envelope here and it silently ruins D1.**
> `JournalVerbs.Read` computes `last_seq` while scanning and `break`s as soon as
> `events.Count >= limit`, so with `limit:1` it stops at the SECOND line of the
> file and reports that line's seq — a watermark of ~2, not the end of the file.
> D1 would then re-report every red error in the whole session as if this run
> caused it. Pushing `since_seq` past the end makes every line fail the filter,
> so the loop never reaches the limit break and reads to EOF, which is what
> makes `last_seq` the true maximum. `accept/1.8-game-clock-advance.sh` step 01b
> is the same idiom and says the same thing; `accept/3.4-pawn-orders.{md,py,ps1}`
> step 0.4 still has the `{"limit":1}` form and inherits the same flaw.

### A1. Default read
```json
{"id":"a1","op":"pawns","args":{"filter":"colonist"}}
```
```
rwa pawns --filter colonist
```
Expect:

- `data.order` == `"id-asc"` — **this is the default now.** 2.6 shipped
  `"attention-desc"` here; if you see that word without having asked for it,
  the old assembly is loaded.
- `data.selected_by` == `"attention-desc"` — a new field. Two orders, two
  fields: `order` is what position means, `selected_by` is what the cap kept.
- every `data.list[i]` carries `attention_rank`, an integer.
- `data.list[*].id` is **strictly ascending**. This is the whole fix.

Record the full id list -> **`<IDS0>`**, and `data.total` -> **`<TOTAL>`**.

### A2. The opt-out still exists
```json
{"id":"a2","op":"pawns","args":{"filter":"colonist","order":"attention"}}
```
```
rwa pawns --filter colonist --order attention
```
Expect `data.order` == `"attention-desc"`, `data.selected_by` ==
`"attention-desc"`, and `data.list[i].attention_rank == i` for every `i` —
under this order the rank IS the index, which is the definition of the two
agreeing.

The **set** of ids must equal `<IDS0>` as a set. Only the sequence differs.
(On a calm colony where every colonist has the same attention score the two
sequences may be identical — that is not a failure, it is the id tie-break
inside the attention sort. A3 forces them apart.)

### A3. A bad `order` is a bad-args, not a silent fallback
```json
{"id":"a3","op":"pawns","args":{"filter":"colonist","order":"name"}}
```
```
rwa pawns --filter colonist --order name
```
Expect `ok:false` and an error naming the legal values `id|attention`.

---

## B — the two orders really are different orders

### B1. Make one colonist urgent (discovery)
Pick the colonist with the **HIGHEST** id from `<IDS0>` — i.e. the LAST entry
of A1's list — as **`<LOUD>`**. Choosing the last one is deliberate: under
`id-asc` it sits at the end, so when attention lifts it to the front the two
orders cannot coincide.

```json
{"id":"b1","op":"dev:set-need","args":{"pawn":"pawn:<LOUD>","need":"Mood","val":0.05}}
```
```
rwa dev:set-need --pawn pawn:<LOUD> --need Mood --val 0.05
```
Expect `ok:true` and `data.dev.cheat:true`.

> A mood floor of 0.05 gives `Attention` a `100 - 5 = 95` point term, which
> beats any healthy colonist's mood term and needs no injury. If the pawn
> breaks into a mental state that is fine — it only scores higher.

### B2. Attention moved; id did not
```json
{"id":"b2","op":"pawns","args":{"filter":"colonist","order":"attention"}}
```
```
rwa pawns --filter colonist --order attention
```
Expect `data.list[0].id` == `<LOUD>`.

```json
{"id":"b3","op":"pawns","args":{"filter":"colonist"}}
```
```
rwa pawns --filter colonist
```
Expect:

- `data.list[*].id` still exactly `<IDS0>` — **byte-for-byte the same sequence
  as A1.** The colony's most urgent pawn changed and the roster order did not.
  This is the property 3.4 needed and did not have.
- the entry whose `id` is `<LOUD>` now has `attention_rank == 0`, while still
  sitting LAST in the list. Urgency travels on the line, not in the position.

### B4. Put it back
```json
{"id":"b4","op":"dev:set-need","args":{"pawn":"pawn:<LOUD>","need":"Mood","val":0.8}}
```
```
rwa dev:set-need --pawn pawn:<LOUD> --need Mood --val 0.8
```

---

## C — the issue's own test: spawn a pawn, re-read, nothing permutes

### C1. Baseline
```json
{"id":"c1","op":"pawns","args":{"filter":"colonist"}}
```
```
rwa pawns --filter colonist
```
Record `data.list[*].id` -> **`<IDSPRE>`** and `data.list[0].at` -> **`<HOME>`**.
`<IDSPRE>` should equal `<IDS0>`; if it does not, someone joined or died
mid-run and C is measuring the wrong thing — restart C.

### C2. Spawn a colonist
```json
{"id":"c2","op":"dev:spawn-pawn","args":{"kind":"Colonist","faction":"player","pos":"<HOME>","name":"Ordertest Newcomer","spread":3}}
```
```
rwa dev:spawn-pawn --kind Colonist --faction player --pos <HOME> --name "Ordertest Newcomer" --spread 3
```
(`<HOME>` is an `[x,z]` pair in the JSON envelope; write it to `rwa` as the
string `"x,z"` — values are never split on commas.)
Read `data.pawns[0].id` -> **`<NEW>`**.

Expect `<NEW>` to be **greater than every id in `<IDSPRE>`**.
`Verse/ThingIDMaker.GiveIDTo` takes `Find.UniqueIDsManager.GetNextThingID()`,
which is a scribed counter that only increments (`RimWorld/UniqueIDsManager.cs`
`GetNextID`), so a freshly generated pawn always outranks every pawn already in
the world. Two documented escapes exist and neither is reachable here: the
counter wraps to 0 at `int.MaxValue` with a `Log.Warning`, and a `GetNextID`
during a broken `LoadingVars` returns `Rand.Int`. If `<NEW>` is NOT the largest,
do not fail the step — check the journal for either warning first and report it,
because that is a much bigger finding than this issue.

### C3. Re-read — THE ASSERTION
```json
{"id":"c3","op":"pawns","args":{"filter":"colonist"}}
```
```
rwa pawns --filter colonist
```
Expect:

- `data.list[*].id` == `<IDSPRE>` **with `<NEW>` appended, in that order.**
  Not "the same set", not "the same relative order" — literally `<IDSPRE>`
  followed by one new element. The pre-existing entries did not move at all,
  and the new pawn appended rather than inserting into the middle, which is
  the exact behaviour the issue reported as missing.
- `data.total` == `<TOTAL>` + 1.

> Weaker fallback if the roster was at `cap` (20 by default) before C2:
> `<NEW>` may have displaced the least-urgent colonist out of the result
> entirely. In that case assert instead that the ids present are a
> subsequence of `<IDSPRE> ++ [<NEW>]` in the same order, and that
> `data.more` grew by one. On a normal fixture (`<TOTAL>` well under 20)
> the strict form above is the one to run.

### C4. The cap still cuts by attention, not by id
```json
{"id":"c4","op":"pawns","args":{"filter":"colonist","cap":2}}
```
```
rwa pawns --filter colonist --cap 2
```
Expect exactly 2 entries, `data.more` == `<TOTAL>` + 1 − 2, `data.order` ==
`"id-asc"`, and `data.list[*].attention_rank` == `[0,1]` in some arrangement —
**the two surviving entries are ranks 0 and 1, not the two lowest ids.** Their
`id`s are still ascending relative to each other.

To make this unambiguous rather than accidental, redo B1 on `<LOUD>` (the
highest id) first and then run C4: `<LOUD>` must be one of the two survivors
despite having the largest id in the colony. That is the proof that the id
sort runs AFTER the cut and not before it — the failure mode that would quietly
undo spec 2.6.

### C5. Clean up
```json
{"id":"c5","op":"dev:destroy","args":{"thing":<NEW>}}
```
```
rwa dev:destroy --thing <NEW>
```
Optional; leaves the fixture as found.

---

## D — standing checks

### D1. No red errors across the whole run
```json
{"id":"d1","op":"journal","args":{"since_seq":<SEQ0>,"types":["red_error"]}}
```
```
rwa journal --since_seq <SEQ0> --types red_error
```
Expect `data.count` == 0. `pawns` is an observer and must not have written
anything; in particular it must not have touched
`Pawn_PlayerSettings.displayOrder` (it does not read it at all — see the
DESIGN entry for why that field is a trap).

---

## What this does NOT prove

- **Nothing here was executed.** The worker cannot launch the bench. Every
  expectation above is derived from the source (`PawnVerbs.Pawns`,
  `PawnSerializer.Attention`, `Verse/MapPawns.cs`, `RimWorld/UniqueIDsManager.cs`)
  and from a clean `-c Release` build. The first real evidence is this run.
- **B1's mood floor is an assumption about the fixture.** If the colony's
  colonists are already in bad shape, `<LOUD>` may not reach rank 0 by mood
  alone. `dev:damage` or `dev:add-hediff` to force a `downed` (+1000) is the
  bigger hammer if B2 does not separate the orders.
- **C3's strict form assumes the roster is under `cap`.** See the fallback.
- **Nothing here proves a stable handle ABOVE the cap, and nothing can, because
  there is not one.** Every step runs on a roster with `more == 0`. The cap
  still cuts by attention (C4 proves that it does, deliberately), so with
  `total > cap` the surviving SET moves as moods move and `list[0]` can still
  name a different pawn between reads. That is the documented limit of the
  contract, not a gap in this run — `more` is the flag, and a caller wanting a
  durable handle raises `cap` past `total` or holds the `id`.
- **C4's parenthetical "`<LOUD>` … having the largest id in the colony" is stale
  by then**: C2 spawned `<NEW>`, whose id is larger. The assertion that matters
  is unaffected — `<LOUD>` must be one of the two survivors despite sorting late
  under `id-asc` — but do not fail the step on the word "largest". Expect the
  two survivors to be `<LOUD>` (mood 5% -> ~95 attention) and `<NEW>` (a freshly
  generated pawn starts at mood 50% -> ~50), with everyone else at high mood
  scoring ~20.
- The `dev:destroy` cleanup in C5 vanishes a colonist; on a fixture you intend
  to keep, skip it and let the pawn stay.
