using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // Spatial verbs (spec 2.3). Queries return candidates + reasons, never bare
    // booleans; every position argument goes through Positions.Resolve, so
    // landmarks/pawn:/thing: work everywhere. Read-only throughout.
    public static class SpatialVerbs
    {
        [Verb("map-view")]
        public static object MapView(VerbContext ctx)
        {
            var map = Map();
            CellRect rect;
            if (ctx.Args.Has("rect"))
            {
                if (!(ctx.Args.Raw("rect") is List<object> r) || r.Count != 4
                    || !(r[0] is double rx) || !(r[1] is double rz) || !(r[2] is double rw) || !(r[3] is double rh))
                    throw new VerbArgsException("rect must be [x,z,w,h]");
                rect = new CellRect((int)rx, (int)rz, Math.Max(1, (int)rw), Math.Max(1, (int)rh));
            }
            else
            {
                var around = Positions.Resolve(map, ctx.Args.Raw("around")
                    ?? throw new VerbArgsException("map-view needs 'rect' or 'around'"));
                int radius = ctx.Args.Int("radius", 12);
                if (radius < 1 || radius > CropRenderer.MaxSide / 2)
                    throw new VerbArgsException($"radius must be 1..{CropRenderer.MaxSide / 2}");
                rect = CellRect.CenteredOn(around, radius);
            }
            var layers = ctx.Args.Has("layers") ? ctx.Args.StrList("layers") : new List<string>(CropRenderer.DefaultLayers);
            return CropRenderer.Render(map, rect, layers);
        }

        // Scored origin candidates for a W x H rect. Spiral out from 'near';
        // per-cell requirements fail fast and the first failure is tallied so
        // the caller learns WHY space is scarce, not just that it is.
        [Verb("find-rect")]
        public static object FindRect(VerbContext ctx)
        {
            var map = Map();
            int w = ctx.Args.IntReq("w");
            int h = ctx.Args.IntReq("h");
            if (w < 1 || h < 1 || w > 30 || h > 30) throw new VerbArgsException("w,h must be 1..30");
            var near = ctx.Args.Has("near") ? Positions.Resolve(map, ctx.Args.Raw("near")) : map.Center;
            int max = Math.Min(20, ctx.Args.Int("max", 5));
            var require = ctx.Args.Has("require") ? ctx.Args.StrList("require") : new List<string> { "buildable" };

            IntVec3 reachFrom = IntVec3.Invalid;
            foreach (var req in require)
                if (req.StartsWith("reachable-from:", StringComparison.Ordinal))
                    reachFrom = Positions.Resolve(map, req.Substring("reachable-from:".Length));

            var candidates = new List<object>();
            var rejected = new Dictionary<string, int>();
            int examined = 0;
            const int ExamineCap = 6000;

            // Ring walk outward from near: origin candidates in growing squares,
            // so the first accepted candidates are also the closest.
            for (int ring = 0; ring < 80 && candidates.Count < max && examined < ExamineCap; ring++)
            {
                foreach (var origin in RingOrigins(near, ring))
                {
                    if (candidates.Count >= max || ++examined > ExamineCap) break;
                    var rect = new CellRect(origin.x, origin.z, w, h);
                    if (rect.minX < 0 || rect.minZ < 0 || rect.maxX >= map.Size.x || rect.maxZ >= map.Size.z)
                    {
                        Tally(rejected, "out-of-bounds");
                        continue;
                    }
                    string fail = CheckRect(map, rect, require, reachFrom);
                    if (fail != null)
                    {
                        Tally(rejected, fail);
                        continue;
                    }
                    var center = rect.CenterCell;
                    candidates.Add(new Dictionary<string, object>
                    {
                        ["at"] = Positions.Out(origin),
                        ["center"] = Positions.Out(center),
                        ["dist"] = Math.Round(near.DistanceTo(center), 1),
                    });
                }
            }
            // Ring order sorts by ORIGIN distance; the score is center
            // distance, so make the ordering match the score.
            candidates.Sort((a, b) =>
                ((double)((Dictionary<string, object>)a)["dist"]).CompareTo(
                    (double)((Dictionary<string, object>)b)["dist"]));
            return new Dictionary<string, object>
            {
                ["candidates"] = candidates,
                ["examined"] = examined,
                ["rejected"] = ToTree(rejected),
            };
        }

        private static IEnumerable<IntVec3> RingOrigins(IntVec3 near, int ring)
        {
            if (ring == 0)
            {
                yield return near;
                yield break;
            }
            for (int dx = -ring; dx <= ring; dx++)
            {
                yield return new IntVec3(near.x + dx, 0, near.z - ring);
                yield return new IntVec3(near.x + dx, 0, near.z + ring);
            }
            for (int dz = -ring + 1; dz <= ring - 1; dz++)
            {
                yield return new IntVec3(near.x - ring, 0, near.z + dz);
                yield return new IntVec3(near.x + ring, 0, near.z + dz);
            }
        }

        private static string CheckRect(Map map, CellRect rect, List<string> require, IntVec3 reachFrom)
        {
            foreach (var c in rect)
            {
                foreach (var req in require)
                {
                    switch (req)
                    {
                        case "buildable":
                            // Heavy affordance carries walls and every normal
                            // building; no standing edifice in the footprint.
                            if (c.GetEdifice(map) != null) return "edifice-in-way";
                            if (!map.terrainGrid.TerrainAt(c).affordances.Contains(TerrainAffordanceDefOf.Heavy))
                                return "terrain-not-buildable";
                            break;
                        case "walkable":
                            if (!c.Walkable(map)) return "not-walkable";
                            break;
                        case "unroofed":
                            if (c.Roofed(map)) return "roofed";
                            break;
                        case "roofed":
                            if (!c.Roofed(map)) return "unroofed";
                            break;
                        default:
                            if (!req.StartsWith("reachable-from:", StringComparison.Ordinal))
                                throw new VerbArgsException($"unknown requirement '{req}' (buildable|walkable|unroofed|roofed|reachable-from:P)");
                            break;
                    }
                }
            }
            if (reachFrom.IsValid
                && !map.reachability.CanReach(reachFrom, rect.CenterCell, PathEndMode.OnCell,
                        TraverseParms.For(TraverseMode.PassDoors)))
                return "unreachable";
            return null;
        }

        [Verb("nearest")]
        public static object Nearest(VerbContext ctx)
        {
            var map = Map();
            var from = ctx.Args.Has("from") ? Positions.Resolve(map, ctx.Args.Raw("from")) : map.Center;
            int max = Math.Min(20, ctx.Args.Int("max", 5));

            List<Thing> pool;
            string what;
            if (ctx.Args.Has("def"))
            {
                what = ctx.Args.Str("def");
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(what)
                    ?? throw new VerbArgsException($"no ThingDef named '{what}'");
                pool = new List<Thing>(map.listerThings.ThingsOfDef(def));
            }
            else
            {
                what = ctx.Args.Str("category")
                    ?? throw new VerbArgsException("nearest needs 'def' or 'category'");
                ThingRequestGroup group;
                switch (what)
                {
                    case "food": group = ThingRequestGroup.FoodSourceNotPlantOrTree; break;
                    case "meds": group = ThingRequestGroup.Medicine; break;
                    case "apparel": group = ThingRequestGroup.Apparel; break;
                    case "weapons": group = ThingRequestGroup.Weapon; break;
                    default: throw new VerbArgsException("category must be food|meds|apparel|weapons (or use 'def')");
                }
                pool = new List<Thing>(map.listerThings.ThingsInGroup(group));
            }

            pool.Sort((a, b) => a.PositionHeld.DistanceToSquared(from).CompareTo(b.PositionHeld.DistanceToSquared(from)));
            var hits = new List<object>();
            for (int i = 0; i < pool.Count && hits.Count < max; i++)
            {
                var t = pool[i];
                if (!t.Spawned || t.PositionHeld.Fogged(map)) continue;
                hits.Add(new Dictionary<string, object>
                {
                    ["id"] = t.thingIDNumber,
                    ["def"] = t.def.defName,
                    ["label"] = t.LabelShort,
                    ["at"] = Positions.Out(t.PositionHeld),
                    ["dist"] = Math.Round(t.PositionHeld.DistanceTo(from), 1),
                    ["count"] = t.stackCount,
                });
            }
            return new Dictionary<string, object> { ["query"] = what, ["from"] = Positions.Out(from), ["hits"] = hits };
        }

        [Verb("reachable")]
        public static object Reachable(VerbContext ctx)
        {
            var map = Map();
            var from = Positions.Resolve(map, ctx.Args.Raw("from") ?? throw new VerbArgsException("needs 'from'"));
            var to = Positions.Resolve(map, ctx.Args.Raw("to") ?? throw new VerbArgsException("needs 'to'"));
            Pawn pawn = null;
            if (ctx.Args.Has("pawn")) pawn = FindPawn(map, ctx.Args.IntReq("pawn"));
            var parms = pawn != null ? TraverseParms.For(pawn) : TraverseParms.For(TraverseMode.PassDoors);
            bool ok = map.reachability.CanReach(from, to, PathEndMode.OnCell, parms);
            string note = null;
            if (!ok)
            {
                if (!from.Walkable(map)) note = "from-cell is not walkable";
                else if (!to.Walkable(map)) note = "to-cell is not walkable (wall or impassable)";
                else note = "no path: separated by walls/terrain" + (pawn != null ? " for this pawn" : "");
            }
            var data = new Dictionary<string, object>
            {
                ["reachable"] = ok,
                ["from"] = Positions.Out(from),
                ["to"] = Positions.Out(to),
            };
            if (pawn != null) data["pawn"] = pawn.LabelShortCap.ToString();
            if (note != null) data["note"] = note;
            return data;
        }

        [Verb("room-at")]
        public static object RoomAt(VerbContext ctx)
        {
            var map = Map();
            var at = Positions.Resolve(map, ctx.Args.Raw("at") ?? throw new VerbArgsException("needs 'at'"));
            var room = at.GetRoom(map);
            if (room == null)
                return new Dictionary<string, object> { ["at"] = Positions.Out(at), ["room"] = null };
            var data = new Dictionary<string, object>
            {
                ["at"] = Positions.Out(at),
                ["id"] = room.ID,
                ["role"] = room.Role?.label,
                ["outdoors"] = room.PsychologicallyOutdoors,
                ["cells"] = room.CellCount,
            };
            if (!room.PsychologicallyOutdoors && !room.TouchesMapEdge)
            {
                // The same lazy stat computation the inspect pane triggers.
                data["temp_c"] = Math.Round(room.Temperature, 1);
                try { data["impressiveness"] = Math.Round(room.GetStat(RoomStatDefOf.Impressiveness), 1); }
                catch { }
            }
            return data;
        }

        [Verb("path-cost")]
        public static object PathCost(VerbContext ctx)
        {
            var map = Map();
            var from = Positions.Resolve(map, ctx.Args.Raw("from") ?? throw new VerbArgsException("needs 'from'"));
            var to = Positions.Resolve(map, ctx.Args.Raw("to") ?? throw new VerbArgsException("needs 'to'"));
            Pawn pawn = null;
            if (ctx.Args.Has("pawn")) pawn = FindPawn(map, ctx.Args.IntReq("pawn"));
            var parms = pawn != null ? TraverseParms.For(pawn) : TraverseParms.For(TraverseMode.PassDoors);
            var path = map.pathFinder.FindPathNow(from, to, parms);
            try
            {
                if (path == null || !path.Found)
                    return new Dictionary<string, object> { ["found"] = false };
                return new Dictionary<string, object>
                {
                    ["found"] = true,
                    ["cost"] = Math.Round(path.TotalCost, 1),
                    ["length"] = path.NodesLeftCount,
                };
            }
            finally
            {
                path?.ReleaseToPool();
            }
        }

        // landmark {set:{name, at:P}} | {list:true} | {remove:name}. `set`
        // returns a crop echo around the point — the same helper wave-3
        // mutation verbs will call for before/after evidence.
        [Verb("landmark")]
        public static object Landmark(VerbContext ctx)
        {
            var map = Map();
            var comp = LandmarkComponent.Current
                ?? throw new VerbArgsException("no landmark store (no game loaded?)");
            if (ctx.Args.Has("set"))
            {
                if (!(ctx.Args.Raw("set") is Dictionary<string, object> setObj))
                    throw new VerbArgsException("set must be {name, at}");
                var sa = new VerbArgs(setObj);
                string name = sa.StrReq("name");
                if (name.Contains(",") || name.StartsWith("pawn:") || name.StartsWith("thing:") || name.Length > 40)
                    throw new VerbArgsException("landmark names must not look like coordinates or ids, max 40 chars");
                var at = Positions.Resolve(map, sa.Raw("at") ?? throw new VerbArgsException("set needs 'at'"));
                comp.Set(name, at);
                return new Dictionary<string, object>
                {
                    ["set"] = name,
                    ["at"] = Positions.Out(at),
                    ["crop"] = CropRenderer.Render(map, CellRect.CenteredOn(at, 5),
                        new List<string>(CropRenderer.DefaultLayers)),
                };
            }
            if (ctx.Args.Has("remove"))
            {
                string name = ctx.Args.Str("remove");
                return new Dictionary<string, object> { ["removed"] = comp.Remove(name), ["name"] = name };
            }
            var all = new Dictionary<string, object>();
            foreach (var kv in comp.All) all[kv.Key] = Positions.Out(kv.Value);
            return new Dictionary<string, object> { ["landmarks"] = all };
        }

        private static Map Map()
            => Find.CurrentMap ?? throw new VerbArgsException("no current map");

        private static Pawn FindPawn(Map map, int id)
        {
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
                if (pawns[i].thingIDNumber == id) return pawns[i];
            throw new VerbArgsException($"no spawned pawn with id {id}");
        }

        private static void Tally(Dictionary<string, int> d, string key)
            => d[key] = d.TryGetValue(key, out var c) ? c + 1 : 1;

        private static Dictionary<string, object> ToTree(Dictionary<string, int> d)
        {
            var t = new Dictionary<string, object>();
            foreach (var kv in d) t[kv.Key] = kv.Value;
            return t;
        }
    }
}
