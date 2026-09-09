# Acceptance — `nepo-restock` (git-bug `eb35e1a`)

The worker that wrote this may never launch RimWorld, so this is the acceptance
in envelope form for the orchestrator to drive — by hand, by `rwa`, or by the
raw file protocol (`commands/<id>.json` → `results/<id>.json`).

Every envelope below is `{"op":…,"args":…}`; with python on the box each is
`rwa <op> --args-json '<json>'`.

**Every `advance` below needs the two per-call escapes** (git-bug `722c951`):

```json
"unread_ok": "accept/eb35e1a-nepo-restock.md: fixture, not a play loop",
"through_casualties": "accept/eb35e1a-nepo-restock.md: fixture, not a play loop"
```

**Phases 0–8 need Nepo active; phase 9 is the only one that needs it OFF, and
it costs a restart.** Phase 7 is the one that costs game time — up to 2,500
ticks — and it is the acceptance bullet the issue is really about.

---

## Fixture

| needs | why | stage it with |
|---|---|---|
| **Nepo active** (`Dorian.Nepo`) | the whole feature | the `_RimWorld-Agent` modlist (`profile/make-profile-agent.sh`, or the `.ps1` on BORGES) |
| a colony **started from the Nepo scenario** | `NepoGate` refuses `not-a-nepo-colony` for reads as well as writes | new game, Nepo scenario |
| **Dad available** for phase 7 only | the sweep is gated on `DadAvailable`; writing a rule is not | true on a fresh Nepo colony |
| one **stuffable apparel** def in the catalog (`Apparel_Parka`, `Apparel_Duster`) | the material and colour gates | any bench |
| a def the colony is **short of** | phase 7 needs a threshold genuinely crossed | pick one from `nepo-restock`'s own `available` |
| `requireCommsForRestock` **ON** (the default) | phase 7 asserts a DISPATCH, not a shipment | Nepo's settings window |

**Do not change any Nepo setting to make a step pass.** This verb reads them and
never writes them.

---

## Phase 0 — preflight and the shape contract

`eq(…, None)` passes on an *absent* key, which is how a suite reports green on a
verb that shipped nothing. These steps assert keys are PRESENT.

1. `{"op":"status"}` → `ok:true`, `data.gameLoaded:true`, and `data.verbs`
   contains `nepo-restock`. **If not, the DLL is stale — rebuild.**
2. `{"op":"journal","args":{"limit":1}}` → record `data.last_seq` as **seq0**.
3. `{"op":"nepo-restock"}` → `ok:true`, and these keys are PRESENT:
   `data.total`, `data.enabled`, `data.would_fire_now`, `data.shown`,
   `data.more`, `data.cap`, `data.rows`, `data.shown_note`, `data.basis`,
   `data.available_basis.map`, `data.available_basis.player_home_maps`,
   `data.sweep.interval_ticks`, `data.sweep.next_sweep_tick`,
   `data.sweep.dad_available`, `data.sweep.require_comms_for_restock`,
   `data.sweep.outcome`, `data.removal`, `data.mode.balance`,
   `data.action.journal_seq` (which is **null**).
4. Same reply → `data.sweep.interval_ticks` is **2500** (one in-game hour) and
   `data.sweep.next_sweep_tick` is greater than `data.sweep.now_tick` and is a
   multiple of 2500. If `interval_ticks` is null, `data.sweep.interval_unknown`
   says why — that is the honest answer, not a failure, but it means
   `NepoTuning.RestockCheckIntervalTicks` moved.
5. On a fresh colony `data.total:0`, `data.rows:[]`, and `data.shown_note`
   reads `0 of 0 standing rule(s) shown; 0 enabled, 0 at or below threshold`.

## Phase 1 — the single-def read, and the option lists a write may use

6. `{"op":"nepo-restock","args":{"def":"MealSimple"}}` → `ok:true`,
   `data.has_rule:false`, `data.rule:null`, `data.available` is an **integer**
   (the same number Nepo's own "you have N" shows if you open the catalog on
   that row), `data.orderable_now:true`, `data.note` explains that a create
   needs both numbers, `data.action.journal_seq:null`.
7. Same reply → `data.customize.can_material:false`,
   `data.customize.can_color:false` (a meal is neither art nor apparel).
8. `{"op":"nepo-restock","args":{"def":"Apparel_Parka"}}` →
   `data.customize.can_material:true`, `data.customize.stuffs_total` > 1,
   `data.customize.stuffs` contains `Cloth`; `data.customize.can_color:true`,
   `data.customize.colors_total` > 1, and every entry in
   `data.customize.colors` has a `hex` and most have a `color_def`. **Record
   one `color_def` from this list — phase 6 uses it.**
