# The map panel — specification

The first of the six per-panel sessions element 2 called for. Build root
`ee4b4f8`. Read against `Spatial.cs` (`CropRenderer`), `MapDumpVerbs.cs`,
`SpatialVerbs.cs`, `SiteVerbs.cs`, `baseviz/render.py`, `rwa cmd_render`, and the
decompiled 1.6 source. Every engine member named here was verified by name.

## 0. What already ships

`CropRenderer.Render(map, rect, layers)` — the one ASCII renderer, north-up,
rulers on all edges, legend from glyphs present, `MaxSide = 61`, alphabet
`map-view/ascii-1`, reserved pawn band `?@$!&^;`, layer priority FOG > pawns >
designations > things > zones > roof > terrain. `map-dump` — per-layer RLE planes
with palettes, `north_up: true`, 65,536-cell budget, alphabet
`baseviz-catalog/1`. `rwa render` — deterministic, sha-checked live on
2026-08-31. `site-survey` — the precedent for an overlay as a **second grid**
with its own alphabet id, never mixed into the crop.

Nothing ships for: home-area bbox, turret range/LOS, wind cells, power-net
per cell, gaps, gone. `Building.Destroy` is not hooked; `827c1bf` adds it.

## 1. The crop

Main crop = bounding box of `map.areaManager.Home.ActiveCells`, expanded by 8,
clipped to the map, fitted to 60x50 by the cut order in §6. If the home area is
empty or `autoHomeArea` is off, fall back to the bbox of
`map.listerBuildings.allBuildingsColonist` + 4, and say which source was used.

**Why the home area rather than the agent's rectangle.** The game maintains it:
`AutoHomeAreaMaker.MarkHomeAroundThing` marks a 4-cell ring around every player
building with `expandHomeArea` as it spawns, so the crop grows with the base and
nobody chooses a rectangle. The run's rectangle did not: `map-dump
{rect:[82,74,42,34]}` was the agent's "base footprint" for the whole second day,
fired 32 times (F-XC-11), and at the wipe the rebuilt turrets, the destroyed
autocannon at (98,114) and the north gate were all **outside** it.

**Why +8.** The home ring is already 4; the margin exists so the *outside* of the
wall is in the picture — the unwalled gap the lancer used, the three-sided art
room's open side (F-S02-4). Turret rings at 28.9–32.9 cells never fit any
margin, which is part of why rings leave the ASCII (§4).

**The stop's place.** If it lies inside the main crop, no second crop — mark the
cell and name it in the lines below. If outside, a 21x21 crop centred on it
(`CellRect.CenteredOn`) plus one line giving distance and bearing from the
nearest edge of the home bbox: `NEW 3 hostiles at (125,175) — 55 cells N`. The
run needed exactly this three times: the raid read as "they land inside" from
the word "nearby" when the pods were ~80 cells north (F-S12-15); Ellis at Food 0
90 cells out (F-S03-4); the refugee at (0,140), never reached. Fog is respected
as `CropRenderer` does today; a raid crop in unexplored ground prints `?` and
the line says how many cells are fogged.

Casualty rows need a position for this. `StampPawn` writes no position today —
either `827c1bf` adds `at` from `PositionHeld`, or the screen builder reads it
live. The builder picks.

## 2. The alphabet — `map-view/ascii-2`

Bump `CropRenderer.AlphabetId` and `map-dump`'s `distinct_from` together (the
`e6faa51` rule).

**Terrain, split on the game's own thresholds, legend prints the number:**
`,` fertility ≥ 1.0 · `.` 0.7 ≤ f < 1.0 (gravel) · `_` f < 0.7, not stone
(sand, mud, ice) · `'` stone floor · `-` built floor or bridge · `~` water.

0.7 is the boundary because `Plants_Bases.xml` sets `fertilityMin` 0.7 on the
cultivated base and corn is explicit 0.70, so gravel sows every staple and sand
sows nothing. Today's renderer prints `.` for everything under 1.0 and the run's
own legend read `".": "sand | stony soil | rough granite | soft sand"` — the
collapse that wrote off 131 farmable cells as sand (F-S03-3) and sent Ellis 90
cells out during a food crisis (F-S03-4).

