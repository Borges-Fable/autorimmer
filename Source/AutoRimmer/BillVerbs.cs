using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ======================================================= spec 3.6 =========
    // PRODUCTION BILLS — the standing orders a work table works from.
    //
    //   bill-options {bench, cap?}                          READ  (the Add Bill menu, as data)
    //   bill-add     {bench, recipe, …config}               the float-menu option's action
    //   bill-set     {bench, index|uid|recipe|all, …}       Dialog_BillConfig + the row's buttons
    //   bill-reorder {bench, index|uid, offset|to}          the row's up/down arrows
    //   bill-remove  {bench, index|uid|recipe|all}          the row's X
    //
    // Reading bills is 2.4's `bills`; this file only writes, and it uses 2.4's
    // field names so observe and act speak one vocabulary.
    //
    // MEDICAL BILLS ON A PAWN ARE PARTLY REACHABLE FROM HERE, and it is worth
    // stating precisely because the obvious reading is wrong. A Pawn IS in
    // `ThingRequestGroup.PotentialBillGiver` — Verse/ThingListGroupHelper.cs
    // makes that group `!def.AllRecipes.NullOrEmpty()`, and a humanlike race
    // def has surgeries in AllRecipes (3.4's own acceptance check 4.6a proves
    // it against the live bench) — so `BenchArg` resolves a pawn id, and
    // `bill-set` / `bill-reorder` / `bill-remove` gate on `IBillGiver`, whose
    // BillStack for a Pawn is `health.surgeryBills`. That is DELIBERATE and it
    // matches the widget: RimWorld/Bill.cs DoInterface draws the suspend, the
    // reorder arrows and the X in EVERY bill listing, the Health tab's
    // included. What is work-table-only is the ADD path — `bill-options` and
    // `bill-add`, whose whole gate is ITab_Bills — and the Dialog_BillConfig
    // levers, which `Configure` refuses by name on a non-Bill_Production. The
    // surgery levers stay on 3.4's `surgery-*`. See the Building_WorkTable
    // gate below and `Configure`'s `prod == null` branch, which says the same.
    //
    // Storage settings live in StorageVerbs.cs, on the same partial class, for
    // the reason the spec bundles them: a bill without a place to put its
    // output is half a verb, and both halves share `IStoreSettingsParent`.
    //
    // ================= THE GATE LIVES IN THE WIDGET ==========================
    // `RimWorld/BillStack.cs AddBill` is FOUR LINES and validates nothing:
    //     bill.billStack = this; bills.Add(bill);
    // DESIGN §Action model names it as the first worked example of the
    // invariant. Everything this file refuses, vanilla refuses in
    // `RimWorld/ITab_Bills.cs FillTab` or `RimWorld/BillStack.cs DoListing`,
    // and every refusal below cites the member it reproduces:
    //
    //   ITab_Bills.SelTable      `(Building_WorkTable)base.SelThing` — the ENTIRE
    //                            gate below exists only for a work table. See
    //                            "WHAT ABOUT OTHER IBillGivers" for what we do
    //                            with the rest.
    //   ITab_Bills.FillTab       `SelTable.def.AllRecipes` is the universe
    //   (OptionsMaker)           `recipe.AvailableNow`      (RE-DERIVED — see below)
    //                            `recipe.AvailableOnNow(SelTable)`
    //   BillStack.DoListing      `if (Count < 15)` — the Add Bill button is not
    //                            DRAWN on a full stack. The literal 15 is
    //                            hardcoded there rather than BillStack.MaxCount,
    //                            and again in FillTab's paste gate
    //                            (`SelTable.billStack.Count >= 15`). Both are 15;
    //                            MaxCount is used here because it is the same
    //                            number with a name.
    //
    // ADVISORY, NOT ENFORCED — and this distinction is the spec's, verified in
    // the source: in FillTab's `Add` delegate the mechanitor and skill branches
    // are an `if`/`else if` chain that DOES NOT RETURN, so
    // `recipe.MakeNewBill(precept)` + `AddBill` run regardless. Vanilla adds the
    // bill and shows a window. Turning either into a refusal would fabricate a
    // restriction the player does not have, so `bill-add` REPORTS them as
    // `warnings` and adds the bill.
    //
    // AND NEITHER WINDOW IS OPENED. Both are `new Dialog_MessageBox`:
    //   * `Dialog_MessageBox("RecipeRequiresMechanitor".Translate(…))` inline
    //   * `Bill.CreateNoPawnsWithSkillDialog(recipe)`, which is a bare
    //     `Find.WindowStack.Add(new Dialog_MessageBox(text))`
    // `Verse/Dialog_MessageBox.cs` sets `forcePause = true`, and per spec 1.7 a
    // force-pausing window halts EVERY later `advance` at 0 ticks with
    // reason:"dialog" until 3.5's routing ships. DESIGN's 2026-08-31 amendment
    // predicted 3.6 would inherit `CreateNoPawnsWithSkillDialog` from the
    // surgery side; it does, through `ITab_Bills`, and this is where that is
    // paid. The predicates are re-derived and returned as result fields — which
    // is better information anyway, since the agent never reads a modal.
    //
    // ALSO DROPPED, deliberately, both from FillTab's Add delegate:
    //   * `PlayerKnowledgeDatabase.KnowledgeDemonstrated(recipe.conceptLearned, …)`
    //   * `TutorSystem.Notify_Event("AddBill-" + …)`
    // Tutorial bookkeeping, per DESIGN's "reproduce the helper's gate and its
    // effect, and drop the tutorial line". Neither is game state the colony
    // depends on; `KnowledgeDemonstrated` writes the per-PROFILE knowledge DB,
    // and letting an agent's bill traffic teach the tutorial concepts would
    // change what a later human player is shown.
    //
    // ---------------- WHAT ABOUT OTHER IBillGivers ---------------------------
    // The spec scope says "any IBillGiver". `ITab_Bills.SelTable` is a hard
    // cast to `Building_WorkTable`, so for a bill giver that is not one there
    // is no ITab_Bills and therefore NO WIDGET GATE TO REPRODUCE. Resolved on
    // the issue: this verb REFUSES a non-work-table giver and names the route
    // that owns it —
    //   * a Pawn's stack is its surgery queue -> 3.4's `surgery-add`
    //   * anything else (mech gestator, subcore scanner, a modded giver with
    //     its own tab) has its own widget, and reproducing a gate we have not
    //     read would be the god-hand this invariant exists to prevent.
    // `Building_WorkTableAutonomous` DERIVES from Building_WorkTable, so
    // biofuel refineries and the like are in scope and take the same gate.
    //
    // ---------------------------- HAZARDS ------------------------------------
    //  * `RecipeDef.AvailableNow` is a WRITE-ON-READ (WorldSafe Class A): its
    //    first clause is `researchPrerequisite.IsFinished` ->
    //    `ResearchManager.GetProgress`, which does `progress.Add(proj, 0f)` on
    //    a miss into a scribed dictionary. `bill-options` asks it once per
    //    recipe on the bench, so the naive version would add an entry per
    //    research-gated recipe to the save on the first call. Every call here
    //    goes through `WorldSafe.RecipeAvailableNow`.
    //  * `Bill_Production.ShouldDoNow` writes the scribed `paused` on three
    //    paths (Class A). Never called; `WorldSafe.BillState` answers from the
    //    stored fields, exactly as 2.4's `bills` does.
    //  * `Bill.SetStoreMode`'s BASE implementation is
    //    `Log.ErrorOnce("Tried to set store mode of a non-production bill")` —
    //    a RED ERROR. Every store-mode path here type-tests `Bill_Production`
    //    first. `Bill_Production.SetStoreMode` then log-errors again on a
    //    mode/group mismatch (`storeMode == SpecificStockpile != (group != null)`)
    //    and stores the values ANYWAY, so the pairing is validated before the
    //    call, not after.
    //  * `BillUtility.GlobalBills()` can reach
    //    `Log.ErrorOnce("Found non-bill-giver tagged as PotentialBillGiver")` on
    //    a modded bench — a red error. Never called; the bill sweep here walks
    //    `ThingsInGroup(PotentialBillGiver)` with its own casts, the way
    //    `ColonyVerbs.Bills` already does.
    //  * `BillDialogUtility.GetPawnRestrictionOptionsForBill` calls
    //    `billGiver.GetWorkgiver()`, and BOTH can log-error
    //    ("Generating pawn restrictions for a BillGiver without a Workgiver",
    //    "Can't find a WorkGiver for a BillGiver"). Never called; `WorkGiverOf`
    //    below is the same walk without the two `Log.ErrorOnce` calls, and a
    //    miss degrades one clause of one gate instead of authoring a red.
    //  * `BillStack.Reorder` guards ONLY the lower bound. Moving the last bill
    //    down computes `num = Count` and `List.Insert(Count, …)` throws after
    //    the `Remove` has already happened — i.e. it loses the bill. Bounds are
    //    checked here BEFORE the call.
    //  * WRITE-ON-SAVE, and it is not in the Class A catalogue because it is a
    //    different class (DESIGN decisions log, 2026-08-31): `Bill.ExposeData`
    //    narrows a live `ingredientFilter` during the SAVING pass for any
    //    recipe with a `fixedIngredientFilter`. Consequence for acceptance —
    //    "save the game and read the Scribe XML" is NOT an independent second
    //    reader for a bill's filter, because it perturbs what it measures. The
    //    filter checks in accept/3.6-*.md use a live read plus a game-acted
    //    change instead.
    // =========================================================================
    internal static partial class BillActs
    {
        public const int OptionCap = 60;

        // --------------------------------------------------------------------
        // bill-options {bench, cap?}   READ-ONLY
        //
        // `ITab_Bills.FillTab`'s OptionsMaker as data: every recipe on the
        // bench, whether the game would DRAW the row, and the gate that hid it.
        // The discovery surface for `bill-add`, and it exists for the reason
        // `surgery-options` does — a hand-written recipe list is a guess
        // against a 38-mod bench, and AddBill accepts anything.
        // --------------------------------------------------------------------
        [Verb("bill-options")]
        public static object BillOptions(VerbContext ctx)
        {
            var map = Map();
            var bench = BenchArg(map, ctx.Args, "bench");
            int cap = ctx.Args.Int("cap", OptionCap);
            if (cap < 1 || cap > 400) throw new VerbArgsException("cap must be 1..400");
            bool onlyAddable = ctx.Args.Bool("addable_only", false);

            var table = bench as Building_WorkTable;
            if (table == null)
                return NotAWorkTable("bill-options", bench);

            var rows = new List<object>();
            int total = 0, addable = 0;
            ScanRecipes(table, (recipe, ok, gate) =>
            {
                total++;
                if (ok) addable++;
                if (onlyAddable && !ok) return;
                if (rows.Count >= cap) return;
                rows.Add(RecipeRow(table, recipe, ok, gate));
            });

            int count = table.billStack?.Count ?? 0;
            return new Dictionary<string, object>
            {
                ["verb"] = "bill-options",
                ["bench"] = bench.thingIDNumber,
                ["def"] = bench.def.defName,
                ["label"] = bench.def.label,
                ["options"] = rows,
                ["total"] = total,
                ["more"] = Math.Max(0, total - rows.Count),
                ["addable"] = addable,
                ["bills_total"] = count,
                ["bill_slots_free"] = Math.Max(0, BillStack.MaxCount - count),
                ["bill_cap"] = BillStack.MaxCount,
                ["source"] = WorldSafe.ResearchRefsOk ? "backing-field" : "unavailable",
                ["note"] = "`addable:false` rows are the ones ITab_Bills.FillTab does NOT DRAW at all — "
                    + "vanilla omits the row rather than explaining it, so `gate` and `reason` are "
                    + "MOD-AUTHORED unless the reason says GAME-AUTHORED. RecipeDef.AvailableNow is a "
                    + "bool over four clauses and each is re-asked separately so `gate` names WHICH "
                    + "(research | meme | faction-recipe-tag | ideo-precept); the bench clause is asked "
                    + "as RecipeWorker.AvailableReport rather than AvailableOnNow, which is the same "
                    + "call with a reason string an override may fill in. BillStack.AddBill would accept "
                    + "any of these rows and the bill would never be worked. Research state is read "
                    + "through WorldSafe's guarded route, never ResearchProjectDef.IsFinished (which "
                    + "writes the save).",
            };
        }

        // --------------------------------------------------------------------
        // bill-add {bench, recipe, …every bill-set lever}
        //
        // The float-menu option's action, minus its two modals and its two
        // tutorial lines. Config args are applied AFTER AddBill, through the
        // same code path `bill-set` uses — which is what a player does, in two
        // clicks, and which makes "a repeat-forever cook bill" ONE round trip
        // instead of two (DESIGN: "a verb that can only be called in a loop is
        // the defect", applied to a sequence rather than a list).
        // --------------------------------------------------------------------
        [Verb("bill-add")]
        public static object BillAdd(VerbContext ctx)
        {
            const string V = "bill-add";
            var map = Map();
            var a = ctx.Args;
            var bench = BenchArg(map, a, "bench");
            var recipe = Dev.Named<RecipeDef>(a.StrReq("recipe"), "recipe");

            var table = bench as Building_WorkTable;
            if (table == null) return NotAWorkTable(V, bench);

            var stack = table.billStack;
            if (stack == null)
                return Refused(V, bench, recipe, "gate", "this work table has no bill stack");

            // RimWorld/BillStack.cs DoListing: `if (Count < 15)` — the Add Bill
            // button is not drawn at all on a full stack. AddBill does not check.
            if (stack.Count >= BillStack.MaxCount)
                return Refused(V, bench, recipe, "bill-cap",
                    $"this bench already has {stack.Count} bills and BillStack.MaxCount is "
                    + BillStack.MaxCount + "; the game draws no Add Bill button on a full stack "
                    + "(BillStack.AddBill itself would accept it — that is the gate this verb reproduces)");

            // RimWorld/ITab_Bills.cs FillTab OptionsMaker, the three-clause row
            // filter. A recipe that fails any of them has NO ROW, so there is
            // nothing to click and nothing to add.
            string gate = RecipeGate(table, recipe, out string reason);
            if (gate != null) return Refused(V, bench, recipe, gate, reason);

            // The two ADVISORY branches, re-derived. Vanilla adds the bill and
            // raises a force-pausing Dialog_MessageBox; we add the bill and
            // return the text. See the file header.
            var warnings = AddWarnings(table, recipe);

            // EVERY CONFIG ARGUMENT, VALIDATED BEFORE THE FIRST WRITE. Config
            // is applied after AddBill (it has to be — see below), so a parse
            // that throws down there would report `bad-args` over a stack that
            // already has the bill in it, with no journal row. See
            // ValidateBillArgs.
            ValidateBillArgs(a);

            Bill bill;
            try
            {
                // RimWorld/BillUtility.cs MakeNewBill picks the Bill subclass
                // from the recipe (Uft / mech resurrection / gestation /
                // forming / plain production). Never `new Bill_Production(…)`:
                // a modded recipe with formingTicks would get the wrong class.
                bill = recipe.MakeNewBill();
                stack.AddBill(bill);
            }
            catch (Exception e)
            {
                return Refused(V, bench, recipe, "exception", e.GetType().Name + ": " + e.Message);
            }

            // Config applied AFTER AddBill: SetStoreMode's validation and
            // CanPossiblyStore both read `bill.Map`, which is
            // `billStack.billGiver.Map` and is null until the bill is in a stack.
            var changed = new List<object>();
            var refusedFields = new List<object>();
            // The bill IS in the stack from here on, so nothing below may
            // escape as an exception: `code:"exception"` would be the same
            // silent-partial-mutation report `bad-args` was, one class up. An
            // unexpected throw out of a modded ThingFilter or store-mode def
            // becomes a refusal line and the verb still journals the add.
            try { Configure(map, bill, a, changed, refusedFields); }
            catch (Exception e)
            {
                refusedFields.Add(new Dictionary<string, object>
                {
                    ["field"] = "(configure)",
                    ["gate"] = "exception",
                    ["reason"] = e.GetType().Name + ": " + e.Message
                        + " — THE BILL IS IN THE STACK. Configuration stopped where it threw; "
                        + "`configured` lists what was written before that. Fix with `bill-set`, "
                        + "or `bill-remove` and start again.",
                });
            }

            int index = stack.IndexOf(bill);
            long seq = Act(V, "add", bench.def.defName + " #" + bench.thingIDNumber + ": " + recipe.defName,
                new Dictionary<string, object>
                {
                    ["bench"] = bench.thingIDNumber,
                    ["recipe"] = recipe.defName,
                    ["uid"] = Safe(() => bill.GetUniqueLoadID()),
                    ["fields"] = changed.Count,
                });

            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["bench"] = bench.thingIDNumber,
                ["recipe"] = recipe.defName,
                ["uid"] = Safe(() => bill.GetUniqueLoadID()),
                ["index"] = index,
                ["configured"] = changed,
                ["config_refused"] = refusedFields,
                // The two windows FillTab's Add delegate would have raised,
                // re-derived. Both force-pause (spec 1.7).
                ["warnings"] = warnings,
                ["bills"] = StackLines(stack),
                ["bill_slots_free"] = Math.Max(0, BillStack.MaxCount - stack.Count),
                ["action"] = Stamp(seq),
                ["note"] = "a bill is not a job: a colonist with the bench's work type enabled picks it up "
                    + "on its next think tick (WorkGiver_DoBill), and only if the ingredients are "
                    + "reachable and unforbidden. If the bench has a CompRefuelable and is empty, "
                    + "WorkGiver_DoBill.JobOnThing yields a REFUEL job first. Advance and re-read "
                    + "`bills` — `next_ingredient_search_tick` in the future means something ran a "
                    + "failing ingredient search on it.",
            };
        }

        // --------------------------------------------------------------------
        // bill-set {bench, index|uid|recipe|all, …levers}
        //
        // Every lever `Dialog_BillConfig` draws, plus the row's suspend button,
        // over a bill SELECTOR rather than one bill — the plural form. Each
        // lever names the clause of DoWindowContents that draws it, and a lever
        // whose widget is not drawn for this recipe is REFUSED by name rather
        // than silently written: `includeTainted` on a non-apparel recipe is a
        // field the player has no control over, and writing it would be a
        // god-hand on a value the counter still reads.
        // --------------------------------------------------------------------
        [Verb("bill-set")]
        public static object BillSet(VerbContext ctx)
        {
            const string V = "bill-set";
            var map = Map();
            var a = ctx.Args;
            var bench = BenchArg(map, a, "bench");
            var stack = StackOf(bench);
            if (stack == null || stack.Count == 0)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["bench"] = bench.thingIDNumber,
                    ["reason"] = "this bill giver has no bills",
                    ["action"] = NoAction(),
                };

            var targets = SelectBills(stack, a);
            // BEFORE THE LOOP, for the same reason bill-add validates before
            // AddBill: Configure writes lever by lever, so a malformed argument
            // discovered halfway through used to leave the first bill (and,
            // under `all:true`, every bill before the throw) half-written and
            // report a clean `bad-args` with no journal row.
            ValidateBillArgs(a);

            var results = new List<object>();
            int touched = 0;
            foreach (var bill in targets)
            {
                var changed = new List<object>();
                var refusedFields = new List<object>();
                try { Configure(map, bill, a, changed, refusedFields); }
                catch (Exception e)
                {
                    refusedFields.Add(new Dictionary<string, object>
                    {
                        ["field"] = "(configure)",
                        ["gate"] = "exception",
                        ["reason"] = e.GetType().Name + ": " + e.Message
                            + " — this bill was left as `changed` reports; the rest of the selection "
                            + "was still processed.",
                    });
                }
                if (changed.Count > 0) touched++;
                results.Add(new Dictionary<string, object>
                {
                    ["index"] = stack.IndexOf(bill),
                    ["uid"] = Safe(() => bill.GetUniqueLoadID()),
                    ["recipe"] = bill.recipe?.defName,
                    ["changed"] = changed,
                    ["refused"] = refusedFields,
                });
            }

            // REACHED, not CHANGED — git-bug 4087644 comment #1: a call whose
            // every lever was refused is exactly the wasted order the agent
            // needs to find in `journal {types:["action"]}` at session end, so
            // it journals and carries the verdict. A call that never reached a
            // bill (an argument threw) owes nothing.
            long seq = targets.Count == 0
                ? 0
                : Act(V, "set", bench.def.defName + " #" + bench.thingIDNumber + " x" + touched,
                    new Dictionary<string, object>
                    {
                        ["bench"] = bench.thingIDNumber,
                        ["bills"] = targets.Count,
                        ["changed"] = touched,
                    });

            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["bench"] = bench.thingIDNumber,
                ["targets"] = results,
                ["counts"] = new Dictionary<string, object>
                {
                    ["targeted"] = targets.Count,
                    ["changed"] = touched,
                },
                ["bills"] = StackLines(stack),
                ["action"] = targets.Count == 0 ? NoAction() : Stamp(seq),
            };
        }

        // --------------------------------------------------------------------
        // bill-reorder {bench, index|uid, offset|to}
        //
        // WIDGET GATE — `RimWorld/Bill.cs DoInterface` draws the up arrow only
        // when `billStack.IndexOf(this) > 0` and the down arrow only when
        // `IndexOf(this) < billStack.Count - 1`. So the reachable destination
        // set is exactly [0, Count-1], and `BillStack.Reorder` is only ever
        // called with an offset that lands inside it.
        //
        // Reorder itself guards ONLY the lower bound (`if (num >= 0)`), so an
        // offset past the end reaches `List.Insert(Count, …)` AFTER
        // `bills.Remove(bill)` — which throws with the bill already gone. This
        // is the spec's `bad-args` acceptance bullet and it is load-bearing,
        // not decorative.
        // --------------------------------------------------------------------
        [Verb("bill-reorder")]
        public static object BillReorder(VerbContext ctx)
        {
            const string V = "bill-reorder";
            var map = Map();
            var a = ctx.Args;
            var bench = BenchArg(map, a, "bench");
            var stack = StackOf(bench);
            if (stack == null || stack.Count == 0)
                throw new VerbArgsException("this bill giver has no bills");

            var picked = SelectBills(stack, a);
            if (picked.Count != 1)
                throw new VerbArgsException(
                    $"bill-reorder moves exactly one bill; the selector matched {picked.Count}. "
                    + "Pass index or uid.");
            var bill = picked[0];

            int from = stack.IndexOf(bill);
            if (from < 0) throw new VerbArgsException("that bill is not in this stack");

            bool hasOffset = a.Has("offset"), hasTo = a.Has("to");
            if (hasOffset == hasTo)
                throw new VerbArgsException("pass exactly one of offset (relative) or to (absolute index)");
            int to = hasTo ? a.IntReq("to") : from + a.IntReq("offset");

            // The bounds the up/down arrows can reach. NOT a clamp-and-proceed:
            // silently moving a bill somewhere the caller did not ask for is
            // the failure mode `bad-args` exists to prevent.
            if (to < 0 || to > stack.Count - 1)
                throw new VerbArgsException(
                    $"destination index {to} is outside 0..{stack.Count - 1} (bill is at {from}). "
                    + "RimWorld/Bill.cs DoInterface draws the reorder arrows only inside that range, and "
                    + "RimWorld/BillStack.cs Reorder checks only the LOWER bound — past the end it "
                    + "removes the bill and then throws out of List.Insert, losing it.");
            if (to == from)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = true,
                    ["bench"] = bench.thingIDNumber,
                    ["uid"] = Safe(() => bill.GetUniqueLoadID()),
                    ["from"] = from,
                    ["to"] = to,
                    ["moved"] = false,
                    ["bills"] = StackLines(stack),
                    // Reached its target and moved nothing — journaled, per
                    // git-bug 4087644: a redundant order the ledger cannot see
                    // is a redundant order nobody learns from.
                    ["action"] = Stamp(Act(V, "reorder",
                        bench.def.defName + " #" + bench.thingIDNumber + ": already at " + from,
                        new Dictionary<string, object>
                        {
                            ["bench"] = bench.thingIDNumber,
                            ["uid"] = Safe(() => bill.GetUniqueLoadID()),
                            ["from"] = from,
                            ["to"] = to,
                            ["moved"] = false,
                        })),
                };

            stack.Reorder(bill, to - from);
            int landed = stack.IndexOf(bill);

            long seq = Act(V, "reorder",
                bench.def.defName + " #" + bench.thingIDNumber + ": " + from + " -> " + landed,
                new Dictionary<string, object>
                {
                    ["bench"] = bench.thingIDNumber,
                    ["uid"] = Safe(() => bill.GetUniqueLoadID()),
                    ["from"] = from,
                    ["to"] = landed,
                });

            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["bench"] = bench.thingIDNumber,
                ["uid"] = Safe(() => bill.GetUniqueLoadID()),
                ["recipe"] = bill.recipe?.defName,
                ["from"] = from,
                ["to"] = landed,
                ["moved"] = true,
                ["bills"] = StackLines(stack),
                ["action"] = Stamp(seq),
                ["note"] = "bill order is the order WorkGiver_DoBill considers them in "
                    + "(BillStack.FirstShouldDoNow walks the list front to back), so index 0 is worked first.",
            };
        }

        // --------------------------------------------------------------------
        // bill-remove {bench, index|uid|recipe|all}
        //
        // WIDGET GATE — `RimWorld/Bill.cs DoInterface`'s X button calls
        // `billStack.Delete(bill)`, which flags `deleted` and notifies the
        // giver. Removing from `BillStack.Bills` directly would leave a live
        // Bill pointing at a stack it is no longer in, and `Notify_BillDeleted`
        // is where a work table drops its unfinished thing.
        // --------------------------------------------------------------------
        [Verb("bill-remove")]
        public static object BillRemove(VerbContext ctx)
        {
            const string V = "bill-remove";
            var map = Map();
            var a = ctx.Args;
            var bench = BenchArg(map, a, "bench");
            var stack = StackOf(bench);
            if (stack == null || stack.Count == 0)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["bench"] = bench.thingIDNumber,
                    ["reason"] = "this bill giver has no bills",
                    ["action"] = NoAction(),
                };

            var doomed = SelectBills(stack, a);
            var removed = new List<object>();
            foreach (var b in doomed)
            {
                var line = new Dictionary<string, object>
                {
                    ["uid"] = Safe(() => b.GetUniqueLoadID()),
                    ["recipe"] = b.recipe?.defName,
                    ["label"] = Safe(() => b.LabelCap),
                    ["index"] = stack.IndexOf(b),
                };
                stack.Delete(b);
                removed.Add(line);
            }

            long seq = removed.Count == 0
                ? 0
                : Act(V, "remove", bench.def.defName + " #" + bench.thingIDNumber + " x" + removed.Count,
                    new Dictionary<string, object>
                    {
                        ["bench"] = bench.thingIDNumber,
                        ["removed"] = removed.Count,
                    });

            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["bench"] = bench.thingIDNumber,
                ["removed"] = removed,
                ["bills"] = StackLines(stack),
                ["bill_slots_free"] = Math.Max(0, BillStack.MaxCount - stack.Count),
                ["action"] = removed.Count == 0 ? NoAction() : Stamp(seq),
            };
        }

        // ======================= the recipe gate =============================

        // RimWorld/ITab_Bills.cs FillTab's OptionsMaker, reproduced. Returns
        // null when the game would DRAW the row, else a gate name; `reason` is
        // mod-authored prose, and says so, because vanilla authors NO STRING
        // here — it omits the row (git-bug 48f666c comment #2, correction 1).
        private static string RecipeGate(Building_WorkTable table, RecipeDef recipe, out string reason)
        {
            reason = null;
            List<RecipeDef> all;
            try { all = table.def.AllRecipes; } catch { all = null; }
            if (all == null || !all.Contains(recipe))
            {
                reason = $"'{recipe.defName}' is not in {table.def.defName}.AllRecipes — this bench cannot "
                    + "make it at all. MOD-AUTHORED: vanilla's ITab_Bills.FillTab simply does not draw the "
                    + "row. Call `bill-options` for what this bench does offer.";
                return "not-on-bench";
            }

            // RE-DERIVED, never RecipeDef.AvailableNow — that reads
            // ResearchProjectDef.IsFinished, which INSERTS into a scribed
            // dictionary (WorldSafe Class A).
            if (!WorldSafe.RecipeAvailableNow(recipe))
            {
                // AvailableNow is ONE bool over FOUR unrelated conditions, and
                // a single "ideo-or-faction" gate with a sentence listing all
                // three possibilities tells the agent nothing it could act on.
                // Each clause is therefore asked again, separately, in
                // AvailableNow's own short-circuit order, and named — the same
                // treatment the research clause already got, and for the same
                // reason: ResearchProjectDef.LabelCap, MemeDef.LabelCap and
                // FactionDef.LabelCap all exist.
                var missing = MissingResearch(recipe);
                if (missing.Count > 0)
                {
                    reason = "research not finished: " + string.Join(", ", missing.ToArray())
                        + ". MOD-AUTHORED: RecipeDef.AvailableNow is a bool and vanilla omits the row "
                        + "rather than explaining it; the project label is ours, read through "
                        + "WorldSafe's guarded route.";
                    return "research";
                }
                string ideoGate = WorldSafe.RecipeIdeoBlock(recipe, out string ideoWhy);
                if (ideoGate != null) { reason = ideoWhy; return ideoGate; }
                // Every clause we know about says yes and AvailableNow still
                // says no — a modded RecipeDef override, or a clause added by a
                // game update. Said plainly rather than blamed on ideology.
                reason = "RecipeDef.AvailableNow is false, but none of its four clauses (research, "
                    + "memePrerequisitesAny, factionPrerequisiteTags, fromIdeoBuildingPreceptOnly) "
                    + "reports as the blocker when asked individually — so something overrides or "
                    + "extends it on this def. MOD-AUTHORED: vanilla omits the row and authors no string.";
                return "available-now";
            }

            // THE REPORT, NOT THE BOOL. `RecipeDef.AvailableOnNow` is
            // `Worker.AvailableOnNow(thing, part)`; `RecipeWorker.AvailableReport`'s
            // base body is literally `return AvailableOnNow(thing, part);`
            // (Verse/RecipeWorker.cs), so this is the SAME question with the
            // same answer — except that an override may return a real string
            // instead of `false`, and AcceptanceReport's implicit bool operator
            // makes the base case free. No vanilla PRODUCTION worker overrides
            // it (only Recipe_ExtractOvum and Recipe_ExtractHemogen do, both
            // surgery), so this harvests nothing today and harvests a modded
            // worker's OWN WORDS the moment one exists. 3.4's `surgery-options`
            // already asks the report; asking the bool here made the two halves
            // of one codebase ask different members the same question.
            AcceptanceReport report;
            try { report = recipe.Worker.AvailableReport(table); }
            catch (Exception e)
            {
                reason = "RecipeWorker.AvailableReport threw on this bench: " + e.Message;
                return "available-on-now";
            }
            if (!report.Accepted)
            {
                string words = string.IsNullOrEmpty(report.Reason) ? null : report.Reason;
                reason = words != null
                    ? "the recipe worker refuses this bench: " + words
                        + " — GAME-AUTHORED, verbatim from "
                        + recipe.Worker.GetType().Name + ".AvailableReport."
                    : $"RecipeDef.AvailableOnNow('{table.def.defName}') is false — the recipe worker "
                        + "refuses this bench in its current state and its AvailableReport supplied no "
                        + "reason string (the base body is `return AvailableOnNow(thing, part)`, a bool). "
                        + "MOD-AUTHORED: vanilla omits the row.";
                return "available-on-now";
            }
            return null;
        }

        private static List<string> MissingResearch(RecipeDef recipe)
        {
            var missing = new List<string>();
            try
            {
                if (recipe.researchPrerequisite != null && !WorldSafe.Finished(recipe.researchPrerequisite))
                    missing.Add(recipe.researchPrerequisite.LabelCap);
                if (recipe.researchPrerequisites != null)
                    for (int i = 0; i < recipe.researchPrerequisites.Count; i++)
                        if (!WorldSafe.Finished(recipe.researchPrerequisites[i]))
                            missing.Add(recipe.researchPrerequisites[i].LabelCap);
            }
            catch { }
            return missing;
        }

        // `def.AllRecipes` in FillTab's own order, with the gate verdict per
        // recipe. The ideo-precept variants FillTab adds a SECOND option for
        // (`Precept_Building.ThingDef == recipe.ProducedThingDef`) are not
        // enumerated: they are the same recipe with a style precept attached,
        // and style is a v1 non-goal.
        private static void ScanRecipes(Building_WorkTable table, Action<RecipeDef, bool, string> sink)
        {
            List<RecipeDef> all;
            try { all = new List<RecipeDef>(table.def.AllRecipes); } catch { return; }
            for (int i = 0; i < all.Count; i++)
            {
                var recipe = all[i];
                if (recipe == null) continue;
                try
                {
                    string gate = RecipeGate(table, recipe, out string reason);
                    sink(recipe, gate == null, gate == null ? null : gate + ": " + reason);
                }
                catch (Exception e)
                {
                    Journal.EmitWarning("bill-options: a recipe gate threw: " + e.Message);
                }
            }
        }

        private static Dictionary<string, object> RecipeRow(Building_WorkTable table, RecipeDef recipe,
            bool addable, string gate)
        {
            var d = new Dictionary<string, object>
            {
                ["recipe"] = recipe.defName,
                ["label"] = Safe(() => recipe.LabelCap.Resolve()),
                ["addable"] = addable,
                ["reason"] = gate,
                ["work_amount"] = SafeObj(() => (object)WorldSafe.R(recipe.WorkAmountTotal(null), 0)),
                ["work_skill"] = recipe.workSkill?.defName,
                ["produces"] = Safe(() => recipe.ProducedThingDef?.defName),
                // The counter decides whether TargetCount is even offerable —
                // BillRepeatModeUtility.MakeConfigFloatMenu refuses the mode
                // with "RecipeCannotHaveTargetCount" when this is false.
                //
                // NOT asked with `new Bill_Production(recipe)`: the Bill ctor
                // ends in InitializeAfterClone(), which burns a
                // UniqueIDsManager.GetNextBillID() — a SCRIBED counter — so a
                // read-only verb would consume one id per recipe per call. Same
                // hazard shape as the nextJobID burn DESIGN records for
                // `orders`, except here it is entirely avoidable.
                ["can_target_count"] = CanCountProducts(recipe),
            };
            if (recipe.skillRequirements != null && recipe.skillRequirements.Count > 0)
                d["min_skill"] = Safe(() => recipe.MinSkillString);
            if (ModsConfig.BiotechActive && recipe.mechanitorOnlyRecipe) d["mechanitor_only"] = true;
            return d;
        }

        // `RecipeWorkerCounter.CanCountProducts` without constructing a Bill.
        // All three vanilla counters ignore the argument entirely — the base is
        // `specialProducts == null && products != null && products.Count == 1`,
        // and RecipeWorkerCounter_ButcherAnimals / _MakeStoneBlocks both
        // `return true` — so a null is safe there and honours a modded override
        // that behaves the same way. A modded counter that DEREFERENCES the
        // bill throws into the catch and gets the base predicate, which is
        // strictly better than burning a scribed bill id per recipe to ask.
        private static object CanCountProducts(RecipeDef recipe)
        {
            try { return recipe.WorkerCounter.CanCountProducts(null); }
            catch { }
            try { return recipe.specialProducts == null && recipe.products != null && recipe.products.Count == 1; }
            catch { return null; }
        }

        // The two branches of FillTab's `Add` delegate that raise a
        // Dialog_MessageBox. Neither blocks the add in vanilla and neither
        // blocks it here; both force-pause, so neither window is opened.
        private static List<object> AddWarnings(Building_WorkTable table, RecipeDef recipe)
        {
            var list = new List<object>();
            var map = table.Map;
            if (map == null) return list;

            // MapPawns.FreeColonists is FreeHumanlikesOfFaction, which CLEARS
            // and refills a cached per-faction list on every read (WorldSafe
            // Class E). Snapshot before the predicate loop.
            List<Pawn> free;
            try { free = new List<Pawn>(map.mapPawns.FreeColonists); } catch { return list; }

            try
            {
                if (ModsConfig.BiotechActive && recipe.mechanitorOnlyRecipe)
                {
                    bool any = false;
                    for (int i = 0; i < free.Count; i++)
                        if (MechanitorUtility.IsMechanitor(free[i])) { any = true; break; }
                    if (!any)
                    {
                        list.Add(new Dictionary<string, object>
                        {
                            ["key"] = "requires-mechanitor",
                            ["text"] = "this recipe is mechanitor-only and no free colonist is a mechanitor",
                            ["game_string"] = Safe(() => (string)"RecipeRequiresMechanitor".Translate(recipe.LabelCap)),
                            ["note"] = "vanilla ADDS THE BILL ANYWAY and raises a force-pausing "
                                + "Dialog_MessageBox; returned here instead. The bill is in the stack.",
                        });
                        // THE RETURN BELONGS INSIDE THIS BRANCH, and getting it
                        // wrong is a silent under-report. FillTab's chain is
                        //   if (Biotech && mechanitorOnlyRecipe && !Any(IsMechanitor)) …
                        //   else if (!Any(satisfies skill)) …
                        // — the mechanitor CONDITION includes `!Any`, so the
                        // else-if is only skipped when the mechanitor branch
                        // actually FIRED. A mechanitor-only recipe with a
                        // mechanitor present falls THROUGH to the skill test,
                        // and vanilla shows the skill dialog. Returning on the
                        // recipe flag alone reported nothing there.
                        return list;
                    }
                }

                bool anySkilled = false;
                for (int i = 0; i < free.Count; i++)
                {
                    try { if (recipe.PawnSatisfiesSkillRequirements(free[i])) { anySkilled = true; break; } }
                    catch { }
                }
                if (!anySkilled)
                    list.Add(new Dictionary<string, object>
                    {
                        ["key"] = "no-pawn-with-skill",
                        ["text"] = "no free colonist meets this recipe's skill requirement",
                        ["min_skill"] = Safe(() => recipe.MinSkillString),
                        ["note"] = "vanilla ADDS THE BILL ANYWAY and calls Bill.CreateNoPawnsWithSkillDialog, "
                            + "a bare Find.WindowStack.Add(new Dialog_MessageBox(…)) which force-pauses and "
                            + "would halt every later `advance` at 0 ticks (spec 1.7). The bill is in the "
                            + "stack; nobody will work it until someone has the skill.",
                    });
            }
            catch { }
            return list;
        }

        // ======================== the config levers ==========================

        // Every lever `RimWorld/Dialog_BillConfig.cs DoWindowContents` draws,
        // plus the suspend button from `RimWorld/Bill.cs DoInterface`. A lever
        // whose widget is not drawn for this bill is refused BY NAME.
        private static void Configure(Map map, Bill bill, VerbArgs a,
            List<object> changed, List<object> refused)
        {
            void Note(string field, object value)
                => changed.Add(new Dictionary<string, object> { ["field"] = field, ["value"] = value });
            void No(string field, string gate, string why)
                => refused.Add(new Dictionary<string, object>
                {
                    ["field"] = field,
                    ["gate"] = gate,
                    ["reason"] = why,
                });

            var prod = bill as Bill_Production;

            // ---- suspended --------------------------------------------------
            // RimWorld/Bill.cs DoInterface's suspend button is unconditional
            // (`suspended = !suspended`) and is drawn in EVERY bill listing —
            // the work table's and the Health tab's alike. So this is the one
            // lever that applies to a Bill_Medical too. `suspended` is scribed
            // and is NOT `paused`: 2.4 publishes both and so do we.
            if (a.Has("suspended"))
            {
                bool v = a.Bool("suspended", false);
                bill.suspended = v;
                Note("suspended", v);
            }

            // EVERYTHING ELSE IS Dialog_BillConfig, and only a Bill_Production
            // has one — `Bill_Production.GetBillDialog()` is where it comes
            // from, and `Bill_Medical` neither overrides it nor draws any
            // config interface beyond the info-card button. So a medical bill
            // reaches this file for suspend/reorder/remove (the row buttons,
            // which the Health tab does draw) and nothing else; the surgery
            // levers stay on 3.4's `surgery-*`.
            if (prod == null)
            {
                foreach (var key in ProductionOnly)
                    if (a.Has(key))
                        No(key, "not-production",
                            "this is a " + bill.GetType().Name + ", not a Bill_Production; the control is "
                            + "in Dialog_BillConfig, which only Bill_Production.GetBillDialog() opens. "
                            + (bill is Bill_Medical
                                ? "A surgery bill's own levers are spec 3.4's `surgery-add` / `surgery-remove`."
                                : ""));
                return;
            }

            // ---- ingredient search radius -----------------------------------
            // Dialog_BillConfig.DoIngredientConfigPane: a slider over 3..100
            // whose value is snapped to 999 ("unlimited") at >= 100. The
            // reachable domain is therefore [3,100) plus 999, and any number
            // >= 100 means unlimited — reproduced exactly rather than clamped
            // to 100, because 100 in the widget IS 999.
            if (a.Has("ingredient_radius"))
            {
                float r = ParseRadius(a);
                bill.ingredientSearchRadius = r;
                Note("ingredient_radius", r >= 999f ? (object)"unlimited" : (object)WorldSafe.R(r, 0));
            }

            // ---- worker restriction -----------------------------------------
            // Dialog_BillConfig.GeneratePawnRestrictionOptions. The five setters
            // are mutually exclusive by construction (each clears the other
            // three), so this is one arg, not four booleans.
            if (a.Has("worker")) SetWorker(map, bill, a.StrReq("worker"), Note, No);

            // ---- allowed skill range ----------------------------------------
            // Dialog_BillConfig draws the IntRange ONLY when
            // `PawnRestriction == null && recipe.workSkill != null && !MechsOnly`,
            // over 0..20. Evaluated AFTER `worker` above, so setting both in one
            // call reads the same order the player's two clicks would.
            if (a.Has("skill_range"))
            {
                var pair = ParseSkillRange(a);
                int lo = pair[0], hi = pair[1];
                if (bill.PawnRestriction != null)
                    No("skill_range", "pawn-restricted",
                        "Dialog_BillConfig draws the skill range only when PawnRestriction is null — a bill "
                        + "pinned to one pawn has no range to draw");
                else if (bill.recipe?.workSkill == null)
                    No("skill_range", "no-work-skill",
                        "this recipe has no workSkill, so the game draws no skill range");
                else if (bill.MechsOnly)
                    No("skill_range", "mechs-only",
                        "Dialog_BillConfig hides the skill range for a mechs-only bill "
                        + "(a mech's level is RaceProps.mechFixedSkillLevel)");
                else
                {
                    bill.allowedSkillRange = new IntRange(lo, hi);
                    Note("skill_range", new List<object> { lo, hi });
                }
            }

            // ---- the custom name --------------------------------------------
            // Bill_Production is IRenameable; Dialog_BillConfig's rename button
            // opens Dialog_RenameBill, which writes RenamableLabel. This is the
            // stable HUMAN handle for a bill; `uid` is the stable PROGRAM handle
            // (see the open-question resolution on the issue).
            if (a.Has("name"))
            {
                string name = ParseName(a);
                prod.RenamableLabel = string.IsNullOrEmpty(name) ? null : name;
                Note("name", name);
            }

            ConfigureProduction(map, prod, a, Note, No);
        }

        // Every lever that lives in Dialog_BillConfig rather than on the bill
        // ROW — refused by name on a non-production bill instead of written
        // behind the game's back.
        private static readonly string[] ProductionOnly =
        {
            "repeat", "count", "target", "pause_when_satisfied", "unpause_when_you_have",
            "include_equipped", "include_tainted", "include_from", "limit_to_allowed_stuff",
            "hp_range", "quality_range", "store_mode", "store_target", "filter", "allow",
            "disallow", "special", "ingredient_radius", "worker", "skill_range", "name",
        };

        private static void ConfigureProduction(Map map, Bill_Production bill, VerbArgs a,
            Action<string, object> Note, Action<string, string, string> No)
        {
            // ---- repeat mode -------------------------------------------------
            // RimWorld/BillRepeatModeUtility.cs MakeConfigFloatMenu: three
            // options, and the TargetCount one is the only gated one — it
            // refuses with the game's own "RecipeCannotHaveTargetCount" message
            // when `!recipe.WorkerCounter.CanCountProducts(bill)` and leaves the
            // mode alone. That is a GAME-AUTHORED string and is quoted as one.
            if (a.Has("repeat"))
            {
                string want = a.StrReq("repeat");
                var mode = RepeatMode(want);
                if (mode == BillRepeatModeDefOf.TargetCount)
                {
                    bool canCount = false;
                    try { canCount = bill.recipe.WorkerCounter.CanCountProducts(bill); } catch { }
                    if (!canCount)
                        No("repeat", "cannot-count-products",
                            Safe(() => (string)"RecipeCannotHaveTargetCount".Translate())
                            ?? "this recipe's products cannot be counted, so 'do until you have X' is not "
                               + "offered (RecipeWorkerCounter.CanCountProducts is false)");
                    else { bill.repeatMode = mode; Note("repeat", mode.defName); }
                }
                else { bill.repeatMode = mode; Note("repeat", mode.defName); }
            }

            // ---- repeat count ------------------------------------------------
            // Dialog_BillConfig draws the IntEntry only under RepeatCount, and
            // its minus button floors at 0 (`Mathf.Max(0, …)`).
            if (a.Has("count"))
            {
                int n = a.IntReq("count");
                if (n < 0) throw new VerbArgsException("count must be >= 0");
                if (bill.repeatMode != BillRepeatModeDefOf.RepeatCount)
                    No("count", "wrong-repeat-mode",
                        "the repeat-count entry is drawn only under repeat:\"RepeatCount\" (this bill is "
                        + (bill.repeatMode?.defName ?? "null") + "); set `repeat` in the same call");
                else { bill.repeatCount = n; Note("count", n); }
            }

            // ---- target count ------------------------------------------------
            // THE WIDGET'S SIDE EFFECT IS PART OF THE CLICK. Dialog_BillConfig
            // does, immediately after the IntEntry:
            //     bill.unpauseWhenYouHave = Mathf.Max(0,
            //         bill.unpauseWhenYouHave + (bill.targetCount - oldTargetCount));
            // i.e. the unpause threshold TRACKS the target. A verb that writes
            // only `targetCount` leaves a stale threshold and the bill unpauses
            // at a number the player never chose — the same failure shape as
            // the work-priorities cache (DESIGN, 2026-08-31: "a verb that
            // reproduces only the gate is half a verb").
            if (a.Has("target"))
            {
                int n = a.IntReq("target");
                if (n < 0) throw new VerbArgsException("target must be >= 0");
                if (bill.repeatMode != BillRepeatModeDefOf.TargetCount)
                    No("target", "wrong-repeat-mode",
                        "the target-count entry is drawn only under repeat:\"TargetCount\" (this bill is "
                        + (bill.repeatMode?.defName ?? "null") + "); set `repeat` in the same call");
                else
                {
                    int old = bill.targetCount;
                    bill.targetCount = n;
                    bill.unpauseWhenYouHave = Math.Max(0, bill.unpauseWhenYouHave + (n - old));
                    Note("target", n);
                    Note("unpause_when_you_have", bill.unpauseWhenYouHave);
                }
            }

            // ---- pause when satisfied ----------------------------------------
            if (a.Has("pause_when_satisfied"))
            {
                bool v = a.Bool("pause_when_satisfied", false);
                if (bill.repeatMode != BillRepeatModeDefOf.TargetCount)
                    No("pause_when_satisfied", "wrong-repeat-mode",
                        "the checkbox is drawn only under repeat:\"TargetCount\"");
                else { bill.pauseWhenSatisfied = v; Note("pause_when_satisfied", v); }
            }

            // ---- unpause threshold -------------------------------------------
            // Drawn only under TargetCount AND pauseWhenSatisfied, and the
            // widget itself clamps: `if (unpauseWhenYouHave >= targetCount)
            // unpauseWhenYouHave = targetCount - 1`.
            if (a.Has("unpause_when_you_have"))
            {
                int n = a.IntReq("unpause_when_you_have");
                if (n < 0) throw new VerbArgsException("unpause_when_you_have must be >= 0");
                if (bill.repeatMode != BillRepeatModeDefOf.TargetCount)
                    No("unpause_when_you_have", "wrong-repeat-mode",
                        "the entry is drawn only under repeat:\"TargetCount\"");
                else if (!bill.pauseWhenSatisfied)
                    No("unpause_when_you_have", "pause-off",
                        "the entry is drawn only while pause_when_satisfied is on");
                else
                {
                    bill.unpauseWhenYouHave = n >= bill.targetCount ? Math.Max(0, bill.targetCount - 1) : n;
                    Note("unpause_when_you_have", bill.unpauseWhenYouHave);
                }
            }

            // ---- the four product-shaped toggles ------------------------------
            // Each is drawn only under TargetCount, only when
            // `recipe.ProducedThingDef != null`, and only under its own clause.
            // A field the player cannot reach is still READ by
            // RecipeWorkerCounter.CountValidThing, so writing one behind the
            // game's back changes what "currently have" means.
            var produced = SafeDef(() => bill.recipe?.ProducedThingDef);
            bool target = bill.repeatMode == BillRepeatModeDefOf.TargetCount;

            if (a.Has("include_equipped"))
            {
                bool v = a.Bool("include_equipped", false);
                if (!target || produced == null)
                    No("include_equipped", "wrong-repeat-mode", TargetOnly(produced));
                else if (!produced.IsWeapon && !produced.IsApparel)
                    No("include_equipped", "not-weapon-or-apparel",
                        "Dialog_BillConfig draws this only when the product is a weapon or apparel");
                else { bill.includeEquipped = v; Note("include_equipped", v); }
            }
            if (a.Has("include_tainted"))
            {
                bool v = a.Bool("include_tainted", false);
                if (!target || produced == null)
                    No("include_tainted", "wrong-repeat-mode", TargetOnly(produced));
                else if (!produced.IsApparel || produced.apparel == null || !produced.apparel.careIfWornByCorpse)
                    No("include_tainted", "not-tainting-apparel",
                        "drawn only when the product is apparel with apparel.careIfWornByCorpse");
                else { bill.includeTainted = v; Note("include_tainted", v); }
            }
            // ---- "Include from" ----------------------------------------------
            // Dialog_BillConfig.DoWindowContents draws a ButtonText between
            // IncludeTainted and the hit-point slider, under the SAME two
            // conditions as the toggles either side of it (TargetCount and
            // `producedThingDef != null`). Its float menu is
            //     "IncludeFromAll"                     -> SetIncludeGroup(null)
            //     FillOutputDropdownOptions(…, slot => SetIncludeGroup(slot))
            // — the very same helper the store-mode dropdown uses, so the four
            // gates are identical and ResolveOutputGroup answers both.
            //
            // `includeGroup` is scribed (Bill_Production.ExposeData does
            // SaveSlotReferencable/LoadSlotReferencable on it) and is read by
            // RecipeWorkerCounter.CountProducts, so it decides what "currently
            // have" MEANS for a TargetCount bill. Leaving it out was the one
            // Dialog_BillConfig lever this file neither implemented nor refused
            // by name — silence, where every other unimplemented control is
            // named. Implemented rather than refused, because the resolution it
            // needs was already written for store_target.
            if (a.Has("include_from"))
            {
                object raw = ParseIncludeFrom(a);
                if (!target || produced == null)
                    No("include_from", "wrong-repeat-mode", TargetOnly(produced));
                else if (raw == null)
                {
                    // The "IncludeFromAll" option: an explicit null, and the
                    // dropdown's default reading.
                    bill.SetIncludeGroup(null);
                    Note("include_from", "all");
                }
                else if (ResolveOutputGroup(map, bill, raw, "include_from", No, out var group))
                {
                    bill.SetIncludeGroup(group);
                    Note("include_from", new Dictionary<string, object>
                    {
                        ["group"] = Safe(() => SlotGroup.GetGroupLabel(group)),
                        ["note"] = "RecipeWorkerCounter.CountProducts now counts only what is in this "
                            + "storage, so the TargetCount bill's \"currently have\" is scoped to it.",
                    });
                }
            }

            if (a.Has("limit_to_allowed_stuff"))
            {
                bool v = a.Bool("limit_to_allowed_stuff", false);
                if (!target || produced == null)
                    No("limit_to_allowed_stuff", "wrong-repeat-mode", TargetOnly(produced));
                else if (!produced.MadeFromStuff)
                    No("limit_to_allowed_stuff", "not-stuff-based",
                        "drawn only when the product is MadeFromStuff");
                else { bill.limitToAllowedStuff = v; Note("limit_to_allowed_stuff", v); }
            }
            if (a.Has("hp_range"))
            {
                var pair = Pct01(a, "hp_range");
                bool anyHp = false;
                try
                {
                    if (bill.recipe.products != null)
                        foreach (var p in bill.recipe.products)
                            if (p?.thingDef != null && p.thingDef.useHitPoints) { anyHp = true; break; }
                }
                catch { }
                if (!target || produced == null)
                    No("hp_range", "wrong-repeat-mode", TargetOnly(produced));
                else if (!anyHp)
                    No("hp_range", "no-hit-points",
                        "drawn only when a product has useHitPoints");
                else
                {
                    // The widget rounds each end to 1/100 after the slider.
                    bill.hpRange = new FloatRange(
                        (float)Math.Round(pair[0] * 100f) / 100f,
                        (float)Math.Round(pair[1] * 100f) / 100f);
                    Note("hp_range", new List<object> { WorldSafe.Pct(bill.hpRange.min), WorldSafe.Pct(bill.hpRange.max) });
                }
            }
            if (a.Has("quality_range"))
            {
                var qr = ParseQualityRange(a, "quality_range");
                QualityCategory lo = qr.min, hi = qr.max;
                bool hasQuality = false;
                try { hasQuality = produced != null && produced.HasComp(typeof(CompQuality)); } catch { }
                if (!target || produced == null)
                    No("quality_range", "wrong-repeat-mode", TargetOnly(produced));
                else if (!hasQuality)
                    No("quality_range", "no-quality",
                        "drawn only when the product HasComp(CompQuality)");
                else { bill.qualityRange = new QualityRange(lo, hi); Note("quality_range", lo + ".." + hi); }
            }

            // ---- store mode ---------------------------------------------------
            if (a.Has("store_mode") || a.Has("store_target")) SetStoreMode(map, bill, a, Note, No);

            // ---- ingredient filter --------------------------------------------
            if (a.Has("filter") || a.Has("allow") || a.Has("disallow") || a.Has("special"))
                SetIngredientFilter(bill, a, Note, No);
        }

        private static string TargetOnly(ThingDef produced)
            => produced == null
                ? "this recipe has no single ProducedThingDef, so Dialog_BillConfig draws none of the "
                  + "product-shaped controls"
                : "Dialog_BillConfig draws this only under repeat:\"TargetCount\"; set `repeat` in the same call";

        private static BillRepeatModeDef RepeatMode(string s)
        {
            if (s == null) throw new VerbArgsException("repeat must be a string");
            if (string.Equals(s, "forever", StringComparison.OrdinalIgnoreCase)) return BillRepeatModeDefOf.Forever;
            if (string.Equals(s, "count", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "RepeatCount", StringComparison.OrdinalIgnoreCase))
                return BillRepeatModeDefOf.RepeatCount;
            if (string.Equals(s, "target", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "TargetCount", StringComparison.OrdinalIgnoreCase))
                return BillRepeatModeDefOf.TargetCount;
            var def = DefDatabase<BillRepeatModeDef>.GetNamedSilentFail(s);
            if (def != null) return def;
            throw new VerbArgsException("repeat must be forever|count|target (or a BillRepeatModeDef defName)");
        }

        // ===================== THE ARGUMENT PRE-PASS =========================
        //
        // EVERY argument the config path will parse, validated BEFORE the first
        // write. This is a correctness fix, not tidiness.
        //
        // `VerbRegistry.Execute` maps `VerbArgsException` to
        // `{ok:false, error:{code:"bad-args"}}` with NO indication that
        // anything changed, and both write paths used to parse arguments AFTER
        // they had already mutated:
        //   * `bill-add` ran `stack.AddBill(bill)` and THEN `Configure`, so a
        //     bad `repeat` word, a negative `count`, a malformed `skill_range`
        //     — any of a dozen — reported a clean rejection with the bill
        //     sitting in the stack. The agent reads `bad-args`, retries, and
        //     now has TWO BILLS.
        //   * `bill-set` reached the first bill, wrote `suspended`, and threw
        //     on the next lever — leaving that bill half-configured.
        // In both cases `Act(...)` is never reached, so there is no journal
        // row at all: an unprovenanced state change, which is exactly what
        // Stamp/NoAction exist to prevent and what git-bug 4087644's journal
        // rule forbids. `storage-set` had the same shape and takes the same
        // treatment (ValidateStorageArgs).
        //
        // THE SPLIT IS THE FILE'S OWN LINE, and it is why this is not simply
        // "turn the throws into No(...)": a MALFORMED ARGUMENT is the caller's
        // error, is `bad-args`, and belongs here, before anything moves. A GATE
        // ("Dialog_BillConfig does not draw this control for this bill") is the
        // game's answer, is per-bill state, and stays a `No(field, gate,
        // reason)` refusal inside Configure with the verb still succeeding.
        // Collapsing the first into the second would return `ok:true` for a
        // typo, which is the failure this whole file is written against.
        //
        // Nothing below touches game state — def lookups and enum parses only —
        // so it is safe to run against a bill that does not exist yet.
        private static void ValidateBillArgs(VerbArgs a)
        {
            if (a.Has("suspended")) a.Bool("suspended", false);
            if (a.Has("ingredient_radius")) ParseRadius(a);
            if (a.Has("worker"))
            {
                string want = a.StrReq("worker");
                if (!IsWorkerKeyword(want)) ParsePawnId(want);
            }
            if (a.Has("skill_range")) ParseSkillRange(a);
            if (a.Has("name")) ParseName(a);

            if (a.Has("repeat")) RepeatMode(a.StrReq("repeat"));
            if (a.Has("count") && a.IntReq("count") < 0)
                throw new VerbArgsException("count must be >= 0");
            if (a.Has("target") && a.IntReq("target") < 0)
                throw new VerbArgsException("target must be >= 0");
            if (a.Has("unpause_when_you_have") && a.IntReq("unpause_when_you_have") < 0)
                throw new VerbArgsException("unpause_when_you_have must be >= 0");
            if (a.Has("pause_when_satisfied")) a.Bool("pause_when_satisfied", false);
            if (a.Has("include_equipped")) a.Bool("include_equipped", false);
            if (a.Has("include_tainted")) a.Bool("include_tainted", false);
            if (a.Has("limit_to_allowed_stuff")) a.Bool("limit_to_allowed_stuff", false);
            if (a.Has("hp_range")) Pct01(a, "hp_range");
            if (a.Has("quality_range")) ParseQualityRange(a, "quality_range");
            if (a.Has("store_mode")) ParseStoreMode(a.Str("store_mode"));
            if (a.Has("include_from")) ParseIncludeFrom(a);

            StorageFilterOps.Validate(a);
        }

        // The same pre-pass for the storage side. `ParseStoragePriority`,
        // `Pct01`, `ParseQualityRange` and the filter word check all used to
        // throw INSIDE `storage-set`'s per-target loop, after `copy_from` and
        // `priority` had already been written to that target — and, with
        // `targets` plural, after earlier targets had been written in full.
        // `copy_from`'s own resolution stays a refusal: it is a LOOKUP against
        // live map state, not a parse, and it already reports as one.
        private static void ValidateStorageArgs(VerbArgs a)
        {
            if (a.Has("priority")) ParseStoragePriority(a.Str("priority"));
            if (a.Has("hp_range")) Pct01(a, "hp_range");
            if (a.Has("quality_range")) ParseQualityRange(a, "quality_range");
            StorageFilterOps.Validate(a);
        }

        // Dialog_BillConfig.DoIngredientConfigPane: a slider over 3..100 whose
        // value snaps to 999 ("unlimited") at >= 100. The reachable domain is
        // [3,100) plus 999, and any number >= 100 means unlimited — reproduced
        // exactly rather than clamped to 100, because 100 in the widget IS 999.
        private static float ParseRadius(VerbArgs a)
        {
            object raw = a.Raw("ingredient_radius");
            if (raw is string s && string.Equals(s, "unlimited", StringComparison.OrdinalIgnoreCase))
                return 999f;
            float r = (float)a.Num("ingredient_radius", 999);
            if (r < 3f)
                throw new VerbArgsException(
                    "ingredient_radius must be >= 3 or \"unlimited\" "
                    + "(Dialog_BillConfig.DoIngredientConfigPane's slider is 3..100, and >= 100 snaps to 999)");
            return r >= 100f ? 999f : r;
        }

        private static int[] ParseSkillRange(VerbArgs a)
        {
            var range = a.Raw("skill_range") as List<object>;
            if (range == null || range.Count != 2 || !(range[0] is double) || !(range[1] is double))
                throw new VerbArgsException("skill_range must be [min,max], each 0..20");
            int lo = (int)(double)range[0], hi = (int)(double)range[1];
            if (lo < 0 || hi > 20 || lo > hi)
                throw new VerbArgsException("skill_range must be [min,max] with 0 <= min <= max <= 20");
            return new[] { lo, hi };
        }

        private static string ParseName(VerbArgs a)
        {
            string name = a.Str("name");
            if (name != null && name.Length > 60)
                throw new VerbArgsException("name must be 1..60 characters");
            return name;
        }

        private static QualityRange ParseQualityRange(VerbArgs a, string key)
        {
            var range = a.Raw(key) as List<object>;
            if (range == null || range.Count != 2 || !(range[0] is string) || !(range[1] is string))
                throw new VerbArgsException(key
                    + " must be [min,max] quality names (Awful|Poor|Normal|Good|Excellent|Masterwork|Legendary)");
            var lo = ParseQuality((string)range[0]);
            var hi = ParseQuality((string)range[1]);
            if (lo > hi) throw new VerbArgsException(key + " min must not exceed max");
            return new QualityRange(lo, hi);
        }

        private static BillStoreModeDef ParseStoreMode(string modeName)
        {
            if (modeName == null) return BillStoreModeDefOf.SpecificStockpile;
            if (string.Equals(modeName, "drop", StringComparison.OrdinalIgnoreCase))
                return BillStoreModeDefOf.DropOnFloor;
            if (string.Equals(modeName, "best", StringComparison.OrdinalIgnoreCase))
                return BillStoreModeDefOf.BestStockpile;
            if (string.Equals(modeName, "specific", StringComparison.OrdinalIgnoreCase))
                return BillStoreModeDefOf.SpecificStockpile;
            var def = DefDatabase<BillStoreModeDef>.GetNamedSilentFail(modeName);
            if (def == null)
                throw new VerbArgsException(
                    "store_mode must be drop|best|specific (or a BillStoreModeDef defName)");
            return def;
        }

        // `include_from` takes a storage target or the word "all" — the
        // "IncludeFromAll" option, which is SetIncludeGroup(null). Shape only;
        // resolving the target needs the map and happens at the write.
        private static object ParseIncludeFrom(VerbArgs a)
        {
            object raw = a.Raw("include_from");
            if (raw == null) return null;
            if (raw is double) return raw;
            if (raw is string s)
            {
                if (string.Equals(s, "all", StringComparison.OrdinalIgnoreCase)) return null;
                return raw;
            }
            throw new VerbArgsException(
                "include_from must be \"all\" (or null) for IncludeFromAll, or a storage target "
                + "(\"zone:<id>\", \"thing:<id>\", or a thing id)");
        }

        // The four keywords GeneratePawnRestrictionOptions offers besides a
        // named pawn. Kept beside ParsePawnId so the pre-pass and SetWorker
        // cannot disagree about what is a keyword and what is an id.
        private static bool IsWorkerKeyword(string want)
            => string.Equals(want, "any", StringComparison.OrdinalIgnoreCase)
               || string.Equals(want, "slave", StringComparison.OrdinalIgnoreCase)
               || string.Equals(want, "mech", StringComparison.OrdinalIgnoreCase)
               || string.Equals(want, "non-mech", StringComparison.OrdinalIgnoreCase);

        private static QualityCategory ParseQuality(string s)
        {
            foreach (QualityCategory q in Enum.GetValues(typeof(QualityCategory)))
                if (string.Equals(q.ToString(), s, StringComparison.OrdinalIgnoreCase)) return q;
            throw new VerbArgsException(
                "quality must be Awful|Poor|Normal|Good|Excellent|Masterwork|Legendary (got '" + s + "')");
        }

        // A [min,max] pair given either as 0..1 unit floats or as 0..100
        // percents — the widget shows percents and the field stores units, and
        // an agent that guesses wrong would silently set 1% instead of 100%.
        private static float[] Pct01(VerbArgs a, string key)
        {
            var range = a.Raw(key) as List<object>;
            if (range == null || range.Count != 2 || !(range[0] is double) || !(range[1] is double))
                throw new VerbArgsException(key + " must be [min,max] as 0..1 fractions or 0..100 percents");
            double lo = (double)range[0], hi = (double)range[1];
            if (lo > 1.0 || hi > 1.0) { lo /= 100.0; hi /= 100.0; }
            if (lo < 0 || hi > 1.0 || lo > hi)
                throw new VerbArgsException(key + " must be [min,max] with 0 <= min <= max <= 1 (or <= 100 as percents)");
            return new[] { (float)lo, (float)hi };
        }

        // Dialog_BillConfig.GeneratePawnRestrictionOptions + its
        // BillDialogUtility half, reproduced. The five setters on
        // RimWorld/Bill.cs are mutually exclusive by construction.
        private static void SetWorker(Map map, Bill bill, string want,
            Action<string, object> Note, Action<string, string, string> No)
        {
            bool mechanitorOnly = ModsConfig.BiotechActive && bill.recipe != null && bill.recipe.mechanitorOnlyRecipe;

            if (string.Equals(want, "any", StringComparison.OrdinalIgnoreCase))
            {
                // The "AnyWorker"/"AnyMechanitor" option, both SetAnyPawnRestriction.
                bill.SetAnyPawnRestriction();
                Note("worker", mechanitorOnly ? "any-mechanitor" : "any");
                return;
            }
            if (string.Equals(want, "slave", StringComparison.OrdinalIgnoreCase))
            {
                if (mechanitorOnly)
                { No("worker", "mechanitor-only", "the dropdown offers only mechanitors for this recipe"); return; }
                if (!ModsConfig.IdeologyActive)
                { No("worker", "no-ideology", "the AnySlave option is drawn only with Ideology active"); return; }
                bill.SetAnySlaveRestriction(); Note("worker", "slave"); return;
            }
            if (string.Equals(want, "mech", StringComparison.OrdinalIgnoreCase)
                || string.Equals(want, "non-mech", StringComparison.OrdinalIgnoreCase))
            {
                if (mechanitorOnly)
                { No("worker", "mechanitor-only", "the dropdown offers only mechanitors for this recipe"); return; }
                bool ok = false;
                try { ok = ModsConfig.BiotechActive && MechWorkUtility.AnyWorkMechCouldDo(bill.recipe); } catch { }
                if (!ok)
                {
                    No("worker", "no-mech-could-do",
                        "MechWorkUtility.AnyWorkMechCouldDo is false for this recipe, so the AnyMech and "
                        + "AnyNonMech options are not drawn");
                    return;
                }
                if (string.Equals(want, "mech", StringComparison.OrdinalIgnoreCase))
                { bill.SetAnyMechRestriction(); Note("worker", "mech"); }
                else { bill.SetAnyNonMechRestriction(); Note("worker", "non-mech"); }
                return;
            }

            // A specific pawn. BillDialogUtility.GetPawnRestrictionOptionsForBill
            // draws from PawnsFinder.AllMaps_FreeColonists — NOT this map only
            // — and gives a NULL action (an unclickable row) to any pawn with
            // `WorkTypeIsDisabled(workGiver.workType)`.
            //
            // SNAPSHOTTED, and the getter is doubly shared (WorldSafe Class E):
            // RimWorld/PawnsFinder.cs AllMaps_FreeColonists opens with
            // `allMaps_FreeColonists_Result.Clear()` on a STATIC list, and on a
            // single-map game — every bench colony — it returns
            // `maps[0].mapPawns.FreeColonists` directly, which is itself a
            // per-faction cache cleared and refilled on read. Vanilla walks it
            // inside a UI frame where nothing re-enters; nothing in this loop
            // re-enters it either, but AddWarnings two hundred lines above
            // already snapshots the same family and the cost is one list.
            int id = ParsePawnId(want);
            Pawn found = null;
            try
            {
                var freeAllMaps = new List<Pawn>(PawnsFinder.AllMaps_FreeColonists);
                for (int i = 0; i < freeAllMaps.Count; i++)
                {
                    var p = freeAllMaps[i];
                    if (p != null && p.thingIDNumber == id) { found = p; break; }
                }
            }
            catch { }
            if (found == null)
            {
                No("worker", "not-a-free-colonist",
                    $"no free colonist with id {id} (BillDialogUtility.GetPawnRestrictionOptionsForBill "
                    + "draws from PawnsFinder.AllMaps_FreeColonists)");
                return;
            }
            if (mechanitorOnly)
            {
                bool isMech = false;
                try { isMech = MechanitorUtility.IsMechanitor(found); } catch { }
                if (!isMech)
                { No("worker", "not-a-mechanitor", "this recipe's dropdown lists only mechanitors"); return; }
            }
            // The unclickable-row clause. WorkGiverOf is our own walk because
            // BillUtility.GetWorkgiver Log.ErrorOnce's twice on a miss.
            var wg = WorkGiverOf(bill.billStack?.billGiver as Thing);
            if (wg?.workType != null)
            {
                bool disabled = false;
                try { disabled = found.WorkTypeIsDisabled(wg.workType); } catch { }
                if (disabled)
                {
                    No("worker", "work-type-disabled",
                        Safe(() => PawnSafe.Name(found)) + " " + Safe(() => (string)"WillNever".Translate(wg.label))
                        + " — BillDialogUtility gives that row a null action, so the option is drawn and "
                        + "cannot be clicked");
                    return;
                }
            }
            bill.SetPawnRestriction(found);
            Note("worker", new Dictionary<string, object>
            {
                ["pawn"] = found.thingIDNumber,
                ["name"] = PawnSafe.Name(found),
                ["work_giver"] = wg?.defName,
            });
        }

        private static int ParsePawnId(string s)
        {
            string body = s.StartsWith("pawn:", StringComparison.Ordinal) ? s.Substring(5) : s;
            if (int.TryParse(body, out int id)) return id;
            throw new VerbArgsException(
                "worker must be any|slave|mech|non-mech or a pawn id (\"pawn:<id>\" or the number)");
        }

        // RimWorld/BillUtility.cs GetWorkgiver's walk, WITHOUT its two
        // `Log.ErrorOnce` calls ("Attempting to get the workgiver for a
        // non-Thing IBillGiver", "Can't find a WorkGiver for a BillGiver"),
        // either of which is a red error on a modded bench. A miss here
        // degrades ONE clause of the worker gate; a miss there breaches the
        // zero-red-errors invariant.
        private static WorkGiverDef WorkGiverOf(Thing giver)
        {
            if (giver == null) return null;
            try
            {
                var all = DefDatabase<WorkGiverDef>.AllDefsListForReading;
                for (int i = 0; i < all.Count; i++)
                {
                    var wg = all[i];
                    if (wg?.Worker is WorkGiver_DoBill dobill && dobill.ThingIsUsableBillGiver(giver)) return wg;
                }
            }
            catch { }
            return null;
        }

        // Dialog_BillConfig's store-mode section. `Bill.SetStoreMode`'s BASE is
        // a red error and `Bill_Production.SetStoreMode` log-errors on a
        // mode/group mismatch and stores the values anyway, so the pairing is
        // validated here, before the call.
        // `Dialog_BillConfig.FillOutputDropdownOptions` + `FillSlotGroupOptions`,
        // reproduced once. BOTH of the dialog's output dropdowns run through
        // this same pair — the store-mode one ("store in…") and the
        // "Include from" one — so both gates are the same four, and factoring
        // them here is what keeps the second lever from drifting from the
        // first. Returns false having already called `No`, so the caller's
        // whole clause is `if (!ResolveOutputGroup(...)) return;`.
        private static bool ResolveOutputGroup(Map map, Bill_Production bill, object raw,
            string field, Action<string, string, string> No, out ISlotGroup group)
        {
            group = null;
            var parent = ResolveStoreParent(map, raw, out string why);
            if (parent == null) { No(field, "not-found", why); return false; }
            group = SlotGroupOf(parent);
            if (group == null)
            {
                No(field, "no-slot-group",
                    "that storage has no ISlotGroup, so Dialog_BillConfig's output dropdowns never "
                    + "offer it (FillOutputDropdownOptions walks "
                    + "map.haulDestinationManager.AllGroupsListInPriorityOrder)");
                return false;
            }
            // A gate nobody names, and it is not obvious from the outside.
            // FillOutputDropdownOptions collects a slot group only when
            //     !(slotGroup.parent is Building_Storage bs) || bs is IRenameable
            // and NO vanilla Building_Storage implements IRenameable. So an
            // UNGROUPED storage building — a shelf, a crate — is never offered
            // in either dropdown; only stockpile zones and STORAGE GROUPS are.
            // Linking the shelf into a group is exactly what makes it
            // offerable, which is why that route is named.
            if (!(group is StorageGroup) && group is SlotGroup sg
                && sg.parent is Building_Storage && !(sg.parent is IRenameable))
            {
                No(field, "ungrouped-storage-building",
                    "Dialog_BillConfig.FillOutputDropdownOptions collects a slot group only when its "
                    + "parent is NOT a Building_Storage, or is one that is IRenameable — and no vanilla "
                    + "Building_Storage is. An ungrouped shelf or crate is therefore never offered in a "
                    + "bill's output dropdowns. Link it into a storage group with `storage-link` (the "
                    + "group IS offered, by its label), or target a stockpile zone.");
                group = null;
                return false;
            }
            // FillSlotGroupOptions: a group the product cannot be stored in is
            // drawn with a NULL action and "(incompatible)". Unclickable.
            bool canStore = true;
            try { canStore = bill.recipe.WorkerCounter.CanPossiblyStore(bill, group); } catch { }
            if (!canStore)
            {
                No(field, "incompatible",
                    "that storage's filter does not accept "
                    + (Safe(() => bill.recipe.ProducedThingDef?.defName) ?? "this bill's product")
                    + " — RecipeWorkerCounter.CanPossiblyStore is false, so "
                    + "Dialog_BillConfig.FillSlotGroupOptions draws the row with a null action and the "
                    + "label \"(incompatible)\". Widen the storage filter with `storage-set` first.");
                group = null;
                return false;
            }
            return true;
        }

        private static void SetStoreMode(Map map, Bill_Production bill, VerbArgs a,
            Action<string, object> Note, Action<string, string, string> No)
        {
            object targetRaw = a.Raw("store_target");
            BillStoreModeDef mode = ParseStoreMode(a.Str("store_mode"));

            // Bill_Production.SetStoreMode's own consistency check:
            //   storeMode == SpecificStockpile != (group != null)  -> Log.ErrorOnce
            // It fires the error AND stores the values, so the check has to
            // happen out here or the verb authors a red on a plain typo.
            if (mode == BillStoreModeDefOf.SpecificStockpile && targetRaw == null)
            {
                No("store_mode", "missing-store-target",
                    "store_mode:\"specific\" needs store_target (a stockpile zone or a storage building). "
                    + "Bill_Production.SetStoreMode Log.ErrorOnce's \"Inconsistent bill StoreMode data set\" "
                    + "on the mismatch and stores the values anyway — a red error, so it is refused here.");
                return;
            }
            if (mode != BillStoreModeDefOf.SpecificStockpile && targetRaw != null)
            {
                No("store_target", "mode-mismatch",
                    "store_target applies only to store_mode:\"specific\"; the same Log.ErrorOnce fires "
                    + "on the reverse mismatch");
                return;
            }

            ISlotGroup group = null;
            if (targetRaw != null)
            {
                if (!ResolveOutputGroup(map, bill, targetRaw, "store_target", No, out group)) return;
            }

            bill.SetStoreMode(mode, group);
            Note("store_mode", new Dictionary<string, object>
            {
                ["mode"] = mode.defName,
                ["target"] = group == null ? null : Safe(() => SlotGroup.GetGroupLabel(group)),
            });
        }

        // Dialog_BillConfig.DoIngredientConfigPane's filter widget, over
        // `bill.ingredientFilter` with `recipe.fixedIngredientFilter` as the
        // parent. THE PANE IS NOT DRAWN AT ALL when every one of the recipe's
        // IngredientCounts IsFixedIngredient — the recipe has no choosable
        // ingredient, so there is nothing to configure.
        private static void SetIngredientFilter(Bill_Production bill, VerbArgs a,
            Action<string, object> Note, Action<string, string, string> No)
        {
            bool anyChoosable = false;
            try
            {
                if (bill.recipe?.ingredients != null)
                    for (int i = 0; i < bill.recipe.ingredients.Count; i++)
                        if (!bill.recipe.ingredients[i].IsFixedIngredient) { anyChoosable = true; break; }
            }
            catch { }
            if (!anyChoosable)
            {
                No("filter", "no-choosable-ingredient",
                    "every ingredient of this recipe IsFixedIngredient, so "
                    + "Dialog_BillConfig.DoIngredientConfigPane draws no filter at all");
                return;
            }
            if (bill.ingredientFilter == null)
            { No("filter", "no-filter", "this bill has no ingredientFilter"); return; }

            // Dialog_BillConfig.DoIngredientConfigPane's own forceHiddenFilters
            // argument: the four Ideology diet filters PLUS this recipe's
            // `forceHiddenSpecialFilters`.
            var hidden = new List<SpecialThingFilterDef>(StorageFilterOps.IdeoDietFilters());
            try
            {
                if (bill.recipe.forceHiddenSpecialFilters != null)
                    hidden.AddRange(bill.recipe.forceHiddenSpecialFilters);
            }
            catch { }

            var applied = StorageFilterOps.Apply(bill.ingredientFilter, bill.recipe.fixedIngredientFilter,
                "recipe-fixed", a, out var refusedDefs, hidden);
            foreach (var r in refusedDefs) No("filter", "outside-fixed-filter", r);
            if (applied.Count > 0)
                Note("filter", new Dictionary<string, object>
                {
                    ["ops"] = applied,
                    ["summary"] = SafeObj(() => FilterSummary.Build(
                        bill.ingredientFilter, bill.recipe.fixedIngredientFilter, "recipe-fixed")),
                    ["note"] = "Bill.ExposeData NARROWS this filter during the SAVING pass for any recipe "
                        + "with a fixedIngredientFilter (DESIGN decisions log 2026-08-31), so an autosave "
                        + "can change what a later read reports with no player action. Read it live.",
                });
        }

        // ========================= plumbing =================================

        // 2.4's `bills` bill line, trimmed to what a mutation needs to echo.
        // Same field names, same guarded route: `state` is
        // WorldSafe.BillState, never Bill.ShouldDoNow (which writes the scribed
        // `paused` on three paths).
        private static List<object> StackLines(BillStack stack)
        {
            var list = new List<object>();
            if (stack == null) return list;
            var snapshot = new List<Bill>(stack.Bills);
            for (int i = 0; i < snapshot.Count; i++)
            {
                var b = snapshot[i];
                if (b?.recipe == null) continue;
                var prod = b as Bill_Production;
                var d = new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["uid"] = Safe(() => b.GetUniqueLoadID()),
                    ["label"] = Safe(() => b.LabelCap),
                    ["recipe"] = b.recipe.defName,
                    ["suspended"] = b.suspended,
                    ["state"] = WorldSafe.BillState(b, -1),
                    ["work_skill"] = b.recipe.workSkill?.defName,
                };
                if (prod != null)
                {
                    d["repeat_mode"] = prod.repeatMode?.defName;
                    d["repeat_count"] = prod.repeatCount;
                    d["target_count"] = prod.targetCount;
                    d["paused_stored"] = prod.paused;
                    d["store_mode"] = Safe(() => prod.GetStoreMode()?.defName);
                    d["store_group"] = Safe(() => prod.GetSlotGroup() == null
                        ? null : SlotGroup.GetGroupLabel(prod.GetSlotGroup()));
                    // The "Include from" lever, read back. null is
                    // "IncludeFromAll", which is the dropdown's own wording.
                    d["include_group"] = Safe(() => prod.GetIncludeSlotGroup() == null
                        ? null : SlotGroup.GetGroupLabel(prod.GetIncludeSlotGroup()));
                }
                d["pawn_restriction"] = b.PawnRestriction?.thingIDNumber;
                d["slaves_only"] = b.SlavesOnly;
                d["mechs_only"] = b.MechsOnly;
                d["non_mechs_only"] = b.NonMechsOnly;
                list.Add(d);
            }
            return list;
        }

        // index | uid | recipe | all, the same selector `surgery-remove` takes
        // — one vocabulary across the two bill surfaces. `index` is positional
        // and moves under `bill-reorder`; `uid` (Bill.GetUniqueLoadID, which is
        // "Bill_<recipe>_<loadID>" and is scribed) is the stable handle. See
        // the open-question resolution on the issue.
        private static List<Bill> SelectBills(BillStack stack, VerbArgs a)
        {
            var snapshot = new List<Bill>(stack.Bills);
            var picked = new List<Bill>();
            if (a.Bool("all", false)) { picked.AddRange(snapshot); return picked; }
            if (a.Has("index"))
            {
                int i = a.IntReq("index");
                if (i < 0 || i >= snapshot.Count)
                    throw new VerbArgsException($"index must be 0..{snapshot.Count - 1}");
                picked.Add(snapshot[i]);
                return picked;
            }
            if (a.Has("uid"))
            {
                string uid = a.StrReq("uid");
                foreach (var b in snapshot)
                    if (string.Equals(Safe(() => b.GetUniqueLoadID()), uid, StringComparison.Ordinal)) picked.Add(b);
                if (picked.Count == 0) throw new VerbArgsException($"no bill with uid '{uid}' on this bench");
                return picked;
            }
            if (a.Has("recipe"))
            {
                var recipe = Dev.Named<RecipeDef>(a.StrReq("recipe"), "recipe");
                foreach (var b in snapshot) if (b?.recipe == recipe) picked.Add(b);
                if (picked.Count == 0)
                    throw new VerbArgsException($"no '{recipe.defName}' bill on this bench");
                return picked;
            }
            throw new VerbArgsException("pass index, uid, recipe, or all:true");
        }

        // A bill giver by thing id. Fog-filtered like every player-facing
        // lookup (DESIGN 2026-08-30), and the error names the POLICY rather
        // than the thing — confirming that id N exists is itself the leak.
        private static Thing BenchArg(Map map, VerbArgs args, string key)
        {
            object raw = args.Raw(key);
            if (raw == null) throw new VerbArgsException($"missing required arg '{key}' (a bill giver's thing id)");
            int id;
            if (raw is double d) id = (int)d;
            else if (raw is string s && s.StartsWith("thing:", StringComparison.Ordinal)
                     && int.TryParse(s.Substring(6), out int tid)) id = tid;
            else if (raw is string s2 && int.TryParse(s2, out int nid)) id = nid;
            else throw new VerbArgsException($"arg '{key}' must be a thing id (number) or \"thing:<id>\"");

            // ThingRequestGroup.PotentialBillGiver is `!def.AllRecipes
            // .NullOrEmpty()` — the game's own membership test, and the list is
            // maintained by ListerThings rather than computed here. The same
            // universe 2.4's `bills` reports over.
            var givers = map.listerThings.ThingsInGroup(ThingRequestGroup.PotentialBillGiver);
            for (int i = 0; i < givers.Count; i++)
            {
                var t = givers[i];
                if (t == null || t.thingIDNumber != id) continue;
                if (WorldSafe.Hidden(t, map)) break;
                return t;
            }
            throw new VerbArgsException(
                $"no visible bill giver with id {id} on the current map "
                + "(things in unexplored ground are not reported). `bills` lists them.");
        }

        private static BillStack StackOf(Thing t) => (t as IBillGiver)?.BillStack;

        private static Dictionary<string, object> NotAWorkTable(string verb, Thing bench)
            => new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["ok"] = false,
                ["bench"] = bench.thingIDNumber,
                ["def"] = bench.def?.defName,
                ["gate"] = "not-a-work-table",
                ["reason"] = bench is Pawn
                    ? "a pawn's bill stack is its SURGERY queue (Verse/Pawn.cs BillStack => "
                      + "health.surgeryBills); use `surgery-options` / `surgery-add` (spec 3.4)"
                    : "RimWorld/ITab_Bills.cs SelTable is `(Building_WorkTable)base.SelThing`, so the "
                      + "production-bill tab — and the whole gate this verb reproduces — exists ONLY for a "
                      + "Building_WorkTable. This giver has its own widget with its own preconditions, and "
                      + "reproducing a gate we have not read would be the god-hand DESIGN §Action model "
                      + "forbids. `bills` still READS it.",
                ["action"] = NoAction(),
            };

        private static Dictionary<string, object> Refused(string verb, Thing bench, RecipeDef recipe,
            string gate, string reason)
            => new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["ok"] = false,
                ["bench"] = bench.thingIDNumber,
                ["recipe"] = recipe?.defName,
                ["gate"] = gate,
                ["reason"] = reason,
                ["action"] = NoAction(),
            };

        private static ThingDef SafeDef(Func<ThingDef> f)
        {
            try { return f(); } catch { return null; }
        }
    }
}
