# Acceptance — `nepo-catalog` / `nepo-order` / `nepo-inbound` (the Nepo soft dependency)

The worker that wrote this may never launch RimWorld, so this is the acceptance
in envelope form for the orchestrator to drive — by hand, by `rwa`, or by the
raw file protocol (`commands/<id>.json` → `results/<id>.json`, ids kept to
`[A-Za-z0-9-]` so `Poller.Sanitize` leaves them alone).

Every envelope below is `{"op":…,"args":…}`; with python on the box each is
`rwa <op> --args-json '<json>'`.

**Every `advance` below needs the two per-call escapes** (git-bug `722c951`).
Add to every advance envelope:

```json
"unread_ok": "accept/nepo-order.md: fixture, not a play loop",
"through_casualties": "accept/nepo-order.md: fixture, not a play loop"
```

Nothing else about the steps changes. **Phases 1–8 need Nepo active; phase 9 is
the only one that needs it OFF, and it costs a restart.**

---

## Fixture

| needs | why | stage it with |
|---|---|---|
| **Nepo active** (`Dorian.Nepo`) | the whole feature | add it to the `_RimWorld-Agent` modlist (`profile/make-profile-agent.sh`, or the `.ps1` on BORGES) |
| a colony **started from the Nepo scenario** | `NepoGameComponent.GameComponentTick` returns early on `!IsNepoGame`, and the delivery sweep is inside it. In a non-Nepo colony a shipment never lands | new game, Nepo scenario. A vanilla save loaded with Nepo active is **phase 2's fixture**, not a bug |
| **Dad available** (`DadAvailable`) | the contact list, and `PlaceOrder`'s own first test | true on a fresh Nepo colony |
| a **powered comms console** the colony can reach | the widget gate `nepo-order` reproduces | the Nepo scenario prebuilds one (`ScenPart_PrebuiltPoweredComms`) |
| ≥ 1 free colonist **capable of Sight**, able to reach that console | `DadCommTarget.CanRead`. **Sight, not Talking** | any colony |
| a **known balance**, i.e. `unlimitedMoney` OFF, for phases 4–5 | phases 4–5 measure the debit; with unlimited money the balance never moves | Nepo mod settings. **Turn it back ON for the bench run** — that is the whole point of the mod for this bench |
| `instantDelivery` **OFF** for phase 6 | phase 6 measures a non-zero arrival tick | Nepo mod settings; flip for phase 7 |

**Do not have the worker change any Nepo setting.** These verbs read them and
never write them; if a phase needs a different mode, the orchestrator sets it in
Nepo's own settings window.

---

## Phase 0 — preflight and the shape contract

Prove every path exists before a later phase leans on it. `eq(…, None)` passes
on an *absent* key, which is how a suite reports green on a verb that shipped
nothing.

1. `{"op":"status"}` → `ok:true`, `data.gameLoaded:true`, **no** `data.forcePause`.
2. `{"op":"status"}` → `data.verbs` contains all three of `nepo-catalog`,
   `nepo-order`, `nepo-inbound`. If not, the DLL is stale — rebuild.
3. `{"op":"journal","args":{"limit":1}}` → record `data.last_seq` as **seq0**.
4. `{"op":"nepo-catalog","args":{"cap":1}}` → `ok:true`, and these keys are
   PRESENT (not merely non-false): `data.mode.unlimited_money`,
   `data.mode.instant_delivery`, `data.mode.balance`,
   `data.mode.delivery_delay_ticks`, `data.mode.require_comms_for_restock`,
   `data.mode.dad_available`, `data.mode.is_nepo_colony`, `data.mode.read_only`,
   `data.items.total`, `data.items.matched`, `data.items.shown`,
   `data.items.more`, `data.items.cap`, `data.items.rows`, `data.shown_note`,
   `data.action.journal_seq` (which is **null**).