**Structure:** `#` wall · `+` door · `%` plain natural rock · `8` resource rock
(a digit cannot be produced by any label-derived rule, so it needs no push
logic) · `0` steam geyser · `|` power-transmitting building · `W` wind turbine ·
`:` a **blocked** wind-clearance cell only · `Y` any turret · `/` a gap · `X`
gone since the last screen · `*` designation · `=` `"` stockpile/growing zone ·
`T` `t` tree/plant.

`8` and `0` earn their own glyphs from the run: `%` read as
"sandstone | marble | compacted steel" and a rect chosen off it designated 14
cells of rock aimed at ore (`855117a`); four geysers went unused until day 170
while `S` also meant stonecutter's table.

**Pawns keep the reserved band** `@ ! $ & ^ ; ?`. `PawnGlyph` already gets the
case the run's filter missed: `MentalState_Manhunter.ForceHostileTo` returns true
for humanlike factions, so `HostileTo(Faction.OfPlayer)` is true and a manhunter
vulture prints `!`. **Twelve `!` inside the walls would have been on the screen
while `pawns --filter hostile` returned five sleeping mechs** (F-S12-12).

**Layer priority:** FOG > pawns > gone > designations > gap > things >
blocked-wind > zones > terrain. Roof leaves the crop for a request plane, which
frees `:`.

## 3. ASCII versus PNG — the ASCII carries the load

The PNG is rendered after every advance for Dorian's viewer and the audit, and
named on the screen only when the crop changed. It is **not** a standing
invitation for the agent.

The evidence: the text channel was consulted ~90 times (`map-view` 19,
`map-dump` 71) against 10 pictures, none after 12:50 on day two. The PNG's
legibility to an agent is **unverified** — the one fresh reader answered 1 of 3
questions and the re-read is still owed (`f7b6207`). And the run did get real
value from PNGs when it opened them (the unhauled steel scatter F-S01-24, the
three-sided art room F-S02-4) — so the picture is not useless, it is unopened,
and pushing a path does not fix that: T11 applies to "remember to Read the PNG"
exactly as it applies to everything else.

**What would prove this wrong**, measurable on the first cockpit run: count
`Read` calls on `frames/*.png` per screen — if the agent opens it on one screen
in three or more where `changed` was non-empty, shrink the ASCII to fund it.
Burn a 3-character per-frame key into the PNG corner and count echoes. And run
`f7b6207`'s fresh-reader test on the **ASCII** crop with ascii-2; if the ASCII
fails where the PNG passes, flip the load.

## 4. What is drawn