9. `{"op":"nepo-restock","args":{"def":"MinifiedThing"}}` → `ok:true`,
   `data.available:null` **and** `data.available_note` present, naming
   `ListerThings.ThingsOfDef` and `Log.ErrorOnce`. **Then check the game log:
   no new red error.** This is the zero-red-errors guard, not a nicety.
10. `{"op":"journal","args":{"since_seq":seq0,"types":["action"]}}` →
    `data.count:0`. Five reads wrote no journal row.

## Phase 2 — the argument contract: refused, never defaulted

Each of these is `ok:false` with a bad-args message, and **nothing is written**
— confirm with `{"op":"nepo-restock"}` after the block that `data.total` is
still 0.

11. `{"op":"nepo-restock","args":{"threshold":5,"target":10}}` → refused: a
    write needs `def`.
12. `{"op":"nepo-restock","args":{"def":"MealSimple","threshold":5}}` →
    `ok:false`, `data.gate:"incomplete-rule"`, and `data.reason` says both
    numbers are required and names the panel's staged defaults.
13. `{"op":"nepo-restock","args":{"def":"MealSimple","threshold":-1,"target":5}}`
    → bad-args naming `0..999999` and `Widgets.TextFieldNumeric`.
14. `{"op":"nepo-restock","args":{"def":"MealSimple","threshold":1,"target":1000000}}`
    → the same refusal for `target`.
15. `{"op":"nepo-restock","args":{"def":"MealSimple","remove":true}}` →
    bad-args, and the message says a rule is DISABLED not deleted, naming
    `SetRestock`'s missing removal branch and `RemoveRestock`'s missing sync
    wrapper. Same for `delete`, `clear`, `unset`, `drop`, `cancel`.
16. `{"op":"nepo-restock","args":{"def":"MealSimple","colour":"Structure_Grey"}}`
    → bad-args: *did you mean 'color'?*
17. `{"op":"nepo-restock","args":{"def":"MealSimple","restock_at":5}}` →
    bad-args: *did you mean 'threshold'?*
18. `{"op":"nepo-restock","args":{"def":"MealSimple","nonsense":1}}` →
    bad-args naming the eight keys this verb accepts.
19. `{"op":"nepo-restock","args":{"cap":0}}` → bad-args, `cap must be 1..200`.

## Phase 3 — dry run writes nothing

20. `{"op":"nepo-restock","args":{"def":"MealSimple","threshold":20,"target":60,"dry_run":true}}`
    → `ok:true`, `data.written:false`, `data.would_create:true`,
    `data.before:null`, `data.would.threshold:20`, `data.would.target:60`,
    `data.would.would_fire` is a bool, `data.gates.reproduced` lists four
    entries, `data.gates.not_reproduced` names `dad-unavailable` and the console
    chain, `data.material_pref` is present, `data.action.journal_seq:null`.
21. `{"op":"nepo-restock"}` → `data.total:0`. **The dry run wrote nothing.**
22. `{"op":"journal","args":{"since_seq":seq0,"types":["action"]}}` →
    `data.count:0`.

## Phase 4 — the create, the read-back, and the journal

23. `{"op":"nepo-restock","args":{"def":"MealSimple","threshold":20,"target":60}}`
    → `ok:true`, `data.written:true`, `data.created:true`,
    `data.rule.threshold:20`, `data.rule.target:60`, `data.rule.enabled:true`,
    `data.rule.stuff:null`, `data.rule.color:null`, `data.changed:["created"]`,
    `data.action.journal_seq` is a **non-null integer**.
24. `{"op":"nepo-restock"}` → `data.total:1`, `data.enabled:1`, and the row
    matches step 23 field for field.
25. `{"op":"journal","args":{"since_seq":seq0,"types":["action"]}}` →
    `data.count:1`, and the row's `verb` is `nepo-restock`, `step` is
    `restock`, `target` is `MealSimple`.
26. **Open Nepo's catalog window in-game, find `MealSimple`, click the row.**
    The restock panel shows the toggle GREEN, "restock at" 20 and "restock to"
    60, and the row's label carries the `↻` badge. **This is the proof that the
    verb wrote the same state the widget writes.**
