using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // ======================================================= spec 3.4 =========
    // EMERGENCY ORDERS — the "intervene by exception" surface.
    //
    // DESIGN §Observation model layer 4 says the agent delegates emergencies to
    // the colony brain and "intervenes by exception". Until this file there was
    // no exception verb: firefighting via work priorities does not cover a fire
    // that starts at 3am in the power room, and `Alert_FireInHomeArea` is
    // standing vanilla. The seven orders here are the session-4 amendment's
    // item 2, all M1:
    //
    //   extinguish       RimWorld/FloatMenuOptionProvider_ExtinguishFires.cs
    //   beat-fire        RimWorld/FloatMenuOptionProvider_PutOutFireOnPawn.cs
    //   tend             RimWorld/FloatMenuOptionProvider_DraftedTend.cs
    //   repair           RimWorld/FloatMenuOptionProvider_DraftedRepair.cs
    //   man-turret       RimWorld/CompMannable.cs CompFloatMenuOptions
    //   rest-until-healed RimWorld/Building_Bed.cs GetBedRestFloatMenuOption
    //   fire-at-will     RimWorld/Pawn_DraftController.cs GetGizmos (the toggle)
    //
    // Each reproduces its provider's Drafted/Undrafted/Multiselect/
    // RequiresManipulation flags and its own reject strings. Three of them are
    // DRAFTED-ONLY (tend, repair, and — via Building_Bed — rest is UNDRAFTED
    // only), which is precisely the class of bug the gate rule exists to catch:
    // without the gate the verb would look correct and silently do nothing.
    internal static partial class PawnActs
    {
        // --------------------------------------------------------------------
        // extinguish {pawns:[…], at:P}
        //
        // WIDGET GATE — RimWorld/FloatMenuOptionProvider_ExtinguishFires.cs:
        //   Drafted=true, Undrafted=true, Multiselect=TRUE, RequiresManipulation
        //   =true. The cell must be burning (TargetInfo.IsBurning), and
        //   PawnCanExtinguish (same file) rejects on the Firefighter work type
        //   being disabled, on no path to the cell OR to the first Fire in it,
        //   and on the cell being outside the pawn's allowed area.
        //
        // The job is JobDefOf.ExtinguishFiresNearby with EVERY fire near the
        // cell queued as a target (IntVec3.GetFiresNearCell) — one order, a
        // whole fire cluster. Multiselect is true, so the plural form here is
        // the game's own.
        //
        // Deviation, named: vanilla's out-of-area string interpolates
        // `EffectiveAreaRestrictionInPawnCurrentMap.Label`, whose getter
        // dereferences a null MapHeld (PawnSafe Class D); ours uses the
        // null-guarded sibling.
        // --------------------------------------------------------------------
        [Verb("extinguish")]
        public static object Extinguish(VerbContext ctx)
        {
            const string V = "extinguish";
            var map = Map();
            var pawns = PawnList(map, ctx.Args);
            var at = Positions.Resolve(map, ctx.Args.Raw("at")
                ?? throw new VerbArgsException("missing required arg 'at' (a position)"));
            var outcome = new Outcome();

            bool burning = false;
            try { burning = new TargetInfo(at, map).IsBurning(); } catch { }
            var fires = new List<Fire>();
            try { fires.AddRange(at.GetFiresNearCell(map)); } catch { }

            if (!burning)
                return outcome.Result(V, 0, new Dictionary<string, object>
                {
                    ["at"] = Positions.Out(at),
                    ["fires"] = 0,
                    ["error"] = "nothing is burning at or adjacent to that cell "
                        + "(TargetInfo.IsBurning); the game offers no extinguish option there",
                });

            var ids = new List<object>();
            foreach (var p in pawns)
            {
                if (!ProviderGate(p, drafted: true, undrafted: true,
                        mechanoidCanDo: false, requiresManipulation: true, out string gate, out string reason))
                { outcome.No(p, gate, reason); continue; }
                if (!CanExtinguish(p, at, out string g2, out string r2))
                { outcome.No(p, g2, r2); continue; }

                bool queued = ctx.Args.Bool("queue", false);
                var job = JobMaker.MakeJob(JobDefOf.ExtinguishFiresNearby);
                for (int i = 0; i < fires.Count; i++) job.AddQueuedTarget(TargetIndex.A, fires[i]);
                // 4087644 — PawnActs.AlreadyDoing. Worth knowing here in
                // particular: the queued targets added above are NOT part of the
                // comparison. Job.JobIsSameAs weighs def, verbToUse and bill and
                // then targetA/B/C — never targetQueueA — so a pawn already
                // running ExtinguishFiresNearby matches an order naming a
                // different set of fires. That is vanilla's own equivalence, and
                // it is exactly why the order would have been swallowed.
                if (AlreadyDoing(p, job))
                { outcome.No(p, GateAlready, AlreadyWhy(queued), AlreadyLine(p, queued)); continue; }
                if (!p.jobs.TryTakeOrderedJob(job, JobTag.Misc, queued))
                { outcome.No(p, "refused", "Pawn_JobTracker.TryTakeOrderedJob refused the job"); continue; }
                ids.Add(p.thingIDNumber);
                outcome.Ok(p, JobLine(p));
            }

            long seq = ActOn(outcome, V, "extinguish", $"({at.x},{at.z}) x{fires.Count}",
                      new Dictionary<string, object>
                      {
                          ["ids"] = ids,
                          ["at"] = Positions.Out(at),
                          ["fires"] = fires.Count,
                      });

            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["at"] = Positions.Out(at),
                ["fires"] = fires.Count,
                ["note"] = "one order covers every fire near the cell (IntVec3.GetFiresNearCell, queued onto "
                    + "one ExtinguishFiresNearby job) — this is the cluster form, not a per-fire order",
            });
        }

        // RimWorld/FloatMenuOptionProvider_ExtinguishFires.cs PawnCanExtinguish.
        private static bool CanExtinguish(Pawn p, IntVec3 cell, out string gate, out string reason)
        {
            gate = null; reason = null;
            try
            {
                if (p.WorkTypeIsDisabled(WorkTypeDefOf.Firefighter))
                {
                    gate = "firefighter-disabled";
                    reason = "incapable of " + WorkTypeDefOf.Firefighter.gerundLabel;
                    return false;
                }
                bool reach = p.CanReach(cell, PathEndMode.ClosestTouch, Danger.Deadly);
                if (!reach && cell.TryGetFirstThing<Fire>(p.Map, out var fire))
                    reach = p.CanReach(fire, PathEndMode.ClosestTouch, Danger.Deadly);
                if (!reach) { gate = "no-path"; reason = Tr("NoPath", "no path"); return false; }
                if (!cell.InAllowedArea(p))
                {
                    gate = "area";
                    reason = "outside the allowed area (" + AreaLabel(p) + ")";
                    return false;
                }
            }
            catch (Exception e) { gate = "exception"; reason = e.GetType().Name + ": " + e.Message; return false; }
            return true;
        }

        // --------------------------------------------------------------------
        // beat-fire {pawns:[…], target:<pawn id>}
        //
        // WIDGET GATE — RimWorld/FloatMenuOptionProvider_PutOutFireOnPawn.cs:
        //   Drafted=true, Undrafted=true, Multiselect=false. The clicked pawn
        //   must be alive and burning; it must share a faction with the doer OR
        //   share a host faction (so a colonist can beat the fire off a prisoner
        //   but not off a raider); the doer must be able to reach it; and the
        //   Fire ATTACHMENT must still exist (a pawn can stop burning between
        //   the check and the click).
        //
        // Distinct from `extinguish`, which handles fires on the GROUND — a
        // burning pawn carries a Fire attachment and needs JobDefOf.BeatFire.
        // --------------------------------------------------------------------
        [Verb("beat-fire")]
        public static object BeatFire(VerbContext ctx)
        {
            const string V = "beat-fire";
            var map = Map();
            var pawns = PawnList(map, ctx.Args);
            var t = ThingArg(map, ctx.Args, "target");
            if (!(t is Pawn target))
                throw new VerbArgsException($"'{V}' targets a burning PAWN; use `extinguish` for a ground fire");

            var outcome = new Outcome();
            bool onFire = false;
            try { onFire = !target.Dead && target.IsBurning(); } catch { }
            Thing fire = null;
            try { fire = target.GetAttachment(ThingDefOf.Fire); } catch { }

            if (!onFire || fire == null)
                return outcome.Result(V, 0, new Dictionary<string, object>
                {
                    ["target"] = target.thingIDNumber,
                    ["error"] = "that pawn is not burning (no Fire attachment); the game offers no beat-fire option",
                });

            var ids = new List<object>();
            foreach (var p in pawns)
            {
                if (!ProviderGate(p, drafted: true, undrafted: true,
                        mechanoidCanDo: false, requiresManipulation: false, out string gate, out string reason))
                { outcome.No(p, gate, reason); continue; }
                bool sameSide = false;
                try
                {
                    sameSide = (target.Faction != null && target.Faction == p.Faction)
                        || (target.HostFaction != null
                            && (target.HostFaction == p.Faction || target.HostFaction == p.HostFaction));
                }
                catch { }
                if (!sameSide)
                { outcome.No(p, "not-ours", "the game only offers this for a pawn of your faction or your prisoner/guest"); continue; }
                if (!CanReachThing(p, target, PathEndMode.Touch, out string noPath))
                { outcome.No(p, "no-path", noPath); continue; }
                bool queued = ctx.Args.Bool("queue", false);
                var job = JobMaker.MakeJob(JobDefOf.BeatFire, fire);
                // 4087644 — PawnActs.AlreadyDoing.
                if (AlreadyDoing(p, job))
                { outcome.No(p, GateAlready, AlreadyWhy(queued), AlreadyLine(p, queued)); continue; }
                if (!p.jobs.TryTakeOrderedJob(job, JobTag.Misc, queued))
                { outcome.No(p, "refused", "Pawn_JobTracker.TryTakeOrderedJob refused the job"); continue; }
                ids.Add(p.thingIDNumber);
                outcome.Ok(p, JobLine(p));
            }

            long seq = ActOn(outcome, V, "beat-fire", PawnSafe.Name(target),
                new Dictionary<string, object> { ["ids"] = ids, ["target"] = target.thingIDNumber });
            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["target"] = target.thingIDNumber,
                ["target_name"] = PawnSafe.Name(target),
            });
        }

        // --------------------------------------------------------------------
        // tend {pawn, target:<pawn id>, medicine?:bool=true}
        //
        // WIDGET GATE — RimWorld/FloatMenuOptionProvider_DraftedTend.cs:
        //   Drafted=TRUE, Undrafted=FALSE, Multiselect=false,
        //   MechanoidCanDo=true, RequiresManipulation=true, CanSelfTarget=TRUE.
        //   The drafted-only flag is the whole point of the verb: an UNDRAFTED
        //   doctor tends through work priorities, and this exists for the case
        //   where they will not (drafted for combat, or the patient is out of
        //   the way). IsValidTendTarget (same file) additionally allows a
        //   self-tend from an undrafted pawn — reproduced.
        //
        //   Then, in order: HasHediffsNeedingTend; the Doctor work type not
        //   disabled; a path; selfTend enabled when tending oneself; the patient
        //   not in an aggro mental state without Scaria.
        //
        //   `medicine:false` is the game's OWN second option ("Tend (without
        //   medicine)") and is offered only when medicine was found, the doctor
        //   can reserve the patient, and the patient is spawned.
        //
        // `job.draftedTend = true` is what tells the JobDriver this is the
        // drafted path; omitting it is the silent-wrong-behaviour bug here.
        // --------------------------------------------------------------------
        [Verb("tend")]
        public static object Tend(VerbContext ctx)
        {
            const string V = "tend";
            var map = Map();
            var doctor = PawnList(map, ctx.Args, true, "pawns", "pawn")[0];
            var t = ThingArg(map, ctx.Args, "target");
            if (!(t is Pawn patient))
                throw new VerbArgsException($"'{V}' targets a pawn");
            bool useMedicine = ctx.Args.Bool("medicine", true);
            var outcome = new Outcome();

            bool self = doctor == patient;
            // CanSelfTarget=true, so the self case skips the provider's
            // self-target rejection; the drafted flag still applies unless this
            // is a self-tend (IsValidTendTarget's first clause).
            if (!self && !ProviderGate(doctor, drafted: true, undrafted: false,
                    mechanoidCanDo: true, requiresManipulation: true, out string gate, out string reason))
            {
                outcome.No(doctor, gate, reason);
                return outcome.Result(V, 0, new Dictionary<string, object> { ["target"] = patient.thingIDNumber });
            }

            string g = null, r = null;
            Thing medicine = null;
            try
            {
                if (!ValidTendTarget(doctor, patient)) { g = "invalid-target"; r = "the game offers no drafted-tend option for this patient (FloatMenuOptionProvider_DraftedTend.IsValidTendTarget)"; }
                else if (!patient.health.HasHediffsNeedingTend()) { g = "not-needed"; r = Tr("TendingNotRequired", "tending not required"); }
                else if (doctor.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)) { g = "doctor-disabled"; r = "work type disabled for this pawn: " + WorkTypeDefOf.Doctor.gerundLabel; }
                else if (!CanReachThing(doctor, patient, PathEndMode.ClosestTouch, out r)) g = "no-path";
                else if (self && doctor.playerSettings != null && !doctor.playerSettings.selfTend)
                { g = "self-tend-off"; r = Tr("SelfTendDisabled", "self-tend is disabled for this pawn"); }
                else if (patient.InAggroMentalState && !patient.health.hediffSet.HasHediff(HediffDefOf.Scaria))
                { g = "aggro"; r = "the patient is in an aggressive mental state"; }
                else
                {
                    medicine = HealthAIUtility.FindBestMedicine(doctor, patient, onlyUseInventory: true);
                    if (!useMedicine) medicine = null;
                }
            }
            catch (Exception e) { g = "exception"; r = e.GetType().Name + ": " + e.Message; }

            if (g != null)
            {
                outcome.No(doctor, g, r);
                return outcome.Result(V, TendRow(outcome, V, doctor, patient, medicine),
                    new Dictionary<string, object> { ["target"] = patient.thingIDNumber });
            }

            bool queued = ctx.Args.Bool("queue", false);
            var job = JobMaker.MakeJob(JobDefOf.TendPatient, patient, medicine);
            job.count = 1;
            job.draftedTend = true;
            // 4087644 — PawnActs.AlreadyDoing. `count` is not one of the fields
            // JobIsSameAs compares, so an order that differs from the running
            // job only in count is still swallowed by vanilla's early-out.
            if (AlreadyDoing(doctor, job))
            {
                outcome.No(doctor, GateAlready, AlreadyWhy(queued), AlreadyLine(doctor, queued));
                return outcome.Result(V, TendRow(outcome, V, doctor, patient, medicine),
                    new Dictionary<string, object> { ["target"] = patient.thingIDNumber });
            }
            if (!doctor.jobs.TryTakeOrderedJob(job, JobTag.Misc, queued))
            {
                outcome.No(doctor, "refused", "Pawn_JobTracker.TryTakeOrderedJob refused the job");
                return outcome.Result(V, TendRow(outcome, V, doctor, patient, medicine));
            }
            outcome.Ok(doctor, JobLine(doctor));
            long seq = TendRow(outcome, V, doctor, patient, medicine);
            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["target"] = patient.thingIDNumber,
                ["target_name"] = PawnSafe.Name(patient),
                ["self"] = self,
                ["medicine"] = medicine?.def?.defName,
                ["medicine_id"] = medicine?.thingIDNumber,
                // FindBestMedicine is called with onlyUseInventory:true, which
                // is the provider's own call: the drafted-tend option uses what
                // the doctor is CARRYING, not what is in a stockpile, because a
                // drafted pawn is not going to go shopping.
                ["note"] = "medicine comes from the doctor's own inventory only "
                    + "(HealthAIUtility.FindBestMedicine onlyUseInventory:true); pass medicine:false to "
                    + "tend bare-handed, which is the game's own second option",
            });
        }

        // `tend`'s `action` row, shared by its refusal exits and its success
        // exit so a refused order journals the same way an accepted one does
        // (4087644 comment #1). ActOn decides whether a row is owed at all.
        private static long TendRow(Outcome outcome, string verb, Pawn doctor, Pawn patient, Thing medicine)
            => ActOn(outcome, verb, "tend", PawnSafe.Name(patient),
                new Dictionary<string, object>
                {
                    ["pawn"] = doctor.thingIDNumber,
                    ["target"] = patient.thingIDNumber,
                    ["medicine"] = medicine?.def?.defName,
                });

        // RimWorld/FloatMenuOptionProvider_DraftedTend.cs IsValidTendTarget.
        private static bool ValidTendTarget(Pawn doctor, Pawn patient)
        {
            try
            {
                if (!doctor.Drafted && patient != doctor) return false;
                if (patient.Downed) return true;
                if (patient.HostileTo(doctor.Faction)) return false;
                if (patient.IsColonist || patient.IsQuestLodger() || patient.IsPrisonerOfColony
                    || patient.IsSlaveOfColony
                    || (patient.Faction == Faction.OfPlayer && patient.IsAnimal)) return true;
                if (patient.IsColonySubhuman && patient.mutant != null && patient.mutant.Def.entitledToMedicalCare) return true;
            }
            catch { }
            return false;
        }

        // --------------------------------------------------------------------
        // repair {pawn, thing}
        //
        // WIDGET GATE — RimWorld/FloatMenuOptionProvider_DraftedRepair.cs:
        //   Drafted=TRUE, Undrafted=FALSE, Multiselect=false,
        //   MechanoidCanDo=true, RequiresManipulation=true; AppliesInt requires
        //   a skills tracker with Construction not TotallyDisabled;
        //   RepairUtility.PawnCanRepairNow(pawn, thing) is the substantive test;
        //   then a Touch path.
        // --------------------------------------------------------------------
        [Verb("repair")]
        public static object Repair(VerbContext ctx)
        {
            const string V = "repair";
            var map = Map();
            var pawn = PawnList(map, ctx.Args, true, "pawns", "pawn")[0];
            var thing = ThingArg(map, ctx.Args, "thing");
            var outcome = new Outcome();

            string g = null, r = null;
            if (!ProviderGate(pawn, drafted: true, undrafted: false,
                    mechanoidCanDo: true, requiresManipulation: true, out g, out r))
            {
                // ProviderGate already filled g/r.
            }
            else if (pawn.skills == null
                     || (SafeObj(() => (object)pawn.skills.GetSkill(SkillDefOf.Construction).TotallyDisabled) as bool?) == true)
            { g = "construction-disabled"; r = "construction is disabled for this pawn"; }
            else if ((SafeObj(() => (object)RepairUtility.PawnCanRepairNow(pawn, thing)) as bool?) != true)
            {
                g = "cannot-repair";
                r = "the game offers no repair option for this thing (RepairUtility.PawnCanRepairNow): "
                    + "it is not damaged, not the player's, or not repairable";
            }
            else if (!CanReachThing(pawn, thing, PathEndMode.Touch, out r)) g = "no-path";

            if (g != null)
            {
                outcome.No(pawn, g, r);
                return outcome.Result(V, RepairRow(outcome, V, pawn, thing),
                    new Dictionary<string, object> { ["thing"] = thing.thingIDNumber });
            }

            bool queued = ctx.Args.Bool("queue", false);
            var job = JobMaker.MakeJob(JobDefOf.Repair, thing);
            // 4087644 — PawnActs.AlreadyDoing.
            if (AlreadyDoing(pawn, job))
            {
                outcome.No(pawn, GateAlready, AlreadyWhy(queued), AlreadyLine(pawn, queued));
                return outcome.Result(V, RepairRow(outcome, V, pawn, thing),
                    new Dictionary<string, object> { ["thing"] = thing.thingIDNumber });
            }
            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, queued))
            {
                outcome.No(pawn, "refused", "Pawn_JobTracker.TryTakeOrderedJob refused the job");
                return outcome.Result(V, RepairRow(outcome, V, pawn, thing));
            }
            outcome.Ok(pawn, JobLine(pawn));
            long seq = RepairRow(outcome, V, pawn, thing);
            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["thing"] = thing.thingIDNumber,
                ["hp"] = thing.def != null && thing.def.useHitPoints ? (object)thing.HitPoints : null,
                ["max_hp"] = SafeObj(() => (object)thing.MaxHitPoints),
            });
        }

        // `repair`'s `action` row, shared by its refusal exits and its success
        // exit so a refused order journals the same way an accepted one does
        // (4087644 comment #1). ActOn decides whether a row is owed at all.
        private static long RepairRow(Outcome outcome, string verb, Pawn pawn, Thing thing)
            => ActOn(outcome, verb, "repair", Safe(() => thing.LabelShortCap.ToString()) ?? thing.def?.defName,
                new Dictionary<string, object> { ["pawn"] = pawn.thingIDNumber, ["thing"] = thing.thingIDNumber });

        // --------------------------------------------------------------------
        // man-turret {pawn, thing}
        //
        // WIDGET GATE — RimWorld/CompMannable.cs CompFloatMenuOptions (reached
        // through FloatMenuOptionProvider_FromThing, which is Drafted=true /
        // Undrafted=true / Multiselect=true):
        //   RaceProps.ToolUser; CanReserveAndReach at the InteractionCell;
        //   Props.manWorkType not disabled for the pawn (the Violent case is the
        //   one that gets a visible reason); and the Odyssey planet-layer
        //   whitelist.
        //
        // MannedNow is a ONE-TICK flag (CompMannable.ManForATick is called by
        // the job driver every tick the pawn stands there), so a result that
        // claimed "manned" would be lying: the pawn has to walk over first. The
        // echo is the JOB, not the state.
        // --------------------------------------------------------------------
        [Verb("man-turret")]
        public static object ManTurret(VerbContext ctx)
        {
            const string V = "man-turret";
            var map = Map();
            var pawns = PawnList(map, ctx.Args);
            var thing = ThingArg(map, ctx.Args, "thing");
            var outcome = new Outcome();

            var comp = (thing as ThingWithComps)?.GetComp<CompMannable>();
            if (comp == null)
                return outcome.Result(V, 0, new Dictionary<string, object>
                {
                    ["thing"] = thing.thingIDNumber,
                    ["error"] = "this thing has no CompMannable; the game offers no man option",
                });

            var ids = new List<object>();
            foreach (var p in pawns)
            {
                string g = null, r = null;
                try
                {
                    if (p.RaceProps == null || !p.RaceProps.ToolUser) { g = "not-tool-user"; r = "this pawn cannot operate machinery"; }
                    else if (!p.CanReserveAndReach(thing, PathEndMode.InteractionCell, Danger.Deadly))
                    { g = "unreachable"; r = "cannot reserve or reach the interaction cell"; }
                    else if (comp.Props.manWorkType != WorkTags.None && p.WorkTagIsDisabled(comp.Props.manWorkType))
                    {
                        g = "work-tag";
                        r = comp.Props.manWorkType == WorkTags.Violent
                            ? Tr("IsIncapableOfViolenceLower", "incapable of violence")
                            : "work tag disabled: " + comp.Props.manWorkType;
                    }
                    else if (comp.Props.planetLayerWhitelist != null && comp.Props.planetLayerWhitelist.Count > 0
                             && !comp.Props.planetLayerWhitelist.Contains(p.Map.Tile.LayerDef))
                    { g = "planet-layer"; r = "cannot function on this planet layer"; }
                }
                catch (Exception e) { g = "exception"; r = e.GetType().Name + ": " + e.Message; }

                if (g != null) { outcome.No(p, g, r); continue; }
                bool queued = ctx.Args.Bool("queue", false);
                var job = JobMaker.MakeJob(JobDefOf.ManTurret, thing);
                // 4087644 — PawnActs.AlreadyDoing.
                if (AlreadyDoing(p, job))
                { outcome.No(p, GateAlready, AlreadyWhy(queued), AlreadyLine(p, queued)); continue; }
                if (!p.jobs.TryTakeOrderedJob(job, JobTag.Misc, queued))
                { outcome.No(p, "refused", "Pawn_JobTracker.TryTakeOrderedJob refused the job"); continue; }
                ids.Add(p.thingIDNumber);
                outcome.Ok(p, JobLine(p));
            }

            long seq = ActOn(outcome, V, "man", Safe(() => thing.LabelShortCap.ToString()) ?? thing.def?.defName,
                new Dictionary<string, object> { ["ids"] = ids, ["thing"] = thing.thingIDNumber });
            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["thing"] = thing.thingIDNumber,
                ["manned_now"] = SafeObj(() => (object)comp.MannedNow),
                ["note"] = "CompMannable.MannedNow is true only within one tick of the pawn actually standing "
                    + "there (ManForATick), so it is false in this result by construction — advance and read it",
            });
        }

        // --------------------------------------------------------------------
        // rest-until-healed {pawns:[…], bed?:<id>}
        //
        // WIDGET GATE — RimWorld/Building_Bed.cs GetBedRestFloatMenuOption:
        //   the pawn must be Humanlike, the bed must be Medical and NOT
        //   ForPrisoners, the pawn must be UNDRAFTED, the bed must belong to the
        //   player faction, and RestUtility.CanUseBedEver must pass. Then
        //   HealthAIUtility.ShouldSeekMedicalRest gates the live option (with
        //   two distinct disabled texts — "no doctor" when a surgery bill is
        //   pending and nobody can do it, "not injured" otherwise), and a slave
        //   needs a ForSlaves bed.
        //   The action sets `job.restUntilHealed = true` — that flag is the
        //   whole difference between "lie down" and "stay until healed", and
        //   omitting it is the silent-wrong-behaviour bug here.
        //
        // With no `bed`, the game's own RestUtility.FindBedFor picks one; the
        // float menu is per-bed because the player clicked a bed.
        // --------------------------------------------------------------------
        [Verb("rest-until-healed")]
        public static object RestUntilHealed(VerbContext ctx)
        {
            const string V = "rest-until-healed";
            var map = Map();
            var pawns = PawnList(map, ctx.Args);
            Thing bedArg = ctx.Args.Has("bed") ? ThingArg(map, ctx.Args, "bed") : null;
            var outcome = new Outcome();
            var ids = new List<object>();

            foreach (var p in pawns)
            {
                var bed = bedArg as Building_Bed;
                if (bedArg != null && bed == null)
                { outcome.No(p, "not-a-bed", "the `bed` argument is not a bed"); continue; }
                if (bed == null) bed = FindBed(p, p, null);
                if (bed == null)
                { outcome.No(p, "no-bed", Tr("NoNonPrisonerBed", "no medical bed available")); continue; }

                string g = null, r = null;
                try
                {
                    if (p.RaceProps == null || !p.RaceProps.Humanlike) { g = "not-humanlike"; r = "the medical-bed option is offered to humanlikes only"; }
                    else if (p.Drafted) { g = "drafted"; r = "the medical-bed option is hidden while drafted (Building_Bed.GetBedRestFloatMenuOption)"; }
                    else if (bed.ForPrisoners || !bed.Medical) { g = "not-medical"; r = "that bed is not a non-prisoner medical bed"; }
                    else if (bed.Faction != Faction.OfPlayer) { g = "not-ours"; r = "that bed is not the colony's"; }
                    else if (!RestUtility.CanUseBedEver(p, bed.def)) { g = "cannot-use"; r = "this pawn can never use that bed"; }
                    else if (!HealthAIUtility.ShouldSeekMedicalRest(p))
                    {
                        g = "not-injured";
                        r = (p.health.surgeryBills.AnyShouldDoNow
                             && !WorkGiver_PatientGoToBedTreatment.AnyAvailableDoctorFor(p))
                            ? Tr("NoDoctor", "no doctor available")
                            : Tr("NotInjured", "not injured");
                    }
                    else if (p.IsSlaveOfColony && !bed.ForSlaves) { g = "not-for-slaves"; r = Tr("NotForSlaves", "that bed is not for slaves"); }
                    else if (!p.CanReserveAndReach(bed, PathEndMode.ClosestTouch, Danger.Deadly,
                                 bed.SleepingSlotsCount, -1, null, ignoreOtherReservations: true))
                    { g = "unreachable"; r = "cannot reserve or reach that bed"; }
                }
                catch (Exception e) { g = "exception"; r = e.GetType().Name + ": " + e.Message; }
                if (g != null) { outcome.No(p, g, r); continue; }

                // The provider's own two branches: already laying in this bed
                // means flip the flag on the running job rather than restart it.
                bool inPlace = false;
                try { inPlace = p.CurJobDef == JobDefOf.LayDown && p.CurJob.GetTarget(TargetIndex.A).Thing == bed; }
                catch { }
                if (inPlace)
                {
                    p.CurJob.restUntilHealed = true;
                }
                else
                {
                    bool queued = ctx.Args.Bool("queue", false);
                    var job = JobMaker.MakeJob(JobDefOf.LayDown, bed);
                    job.restUntilHealed = true;
                    // 4087644 — PawnActs.AlreadyDoing, on the ELSE branch only.
                    // The `inPlace` branch above is NOT a no-op: it flips
                    // restUntilHealed on the RUNNING job, which is a real
                    // mutation the provider itself performs, so its `accepted`
                    // is honest and reporting already-doing-it there would
                    // introduce a new lie rather than remove one. This branch
                    // reaches the collision a different way — a LayDown job in
                    // some OTHER bed, where JobIsSameAs still matches on def
                    // alone if the target happens to agree.
                    if (AlreadyDoing(p, job))
                    { outcome.No(p, GateAlready, AlreadyWhy(queued), AlreadyLine(p, queued)); continue; }
                    if (!p.jobs.TryTakeOrderedJob(job, JobTag.Misc, queued))
                    { outcome.No(p, "refused", "Pawn_JobTracker.TryTakeOrderedJob refused the job"); continue; }
                }
                try { p.mindState.ResetLastDisturbanceTick(); } catch { }
                ids.Add(p.thingIDNumber);
                var line = JobLine(p);
                line["bed"] = bed.thingIDNumber;
                line["already_in_bed"] = inPlace;
                outcome.Ok(p, line);
            }

            long seq = ActOn(outcome, V, "medical-rest", outcome.Count + " pawn(s)",
                new Dictionary<string, object> { ["ids"] = ids });
            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["note"] = "job.restUntilHealed is what makes this 'rest until healed' rather than 'lie down'",
            });
        }

        // --------------------------------------------------------------------
        // fire-at-will {pawns:[…], on:bool}
        //
        // WIDGET GATE — RimWorld/Pawn_DraftController.cs GetGizmos(): the
        // fire-at-will toggle exists only while `Drafted && equipment.Primary !=
        // null && Primary.def.IsRangedWeapon`. A verb that ignores that gate
        // would set a flag nothing reads.
        //
        // The setter (same file) cancels a Stance_Warmup when turned OFF — that
        // is how holding fire actually stops a shot already being aimed.
        //
        // Also worth knowing and echoed: the draft setter RESETS fireAtWill to
        // true on every draft/undraft transition, and Notify_PrimaryWeaponChanged
        // does the same on a weapon swap. So this is not a sticky preference.
        // --------------------------------------------------------------------
        [Verb("fire-at-will")]
        public static object FireAtWill(VerbContext ctx)
        {
            const string V = "fire-at-will";
            var map = Map();
            var pawns = PawnList(map, ctx.Args);
            bool on = ctx.Args.Bool("on", true);
            var outcome = new Outcome();
            var ids = new List<object>();

            foreach (var p in pawns)
            {
                if (p.drafter == null) { outcome.No(p, "no-drafter", "this pawn has no draft controller"); continue; }
                if (!p.Drafted) { outcome.No(p, "not-drafted", "the fire-at-will toggle exists only while drafted"); continue; }
                var primary = p.equipment?.Primary;
                if (primary == null || !primary.def.IsRangedWeapon)
                { outcome.No(p, "no-ranged-weapon", "the fire-at-will toggle exists only with a ranged primary weapon"); continue; }
                bool before = p.drafter.FireAtWill;
                if (before == on) { outcome.No(p, "already", "already " + (on ? "on" : "off")); continue; }
                p.drafter.FireAtWill = on;
                ids.Add(p.thingIDNumber);
                outcome.Ok(p, new Dictionary<string, object>
                {
                    ["before"] = before,
                    ["fire_at_will"] = p.drafter.FireAtWill,
                    ["weapon"] = primary.def.defName,
                });
            }

            long seq = ActOn(outcome, V, on ? "on" : "off", outcome.Count + " pawn(s)",
                new Dictionary<string, object> { ["ids"] = ids, ["on"] = on });
            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["on"] = on,
                ["note"] = "NOT a sticky preference: Pawn_DraftController resets fireAtWill to true on every "
                    + "draft/undraft transition and on a primary-weapon change (Notify_PrimaryWeaponChanged), "
                    + "so re-issue it after either",
            });
        }
    }
}
