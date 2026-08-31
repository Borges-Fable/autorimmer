using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ==================== THE WORLD READ-SAFETY LAYER ======================
    // Spec 2.4, and the exact counterpart of PawnSafe (2.2): "read-only" and
    // "a plain field read" are not the same thing on a Zone, a Bill, a Room or
    // the ResearchManager either, and none of the traps below are visible at
    // the call site. Read PawnSafe's header first — the class letters here are
    // the same ones, extended with one class Pawn does not have.
    //
    // Every accessor was checked by hand against the decompiled 1.6 tree built
    // from THIS bench's Assembly-CSharp.dll
    // (misc/rimworld/reference/decompiled/RimWorldBase). Citations are FILE +
    // MEMBER, never a line offset — verify by grepping the member name.
    //
    // Where the game's public accessor writes, the guarded route is in this
    // file and the serializers call it. They never touch the raw getter.
    //
    // ------------------- CLASS R: ADVANCES THE RNG --------------------------
    // New with this spec, and the worst class in it. _mp/DETERMINISM.md:
    // "`Rand.*` in UI/draw code — desyncs the shared stream", and "a lazy
    // getter is how simulation code ends up running from UI".
    //
    //  * Zone.Cells — Verse/Zone.cs. The GETTER does
    //      if (!cellsShuffled) { cells.Shuffle(); cellsShuffled = true; }
    //    and Verse/GenList.cs Shuffle() is a Fisher-Yates over
    //    Rand.RangeInclusive. So the first read of ANY zone's Cells from an
    //    observer both (a) advances the shared Rand stream and (b) permanently
    //    reorders `cells`, which is Scribe_Collections-scribed. cellsShuffled
    //    is reset by AddCell/RemoveCell, so it re-arms every time the player
    //    edits a zone. Reached from Zone_Growing.ContentsStatistics,
    //    Zone_Growing.GetInspectString and IPlantToGrowSettable.Cells — i.e.
    //    from every obvious way to answer "what is growing in this zone".
    //    => ZoneCells(): the public `cells` FIELD, which is the same list
    //       without the shuffle. Zone.CellCount reads `cells.Count` directly
    //       and is safe; AllSlotCellsList() returns `cells` and is safe.
    //
    // ---------------------- CLASS A: WRITES THE SAVE ------------------------
    // Reading these permanently changes the game, and the change is scribed.
    //
    //  * Zone_Growing.PlantDefToGrow — RimWorld/Zone_Growing.cs. The GETTER
    //    does `if (plantDefToGrow == null) plantDefToGrow = <Toxipotato or
    //    Potato>` after running PollutionUtility.SettableEntirelyPolluted
    //    (a walk over the zone's cells), and plantDefToGrow is
    //    `Scribe_Defs.Look(ref plantDefToGrow, "plantDefToGrow")`. So ASKING a
    //    never-configured growing zone what it grows PINS its answer forever —
    //    including pinning "potato" on a zone that would later have defaulted
    //    to toxipotato. GetPlantDefToGrow() is the same getter.
    //    => PlantToGrow(): private backing field via AccessTools; null is
    //       reported as null and `plant_default` says what the game WOULD
    //       choose. "Not configured" is a real, reportable state.
    //    NOTE FOR THE ORCHESTRATOR: CropRenderer.Cell (Spatial.cs, spec 2.3,
    //    shipped) calls zg.GetPlantDefToGrow() to build the zone legend, so
    //    `map-view` trips this today. Not this spec's file to change.
    //
    //  * ResearchManager.GetProgress — RimWorld/ResearchManager.cs. On a miss
    //    it does `progress.Add(proj, 0f)` (and, for a knowledge project,
    //    `anomalyKnowledge.Add(proj, 0f)`), and BOTH dictionaries are
    //    Scribe_Collections-scribed. ResearchProjectDef.ProgressReal is that
    //    call, and IsFinished is `ProgressReal >= Cost`, and
    //    PrerequisitesCompleted is IsFinished over the prerequisites, and
    //    CanStartNow is all of the above. So the obvious research serializer —
    //    `AllDefs.Where(p => p.CanStartNow)` — adds a zero entry for every
    //    research project on the bench (six DLCs plus mods) to the save, on
    //    the first `research` call, permanently.
    //    => Progress()/Finished()/PrereqsDone()/CanStart(): the private
    //       dictionaries via AccessTools with TryGetValue, and the rest of
    //       CanStartNow's clauses re-derived from the sub-getters that do not
    //       write. This is the observer form of DESIGN's "the gate lives in
    //       the widget, so re-implement it and cite it".
    //
    //  * RecipeDef.AvailableNow — Verse/RecipeDef.cs. Its first clause is
    //    `researchPrerequisite.IsFinished`, so it is the SAME trap one level
    //    up: asking "can this bench make this?" over a bench's AllRecipes adds
    //    a zero entry per research-gated recipe to the save. Reached from
    //    ITab_Bills.FillTab's options maker and from HealthCardUtility's
    //    recipeOptionsMaker — i.e. from every obvious way to answer "what can
    //    I build a bill for".
    //    => RecipeAvailableNow(): AvailableNow clause for clause with
    //       Finished() in place of IsFinished. Moved here from
    //       MedicalBillVerbs (spec 3.6) so there is ONE catalogue: 3.4 wrote
    //       it privately six minutes after 3.6's issue body was last edited,
    //       and two identical re-derivations of a vanilla getter is exactly
    //       the fork this file exists to prevent.
    //
    //  * Bill_Production.ShouldDoNow — RimWorld/Bill_Production.cs. Writes
    //    `paused` on three separate paths (unconditionally to false when the
    //    repeat mode is not TargetCount; to true when pauseWhenSatisfied and
    //    the count is met; back to false under unpauseWhenYouHave) and
    //    `paused` is `Scribe_Values.Look(ref paused, "paused")`. Bill.BaseColor
    //    calls it too, which is why the bill list looks like a read.
    //    => never called. `active` is derived here from the plain scribed
    //       fields (suspended / repeatMode / repeatCount / targetCount /
    //       paused) and `paused` is reported as STORED, not as recomputed.
    //
    // ------------------ CLASS E: SHARED / REBUILT LISTS ---------------------
    // Not mutations, but iterating one while anything re-enters is the
    // Collection-was-modified bug 2.1 shipped live.
    //
    //  * Room.Regions returns `tmpRegions`, a PER-ROOM list cleared and refilled
    //    on every access (Verse/Room.cs). Room.ContainedAndAdjacentThings does
    //    the same with `uniqueContainedThings`, and Room.ContainedThings /
    //    ThingCount share `uniqueContainedThingsOfDef`. Room.Owners enumerates
    //    ContainedBeds THREE times (Count(), a Where().Count(), then the
    //    foreach), each re-entering ContainedAndAdjacentThings — sequentially,
    //    which is why vanilla gets away with it.
    //    => every one of these is snapshotted into our own list before use, and
    //       Owners is materialised immediately and never held.
    //  * ListerThings.ThingsMatchingFilter fills a STATIC
    //    `tmpThingsMatchingFilter` (Verse/ListerThings.cs). => not used.
    //  * ListerThings.ThingsOfDef / ThingsInGroup return the REAL backing lists
    //    and are safe to read; snapshot before any loop that can reach mod code.
    //  * ListerThings.ThingsOfDef(MinifiedThing) Log.ErrorOnce's and tells you
    //    to use the group (breach of zero-red-errors). SpatialVerbs.Nearest
    //    already routes around it; `things` does the same.
    //  * RegionGrid.AllRooms is an IReadOnlyList over the real `allRooms` list.
    //    ZoneManager.AllZones and AreaManager.AllAreas likewise. BillStack.Bills
    //    likewise. OutfitDatabase.AllOutfits / FoodRestrictionDatabase
    //    .AllFoodRestrictions / DrugPolicyDatabase.AllPolicies likewise.
    //
    // ------------------- CLASS COST: LAZY, NOT WRONG ------------------------
    //
    //  * Room.Role and Room.GetStat both run UpdateRoomStatsAndRole() when
    //    statsAndRoleDirty — every RoomStatDef worker and every RoomRoleDef
    //    worker over the room's cells and contents. Idempotent and RNG-free, so
    //    read-only holds (DigestVerb's header has the standing warning), and
    //    ONE call clears the flag for both — so role + all stats cost one
    //    analysis per room, not nine. `rooms` is therefore capped and ORDERS
    //    BEFORE IT CUTS on a score that needs no analysis, and only the rooms
    //    that survive the cut are analysed at all.
    //  * ThingFilter.DisplayRootCategory runs RecalculateDisplayRootCategory()
    //    when null — a GenThreading.ParallelFor over every ThingCategory node
    //    crossed with every allowed def — and its SETTER then runs
    //    RecalculateSpecialFilterConfigurability(), writing
    //    allowedHitPointsConfigurable and allowedQualitiesConfigurable.
    //    => not used. See FilterSummary for what replaces it.
    //  * ThingFilter.Summary is read-only but useless for a storage filter: its
    //    first two branches need the XML-only `thingDefs`/`categories` lists,
    //    which a runtime storage filter does not have, so it falls through to
    //    the literal "UsableIngredients" translation. => not used.
    //  * Thing.MaxHitPoints is a GetStatValue (PawnSafe Class F: writes a
    //    non-scribed memo). Unavoidable for an HP range, so `things` memoises
    //    it per (def, stuff, quality) for the life of one call — see MaxHp.
    //  * RecipeDef.Worker / WorkerCounter Activator.CreateInstance on first
    //    read into a def-level field. Not scribed, def-scoped, idempotent — the
    //    same class as SkillRecord's memos.
    //
    // ---------------------- CLASS D: THROWS, NOT WRITES ---------------------
    //  * ChoiceLetter.Option_ViewInQuestsTab dereferences `quest.name` before
    //    its own `quest == null` check (Verse/ChoiceLetter.cs). Vanilla only
    //    reaches it behind `quest != null`, but a modded ChoiceLetter need not.
    //    => every Choices enumeration is inside a try/catch that degrades the
    //       ONE letter to `options_error`, never the verb.
    public static class WorldSafe
    {
        // -------------------------- Class R route ---------------------------
        // The real cell list, without Zone.Cells's Fisher-Yates shuffle.
        public static List<IntVec3> ZoneCells(Zone zone) => zone?.cells ?? EmptyCells;

        private static readonly List<IntVec3> EmptyCells = new List<IntVec3>();

        // -------------------------- Class A routes --------------------------
        private static bool refsTried;
        private static AccessTools.FieldRef<Zone_Growing, ThingDef> plantRef;
        private static AccessTools.FieldRef<ResearchManager, Dictionary<ResearchProjectDef, float>> progressRef;
        private static AccessTools.FieldRef<ResearchManager, Dictionary<ResearchProjectDef, float>> knowledgeRef;

        // Resolved lazily and TOLERANTLY, the PawnSafe way: a field ref that
        // fails to bind must degrade to "unknown", never take the verb down —
        // a mod can in principle replace any of these.
        private static void EnsureRefs()
        {
            if (refsTried) return;
            refsTried = true;
            try { plantRef = AccessTools.FieldRefAccess<Zone_Growing, ThingDef>("plantDefToGrow"); }
            catch (Exception e) { Journal.EmitWarning("world: growing-zone plant field ref failed: " + e.Message); }
            try
            {
                progressRef = AccessTools.FieldRefAccess<ResearchManager,
                    Dictionary<ResearchProjectDef, float>>("progress");
            }
            catch (Exception e) { Journal.EmitWarning("world: research progress field ref failed: " + e.Message); }
            try
            {
                knowledgeRef = AccessTools.FieldRefAccess<ResearchManager,
                    Dictionary<ResearchProjectDef, float>>("anomalyKnowledge");
            }
            catch (Exception e) { Journal.EmitWarning("world: anomaly knowledge field ref failed: " + e.Message); }
        }

        // True when the guarded routes are actually available. Published beside
        // the data as `source`, so "not configured" and "we could not look"
        // never read alike (PawnSafe.Policies's discipline).
        public static bool PlantRefOk { get { EnsureRefs(); return plantRef != null; } }

        public static bool ResearchRefsOk { get { EnsureRefs(); return progressRef != null; } }

        // The zone's CONFIGURED plant, or null when the player has never set
        // one. Never the lazy getter, which would write the null away.
        public static ThingDef PlantToGrow(Zone_Growing zone)
        {
            EnsureRefs();
            if (zone == null || plantRef == null) return null;
            try { return plantRef(zone); }
            catch { return null; }
        }

        // ------------------------ research, guarded -------------------------
        // Everything below reads the two scribed dictionaries through
        // TryGetValue and NEVER through ResearchManager.GetProgress, which
        // inserts on a miss.
        public static float Progress(ResearchProjectDef proj)
        {
            EnsureRefs();
            if (proj == null) return 0f;
            var mgr = Find.ResearchManager;
            if (mgr == null) return 0f;
            try
            {
                if (proj.baseCost > 0f)
                {
                    var d = progressRef?.Invoke(mgr);
                    return d != null && d.TryGetValue(proj, out var v) ? v : 0f;
                }
                if (ModsConfig.AnomalyActive && proj.knowledgeCost > 0f)
                {
                    var k = knowledgeRef?.Invoke(mgr);
                    return k != null && k.TryGetValue(proj, out var kv) ? kv : 0f;
                }
            }
            catch { }
            return 0f;
        }

        // ResearchProjectDef.IsFinished, without the insert.
        public static bool Finished(ResearchProjectDef proj)
        {
            if (proj == null) return false;
            float cost = proj.Cost;
            // Cost 0 projects are "finished" by vanilla's own >= test; keep the
            // same arithmetic rather than a nicer-looking one.
            return Progress(proj) >= cost;
        }

        // ResearchProjectDef.PrerequisitesCompleted, without the insert.
        public static bool PrereqsDone(ResearchProjectDef proj)
        {
            if (proj == null) return false;
            if (proj.prerequisites != null)
                for (int i = 0; i < proj.prerequisites.Count; i++)
                    if (!Finished(proj.prerequisites[i])) return false;
            if (proj.hiddenPrerequisites != null)
                for (int i = 0; i < proj.hiddenPrerequisites.Count; i++)
                    if (!Finished(proj.hiddenPrerequisites[i])) return false;
            return true;
        }

        // ResearchProjectDef.CanStartNow, clause for clause, with our own
        // Finished/PrereqsDone in place of the two that insert. The remaining
        // clauses (techprints, bench, mechanitor, analysis, codex, grav engine)
        // are the game's own sub-getters and none of them write.
        //
        // `benchOk` is passed in because PlayerHasAnyAppropriateResearchBench
        // walks every colonist building on every map, and the research verb
        // asks this question once per project.
        public static bool CanStart(ResearchProjectDef proj, out string blockedBy, Func<ResearchProjectDef, bool> benchOk)
        {
            blockedBy = null;
            if (proj == null) { blockedBy = "null"; return false; }
            if (Finished(proj)) { blockedBy = "finished"; return false; }
            if (!PrereqsDone(proj)) { blockedBy = "prerequisites"; return false; }
            try
            {
                if (proj.TechprintCount > 0 && Find.ResearchManager.GetTechprints(proj) < proj.TechprintCount)
                { blockedBy = "techprints"; return false; }
            }
            catch { }
            try
            {
                if (proj.requiredResearchBuilding != null && benchOk != null && !benchOk(proj))
                { blockedBy = "no-bench"; return false; }
            }
            catch { }
            try { if (!proj.PlayerMechanitorRequirementMet) { blockedBy = "mechanitor"; return false; } }
            catch { }
            try { if (!proj.AnalyzedThingsRequirementsMet) { blockedBy = "analysis"; return false; } }
            catch { }
            try
            {
                // IsHidden is `!IsFinished && Anomaly ? EntityCodex.Hidden : false`
                // — the IsFinished half is ours, so call only the codex half.
                if (ModsConfig.AnomalyActive && Find.EntityCodex != null && Find.EntityCodex.Hidden(proj))
                { blockedBy = "codex-hidden"; return false; }
            }
            catch { }
            try { if (!proj.InspectionRequirementsMet) { blockedBy = "grav-engine"; return false; } }
            catch { }
            return true;
        }

        // ------------------------ recipes, guarded --------------------------
        // Verse/RecipeDef.cs AvailableNow, clause for clause, with
        // WorldSafe.Finished in place of ResearchProjectDef.IsFinished. The
        // ideology/faction clauses do not write and are evaluated as vanilla
        // does; the role-apparel `Check()` unlock is reproduced because without
        // it an ideo-unlocked recipe would read as unavailable.
        //
        // Moved here from MedicalBillVerbs.PawnActs by spec 3.6 (git-bug
        // 48f666c comment #2, correction 3): 3.4 had already written this
        // privately, so the recipe-level re-derivation existed in one file
        // while the research-level one lived here. One catalogue.
        public static bool RecipeAvailableNow(RecipeDef recipe)
        {
            if (recipe == null) return false;
            try
            {
                if (recipe.researchPrerequisite != null && !Finished(recipe.researchPrerequisite)) return false;
                if (recipe.researchPrerequisites != null)
                    for (int i = 0; i < recipe.researchPrerequisites.Count; i++)
                        if (!Finished(recipe.researchPrerequisites[i])) return false;

                if (recipe.memePrerequisitesAny != null)
                {
                    bool any = false;
                    foreach (var meme in recipe.memePrerequisitesAny)
                        if (Faction.OfPlayer.ideos.HasAnyIdeoWithMeme(meme)) { any = true; break; }
                    if (!any) return false;
                }

                if (recipe.factionPrerequisiteTags != null)
                {
                    bool anyMissing = false;
                    foreach (var tag in recipe.factionPrerequisiteTags)
                    {
                        var tags = Faction.OfPlayer.def.recipePrerequisiteTags;
                        if (tags == null || !tags.Contains(tag)) { anyMissing = true; break; }
                    }
                    if (anyMissing && !UnlockedByRoleApparel(recipe)) return false;
                }

                if (recipe.fromIdeoBuildingPreceptOnly
                    && (!ModsConfig.IdeologyActive || !IdeoUtility.PlayerHasPreceptForBuilding(recipe.ProducedThingDef)))
                    return false;
            }
            catch { return false; }
            return true;
        }

        // Verse/RecipeDef.cs AvailableNow's local Check(): a faction-tag-gated
        // recipe is still available when one of the player's ideo roles
        // REQUIRES a piece of apparel this recipe produces.
        private static bool UnlockedByRoleApparel(RecipeDef recipe)
        {
            try
            {
                if (!ModsConfig.IdeologyActive) return false;
                foreach (var ideo in Faction.OfPlayer.ideos.AllIdeos)
                {
                    foreach (var role in ideo.RolesListForReading)
                    {
                        if (role.apparelRequirements == null) continue;
                        foreach (var req in role.apparelRequirements)
                        {
                            ThingDef want = null;
                            foreach (var d in req.requirement.AllRequiredApparel()) { want = d; break; }
                            if (want == null) continue;
                            foreach (var product in recipe.products)
                                if (product.thingDef == want) return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        // ------------------------- bills, guarded ---------------------------
        // Bill_Production.ShouldDoNow's answer WITHOUT its three writes to the
        // scribed `paused` field. `paused` here is the stored value, unchanged.
        // `count` is the already-computed product count (or -1 when unknown) so
        // this never triggers the count walk itself.
        public static string BillState(Bill bill, int count)
        {
            if (bill == null) return "none";
            if (bill.suspended) return "suspended";
            var prod = bill as Bill_Production;
            if (prod == null) return "active";
            if (prod.repeatMode == BillRepeatModeDefOf.Forever) return "active";
            if (prod.repeatMode == BillRepeatModeDefOf.RepeatCount)
                return prod.repeatCount > 0 ? "active" : "done";
            if (prod.repeatMode == BillRepeatModeDefOf.TargetCount)
            {
                if (count < 0) return "unknown";
                // The stored `paused` only sticks while the count is still at
                // or above unpauseWhenYouHave; below that vanilla clears it on
                // its next ShouldDoNow. Reported as the game will behave, with
                // the stored flag published separately.
                if (prod.pauseWhenSatisfied && prod.paused && count > prod.unpauseWhenYouHave) return "paused";
                return count < prod.targetCount ? "active" : "satisfied";
            }
            return "unknown";
        }

        // ------------------------------ fog ---------------------------------
        // DESIGN decisions log 2026-08-30: one rule across the player-facing
        // surface. Nothing in the 2.4 observers is dev:*.
        public static bool Hidden(Thing t, Map map)
        {
            if (t == null || map == null) return true;
            try
            {
                if (!t.Spawned) return true;
                if (t.Map != map) return true;
                return t.Position.Fogged(map);
            }
            catch { return true; }
        }

        // Room.Fogged is `RegionCount == 0 ? false : FirstRegion.AnyCell
        // .Fogged(Map)` — the game's own test, no analysis, no shared list.
        public static bool RoomHidden(Room room)
        {
            if (room == null) return true;
            try { return room.RegionCount == 0 || room.Fogged; }
            catch { return true; }
        }

        // The room a caller can name. A fogged room declines the lookup
        // outright, for the same reason `pawn <id>` does (2.2): confirming that
        // room N exists is itself the leak, so the error names the POLICY rather
        // than this room.
        public static Room FindRoom(Map map, int id)
        {
            try
            {
                // AllRooms is an IReadOnlyList over the real `allRooms` list.
                var all = map.regionGrid.AllRooms;
                for (int i = 0; i < all.Count; i++)
                    if (all[i] != null && all[i].ID == id && !RoomHidden(all[i])) return all[i];
            }
            catch { }
            throw new VerbArgsException(
                $"no visible room with id {id} on the current map "
                + "(rooms in unexplored ground are not reported)");
        }

        // A zone is hidden only when EVERY cell it owns is fogged: a stockpile
        // half-explored is a stockpile the colony knows about.
        public static bool ZoneHidden(Zone zone, Map map, out int foggedCells)
        {
            foggedCells = 0;
            if (zone == null || map == null) return true;
            var cells = ZoneCells(zone);
            if (cells.Count == 0) return false;
            for (int i = 0; i < cells.Count; i++)
            {
                try { if (cells[i].Fogged(map)) foggedCells++; }
                catch { foggedCells++; }
            }
            return foggedCells >= cells.Count;
        }

        // --------------------------- small helpers --------------------------

        public static Map CurrentMap() => PawnSafe.CurrentMap();

        public static int Pct(float unit) => (int)Math.Round(unit * 100f);

        public static double R(float v, int digits) => Math.Round(v, digits);

        // A modded getter that throws must degrade one FIELD to null, not fail
        // the whole verb — PawnSerializer's rule, same silence for the same
        // reason (32 mods on the bench; a per-field journal line is a storm).
        public static T Safe<T>(Func<T> f) where T : class
        {
            try { return f(); } catch { return null; }
        }

        public static object SafeObj(Func<object> f)
        {
            try { return f(); } catch { return null; }
        }

        // Thing.MaxHitPoints is a GetStatValue (PawnSafe Class F). A rollup can
        // cross thousands of things, so the answer is memoised per
        // (def, stuff, quality) for the life of ONE call — those three are what
        // the vanilla stat parts key on. Approximate by construction if a mod
        // adds a per-instance stat part; `hp_source` says so on every rollup.
        public sealed class MaxHpMemo
        {
            private readonly Dictionary<string, int> memo = new Dictionary<string, int>();

            public int Of(Thing t)
            {
                if (t?.def == null || !t.def.useHitPoints) return 0;
                string quality = "-";
                try { if (t.TryGetQuality(out var qc)) quality = qc.ToString(); }
                catch { }
                string key = t.def.defName + "|" + (t.Stuff?.defName ?? "-") + "|" + quality;
                if (memo.TryGetValue(key, out var v)) return v;
                int max = 0;
                try { max = t.MaxHitPoints; }
                catch { }
                memo[key] = max;
                return max;
            }
        }

        // Deteriorate-ability is a DEF property (Verse/ThingDef.CanEverDeteriorate
        // plus a non-zero abstract DeteriorationRate), so it costs one stat call
        // per def rather than one per thing. The per-THING half of the question
        // is just "is it under a roof", which is a grid read.
        public sealed class DeteriorateMemo
        {
            private readonly Dictionary<ThingDef, bool> memo = new Dictionary<ThingDef, bool>();

            public bool Of(ThingDef def)
            {
                if (def == null) return false;
                if (memo.TryGetValue(def, out var v)) return v;
                bool can = false;
                try
                {
                    can = def.CanEverDeteriorate
                        && def.deteriorateFromEnvironmentalEffects
                        && def.GetStatValueAbstract(StatDefOf.DeteriorationRate) > 0.00001f;
                }
                catch { }
                memo[def] = can;
                return can;
            }
        }
    }
}
