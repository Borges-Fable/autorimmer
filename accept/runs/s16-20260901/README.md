# Session 16 — the session-B bench pass

Bench `_RimWorld-Agent`, `--quicktest` 250x250 desert, unattended (640x480
windowed). Assembly `8b804c5` for the pass, then `479e4a1` after the one fix it
forced. Verb surface **123** — `build`, `construction`, `site-audit` live.

`accept/s13-mod-surface.py`: **174 PASS / 0 FAIL / 1 SKIP / 3 NOTE**.

## The lifecycle, end to end, on one placement id

    build {def:"Wall", stuff:"WoodLog", pos:[120,120]}
      -> Blueprint_Wall 18072, placement_id "pl-1", journal seq 33
    construction -> "awaiting-materials", needed 5 / present 0 / still_wanted 5
    (colonist hauls)  -> frame, "ready", present 5 / still_wanted 0
    (colonist builds) -> journal seq 55 {"kind":"completed","worker":"Lizzie",
                          "thing_id":18369,"placement_id":"pl-1"}
    construction {placement_id:"pl-1"} -> "built", completed_tick 37157,
                                          thing_id 18369
    construction {placement_id:"pl-2"} -> "cancelled", thing_id null

`built` and `cancelled` distinguishable **by field**, after both the blueprint
and the frame are gone. That was the worker's own highest-risk item — the
postfix takes its map from `worker?.Map` because `__instance.Map` is null there,
and had that been wrong every finished build would read `cancelled`.

## Everything else proven

`buildable:true` refuses what the god-hand accepts ("Identical thing already
exists here." — a `CanPlaceBlueprintAt` check `CanSpawnAt` lacks) · `site-audit`
returns `hit_count: 2` against a deliberately staged pair of
interaction-overlapping benches, `0` over clean ground · `site-survey`'s verdict
and `build`'s refusal are the SAME SENTENCE for the same arguments · a tolerated
chair is listed under `occupants` with `tolerated: true` · the rotation search
falls through to East/West in a corridor where North is refused for "Space
already occupied." · the widget gate proven BOTH ways on one map, refusing
`HiTechResearchBench` without searching (`examined: 0`) before
`dev:finish-research` and allowing it after · `dev:starter-kit --preset medical`
stages two beds that both survive.

## Three findings

**1. `world-fixture`'s site search did not share its new gate.** Session B gave
the `bench` step a gate and left `FindClearRect` as the search; the search
returns the box beside an existing table, whose footprint sits on that table's
interaction cell, and the gate then refuses. The step died on its SECOND call,
making `0d9cbd7`'s own acceptance unreachable for a reason unrelated to the bug
it was filed about. Fixed (`479e4a1`, `FindGatedSite`): three consecutive calls
now give three benches with two bills each.

**2. `3a5ff6c`'s `wiped[]` bullet is unreachable as written.**
`GenSpawn.CanSpawnAt` tests `!c.Walkable(map)` on the CENTRE cell inside the
loop over the occupied rect, so any unwalkable occupant refuses before
`canWipeEdifices` is consulted. Four attempts including the bullet's own
rock-wall case, all "the cell is not walkable". The field is honest; the example
cannot occur.

**3. THE SUITE'S RED-ERROR CHECK HAS NEVER BEEN SCOPED TO THE RUN IT GRADES.**
The pass first reported 3 FAILs, all "zero red errors". The only red error on
the bench was `"[AutoRimmer] selftest-induced red error (deliberate)"` — emitted
by design, by a `journal-selftest` call in this session's own smoke pass.

`s13-mod-surface.py` was the one suite that never received session 11's
watermark fix. It read `journal {limit: 1}`, and `JournalVerbs.Read` updates
`last_seq` BEFORE the `since_seq` skip and breaks on `events.Count >= limit`
BEFORE the append — so it reports the SECOND row's seq. The run printed
`seq0 = 2` against a 108-row journal. `3.4-pawn-orders`, `3.6`, `4087644` and
`1.8` all use the corrected `{since_seq: 999999999, limit: 1}`.

**Sessions 13 (169/0) and 15 (174/0) scored zero failures only because no red
error happened to precede them.** Fixed in `19d6446`; same bench, same
assembly, seq0 = 108, 174 PASS / 0 FAIL.

## Not demonstrated

`state:"blocked"` and `blocking_is_pawn` — nothing was dropped on a blueprint,
so `GenConstruct.FirstBlockingThing`'s pawn clause is guarded by reading only.
The `letter`/`open-letter` chain. `site-audit` over the M1 save.

## Two process notes

**A shell cwd survives a deleted worktree.** After the session-B agent's
worktree was auto-cleaned, this session's persistent shell was still inside it;
`git merge` then reported "already up to date" and the assembly looked dirty.
Nothing was damaged, and only the nonsensical merge result gave it away.

**Wrong dig paths print confident zeros.** `things --def X` reports under
`rollups`, `site-audit` under `hits` — one-liners looking for `list` or
`findings` said "0" both times. The shape discipline `accept/` enforces on
checks does not extend to the orchestrator's own shell, and on this evidence it
should.
