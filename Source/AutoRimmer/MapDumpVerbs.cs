using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // `map-dump` (spec 2.5): the per-cell record the PNG channel renders from.
    // Read-only. The game side does NO image work — DESIGN's invariant for this
    // spec is "no game-side image rendering; render is a pure function of dump
    // + catalog" — so this verb's whole job is to publish what is in a rect,
    // losslessly enough that `rwa render` can draw it and that the same dump
    // always draws the same PNG.
    //
    // WHY THIS IS NOT map-view, AND NOT A WRAPPER OVER IT
    // ---------------------------------------------------
    // CropRenderer (2.3) answers a different question and cannot be reused:
    //   * it caps at MaxSide 61, so any base-sized rect is a guaranteed
    //     bad-args, let alone a whole map;
    //   * it collapses FOG > pawns > designations > things > zones > roof >
    //     terrain to ONE char per cell, so terrain-under-thing-under-zone is
    //     unrecoverable — and a render needs all three at once, because the
    //     floor is the fill, the thing is the block on top of it and the zone
    //     is the tint around both.
    // So this emits one PLANE PER LAYER, and the two channels stay independent
    // on purpose: 2.5 exists to be a second opinion on 2.3's ASCII, and a
    // second opinion computed from the same collapsed cell would not be one.
    //
    // THE ALPHABET IS DIFFERENT ON PURPOSE (git-bug f7b6207 comment #4)
    // ----------------------------------------------------------------
    // map-view says `#` for a wall; the PNG says a grey block labelled `WA`.
    // Those are two alphabets for two media — one ASCII char in a fixed-width
    // grid where collisions are fatal, versus a coloured sized cell where
    // colour disambiguates and a 2-char token is affordable. They are NOT
    // reconciled and must never be compared cell-for-cell. Hence the `channel`
    // block in the response: a consumer keys on `alphabet` and knows which
    // symbol system it is holding. No third alphabet is introduced here —
    // colours and glyphs both resolve from the def catalog, which is
    // baseviz's own scheme.
    //
    // FOG IS RESPECTED (DESIGN decisions log, 2026-08-30)
    // ---------------------------------------------------
    // This is a player verb, so a fogged cell reports index 0 — nothing — in
    // EVERY plane, and the `fog` plane marks it. The agent must not learn the
    // shape of unexplored ground by rendering it. That is the same rule
    // map-view, find-rect, nearest and room-at follow, and it is why the PNG
    // is a fair second check rather than a privileged one: both channels see
    // exactly what the colony has seen. `dev:*` is exempt from the rule and
    // nothing here is dev:*.
    //
    // MUTATION HAZARDS ROUTED AROUND (WorldSafe's catalogue)
    // ------------------------------------------------------
    // A per-cell walk touches zones on every cell, which is precisely where
    // the observer traps are:
    //   * Zone.Cells (class R) shuffles a scribed list off the shared RNG on
    //     first read. Not called at all here — ZoneAt is a grid lookup.
    //   * Zone_Growing.PlantDefToGrow / GetPlantDefToGrow (class A) ASSIGNS and
    //     SCRIBES a default onto a never-configured zone, so asking what a zone
    //     grows writes the save. WorldSafe.PlantToGrow is the guarded route and
    //     is what this uses; map-view was fixed for exactly this.
    //   * Room.Role is lazy (a full room analysis) but idempotent and RNG-free.
    //     It is read ONCE PER DISTINCT ROOM here, not once per cell.
    public static class MapDumpVerbs
    {
        // The budget, stated rather than inherited. There is no response size
        // cap anywhere in this protocol — MiniJson guards only recursion depth,
        // and 2.6's "digest budget" is a set of per-verb constants in
        // DigestVerb, not a transport rule — so a dump verb that did not bound
        // itself would be the first thing here able to emit megabytes.
        //
        // 65536 cells is 256x256: any vanilla map (250x250 at the largest
        // preset) fits whole, and nothing larger can be asked for by accident.
        //
        // What that costs, MEASURED at 250x250 rather than asserted, because a
        // budget nobody has measured is not a budget:
        //   * a realistic map — terrain in patches, the thing/zone/room planes
        //     mostly empty — run-length-encodes to single-digit KB;
        //   * the pathological worst case, every cell differing from its
        //     neighbour in every plane, is ~940KB of planes / ~950KB of
        //     envelope. That is not a shape a RimWorld map can actually take,
        //     but it is the real ceiling and it is an order of magnitude under
        //     the ~4MB a per-cell JSON record would cost at ANY entropy.
        // So the honest claim is: single-digit KB in practice, bounded at ~1MB
        // by construction. Still far and away the largest response this
        // protocol emits — every other verb is 1-2KB to low tens of KB — which
        // is exactly why the cap is stated here instead of inherited.
        public const int MaxCells = 65536;
        public const int MaxSide = 300;

        // Bounds the glyph-label list, which is per-BUILDING rather than
        // per-cell and so is small in practice: a built base runs to a few
        // hundred. A pathological rect (a stockpile of 5000 items) stops here
        // and says so rather than doubling the payload.
        public const int LabelCap = 2000;

        public static readonly IReadOnlyList<string> DefaultLayers =
            new[] { "terrain", "things", "zones", "rooms", "roof", "pawns" };

        [Verb("map-dump")]
        public static object MapDump(VerbContext ctx)
        {
            var map = Find.CurrentMap ?? throw new VerbArgsException("no current map");

            CellRect rect;
            if (ctx.Args.Has("rect"))
            {
                if (!(ctx.Args.Raw("rect") is List<object> r) || r.Count != 4
                    || !(r[0] is double rx) || !(r[1] is double rz)
                    || !(r[2] is double rw) || !(r[3] is double rh))
                    throw new VerbArgsException("rect must be [x,z,w,h]");
                rect = new CellRect((int)rx, (int)rz, Math.Max(1, (int)rw), Math.Max(1, (int)rh));
            }
            else if (ctx.Args.Has("around"))
            {
                var around = Positions.Resolve(map, ctx.Args.Raw("around"));
                int radius = ctx.Args.Int("radius", 30);
                if (radius < 1 || radius > (MaxSide - 1) / 2)
                    throw new VerbArgsException($"radius must be 1..{(MaxSide - 1) / 2}");
                rect = CellRect.CenteredOn(around, radius);
            }
            else if (ctx.Args.Bool("whole_map", false))
            {
                rect = new CellRect(0, 0, map.Size.x, map.Size.z);
            }
            else
            {
                throw new VerbArgsException("map-dump needs 'rect', 'around' or whole_map:true");
            }

            // Same clipping contract as map-view: a rect that overlaps the map
            // edge is cropped and says so; one that misses entirely is a caller
            // mistake, because clipping cannot rescue it into anything useful.
            bool clipped = rect.minX < 0 || rect.minZ < 0
                           || rect.maxX >= map.Size.x || rect.maxZ >= map.Size.z;
            if (rect.maxX < 0 || rect.maxZ < 0 || rect.minX >= map.Size.x || rect.minZ >= map.Size.z)
                throw new VerbArgsException(
                    $"rect [{rect.minX},{rect.minZ},{rect.Width},{rect.Height}] lies entirely outside the {map.Size.x}x{map.Size.z} map");
            rect = rect.ClipInsideMap(map);

            if (rect.Width > MaxSide || rect.Height > MaxSide)
                throw new VerbArgsException(
                    $"rect {rect.Width}x{rect.Height} exceeds the {MaxSide} cap on a side");
            long cells = (long)rect.Width * rect.Height;
            if (cells > MaxCells)
                throw new VerbArgsException(
                    $"rect {rect.Width}x{rect.Height} is {cells} cells, over the {MaxCells}-cell budget; "
                    + "ask for a sub-rect (the whole map fits, so this means the rect is not a map rect)");

            var layers = ctx.Args.Has("layers")
                ? new HashSet<string>(ctx.Args.StrList("layers"))
                : new HashSet<string>(DefaultLayers);
            bool L(string n) => layers.Contains(n);

            var terrain = new Plane(L("terrain"));
            var things = new Plane(L("things"));
            var zones = new Plane(L("zones"));
            var rooms = new Plane(L("rooms"));
            var roof = new Plane(L("roof"));
            var pawns = new Plane(L("pawns"));
            var fog = new Plane(true);

            var terrainPal = new Palette();
            var thingPal = new Palette();
            var zonePal = new Palette();
            var roomPal = new Palette();
            var roofPal = new Palette();
            var pawnPal = new Palette();

            // Role is lazy; one read per room, not per cell.
            var roleCache = new Dictionary<int, Room>();
            // One label per building INSTANCE, keyed on thingIDNumber so a
            // multi-cell building is labelled once rather than once per cell.
            var seenBuildings = new HashSet<int>();
            var labels = new List<object>();
            bool labelsCapped = false;

            int foggedCells = 0;

            // North-up, matching map-view: the first cell emitted is the
            // north-west corner, so rows[0] is z = maxZ. The renderer flips to
            // screen coordinates in exactly one place and nowhere else.
            for (int z = rect.maxZ; z >= rect.minZ; z--)
            {
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    var c = new IntVec3(x, 0, z);

                    if (c.Fogged(map))
                    {
                        foggedCells++;
                        fog.Add(1);
                        terrain.Add(0); things.Add(0); zones.Add(0);
                        rooms.Add(0); roof.Add(0); pawns.Add(0);
                        continue;
                    }
                    fog.Add(0);

                    if (terrain.On)
                    {
                        var t = map.terrainGrid.TerrainAt(c);
                        terrain.Add(t == null ? 0 : terrainPal.Index(
                            t.defName, () => new Dictionary<string, object>
                            {
                                ["def"] = t.defName,
                                ["label"] = t.label ?? t.defName,
                            }));
                    }

                    if (things.On)
                    {
                        var best = BestThing(map, c);
                        if (best == null) things.Add(0);
                        else
                        {
                            string stuff = best.Stuff?.defName;
                            string key = best.def.defName + "|" + (stuff ?? "");
                            var def = best.def;
                            int idx = thingPal.Index(key, () => new Dictionary<string, object>
                            {
                                ["def"] = def.defName,
                                ["stuff"] = stuff,
                                ["label"] = def.label ?? def.defName,
                                ["category"] = def.category.ToString(),
                                ["door"] = def.IsDoor,
                                ["size"] = new List<object> { (double)def.size.x, (double)def.size.z },
                                // STRUCTURE. The first render drew every thing
                                // the same way — an inset block in its catalog
                                // colour — and a reader could not trace a
                                // single wall, because an inset block leaves a
                                // gap on all four sides and two adjacent wall
                                // cells therefore do NOT touch. Walls have to
                                // read as one continuous mass, and constructed
                                // wall has to be distinguishable from natural
                                // rock (both are grey impassable buildings), so
                                // the renderer needs these two facts and cannot
                                // derive either from category or colour.
                                // Same test Spatial.cs ThingGlyph uses for
                                // '%' vs '#'.
                                ["impassable"] = def.passability == Traversability.Impassable,
                                ["natural_rock"] = def.building != null && def.building.isNaturalRock,
                            });
                            things.Add(idx);

                            // The glyph anchor. Only for buildings: labelling
                            // every item stack would bury the base under
                            // two-letter tokens, and items are legible from
                            // colour alone at this scale.
                            //
                            // And NOT for impassable buildings, which is the
                            // fix for the first render's worst symptom. Every
                            // wall cell is its own Thing with its own id, so a
                            // stone base emitted thousands of `WA` anchors,
                            // blew LabelCap, and got truncated to an arbitrary
                            // scatter — "sparse WA labels in ones and twos"
                            // that a reader could not tell from an opening.
                            // Structure is read by SHAPE, not by token: walls
                            // are drawn as a continuous mass and carry no
                            // label at all. Doors keep theirs (passability is
                            // PassThroughOnly, not Impassable) — the one thing
                            // the first render got right was doors, and this
                            // does not touch them.
                            if (def.category == ThingCategory.Building
                                && def.passability != Traversability.Impassable
                                && seenBuildings.Add(best.thingIDNumber))
                            {
                                if (labels.Count < LabelCap)
                                {
                                    labels.Add(new Dictionary<string, object>
                                    {
                                        ["p"] = idx,
                                        ["at"] = Positions.Out(best.Position),
                                        ["size"] = new List<object> { (double)def.size.x, (double)def.size.z },
                                        // ToStringWord, NOT ToStringHuman.
                                        // Verse/Rot4.ToStringHuman is
                                        // "North".Translate() — a LOCALIZED
                                        // string — while ToStringWord is the
                                        // invariant word. This is a data field
                                        // the renderer keys on
                                        // (baseviz/render.py, occupied_rect),
                                        // and on a non-English install the
                                        // comparison silently missed, so every
                                        // rotated building's footprint was
                                        // derived without the axis swap. Same
                                        // class as 00a1be7: read by field,
                                        // never through the game's sentence
                                        // builder. git-bug 2a7c064.
                                        ["rot"] = best.Rotation.ToStringWord(),
                                    });
                                }
                                else labelsCapped = true;
                            }
                        }
                    }

                    if (zones.On)
                    {
                        var zone = map.zoneManager.ZoneAt(c);
                        if (zone == null) zones.Add(0);
                        else
                        {
                            var z2 = zone;
                            zones.Add(zonePal.Index("z" + zone.ID, () =>
                            {
                                var d = new Dictionary<string, object>
                                {
                                    ["id"] = z2.ID,
                                    ["label"] = z2.label,
                                    ["kind"] = z2 is Zone_Stockpile ? "stockpile"
                                             : z2 is Zone_Growing ? "growing" : "zone",
                                };
                                // NEVER GetPlantDefToGrow: the getter assigns
                                // and scribes a default onto a never-configured
                                // zone. WorldSafe.PlantToGrow reads the backing
                                // field, and "unconfigured" is a real answer.
                                if (z2 is Zone_Growing zg)
                                    d["plant"] = WorldSafe.PlantToGrow(zg)?.label;
                                return d;
                            }));
                        }
                    }

                    if (rooms.On)
                    {
                        var room = c.GetRoom(map);
                        if (room == null || WorldSafe.RoomHidden(room)) rooms.Add(0);
                        else
                        {
                            roleCache[room.ID] = room;
                            var r2 = room;
                            rooms.Add(roomPal.Index("r" + room.ID, () => new Dictionary<string, object>
                            {
                                ["id"] = r2.ID,
                                ["outdoors"] = r2.PsychologicallyOutdoors,
                                ["cells"] = r2.CellCount,
                                // A door cell is its own single-cell Room
                                // (Verse/Room.cs IsDoorway -> the district is a
                                // doorway). The first render enumerated all six
                                // of them in its ROOMS legend as "NONE / 1
                                // CELLS" and a reader asked to count rooms got
                                // six confident wrong answers pointing at
                                // invisible 1-cell swatches. The renderer needs
                                // to be able to tell a doorway from a room, so
                                // the dump says which it is rather than making
                                // the client guess from CellCount == 1.
                                ["doorway"] = r2.IsDoorway,
                                // Verse/Room.cs ProperRoom: does not touch the
                                // map edge and has a Normal region — i.e. what
                                // a player would call an enclosed room, as
                                // opposed to open ground that happens to be
                                // regioned.
                                ["proper"] = r2.ProperRoom,
                            }));
                        }
                    }

                    if (roof.On)
                    {
                        var rd = map.roofGrid.RoofAt(c);
                        roof.Add(rd == null ? 0 : roofPal.Index(rd.defName,
                            () => new Dictionary<string, object>
                            {
                                ["def"] = rd.defName,
                                ["label"] = rd.label ?? rd.defName,
                                // Verse/RoofDef.cs: the field is isThickRoof.
                                // Thick roof is the mountain overhead that
                                // blocks drop pods and hides infestations, so
                                // it is worth distinguishing in the render.
                                ["thick"] = rd.isThickRoof,
                                ["natural"] = rd.isNatural,
                            }));
                    }

                    if (pawns.On)
                    {
                        var p = FirstPawn(map, c);
                        if (p == null) pawns.Add(0);
                        else
                        {
                            var p2 = p;
                            pawns.Add(pawnPal.Index("p" + p.thingIDNumber, () => new Dictionary<string, object>
                            {
                                ["id"] = p2.thingIDNumber,
                                ["name"] = p2.LabelShortCap.ToString(),
                                ["kind"] = PawnKind(p2),
                            }));
                        }
                    }
                }
            }

            // Role is read here, once per distinct room, so a rect crossing a
            // 40-cell bedroom triggers one room analysis rather than forty.
            foreach (var kv in roleCache)
            {
                var entry = roomPal.Get("r" + kv.Key);
                if (entry != null)
                {
                    try { entry["role"] = kv.Value.Role?.label; }
                    catch { entry["role"] = null; }
                }
            }

            var planes = new Dictionary<string, object>();
            var runs = new Dictionary<string, object>();
            void Emit(string name, Plane p)
            {
                if (!p.On) return;
                planes[name] = p.Encode();
                runs[name] = p.Runs;
            }
            Emit("terrain", terrain);
            Emit("things", things);
            Emit("zones", zones);
            Emit("rooms", rooms);
            Emit("roof", roof);
            Emit("pawns", pawns);
            Emit("fog", fog);

            var palettes = new Dictionary<string, object>();
            if (terrain.On) palettes["terrain"] = terrainPal.Out();
            if (things.On) palettes["things"] = thingPal.Out();
            if (zones.On) palettes["zones"] = zonePal.Out();
            if (rooms.On) palettes["rooms"] = roomPal.Out();
            if (roof.On) palettes["roof"] = roofPal.Out();
            if (pawns.On) palettes["pawns"] = pawnPal.Out();

            var data = new Dictionary<string, object>
            {
                // The channel's identity. 2.5's whole point is being an
                // INDEPENDENT second read of the same map, so a consumer has to
                // be able to tell which symbol system it is holding — 4.2 and
                // 5.1 must never compare a glyph from here against one from
                // map-view. See this file's header.
                ["channel"] = new Dictionary<string, object>
                {
                    ["name"] = "map-dump",
                    ["alphabet"] = "baseviz-catalog/1",
                    ["distinct_from"] = "map-view/ascii-1",
                    ["note"] = "colours and 2-char glyphs resolve from the def catalog "
                             + "(catalog-dump); this is NOT map-view's single-char ASCII "
                             + "band and the two are not comparable cell-for-cell",
                },
                ["origin"] = new List<object> { (double)rect.minX, (double)rect.minZ },
                ["w"] = rect.Width,
                ["h"] = rect.Height,
                ["north_up"] = true,
                ["clipped"] = clipped,
                ["map"] = new Dictionary<string, object>
                {
                    ["w"] = map.Size.x,
                    ["h"] = map.Size.z,
                },
                ["cells"] = rect.Width * rect.Height,
                ["fogged_cells"] = foggedCells,
                ["fog_respected"] = true,
                ["encoding"] = "rle-v1",
                ["palettes"] = palettes,
                ["planes"] = planes,
                ["runs"] = runs,
                ["labels"] = labels,
            };
            if (labelsCapped) data["labels_capped"] = LabelCap;
            return data;
        }

        // The same priority CropRenderer.Rank uses (building > item > plant),
        // and the same exclusions. Deliberately identical: the two channels
        // disagree about how to NAME what is in a cell, but they must not
        // disagree about WHICH thing is in it, or the second opinion stops
        // being about the same map.
        private static Thing BestThing(Map map, IntVec3 c)
        {
            var list = map.thingGrid.ThingsListAtFast(c);
            Thing best = null;
            int bestRank = -1;
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (t is Pawn) continue;
                var cat = t.def.category;
                if (cat == ThingCategory.Filth || cat == ThingCategory.Mote
                    || cat == ThingCategory.Ethereal) continue;
                int rank = cat == ThingCategory.Building ? 3
                         : cat == ThingCategory.Item ? 2
                         : cat == ThingCategory.Plant ? 1 : 0;
                if (rank > bestRank) { best = t; bestRank = rank; }
            }
            return best;
        }

        private static Pawn FirstPawn(Map map, IntVec3 c)
        {
            var list = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < list.Count; i++)
                if (list[i] is Pawn p) return p;
            return null;
        }

        // The KIND names, not the ASCII band. 2.6 owns `? @ $ ! & ^ ;` for the
        // text channel; this channel names the same seven categories in words
        // and lets the renderer choose its own marks, which is what keeping the
        // two alphabets separate means in practice.
        private static string PawnKind(Pawn p)
        {
            if (p.IsPrisoner) return "prisoner";
            if (p.Faction == Faction.OfPlayer)
                return p.RaceProps.Humanlike ? "colonist" : "tame animal";
            if (p.HostileTo(Faction.OfPlayer)) return "hostile";
            return p.RaceProps.Humanlike ? "neutral/guest" : "wild animal";
        }

        // ------------------------------------------------------------------
        // A palette plus a run-length-encoded index plane.
        //
        // Encoding is "rle-v1": comma-separated tokens over the cells in
        // north-up row-major order, each either a bare palette index (one cell)
        // or `count:index`. Runs cross row boundaries — the decoder knows `w`
        // and reshapes — because a wall running the width of a base should cost
        // one token, not one per row.
        // ------------------------------------------------------------------
        private sealed class Plane
        {
            public readonly bool On;
            private readonly StringBuilder sb = new StringBuilder();
            private int cur = -1, count;
            public int Runs { get; private set; }

            public Plane(bool on) { On = on; }

            public void Add(int index)
            {
                if (!On) return;
                if (index == cur) { count++; return; }
                Flush();
                cur = index;
                count = 1;
            }

            private void Flush()
            {
                if (count == 0) return;
                if (sb.Length > 0) sb.Append(',');
                if (count == 1) sb.Append(cur);
                else sb.Append(count).Append(':').Append(cur);
                Runs++;
                count = 0;
            }

            public string Encode() { Flush(); return sb.ToString(); }
        }

        private sealed class Palette
        {
            // Index 0 is reserved for "nothing here" in every plane, so a
            // fogged cell and an empty one encode identically and the fog plane
            // is the only thing that distinguishes them.
            private readonly Dictionary<string, int> byKey = new Dictionary<string, int>();
            private readonly List<Dictionary<string, object>> entries =
                new List<Dictionary<string, object>>();

            public int Index(string key, Func<Dictionary<string, object>> make)
            {
                if (byKey.TryGetValue(key, out int i)) return i;
                entries.Add(make());
                i = entries.Count; // 1-based; 0 means none
                byKey[key] = i;
                return i;
            }

            public Dictionary<string, object> Get(string key)
                => byKey.TryGetValue(key, out int i) ? entries[i - 1] : null;

            public List<object> Out()
            {
                var list = new List<object> { null };
                foreach (var e in entries) list.Add(e);
                return list;
            }
        }
    }
}
