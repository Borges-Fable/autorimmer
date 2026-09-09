# 975973e (first half) — the screen, by hand

Run in order on the agent bench (`_RimWorld-Agent`). Every numbered line is one
`rwa` call and one thing to read. **Nothing in this file was run** — the worker
never launches the game.

This is the FIRST half of `975973e`: the screen object and its six panels. The
answer gate, `defer` and `write-off` are the second half and are **not built**.
Section I is the check that this half changed no control flow, and it is the one
to run first if anything looks wrong.

Two shorthands used throughout:

```bash
S='rwa look --json'
J='rwa journal --json'                          # reads journal/<sid>.ndjson directly
```

`rwa` takes any op verbatim, so `look` needs no client change. Array arguments
use the `:json` suffix — `rwa`'s bare-value autotype guesses bool/int/float/
string and nothing else.

---

## A. The screen exists, and both verbs return the same object

1. `rwa ping --json | jq .ok` → `true`. The bench answers.
2. `$S | jq -r '.data.panels | join(",")'` →
   `stop,map,colonists,gauges,since,decisions`. **Six panels, in that order.**
   If this line is wrong, stop.
3. `$S | jq '.data | keys_unsorted'` → the six panels plus `screen` and `spec`.
   `.data.spec` is `"975973e"`.
4. `rwa advance --ticks 200 --json | jq -r '.data.screen.panels | join(",")'` →
   the same six. **`advance` returns the screen as `data.screen`.**
5. `rwa advance --ticks 200 --json | jq '.data.screen | keys_unsorted'` and
   `$S | jq '.data | keys_unsorted'` → **identical key sets.** One object, two
   verbs.
6. `$S | jq '.data.stop.time.tick'` twice in a row → the SAME tick. `look` moves
   no time. (Compare `rwa status --json | jq .data.tick` between the two.)

## B. The stop line

7. `rwa advance --ticks 500 --json | jq -c '.data.screen.stop | {kind, why, since_last_look_ticks, saved}'`
   → `kind` is the advance's own halt reason (`"ticks"` here) and `why` is a
   sentence. `saved` is the last save's file name or `null`.
8. `rwa save --name screen-check --json >/dev/null; $S | jq -r '.data.stop.saved'`
   → `screen-check`. The stop line carries the save mark.
9. `$S | jq -c '.data.stop.time'` → the digest's `time` section verbatim: `tick`,
   `paused`, `speed`, `day_of_season`, `season`, `year`, `hour`, `weather`,
   `outdoor_c`. **Field names unchanged from `digest.time`.**
10. `rwa advance --until:json '{"letter":true}' --timeout-ticks 60000 --json | jq -c '.data.screen.stop | {kind, halted_seq}'`
    → when a letter lands, `kind` is `"letter"` and `halted_seq` names the
    journal row. `stop.halted_on` is the same object `data.halted_on` carries.

## C. The map seam (ee4b4f8 has not landed)

11. `$S | jq -r '.data.map.pending'` → `ee4b4f8`.
12. `$S | jq -c '.data.map.site | keys_unsorted'` → the digest's `site` keys:
    `biome`, `biome_label`, `tile`, `avg_temp_c`, `rainfall`, `elevation`,
    `hilliness`, `swampiness`, `pollution`, `map_size`, `pocket_map`.
13. `$S | jq '.data.map | has("ascii")'` → `false`. **The map panel carries the
    site line and nothing the map spec owns.** If this is `true`, someone has
    reached into ee4b4f8.

## D. Colonists

14. `$S | jq -c '.data.colonists | {total, more, order, armed, armed_of, unarmed}'`
    → the digest's colonist block plus the armed pair. `armed_of` is the count
    of VIOLENCE-CAPABLE colonists, not the roster.
15. `$S | jq -c '.data.colonists.list[0] | keys_unsorted'` → `name`, `job`,
    `mood_pct`, `mood_arrow`, `drafted`, `room` (+ `flags` when non-empty).
    Unchanged from `digest.colonists`.
16. `$S | jq -c '.data.colonists.posture | {will_seek, area_bound, attack, omitted}'`
    → the posture block, with `omitted` naming the prose keys dropped for the
    turn budget. `digest.posture` still carries them in full.

## E. The gauges — and what set each one counts

17. `$S | jq -r '.data.gauges | keys_unsorted | join(",")'` → `guns, power,
    food, temperature, stock, goals, lights, losses, threats, armament`.