27. `{"op":"nepo-restock","args":{"def":"MealSimple","threshold":25}}` →
    `ok:true`, `data.created:false`, `data.rule.threshold:25`,
    `data.rule.target:60` (carried), `data.rule.enabled:true` (carried, not
    reset), `data.changed` contains `threshold 20 -> 25` and nothing about
    `target` or `enabled`, and `data.before.threshold:20`. **`before` proves the
    snapshot was taken before the in-place mutation.**
28. `{"op":"nepo-restock","args":{"def":"MealSimple","threshold":30,"target":30}}`
    → `ok:true`, `data.clamp.target_sent:30`, `data.clamp.target_stored:31`,
    `data.rule.target:31`, and `data.clamp.why` quotes
    `Mathf.Max(threshold + 1, target)`.

## Phase 5 — disable is how remove is spelled

29. `{"op":"nepo-restock","args":{"def":"MealSimple","enabled":false}}` →
    `ok:true`, `data.rule.enabled:false`, `data.rule.threshold:30`,
    `data.rule.target:31` (**the numbers are kept**), `data.disabled_note`
    present, `data.changed` contains `enabled True -> False`.
30. `{"op":"nepo-restock"}` → `data.total:1`, `data.enabled:0`,
    `data.would_fire_now:0`.
31. In-game: the same catalog row's toggle is now GRAY and the `↻` badge is
    gone, with 30 and 31 still in the fields.
32. `{"op":"nepo-restock","args":{"def":"MealSimple","enabled":true}}` →
    `data.rule.enabled:true`, numbers unchanged.

## Phase 6 — the material and colour gates

33. `{"op":"nepo-restock","args":{"def":"MealSimple","threshold":1,"target":2,"stuff":"Cloth"}}`
    → `ok:false`, `data.gate:"stuff-not-customizable"`, `data.gate_cite` names
    `CatalogBuilder.CanCustomizeMaterial` and `Window_DadCatalog.RestockStuff`.
34. `{"op":"nepo-restock","args":{"def":"Apparel_Parka","threshold":1,"target":3,"stuff":"Steel"}}`
    → `ok:false`, `data.gate:"stuff-not-allowed"`, citing `AllowedStuffs`.
35. `{"op":"nepo-restock","args":{"def":"Apparel_Parka","threshold":1,"target":3,"stuff":"Cloth"}}`
    → `ok:true`, `data.rule.stuff:"Cloth"`, `data.stuff_source` says *set by
    this call*, and `data.rule.unit_price` is the **stuffed** price (compare
    against `nepo-catalog {filter:"Parka"}`'s stuffless one — for cloth they may
    differ; the assertion is that it is an integer > 0).
36. `{"op":"nepo-restock","args":{"def":"Apparel_Parka","threshold":2}}` →
    `data.rule.stuff:"Cloth"` still, and `data.stuff_source` says *carried
    forward*. **An absent key did not silently change what ships.**
37. `{"op":"nepo-restock","args":{"def":"Apparel_Parka","stuff":null}}` →
    `data.rule.stuff:null`, `data.stuff_source` says *cleared by this call*.
38. `{"op":"nepo-restock","args":{"def":"MealSimple","color":"<the color_def from step 8>"}}`
    → `ok:false`, `data.gate:"color-not-customizable"`.
39. `{"op":"nepo-restock","args":{"def":"Apparel_Parka","color":"<the color_def from step 8>"}}`
    → `ok:true`, `data.rule.color` is a `#RRGGBBAA` string,
    `data.rule.color_def` is the defName you sent, `data.color_source` names it.
40. `{"op":"nepo-restock","args":{"def":"Apparel_Parka","color":"NoSuchColorDef"}}`
    → bad-args naming `nepo-restock {def:…}` as the way to list the palette.
41. A ColorDef that exists but is NOT in the palette → `ok:false`,
    `data.gate:"color-not-in-palette"`. On a bench with Ideology OFF, any
    `ColorType.Ideo` def works (`ColorWhite` is a Structure colour and IS in
    the palette then; pick one the step-8 list does not contain). If every
    ColorDef on the bench happens to be in the palette, **record the step as
    unreachable rather than green.**
42. `{"op":"nepo-restock","args":{"def":"Apparel_Parka","color":null}}` →
    `data.rule.color:null`, `data.color_source` says *cleared*.
43. `{"op":"nepo-restock","args":{"def":"Table2x2c","threshold":1,"target":2}}`
    (or any non-orderable def — a corpse, or a non-minifiable building) →
    `ok:false`, `data.gate:"not-orderable"`, `data.gate_cite` names
    `CatalogBuilder.IsOrderable`, and the reason says the sweep would have
    shipped it anyway.