5. `{"op":"nepo-inbound"}` → `ok:true`, and `data.shipments.total`,
   `data.dispatches.total`, `data.now_tick`, `data.shown_note` are all present.

---

## Phase 1 — the catalog reads, is capped, and the cap tells the truth

6. `{"op":"nepo-catalog"}` → `ok:true`. `data.items.rows` has **at most 20**
   entries; `data.items.total` is in the **hundreds or thousands**;
   `data.items.more == data.items.matched - data.items.shown`; and
   `data.shown_note` begins with a literal `"<shown> of <total> items shown"`.
   **This is the standing capped-list rule and the issue's own example.**
7. Same envelope → every row in `data.items.rows` has `def`, `label`,
   `category`, `price` (an integer **> 0**), `stuffable`, `mod`.
8. `{"op":"nepo-catalog","args":{"cap":3}}` → exactly 3 item rows;
   `data.items.cap:3`; `more` grew by 17 against step 6.
9. `{"op":"nepo-catalog","args":{"cap":0}}` → `ok:false`, bad-args,
   `cap must be 1..200`. Same for `{"cap":201}`.
10. `{"op":"nepo-catalog","args":{"filter":"meal"}}` → every row's `def` **or**
    `label` contains `meal`, case-insensitively; `matched` is far below `total`;
    `total` is **unchanged from step 6** — the filter narrows the answer, never
    the census.
11. `{"op":"nepo-catalog","args":{"filter":"MEAL"}}` → identical `matched` to
    step 10. Case-insensitive.
12. `{"op":"nepo-catalog","args":{"filter":"zzzznosuchthing"}}` →
    `ok:true`, `matched:0`, `rows:[]`, `data.shown_note` says `0 of <total>`.
    **An empty match is not an error.**
13. `{"op":"nepo-catalog","args":{"kind":"items"}}` → no `data.mechs`,
    `data.animals` or `data.slaves` keys at all.
14. `{"op":"nepo-catalog","args":{"kind":"mechs"}}` → `ok:true`. With Biotech
    off or `allowMechPurchases` off, `total:0` and `data.mechs.basis` says which.
    With both on, rows carry `kind`, `label`, `category` (a weight class) and
    `price`.
15. `{"op":"nepo-catalog","args":{"kind":"animals"}}` → same shape; `category`
    is a trade tag (`AnimalFarm`, `AnimalCommon`, …) and `category_label` is its
    human form.
16. `{"op":"nepo-catalog","args":{"kind":"slaves"}}` → `ok:true`. Rows carry
    **`index`** (the order handle), `name`, `price`, `age`, `gender`, and the
    section carries `index_note` warning that the index moves on the daily
    reroll. With `allowSlavePurchases` off, `total:0`.
17. `{"op":"nepo-catalog","args":{"kind":"nonsense"}}` → `ok:false`, bad-args,
    the message lists `all|items|mechs|animals|slaves`.
18. `{"op":"journal","args":{"since_seq":seq0,"types":["action"]}}` →
    **`data.count:0`.** Six catalog reads wrote no journal row. Observers never
    mutate.

---

## Phase 2 — mode is reported, not assumed

19. `{"op":"nepo-catalog","args":{"cap":1}}` → `data.mode.unlimited_money` and
    `data.mode.instant_delivery` are **booleans** and match what Nepo's settings
    window shows. `data.mode.balance` matches the number the catalog window
    shows. `data.mode.read_only` is present and says no verb writes a setting.
20. Same reply → `data.mode.require_comms_for_restock` is a bool AND
    `data.mode.require_comms_meaning` names `RunRestockSweep` /
    `RunSurgeryOrderSweep` and says it does **not** gate `nepo-order`.
21. **The read-only proof.** Open Nepo's settings window and record all ten
    values. Run steps 6, 14, 16 and a `nepo-inbound`. Re-open the settings
    window: **every value is unchanged.**
