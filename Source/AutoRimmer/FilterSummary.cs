using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // How a ThingFilter is compressed for an LLM (spec 2.4 open question 2:
    // "how filter summaries compress — storage filters are trees").
    //
    // RESOLUTION, and why the two obvious routes are both wrong:
    //
    //  * ThingFilter.Summary reads the XML-only `thingDefs`/`categories` lists
    //    in its first two branches. A runtime storage filter has neither, and
    //    a real one never has exactly one allowed def, so Summary falls all the
    //    way through to the literal "UsableIngredients" translation — the same
    //    four words for every stockpile on the map. Read-only, and useless.
    //  * ThingFilter.DisplayRootCategory — the field the storage tab draws from
    //    — is a lazy-init getter that runs a GenThreading.ParallelFor over every
    //    ThingCategory node crossed with every allowed def, and whose SETTER
    //    then writes allowedHitPointsConfigurable and allowedQualitiesConfigurable.
    //    An observer must not be what runs it.
    //
    // So the tree is walked here, from ThingCategoryDefOf.Root, using only
    // `filter.AllowedThingDefs` (the real HashSet) and `def.thingCategories`
    // (a plain list field). Counts are built in ONE bottom-up pass: each def
    // walks its categories to the root once, incrementing `total` for the
    // universe and `allowed` for the filter. The walk then descends only into
    // nodes that are PARTIALLY allowed — a node that is all-in or all-out is
    // one line and its whole subtree is dropped, which is the compression.
    //
    // `universe` is the denominator and is always named in the output:
    //  * a stockpile passes its own GetParentStoreSettings().filter — the
    //    EverStorable fixed filter, i.e. exactly what the tab could offer;
    //  * a bill passes recipe.fixedIngredientFilter;
    //  * a policy filter has no natural parent and passes null, which falls
    //    back to every ThingDef in the category (`universe:"all-thingdefs"`).
    // Without that field an "8/12" would be uninterpretable.
    public static class FilterSummary
    {
        public const int NodeCap = 24;
        public const int DepthCap = 4;
        public const int DefListCap = 12;

        public static Dictionary<string, object> Build(ThingFilter filter, ThingFilter universe, string universeName)
        {
            if (filter == null) return null;
            var allowed = new Dictionary<ThingCategoryDef, int>();
            var total = new Dictionary<ThingCategoryDef, int>();
            int allowedCount = 0;

            // The allowed set first: it is the small one, and it is the real
            // backing HashSet (ThingFilter.AllowedThingDefs => allowedDefs).
            var allowedDefs = new List<ThingDef>();
            try
            {
                foreach (var def in filter.AllowedThingDefs)
                {
                    if (def == null) continue;
                    allowedDefs.Add(def);
                    allowedCount++;
                    Credit(allowed, def);
                }
            }
            catch { }

            // Then the denominator.
            try
            {
                if (universe != null)
                {
                    foreach (var def in universe.AllowedThingDefs)
                        if (def != null) Credit(total, def);
                }
                else
                {
                    total = AllDefTotals();
                }
            }
            catch { }

            var nodes = new List<object>();
            int emitted = 0, dropped = 0;
            try
            {
                Walk(ThingCategoryDefOf.Root, allowed, total, nodes, 0, ref emitted, ref dropped);
            }
            catch { }

            var d = new Dictionary<string, object>
            {
                ["allowed_defs"] = allowedCount,
                ["universe"] = universeName ?? (universe != null ? "parent-filter" : "all-thingdefs"),
                ["categories"] = nodes,
                ["categories_more"] = dropped,
            };

            // A short filter is better shown verbatim than as a tree: "steel,
            // plasteel" beats "ResourcesRaw 2/38". Sorted for determinism.
            if (allowedCount > 0 && allowedCount <= DefListCap)
            {
                allowedDefs.Sort((a, b) => string.CompareOrdinal(a.defName, b.defName));
                var names = new List<object>();
                for (int i = 0; i < allowedDefs.Count; i++) names.Add(allowedDefs[i].defName);
                d["defs"] = names;
            }

            // The two special filters the storage tab draws as sliders. Both
            // getters are plain field reads; `CaresAboutHitPoints` folds in
            // allowedHitPointsConfigurable, which is a plain field too.
            try
            {
                if (filter.CaresAboutHitPoints)
                    d["hp_range_pct"] = new List<object>
                    {
                        WorldSafe.Pct(filter.AllowedHitPointsPercents.min),
                        WorldSafe.Pct(filter.AllowedHitPointsPercents.max),
                    };
            }
            catch { }
            try
            {
                var q = filter.AllowedQualityLevels;
                if (q.min != QualityCategory.Awful || q.max != QualityCategory.Legendary)
                    d["quality_range"] = new List<object> { q.min.ToString(), q.max.ToString() };
            }
            catch { }
            return d;
        }

        // The fallback denominator: every ThingDef, by category. Built ONCE —
        // the DefDatabase does not change at runtime — because `policies` calls
        // Build once per outfit, food and drug policy and a full def walk per
        // call would be two dozen passes over four thousand defs for an answer
        // that cannot have changed. Reset with the game only in the sense that
        // it never needs resetting; a reload does not add defs.
        private static Dictionary<ThingCategoryDef, int> allDefTotals;

        private static Dictionary<ThingCategoryDef, int> AllDefTotals()
        {
            if (allDefTotals != null) return allDefTotals;
            var d = new Dictionary<ThingCategoryDef, int>();
            var all = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++) if (all[i] != null) Credit(d, all[i]);
            allDefTotals = d;
            return d;
        }

        // One def credits every category on its path to the root, once each —
        // a def can sit in two categories that share an ancestor, and double
        // counting there would make an "all" read as "12/8".
        private static void Credit(Dictionary<ThingCategoryDef, int> into, ThingDef def)
        {
            var cats = def.thingCategories;
            if (cats == null) return;
            var seen = new HashSet<ThingCategoryDef>();
            for (int i = 0; i < cats.Count; i++)
                for (var c = cats[i]; c != null; c = c.parent)
                {
                    if (!seen.Add(c)) break; // this path already credited above
                    into[c] = into.TryGetValue(c, out var n) ? n + 1 : 1;
                }
        }

        // Descend only where the answer is not yet decided. `all`/`none` end a
        // subtree; `some` recurses until the depth cap, then reports the ratio
        // and stops. The cap is a budget, and `categories_more` says what it
        // cost — the truncation contract, same as everywhere else.
        private static void Walk(ThingCategoryDef node,
            Dictionary<ThingCategoryDef, int> allowed, Dictionary<ThingCategoryDef, int> total,
            List<object> into, int depth, ref int emitted, ref int dropped)
        {
            if (node == null) return;
            int a = allowed.TryGetValue(node, out var av) ? av : 0;
            int t = total.TryGetValue(node, out var tv) ? tv : 0;
            if (t == 0 && a == 0) return;

            string state = a == 0 ? "none" : (a >= t ? "all" : "some");
            bool leaf = state != "some" || depth >= DepthCap
                        || node.childCategories == null || node.childCategories.Count == 0;

            // Only leaves of the WALK are lines, so the emitted set partitions
            // the filter instead of double-counting a parent beside its own
            // children. The root is never a line: it is the whole filter, which
            // `allowed_defs` already states.
            if (leaf && node != ThingCategoryDefOf.Root)
            {
                if (emitted >= NodeCap) { dropped++; }
                else
                {
                    emitted++;
                    into.Add(new Dictionary<string, object>
                    {
                        ["cat"] = node.defName,
                        ["label"] = node.label,
                        ["state"] = state,
                        ["allowed"] = a,
                        ["total"] = t,
                    });
                }
            }
            if (leaf) return;

            // Deterministic order: children by defName, so rwtest can assert on
            // the list rather than on a set.
            var kids = new List<ThingCategoryDef>(node.childCategories);
            kids.Sort((x, y) => string.CompareOrdinal(x?.defName ?? "", y?.defName ?? ""));
            for (int i = 0; i < kids.Count; i++)
                Walk(kids[i], allowed, total, into, depth + 1, ref emitted, ref dropped);
        }
    }
}
