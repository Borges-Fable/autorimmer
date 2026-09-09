# 827c1bf — who did it, and what was destroyed, by hand

Run in order on the agent bench (`_RimWorld-Agent`). Every numbered line is one
`rwa` call and one thing to read. **Nothing in this file was run** — the worker
never launches the game.

Two shorthands used throughout:

```bash
J='rwa journal --json'                          # reads journal/<sid>.ndjson directly
BY='.data.events[] | "\(.seq) \(.by) \(.type) \(.payload.step // .payload.def // "")"'
```

`rwa journal` reads the NDJSON file directly, so `by` and `cmd` are on the
envelope exactly as the mod wrote them; `jq -r "$BY"` is the whole check for
most of what follows.

Every array argument below uses the `:json` suffix. `rwa`'s bare-value autotype
guesses bool/int/float/string and nothing else, so `--things '[1,2]'` would send
the six-character STRING `[1,2]` and the verb would refuse it — correctly, and
confusingly.

---

## A. `by` on every row (the schema invariant)

1. `rwa ping --json | jq .ok` → `true`. The bench answers.
2. `$J | jq '[.data.events[] | select(has("by") | not)] | length'` → **`0`**.
   Every row in the session file carries a provenance. This is the invariant;
   if it is not zero, stop.
3. `$J | jq -r '.data.events[].by' | sort | uniq -c` → only the four words
   `agent`, `mod`, `game`, `human`. **No fifth value, no nulls, no empties.**
4. `$J --type session | jq -r '.data.events[0] | "\(.by) \(.type) \(.payload.kind)"'`
   → `mod session boot`. Seq 1 is the mod writing about itself; nobody pressed
   "boot". Same for `unloaded`, which is the poller inferring that the
   heartbeat stopped. `newgame` and `loaded` are deliberately left on the
   default, because those ARE somebody's act — whoever loaded the save.
4b. `$J --type alert_on | jq -r 'last(.data.events[]) | .by'` → `mod`. The
   alert scanner is a standing per-frame observer, so nobody "did" an
   `alert_on`; the mod's own diff noticed a condition. The five sites that
   declare `mod` rather than inherit are the clock observer, the alert scanner,
   the name-dialog sweep, `boot` and `unloaded` — every other row's provenance
   is whatever was actually running.

## B. `agent` carries the command id

5. `rwa digest --json >/dev/null; rwa designate --op forbid --things:json '[<id>]' --json | jq .ok`
   → `true`. Any mutating player verb will do; `designate` writes an `action`
   row.
6. `$J | jq -r 'last(.data.events[] | select(.type=="action")) | "\(.by) \(.cmd) \(.payload.verb)"'`
   → `agent designate-HHMMSS-NNNN designate`. **`cmd` is the id of the command
   that caused it** — the join key from a journal row back to its result
   envelope in `results/`.
7. `$J | jq '[.data.events[] | select(.by!="agent" and has("cmd"))] | length'`
   → **`0`**. `cmd` appears under `agent` and nowhere else.

## C. `game` — the tick, whoever set it running

8. `rwa advance --ticks 2000 --json | jq -c '.data | {reason, ticks_elapsed}'`
   → `{"reason":"ticks","ticks_elapsed":2000}` (or a halt, which is fine).
9. `$J | jq -r '[.data.events[] | select(.by=="game")] | length'` → some rows,
   if the colony did anything during those 2,000 ticks (a message, a letter, a
   `mental_break`). A quiet 2,000 ticks legitimately produces none — go to 10.
10. **The one that matters, and it needs no advance.** Unpause the game by hand
    (spacebar), let a colonist finish a meal or a job so the game emits a
    `message`, pause again. Then
    `$J | jq -r 'last(.data.events[] | select(.type=="message")) | .by'`
    → **`game`**, not `human`. This is the case a bracket around the mod's own
    call sites would have got wrong: the mod drove none of those ticks.
11. `$J | jq -r 'last(.data.events[] | select(.type=="clock")) | "\(.by) \(.payload.drove) \(.payload.ticks)"'`
    → `mod external <n>`. The row's provenance is `mod` (the mod's clock
    observer wrote it) and `drove` is `external` (nobody of ours ran those
    ticks). **Two different questions, two different words** — that is the
    whole reconciliation, and `by:"human"` on this row would be a bug.

## D. `human` — the five input handlers

Do these with the window in focus and the game paused, so nothing of the mod's
is running.

12. Select a colonist and press **Draft**. Then
    `$J | jq -r 'last(.data.events[] | select(.payload.step=="gizmo")) | "\(.by) \(.payload.label) \(.payload.gizmo)"'`
    → `human Draft Command_Toggle`.
