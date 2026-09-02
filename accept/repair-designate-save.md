# Bench acceptance — b7359fa, 855117a, bb931b9

Branch `fix/b7359fa-designate-reach`. Run these in order on `_RimWorld-Agent`
with a colony loaded and **PAUSED**. Paste the output back.

**Substitute for yourself:** `<C1>`/`<C2>` colonist ids, `<ROCK>` a mineable
cell, `<ORE>` a cell whose def is `Mineable*`, `<A_WIDE>`/`<A_NARROW>` area ids
from step 2. Steps 1–3 are setup; the assertions start at 4.

This suite **rebinds every colonist's allowed area** and leaves Mine/MineVein
designations behind. Step 20 puts both back.

---

## Setup

1. `rwa pawns --filter colonists` and `rwa nearest --def MineableSteel --from <C1 pos>`
   (any `Mineable*` def the map has; `map-dump` names them).
   EXPECT a colonist id and at least one ore cell. Note one ore cell as `<ORE>`
   and one plain-rock cell as `<ROCK>`. Also note each colonist's CURRENT area
   from `rwa posture` (no args = pure read) — step 20 restores it.

2. `rwa area --kind allowed --op create --label accWide` then
   `rwa area --kind allowed --op add --id <A_WIDE> --rect <a rect containing ROCK and ORE>`;
   repeat for `accNarrow` over a 4x4 rect **far from** ROCK/ORE.
   EXPECT two area ids. Note them.

3. `rwa posture --pawns '[<C1>,<C2>,…]' --area <A_WIDE> --seek auto --hostility Attack`
   EXPECT every colonist bound to accWide.

## b7359fa — the allowed area is per pawn

4. `rwa designate --type mine --cells '["<ROCK>"]'`
   EXPECT `accepted:1`, `accepted_actionable:1`, `accepted_unreachable:0`, no
   `refused`. `reach.applies:true`, `reach.work_type:"Mining"`,
   `reach.gate` naming `ForbidUtility.InAllowedArea` and
   `EffectiveAreaRestrictionInPawnCurrentMap`, `reach.test` saying it is NOT a
   pathing test. NOT `accepted:1` with no reachability field.

5. `rwa designate --type claim --cells '["<ROCK>"]' --dry-run`
   EXPECT `reach.applies:false` with a `reach.why` sentence, and
   `accepted_actionable`/`accepted_unreachable` **present and null** — not 0.
   (claim produces no pawn work, so there is no area question to ask.)

6. `rwa posture --pawns '[<all colonists>]' --area <A_NARROW> --seek auto --hostility Attack`
   then `rwa designate --type mine --cells '["<ROCK>"]'`
   EXPECT **`refused`**: `refused.code:"outside-every-allowed-area"`, a reason,
   and a `hint` naming `area {kind:"allowed", op:"add"…}`. `accepted:0`,
   `designated:0`, and **`designations_now == designations_before`** — the
   refusal came off a dry preflight and wrote nothing. This is the headline.

7. Same envelope as 6: `reach.areas[]`
   EXPECT a row for `<A_NARROW>` with `id`, `label`, `cells`, the `pawns` bound
   to it, and `excludes` ≥ 1 — the area that shuts the target out is NAMED, not
   left to be inferred.

8. `rwa designate --type mine --cells '["<ROCK>"]' --allow-unreachable`
   (or `--json '{"type":"mine","cells":["<ROCK>"],"allow_unreachable":true}'`)
   EXPECT `accepted:1`, no `refused`, `accepted_unreachable:1`,
   `accepted_actionable:0`, and `reach.warning` containing "outside EVERY".
   The override designates without pretending the target became reachable.

9. `rwa designate --type cancel --cells '["<ROCK>"]'`, then
   `rwa posture --pawns '[<C1>]' --area <A_WIDE> --seek auto --hostility Attack`,
   then `rwa designate --type mine --cells '["<ROCK>"]'`
   EXPECT `accepted_actionable:1`, `accepted_unreachable:0`, **no refusal** —
   ONE colonist with a covering area makes the same batch actionable, while the
   rest are still confined to accNarrow. **This is the per-pawn test: a check
   written against a single colony-wide area cannot produce this result.**
   `reach.pawns[]` should show `can_work:1` for `<C1>` and `0` for the others.

10. `rwa area --kind allowed --op create --label accEmpty` (paint nothing), then
    `rwa posture --pawns '[<C1>]' --area <A_EMPTY> --allow-empty-area true --seek auto --hostility Attack`,
    then `rwa designate --type mine --cells '["<ROCK>"]' --dry-run`
    EXPECT `<C1>`'s `reach.pawns[]` row to read `area_cells:0` and
    **`restricted:false`**, and `reach.unrestricted` ≥ 1. An area with
    `TrueCount == 0` is ignored by the game, so "has an area" is not
    "is restricted".

11. `rwa pawns --filter wildlife` then
    `rwa designate --type hunt --things '[<3 animal ids outside accNarrow>]'`
    with every colonist on accNarrow.
    EXPECT the issue's own line: **a refusal, or a report naming the excluding
    area** — not `accepted: 3` and silence. `reach.work_type:"Hunting"`.
    If some animals are inside, expect a REPORT with
    `accepted_actionable + accepted_unreachable == designated`.

12. `rwa journal --limit 3 --types action` right after any accepted designate
    EXPECT the action row's `counts` to carry `designated`, `actionable` and
    `unreachable` — a post-mortem reads the row, not the envelope the agent
    discarded.

## 855117a — what was designated, and mine-vein

