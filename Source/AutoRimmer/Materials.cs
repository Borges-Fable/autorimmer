using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // ============================================== git-bug 54b0c9a =========
    // CAN THE COLONY ACTUALLY BUILD THIS — asked the way the BUILDER asks it.
    //
    // WHAT WENT WRONG, and it was a measurement rather than a review note.
    // `place-layout templates/bedroom.ir.json --stuff '*=WoodLog'` on a fresh
    // quicktest map reported
    //
    //     materials  [{"def":"WoodLog","count":185,"in_stockpiles":0}]
    //     shortfall  [{"def":"WoodLog","needed":185,"in_stockpiles":0,"short_by":185}]
    //
    // with 869 unforbidden WoodLog lying ten cells from the site and no
    // stockpile zone anywhere on the map. Danielle then hauled and built all 22
    // elements without a single stall. Nothing was short.
    //
    // `LayoutVerbs.MaterialBill`'s own header already stated the true fact —
    // `in_stockpiles` is `map.resourceCounter`, which walks SlotGroup haul
    // destinations, so goods on unzoned ground read as ZERO, and "we have no
    // steel" and "the steel is not in a stockpile" are different problems this
    // number cannot tell apart. The comment was right and it indicted the code
    // three lines below it: `in_stockpiles` is a MEASUREMENT whose name says
    // what it measured, and `short_by` was a CONCLUSION drawn from it anyway.
    // The agent was handed a verdict manufactured from a partial count with the
    // disclaimer sitting in a source comment it cannot read — the project's own
    // "candidates + reasons, never bare booleans" rule at one remove.
    //
    // It is not a corner case. It is the DEFAULT state of every fresh map:
    // quicktest generates no stockpile.
    //
    // ------------------- THE GAME'S OWN AVAILABILITY TEST --------------------
    // Verified in the decompiled 1.6 tree, BY MEMBER:
    //
    //  * `RimWorld/WorkGiver_ConstructDeliverResources.ResourceDeliverJobFor`
    //    reaches material through `GenClosest.ClosestThingReachable(
    //    pawn.Position, pawn.Map, ThingRequest.ForDef(need.thingDef),
    //    PathEndMode.ClosestTouch, TraverseParms.For(pawn), 9999f,
    //    (Thing r) => ResourceValidator(pawn, need, r))`.
    //  * `WorkGiver_ConstructDeliverResources.ResourceValidator` is THREE
    //    clauses and no more: `th.def != need.thingDef`, `th.IsForbidden(pawn)`,
    //    `!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, th, forced: false)`.
    //  * `WorkGiver_ConstructDeliverResources.MaxPathDanger` is
    //    `return Danger.Deadly;`, which is the danger the reach above runs at.
    //  * `Verse.AI/HaulAIUtility.PawnCanAutomaticallyHaulFast` is
    //    `t.Fogged()`, `p.CanReserve(t, 1, -1, null, forced)`,
    //    `p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)`, an
    //    `UnfinishedThing`/`BoundBill` clause, `p.CanReach(t,
    //    PathEndMode.ClosestTouch, p.NormalMaxDanger())`, a socially-proper
    //    clause for human-edible ingestibles, and `t.IsBurning()`.
    //  * **`SlotGroup`, `slotGroup`, `haulDestination`, `Stockpile` and
    //    `resourceCounter` appear NOWHERE in that work giver.** Grepped over
    //    the whole file, unpiped.
    //
    // So the game asks "is there a reachable, unforbidden stack of this def"
    // and never asks whether it is zoned. This class asks the game's question.
    //
    // ---------------------- WHAT IS AND IS NOT REPRODUCED --------------------
    // Reproduced, clause for clause: def match, `Fogged()`, `IsBurning()`,
    // `IsForbidden(pawn)`, `CanReach(…, ClosestTouch, Danger.Deadly)` — the
    // last two asked of the REAL pawns who would take the job, which is what
    // makes this the builder's question rather than an approximation of it.
    // Manipulation capacity is applied when CHOOSING those pawns, which is the
    // same clause one level up.
    //
    // NOT reproduced, and named rather than hidden:
    //  * `p.CanReserve(t, …)`. A stack somebody is already hauling is reserved,
    //    and calling that "missing" would be exactly the wrong answer — the
    //    material is on its way. Reservations are also per-tick churn, and a
    //    bill is not a job.
    //  * The `UnfinishedThing`/`BoundBill` clause. It cannot apply: an
    //    unfinished thing is not a construction material.
    //  * The socially-proper clause, which fires only for human-edible
    //    ingestibles and no building is made of those.
    //  * `GenClosest`'s 9999-cell radius and
    //    `FindAvailableNearbyResources`'s 5-cell multi-pickup widening. Both
    //    are about which stack a pawn walks to, not whether any exists.
    //
    // ------------------------- WHAT IT COSTS TO ASK --------------------------
    // `map.listerThings.ThingsOfDef(def)` is a stored list (snapshotted, since
    // the loop below reaches def flags and the pathfinder). Then at most
    // `StackCap` stacks x builders `CanReach` calls, short-circuited on the
    // first pass. `Reachability.CanReach` fills a `ReachabilityCache` — an
    // OBSERVER WRITE, and the same ruling `CostListCalculator.cachedCosts` and
    // `DangerWatcher.dangerRatingInt` already got: idempotent, RNG-free, never
    // scribed, and filled by the game itself on every haul job. Nothing scribed
    // changes and the shared `Rand` stream is untouched, which is the project's
    // real observer invariant.
    //
    // This is a per-CALL cost on a read, never a per-frame one: no predicate
    // and no digest section reaches this file.
    public static class Materials
    {
        // Stacks considered per def. 869 wood on a quicktest map is ~20 stacks;
        // a hoarder's map is not 200. Reported when it bites, never silently.
        private const int StackCap = 200;
        // Builders whose reach is tried. Reaching is a per-pawn question and
        // the answer is "somebody can", so more than a handful buys nothing.
        private const int BuilderCap = 8;

        public sealed class Availability
        {
            public ThingDef Def;
            // The MEASUREMENT, kept beside the conclusion rather than replaced
            // by it. `map.resourceCounter` is the number the game's own
            // resource readout shows, and it remains the right answer to "what
            // is stockpiled".
            public int InStockpiles;
            // The CONCLUSION, and now it comes from the builder's own test.
            public int Available;
            // Why the rest is not available, as amounts, so a caller can act on
            // the difference. `unforbid` fixes one of these and not the others.
            public int Forbidden;
            public int Unreachable;
            public int Fogged;
            public int Burning;
            public int Stacks;
            public int StacksMore;
            public int Builders;
            public string Basis;

            public void Fill(Dictionary<string, object> row)
            {
                row["available"] = Available;
                row["in_stockpiles"] = InStockpiles;
                row["availability_basis"] = Basis;
                if (Forbidden > 0) row["forbidden"] = Forbidden;
                if (Unreachable > 0) row["unreachable"] = Unreachable;
                if (Fogged > 0) row["fogged"] = Fogged;
                if (Burning > 0) row["burning"] = Burning;
                if (StacksMore > 0)
                {
                    row["stacks_scanned"] = Stacks;
                    row["stacks_more"] = StacksMore;
                }
            }

            // The sentence that separates the three problems `short_by` used to
            // conflate. An agent reads `hint` and knows whether to send
            // `unforbid`, to go mining, or to fix a path.
            public string Hint(int shortBy)
            {
                if (Forbidden > 0 && Forbidden >= shortBy)
                    return "there is enough of this on the map, but " + Forbidden
                        + " of it is FORBIDDEN — `unforbid` is the fix, not mining";
                if (Unreachable > 0 && Unreachable >= shortBy)
                    return "there is enough of this on the map, but " + Unreachable
                        + " of it is UNREACHABLE by every colonist who could haul it — a door, a "
                        + "wall or a missing bridge, not a shortage";
                if (Forbidden > 0 || Unreachable > 0)
                    return "genuinely short, and " + (Forbidden + Unreachable)
                        + " more is present but forbidden or unreachable";
                if (Builders == 0)
                    return "no colonist on this map can currently haul (all downed, or none "
                        + "capable of Manipulation), so reachability could not be tested and this "
                        + "count is the faction-level one — see availability_basis";
                return "genuinely short: nothing else of this def is on the map within reach";
            }
        }

        // The pawns whose reach decides the answer — the ones who could take
        // the construction-delivery job. `FreeColonistsSpawned` CLEARS and
        // rebuilds its cached list on every access (DigestVerb's hazard 1), so
        // it is snapshotted before anything below touches a capacity handler.
        public static List<Pawn> Builders(Map map)
        {
            var picked = new List<Pawn>();
            if (map == null) return picked;
            List<Pawn> all;
            try { all = new List<Pawn>(map.mapPawns.FreeColonistsSpawned); }
            catch { return picked; }
            for (int i = 0; i < all.Count && picked.Count < BuilderCap; i++)
            {
                var p = all[i];
                if (p == null) continue;
                try
                {
                    if (p.Downed || p.Dead || !p.Spawned) continue;
                    // PawnCanAutomaticallyHaulFast's own capacity clause, asked
                    // once per pawn here instead of once per pawn per stack.
                    if (!p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) continue;
                }
                catch { continue; }
                picked.Add(p);
            }
            return picked;
        }

        public static Availability Of(Map map, ThingDef def)
            => Of(map, def, Builders(map));

        public static Availability Of(Map map, ThingDef def, List<Pawn> builders)
        {
            var a = new Availability { Def = def };
            if (map == null || def == null) { a.Basis = "no-map"; return a; }
            a.InStockpiles = ConstructionVerbs.Stored(map, def);
            a.Builders = builders?.Count ?? 0;
            a.Basis = a.Builders > 0 ? "reachable-unforbidden-by-a-colonist" : "faction-unforbidden";

            List<Thing> stacks;
            try { stacks = new List<Thing>(map.listerThings.ThingsOfDef(def)); }
            catch { a.Basis = "unreadable"; return a; }

            for (int i = 0; i < stacks.Count; i++)
            {
                if (a.Stacks >= StackCap) { a.StacksMore++; continue; }
                var t = stacks[i];
                if (t == null || t.Destroyed) continue;
                a.Stacks++;
                int amount = t.stackCount;
                if (amount <= 0) continue;

                try { if (t.Fogged()) { a.Fogged += amount; continue; } } catch { }
                try { if (t.IsBurning()) { a.Burning += amount; continue; } } catch { }

                // Faction-forbidden short-circuits the pawn loop entirely: it
                // is true for every player pawn, and `IsForbidden(Thing, Pawn)`
                // walks the map's lords, which is not worth paying per builder.
                bool factionForbidden = false;
                try { factionForbidden = t.IsForbidden(Faction.OfPlayer); } catch { }
                if (factionForbidden) { a.Forbidden += amount; continue; }

                if (a.Builders == 0)
                {
                    // Nobody can haul, so reachability is not a question this
                    // map can answer. Counting it as unavailable would report a
                    // shortage on a colony that has the wood and a broken arm,
                    // which is a different problem and would send the agent
                    // mining. Said in `Basis` and in `Hint`.
                    a.Available += amount;
                    continue;
                }

                bool anyUnforbidden = false, reached = false;
                for (int b = 0; b < builders.Count; b++)
                {
                    var p = builders[b];
                    bool forbidden = true;
                    try { forbidden = t.IsForbidden(p); } catch { }
                    if (forbidden) continue;
                    anyUnforbidden = true;
                    try
                    {
                        // `MaxPathDanger(pawn)` on this work giver is
                        // `Danger.Deadly`, and ClosestTouch is the peMode
                        // ResourceDeliverJobFor passes.
                        if (p.CanReach(t, PathEndMode.ClosestTouch, Danger.Deadly))
                        {
                            reached = true;
                            break;
                        }
                    }
                    catch { }
                }
                if (reached) a.Available += amount;
                else if (anyUnforbidden) a.Unreachable += amount;
                else a.Forbidden += amount;
            }
            return a;
        }

        // ------------------------------------------------------- the rows --

        // One bill row: what is needed, what is available, and the stockpile
        // figure kept beside it as the separate fact it is.
        public static Dictionary<string, object> BillRow(Availability a, int needed)
        {
            var row = new Dictionary<string, object>
            {
                ["def"] = a.Def?.defName,
                ["count"] = needed,
            };
            a.Fill(row);
            return row;
        }

        // One shortfall row, or null when there is no shortfall. `short_by`
        // now means what its name says.
        public static Dictionary<string, object> ShortfallRow(Availability a, int needed)
        {
            int shortBy = needed - a.Available;
            if (shortBy <= 0) return null;
            var row = new Dictionary<string, object>
            {
                ["def"] = a.Def?.defName,
                ["needed"] = needed,
                ["short_by"] = shortBy,
            };
            a.Fill(row);
            row["hint"] = a.Hint(shortBy);
            return row;
        }

        // The provenance, published ONCE on an envelope rather than on every
        // row — the way `Blockers.cs` cites its gate, file and member.
        public static Dictionary<string, object> Basis(List<Pawn> builders)
            => new Dictionary<string, object>
            {
                ["gate"] = "RimWorld/WorkGiver_ConstructDeliverResources.ResourceValidator",
                ["gate_detail"] =
                    "the builder's own availability test — def match, IsForbidden(pawn), and "
                    + "HaulAIUtility.PawnCanAutomaticallyHaulFast's Fogged/CanReach/IsBurning "
                    + "clauses, asked of the colonists who could take the job. SlotGroup, "
                    + "haulDestination and resourceCounter appear nowhere in that work giver, so "
                    + "`in_stockpiles` is published as the separate measurement it is and no "
                    + "conclusion is drawn from it. CanReserve is deliberately not applied: a "
                    + "stack somebody is already hauling is not missing.",
                ["builders"] = builders?.Count ?? 0,
                ["stack_cap"] = StackCap,
            };
    }
}
