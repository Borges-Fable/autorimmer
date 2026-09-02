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

            // ============ A ROOM THAT IS NOT A ROOM YET (git-bug a1644d6) =====
            // The loop above can only list rooms that EXIST. A layout placed as
            // a freezer and never closed produces no room at all — its cells
            // belong to the map-wide outdoor blob, which this verb skips as
            // `outdoors` — so the one structure the agent is waiting on is the
            // one structure `rooms` could not mention. Run m1-20260901 read this
            // envelope for forty days and never saw its freezer in it.
            //
            // So the failing layouts ride along on the turn-level read, with the
            // gap cell named, and the caller does not have to already suspect a
            // particular layout to find out. `construction {layout_id}` has the
            // full report; this is the pointer to it.
            int layoutsTotal = 0, layoutsChecked = 0, layoutsFailing = 0;
            var layoutRows = new List<object>();
            try
            {
                var reports = LayoutEnclosure.Scan(map, LayoutEnclosure.LayoutCap,
                    out layoutsTotal, out layoutsChecked, out layoutsFailing);
                for (int i = 0; i < reports.Count; i++) layoutRows.Add(reports[i].Brief());
            }
            catch (Exception e)
            {
                Journal.EmitWarning("rooms: layout enclosure scan threw: " + e.Message);
            }

            return new Dictionary<string, object>
            {
                ["list"] = list,
                ["total"] = rooms.Count,
                ["more"] = Math.Max(0, rooms.Count - list.Count),
                ["order"] = "proper-indoor-then-size-desc",
                // Placed layouts that declared an enclosed space and do not have
                // one — an EMPTY list is the good news and is published as such,
                // because an absent key and "nothing wrong" must not look alike.
                ["layouts_unenclosed"] = layoutRows,
                ["layouts_total"] = layoutsTotal,
                ["layouts_checked"] = layoutsChecked,
                ["layouts_failing"] = layoutsFailing,
                ["layouts_cap"] = LayoutEnclosure.LayoutCap,
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

    // ===================================================== git-bug a1644d6 ===
    // LAYOUT ENCLOSURE — "is the room I placed a room YET?"
    //
    // WHY THIS EXISTS. Run `m1-20260901` placed a freezer as a layout, built it,
    // and never closed it. `room-at` on its interior answered honestly —
    // `outdoors: true, cells: 60082`, which is the whole outdoors and not a
    // 60,000-cell freezer — and nothing connected *a placed layout that was
    // supposed to be a room* to the question *is it a room*. The colony had no
    // larder, ran hand-to-mouth into winter, and starved. Finding this out cost
    // a cell-by-cell interrogation the agent had no reason to run on a layout
    // that reported `placed` with zero blockers.
    //
    // -------------------- THERE ARE TWO FAILURE MODES ------------------------
    // and a check that reports only the first passes a freezer that cannot hold
    // cold (DESIGN decisions log 2026-09-02).
    //
    //   1. NOT ENCLOSED — `Verse/Room.cs` `ProperRoom`:
    //
    //        if (TouchesMapEdge) return false;
    //        for (int i = 0; i < districts.Count; i++)
    //          if (districts[i].RegionType == RegionType.Normal) return true;
    //        return false;
    //
    //      A layout that never closed leaks into the map-wide outdoor room,
    //      which touches the map edge, so `ProperRoom` is false. That IS the
    //      `outdoors: true / cells: 60082` the run measured.
    //
    //   2. ENCLOSED AND THERMALLY OUTDOORS — `Verse/Room.cs`:
    //
    //        public bool UsesOutdoorTemperature =>
    //            TouchesMapEdge || OpenRoofCount >= Mathf.CeilToInt(CellCount * 0.25f);
    //
    //      A sealed room missing a quarter of its roof sits on the OUTDOOR
    //      temperature with `ProperRoom` still true. For a freezer that is the
    //      same dead colony by a different mechanism. Both are reported, and
    //      distinctly.
    //
    //   `PsychologicallyOutdoors` is a THIRD reading — mood-facing, different
    //   thresholds (`OpenRoofCountStopAt(300) >= 300`, or map-edge with half the
    //   roof open) — and is NOT the enclosure question. `Brief` publishes it as
    //   `indoors` and the three are never conflated.
    //
    // ------------------- NAMING THE GAP IS A COMPARISON ----------------------
    // not a search, and no flood fill over the map is involved. Two things the
    // mod already holds close it:
    //
    //   * `Layouts.Open(...)` records a `CellRect` per placed layout and it
    //     survives across days — the run called `construction {layout_id:"ly-1"}`
    //     on day 40+ successfully — and every element's `Placement` carries its
    //     `Def`, `Pos` (the game's CENTRE) and `Rot`. So the DECLARED shell
    //     cells are `GenAdj.OccupiedRect` over the wall and door elements.
    //
    //   * What actually stands at a cell is one grid read.
    //
    // THE PER-CELL TEST IS THE GAME'S OWN, `Verse/RegionTypeUtility.cs`
    // `GetExpectedRegionType(this IntVec3 c, Map map)`:
    //
    //     if (c.GetDoor(map) != null)     return RegionType.Portal;
    //     if (c.GetFence(map) != null)    return RegionType.Fence;
    //     if (c.WalkableByNormal(map))    return RegionType.Normal;
    //     … any thing with def.Fillage == Full → RegionType.None
    //     otherwise → RegionType.ImpassableFreeAirExchange
    //
    // A declared shell cell CLOSES the room exactly when that answer is `None`
    // (a full-fillage wall) or `Portal` (a door). Anything else leaks. Calling
    // the region builder's own predicate is what makes three nuances fall out
    // instead of being special-cased:
    //
    //   * A DOOR DOES NOT BREAK ENCLOSURE; AN UNBUILT DOOR DOES. A built door
    //     is `Portal`; an empty door cell is `Normal`.
    //
    //   * A WALL FRAME DOES NOT CLOSE A ROOM, AND IT IS AN EDIFICE.
    //     `RimWorld/ThingDefGenerator_Buildings.NewFrameDef_Thing` copies
    //     `building.isEdifice` from the finished def, so a wall frame REGISTERS
    //     IN THE EDIFICE GRID and `c.GetEdifice(map) != null` is true at it —
    //     keying the check on "an edifice stands here" would report a half-built
    //     wall as sealed, which is the original defect with extra steps. The
    //     same generator clamps `passability` from Impassable to
    //     `PassThroughOnly` and sets `fillPercent = 0.2f`, so the frame's cell
    //     is `Normal` and the leak is reported. That is why this file calls
    //     `GetExpectedRegionType` and never `GetEdifice`.
    //
    //   * A SANDBAG IN A WALL SLOT is `ImpassableFreeAirExchange` — impassable
    //     to a pawn, transparent to the room — and reads as open, correctly.
    //
    // ------------------- "INTENDS A ROOM" IS A DECLARATION -------------------
    // A defensive wall is not a room that failed; reporting it as one for the
    // rest of the run is how an agent learns to ignore this field. So intent is
    // decided from the IR-DERIVED DECLARATION ALONE, with no game state in it:
    // flood the layout's own rect, 4-connected, blocked by the DECLARED wall and
    // door cells. A component that never touches the rect's perimeter is a
    // declared interior; one that escapes is outside ground the rect happens to
    // cover. `intends_room` is "at least one enclosed component exists", it is
    // the same answer on the day of placement and forty days later, and a
    // straight wall's flood escapes on both sides so it is never listed.
    //
    // The flood is bounded by the layout's own rect (`CellCap`), touches no
    // region, no pathfinder and no room, and is the ONLY search in this file —
    // the enclosure question itself is answered by `ProperRoom`, per the rule
    // that the gate lives in the widget.
    //
    // ------------------------------ THE ROOF ---------------------------------
    // `place-layout` DELIBERATELY DOES NOT CONSUME THE IR'S ROOF MASK
    // (LayoutVerbs' header: a roof is a designation, not a placement, and
    // folding a second designator into that transaction would make one call mean
    // two things). So the mod does not hold the mask, and "intended roofed" is
    // taken as THE DECLARED INTERIOR — the cells the declared shell encloses.
    // For a room layout that is the same set the mask would give, it needs no
    // new IR field and no change to `place-layout`'s contract, and it is the set
    // `UsesOutdoorTemperature` is actually counting against.
    //
    // Where the built room is LARGER than this layout's declared interior — two
    // layouts sharing a wall, or a room extended by hand — the room's own
    // `OpenRoofCount` can exceed the holes named here. Both numbers are
    // published and a note says so rather than letting the shorter list read as
    // the whole truth.
    //
    // ---------------------------- OBSERVER COST ------------------------------
    // Per layout: one `Placements.Get` and one `GenAdj.OccupiedRect` per
    // element, one rect-bounded flood, one `GetExpectedRegionType` per declared
    // shell cell, one `GetRoom` per interior cell up to the distinct-room cap,
    // and one `Roofed` per interior cell. No `Room.Role`, no `Room.GetStat`, no
    // `Room.Owners`, no `ContainedAndAdjacentThings`.
    //
    // `Room.ProperRoom`, `TouchesMapEdge` and `CellCount` walk districts.
    // `OpenRoofCount` fills a `cachedOpenRoofCount` on first read and the game
    // itself reads it every tick out of `RoomTempTracker`, so it is warm in
    // practice; the cache is content-derived, invalidated by the game's own
    // dirtying, and unscribed — the same ruling `Room.Role` already has.
    //
    // `IntVec3.GetRoom(map)` bottoms out in `RegionGrid.GetValidRegionAt`, which
    // calls `TryRebuildDirtyRegionsAndRooms()`. That is `Map.MapUpdate`'s own
    // per-frame call, run here at a safe point on the main thread, and it is the
    // route `room-at` already takes — stated rather than hidden.
    public static class LayoutEnclosure
    {
        // Rect cells the flood and the interior scan will look at. 4096 is a
        // 64x64 layout; the biggest shipped template is 11x6.
        public const int CellCap = 4096;
        // Named cells per gap list. A room with 12 holes has a bigger problem
        // than a list can express, and the counts stay complete.
        public const int GapCap = 12;
        // Distinct interior rooms reported per layout. A layout with more than
        // four rooms in it is a wing, and the flags aggregate over all of them.
        public const int RoomCap = 4;
        // Layouts evaluated by the roll-ups (`rooms`, `digest.construction`).
        public const int LayoutCap = 8;


        // ---------------------------------------------------------------------
        // A declared WALL or DOOR, decided on the def alone.
        //
        // `Verse/ThingDef.IsDoor` is `typeof(Building_Door).IsAssignableFrom
        // (thingClass)`, and `ThingDef.Fillage` is Full at `fillPercent > 0.99f`
        // — which is the exact property `GetExpectedRegionType` tests for
        // `RegionType.None`. So "declared shell" and "closes the room" are
        // decided by the same two members, one on the def and one on the cell.
        // ---------------------------------------------------------------------
        public static bool IsShellDef(BuildableDef bd, out bool isDoor)
        {
            isDoor = false;
            var td = bd as ThingDef;
            if (td == null) return false;
            try
            {
                if (td.IsDoor) { isDoor = true; return true; }
                return td.Fillage == FillCategory.Full;
            }
            catch { return false; }
        }

        public static EnclosureReport Evaluate(Map map, LayoutRecord record)
        {
            var r = new EnclosureReport { LayoutId = record?.Id, Name = record?.Name };
            if (map == null || record == null) { r.Note = "no layout"; return r; }
            if (record.MapId != map.uniqueID)
            {
                r.Note = "this layout is on map " + record.MapId + ", not the current one";
                return r;
            }
            if (record.CancelledSeq != 0)
            {
                r.Cancelled = true;
                r.Note = "this layout was cancelled; enclosure is not asked of it";
                return r;
            }

            var rect = record.Rect.ClipInsideMap(map);
            r.Rect = rect;
            if (rect.Area <= 0) { r.Note = "this layout's rect is empty on this map"; return r; }
            if (rect.Area > CellCap)
            {
                r.Note = "this layout's rect is " + rect.Area + " cells, past the "
                    + CellCap + "-cell scan cap; enclosure was not evaluated";
                return r;
            }

            // --------------------------------------------- the declared shell -
            var shell = new Dictionary<IntVec3, ShellCell>();
            for (int i = 0; i < record.PlacementIds.Count; i++)
            {
                var p = Placements.Get(record.PlacementIds[i]);
                if (p?.Def == null) continue;
                if (!IsShellDef(p.Def, out bool isDoor)) continue;
                CellRect occ;
                try { occ = GenAdj.OccupiedRect(p.Pos, p.Rot, p.Def.Size); }
                catch { continue; }
                foreach (var c in occ)
                {
                    if (!c.InBounds(map)) continue;
                    if (!shell.ContainsKey(c))
                        shell[c] = new ShellCell { At = c, DefName = p.DefName,
                                                   PlacementId = p.Id, IsDoor = isDoor };
                }
            }
            r.ShellCells = shell.Count;

            // ------------------------------- what the DECLARATION encloses ----
            // The only search in this file, and it never leaves the rect. See
            // the header: this decides INTENT, not enclosure.
            var interior = new List<IntVec3>();
            r.Components = FloodInterior(rect, shell, interior);
            // South-to-north, west-to-east — the flood's own order is a stack's
            // and would move between builds. A deterministic order is what lets
            // an acceptance suite assert on `gaps[0]` and `unroofed[0]` at all.
            interior.Sort(CellOrder);
            r.InteriorCells = interior.Count;
            r.IntendsRoom = interior.Count > 0;

            // ------------------------------------------------ the shell gaps --
            var shellCells = new List<IntVec3>(shell.Keys);
            shellCells.Sort(CellOrder);
            for (int si = 0; si < shellCells.Count; si++)
            {
                var kv = new KeyValuePair<IntVec3, ShellCell>(shellCells[si], shell[shellCells[si]]);
                RegionType rt;
                try { rt = kv.Key.GetExpectedRegionType(map); }
                catch { continue; }
                // `None` is a full-fillage wall, `Portal` is a door. Everything
                // else leaks — including a wall FRAME, which is `Normal`.
                // Compared as values, not as strings: `Verse/RegionType` is a
                // [Flags] enum, so a future combined member would stringify to
                // something a name test would silently miss.
                if (rt == RegionType.None || rt == RegionType.Portal) continue;
                string token = rt.ToString();
                r.OpenShell++;
                if (r.Gaps.Count >= GapCap) continue;
                r.Gaps.Add(new Dictionary<string, object>
                {
                    ["at"] = Positions.Out(kv.Key),
                    ["def"] = kv.Value.DefName,
                    ["is_door"] = kv.Value.IsDoor,
                    ["placement_id"] = kv.Value.PlacementId,
                    ["standing"] = StandingAt(map, kv.Key),
                    // The game's own answer for the cell, so a caller can look
                    // the verdict up rather than trust this one.
                    ["region_type"] = token,
                });
            }
            r.ShellComplete = r.OpenShell == 0;

            // A layout whose declared shell encloses nothing is NOT a room that
            // failed — a defensive wall, a conduit spine, a row of solar panels
            // — and reporting it as one for the rest of the run is how an agent
            // learns to ignore this field. It still gets its gap list, which is
            // a real fact about a wall with a hole in it; it does not get an
            // enclosure verdict, and `enclosed` stays NULL rather than false.
            if (!r.IntendsRoom)
            {
                r.Note = "this layout's declared walls and doors enclose no cell of "
                    + "its own rect, so it does not declare a room and the enclosure "
                    + "question is not asked of it";
                return r;
            }

            // --------------------------------------------- the actual rooms ---
            var seen = new List<Room>();
            for (int i = 0; i < interior.Count; i++)
            {
                var c = interior[i];
                // FOG: no room detail out of unexplored ground, the rule this
                // file's header already states for `rooms`.
                bool fogged;
                try { fogged = c.Fogged(map); } catch { fogged = true; }
                if (fogged) { r.FoggedCells++; continue; }
                if (!c.Roofed(map)) { r.UnroofedCells++; if (r.RoofHoles.Count < GapCap) r.RoofHoles.Add(Positions.Out(c)); }
                if (seen.Count >= RoomCap) continue;
                Room room = null;
                try { room = c.GetRoom(map); } catch { }
                if (room == null) continue;
                bool dup = false;
                for (int j = 0; j < seen.Count; j++) if (seen[j] == room) { dup = true; break; }
                if (dup) continue;
                seen.Add(room);
            }
            for (int i = 0; i < seen.Count; i++) r.Rooms.Add(RoomRow(seen[i]));
            r.RoomsFound = seen.Count;

            if (seen.Count == 0)
            {
                r.Note = r.FoggedCells > 0
                    ? "every declared interior cell is fogged; enclosure was not read"
                    : "no room resolved at any declared interior cell";
                return r;
            }

            bool allProper = true, anyOutdoorTemp = false;
            int roomOpenRoof = 0;
            for (int i = 0; i < seen.Count; i++)
            {
                var room = seen[i];
                if (!SafeBool(() => room.ProperRoom)) allProper = false;
                if (SafeBool(() => room.UsesOutdoorTemperature)) anyOutdoorTemp = true;
                var orc = WorldSafe.SafeObj(() => (object)room.OpenRoofCount) as int?;
                if (orc.HasValue) roomOpenRoof += orc.Value;
            }
            r.Enclosed = allProper;
            r.UsesOutdoorTemp = anyOutdoorTemp;
            r.RoomOpenRoofCells = roomOpenRoof;
            return r;
        }

        // Every OPEN layout on this map, evaluated, failing ones first. The
        // roll-up `rooms` and `digest.construction` both call — one routine, so
        // a turn-level glance and a targeted read cannot disagree about whether
        // a room is a room.
        public static List<EnclosureReport> Scan(Map map, int cap,
            out int total, out int checkedCount, out int failing)
        {
            total = 0;
            checkedCount = 0;
            failing = 0;
            var rows = new List<EnclosureReport>();
            if (map == null) return rows;
            var all = Layouts.All();
            // ONE `CellCap` FOR THE WHOLE SCAN, not one per layout. `digest` is
            // documented as called constantly and this rides on it, so the
            // budget has to be a ceiling on the roll-up rather than on each
            // member of it — eight 64x64 layouts would otherwise be 32k flood
            // steps a glance. `layouts_checked` against `layouts_total` is how a
            // reader sees the budget bite.
            int budget = CellCap;
            // Newest first: the layout an agent just placed is the one it is
            // waiting on.
            for (int i = all.Count - 1; i >= 0; i--)
            {
                var rec = all[i];
                if (rec == null || rec.MapId != map.uniqueID || rec.CancelledSeq != 0) continue;
                total++;
                if (checkedCount >= cap) continue;
                int area;
                try { area = rec.Rect.ClipInsideMap(map).Area; } catch { continue; }
                if (area > budget) continue;
                budget -= area;
                checkedCount++;
                EnclosureReport rep;
                try { rep = Evaluate(map, rec); }
                catch (Exception e)
                {
                    Journal.EmitWarning("rooms: enclosure evaluation threw for layout "
                        + rec.Id + ": " + e.Message);
                    continue;
                }
                if (!rep.IntendsRoom) continue;
                rep.Track();
                if (!rep.Failing) continue;
                failing++;
                rows.Add(rep);
            }
            return rows;
        }

        // ---------------------------------------------------------------------

        private struct ShellCell
        {
            public IntVec3 At;
            public string DefName;
            public string PlacementId;
            public bool IsDoor;
        }

        // South-to-north, then west-to-east. The one ordering rule in this file,
        // applied to both cell lists so the output is stable across builds.
        private static int CellOrder(IntVec3 a, IntVec3 b)
        {
            int c = a.z.CompareTo(b.z);
            return c != 0 ? c : a.x.CompareTo(b.x);
        }

        // 4-connected flood over the rect's non-shell cells. A component that
        // reaches the rect's own perimeter ESCAPES — the declaration does not
        // close it — and its cells are not interior. Returns the number of
        // enclosed components and fills `interior` with their cells.
        private static int FloodInterior(CellRect rect,
            Dictionary<IntVec3, ShellCell> shell, List<IntVec3> interior)
        {
            var seen = new HashSet<IntVec3>();
            var stack = new List<IntVec3>();
            var comp = new List<IntVec3>();
            int components = 0;
            foreach (var start in rect)
            {
                if (shell.ContainsKey(start) || seen.Contains(start)) continue;
                comp.Clear();
                stack.Clear();
                stack.Add(start);
                seen.Add(start);
                bool escapes = false;
                while (stack.Count > 0)
                {
                    var c = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    comp.Add(c);
                    if (c.x == rect.minX || c.x == rect.maxX
                        || c.z == rect.minZ || c.z == rect.maxZ) escapes = true;
                    for (int d = 0; d < 4; d++)
                    {
                        var n = c + GenAdj.CardinalDirections[d];
                        if (!rect.Contains(n) || shell.ContainsKey(n) || seen.Contains(n)) continue;
                        seen.Add(n);
                        stack.Add(n);
                    }
                }
                if (escapes) continue;
                components++;
                interior.AddRange(comp);
            }
            return components;
        }

        // What is actually standing on a declared shell cell that did not close.
        // `blueprint` and `frame` are the honest "someone is on it"; `missing` is
        // nothing at all, which is a cancelled or destroyed element; `other` is
        // something else entirely occupying the slot.
        private static string StandingAt(Map map, IntVec3 c)
        {
            try
            {
                var list = map.thingGrid.ThingsListAtFast(c);
                bool other = false;
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t?.def == null) continue;
                    if (t is Blueprint) return "blueprint";
                    if (t is Frame) return "frame";
                    if (t.def.category == ThingCategory.Building) other = true;
                }
                return other ? "other-building" : "missing";
            }
            catch { return "unknown"; }
        }

        private static Dictionary<string, object> RoomRow(Room room)
        {
            return new Dictionary<string, object>
            {
                ["id"] = room.ID,
                ["cells"] = WorldSafe.SafeObj(() => (object)room.CellCount),
                // Verse/Room.cs ProperRoom — THE enclosure test.
                ["proper"] = SafeBool(() => room.ProperRoom),
                // Verse/Room.cs UsesOutdoorTemperature — the SECOND mechanism.
                ["uses_outdoor_temp"] = SafeBool(() => room.UsesOutdoorTemperature),
                ["touches_map_edge"] = SafeBool(() => room.TouchesMapEdge),
                ["open_roof_cells"] = WorldSafe.SafeObj(() => (object)room.OpenRoofCount),
                ["temp_c"] = WorldSafe.SafeObj(() => (object)WorldSafe.R(room.Temperature, 1)),
            };
        }

        private static bool SafeBool(Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }
    }

    // The enclosure verdict for ONE placed layout. One type, published by
    // `construction {layout_id}` in full and by `rooms` / `digest.construction`
    // in brief, so a targeted read and a turn-level glance cannot disagree.
    public sealed class EnclosureReport
    {
        public string LayoutId;
        public string Name;
        public string Note;
        public bool Cancelled;
        public CellRect Rect;

        public bool IntendsRoom;
        public int Components;
        public int ShellCells;
        public int InteriorCells;
        public int FoggedCells;
        public int OpenShell;
        public bool ShellComplete;
        public int UnroofedCells;
        public int RoomOpenRoofCells;
        public int RoomsFound;

        // NULLABLE ON PURPOSE. `false` means measured-and-open; null means the
        // question could not be answered here (fog, no room, off-map), and the
        // two must never collapse — an unanswered enclosure reading as "fine"
        // is the defect this issue exists to close, one level up.
        public bool? Enclosed;
        public bool? UsesOutdoorTemp;

        public readonly List<Dictionary<string, object>> Gaps = new List<Dictionary<string, object>>();
        public readonly List<object> RoofHoles = new List<object>();
        public readonly List<object> Rooms = new List<object>();

        // The thing an agent branches on: this layout declared a room and the
        // room is not one, OR it is one and is sitting on outdoor temperature.
        public bool Failing => IntendsRoom && (Enclosed != true || UsesOutdoorTemp == true);

        public void Track()
        {
            if (LayoutId != null) LayoutEnclosureWatch.Note(LayoutId, Failing);
        }

        public Dictionary<string, object> Out()
        {
            var d = new Dictionary<string, object>
            {
                ["layout_id"] = LayoutId,
                ["name"] = Name,
                // The declaration's own verdict, independent of build state: did
                // this layout's walls and doors ever enclose anything? A
                // defensive wall answers false here forever and is never
                // reported as a failed room.
                ["intends_room"] = IntendsRoom,
                ["declared_rooms"] = Components,
                ["shell_cells"] = ShellCells,
                ["interior_cells"] = InteriorCells,
                // Verse/Room.cs ProperRoom over every declared interior room.
                ["enclosed"] = Enclosed,
                // Verse/Room.cs UsesOutdoorTemperature over the same rooms. A
                // freezer can be `enclosed:true` and `uses_outdoor_temp:true` at
                // once, and that is the second way m1-20260901 could have died.
                ["uses_outdoor_temp"] = UsesOutdoorTemp,
                // Every declared wall/door cell that does not close the room, by
                // `Verse/RegionTypeUtility.GetExpectedRegionType`. A bare
                // `enclosed:false` repeats the defect one level up.
                ["open_shell_cells"] = OpenShell,
                ["shell_complete"] = ShellComplete,
                ["gaps"] = Gaps,
                ["gaps_more"] = Math.Max(0, OpenShell - Gaps.Count),
                // Declared interior cells with no roof — the thermal hole,
                // counted the way UsesOutdoorTemperature counts it.
                ["unroofed_cells"] = UnroofedCells,
                ["unroofed"] = RoofHoles,
                ["unroofed_more"] = Math.Max(0, UnroofedCells - RoofHoles.Count),
                ["rooms"] = Rooms,
                ["rooms_found"] = RoomsFound,
                ["failing"] = Failing,
            };
            if (Cancelled) d["cancelled"] = true;
            if (FoggedCells > 0) d["fogged_cells"] = FoggedCells;
            if (Note != null) d["note"] = Note;
            // The built room can be BIGGER than this layout's declared interior
            // (two layouts sharing a wall, a room extended by hand), so the
            // named holes can be a subset of what the flag counts. Say so rather
            // than let the shorter list read as the whole truth.
            if (RoomOpenRoofCells > UnroofedCells)
                d["unroofed_note"] = "the room's own OpenRoofCount is "
                    + RoomOpenRoofCells + " against " + UnroofedCells + " named here: "
                    + "the room extends past this layout's declared interior, so the "
                    + "named cells are a FLOOR. `room {id}` reads the whole room.";
            if (ShellComplete && Enclosed == false)
                d["shell_note"] = "every declared wall and door cell is closed and the "
                    + "space still leaks — the hole is outside this layout's own "
                    + "declaration. `room-at` on an interior cell names the room it "
                    + "actually joined.";
            var age = LayoutEnclosureWatch.Age(LayoutId);
            if (age != null) d["unenclosed_for"] = age;
            return d;
        }

        // The roll-up row: enough to act on without a second call, short enough
        // to sit in `rooms` and in the digest.
        public Dictionary<string, object> Brief()
        {
            var d = new Dictionary<string, object>
            {
                ["layout_id"] = LayoutId,
                ["name"] = Name,
                ["enclosed"] = Enclosed,
                ["uses_outdoor_temp"] = UsesOutdoorTemp,
                ["open_shell_cells"] = OpenShell,
                ["unroofed_cells"] = UnroofedCells,
                ["rect"] = Footprint.Out(Rect),
                // ONE named cell, so the roll-up itself is actionable: an agent
                // reading `rooms` is told where to look, not merely that
                // something is wrong. `construction {layout_id}` has the rest.
                ["first_gap"] = Gaps.Count > 0 ? Gaps[0] : null,
            };
            if (Note != null) d["note"] = Note;
            var age = LayoutEnclosureWatch.Age(LayoutId);
            if (age != null) d["unenclosed_for"] = age;
            return d;
        }
    }

    // ===================================================== git-bug a1644d6 ===
    // HOW LONG HAS IT BEEN LIKE THAT — acceptance item 4, and it follows
    // `f9dadc7`'s settled resolution (DESIGN decisions log 2026-09-02) to the
    // letter so the two roll-ups read consistently when the stalled-blueprint
    // half lands: an unenclosed room and a stalled blueprint are usually the
    // same event seen from two sides.
    //
    // TRACK IN MEMORY, AND PUBLISH WHAT YOU DO NOT KNOW. `AgentGameComponent`
    // has no `ExposeData`, and this project deliberately does not buy durability
    // with new scribed state (`d16a463` is an open hazard on exactly that). So
    // this table dies at a game boundary along with `Placements` and `Layouts`,
    // and `tracked_since` says when this process started watching. A reader can
    // then tell three states apart that must never collapse:
    //
    //   * stale        — observed unenclosed across at least one day boundary
    //   * not stale    — observed, and younger than that
    //   * not yet known — tracking is younger than the layout
    //
    // The third is the whole point. An absent or zero age must not read as
    // "clean": that is exactly how the original defect worked, where a flat
    // number read as a healthy one.
    //
    // IT IS SAMPLED, NOT TICKED. Nothing here runs on a tick; `Note` is called
    // from the reads that evaluate a layout (`rooms`, `digest.construction`).
    // So the age is "how long since the first read that saw it open", which is a
    // FLOOR on the real age and is published as one.
    public static class LayoutEnclosureWatch
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string, int> firstFail =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private static int trackedSince = -1;

        public static void Note(string layoutId, bool failing)
        {
            if (string.IsNullOrEmpty(layoutId)) return;
            int now;
            try { now = Find.TickManager.TicksGame; } catch { return; }
            lock (gate)
            {
                if (trackedSince < 0) trackedSince = now;
                if (!failing) { firstFail.Remove(layoutId); return; }
                if (!firstFail.ContainsKey(layoutId)) firstFail[layoutId] = now;
            }
        }

        // Null when this layout is not currently failing — presence is the
        // signal, so a caller never compares a zero against a missing key.
        public static Dictionary<string, object> Age(string layoutId)
        {
            if (string.IsNullOrEmpty(layoutId)) return null;
            int since, since0;
            lock (gate)
            {
                if (!firstFail.TryGetValue(layoutId, out since)) return null;
                since0 = trackedSince;
            }
            int now;
            try { now = Find.TickManager.TicksGame; } catch { return null; }
            // Verse GenDate.TicksPerDay.
            const int day = GenDate.TicksPerDay;
            int boundaries = Math.Max(0, now / day - since / day);
            var d = new Dictionary<string, object>
            {
                ["since_tick"] = since,
                ["ticks"] = Math.Max(0, now - since),
                ["day_boundaries"] = boundaries,
                // The f9dadc7 idiom: what this PROCESS has watched, so a reload
                // cannot silently report "not stale".
                ["tracked_since"] = since0,
                ["stale"] = boundaries >= 1,
            };
            if (since <= since0)
                d["floor_note"] = "this layout was already open at the first read this "
                    + "process made, so the age is a FLOOR — tracking is in memory only "
                    + "and resets at a load, a new game or a return to the menu.";
            return d;
        }

        // A layout id names a rect on a map. After a game boundary the map is
        // gone; see Runtime.ResetForGameBoundary and Placements' header.
        public static void Clear()
        {
            lock (gate)
            {
                firstFail.Clear();
                trackedSince = -1;
            }
        }
    }

}
