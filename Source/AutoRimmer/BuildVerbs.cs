using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ============================================================ spec 3.3 ===
    // `build {def, pos|at, rot?, stuff?}` — THE PLAYER'S HAND.
    //
    // The first verb in this mod that adds a building to the colony without
    // being a god-hand. Everything before it either observed, designated
    // something already on the map, or was `dev:*`. M1's measurement is the
    // argument for it (git-bug 1adc737 #6): three colonists, six in-game days,
    // `Alert_ColonistsIdle` up for most of the run, four dev-staged buildings as
    // the entire colony, and a destroyed research bench that could not be
    // replaced — because no player verb could place a blueprint.
    //
    // ---------------------------- WHAT IT IS NOT -----------------------------
    // It is NOT a second placement gate. `Source/AutoRimmer/Siting.cs`'s
    // `SiteGate` is the one routine — `GenConstruct.CanPlaceBlueprintAt(
    // godMode:false)` plus `Designator_Build.Visible`'s ten clauses — and
    // `site-survey`, `find-rect {def}`, `dev:spawn-thing {buildable:true}`,
    // `site-audit` and this verb all ASK it. That is what makes git-bug
    // c718e4a's acceptance true rather than nearly true: a survey's verdict and
    // this verb's refusal for the same arguments are the same sentence because
    // they are the same call.
    //
    // AND IT GETS NO godMode BYPASS, ON ANY SETTING, EVER (git-bug 1adc737 #12,
    // resolved by the orchestrator 2026-09-01). `Designator_Build.Visible`'s
    // first clause is `if (DebugSettings.godMode) return true;` and `SiteGate`
    // reports that flag without honouring it, because honouring it would turn
    // this verb into a god-hand the moment a dev session left the flag on —
    // silently, with `ok:true` to show for it, which is strictly worse than no
    // gate because it looks like one. A caller that wants the bypass asks
    // `dev:spawn-thing`, which says so in its own envelope.
    //
    // ------------------------ THE MODEL, MEMBER BY MEMBER --------------------
    // `RimWorld/Designator_Build.DesignateSingleCell` is the thing being
    // reproduced, and it is reproduced in its own order:
    //
    //   1. destroy any Frame on the cell whose `replaceTags` intersect
    //      `def.blueprintDef.replaceTags` (`DestroyMode.Cancel`);
    //   2. if `WorkToBuild == 0`, place the finished thing directly — vanilla's
    //      own branch, and NOT a cheat (see below);
    //   3. otherwise `GenSpawn.WipeExistingThings(pos, rot, def.blueprintDef,
    //      map, DestroyMode.Deconstruct)` and then
    //      `GenConstruct.PlaceBlueprintForBuild(def, pos, map, rot,
    //      Faction.OfPlayer, stuff)`;
    //   4. `def.PlaceWorkers[i].PostPlace(map, def, pos, rot)`.
    //
    // FOUR THINGS VANILLA DOES THAT THIS DELIBERATELY DOES NOT.
    // `FleckMaker.ThrowMetaPuffs` is a particle effect. `TutorSystem.
    // AllowAction`/`Notify_Event` is the tutorial's veto and belongs to a UI
    // session that does not exist here. `PlayerKnowledgeDatabase.
    // KnowledgeDemonstrated(ConceptDefOf.BuildOrbitalTradeBeacon, …)` WRITES
    // scribed knowledge state, which an agent's placement has no business
    // touching. And style/precept/glower come off a selected designator's
    // fields; there is no designator, and `1adc737`'s stuff-map open question is
    // where a style argument would land if one is ever wanted.
    //
    // WHY THE ZERO-WORK BRANCH IS NOT A GOD-HAND. Vanilla's condition is
    // `DebugSettings.godMode || entDef.GetStatValueAbstract(StatDefOf.
    // WorkToBuild, StuffDef) == 0f`. The first disjunct is dropped (see above);
    // the second is what a PLAYER clicking the architect menu gets, and skipping
    // it would make `build` refuse to do what the game does — a blueprint that
    // needs no work is a blueprint no pawn will ever be dispatched to. `mode` in
    // the result says which branch ran, so the two are never confused.
    // Reading the stat is `Designator_Build`'s own per-frame call under the
    // cursor (`DrawPlaceMouseAttachments` reads `CostListAdjusted` beside it),
    // so it is the same cost the game pays to draw the tooltip.
    //
    // ------------------------- THE PLACEMENT ID ------------------------------
    // Every placement publishes one, because COMPLETION IS AN ABSENCE (DESIGN
    // decisions log, 2026-09-01). A finished build leaves no blueprint and no
    // frame — `Blueprint.TryReplaceWithSolidThing` turns the blueprint into a
    // `Frame` and `Frame.CompleteConstruction` turns the frame into the building
    // and destroys itself — so a read that only enumerates live blueprints and
    // frames reports "finished" and "cancelled" identically, as nothing. The id
    // is the handle the answer hangs on; `Placements` below is the registry and
    // `construction` is the read.
    public static class BuildVerbs
    {
        // How far outside the footprint the echoed crop reaches. Small on
        // purpose: `site-survey` is the verb for looking, this is the verb for
        // acting, and the echo exists so the agent can see the blueprint landed
        // where it asked without a second round trip.
        private const int EchoMargin = 3;

        [Verb("build")]
        public static object Build(VerbContext ctx)
        {
            var map = Find.CurrentMap ?? throw new VerbArgsException("no current map");
            var a = ctx.Args;

            var def = SiteGate.Named(a.StrReq("def"));
            // IS THERE A DESIGNATOR FOR THIS AT ALL. The architect menu is built
            // by `Verse/DesignationCategoryDef.ResolveDesignators`, whose generic
            // pass is `from tDef in TerrainDef+ThingDef where tDef
            // .designationCategory == this && tDef.canGenerateDefaultDesignator`
            // — so a null `designationCategory` means no `Designator_Build` for
            // the def exists anywhere and the player has no way to place it.
            // Without this test `build {def:"Steel"}` reaches the zero-work
            // branch (a resource has no WorkToBuild stat, so the abstract value
            // is the stat's default) and god-hands a steel stack into existence
            // through the one verb that is supposed to be a player's hand.
            if (def.designationCategory == null)
                throw new VerbArgsException(
                    $"'{def.defName}' has no designationCategory, so no Designator_Build exists for "
                    + "it and no player can place it (Verse/DesignationCategoryDef"
                    + ".ResolveDesignators). It may still be spawnable — that is dev:spawn-thing.");
            var stuff = SiteVerbs.ResolveStuff(def, a.Str("stuff"));
            // `def.defaultPlacingRot`, not Rot4.North: this verb models
            // Designator_Build, which starts there, and 76 vanilla defs set it
            // to something other than North. `dev:spawn-thing` keeps North
            // because it models DebugThingPlaceHelper.DebugSpawn. Two verbs, two
            // models — DESIGN records the split (2026-09-01).
            var rot = Rotations.Arg(a, "rot", def.defaultPlacingRot);

            // Exactly `site-survey`'s convention, and the same refusal, because
            // the whole point of the pair is that a caller surveys and then
            // builds with the arguments it surveyed.
            bool hasPos = a.Has("pos"), hasAt = a.Has("at");
            if (hasPos && hasAt)
                throw new VerbArgsException(
                    "pass 'pos' (the game's placement centre) or 'at' (the footprint's "
                    + "south-west corner), not both — they are different conventions and "
                    + "for an even-sized def they name different cells");
            if (!hasPos && !hasAt)
                throw new VerbArgsException("build needs 'pos' or 'at'");

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

            bool dryRun = a.Bool("dry_run", false);

            // ------------------------------------------------------- gate ----
            var verdict = SiteGate.Check(map, def, pos, rot, stuff);
            var data = verdict.Out();
            data["pos_source"] = posSource;
            data["rot_source"] = a.Has("rot") ? "arg" : "def.defaultPlacingRot";
            data["interaction_cells"] = OutCells(SiteVerbs.InteractionCells(def, pos, rot, map));

            if (!verdict.Ok || dryRun)
            {
                data["placed"] = false;
                data["placement_id"] = null;
                data["dry_run"] = dryRun;
                if (!verdict.Ok)
                {
                    // The refusal, in the same row shape every other refusal in
                    // this mod uses — `{at, role, ok, reason, blocker}` — so a
                    // caller can hand it to the same code that reads
                    // `site-survey`'s tiers and `dev:spawn-thing`'s `failed[]`.
                    // `reason` at the top level is the game's own sentence
                    // verbatim, which is the half c718e4a's acceptance measures.
                    var row = verdict.PlaceOk
                        ? null
                        : SiteVerbs.FirstRefusingRow(map, def, stuff, pos, rot);
                    data["refused"] = new Dictionary<string, object>
                    {
                        // WHICH HALF. "would be refused" and "cannot even be
                        // selected" are different news and must not read alike.
                        ["half"] = verdict.PlaceOk ? "selectable" : "verdict",
                        ["reason"] = verdict.PlaceOk
                            ? verdict.SelectableDetail
                            : verdict.PlaceReason,
                        ["clause"] = verdict.PlaceOk ? verdict.SelectableClause : null,
                        ["cell"] = row,
                    };
                }
                data["view"] = Echo(map, verdict.Rect);
                return data;
            }

            // ------------------------------------------------------ place ----
            string mode;
            Thing produced = null;
            var cleared = new List<object>();
            int clearedMore = 0;

            // 1. A Frame this def is allowed to REPLACE goes first, and it is
            //    destroyed rather than wiped: DestroyMode.Cancel is what
            //    Designator_Build passes, and it refunds the frame's contents to
            //    the player instead of vanishing them.
            foreach (var frame in ReplaceableFrames(map, def, pos))
            {
                cleared.Add(Blockers.Describe(frame));
                try { frame.Destroy(DestroyMode.Cancel); } catch { }
            }

            float work = 0f;
            try { work = def.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff); } catch { }

            if (work == 0f)
            {
                mode = "instant-zero-work";
                produced = PlaceZeroWork(map, def, stuff, pos, rot);
            }
            else
            {
                mode = "blueprint";
                var bpDef = def.blueprintDef;
                if (bpDef == null)
                    throw new VerbArgsException(
                        $"'{def.defName}' has no blueprintDef, so there is nothing for a colonist "
                        + "to build — this is a def that can only be spawned (dev:spawn-thing)");
                // 3a. What the blueprint DECONSTRUCTS to make room, captured
                //     before and confirmed after. Note the wipe is keyed on the
                //     BLUEPRINT def and the mode is Deconstruct, not Vanish:
                //     unlike the god-hand path this refunds, because it is what
                //     the player's own designator does.
                var watch = WipeWatch.Before(map, bpDef, pos, rot);
                try { GenSpawn.WipeExistingThings(pos, rot, bpDef, map, DestroyMode.Deconstruct); }
                catch { }
                cleared.AddRange(watch.Destroyed());
                clearedMore += watch.Skipped;
                produced = GenConstruct.PlaceBlueprintForBuild(def, pos, map, rot,
                    Faction.OfPlayer, stuff);
            }

            // 4. PostPlace, wrapped: a PlaceWorker is third-party code for a
            //    modded def, and `PlaceWorker_SunLamp`-style hooks are how the
            //    game attaches a matching growing zone to a placement.
            try
            {
                var workers = def.PlaceWorkers;
                if (workers != null)
                    for (int i = 0; i < workers.Count; i++)
                        try { workers[i].PostPlace(map, def, pos, rot); }
                        catch { }
            }
            catch { }

            // ---------------------------------------------------- publish ----
            var placement = Placements.Record(map, def, stuff, pos, rot, mode, produced);

            var target = def.defName + (stuff != null ? " (" + stuff.defName + ")" : "")
                + " @ " + pos.x + "," + pos.z + " " + rot.ToStringWord()
                + " [" + mode + "]";
            var payload = new Dictionary<string, object>
            {
                ["verb"] = "build",
                ["step"] = mode,
                ["target"] = target,
                ["placement_id"] = placement.Id,
                ["def"] = def.defName,
                ["stuff"] = stuff?.defName,
                ["at"] = Positions.Out(pos),
                ["rot"] = rot.ToStringWord(),
                ["pos_source"] = posSource,
                ["footprint"] = Footprint.Out(verdict.Rect),
                ["gate"] = SiteGate.GateId,
                ["thing_id"] = produced?.thingIDNumber,
            };
            if (cleared.Count > 0)
            {
                payload["cleared"] = cleared;
                payload["cleared_mode"] = "Deconstruct";
            }
            int tick = 0;
            try { tick = Find.TickManager.TicksGame; } catch { }
            long seq = Journal.Emit("action", payload, tick);
            placement.JournalSeq = seq;

            data["placed"] = true;
            data["dry_run"] = false;
            data["mode"] = mode;
            data["placement_id"] = placement.Id;
            data["thing"] = produced == null ? null : new Dictionary<string, object>
            {
                ["id"] = produced.thingIDNumber,
                ["def"] = produced.def?.defName,
                ["kind"] = Placements.KindOf(produced),
                ["at"] = produced.Spawned ? Positions.Out(produced.Position) : null,
            };
            // What a colonist will have to haul before anything happens. Read
            // through the guarded route, never `Frame.TotalMaterialCost()` —
            // see Placements.Materials for the red-error hazard.
            data["materials"] = Placements.Materials(def, stuff, out string materialsNote);
            if (materialsNote != null) data["materials_note"] = materialsNote;
            if (cleared.Count > 0)
            {
                data["cleared"] = cleared;
                data["cleared_mode"] =
                    "Deconstruct — the blueprint's own wipe mode (Designator_Build passes it), so "
                    + "what it removed was REFUNDED. dev:spawn-thing's `wiped` is Vanish and is not.";
                if (clearedMore > 0) data["cleared_more"] = clearedMore;
            }
            data["view"] = Echo(map, verdict.Rect);
            data["journal_seq"] = seq;
            if (seq == 0)
                data["provenance"] = "NOT WRITTEN — the journal writer is closed, so this placement "
                    + "has no journal line. `construction` can still answer for the id in this "
                    + "session; nothing outside it can.";
            return data;
        }

        // --------------------------------------------------------- helpers --

        // The Frames on this cell that this def is allowed to replace, taken from
        // `Designator_Build.DesignateSingleCell`'s own first loop. The tag test
        // is spelled out rather than routed through
        // `GenCollection.NotNullAndContainsAnyElement` so a null on either side
        // is visibly a no-match rather than a silent extension-method behaviour.
        private static List<Thing> ReplaceableFrames(Map map, BuildableDef def, IntVec3 pos)
        {
            var found = new List<Thing>();
            var mine = def.blueprintDef?.replaceTags;
            if (mine == null || mine.Count == 0) return found;
            var list = map.thingGrid.ThingsListAtFast(pos);
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (!(t is Frame) || t.def?.replaceTags == null) continue;
                for (int j = 0; j < mine.Count; j++)
                    if (t.def.replaceTags.Contains(mine[j])) { found.Add(t); break; }
            }
            return found;
        }

        // Vanilla's zero-work branch, minus godMode and minus the style/glower
        // fields a designator would have carried. `RemoveTempTerrain` before the
        // three-way terrain set is the game's own order, and
        // `RemoveTopLayer(c, !godMode)` becomes `RemoveTopLayer(c, true)` — the
        // player's case, which drops the removed floor's leavings.
        private static Thing PlaceZeroWork(Map map, BuildableDef def, ThingDef stuff,
            IntVec3 pos, Rot4 rot)
        {
            if (def is TerrainDef terrain)
            {
                map.terrainGrid.RemoveTempTerrain(pos);
                if (terrain.isFoundation)
                {
                    if (map.terrainGrid.CanRemoveTopLayerAt(pos))
                        map.terrainGrid.RemoveTopLayer(pos, doLeavings: true);
                    map.terrainGrid.SetFoundation(pos, terrain);
                }
                else if (terrain.temporary) map.terrainGrid.SetTempTerrain(pos, terrain);
                else map.terrainGrid.SetTerrain(pos, terrain);
                return null;
            }
            var thing = ThingMaker.MakeThing((ThingDef)def, stuff);
            thing.SetFactionDirect(Faction.OfPlayer);
            return GenSpawn.Spawn(thing, pos, map, rot);
        }

        // The picture, in `map-view`'s alphabet and nothing else — no overlay.
        // `site-survey` is where the tier overlay lives; duplicating it here
        // would be a second grid with the same identity and a different producer,
        // which is exactly what CropRenderer.AlphabetId's discipline is against.
        private static Dictionary<string, object> Echo(Map map, CellRect footprint)
        {
            var view = footprint.ExpandedBy(EchoMargin);
            int margin = EchoMargin;
            while ((view.Width > CropRenderer.MaxSide || view.Height > CropRenderer.MaxSide)
                   && margin > 0)
                view = footprint.ExpandedBy(--margin);
            return CropRenderer.Render(map, view.ClipInsideMap(map),
                new List<string>(CropRenderer.DefaultLayers));
        }

        private static List<object> OutCells(List<IntVec3> cells)
        {
            var list = new List<object>();
            for (int i = 0; i < cells.Count; i++) list.Add(Positions.Out(cells[i]));
            return list;
        }
    }

    // ========================================================== Placements ===
    // THE REGISTRY THAT MAKES COMPLETION READABLE.
    //
    // A finished build leaves NOTHING behind: `Blueprint.TryReplaceWithSolidThing`
    // turns the blueprint into a `Frame` (via `Blueprint_Build.MakeSolidThing`,
    // which also calls `Map.enrouteManager.SendReservations`) and
    // `Frame.CompleteConstruction` turns the frame into the building and calls
    // `Destroy()` on itself. So "it finished" and "somebody cancelled it" are the
    // same absence, and an agent that asked for a wall cannot tell it got one
    // from the fact that something deconstructed it. 3.3's "every placement
    // journaled with a placement id" was written as bookkeeping; it is in fact
    // the only handle the completion answer can hang on (DESIGN, 2026-09-01).
    //
    // IN MEMORY AND SESSION-SCOPED, ON PURPOSE. Nothing here is scribed: a
    // placement id is a handle for the agent that issued the placement, not a
    // durable fact about the colony, and writing our own data into the save
    // would be the one mutation this mod's whole observer discipline exists to
    // avoid. The DURABLE record is the journal — `build` writes an `action` row
    // carrying the id, and `Frame.CompleteConstruction` / `FailConstruction`
    // write `construction` rows carrying it — so a post-mortem reads the ndjson
    // and a live agent reads this table. `Runtime.ResetForGameBoundary` clears
    // it, because after a load the ids name a map that no longer exists.
    public static class Placements
    {
        // A ten-day run places a few hundred things; the cap is here so a
        // pathological loop cannot grow this without bound. Oldest first out.
        private const int Cap = 2000;

        private static readonly object gate = new object();
        private static readonly List<Placement> order = new List<Placement>();
        private static readonly Dictionary<string, Placement> byId =
            new Dictionary<string, Placement>(StringComparer.Ordinal);
        private static int counter;

        // States, as tokens. An agent branches on these; the prose belongs in
        // `detail` (read by field, never through a description).
        public const string StateBlueprint = "blueprint";
        public const string StateFrame = "frame";
        public const string StateBuilt = "built";
        public const string StateCancelled = "cancelled";
        public const string StateInstant = "instant";

        public static Placement Record(Map map, BuildableDef def, ThingDef stuff,
            IntVec3 pos, Rot4 rot, string mode, Thing produced)
        {
            lock (gate)
            {
                counter++;
                var p = new Placement
                {
                    Id = "pl-" + counter,
                    Def = def,
                    DefName = def?.defName,
                    Stuff = stuff,
                    Pos = pos,
                    Rot = rot,
                    MapId = map?.uniqueID ?? -1,
                    Mode = mode,
                    ThingId = produced?.thingIDNumber ?? -1,
                };
                try { p.Tick = Find.TickManager.TicksGame; } catch { }
                // A zero-work placement is BUILT the moment it returns; there is
                // no blueprint and no frame to wait for, and reporting it as
                // "blueprint" would make `construction` lie about a thing that
                // is standing there.
                if (mode == "instant-zero-work")
                {
                    p.CompletedTick = p.Tick;
                    p.BuiltId = produced?.thingIDNumber ?? -1;
                }
                order.Add(p);
                byId[p.Id] = p;
                while (order.Count > Cap)
                {
                    byId.Remove(order[0].Id);
                    order.RemoveAt(0);
                }
                return p;
            }
        }

        public static Placement Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (gate) return byId.TryGetValue(id, out var p) ? p : null;
        }

        // Every placement this session still knows about, newest last, so
        // `construction` can list them without exposing the table.
        public static List<Placement> All()
        {
            lock (gate) return new List<Placement>(order);
        }

        // The placement a live blueprint or frame belongs to, matched on map,
        // cell and the def being built. Cheap (the table is small) and used by
        // the read to hang a `placement_id` on rows the agent placed, while
        // still reporting the ones it did not.
        public static Placement For(Thing t)
        {
            if (t == null) return null;
            BuildableDef built = t.def?.entityDefToBuild;
            if (built == null) return null;
            int mapId = -1;
            try { mapId = t.Map?.uniqueID ?? -1; } catch { }
            lock (gate)
                for (int i = order.Count - 1; i >= 0; i--)
                {
                    var p = order[i];
                    if (p.MapId != mapId || p.Pos != t.Position) continue;
                    if (p.Def != built) continue;
                    return p;
                }
            return null;
        }

        // Called from the Harmony postfix on `Frame.CompleteConstruction`, which
        // is the POSITIVE event that turns two absences into one fact.
        public static Placement NoteCompleted(Frame frame, Thing result, int tick)
        {
            var p = For(frame);
            if (p == null) return null;
            lock (gate)
            {
                p.CompletedTick = tick;
                p.BuiltId = result?.thingIDNumber ?? -1;
            }
            return p;
        }

        public static void NoteFailed(Frame frame, int tick)
        {
            var p = For(frame);
            if (p == null) return;
            lock (gate) p.Failures++;
        }

        public static void Clear()
        {
            lock (gate)
            {
                order.Clear();
                byId.Clear();
            }
        }

        // ------------------------------------------------------ the answer --

        // `blueprint` | `frame` | `built` | `cancelled`, BY FIELD.
        //
        // The live look comes first and the recorded completion second, because
        // a `FailConstruction` puts the blueprint BACK: a placement whose frame
        // failed is genuinely "blueprint" again, and its failure count is a
        // separate field rather than a state. `cancelled` is the residual — no
        // blueprint, no frame, no completion — and it is reached only after the
        // two positive answers have both said no.
        public static Dictionary<string, object> Answer(Placement p)
        {
            var map = MapOf(p);
            Thing blueprint = null, frame = null, built = null;
            if (map != null)
            {
                var list = map.thingGrid.ThingsListAtFast(p.Pos);
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t?.def == null) continue;
                    if (t.def.entityDefToBuild == p.Def)
                    {
                        if (t is Blueprint) blueprint = t;
                        else if (t is Frame) frame = t;
                    }
                    else if (t.def == p.Def) built = t;
                }
            }
            string state;
            if (blueprint != null) state = StateBlueprint;
            else if (frame != null) state = StateFrame;
            else if (p.CompletedTick > 0 || built != null) state = StateBuilt;
            else state = StateCancelled;

            var d = new Dictionary<string, object>
            {
                ["placement_id"] = p.Id,
                ["state"] = state,
                ["def"] = p.DefName,
                ["stuff"] = p.Stuff?.defName,
                ["at"] = Positions.Out(p.Pos),
                ["rot"] = p.Rot.ToStringWord(),
                ["mode"] = p.Mode,
                ["placed_tick"] = p.Tick,
                ["journal_seq"] = p.JournalSeq == 0 ? (object)null : p.JournalSeq,
                // A frame that failed is not a cancelled build: the game spawns
                // the blueprint again and a pawn tries once more. The count is
                // the fact worth having, because a build that fails repeatedly
                // is a skill problem an agent can act on.
                ["construction_failures"] = p.Failures,
                ["completed_tick"] = p.CompletedTick > 0 ? (object)p.CompletedTick : null,
                // The id of the thing that resulted, from the completion hook
                // where one fired and from the cell otherwise. Null for a
                // TerrainDef, which produces no Thing at all.
                ["thing_id"] = p.BuiltId > 0 ? (object)p.BuiltId
                    : (built != null ? (object)built.thingIDNumber : null),
                ["present"] = built != null || blueprint != null || frame != null,
                ["source"] = p.CompletedTick > 0
                    ? "Frame.CompleteConstruction (journaled)"
                    : "live blueprint/frame/thing at the placement cell",
            };
            if (state == StateCancelled)
                d["detail"] = "no blueprint, no frame and no recorded completion at "
                    + p.Pos.x + "," + p.Pos.z + " — something destroyed it "
                    + "(designate cancel, deconstruct, a raid) or the map it was on is gone";
            return d;
        }

        public static Map MapOf(Placement p)
        {
            try
            {
                var maps = Find.Maps;
                for (int i = 0; i < maps.Count; i++)
                    if (maps[i].uniqueID == p.MapId) return maps[i];
            }
            catch { }
            return null;
        }

        // ---------------------------------------------------- the materials --

        // THE COST LIST, AND WHY IT IS NOT `TotalMaterialCost()`.
        //
        // `Frame.TotalMaterialCost()` and `Blueprint_Build.TotalMaterialCost()`
        // both bottom out in `RimWorld/CostListCalculator.CostListAdjusted(
        // BuildableDef, ThingDef, bool errorOnNullStuff = true)`, whose null-stuff
        // branch is `Log.Error("Cannot get AdjustedCostList for … with null
        // Stuff.")`. `JournalHooks` postfixes `Log.Error` into the journal and
        // every acceptance suite watermarks red errors, so a naive observer turns
        // a clean run RED BY READING IT (git-bug d7c8088 hazard 2).
        //
        // THE OBVIOUS ESCAPE HATCH IS A TRAP, and this is the finding.
        // `errorOnNullStuff: false` silences the error — and then falls through
        // to `value.Add(new ThingDefCountClass(stuff, num))` with `stuff` NULL,
        // and CACHES that list under the key `(entDef, null)` in the static
        // `cachedCosts`. Vanilla's own error path never inserts that key: it
        // recurses into `(entDef, DefaultStuffFor(entDef))` and returns. So
        // passing `false` converts a red error into a process-lifetime poisoned
        // cache entry holding a `ThingDefCountClass` with a null `thingDef`,
        // which the NEXT vanilla call for the same key would consume instead of
        // taking its error branch. That is a mutation of shared game state by an
        // observer, which is worse than the error it avoids.
        //
        // So the guard is not a flag, it is a REFUSAL TO ASK: a `MadeFromStuff`
        // def with no stuff gets `null` materials and a note saying why. Every
        // path in this mod that places a blueprint resolves stuff first
        // (`SiteVerbs.ResolveStuff` -> `GenStuff.DefaultStuffFor`), so the case
        // is reachable only for a blueprint someone else made.
        //
        // THE CACHE ITSELF IS ACCEPTED, same ruling as `BuildableDef.PlaceWorkers`
        // (git-bug c718e4a) and stated because d7c8088 asks for it to be:
        // `cachedCosts` is def-level, keyed on `(entDef, stuff)`, never scribed,
        // reset whenever `Find.Storyteller.difficulty` changes, and the game
        // fills it on any hover over the architect menu. What it is not is free,
        // and whoever reads it first pays for the allocation.
        public static List<object> Materials(BuildableDef def, ThingDef stuff, out string note)
        {
            note = null;
            if (def == null) return null;
            if (def.MadeFromStuff && stuff == null)
            {
                note = "not read: " + def.defName + " is MadeFromStuff and this blueprint has no "
                    + "stuffToUse, and CostListCalculator.CostListAdjusted Log.Errors on that pair. "
                    + "errorOnNullStuff:false would silence it and cache a cost entry with a null "
                    + "thingDef under (def, null), which vanilla's own error path never creates — "
                    + "so the cost is left unread rather than the game's cache poisoned.";
                return null;
            }
            if (!def.MadeFromStuff && stuff != null)
            {
                // The mirror-image Log.Error ("… but is not MadeFromStuff").
                // Cannot happen through our own verbs, which refuse the pair in
                // SiteVerbs.ResolveStuff; guarded because a Frame read off the
                // map is not ours.
                note = "stuff ignored: " + def.defName + " is not MadeFromStuff";
                stuff = null;
            }
            List<ThingDefCountClass> costs;
            try { costs = def.CostListAdjusted(stuff); }
            catch { note = "CostListAdjusted threw"; return null; }
            if (costs == null) return null;
            var list = new List<object>();
            for (int i = 0; i < costs.Count; i++)
            {
                var c = costs[i];
                if (c?.thingDef == null) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["def"] = c.thingDef.defName,
                    ["count"] = c.count,
                });
            }
            return list;
        }

        public static string KindOf(Thing t)
        {
            if (t == null) return null;
            if (t is Blueprint) return StateBlueprint;
            if (t is Frame) return StateFrame;
            return StateBuilt;
        }
    }

    public sealed class Placement
    {
        public string Id;
        public BuildableDef Def;
        public string DefName;
        public ThingDef Stuff;
        public IntVec3 Pos;
        public Rot4 Rot;
        public int MapId;
        public int Tick;
        public string Mode;
        public long JournalSeq;
        // The blueprint or frame `build` itself spawned, for provenance. Not the
        // completion answer: that thing is destroyed on the way to the building.
        public int ThingId = -1;
        public int BuiltId = -1;
        public int CompletedTick;
        public int Failures;
    }
}
