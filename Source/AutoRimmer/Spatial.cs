using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // Spatial substrate (spec 2.3): position resolution, the landmark registry,
    // and the ONE crop renderer every map look goes through — the viewport verb
    // now, wave-3 mutation echoes later. Principle (DESIGN §Tile-system risk):
    // the model does topology, the game does geometry.

    // P ::= "x,z" | [x,z] | "<landmark>" | "pawn:<id>" | "thing:<id>"
    public static class Positions
    {
        public static IntVec3 Resolve(Map map, object arg)
        {
            switch (arg)
            {
                case List<object> list when list.Count == 2 && list[0] is double x && list[1] is double z:
                    return Validate(map, new IntVec3((int)x, 0, (int)z), "[x,z]");
                case string s:
                    return ResolveString(map, s);
                default:
                    throw new VerbArgsException("position must be \"x,z\", [x,z], a landmark name, pawn:<id> or thing:<id>");
            }
        }

        private static IntVec3 ResolveString(Map map, string s)
        {
            if (s.StartsWith("pawn:", StringComparison.Ordinal) || s.StartsWith("thing:", StringComparison.Ordinal))
            {
                string idPart = s.Substring(s.IndexOf(':') + 1);
                if (!int.TryParse(idPart, out int id))
                    throw new VerbArgsException($"'{s}': id must be a thingIDNumber");
                var things = map.listerThings.AllThings;
                for (int i = 0; i < things.Count; i++)
                    if (things[i].thingIDNumber == id)
                        return Validate(map, things[i].PositionHeld, s);
                throw new VerbArgsException($"'{s}': no spawned thing with that id on the current map");
            }
            int comma = s.IndexOf(',');
            if (comma > 0
                && int.TryParse(s.Substring(0, comma).Trim(), out int px)
                && int.TryParse(s.Substring(comma + 1).Trim(), out int pz))
                return Validate(map, new IntVec3(px, 0, pz), s);
            var lm = LandmarkComponent.Current;
            if (lm != null && lm.TryGet(s, out var pos))
                return Validate(map, pos, "landmark '" + s + "'");
            throw new VerbArgsException($"'{s}' is neither coordinates, a known landmark, pawn:<id> nor thing:<id>");
        }

        private static IntVec3 Validate(Map map, IntVec3 c, string what)
        {
            if (!c.InBounds(map))
                throw new VerbArgsException($"{what} = ({c.x},{c.z}) is outside the {map.Size.x}x{map.Size.z} map");
            return c;
        }

        public static List<object> Out(IntVec3 c) => new List<object> { (double)c.x, (double)c.z };
    }

    // Named places, persisted in the save (GameComponent ExposeData) so plans
    // reference "kitchen-door", not numbers, across save/load.
    public class LandmarkComponent : GameComponent
    {
        private Dictionary<string, IntVec3> landmarks = new Dictionary<string, IntVec3>();

        public LandmarkComponent(Game game)
        {
        }

        public static LandmarkComponent Current => Verse.Current.Game?.GetComponent<LandmarkComponent>();

        public bool TryGet(string name, out IntVec3 pos) => landmarks.TryGetValue(name, out pos);

        public void Set(string name, IntVec3 pos) => landmarks[name] = pos;

        public bool Remove(string name) => landmarks.Remove(name);

        public Dictionary<string, IntVec3> All => landmarks;

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref landmarks, "autoRimmerLandmarks", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && landmarks == null)
                landmarks = new Dictionary<string, IntVec3>();
        }
    }

    // The single source of crop rendering. ASCII grid, north (max z) at the top,
    // x/z rulers on all four edges, legend built only from glyphs present.
    //
    // Glyph policy: derived from live def properties (category, passability,
    // door/plant-ness), deterministic per bench version — the same def set the
    // dumped baseviz_catalog.json records (colors stay the catalog's business;
    // an ASCII channel has none). Layer priority per cell:
    // FOG > pawns > designations > things > zones > roof > terrain.
    //
    // Two 2.6 corrections to that policy:
    //
    // 1. RESERVED BAND. Pawn and fog glyphs own "?@$!&^;" and nothing derived
    //    from a def label may land there. 2.3 mapped items to label[0]
    //    lowercased and pawns to g/d/w, so `gold` collided with guest, `duster`
    //    with tame animal and `wood` with wild animal, and the legend merged
    //    them into one entry ("tame animal | duster (Muffalo)"). With a 32-mod
    //    catalog that is the common case, not the corner. UpperLetter and
    //    LowerLetter now push any label that would collide out of the band, so
    //    disjointness is enforced rather than assumed. Collisions BETWEEN
    //    things (Table/Tree both `T`) are still allowed and still disambiguated
    //    by the legend — only the pawn/fog band is reserved.
    //
    // 2. DESIGNATIONS OUTRANK THINGS. Almost every designation is ON a thing,
    //    so rendering the designation layer UNDER things (2.3) made it
    //    invisible exactly when it mattered: a wall marked for deconstruction
    //    drew `#` and never `*`. Designations are an overlay.
    //
    // Fog: `c.Fogged(map)` short-circuits the whole cell to `?`, so no detail
    // leaks out of ground the colony has never walked (DESIGN decisions log,
    // 2026-08-30). Fogged cells get their OWN glyph rather than being blanked —
    // the shape of the unexplored is information a player has too.
    public static class CropRenderer
    {
        // ODD on purpose. CellRect.CenteredOn(c, r) is (2r+1) on a side, so an
        // even cap made `map-view {radius:30}` — 61x61, and documented-legal at
        // SpatialVerbs.cs:31 — fail with bad-args every single time (2.6
        // blocker 4). 61 keeps every legal radius renderable.
        public const int MaxSide = 61;

        // Reserved for pawns and fog; def-derived glyphs are pushed out of it.
        private const string ReservedBand = "?@$!&^;";
        public const char FogGlyph = '?';

        public static readonly IReadOnlyList<string> DefaultLayers = new[] { "terrain", "things", "zones", "pawns" };

        public static Dictionary<string, object> Render(Map map, CellRect rect, ICollection<string> layers)
        {
            bool clipped = rect.minX < 0 || rect.minZ < 0 || rect.maxX >= map.Size.x || rect.maxZ >= map.Size.z;
            // A rect with NO overlap at all is a caller mistake, not a crop.
            // ClipInsideMap only clamps the two low edges, so a fully off-map
            // rect used to survive as ok:true with a negative width and an
            // origin outside the map (2.3 only ever tested overlapping edges).
            if (rect.maxX < 0 || rect.maxZ < 0 || rect.minX >= map.Size.x || rect.minZ >= map.Size.z)
                throw new VerbArgsException(
                    $"rect [{rect.minX},{rect.minZ},{rect.Width},{rect.Height}] lies entirely outside the {map.Size.x}x{map.Size.z} map");
            rect = rect.ClipInsideMap(map);
            if (rect.Width > MaxSide || rect.Height > MaxSide)
                throw new VerbArgsException($"viewport {rect.Width}x{rect.Height} exceeds the {MaxSide}x{MaxSide} cap");

            bool L(string name) => layers.Contains(name);
            var legend = new Dictionary<string, object>();
            var pawnNames = new Dictionary<char, List<string>>();
            var rows = new List<object>();

            for (int z = rect.maxZ; z >= rect.minZ; z--)
            {
                var sb = new StringBuilder(rect.Width);
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    char g = Cell(map, c, L, legend, pawnNames);
                    sb.Append(g);
                }
                rows.Add(sb.ToString());
            }

            foreach (var kv in pawnNames)
            {
                string key = kv.Key.ToString();
                string existing = legend.TryGetValue(key, out var v) ? (string)v : "";
                var names = kv.Value;
                string joined = string.Join(",", names.GetRange(0, Math.Min(names.Count, 8)));
                if (names.Count > 8) joined += ",+" + (names.Count - 8);
                legend[key] = existing + " (" + joined + ")";
            }

            return new Dictionary<string, object>
            {
                ["origin"] = new List<object> { (double)rect.minX, (double)rect.minZ },
                ["w"] = rect.Width,
                ["h"] = rect.Height,
                ["rows"] = rows, // rows[0] is z = origin_z + h - 1 (north at top)
                ["rulers"] = Rulers(rect),
                ["legend"] = legend,
                ["clipped"] = clipped,
            };
        }

        private static char Cell(Map map, IntVec3 c, Func<string, bool> L,
            Dictionary<string, object> legend, Dictionary<char, List<string>> pawnNames)
        {
            // Fog first and unconditionally: the player-facing surface hides
            // undiscovered cells, and no layer may leak detail through it.
            if (c.Fogged(map))
            {
                Note(legend, FogGlyph, "unexplored (fogged)");
                return FogGlyph;
            }
            if (L("pawns"))
            {
                var things = map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < things.Count; i++)
                {
                    if (!(things[i] is Pawn p)) continue;
                    char g = PawnGlyph(p, out string kind);
                    Note(legend, g, kind);
                    if (!pawnNames.TryGetValue(g, out var names)) pawnNames[g] = names = new List<string>();
                    names.Add(p.LabelShortCap.ToString());
                    return g;
                }
            }
            // Above things, not below: a designation is an overlay ON a thing.
            if (L("designations"))
            {
                var des = map.designationManager.AllDesignationsAt(c);
                foreach (var d in des) { Note(legend, '*', d.def.defName); return '*'; }
            }
            if (L("things"))
            {
                var things = map.thingGrid.ThingsListAtFast(c);
                Thing best = null;
                for (int i = 0; i < things.Count; i++)
                {
                    var t = things[i];
                    if (t is Pawn || t.def.category == ThingCategory.Filth
                        || t.def.category == ThingCategory.Mote || t.def.category == ThingCategory.Ethereal) continue;
                    if (best == null || Rank(t) > Rank(best)) best = t;
                }
                if (best != null)
                {
                    char g = ThingGlyph(best.def, best.Stuff);
                    Note(legend, g, best.def.label);
                    return g;
                }
            }
            if (L("zones"))
            {
                var zone = map.zoneManager.ZoneAt(c);
                if (zone is Zone_Stockpile) { Note(legend, '=', "stockpile: " + zone.label); return '='; }
                if (zone is Zone_Growing zg) { Note(legend, '"', "growing: " + zg.GetPlantDefToGrow()?.label); return '"'; }
            }
            if (L("roof") && c.Roofed(map))
            {
                Note(legend, ':', "roofed");
                return ':';
            }
            if (L("terrain"))
            {
                var terrain = map.terrainGrid.TerrainAt(c);
                char g = TerrainGlyph(terrain);
                Note(legend, g, terrain.label);
                return g;
            }
            return ' ';
        }

        private static int Rank(Thing t)
        {
            switch (t.def.category)
            {
                case ThingCategory.Building: return 3;
                case ThingCategory.Plant: return 1;
                case ThingCategory.Item: return 2;
                default: return 0;
            }
        }

        // Every glyph here is inside ReservedBand, so a pawn can never be
        // confused with an item whose label happens to start with the same
        // letter. Was g/d/w for guest/tame/wild in 2.3 — see the class comment.
        private static char PawnGlyph(Pawn p, out string kind)
        {
            if (p.IsPrisoner) { kind = "prisoner"; return '$'; }
            if (p.Faction == Faction.OfPlayer)
            {
                if (p.RaceProps.Humanlike) { kind = "colonist"; return '@'; }
                kind = "tame animal";
                return '^';
            }
            if (p.HostileTo(Faction.OfPlayer)) { kind = "hostile"; return '!'; }
            if (p.RaceProps.Humanlike) { kind = "neutral/guest"; return '&'; }
            kind = "wild animal";
            return ';';
        }

        private static char ThingGlyph(ThingDef def, ThingDef stuff)
        {
            if (def.IsDoor) return '+';
            if (def.category == ThingCategory.Building)
            {
                if (def.passability == Traversability.Impassable)
                    return def.building != null && def.building.isNaturalRock ? '%' : '#';
                return UpperLetter(def.label);
            }
            if (def.category == ThingCategory.Plant)
                return def.plant != null && def.plant.IsTree ? 'T' : 't';
            // A non-Pawn-class thing of Pawn category (rare; the things layer
            // filters real pawns out above). `?` is the fog glyph now, so it
            // takes the ordinary item letter.
            return LowerLetter(def.label);
        }

        private static char TerrainGlyph(TerrainDef terrain)
        {
            if (terrain.IsWater) return '~';
            if (!terrain.layerable && terrain.fertility <= 0 && terrain.passability == Traversability.Impassable) return '#';
            if (terrain.layerable || terrain.bridge) return '_'; // built floor / bridge
            if (terrain.fertility >= 1f) return ','; // rich/fertile ground
            return '.';
        }

        // Label-derived glyphs, forced out of the pawn/fog band. Nothing in a
        // vanilla catalog starts with one of those characters, but a modded
        // label can, and the collision would be silent.
        private static char UpperLetter(string label)
        {
            if (string.IsNullOrEmpty(label)) return 'B';
            char g = char.ToUpperInvariant(label[0]);
            return ReservedBand.IndexOf(g) >= 0 ? 'B' : g;
        }

        private static char LowerLetter(string label)
        {
            if (string.IsNullOrEmpty(label)) return 'i';
            char g = char.ToLowerInvariant(label[0]);
            return ReservedBand.IndexOf(g) >= 0 ? 'i' : g;
        }

        private static void Note(Dictionary<string, object> legend, char g, string label)
        {
            string key = g.ToString();
            if (legend.TryGetValue(key, out var v))
            {
                string s = (string)v;
                if (label != null && !s.Contains(label) && s.Length < 120) legend[key] = s + " | " + label;
            }
            else
            {
                legend[key] = label ?? "?";
            }
        }

        // x ruler (top/bottom) and z ruler (left/right) as strings the CLI can
        // print verbatim around the rows: tens line + units line for x, and the
        // z of the first and last row (rows run north->south).
        private static Dictionary<string, object> Rulers(CellRect rect)
        {
            var tens = new StringBuilder(rect.Width);
            var units = new StringBuilder(rect.Width);
            for (int x = rect.minX; x <= rect.maxX; x++)
            {
                tens.Append(x % 10 == 0 ? ((x / 10) % 10).ToString() : " ");
                units.Append((x % 10).ToString());
            }
            return new Dictionary<string, object>
            {
                ["x_tens"] = tens.ToString(),
                ["x_units"] = units.ToString(),
                ["z_top"] = rect.maxZ,
                ["z_bottom"] = rect.minZ,
            };
        }
    }
}
