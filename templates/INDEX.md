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
- **variants** — a genuinely different size is a second `.ir.json` beside the
  first, sharing the .md. Scaling RULES (what a bigger freezer must preserve)
  live in the .md as prose, because they are judgement, which is what this
  repo keeps in markdown.

If freeform resizing turns out to be wanted, the generator belongs beside the
consumer — `rwa`/3.3, where python already lives — consuming these .md rules.
Do not invent a template interpreter ahead of the need.

## Proposed dialect pins (3.3's "IR dialect delta" question)

Four ambiguities ir.py leaves open, resolved here as PROPOSALS — 3.3 pins or
overrides them, and each template's .md prose is normative meanwhile:

0. **Row 0 = north — PINNED**, no longer a proposal. The one above;
   restated here so a reader of this section alone does not miss it. `ir.py`
   now says so in its own docstring.
1. **Multi-cell anchor**: the token sits in the footprint's north-west cell;
   remaining cells are `.`. (A token per occupied cell would make stuff-maps
   and diffs ambiguous.)
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
3. **Layer order**: `layers[0]` = buildings and furniture, `layers[1]` =
   conduits. Build order inside a layout (walls → door → roof → furniture) is
   3.3's open question, not encoded here.

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
and neither adds a further research gate. The enforcing check is still 3.3's preflight —
every cell validated before anything places, per-cell failures in the
`removal`/`reason` shape, nothing placed on any failure. A wrong claim in
these files fails loudly at preflight, not silently on the map. Research
gates are widget gates: `build`/`place-layout` must refuse unresearched defs
(DESIGN §Action model), so a template listing a gated def is honestly
unplaceable until the research lands — the .md says which lines those are.
