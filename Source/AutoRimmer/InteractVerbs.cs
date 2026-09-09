using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // ======================================================= git-bug 70c1f9e ==
    // INTERACT — the float-menu option every `CompInteractable` thing offers,
    // and the ONLY route to `JobDefOf.AnalyzeItem` in the game.
    //
    // WIDGET GATE — RimWorld/CompInteractable.cs `CompFloatMenuOptions`, reached
    // from `RimWorld/FloatMenuOptionProvider_FromThing.GetOptionsFor` ->
    // `Verse/ThingWithComps.GetFloatMenuOptions` -> `comps[i].CompFloatMenuOptions`.
    // The seventeen clauses, the route table, the two forbidden clauses and the
    // `checkOptionalItems` question all live in InteractableSafe.cs; read its
    // header first. This file is the two verbs.
    //
    // SIBLING OF UseVerbs.cs, DELIBERATELY NOT A BRANCH OF IT. `use` reaches
    // `CompUsable`; nothing it does reaches `CompInteractable`, which is a
    // separate ThingComp root with `CanInteract` instead of `CanBeUsedBy`,
    // `Props.useJob` replaced by a per-route JobDef, and a cooldown/active state
    // model `CompUsable` has no equivalent of. The two files share their idiom,
    // their refusal shape and their reason-provenance rule and share no clause.
    //
    // WHAT WAS UNREACHABLE BEFORE THIS FILE. Every `CompAnalyzable` — and there
    // is NO WorkGiver for `JobDefOf.AnalyzeItem` anywhere in the tree, so
    // analysis is player-driven by construction and no amount of work-priority
    // tuning produces it. On a Biotech bench that is the entire mech research
    // ladder: `StandardMechtech` needs `SignalChip` analysed, `HighMechtech`
    // needs `PowerfocusChip`, `UltraMechtech` needs `NanostructuringChip`
    // (`ResearchProjectDef.requiredAnalyzed`), and until then `research-set`
    // answers `blocked_by:"analysis"`. Anomaly adds the whole obelisk/monolith
    // surface on the same comp root; most of those take routes this verb refuses
    // by name rather than drives (InteractableSafe.RouteRefusal).
    internal static partial class PawnActs
    {
        // The verb's full argument list, beside the code that reads it, for
        // RefuseStray's message.
        private static readonly string[] InteractArgs = { "pawn", "pawns", "thing", "queue" };

        private static long InteractJournalRow(Outcome outcome, Pawn pawn, Thing thing,
            CompInteractable comp, Job job)
            => ActOn(outcome, "interact", "interact",
                Safe(() => thing.LabelShort) ?? thing.def?.defName,
                new Dictionary<string, object>
                {
                    ["pawn"] = pawn?.thingIDNumber,
                    ["thing"] = thing.thingIDNumber,
                    ["def"] = thing.def?.defName,
                    ["comp"] = comp?.GetType()?.Name,
                    ["route"] = comp != null ? InteractableSafe.Route(comp) : null,
                    ["job"] = job?.def?.defName,
                });

        // --------------------------------------------------------------------
        // interact {pawn|pawns, thing, queue?}
        //
        // ONE activator per call, like `use`, and for the game's own reason:
        // both reproduced routes end in `target.Pawn.jobs.TryTakeOrderedJob`
        // with a single pawn, and `JobDriver_InteractThing`/`JobDriver_StudyItem`
        // both `pawn.Reserve(TargetA, job, 1, -1, …)` — maxPawns 1. A second
        // pawn in the list is REFUSED with its own gate rather than silently
        // dropped.
        //
        // THERE IS NO `target` ARGUMENT AND THERE IS NOTHING FOR ONE TO MEAN.
        // `CompInteractable` is an ITargetingSource, but its target is WHO
        // SHOULD ACTIVATE — `OrderForceTarget` does `target.Pawn.jobs
        // .TryTakeOrderedJob(...)`, `ValidateTarget` refuses `target.Pawn ==
        // null`, and `OnGUI`'s label is "ChooseWhoShouldActivate". So `pawn` IS
        // the target, `interact {pawn, thing}` is already the targeted form, and
        // git-bug 826d4bf (`use {target}`, a CompTargetable aimed at a corpse or
        // a pawn) has no counterpart here. A stray `target` key is refused with
        // that said out loud rather than dropped.
        // --------------------------------------------------------------------
        [Verb("interact")]
        public static object Interact(VerbContext ctx)
        {
            const string V = "interact";

            // PRE-MUTATION (git-bug c519477). The one key a caller is likely to
            // pass on the strength of the issue's own framing, refused with the
            // correction attached.
            ctx.Args.RefuseStray(V, InteractArgs,
                "Nothing was ordered. `interact` has NO `target` argument and never will: "
                + "CompInteractable.OrderForceTarget's target IS the activating pawn "
                + "(target.Pawn.jobs.TryTakeOrderedJob; the cursor label is \"ChooseWhoShouldActivate\"), "
                + "so `pawn` is the target. The interactables that DO begin a second targeting session — "
                + "CompNociosphere's destination cell, the rocketswarm launcher's attack target — are "
                + "refused by gate `unsupported-route`, not served by an argument.");

            var map = Map();
            var pawns = PawnList(map, ctx.Args, true, "pawns", "pawn");
            var thing = ThingArg(map, ctx.Args, "thing");
            var outcome = new Outcome();
            var pawn = pawns[0];
            string label = Safe(() => thing.LabelShort) ?? thing.def?.defName;

            for (int i = 1; i < pawns.Count; i++)
                outcome.No(pawns[i], "one-activator",
                    "one thing, one activator: CompInteractable.OrderForceTarget orders ONE pawn and both "
                    + "drivers reserve the target with maxPawns 1 (JobDriver_InteractThing"
                    + ".TryMakePreToilReservations, JobDriver_StudyItem's the same), so only the first pawn "
                    + "in the list is ordered. Call `interact` once per pawn.");

            // A thing may carry more than one CompInteractable and vanilla picks
            // between them with `job.interactableIndex`, set from whichever
            // GIZMO was clicked. A verb has no click.
            var comps = InteractableSafe.Comps(thing);
            if (comps.Count == 0)
            {
                bool usable = false;
                try { usable = thing.TryGetComp<CompUsable>() != null; } catch { }
                outcome.NoThing(thing, InteractableSafe.GateNotInteractable,
                    "this thing has no CompInteractable, so the game offers no activation option for it"
                    + (usable
                        ? ". It DOES carry a CompUsable and the game offers a USE option instead — "
                          + "call `use {pawn, thing}` (`use-options` lists what is usable and why not)."
                        : ". `interact-options` lists what carries one; `orders` lists what a pawn can be "
                          + "told to do WITH it as work."));
                return InteractResult(V, outcome, InteractJournalRow(outcome, pawn, thing, null, null),
                    thing, label, null, null, pawn);
            }
            if (comps.Count > 1)
            {
                var names = new List<object>();
                for (int i = 0; i < comps.Count; i++) names.Add(comps[i].GetType().Name);
                outcome.NoThing(thing, InteractableSafe.GateAmbiguous,
                    "this thing carries " + comps.Count + " CompInteractables and the game disambiguates "
                    + "them by WHICH GIZMO WAS CLICKED — CompInteractable.OrderForceTarget leaves "
                    + "job.interactableIndex at -1 and JobDriver_InteractThing then resolves it to the "
                    + "FIRST comp, while CompObeliskDeactivationInteractor sets the index explicitly. There "
                    + "is no click here, so guessing would order the wrong interaction: "
                    + string.Join(", ", names.ConvertAll(o => (string)o).ToArray()));
                return InteractResult(V, outcome, InteractJournalRow(outcome, pawn, thing, null, null),
                    thing, label, null, null, pawn);
            }

            var comp = comps[0];

            // Which OrderForceTarget the game would have run. Everything but the
            // base route and CompAnalyzable's is refused BY NAME — see
            // InteractableSafe.RouteRefusal for the per-class reason.
            if (InteractableSafe.Route(comp) == InteractableSafe.RouteUnsupported)
            {
                var routeRefusal = InteractableSafe.RouteRefusal(comp);
                outcome.No(pawn, routeRefusal.Gate, routeRefusal.Reason, routeRefusal.Row());
                return InteractResult(V, outcome, InteractJournalRow(outcome, pawn, thing, comp, null),
                    thing, label, comp, null, pawn);
            }

            // The seventeen clauses, first refusal wins — the game's own order,
            // asked with checkOptionalItems:true because that is what
            // CompFloatMenuOptions and ValidateTarget ask.
            var refusal = InteractableSafe.Gate(comp, pawn);
            if (refusal != null)
            {
                outcome.No(pawn, refusal.Gate, refusal.Reason, refusal.Row());
                return InteractResult(V, outcome, InteractJournalRow(outcome, pawn, thing, comp, null),
                    thing, label, comp, null, pawn);
            }

            // The job, per route. On the analyze route this also runs vanilla's
            // SECOND bench lookup — OrderForceTarget's own
            // `(canStudyInPlace || TryFindResearchBench)` — which is a silent
            // no-op in the game when it fails and must not be one here.
            Building_ResearchBench bench;
            var job = InteractableSafe.MakeJob(comp, pawn, out bench);
            if (job == null)
            {
                outcome.No(pawn, InteractableSafe.GateNoResearchBench,
                    "CompAnalyzable.OrderForceTarget's own second lookup failed: it runs "
                    + "`ValidateTarget(target) && (Props.canStudyInPlace || StudyUtility"
                    + ".TryFindResearchBench(pawn, out bench))` and does NOTHING AT ALL when the bench "
                    + "lookup fails — the player's click is silently swallowed. Refused here rather than "
                    + "reported as an order. (The gate above asks the same question one clause earlier; "
                    + "reaching this means the bench went away between the two, or a mod moved it.)",
                    new Dictionary<string, object>
                    {
                        ["cite"] = "RimWorld/CompAnalyzable.OrderForceTarget",
                    });
                return InteractResult(V, outcome, InteractJournalRow(outcome, pawn, thing, comp, null),
                    thing, label, comp, null, pawn);
            }

            // 4087644 — PawnActs.AlreadyDoing. DELIBERATELY BEFORE THE UN-FORBID,
            // which is one step earlier than vanilla would clear it: RimWorld has
            // no already-doing-it check at all and reaches TryTakeOrderedJob's
            // JobIsSameAs early-out with the flag already cleared. Ordering it
            // this way means a repeat `interact` on a job already running mutates
            // NOTHING, which is what `already-doing-it` promises; the difference
            // is only observable when the same pawn is re-ordered onto the same
            // forbidden thing it is already carrying to a bench.
            bool queued = ctx.Args.Bool("queue", false);
            if (AlreadyDoing(pawn, job))
            {
                outcome.No(pawn, GateAlready, AlreadyWhy(queued), AlreadyLine(pawn, queued));
                return InteractResult(V, outcome, InteractJournalRow(outcome, pawn, thing, comp, job),
                    thing, label, comp, bench, pawn);
            }

            // THE UN-FORBID GOES HERE AND NOWHERE EARLIER. CompAnalyzable
            // .OrderForceTarget clears CompForbiddable AFTER ValidateTarget has
            // already run the forbidden clauses, so a forbidden thing plus an
            // undrafted pawn is a refusal the player gets too. `use` un-forbids
            // BEFORE ordering because its chain has no forbidden clause at all;
            // copying that here would fabricate a permission.
            bool unforbidden = false;
            if (InteractableSafe.Route(comp) == InteractableSafe.RouteAnalyze)
                unforbidden = InteractableSafe.ClearForbidden(comp);

            if (!TakeOrder(pawn, job, JobTag.Misc, queued, out var eff))
            {
                outcome.No(pawn, "refused",
                    "Pawn_JobTracker.TryTakeOrderedJob refused the job (pre-toil reservations failed). "
                    + "Both drivers reserve the target thing with maxPawns 1, and the analyze driver also "
                    + "reserves the BENCH and the cell the item is placed in — a bench another pawn has "
                    + "reserved refuses here rather than at the gate.",
                    // The un-forbid ALREADY HAPPENED — vanilla clears the flag
                    // before TryTakeOrderedJob too — so the refusal says so
                    // rather than leaving a silent mutation behind it.
                    new Dictionary<string, object> { ["unforbidden"] = unforbidden });
                return InteractResult(V, outcome, InteractJournalRow(outcome, pawn, thing, comp, job),
                    thing, label, comp, bench, pawn);
            }

            var line = Merge(JobLine(pawn), eff);
            if (unforbidden) line["unforbidden"] = true;
            outcome.Ok(pawn, line);

            long seq = InteractJournalRow(outcome, pawn, thing, comp, job);
            return InteractResult(V, outcome, seq, thing, label, comp, bench, pawn, job);
        }

        // Every exit's envelope, so a refusal and an acceptance describe the
        // target identically and no exit can quietly drop a disclosure.
        private static object InteractResult(string V, Outcome outcome, long seq, Thing thing, string label,
            CompInteractable comp, Building_ResearchBench bench, Pawn pawn = null, Job job = null)
        {
            var d = new Dictionary<string, object>
            {
                ["thing"] = thing.thingIDNumber,
                ["label"] = label,
                ["def"] = thing.def?.defName,
            };
            if (comp != null)
            {
                foreach (var kv in InteractableSafe.Describe(comp)) d[kv.Key] = kv.Value;

                // ============== THE checkOptionalItems DISCLOSURE ============
                // Its own concept, never collapsed into the re-check. Published
                // on every exit that got as far as a comp, because "this def has
                // no optional item" is a different fact from "we did not look".
                d["optional_items"] = OptionalItems(comp, pawn);

                // No modal is opened by either reproduced route — checked, not
                // assumed — but the analyze route DOES end in a letter, and
                // `advance` halts on unread news. `advance_halt` carries it
                // (InteractableSafe.Describe adds it).
                d["dialogs_skipped"] = "none. Neither reproduced route calls Find.WindowStack.Add: "
                    + "CompInteractable.OrderForceTarget makes an InteractThing job and "
                    + "CompAnalyzable.OrderForceTarget an AnalyzeItem job, and neither raises a "
                    + "confirmation. The routes that DO — CompGoldenCube's Dialog_MessageBox above all — "
                    + "are refused with gate `unsupported-route` rather than driven and then explained. "
                    + "Read `advance_halt` for what stops the advance instead.";

                if (job != null)
                {
                    d["job"] = job.def?.defName;
                    d["job_targets"] = new Dictionary<string, object>
                    {
                        ["a"] = SafeObj(() => (object)job.targetA.Thing?.thingIDNumber),
                        ["b"] = SafeObj(() => (object)job.targetB.Thing?.thingIDNumber),
                        ["c"] = SafeObj(() => (object)(job.targetC.IsValid
                            ? Positions.Out(job.targetC.Cell) : null)),
                    };
                }
                if (bench != null)
                {
                    d["research_bench"] = new Dictionary<string, object>
                    {
                        ["thing"] = bench.thingIDNumber,
                        ["def"] = bench.def?.defName,
                        ["pos"] = SafeObj(() => Positions.Out(bench.Position)),
                        ["note"] = "the item is HAULED here. JobDriver_StudyItem goes to the item, carries "
                            + "it, goes to the bench's interaction cell and places it, and only then does "
                            + "JobDriver_AnalyzeItem's wait toil run — "
                            + "ceil(Props.analysisDurationHours * 2500) / the pawn's ResearchSpeed.",
                    };
                }

                string advisory = InteractableSafe.TargetParamsAdvisory(comp, pawn);
                if (advisory != null) d["target_params_note"] = advisory;
            }
            d["note"] = "an accepted `interact` is an ORDER, not an effect. Both drivers re-evaluate the "
                + "gate continuously — JobDriver_InteractThing FailOns `!Interactable.CanInteract(pawn)` on "
                + "every goto and on the activation toil, and JobDriver_StudyItem FailOnDespawnedNull"
                + "OrForbidden's the bench and the item — so the job can still die mid-run. The effect "
                + "lands in the LAST toil (CompInteractable.Interact, which re-checks with "
                + "checkOptionalItems:false, or CompAnalyzable.OnAnalyzed). Advance, then read the effect "
                + "— not this envelope — for what happened.";
            return outcome.Result(V, seq, d);
        }

        // ==================== checkOptionalItems, AS ITS OWN CONCEPT =========
        // `CompInteractable.CanInteract(Pawn, bool checkOptionalItems = true)`
        // and `Interact(Pawn caster, bool force)` — whose first line re-checks
        // with checkOptionalItems:FALSE — do not ask the same question, and the
        // difference is not "a second opinion". `JobDriver_InteractThing`'s last
        // toil does `pawn.carryTracker.DestroyCarriedThing()` and THEN calls
        // Interact, so by that point the optional item has been consumed and an
        // optional-item check would refuse the very interaction it was carried
        // for. That is the whole of why the parameter exists.
        //
        // The base method IGNORES the parameter. Two vanilla classes read it,
        // both Anomaly, both wanting Shards: CompObeliskDeactivationInteractor
        // (Props.shardsRequired) and CompGoldenCube (one). Both take routes this
        // verb refuses, so on everything it DOES drive the answer is "no
        // optional item" — which is stated rather than left to inference.
        private static Dictionary<string, object> OptionalItems(CompInteractable comp, Pawn pawn)
        {
            bool matters = InteractableSafe.OptionalItemsMatter(comp);
            var d = new Dictionary<string, object>
            {
                ["applies"] = matters,
                ["checked_with"] = true,
                ["note"] = "the gate above was asked with checkOptionalItems:TRUE, which is what "
                    + "CompInteractable.CompFloatMenuOptions and ValidateTarget ask. "
                    + "CompInteractable.Interact re-asks with FALSE in the job's LAST toil, AFTER "
                    + "JobDriver_InteractThing has done pawn.carryTracker.DestroyCarriedThing() — the item "
                    + "is already spent by then, so the two calls are deliberately different questions and "
                    + "neither implies the other.",
            };
            if (!matters)
            {
                d["detail"] = (comp != null ? comp.GetType().Name : "this comp")
                    + " does not read checkOptionalItems (CompInteractable.CanInteract ignores the "
                    + "parameter entirely), so no interaction here consumes a carried item and the two "
                    + "calls agree. The only vanilla readers are CompObeliskDeactivationInteractor and "
                    + "CompGoldenCube, both of which want Shards and both of which this verb refuses by "
                    + "route.";
                return d;
            }
            // A type that DOES read it: run the chain both ways and report the
            // difference rather than asserting there is none.
            var withItems = InteractableSafe.Refusals(comp, pawn, false, true);
            var withoutItems = InteractableSafe.Refusals(comp, pawn, false, false);
            d["refusals_with_optional_items"] = Rows(withItems);
            d["refusals_without_optional_items"] = Rows(withoutItems);
            d["differs"] = withItems.Count != withoutItems.Count;
            if (withItems.Count != withoutItems.Count)
                d["detail"] = "the two passes DISAGREE: this comp's CanInteract reads checkOptionalItems, "
                    + "so the order-time question and the job's final re-check have different answers. The "
                    + "clauses only the first pass refuses are the ones an item must be carried to satisfy.";
            return d;
        }

        private static List<object> Rows(List<InteractableSafe.Refusal> list)
        {
            var rows = new List<object>();
            for (int i = 0; i < list.Count; i++) rows.Add(list[i].Row());
            return rows;
        }

        // --------------------------------------------------------------------
        // interact-options {pawn?, thing?, cap?}   READ-ONLY
        //
        // The `use-options` shape, for this surface: what the game would offer,
        // whether it is clickable, and — when it is not — the game's own reason.
        // Three forms, all one row shape:
        //
        //   {}                 every CompInteractable thing on the map, no verdict
        //   {pawn}             the same list, each with THAT pawn's verdict
        //   {thing, pawn?}     one thing, in full
        //
        // AND IT PUBLISHES EVERY REFUSAL, NOT THE FIRST. `CanInteract` returns
        // on its first refusal and `interact` stops where the game stops, so an
        // agent peeling clauses off one order at a time pays one round trip per
        // clause — and this chain is seventeen long. This read runs the whole
        // thing and hands back the list.
        // --------------------------------------------------------------------
        [Verb("interact-options")]
        public static object InteractOptions(VerbContext ctx)
        {
            var map = Map();
            var pawns = PawnList(map, ctx.Args, false, "pawns", "pawn");
            Pawn pawn = pawns.Count > 0 ? pawns[0] : null;
            var one = ThingArg(map, ctx.Args, "thing", false);
            int cap = ctx.Args.Int("cap", 40);
            if (cap < 1 || cap > 300) throw new VerbArgsException("cap must be 1..300");

            // An explicit `thing` with no CompInteractable must SAY so. Dropping
            // it and answering `options:[]` reads as "nothing is interactable
            // here", which is a different fact.
            if (one != null && InteractableSafe.Comps(one).Count == 0)
            {
                bool usable = false;
                try { usable = one.TryGetComp<CompUsable>() != null; } catch { }
                return new Dictionary<string, object>
                {
                    ["pawn"] = pawn?.thingIDNumber,
                    ["thing"] = one.thingIDNumber,
                    ["def"] = one.def?.defName,
                    ["options"] = new List<object>(),
                    ["total"] = 0,
                    ["more"] = 0,
                    ["interactable"] = pawn != null ? (object)0 : null,
                    ["gate"] = InteractableSafe.GateNotInteractable,
                    ["reason"] = "this thing has no CompInteractable, so the game offers no activation "
                        + "option for it"
                        + (usable ? ". It carries a CompUsable — `use` / `use-options` is that surface." : ""),
                };
            }

            var candidates = new List<Thing>();
            if (one != null) candidates.Add(one);
            else
            {
                // Fog-filtered, like every player-facing read
                // (FloatMenuOptionProvider.IgnoreFogged is true, and WorldSafe
                // .Hidden is this project's standing rule — the agent must not
                // be handed things no player has seen).
                var all = map.listerThings.AllThings;
                for (int i = 0; i < all.Count; i++)
                {
                    var t = all[i];
                    if (t == null) continue;
                    // TryGetComp first, Comps() only for the survivors: AllThings
                    // is thousands of entries on a built colony and Comps
                    // allocates a list per call.
                    bool any = false;
                    try { any = t.TryGetComp<CompInteractable>() != null; } catch { }
                    if (!any) continue;
                    bool hidden = true;
                    try { hidden = WorldSafe.Hidden(t, map); } catch { }
                    if (hidden) continue;
                    candidates.Add(t);
                }
                candidates.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
            }

            var rows = new List<object>();
            int total = 0, ok = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                var t = candidates[i];
                var comps = InteractableSafe.Comps(t);
                if (comps.Count == 0) continue;
                total++;
                bool can;
                var row = InteractOptionRow(t, comps, pawn, out can);
                if (can) ok++;
                if (rows.Count < cap) rows.Add(row);
            }

            return new Dictionary<string, object>
            {
                ["pawn"] = pawn?.thingIDNumber,
                ["name"] = pawn != null ? PawnSafe.Name(pawn) : null,
                ["options"] = rows,
                ["total"] = total,
                ["more"] = Math.Max(0, total - rows.Count),
                ["interactable"] = pawn != null ? (object)ok : null,
                ["note"] = pawn == null
                    ? "pass `pawn` for a verdict: without one this lists what CARRIES a CompInteractable, "
                      + "not what anyone can activate — eleven of the seventeen clauses cannot fire "
                      + "without one (CanInteract wraps them in `if (activateBy != null)`), and the "
                      + "research-bench clause asks a WEAKER question without one: with no pawn it is "
                      + "merely \"does the colony own a bench\", with one it is \"can THIS pawn reach and "
                      + "reserve a powered one\". Buildings and items are both here."
                    : "`can_interact:false` rows carry EVERY clause that refused, not just the first: "
                      + "CompInteractable.CanInteract returns on the first and `interact` stops where the "
                      + "game stops, but this read runs the whole chain. `reason_from` says whose words "
                      + "the reason is, and `cite` names the member each clause was read off.",
            };
        }

        private static Dictionary<string, object> InteractOptionRow(Thing t, List<CompInteractable> comps,
            Pawn pawn, out bool can)
        {
            can = false;
            var comp = comps[0];
            var d = new Dictionary<string, object>
            {
                ["thing"] = t.thingIDNumber,
                ["def"] = t.def?.defName,
                ["label"] = Safe(() => t.LabelShort) ?? t.def?.defName,
                ["pos"] = SafeObj(() => Positions.Out(t.Position)),
                ["forbidden"] = SafeObj(() => (object)t.IsForbidden(Faction.OfPlayer)),
                ["stack"] = SafeObj(() => (object)t.stackCount),
            };
            foreach (var kv in InteractableSafe.Describe(comp)) d[kv.Key] = kv.Value;

            // Seeded null so EVERY row carries the three verdict keys, including
            // the no-pawn form. An ABSENT key and a null one read identically to
            // an `eq(…, None)` assertion, so a suite checking the shape would go
            // green on a row that never computed a verdict at all.
            d["can_interact"] = null;
            d["gate"] = null;
            d["reason"] = null;

            if (comps.Count > 1)
            {
                var names = new List<object>();
                for (int i = 0; i < comps.Count; i++) names.Add(comps[i].GetType().Name);
                d["comps"] = names;
                d["can_interact"] = false;
                d["gate"] = InteractableSafe.GateAmbiguous;
                d["reason"] = "this thing carries " + comps.Count + " CompInteractables and only a GIZMO "
                    + "click distinguishes them (job.interactableIndex). `interact` refuses it.";
                d["refusals"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["gate"] = InteractableSafe.GateAmbiguous,
                        ["reason"] = d["reason"],
                        ["reason_from"] = "autorimmer",
                        ["cite"] = "RimWorld/JobDriver_InteractThing.Interactable",
                    },
                };
                return d;
            }

            // The route refusal is a fact about the DEF, not about the pawn, so
            // it is published with or without one.
            if (InteractableSafe.Route(comp) == InteractableSafe.RouteUnsupported)
            {
                var routeRefusal = InteractableSafe.RouteRefusal(comp);
                d["can_interact"] = false;
                d["gate"] = routeRefusal.Gate;
                d["reason"] = routeRefusal.Reason;
                d["refusals"] = new List<object> { routeRefusal.Row() };
                return d;
            }

            if (pawn == null) return d;

            var chain = InteractableSafe.Refusals(comp, pawn, stopAtFirst: false);
            var refusals = Rows(chain);
            can = refusals.Count == 0;
            d["can_interact"] = can;
            // The FIRST refusal promoted to the top level, because that is the
            // one `interact` would answer with and the acceptance asks for "the
            // gate" and "the game's reason" singular.
            d["gate"] = chain.Count > 0 ? chain[0].Gate : null;
            d["reason"] = chain.Count > 0 ? chain[0].Reason : null;
            d["refusals"] = refusals;
            d["optional_items"] = OptionalItems(comp, pawn);
            string advisory = InteractableSafe.TargetParamsAdvisory(comp, pawn);
            if (advisory != null) d["target_params_note"] = advisory;
            return d;
        }
    }
}
