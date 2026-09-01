using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // ======================================================= spec 3.5 =========
    // COMMS — the console, its targets, and the faction dialogue tree, all
    // headless. This is the spec's "generic DiaNode walker so faction calls and
    // modded trees work uniformly", and it turns out to need NO window at all.
    //
    // ============ THE FINDING THAT MADE THE WALKER POSSIBLE =================
    // The session-4 amendment said the comms actions "live in private lambdas
    // and must be reimplemented (~3 lines each)" — `RequestTraderOption`,
    // `RequestOrbitalTraderOption`, `RequestMilitaryAidOption` and `CallForAid`
    // are all `private static` in `RimWorld/FactionDialogMaker`, with their
    // effects inside `diaOption.action = delegate {...}`. That is true, and it
    // is also beside the point, because the ONE member that assembles them —
    //
    //     public static DiaNode FactionDialogFor(Pawn negotiator, Faction faction)
    //
    // — IS PUBLIC, and it returns the whole tree already built: every option,
    // every `action` closure, every `link` / `linkLateBind`, and every
    // `Disable(reason)` the game would have greyed out. So the actions are not
    // reimplemented at all. They are REPLAYED, from the same objects the window
    // would have shown, which is strictly more faithful than a three-line copy
    // and picks up modded royal-permit options (`RoyalTitlePermitDef.Worker
    // .GetFactionCommDialogOptions`) for free.
    //
    // AND NO WINDOW IS INVOLVED. `Faction.TryOpenComms` is
    // `Find.WindowStack.Add(new Dialog_Negotiation(negotiator, this,
    // FactionDialogMaker.FactionDialogFor(negotiator, this), radioMode: true))`
    // — the window is a VIEW over the node the second argument already built.
    // `comms-call` builds the same node and holds it in a headless session, so
    // `Dialog_Negotiation` (a `Dialog_NodeTree`, hence `forcePause = true`)
    // never goes up and no advance is ever halted by a faction call.
    //
    // ============ THE RED ERROR THIS FILE EXISTS TO PREVENT ==================
    // `Building_CommsConsole.GetFailureReason` is `private` and its LAST clause
    // is
    //     if (!CanUseCommsNow)
    //     { Log.Error(myPawn?.ToString() + " could not use comm console for unknown reason.");
    //       return new FloatMenuOption("Cannot use now", null); }
    // — a RED ERROR reachable from the vanilla widget itself. It is reachable
    // only when an EARLIER clause should have caught the same condition, so
    // reproducing the earlier clauses (solar flare, then power) makes it
    // unreachable. `CanUseCommsNow` is checked here too, before anything else
    // could log.
    //
    // A SECOND ONE, one layer in: `FactionDialogMaker.FactionDialogFor` opens
    //     if (faction.leader != null) { ... } else { Log.Error($"Faction {faction} has no leader."); ... }
    // so calling a leaderless faction is a red error. `Faction
    // .CommFloatMenuOption` already refuses it at the widget — it returns an
    // option with a NULL action and the text "LeaderUnavailableNoLeader" — via
    // the private `LeaderIsAvailableToTalk()`. Reproduced below, which closes
    // that path too.
    //
    // ============ WHAT IS DROPPED, AND WHY ==================================
    // Both vanilla entry points end with a knowledge-database write:
    // `Building_CommsConsole.GiveUseCommsJob` does
    // `PlayerKnowledgeDatabase.KnowledgeDemonstrated(ConceptDefOf.OpeningComms,
    // KnowledgeAmount.Total)`, and `FloatMenuOptionProvider_Trade`'s action does
    // the same with `ConceptDefOf.InteractingWithTraders`. Those are tutorial
    // bookkeeping, not game state, and the standing rule (3.2's flick tutorial
    // modal) is reproduce the gate and the effect, drop the tutorial line.
    // Dropped, and named here so the omission is a decision rather than an
    // oversight.
    //
    // The JOB is not taken either, for the reason TradeVerbs.cs sets out at
    // length: `JobDriver_UseCommsConsole`'s only payload is
    // `commTarget.TryOpenComms(actor)`, which is a window in both
    // implementations. The gates the walk would have had to pass — including
    // `CanReach(console, PathEndMode.InteractionCell, Danger.Some)` — are
    // reproduced in full, and the result says the negotiator did not walk.
    internal static partial class PawnActs
    {
        // The headless node tree. This is the analogue of TradeSession: a
        // static conversation with no window, whose lifecycle THIS file owns
        // because there is no window to own it.
        private static DiaNode commsNode;
        private static Pawn commsNegotiator;
        private static ICommunicable commsTarget;
        private static string commsTargetLabel;
        private static int commsSteps;

        private static bool CommsActive => commsNode != null;

        // ====================================================================
        // comms-targets {console?, negotiator?}
        //
        // What this console could call right now, and — for each one that it
        // could not — the game's own reason. Read-only.
        // `Building_CommsConsole.GetCommTargets` is PUBLIC and is
        //   `myPawn.Map.passingShipManager.passingShips.Cast<ICommunicable>()
        //    .Concat(Find.FactionManager.AllFactionsVisibleInViewOrder
        //            .Where(f => !f.temporary && !f.IsPlayer).Cast<ICommunicable>())`
        // — LINQ over two live lists, no lazy build, so it is called rather
        // than re-derived.
        // ====================================================================
        [Verb("comms-targets")]
        public static object CommsTargets(VerbContext ctx)
        {
            const string V = "comms-targets";
            var map = PawnSafe.CurrentMap() ?? throw new VerbArgsException(V + " needs a current map");
            var negotiator = CommsNegotiatorArg(map, ctx.Args);
            var console = ConsoleArg(map, ctx.Args, out string consoleError);

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["negotiator"] = PawnRef(negotiator),
                ["action"] = NoStamp(),
            };
            if (console == null)
            {
                d["ok"] = false;
                d["gate"] = "no-console";
                d["reason"] = consoleError;
                return d;
            }
            d["console"] = new Dictionary<string, object>
            {
                ["id"] = console.thingIDNumber,
                ["def"] = console.def?.defName,
                ["at"] = Positions.Out(console.Position),
                ["can_use_now"] = WorldSafe.SafeObj(() => (object)console.CanUseCommsNow),
            };

            string blocked = ConsoleFailure(console, negotiator, map);
            d["console_blocked"] = blocked;
            d["ok"] = blocked == null;

            var targets = new List<object>();
            try
            {
                foreach (var t in console.GetCommTargets(negotiator))
                {
                    if (t == null) continue;
                    if (targets.Count >= 40) break;
                    targets.Add(TargetLine(t, console, negotiator, map));
                }
            }
            catch (Exception e)
            {
                d["targets_error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 160);
            }
            d["targets"] = targets;
            if (CommsActive) d["open_call"] = CommsHead();
            return d;
        }

        // ====================================================================
        // comms-call {target, console?, negotiator?}
        //
        // `target` is a faction NAME (or defName) for a node tree, or a passing
        // ship NAME for a trade. Both routes reproduce the console gates first;
        // then the per-target gate, which differs by type.
        // ====================================================================
        [Verb("comms-call")]
        public static object CommsCall(VerbContext ctx)
        {
            const string V = "comms-call";
            var map = PawnSafe.CurrentMap() ?? throw new VerbArgsException(V + " needs a current map");

            if (CommsActive)
                return CommsRefuse(V, "call-open",
                    "RimWorld/Dialog_Negotiation is a Dialog_NodeTree, hence forcePause and modal — "
                    + "the game cannot have two conversations",
                    "a comms call to " + commsTargetLabel + " is already open. Walk it with "
                    + "`comms-choose`, or end it with `comms-hang-up`.",
                    new Dictionary<string, object> { ["open_call"] = CommsHead() });
            if (TradeSession.Active)
                return CommsRefuse(V, "trade-open",
                    "RimWorld/TradeSession is static — one session at a time",
                    "a trade session is open with "
                    + WorldSafe.Safe(() => TradeSession.trader?.TraderName)
                    + "; finish it with `trade-confirm` or `trade-cancel` first");

            var negotiator = CommsNegotiatorArg(map, ctx.Args);
            var console = ConsoleArg(map, ctx.Args, out string consoleError);
            if (console == null)
                return CommsRefuse(V, "no-console",
                    "RimWorld/Building_CommsConsole is the only vanilla source of comm targets",
                    consoleError);

            // ---- the console gates, GetFailureReason's own order ------------
            string blocked = ConsoleFailure(console, negotiator, map);
            if (blocked != null)
                return CommsRefuse(V, "console-unusable",
                    "RimWorld/Building_CommsConsole.GetFailureReason (private) — reproduced clause by "
                    + "clause so that its own final `if (!CanUseCommsNow) { Log.Error(...) }` branch, "
                    + "which is a RED ERROR reachable from the vanilla widget, is never entered",
                    blocked);

            // ---- resolve the target ----------------------------------------
            string want = ctx.Args.StrReq("target");
            ICommunicable target = null;
            var names = new List<string>();
            try
            {
                foreach (var t in console.GetCommTargets(negotiator))
                {
                    if (t == null) continue;
                    string label = TargetName(t);
                    names.Add(label);
                    if (string.Equals(label, want, StringComparison.OrdinalIgnoreCase)
                        || (t is Faction f && (string.Equals(f.Name, want, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(f.def?.defName, want, StringComparison.OrdinalIgnoreCase)))
                        || (t is PassingShip s && string.Equals(s.name, want, StringComparison.OrdinalIgnoreCase)))
                    { target = t; break; }
                }
            }
            catch (Exception e)
            {
                throw new VerbArgsException("enumerating comm targets threw: " + e.Message);
            }
            if (target == null)
                throw new VerbArgsException("no comm target matching '" + want + "'. Reachable: "
                    + (names.Count == 0 ? "(none)" : string.Join(" | ", names.ToArray())));

            // ---- the per-target gate ---------------------------------------
            string targetBlocked = TargetFailure(target, console, negotiator, map);
            if (targetBlocked != null)
                return CommsRefuse(V, "target-unavailable",
                    target is Faction
                        ? "RimWorld/Faction.CommFloatMenuOption returns a FloatMenuOption with a NULL "
                          + "action (i.e. an unclickable row) when !LeaderIsAvailableToTalk(); and "
                          + "RimWorld/FactionDialogMaker.FactionDialogFor Log.Errors \"Faction {0} has "
                          + "no leader.\" if called anyway"
                        : "RimWorld/PassingShip.CommFloatMenuOption gives its option a null action when "
                          + "!CanCommunicateWith(negotiator), and its action refuses with "
                          + "\"MessageNeedBeaconToTradeWithShip\" when "
                          + "!Building_OrbitalTradeBeacon.AllPowered(Map).Any()",
                    targetBlocked);

            // ---- a passing ship IS a trade; hand it to TradeSession ---------
            if (target is ITrader ship && target is PassingShip)
            {
                bool canTradeNow = false;
                try { canTradeNow = ship.CanTradeNow; } catch { }
                if (!canTradeNow)
                    return CommsRefuse(V, "cannot-trade-now",
                        "RimWorld/TradeShip.TryOpenComms is wrapped in `if (CanTradeNow)`, and "
                        + "TradeSession.SetupWith Log.Warnings on it",
                        "'" + TargetName(target) + "' is not willing to trade right now");

                OpenSessionUnchecked(ship, negotiator, false);

                // `RimWorld/TradeShip.TryOpenComms` is FOUR statements, not one,
                // and the third is NOT presentation:
                //
                //     Find.WindowStack.Add(new Dialog_Trade(negotiator, this));
                //     LessonAutoActivator.TeachOpportunity(ConceptDefOf.BuildOrbitalTradeBeacon, ...);
                //     PawnRelationUtility.Notify_PawnsSeenByPlayer_Letter_Send(
                //         Goods.OfType<Pawn>(),
                //         "LetterRelatedPawnsTradeShip".Translate(Faction.OfPlayer.def.pawnsPlural),
                //         LetterDefOf.NeutralEvent);
                //     TutorUtility.DoModalDialogIfNotKnown(ConceptDefOf.TradeGoodsMustBeNearBeacon);
                //
                // That letter is how the player learns a COLONIST'S RELATIVE is
                // among the pawns for sale — a fact the agent cannot get any
                // other way, and a real decision input. Reproduced.
                // `TradeShip.Goods` is a plain iterator over `things` skipping
                // `soldPrisoners`, so reading it is safe.
                // The two TUTOR calls are dropped, and that is deliberate:
                // `LessonAutoActivator` is a UI nudge, and
                // `TutorUtility.DoModalDialogIfNotKnown` STACKS A MODAL, which
                // is precisely what halts every subsequent advance (spec 1.7).
                var relations = new Dictionary<string, object>
                {
                    ["cite"] = "RimWorld/TradeShip.TryOpenComms -> PawnRelationUtility"
                        + ".Notify_PawnsSeenByPlayer_Letter_Send(Goods.OfType<Pawn>(), ...)",
                    ["called"] = false,
                };
                try
                {
                    if (ship is TradeShip tradeShip)
                    {
                        var seen = new List<Pawn>();
                        foreach (var g in tradeShip.Goods) if (g is Pawn gp) seen.Add(gp);
                        relations["pawns_in_goods"] = seen.Count;
                        PawnRelationUtility.Notify_PawnsSeenByPlayer_Letter_Send(
                            seen,
                            "LetterRelatedPawnsTradeShip".Translate(Faction.OfPlayer.def.pawnsPlural),
                            LetterDefOf.NeutralEvent);
                        relations["called"] = true;
                        relations["note"] = "the game decides internally whether a letter is warranted "
                            + "(Notify_PawnsSeenByPlayer_Letter returns empty text when no seen pawn "
                            + "has a colony relative), so `called` is not `a letter was sent` — read "
                            + "`interactions` for that.";
                    }
                    else
                    {
                        relations["note"] = "this passing ship is not a TradeShip, so vanilla has no "
                            + "relations letter to send on this route";
                    }
                }
                catch (Exception e)
                {
                    relations["error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 160);
                    Journal.EmitWarning(V + ": the TradeShip relations letter threw: " + e.Message);
                }

                long tseq = Act(V, "call-trade", TargetName(target), new Dictionary<string, object>
                {
                    ["target"] = TargetName(target),
                    ["console"] = console.thingIDNumber,
                    ["negotiator"] = WorldSafe.Safe(() => negotiator.LabelShortCap.ToString()),
                });
                var td = SessionSummary(ctx.Args, V);
                td["ok"] = TradeSession.Active;
                td["kind"] = "trade";
                td["negotiator"] = PawnRef(negotiator);
                td["negotiator_walked"] = false;
                td["relations_letter"] = relations;
                td["action"] = Stamp(tseq);
                td["note"] = "calling a trade ship opens a TRADE, not a dialogue. "
                    + "RimWorld/TradeShip.TryOpenComms is four statements: the Dialog_Trade add (NOT "
                    + "reproduced — a force-pausing window halts every subsequent advance, spec 1.7; "
                    + "its only model effect, TradeSession.SetupWith, ran here directly), the "
                    + "BuildOrbitalTradeBeacon lesson and the TradeGoodsMustBeNearBeacon tutorial "
                    + "modal (both dropped as UI, and the second one would itself stack a modal), and "
                    + "PawnRelationUtility.Notify_PawnsSeenByPlayer_Letter_Send, which IS reproduced "
                    + "and is reported under `relations_letter` — it is how you learn a colonist's "
                    + "relative is among the pawns for sale. The session is open: use `trade-set` / "
                    + "`trade-confirm` / `trade-cancel`.";
                return td;
            }

            // ---- a faction IS a node tree ----------------------------------
            var faction = target as Faction;
            if (faction == null)
                return CommsRefuse(V, "unknown-target-type",
                    "RimWorld/ICommunicable has exactly two vanilla implementations, "
                    + "RimWorld/Faction and RimWorld/PassingShip",
                    "'" + TargetName(target) + "' is a " + target.GetType().Name
                    + ", which is neither a Faction nor a PassingShip. A modded ICommunicable's "
                    + "TryOpenComms is arbitrary code and is NOT invoked — it would very likely stack "
                    + "a window and halt the run.");

            DiaNode root;
            try { root = FactionDialogMaker.FactionDialogFor(negotiator, faction); }
            catch (Exception e)
            {
                return CommsRefuse(V, "dialog-build-threw",
                    "RimWorld/FactionDialogMaker.FactionDialogFor",
                    "building the faction dialogue threw: " + e.GetType().Name + ": "
                    + Journal.Truncate(e.Message, 200));
            }
            if (root == null)
                return CommsRefuse(V, "no-dialog", "RimWorld/FactionDialogMaker.FactionDialogFor",
                    "the game returned no dialogue node for this faction");

            commsNode = root;
            commsNegotiator = negotiator;
            commsTarget = target;
            commsTargetLabel = TargetName(target);
            commsSteps = 0;

            long seq = Act(V, "call", commsTargetLabel, new Dictionary<string, object>
            {
                ["target"] = commsTargetLabel,
                ["faction"] = faction.Name,
                ["console"] = console.thingIDNumber,
                ["negotiator"] = WorldSafe.Safe(() => negotiator.LabelShortCap.ToString()),
            });

            var d = CommsHead();
            d["verb"] = V;
            d["ok"] = true;
            d["kind"] = "node-tree";
            d["negotiator"] = PawnRef(negotiator);
            d["negotiator_walked"] = false;
            d["action"] = Stamp(seq);
            d["note"] = "this is the SAME DiaNode Faction.TryOpenComms would have wrapped in a "
                + "Dialog_Negotiation, built by the public FactionDialogMaker.FactionDialogFor — but "
                + "held headlessly, so no forcePause window is on the stack and `advance` is not "
                + "halted. Walk it with `comms-choose`; end it with `comms-hang-up`.";
            AddStackState(d);
            return d;
        }

        // ====================================================================
        // comms-choose {option|option_label}
        //
        // The generic DiaNode walker, on the headless tree. Same `Replay` the
        // window path uses, so the `disabled` gate and the presentation revert
        // cannot drift apart between them.
        //
        // `resolveTree` ends the CALL (there is no window to close), and a
        // `link` / `linkLateBind` moves to the next node in place — exactly
        // what `Dialog_NodeTree.GotoNode` does for the windowed version, minus
        // the `option.dialog = this` assignment, which only exists to let
        // `Activate()` find its own window.
        // ====================================================================
        [Verb("comms-choose")]
        public static object CommsChoose(VerbContext ctx)
        {
            const string V = "comms-choose";
            EnsureDiaRefs();
            if (!CommsActive)
                return CommsRefuse(V, "no-call", "this verb set owns the headless call's lifecycle "
                    + "because RimWorld/Dialog_Negotiation used to",
                    "no comms call is open. `comms-call {target}` first.");

            var opts = new List<DiaOption>();
            foreach (var o in commsNode.options)
            { if (o != null) opts.Add(o); if (opts.Count >= DiaOptionCap) break; }
            if (opts.Count == 0)
                return CommsRefuse(V, "no-options", "Verse/DiaNode.options",
                    "the current node has no options; `comms-hang-up` to end the call");

            int idx = ResolveOptionIndex(ctx.Args, opts.Count, i => OptText(opts[i]));
            var picked = opts[idx];
            string label = OptText(picked);

            // THE WIDGET GATE. Verse/DiaOption.OptOnGUI gates the button with
            // `Widgets.ButtonText(rect, text, drawBackground: false, !disabled,
            // textColor, active && !disabled)` and only then calls Activate();
            // Activate() itself does NOT check `disabled`. FactionDialogMaker
            // uses this constantly — MustBeAlly, BadTemperature, WaitTime,
            // WorkTypeDisablesOption — so without this check the agent would
            // request a trade caravan from a neutral faction on cooldown and
            // the model would happily queue the incident.
            if (picked.disabled)
                return CommsRefuse(V, "option-disabled",
                    "Verse/DiaOption.OptOnGUI gates the button with `Widgets.ButtonText(..., !disabled, "
                    + "textColor, active && !disabled)` and only then calls Activate(); Activate() "
                    + "itself does NOT check `disabled`",
                    "option " + idx + " (\"" + label + "\") is disabled"
                        + (string.IsNullOrEmpty(picked.disabledReason) ? " (the game gives no reason)"
                                                                      : ": " + picked.disabledReason),
                    new Dictionary<string, object>
                    {
                        ["option"] = idx,
                        ["option_label"] = label,
                        ["disabled_reason"] = picked.disabledReason,
                        ["options"] = OptionLines(opts),
                    });

            bool ended = false;
            var outcome = Replay(picked, null, "comms option",
                onNode: n => { commsNode = n; commsSteps++; },
                onResolve: () => { ended = true; });

            long seq = Act(V, "choose", commsTargetLabel, new Dictionary<string, object>
            {
                ["target"] = commsTargetLabel,
                ["option"] = idx,
                ["option_label"] = label,
                ["ended"] = ended,
            });

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["target"] = commsTargetLabel,
                ["option"] = idx,
                ["option_label"] = label,
                ["source"] = diaTextRef != null ? "backing-field" : "unavailable",
                ["action"] = Stamp(seq),
            };
            foreach (var kv in outcome) d[kv.Key] = kv.Value;
            if (ended) { EndCall(); d["call_ended"] = true; }
            else
            {
                d["steps"] = commsSteps;
                d["now_showing"] = NodeBlock(commsNode);
            }
            AddStackState(d);
            return d;
        }

        // ====================================================================
        // comms-hang-up
        //
        // The esc-equivalent for the headless call. It is deliberately NOT the
        // same as `dialog-dismiss`: there is no window, so nothing is removed
        // from the stack and no `closeAction` fires. Vanilla's equivalent is
        // picking the "(Disconnect)" option, which `FactionDialogMaker
        // .FactionDialogFor` always appends with `resolveTree = true` and no
        // action — i.e. dropping the tree IS what it does.
        // ====================================================================
        [Verb("comms-hang-up")]
        public static object CommsHangUp(VerbContext ctx)
        {
            const string V = "comms-hang-up";
            if (!CommsActive)
                return CommsRefuse(V, "no-call", "this verb set owns the headless call's lifecycle",
                    "no comms call is open; nothing to hang up");

            string target = commsTargetLabel;
            int steps = commsSteps;
            EndCall();

            long seq = Act(V, "hang-up", target, new Dictionary<string, object>
            {
                ["target"] = target,
                ["steps"] = steps,
            });

            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = !CommsActive,
                ["target"] = target,
                ["steps"] = steps,
                ["action"] = Stamp(seq),
                ["note"] = "no window was removed and no closeAction fired — the call was headless. "
                    + "This is the model equivalent of FactionDialogFor's own always-appended "
                    + "\"(Disconnect)\" option, which is `resolveTree = true` with no action.",
            };
        }

        // ========================== shared plumbing =========================

        private static void EndCall()
        {
            commsNode = null;
            commsNegotiator = null;
            commsTarget = null;
            commsTargetLabel = null;
            commsSteps = 0;
        }

        private static Dictionary<string, object> CommsHead()
        {
            var d = new Dictionary<string, object>
            {
                ["target"] = commsTargetLabel,
                ["target_type"] = commsTarget?.GetType().Name,
                ["negotiator"] = WorldSafe.Safe(() => commsNegotiator?.LabelShortCap.ToString()),
                ["steps"] = commsSteps,
            };
            if (commsTarget is Faction f)
            {
                d["faction"] = f.Name;
                d["relation"] = WorldSafe.Safe(() => f.PlayerRelationKind.ToString());
                d["goodwill"] = WorldSafe.SafeObj(() => (object)f.PlayerGoodwill);
                d["leader"] = WorldSafe.Safe(() => f.leader?.LabelShortCap.ToString());
            }
            if (commsNode != null) d["node"] = NodeBlock(commsNode);
            return d;
        }

        private static Dictionary<string, object> NodeBlock(DiaNode node)
            => new Dictionary<string, object>
            {
                ["text"] = WorldSafe.Safe(() => Journal.Truncate(node.text.ToString(), LetterTextClip)),
                ["options"] = OptionLines(node.options),
            };

        // `Building_CommsConsole.GetFailureReason` is private; this is it,
        // clause for clause and in its order, returning the game's own string.
        // Reproducing clauses 2 and 3 is what makes its own clause 6 — the
        // `Log.Error("... could not use comm console for unknown reason.")` —
        // unreachable, and `CanUseCommsNow` (public) is checked directly too so
        // that even a future clause we do not know about cannot reach it.
        private static string ConsoleFailure(Building_CommsConsole console, Pawn pawn, Map map)
        {
            try
            {
                if (!pawn.CanReach(console, PathEndMode.InteractionCell, Danger.Some))
                    return WorldSafe.Safe(() => "CannotUseNoPath".Translate().ToString())
                        ?? "the negotiator cannot reach the comms console";
                if (console.Spawned && map.gameConditionManager.ElectricityDisabled(map))
                    return WorldSafe.Safe(() => "CannotUseSolarFlare".Translate().ToString())
                        ?? "a solar flare has disabled electricity";
                var power = console.GetComp<CompPowerTrader>();
                if (power != null && !power.PowerOn)
                    return WorldSafe.Safe(() => "CannotUseNoPower".Translate().ToString())
                        ?? "the comms console has no power";
                if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Talking))
                    return WorldSafe.Safe(() => "CannotUseReason".Translate(
                        "IncapableOfCapacity".Translate(PawnCapacityDefOf.Talking.label,
                            pawn.Named("PAWN"))).ToString())
                        ?? "the negotiator is incapable of talking";
                if (!console.GetCommTargets(pawn).Any())
                    return WorldSafe.Safe(() => "CannotUseReason".Translate(
                        "NoCommsTarget".Translate()).ToString())
                        ?? "there is nobody to call";
                if (!console.CanUseCommsNow)
                    // Vanilla Log.Errors here. We do not: the three clauses
                    // above are the known causes, so reaching this means a
                    // cause we do not know about, and reporting it beats
                    // breaching the zero-red-errors invariant to find out.
                    return "the comms console cannot be used now for a reason none of the game's own "
                        + "earlier clauses explains (Building_CommsConsole.GetFailureReason would have "
                        + "logged a RED ERROR here; this verb reports instead)";
            }
            catch (Exception e)
            {
                return "checking the console's preconditions threw: " + e.GetType().Name + ": "
                    + Journal.Truncate(e.Message, 160);
            }
            return null;
        }

        // The per-target half of the float menu: Faction.CommFloatMenuOption
        // and PassingShip.CommFloatMenuOption, both of which express "no" as an
        // option with a NULL action rather than as an absent option.
        private static string TargetFailure(ICommunicable target, Building_CommsConsole console,
            Pawn pawn, Map map)
        {
            try
            {
                if (target is Faction f)
                {
                    // Faction.CommFloatMenuOption returns NULL for the player
                    // faction, i.e. no row at all.
                    if (f.IsPlayer) return "the player faction is not a comm target";
                    // Faction.LeaderIsAvailableToTalk() is private; this is it.
                    if (f.leader == null)
                        return (WorldSafe.Safe(() => "LeaderUnavailableNoLeader".Translate().ToString())
                            ?? "that faction has no leader")
                            + " — and FactionDialogMaker.FactionDialogFor would Log.Error "
                            + "\"Faction ... has no leader.\" if called anyway";
                    if (f.leader.Spawned && (f.leader.Downed || f.leader.IsPrisoner
                                             || !f.leader.Awake() || f.leader.InMentalState))
                        return WorldSafe.Safe(() => "LeaderUnavailable".Translate(
                            f.leader.LabelShort, f.leader).ToString())
                            ?? "that faction's leader cannot talk right now";
                    return null;
                }
                if (target is PassingShip ship)
                {
                    // PassingShip.CanCommunicateWith is protected; TradeShip's
                    // override is `base.CanCommunicateWith(negotiator)` (always
                    // accepted) then `negotiator.CanTradeWith(Faction, TraderKind)`.
                    if (ship is TradeShip ts)
                    {
                        AcceptanceReport rep = AcceptanceReport.WasAccepted;
                        try { rep = pawn.CanTradeWith(ts.Faction, ts.TraderKind); } catch { }
                        if (!rep.Accepted)
                            return string.IsNullOrEmpty(rep.Reason)
                                ? "the negotiator cannot trade with that ship"
                                : rep.Reason;
                    }
                    // The option's own action refuses before giving the job.
                    bool beacon = false;
                    try { beacon = Building_OrbitalTradeBeacon.AllPowered(map).Any(); } catch { }
                    if (!beacon)
                        return WorldSafe.Safe(() => "MessageNeedBeaconToTradeWithShip".Translate().ToString())
                            ?? "the colony has no powered orbital trade beacon";
                    return null;
                }
            }
            catch (Exception e)
            {
                return "checking the target's preconditions threw: " + e.GetType().Name + ": "
                    + Journal.Truncate(e.Message, 160);
            }
            return null;
        }

        private static Dictionary<string, object> TargetLine(ICommunicable t,
            Building_CommsConsole console, Pawn pawn, Map map)
        {
            var d = new Dictionary<string, object>
            {
                ["name"] = TargetName(t),
                ["type"] = t.GetType().Name,
                ["type_full"] = t.GetType().FullName,
                ["info"] = WorldSafe.Safe(() => Journal.Truncate(t.GetInfoText(), 200)),
            };
            if (t is Faction f)
            {
                d["kind"] = "faction";
                d["faction"] = f.Name;
                d["faction_def"] = f.def?.defName;
                d["relation"] = WorldSafe.Safe(() => f.PlayerRelationKind.ToString());
                d["goodwill"] = WorldSafe.SafeObj(() => (object)f.PlayerGoodwill);
                d["leader"] = WorldSafe.Safe(() => f.leader?.LabelShortCap.ToString());
                d["can_request_traders"] = f.def?.canRequestTraders;
                d["can_request_orbital"] = f.def?.canRequestOrbitalTrader;
                d["can_request_military_aid"] = f.def?.canRequestMilitaryAid;
            }
            else if (t is PassingShip s)
            {
                d["kind"] = "ship";
                d["ship"] = s.name;
                d["ticks_until_departure"] = s.ticksUntilDeparture;
                if (s is ITrader tr)
                {
                    d["trader_kind"] = WorldSafe.Safe(() => tr.TraderKind?.LabelCap.ToString());
                    d["can_trade_now"] = WorldSafe.SafeObj(() => (object)tr.CanTradeNow);
                    d["opens"] = "trade";
                }
            }
            else d["kind"] = "other";

            string blocked = TargetFailure(t, console, pawn, map);
            d["blocked"] = blocked;
            d["callable"] = blocked == null;
            return d;
        }

        private static string TargetName(ICommunicable t)
        {
            string s = WorldSafe.Safe(() => t.GetCallLabel());
            if (!string.IsNullOrEmpty(s)) return s;
            return t.GetType().Name;
        }

        private static Building_CommsConsole ConsoleArg(Map map, VerbArgs args, out string error)
        {
            error = null;
            var all = new List<Building_CommsConsole>();
            try { all.AddRange(map.listerBuildings.AllBuildingsColonistOfClass<Building_CommsConsole>()); }
            catch { }

            if (args.Has("console"))
            {
                int id = args.IntReq("console");
                foreach (var c in all) if (c.thingIDNumber == id) return c;
                error = "no colony comms console with id " + id + " on the current map ("
                    + all.Count + " present)";
                return null;
            }
            if (all.Count == 0)
            {
                error = "the colony has no comms console on the current map. Build one "
                    + "(ThingDefOf.CommsConsole) — it is the only vanilla source of comm targets.";
                return null;
            }
            // Prefer one that is actually usable, so a second console being
            // unpowered does not decide the call for the agent.
            foreach (var c in all)
            {
                bool ok = false;
                try { ok = c.CanUseCommsNow; } catch { }
                if (ok) return c;
            }
            return all[0];
        }

        private static Pawn CommsNegotiatorArg(Map map, VerbArgs args)
        {
            if (args.Has("negotiator")) return Dev.PawnArg(map, args, "negotiator");
            Pawn best = null;
            int bestSkill = -1;
            try
            {
                foreach (var p in map.mapPawns.FreeColonistsSpawned)
                {
                    if (p == null || p.Downed || p.Dead || p.skills == null) continue;
                    // The talking capacity is a console gate; skip anyone who
                    // would only be refused, so the default pick is a pawn that
                    // can actually make the call.
                    bool talks = false;
                    try { talks = p.health.capacities.CapableOf(PawnCapacityDefOf.Talking); } catch { }
                    if (!talks) continue;
                    int lvl;
                    try
                    {
                        var s = p.skills.GetSkill(SkillDefOf.Social);
                        if (s == null || s.TotallyDisabled) continue;
                        lvl = s.Level;
                    }
                    catch { continue; }
                    if (lvl > bestSkill) { bestSkill = lvl; best = p; }
                }
            }
            catch { }
            return best ?? throw new VerbArgsException("no free spawned colonist is capable of both "
                + "Social and Talking; pass `negotiator` explicitly (a pawn id from `pawns`)");
        }

        private static Dictionary<string, object> CommsRefuse(string verb, string gate, string cite,
            string reason, Dictionary<string, object> extra = null)
        {
            var d = new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["ok"] = false,
                ["gate"] = gate,
                ["gate_cite"] = cite,
                ["reason"] = reason,
                ["action"] = NoStamp(),
            };
            if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
            return d;
        }
    }
}
