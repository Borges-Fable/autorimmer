using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoRimmer
{
    // =========================================== git-bug 261f2e9 =============
    // THE ROT TERM — because `food_days` cannot see the thing that kills a
    // ten-day run, and it is the M1 death shape one system over.
    //
    // M1 died because `BloodLoss` was truncated out of the health read: the
    // surface showed a number that was not the thing killing the colony. A food
    // run dies the same way, and here is the arithmetic, all of it verified in
    // the decompiled 1.6 tree by member:
    //
    //  * `DigestVerb.ResourceSection`'s `food_days` is
    //    `map.resourceCounter.TotalHumanEdibleNutrition / (colonists +
    //    prisoners)` — vanilla's `Alert_LowFood` division.
    //  * `RimWorld/ResourceCounter.UpdateResourceCounts` walks
    //    `map.haulDestinationManager.AllGroupsListForReading`, i.e. SlotGroUPS.
    //    **Food lying on unzoned ground counts as ZERO.** A quicktest map
    //    generates no stockpile at all, so the default state of a fresh colony
    //    is that none of its food is visible to this number.
    //  * `ResourceCounter.ShouldCount` is
    //    `if (t.IsNotFresh()) return false;` — and
    //    `RimWorld/RottableUtility.IsNotFresh` is
    //
    //        var c = t.TryGetComp<CompRottable>();
    //        if (c != null) return c.Stage != RotStage.Fresh;
    //        return false;
    //
    //    written out in full because the obvious one-line paraphrase
    //    (`TryGetComp<CompRottable>()?.Stage != RotStage.Fresh`) INVERTS the
    //    null case: a null comp makes `null != Fresh` true, i.e. it would say
    //    imperishable food is excluded from `food_days`, which is the opposite
    //    of the truth and the opposite of what the `imperishable` band below
    //    rests on. **So the instant a stack finishes rotting it leaves
    //    `food_days` entirely**, with no warning during the ramp: the number
    //    sits at full value and then falls off a cliff.
    //  * `grep -rn CompRottable Source/AutoRimmer/` returned NOTHING before this
    //    file. There was no rot term anywhere in the mod.
    //
    // So a colony that builds a freezer it cannot set watches `food_days` report
    // a fortnight of food right up to the morning it has none.
    //
    // ===================== WHAT SHIPS, AND WHAT DOES NOT ====================
    // **`food_days` IS NOT REDEFINED.** It is a shipped predicate target
    // (`DigestVerb.PredicateSections` includes `resources`), `accept/` suites
    // assert on it, and its documented meaning — "the vanilla Alert_LowFood
    // division" — is a true and useful fact about what the ALERT will do.
    // Silently changing what it counts would break every consumer in the
    // direction that looks fine. What ships instead is
    //
    //   * `resources.food_days_basis` — a sentence on the shipped field saying
    //     what it does not count, so the disclaimer stops living only in a
    //     source comment the agent cannot read (the exact failure `Materials.cs`
    //     was written to fix: git-bug 54b0c9a); and
    //   * `resources.food_rot` — the honest block beside it, whose `days` is
    //     the same division over MAP-WIDE fresh food and whose bands say how
    //     much of that food has a clock on it.
    //
    // ======================== WHY NOT `Materials.Of` ========================
    // The round brief asks whether `food_days` should use `Materials.Of`, the
    // count that "asks the builder's own question". No, and `Materials.cs`'s own
    // header says why in its last line: *"This is a per-CALL cost on a read,
    // never a per-frame one: no predicate and no digest section reaches this
    // file."* It runs `Pawn.CanReach` per stack per builder — a PATHFIND — and
    // `resources` is a registered predicate section evaluated once per cadence
    // window inside `advance`. Session 19's cost axis names pathfinding as the
    // disqualifier explicitly. It is also per-DEF, and food is dozens of defs.
    //
    // What this file does instead is the cheap half of the same correction:
    // `map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree)`
    // is a stored list, so map-wide nutrition costs a walk of the food stacks
    // and no reachability at all. The honest consequence is stated in the
    // output rather than hidden: `nutrition` is an UPPER bound (it counts food
    // nobody can reach), `food_nutrition` from the resource counter is a LOWER
    // bound (it counts only what is zoned), and `nutrition_forbidden` narrows
    // the gap for free because `Thing.IsForbidden(Faction)` short-circuits on
    // `compForbiddable` and never walks a lord. An agent that needs the
    // builder's answer for one def calls a verb that pays for it.
    //
    // ============================ THE BANDS =================================
    // `Verse/GenTemperature.RotRateAtTemperature` is the whole model:
    //
    //     temperature <  0  -> 0        (frozen: no rot at all)
    //     temperature >= 10 -> 1        (full speed)
    //     otherwise         -> temp/10  (linear ramp)
    //
    // and `CompRottable.CompTickInterval` is `RotProgress += rate * delta`. The
    // three bands published here are `CompRottable.CompInspectStringExtra`'s
    // OWN three-way split, cutpoints included — `< 0.001` reads
    // "CurrentlyFrozen", `< 0.999` reads "CurrentlyRefrigerated", else
    // "NotRefrigerated" — so the digest says what the player's inspect pane
    // says. `TicksUntilRotAtCurrentTemp` is likewise the game's own
    // `(TicksToRotStart - RotProgress) / rate`, rounded, and returns
    // 72,000,000 (1200 days) when the rate is zero, which this file publishes as
    // null rather than as a plausible-looking number.
    //
    // ===================== MUTATION / COST HAZARDS ==========================
    // Reached from `digest` and therefore from a predicate cadence, so:
    //  * `ListerThings.ThingsInGroup` returns the real backing list
    //    (`listsByGroup[(uint)group]`). Snapshotted before the loop, which
    //    reaches modded comp getters.
    //  * `ThingDef.GetStatValueAbstract(StatDefOf.Nutrition)` is memoised per
    //    def for the life of one scan — the same trick `WorldSafe.MaxHpMemo` and
    //    `DeteriorateMemo` use, and the reason this walk is affordable where
    //    `ResourceCounter.TotalHumanEdibleNutrition` pays per def per read.
    //    Worth knowing rather than relying on: the filter one line earlier,
    //    `ThingDef.IsNutritionGivingIngestible`, already routes through
    //    `IngestibleProperties.CachedNutrition`, which lazily calls the SAME
    //    stat and writes `cachedNutrition` onto the def. So vanilla is already
    //    memoising this value, and that lazy write is the one hidden write in
    //    this scan — benign, because ThingDefs are not scribed, but it is a
    //    write and it belongs in this list rather than in nobody's.
    //  * `Thing.AmbientTemperature` bottoms out in
    //    `GenTemperature.TryGetDirectAirTemperatureForCell` -> `c.GetRoom(map)`
    //    -> `Room.Temperature`, which is `RoomTempTracker.Temperature`'s
    //    `temperatureInt` field. It is not a stat call. For a stack standing in
    //    a cell with NO room (inside a wall) it falls through to
    //    `TryGetAirTemperatureAroundThing`, which is eight more `GetRoom`
    //    lookups — so it is read ONCE per stack, memoised per room, and handed
    //    to `TicksUntilRotAtTemp(temp)` rather than letting
    //    `TicksUntilRotAtCurrentTemp` read it a second time.
    //  * `IntVec3.GetRoom(map)` can flush a pending region rebuild. It is the
    //    same pre-existing property of the whole observation surface that
    //    `TemperatureVerbs`' header documents at length; not repeated here.
    //  * `CompRottable.RotProgress`'s SETTER can fire `StageChanged()`. Only the
    //    getter is used here. `TicksUntilRotAtTemp` allocates nothing.
    //    `CompRottable.Active` reaches `CompHatcher.TemperatureDamaged` ->
    //    `CompTemperatureRuinable.Ruined`, checked and confirmed a pure read: it
    //    resolves its comp each call and caches nothing.
    //  * `ThingWithComps.GetComp<T>()` is reached through
    //    `TemperatureVerbs.Comp<T>`, because an empty non-null comps list makes
    //    it throw IndexOutOfRange rather than return null. See that helper.
    //  * Fogged stacks are excluded, matching `ResourceCounter.ShouldCount` and
    //    DESIGN's fog rule for the whole player-facing surface.
    //  * The whole scan is memoised per (map, tick) — see `Of`.
    internal static class FoodRot
    {
        // A PATHOLOGY CEILING, not a context budget, and the difference is the
        // whole reason it is 5000 rather than the 600 an earlier draft used.
        //
        // Every other cap in this mod truncates a LIST and orders by importance
        // first, so the reader loses only boring rows. This cap cannot do that:
        // the scan produces an AGGREGATE, the stacks arrive in `listerThings`
        // insertion order, and there is no cheap way to know which stack is the
        // one about to spoil without looking at it. A 600-stack cap therefore
        // meant `food_rot.ok` could read TRUE because the stack with the
        // shortest clock happened to be the 700th — the alarm silently
        // under-reporting, which is precisely the failure this file exists to
        // fix, reintroduced one level down.
        //
        // Two things make the high ceiling affordable. The per-stack work is a
        // def dictionary lookup, one comp lookup, one region-grid room lookup
        // and arithmetic; and the room temperature is memoised per room, so
        // `AmbientTemperature` is not paid twice per stack. And when the ceiling
        // DOES bite, `ok` goes false with `truncated: true` rather than staying
        // true on a partial scan — an alarm that fails loud, since at five
        // thousand food stacks something is wrong anyway.
        private const int StackCap = 5000;

        // OURS. `food_rot.ok` is false when something has already spoiled or
        // when the soonest thing to spoil does so inside this many days. A
        // threshold with no name is a threshold nobody can check, so it is
        // published as `warn_days` on every read.
        private const float WarnDays = 1f;

        private const float TicksPerDay = 60000f;

        // GenTemperature.RotRateAtTemperature's own zero, and
        // CompRottable.CompInspectStringExtra's own two cutpoints.
        private const float FrozenRate = 0.001f;
        private const float RefrigeratedRate = 0.999f;

        internal sealed class RoomFood
        {
            public Room Room;
            public int Stacks;
            public float Nutrition;
            public float NutritionFrozen;
            public float NutritionRefrigerated;
            public float NutritionUnrefrigerated;
            public float MaxRotRate;
            public float WorstRotPct;
            public int SoonestRotTicks = int.MaxValue;
        }

        internal sealed class Scan
        {
            public int Stacks;
            public int StacksMore;
            public int FoggedStacks;
            public int SpoiledStacks;
            public int RottableStacks;
            // Human-edible by the flags and NOT a colony resource — corpses,
            // almost always. Counted so the exclusion is visible rather than
            // silent; see the membership test in Of().
            public int CorpseStacks;
            public int UncountedStacks;

            // FRESH human-edible nutrition, map-wide, unfogged. The upper bound.
            public float Nutrition;
            public float NutritionForbidden;
            public float Frozen;
            public float Refrigerated;
            public float Unrefrigerated;
            // Food with no CompRottable at all (packaged survival meals, kibble
            // in some modlists): real nutrition with no clock, and it must not
            // be silently folded into "unrefrigerated".
            public float Imperishable;
            public float SpoiledNutrition;

            public int SoonestRotTicks = int.MaxValue;
            public float SoonestRotNutrition;
            public float WorstRotPct;

            public readonly Dictionary<int, RoomFood> ByRoom = new Dictionary<int, RoomFood>();
            public string Error;
        }

        // ------------------------------------------------------------------
        // MEMOISED PER (map, tick), because a full `digest` builds BOTH
        // `resources` (for `food_rot`) and `temperature` (for the per-room food
        // rows) and would otherwise walk every food stack on the map twice per
        // read — and both are registered predicate sections, so an `advance`
        // pays it per cadence window as well.
        //
        // The key is the game tick and nothing finer, which is exactly right and
        // not an approximation: game state only changes on a tick, so two reads
        // inside one tick MUST agree, and a predicate evaluated on several
        // frames within one tick is asking a question whose answer cannot have
        // moved. The memo is dropped whenever the tick or the map changes, so it
        // never survives a save/load or a map switch. When `Find.TickManager` is
        // unavailable (no game) the memo is bypassed entirely rather than
        // keyed on a guess.
        private static Map memoMap;
        private static int memoTick = int.MinValue;
        private static Scan memo;

        internal static Scan Of(Map map)
        {
            int tick;
            try { tick = Find.TickManager.TicksGame; }
            catch { return Walk(map); }
            if (memo != null && ReferenceEquals(memoMap, map) && memoTick == tick) return memo;
            var fresh = Walk(map);
            memoMap = map;
            memoTick = tick;
            memo = fresh;
            return fresh;
        }

        // The one walk. `Walk` rather than `Scan` because `Scan` is the type it
        // returns and C# will not have both under one name.
        private static Scan Walk(Map map)
        {
            var s = new Scan();
            if (map == null) { s.Error = "no-map"; return s; }
            List<Thing> food;
            try
            {
                // FoodSourceNotPlantOrTree also includes the nutrient paste
                // DISPENSER (a Building) by `ThingListGroupHelper.Includes`'s
                // last clause; the per-thing test below drops it, because a
                // dispenser is not a stack of nutrition.
                food = new List<Thing>(
                    map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree));
            }
            catch (Exception e)
            {
                s.Error = e.GetType().Name + ": " + Journal.Truncate(e.Message, 120);
                return s;
            }

            var nutritionByDef = new Dictionary<ThingDef, float>();
            // The ROOM temperature memo. `Thing.AmbientTemperature` bottoms out
            // in `c.GetRoom(map).Temperature`, and it was being paid TWICE per
            // stack — once for the band and once inside
            // `TicksUntilRotAtCurrentTemp`. Read once per stack here, memoised
            // per room, and handed to `TicksUntilRotAtTemp(temp)` instead, which
            // is the same member `TicksUntilRotAtCurrentTemp` delegates to.
            var tempByRoom = new Dictionary<int, float>();
            // SilentFail: `Faction.OfPlayer` calls `Log.Error("Could not find
            // player faction.")` when it resolves null, and a red error raised
            // from an observer breaches the standing zero-red-errors invariant.
            // `IsForbidden(null)` already returns false, so a null faction
            // degrades to "nothing is forbidden" rather than to a log line.
            var player = Faction.OfPlayerSilentFail;

            for (int i = 0; i < food.Count; i++)
            {
                var t = food[i];
                if (t == null || t.Destroyed || t.def == null) continue;
                // ResourceCounter's OWN membership test, all three clauses, so
                // `nutrition` and `food_nutrition` count the same KIND of thing
                // and differ ONLY in scope. That is the whole claim this block
                // makes, and dropping a clause would quietly break it.
                //
                // `CountAsResource` is the clause that matters most and it is
                // the one that is easy to forget: it is
                // `resourceReadoutPriority != Uncounted`, and **a CORPSE fails
                // it while passing every other test.**
                // `ThingDefGenerator_Corpses` gives every corpse def
                // `ingestible.foodType = FoodTypeFlags.Corpse`, and
                // `IngestibleProperties.HumanEdible` is
                // `(OmnivoreHuman & foodType) != 0` with `OmnivoreHuman` =
                // 0x1F3F, which HAS the 0x8 Corpse bit — so without this clause
                // a battlefield would land in the colony's larder figure, pin
                // `spoiled_stacks` permanently non-zero and swamp
                // `soonest_rot_days` with a number about butchering rather than
                // about eating. Counted and published instead, because "when do
                // these corpses stop being butcherable" is a real question and
                // this is simply not the block that answers it.
                if (!t.def.IsNutritionGivingIngestible) continue;
                if (t.def.ingestible == null || !t.def.ingestible.HumanEdible) continue;
                if (!t.def.CountAsResource)
                {
                    if (t.def.IsCorpse) s.CorpseStacks++;
                    else s.UncountedStacks++;
                    continue;
                }
                int count = t.stackCount;
                if (count <= 0) continue;
                if (s.Stacks >= StackCap) { s.StacksMore++; continue; }
                s.Stacks++;


                try { if (t.Fogged()) { s.FoggedStacks++; continue; } }
                catch { s.FoggedStacks++; continue; }

                float per;
                if (!nutritionByDef.TryGetValue(t.def, out per))
                {
                    try { per = t.def.GetStatValueAbstract(StatDefOf.Nutrition); }
                    catch { per = 0f; }
                    nutritionByDef[t.def] = per;
                }
                float nutrition = per * count;
                if (nutrition <= 0f) continue;

                // TemperatureVerbs.Comp<T> and not a bare GetComp: an empty
                // non-null `comps` list makes `ThingWithComps.GetComp<T>()`
                // throw IndexOutOfRange from `comps[0]` on its short-list fast
                // path, which a modded def with one broken comp produces. See
                // that helper's comment.
                var rot = TemperatureVerbs.Comp<CompRottable>(t);
                bool active = false;
                try { active = rot != null && rot.Active; }
                catch { }

                if (rot != null)
                {
                    RotStage stage = RotStage.Fresh;
                    try { stage = rot.Stage; } catch { }
                    if (stage != RotStage.Fresh)
                    {
                        // EXACTLY what `food_days` drops on the floor:
                        // ResourceCounter.ShouldCount -> IsNotFresh. Counted
                        // separately and never folded into `nutrition`, so the
                        // two numbers stay comparable.
                        s.SpoiledStacks++;
                        s.SpoiledNutrition += nutrition;
                        continue;
                    }
                }

                s.Nutrition += nutrition;
                try { if (t.IsForbidden(player)) s.NutritionForbidden += nutrition; }
                catch { }

                var room = RoomOf(t, map);
                RoomFood rf = null;
                if (room != null)
                {
                    if (!s.ByRoom.TryGetValue(room.ID, out rf))
                    {
                        rf = new RoomFood { Room = room };
                        s.ByRoom[room.ID] = rf;
                    }
                    rf.Stacks++;
                    rf.Nutrition += nutrition;
                }

                if (!active)
                {
                    // No CompRottable (or a disabled one): real nutrition with
                    // no clock. It belongs in NO band — folding it into
                    // "unrefrigerated" would invent a deadline it does not have.
                    s.Imperishable += nutrition;
                    continue;
                }

                s.RottableStacks++;
                float ambient = 21f;
                if (room != null && tempByRoom.TryGetValue(room.ID, out var cached)) ambient = cached;
                else
                {
                    try { ambient = t.AmbientTemperature; } catch { }
                    if (room != null) tempByRoom[room.ID] = ambient;
                }
                // CompRottable rounds before asking for the rate, in BOTH
                // CompInspectStringExtra and TicksUntilRotAtCurrentTemp. Match
                // it, or the band and the clock disagree at the cutpoints.
                int roundedC = Mathf.RoundToInt(ambient);
                float rate = 1f;
                try { rate = GenTemperature.RotRateAtTemperature(roundedC); }
                catch { }

                if (rate < FrozenRate)
                {
                    s.Frozen += nutrition;
                    if (rf != null) rf.NutritionFrozen += nutrition;
                }
                else if (rate < RefrigeratedRate)
                {
                    s.Refrigerated += nutrition;
                    if (rf != null) rf.NutritionRefrigerated += nutrition;
                }
                else
                {
                    s.Unrefrigerated += nutrition;
                    if (rf != null) rf.NutritionUnrefrigerated += nutrition;
                }
                if (rf != null && rate > rf.MaxRotRate) rf.MaxRotRate = rate;

                float pct = 0f;
                try { pct = rot.RotProgressPct; } catch { }
                // `RotProgressPct` divides by `PropsRot.TicksToRotStart`, so a
                // modded `daysToRotStart: 0` yields Infinity or NaN — which
                // Math.Round passes straight through into JSON as a token no
                // parser on the other end accepts.
                if (!float.IsNaN(pct) && !float.IsInfinity(pct))
                {
                    if (pct > s.WorstRotPct) s.WorstRotPct = pct;
                    if (rf != null && pct > rf.WorstRotPct) rf.WorstRotPct = pct;
                }

                if (rate > 0f)
                {
                    int ticks = int.MaxValue;
                    // The same member TicksUntilRotAtCurrentTemp delegates to,
                    // handed the temperature already read and rounded above
                    // rather than making it read AmbientTemperature a second
                    // time. Identical arithmetic, half the room lookups.
                    try { ticks = rot.TicksUntilRotAtTemp(roundedC); } catch { }
                    // 72,000,000 is CompRottable.TicksUntilRotAtTemp's own
                    // "never" sentinel; treating it as a deadline would publish
                    // 1200 days as if it meant something.
                    if (ticks > 0 && ticks < 72000000)
                    {
                        if (ticks < s.SoonestRotTicks)
                        {
                            s.SoonestRotTicks = ticks;
                            s.SoonestRotNutrition = nutrition;
                        }
                        else if (ticks == s.SoonestRotTicks) s.SoonestRotNutrition += nutrition;
                        if (rf != null && ticks < rf.SoonestRotTicks) rf.SoonestRotTicks = ticks;
                    }
                }
            }
            return s;
        }

        // A thing's room, the same route Thing.AmbientTemperature takes, minus
        // the held/unspawned branches this scan never sees (a stack inside a
        // container is not in a room and simply has none).
        private static Room RoomOf(Thing t, Map map)
        {
            try
            {
                if (!t.Spawned || t.Map != map) return null;
                return t.Position.GetRoom(map);
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------
        // `resources.food_rot`. Every band is nutrition, the same unit
        // `food_nutrition` is in, so an agent can compare them without a
        // conversion it has to be told about.
        internal static Dictionary<string, object> Block(Scan s, int needers, float inStockpiles)
        {
            if (s.Error != null)
                return new Dictionary<string, object> { ["error"] = s.Error };

            double? soonestDays = s.SoonestRotTicks == int.MaxValue
                ? (double?)null
                : Math.Round(s.SoonestRotTicks / TicksPerDay, 2);
            // FAILS LOUD ON TRUNCATION. See StackCap's comment: this cap cannot
            // order by importance before it cuts, so a partial scan's `true` is
            // not a claim this block is entitled to make.
            bool ok = s.StacksMore == 0
                && s.SpoiledStacks == 0
                && (!soonestDays.HasValue || soonestDays.Value >= WarnDays);

            var d = new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["warn_days"] = Math.Round((double)WarnDays, 2),
                // THE HONEST DIVISION: the same denominator `food_days` uses,
                // over map-wide fresh human-edible nutrition instead of over
                // stockpiled-and-fresh.
                ["days"] = needers > 0 ? (object)Math.Round(s.Nutrition / needers, 1) : null,
                ["days_frozen"] = needers > 0 ? (object)Math.Round(s.Frozen / needers, 1) : null,
                ["nutrition"] = Math.Round(s.Nutrition, 1),
                // Published side by side so the stockpile gap is a subtraction
                // the reader can do, not a thing they have to be told about.
                ["nutrition_in_stockpiles"] = Math.Round((double)inStockpiles, 1),
                ["nutrition_forbidden"] = Math.Round(s.NutritionForbidden, 1),
                ["frozen"] = Math.Round(s.Frozen, 1),
                ["refrigerated"] = Math.Round(s.Refrigerated, 1),
                ["unrefrigerated"] = Math.Round(s.Unrefrigerated, 1),
                ["imperishable"] = Math.Round(s.Imperishable, 1),
                // WHAT `food_days` SILENTLY DROPS. ResourceCounter.ShouldCount
                // excludes anything IsNotFresh, so this nutrition is invisible
                // to the shipped field and to vanilla's own alert.
                ["spoiled_stacks"] = s.SpoiledStacks,
                ["spoiled_nutrition"] = Math.Round(s.SpoiledNutrition, 1),
                ["soonest_rot_days"] = soonestDays,
                ["soonest_rot_nutrition"] = Math.Round(s.SoonestRotNutrition, 1),
                ["worst_rot_pct"] = Math.Round(s.WorstRotPct * 100.0, 1),
                ["stacks"] = s.Stacks,
                ["rottable_stacks"] = s.RottableStacks,
                ["fogged_stacks"] = s.FoggedStacks,
                // EXCLUDED ON PURPOSE, and said out loud. A corpse is
                // human-edible by the food flags but is not a colony resource,
                // so `food_days` never counted it and neither does this. "When
                // do these corpses stop being butcherable" is a real question
                // and a different one.
                ["corpse_stacks_excluded"] = s.CorpseStacks,
                ["uncounted_stacks_excluded"] = s.UncountedStacks,
                ["scope"] = "map-wide",
                ["basis"] = "listerThings FoodSourceNotPlantOrTree, filtered to "
                    + "IsNutritionGivingIngestible && ingestible.HumanEdible && CountAsResource "
                    + "(ResourceCounter's own three-clause membership test — the third is what "
                    + "keeps corpses out), unfogged, FRESH only. Bands and clock are "
                    + "GenTemperature.RotRateAtTemperature and "
                    + "CompRottable.TicksUntilRotAtCurrentTemp, with "
                    + "CompRottable.CompInspectStringExtra's own 0.001/0.999 cutpoints.",
                ["note"] = "`nutrition` is an UPPER bound (it counts food nobody can reach; no "
                    + "reachability is tested, because this block is evaluated on a predicate "
                    + "cadence and Materials.Of pathfinds). `nutrition_in_stockpiles` is the "
                    + "LOWER bound and is the same number as resources.food_nutrition. "
                    + "`soonest_rot_days` is null when nothing is rotting at its current "
                    + "temperature — that is `frozen`, not `no data`.",
            };
            if (s.StacksMore > 0)
            {
                d["truncated"] = true;
                d["stacks_more"] = s.StacksMore;
                d["stacks_cap"] = StackCap;
                d["truncated_note"] = "the scan hit its " + StackCap + "-stack ceiling, so every "
                    + "aggregate above is a LOWER bound and `soonest_rot_days` may not be the "
                    + "soonest. `ok` is false for that reason alone and not because anything is "
                    + "known to be spoiling — this cap cannot order by importance before it cuts, "
                    + "so a partial scan is not allowed to report all-clear.";
            }
            return d;
        }

        // The compact per-room half, for digest.temperature's room rows.
        internal static Dictionary<string, object> RoomOut(RoomFood rf)
        {
            var d = new Dictionary<string, object>
            {
                ["stacks"] = rf.Stacks,
                ["nutrition"] = Math.Round(rf.Nutrition, 1),
                ["frozen"] = Math.Round(rf.NutritionFrozen, 1),
                ["refrigerated"] = Math.Round(rf.NutritionRefrigerated, 1),
                ["unrefrigerated"] = Math.Round(rf.NutritionUnrefrigerated, 1),
                ["rot_rate"] = Math.Round(rf.MaxRotRate, 3),
                ["worst_rot_pct"] = Math.Round(rf.WorstRotPct * 100.0, 1),
                ["soonest_rot_days"] = rf.SoonestRotTicks == int.MaxValue
                    ? (object)null
                    : Math.Round(rf.SoonestRotTicks / TicksPerDay, 2),
            };
            return d;
        }
    }
}