22. **The not-a-Nepo-colony refusal.** Load a save NOT started from the Nepo
    scenario (any other bench colony) and send
    `{"op":"nepo-order","args":{"items":[{"def":"Silver","count":1}]}}` →
    `ok:false`, `data.gate:"not-a-nepo-colony"`, `data.gate_cite` names
    `GameComponentTick` and `IsNepoGame`, and `data.reason` explains that a
    shipment here would never deliver. **Then reload the Nepo colony.**

---

## Phase 3 — the argument contract: refused, never defaulted

Every one of these must leave the balance and `nepo-inbound` **unchanged** —
check with a `nepo-catalog {cap:1}` before and after the block.

23. `{"op":"nepo-order","args":{"items":[{"def":"MealSimple"}]}}` → `ok:false`,
    bad-args, the message says a `count` is **not defaulted to 1** and that
    nothing was ordered. **The issue's own example.**
24. `{"op":"nepo-order","args":{"items":[{"def":"MealSimple","qty":20}]}}` →
    `ok:false`, bad-args naming `qty` as an unknown key **in a `items` entry**
    and listing `count, def, stuff`. The stray-key rule one level in.
25. `{"op":"nepo-order","args":{"items":[{"def":"MealSimple","count":20}],"foo":1}}`
    → `ok:false`, bad-args, `unknown arg 'foo' — nepo-order accepts …`, and the
    safety sentence says nothing was ordered and nothing was charged.
26. `{"op":"nepo-order","args":{"goods":[{"def":"MealSimple","count":20}]}}` →
    `ok:false`, and the message says **`did you mean 'items'?`** — the
    `NearMiss` alias, which Levenshtein would never find.
27. `{"op":"nepo-order","args":{"items":[{"def":"MealSimple","count":0}]}}` →
    `ok:false`, `'count' must be at least 1`.
28. `{"op":"nepo-order","args":{"items":[{"def":"MealSimple","count":2.5}]}}` →
    `ok:false`, `not a whole number`.
29. `{"op":"nepo-order","args":{"items":[{"def":"MealSimple","count":999999}]}}`
    → `ok:false`, the message names the int overflow in Nepo's own `OrderCost`.
30. `{"op":"nepo-order","args":{}}` → `ok:false`, `data.gate:"empty-order"`,
    `data.gate_cite` names `Window_DadCatalog.DrawFooter`'s `cartCount > 0`.
31. `{"op":"nepo-order","args":{"items":[{"def":"NoSuchDefAtAll","count":1}]}}`
    → `ok:false`, bad-args, and the message points at
    `nepo-catalog {filter:"NoSuchDefAtAll"}`.
32. `{"op":"nepo-order","args":{"items":[{"def":"Human_Corpse","count":1}]}}` →
    `ok:false`, `data.gate:"not-orderable"`, `data.gate_cite` names
    `CatalogBuilder.IsOrderable`. (Any def the catalog rejects will do — a
    corpse, or a non-minifiable building such as `Wall`.)
33. `{"op":"nepo-order","args":{"items":[{"def":"MealSimple","count":1,"stuff":"Steel"}]}}`
    → `ok:false`, `data.gate:"stuff-not-customizable"`, citing
    `CatalogBuilder.CanCustomizeMaterial` and `CatalogEntry.UnitPrice`.
34. `{"op":"nepo-order","args":{"slaves":[999]}}` → `ok:false`,
    `data.gate:"slave-not-offered"`, and the reason names either the empty
    roster (with the Ideology / `allowSlavePurchases` / ideoligion clause) or
    the real roster bounds.
35. `{"op":"nepo-inbound"}` → `data.shipments.total` is **the same as at step 5**
    and the balance in `data.mode.balance` is unchanged. **Twelve refusals cost
    nothing.**
36. `{"op":"journal","args":{"since_seq":seq0,"types":["action"]}}` →
    still `data.count:0`. **A refusal writes no action row.**

---

## Phase 4 — dry run

