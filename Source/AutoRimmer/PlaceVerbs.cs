using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // The places the player reads (spec 2.4): `rooms` / `room <id>`, `zones`,
    // `areas`. Read-only; WorldSafe holds the hazard catalogue.
    //
    // FOG OF WAR (DESIGN decisions log 2026-08-30): a room whose first region
    // sits in fog is not reported (Room.Fogged is the game's own test); a zone
    // is hidden only when EVERY cell it owns is fogged, and a partly-explored
    // zone publishes `fogged_cells`. Areas are player-drawn and therefore never
    // fogged, but the cells they cover may be, so `home` reports both.
    //
    // ===================== THE ROOM COST, STATED ============================
    // Room.Role AND Room.GetStat both run UpdateRoomStatsAndRole() when
    // statsAndRoleDirty — every RoomStatDef worker and every RoomRoleDef worker
    // over the room's cells and contents, and statsAndRoleDirty is set from six
    // sites in Verse/Room.cs, so ordinary hauling re-arms it constantly. It is
    // idempotent and RNG-free (DigestVerb's header), so read-only holds, and one
    // call clears the flag for role and every stat together.
    //
    // The consequence for THIS verb is the design: `rooms` scores every room on
    // properties that need NO analysis (proper / indoor / prison-cell / cell
    // count), sorts, cuts to the cap, and only then analyses the survivors. So a
    // 60-room base costs `cap` analyses, not 60, and the cap is a cost ceiling
    // rather than only a context one. `analysed` is published so that is
    // checkable from the output.
    //
    // `Room.Owners` IS NO LONGER CALLED HERE AT ALL (git-bug daa269a). It was
    // both the wrong question — see `Brief`'s comment — and the third-most
    // expensive read in the file, re-entering ContainedAndAdjacentThings three
    // times per room. The rollup now shares the ONE bed snapshot.
    public static class PlaceVerbs
    {
        public const int RoomCap = 12;
        public const int ZoneCap = 20;
        public const int OwnerCap = 6;
        public const int AreaCap = 16;
        public const int AssignmentCap = 40;
        public const int RoomThingCap = 12;

        // ------------------------------ rooms -------------------------------

        [Verb("rooms")]
        public static object Rooms(VerbContext ctx)
        {
            var map = WorldSafe.CurrentMap();
            int cap = ctx.Args.Int("cap", RoomCap);
            if (cap < 1 || cap > 100) throw new VerbArgsException("cap must be 1..100");
            bool includeOutdoors = ctx.Args.Bool("include_outdoors", false);
            bool includeDoorways = ctx.Args.Bool("include_doorways", false);
            int minCells = ctx.Args.Int("min_cells", 1);

            // AllRooms is an IReadOnlyList over the real `allRooms` list
            // (Verse/RegionGrid.cs); snapshot before a loop that reaches room
            // analysis, which walks contents and can touch modded workers.
            var rooms = new List<Room>();
            int skippedFogged = 0, skippedOutdoors = 0, skippedDoorways = 0, skippedSmall = 0, skippedDead = 0;
            try
            {
                var all = map.regionGrid.AllRooms;
                for (int i = 0; i < all.Count; i++)
                {
                    var r = all[i];
                    if (r == null) continue;
                    if (r.RegionCount == 0) { skippedDead++; continue; }
                    if (WorldSafe.RoomHidden(r)) { skippedFogged++; continue; }
                    bool doorway = SafeBool(() => r.IsDoorway);
                    if (doorway && !includeDoorways) { skippedDoorways++; continue; }
                    if (!includeOutdoors && SafeBool(() => r.PsychologicallyOutdoors)) { skippedOutdoors++; continue; }
                    if (r.CellCount < minCells) { skippedSmall++; continue; }
                    rooms.Add(r);
                }
            }
            catch (Exception e)
            {
                Journal.EmitWarning("rooms: room enumeration threw: " + e.Message);
            }

            // Ordered BEFORE the cut, and on properties that cost no analysis:
            // a proper indoor room the colony built outranks a 900-cell outdoor
            // blob, and the prison cell outranks a corridor. Deterministic
            // tie-break on room ID so rwtest can assert on the order.
            rooms.Sort((a, b) =>
            {
                int c = CheapScore(b).CompareTo(CheapScore(a));
                return c != 0 ? c : a.ID.CompareTo(b.ID);
            });

            var list = new List<object>();
            for (int i = 0; i < rooms.Count && i < cap; i++) list.Add(Brief(rooms[i]));

            return new Dictionary<string, object>
            {
                ["list"] = list,
                ["total"] = rooms.Count,
                ["more"] = Math.Max(0, rooms.Count - list.Count),
                ["order"] = "proper-indoor-then-size-desc",
                // The cost ceiling, checkable from the output: exactly this many
                // rooms had Role/stats computed.
                ["analysed"] = list.Count,
                ["skipped"] = new Dictionary<string, object>
                {
                    ["fogged"] = skippedFogged,
                    ["outdoors"] = skippedOutdoors,
                    ["doorways"] = skippedDoorways,
                    ["too_small"] = skippedSmall,
                    ["dereferenced"] = skippedDead,
                },
            };
        }

        [Verb("room")]
        public static object RoomDetail(VerbContext ctx)
        {
            var map = WorldSafe.CurrentMap();
            int id = ctx.Args.IntReq("id");
            var room = WorldSafe.FindRoom(map, id);
            var d = Brief(room);

            // Every RoomStatDef, not only the five the brief line carries. One
            // analysis has already run for the brief, so these are dictionary
            // reads (Room.stats is a DefMap filled by that same pass).
            var stats = new Dictionary<string, object>();
            try
            {
                var defs = DefDatabase<RoomStatDef>.AllDefsListForReading;
                for (int i = 0; i < defs.Count; i++)
                {
                    var sd = defs[i];
                    if (sd == null) continue;
                    try { stats[sd.defName] = WorldSafe.R(room.GetStat(sd), 2); }
                    catch { }
                }
            }
            catch { }
            d["stats"] = stats;

            try
            {
                var ext = room.ExtentsClose;
                d["extents"] = new List<object> { (double)ext.minX, (double)ext.minZ, (double)ext.Width, (double)ext.Height };
            }
            catch { }

            // Snapshot IMMEDIATELY: ContainedAndAdjacentThings clears and
            // refills a per-room list on every access (WorldSafe Class E), and
            // the rollup builder below reaches stat calls.
            var contents = new List<Thing>();
            try
            {
                var raw = room.ContainedAndAdjacentThings;
                for (int i = 0; i < raw.Count; i++)
                {
                    var t = raw[i];
                    if (t == null || t is Pawn) continue;
                    if (t.def == null || t.def.category == ThingCategory.Filth
                        || t.def.category == ThingCategory.Mote
                        || t.def.category == ThingCategory.Ethereal) continue;
                    contents.Add(t);
                }
            }
            catch (Exception e)
            {
                Journal.EmitWarning("room: contents enumeration threw: " + e.Message);
            }
            d["contains"] = ThingVerbs.Rollups(map, contents, RoomThingCap, false, 0);

            // Same snapshot-first rule as the rollup in `Brief`, and for the
            // same reason: ContainedBeds yields out of the shared
            // ContainedAndAdjacentThings list.
            var bedThings = new List<Building_Bed>();
            var beds = new List<object>();
            try
            {
                foreach (var bed in room.ContainedBeds) if (bed?.def != null) bedThings.Add(bed);
            }
            catch (Exception e)
            {
                Journal.EmitWarning("room: bed enumeration threw for room "
                    + room.ID + ": " + e.Message);
                d["beds_error"] = "the bed enumeration threw and was caught; the "
                    + "`beds` list is a FLOOR. See the journal warning.";
            }
            for (int b = 0; b < bedThings.Count && beds.Count < OwnerCap; b++)
            {
                var bed = bedThings[b];
                var owners = new List<object>();
                try
                {
                    var list = bed.OwnersForReading;
                    for (int i = 0; list != null && i < list.Count && i < OwnerCap; i++)
                    {
                        if (list[i] == null) continue;
                        owners.Add(new Dictionary<string, object>
                        {
                            ["id"] = list[i].thingIDNumber,
                            ["name"] = PawnSafe.Name(list[i]),
                        });
                    }
                }
                catch (Exception e)
                {
                    // git-bug daa269a acceptance item 2: no bare catch around an
                    // owner enumeration. An eaten throw here reads as an
                    // unclaimed bed.
                    Journal.EmitWarning("room: bed " + bed.thingIDNumber
                        + " owner enumeration threw: " + e.Message);
                }
                beds.Add(new Dictionary<string, object>
                {
                    ["id"] = bed.thingIDNumber,
                    ["def"] = bed.def.defName,
                    ["at"] = Positions.Out(bed.Position),
                    ["for_prisoners"] = WorldSafe.SafeObj(() => (object)bed.ForPrisoners) ?? false,
                    ["medical"] = WorldSafe.SafeObj(() => (object)bed.Medical) ?? false,
                    ["owners"] = owners,
                });
            }
            d["beds"] = beds;
            // The list is capped at OwnerCap beds and `beds_total` (from Brief)
            // is not, so the truncation is published rather than left to be
            // inferred from a length — otherwise a seventh bed's owner appears
            // in `owners_total` with no row to explain it.
            d["beds_more"] = Math.Max(0, bedThings.Count - beds.Count);
            return d;
        }

        private static int CheapScore(Room r)
        {
            int score = 0;
            try
            {
                if (SafeBool(() => r.ProperRoom)) score += 100000;
                if (!SafeBool(() => r.PsychologicallyOutdoors)) score += 50000;
                if (r.IsPrisonCell) score += 20000;
                if (SafeBool(() => r.IsDoorway)) score -= 40000;
                score += Math.Min(r.CellCount, 10000);
            }
            catch { }
            return score;
        }

        private static Dictionary<string, object> Brief(Room room)
        {
            // THE analysis call. Role and every stat come out of the one
            // UpdateRoomStatsAndRole pass this triggers.
            RoomRoleDef role = null;
            try { role = room.Role; }
            catch (Exception e) { Journal.EmitWarning("rooms: Room.Role threw for room " + room.ID + ": " + e.Message); }

            // ================= WHO SLEEPS HERE (git-bug daa269a) =============
            // NOT `Room.Owners`, and the difference is the whole bug.
            // `Verse/Room.cs` `Owners`, by member name:
            //
            //   if (TouchesMapEdge || IsHuge || (Role != Bedroom && Role !=
            //       PrisonCell && Role != Barracks && Role != PrisonBarracks))
            //       yield break;
            //   var beds = ContainedBeds.Where(x => x.def.building.bed_humanlike);
            //   if (beds.Count() > 1 && (Role == Barracks || Role == PrisonBarracks)
            //       && beds.Where(b => b.OwnersForReading.Any()).Count() > 1)
            //       yield break;
            //
            // Run m1-20260901's room 38 — a Barracks with three owned beds —
            // takes the SECOND `yield break` exactly, so this field published
            // `owners_total: 0` beside a `beds[]` block naming all three
            // colonists, and nothing threw. The gate is not the Barracks role,
            // it is MORE THAN ONE OWNED BED, which is why a two-colonist
            // barracks never showed it. `TouchesMapEdge` and `IsHuge` are two
            // further silent-empty conditions on the same getter.
            //
            // Vanilla is not broken; it answers "WHOSE ROOM IS THIS", a
            // bedroom-ownership question with an honest empty answer for a
            // shared barracks. This field is asked "who sleeps in here", so it
            // comes off `ContainedBeds` / `OwnersForReading` — the same route
            // `room`'s own `beds[]` block uses and gets right, which is what
            // makes the two incapable of disagreeing. (DESIGN decisions log
            // 2026-09-02.) Vanilla's reading is still recoverable from what is
            // published here: it is `owners` whenever `beds_owned <= 1`, and
            // `role` already carries the room-role verdict it feeds.
            var owners = new List<object>();
            var ownerIds = new HashSet<int>();
            int bedCount = 0, ownedBeds = 0;
            bool ownersThrew = false;
            try
            {
                // SNAPSHOT FIRST. Room.ContainedBeds yields LAZILY out of
                // ContainedAndAdjacentThings, whose backing list is cleared and
                // refilled on every access (WorldSafe Class E), so the
                // enumeration must not be live while anything else reaches the
                // room. Materialise, then read owners off the snapshot.
                var beds = new List<Building_Bed>();
                foreach (var bed in room.ContainedBeds) if (bed?.def != null) beds.Add(bed);
                bedCount = beds.Count;
                for (int i = 0; i < beds.Count; i++)
                {
                    // Building_Bed.OwnersForReading is
                    // CompAssignableToPawn.AssignedPawnsForReading — a plain
                    // list read behind a GetComp, no lazy assignment.
                    var list = beds[i].OwnersForReading;
                    if (list == null || list.Count == 0) continue;
                    ownedBeds++;
                    for (int j = 0; j < list.Count; j++)
                    {
                        var p = list[j];
                        // DISTINCT PAWNS, not bed-slots: a double bed lists the
                        // same two owners twice across the pair and `owners_total`
                        // is a headcount.
                        if (p == null || !ownerIds.Add(p.thingIDNumber)) continue;
                        if (owners.Count >= OwnerCap) continue;
                        owners.Add(new Dictionary<string, object>
                        {
                            ["id"] = p.thingIDNumber,
                            ["name"] = PawnSafe.Name(p),
                        });
                    }
                }
            }
            catch (Exception e)
            {
                // NOT a bare `catch {}` (git-bug daa269a acceptance item 2). A
                // swallowed throw here presents as a legitimately empty room,
                // which is exactly the reading that made the original defect
                // undiagnosable from the envelope.
                ownersThrew = true;
                Journal.EmitWarning("rooms: bed-owner rollup threw for room "
                    + room.ID + ": " + e.Message);
            }

            IntVec3 at = IntVec3.Invalid;
            try { foreach (var c in room.Cells) { at = c; break; } }
            catch { }

            var d = new Dictionary<string, object>
            {
                ["id"] = room.ID,
                ["role"] = role?.defName,
                ["role_label"] = role?.label,
                ["cells"] = room.CellCount,
                ["regions"] = WorldSafe.SafeObj(() => (object)room.RegionCount),
                ["indoors"] = !SafeBool(() => room.PsychologicallyOutdoors),
                ["proper"] = SafeBool(() => room.ProperRoom),
                ["doorway"] = SafeBool(() => room.IsDoorway),
                ["prison_cell"] = room.IsPrisonCell,
                ["touches_map_edge"] = SafeBool(() => room.TouchesMapEdge),
                ["open_roof_cells"] = WorldSafe.SafeObj(() => (object)room.OpenRoofCount),
                // Room.Temperature is tempTracker.temperatureInt — a plain field
                // read (Verse/RoomTempTracker.cs). Meaningless for a room that
                // uses the outdoor temperature, so it is published with the flag
                // that says which it is rather than silently.
                ["temp_c"] = WorldSafe.SafeObj(() => (object)WorldSafe.R(room.Temperature, 1)),
                ["uses_outdoor_temp"] = SafeBool(() => room.UsesOutdoorTemperature),
                ["owners"] = owners,
                ["owners_total"] = ownerIds.Count,
                ["owners_more"] = Math.Max(0, ownerIds.Count - owners.Count),
                // The route, named, PawnSafe-style — so a reader never has to
                // guess which of the game's two ownership questions this
                // answers. `room.Owners` is the other one.
                ["owners_source"] = "contained-beds",
                // The denominator that makes a zero readable. `owners_total: 0`
                // with `beds_total: 0` is an empty room; with `beds_total: 3`
                // and `beds_owned: 0` it is three unclaimed beds. Those were
                // indistinguishable before, and telling them apart is the whole
                // reason the defect went unnoticed for a run.
                ["beds_total"] = bedCount,
                ["beds_owned"] = ownedBeds,
                ["at"] = at.IsValid ? Positions.Out(at) : null,
            };
            // Presence is the signal (Dev.NoteFog's rule): a `false` here would
            // read exactly like a key that was never published.
            if (ownersThrew)
                d["owners_error"] = "the bed-owner rollup threw and was caught; "
                    + "`owners_total` is a FLOOR, not a census. See the journal warning.";
            AddStat(d, "impressiveness", room, RoomStatDefOf.Impressiveness);
            AddStat(d, "beauty", room, RoomStatDefOf.Beauty);
            AddStat(d, "cleanliness", room, RoomStatDefOf.Cleanliness);
            AddStat(d, "space", room, RoomStatDefOf.Space);
            AddStat(d, "wealth", room, RoomStatDefOf.Wealth);
            return d;
        }

        private static void AddStat(Dictionary<string, object> d, string key, Room room, RoomStatDef stat)
        {
            try { d[key] = WorldSafe.R(room.GetStat(stat), 2); }
            catch { d[key] = null; }
        }

        private static bool SafeBool(Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }

        // ------------------------------ zones -------------------------------

        [Verb("zones")]
        public static object Zones(VerbContext ctx)
        {
            var map = WorldSafe.CurrentMap();
            string kind = ctx.Args.Str("kind", "all");
            if (kind != "all" && kind != "stockpile" && kind != "growing")
                throw new VerbArgsException("kind must be all|stockpile|growing");
            int cap = ctx.Args.Int("cap", ZoneCap);
            if (cap < 1 || cap > 100) throw new VerbArgsException("cap must be 1..100");

            var stockpiles = new List<object>();
            var growing = new List<object>();
            int stockpileTotal = 0, growingTotal = 0, otherTotal = 0, skippedFogged = 0;

            // AllZones is the real backing list (Verse/ZoneManager.cs).
            var zones = new List<Zone>(map.zoneManager.AllZones);
            zones.Sort((a, b) => (b?.CellCount ?? 0).CompareTo(a?.CellCount ?? 0));
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                if (z == null) continue;
                if (WorldSafe.ZoneHidden(z, map, out int fogged)) { skippedFogged++; continue; }
                if (z is Zone_Stockpile sp)
                {
                    stockpileTotal++;
                    if (kind != "growing" && stockpiles.Count < cap) stockpiles.Add(Stockpile(map, sp, fogged));
                }
                else if (z is Zone_Growing zg)
                {
                    growingTotal++;
                    if (kind != "stockpile" && growing.Count < cap) growing.Add(Growing(map, zg, fogged));
                }
                else
                {
                    otherTotal++;
                }
            }

            return new Dictionary<string, object>
            {
                ["stockpiles"] = new Dictionary<string, object>
                {
                    ["list"] = stockpiles,
                    ["total"] = stockpileTotal,
                    ["more"] = Math.Max(0, stockpileTotal - stockpiles.Count),
                },
                ["growing"] = new Dictionary<string, object>
                {
                    ["list"] = growing,
                    ["total"] = growingTotal,
                    ["more"] = Math.Max(0, growingTotal - growing.Count),
                },
                ["other_zones"] = otherTotal,
                ["order"] = "cells-desc",
                ["skipped"] = new Dictionary<string, object> { ["fogged"] = skippedFogged },
            };
        }

        private static Dictionary<string, object> Stockpile(Map map, Zone_Stockpile zone, int foggedCells)
        {
            var settings = zone.settings;
            var d = new Dictionary<string, object>
            {
                ["id"] = zone.ID,
                ["kind"] = "stockpile",
                ["label"] = zone.label,
                ["cells"] = zone.CellCount,
                ["fogged_cells"] = foggedCells,
                ["priority"] = WorldSafe.Safe(() => settings?.Priority.ToString()),
                // CellCount - HeldThingsCount, the game's own arithmetic; a cell
                // holding a stack still counts as occupied.
                ["space_remaining"] = WorldSafe.SafeObj(() => (object)zone.SpaceRemaining),
                ["at"] = ZoneAt(zone),
            };
            try
            {
                // The denominator is the zone's OWN parent settings — the
                // EverStorable fixed filter, i.e. exactly what its storage tab
                // could offer. That static is built once per session on first
                // use (StorageSettings.EverStorableFixedSettings) and is a pure
                // def-derived cache, not game state.
                d["filter"] = FilterSummary.Build(settings?.filter,
                    zone.GetParentStoreSettings()?.filter, "storable");
            }
            catch (Exception e)
            {
                Journal.EmitWarning("zones: filter summary threw for zone " + zone.ID + ": " + e.Message);
            }
            return d;
        }

        private static Dictionary<string, object> Growing(Map map, Zone_Growing zone, int foggedCells)
        {
            // NEVER PlantDefToGrow / GetPlantDefToGrow: the getter assigns and
            // SCRIBES a default on a never-configured zone (WorldSafe Class A).
            var plant = WorldSafe.PlantToGrow(zone);
            var cells = WorldSafe.ZoneCells(zone); // never Zone.Cells — it shuffles (Class R)

            int planted = 0, wrongPlant = 0, harvestable = 0, empty = 0, blighted = 0;
            float growthSum = 0f, growthMin = 2f, growthMax = -1f;
            try
            {
                for (int i = 0; i < cells.Count; i++)
                {
                    var c = cells[i];
                    if (!c.InBounds(map)) continue;
                    Plant found = null;
                    var list = map.thingGrid.ThingsListAtFast(c);
                    for (int j = 0; j < list.Count; j++)
                        if (list[j] is Plant p) { found = p; break; }
                    if (found == null) { empty++; continue; }
                    if (plant != null && found.def != plant) { wrongPlant++; continue; }
                    planted++;
                    float g = found.Growth; // growthInt, a plain field (RimWorld/Plant.cs)
                    growthSum += g;
                    if (g < growthMin) growthMin = g;
                    if (g > growthMax) growthMax = g;
                    if (WorldSafe.SafeObj(() => (object)found.HarvestableNow) as bool? ?? false) harvestable++;
                    if (WorldSafe.SafeObj(() => (object)found.Blighted) as bool? ?? false) blighted++;
                }
            }
            catch (Exception e)
            {
                Journal.EmitWarning("zones: growing-zone scan threw for zone " + zone.ID + ": " + e.Message);
            }

            return new Dictionary<string, object>
            {
                ["id"] = zone.ID,
                ["kind"] = "growing",
                ["label"] = zone.label,
                ["cells"] = zone.CellCount,
                ["fogged_cells"] = foggedCells,
                ["plant"] = plant?.defName,
                ["plant_label"] = plant?.label,
                // false = the player has never set one and the game has not been
                // asked either. We decline to ask, because asking WRITES the
                // answer into the save. `source` names the route, PawnSafe-style.
                ["plant_configured"] = plant != null,
                ["plant_source"] = WorldSafe.PlantRefOk ? "backing-field" : "unavailable",
                ["allow_sow"] = zone.allowSow,
                ["allow_cut"] = zone.allowCut,
                ["planted"] = planted,
                ["wrong_plant"] = wrongPlant,
                ["empty_cells"] = empty,
                ["harvestable"] = harvestable,
                ["blighted"] = blighted,
                ["growth_avg_pct"] = planted > 0 ? (object)WorldSafe.Pct(growthSum / planted) : null,
                ["growth_min_pct"] = planted > 0 ? (object)WorldSafe.Pct(growthMin) : null,
                ["growth_max_pct"] = planted > 0 ? (object)WorldSafe.Pct(growthMax) : null,
                ["at"] = ZoneAt(zone),
            };
        }

        private static object ZoneAt(Zone zone)
        {
            var cells = WorldSafe.ZoneCells(zone);
            return cells.Count > 0 ? Positions.Out(cells[0]) : null;
        }

        // ------------------------------ areas -------------------------------

        [Verb("areas")]
        public static object Areas(VerbContext ctx)
        {
            var map = WorldSafe.CurrentMap();
            var areas = new List<object>();
            // AllAreas is the real backing list (Verse/AreaManager.cs). Area's
            // TrueCount, Label, Mutable and AssignableAsAllowed are all plain
            // reads over a BoolGrid — nothing lazy, nothing cached-on-read.
            var all = new List<Area>(map.areaManager.AllAreas);
            all.Sort((a, b) =>
            {
                int c = (a?.ListPriority ?? 0).CompareTo(b?.ListPriority ?? 0);
                return c != 0 ? -c : (a?.ID ?? 0).CompareTo(b?.ID ?? 0);
            });
            for (int i = 0; i < all.Count && i < AreaCap; i++)
            {
                var a = all[i];
                if (a == null) continue;
                areas.Add(new Dictionary<string, object>
                {
                    ["id"] = a.ID,
                    ["label"] = WorldSafe.Safe(() => a.Label),
                    ["kind"] = a.GetType().Name,
                    ["cells"] = WorldSafe.SafeObj(() => (object)a.TrueCount),
                    ["mutable"] = WorldSafe.SafeObj(() => (object)a.Mutable) ?? false,
                    // Whether it can be handed to a pawn's Restrict tab at all —
                    // Home/BuildRoof/NoRoof cannot.
                    ["assignable"] = WorldSafe.SafeObj(() => (object)a.AssignableAsAllowed()) ?? false,
                });
            }

            // Per-pawn assignment. AreaUtility.AreaAllowedLabel reads the
            // NULL-GUARDED AreaRestrictionInPawnCurrentMap; the Effective
            // variant throws for a pawn with no MapHeld (PawnSafe Class D).
            var assignments = new List<object>();
            int assignedTotal = 0;
            var pawns = new List<Pawn>(map.mapPawns.AllPawnsSpawned);
            pawns.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
            for (int i = 0; i < pawns.Count; i++)
            {
                var p = pawns[i];
                if (p == null || p.Dead) continue;
                if (PawnSafe.Hidden(p, map)) continue;
                string cls = PawnSafe.Classify(p);
                // Animals are INCLUDED deliberately: DESIGN's non-goals defer
                // animal MANAGEMENT, not animal observation, and area
                // restriction is one of the seven alert-bearing animal states.
                if (cls != PawnSafe.ClassColonist && cls != PawnSafe.ClassSlave
                    && cls != PawnSafe.ClassAnimal && cls != PawnSafe.ClassPrisoner) continue;
                var ps = p.playerSettings;
                if (ps == null) continue;
                assignedTotal++;
                if (assignments.Count >= AssignmentCap) continue;
                assignments.Add(new Dictionary<string, object>
                {
                    ["id"] = p.thingIDNumber,
                    ["name"] = PawnSafe.Name(p),
                    ["class"] = cls,
                    ["area"] = WorldSafe.Safe(() => ps.AreaRestrictionInPawnCurrentMap?.Label),
                    ["label"] = WorldSafe.Safe(() => AreaUtility.AreaAllowedLabel(p)),
                    ["respects"] = WorldSafe.SafeObj(() => (object)ps.RespectsAllowedArea) ?? false,
                });
            }

            var home = map.areaManager?.Home;
            return new Dictionary<string, object>
            {
                ["list"] = areas,
                ["total"] = all.Count,
                ["more"] = Math.Max(0, all.Count - areas.Count),
                ["order"] = "list-priority-desc",
                ["home_cells"] = WorldSafe.SafeObj(() => (object)(home?.TrueCount ?? 0)),
                ["assignments"] = assignments,
                ["assignments_total"] = assignedTotal,
                ["assignments_more"] = Math.Max(0, assignedTotal - assignments.Count),
            };
        }
    }
}