13. `rwa designate --type mine --rect '<a rect over mixed rock>' --allow-unreachable`
    EXPECT a `composition` array: one row per def with `def`, `label`,
    `by:"mineable"`, `count`, and on an ore row `mineable_thing`,
    `mineable_yield` AND `yield_effective` (the difficulty-scaled one).
    `sum(count)` must equal `designated`. On a plain-rock row `mineable_thing`
    must be **absent**, not null.

14. `rwa designate --type cancel --rect '<same rect>'` then
    `rwa designate --type mine-vein --cells '["<ORE>"]'`
    EXPECT `accepted:1`, `designator:"Designator_MineVein"`, and
    **`designated` > 1 if the vein is more than one cell** —
    `designated_from` should read `designation delta (MineVein …)`, and
    `designations_now - designations_before` must equal `designated`.
    **This is the first time mine-vein has ever been exercised.** If
    `designated == 1`, say so — the vein was one cell wide and the flood fill
    is NOT demonstrated.

15. Same envelope: `composition`
    EXPECT exactly ONE row (the flood fill stops at any cell whose edifice def
    differs), with `vein_mineable:true` and a `mineable_thing`.

16. `rwa designate --type mine --cells '["<ORE>"]'` (the vein cell from 14)
    EXPECT `accepted:0` and a reject row with
    **`why:"already-designated-other"`** and
    **`designation_present:"MineVein"`** — NOT `not-designatable`, which is what
    it used to say and reads identically to "this rock is not mineable".
    `rejects_by_reason` must key on `already-designated-other` and must NOT
    contain `not-designatable`.

17. `rwa designate --type cancel --cells '["<ORE>"]'`,
    `rwa designate --type mine --cells '["<ORE>"]'`,
    then `rwa designate --type mine-vein --cells '["<ORE>"]'`
    EXPECT the mine-vein call to be ACCEPTED (its gate has no such clause) and
    the envelope to carry **`replaced`**: a row naming `Mine`, how many cells
    lost it, the cells, and why. The Mine count really falls; until now that
    was silent.

## bb931b9 — the save verb

18. `rwa save --name repair-check-1` then `rwa digest` for the tick
    EXPECT `ok:true`, `path` ending `Saves/repair-check-1.rws`, `written:true`,
    `bytes` in the megabytes, `tick` **equal to `digest.time.tick`** (the game
    is paused, so it cannot move), and `sid` matching `rwa version`.
    `autosave_slots.slots[]` lists `Autosave-1..N` with their current sizes.
    Confirm the file exists on disk.

19. Four refusals, one line each:
    - `rwa save --name repair-check-1` again → **`bad-args`**, detail says
      "already exists" and names `overwrite:true`.
    - `rwa save --name repair-check-1 --overwrite` → `ok:true`,
      `overwrote:true`, `bytes_before` an integer.
    - `rwa save --name Autosave-3` → **`bad-args`** citing `IsAutoSave` and
      `Autosaver`. Then re-run step 18 and confirm every `autosave_slots` byte
      count is unchanged from before — **the rotation was never touched.**
    - `rwa save --name "../escape"` → **`bad-args`** citing
      `GenText.IsValidFilename`.
    Also: `rwa journal --limit 2 --types action` must show the save as an
    `action` row with `path`, `tick` and `bytes`, and **no `cheat` key**.

## Teardown

20. `rwa designate --type cancel --cells '["<ROCK>","<ORE>"]'`,
    `rwa posture --pawns '[…]' --area <each colonist's ORIGINAL area from step 1>`,
    `rwa area --kind allowed --op delete --id <A_WIDE> / <A_NARROW> / <A_EMPTY>`.
    Then `rwa journal --types red_error --since-seq <the seq from step 1>`
    EXPECT **`count: 0`** — the zero-red-errors invariant across the whole run.
    Remove `Saves/repair-check-1.rws` by hand.

---

## The two source-level facts worth pinning offline

Neither is checkable in-game, and both decide code above.

- `Designator_MineVein.CanDesignateCell` returns **true** for a FOGGED cell,
  while `DesignateSingleCell` then does `loc.GetEdifice(base.Map).def` with no
  null check. The NRE is unreachable through `designate` **only because**
  `DesignateEngine` gates fog before the designator — not because the game is
  safe. If that gate ever moves, `mine-vein` starts throwing.
- `GameDataSaveLoader.SaveGame` is `void` and catches its own exception into a
  `Log.Error`, and `SafeSaver.Save` pops `GenUI.ErrorDialog` first — a
  `Dialog_MessageBox`, hence `forcePause`, hence every later `advance` halts on
  reason `"dialog"`. That is why `save` stats the file afterwards and publishes
  `written` and `force_pause` in the same envelope. **A failed save wedges the
  run**, so `written:false` is an escalation, not a retry.

## What is NOT covered above, in those words

- **Pathing.** `reach` is the allowed-area test only; a target inside the area
  can still be unroutable. The envelope's own `test` field says so. Filed as
  `1d381be`.
- **The standing re-check.** Nothing re-evaluates designations already on the
  map when the area, the roster or the target moves — and in the incident that
  killed `Marco` the herd walked AFTER the designation was placed. Filed as
  `9c68756`.
- **The save FAILURE path** (`written:false` + `force_pause`). Staging it needs
  a read-only `Saves/` or a full disk, and it leaves the bench wedged on a
  modal — which is exactly what the play loop must not do to itself.
- **A flood fill wider than one cell**, if this map has no multi-cell exposed
  vein. Step 14 says how to tell.