Record `B0 = data.mode.balance` from a `nepo-catalog {cap:1}` first.

37. `{"op":"nepo-order","args":{"items":[{"def":"MealSimple","count":10}],"dry_run":true}}`
    → `ok:true`, **`data.dry_run:true`**, **`data.placed:false`**,
    `data.order.lines` has one row with `def:"MealSimple"`, `count:10`,
    `unit_price` > 0 and `line_total == unit_price * 10`;
    `data.cost == line_total`; `data.would.balance_before == B0`;
    `data.would.balance_after == B0 - data.cost`; `data.would.arrival_tick` is
    in the future; `data.would.arrives_in` is a human span;
    **`data.action.journal_seq` is null**.
38. Same reply → `data.order.console.id` and `data.order.negotiator.id` are
    real. **The dry run ran the console gate too** — that is what makes it a
    preflight rather than a price quote.
39. `{"op":"nepo-catalog","args":{"cap":1}}` → `data.mode.balance == B0`.
    **Nothing was charged.**
40. `{"op":"nepo-inbound"}` → `data.shipments.total` unchanged.
41. `{"op":"journal","args":{"since_seq":seq0,"types":["action"]}}` →
    still `data.count:0`. **A dry run writes no journal row.**
42. `{"op":"nepo-order","args":{"items":[{"def":"MealSimple","count":10}],"dry-run":true}}`
    (**hyphen**) → still `data.dry_run:true` and `data.placed:false`.
    `Poller.Underscore` rewrites it. This is the exact defect `c519477` records
    as ten real blueprints in `openrun-20260902`.

---

## Phase 5 — the real order, and the balance moves

43. `{"op":"nepo-order","args":{"items":[{"def":"MealSimple","count":10}]}}` →
    `ok:true`, `data.placed:true`, `data.dry_run:false`.
44. Same reply → `data.balance.before == B0`;
    `data.balance.after == B0 - data.cost`; `data.balance.charged == data.cost`;
    **`data.balance.charged_as_expected:true`**; there is **no**
    `data.cost_mismatch` key; `data.balance.basis` names
    `NepoGameComponent.Spend`.
45. Same reply → `data.arrival.tick` is a future tick; `data.arrival.delay_ticks`
    matches `data.mode.delivery_delay_ticks`; `data.arrival.instant:false`;
    `data.arrival.basis` mentions the `now % 250 == 0` sweep.
46. Same reply → `data.pending_after == data.pending_before + 1`; `data.shipment`
    is present with `arrival_tick`, `contents` (one row, `def:"MealSimple"`,
    `count:10`) and `value_note` saying the value is a re-price.
47. Same reply → **`data.action.journal_seq >= 1`**.
48. `{"op":"journal","args":{"since_seq":seq0,"types":["action"],"limit":50}}` →
    exactly **one** new row, `payload.verb:"nepo-order"`, and:
    - **`by:"agent"`** — the provenance work (`827c1bf`/`ca626b3`) stamps it
      automatically from `VerbRegistry.Execute`'s `Provenance.Command` scope;
      the verb passes nothing. **If this reads `human` or `game`, the
      provenance work regressed, not this verb.**
    - `cmd` is the command id of step 43.
    - `payload.cost`, `payload.charged`, `payload.balance_before`,
      `payload.balance_after`, `payload.unlimited_money`,
      `payload.instant_delivery`, `payload.arrival_tick`, `payload.delay_ticks`,
      `payload.lines`, `payload.negotiator` all present.
49. `{"op":"nepo-inbound"}` → `data.shipments.total` is one higher than step 40;
    the newest row's `arrival_tick` equals step 45's; `arrives_in_ticks` is
    positive; `due:false`.
50. `{"op":"advance","args":{"ticks":<delay + 500>,"max_tps":600, …escapes}}`
    then `{"op":"nepo-inbound"}` → the shipment is **gone** from
    `data.shipments.rows` and `total` is back to step 40's value.
