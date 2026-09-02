using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ============================ git-bug eef837a =============================
    // THE THING-LEVEL ANSWER TO "why is this bill not being worked".
    //
    // WHY THIS FILE EXISTS, and it is a post-mortem rather than a feature note.
    // Run m1-20260901 lost three colonists to a butcher bill that reported
    // healthy and matched nothing. Every field the observer published about that
    // bill was true and none of them was the answer:
    //
    //   * `ingredient_filter` is a DEF-level summary. `Corpse_WildBoar` was in
    //     `allowed_defs` for the whole run.
    //   * The corpse on the butcher spot's own cell was unforbidden, 95% hp,
    //     and — this is the part nothing published — ROTTING.
    //   * `ButcherCorpseFlesh.fixedIngredientFilter` carries
    //     `<specialFiltersToDisallow><li>AllowRotten</li>`, and
    //     `RimWorld/Bill.cs IsFixedOrAllowedIngredient` consults
    //     `recipe.fixedIngredientFilter` BEFORE the bill's own filter. So a
    //     rotting corpse fails at the recipe, not at the bill, and no amount of
    //     `bill-set {allow:…}` could ever have fixed it.
    //
    // A def-level filter summary cannot express that, because the rejection is
    // per THING and per SPECIAL FILTER. This file answers the question the
    // agent actually had — "of the things on this map, how many can this bill
    // use, and what rejected the rest" — by decomposing the game's own
    // predicate clause by clause and NAMING the clause that said no.
    //
    // THE PREDICATE REPRODUCED, in the order RimWorld evaluates it
    // (RimWorld/WorkGiver_DoBill.cs TryFindBestIngredientsHelper's
    // `baseValidator`, over `IsUsableIngredient`):
    //
    //     t.Spawned
    //     && IsUsableIngredient(t, bill)                       <- Bill.IsFixedOrAllowedIngredient
    //                                                             + "some recipe ingredient allows it"
    //     && (t.Position - billGiver.Position).LengthHorizontalSquared < radius^2
    //     && !t.IsForbidden(pawn)
    //     && pawn.CanReserve(t)
    //
    // The last two clauses are PAWN-SCOPED and this is an observer with no
    // pawn, so `forbidden` is answered at the FACTION level
    // (ForbidUtility.IsForbidden(Thing, Faction) — the CompForbiddable read, no
    // allowed-area and no drafted/mental-state branch) and reservation and
    // reachability are NOT answered at all. Both facts are stated in the result
    // rather than left for the reader to assume: an unanswered clause reported
    // as a pass is exactly the lie this file exists to stop.
    //
    // OBSERVERS NEVER MUTATE. Every accessor here is a field read, a HashSet
    // lookup or a public getter whose body is `return <field>;`:
    //   * `ListerThings.ThingsOfDef` -> `listsByDef.TryGetValue`, read-only,
    //     EXCEPT that it `Log.ErrorOnce`s for `ThingDefOf.MinifiedThing` — a
    //     red error — so that def is skipped by name.
    //   * `ThingFilter.AllowedThingDefs` is the real backing HashSet;
    //     `AllowedHitPointsPercents`, `AllowedQualityLevels`,
    //     `AllowedMentalBreakChance`, `CaresAboutHitPoints` and
    //     `OnlySpecialFilters` are all `return <field>;` getters. NOT
    //     `DisplayRootCategory`, which is the lazy-init parallel walk whose
    //     setter writes two configurability flags (FilterSummary's header).
    //   * `SpecialThingFilterDef.Worker` IS a lazy-init getter, but into an
    //     `[Unsaved(false)]` field — it caches a worker instance, scribes
    //     nothing, and `ThingFilter.Allows(Thing)` runs it on every storage
    //     check the game makes anyway.
    //   * `Thing.MaxHitPoints` is a GetStatValue (PawnSafe Class F, read-only
    //     but not cheap) and is asked ONLY when the filter cares about hit
    //     points, through WorldSafe.MaxHpMemo.
    // Nothing here calls `Bill.ShouldDoNow` (writes the scribed `paused`),
    // `RecipeDef.AvailableNow` (writes the research progress dictionary) or
    // constructs a Bill (burns a scribed bill id — DESIGN 2026-08-31 (c)).
    // =========================================================================
    internal static class BillIngredients
    {
        // The universe is "every def some recipe ingredient allows", which for
        // a butchery bill is the whole Corpses category (~180 defs) and for a
        // cooking bill the raw-food tree. A modded recipe with a category-wide
        // ingredient can be larger; the cap is a budget and `truncated` says
        // when it bit, same truncation contract as everywhere else.
        public const int DefCap = 1500;
        public const int ThingCap = 400;
        public const int SampleCap = 6;
        public const int ReasonCap = 12;

        // ==================================================================
        // THE ONE DIAGNOSIS, spoken by `bills`, `bill-add` and `bill-set`.
        //
        // Every field an agent needs to answer "is this bill going to produce
        // anything, and if not what do I do about it", in one block, with a
        // NAMED verdict rather than four numbers to correlate. The run that
        // motivated this had every input to that verdict published separately
        // and drew the wrong conclusion from all of them.
        //
        //   filter_state    published | empty | absent | unavailable
        //                   git-bug eef837a item 3: `filter: null` used to
        //                   mean BOTH "this bill has no ingredientFilter" and
        //                   "the summary threw", and the second is what
        //                   actually happened. Now they are different words
        //                   and the key is never missing.
        //   ingredient_filter   FilterSummary, including the special filters.
        //   ingredient_search   BillWatch's named sleep state (d9d6c12 1 & 2).
        //   ingredient_match    BillIngredients.Scan, the thing-level answer.
        //   health              the verdict, one word.
        //   remedy              the VERB that would fix it, or an honest
        //                       statement that no verb can.
        //
        // `health` is ordered by precedence and its two most important values
        // are the pair d9d6c12 item 3 asks for:
        //   asleep-will-retry               — backed off, and there IS a usable
        //                                     ingredient, so it will pick up.
        //   asleep-no-matching-ingredient   — backed off, and NOTHING on the
        //                                     map can satisfy it. This is the
        //                                     m1-20260901 state, and it must
        //                                     never present as the first one.
        // ==================================================================
        public static Dictionary<string, object> Diagnose(Map map, Thing giver, Bill bill,
            bool scan, int thingCap)
        {
            var d = new Dictionary<string, object>();
            if (bill?.recipe == null)
            {
                d["filter_state"] = "unavailable";
                d["ingredient_filter"] = null;
                d["ingredient_search"] = BillWatch.Block(bill);
                d["ingredient_match"] = null;
                d["health"] = "unknown";
                d["remedy"] = null;
                return d;
            }

            // ---- filter_state: three different facts, three different words -
            string filterState;
            Dictionary<string, object> summary = null;
            if (bill.ingredientFilter == null) filterState = "absent";
            else
            {
                try
                {
                    summary = FilterSummary.Build(bill.ingredientFilter,
                        bill.recipe.fixedIngredientFilter, "recipe-fixed");
                }
                catch { summary = null; }
                if (summary == null) filterState = "unavailable";
                else
                {
                    int n = 0;
                    try { n = bill.ingredientFilter.AllowedDefCount; }
                    catch { }
                    filterState = n == 0 ? "empty" : "published";
                }
            }
            d["filter_state"] = filterState;
            d["ingredient_filter"] = summary;

            // ---- the recipe's own default, for comparison (item 1) ----------
            // `RimWorld/BillUtility.cs MakeNewBill` -> the `Bill(RecipeDef,
            // Precept_ThingStyle)` ctor -> `ingredientFilter.CopyAllowancesFrom
            // (recipe.defaultIngredientFilter)`. That IS what the game's Add
            // Bill button produces, and `Verse/RecipeDef.cs ResolveReferences`
            // guarantees `defaultIngredientFilter` is non-null (it falls back
            // to a copy of `fixedIngredientFilter`). Published so a bill that
            // does NOT match its recipe's default is visible rather than
            // inferred — a Harmony patch or a modded MakeNewBill override is
            // the only way that can happen, and it is exactly the failure
            // eef837a was filed against.
            d["recipe_default_defs"] = WorldSafe.SafeObj(
                () => bill.recipe.defaultIngredientFilter == null
                    ? null : (object)bill.recipe.defaultIngredientFilter.AllowedDefCount);
            d["filter_defs"] = WorldSafe.SafeObj(
                () => bill.ingredientFilter == null
                    ? null : (object)bill.ingredientFilter.AllowedDefCount);

            var search = BillWatch.Block(bill);
            d["ingredient_search"] = search;

            Dictionary<string, object> match = null;
            if (scan && map != null)
            {
                try { match = Scan(map, bill, giver, thingCap); }
                catch { match = null; }
            }
            d["ingredient_match"] = match;

            // ---- the verdict ------------------------------------------------
            object stateRaw = null;
            if (search != null) search.TryGetValue("state", out stateRaw);
            bool asleep = "asleep".Equals(stateRaw as string, StringComparison.Ordinal);
            int usable = -1;
            if (match != null && match.TryGetValue("usable", out var u) && u is int ui) usable = ui;

            string health;
            string remedy = null;
            if (bill.suspended)
            {
                health = "suspended";
                remedy = "`bill-set {suspended:false}` — the row's own suspend button.";
            }
            else if (filterState == "absent")
            {
                health = "no-ingredient-filter";
                remedy = "this bill has NO ingredientFilter at all, which no `bill-set` lever can "
                    + "create. `bill-remove` it and `bill-add` again — a bill built by "
                    + "RecipeDef.MakeNewBill always has one.";
            }
            else if (filterState == "empty")
            {
                health = "filter-empty";
                remedy = "`bill-set {allow:[…]}` — the filter allows ZERO defs, so nothing can "
                    + "ever match it.";
            }
            else if (MissingResearch(bill.recipe, out string proj))
            {
                // BillStack.AddBill checks nothing, so a bill for an
                // unresearched recipe sits in the stack forever and is never
                // worked (ColonyVerbs already publishes `research_ok`). A
                // verdict of `workable` on one of those would be a lie, and the
                // research half of RecipeDef.AvailableNow is re-derived through
                // WorldSafe rather than asked, because IsFinished writes the
                // scribed progress dictionary.
                health = "research-missing";
                remedy = "`research-set {project:\"" + proj + "\"}` — this recipe's research "
                    + "prerequisite is not finished, and no colonist will ever start the bill.";
            }
            else if (match == null) health = "unknown";
            else if (usable > 0) health = asleep ? "asleep-will-retry" : "workable";
            else
            {
                health = asleep ? "asleep-no-matching-ingredient" : "no-matching-ingredient";
                remedy = Remedy(match);
            }
            d["health"] = health;
            d["remedy"] = remedy;
            d["health_note"] = "`workable` means at least one thing on the map passes this bill's "
                + "whole ingredient predicate right now. `asleep-*` means "
                + "WorkGiver_DoBill is skipping the bill for every pawn until `wakes_tick`. "
                + "`asleep-no-matching-ingredient` is NOT a transient: it is the state that "
                + "starved run m1-20260901, and `remedy` names the verb that changes it — or "
                + "says that none does. Reachability and reservation are pawn-scoped and are "
                + "NOT in this verdict; `prioritize {pawn, work, thing}` answers those.";
            return d;
        }

        private static bool MissingResearch(RecipeDef recipe, out string first)
        {
            first = null;
            try
            {
                if (recipe.researchPrerequisite != null && !WorldSafe.Finished(recipe.researchPrerequisite))
                { first = recipe.researchPrerequisite.defName; return true; }
                if (recipe.researchPrerequisites != null)
                    for (int i = 0; i < recipe.researchPrerequisites.Count; i++)
                        if (!WorldSafe.Finished(recipe.researchPrerequisites[i]))
                        { first = recipe.researchPrerequisites[i].defName; return true; }
            }
            catch { }
            return false;
        }

        // The top rejection reason, turned into the verb that addresses it.
        // The `recipe-fixed:` branch is the one worth the file: no bill lever
        // can widen past `recipe.fixedIngredientFilter`, and an agent that does
        // not know that will retry `bill-set` forever — which is what happened.
        private static string Remedy(Dictionary<string, object> match)
        {
            string top = null;
            if (match != null && match.TryGetValue("rejected", out var r)
                && r is Dictionary<string, object> reasons)
                foreach (var kv in reasons) { top = kv.Key; break; }   // already count-sorted
            if (top == null)
                return "nothing on the map is even a candidate for this recipe's ingredients — "
                    + "produce or haul some in. `things {def:…}` says what exists.";
            if (top == "forbidden")
                return "`unforbid {thing:<id>}` (or `unforbid {rect:…}`) — the candidates exist "
                    + "and are forbidden. See `rejected_sample[].id`.";
            if (top == "fogged")
                return "the only candidates are under unexplored ground; nothing to act on yet.";
            if (top == "out-of-search-radius")
                return "`bill-set {ingredient_radius:\"unlimited\"}` — candidates exist outside "
                    + "Bill.ingredientSearchRadius.";
            if (top.StartsWith("recipe-fixed:special:", StringComparison.Ordinal))
                return "NO BILL LEVER FIXES THIS. `" + top.Substring("recipe-fixed:special:".Length)
                    + "` is disallowed by the RECIPE's own fixedIngredientFilter, which "
                    + "Bill.IsFixedOrAllowedIngredient consults before the bill's filter. For "
                    + "ButcherCorpseFlesh that filter is `specialFiltersToDisallow: [AllowRotten]`, "
                    + "so a corpse past CompRottable's 2.5-day rot start can NEVER be butchered by "
                    + "it — see `rejected_sample[].rot_stage`. Butcher kills while they are Fresh, "
                    + "or find a recipe whose fixed filter accepts them.";
            if (top.StartsWith("recipe-fixed:", StringComparison.Ordinal))
                return "NO BILL LEVER FIXES THIS: the candidates fail the RECIPE's own "
                    + "fixedIngredientFilter (" + top + "), which `bill-set` cannot widen past. "
                    + "A different recipe, or different ingredients.";
            if (top.StartsWith("bill-filter:special:", StringComparison.Ordinal))
                return "`bill-set {special:{\"" + top.Substring("bill-filter:special:".Length)
                    + "\":true}}` — this bill's own filter disallows that special filter.";
            if (top.StartsWith("bill-filter:", StringComparison.Ordinal))
                return "`bill-set {allow:[…]}` — the candidates fail THIS BILL's filter (" + top
                    + "), which is a lever you have.";
            if (top == "not-a-recipe-ingredient")
                return "the things on the map are not ingredients of this recipe at all.";
            return "top rejection reason: " + top;
        }

        // ------------------------------------------------------------------
        // The block published on a bill line. Never null, never a bare count:
        // `usable` is the number the caller acts on and `rejected` is why the
        // rest are not it.
        // ------------------------------------------------------------------
        public static Dictionary<string, object> Scan(Map map, Bill bill, Thing billGiver, int thingCap)
        {
            var d = new Dictionary<string, object>
            {
                ["scanned"] = 0,
                ["usable"] = 0,
                ["usable_sample"] = new List<object>(),
                ["rejected"] = new Dictionary<string, object>(),
                ["rejected_sample"] = new List<object>(),
                ["truncated"] = false,
                ["clauses_not_checked"] = new List<object> { "reachable", "reservable" },
                ["forbidden_scope"] = "faction",
            };
            if (map == null || bill?.recipe == null) { d["unavailable"] = "no map or no recipe"; return d; }

            var reasons = new Dictionary<string, int>();
            var rejectSample = new List<object>();
            var usableSample = new List<object>();
            int scanned = 0, usable = 0;
            bool truncated = false;
            var hp = new WorldSafe.MaxHpMemo();

            // The search-radius clause needs the bill giver's cell. A bill on a
            // Pawn (a surgery queue) has one too — the pawn's own position —
            // and `Bill.ingredientSearchRadius` applies there identically.
            IntVec3 giverPos = IntVec3.Invalid;
            try { if (billGiver != null && billGiver.Spawned) giverPos = billGiver.Position; }
            catch { }
            float radius = 999f;
            try { radius = bill.ingredientSearchRadius; }
            catch { }
            float radiusSq = radius * radius;

            var defs = new List<ThingDef>();
            try { CollectUniverse(bill.recipe, defs); }
            catch { }
            if (defs.Count >= DefCap) truncated = true;

            var lister = map.listerThings;
            for (int i = 0; i < defs.Count; i++)
            {
                if (scanned >= thingCap) { truncated = true; break; }
                var def = defs[i];
                List<Thing> things = null;
                try { things = lister.ThingsOfDef(def); }
                catch { }
                if (things == null || things.Count == 0) continue;
                for (int j = 0; j < things.Count; j++)
                {
                    if (scanned >= thingCap) { truncated = true; break; }
                    var t = things[j];
                    if (t == null) continue;
                    scanned++;

                    // The fog rule (DESIGN 2026-08-30) applies to every
                    // player-facing read: a thing under fog is not reported,
                    // and the COUNT of them is not a leak the way a def name
                    // would be.
                    if (WorldSafe.Hidden(t, map)) { Bump(reasons, "fogged"); continue; }

                    string why = Reject(bill, t, giverPos, radiusSq, hp);
                    if (why == null)
                    {
                        usable++;
                        if (usableSample.Count < SampleCap) usableSample.Add(Line(t, null));
                        continue;
                    }
                    Bump(reasons, why);
                    if (rejectSample.Count < SampleCap) rejectSample.Add(Line(t, why));
                }
            }

            d["scanned"] = scanned;
            d["usable"] = usable;
            d["usable_sample"] = usableSample;
            d["rejected_sample"] = rejectSample;
            d["truncated"] = truncated;
            d["rejected"] = TopReasons(reasons);
            d["defs_in_universe"] = defs.Count;
            d["search_radius"] = radius >= 999f ? (object)"unlimited" : WorldSafe.R(radius, 0);
            d["note"] = "WorkGiver_DoBill's own ingredient predicate, clause by clause "
                + "(TryFindBestIngredientsHelper's baseValidator over IsUsableIngredient). "
                + "`usable:0` with things on the map is the state that killed run m1-20260901. "
                + "A `recipe-fixed:*` reason is NOT fixable with `bill-set` — it is the RECIPE's "
                + "own fixedIngredientFilter, which Bill.IsFixedOrAllowedIngredient consults "
                + "before the bill's filter. `reachable` and `reservable` are pawn-scoped and are "
                + "NOT evaluated here; `prioritize {work, thing}` answers those for one pawn.";
            return d;
        }

        // Every def any recipe ingredient allows. This is the second half of
        // `WorkGiver_DoBill.IsUsableIngredient` (`ingredient.filter.Allows(t)`)
        // and therefore the largest set a thing could come from; the bill's own
        // filter and the recipe's fixed filter narrow it, and NAMING which one
        // narrowed it is the whole point.
        private static void CollectUniverse(RecipeDef recipe, List<ThingDef> into)
        {
            var seen = new HashSet<ThingDef>();
            if (recipe.ingredients == null) return;
            for (int i = 0; i < recipe.ingredients.Count; i++)
            {
                var ing = recipe.ingredients[i];
                if (ing?.filter == null) continue;
                foreach (var def in ing.filter.AllowedThingDefs)
                {
                    if (def == null || !seen.Add(def)) continue;
                    // ListerThings.ThingsOfDef Log.ErrorOnce's on this def and
                    // tells the caller to use the MinifiedThing group instead.
                    // A red error out of an observer is a defect on its own.
                    if (def == ThingDefOf.MinifiedThing) continue;
                    into.Add(def);
                    if (into.Count >= DefCap) return;
                }
            }
        }

        // ------------------------------------------------------------------
        // null == this thing is a usable ingredient. Otherwise the NAME of the
        // clause that rejected it, in the game's own evaluation order.
        // ------------------------------------------------------------------
        private static string Reject(Bill bill, Thing t, IntVec3 giverPos, float radiusSq,
            WorldSafe.MaxHpMemo hp)
        {
            try
            {
                if (!t.Spawned) return "not-spawned";

                // RimWorld/Bill.cs IsFixedOrAllowedIngredient, decomposed. Its
                // first loop short-circuits the whole filter chain for a
                // recipe ingredient that is a single fixed def.
                bool fixedIngredient = false;
                var ings = bill.recipe.ingredients;
                bool anyRecipeIngredientAllows = false;
                if (ings != null)
                    for (int i = 0; i < ings.Count; i++)
                    {
                        var ing = ings[i];
                        if (ing?.filter == null) continue;
                        bool allows = ing.filter.Allows(t);
                        if (allows) anyRecipeIngredientAllows = true;
                        if (allows && ing.IsFixedIngredient) fixedIngredient = true;
                    }

                if (!fixedIngredient)
                {
                    string fixedWhy = WhyRejected(bill.recipe.fixedIngredientFilter, t, hp);
                    if (fixedWhy != null) return "recipe-fixed:" + fixedWhy;
                    string billWhy = WhyRejected(bill.ingredientFilter, t, hp);
                    if (billWhy != null) return "bill-filter:" + billWhy;
                }

                // IsUsableIngredient's second half: past the filters, the thing
                // still has to satisfy at least one of the recipe's own
                // IngredientCounts.
                if (!anyRecipeIngredientAllows) return "not-a-recipe-ingredient";

                if (giverPos.IsValid && radiusSq < 999f * 999f)
                {
                    float dsq = (t.Position - giverPos).LengthHorizontalSquared;
                    if (dsq >= radiusSq) return "out-of-search-radius";
                }

                // The pawn-scoped clause, answered at the only scope an
                // observer has. ForbidUtility.IsForbidden(Thing, Faction) is
                // the CompForbiddable read; the PAWN overload adds the allowed
                // area, the drafted/mental-state bypass and the lord checks,
                // none of which an observer can answer without choosing a pawn.
                if (t.IsForbidden(Faction.OfPlayer)) return "forbidden";
            }
            catch (Exception e) { return "exception:" + e.GetType().Name; }
            return null;
        }

        // Verse/ThingFilter.cs Allows(Thing), clause for clause, returning the
        // clause NAME instead of a bool. A `null` filter allows everything,
        // which is what a missing `fixedIngredientFilter` means to the game.
        private static string WhyRejected(ThingFilter f, Thing t, WorldSafe.MaxHpMemo hp)
        {
            if (f == null) return null;
            Thing inner = t;
            try { inner = t.GetInnerIfMinified(); } catch { }
            if (inner?.def == null) return null;

            bool onlySpecial = false;
            try { onlySpecial = f.OnlySpecialFilters; } catch { }
            if (!onlySpecial)
            {
                bool allowsDef = false;
                try { allowsDef = f.Allows(inner.def); } catch { allowsDef = true; }
                if (!allowsDef) return "def-not-allowed";
            }

            try
            {
                if (inner.def.useHitPoints && f.CaresAboutHitPoints)
                {
                    int max = hp.Of(inner);
                    if (max > 0)
                    {
                        float frac = (float)inner.HitPoints / max;
                        if (!f.AllowedHitPointsPercents.IncludesEpsilon(Mathf01(frac)))
                            return "hit-points-out-of-range";
                    }
                }
            }
            catch { }

            try
            {
                var q = f.AllowedQualityLevels;
                if ((q.min != QualityCategory.Awful || q.max != QualityCategory.Legendary)
                    && inner.TryGetQuality(out var qc) && !q.Includes(qc))
                    return "quality-out-of-range";
            }
            catch { }

            // THE CLAUSE THAT KILLED THE COLONY. `AllowRotten` sits in
            // ButcherCorpseFlesh's fixedIngredientFilter's specialFiltersToDisallow,
            // so `SpecialThingFilterWorker_Rotten.Matches` (CompRottable.Stage
            // != Fresh) rejects every corpse past 2.5 days — with the def still
            // sitting in `allowed_defs`, which is why the def-level summary
            // read healthy for the whole run.
            try
            {
                var all = DefDatabase<SpecialThingFilterDef>.AllDefsListForReading;
                for (int i = 0; i < all.Count; i++)
                {
                    var sf = all[i];
                    if (sf == null || f.Allows(sf)) continue;      // not disallowed here
                    if (!sf.Worker.Matches(inner)) continue;
                    if (onlySpecial || inner.def.IsWithinCategory(sf.parentCategory))
                        return "special:" + sf.defName;
                }
            }
            catch { }
            return null;
        }

        private static float Mathf01(float f)
        {
            // GenMath.RoundedHundredth then Clamp01, exactly as ThingFilter does.
            float r = (float)Math.Round(f * 100f) / 100f;
            return r < 0f ? 0f : (r > 1f ? 1f : r);
        }

        private static void Bump(Dictionary<string, int> d, string k)
        {
            d[k] = d.TryGetValue(k, out var n) ? n + 1 : 1;
        }

        // Deterministic order: count descending, then name — rwtest asserts on
        // the list rather than on a set.
        private static Dictionary<string, object> TopReasons(Dictionary<string, int> reasons)
        {
            var keys = new List<string>(reasons.Keys);
            keys.Sort((a, b) =>
            {
                int c = reasons[b].CompareTo(reasons[a]);
                return c != 0 ? c : string.CompareOrdinal(a, b);
            });
            var d = new Dictionary<string, object>();
            for (int i = 0; i < keys.Count && i < ReasonCap; i++) d[keys[i]] = reasons[keys[i]];
            return d;
        }

        private static Dictionary<string, object> Line(Thing t, string why)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = t.thingIDNumber,
                ["def"] = t.def?.defName,
                ["at"] = WorldSafe.SafeObj(() => Positions.Out(t.Position)),
            };
            if (why != null) d["why"] = why;
            // The two facts a reader needs to act: rot stage names the
            // `special:AllowRotten` case in words, and `forbidden` names the
            // one verb that fixes its own reason.
            d["rot_stage"] = WorldSafe.Safe(() =>
            {
                var comp = t.TryGetComp<CompRottable>();
                return comp == null ? null : comp.Stage.ToString();
            });
            d["forbidden"] = WorldSafe.SafeObj(() => t.IsForbidden(Faction.OfPlayer));
            return d;
        }
    }
}