18. **GUNS — every position, never one for six.** Build six mini-turrets, then:
    `$S | jq -c '.data.gauges.guns.by_def[] | {def, count, n: (.at|length)}'`
    → `count == n == 6` for `Turret_MiniTurret`. **Six coordinates, not one.**
    `$S | jq -r '.data.gauges.guns.set'` names the set:
    `ListerBuildings.allBuildingsColonist` filtered to `Building_Turret`.
19. `$S | jq '.data.gauges.guns.coverage'` → `null`, and
    `.coverage_pending` names `ee4b4f8`. Coverage is the map spec's.
20. **POWER — nets and batteries.**
    `$S | jq -c '.data.gauges.power | {gen_w, draw_w, gain_w, batteries, nets, nets_with_generator, nets_no_generator, nets_no_consumer, batteries_unreachable}'`
    → the digest's eight power fields, unchanged, plus the three new ones.
21. Build two wind turbines on a conduit run with no consumer:
    `$S | jq -c '.data.gauges.power | {nets_no_consumer, nets_no_consumer_rows}'`
    → `1`, and the row carries an `at` cell you can hand to `room-at`.
22. Wall a battery into a room with no door:
    `$S | jq -c '.data.gauges.power | {batteries_unreachable, batteries_checked, batteries_reach_basis}'`
    → non-zero, and the basis names `Pawn.CanReach(Touch, Danger.Deadly)`.
    Knock the wall out; it returns to `0`.
23. **FOOD — two numbers (e811574).** Drop meals outside any stockpile:
    `$S | jq -c '.data.gauges.food | {food_days, nutrition, nutrition_in_stockpiles}'`
    → `nutrition` (map-wide) and `nutrition_in_stockpiles` DIFFER. Haul them in;
    they converge. `food_days` is vanilla's stockpile-only figure throughout.
24. **STOCK — the map-wide twin (e811574).** Mine steel and leave it at the vein:
    `$S | jq -c '.data.gauges.stock.materials[] | select(.name=="steel")'`
    → `in_stockpiles` and `map_wide` DIFFER, and `forbidden` is the part nobody
    will haul. Haul it in; they converge. Repeat for `wood`, `components`,
    `silver`, `meds`.
