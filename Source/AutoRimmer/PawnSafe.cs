using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ===================== THE PAWN READ-SAFETY LAYER =======================
    // Spec 2.2. This file exists because "read-only" and "a plain field read"
    // are not the same thing on a Pawn, and the difference is not visible at
    // the call site: several public getters the pawn tabs use MUTATE the pawn
    // — some of them write Scribe-serialized fields, i.e. an observer that
    // changes the SAVE by looking. That is the _mp/DETERMINISM.md lazy-getter
    // hazard class, and Pawn is where it is thickest.
    //
    // Every accessor below was checked by hand against the decompiled 1.6 tree
    // generated from THIS bench's Assembly-CSharp.dll
    // (misc/rimworld/reference/decompiled/RimWorldBase). Citations are
    // FILE + MEMBER, never a line offset: the offsets in the issue's preserved
    // research came from a different decompile and do not match this one.
    // Verify by grepping the member name.
    //
    // Nothing here is dev:*. Nothing here writes. Where the game's public
    // accessor writes, the guarded route is in this file and the serializers
    // call it — they never touch the raw getter.
    //
    // ---------------------- CLASS A: WRITES THE SAVE ------------------------
    // Reading these permanently changes a pawn, and the change is scribed.
    //
    //  * Pawn_OutfitTracker.CurrentApparelPolicy — RimWorld/Pawn_OutfitTracker
    //    .cs. The GETTER does `if (curApparelPolicy == null) curApparelPolicy =
    //    Current.Game.outfitDatabase.DefaultOutfit();` and `curApparelPolicy`
    //    is `Scribe_References.Look(..., "curOutfit")`. So asking a raider, a
    //    visitor or a fresh prisoner "what outfit policy?" ASSIGNS them one,
    //    forever. Same shape in Pawn_FoodRestrictionTracker.CurrentFoodPolicy
    //    (field `curPolicy`, scribed "curFoodRestriction") and
    //    Pawn_DrugPolicyTracker.CurrentPolicy (field `curPolicy`).
    //    => Policy(): private backing fields via AccessTools, null reported as
    //       null. "No policy" is a real, reportable state; inventing one is not.
    //
    //  * Pawn_IdeoTracker.Certainty — RimWorld/Pawn_IdeoTracker.cs. The getter
    //    writes `certaintyInt = 0f` when the pawn is a baby, and certaintyInt is
    //    scribed. It also routes through Pawn.DevelopmentalStage (Class C).
    //    => not read at all. Ideo name and role are reported; certainty is not.
    //
    //  * Pawn_PlayerSettings.displayOrder — RimWorld/Pawn_PlayerSettings.cs.
    //    THE WRITER IS THE UI, NOT THE FIELD. `displayOrder` is a plain int
    //    that defaults to the sentinel `UnsetDisplayOrder` (-9999999) and is
    //    scribed (`Scribe_Values.Look(ref displayOrder, "displayOrder", 0)`),
    //    so reading it is harmless; what makes it Class A is that the only
    //    thing which ever ASSIGNS it is a draw pass.
    //    `ColonistBar.CheckRecacheEntries` (RimWorld/ColonistBar.cs) walks the
    //    bar's pawns and, for any still holding the sentinel, writes
    //    `Mathf.Max(tmpPawns.MaxBy(displayOrder).displayOrder, 0) + 1` into
    //    that scribed field — so the numbers exist only once the colonist bar
    //    has drawn, and on a parked or headless bench every colonist shares
    //    the sentinel. `PlayerPawnsDisplayOrderUtility.Sort/InOrder` is what
    //    the bar and the Assign/Work tables order by.
    //    => NOT READ, and not sorted by. It is the obvious reach for "the
    //       order the player sees" and it is a trap twice over: the value is
    //       a UI side effect on scribed state, and it is uniform (therefore
    //       useless as a key) exactly on the benches we run. `pawns` emits by
    //       `thingIDNumber`, a plain field on Verse/Thing that no getter
    //       touches. See git-bug 1eb2262 and the DESIGN decisions-log entry.
    //
    // ------------------- CLASS B: WRITES + RED ERROR ------------------------
    //
    //  * Pawn_WorkSettings.GetPriority / WorkIsActive / Disable — RimWorld/
    //    Pawn_WorkSettings.cs. All three open with ConfirmInitializedDebug(),
    //    which when `priorities == null` does BOTH `Log.Error(pawn + " did not
    //    have work settings initialized.")` AND EnableAndInitialize() — which
    //    allocates a DefMap and calls SetPriority up to 6 times, each able to
    //    reach pawn.jobs.Notify_WorkTypeDisabled. So reading one work priority
    //    off a pawn that never had work settings (most non-colonists) both
    //    breaches the standing zero-red-errors invariant AND gives that pawn
    //    work settings it never had.
    //    => WorkRow(): guarded on `workSettings != null && EverWork`
    //       (EverWork IS `priorities != null`, same file), which is the gate
    //       vanilla's own PawnColumnWorker_WorkPriority and JobGiver_Work use.
    //       Ungated, the section returns {"initialized":false} and calls
    //       NOTHING.
    //    => second trap in the same method, unrelated to initialization:
    //       GetPriority returns a hard 3 for every active work type when
    //       Find.PlaySettings.useWorkPriorities is off. The number you serialize
    //       is then not the stored number, so `work.use_priorities` is published
    //       beside the row and consumers must read it.
    //
    // ------------------- CLASS C: WRITES, INDIRECTLY ------------------------
    //
    //  * Pawn_AgeTracker.CurLifeStageIndex — Verse/Pawn_AgeTracker.cs. When
    //    `cachedLifeStageIndex < 0` it runs RecalculateLifeStageIndex(), which
    //    can CalculateInitialGrowth(), set lifeStageChange, touch
    //    pawn.Drawer.renderer.SetAllGraphicsDirty() (lazy-creating the Drawer),
    //    call CheckChangePawnKindName() — which can RENAME THE PAWN — and run
    //    PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn). Reached
    //    from CurLifeStage, CurLifeStageRace and therefore from
    //    Pawn.DevelopmentalStage and anything that asks whether a pawn is a
    //    baby.
    //    => not read. Ages come from AgeBiologicalYears / AgeChronologicalYears,
    //       which are pure division on ticks (same file).
    //
    //  * Pawn_RelationsTracker.OpinionOf — RimWorld/Pawn_RelationsTracker.cs ->
    //    ThoughtHandler.TotalOpinionOffset -> SituationalThoughtHandler
    //    .AppendSocialThoughts -> CheckRecalculateSocialThoughts, which CREATES
    //    a CachedSocialThoughts entry for the (observer, other) pair when none
    //    exists, instantiates Thought_SituationalSocial objects, and stamps
    //    lastQueryTick — the field `Expired` keys on, so querying also KEEPS
    //    entries alive that would have been reaped. Reaping happens in
    //    SituationalThoughtInterval, which does not run while the game is
    //    PAUSED — and this bench is paused by default, so entries an observer
    //    creates sit until time runs.
    //    Cost on top of that: each OpinionOf runs GetRelations, whose Kin
    //    worker walks FamilyByBlood — a full family-graph BFS
    //    (FamilyByBlood_Internal, same file). N pawns = N BFS walks.
    //    => Opinions are OPT-IN (`opinions:true`), bounded by OpinionCap, and
    //       the count actually perturbed is published as relations.opinions
    //       .queried. Default off. The cache is NOT scribed (ThoughtHandler
    //       .ExposeData scribes only `memories`; SituationalThoughtHandler has
    //       no ExposeData at all), so this is a cost-and-allocation hazard
    //       rather than a save-corrupting one — which is exactly why it is
    //       offered at all instead of banned.
    //
    //  * Pawn_RelationsTracker.RelatedPawns — sets canCacheFamilyByBlood = true
    //    on entry and clears it only in the iterator's finally. A foreach that
    //    breaks still disposes and still runs the finally; an enumerator
    //    obtained by hand and dropped does not, and leaves the family cache
    //    pinned. => not used. Direct relations come from the DirectRelations
    //    backing list.
    //
    // ------------------ CLASS D: THROWS, NOT WRITES -------------------------
    //
    //  * Pawn_PlayerSettings.EffectiveAreaRestrictionInPawnCurrentMap —
    //    RimWorld/Pawn_PlayerSettings.cs. Does `allowedAreas.TryGetValue(pawn
    //    .MapHeld, ...)` with NO null check on MapHeld, and
    //    Dictionary.TryGetValue(null) throws ArgumentNullException. Its sibling
    //    AreaRestrictionInPawnCurrentMap has the guard.
    //    => AreaUtility.AreaAllowedLabel is used, which reads the GUARDED
    //       sibling — and is also the Restrict tab's own label.
    //
    //  * Pawn.IsPrisonerOfColony — Verse/Pawn.cs. `guest.IsPrisoner` then
    //    `guest.HostFaction.IsPlayer` with no null check on HostFaction, so a
    //    GuestStatus.Prisoner pawn with a null host faction NREs.
    //    => Classify() tests guest.HostFaction explicitly first.
    //
    // ---------------- CLASS E: SHARED / REBUILT LISTS -----------------------
    // Not mutations, but iterating them while anything re-enters is the
    // Collection-was-modified bug 2.1 shipped live.
    //
    //  * MapPawns.FreeColonistsSpawned (via FreeHumanlikesSpawnedOfFaction)
    //    CLEARS and refills one cached List on EVERY access. Verse/MapPawns.cs.
    //    AllPawnsSpawned returns the real `pawnsSpawned` list and is safe;
    //    PrisonersOfColonySpawned likewise returns its real backing list.
    //    => the pawn roster is built from ONE snapshot of AllPawnsSpawned and
    //       classified here, so no MapPawns getter is ever re-entered mid-loop.
    //  * TraitSet.TraitsSorted clears and refills a per-instance tmp list on
    //    every access (RimWorld/TraitSet.cs). => `allTraits` (the real list) is
    //    read instead.
    //  * Pawn.GetDisabledWorkTypes returns THE cached list (Verse/Pawn.cs).
    //    Read, never retained, never mutated.
    //
    // ------------- CLASS F: WRITES A MEMO, NOT GAME STATE -------------------
    // Accepted deliberately; see the resolution comment on git-bug 69ae91f.
    //
    //  * StatWorker.GetValue writes temporaryStatCache (a plain Dictionary) or
    //    immutableStatCache on EVERY call, and the write is NOT gated on the
    //    cacheStaleAfterTicks argument — there is no read-only mode
    //    (RimWorld/StatWorker.cs). Thing.MaxHitPoints IS a GetStatValue call
    //    (Verse/Thing.cs), so apparel HP% cannot avoid it. What is written is a
    //    pure memo keyed by Thing: not scribed, not simulation-visible,
    //    recomputed from the same inputs, and the identical write the vanilla
    //    inspect pane performs every frame the cursor rests on an item.
    //    CONSEQUENCE THAT IS NOT OPTIONAL: temporaryStatCache is not
    //    concurrent, so GetStatValue off the main thread corrupts it. This is
    //    an independent proof of the project's main-thread rule, not a style
    //    preference.
    //    Confined to the `pawn <id>` drill-down; the `pawns` hot path makes no
    //    stat call at all.
    //  * SkillRecord.Level -> GetLevel -> TotallyDisabled / PermanentlyDisabled
    //    / Aptitude fill three BoolUnknown/int? memos on first read
    //    (RimWorld/SkillRecord.cs). PawnCapacitiesHandler.GetLevel fills a
    //    DefMap memo (Verse/PawnCapacitiesHandler.cs). Both are dirtied by the
    //    game on the right events. Idempotent, not scribed.
    //  * SituationalThoughtHandler.AppendMoodThoughts runs UpdateAllMoodThoughts
    //    when thoughtsDirty — the same recompute opening the Needs tab triggers,
    //    on the game's own dirty flag, into a non-scribed cache.
    //
    // -------------------------- NOT USED, WHY ------------------------------
    //  * SocialCardUtility.* other than GetPawnSituationLabel: static
    //    cachedForPawn/cachedEntries and a cleared-and-refilled shared static.
    //    And GetPawnSituationLabel itself runs QuestUtility
    //    .GetAllQuestPartsOfType<QuestPart_LendColonistsToFaction>() with a
    //    LINQ scan over every quest part per call — read-only but not cheap.
    //  * WorkTypeDef.VisibleCurrently writes a frame cache and walks
    //    PawnsFinder. The plain `def.visible` field is what
    //    PawnColumnDefGenerator filters the Work tab's columns on.
    //  * PawnRelationUtility.GetMostImportantColonyRelative runs GetRelations
    //    against every free colonist and prisoner on every map.
    //  * QuestUtility.IsQuestLodger clears/refills a static and walks every
    //    quest part.
    public static class PawnSafe
    {
        // ---- Class A: private backing fields, so "unset" stays unset. -------
        // Same technique as AlertScanner's activeAlerts ref. Resolved lazily and
        // tolerantly: a Harmony field-ref that fails to bind must degrade to
        // "unknown", never take the verb down, because a mod could in principle
        // replace one of these trackers.
        private static bool policyRefsTried;
        private static AccessTools.FieldRef<Pawn_OutfitTracker, ApparelPolicy> apparelPolicyRef;
        private static AccessTools.FieldRef<Pawn_FoodRestrictionTracker, FoodPolicy> foodPolicyRef;
        private static AccessTools.FieldRef<Pawn_DrugPolicyTracker, DrugPolicy> drugPolicyRef;

        private static void EnsurePolicyRefs()
        {
            if (policyRefsTried) return;
            policyRefsTried = true;
            try { apparelPolicyRef = AccessTools.FieldRefAccess<Pawn_OutfitTracker, ApparelPolicy>("curApparelPolicy"); }
            catch (Exception e) { Journal.EmitWarning("pawn: outfit policy field ref failed: " + e.Message); }
            try { foodPolicyRef = AccessTools.FieldRefAccess<Pawn_FoodRestrictionTracker, FoodPolicy>("curPolicy"); }
            catch (Exception e) { Journal.EmitWarning("pawn: food policy field ref failed: " + e.Message); }
            try { drugPolicyRef = AccessTools.FieldRefAccess<Pawn_DrugPolicyTracker, DrugPolicy>("curPolicy"); }
            catch (Exception e) { Journal.EmitWarning("pawn: drug policy field ref failed: " + e.Message); }
        }

        // {apparel, food, drug, source}. A null policy is reported as null —
        // that is the pawn's real state and the public getter would have
        // destroyed it. `source` says which route answered, so a consumer can
        // tell "no policy" from "we could not look".
        public static Dictionary<string, object> Policies(Pawn pawn)
        {
            EnsurePolicyRefs();
            string apparel = null, food = null, drug = null;
            bool ok = true;
            try
            {
                if (pawn.outfits != null && apparelPolicyRef != null) apparel = apparelPolicyRef(pawn.outfits)?.label;
                else if (pawn.outfits != null) ok = false;
            }
            catch { ok = false; }
            try
            {
                if (pawn.foodRestriction != null && foodPolicyRef != null) food = foodPolicyRef(pawn.foodRestriction)?.label;
                else if (pawn.foodRestriction != null) ok = false;
            }
            catch { ok = false; }
            try
            {
                if (pawn.drugs != null && drugPolicyRef != null) drug = drugPolicyRef(pawn.drugs)?.label;
                else if (pawn.drugs != null) ok = false;
            }
            catch { ok = false; }
            return new Dictionary<string, object>
            {
                ["apparel"] = apparel,
                ["food"] = food,
                ["drug"] = drug,
                // "backing-field" = read without touching the lazy-init getter.
                // "unavailable" = the field ref did not bind; values are null
                // because we declined to ask, not because the pawn has none.
                ["source"] = ok ? "backing-field" : "unavailable",
            };
        }

        // ---------------------- classification ladder ------------------------
        // GuestStatus is only {Guest, Prisoner, Slave} and Pawn.GuestStatus
        // returns NULL for a plain visitor with no host faction (Verse/Pawn.cs),
        // so "visitor" has to be derived. Tightest test first;
        // IsFreeNonSlaveColonist is the tightest "real colonist" there is.
        // These are the values of the `class` field and of the `filter` arg —
        // a forever contract.
        public const string ClassColonist = "colonist";
        public const string ClassSlave = "slave";
        public const string ClassPrisoner = "prisoner";
        public const string ClassPrisonerOther = "prisoner-other";
        public const string ClassMech = "mech";
        public const string ClassAnimal = "animal";
        public const string ClassWildlife = "wildlife";
        public const string ClassHostile = "hostile";
        public const string ClassGuest = "guest";
        public const string ClassVisitor = "visitor";
        public const string ClassOther = "other";

        public static string Classify(Pawn pawn)
        {
            if (pawn == null) return ClassOther;
            var player = Faction.OfPlayerSilentFail;
            var guest = pawn.guest;

            if (pawn.IsFreeNonSlaveColonist) return ClassColonist;
            if (pawn.IsSlaveOfColony) return ClassSlave;
            // Pawn.IsPrisonerOfColony dereferences guest.HostFaction unguarded
            // (Class D above); do the same test without the NRE.
            if (guest != null && guest.IsPrisoner)
                return guest.HostFaction != null && guest.HostFaction.IsPlayer ? ClassPrisoner : ClassPrisonerOther;
            if (pawn.IsSlave) return ClassSlave;

            bool playerFaction = player != null && pawn.Faction == player;
            if (pawn.RaceProps != null && pawn.RaceProps.Animal)
                return playerFaction ? ClassAnimal : ClassWildlife;
            if (pawn.RaceProps != null && pawn.RaceProps.IsMechanoid && playerFaction) return ClassMech;

            bool hostile = false;
            try { hostile = player != null && pawn.HostileTo(player); }
            catch { }
            if (hostile) return ClassHostile;

            if (guest != null && guest.HostFaction != null && player != null && guest.HostFaction.IsPlayer)
                return ClassGuest;
            if (pawn.RaceProps != null && pawn.RaceProps.Humanlike) return ClassVisitor;
            return ClassOther;
        }

        // Filter word -> the set of classes it selects. The five names in the
        // spec, plus `all`, plus every class name verbatim (a published field
        // value that is not accepted as a filter is a papercut waiting to
        // happen). Returns null for an unknown word so the caller can throw a
        // bad-args naming the legal set.
        public static HashSet<string> FilterClasses(string filter)
        {
            switch (filter)
            {
                case "all": return null; // caller treats null-with-ok as "no filter"
                case "colonist": return new HashSet<string> { ClassColonist };
                case "prisoner": return new HashSet<string> { ClassPrisoner };
                case "animal": return new HashSet<string> { ClassAnimal, ClassWildlife };
                case "hostile": return new HashSet<string> { ClassHostile };
                case "guest": return new HashSet<string> { ClassGuest, ClassVisitor };
                case ClassSlave: return new HashSet<string> { ClassSlave };
                case ClassPrisonerOther: return new HashSet<string> { ClassPrisonerOther };
                case ClassMech: return new HashSet<string> { ClassMech };
                case ClassWildlife: return new HashSet<string> { ClassWildlife };
                case ClassVisitor: return new HashSet<string> { ClassVisitor };
                case ClassOther: return new HashSet<string> { ClassOther };
                default: return new HashSet<string>(); // empty = unknown word
            }
        }

        public const string FilterWords =
            "all|colonist|prisoner|prisoner-other|slave|animal|wildlife|mech|hostile|guest|visitor|other";

        // ------------------------- fog of war --------------------------------
        // DESIGN decisions log 2026-08-30: the whole player-facing surface hides
        // undiscovered ground, one rule rather than a per-verb judgement. A pawn
        // standing in fog is a pawn the colony has not found. `dev:*` is exempt;
        // nothing in the 2.2 observers is dev:*.
        public static bool Hidden(Pawn pawn, Map map)
        {
            if (pawn == null || map == null) return true;
            if (!pawn.Spawned || pawn.Map != map) return true;
            try { return pawn.Position.Fogged(map); }
            catch { return true; }
        }

        // ------------------------ small helpers ------------------------------

        // Name.ToStringShort with no rich-text markup. Pawn.LabelNoCount (and
        // therefore LabelCap) appends ", <backstory title>" wrapped in
        // .Colorize(...) — literal <color=...> tags in an LLM's context
        // (Verse/Pawn.cs). LabelShortCap does not. Neither is cached, so this is
        // a fresh string per call: compute once per pawn per pass.
        public static string Name(Pawn pawn)
        {
            try { return pawn.LabelShortCap.ToString(); }
            catch { return pawn?.ThingID ?? "?"; }
        }

        public static int Pct(float unit) => (int)Math.Round(unit * 100f);

        public static double R(float v, int digits) => Math.Round(v, digits);

        // The map every player-facing pawn verb reads. Single-map by design (v1).
        public static Map CurrentMap()
            => Find.CurrentMap ?? throw new VerbArgsException("no current map");

        // Ordered, deterministic truncation in the one shape 2.1 established:
        // sort by what the reader would most regret losing, cut, publish the
        // count dropped. Never cut in enumeration order.
        public static Dictionary<string, object> Capped(List<object> all, int cap, string order)
        {
            int total = all.Count;
            var list = total > cap ? all.GetRange(0, cap) : all;
            var d = new Dictionary<string, object>
            {
                ["list"] = list,
                ["total"] = total,
                ["more"] = total > cap ? total - cap : 0,
            };
            if (order != null) d["order"] = order;
            return d;
        }
    }
}