13. Pick the **Mine** designator from the Orders tab and left-click a rock
    cell. Then
    `$J | jq -r 'last(.data.events[] | select(.payload.step=="designator")) | "\(.by) \(.payload.label) \(.payload.at|@json)"'`
    → `human mine [x,z]`. Now **right-click** to cancel the designator and
    re-run the same line — the seq must **not** have moved. The deselect
    branch consumes the event too, and it is not an act.
14. Right-click with a colonist selected and choose any order from the float
    menu. Then
    `$J | jq -r 'last(.data.events[] | select(.payload.step=="float-menu")) | "\(.by) \(.payload.label)"'`
    → `human <the menu text>`.
15. Open the debug menu (`~`, Debug actions) and **spawn a thing**. Then
    `$J | jq -r 'last(.data.events[] | select(.payload.step=="debug-menu")) | "\(.by) \(.payload.label) \(.payload.action_type)"'`
    → `human "Spawn thing…" ToolMap` (or `Action`). For a `Tool*` type the row
    marks the CHOICE; the spawn itself lands on the next map click and also
    reads `by:"human"`, because nothing of ours is running then either.
16. Kill a pawn with the debug menu, then **resurrect** it from the debug menu.
    `$J | jq -r '.data.events[] | select(.payload.step=="debug-menu") | .payload.label' | tail -3`
    → the three menu leaves, in order. **This is the row run
    `openrun-20260902` did not have**: John's first death stands unqualified in
    that journal while he died again 5.5M ticks later, and the reversal has no
    row at all.
17. Answer a letter (open one, press a choice button). Then
    `$J | jq -r 'last(.data.events[] | select(.payload.step=="dialog-button")) | "\(.by) \(.payload.label) \(.payload.dialog)"'`
    → `human <the button text> Dialog_NodeTree`.
18. **The gap, stated so it is not mistaken for a pass.** A
    `Verse/Dialog_MessageBox` button (the plain OK/Cancel box) produces **no**
    row — it has no funnel to hook; see `HumanActions.cs`'s
    `Patch_DiaOptionActivate` header. Press one and confirm nothing new appears
    under `step=="dialog-button"`, so the limit is measured rather than
    assumed.
18b. The second measured gap. Pick **Build > Wall** off the architect menu
    (do not click the map yet) and confirm no new `step=="gizmo"` row appears:
    `Designator_Build.ProcessInput` does not call `base.ProcessInput`, so
    arming that cursor is invisible. Now click a cell — a `step=="designator"`
    row appears. Arming is not recorded; using is.
19. `$J | jq '[.data.events[] | select(.payload.verb=="human" and .by!="human")] | length'`
    → **`0`**. A human-action row that is not `by:"human"` would mean a hook
    fired while something of ours was running, which is the one thing the
    `Provenance.NothingOfOursIsRunning` guard exists to prevent.

## E. `destroyed`, and the halt

`dev:destroy` cannot drive the halt: `AgentGameComponent.DrainCommands` answers
every main-thread verb except `pause` with `busy` while an advance is in
flight, so a `dev:destroy` sent during an advance is refused, not executed.
Steps 20–21 test the ROW with the game paused; 22–25 test the HALT through the
`destroy-at` fixture, which arms from the drain and fires from
`GameComponentTick` — inside `DoSingleTick`, inside the advance.

20. Find three ids: a player wall, a player turret, and a corpse.
    ```bash
    rwa things --def Wall --detail --json | jq -r '.data.things[0].id'
    rwa things --def Turret_MiniTurret --detail --json | jq -r '.data.things[0].id'
    rwa things --category corpses --detail --json | jq -r '.data.things[0].id'
    ```
21. `rwa dev:destroy --things:json '[<wall>,<turret>,<corpse>]' --json | jq -c '.data | {count, mode}'`
    → `{"count":3,"mode":"Vanish"}`. Then
    `$J --type destroyed | jq -r '.data.events[-3:][] | "\(.by) \(.payload.kind) \(.payload.def) \(.payload.mode) \(.payload.player)"'`
    → three rows, all `agent`, kinds `building`/`building`/`corpse`, all
    `Vanish`, `player` true/true/(false or true). **`mode` is the game's own
    `DestroyMode`, not a story about what happened** — that is a8d8ada's bullet
    exactly.
22. Now the halt. Arm two things — a wall and a corpse — to die on the same
    tick, then advance across it:
    ```bash
    rwa journal-selftest --steps:json '["destroy-at"]' \
        --destroy_things:json '[<wall2>,<corpse2>]' --destroy_delay_ticks 300 --json \
      | jq -c '.data.destroy_at | {fires_at_tick, mode, things: [.things[] | {def, is_corpse, player_faction}]}'
    rwa advance --ticks 2000 --json | jq -c '.data | {reason, ticks_elapsed, halted_on}'
    ```
    → `reason` is **`"loss"`**, `ticks_elapsed` is about 300, and `halted_on`
    names the wall: `kind:"loss"`, `what:"building"`, `mode:"Vanish"`, the
    def, the id and the cell.
