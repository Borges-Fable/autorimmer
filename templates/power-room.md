# power room — 7×7, the canonical template

DESIGN's own example of the escalation ladder's last rung: "the power-room
template grows a firefoam popper." This file is that sentence made into
geometry, with every number read from source this session. Once rooms are
built from this template, "check popper coverage" leaves the daily checklist
for them (4.4: escalation removes) — what stays behind is the
`power-incident` trigger, because the protection is one-shot.

    W W W W W W W       row 0 = north (proposed — templates/INDEX.md)
    W . . . . . W
    W G g . . B W       G = WoodFiredGenerator (2×2, anchor NW)
    * g g h h b W       B = Battery_South (1×2); * = Wall over PowerConduit
    W . . P . B W       P = FirefoamPopper; h = HiddenConduit
    W . . . . b W
    W W W D W W W
    (sketch; the .ir.json is the artifact — g/b mark footprint cells)

The two `h` cells are not decoration and they are not ordinary conduit. They
are the fix for a real defect this template shipped with: without them the
batteries are **on a different power net from the generator** and never
charge. See "Why the bank needs the hidden conduit" below.

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

So the template's interior has **no SHORTABLE conduit at all**. The single
`PowerConduit` sits UNDER the west wall, cardinally adjacent to the generator
— the room's one reachable spark point, buried in stone. The two interior
cells that link the bank are `HiddenConduit`, which is a different def and
therefore never a candidate (below). `spawnConduits` is `false` in the IR
**on purpose**: auto-laced conduit would re-scatter Zzzt candidates through
the room this layout just cleared. Extend the exterior run FROM that wall
cell, keep it short, and keep it away from bedrooms — you are choosing where
the explosion lands.

(Near-empty banks change the failure, not the site: with no battery on the
culprit's net holding more than 20 Wd, the same event starts a FIRE within 3
cells of the conduit instead — `ShortCircuitUtility.DoShortCircuit` branches
on `powerNet.batteryComps.Any(x => x.StoredEnergy > 20f)`, and the fire branch
is `TryStartFireNear`, line-of-sight — which the wall blocks and the popper
watches anyway.)

## Why the bank needs the hidden conduit

### Transmitters must TOUCH; only connectors get the 6 cells

This is the distinction that made the defect invisible, so it is worth
stating flat:

| kind | how it joins a net | the rule |
|---|---|---|
| **transmitter** — conduit, battery, generator | flood-fill over cardinal adjacency | must **physically touch** |
| **connector** — stove, cooler, lamp, bench | nearest transmitter searched in a box | within **6 cells** |

`Battery`'s `CompProperties_Battery` carries `transmitsPower true`, so a
battery is a TRANSMITTER, and `PowerNetMaker.ContiguousPowerBuildings` grows a
net by flood-filling `GenAdj.CellsAdjacentCardinal` over buildings whose
`TransmitsPowerNow` is set. Nothing in that walk is distance-based. The
6-cell rule people remember is `PowerConnectionMaker
.BestTransmitterForConnector`'s `ExpandedBy(6)`, and it is reached only for
things with `def.ConnectToPower` — appliances. "Within 6 cells" is true for a
stove and **false for a battery**. A battery never cords to anything; it
either abuts the net or it is a net of its own.

**This template shipped the wrong half of that.** Batteries at col 5, the
generator's east edge at col 2, cols 3–4 empty, `spawnConduits:false` — two
nets: generator + wall conduit, and a battery pair that never charges. Every
headline number in this file was false at once. The bank never held the charge
the sizing table prices, and a Zzzt at the wall conduit took the
`TryStartFireNear` branch rather than the explosion branch, because the
culprit's net had no battery on it. The room looked right on the map and was
protecting nothing.

### Why HiddenConduit and not PowerConduit

`HiddenConduit` is a **separate ThingDef**, `ParentName="PowerConduit"`, and
that inheritance is the whole trick: it keeps the parent's
`CompProperties_Power`/`CompPowerTransmitter` with `transmitsPower true`, so
it carries power and joins nets exactly like a conduit — while
`ShortCircuitUtility.GetShortCircuitablePowerConduits` builds the Zzzt
candidate list from `map.listerThings.ThingsOfDef(ThingDefOf.PowerConduit)`,
matched **by def, not by parent**. A `HiddenConduit` is therefore never a
candidate. (`DoShortCircuit`'s own letter-text branch uses the same def
identity test, `culprit.def != ThingDefOf.PowerConduit`.)

