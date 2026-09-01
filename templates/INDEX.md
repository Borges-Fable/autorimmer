# templates/ — layouts with their lessons baked in

The escalation ladder's structural rung: a check that verified the same
property every time becomes a template that makes the property true by
construction, and the check retires (4.4: "escalation removes"). The firefoam
popper in `power-room` is DESIGN's own canonical example of a lesson that
stopped being a checklist line by becoming geometry.

| template | footprint | research gate | the lessons it carries |
|---|---|---|---|
| `bedroom` | 5×7 | none (3.3's own rehearsal size) | enclosure, auto-roof, ownership-or-Barracks |
| `freezer-kitchen` | 11×6 | AirConditioning (+Electricity for the conduit spine) | cold that is checked, clean that is measured, haul paths short, 400 W that has to come from somewhere |
| `power-room` | 7×7 | Electricity (+Batteries, +Firefoam) | the popper, the one deliberate conduit, hidden conduit for the rest, banks sized to their own explosion |

## Format: annotated IR, in two halves

Each template is a pair. **`<name>.ir.json`** is the machine half — the
baseviz IR dialect (`baseviz/ir.py`): `defName`, `size [w,h]`, `layers` of
row-major token grids, `terrain`, `roof`. **`<name>.md`** is the lesson
half — every placement decision that came from a lesson, cited. JSON has no
comment channel, so the .md IS the "templates carry their lessons as
comments" invariant; a patch that touches one half touches both or it is not
done.

**ROW 0 IS NORTH — PINNED 2026-09-01** (git-bug `bac4eba`; it had been
PROPOSED since session 10 and assigned to 3.3, which had not reached it). The
pin moved off 3.3 because it is a decision about this directory and
`baseviz/`, neither of which is C#: the mod has no opinion on IR orientation
and would not acquire one until `place-layout` exists.

North-up was already the convention everywhere that had one —
`baseviz/render.py` ("row 0 is z = oz + h - 1"), AutoRimmer's `CropRenderer`
("north at top") and `map-dump`'s `north_up: true`. Only `ir.py` was silent,
saying just "the top row as written in the XML", which is an XML-order
statement that takes no compass position. It now says north; the format
declares its own orientation rather than leaving it to each consumer.

It was load-bearing, exactly as this file argued: `Building_Cooler.TickRare`
cools `Position + IntVec3.South.RotatedBy(Rotation)` and vents to
`Position + IntVec3.North.RotatedBy(Rotation)`, so `freezer-kitchen`'s two
`Cooler_North` cells in row 0 chill row 1 only under north-up. Under south-up
they would have refrigerated the outdoors.

**And pinning it found a real bug — in the stove, not the coolers.** The
corpus was checked against the pin rather than assumed to agree with it.
`freezer-kitchen` carried `FueledStove_South`, whose interaction cell lands in
the north wall: `FueledStove` is `interactionCellOffset (0,0,-1)`, so
`Rot4.South` rotates the pawn's cell 180° to the NORTH of the stove, which is
row 0 — a `Wall`. It is now `FueledStove_North`, interaction cell row 2, in
the kitchen interior. The check is independent of the multi-cell anchor
convention: it gives the same answer whether the token is read as a corner or
as a centre.

That is a convention bug rather than a typo, and pin 2 below is tightened to
close it. `FueledStove` is the only interaction-cell-bearing rotated token in
the whole corpus — `Bed`, `Battery`, `Cooler`, `TorchLamp`,
`WoodFiredGenerator` and `FirefoamPopper` all have `hasInteractionCell`
unset — so the audit is complete, not a sample.

## Parameters — the resolved open question

No substitution syntax. A template is a concrete worked instance plus three
parameter channels that already exist at the placement verb (3.3):

- **origin and rotation** — `place-layout --origin P` args; nothing encoded.
- **stuff** — tokens are bare defNames (`Wall`, not `Wall_WoodLog`); material
  is bound at placement via `--stuff-map`. Where a lesson CONSTRAINS the
  material, the .md carries a `constraint:` line (power-room walls must be
  Flammability 0) — a constraint violation is a reason to refuse, and 3.3's
  stuff resolution is already required to be explicit, never silent.

  **This is now enforced by the client refusing to guess.** `rwa place-layout`
  splits ONLY the four `Rot4` words off a token, so a KCSG-style
  `Wall_WoodLog` is sent as a def name and the mod refuses it by name ("no
  ThingDef or TerrainDef named 'Wall_WoodLog'"). Telling a material suffix from
  a def name needs the def database, which lives on the other side of the
  bridge, and a silent split would invent a def. The stuff-map format is
  `defName -> stuff defName` with `*` as the default for every `MadeFromStuff`
  def it does not name; an element's own `stuff` beats both; and each placement
  publishes `stuff_source` (`element` | `stuff_map` | `stuff_map:*` |
  `game-default`) so a default is named rather than silent. `strict_stuff:true`
  refuses to default at all.
- **variants** — a genuinely different size is a second `.ir.json` beside the
  first, sharing the .md. Scaling RULES (what a bigger freezer must preserve)
  live in the .md as prose, because they are judgement, which is what this
  repo keeps in markdown.

If freeform resizing turns out to be wanted, the generator belongs beside the
consumer — `rwa`/3.3, where python already lives — consuming these .md rules.
Do not invent a template interpreter ahead of the need.

## Dialect pins — ALL FOUR NOW PINNED (3.3's "IR dialect delta" question)

Four ambiguities `ir.py` left open. They were PROPOSALS here until 2026-09-01;
**3.3 has now shipped `place-layout` against all four and pins them**, so this
section is normative rather than provisional. Where the verb's behaviour and
this list ever disagree, the verb is the thing that runs:
`Source/AutoRimmer/LayoutVerbs.cs`'s class header carries the reasoning and the
member citations, and DESIGN's decisions log (2026-09-01, session 17) carries
the rulings.

0. **Row 0 = north — PINNED**, no longer a proposal. The one above;
   restated here so a reader of this section alone does not miss it. `ir.py`
   now says so in its own docstring.
1. **Multi-cell anchor — PINNED.** The token sits in the footprint's
   north-west cell; remaining cells are `.`. (A token per occupied cell would
   make stuff-maps and diffs ambiguous.)

   `place-layout` implements exactly this and publishes it as
   `anchor: "north-west"` on every call. Note that it is deliberately NOT the
   corner `build --at` and `find-rect` use, which is the SOUTH-WEST corner —
   `[x,z,w,h]`'s own `x,z`. The two cannot be unified, because converting
   north-west to south-west needs the def's ROTATED size (the game's
   `AdjustForRotation` axis swap), and `rwa` has no def database. So the
   conversion happens mod-side, and every placement publishes `at` (the token
   cell), `pos` (the game's placement centre) and `footprint` (`[x,z,w,h]`,
   south-west anchored) so the three can never be confused.

   The `Bed` is what makes a wrong conversion visible: it is size (1,2), so its
   north-west cell and its south-west corner differ by exactly one, and a
   conversion that read the token as the south-west corner would put
   `bedroom`'s bed through the north wall. `accept/1adc737-place-layout.py`
   check 2.4 asserts the footprint by coordinate for that reason.
2. **Rotation suffix — PINNED, and tightened.** `_North/_South/_East/_West`
   is the **`Rot4` value, verbatim**, as the game names it and as `map-dump`
   publishes it. It is NOT a description of which way the thing faces.

   The old wording ("the direction the def FACES — a bed faces its foot; a
   cooler's suffix is its exhaust/hot side") read naturally and was wrong in a
   way that could not be caught by reading, because "faces" means something
   different per def. A cooler at `Rot4.North` does vent north, so
   `Cooler_North` happened to be right. A workbench at `Rot4.North` is one a
   pawn uses from the SOUTH, because `interactionCellOffset` is `(0,0,-1)` in
   the def's unrotated frame — so an author writing "the stove faces south"
   wrote `FueledStove_South` and got a stove the pawn must stand in a wall to
   use. One gloss, two opposite meanings, and only one of the two defs in the
   corpus exercised the difference.

   A `Rot4` value has exactly one meaning and round-trips through `map-dump`,
   so the suffix is data now, not prose. Same rule as `00a1be7`: read by
   field, never through a description. Where a template's .md wants to say
   which way something faces, it says it in the .md — that is what the .md is
   for.
3. **Layer order — PINNED**: `layers[0]` = buildings and furniture,
   `layers[1]` = conduits.

   **Build order inside a layout is resolved and is NOT encoded here**, because
   it turned out not to be a property of the layout at all.
   `place-layout` places TERRAIN FIRST and everything else in the caller's
   order, and that is the whole rule. Terrain first because
   `GenConstruct.CanPlaceBlueprintAt`'s occupancy loop refuses a floor asked
   for under a building whose affordance the floor does not provide, while the
   same building over the same floor is governed by `CanPlaceBlueprintOver`'s
   `CoexistsWithFloors` branch and is not — an asymmetric rule whose safe half
   is floor-first. Everything else is SYMMETRIC: a conduit may go under a wall
   and a wall over a conduit (`canBuildNonEdificesUnder` /
   `IsEdificeOverNonEdifice`, which is why `power-room`'s deliberate conduit in
   a wall cell works either way), two edifices in one cell fail both ways, and
   the two interaction rules refuse in both directions. A layout that violates
   them is a broken layout, not a mis-ordered one — so "walls → door → roof →
   furniture" buys nothing, and the template corpus needs no order field.

   Nor does the mod stage the work: `WorkGiver_ConstructDeliverResources` and
   `WorkGiver_ConstructFinishFrame` have no notion of walls-before-furniture,
   so staging could only mean withholding blueprints from the colony, which is
   a scheduler the game does not have.

## Validation

Footprints and research gates were read from the bench's own def XML this
session (Bed 1×2, FueledStove 3×1, WoodFiredGenerator 2×2, Battery 1×2,
Cooler/TorchLamp/popper 1×1; Cooler needs AirConditioning + construction 5,
WoodFiredGenerator needs Electricity, Battery needs Batteries, popper needs
Firefoam + construction 5). Conduits added session 10: `PowerConduit` 1×1,
1 Steel, Flammability 0.7; `HiddenConduit` 1×1, 2 Steel, Flammability 0,
`canBeDamagedByAttacks false`, `WorkToBuild` 280 — a SEPARATE def with
`ParentName="PowerConduit"`, so it transmits power but is never in
`ShortCircuitUtility.GetShortCircuitablePowerConduits`, which matches
`ThingsOfDef(ThingDefOf.PowerConduit)` by def. Both inherit **Electricity**
and neither adds a further research gate. The enforcing check is 3.3's preflight, which now
ships: every cell validated before anything places, per-cell failures in the
`removal`/`reason` shape, nothing placed on any failure. It is worth running
the corpus through it once as a CORPUS CHECK rather than only per-placement at
play time — `rwa place-layout <template> --origin P --dry-run` does exactly
that, and it is the check that would have caught `FueledStove_South` from the
game's side (`CanPlaceBlueprintAt` runs `InteractionCellStandable`) rather than
from a reading of the pin. A wrong claim in
these files fails loudly at preflight, not silently on the map. Research
gates are widget gates: `build`/`place-layout` must refuse unresearched defs
(DESIGN §Action model), so a template listing a gated def is honestly
unplaceable until the research lands — the .md says which lines those are.
