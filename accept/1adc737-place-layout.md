# Acceptance — 1adc737, `place-layout` / `cancel-layout`

Runnable driver: `accept/1adc737-place-layout.py` — five phases plus phase 0,
the shape contract that asserts every envelope key the later phases dig on. No
`.ps1` twin; the bench lives on this box. **No check count is recorded here on
purpose:** counts go stale within a session, and the tables below are the
contract.

    ./accept/1adc737-place-layout.py --dry-run   # the plan, sends nothing
    ./accept/1adc737-place-layout.py             # against a live bench
    ./accept/1adc737-place-layout.py --phase 4   # one phase (0 always runs)

**Fixture:** the agent bench (`_RimWorld-Agent/run-agent.sh`) and a colony on a
map with open buildable ground. The driver sites itself with `find-rect` — a
9×11 box for a 5×7 room, which is the two-to-three-times-the-footprint siting
discipline session 13 settled on — and takes a fresh site for each of phases
2/3, 4 and 5. With no clear box it exits 2 as a fixture gap rather than failing.
Paused is fine; nothing here needs time to pass.

**It dirties the bench.** Phase 4 spawns a real 5×7 room in instant mode and
does **not** remove it, because removing it would destroy the very evidence
`site-audit` was asked about. Phase 5 leaves nothing (it cancels what it
places), and phases 1–3 leave nothing.

**Exit codes:** 0 all passed · 1 at least one FAIL · 2 a fixture precondition
could not be met, which is not a spec failure.

**`--dry-run` proves the plan, never the paths.** It sends nothing, so every
envelope is empty and every wrong `dig()` path looks fine — which is why it
refuses to print the word *passed*. Only a live run tells you whether the
envelopes are the shape the assertions assume, which is what phase 0 exists for.

---

## The four claims, and which phase settles each

| claim | phase | how it is settled |
|---|---|---|
| **Atomicity** — one refusing cell places nothing | 1 | a self-overlapping layout, then a layout over a hand-placed wall; the witness is `construction` over the layout's rect, **not** the verb's own `placed_count` |
| **The corner contract** — `at` is the footprint's north-west cell | 2 | the 1×2 `Bed_South`: token at `(ox+2, oz+5)`, footprint `[ox+2, oz+4, 1, 2]`. For a 1×2 def the north-west cell and the south-west corner differ by exactly one, so a wrong conversion is one cell off and *visible* |
| **Instant ≡ blueprint** | 4 | `site-audit` over the layout's rect returns `hit_count: 0` — every placed building is somewhere a blueprint would have been accepted, so the state is one blueprint mode could have produced |
| **The undo** | 3 | `cancel-layout {layout_id}` twice: 22 cancelled, then 0 with every row `not-present` and every placement reading `cancelled` rather than `built` |

## Phase 5 is the interesting one

`--partial` is the only door out of the atomicity invariant, and it is checked
that it opens exactly one cell wide: 21 placed, 1 skipped, `ok` still **false**
because something the caller asked for did not happen.

The rollback fixture is chosen so it **must** fire.
`RimWorld/PlaceWorker_NeverAdjacentTrap.AllowsPlacing` walks its own occupied
rect `ExpandedBy(1)` and refuses on any trap building, blueprint **or frame** it
finds there. Two `TrapSpike` elements in adjacent cells therefore:

* do not overlap, so the self-overlap check is silent;
* have no interaction cells, so the interaction check is silent;
* each pass `SiteGate` individually against empty ground.

The refusal can only appear **after the first one is placed** — which is exactly
the case no preflight against the pre-placement map can see, and exactly what
the rollback exists for. The assertion is that the call places nothing, gets no
layout id, reports the late refusal separately, and that `construction` agrees
the ground is clear.

`TrapSpike` has no research prerequisite, is 1×1 and is made from stuff, so it
is available on a fresh colony. If the bench's own ground refuses it, phase 5
says the rollback claim is **unproven by that run** and moves on — an honest
"not demonstrated" rather than a failure.

## What this suite deliberately does NOT prove

* **That pawns then build the blueprints.** That needs `advance` over game-days
  and is 4.2's play loop, not a placement check.
* **`role: "Bedroom"`.** It will not be, and correctly:
  `RimWorld/RoomRoleWorker_Bedroom` → `IsBedroom` → `IsBedroomHelper` reads
  `bed.OwnersForReading`, and an unowned bed with `bed_emptyCountsForBarracks`
  scores as **Barracks**. Colonists claim beds by sleeping in them, so this is
  not something a verb sets (git-bug `1adc737` #4). Reported as a NOTE with the
  reason, never as a check.
* **The roof.** `Verse/AutoBuildRoofAreaSetter.TryGenerateAreaFor` only QUEUES
  (`queuedGenerateRooms.Add(room)`); `TryGenerateAreaNow` runs from
  `AutoBuildRoofAreaSetterTick_First`, i.e. **next tick**. A same-call read sees
  nothing and would report a correct implementation as broken. Advance one tick
  first. Reported as a NOTE.
* **The `rwa place-layout` CLI.** The IR expansion is the client half and is
  covered offline by `rwa/selftest.sh` section 13 — the coordinate map, the
  token split, the stuff-map merge and the refusals, none of which need a game.
  This driver builds its own payload so it runs from a bare checkout.

## The inline grid is the template, and check 0.6 enforces it

The 5×7 bedroom lives inline in the driver, because every file in `accept/`
stands alone and runs from a bare checkout — that is what makes acceptance
portable across two benches with different tooling. Check **0.6** then compares
it against `templates/bedroom.ir.json`, so a rehearsal of a layout that has
drifted from the template it is supposed to rehearse fails loudly instead of
quietly rehearsing nothing.

## The watermark idiom

Phase 0 reads the journal watermark as `{since_seq: 999999999, limit: 1}`, not
`{limit: 1}`. `JournalVerbs.Read` updates `last_seq` **before** the
`seq <= since_seq` skip and breaks on `events.Count >= limit` **before**
appending, so `{limit: 1}` reports the *second* row's seq.
`accept/s13-mod-surface.py` was the one suite that never got this fix, and two
zero-failure runs (sessions 13 and 15) scored zero only because nothing happened
to precede them. Phase 5's "no red errors" check is scoped to that watermark for
the same reason.
