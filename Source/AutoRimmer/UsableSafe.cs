using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // ======================================================= git-bug d318d4a ==
    // USABLE THINGS — the one reproduction of RimWorld/CompUsable.cs, shared.
    //
    // WHY THIS IS ITS OWN FILE AND NOT A SECTION OF PawnOrderVerbs. Three open
    // issues bottom out in the SAME nine-clause gate chain and the same job
    // construction: d318d4a (`use`), 826d4bf (`use {target}` for CompTargetable
    // items) and 88880d7 (bossgroup-call, whose act is `use` on a MechbandDish /
    // CommsConsole / BurnoutMechlinkBooster). Written per verb, that chain would
    // be transcribed three times and would diverge — the forked-catalogue
    // failure WorldSafe exists to prevent. d318d4a is the ROOT of that cluster
    // and owns this file; the other two ADD to it.
    //
    // ================== WHY THE VERB DOES NOT CALL TryStartUseJob =============
    // `CompUsable.TryStartUseJob` ends in
    //
    //     if (text.NullOrEmpty()) StartJob();
    //     else Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, StartJob));
    //
    // where `text` is every `CompUseEffect.ConfirmMessage(pawn)` concatenated.
    // `Verse/Dialog_MessageBox.cs` sets `forcePause = true`, and per spec 1.7 a
    // force-pausing window halts EVERY subsequent `advance` at 0 ticks with
    // `reason:"dialog"`. **No shipped verb can press its Accept button**:
    // `dialog-choose` (DialogVerbs.cs) takes a `Dialog_NodeTree` and reads
    // `curNode.options`, while a `Dialog_MessageBox` is a plain `Window` with
    // `buttonAAction`/`buttonBAction` and no `DiaOption`s; `dialog-dismiss`
    // TryRemoves the window, which CANCELS the confirmation, so the use never
    // happens. There is no route to "yes".
    //
    // And `RimWorld/CompUsableImplant.cs` overrides TryStartUseJob entirely and
    // raises a SECOND, separate `new Dialog_MessageBox(text, "Yes", …, "No")`
    // from `CompRoyalImplant.CheckForViolations` before ever reaching base —
    // and `CompUsableImplant` is the comp class on `Mechlink`,
    // `PsychicAmplifier` and all six mechanitor implants, i.e. on exactly the
    // defs this issue was filed for.
    //
    // So `use` REPRODUCES TryStartUseJob's local `StartJob()` (MakeJob below,
    // clause for clause) and never calls TryStartUseJob. The ConfirmMessage set
    // is PRE-COLLECTED (Confirmations below), transacted as accepted, and
    // DISCLOSED on the result — the shipped `dialogs_skipped` pattern from
    // `equip` (bladelink / persona weapon), `wear` (mechanitor bandwidth) and
    // `MedicalBillVerbs`' `sendMessages:false`. A royal-title violation is a
    // real consequence the agent owes a decision on, so it is NAMED rather than
    // swallowed.
    //
    // Reproducing StartJob is also what makes `queue` honourable: vanilla's
    // local ends `pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)` with no
    // `requestQueueing` parameter, the same shape that made `attack` refuse
    // `queue` with a gate (git-bug bc2250b). The caller of MakeJob passes the
    // job to `PawnActs.TakeOrder`, which does have it.
    //
    // ========================= THE GATE, IN FULL =============================
    // `CompUsable.CanBeUsedBy(Pawn p, bool forced = false, bool
    // ignoreReserveAndReachable = false)` — ONE method, no `out string`
    // overload; the refusal string is `AcceptanceReport.Reason`. Its clauses,
    // in order, each with its own gate name here:
    //
    //   1  !p.RaceProps.IsFlesh                      -> bare `false`, NO REASON
    //   2  p.IsMutant && !Props.allowedMutants…      -> bare `false`, NO REASON
    //   3  Props.layerWhitelist                      -> "CannotPerformPlanetLayer"
    //   4  Props.layerBlacklist                      -> "CannotPerformPlanetLayer"
    //   5  CompPowerTrader && !PowerOn               -> "NoPower"
    //   6  !p.CanReach(parent, Touch, Deadly)        -> "NoPath"
    //   7  !p.CanReserve(parent, 1, -1, null, forced)-> "ReservedBy"/"Reserved"
    //   8  Props.userMustHaveHediff                  -> "MustHaveHediff"
    //   9  each CompUseEffect.CanBeUsedBy(p), FIRST refusal wins
    //
    // Clauses 6 and 7 are SEPARATE — `CanReach` then `CanReserve`, not
    // `CanReserveAndReach`. Clauses 1 and 2 return a bare `false`, so
    // `Reason` is `""`: a verb that printed it would publish an empty refusal.
    // Every refusal below therefore carries `reason_from:"game"` or
    // `reason_from:"autorimmer"` so a phrase of ours is never dressed up as the
    // game's (PawnActs.Tr's rule, made explicit because two clauses force it).
    //
    // Clause 9 SHORT-CIRCUITS. That is why `Refusals()` takes `stopAtFirst`:
    // the act path stops where the game stops, and `use-options` runs the whole
    // loop so the agent sees every reason at once instead of peeling them off
    // one advance at a time.
    //
    // MapHeld NRE (PawnSafe Class D). Clause 3's setup is `PlanetTile tile =
    // p.MapHeld.Tile;` with no null guard — same shape as
    // `Pawn_PlayerSettings.EffectiveAreaRestrictionInPawnCurrentMap`. Guarded
    // here; a pawn with no MapHeld is refused by its own gate rather than
    // throwing where the player's own click would throw too.
    //
    // ==================== WHAT IS *NOT* A GATE, AND WHY ======================
    //  * FORBIDDEN. No `IsForbidden` check exists anywhere from
    //    `FloatMenuOptionProvider_FromThing` down to `TryStartUseJob`; the only
    //    one is `FailOnDespawnedNullOrForbidden(TargetIndex.A)` on
    //    `JobDriver_UseItem`'s wait toil. So `use` un-forbids before ordering,
    //    exactly like `equip`/`wear`/`consume`, and says so on the line.
    //  * DRAFTED. `FloatMenuOptionProvider_FromThing` sets `Drafted => true`
    //    AND `Undrafted => true`. A drafted pawn may use.
    //  * MANIPULATION. `RequiresManipulation => false` on that provider; the
    //    check is `this.FailOnIncapable(PawnCapacityDefOf.Manipulation)` INSIDE
    //    `JobDriver_UseItem`, i.e. the job is taken and then fails. Refusing up
    //    front would fabricate a restriction the player does not have (the
    //    261f2e9 error facing the other way), so it is published as an
    //    ADVISORY, not a gate.
    //  * MECHANOID. `MechanoidCanDo => false` IS real — and clause 1
    //    (`!IsFlesh`) refuses mechs anyway, so the vocabulary is `not-flesh`.
    //  * DESPAWNED. `CanTargetDespawned => false`: an item in an inventory or a
    //    container offers nothing. `PawnActs.ThingArg` resolves only from
    //    `map.listerThings.AllThings`, so this holds for free.
    //
    // ======================= THE SECOND PLAYER ROUTE =========================
    // `CompUsable.CompGetGizmosExtra` yields a `Command_Action` when
    // `Props.showUseGizmo`, whose action is `Find.Targeter.BeginTargeting(this)`
    // — CompUsable is itself an `ITargetingSource`, and the click lands in
    // `OrderForceTarget`. That route is DELIBERATELY NOT DRIVEN: it arms
    // `Find.Targeter` and consumes the next click, which on a headless bench
    // never comes. The float-menu route is the one reproduced. (showUseGizmo is
    // set on the neurotrainers/psytrainers, TechprofSubpersonaCore,
    // MechSerumHealer/Resurrector, GhoulResurrectionSerum, SentienceCatalyst,
    // DeathrestCapacitySerum, PsychicAmplifier and the artifact/serum bases —
    // but NOT on Mechlink, the six mechanitor implants, or any building.)
    internal static class UsableSafe
    {
        // ---------------------------- gate names -----------------------------
        public const string GateNotUsable = "not-usable";
        public const string GateNoUseJob = "no-use-job";
        public const string GateNoMap = "no-map";
        public const string GateNotFlesh = "not-flesh";
        public const string GateMutant = "mutant";
        public const string GateLayerWhitelist = "layer-whitelist";
        public const string GateLayerBlacklist = "layer-blacklist";
        public const string GateNoPower = "no-power";
        public const string GateNoPath = "no-path";
        public const string GateReserved = "reserved";
        public const string GateHediff = "hediff";
        public const string GateResearchBench = "no-research-bench";
        public const string GateOverride = "usable-override";
        public const string GateNeedsTarget = "needs-target";
        public const string GateUnknownUseOption = "unknown-use-option";

        // A single refused clause: the gate that refused, the reason, and WHOSE
        // words the reason is. `Comp` is the CompUseEffect class for clause 9,
        // null for clauses 1-8.
        internal sealed class Refusal
        {
            public string Gate;
            public string Reason;
            public bool FromGame;
            public string Comp;

            public Refusal(string gate, string reason, bool fromGame, string comp = null)
            { Gate = gate; Reason = reason; FromGame = fromGame; Comp = comp; }

            public Dictionary<string, object> Row()
            {
                var d = new Dictionary<string, object>
                {
                    ["gate"] = Gate,
                    ["reason"] = Reason,
                    // Two of the nine clauses return a bare `false`, so the
                    // game HAS no words for them. Saying whose words these are
                    // costs one key and stops a phrase of ours being quoted
                    // back as RimWorld's.
                    ["reason_from"] = FromGame ? "game" : "autorimmer",
                };
                if (Comp != null) d["use_effect"] = Comp;
                return d;
            }
        }

        // ------------------------------ lookup -------------------------------

        public static CompUsable Comp(Thing t)
        {
            try { return t?.TryGetComp<CompUsable>(); }
            catch { return null; }
        }

        // Every CompUseEffect on the thing, in AllComps order — the order
        // CanBeUsedBy walks and therefore the order refusals arrive in.
        // (UsedBy walks a DIFFERENT order: descending OrderPriority. See
        // DestroysSelf below.)
        public static List<CompUseEffect> Effects(CompUsable comp)
        {
            var list = new List<CompUseEffect>();
            try
            {
                var all = comp?.parent?.AllComps;
                if (all == null) return list;
                for (int i = 0; i < all.Count; i++)
                    if (all[i] is CompUseEffect e) list.Add(e);
            }
            catch { }
            return list;
        }

        // ================ THE NINE CLAUSES, REPRODUCED IN ORDER ==============
        // `stopAtFirst` mirrors the game (first refusal wins) for the act path;
        // false runs the whole chain so `use-options` can publish every reason.
        //
        // `forced` is `Props.ignoreOtherReservations`, which is what
        // `CompFloatMenuOptions` passes — NOT a caller-supplied flag. The verb
        // does not get to force a reservation the player's click would not.
        public static List<Refusal> Refusals(CompUsable comp, Pawn p, bool stopAtFirst)
        {
            var outp = new List<Refusal>();
            if (comp == null || p == null)
            {
                outp.Add(new Refusal(GateNotUsable, "no CompUsable on this thing", false));
                return outp;
            }
            bool forced = false;
            try { forced = comp.Props.ignoreOtherReservations; } catch { }
            var parent = comp.parent;

            // 1 — !p.RaceProps.IsFlesh. Bare `false`: the game has no words.
            bool flesh = true;
            try { flesh = p.RaceProps.IsFlesh; } catch { }
            if (!flesh)
            {
                outp.Add(new Refusal(GateNotFlesh,
                    "this pawn is not flesh (a mechanoid or a building-like pawn); "
                    + "CompUsable.CanBeUsedBy returns a bare false here, with no reason string of its own",
                    false));
                if (stopAtFirst) return outp;
            }

            // 2 — the mutant whitelist. Also a bare `false`. The only shipped
            // non-empty allowedMutants is [Ghoul], on the four Anomaly serums.
            try
            {
                if (p.IsMutant && p.mutant != null
                    && (comp.Props.allowedMutants == null || !comp.Props.allowedMutants.Contains(p.mutant.Def)))
                {
                    outp.Add(new Refusal(GateMutant,
                        "this pawn is a " + (p.mutant.Def?.label ?? "mutant")
                        + " and CompProperties_Usable.allowedMutants does not list it; "
                        + "CanBeUsedBy returns a bare false here, with no reason string of its own",
                        false));
                    if (stopAtFirst) return outp;
                }
            }
            catch { }

            // 3/4 — planet layer. THE MapHeld GUARD: vanilla's setup line is
            // `PlanetTile tile = p.MapHeld.Tile;` with no null check.
            Map held = null;
            try { held = p.MapHeld; } catch { }
            if (held == null)
            {
                // Only a refusal when the clauses could actually have fired;
                // otherwise inventing one would fabricate a restriction.
                bool layered = false;
                try
                {
                    layered = !comp.Props.layerWhitelist.NullOrEmpty()
                              || !comp.Props.layerBlacklist.NullOrEmpty();
                }
                catch { }
                if (layered)
                {
                    outp.Add(new Refusal(GateNoMap,
                        "this pawn has no MapHeld, and CompUsable.CanBeUsedBy dereferences "
                        + "`p.MapHeld.Tile` with no null guard to test this def's planet-layer list — "
                        + "the player's own click would throw here (PawnSafe Class D)",
                        false));
                    if (stopAtFirst) return outp;
                }
            }
            else
            {
                PlanetTile tile = default(PlanetTile);
                bool haveTile = false;
                try { tile = held.Tile; haveTile = true; } catch { }
                if (haveTile && tile.Valid)
                {
                    PlanetLayerDef layer = null;
                    try { layer = tile.LayerDef; } catch { }
                    try
                    {
                        if (layer != null && !comp.Props.layerWhitelist.NullOrEmpty()
                            && !comp.Props.layerWhitelist.Contains(layer))
                        {
                            outp.Add(new Refusal(GateLayerWhitelist, LayerReason(layer), true));
                            if (stopAtFirst) return outp;
                        }
                    }
                    catch { }
                    try
                    {
                        if (layer != null && !comp.Props.layerBlacklist.NullOrEmpty()
                            && comp.Props.layerBlacklist.Contains(layer))
                        {
                            outp.Add(new Refusal(GateLayerBlacklist, LayerReason(layer), true));
                            if (stopAtFirst) return outp;
                        }
                    }
                    catch { }
                }
            }

            // 5 — powered. Reachable on CommsConsole, MechbandDish,
            // BurnoutMechlinkBooster: every CompUsable BUILDING.
            try
            {
                if (parent != null && parent.TryGetComp<CompPowerTrader>(out var power) && !power.PowerOn)
                {
                    outp.Add(new Refusal(GateNoPower, TrGame("NoPower", "no power"), true));
                    if (stopAtFirst) return outp;
                }
            }
            catch { }

            // 6 — reach. PathEndMode.Touch, which is the GATE's mode;
            // JobDriver_UseItem's goto toil uses InteractionCell when the def
            // hasInteractionCell, and that is a different question.
            bool reach = false;
            try { reach = p.CanReach(parent, PathEndMode.Touch, Danger.Deadly); } catch { }
            if (!reach)
            {
                outp.Add(new Refusal(GateNoPath, TrGame("NoPath", "no path"), true));
                if (stopAtFirst) return outp;
            }

            // 7 — reserve, and NAME the reserver the way the game does.
            bool reserve = true;
            try { reserve = p.CanReserve(parent, 1, -1, null, forced); } catch { }
            if (!reserve)
            {
                outp.Add(new Refusal(GateReserved, ReservedReason(p, parent), true));
                if (stopAtFirst) return outp;
            }

            // 8 — userMustHaveHediff. Reachable on the six mechanitor implants
            // that require MechlinkImplant (ControlSublink, ControlSublinkHigh,
            // MechFormfeeder, RemoteRepairer, RemoteShielder, RepairProbe).
            try
            {
                var need = comp.Props.userMustHaveHediff;
                if (need != null && !p.health.hediffSet.HasHediff(need))
                {
                    outp.Add(new Refusal(GateHediff,
                        TrGame1("MustHaveHediff", need, "requires the hediff " + need.label), true));
                    if (stopAtFirst) return outp;
                }
            }
            catch { }

            // 9 — every CompUseEffect, first refusal wins in the game.
            var effects = Effects(comp);
            for (int i = 0; i < effects.Count; i++)
            {
                var e = effects[i];
                string cls = e.GetType().Name;
                AcceptanceReport rep;
                try { rep = e.CanBeUsedBy(p); }
                catch (Exception ex)
                {
                    outp.Add(new Refusal(EffectGate(cls),
                        cls + ".CanBeUsedBy threw: " + ex.GetType().Name + ": " + ex.Message, false, cls));
                    if (stopAtFirst) return outp;
                    continue;
                }
                if (rep.Accepted) continue;
                string why = rep.Reason;
                if (string.IsNullOrEmpty(why))
                    outp.Add(new Refusal(EffectGate(cls), BareFalseWhy(e, p, cls), false, cls));
                else
                    outp.Add(new Refusal(EffectGate(cls), why, true, cls));
                if (stopAtFirst) return outp;
            }

            // 9b — CompUseableDatacore's OWN clause, which sits AFTER the base
            // chain: `if (!GetExtraTarget(p).HasThing) return "NoResearchBench"`.
            // Its GetExtraTarget is a nearest-reachable research bench, so this
            // is a real refusal on MechanoidTransponder and nothing else ships
            // it. Named rather than folded into `usable-override` because it is
            // a vanilla clause with a vanilla reason.
            if (comp is CompUseableDatacore)
            {
                bool bench = false;
                try { bench = comp.GetExtraTarget(p).HasThing; } catch { }
                if (!bench)
                {
                    outp.Add(new Refusal(GateResearchBench,
                        TrGame("NoResearchBench", "no research bench it can reach"), true));
                    if (stopAtFirst) return outp;
                }
            }
            else if (OverridesCanBeUsedBy(comp))
            {
                // A MODDED CompUsable subclass with its own CanBeUsedBy clauses
                // that this file cannot know. Ask the game itself, once, and
                // report the difference rather than accepting an order the
                // player's own click would not have offered. Cheap: it only
                // runs for a type that actually overrides.
                AcceptanceReport rep;
                try { rep = comp.CanBeUsedBy(p, forced); }
                catch (Exception ex)
                {
                    outp.Add(new Refusal(GateOverride,
                        comp.GetType().Name + ".CanBeUsedBy threw: " + ex.GetType().Name + ": " + ex.Message,
                        false));
                    return outp;
                }
                if (!rep.Accepted && outp.Count == 0)
                {
                    string why = rep.Reason;
                    outp.Add(new Refusal(GateOverride,
                        string.IsNullOrEmpty(why)
                            ? comp.GetType().Name + " overrides CanBeUsedBy and refused with no reason string"
                            : why,
                        !string.IsNullOrEmpty(why)));
                }
            }

            return outp;
        }

        // The act path's question: the first refusal, or null when accepted.
        public static Refusal Gate(CompUsable comp, Pawn p)
        {
            var list = Refusals(comp, p, stopAtFirst: true);
            return list.Count > 0 ? list[0] : null;
        }

        // ===================== THE FLOAT MENU'S FIRST ACT ====================
        // `CompUsable.CompFloatMenuOptions`' action delegate opens with
        //
        //     foreach (CompUseEffect comp in parent.GetComps<CompUseEffect>())
        //         if (comp.SelectedUseOption(myPawn)) return;
        //
        // and `RimWorld/CompTargetable.cs` is the ONLY vanilla overrider: when
        // `PlayerChoosesTarget` it sets `caster = p`, calls
        // `Find.Targeter.BeginTargeting(this, …)` and returns TRUE — so
        // TryStartUseJob is never reached and the click becomes a targeting
        // cursor. A verb that skipped this and started the job anyway would run
        // to completion and DO NOTHING, because `CompTargetable.DoEffect` reads
        // the private `selectedTarget` field and returns early when it is null.
        // That is a false positive of exactly the class git-bug 4087644 names.
        //
        // So this is a REFUSAL with its own gate, not a silent success. Seating
        // `selectedTarget`/`caster` is git-bug 826d4bf's half of the verb.
        //
        // `PlayerChoosesTarget` is `protected abstract`, so it is read by
        // reflection rather than by a hardcoded class list — the shipped values
        // are true on _SingleCorpse/_SinglePawn/_SingleAnimal and false on
        // _AllPawnsOnTheMap/_AllAnimalsOnTheMap, and a mod may ship either.
        //
        // A modded NON-CompTargetable override of SelectedUseOption is refused
        // too (`unknown-use-option`): we will not call an unknown method that
        // vanilla uses to open UI.
        public static Refusal TargeterRoute(CompUsable comp, Pawn p, out List<CompUseEffect> toClear)
        {
            toClear = new List<CompUseEffect>();
            var effects = Effects(comp);
            for (int i = 0; i < effects.Count; i++)
            {
                var e = effects[i];
                if (!OverridesSelectedUseOption(e.GetType())) continue;   // base: `return false`
                if (e is CompTargetable ct)
                {
                    if (PlayerChoosesTarget(ct))
                        return new Refusal(GateNeedsTarget,
                            e.GetType().Name + " chooses its target by pointing: the game's own option calls "
                            + "Find.Targeter.BeginTargeting and never reaches TryStartUseJob, and "
                            + "CompTargetable.DoEffect returns early while its private selectedTarget is null — "
                            + "so ordering this without a target would complete the job and do nothing. "
                            + "The `target` argument is git-bug 826d4bf.",
                            false, e.GetType().Name);
                    // PlayerChoosesTarget == false: SelectedUseOption is
                    // `selectedTarget = null; return false;` — the clear IS the
                    // vanilla behaviour and dropping it would leave a scribed
                    // selectedTarget from a previous use pointing at a stale
                    // thing (CompTargetable.PostExposeData scribes it).
                    toClear.Add(e);
                    continue;
                }
                return new Refusal(GateUnknownUseOption,
                    e.GetType().Name + " overrides CompUseEffect.SelectedUseOption and is not a CompTargetable. "
                    + "Vanilla uses that method to open a targeting cursor, so it is not called blind on a "
                    + "headless bench; this def needs its own route.",
                    false, e.GetType().Name);
            }
            return null;
        }

        // ==================== THE CONFIRMATIONS, PRE-COLLECTED ================
        // Everything TryStartUseJob would have raised as a modal, as data.
        // Three vanilla ConfirmMessage overriders — CompUseEffect_CallBossgroup
        // (ALWAYS non-empty: the wave roster), CompUseEffect_InstallImplantMechlink
        // (Smithing work type or Intellectual work tag disabled) and
        // CompUseEffect_GainAbility (psylink level below the psycast's) — plus
        // CompUsableImplant's separate royal-title violation from
        // CompRoyalImplant.CheckForViolations.
        public static List<object> Confirmations(CompUsable comp, Pawn p)
        {
            var rows = new List<object>();
            var effects = Effects(comp);
            for (int i = 0; i < effects.Count; i++)
            {
                var e = effects[i];
                string text = null;
                try
                {
                    var ts = e.ConfirmMessage(p);
                    if (!ts.NullOrEmpty()) text = ts.Resolve();
                }
                catch (Exception ex) { text = e.GetType().Name + ".ConfirmMessage threw: " + ex.Message; }
                if (string.IsNullOrEmpty(text)) continue;
                rows.Add(new Dictionary<string, object>
                {
                    ["source"] = e.GetType().Name + ".ConfirmMessage",
                    ["text"] = Journal.Truncate(text, 600),
                });
            }

            // CompUsableImplant's own modal, one level ABOVE the base method.
            if (comp is CompUsableImplant)
            {
                try
                {
                    var implant = comp.parent.TryGetComp<CompUseEffect_InstallImplant>();
                    if (implant != null)
                    {
                        var existing = implant.GetExistingImplant(p) as Hediff_Level;
                        int offset = (existing != null && implant.Props.canUpgrade) ? 1 : 0;
                        var text = CompRoyalImplant.CheckForViolations(p, implant.Props.hediffDef, offset);
                        if (!text.NullOrEmpty())
                            rows.Add(new Dictionary<string, object>
                            {
                                ["source"] = "CompRoyalImplant.CheckForViolations "
                                    + "(raised by CompUsableImplant.TryStartUseJob, before the base method)",
                                ["text"] = Journal.Truncate(text.Resolve(), 900),
                                ["consequence"] = "installing this implant VIOLATES a royal title's rules; "
                                    + "the faction will react. This is a decision, not a formality.",
                            });
                    }
                }
                catch { }
            }
            return rows;
        }

        // The one message CompUsableImplant.UseJobInternal sends AFTER taking
        // the job, re-derived rather than left to a top-of-screen Messages call
        // the agent never reads.
        public static string PsylinkSensitivityWarning(CompUsable comp, Pawn p)
        {
            if (!(comp is CompUsableImplant)) return null;
            try
            {
                var implant = comp.parent.TryGetComp<CompUseEffect_InstallImplant>();
                if (implant?.Props?.hediffDef != HediffDefOf.PsychicAmplifier) return null;
                if (p.GetStatValue(StatDefOf.PsychicSensitivity) >= float.Epsilon) return null;
                return "this pawn has zero psychic sensitivity, so the psylink will do nothing "
                    + "(CompUsableImplant.UseJobInternal's MessagePsylinkNoSensitivity)";
            }
            catch { return null; }
        }

        // ================= StartJob(), REPRODUCED CLAUSE FOR CLAUSE ==========
        //     if (extraTarget == pawn) extraTarget = LocalTargetInfo.Invalid;
        //     Job job = extraTarget.IsValid
        //         ? JobMaker.MakeJob(Props.useJob, parent, extraTarget)
        //         : JobMaker.MakeJob(Props.useJob, parent);
        //     job.count = 1;
        //     pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        // The take is the CALLER's, through PawnActs.TakeOrder, so `queue`
        // reaches TryTakeOrderedJob's requestQueueing parameter.
        //
        // THE JOB IS PER-DEF AND IS NEVER NAMED HERE. There is no
        // `JobDefOf.UseItem` field (JobDefOf has UseNeurotrainer,
        // UseVerbOnThing, UseCommsConsole, UseOutfitStand, UseStylingStation,
        // GetNeuralSupercharge — and no UseItem); the job is `Props.useJob`,
        // and the shipped values are UseItem, UseNeurotrainer, UseArtifact,
        // InstallMechlink, TriggerObject, ReadDatacore, SmashThing — **and
        // Ingest**, on the four Anomaly serums, which carry an <ingestible>
        // block with showIngestFloatOption=false so the player only ever sees
        // "Use". Their CompUsable.UsedBy is invoked from
        // IngestionOutcomeDoer_UseThing.DoIngestionOutcomeSpecial, not from
        // JobDriver_UseItem. Read Props.useJob; never assume a driver.
        public static Job MakeJob(CompUsable comp, Pawn pawn, LocalTargetInfo extraTarget)
        {
            var useJob = comp.Props.useJob;
            if (extraTarget == pawn) extraTarget = LocalTargetInfo.Invalid;
            Job job = extraTarget.IsValid
                ? JobMaker.MakeJob(useJob, comp.parent, extraTarget)
                : JobMaker.MakeJob(useJob, comp.parent);
            job.count = 1;
            return job;
        }

        // The virtual, not a hardcoded Invalid: CompUseableDatacore overrides it
        // to the nearest reachable research bench, and JobDriver_UseItem hauls
        // the item there (job ReadDatacore -> JobDriver_UseItemResearchBench).
        public static LocalTargetInfo ExtraTarget(CompUsable comp, Pawn p)
        {
            try { return comp.GetExtraTarget(p); }
            catch { return LocalTargetInfo.Invalid; }
        }

        // Whether the thing destroys itself on use. `CompUseEffect_DestroySelf`
        // is a COMP, not a rule: `OrderPriority => Props.orderPriority`, default
        // -1000f, so it runs last — but only on defs that carry it. The
        // generated neurotrainers/psytrainers inherit it from the code-built
        // BaseNeurotrainer, TechprofSubpersonaCore and MechSerumHealer declare
        // it, Mechlink inherits it from MechanitorImplantBase — and no building
        // has it. "the item is destroyed" is per-def, so it is REPORTED.
        public static bool DestroysSelf(CompUsable comp)
        {
            var effects = Effects(comp);
            for (int i = 0; i < effects.Count; i++)
                if (effects[i] is CompUseEffect_DestroySelf) return true;
            return false;
        }

        // ------------------------- reason plumbing ---------------------------

        private static string LayerReason(PlanetLayerDef layer)
        {
            try
            {
                var s = "CannotPerformPlanetLayer".Translate(
                    layer.gerundLabel.Named("GERUND"), layer.label.Named("LAYER")).Resolve();
                if (!s.NullOrEmpty() && !s.Contains("CannotPerformPlanetLayer")) return s;
            }
            catch { }
            return "cannot be done on the " + (layer?.label ?? "current") + " planet layer";
        }

        private static string ReservedReason(Pawn p, Thing parent)
        {
            try
            {
                Pawn other = p.Map?.reservationManager?.FirstRespectedReserver(parent, p)
                             ?? p.Map?.physicalInteractionReservationManager?.FirstReserverOf(parent);
                if (other != null)
                {
                    var s = "ReservedBy".Translate(other.LabelShort, other).Resolve();
                    if (!s.NullOrEmpty() && !s.Contains("ReservedBy")) return s;
                    return "reserved by " + other.LabelShort;
                }
            }
            catch { }
            return TrGame("Reserved", "reserved");
        }

        // Same contract as PawnActs.Tr: the game's own string, or a phrase of
        // ours that the caller can tell apart via `reason_from`.
        private static string TrGame(string key, string fallback)
        {
            try
            {
                var s = key.Translate().ToString();
                return string.IsNullOrEmpty(s) || s.Contains(key) ? fallback : s.CapitalizeFirst();
            }
            catch { return fallback; }
        }

        private static string TrGame1(string key, Def arg, string fallback)
        {
            try
            {
                var s = key.Translate(arg).ToString();
                return string.IsNullOrEmpty(s) || s.Contains(key) ? fallback : s.CapitalizeFirst();
            }
            catch { return fallback; }
        }

        // The gate for a CompUseEffect refusal, derived from the class name so
        // it works for a modded comp written after this file: strip the
        // `CompUseEffect_` (or bare `Comp`) prefix and kebab-case the rest.
        // CompUseEffect_LearnSkill -> learn-skill;
        // CompUseEffect_FinishRandomResearchProject -> finish-random-research-project.
        public static string EffectGate(string className)
        {
            if (string.IsNullOrEmpty(className)) return "use-effect";
            string s = className;
            if (s.StartsWith("CompUseEffect_", StringComparison.Ordinal)) s = s.Substring(14);
            else if (s.StartsWith("CompUseEffect", StringComparison.Ordinal)) s = s.Substring(13);
            else if (s.StartsWith("Comp", StringComparison.Ordinal)) s = s.Substring(4);
            if (s.Length == 0) return "use-effect";
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                // `sb[^1] != '-'` matters: `CompTargetable_SingleCorpse` has an
                // underscore IMMEDIATELY before a capital, which without this
                // yields a double dash.
                bool dashable = sb.Length > 0 && sb[sb.Length - 1] != '-';
                if (char.IsUpper(c))
                {
                    if (dashable) sb.Append('-');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else if (c == '_') { if (dashable) sb.Append('-'); }
                else sb.Append(c);
            }
            return sb.Length > 0 ? sb.ToString() : "use-effect";
        }

        // The two shipped bare-`false` returns from a CompUseEffect, named. Not
        // a guess: each is the ONLY `return false` in that class's CanBeUsedBy.
        private static string BareFalseWhy(CompUseEffect e, Pawn p, string cls)
        {
            if (cls == "CompUseEffect_LearnSkill")
                return "this pawn has no skill tracker (Pawn.skills is null — a mech, or an animal); "
                     + "CompUseEffect_LearnSkill.CanBeUsedBy returns a bare false with no reason string";
            if (cls == "CompUseEffect_InstallImplantMechlink")
                return "Biotech is not active (ModLister.CheckBiotech); "
                     + "CompUseEffect_InstallImplantMechlink.CanBeUsedBy returns a bare false";
            return cls + ".CanBeUsedBy refused with a bare false and no reason string of its own";
        }

        // ---------------------- reflection, cached by type -------------------

        private static readonly Dictionary<Type, bool> canBeUsedByOverride = new Dictionary<Type, bool>();
        private static readonly Dictionary<Type, bool> selectedUseOptionOverride = new Dictionary<Type, bool>();
        private static bool playerChoosesTried;
        private static MethodInfo playerChoosesGetter;

        private static bool OverridesCanBeUsedBy(CompUsable comp)
        {
            var t = comp.GetType();
            if (canBeUsedByOverride.TryGetValue(t, out bool v)) return v;
            v = false;
            try
            {
                var m = t.GetMethod("CanBeUsedBy",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(Pawn), typeof(bool), typeof(bool) }, null);
                v = m != null && m.DeclaringType != typeof(CompUsable);
            }
            catch { }
            canBeUsedByOverride[t] = v;
            return v;
        }

        private static bool OverridesSelectedUseOption(Type t)
        {
            if (selectedUseOptionOverride.TryGetValue(t, out bool v)) return v;
            v = false;
            try
            {
                var m = t.GetMethod("SelectedUseOption",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(Pawn) }, null);
                v = m != null && m.DeclaringType != typeof(CompUseEffect);
            }
            catch { }
            selectedUseOptionOverride[t] = v;
            return v;
        }

        // `protected abstract bool PlayerChoosesTarget` — read, never assumed
        // from the class name. Unreadable => treated as TRUE, which refuses
        // rather than starting a job that would silently no-op.
        public static bool PlayerChoosesTarget(CompTargetable ct)
        {
            if (!playerChoosesTried)
            {
                playerChoosesTried = true;
                try
                {
                    playerChoosesGetter =
                        AccessTools.PropertyGetter(typeof(CompTargetable), "PlayerChoosesTarget");
                }
                catch (Exception e)
                {
                    Journal.EmitWarning("usable: PlayerChoosesTarget getter not bound: " + e.Message);
                }
            }
            // The getter is virtual, so Invoke dispatches to the subclass
            // override. Unbound => true, which REFUSES rather than starting a
            // job that would silently no-op.
            if (playerChoosesGetter == null) return true;
            try { return (bool)playerChoosesGetter.Invoke(ct, null); }
            catch { return true; }
        }
    }
}
