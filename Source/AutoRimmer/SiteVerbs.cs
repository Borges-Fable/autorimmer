using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // ============================================================ siting ====
    // `site-survey` (git-bug c718e4a). A READ, and the answer to Evan's
    // requirement verbatim: **"the agent looks at an area 2-3x bigger than
    // whatever they're building."**
    //
    // WHY IT EXISTS. On bench 20260901T121508 `find-rect {w:3,h:2}` approved a
    // box and `dev:spawn-thing` then refused it: "Interaction spot is blocked by
    // granite." The granite was one row SOUTH of the box. `SpatialVerbs.CheckRect`
    // walks the rect's own cells and takes no def, so it cannot know that a
    // research bench needs a standable cell outside its footprint
    // (`interactionCellOffset (0,0,-1)`); the game's gate does know, because
    // both `GenSpawn.CanSpawnAt` and `GenConstruct.CanPlaceBlueprintAt` call
    // `GenConstruct.InteractionCellStandable`. The agent asked "is this box
    // clear" and was told yes; the thing that refused was outside the box.
    //
    // THREE TIERS, and only two of them are gates:
    //
    //   footprint   — the cells the thing occupies. The game's own walk.
    //   interaction — the cells a pawn must stand on. Standable and free of
    //                 unstandable blueprints; exclusive against OTHER things'
    //                 interaction cells; a chair is fine.
    //   margin      — NOT a gate. Computed facts over the footprint expanded by
    //                 `margin` cells per side, which is where "2-3x bigger"
    //                 lives: what is standable, what is fogged, where the doors
    //                 are, and whether a colonist can actually reach the
    //                 interaction cell.
    //
    // ONE SOURCE OF TRUTH, THREE RENDERINGS (DESIGN, 2026-09-01). The structured
    // survey is the CONTRACT — verbs act on it, suites prove it by shape. The
    // ASCII crop rides in the same result because the agent reads it mid-loop
    // with no round trip, in the alphabet `map-view` already uses. The PNG stays
    // CLI-side: 2.5's invariant is "no game-side image rendering", so `rwa
    // render` gains an overlay input that is exactly these tiers. No image code
    // in the mod.
    //
    // READ-ONLY throughout. The two non-obvious calls are `BuildableDef.PlaceWorkers`
    // (a lazy-init getter, accepted — see SiteGate) and `Room.Role`, which this
    // verb does not touch at all.
    public static class SiteVerbs
    {
        // Per-cell rejection tokens. An agent branches on these, so they are
        // tokens and not sentences; the game's own sentence is the VERDICT's
        // job, which is where it appears verbatim.
        //
        // The first four are `SpatialVerbs.CheckRect`'s existing tally keys,
        // reused deliberately so `find-rect`'s `rejected` tree and a survey's
        // per-cell `reason` are one vocabulary rather than two spellings of the
        // same facts.
        public const string WhyOutOfBounds = "out-of-bounds";
        public const string WhyFogged = "fogged";
        public const string WhyTerrain = "terrain-not-buildable";
        public const string WhyNotWalkable = "not-walkable";
        // New here, and each names a clause of CanPlaceBlueprintAt or of the
        // interaction rule rather than a shape of ground.
        public const string WhyMapEdge = "map-edge";
        public const string WhyOccupied = "occupied";
        public const string WhyIdentical = "identical-thing-exists";
        public const string WhyInteractionBlocked = "interaction-blocked";
        public const string WhyInteractionOverlap = "interaction-overlap";
        public const string WhyNotStandable = "not-standable";

        // The overlay grid's own identity, in `map-dump`'s and `CropRenderer`'s
        // field and format (git-bug e6faa51). It is a SECOND grid beside the
        // crop, never mixed into it: the crop keeps `map-view/ascii-1` exactly
        // as `map-view` publishes it, because mixing overlay glyphs into the
        // terrain grid would change what a char means and oblige a bump of that
        // alphabet and of `map-dump`'s `distinct_from` — which is the identity
        // `accept/e6faa51-channel-alphabet.py` enforces.
        public const string OverlayAlphabetId = "site-survey/overlay-1";

        private const char GlyphFootprint = 'F';
        private const char GlyphInteraction = 'I';
        private const char GlyphMargin = 'M';

        // Per-cell rows are cheap for the two gate tiers and unbounded for the
        // margin (a 3x survey of an 11x6 def is 33x18 = 594 cells), so the
        // margin publishes rows only for cells worth reporting and tallies the
        // rest. The picture carries the whole ring.
        private const int MarginRowCap = 40;

        [Verb("site-survey")]
        public static object Survey(VerbContext ctx)
        {
            var map = Find.CurrentMap ?? throw new VerbArgsException("no current map");
            var a = ctx.Args;

            var def = SiteGate.Named(a.StrReq("def"));
            var stuff = ResolveStuff(def, a.Str("stuff"));
            // `rot` omitted is the game's own preference: Designator_Build
            // starts at def.defaultPlacingRot, so a caller that does not care
            // gets what a player clicking the architect menu would get. (NOT
            // Rot4.North, which is dev:spawn-thing's default because
            // DebugThingPlaceHelper.DebugSpawn hard-codes it — two verbs, two
            // models, DESIGN records the split.)
            var rot = Rotations.Arg(a, "rot", def.defaultPlacingRot);

            // `pos` is the game's centre; `at` is a rect CORNER and is
            // converted, per rotation, by inverting GenAdj.OccupiedRect. The
            // two are not interchangeable and for an even-sized def they are
            // not even one cell apart consistently — see Footprint's header.
            bool hasPos = a.Has("pos"), hasAt = a.Has("at");
            if (hasPos && hasAt)
                throw new VerbArgsException(
                    "pass 'pos' (the game's placement centre) or 'at' (the footprint's "
                    + "south-west corner), not both — they are different conventions and "
                    + "for an even-sized def they name different cells");
            if (!hasPos && !hasAt)
                throw new VerbArgsException("site-survey needs 'pos' or 'at'");

            IntVec3 pos;
            string posSource;
            if (hasPos)
            {
                pos = Positions.Resolve(map, a.Raw("pos"));
                posSource = "pos";
            }
            else
            {
                var corner = Positions.Resolve(map, a.Raw("at"));
                if (!Footprint.TryCentreFor(def.Size, corner, rot, out pos))
                    throw new VerbArgsException(
                        $"could not invert GenAdj.OccupiedRect for {def.defName} at "
                        + $"{corner.x},{corner.z} rot {rot.ToStringWord()} — the round trip did "
                        + "not close, so no centre reproduces that corner");
                posSource = "at";
            }

            var verdict = SiteGate.Check(map, def, pos, rot, stuff);
            var rect = verdict.Rect;

            var interactionCells = InteractionCells(def, pos, rot, map);

            // ------------------------------------------------------- tiers --
            var footprintRows = new List<object>();
            foreach (var c in rect)
                footprintRows.Add(FootprintRow(map, def, stuff, c));

            var interactionRows = new List<object>();
            foreach (var c in interactionCells)
                interactionRows.Add(InteractionRow(map, def, pos, rot, c));

            // ------------------------------------------------------ margin --
            var rotated = Footprint.RotatedSize(def.Size, rot);
            int defaultMargin = Math.Max(3, Math.Max(rotated.x, rotated.z));
            int margin = a.Int("margin", defaultMargin);
            if (margin < 0 || margin > 30) throw new VerbArgsException("margin must be 0..30");

            // margin:0 is "the footprint plus its interaction cells", so the
            // view is the union rather than the footprint alone — an interaction
            // cell one row outside would otherwise be surveyed and not drawn.
            var view = Union(rect.ExpandedBy(margin), interactionCells);
            bool marginCapped = false;
            int marginUsed = margin;
            while ((view.Width > CropRenderer.MaxSide || view.Height > CropRenderer.MaxSide)
                   && marginUsed > 0)
            {
                marginUsed--;
                marginCapped = true;
                view = Union(rect.ExpandedBy(marginUsed), interactionCells);
            }
            view = view.ClipInsideMap(map);

            var marginRows = new List<object>();
            int cells = 0, standable = 0, fogged = 0, roofed = 0, doors = 0, notable = 0;
            foreach (var c in view)
            {
                if (rect.Contains(c) || interactionCells.Contains(c)) continue;
                cells++;
                bool cFog = c.Fogged(map);
                bool cStand = !cFog && c.Standable(map);
                bool cRoof = c.Roofed(map);
                var door = c.GetDoor(map);
                if (cStand) standable++;
                if (cFog) fogged++;
                if (cRoof) roofed++;
                if (door != null) doors++;
                // A row only where there is something to say. The picture
                // carries the clear cells; a 600-row array of "fine" does not.
                if (cStand && !cRoof && door == null) continue;
                notable++;
                if (marginRows.Count < MarginRowCap)
                    marginRows.Add(MarginRow(map, c, cFog, cStand, cRoof, door));
            }

            var tiers = new Dictionary<string, object>
            {
                ["footprint"] = footprintRows,
                ["interaction"] = interactionRows,
                ["margin"] = marginRows,
                // The margin is not a gate, so its answer is facts and tallies
                // rather than a verdict. `blocked` is not a refusal — a roofed
                // or fogged margin cell is information the agent judges with.
                ["margin_facts"] = new Dictionary<string, object>
                {
                    ["rect"] = Footprint.Block(view),
                    ["margin"] = marginUsed,
                    ["margin_default"] = defaultMargin,
                    ["margin_capped"] = marginCapped,
                    ["cells"] = cells,
                    ["standable"] = standable,
                    ["fogged"] = fogged,
                    ["roofed"] = roofed,
                    ["doors"] = doors,
                    ["notable"] = notable,
                    ["listed"] = marginRows.Count,
                    ["more"] = Math.Max(0, notable - marginRows.Count),
                    ["reach"] = Reach(map, a, interactionCells),
                },
            };

            // --------------------------------------------------------- out --
            var data = verdict.Out();
            data["pos_source"] = posSource;
            data["rot_source"] = a.Has("rot") ? "arg" : "def.defaultPlacingRot";
            data["interaction_cells"] = OutCells(interactionCells);
            data["tiers"] = tiers;
            data["view"] = View(map, view, rect, interactionCells);
            return data;
        }

        // ----------------------------------------------------------- tiers --

        // One footprint cell, tested the way RimWorld/GenConstruct.
        // CanPlaceBlueprintAt tests it: bounds, InNoBuildEdgeArea (GenGrid,
        // 10 cells unless the map is a pocket map or its layer sets
        // ignoreNoBuildArea), fog, the terrain affordance from
        // ThingUtility.GetTerrainAffordanceNeed(stuff), and
        // CanPlaceBlueprintOver per occupant — under which a haulable item
        // PASSES, because a pawn hauls it away, while a building does not.
        //
        // ONE DELIBERATE REFINEMENT of vanilla: `CanPlaceBlueprintAt` tests
        // `center.Fogged(map)` and the identical-thing scan on the CENTRE cell
        // only. Per cell is strictly more informative and cannot disagree with
        // the verdict about whether the placement is legal — the verdict is the
        // game's own call, published verbatim beside these rows. Said out loud
        // because a reader comparing the two will notice.
        private static Dictionary<string, object> FootprintRow(Map map, BuildableDef def,
            ThingDef stuff, IntVec3 c)
        {
            if (!c.InBounds(map)) return Row(map, c, "footprint", false, WhyOutOfBounds, null);
            if (c.InNoBuildEdgeArea(map)) return Row(map, c, "footprint", false, WhyMapEdge, null);
            if (c.Fogged(map)) return Row(map, c, "footprint", false, WhyFogged, null);

            var need = def.GetTerrainAffordanceNeed(stuff);
            if (need != null && !c.GetAffordances(map).Contains(need))
                return Row(map, c, "footprint", false, WhyTerrain, null);

            var things = c.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                var t = things[i];
                if (t?.def == null) continue;
                if (t.def == def || t.def.entityDefToBuild == def)
                    return Row(map, c, "footprint", false, WhyIdentical, t);
                if (!GenConstruct.CanPlaceBlueprintOver(def, t.def, stuff, t.Stuff))
                    return Row(map, c, "footprint", false, WhyOccupied, t);
            }
            return Row(map, c, "footprint", true, null, null);
        }

        // One interaction cell. Two rules, and they are different rules:
        //
        //  1. STANDABLE — GenConstruct.InteractionCellStandable: in bounds, and
        //     no occupant whose `def.passability != Standable`, whose def is
        //     this very def, or whose `entityDefToBuild` fails either test (an
        //     unstandable blueprint standing where the pawn would). A chair is
        //     fine: DiningChair inherits from BuildingBase, which sets no
        //     passability, and Traversability's first value is Standable.
        //
        //  2. EXCLUSIVE — RimWorld/PlaceWorker_PreventInteractionSpotOverlap,
        //     which both research benches carry. It scans the 3x3 around each of
        //     OUR interaction cells for anything (blueprints included, via
        //     entityDefToBuild) whose OWN interaction cell is the same cell.
        //     Two benches may not share a spot even when both spots are clear.
        //
        // Rule 2 is asked of the whole placement, not of one cell, so it is
        // asked once and attributed to the cell the PlaceWorker's own walk
        // names. Third-party code, so wrapped (SiteGate's discipline).
        private static Dictionary<string, object> InteractionRow(Map map, BuildableDef def,
            IntVec3 pos, Rot4 rot, IntVec3 c)
        {
            if (!c.InBounds(map)) return Row(map, c, "interaction", false, WhyOutOfBounds, null);
            var thingDef = def as ThingDef;
            if (thingDef != null)
            {
                var list = map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t?.def == null) continue;
                    if (t.def.passability != Traversability.Standable || t.def == thingDef)
                        return Row(map, c, "interaction", false, WhyInteractionBlocked, t);
                    var built = t.def.entityDefToBuild;
                    if (built != null
                        && (built.passability != Traversability.Standable || built == thingDef))
                        return Row(map, c, "interaction", false, WhyInteractionBlocked, t);
                }
                var overlap = Overlap(map, thingDef, pos, rot);
                if (overlap != null && overlap.Value.Key == c)
                    return Row(map, c, "interaction", false, WhyInteractionOverlap, overlap.Value.Value);
            }
            // Not a gate but the fact the margin tier is for: a spot nobody can
            // stand on is legal and useless, and `standable` is the field that
            // says so without pretending the game refused.
            var row = Row(map, c, "interaction", true, null, null);
            row["standable"] = c.Standable(map);
            return row;
        }

        private static Dictionary<string, object> MarginRow(Map map, IntVec3 c,
            bool fogged, bool standable, bool roofed, Thing door)
        {
            string why = fogged ? WhyFogged : (!standable ? WhyNotStandable : null);
            var row = Row(map, c, "margin", why == null, why, null);
            row["standable"] = standable;
            row["fogged"] = fogged;
            row["roofed"] = roofed;
            if (door != null) row["door"] = Blockers.Describe(door);
            return row;
        }

        // The row shape all three tiers share, so a consumer written for one
        // reads the others. `blocker` is null on an accepted cell and carries
        // Blockers.cs's {def,label,at,removal,reason} shape otherwise — the
        // shape 3.2's designators, 3.3's preflight and 3.4's drafted attack
        // already consume, which is how "clear it" vs "site elsewhere" becomes
        // a decision the agent can make from this one read.
        private static Dictionary<string, object> Row(Map map, IntVec3 c, string role,
            bool ok, string reason, Thing blocker)
        {
            var d = new Dictionary<string, object>
            {
                ["at"] = Positions.Out(c),
                ["role"] = role,
                ["ok"] = ok,
                ["reason"] = reason,
            };
            d["blocker"] = ok ? null : Blockers.At(map, c, reason, blocker);
            return d;
        }

        // ------------------------------------------------------- the ring ---

        // Whether a colonist can reach each interaction cell, and from where.
        // NOT a gate — the game will happily let you blueprint a bench in a
        // sealed room — and the single most useful margin fact there is.
        //
        // `from` defaults to the first free colonist rather than the map centre,
        // because the question is "can the colony use this", and it is published
        // with its source so "unreachable" and "nobody to reach it" never read
        // alike. TraverseMode.PassDoors matches SpatialVerbs.CheckRect's
        // reachable-from, so the two verbs answer the same question.
        private static Dictionary<string, object> Reach(Map map, VerbArgs a, List<IntVec3> cells)
        {
            IntVec3 from = IntVec3.Invalid;
            string source;
            if (a.Has("from"))
            {
                from = Positions.Resolve(map, a.Raw("from"));
                source = "arg";
            }
            else
            {
                var colonists = map.mapPawns.FreeColonistsSpawned;
                if (colonists != null && colonists.Count > 0)
                {
                    from = colonists[0].Position;
                    source = "first-free-colonist";
                }
                else source = "none";
            }
            var d = new Dictionary<string, object>
            {
                ["from"] = from.IsValid ? Positions.Out(from) : null,
                ["source"] = source,
                ["mode"] = "PassDoors",
            };
            var answers = new List<object>();
            if (from.IsValid)
                foreach (var c in cells)
                {
                    bool ok = false;
                    try
                    {
                        ok = map.reachability.CanReach(from, c, PathEndMode.OnCell,
                            TraverseParms.For(TraverseMode.PassDoors));
                    }
                    catch { }
                    answers.Add(new Dictionary<string, object>
                    {
                        ["at"] = Positions.Out(c),
                        ["reachable"] = ok,
                        // The same rule the rest of the surface follows: fog is
                        // reported, never used to refuse a reachability answer
                        // (SpatialVerbs' class comment).
                        ["fogged"] = c.Fogged(map),
                    });
                }
            d["interaction_cells"] = answers;
            return d;
        }

        // ------------------------------------------------------ the picture --

        // The crop plus a SECOND grid of the same origin and extent marking the
        // three tiers. Two grids rather than one so `map-view/ascii-1` is
        // untouched: the overlay has its own alphabet id and its own legend,
        // and `rows[i][j]` of one lines up with `rows[i][j]` of the other
        // because both are built from the same clipped rect in the same
        // north-up order (CropRenderer publishes `origin`/`w`/`h` for the
        // crop; the overlay repeats them so a consumer can assert it).
        private static Dictionary<string, object> View(Map map, CellRect view,
            CellRect footprint, List<IntVec3> interaction)
        {
            var crop = CropRenderer.Render(map, view, new List<string>(CropRenderer.DefaultLayers));
            var rows = new List<object>();
            for (int z = view.maxZ; z >= view.minZ; z--)
            {
                var sb = new System.Text.StringBuilder(view.Width);
                for (int x = view.minX; x <= view.maxX; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    // Every cell of `view` is one of the three by
                    // construction: the loop walks exactly that rect.
                    if (footprint.Contains(c)) sb.Append(GlyphFootprint);
                    else if (interaction.Contains(c)) sb.Append(GlyphInteraction);
                    else sb.Append(GlyphMargin);
                }
                rows.Add(sb.ToString());
            }
            crop["overlay"] = new Dictionary<string, object>
            {
                ["channel"] = new Dictionary<string, object>
                {
                    ["name"] = "site-survey/overlay",
                    ["alphabet"] = OverlayAlphabetId,
                    ["distinct_from"] = CropRenderer.AlphabetId,
                    ["note"] = "one glyph per cell naming which TIER of the survey the cell "
                             + "belongs to, over the same origin/w/h as the crop beside it; "
                             + "this is not a map alphabet and says nothing about what is on "
                             + "the ground",
                },
                ["origin"] = new List<object> { (double)view.minX, (double)view.minZ },
                ["w"] = view.Width,
                ["h"] = view.Height,
                ["rows"] = rows,
                ["legend"] = new Dictionary<string, object>
                {
                    ["F"] = "footprint (the game's gate)",
                    ["I"] = "interaction cell (the game's gate)",
                    ["M"] = "margin (surveyed facts, NOT a gate)",
                },
            };
            return crop;
        }

        // --------------------------------------------------------- helpers --

        // A COPY. ThingUtility.InteractionCellsWhenAt hands back the shared
        // static tmpInteractionCells list, cleared and refilled on every call,
        // so holding it across another call hands the caller someone else's
        // answer.
        internal static List<IntVec3> InteractionCells(BuildableDef def, IntVec3 pos, Rot4 rot, Map map)
        {
            var cells = new List<IntVec3>();
            var thingDef = def as ThingDef;
            if (thingDef == null || !thingDef.HasSingleOrMultipleInteractionCells) return cells;
            try { cells.AddRange(ThingUtility.InteractionCellsWhenAt(thingDef, pos, rot, map)); }
            catch { }
            return cells;
        }

        // PlaceWorker_PreventInteractionSpotOverlap's own walk, for the cell and
        // the thing its AcceptanceReport names only by label. Returns null when
        // nothing overlaps. Runs only when the def actually carries that
        // PlaceWorker, so a def without it pays nothing.
        private static KeyValuePair<IntVec3, Thing>? Overlap(Map map, ThingDef def,
            IntVec3 pos, Rot4 rot)
        {
            try
            {
                bool carries = false;
                var workers = def.PlaceWorkers;
                if (workers != null)
                    for (int i = 0; i < workers.Count; i++)
                        if (workers[i] is PlaceWorker_PreventInteractionSpotOverlap) carries = true;
                if (!carries) return null;

                var ours = InteractionCells(def, pos, rot, map);
                for (int i = -1; i <= 1; i++)
                    for (int j = -1; j <= 1; j++)
                        foreach (var mine in ours)
                        {
                            var c = new IntVec3(mine.x + i, 0, mine.z + j);
                            if (!c.InBounds(map)) continue;
                            foreach (var t in map.thingGrid.ThingsListAtFast(c))
                            {
                                var other = t.def.entityDefToBuild != null
                                    ? t.def.entityDefToBuild as ThingDef
                                    : t.def;
                                if (other == null || !other.HasSingleOrMultipleInteractionCells) continue;
                                var theirs = InteractionCells(other, t.Position, t.Rotation, map);
                                for (int k = 0; k < theirs.Count; k++)
                                    if (theirs[k] == mine)
                                        return new KeyValuePair<IntVec3, Thing>(mine, t);
                            }
                        }
            }
            catch { }
            return null;
        }

        private static CellRect Union(CellRect rect, List<IntVec3> cells)
        {
            int minX = rect.minX, minZ = rect.minZ, maxX = rect.maxX, maxZ = rect.maxZ;
            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i].x < minX) minX = cells[i].x;
                if (cells[i].x > maxX) maxX = cells[i].x;
                if (cells[i].z < minZ) minZ = cells[i].z;
                if (cells[i].z > maxZ) maxZ = cells[i].z;
            }
            return new CellRect(minX, minZ, maxX - minX + 1, maxZ - minZ + 1);
        }

        private static List<object> OutCells(List<IntVec3> cells)
        {
            var list = new List<object>();
            for (int i = 0; i < cells.Count; i++) list.Add(Positions.Out(cells[i]));
            return list;
        }

        // Stuff resolution without dev:spawn-thing's `random`: a survey must be
        // deterministic, because its whole purpose is to be re-asked and to
        // agree with the build verb that follows it. Absent means the game's own
        // default, which is what Designator_Build's stuff dropdown opens on.
        internal static ThingDef ResolveStuff(BuildableDef def, string arg)
        {
            bool madeFromStuff = def.MadeFromStuff;
            if (!madeFromStuff)
            {
                if (arg != null) throw new VerbArgsException($"'{def.defName}' is not made from stuff");
                return null;
            }
            if (arg == null) return GenStuff.DefaultStuffFor(def);
            var stuff = DefDatabase<ThingDef>.GetNamedSilentFail(arg)
                ?? throw new VerbArgsException($"no ThingDef named '{arg}'");
            if (stuff.stuffProps == null || !stuff.stuffProps.CanMake(def))
                throw new VerbArgsException(
                    $"'{stuff.defName}' cannot make '{def.defName}' (stuffProps.CanMake said no)");
            return stuff;
        }
    }
}