## Phase 7 — THE LOOP: a rule fires and `nepo-inbound` shows it

**This is the issue's last acceptance bullet and it needs the clock.** The sweep
runs when `now % 2500 == 0 && DadAvailable`, so a same-call assertion fails on a
correct implementation.

44. `{"op":"nepo-restock","args":{"def":"MealSimple"}}` → record
    `data.available` as **A**.
45. `{"op":"nepo-restock","args":{"def":"MealSimple","threshold":<A+10>,"target":<A+30>,"enabled":true}}`
    → `ok:true`, and `data.rule.would_fire:true`,
    `data.rule.would_order:30`ish. **`would_fire` is the verb's own prediction;
    the rest of this phase is whether the game agrees.**
46. `{"op":"nepo-inbound"}` → record `data.dispatches.total` as **D0**.
47. `{"op":"nepo-restock"}` → `data.sweep.next_sweep_tick` minus
    `data.sweep.now_tick` is **N** (at most 2500).
48. `{"op":"advance","args":{"ticks":<N+300>, …the two escapes…}}` → `ok:true`.
    (300 of slack: the sweep tick must also satisfy `now % 250 == 0`, which
    2500 already does, but `advance` lands where it lands.)
49. `{"op":"nepo-inbound"}` → `data.dispatches.total` is **D0 + 1**, and one row
    has `def:"MealSimple"` with a `count` near 30. **The loop is closed: the
    agent caused a dispatch it could previously only observe.**
50. If `data.shipments.total` grew instead and `dispatches` did not, check
    `data.mode.require_comms_for_restock` — with it OFF the sweep charges and
    ships on the spot, which is the other correct outcome and is what
    `data.sweep.outcome` predicted in step 3.
51. `{"op":"nepo-restock","args":{"def":"MealSimple"}}` → `data.available` has
    grown by the dispatched count (`CommittedCount` includes queued
    dispatches), and `data.rule.would_fire` is now **false**. **This is Nepo's
    own no-double-order guarantee, visible through the verb.**
52. `{"op":"nepo-restock","args":{"def":"MealSimple","enabled":false}}` — tidy
    up before the bench run, or the colony keeps ordering meals.

## Phase 8 — the negative log check

53. Search the game log for `[AutoRimmer] Nepo`. **There must be no warning.**
    A bridge that half-bound would have journalled one, and every step above
    would still have looked green.
54. `{"op":"journal","args":{"since_seq":seq0,"types":["warning"]}}` →
    no row mentioning `nepo-restock`.

## Phase 9 — the absent-mod phase (costs a restart)

55. Restart with Nepo **disabled**, load any colony, then
    `{"op":"nepo-restock"}` → `ok:false`, `data.gate:"nepo-absent"`,
    `data.reason` names `Nepo` and `Dorian.Nepo`, `data.mod` is
    `Nepo (Dorian.Nepo)`.
56. Same run → `{"op":"digest"}` and `{"op":"pawns"}` are `ok:true` and
    unchanged. **No red error, no warning, and no other verb notices.**

---

## What this file cannot prove, and why

- **Multiplayer.** `NepoSync.SetRestockRule` IS the registered sync site, so
  calling it is the correct route — but MP's prefix fires only in interface
  context and AutoRimmer's verbs run from the command drain, which is not.
  Under MP the write would stay local. Identical to `nepo-order`'s caveat and
  to `FswaBridge`'s; out of scope for a single-player bench, recorded so nobody
  rediscovers it.
- **API drift.** The restock tier disables only this verb and journals a
  warning. Reachable only by editing Nepo, so it is verified by reading — and
  negatively by step 53.
- **`restock-did-not-take`.** `SetRestockRule` returns silently only on a null
  component or a null def, both already refused upstream, so the read-back's
  refusal branch is unreachable from a legal call. It exists because "the
  invoke did not throw" is not evidence, which is the same argument
  `nepo-order`'s `order-did-not-take` makes.
- **The `materialPrefs` cross-effect.** Visible only in the catalog window's
  pre-fill: after step 35, close and reopen the catalog and expand the Parka
  row — its material comes up on Cloth. Worth doing once by eye; there is no
  verb that reads `materialPrefs`.
- **Two home maps.** `available_basis.player_home_maps` reports the count and
  the note fires above 1. Staging a second home map for one assertion is not
  worth a bench restart; read the note's presence instead.
