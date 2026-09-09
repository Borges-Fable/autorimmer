using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // ============================ THE GAUGES, PANEL FOUR ====================
    // spec 975973e. COCKPIT.md §"The screen", §"How it stays honest" item 4.
    //
    // The numbers that are always there, EACH FROM A FULL COUNT. That rule is
    // the design's own, and run openrun-20260902 taught the subtler version of
    // it: the counts were full and THE LISTS LIED. `things --def MinifiedThing`
    // said "1 of 1" and gave one coordinate for a def with six instances; the
    // guns reading gave one position for six turrets. So a full count of the
    // WRONG SET is the real failure, and every gauge below states the set it
    // counts, by member name, in `set`.
    //
    // ==================== WHAT THIS FILE DOES NOT COMPUTE ===================
    // COVERAGE. `ee4b4f8` (the map) computes sight coverage per gate and of
    // the inside; this spec PRINTS it. It has not landed, so the guns gauge
    // publishes positions and says the coverage is pending rather than
    // printing a number nobody computed. The 2026-09-09 ruling is folded in:
    // turret RINGS are out and SIGHT is the fact, because
    // `Building_TurretGun.DrawExtraSelectionOverlays` draws `EffectiveRange`
    // with no line-of-sight term and on the run's own map seven discs covered
    // the courtyard a lancer worked unopposed for ~200,000 ticks.
    //
    // ARMOUR RATING. `47547ca` is open — apparel rows carry no armor value —
    // so G3 grades "armed", counts unworn apparel BY DEF so a person can see
    // that two of them are flak vests, and returns `unknown` for the armour
    // clause. It does not invent a threshold on `ArmorRating_Sharp` to turn a
    // count into a verdict: `54b0c9a` is the standing lesson on a count with a
    // missing term manufacturing a verdict.
    //
    // A ROOM'S ROLE HISTORY. `f1a1700` comment #2 established there is no
    // durable baseline across a load and this project does not buy one with
    // scribed state, so G4 publishes the current room count with
    // `baseline: null` and a verdict of `unknown`. A room that changes role is
    // UNKNOWN to this screen, stated as a limit.
    internal static class ScreenGauges
    {
        // Sized against the measured saturated screen (see the acceptance
        // artifact and DESIGN's decisions log), not by feel.
        private const int DefCap = 10;        // distinct defs printed on a gauge
        private const int PositionCap = 20;   // positions printed for ONE def
        private const int RowCap = 8;         // rows on a list gauge
        private const int BatteryProbeCap = 24;
        private const int ReachProbePawns = 4;

        // ================================ GUNS ==============================
        //
        // SET: every building on `map.listerBuildings.allBuildingsColonist`
        // whose runtime type is or derives from `RimWorld/Building_Turret`,
        // plus every def with an outstanding loss whose `thingClass` is one.
        // `allBuildingsColonist` is a plain `readonly List<Building>` the game
        // maintains in `Add`/`Remove` (Verse/ListerBuildings.cs) — the real
        // list, not a query and not a cache rebuilt on read — so this is a
        // full count by construction.
        //
        // EVERY POSITION, NEVER ONE FOR SIX. The acceptance line is "six
        // turrets on the map print as six coordinates, never one", so the cap
        // here is per-def and it says how many it dropped; it never collapses
        // a def to a representative cell.
        internal static Dictionary<string, object> Guns(Map map)
        {
            var groups = new Dictionary<string, GunGroup>(StringComparer.Ordinal);
            var buildings = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < buildings.Count; i++)
            {
                var b = buildings[i];
                if (b == null || !(b is Building_Turret)) continue;
                // One fog rule across the player-facing surface (DESIGN
                // decisions log 2026-08-30). A turret the colony built cannot
                // be in unexplored ground, so this costs nothing and keeps the
                // rule from becoming a per-verb judgement.
                if (WorldSafe.Hidden(b, map)) continue;
                string def = b.def?.defName ?? "?";
                if (!groups.TryGetValue(def, out var g))
                {
                    g = new GunGroup { Def = def, Label = b.def?.label };
                    groups[def] = g;
                }
                g.Count++;
                if (g.At.Count < PositionCap) g.At.Add(Positions.Out(b.Position));
                else g.AtDropped++;
            }

            // The losses that belong on THIS gauge. A def with every instance
            // destroyed has no live building to hang its level on, and that is
            // exactly the case the level exists for.
            var losses = LossLevels.Snapshot();
            for (int i = 0; i < losses.Count; i++)
            {
                string def = losses[i].Def;
                if (def == null || groups.ContainsKey(def)) continue;
                if (!IsTurretDef(def)) continue;
                groups[def] = new GunGroup { Def = def, Label = losses[i].Label };
            }

            var rows = new List<object>();
            var ordered = new List<GunGroup>(groups.Values);
            // Importance: the def you have most of first, then alphabetical so
            // the truncation is deterministic run to run (the 2.6 rule).
            ordered.Sort((a, b) =>
            {
                int c = b.Count.CompareTo(a.Count);
                return c != 0 ? c : string.CompareOrdinal(a.Def, b.Def);
            });
            int total = 0;
            for (int i = 0; i < ordered.Count; i++) total += ordered[i].Count;
            for (int i = 0; i < ordered.Count && i < DefCap; i++)
            {
                var g = ordered[i];
                int lost = LossLevels.LostFor(g.Def);
                var row = new Dictionary<string, object>
                {
                    ["def"] = g.Def,
                    ["label"] = g.Label,
                    ["count"] = g.Count,
                    ["at"] = g.At,
                };
                if (g.AtDropped > 0) row["at_more"] = g.AtDropped;
                if (lost > 0)
                {
                    row["lost"] = lost;
                    // "was N" is `count + lost` and NOT a remembered earlier
                    // count — see LossLevels' header. After a rebuild the two
                    // diverge and this field keeps showing the loss, which is
                    // the safe direction and the one the design chose over
                    // expiry.
                    row["was"] = g.Count + lost;
                }
                rows.Add(row);
            }

            return new Dictionary<string, object>
            {
                ["by_def"] = rows,
                ["total"] = total,
                ["defs"] = ordered.Count,
                ["more"] = Math.Max(0, ordered.Count - DefCap),
                ["order"] = "count-desc, then defName",
                // ONE LINE, and the member-by-member argument lives in this
                // file's header and in DESIGN's decisions log. A screen
                // arrives every turn and cannot carry a paragraph per gauge:
                // the first assembled screen measured 17,054 bytes with 6,538
                // of them fixed prose, against a budget of two to four
                // thousand tokens. Every gauge still SAYS what set it counts.
                ["set"] = "every Building_Turret on ListerBuildings.allBuildingsColonist, unfogged. "
                    + "FULL COUNT; positions are never collapsed.",
                ["coverage"] = null,
                ["coverage_pending"] = "ee4b4f8 computes SIGHT coverage per gate and of the inside. "
                    + "Rings are out: EffectiveRange has no line-of-sight term (ruled 2026-09-09).",
            };
        }

        private sealed class GunGroup
        {
            public string Def;
            public string Label;
            public int Count;
            public int AtDropped;
            public List<object> At = new List<object>();
        }

        private static readonly Dictionary<string, bool> turretDefMemo =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        private static bool IsTurretDef(string defName)
        {
            if (defName == null) return false;
            if (turretDefMemo.TryGetValue(defName, out var known)) return known;
            bool isTurret = false;
            try
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                isTurret = def?.thingClass != null && typeof(Building_Turret).IsAssignableFrom(def.thingClass);
            }
            catch { }
            turretDefMemo[defName] = isTurret;
            return isTurret;
        }

        // ================================ POWER =============================
        //
        // The digest's `power` section VERBATIM (its field names ship and are
        // not renamed), plus the three readings the spec adds. The net walk is
        // repeated rather than folded into `DigestVerb.PowerSection`, because
        // the reachability probe below is a per-battery region query and
        // `digest` is documented as called constantly — a gauge on a turn
        // budget may pay for it and a predicate section may not.
        //
        // SET: every net in `PowerNetManager.AllNetsListForReading`, every
        // `CompPowerTrader` in `PowerNet.powerComps`, every battery in
        // `PowerNet.batteryComps`. Full, uncapped: only the PRINTED lists are
        // capped, and they say what they dropped.
        internal static Dictionary<string, object> Power(Map map, Dictionary<string, object> digestPower)
        {
            var d = digestPower != null
                ? new Dictionary<string, object>(digestPower)
                : new Dictionary<string, object>();

            var noGenerator = new List<object>();
            var noConsumer = new List<object>();
            int noGeneratorTotal = 0, noConsumerTotal = 0;
            var batteries = new List<Thing>();
            try
            {
                var nets = map.powerNetManager.AllNetsListForReading;
                for (int i = 0; i < nets.Count; i++)
                {
                    var net = nets[i];
                    if (net == null) continue;
                    bool hasGenerator = false, hasConsumer = false;
                    var comps = net.powerComps;
                    for (int j = 0; j < comps.Count; j++)
                    {
                        var c = comps[j];
                        if (c?.Props == null) continue;
                        // The game's own producer/consumer distinction, and the
                        // same test DigestVerb.PowerSection cites: PowerNet's
                        // IsPowerSource is `Props.PowerConsumption < 0`, and it
                        // holds whether or not the thing is currently running
                        // or fuelled — which is what makes "this net has no
                        // generator at all" a structural fact rather than a
                        // reading of the moment.
                        if (c.Props.PowerConsumption < 0f) hasGenerator = true;
                        else if (c.Props.PowerConsumption > 0f) hasConsumer = true;
                    }
                    for (int j = 0; j < net.batteryComps.Count; j++)
                    {
                        var bc = net.batteryComps[j];
                        if (bc?.parent != null) batteries.Add(bc.parent);
                    }
                    if (!hasGenerator)
                    {
                        noGeneratorTotal++;
                        if (noGenerator.Count < RowCap) noGenerator.Add(NetRow(net));
                    }
                    if (!hasConsumer)
                    {
                        noConsumerTotal++;
                        if (noConsumer.Count < RowCap) noConsumer.Add(NetRow(net));
                    }
                }
            }
            catch (Exception e) { d["nets_error"] = e.GetType().Name; }

            d["nets_no_generator"] = noGeneratorTotal;
            d["nets_no_generator_rows"] = noGenerator;
            d["nets_no_generator_more"] = Math.Max(0, noGeneratorTotal - noGenerator.Count);
            d["nets_no_consumer"] = noConsumerTotal;
            d["nets_no_consumer_rows"] = noConsumer;
            d["nets_no_consumer_more"] = Math.Max(0, noConsumerTotal - noConsumer.Count);

            // ---- batteries nobody can walk to --------------------------------
            // The run's own line: "batteries 3 at 51 % in room 73 — no door,
            // nobody can reach them". A battery inside a sealed room is not a
            // buffer, it is 51% of a buffer the colony cannot service, and
            // nothing in the surface said so.
            //
            // `Pawn.CanReach` (ReachabilityUtility) and not `FindPathNow`: the
            // reachability test is the region-graph query every job giver in
            // the game runs, and the mod already routes through it in
            // `Materials`, `PawnActs` and `CommsVerbs`. It writes only the
            // game's own transient reachability cache — no scribed state, no
            // RNG, no lazy-init of anything a save holds.
            var probe = new List<Pawn>();
            try
            {
                // MapPawns.FreeColonistsSpawned CLEARS and rebuilds the same
                // cached List on every access (DigestVerb's first documented
                // hazard). Snapshot before iterating.
                var roster = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
                for (int i = 0; i < roster.Count && probe.Count < ReachProbePawns; i++)
                {
                    var p = roster[i];
                    if (p == null || p.Dead || p.Downed) continue;
                    probe.Add(p);
                }
            }
            catch { }

            var unreachable = new List<object>();
            int checkedCount = 0, unreachableTotal = 0;
            for (int i = 0; i < batteries.Count && checkedCount < BatteryProbeCap; i++)
            {
                var b = batteries[i];
                if (b == null || !b.Spawned) continue;
                checkedCount++;
                bool reachable = probe.Count == 0;
                for (int j = 0; j < probe.Count && !reachable; j++)
                {
                    try
                    {
                        if (probe[j].CanReach(b, PathEndMode.Touch, Danger.Deadly)) reachable = true;
                    }
                    catch { reachable = true; }
                }
                if (reachable) continue;
                unreachableTotal++;
                if (unreachable.Count < RowCap)
                    unreachable.Add(new Dictionary<string, object>
                    {
                        ["def"] = b.def?.defName,
                        ["at"] = Positions.Out(b.Position),
                        ["thing"] = b.thingIDNumber,
                    });
            }
            d["batteries_unreachable"] = unreachableTotal;
            d["batteries_unreachable_rows"] = unreachable;
            d["batteries_unreachable_more"] = Math.Max(0, unreachableTotal - unreachable.Count);
            d["batteries_checked"] = checkedCount;
            d["batteries_not_checked"] = Math.Max(0, batteries.Count - checkedCount);
            d["batteries_reach_basis"] = probe.Count == 0
                ? "no probe: no living undowned colonist is spawned, so nothing is called unreachable."
                : "Pawn.CanReach(Touch, Danger.Deadly) for up to " + ReachProbePawns
                  + " colonists; unreachable = none of them could.";
            d["set"] = "every net in PowerNetManager.AllNetsListForReading, every powerComp and "
                + "batteryComp on it. Counts full; printed lists capped.";
            return d;
        }

        private static Dictionary<string, object> NetRow(PowerNet net)
        {
            var row = new Dictionary<string, object>();
            try
            {
                row["transmitters"] = net.transmitters?.Count ?? 0;
                row["comps"] = net.powerComps?.Count ?? 0;
                row["batteries"] = net.batteryComps?.Count ?? 0;
                // A net has no id of its own; a cell on it is the handle a
                // caller can take to `room-at` or `map-dump`.
                Thing anchor = null;
                if (net.transmitters != null && net.transmitters.Count > 0) anchor = net.transmitters[0]?.parent;
                if (anchor == null && net.powerComps != null && net.powerComps.Count > 0)
                    anchor = net.powerComps[0]?.parent;
                if (anchor != null && anchor.Spawned)
                {
                    row["at"] = Positions.Out(anchor.Position);
                    row["at_def"] = anchor.def?.defName;
                }
            }
            catch { }
            return row;
        }

        // ================================ FOOD ==============================
        //
        // TWO NUMBERS, map-wide and in stockpiles (git-bug e811574). Both
        // already ship under names this gauge does not rename: `food_days` is
        // the vanilla `Alert_LowFood` division over `ResourceCounter`, which is
        // STOCKPILE-ONLY, and `food_rot.nutrition` /
        // `food_rot.nutrition_in_stockpiles` are the map-wide pair `FoodRot`
        // built for exactly this reason. The gauge lifts them beside each
        // other so the difference is on the screen instead of two verbs apart.
        internal static Dictionary<string, object> Food(Dictionary<string, object> resources)
        {
            var d = new Dictionary<string, object>();
            if (resources == null) return d;
            Copy(resources, d, "food_days");
            Copy(resources, d, "food_needers");
            Copy(resources, d, "food_nutrition");
            object rot = resources.TryGetValue("food_rot", out var r) ? r : null;
            var rd = rot as Dictionary<string, object>;
            if (rd != null)
            {
                Copy(rd, d, "days");
                Copy(rd, d, "nutrition");
                Copy(rd, d, "nutrition_in_stockpiles");
                Copy(rd, d, "nutrition_forbidden");
                Copy(rd, d, "frozen");
                Copy(rd, d, "spoiled_stacks");
                Copy(rd, d, "soonest_rot_days");
                Copy(rd, d, "ok");
            }
            d["scope"] = "TWO NUMBERS (e811574). food_days/food_nutrition are vanilla's "
                + "ResourceCounter figures: STOCKPILE-ONLY and FRESH-ONLY. days/nutrition/"
                + "nutrition_in_stockpiles are FoodRot's map-wide walk. 0 in stockpiles is not "
                + "none on the map.";
            return d;
        }

        // =============================== STOCK ==============================
        //
        // The map-wide twin for every material row (git-bug e811574), plus the
        // two things the run lost track of for sixty in-game days: unworn
        // apparel and uninstalled craft output (git-bug 4c12e5d).
        //
        // SET, per material: `ListerThings.ThingsOfDef(def)` — the real
        // per-def backing list out of `listsByDef`, which holds every SPAWNED
        // thing of that def on the map wherever it lies. The stockpile figure
        // beside it is `ResourceCounter`, which walks SlotGroup haul
        // destinations. The two differ by exactly the material lying in the
        // open, which is the fact this gauge exists to publish.
        internal static Dictionary<string, object> Stock(Map map, Dictionary<string, object> resources)
        {
            var d = new Dictionary<string, object>();
            var rows = new List<object>();
            rows.Add(MaterialRow(map, resources, "steel", ThingDefOf.Steel));
            rows.Add(MaterialRow(map, resources, "wood", ThingDefOf.WoodLog));
            rows.Add(MaterialRow(map, resources, "components", ThingDefOf.ComponentIndustrial));
            rows.Add(MaterialRow(map, resources, "silver", ThingDefOf.Silver));
            rows.Add(MedicineRow(map, resources));
            d["materials"] = rows;
            d["unworn_apparel"] = UnwornApparel(map);
            d["uninstalled"] = Uninstalled(map);
            d["scope"] = "BOTH figures per row (e811574): in_stockpiles = ResourceCounter "
                + "(SlotGroups only); map_wide = ListerThings.ThingsOfDef over stackCount; "
                + "forbidden = the map-wide part nobody will haul.";
            return d;
        }

        private static Dictionary<string, object> MaterialRow(Map map, Dictionary<string, object> res,
                                                              string key, ThingDef def)
        {
            var row = new Dictionary<string, object> { ["name"] = key, ["def"] = def?.defName };
            if (res != null && res.TryGetValue(key, out var v)) row["in_stockpiles"] = v;
            int wide = 0, forbidden = 0, stacks = 0;
            CountDef(map, def, ref wide, ref forbidden, ref stacks);
            row["map_wide"] = wide;
            row["forbidden"] = forbidden;
            row["stacks"] = stacks;
            return row;
        }

        // Medicine is a CATEGORY in the digest (`rc.GetCountIn`), so its
        // map-wide twin has to walk the same category. `DescendantThingDefs` is
        // a pure iterator over `childThingDefs` (Verse/ThingCategoryDef.cs) —
        // no cache, no lazy-init, nothing written — memoised here only because
        // it allocates an enumerator per call and this runs every screen.
        private static List<ThingDef> medicineDefs;

        private static Dictionary<string, object> MedicineRow(Map map, Dictionary<string, object> res)
        {
            var row = new Dictionary<string, object> { ["name"] = "meds", ["def"] = "category:Medicine" };
            if (res != null && res.TryGetValue("meds", out var v)) row["in_stockpiles"] = v;
            if (medicineDefs == null)
            {
                medicineDefs = new List<ThingDef>();
                try
                {
                    foreach (var def in ThingCategoryDefOf.Medicine.DescendantThingDefs)
                        if (def != null) medicineDefs.Add(def);
                }
                catch { }
            }
            int wide = 0, forbidden = 0, stacks = 0;
            for (int i = 0; i < medicineDefs.Count; i++)
                CountDef(map, medicineDefs[i], ref wide, ref forbidden, ref stacks);
            row["map_wide"] = wide;
            row["forbidden"] = forbidden;
            row["stacks"] = stacks;
            return row;
        }

        private static void CountDef(Map map, ThingDef def, ref int total, ref int forbidden, ref int stacks)
        {
            if (map == null || def == null) return;
            try
            {
                // ThingsOfDef hands back the real backing list out of
                // listsByDef; it is not rebuilt on read. (It DOES Log.ErrorOnce
                // for ThingDefOf.MinifiedThing — see Uninstalled below, which
                // takes the group route the game's own message names.)
                var list = map.listerThings.ThingsOfDef(def);
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t == null || !t.Spawned) continue;
                    if (WorldSafe.Hidden(t, map)) continue;
                    stacks++;
                    total += t.stackCount;
                    if (t.IsForbidden(Faction.OfPlayer)) forbidden += t.stackCount;
                }
            }
            catch { }
        }

        // Unworn apparel is a full count of SPAWNED apparel: worn apparel is
        // not spawned at all (it lives in Pawn_ApparelTracker's container), so
        // `ThingsInGroup(Apparel)` and "what somebody is wearing" are disjoint
        // by construction and this needs no wearer test.
        //
        // WHICH OF THESE IS ARMOUR IS NOT PUBLISHED, and that is `47547ca`
        // standing open rather than an omission. The rows carry the def and
        // its label, so a reader sees "flak vest 2" and judges; the gauge does
        // not turn a count into a verdict on a stat it cannot read.
        private static Dictionary<string, object> UnwornApparel(Map map)
        {
            var byDef = new Dictionary<string, int>(StringComparer.Ordinal);
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            int total = 0, forbidden = 0;
            try
            {
                var list = map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel);
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t == null || !t.Spawned) continue;
                    if (WorldSafe.Hidden(t, map)) continue;
                    total += t.stackCount;
                    if (t.IsForbidden(Faction.OfPlayer)) forbidden += t.stackCount;
                    string def = t.def?.defName ?? "?";
                    byDef[def] = byDef.TryGetValue(def, out var c) ? c + t.stackCount : t.stackCount;
                    if (!labels.ContainsKey(def)) labels[def] = t.def?.label;
                }
            }
            catch { }
            return new Dictionary<string, object>
            {
                ["count"] = total,
                ["forbidden"] = forbidden,
                ["by_def"] = TopDefs(byDef, labels, out int moreDefs),
                ["more_defs"] = moreDefs,
                ["set"] = "ThingsInGroup(Apparel) = def.IsApparel, SPAWNED only — worn apparel is "
                    + "not spawned, so all of this is unworn. FULL COUNT.",
                ["armour_readable"] = false,
                ["armour_note"] = "which of these is ARMOUR is not published: apparel rows carry no "
                    + "armor rating (47547ca, open). The defs are given; no threshold is invented.",
            };
        }

        // git-bug 4c12e5d. Five sculptures sat minified for the whole run
        // giving zero beauty and nothing counted them; the one at (108,105)
        // was last read sixty-one in-game days before the wipe.
        //
        // The group route rather than `ThingsOfDef(ThingDefOf.MinifiedThing)`,
        // which the game itself `Log.ErrorOnce`s for and whose message names
        // this exact replacement (Verse/ListerThings.ThingsOfDef).
        private static Dictionary<string, object> Uninstalled(Map map)
        {
            var rows = new List<object>();
            int total = 0, forbidden = 0;
            try
            {
                var list = map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing);
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t == null || !t.Spawned) continue;
                    if (WorldSafe.Hidden(t, map)) continue;
                    total++;
                    bool forb = false;
                    try { forb = t.IsForbidden(Faction.OfPlayer); } catch { }
                    if (forb) forbidden++;
                    if (rows.Count >= RowCap) continue;
                    string inner = null, innerLabel = null;
                    try
                    {
                        // MinifiedThing.InnerThing is the crate's contents —
                        // the sculpture, not the crate — which is the def a
                        // reader needs. A null or a throw degrades this ONE row.
                        var mt = t as MinifiedThing;
                        var it = mt?.InnerThing;
                        inner = it?.def?.defName;
                        innerLabel = it?.def?.label;
                    }
                    catch { }
                    rows.Add(new Dictionary<string, object>
                    {
                        ["thing"] = t.thingIDNumber,
                        ["def"] = inner ?? t.def?.defName,
                        ["label"] = innerLabel ?? t.def?.label,
                        ["at"] = Positions.Out(t.Position),
                        ["forbidden"] = forb,
                    });
                }
            }
            catch { }
            return new Dictionary<string, object>
            {
                ["count"] = total,
                ["forbidden"] = forbidden,
                ["rows"] = rows,
                ["more"] = Math.Max(0, total - rows.Count),
                ["set"] = "ThingsInGroup(MinifiedThing), SPAWNED, this map. FULL COUNT. This is the "
                    + "G5 measure (4c12e5d): the contract grades G5 as count == 0.",
            };
        }

        private static List<object> TopDefs(Dictionary<string, int> byDef,
                                            Dictionary<string, string> labels, out int more)
        {
            var keys = new List<string>(byDef.Keys);
            keys.Sort((a, b) =>
            {
                int c = byDef[b].CompareTo(byDef[a]);
                return c != 0 ? c : string.CompareOrdinal(a, b);
            });
            var outp = new List<object>();
            for (int i = 0; i < keys.Count && i < DefCap; i++)
                outp.Add(new Dictionary<string, object>
                {
                    ["def"] = keys[i],
                    ["label"] = labels != null && labels.TryGetValue(keys[i], out var l) ? l : null,
                    ["count"] = byDef[keys[i]],
                });
            more = Math.Max(0, keys.Count - outp.Count);
            return outp;
        }

        // ============================ ARMAMENT ==============================
        //
        // git-bug c41bdcc, resolved by investigation as that issue asks.
        //
        // Q1 — WHAT IS A WEAPON. `ThingRequestGroup.Weapon` IS `def.IsWeapon`
        // (Verse/ThingListGroupHelper.ThingListGroupHelper, `case
        // ThingRequestGroup.Weapon: return def.IsWeapon`), so the group and the
        // predicate are the same set and there is one definition, not three.
        // `equipmentType == Primary` is a different question — what a pawn may
        // hold — and is answered by walking equipment, below.
        //
        // Q2 — IS A CLUB A WEAPON. Yes, and the count says so instead of
        // hiding it: every figure below is SPLIT `ranged` / `melee` on
        // `def.IsRangedWeapon` (Verse/ThingDef; `IsMeleeWeapon` is
        // `IsWeapon && !IsRangedWeapon`). "6 of 6 armed" on six clubs is
        // prevented by the split, not by a quality threshold — and no
        // `GetStatValueAbstract` is read, which keeps this affordable.
        //
        // Q3 — FORBIDDEN AND UNREACHABLE SPARES. The spare figure carries
        // `forbidden` beside it. No reachability term: `54b0c9a`'s lesson is
        // that a count with a missing term must not draw a conclusion, and this
        // one draws none.
        //
        // SETS, and they are DISJOINT by construction: EQUIPPED is
        // `Pawn_EquipmentTracker.Primary` over the spawned free colonists;
        // SPARE is `ListerThings.ThingsInGroup(Weapon)`, which holds SPAWNED
        // things only and therefore cannot contain anything a pawn is holding.
        internal static Dictionary<string, object> Armament(Map map)
        {
            int colonists = 0, violenceCapable = 0, armedRanged = 0, armedMelee = 0, unarmed = 0;
            var unarmedNames = new List<object>();
            try
            {
                var roster = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
                for (int i = 0; i < roster.Count; i++)
                {
                    var p = roster[i];
                    if (p == null) continue;
                    colonists++;
                    bool violent;
                    try { violent = !p.WorkTagIsDisabled(WorkTags.Violent); }
                    catch { violent = false; }
                    if (!violent) continue;
                    violenceCapable++;
                    ThingWithComps w = null;
                    try { w = p.equipment?.Primary; } catch { }
                    bool ranged = false, melee = false;
                    try
                    {
                        ranged = w?.def != null && w.def.IsRangedWeapon;
                        melee = w?.def != null && w.def.IsMeleeWeapon;
                    }
                    catch { }
                    if (ranged) armedRanged++;
                    else if (melee) armedMelee++;
                    else
                    {
                        unarmed++;
                        if (unarmedNames.Count < RowCap) unarmedNames.Add(PawnSafe.Name(p));
                    }
                }
            }
            catch { }

            int spareRanged = 0, spareMelee = 0, spareForbidden = 0;
            try
            {
                var list = map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon);
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t == null || !t.Spawned) continue;
                    if (WorldSafe.Hidden(t, map)) continue;
                    if (t.def != null && t.def.IsRangedWeapon) spareRanged++;
                    else spareMelee++;
                    try { if (t.IsForbidden(Faction.OfPlayer)) spareForbidden++; }
                    catch { }
                }
            }
            catch { }

            return new Dictionary<string, object>
            {
                ["colonists"] = colonists,
                ["violence_capable"] = violenceCapable,
                // "armed 5 of 7" is this pair, and `armed` means a RANGED
                // primary, which is the run contract's own wording for G3.
                ["armed"] = armedRanged,
                ["armed_of"] = violenceCapable,
                ["armed_melee_only"] = armedMelee,
                ["unarmed"] = unarmed,
                ["unarmed_names"] = unarmedNames,
                ["spare_ranged"] = spareRanged,
                ["spare_melee"] = spareMelee,
                ["spare_forbidden"] = spareForbidden,
                ["total_weapons"] = armedRanged + armedMelee + spareRanged + spareMelee,
                ["set"] = "EQUIPPED: equipment.Primary over spawned free colonists that are not "
                    + "WorkTagIsDisabled(Violent). SPARE: ThingsInGroup(Weapon) = def.IsWeapon, "
                    + "SPAWNED only — disjoint, because equipped weapons are not spawned. Split "
                    + "ranged/melee on def.IsRangedWeapon (c41bdcc).",
            };
        }

        // ============================== GOALS ===============================
        //
        // The six contract goals (RUNS/RUN-CONTRACT-open-ended.md), graded on
        // EVERY screen. The run held them as prose in a contract file and
        // graded them by hand at day boundaries; two of the six were never
        // graded at all after the first week.
        //
        // Each goal publishes its own `ok` and the numbers behind it, and
        // `ok: null` means UNKNOWN — never false. G4 is the case that matters:
        // there is no durable baseline across a load, so "is the base still
        // growing" is unknown rather than failing.
        internal static Dictionary<string, object> Goals(Map map, Dictionary<string, object> armament,
                                                         Dictionary<string, object> stock,
                                                         int offersOpen)
        {
            return new Dictionary<string, object>
            {
                ["G1"] = G1Scanner(map),
                ["G2"] = G2Geothermal(map),
                ["G3"] = G3Militia(armament, stock),
                ["G4"] = G4Growth(map),
                ["G5"] = G5Art(stock),
                ["G6"] = G6Strangers(offersOpen),
                ["note"] = "RUN-CONTRACT-open-ended.md's six goals, graded every screen. "
                    + "`ok: null` is UNKNOWN, never a failure.",
                // ONE `set` FOR ALL SIX rather than one per goal. Six
                // paragraphs of provenance on a block that arrives every turn
                // cost 1.3KB of a 2-4k-token budget, and the per-goal
                // derivations are in this file's methods and in DESIGN's
                // decisions log. A goal still says how it was graded — the
                // numbers behind each verdict are on its own row.
                ["set"] = "G1 ThingsOfDef(GroundPenetratingScanner) + CompPowerTrader.PowerOn + "
                    + "WorldSafe.Finished over the def's own researchPrerequisites. G2 "
                    + "ThingsOfDef(SteamGeyser) unfogged, joined to generators by the game's own "
                    + "ThingGrid.ThingAt. G3 gauges.armament. G4 RegionGrid.AllRooms. G5 "
                    + "gauges.stock.uninstalled. G6 quests in QuestState.NotYetAccepted.",
            };
        }

        // G1 — researched AND built AND powered.
        //
        // The research clause reads the BUILDING's own prerequisites
        // (`ThingDef.researchPrerequisites`) rather than a hardcoded project
        // name, so it stays right if the def moves. Every `IsFinished` goes
        // through `WorldSafe.Finished`, never `ResearchProjectDef.IsFinished`,
        // which bottoms out in `ResearchManager.GetProgress` and INSERTS a zero
        // entry into a scribed dictionary on a miss (DESIGN decisions log
        // 2026-08-30).
        private static Dictionary<string, object> G1Scanner(Map map)
        {
            var d = new Dictionary<string, object> { ["goal"] = "deep mineral scanner" };
            var def = ThingDefOf.GroundPenetratingScanner;
            d["def"] = def?.defName;
            bool researched = true;
            var missing = new List<object>();
            try
            {
                if (def?.researchPrerequisites != null)
                    for (int i = 0; i < def.researchPrerequisites.Count; i++)
                    {
                        var p = def.researchPrerequisites[i];
                        if (WorldSafe.Finished(p)) continue;
                        researched = false;
                        missing.Add(p?.defName);
                    }
            }
            catch { researched = false; }
            int built = 0, powered = 0;
            var at = new List<object>();
            try
            {
                var list = map.listerThings.ThingsOfDef(def);
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t == null || !t.Spawned) continue;
                    if (t.Faction == null || !t.Faction.IsPlayer) continue;
                    built++;
                    if (at.Count < PositionCap) at.Add(Positions.Out(t.Position));
                    try
                    {
                        var trader = (t as ThingWithComps)?.TryGetComp<CompPowerTrader>();
                        if (trader != null && trader.PowerOn) powered++;
                    }
                    catch { }
                }
            }
            catch { }
            d["researched"] = researched;
            d["research_missing"] = missing;
            d["built"] = built;
            d["powered"] = powered;
            d["at"] = at;
            d["ok"] = researched && built > 0 && powered > 0;
            return d;
        }

        // G2 — geothermal built on a geyser and producing. Geysers USED AND
        // UNUSED, which is the half the run never had: "there is no geyser you
        // can defend" is a day-1 decision and nothing published the geysers.
        //
        // The generator/geyser join is the game's OWN lookup, from
        // `CompPowerPlantSteam.CompTick`: `parent.Map.thingGrid.ThingAt(
        // parent.Position, ThingDefOf.SteamGeyser)`. Reproduced rather than
        // approximated by comparing positions.
        private static Dictionary<string, object> G2Geothermal(Map map)
        {
            var d = new Dictionary<string, object> { ["goal"] = "geothermal power" };
            var used = new HashSet<int>();
            int generators = 0, producing = 0;
            var genRows = new List<object>();
            try
            {
                var gens = map.listerThings.ThingsOfDef(ThingDefOf.GeothermalGenerator);
                for (int i = 0; i < gens.Count; i++)
                {
                    var g = gens[i];
                    if (g == null || !g.Spawned) continue;
                    if (g.Faction == null || !g.Faction.IsPlayer) continue;
                    generators++;
                    bool on = false;
                    try
                    {
                        var trader = (g as ThingWithComps)?.TryGetComp<CompPowerTrader>();
                        on = trader != null && trader.PowerOn && trader.PowerOutput > 0f;
                    }
                    catch { }
                    if (on) producing++;
                    try
                    {
                        var geyser = map.thingGrid.ThingAt(g.Position, ThingDefOf.SteamGeyser);
                        if (geyser != null) used.Add(geyser.thingIDNumber);
                    }
                    catch { }
                    if (genRows.Count < RowCap)
                        genRows.Add(new Dictionary<string, object>
                        {
                            ["at"] = Positions.Out(g.Position),
                            ["producing"] = on,
                        });
                }
            }
            catch { }

            int geysers = 0, fogged = 0;
            var unusedAt = new List<object>();
            try
            {
                var list = map.listerThings.ThingsOfDef(ThingDefOf.SteamGeyser);
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t == null || !t.Spawned) continue;
                    // A geyser in unexplored ground is one the player has not
                    // found. One fog rule (DESIGN decisions log 2026-08-30) —
                    // and the count of hidden ones is published so "there is no
                    // geyser here" and "you have not looked" never read alike.
                    if (WorldSafe.Hidden(t, map)) { fogged++; continue; }
                    geysers++;
                    if (used.Contains(t.thingIDNumber)) continue;
                    if (unusedAt.Count < PositionCap) unusedAt.Add(Positions.Out(t.Position));
                }
            }
            catch { }

            d["geysers"] = geysers;
            d["geysers_fogged"] = fogged;
            d["used"] = used.Count;
            d["unused"] = Math.Max(0, geysers - used.Count);
            d["unused_at"] = unusedAt;
            d["generators"] = generators;
            d["generators_producing"] = producing;
            d["generator_rows"] = genRows;
            d["ok"] = producing > 0;
            return d;
        }

        private static Dictionary<string, object> G3Militia(Dictionary<string, object> armament,
                                                            Dictionary<string, object> stock)
        {
            var d = new Dictionary<string, object> { ["goal"] = "a defensive militia" };
            int armed = Int(armament, "armed"), of = Int(armament, "armed_of");
            d["armed"] = armed;
            d["of"] = of;
            d["unarmed"] = Int(armament, "unarmed");
            d["armed_melee_only"] = Int(armament, "armed_melee_only");
            int unworn = 0;
            var app = stock != null && stock.TryGetValue("unworn_apparel", out var a)
                ? a as Dictionary<string, object> : null;
            if (app != null) unworn = Int(app, "count");
            d["unworn_apparel"] = unworn;
            bool armedOk = of > 0 && armed >= of;
            d["armed_ok"] = armedOk;
            // The armour clause is UNKNOWN, not false: `47547ca` is open and
            // apparel rows carry no armor rating, so "no armour piece is
            // sitting unworn" cannot be graded. The run contract states the
            // same blind spot in the same words.
            d["armour_ok"] = null;
            d["ok"] = armedOk ? (object)null : false;
            d["note"] = "armour is UNKNOWN: apparel rows carry no armor rating (47547ca). "
                + "gauges.stock.unworn_apparel.by_def is the evidence."
                + (armedOk ? "" : " Not every violence-capable colonist holds a ranged weapon.");
            return d;
        }

        // G4 — rooms and enclosed floor area higher than ten days prior.
        //
        // GRADED `null`. `f1a1700` comment #2 established that `Room.ID` is
        // stable across ordinary play and destroyed by a LOAD
        // (`RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms` takes
        // `RebuildAllRegionsAndRooms` when `!initialized`, and `nextRoomID` is
        // not scribed), and that a durable baseline would be new scribed state
        // this project does not buy from an observer (d16a463). So the level is
        // published and the trend is not, and a room that changes ROLE is
        // UNKNOWN to this screen. Stated, not hidden.
        private static Dictionary<string, object> G4Growth(Map map)
        {
            var d = new Dictionary<string, object> { ["goal"] = "a base that keeps growing" };
            int rooms = 0, cells = 0;
            try
            {
                // RegionGrid.AllRooms is an IReadOnlyList over the real
                // `allRooms` list (WorldSafe's own note). Room.Role is NOT read
                // here — it is the most expensive line in the digest and this
                // gauge needs only the count.
                var all = map.regionGrid.AllRooms;
                for (int i = 0; i < all.Count; i++)
                {
                    var r = all[i];
                    if (r == null || r.TouchesMapEdge || r.RegionCount == 0) continue;
                    if (WorldSafe.RoomHidden(r)) continue;
                    rooms++;
                    // `Room.CellCount` MEMOISES into `cachedCellCount` on a
                    // miss (Verse/Room.cs) — a write on read, and it is
                    // declared here rather than discovered later. It is not
                    // scribed state, touches no RNG and is invalidated by the
                    // region updater the same way `Room.Role`'s analysis is, so
                    // it is in the same class as the accessor the digest
                    // already pays for per colonist and two classes below the
                    // ones `WorldSafe` bans. It sums district counts; it does
                    // not walk cells.
                    try { cells += r.CellCount; } catch { }
                }
            }
            catch { }
            d["rooms"] = rooms;
            d["enclosed_cells"] = cells;
            d["baseline"] = null;
            d["ok"] = null;
            d["note"] = "UNKNOWN by design: no durable baseline across a load (Room.ID is remade on "
                + "load, nextRoomID is not scribed, and an observer may not own scribed state — "
                + "d16a463, f1a1700 #2). The LEVEL is published, the ten-day trend is not, and a "
                + "room that changes ROLE is UNKNOWN to this screen.";
            return d;
        }

        private static Dictionary<string, object> G5Art(Dictionary<string, object> stock)
        {
            var d = new Dictionary<string, object> { ["goal"] = "art placed" };
            var un = stock != null && stock.TryGetValue("uninstalled", out var u)
                ? u as Dictionary<string, object> : null;
            int count = un != null ? Int(un, "count") : 0;
            d["uninstalled"] = count;
            d["ok"] = count == 0;
            // The rows are NOT repeated here: they are already on this screen
            // at `gauges.stock.uninstalled.rows`, and a screen that prints the
            // same list twice fits half as much. The contract's second clause —
            // no colonist carrying `Unsightly environment` — is a per-pawn
            // thought and is read from `pawn {id}`; it is not graded here.
            d["rows_at"] = "gauges.stock.uninstalled.rows";
            return d;
        }

        private static Dictionary<string, object> G6Strangers(int offersOpen)
        {
            return new Dictionary<string, object>
            {
                ["goal"] = "strangers taken on",
                ["offers_open"] = offersOpen,
                // An offer is a quest in QuestState.NotYetAccepted — how a
                // joiner, a wanderer and a refugee arrive. G6 is that none of
                // them ever LAPSES, which is why each is also a decision.
                ["ok"] = offersOpen == 0,
                ["each_is_a_decision"] = true,
            };
        }

        private static int Int(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.TryGetValue(k, out var v)) return 0;
            if (v is int i) return i;
            if (v is double x) return (int)x;
            if (v is long l) return (int)l;
            return 0;
        }

        private static void Copy(Dictionary<string, object> src, Dictionary<string, object> dst, string k)
        {
            if (src != null && src.TryGetValue(k, out var v)) dst[k] = v;
        }
    }
}