It also brings `Flammability 0` (against `PowerConduit`'s 0.7) and
`canBeDamagedByAttacks false`, which is exactly the right pair for a cell
inside a room built around a fuel-burning generator.

The cost is real but small: `WorkToBuild` 280 against 35, 2 Steel against 1,
`MaxHitPoints` 100. **No extra research gate** — it declares no
`researchPrerequisites` of its own and inherits `PowerConduit`'s
**Electricity**, which this template already requires for the generator. And
it can go under walls and other buildings for the same inherited reasons an
ordinary conduit can (`building.isEdifice false`, `clearBuildingArea false`,
and `PlaceWorker_Conduit`, which refuses only a cell that already holds a
power transmitter). Vanilla lays it the same way we do — `RoomPart_Connect
Conduits.CreateHiddenConduitPath` spawns it along a **cardinal-only** path,
which is the touching rule again, in the game's own hand.

So the fix keeps the layout: two `HiddenConduit` cells at row 3, cols 3–4,
bridging the generator's east edge to the bank. The alternative — moving the
batteries to abut the generator — also fixes the net, and is rejected because
it solves the wrong half: this template exists FOR Zzzt safety, and running
ordinary conduit or shuffling geometry leaves the spark point merely buried
instead of removed.

**One honest limit.** Hidden conduit removes the spark point from the stretch
you make hidden — it does not immunise the NET. `DoShortCircuit` drains every
battery on the culprit's net and explodes at the culprit, so the moment this
room's net reaches the base's ordinary conduit grid, the first `PowerConduit`
out there is a candidate holding this bank's full charge. That is the argument
for the west-wall cell staying a deliberate, known, stone-buried
`PowerConduit` rather than pushing the surprise somewhere unplanned — and the
argument for keeping the bank small (sizing table below).

## Battery bank sizing — the room's own damping term

Stored energy prices the blast: radius = `sqrt(Wd) × 0.05`.

| bank | full charge | Zzzt radius | note |
|---|---|---|---|
| 2 × Battery (this template) | 1,200 Wd | 1.7 | clamp floor is 1.5 — near-minimal |
| 5 | 3,000 Wd | 2.7 | still Flame-only |
| 10 | 6,000 Wd | 3.9 | now adds the Bomb sub-blast (> 3.5) |
| 25 | 15,000 Wd | 6.1 | "large" letter territory |

Growth rule: prefer a SECOND small bank on a separate net over one large
bank — `DoShortCircuit` drains only the culprit's net. A separate net means a
separate GENERATOR too: a battery-only net has no way to charge, and
`PowerNet.IsPowerSource` counts a bare battery, so the digest's `nets` will
happily report it as powered. And batteries are
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
  safety, not comfort. (This room is inside the auto-roof envelope —
  `AutoBuildRoofAreaSetter.TryGenerateAreaNow` bails only above 26 regions or
  320 cells, as `bedroom.md` states; 49 cells is nowhere near it. Blueprint
  mode gets the roof AREA for free; instant mode needs the grid.)
- variants: more batteries extend the east column — and each new battery must
  stay cardinally adjacent to a battery already on the bank, or extend the
  `HiddenConduit` spur to reach it. A battery placed "near" the bank is a
  second net (see above). The sizing table IS the scaling rule; re-check every
  cell stays within 3 of the popper or add a second popper.
- research gates, in the order they unlock: **Electricity** (generator, and
  both conduit defs — `HiddenConduit` inherits that prerequisite and adds
  none of its own), **Batteries**, **Firefoam** (popper, + construction 5).
  Before Firefoam the
  template is placeable minus its popper line — and the daily structural
  check keeps flagging the room until the popper exists, which is the
  system working, not a nuisance.

## Placement

`place-layout templates/power-room.ir.json --mode blueprint` (3.3 — the verb
does not exist yet; `1adc737` is open). Fuel the generator (wood — the
standing designation loop again), then **prove the bank is on the generator's
net**, then extend the exterior conduit run. Read the roof back rather than
assuming it: the auto-roof queue runs next tick, and this room's protection
depends on the roof (see rain rule). No landmark needed — every read this
room wants is already in the digest's power section.

**The net check, and why the obvious one does not work.** `power
.nets_with_generator ≥ 1` and `batteries: 2` BOTH PASS ON A SPLIT NET, so
neither is evidence of anything: `DigestVerb.PowerSection` sums `batteries`
across every net on the map and counts `nets_with_generator` per net, so a
generator net plus an orphan battery pair scores 1 and 2 exactly like a
correct room. Two reads that do discriminate:

- **structural, free, immediate — `power.nets == power.nets_with_generator`.**
  `PowerNet.IsPowerSource` returns true for any `CompPowerBattery`, so a
  battery-only net still counts in `nets` while contributing nothing to
  `nets_with_generator`. Any gap between the two numbers IS an orphaned bank.
  A powered net with no generator can never charge, so this is a standing
  colony invariant and not merely a template check.
- **behavioural — `power.stored_wd` climbing across successive reads.** An
  orphan bank sits at 0 forever.

`battery_days` answers a different question and cannot be used here: it is
`stored_wd / (draw − gen)` and is **null whenever generation covers draw** —
which is the healthy state of a freshly fuelled power room *and* the broken
one. It is a runway figure for a colony in deficit, not a charging signal.