51. `{"op":"things","args":{"def":"MealSimple","detail":true}}` → 10 more meals
    than before phase 5. **The pod landed.**
52. `{"op":"journal","args":{"since_seq":seq0,"types":["red_error"]}}` →
    **`data.count:0`.** The standing invariant.
53. `{"op":"journal","args":{"since_seq":seq0,"types":["warning"]}}` → **no**
    warning whose text starts `[AutoRimmer] Nepo`. Those fire only on a
    reflection throw, on API drift, or on an order that did not take — one here
    means the bridge is fabricating and every number above is suspect.

---

## Phase 6 — the plural form, and the other three goods

54. One call, several kinds:
    ```json
    {"op":"nepo-order","args":{
      "items":[{"def":"MealSimple","count":5},{"def":"Steel","count":100}],
      "animals":[{"kind":"Muffalo","count":1}],
      "dry_run":true}}
    ```
    → `ok:true`; `data.order.lines` has **three** rows with the right `kind`
    values; `data.cost` equals the sum of the three `line_total`s.
    **One call, three goods, one decision.** Skip the `animals` half with a NOTE
    if `allowAnimalPurchases` is off (step 15 said so).
55. Drop `dry_run` and re-send → `data.placed:true`,
    `data.balance.charged_as_expected:true`, **one** journal row (not three),
    and `payload.lines` has three entries.
56. With Biotech and `allowMechPurchases` on:
    `{"op":"nepo-order","args":{"mechs":[{"kind":"Mech_Lifter","count":1}],"dry_run":true}}`
    → `ok:true`, one `kind:"mech"` line, `unit_price` matching step 14's row.
    **Skip with a NOTE otherwise.**
57. With Biotech/`allowMechPurchases` **off**:
    `{"op":"nepo-order","args":{"mechs":[{"kind":"Mech_Lifter","count":1}]}}` →
    `ok:false`, `data.gate:"mech-not-orderable"`, and the reason says the list
    is empty and names the two clauses.
58. With `allowSlavePurchases` on and an approving ideoligion: take an `index`
    from step 16 and send
    `{"op":"nepo-order","args":{"slaves":[<index>],"dry_run":true}}` → `ok:true`,
    one `kind:"slave"` line with that `index`, `name`, `pawn_id` and
    `count:1`. **Skip with a NOTE otherwise** — it needs Ideology, the setting,
    and an ideoligion that approves, and none of the three is stageable with
    `dev:*`.

---

## Phase 7 — instant delivery is reported, not assumed

Turn `instantDelivery` ON in Nepo's settings, then:

59. `{"op":"nepo-catalog","args":{"cap":1}}` → `data.mode.instant_delivery:true`,
    `data.mode.delivery_delay_ticks:0`, and `data.mode.delivery_meaning` says
    INSTANT **and** names the `now % 250 == 0` sweep. The number changed because
    Dorian changed it, and the verb read it.
60. `{"op":"nepo-order","args":{"items":[{"def":"MealSimple","count":1}]}}` →
    `data.arrival.instant:true`, `data.arrival.delay_ticks:0`, and
    `data.arrival.tick` equals the current tick.
61. `{"op":"nepo-inbound"}` → the shipment is listed with `due:true` and
    `arrives_in:"now"`. **It is listed, not already gone** — the sweep has not
    run yet, which is exactly what `arrival.basis` warned.
62. `{"op":"advance","args":{"ticks":300,"max_tps":600, …escapes}}` then
    `{"op":"nepo-inbound"}` → gone. Delivered inside 250 ticks.

Turn `unlimitedMoney` ON and:

63. `{"op":"nepo-catalog","args":{"cap":1}}` → `data.mode.unlimited_money:true`
    and `data.mode.balance_meaning` says the balance is **not** to be planned
    around.
