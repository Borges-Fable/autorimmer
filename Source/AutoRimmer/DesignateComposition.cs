using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ======================================================= git-bug 855117a ==
    // WHAT WAS ACTUALLY DESIGNATED — not how many, WHICH.
    //
    //     designate {type:"mine", rect:[131,116,6,10], max_cells:600}
    //       ->  accepted 14 of 60
    //
    // The intent was compacted steel. The result was fourteen cells of whatever
    // rock was exposed on that rect, and there was no field in the envelope
    // that could tell one from the other. `map-view`'s `%` glyph collapses
    // `sandstone | marble | compacted steel` into one character, so the rect
    // was chosen off a view that cannot distinguish the thing worth mining from
    // the two things that are not — collision is the documented cost of a
    // fixed-width channel and `map-view` says so, but nothing anywhere said
    // **map-view is the wrong instrument for siting a mine designation**.
    //
    // The colony can only decide this for itself if it can see it, and on a
    // mountain base mining rock IS the point. So the answer is a REPORT, never
    // a refusal: say what was designated.
    //
    // ------------------------- THE SUBJECT OF A CELL -------------------------
    // A cell designation is about whatever the designator's own gate looked at,
    // so the rollup asks the same question in the same order:
    //
    //   1. `IntVec3.GetFirstMineable(map)` — `Designator_Mine.CanDesignateCell`
    //      and `Designator_MineVein.CanDesignateCell` both gate on it, so for
    //      mine and mine-vein this is always the subject and always non-null.
    //   2. else `IntVec3.GetEdifice(map)` — deconstruct, uninstall, fill-in,
    //      smooth-wall.
    //   3. else `map.terrainGrid.TerrainAt(c)` — remove-floor, smooth-floor,
    //      remove-foundation, whose subject is the ground itself.
    //
    // All three are plain grid reads: `GetFirstMineable` scans
    // `thingGrid.ThingsListAt`, `GetEdifice` indexes `EdificeGrid`, and
    // `TerrainGrid.TerrainAt` indexes three arrays. Nothing lazily built,
    // nothing cached, nothing written. `by` on each row names which of the
    // three answered, because "the marble WALL" and "the marble FLOOR" are
    // different corrections.
    //
    // A THING designation (hunt, chop, cut, haul, slaughter, tame, strip …)
    // rolls up by `thing.def.defName` directly, and that is the same fix on the
    // other axis: `designate hunt --filter` over a herd now says
    // `Deer: 4, Hare: 2` instead of `accepted: 6`.
    //
    // ------------------------- WHAT THE ORE PRODUCES ------------------------
    // Where the game publishes it, each row carries the yield the cell will
    // actually drop — `ThingDef.building.mineableThing` and `mineableYield`,
    // plus `EffectiveMineableYield`, which is `mineableYield *
    // Find.Storyteller.difficulty.mineYieldFactor` rounded. BOTH are published:
    // the raw number is the def's, the effective one is this game's, and on
    // anything but Rough they differ. `vein_mineable` rides along because it is
    // exactly the predicate that decides whether `mine-vein` will take the cell
    // at all (`Designator_MineVein.CanDesignateThing`:
    // `!t.def.mineable || !t.def.building.veinMineable` -> false).
    //
    // ---------------------- WHY IT IS SCORED ON `Landed` --------------------
    // Not on `accepted`. `Designator_MineVein.DesignateSingleCell` flood-fills,
    // so one accepted cell can paint forty and every later cell in the drag
    // then comes back already-designated and REJECTED. A composition keyed on
    // `accepted` would describe one cell of a forty-cell job. See
    // `DesignateEngine.Landed` — the delta of the designation's own cell set.
    // =========================================================================
    internal static class DesignateComposition
    {
        // Rows are capped like every list in this spec; `composition_more` says
        // what the cap hid and the TOTAL is complete.
        public const int RowCap = 24;

        private sealed class Row
        {
            public string Def;
            public string Label;
            public string By;
            public int Count;
            public ThingDef Thing;      // the def, when the subject was a thing
            public TerrainDef Terrain;
        }

        public static List<object> Build(Map map, DesignateEngine.Landed landed, out int more,
            out int total)
        {
            more = 0;
            int seen = 0;
            var order = new List<string>();
            var byKey = new Dictionary<string, Row>(StringComparer.Ordinal);

            void Bump(string key, string label, string by, ThingDef td, TerrainDef ter)
            {
                if (key == null) key = "(nothing)";
                if (!byKey.TryGetValue(key, out var row))
                {
                    row = new Row { Def = key, Label = label, By = by, Thing = td, Terrain = ter };
                    byKey[key] = row;
                    order.Add(key);
                }
                row.Count++;
                seen++;
            }

            if (landed.IsThings)
            {
                for (int i = 0; i < landed.Things.Count; i++)
                {
                    var t = landed.Things[i];
                    if (t?.def == null) { Bump(null, null, "thing", null, null); continue; }
                    Bump(t.def.defName, WorldSafe.Safe(() => t.def.label), "thing", t.def, null);
                }
            }
            else
            {
                for (int i = 0; i < landed.Cells.Count; i++)
                {
                    var c = landed.Cells[i];
                    if (!c.IsValid || !c.InBounds(map)) { Bump(null, null, "out-of-bounds", null, null); continue; }
                    ThingDef td = null;
                    string by = null;
                    try
                    {
                        var mineable = c.GetFirstMineable(map);
                        if (mineable?.def != null) { td = mineable.def; by = "mineable"; }
                        else
                        {
                            var ed = c.GetEdifice(map);
                            if (ed?.def != null) { td = ed.def; by = "edifice"; }
                        }
                    }
                    catch { }
                    if (td != null)
                    {
                        Bump(td.defName, WorldSafe.Safe(() => td.label), by, td, null);
                        continue;
                    }
                    TerrainDef ter = null;
                    try { ter = map.terrainGrid.TerrainAt(c); } catch { }
                    if (ter != null) Bump(ter.defName, WorldSafe.Safe(() => ter.label), "terrain", null, ter);
                    else Bump(null, null, "empty", null, null);
                }
            }

            total = seen;

            // Largest first, def name as the tie-break so the order is stable
            // run to run — the same rule WorkCoverage.EssentialTypes uses.
            order.Sort((a, b) =>
            {
                int c = byKey[b].Count.CompareTo(byKey[a].Count);
                return c != 0 ? c : string.CompareOrdinal(a, b);
            });

            var outp = new List<object>();
            for (int i = 0; i < order.Count; i++)
            {
                if (outp.Count >= RowCap) { more = order.Count - outp.Count; break; }
                var r = byKey[order[i]];
                var d = new Dictionary<string, object>
                {
                    ["def"] = r.Def,
                    ["label"] = r.Label,
                    ["by"] = r.By,
                    ["count"] = r.Count,
                };
                Yield(r.Thing, d);
                outp.Add(d);
            }
            return outp;
        }

        // The ore half. Absent, not null-filled, on a def that publishes none —
        // `building` is null for most things and `mineableThing` is null for
        // plain rock, and a row of nulls would read as "this produces nothing"
        // rather than "this is not a mineable".
        private static void Yield(ThingDef td, Dictionary<string, object> d)
        {
            BuildingProperties b = null;
            try { b = td?.building; } catch { }
            if (b == null) return;
            try { d["mineable"] = td.mineable; } catch { }
            try { d["vein_mineable"] = b.veinMineable; } catch { }
            try { d["resource_rock"] = b.isResourceRock; } catch { }
            try { d["natural_rock"] = b.isNaturalRock; } catch { }
            if (b.mineableThing == null) return;
            d["mineable_thing"] = b.mineableThing.defName;
            try { d["mineable_yield"] = b.mineableYield; } catch { }
            // The def's number times this game's difficulty factor
            // (BuildingProperties.EffectiveMineableYield). Both, because the
            // raw one is what a wiki says and the effective one is what the
            // colony gets.
            try { d["yield_effective"] = b.EffectiveMineableYield; } catch { }
        }

        // ------------------------------------------------------------------
        // WHAT THIS DESIGNATION REPLACED
        // ------------------------------------------------------------------
        // `designate mine-vein` over ground already marked `mine` REPLACES the
        // Mine designations silently: `Designator_MineVein.FloodFillDesignations`
        // does `map.designationManager.TryRemoveDesignation(c,
        // DesignationDefOf.Mine)` on every cell it paints. `designate mine`
        // does the same to `SmoothWall` in its own `DesignateSingleCell`. The
        // MineVein count goes up, the Mine count goes down, and until now
        // nothing said the second half — so a caller reading
        // `designations_now` for `Mine` after a `mine-vein` call would see it
        // fall for no published reason.
        public static List<object> Replaced(Map map, DesignationDef[] defs,
            Dictionary<DesignationDef, HashSet<IntVec3>> before, string why)
        {
            if (defs == null || defs.Length == 0 || before == null) return null;
            var outp = new List<object>();
            for (int i = 0; i < defs.Length; i++)
            {
                var def = defs[i];
                if (def == null || !before.TryGetValue(def, out var was) || was == null) continue;
                var now = DesignateEngine.CellSnapshot(map, def);
                if (now == null) continue;
                var gone = new List<IntVec3>();
                foreach (var c in was) if (!now.Contains(c)) gone.Add(c);
                if (gone.Count == 0) continue;
                outp.Add(new Dictionary<string, object>
                {
                    ["designation"] = def.defName,
                    ["removed"] = gone.Count,
                    ["cells"] = DesignateEngine.CellsOut(gone, out int cellMore),
                    ["cells_more"] = cellMore,
                    ["why"] = why,
                });
            }
            return outp.Count > 0 ? outp : null;
        }
    }
}
