# git-bug 70ac258 acceptance — `things` and `fires` return a stable, documented order

Run by the ORCHESTRATOR against the live `_RimWorld-Agent` bench. The worker
wrote and compiled this and never launched the game.

Every step is one protocol envelope. Write it to `commands/<id>.json` and read
`results/<id>.json`, or use the `rwa` line beside it — they are the same call.

**Every `advance` below needs the two per-call escapes** (git-bug `722c951`).
`advance` now refuses with `error.code:"unread-journal"` when the previous
advance journaled events no `journal` call has read, and halts with
`reason:"casualty"` on an own-faction downing or death. Both are right for a
play loop and wrong for a scripted fixture, which advances to move state and
never reads the journal in between — so add to every advance envelope below:

```json
"unread_ok": "<this file>: fixture, not a play loop",
"through_casualties": "<this file>: fixture, not a play loop"
```

or `--unread_ok:str '<why>' --through_casualties:str '<why>'` on the `rwa`
line. The reason is REQUIRED and non-empty, and the mod journals it as an act.
The `.py`/`.ps1` twin of this file does it in one `advance()` wrapper; by hand
it has to go on each call. Nothing else about the steps changes.

**Preconditions.** A loaded colony on `_RimWorld-Agent`; `devMode` on for the
`dev:*` staging in D; no force-pausing window up (`rwa status`); and a def with
at least **three** loose or stockpiled stacks on the map, so a re-order is
actually observable (with two, any permutation is one swap and half of them look
like the identity — 1eb2262's lesson, same words). A0a finds that def.

**What is being asserted.** Four separate claims and they need four different
sections:

| claim | proved by |
|---|---|
| the detail list is emitted `thingIDNumber` ascending, and says so | A1 |
| a score change with NO add/remove does not permute it | C1 → C3 |
| the cap still keeps the urgent item, not the lowest ids | B2 |
| the fire list got the same treatment on BOTH routes | D1, D4, D5 |

**Standing assertions on every step**: `ok:true` (except A3/D0b, which assert
`ok:false`), and no `red_error` in the journal (checked once at the end, E1).

---

## A — the shape of the `things` detail result

### A0. Journal watermark (discovery)
```json
{"id":"a0","op":"journal","args":{"since_seq":999999999,"limit":1}}
```
```
rwa journal --since_seq 999999999 --limit 1
```
Read `data.last_seq` -> **`<SEQ0>`**. E1 reads forward from it, so a red error
that predates this run is not charged to it.

> `{"limit":1}` alone is the WRONG envelope and it silently ruins E1 —
> `JournalVerbs.Read` breaks at the second line of the file and reports that
> line's seq. Pushing `since_seq` past the end makes every line fail the filter,
> so the scan reaches EOF and `last_seq` is the true maximum. See
> `accept/1eb2262-stable-pawn-order.md` A0 for the full reasoning.

### A0a. Pick the fixture def (discovery)
```json
{"id":"a0a","op":"things","args":{"category":"resources","cap":20}}
```
```
rwa things --category resources --cap 20
```
Read the rollups. Choose a def with `stacks >= 3` — `Steel`, `WoodLog` and
`RawPotatoes` are the usual candidates — and call it **`<DEF>`**. Note its
`stacks` count.

If nothing has three stacks, stage them:
`rwa dev:spawn-thing --def Steel --count 150 --pos <SOMEWHERE>` places three
75-stack piles (`dev:spawn-thing` splits at `def.stackLimit`), then re-run A0a.

### A0b. Pin the cap above the total (discovery)
```json
{"id":"a0b","op":"things","args":{"def":"<DEF>","detail":true,"detail_cap":300}}
```
```
rwa things --def <DEF> --detail --detail_cap 300
```
Read `data.things_total` -> **`<TOTAL>`** and `data.things_more`.

**`data.things_more` MUST be 0 here.** Everything in C depends on it: above the
cap, membership moves with the score BY DESIGN (spec 2.6 — the cap must never
hide the urgent item), so a stability check run over a truncated list fails for
the correct reason. That is the amendment the orchestrator's ruling made to this
issue's acceptance. If `<TOTAL>` is over 300, pick a narrower `<DEF>`; the cap
argument cannot go higher.

### A1. Default read — THE FIX
```json
{"id":"a1","op":"things","args":{"def":"<DEF>","detail":true,"detail_cap":300}}
```
```
rwa things --def <DEF> --detail --detail_cap 300
```
Expect:

- `data.things_order` == `"id-asc"` — **this is the default now.** 2.4 shipped
  `"attention-desc"` here; if you see that word without having asked for it, the
  old assembly is loaded.
- `data.things_selected_by` == `"attention-desc"` — a new field. Two orders, two
  fields: `things_order` is what position means, `things_selected_by` is what
  the cap kept.
- every `data.things[i]` carries `attention_rank`, an integer.
- `data.things[*].id` is **strictly ascending**. This is the whole fix.
- `data.order` is still `"attention-desc"` and `data.rollups` is unchanged —
  the def-keyed summary was deliberately left alone (the ruling, point 1), and
  the bare `order` belongs to it, not to the detail list.

Record the full id list -> **`<IDS0>`**.

### A2. The ranked view still exists
```json
{"id":"a2","op":"things","args":{"def":"<DEF>","detail":true,"detail_cap":300,"order":"attention"}}
```
```
rwa things --def <DEF> --detail --detail_cap 300 --order attention
```
Expect `data.things_order` == `"attention-desc"`, `data.things_selected_by` ==
`"attention-desc"`, and `data.things[i].attention_rank == i` for every `i` —
under this order the rank IS the index, which is the definition of the two
agreeing.

The **set** of ids must equal `<IDS0>` as a set. Only the sequence differs. (On
a fixture where every stack has the same size, hit points and forbidden state,
the two sequences are identical — that is not a failure, it is the id tie-break
inside the attention sort. C2 forces them apart.)

### A3. A bad `order` is a bad-args, not a silent fallback
```json
{"id":"a3","op":"things","args":{"def":"<DEF>","order":"size"}}
```
```
rwa things --def <DEF> --order size
```
Expect `ok:false`, error code `bad-args`, and a message naming the legal values
`id|attention`.

---

## B — the cap still cuts by attention, not by id

### B1. Make one stack urgent, without adding or removing anything
Pick the entry with the **HIGHEST** id from `<IDS0>` — the LAST entry of A1's
list — as **`<LOUD>`**. Choosing the last is deliberate: under `id-asc` it sits
at the end, so when attention lifts it to the front the two orders cannot
coincide.

```json
{"id":"b1","op":"forbid","args":{"things":[<LOUD>]}}
```
```
rwa forbid --things:json '[<LOUD>]'
```
Expect `ok:true` and one accepted target. `ThingAttention` adds `+100000` for
`IsForbidden(Faction.OfPlayer)`, which dominates every other term — so this is a
score change of ~100000 points with **nothing added to or removed from the map**,
which is exactly the condition this issue's acceptance asks for.

### B2. The cap keeps the urgent one — `detail_cap:1`
```json
{"id":"b2","op":"things","args":{"def":"<DEF>","detail":true,"detail_cap":1}}
```
```
rwa things --def <DEF> --detail --detail_cap 1
```
Expect:

- exactly ONE entry, and `data.things[0].id` == `<LOUD>` — **not the lowest id.**
  This is the proof that the id sort runs AFTER the cut, not before it; a
  re-sort applied to the candidate set would quietly undo spec 2.6.
- `data.things[0].forbidden` == `true`.
- `data.things[0].attention_rank` == 0.
- `data.things_order` == `"id-asc"`, `data.things_selected_by` ==
  `"attention-desc"`, `data.things_more` == `<TOTAL>` − 1.

---

## C — a score change does not permute the sequence

### C1. Re-read at the pinned cap — THE ASSERTION
```json
{"id":"c1","op":"things","args":{"def":"<DEF>","detail":true,"detail_cap":300}}
```
```
rwa things --def <DEF> --detail --detail_cap 300
```
Expect `data.things[*].id` **byte-for-byte `<IDS0>`** — the same ids in the same
order as A1, taken after a 100000-point score change. `data.things_total` is
still `<TOTAL>`, `data.things_more` still 0.

The entry whose `id` is `<LOUD>` now has `attention_rank == 0` while still
sitting LAST in the list. Urgency travels on the line, not in the position.

### C2. The ranked view moved, so the two orders really are different
```json
{"id":"c2","op":"things","args":{"def":"<DEF>","detail":true,"detail_cap":300,"order":"attention"}}
```
```
rwa things --def <DEF> --detail --detail_cap 300 --order attention
```
Expect `data.things[0].id` == `<LOUD>`. C1 and C2 read the same population
milliseconds apart and disagree about position — which is the point: one is a
register and one is a ranking, and each now says which it is.

### C3. The environmental version of the same check (optional, weaker)
```json
{"id":"c3a","op":"advance","args":{"ticks":2500,"max_tps":600}}
```
```
rwa advance --ticks 2500 --max_tps 600
```
then repeat C1. Expect the id sequence to be a **subsequence of `<IDS0>` in the
same relative order**, plus any newly spawned ids in ascending position — not
"the same set", the same ORDER. Over 2500 ticks haulers merge and split stacks
(moving the `stackCount` term) and unroofed stock deteriorates (moving the
hp term), while things are also genuinely created and destroyed, which is why
this check is stated on the intersection and C1 is the one that proves the
property.

### C4. Put it back
```json
{"id":"c4","op":"unforbid","args":{"things":[<LOUD>]}}
```
```
rwa unforbid --things:json '[<LOUD>]'
```

---

## D — `fires`, on both routes into `FireScan`

D1 and D2 need no fire at all and should be run first; they are the checks that
survive if the staging in D3 turns out not to work.

### D1. The `fires` verb publishes two facts
```json
{"id":"d1","op":"fires","args":{}}
```
```
rwa fires
```
Expect `data.order` == `"id-asc"` (it was `"outside-home-then-size-desc"` before
this fix — that word appearing under the DEFAULT means the old assembly is
loaded) and `data.selected_by` == `"outside-home-then-size-desc"`.

### D2. `things`' own fire block is the same object
```json
{"id":"d2","op":"things","args":{"category":"resources"}}
```
```
rwa things --category resources
```
Expect `data.fire.order` == `"id-asc"` and `data.fire.selected_by` ==
`"outside-home-then-size-desc"` — identical to D1. Before this fix the two
routes would have answered to different contracts, which is the failure the
ruling names in point 1.

Then, with the order argument carried through:
```json
{"id":"d2b","op":"things","args":{"category":"resources","order":"attention"}}
```
```
rwa things --category resources --order attention
```
Expect `data.fire.order` == `"outside-home-then-size-desc"` — the `things`
argument reaches the embedded fire list, and the ranked view names its real
rule rather than borrowing the word "attention".

### D0b. A bad `order` on `fires` is a bad-args too
```json
{"id":"d0b","op":"fires","args":{"order":"size"}}
```
```
rwa fires --order size
```
Expect `ok:false` naming `id|attention`.

### D3. Stage two fires — **DANGEROUS, read this first**
> A fire spreads. Stage on **stone or dirt, away from anything wooden**, destroy
> both within a few steps, and do not `advance` between D3 and D6. If the colony
> is flammable anywhere near the chosen cells, skip D3–D5 entirely: D1, D2 and
> D0b already prove the contract's shape, and D4's ordering claim is the only
> thing lost.
>
> Pick **`<INSIDE>`**, a cell INSIDE the home area, and **`<OUTSIDE>`**, a cell
> OUTSIDE it (`rwa areas` gives the home area; `map-view` gives bare ground).
> Spawn the INSIDE one FIRST so it gets the LOWER id — that is what makes D4's
> assertion non-trivial.

```json
{"id":"d3a","op":"dev:spawn-thing","args":{"def":"Fire","pos":"<INSIDE>","mode":"direct","count":1}}
```
```
rwa dev:spawn-thing --def Fire --pos <INSIDE> --mode direct --count 1
```
Read `data.spawned[0].id` -> **`<FIN>`**.
```json
{"id":"d3b","op":"dev:spawn-thing","args":{"def":"Fire","pos":"<OUTSIDE>","mode":"direct","count":1}}
```
```
rwa dev:spawn-thing --def Fire --pos <OUTSIDE> --mode direct --count 1
```
Read `data.spawned[0].id` -> **`<FOUT>`**, which must be greater than `<FIN>`.

> **UNVERIFIED, and the most likely step to fail.** The worker could not check
> whether `Fire` passes `DebugThingPlaceHelper.IsDebugSpawnable` — that test
> keys on `ThingDef.category`, and the Core def XML is not in the decompiled
> reference on BORGES (the guard itself is at
> `Verse/DebugThingPlaceHelper.cs IsDebugSpawnable`, and `Ethereal` passes it).
> If the call is refused with that reason, retry with `--force true`. If it is
> still refused, or `data.placed` is 0, the vanilla route is
> `RimWorld/FireUtility.TryStartFireIn` — which AutoRimmer does not expose, so
> report it and stop at D2 rather than reaching for another mechanism.

### D4. Both fire assertions in one read
```json
{"id":"d4","op":"fires","args":{}}
```
```
rwa fires
```
Expect:

- `data.list[*].id` **strictly ascending**, i.e. `<FIN>` then `<FOUT>`.
- the entry whose id is `<FOUT>` has `attention_rank` == 0 while sitting SECOND
  — the fire outside the home area outranks the one inside it at equal size
  (`+10f`), because the inside one already has `Alert_FireInHomeArea` and the
  outside one is a blind spot. Position no longer carries that, the field does.
- `data.count` == 2, `data.more` == 0.

```json
{"id":"d4b","op":"fires","args":{"order":"attention"}}
```
```
rwa fires --order attention
```
Expect `data.list[0].id` == `<FOUT>`, `data.order` ==
`"outside-home-then-size-desc"`, and `data.list[i].attention_rank == i`.

### D5. The `things` route agrees, live
```json
{"id":"d5","op":"things","args":{"category":"resources"}}
```
```
rwa things --category resources
```
Expect `data.fire.list[*].id` ascending and the same two entries as D4 — the
same list, reached the other way.

### D6. Put the fires out
```json
{"id":"d6","op":"dev:destroy","args":{"things":[<FIN>,<FOUT>]}}
```
```
rwa dev:destroy --things:json '[<FIN>,<FOUT>]'
```
One call, both fires — `dev:destroy` takes the plural (`things:[id,…]`), and a
fire is not a thing to put out one round trip at a time. `mode` defaults to
`vanish`: no leavings, no letter.
Then re-run D1 and expect `data.count` == 0. **Not optional** — a fire left
burning on an unattended bench is how a fixture becomes a crater.

---

## E — standing checks

### E1. No red errors across the whole run
```json
{"id":"e1","op":"journal","args":{"since_seq":<SEQ0>,"types":["red_error"]}}
```
```
rwa journal --since_seq <SEQ0> --types red_error
```
Expect `data.count` == 0. `things` and `fires` are observers: the only
game-state writes in this run are B1/C4's `forbid`/`unforbid` and D3/D6's
`dev:*`, all of them deliberate.

---

## What this does NOT prove

- **Nothing here was executed.** The worker cannot launch the bench. Every
  expectation above is derived from the source (`ThingVerbs.Things`,
  `ThingVerbs.Rollups`, `ThingVerbs.FireScan`, `ThingVerbs.ThingAttention`,
  `RimWorld/Fire.cs`, `Verse/DebugThingPlaceHelper.cs`) and from a clean
  `-c Release` build with 0 warnings. The first real evidence is this run.
- **D3's fire staging is unverified** and may simply be refused — see the note
  there. D1, D2 and D0b do not depend on it.
- **Nothing here proves a stable handle ABOVE the cap, and nothing can, because
  there is not one.** A0b pins `things_more` to 0 on purpose. The cap still cuts
  by the live score (B2 proves that it does, deliberately), so with
  `things_total > detail_cap` the surviving SET moves as hit points and stack
  counts move, and `things[0]` can still name a different thing between reads.
  That is the documented limit of the contract, not a gap in this run:
  `things_more` / `more` is the flag, and a caller wanting a durable handle
  raises the cap past the total or holds the `id`.
- **C3 is environmental and is not a pass/fail gate.** It observes that real
  hauling and deterioration churn do not permute the survivors; on a quiet
  colony it may show no churn at all, which proves nothing either way.
- **`rollups` is untested here because it is unchanged** — it stays
  `attention-desc`, def-keyed, by the ruling. A1's last bullet is the only
  check that it did not move.
- **The `by_location` view is not exercised.** It runs the same builder three
  times and the `order` argument reaches all three, but no step here reads
  `things {category:"apparel"}` to see it.
