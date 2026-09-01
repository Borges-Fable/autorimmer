using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoRimmer
{
    // =========================================== git-bug 261f2e9 =============
    // TEMPERATURE: the target the agent can set, and the room it can SEE.
    //
    // The issue is an ACTUATOR gap sitting on an OBSERVATION blind spot, and
    // the blind spot is the dangerous half. Session 18 built
    // `templates/freezer-kitchen`, wired it, watched `digest.power.draw_w` go
    // 0 -> 40, and read 14.6 C against a 16 C outdoors. Nothing was broken: a
    // `Cooler` carries `CompTempControl` whose `Props.defaultTargetTemperature`
    // is 21 C, and a cooler asked to hold 21 C in a 14.6 C room does exactly
    // nothing (`GenTemperature.ControlTemperatureTempChange` with a negative
    // energyLimit returns `Min(Max(target - roomTemp, b), 0)` = 0), which is
    // what a 40 W draw across two 200 W coolers at
    // `Props.lowPowerConsumptionFactor` 0.1 means. The building worked. Nobody
    // could tell it what to hold, and nothing in the observation surface said
    // the freezer was at room temperature.
    //
    // ===================== WHERE THE GATE LIVES, CITED ======================
    // DESIGN's Action model: a player verb re-implements the WIDGET's
    // precondition and cites it by file + member. The widget here is
    // `RimWorld/CompTempControl.CompGetGizmosExtra`, which yields FIVE
    // `Command_Action`s — -10, -1, "reset to 21", +1, +10 — and the four
    // offset ones run `CompTempControl.InterfaceChangeTargetTemperature`:
    //
    //     TargetTemperature += offset;
    //     TargetTemperature = Mathf.Clamp(TargetTemperature, -273.15f, 1000f);
    //
    // **THAT CLAMP IS THE ONLY GATE, AND THE ISSUE GOT IT WRONG.** 261f2e9
    // says "the def-side `TemperatureControlProps` carry `minTemperature` /
    // `maxTemperature`, which is the range the UI slider is clamped to", and
    // asks for a REFUSAL below it. Three things are false in that sentence and
    // they were checked rather than assumed:
    //
    //  1. There is no slider. There are five buttons.
    //  2. The fields are `CompProperties_TempControl.minTargetTemperature`
    //     (-50) and `.maxTargetTemperature` (50), not `minTemperature` /
    //     `maxTemperature`.
    //  3. **NOTHING IN THE 1.6 TREE READS EITHER FIELD.** Grepped unpiped over
    //     the whole decompiled source: the only two hits are the declarations
    //     in `RimWorld/CompProperties_TempControl.cs`. They are dead.
    //
    // So a player can walk a cooler's target down to -273.15 with the -10
    // button and the game does not stop them. A verb that refused at -50 would
    // refuse something a player can do, which is the Action model broken in the
    // other direction — the same class of error as bypassing a gate. The def
    // range is therefore published on every row as `def_min_c`/`def_max_c`
    // with `def_clamp_enforced: false` and an ADVISORY when the target lies
    // outside it, and the refusal is the real clamp. See DESIGN's decisions log.
    //
    // ============================ THE VENT ==================================
    // 261f2e9 asks whether `Building_Vent` belongs in this verb or is a `flick`
    // case. It is a `flick` case, from the def rather than from judgement:
    // `Data/Core/Defs/ThingDefs_Buildings/Buildings_Temperature.xml`'s `Vent`
    // carries exactly one comp, `CompProperties_Flickable`, and NO
    // `CompProperties_TempControl` — so `Building_Vent.compTempControl` is null,
    // the gizmos above never exist for it, and `Building_Vent.TickRare` reads no
    // target at all — its whole body is
    // `if (FlickUtility.WantsToBeOn(this)) {
    // GenTemperature.EqualizeTemperaturesThroughBuilding(this, 14f,
    // twoWay: true); Map.gasGrid.EqualizeGasThroughBuilding(this, twoWay: true); }`.
    // A vent has no temperature to set; what it has is an open/closed state,
    // and that state is the FLICK switch. It is refused by name with that
    // reason, and the reason points at `flick`.
    //
    // ===================== MUTATION / COST HAZARDS ==========================
    // This file is reached from `digest` (a section AND a predicate section), so
    // it is held to session 19's axis: no pathfind, no `GetStatValueAbstract`,
    // no `Room.Role`, no `Room.GetStat`. What it does use, and why each is free:
    //
    //  * `map.listerBuildings.allBuildingsColonist` is the real backing List
    //    (Verse/ListerBuildings.allBuildingsColonist), not a cache rebuilt on
    //    read. Snapshotted anyway, because the loop below reaches modded comp
    //    getters.
    //  * `ThingDef.HasComp` walks the def's CompProperties list, so it is
    //    memoised per def in `TempDefs` — defs are fixed after load.
    //  * `Room.Temperature` is `RoomTempTracker.Temperature`, whose getter
    //    returns `temperatureInt` — a field read.
    //  * `Room.Role` and `Room.GetStat` are the ONLY two members that run
    //    `UpdateRoomStatsAndRole()`, and neither is called anywhere in this
    //    file. That is why a room row in the digest section is identified by
    //    id/at/cells rather than by role: `room <id>` is where a reader goes
    //    for the role, and pays for it there.
    //  * **`Room.UsesOutdoorTemperature` is NOT a field read**, and an earlier
    //    draft of this comment said it was. It is
    //    `TouchesMapEdge || OpenRoofCount >= CeilToInt(CellCount * 0.25f)`, and
    //    `OpenRoofCount` -> `District.OpenRoofCountStopAt` walks every cell of
    //    the room on first read per district and caches a live enumerator on
    //    it; `CellCount` lazily fills `cachedCellCount` the same way. Nothing
    //    scribed changes and nothing touches Rand, so the observer invariant
    //    holds — but it is the most expensive member this file touches and
    //    `Effective` asks it several times per controller, so it is read ONCE
    //    into `Controller.ServesOutdoor` and never through the property again.
    //  * `CompPowerTrader.PowerOn` is `powerOnInt`, a field read. Only the
    //    SETTER on that property has side effects. Same for
    //    `CompFlickable.SwitchIsOn` (`switchOnInt`) and `Thing.IsBrokenDown()`
    //    (`CompBreakdownable.brokenDownInt`).
    //  * `Thing.IsForbidden(Faction)` short-circuits on `compForbiddable`; the
    //    `(Thing, Pawn)` overload walks the map's lords and is NOT used here.
    //  * **`IntVec3.GetRoom(map)` is not quite free and the honest statement is
    //    narrower than "no room analysis".** It bottoms out in
    //    `RegionGrid.GetValidRegionAt`, which calls
    //    `map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms()` — so a read
    //    taken in the same frame as a wall being built or destroyed can flush a
    //    pending region rebuild, which recreates `Room` objects (invalidating
    //    the `room_id`s published here) and, via `Room.Notify_RoomShapeChanged`,
    //    reaches `autoBuildRoofAreaSetter.TryGenerateAreaFor` and
    //    `Building_Bed.ForPrisoners`. In practice the updater is flushed every
    //    `MapPreTick`, so a digest taken after a tick finds it clean. This is a
    //    PRE-EXISTING property of the whole observation surface, not something
    //    this file introduces — `DigestVerb.ColonistSection` (`pawn.GetRoom()`),
    //    `SpatialVerbs.RoomAt` and `PlaceVerbs` all take the same route — and it
    //    is filed rather than fixed here, because a fix belongs to all of them
    //    at once.
    //
    // The food half of the room rows comes from `FoodRot.Of`, which memoises its
    // scan per (map, tick): the full digest builds both `resources` and
    // `temperature`, and without that memo the food walk ran TWICE per read.
    public static class TemperatureVerbs
    {
        // The real clamp, verbatim from
        // RimWorld/CompTempControl.InterfaceChangeTargetTemperature.
        public const float ClampMinC = -273.15f;
        public const float ClampMaxC = 1000f;

        // RimWorld/CompTempControl.DefaultTargetTemperature, and the literal the
        // "reset" gizmo assigns (`TargetTemperature = 21f`).
        public const float VanillaDefaultC = 21f;

        // OURS, and the only invented number in this file. The game's own
        // "am I at target" test is `Mathf.Approximately(num4, 0f)` on the
        // computed change, i.e. a tolerance of zero — usable for a per-building
        // fact (`operating_at_high_power` is published raw and IS that test's
        // own answer) but useless as a room-level alarm, because a freezer
        // oscillates around its target by design. 2 C is the band inside which
        // a controlled room counts as holding. Published as `tolerance_c` so a
        // reader never has to guess what `out_of_range` meant.
        public const float ToleranceC = 2f;

        // Rows in the digest section. Out-of-range rooms are NEVER dropped —
        // the cap only ever eats rooms that are fine, which is DigestVerb's
        // standing truncation contract.
        private const int RoomCap = 10;
        private const int ControllerCap = 4;
        // Rows in the `temp-control` verb, which is a per-call read and can
        // afford more than the glance.
        private const int ListCap = 60;

        // ------------------------------------------------------------------
        // Which defs carry the comp at all. `ThingDef.HasComp` is a walk of the
        // def's CompProperties list; defs are fixed after load, so this is
        // memoised for the process. Cleared by nothing on purpose: a def set
        // that changed under a running game would already have invalidated far
        // more than this.
        private static readonly Dictionary<ThingDef, bool> TempDefs =
            new Dictionary<ThingDef, bool>();

        internal static bool DefHasTempControl(ThingDef def)
        {
            if (def == null) return false;
            if (TempDefs.TryGetValue(def, out var v)) return v;
            bool has = false;
            try { has = def.HasComp(typeof(CompTempControl)); }
            catch { }
            // A modded comp SUBCLASSING CompTempControl would fail the exact
            // compClass test above, so the generic overload (which tests
            // IsAssignableFrom) is the fallback rather than the primary — it is
            // the slower of the two and the exact match covers vanilla.
            if (!has)
            {
                try { has = def.HasComp<CompTempControl>(); }
                catch { }
            }
            TempDefs[def] = has;
            return has;
        }

        // ------------------------------------------------------------------
        // ONE CONTROLLER, READ. Everything below is a field read or a grid
        // lookup; see the class comment for the per-member cost argument.
        internal sealed class Controller
        {
            public Thing Thing;
            public CompTempControl Comp;
            public float TargetC;
            public float EnergyPerSecond;
            public string Kind;              // cooler | heater | other
            public bool Powered;
            public bool HasPowerComp;
            public bool SwitchOn;
            public bool HasFlickComp;
            public bool BrokenDown;
            public bool HighPower;
            public Room Serves;              // the room the game's own TickRare pushes into
            public IntVec3 ServesAt = IntVec3.Invalid;
            public string ServesBasis;
            public Room Exhaust;             // coolers only: the hot side
            public IntVec3 ExhaustAt = IntVec3.Invalid;
            public bool ColdSideBlocked;
            public bool HotSideBlocked;
            public float DefMinC, DefMaxC, DefDefaultC;
            // Read ONCE, in FillServed, and never through the property again.
            // `Room.UsesOutdoorTemperature` is NOT a field read: it is
            // `TouchesMapEdge || OpenRoofCount >= CeilToInt(CellCount * 0.25f)`,
            // and `OpenRoofCount` walks every cell of the room on first read
            // per district (`District.OpenRoofCountStopAt`, which also caches a
            // live enumerator). Nothing scribed changes, so the observer
            // invariant holds — but it is the most expensive thing this file
            // touches, `Effective` is asked several times per controller, and
            // this section runs on a per-frame predicate cadence. An earlier
            // draft called it a field read in a comment; measured instead.
            public bool ServesOutdoor;

            // The verdict, and it is only ever asked of a controller that is
            // switched ON and serves a room the game can actually change.
            public bool Effective =>
                Serves != null && !ServesOutdoor
                && !(Kind == "cooler" && (ColdSideBlocked || HotSideBlocked));

            public bool Counts => SwitchOn && Effective && Kind != "other";

            public float DriftC => Serves == null ? 0f : Serves.Temperature - TargetC;

            public bool OutOfRange
            {
                get
                {
                    if (!Counts) return false;
                    if (Kind == "cooler") return DriftC > ToleranceC;
                    if (Kind == "heater") return DriftC < -ToleranceC;
                    return false;
                }
            }

            public bool OutsideDefRange => TargetC < DefMinC || TargetC > DefMaxC;
        }

        // `ThingWithComps.GetComp<T>()` is NOT unconditionally safe and this is
        // the guard for it: its fast path is `if (comps == null) return null;
        // int count = comps.Count; if (count < 3) { if (comps[0] is T ...`, so a
        // comps list that is non-null and EMPTY throws IndexOutOfRange rather
        // than returning null. `InitializeComps` normally leaves the list null
        // for a def with no comps, but its own catch does
        // `comps.Remove(thingComp)` when a comp class fails to construct — so a
        // modded def with exactly one broken comp leaves an empty non-null list
        // and every GetComp on that thing throws. With 32 mods on the bench that
        // is a verb crash rather than a caught per-thing error, which is the one
        // outcome this file must not produce.
        internal static T Comp<T>(Thing t) where T : ThingComp
        {
            try { return (t as ThingWithComps)?.GetComp<T>(); }
            catch { return null; }
        }

        internal static Controller Read(Thing t)
        {
            if (t == null) return null;
            var comp = Comp<CompTempControl>(t);
            if (comp == null) return null;
            var c = new Controller { Thing = t, Comp = comp };
            try { c.TargetC = comp.TargetTemperature; } catch { }
            // `CompTempControl.Props` is a HARD CAST, `(CompProperties_TempControl)props`
            // — a modded comp registered against a plain `CompProperties` whose
            // compClass is CompTempControl throws InvalidCastException here, and
            // outside `digest` (where Section's catch would swallow it) that
            // escapes the verb.
            CompProperties_TempControl props = null;
            try { props = comp.Props; } catch { }
            if (props != null)
            {
                c.EnergyPerSecond = props.energyPerSecond;
                c.DefMinC = props.minTargetTemperature;
                c.DefMaxC = props.maxTargetTemperature;
                c.DefDefaultC = props.defaultTargetTemperature;
            }
            // The SIGN of energyPerSecond is the game's own cooler/heater
            // distinction (`Building_Cooler` ships -21, `Building_Heater` +21),
            // and asking the def rather than the class picks up a modded
            // controller for free.
            c.Kind = c.EnergyPerSecond < 0f ? "cooler" : c.EnergyPerSecond > 0f ? "heater" : "other";
            try { c.HighPower = comp.operatingAtHighPower; } catch { }

            var power = Comp<CompPowerTrader>(t);
            c.HasPowerComp = power != null;
            // powerOnInt, a field read. The SETTER is the one with side effects.
            try { c.Powered = power == null || power.PowerOn; } catch { c.Powered = false; }

            var flick = Comp<CompFlickable>(t);
            c.HasFlickComp = flick != null;
            try { c.SwitchOn = flick == null || flick.SwitchIsOn; } catch { c.SwitchOn = true; }

            try { c.BrokenDown = t.IsBrokenDown(); } catch { }

            FillServed(c);
            return c;
        }

        // WHICH ROOM A CONTROLLER SERVES, taken from the game's own TickRare
        // rather than from "the room it stands in", because for a cooler those
        // are different and the difference is the whole building.
        //
        //  * `RimWorld/Building_Cooler.TickRare` computes
        //    `intVec  = Position + IntVec3.South.RotatedBy(Rotation)` and
        //    `intVec2 = Position + IntVec3.North.RotatedBy(Rotation)`, then
        //    pushes the temperature change into `intVec.GetRoom(Map)` and the
        //    waste heat into `intVec2`. SOUTH is the cold side and is the room
        //    served; NORTH is the exhaust. A cooler sits IN a wall, so its own
        //    cell's room is neither.
        //  * That whole block is guarded by
        //    `if (!intVec2.Impassable(Map) && !intVec.Impassable(Map))` — a
        //    cooler with a wall on EITHER side moves no heat at all and draws
        //    idle power forever. Published as `cold_side_blocked` /
        //    `hot_side_blocked`, because it is invisible in every other reading.
        //  * `RimWorld/Building_Heater.TickRare` uses `this.GetRoom()` and
        //    `base.Position` — its own room.
        //  * Anything else carrying the comp gets its own room, flagged
        //    `serves_basis: "own-cell"` so the reading is not passed off as the
        //    game's own arithmetic when it is our fallback.
        private static void FillServed(Controller c)
        {
            var t = c.Thing;
            Map map = null;
            try { map = t.Map; } catch { }
            if (map == null || !t.Spawned) { c.ServesBasis = "not-spawned"; return; }
            try
            {
                if (t is Building_Cooler)
                {
                    var cold = t.Position + IntVec3.South.RotatedBy(t.Rotation);
                    var hot = t.Position + IntVec3.North.RotatedBy(t.Rotation);
                    c.ServesBasis = "RimWorld/Building_Cooler.TickRare (south-rotated cell)";
                    if (cold.InBounds(map))
                    {
                        c.ServesAt = cold;
                        c.Serves = cold.GetRoom(map);
                        c.ColdSideBlocked = cold.Impassable(map);
                    }
                    if (hot.InBounds(map))
                    {
                        c.ExhaustAt = hot;
                        c.Exhaust = hot.GetRoom(map);
                        c.HotSideBlocked = hot.Impassable(map);
                    }
                    Outdoor(c);
                    return;
                }
                if (t is Building_Heater)
                {
                    c.ServesBasis = "RimWorld/Building_Heater.TickRare (own cell)";
                    c.ServesAt = t.Position;
                    c.Serves = t.GetRoom();
                    Outdoor(c);
                    return;
                }
                c.ServesBasis = "own-cell (no vanilla TickRare to cite for this thing class)";
                c.ServesAt = t.Position;
                c.Serves = t.GetRoom();
                Outdoor(c);
            }
            catch { c.ServesBasis = "unreadable"; }
        }

        private static void Outdoor(Controller c)
        {
            if (c.Serves == null) return;
            try { c.ServesOutdoor = c.Serves.UsesOutdoorTemperature; } catch { }
        }

        internal static Dictionary<string, object> Row(Controller c)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = c.Thing.thingIDNumber,
                ["def"] = c.Thing.def?.defName,
                ["label"] = WorldSafe.Safe(() => c.Thing.LabelShort),
                ["at"] = Positions.Out(c.Thing.Position),
                ["rot"] = WorldSafe.SafeObj(() => (object)c.Thing.Rotation.ToStringHuman()),
                ["faction"] = WorldSafe.Safe(() => c.Thing.Faction?.Name),
                ["kind"] = c.Kind,
                ["target_c"] = WorldSafe.R(c.TargetC, 1),
                ["energy_per_second"] = WorldSafe.R(c.EnergyPerSecond, 1),
                // The def's advertised range, published as a MEASUREMENT and
                // never as a gate — see the class comment. Nothing in 1.6 reads
                // these two fields.
                ["def_default_c"] = WorldSafe.R(c.DefDefaultC, 1),
                ["def_min_c"] = WorldSafe.R(c.DefMinC, 1),
                ["def_max_c"] = WorldSafe.R(c.DefMaxC, 1),
                ["def_clamp_enforced"] = false,
                ["outside_def_range"] = c.OutsideDefRange,
                ["powered"] = c.Powered,
                ["has_power_comp"] = c.HasPowerComp,
                ["switch_on"] = c.SwitchOn,
                ["broken_down"] = c.BrokenDown,
                // CompTempControl.operatingAtHighPower is a PUBLIC FIELD and is
                // the game's own answer to "is this thing actually working right
                // now", set at the end of every TickRare. Not our arithmetic.
                ["operating_at_high_power"] = c.HighPower,
                ["effective"] = c.Effective,
                ["serves_basis"] = c.ServesBasis,
            };
            if (c.Serves != null)
            {
                d["serves"] = new Dictionary<string, object>
                {
                    ["room_id"] = c.Serves.ID,
                    ["at"] = c.ServesAt.IsValid ? Positions.Out(c.ServesAt) : null,
                    ["temp_c"] = WorldSafe.SafeObj(() => (object)WorldSafe.R(c.Serves.Temperature, 1)),
                    ["uses_outdoor_temp"] = c.ServesOutdoor,
                    ["cells"] = WorldSafe.SafeObj(() => (object)c.Serves.CellCount),
                };
                d["drift_c"] = WorldSafe.R(c.DriftC, 1);
                d["out_of_range"] = c.OutOfRange;
            }
            else d["serves"] = null;
            if (c.Kind == "cooler")
            {
                d["cold_side_blocked"] = c.ColdSideBlocked;
                d["hot_side_blocked"] = c.HotSideBlocked;
                if (c.Exhaust != null)
                    d["exhaust"] = new Dictionary<string, object>
                    {
                        ["room_id"] = c.Exhaust.ID,
                        ["at"] = c.ExhaustAt.IsValid ? Positions.Out(c.ExhaustAt) : null,
                        ["temp_c"] = WorldSafe.SafeObj(() => (object)WorldSafe.R(c.Exhaust.Temperature, 1)),
                    };
            }
            var why = Advisory(c);
            if (why != null) d["advisory"] = why;
            return d;
        }

        // CANDIDATES + REASONS, NEVER BARE BOOLEANS. Every one of these is a
        // state in which the target is stored and silently not honoured, which
        // is the exact shape of the session-18 finding.
        internal static string Advisory(Controller c)
        {
            if (c.Serves == null)
                return "this controller serves no room — nothing it is told will have an effect";
            if (c.ServesOutdoor)
                return "the room it serves USES THE OUTDOOR TEMPERATURE (open to the sky or "
                     + "touching the map edge), and GenTemperature.ControlTemperatureTempChange "
                     + "returns 0 for such a room — the target is stored and does nothing. Roof "
                     + "and seal the room first.";
            if (c.Kind == "cooler" && (c.ColdSideBlocked || c.HotSideBlocked))
                return "Building_Cooler.TickRare moves no heat unless BOTH the cold (south) and "
                     + "hot (north) cells are passable, and "
                     + (c.ColdSideBlocked && c.HotSideBlocked ? "both are" : c.ColdSideBlocked ? "the cold side is" : "the hot side is")
                     + " blocked — the cooler draws idle power and does nothing";
            if (!c.SwitchOn)
                return "the flick switch is OFF, so the target is stored and not acted on — "
                     + "`flick {things:[" + c.Thing.thingIDNumber + "], on:true}` turns it back on";
            if (c.BrokenDown)
                return "BROKEN DOWN (CompBreakdownable) — it needs a component and a repair job "
                     + "before any target is honoured";
            if (!c.Powered)
                return "UNPOWERED — Building_Cooler.TickRare and Building_Heater.TickRare both "
                     + "return immediately when compPowerTrader.PowerOn is false, so the target "
                     + "is stored and nothing acts on it. Check digest.power.";
            if (c.OutsideDefRange)
                return "the target is outside the def's advertised "
                     + WorldSafe.R(c.DefMinC, 1) + ".." + WorldSafe.R(c.DefMaxC, 1)
                     + " C range. That is ADVISORY, not a refusal: nothing in the 1.6 tree reads "
                     + "CompProperties_TempControl.minTargetTemperature or maxTargetTemperature, "
                     + "and the game's own gizmo clamps only to -273.15..1000.";
            if (c.OutOfRange)
                return "the room is " + WorldSafe.R(Math.Abs(c.DriftC), 1) + " C on the wrong side "
                     + "of the target and this controller is "
                     + (c.HighPower ? "working at full power" : "IDLING")
                     + " — " + (c.HighPower
                        ? "it is trying; give it time, more units, or a smaller room"
                        : "it has stopped trying, which usually means it cannot reach the target");
            return null;
        }

        // ------------------------------------------------------------------
        // TARGET SELECTION
        // ------------------------------------------------------------------
        // `rect|cells|things|filter` come from DesignateEngine.Resolve — the
        // house's one target grammar, so a caller that can address `flick` can
        // address this. Two additions:
        //
        //   room: <id>   the controllers SERVING that room, which is the
        //                question a freezer actually poses ("make THAT room
        //                cold"), and which no cell rect answers cleanly because
        //                a cooler sits in the wall and its own cell belongs to
        //                neither side.
        //   (nothing)    every player-faction controller on the map. READ ONLY:
        //                `temp-set` requires an explicit target set, because
        //                "set every cooler and heater on the map to -10" is a
        //                mistake worth refusing rather than performing.
        private sealed class Picked
        {
            public List<Controller> Controllers = new List<Controller>();
            public List<DesignateEngine.Reject> Rejects = new List<DesignateEngine.Reject>();
            public Dictionary<string, object> Scope;
            public int Requested;
            public bool Capped;
        }

        private static Picked Pick(Map map, VerbArgs a, bool allowWholeMap)
        {
            var p = new Picked();
            bool hasTargets = a.Has("rect") || a.Has("cells") || a.Has("things")
                || a.Has("filter") || a.Has("area_things");
            bool hasRoom = a.Has("room");
            if (hasTargets && hasRoom)
                throw new VerbArgsException(
                    "`room` and the cell/thing target forms are mutually exclusive — `room:<id>` "
                    + "selects the controllers SERVING that room, which is a different question "
                    + "from the controllers standing in a rect");

            if (hasRoom)
            {
                int id = a.IntReq("room");
                var room = WorldSafe.FindRoom(map, id);
                int exhaustOnly = 0;
                p.Scope = new Dictionary<string, object> { ["kind"] = "room", ["room_id"] = id };
                foreach (var c in AllOnMap(map))
                {
                    if (c.Serves != null && c.Serves.ID == room.ID) { p.Controllers.Add(c); continue; }
                    if (c.Exhaust == null || c.Exhaust.ID != room.ID) continue;
                    // EXHAUST-ONLY, AND IT MUST NOT BE SET. A cooler in the wall
                    // between a freezer and the kitchen SERVES the freezer and
                    // EXHAUSTS into the kitchen, so `temp-set {room:<kitchen>,
                    // target_c:20}` would otherwise silently rewrite the
                    // freezer's target and thaw it — a mutation nobody asked for,
                    // on a building the caller was not thinking about. The READ
                    // wants it (a room's temperature really is affected by what
                    // dumps heat into it), so it is listed there and REFUSED by
                    // name here.
                    exhaustOnly++;
                    if (allowWholeMap) p.Controllers.Add(c);
                    else p.Rejects.Add(new DesignateEngine.Reject
                    {
                        At = c.Thing.Position,
                        Thing = c.Thing,
                        Why = "exhaust-only",
                        Reason = "this cooler EXHAUSTS into room " + room.ID + " but SERVES room "
                            + (c.Serves != null ? c.Serves.ID.ToString() : "none")
                            + " (Building_Cooler.TickRare pushes the temperature change into the "
                            + "SOUTH-rotated cell and the waste heat into the north one). Setting "
                            + "it from the room it heats would retarget the room it cools — "
                            + "address it by `things:[" + c.Thing.thingIDNumber + "]` if that is "
                            + "what you meant.",
                    });
                }
                if (exhaustOnly > 0)
                    p.Scope["exhaust_only"] = exhaustOnly;
                p.Requested = p.Controllers.Count;
                return p;
            }

            if (!hasTargets)
            {
                if (!allowWholeMap)
                    throw new VerbArgsException(
                        "needs a target set: room:<id> | things:[id,…] | rect:[x,z,w,h] | "
                        + "cells:[P,…] | filter:{…}. `temp-set` has no whole-map default on "
                        + "purpose — setting every cooler and heater on the map at once is a "
                        + "mistake worth refusing. `temp-control` with no target reads them all.");
                p.Scope = new Dictionary<string, object>
                {
                    ["kind"] = "map",
                    ["detail"] = "every player-faction building carrying CompTempControl "
                        + "(Verse/ListerBuildings.allBuildingsColonist). A controller belonging "
                        + "to another faction is reachable by `things:[id]` and is not listed "
                        + "here — that is a listing scope, not a gate: "
                        + "CompTempControl.CompGetGizmosExtra has no faction clause.",
                };
                p.Controllers.AddRange(AllOnMap(map));
                p.Requested = p.Controllers.Count;
                return p;
            }

            var targets = DesignateEngine.Resolve(map, a, DesignateEngine.MaxCellsArg(a));
            p.Scope = targets.Detail;
            p.Requested = targets.Requested;
            p.Capped = targets.Capped;
            var seen = new HashSet<int>();
            if (targets.IsThings)
            {
                for (int i = 0; i < targets.Things.Count; i++) Consider(map, targets.Things[i], p, seen, true);
            }
            else
            {
                for (int i = 0; i < targets.Cells.Count; i++)
                {
                    var cell = targets.Cells[i];
                    if (!cell.InBounds(map))
                    {
                        p.Rejects.Add(new DesignateEngine.Reject { At = cell, Why = "out-of-bounds" });
                        continue;
                    }
                    if (cell.Fogged(map))
                    {
                        p.Rejects.Add(new DesignateEngine.Reject
                        { At = cell, Why = DesignateEngine.WhyFogged, Reason = Blockers.FoggedReason });
                        continue;
                    }
                    bool found = false;
                    var list = map.thingGrid.ThingsListAtFast(cell);
                    for (int j = 0; j < list.Count; j++)
                        if (list[j] != null && DefHasTempControl(list[j].def))
                        {
                            Consider(map, list[j], p, seen, false);
                            found = true;
                        }
                    // A cell form is a SWEEP: "no controller here" is the normal
                    // case for most cells in a rect and is not worth a rejection
                    // line each. Only an explicitly named cell list says so.
                    if (!found && targets.Kind == "cells")
                        p.Rejects.Add(new DesignateEngine.Reject { At = cell, Why = "no-temp-control-here" });
                }
            }
            return p;
        }

        private static void Consider(Map map, Thing t, Picked p, HashSet<int> seen, bool explicitly)
        {
            if (t == null) return;
            if (!seen.Add(t.thingIDNumber)) return;
            if (WorldSafe.Hidden(t, map))
            {
                p.Rejects.Add(new DesignateEngine.Reject
                {
                    At = t.PositionHeld,
                    Thing = t,
                    Why = t.Spawned && t.Map == map ? DesignateEngine.WhyFogged : "not-on-map",
                    Reason = t.Spawned && t.Map == map ? Blockers.FoggedReason : null,
                });
                return;
            }
            var c = Read(t);
            if (c != null) { p.Controllers.Add(c); return; }
            if (!explicitly) return;
            // THE NAMED REFUSAL the issue asks for, with the game's own reason.
            // The vent is the case worth spelling out, because it is in the
            // Temperature build category, it is called a temperature building,
            // and it has no target at all.
            bool vent = t is Building_Vent;
            p.Rejects.Add(new DesignateEngine.Reject
            {
                At = t.Position,
                Thing = t,
                Why = "no-temp-control",
                Reason = vent
                    ? "a Vent carries no CompTempControl at all (Core's Buildings_Temperature.xml "
                      + "gives it only CompProperties_Flickable), so it has no temperature target: "
                      + "Building_Vent.TickRare just calls "
                      + "GenTemperature.EqualizeTemperaturesThroughBuilding. Open and close it "
                      + "with `flick`."
                    : "'" + (t.def?.defName ?? "?") + "' has no CompTempControl, so "
                      + "CompTempControl.CompGetGizmosExtra yields no temperature buttons for it "
                      + "and there is nothing to set",
            });
        }

        // Every player-faction controller on the map. `allBuildingsColonist` is
        // the real backing List (Verse/ListerBuildings.allBuildingsColonist);
        // snapshotted
        // because the read loop reaches modded comp getters.
        internal static List<Controller> AllOnMap(Map map)
        {
            var outp = new List<Controller>();
            if (map == null) return outp;
            List<Building> all;
            try { all = new List<Building>(map.listerBuildings.allBuildingsColonist); }
            catch { return outp; }
            for (int i = 0; i < all.Count; i++)
            {
                var b = all[i];
                if (b == null || b.def == null) continue;
                if (!DefHasTempControl(b.def)) continue;
                if (WorldSafe.Hidden(b, map)) continue;
                var c = Read(b);
                if (c != null) outp.Add(c);
            }
            outp.Sort((x, y) => x.Thing.thingIDNumber.CompareTo(y.Thing.thingIDNumber));
            return outp;
        }

        // ------------------------------------------------------------------
        // temp-control — THE READ. Mutates nothing.
        // ------------------------------------------------------------------
        [Verb("temp-control")]
        public static object TempControl(VerbContext ctx)
        {
            var map = WorldSafe.CurrentMap();
            var a = ctx.Args;
            a.NearMiss("room", "room_id", "roomId");
            int cap = a.Int("cap", ListCap);
            if (cap < 1 || cap > 500) throw new VerbArgsException("cap must be 1..500");

            var picked = Pick(map, a, allowWholeMap: true);
            // Out-of-range first, then the ones that cannot work, then by id —
            // deterministic run to run, and the cap eats the boring ones.
            picked.Controllers.Sort((x, y) =>
            {
                int c = Rank(y).CompareTo(Rank(x));
                return c != 0 ? c : x.Thing.thingIDNumber.CompareTo(y.Thing.thingIDNumber);
            });

            var list = new List<object>();
            for (int i = 0; i < picked.Controllers.Count && i < cap; i++)
                list.Add(Row(picked.Controllers[i]));

            var data = new Dictionary<string, object>
            {
                ["verb"] = "temp-control",
                ["gate"] = "RimWorld/CompTempControl.CompGetGizmosExtra "
                    + "(+ InterfaceChangeTargetTemperature's Mathf.Clamp(-273.15, 1000))",
                ["target_scope"] = picked.Scope,
                ["list"] = list,
                ["total"] = picked.Controllers.Count,
                ["more"] = Math.Max(0, picked.Controllers.Count - list.Count),
                ["order"] = "out-of-range-first, then ineffective, then thing-id",
                ["tolerance_c"] = WorldSafe.R(ToleranceC, 1),
                ["clamp_c"] = new List<object> { (double)ClampMinC, (double)ClampMaxC },
                ["outdoor_c"] = WorldSafe.SafeObj(() => (object)WorldSafe.R(map.mapTemperature.OutdoorTemp, 1)),
                ["note"] = "READ ONLY. `def_min_c`/`def_max_c` are the def's advertised range and "
                    + "are NOT enforced by the game — nothing in the 1.6 tree reads "
                    + "CompProperties_TempControl.minTargetTemperature or maxTargetTemperature, "
                    + "and the gizmo clamps only to -273.15..1000, which is what `temp-set` "
                    + "refuses outside. `serves` is the room the game's own TickRare pushes into: "
                    + "for a cooler that is the SOUTH-rotated cell, not the cooler's own cell.",
            };
            if (picked.Capped) data["capped"] = true;
            if (picked.Requested > 0) data["requested"] = picked.Requested;
            DesignateEngine.PublishRejects(map, picked.Rejects, data);
            return data;
        }

        private static int Rank(Controller c)
        {
            int score = 0;
            if (c.OutOfRange) score += 1000;
            if (!c.Effective) score += 500;
            if (!c.Powered) score += 400;
            if (c.BrokenDown) score += 300;
            if (!c.SwitchOn) score += 100;
            if (c.OutsideDefRange) score += 50;
            return score;
        }

        // ------------------------------------------------------------------
        // temp-set — THE ACTUATOR.
        // ------------------------------------------------------------------
        // temp-set {room|things|rect|cells|filter, target_c: <C>, dry_run?}
        //
        // CELSIUS ONLY, and the field is `target_c` to sit beside `temp_c`,
        // `outdoor_c` and `comfort_min_c`. `CompTempControl.targetTemperature`
        // IS Celsius; `Prefs.TemperatureMode` and
        // `RoundedToCurrentTempModeOffset` convert only for DISPLAY, and a
        // protocol whose units depend on a user preference is a protocol that
        // breaks when somebody clicks the F button.
        //
        // The write is `comp.TargetTemperature = value` — the gizmo's own
        // property, whose vanilla setter is a plain field assignment, so no
        // reflection is needed here (contrast `flick`, which had to reach a
        // private `wantSwitchOn`). What is NOT reproduced from
        // `InterfaceChangeTargetTemperature` is `SoundDefOf.DragSlider`, the
        // `MoteMaker.ThrowText` mote and the ± offset arithmetic: this verb sets
        // an absolute target, and a sound played from the file bridge is a
        // side effect nobody asked for.
        [Verb("temp-set")]
        public static object TempSet(VerbContext ctx)
        {
            var map = WorldSafe.CurrentMap();
            var a = ctx.Args;
            a.NearMiss("target_c", "target", "targetC", "temp_c", "celsius", "temperature");
            a.NearMiss("room", "room_id", "roomId");
            bool dryRun = a.Bool("dry_run", false);

            double raw = a.NumReq("target_c");
            if (double.IsNaN(raw) || double.IsInfinity(raw))
                throw new VerbArgsException("target_c must be a finite number of degrees Celsius");
            // THE ONLY REAL GATE. Cited, and it is the game's own clamp rather
            // than the def range the issue asked for — see the class comment for
            // why the def range is an advisory instead.
            if (raw < ClampMinC || raw > ClampMaxC)
                throw new VerbArgsException(
                    $"target_c {raw} is outside the game's own clamp of {ClampMinC}..{ClampMaxC} C "
                    + "(RimWorld/CompTempControl.InterfaceChangeTargetTemperature: "
                    + "Mathf.Clamp(TargetTemperature, -273.15f, 1000f)). That clamp is the WHOLE "
                    + "gate: CompProperties_TempControl.minTargetTemperature/maxTargetTemperature "
                    + "exist but nothing in the 1.6 tree reads them, so a target outside the def "
                    + "range is reported as an advisory and set, exactly as the -10 button would.");
            float target = (float)raw;

            var picked = Pick(map, a, allowWholeMap: false);
            var results = new List<object>();
            int changed = 0, unchanged = 0;
            var advisories = new List<object>();
            var echoCells = new List<IntVec3>();

            for (int i = 0; i < picked.Controllers.Count; i++)
            {
                var c = picked.Controllers[i];
                float before = c.TargetC;
                bool same = Mathf.Approximately(before, target);
                if (!dryRun && !same)
                {
                    try { c.Comp.TargetTemperature = target; }
                    catch (Exception e)
                    {
                        picked.Rejects.Add(new DesignateEngine.Reject
                        {
                            At = c.Thing.Position,
                            Thing = c.Thing,
                            Why = "setter-threw",
                            Reason = e.GetType().Name + ": " + Journal.Truncate(e.Message, 120),
                        });
                        continue;
                    }
                }
                if (same) unchanged++; else changed++;
                if (c.Thing.Position.IsValid) echoCells.Add(c.Thing.Position);

                // READ BACK, never project: the row reports what the comp says
                // now, which is the same discipline `flick` uses for wants_on.
                float after = before;
                if (!dryRun) { try { after = c.Comp.TargetTemperature; } catch { } }
                else after = target;
                c.TargetC = after;

                var row = Row(c);
                row["target_before_c"] = WorldSafe.R(before, 1);
                row["target_after_c"] = WorldSafe.R(after, 1);
                row["changed"] = !same;
                row["was_already"] = same;
                results.Add(row);
                if (row.TryGetValue("advisory", out var why) && why != null)
                    advisories.Add(new Dictionary<string, object>
                    {
                        ["id"] = c.Thing.thingIDNumber,
                        ["def"] = c.Thing.def?.defName,
                        ["advisory"] = why,
                    });
            }

            var data = new Dictionary<string, object>
            {
                ["verb"] = "temp-set",
                // `data.ok` is the VERB's verdict; the envelope stays ok:true.
                // False when nothing was actually set — an empty target set or a
                // call every one of whose targets was refused is not a success
                // just because the protocol round trip worked.
                ["ok"] = results.Count > 0 && picked.Rejects.Count == 0,
                ["gate"] = "RimWorld/CompTempControl.CompGetGizmosExtra "
                    + "(+ InterfaceChangeTargetTemperature's Mathf.Clamp(-273.15, 1000))",
                ["target_c"] = WorldSafe.R(target, 1),
                ["units"] = "celsius",
                ["target_scope"] = picked.Scope,
                ["targeted"] = picked.Controllers.Count,
                ["accepted"] = results.Count,
                ["changed"] = changed,
                ["already_at_target"] = unchanged,
                ["dry_run"] = dryRun,
                ["things"] = results,
                ["tolerance_c"] = WorldSafe.R(ToleranceC, 1),
                ["note"] = "The target is STORED, not applied: Building_Cooler.TickRare and "
                    + "Building_Heater.TickRare act on it at their own rare cadence and both "
                    + "return immediately when the thing is unpowered. Read `advisories`, then "
                    + "advance before asserting on `room <id>`'s temp_c or on digest.temperature.",
            };
            if (advisories.Count > 0) data["advisories"] = advisories;
            if (results.Count == 0 && picked.Rejects.Count == 0)
                data["note_empty"] = "the target set resolved to no temperature-controlled "
                    + "building at all — nothing was set";
            DesignateEngine.PublishRejects(map, picked.Rejects, data);
            data["crop"] = DesignateEngine.Echo(map, echoCells);
            data["action"] = dryRun
                ? NoAction()
                // INVARIANT, not the ambient culture: the journal is machine
                // read, and a bench whose locale uses a decimal comma would
                // otherwise write `-10,0` into a field a parser splits on.
                : Act("temp-set",
                      WorldSafe.R(target, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "C",
                      results.Count + " controller(s)", new Dictionary<string, object>
                      {
                          ["target_c"] = WorldSafe.R(target, 1),
                          ["counts"] = new Dictionary<string, object>
                          {
                              ["targeted"] = picked.Controllers.Count,
                              ["accepted"] = results.Count,
                              ["changed"] = changed,
                              ["rejected"] = picked.Rejects.Count,
                          },
                          ["targets"] = Journaled(results),
                      });
            return data;
        }

        // The before/after target per building, which is what 261f2e9's last
        // acceptance bullet asks the journal to carry. Capped: a journal line is
        // provenance, not a dump.
        private static List<object> Journaled(List<object> rows)
        {
            var l = new List<object>();
            for (int i = 0; i < rows.Count && i < 12; i++)
            {
                if (!(rows[i] is Dictionary<string, object> r)) continue;
                l.Add(new Dictionary<string, object>
                {
                    ["id"] = r.TryGetValue("id", out var id) ? id : null,
                    ["def"] = r.TryGetValue("def", out var def) ? def : null,
                    ["before_c"] = r.TryGetValue("target_before_c", out var b) ? b : null,
                    ["after_c"] = r.TryGetValue("target_after_c", out var af) ? af : null,
                });
            }
            return l;
        }

        // ------------------------------------------------------------------
        // digest.temperature — THE HALF THAT MATTERS
        // ------------------------------------------------------------------
        // 261f2e9 asks for an actuator. The orchestrator's investigation found
        // the observation gap underneath it, and this section is the answer to
        // "is any room I care about out of range" asked at EVERY read rather
        // than on request.
        //
        // A room is WATCHED when either
        //   (a) a temperature controller serves it — the agent has stated a
        //       target for it, so there is something to be out of, or
        //   (b) it holds human-edible food — the room whose temperature the
        //       colony is betting food on, whether or not anything controls it.
        //
        // `ok` is deliberately NARROW: false when a room a SWITCHED-ON
        // controller serves is on the wrong side of that controller's target by
        // more than `tolerance_c`. Not "food is warm somewhere" — on a colony
        // with no freezer that is permanently true and an alarm that is always
        // on is not an alarm. The food half is published as counts and per-room
        // rows, and the food ALARM lives in `resources.food_rot.ok`, where it is
        // time-bounded and can actually change.
        //
        // Switched-off controllers are excluded from `ok` because
        // `CompFlickable.SwitchIsOn` is the player's own recorded intent — a
        // heater turned off in summer is not a fault. Unpowered ones are NOT
        // excluded: a freezer whose power net died is the emergency this section
        // exists for, and it reads `powered:false` on the row.
        //
        // CHEAP ON SESSION 19's AXIS, which is why it is a registered predicate
        // section: one walk of `allBuildingsColonist` with a memoised per-def
        // comp test, one `FoodRot.Of` scan (see that file), region-grid room
        // lookups and plain field reads. No pathfind, no Room.Role, no
        // Room.GetStat, no GetStatValueAbstract except the per-def memoised
        // Nutrition inside the food scan.
        private sealed class RoomRow
        {
            public Room Room;
            public readonly List<Controller> Controllers = new List<Controller>();
            public FoodRot.RoomFood Food;
            public bool OutOfRange;
            public float WorstDrift;
        }

        internal static Dictionary<string, object> Section(Map map)
        {
            try { return Build(map); }
            catch (Exception e)
            {
                return new Dictionary<string, object>
                {
                    ["error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 160),
                };
            }
        }

        private static Dictionary<string, object> Build(Map map)
        {
            var controllers = AllOnMap(map);
            var scan = FoodRot.Of(map);
            var rooms = new Dictionary<int, RoomRow>();

            int unpowered = 0, ineffective = 0, switchedOff = 0;
            for (int i = 0; i < controllers.Count; i++)
            {
                var c = controllers[i];
                if (!c.Powered) unpowered++;
                if (!c.Effective) ineffective++;
                if (!c.SwitchOn) switchedOff++;
                if (c.Serves == null || WorldSafe.RoomHidden(c.Serves)) continue;
                Row(rooms, c.Serves).Controllers.Add(c);
            }
            foreach (var kv in scan.ByRoom)
            {
                var room = kv.Value.Room;
                // FOG, same rule as every other observer (DESIGN, 2026-08-30):
                // `WorldSafe.RoomHidden` is the game's own `Room.Fogged` test.
                // A controller and its food are already unfogged individually,
                // but the ROOM they sit in can still be undiscovered ground, and
                // publishing its id and cell count would leak the shape of a
                // room the colony has not entered.
                if (room == null || WorldSafe.RoomHidden(room)) continue;
                Row(rooms, room).Food = kv.Value;
            }

            var list = new List<RoomRow>(rooms.Values);
            int outOfRange = 0, foodRoomsUncontrolled = 0, foodRoomsUnfrozen = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                for (int j = 0; j < r.Controllers.Count; j++)
                {
                    var c = r.Controllers[j];
                    if (c.OutOfRange) { r.OutOfRange = true; }
                    if (c.Counts && Math.Abs(c.DriftC) > Math.Abs(r.WorstDrift)) r.WorstDrift = c.DriftC;
                }
                if (r.OutOfRange) outOfRange++;
                if (r.Food != null)
                {
                    if (r.Controllers.Count == 0) foodRoomsUncontrolled++;
                    if (r.Food.NutritionUnrefrigerated > 0.01f) foodRoomsUnfrozen++;
                }
            }

            // Out-of-range first and NEVER dropped; then rooms holding food that
            // is not frozen; then by |drift|; then by room id so the truncation
            // is deterministic run to run.
            list.Sort((x, y) =>
            {
                int c = RoomRank(y).CompareTo(RoomRank(x));
                if (c != 0) return c;
                c = Math.Abs(y.WorstDrift).CompareTo(Math.Abs(x.WorstDrift));
                if (c != 0) return c;
                return (x.Room?.ID ?? 0).CompareTo(y.Room?.ID ?? 0);
            });

            var rows = new List<object>();
            int dropped = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (rows.Count >= RoomCap && !r.OutOfRange) { dropped++; continue; }
                rows.Add(RoomOut(r));
            }

            var names = new List<object>();
            for (int i = 0; i < list.Count && names.Count < 8; i++)
                if (list[i].OutOfRange) names.Add(list[i].Room?.ID ?? -1);

            return new Dictionary<string, object>
            {
                // THE HEADLINE. Narrow on purpose — see the block comment.
                ["ok"] = outOfRange == 0,
                ["out_of_range"] = names,
                ["out_of_range_rooms"] = outOfRange,
                ["outdoor_c"] = WorldSafe.SafeObj(() => (object)WorldSafe.R(map.mapTemperature.OutdoorTemp, 1)),
                ["tolerance_c"] = WorldSafe.R(ToleranceC, 1),
                ["controllers"] = controllers.Count,
                ["controllers_unpowered"] = unpowered,
                ["controllers_switched_off"] = switchedOff,
                // A controller that cannot move heat where it stands: no room,
                // an outdoor room, or a cooler with a blocked side. The
                // session-18 shape, counted.
                ["controllers_ineffective"] = ineffective,
                ["food_rooms_uncontrolled"] = foodRoomsUncontrolled,
                ["food_rooms_unfrozen"] = foodRoomsUnfrozen,
                ["rooms"] = rows,
                ["total"] = list.Count,
                ["more"] = dropped,
                ["order"] = "out-of-range-first, then unfrozen-food, then |drift|-desc, then room-id",
                ["note"] = "`ok` is false only when a SWITCHED-ON controller's room is more than "
                    + "tolerance_c on the wrong side of its target. Food sitting warm in an "
                    + "uncontrolled room is counted here and alarmed in resources.food_rot, not "
                    + "here, because a colony with no freezer would otherwise be permanently "
                    + "not-ok. No room ROLE is published: Room.Role runs a full room analysis and "
                    + "this section is evaluated per predicate cadence — call `room <id>` for it.",
            };
        }

        private static int RoomRank(RoomRow r)
        {
            int score = 0;
            if (r.OutOfRange) score += 10000;
            if (r.Food != null && r.Food.NutritionUnrefrigerated > 0.01f) score += 5000;
            if (r.Food != null) score += 1000;
            if (r.Controllers.Count > 0) score += 100;
            return score;
        }

        private static RoomRow Row(Dictionary<int, RoomRow> rooms, Room room)
        {
            if (!rooms.TryGetValue(room.ID, out var r))
            {
                r = new RoomRow { Room = room };
                rooms[room.ID] = r;
            }
            return r;
        }

        private static Dictionary<string, object> RoomOut(RoomRow r)
        {
            var room = r.Room;
            IntVec3 at = IntVec3.Invalid;
            try { foreach (var c in room.Cells) { at = c; break; } }
            catch { }

            var ctl = new List<object>();
            float? targetMin = null, targetMax = null;
            for (int i = 0; i < r.Controllers.Count && i < ControllerCap; i++)
            {
                var c = r.Controllers[i];
                ctl.Add(new Dictionary<string, object>
                {
                    ["id"] = c.Thing.thingIDNumber,
                    ["def"] = c.Thing.def?.defName,
                    ["kind"] = c.Kind,
                    ["target_c"] = WorldSafe.R(c.TargetC, 1),
                    ["powered"] = c.Powered,
                    ["switch_on"] = c.SwitchOn,
                    ["operating_at_high_power"] = c.HighPower,
                    ["effective"] = c.Effective,
                    ["out_of_range"] = c.OutOfRange,
                });
            }
            for (int i = 0; i < r.Controllers.Count; i++)
            {
                var c = r.Controllers[i];
                if (!c.Counts) continue;
                if (!targetMin.HasValue || c.TargetC < targetMin.Value) targetMin = c.TargetC;
                if (!targetMax.HasValue || c.TargetC > targetMax.Value) targetMax = c.TargetC;
            }

            var d = new Dictionary<string, object>
            {
                ["room_id"] = room.ID,
                ["at"] = at.IsValid ? Positions.Out(at) : null,
                ["cells"] = WorldSafe.SafeObj(() => (object)room.CellCount),
                ["temp_c"] = WorldSafe.SafeObj(() => (object)WorldSafe.R(room.Temperature, 1)),
                ["uses_outdoor_temp"] = WorldSafe.SafeObj(() => (object)room.UsesOutdoorTemperature) ?? false,
                ["controllers"] = ctl,
                ["controllers_total"] = r.Controllers.Count,
                ["out_of_range"] = r.OutOfRange,
            };
            // ONE target when every counting controller agrees, a RANGE when
            // they do not — never a silent average. Two coolers set to
            // different targets is a real state and a mean would hide it.
            if (targetMin.HasValue)
            {
                if (Mathf.Approximately(targetMin.Value, targetMax.Value))
                    d["target_c"] = WorldSafe.R(targetMin.Value, 1);
                else
                {
                    d["target_c"] = null;
                    d["target_min_c"] = WorldSafe.R(targetMin.Value, 1);
                    d["target_max_c"] = WorldSafe.R(targetMax.Value, 1);
                }
                d["drift_c"] = WorldSafe.R(r.WorstDrift, 1);
            }
            else
            {
                d["target_c"] = null;
                d["drift_c"] = null;
            }
            if (r.Food != null) d["food"] = FoodRot.RoomOut(r.Food);
            return d;
        }

        // ------------------------------------------------------------------
        // THE `action` JOURNAL EVENT — the per-file private helper, matching
        // DesignationVerbs/AreaVerbs/ZoneVerbs. Deliberately not factored into a
        // shared class: parallel workers writing the same helper in one shared
        // file is the merge collision the house convention exists to avoid, and
        // the orchestrator factors it at merge time if it ever wants to.
        private static Dictionary<string, object> Act(string verb, string step, string target,
            Dictionary<string, object> extra)
        {
            var payload = new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["step"] = step,
                ["target"] = target,
            };
            if (extra != null)
                foreach (var kv in extra)
                    if (!payload.ContainsKey(kv.Key) && kv.Value != null) payload[kv.Key] = kv.Value;
            int tick = 0;
            try { tick = Find.TickManager.TicksGame; } catch { }
            long seq = Journal.Emit("action", payload, tick);
            var d = new Dictionary<string, object> { ["journal_seq"] = seq };
            if (seq == 0)
                d["provenance"] = "NOT WRITTEN — the journal writer is closed, so this mutation has no "
                    + "journal line. Treat any state changed in this session as unprovenanced.";
            return d;
        }

        private static Dictionary<string, object> NoAction()
            => new Dictionary<string, object>
            {
                ["journal_seq"] = null,
                ["provenance"] = "not applicable — dry_run mutated nothing",
            };
    }
}