23. `$J --type destroyed | jq -r '.data.events[-2:][] | "\(.by) \(.payload.kind) \(.payload.mode)"'`
    → `game building Vanish` and `game corpse Vanish`. **Both rows exist and
    only the building stopped the clock.** `by` is `game` because the fixture
    fires from inside the tick, which is where a manhunter pack's damage comes
    from too.
24. The escape. Re-arm and ride past it:
    ```bash
    rwa journal-selftest --steps:json '["destroy-at"]' --destroy_things:json '[<wall3>]' \
        --destroy_delay_ticks 300 --json | jq .ok
    rwa advance --ticks 2000 --through_losses:str "the raid is being fought; walls are expendable" \
        --json | jq -c '.data | {reason, ticks_elapsed, losses_rode_past, through_losses}'
    ```
    → `reason:"ticks"`, `ticks_elapsed:2000`, and
    `losses_rode_past:{count:1, shown:1, events:[…]}`. **The escape counts what
    it swallowed**; an escape that hid its own cost would be a silent bypass
    with a reason string on it.
25. The mode filter. Deconstruct a wall through the normal player route
    (`rwa designate --op deconstruct …`, then advance until a pawn finishes it,
    or `rwa dev:destroy --things:json '[<id>]' --mode deconstruct`). The
    `destroyed` row appears with `mode:"Deconstruct"` and the advance does
    **not** stop. Five modes are the colony's own decision arriving as planned
    — `Deconstruct`, `WillReplace`, `Cancel`, `Refund`, `FailConstruction` —
    and the row is written for all of them; only the halt is filtered.
26. Mining. `rwa designate --op mine …` and advance until the rock goes.
    `$J --type destroyed | jq '[.data.events[] | select(.payload.def|test("Rock|Granite|Sandstone|Marble|Limestone|Slate"))] | length'`
    → **`0`**. Natural rock has no faction, so the player-faction filter keeps
    a mined mountain off this row entirely.

## F. The audit's own test, re-run

27. The zero-prior-row test (themes.md T13), on the next run's journal:
    ```bash
    rwa journal --json | jq '[.data.events[] | select(.type=="death" and (has("by")|not))] | length'
    ```
    → **`0`**. No death without provenance.
28. And the count that becomes a measurement:
    ```bash
    rwa journal --type death --json | jq -r '.data.events[] | .by' | sort | uniq -c
    ```
    → a breakdown, not a total. A `death` under `human` is debug-menu residue
    and is not a colony fact; a `death` under `game` is. That distinction did
    not exist before this issue, which is why three of run
    `openrun-20260902`'s 23 death rows silently were not deaths.

## The source-level half

Three invariants no bench call can show, because they are about what cannot be
written:

- **One emit site stamps every row.** `grep -n 'Provenance.Current'
  Source/AutoRimmer/` → one read, in `Journal.Emit`. There is no second place a
  `by` can come from and no hook that has to remember to set one.
- **`Thing.Destroy` is not patched.** `grep -rn 'HarmonyPatch(typeof(Thing)'
  Source/AutoRimmer/` → nothing. `grep -rn 'nameof(Building.Destroy)\|
  nameof(Frame.Destroy)\|nameof(Corpse.Destroy)' Source/AutoRimmer/` → exactly
  three.
- **One prefix in the whole tree, and it is the tick bracket.**
  `grep -rn 'public static void Prefix' Source/AutoRimmer/` → one, in
  `Provenance.Patch_DoSingleTick`, returning `void` (so Harmony cannot skip the
  original with it) and paired with a `Finalizer` rather than a `Postfix` (so a
  throw out of `DoSingleTick` cannot leave the main thread stamped `game`).

## What this file cannot discharge

The issue's last acceptance bullet — **"the `Building.Destroy` postfix is timed
on the 38-mod bench across a 60,000-tick advance with a fire burning, and the
cost is stated in the closing comment"** — is a measurement, not a check, and
it is Dorian's. The reasoning it has to beat is in `JOURNAL.md` §Cost and in
`JournalHooks.cs`'s `destroyed` header: `Building.Destroy` is reached only when
a building actually ends, which is single digits per in-game day on a colony
nobody is attacking and is bounded by the number of things there are to lose
during a raid, whereas `Thing.Destroy` — the hook deliberately not taken — is
on every stack merge. The number to record is the advance's own `avg_tps` with
and without a fire, from `rwa advance --ticks 60000 --json | jq .data.avg_tps`.
