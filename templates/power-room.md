# power room — 7×7, the canonical template

DESIGN's own example of the escalation ladder's last rung: "the power-room
template grows a firefoam popper." This file is that sentence made into
geometry, with every number read from source this session. Once rooms are
built from this template, "check popper coverage" leaves the daily checklist
for them (4.4: escalation removes) — what stays behind is the
`power-incident` trigger, because the protection is one-shot.

    W W W W W W W       row 0 = north
    W . . . . . W
    W G g . . B W       G = WoodFiredGenerator (2×2, anchor NW)
    * g g . . b W       B = Battery_South (1×2); * = Wall over PowerConduit
    W . . P . B W       P = FirefoamPopper
    W . . . . b W
    W W W D W W W
    (sketch; the .ir.json is the artifact — g/b mark footprint cells)

## The popper, precisely

- **Its trigger is 3 cells; its foam is 9.9.** The def carries a
  `CompProperties_ProximityFuse` (target Fire, **radius 3**) and a
  `CompProperties_Explosive` (**explosiveRadius 9.9**, damage Extinguish).
  The generous number is the foam; the BINDING one is the fuse. Place the
  popper within 3 of what will burn, not within 9.9 — here it sits ≤ 2.9
  cells from every generator and battery cell, and 2 from the doorway a
  spreading fire would enter by.
- **It is one-shot, and re-protection is not free.** Popping DESTROYS the
  building; `Building_FirefoamPopper.Destroy` then places a rebuild
  BLUEPRINT — but only if auto-rebuild armed, which `SpawnSetup` sets only
  for a player popper standing in Home with Firefoam researched. The rebuild
  costs 75 steel + 1 component + construction 5. After any pop the room is
  unprotected until a builder finishes — the `power-incident` trigger in
  `checklists/triggered.md` exists for that gap.
- **The popper itself burns** (Flammability 1.0 in its def). Foam does not
  protect the device that already popped; stone around it does.

## Why the one conduit is where it is

A Zzzt happens **only at a conduit**: `ShortCircuitUtility
.GetShortCircuitablePowerConduits` enumerates `ThingsOfDef(PowerConduit)` on
powered nets — nothing else is ever the culprit. And the blast is centred ON
the culprit: batteries on its net dump everything
(`DrainBatteriesAndCauseExplosion`), radius `sqrt(storedWd) × 0.05`, clamped
1.5–14.9, Flame — plus a Bomb blast at 30% radius once over 3.5.

So the template's interior has **no conduit at all**, and that is possible
because appliances cord to a transmitter within 6 cells
(`PowerConnectionMaker.BestTransmitterForConnector`, `ExpandedBy(6)`) and the
generator is itself a transmitter: both batteries cord to it directly. The
single conduit sits UNDER the west wall, orthogonally adjacent to the
generator — the net's one interior-reachable spark point, buried in stone.
`spawnConduits` is `false` in the IR **on purpose**: auto-laced conduit would
re-scatter Zzzt candidates through the room this layout just cleared.
Extend the exterior run FROM that wall cell, keep it short, and keep it away
from bedrooms — you are choosing where the explosion lands.

(Near-empty banks change the failure, not the site: below 20 Wd stored the
same event starts a FIRE within 3 cells of the conduit instead —
`TryStartFireNear`, line-of-sight — which the wall blocks and the popper
watches anyway.)

## Battery bank sizing — the room's own damping term

Stored energy prices the blast: radius = `sqrt(Wd) × 0.05`.

| bank | full charge | Zzzt radius | note |
|---|---|---|---|
| 2 × Battery (this template) | 1,200 Wd | 1.7 | clamp floor is 1.5 — near-minimal |
| 5 | 3,000 Wd | 2.7 | still Flame-only |
| 10 | 6,000 Wd | 3.9 | now adds the Bomb sub-blast (> 3.5) |
| 25 | 15,000 Wd | 6.1 | "large" letter territory |

Growth rule: prefer a SECOND small bank on a separate net over one large
bank — `DoShortCircuit` drains only the culprit's net. And batteries are
wealth twice over: they price the next raid ([[wealth-buys-bigger-raids]])
and they price their own explosion. Store what the deficit math needs
(`power-deficit`, `checklists/turn.md`), not what feels safe.

## Parameters and constraints

- **constraint: wall stuff Flammability 0** (stone blocks). This is the
  template whose material is not a taste: it contains a fuel-burning
  generator, a flammable popper, and a designed explosion site.
- **constraint: roof stays whole.** Rain short-circuits any battery holding
  > 100 Wd — a 1.9-radius Flame explosion at the battery itself
  (`TryShortCircuitInRain`). The roof grid is all-1 and load-bearing for
  safety, not comfort. (35–49-cell rooms auto-roof in blueprint mode;
  instant mode needs the grid.)
- variants: more batteries extend the east column — the sizing table above
  IS the scaling rule; re-check every cell stays within 3 of the popper or
  add a second popper.
- research gates, in the order they unlock: **Electricity** (generator),
  **Batteries**, **Firefoam** (popper, + construction 5). Before Firefoam the
  template is placeable minus its popper line — and the daily structural
  check keeps flagging the room until the popper exists, which is the
  system working, not a nuisance.

## Placement

`place-layout templates/power-room.ir.json --mode blueprint` (3.3). Fuel the
generator (wood — the standing designation loop again), confirm both
batteries corded (`power.nets_with_generator ≥ 1`, `batteries: 2` in the
digest), and extend the exterior conduit run. Read the roof back rather than
assuming it: the auto-roof queue runs next tick, and this room's protection
depends on the roof (see rain rule). No landmark needed — every read this
room wants is already in the digest's power section.
