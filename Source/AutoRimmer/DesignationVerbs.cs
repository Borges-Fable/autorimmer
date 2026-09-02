using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ============================================================ spec 3.2 ===
    // DESIGNATION VERBS — what the player drags, the agent declares.
    //
    //   designate {type} {rect | cells | things | filter}
    //   forbid / unforbid       {rect | cells | things | filter}
    //   flick                   {rect | cells | things | filter} [on]
    //
    // All three are PLURAL: one call, N targets, per-target accept/reject with
    // the game's own reason strings, plus a crop echo. See DesignateEngine's
    // header for the shared substrate, the fog rule and why Finalize is called
    // on success only.
    //
    // ------------------- THE GATE, PER TYPE, CITED --------------------------
    // Every entry in the table below names the `Designator_*` class whose own
    // `CanDesignateCell` / `CanDesignateThing` is the gate we run — the game's
    // widget-layer precondition, driven rather than reimplemented, so the
    // reasons the agent gets back are the reasons the player would read.
    //
    // `isOrder = true` on every instance, deliberately and citing
    // `Verse/DesignationCategoryDef.ResolveDesignators`, which sets it on every
    // designator built from `specialDesignatorClasses` — i.e. on every
    // designator in the architect menu. It is not cosmetic: it is the ONLY
    // thing separating `Designator_PlantsCut.AffectsThing`'s two behaviours
    // (`!isOrder && IsTree` refuses a HARVESTABLE tree). The architect "Cut
    // plants" tool accepts mature trees; the right-click reverse designator,
    // built by `ReverseDesignatorDatabase` without the flag, does not. `designate`
    // is a drag over a rect, so it is the architect surface.
    //
    // NAMES. `chop` is `Designator_PlantsHarvestWood`, whose English label is
    // literally "Chop wood" (Core/Languages/English/Keyed, `DesignatorHarvestWood`),
    // and `harvest-wood` is its alias — the spec's scope list names both and
    // they are one designator. `cut` is `Designator_PlantsCut` ("Cut plants"),
    // which the scope list omitted under any name.
    // =========================================================================
    public static class DesignationVerbs
    {
        // ------------------------------------------------------------------
        // THE TABLE
        // ------------------------------------------------------------------
        private sealed class DesEntry
        {
            public Func<Designator> Make;
            public DesignationDef Def;      // for the after-count; null where the
                                           // designator acts immediately (claim)
            public string Cite;             // the class whose gate we run
            public string Note;             // published when the verb is not a designation
            // WHICH WorkGiver CONSUMES THIS DESIGNATION (git-bug b7359fa).
            // The CLASS, not the work type: `DesignateReach.WorkTypeFor` looks
            // it up in DefDatabase<WorkGiverDef> by `giverClass` and reads the
            // def's own `workType`, so a mod that re-homes a giver is honoured
            // and nothing here hardcodes a work type. null means the designator
            // produces no pawn work at all (claim acts on the spot; cancel and
            // the smooth chooser are not one giver's), and the reach block then
            // publishes `applies:false` with the reason.
            //
            // Every entry below was verified against the decompiled 1.6 source
            // by member name: the named class is the one that reads this
            // designation out of `designationManager`.
            public Type Giver;
            // OTHER designations whose presence makes this designator's own
            // gate say no (git-bug 855117a). Exactly one entry in the table
            // has any: `Designator_Mine.CanDesignateThing`'s third clause,
            // `DesignationAt(t.Position, DesignationDefOf.MineVein)`. Used to
            // re-key a rejection the gate already made — see
            // DesignateEngine.WhyAlreadyOther.
            public DesignationDef[] Blocks;
            // OTHER designations this designator SILENTLY REMOVES where it
            // lands. `Designator_MineVein.FloodFillDesignations` calls
            // TryRemoveDesignation(c, Mine) on every cell it paints;
            // `Designator_Mine.DesignateSingleCell` does the same to
            // SmoothWall. Their before/after cells are diffed so the removal
            // is published rather than showing up as an unexplained fall in
            // someone else's count.
            public DesignationDef[] Replaces;
        }

        private static Dictionary<string, DesEntry> table;

        private static Dictionary<string, DesEntry> Table()
        {
            if (table != null) return table;
            var t = new Dictionary<string, DesEntry>(StringComparer.Ordinal);

            void Add(string name, Func<Designator> make, DesignationDef def, string cite,
                Type giver, string note = null)
                => t[name] = new DesEntry
                { Make = make, Def = def, Cite = cite, Giver = giver, Note = note };

            // --- rock and ore ---------------------------------------------
            // Mine and MineVein are ONE giver: `MineAIUtility.PotentialMineables`
            // unions SpawnedDesignationsOfDef(Mine) with (MineVein) and
            // `WorkGiver_Miner.ShouldSkip` bails only when both are empty, so
            // the two designations are interchangeable for job generation.
            Add("mine", () => new Designator_Mine(), DesignationDefOf.Mine,
                "RimWorld/Designator_Mine.CanDesignateCell", typeof(WorkGiver_Miner));
            t["mine"].Blocks = new[] { DesignationDefOf.MineVein };
            t["mine"].Replaces = new[] { DesignationDefOf.SmoothWall };
            // ONE designate paints a whole vein — cheaper than painting cells,
            // and the reason the session-4 amendment named it.
            //
            // ORDER MATTERS BETWEEN THESE TWO, and it is not symmetric:
            //  * `mine` on a MineVein-designated cell is REFUSED
            //    (Designator_Mine.CanDesignateThing's third clause), now
            //    reported as `already-designated-other` rather than reading
            //    like "this rock is not mineable";
            //  * `mine-vein` on a Mine-designated cell is ACCEPTED and
            //    REPLACES it — FloodFillDesignations calls
            //    TryRemoveDesignation(c, Mine) on every cell it paints — now
            //    reported in `replaced`.
            // AND IT FLOOD-FILLS: one accepted cell paints the whole
            // contiguous vein of the same edifice def, so `accepted` is not
            // the size of the job. `designated` is.
            Add("mine-vein", () => new Designator_MineVein(), DesignationDefOf.MineVein,
                "RimWorld/Designator_MineVein.CanDesignateCell", typeof(WorkGiver_Miner),
                "mine-vein FLOOD-FILLS: Designator_MineVein.DesignateSingleCell paints every "
                + "contiguous non-fogged cell whose edifice def matches the seed, so one "
                + "accepted cell can create many designations — read `designated`, not "
                + "`accepted`. It takes ORE only (ThingDef.building.veinMineable), and it "
                + "REPLACES any Mine designation on the cells it paints (see `replaced`).");
            t["mine-vein"].Replaces = new[] { DesignationDefOf.Mine };

            // --- plants ----------------------------------------------------
            // HarvestPlant and CutPlant are both `WorkGiver_PlantsCut`'s:
            // its PotentialWorkThingsGlobal walks designationsByDef for each.
            Add("chop", () => new Designator_PlantsHarvestWood(), DesignationDefOf.HarvestPlant,
                "RimWorld/Designator_PlantsHarvestWood.CanDesignateThing", typeof(WorkGiver_PlantsCut));
            Add("harvest-wood", () => new Designator_PlantsHarvestWood(), DesignationDefOf.HarvestPlant,
                "RimWorld/Designator_PlantsHarvestWood.CanDesignateThing", typeof(WorkGiver_PlantsCut));
            Add("harvest", () => new Designator_PlantsHarvest(), DesignationDefOf.HarvestPlant,
                "RimWorld/Designator_PlantsHarvest.CanDesignateThing", typeof(WorkGiver_PlantsCut));
            Add("cut", () => new Designator_PlantsCut(), DesignationDefOf.CutPlant,
                "RimWorld/Designator_PlantsCut.CanDesignateThing", typeof(WorkGiver_PlantsCut));
            Add("cut-plants", () => new Designator_PlantsCut(), DesignationDefOf.CutPlant,
                "RimWorld/Designator_PlantsCut.CanDesignateThing", typeof(WorkGiver_PlantsCut));
            Add("extract-tree", () => new Designator_ExtractTree(), DesignationDefOf.ExtractTree,
                "RimWorld/Designator_ExtractTree.CanDesignateThing", typeof(WorkGiver_ExtractTree));

            // --- hauling and animals ---------------------------------------
            // No WorkGiver names DesignationDefOf.Haul; the designation makes a
            // thing haulable (`ListerHaulables.ShouldBeHaulable`,
            // `HaulAIUtility.PawnCanAutomaticallyHaulFast`) and
            // WorkGiver_HaulGeneral is what then picks it up.
            Add("haul", () => new Designator_Haul(), DesignationDefOf.Haul,
                "RimWorld/Designator_Haul.CanDesignateThing", typeof(WorkGiver_HaulGeneral));
            Add("hunt", () => new Designator_Hunt(), DesignationDefOf.Hunt,
                "RimWorld/Designator_Hunt.CanDesignateThing", typeof(WorkGiver_HunterHunt));
            Add("slaughter", () => new Designator_Slaughter(), DesignationDefOf.Slaughter,
                "RimWorld/Designator_Slaughter.CanDesignateThing", typeof(WorkGiver_Slaughter));
            Add("tame", () => new Designator_Tame(), DesignationDefOf.Tame,
                "RimWorld/Designator_Tame.CanDesignateThing", typeof(WorkGiver_Tame));
            Add("release-to-wild", () => new Designator_ReleaseAnimalToWild(), DesignationDefOf.ReleaseAnimalToWild,
                "RimWorld/Designator_ReleaseAnimalToWild.CanDesignateThing",
                typeof(WorkGiver_ReleaseAnimalsToWild));

            // --- things and buildings --------------------------------------
            Add("strip", () => new Designator_Strip(), DesignationDefOf.Strip,
                "RimWorld/Designator_Strip.CanDesignateThing", typeof(WorkGiver_Strip));
            Add("open", () => new Designator_Open(), DesignationDefOf.Open,
                "RimWorld/Designator_Open.CanDesignateThing", typeof(WorkGiver_Open));
            // Claim is the ONE entry with no designation: Designator_Claim
            // .DesignateThing calls t.SetFaction(Faction.OfPlayer) on the spot.
            // Session-4 amendment ranks it M1 — without it the agent cannot use
            // anything it did not build (ancient-ruins furniture, abandoned bases).
            Add("claim", () => new Designator_Claim(), null,
                "RimWorld/Designator_Claim.CanDesignateThing (ClaimableBy)", null,
                "claim takes effect IMMEDIATELY — Designator_Claim.DesignateThing calls "
                + "SetFaction(player); there is no designation and no colonist walks anywhere");
            Add("deconstruct", () => new Designator_Deconstruct(), DesignationDefOf.Deconstruct,
                "RimWorld/Designator_Deconstruct.CanDesignateThing (DeconstructibleBy)",
                typeof(WorkGiver_Deconstruct));
            Add("deconstruct-conduit", () => new Designator_DeconstructConduit(), DesignationDefOf.Deconstruct,
                "RimWorld/Designator_DeconstructConduit.CanDesignateThing", typeof(WorkGiver_Deconstruct));
            Add("uninstall", () => new Designator_Uninstall(), DesignationDefOf.Uninstall,
                "RimWorld/Designator_Uninstall.CanDesignateThing", typeof(WorkGiver_Uninstall));
            Add("eject-fuel", () => new Designator_EjectFuel(), DesignationDefOf.EjectFuel,
                "RimWorld/Designator_EjectFuel.CanDesignateThing (CompRefuelable.CanEjectFuel)",
                typeof(WorkGiver_EjectFuel));
            Add("fill-in", () => new Designator_FillIn(), DesignationDefOf.FillIn,
                "RimWorld/Designator_FillIn.CanDesignateThing", typeof(WorkGiver_FillIn));
            Add("extract-skull", () => new Designator_ExtractSkull(), DesignationDefOf.ExtractSkull,
                "RimWorld/Designator_ExtractSkull.CanDesignateThing (+ its Visible gate)",
                typeof(WorkGiver_ExtractSkull));

            // --- terrain ---------------------------------------------------
            Add("smooth", () => new Designator_SmoothSurface(), null,
                "RimWorld/Designator_SmoothSurface.CanDesignateCell", null,
                "smooth picks wall or floor per cell (SmoothSurfaceDesignatorUtility), so the "
                + "designation is SmoothWall on an edifice and SmoothFloor otherwise — and for "
                + "the same reason it has no single work-giver, so the reach block does not "
                + "apply; use smooth-floor / smooth-wall to get one");
            Add("smooth-floor", () => new Designator_SmoothFloors(), DesignationDefOf.SmoothFloor,
                "RimWorld/Designator_SmoothFloors.CanDesignateCell", typeof(WorkGiver_ConstructSmoothFloor));
            Add("smooth-wall", () => new Designator_SmoothWalls(), DesignationDefOf.SmoothWall,
                "RimWorld/Designator_SmoothWalls.CanDesignateCell", typeof(WorkGiver_ConstructSmoothWall));
            Add("remove-floor", () => new Designator_RemoveFloor(), DesignationDefOf.RemoveFloor,
                "RimWorld/Designator_RemoveFloor.CanDesignateCell", typeof(WorkGiver_ConstructRemoveFloor));
            Add("remove-foundation", () => new Designator_RemoveFoundation(), DesignationDefOf.RemoveFoundation,
                "RimWorld/Designator_RemoveFoundation.CanDesignateCell",
                typeof(WorkGiver_ConstructRemoveFoundation));

            // --- undo ------------------------------------------------------
            // `cancel` scopes to WHAT IT IS POINTED AT and nothing else (open
            // question 3): every cancelable designation on the targeted cells or
            // things, plus blueprints and frames, which is exactly
            // Designator_Cancel's own behaviour. There is deliberately no
            // "cancel all of type X map-wide" here — the game's own version of
            // that is a right-click float-menu option on a selected designator
            // (Designator.RightClickFloatMenuOptions "RemoveAllDesignations"),
            // and a verb that clears the whole map by default is the kind of
            // god-hand this spec exists to avoid.
            Add("cancel", () => new Designator_Cancel(), null,
                "RimWorld/Designator_Cancel.CanDesignateCell / CanDesignateThing", null,
                "cancel removes every cancelable designation on the target and destroys "
                + "player blueprints/frames there (Designator_Cancel.DesignateThing)");

            table = t;
            return table;
        }

        public static string TypeWords()
        {
            var names = new List<string>(Table().Keys);
            names.Sort(StringComparer.Ordinal);
            return string.Join("|", names.ToArray());
        }

        // ------------------------------------------------------------------
        // designate
        // ------------------------------------------------------------------
        // designate {type, rect|cells|things|filter, dry_run?, max_cells?,
        //            allow_unreachable?}
        //
        // ------------------- THE ALLOWED-AREA GATE (b7359fa) ------------------
        // `designate hunt` once returned `accepted: 6` and a colonist starved
        // beside five designated deer, because every one stood outside the
        // hunters' `Area_Allowed`. Six designations were genuinely created; no
        // pawn would ever step outside its area to reach one. So this verb now
        // asks the game's own `ForbidUtility.InAllowedArea` per target, over
        // the roster of colonists who can actually do that work, and:
        //
        //   * a MIXED batch reports — `accepted_actionable` and
        //     `accepted_unreachable` beside `accepted`, plus a `reach` block
        //     naming the areas and how many targets each shuts out;
        //   * a batch where NOT ONE target is workable by ANY capable colonist
        //     is REFUSED, before anything is designated, with the correction in
        //     `refused.hint`. `allow_unreachable:true` overrides — a player
        //     legitimately paints ahead of an area expansion, and a wall with
        //     no door is worse than a loud report.
        //
        // The refusal decision is made from a DRY PREFLIGHT — a second
        // designator instance run with `dryRun:true`, so `CanDesignate*` is the
        // only thing called and nothing is written. It runs the gate twice for
        // a batch that will proceed, which is cheap next to the resolve, and it
        // is the only way a refusal can be a refusal rather than an apology for
        // a mutation already made. See DesignateReach's header for the three
        // facts about `InAllowedArea` that decide the shape.
        [Verb("designate")]
        public static object Designate(VerbContext ctx)
        {
            var map = DesignateEngine.Map();
            var a = ctx.Args;
            string type = a.StrReq("type");
            if (!Table().TryGetValue(type, out var entry))
                throw new VerbArgsException($"unknown designation type '{type}' ({TypeWords()})");
            bool dryRun = a.Bool("dry_run", false);
            bool allowUnreachable = a.Bool("allow_unreachable", false);
            var targets = DesignateEngine.Resolve(map, a, DesignateEngine.MaxCellsArg(a));

            var des = entry.Make();
            // The architect menu's flag — see the class header. Set before the
            // first CanDesignate* call, because Designator_PlantsCut reads it there.
            des.isOrder = true;

            // The architect/reverse menu's OWN visibility gate. DESIGN cites
            // Designator_Build.Visible as the canonical UI-only precondition;
            // this is the same property on the same base class (Verse/Gizmo.Visible),
            // so a type the player could not select is a type the agent cannot use.
            bool visible;
            try { visible = des.Visible; } catch { visible = true; }
            if (!visible)
                throw new VerbArgsException(
                    $"'{type}' is not available in this game ({des.GetType().Name}.Visible is false — "
                    + "the designator is hidden from the architect menu, e.g. a missing DLC, research or precept)");

            // ------------------------------------------------- b7359fa ----
            // THE PREFLIGHT. Roster first (cheap, and it decides whether the
            // question applies at all), then the gate DRY over the targets, on
            // a throwaway designator so the real run below gets a virgin one.
            var reach = DesignateReach.Roster(map, entry.Giver);
            if (reach.Applies)
            {
                var probe = entry.Make();
                probe.isOrder = true;
                var pCells = new List<IntVec3>();
                var pThings = new List<Thing>();
                var pRejects = new List<DesignateEngine.Reject>();
                if (targets.IsThings)
                    DesignateEngine.RunThings(map, probe, targets.Things, true, pThings, pRejects,
                        entry.Def, entry.Blocks);
                else
                    DesignateEngine.RunCells(map, probe, targets.Cells, true, pCells, pRejects,
                        entry.Def, entry.Blocks);
                DesignateReach.Score(reach, map,
                    targets.IsThings ? null : pCells, targets.IsThings ? pThings : null);

                if (reach.NothingActionable && !allowUnreachable)
                    return Unreachable(map, type, entry, des, targets, reach, dryRun);
            }

            // null, not -1, when the type has no designation at all (claim,
            // smooth): "there is no such count" and "the count is zero" must
            // not read alike.
            object before = entry.Def == null ? null : (object)DesignateEngine.CountOf(map, entry.Def);
            // The CELLS carrying this def before the call, so `Landed` can name
            // what mine-vein's flood-fill actually painted rather than the one
            // cell the gate accepted. Null for a Thing-targeted def.
            var beforeCells = DesignateEngine.CellSnapshot(map, entry.Def);
            // …and of every designation this designator silently REMOVES where
            // it lands, so `replaced` can name what went (855117a).
            var replacedBefore = ReplacedSnapshot(map, entry.Replaces);
            var acceptedCells = new List<IntVec3>();
            var acceptedThings = new List<Thing>();
            var rejects = new List<DesignateEngine.Reject>();

            // `entry.Def` rides along so a rejection can be told apart from a
            // REDUNDANCY: the game's gates return an empty AcceptanceReport or a
            // bare false for a target that is already designated, which is
            // indistinguishable from "you cannot do that here" and calls for the
            // opposite correction. See DesignateEngine.WhyAlready — including
            // why it dispatches on `DesignationDef.targetType` rather than
            // choosing DesignationAt/DesignationOn per verb. null here (claim,
            // smooth, cancel) simply turns the distinction off.
            //
            // Residual, recorded not fixed: `designate mine` over a cell already
            // carrying a MINE-VEIN designation still reports not-designatable,
            // because `Designator_Mine.CanDesignateThing` rejects on
            // `DesignationAt(t.Position, DesignationDefOf.MineVein)` — a def
            // that is not this entry's. Telling that one apart means
            // re-implementing the widget's second clause, which the gate rule
            // forbids doing blind; `mine-vein` on a mine-designated cell has no
            // such clause and is accepted by the game.
            if (targets.IsThings)
                DesignateEngine.RunThings(map, des, targets.Things, dryRun, acceptedThings, rejects,
                    entry.Def, entry.Blocks);
            else
                DesignateEngine.RunCells(map, des, targets.Cells, dryRun, acceptedCells, rejects,
                    entry.Def, entry.Blocks);

            int acceptedCount = targets.IsThings ? acceptedThings.Count : acceptedCells.Count;
            if (!dryRun) DesignateEngine.FinalizeSucceeded(des, acceptedCount > 0);
            object after = entry.Def == null ? null : (object)DesignateEngine.CountOf(map, entry.Def);

            // What the call actually PUT ON THE MAP — the designation delta for
            // a cell-targeted def, the accepted things otherwise. The reach
            // report is re-scored over it, because `accepted` is not the work
            // set for mine-vein (see DesignateEngine.Landed). A dry run put
            // nothing on the map, so it keeps the preflight's score, which was
            // taken over exactly the set it would have created.
            var landed = DesignateEngine.LandedOf(map, entry.Def, targets,
                acceptedCells, acceptedThings, beforeCells, dryRun);
            if (reach.Applies && !dryRun)
            {
                reach = DesignateReach.Roster(map, entry.Giver);
                DesignateReach.Score(reach, map,
                    landed.IsThings ? null : landed.Cells, landed.IsThings ? landed.Things : null);
            }

            // Cells for the echo: everything we aimed at, so a total rejection
            // still shows the ground it was aimed at.
            var echoCells = new List<IntVec3>();
            if (targets.IsThings)
            {
                for (int i = 0; i < targets.Things.Count; i++)
                    if (targets.Things[i].Spawned) echoCells.Add(targets.Things[i].Position);
            }
            else echoCells.AddRange(targets.Cells);

            var data = new Dictionary<string, object>
            {
                ["verb"] = "designate",
                ["type"] = type,
                ["designator"] = des.GetType().Name,
                ["gate"] = entry.Cite,
                ["designation"] = entry.Def?.defName,
                ["targeted"] = targets.Count,
                ["requested"] = targets.Requested,
                ["capped"] = targets.Capped,
                ["target_scope"] = targets.Detail,
                ["accepted"] = acceptedCount,
                ["dry_run"] = dryRun,
                // b7359fa acceptance item 2: `accepted` is no longer one number
                // covering both cases. These two are about what LANDED (the
                // designation delta), which for mine-vein is not `accepted`;
                // null when the reach question does not apply to this type.
                ["accepted_actionable"] = reach.Applies ? (object)reach.Actionable : null,
                ["accepted_unreachable"] = reach.Applies ? (object)reach.Unreachable : null,
                ["designated"] = landed.Count,
                ["designated_from"] = landed.Source,
                ["reach"] = DesignateReach.Out(reach, map),
                ["allow_unreachable"] = allowUnreachable,
            };
            // WHAT was designated, not how many (git-bug 855117a): a per-def
            // rollup of what actually landed, with the yield an ore cell
            // produces where the game publishes one. `map-view`'s `%` glyph
            // collapses sandstone, marble and compacted steel into one
            // character, so a rect aimed off that view lands on whatever rock
            // was exposed — and until now nothing in the envelope could tell
            // an agent which it got.
            data["composition"] = DesignateComposition.Build(map, landed, out int compMore,
                out int compTotal);
            data["composition_more"] = compMore;
            data["composition_total"] = compTotal;
            var replaced = DesignateComposition.Replaced(map, entry.Replaces, replacedBefore,
                des.GetType().Name + " removes it where it lands");
            if (replaced != null) data["replaced"] = replaced;
            if (entry.Note != null) data["note"] = entry.Note;
            if (targets.IsThings)
            {
                data["ids"] = DesignateEngine.IdsOut(acceptedThings, out int idMore);
                data["ids_more"] = idMore;
                var atCells = new List<IntVec3>();
                for (int i = 0; i < acceptedThings.Count; i++)
                    if (acceptedThings[i].Spawned) atCells.Add(acceptedThings[i].Position);
                data["cells"] = DesignateEngine.CellsOut(atCells, out int cellMore);
                data["cells_more"] = cellMore;
            }
            else
            {
                data["cells"] = DesignateEngine.CellsOut(acceptedCells, out int cellMore);
                data["cells_more"] = cellMore;
            }
            DesignateEngine.PublishRejects(map, rejects, data);
            // Standing on the map now, not "we added N": a designation the
            // designator merged, replaced or flood-filled (mine-vein) moves this
            // number by something other than `accepted`, and that is the truth.
            data["designations_before"] = before;
            data["designations_now"] = after;
            data["crop"] = DesignateEngine.Echo(map, echoCells);

            data["action"] = dryRun
                ? NoAction()
                : Act("designate", type, DesignateEngine.Describe(targets), new Dictionary<string, object>
                {
                    ["counts"] = new Dictionary<string, object>
                    {
                        ["targeted"] = targets.Count,
                        ["accepted"] = acceptedCount,
                        ["rejected"] = rejects.Count,
                        // b7359fa: a post-mortem reads the JOURNAL, not the
                        // envelope the agent saw and discarded. The row that
                        // records "we designated six deer" now also records
                        // that none of them was workable.
                        ["designated"] = landed.Count,
                        ["actionable"] = reach.Applies ? (object)reach.Actionable : null,
                        ["unreachable"] = reach.Applies ? (object)reach.Unreachable : null,
                    },
                    ["designation"] = entry.Def?.defName,
                    // 855117a: the transcript records WHAT was designated, so
                    // a post-mortem can ask "we mined for six days — what did
                    // we actually queue?" without a save.
                    ["composition"] = data["composition"],
                    ["cells"] = data.TryGetValue("cells", out var cs) ? cs : null,
                    ["ids"] = data.TryGetValue("ids", out var ids) ? ids : null,
                    ["rejected_by_reason"] = data["rejects_by_reason"],
                });
            return data;
        }

        // The cells carrying each def this designator will silently remove,
        // taken before it runs. Null when the entry replaces nothing, which is
        // every entry but `mine` and `mine-vein`.
        private static Dictionary<DesignationDef, HashSet<IntVec3>> ReplacedSnapshot(
            Map map, DesignationDef[] defs)
        {
            if (defs == null || defs.Length == 0) return null;
            var d = new Dictionary<DesignationDef, HashSet<IntVec3>>();
            for (int i = 0; i < defs.Length; i++)
            {
                if (defs[i] == null) continue;
                var set = DesignateEngine.CellSnapshot(map, defs[i]);
                if (set != null) d[defs[i]] = set;
            }
            return d.Count > 0 ? d : null;
        }

        // ------------------------------------------------------------------
        // THE ALL-UNREACHABLE REFUSAL                          (git-bug b7359fa)
        // ------------------------------------------------------------------
        // Nothing has been designated when this runs — the preflight was dry —
        // so `accepted` is 0 and `designations_now` equals `designations_before`
        // by construction, both published so a caller can see that for itself
        // rather than take our word.
        //
        // IT JOURNALS. `PawnEmergencyVerbs` set the precedent (a refused order
        // journals the same way an accepted one does, git-bug 4087644): a run
        // that tried to designate 128 mining cells and was told no must appear
        // in the transcript, or the post-mortem sees a gap where a decision was.
        // A dry run still journals nothing, same as every other dry run.
        private static object Unreachable(Map map, string type, DesEntry entry, Designator des,
            DesignateEngine.Targets targets, DesignateReach.Verdict reach, bool dryRun)
        {
            int standing = entry.Def == null ? -1 : DesignateEngine.CountOf(map, entry.Def);
            var refused = DesignateReach.Refusal(reach);
            var data = new Dictionary<string, object>
            {
                ["verb"] = "designate",
                ["type"] = type,
                ["designator"] = des.GetType().Name,
                ["gate"] = entry.Cite,
                ["designation"] = entry.Def?.defName,
                ["targeted"] = targets.Count,
                ["requested"] = targets.Requested,
                ["capped"] = targets.Capped,
                ["target_scope"] = targets.Detail,
                ["accepted"] = 0,
                ["accepted_actionable"] = 0,
                ["accepted_unreachable"] = reach.Unreachable,
                ["designated"] = 0,
                ["designated_from"] = "nothing was designated — refused before the write",
                ["dry_run"] = dryRun,
                ["allow_unreachable"] = false,
                ["refused"] = refused,
                ["reach"] = DesignateReach.Out(reach, map),
                ["designations_before"] = entry.Def == null ? null : (object)standing,
                ["designations_now"] = entry.Def == null ? null : (object)standing,
            };
            if (entry.Note != null) data["note"] = entry.Note;
            // The ground it was aimed at, same echo a total rejection gets.
            var echoCells = new List<IntVec3>();
            if (targets.IsThings)
            {
                for (int i = 0; i < targets.Things.Count; i++)
                    if (targets.Things[i].Spawned) echoCells.Add(targets.Things[i].Position);
            }
            else echoCells.AddRange(targets.Cells);
            data["crop"] = DesignateEngine.Echo(map, echoCells);
            data["action"] = dryRun
                ? NoAction()
                : Act("designate", type, DesignateEngine.Describe(targets), new Dictionary<string, object>
                {
                    ["refused"] = refused["code"],
                    ["counts"] = new Dictionary<string, object>
                    {
                        ["targeted"] = targets.Count,
                        ["accepted"] = 0,
                        ["designated"] = 0,
                        ["actionable"] = 0,
                        ["unreachable"] = reach.Unreachable,
                    },
                    ["designation"] = entry.Def?.defName,
                });
            return data;
        }

        // ------------------------------------------------------------------
        // forbid / unforbid
        // ------------------------------------------------------------------
        // Gate: RimWorld/Designator_Forbid.CanDesignateThing (category == Item
        // AND a CompForbiddable that is not already in the wanted state) and its
        // Unforbid twin. Both DesignateThing calls are `t.SetForbidden(value,
        // warnOnFail:false)` — the model write is the same one ForbidUtility
        // exposes, but the CATEGORY test lives only in the designator, which is
        // why we drive the designator.
        [Verb("forbid")]
        public static object Forbid(VerbContext ctx) => ForbidCore(ctx, true);

        [Verb("unforbid")]
        public static object Unforbid(VerbContext ctx) => ForbidCore(ctx, false);

        private static object ForbidCore(VerbContext ctx, bool forbid)
        {
            var map = DesignateEngine.Map();
            var a = ctx.Args;
            bool dryRun = a.Bool("dry_run", false);
            var targets = DesignateEngine.Resolve(map, a, DesignateEngine.MaxCellsArg(a));

            Designator des = forbid ? (Designator)new Designator_Forbid() : new Designator_Unforbid();
            des.isOrder = true;
            string verb = forbid ? "forbid" : "unforbid";

            var acceptedCells = new List<IntVec3>();
            var acceptedThings = new List<Thing>();
            var rejects = new List<DesignateEngine.Reject>();
            if (targets.IsThings)
                DesignateEngine.RunThings(map, des, targets.Things, dryRun, acceptedThings, rejects);
            else
                DesignateEngine.RunCells(map, des, targets.Cells, dryRun, acceptedCells, rejects);

            int acceptedCount = targets.IsThings ? acceptedThings.Count : acceptedCells.Count;
            if (!dryRun) DesignateEngine.FinalizeSucceeded(des, acceptedCount > 0);

            var echoCells = new List<IntVec3>();
            if (targets.IsThings)
            {
                for (int i = 0; i < targets.Things.Count; i++)
                    if (targets.Things[i].Spawned) echoCells.Add(targets.Things[i].Position);
            }
            else echoCells.AddRange(targets.Cells);

            var data = new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["designator"] = des.GetType().Name,
                ["gate"] = "RimWorld/" + des.GetType().Name + ".CanDesignateThing",
                ["targeted"] = targets.Count,
                ["requested"] = targets.Requested,
                ["capped"] = targets.Capped,
                ["target_scope"] = targets.Detail,
                ["accepted"] = acceptedCount,
                ["dry_run"] = dryRun,
                // A cell target forbids EVERY forbiddable item on it
                // (Designator_Forbid.DesignateSingleCell loops the thing list),
                // so the accepted-cell count is not an item count.
                ["note"] = targets.IsThings
                    ? "per-thing"
                    : "per-cell: every forbiddable item on an accepted cell was toggled",
            };
            if (targets.IsThings)
            {
                data["ids"] = DesignateEngine.IdsOut(acceptedThings, out int idMore);
                data["ids_more"] = idMore;
            }
            data["cells"] = DesignateEngine.CellsOut(targets.IsThings ? echoCells : acceptedCells, out int cellMore);
            data["cells_more"] = cellMore;
            DesignateEngine.PublishRejects(map, rejects, data);
            data["crop"] = DesignateEngine.Echo(map, echoCells);
            data["action"] = dryRun
                ? NoAction()
                : Act(verb, targets.Kind, DesignateEngine.Describe(targets), new Dictionary<string, object>
                {
                    ["counts"] = new Dictionary<string, object>
                    {
                        ["targeted"] = targets.Count,
                        ["accepted"] = acceptedCount,
                        ["rejected"] = rejects.Count,
                    },
                    ["rejected_by_reason"] = data["rejects_by_reason"],
                });
            return data;
        }

        // ------------------------------------------------------------------
        // flick
        // ------------------------------------------------------------------
        // flick {rect|cells|things|filter, on: true|false|"toggle"}
        //
        // Its shape is unusual and the session-4 amendment says so: CompFlickable
        // adds a Flick DESIGNATION through the same DesignationManager the rest
        // of this file drives, but it is reached per-thing through a gizmo
        // TOGGLE rather than a cell-drag designator, so there is no
        // `Designator_*` to run and the gate has to be reproduced by hand.
        //
        // GATE (cited): RimWorld/CompFlickable.CompGetGizmosExtra — the toggle
        // exists only when `parent.Faction == Faction.OfPlayer`, and its action
        // is exactly `wantSwitchOn = !wantSwitchOn;
        // FlickUtility.UpdateFlickDesignation(parent)`.
        //
        // ECHO THE DESIGNATION, NEVER THE STATE. `CompFlickable.SwitchIsOn`
        // does not move until a colonist walks over and does the job
        // (JobDriver_Flick); a verb that returned "power off" immediately would
        // be lying. The result reports `switch_is_on` (now), `wants_on` (what
        // was asked) and `flick_designated` (whether the work order stands).
        //
        // ONE DELIBERATE OMISSION, and it is a HAZARD worth reading:
        // `FlickUtility.UpdateFlickDesignation` ends with
        // `TutorUtility.DoModalDialogIfNotKnown(ConceptDefOf.SwitchFlickingDesignation)`,
        // which on a save that has never flicked anything does
        // `Find.WindowStack.Add(new Dialog_MessageBox(msg))` — and
        // Verse/Dialog_MessageBox sets `forcePause = true`. Per JOURNAL.md
        // (spec 1.7) a force-pausing window halts EVERY subsequent `advance`
        // with reason:"dialog", is not suppressible, and cannot be closed from
        // here. So the designation half of UpdateFlickDesignation is
        // re-implemented below, line for line, and the tutorial modal is the
        // only thing dropped. Nothing about the model differs.
        [Verb("flick")]
        public static object Flick(VerbContext ctx)
        {
            var map = DesignateEngine.Map();
            var a = ctx.Args;
            bool dryRun = a.Bool("dry_run", false);

            // on: true | false | "toggle" (default). A program that says
            // `on:false` twice must not turn the switch back on.
            string mode;
            object rawOn = a.Raw("on");
            if (rawOn == null) mode = "toggle";
            else if (rawOn is bool b) mode = b ? "on" : "off";
            else if (rawOn is string s && s == "toggle") mode = "toggle";
            else throw new VerbArgsException("arg 'on' must be true, false or \"toggle\"");

            var targets = DesignateEngine.Resolve(map, a, DesignateEngine.MaxCellsArg(a));

            // Cells resolve to the things standing on them: flick is per-thing.
            var candidates = new List<Thing>();
            var rejects = new List<DesignateEngine.Reject>();
            if (targets.IsThings) candidates.AddRange(targets.Things);
            else
            {
                for (int i = 0; i < targets.Cells.Count; i++)
                {
                    var c = targets.Cells[i];
                    if (!c.InBounds(map))
                    {
                        rejects.Add(new DesignateEngine.Reject { At = c, Why = "out-of-bounds" });
                        continue;
                    }
                    if (c.Fogged(map))
                    {
                        rejects.Add(new DesignateEngine.Reject
                        { At = c, Why = DesignateEngine.WhyFogged, Reason = Blockers.FoggedReason });
                        continue;
                    }
                    bool found = false;
                    var list = map.thingGrid.ThingsListAtFast(c);
                    for (int j = 0; j < list.Count; j++)
                        if (list[j] is ThingWithComps twc && twc.GetComp<CompFlickable>() != null)
                        {
                            if (!candidates.Contains(twc)) candidates.Add(twc);
                            found = true;
                        }
                    if (!found)
                        rejects.Add(new DesignateEngine.Reject { At = c, Why = "nothing-flickable" });
                }
            }

            var results = new List<object>();
            int changed = 0, alreadyThere = 0;
            var echoCells = new List<IntVec3>();
            for (int i = 0; i < candidates.Count; i++)
            {
                var t = candidates[i];
                if (WorldSafe.Hidden(t, map))
                {
                    rejects.Add(new DesignateEngine.Reject
                    {
                        At = t.PositionHeld,
                        Thing = t,
                        Why = t.Spawned && t.Map == map ? DesignateEngine.WhyFogged : "not-on-map",
                        Reason = t.Spawned && t.Map == map ? Blockers.FoggedReason : null,
                    });
                    continue;
                }
                var comp = (t as ThingWithComps)?.GetComp<CompFlickable>();
                if (comp == null)
                {
                    rejects.Add(new DesignateEngine.Reject
                    { At = t.Position, Thing = t, Why = "not-flickable" });
                    continue;
                }
                // The gizmo's own gate, verbatim: CompFlickable.CompGetGizmosExtra
                // yields the toggle only `if (parent.Faction == Faction.OfPlayer)`.
                if (t.Faction != Faction.OfPlayer)
                {
                    rejects.Add(new DesignateEngine.Reject
                    {
                        At = t.Position,
                        Thing = t,
                        Why = "not-player-faction",
                    });
                    continue;
                }
                echoCells.Add(t.Position);

                bool wantsNow = WantSwitchOn(comp, out bool refOk);
                if (!refOk)
                {
                    rejects.Add(new DesignateEngine.Reject
                    {
                        At = t.Position,
                        Thing = t,
                        Why = "flick-field-unavailable",
                        Reason = "CompFlickable.wantSwitchOn could not be reached (a mod may have replaced the comp)",
                    });
                    continue;
                }
                bool target = mode == "toggle" ? !wantsNow : mode == "on";
                if (target == wantsNow) alreadyThere++;
                else if (!dryRun)
                {
                    SetWantSwitchOn(comp, target);
                    UpdateFlickDesignation(map, t);
                    changed++;
                }
                else changed++;

                bool designated = map.designationManager.DesignationOn(t, DesignationDefOf.Flick) != null;
                results.Add(new Dictionary<string, object>
                {
                    ["id"] = t.thingIDNumber,
                    ["def"] = t.def?.defName,
                    ["label"] = WorldSafe.Safe(() => t.LabelShort),
                    ["at"] = Positions.Out(t.Position),
                    ["wants_on"] = dryRun ? wantsNow : WantSwitchOn(comp, out _),
                    // The CURRENT switch, which has not moved and will not until
                    // a colonist does the job. Never conflated with wants_on.
                    ["switch_is_on"] = comp.SwitchIsOn,
                    ["flick_designated"] = designated,
                    ["was_already"] = target == wantsNow,
                });
            }

            var data = new Dictionary<string, object>
            {
                ["verb"] = "flick",
                ["mode"] = mode,
                ["gate"] = "RimWorld/CompFlickable.CompGetGizmosExtra (parent.Faction == Faction.OfPlayer)"
                    + " + RimWorld/FlickUtility.UpdateFlickDesignation",
                ["targeted"] = targets.Count,
                ["requested"] = targets.Requested,
                ["capped"] = targets.Capped,
                ["target_scope"] = targets.Detail,
                ["accepted"] = results.Count,
                ["changed"] = changed,
                ["already_wanted"] = alreadyThere,
                ["dry_run"] = dryRun,
                ["things"] = results,
                ["flick_designations_now"] = DesignateEngine.CountOf(map, DesignationDefOf.Flick),
                // The whole point of the verb's contract, said out loud in the
                // result rather than only in a comment.
                ["note"] = "flick is a WORK ORDER, not a switch: SwitchIsOn does not move until a colonist "
                    + "walks over and does the job (JobDriver_Flick). `wants_on` is what was asked, "
                    + "`switch_is_on` is what is true now — advance before asserting on power.",
            };
            DesignateEngine.PublishRejects(map, rejects, data);
            data["crop"] = DesignateEngine.Echo(map, echoCells.Count > 0 ? echoCells : new List<IntVec3>(targets.Cells));
            data["action"] = dryRun
                ? NoAction()
                : Act("flick", mode, results.Count + " flickable(s)", new Dictionary<string, object>
                {
                    ["counts"] = new Dictionary<string, object>
                    {
                        ["targeted"] = targets.Count,
                        ["accepted"] = results.Count,
                        ["changed"] = changed,
                        ["rejected"] = rejects.Count,
                    },
                });
            return data;
        }

        // RimWorld/FlickUtility.UpdateFlickDesignation, minus its final
        // TutorUtility.DoModalDialogIfNotKnown — see the Flick header for why
        // that one line is the difference between a working session and an
        // advance that halts on reason:"dialog" forever.
        private static void UpdateFlickDesignation(Map map, Thing t)
        {
            bool wantsFlick = false;
            if (t is ThingWithComps twc)
            {
                // AllComps is a plain list field on ThingWithComps; the game's
                // own loop, kept because a thing may carry more than one
                // CompFlickable (a mod can) and ANY of them wanting a flick is
                // what puts the designation on.
                for (int i = 0; i < twc.AllComps.Count; i++)
                    if (twc.AllComps[i] is CompFlickable f && f.WantsFlick()) { wantsFlick = true; break; }
            }
            var designation = map.designationManager.DesignationOn(t, DesignationDefOf.Flick);
            if (wantsFlick && designation == null)
                map.designationManager.AddDesignation(new Designation(t, DesignationDefOf.Flick));
            else if (!wantsFlick) designation?.Delete();
        }

        // CompFlickable.wantSwitchOn is private and there is no public setter —
        // the gizmo closure writes the field directly. AccessTools, resolved
        // once and TOLERANTLY (PawnSafe/WorldSafe's rule): a ref that fails to
        // bind degrades the verb to a per-thing rejection, never an exception.
        private static bool flickRefTried;
        private static AccessTools.FieldRef<CompFlickable, bool> flickRef;

        private static AccessTools.FieldRef<CompFlickable, bool> FlickRef()
        {
            if (flickRefTried) return flickRef;
            flickRefTried = true;
            try { flickRef = AccessTools.FieldRefAccess<CompFlickable, bool>("wantSwitchOn"); }
            catch (Exception e)
            {
                Journal.EmitWarning("3.2: CompFlickable.wantSwitchOn field ref failed: " + e.Message);
            }
            return flickRef;
        }

        private static bool WantSwitchOn(CompFlickable comp, out bool ok)
        {
            var fr = FlickRef();
            if (fr == null) { ok = false; return comp.SwitchIsOn; }
            try { ok = true; return fr(comp); }
            catch { ok = false; return comp.SwitchIsOn; }
        }

        private static void SetWantSwitchOn(CompFlickable comp, bool value)
        {
            var fr = FlickRef();
            if (fr == null) return;
            try { fr(comp) = value; } catch { }
        }

        // ------------------------------------------------------------------
        // THE `action` JOURNAL EVENT
        // ------------------------------------------------------------------
        // Player-verb mutations journal as `action`, mirroring the `dev` row —
        // {verb, step, target} plus additive extras, consumers ignore unknown
        // fields (JOURNAL.md's standing contract). Unlike `dev` an `action` line
        // is NOT a cheat, so it carries neither `cheat` nor `fog_exempt`.
        //
        // The join key is carried back the way Dev.Stamp does it, INCLUDING the
        // honest failure: Journal.Emit returns 0 when the writer is closed, and
        // a mutation with no journal line is a mutation that cannot be traced,
        // so it says so rather than looking like a normal result.
        //
        // Private static, in this file, on purpose: 3.4's worker is writing the
        // same helper in parallel and a shared public class would collide at
        // merge. The orchestrator factors it at merge time; nothing here edits
        // JOURNAL.md.
        private static Dictionary<string, object> Act(string verb, string step, string target,
            Dictionary<string, object> extra)
        {
            var payload = new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["step"] = step,
                ["target"] = target,
            };
            if (extra != null)
                foreach (var kv in extra)
                    if (!payload.ContainsKey(kv.Key) && kv.Value != null) payload[kv.Key] = kv.Value;
            int tick = 0;
            try { tick = Find.TickManager.TicksGame; } catch { }
            long seq = Journal.Emit("action", payload, tick);
            var d = new Dictionary<string, object> { ["journal_seq"] = seq };
            if (seq == 0)
                d["provenance"] = "NOT WRITTEN — the journal writer is closed, so this mutation has no "
                    + "journal line. Treat any state changed in this session as unprovenanced.";
            return d;
        }

        private static Dictionary<string, object> NoAction()
            => new Dictionary<string, object>
            {
                ["journal_seq"] = null,
                ["provenance"] = "not applicable — dry_run mutated nothing",
            };
    }
}
