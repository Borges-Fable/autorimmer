using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AutoRimmer
{
    // ======================================================= spec 3.5 =========
    // TRADE — transacted against the model, with no window on the stack.
    //
    // THE LOAD-BEARING FACT, verified end to end: `RimWorld/TradeSession` is a
    // STATIC class, and `SetupWith` -> `Tradeable.AdjustTo` ->
    // `TradeDeal.UpdateCurrencyCount` -> `TradeDeal.TryExecute` is entirely
    // window-free. A grep for `Find.WindowStack`, `Dialog_Trade`, `Find.UIRoot`
    // and `Event.current` across TradeDeal, TradeSession, Tradeable,
    // Tradeable_Pawn, TradeUtility and FactionGiftUtility yields exactly TWO
    // hits: `TradeDeal.TryExecute` (the NRE below) and
    // `FactionGiftUtility.OfferGiftsCommand` (a caravan gizmo, off this path).
    // So DESIGN's "transact the model, never drive widgets" survives intact for
    // the hardest verb in the set.
    //
    // ============ TRAP 1: AN UNGUARDED NRE ON EXACTLY THE HEADLESS PATH ======
    // `RimWorld/TradeDeal.TryExecute`, verbatim:
    //
    //     if (CurrencyTradeable == null || CurrencyTradeable.CountPostDealFor(Transactor.Colony) < 0)
    //     {
    //         Find.WindowStack.WindowOfType<Dialog_Trade>().FlashSilver();
    //         Messages.Message("MessageColonyCannotAfford".Translate(), MessageTypeDefOf.RejectInput, historical: false);
    //         actuallyTraded = false;
    //         return false;
    //     }
    //
    // With no `Dialog_Trade` on the stack, `WindowOfType<T>()` returns null and
    // that first line NREs. This is the gate-lives-in-the-widget invariant in
    // its purest form: the game assumes its own UI is present and the model
    // does not defend itself. The fix is NOT a try/catch — a caught NRE would
    // leave the deal half-validated and tell the caller nothing. `Confirm`
    // below makes the branch UNREACHABLE by pre-testing the exact negation of
    // that condition and returning the refusal ourselves with the game's own
    // "MessageColonyCannotAfford" string. (The gift branch returns before it,
    // so gift mode never reaches the NRE; the pre-test is skipped there, as
    // vanilla does.)
    //
    // ============ TRAP 2: A SECOND RED ERROR, ONE LAYER UP ==================
    // `RimWorld/Transferable.AdjustTo` is:
    //     if (!CanAdjustTo(destination).Accepted) { Log.Error("Failed to adjust
    //         transferable counts"); } else { CountToTransfer = ClampAmount(destination); }
    // — a RED ERROR on any out-of-range count. The widget never reaches it,
    // because `TradeUI.DrawTradeableRow` hands
    // `TransferableUIUtility.DoCountAdjustInterface` the pre-computed
    // `trad.GetMinimumToTransfer()` / `GetMaximumToTransfer()` bounds and every
    // AdjustTo inside is already clamped. `Set` below reproduces that: it asks
    // `CanAdjustTo` FIRST and refuses with the game's own `UnderflowReport()` /
    // `OverflowReport()` reason ("TraderHasNoMore" / "ColonyHasNoMore") rather
    // than letting AdjustTo log.
    //
    // ============ TRAP 3: THE SESSION HAS NO OWNER ==========================
    // `TradeSession.Close()` has ZERO callers in the whole tree, and
    // `TradeSession.SetupWith` has exactly ONE — `RimWorld/Dialog_Trade`'s
    // constructor. The WINDOW owned the lifecycle. With no window, this file
    // owns it: `trade-confirm` and `trade-cancel` close it explicitly on
    // every exit path. `trade-start` does NOT close a session it finds already
    // open — it REFUSES with gate `session-open` and leaves the existing deal
    // intact, because silently discarding a deal the caller may still be
    // building is a worse answer than making them say `trade-cancel`.
    // `Close()` is `trader = null;` and nothing else, so `deal`,
    // `playerNegotiator` and `giftMode` are left set — which is why "cancel
    // leaves state untouched" is DEFINED below in terms of the game world, not
    // the statics.
    //
    // ============ WHAT `trade-start` COSTS, DISCLOSED NOT SUPPRESSED ========
    // Opening a session is not free, and it is not free in the vanilla click
    // either:
    //   * `TradeDeal.AddAllTradeables` runs `ThingMaker.MakeThing(ThingDefOf
    //     .Silver)` when the trader has no silver tradeable. `Thing.PostMake`
    //     calls `ThingIDMaker.GiveIDTo(this)` -> `Find.UniqueIDsManager
    //     .GetNextThingID()`, and `nextThingID` is `Scribe_Values.Look`-scribed
    //     (RimWorld/UniqueIDsManager.ExposeData). PostMake also rolls
    //     `def.startingHpRange.RandomInRange`. SO OPENING A TRADE BURNS A
    //     SCRIBED THING ID AND A RAND CALL.
    //   * `TradeSession.SetupWith` sends `Messages.Message(
    //     "MessageCannotSellItemsReason" ...)` when `deal.cannotSellReasons` is
    //     non-empty — a journaled message, from START.
    // Small, unavoidable, and what the click does — so DISCLOSED in the result,
    // the same call `orders` made for the job-ID counter.
    //
    // ============ WHAT IS NOT REACHED FOR =================================
    // `RimWorld.Planet/Settlement_TraderTracker.StockListForReading` is
    // `if (stock == null || stock.InnerListForReading.Empty()) { RegenerateStock(); }
    //  return stock.InnerListForReading;` — a property getter that
    // `TryDestroyStock()`s, builds a new ThingOwner, sets the scribed
    // `everGeneratedStock`, generates contents through
    // `ThingSetMakerDefOf.TraderStock.root.Generate(parms)` (RNG) and
    // `Find.WorldPawns.PassToWorld`es any pawns it made. It MANUFACTURES WORLD
    // STATE from a read. It is the WORLD-MAP settlement tracker, DESIGN's
    // non-goals rule caravans and settlements out for v1, and nothing in this
    // file — including its convenience helpers — touches it. The traders here
    // are map pawns and passing ships, through TradeSession.
    //
    // ============ A GATE THAT THREW HAS NOT PASSED =========================
    // Every widget gate below is read inside a try, because a modded `ITrader`
    // or `Tradeable` getter can throw (several vanilla ones already NRE with no
    // session — `Tradeable.TraderWillTrade` dereferences `TradeSession.trader`
    // directly). The catch used to leave the PERMISSIVE default in place, which
    // silently converted "this gate could not be evaluated" into "this gate
    // passed" and let the verb EXECUTE past a refusal it should have made —
    // `traderHasSilver = true` on a throwing getter skipping the
    // `trader-short-funds` refusal entirely. Every one of them now refuses with
    // `gate-unreadable` (per item, in `trade-set`) and names the exception.
    // Refusing is the only answer a verb can honestly give about a precondition
    // it could not read.
    internal static partial class PawnActs
    {
        public const int TradeableCap = 60;

        // The negotiator is not in TradeSession's own statics in a form we can
        // trust across calls (Close() leaves it set), so the verb set keeps its
        // own note of what IT opened, for provenance only. Never read as truth
        // about the game — `TradeSession.Active` is.
        private static string tradeOpenedBy;
        private static int tradeOpenedTick = -1;

        // ====================================================================
        // trade-start {trader, negotiator, gift?, console?}
        //
        // WHY THIS DOES NOT TAKE THE JOB, and the reasoning is recorded because
        // the spec's session-4 amendment assumed it would ("order-then-advance-
        // then-transact"). Both vanilla entry points end in a job whose ONLY
        // payload is a window:
        //   * `RimWorld/JobDriver_TradeWithPawn`'s final toil is
        //     `if (Trader.CanTradeNow) Find.WindowStack.Add(new Dialog_Trade(actor, Trader));`
        //   * `RimWorld/JobDriver_UseCommsConsole`'s is
        //     `commTarget.TryOpenComms(actor)`, and `RimWorld/TradeShip
        //     .TryOpenComms` is `Find.WindowStack.Add(new Dialog_Trade(negotiator, this))`.
        // `Dialog_Trade`'s constructor then calls `TradeSession.SetupWith` —
        // the same static state this verb sets — and `Dialog_Trade` sets
        // `forcePause = true`. So ordering the job buys IDENTICAL model state
        // plus a force-pausing window, and spec 1.7 proves a force-pausing
        // window halts every subsequent advance at 0 ticks. (`Dialog_Trade
        // .PostOpen` can stack a SECOND modal on top: a `Dialog_MessageBox` when
        // the negotiator's Talking or Hearing capacity is below 0.95.) The job
        // is therefore not taken, the negotiator does not walk, and the result
        // SAYS SO rather than implying a colonist crossed the map.
        //
        // THE GATES ARE REPRODUCED IN FULL, including the reachability the walk
        // would have needed. Map-trader route —
        // `RimWorld/FloatMenuOptionProvider_Trade.GetOptionsFor`, in its order:
        //   1. `((ITrader)clickedPawn).CanTradeNow` — no option at all if false.
        //   2. `CanReach(clickedPawn, PathEndMode.OnCell, Danger.Deadly)`
        //      -> "CannotTrade" + "NoPath"
        //   3. `skills.GetSkill(SkillDefOf.Social).TotallyDisabled`
        //      -> "CannotPrioritizeWorkTypeDisabled"
        //   4. `clickedPawn.mindState.traderDismissed` -> "TraderDismissed"
        //   5. `CanTradeWith(clickedPawn.Faction, clickedPawn.TraderKind)`
        //      -> "CannotTrade" + "MissingTitleAbility"
        // Passing-ship route — `RimWorld/Building_CommsConsole.GetFailureReason`
        // then `RimWorld/PassingShip.CommFloatMenuOption`; see CommsVerbs.cs,
        // which holds the console gates and calls this.
        //
        // AND `TradeSession.SetupWith` ITSELF opens with
        // `if (!newTrader.CanTradeNow) Log.Warning("Called SetupWith with a
        // trader not willing to trade now.")` — a yellow, not a red, but the
        // journal records warnings and one that fires on every trade start is
        // noise the run does not need. Pre-checked (gate 1) so it never fires.
        // ====================================================================
        [Verb("trade-start")]
        public static object TradeStart(VerbContext ctx)
        {
            const string V = "trade-start";
            var map = PawnSafe.CurrentMap() ?? throw new VerbArgsException(V + " needs a current map");
            bool gift = ctx.Args.Bool("gift", false);

            if (TradeSession.Active)
                return TradeRefuse(V, "session-open",
                    "RimWorld/Dialog_Trade is the only vanilla caller of TradeSession.SetupWith, and it "
                    + "is modal — the game cannot have two sessions",
                    "a trade session is already open with " + WorldSafe.Safe(() => TradeSession.trader?.TraderName)
                    + ". Finish it with `trade-confirm` or `trade-cancel` first.",
                    new Dictionary<string, object> { ["session"] = SessionHead() });

            var negotiator = TradeNegotiatorArg(map, ctx.Args);

            // ---- gate 0: the negotiator is a pawn the player could have
            // right-clicked with at all. The auto-picker applies its own
            // filters, but an EXPLICIT `negotiator` goes straight to
            // Dev.PawnArg, which resolves ANY pawn on the map — a downed
            // colonist, a prisoner, a mechanoid, a raider. `dev:*` may bypass a
            // widget gate; `trade-start` may not, so the gate runs on both
            // paths and the auto-picked pawn simply always passes it.
            var negGate = NegotiatorGate(V, map, negotiator);
            if (negGate != null) return negGate;

            var trader = TraderArg(map, ctx.Args, out string traderKindLabel, out Pawn traderPawn);

            // ---- gate 1: CanTradeNow (also silences SetupWith's Log.Warning)
            bool canTradeNow = false;
            try { canTradeNow = trader.CanTradeNow; } catch { }
            if (!canTradeNow)
                return TradeRefuse(V, "cannot-trade-now",
                    "RimWorld/FloatMenuOptionProvider_Trade.GetOptionsFor yields NOTHING when "
                    + "!((ITrader)clickedPawn).CanTradeNow; RimWorld/TradeShip.TryOpenComms is wrapped "
                    + "in `if (CanTradeNow)`; and TradeSession.SetupWith Log.Warnings on it",
                    "'" + WorldSafe.Safe(() => trader.TraderName) + "' is not willing to trade right now "
                    + "(a caravan still forming up or already leaving, a ship that has departed, or a "
                    + "trader whose lord has moved on)");

            // ---- gate 3: the negotiator can hold a conversation at all ------
            // FloatMenuOptionProvider_Trade checks this before CanTradeWith
            // does; both are reproduced because their messages differ.
            bool socialDisabled = true;
            try { socialDisabled = negotiator.skills == null
                    || negotiator.skills.GetSkill(SkillDefOf.Social).TotallyDisabled; } catch { }
            if (socialDisabled)
                return TradeRefuse(V, "social-disabled",
                    "RimWorld/FloatMenuOptionProvider_Trade.GetOptionsFor -> "
                    + "\"CannotPrioritizeWorkTypeDisabled\" when "
                    + "context.FirstSelectedPawn.skills.GetSkill(SkillDefOf.Social).TotallyDisabled",
                    WorldSafe.Safe(() => negotiator.LabelShortCap.ToString())
                    + " is incapable of Social and cannot negotiate");

            if (traderPawn != null)
            {
                // ---- gate 2: reachability -------------------------------
                // The walk itself is not ordered (see the header), but the
                // gate the walk would have had to pass IS reproduced: a
                // colonist who cannot get to the trader has no trade option.
                bool canReach = false;
                try { canReach = negotiator.CanReach(traderPawn, PathEndMode.OnCell, Danger.Deadly); } catch { }
                if (!canReach)
                    return TradeRefuse(V, "no-path",
                        "RimWorld/FloatMenuOptionProvider_Trade.GetOptionsFor -> \"CannotTrade\" + "
                        + "\"NoPath\" when !context.FirstSelectedPawn.CanReach(clickedPawn, "
                        + "PathEndMode.OnCell, Danger.Deadly)",
                        WorldSafe.Safe(() => negotiator.LabelShortCap.ToString())
                        + " cannot reach the trader");

                // ---- gate 4: dismissed ----------------------------------
                bool dismissed;
                try { dismissed = traderPawn.mindState.traderDismissed; }
                catch (Exception e)
                {
                    return GateUnreadable(V, "trader-dismissed",
                        "RimWorld/FloatMenuOptionProvider_Trade.GetOptionsFor reads "
                        + "clickedPawn.mindState.traderDismissed", e);
                }
                if (dismissed)
                    return TradeRefuse(V, "trader-dismissed",
                        "RimWorld/FloatMenuOptionProvider_Trade.GetOptionsFor -> \"TraderDismissed\" "
                        + "when clickedPawn.mindState.traderDismissed",
                        "this trader has been dismissed and will not trade again on this visit");
            }

            // ---- gate 5: title/permit --------------------------------------
            AcceptanceReport tradeWith;
            try { tradeWith = negotiator.CanTradeWith(trader.Faction, trader.TraderKind); }
            catch (Exception e)
            {
                return GateUnreadable(V, "can-trade-with",
                    "RimWorld/FloatMenuOptionProvider_Trade.GetOptionsFor -> "
                    + "RimWorld/FactionUtility.CanTradeWith(faction, traderKind)", e);
            }
            if (!tradeWith.Accepted)
                return TradeRefuse(V, "cannot-trade-with",
                    "RimWorld/FloatMenuOptionProvider_Trade.GetOptionsFor -> \"CannotTrade\" + "
                    + "\"MissingTitleAbility\" when !CanTradeWith(...).Accepted "
                    + "(RimWorld/FactionUtility.CanTradeWith)",
                    string.IsNullOrEmpty(tradeWith.Reason)
                        ? "the game refuses this pairing (hostile faction, or a royal permit the "
                          + "negotiator does not hold)"
                        : tradeWith.Reason);

            // ---- the act ---------------------------------------------------
            TradeSession.SetupWith(trader, negotiator, gift);
            tradeOpenedBy = WorldSafe.Safe(() => negotiator.LabelShortCap.ToString());
            try { tradeOpenedTick = Find.TickManager.TicksGame; } catch { tradeOpenedTick = -1; }

            long seq = Act(V, "start", WorldSafe.Safe(() => trader.TraderName),
                new Dictionary<string, object>
                {
                    ["trader"] = WorldSafe.Safe(() => trader.TraderName),
                    ["trader_kind"] = traderKindLabel,
                    ["negotiator"] = tradeOpenedBy,
                    ["gift"] = gift,
                });

            var d = Summary(ctx.Args, V);
            d["ok"] = TradeSession.Active;
            d["negotiator"] = PawnRef(negotiator);
            d["action"] = Stamp(seq);
            d["negotiator_walked"] = false;
            d["walk_note"] = "the negotiator did NOT walk to the trader: the vanilla job "
                + "(JobDriver_TradeWithPawn / JobDriver_UseCommsConsole) exists only to open a "
                + "force-pausing Dialog_Trade, which would halt every subsequent `advance` at 0 ticks "
                + "(spec 1.7). The reachability gate the walk would have had to pass WAS reproduced.";
            d["session_cost"] = new Dictionary<string, object>
            {
                ["scribed_thing_id"] = "TradeDeal.AddAllTradeables runs ThingMaker.MakeThing("
                    + "ThingDefOf.Silver) when the trader has no silver tradeable; Thing.PostMake -> "
                    + "ThingIDMaker.GiveIDTo -> Find.UniqueIDsManager.GetNextThingID(), and nextThingID "
                    + "is Scribe_Values-scribed. PostMake also rolls def.startingHpRange.RandomInRange. "
                    + "Unavoidable — it is exactly what clicking Trade does — and disclosed rather than "
                    + "suppressed.",
            };
            var cannotSell = new List<object>();
            try { foreach (var r in TradeSession.deal.cannotSellReasons) cannotSell.Add(r); } catch { }
            if (cannotSell.Count > 0)
            {
                d["cannot_sell_reasons"] = cannotSell;
                d["cannot_sell_note"] = "TradeSession.SetupWith already sent these as a "
                    + "\"MessageCannotSellItemsReason\" message, so they are in the journal too.";
            }
            return d;
        }

        // ====================================================================
        // trade {filter?, action_only?, cap?}
        //
        // The session summary: tradeables, prices, silver on both sides. A
        // PURE READ — it calls nothing but the `Tradeable` getters, and
        // deliberately does NOT call `TradeDeal.UpdateCurrencyCount()` even
        // though `Dialog_Trade.DoWindowContents` runs that every frame the
        // window is up. An observer never mutates: the silver row it reports is
        // therefore whatever the last `trade-set` / `trade-confirm` left, which
        // is the honest answer. `trade-set` and `trade-confirm` each call
        // `UpdateCurrencyCount()` themselves, so a `trade` read taken after
        // either one is current.
        //
        // `Tradeable`'s price getters read the STATIC session unguarded (e.g.
        // `public virtual bool TraderWillTrade => TradeSession.trader.TraderKind
        // .WillTrade(ThingDef);`) and NRE with no session active — which is why
        // every reader here refuses first when !TradeSession.Active.
        // ====================================================================
        [Verb("trade")]
        public static object Trade(VerbContext ctx)
        {
            const string V = "trade";
            if (!TradeSession.Active)
                return TradeRefuse(V, "no-session",
                    "RimWorld/TradeSession.Active => trader != null",
                    "no trade session is open. `trade-start {trader, negotiator}` first. "
                    + "(Reading a Tradeable with no session NREs: its price getters dereference "
                    + "TradeSession.trader directly.)");
            var d = Summary(ctx.Args, V);
            d["ok"] = true;
            d["action"] = NoStamp();
            return d;
        }

        // ====================================================================
        // trade-set {items:[{thing|index, count|buy|sell}]} — or the singular
        // {thing|index, count|buy|sell}.
        //
        // THE PLURAL FORM IS THE VERB (DESIGN §Action model): a ten-line deal
        // is one call, not ten round trips. Every item reports accepted or
        // rejected-with-reason, in the game's own words.
        //
        // SIGN CONVENTION is the game's, and the game states it on the window:
        // `Dialog_Trade.DoWindowContents` draws the literal label
        // "PositiveBuysNegativeSells" over the count column, and
        // `Tradeable.ActionToDo` is `CountToTransfer == 0 -> None`,
        // `CountToTransferToDestination > 0 -> PlayerSells`, else `PlayerBuys`.
        // `buy:` and `sell:` are unsigned aliases so a caller never has to
        // remember which way the sign points.
        //
        // FOUR WIDGET GATES, all in `RimWorld/TradeUI.DrawTradeableRow` and the
        // `TransferableUIUtility.DoCountAdjustInterface` it calls:
        //   1. `!trad.TraderWillTrade` — the adjust interface is NOT DRAWN;
        //      DrawWillNotTradeText("TraderWillNotTrade") takes its place.
        //   2. Ideology: `TransferableUIUtility.TradeIsPlayerSellingToSlavery(
        //      trad, TradeSession.trader.Faction)` and the negotiator's
        //      `HistoryEvent(SoldSlave).DoerWillingToDo()` — same, with
        //      "NegotiatorWillNotTradeSlaves".
        //   3. `DoCountAdjustInterface` opens `if (!trad.Interactive || readOnly)`
        //      and draws a read-only label. `Tradeable.Interactive` is FALSE for
        //      the currency row unless giftMode, so silver is not directly
        //      settable in a normal trade — `TradeDeal.UpdateCurrencyCount`
        //      owns it.
        //   4. The bounds. Every AdjustTo in the widget is already clamped to
        //      `GetMinimumToTransfer()`/`GetMaximumToTransfer()`; ours asks
        //      `CanAdjustTo` and refuses with the game's own UnderflowReport /
        //      OverflowReport, because `Transferable.AdjustTo` Log.Errors
        //      "Failed to adjust transferable counts" otherwise (trap 2).
        // ====================================================================
        [Verb("trade-set")]
        public static object TradeSet(VerbContext ctx)
        {
            const string V = "trade-set";
            if (!TradeSession.Active)
                return TradeRefuse(V, "no-session", "RimWorld/TradeSession.Active => trader != null",
                    "no trade session is open. `trade-start {trader, negotiator}` first.");

            var deal = TradeSession.deal ?? throw new VerbArgsException("the session has no deal");
            var all = new List<Tradeable>(deal.AllTradeables);

            var requests = new List<Dictionary<string, object>>();
            if (ctx.Args.Has("items"))
            {
                if (!(ctx.Args.Raw("items") is List<object> list))
                    throw new VerbArgsException("arg 'items' must be an array of objects");
                foreach (var o in list)
                {
                    if (!(o is Dictionary<string, object> item))
                        throw new VerbArgsException("every entry in 'items' must be an object "
                            + "{thing|index, count|buy|sell}");
                    requests.Add(item);
                }
                if (requests.Count == 0) throw new VerbArgsException("'items' must not be empty");
            }
            else
            {
                // Singular sugar: lift the top-level args into one request, so
                // the degenerate case reads naturally without a second verb.
                var one = new Dictionary<string, object>();
                foreach (var k in new[] { "thing", "index", "count", "buy", "sell" })
                    if (ctx.Args.Has(k)) one[k] = ctx.Args.Raw(k);
                requests.Add(one);
                if (one.Count == 0)
                    throw new VerbArgsException("pass 'items' (array) or the singular "
                        + "{thing|index, count|buy|sell}");
            }

            var accepted = new List<object>();
            var rejected = new List<object>();
            foreach (var req in requests)
            {
                Tradeable tr;
                string resolveError = ResolveTradeable(all, req, out tr);
                if (resolveError != null)
                {
                    rejected.Add(new Dictionary<string, object>
                    {
                        ["request"] = Echo(req),
                        ["gate"] = "not-found",
                        ["reason"] = resolveError,
                    });
                    continue;
                }

                int want;
                string countError = ResolveCount(req, out want);
                if (countError != null)
                {
                    rejected.Add(new Dictionary<string, object>
                    {
                        ["request"] = Echo(req),
                        ["thing"] = tr.ThingDef?.defName,
                        ["gate"] = "bad-count",
                        ["reason"] = countError,
                    });
                    continue;
                }

                // Gate 1.
                bool willTrade;
                try { willTrade = tr.TraderWillTrade; }
                catch (Exception e)
                {
                    rejected.Add(RejectUnreadable(tr, "trader-will-trade",
                        "RimWorld/TradeUI.DrawTradeableRow reads trad.TraderWillTrade; "
                        + "RimWorld/Tradeable.TraderWillTrade dereferences TradeSession.trader", e));
                    continue;
                }
                if (!willTrade)
                {
                    rejected.Add(Reject(tr, "trader-will-not-trade",
                        "RimWorld/TradeUI.DrawTradeableRow draws DrawWillNotTradeText("
                        + "\"TraderWillNotTrade\") INSTEAD of the count-adjust interface when "
                        + "!trad.TraderWillTrade",
                        WorldSafe.Safe(() => "TraderWillNotTrade".Translate().ToString())
                            ?? "this trader does not deal in this kind of thing"));
                    continue;
                }

                // Gate 2.
                if (ModsConfig.IdeologyActive)
                {
                    bool slaveryRefused;
                    try
                    {
                        slaveryRefused =
                            TransferableUIUtility.TradeIsPlayerSellingToSlavery(tr, TradeSession.trader.Faction)
                            && !new HistoryEvent(HistoryEventDefOf.SoldSlave,
                                    TradeSession.playerNegotiator.Named(HistoryEventArgsNames.Doer))
                                .DoerWillingToDo();
                    }
                    catch (Exception e)
                    {
                        rejected.Add(RejectUnreadable(tr, "negotiator-will-trade-slaves",
                            "RimWorld/TransferableUIUtility.TradeIsPlayerSellingToSlavery + "
                            + "HistoryEvent(SoldSlave).DoerWillingToDo()", e));
                        continue;
                    }
                    if (slaveryRefused)
                    {
                        rejected.Add(Reject(tr, "negotiator-will-not-trade-slaves",
                            "RimWorld/TradeUI.DrawTradeableRow draws DrawWillNotTradeText("
                            + "\"NegotiatorWillNotTradeSlaves\") instead of the adjust interface",
                            WorldSafe.Safe(() => "NegotiatorWillNotTradeSlaves"
                                .Translate(TradeSession.playerNegotiator).ToString())
                                ?? "the negotiator's ideology forbids selling this pawn into slavery"));
                        continue;
                    }
                }

                // Gate 3.
                bool interactive;
                try { interactive = tr.Interactive; }
                catch (Exception e)
                {
                    rejected.Add(RejectUnreadable(tr, "interactive",
                        "RimWorld/TransferableUIUtility.DoCountAdjustInterface opens with "
                        + "`if (!trad.Interactive || readOnly)`", e));
                    continue;
                }
                if (!interactive)
                {
                    rejected.Add(Reject(tr, "not-interactive",
                        "RimWorld/TransferableUIUtility.DoCountAdjustInterface opens with "
                        + "`if (!trad.Interactive || readOnly)` and draws a READ-ONLY label; "
                        + "RimWorld/Tradeable.Interactive is false for the currency row unless giftMode",
                        "silver is not directly settable in a normal trade — "
                        + "TradeDeal.UpdateCurrencyCount derives it from the rest of the deal"));
                    continue;
                }

                // Gate 4 — the red-error guard.
                AcceptanceReport adj;
                try { adj = tr.CanAdjustTo(want); }
                catch (Exception e)
                {
                    rejected.Add(Reject(tr, "adjust-threw", "RimWorld/Transferable.CanAdjustTo",
                        e.GetType().Name + ": " + Journal.Truncate(e.Message, 160)));
                    continue;
                }
                if (!adj.Accepted)
                {
                    int min = 0, max = 0;
                    try { min = tr.GetMinimumToTransfer(); max = tr.GetMaximumToTransfer(); } catch { }
                    var row = Reject(tr, "out-of-range",
                        "RimWorld/TradeUI.DrawTradeableRow passes trad.GetMinimumToTransfer()/"
                        + "GetMaximumToTransfer() into DoCountAdjustInterface, so every AdjustTo in the "
                        + "widget is pre-clamped; RimWorld/Transferable.AdjustTo Log.Errors "
                        + "\"Failed to adjust transferable counts\" on anything outside them",
                        string.IsNullOrEmpty(adj.Reason)
                            ? "count " + want + " is outside [" + min + ", " + max + "]"
                            : adj.Reason);
                    row["requested"] = want;
                    row["min"] = min;
                    row["max"] = max;
                    row["colony_has"] = Held(tr, Transactor.Colony);
                    row["trader_has"] = Held(tr, Transactor.Trader);
                    rejected.Add(row);
                    continue;
                }

                int before = tr.CountToTransfer;
                tr.AdjustTo(want);
                var line = TradeableLine(tr, all.IndexOf(tr));
                line["was"] = before;
                accepted.Add(line);
            }

            // What Dialog_Trade.DoWindowContents does every frame, and what the
            // Accept button's own pre-check reads.
            try { deal.UpdateCurrencyCount(); } catch (Exception e)
            { Journal.EmitWarning("trade-set: UpdateCurrencyCount threw: " + e.Message); }

            long seq = accepted.Count > 0
                ? Act(V, "set", WorldSafe.Safe(() => TradeSession.trader?.TraderName),
                    new Dictionary<string, object>
                    {
                        ["set"] = accepted.Count,
                        ["refused"] = rejected.Count,
                    })
                : 0;

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = rejected.Count == 0,
                ["accepted"] = accepted,
                ["rejected"] = rejected,
                ["action"] = accepted.Count > 0 ? Stamp(seq) : NoStamp(),
            };
            AddDealTotals(d);
            return d;
        }

        // ====================================================================
        // trade-confirm {allow_trader_short_funds?}
        //
        // Atomic: `TradeDeal.TryExecute` resolves EVERY tradeable or none, and
        // its own validation errors are surfaced verbatim.
        //
        // THREE PRE-CHECKS. THEY RUN 3, THEN 1, THEN 2 — the numbering below
        // is the widget's, the order is this verb's, and they differ on
        // purpose: the cheapest and most likely refusal (the trader has left)
        // is made before the deal is walked, and neither reordering changes an
        // outcome because all three refuse without transacting. Read the code's
        // own "Pre-check N" comments for what actually runs where.
        //  1. The NRE guard (trap 1) — the exact negation of TryExecute's
        //     cannot-afford condition, so that branch is unreachable. The
        //     refusal carries the game's own "MessageColonyCannotAfford".
        //  2. `TradeDeal.DoesTraderHaveEnoughSilver()`. When it is FALSE the
        //     widget does NOT execute: `Dialog_Trade.DoWindowContents` calls
        //     `FlashSilver()`, plays ClickReject and stacks
        //     `Dialog_MessageBox.CreateConfirmation("ConfirmTraderShortFunds", action)`.
        //     A modal is what 1.7 proves wedges an unattended run, so the
        //     confirmation is reproduced as an ARGUMENT: default false refuses,
        //     `allow_trader_short_funds:true` is the headless "Confirm".
        //  3. `ITrader.CanTradeNow`, re-read: a session opened before an
        //     `advance` can outlive the caravan that opened it. RUNS FIRST.
        //
        // AND THE SESSION IS CLOSED ON EVERY EXIT PATH, because
        // `TradeSession.Close()` has no vanilla caller — the window used to own
        // the lifecycle. `Dialog_Trade.Close()`'s one MODEL effect is
        // reproduced first: `if (TradeSession.trader is Pawn pawn &&
        // pawn.mindState.hasQuest) TradeUtility.ReceiveQuestFromTrader(pawn,
        // TradeSession.playerNegotiator);` (Odyssey only — it early-returns on
        // !ModsConfig.OdysseyActive). It is REPRODUCED rather than dropped
        // because it is a real consequence of ending a trade, and REPORTED
        // because it generates a quest and sends a letter.
        // ====================================================================
        [Verb("trade-confirm")]
        public static object TradeConfirm(VerbContext ctx)
        {
            const string V = "trade-confirm";
            if (!TradeSession.Active)
                return TradeRefuse(V, "no-session", "RimWorld/TradeSession.Active => trader != null",
                    "no trade session is open");

            var deal = TradeSession.deal ?? throw new VerbArgsException("the session has no deal");
            bool allowShort = ctx.Args.Bool("allow_trader_short_funds", false);
            bool gift = TradeSession.giftMode;
            string traderName = WorldSafe.Safe(() => TradeSession.trader.TraderName);

            // Pre-check 3.
            bool canTradeNow = false;
            try { canTradeNow = TradeSession.trader.CanTradeNow; } catch { }
            if (!canTradeNow)
                return TradeRefuse(V, "cannot-trade-now",
                    "RimWorld/JobDriver_TradeWithPawn's trade toil is `if (Trader.CanTradeNow)`, and its "
                    + "goto toil FailOn(() => !Trader.CanTradeNow)",
                    "'" + traderName + "' is no longer willing to trade — the session has outlived it. "
                    + "Nothing was transacted; `trade-cancel` to close the session.");

            // The window recomputes this every frame before drawing Accept.
            try { deal.UpdateCurrencyCount(); } catch { }

            // Snapshot the deal BEFORE it is executed and Reset()s: this is the
            // evidence the result echoes, and the Tradeable objects do not
            // survive the call.
            var planned = new List<object>();
            var watch = new List<Tradeable>();
            int plannedSilverColony = 0;
            try
            {
                foreach (var tr in deal.AllTradeables)
                {
                    if (tr == null) continue;
                    TradeAction act;
                    try { act = tr.ActionToDo; } catch { continue; }
                    if (act == TradeAction.None) continue;
                    watch.Add(tr);
                    planned.Add(TradeableLine(tr, -1));
                }
                var cur = deal.CurrencyTradeable;
                if (cur != null) plannedSilverColony = cur.CountHeldBy(Transactor.Colony);
            }
            catch { }

            // Pre-check 1 — THE NRE GUARD. Skipped in gift mode exactly as
            // TryExecute skips it (the gift branch returns before that line).
            if (!gift)
            {
                var cur = deal.CurrencyTradeable;
                int postDeal = 0;
                bool afford;
                try
                {
                    afford = cur != null && (postDeal = cur.CountPostDealFor(Transactor.Colony)) >= 0;
                }
                catch (Exception e)
                {
                    return TradeRefuse(V, "afford-check-threw",
                        "RimWorld/TradeDeal.TryExecute's cannot-afford condition",
                        "could not evaluate the colony's post-deal silver: "
                        + e.GetType().Name + ": " + Journal.Truncate(e.Message, 160));
                }
                if (!afford)
                {
                    var d0 = TradeRefuse(V, "colony-cannot-afford",
                        "RimWorld/TradeDeal.TryExecute: `if (CurrencyTradeable == null || "
                        + "CurrencyTradeable.CountPostDealFor(Transactor.Colony) < 0) { "
                        + "Find.WindowStack.WindowOfType<Dialog_Trade>().FlashSilver(); ... }` — that "
                        + "first line NREs with no Dialog_Trade on the stack, which is exactly the "
                        + "headless path, so the branch is pre-empted here rather than entered",
                        WorldSafe.Safe(() => "MessageColonyCannotAfford".Translate().ToString())
                            ?? "the colony cannot afford this deal");
                    d0["colony_silver"] = plannedSilverColony;
                    d0["colony_silver_post_deal"] = cur == null ? (object)null : postDeal;
                    d0["planned"] = planned;
                    d0["session"] = SessionHead();
                    d0["note"] = "NOTHING was transacted and the session is STILL OPEN — reduce the "
                        + "purchase with `trade-set` and retry, or `trade-cancel`. TradeDeal.TryExecute "
                        + "was never called, so its NRE was never reachable.";
                    return d0;
                }
            }

            // Pre-check 2 — the confirmation modal, as an argument.
            // This one is the reason the whole family was fixed: a `true`
            // default here meant a throwing modded ITrader skipped the
            // trader-short-funds refusal and went straight to TryExecute.
            bool traderHasSilver;
            try { traderHasSilver = deal.DoesTraderHaveEnoughSilver(); }
            catch (Exception e)
            {
                var dg = GateUnreadable(V, "trader-has-enough-silver",
                    "RimWorld/Dialog_Trade.DoWindowContents gates execution on "
                    + "TradeSession.deal.DoesTraderHaveEnoughSilver()", e);
                dg["planned"] = planned;
                dg["session"] = SessionHead();
                dg["note"] = "NOTHING was transacted and the session is STILL OPEN. "
                    + "TradeDeal.TryExecute was NOT called.";
                return dg;
            }
            if (!traderHasSilver && !allowShort)
            {
                var d1 = TradeRefuse(V, "trader-short-funds",
                    "RimWorld/Dialog_Trade.DoWindowContents does NOT execute when "
                    + "!TradeSession.deal.DoesTraderHaveEnoughSilver(): it calls FlashSilver(), plays "
                    + "ClickReject and stacks Dialog_MessageBox.CreateConfirmation("
                    + "\"ConfirmTraderShortFunds\", action)",
                    "the trader cannot cover this deal in silver; the game asks the player to confirm "
                    + "before executing (they would be paid in goods short). Re-send with "
                    + "allow_trader_short_funds:true to proceed, or reduce the sale with `trade-set`.");
                d1["planned"] = planned;
                d1["session"] = SessionHead();
                d1["note"] = "NOTHING was transacted and the session is STILL OPEN. A modal was "
                    + "deliberately not raised: a force-pausing window halts every subsequent advance "
                    + "at 0 ticks (spec 1.7).";
                return d1;
            }

            // ---------------------------- execute ---------------------------
            bool ok, actuallyTraded = false;
            string execError = null;
            try { ok = deal.TryExecute(out actuallyTraded); }
            catch (Exception e)
            {
                ok = false;
                execError = e.GetType().Name + ": " + Journal.Truncate(e.ToString(), 600);
                Journal.EmitWarning("trade-confirm: TradeDeal.TryExecute threw: " + e.Message);
            }

            // Post-trade counts, read from the REBUILT deal (TryExecute ends in
            // Reset(), which re-runs AddAllTradeables against the new world
            // state) — the honest "verify the write back".
            int silverAfter = 0;
            var after = new List<object>();
            try
            {
                var cur2 = deal.CurrencyTradeable;
                if (cur2 != null) silverAfter = cur2.CountHeldBy(Transactor.Colony);
                foreach (var tr in watch)
                {
                    var def = tr.ThingDef;
                    if (def == null) continue;
                    Tradeable now = null;
                    foreach (var t2 in deal.AllTradeables)
                        if (t2 != null && t2.ThingDef == def) { now = t2; break; }
                    after.Add(new Dictionary<string, object>
                    {
                        ["thing"] = def.defName,
                        ["colony_now"] = now == null ? 0 : Held(now, Transactor.Colony),
                        ["trader_now"] = now == null ? 0 : Held(now, Transactor.Trader),
                    });
                }
            }
            catch { }

            var closed = CloseSession();

            long seq = Act(V, "confirm", traderName, new Dictionary<string, object>
            {
                ["trader"] = traderName,
                ["gift"] = gift,
                ["executed"] = ok,
                ["actually_traded"] = actuallyTraded,
                ["lines"] = planned.Count,
                ["colony_silver_before"] = plannedSilverColony,
                ["colony_silver_after"] = silverAfter,
            });

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = ok,
                ["trader"] = traderName,
                ["gift_mode"] = gift,
                // TryExecute's own two-value answer, both surfaced: `ok` is
                // "the deal was allowed", `actually_traded` is "anything moved".
                ["executed"] = ok,
                ["actually_traded"] = actuallyTraded,
                ["transacted"] = planned,
                ["after"] = after,
                ["colony_silver_before"] = plannedSilverColony,
                ["colony_silver_after"] = silverAfter,
                ["colony_silver_delta"] = silverAfter - plannedSilverColony,
                ["session_closed"] = true,
                ["action"] = Stamp(seq),
            };
            if (execError != null) d["error"] = execError;
            foreach (var kv in closed) d[kv.Key] = kv.Value;
            if (!ok && execError == null)
                d["note"] = "TryExecute returned false without throwing. With Ideology active it does "
                    + "that when a HistoryEvent (TradedOrgan / SoldOrgan) is refused by the "
                    + "negotiator's ideo — `Notify_PawnAboutToDo` returning false. Nothing moved.";
            return d;
        }

        // ====================================================================
        // trade-cancel
        //
        // "Cancel leaves state untouched" NEEDS A DEFINITION, because
        // `TradeSession.Close()` is `trader = null;` and nothing else — `deal`,
        // `playerNegotiator` and `giftMode` stay set, so "untouched" cannot mean
        // the statics. THE DEFINITION USED HERE, and asserted in the acceptance:
        // NO `Tradeable.ResolveTrade` RAN; COLONY SILVER AND STOCK COUNTS ARE
        // UNCHANGED. What `trade-start` already spent — one scribed thing ID and
        // one Rand call from `ThingMaker.MakeThing(ThingDefOf.Silver)`, plus any
        // "MessageCannotSellItemsReason" message — is spent, and is reported
        // here rather than pretended away.
        // ====================================================================
        [Verb("trade-cancel")]
        public static object TradeCancel(VerbContext ctx)
        {
            const string V = "trade-cancel";
            if (!TradeSession.Active)
                return TradeRefuse(V, "no-session", "RimWorld/TradeSession.Active => trader != null",
                    "no trade session is open; nothing to cancel");

            string traderName = WorldSafe.Safe(() => TradeSession.trader.TraderName);
            int silver = 0;
            var abandoned = new List<object>();
            try
            {
                var deal = TradeSession.deal;
                var cur = deal?.CurrencyTradeable;
                if (cur != null) silver = cur.CountHeldBy(Transactor.Colony);
                if (deal != null)
                    foreach (var tr in deal.AllTradeables)
                    {
                        if (tr == null) continue;
                        TradeAction act;
                        try { act = tr.ActionToDo; } catch { continue; }
                        if (act == TradeAction.None) continue;
                        if (abandoned.Count < TradeableCap) abandoned.Add(TradeableLine(tr, -1));
                    }
            }
            catch { }

            var closed = CloseSession();

            long seq = Act(V, "cancel", traderName, new Dictionary<string, object>
            {
                ["trader"] = traderName,
                ["abandoned_lines"] = abandoned.Count,
            });

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = !TradeSession.Active,
                ["trader"] = traderName,
                ["abandoned"] = abandoned,
                ["colony_silver"] = silver,
                ["session_closed"] = !TradeSession.Active,
                ["action"] = Stamp(seq),
                ["untouched_means"] = "no Tradeable.ResolveTrade ran; colony silver and stock counts are "
                    + "unchanged. It does NOT mean the statics are pristine — TradeSession.Close() is "
                    + "`trader = null;` and nothing else, so deal/playerNegotiator/giftMode stay set "
                    + "(TradeSession.Active is the only reliable test) — and it does not undo what "
                    + "`trade-start` spent: one scribed thing ID plus one Rand call from "
                    + "TradeDeal.AddAllTradeables' ThingMaker.MakeThing(ThingDefOf.Silver).",
            };
            foreach (var kv in closed) d[kv.Key] = kv.Value;
            return d;
        }

        // ========================= shared plumbing ==========================

        // Reproduces `Dialog_Trade.Close`'s ONE model effect, then closes the
        // session — which nothing in vanilla ever does, because the window was
        // the owner.
        private static Dictionary<string, object> CloseSession()
        {
            var d = new Dictionary<string, object>();
            try
            {
                // Read the trader BEFORE Close() nulls it.
                if (TradeSession.trader is Pawn pawn)
                {
                    bool hasQuest = false;
                    try { hasQuest = pawn.mindState.hasQuest; } catch { }
                    if (hasQuest)
                    {
                        try
                        {
                            TradeUtility.ReceiveQuestFromTrader(pawn, TradeSession.playerNegotiator);
                            d["trader_quest"] = true;
                            d["trader_quest_note"] = "Dialog_Trade.Close() ends with `if "
                                + "(TradeSession.trader is Pawn pawn && pawn.mindState.hasQuest) "
                                + "TradeUtility.ReceiveQuestFromTrader(pawn, "
                                + "TradeSession.playerNegotiator);` — reproduced, because it is a real "
                                + "consequence of ending a trade. It is Odyssey-only, generates a quest "
                                + "with RNG and sends a letter; see `quests`.";
                        }
                        catch (Exception e)
                        {
                            d["trader_quest_error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 160);
                        }
                    }
                }
            }
            catch { }
            try { TradeSession.Close(); } catch (Exception e)
            { d["close_error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 160); }
            tradeOpenedBy = null;
            tradeOpenedTick = -1;
            return d;
        }

        // Opened from CommsVerbs.cs for the passing-ship route, which reaches
        // the same static session by a different set of gates.
        public static void OpenSessionUnchecked(ITrader trader, Pawn negotiator, bool gift)
            => TradeSession.SetupWith(trader, negotiator, gift);

        public static Dictionary<string, object> SessionSummary(VerbArgs args, string verb)
            => Summary(args, verb);

        private static Dictionary<string, object> Summary(VerbArgs args, string verb)
        {
            int cap = args.Int("cap", TradeableCap);
            if (cap < 1 || cap > 500) throw new VerbArgsException("cap must be 1..500");
            string filter = args.Str("filter", null);
            bool actionOnly = args.Bool("action_only", false);

            var deal = TradeSession.deal;
            var rows = new List<object>();
            int total = 0, shown = 0;
            try
            {
                var all = new List<Tradeable>(deal.AllTradeables);
                for (int i = 0; i < all.Count; i++)
                {
                    var tr = all[i];
                    if (tr == null) continue;
                    total++;
                    if (actionOnly)
                    {
                        TradeAction act;
                        try { act = tr.ActionToDo; } catch { continue; }
                        if (act == TradeAction.None) continue;
                    }
                    if (filter != null)
                    {
                        string label = tr.ThingDef?.defName ?? "";
                        string human = WorldSafe.Safe(() => tr.Label) ?? "";
                        if (label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                            && human.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    }
                    shown++;
                    if (rows.Count < cap) rows.Add(TradeableLine(tr, i));
                }
            }
            catch (Exception e)
            {
                Journal.EmitWarning(verb + ": tradeable enumeration threw: " + e.Message);
            }

            var d = new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["session"] = SessionHead(),
                ["tradeables"] = rows,
                ["tradeables_total"] = total,
                ["matched"] = shown,
                ["more"] = Math.Max(0, shown - rows.Count),
                ["index_note"] = "`index` is this tradeable's position in TradeDeal.AllTradeables and is "
                    + "the unambiguous handle for `trade-set`. It is stable for the life of the session "
                    + "but NOT across TryExecute, which ends in TradeDeal.Reset().",
                // What "colony_has" means, exactly, because it is not "on the
                // map": TradeDeal.AddAllTradeables walks
                // `TradeSession.trader.ColonyThingsWillingToBuy(negotiator)`,
                // which is the trade radius for a caravan pawn and the powered
                // orbital beacon cells for a ship. Silver in a distant stockpile
                // is invisible to the deal, exactly as it is in the window.
                ["colony_scope_note"] = "colony counts are what THIS trader can see — TradeDeal"
                    + ".AddAllTradeables walks ITrader.ColonyThingsWillingToBuy(negotiator), which is "
                    + "the caravan's trade radius or the powered orbital beacon cells, not the whole "
                    + "map. Goods outside it are not in the deal and never were in the window either.",
                ["sign_note"] = "count is the game's own convention — POSITIVE BUYS, NEGATIVE SELLS "
                    + "(Dialog_Trade.DoWindowContents draws the literal \"PositiveBuysNegativeSells\" "
                    + "label over this column). `buy:`/`sell:` are unsigned aliases in `trade-set`.",
            };
            AddDealTotals(d);
            return d;
        }

        private static Dictionary<string, object> SessionHead()
        {
            var d = new Dictionary<string, object>
            {
                ["active"] = TradeSession.Active,
                ["trader"] = WorldSafe.Safe(() => TradeSession.trader?.TraderName),
                ["trader_kind"] = WorldSafe.Safe(() => TradeSession.trader?.TraderKind?.LabelCap.ToString()),
                ["trader_type"] = TradeSession.trader?.GetType().Name,
                ["faction"] = WorldSafe.Safe(() => TradeSession.trader?.Faction?.Name),
                ["negotiator"] = WorldSafe.Safe(() => TradeSession.playerNegotiator?.LabelShortCap.ToString()),
                ["gift_mode"] = TradeSession.giftMode,
                ["currency"] = WorldSafe.Safe(() => TradeSession.trader == null
                    ? null : TradeSession.TradeCurrency.ToString()),
                ["can_trade_now"] = WorldSafe.SafeObj(() => (object)TradeSession.trader?.CanTradeNow),
                ["opened_by"] = tradeOpenedBy,
                ["opened_tick"] = tradeOpenedTick,
            };
            return d;
        }

        private static void AddDealTotals(Dictionary<string, object> d)
        {
            try
            {
                var deal = TradeSession.deal;
                if (deal == null) return;
                var cur = deal.CurrencyTradeable;
                var totals = new Dictionary<string, object>
                {
                    ["colony_silver"] = cur == null ? (object)null : Held(cur, Transactor.Colony),
                    ["trader_silver"] = cur == null ? (object)null : Held(cur, Transactor.Trader),
                    // The two numbers TryExecute's own guards read.
                    ["colony_silver_post_deal"] = cur == null ? (object)null
                        : WorldSafe.SafeObj(() => (object)cur.CountPostDealFor(Transactor.Colony)),
                    ["trader_silver_post_deal"] = cur == null ? (object)null
                        : WorldSafe.SafeObj(() => (object)cur.CountPostDealFor(Transactor.Trader)),
                    ["trader_can_cover"] = WorldSafe.SafeObj(() => (object)deal.DoesTraderHaveEnoughSilver()),
                };
                int lines = 0;
                double buyValue = 0, sellValue = 0;
                foreach (var tr in deal.AllTradeables)
                {
                    if (tr == null) continue;
                    TradeAction act;
                    try { act = tr.ActionToDo; } catch { continue; }
                    if (act == TradeAction.None) continue;
                    lines++;
                    try
                    {
                        if (act == TradeAction.PlayerBuys) buyValue += tr.CurTotalCurrencyCostForSource;
                        else sellValue += tr.CurTotalCurrencyCostForDestination;
                    }
                    catch { }
                }
                totals["lines_with_action"] = lines;
                totals["buy_value"] = Math.Round(buyValue, 1);
                totals["sell_value"] = Math.Round(sellValue, 1);
                d["totals"] = totals;
            }
            catch { }
        }

        private static Dictionary<string, object> TradeableLine(Tradeable tr, int index)
        {
            var d = new Dictionary<string, object>();
            if (index >= 0) d["index"] = index;
            d["thing"] = tr.ThingDef?.defName;
            d["label"] = WorldSafe.Safe(() => Journal.Truncate(tr.Label, 120));
            d["stuff"] = WorldSafe.Safe(() => tr.StuffDef?.defName);
            d["colony_has"] = Held(tr, Transactor.Colony);
            d["trader_has"] = Held(tr, Transactor.Trader);
            d["count"] = WorldSafe.SafeObj(() => (object)tr.CountToTransfer);
            d["action"] = WorldSafe.Safe(() => tr.ActionToDo.ToString());
            d["min"] = WorldSafe.SafeObj(() => (object)tr.GetMinimumToTransfer());
            d["max"] = WorldSafe.SafeObj(() => (object)tr.GetMaximumToTransfer());
            d["interactive"] = WorldSafe.SafeObj(() => (object)tr.Interactive);
            d["trader_will_trade"] = WorldSafe.SafeObj(() => (object)tr.TraderWillTrade);
            d["is_currency"] = WorldSafe.SafeObj(() => (object)tr.IsCurrency);
            // Prices come from Tradeable.GetPriceFor, which lazily caches into
            // the Tradeable's own private price fields off the STATIC session —
            // safe while a session is active, an NRE without one, which is why
            // every entry point refuses on !TradeSession.Active first.
            d["price_buy"] = WorldSafe.SafeObj(() => (object)Math.Round(
                (double)tr.GetPriceFor(TradeAction.PlayerBuys), 2));
            d["price_sell"] = WorldSafe.SafeObj(() => (object)Math.Round(
                (double)tr.GetPriceFor(TradeAction.PlayerSells), 2));
            d["market_value"] = WorldSafe.SafeObj(() => (object)Math.Round((double)tr.BaseMarketValue, 2));
            try
            {
                if (tr.ActionToDo != TradeAction.None)
                    d["line_value"] = Math.Round(tr.ActionToDo == TradeAction.PlayerBuys
                        ? (double)tr.CurTotalCurrencyCostForSource
                        : (double)tr.CurTotalCurrencyCostForDestination, 1);
            }
            catch { }
            return d;
        }

        private static int Held(Tradeable tr, Transactor t)
        {
            try { return tr.CountHeldBy(t); } catch { return 0; }
        }

        // A widget gate that THREW is not a widget gate that PASSED. Both of
        // these refuse rather than fall through to the permissive default; see
        // the file header.
        private static Dictionary<string, object> GateUnreadable(string verb, string gate,
            string cite, Exception e)
            => TradeRefuse(verb, "gate-unreadable", cite,
                "the '" + gate + "' widget gate could not be read: " + e.GetType().Name + ": "
                + Journal.Truncate(e.Message, 200) + ". A gate that throws has NOT passed, so the verb "
                + "refuses rather than executing on an unread precondition. This is almost always a "
                + "modded ITrader or Tradeable whose getter assumes the trade window is on the stack.",
                new Dictionary<string, object> { ["unreadable_gate"] = gate });

        private static Dictionary<string, object> RejectUnreadable(Tradeable tr, string gate,
            string cite, Exception e)
        {
            var d = Reject(tr, "gate-unreadable", cite,
                "the '" + gate + "' widget gate could not be read: " + e.GetType().Name + ": "
                + Journal.Truncate(e.Message, 200) + ". A gate that throws has NOT passed, so this item "
                + "was left untouched rather than set past an unread precondition.");
            d["unreadable_gate"] = gate;
            return d;
        }

        private static Dictionary<string, object> Reject(Tradeable tr, string gate, string cite, string reason)
            => new Dictionary<string, object>
            {
                ["thing"] = tr.ThingDef?.defName,
                ["label"] = WorldSafe.Safe(() => Journal.Truncate(tr.Label, 120)),
                ["gate"] = gate,
                ["gate_cite"] = cite,
                ["reason"] = reason,
            };

        private static Dictionary<string, object> Echo(Dictionary<string, object> req)
        {
            var d = new Dictionary<string, object>();
            foreach (var kv in req) d[kv.Key] = kv.Value is double dd ? (object)dd : kv.Value as string;
            return d;
        }

        private static string ResolveTradeable(List<Tradeable> all, Dictionary<string, object> req,
            out Tradeable found)
        {
            found = null;
            if (req.TryGetValue("index", out var iv) && iv is double id)
            {
                int i = (int)id;
                if (i < 0 || i >= all.Count)
                    return "index must be 0.." + (all.Count - 1) + " (see `trade`)";
                found = all[i];
                return found == null ? "tradeable at index " + i + " is null" : null;
            }
            if (!req.TryGetValue("thing", out var tv) || !(tv is string defName))
                return "every item needs 'thing' (a ThingDef defName) or 'index' (from `trade`)";

            var hits = new List<Tradeable>();
            for (int i = 0; i < all.Count; i++)
            {
                var tr = all[i];
                if (tr?.ThingDef == null) continue;
                if (string.Equals(tr.ThingDef.defName, defName, StringComparison.OrdinalIgnoreCase))
                    hits.Add(tr);
            }
            if (hits.Count == 1) { found = hits[0]; return null; }
            if (hits.Count == 0)
                return "no tradeable for ThingDef '" + defName + "' in this session (the trader does not "
                    + "carry it and the colony has none in a sellable position — see `trade`)";
            // Several rows share a def when stuff/quality/hit-points differ; the
            // widget shows them as separate rows and so does `trade`.
            return hits.Count + " tradeables share ThingDef '" + defName
                + "' (different stuff, quality or hit points) — address one by `index` from `trade`";
        }

        private static string ResolveCount(Dictionary<string, object> req, out int count)
        {
            count = 0;
            bool has = false;
            if (req.TryGetValue("count", out var cv))
            {
                if (!(cv is double cd)) return "'count' must be a number";
                count = (int)cd; has = true;
            }
            if (req.TryGetValue("buy", out var bv))
            {
                if (has) return "pass exactly one of 'count', 'buy', 'sell'";
                if (!(bv is double bd)) return "'buy' must be a number";
                if (bd < 0) return "'buy' must be >= 0 (use 'sell' to sell)";
                count = (int)bd; has = true;
            }
            if (req.TryGetValue("sell", out var sv))
            {
                if (has) return "pass exactly one of 'count', 'buy', 'sell'";
                if (!(sv is double sd)) return "'sell' must be a number";
                if (sd < 0) return "'sell' must be >= 0 (use 'buy' to buy)";
                count = -(int)sd; has = true;
            }
            return has ? null : "every item needs 'count' (signed: positive buys, negative sells), "
                + "or the unsigned 'buy' / 'sell'";
        }

        // The negotiator: a free colonist. Defaults to the highest Social skill
        // among those who pass the gates, because that is the pawn a player
        // sends and the price improvement is real — reported either way so the
        // choice is never silent.
        // `RimWorld/FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn` plus the
        // one clause of `RimWorld/FloatMenuOptionProvider.SelectedPawnValid`
        // that `FloatMenuOptionProvider_Trade` does not opt out of. Right-click
        // produces NO trade option at all for a pawn that fails these, so
        // neither may this verb. Returns null when the negotiator is fine.
        //
        // What is deliberately NOT reproduced, and why: vanilla has no
        // player-faction clause on this path — the float menu is built from
        // `Find.Selector.SelectedPawns` and `FloatMenuOptionProvider_Trade
        // .GetOptionsFor` never asks whose faction the negotiator is in. So a
        // prisoner or a guest is refused here only if they fail a real clause
        // above. The AUTO-PICKER is stricter (it walks
        // `map.mapPawns.FreeColonistsSpawned`), which is a choice about who to
        // pick, not a gate; an explicit `negotiator` gets the widget's rules,
        // exactly as DESIGN's action model requires.
        private static Dictionary<string, object> NegotiatorGate(string verb, Map map, Pawn negotiator)
        {
            const string SGC = "RimWorld/FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn";

            bool dead = false, downed = false, offMap = false, mech = false, deathresting = false;
            try { dead = negotiator.Dead; } catch { }
            try { downed = negotiator.Downed; } catch { }
            try { offMap = negotiator.Map != map; } catch { offMap = true; }
            try { mech = negotiator.RaceProps != null && negotiator.RaceProps.IsMechanoid; } catch { }
            try { deathresting = ModsConfig.BiotechActive && negotiator.Deathresting; } catch { }

            string who = WorldSafe.Safe(() => negotiator.LabelShortCap.ToString());

            if (dead || offMap)
                return TradeRefuse(verb, "negotiator-not-on-map",
                    SGC + " returns false when `pawn.Map != Find.CurrentMap`, so a right-click "
                    + "produces no float menu for it at all",
                    who + (dead ? " is dead" : " is not on the current map")
                    + " and cannot be given an order");

            if (downed)
                return TradeRefuse(verb, "negotiator-downed",
                    SGC + " returns \"IsIncapped\".Translate(...) when `pawn.Downed`",
                    WorldSafe.Safe(() => "IsIncapped".Translate(negotiator.LabelCap, negotiator).ToString())
                        ?? (who + " is incapable of doing that right now"));

            if (deathresting)
                return TradeRefuse(verb, "negotiator-deathresting",
                    SGC + " returns \"IsDeathresting\".Translate(...) when "
                    + "`ModsConfig.BiotechActive && pawn.Deathresting`",
                    WorldSafe.Safe(() => "IsDeathresting".Translate(negotiator.Named("PAWN")).ToString())
                        ?? (who + " is deathresting"));

            if (mech)
                return TradeRefuse(verb, "negotiator-mechanoid",
                    "RimWorld/FloatMenuOptionProvider.SelectedPawnValid returns false when "
                    + "`!MechanoidCanDo && pawn.RaceProps.IsMechanoid`, and "
                    + "RimWorld/FloatMenuOptionProvider_Trade does not override MechanoidCanDo "
                    + "(the base default is false)",
                    who + " is a mechanoid; the trade option is never offered for one");

            // `lord.AllowsFloatMenu(pawn)` — a pawn inside a ritual or a party
            // lord job has no float menu at all.
            try
            {
                var lord = negotiator.GetLord();
                if (lord != null)
                {
                    var report = lord.AllowsFloatMenu(negotiator);
                    if (!report.Accepted)
                        return TradeRefuse(verb, "negotiator-lord-refuses",
                            SGC + " returns `lord.AllowsFloatMenu(pawn)` when the pawn has a lord",
                            string.IsNullOrEmpty(report.Reason)
                                ? who + " is committed to a group activity and takes no orders right now"
                                : report.Reason);
                }
            }
            catch (Exception e)
            {
                return GateUnreadable(verb, "lord-allows-float-menu", SGC + " -> Lord.AllowsFloatMenu", e);
            }

            return null;
        }

        // NOTE: an explicit `negotiator` resolves through `Dev.PawnArg`, which
        // accepts ANY pawn on the map — none of the filters below apply to it.
        // `NegotiatorGate` above is what makes that safe; do not move the
        // widget gates into this picker, because then the explicit path would
        // skip them again.
        private static Pawn TradeNegotiatorArg(Map map, VerbArgs args)
        {
            if (args.Has("negotiator"))
                return Dev.PawnArg(map, args, "negotiator");
            Pawn best = null;
            int bestSkill = -1;
            try
            {
                foreach (var p in map.mapPawns.FreeColonistsSpawned)
                {
                    if (p == null || p.Downed || p.Dead || p.skills == null) continue;
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
            return best ?? throw new VerbArgsException("no free spawned colonist is capable of Social; "
                + "pass `negotiator` explicitly (a pawn id from `pawns`)");
        }

        // A map trader pawn, or a passing ship by name. NOT a world settlement:
        // Settlement_TraderTracker.StockListForReading REGENERATES the whole
        // inventory from a getter (see the file header), and DESIGN's v1
        // non-goals rule out caravans and settlements anyway.
        private static ITrader TraderArg(Map map, VerbArgs args, out string kindLabel, out Pawn asPawn)
        {
            kindLabel = null; asPawn = null;
            object raw = args.Raw("trader");
            if (raw == null)
                throw new VerbArgsException("missing required arg 'trader' (a trader pawn id from "
                    + "`pawns`, or a passing ship name from `comms-targets`)");

            if (raw is double d)
            {
                int id = (int)d;
                foreach (var p in map.mapPawns.AllPawnsSpawned)
                {
                    if (p == null || p.thingIDNumber != id) continue;
                    if (!(p is ITrader t))
                        throw new VerbArgsException("pawn " + id + " ("
                            + WorldSafe.Safe(() => p.LabelShortCap.ToString()) + ") is not a trader");
                    asPawn = p;
                    kindLabel = WorldSafe.Safe(() => t.TraderKind?.LabelCap.ToString());
                    return t;
                }
                throw new VerbArgsException("no spawned pawn with id " + id + " on the current map");
            }
            if (raw is string name)
            {
                try
                {
                    foreach (var ship in map.passingShipManager.passingShips)
                    {
                        if (ship == null) continue;
                        if (!string.Equals(ship.name, name, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(WorldSafe.Safe(() => ship.GetCallLabel()), name,
                                StringComparison.OrdinalIgnoreCase)) continue;
                        if (!(ship is ITrader t))
                            throw new VerbArgsException("passing ship '" + name + "' is not a trader");
                        kindLabel = WorldSafe.Safe(() => t.TraderKind?.LabelCap.ToString());
                        return t;
                    }
                }
                catch (VerbArgsException) { throw; }
                catch { }
                throw new VerbArgsException("no passing ship named '" + name
                    + "' (see `comms-targets`). A map trader is addressed by its pawn id instead.");
            }
            throw new VerbArgsException("arg 'trader' must be a pawn id (number) or a ship name (string)");
        }

        private static Dictionary<string, object> TradeRefuse(string verb, string gate, string cite,
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
