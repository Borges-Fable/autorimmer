using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // ======================================================= git-bug d318d4a ==
    // USE — the float-menu option every `CompUsable` thing offers.
    //
    // WIDGET GATE — RimWorld/CompUsable.cs `CompFloatMenuOptions`, reached from
    // `RimWorld/FloatMenuOptionProvider_FromThing.GetOptionsFor` ->
    // `Verse/ThingWithComps.GetFloatMenuOptions` -> `comps[i].CompFloatMenuOptions`.
    // The whole nine-clause chain, the confirmation problem and the StartJob
    // reproduction live in UsableSafe.cs; read its header first. This file is
    // the two verbs.
    //
    // THE TARGET SET IS NOT ITEMS. It is every `CompUsable`, BUILDINGS
    // INCLUDED: `CommsConsole`, `MechbandDish`, `BurnoutMechlinkBooster` and
    // `FirefoamPopper` all carry `CompProperties_Usable`, and
    // `CallBossgroupUtility.TryStartSummonBossgroupJob` ends in
    // `list[num].TryGetComp<CompUsable>().TryStartUseJob(pawn, null, forced)` on
    // one of the first three. Nothing below filters on `ThingCategory.Item`.
    //
    // WHAT WAS UNREACHABLE BEFORE THIS FILE. 23 shipped ThingDefs carry
    // `CompProperties_Usable` (Core/Biotech/Anomaly/Ideology/Odyssey), plus the
    // 41 that `RimWorld/ThingDefGenerator_Neurotrainer.cs` BUILDS IN CODE —
    // one `Neurotrainer_<SkillDef>` per skill (12) and one
    // `Psytrainer_<AbilityDef>` per psycast (29). Grepping the XML for
    // `Neurotrainer` finds only ThingCategoryDefs; they are not defs on disk.
    //
    // `consume` did not and could not cover any of it: `PawnOrderVerbs.Consume`
    // gates on `def.ingestible.showIngestFloatOption` and issues
    // `JobDefOf.Ingest`, while a usable thing's job is `Props.useJob` — per def,
    // never named here. See UsableSafe.MakeJob.
    internal static partial class PawnActs
    {
        // The verb's full argument list, beside the code that reads it, for
        // RefuseStray's message.
        private static readonly string[] UseArgs = { "pawn", "pawns", "thing", "queue" };

        // Every exit of `use` owes the journal a row (git-bug 4087644 comment
        // #1: a refused order journals the same way an accepted one does), and
        // `use` has eight of them.
        private static long UseJournalRow(Outcome outcome, Pawn pawn, Thing thing, JobDef useJob)
            => ActOn(outcome, "use", "use", Safe(() => thing.LabelShort) ?? thing.def?.defName,
                new Dictionary<string, object>
                {
                    ["pawn"] = pawn?.thingIDNumber,
                    ["thing"] = thing.thingIDNumber,
                    ["def"] = thing.def?.defName,
                    ["use_job"] = useJob?.defName,
                });

        // --------------------------------------------------------------------
        // use {pawn|pawns, thing, queue?}
        //
        // ONE user per call, like `equip`/`wear`/`consume`, and for a reason the
        // game states: clause 7 is `p.CanReserve(parent, 1, …)` — maxPawns 1 —
        // and `CompUseEffect_DestroySelf` splits one off the stack and destroys
        // it. A second pawn in the list is therefore REFUSED with its own gate
        // rather than silently dropped, which is what the three sibling verbs
        // do today.
        // --------------------------------------------------------------------
        [Verb("use")]
        public static object Use(VerbContext ctx)
        {
            const string V = "use";

            // PRE-MUTATION (git-bug c519477). `target` is the argument git-bug
            // 826d4bf adds, and a caller who passes it today would have it
            // dropped while the order ran without one — on a CompTargetable
            // item that means a job that completes and does nothing (the
            // 4087644 defect class). Refused, with the reason named.
            ctx.Args.RefuseStray(V, UseArgs,
                "Nothing was ordered. `use` has NO target argument yet: a thing whose effect is aimed at "
                + "something else (a resurrector serum on a corpse, a shock lance on a pawn) is refused "
                + "here with gate `needs-target` and is git-bug 826d4bf's half of this verb.");

            var map = Map();
            var pawns = PawnList(map, ctx.Args, true, "pawns", "pawn");
            var thing = ThingArg(map, ctx.Args, "thing");
            var outcome = new Outcome();
            var pawn = pawns[0];
            string label = Safe(() => thing.LabelShort) ?? thing.def?.defName;

            for (int i = 1; i < pawns.Count; i++)
                outcome.No(pawns[i], "one-user",
                    "one thing, one user: CompUsable.CanBeUsedBy reserves with maxPawns 1 and "
                    + "CompUseEffect_DestroySelf splits one off the stack, so only the first pawn in the "
                    + "list is ordered. Call `use` once per pawn.");

            var comp = UsableSafe.Comp(thing);
            if (comp == null)
            {
                bool ingestible = false;
                try
                {
                    ingestible = thing.def?.ingestible != null
                                 && thing.def.ingestible.showIngestFloatOption;
                }
                catch { }
                outcome.NoThing(thing, UsableSafe.GateNotUsable,
                    "this thing has no CompUsable, so the game offers no Use option for it"
                    + (ingestible
                        ? ". It IS ingestible and the game offers a consume option instead — use `consume`."
                        : ". `orders` lists what a pawn can be told to do WITH it as work."));
                return UseResult(V, outcome, UseJournalRow(outcome, pawn, thing, null), thing, label, null, null, null, null);
            }

            JobDef useJob = null;
            try { useJob = comp.Props.useJob; } catch { }
            if (useJob == null)
            {
                // CompFloatMenuOptions opens `if (Props.useJob == null) yield
                // break;` — the option is not merely disabled, it does not
                // exist. Nothing to reproduce.
                outcome.NoThing(thing, UsableSafe.GateNoUseJob,
                    "CompProperties_Usable.useJob is null on this def, and CompUsable.CompFloatMenuOptions "
                    + "yields NO option at all in that case — there is no player route to reproduce");
                return UseResult(V, outcome, UseJournalRow(outcome, pawn, thing, null), thing, label, comp, null, null, null);
            }

            // The float menu's FIRST act, before TryStartUseJob: the
            // SelectedUseOption sweep. See UsableSafe.TargeterRoute.
            var route = UsableSafe.TargeterRoute(comp, pawn, out var toClear);
            if (route != null)
            {
                outcome.No(pawn, route.Gate, route.Reason, route.Row());
                return UseResult(V, outcome, UseJournalRow(outcome, pawn, thing, useJob), thing, label, comp, null, null, null);
            }

            // The nine clauses, first refusal wins — the game's own order.
            var refusal = UsableSafe.Gate(comp, pawn);
            if (refusal != null)
            {
                outcome.No(pawn, refusal.Gate, refusal.Reason, refusal.Row());
                return UseResult(V, outcome, UseJournalRow(outcome, pawn, thing, useJob), thing, label, comp,
                    null, null, null);
            }

            // Pre-collected BEFORE the order, because after it the pawn's state
            // may already have moved. Transacted as accepted; disclosed below.
            var confirmations = UsableSafe.Confirmations(comp, pawn);

            // No IsForbidden check exists anywhere on this route (the only one
            // is FailOnDespawnedNullOrForbidden on JobDriver_UseItem's wait
            // toil), so un-forbid first exactly as equip/wear/consume do.
            bool wasForbidden = false;
            try { wasForbidden = thing.IsForbidden(Faction.OfPlayer); } catch { }
            try { thing.SetForbidden(value: false); } catch { }

            // The half of the SelectedUseOption sweep that IS safe to run: a
            // CompTargetable whose PlayerChoosesTarget is false does
            // `selectedTarget = null; return false;` and nothing else, and that
            // clear is vanilla behaviour on a field CompTargetable SCRIBES.
            for (int i = 0; i < toClear.Count; i++)
            {
                try { toClear[i].SelectedUseOption(pawn); } catch { }
            }

            var extra = UsableSafe.ExtraTarget(comp, pawn);
            bool queued = ctx.Args.Bool("queue", false);
            var job = UsableSafe.MakeJob(comp, pawn, extra);

            // 4087644 — PawnActs.AlreadyDoing.
            if (AlreadyDoing(pawn, job))
            {
                outcome.No(pawn, GateAlready, AlreadyWhy(queued), AlreadyLine(pawn, queued));
                return UseResult(V, outcome, UseJournalRow(outcome, pawn, thing, useJob), thing, label, comp,
                    confirmations, extra, null);
            }
            if (!TakeOrder(pawn, job, JobTag.Misc, queued, out var eff))
            {
                outcome.No(pawn, "refused",
                    "Pawn_JobTracker.TryTakeOrderedJob refused the job (pre-toil reservations failed)");
                return UseResult(V, outcome, UseJournalRow(outcome, pawn, thing, useJob), thing, label, comp,
                    confirmations, extra, null);
            }

            var line = Merge(JobLine(pawn), eff);
            if (wasForbidden) line["unforbidden"] = true;
            outcome.Ok(pawn, line);

            var warnings = new List<object>();
            string psylink = UsableSafe.PsylinkSensitivityWarning(comp, pawn);
            if (psylink != null) warnings.Add(psylink);

            long seq = UseJournalRow(outcome, pawn, thing, useJob);
            return UseResult(V, outcome, seq, thing, label, comp, confirmations, extra,
                warnings.Count > 0 ? warnings : null, pawn);
        }

        // Every exit's envelope, so a refusal and an acceptance describe the
        // target identically and no exit can quietly drop a disclosure.
        private static object UseResult(string V, Outcome outcome, long seq, Thing thing, string label,
            CompUsable comp, List<object> confirmations, LocalTargetInfo? extra, List<object> warnings,
            Pawn pawn = null)
        {
            var d = new Dictionary<string, object>
            {
                ["thing"] = thing.thingIDNumber,
                ["label"] = label,
                ["def"] = thing.def?.defName,
            };
            if (comp != null)
            {
                d["comp"] = comp.GetType().Name;
                d["use_job"] = SafeObj(() => comp.Props.useJob?.defName);
                d["use_duration"] = SafeObj(() => (object)comp.Props.useDuration);
                d["destroys_self"] = UsableSafe.DestroysSelf(comp);
                d["effects"] = UseEffectRows(comp);

                // ============== THE CONFIRMATION PROBLEM, DISCLOSED ==========
                // `CompUsable.TryStartUseJob` is NOT called: it ends in
                // `Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                // text, StartJob))` whenever any CompUseEffect has a
                // ConfirmMessage, and `CompUsableImplant` raises a SECOND
                // Dialog_MessageBox of its own (the royal-title violation)
                // before ever reaching base. Dialog_MessageBox sets
                // forcePause, so per spec 1.7 every subsequent `advance` halts
                // at 0 ticks with reason:"dialog" — and NO shipped verb can
                // press its Accept: `dialog-choose` needs a Dialog_NodeTree
                // (this is a plain Window with buttonAAction/buttonBAction) and
                // `dialog-dismiss` CANCELS. So the verb reproduces StartJob and
                // publishes the text instead.
                //
                // Published only on an exit that REACHED the order path: on a
                // refusal there was no modal to skip, and an empty
                // `confirmations` there would read as "we checked and there
                // were none" rather than "we never got that far".
                if (confirmations != null)
                {
                    d["dialogs_skipped"] = "CompUsable.TryStartUseJob's Dialog_MessageBox confirmation (and "
                        + "CompUsableImplant's separate royal-implant Yes/No) are transacted as ACCEPTED rather "
                        + "than opened as modals — spec 1.7: a force-pausing window wedges every later advance "
                        + "and dialog-choose cannot answer a Dialog_MessageBox. Their text is in `confirmations`.";
                    d["confirmations"] = confirmations;
                }

                string halt = AdvanceHalt(comp);
                if (halt != null) d["advance_halt"] = halt;

                string advisory = ManipulationAdvisory(comp, pawn);
                if (advisory != null) d["would_fail_on"] = advisory;
            }
            if (extra.HasValue && extra.Value.IsValid)
                d["extra_target"] = SafeObj(() => (object)(extra.Value.Thing?.thingIDNumber));
            if (warnings != null) d["warnings"] = warnings;
            d["note"] = "an accepted `use` is an ORDER, not an effect. "
                + "JobDriver_UseItem re-evaluates CompUsable.CanBeUsedBy continuously "
                + "(`this.FailOn(() => !comp.CanBeUsedBy(pawn))`, with forced:false regardless of what was "
                + "passed here), so the job can still die mid-run; and CompUsable.UsedBy runs the effect "
                + "comps in DESCENDING OrderPriority inside a try/catch that only Log.Errors. Advance, then "
                + "read the effect — not this envelope — for what happened.";
            return outcome.Result(V, seq, d);
        }

        // Named for what it is: a HALT the orchestrator will hit during the
        // advance that runs the job, not at order time.
        private static string AdvanceHalt(CompUsable comp)
        {
            var effects = UsableSafe.Effects(comp);
            for (int i = 0; i < effects.Count; i++)
            {
                if (effects[i] is CompUseEffect_FinishRandomResearchProject)
                    return "THIS USE WILL HALT THE NEXT ADVANCE. "
                        + "CompUseEffect_FinishRandomResearchProject.DoEffect calls "
                        + "ResearchManager.FinishProject(proj, doCompletionDialog: true), whose dialog block "
                        + "is `Find.WindowStack.Add(new Dialog_NodeTree(diaNode, delayInteractivity: true))` "
                        + "— forcePause, raised at DoEffect time, i.e. DURING the advance that runs the job. "
                        + "That one IS a Dialog_NodeTree, so `dialog-choose` answers it (unlike the "
                        + "Dialog_MessageBox on the order path). Expect advance to stop with reason:\"dialog\" "
                        + "and clear it with dialog-choose. Also note FinishProject recursively finishes "
                        + "unfinished PREREQUISITES and AddTechprints-satisfies techprint requirements, so one "
                        + "core can finish a chain, and it ends `if (currentProj == proj) currentProj = null`.";
            }
            return null;
        }

        // NOT a gate — an advisory. `FloatMenuOptionProvider_FromThing` has
        // `RequiresManipulation => false`, so the player is offered this order
        // for a manipulation-incapable pawn; the check is
        // `this.FailOnIncapable(PawnCapacityDefOf.Manipulation)` INSIDE
        // JobDriver_UseItem, i.e. the job is taken and then fails. Refusing up
        // front would fabricate a restriction (the 261f2e9 error inverted).
        //
        // Only published when the driver actually IS JobDriver_UseItem: the four
        // Anomaly serums run `Ingest`, whose driver has no such FailOnIncapable.
        private static string ManipulationAdvisory(CompUsable comp, Pawn pawn)
        {
            if (pawn == null) return null;
            if (SafeCapable(pawn, PawnCapacityDefOf.Manipulation)) return null;
            try
            {
                var driver = comp.Props.useJob?.driverClass;
                if (driver == null || !typeof(JobDriver_UseItem).IsAssignableFrom(driver)) return null;
            }
            catch { return null; }
            return "manipulation — the order was NOT refused (the float-menu provider's "
                + "RequiresManipulation is false, so the player gets this option too), but "
                + "JobDriver_UseItem.MakeNewToils opens with FailOnIncapable(PawnCapacityDefOf.Manipulation) "
                + "and this pawn is incapable, so the job will fail when it starts";
        }

        private static List<object> UseEffectRows(CompUsable comp)
        {
            var rows = new List<object>();
            var effects = UsableSafe.Effects(comp);
            for (int i = 0; i < effects.Count; i++)
            {
                var e = effects[i];
                rows.Add(new Dictionary<string, object>
                {
                    ["comp"] = e.GetType().Name,
                    ["gate"] = UsableSafe.EffectGate(e.GetType().Name),
                    ["order_priority"] = SafeObj(() => (object)e.OrderPriority),
                });
            }
            return rows;
        }

        // --------------------------------------------------------------------
        // use-options {pawn?, thing?, cap?}   READ-ONLY
        //
        // The `surgery-options` shape, for this surface: what the game would
        // offer, whether it is clickable, and — when it is not — the game's own
        // reason. Three forms, all one row shape:
        //
        //   {}                 every CompUsable thing on the map, no verdict
        //   {pawn}             the same list, each with THAT pawn's verdict
        //   {thing, pawn?}     one thing, in full
        //
        // AND IT PUBLISHES EVERY REFUSAL, NOT THE FIRST. `CompUsable.CanBeUsedBy`
        // short-circuits — it returns the first non-accepted report and stops —
        // so an agent peeling refusals off one `use` at a time pays one round
        // trip per clause. The act path stops where the game stops; this read
        // runs the whole chain and hands back the list.
        // --------------------------------------------------------------------
        [Verb("use-options")]
        public static object UseOptions(VerbContext ctx)
        {
            var map = Map();
            var pawns = PawnList(map, ctx.Args, false, "pawns", "pawn");
            Pawn pawn = pawns.Count > 0 ? pawns[0] : null;
            var one = ThingArg(map, ctx.Args, "thing", false);
            int cap = ctx.Args.Int("cap", 40);
            if (cap < 1 || cap > 300) throw new VerbArgsException("cap must be 1..300");

            // An explicit `thing` with no CompUsable must SAY so. Dropping it
            // and answering `options:[]` reads as "nothing is usable here",
            // which is a different fact.
            if (one != null && UsableSafe.Comp(one) == null)
                return new Dictionary<string, object>
                {
                    ["pawn"] = pawn?.thingIDNumber,
                    ["thing"] = one.thingIDNumber,
                    ["def"] = one.def?.defName,
                    ["options"] = new List<object>(),
                    ["total"] = 0,
                    ["more"] = 0,
                    ["usable"] = pawn != null ? (object)0 : null,
                    ["gate"] = UsableSafe.GateNotUsable,
                    ["reason"] = "this thing has no CompUsable, so the game offers no Use option for it"
                        + (SafeObj(() => (object)(one.def?.ingestible != null
                                                  && one.def.ingestible.showIngestFloatOption)) as bool? == true
                            ? ". It IS ingestible — `consume` is the verb."
                            : ""),
                };

            var candidates = new List<Thing>();
            if (one != null) candidates.Add(one);
            else
            {
                // Fog-filtered, like every player-facing read
                // (FloatMenuOptionProvider.IgnoreFogged is true, but
                // WorldSafe.Hidden is this project's standing rule and the
                // agent must not be handed things no player has seen).
                var all = map.listerThings.AllThings;
                for (int i = 0; i < all.Count; i++)
                {
                    var t = all[i];
                    if (t == null) continue;
                    if (UsableSafe.Comp(t) == null) continue;
                    bool hidden = true;
                    try { hidden = WorldSafe.Hidden(t, map); } catch { }
                    if (hidden) continue;
                    candidates.Add(t);
                }
                candidates.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
            }

            var rows = new List<object>();
            int total = 0, usable = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                var t = candidates[i];
                var comp = UsableSafe.Comp(t);
                if (comp == null) continue;
                total++;
                bool ok = false;
                var row = UseOptionRow(t, comp, pawn, out ok);
                if (ok) usable++;
                if (rows.Count < cap) rows.Add(row);
            }

            return new Dictionary<string, object>
            {
                ["pawn"] = pawn?.thingIDNumber,
                ["name"] = pawn != null ? PawnSafe.Name(pawn) : null,
                ["options"] = rows,
                ["total"] = total,
                ["more"] = Math.Max(0, total - rows.Count),
                ["usable"] = pawn != null ? (object)usable : null,
                ["note"] = pawn == null
                    ? "pass `pawn` for a verdict: without one this lists what CARRIES a CompUsable, not what "
                      + "anyone can use. Buildings are included — a CommsConsole and a MechbandDish are used "
                      + "the same way a neurotrainer is."
                    : "`usable:false` rows carry EVERY clause that refused, not just the first one — "
                      + "CompUsable.CanBeUsedBy short-circuits and `use` stops where it stops, but this read "
                      + "runs the whole chain. `reason_from` says whose words the reason is: two of the nine "
                      + "clauses return a bare false and have none of the game's own.",
            };
        }

        private static Dictionary<string, object> UseOptionRow(Thing t, CompUsable comp, Pawn pawn, out bool ok)
        {
            ok = false;
            var d = new Dictionary<string, object>
            {
                ["thing"] = t.thingIDNumber,
                ["def"] = t.def?.defName,
                ["label"] = Safe(() => t.LabelShort) ?? t.def?.defName,
                ["pos"] = SafeObj(() => Positions.Out(t.Position)),
                ["comp"] = comp.GetType().Name,
                ["use_label"] = SafeObj(() => (object)Safe(() => comp.Props.useLabel.Formatted(comp.parent).Resolve())),
                ["use_job"] = SafeObj(() => comp.Props.useJob?.defName),
                ["use_duration"] = SafeObj(() => (object)comp.Props.useDuration),
                ["destroys_self"] = UsableSafe.DestroysSelf(comp),
                ["effects"] = UseEffectRows(comp),
                // The SECOND player route, named as one we deliberately do not
                // drive: CompGetGizmosExtra's Command_Action calls
                // Find.Targeter.BeginTargeting(this) and consumes the next
                // click, which never comes on a headless bench.
                ["show_use_gizmo"] = SafeObj(() => (object)comp.Props.showUseGizmo),
                ["stack"] = SafeObj(() => (object)t.stackCount),
            };
            try
            {
                if (comp.Props.useJob == null)
                {
                    d["usable"] = false;
                    d["refusals"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["gate"] = UsableSafe.GateNoUseJob,
                            ["reason"] = "CompProperties_Usable.useJob is null; "
                                + "CompUsable.CompFloatMenuOptions yields no option at all",
                            ["reason_from"] = "autorimmer",
                        },
                    };
                    return d;
                }
            }
            catch { }

            if (pawn == null) return d;

            var refusals = new List<object>();
            var route = UsableSafe.TargeterRoute(comp, pawn, out _);
            if (route != null) refusals.Add(route.Row());
            var chain = UsableSafe.Refusals(comp, pawn, stopAtFirst: false);
            for (int i = 0; i < chain.Count; i++) refusals.Add(chain[i].Row());

            ok = refusals.Count == 0;
            d["usable"] = ok;
            d["refusals"] = refusals;
            d["confirmations"] = UsableSafe.Confirmations(comp, pawn);
            var extra = UsableSafe.ExtraTarget(comp, pawn);
            if (extra.IsValid) d["extra_target"] = SafeObj(() => (object)(extra.Thing?.thingIDNumber));
            string halt = AdvanceHalt(comp);
            if (halt != null) d["advance_halt"] = halt;
            string advisory = ManipulationAdvisory(comp, pawn);
            if (advisory != null) d["would_fail_on"] = advisory;
            return d;
        }
    }
}
