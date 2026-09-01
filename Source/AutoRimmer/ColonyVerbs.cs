using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // The colony's standing orders (spec 2.4): `bills`, `research`, `policies`.
    // Read-only; WorldSafe holds the hazard catalogue and the guarded routes.
    //
    // Two of the three verbs here exist ENTIRELY behind a guarded route,
    // because the obvious implementation of each writes the save:
    //
    //  * `bills` must never call Bill.ShouldDoNow — it writes the scribed
    //    `paused` flag on three paths. WorldSafe.BillState answers the same
    //    question from the stored fields, and `paused` is reported as STORED.
    //  * `research` must never call ResearchProjectDef.IsFinished /
    //    ProgressReal / PrerequisitesCompleted / CanStartNow, nor
    //    RecipeDef.AvailableNow, because every one of them bottoms out in
    //    ResearchManager.GetProgress, which INSERTS a zero entry into a scribed
    //    dictionary on a miss. `AllDefs.Where(p => p.CanStartNow)` would add one
    //    per research project on the bench — six DLCs plus mods — to the save,
    //    on the first call, permanently. WorldSafe.Progress/Finished/PrereqsDone/
    //    CanStart re-derive the whole ladder from the backing dictionaries.
    //
    // That is DESIGN's "the gate lives in the widget, so re-implement it and
    // cite it", in observer form: where the game's own accessor cannot be used,
    // this file names the member it reproduces.
    public static class ColonyVerbs
    {
        public const int BenchCap = 20;
        public const int BillCap = 15;      // BillStack.MaxCount
        public const int ProjectCap = 12;
        public const int FinishedCap = 40;
        public const int TechprintCap = 12;
        public const int PolicyCap = 12;
        public const int DrugEntryCap = 12;
        public const int AssignmentCap = 40;

        // ------------------------------ bills -------------------------------

        [Verb("bills")]
        public static object Bills(VerbContext ctx)
        {
            var map = WorldSafe.CurrentMap();
            bool one = ctx.Args.Has("bench");
            int wantId = one ? ctx.Args.IntReq("bench") : -1;
            int cap = ctx.Args.Int("cap", BenchCap);
            if (cap < 1 || cap > 100) throw new VerbArgsException("cap must be 1..100");
            bool counts = ctx.Args.Bool("counts", true);

            // PotentialBillGiver is `!def.AllRecipes.NullOrEmpty()` — the
            // game's own membership test, and the list is maintained by
            // ListerThings rather than computed here. Snapshot it: the loop
            // below reaches recipe workers and product counters.
            var givers = new List<Thing>(map.listerThings.ThingsInGroup(ThingRequestGroup.PotentialBillGiver));
            givers.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));

            var benches = new List<object>();
            int withBills = 0, skippedFogged = 0, totalBills = 0;
            bool found = false;
            for (int i = 0; i < givers.Count; i++)
            {
                var t = givers[i];
                if (t?.def == null) continue;
                var giver = t as IBillGiver;
                if (giver?.BillStack == null) continue;
                if (one && t.thingIDNumber != wantId) continue;
                if (WorldSafe.Hidden(t, map)) { skippedFogged++; continue; }
                found = true;
                // BillStack.Bills is the real backing list (RimWorld/BillStack.cs).
                var stack = new List<Bill>(giver.BillStack.Bills);
                totalBills += stack.Count;
                if (stack.Count > 0) withBills++;
                // A bench with no bills is noise on an `all` read and is the
                // whole answer on a `bench` read.
                if (!one && stack.Count == 0) continue;
                if (benches.Count >= cap) continue;
                benches.Add(Bench(map, t, stack, counts));
            }
            if (one && !found)
                throw new VerbArgsException(
                    $"no visible bill giver with id {wantId} on the current map "
                    + "(things in unexplored ground are not reported)");

            return new Dictionary<string, object>
            {
                ["benches"] = benches,
                ["benches_with_bills"] = withBills,
                // Every IBillGiver on the map, which is NOT only work tables:
                // ThingRequestGroup.PotentialBillGiver is `!def.AllRecipes
                // .NullOrEmpty()`, and Pawn implements IBillGiver with
                // health.surgeryBills (Verse/Pawn.cs). So a pending operation is
                // a bill and shows up here — correctly, and `kind` says which
                // kind of giver each entry is.
                ["bill_givers_total"] = givers.Count,
                ["benches_more"] = Math.Max(0, withBills - benches.Count),
                ["bills_total"] = totalBills,
                ["order"] = "id-asc",
                ["skipped"] = new Dictionary<string, object> { ["fogged"] = skippedFogged },
            };
        }

        private static Dictionary<string, object> Bench(Map map, Thing t, List<Bill> stack, bool counts)
        {
            var bills = new List<object>();
            for (int i = 0; i < stack.Count && i < BillCap; i++)
            {
                var b = stack[i];
                if (b?.recipe == null) continue;
                bills.Add(BillLine(b, i, counts));
            }
            return new Dictionary<string, object>
            {
                ["id"] = t.thingIDNumber,
                ["def"] = t.def.defName,
                ["label"] = t.def.label,
                // A pawn's bill stack is its SURGERY queue (Verse/Pawn.cs
                // BillStack => health.surgeryBills), which is a real standing
                // order the player reads the same way.
                ["kind"] = t is Pawn ? "pawn" : "bench",
                ["name"] = t is Pawn p ? PawnSafe.Name(p) : null,
                ["at"] = Positions.Out(t.Position),
                ["powered"] = PowerState(t),
                ["bills"] = bills,
                ["bills_total"] = stack.Count,
                ["bills_more"] = Math.Max(0, stack.Count - bills.Count),
                // BillStack.MaxCount is 15 and AddBill does not enforce it
                // (DESIGN §Action model) — 3.x's add-bill verb needs this number.
                ["bill_slots_free"] = Math.Max(0, 15 - stack.Count),
            };
        }

        private static object PowerState(Thing t)
        {
            try
            {
                var comp = (t as ThingWithComps)?.GetComp<CompPowerTrader>();
                return comp == null ? null : (object)comp.PowerOn;
            }
            catch { return null; }
        }

        private static Dictionary<string, object> BillLine(Bill b, int index, bool counts)
        {
            var prod = b as Bill_Production;
            int count = -1;
            if (counts && prod != null && prod.repeatMode == BillRepeatModeDefOf.TargetCount)
            {
                // RecipeWorkerCounter.CountProducts is read-only but NOT cheap:
                // it walks the def's lister list, every minified thing, every
                // haul source, and (with includeEquipped) every colonist's
                // equipment, apparel and inventory. Once per target-count bill,
                // behind `counts`, and never for the other repeat modes.
                try { if (b.recipe.WorkerCounter.CanCountProducts(prod)) count = b.recipe.WorkerCounter.CountProducts(prod); }
                catch { count = -1; }
            }

            var d = new Dictionary<string, object>
            {
                ["index"] = index,
                ["uid"] = WorldSafe.Safe(() => b.GetUniqueLoadID()),
                ["label"] = WorldSafe.Safe(() => b.LabelCap),
                ["recipe"] = b.recipe.defName,
                ["suspended"] = b.suspended,
                // NEVER ShouldDoNow (writes the scribed `paused`); this is the
                // same answer derived from the stored fields.
                ["state"] = WorldSafe.BillState(b, count),
                ["ingredient_radius"] = b.ingredientSearchRadius >= 999f
                    ? (object)"unlimited" : WorldSafe.R(b.ingredientSearchRadius, 0),
                // THE INGREDIENT-SEARCH COOLDOWN, plain public field
                // (RimWorld/Bill.cs). Not scribed — Bill.ExposeData does not
                // touch it — but it is real and it is INVISIBLE in the UI:
                // RimWorld/WorkGiver_DoBill.cs StartOrResumeBillJob skips a bill
                // outright while `TicksGame <= nextTickToSearchForIngredients`,
                // for every pawn, so a bill can read `active` here and be worked
                // by nobody. Vanilla sets it when an ingredient search FAILS, to
                // stop the whole colony re-searching each think tick.
                // Published because a value in the future is the only observable
                // proof that something ran a failing ingredient search on this
                // bill — which is how git-bug 32b9e01 was reproduced, and how a
                // third-party work giver doing the same thing gets caught.
                ["next_ingredient_search_tick"] = b.nextTickToSearchForIngredients,
                ["ingredient_search_cooldown"] = WorldSafe.SafeObj(
                    () => Math.Max(0, b.nextTickToSearchForIngredients - Find.TickManager.TicksGame)),
                ["skill_range"] = new List<object> { b.allowedSkillRange.min, b.allowedSkillRange.max },
                ["pawn_restriction"] = b.PawnRestriction != null
                    ? (object)new Dictionary<string, object>
                    {
                        ["id"] = b.PawnRestriction.thingIDNumber,
                        ["name"] = PawnSafe.Name(b.PawnRestriction),
                    }
                    : null,
                ["slaves_only"] = b.SlavesOnly,
                ["mechs_only"] = b.MechsOnly,
                ["non_mechs_only"] = b.NonMechsOnly,
                ["work_skill"] = b.recipe.workSkill?.defName,
            };

            if (prod != null)
            {
                d["repeat_mode"] = prod.repeatMode?.defName;
                d["repeat_count"] = prod.repeatCount;
                d["target_count"] = prod.targetCount;
                d["current_count"] = count >= 0 ? (object)count : null;
                d["pause_when_satisfied"] = prod.pauseWhenSatisfied;
                d["unpause_when_you_have"] = prod.unpauseWhenYouHave;
                // The STORED flag, not a recomputed one. See `state`.
                d["paused_stored"] = prod.paused;
                d["include_equipped"] = prod.includeEquipped;
                d["include_tainted"] = prod.includeTainted;
                d["limit_to_allowed_stuff"] = prod.limitToAllowedStuff;
                d["store_mode"] = WorldSafe.Safe(() => prod.GetStoreMode()?.defName);
                d["store_group"] = WorldSafe.Safe(() => (prod.GetSlotGroup()?.Settings?.owner as Zone)?.label);
                if (prod.hpRange.min > 0f || prod.hpRange.max < 1f)
                    d["hp_range_pct"] = new List<object> { WorldSafe.Pct(prod.hpRange.min), WorldSafe.Pct(prod.hpRange.max) };
                if (prod.qualityRange.min != QualityCategory.Awful || prod.qualityRange.max != QualityCategory.Legendary)
                    d["quality_range"] = new List<object> { prod.qualityRange.min.ToString(), prod.qualityRange.max.ToString() };
            }

            // The research half of RecipeDef.AvailableNow, RE-DERIVED. The
            // property itself calls ResearchProjectDef.IsFinished, which inserts
            // into the scribed progress dictionary (WorldSafe Class A). The
            // ideo/faction-tag halves of AvailableNow are NOT evaluated — they
            // do not write, but they cannot be reached without the research
            // clauses running first — so `ideo_gated` says when a `false` could
            // still be hiding there. This matters because BillStack.AddBill
            // checks NOTHING (DESIGN §Action model): a bill for an unresearched
            // recipe sits in the stack forever and is never worked.
            var missing = new List<object>();
            try
            {
                if (b.recipe.researchPrerequisite != null && !WorldSafe.Finished(b.recipe.researchPrerequisite))
                    missing.Add(b.recipe.researchPrerequisite.defName);
                if (b.recipe.researchPrerequisites != null)
                    for (int i = 0; i < b.recipe.researchPrerequisites.Count; i++)
                        if (!WorldSafe.Finished(b.recipe.researchPrerequisites[i]))
                            missing.Add(b.recipe.researchPrerequisites[i].defName);
            }
            catch { }
            d["research_ok"] = missing.Count == 0;
            if (missing.Count > 0) d["research_missing"] = missing;
            d["ideo_gated"] = b.recipe.fromIdeoBuildingPreceptOnly
                || b.recipe.memePrerequisitesAny != null
                || b.recipe.factionPrerequisiteTags != null;

            try
            {
                d["ingredient_filter"] = FilterSummary.Build(
                    b.ingredientFilter, b.recipe.fixedIngredientFilter, "recipe-fixed");
            }
            catch { }
            return d;
        }

        // ----------------------------- research -----------------------------

        [Verb("research")]
        public static object Research(VerbContext ctx)
        {
            int cap = ctx.Args.Int("cap", ProjectCap);
            if (cap < 1 || cap > 200) throw new VerbArgsException("cap must be 1..200");
            bool includeFinished = ctx.Args.Bool("include_finished", false);
            var mgr = Find.ResearchManager ?? throw new VerbArgsException("no research manager");

            // PlayerHasAnyAppropriateResearchBench walks every colonist building
            // on every map. Asked once per project below, so memoise it on the
            // only thing it varies with: the required building + facilities,
            // which is per project — keyed by the project itself.
            var benchMemo = new Dictionary<ResearchProjectDef, bool>();
            Func<ResearchProjectDef, bool> benchOk = p =>
            {
                if (benchMemo.TryGetValue(p, out var v)) return v;
                bool ok = false;
                try { ok = p.PlayerHasAnyAppropriateResearchBench; }
                catch { }
                benchMemo[p] = ok;
                return ok;
            };

            // GetProject() with a null category returns `currentProj` directly.
            // GetProject(category) would route through
            // CurrentAnomalyKnowledgeProjects -> EnsureKnowledgeProjectsInitialized,
            // which ADDS to a scribed list; so the anomaly knowledge projects are
            // deliberately not enumerated here.
            ResearchProjectDef current = null;
            try { current = mgr.GetProject(); }
            catch { }

            var available = new List<KeyValuePair<float, Dictionary<string, object>>>();
            var techprints = new List<KeyValuePair<int, Dictionary<string, object>>>();
            var blocked = new Dictionary<string, object>();
            var finishedList = new List<object>();
            int finishedCount = 0, availableCount = 0, total = 0;

            var defs = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                var p = defs[i];
                if (p == null) continue;
                total++;
                if (WorldSafe.Finished(p))
                {
                    finishedCount++;
                    if (includeFinished && finishedList.Count < FinishedCap)
                        finishedList.Add(new Dictionary<string, object>
                        {
                            ["def"] = p.defName,
                            ["label"] = WorldSafe.Safe(() => p.LabelCap.ToString()),
                            ["tech_level"] = p.techLevel.ToString(),
                        });
                    continue;
                }
                if (!WorldSafe.CanStart(p, out string why, benchOk))
                {
                    blocked[why ?? "unknown"] = blocked.TryGetValue(why ?? "unknown", out var n) ? (int)n + 1 : 1;
                    // Techprint needs are reported even for a project that is
                    // blocked BY that need: "you cannot start X until you buy a
                    // techprint" is the actionable half.
                    if (why == "techprints") techprints.Add(TechprintLine(p));
                    continue;
                }
                availableCount++;
                float progress = WorldSafe.Progress(p);
                float cost = p.Cost;
                if (p.TechprintCount > 0) techprints.Add(TechprintLine(p));
                // Ordered before the cut: a project already part-done first,
                // then the cheapest — which is what a player picks off the
                // research tab, and what the agent would most regret losing.
                float score = (progress > 0f ? 1000000f : 0f)
                    + (cost > 0f ? Math.Max(0f, 100000f - cost) : 0f);
                available.Add(new KeyValuePair<float, Dictionary<string, object>>(score, new Dictionary<string, object>
                {
                    ["def"] = p.defName,
                    ["label"] = WorldSafe.Safe(() => p.LabelCap.ToString()),
                    ["cost"] = WorldSafe.R(cost, 0),
                    ["progress"] = WorldSafe.R(progress, 0),
                    ["pct"] = cost > 0f ? WorldSafe.Pct(progress / cost) : 0,
                    ["tech_level"] = p.techLevel.ToString(),
                    ["tab"] = p.tab?.defName,
                    ["knowledge"] = p.knowledgeCategory?.defName,
                    ["techprints_needed"] = p.TechprintCount,
                }));
            }

            available.Sort((a, b) =>
            {
                int c = b.Key.CompareTo(a.Key);
                return c != 0 ? c : string.CompareOrdinal(
                    (string)a.Value["def"] ?? "", (string)b.Value["def"] ?? "");
            });
            var availableList = new List<object>();
            for (int i = 0; i < available.Count && i < cap; i++) availableList.Add(available[i].Value);

            techprints.Sort((a, b) =>
            {
                int c = b.Key.CompareTo(a.Key);
                return c != 0 ? c : string.CompareOrdinal(
                    (string)a.Value["def"] ?? "", (string)b.Value["def"] ?? "");
            });
            var techprintList = new List<object>();
            for (int i = 0; i < techprints.Count && i < TechprintCap; i++) techprintList.Add(techprints[i].Value);

            Dictionary<string, object> cur = null;
            if (current != null)
            {
                float progress = WorldSafe.Progress(current);
                float cost = current.Cost;
                cur = new Dictionary<string, object>
                {
                    ["def"] = current.defName,
                    ["label"] = WorldSafe.Safe(() => current.LabelCap.ToString()),
                    ["cost"] = WorldSafe.R(cost, 0),
                    ["progress"] = WorldSafe.R(progress, 0),
                    ["pct"] = cost > 0f ? WorldSafe.Pct(progress / cost) : 0,
                    ["tech_level"] = current.techLevel.ToString(),
                    // Same fix, same reason, as `research-set` — see M1 finding J
                    // in ResearchVerbs.ResearchSet. `bench_ok` is now "a bench
                    // that can research this exists", not "the gate would pass",
                    // and `bench_required` carries the gate's input. The observer
                    // is where the missing bench was supposed to be visible.
                    ["bench_required"] = current.requiredResearchBuilding?.defName,
                    ["bench_ok"] = benchOk(current),
                };
            }

            var d = new Dictionary<string, object>
            {
                // "backing-field" = read without touching GetProgress, which
                // inserts. "unavailable" = the field ref did not bind, so every
                // progress number below is a floor of zero, not a measurement.
                ["source"] = WorldSafe.ResearchRefsOk ? "backing-field" : "unavailable",
                ["current"] = cur,
                ["available"] = new Dictionary<string, object>
                {
                    ["list"] = availableList,
                    ["total"] = availableCount,
                    ["more"] = Math.Max(0, availableCount - availableList.Count),
                    ["order"] = "started-then-cheapest",
                },
                ["blocked_by"] = blocked,
                ["techprints"] = new Dictionary<string, object>
                {
                    ["list"] = techprintList,
                    ["total"] = techprints.Count,
                    ["more"] = Math.Max(0, techprints.Count - techprintList.Count),
                    ["order"] = "shortfall-desc",
                },
                ["finished_count"] = finishedCount,
                ["projects_total"] = total,
                ["note"] = "anomaly knowledge projects are not enumerated: "
                    + "ResearchManager.CurrentAnomalyKnowledgeProjects initialises "
                    + "and adds to a scribed list on read",
            };
            if (includeFinished)
            {
                d["finished"] = finishedList;
                d["finished_more"] = Math.Max(0, finishedCount - finishedList.Count);
            }
            return d;
        }

        private static KeyValuePair<int, Dictionary<string, object>> TechprintLine(ResearchProjectDef p)
        {
            int applied = 0;
            // GetTechprints is a plain TryGetValue with a 0 default — it does
            // NOT insert, unlike GetProgress right beside it.
            try { applied = Find.ResearchManager.GetTechprints(p); }
            catch { }
            int needed = p.TechprintCount;
            return new KeyValuePair<int, Dictionary<string, object>>(
                Math.Max(0, needed - applied),
                new Dictionary<string, object>
                {
                    ["def"] = p.defName,
                    ["label"] = WorldSafe.Safe(() => p.LabelCap.ToString()),
                    ["applied"] = applied,
                    ["needed"] = needed,
                    ["short_by"] = Math.Max(0, needed - applied),
                    // Techprint is a cached DefDatabase scan on first read; a
                    // def-level memo, not game state.
                    ["techprint_def"] = WorldSafe.Safe(() => p.Techprint?.defName),
                });
        }

        // ----------------------------- policies -----------------------------

        [Verb("policies")]
        public static object Policies(VerbContext ctx)
        {
            var map = WorldSafe.CurrentMap();
            var game = Current.Game ?? throw new VerbArgsException("no active game");

            var outfits = new List<object>();
            var foods = new List<object>();
            var drugs = new List<object>();
            int outfitTotal = 0, foodTotal = 0, drugTotal = 0;

            try
            {
                // AllOutfits is the real backing list. DefaultOutfit() is NOT
                // called: it MAKES a policy when the list is empty. Index 0 IS
                // the default (OutfitDatabase.SetDefault swaps into slot 0), so
                // the flag comes from the position instead.
                var all = game.outfitDatabase?.AllOutfits;
                if (all != null)
                {
                    outfitTotal = all.Count;
                    for (int i = 0; i < all.Count && i < PolicyCap; i++)
                    {
                        var o = all[i];
                        if (o == null) continue;
                        outfits.Add(new Dictionary<string, object>
                        {
                            ["id"] = o.id,
                            ["label"] = o.label,
                            ["default"] = i == 0,
                            ["filter"] = FilterSummary.Build(o.filter, null, "all-thingdefs"),
                        });
                    }
                }
            }
            catch (Exception e) { Journal.EmitWarning("policies: outfits threw: " + e.Message); }

            try
            {
                var all = game.foodRestrictionDatabase?.AllFoodRestrictions;
                if (all != null)
                {
                    foodTotal = all.Count;
                    for (int i = 0; i < all.Count && i < PolicyCap; i++)
                    {
                        var f = all[i];
                        if (f == null) continue;
                        foods.Add(new Dictionary<string, object>
                        {
                            ["id"] = f.id,
                            ["label"] = f.label,
                            ["default"] = i == 0,
                            ["filter"] = FilterSummary.Build(f.filter, null, "all-thingdefs"),
                        });
                    }
                }
            }
            catch (Exception e) { Journal.EmitWarning("policies: food policies threw: " + e.Message); }

            try
            {
                var all = game.drugPolicyDatabase?.AllPolicies;
                if (all != null)
                {
                    drugTotal = all.Count;
                    for (int i = 0; i < all.Count && i < PolicyCap; i++)
                    {
                        var p = all[i];
                        if (p == null) continue;
                        var entries = new List<object>();
                        int entryTotal = 0;
                        try
                        {
                            entryTotal = p.Count;
                            for (int j = 0; j < p.Count && entries.Count < DrugEntryCap; j++)
                            {
                                var e = p[j];
                                if (e?.drug == null) continue;
                                // Only the lines that actually DO something:
                                // a policy with 30 drugs all off is one number,
                                // not thirty lines.
                                if (!e.allowedForAddiction && !e.allowedForJoy
                                    && !e.allowScheduled && e.takeToInventory == 0) continue;
                                entries.Add(new Dictionary<string, object>
                                {
                                    ["drug"] = e.drug.defName,
                                    ["for_addiction"] = e.allowedForAddiction,
                                    ["for_joy"] = e.allowedForJoy,
                                    ["scheduled"] = e.allowScheduled,
                                    ["days_frequency"] = WorldSafe.R(e.daysFrequency, 2),
                                    ["only_if_mood_below"] = WorldSafe.Pct(e.onlyIfMoodBelow),
                                    ["only_if_joy_below"] = WorldSafe.Pct(e.onlyIfJoyBelow),
                                    ["take_to_inventory"] = e.takeToInventory,
                                });
                            }
                        }
                        catch { }
                        drugs.Add(new Dictionary<string, object>
                        {
                            ["id"] = p.id,
                            ["label"] = p.label,
                            ["default"] = i == 0,
                            ["entries"] = entries,
                            ["entries_total"] = entryTotal,
                            ["entries_note"] = "only entries that allow or stock something are listed",
                        });
                    }
                }
            }
            catch (Exception e) { Journal.EmitWarning("policies: drug policies threw: " + e.Message); }

            // The per-pawn half goes through PawnSafe.Policies — 2.2's SHIPPED
            // guarded route, reused rather than re-derived. The public getters
            // (Pawn_OutfitTracker.CurrentApparelPolicy and friends) ASSIGN a
            // default to any pawn that has none and scribe it, so asking a
            // visitor "what outfit policy?" would give them one forever.
            var assignments = new List<object>();
            int assignedTotal = 0;
            var pawns = new List<Pawn>(map.mapPawns.AllPawnsSpawned);
            pawns.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
            for (int i = 0; i < pawns.Count; i++)
            {
                var p = pawns[i];
                if (p == null || p.Dead) continue;
                if (PawnSafe.Hidden(p, map)) continue;
                string cls = PawnSafe.Classify(p);
                if (cls != PawnSafe.ClassColonist && cls != PawnSafe.ClassSlave
                    && cls != PawnSafe.ClassPrisoner) continue;
                assignedTotal++;
                if (assignments.Count >= AssignmentCap) continue;
                var pol = PawnSafe.Policies(p);
                pol["id"] = p.thingIDNumber;
                pol["name"] = PawnSafe.Name(p);
                pol["class"] = cls;
                assignments.Add(pol);
            }

            return new Dictionary<string, object>
            {
                ["outfits"] = new Dictionary<string, object>
                {
                    ["list"] = outfits,
                    ["total"] = outfitTotal,
                    ["more"] = Math.Max(0, outfitTotal - outfits.Count),
                },
                ["food"] = new Dictionary<string, object>
                {
                    ["list"] = foods,
                    ["total"] = foodTotal,
                    ["more"] = Math.Max(0, foodTotal - foods.Count),
                },
                ["drugs"] = new Dictionary<string, object>
                {
                    ["list"] = drugs,
                    ["total"] = drugTotal,
                    ["more"] = Math.Max(0, drugTotal - drugs.Count),
                },
                ["assignments"] = assignments,
                ["assignments_total"] = assignedTotal,
                ["assignments_more"] = Math.Max(0, assignedTotal - assignments.Count),
                ["order"] = "database-order (index 0 is the default policy)",
            };
        }
    }
}
