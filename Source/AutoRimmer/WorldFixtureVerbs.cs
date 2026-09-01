using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoRimmer
{
    // Deliberate stimulus for spec 2.4's acceptance, in its OWN verb rather than
    // as steps on `journal-selftest` or `pawn-fixture` — those files belong to
    // other specs, and this wave's file rule is new-files-only. The `pawn-fixture`
    // precedent exactly (spec 2.2), including the disclosure duty.
    //
    // WHY IT EXISTS. 2.4's acceptance is "a fixture colony with a butcher bill,
    // two stockpiles, a growing zone, an unfinished research, one choice letter
    // open", and on this bench NONE of that can be produced by a shipped verb:
    // no build verb until 3.3, no bill verb, no zone verb, no research verb
    // until 3.x, and `journal-selftest stockpile` makes ONE zone from the
    // default preset with no priority and no filter edit — which is exactly the
    // case a filter summary cannot be tested against.
    //
    // THIS VERB MUTATES GAME STATE BY DESIGN. It is gated on Prefs.DevMode, it
    // journals a `dev` event per step with the established {verb, step, target}
    // payload, and it is disclosed on git-bug 21856e3 as an undeclared addition.
    // Superseded by 3.1's dev layer, exactly as `journal-selftest` and
    // `pawn-fixture` are.
    //
    // TWO GOD-HAND CALLS, NAMED. DESIGN §Action model says the gate lives in the
    // widget and a PLAYER verb must re-implement it. This is not a player verb,
    // and it deliberately uses two of the three calls that section cites:
    //   * BillStack.AddBill checks NOTHING — not RecipeDef.AvailableNow, not the
    //     15-bill cap (RimWorld/BillStack.cs). Used here on purpose, and the
    //     fixture picks an available recipe itself so the resulting bill is a
    //     REAL one rather than a stuck one; `research_ok` on the bill line is
    //     what proves the serializer would have caught a stuck one.
    //     THE CAP HALF IS NO LONGER A GOD-HAND (git-bug 0d9cbd7 comment #1,
    //     point 2): this step adds TWO bills and never cleared, so repeated
    //     calls pushed the stack past `BillStack.MaxCount`, which the game's own
    //     UI enforces. It now REFUSES at the cap rather than clearing silently,
    //     with `clear_bills:true` as the explicit opt-in. The recipe half stays
    //     a god-hand on purpose — an unresearched bill is a state worth being
    //     able to stage.
    //   * ResearchManager.SetCurrentProject tests only `baseCost > 0f` — no
    //     prerequisite check at all. Used here on purpose, and the fixture picks
    //     a project whose prerequisites ARE met, so `research.current` describes
    //     a legal state.
    // A wave-3 player verb may not do either. Saying so here is the point.
    public static class WorldFixtureVerbs
    {
        public const string FixtureLetterLabel = "[AutoRimmer] world-fixture choice letter";

        [Verb("world-fixture")]
        public static object Fixture(VerbContext ctx)
        {
            if (!Prefs.DevMode)
                throw new VerbArgsException("world-fixture requires devMode=True (it mutates game state)");
            var map = Find.CurrentMap ?? throw new VerbArgsException("world-fixture needs a current map");

            var steps = ctx.Args.StrList("steps");
            if (steps.Count == 0)
                steps = new List<string> { "bench", "bill", "stockpiles", "growing", "research", "letter" };

            var executed = new List<object>();
            var extras = new Dictionary<string, object>();
            // THE HANDLE THE STEPS PASS TO EACH OTHER (git-bug 0d9cbd7). Before
            // this existed the switch shared nothing but `extras`, which is
            // OUTPUT, so a later step had no way to reach an object an earlier
            // one had made and re-resolved it from the map instead.
            var chain = new FixtureChain();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var step in steps)
            {
                string target;
                switch (step)
                {
                    case "bench": target = Bench(map, ctx, extras, chain); break;
                    case "bill": target = AddBill(map, ctx, extras, chain); break;
                    case "stockpiles": target = Stockpiles(map, ctx, extras); break;
                    case "growing": target = Growing(map, ctx, extras); break;
                    case "research": target = Research(ctx, extras); break;
                    case "letter": target = Letter(map, ctx, extras, chain); break;
                    case "open-letter": target = OpenLetter(extras, chain); break;
                    case "forbid": target = Forbid(map, ctx, extras); break;
                    case "fire": target = Fire(map, ctx, extras); break;
                    default:
                        throw new VerbArgsException(
                            $"unknown step '{step}' (bench|bill|stockpiles|growing|research|letter|open-letter|forbid|fire)");
                }
                Journal.Emit("dev", new Dictionary<string, object>
                {
                    ["verb"] = "world-fixture",
                    ["step"] = step,
                    ["target"] = target,
                }, Find.TickManager.TicksGame);
                executed.Add(step);
                seen[step] = seen.TryGetValue(step, out var n) ? n + 1 : 1;
            }

            // WHAT THE BILLS ACTUALLY LOOK LIKE NOW. `extras["bench"]` is built
            // inside `Bench(...)`, i.e. BEFORE any `bill` step in the same call
            // has run, so `expect_bills: 0` and `expect_slots_free: 15` were
            // literals that a chained call made WRONG. The either/or in the
            // issue's acceptance ("reflects the bills present when the call
            // RETURNS *or* the field is removed") is resolved in favour of
            // KEEPING it and making it true: an independent hand-count is what
            // the field is for, and deleting it would leave `bills` with nothing
            // to be checked against. Refreshed here rather than by deferring the
            // whole block, so the block keeps its position in the envelope.
            Refresh(extras, chain);

            var data = new Dictionary<string, object> { ["executed"] = executed };
            // A REPEATED STEP IS ALLOWED AND SAYS SO. `steps` is not deduped and
            // every handler writes `extras["<key>"] = …`, so
            // `{steps:["bench","bench"]}` really does spawn two tables and
            // publish only the second — which is legitimate for a fixture (two
            // benches is a thing a test may want) and silent, which is not.
            // The chain points at the LAST object created, which is the only
            // answer that makes `{steps:["bench","bench","bill"]}` mean anything.
            var repeated = new Dictionary<string, object>();
            foreach (var kv in seen) if (kv.Value > 1) repeated[kv.Key] = kv.Value;
            if (repeated.Count > 0)
            {
                data["repeated_steps"] = repeated;
                data["repeated_note"] =
                    "a step that ran more than once really ran more than once — every object was "
                    + "created — but each block below reports only the LAST, and a later step in "
                    + "the same call chains to the LAST. Ask for one per call if you need both ids.";
            }
            foreach (var kv in extras) data[kv.Key] = kv.Value;
            return data;
        }

        // The objects this call has created, for the steps that come after.
        //
        // THE AUDIT, EVERY STEP IN THE SWITCH (git-bug 0d9cbd7 comment #1, which
        // corrected the issue's own list: it named four steps that CREATE things
        // and omitted the one other step that RESOLVES one).
        //
        //   RESOLVERS — the whole bug class, both fixed here:
        //     `bill`         resolved the first player `TableButcher` in
        //                    `ListerThings.ThingsInGroup(PotentialBillGiver)`.
        //                    `Verse/ListerThings.Add` APPENDS, so "first" is
        //                    "oldest spawned" and the second `bench` step in a
        //                    session lands BEHIND the first. Confirmed live on
        //                    BORGES: bench #23492 made, bills onto #23491.
        //     `open-letter`  resolved the first letter in
        //                    `LetterStack.LettersListForReading` whose label
        //                    starts with the fixture prefix. All three tells of
        //                    the same bug: first-match over a shared live list,
        //                    an error string that ASSERTS the chaining ("run the
        //                    `letter` step first"), and an id the earlier step
        //                    already published and nobody consulted.
        //
        //   CREATORS — nothing to chain TO, so nothing to fix:
        //     `bench`, `letter`   the two sources of a handle.
        //     `stockpiles`, `growing`, `fire`   each picks GROUND, not an object.
        //                    They ring out from the colonist anchor and
        //                    `FindClearRect`/`ZoneOk` already exclude cells that
        //                    hold a zone or an edifice, so two `stockpiles`
        //                    steps in one call get different rects — the
        //                    first-match is over CELLS and is re-narrowed by
        //                    what the previous step did to them.
        //     `research`     resolves a `ResearchProjectDef` out of the def
        //                    database, and no step creates one. First-match, but
        //                    over immutable data.
        //     `forbid`       resolves a POOL of live haulables (`HaulableEver`,
        //                    sorted by id) and is genuinely first-match over a
        //                    shared live list — but no step in the switch creates
        //                    a haulable, so there is nothing for it to chain to.
        //                    If one is ever added, this is the step to revisit.
        private sealed class FixtureChain
        {
            public Thing Bench;
            public Letter Letter;
        }

        // `expect_bills` and `expect_slots_free` from the bench's REAL stack.
        // Both were literals (0 and 15) and both are the same defect; the issue's
        // acceptance names only the first, so fixing only that would have left a
        // bench holding two bills reporting fifteen free slots.
        private static void Refresh(Dictionary<string, object> extras, FixtureChain chain)
        {
            if (chain.Bench == null) return;
            if (!(extras.TryGetValue("bench", out var raw) && raw is Dictionary<string, object> block)) return;
            int count = 0;
            try { count = (chain.Bench as IBillGiver)?.BillStack?.Count ?? 0; }
            catch { }
            block["expect_bills"] = count;
            block["expect_slots_free"] = Math.Max(0, BillStack.MaxCount - count);
        }

        // ------------------------------ bench -------------------------------
        // A real butcher table, player-faction, spawned the way the game's own
        // dev spawner does (SetFactionDirect BEFORE spawn, so it is the player's
        // from its first tick — the `power` step of journal-selftest established
        // that discipline and BillGiver/DeconstructibleBy both key on it).
        private static string Bench(Map map, VerbContext ctx, Dictionary<string, object> extras,
            FixtureChain chain)
        {
            var def = ThingDefOf.TableButcher;
            var anchor = Anchor(map);
            var rect = FindClearRect(map, anchor, Math.Max(1, def.size.x) + 1, Math.Max(1, def.size.z) + 1);
            // THE GATE, which this step had none of (git-bug 3a5ff6c item 3).
            // `FindClearRect` walks cells; a butcher table has a footprint AND an
            // interaction cell, and neither the rect search nor the bare
            // GenSpawn.Spawn that followed it knew that. FixtureSite runs
            // GenConstruct.CanPlaceBlueprintAt and refuses rather than staging a
            // bench no colonist could have built — see its header for why the
            // widget half is reported and not honoured here.
            var spawned = FixtureSite.Spawn(map, def, GenStuff.DefaultStuffFor(def),
                rect.CenterCell, Rot4.North, "world-fixture bench", out var gate);
            // The handle. Everything the `bill` bug was is the absence of this
            // one line (git-bug 0d9cbd7).
            chain.Bench = spawned;
            extras["bench"] = new Dictionary<string, object>
            {
                ["id"] = spawned.thingIDNumber,
                ["def"] = def.defName,
                ["at"] = Positions.Out(spawned.Position),
                ["gate"] = gate,
                // What `bills` must independently report.
                ["expect_bills"] = 0,
                ["expect_slots_free"] = 15,
            };
            return def.defName + " #" + spawned.thingIDNumber;
        }

        // ------------------------------- bill -------------------------------
        private static string AddBill(Map map, VerbContext ctx, Dictionary<string, object> extras,
            FixtureChain chain)
        {
            var giver = FindBench(map, ctx, chain, out string benchSource) as IBillGiver;
            if (giver == null) throw new VerbArgsException("bill needs a bill giver (run the `bench` step first)");
            var thing = (Thing)giver;

            // Pick a recipe the bench can actually run RIGHT NOW, so the bill is
            // a real one. AddBill would happily take an unresearched recipe —
            // that is the point of the DESIGN citation in the class comment —
            // and the serializer's `research_ok` is what would surface it.
            RecipeDef chosen = null;
            var recipes = thing.def.AllRecipes;
            string wanted = ctx.Args.Str("recipe");
            for (int i = 0; i < recipes.Count; i++)
            {
                var r = recipes[i];
                if (r == null) continue;
                if (wanted != null) { if (r.defName == wanted) { chosen = r; break; } continue; }
                bool researchOk = true;
                if (r.researchPrerequisite != null && !WorldSafe.Finished(r.researchPrerequisite)) researchOk = false;
                if (r.researchPrerequisites != null)
                    for (int j = 0; j < r.researchPrerequisites.Count; j++)
                        if (!WorldSafe.Finished(r.researchPrerequisites[j])) researchOk = false;
                if (!researchOk) continue;
                if (chosen == null || r.defName.IndexOf("Butcher", StringComparison.OrdinalIgnoreCase) >= 0) chosen = r;
                if (chosen != null && chosen.defName.IndexOf("Butcher", StringComparison.OrdinalIgnoreCase) >= 0) break;
            }
            if (chosen == null)
                throw new VerbArgsException($"{thing.def.defName} has no runnable recipe"
                    + (wanted != null ? $" named '{wanted}'" : ""));

            // THE CAP THE MODEL DOES NOT ENFORCE. `RimWorld/BillStack.AddBill`
            // is `bill.billStack = this; bills.Add(bill);` and validates
            // nothing, while the game's own UI stops the player at
            // `BillStack.MaxCount` (15). Eight `bill` steps against one bench
            // therefore pushed the stack past a cap no player can exceed, and
            // this step adds TWO bills and never clears.
            //
            // REFUSE, DO NOT SILENTLY CLEAR. A fixture that clears destroys
            // state the caller or an earlier step staged, and it would do it
            // under a name ("bill") that says nothing about removal. Refusing
            // names the problem and `clear_bills:true` is the explicit opt-in.
            bool clearFirst = ctx.Args.Bool("clear_bills", false);
            int cleared = 0;
            if (clearFirst)
            {
                cleared = giver.BillStack.Count;
                giver.BillStack.Clear();
            }
            if (giver.BillStack.Count + 2 > BillStack.MaxCount)
                throw new VerbArgsException(
                    $"#{thing.thingIDNumber} already holds {giver.BillStack.Count} bills and this "
                    + $"step adds 2, which would pass BillStack.MaxCount ({BillStack.MaxCount}) — a "
                    + "cap the game's UI enforces and BillStack.AddBill does not. Pass "
                    + "clear_bills:true to empty the stack first, or name a different bench.");

            // BillUtility.MakeNewBill, NOT `new Bill_Production(recipe)`.
            // `RimWorld/BillUtility.MakeNewBill` dispatches on the recipe:
            // `Bill_ProductionWithUft` for `UsesUnfinishedThing`,
            // `Bill_ResurrectMech` for `mechResurrection`, `Bill_ProductionMech`
            // for `gestationCycles > 0`, `Bill_Autonomous` for
            // `formingTicks > 0`, and `Bill_Production` otherwise. This step
            // takes a caller-supplied `recipe`, so constructing the base class
            // directly staged the WRONG RUNTIME TYPE for any of those four — the
            // same "silently stages the wrong object" class this issue is filed
            // about, one level down (0d9cbd7 comment #1, point 3). Every one of
            // the four derives from `Bill_Production`, so the property sets below
            // are still valid; the cast is guarded anyway because a mod recipe
            // reaching a modded MakeNewBill is not ours to assume about.
            var bill = chosen.MakeNewBill() as Bill_Production
                ?? throw new VerbArgsException(
                    $"BillUtility.MakeNewBill returned a bill that is not a Bill_Production for "
                    + $"'{chosen.defName}', so the repeat-mode fields this fixture sets do not "
                    + "exist on it — name a different recipe");
            bill.repeatMode = BillRepeatModeDefOf.TargetCount;
            bill.targetCount = ctx.Args.Int("target_count", 20);
            bill.unpauseWhenYouHave = ctx.Args.Int("unpause_when", 5);
            bill.pauseWhenSatisfied = ctx.Args.Bool("pause_when_satisfied", true);
            bill.ingredientSearchRadius = (float)ctx.Args.Num("ingredient_radius", 24);
            bill.allowedSkillRange = new IntRange(ctx.Args.Int("skill_min", 4), ctx.Args.Int("skill_max", 20));
            bill.suspended = ctx.Args.Bool("suspended", false);
            // The god-hand call, named in the class comment. Four lines, checks
            // nothing (RimWorld/BillStack.cs).
            giver.BillStack.AddBill(bill);

            // A second, SUSPENDED bill so the serializer's `suspended` and
            // `state` fields have both values to show in one read.
            var second = chosen.MakeNewBill() as Bill_Production;
            second.repeatMode = BillRepeatModeDefOf.RepeatCount;
            second.repeatCount = 3;
            second.suspended = true;
            giver.BillStack.AddBill(second);

            extras["bill"] = new Dictionary<string, object>
            {
                ["bench_id"] = thing.thingIDNumber,
                // WHERE THE BENCH CAME FROM — `arg` | `chained` | `first-on-map`.
                // The same discipline `Dev.PosArg`'s `pos_source` follows
                // (git-bug 7382bdd), and it is what would have made this issue's
                // original defect visible in the envelope instead of only in a
                // hand-comparison of two ids: `first-on-map` after a `bench`
                // step in the SAME call is now impossible, and seeing it at all
                // means no bench was made and none was named.
                ["bench_source"] = benchSource,
                ["bench_def"] = thing.def?.defName,
                ["recipe"] = chosen.defName,
                ["bill_class"] = bill.GetType().Name,
                ["cleared_first"] = clearFirst ? (object)cleared : null,
                // The hand-computation `bills` must independently arrive at —
                // two readers, one truth (the `stockpile` step's discipline).
                ["expect_bills"] = giver.BillStack.Count,
                ["expect_first"] = new Dictionary<string, object>
                {
                    ["repeat_mode"] = "TargetCount",
                    ["target_count"] = bill.targetCount,
                    ["unpause_when_you_have"] = bill.unpauseWhenYouHave,
                    ["ingredient_radius"] = Math.Round(bill.ingredientSearchRadius, 0),
                    ["skill_range"] = new List<object> { bill.allowedSkillRange.min, bill.allowedSkillRange.max },
                    ["suspended"] = bill.suspended,
                },
                ["expect_second"] = new Dictionary<string, object>
                {
                    ["repeat_mode"] = "RepeatCount",
                    ["repeat_count"] = second.repeatCount,
                    ["suspended"] = true,
                    ["state"] = "suspended",
                },
                ["expect_slots_free"] = Math.Max(0, BillStack.MaxCount - giver.BillStack.Count),
            };
            return chosen.defName + " x2 on #" + thing.thingIDNumber;
        }

        // ---------------------------- stockpiles ----------------------------
        // TWO zones, deliberately different: one at the default preset and
        // Normal priority, one restricted to Foods at Important priority. The
        // acceptance line is "two stockpiles", and the filter-summary open
        // question cannot be answered against two identical default zones.
        private static string Stockpiles(Map map, VerbContext ctx, Dictionary<string, object> extras)
        {
            int side = Math.Max(2, Math.Min(8, ctx.Args.Int("stockpile_side", 4)));
            var anchor = Anchor(map);
            var rectA = FindClearRect(map, anchor, side, side);
            var a = MakeStockpile(map, rectA, StorageSettingsPreset.DefaultStockpile, StoragePriority.Normal, null);
            var rectB = FindClearRect(map, new IntVec3(rectA.maxX + 3, 0, rectA.minZ), side, side);
            var b = MakeStockpile(map, rectB, StorageSettingsPreset.DefaultStockpile, StoragePriority.Important,
                ThingCategoryDefOf.Foods);

            extras["stockpiles"] = new Dictionary<string, object>
            {
                ["a"] = new Dictionary<string, object>
                {
                    ["id"] = a.ID,
                    ["label"] = a.label,
                    ["at"] = Positions.Out(rectA.minX == 0 && rectA.minZ == 0 ? map.Center : new IntVec3(rectA.minX, 0, rectA.minZ)),
                    ["cells"] = a.CellCount,
                    ["expect_priority"] = "Normal",
                    ["expect_filter_state"] = "the default preset: Foods/Manufactured/ResourcesRaw/Items/"
                        + "Buildings/Weapons/Apparel/BodyParts allowed",
                },
                ["b"] = new Dictionary<string, object>
                {
                    ["id"] = b.ID,
                    ["label"] = b.label,
                    ["at"] = Positions.Out(new IntVec3(rectB.minX, 0, rectB.minZ)),
                    ["cells"] = b.CellCount,
                    ["expect_priority"] = "Important",
                    // The interesting case for the tree walk: one category all
                    // in, everything else out.
                    ["expect_filter_state"] = "Foods only (SetDisallowAll then SetAllow(Foods))",
                },
            };
            return $"#{a.ID} ({a.CellCount} cells) + #{b.ID} ({b.CellCount} cells)";
        }

        private static Zone_Stockpile MakeStockpile(Map map, CellRect rect, StorageSettingsPreset preset,
            StoragePriority priority, ThingCategoryDef only)
        {
            var zone = new Zone_Stockpile(preset, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var c in rect) if (ZoneOk(map, c)) zone.AddCell(c);
            if (zone.settings != null)
            {
                zone.settings.Priority = priority;
                if (only != null)
                {
                    zone.settings.filter.SetDisallowAll();
                    zone.settings.filter.SetAllow(only, allow: true);
                }
            }
            return zone;
        }

        // ----------------------------- growing ------------------------------
        // The plant is SET explicitly (SetPlantDefToGrow writes the backing
        // field directly), so the zone has a CONFIGURED plant and the
        // serializer's `plant_configured:true` is exercised. Run with
        // `growing_set_plant:false` to leave it unconfigured, which is the case
        // that proves the serializer does not trip the lazy scribed default —
        // read `zones` twice and the answer must stay null both times.
        private static string Growing(Map map, VerbContext ctx, Dictionary<string, object> extras)
        {
            int side = Math.Max(2, Math.Min(10, ctx.Args.Int("growing_side", 5)));
            var anchor = Anchor(map);
            var rect = FindClearRect(map, anchor, side, side, needsSoil: true);
            var zone = new Zone_Growing(map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var c in rect) if (ZoneOk(map, c)) zone.AddCell(c);
            bool setPlant = ctx.Args.Bool("growing_set_plant", true);
            ThingDef plant = null;
            if (setPlant)
            {
                string name = ctx.Args.Str("growing_plant", "Plant_Potato");
                plant = DefDatabase<ThingDef>.GetNamedSilentFail(name)
                    ?? throw new VerbArgsException($"no ThingDef named '{name}'");
                zone.SetPlantDefToGrow(plant);
            }
            zone.allowCut = ctx.Args.Bool("growing_allow_cut", true);
            zone.allowSow = ctx.Args.Bool("growing_allow_sow", true);

            extras["growing"] = new Dictionary<string, object>
            {
                ["id"] = zone.ID,
                ["label"] = zone.label,
                ["at"] = Positions.Out(new IntVec3(rect.minX, 0, rect.minZ)),
                ["cells"] = zone.CellCount,
                ["expect_plant"] = plant?.defName,
                // The whole point of the unconfigured case: `zones` must report
                // null here on EVERY call, not just the first.
                ["expect_plant_configured"] = setPlant,
                ["expect_planted"] = 0,
            };
            return $"#{zone.ID} ({zone.CellCount} cells, {plant?.defName ?? "unconfigured"})";
        }

        // ----------------------------- research -----------------------------
        private static string Research(VerbContext ctx, Dictionary<string, object> extras)
        {
            var mgr = Find.ResearchManager ?? throw new VerbArgsException("no research manager");
            ResearchProjectDef chosen = null;
            string wanted = ctx.Args.Str("project");
            var defs = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                var p = defs[i];
                if (p == null || p.baseCost <= 0f) continue;
                if (wanted != null) { if (p.defName == wanted) { chosen = p; break; } continue; }
                // Guarded throughout: the ordinary IsFinished/PrerequisitesCompleted
                // would insert a zero entry per project into the scribed
                // progress dictionary (WorldSafe Class A) — from a FIXTURE,
                // which is the worst place to leave that trace.
                if (WorldSafe.Finished(p) || !WorldSafe.PrereqsDone(p)) continue;
                if (p.TechprintCount > 0 && mgr.GetTechprints(p) < p.TechprintCount) continue;
                if (chosen == null || p.baseCost < chosen.baseCost) chosen = p;
            }
            if (chosen == null) throw new VerbArgsException("no unfinished research project with its prerequisites met");
            // The god-hand call, named in the class comment: SetCurrentProject
            // tests only baseCost > 0. The prerequisite check above is ours.
            mgr.SetCurrentProject(chosen);

            float progress = WorldSafe.Progress(chosen);
            extras["research"] = new Dictionary<string, object>
            {
                ["project"] = chosen.defName,
                ["expect_cost"] = Math.Round(chosen.Cost, 0),
                ["expect_progress"] = Math.Round(progress, 0),
                ["expect_pct"] = chosen.Cost > 0f ? WorldSafe.Pct(progress / chosen.Cost) : 0,
                ["expect_tech_level"] = chosen.techLevel.ToString(),
            };
            return chosen.defName;
        }

        // ------------------------------ letter ------------------------------
        // A REAL ChoiceLetter with several distinct option labels, built the way
        // the game builds one. StandardLetter's Choices yield Option_Close, then
        // Option_JumpToLocation when lookTargets is valid, then one
        // Option_ViewInfoCard per hyperlink def — so a letter with two
        // hyperlinks has FOUR options with four different labels, which is what
        // "lists the letter with its exact option labels" needs to be a real
        // test rather than a one-button one.
        private static string Letter(Map map, VerbContext ctx, Dictionary<string, object> extras,
            FixtureChain chain)
        {
            var at = Anchor(map);
            var hyperlinks = new List<ThingDef> { ThingDefOf.Steel, ThingDefOf.MedicineHerbal };
            var letter = LetterMaker.MakeLetter(
                FixtureLetterLabel,
                "Deliberate choice letter (spec 2.4 acceptance). Its options are vanilla-generated: "
                + "Close, jump-to-location, and one info-card option per hyperlinked thing def.",
                LetterDefOf.NeutralEvent,
                new LookTargets(at, map),
                null, null, hyperlinks);
            if (letter == null)
                throw new VerbArgsException("LetterMaker refused NeutralEvent as a choice letter");
            letter.title = ctx.Args.Str("letter_title", "AutoRimmer world fixture");
            int timeout = ctx.Args.Int("letter_timeout_ticks", 0);
            if (timeout > 0) letter.StartTimeout(timeout);
            Find.LetterStack.ReceiveLetter(letter, null, 0, playSound: false);

            // The labels computed HERE, from the game's own DiaOptions, as the
            // independent hand-check `interactions` must agree with. Read with
            // the same field ref the serializer uses, because there is no public
            // accessor for DiaOption.text at all.
            var expect = new List<object>();
            try
            {
                foreach (var opt in letter.Choices)
                {
                    if (opt == null) continue;
                    expect.Add(InteractionOptionLabel(opt));
                }
            }
            catch (Exception e)
            {
                expect.Add("choices threw: " + e.Message);
            }

            // The handle, the other half of git-bug 0d9cbd7. `open-letter` had
            // the identical defect to `bill` and was named nowhere in the
            // issue's own audit list — comment #1's omission.
            chain.Letter = letter;
            extras["letter"] = new Dictionary<string, object>
            {
                ["id"] = letter.ID,
                ["label"] = FixtureLetterLabel,
                ["title"] = letter.title,
                ["type"] = letter.GetType().Name,
                ["at"] = Positions.Out(at),
                ["timeout_ticks"] = timeout,
                ["expect_kind"] = "choice",
                ["expect_option_labels"] = expect,
                ["expect_option_count"] = expect.Count,
            };
            return $"#{letter.ID} with {expect.Count} options";
        }

        // Opens the fixture letter, which stacks a Dialog_NodeTreeWithFactionInfo
        // (forcePause = true from its Dialog_NodeTree base). That is how the
        // window half of `interactions` gets something to describe without
        // waiting for a real event — and it is a MUTATION, which is why it lives
        // in the fixture. `journal-selftest dialogs-clear` is the escape hatch.
        //
        // THE SECOND CHAINING BUG, and the issue's own audit list did not
        // contain it (git-bug 0d9cbd7 comment #1). This resolved the first letter
        // in `LetterStack.LettersListForReading` whose label starts with the
        // fixture prefix — first-match over a shared live list, with an error
        // string that ASSERTS the chaining it did not perform, and an id
        // (`extras["letter"]["id"]`) the `letter` step had already published and
        // nobody consulted. Two `letter` steps in a session and this opened the
        // OLDER one. Fixed the same way `bill` is; `letter_source` says which
        // route answered.
        private static string OpenLetter(Dictionary<string, object> extras, FixtureChain chain)
        {
            var stack = Find.LetterStack?.LettersListForReading;
            Letter found = null;
            string source = null;
            // The chain first, and only while the letter is still STACKED: one
            // that timed out or was dismissed between the two steps is gone, and
            // opening a dead letter would fail somewhere less legible than here.
            if (chain?.Letter != null && stack != null && stack.Contains(chain.Letter))
            {
                found = chain.Letter;
                source = "chained";
            }
            if (found == null && stack != null)
                foreach (var l in stack)
                    if (l != null && l.Label.ToString().StartsWith(FixtureLetterLabel, StringComparison.Ordinal))
                    { found = l; source = "first-in-stack"; break; }
            if (found == null) throw new VerbArgsException("no fixture letter in the stack (run the `letter` step first)");
            found.OpenLetter();
            bool paused = false;
            try { paused = Find.WindowStack != null && Find.WindowStack.WindowsForcePause; }
            catch { }
            extras["open_letter"] = new Dictionary<string, object>
            {
                ["id"] = found.ID,
                ["letter_source"] = source,
                ["force_pause_now"] = paused,
                ["expect_window_type"] = "Dialog_NodeTreeWithFactionInfo",
                ["note"] = "close it with journal-selftest dialogs-clear before advancing",
            };
            return "#" + found.ID;
        }

        // ------------------------------ forbid ------------------------------
        // The session-4 amendment's first blind spot, made reproducible: forbid
        // N loose items so `things` has a non-zero `forbidden` count to report.
        // On a drop-pod start the game does this for you
        // (ScenPart_PlayerPawnsArriveMethod.DoDropPods passes forbid:true), which
        // is exactly why the field exists.
        private static string Forbid(Map map, VerbContext ctx, Dictionary<string, object> extras)
        {
            int want = Math.Max(1, Math.Min(50, ctx.Args.Int("forbid_count", 5)));
            bool value = ctx.Args.Bool("forbid_value", true);
            var pool = new List<Thing>(map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver));
            pool.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
            var touched = new List<object>();
            int items = 0;
            for (int i = 0; i < pool.Count && touched.Count < want; i++)
            {
                var t = pool[i];
                if (t?.def == null || !t.Spawned) continue;
                if (t.Position.Fogged(map)) continue;
                if (!(t is ThingWithComps twc) || twc.GetComp<CompForbiddable>() == null) continue;
                t.SetForbidden(value, warnOnFail: false);
                items += Math.Max(1, t.stackCount);
                touched.Add(new Dictionary<string, object>
                {
                    ["id"] = t.thingIDNumber,
                    ["def"] = t.def.defName,
                    ["count"] = t.stackCount,
                    ["at"] = Positions.Out(t.Position),
                });
            }
            extras["forbid"] = new Dictionary<string, object>
            {
                ["value"] = value,
                ["stacks"] = touched.Count,
                // What `things` must independently report as `totals.forbidden`
                // for a whole-map haulable query.
                ["expect_forbidden_items"] = value ? items : 0,
                ["expect_forbidden_stacks"] = value ? touched.Count : 0,
                ["things"] = touched,
            };
            return touched.Count + " stacks " + (value ? "forbidden" : "unforbidden");
        }

        // ------------------------------- fire -------------------------------
        // The session-4 amendment's second blind spot, made reproducible. The
        // fire is started OUTSIDE the home area on purpose: inside it,
        // Alert_FireInHomeArea fires and the digest would have shown it anyway.
        // Outside it, the alert readout says nothing at all, and only the
        // map-level scan can see it.
        private static string Fire(Map map, VerbContext ctx, Dictionary<string, object> extras)
        {
            bool inHome = ctx.Args.Bool("fire_in_home", false);
            float size = (float)ctx.Args.Num("fire_size", 0.4);
            var home = map.areaManager?.Home;
            var anchor = Anchor(map);
            IntVec3 at = IntVec3.Invalid;
            // Ring outward from the colony until a cell matches the home-area
            // requirement AND can hold a fire at all.
            for (int radius = inHome ? 0 : 14; radius < 90 && !at.IsValid; radius++)
            {
                for (int dx = -radius; dx <= radius && !at.IsValid; dx++)
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != radius) continue;
                        var c = new IntVec3(anchor.x + dx, 0, anchor.z + dz);
                        if (!c.InBounds(map) || c.Fogged(map)) continue;
                        bool atHome = home != null && home[c];
                        if (atHome != inHome) continue;
                        if (FireUtility.ChanceToStartFireIn(c, map) <= 0f) continue;
                        at = c;
                        break;
                    }
            }
            if (!at.IsValid)
                throw new VerbArgsException(inHome
                    ? "no flammable cell inside the home area"
                    : "no flammable cell outside the home area within 90 cells of the colony");
            bool started = FireUtility.TryStartFireIn(at, map, size, null);
            extras["fire"] = new Dictionary<string, object>
            {
                ["at"] = Positions.Out(at),
                ["started"] = started,
                ["in_home_area"] = inHome,
                ["size"] = Math.Round(size, 2),
                // What `fires` (and the `fire` block on `things`) must report.
                // Alert_FireInHomeArea covers only the in-home case, which is
                // the whole reason the scan exists.
                ["expect_outside_home_area"] = inHome ? 0 : 1,
                ["expect_alert"] = inHome ? "Alert_FireInHomeArea active" : "NO vanilla alert",
            };
            return (started ? "fire at " : "refused at ") + at.x + "," + at.z;
        }

        // ---------------------------- helpers -------------------------------

        private static string InteractionOptionLabel(DiaOption opt)
        {
            try
            {
                var r = HarmonyLib.AccessTools.FieldRefAccess<DiaOption, string>("text");
                return r(opt);
            }
            catch { return "?"; }
        }

        // THE THREE SOURCES, IN PRECEDENCE ORDER, and the source is published.
        //
        //  1. `bench` ARG — an explicit id always wins, which is what keeps the
        //     two-call workaround in `accept/32b9e01-orders-makingfor.ps1`
        //     working unchanged (that file's `New-FixtureBench` names the bench
        //     it just made and asserts `data.bill.bench_id` came back equal).
        //  2. THE CHAIN — the bench a `bench` step made in THIS call. This is
        //     the fix: the resolution below was reached even when the answer was
        //     sitting right there, because the steps shared nothing.
        //  3. The lister's first player bill giver — the old behaviour, kept for
        //     `{steps:["bill"]}` on a map that already has a bench, and now
        //     reported as `first-on-map` so a reader can see it happened.
        private static Thing FindBench(Map map, VerbContext ctx, FixtureChain chain,
            out string source)
        {
            var givers = map.listerThings.ThingsInGroup(ThingRequestGroup.PotentialBillGiver);
            if (ctx.Args.Has("bench"))
            {
                int id = ctx.Args.IntReq("bench");
                source = "arg";
                for (int i = 0; i < givers.Count; i++)
                    if (givers[i] != null && givers[i].thingIDNumber == id) return givers[i];
                throw new VerbArgsException($"no bill giver with id {id}");
            }
            if (chain?.Bench != null && chain.Bench.Spawned && chain.Bench is IBillGiver)
            {
                source = "chained";
                return chain.Bench;
            }
            source = "first-on-map";
            Thing best = null;
            for (int i = 0; i < givers.Count; i++)
            {
                var t = givers[i];
                if (t == null || !(t is IBillGiver) || t.Faction != Faction.OfPlayer) continue;
                if (t.def == ThingDefOf.TableButcher) return t;
                if (best == null) best = t;
            }
            if (best == null) throw new VerbArgsException("no player bill giver on the map");
            return best;
        }

        // Zone.AddCell Log.Error's on a cell already in a zone AND on a cell
        // holding a thing with !def.CanOverlapZones (Verse/Zone.cs) — either
        // would breach the standing zero-red-errors invariant from a fixture.
        // Both are checked here instead.
        private static bool ZoneOk(Map map, IntVec3 c)
        {
            if (!c.InBounds(map)) return false;
            if (map.zoneManager.ZoneAt(c) != null) return false;
            var things = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < things.Count; i++)
                if (things[i]?.def != null && !things[i].def.CanOverlapZones) return false;
            return true;
        }

        private static IntVec3 Anchor(Map map)
        {
            // Snapshot: FreeColonistsSpawned clears and rebuilds on every access.
            var colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
            return colonists.Count > 0 ? colonists[0].Position : map.Center;
        }

        // First clear w x h footprint at Chebyshev distance 0,1,2... from
        // `anchor`: unfogged, standable, no edifice, no existing zone, and
        // buildable terrain (or fertile, for a growing zone). Deliberately not
        // CellFinder — the fixture wants a RECT and wants to fail loudly rather
        // than fall back to a cell that will not hold what it is asked to hold.
        private static CellRect FindClearRect(Map map, IntVec3 anchor, int w, int h, bool needsSoil = false)
        {
            for (int ring = 0; ring < 70; ring++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    for (int dz = -ring; dz <= ring; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                        var rect = new CellRect(anchor.x + dx, anchor.z + dz, w, h);
                        if (rect.minX < 1 || rect.minZ < 1
                            || rect.maxX >= map.Size.x - 1 || rect.maxZ >= map.Size.z - 1) continue;
                        bool ok = true;
                        foreach (var c in rect)
                        {
                            var terrain = map.terrainGrid.TerrainAt(c);
                            if (c.Fogged(map) || c.GetEdifice(map) != null || !c.Standable(map)
                                || map.zoneManager.ZoneAt(c) != null
                                || (needsSoil ? terrain.fertility <= 0f
                                              : !terrain.affordances.Contains(TerrainAffordanceDefOf.Heavy)))
                            {
                                ok = false;
                                break;
                            }
                        }
                        if (ok) return rect;
                    }
                }
            }
            throw new VerbArgsException(
                $"no clear {w}x{h} area within 70 cells of ({anchor.x},{anchor.z})"
                + (needsSoil ? " with fertile soil" : ""));
        }
    }
}
