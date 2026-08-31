using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // ======================================================= spec 3.4 =========
    // ORDERS — the right-click layer and the draft layer.
    //
    // Read PawnActs.cs's header first: the gate-lives-in-the-widget rule, the
    // observer-ban-vs-player-verb line, and the plural-is-the-verb rule are all
    // stated there and every verb below obeys them.
    //
    // Every order in this file ends in `Pawn_JobTracker.TryTakeOrderedJob` or
    // `TryTakeOrderedJobPrioritizedWork` (Verse.AI/Pawn_JobTracker.cs), never in
    // `StartJob`, so a mod's Harmony patch on the order path sees exactly the
    // traffic a player's click produces. Where the game ships a public static
    // that does the whole job (FloatMenuOptionProvider_DraftedMove.PawnGotoAction,
    // FloatMenuUtility.GetRangedAttackAction) this file CALLS IT rather than
    // reimplementing it — wrap, don't reinvent.
    //
    // QUEUEING. TryTakeOrderedJob decides between "interrupt now" and "append to
    // the queue" by reading `KeyBindingDefOf.QueueOrder.IsDownEvent` — live
    // keyboard state, which for an unattended bench is always false. The
    // `requestQueueing` parameter is the same switch without a keyboard, so
    // every order here takes `queue:true` and passes it through. Without that
    // the agent could never build a queue at all.
    internal static partial class PawnActs
    {
        // --------------------------------------------------------------------
        // draft {pawns:[…], queue?}  ·  undraft {pawns:[…]|"colonists"}
        //
        // THE PLURAL IS THE VERB: draft takes a list, and UNDRAFT IS ITS OWN OP
        // rather than `draft {drafted:false}`. That is not symmetry for its own
        // sake — the game's own scripted tutorial ships a dedicated `UndraftAll`
        // instruction and the `Drafting` concept warns that colonists left
        // drafted starve and get unhappy, so closing the loop has to be one
        // obvious call. `undraft {pawns:"colonists"}` is the whole-roster form.
        //
        // WIDGET GATE — RimWorld/Pawn_DraftController.cs GetGizmos():
        //   * the gizmo exists at all only when ShowDraftGizmo (a Biotech colony
        //     mech with no control group, and a non-player-controlled subhuman,
        //     get no draft button)
        //   * Disable("IsIncapped") when pawn.Downed
        //   * Disable("IsDeathresting") when pawn.Deathresting
        //   * Disable(report.Reason) from MechanitorUtility.CanDraftMech for a
        //     Biotech colony mech
        // A disabled gizmo cannot be clicked, so a downed drafted pawn cannot be
        // undrafted by hand in vanilla either — the game undrafts it itself via
        // Pawn_DraftController's AutoUndrafter. Reproduced faithfully; the
        // rejection names the gate so the agent is not left guessing.
        //
        // The SETTER is the whole mutation and it does a great deal more than
        // set a bool (same file): it clears mindState.priorityWork, resets
        // fireAtWill to true, releases destination reservations, clears the job
        // queue, ends the current job, drops a carried thing, notifies a
        // voluntarily-joinable lord, and clears animalsReleased on undraft. All
        // of that is the point — it is what the player's click does.
        // --------------------------------------------------------------------
        [Verb("draft")]
        public static object Draft(VerbContext ctx) => DraftImpl(ctx, true);

        [Verb("undraft")]
        public static object Undraft(VerbContext ctx) => DraftImpl(ctx, false);

        private static object DraftImpl(VerbContext ctx, bool want)
        {
            string verb = want ? "draft" : "undraft";
            var map = Map();
            var pawns = PawnList(map, ctx.Args);
            var outcome = new Outcome();
            var ids = new List<object>();

            foreach (var p in pawns)
            {
                if (p.drafter == null)
                {
                    outcome.No(p, "no-drafter", "this pawn has no draft controller (not draftable)");
                    continue;
                }
                if (!DraftGizmoOffered(p, out string gate, out string reason))
                {
                    outcome.No(p, gate, reason);
                    continue;
                }
                if (p.drafter.Drafted == want)
                {
                    outcome.No(p, "already", want ? "already drafted" : "already undrafted");
                    continue;
                }
                bool before = p.drafter.Drafted;
                p.drafter.Drafted = want;
                ids.Add(p.thingIDNumber);
                outcome.Ok(p, new Dictionary<string, object>
                {
                    ["drafted_before"] = before,
                    ["drafted"] = p.drafter.Drafted,
                    ["fire_at_will"] = SafeObj(() => (object)p.drafter.FireAtWill),
                    ["job"] = JobLine(p)["job"],
                    // The setter CLEARS priorityWork; say so rather than let a
                    // caller discover its prioritize order evaporated.
                    ["priority_work_cleared"] = true,
                });
            }

            long seq = ActOn(outcome, verb, want ? "drafted" : "undrafted",
                outcome.Count + " pawn(s)",
                new Dictionary<string, object> { ["ids"] = ids });

            return outcome.Result(verb, seq, new Dictionary<string, object>
            {
                ["note"] = want
                    ? "drafted pawns do not eat, sleep or work; undraft is its own verb "
                      + "(`undraft {pawns:\"colonists\"}`) and closing that loop is the caller's job"
                    : "Pawn_DraftController's setter also clears mindState.priorityWork and the job queue",
            });
        }

        // RimWorld/Pawn_DraftController.cs GetGizmos(), clause for clause.
        private static bool DraftGizmoOffered(Pawn p, out string gate, out string reason)
        {
            gate = null;
            reason = null;
            bool show = true;
            try { show = p.drafter.ShowDraftGizmo; } catch { }
            if (!show)
            {
                gate = "no-gizmo";
                reason = "the game offers no draft control for this pawn "
                    + "(Pawn_DraftController.ShowDraftGizmo: an uncontrolled colony mech or subhuman)";
                return false;
            }
            if (p.Downed)
            {
                gate = "downed";
                reason = Tr("IsIncapped", "incapacitated");
                return false;
            }
            bool deathresting = false;
            try { deathresting = p.Deathresting; } catch { }
            if (deathresting)
            {
                gate = "deathresting";
                reason = Tr("IsDeathresting", "deathresting");
                return false;
            }
            if (ModsConfig.BiotechActive)
            {
                try
                {
                    if (p.IsColonyMech)
                    {
                        var report = MechanitorUtility.CanDraftMech(p);
                        if (!report.Accepted)
                        {
                            gate = "mech";
                            reason = string.IsNullOrEmpty(report.Reason) ? "this mech cannot be drafted" : report.Reason;
                            return false;
                        }
                    }
                }
                catch { }
            }
            return true;
        }

        // --------------------------------------------------------------------
        // move-to {pawns:[…], to:P}
        //
        // WIDGET GATE — RimWorld/FloatMenuOptionProvider_DraftedMove.cs:
        //   Drafted=true, Undrafted=FALSE, Multiselect=true, MechanoidCanDo=true,
        //   and — uniquely in the whole provider set — IgnoreFogged=FALSE.
        //   That last one is why this is the ONE verb in 3.4 that accepts a
        //   fogged destination: the game lets a player send a drafted colonist
        //   into ground the colony has not explored, so this does too (DESIGN's
        //   fog rule is "mirror the player", not "hide everything").
        //   Then CellFinder.StandableCellNear(cell, map, 2.9f) must find a
        //   standable cell, and PawnCanGoto (same file, public static) answers
        //   the mech-command-range and no-path cases with an AcceptanceReport
        //   carrying the game's own string.
        //
        // DEVIATION, named: the multi-select branch drives
        // `Find.Selector.gotoController`, which is UI state (a drag interaction
        // that spreads the selection around the clicked cell). DESIGN forbids
        // driving widgets, so this calls the SINGLE-select path per pawn —
        // PawnGotoAction with RCellFinder.BestOrderedGotoDestNear, which is the
        // same spreading function the controller ultimately uses.
        // --------------------------------------------------------------------
        [Verb("move-to")]
        public static object MoveTo(VerbContext ctx)
        {
            const string V = "move-to";
            var map = Map();
            var pawns = PawnList(map, ctx.Args);
            var to = Positions.Resolve(map, ctx.Args.Raw("to")
                ?? throw new VerbArgsException("missing required arg 'to' (a position)"));

            var near = CellFinder.StandableCellNear(to, map, 2.9f);
            var outcome = new Outcome();
            if (!near.IsValid)
                return outcome.Result(V, 0, new Dictionary<string, object>
                {
                    ["to"] = Positions.Out(to),
                    ["error"] = "no standable cell within 2.9 of the destination "
                        + "(CellFinder.StandableCellNear); the game offers no GoHere option there either",
                });

            var ids = new List<object>();
            foreach (var p in pawns)
            {
                if (!ProviderGate(p, drafted: true, undrafted: false,
                        mechanoidCanDo: true, requiresManipulation: false, out string gate, out string reason))
                {
                    outcome.No(p, gate, reason);
                    continue;
                }
                if (near == p.Position)
                {
                    outcome.No(p, "already-there", "already standing on the destination cell");
                    continue;
                }
                AcceptanceReport can;
                try { can = FloatMenuOptionProvider_DraftedMove.PawnCanGoto(p, near); }
                catch (Exception e) { outcome.No(p, "exception", e.GetType().Name + ": " + e.Message); continue; }
                if (!can.Accepted)
                {
                    outcome.No(p, "cannot-goto", string.IsNullOrEmpty(can.Reason) ? "cannot go there" : can.Reason);
                    continue;
                }
                IntVec3 dest;
                try { dest = RCellFinder.BestOrderedGotoDestNear(near, p); }
                catch { dest = near; }
                FloatMenuOptionProvider_DraftedMove.PawnGotoAction(to, p, dest);
                ids.Add(p.thingIDNumber);
                var line = JobLine(p);
                line["dest"] = Positions.Out(dest);
                outcome.Ok(p, line);
            }

            long seq = ActOn(outcome, V, "goto", $"({to.x},{to.z})",
                new Dictionary<string, object> { ["ids"] = ids, ["to"] = Positions.Out(to) });

            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["to"] = Positions.Out(to),
                ["standable_near"] = Positions.Out(near),
                ["fogged_destination"] = SafeObj(() => (object)to.Fogged(map)),
                ["note"] = "a drafted pawn HOLDS this cell until it is given another order or undrafted; "
                    + "the destination may be fogged because FloatMenuOptionProvider_DraftedMove sets "
                    + "IgnoreFogged=false, unlike every other order provider",
            });
        }

        // --------------------------------------------------------------------
        // attack {pawns:[…], target:<thing id>, mode?:auto|ranged|melee}
        //
        // WIDGET GATE — RimWorld/FloatMenuOptionProvider_DraftedAttack.cs:
        //   Drafted=true, Undrafted=false, Multiselect=true, MechanoidCanDo=true;
        //   then the private CanTarget(clickedThing), reproduced below clause
        //   for clause; then the mech command-range check; then
        //   FloatMenuUtility.GetRangedAttackAction / GetMeleeAttackAction, which
        //   are public statics and are CALLED rather than reimplemented — they
        //   are what actually issues the job and they hand back the game's own
        //   failStr.
        //
        // This is the consumer of Blockers' `removal:"attack"` (DESIGN decisions
        // log 2026-08-30): a building that is neither mineable nor
        // deconstructible has to be beaten down by a drafted colonist, and this
        // is that verb. The result echoes `removal` for the target so the join
        // is visible from either side.
        //
        // MANHUNTERS (2.2's note on this issue): a manhunter pack classifies as
        // `wildlife`, not `hostile`, because PawnSafe.Classify tests
        // RaceProps.Animal before HostileTo. So `pawns {filter:"hostile"}` does
        // NOT surface every threat, and neither does HostileTo here — which is
        // exactly why CanTarget's third clause (`Pawn && NonHumanlikeOrWildMan`)
        // matters: an ordinary animal IS attackable regardless of hostility, so
        // this verb can be pointed at a manhunter that no hostility test found.
        // The result publishes the target's mental state for the same reason.
        // --------------------------------------------------------------------
        [Verb("attack")]
        public static object Attack(VerbContext ctx)
        {
            const string V = "attack";
            var map = Map();
            var pawns = PawnList(map, ctx.Args);
            var target = ThingArg(map, ctx.Args, "target");
            string mode = ctx.Args.Str("mode", "auto");
            if (mode != "auto" && mode != "ranged" && mode != "melee")
                throw new VerbArgsException("mode must be auto|ranged|melee");

            var outcome = new Outcome();
            Blockers.Classify(target, out string removal, out string blockReason);
            var targetInfo = new Dictionary<string, object>
            {
                ["id"] = target.thingIDNumber,
                ["def"] = target.def?.defName,
                ["label"] = Safe(() => target.LabelShortCap.ToString()),
                ["at"] = Positions.Out(target.PositionHeld),
                ["hostile"] = SafeObj(() => (object)target.HostileTo(Faction.OfPlayer)),
                ["removal"] = removal,
                ["removal_reason"] = blockReason,
                ["hp"] = target.def != null && target.def.useHitPoints ? (object)target.HitPoints : null,
                ["mental"] = (target as Pawn)?.MentalStateDef?.defName,
            };

            if (!CanDraftAttack(target, out string why))
                return outcome.Result(V, 0, new Dictionary<string, object>
                {
                    ["target"] = targetInfo,
                    ["error"] = why,
                });

            var ids = new List<object>();
            foreach (var p in pawns)
            {
                if (!ProviderGate(p, drafted: true, undrafted: false,
                        mechanoidCanDo: true, requiresManipulation: false, out string gate, out string reason))
                {
                    outcome.No(p, gate, reason);
                    continue;
                }
                if (p == target)
                {
                    outcome.No(p, "self", "a pawn cannot be ordered to attack itself");
                    continue;
                }
                if (ModsConfig.BiotechActive)
                {
                    bool inRange = true;
                    try { if (p.IsColonyMech) inRange = MechanitorUtility.InMechanitorCommandRange(p, target); }
                    catch { }
                    if (!inRange)
                    {
                        outcome.No(p, "command-range", Tr("OutOfCommandRange", "out of mechanitor command range"));
                        continue;
                    }
                }

                string failStr = null;
                Action action = null;
                string chosen = null;
                bool useRanged = false;
                try { useRanged = FloatMenuUtility.UseRangedAttack(p); } catch { }

                if (mode != "melee" && (mode == "ranged" || useRanged))
                {
                    try { action = FloatMenuUtility.GetRangedAttackAction(p, target, out failStr); }
                    catch (Exception e) { failStr = e.GetType().Name + ": " + e.Message; }
                    if (action != null) chosen = "ranged";
                }
                if (action == null && mode != "ranged")
                {
                    string meleeFail = null;
                    try { action = FloatMenuUtility.GetMeleeAttackAction(p, target, out meleeFail); }
                    catch (Exception e) { meleeFail = e.GetType().Name + ": " + e.Message; }
                    if (action != null) chosen = "melee";
                    else if (!string.IsNullOrEmpty(meleeFail)) failStr = meleeFail;
                }

                if (action == null)
                {
                    outcome.No(p, "no-attack-action",
                        string.IsNullOrEmpty(failStr) ? "the game offers no attack option here" : failStr);
                    continue;
                }
                // 4087644. `attack` cannot take the pre-check the other job
                // verbs take, because it never builds the Job:
                // FloatMenuUtility.GetRangedAttackAction / GetMeleeAttackAction
                // hand back a delegate that constructs and takes the order
                // internally, and wrapping the game's own static beats
                // reimplementing it (this file's header, the wrap-don't-reinvent
                // rule). So the collision is caught BEHAVIOURALLY instead: what
                // the pawn was doing before the delegate ran, against what it is
                // doing after.
                //
                // THIS IS A PROBE, NOT A PREDICTION, and the difference is not
                // cosmetic. Unchanged state cannot distinguish "was already
                // doing it" from "the game's delegate declined silently" — both
                // look identical from out here, and the reason string says so
                // rather than picking one. Do NOT unify this with
                // PawnActs.AlreadyDoing in a later pass: the pre-check knows
                // WHY the order did nothing, this only knows THAT it did.
                int beforeJob = -1, beforeQueue = -1;
                try
                {
                    beforeJob = p.jobs?.curJob?.loadID ?? -1;
                    beforeQueue = p.jobs?.jobQueue?.Count ?? -1;
                }
                catch { }
                action();
                int afterJob = -1, afterQueue = -1;
                try
                {
                    afterJob = p.jobs?.curJob?.loadID ?? -1;
                    afterQueue = p.jobs?.jobQueue?.Count ?? -1;
                }
                catch { }
                if (beforeJob == afterJob && beforeQueue == afterQueue)
                {
                    var same = JobLine(p);
                    same["attack"] = chosen;
                    same["job_id_before"] = beforeJob;
                    same["queued_before"] = beforeQueue;
                    outcome.No(p, GateAlready,
                        "the order changed nothing — the pawn's current job (id " + beforeJob
                        + ") and its job queue are identical after it. Either the pawn was already "
                        + "running an equivalent job, in which case "
                        + "Pawn_JobTracker.TryTakeOrderedJob returns true without taking the order "
                        + "(Job.JobIsSameAs against curJob), or the game's own attack delegate "
                        + "declined silently. This is a before/after probe, so it cannot tell those "
                        + "two apart; it can only tell you nothing happened.",
                        same);
                    continue;
                }
                ids.Add(p.thingIDNumber);
                var line = JobLine(p);
                line["attack"] = chosen;
                outcome.Ok(p, line);
            }

            long seq = ActOn(outcome, V, "attack", Safe(() => target.LabelShortCap.ToString()) ?? target.def?.defName,
                new Dictionary<string, object>
                {
                    ["ids"] = ids,
                    ["target"] = target.thingIDNumber,
                    ["removal"] = removal,
                });

            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["target"] = targetInfo,
                ["mode"] = mode,
                ["note"] = "fire-at-will is a separate standing toggle (`fire-at-will`); this order is one "
                    + "attack job and a pawn that finishes or is interrupted returns to whatever its "
                    + "drafted posture says",
            });
        }

        // RimWorld/FloatMenuOptionProvider_DraftedAttack.cs CanTarget(Thing) —
        // private in the game, so reproduced clause for clause here.
        private static bool CanDraftAttack(Thing t, out string why)
        {
            why = null;
            try
            {
                if (t.def.noRightClickDraftAttack && t.HostileTo(Faction.OfPlayer))
                {
                    why = "this thing is flagged noRightClickDraftAttack; the game offers no attack option";
                    return false;
                }
                if (t.def.IsNonDeconstructibleAttackableBuilding) return true;
                var bp = t.def.building;
                if (bp != null && bp.quickTargetable) return true;
                if (!t.def.destroyable)
                {
                    why = "this thing is not destroyable";
                    return false;
                }
                if (t.HostileTo(Faction.OfPlayer)) return true;
                if (t is Pawn p && p.NonHumanlikeOrWildMan()) return true;
            }
            catch (Exception e)
            {
                why = e.GetType().Name + ": " + e.Message;
                return false;
            }
            why = "the game offers no drafted-attack option for this target "
                + "(not hostile, not an animal or wild man, not an attackable non-deconstructible building)";
            return false;
        }

        // --------------------------------------------------------------------
        // orders {pawn, thing?|cell?, cap?}   NOT READ-ONLY — see the cost note
        //
        // The parity list: every WorkGiver order the right-click menu would show
        // for this pawn on this target, with the game's own label and, where the
        // option would be disabled, the game's own reason.
        //
        // This exists because the session-4 amendment's item 3(a) is a fact
        // about breadth: WorkGiverDef.directOrderable defaults TRUE
        // (RimWorld/WorkGiverDef.cs), so essentially every WorkGiver_Scanner on
        // a 38-mod bench is a right-click order. A `prioritize` verb with a
        // hand-written list of four job types would have been a guess; this
        // enumerates what the game actually offers, so `prioritize {work:…}` is
        // never a shot in the dark.
        //
        // WHAT ASKING COSTS. This header said "read-only" until git-bug
        // 32b9e01, and it was wrong. There is no way to ask the game what a
        // pawn could do here without RUNNING each scanner's HasJobOn* / JobOn*
        // with forced:true — the same call the float menu makes when it opens —
        // and vanilla's own side effects come with it. Two survive even with
        // the makingFor fix below, and both are disclosed rather than
        // suppressed, because both are what the player's right-click does too:
        //
        //  * A BILL CAN BE DELETED. RimWorld/WorkGiver_DoBill.cs JobOnThing
        //    calls billGiver.BillStack.RemoveIncompletableBills(), and
        //    RimWorld/BillStack.cs RemoveIncompletableBills does
        //    bills.Remove(bill) + billGiver.Notify_BillDeleted(bill) for every
        //    bill whose CompletableEver is false — a surgery whose body part is
        //    already gone, say. Genuine click-parity: opening the float menu on
        //    that bench deletes it too.
        //  * A JOB ID IS CONSUMED PER CANDIDATE. Every non-null JobOn* comes
        //    from Verse/JobMaker.cs MakeJob, which takes SimplePool<Job>.Get()
        //    and Find.UniqueIDsManager.GetNextJobID(); nextJobID is
        //    `Scribe_Values.Look(ref nextJobID, "nextJobID", 0)`
        //    (RimWorld/UniqueIDsManager.cs). A probe therefore inflates a
        //    counter that is written to the save, permanently. Harmless, and
        //    the precise reason "read-only" was never strictly true.
        //  * EVERY BILL ON THE MAP IS RE-EVALUATED, AND THAT WRITES `paused`.
        //    NOT in git-bug 32b9e01 — found while writing its acceptance, and
        //    it is the widest of the three. RimWorld/WorkGiver_DoBill.cs
        //    ShouldSkip walks `listerThings.ThingsInGroup(PotentialBillGiver)`
        //    asking each for `BillStack.AnyShouldDoNow`, which is
        //    `bills[i].ShouldDoNow()` over the stack (RimWorld/BillStack.cs) —
        //    and RimWorld/Bill_Production.cs ShouldDoNow WRITES `paused`:
        //    unconditionally false when the repeat mode is not TargetCount,
        //    true when pauseWhenSatisfied and the count is met, false again
        //    under unpauseWhenYouHave. `paused` is
        //    `Scribe_Values.Look(ref paused, "paused", false)`, same file. It
        //    short-circuits on the first giver with a doable bill, so the worst
        //    case is a colony with nothing doable — where one `orders` call
        //    touches every bill on the map. Note the asymmetry this creates and
        //    do not let it read as an inconsistency: `bills` (spec 2.4) refuses
        //    to call ShouldDoNow AT ALL for exactly this reason and derives the
        //    same answer from the stored fields (WorldSafe Class A). It can
        //    refuse because it chooses its own route. This verb cannot: the
        //    call is inside vanilla's JobOnThing, there is no hook, and
        //    declining it means declining to ask the question. Click-parity,
        //    like the two above — the float menu writes exactly the same flags.
        //
        // What the makingFor fix DID close — a colony-wide bill cooldown, an
        // RNG burn, a danger-threshold divergence and the missing reasons — is
        // documented at ScanWorkGivers below.
        //
        // Not free either: bounded by the WorkGiverDef count rather than by map
        // size, and since WorkGiverDef.directOrderable defaults true this runs
        // arbitrary THIRD-PARTY JobOnThing implementations on a 38-mod bench.
        // Vanilla's own worst case is destructive, so there is no basis for
        // assuming mods are better behaved and no practical way to audit them
        // all. That is an argument for this disclosure, not for hiding it.
        // --------------------------------------------------------------------
        [Verb("orders")]
        public static object Orders(VerbContext ctx)
        {
            var map = Map();
            var pawn = PawnList(map, ctx.Args, true, "pawns", "pawn")[0];
            int cap = ctx.Args.Int("cap", 30);
            if (cap < 1 || cap > 200) throw new VerbArgsException("cap must be 1..200");

            LocalTargetInfo target = ResolveWorkTarget(map, ctx.Args, out Thing thing, out IntVec3 cell);

            var available = new List<object>();
            var blocked = new List<object>();
            int total = 0, availableTotal = 0, blockedTotal = 0;
            string fatal = ScanWorkGivers(pawn, target, thing != null,
                (giver, scanner, job, label, why) =>
                {
                    total++;
                    if (why == null) availableTotal++; else blockedTotal++;
                    var line = new Dictionary<string, object>
                    {
                        ["work"] = giver.defName,
                        ["work_type"] = giver.workType?.defName,
                        ["label"] = label,
                        ["job_def"] = job?.def?.defName,
                        ["sustains"] = giver.prioritizeSustains,
                        ["while_drafted"] = giver.canBeDoneWhileDrafted,
                    };
                    if (why == null) { if (available.Count < cap) available.Add(line); }
                    else { line["reason"] = why; if (blocked.Count < cap) blocked.Add(line); }
                });

            return new Dictionary<string, object>
            {
                ["pawn"] = pawn.thingIDNumber,
                ["name"] = PawnSafe.Name(pawn),
                ["drafted"] = pawn.Drafted,
                ["target"] = thing != null
                    ? (object)new Dictionary<string, object>
                    {
                        ["kind"] = "thing",
                        ["id"] = thing.thingIDNumber,
                        ["def"] = thing.def?.defName,
                        ["label"] = Safe(() => thing.LabelShortCap.ToString()),
                        ["at"] = Positions.Out(thing.PositionHeld),
                    }
                    : new Dictionary<string, object> { ["kind"] = "cell", ["at"] = Positions.Out(cell) },
                ["available"] = available,
                // Truncation is a contract (2.1's rule, kept): the WALK covers
                // every WorkGiverDef, only the printed lists are capped, and
                // what was dropped is published rather than implied.
                ["available_total"] = availableTotal,
                ["available_more"] = Math.Max(0, availableTotal - available.Count),
                ["blocked"] = blocked,
                ["blocked_total"] = blockedTotal,
                ["blocked_more"] = Math.Max(0, blockedTotal - blocked.Count),
                ["total"] = total,
                ["unavailable_reason"] = fatal,
                ["note"] = "WorkGiverDef.directOrderable defaults true, so this list is as wide as the "
                    + "bench's WorkGiver set; pass a `work` value from it to `prioritize`. "
                    + "NO JOB IS TAKEN, but asking is not read-only: this runs the game's own "
                    + "work-giver scan, so a bill that can never be completed is deleted exactly as "
                    + "opening the float menu on that bench would delete it, one job id is "
                    + "consumed per candidate job (the id counter is saved), and every bill on the "
                    + "map may be re-evaluated, which rewrites each bill's stored `paused` flag. "
                    + "All three are exactly what the player's right-click does. What is NOT done "
                    + "any more: setting the bills' ingredient-search cooldown, which used to "
                    + "suppress a bill colony-wide for 500-600 ticks per call.",
            };
        }

        // --------------------------------------------------------------------
        // prioritize {pawn, work:<WorkGiverDef>, thing?:<id> | cell?:P, queue?}
        //
        // The right-click "Prioritize <x>" order, in BOTH its forms — a THING
        // target and a CELL target. The cell form is not a convenience: it is a
        // separate branch of the game's own provider
        // (FloatMenuOptionProvider_WorkGivers.GetOptions, which passes
        // context.ClickedCell, vs GetOptionsFor, which passes the clicked
        // Thing), and it is how "sow this area" and "mine this area" are
        // ordered. A prioritize verb with only a thing form cannot express them.
        //
        // WIDGET GATE — RimWorld/FloatMenuOptionProvider_WorkGivers.cs
        // GetWorkGiverOption, reproduced clause for clause in ScanWorkGivers
        // below. Every rejection carries the game's own translated string.
        //
        // Shares `orders`' scan, so it shares its costs — see the cost note on
        // `orders` above. They matter less here (this verb mutates by design)
        // but they are the same calls.
        //
        // ECHO THE DURABLE STATE, NOT A HOPE. This routes through
        // Pawn_JobTracker.TryTakeOrderedJobPrioritizedWork, which — when
        // `giver.def.prioritizeSustains` — calls
        // `pawn.mindState.priorityWork.Set(cell, giver.def)`. That is SCRIBED
        // state with a 30000-tick timeout (Verse/PriorityWork.cs), not a
        // one-shot job: it survives save/load, it keeps pulling the pawn back to
        // that cell, and drafting the pawn is what clears it. So the result
        // reports `priority_work` as state, and `sustains` says whether this
        // particular work giver writes it at all.
        // --------------------------------------------------------------------
        [Verb("prioritize")]
        public static object Prioritize(VerbContext ctx)
        {
            const string V = "prioritize";
            var map = Map();
            var pawn = PawnList(map, ctx.Args, true, "pawns", "pawn")[0];
            string workName = ctx.Args.StrReq("work");
            bool queue = ctx.Args.Bool("queue", false);
            var giverDef = Dev.Named<WorkGiverDef>(workName, "work");

            LocalTargetInfo target = ResolveWorkTarget(map, ctx.Args, out Thing thing, out IntVec3 cell);

            if (!EverWork(pawn, out string noWork))
                throw new VerbArgsException(noWork);

            Job job = null;
            WorkGiver_Scanner scanner = null;
            string label = null, why = null;
            bool matched = false;
            string fatal = ScanWorkGivers(pawn, target, thing != null,
                (g, s, j, l, w) =>
                {
                    if (g != giverDef) return;
                    matched = true;
                    job = j; scanner = s; label = l; why = w;
                });

            if (fatal != null)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["work"] = giverDef.defName,
                    ["reason"] = fatal,
                    ["action"] = NoStamp(),
                };
            if (!matched)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["work"] = giverDef.defName,
                    ["reason"] = "the game offers no option for this work giver on this target "
                        + "(not directOrderable, not a WorkGiver_Scanner, the scanner skipped the target, "
                        + "or — if the pawn is drafted — canBeDoneWhileDrafted is false). "
                        + "Call `orders` for what IS offered.",
                    ["drafted"] = pawn.Drafted,
                    ["while_drafted"] = giverDef.canBeDoneWhileDrafted,
                    ["action"] = NoStamp(),
                };
            if (job == null || why != null)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["work"] = giverDef.defName,
                    ["label"] = label,
                    // The game's own disabled-option text, verbatim.
                    ["reason"] = why ?? "the work giver produced no job",
                    ["action"] = NoStamp(),
                };

            // The provider stamps the giver def on the job before ordering it;
            // TryTakeOrderedJobPrioritizedWork does it again, but doing it here
            // too keeps the traffic byte-identical to the click.
            job.workGiverDef = scanner.def;
            bool took;
            if (queue)
            {
                // TryTakeOrderedJob's queue branch is keyboard-gated
                // (KeyBindingDefOf.QueueOrder.IsDownEvent); requestQueueing is
                // the same switch without a keyboard. Note it does NOT set
                // priorityWork — TryTakeOrderedJobPrioritizedWork is the only
                // path that does, and it does not take a queueing flag.
                took = pawn.jobs.TryTakeOrderedJob(job, scanner.def.tagToGive, requestQueueing: true);
            }
            else
            {
                took = pawn.jobs.TryTakeOrderedJobPrioritizedWork(job, scanner, cell);
            }

            if (!took)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["work"] = giverDef.defName,
                    ["label"] = label,
                    ["reason"] = "Pawn_JobTracker.TryTakeOrderedJob refused the job "
                        + "(pre-toil reservations failed)",
                    ["action"] = NoStamp(),
                };

            long seq = Act(V, "prioritize", label,
                new Dictionary<string, object>
                {
                    ["pawn"] = pawn.thingIDNumber,
                    ["work"] = giverDef.defName,
                    ["job_def"] = job.def?.defName,
                    ["target"] = thing != null ? (object)thing.thingIDNumber : Positions.Out(cell),
                });

            var data = JobLine(pawn);
            data["verb"] = V;
            data["ok"] = true;
            data["pawn"] = pawn.thingIDNumber;
            data["name"] = PawnSafe.Name(pawn);
            data["work"] = giverDef.defName;
            data["work_type"] = giverDef.workType?.defName;
            data["label"] = label;
            data["queued"] = queue;
            // The honest half: `sustains` is what decides whether anything
            // DURABLE was written, and priority_work is that state read back.
            data["sustains"] = giverDef.prioritizeSustains;
            data["priority_work"] = queue ? null : PriorityWorkLine(pawn);
            data["action"] = Stamp(seq);
            data["note"] = giverDef.prioritizeSustains && !queue
                ? "prioritizeSustains is TRUE for this work giver, so mindState.priorityWork was written: "
                  + "durable, scribed, 30000-tick state that keeps pulling this pawn back to that cell. "
                  + "`clear-priority-work` or drafting the pawn clears it."
                : "prioritizeSustains is false for this work giver (or the order was queued), so this is a "
                  + "one-shot job and nothing durable was written";
            return data;
        }

        // --------------------------------------------------------------------
        // clear-priority-work {pawns:[…]}
        //
        // The other half of prioritize's durable state. WIDGET GATE —
        // Verse/PriorityWork.cs GetGizmos(): the "Clear prioritized work"
        // command is offered only when something is prioritized OR the current
        // job is player-forced and interruptible OR the queue holds a forced
        // job, AND the pawn is neither Drafted nor Deathresting.
        // --------------------------------------------------------------------
        [Verb("clear-priority-work")]
        public static object ClearPriorityWork(VerbContext ctx)
        {
            const string V = "clear-priority-work";
            var map = Map();
            var pawns = PawnList(map, ctx.Args);
            var outcome = new Outcome();
            var ids = new List<object>();

            foreach (var p in pawns)
            {
                var pw = p.mindState?.priorityWork;
                if (pw == null) { outcome.No(p, "no-mindstate", "this pawn has no priorityWork state"); continue; }
                if (p.Drafted) { outcome.No(p, "drafted", "the clear-prioritized-work command is hidden while drafted"); continue; }
                bool deathresting = false;
                try { deathresting = p.Deathresting; } catch { }
                if (deathresting) { outcome.No(p, "deathresting", Tr("IsDeathresting", "deathresting")); continue; }

                bool anything = false;
                try
                {
                    anything = pw.IsPrioritized
                        || (p.CurJob != null && p.CurJob.playerForced && p.jobs.IsCurrentJobPlayerInterruptible())
                        || p.jobs.jobQueue.AnyPlayerForced;
                }
                catch { }
                if (!anything) { outcome.No(p, "nothing", "nothing prioritized and no player-forced job to clear"); continue; }

                var before = PriorityWorkLine(p);
                pw.ClearPrioritizedWorkAndJobQueue();
                try
                {
                    if (p.CurJob != null && p.CurJob.playerForced && p.jobs.IsCurrentJobPlayerInterruptible())
                        p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
                catch { }
                ids.Add(p.thingIDNumber);
                var line = JobLine(p);
                line["before"] = before;
                line["after"] = PriorityWorkLine(p);
                outcome.Ok(p, line);
            }

            long seq = ActOn(outcome, V, "clear", outcome.Count + " pawn(s)",
                new Dictionary<string, object> { ["ids"] = ids });
            return outcome.Result(V, seq);
        }

        // `equip` / `wear` / `consume` each have refusal exits that return
        // early, so their `action` row is factored out here and called from
        // every exit — a refused order journals the same way an accepted one
        // does (4087644 comment #1). ActOn decides whether a row is owed.
        private static long EquipRow(Outcome outcome, string verb, string label, Pawn pawn, Thing thing)
            => ActOn(outcome, verb, "equip", label,
                new Dictionary<string, object> { ["pawn"] = pawn.thingIDNumber, ["thing"] = thing.thingIDNumber });

        private static long WearRow(Outcome outcome, string verb, Pawn pawn, Apparel apparel)
            => ActOn(outcome, verb, "wear", Safe(() => apparel.LabelShort) ?? apparel.def?.defName,
                new Dictionary<string, object> { ["pawn"] = pawn.thingIDNumber, ["thing"] = apparel.thingIDNumber });

        private static long ConsumeRow(Outcome outcome, string verb, Pawn pawn, Thing thing, int count)
            => ActOn(outcome, verb, "ingest", Safe(() => thing.LabelShort) ?? thing.def?.defName,
                new Dictionary<string, object>
                {
                    ["pawn"] = pawn.thingIDNumber,
                    ["thing"] = thing.thingIDNumber,
                    ["count"] = count,
                });

        // ===================== rescue / capture / arrest / carry ==============
        // Four distinct orders that all end in "one colonist takes one downed
        // pawn to one bed". Each reproduces its own provider, and each takes a
        // list of DOERS — the real plural axis, since the target is one pawn.

        // WIDGET GATE — RimWorld/FloatMenuOptionProvider_RescuePawn.cs.
        [Verb("rescue")]
        public static object Rescue(VerbContext ctx) => TakeToBed(ctx, "rescue");

        // WIDGET GATE — RimWorld/FloatMenuOptionProvider_CapturePawn.cs.
        [Verb("capture")]
        public static object Capture(VerbContext ctx) => TakeToBed(ctx, "capture");

        // WIDGET GATE — RimWorld/FloatMenuOptionProvider_Arrest.cs.
        //
        // ITS ACTION DELEGATE IS NOT CALLED, and that is load-bearing: the
        // vanilla action ends in
        // `TutorUtility.DoModalDialogIfNotKnown(ConceptDefOf.ArrestingCreatesEnemies, …)`,
        // which adds a Dialog_MessageBox (forcePause = true) on any save where
        // the concept has not been demonstrated — and
        // PlayerKnowledgeDatabase.IsComplete has no tutor-enabled short-circuit,
        // so tutorial settings do not save you. Per spec 1.7 that halts every
        // subsequent `advance` at 0 ticks with reason:"dialog", permanently,
        // until 3.5 ships dialog routing. TakeToBedGate reproduces the gate and
        // TakeToBed takes the job itself; only the tutorial line is dropped.
        [Verb("arrest")]
        public static object Arrest(VerbContext ctx) => TakeToBed(ctx, "arrest");

        // WIDGET GATE — RimWorld/FloatMenuOptionProvider_CarryPawn.cs.
        [Verb("carry")]
        public static object Carry(VerbContext ctx) => TakeToBed(ctx, "carry");

        private static object TakeToBed(VerbContext ctx, string kind)
        {
            var map = Map();
            var doers = PawnList(map, ctx.Args);
            var t = ThingArg(map, ctx.Args, "target");
            if (!(t is Pawn target))
                throw new VerbArgsException($"'{kind}' targets a pawn; thing {t.thingIDNumber} is a {t.def?.defName}");

            var outcome = new Outcome();
            var ids = new List<object>();
            foreach (var doer in doers)
            {
                if (doer == target) { outcome.No(doer, "self", "a pawn cannot do this to itself"); continue; }
                // Every one of the four providers is RequiresManipulation=true;
                // carry is the only one that is Drafted-only.
                if (!ProviderGate(doer, drafted: true, undrafted: kind != "carry",
                        mechanoidCanDo: false, requiresManipulation: true, out string gate, out string reason))
                {
                    outcome.No(doer, gate, reason);
                    continue;
                }
                if (!TakeToBedGate(kind, doer, target, out string g2, out string r2, out Building_Bed bed, out JobDef jobDef))
                {
                    outcome.No(doer, g2, r2);
                    continue;
                }
                Job job = bed != null
                    ? JobMaker.MakeJob(jobDef, target, bed)
                    : JobMaker.MakeJob(jobDef, target);
                job.count = 1;
                if (kind == "carry")
                {
                    // The provider un-forbids the target first; a forbidden
                    // downed pawn is otherwise silently unreachable.
                    try { target.SetForbidden(value: false, warnOnFail: false); } catch { }
                }
                bool queued = ctx.Args.Bool("queue", false);
                // 4087644 — PawnActs.AlreadyDoing. All four kinds share this
                // site, so all four get the gate.
                if (AlreadyDoing(doer, job))
                {
                    outcome.No(doer, GateAlready, AlreadyWhy(queued), AlreadyLine(doer, queued));
                    continue;
                }
                if (!doer.jobs.TryTakeOrderedJob(job, JobTag.Misc, queued))
                {
                    outcome.No(doer, "refused", "Pawn_JobTracker.TryTakeOrderedJob refused the job "
                        + "(pre-toil reservations failed)");
                    continue;
                }
                ids.Add(doer.thingIDNumber);
                var line = JobLine(doer);
                if (bed != null) line["bed"] = bed.thingIDNumber;
                outcome.Ok(doer, line);
            }

            long seq = ActOn(outcome, kind, kind, PawnSafe.Name(target),
                new Dictionary<string, object> { ["ids"] = ids, ["target"] = target.thingIDNumber });

            return outcome.Result(kind, seq, new Dictionary<string, object>
            {
                ["target"] = new Dictionary<string, object>
                {
                    ["id"] = target.thingIDNumber,
                    ["name"] = PawnSafe.Name(target),
                    ["class"] = PawnSafe.Classify(target),
                    ["downed"] = target.Downed,
                    ["faction"] = target.Faction?.def?.defName,
                },
            });
        }

        // The per-kind clauses, each from its own provider file. `bed` is the
        // second job target where the game finds one; a null bed with an
        // accepted gate means the job takes only the pawn (carry).
        private static bool TakeToBedGate(string kind, Pawn doer, Pawn target,
            out string gate, out string reason, out Building_Bed bed, out JobDef jobDef)
        {
            gate = null; reason = null; bed = null; jobDef = null;
            try
            {
                switch (kind)
                {
                    case "rescue":
                        jobDef = JobDefOf.Rescue;
                        if (!HealthAIUtility.CanRescueNow(doer, target, forced: true))
                        { gate = "cannot-rescue"; reason = "the game offers no rescue option (HealthAIUtility.CanRescueNow)"; return false; }
                        if (target.mindState.WillJoinColonyIfRescued)
                        { gate = "will-join"; reason = "this pawn joins the colony if rescued; the game hides the plain rescue option"; return false; }
                        if (target.IsPrisonerOfColony || target.IsSlaveOfColony || target.IsColonyMech)
                        { gate = "not-rescuable"; reason = "prisoners, slaves and colony mechs are not rescued"; return false; }
                        if (target.Faction != null && target.Faction.HostileTo(Faction.OfPlayer))
                        { gate = "hostile"; reason = "a hostile pawn is captured, not rescued"; return false; }
                        if (ChildcareUtility.CanSuckle(target, out _))
                        { gate = "baby"; reason = "babies are carried to safety, not rescued"; return false; }
                        // The provider reads ageTracker.CurLifeStage here. That
                        // is PawnSafe Class C for an OBSERVER; a player verb
                        // reproduces the click, and the click reads it.
                        if (!(HealthAIUtility.ShouldSeekMedicalRest(target) || !target.ageTracker.CurLifeStage.alwaysDowned))
                        { gate = "no-rest-needed"; reason = Tr("TendingNotRequired", "does not need medical rest"); return false; }
                        if (target.playerSettings != null && target.playerSettings.medCare == MedicalCareCategory.NoCare)
                        { gate = "no-care"; reason = Tr("MedicalCareDisabled", "medical care is disabled for this pawn"); return false; }
                        bed = FindBed(target, doer, null);
                        if (bed == null)
                        {
                            gate = "no-bed";
                            reason = Tr(target.RaceProps.Animal ? "NoAnimalBed" : "NoNonPrisonerBed", "no suitable bed");
                            return false;
                        }
                        return true;

                    case "capture":
                        jobDef = JobDefOf.Capture;
                        if (!target.CanBeCaptured())
                        { gate = "cannot-capture"; reason = "the game offers no capture option (GenAI.CanBeCaptured)"; return false; }
                        if (!HealthAIUtility.CanRescueNow(doer, target, forced: true))
                        { gate = "cannot-rescue"; reason = "not reachable or not in a capturable state (HealthAIUtility.CanRescueNow)"; return false; }
                        bed = FindBed(target, doer, GuestStatus.Prisoner);
                        if (bed == null) { gate = "no-bed"; reason = Tr("NoPrisonerBed", "no prisoner bed"); return false; }
                        return true;

                    case "arrest":
                        jobDef = JobDefOf.Arrest;
                        if (!target.CanBeArrestedBy(doer))
                        { gate = "cannot-arrest"; reason = "the game offers no arrest option (GenAI.CanBeArrestedBy)"; return false; }
                        if (target.Downed && target.guilt.IsGuilty)
                        { gate = "downed-guilty"; reason = "a downed guilty pawn is captured, not arrested"; return false; }
                        if (!doer.Drafted && (!target.IsWildMan() || target.IsPrisonerOfColony))
                        { gate = "drafted-only"; reason = "arrest is offered to an undrafted colonist only for a free wild man"; return false; }
                        if (doer.InSameExtraFaction(target, ExtraFactionType.HomeFaction)
                            || doer.InSameExtraFaction(target, ExtraFactionType.MiniFaction))
                        { gate = "same-faction"; reason = Tr("SameFaction", "same faction"); return false; }
                        if (!CanReachThing(doer, target, PathEndMode.OnCell, out string noPath))
                        { gate = "no-path"; reason = noPath; return false; }
                        bed = FindBed(target, doer, GuestStatus.Prisoner);
                        if (bed == null) { gate = "no-bed"; reason = Tr("NoPrisonerBed", "no prisoner bed"); return false; }
                        return true;

                    case "carry":
                        jobDef = JobDefOf.CarryDownedPawnDrafted;
                        if (!target.Downed && !target.IsSelfShutdown())
                        { gate = "not-downed"; reason = "only a downed or self-shut-down pawn can be carried"; return false; }
                        if (!CanReachThing(doer, target, PathEndMode.ClosestTouch, out string noPath2))
                        { gate = "no-path"; reason = noPath2; return false; }
                        return true;
                }
            }
            catch (Exception e)
            {
                gate = "exception";
                reason = e.GetType().Name + ": " + e.Message;
                return false;
            }
            gate = "unknown";
            reason = "unknown order kind";
            return false;
        }

        // The two-pass bed search every take-to-bed provider does: honour
        // reservations first, then ignore them (RestUtility.FindBedFor).
        private static Building_Bed FindBed(Pawn sleeper, Pawn traveler, GuestStatus? status)
        {
            try
            {
                var bed = RestUtility.FindBedFor(sleeper, traveler, checkSocialProperness: false,
                    ignoreOtherReservations: false, status);
                return bed ?? RestUtility.FindBedFor(sleeper, traveler, checkSocialProperness: false,
                    ignoreOtherReservations: true, status);
            }
            catch { return null; }
        }

        // ===================== equip / wear / drop / consume ==================

        // WIDGET GATE — RimWorld/FloatMenuOptionProvider_Equip.cs. Multiselect
        // is FALSE there (one weapon, one pawn), so this verb is singular by the
        // game's own shape rather than by omission.
        [Verb("equip")]
        public static object Equip(VerbContext ctx)
        {
            const string V = "equip";
            var map = Map();
            var pawn = PawnList(map, ctx.Args, true, "pawns", "pawn")[0];
            var thing = ThingArg(map, ctx.Args, "thing");
            var outcome = new Outcome();

            if (pawn.equipment == null)
            {
                outcome.No(pawn, "no-equipment", "this pawn has no equipment tracker");
                return outcome.Result(V, 0);
            }
            if (!thing.HasComp<CompEquippable>())
            {
                outcome.NoThing(thing, "not-equippable", "this thing has no CompEquippable");
                return outcome.Result(V, 0);
            }
            string label = Safe(() => thing.LabelShort) ?? thing.def?.defName;
            string gate = null, reason = null;
            if (thing.def.IsWeapon && pawn.WorkTagIsDisabled(WorkTags.Violent))
            { gate = "violence"; reason = Tr("IsIncapableOfViolenceLower", "incapable of violence"); }
            else if (thing.def.IsRangedWeapon && pawn.WorkTagIsDisabled(WorkTags.Shooting))
            { gate = "shooting"; reason = Tr("IsIncapableOfShootingLower", "incapable of shooting"); }
            else if (!CanReachThing(pawn, thing, PathEndMode.ClosestTouch, out reason)) gate = "no-path";
            else if (!SafeCapable(pawn, PawnCapacityDefOf.Manipulation))
            { gate = "manipulation"; reason = Tr("Incapable", "incapable"); }
            else if (thing.IsBurning()) { gate = "burning"; reason = Tr("BurningLower", "burning"); }
            else if (pawn.IsQuestLodger() && !EquipmentUtility.QuestLodgerCanEquip(thing, pawn))
            { gate = "quest"; reason = Tr("QuestRelated", "quest-related"); }
            else if (!EquipmentUtility.CanEquip(thing, pawn, out string cantReason, checkBonded: false))
            { gate = "cannot-equip"; reason = cantReason; }

            if (gate != null)
            {
                outcome.No(pawn, gate, reason);
                return outcome.Result(V, EquipRow(outcome, V, label, pawn, thing),
                    new Dictionary<string, object> { ["thing"] = thing.thingIDNumber, ["label"] = label });
            }

            // The provider's Equip() local: un-forbid, then order.
            try { thing.SetForbidden(value: false); } catch { }
            bool queued = ctx.Args.Bool("queue", false);
            var job = JobMaker.MakeJob(JobDefOf.Equip, thing);
            // 4087644 — PawnActs.AlreadyDoing.
            if (AlreadyDoing(pawn, job))
            {
                outcome.No(pawn, GateAlready, AlreadyWhy(queued), AlreadyLine(pawn, queued));
                return outcome.Result(V, EquipRow(outcome, V, label, pawn, thing),
                    new Dictionary<string, object> { ["thing"] = thing.thingIDNumber, ["label"] = label });
            }
            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, queued))
            {
                outcome.No(pawn, "refused", "Pawn_JobTracker.TryTakeOrderedJob refused the job");
                return outcome.Result(V, EquipRow(outcome, V, label, pawn, thing));
            }
            outcome.Ok(pawn, JobLine(pawn));
            long seq = EquipRow(outcome, V, label, pawn, thing);
            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["thing"] = thing.thingIDNumber,
                ["label"] = label,
                // The provider's action delegate is NOT called. It can raise two
                // Dialog_MessageBoxes — a bladelink already bonded to another
                // weapon, and a persona-weapon confirmation — and
                // Dialog_MessageBox sets forcePause, which per spec 1.7 halts
                // EVERY subsequent advance at 0 ticks with reason:"dialog",
                // permanently, until 3.5 ships dialog routing. Both are pure
                // confirmations of an order the caller already gave, so they are
                // transacted as accepted and DISCLOSED here.
                ["dialogs_skipped"] = "bladelink-already-bonded and persona-weapon confirmations are "
                    + "transacted as accepted rather than opened as modals (spec 1.7: one force-pausing "
                    + "window wedges every later advance and nothing can clear it yet)",
            });
        }

        // WIDGET GATE — RimWorld/FloatMenuOptionProvider_Wear.cs.
        [Verb("wear")]
        public static object Wear(VerbContext ctx)
        {
            const string V = "wear";
            var map = Map();
            var pawn = PawnList(map, ctx.Args, true, "pawns", "pawn")[0];
            var thing = ThingArg(map, ctx.Args, "thing");
            var outcome = new Outcome();

            if (pawn.apparel == null)
            {
                outcome.No(pawn, "no-apparel", "this pawn has no apparel tracker");
                return outcome.Result(V, 0);
            }
            if (!(thing is Apparel apparel))
            {
                outcome.NoThing(thing, "not-apparel", "this thing is not apparel");
                return outcome.Result(V, 0);
            }
            string gate = null, reason = null;
            if (!CanReachThing(pawn, apparel, PathEndMode.ClosestTouch, out reason)) gate = "no-path";
            else if (apparel.IsBurning()) { gate = "burning"; reason = Tr("Burning", "burning"); }
            else if (pawn.apparel.WouldReplaceLockedApparel(apparel))
            { gate = "locked"; reason = Tr("WouldReplaceLockedApparel", "would replace locked apparel"); }
            else if (!ApparelUtility.HasPartsToWear(pawn, apparel.def))
            { gate = "body-parts"; reason = Tr("CannotWearBecauseOfMissingBodyParts", "missing the body parts to wear this"); }
            else if (!EquipmentUtility.CanEquip(apparel, pawn, out string cantReason)) { gate = "cannot-wear"; reason = cantReason; }

            if (gate != null)
            {
                outcome.No(pawn, gate, reason);
                return outcome.Result(V, WearRow(outcome, V, pawn, apparel),
                    new Dictionary<string, object> { ["thing"] = apparel.thingIDNumber });
            }

            try { apparel.SetForbidden(value: false); } catch { }
            bool queued = ctx.Args.Bool("queue", false);
            var job = JobMaker.MakeJob(JobDefOf.Wear, apparel);
            // 4087644 — PawnActs.AlreadyDoing. THIS IS THE VERB THAT BIT US:
            // marine armour force-worn on four pawns whose apparel policy
            // independently wanted the same armour, `accepted:1` on all four,
            // three armoured and one not, and no way to tell which mechanism
            // dressed whom. `forced` on the apparel row (PawnSerializer.Apparel)
            // is the durable other half of that answer.
            if (AlreadyDoing(pawn, job))
            {
                outcome.No(pawn, GateAlready, AlreadyWhy(queued), AlreadyLine(pawn, queued));
                return outcome.Result(V, WearRow(outcome, V, pawn, apparel),
                    new Dictionary<string, object> { ["thing"] = apparel.thingIDNumber });
            }
            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, queued))
            {
                outcome.No(pawn, "refused", "Pawn_JobTracker.TryTakeOrderedJob refused the job");
                return outcome.Result(V, WearRow(outcome, V, pawn, apparel));
            }
            outcome.Ok(pawn, JobLine(pawn));
            long seq = WearRow(outcome, V, pawn, apparel);
            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["thing"] = apparel.thingIDNumber,
                // The provider's action delegate is NOT called: for a mechanitor
                // dropping bandwidth apparel it routes through
                // MechanitorUtility.TryConfirmBandwidthLossFromDroppingThing,
                // which adds a Dialog_MessageBox.CreateConfirmation —
                // force-pausing, and per spec 1.7 unrecoverable on this bench.
                ["dialogs_skipped"] = "the mechanitor bandwidth-loss confirmation is transacted as accepted "
                    + "rather than opened as a modal (spec 1.7)",
                ["note"] = "a FORCED wear survives the outfit policy: OutfitForcedHandler keeps it on until "
                    + "the pawn takes it off, so `assign {apparel_policy:…}` will not undress this piece",
            });
        }

        // WIDGET GATE — RimWorld/FloatMenuOptionProvider_DropEquipment.cs. The
        // provider only ever targets the pawn itself and only its PRIMARY, so
        // that is the whole shape; the plural axis is the pawn list.
        [Verb("drop")]
        public static object Drop(VerbContext ctx)
        {
            const string V = "drop";
            var map = Map();
            var pawns = PawnList(map, ctx.Args);
            var outcome = new Outcome();
            var ids = new List<object>();

            foreach (var p in pawns)
            {
                var primary = p.equipment?.Primary;
                if (primary == null) { outcome.No(p, "no-primary", "this pawn carries no primary equipment"); continue; }
                if (p.IsQuestLodger() && !EquipmentUtility.QuestLodgerCanUnequip(primary, p))
                { outcome.No(p, "quest", Tr("QuestRelated", "quest-related")); continue; }
                bool queued = ctx.Args.Bool("queue", false);
                var job = JobMaker.MakeJob(JobDefOf.DropEquipment, primary);
                // 4087644 — PawnActs.AlreadyDoing.
                if (AlreadyDoing(p, job))
                { outcome.No(p, GateAlready, AlreadyWhy(queued), AlreadyLine(p, queued)); continue; }
                if (!p.jobs.TryTakeOrderedJob(job, JobTag.Misc, queued))
                { outcome.No(p, "refused", "Pawn_JobTracker.TryTakeOrderedJob refused the job"); continue; }
                ids.Add(p.thingIDNumber);
                var line = JobLine(p);
                line["dropped"] = primary.thingIDNumber;
                line["dropped_def"] = primary.def?.defName;
                outcome.Ok(p, line);
            }

            long seq = ActOn(outcome, V, "drop-primary", outcome.Count + " pawn(s)",
                new Dictionary<string, object> { ["ids"] = ids });
            return outcome.Result(V, seq);
        }

        // WIDGET GATE — RimWorld/FloatMenuOptionProvider_Ingest.cs. Reproduced
        // down to the stack count: FoodUtility.GetMaxAmountToPickup returning 0
        // is what makes the game's own option unclickable, and that is the one
        // clause a naive implementation drops.
        [Verb("consume")]
        public static object Consume(VerbContext ctx)
        {
            const string V = "consume";
            var map = Map();
            var pawn = PawnList(map, ctx.Args, true, "pawns", "pawn")[0];
            var thing = ThingArg(map, ctx.Args, "thing");
            var outcome = new Outcome();

            string gate = null, reason = null;
            if (thing.def.ingestible == null || !thing.def.ingestible.showIngestFloatOption)
            { gate = "not-ingestible"; reason = "the game offers no consume option for this def"; }
            else if (!thing.IngestibleNow || !pawn.RaceProps.CanEverEat(thing.def))
            { gate = "cannot-eat"; reason = "this pawn cannot ever eat this"; }
            else if (!thing.def.IsDrug && !thing.def.ingestible.nonDrugIngestibleWithoutFoodNeed
                     && !pawn.FoodIsSuitable(thing.def))
            { gate = "food-unsuitable"; reason = Tr("FoodNotSuitable", "food not suitable"); }
            else if (thing.def.IsDrug && !pawn.DrugIsSuitable(thing.def))
            { gate = "drug-unsuitable"; reason = Tr("DrugNotSuitable", "drug not suitable"); }
            else if (thing.def.IsNonMedicalDrug && !pawn.CanTakeDrug(thing.def))
            { gate = "drug-desire"; reason = "this pawn's drug desire forbids it"; }
            else if (FoodUtility.InappropriateForTitle(thing.def, pawn, allowIfStarving: true))
            { gate = "title"; reason = "below this pawn's royal title's food requirements"; }
            else if (!CanReachThing(pawn, thing, PathEndMode.OnCell, out reason)) gate = "no-path";

            int count = 0;
            if (gate == null)
            {
                try
                {
                    count = FoodUtility.GetMaxAmountToPickup(thing, pawn,
                        FoodUtility.WillIngestStackCountOf(pawn, thing.def, FoodUtility.NutritionForEater(pawn, thing)));
                }
                catch { count = 0; }
                if (count == 0)
                {
                    gate = "stack";
                    reason = "the pawn can pick up none of this stack (FoodUtility.GetMaxAmountToPickup == 0); "
                        + "the game's own option is present but unclickable here";
                }
            }

            if (gate != null)
            {
                outcome.No(pawn, gate, reason);
                return outcome.Result(V, ConsumeRow(outcome, V, pawn, thing, count),
                    new Dictionary<string, object> { ["thing"] = thing.thingIDNumber });
            }

            try { thing.SetForbidden(value: false); } catch { }
            bool queued = ctx.Args.Bool("queue", false);
            var job = JobMaker.MakeJob(JobDefOf.Ingest, thing);
            job.count = count;
            // 4087644 — PawnActs.AlreadyDoing. `count` is NOT compared by
            // Job.JobIsSameAs, so a pawn already eating this thing swallows an
            // order for a different amount too.
            if (AlreadyDoing(pawn, job))
            {
                outcome.No(pawn, GateAlready, AlreadyWhy(queued), AlreadyLine(pawn, queued));
                return outcome.Result(V, ConsumeRow(outcome, V, pawn, thing, count),
                    new Dictionary<string, object> { ["thing"] = thing.thingIDNumber });
            }
            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, queued))
            {
                outcome.No(pawn, "refused", "Pawn_JobTracker.TryTakeOrderedJob refused the job");
                return outcome.Result(V, ConsumeRow(outcome, V, pawn, thing, count));
            }
            outcome.Ok(pawn, JobLine(pawn));
            long seq = ConsumeRow(outcome, V, pawn, thing, count);
            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["thing"] = thing.thingIDNumber,
                ["count"] = count,
                // The Ideology history-event checks the provider runs
                // (IngestedDrug / IngestedRecreationalDrug / IngestedHardDrug)
                // return a DISABLED option rather than blocking; the pawn takes
                // a mood hit rather than refusing. Not reproduced as a gate for
                // that reason, and named here so it is a decision, not an
                // oversight.
                ["note"] = "Ideology precept violations do not block ingestion; they are a mood consequence",
            });
        }

        // ======================== work-giver scanning ========================
        // The single reproduction of RimWorld/FloatMenuOptionProvider_WorkGivers
        // .GetWorkGiverOption, shared by `orders` (which lists) and `prioritize`
        // (which acts). Returns a fatal reason when the pawn has no work think
        // node at all, else null; calls `sink` once per work giver with
        // (giverDef, scanner, job-or-null, label, reason-or-null).
        private static string ScanWorkGivers(Pawn pawn, LocalTargetInfo target, bool hasThing,
            Action<WorkGiverDef, WorkGiver_Scanner, Job, string, string> sink)
        {
            try
            {
                if (pawn.thinker == null || pawn.thinker.TryGetMainTreeThinkNode<JobGiver_Work>() == null)
                    return "this pawn has no JobGiver_Work in its think tree; the game offers it no work orders";
            }
            catch { return "this pawn's think tree could not be read"; }

            // ------------- makingFor: vanilla's ambient argument ------------
            // RimWorld/FloatMenuMakerMap.cs GetOptions sets `makingFor =
            // context.FirstSelectedPawn` around its ENTIRE option-building pass
            // and clears it afterwards. That static is not decoration: it is how
            // the game tells the work-giver layer "a player is asking on behalf
            // of this pawn, right now", and four members downstream branch on
            // it. A scan that omits it is therefore NOT the float menu's scan —
            // it is a different, more destructive one (git-bug 32b9e01):
            //
            //  1. A COLONY-WIDE BILL COOLDOWN. RimWorld/WorkGiver_DoBill.cs
            //     StartOrResumeBillJob: when TryFindBestBillIngredients fails
            //     and `makingFor != pawn`, it writes
            //     `bill.nextTickToSearchForIngredients = TicksGame +
            //     ReCheckFailedBillTicksRange.RandomInRange` — IntRange(500,600)
            //     in the same file. The same method's skip clause reads that
            //     field back with NO pawn qualifier, so the suppression applies
            //     to every colonist. Asking what a cook could do at a stove
            //     would have stopped the whole colony cooking for ~10 in-game
            //     seconds. A player's click never does this.
            //  2. AN RNG BURN. That same line is Verse/IntRange.cs RandomInRange
            //     -> Rand.RangeInclusive, i.e. the shared stream. WorldSafe's
            //     Class R, which this project treats as disqualifying for an
            //     observer.
            //  3. A DANGER-THRESHOLD DIVERGENCE. Verse/DangerUtility.cs
            //     NormalMaxDanger returns Danger.Deadly when `makingFor == p`.
            //     Without it, every reachability test the scanners make runs at
            //     a different threshold than the menu this verb claims parity
            //     with — so it could honestly disagree with the game's own
            //     answer, which is the exact failure the widget-parity doctrine
            //     exists to prevent.
            //  4. THE REASONS. `makingFor == pawn` is what unlocks the
            //     JobFailReason strings: the missing-materials list in
            //     StartOrResumeBillJob, and in RimWorld/
            //     WorkGiver_ConstructDeliverResources.cs JobOnThing BOTH the
            //     "missing 30 steel" string AND the loop that collects every
            //     missing def instead of `break`ing at the first one. Without
            //     it the agent gets a silent skip where the player gets a
            //     reason, and pays a round trip to find out why.
            //
            // Set for the WHOLE walk, exactly as vanilla scopes it, and dropped
            // before the caller acts: `prioritize` takes its job after this
            // method returns, which is also when the float menu's option action
            // runs.

            // NO EARLY EXIT. The walk always covers every WorkGiverDef, for two
            // reasons: `prioritize` must see the one it was NAMED, wherever it
            // sits in the order; and truncating the walk would truncate
            // `total` silently, which breaks the project's standing
            // truncation-is-a-contract rule (cap the OUTPUT, count the truth).
            // The cost is bounded by the bench's WorkGiverDef count, not by map
            // size, and each check is exactly what the float menu runs when it
            // opens.

            var prevMakingFor = FloatMenuMakerMap.makingFor;
            FloatMenuMakerMap.makingFor = pawn;
            try
            {
                foreach (var workType in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                {
                    if (workType?.workGiversByPriority == null) continue;
                    foreach (var giver in workType.workGiversByPriority)
                    {
                        if (giver == null) continue;
                        try
                        {
                            if (pawn.Drafted && !giver.canBeDoneWhileDrafted) continue;
                            if (!(giver.Worker is WorkGiver_Scanner scanner) || !scanner.def.directOrderable) continue;

                            JobFailReason.Clear();
                            if (hasThing)
                            {
                                if (ScannerShouldSkip(pawn, scanner, target.Thing)) continue;
                            }
                            else
                            {
                                var cells = scanner.PotentialWorkCellsGlobal(pawn);
                                bool inSet = false;
                                if (cells != null) foreach (var c in cells) { if (c == target.Cell) { inSet = true; break; } }
                                if (!inSet || scanner.ShouldSkip(pawn, forced: true)) continue;
                            }

                            Job job = hasThing
                                ? (scanner.HasJobOnThing(pawn, target.Thing, forced: true)
                                    ? scanner.JobOnThing(pawn, target.Thing, forced: true) : null)
                                : (scanner.HasJobOnCell(pawn, target.Cell, forced: true)
                                    ? scanner.JobOnCell(pawn, target.Cell, forced: true) : null);

                            string label = hasThing
                                ? "prioritize " + giver.verb + " " + Safe(() => target.Thing.LabelShort)
                                : "prioritize " + giver.verb + " here";
                            string why = null;

                            if (job == null)
                            {
                                // No job AND no reason = the game shows nothing at
                                // all; that is a skip, not a rejection.
                                if (!JobFailReason.HaveReason) continue;
                                why = JobFailReason.Reason.CapitalizeFirst();
                            }
                            else
                            {
                                why = PrioritizeRejection(pawn, scanner, job, target, hasThing, out string better);
                                if (better != null) label = better;
                            }
                            sink(giver, scanner, why == null ? job : null, label, why);
                        }
                        catch (Exception e)
                        {
                            // The game logs and continues here too
                            // (GetWorkGiversOptionsFor's try/catch). Ours journals a
                            // warning rather than a red error, and never fails the
                            // verb over one broken modded work giver.
                            Journal.EmitWarning("orders: work giver " + giver.defName + " threw: " + e.Message);
                        }
                    }
                }
            }
            finally
            {
                // Vanilla assigns null here rather than restoring, because
                // vanilla is the only setter and its pass cannot re-enter
                // itself. We are a SECOND setter, so we restore: prevMakingFor
                // is null on every real path (verbs drain in
                // GameComponentUpdate, the float menu builds during OnGUI, so
                // the two cannot interleave), which makes this identical to
                // vanilla's null everywhere except the one case where nulling
                // would clear a value we did not set.
                FloatMenuMakerMap.makingFor = prevMakingFor;
            }

            return null;
        }

        // RimWorld/FloatMenuOptionProvider_WorkGivers.cs ScannerShouldSkip.
        private static bool ScannerShouldSkip(Pawn pawn, WorkGiver_Scanner scanner, Thing t)
        {
            try
            {
                var global = scanner.PotentialWorkThingsGlobal(pawn);
                bool accepts = scanner.PotentialWorkThingRequest.Accepts(t);
                if (!accepts && global != null)
                {
                    foreach (var g in global) { if (g == t) { accepts = true; break; } }
                }
                if (!accepts) return true;
                return scanner.ShouldSkip(pawn, forced: true);
            }
            catch { return true; }
        }

        // The reason cascade of GetWorkGiverOption, in the game's own order and
        // with the game's own strings. Returns null when the option is LIVE.
        //
        // Deviation, named: the vanilla forbidden/area branches interpolate
        // `pawn.playerSettings.EffectiveAreaRestrictionInPawnCurrentMap.Label`,
        // whose getter dereferences a null MapHeld (PawnSafe Class D). Ours
        // reads the NULL-GUARDED sibling AreaRestrictionInPawnCurrentMap for the
        // same label, so a pawn with no MapHeld yields a reason rather than an
        // exception.
        private static string PrioritizeRejection(Pawn pawn, WorkGiver_Scanner scanner, Job job,
            LocalTargetInfo target, bool hasThing, out string label)
        {
            label = null;
            var workType = scanner.def.workType;
            try
            {
                var missing = scanner.MissingRequiredCapacity(pawn);
                if (missing != null) return Tr("CannotMissingHealthActivities", "missing " + missing.label);
                if (pawn.WorkTagIsDisabled(scanner.def.workTags))
                    return "work giver disabled for this pawn: " + scanner.def.label;
                if (pawn.jobs.curJob != null && pawn.jobs.curJob.JobIsSameAs(pawn, job))
                    return "already doing exactly this";
                // EverWork-gated: GetPriority on an uninitialised pawn Log.Errors
                // AND initialises (PawnSafe Class B).
                if (pawn.workSettings != null && pawn.workSettings.EverWork
                    && pawn.workSettings.GetPriority(workType) == 0)
                    return pawn.WorkTypeIsDisabled(workType)
                        ? "work type disabled for this pawn: " + workType.gerundLabel
                        : "not assigned to this work type: " + workType.gerundLabel;
                if (job.def == JobDefOf.Research && target.Thing is Building_ResearchBench)
                    return Tr("CannotPrioritizeResearch", "no research project is selected");
                if (hasThing && target.Thing.IsForbidden(pawn))
                    return target.Thing.Position.InAllowedArea(pawn)
                        ? "forbidden"
                        : "forbidden: outside the allowed area (" + AreaLabel(pawn) + ")";
                if (!hasThing && target.Cell.IsForbidden(pawn))
                    return target.Cell.InAllowedArea(pawn)
                        ? "cell forbidden"
                        : "forbidden: outside the allowed area (" + AreaLabel(pawn) + ")";
                if (hasThing && !pawn.CanReach(target.Thing, scanner.PathEndMode, Danger.Deadly))
                    return Tr("NoPath", "no path");
                if (!hasThing && !pawn.CanReach(target.Cell, PathEndMode.Touch, Danger.Deadly))
                    return Tr("NoPath", "no path");
                label = Safe(() => scanner.PostProcessedGerund(job)) ?? scanner.def.verb;
                if (hasThing) label = label + " " + Safe(() => target.Thing.LabelShort);
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + e.Message;
            }
            return null;
        }

        private static string AreaLabel(Pawn pawn)
            => Safe(() => pawn.playerSettings?.AreaRestrictionInPawnCurrentMap?.Label) ?? "unrestricted";

        // thing / cell target resolution shared by `orders` and `prioritize`.
        private static LocalTargetInfo ResolveWorkTarget(Map map, VerbArgs args, out Thing thing, out IntVec3 cell)
        {
            thing = null;
            cell = IntVec3.Invalid;
            if (args.Has("thing"))
            {
                thing = ThingArg(map, args, "thing");
                cell = thing.PositionHeld;
                return new LocalTargetInfo(thing);
            }
            if (args.Has("cell"))
            {
                cell = Positions.Resolve(map, args.Raw("cell"));
                if (cell.Fogged(map))
                    throw new VerbArgsException(
                        $"cell ({cell.x},{cell.z}) is in unexplored ground; the game offers no work orders there "
                        + "(FloatMenuOptionProvider.IgnoreFogged)");
                return new LocalTargetInfo(cell);
            }
            throw new VerbArgsException("pass either 'thing' (a thing id) or 'cell' (a position) — "
                + "the game's work-giver menu has both forms and they are different orders");
        }

        private static bool SafeCapable(Pawn p, PawnCapacityDef cap)
        {
            try { return p.health.capacities.CapableOf(cap); } catch { return false; }
        }
    }
}
