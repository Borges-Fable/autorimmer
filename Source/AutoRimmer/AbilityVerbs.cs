using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AutoRimmer
{
    // ===================================================== git-bug ae84a07 ====
    // ABILITIES — the read (`abilities`) and the act (`ability-cast`).
    //
    // Before this file `Pawn_AbilityTracker` was unreached by any verb, and so
    // was `Pawn_PsychicEntropyTracker`: an ability a pawn HAS could not be
    // used, and nothing published psylink level, psyfocus or entropy, so an
    // agent could not even see that a colonist was a psycaster.
    //
    // ------------------- WHY THE READ IS ITS OWN VERB -----------------------
    // Settled in the issue's scoping pass and not re-litigated here. Four
    // reasons, all from the tree's own shape: every one of PawnSerializer's 13
    // sections is gate-free state, while every gate-EVALUATING discovery
    // surface is already a separate verb paired with an act verb
    // (`surgery-options`/`surgery-add`, `bill-options`/`bill-add`,
    // `orders`/`prioritize`, `comms-targets`/`comms-call`); a `pawn` section
    // cannot take a TARGET and half this gate chain is target-dependent;
    // `pawn` fills every section by default, so an ability section would put a
    // ~19-clause × N-abilities evaluation on every `pawn <id>` call; and the
    // observer/act safety split (B12 below) runs along the same line. `pawn`
    // gains no section — two vocabularies for one fact is what git-bug 927be4f
    // is about.
    //
    // ------------------------- THE GATE CHAIN -------------------------------
    // DESIGN §Action model: the gate lives in the widget, so the verb
    // re-implements the widget's precondition and CITES IT BY FILE + MEMBER,
    // and every distinct refusal gets its own gate name. Verified by hand
    // against the decompiled 1.6 tree; citations are FILE + MEMBER, never a
    // line offset — grep the member.
    //
    // Nineteen clauses at the gizmo layer, in the game's own short-circuit
    // order, and eleven at the target layer:
    //
    //   VISIBILITY — RimWorld/Pawn_AbilityTracker.cs GetGizmos, then
    //   RimWorld/Ability.cs GetGizmos (Psycast overrides the second). A gizmo
    //   that is never drawn cannot be clicked, so these ARE refusals:
    //     V1 not-controlled     `IsColonistPlayerControlled ||
    //                           IsColonyMechPlayerControlled ||
    //                           IsColonySubhumanPlayerControlled ||
    //                           IsColonyAnimal`, or DebugSettings.ShowDevGizmos
    //     V2 gizmo-hidden       `(Drafted || def.displayGizmoWhileUndrafted)
    //                           && ability.GizmosVisible()`, with the same
    //                           dev-gizmo fallback
    //     V3 gizmo-hidden-drafted / royalty-missing
    //                           base: `!Drafted || def.showWhenDrafted`.
    //                           Psycast overrides GetGizmos, IGNORES
    //                           def.gizmoClass and def.showWhenDrafted, and
    //                           yields only when ModLister.RoyaltyInstalled —
    //                           so for a psycast this clause is the Royalty
    //                           check instead.
    //
    //   DISABLE — RimWorld/Psycast.cs GizmoDisabled (five clauses, psycasts
    //   only) and then RimWorld/Ability.cs GizmoDisabled (eleven):
    //     B0 no-entropy-tracker  ours, not the game's: Psycast.GizmoDisabled
    //                            dereferences pawn.psychicEntropy on its FIRST
    //                            line. A psycast on a pawn with no tracker
    //                            would NRE, and the issue's acceptance asks
    //                            for a reason rather than a null deref.
    //     B1 psychic-sensitivity `PsychicSensitivity < ε`
    //     B2 psylink-level       `def.level > 0 && GetPsylinkLevel() < def.level`
    //     B3 psyfocus            `def.PsyfocusCost + queued > CurrentPsyfocus + 0.0005`
    //     B4 psyfocus-band       `def.level > psychicEntropy.MaxAbilityLevel` —
    //                            THE BANDED ONE. PsyfocusBandPercentages
    //                            {0,.25,.5,1} against MaxAbilityLevelPerPsyfocusBand
    //                            {2,4,6}: below 25% psyfocus nothing above level 2
    //                            casts even when the cost is affordable. Distinct
    //                            from B3 and constantly hit.
    //     B5 entropy             `def.EntropyGain > ε && WouldOverflowEntropy(
    //                            EntropyGain + queued)`
    //     B6 cooldown            `CanCooldown && OnCooldown &&
    //                            (!cooldownPerCharge || charges == 0)`
    //     B7 charges             `UsesCharges && charges <= 0`
    //     B8 comp-gizmo          any `comps[i].GizmoDisabled(out reason)` — ALL
    //                            AbilityComps, not just effect comps. This is
    //                            where a modded comp's own refusal surfaces.
    //     B9 can-cast            `!CanCast.Accepted`. CanCast is an
    //                            AcceptanceReport, NOT a bool, and its Reason
    //                            is frequently EMPTY (pocket map, vacuum,
    //                            planet layer, and several bare `false`s).
    //    B10 lord                `pawn.GetLord()?.AbilityAllowed(this)`
    //    B11 undrafted           `!Drafted && def.disableGizmoWhileUndrafted &&
    //                            GetCaravan() == null && !DebugSettings.ShowDevGizmos`
    //    B12 baby                `pawn.DevelopmentalStage.Baby()` — PawnSafe
    //                            Class C, see the observer split below
    //    B13 downed              `pawn.Downed`
    //    B14 deathresting        `pawn.Deathresting`
    //    B15 violence            `def.casterMustBeCapableOfViolence &&
    //                            WorkTagIsDisabled(Violent)`
    //    B16 already-queued      `!CanQueueCast`
    //
    //   TARGET — RimWorld/Verb_CastAbility.cs ValidateTarget, then
    //   RimWorld/Verb_CastPsycast.cs ValidateTarget's four extra clauses, plus
    //   the targeter's own parameter test and the one QueueCastingJob itself
    //   gates on:
    //     T0 target-params       `verb.targetParams.CanTarget(...)` —
    //                            RimWorld/TargetingParameters.cs. This is the
    //                            clause that refuses a CELL for an ability
    //                            whose canTargetLocations is false.
    //     T1 out-of-range        EffectiveRange > 0, !CanHitTarget, OutOfRange
    //     T2 cannot-hit          EffectiveRange > 0, !CanHitTarget, in range
    //                            (line of sight, a different map, psychological
    //                            invisibility, apparel)
    //     T3 cannot-reach        EffectiveRange <= 0 && !CanReach(Touch)
    //     T4 psychically-deaf    Verb_CastPsycast.IsApplicableTo ->
    //                            `!def.HasAreaOfEffect && !CanApplyPsycastTo`
    //     T5 comp-valid          any `EffectComps[i].Valid(target, false)` —
    //                            the second place a modded comp refuses
    //     T6 boss                `!all canTargetBosses && target.kindDef.isBoss`
    //     T7 caster-sensitivity  the caster's sensitivity again, at target time
    //     T8 entropy-queued      entropy + queued, at target time
    //     T9 psyfocus-queued     **FinalPsyfocusCost(target)** + queued. Not the
    //                            flat stat: any comp with
    //                            props.OverridesPsyfocusCost replaces the cost
    //                            PER TARGET, so B3 and T9 can disagree and both
    //                            numbers are published.
    //    T10 cannot-apply        `Ability.CanApplyOn(target)` — the clause
    //                            QueueCastingJob ITSELF gates on, and the one
    //                            that fails SILENTLY. See EffectComps below.
    //
    // Verb_CastAbility.ValidateTarget OVERRIDES Verb.ValidateTarget and never
    // calls base, so the base's extra-faction / innocent-animal /
    // venerated-animal clauses do NOT run for an ability. They are not
    // reproduced here for the same reason.
    //
    // ------------- WHY `Ability.CanCast` IS NOT THE GATE ---------------------
    // It has the right name and it is a trap. RimWorld/Psycast.cs overrides it
    // with two MUTUALLY EXCLUSIVE branches:
    //     if (def.EntropyGain > ε) { psylink level, then WouldOverflowEntropy }
    //     if (def.PsyfocusCost > CurrentPsyfocus + 0.0005f) return false;
    // — an ability with ANY entropy gain never has its psyfocus checked in
    // CanCast at all. A verb that used CanCast as its gate would let a
    // 30%-psyfocus psycast fire on an empty tank. Only GizmoDisabled and
    // Verb_CastPsycast.ValidateTarget check both, which is why the chain above
    // is reproduced clause by clause and CanCast appears only as B9.
    //
    // ------------ THE QUEUE TERMS, AND WHY THEY ARE NOT OPTIONAL ------------
    // B3, B5, T8 and T9 all add RimWorld/PsycastUtility.cs
    // TotalPsyfocusCostOfQueuedPsycasts / TotalEntropyFromQueuedPsycasts, which
    // walk pawn.jobs.curJob and pawn.jobs.jobQueue. Publishing "psyfocus 0.6,
    // cost 0.3, castable" with two casts already queued is a false yes, and
    // vanilla expects players to stack casts — that accounting exists for
    // exactly this. The envelope publishes both totals beside the numbers.
    //
    // ------------------ EffectComps, AND THE SILENT SKIP --------------------
    // RimWorld/Ability.cs CanApplyOn(LocalTargetInfo) iterates the PRIVATE
    // `effectComps` FIELD, not the EffectComps property:
    //     if (effectComps != null) foreach (...) if (!c.CanApplyOn(target, null)) return false;
    // — so on a freshly loaded save, where nothing has touched the property
    // yet, it skips EVERY comp SILENTLY and returns true. QueueCastingJob gates
    // on that method. Meanwhile Verb_CastAbility.ValidateTarget gates on
    // `Valid` and never calls CanApplyOn at all, so the two are NEVER both
    // called: a comp that overrides only Valid is invisible to QueueCastingJob,
    // and a comp that overrides only CanApplyOn is invisible to the targeter.
    // Both verbs here therefore TOUCH `ability.EffectComps` once before
    // evaluating anything, and check CanApplyOn (T10) and Valid (T5)
    // SEPARATELY under their own gate names. Touching the property is the same
    // thing drawing the ability's tooltip does (Ability.Tooltip reads it); the
    // field is a memo over `comps`, not scribed state.
    //
    // ------------------ OBSERVER SAFETY: WHERE THE LINE IS ------------------
    // PawnActs' header rule stands — a player verb is a reproduction of a
    // click, so where the WIDGET calls a lazy-init getter the ACT verb calls it
    // too. `abilities` is not a click, and two things follow:
    //
    //   * `Pawn_AbilityTracker.AllAbilitiesForReading` is a rebuild-on-read
    //     SHARED list that the Anomaly whitelist branch replaces outright
    //     (PawnSafe Class E). Both verbs read it through PawnSafe.Abilities,
    //     which takes ONE snapshot into our own List and never holds the
    //     game's container. `AICastableAbilities` returns the shared
    //     `tmpAbilities` field and is never touched.
    //   * B12 is `pawn.DevelopmentalStage.Baby()`, which routes through
    //     Pawn_AgeTracker.CurLifeStageIndex — on a stale cache that RENAMES THE
    //     PAWN and runs AddAndRemoveDynamicComponents (PawnSafe Class C). The
    //     READ evaluates B12 through PawnSafe.Baby, which reads the cached
    //     index through a field ref and re-derives it without the write when it
    //     is stale, and publishes which route answered. `ability-cast` calls
    //     the game's own GizmoDisabled as its final authority and therefore
    //     touches the real getter — which is exactly what the player's click
    //     does.
    //
    // The read calls no Activate, no PreActivate and no comp Apply: all three
    // roll on the shared Rand stream (`def.cooldownTicksRange.RandomInRange` in
    // PreActivate and CooldownTick, and comp effects roll their own), which is
    // fine for a click and disqualifying for an observation.
    //
    // ------------------ THE ACT: WHAT IT CALLS AND WHY ----------------------
    // `Ability.QueueCastingJob(LocalTargetInfo, LocalTargetInfo)` returns VOID
    // and fails silently on `!CanQueueCast || !CanApplyOn(target)`. Worse, its
    // body wraps the take in ShowCastingConfirmationIfNeeded, which for a def
    // with `confirmationDialogText` (or a comp with a ConfirmationDialog) does
    // `Find.WindowStack.Add(window)` and takes NO job — a force-pausing modal
    // that wedges every subsequent `advance` at 0 ticks, which is the hard rule
    // in PawnActs' header. So the callback body is REPRODUCED instead:
    //
    //     pawn.jobs.TryTakeOrderedJob(ability.GetJob(target, dest), JobTag.Misc)
    //
    // `GetJob` is public and builds the same Job the game would
    // (`def.jobDef ?? JobDefOf.CastAbilityOnThing`, `verbToUse = verb`,
    // `ability = this`), so this is the smallest reproduction that has the
    // missing parameters — the confirmation, and `requestQueueing`, which
    // TryTakeOrderedJob otherwise only ever gets from live keyboard state
    // (git-bug bc2250b). The confirmation is PRE-CHECKED through the public
    // `Ability.ConfirmationDialog(target, action)`, which CONSTRUCTS the window
    // without showing it, and its text is published as `confirmation`. Nothing
    // here calls `Ability.Activate` or `verb.TryStartCastOn` directly: both
    // skip the job, the warmup and Ability.WarmupTick's continuous
    // re-validation, and the mod being tested on this bench treats the cast JOB
    // as its multiplayer sync boundary.
    //
    // The ONE exception is `verbProps.nonInterruptingSelfCast`, where the game
    // itself takes no job — QueueCastingJob's second line is
    // `verb.TryStartCastOn(verb.Caster); return;`, BEFORE the confirmation. For
    // that shape the verb calls `ability.QueueCastingJob` itself: it is the
    // game's own method, that branch provably cannot open a window, and
    // reproducing it would mean going around the game rather than through it.
    //
    // And the result is READ BACK rather than reported from the call: after the
    // take, `pawn.jobs.curJob.ability == ability` is the evidence, and
    // PawnActs.OrderEffect says whether the order started, queued or vanished.
    //
    // ------------------------- SOFT DEPENDENCY ------------------------------
    // Nothing here names a mod, a def or a comp class. Every field is read off
    // AbilityDef / Ability / CompAbilityEffect, so a modded ability is
    // evaluated by the same nineteen clauses as a vanilla one and a modded
    // comp's own refusal SURFACES (B8, T5, T10) instead of being special-cased.
    // Where a modded subclass overrides GizmoDisabled or CanCast beyond
    // Ability/Psycast, the read cannot see the override without casting and
    // SAYS SO on the row (`overrides`), rather than publishing a castable:true
    // it cannot stand behind.
    internal static partial class PawnActs
    {
        // ---------------------------- gate names -----------------------------
        // One vocabulary for the read and the act: a `castable:false` from
        // `abilities` and a rejection from `ability-cast` name the same gate,
        // so the pair joins without a translation table.
        private const string AbGateNotKnown = "not-known";
        private const string AbGateNoVerb = "no-verb";
        private const string AbGateNotControlled = "not-controlled";
        private const string AbGateGizmoHidden = "gizmo-hidden";
        private const string AbGateGizmoHiddenDrafted = "gizmo-hidden-drafted";
        private const string AbGateRoyaltyMissing = "royalty-missing";
        private const string AbGateNoEntropyTracker = "no-entropy-tracker";
        private const string AbGateSensitivity = "psychic-sensitivity";
        private const string AbGatePsylink = "psylink-level";
        private const string AbGatePsyfocus = "psyfocus";
        private const string AbGatePsyfocusBand = "psyfocus-band";
        private const string AbGateEntropy = "entropy";
        private const string AbGateCooldown = "cooldown";
        private const string AbGateCharges = "charges";
        private const string AbGateCompGizmo = "comp-gizmo";
        private const string AbGateCanCast = "can-cast";
        private const string AbGateLord = "lord";
        private const string AbGateUndrafted = "undrafted";
        private const string AbGateBaby = "baby";
        private const string AbGateDowned = "downed";
        private const string AbGateDeathresting = "deathresting";
        private const string AbGateViolence = "violence";
        private const string AbGateQueued = "already-queued";
        private const string AbGateGizmoOther = "gizmo-disabled";

        private const string AbGateTargetParams = "target-params";
        private const string AbGateOutOfRange = "out-of-range";
        private const string AbGateCannotHit = "cannot-hit";
        private const string AbGateCannotReach = "cannot-reach";
        private const string AbGateDeaf = "psychically-deaf";
        private const string AbGateCompValid = "comp-valid";
        private const string AbGateBoss = "boss";
        private const string AbGateCasterSensitivity = "caster-sensitivity";
        private const string AbGateEntropyQueued = "entropy-queued";
        private const string AbGatePsyfocusQueued = "psyfocus-queued";
        private const string AbGateCannotApply = "cannot-apply";

        private const string AbGateTargetRequired = "target-required";
        private const string AbGateDestRequired = "dest-required";
        private const string AbGateWorldTarget = "world-target-unsupported";
        private const string AbGateSelfCastQueue = "self-cast-cannot-queue";
        private const string AbGateRefused = "refused";

        // The tolerance every psyfocus comparison in the game uses
        // (RimWorld/Pawn_PsychicEntropyTracker.cs PsyfocusCostTolerance).
        private const float AbPsyfocusTolerance = 0.0005f;

        // =====================================================================
        // abilities {pawn|pawns, target?, cap?}          READ-ONLY
        //
        // What each caster has, what the caster's psychic budget is, and — for
        // every ability — whether the game would let it be cast right now, with
        // the gate that refuses it and the game's own words for why. With
        // `target`, the eleven target-layer clauses are evaluated too, which
        // makes this the dry run for `ability-cast`.
        // =====================================================================
        [Verb("abilities")]
        public static object Abilities(VerbContext ctx)
        {
            var map = Map();
            ctx.Args.NearMiss("target", "at", "on", "targ");
            var pawns = PawnList(map, ctx.Args);
            int cap = ctx.Args.Int("cap", 60);
            if (cap < 1 || cap > 300) throw new VerbArgsException("cap must be 1..300");
            var target = AbResolveTarget(map, ctx.Args, "target", required: false);

            var casters = new List<object>();
            int abilityCount = 0, castableCount = 0;
            foreach (var p in pawns)
            {
                bool listOk;
                var list = PawnSafe.Abilities(p, out listOk);
                var rows = new List<object>();
                int castableHere = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var row = AbRow(p, list[i], target);
                    abilityCount++;
                    if (row.TryGetValue("castable", out var c) && c is bool && (bool)c)
                    {
                        castableHere++;
                        castableCount++;
                    }
                    if (rows.Count < cap) rows.Add(row);
                }
                casters.Add(new Dictionary<string, object>
                {
                    ["pawn"] = p.thingIDNumber,
                    ["name"] = PawnSafe.Name(p),
                    ["drafted"] = SafeObj(() => (object)p.Drafted),
                    ["psychic"] = AbPsychic(p),
                    ["abilities"] = rows,
                    ["total"] = list.Count,
                    ["more"] = Math.Max(0, list.Count - rows.Count),
                    ["castable"] = castableHere,
                    // "snapshot" = one read of AllAbilitiesForReading, copied.
                    // "unavailable" = the getter threw; the list is EMPTY
                    // because we could not ask, not because the pawn has none.
                    ["source"] = listOk ? "snapshot" : "unavailable",
                });
            }

            return new Dictionary<string, object>
            {
                ["casters"] = casters,
                ["target"] = AbTargetBlock(target),
                ["counts"] = new Dictionary<string, object>
                {
                    ["casters"] = casters.Count,
                    ["abilities"] = abilityCount,
                    ["castable"] = castableCount,
                },
                ["gates"] = AbGateGlossary(),
                ["action"] = NoStamp(),
                ["note"] = "`castable` is the GIZMO layer only — the nineteen clauses of "
                    + "Pawn_AbilityTracker.GetGizmos, Ability.GetGizmos, Psycast.GizmoDisabled and "
                    + "Ability.GizmoDisabled, reproduced in the game's own order. With `target` the "
                    + "eleven target-layer clauses of Verb_CastAbility.ValidateTarget, "
                    + "Verb_CastPsycast.ValidateTarget, TargetingParameters.CanTarget and "
                    + "Ability.CanApplyOn are evaluated as well and answer `target_castable`; "
                    + "WITHOUT it, a castable:true says only that the button would be clickable, not "
                    + "that any particular target would be accepted. `psyfocus_cost` is the flat stat "
                    + "the gizmo uses and `final_psyfocus_cost` is Ability.FinalPsyfocusCost(target), "
                    + "which a comp may replace per target — when they differ, the target-layer "
                    + "clause uses the second. This verb never calls Activate, PreActivate or a comp "
                    + "Apply (all three roll on the shared Rand stream), never touches a Gizmo, and "
                    + "reads the ability list through PawnSafe.Abilities because "
                    + "AllAbilitiesForReading hands back the game's live list.",
            };
        }

        // =====================================================================
        // ability-cast {pawn|pawns, ability, target?, dest?, queue?}
        //
        // Queues the cast through the game's own job — Ability.GetJob +
        // Pawn_JobTracker.TryTakeOrderedJob — never by invoking the effect comp
        // directly. Every clause `abilities` evaluates is re-evaluated here, and
        // then Ability.GizmoDisabled is called as the FINAL AUTHORITY so a
        // modded override this file cannot see still refuses.
        // =====================================================================
        [Verb("ability-cast")]
        public static object AbilityCast(VerbContext ctx)
        {
            const string V = "ability-cast";
            var map = Map();
            ctx.Args.NearMiss("target", "at", "on", "targ");
            ctx.Args.NearMiss("ability", "abilityDef", "def", "psycast");
            var pawns = PawnList(map, ctx.Args);
            var def = Dev.Named<AbilityDef>(ctx.Args.StrReq("ability"), "ability");
            var target = AbResolveTarget(map, ctx.Args, "target", required: false);
            var dest = AbResolveTarget(map, ctx.Args, "dest", required: false);
            bool queued = ctx.Args.Bool("queue", false);

            var outcome = new Outcome();
            var ids = new List<object>();
            Dictionary<string, object> confirmation = null;

            foreach (var p in pawns)
            {
                bool listOk;
                var list = PawnSafe.Abilities(p, out listOk);
                Ability ability = null;
                for (int i = 0; i < list.Count; i++)
                    if (list[i].def == def) { ability = list[i]; break; }
                if (ability == null)
                {
                    outcome.No(p, AbGateNotKnown,
                        listOk
                            ? "this pawn does not have '" + def.defName + "'. Pawn_AbilityTracker"
                              + ".AllAbilitiesForReading holds innate, hediff, equipment, apparel, "
                              + "mutant, royalty and ideo-role abilities and none of them is this one; "
                              + "most psycasts are learned by USING a psytrainer "
                              + "(CompUseEffect_GainAbility), and a psylink comes from PsychicAmplifier."
                            : "the ability list could not be read for this pawn "
                              + "(Pawn_AbilityTracker.AllAbilitiesForReading threw)");
                    continue;
                }

                // Addition B: fill the PRIVATE effectComps field before anything
                // consults Ability.CanApplyOn, which iterates that field and
                // silently skips every comp while it is null.
                int compCount = AbTouchEffectComps(ability);

                var verb = AbVerbOf(ability);
                if (verb == null)
                {
                    outcome.No(p, AbGateNoVerb,
                        "this ability has no usable Verb. RimWorld/Ability.cs `verb` reads the PRIVATE "
                        + "verbTracker field rather than the lazy VerbTracker property, so an Ability "
                        + "that never ran Initialize() throws here rather than building one.");
                    continue;
                }

                // ------- the gizmo layer, reproduced, then the authority ------
                string gate, reason, babySource;
                if (AbGizmoRefusal(p, ability, out gate, out reason, out babySource))
                {
                    outcome.No(p, gate, reason, AbRefusalDetail(ability, target, babySource));
                    continue;
                }
                // RimWorld/Command_Ability.cs Disabled -> DisabledCheck ->
                // ability.GizmoDisabled(out reason): the gizmo adds nothing the
                // Ability does not already give, so this is called directly and
                // no Gizmo is ever constructed. It is the AUTHORITY: a modded
                // Ability subclass overriding GizmoDisabled refuses here even
                // though the reproduction above could not see the clause.
                string authorityReason = null;
                bool authorityRefused = false;
                try { authorityRefused = ability.GizmoDisabled(out authorityReason); }
                catch (Exception e)
                {
                    authorityRefused = true;
                    authorityReason = e.GetType().Name + ": " + e.Message;
                }
                if (authorityRefused)
                {
                    outcome.No(p, AbGateGizmoOther,
                        string.IsNullOrEmpty(authorityReason)
                            ? "Ability.GizmoDisabled refused with an EMPTY reason — several of its "
                              + "clauses return a bare `false` AcceptanceReport. The clause is not one "
                              + "of the nineteen this verb reproduces, so it belongs to an overriding "
                              + "subclass or comp on " + ability.GetType().Name + "."
                            : authorityReason,
                        AbRefusalDetail(ability, target, babySource));
                    continue;
                }

                // ----------------------- the target --------------------------
                if (def.targetWorldCell)
                {
                    outcome.No(p, AbGateWorldTarget,
                        "'" + def.defName + "' targets a WORLD TILE (AbilityDef.targetWorldCell). The "
                        + "game's path for it is Ability.QueueCastingJob(GlobalTargetInfo) and "
                        + "JobDefOf.CastAbilityOnWorldTile; this verb takes a map-local target only. "
                        + "Casting it is a real capability gap and is reported rather than faked.");
                    continue;
                }
                if (!target.Given && def.targetRequired)
                {
                    outcome.No(p, AbGateTargetRequired,
                        "'" + def.defName + "' requires a target (AbilityDef.targetRequired). Pass "
                        + "`target` as a pawn id, \"pawn:<id>\", \"thing:<id>\", \"self\", \"x,z\", "
                        + "[x,z] or a landmark name.");
                    continue;
                }
                var local = target.Given ? target.Local(p) : new LocalTargetInfo(p);
                var destLocal = dest.Given ? dest.Local(p) : LocalTargetInfo.Invalid;
                if (AbNeedsSelectedDest(ability) && !dest.Given)
                {
                    outcome.No(p, AbGateDestRequired,
                        "'" + def.defName + "' has a CompAbilityEffect_WithDest whose "
                        + "props.destination is Selected, so RimWorld/Verb_CastAbility.cs "
                        + "OrderForceTarget routes it through comp.SetTarget for a SECOND click "
                        + "instead of casting. Pass `dest` (same forms as `target`).");
                    continue;
                }

                if (AbTargetRefusal(p, ability, verb, local, destLocal, out gate, out reason))
                {
                    outcome.No(p, gate, reason, AbRefusalDetail(ability, target, babySource));
                    continue;
                }

                // -------------------- the confirmation -----------------------
                // Pre-checked, never shown. Ability.ConfirmationDialog is public
                // and only CONSTRUCTS the Window; ShowCastingConfirmationIfNeeded
                // is what would Find.WindowStack.Add it, and a force-pausing
                // window halts every subsequent `advance` at 0 ticks with
                // reason:"dialog".
                var conf = AbConfirmation(ability, local);
                if (conf != null && confirmation == null) confirmation = conf;

                // ----------------------- take the job ------------------------
                bool selfCast = false;
                try { selfCast = verb.verbProps != null && verb.verbProps.nonInterruptingSelfCast; }
                catch { }
                if (selfCast)
                {
                    if (queued)
                    {
                        outcome.No(p, AbGateSelfCastQueue,
                            "'" + def.defName + "' is a nonInterruptingSelfCast verb. RimWorld/"
                            + "Ability.cs QueueCastingJob takes NO job for that shape — it calls "
                            + "verb.TryStartCastOn(verb.Caster) and returns — so there is nothing to "
                            + "queue. Re-issue without `queue`.");
                        continue;
                    }
                    int beforeSelf = -1;
                    try { beforeSelf = p.jobs != null && p.jobs.curJob != null ? p.jobs.curJob.loadID : -1; } catch { }
                    try { ability.QueueCastingJob(local, destLocal); }
                    catch (Exception e)
                    {
                        outcome.No(p, AbGateRefused,
                            "Ability.QueueCastingJob threw: " + e.GetType().Name + ": " + e.Message);
                        continue;
                    }
                    var selfLine = JobLine(p);
                    selfLine["ability"] = def.defName;
                    selfLine["self_cast"] = true;
                    selfLine["warming_up"] = SafeObj(() => (object)verb.WarmingUp);
                    selfLine["casting"] = SafeObj(() => (object)ability.Casting);
                    selfLine["job_id_before"] = beforeSelf;
                    selfLine["order_effect"] = "self-cast";
                    selfLine["order_effect_note"] =
                        "verbProps.nonInterruptingSelfCast: the game's own QueueCastingJob calls "
                        + "verb.TryStartCastOn(verb.Caster) and takes no Job at all, so there is no "
                        + "job id and no queue entry to read back. `warming_up` and `casting` are the "
                        + "evidence. This is the one branch where the game's method is called rather "
                        + "than its callback reproduced, and it is also the one branch that provably "
                        + "cannot raise a confirmation window (it returns before the check).";
                    ids.Add(p.thingIDNumber);
                    outcome.Ok(p, selfLine);
                    continue;
                }

                Job job;
                try { job = ability.GetJob(local, destLocal); }
                catch (Exception e)
                {
                    outcome.No(p, AbGateRefused,
                        "Ability.GetJob threw: " + e.GetType().Name + ": " + e.Message);
                    continue;
                }
                if (job == null)
                {
                    outcome.No(p, AbGateRefused, "Ability.GetJob returned no job");
                    continue;
                }
                // git-bug 4087644. TryTakeOrderedJob returns TRUE having done
                // nothing when the pawn already runs an equivalent job, and with
                // requestQueueing it enqueues NOTHING and leaves no trace.
                if (AlreadyDoing(p, job))
                {
                    var already = AlreadyLine(p, queued);
                    already["ability"] = def.defName;
                    outcome.No(p, GateAlready, AlreadyWhy(queued), already);
                    continue;
                }
                Dictionary<string, object> eff;
                if (!TakeOrder(p, job, JobTag.Misc, queued, out eff))
                {
                    outcome.No(p, AbGateRefused,
                        "Pawn_JobTracker.TryTakeOrderedJob refused the cast job", JobLine(p));
                    continue;
                }

                // Read back what happened. QueueCastingJob is void and gives no
                // success signal at all, so the evidence is the pawn's own job:
                // GetJob stamps `job.ability = this`, so curJob.ability being
                // this ability is proof the cast is the running job.
                var line = JobLine(p);
                line["ability"] = def.defName;
                line["target"] = AbTargetEcho(local);
                line["comps"] = compCount;
                bool isCastJob = false;
                try { isCastJob = p.jobs != null && p.jobs.curJob != null && p.jobs.curJob.ability == ability; }
                catch { }
                line["ability_job_running"] = isCastJob;
                line["warmup_ticks"] = SafeObj(() =>
                    def.verbProperties != null ? (object)def.verbProperties.warmupTime : null);
                if (conf != null) line["confirmation"] = conf;
                Merge(line, eff);
                ids.Add(p.thingIDNumber);
                outcome.Ok(p, line);
            }

            long seq = ActOn(outcome, V, "cast", def.defName, new Dictionary<string, object>
            {
                ["ids"] = ids,
                ["ability"] = def.defName,
                ["target"] = target.Describe,
                ["queue"] = queued,
            });

            var extra = new Dictionary<string, object>
            {
                ["ability"] = def.defName,
                ["label"] = def.label,
                ["level"] = def.level,
                ["is_psycast"] = SafeObj(() => (object)def.IsPsycast),
                ["target"] = AbTargetBlock(target),
                ["queue"] = queued,
                ["note"] = "the cast is QUEUED AS A JOB, never applied directly: "
                    + "Ability.GetJob(target, dest) + Pawn_JobTracker.TryTakeOrderedJob, which is the "
                    + "body of the callback Ability.QueueCastingJob passes to "
                    + "ShowCastingConfirmationIfNeeded. It is reproduced rather than called so a "
                    + "confirmation window is never raised (a force-pausing window halts every "
                    + "`advance` at 0 ticks) and so `queue` can be honoured at all — "
                    + "TryTakeOrderedJob otherwise reads requestQueueing from live keyboard state. "
                    + "The effect lands when the WARMUP ends, not now: read `order_effect` and "
                    + "`ability_job_running`, then advance past `warmup_ticks` seconds and re-read "
                    + "the target. Ability.WarmupTick re-checks CanApplyOn every tick and aborts the "
                    + "cast if it stops holding.",
            };
            if (confirmation != null) extra["confirmation"] = confirmation;
            return outcome.Result(V, seq, extra);
        }

        // =====================================================================
        //                          THE GIZMO LAYER
        // =====================================================================

        // True when the ability could not be cast at all right now, target
        // aside. Clause order is the game's, so the FIRST reason the player
        // would be shown is the one reported.
        private static bool AbGizmoRefusal(Pawn pawn, Ability a,
            out string gate, out string reason, out string babySource)
        {
            gate = null;
            reason = null;
            babySource = null;
            var def = a.def;

            // ---- V1/V2/V3: RimWorld/Pawn_AbilityTracker.cs GetGizmos --------
            bool dev = false;
            try { dev = DebugSettings.ShowDevGizmos; } catch { }
            bool visiblePrimary = false;
            try
            {
                visiblePrimary = pawn.IsColonistPlayerControlled || pawn.IsColonyMechPlayerControlled
                    || pawn.IsColonySubhumanPlayerControlled || pawn.IsColonyAnimal;
            }
            catch { }
            if (!visiblePrimary && !dev)
            {
                gate = AbGateNotControlled;
                reason = "no ability gizmo is drawn for this pawn at all. Pawn_AbilityTracker"
                    + ".GetGizmos yields nothing unless the pawn is IsColonistPlayerControlled, "
                    + "IsColonyMechPlayerControlled, IsColonySubhumanPlayerControlled or "
                    + "IsColonyAnimal (a mental state, a host faction or an unoverseen mech all "
                    + "clear the first of those).";
                return true;
            }
            bool visibleSecondary = false;
            try { visibleSecondary = (pawn.Drafted || def.displayGizmoWhileUndrafted) && a.GizmosVisible(); }
            catch { }
            bool devFallback = false;
            try { devFallback = !pawn.IsColonistPlayerControlled && !pawn.IsColonyMechPlayerControlled && dev; }
            catch { }
            if (!visibleSecondary && !devFallback)
            {
                gate = AbGateGizmoHidden;
                reason = "the gizmo is not drawn. Pawn_AbilityTracker.GetGizmos needs "
                    + "`(pawn.Drafted || def.displayGizmoWhileUndrafted) && ability.GizmosVisible()`; "
                    + "here drafted=" + AbBool(() => pawn.Drafted)
                    + ", displayGizmoWhileUndrafted=" + def.displayGizmoWhileUndrafted
                    + ", GizmosVisible=" + AbBool(() => a.GizmosVisible())
                    + " (GizmosVisible is false when any effect comp sets ShouldHideGizmo).";
                return true;
            }
            if (a is Psycast)
            {
                bool royalty = true;
                try { royalty = ModLister.RoyaltyInstalled; } catch { }
                if (!royalty)
                {
                    gate = AbGateRoyaltyMissing;
                    reason = "Psycast.GetGizmos yields nothing without Royalty "
                        + "(ModLister.RoyaltyInstalled).";
                    return true;
                }
            }
            else
            {
                bool drafted = false;
                try { drafted = pawn.Drafted; } catch { }
                if (drafted && !def.showWhenDrafted)
                {
                    gate = AbGateGizmoHiddenDrafted;
                    reason = "Ability.GetGizmos withholds the gizmo from a DRAFTED pawn when "
                        + "def.showWhenDrafted is false (`if (!pawn.Drafted || def.showWhenDrafted)`).";
                    return true;
                }
            }

            // ---- B0..B5: RimWorld/Psycast.cs GizmoDisabled ------------------
            if (a is Psycast)
            {
                var ent = pawn.psychicEntropy;
                if (ent == null)
                {
                    gate = AbGateNoEntropyTracker;
                    reason = "this pawn has no Pawn_PsychicEntropyTracker, and "
                        + "Psycast.GizmoDisabled dereferences it on its first line "
                        + "(`pawn.psychicEntropy.PsychicSensitivity`). The tracker is created for "
                        + "humanlikes with Royalty active; without it no psycast is castable.";
                    return true;
                }
                float sensitivity = 0f;
                try { sensitivity = ent.PsychicSensitivity; } catch { }
                if (sensitivity < float.Epsilon)
                {
                    gate = AbGateSensitivity;
                    reason = AbTr("CommandPsycastZeroPsychicSensitivity",
                        () => "CommandPsycastZeroPsychicSensitivity".Translate().ToString(),
                        "the caster's psychic sensitivity is zero");
                    return true;
                }
                float queuedFocus = AbQueuedPsyfocus(pawn);
                int psylink = 0;
                try { psylink = pawn.GetPsylinkLevel(); } catch { }
                if (def.level > 0 && psylink < def.level)
                {
                    gate = AbGatePsylink;
                    reason = AbTr("CommandPsycastHigherLevelPsylinkRequired",
                        () => "CommandPsycastHigherLevelPsylinkRequired".Translate(def.level).ToString(),
                        "needs psylink level " + def.level + "; this caster has " + psylink);
                    return true;
                }
                float focus = 0f;
                try { focus = ent.CurrentPsyfocus; } catch { }
                if (def.PsyfocusCost + queuedFocus > focus + AbPsyfocusTolerance)
                {
                    gate = AbGatePsyfocus;
                    reason = AbTr("CommandPsycastNotEnoughPsyfocus",
                        () => "CommandPsycastNotEnoughPsyfocus".Translate(
                            def.PsyfocusCostPercent,
                            (focus - queuedFocus).ToStringPercent("0.#"),
                            def.label.Named("PSYCASTNAME"),
                            pawn.Named("CASTERNAME")).ToString(),
                        "needs " + def.PsyfocusCost.ToString("0.###") + " psyfocus (plus "
                            + queuedFocus.ToString("0.###") + " already committed to queued casts); "
                            + "the caster has " + focus.ToString("0.###"));
                    return true;
                }
                int maxLevel = 6;
                try { maxLevel = ent.MaxAbilityLevel; } catch { }
                if (def.level > maxLevel)
                {
                    gate = AbGatePsyfocusBand;
                    reason = AbTr("CommandPsycastLowPsyfocus",
                        () => "CommandPsycastLowPsyfocus".Translate(
                            Pawn_PsychicEntropyTracker.PsyfocusBandPercentages[def.RequiredPsyfocusBand]
                                .ToStringPercent()).ToString(),
                        "psyfocus is in band " + AbBandOf(ent) + ", which caps casting at level "
                            + maxLevel + "; this is level " + def.level)
                        + " [BANDED: psyfocus is banded {0, 25%, 50%, 100%} against max ability level "
                        + "{2, 4, 6} (Pawn_PsychicEntropyTracker.PsyfocusBandPercentages / "
                        + "MaxAbilityLevelPerPsyfocusBand), so this refuses even when the cost is "
                        + "affordable. Raise psyfocus to at least "
                        + Pawn_PsychicEntropyTracker.PsyfocusBandPercentages[def.RequiredPsyfocusBand]
                            .ToString("0.##") + " by meditating.]";
                    return true;
                }
                float queuedEntropy = AbQueuedEntropy(pawn);
                bool overflow = false;
                try { overflow = def.EntropyGain > float.Epsilon
                        && ent.WouldOverflowEntropy(def.EntropyGain + queuedEntropy); }
                catch { }
                if (overflow)
                {
                    gate = AbGateEntropy;
                    reason = AbTr("CommandPsycastWouldExceedEntropy",
                        () => "CommandPsycastWouldExceedEntropy".Translate(def.label).ToString(),
                        "would overflow neural heat: " + def.EntropyGain.ToString("0.#") + " gain plus "
                            + queuedEntropy.ToString("0.#") + " already queued, on top of "
                            + AbFloat(() => ent.EntropyValue) + " of " + AbFloat(() => ent.MaxEntropy));
                    return true;
                }
            }

            // ---- B6..B16: RimWorld/Ability.cs GizmoDisabled -----------------
            bool onCooldown = false;
            try { onCooldown = a.CanCooldown && a.OnCooldown && (!def.cooldownPerCharge || a.RemainingCharges == 0); }
            catch { }
            if (onCooldown)
            {
                gate = AbGateCooldown;
                int left = 0;
                try { left = a.CooldownTicksRemaining; } catch { }
                reason = AbTr("AbilityOnCooldown",
                    () => "AbilityOnCooldown".Translate(left.ToStringTicksToPeriod()).Resolve(),
                    "on cooldown for another " + left + " ticks");
                return true;
            }
            bool noCharges = false;
            try { noCharges = a.UsesCharges && a.RemainingCharges <= 0; } catch { }
            if (noCharges)
            {
                gate = AbGateCharges;
                reason = AbTr("AbilityNoCharges", () => "AbilityNoCharges".Translate().ToString(),
                    "no charges left");
                return true;
            }
            // ALL AbilityComps, not just effect comps — this is where a modded
            // comp's own refusal surfaces, and it is the first of the three
            // places one can (B8, T5, T10).
            if (a.comps != null)
            {
                for (int i = 0; i < a.comps.Count; i++)
                {
                    string compReason = null;
                    bool refused = false;
                    try { refused = a.comps[i].GizmoDisabled(out compReason); }
                    catch (Exception e)
                    {
                        refused = true;
                        compReason = a.comps[i].GetType().Name + " threw: "
                            + e.GetType().Name + ": " + e.Message;
                    }
                    if (refused)
                    {
                        gate = AbGateCompGizmo;
                        reason = (string.IsNullOrEmpty(compReason)
                            ? "refused by comp " + a.comps[i].GetType().Name + " with no reason given"
                            : compReason) + " [AbilityComp.GizmoDisabled, comp "
                            + a.comps[i].GetType().Name + "]";
                        return true;
                    }
                }
            }
            AcceptanceReport canCast;
            try { canCast = a.CanCast; }
            catch (Exception e)
            {
                gate = AbGateCanCast;
                reason = "Ability.CanCast threw: " + e.GetType().Name + ": " + e.Message;
                return true;
            }
            if (!canCast.Accepted)
            {
                gate = AbGateCanCast;
                reason = string.IsNullOrEmpty(canCast.Reason)
                    ? "Ability.CanCast returned a bare `false` with no reason. Its reasonless "
                      + "branches are: a comp's CanCast is false; a cooldown with no charge; charges "
                      + "exhausted; and for a psycast, psylink level or a psyfocus/entropy shortfall "
                      + "on whichever of the two branches its EntropyGain selects."
                    : canCast.Reason;
                return true;
            }
            string lordReason = null;
            bool lordRefused = false;
            try
            {
                var lord = pawn.GetLord();
                if (lord != null)
                {
                    var rep = lord.AbilityAllowed(a);
                    if (!rep) { lordRefused = true; lordReason = rep.Reason; }
                }
            }
            catch { }
            if (lordRefused)
            {
                gate = AbGateLord;
                reason = string.IsNullOrEmpty(lordReason)
                    ? "the Lord controlling this pawn does not allow this ability "
                      + "(Lord.AbilityAllowed)"
                    : lordReason;
                return true;
            }
            bool undrafted = false;
            try
            {
                undrafted = !pawn.Drafted && def.disableGizmoWhileUndrafted
                    && pawn.GetCaravan() == null && !dev;
            }
            catch { }
            if (undrafted)
            {
                gate = AbGateUndrafted;
                reason = AbTr("AbilityDisabledUndrafted",
                    () => "AbilityDisabledUndrafted".Translate().ToString(),
                    "must be drafted")
                    + " [AbilityDef.disableGizmoWhileUndrafted DEFAULTS TO TRUE and is independent "
                    + "of displayGizmoWhileUndrafted: a def that sets only the second SHOWS the "
                    + "gizmo undrafted and then disables it. Draft the caster.]";
                return true;
            }
            // PawnSafe Class C: the observer route. Pawn.DevelopmentalStage goes
            // through Pawn_AgeTracker.CurLifeStageIndex, which on a stale cache
            // renames the pawn and adds/removes components.
            bool baby = PawnSafe.Baby(pawn, out babySource);
            if (baby)
            {
                gate = AbGateBaby;
                reason = AbTr("IsIncapped",
                    () => "IsIncapped".Translate(pawn.LabelShort, pawn).ToString(),
                    "babies cannot use abilities")
                    + " [evaluated through PawnSafe.Baby (" + babySource + "), not "
                    + "Pawn.DevelopmentalStage, which writes]";
                return true;
            }
            bool downed = false;
            try { downed = pawn.Downed; } catch { }
            if (downed)
            {
                gate = AbGateDowned;
                reason = AbTr("CommandDisabledUnconscious",
                    () => "CommandDisabledUnconscious".TranslateWithBackup("CommandCallRoyalAidUnconscious")
                        .Formatted(pawn).ToString(),
                    "the caster is downed");
                return true;
            }
            bool deathresting = false;
            try { deathresting = pawn.Deathresting; } catch { }
            if (deathresting)
            {
                gate = AbGateDeathresting;
                reason = AbTr("CommandDisabledDeathresting",
                    () => "CommandDisabledDeathresting".Translate(pawn).ToString(),
                    "the caster is deathresting");
                return true;
            }
            bool pacifist = false;
            try { pacifist = def.casterMustBeCapableOfViolence && pawn.WorkTagIsDisabled(WorkTags.Violent); }
            catch { }
            if (pacifist)
            {
                gate = AbGateViolence;
                reason = AbTr("IsIncapableOfViolence",
                    () => "IsIncapableOfViolence".Translate(pawn.LabelShort, pawn).ToString(),
                    "the caster is incapable of violence and this ability requires it "
                        + "(AbilityDef.casterMustBeCapableOfViolence)");
                return true;
            }
            bool canQueue = true;
            try { canQueue = a.CanQueueCast; } catch { }
            if (!canQueue)
            {
                gate = AbGateQueued;
                reason = AbTr("AbilityAlreadyQueued",
                    () => "AbilityAlreadyQueued".Translate().ToString(),
                    "a cast of this ability (or of another in its AbilityGroupDef) is already "
                        + "running or queued; Ability.CanQueueCast walks pawn.jobs.AllJobs()");
                return true;
            }
            return false;
        }

        // =====================================================================
        //                         THE TARGET LAYER
        // =====================================================================

        // The eleven clauses that need a LocalTargetInfo, in the game's order:
        // the targeter's parameter test, then Verb_CastAbility.ValidateTarget,
        // then Verb_CastPsycast.ValidateTarget's four, then the one
        // QueueCastingJob itself gates on.
        private static bool AbTargetRefusal(Pawn pawn, Ability a, Verb verb,
            LocalTargetInfo target, LocalTargetInfo dest, out string gate, out string reason)
        {
            gate = null;
            reason = null;
            var def = a.def;

            // ---- T0: RimWorld/TargetingParameters.cs CanTarget --------------
            // Applied by the targeter, separately from ValidateTarget. It is
            // the clause that refuses a CELL for canTargetLocations:false and a
            // PAWN for canTargetPawns:false.
            var tp = AbTargetParams(verb);
            if (tp != null)
            {
                bool canTarget;
                try { canTarget = tp.CanTarget(target.ToTargetInfo(pawn.Map), verb); }
                catch (Exception e)
                {
                    gate = AbGateTargetParams;
                    reason = "TargetingParameters.CanTarget threw on this target: "
                        + e.GetType().Name + ": " + e.Message;
                    return true;
                }
                if (!canTarget)
                {
                    gate = AbGateTargetParams;
                    reason = target.HasThing
                        ? "'" + def.defName + "' does not accept this target. Its "
                          + "verbProperties.targetParams: " + AbParamsSummary(tp)
                        : "'" + def.defName + "' cannot be aimed at a CELL: its "
                          + "verbProperties.targetParams.canTargetLocations is "
                          + tp.canTargetLocations + ". " + AbParamsSummary(tp);
                    return true;
                }
            }

            // ---- T1/T2/T3: RimWorld/Verb_CastAbility.cs ValidateTarget ------
            float range = 0f;
            try { range = verb.EffectiveRange; } catch { }
            if (range > 0f)
            {
                bool canHit;
                try { canHit = verb.CanHitTarget(target); } catch { canHit = false; }
                if (!canHit)
                {
                    bool outOfRange = false;
                    try
                    {
                        outOfRange = verb.OutOfRange(pawn.Position, target,
                            target.HasThing ? target.Thing.OccupiedRect() : CellRect.SingleCell(target.Cell));
                    }
                    catch { }
                    if (outOfRange)
                    {
                        gate = AbGateOutOfRange;
                        reason = AbTr("AbilityOutOfRange",
                            () => "AbilityOutOfRange".Translate().ToString(), "out of range")
                            + " [effective range " + range.ToString("0.##")
                            + " — Verb.EffectiveRange is verbProps.AdjustedRange, which CLAMPS to "
                            + "map.weatherManager.CurWeatherMaxRangeCap, so a def's nominal range is "
                            + "not what applies in fog]";
                        return true;
                    }
                    gate = AbGateCannotHit;
                    reason = AbTr("AbilityCannotHitTarget",
                        () => "AbilityCannotHitTarget".Translate().ToString(),
                        "in range but cannot be hit")
                        + " [Verb.CanHitTargetFrom: a target on another map, no line of sight where "
                        + "verbProps.requireLineOfSight is " + AbBool(() => verb.verbProps.requireLineOfSight)
                        + ", a psychologically invisible hostile, or apparel preventing the cast]";
                    return true;
                }
            }
            else
            {
                bool reach = false;
                try { reach = pawn.CanReach(target, PathEndMode.Touch, pawn.NormalMaxDanger()); }
                catch { }
                if (!reach)
                {
                    gate = AbGateCannotReach;
                    reason = AbTr("AbilityCannotReachTarget",
                        () => "AbilityCannotReachTarget".Translate().ToString(),
                        "cannot reach the target")
                        + " [EffectiveRange is 0, so Verb_CastAbility.ValidateTarget requires "
                        + "pawn.CanReach(target, Touch, NormalMaxDanger) instead of a shot line]";
                    return true;
                }
            }

            // ---- T4: Verb_CastPsycast.IsApplicableTo -------------------------
            if (a is Psycast psy)
            {
                bool applicable = true;
                try { applicable = def.HasAreaOfEffect || psy.CanApplyPsycastTo(target); } catch { }
                if (!applicable)
                {
                    gate = AbGateDeaf;
                    reason = AbTr("AbilityTargetPsychicallyDeaf",
                        () => "AbilityTargetPsychicallyDeaf".Translate().ToString(),
                        "the target is psychically deaf")
                        + " [Psycast.CanApplyPsycastTo: zero PsychicSensitivity, or a mechanoid "
                        + "target for a comp that is not applicableToMechs]";
                    return true;
                }
            }

            // ---- T5: EffectComps[i].Valid -----------------------------------
            // showMessages FALSE — the true form throws top-of-screen messages
            // and, in AbilityUtility's validators, can raise a message per
            // clause. Second of the three places a modded comp refuses.
            var comps = AbEffectComps(a);
            for (int i = 0; i < comps.Count; i++)
            {
                bool valid;
                try { valid = comps[i].Valid(target, false); }
                catch (Exception e)
                {
                    gate = AbGateCompValid;
                    reason = comps[i].GetType().Name + ".Valid threw: "
                        + e.GetType().Name + ": " + e.Message;
                    return true;
                }
                if (!valid)
                {
                    gate = AbGateCompValid;
                    reason = "refused by " + comps[i].GetType().Name
                        + ".Valid(target) [CompAbilityEffect.Valid — the base checks canTargetBaby "
                        + "and canTargetBosses; a modded comp adds its own conditions and does not "
                        + "have to explain them. Ask the mod, not the ability def.]";
                    return true;
                }
            }

            // ---- T6..T9: RimWorld/Verb_CastPsycast.cs ValidateTarget ---------
            if (a is Psycast psy2)
            {
                var victim = target.Pawn;
                bool allowBosses = true;
                try
                {
                    for (int i = 0; i < comps.Count; i++)
                        if (!comps[i].Props.canTargetBosses) { allowBosses = false; break; }
                }
                catch { }
                bool isBoss = false;
                try { isBoss = victim != null && victim.kindDef != null && victim.kindDef.isBoss; } catch { }
                if (!allowBosses && isBoss)
                {
                    gate = AbGateBoss;
                    reason = AbTr("CommandPsycastInsanityImmune",
                        () => "CommandPsycastInsanityImmune".Translate().ToString(),
                        "the target is a boss and this psycast's comps are not canTargetBosses");
                    return true;
                }
                var ent = pawn.psychicEntropy;
                if (ent == null)
                {
                    gate = AbGateNoEntropyTracker;
                    reason = "this pawn has no Pawn_PsychicEntropyTracker; "
                        + "Verb_CastPsycast.ValidateTarget dereferences it";
                    return true;
                }
                float sensitivity = 0f;
                try { sensitivity = ent.PsychicSensitivity; } catch { }
                if (sensitivity < float.Epsilon)
                {
                    gate = AbGateCasterSensitivity;
                    reason = AbTr("CommandPsycastZeroPsychicSensitivity",
                        () => "CommandPsycastZeroPsychicSensitivity".Translate().ToString(),
                        "the caster's psychic sensitivity is zero")
                        + " [checked a second time at the target layer]";
                    return true;
                }
                float queuedEntropy = AbQueuedEntropy(pawn);
                bool overflow = false;
                try
                {
                    overflow = def.EntropyGain > float.Epsilon
                        && ent.WouldOverflowEntropy(def.EntropyGain + queuedEntropy);
                }
                catch { }
                if (overflow)
                {
                    gate = AbGateEntropyQueued;
                    reason = AbTr("CommandPsycastWouldExceedEntropy",
                        () => "CommandPsycastWouldExceedEntropy".Translate().ToString(),
                        "would overflow neural heat with the queued casts included");
                    return true;
                }
                float finalCost = 0f;
                try { finalCost = psy2.FinalPsyfocusCost(target); } catch { finalCost = def.PsyfocusCost; }
                float queuedFocus = AbQueuedPsyfocus(pawn);
                float have = 0f;
                try { have = ent.CurrentPsyfocus; } catch { }
                if (finalCost > float.Epsilon && finalCost + queuedFocus > have + AbPsyfocusTolerance)
                {
                    gate = AbGatePsyfocusQueued;
                    reason = AbTr("CommandPsycastNotEnoughPsyfocus",
                        () => "CommandPsycastNotEnoughPsyfocus".Translate(
                            (finalCost + queuedFocus).ToStringPercent("0.#"),
                            (have - queuedFocus).ToStringPercent("0.#"),
                            def.label.Named("PSYCASTNAME"),
                            pawn.Named("CASTERNAME")).ToString(),
                        "needs " + finalCost.ToString("0.###") + " psyfocus for THIS target plus "
                            + queuedFocus.ToString("0.###") + " queued; the caster has "
                            + have.ToString("0.###"))
                        + (Math.Abs(finalCost - def.PsyfocusCost) > 0.0001f
                            ? " [Ability.FinalPsyfocusCost(target) is " + finalCost.ToString("0.###")
                              + ", NOT the flat stat " + def.PsyfocusCost.ToString("0.###")
                              + " — a comp with props.OverridesPsyfocusCost replaced it for this target]"
                            : "");
                    return true;
                }
            }

            // ---- T10: RimWorld/Ability.cs CanApplyOn -------------------------
            // The clause QueueCastingJob gates on, and the one that fails
            // SILENTLY — it returns and the player sees nothing. EffectComps was
            // touched before this so the private field is filled and the comps
            // are actually consulted; without that it returns true having
            // skipped all of them.
            bool canApply;
            try { canApply = a.CanApplyOn(target); }
            catch (Exception e)
            {
                gate = AbGateCannotApply;
                reason = "Ability.CanApplyOn threw: " + e.GetType().Name + ": " + e.Message
                    + " [CompAbilityEffect.CanApplyOn dereferences target.Pawn.health when its "
                    + "props.availableWhenTargetIsWounded is false, so a cell target can NRE there]";
                return true;
            }
            if (!canApply)
            {
                gate = AbGateCannotApply;
                reason = "Ability.CanApplyOn(target) is false, which is the clause "
                    + "Ability.QueueCastingJob gates on — and it fails SILENTLY, with no message and "
                    + "no job. Either a CompAbilityEffect.CanApplyOn refused (a DIFFERENT method from "
                    + "the Valid the targeter uses; the two are never both called), or the ability is "
                    + "self-only (`!verbProperties.targetable && targetParams.canTargetSelf` requires "
                    + "target == caster).";
                return true;
            }
            return false;
        }

        // =====================================================================
        //                         ROWS AND BLOCKS
        // =====================================================================

        private static Dictionary<string, object> AbRow(Pawn pawn, Ability a, AbTarget target)
        {
            var def = a.def;
            // Addition B, on the READ side too: fill the private effectComps
            // field before CanApplyOn is consulted, or it skips every comp and
            // this row would publish a castable:true the act would then refuse.
            int compCount = AbTouchEffectComps(a);
            var verb = AbVerbOf(a);

            string gate, reason, babySource;
            bool refused = AbGizmoRefusal(pawn, a, out gate, out reason, out babySource);

            var row = new Dictionary<string, object>
            {
                ["def"] = def.defName,
                ["label"] = def.label,
                ["level"] = def.level,
                ["is_psycast"] = SafeObj(() => (object)def.IsPsycast),
                ["ability_class"] = a.GetType().Name,
                ["category"] = def.category != null ? def.category.defName : null,
                ["group"] = def.groupDef != null ? def.groupDef.defName : null,
                ["hostile"] = def.hostile,
                ["psyfocus_cost"] = PawnSafe.R(def.PsyfocusCost, 3),
                ["entropy_gain"] = PawnSafe.R(def.EntropyGain, 2),
                ["required_psyfocus_band"] = SafeObj(() => (object)def.RequiredPsyfocusBand),
                ["cooldown_ticks_remaining"] = SafeObj(() => (object)a.CooldownTicksRemaining),
                ["cooldown_ticks_total"] = SafeObj(() => (object)a.CooldownTicksTotal),
                ["on_cooldown"] = SafeObj(() => (object)a.OnCooldown),
                ["uses_charges"] = SafeObj(() => (object)a.UsesCharges),
                ["charges"] = SafeObj(() => a.UsesCharges ? (object)a.RemainingCharges : null),
                ["max_charges"] = SafeObj(() => a.UsesCharges ? (object)a.maxCharges : null),
                ["comps"] = AbCompNames(a),
                ["effect_comps"] = compCount,
                ["target_required"] = def.targetRequired,
                ["target_world_cell"] = def.targetWorldCell,
                ["display_gizmo_while_undrafted"] = def.displayGizmoWhileUndrafted,
                ["disable_gizmo_while_undrafted"] = def.disableGizmoWhileUndrafted,
                ["show_when_drafted"] = def.showWhenDrafted,
                ["caster_must_be_capable_of_violence"] = def.casterMustBeCapableOfViolence,
                ["confirmation_text"] = Journal.Truncate(def.confirmationDialogText, 400),
                ["castable"] = !refused,
                ["gate"] = gate,
                ["reason"] = Journal.Truncate(reason, 900),
                ["baby_check"] = babySource,
            };
            if (def.verbProperties != null)
            {
                row["warmup_seconds"] = PawnSafe.R(def.verbProperties.warmupTime, 2);
                row["requires_los"] = def.verbProperties.requireLineOfSight;
                row["nominal_range"] = PawnSafe.R(def.verbProperties.range, 2);
                row["non_interrupting_self_cast"] = def.verbProperties.nonInterruptingSelfCast;
            }
            if (verb != null)
            {
                // EffectiveRange, not verbProps.range: VerbProperties.AdjustedRange
                // clamps to map.weatherManager.CurWeatherMaxRangeCap, so a
                // 9999-range psycast collapses in fog and the def's number lies.
                row["range"] = SafeObj(() => (object)PawnSafe.R(verb.EffectiveRange, 2));
                var tp = AbTargetParams(verb);
                if (tp != null) row["target_params"] = AbParamsBlock(tp);
            }
            else
            {
                row["range"] = null;
                row["verb"] = "unavailable — Ability.verb reads the private verbTracker field and "
                    + "this ability has not been Initialize()d";
            }
            // A modded subclass may override the very members this row
            // reproduces. Say so rather than publishing a verdict we cannot
            // stand behind; `ability-cast` calls the real GizmoDisabled and
            // will catch it.
            var overrides = AbOverrides(a);
            if (overrides != null) row["overrides"] = overrides;

            if (target.Given && verb != null)
            {
                var local = target.Local(pawn);
                float finalCost = def.PsyfocusCost;
                try { if (a is Psycast psy) finalCost = psy.FinalPsyfocusCost(local); } catch { }
                if (Math.Abs(finalCost - def.PsyfocusCost) > 0.0001f)
                    row["final_psyfocus_cost"] = PawnSafe.R(finalCost, 3);
                string tgate, treason;
                bool trefused = AbTargetRefusal(pawn, a, verb, local,
                    LocalTargetInfo.Invalid, out tgate, out treason);
                row["target_castable"] = !refused && !trefused;
                row["target_gate"] = trefused ? tgate : null;
                row["target_reason"] = trefused ? Journal.Truncate(treason, 900) : null;
            }
            else if (target.Given)
            {
                row["target_castable"] = null;
                row["target_gate"] = AbGateNoVerb;
                row["target_reason"] = "the target-layer clauses need a Verb and this ability has none";
            }
            // The confirmation OUTLOOK, derived by reflection and never by
            // constructing the Window. `ability-cast` calls the real
            // Ability.ConfirmationDialog because a click does; an observer must
            // not, since a modded comp's ConfirmationDialog is free to
            // Find.WindowStack.Add on its own and one force-pausing window
            // halts every subsequent `advance` at 0 ticks.
            var outlook = AbConfirmationOutlook(a);
            if (outlook != null) row["confirmation"] = outlook;
            return row;
        }

        // The caster-level psychic budget. Nothing in the tree published any of
        // it before this issue: a `castable:false` with reason
        // CommandPsycastLowPsyfocus is unactionable without the band, and
        // "wait for entropy" is unactionable without the recovery rate.
        private static Dictionary<string, object> AbPsychic(Pawn pawn)
        {
            var ent = pawn.psychicEntropy;
            var d = new Dictionary<string, object>
            {
                // PawnUtility.GetPsylinkLevel sums every Hediff_Psylink.level.
                // Pawn_PsychicEntropyTracker has no PsylinkLevel member.
                ["psylink_level"] = SafeObj(() => (object)pawn.GetPsylinkLevel()),
                ["max_psylink_level"] = SafeObj(() => (object)pawn.GetMaxPsylinkLevel()),
                ["has_tracker"] = ent != null,
            };
            if (ent == null)
            {
                d["note"] = "no Pawn_PsychicEntropyTracker on this pawn (created for humanlikes with "
                    + "Royalty active). Every psycast refuses at the `no-entropy-tracker` gate.";
                return d;
            }
            d["psyfocus"] = SafeObj(() => (object)PawnSafe.R(ent.CurrentPsyfocus, 3));
            d["target_psyfocus"] = SafeObj(() => (object)PawnSafe.R(ent.TargetPsyfocus, 3));
            d["psyfocus_band"] = SafeObj(() => (object)ent.PsyfocusBand);
            d["max_ability_level"] = SafeObj(() => (object)ent.MaxAbilityLevel);
            d["entropy"] = SafeObj(() => (object)PawnSafe.R(ent.EntropyValue, 2));
            // Class F, sanctioned: MaxEntropy is pawn.GetStatValue, i.e. a
            // StatWorker memo write, the same one the inspect pane performs.
            d["max_entropy"] = SafeObj(() => (object)PawnSafe.R(ent.MaxEntropy, 2));
            d["entropy_relative"] = SafeObj(() => (object)PawnSafe.R(ent.EntropyRelativeValue, 3));
            d["entropy_severity"] = SafeObj(() => ent.Severity.ToString());
            d["entropy_recovery_rate"] = SafeObj(() => (object)PawnSafe.R(ent.RecoveryRate, 3));
            d["entropy_gain_multiplier"] = SafeObj(()
                => (object)PawnSafe.R(pawn.GetStatValue(StatDefOf.PsychicEntropyGain), 3));
            d["limit_entropy"] = SafeObj(() => (object)ent.limitEntropyAmount);
            d["psychic_sensitivity"] = SafeObj(() => (object)PawnSafe.R(ent.PsychicSensitivity, 3));
            d["is_meditating"] = SafeObj(() => (object)ent.IsCurrentlyMeditating);
            d["needs_psyfocus"] = SafeObj(() => (object)ent.NeedsPsyfocus);
            d["queued"] = new Dictionary<string, object>
            {
                ["psyfocus_cost"] = PawnSafe.R(AbQueuedPsyfocus(pawn), 3),
                ["entropy"] = PawnSafe.R(AbQueuedEntropy(pawn), 2),
                ["note"] = "PsycastUtility.TotalPsyfocusCostOfQueuedPsycasts / "
                    + "TotalEntropyFromQueuedPsycasts over pawn.jobs.curJob and jobQueue. These are "
                    + "ADDED to the cost in four separate gate clauses, so psyfocus alone does not "
                    + "tell you whether the next cast fits.",
            };
            d["bands"] = new Dictionary<string, object>
            {
                ["psyfocus_band_floors"] = new List<object> { 0.0, 0.25, 0.5, 1.0 },
                ["max_ability_level_per_band"] = new List<object> { 2.0, 4.0, 6.0 },
                ["note"] = "Pawn_PsychicEntropyTracker.PsyfocusBandPercentages against "
                    + "MaxAbilityLevelPerPsyfocusBand: below 25% psyfocus nothing above level 2 casts, "
                    + "below 50% nothing above level 4, EVEN WHEN THE COST IS AFFORDABLE. That is the "
                    + "`psyfocus-band` gate and it is the most confusing psycast rule in the game.",
            };
            d["psyfocus_cost_tolerance"] = 0.0005;
            return d;
        }

        // =====================================================================
        //                        TARGET RESOLUTION
        // =====================================================================

        // A resolved target argument. Deliberately NOT Positions.Resolve for
        // every form: that helper maps "pawn:<id>" to the pawn's CELL, and the
        // difference between a thing target and the cell it stands on is the
        // whole of TargetingParameters.CanTarget's first branch
        // (`if (targ.Thing == null) return canTargetLocations;`).
        private sealed class AbTarget
        {
            public bool Given;
            public bool Self;
            public Thing Thing;
            public IntVec3 Cell;
            public bool IsCell;
            public string Describe;
            public bool Fogged;

            public LocalTargetInfo Local(Pawn caster)
            {
                if (Self) return new LocalTargetInfo(caster);
                if (Thing != null) return new LocalTargetInfo(Thing);
                return new LocalTargetInfo(Cell);
            }
        }

        private static AbTarget AbResolveTarget(Map map, VerbArgs args, string key, bool required)
        {
            var t = new AbTarget();
            object raw = args.Raw(key);
            if (raw == null)
            {
                if (required) throw new VerbArgsException("missing required arg '" + key + "'");
                return t;
            }
            t.Given = true;
            if (raw is double dnum)
            {
                t.Thing = AbThingById(map, (int)dnum, key);
                t.Describe = "thing:" + (int)dnum;
                t.Fogged = AbFogged(t.Thing, map);
                return t;
            }
            if (raw is string s)
            {
                if (s == "self")
                {
                    t.Self = true;
                    t.Describe = "self";
                    return t;
                }
                if (s.StartsWith("pawn:", StringComparison.Ordinal)
                    || s.StartsWith("thing:", StringComparison.Ordinal))
                {
                    int id;
                    if (!int.TryParse(s.Substring(s.IndexOf(':') + 1), out id))
                        throw new VerbArgsException("arg '" + key + "': '" + s
                            + "' must end in a thingIDNumber");
                    t.Thing = AbThingById(map, id, key);
                    t.Describe = s;
                    t.Fogged = AbFogged(t.Thing, map);
                    return t;
                }
            }
            // Everything else is a CELL: [x,z], "x,z", or a landmark name.
            IntVec3 c;
            try { c = Positions.Resolve(map, raw); }
            catch (VerbArgsException e)
            {
                throw new VerbArgsException("arg '" + key + "': " + e.Message
                    + ". A target is a thing id (number), \"pawn:<id>\", \"thing:<id>\", \"self\", "
                    + "or a cell as \"x,z\", [x,z] or a landmark name.");
            }
            t.IsCell = true;
            t.Cell = c;
            t.Describe = "(" + c.x + "," + c.z + ")";
            try { t.Fogged = c.Fogged(map); } catch { }
            return t;
        }

        // Fog-filtered, like PawnActs.ThingArg — the game's own default for an
        // order target (FloatMenuOptionProvider.IgnoreFogged => true) and this
        // project's standing rule. A CELL is NOT refused for fog: the ability
        // targeter has no fog clause for a location target (only
        // Ability.ValidAOEAffectedTarget refuses fogged THINGS, and only when
        // picking AOE victims), so the cell is accepted and `fogged` published.
        private static Thing AbThingById(Map map, int id, string key)
        {
            var things = map.listerThings.AllThings;
            for (int i = 0; i < things.Count; i++)
            {
                var th = things[i];
                if (th != null && th.thingIDNumber == id)
                {
                    if (WorldSafe.Hidden(th, map))
                        throw new VerbArgsException("no visible thing with id " + id
                            + " on the current map (things in unexplored ground are not reported)");
                    return th;
                }
            }
            throw new VerbArgsException("arg '" + key + "': no thing with id " + id
                + " on the current map");
        }

        private static bool AbFogged(Thing t, Map map)
        {
            try { return t != null && t.Spawned && t.Position.Fogged(map); }
            catch { return false; }
        }

        private static Dictionary<string, object> AbTargetBlock(AbTarget t)
        {
            if (t == null || !t.Given) return null;
            var d = new Dictionary<string, object>
            {
                ["kind"] = t.Self ? "self" : (t.IsCell ? "cell" : "thing"),
                ["fogged"] = t.Fogged,
            };
            if (t.Thing != null)
            {
                d["id"] = t.Thing.thingIDNumber;
                d["def"] = t.Thing.def != null ? t.Thing.def.defName : null;
                d["label"] = Safe(() => t.Thing.LabelShortCap.ToString());
                d["at"] = Positions.Out(t.Thing.PositionHeld);
                var vp = t.Thing as Pawn;
                if (vp != null)
                {
                    d["class"] = PawnSafe.Classify(vp);
                    d["downed"] = SafeObj(() => (object)vp.Downed);
                    d["mental"] = vp.MentalStateDef != null ? vp.MentalStateDef.defName : null;
                    d["psychic_sensitivity"] = SafeObj(()
                        => (object)PawnSafe.R(vp.GetStatValue(StatDefOf.PsychicSensitivity), 3));
                    d["boss"] = SafeObj(() => vp.kindDef != null ? (object)vp.kindDef.isBoss : null);
                }
            }
            else if (t.IsCell) d["at"] = Positions.Out(t.Cell);
            return d;
        }

        private static object AbTargetEcho(LocalTargetInfo t)
        {
            if (t.HasThing)
                return new Dictionary<string, object>
                {
                    ["id"] = t.Thing.thingIDNumber,
                    ["def"] = t.Thing.def != null ? t.Thing.def.defName : null,
                    ["at"] = Positions.Out(t.Thing.PositionHeld),
                };
            return new Dictionary<string, object> { ["at"] = Positions.Out(t.Cell) };
        }

        // =====================================================================
        //                             HELPERS
        // =====================================================================

        // Fill the PRIVATE effectComps field via the public property, once, so
        // Ability.CanApplyOn actually consults the comps instead of skipping
        // them. Returns the count so the row can say how many were consulted.
        private static int AbTouchEffectComps(Ability a)
        {
            try { var l = a.EffectComps; return l == null ? 0 : l.Count; }
            catch { return -1; }
        }

        private static List<CompAbilityEffect> AbEffectComps(Ability a)
        {
            try
            {
                var l = a.EffectComps;
                return l ?? new List<CompAbilityEffect>();
            }
            catch { return new List<CompAbilityEffect>(); }
        }

        private static List<object> AbCompNames(Ability a)
        {
            var names = new List<object>();
            try
            {
                if (a.comps != null)
                    for (int i = 0; i < a.comps.Count; i++)
                        if (a.comps[i] != null) names.Add(a.comps[i].GetType().Name);
            }
            catch { }
            return names;
        }

        // RimWorld/Ability.cs `verb` is `verbTracker.PrimaryVerb` reading the
        // PRIVATE FIELD, not the lazy VerbTracker property — so it throws on an
        // Ability that never ran Initialize(). Checked through a field ref
        // first so the common case never raises an exception, and never
        // through the property, which would CREATE a tracker on a pawn we are
        // only observing.
        private static bool abVerbRefTried;
        private static AccessTools.FieldRef<Ability, VerbTracker> abVerbTrackerRef;

        private static Verb AbVerbOf(Ability a)
        {
            if (!abVerbRefTried)
            {
                abVerbRefTried = true;
                try { abVerbTrackerRef = AccessTools.FieldRefAccess<Ability, VerbTracker>("verbTracker"); }
                catch (Exception e) { Journal.EmitWarning("abilities: verbTracker field ref failed: " + e.Message); }
            }
            try
            {
                if (abVerbTrackerRef != null && abVerbTrackerRef(a) == null) return null;
                return a.verb;
            }
            catch { return null; }
        }

        private static TargetingParameters AbTargetParams(Verb verb)
        {
            try { return verb.targetParams; } catch { return null; }
        }

        private static bool AbNeedsSelectedDest(Ability a)
        {
            try
            {
                var comp = a.CompOfType<CompAbilityEffect_WithDest>();
                return comp != null && comp.Props != null
                    && comp.Props.destination == AbilityEffectDestination.Selected;
            }
            catch { return false; }
        }

        private static float AbQueuedPsyfocus(Pawn pawn)
        {
            try { return PsycastUtility.TotalPsyfocusCostOfQueuedPsycasts(pawn); }
            catch { return 0f; }
        }

        private static float AbQueuedEntropy(Pawn pawn)
        {
            try { return PsycastUtility.TotalEntropyFromQueuedPsycasts(pawn); }
            catch { return 0f; }
        }

        private static int AbBandOf(Pawn_PsychicEntropyTracker ent)
        {
            try { return ent.PsyfocusBand; } catch { return -1; }
        }

        // The confirmation, PRE-CHECKED and never shown.
        // RimWorld/Ability.cs ConfirmationDialog is public, consults each effect
        // comp's ConfirmationDialog and then def.confirmationDialogText, and
        // only CONSTRUCTS the Window — ShowCastingConfirmationIfNeeded is what
        // would add it to the stack. Nothing here adds it, so the run never
        // acquires a force-pausing window, and the text the player would have
        // been shown rides on the result instead.
        private static Dictionary<string, object> AbConfirmation(Ability a, LocalTargetInfo target)
        {
            Window w = null;
            string err = null;
            try { w = a.ConfirmationDialog(target, AbNoop); }
            catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
            if (err != null)
                return new Dictionary<string, object>
                {
                    ["required"] = null,
                    ["error"] = "Ability.ConfirmationDialog threw: " + err,
                };
            if (w == null) return null;
            string text = null;
            try { text = a.def.confirmationDialogText; } catch { }
            return new Dictionary<string, object>
            {
                ["required"] = true,
                ["window"] = w.GetType().Name,
                ["text"] = Journal.Truncate(text, 1200),
                ["source"] = string.IsNullOrEmpty(text)
                    ? "a CompAbilityEffect.ConfirmationDialog (the def has no confirmationDialogText)"
                    : "AbilityDef.confirmationDialogText",
                ["note"] = "the player would see a FORCE-PAUSING modal here and would have to press "
                    + "the button. This verb constructed the window to read it and DID NOT SHOW IT — "
                    + "one such window halts every subsequent `advance` at 0 ticks with "
                    + "reason:\"dialog\" — and reproduced the callback body instead. The cast was "
                    + "taken as if the button had been pressed; read this text and decide whether "
                    + "that was what you wanted.",
            };
        }

        private static void AbNoop() { }

        // The read's half of the same question, answered WITHOUT constructing
        // anything: does this ability have a confirmation at all? True when the
        // def carries confirmationDialogText, or when an effect comp overrides
        // CompAbilityEffect.ConfirmationDialog — the two sources
        // Ability.ConfirmationDialog consults, in that method's own order
        // (comps first, def second).
        private static Dictionary<string, object> AbConfirmationOutlook(Ability a)
        {
            try
            {
                bool defText = !string.IsNullOrEmpty(a.def.confirmationDialogText);
                var overriding = new List<object>();
                var comps = AbEffectComps(a);
                for (int i = 0; i < comps.Count; i++)
                {
                    var m = comps[i].GetType().GetMethod("ConfirmationDialog",
                        new Type[] { typeof(LocalTargetInfo), typeof(Action) });
                    if (m != null && m.DeclaringType != typeof(CompAbilityEffect))
                        overriding.Add(comps[i].GetType().Name);
                }
                if (!defText && overriding.Count == 0) return null;
                return new Dictionary<string, object>
                {
                    ["required"] = true,
                    ["text"] = Journal.Truncate(a.def.confirmationDialogText, 1200),
                    ["from_comps"] = overriding,
                    ["note"] = "casting this raises a FORCE-PAUSING confirmation modal for a player. "
                        + "`ability-cast` reads the window's text, does NOT show it, and reproduces "
                        + "the callback body — so the cast is taken as if the button had been "
                        + "pressed. Read this text first. (Derived here by reflection over "
                        + "AbilityDef.confirmationDialogText and CompAbilityEffect.ConfirmationDialog "
                        + "overrides; the observer constructs no Window, because a modded comp's "
                        + "ConfirmationDialog is free to add one to the stack itself.)",
                };
            }
            catch { return null; }
        }

        private static Dictionary<string, object> AbRefusalDetail(Ability a, AbTarget t, string babySource)
        {
            var d = new Dictionary<string, object>
            {
                ["ability"] = a.def.defName,
                ["level"] = a.def.level,
                ["psyfocus_cost"] = PawnSafe.R(a.def.PsyfocusCost, 3),
                ["entropy_gain"] = PawnSafe.R(a.def.EntropyGain, 2),
                ["cooldown_ticks_remaining"] = SafeObj(() => (object)a.CooldownTicksRemaining),
                ["psychic"] = AbPsychic(a.pawn),
            };
            if (babySource != null) d["baby_check"] = babySource;
            if (t != null && t.Given) d["target_kind"] = t.Self ? "self" : (t.IsCell ? "cell" : "thing");
            return d;
        }

        // Which of the members this file reproduces belong to a subclass we do
        // not know. Reflection on the DECLARING TYPE, so a mod that overrides
        // GizmoDisabled or CanCast is named rather than silently trusted.
        private static Dictionary<string, object> AbOverrides(Ability a)
        {
            try
            {
                var t = a.GetType();
                if (t == typeof(Ability) || t == typeof(Psycast)) return null;
                var d = new Dictionary<string, object> { ["ability_class"] = t.FullName };
                bool any = false;
                var gd = t.GetMethod("GizmoDisabled",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (gd != null && gd.DeclaringType != typeof(Ability) && gd.DeclaringType != typeof(Psycast))
                { d["gizmo_disabled"] = gd.DeclaringType.FullName; any = true; }
                var cc = t.GetProperty("CanCast",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (cc != null && cc.DeclaringType != typeof(Ability) && cc.DeclaringType != typeof(Psycast))
                { d["can_cast"] = cc.DeclaringType.FullName; any = true; }
                var ca = t.GetMethod("CanApplyOn", new Type[] { typeof(LocalTargetInfo) });
                if (ca != null && ca.DeclaringType != typeof(Ability))
                { d["can_apply_on"] = ca.DeclaringType.FullName; any = true; }
                if (!any) return null;
                d["note"] = "this ability's class overrides a member the gate chain reproduces, so a "
                    + "clause exists here that `abilities` cannot see without casting. `ability-cast` "
                    + "calls the real Ability.GizmoDisabled as its final authority and will refuse at "
                    + "the `gizmo-disabled` gate if the override says no.";
                return d;
            }
            catch { return null; }
        }

        private static Dictionary<string, object> AbParamsBlock(TargetingParameters tp)
            => new Dictionary<string, object>
            {
                ["can_target_locations"] = tp.canTargetLocations,
                ["can_target_self"] = tp.canTargetSelf,
                ["can_target_pawns"] = tp.canTargetPawns,
                ["can_target_humans"] = tp.canTargetHumans,
                ["can_target_animals"] = tp.canTargetAnimals,
                ["can_target_mechs"] = tp.canTargetMechs,
                ["can_target_buildings"] = tp.canTargetBuildings,
                ["can_target_items"] = tp.canTargetItems,
                ["can_target_plants"] = tp.canTargetPlants,
                ["can_target_fires"] = tp.canTargetFires,
                ["never_target_incapacitated"] = tp.neverTargetIncapacitated,
                ["only_target_psychic_sensitive"] = tp.onlyTargetPsychicSensitive,
                ["has_validator"] = tp.validator != null,
            };

        private static string AbParamsSummary(TargetingParameters tp)
        {
            var sb = new System.Text.StringBuilder("locations=");
            sb.Append(tp.canTargetLocations).Append(", pawns=").Append(tp.canTargetPawns)
              .Append(", humans=").Append(tp.canTargetHumans).Append(", animals=").Append(tp.canTargetAnimals)
              .Append(", mechs=").Append(tp.canTargetMechs).Append(", buildings=").Append(tp.canTargetBuildings)
              .Append(", items=").Append(tp.canTargetItems).Append(", self=").Append(tp.canTargetSelf);
            if (tp.validator != null)
                sb.Append(". It also carries a custom validator predicate, which can refuse for "
                    + "reasons no flag names.");
            return sb.ToString();
        }

        // The gate glossary rides on the read so a caller never has to guess
        // what a gate name meant, and so the read and the act are visibly one
        // vocabulary.
        private static Dictionary<string, object> AbGateGlossary()
            => new Dictionary<string, object>
            {
                ["gizmo_layer"] = new List<object>
                {
                    AbGateNotControlled, AbGateGizmoHidden, AbGateGizmoHiddenDrafted,
                    AbGateRoyaltyMissing, AbGateNoEntropyTracker, AbGateSensitivity, AbGatePsylink,
                    AbGatePsyfocus, AbGatePsyfocusBand, AbGateEntropy, AbGateCooldown, AbGateCharges,
                    AbGateCompGizmo, AbGateCanCast, AbGateLord, AbGateUndrafted, AbGateBaby,
                    AbGateDowned, AbGateDeathresting, AbGateViolence, AbGateQueued, AbGateGizmoOther,
                },
                ["target_layer"] = new List<object>
                {
                    AbGateTargetParams, AbGateOutOfRange, AbGateCannotHit, AbGateCannotReach,
                    AbGateDeaf, AbGateCompValid, AbGateBoss, AbGateCasterSensitivity,
                    AbGateEntropyQueued, AbGatePsyfocusQueued, AbGateCannotApply,
                },
                ["act_only"] = new List<object>
                {
                    AbGateNotKnown, AbGateNoVerb, AbGateTargetRequired, AbGateDestRequired,
                    AbGateWorldTarget, AbGateSelfCastQueue, GateAlready, AbGateRefused,
                },
            };

        // A translated key with the game's own arguments, falling back to a
        // phrase of OURS that is visibly ours. Same contract as PawnActs.Tr: a
        // missing key returns the key itself, and a key echoed back is not the
        // game's words.
        private static string AbTr(string key, Func<string> exact, string fallback)
        {
            try
            {
                string s = exact();
                if (string.IsNullOrEmpty(s) || s.Contains(key)) return fallback;
                return s;
            }
            catch { return fallback; }
        }

        private static string AbBool(Func<bool> f)
        {
            try { return f() ? "true" : "false"; } catch { return "?"; }
        }

        private static string AbFloat(Func<float> f)
        {
            try { return f().ToString("0.##"); } catch { return "?"; }
        }
    }
}