25. **STOCK — unworn apparel (G3's evidence).** Craft a flak vest and leave it:
    `$S | jq -c '.data.gauges.stock.unworn_apparel | {count, by_def, armour_readable}'`
    → the count and the def, and `armour_readable: false` citing `47547ca`.
    **No armour VERDICT is published; that is deliberate.**
26. **STOCK — uninstalled art (4c12e5d).** `dev:spawn-thing` a minified
    sculpture: `$S | jq -c '.data.gauges.stock.uninstalled | {count, rows}'`
    → counted with its def, position and forbidden flag. Install it; the count
    clears.
27. **LIGHTS — an age on every row (91bc250).**
    `$S | jq -c '.data.gauges.lights.active[] | {id, priority, muted, age_ticks, age_days, returned}'`
    → every live alert carries an age. Advance a day; the age grows by the tick
    delta. `alert-mute` it; the age is UNAFFECTED (age and mute are orthogonal).
28. **LIGHTS — a return is visible.** `journal-selftest {steps:["alert-at"]}`
    to raise a fixture alert, clear it, raise it again:
    `$S | jq -c '.data.gauges.lights.active[] | select(.returned) | {id, returns, age_ticks, last_cleared_tick}'`
    → `returned: true`, `returns >= 1`, and the age RESTARTED at the return.
    `$J --type alert_on | jq -r 'last(.data.events[]) | .payload.returns'` → the
    same count on the journal row.
29. **LIGHTS — an alert we never saw start.** After a fresh load with an alert
    already live: `$S | jq '.data.gauges.lights.active[] | select(.age_ticks == null) | .id'`
    → named, and `.data.gauges.lights.age_basis` says why. **`null` is not `0`.**
30. **GOALS — all six, every screen.**
    `$S | jq -c '.data.gauges.goals | {G1: .G1.ok, G2: .G2.ok, G3: .G3.ok, G4: .G4.ok, G5: .G5.ok, G6: .G6.ok}'`
    → six verdicts. **`null` is UNKNOWN and never a failure.**
31. **G2 — geysers used AND unused.**
    `$S | jq -c '.data.gauges.goals.G2 | {geysers, geysers_fogged, used, unused, unused_at, generators_producing}'`
    → every unused geyser prints its own coordinate. `dev:unfog` and watch
    `geysers_fogged` fall and `geysers` rise: a geyser under fog is one the
    player has not found, and the two are counted apart.
32. **G3 — the armed count (c41bdcc).**
    `$S | jq -c '.data.gauges.armament | {colonists, violence_capable, armed, armed_of, armed_melee_only, unarmed, spare_ranged, spare_melee, total_weapons}'`
    → give one colonist the map's only rifle: `armed 1`, `spare_ranged 0`,
    `total_weapons 1`. Make him drop it: `armed 0`, `spare_ranged 1`,
    **`total_weapons` unchanged at 1.** The two populations are disjoint.
33. `$S | jq -c '.data.gauges.goals.G3 | {armed, of, armed_ok, armour_ok}'` →
    `armour_ok` is `null`, with the reason on `.note`.
34. **G4 — UNKNOWN, and it says so.**
    `$S | jq -c '.data.gauges.goals.G4 | {rooms, enclosed_cells, baseline, ok}'`
    → `baseline: null`, `ok: null`, and `.note` names `f1a1700` comment #2.
35. **TEMPERATURE.** `$S | jq -c '.data.gauges.temperature | {ok, out_of_range, outdoor_c, omitted}'`
    → the digest's temperature section with its prose named in `omitted`.

## F. Since you last looked

36. `$S | jq -c '.data.since | {from_seq, to_seq, rows_total, agent_rows}'` →
    the seq window and the count of the agent's own rows.
37. `$S | jq -r '.data.since | keys_unsorted | join(",")'` → includes `game`,
    `human`, `mod`. **Three rows.**
38. **Three rows, three provenances.** Play by hand for a few seconds (mouse a
    designation), then `rwa advance --ticks 2000 --json | jq -c '.data.screen.since | {game: .game.total, human: .human.total, mod: .mod.total, agent_rows}'`
    → the hand designation is in `human`, the clock span is in `mod`, anything
    the colony did is in `game`, and your own commands are counted in
    `agent_rows` and NOT listed.
39. Cross-check against the file:
    `$J --since-seq $($S | jq '.data.since.from_seq') | jq -r '.data.events[].by' | sort | uniq -c`
    → the same distribution. The panel is the journal, read by the mod.
40. **Twenty rows including a death.** `journal-selftest {steps:["down-at"]}`
    plus enough noise to exceed the per-bucket cap, in ONE advance:
    `rwa advance --ticks 5000 --json | jq -c '.data.screen.since.game | {total, more, rows: [.rows[] | .type]}'`
    → the `downed` row is present **whatever the cap**, and `more` says how many
    ordinary rows were dropped.
    `... | jq -c '.data.screen.since.truncated'` → `dropped`,
    `ordinary_dropped`, `important_dropped`, `by_type`, and a `read_the_rest`
    string you can paste.
41. `$S | jq -r '.data.since.always_kept | join(",")'` →
    `death,downed,destroyed,letter,red_error,mental_break`. **An arrival has no
    event type of its own and arrives as a `letter`** — `.data.since.note` says
    so.
42. **The mark moves on a look.** `$S >/dev/null; $S | jq -c '.data.since | {rows_total, game: .game.total}'`
    → the second look reports (near) zero: the first one delivered those rows.
43. **The mark moves on an advance too.** `rwa advance --ticks 1000 --json | jq '.data.screen.since.rows_total'`
    then immediately `$S | jq '.data.since.rows_total'` → the second is much
    smaller. One mark, both verbs.
44. **The clock half rides inside the panel.**
    `$S | jq -c '.data.since.clock | {ticks, in_this_advance, outside_ticks, outside_spans, journal_seq}'`
    → `in_this_advance` is `0` on a look. Play by hand for ten seconds, then
    `$S | jq -c '.data.since.clock.outside'` → the span, with `drove: "external"`.
45. **ACROSS A LOAD — "no earlier screen".** `rwa save --name screen-load
    --json`, quit to the main menu, load `screen-load`, then FIRST call:
    `$S | jq -r '.data.since.no_earlier_screen'` → the sentence. Call `look`
    again → **the key is gone.** The mark is in memory and is cleared at a game
    boundary, like the sampler ring.
46. Same check on the advance path: after a fresh load, the FIRST
    `rwa advance --ticks 100 --json | jq -r '.data.screen.since.no_earlier_screen'`
    → the sentence; the second advance does not carry it.

## G. Losses are levels

47. Note the turret count: `$S | jq -c '.data.gauges.guns.by_def[] | {def, count, was}'`
    → no `was` key while nothing is lost.
48. `rwa dev:destroy --things:json '[<turret thingId>]' --json` (a `dev:destroy`
    sent DURING an advance is refused `busy` — see 827c1bf's note; destroy it
    between turns, or drive the halt with
    `journal-selftest {steps:["destroy-at"]}`).
49. `$S | jq -c '.data.gauges.guns.by_def[] | select(.def=="<def>") | {count, lost, was}'`
    → `count` fell by one, `lost: 1`, **`was` is the old count.**
50. `$S | jq -c '.data.gauges.losses | {total, rows: [.rows[] | {def, at, mode, ticks_ago}]}'`
    → the loss with the game's own `DestroyMode`, its cell and its age.
51. **It PERSISTS.** Advance ten in-game days and call `look` again → `was` is
    still there and `ticks_ago` has grown. Nothing expires it; that is the
    design's own call (`clears_with` on the block says so).
52. **A rebuild does not clear it, and the field says why.** Rebuild the turret:
    `count` rises, `lost` HOLDS, `was` is `count + lost`. This is the stated
    behaviour until `write-off {id, reason}` lands in the second half.
53. **The colony's own work is not a loss.** Deconstruct a wall:
    `$S | jq '.data.gauges.losses.total'` → unchanged. `Deconstruct`,
    `WillReplace`, `Cancel`, `Refund` and `FailConstruction` are excluded by the
    same deny list the halt uses.
54. **Cleared at a game boundary.** Save, quit to menu, load:
    `$S | jq '.data.gauges.losses.total'` → `0`. The level is session memory,
    and the journal file still holds every `destroyed` row.

## H. Decisions owed — RENDERED, NOT ENFORCED

55. `$S | jq -r '.data.decisions.gate'` → the sentence saying the gate is NOT
    enforced. **This is the line that must be true of this half.**
56. `rwa dev:incident --def RaidEnemy --json` (or spawn any choice letter), then
    `$S | jq -c '.data.decisions | {group, in_group, total_owed, groups_pending, by_kind}'`
    → the letter group is served and the other kinds are counted, not piled.
57. `$S | jq -c '.data.decisions.owed[] | {id, kind, text, deadline_tick, acts: [.acts[] | {op, label}]}'`
    → each has an id, one sentence, a deadline where the game has one, and
    ready-to-send acts in `triage`'s `{op, args, why}` shape.
58. **The ids are derived, not counted.** Call `look` three times: the id for
    the same letter is `d-letter-<the letter's own ID>` every time.
59. Send one of the listed acts verbatim:
    `rwa letter-choose --letter <id> --option 0 --json | jq .ok` → `true`, and
    the next `look` no longer lists that decision.
60. **A muted alert is never a decision.** `alert-mute` a live, old alert:
    `$S | jq '[.data.decisions.owed[] | select(.kind=="alert")] | length'` → the
    muted one is gone. Unmute it; once it is older than 60,000 ticks it returns.
61. **A pending force-pausing dialog is a decision (9227839).** There is no
    `journal-selftest` step that raises one — the five steps are `error-at`,
    `down-at`, `alert-at`, `destroy-at` and `main-menu` — so use a real window:
    set `"autoAnswerNameDialogs": false` in `config.json` and let the colony
    naming prompt arrive (it falls due at 4.3 in-game days, per
    `NamePlayerFactionAndSettlementUtility.CanNameFaction`), or open a trade
    with `comms-call`. Then:
    `$S | jq -c '.data.decisions.owed[] | select(.kind=="dialog")'` → id
    `d-dialog-<WindowType>`, urgency above every other kind, and for a
    `Dialog_GiveName` the act is `dialog-accept`, never `dialog-dismiss` —
    dismissing closes it without answering and `Faction.FactionTick` raises it
    again 1,000 ticks later, forever.
62. `$S | jq -r '.data.decisions.not_detected'` → the sentence saying a chore
    that needs a policy is not detectable because the chore set is not built.

## I. What must NOT have changed — the control-flow check

**Run this section if anything at all looks wrong.** This half is required to
change no control flow.

63. **`advance` still refuses for exactly its old reasons.** With a decision
    owed from section H still unanswered:
    `rwa advance --ticks 500 --json | jq -c '{ok, code: .error.code, reason: .data.reason}'`
    → **`ok: true`.** There is NO `decision-owed` refusal in this half. If you
    see one, the second half has leaked in.
64. **`defer` and `write-off` do not exist.**
    `rwa status --json | jq -r '.data.verbs | map(select(. == "defer" or . == "write-off")) | length'`
    → `0`. And `rwa verbs | grep -c '^look$'` → `1`.
65. **The unread-journal gate of 722c951 is untouched.** Make an advance journal
    something, then advance again without reading:
    `rwa advance --ticks 500 --json | jq -c '{ok, code: .error.code, detail: .error.detail}'`
    → the same `unread-journal` refusal as before, with the same code.
66. **Every pre-existing advance field is still there.**
    `rwa advance --ticks 200 --json | jq '.data | keys_unsorted'` → `reason`,
    `tick`, `ticks_elapsed`, `wall_seconds`, `avg_tps`, `timeout_ticks`,
    `timeout_source`, `speed`, `speed_requested`, `speed_source`,
    `speed_nominal_tps`, `max_tps_effective`, `max_ticks_in_frame`,
    `overshoot_bound`, `overshoot_bound_speed`, `paused_on_exit`, `journal_seq`,
    `slower_spans`, `since_last_look`, `journal_read_watermark`,
    `journal_unread` — **plus `screen` and nothing removed.**
67. **`since_last_look` still means what 65e7cf9 says.**
    `rwa advance --ticks 400 --json | jq -c '.data.since_last_look'` → `ticks`,
    `in_this_advance`, `outside`, `outside_ticks`, `outside_spans`,
    `journal_seq`, with `outside[].drove` still `mod|external`.
68. **A `look` does not move time and does not create an obligation.**
    `rwa advance --ticks 100 --json | jq .data.journal_unread` (note it), then
    `$S >/dev/null`, then `rwa advance --ticks 100 --json | jq -c '{ok, unread: .data.journal_unread}'`
    → the obligation is what it was. `look` moves the SCREEN mark, not the read
    watermark.
69. **The digest still parses.** `rwa digest --json | jq -c '.data | keys_unsorted'`
    → unchanged: `time`, `site`, `alerts`, `construction`, `colonists`,
    `work_coverage`, `posture`, `resources`, `power`, `temperature`, `threats`,
    `trends`, `changed`. The only additions are inside `alerts.active[]`
    (`first_seen_tick`, `age_ticks`, `age_days`, `returned`) and
    `alerts.age_basis`.
70. **The cockpit still folds it.** With `cockpit/` running, take a screenshot
    after an advance: nothing throws on the new `screen` key.

## J. The size, measured

71. `$S > /tmp/screen.json; wc -c /tmp/screen.json` → the whole envelope. For
    the screen alone:
    `$S | jq -c '.data' | wc -c`.
72. The worker's own synthetic measurement, against run `openrun-20260902`'s
    colony at the raid that ended it, is **13,925 bytes** (~3,500–4,600 tokens),
    and **25,409 bytes** (~6,400–8,500 tokens) with every cap saturated. Record
    the real number here when this runs; if it is materially above 14 KB on a
    quiet colony, the panel to cut is `gauges.goals` (1,757 bytes that change on
    the scale of in-game days, not turns).
73. Per panel: `$S | jq -c '.data | to_entries[] | {k: .key, n: (.value | tostring | length)}'`.
74. **The saturated screen.** Drive every cap up — 10+ turret defs, 12+ live
    alerts, 10+ unworn apparel defs, 10 uninstalled things, 5 decisions of one
    kind, a 5,000-tick advance through a raid — and measure again. The
    acceptance line is "the screen with every cap saturated is measured in bytes
    and the number is stated in the closing comment".

---

## What this half does NOT discharge

- **The whole of section H's gate.** `advance` refusing with `decision-owed`,
  `defer {id, reason}`, `write-off {id, reason}` — second half.
- **The map panel.** `ee4b4f8`.
- **`rwa` writing `RUNS/<run>/frames/<tick>.json`** — that is the client spec
  (`70ee75e`), not the mod.
- **A room whose ROLE changed.** Stated limit, `f1a1700` comment #2.
- **An armour rating.** Stated limit, `47547ca`, open.
