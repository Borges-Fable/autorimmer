# templates/ — layouts with their lessons baked in

The escalation ladder's structural rung: a check that verified the same
property every time becomes a template that makes the property true by
construction, and the check retires (4.4: "escalation removes"). The firefoam
popper in `power-room` is DESIGN's own canonical example of a lesson that
stopped being a checklist line by becoming geometry.

| template | footprint | research gate | the lessons it carries |
|---|---|---|---|
| `bedroom` | 5×7 | none (3.3's own rehearsal size) | enclosure, auto-roof, ownership-or-Barracks |
| `freezer-kitchen` | 11×6 | AirConditioning | cold that is checked, clean that is measured, haul paths short |
| `power-room` | 7×7 | Electricity (+Batteries, +Firefoam) | the popper, the one deliberate conduit, banks sized to their own explosion |

## Format: annotated IR, in two halves

Each template is a pair. **`<name>.ir.json`** is the machine half — the
baseviz IR dialect (`baseviz/ir.py`): `defName`, `size [w,h]`, `layers` of
row-major token grids, `terrain`, `roof`, with **row 0 = north** (the IR
mirrors KCSG XML order; ir.py's own docstring). **`<name>.md`** is the lesson
half — every placement decision that came from a lesson, cited. JSON has no
comment channel, so the .md IS the "templates carry their lessons as
comments" invariant; a patch that touches one half touches both or it is not
done.

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

Three ambiguities ir.py leaves open, resolved here as PROPOSALS — 3.3 pins or
overrides them, and each template's .md prose is normative meanwhile:

1. **Multi-cell anchor**: the token sits in the footprint's north-west cell;
   remaining cells are `.`. (A token per occupied cell would make stuff-maps
   and diffs ambiguous.)
2. **Rotation suffix**: `_North/_South/_East/_West` on rotatable defs —
   the direction the def FACES (a bed faces its foot; a cooler's suffix is
   its exhaust/hot side).
3. **Layer order**: `layers[0]` = buildings and furniture, `layers[1]` =
   conduits. Build order inside a layout (walls → door → roof → furniture) is
   3.3's open question, not encoded here.

## Validation

Footprints and research gates were read from the bench's own def XML this
session (Bed 1×2, FueledStove 3×1, WoodFiredGenerator 2×2, Battery 1×2,
Cooler/TorchLamp/popper 1×1; Cooler needs AirConditioning + construction 5,
WoodFiredGenerator needs Electricity, Battery needs Batteries, popper needs
Firefoam + construction 5). The enforcing check is still 3.3's preflight —
every cell validated before anything places, per-cell failures in the
`removal`/`reason` shape, nothing placed on any failure. A wrong claim in
these files fails loudly at preflight, not silently on the map. Research
gates are widget gates: `build`/`place-layout` must refuse unresearched defs
(DESIGN §Action model), so a template listing a gated def is honestly
unplaceable until the research lands — the .md says which lines those are.
