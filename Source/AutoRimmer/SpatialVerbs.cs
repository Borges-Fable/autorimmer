using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // Spatial verbs (spec 2.3, remediated by 2.6). Queries return candidates +
    // reasons, never bare booleans; every position argument goes through
    // Positions.Resolve, so landmarks/pawn:/thing: work everywhere. Read-only
    // throughout.
    //
    // FOG OF WAR (DESIGN decisions log, 2026-08-30): the whole player-facing
    // surface hides undiscovered cells — map-view, find-rect, nearest and
    // room-at alike, one rule rather than the three behaviours 2.3 shipped in
    // one file. `c.Fogged(map)` is the test. The agent must not site a building
    // in ground it has never explored, and an agent holding information no
    // player has weakens the colony as a test substrate. `dev:*` is exempt;
    // nothing in this file is dev:*. reachable/path-cost are deliberately NOT
    // gated — a player cannot query reachability at all, they issue a move
    // order and the pawn paths into fog like any other pawn; those two verbs
    // report `from_fogged`/`to_fogged` instead of refusing, so the caller knows
    // it is asking about unexplored ground.
    //
    // BLOCKERS (same decisions-log entry): every rejected cell or thing carries
    // `removal` (mine|deconstruct|attack|none) and the game's own reason string.
    // See Blockers.cs — the taxonomy is the game's, not ours.
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
                // CenteredOn(c, r) is (2r+1) on a side, so the legal radius is
                // (MaxSide-1)/2 and NOT MaxSide/2 — which is what 2.3 wrote,
                // making the documented-legal radius:30 a guaranteed bad-args
                // against a 60-cell cap (2.6 blocker 4). MaxSide is odd now.
                int radius = ctx.Args.Int("radius", 12);
                int maxRadius = (CropRenderer.MaxSide - 1) / 2;
                if (radius < 1 || radius > maxRadius)
                    throw new VerbArgsException($"radius must be 1..{maxRadius}");
                rect = CellRect.CenteredOn(around, radius);
            }
            var layers = ctx.Args.Has("layers") ? ctx.Args.StrList("layers") : new List<string>(CropRenderer.DefaultLayers);
            return CropRenderer.Render(map, rect, layers);
        }

        // Origin candidates for a W x H rect, ordered by the distance from
        // `near` to the rect's CENTRE — the number the caller is shown and the
        // number the ordering is proven against.
        //
        // 2.6 blocker 1, stated precisely because 2.3's own comment claimed the
        // mismatch was already fixed. CellRect.CenterCell is `origin + (w/2,
        // h/2)` (decompiled CellRect.cs:170), so the origins whose CENTRES sit
        // closest to `near` form a ring around `near - (w/2, h/2)` — several
        // rings away from `near` itself. 2.3 ring-walked ORIGINS around `near`,
        // stopped the instant it held `max` of them, and only THEN sorted by
        // centre distance. Sorting after early termination reorders the wrong
        // set; it does not select the right one. Visible in 2.3's own closing
        // evidence: near=[114,133] returned [113,132] at dist 2.2 while
        // [112,130] is at dist 0.0.
        //
        // So the walk is in CENTRE space: ring r enumerates candidate CENTRES at
        // Chebyshev distance r from `near`, and each origin is `centre - (w/2,
        // h/2)`. Termination is proven, not assumed: a cell on ring r is at
        // Euclidean distance >= r, so once `max` candidates are held and the
        // worst of them is nearer than the ring about to be walked, no later
        // ring can improve the answer. A walk that stops for any OTHER reason
        // (examine cap, ring ceiling) says so in `capped`, and `searched_radius`
        // reports how far it actually looked — empty candidates used to be
        // ambiguous between "none nearby" and "stopped looking".
        [Verb("find-rect")]
        public static object FindRect(VerbContext ctx)
        {
            // `def` SWITCHES THE VERB, and the size path below is untouched.
            // Two reasons for the hard branch rather than one merged walk
            // (git-bug c718e4a): the def path asks the real placement gate
            // where the size path asks three cheap cell predicates, and the
            // acceptance requires `find-rect {w,h}` output to be what it always
            // was, byte for byte. A shared body is how that quietly stops being
            // true.
            if (ctx.Args.Has("def")) return FindRectForDef(ctx);

            var map = Map();
            int w = ctx.Args.IntReq("w");
            int h = ctx.Args.IntReq("h");
            if (w < 1 || h < 1 || w > 30 || h > 30) throw new VerbArgsException("w,h must be 1..30");
            var near = ctx.Args.Has("near") ? Positions.Resolve(map, ctx.Args.Raw("near")) : map.Center;
            int max = Math.Min(20, ctx.Args.Int("max", 5));
            if (max < 1) throw new VerbArgsException("max must be >= 1");
            var require = ctx.Args.Has("require") ? ctx.Args.StrList("require") : new List<string> { "buildable" };

            IntVec3 reachFrom = IntVec3.Invalid;
            foreach (var req in require)
                if (req.StartsWith("reachable-from:", StringComparison.Ordinal))
                    reachFrom = Positions.Resolve(map, req.Substring("reachable-from:".Length));

            var found = new List<Candidate>();
            var rejected = new Dictionary<string, int>();
            var blockers = new Dictionary<string, BlockerTally>();
            int examined = 0;
            int searchedRadius = -1;
            bool capped = false;
            const int ExamineCap = 6000;
            const int MaxRing = 80;

            int offX = w / 2, offZ = h / 2;
            int ring = 0;
            for (; ring <= MaxRing; ring++)
            {
                if (found.Count >= max)
                {
                    found.Sort((a, b) => a.Dist.CompareTo(b.Dist));
                    if (found.Count > max) found.RemoveRange(max, found.Count - max);
                    // Every cell on this ring is at Euclidean distance >= ring.
                    if (ring > found[max - 1].Dist) break;
                }
                if (examined >= ExamineCap) { capped = true; break; }

                foreach (var centre in RingCells(near, ring))
                {
                    if (++examined > ExamineCap) { capped = true; break; }
                    var origin = new IntVec3(centre.x - offX, 0, centre.z - offZ);
                    var rect = new CellRect(origin.x, origin.z, w, h);
                    if (rect.minX < 0 || rect.minZ < 0 || rect.maxX >= map.Size.x || rect.maxZ >= map.Size.z)
                    {
                        Tally(rejected, "out-of-bounds");
                        continue;
                    }
                    var fail = CheckRect(map, rect, require, reachFrom);
                    if (fail != null)
                    {
                        Tally(rejected, fail.Reason);
                        Record(blockers, fail);
                        continue;
                    }
                    // rect.CenterCell == centre by construction; recomputed
                    // rather than asserted so the published pair can never
                    // disagree with the game's own arithmetic.
                    var actualCentre = rect.CenterCell;
                    found.Add(new Candidate
                    {
                        Origin = origin,
                        Centre = actualCentre,
                        Dist = near.DistanceTo(actualCentre),
                    });
                }
                searchedRadius = ring;
                if (capped) break;
            }
            if (ring > MaxRing) capped = true;

            found.Sort((a, b) => a.Dist.CompareTo(b.Dist));
            if (found.Count > max) found.RemoveRange(max, found.Count - max);
            var candidates = new List<object>();
            foreach (var c in found)
                candidates.Add(new Dictionary<string, object>
                {
                    ["at"] = Positions.Out(c.Origin),
                    ["center"] = Positions.Out(c.Centre),
                    ["dist"] = Math.Round(c.Dist, 1),
                });

            return new Dictionary<string, object>
            {
                ["candidates"] = candidates,
                ["examined"] = examined,
                // How far the walk actually looked, in cells, around `near` in
                // centre space; -1 means it never completed even ring 0.
                ["searched_radius"] = searchedRadius,
                // true = stopped on a budget, not on a proof. Candidates may be
                // incomplete AND a closer one may exist beyond searched_radius.
                ["capped"] = capped,
                ["rejected"] = ToTree(rejected),
                ["blockers"] = TopBlockers(blockers, BlockerCap, out int blockersMore),
                ["blockers_more"] = blockersMore,
            };
        }

        // ------------------------------------------- find-rect {def} -------
        // The same search, gated by the REAL thing (git-bug c718e4a).
        //
        // `find-rect {w,h}` answers "is this box clear", and on bench
        // 20260901T121508 that was the wrong question: it approved a 3x2 box
        // that the game then refused for granite on the interaction cell one row
        // SOUTH of it. `CheckRect` walks the rect's own cells and takes no def,
        // so it cannot know a def has an interaction cell at all. With `def` in
        // hand every candidate goes through `SiteGate` — the identical routine
        // `site-survey` publishes — so an approved candidate is one the game
        // will accept, and a caller can prove it with one `site-survey` on the
        // candidate it picked.
        //
        // ROTATION IS PART OF THE ANSWER, not an assumption. An interaction cell
        // moves with rotation, so a bench that will not fit facing north
        // routinely fits facing east, and rotation is usually the cheapest thing
        // an agent can change about a rejected site. A search that silently
        // fixed `rot` would report "no room" for ground that has room. Omitted
        // `rot` searches all four in `def.defaultPlacingRot`-first order, which
        // is the game's own preference (`Designator_Build` opens there); a given
        // `rot` pins the search to it and the refusals are tallied so "does not
        // fit facing north" is a readable answer rather than an empty list.
        //
        // ONE CANDIDATE PER CELL — the first rotation in preference order that
        // the gate accepts, and NOT the cross product. Resolved rather than left
        // open: four rotations of one cell are one site as far as "where can
        // this go" is concerned, `max` would otherwise be spent four times over
        // on the same ground, and a caller that wants a specific facing pins
        // `rot`. `rot_order` is published so the choice is legible.
        private static object FindRectForDef(VerbContext ctx)
        {
            var map = Map();
            var a = ctx.Args;
            if (a.Has("w") || a.Has("h"))
                throw new VerbArgsException(
                    "w/h are forbidden alongside def — the def sizes the rect, with the "
                    + "horizontal axis swap applied per rotation, and a caller-supplied size "
                    + "could disagree with it");
            // `require` NARROWS the def gate; it does not replace it, and the
            // two cell predicates the gate already subsumes are refused rather
            // than silently accepted and ignored (git-bug 7382bdd's failure
            // mode: an argument that reads as honoured and is not).
            //
            // `reachable-from:P` changes meaning here, and the change is the
            // point of c718e4a's third complaint: in size mode it tests the
            // rect's CENTRE cell, which for a workbench is a cell no pawn ever
            // stands on. With a def in hand it tests the INTERACTION cells —
            // the cells a pawn must reach to use the thing — and falls back to
            // the footprint for a def that has none.
            var reachFrom = IntVec3.Invalid;
            bool wantRoofed = false, wantUnroofed = false;
            if (a.Has("require"))
                foreach (var req in a.StrList("require"))
                {
                    if (req.StartsWith("reachable-from:", StringComparison.Ordinal))
                        reachFrom = Positions.Resolve(map, req.Substring("reachable-from:".Length));
                    else if (req == "roofed") wantRoofed = true;
                    else if (req == "unroofed") wantUnroofed = true;
                    else if (req == "buildable" || req == "walkable")
                        throw new VerbArgsException(
                            $"require '{req}' is meaningless alongside def: the gate is "
                            + "GenConstruct.CanPlaceBlueprintAt plus Designator_Build.Visible, "
                            + "which subsumes it and is strictly stricter. Drop it rather than "
                            + "have it silently ignored");
                    else
                        throw new VerbArgsException(
                            $"unknown requirement '{req}' in def mode "
                            + "(roofed|unroofed|reachable-from:P)");
                }
            if (wantRoofed && wantUnroofed)
                throw new VerbArgsException("require cannot ask for both roofed and unroofed");

            var def = SiteGate.Named(a.StrReq("def"));
            var stuff = SiteVerbs.ResolveStuff(def, a.Str("stuff"));
            var near = a.Has("near") ? Positions.Resolve(map, a.Raw("near")) : map.Center;
            int max = Math.Min(20, a.Int("max", 5));
            if (max < 1) throw new VerbArgsException("max must be >= 1");

            bool rotGiven = a.Has("rot");
            var order = rotGiven
                ? new List<Rot4> { Rotations.Arg(a, "rot", def.defaultPlacingRot) }
                : RotationOrder(def);

            // Designator_Build.Visible is DEF-level, not cell-level, so it is
            // asked once. A def that is not on the architect menu has no
            // candidates anywhere, and walking six thousand cells to discover
            // that is six thousand calls into CanPlaceBlueprintAt for nothing.
            bool selectable = SiteGate.Selectable(map, def, out string clause, out string detail);

            var found = new List<DefCandidate>();
            var rejected = new Dictionary<string, int>();
            var refusals = new List<object>();
            int examined = 0, gateCalls = 0;
            int searchedRadius = -1;
            bool capped = false;
            // Lower than the size path's 6000 on purpose: each examine here can
            // cost four CanPlaceBlueprintAt calls, and that member walks the
            // occupied rect, every occupant's thing list and the def's
            // PlaceWorkers. Published, so an empty answer is never mistaken for
            // a proof.
            const int ExamineCap = 2000;
            const int RefusalCap = 8;
            const int MaxRing = 60;

            int ring = 0;
            if (selectable)
            for (; ring <= MaxRing; ring++)
            {
                if (found.Count >= max)
                {
                    Sort(found);
                    if (found.Count > max) found.RemoveRange(max, found.Count - max);
                    // Every cell on this ring is at Euclidean distance >= ring
                    // from `near`, and `Dist` is measured to the placement
                    // centre we walked — the same key we sort by, which is the
                    // whole of 2.6 blocker 1's lesson: terminating on one key
                    // and sorting by another selects the wrong set.
                    if (ring > found[max - 1].Dist) break;
                }
                if (examined >= ExamineCap) { capped = true; break; }

                foreach (var pos in RingCells(near, ring))
                {
                    if (++examined > ExamineCap) { capped = true; break; }
                    if (!pos.InBounds(map)) { Tally(rejected, "out-of-bounds"); continue; }

                    bool placed = false;
                    for (int i = 0; i < order.Count && !placed; i++)
                    {
                        var rot = order[i];
                        var rect = GenAdj.OccupiedRect(pos, rot, def.Size);
                        if (rect.minX < 0 || rect.minZ < 0
                            || rect.maxX >= map.Size.x || rect.maxZ >= map.Size.z)
                        {
                            Tally(rejected, "out-of-bounds");
                            continue;
                        }
                        // Fog first and cheaply. The game's own gate tests only
                        // the CENTRE cell (GenConstruct.CanPlaceBlueprintAt,
                        // `center.Fogged(map)`), while this file's standing rule
                        // is that the whole player-facing surface hides
                        // undiscovered cells — and it short-circuits most of the
                        // expensive calls in unexplored ground.
                        bool fogged = false;
                        foreach (var c in rect) if (c.Fogged(map)) { fogged = true; break; }
                        if (fogged) { Tally(rejected, "fogged"); continue; }

                        if (wantRoofed || wantUnroofed)
                        {
                            bool anyOpen = false, anyRoof = false;
                            foreach (var c in rect)
                                if (c.Roofed(map)) anyRoof = true; else anyOpen = true;
                            if (wantRoofed && anyOpen) { Tally(rejected, "unroofed"); continue; }
                            if (wantUnroofed && anyRoof) { Tally(rejected, "roofed"); continue; }
                        }

                        gateCalls++;
                        var v = SiteGate.Check(map, def, pos, rot, stuff);
                        if (v.PlaceOk && reachFrom.IsValid && !Reaches(map, def, pos, rot, reachFrom))
                        {
                            Tally(rejected, "unreachable");
                            continue;
                        }
                        if (v.PlaceOk)
                        {
                            found.Add(new DefCandidate
                            {
                                Pos = pos,
                                Rot = rot,
                                RotRank = i,
                                Rect = v.Rect,
                                Dist = near.DistanceTo(pos),
                            });
                            placed = true;
                            break;
                        }
                        // The game's own sentence is the tally key, so
                        // `rejected` reads as the reasons a player would see.
                        Tally(rejected, v.PlaceReason ?? "refused");
                        if (refusals.Count < RefusalCap)
                            refusals.Add(new Dictionary<string, object>
                            {
                                ["pos"] = Positions.Out(pos),
                                ["rot"] = rot.ToStringWord(),
                                ["footprint"] = Footprint.Block(v.Rect),
                                ["reason"] = v.PlaceReason,
                            });
                    }
                }
                searchedRadius = ring;
                if (capped) break;
            }
            if (ring > MaxRing) capped = true;

            Sort(found);
            if (found.Count > max) found.RemoveRange(max, found.Count - max);
            var candidates = new List<object>();
            foreach (var c in found)
                candidates.Add(new Dictionary<string, object>
                {
                    // The CORNER is the candidate's identity — stable across
                    // rotations in a way a centre is not. `pos` is the argument
                    // to pass to build / dev:spawn-thing / site-survey, computed
                    // the game's way. `center` is deliberately ABSENT in this
                    // mode: CellRect.CenterCell is not the placement centre for
                    // an even-sized rect, and a field that looks like the value
                    // to pass and is off by one exactly where it matters is the
                    // bench failure this verb was fixed for.
                    ["at"] = Positions.Out(new IntVec3(c.Rect.minX, 0, c.Rect.minZ)),
                    ["w"] = c.Rect.Width,
                    ["h"] = c.Rect.Height,
                    ["rot"] = c.Rot.ToStringWord(),
                    ["pos"] = Positions.Out(c.Pos),
                    ["dist"] = Math.Round(c.Dist, 1),
                });

            var rotOrder = new List<object>();
            for (int i = 0; i < order.Count; i++) rotOrder.Add(order[i].ToStringWord());

            return new Dictionary<string, object>
            {
                // Present only in def mode, and it is what tells a consumer
                // which shape it is holding: this mode publishes `pos` and
                // `refusals`, the size mode publishes `center` and `blockers`.
                ["mode"] = "def",
                ["def"] = def.defName,
                ["stuff"] = stuff?.defName,
                ["gate"] = SiteGate.GateId,
                ["rot_given"] = rotGiven,
                ["rot_order"] = rotOrder,
                // What `require` was understood to mean, echoed because in def
                // mode reachable-from tests the INTERACTION cells and not the
                // rect's centre — a caller must be able to see which question
                // was answered.
                ["require"] = new Dictionary<string, object>
                {
                    ["reachable_from"] = reachFrom.IsValid ? Positions.Out(reachFrom) : null,
                    ["reach_target"] = "interaction cells, else the footprint (Touch)",
                    ["roofed"] = wantRoofed,
                    ["unroofed"] = wantUnroofed,
                },
                // The def-level half of the gate, asked once. `ok:false` here
                // means the def is not on the architect menu at all, which is
                // why `candidates` is empty — a different fact from "no room".
                ["selectable"] = new Dictionary<string, object>
                {
                    ["ok"] = selectable,
                    ["source"] = "Designator_Build.Visible",
                    ["clause"] = clause,
                    ["detail"] = detail,
                },
                ["candidates"] = candidates,
                ["examined"] = examined,
                ["gate_calls"] = gateCalls,
                ["searched_radius"] = searchedRadius,
                ["capped"] = capped,
                ["rejected"] = ToTree(rejected),
                // A few worked refusals rather than a tally of obstacles. There
                // is deliberately no per-cell blocker read here: the refusing
                // cell of a rejected candidate is frequently NOT the cell we
                // walked (git-bug 8b4839f — a bench refused for granite one row
                // south reported a wood log on the target), and describing the
                // walked cell instead is that defect at scale. One `site-survey`
                // on a candidate gives all three tiers with the right cells.
                ["refusals"] = refusals,
                ["refusals_note"] = "per-cell obstacles are site-survey's job — its tiers "
                                  + "name the cell that actually refused",
            };
        }

        // Whether a pawn could get to the cells that USING this thing needs —
        // its interaction cells, and the footprint itself only for a def that
        // has none (a wall, a floor, a battery). PassDoors matches CheckRect's
        // own reachable-from so both modes answer the same question about the
        // same traversal rules.
        private static bool Reaches(Map map, BuildableDef def, IntVec3 pos, Rot4 rot, IntVec3 from)
        {
            try
            {
                var parms = TraverseParms.For(TraverseMode.PassDoors);
                var cells = SiteVerbs.InteractionCells(def, pos, rot, map);
                if (cells.Count > 0)
                {
                    for (int i = 0; i < cells.Count; i++)
                        if (!map.reachability.CanReach(from, cells[i], PathEndMode.OnCell, parms))
                            return false;
                    return true;
                }
                // No interaction cell: "can a pawn get to it at all", which for
                // a wall or a floor is a question about touching the rect. There
                // is no CellRect overload of Reachability.CanReach — only
                // LocalTargetInfo — so the rect is walked and any touchable cell
                // is enough, which is what PathEndMode.Touch means for the
                // construction jobs that will build the thing.
                foreach (var c in GenAdj.OccupiedRect(pos, rot, def.Size))
                    if (map.reachability.CanReach(from, c, PathEndMode.Touch, parms)) return true;
                return false;
            }
            catch { return false; }
        }

        private sealed class DefCandidate
        {
            public IntVec3 Pos;
            public Rot4 Rot;
            public int RotRank;
            public CellRect Rect;
            public float Dist;
        }

        // Nearest first; at equal distance, def.defaultPlacingRot first. The
        // rotation rank is the tie-break rather than a sort key of its own, so a
        // caller that does not care gets the game's own preference and a caller
        // that does reads `rot` off each candidate.
        private static void Sort(List<DefCandidate> found)
            => found.Sort((x, y) =>
            {
                int c = x.Dist.CompareTo(y.Dist);
                return c != 0 ? c : x.RotRank.CompareTo(y.RotRank);
            });

        // def.defaultPlacingRot, then the remaining three in Rot4 order.
        private static List<Rot4> RotationOrder(BuildableDef def)
        {
            var first = def.defaultPlacingRot;
            var order = new List<Rot4> { first };
            for (int i = 0; i < Rotations.All.Length; i++)
                if (Rotations.All[i] != first) order.Add(Rotations.All[i]);
            return order;
        }

        private sealed class Candidate
        {
            public IntVec3 Origin;
            public IntVec3 Centre;
            public float Dist;
        }

        // Why one candidate rect was rejected: the tally key, plus the cell and
        // (where there is one) the thing standing in the way.
        private sealed class RectFail
        {
            public string Reason;
            public Thing Thing;
            public IntVec3 At;
        }

        private sealed class BlockerTally
        {
            public string Def, Label, Removal, Reason, Why;
            public int Count;
            public IntVec3 At;
        }

        private const int BlockerCap = 8;

        // Ring r of CENTRES around `near`: the square shell at Chebyshev
        // distance r. Unchanged in shape from 2.3's RingOrigins; what changed is
        // what the yielded cell MEANS (a centre, not an origin).
        private static IEnumerable<IntVec3> RingCells(IntVec3 near, int ring)
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

        private static RectFail CheckRect(Map map, CellRect rect, List<string> require, IntVec3 reachFrom)
        {
            foreach (var c in rect)
            {
                // Checked for every requirement set, not just "buildable": a
                // fogged cell is unknown ground whatever the caller asked about.
                if (c.Fogged(map)) return new RectFail { Reason = "fogged", At = c };
                foreach (var req in require)
                {
                    switch (req)
                    {
                        case "buildable":
                        {
                            // Heavy affordance carries walls and every normal
                            // building; no standing edifice in the footprint.
                            var edifice = c.GetEdifice(map);
                            if (edifice != null)
                                return new RectFail { Reason = "edifice-in-way", Thing = edifice, At = c };
                            if (!map.terrainGrid.TerrainAt(c).affordances.Contains(TerrainAffordanceDefOf.Heavy))
                                return new RectFail { Reason = "terrain-not-buildable", At = c };
                            break;
                        }
                        case "walkable":
                            if (!c.Walkable(map))
                                return new RectFail { Reason = "not-walkable", Thing = c.GetEdifice(map), At = c };
                            break;
                        case "unroofed":
                            if (c.Roofed(map)) return new RectFail { Reason = "roofed", At = c };
                            break;
                        case "roofed":
                            if (!c.Roofed(map)) return new RectFail { Reason = "unroofed", At = c };
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
                return new RectFail { Reason = "unreachable", At = rect.CenterCell };
            return null;
        }

        // Aggregate rejections into "what is in the way, and how does it clear".
        // Keyed by (def, why) so one wall type is one line however many rects it
        // spoiled; a cell-level rejection (fog, terrain, roof) keys on its own
        // reason and carries removal "none".
        private static void Record(Dictionary<string, BlockerTally> into, RectFail fail)
        {
            string removal, reason, def = null, label = null;
            if (fail.Thing != null)
            {
                Blockers.Classify(fail.Thing, out removal, out reason);
                def = fail.Thing.def.defName;
                label = fail.Thing.def.label;
            }
            else
            {
                removal = Blockers.None;
                reason = fail.Reason == "fogged" ? Blockers.FoggedReason : null;
            }
            string key = (def ?? "-") + "|" + fail.Reason + "|" + removal;
            if (!into.TryGetValue(key, out var tally))
                into[key] = tally = new BlockerTally
                {
                    Def = def,
                    Label = label,
                    Removal = removal,
                    Reason = reason,
                    Why = fail.Reason,
                    At = fail.At,
                };
            tally.Count++;
        }

        private static List<object> TopBlockers(Dictionary<string, BlockerTally> tallies, int cap, out int more)
        {
            var all = new List<BlockerTally>(tallies.Values);
            all.Sort((a, b) =>
            {
                int c = b.Count.CompareTo(a.Count);
                return c != 0 ? c : string.CompareOrdinal(a.Def ?? a.Why, b.Def ?? b.Why);
            });
            more = all.Count > cap ? all.Count - cap : 0;
            var list = new List<object>();
            for (int i = 0; i < all.Count && i < cap; i++)
            {
                var t = all[i];
                list.Add(new Dictionary<string, object>
                {
                    ["why"] = t.Why,
                    ["def"] = t.Def,
                    ["label"] = t.Label,
                    ["removal"] = t.Removal,
                    ["reason"] = t.Reason,
                    ["count"] = t.Count,
                    ["at"] = Positions.Out(t.At),
                });
            }
            return list;
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
                // ListerThings.ThingsOfDef Log.ErrorOnce's on MinifiedThing and
                // tells you to use the group instead (decompiled
                // Verse/ListerThings.cs). A red error raised by agent-supplied
                // args is a breach of the zero-red-errors invariant, so take the
                // route the game itself names.
                pool = def == ThingDefOf.MinifiedThing
                    ? new List<Thing>(map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing))
                    : new List<Thing>(map.listerThings.ThingsOfDef(def));
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
            int skippedFogged = 0, skippedUnspawned = 0;
            for (int i = 0; i < pool.Count && hits.Count < max; i++)
            {
                var t = pool[i];
                if (!t.Spawned) { skippedUnspawned++; continue; }
                // Fog: the same rule the rest of the surface follows. Counted
                // rather than silently dropped, so "no medicine" and "no
                // medicine you have found yet" are distinguishable.
                if (t.PositionHeld.Fogged(map)) { skippedFogged++; continue; }
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
            return new Dictionary<string, object>
            {
                ["query"] = what,
                ["from"] = Positions.Out(from),
                ["hits"] = hits,
                ["pool"] = pool.Count,
                ["skipped"] = new Dictionary<string, object>
                {
                    // removal "none", reason "unexplored" — a fogged thing is
                    // not blocked, it is simply not known to the colony.
                    ["fogged"] = skippedFogged,
                    ["unspawned"] = skippedUnspawned,
                },
            };
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
            var data = new Dictionary<string, object>
            {
                ["reachable"] = ok,
                ["from"] = Positions.Out(from),
                ["to"] = Positions.Out(to),
                // Not gated on fog (see the class comment): pawns path into
                // undiscovered ground in the normal game. Flagged so the caller
                // knows the answer concerns ground the colony has not explored.
                ["from_fogged"] = from.Fogged(map),
                ["to_fogged"] = to.Fogged(map),
            };
            if (pawn != null) data["pawn"] = pawn.LabelShortCap.ToString();
            if (!ok)
            {
                // Fog stays opaque even here. "You cannot get there" is
                // player-observable — order a pawn and watch it refuse — but
                // WHAT is in the way is not, and the first cut of this verb
                // happily named the limestone under undiscovered ground.
                // Caught by 2.6's own acceptance run, which is the argument for
                // running one.
                if (to.Fogged(map))
                {
                    data["note"] = "to-cell is unexplored; no known path";
                    data["blocker"] = Blockers.Cell(to, Blockers.FoggedReason);
                }
                else if (from.Fogged(map))
                {
                    data["note"] = "from-cell is unexplored";
                    data["blocker"] = Blockers.Cell(from, Blockers.FoggedReason);
                }
                else
                {
                    string note;
                    if (!from.Walkable(map)) note = "from-cell is not walkable";
                    else if (!to.Walkable(map)) note = "to-cell is not walkable (wall or impassable)";
                    else note = "no path: separated by walls/terrain" + (pawn != null ? " for this pawn" : "");
                    data["note"] = note;
                    // A rejected cell says HOW it clears, not just that it blocks.
                    var blocker = !to.Walkable(map) ? to.GetEdifice(map)
                        : (!from.Walkable(map) ? from.GetEdifice(map) : null);
                    if (blocker != null) data["blocker"] = Blockers.Describe(blocker);
                }
            }
            return data;
        }

        [Verb("room-at")]
        public static object RoomAt(VerbContext ctx)
        {
            var map = Map();
            var at = Positions.Resolve(map, ctx.Args.Raw("at") ?? throw new VerbArgsException("needs 'at'"));
            // Fog: no room detail out of unexplored ground. Reported as a
            // rejected cell in the standard shape rather than as an error — the
            // agent is allowed to ASK about anywhere, it just gets told the
            // colony has not been there.
            if (at.Fogged(map))
            {
                var fogged = Blockers.Cell(at, Blockers.FoggedReason);
                fogged["room"] = null;
                fogged["fogged"] = true;
                return fogged;
            }
            var room = at.GetRoom(map);
            var edifice = at.GetEdifice(map);
            if (room == null)
            {
                var none = new Dictionary<string, object> { ["at"] = Positions.Out(at), ["room"] = null, ["fogged"] = false };
                if (edifice != null) none["blocker"] = Blockers.Describe(edifice);
                return none;
            }
            var data = new Dictionary<string, object>
            {
                ["at"] = Positions.Out(at),
                ["fogged"] = false,
                ["id"] = room.ID,
                // LAZY: Room.Role runs UpdateRoomStatsAndRole() when
                // statsAndRoleDirty — a full room analysis. See DigestVerb's
                // header; it is idempotent and RNG-free, so read-only holds,
                // but it is not free.
                ["role"] = room.Role?.label,
                ["outdoors"] = room.PsychologicallyOutdoors,
                ["cells"] = room.CellCount,
            };
            // The thing standing on the queried cell, with how it clears — what
            // 3.3's place-layout preflight needs to choose between "clear this
            // and retry" and "site it elsewhere".
            if (edifice != null) data["blocker"] = Blockers.Describe(edifice);
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
                // Same rule as `reachable`: reported, not refused.
                bool fromFogged = from.Fogged(map), toFogged = to.Fogged(map);
                if (path == null || !path.Found)
                    return new Dictionary<string, object>
                    {
                        ["found"] = false,
                        ["from_fogged"] = fromFogged,
                        ["to_fogged"] = toFogged,
                    };
                return new Dictionary<string, object>
                {
                    ["found"] = true,
                    ["cost"] = Math.Round(path.TotalCost, 1),
                    ["length"] = path.NodesLeftCount,
                    ["from_fogged"] = fromFogged,
                    ["to_fogged"] = toFogged,
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