**Every screen, in the crop:** terrain bands, walls/doors/rock/ore, geysers,
conduits, turrets, turbines with blocked cells, gaps, pawns, zones,
designations, gone. Under it, three lines — `GONE` (from `827c1bf`'s destroyed
rows with the game's `DestroyMode`, held until rebuilt or written off), `NEW`,
and `OPEN` (perimeter openings with gun coverage).

**Gaps and "inside", defined:** inside = home-area cells whose `GetRoom` has
`!room.TouchesMapEdge`. An opening = a `Walkable` cell whose room touches the
map edge and is 4-adjacent to an inside cell; a gate if `IsDoor`, else a gap. A
base with no enclosure prints `OPEN: no enclosure` — the fact the run never had.

**Gun coverage**, computed here and printed by the GUNS gauge: per turret, range
= `AttackVerb.EffectiveRange`, min = `verbProps.EffectiveMinRange` — the exact
two numbers `Building_TurretGun.DrawExtraSelectionOverlays` draws — and
`GenSight.LineOfSight(turret.Position, cell, map, skipFirstCell: true)`, the same
call `Verb.TryFindShootLineFromTo` makes.

**Not in the ASCII, always in the PNG, available as `map-dump` planes on
request:** turret range rings and per-cell coverage; full wind clearance;
power-net membership per cell; roof; rooms. **This is the one place the spec
departs from the mock's "turret range drawn"** — a ring at r≈29 does not fit a
60x50 crop and seven overlapping rings overwrite the walls, doors and gaps that
carry the information, while the fact the run needed was a count and a per-gate
answer, which the OPEN line and the gauge give. It is also the call most likely
to be reversed by §3's measurement.

## 5. Overlays as data — verified by member

| overlay | the game's draw member | the data member to read instead | cost |
|---|---|---|---|
| turret range | `Building_TurretGun.DrawExtraSelectionOverlays` → `GenDraw.DrawRadiusRing` | `AttackVerb`, `Verb.EffectiveRange`, `VerbProperties.EffectiveMinRange`, `GenRadial.RadialCellsAround` | cheap |
| range at placement | `PlaceWorker_ShowTurretRadius.AllowsPlacing` — **not** `DrawGhost` | `VerbProperties.range`, `minRange` | cheap |
| line of sight | none drawn | `GenSight.LineOfSight` → `GenGrid.CanBeSeenOverFast` → `Building.CanBeSeenOver` | ~400k edifice lookups per stop — **measure on the 38-mod bench** |
| wind clearance | `PlaceWorker_WindTurbine.DrawGhost` → `WindTurbineUtility.CalculateWindCells` | the same utility; blocked cells recomputed from `roofGrid.Roofed` and `def.blockWind` (`windPathBlockedCells` is private) | cheap |
| power net per cell | not drawn per cell | `PowerNetGrid.TransmittedPowerNetAt`; `CompPower.PowerNet` null = connected to nothing | cheap |
| home area | — | `AreaManager.Home`, `Area.ActiveCells` | cheap |
| gone | — | `827c1bf` postfixes on `Building.Destroy` and `Frame.Destroy` | none per screen |

**Two corrections to `ee4b4f8`'s scope text:** `DrawExtraSelectionOverlays` and
`DrawGhost` are draw calls returning nothing and cannot be run headless — each
overlay is re-derived from the member the draw reads, and there is no generic
route, so a modded def with its own PlaceWorker gets no overlay and the plane
must say so. And the turret's placement ring lives in `AllowsPlacing`.

**Hazard:** `GetRoom` on a crop can trigger a region rebuild on the main thread
(`d16a463`). Acceptable at a stop; measure.

## 6. Size and cost

Measured from the run: a 60x55 terrain-only `map-view` is 3,355 bytes of rows,
3,752 with rulers. A 60x50 crop with rulers and legend ≈ 3.7 KB; the 21x21 stop
crop ≈ 0.55 KB. `MaxSide = 61` already admits 60x50.

**Tokens: UNKNOWN.** No tokenizer on this box, and the two effects pull opposite
ways — long glyph runs merge cheaply, mixed structure does not. Plausibly
1,200–2,500 tokens, which is most of the 2–4k screen budget. Settled by
`count_tokens` on one real `frames/<tick>.json` map panel from the first run.

**Cut order when over budget:** margin 8→4; the ruler's units line; plants and
items collapse to `t`/`i`; margin 4→0; window to 48x40 around the stop's place
with the header saying `48x40 of 71x62 shown`. **Never cut:** walls, doors,
gaps, pawns, gone, turrets, turbines, the three lines, the legend.

## Acceptance (adds to `ee4b4f8`'s)

Three terrain glyphs with fertility in the legend · ore `8` distinct from rock
`%` · a geyser named from the first screen · a manhunter prints `!` while
`pawns --filter hostile` is unchanged · a `dev:*`-destroyed turret prints `X`
and a GONE line, cleared by rebuilding without a `write-off` · a walled compound
with one door and one one-cell gap prints both on OPEN with bearing-gun counts ·
a raid 80 cells out produces a second crop and a bearing line, one inside
produces none · the panel re-renders byte-identically from `frames/<tick>.json` ·
the first run reports PNG `Read` count per screen and the token count of one
map panel.

## Left to the builder

The push rule for label letters colliding with the reserved set; the `changed`
predicate; whether the raid crop centres on the hostiles' bbox or the letter's
target when they disagree; where casualty `at` is added; the PNG frame-key
mechanism and whether `render` writes a transcript step (it should); the
`cockpit/` viewer, which redraws only on `map-view` today (F-S03-5) and must read
`frames/`; whether sandbags and embrasures deserve a structural glyph.
