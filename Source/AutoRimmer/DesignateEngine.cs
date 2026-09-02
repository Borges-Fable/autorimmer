using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ============================================================ spec 3.2 ===
    // THE PLURAL SUBSTRATE. Target resolution, the fog gate, the reject record,
    // and the crop echo — shared by `designate`, `forbid`/`unforbid`, `flick`,
    // `zone` and `area`.
    //
    // DESIGN §Action model, 2026-08-31: **the plural form IS the verb; the
    // singular is its degenerate case.** A 40-cell chop job is ONE call. So
    // every verb in this spec takes a SET — `rect`, `cells`, `things` or
    // `filter` — resolves it here, and reports per-item accepted + rejected
    // with the game's own reason on every rejection. A verb that can only be
    // called in a loop is the defect.
    //
    // ------------------- THE GATE LIVES IN THE WIDGET -----------------------
    // DESIGN's standing invariant, and this spec is the easy case: almost every
    // verb here HAS a real `Designator_*` class whose own
    // `CanDesignateCell`/`CanDesignateThing` IS the widget-layer gate, complete
    // with the game's own `AcceptanceReport` reason strings. So we drive the
    // game's designator objects rather than reimplementing their logic, and
    // each verb cites the class it drove. Two gates ride along:
    //
    //  * `Gizmo.Visible` (Verse/Gizmo.cs) — the architect/reverse-designator
    //    menu's own visibility test. DESIGN cites `Designator_Build.Visible`
    //    (`!entDef.IsResearchFinished`) as the canonical example of a gate that
    //    lives ONLY in the UI layer. `Designator_ExtractSkull.Visible` is the
    //    one override in our table (Ideology + WorkGiver_ExtractSkull
    //    .CanPlayerExtractSkull); it is checked for every type, so a modded
    //    override is honoured too.
    //  * FOG. Checked HERE, before the designator, for every verb — because the
    //    game's own designators disagree with each other about it and one of
    //    them is actively wrong for us: `Designator_Mine.CanDesignateCell`
    //    RETURNS TRUE for a fogged cell (the player is allowed to paint mining
    //    over unexplored rock), while `Designator_Haul`, `Designator_Claim` and
    //    `Designator_ZoneAdd.IsZoneableCell` refuse, and
    //    `Designator_Deconstruct`/`Designator_Uninstall` refuse unless godMode.
    //    DESIGN decisions log 2026-08-30 makes it ONE rule for the whole
    //    player-facing surface, so the gate is ours and uniform: `c.Fogged(map)`
    //    rejects with removal `none` and reason `unexplored`. `dev:*` stays the
    //    only exempt layer.
    //
    // ----------------- WHY WE DO NOT CALL Designator.Finalize ---------------
    // ...except on success. `Designator.Finalize(false)` is pure UI — it plays
    // the failure sound and posts `Find.DesignatorManager.Dragger.FailureReason`
    // as a Message — so a run that designated nothing skips it. `Finalize(true)`
    // is NOT purely cosmetic and is called: `Designator_AreaNoRoof
    // .FinalizeDesignationSucceeded` clears `BuildRoof` for the cells it just
    // added AND drains a STATIC `justAddedCells` list, so skipping it would
    // leak our cells into the next real player drag. Hunt/Slaughter/Tame's
    // Finalize is where the game's own "no hunters available" /
    // venerated-animal warnings come from, and those Messages are journaled —
    // real information the agent should get. We never call `DesignateMultiCell`
    // (it re-enters TutorSystem and, for zones, drives `Find.Selector`).
    //
    // ----------------------------- HAZARDS ---------------------------------
    //  * `DesignationManager.AllDesignations` and `AllDesignationsAt(c)` both
    //    return the SAME static `tmpDesignations` list, cleared and refilled on
    //    every read (WorldSafe Class E). Never held across another call into
    //    the manager — every read here is copied immediately.
    //  * `Designator_ZoneAdd_Fishing.Visible` reads
    //    `ResearchProjectDefOf.Fishing.IsFinished`, which bottoms out in
    //    `ResearchManager.GetProgress` and INSERTS a zero entry into a scribed
    //    dictionary (WorldSafe Class A). Fishing zones are therefore not in
    //    this spec's table at all; see the report on git-bug 57ab92a.
    // =========================================================================
    public static class DesignateEngine
    {
        // A player drag is bounded by the screen; an agent's rect is not. The
        // default is generous (a 50x50 room block) and the ceiling is a budget,
        // not a wall — `capped` and `requested` say what was dropped, the
        // truncation contract every other verb in this mod follows.
        public const int DefaultMaxCells = 2500;
        public const int MaxCellsCeiling = 20000;
        public const int RejectCap = 24;
        public const int ListCap = 64;

        // ------------------------------------------------------------------
        // TARGETS
        // ------------------------------------------------------------------
        // Four mutually exclusive forms, deliberately NOT overloaded onto one
        // key: `rect` and `cells` are both "cells" in the spec's `--cells
        // rect|list` sense, but a 4-element array is ambiguous between a rect
        // and four positions and the caller is a program. One shape per key.
        //
        //   rect:   [x,z,w,h]
        //   cells:  [P, …]        P is any Positions.Resolve form
        //   things: [id, …]       thingIDNumber, the id every 2.x serializer publishes
        //   filter: {…}           see FilterTargets — the `--area-things` form
        public sealed class Targets
        {
            public string Kind;                       // rect|cells|things|filter
            public List<IntVec3> Cells = new List<IntVec3>();
            public List<Thing> Things = new List<Thing>();
            public Dictionary<string, object> Detail;
            public int Requested;                     // before the cap
            public bool Capped;

            public int Count => Kind == "things" || Kind == "filter" ? Things.Count : Cells.Count;

            public bool IsThings => Kind == "things" || Kind == "filter";
        }

        // `filterSelectsTargets` is false for the verbs where `filter` means
        // something else to the CALLER. `zone` is the case that forced this:
        // a stockpile's footprint is always cells, and its `filter` argument is
        // the STORAGE filter (the five presets), so consuming `filter` here as a
        // target selector made `zone add --rect … --filter meds` — the spec's
        // own acceptance bullet — impossible, rejecting it as "mutually
        // exclusive". Found in acceptance, orchestrator, session 6.
        public static Targets Resolve(Map map, VerbArgs a, int maxCells,
            bool filterSelectsTargets = true)
        {
            int given = 0;
            if (a.Has("rect")) given++;
            if (a.Has("cells")) given++;
            if (a.Has("things")) given++;
            if (filterSelectsTargets && (a.Has("filter") || a.Has("area_things"))) given++;
            if (given == 0)
                throw new VerbArgsException(filterSelectsTargets
                    ? "needs a target set: rect:[x,z,w,h] | cells:[P,…] | things:[id,…] | filter:{…} "
                        + "(the plural form IS the verb — one call, N targets)"
                    : "needs a target set: rect:[x,z,w,h] | cells:[P,…] — a zone's footprint is "
                        + "always cells, and `filter` here is its STORAGE filter, not a target selector");
            if (given > 1)
                throw new VerbArgsException(filterSelectsTargets
                    ? "rect, cells, things and filter are mutually exclusive"
                    : "rect and cells are mutually exclusive");

            if (a.Has("rect")) return FromRect(map, ReadRect(a.Raw("rect")), maxCells);
            if (a.Has("cells")) return FromCells(map, a.Raw("cells"), maxCells);
            if (a.Has("things")) return FromThings(map, a.Raw("things"));
            return FromFilter(map, a.Raw("filter") ?? a.Raw("area_things"), maxCells);
        }

        public static CellRect ReadRect(object raw)
        {
            if (!(raw is List<object> r) || r.Count != 4
                || !(r[0] is double x) || !(r[1] is double z)
                || !(r[2] is double w) || !(r[3] is double h))
                throw new VerbArgsException("rect must be [x,z,w,h]");
            if (w < 1 || h < 1) throw new VerbArgsException("rect w,h must be >= 1");
            return new CellRect((int)x, (int)z, (int)w, (int)h);
        }

        private static Targets FromRect(Map map, CellRect rect, int maxCells)
        {
            // A rect with NO overlap at all is a caller mistake, not a crop —
            // CropRenderer's rule, same words, because ClipInsideMap only
            // clamps the two low edges.
            if (rect.maxX < 0 || rect.maxZ < 0 || rect.minX >= map.Size.x || rect.minZ >= map.Size.z)
                throw new VerbArgsException(
                    $"rect [{rect.minX},{rect.minZ},{rect.Width},{rect.Height}] lies entirely outside "
                    + $"the {map.Size.x}x{map.Size.z} map");
            var t = new Targets
            {
                Kind = "rect",
                Detail = new Dictionary<string, object>
                {
                    ["kind"] = "rect",
                    ["rect"] = new List<object> { (double)rect.minX, (double)rect.minZ, (double)rect.Width, (double)rect.Height },
                },
            };
            var clipped = rect.ClipInsideMap(map);
            t.Requested = clipped.Area;
            foreach (var c in clipped)
            {
                if (t.Cells.Count >= maxCells) { t.Capped = true; break; }
                t.Cells.Add(c);
            }
            t.Detail["clipped"] = clipped.Width != rect.Width || clipped.Height != rect.Height;
            return t;
        }

        private static Targets FromCells(Map map, object raw, int maxCells)
        {
            if (!(raw is List<object> list))
                throw new VerbArgsException("cells must be an array of positions (\"x,z\", [x,z], landmark, pawn:<id>, thing:<id>)");
            var t = new Targets { Kind = "cells", Requested = list.Count };
            var seen = new HashSet<IntVec3>();
            foreach (var item in list)
            {
                if (t.Cells.Count >= maxCells) { t.Capped = true; break; }
                var c = Positions.Resolve(map, item);
                if (seen.Add(c)) t.Cells.Add(c);
            }
            t.Detail = new Dictionary<string, object> { ["kind"] = "cells", ["given"] = list.Count };
            return t;
        }

        private static Targets FromThings(Map map, object raw)
        {
            if (!(raw is List<object> list))
                throw new VerbArgsException("things must be an array of thingIDNumbers");
            var t = new Targets { Kind = "things", Requested = list.Count };
            var missing = new List<object>();
            var byId = new Dictionary<int, Thing>();
            var all = map.listerThings.AllThings;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null) byId[all[i].thingIDNumber] = all[i];
            foreach (var item in list)
            {
                if (!(item is double d))
                    throw new VerbArgsException("things must be an array of thingIDNumbers (numbers)");
                int id = (int)d;
                if (byId.TryGetValue(id, out var thing)) t.Things.Add(thing);
                else missing.Add((double)id);
            }
            t.Detail = new Dictionary<string, object> { ["kind"] = "things", ["given"] = list.Count };
            // Not an error: a thing the agent saw last glance may have been
            // hauled, eaten or destroyed while time ran. Named, not silent.
            if (missing.Count > 0) t.Detail["not_on_map"] = missing;
            return t;
        }

        // The `--area-things filter` form. Deliberately small and def-derived:
        // `category` uses the same fourteen words as `things` (2.4) and the
        // same ThingRequestGroups, so one vocabulary rather than two. The table
        // is duplicated rather than shared because ThingVerbs.Pool is private
        // and 3.4's worker is editing beside us — see the report.
        private static Targets FromFilter(Map map, object raw, int maxCells)
        {
            if (!(raw is Dictionary<string, object> obj))
                throw new VerbArgsException(
                    "filter must be an object: {def?, category?, rect?, forbidden?, faction?, max?}");
            var f = new VerbArgs(obj);
            string defName = f.Str("def");
            string category = f.Str("category");
            if (defName != null && category != null)
                throw new VerbArgsException("filter 'def' and 'category' are exclusive");

            List<Thing> pool;
            string source;
            if (defName != null)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName)
                    ?? throw new VerbArgsException($"no ThingDef named '{defName}'");
                // ThingsOfDef Log.ErrorOnce's on MinifiedThing and names the
                // group to use instead; a red error raised by agent-supplied
                // args breaches the zero-red-errors invariant (2.3/2.4's rule).
                pool = def == ThingDefOf.MinifiedThing
                    ? new List<Thing>(map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing))
                    : new List<Thing>(map.listerThings.ThingsOfDef(def));
                source = "def:" + def.defName;
            }
            else
            {
                pool = new List<Thing>(map.listerThings.ThingsInGroup(Group(category ?? "haulable", out source)));
                if ((category ?? "haulable") == "resources")
                {
                    var kept = new List<Thing>();
                    for (int i = 0; i < pool.Count; i++)
                        if (pool[i]?.def != null && pool[i].def.CountAsResource) kept.Add(pool[i]);
                    pool = kept;
                }
            }

            CellRect? within = f.Has("rect") ? ReadRect(f.Raw("rect")) : (CellRect?)null;
            bool? forbidden = f.Has("forbidden") ? f.Bool("forbidden", false) : (bool?)null;
            Faction faction = f.Has("faction") ? Dev.FactionArg(f.Str("faction")) : null;
            bool factionGiven = f.Has("faction");
            int max = f.Int("max", maxCells);
            if (max < 1) throw new VerbArgsException("filter 'max' must be >= 1");

            var t = new Targets { Kind = "filter", Requested = pool.Count };
            int fogged = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                var thing = pool[i];
                if (thing?.def == null || !thing.Spawned || thing.Map != map) continue;
                // Fog, here as everywhere: a thing in ground the colony has not
                // explored is not a target, and the count is published rather
                // than the thing being silently dropped (2.3's `nearest` shape).
                if (thing.Position.Fogged(map)) { fogged++; continue; }
                if (within.HasValue && !within.Value.Contains(thing.Position)) continue;
                if (forbidden.HasValue && thing.IsForbidden(Faction.OfPlayer) != forbidden.Value) continue;
                if (factionGiven && thing.Faction != faction) continue;
                if (t.Things.Count >= max) { t.Capped = true; break; }
                t.Things.Add(thing);
            }
            t.Detail = new Dictionary<string, object>
            {
                ["kind"] = "filter",
                ["source"] = source,
                ["pool"] = pool.Count,
                ["skipped_fogged"] = fogged,
            };
            if (within.HasValue)
                t.Detail["rect"] = new List<object>
                {
                    (double)within.Value.minX, (double)within.Value.minZ,
                    (double)within.Value.Width, (double)within.Value.Height,
                };
            return t;
        }

        public const string CategoryWords =
            "food|meds|apparel|weapons|drugs|corpses|chunks|art|plants|beds|buildings|resources|haulable|all";

        private static ThingRequestGroup Group(string category, out string source)
        {
            switch (category)
            {
                case "food": source = "group:FoodSourceNotPlantOrTree"; return ThingRequestGroup.FoodSourceNotPlantOrTree;
                case "meds": source = "group:Medicine"; return ThingRequestGroup.Medicine;
                case "apparel": source = "group:Apparel"; return ThingRequestGroup.Apparel;
                case "weapons": source = "group:Weapon"; return ThingRequestGroup.Weapon;
                case "drugs": source = "group:Drug"; return ThingRequestGroup.Drug;
                case "corpses": source = "group:Corpse"; return ThingRequestGroup.Corpse;
                case "chunks": source = "group:Chunk"; return ThingRequestGroup.Chunk;
                case "art": source = "group:Art"; return ThingRequestGroup.Art;
                case "plants": source = "group:Plant"; return ThingRequestGroup.Plant;
                case "beds": source = "group:Bed"; return ThingRequestGroup.Bed;
                case "buildings": source = "group:BuildingArtificial"; return ThingRequestGroup.BuildingArtificial;
                case "haulable": source = "group:HaulableEver"; return ThingRequestGroup.HaulableEver;
                case "all": source = "group:Everything"; return ThingRequestGroup.Everything;
                case "resources": source = "group:HaulableEver + ThingDef.CountAsResource"; return ThingRequestGroup.HaulableEver;
                default:
                    throw new VerbArgsException($"unknown filter category '{category}' ({CategoryWords})");
            }
        }

        // ------------------------------------------------------------------
        // REJECTIONS
        // ------------------------------------------------------------------
        // `why` is OUR short code (the tally key); `reason` is the GAME's own
        // AcceptanceReport string, verbatim, or null when the game gave none —
        // never a phrase of ours dressed up as the game's. That is the split
        // 2.6's find-rect already publishes, kept identical here so one
        // consumer reads both. `removal` + `blocker` come from Blockers.cs,
        // the ONE removal taxonomy: `mine` and `deconstruct` are the two values
        // that point straight back at verbs in this file.
        public sealed class Reject
        {
            public IntVec3 At;
            public Thing Thing;
            public string Why;
            public string Reason;
            // The OTHER designation standing here that made the gate say no —
            // see WhyAlreadyOther.
            public DesignationDef Other;
        }

        public const string WhyFogged = "fogged";

        // A REDUNDANT ORDER IS NOT AN IMPOSSIBLE ONE (git-bug 8b0b88f)
        // ---------------------------------------------------------------
        // The game's own gates do not distinguish them. `Designator_Mine
        // .CanDesignateCell` and `.CanDesignateThing` return
        // `AcceptanceReport.WasRejected` — reason `""` — for a target that
        // already carries the designation; `Designator_MineVein` does the same
        // with its own def; `Designator_Hunt.CanDesignateThing`,
        // `Designator_Plants.CanDesignateThing` and `Designator_Haul
        // .CanDesignateThing` return a bare `false`. `ReasonOf` turns `""`
        // into null — correctly, it refuses to invent words the game did not
        // say — so every one of those arrives as
        // `{why:"not-designatable", reason:null}`, which reads exactly like
        // "this rock is not mineable" or "this animal is not huntable". Those
        // are OPPOSITE corrections: one says stop asking, the other says aim
        // somewhere else. `rejects_by_reason.already-designated` is then a
        // free per-call count of wasted orders.
        public const string WhyAlready = "already-designated";

        // A THIRD KIND OF NO, and it was the residual 8b0b88f recorded and
        // deliberately left (git-bug 855117a closes it). `designate mine` over
        // a cell that already carries a MINE-VEIN designation reported
        // `not-designatable` — the same envelope as "this rock is not mineable"
        // — because `Designator_Mine.CanDesignateThing` rejects on a def that
        // is not this entry's:
        //
        //     if (!t.def.mineable) return false;
        //     if (DesignationAt(t.Position, Designation) != null) return WasRejected;
        //     if (DesignationAt(t.Position, DesignationDefOf.MineVein) != null) return WasRejected;
        //
        // The third clause is what this key names. It is not a re-implementation
        // of the gate — the gate has already spoken and this only re-keys a
        // rejection we were going to emit anyway (WhyAlready's rule) — and it is
        // not blind, because the clause is quoted above from the decompiled 1.6
        // source by member name, which is exactly what the gate rule asks for.
        //
        // A DISTINCT KEY, not folded into `already-designated`: both mean stop
        // asking, but this one also means "the work IS queued, under another
        // def", which is a different thing to tell an agent that is about to
        // conclude its mining order was dropped. The def itself rides on the
        // reject row as `designation_present`.
        public const string WhyAlreadyOther = "already-designated-other";

        // Is the designation ALREADY on this target? Asked only after the
        // game's gate has rejected, so the gate stays the sole authority on
        // what may be designated and the accept path is untouched — this only
        // re-keys a rejection we were going to emit anyway.
        //
        // HAZARD, and the reason this dispatches on `targetType` instead of
        // picking an accessor per verb: `DesignationManager.DesignationOn(
        // Thing, DesignationDef)` `Log.Error`s on a Cell-targeted def and
        // `DesignationManager.DesignationAt(IntVec3, DesignationDef)`
        // `Log.Error`s on a Thing-targeted def (Verse/DesignationManager.cs,
        // both members). THE TABLE IS NOT UNIFORM: Mine and MineVein are
        // TargetType.Cell — `Designator_Mine.CanDesignateCell` and
        // `Designator_MineVein.CanDesignateThing` call `DesignationAt` with
        // them — while Hunt, HarvestPlant, CutPlant, Haul and Flick are
        // TargetType.Thing, called through `DesignationOn` by
        // `Designator_Hunt.CanDesignateThing` and
        // `Designator_Plants.CanDesignateThing`. Backwards is a RED ERROR in
        // the log, not a silent null, and a red error breaches the
        // zero-red-errors invariant — so the wrong check here would be worse
        // than no check. `def.targetType` is the game's OWN discriminator, the
        // one `DesignationManager.AddDesignation` and `IndexDesignation`
        // switch on, which makes a swapped pair unrepresentable rather than
        // merely untested.
        public static bool AlreadyDesignated(Map map, DesignationDef def, IntVec3 cell, Thing thing)
        {
            if (def == null || map == null || map.designationManager == null) return false;
            var mgr = map.designationManager;
            try
            {
                if (def.targetType == TargetType.Thing)
                {
                    if (thing != null) return mgr.DesignationOn(thing, def) != null;
                    // A CELL aimed at a THING designation — `designate chop
                    // --rect` over an already-marked forest is the common case.
                    // `IntVec3.GetThingList` is `ThingGrid.ThingsListAt`, the
                    // live grid list returned by reference: nothing lazily
                    // built, nothing shared-and-refilled like
                    // `DesignationManager.AllDesignationsAt`'s static (Class E),
                    // and we only read it.
                    if (!cell.IsValid || !cell.InBounds(map)) return false;
                    var list = cell.GetThingList(map);
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] != null && mgr.DesignationOn(list[i], def) != null) return true;
                    return false;
                }
                if (def.targetType == TargetType.Cell)
                {
                    // Cell designations are indexed by location only, so a
                    // THING target asks about the cell it stands on — which is
                    // what `Designator_Mine.CanDesignateThing` itself does
                    // (`DesignationAt(t.Position, Designation)`).
                    var c = thing != null ? thing.Position : cell;
                    if (!c.IsValid || !c.InBounds(map)) return false;
                    return mgr.DesignationAt(c, def) != null;
                }
            }
            catch { }
            return false;
        }

        // WHICH other designation the gate tripped on, when it was one. Its own
        // key rather than a sentence stuffed into `reason`: `reason` is the
        // GAME's own AcceptanceReport string verbatim or null, and this file's
        // REJECTIONS contract forbids inventing words the game did not say.
        // The def name is a fact, not a phrase.
        private static object PresentDef(Reject r) => r.Other?.defName;

        public static Dictionary<string, object> RejectOut(Map map, Reject r)
        {
            var d = new Dictionary<string, object>
            {
                ["at"] = r.At.IsValid ? Positions.Out(r.At) : null,
                ["why"] = r.Why,
                ["reason"] = string.IsNullOrEmpty(r.Reason) ? null : r.Reason,
            };
            if (r.Other != null) d["designation_present"] = PresentDef(r);
            if (r.Thing != null)
            {
                d["id"] = r.Thing.thingIDNumber;
                d["def"] = r.Thing.def?.defName;
                d["label"] = WorldSafe.Safe(() => r.Thing.LabelShort);
                Blockers.Classify(r.Thing, out string removal, out string blockerReason);
                d["removal"] = removal;
                if (d["reason"] == null && blockerReason != null) d["reason"] = blockerReason;
            }
            else
            {
                d["removal"] = Blockers.None;
                // What is standing on the cell, and how it clears — the field
                // 3.3's place-layout preflight and this spec's own mine/
                // deconstruct verbs consume. A `mine` rejected on a player wall
                // says "deconstruct", which is the next call to make.
                var edifice = r.At.IsValid && r.At.InBounds(map) ? r.At.GetEdifice(map) : null;
                if (edifice != null) d["blocker"] = Blockers.Describe(edifice);
            }
            return d;
        }

        // Cap + tally, the truncation contract: the LIST is capped, the tally
        // is complete, and `rejects_more` says what the cap hid.
        public static void PublishRejects(Map map, List<Reject> rejects, Dictionary<string, object> into)
        {
            var list = new List<object>();
            var tally = new Dictionary<string, int>();
            for (int i = 0; i < rejects.Count; i++)
            {
                string key = rejects[i].Why ?? "rejected";
                tally[key] = tally.TryGetValue(key, out var n) ? n + 1 : 1;
                if (list.Count < RejectCap) list.Add(RejectOut(map, rejects[i]));
            }
            var byReason = new Dictionary<string, object>();
            foreach (var kv in tally) byReason[kv.Key] = kv.Value;
            into["rejected"] = rejects.Count;
            into["rejects"] = list;
            into["rejects_more"] = Math.Max(0, rejects.Count - list.Count);
            into["rejects_by_reason"] = byReason;
        }

        // An AcceptanceReport's own words, or null. `AcceptanceReport.Reason`
        // is "" for a bare `false`, and "" must not read as a reason.
        public static string ReasonOf(AcceptanceReport report)
            => string.IsNullOrEmpty(report.Reason) ? null : report.Reason;

        // ------------------------------------------------------------------
        // THE DESIGNATOR LOOP
        // ------------------------------------------------------------------
        // CanDesignateCell/Thing then DesignateSingleCell/Thing, per target,
        // with our fog gate in front. Never DesignateMultiCell — it re-enters
        // TutorSystem and (for zones) drives Find.Selector.
        //
        // `designation` is the def the designator ADDS, taken from the caller's
        // own table — passed so a rejection can be told apart from a redundancy
        // (see WhyAlready). Optional: a designator that adds no designation
        // (claim, smooth, the area brushes) passes null and nothing changes.
        //
        // `blockedBy` is the OTHER designations whose presence this
        // designator's gate rejects on — `Designator_Mine.CanDesignateThing`'s
        // third clause, and the only entry in the table with one. Asked only
        // after the gate has rejected AND the entry's own def is not present,
        // so it never touches the accept path. See WhyAlreadyOther.
        public static void RunCells(Map map, Designator des, List<IntVec3> cells, bool dryRun,
            List<IntVec3> accepted, List<Reject> rejects, DesignationDef designation = null,
            DesignationDef[] blockedBy = null)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                if (!c.InBounds(map))
                {
                    rejects.Add(new Reject { At = c, Why = "out-of-bounds" });
                    continue;
                }
                if (c.Fogged(map))
                {
                    rejects.Add(new Reject { At = c, Why = WhyFogged, Reason = Blockers.FoggedReason });
                    continue;
                }
                AcceptanceReport report;
                try { report = des.CanDesignateCell(c); }
                catch (Exception e)
                {
                    rejects.Add(new Reject { At = c, Why = "designator-threw", Reason = e.Message });
                    continue;
                }
                if (!report.Accepted)
                {
                    // `reason` stays the game's own words verbatim even here —
                    // this file's REJECTIONS contract — and for every type in
                    // the table it is null at this branch, with ONE exception,
                    // recorded rather than papered over:
                    //
                    // KNOWN MISATTRIBUTION. `RimWorld/Designator_Hunt
                    // .CanDesignateCell` answers "MessageMustDesignateHuntable"
                    // when the true cause is already-designated, because its
                    // `HuntablesInCell` filters through `CanDesignateThing`,
                    // which drops animals that already carry the Hunt
                    // designation — so a cell whose animals are all already
                    // marked looks to it like a cell with no huntables in it.
                    // When `why` is already-designated, `why` is the
                    // classification and that `reason` answers a different
                    // question. It is kept anyway: a reason we deleted or
                    // invented would be worse than the game's own inaccurate
                    // one, and `why` is what `rejects_by_reason` keys on.
                    bool already = AlreadyDesignated(map, designation, c, null);
                    var other = already ? null : OtherPresent(map, blockedBy, c, null);
                    rejects.Add(new Reject
                    {
                        At = c,
                        Why = already ? WhyAlready : (other != null ? WhyAlreadyOther : "not-designatable"),
                        Reason = ReasonOf(report),
                        Other = other,
                    });
                    continue;
                }
                if (!dryRun)
                {
                    try { des.DesignateSingleCell(c); }
                    catch (Exception e)
                    {
                        rejects.Add(new Reject { At = c, Why = "designate-threw", Reason = e.Message });
                        continue;
                    }
                }
                accepted.Add(c);
            }
        }

        // The first of `blockedBy` that is present on this target, or null.
        // Uses the same `AlreadyDesignated` dispatch, so a Cell-targeted def
        // asked through a thing and vice versa is still the game's own
        // `targetType` discriminator and never a Log.Error.
        public static DesignationDef OtherPresent(Map map, DesignationDef[] blockedBy,
            IntVec3 cell, Thing thing)
        {
            if (blockedBy == null) return null;
            for (int i = 0; i < blockedBy.Length; i++)
            {
                var d = blockedBy[i];
                if (d == null) continue;
                if (AlreadyDesignated(map, d, cell, thing)) return d;
            }
            return null;
        }

        public static void RunThings(Map map, Designator des, List<Thing> things, bool dryRun,
            List<Thing> accepted, List<Reject> rejects, DesignationDef designation = null,
            DesignationDef[] blockedBy = null)
        {
            for (int i = 0; i < things.Count; i++)
            {
                var t = things[i];
                if (t == null) continue;
                // WorldSafe.Hidden folds "unspawned", "wrong map" and "fogged"
                // into one test — the same route every 2.x serializer takes.
                if (WorldSafe.Hidden(t, map))
                {
                    rejects.Add(new Reject
                    {
                        At = t.PositionHeld,
                        Thing = t,
                        Why = t.Spawned && t.Map == map ? WhyFogged : "not-on-map",
                        Reason = t.Spawned && t.Map == map ? Blockers.FoggedReason : null,
                    });
                    continue;
                }
                AcceptanceReport report;
                try { report = des.CanDesignateThing(t); }
                catch (Exception e)
                {
                    rejects.Add(new Reject { At = t.Position, Thing = t, Why = "designator-threw", Reason = e.Message });
                    continue;
                }
                if (!report.Accepted)
                {
                    // See RunCells: the gate has spoken, we only say WHICH kind
                    // of no it was. `Designator_Hunt`, `Designator_Plants` and
                    // `Designator_Haul` all return a bare `false` here for an
                    // already-designated thing, so `reason` is null either way
                    // and `why` carries the whole distinction.
                    bool already = AlreadyDesignated(map, designation, t.Position, t);
                    var other = already ? null : OtherPresent(map, blockedBy, t.Position, t);
                    rejects.Add(new Reject
                    {
                        At = t.Position,
                        Thing = t,
                        Why = already ? WhyAlready : (other != null ? WhyAlreadyOther : "not-designatable"),
                        Reason = ReasonOf(report),
                        Other = other,
                    });
                    continue;
                }
                if (!dryRun)
                {
                    try { des.DesignateThing(t); }
                    catch (Exception e)
                    {
                        rejects.Add(new Reject { At = t.Position, Thing = t, Why = "designate-threw", Reason = e.Message });
                        continue;
                    }
                }
                accepted.Add(t);
            }
        }

        // The game's own end-of-drag step, on success only — see the class
        // header for why it is neither always called nor never called.
        public static void FinalizeSucceeded(Designator des, bool anyAccepted)
        {
            if (!anyAccepted) return;
            try { des.Finalize(somethingSucceeded: true); }
            catch (Exception e)
            {
                Journal.EmitWarning("3.2: " + des.GetType().Name + ".Finalize threw: " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // ECHO
        // ------------------------------------------------------------------
        // "Every mutation echoes evidence (before/after crop for spatial
        // verbs)" — DESIGN §Action model. The crop is centred on the bounding
        // box of everything the call TOUCHED (accepted and rejected alike, so a
        // total rejection still shows the ground it was aimed at), clamped to
        // CropRenderer's odd MaxSide, and it carries the `designations` layer:
        // that layer is an overlay above things (2.6's correction), which is
        // exactly what makes a fresh Mine designation on a rock wall visible as
        // `*` rather than hidden under `%`.
        public static readonly IReadOnlyList<string> EchoLayers =
            new[] { "terrain", "things", "zones", "designations", "pawns" };

        public static object Echo(Map map, IEnumerable<IntVec3> cells)
        {
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            bool any = false;
            foreach (var c in cells)
            {
                if (!c.IsValid) continue;
                any = true;
                if (c.x < minX) minX = c.x;
                if (c.z < minZ) minZ = c.z;
                if (c.x > maxX) maxX = c.x;
                if (c.z > maxZ) maxZ = c.z;
            }
            if (!any) return null;
            // One cell of margin so the echo shows context, then clamp each
            // side to the renderer's cap around the CENTRE of what we touched —
            // a 200-cell drag echoes its middle rather than failing.
            minX--; minZ--; maxX++; maxZ++;
            int w = maxX - minX + 1, h = maxZ - minZ + 1;
            int cap = CropRenderer.MaxSide;
            if (w > cap) { int cx = (minX + maxX) / 2; minX = cx - cap / 2; w = cap; }
            if (h > cap) { int cz = (minZ + maxZ) / 2; minZ = cz - cap / 2; h = cap; }
            var rect = new CellRect(minX, minZ, w, h);
            if (rect.maxX < 0 || rect.maxZ < 0 || rect.minX >= map.Size.x || rect.minZ >= map.Size.z) return null;
            try { return CropRenderer.Render(map, rect, new List<string>(EchoLayers)); }
            catch (Exception e)
            {
                Journal.EmitWarning("3.2: crop echo failed: " + e.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // OUTPUT HELPERS
        // ------------------------------------------------------------------
        public static List<object> CellsOut(List<IntVec3> cells, out int more)
        {
            var list = new List<object>();
            for (int i = 0; i < cells.Count && i < ListCap; i++) list.Add(Positions.Out(cells[i]));
            more = Math.Max(0, cells.Count - list.Count);
            return list;
        }

        public static List<object> IdsOut(List<Thing> things, out int more)
        {
            var list = new List<object>();
            for (int i = 0; i < things.Count && i < ListCap; i++) list.Add(things[i].thingIDNumber);
            more = Math.Max(0, things.Count - list.Count);
            return list;
        }

        // A one-line human string for the journal's `target` field.
        public static string Describe(Targets t)
        {
            if (t.IsThings)
                return t.Things.Count + " thing(s)" + (t.Things.Count > 0 ? " from " + t.Kind : "");
            if (t.Cells.Count == 0) return "0 cells";
            return t.Cells.Count + " cell(s) around " + Show(t.Cells[0]);
        }

        public static string Show(IntVec3 c) => "[" + c.x + "," + c.z + "]";

        // How many designations of this def stand on the map now. `AllDesignations`
        // is a shared static list (Class E) — SpawnedDesignationsOfDef enumerates
        // the per-def list instead and is the cheap, safe route.
        public static int CountOf(Map map, DesignationDef def)
        {
            if (def == null) return -1;
            int n = 0;
            try
            {
                foreach (var d in map.designationManager.SpawnedDesignationsOfDef(def)) { if (d != null) n++; }
            }
            catch { return -1; }
            return n;
        }

        // ------------------------------------------------------------------
        // WHAT THE CALL ACTUALLY PUT ON THE MAP        (git-bug b7359fa, 855117a)
        // ------------------------------------------------------------------
        // `accepted` is the count of TARGETS the game's gate took, and for one
        // designator in the table that is NOT the count of designations
        // created. `Designator_MineVein.DesignateSingleCell` calls
        // `FloodFillDesignations`, which paints `MineVein` over every
        // contiguous non-fogged cell whose edifice def matches — so ONE
        // accepted cell can designate a whole vein, and every later cell in the
        // same drag then comes back already-designated and is REJECTED. A
        // report keyed on `accepted` would say "1" about a call that created
        // forty designations, and a reach or composition rollup built on it
        // would be measuring the wrong set.
        //
        // So for a CELL-targeted designation the subject is the DELTA: the
        // cells carrying this def after the call, minus the cells carrying it
        // before. For a THING-targeted one the accepted things are exact and
        // are used directly. `designations_before`/`designations_now` have
        // always reported the same truth as a pair of counts; this is that
        // pair as a SET, so the cells themselves can be looked at.
        public sealed class Landed
        {
            public readonly List<IntVec3> Cells = new List<IntVec3>();
            public readonly List<Thing> Things = new List<Thing>();
            public bool IsThings;
            public string Source;
            public int Count => IsThings ? Things.Count : Cells.Count;
        }

        // The cells carrying `def` right now, or null when the question does
        // not apply (no def, or a Thing-targeted def, whose designations are
        // not addressed by cell). Copied out immediately: see the class
        // header's Class E note on the manager's shared statics —
        // `SpawnedDesignationsOfDef` iterates the per-def list, which is the
        // cheap and safe route, but nothing may be held across another call in.
        public static HashSet<IntVec3> CellSnapshot(Map map, DesignationDef def)
        {
            if (def == null || map?.designationManager == null) return null;
            if (def.targetType != TargetType.Cell) return null;
            var set = new HashSet<IntVec3>();
            try
            {
                foreach (var d in map.designationManager.SpawnedDesignationsOfDef(def))
                    if (d != null) set.Add(d.target.Cell);
            }
            catch { return null; }
            return set;
        }

        // `before` is CellSnapshot's answer from before the designator ran, or
        // null when it did not apply. A null `before` falls back to the
        // accepted set, and `Source` says which reading the caller got — the
        // two are the same number for every designator except mine-vein, and a
        // consumer that cannot tell them apart cannot tell a flood-fill from a
        // straight drag.
        //
        // A DRY RUN HAS NO DELTA, and taking one anyway would be the bug: the
        // designator wrote nothing, so after == before, so the landed set is
        // EMPTY and every rollup built on it reports zero for a call that
        // accepted forty targets. `dry_run` is how the playbook tells an agent
        // to re-check standing orders after an area change; a `reach` and a
        // `composition` computed over nothing would make that advice useless.
        // So a dry run reports the ACCEPTED set — what would land — and says so
        // in `Source`, including the one way it under-reports.
        public static Landed LandedOf(Map map, DesignationDef def, Targets targets,
            List<IntVec3> acceptedCells, List<Thing> acceptedThings, HashSet<IntVec3> before,
            bool dryRun)
        {
            var l = new Landed { IsThings = targets.IsThings };
            if (targets.IsThings)
            {
                l.Things.AddRange(acceptedThings);
                l.Source = dryRun ? "accepted things (dry run: what WOULD be designated)"
                    : "accepted things";
                return l;
            }
            if (dryRun)
            {
                l.Cells.AddRange(acceptedCells);
                l.Source = "accepted cells (dry run: what WOULD be designated — a "
                    + "flood-filling designator such as mine-vein would add more, and "
                    + "nothing here can know how many without writing)";
                return l;
            }
            var after = before == null ? null : CellSnapshot(map, def);
            if (before == null || after == null)
            {
                l.Cells.AddRange(acceptedCells);
                l.Source = def == null
                    ? "accepted cells (this designator adds no designation)"
                    : "accepted cells (no cell-indexed designation to diff)";
                return l;
            }
            foreach (var c in after) if (!before.Contains(c)) l.Cells.Add(c);
            l.Source = "designation delta (" + def.defName + " cells now, minus before)";
            return l;
        }

        public static Map Map()
            => Find.CurrentMap ?? throw new VerbArgsException("no current map");

        public static int MaxCellsArg(VerbArgs a)
        {
            int max = a.Int("max_cells", DefaultMaxCells);
            if (max < 1 || max > MaxCellsCeiling)
                throw new VerbArgsException($"max_cells must be 1..{MaxCellsCeiling}");
            return max;
        }
    }
}
