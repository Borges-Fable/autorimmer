# Session 18 — M2: a room colonists built, from a template

Bench `_RimWorld-Agent`, sid `20260901T180111`, `--quicktest` 250x250 temperate
forest, assembly at `6b4422c` (`Build:` after the place-layout merge; no `*.cs`
changed since). Watched on workspace 3 at 2560x1600 fullscreen.

**M2 is met.** `templates/bedroom.ir.json` was placed as 22 blueprints at a
`find-rect`-chosen origin, colonists built all 22 under `advance`, and the
game's own `Room` analysis calls the result a bedroom.

## The claim, and the evidence for each half

| M2 asks | evidence | file |
|---|---|---|
| placed from the template, nothing hand-built | `place-layout` envelope: `requested 22`, `preflight.ok true / checked 22 / failed 0`, `placed_count 22`, `layout_id "ly-1"`, `rolled_back false` | `place-layout-envelope.json` |
| colonists built it | 22 journal `construction` rows, all `kind: "completed"`, **all `worker: "Danielle"`**, ticks 1264-4444 | `journal.json` |
| nothing left standing as a plan | `construction`: `blueprints 0, frames 0, by_state {}` | `construction-final.json` |
| enclosed | `proper: true`, `indoors: true`, `regions: 1`, `touches_map_edge: false`, `cells: 15` (3x5 interior of a 5x7 shell) | `room-66.json` |
| roofed | `open_roof_cells: 0`; `uses_outdoor_temp: false`; interior 22.7 C against an outdoor 4 C | `room-66.json` |
| role Bedroom | `role: "Bedroom"` (the RoomRoleDef defName), `role_label: "bedroom"` | `room-66.json` |
| zero red errors | journal seq 12-62: 22 `construction`, 19 `dev`, 5 `alert_on`, 3 `action`, 2 `alert_off`. **No `warning`, no `error`.** | `journal.json` |

The bed at `[121,131]` ended owned by **Izzy**, who claimed it by sleeping in it
during the advance — `JobDriver_LayDown` yielding `Toils_Bed.ClaimBedIfNonMedical`,
with no verb involved, exactly as the brief predicted.

`bedroom-built.png` is `rwa render --around 121,129 --radius 12`. The renderer
labels the room `BEDROOM ID 66 15 CELLS` and reports `ENCLOSED ROOMS: 1`.

## Fixture staging (all `dev:*`, all journaled)

`dev:starter-kit --preset survival --at 111,127`, then `unforbid` over the drop.
Construction skill set to 8 on all three colonists (`dev:set-skill`) so the run
would not spend its time on failed-construction rolls at skill 0-2; that changes
how fast colonists built it, not who built it.

**The kit ran TWICE.** The first call was meant to read the resolved plan and I
had forgotten `dev:starter-kit` is not dry by default — `dry_run: true` is the
flag, and reading the plan without it applies it. Harmless here (double
materials), recorded because a doubled fixture is invisible in an envelope that
reports only its own call.

## What this run did NOT prove

- **Instant mode.** `--mode instant` was never called, so 3.3's "instant is
  identical to the built result" acceptance is untouched by this run.
- **A refused layout.** The preflight passed 22/22 on the first try. Nothing here
  exercises the per-cell failure shape or the rollback path.
- **A roof designation.** The room roofed itself via `AutoBuildRoofAreaSetter`, as
  session 17's decision predicted for a 35-cell room. `--roof` was not sent, so
  the explicit `area {kind:"build-roof"}` second call is still unexercised.
- **A layout of more than one room**, and anything about `bac4eba`'s module grid.
- **The 640x480 unattended path.** This was a watched run throughout.

## Three defects found, all filed

- `54b0c9a` — `place-layout`'s `shortfall[]` reported `short_by: 185` while 869
  reachable unforbidden WoodLog sat ten cells away, because `in_stockpiles` is
  `map.resourceCounter` and the map had no stockpile zone. The room was then
  built out of that "missing" wood. `construction`'s `missing[]` has it too.
- `36999fd` — `construction` has no layout scope, and silently ignored
  `--layout_id ly-1` while answering whole-map and reporting success.
- Comment on `fc287ba` — there is no state predicate for `advance`, so "advance
  until the room is finished" was two guessed tick counts. Includes the finding
  that this predicate is false-at-start THREE times over, which makes the issue's
  own edge-vs-immediate open question concrete.

## One source correction to the M2 brief

The brief states: *"role: Bedroom needs a bed owner. RoomRoleWorker_Bedroom reads
bed.OwnersForReading, and an unowned bed scores as Barracks."* **That is not true
in 1.6 for a one-bed room**, read in the decompiled tree:

`RimWorld/RoomRoleWorker_Bedroom.IsBedroomHelper` counts an owner-less bed into
`num` when `bed.def.building.bed_emptyCountsForBarracks` (default `true`,
`RimWorld/BuildingProperties.cs:196`), and then opens its verdict with
`if (num == 1 && num2 == 0) { return true; }` — one empty bed, no owned beds, IS
a bedroom. `RoomRoleWorker_Barracks.GetScore` returns `0f` whenever
`RoomRoleWorker_Bedroom.IsBedroom` is true, so it cannot win. The brief's rule
holds from TWO empty beds up (`num > 0` after both single-bed clauses fail →
`return false` → Barracks scores `num * 100100f`).

It made no difference to this run — Izzy claimed the bed anyway — so this is a
source reading, NOT a measurement, and the one-bed-unowned case was never
observed live. Worth knowing before an acceptance suite is written that advances
until someone sleeps in order to satisfy a condition that was already met.
