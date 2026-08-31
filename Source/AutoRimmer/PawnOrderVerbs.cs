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

            long seq = outcome.Count > 0
                ? Act(verb, want ? "drafted" : "undrafted",
                      outcome.Count + " pawn(s)",
                      new Dictionary<string, object> { ["ids"] = ids })
                : 0;

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

            long seq = outcome.Count > 0
                ? Act(V, "goto", $"({to.x},{to.z})",
                      new Dictionary<string, object> { ["ids"] = ids, ["to"] = Positions.Out(to) })
                : 0;

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
                action();
                ids.Add(p.thingIDNumber);
                var line = JobLine(p);
                line["attack"] = chosen;
                outcome.Ok(p, line);
            }

            long seq = outcome.Count > 0
                ? Act(V, "attack", Safe(() => target.LabelShortCap.ToString()) ?? target.def?.defName,
                      new Dictionary<string, object>
                      {
                          ["ids"] = ids,
                          ["target"] = target.thingIDNumber,
                          ["removal"] = removal,
                      })
                : 0;

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
        // orders {pawn, thing?|cell?, cap?}   READ-ONLY
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
        // Read-only, but NOT free: it runs each scanner's HasJobOn* with
        // forced:true, exactly as the float menu does when it opens. That is
        // real work — bounded by the WorkGiverDef count, not by map size.
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
            int total = 0;
            string fatal = ScanWorkGivers(pawn, target, thing != null, cap,
                (giver, scanner, job, label, why) =>
                {
                    total++;
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
                ["blocked"] = blocked,
                ["total"] = total,
                ["unavailable_reason"] = fatal,
                ["note"] = "WorkGiverDef.directOrderable defaults true, so this list is as wide as the "
                    + "bench's WorkGiver set; pass a `work` value from it to `prioritize`. "
                    + "Read-only: no job is taken.",
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
            string fatal = ScanWorkGivers(pawn, target, thing != null, int.MaxValue,
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

            long seq = outcome.Count > 0
                ? Act(V, "clear", outcome.Count + " pawn(s)",
                      new Dictionary<string, object> { ["ids"] = ids })
                : 0;
            return outcome.Result(V, seq);
        }

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
                if (!doer.jobs.TryTakeOrderedJob(job, JobTag.Misc, ctx.Args.Bool("queue", false)))
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

            long seq = outcome.Count > 0
                ? Act(kind, kind, PawnSafe.Name(target),
                      new Dictionary<string, object> { ["ids"] = ids, ["target"] = target.thingIDNumber })
                : 0;

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
                return outcome.Result(V, 0, new Dictionary<string, object> { ["thing"] = thing.thingIDNumber, ["label"] = label });
            }

            // The provider's Equip() local: un-forbid, then order.
            try { thing.SetForbidden(value: false); } catch { }
            if (!pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Equip, thing), JobTag.Misc,
                    ctx.Args.Bool("queue", false)))
            {
                outcome.No(pawn, "refused", "Pawn_JobTracker.TryTakeOrderedJob refused the job");
                return outcome.Result(V, 0);
            }
            outcome.Ok(pawn, JobLine(pawn));
            long seq = Act(V, "equip", label,
                new Dictionary<string, object> { ["pawn"] = pawn.thingIDNumber, ["thing"] = thing.thingIDNumber });
            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["thing"] = thing.thingIDNumber,
                ["label"] = label,
                // The two confirmation dialogs the provider can raise are NOT
                // raised here: a bladelink already bonded to another weapon, and
                // a persona-weapon confirmation. Both would be force-pausing
                // windows (spec 1.7 halts every advance on one), and both are
                // pure confirmations of an order the caller already gave.
                ["dialogs_skipped"] = "bladelink-already-bonded and persona-weapon confirmations are "
                    + "transacted as accepted rather than opened as modals (1.7: a force-pausing window "
                    + "stops every later advance)",
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
                return outcome.Result(V, 0, new Dictionary<string, object> { ["thing"] = apparel.thingIDNumber });
            }

            try { apparel.SetForbidden(value: false); } catch { }
            if (!pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Wear, apparel), JobTag.Misc,
                    ctx.Args.Bool("queue", false)))
            {
                outcome.No(pawn, "refused", "Pawn_JobTracker.TryTakeOrderedJob refused the job");
                return outcome.Result(V, 0);
            }
            outcome.Ok(pawn, JobLine(pawn));
            long seq = Act(V, "wear", Safe(() => apparel.LabelShort) ?? apparel.def?.defName,
                new Dictionary<string, object> { ["pawn"] = pawn.thingIDNumber, ["thing"] = apparel.thingIDNumber });
            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["thing"] = apparel.thingIDNumber,
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
                if (!p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.DropEquipment, primary), JobTag.Misc,
                        ctx.Args.Bool("queue", false)))
                { outcome.No(p, "refused", "Pawn_JobTracker.TryTakeOrderedJob refused the job"); continue; }
                ids.Add(p.thingIDNumber);
                var line = JobLine(p);
                line["dropped"] = primary.thingIDNumber;
                line["dropped_def"] = primary.def?.defName;
                outcome.Ok(p, line);
            }

            long seq = outcome.Count > 0
                ? Act(V, "drop-primary", outcome.Count + " pawn(s)",
                      new Dictionary<string, object> { ["ids"] = ids })
                : 0;
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
                return outcome.Result(V, 0, new Dictionary<string, object> { ["thing"] = thing.thingIDNumber });
            }

            try { thing.SetForbidden(value: false); } catch { }
            var job = JobMaker.MakeJob(JobDefOf.Ingest, thing);
            job.count = count;
            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, ctx.Args.Bool("queue", false)))
            {
                outcome.No(pawn, "refused", "Pawn_JobTracker.TryTakeOrderedJob refused the job");
                return outcome.Result(V, 0);
            }
            outcome.Ok(pawn, JobLine(pawn));
            long seq = Act(V, "ingest", Safe(() => thing.LabelShort) ?? thing.def?.defName,
                new Dictionary<string, object>
                {
                    ["pawn"] = pawn.thingIDNumber,
                    ["thing"] = thing.thingIDNumber,
                    ["count"] = count,
                });
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
        private static string ScanWorkGivers(Pawn pawn, LocalTargetInfo target, bool hasThing, int cap,
            Action<WorkGiverDef, WorkGiver_Scanner, Job, string, string> sink)
        {
            try
            {
                if (pawn.thinker == null || pawn.thinker.TryGetMainTreeThinkNode<JobGiver_Work>() == null)
                    return "this pawn has no JobGiver_Work in its think tree; the game offers it no work orders";
            }
            catch { return "this pawn's think tree could not be read"; }

            int emitted = 0;
            foreach (var workType in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            {
                if (workType?.workGiversByPriority == null) continue;
                foreach (var giver in workType.workGiversByPriority)
                {
                    if (emitted >= cap * 2) return null;
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
                        emitted++;
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
