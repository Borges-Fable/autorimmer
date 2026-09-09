using System;
using System.Collections;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // ========================================================================
    // NEPO — order goods from Dad's catalog.  `nepo-catalog` / `nepo-order` /
    // `nepo-inbound`.
    //
    // Nepo (Dorian.Nepo) starts the colony with a comms console and a rich
    // father off-map: you call Dad, browse a catalog of everything with a market
    // value, and it lands by drop pod. It already works — what it did not have
    // is a route in for something that is not a mouse. Its catalog is a WINDOW
    // and the agent plays through verbs, so these three are that window's
    // semantic equivalent: what is orderable, place an order, what is inbound.
    //
    // SOFT DEPENDENCY. There is no project reference and no assembly reference
    // to Nepo, and there must never be one. Everything goes through
    // `NepoBridge.cs`, which binds by name and signature at first use. On a
    // bench without Nepo all three verbs answer `ok:false` with
    // `gate:"nepo-absent"` and a sentence naming the mod and its package id;
    // nothing throws, nothing logs, no red error, and no other verb in the tree
    // changes behaviour.
    //
    // ============ THE GATE LIVES IN THE WIDGET, AND HERE IS THE WIDGET =======
    // CLAUDE.md and DESIGN §Action model: a player verb re-implements its
    // precondition and cites it by file and member. The player's route to an
    // order has two halves, and BOTH are reproduced by `nepo-order`, because a
    // verb that can order goods with no comms console when the UI cannot is a
    // god-hand the agent was never meant to have.
    //
    // HALF ONE — GETTING THE CATALOG OPEN. Nepo's window is constructed in
    // exactly one place, `Nepo/DadCommTarget.TryOpenComms`, and the chain to it
    // is:
    //   * `Nepo/Patch_CommsConsole_GetCommTargets.Postfix` appends
    //     `new DadCommTarget()` only when
    //     `NepoGameComponent.Active?.DadAvailable ?? false` — outside a Nepo
    //     colony Dad is simply not in the contact list.
    //   * vanilla's own console gate applies unchanged:
    //     `RimWorld/Building_CommsConsole.CanUseCommsNow` is
    //     `!(Spawned && Map.gameConditionManager.ElectricityDisabled(Map))`
    //     followed by `powerComp.PowerOn` — i.e. it folds solar flare AND power
    //     into one property — and the colonist must be able to reach the
    //     console (`CanReach(console, PathEndMode.InteractionCell, Danger.Some)`,
    //     the clause `Building_CommsConsole.GetFailureReason` opens with and
    //     `Patch_CommsConsole_GetFloatMenuOptions.ShouldOfferDadDespiteMuteness`
    //     repeats).
    //   * `Nepo/DadCommTarget.CanRead(Pawn)` is
    //     `negotiator?.health?.capacities?.CapableOf(PawnCapacityDefOf.Sight)
    //      ?? false` — **SIGHT, NOT TALKING.** The catalog is an app, not a
    //     radio call, and `Patch_CommsConsole_GetFloatMenuOptions` exists
    //     precisely to hand a MUTE colonist the Dad option that vanilla's
    //     Talking clause would have denied. This verb therefore does NOT
    //     reproduce `GetFailureReason`'s Talking clause — reproducing it would
    //     fabricate a restriction the player does not have, which DESIGN's
    //     `261f2e9` entry names as the same class of error as bypassing a gate,
    //     only facing the other way. `CommsVerbs.ConsoleFailure` is not reused
    //     for that one reason; everything else in it is reproduced below.
    //   * `Nepo/NepoSync.GiveCatalogCommsJob` re-checks `DadAvailable` and
    //     `CanRead` before the job is even given.
    //
    // HALF TWO — CONFIRMING THE ORDER. `Nepo/Window_DadCatalog.DrawFooter`
    // computes exactly one predicate for the confirm button:
    //     bool canOrder = comp != null && cartCount > 0 && comp.CanAfford(cartTotal);
    // and `Nepo/NepoSync.PlaceOrder` re-checks `comp.DadAvailable` and
    // `comp.CanAfford(cost)` on arrival. There is no console check at confirm
    // time — the window is `forcePause` and already open — so the console gate
    // above is a gate on OPENING, and this verb applies it at order time because
    // it is doing both acts in one call.
    //
    // A THIRD GATE THAT IS NOT A WIDGET AND IS THE EASIEST TO MISS:
    // `Nepo/NepoGameComponent.GameComponentTick` opens with
    // `if (!IsNepoGame) return;`, and `IsNepoGame => scenarioActive || gateArmed`.
    // A shipment scheduled in a colony that was not started from the Nepo
    // scenario sits in `pending` forever and never delivers. `nepo-order`
    // refuses that colony by name rather than taking payment for a pod that
    // will not come.
    //
    // ============ WHAT `requireCommsForRestock` DOES *NOT* GATE ==============
    // Worth stating because the name reads as though it gates every order. It
    // does not. Its only two call sites are
    // `Nepo/NepoGameComponent.RunRestockSweep` and
    // `Nepo/NepoGameComponent.RunSurgeryOrderSweep`, both of which branch on it
    // to decide whether an AUTOMATED reorder is queued as a `PendingDispatch`
    // for a colonist to call in, or charged and shipped on the spot. A manual
    // order — the catalog window's, and this verb's — never consults it. So
    // `nepo-order` does not enforce it, `nepo-catalog` and `nepo-inbound`
    // REPORT it, and the reason is recorded here so the omission is a decision
    // rather than an oversight.
    //
    // ============ MODE IS REPORTED, NEVER ASSUMED, AND NEVER WRITTEN =========
    // `unlimitedMoney` and `instantDelivery` change what the agent should plan
    // around — one makes the balance meaningless, the other makes a two-day
    // logistics problem disappear — so every reply here carries a `mode` block
    // read fresh from `NepoGameComponent.Active.Config`. Nothing in this file
    // writes a config field. Dorian sets his own mod's settings, and Nepo's own
    // only sanctioned writer is the synced `NepoSync.SetConfig`, which carries
    // cache invalidations this file has no business reproducing.
    //
    // ============ OBSERVERS NEVER MUTATE ====================================
    // `nepo-catalog` and `nepo-inbound` write nothing and journal nothing
    // (`NoStamp()`). The one trap on the read side is
    // `NepoGameComponent.SlaveRoster`, which GENERATES a roster of pawns when
    // the backing field is null — a write-on-read of the class `PawnSafe`
    // exists to refuse. `NepoBridge` binds `SlaveRosterNoGenerate` instead and
    // never binds `SlaveRoster` at all, so it cannot be reached by accident.
    // `nepo-order` is the only thing here that writes.
    // ========================================================================
    internal static partial class PawnActs
    {
        // Per SECTION, not per reply: `kind:"all"` with the default shows up to
        // twenty of each. Sized against `things`' RollupCap rather than invented
        // — the catalog is a browse surface and the agent narrows with `filter`.
        public const int NepoCatalogCap = 20;
        public const int NepoInboundCap = 20;

        // Guards a single order line. Nepo itself has no cap — `PlaceOrder`
        // charges whatever `OrderCost` sums — but a `count` that overflows the
        // int multiply in `CatalogBuilder.PriceFor(def) * count` would charge a
        // NEGATIVE cost, which `CanAfford` then passes. Refused rather than
        // clamped, per the stray-key rule's reasoning: a silently narrowed order
        // is a worse answer than no order.
        public const int NepoMaxCount = 100000;

        private static readonly string[] NepoCatalogArgs = { "cap", "filter", "kind" };
        private static readonly string[] NepoInboundArgs = { "cap" };
        private static readonly string[] NepoOrderArgs =
            { "animals", "console", "dry_run", "items", "mechs", "negotiator", "slaves" };

        private static readonly string[] NepoRestockArgs =
            { "cap", "color", "def", "dry_run", "enabled", "stuff", "target", "threshold" };

        private static readonly string[] NepoItemLineKeys = { "count", "def", "stuff" };
        private static readonly string[] NepoKindLineKeys = { "count", "kind" };

        // Standing rules are made one at a time by hand, so this is a browse
        // cap and not a paging strategy; `total`/`more` still ride the reply.
        public const int NepoRestockCap = 50;

        // The customize panel's option lists on a single-def read. Bounded
        // already (a def's allowed stuffs, and the styling station's palette),
        // capped so a modded bench with four hundred fabrics cannot make a read
        // reply enormous.
        public const int NepoRestockOptionCap = 60;

        // `Nepo/Window_DadCatalog.DrawRestockPanel` draws BOTH numbers with
        // `Widgets.TextFieldNumeric(…, 0f, 999999f)`. A player cannot type a
        // negative or anything past this, so the verb refuses those rather than
        // handing them to the backend, whose only defence is a silent clamp.
        public const int NepoRestockFieldMax = 999999;

        public const string NepoKindWords = "all|items|mechs|animals|slaves";

        // ====================================================================
        // nepo-catalog {kind?, filter?, cap?}
        //
        // What Dad will sell, with price and category. A pure read.
        //
        // THE LIST IS LARGE — `CatalogBuilder.OrderableDefs()` is every
        // `ThingDef` on the bench with `tradeability != None`, `PlayerAcquirable`,
        // a `BaseMarketValue > 0` and not a corpse, which on a modded bench is
        // four figures — so it obeys the standing rule: capped, with `total`,
        // `matched`, `more` and a `shown_note` that says how many of the total
        // came back. `filter` is the way to find a def without paging the world;
        // it is a case-insensitive substring over BOTH `defName` and label,
        // matching `trade`'s `filter` exactly rather than inventing a second
        // dialect.
        // ====================================================================
        [Verb("nepo-catalog")]
        public static object NepoCatalog(VerbContext ctx)
        {
            const string V = "nepo-catalog";
            var a = ctx.Args;

            // A READ that refuses a stray key, which DESIGN's c519477 entry
            // scopes to verbs that MUTATE. The deviation is deliberate and
            // narrow: `filter` is the whole reason this verb is usable at all
            // against a four-figure catalog, and a dropped `filter` does not
            // return less, it returns the WRONG twenty — the first twenty of
            // everything, which reads exactly like a correct answer. That is the
            // widening the rule exists to stop, and the fact that nothing is
            // written does not make acting on it cheaper. Nothing is computed
            // before this line, so the refusal costs a dictionary walk.
            // `Suggest`'s Levenshtein cut-off never finds these — `search` is
            // five edits from `filter` — so they are named at the call site,
            // BEFORE RefuseStray, which would otherwise refuse them with a
            // worse sentence.
            a.NearMiss("filter", "search", "query", "match", "name");
            a.NearMiss("kind", "type", "section", "category");
            a.RefuseStray(V, NepoCatalogArgs,
                "Nothing was read. A key this verb cannot see is not a smaller catalog — a dropped "
                + "`filter` is the first " + NepoCatalogCap + " of EVERYTHING, which looks like a "
                + "match.");

            var gate = NepoGate(V, false, out object comp);
            if (gate != null) return gate;

            string kind = a.Str("kind", "all");
            if (kind != "all" && kind != "items" && kind != "mechs" && kind != "animals"
                && kind != "slaves")
                throw new VerbArgsException("unknown kind '" + kind + "' (" + NepoKindWords + ")");
            string filter = a.Str("filter", null);
            int cap = a.Int("cap", NepoCatalogCap);
            if (cap < 1 || cap > 200) throw new VerbArgsException("cap must be 1..200");

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["mode"] = NepoMode(comp),
                ["query"] = new Dictionary<string, object>
                {
                    ["kind"] = kind,
                    ["filter"] = filter,
                    ["cap"] = cap,
                },
            };

            var notes = new List<string>();
            if (kind == "all" || kind == "items")
                d["items"] = NepoItemSection(comp, filter, cap, notes);
            if (kind == "all" || kind == "mechs")
                d["mechs"] = NepoMechSection(comp, filter, cap, notes);
            if (kind == "all" || kind == "animals")
                d["animals"] = NepoAnimalSection(comp, filter, cap, notes);
            if (kind == "all" || kind == "slaves")
                d["slaves"] = NepoSlaveSection(comp, filter, cap, notes);

            // The first line of the reply: how much of the total this is.
            d["shown_note"] = notes.Count == 0
                ? "nothing matched"
                : string.Join("; ", notes.ToArray())
                  + ". Narrow with `filter` or raise `cap` (max 200).";
            d["price_basis"] = "price is `CatalogBuilder.PriceFor` — base market value times "
                + "`NepoTuning.GlitterMarkup`, which ships at 1.0, so today it is market value "
                + "exactly. A stuffable item's price moves with `stuff`; the price shown is the "
                + "stuffless one unless a row says otherwise.";
            // `nepo-catalog` deliberately does NOT refuse when Dad is
            // unreachable — reading a price list writes nothing and costs
            // nothing, and the agent planning a shopping list before it can
            // shop is a legitimate act. But `mode.dad_available` is one field
            // among sixteen, so the fact is lifted to its own advisory: a
            // catalog the agent cannot buy from is news.
            if (NepoBool(() => Nepo.DadAvailable(comp)) as bool? == false)
                d["advisory"] = "Dad is not answering right now, so nothing here can be ordered yet "
                    + "(Nepo/Patch_CommsConsole_GetCommTargets.Postfix leaves him out of the contact "
                    + "list unless `DadAvailable`). The prices are still real.";

            d["action"] = NoStamp();
            return d;
        }

        // ====================================================================
        // nepo-order {items?, mechs?, animals?, slaves?, dry_run?, negotiator?,
        //             console?}
        //
        //   items:   [{def:"MealSimple", count:20, stuff:null}, …]
        //   mechs:   [{kind:"Mech_Lifter", count:1}, …]
        //   animals: [{kind:"Muffalo", count:2}, …]
        //   slaves:  [0, 3]   — roster INDEXES, from `nepo-catalog`'s slave rows
        //   dry_run: bool, default false
        //
        // The only verb in this file that writes. An order is irreversible and
        // spends a balance, so `dry_run` is the model `build` set: everything up
        // to and including the affordability gate runs, the reply says what
        // WOULD happen, and nothing is placed.
        // ====================================================================
        [Verb("nepo-order")]
        public static object NepoOrder(VerbContext ctx)
        {
            const string V = "nepo-order";
            var a = ctx.Args;

            // PRE-MUTATION (git-bug c519477), before the first step and before
            // anything is read. Three of these keys change what the call DOES
            // when they go unread rather than merely narrowing it: `dry_run`
            // defaults to FALSE, so a dropped preflight flag is a real order
            // against a real balance; `negotiator` and `console` fall through to
            // an auto-pick, which decides which colonist the game thinks made
            // the call. And a mis-spelled `item`/`mech` list is not a smaller
            // order — it is a DIFFERENT one, charged in full.
            a.NearMiss("items", "goods", "things", "order", "cart");
            a.NearMiss("dry_run", "preview", "simulate", "plan");
            a.RefuseStray(V, NepoOrderArgs,
                "Nothing was ordered and nothing was charged. `dry_run` defaults to false, so a "
                + "dropped preflight flag is a real order; a mis-spelled goods list is a different "
                + "order, not a smaller one.");

            var gate = NepoGate(V, true, out object comp);
            if (gate != null) return gate;

            bool dryRun = a.Bool("dry_run", false);

            // ---- argument shape first, so a malformed line reports its own
            // fault rather than being masked by a colony-level refusal --------
            var items = NepoItemLines(a);
            var mechs = NepoKindLines(a, "mechs");
            var animals = NepoKindLines(a, "animals");
            var slaveIdx = NepoSlaveIndexes(a);

            if (items.Count == 0 && mechs.Count == 0 && animals.Count == 0 && slaveIdx.Count == 0)
                return NepoRefuse(V, "empty-order",
                    "Nepo/Window_DadCatalog.DrawFooter gates the confirm button on `cartCount > 0`, "
                    + "and Nepo/NepoSync.PlaceOrder returns without charging when every section "
                    + "decodes to nothing",
                    "no goods were named. Pass at least one of `items`, `mechs`, `animals` or "
                    + "`slaves`; `nepo-catalog` lists what is orderable.",
                    NepoMode(comp));

            // ---- resolve every line against the catalog ---------------------
            var lines = new List<object>();
            long cost = 0;

            var itemEntries = Nepo.NewItemList();
            foreach (var l in items)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(l.Name);
                if (def == null)
                    throw new VerbArgsException("no ThingDef named '" + l.Name
                        + "' (see `nepo-catalog {filter:\"" + l.Name + "\"}`)");

                // The catalog's own membership test, and the reason a def can be
                // real and still unbuyable: `CatalogBuilder.IsOrderable` refuses
                // `tradeability == None`, corpses, `!PlayerAcquirable`,
                // `BaseMarketValue <= 0`, and every Building that is not
                // minifiable — and, for a minifiable one that is not art, it
                // consults `Config.allowMinifiableBuildings`, which is Dorian's
                // switch and reads live.
                if (!Nepo.IsOrderable(def))
                    return NepoRefuse(V, "not-orderable",
                        "Nepo/CatalogBuilder.IsOrderable — the predicate OrderableDefs() filters on, "
                        + "so a def it rejects has no row in the catalog window either",
                        "'" + def.defName + "' is not in Dad's catalog. Nepo lists a def only when it "
                        + "is tradeable, player-acquirable, not a corpse and has a market value above "
                        + "zero; a minifiable BUILDING additionally needs `allowMinifiableBuildings` "
                        + "unless it is art (that setting is currently "
                        + (Nepo.AllowMinifiableBuildings(comp) ? "ON" : "OFF") + ").",
                        NepoMode(comp));

                ThingDef stuff = null;
                if (l.Stuff != null)
                {
                    stuff = DefDatabase<ThingDef>.GetNamedSilentFail(l.Stuff);
                    if (stuff == null)
                        throw new VerbArgsException("no ThingDef named '" + l.Stuff
                            + "' for the `stuff` of '" + def.defName + "'");

                    // The window draws a material selector only when
                    // `CatalogEntry.canMaterial` is set from
                    // `CatalogBuilder.CanCustomizeMaterial(def)`, and
                    // `CatalogEntry.UnitPrice` reads `selectedStuff` only under
                    // the same `canMaterial &&` clause — so a stuff on a def
                    // that cannot take one is a value the UI would have thrown
                    // away, and accepting it here would charge a price no player
                    // could be charged.
                    if (!Nepo.CanCustomizeMaterial(def))
                        return NepoRefuse(V, "stuff-not-customizable",
                            "Nepo/CatalogBuilder.CanCustomizeMaterial gates the window's material "
                            + "selector, and Nepo/CatalogEntry.UnitPrice reads `selectedStuff` only "
                            + "under the same `canMaterial &&` clause",
                            "'" + def.defName + "' has no material choice in the catalog, so `stuff` "
                            + "cannot be set for it. Drop the key.",
                            NepoMode(comp));

                    // The selector's own option list, so a stuff outside it is
                    // refused for the same reason: it is not an option a click
                    // could produce.
                    if (!Nepo.AllowedStuffs(def).Contains(stuff))
                        return NepoRefuse(V, "stuff-not-allowed",
                            "Nepo/CatalogBuilder.AllowedStuffs(def) is the material selector's option "
                            + "list in Nepo/Window_DadCatalog",
                            "'" + stuff.defName + "' is not one of the materials Dad will make '"
                            + def.defName + "' from.",
                            NepoMode(comp));
                }

                int unit = Nepo.PriceFor(def, stuff);
                cost += (long)unit * l.Count;
                itemEntries.Add(Nepo.ItemEntry(def, l.Count, stuff));
                lines.Add(new Dictionary<string, object>
                {
                    ["kind"] = "item",
                    ["def"] = def.defName,
                    ["label"] = WorldSafe.Safe(() => def.LabelCap.ToString()),
                    ["count"] = l.Count,
                    ["stuff"] = stuff?.defName,
                    ["unit_price"] = unit,
                    ["line_total"] = (long)unit * l.Count,
                });
            }

            var mechEntries = Nepo.NewMechList();
            if (mechs.Count > 0)
            {
                // The catalog's mech folder is `CatalogBuilder.OrderableMechs()`,
                // which returns an EMPTY list — no folder at all in the window —
                // unless `ModsConfig.BiotechActive && NepoMod.Settings
                // .allowMechPurchases`. Membership of that list is therefore the
                // whole gate, and it carries both clauses for free.
                var allowed = Nepo.OrderableMechs();
                foreach (var l in mechs)
                {
                    var kind = NepoKind(allowed, l.Name, "mech", "Nepo/CatalogBuilder.OrderableMechs()",
                        "Biotech must be active and `allowMechPurchases` on in Nepo's mod settings",
                        V, comp, out object refusal);
                    if (refusal != null) return refusal;
                    int unit = Nepo.PriceForMech(kind);
                    cost += (long)unit * l.Count;
                    mechEntries.Add(Nepo.MechEntry(kind, l.Count));
                    lines.Add(NepoKindLine("mech", kind, l.Count, unit));
                }
            }

            var animalEntries = Nepo.NewAnimalList();
            if (animals.Count > 0)
            {
                var allowed = Nepo.OrderableAnimals();
                foreach (var l in animals)
                {
                    var kind = NepoKind(allowed, l.Name, "animal",
                        "Nepo/CatalogBuilder.OrderableAnimals()",
                        "`allowAnimalPurchases` must be on in Nepo's mod settings",
                        V, comp, out object refusal);
                    if (refusal != null) return refusal;
                    int unit = Nepo.PriceForAnimal(kind);
                    cost += (long)unit * l.Count;
                    animalEntries.Add(Nepo.AnimalEntry(kind, l.Count));
                    lines.Add(NepoKindLine("animal", kind, l.Count, unit));
                }
            }

            // Slaves are addressed by ROSTER INDEX rather than by kind, because
            // each one is a generated individual and not a fungible def —
            // `SlaveCatalogEntry.inCart` is a bool, not a quantity, for the same
            // reason. `SlaveRosterNoGenerate` is the safe read; `SlaveRoster`
            // would build a roster as a side effect of looking.
            var roster = Nepo.SlaveRoster(comp);
            var slavePawns = new List<Pawn>();
            foreach (int idx in slaveIdx)
            {
                if (idx < 0 || idx >= roster.Count)
                {
                    // An empty roster is the ordinary shape of "slavery is off":
                    // `NepoGameComponent.RerothSlaveRoster` returns an empty list
                    // unless Ideology is active, `Config.allowSlavePurchases` is
                    // on, and the player's primary ideoligion approves — the same
                    // gate vanilla slave stock uses.
                    string why = roster.Count == 0
                        ? "the slave roster is empty. Nepo/NepoGameComponent.RerollSlaveRoster yields "
                          + "nothing unless Ideology is active, `allowSlavePurchases` is on (it is "
                          + (Nepo.AllowSlavePurchases(comp) ? "ON" : "OFF")
                          + "), and the player's primary ideoligion approves of slavery"
                          + (Nepo.SlaveOffersBlockedReason(comp) == null
                              ? "" : " — " + Nepo.SlaveOffersBlockedReason(comp))
                        : "slave index " + idx + " is outside the roster (0.." + (roster.Count - 1)
                          + "). The roster rerolls daily, so an index read earlier may have moved; "
                          + "re-read `nepo-catalog {kind:\"slaves\"}`.";
                    return NepoRefuse(V, "slave-not-offered",
                        "Nepo/NepoGameComponent.SlaveRosterNoGenerate is the list "
                        + "Nepo/Window_DadCatalog builds its slave folder from, and "
                        + "Nepo/NepoSync.EncodeOrder addresses a bought slave as `roster.IndexOf(slave)`",
                        why, NepoMode(comp));
                }
                Pawn p = roster[idx];
                if (p == null || slavePawns.Contains(p))
                    throw new VerbArgsException("slave index " + idx
                        + " is null or named twice; a slave is an individual, not a quantity");
                slavePawns.Add(p);
                int unit = Nepo.PriceForSlave(p);
                cost += unit;
                lines.Add(new Dictionary<string, object>
                {
                    ["kind"] = "slave",
                    ["index"] = idx,
                    ["name"] = WorldSafe.Safe(() => p.LabelShortCap.ToString()),
                    ["pawn_id"] = p.thingIDNumber,
                    ["count"] = 1,
                    ["unit_price"] = unit,
                    ["line_total"] = unit,
                });
            }

            // `OrderCost` sums into an int; this verb sums into a long precisely
            // so an order that would overflow it is REFUSED instead of wrapping
            // negative and sailing through `CanAfford`.
            if (cost > int.MaxValue)
                return NepoRefuse(V, "cost-overflow",
                    "Nepo/NepoSync.OrderCost accumulates into an `int`, and "
                    + "Nepo/NepoGameComponent.CanAfford compares that int",
                    "this order totals " + cost + ", which does not fit in the int Nepo costs an "
                    + "order in. Split it.",
                    NepoMode(comp));
            int total = (int)cost;

            // ---- the console gate (half one of the widget) ------------------
            var map = PawnSafe.CurrentMap();
            if (map == null)
                return NepoRefuse(V, "no-map",
                    "Nepo/DadCommTarget.TryOpenComms is reached from a job on a colonist, which needs "
                    + "a map",
                    "there is no current map, so no colonist can call Dad", NepoMode(comp));

            var console = ConsoleArg(map, a, out string consoleError);
            if (console == null)
                return NepoRefuse(V, "no-comms-console",
                    "Nepo/Patch_CommsConsole_GetCommTargets.Postfix hangs Dad off "
                    + "RimWorld/Building_CommsConsole.GetCommTargets — with no console there is no "
                    + "contact list to put him in",
                    consoleError, NepoMode(comp));

            var negotiator = NepoNegotiatorArg(map, a, console, out string negotiatorError);
            if (negotiator == null)
                return NepoRefuse(V, "no-negotiator",
                    "Nepo/DadCommTarget.CanRead(Pawn) is "
                    + "`negotiator?.health?.capacities?.CapableOf(PawnCapacityDefOf.Sight) ?? false`",
                    negotiatorError, NepoMode(comp));

            string consoleWhy = NepoConsoleFailure(console, negotiator, map);
            if (consoleWhy != null)
                return NepoRefuse(V, "console-unusable",
                    "RimWorld/Building_CommsConsole.CanUseCommsNow (power and solar flare) plus the "
                    + "reach clause Nepo/Patch_CommsConsole_GetFloatMenuOptions"
                    + ".ShouldOfferDadDespiteMuteness repeats, and Nepo/DadCommTarget.CanRead's Sight "
                    + "capacity",
                    consoleWhy,
                    NepoMode(comp),
                    new Dictionary<string, object>
                    {
                        ["console"] = NepoConsoleRef(console),
                        ["negotiator"] = PawnRef(negotiator),
                    });

            // ---- the affordability gate (half two) --------------------------
            bool afford;
            try { afford = Nepo.CanAfford(comp, total); }
            catch (Exception e)
            {
                // A gate that threw has not passed (TradeVerbs' rule).
                return NepoRefuse(V, "gate-unreadable",
                    "Nepo/NepoGameComponent.CanAfford, which Nepo/Window_DadCatalog.DrawFooter gates "
                    + "the confirm button on",
                    "reading the balance threw: " + e.GetType().Name + ": "
                    + Journal.Truncate(e.Message, 160),
                    NepoMode(comp));
            }
            int budgetBefore = Nepo.Budget(comp);
            long spentBefore = Nepo.SpentWhileUnlimited(comp);
            if (!afford)
                return NepoRefuse(V, "cannot-afford",
                    "Nepo/Window_DadCatalog.DrawFooter — `canOrder = comp != null && cartCount > 0 && "
                    + "comp.CanAfford(cartTotal)` — and Nepo/NepoSync.PlaceOrder re-checks the same "
                    + "thing on arrival and refuses the WHOLE order rather than part-filling it",
                    "this order costs " + total + " and the balance is " + budgetBefore,
                    NepoMode(comp),
                    new Dictionary<string, object> { ["cost"] = total, ["balance"] = budgetBefore });

            // AN ORDER WHOSE ARRIVAL CANNOT BE STATED IS REFUSED. Rule 2: the
            // fallback would be to assume Nepo's shipped two days, and a wrong
            // arrival time reads exactly like a right one — the agent plans a
            // food gap around it. `nepo-catalog` reports the same unknown and
            // keeps working, because reading is not planning.
            int? delayOpt = Nepo.DeliveryDelayTicks(comp);
            if (!delayOpt.HasValue)
                return NepoRefuse(V, "delay-unreadable",
                    "Nepo/NepoTuning.DeliveryDelayTicks, which Nepo/Window_DadCatalog.DeliveryDelay() "
                    + "returns when `instantDelivery` is off",
                    Nepo.DelayUnavailable + " — so this verb cannot say when the pod would land, and "
                    + "will not guess. Nothing was ordered.",
                    NepoMode(comp));
            int delay = delayOpt.Value;
            int nowTick = 0;
            try { nowTick = Find.TickManager.TicksGame; } catch { }

            var order = new Dictionary<string, object>
            {
                ["lines"] = lines,
                ["cost"] = total,
                ["console"] = NepoConsoleRef(console),
                ["negotiator"] = PawnRef(negotiator),
            };

            // ---- dry run ----------------------------------------------------
            if (dryRun)
            {
                var pre = new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = true,
                    ["mode"] = NepoMode(comp),
                    ["dry_run"] = true,
                    ["placed"] = false,
                    ["order"] = order,
                    ["cost"] = total,
                    ["would"] = new Dictionary<string, object>
                    {
                        ["cost"] = total,
                        ["balance_before"] = budgetBefore,
                        ["balance_after"] = Nepo.UnlimitedMoney(comp)
                            ? budgetBefore : Math.Max(0, budgetBefore - total),
                        ["arrival_tick"] = nowTick + delay,
                        ["delay_ticks"] = delay,
                        ["arrives_in"] = NepoSpan(delay),
                    },
                    ["note"] = "nothing was ordered and nothing was charged. Every gate above ran — "
                        + "catalog membership, the comms console, the negotiator's Sight and the "
                        + "balance — so this is what the real call would do, not a guess. Re-send "
                        + "without `dry_run` to place it.",
                    ["action"] = NoStamp(),
                };
                return pre;
            }

            // ---- the act ----------------------------------------------------
            // The widget's own route, and NOT `ScheduleShipment`: see
            // NepoBridge.cs's header. `PlaceOrder` charges, parks bought slaves
            // in WorldPawns, memoises the material preference, schedules, and is
            // the method Nepo registers with `MP.RegisterSyncMethod`.
            int pendingBefore = Nepo.PendingCount(comp);
            var beforeIds = NepoShipmentIdentity(comp);

            string payload;
            try
            {
                payload = Nepo.EncodeOrder(delay, itemEntries, mechEntries, animalEntries,
                    slavePawns, roster);
            }
            catch (Exception e)
            {
                return NepoRefuse(V, "encode-failed",
                    "Nepo/NepoSync.EncodeOrder — the wire format Nepo/NepoSync.TryDecodeOrder parses",
                    "encoding the order threw, so nothing was sent: " + e.GetType().Name + ": "
                    + Journal.Truncate(e.Message, 160),
                    NepoMode(comp));
            }

            try { Nepo.PlaceOrder(negotiator, payload); }
            catch (Exception e)
            {
                // The invoke threw, which means we do not know whether the debit
                // landed. Say so rather than guess; the read-back below would
                // report a partial state as a success.
                string diag = "Nepo/NepoSync.PlaceOrder threw: " + e.GetType().Name + ": "
                    + Journal.Truncate(e.Message, 200);
                Journal.EmitWarning("[AutoRimmer] nepo-order: " + diag);
                return NepoRefuse(V, "place-threw",
                    "Nepo/NepoSync.PlaceOrder", diag + ". The balance is now "
                    + Nepo.Budget(comp) + " and " + Nepo.PendingCount(comp)
                    + " shipment(s) are pending — compare against " + budgetBefore + " and "
                    + pendingBefore + " before assuming nothing happened.",
                    NepoMode(comp));
            }

            // ---- READ THE WRITE BACK (rule 3) -------------------------------
            // `PlaceOrder` is `void` and returns SILENTLY on a null component,
            // on `!DadAvailable`, on an empty payload and on `!CanAfford` — and
            // its decoder DROPS any line whose def no longer resolves. "It did
            // not throw" is therefore no evidence at all.
            int pendingAfter = Nepo.PendingCount(comp);
            int budgetAfter = Nepo.Budget(comp);
            long spentAfter = Nepo.SpentWhileUnlimited(comp);
            bool unlimited = Nepo.UnlimitedMoney(comp);
            object placedShipment = NepoNewShipment(comp, beforeIds);

            if (pendingAfter <= pendingBefore || placedShipment == null)
            {
                string diag = "Nepo/NepoSync.PlaceOrder returned without scheduling anything. "
                    + "`pending` went " + pendingBefore + " -> " + pendingAfter + " and the balance "
                    + "went " + budgetBefore + " -> " + budgetAfter + ". PlaceOrder returns silently "
                    + "on a null component, on !DadAvailable, on an empty payload and on !CanAfford, "
                    + "and its decoder drops a line whose def no longer resolves.";
                Journal.EmitWarning("[AutoRimmer] nepo-order: the order did not take. " + diag);
                return NepoRefuse(V, "order-did-not-take",
                    "Nepo/NepoSync.PlaceOrder, read back against "
                    + "Nepo/NepoGameComponent.PendingCount and .Budget",
                    diag, NepoMode(comp));
            }

            // What was actually charged, as opposed to what we computed. Under
            // `unlimitedMoney` the balance does not move at all and the spend
            // accumulates into `SpentWhileUnlimited` instead — so the two modes
            // are read from different members and neither is inferred.
            long charged = unlimited ? spentAfter - spentBefore : budgetBefore - budgetAfter;
            bool chargedAsExpected = charged == total;

            int arrival = Nepo.ShipmentTick(placedShipment);
            var shipment = NepoShipmentRow(placedShipment, nowTick);

            long seq = Act(V, "order", lines.Count + " line(s)", new Dictionary<string, object>
            {
                ["cost"] = total,
                ["charged"] = charged,
                ["balance_before"] = budgetBefore,
                ["balance_after"] = budgetAfter,
                ["unlimited_money"] = unlimited,
                ["instant_delivery"] = Nepo.InstantDelivery(comp),
                ["arrival_tick"] = arrival,
                ["delay_ticks"] = delay,
                ["lines"] = lines,
                ["negotiator"] = WorldSafe.Safe(() => negotiator.LabelShortCap.ToString()),
            });

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["mode"] = NepoMode(comp),
                ["dry_run"] = false,
                ["placed"] = true,
                ["order"] = order,
                ["cost"] = total,
                ["balance"] = new Dictionary<string, object>
                {
                    ["before"] = budgetBefore,
                    ["after"] = budgetAfter,
                    ["charged"] = charged,
                    ["charged_as_expected"] = chargedAsExpected,
                    ["unlimited"] = unlimited,
                    ["spent_while_unlimited"] = spentAfter,
                    ["basis"] = unlimited
                        ? "with `unlimitedMoney` on, Nepo/NepoGameComponent.Spend leaves the balance "
                          + "untouched and adds the cost to `spentWhileUnlimited` instead — so "
                          + "`charged` here is the movement of THAT counter, not of the balance"
                        : "Nepo/NepoGameComponent.Spend debits `budget`, clamped at zero; `charged` "
                          + "is the measured movement, not the price we computed",
                },
                ["arrival"] = new Dictionary<string, object>
                {
                    ["tick"] = arrival,
                    ["delay_ticks"] = delay,
                    ["arrives_in"] = NepoSpan(Math.Max(0, arrival - nowTick)),
                    ["instant"] = delay == 0,
                    ["basis"] = "Nepo/Window_DadCatalog.DeliveryDelay() is `instantDelivery ? 0 : "
                        + "NepoTuning.DeliveryDelayTicks`, and the pod is dropped by "
                        + "Nepo/NepoGameComponent.GameComponentTick, which only runs its delivery "
                        + "sweep on a tick where `now % 250 == 0` — so even an instant delivery "
                        + "lands within 250 ticks, not on this one. `nepo-inbound` is the read.",
                },
                ["shipment"] = shipment,
                ["pending_before"] = pendingBefore,
                ["pending_after"] = pendingAfter,
                ["action"] = Stamp(seq),
            };

            if (!chargedAsExpected)
                d["cost_mismatch"] = "the balance moved by " + charged + " against a computed cost of "
                    + total + ". Nepo/NepoSync.TryDecodeOrder silently drops a manifest line whose def "
                    + "no longer resolves, and Nepo/NepoGameComponent.Spend clamps the balance at "
                    + "zero — compare `shipment` against `order.lines` to see which.";

            return d;
        }

        // ====================================================================
        // nepo-inbound {cap?}
        //
        // What is already coming, so a drop pod is not a surprise the agent has
        // to remember it caused. Three lists, and they are three different
        // things:
        //   * shipments  — paid for, in flight, with an absolute arrival tick
        //                  (`NepoGameComponent.pending`)
        //   * dispatches — decided but NOT yet paid for, waiting for a colonist
        //                  to walk to a console and call it in
        //                  (`NepoGameComponent.pendingDispatches`, created only
        //                  when `requireCommsForRestock` is on)
        //   * the two counts Nepo publishes about them
        // A pure read.
        // ====================================================================
        [Verb("nepo-inbound")]
        public static object NepoInbound(VerbContext ctx)
        {
            const string V = "nepo-inbound";
            var a = ctx.Args;

            a.RefuseStray(V, NepoInboundArgs,
                "Nothing was read. This verb takes only `cap`.");

            var gate = NepoGate(V, false, out object comp);
            if (gate != null) return gate;

            int cap = a.Int("cap", NepoInboundCap);
            if (cap < 1 || cap > 200) throw new VerbArgsException("cap must be 1..200");

            int now = 0;
            try { now = Find.TickManager.TicksGame; } catch { }

            var ships = Nepo.PendingShipments(comp);
            var shipRows = new List<object>();
            for (int i = 0; i < ships.Count && shipRows.Count < cap; i++)
                shipRows.Add(NepoShipmentRow(ships[i], now));

            var disp = Nepo.PendingDispatches(comp);
            var dispRows = new List<object>();
            for (int i = 0; i < disp.Count && dispRows.Count < cap; i++)
            {
                var e = disp[i];
                ThingDef def = Nepo.DispatchDef(e);
                ThingDef stuff = Nepo.DispatchStuff(e);
                int count = Nepo.DispatchCount(e);
                dispRows.Add(new Dictionary<string, object>
                {
                    ["def"] = def?.defName,
                    ["label"] = def == null ? null : WorldSafe.Safe(() => def.LabelCap.ToString()),
                    ["count"] = count,
                    ["stuff"] = stuff?.defName,
                    ["price"] = def == null ? (object)null : Nepo.PriceFor(def, stuff) * count,
                });
            }

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["mode"] = NepoMode(comp),
                ["now_tick"] = now,
                ["shipments"] = new Dictionary<string, object>
                {
                    ["total"] = ships.Count,
                    ["shown"] = shipRows.Count,
                    ["more"] = Math.Max(0, ships.Count - shipRows.Count),
                    ["cap"] = cap,
                    ["rows"] = shipRows,
                    ["basis"] = "Nepo/NepoGameComponent.pending — paid for and in flight. "
                        + "Nepo/NepoGameComponent.GameComponentTick delivers one when "
                        + "`now >= deliveryTick`, checked only on a tick where `now % 250 == 0`, and "
                        + "only in a colony where `IsNepoGame` holds.",
                },
                ["dispatches"] = new Dictionary<string, object>
                {
                    ["total"] = disp.Count,
                    ["shown"] = dispRows.Count,
                    ["more"] = Math.Max(0, disp.Count - dispRows.Count),
                    ["cap"] = cap,
                    ["rows"] = dispRows,
                    ["awaiting_colonist"] = NepoBool(() => Nepo.HasPendingDispatch(comp)),
                    ["any_affordable"] = NepoBool(() => Nepo.HasAffordableDispatch(comp)),
                    ["basis"] = "Nepo/NepoGameComponent.pendingDispatches — an AUTOMATED restock or "
                        + "surgery reorder that is decided but NOT yet charged, waiting for a "
                        + "colonist to call it in at a console "
                        + "(Nepo/WorkGiver_DispatchRestockOrder). These exist only because "
                        + "`requireCommsForRestock` is on; with it off "
                        + "Nepo/NepoGameComponent.RunRestockSweep charges and ships on the spot. "
                        + "Nothing you order with `nepo-order` ever lands here.",
                },
                ["shown_note"] = shipRows.Count + " of " + ships.Count + " shipment(s) shown, "
                    + dispRows.Count + " of " + disp.Count + " dispatch(es) shown.",
                ["action"] = NoStamp(),
            };
            return d;
        }

        // ====================================================================
        // nepo-restock {def?, threshold?, target?, enabled?, stuff?, color?,
        //               dry_run?, cap?}
        //
        //   {}                                   every standing rule
        //   {def}                                one rule, and what a write of
        //                                        it may say (stuffs, palette)
        //   {def, threshold, target}             create or update it
        //   {def, enabled:false}                 stop the sweep acting on it,
        //                                        the numbers kept
        //   {…, dry_run:true}                    every gate runs, nothing writes
        //
        // A STANDING RULE IS NOT AN ORDER, and that is the whole reason this
        // verb exists next to `nepo-order`. `Nepo/NepoGameComponent
        // .RunRestockSweep` walks every enabled rule once an in-game HOUR
        // (`NepoTuning.RestockCheckIntervalTicks`, and only on a tick where
        // `now % 250 == 0 && DadAvailable`), and for any def whose available
        // count has fallen to or below `threshold` it orders `target -
        // available`. With `requireCommsForRestock` on — the default — that
        // becomes a `PendingDispatch` a colonist must call in at a console
        // (`Nepo/WorkGiver_DispatchRestockOrder`), which `nepo-inbound` already
        // reports. Before this verb the agent could SEE those dispatches and had
        // no way to cause one.
        //
        // ============ THERE IS NO DELETE, AND THAT IS NOT AN OMISSION ========
        // `Nepo/NepoGameComponent.SetRestock` has no removal branch:
        // `threshold = Max(0, threshold); target = Max(threshold + 1, target);`
        // then update-or-insert, so a zeroed rule is STORED at 0/1, not removed.
        // `RemoveRestock` exists, has zero callers in Nepo, has no `NepoSync`
        // wrapper and is not in `MpBridge.RegisterAll` — calling it would be an
        // unsynced write to scribed state, which is the argument that keeps
        // `ScheduleShipment` unbound. The catalog window cannot delete a rule
        // either; its toggle only disables. So "remove" is spelled
        // `enabled:false`, and asking for a delete gets that sentence.
        //
        // ============ THE GATE LIVES IN THE WIDGET — ALL FOUR OF THEM ========
        // The backend enforces exactly two things, and they are both clamps.
        // Every actual gate on a restock rule is drawn in
        // `Nepo/Window_DadCatalog`, so a verb that binds `SetRestockRule` and
        // stops there would hand the agent a rule on a def with no catalog row,
        // in a material the picker never offers, in a colour outside the
        // palette, with numbers no text field would accept — and Nepo would
        // price and ship all of it. The four are reproduced and cited:
        //   * CATALOG MEMBERSHIP — the panel exists only on rows built from
        //     `CatalogBuilder.OrderableDefs()`, predicate
        //     `CatalogBuilder.IsOrderable(ThingDef)`.
        //   * MATERIAL — offered only when `CatalogBuilder
        //     .CanCustomizeMaterial(def)`, chosen from `AllowedStuffs(def)`;
        //     `Window_DadCatalog.RestockStuff(e)` hard-nulls it otherwise.
        //   * COLOUR — offered only when `CatalogBuilder.CanCustomizeColor(def)`
        //     (`IsApparel && HasComp(CompColorable)`), chosen from
        //     `CatalogBuilder.StylingStationColors()`.
        //   * RANGE — `Widgets.TextFieldNumeric(…, 0f, 999999f)` on both
        //     fields. Refused here as bad args rather than left to the
        //     backend's silent clamp.
        //
        // ============ TWO GATES DELIBERATELY NOT REPRODUCED ==================
        // `dad-unavailable`: every other write in this file refuses on it, and
        // this one MUST NOT. The restock panel has no `DadAvailable` check — a
        // player can edit a rule with Dad gone — and what is gated is the SWEEP,
        // inside `GameComponentTick`. Refusing here would fabricate a
        // restriction the player does not have, which DESIGN's `261f2e9` entry
        // names as the same class of error as bypassing a gate, facing the other
        // way. So the rule is stored, `mode.dad_available` reports the fact, and
        // `sweep` says in words that nothing will fire until he is back.
        //
        // THE CONSOLE CHAIN, likewise not reproduced, and this one is a closer
        // call: reaching `DrawRestockPanel` at all means the catalog window is
        // open, which means a sighted colonist walked to a powered console. It
        // is not reproduced because editing a standing rule is bookkeeping —
        // nothing is charged, nothing ships, and the act it schedules is itself
        // re-gated on `DadAvailable` at sweep time and, under
        // `requireCommsForRestock`, on a colonist walking to a console to call
        // it in. The difference is WHEN the rule may be written, not WHAT it can
        // buy. `nepo-order`, which does charge and does ship, keeps the whole
        // chain. Recorded so the omission is a decision.
        //
        // ============ ONE CROSS-VERB EFFECT, PUBLISHED NOT DISCOVERED ========
        // `SetRestock`'s last line is `SetMaterialPref(def, stuff, color)`, on
        // every call, and `SetMaterialPref` REMOVES the entry when the choice is
        // default-stuff-and-no-colour. So a write here also rewrites Nepo's
        // remembered material for that def. Measured rather than assumed: the
        // only reader of `materialPrefs` is
        // `Window_DadCatalog.SeedCustomizationFromPrefs`, and even there a
        // standing rule WINS over the pref — so the effect lands on what a
        // human's next catalog visit comes up pre-filled with, and on nothing
        // else. `nepo-order` is untouched by it: it sends an explicit `stuff`
        // and `PlaceOrder` ships the manifest it is given.
        //
        // ============ READ THE WRITE BACK ====================================
        // `NepoSync.SetRestockRule` is `void` and returns SILENTLY on a null
        // component and on a null def. The verb re-reads with `GetRestock` and
        // compares field by field POST-CLAMP; a disagreement is
        // `restock-did-not-take` with the diagnosis, never a success.
        // ====================================================================
        [Verb("nepo-restock")]
        public static object NepoRestock(VerbContext ctx)
        {
            const string V = "nepo-restock";
            var a = ctx.Args;

            // The delete ruling first, so a caller reaching for one gets the
            // reason rather than the stray-key rule's generic sentence.
            NepoNoDelete(ctx.Command?.Args);

            a.NearMiss("def", "item", "thing", "defname");
            a.NearMiss("threshold", "restock_at", "at", "min", "low", "reorder_at");
            a.NearMiss("target", "restock_to", "to", "max", "up_to", "top_up");
            a.NearMiss("enabled", "enable", "active", "on");
            a.NearMiss("stuff", "material", "mat");
            a.NearMiss("color", "colour", "tint", "dye");
            a.NearMiss("dry_run", "preview", "simulate", "plan");
            a.RefuseStray(V, NepoRestockArgs,
                "Nothing was read and nothing was written. `dry_run` defaults to false, so a dropped "
                + "preflight flag writes a real rule — and a rule is a STANDING instruction the hourly "
                + "sweep spends credits on, not a one-shot order.");

            // The mode is decided by which keys are present, before anything is
            // read: any of the five rule fields makes this a write.
            bool wantsWrite = a.Has("threshold") || a.Has("target") || a.Has("enabled")
                || a.Has("stuff") || a.Has("color");
            string defName = a.Str("def", null);
            if (wantsWrite && defName == null)
                throw new VerbArgsException("a rule is keyed by ThingDef, so a write needs `def`. "
                    + "Send {def:\"MealSimple\", threshold:20, target:60}; send no arguments at all to "
                    + "list every rule. Nothing was written.");

            // The restock tier, and NOT the dad gate — see the header.
            var gate = NepoGate(V, wantsWrite, out object comp, restockTier: true, requireDad: false);
            if (gate != null) return gate;

            if (defName == null) return NepoRestockList(V, a, comp);

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
                throw new VerbArgsException("no ThingDef named '" + defName
                    + "' (see `nepo-catalog {filter:\"" + defName + "\"}`)");

            return wantsWrite ? NepoRestockWrite(V, a, comp, def) : NepoRestockRead(V, comp, def);
        }

        /// Every standing rule. A pure read — and a snapshot one: `AllRestocks`
        /// is the live `Dictionary.ValueCollection`, so the bridge copies it out
        /// before anything walks it.
        private static object NepoRestockList(string V, VerbArgs a, object comp)
        {
            int cap = a.Int("cap", NepoRestockCap);
            if (cap < 1 || cap > 200) throw new VerbArgsException("cap must be 1..200");

            var rules = Nepo.AllRestocks(comp);
            rules.Sort((x, y) => string.CompareOrdinal(NepoRuleKey(x), NepoRuleKey(y)));

            var rows = new List<object>();
            int on = 0, firing = 0;
            foreach (var r in rules)
            {
                var row = NepoRuleRow(comp, r);
                if (row["enabled"] as bool? == true) on++;
                if (row["would_fire"] as bool? == true) firing++;
                if (rows.Count < cap) rows.Add(row);
            }

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["mode"] = NepoMode(comp),
                ["total"] = rules.Count,
                ["enabled"] = on,
                ["would_fire_now"] = firing,
                ["shown"] = rows.Count,
                ["more"] = Math.Max(0, rules.Count - rows.Count),
                ["cap"] = cap,
                ["rows"] = rows,
                ["shown_note"] = rows.Count + " of " + rules.Count + " standing rule(s) shown; "
                    + on + " enabled, " + firing + " at or below threshold right now.",
                ["basis"] = "Nepo/NepoGameComponent.AllRestocks — `restockOrders.Values`, the live "
                    + "dictionary, snapshotted before it is walked. Rules are GLOBAL PER SAVE: nothing "
                    + "keys on a Map.",
                ["available_basis"] = NepoHomeMapBlock(),
                ["sweep"] = NepoSweepBlock(comp),
                ["removal"] = NepoRemovalNote,
                ["action"] = NoStamp(),
            };
            return d;
        }

        /// One rule, plus what a write of it would be allowed to say. The option
        /// lists are here and not in the list reply because they are per-def and
        /// this is the call that asked about a def.
        private static object NepoRestockRead(string V, object comp, ThingDef def)
        {
            object rule = null;
            try { rule = Nepo.GetRestock(comp, def); }
            catch (Exception e)
            {
                return NepoRefuse(V, "gate-unreadable", "Nepo/NepoGameComponent.GetRestock",
                    "reading the rule threw: " + e.GetType().Name + ": "
                    + Journal.Truncate(e.Message, 160), NepoMode(comp));
            }

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["mode"] = NepoMode(comp),
                ["def"] = def.defName,
                ["label"] = WorldSafe.Safe(() => def.LabelCap.ToString()),
                ["has_rule"] = rule != null,
                ["rule"] = rule == null ? null : NepoRuleRow(comp, rule),
                ["available"] = NepoAvailable(comp, def),
                ["available_basis"] = NepoHomeMapBlock(),
                ["orderable_now"] = NepoBool(() => Nepo.IsOrderable(def)),
                ["customize"] = NepoCustomizeBlock(def),
                ["sweep"] = NepoSweepBlock(comp),
                ["removal"] = NepoRemovalNote,
                ["action"] = NoStamp(),
            };
            string uncountable = NepoUncountable(def);
            if (uncountable != null) d["available_note"] = uncountable;
            if (rule == null)
                d["note"] = "no standing rule for this def. Create one with {def:\"" + def.defName
                    + "\", threshold:N, target:M}; both numbers are required and neither is "
                    + "defaulted. Nepo's own panel stages stackLimit/2 and stackLimit for a def with "
                    + "no rule, but a player sees those numbers before clicking and an agent would "
                    + "not, so this verb refuses instead of inventing them.";
            return d;
        }

        /// Create or update one rule, through the synced entry point, with the
        /// four widget gates in front of it and a read-back behind it.
        private static object NepoRestockWrite(string V, VerbArgs a, object comp, ThingDef def)
        {
            bool dryRun = a.Bool("dry_run", false);

            // ---- GATE 1: catalog membership ---------------------------------
            bool orderable;
            try { orderable = Nepo.IsOrderable(def); }
            catch (Exception e)
            {
                // A gate that threw has not passed.
                return NepoRefuse(V, "gate-unreadable", "Nepo/CatalogBuilder.IsOrderable",
                    "reading it threw: " + e.GetType().Name + ": " + Journal.Truncate(e.Message, 160),
                    NepoMode(comp));
            }
            if (!orderable)
                return NepoRefuse(V, "not-orderable",
                    "Nepo/CatalogBuilder.IsOrderable — the predicate OrderableDefs() filters on, and "
                    + "Nepo/Window_DadCatalog draws its restock panel only on a row that list built",
                    "'" + def.defName + "' has no row in Dad's catalog, so no restock panel exists for "
                    + "it and no player could set this rule. Nepo would still sweep and ship it — the "
                    + "sweep never re-checks orderability — which is exactly why this is refused. "
                    + "(A minifiable BUILDING additionally needs `allowMinifiableBuildings` unless it "
                    + "is art; that setting is currently "
                    + (Nepo.AllowMinifiableBuildings(comp) ? "ON" : "OFF") + ".) Nothing was written.",
                    NepoMode(comp));

            object existing;
            try { existing = Nepo.GetRestock(comp, def); }
            catch (Exception e)
            {
                return NepoRefuse(V, "gate-unreadable", "Nepo/NepoGameComponent.GetRestock",
                    "reading the current rule threw, so this write cannot say what it would change: "
                    + e.GetType().Name + ": " + Journal.Truncate(e.Message, 160), NepoMode(comp));
            }

            // THE SNAPSHOT MUST HAPPEN HERE. `SetRestock` mutates the EXISTING
            // RestockOrder in place when one is present, so a "before" row read
            // after the write would be the "after" row wearing a different key.
            var before = existing == null ? null : NepoRuleRow(comp, existing);
            bool wasEnabled = existing != null && Nepo.RestockEnabled(existing);

            // ---- the numbers ------------------------------------------------
            bool hasThreshold = a.Has("threshold");
            bool hasTarget = a.Has("target");
            if (existing == null && (!hasThreshold || !hasTarget))
                return NepoRefuse(V, "incomplete-rule",
                    "Nepo/Window_DadCatalog.DrawRestockPanel stages both numbers before its toggle "
                    + "can create a rule (Nepo/NepoSync.ToggleRestockRule takes threshold and target), "
                    + "and Nepo/NepoGameComponent.SetRestock has no defaults of its own",
                    "there is no standing rule for '" + def.defName + "' yet, so creating one needs "
                    + "BOTH `threshold` and `target`. Neither is defaulted: a guessed threshold is a "
                    + "standing instruction to spend credits every hour, and the panel's own staged "
                    + "defaults (stackLimit/2 and stackLimit) are numbers a player reads before "
                    + "clicking. Nothing was written.",
                    NepoMode(comp));

            int threshold = hasThreshold ? a.Int("threshold", 0) : Nepo.RestockThreshold(existing);
            int target = hasTarget ? a.Int("target", 0) : Nepo.RestockTarget(existing);

            // Only what the CALLER supplied is range-checked. A value carried
            // forward is what the rule already holds, and re-sending it is what
            // the panel does; refusing it would make a numbers-untouched write
            // fail on a state this verb did not create.
            if (hasThreshold) NepoRestockRange("threshold", threshold);
            if (hasTarget) NepoRestockRange("target", target);

            // `SetRestock`'s own two lines, applied here so the read-back has
            // something to compare against and the reply can say it happened.
            // The panel pre-clamps the same way before it sends.
            int storedTarget = Math.Max(threshold + 1, target);

            // ---- enabled ----------------------------------------------------
            // Absent means "leave it as it is" on an existing rule and "on" for
            // a new one — the widget exactly: its number fields re-send the
            // rule's current `active`, and its toggle creates enabled.
            bool enabled = a.Bool("enabled", existing == null || wasEnabled);

            // ---- GATE 2: the material ---------------------------------------
            ThingDef stuff;
            string stuffSource;
            if (a.Has("stuff"))
            {
                object raw = a.Raw("stuff");
                if (raw == null)
                {
                    stuff = null;
                    stuffSource = "cleared by this call — the reorder will ship in the def's default "
                        + "stuff (Verse/GenStuff.DefaultStuffFor)";
                }
                else
                {
                    if (!(raw is string sName) || sName.Length == 0)
                        throw new VerbArgsException("`stuff` must be a non-empty defName string, or "
                            + "null to clear it back to the def's default stuff");
                    stuff = DefDatabase<ThingDef>.GetNamedSilentFail(sName);
                    if (stuff == null)
                        throw new VerbArgsException("no ThingDef named '" + sName + "' for the "
                            + "`stuff` of '" + def.defName + "'");
                    if (!Nepo.CanCustomizeMaterial(def))
                        return NepoRefuse(V, "stuff-not-customizable",
                            "Nepo/CatalogBuilder.CanCustomizeMaterial (`MadeFromStuff && (IsArt || "
                            + "IsApparel)`) gates the customize panel's material picker, and "
                            + "Nepo/Window_DadCatalog.RestockStuff hard-nulls the stuff a rule is "
                            + "written with when the row does not offer one",
                            "'" + def.defName + "' has no material picker in the catalog, so a rule "
                            + "for it cannot carry a `stuff`. Drop the key. Nothing was written.",
                            NepoMode(comp));
                    if (!Nepo.AllowedStuffs(def).Contains(stuff))
                        return NepoRefuse(V, "stuff-not-allowed",
                            "Nepo/CatalogBuilder.AllowedStuffs(def) — `GenStuff.AllowedStuffsFor` — is "
                            + "the material picker's whole option list in Nepo/Window_DadCatalog"
                            + ".DrawMaterialPicker",
                            "'" + stuff.defName + "' is not one of the materials Dad will make '"
                            + def.defName + "' from. `nepo-restock {def:\"" + def.defName + "\"}` "
                            + "lists the ones he will. Nothing was written.",
                            NepoMode(comp));
                    stuffSource = "set by this call";
                }
            }
            else
            {
                // Carried, not dropped. `SetRestockRule` has no optional
                // parameters, so every write sends a stuff — and sending null
                // because the caller said nothing would silently change what the
                // next reorder ships in.
                stuff = existing == null ? null : Nepo.RestockStuff(existing);
                stuffSource = existing == null
                    ? "not set — a new rule with no `stuff` ships in the def's default stuff"
                    : "carried forward from the stored rule (the key was absent, and an absent key "
                      + "must not silently change what the reorder ships in)";
            }

            // ---- GATE 3: the colour -----------------------------------------
            Color? color;
            string colorSource;
            if (a.Has("color"))
            {
                object raw = a.Raw("color");
                if (raw == null)
                {
                    color = null;
                    colorSource = "cleared by this call — the reorder ships in the material's own "
                        + "colour, which is the panel's \"natural\"";
                }
                else
                {
                    if (!(raw is string cName) || cName.Length == 0)
                        throw new VerbArgsException("`color` must be a ColorDef defName string (the "
                            + "palette is Nepo/CatalogBuilder.StylingStationColors(); "
                            + "`nepo-restock {def:\"" + def.defName + "\"}` lists it), or null for "
                            + "the natural colour");
                    ColorDef cd = DefDatabase<ColorDef>.GetNamedSilentFail(cName);
                    if (cd == null)
                        throw new VerbArgsException("no ColorDef named '" + cName + "'. The palette "
                            + "is named by ColorDef because Nepo stores a raw UnityEngine.Color and "
                            + "an agent cannot type one; `nepo-restock {def:\"" + def.defName
                            + "\"}` lists every colour the picker offers, by defName.");
                    if (!Nepo.CanCustomizeColor(def))
                        return NepoRefuse(V, "color-not-customizable",
                            "Nepo/CatalogBuilder.CanCustomizeColor (`IsApparel && "
                            + "HasComp(CompColorable)`) gates the customize panel's colour palette, "
                            + "and Nepo/Window_DadCatalog.RestockColor hard-nulls the colour a rule "
                            + "is written with when the row is not colourable",
                            "'" + def.defName + "' has no colour picker in the catalog — it is not "
                            + "apparel carrying CompColorable — so a rule for it cannot carry a "
                            + "`color`. Drop the key. Nothing was written.",
                            NepoMode(comp));
                    if (!NepoInPalette(cd.color))
                        return NepoRefuse(V, "color-not-in-palette",
                            "Nepo/CatalogBuilder.StylingStationColors() is the whole option list "
                            + "Nepo/Window_DadCatalog.DrawCustomizePanel hands Widgets.ColorSelector",
                            "'" + cd.defName + "' is a real ColorDef but its colour is not one the "
                            + "catalog's picker offers. That list is the styling station's own "
                            + "(ColorType.Ideo and .Misc, falling back to .Structure when Ideology is "
                            + "off), so with Ideology absent most ideoligion colours are unreachable. "
                            + "`nepo-restock {def:\"" + def.defName + "\"}` lists the reachable ones. "
                            + "Nothing was written.",
                            NepoMode(comp));
                    color = cd.color;
                    colorSource = "set by this call, from ColorDef " + cd.defName;
                }
            }
            else
            {
                color = existing == null ? (Color?)null : Nepo.RestockColor(existing);
                colorSource = existing == null
                    ? "not set — a new rule with no `color` ships in the natural colour"
                    : "carried forward from the stored rule (an absent key must not silently recolour "
                      + "what the reorder ships)";
            }

            // ---- the shape of what is about to be written -------------------
            var wanted = NepoRuleRowOf(comp, def, threshold, storedTarget, enabled, stuff, color);
            var clamp = target == storedTarget ? null : new Dictionary<string, object>
            {
                ["target_sent"] = target,
                ["target_stored"] = storedTarget,
                ["why"] = "Nepo/NepoGameComponent.SetRestock is `target = Mathf.Max(threshold + 1, "
                    + "target)`, so a reorder always brings in at least one unit. "
                    + "Nepo/Window_DadCatalog.DrawRestockPanel pre-clamps the same way before it "
                    + "sends, so this is the number a player would have committed too.",
            };

            var gates = new Dictionary<string, object>
            {
                ["reproduced"] = new List<object>
                {
                    "catalog membership — Nepo/CatalogBuilder.IsOrderable",
                    "material — Nepo/CatalogBuilder.CanCustomizeMaterial + .AllowedStuffs",
                    "colour — Nepo/CatalogBuilder.CanCustomizeColor + .StylingStationColors",
                    "range 0.." + NepoRestockFieldMax
                        + " — Nepo/Window_DadCatalog.DrawRestockPanel's Widgets.TextFieldNumeric",
                },
                ["not_reproduced"] = "`dad-unavailable`, and the comms-console chain that opens the "
                    + "catalog window. The restock panel itself has no DadAvailable check — what is "
                    + "gated is the SWEEP (Nepo/NepoGameComponent.GameComponentTick's "
                    + "`&& DadAvailable`) — and editing a rule charges nothing and ships nothing. See "
                    + "`sweep` for whether this rule can actually fire.",
            };

            // ---- dry run ----------------------------------------------------
            if (dryRun)
            {
                var pre = new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = true,
                    ["mode"] = NepoMode(comp),
                    ["dry_run"] = true,
                    ["written"] = false,
                    ["def"] = def.defName,
                    ["would"] = wanted,
                    ["would_create"] = existing == null,
                    ["before"] = before,
                    ["stuff_source"] = stuffSource,
                    ["color_source"] = colorSource,
                    ["gates"] = gates,
                    ["sweep"] = NepoSweepBlock(comp),
                    ["available_basis"] = NepoHomeMapBlock(),
                    ["material_pref"] = NepoMaterialPrefNote(stuff, color),
                    ["removal"] = NepoRemovalNote,
                    ["note"] = "nothing was written. Every gate above ran — catalog membership, the "
                        + "material, the colour and the numeric range — so this is what the real call "
                        + "would store, not a guess. Re-send without `dry_run` to write it.",
                    ["action"] = NoStamp(),
                };
                if (clamp != null) pre["clamp"] = clamp;
                return pre;
            }

            // ---- the act ----------------------------------------------------
            // `NepoSync.SetRestockRule`, never `NepoGameComponent.SetRestock`:
            // only the former is registered with MP.RegisterSyncMethod
            // (Nepo/MpBridge.RegisterAll, "restock rule write"). The colour goes
            // through Nepo's own `EncodeColor` inside the bridge.
            try { Nepo.SetRestockRule(def, threshold, storedTarget, enabled, stuff, color); }
            catch (Exception e)
            {
                string diag = "Nepo/NepoSync.SetRestockRule threw: " + e.GetType().Name + ": "
                    + Journal.Truncate(e.Message, 200);
                Journal.EmitWarning("[AutoRimmer] nepo-restock: " + diag);
                return NepoRefuse(V, "set-threw", "Nepo/NepoSync.SetRestockRule",
                    diag + ". Re-read with `nepo-restock {def:\"" + def.defName + "\"}` before "
                    + "assuming nothing changed.",
                    NepoMode(comp));
            }

            // ---- READ THE WRITE BACK ----------------------------------------
            object after = null;
            try { after = Nepo.GetRestock(comp, def); } catch { }
            if (after == null)
            {
                string diag = "Nepo/NepoSync.SetRestockRule returned without storing anything — it "
                    + "returns silently when NepoGameComponent.Active is null and when the def is "
                    + "null, and GetRestock now answers null for '" + def.defName + "'.";
                Journal.EmitWarning("[AutoRimmer] nepo-restock: the rule did not take. " + diag);
                return NepoRefuse(V, "restock-did-not-take",
                    "Nepo/NepoSync.SetRestockRule, read back against Nepo/NepoGameComponent.GetRestock",
                    diag, NepoMode(comp));
            }

            var wrong = new List<string>();
            if (Nepo.RestockThreshold(after) != threshold)
                wrong.Add("threshold is " + Nepo.RestockThreshold(after) + ", sent " + threshold);
            if (Nepo.RestockTarget(after) != storedTarget)
                wrong.Add("target is " + Nepo.RestockTarget(after) + ", sent " + storedTarget
                    + " (already clamped)");
            if (Nepo.RestockEnabled(after) != enabled)
                wrong.Add("enabled is " + Nepo.RestockEnabled(after) + ", sent " + enabled);
            if (Nepo.RestockStuff(after) != stuff)
                wrong.Add("stuff is " + (Nepo.RestockStuff(after)?.defName ?? "null") + ", sent "
                    + (stuff?.defName ?? "null"));
            Color? afterColor = Nepo.RestockColor(after);
            if (afterColor.HasValue != color.HasValue
                || (color.HasValue && !NepoSameColor(afterColor.Value, color.Value)))
                wrong.Add("color is " + (afterColor.HasValue ? NepoHex(afterColor.Value) : "null")
                    + ", sent " + (color.HasValue ? NepoHex(color.Value) : "null")
                    + " — it crosses Nepo/NepoSync.EncodeColor as a string and comes back through "
                    + "its private DecodeColor, which answers null on anything it cannot parse");
            if (wrong.Count > 0)
            {
                string diag = "the stored rule disagrees with what was sent: "
                    + string.Join("; ", wrong.ToArray());
                Journal.EmitWarning("[AutoRimmer] nepo-restock: " + diag);
                return NepoRefuse(V, "restock-did-not-take",
                    "Nepo/NepoSync.SetRestockRule, read back field by field against "
                    + "Nepo/NepoGameComponent.GetRestock",
                    diag + ". The rule that IS stored is in `stored`.", NepoMode(comp),
                    new Dictionary<string, object> { ["stored"] = NepoRuleRow(comp, after) });
            }

            var stored = NepoRuleRow(comp, after);
            var changed = NepoRuleDiff(before, stored);

            long seq = Act(V, "restock", def.defName, new Dictionary<string, object>
            {
                ["created"] = existing == null,
                ["threshold"] = threshold,
                ["target"] = storedTarget,
                ["enabled"] = enabled,
                ["stuff"] = stuff?.defName,
                ["color"] = color.HasValue ? NepoHex(color.Value) : null,
                ["changed"] = changed,
            });

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["mode"] = NepoMode(comp),
                ["dry_run"] = false,
                ["written"] = true,
                ["created"] = existing == null,
                ["def"] = def.defName,
                ["rule"] = stored,
                ["before"] = before,
                ["changed"] = changed,
                ["stuff_source"] = stuffSource,
                ["color_source"] = colorSource,
                ["gates"] = gates,
                ["sweep"] = NepoSweepBlock(comp),
                ["available_basis"] = NepoHomeMapBlock(),
                ["material_pref"] = NepoMaterialPrefNote(stuff, color),
                ["removal"] = NepoRemovalNote,
                ["action"] = Stamp(seq),
            };
            if (clamp != null) d["clamp"] = clamp;
            if (!enabled)
                d["disabled_note"] = "the rule is stored and the sweep will skip it "
                    + "(Nepo/NepoGameComponent.RunRestockSweep's `if (!order.enabled) continue;`). "
                    + "The numbers are kept — this is what \"remove\" is spelled as.";
            return d;
        }

        // ==================================================================
        // ------------------------- restock helpers ------------------------
        // ==================================================================

        private const string NepoRemovalNote =
            "there is no delete. Nepo/NepoGameComponent.SetRestock has no removal branch — a zeroed "
            + "rule is STORED at threshold 0 / target 1 — and RemoveRestock has no NepoSync wrapper "
            + "and is not registered with Multiplayer, so calling it would be an unsynced write. The "
            + "catalog window cannot delete a rule either; its toggle only disables. Send "
            + "{def:\"…\", enabled:false} to stop the sweep acting on a rule while keeping its numbers.";

        /// A delete key is refused BY NAME, before the stray-key rule gets to
        /// it, because "the verb does not accept `remove`" is a much worse
        /// answer than "a rule is disabled, not deleted, and here is why".
        private static void NepoNoDelete(Dictionary<string, object> raw)
        {
            // Deliberately the RAW dictionary and not `VerbArgs.Has`: a `Has`
            // here would MARK these keys as read, so a later refactor that lost
            // the throw would let them slip past RefuseStray in silence — the
            // exact widening the stray-key rule exists to stop.
            if (raw == null) return;
            string[] words = { "remove", "delete", "clear", "unset", "drop", "cancel" };
            foreach (var w in words)
                if (raw.ContainsKey(w))
                    throw new VerbArgsException("this verb has no delete mode and does not read '" + w
                        + "'. " + NepoRemovalNote);
        }

        private static string NepoRuleKey(object rule)
        {
            var def = Nepo.RestockRuleDef(rule);
            return def?.defName ?? "";
        }

        private static Dictionary<string, object> NepoRuleRow(object comp, object rule)
            => NepoRuleRowOf(comp, Nepo.RestockRuleDef(rule), Nepo.RestockThreshold(rule),
                Nepo.RestockTarget(rule), Nepo.RestockEnabled(rule), Nepo.RestockStuff(rule),
                Nepo.RestockColor(rule));

        /// One row, built from values rather than from a rule object, so the
        /// dry run's preview and the stored rule are the same shape and can be
        /// compared key by key.
        private static Dictionary<string, object> NepoRuleRowOf(object comp, ThingDef def,
            int threshold, int target, bool enabled, ThingDef stuff, Color? color)
        {
            int? available = def == null ? null : NepoAvailable(comp, def);
            int? qty = available.HasValue ? (int?)Math.Max(0, target - available.Value) : null;
            int? unit = null;
            if (def != null)
                try { unit = Nepo.PriceFor(def, stuff); } catch { }

            var d = new Dictionary<string, object>
            {
                ["def"] = def?.defName,
                ["label"] = def == null ? null : WorldSafe.Safe(() => def.LabelCap.ToString()),
                ["enabled"] = enabled,
                ["threshold"] = threshold,
                ["target"] = target,
                ["stuff"] = stuff?.defName,
                ["color"] = color.HasValue ? NepoHex(color.Value) : null,
                ["color_def"] = color.HasValue ? NepoColorDefName(color.Value) : null,
                ["available"] = available,
                ["would_order"] = qty,
                // The sweep's own two tests, in its own order: enabled, def
                // non-null, and `available <= threshold`. `target` is always at
                // least `threshold + 1`, so a crossed threshold always yields a
                // positive quantity.
                ["would_fire"] = enabled && def != null && available.HasValue
                    && available.Value <= threshold,
                ["unit_price"] = unit,
                ["reorder_cost"] = unit.HasValue && qty.HasValue ? (object)((long)unit.Value * qty.Value) : null,
            };
            if (def == null)
            {
                d["note"] = "this rule's def no longer resolves (a mod was removed after the save "
                    + "was written). Nepo/NepoGameComponent.RunRestockSweep skips it: "
                    + "`if (!order.enabled || order.def == null) continue;`.";
            }
            else
            {
                // Null is "we could not look", and only a measured FALSE earns
                // the warning — a def we could not test must not be reported as
                // one that fell out of the catalog.
                bool? stillOrderable = NepoBool(() => Nepo.IsOrderable(def)) as bool?;
                d["orderable_now"] = stillOrderable;
                string uncountable = NepoUncountable(def);
                if (uncountable != null) d["available_note"] = uncountable;
                if (stillOrderable == false)
                    d["note"] = "this def is no longer in Dad's catalog "
                        + "(Nepo/CatalogBuilder.IsOrderable is false for it), but the sweep does not "
                        + "re-check that — it will still be ordered, priced and shipped.";
            }
            return d;
        }

        /// The two rows differing, in words, so the journal line and the reply
        /// say what a write actually moved rather than only what it stored.
        private static List<object> NepoRuleDiff(Dictionary<string, object> before,
            Dictionary<string, object> after)
        {
            var outp = new List<object>();
            if (before == null) { outp.Add("created"); return outp; }
            string[] keys = { "enabled", "threshold", "target", "stuff", "color_def", "color" };
            foreach (var k in keys)
            {
                object b = before.TryGetValue(k, out var bv) ? bv : null;
                object v = after.TryGetValue(k, out var av) ? av : null;
                if (Equals(b, v)) continue;
                // `color` and `color_def` describe the same change; the defName
                // is the readable half and the hex is the fallback when no
                // ColorDef matches.
                if (k == "color" && !Equals(before.TryGetValue("color_def", out var bd) ? bd : null,
                        after.TryGetValue("color_def", out var ad) ? ad : null))
                    continue;
                outp.Add(k + " " + (b ?? "null") + " -> " + (v ?? "null"));
            }
            if (outp.Count == 0) outp.Add("nothing — the rule already said this");
            return outp;
        }

        /// Nepo's own "you have N". Null is WE COULD NOT LOOK, never zero.
        private static int? NepoAvailable(object comp, ThingDef def)
        {
            if (def == null || NepoUncountable(def) != null) return null;
            try { return Nepo.ColonyAvailableCount(comp, def); }
            catch { return null; }
        }

        /// Why this def's available count must not be asked for, or null.
        ///
        /// `ColonyAvailableCount` reaches `Verse/ListerThings.ThingsOfDef`,
        /// which `Log.ErrorOnce`s for `MinifiedThing` and tells the caller to
        /// use the group instead — a RED ERROR raised by an agent-supplied
        /// `def`, which this repo's zero-red-errors invariant forbids.
        /// `SpatialVerbs.Nearest` and `things` route around the same call; this
        /// one cannot route around it (the count is Nepo's, computed inside the
        /// other mod), so it declines to ask and says so. `nepo-restock
        /// {def:"MinifiedThing"}` is one word away from every other read, so
        /// this is reachable, not theoretical.
        private static string NepoUncountable(ThingDef def)
        {
            ThingDef minified = null;
            try { minified = ThingDefOf.MinifiedThing; } catch { }
            if (minified != null && def == minified)
                return "`available` was NOT read for this def. Nepo/NepoGameComponent"
                    + ".ColonyAvailableCount goes through Verse/ListerThings.ThingsOfDef, which "
                    + "Log.ErrorOnce's on MinifiedThing and tells you to ask for the group instead — "
                    + "and a red error raised by an argument is a thing this mod does not do. Null "
                    + "here is 'not asked', not 'none'.";
            return null;
        }

        /// WHICH map the availability count came from. `ColonyAvailableCount`
        /// and `RunRestockSweep` both open `Find.AnyPlayerHomeMap`, so with two
        /// home maps "the colony has N" is a statement about ONE of them, and
        /// the drop lands there too.
        private static Dictionary<string, object> NepoHomeMapBlock()
        {
            Map home = null;
            int homes = 0;
            try
            {
                home = Find.AnyPlayerHomeMap;
                var maps = Find.Maps;
                if (maps != null)
                    for (int i = 0; i < maps.Count; i++)
                        if (maps[i] != null && maps[i].IsPlayerHome) homes++;
            }
            catch { }

            var d = new Dictionary<string, object>
            {
                ["map_id"] = home?.uniqueID,
                ["map"] = home == null ? null : WorldSafe.Safe(() => home.Parent?.LabelCap.ToString()),
                ["player_home_maps"] = homes,
                ["basis"] = "Nepo/NepoGameComponent.ColonyAvailableCount is MapCount(map, def) + "
                    + "CommittedCount(def) on `Find.AnyPlayerHomeMap` — storage, loose stacks, "
                    + "haul-source containers and carried units, plus in-flight drops and dispatches "
                    + "awaiting a call. Nepo/NepoGameComponent.RunRestockSweep opens the SAME map and "
                    + "returns early when it is null. Reading it is observer-safe: its only cache "
                    + "effect is Verse/MapPawns.SpawnedPawnsInFaction creating an EMPTY per-faction "
                    + "list for a faction that has none — create-if-missing, not the clear-and-refill "
                    + "class WorldSafe.cs refuses — and it is what Nepo's own panel does every "
                    + "redraw.",
            };
            if (home == null)
                d["note"] = "there is no player home map, so the sweep returns before it looks at any "
                    + "rule and `available` could not be read.";
            else if (homes > 1)
                d["note"] = "there are " + homes + " player home maps. `available` is this one's, not "
                    + "the colony's, and the reorder is decided and delivered against whichever map "
                    + "`Find.AnyPlayerHomeMap` returns at sweep time.";
            return d;
        }

        /// When the sweep next looks, and whether it will look at all.
        private static Dictionary<string, object> NepoSweepBlock(object comp)
        {
            int now = 0;
            try { now = Find.TickManager.TicksGame; } catch { }
            int? interval = Nepo.RestockSweepIntervalTicks();
            object next = null;
            if (interval.HasValue && interval.Value > 0)
                next = now + (interval.Value - now % interval.Value);

            var d = new Dictionary<string, object>
            {
                ["now_tick"] = now,
                ["interval_ticks"] = interval.HasValue ? (object)interval.Value : null,
                ["next_sweep_tick"] = next,
                ["dad_available"] = NepoBool(() => Nepo.DadAvailable(comp)),
                ["require_comms_for_restock"] = NepoBool(() => Nepo.RequireCommsForRestock(comp)),
                ["basis"] = "Nepo/NepoGameComponent.GameComponentTick runs the sweep only when "
                    + "`now % NepoTuning.RestockCheckIntervalTicks == 0 && DadAvailable`, inside a "
                    + "tick that has already returned unless `now % 250 == 0` and `IsNepoGame`. "
                    + "RunRestockSweep itself is PRIVATE — there is no way to force it from a verb, "
                    + "and DevForceRestockSweep is Nepo's own dev-only entry. A rule written now is "
                    + "therefore not visible in `nepo-inbound` until the next sweep tick above.",
            };
            if (interval.HasValue)
                d["waiting"] = "advance up to " + interval.Value + " ticks with the threshold "
                    + "genuinely crossed to see this fire.";
            else
                d["interval_unknown"] = Nepo.SweepUnavailable
                    + " — so this verb will not say how long a sweep is. Not 2500: unread.";
            d["outcome"] = NepoBool(() => Nepo.RequireCommsForRestock(comp)) as bool? == false
                ? "with `requireCommsForRestock` OFF, a fired rule is charged and shipped on the spot "
                  + "and shows up in `nepo-inbound`'s SHIPMENTS."
                : "with `requireCommsForRestock` ON (the default), a fired rule becomes a "
                  + "PendingDispatch — unpaid, waiting for a colonist to call it in at a comms "
                  + "console (Nepo/WorkGiver_DispatchRestockOrder) — and shows up in "
                  + "`nepo-inbound`'s DISPATCHES.";
            return d;
        }

        /// What a write of this rule also did to Nepo's remembered material.
        private static string NepoMaterialPrefNote(ThingDef stuff, Color? color)
        {
            string what = stuff == null && !color.HasValue
                ? "REMOVED Nepo's remembered material for this def"
                : "overwrote Nepo's remembered material for this def";
            return "writing a rule also " + what + " — Nepo/NepoGameComponent.SetRestock's last line "
                + "is `SetMaterialPref(def, stuff, color)`, and SetMaterialPref removes the entry for "
                + "a default-stuff, no-colour choice. Measured: the only reader of `materialPrefs` is "
                + "Nepo/Window_DadCatalog.SeedCustomizationFromPrefs, where a standing RULE already "
                + "wins over the pref — so what this changes is what a human's next catalog visit "
                + "comes up pre-filled with. `nepo-order` is unaffected: it sends an explicit `stuff` "
                + "and Nepo/NepoSync.PlaceOrder ships the manifest it is handed.";
        }

        /// What a write of this def may say, from the customize panel's own
        /// predicates and option lists.
        private static Dictionary<string, object> NepoCustomizeBlock(ThingDef def)
        {
            bool canMat = false, canCol = false;
            try { canMat = Nepo.CanCustomizeMaterial(def); } catch { }
            try { canCol = Nepo.CanCustomizeColor(def); } catch { }

            var stuffs = new List<object>();
            int stuffTotal = 0;
            if (canMat)
            {
                var all = Nepo.AllowedStuffs(def);
                stuffTotal = all.Count;
                for (int i = 0; i < all.Count && stuffs.Count < NepoRestockOptionCap; i++)
                    if (all[i] != null) stuffs.Add(all[i].defName);
            }

            var colors = new List<object>();
            int colorTotal = 0;
            if (canCol)
            {
                var palette = Nepo.StylingStationColors();
                colorTotal = palette.Count;
                for (int i = 0; i < palette.Count && colors.Count < NepoRestockOptionCap; i++)
                    colors.Add(new Dictionary<string, object>
                    {
                        ["color_def"] = NepoColorDefName(palette[i]),
                        ["hex"] = NepoHex(palette[i]),
                    });
            }

            return new Dictionary<string, object>
            {
                ["can_material"] = canMat,
                ["can_color"] = canCol,
                ["stuffs_total"] = stuffTotal,
                ["stuffs"] = stuffs,
                ["colors_total"] = colorTotal,
                ["colors"] = colors,
                ["cap"] = NepoRestockOptionCap,
                ["basis"] = "Nepo/CatalogBuilder.CanCustomizeMaterial (`MadeFromStuff && (IsArt || "
                    + "IsApparel)`) with .AllowedStuffs, and .CanCustomizeColor (`IsApparel && "
                    + "HasComp(CompColorable)`) with .StylingStationColors(). `color` is passed as a "
                    + "ColorDef DEFNAME: Nepo stores a raw UnityEngine.Color, the palette is built "
                    + "from ColorDefs, and a defName is the only handle an agent can type. A palette "
                    + "entry with a null `color_def` is a colour no ColorDef in this bench matches by "
                    + "value and cannot be named.",
            };
        }

        private static void NepoRestockRange(string key, int v)
        {
            if (v < 0 || v > NepoRestockFieldMax)
                throw new VerbArgsException("'" + key + "' must be 0.." + NepoRestockFieldMax
                    + " (got " + v + "). Nepo/Window_DadCatalog.DrawRestockPanel draws both numbers "
                    + "with `Widgets.TextFieldNumeric(…, 0f, 999999f)`, so a player cannot commit one "
                    + "outside that range. The backend would clamp a negative to 0 silently, which is "
                    + "a rule you did not ask for. Nothing was written.");
        }

        // ---- colours ------------------------------------------------------
        // Nepo stores `UnityEngine.Color?` and the palette is a List<Color>, so
        // nothing on that surface has a name. These map between the raw colour
        // and the ColorDef the palette was built from, which is the only handle
        // an agent can send.

        private static bool NepoSameColor(Color a, Color b)
            // `==` on Color is Unity's APPROXIMATE compare (squared magnitude of
            // the difference under 1e-10), which is what a round trip through
            // Nepo/NepoSync.EncodeColor's "R" formatting and back wants.
            // `List<Color>.Contains` would use exact float equality instead.
            => a == b;

        private static bool NepoInPalette(Color c)
        {
            List<Color> palette;
            try { palette = Nepo.StylingStationColors(); }
            catch { return false; }
            for (int i = 0; i < palette.Count; i++)
                if (NepoSameColor(palette[i], c)) return true;
            return false;
        }

        private static string NepoColorDefName(Color c)
        {
            try
            {
                var all = DefDatabase<ColorDef>.AllDefsListForReading;
                for (int i = 0; i < all.Count; i++)
                    if (all[i] != null && NepoSameColor(all[i].color, c)) return all[i].defName;
            }
            catch { }
            return null;
        }

        private static string NepoHex(Color c)
            => "#" + NepoChan(c.r) + NepoChan(c.g) + NepoChan(c.b) + NepoChan(c.a);

        private static string NepoChan(float v)
            => Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255).ToString("X2");

        // ==================================================================
        // ------------------------------ gates -----------------------------
        // ==================================================================

        /// The refusals every nepo verb shares, in order, cheapest first.
        /// Returns null when the call may proceed and `comp` is non-null.
        ///
        /// `restockTier` picks `Nepo.RestockAvailable` over the read/order
        /// tiers, so a drifted restock api costs `nepo-restock` and nothing
        /// else (NepoBridge.cs's three-tier comment).
        ///
        /// `requireDad` is a DECISION, not a default, and only `nepo-restock`
        /// turns it off — see that verb's header. Every other write keeps it.
        private static object NepoGate(string verb, bool forWrite, out object comp,
            bool restockTier = false, bool requireDad = true)
        {
            comp = null;

            bool blocked = restockTier ? !Nepo.RestockAvailable
                : forWrite ? !Nepo.OrderAvailable : !Nepo.Available;
            if (blocked)
            {
                // Absence and drift are different news. Absence is the ordinary
                // case on a bench without the mod and is NOT an error.
                return NepoRefuse(verb, Nepo.Absent ? "nepo-absent" : "nepo-api-drift",
                    "AutoRimmer/NepoBridge.cs Resolve() — bound by name AND signature, so a changed "
                    + "shape disables the verb instead of throwing inside it",
                    restockTier ? Nepo.RestockUnavailable
                        : forWrite ? Nepo.OrderUnavailable : Nepo.Unavailable,
                    null);
            }

            comp = Nepo.Component();
            if (comp == null)
                return NepoRefuse(verb, "no-nepo-component",
                    "Nepo/NepoGameComponent.Active is `Current.Game?.GetComponent<NepoGameComponent>()`",
                    ModName() + " is loaded but there is no game component — no game is loaded yet, or "
                    + "this save predates the mod. Load a colony first.",
                    null);

            bool isNepo;
            try { isNepo = Nepo.IsNepoGame(comp); }
            catch (Exception e)
            {
                return NepoRefuse(verb, "gate-unreadable",
                    "Nepo/NepoGameComponent.IsNepoGame", "reading it threw: " + e.GetType().Name
                    + ": " + Journal.Truncate(e.Message, 160), null);
            }

            if (!isNepo)
                return NepoRefuse(verb, "not-a-nepo-colony",
                    "Nepo/NepoGameComponent.GameComponentTick opens with `if (!IsNepoGame) return;`, "
                    + "and `IsNepoGame => scenarioActive || gateArmed`",
                    "this colony was not started from the Nepo scenario and has no armed lineage "
                    + "gate, so Dad is not reachable — and a shipment scheduled here would sit in "
                    + "`pending` forever, because the delivery sweep is inside the tick that returns "
                    + "early. Nothing was ordered.",
                    NepoMode(comp));

            if (forWrite && requireDad)
            {
                bool dad;
                try { dad = Nepo.DadAvailable(comp); }
                catch (Exception e)
                {
                    return NepoRefuse(verb, "gate-unreadable",
                        "Nepo/NepoGameComponent.DadAvailable", "reading it threw: " + e.GetType().Name
                        + ": " + Journal.Truncate(e.Message, 160), NepoMode(comp));
                }
                if (!dad)
                    return NepoRefuse(verb, "dad-unavailable",
                        "Nepo/Patch_CommsConsole_GetCommTargets.Postfix appends DadCommTarget only "
                        + "when `NepoGameComponent.Active?.DadAvailable ?? false`, and "
                        + "Nepo/NepoSync.PlaceOrder returns without charging on the same test",
                        "Dad is not answering — the bloodline gate is armed and the heir is gone, so "
                        + "he is not in the console's contact list. Nothing was ordered.",
                        NepoMode(comp));
            }

            return null;
        }

        private static string ModName() => Nepo.ModName + " (" + Nepo.PackageId + ")";

        /// `Building_CommsConsole.GetFailureReason` is private; this is it,
        /// MINUS its Talking clause and PLUS Nepo's Sight clause, which is the
        /// exact difference Nepo's own float-menu patch makes. Ordered the way
        /// vanilla orders it so the first true clause is the one reported.
        ///
        /// Not `CommsVerbs.ConsoleFailure`: that one reproduces vanilla
        /// faithfully, Talking clause and all, which is right for `comms-call`
        /// and wrong here. A mute colonist CAN call Dad
        /// (`Patch_CommsConsole_GetFloatMenuOptions.ShouldOfferDadDespiteMuteness`
        /// exists for no other purpose) and refusing one would invent a
        /// restriction the player does not have.
        private static string NepoConsoleFailure(Building_CommsConsole console, Pawn pawn, Map map)
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
                if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Sight))
                    return WorldSafe.Safe(() => pawn.LabelShortCap.ToString())
                        + " cannot see, and Nepo's catalog is read, not heard "
                        + "(DadCommTarget.CanRead requires PawnCapacityDefOf.Sight)";
                if (!console.CanUseCommsNow)
                    // Vanilla Log.Errors in the equivalent position. We do not:
                    // the clauses above are the known causes, so reaching this
                    // means one we do not know about, and reporting it beats
                    // breaching the zero-red-errors invariant to find out.
                    return "the comms console cannot be used now for a reason none of "
                        + "Building_CommsConsole.CanUseCommsNow's own clauses explains";
            }
            catch (Exception e)
            {
                // A gate that threw has not passed.
                return "checking the console's preconditions threw: " + e.GetType().Name + ": "
                    + Journal.Truncate(e.Message, 160);
            }
            return null;
        }

        /// The colonist the game would consider to have made the call. An
        /// explicit `negotiator` goes through `Dev.PawnArg`, which resolves ANY
        /// pawn on the map — a prisoner, a raider, a mechanoid — so the Sight
        /// gate runs on both paths and the auto-picked pawn simply always passes
        /// it. Auto-pick is by `thingIDNumber` ascending, the stable roster order
        /// `1eb2262` settled on, so two identical calls pick the same colonist.
        private static Pawn NepoNegotiatorArg(Map map, VerbArgs a, Building_CommsConsole console,
            out string error)
        {
            error = null;
            if (a.Has("negotiator"))
            {
                var p = Dev.PawnArg(map, a, "negotiator");
                if (p == null) { error = "no such pawn"; return null; }
                return p;
            }

            var pool = new List<Pawn>();
            try { pool.AddRange(map.mapPawns.FreeColonistsSpawned); } catch { }
            pool.Sort((x, y) => x.thingIDNumber.CompareTo(y.thingIDNumber));

            int sighted = 0;
            foreach (var p in pool)
            {
                if (p == null || p.Dead || p.Downed) continue;
                bool sees = false;
                try { sees = p.health.capacities.CapableOf(PawnCapacityDefOf.Sight); } catch { }
                if (!sees) continue;
                sighted++;
                bool reaches = false;
                try { reaches = p.CanReach(console, PathEndMode.InteractionCell, Danger.Some); }
                catch { }
                if (reaches) return p;
            }

            error = pool.Count == 0
                ? "there are no free colonists on the map to make the call"
                : sighted == 0
                    ? "no free colonist can SEE — Nepo/DadCommTarget.CanRead requires "
                      + "PawnCapacityDefOf.Sight, because the catalog is read rather than heard"
                    : "no sighted free colonist can reach the comms console. Name one explicitly with "
                      + "`negotiator` to get the game's own reason for that pawn.";
            return null;
        }

        // ==================================================================
        // --------------------------- serialization ------------------------
        // ==================================================================

        /// Read fresh on every reply, never cached and never written.
        private static Dictionary<string, object> NepoMode(object comp)
        {
            if (comp == null) return null;
            var d = new Dictionary<string, object>();
            try
            {
                bool unlimited = Nepo.UnlimitedMoney(comp);
                bool instant = Nepo.InstantDelivery(comp);
                d["unlimited_money"] = unlimited;
                d["instant_delivery"] = instant;
                d["balance"] = Nepo.Budget(comp);
                d["balance_meaning"] = unlimited
                    ? "UNLIMITED — Nepo/NepoGameComponent.CanAfford always answers true and Spend "
                      + "leaves the balance where it is, adding the cost to `spent_while_unlimited` "
                      + "instead. Do not plan around this number."
                    : "credits. Nepo/NepoGameComponent.Spend debits it, clamped at zero.";
                d["spent_while_unlimited"] = Nepo.SpentWhileUnlimited(comp);
                int? delay = Nepo.DeliveryDelayTicks(comp);
                // null is WE COULD NOT LOOK, never zero — zero is what
                // `instantDelivery` legitimately produces.
                d["delivery_delay_ticks"] = delay.HasValue ? (object)delay.Value : null;
                d["delivery_meaning"] = instant
                    ? "INSTANT — the shipment is scheduled for the current tick, but the pod still "
                      + "waits for the next tick where `now % 250 == 0` "
                      + "(Nepo/NepoGameComponent.GameComponentTick)."
                    : delay.HasValue
                        ? "a pod lands " + NepoSpan(delay.Value) + " after the order."
                        : "UNKNOWN — " + Nepo.DelayUnavailable + ". Not zero, and not two days: "
                          + "unread.";
                d["allowance_per_period"] = Nepo.AllowancePerPeriod(comp);
                d["allowance_period_days"] = Nepo.AllowancePeriodDays(comp);
                d["ticks_until_next_allowance"] = Nepo.TicksUntilAllowance(comp);
                d["allow_slave_purchases"] = Nepo.AllowSlavePurchases(comp);
                d["allow_minifiable_buildings"] = Nepo.AllowMinifiableBuildings(comp);
                d["require_comms_for_restock"] = Nepo.RequireCommsForRestock(comp);
                d["require_comms_meaning"] = "gates AUTOMATED restock and surgery reorders only "
                    + "(Nepo/NepoGameComponent.RunRestockSweep and .RunSurgeryOrderSweep are its only "
                    + "two call sites). It does not gate `nepo-order`, and it never gated the catalog "
                    + "window either.";
                d["dad_available"] = Nepo.DadAvailable(comp);
                d["is_nepo_colony"] = Nepo.IsNepoGame(comp);
                d["read_only"] = "every field here is READ from Nepo/NepoGameComponent.Active.Config. "
                    + "No verb in AutoRimmer writes a Nepo setting.";
            }
            catch (Exception e)
            {
                // Rule 2: a mode we could not read is not a mode of `false`.
                // The dictionary accumulates, so the keys already in it were
                // read successfully; it is the ABSENT ones that are unknown.
                // Saying "treat everything as unknown" would throw away good
                // readings, and saying nothing would let an absent key read as
                // a `false` the caller never got.
                d["error"] = "reading Nepo's mode stopped partway: " + e.GetType().Name + ": "
                    + Journal.Truncate(e.Message, 160)
                    + ". The fields present above were read; any field MISSING from this block was "
                    + "not read at all — absent here never means off.";
            }
            return d;
        }

        private static object NepoBool(Func<bool> f)
        {
            try { return f(); } catch { return null; }
        }

        private static Dictionary<string, object> NepoConsoleRef(Building_CommsConsole c)
            => new Dictionary<string, object>
            {
                ["id"] = c.thingIDNumber,
                ["label"] = WorldSafe.Safe(() => c.LabelCap.ToString()),
                ["can_use_now"] = WorldSafe.SafeObj(() => (object)c.CanUseCommsNow),
            };

        private static Dictionary<string, object> NepoKindLine(string what, PawnKindDef k, int count,
            int unit)
            => new Dictionary<string, object>
            {
                ["kind"] = what,
                ["def"] = k.defName,
                ["label"] = WorldSafe.Safe(() => k.LabelCap.ToString()),
                ["count"] = count,
                ["unit_price"] = unit,
                ["line_total"] = (long)unit * count,
            };

        private static Dictionary<string, object> NepoShipmentRow(object s, int now)
        {
            int tick = Nepo.ShipmentTick(s);
            var d = new Dictionary<string, object>
            {
                ["arrival_tick"] = tick,
                ["arrives_in_ticks"] = tick - now,
                ["arrives_in"] = NepoSpan(Math.Max(0, tick - now)),
                ["due"] = tick <= now,
            };
            var rows = new List<object>();
            long value = 0;
            foreach (var e in Nepo.ShipmentItems(s))
            {
                var def = Nepo.ItemDef(e);
                var stuff = Nepo.ItemStuff(e);
                int n = Nepo.ItemCount(e);
                int price = def == null ? 0 : Nepo.PriceFor(def, stuff);
                value += (long)price * n;
                rows.Add(new Dictionary<string, object>
                {
                    ["kind"] = "item",
                    ["def"] = def?.defName,
                    ["label"] = def == null ? null : WorldSafe.Safe(() => def.LabelCap.ToString()),
                    ["count"] = n,
                    ["stuff"] = stuff?.defName,
                });
            }
            foreach (var e in Nepo.ShipmentMechs(s))
            {
                var k = Nepo.MechKind(e);
                int n = Nepo.MechCount(e);
                if (k != null) value += (long)Nepo.PriceForMech(k) * n;
                rows.Add(new Dictionary<string, object>
                {
                    ["kind"] = "mech",
                    ["def"] = k?.defName,
                    ["label"] = k == null ? null : WorldSafe.Safe(() => k.LabelCap.ToString()),
                    ["count"] = n,
                });
            }
            foreach (var e in Nepo.ShipmentAnimals(s))
            {
                var k = Nepo.AnimalKind(e);
                int n = Nepo.AnimalCount(e);
                if (k != null) value += (long)Nepo.PriceForAnimal(k) * n;
                rows.Add(new Dictionary<string, object>
                {
                    ["kind"] = "animal",
                    ["def"] = k?.defName,
                    ["label"] = k == null ? null : WorldSafe.Safe(() => k.LabelCap.ToString()),
                    ["count"] = n,
                });
            }
            foreach (var e in Nepo.ShipmentSlaves(s))
            {
                var p = Nepo.SlavePawn(e);
                if (p != null) value += Nepo.PriceForSlave(p);
                rows.Add(new Dictionary<string, object>
                {
                    ["kind"] = "slave",
                    ["name"] = p == null ? null : WorldSafe.Safe(() => p.LabelShortCap.ToString()),
                    ["pawn_id"] = p?.thingIDNumber,
                    ["count"] = 1,
                });
            }
            d["contents"] = rows;
            d["lines"] = rows.Count;
            // A shipment carries no price of its own — Nepo charges at order
            // time and stores only the manifest — so this is a re-priced
            // estimate at TODAY's catalog price, which is not necessarily what
            // was paid.
            d["value_now"] = value;
            d["value_note"] = "re-priced at today's catalog price. Nepo/PendingShipment stores no "
                + "cost, so this is not necessarily what was paid.";
            return d;
        }

        /// Identity of every shipment currently pending, so the one PlaceOrder
        /// adds can be picked out afterwards. Reference identity, not a tick:
        /// two orders placed on the same tick have the same `deliveryTick`.
        private static HashSet<object> NepoShipmentIdentity(object comp)
        {
            var set = new HashSet<object>(ReferenceComparer.Instance);
            foreach (var s in Nepo.PendingShipments(comp)) set.Add(s);
            return set;
        }

        /// The shipment PlaceOrder just appended, or null if it appended none.
        /// `DispatchOnePending` can APPEND to an existing shipment rather than
        /// make a new one, but only from the restock job — never from
        /// PlaceOrder, which always constructs one — so a new reference is the
        /// right test and its absence is a genuine failure.
        private static object NepoNewShipment(object comp, HashSet<object> before)
        {
            object found = null;
            foreach (var s in Nepo.PendingShipments(comp))
                if (!before.Contains(s)) found = s;  // last wins: PlaceOrder appends
            return found;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            bool IEqualityComparer<object>.Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices
                .RuntimeHelpers.GetHashCode(o);
        }

        /// `RimWorld/GenDate.ToStringTicksToPeriod` is the game's own phrasing
        /// and is what the catalog's own ETA label uses. Wrapped because it goes
        /// through `Translate`.
        private static string NepoSpan(int ticks)
        {
            if (ticks <= 0) return "now";
            return WorldSafe.Safe(() => ticks.ToStringTicksToPeriod())
                ?? (ticks + " ticks");
        }

        // ==================================================================
        // ----------------------------- sections ---------------------------
        // ==================================================================

        private static bool NepoMatch(string filter, string defName, Func<string> label)
        {
            if (filter == null) return true;
            if (defName != null && defName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            string human = WorldSafe.Safe(label) ?? "";
            return human.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Dictionary<string, object> NepoSection(string what, int total, int matched,
            List<object> rows, int cap, List<string> notes, string basis)
        {
            notes.Add(rows.Count + " of " + total + " " + what + " shown"
                + (matched == total ? "" : " (" + matched + " matched the filter)"));
            return new Dictionary<string, object>
            {
                ["total"] = total,
                ["matched"] = matched,
                ["shown"] = rows.Count,
                ["more"] = Math.Max(0, matched - rows.Count),
                ["cap"] = cap,
                ["rows"] = rows,
                ["basis"] = basis,
            };
        }

        private static Dictionary<string, object> NepoItemSection(object comp, string filter, int cap,
            List<string> notes)
        {
            var all = Nepo.OrderableDefs();
            var rows = new List<object>();
            int matched = 0;
            foreach (var def in all)
            {
                if (def == null) continue;
                if (!NepoMatch(filter, def.defName, () => def.LabelCap.ToString())) continue;
                matched++;
                if (rows.Count >= cap) continue;
                bool stuffable = false;
                try { stuffable = Nepo.CanCustomizeMaterial(def); } catch { }
                rows.Add(new Dictionary<string, object>
                {
                    ["def"] = def.defName,
                    ["label"] = WorldSafe.Safe(() => def.LabelCap.ToString()),
                    ["category"] = WorldSafe.Safe(() => def.FirstThingCategory?.defName),
                    ["category_label"] = WorldSafe.Safe(() => def.FirstThingCategory?.LabelCap.ToString()),
                    ["price"] = Nepo.PriceFor(def),
                    ["stuffable"] = stuffable,
                    ["default_stuff"] = stuffable
                        ? WorldSafe.Safe(() => GenStuff.DefaultStuffFor(def)?.defName) : null,
                    ["mod"] = WorldSafe.Safe(() => def.modContentPack?.PackageId),
                });
            }
            return NepoSection("items", all.Count, matched, rows, cap, notes,
                "Nepo/CatalogBuilder.OrderableDefs() — every ThingDef with tradeability != None, "
                + "PlayerAcquirable, not a corpse and BaseMarketValue > 0, plus minifiable buildings "
                + "when they are art or `allowMinifiableBuildings` is on. `price` is the STUFFLESS "
                + "price; a stuffable row's real price is Nepo/CatalogBuilder.PriceFor(def, stuff).");
        }

        private static Dictionary<string, object> NepoMechSection(object comp, string filter, int cap,
            List<string> notes)
        {
            var all = Nepo.OrderableMechs();
            var rows = new List<object>();
            int matched = 0;
            foreach (var k in all)
            {
                if (k == null) continue;
                if (!NepoMatch(filter, k.defName, () => k.LabelCap.ToString())) continue;
                matched++;
                if (rows.Count >= cap) continue;
                rows.Add(new Dictionary<string, object>
                {
                    ["kind"] = k.defName,
                    ["label"] = WorldSafe.Safe(() => k.LabelCap.ToString()),
                    ["category"] = WorldSafe.Safe(() => k.RaceProps?.mechWeightClass?.defName),
                    ["price"] = Nepo.PriceForMech(k),
                });
            }
            return NepoSection("mechs", all.Count, matched, rows, cap, notes,
                "Nepo/CatalogBuilder.OrderableMechs() — EMPTY, and the window shows no mech folder at "
                + "all, unless Biotech is active and `allowMechPurchases` is on in Nepo's mod "
                + "settings (a per-machine setting, not the save config). Price is a flat table by "
                + "weight class, not market value.");
        }

        private static Dictionary<string, object> NepoAnimalSection(object comp, string filter, int cap,
            List<string> notes)
        {
            var all = Nepo.OrderableAnimals();
            var rows = new List<object>();
            int matched = 0;
            foreach (var k in all)
            {
                if (k == null) continue;
                if (!NepoMatch(filter, k.defName, () => k.LabelCap.ToString())) continue;
                matched++;
                if (rows.Count >= cap) continue;
                string tag = null;
                try { tag = Nepo.AnimalTradeTag(k); } catch { }
                rows.Add(new Dictionary<string, object>
                {
                    ["kind"] = k.defName,
                    ["label"] = WorldSafe.Safe(() => k.LabelCap.ToString()),
                    ["category"] = tag,
                    ["category_label"] = tag == null
                        ? "Other" : WorldSafe.Safe(() => Nepo.AnimalTagLabel(tag)),
                    ["price"] = Nepo.PriceForAnimal(k),
                });
            }
            return NepoSection("animals", all.Count, matched, rows, cap, notes,
                "Nepo/CatalogBuilder.OrderableAnimals() — EMPTY unless `allowAnimalPurchases` is on "
                + "in Nepo's mod settings (per-machine, not the save config). One entry per RACE, "
                + "deduped; the category is Nepo/CatalogBuilder.AnimalTradeTag.");
        }

        private static Dictionary<string, object> NepoSlaveSection(object comp, string filter, int cap,
            List<string> notes)
        {
            // `SlaveRosterNoGenerate`, never `SlaveRoster` — see the header.
            var all = Nepo.SlaveRoster(comp);
            var rows = new List<object>();
            int matched = 0;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null) continue;
                if (!NepoMatch(filter, p.def?.defName, () => p.LabelShortCap.ToString())) continue;
                matched++;
                if (rows.Count >= cap) continue;
                rows.Add(new Dictionary<string, object>
                {
                    // THE HANDLE. `nepo-order {slaves:[i]}` takes this index,
                    // because Nepo/NepoSync.EncodeOrder addresses a bought slave
                    // as `roster.IndexOf(slave)` — a roster pawn is registered
                    // with neither a map nor WorldPawns, so a thingIDNumber
                    // would not resolve on the far side.
                    ["index"] = i,
                    ["name"] = WorldSafe.Safe(() => p.LabelShortCap.ToString()),
                    ["pawn_id"] = p.thingIDNumber,
                    ["category"] = "slave",
                    ["price"] = Nepo.PriceForSlave(p),
                    ["age"] = WorldSafe.SafeObj(() => (object)p.ageTracker?.AgeBiologicalYears),
                    ["gender"] = WorldSafe.Safe(() => p.gender.ToString()),
                });
            }
            var d = NepoSection("slaves", all.Count, matched, rows, cap, notes,
                "Nepo/NepoGameComponent.SlaveRosterNoGenerate — a batch of generated individuals that "
                + "REROLLS DAILY, so `index` is a handle for now and not for later. Empty unless "
                + "Ideology is active, `allowSlavePurchases` is on, and the player's primary "
                + "ideoligion approves of slavery (Nepo/NepoGameComponent.RerollSlaveRoster).");
            d["blocked_reason"] = WorldSafe.Safe(() => Nepo.SlaveOffersBlockedReason(comp));
            d["index_note"] = "`index` is the order handle and it is NOT stable across a reroll "
                + "(NepoTuning.SlaveRerollIntervalTicks is one day) or across an order, because "
                + "Nepo/NepoSync.PlaceOrder removes a bought slave from the roster. Re-read before "
                + "ordering.";
            return d;
        }

        // ==================================================================
        // ------------------------ argument parsing ------------------------
        // ==================================================================

        private struct NepoLine
        {
            public string Name;
            public int Count;
            public string Stuff;
        }

        /// The plural IS the verb and the singular is its degenerate case
        /// (DESIGN §Action model), so a bare entry is lifted to a one-element
        /// list — `PawnActs.PawnList`'s own `raw as List<object> ?? new
        /// List<object> { raw }`. A bare entry of the WRONG SHAPE still lands on
        /// the per-entry check below and gets that check's sentence, which is
        /// the better one.
        private static List<object> NepoRawList(VerbArgs a, string key)
        {
            var raw = a.Raw(key);
            if (raw == null) return new List<object>();
            return raw as List<object> ?? new List<object> { raw };
        }

        private static List<NepoLine> NepoItemLines(VerbArgs a)
        {
            var outp = new List<NepoLine>();
            foreach (var o in NepoRawList(a, "items"))
            {
                if (!(o is Dictionary<string, object> row))
                    throw new VerbArgsException(
                        "every entry in 'items' must be an object like {def:\"MealSimple\", count:20}");
                NepoStrayLine("items", row, NepoItemLineKeys);
                outp.Add(new NepoLine
                {
                    Name = NepoLineStr(row, "items", "def"),
                    Count = NepoLineCount(row, "items"),
                    Stuff = row.TryGetValue("stuff", out var s) && s != null
                        ? NepoLineStr(row, "items", "stuff") : null,
                });
            }
            return outp;
        }

        private static List<NepoLine> NepoKindLines(VerbArgs a, string key)
        {
            var outp = new List<NepoLine>();
            foreach (var o in NepoRawList(a, key))
            {
                if (!(o is Dictionary<string, object> row))
                    throw new VerbArgsException("every entry in '" + key
                        + "' must be an object like {kind:\"Muffalo\", count:2}");
                NepoStrayLine(key, row, NepoKindLineKeys);
                outp.Add(new NepoLine
                {
                    Name = NepoLineStr(row, key, "kind"),
                    Count = NepoLineCount(row, key),
                });
            }
            return outp;
        }

        private static List<int> NepoSlaveIndexes(VerbArgs a)
        {
            var outp = new List<int>();
            foreach (var o in NepoRawList(a, "slaves"))
            {
                if (!(o is double n))
                    throw new VerbArgsException("every entry in 'slaves' must be a roster INDEX "
                        + "(a number from `nepo-catalog {kind:\"slaves\"}`), not "
                        + (o == null ? "null" : o.GetType().Name)
                        + ". A slave is an individual, so there is no count.");
                int i = (int)n;
                if (i != n) throw new VerbArgsException("slave index " + n + " is not a whole number");
                outp.Add(i);
            }
            return outp;
        }

        /// The stray-key rule, one level in. A line object is an argument list
        /// of its own, and `{def:"Steel", qty:20}` silently becoming a count of
        /// ONE is exactly the widening c519477 refuses at the top level.
        private static void NepoStrayLine(string key, Dictionary<string, object> row, string[] accepted)
        {
            List<string> stray = null;
            foreach (var kv in row)
                if (Array.IndexOf(accepted, kv.Key) < 0)
                    (stray ?? (stray = new List<string>())).Add(kv.Key);
            if (stray == null) return;
            stray.Sort(StringComparer.Ordinal);
            throw new VerbArgsException("unknown key" + (stray.Count > 1 ? "s" : "") + " "
                + string.Join(", ", stray.ToArray()) + " in a '" + key + "' entry — an entry accepts "
                + string.Join(", ", accepted) + ". Nothing was ordered.");
        }

        private static string NepoLineStr(Dictionary<string, object> row, string key, string field)
        {
            if (!row.TryGetValue(field, out var v) || v == null)
                throw new VerbArgsException("every entry in '" + key + "' needs a '" + field + "'");
            if (!(v is string s) || s.Length == 0)
                throw new VerbArgsException("'" + field + "' in a '" + key
                    + "' entry must be a non-empty defName string");
            return s;
        }

        /// A MISSING COUNT IS REFUSED, NEVER DEFAULTED (git-bug c519477). One is
        /// the tempting default and it is the wrong one in both directions: a
        /// dropped `count` on a bulk food order starves the colony it was meant
        /// to feed, and a dropped `count` on something expensive is the only
        /// mistake here that cannot be undone.
        private static int NepoLineCount(Dictionary<string, object> row, string key)
        {
            if (!row.TryGetValue("count", out var v) || v == null)
                throw new VerbArgsException("every entry in '" + key + "' needs a 'count'. It is not "
                    + "defaulted to 1: an order is irreversible and spends a balance, so a count this "
                    + "verb had to guess is refused instead. Nothing was ordered.");
            if (!(v is double n))
                throw new VerbArgsException("'count' in a '" + key + "' entry must be a number");
            int c = (int)n;
            if (c != n) throw new VerbArgsException("'count' " + n + " is not a whole number");
            if (c < 1) throw new VerbArgsException("'count' must be at least 1 (got " + c + ")");
            if (c > NepoMaxCount)
                throw new VerbArgsException("'count' " + c + " exceeds " + NepoMaxCount
                    + ". Nepo sums an order's cost into an int; a count large enough to overflow that "
                    + "multiply would charge a NEGATIVE price that CanAfford happily accepts.");
            return c;
        }

        private static PawnKindDef NepoKind(List<PawnKindDef> allowed, string name, string what,
            string cite, string why, string verb, object comp, out object refusal)
        {
            refusal = null;
            foreach (var k in allowed)
                if (k != null && string.Equals(k.defName, name, StringComparison.Ordinal)) return k;

            var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(name);
            refusal = NepoRefuse(verb, what + "-not-orderable", cite,
                kind == null
                    ? "no PawnKindDef named '" + name + "'. See `nepo-catalog {kind:\"" + what + "s\"}`."
                    : "'" + name + "' is not in Dad's " + what + " list"
                      + (allowed.Count == 0
                          ? ", and neither is anything else — the list is empty, which means " + why
                          : ". " + allowed.Count + " " + what + "(s) are orderable; see "
                            + "`nepo-catalog {kind:\"" + what + "s\"}`"),
                NepoMode(comp));
            return null;
        }

        // ==================================================================
        // ------------------------------ refusal ---------------------------
        // ==================================================================

        /// `TradeVerbs.TradeRefuse`'s shape, which is `e440676`'s: a named
        /// `gate`, the member it reproduces in `gate_cite`, the sentence in
        /// `reason`, and `action` saying nothing was journaled because nothing
        /// was done.
        private static Dictionary<string, object> NepoRefuse(string verb, string gate, string cite,
            string reason, Dictionary<string, object> mode, Dictionary<string, object> extra = null)
        {
            var d = new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["ok"] = false,
                ["gate"] = gate,
                ["gate_cite"] = cite,
                ["reason"] = reason,
                ["mod"] = ModName(),
                ["action"] = NoStamp(),
            };
            if (mode != null) d["mode"] = mode;
            if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
            return d;
        }
    }
}