64. Record `S0 = data.mode.spent_while_unlimited`, then
    `{"op":"nepo-order","args":{"items":[{"def":"MealSimple","count":50}]}}` →
    `ok:true`; `data.balance.unlimited:true`;
    **`data.balance.before == data.balance.after`** (the balance did not move);
    `data.balance.charged == data.cost`;
    `data.balance.spent_while_unlimited == S0 + data.cost`;
    `data.balance.basis` explains that `charged` is the movement of the
    *counter*, not of the balance. **This is the whole point of the two-member
    read-back** — a verb that inferred the charge from the balance would report
    zero here.
65. `{"op":"journal","args":{"since_seq":seq0,"types":["red_error"]}}` →
    `data.count:0`.

---

## Phase 8 — the widget gate

66. **No power.** `{"op":"flick","args":{…the comms console…}}` or cut its
    power, `advance` 200 ticks, then
    `{"op":"nepo-order","args":{"items":[{"def":"MealSimple","count":1}]}}` →
    `ok:false`, `data.gate:"console-unusable"`, `data.gate_cite` names
    `Building_CommsConsole.CanUseCommsNow`, `data.reason` is the game's own
    "no power" string, and `data.console.can_use_now:false`. **Restore power.**
67. `{"op":"nepo-catalog","args":{"cap":1}}` with the console still unpowered →
    **`ok:true`.** Reading a price list needs no console. This asymmetry is
    deliberate: the read never claims you can buy.
68. **A negotiator who cannot see.** With a blind colonist (`dev:add-hediff` a
    blinding one, or a colony that has one),
    `{"op":"nepo-order","args":{"items":[{"def":"MealSimple","count":1}],"negotiator":<blind id>}}`
    → `ok:false`, `data.gate:"console-unusable"`, and the reason names
    `DadCommTarget.CanRead` and `PawnCapacityDefOf.Sight`. **Skip with a NOTE**
    if no such pawn can be staged.
