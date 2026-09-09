using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoRimmer
{
    // ========================================================================
    // MOD-AWARE BRIDGE — Nepo (Dorian.Nepo).
    //
    // Nepo is Dorian's own scenario mod: the colony starts with a comms console
    // and a rich father off-map, you call Dad, browse a catalog, and order
    // anything with a market value, delivered by drop pod. Its catalog is a
    // WINDOW; the agent plays through verbs. This file is the seam.
    //
    // It is NOT a dependency of this mod and must never become one — AutoRimmer
    // loads against benches that do not have it — so nothing here takes a
    // compile-time reference. Every member is resolved by name and SIGNATURE at
    // first use and latched.
    //
    // The four rules are `FswaBridge.cs`'s, which DESIGN's 2026-08-31 entry
    // (git-bug 1a072fa) says generalise to every third-party bridge this repo
    // grows. Restated here only where Nepo changes what they mean:
    //
    // 1. BIND BY SIGNATURE. `AccessTools.Method(t, "PriceFor")` with no
    //    parameter list does not merely risk drift here — it THROWS
    //    `AmbiguousMatchException` on arrival, because `CatalogBuilder` has
    //    three `PriceFor` overloads and `NepoGameComponent` has four
    //    `ScheduleShipment`s and two `TogglePin`s. Every lookup below passes the
    //    parameter-type array AND checks the return type AND checks
    //    static/instance; the pure-BCL ones are then turned into typed
    //    delegates so the CLR re-verifies a third time.
    //
    // 2. A REFLECTION THROW IS NEVER A LEGAL VALUE. Every reader here can
    //    answer "we could not look" distinctly from a real zero: `Available`
    //    plus `Unavailable` carry the reason, and no verb publishes a number it
    //    did not read.
    //
    // 3. READ THE WRITE BACK. `NepoSync.PlaceOrder` returns `void` and RETURNS
    //    SILENTLY on four separate conditions (null component, `!DadAvailable`,
    //    an empty payload, `!CanAfford`), and its decoder silently DROPS any
    //    manifest line whose def no longer resolves. So "the invoke did not
    //    throw" is no evidence whatever that an order was placed. `nepo-order`
    //    snapshots the balance and the pending list, calls, re-reads, and
    //    reports the DIFFERENCE — a disagreement is a rejection with a
    //    diagnosis, never a success.
    //
    // 4. ABSENCE IS NOT AN ERROR. A bench without Nepo answers "this verb needs
    //    Nepo, which is not loaded", by name, with no log line, no red error and
    //    no warning; every other verb in the tree is unchanged.
    //
    // ==================== WHY THIS DOES NOT CALL ScheduleShipment ============
    // `NepoGameComponent.ScheduleShipment(int, List<ItemOrderEntry>,
    // List<MechOrderEntry>, List<SlaveOrderEntry>, List<AnimalOrderEntry>)` is
    // public, is four overloads deep, and looks like the order API. It is not.
    // Its body is ONE line —
    //     pending.Add(new PendingShipment(Find.TickManager.TicksGame + delayTicks, …));
    // — and it therefore performs NO affordability check, NO debit (it never
    // calls `Spend`), no comms check, no orderability check, no
    // `allowSlavePurchases` check, no `WorldPawns` registration for a bought
    // slave, and no multiplayer sync. An order placed through it is FREE. A verb
    // built on it could not answer "what it cost" or "the balance after" —
    // the two things this verb set exists to report — because neither would
    // have moved.
    //
    // The catalog window does not call it either, except transitively:
    // `Window_DadCatalog.DoPlaceOrder` calls
    // `NepoSync.PlaceOrder(negotiator, NepoSync.EncodeOrder(…))`, and
    // `NepoSync.PlaceOrder` is the method that re-checks `DadAvailable`,
    // re-checks `CanAfford`, memoises the material preference, parks a bought
    // slave in `WorldPawns` with `PassToWorld(…, KeepForever)`, calls `Spend`,
    // and only THEN calls `ScheduleShipment`. It is also one of the eight
    // methods `MpBridge` registers with `MP.RegisterSyncMethod` ("catalog
    // place-order"); `ScheduleShipment` and `Spend` are not registered, so
    // calling either directly desyncs a multiplayer game.
    //
    // So the write path here is `EncodeOrder` + `PlaceOrder`, which is the
    // widget's own route, and the verb reproduces the gates the WIDGET has that
    // `PlaceOrder` does not (see NepoVerbs.cs `OrderGate`). `ScheduleShipment`
    // is deliberately NOT BOUND: binding a method we have decided not to call
    // would be one more thing to drift.
    //
    // ==================== THE PAYLOAD IS NEPO'S, NOT OURS ====================
    // `EncodeOrder` flattens the order to a `;`/`|`/`:`-delimited string that
    // `NepoSync.TryDecodeOrder` parses back. We could write that string
    // ourselves and skip constructing four of Nepo's types by reflection. We do
    // not, for one reason: a payload with the wrong section count is dropped by
    // `TryDecodeOrder` with a `Log.Error`, and a RED ERROR raised by
    // agent-supplied arguments breaches this repo's standing zero-red-errors
    // invariant. Letting Nepo encode its own format makes that unreachable.
    //
    // ==================== WHY THIS DOES NOT CALL RemoveRestock ==============
    // `NepoGameComponent.RemoveRestock(ThingDef)` is public, is named exactly
    // like the way to delete a standing rule, and is the ScheduleShipment trap
    // wearing a smaller hat: it has ZERO callers in Nepo, it has no `NepoSync`
    // wrapper, and it is not one of the eight methods `MpBridge.RegisterAll`
    // registers — so calling it would be an unsynced write to scribed state.
    // The catalog window cannot delete a rule either: its toggle only DISABLES
    // one, and `SetRestock` has no removal branch at all (a zeroed rule is
    // STORED, at `threshold 0 / target 1`, because `target` is floored at
    // `threshold + 1`). So "remove" is spelled `enabled:false`, and
    // `RemoveRestock` is deliberately NOT BOUND.
    //
    // Main thread only, like every other Verse-touching file here. Called from
    // the command drain at a safe point, so the latch needs no lock.
    // ========================================================================
    internal static class Nepo
    {
        public const string ModName = "Nepo";
        public const string PackageId = "Dorian.Nepo";

        private const string TComponent = "Nepo.NepoGameComponent";
        private const string TCatalog = "Nepo.CatalogBuilder";
        private const string TSync = "Nepo.NepoSync";
        private const string TConfig = "Nepo.NepoConfig";
        private const string TTuning = "Nepo.NepoTuning";
        private const string TItemEntry = "Nepo.ItemOrderEntry";
        private const string TMechEntry = "Nepo.MechOrderEntry";
        private const string TAnimalEntry = "Nepo.AnimalOrderEntry";
        private const string TSlaveEntry = "Nepo.SlaveOrderEntry";
        private const string TShipment = "Nepo.PendingShipment";
        private const string TDispatch = "Nepo.PendingDispatch";
        private const string TRestock = "Nepo.RestockOrder";

        // ---------------------------------------------------------- the latch

        private static bool resolved;
        private static string absentWhy;      // not loaded at all — the ordinary case
        private static string readDriftWhy;   // loaded, but the READ surface changed shape
        private static string orderDriftWhy;  // loaded and readable, but the WRITE surface changed

        // A THIRD TIER, AND THE REASON THERE IS ONE. `Nepo/MpBridge.cs`'s own
        // header argues that one drift flag covering two unrelated features is
        // the bug three sibling bridges shipped: the restock surface is a
        // different set of members from the order surface, changed by different
        // edits, and a drifted `SetRestockRule` must cost `nepo-restock` and
        // NOTHING ELSE. Both HALVES of the restock surface live in this tier —
        // its reads (`AllRestocks`, `GetRestock`, `ColonyAvailableCount`) as
        // well as its write — for the same reason facing the other way: a
        // renamed `AllRestocks` must not cost `nepo-catalog` its catalog.
        private static string restockDriftWhy;

        /// True when every member the READ verbs need is bound. False with
        /// `Unavailable` naming the reason — absence and drift read differently.
        public static bool Available
        {
            get { Resolve(); return absentWhy == null && readDriftWhy == null; }
        }

        /// Why the read surface is unavailable, in words that name the mod.
        /// Null when it IS available.
        public static string Unavailable
        {
            get { Resolve(); return absentWhy ?? readDriftWhy; }
        }

        /// True when an order can actually be placed. A half-bound bridge that
        /// could read the catalog but not encode an order is deliberately NOT
        /// order-available: rule 3 needs both halves.
        public static bool OrderAvailable
        {
            get { Resolve(); return Available && orderDriftWhy == null; }
        }

        public static string OrderUnavailable
        {
            get { Resolve(); return Unavailable ?? orderDriftWhy; }
        }

        /// True when a standing restock rule can be read AND written. Its own
        /// tier: a drifted restock surface disables `nepo-restock` alone.
        public static bool RestockAvailable
        {
            get { Resolve(); return Available && restockDriftWhy == null; }
        }

        public static string RestockUnavailable
        {
            get { Resolve(); return Unavailable ?? restockDriftWhy; }
        }

        /// True only when the mod is absent, as opposed to present-but-drifted.
        /// The two want different sentences and different follow-up.
        public static bool Absent { get { Resolve(); return absentWhy != null; } }

        // -------------------------------------------------------- read handles

        private static MethodInfo activeGetter;         // static NepoGameComponent.Active

        private static MethodInfo gBudget, gSpentUnlimited, gPendingCount, gPendingOrderCount;
        private static MethodInfo gHasPendingDispatch, gHasAffordableDispatch, gTicksUntilAllowance;
        private static MethodInfo gIsNepoGame, gDadAvailable, gConfig, gSlaveRosterNoGenerate;
        private static MethodInfo gSlaveOffersBlocked, mCanAfford;
        private static FieldInfo fPending, fPendingDispatches;

        private static FieldInfo cUnlimitedMoney, cInstantDelivery, cAllowSlavePurchases;
        private static FieldInfo cAllowMinifiable, cRequireCommsForRestock;
        private static FieldInfo cAllowancePerPeriod, cAllowancePeriodDays;

        private static Func<List<ThingDef>> orderableDefs;
        private static Func<List<PawnKindDef>> orderableMechs;
        private static Func<List<PawnKindDef>> orderableAnimals;
        private static Func<ThingDef, bool> isOrderable;
        private static Func<ThingDef, int> priceForDef;
        private static Func<ThingDef, ThingDef, int> priceForStuffed;
        private static Func<PawnKindDef, int> priceForMech;
        private static Func<PawnKindDef, int> priceForAnimal;
        private static Func<Pawn, int> priceForSlave;
        private static Func<PawnKindDef, string> animalTradeTag;
        private static Func<string, string> animalTagLabel;
        private static Func<ThingDef, bool> canCustomizeMaterial;
        private static Func<ThingDef, List<ThingDef>> allowedStuffs;

        private static FieldInfo sDeliveryTick, sManifest, sMechs, sSlaves, sAnimals;
        private static FieldInfo iDef, iCount, iStuff;
        private static FieldInfo mKind, mCount;
        private static FieldInfo aKind, aCount;
        private static FieldInfo slSlave;
        private static FieldInfo dDef, dCount, dStuff;

        // ------------------------------------------------------- write handles

        private static MethodInfo mEncodeOrder, mPlaceOrder;
        private static ConstructorInfo ctorItem, ctorMech, ctorAnimal;
        private static Type itemListType, mechListType, animalListType;

        // OPTIONAL, AND IN THE READ TIER ON PURPOSE. The arrival time is
        // something the READ verbs report, so binding it with the write half
        // would mean a drifted `EncodeOrder` silently costing `nepo-catalog`
        // its whole `mode` block. It is optional because losing it should cost
        // the ARRIVAL TIME and not the catalog: `DeliveryDelayTicks` answers
        // null, every caller says "unknown" rather than guessing, and only
        // `nepo-order` refuses on it — because an order whose arrival cannot be
        // stated is an order the agent cannot plan around.
        private static FieldInfo tDeliveryDelayTicks;
        private static string delayWhyNot;

        // ----------------------------------------------------- restock handles
        // Read and write together, in the tier described above.
        private static MethodInfo gAllRestocks, mGetRestock, mColonyAvailableCount;
        private static MethodInfo mSetRestockRule, mEncodeColor;
        private static FieldInfo rDef, rThreshold, rTarget, rEnabled, rStuff, rColor;
        private static Func<ThingDef, bool> canCustomizeColor;
        private static Func<List<Color>> stylingStationColors;

        // OPTIONAL, and for the same reason `tDeliveryDelayTicks` is: the sweep
        // cadence is something the verb REPORTS so the caller knows how long to
        // wait, not something it gates on. Losing it costs the number and says
        // so — it must not cost the verb.
        private static FieldInfo tRestockIntervalTicks;
        private static string sweepWhyNot;

        // ------------------------------------------------------------- resolve

        private static void Resolve()
        {
            if (resolved) return;
            // Latched BEFORE the work: a probe that throws must not re-run on
            // every frame of every subsequent call.
            resolved = true;
            try
            {
                Type comp = AccessTools.TypeByName(TComponent);
                if (comp == null)
                {
                    // The ordinary case on most benches, and not a fault: no
                    // warning, no log, just a reason a caller can print.
                    absentWhy = ModName + " (" + PackageId + ") is not loaded — no type " + TComponent
                        + " in any loaded assembly. The catalog, the balance and the drop-pod delivery "
                        + "are that mod's features; with it absent there is nothing to order from.";
                    return;
                }

                Type catalog = AccessTools.TypeByName(TCatalog);
                Type config = AccessTools.TypeByName(TConfig);
                Type shipment = AccessTools.TypeByName(TShipment);
                Type dispatch = AccessTools.TypeByName(TDispatch);
                Type itemEntry = AccessTools.TypeByName(TItemEntry);
                Type mechEntry = AccessTools.TypeByName(TMechEntry);
                Type animalEntry = AccessTools.TypeByName(TAnimalEntry);
                Type slaveEntry = AccessTools.TypeByName(TSlaveEntry);

                var drift = new List<string>();
                RequireType(catalog, TCatalog, drift);
                RequireType(config, TConfig, drift);
                RequireType(shipment, TShipment, drift);
                RequireType(dispatch, TDispatch, drift);
                RequireType(itemEntry, TItemEntry, drift);
                RequireType(mechEntry, TMechEntry, drift);
                RequireType(animalEntry, TAnimalEntry, drift);
                RequireType(slaveEntry, TSlaveEntry, drift);
                if (drift.Count > 0) { ReadDrifted(drift); return; }

                itemListType = typeof(List<>).MakeGenericType(itemEntry);
                mechListType = typeof(List<>).MakeGenericType(mechEntry);
                animalListType = typeof(List<>).MakeGenericType(animalEntry);
                Type slaveListType = typeof(List<>).MakeGenericType(slaveEntry);
                Type shipmentListType = typeof(List<>).MakeGenericType(shipment);
                Type dispatchListType = typeof(List<>).MakeGenericType(dispatch);

                // ---- the component ------------------------------------------
                activeGetter = Getter(comp, "Active", comp, true, drift);
                gBudget = Getter(comp, "Budget", typeof(int), false, drift);
                gSpentUnlimited = Getter(comp, "SpentWhileUnlimited", typeof(long), false, drift);
                gPendingCount = Getter(comp, "PendingCount", typeof(int), false, drift);
                gPendingOrderCount = Getter(comp, "PendingOrderCount", typeof(int), false, drift);
                gHasPendingDispatch = Getter(comp, "HasPendingDispatch", typeof(bool), false, drift);
                gHasAffordableDispatch = Getter(comp, "HasAffordableDispatch", typeof(bool), false, drift);
                gTicksUntilAllowance = Getter(comp, "TicksUntilNextAllowance", typeof(int), false, drift);
                gIsNepoGame = Getter(comp, "IsNepoGame", typeof(bool), false, drift);
                gDadAvailable = Getter(comp, "DadAvailable", typeof(bool), false, drift);
                gConfig = Getter(comp, "Config", config, false, drift);
                gSlaveOffersBlocked = Getter(comp, "SlaveOffersBlockedReason", typeof(string), false, drift);
                mCanAfford = Bind(comp, "CanAfford", typeof(bool), new[] { typeof(int) }, false, drift);

                // NOT `SlaveRoster`. That property GENERATES the roster when it
                // is null — a write-on-read of exactly the class `PawnSafe`
                // exists to refuse, and DESIGN §Observation model forbids an
                // observer to call it. `SlaveRosterNoGenerate` is Nepo's own
                // safe read (`slaveRoster ?? EmptyRoster`) and is the only one
                // this bridge binds, for reads AND for the roster `EncodeOrder`
                // needs to index a slave against.
                gSlaveRosterNoGenerate =
                    Getter(comp, "SlaveRosterNoGenerate", typeof(List<Pawn>), false, drift);

                // Private, because Nepo publishes only PendingCount. Reading
                // the list is the whole of `nepo-inbound`.
                fPending = PrivField(comp, "pending", shipmentListType, drift);
                fPendingDispatches = PrivField(comp, "pendingDispatches", dispatchListType, drift);

                // ---- the config ---------------------------------------------
                // Read, never written. Dorian sets his own mod's settings, and
                // the only sanctioned writer is `NepoSync.SetConfig`, which is
                // a synced command with cache invalidations attached.
                cUnlimitedMoney = PubField(config, "unlimitedMoney", typeof(bool), drift);
                cInstantDelivery = PubField(config, "instantDelivery", typeof(bool), drift);
                cAllowSlavePurchases = PubField(config, "allowSlavePurchases", typeof(bool), drift);
                cAllowMinifiable = PubField(config, "allowMinifiableBuildings", typeof(bool), drift);
                cRequireCommsForRestock = PubField(config, "requireCommsForRestock", typeof(bool), drift);
                cAllowancePerPeriod = PubField(config, "allowancePerPeriod", typeof(int), drift);
                cAllowancePeriodDays = PubField(config, "allowancePeriodDays", typeof(int), drift);

                // ---- the catalog --------------------------------------------
                orderableDefs = Del<Func<List<ThingDef>>>(
                    Bind(catalog, "OrderableDefs", typeof(List<ThingDef>), Type.EmptyTypes, true, drift));
                orderableMechs = Del<Func<List<PawnKindDef>>>(
                    Bind(catalog, "OrderableMechs", typeof(List<PawnKindDef>), Type.EmptyTypes, true, drift));
                orderableAnimals = Del<Func<List<PawnKindDef>>>(
                    Bind(catalog, "OrderableAnimals", typeof(List<PawnKindDef>), Type.EmptyTypes, true, drift));
                isOrderable = Del<Func<ThingDef, bool>>(
                    Bind(catalog, "IsOrderable", typeof(bool), new[] { typeof(ThingDef) }, true, drift));
                priceForDef = Del<Func<ThingDef, int>>(
                    Bind(catalog, "PriceFor", typeof(int), new[] { typeof(ThingDef) }, true, drift));
                priceForStuffed = Del<Func<ThingDef, ThingDef, int>>(
                    Bind(catalog, "PriceFor", typeof(int),
                        new[] { typeof(ThingDef), typeof(ThingDef) }, true, drift));
                priceForMech = Del<Func<PawnKindDef, int>>(
                    Bind(catalog, "PriceFor", typeof(int), new[] { typeof(PawnKindDef) }, true, drift));
                priceForAnimal = Del<Func<PawnKindDef, int>>(
                    Bind(catalog, "PriceForAnimal", typeof(int), new[] { typeof(PawnKindDef) }, true, drift));
                priceForSlave = Del<Func<Pawn, int>>(
                    Bind(catalog, "PriceForSlave", typeof(int), new[] { typeof(Pawn) }, true, drift));
                animalTradeTag = Del<Func<PawnKindDef, string>>(
                    Bind(catalog, "AnimalTradeTag", typeof(string), new[] { typeof(PawnKindDef) }, true, drift));
                animalTagLabel = Del<Func<string, string>>(
                    Bind(catalog, "AnimalTagLabel", typeof(string), new[] { typeof(string) }, true, drift));
                canCustomizeMaterial = Del<Func<ThingDef, bool>>(
                    Bind(catalog, "CanCustomizeMaterial", typeof(bool), new[] { typeof(ThingDef) }, true, drift));
                allowedStuffs = Del<Func<ThingDef, List<ThingDef>>>(
                    Bind(catalog, "AllowedStuffs", typeof(List<ThingDef>), new[] { typeof(ThingDef) }, true, drift));

                // ---- the manifest rows --------------------------------------
                sDeliveryTick = PubField(shipment, "deliveryTick", typeof(int), drift);
                sManifest = PubField(shipment, "manifest", itemListType, drift);
                sMechs = PubField(shipment, "mechs", mechListType, drift);
                sSlaves = PubField(shipment, "slaves", slaveListType, drift);
                sAnimals = PubField(shipment, "animals", animalListType, drift);

                iDef = PubField(itemEntry, "def", typeof(ThingDef), drift);
                iCount = PubField(itemEntry, "count", typeof(int), drift);
                iStuff = PubField(itemEntry, "stuff", typeof(ThingDef), drift);
                mKind = PubField(mechEntry, "kind", typeof(PawnKindDef), drift);
                mCount = PubField(mechEntry, "count", typeof(int), drift);
                aKind = PubField(animalEntry, "kind", typeof(PawnKindDef), drift);
                aCount = PubField(animalEntry, "count", typeof(int), drift);
                slSlave = PubField(slaveEntry, "slave", typeof(Pawn), drift);
                dDef = PubField(dispatch, "def", typeof(ThingDef), drift);
                dCount = PubField(dispatch, "count", typeof(int), drift);
                dStuff = PubField(dispatch, "stuff", typeof(ThingDef), drift);

                if (drift.Count > 0) { ReadDrifted(drift); return; }

                // ---- the delivery delay, optional (see the field's comment) --
                try
                {
                    var dDrift = new List<string>();
                    Type tuning = AccessTools.TypeByName(TTuning);
                    if (tuning == null) dDrift.Add("no type " + TTuning);
                    else
                        // `static readonly`, not `const` — a const would be
                        // inlined into Nepo's own callers and unreadable here.
                        tDeliveryDelayTicks = StaticField(tuning, "DeliveryDelayTicks", typeof(int), dDrift);
                    if (tDeliveryDelayTicks == null)
                        delayWhyNot = ModName + "'s delivery delay could not be read: "
                            + string.Join("; ", dDrift.ToArray());
                }
                catch (Exception dEx)
                {
                    tDeliveryDelayTicks = null;
                    delayWhyNot = "reading " + ModName + "'s delivery delay threw: "
                        + dEx.GetType().Name + ": " + Journal.Truncate(dEx.Message, 120);
                }

                // ---- THE WRITE HALF, IN ITS OWN GUARD -----------------------
                // Its failure costs the ORDER verb and nothing else: the two
                // read verbs above have already bound cleanly and must keep
                // working, which they would not if a throw down here fell into
                // the outer catch.
                try
                {
                    var wDrift = new List<string>();
                    Type sync = AccessTools.TypeByName(TSync);
                    RequireType(sync, TSync, wDrift);
                    if (wDrift.Count == 0)
                    {
                        // NOTE the parameter order, which is NOT ScheduleShipment's:
                        // EncodeOrder takes items, mechs, ANIMALS, SLAVES, roster;
                        // ScheduleShipment takes manifest, mechs, SLAVES, ANIMALS.
                        // The two are transposed, they are both List<T> of Nepo
                        // types, and nothing but this type array catches a swap.
                        mEncodeOrder = Bind(sync, "EncodeOrder", typeof(string),
                            new[]
                            {
                                typeof(int), itemListType, mechListType, animalListType,
                                typeof(List<Pawn>), typeof(List<Pawn>),
                            }, true, wDrift);
                        mPlaceOrder = Bind(sync, "PlaceOrder", typeof(void),
                            new[] { typeof(Pawn), typeof(string) }, true, wDrift);

                        ctorItem = Ctor(itemEntry,
                            new[] { typeof(ThingDef), typeof(int), typeof(ThingDef), typeof(Color?) }, wDrift);
                        ctorMech = Ctor(mechEntry, new[] { typeof(PawnKindDef), typeof(int) }, wDrift);
                        ctorAnimal = Ctor(animalEntry, new[] { typeof(PawnKindDef), typeof(int) }, wDrift);
                    }
                    if (wDrift.Count > 0)
                    {
                        orderDriftWhy = ModName + " is loaded and readable, but its ORDER api has drifted, "
                            + "so placing an order is disabled rather than guessed at: "
                            + string.Join("; ", wDrift.ToArray());
                        Log.Warning("[AutoRimmer] " + orderDriftWhy);
                        Journal.EmitWarning("[AutoRimmer] " + orderDriftWhy);
                    }
                }
                catch (Exception w)
                {
                    mEncodeOrder = null;
                    mPlaceOrder = null;
                    orderDriftWhy = "probing " + ModName + "'s order api threw, so placing an order is "
                        + "disabled (reading the catalog still works): " + w.GetType().Name + ": "
                        + Journal.Truncate(w.Message, 160);
                    Log.Warning("[AutoRimmer] " + orderDriftWhy);
                    Journal.EmitWarning("[AutoRimmer] " + orderDriftWhy);
                }

                // ---- THE RESTOCK SURFACE, IN ITS OWN GUARD ------------------
                // Standing rules: read AND write. Separate from the order half
                // above so neither can cost the other its verb.
                try
                {
                    var sDrift = new List<string>();
                    Type restock = AccessTools.TypeByName(TRestock);
                    Type sync = AccessTools.TypeByName(TSync);
                    RequireType(restock, TRestock, sDrift);
                    RequireType(sync, TSync, sDrift);
                    if (sDrift.Count == 0)
                    {
                        // `AllRestocks` is `restockOrders.Values` — the LIVE
                        // ValueCollection of Nepo's own dictionary, with no memo
                        // and no rebuild. It is therefore not a write-on-read
                        // hazard at all; the two hazards it does carry are
                        // handled at the accessor (a snapshot, so a write during
                        // a listing cannot throw InvalidOperationException) and
                        // in the verb (the rows it yields are Nepo's live
                        // objects and are never assigned to — writing
                        // `order.threshold` would bypass SetRestock's clamp, its
                        // SetMaterialPref side effect and multiplayer sync).
                        gAllRestocks = Getter(comp, "AllRestocks",
                            typeof(IEnumerable<>).MakeGenericType(restock), false, sDrift);
                        mGetRestock = Bind(comp, "GetRestock", restock,
                            new[] { typeof(ThingDef) }, false, sDrift);
                        // The window's own "you have N" readout. A pure read:
                        // an indexed `listerThings` lookup plus haul-source and
                        // carried counts plus the in-flight/dispatch sums.
                        mColonyAvailableCount = Bind(comp, "ColonyAvailableCount", typeof(int),
                            new[] { typeof(ThingDef) }, false, sDrift);

                        // THE SYNCED ENTRY POINT, and the only write here.
                        // `NepoGameComponent.SetRestock` is public and is what
                        // this ends up calling, but it is not registered with
                        // `MP.RegisterSyncMethod` — `NepoSync.SetRestockRule`
                        // is (Nepo/MpBridge.RegisterAll, "restock rule write").
                        // Note also that SetRestock's last two parameters are
                        // OPTIONAL: `AccessTools.Method(comp, "SetRestock",
                        // new[]{ThingDef,int,int,bool})` returns NULL, because
                        // GetMethod does not match on optionals. Binding the
                        // four-type array and calling it would have been two
                        // mistakes at once, and is not done.
                        mSetRestockRule = Bind(sync, "SetRestockRule", typeof(void),
                            new[]
                            {
                                typeof(ThingDef), typeof(int), typeof(int), typeof(bool),
                                typeof(ThingDef), typeof(string),
                            }, true, sDrift);
                        // The colour crosses the wire as a STRING, because
                        // `Color?` is not a type Multiplayer serialises. This is
                        // the header's "the payload stays Nepo's" rule a second
                        // time: `DecodeColor` is private and its format is
                        // Nepo's business, so we never hand-format "r,g,b,a".
                        mEncodeColor = Bind(sync, "EncodeColor", typeof(string),
                            new[] { typeof(Color?) }, true, sDrift);

                        rDef = PubField(restock, "def", typeof(ThingDef), sDrift);
                        rThreshold = PubField(restock, "threshold", typeof(int), sDrift);
                        rTarget = PubField(restock, "target", typeof(int), sDrift);
                        rEnabled = PubField(restock, "enabled", typeof(bool), sDrift);
                        rStuff = PubField(restock, "stuff", typeof(ThingDef), sDrift);
                        // `UnityEngine.Color?`, NOT a ColorDef — `PubField`
                        // checks FieldType, so getting this wrong would read as
                        // drift and disable the verb.
                        rColor = PubField(restock, "color", typeof(Color?), sDrift);

                        // The customize panel's two remaining gates. The
                        // material pair is already bound in the read tier and is
                        // reused; these two are colour-only and belong here.
                        canCustomizeColor = Del<Func<ThingDef, bool>>(
                            Bind(catalog, "CanCustomizeColor", typeof(bool),
                                new[] { typeof(ThingDef) }, true, sDrift));
                        stylingStationColors = Del<Func<List<Color>>>(
                            Bind(catalog, "StylingStationColors", typeof(List<Color>),
                                Type.EmptyTypes, true, sDrift));
                    }
                    if (sDrift.Count > 0)
                    {
                        restockDriftWhy = ModName + " is loaded and readable, but its STANDING RESTOCK "
                            + "api has drifted, so reading and writing restock rules is disabled rather "
                            + "than guessed at (the catalog, ordering and inbound are unaffected): "
                            + string.Join("; ", sDrift.ToArray());
                        Log.Warning("[AutoRimmer] " + restockDriftWhy);
                        Journal.EmitWarning("[AutoRimmer] " + restockDriftWhy);
                    }

                    // ---- the sweep cadence, optional (see the field's comment)
                    try
                    {
                        var cDrift = new List<string>();
                        Type tuning = AccessTools.TypeByName(TTuning);
                        if (tuning == null) cDrift.Add("no type " + TTuning);
                        else
                            // A `const`, unlike DeliveryDelayTicks. Reflection
                            // reads a literal field's value fine — it is only
                            // Nepo's OWN callers that got it inlined.
                            tRestockIntervalTicks =
                                StaticField(tuning, "RestockCheckIntervalTicks", typeof(int), cDrift);
                        if (tRestockIntervalTicks == null)
                            sweepWhyNot = ModName + "'s restock sweep interval could not be read: "
                                + string.Join("; ", cDrift.ToArray());
                    }
                    catch (Exception cEx)
                    {
                        tRestockIntervalTicks = null;
                        sweepWhyNot = "reading " + ModName + "'s restock sweep interval threw: "
                            + cEx.GetType().Name + ": " + Journal.Truncate(cEx.Message, 120);
                    }
                }
                catch (Exception s)
                {
                    mSetRestockRule = null;
                    mGetRestock = null;
                    gAllRestocks = null;
                    restockDriftWhy = "probing " + ModName + "'s standing-restock api threw, so "
                        + "`nepo-restock` is disabled (the catalog, ordering and inbound still work): "
                        + s.GetType().Name + ": " + Journal.Truncate(s.Message, 160);
                    Log.Warning("[AutoRimmer] " + restockDriftWhy);
                    Journal.EmitWarning("[AutoRimmer] " + restockDriftWhy);
                }
            }
            catch (Exception e)
            {
                readDriftWhy = "probing " + ModName + " threw, so every nepo verb is disabled: "
                    + e.GetType().Name + ": " + Journal.Truncate(e.Message, 200);
                Log.Warning("[AutoRimmer] " + readDriftWhy);
                Journal.EmitWarning("[AutoRimmer] " + readDriftWhy);
            }
        }

        private static void ReadDrifted(List<string> drift)
        {
            // Loaded but changed shape. That IS a fault and it is loud: silence
            // here is how a bridge rots for a release without anyone noticing.
            readDriftWhy = ModName + " is loaded but its catalog api has drifted, so the nepo verbs "
                + "are disabled rather than guessed at: " + string.Join("; ", drift.ToArray());
            Log.Warning("[AutoRimmer] " + readDriftWhy);
            Journal.EmitWarning("[AutoRimmer] " + readDriftWhy);
        }

        // ------------------------------------------------------ bind helpers

        private static void RequireType(Type t, string name, List<string> drift)
        {
            if (t == null) drift.Add("no type " + name + " in the loaded " + ModName + " assembly");
        }

        /// Name AND parameter types AND return type AND static-ness. The type
        /// array is not optional here — three of the methods this file binds are
        /// overloaded, and `AccessTools.Method(t, name)` with no array throws
        /// `AmbiguousMatchException` on those rather than merely mis-binding.
        private static MethodInfo Bind(Type owner, string name, Type ret, Type[] args,
            bool wantStatic, List<string> drift)
        {
            MethodInfo m = AccessTools.Method(owner, name, args);
            if (m == null)
            {
                drift.Add("no " + owner.FullName + "." + name + "(" + Params(args) + ")");
                return null;
            }
            if (m.IsStatic != wantStatic)
            {
                drift.Add(owner.FullName + "." + name + " is "
                    + (m.IsStatic ? "now static" : "no longer static"));
                return null;
            }
            if (m.ReturnType != ret)
            {
                drift.Add(owner.FullName + "." + name + " now returns " + m.ReturnType.Name
                    + ", expected " + ret.Name);
                return null;
            }
            return m;
        }

        private static MethodInfo Getter(Type owner, string name, Type ret, bool wantStatic,
            List<string> drift)
        {
            MethodInfo g = AccessTools.PropertyGetter(owner, name);
            if (g == null)
            {
                drift.Add("no readable property " + owner.FullName + "." + name);
                return null;
            }
            if (g.IsStatic != wantStatic)
            {
                drift.Add(owner.FullName + "." + name + " is "
                    + (g.IsStatic ? "now static" : "no longer static"));
                return null;
            }
            if (ret != null && g.ReturnType != ret)
            {
                drift.Add(owner.FullName + "." + name + " now reads " + g.ReturnType.Name
                    + ", expected " + ret.Name);
                return null;
            }
            return g;
        }

        private static FieldInfo PubField(Type owner, string name, Type ft, List<string> drift)
            => FieldOf(owner, name, ft, false, false, drift);

        private static FieldInfo PrivField(Type owner, string name, Type ft, List<string> drift)
            => FieldOf(owner, name, ft, false, true, drift);

        private static FieldInfo StaticField(Type owner, string name, Type ft, List<string> drift)
            => FieldOf(owner, name, ft, true, false, drift);

        private static FieldInfo FieldOf(Type owner, string name, Type ft, bool wantStatic,
            bool allowPrivate, List<string> drift)
        {
            FieldInfo f = AccessTools.DeclaredField(owner, name);
            if (f == null)
            {
                drift.Add("no field " + owner.FullName + "." + name);
                return null;
            }
            if (f.FieldType != ft)
            {
                drift.Add(owner.FullName + "." + name + " is now " + f.FieldType.Name
                    + ", expected " + ft.Name);
                return null;
            }
            if (f.IsStatic != wantStatic)
            {
                drift.Add(owner.FullName + "." + name + " is "
                    + (f.IsStatic ? "now static" : "no longer static"));
                return null;
            }
            // A field that was public and went private is a deliberate change of
            // contract on Nepo's side, and reading it anyway is how a bridge
            // ends up depending on something its author moved on purpose.
            if (!allowPrivate && !f.IsPublic)
            {
                drift.Add(owner.FullName + "." + name + " is no longer public");
                return null;
            }
            return f;
        }

        private static ConstructorInfo Ctor(Type owner, Type[] args, List<string> drift)
        {
            ConstructorInfo c = AccessTools.Constructor(owner, args);
            if (c == null) drift.Add("no " + owner.FullName + " ctor(" + Params(args) + ")");
            return c;
        }

        /// The third verification: `Delegate.CreateDelegate` re-checks the whole
        /// signature against `T` and throws if it disagrees, so a method that
        /// slipped past `Bind` cannot slip past this.
        private static T Del<T>(MethodInfo m) where T : class
            => m == null ? null : (T)(object)Delegate.CreateDelegate(typeof(T), m);

        private static string Params(Type[] args)
        {
            var names = new string[args.Length];
            for (int i = 0; i < args.Length; i++) names[i] = args[i].Name;
            return string.Join(", ", names);
        }

        // ============================== READS ================================
        // Every one of these assumes `Available` and a non-null component where
        // one is taken; the verbs check both before they call.

        /// The game component, or null before a game is loaded. `Active` is
        /// `Current.Game?.GetComponent<NepoGameComponent>()`, a plain scan over
        /// `Game.components` (Verse/Game.GetComponent<T>) that creates nothing —
        /// which is why the observer surface is allowed to call it.
        public static object Component()
        {
            Resolve();
            if (!Available) return null;
            try { return activeGetter.Invoke(null, null); }
            catch { return null; }
        }

        public static int Budget(object c) => (int)gBudget.Invoke(c, null);
        public static long SpentWhileUnlimited(object c) => (long)gSpentUnlimited.Invoke(c, null);
        public static int PendingCount(object c) => (int)gPendingCount.Invoke(c, null);
        public static int PendingOrderCount(object c) => (int)gPendingOrderCount.Invoke(c, null);
        public static bool HasPendingDispatch(object c) => (bool)gHasPendingDispatch.Invoke(c, null);
        public static bool HasAffordableDispatch(object c) => (bool)gHasAffordableDispatch.Invoke(c, null);
        public static int TicksUntilAllowance(object c) => (int)gTicksUntilAllowance.Invoke(c, null);
        public static bool IsNepoGame(object c) => (bool)gIsNepoGame.Invoke(c, null);
        public static bool DadAvailable(object c) => (bool)gDadAvailable.Invoke(c, null);
        public static bool CanAfford(object c, int cost) => (bool)mCanAfford.Invoke(c, new object[] { cost });
        public static string SlaveOffersBlockedReason(object c)
            => (string)gSlaveOffersBlocked.Invoke(c, null);
        public static List<Pawn> SlaveRoster(object c)
            => (List<Pawn>)gSlaveRosterNoGenerate.Invoke(c, null) ?? new List<Pawn>();

        private static object Config(object c) => gConfig.Invoke(c, null);

        public static bool UnlimitedMoney(object c) => (bool)cUnlimitedMoney.GetValue(Config(c));
        public static bool InstantDelivery(object c) => (bool)cInstantDelivery.GetValue(Config(c));
        public static bool AllowSlavePurchases(object c) => (bool)cAllowSlavePurchases.GetValue(Config(c));
        public static bool AllowMinifiableBuildings(object c) => (bool)cAllowMinifiable.GetValue(Config(c));
        public static bool RequireCommsForRestock(object c)
            => (bool)cRequireCommsForRestock.GetValue(Config(c));
        public static int AllowancePerPeriod(object c) => (int)cAllowancePerPeriod.GetValue(Config(c));
        public static int AllowancePeriodDays(object c) => (int)cAllowancePeriodDays.GetValue(Config(c));

        /// `Window_DadCatalog.DeliveryDelay()` is private; this is it, and it is
        /// the only place the arrival tick can come from. `null` means WE COULD
        /// NOT LOOK (rule 2), never zero — zero is the legal value that
        /// `instantDelivery` produces, and the two must never read alike.
        public static int? DeliveryDelayTicks(object c)
        {
            if (InstantDelivery(c)) return 0;
            if (tDeliveryDelayTicks == null) return null;
            return (int)tDeliveryDelayTicks.GetValue(null);
        }

        /// Why the delivery delay is unknown. Null when it is known.
        public static string DelayUnavailable { get { Resolve(); return delayWhyNot; } }

        // ---- catalog --------------------------------------------------------
        // The three list builders memoise into private statics and hand back the
        // LIVE cached list, so nothing here ever mutates what it is given. That
        // memo is not scribed state and Nepo invalidates it itself
        // (`CatalogBuilder.InvalidateCache` from `FinalizeInit` and from
        // `NepoSync.SetConfig`), so building it on a read is the `Def.LabelCap`
        // case and not the write-on-read class `PawnSafe` refuses.
        public static List<ThingDef> OrderableDefs() => orderableDefs();
        public static List<PawnKindDef> OrderableMechs() => orderableMechs();
        public static List<PawnKindDef> OrderableAnimals() => orderableAnimals();
        public static bool IsOrderable(ThingDef d) => isOrderable(d);
        public static int PriceFor(ThingDef d) => priceForDef(d);
        public static int PriceFor(ThingDef d, ThingDef stuff)
            => stuff == null ? priceForDef(d) : priceForStuffed(d, stuff);
        public static int PriceForMech(PawnKindDef k) => priceForMech(k);
        public static int PriceForAnimal(PawnKindDef k) => priceForAnimal(k);
        public static int PriceForSlave(Pawn p) => priceForSlave(p);
        public static string AnimalTradeTag(PawnKindDef k) => animalTradeTag(k);
        public static string AnimalTagLabel(string tag) => animalTagLabel(tag);
        public static bool CanCustomizeMaterial(ThingDef d) => canCustomizeMaterial(d);
        public static List<ThingDef> AllowedStuffs(ThingDef d) => allowedStuffs(d) ?? new List<ThingDef>();

        // ---- in-flight ------------------------------------------------------

        /// One row per scheduled shipment, oldest first, in `pending` order.
        /// Read-only: the list is Nepo's and is never touched.
        public static List<object> PendingShipments(object c)
        {
            var outp = new List<object>();
            var raw = fPending.GetValue(c) as IEnumerable;
            if (raw == null) return outp;
            foreach (var s in raw) if (s != null) outp.Add(s);
            return outp;
        }

        public static List<object> PendingDispatches(object c)
        {
            var outp = new List<object>();
            var raw = fPendingDispatches.GetValue(c) as IEnumerable;
            if (raw == null) return outp;
            foreach (var d in raw) if (d != null) outp.Add(d);
            return outp;
        }

        public static int ShipmentTick(object s) => (int)sDeliveryTick.GetValue(s);

        public static List<object> ShipmentItems(object s) => Rows(sManifest.GetValue(s));
        public static List<object> ShipmentMechs(object s) => Rows(sMechs.GetValue(s));
        public static List<object> ShipmentAnimals(object s) => Rows(sAnimals.GetValue(s));
        public static List<object> ShipmentSlaves(object s) => Rows(sSlaves.GetValue(s));

        private static List<object> Rows(object list)
        {
            var outp = new List<object>();
            if (list is IEnumerable e) foreach (var x in e) if (x != null) outp.Add(x);
            return outp;
        }

        public static ThingDef ItemDef(object e) => (ThingDef)iDef.GetValue(e);
        public static int ItemCount(object e) => (int)iCount.GetValue(e);
        public static ThingDef ItemStuff(object e) => (ThingDef)iStuff.GetValue(e);
        public static PawnKindDef MechKind(object e) => (PawnKindDef)mKind.GetValue(e);
        public static int MechCount(object e) => (int)mCount.GetValue(e);
        public static PawnKindDef AnimalKind(object e) => (PawnKindDef)aKind.GetValue(e);
        public static int AnimalCount(object e) => (int)aCount.GetValue(e);
        public static Pawn SlavePawn(object e) => (Pawn)slSlave.GetValue(e);
        public static ThingDef DispatchDef(object e) => (ThingDef)dDef.GetValue(e);
        public static int DispatchCount(object e) => (int)dCount.GetValue(e);
        public static ThingDef DispatchStuff(object e) => (ThingDef)dStuff.GetValue(e);

        // ============================== THE WRITE ============================

        /// Build one `Nepo.ItemOrderEntry`. `color` is deliberately always null:
        /// Nepo's catalog can tint a coloured item, the choice does not move the
        /// price (`CatalogBuilder.PriceFor` reads only def and stuff), and a
        /// colour argument would be one more shape for the agent to get wrong on
        /// a verb whose job is to keep a bench fed. Named so the omission is a
        /// decision rather than an oversight.
        public static object ItemEntry(ThingDef def, int count, ThingDef stuff)
            => ctorItem.Invoke(new object[] { def, count, stuff, (Color?)null });

        public static object MechEntry(PawnKindDef kind, int count)
            => ctorMech.Invoke(new object[] { kind, count });

        public static object AnimalEntry(PawnKindDef kind, int count)
            => ctorAnimal.Invoke(new object[] { kind, count });

        public static IList NewItemList() => (IList)Activator.CreateInstance(itemListType);
        public static IList NewMechList() => (IList)Activator.CreateInstance(mechListType);
        public static IList NewAnimalList() => (IList)Activator.CreateInstance(animalListType);

        /// Nepo's own wire encoder. See the header: we do not write this string
        /// ourselves, because a malformed one is dropped with a `Log.Error` and
        /// this repo does not raise red errors from agent input.
        public static string EncodeOrder(int delayTicks, IList items, IList mechs, IList animals,
            List<Pawn> slaves, List<Pawn> roster)
            => (string)mEncodeOrder.Invoke(null,
                new object[] { delayTicks, items, mechs, animals, slaves, roster });

        /// The widget's own confirm path. Returns void and bails silently on
        /// four conditions — the caller MUST read the write back.
        public static void PlaceOrder(Pawn negotiator, string payload)
            => mPlaceOrder.Invoke(null, new object[] { negotiator, payload });

        // ========================= STANDING RESTOCK ==========================

        /// Every standing rule, SNAPSHOTTED into our own list.
        ///
        /// `AllRestocks` hands back `restockOrders.Values`, the live
        /// `Dictionary.ValueCollection`. Listing straight off it while writing
        /// in the same pass would throw `InvalidOperationException` the moment a
        /// `SetRestock` for a def not already in the dictionary INSERTS — so the
        /// snapshot is not tidiness, it is the fix. Same shape as
        /// `PendingShipments` / `PendingDispatches` above.
        ///
        /// The objects inside are still Nepo's live `RestockOrder`s. Read them;
        /// never assign to one. `order.threshold = n` would bypass
        /// `SetRestock`'s clamp, bypass its `SetMaterialPref` side effect and
        /// bypass multiplayer sync — three silent divergences for one
        /// convenience.
        public static List<object> AllRestocks(object c)
        {
            var outp = new List<object>();
            var raw = gAllRestocks.Invoke(c, null) as IEnumerable;
            if (raw == null) return outp;
            foreach (var r in raw) if (r != null) outp.Add(r);
            return outp;
        }

        /// The rule for one def, or null when there is none.
        public static object GetRestock(object c, ThingDef def)
            => mGetRestock.Invoke(c, new object[] { def });

        /// The window's "you have N": everything on `Find.AnyPlayerHomeMap`
        /// (storage, loose stacks, haul-source containers, carried) plus
        /// everything already committed (in-flight drops and dispatches
        /// awaiting a call). Which map that is matters with two home maps, and
        /// the verb reports it rather than saying "the colony".
        public static int ColonyAvailableCount(object c, ThingDef def)
            => (int)mColonyAvailableCount.Invoke(c, new object[] { def });

        public static ThingDef RestockRuleDef(object r) => (ThingDef)rDef.GetValue(r);
        public static int RestockThreshold(object r) => (int)rThreshold.GetValue(r);
        public static int RestockTarget(object r) => (int)rTarget.GetValue(r);
        public static bool RestockEnabled(object r) => (bool)rEnabled.GetValue(r);
        public static ThingDef RestockStuff(object r) => (ThingDef)rStuff.GetValue(r);
        public static Color? RestockColor(object r) => (Color?)rColor.GetValue(r);

        /// Nepo's own colour encoder, for the same reason `EncodeOrder` is used
        /// rather than hand-written: the wire format is Nepo's business.
        public static string EncodeColor(Color? color)
            => (string)mEncodeColor.Invoke(null, new object[] { color });

        /// The synced create-or-update. Returns void and returns SILENTLY on a
        /// null component and on a null def, so the caller MUST re-read with
        /// `GetRestock` and compare — post-clamp, because `SetRestock` floors
        /// `threshold` at 0 and raises `target` to at least `threshold + 1`.
        ///
        /// NOT `ToggleRestockRule`, which is the window's own CREATE path (its
        /// number fields only reach the wire when a rule already exists, so the
        /// on/off button is the sole creator). `SetRestockRule` is idempotent
        /// create-or-update and expresses both halves; the window's asymmetry is
        /// a UI accident, not a game rule.
        public static void SetRestockRule(ThingDef def, int threshold, int target, bool enabled,
            ThingDef stuff, Color? color)
            => mSetRestockRule.Invoke(null,
                new object[] { def, threshold, target, enabled, stuff, EncodeColor(color) });

        public static bool CanCustomizeColor(ThingDef d) => canCustomizeColor(d);

        /// The colour picker's own palette — the styling station's colours, or
        /// the Structure colours when Ideology is off. Memoised by Nepo into a
        /// private static and handed back live, the same `Def.LabelCap` case the
        /// three catalog list builders are, so building it on a read is allowed.
        /// Never mutated here.
        public static List<Color> StylingStationColors()
            => stylingStationColors() ?? new List<Color>();

        /// How often the sweep looks, in ticks. `null` means WE COULD NOT LOOK
        /// (rule 2) — never a guess at 2500.
        public static int? RestockSweepIntervalTicks()
        {
            Resolve();
            if (tRestockIntervalTicks == null) return null;
            return (int)tRestockIntervalTicks.GetValue(null);
        }

        /// Why the sweep interval is unknown. Null when it is known.
        public static string SweepUnavailable { get { Resolve(); return sweepWhyNot; } }
    }
}