69. **A MUTE negotiator is ACCEPTED.** With a colonist incapable of Talking but
    capable of Sight, the same call with that `negotiator` → **`ok:true`**
    (or a dry run's `ok:true`). This is the Sight/Talking inversion:
    `comms-call` would refuse this pawn and `nepo-order` must not, because
    `Patch_CommsConsole_GetFloatMenuOptions.ShouldOfferDadDespiteMuteness`
    exists precisely to let a mute colonist call Dad. **Skip with a NOTE** if no
    such pawn can be staged — but if one CAN be staged and this refuses, the
    verb has fabricated a restriction and that is a failure.
70. **Auto-pick is stable.** Two consecutive
    `{"op":"nepo-order","args":{"items":[{"def":"MealSimple","count":1}],"dry_run":true}}`
    → the same `data.order.negotiator.id` both times.
71. **Cannot afford.** With `unlimitedMoney` OFF, order something far past the
    balance → `ok:false`, `data.gate:"cannot-afford"`, `data.gate_cite` quotes
    `DrawFooter`'s `canOrder` line, and `data.cost` / `data.balance` are both
    reported. Balance unchanged afterwards.
72. `{"op":"journal","args":{"since_seq":seq0,"types":["action"]}}` → the count
    has not risen across phase 8's refusals.

---

## Phase 9 — **Nepo ABSENT** (needs a restart; this is the soft-dependency proof)

Disable **Nepo** in the bench's `ModsConfig.xml`, restart, load any colony.

73. `{"op":"status"}` → `ok:true`, and `data.verbs` **still contains all three
    verbs.** They register unconditionally; only their answers change.
74. `{"op":"nepo-catalog"}` → **`ok:false`** with
    `data.gate:"nepo-absent"`, `data.gate_cite` naming
    `AutoRimmer/NepoBridge.cs Resolve()`, `data.mod:"Nepo (Dorian.Nepo)"`, and
    `data.reason` naming the missing type `Nepo.NepoGameComponent`. **No
    `data.mode` key** — there is no mode to read, and inventing one would be the
    fabrication rule 2 forbids.
75. `{"op":"nepo-order","args":{"items":[{"def":"MealSimple","count":1}]}}` →
    the same `gate:"nepo-absent"` refusal. `data.action.journal_seq` is null.
76. `{"op":"nepo-inbound"}` → the same.
77. `{"op":"journal","args":{"since_seq":seq0,"types":["red_error"]}}` →
    **`data.count:0`. An absent optional mod is not an error.**
78. `{"op":"journal","args":{"since_seq":seq0,"types":["warning"]}}` → **no**
    warning mentioning Nepo. Absence is silent; only drift is loud.
79. **Nothing else changed.** Run `{"op":"digest"}`, `{"op":"pawns"}`,
    `{"op":"look"}`, `{"op":"things","args":{"category":"food"}}` and
    `{"op":"build","args":{"def":"Wall","pos":[…],"stuff":"WoodLog","dry_run":true}}`
    → all `ok:true` and materially identical to a Nepo-active run. **No other
    verb changes behaviour.**
80. `./rwa/selftest.sh` from the repo root → **263 passed, 0 failed**, with Nepo
    absent and with it present. It is a client-side suite and neither should
    move it.

Put `ModsConfig.xml` back afterwards.

---

## What this acceptance deliberately does NOT cover

- **`ScheduleShipment`.** It is never called and is never even bound — see
  DESIGN's 2026-09-09 entry. The claim that it is free and unsynced is verified
  by READING `NepoGameComponent.cs` and `MpBridge.cs`, not by running it; the
  only way to test it would be to write the free-order path this branch exists
  to avoid.
- **Multiplayer.** `NepoSync.PlaceOrder` IS the registered sync site, so calling
  it is the correct route — but MP's prefix fires only in interface context and
  AutoRimmer's verbs run from `GameComponentUpdate`, which is not. Under MP the
  write would stay local. Identical to `FswaBridge`'s caveat, out of scope for a
  single-player bench, recorded so nobody rediscovers it. Nepo's own README says
  its sync has never been confirmed in a two-client session either.
- **API drift.** The bridge disables the verbs and journals a warning when a
  bound member changes shape. Reachable only by editing Nepo, so it is verified
  by reading. **Its shape IS checked here, negatively**: step 53 asserts no
  `[AutoRimmer] Nepo` warning appears in the ordinary case, so a drift that
  did fire would be caught by an otherwise-green run.
- **`delay-unreadable`.** Reachable only if `NepoTuning.DeliveryDelayTicks`
  stops being a `static readonly int` (a `const` would be inlined and
  unreadable). Verified by reading; the branch refuses the order rather than
  assuming two days.
- **Colour.** Nepo's catalog can tint an item; `nepo-order` does not expose
  `color`, deliberately — it does not move the price, and it is one more shape
  to get wrong on a verb whose job is to keep a bench fed. A `color` key is
  therefore refused by `RefuseStray`, which is the correct answer for a key the
  verb genuinely does not take.
- **The bench run itself.** These verbs exist so an unattended agent cannot
  starve. Whether it *doesn't* is the run, not this file.

## A note on the independent second reader

Sessions 2.2 and 2.4 established "save and read the save's Scribe XML" as the
reader that makes a claim credible. It works cleanly here and is worth using for
step 46: `NepoGameComponent.ExposeData` scribes `pending` and `budget`, so a
saved game shows the shipment under `<pending>` with its `deliveryTick` and its
`manifest`, and the balance under `<budget>`. Two cautions, both from Nepo's own
code rather than from this bridge: `PendingShipment.slaves` is scribed
`LookMode.Reference`, so a bought slave appears only because
`NepoSync.PlaceOrder` parked it in `WorldPawns` first — a slave ordered any
other way would vanish from the save; and `budget` is untouched under
`unlimitedMoney`, so the XML confirms phase 7's "the balance did not move"
rather than contradicting it.
