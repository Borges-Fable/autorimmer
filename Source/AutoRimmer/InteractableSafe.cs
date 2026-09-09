using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // ======================================================= git-bug 70c1f9e ==
    // INTERACTABLE THINGS — the one reproduction of RimWorld/CompInteractable.cs.
    //
    // SIBLING OF UsableSafe.cs, NOT A BRANCH OF IT. `CompUsable` and
    // `CompInteractable` are two unrelated ThingComp roots with two unrelated
    // gate methods (`CanBeUsedBy` vs `CanInteract`), two job constructions, two
    // props classes (`CompProperties_Usable` vs `CompProperties_Interactable`)
    // and — the part `CompUsable` has NO equivalent of — a cooldown/active
    // state model that refuses on the comp's own scribed counters before it ever
    // looks at a pawn. This file shares UsableSafe's idiom, its refusal shape
    // and its reason-provenance rule; it shares none of its clauses.
    //
    // WHY IT EXISTS AT ALL: `CompAnalyzable : CompInteractable`, the mech chips
    // are `CompAnalyzableUnlockResearch`, and there is NO WorkGiver anywhere for
    // `JobDefOf.AnalyzeItem` — grep the tree, the only producers are
    // `CompAnalyzable.OrderForceTarget` and the DEV gizmo. Analysis is
    // player-driven by construction, so with no verb reaching it the whole mech
    // research ladder (`StandardMechtech`/`HighMechtech`/`UltraMechtech`, one
    // chip per rung via `ResearchProjectDef.requiredAnalyzed`) is unreachable.
    //
    // ================= THE ISSUE'S `TryInteract` DOES NOT EXIST ==============
    // There is no `TryInteract` on `CompInteractable`. The method that re-checks
    // is `Interact(Pawn caster, bool force = false)`, whose first line is
    //
    //     if (CanInteract(caster, checkOptionalItems: false).Accepted || force)
    //
    // and it is called from the LAST toil of `JobDriver_InteractThing`, not at
    // order time. So the two calls really do ask different questions — but the
    // reason is narrower and more interesting than "a second opinion": the toil
    // immediately above it does `pawn.carryTracker.DestroyCarriedThing()`, so by
    // the time `Interact` runs the optional item HAS ALREADY BEEN CONSUMED and
    // an optional-item check would refuse the very interaction it was carried
    // for. `checkOptionalItems` is therefore a real, separate concept and is
    // modelled as one below (Refusals takes it), never collapsed into the
    // re-check.
    //
    // Only two vanilla classes read it, both Anomaly, both wanting Shards:
    // `CompObeliskDeactivationInteractor` (Props.shardsRequired) and
    // `CompGoldenCube` (one). The base method ignores the parameter entirely.
    //
    // ======================== THE GATE, CLAUSE BY CLAUSE =====================
    // `CompInteractable.CanInteract(Pawn activateBy = null, bool
    // checkOptionalItems = true)` -> AcceptanceReport. Verified against the
    // decompiled 1.6 source rather than transcribed; the issue's chain stopped
    // at "and more past line 206" and there are exactly two more.
    //
    //   1  Active                                  -> "AlreadyActive"
    //   2  OnCooldown                              -> Props.onCooldownString + " (DurationLeft)"
    //   3  refuelable != null && !HasFuel          -> refuelable.Props.outOfFuelMessage ?? "NoFuel"
    //   4  Props.requiresPower && power && !PowerOn-> "NoPower"
    //   -- everything below only when activateBy != null --
    //   5  activateBy.Dead                         -> "PawnIsDead"
    //   6  Props.activateStat disabled for pawn
    //      OR ANY OF ITS statFactors disabled      -> "Incapable"
    //   7  parent.PositionHeld.IsForbidden(pawn)   -> "CannotPrioritizeForbiddenOutsideAllowedArea"
    //         && !Drafted && !Props.ignoreForbidden      + ": " + area label
    //   8  parent.IsForbidden(pawn)                -> "CannotPrioritizeCellForbidden"
    //         && !Drafted && !Props.ignoreForbidden
    //   9  activateBy.Downed                       -> "MessageRitualPawnDowned"
    //  10  activateBy.Deathresting                 -> "IsDeathresting"
    //  11  !CapableOf(Manipulation)                -> "MessageIncapableOfManipulation"
    //  12  !CanReach(parent.SpawnedParentOrMe,
    //         ClosestTouch, Deadly)                -> "CannotReach"
    //
    // Clause 3's message comes off the REFUELABLE's props, not the
    // interactable's — `refuelable.Props.outOfFuelMessage`. The issue read it as
    // `Props.outOfFuelMessage`; there is no such field on
    // CompProperties_Interactable.
    //
    // CLAUSES 7 AND 8 ARE GENUINELY DIFFERENT QUESTIONS, which is why they get
    // two gates. RimWorld/ForbidUtility.cs: `IntVec3.IsForbidden(Pawn)` is the
    // ALLOWED-AREA test (plus the squad-flag distance), while
    // `Thing.IsForbidden(Pawn)` is the forbid FLAG (plus faction, plus lord
    // reservations) and happens to re-test the position inside itself. 7 fires
    // first and says which area; 8 is the red-X. Both skip on
    // `activateBy.Drafted` or `Props.ignoreForbidden`, and BOTH escapes are
    // reported on the refusal so the agent knows drafting would clear it.
    //
    // MANIPULATION IS A REAL GATE HERE, unlike on the `use` surface. `use`
    // publishes it as an advisory because the check lives inside
    // `JobDriver_UseItem` while `FloatMenuOptionProvider_FromThing`'s
    // `RequiresManipulation` is false; clause 11 is in `CanInteract` itself, so
    // the game's own option is disabled and so is ours.
    //
    // CLAUSE 7's REASON IS A PawnSafe CLASS D HAZARD. Vanilla interpolates
    // `activateBy.playerSettings.EffectiveAreaRestrictionInPawnCurrentMap.Label`,
    // whose getter does `allowedAreas.TryGetValue(pawn.MapHeld, …)` with no null
    // guard. The guarded sibling `AreaRestrictionInPawnCurrentMap` is used
    // instead, exactly as `extinguish` and `orders` already do.
    //
    // ================== THE SUBCLASS CLAUSES THAT MATTER ====================
    // `CompAnalyzable.CanInteract` calls base and then adds three of its own —
    // these are the ones the mech run actually hits:
    //
    //  13  !Props.allowRepeatAnalysis && details.Satisfied -> "AlreadyAnalyzed"
    //  14  pawn && StatDefOf.ResearchSpeed disabled for it -> "Incapable"
    //  15  pawn && !canStudyInPlace && no reachable bench  -> "NoResearchBench"
    //  16  no pawn && !canStudyInPlace && the COLONY has
    //      no research bench at all                       -> "NoResearchBench"
    //
    // and `CompAnalyzableUnlockResearch` adds one more:
    //
    //  17  pawn && Props.requiresMechanitor
    //      && !MechanitorUtility.IsMechanitor(pawn)       -> "RequiresMechanitor"
    //
    // 14 and 6 both answer "Incapable" in the game's words. That is precisely
    // why the gate NAME carries the meaning and the reason carries the game's
    // phrasing — two clauses, one string, two gates.
    //
    // All three chips (SignalChip / PowerfocusChip / NanostructuringChip) ship
    // `requiresMechanitor: true` and DO NOT ship `canStudyInPlace`, so 15 and 17
    // are both live on this bench: the analysis needs a mechanitor AND a
    // reachable, powered, reservable research bench, and the item is HAULED to
    // it (JobDriver_StudyItem's TargetIndex.B/C).
    //
    // ==================== THE TARGET IS THE ACTIVATING PAWN =================
    // The issue expected `interact {pawn, thing, target?}` and flagged an
    // overlap with git-bug 826d4bf (`use {target}`). There is no overlap.
    // `CompInteractable` is an `ITargetingSource` whose `targetParams` is
    // `Props.targetingParameters`, and its `OrderForceTarget(LocalTargetInfo
    // target)` opens `target.Pawn.jobs.TryTakeOrderedJob(...)` — the thing being
    // pointed at is WHO SHOULD DO IT, not what the interaction is aimed at.
    // `ValidateTarget` refuses anything with `target.Pawn == null`, and
    // `OnGUI`'s mouse label is literally "ChooseWhoShouldActivate" (the chips
    // override it to "Choose who should analyze this"). So `interact
    // {pawn, thing}` IS the targeted form, and no `target` argument exists to
    // add. `interact` refuses a stray `target` key up front and says so.
    //
    // The real second-targeter cases are elsewhere and are refused by route (see
    // below): `CompNociosphere` begins a SECOND targeting session for a
    // destination cell, and `CompInteractableRocketswarmLauncher` begins one on
    // the turret's attack verb.
    //
    // ========================= THE ORDER, PER ROUTE =========================
    // `OrderForceTarget` is virtual and seven vanilla classes override it, so
    // "make an InteractThing job" is NOT the route — it is one of them. The
    // route is selected off the DECLARING TYPE of the override, by reflection,
    // and only the two we can reproduce faithfully are driven:
    //
    //  BASE (CompInteractable.OrderForceTarget)
    //      ValidateTarget, then JobMaker.MakeJob(JobDefOf.InteractThing, parent)
    //      and TryTakeOrderedJob(job, JobTag.Misc). No count, no playerForced.
    //
    //  ANALYZE (CompAnalyzable.OrderForceTarget)
    //      ValidateTarget && (canStudyInPlace || StudyUtility
    //      .TryFindResearchBench(pawn, out bench)), then — and only then —
    //      `parent.TryGetComp<CompForbiddable>().Forbidden = false`, then
    //      JobMaker.MakeJob(JobDefOf.AnalyzeItem, parent, bench,
    //      bench?.Position ?? IntVec3.Invalid).
    //
    //      THE UN-FORBID HAPPENS AFTER THE GATE, WHICH IS THE OPPOSITE OF
    //      `use`. UsableSafe un-forbids BEFORE ordering because no forbidden
    //      clause exists anywhere on the CompUsable route. Here clauses 7 and 8
    //      are IN the gate, so a forbidden chip and an undrafted pawn is a
    //      refusal the player gets too — and clearing the flag first would
    //      fabricate a permission. Draft the pawn, or clear the flag with a
    //      designation verb, then interact.
    //
    //  EVERYTHING ELSE -> refused, gate `unsupported-route`, per-class reason.
    //      CompGoldenCube opens a Dialog_MessageBox confirmation (forcePause —
    //      the exact wedge d318d4a hit, and `dialog-choose` cannot answer a
    //      Dialog_MessageBox because it has buttonAAction/buttonBAction and no
    //      DiaOptions); CompNociosphere and CompInteractableRocketswarmLauncher
    //      arm Find.Targeter and wait for a click that never comes on a headless
    //      bench; CompObeliskDeactivationInteractor hauls Props.shardsRequired
    //      Shards as a targetQueueB with job.interactableIndex = 1;
    //      CompLabyrinthDoor and CompDisableUnnaturalCorpse make a plain
    //      InteractThing job but gate their float menu on a class-specific
    //      widget precondition (Building_JammedDoor.Jammed, UnnaturalCorpse
    //      .Tracker.CanDestroyViaResearch) that this file has not verified in
    //      play. Naming what we do not drive beats guessing at it.
    //
    // ===================== NO MODAL IS OPENED BY THIS VERB ==================
    // Checked, not assumed. Neither reproduced route calls Find.WindowStack.Add,
    // and neither `CompAnalyzable.OnAnalyzed` nor `CompAnalyzableUnlockResearch`
    // does either — the completion is `Find.LetterStack.ReceiveLetter`, which is
    // a LETTER. That still stops the orchestrator: `advance` halts on unread
    // news unless `through_news` is passed, and the halt lands DURING the
    // advance that finishes the analysis. Published as `advance_halt` rather
    // than discovered. The only interactables that DO raise a force-pausing
    // Dialog_NodeTree from OnInteracted are CompVoidNode and CompCerebrexCore;
    // both take the base route, so both get an `advance_halt` naming
    // `dialog-choose` as the answer.
    internal static class InteractableSafe
    {
        // ---------------------------- gate names -----------------------------
        public const string GateNotInteractable = "not-interactable";
        public const string GateAmbiguous = "ambiguous-interactable";
        public const string GateHidden = "hidden-interaction";
        // clauses 1-12, CompInteractable.CanInteract
        public const string GateAlreadyActive = "already-active";
        public const string GateOnCooldown = "on-cooldown";
        public const string GateNoFuel = "no-fuel";
        public const string GateNoPower = "no-power";
        public const string GateDead = "dead";
        public const string GateActivateStat = "activate-stat";
        public const string GateOutsideArea = "outside-allowed-area";
        public const string GateForbidden = "forbidden";
        public const string GateDowned = "downed";
        public const string GateDeathresting = "deathresting";
        public const string GateManipulation = "manipulation";
        public const string GateNoPath = "no-path";
        // clauses 13-16, CompAnalyzable.CanInteract
        public const string GateAlreadyAnalyzed = "already-analyzed";
        public const string GateResearchSpeed = "research-speed";
        public const string GateNoResearchBench = "no-research-bench";
        // clause 17, CompAnalyzableUnlockResearch.CanInteract
        public const string GateMechanitor = "mechanitor";
        // the rest
        public const string GateOverride = "interactable-override";
        public const string GateUnsupportedRoute = "unsupported-route";

        // ------------------------------ routes -------------------------------
        public const string RouteBase = "interact-thing";
        public const string RouteAnalyze = "analyze-item";
        public const string RouteUnsupported = "unsupported";

        // A single refused clause. Same three keys as UsableSafe.Refusal so the
        // two surfaces read alike, plus `cite` — the member the clause was read
        // off, which DESIGN's action model requires a player verb to name and
        // which a comment in this file cannot put in the envelope.
        internal sealed class Refusal
        {
            public string Gate;
            public string Reason;
            public bool FromGame;
            public string Cite;
            public Dictionary<string, object> Extra;

            public Refusal(string gate, string reason, bool fromGame, string cite,
                Dictionary<string, object> extra = null)
            { Gate = gate; Reason = reason; FromGame = fromGame; Cite = cite; Extra = extra; }

            public Dictionary<string, object> Row()
            {
                var d = new Dictionary<string, object>
                {
                    ["gate"] = Gate,
                    ["reason"] = Reason,
                    // Whose words the reason is. Several clauses here have no
                    // string of the game's at all (the route refusals, the
                    // ambiguity gate), and a phrase of ours must never be
                    // quoted back as RimWorld's — PawnActs.Tr's rule.
                    ["reason_from"] = FromGame ? "game" : "autorimmer",
                    ["cite"] = Cite,
                };
                if (Extra != null) foreach (var kv in Extra) d[kv.Key] = kv.Value;
                return d;
            }
        }

        // ------------------------------ lookup -------------------------------

        // EVERY CompInteractable on the thing, because a thing may carry more
        // than one and vanilla disambiguates with `job.interactableIndex`: the
        // void obelisk has both a CompObeliskTriggerInteractor and a
        // CompObeliskDeactivationInteractor, and which gizmo you clicked is the
        // only thing that says which you meant. A verb has no click, so more
        // than one is REFUSED by name rather than silently resolved to the
        // first (which is what `TryGetComp<CompInteractable>()` would do, and
        // what JobDriver_InteractThing does when interactableIndex is -1).
        public static List<CompInteractable> Comps(Thing t)
        {
            var list = new List<CompInteractable>();
            try
            {
                var all = (t as ThingWithComps)?.AllComps;
                if (all == null) return list;
                for (int i = 0; i < all.Count; i++)
                    if (all[i] is CompInteractable c) list.Add(c);
            }
            catch { }
            return list;
        }

        public static CompInteractable Comp(Thing t)
        {
            var list = Comps(t);
            return list.Count > 0 ? list[0] : null;
        }

        // ============ THE CHAIN, IN THE GAME'S OWN ORDER ====================
        // `stopAtFirst` mirrors the game (CanInteract returns on the first
        // refusal) for the act path; false runs the whole chain so
        // `interact-options` publishes every reason at once instead of the agent
        // peeling them off one round trip at a time.
        //
        // `checkOptionalItems` is passed through as its own concept — the base
        // method ignores it, two Anomaly subclasses read it, and the act path
        // asks with TRUE because that is what the float menu and ValidateTarget
        // ask. See the header on why `Interact` later asks with FALSE.
        public static List<Refusal> Refusals(CompInteractable comp, Pawn p, bool stopAtFirst,
            bool checkOptionalItems = true)
        {
            var outp = new List<Refusal>();
            if (comp == null)
            {
                outp.Add(new Refusal(GateNotInteractable, "no CompInteractable on this thing", false,
                    "AutoRimmer/InteractableSafe.Comps"));
                return outp;
            }
            var parent = comp.parent;

            // 0a — the provider gate. RimWorld/FloatMenuOptionProvider_FromThing
            // is Drafted && Undrafted && CanSelfTarget, and inherits the base
            // defaults RequiresManipulation:false, MechanoidCanDo:false. Only
            // the mechanoid clause can fire, and it fires before the comp is
            // ever asked.
            if (p != null && p.RaceProps != null)
            {
                bool mech = false;
                try { mech = p.RaceProps.IsMechanoid; } catch { }
                if (mech)
                {
                    outp.Add(new Refusal("mechanoid",
                        "mechanoids are not offered this order (FloatMenuOptionProvider.MechanoidCanDo "
                        + "is false and FloatMenuOptionProvider_FromThing does not override it)", false,
                        "RimWorld/FloatMenuOptionProvider.SelectedPawnValid"));
                    if (stopAtFirst) return outp;
                }
            }

            // 0b — the widget's own `if`. CompFloatMenuOptions opens
            // `if (!HideInteraction)` and yields NOTHING otherwise: the option
            // does not exist, it is not merely disabled. CompDestroyHeart
            // defines HideInteraction as `!CanInteract()`, so reading it is a
            // read of the gate — safe, and deliberately still read rather than
            // special-cased.
            bool hidden = false;
            try { hidden = comp.HideInteraction; } catch { }
            if (hidden)
            {
                outp.Add(new Refusal(GateHidden,
                    comp.GetType().Name + ".HideInteraction is true, and "
                    + "CompInteractable.CompFloatMenuOptions yields NO option at all in that case — "
                    + "there is no player route to reproduce", false,
                    "RimWorld/CompInteractable.CompFloatMenuOptions"));
                if (stopAtFirst) return outp;
            }

            const string CiteBase = "RimWorld/CompInteractable.CanInteract";

            // 1 — Active. The comp is already mid-interaction (activeTicks > 0).
            bool active = false;
            try { active = comp.Active; } catch { }
            if (active)
            {
                outp.Add(new Refusal(GateAlreadyActive, TrGame("AlreadyActive", "already active"), true,
                    CiteBase));
                if (stopAtFirst) return outp;
            }

            // 2 — OnCooldown, with the game's own remaining-duration suffix.
            // CompUsable has no equivalent of this clause at all.
            bool cooling = false;
            try { cooling = comp.OnCooldown; } catch { }
            if (cooling)
            {
                outp.Add(new Refusal(GateOnCooldown, CooldownReason(comp), true, CiteBase,
                    new Dictionary<string, object>
                    {
                        ["cooldown_ticks_remaining"] = CooldownTicks(comp),
                        ["cooldown_ticks_total"] = SafeInt(() => comp.Props.cooldownTicks),
                    }));
                if (stopAtFirst) return outp;
            }

            // 3 — fuel. Vanilla reads a cached field assigned in PostSpawnSetup;
            // TryGetComp is the same comp for anything spawned, and does not
            // report a false "fuelled" on a comp whose setup never ran.
            try
            {
                var refuelable = parent?.TryGetComp<CompRefuelable>();
                if (refuelable != null && !refuelable.HasFuel)
                {
                    string msg = null;
                    try { msg = refuelable.Props.outOfFuelMessage; } catch { }
                    bool own = !string.IsNullOrEmpty(msg);
                    outp.Add(new Refusal(GateNoFuel,
                        own ? msg : TrGame("NoFuel", "out of fuel"), true, CiteBase));
                    if (stopAtFirst) return outp;
                }
            }
            catch { }

            // 4 — power, and note it is gated on Props.requiresPower: an
            // interactable with a CompPowerTrader but requiresPower false is NOT
            // refused by the game and must not be refused here.
            try
            {
                bool needsPower = false;
                try { needsPower = comp.Props.requiresPower; } catch { }
                if (needsPower && parent != null
                    && parent.TryGetComp<CompPowerTrader>(out var power) && !power.PowerOn)
                {
                    outp.Add(new Refusal(GateNoPower, TrGame("NoPower", "no power"), true, CiteBase));
                    if (stopAtFirst) return outp;
                }
            }
            catch { }

            // ---- clauses 5-12 exist only when a pawn was named ----
            if (p != null)
            {
                // 5 — Dead.
                bool dead = false;
                try { dead = p.Dead; } catch { }
                if (dead)
                {
                    outp.Add(new Refusal(GateDead, TrGameArg("PawnIsDead", p, "this pawn is dead"), true,
                        CiteBase));
                    if (stopAtFirst) return outp;
                }

                // 6 — ONE clause, TWO tests: the activateStat itself being
                // disabled for the pawn, OR any StatDef in its `statFactors`
                // being disabled. Both answer "Incapable", so the row names
                // WHICH stat did it — the game's message cannot.
                var statRefusal = ActivateStatRefusal(comp, p, CiteBase);
                if (statRefusal != null)
                {
                    outp.Add(statRefusal);
                    if (stopAtFirst) return outp;
                }

                bool drafted = false;
                try { drafted = p.Drafted; } catch { }
                bool ignoreForbidden = false;
                try { ignoreForbidden = comp.Props.ignoreForbidden; } catch { }
                bool forbidEscape = drafted || ignoreForbidden;

                // 7 — the ALLOWED-AREA half. ForbidUtility.IsForbidden(IntVec3,
                // Pawn): !InAllowedArea, or outside maxDistToSquadFlag.
                if (!forbidEscape)
                {
                    bool outsideArea = false;
                    try { outsideArea = parent.PositionHeld.IsForbidden(p); } catch { }
                    if (outsideArea)
                    {
                        outp.Add(new Refusal(GateOutsideArea,
                            TrGame("CannotPrioritizeForbiddenOutsideAllowedArea", "outside the allowed area")
                            + ": " + AreaLabel(p), true, CiteBase, ForbidEscapes(drafted, ignoreForbidden)));
                        if (stopAtFirst) return outp;
                    }
                }

                // 8 — the FORBID-FLAG half. ForbidUtility.IsForbidden(Thing,
                // Pawn): the thing's own CompForbiddable, its faction, and lord
                // reservations. A separate clause with a separate string, and
                // the one an item dropped by a trader lands on.
                if (!forbidEscape)
                {
                    bool forbidden = false;
                    try { forbidden = parent.IsForbidden(p); } catch { }
                    if (forbidden)
                    {
                        outp.Add(new Refusal(GateForbidden,
                            TrGame("CannotPrioritizeCellForbidden", "that is forbidden"), true, CiteBase,
                            ForbidEscapes(drafted, ignoreForbidden)));
                        if (stopAtFirst) return outp;
                    }
                }

                // 9 — Downed.
                bool downed = false;
                try { downed = p.Downed; } catch { }
                if (downed)
                {
                    outp.Add(new Refusal(GateDowned,
                        TrGameArg("MessageRitualPawnDowned", p, "this pawn is downed"), true, CiteBase));
                    if (stopAtFirst) return outp;
                }

                // 10 — Deathresting (Biotech).
                bool deathresting = false;
                try { deathresting = p.Deathresting; } catch { }
                if (deathresting)
                {
                    outp.Add(new Refusal(GateDeathresting,
                        TrGameArg("IsDeathresting", p.Named("PAWN"), "this pawn is deathresting"), true,
                        CiteBase));
                    if (stopAtFirst) return outp;
                }

                // 11 — manipulation. A GATE here, not the advisory it is on the
                // `use` surface: this clause is inside CanInteract itself, so
                // the game's own float-menu option is disabled.
                bool canManipulate = false;
                try { canManipulate = p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation); }
                catch { }
                if (!canManipulate)
                {
                    outp.Add(new Refusal(GateManipulation,
                        TrGameArg("MessageIncapableOfManipulation", p, "incapable of manipulation"), true,
                        CiteBase));
                    if (stopAtFirst) return outp;
                }

                // 12 — reach. ClosestTouch to SpawnedParentOrMe, which is not
                // the same question JobDriver_InteractThing's goto toil asks
                // (that one paths to TargetIndex.C with PathEndMode.Touch).
                bool reach = false;
                try { reach = p.CanReach(parent.SpawnedParentOrMe, PathEndMode.ClosestTouch, Danger.Deadly); }
                catch { }
                if (!reach)
                {
                    outp.Add(new Refusal(GateNoPath, TrGame("CannotReach", "cannot reach it"), true, CiteBase));
                    if (stopAtFirst) return outp;
                }
            }

            // ---- 13-17: the CompAnalyzable family, which is why this ships ----
            if (comp is CompAnalyzable analyzable)
            {
                AnalyzableRefusals(analyzable, p, outp, stopAtFirst);
                if (stopAtFirst && outp.Count > 0) return outp;
            }

            // ---- a modded (or unmodelled) override, asked once, by the game ---
            if (OverridesCanInteract(comp))
            {
                AcceptanceReport rep;
                try { rep = comp.CanInteract(p, checkOptionalItems); }
                catch (Exception ex)
                {
                    outp.Add(new Refusal(GateOverride,
                        comp.GetType().Name + ".CanInteract threw: " + ex.GetType().Name + ": " + ex.Message,
                        false, comp.GetType().Name + ".CanInteract"));
                    return outp;
                }
                if (!rep.Accepted && outp.Count == 0)
                {
                    string why = rep.Reason;
                    outp.Add(new Refusal(GateOverride,
                        string.IsNullOrEmpty(why)
                            ? comp.GetType().Name + " overrides CanInteract and refused with no reason string"
                            : why,
                        !string.IsNullOrEmpty(why), comp.GetType().Name + ".CanInteract"));
                }
            }

            return outp;
        }

        // The act path's question: the first refusal, or null when accepted.
        public static Refusal Gate(CompInteractable comp, Pawn p)
        {
            var list = Refusals(comp, p, stopAtFirst: true);
            return list.Count > 0 ? list[0] : null;
        }

        // ------------------ CompAnalyzable's four + one clauses ---------------
        private static void AnalyzableRefusals(CompAnalyzable comp, Pawn p, List<Refusal> outp,
            bool stopAtFirst)
        {
            const string Cite = "RimWorld/CompAnalyzable.CanInteract";
            int id = -1;
            try { id = comp.AnalysisID; } catch { }

            // 13 — AlreadyAnalyzed. NOTE the guard: `details != null`, so a comp
            // whose analysis task was never registered (PostSpawnSetup adds it)
            // does NOT refuse here.
            try
            {
                bool repeat = comp.Props.allowRepeatAnalysis;
                AnalysisDetails details = null;
                try { Find.AnalysisManager.TryGetAnalysisProgress(id, out details); } catch { }
                if (!repeat && details != null && details.Satisfied)
                {
                    outp.Add(new Refusal(GateAlreadyAnalyzed,
                        TrGame("AlreadyAnalyzed", "already analyzed"), true, Cite,
                        new Dictionary<string, object>
                        {
                            ["analysis_id"] = id,
                            ["times_done"] = details.timesDone,
                            ["required"] = details.required,
                        }));
                    if (stopAtFirst) return;
                }
            }
            catch { }

            if (p != null)
            {
                // 14 — ResearchSpeed disabled. Same "Incapable" string as clause
                // 6 and a different gate, because it is a different fact:
                // clause 6 is Props.activateStat (null on every chip), this one
                // is hardcoded StatDefOf.ResearchSpeed.
                bool researchDisabled = false;
                try { researchDisabled = StatDefOf.ResearchSpeed.Worker.IsDisabledFor(p); } catch { }
                if (researchDisabled)
                {
                    outp.Add(new Refusal(GateResearchSpeed,
                        TrGame("Incapable", "incapable"), true, Cite,
                        new Dictionary<string, object>
                        {
                            ["stat"] = "ResearchSpeed",
                            ["detail"] = "CompAnalyzable.CanInteract refuses when StatDefOf.ResearchSpeed "
                                + "is disabled for the pawn — an Intellectual work-tag or backstory "
                                + "restriction, NOT the same clause as Props.activateStat",
                        }));
                    if (stopAtFirst) return;
                }
            }

            // 15/16 — the research bench, in two forms the game itself splits:
            // with a pawn it must be one THAT PAWN can reach and reserve and
            // that is powered; with no pawn it is merely "does the colony have
            // one". Same string, and the distinction is reported.
            bool inPlace = false;
            try { inPlace = comp.Props.canStudyInPlace; } catch { }
            if (!inPlace)
            {
                if (p != null)
                {
                    bool found = false;
                    try { found = StudyUtility.TryFindResearchBench(p, out _); } catch { }
                    if (!found)
                    {
                        outp.Add(new Refusal(GateNoResearchBench,
                            TrGame("NoResearchBench", "no research bench"), true, Cite,
                            new Dictionary<string, object>
                            {
                                ["detail"] = "StudyUtility.TryFindResearchBench found no bench this pawn "
                                    + "can REACH and RESERVE that is also powered "
                                    + "(GenClosest over ThingRequestGroup.ResearchBench, PathEndMode "
                                    + "InteractionCell, Danger.Some). The item is HAULED to the bench — "
                                    + "JobDriver_StudyItem carries TargetA there when canStudyInPlace is "
                                    + "false — so the bench is not optional scenery.",
                            }));
                        if (stopAtFirst) return;
                    }
                }
                else
                {
                    bool any = false;
                    try { any = comp.parent.MapHeld.listerBuildings.ColonistsHaveResearchBench(); } catch { }
                    if (!any)
                    {
                        outp.Add(new Refusal(GateNoResearchBench,
                            TrGame("NoResearchBench", "no research bench"), true, Cite,
                            new Dictionary<string, object>
                            {
                                ["detail"] = "the no-pawn form of the clause: the colony has no research "
                                    + "bench at all (ListerBuildings.ColonistsHaveResearchBench). Pass a "
                                    + "`pawn` for the stricter per-pawn form.",
                            }));
                        if (stopAtFirst) return;
                    }
                }
            }

            // 17 — the mechanitor clause, on CompAnalyzableUnlockResearch only,
            // and the one that gates all three mech chips.
            if (comp is CompAnalyzableUnlockResearch unlock && p != null)
            {
                bool needs = false;
                try { needs = unlock.Props.requiresMechanitor; } catch { }
                if (needs)
                {
                    bool isMech = false;
                    try { isMech = MechanitorUtility.IsMechanitor(p); } catch { }
                    if (!isMech)
                    {
                        outp.Add(new Refusal(GateMechanitor,
                            TrGame("RequiresMechanitor", "requires a mechanitor"), true,
                            "RimWorld/CompAnalyzableUnlockResearch.CanInteract",
                            new Dictionary<string, object>
                            {
                                ["detail"] = "MechanitorUtility.IsMechanitor is ShouldBeMechanitor(pawn) "
                                    + "&& pawn.mechanitor != null — i.e. the pawn carries a MechlinkImplant "
                                    + "hediff. `use {pawn, thing:<Mechlink>}` is how one is made.",
                            }));
                        if (stopAtFirst) return;
                    }
                }
            }
        }

        // Clause 6 in full: the stat, then every statFactor. Reported as ONE
        // gate because the game has one clause, with the offending stat named
        // because the game's "Incapable" does not name it.
        private static Refusal ActivateStatRefusal(CompInteractable comp, Pawn p, string cite)
        {
            StatDef stat = null;
            try { stat = comp.Props.activateStat; } catch { }
            if (stat == null) return null;
            try
            {
                if (stat.Worker.IsDisabledFor(p))
                    return new Refusal(GateActivateStat, TrGame("Incapable", "incapable"), true, cite,
                        new Dictionary<string, object>
                        {
                            ["stat"] = stat.defName,
                            ["disabled"] = "the activateStat itself",
                        });
            }
            catch { }
            try
            {
                var factors = stat.statFactors;
                if (factors != null)
                    for (int i = 0; i < factors.Count; i++)
                    {
                        var f = factors[i];
                        if (f == null) continue;
                        bool off = false;
                        try { off = f.Worker.IsDisabledFor(p); } catch { }
                        if (off)
                            return new Refusal(GateActivateStat, TrGame("Incapable", "incapable"), true, cite,
                                new Dictionary<string, object>
                                {
                                    ["stat"] = stat.defName,
                                    ["disabled"] = "statFactor " + f.defName,
                                    ["detail"] = "the SECOND half of one clause: CanInteract refuses when "
                                        + "Props.activateStat is disabled OR when any StatDef in its "
                                        + "statFactors is. Same refusal string either way.",
                                });
                    }
            }
            catch { }
            return null;
        }

        private static Dictionary<string, object> ForbidEscapes(bool drafted, bool ignoreForbidden)
            => new Dictionary<string, object>
            {
                // Both are false here by construction (the clause is skipped
                // otherwise); they are published so the agent reads the ESCAPE
                // off the refusal rather than off this file's source.
                ["drafted"] = drafted,
                ["ignore_forbidden"] = ignoreForbidden,
                ["escape"] = "BOTH forbidden clauses are skipped entirely when the pawn is DRAFTED or when "
                    + "CompProperties_Interactable.ignoreForbidden is set on the def. Draft the pawn, or "
                    + "clear the forbid flag, and this clause stops firing.",
            };

        // ============================ THE ROUTE ==============================
        // Which OrderForceTarget the game would have run, by the DECLARING TYPE
        // of the override — never by a class-name string, so a modded subclass
        // of CompAnalyzable that does not re-override still takes the analyze
        // route and a modded one that DOES is refused.
        public static string Route(CompInteractable comp)
        {
            if (comp == null) return RouteUnsupported;
            var declaring = OrderForceTargetDeclaringType(comp.GetType());
            if (declaring == typeof(CompInteractable)) return RouteBase;
            if (declaring == typeof(CompAnalyzable)) return RouteAnalyze;
            return RouteUnsupported;
        }

        // The refusal for a route we will not drive, with the reason NAMED per
        // class rather than a shrug. Every one of these was read off the
        // decompiled source; an unlisted override gets the generic form.
        public static Refusal RouteRefusal(CompInteractable comp)
        {
            string cls = comp?.GetType()?.Name ?? "the comp";
            string why;
            switch (cls)
            {
                case "CompGoldenCube":
                    why = "CompGoldenCube.OrderDeactivation ends in "
                        + "Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(...)) — a forcePause "
                        + "window. Per spec 1.7 that halts EVERY later advance at 0 ticks with "
                        + "reason:\"dialog\", and no shipped verb can press its Accept: `dialog-choose` "
                        + "reads a Dialog_NodeTree's options and a Dialog_MessageBox has "
                        + "buttonAAction/buttonBAction instead, while `dialog-dismiss` CANCELS it.";
                    break;
                case "CompNociosphere":
                    why = "CompNociosphere.TargetLocation calls Find.Targeter.BeginTargeting for a "
                        + "DESTINATION CELL before it ever orders the job, and that cursor consumes a click "
                        + "that never comes on a headless bench.";
                    break;
                case "CompInteractableRocketswarmLauncher":
                    why = "CompInteractableRocketswarmLauncher.TargetLocation calls "
                        + "Find.Targeter.BeginTargeting on the turret's own attack verb and only orders the "
                        + "job from the targeter's callback — a second click this verb cannot supply.";
                    break;
                case "CompObeliskDeactivationInteractor":
                    why = "CompObeliskDeactivationInteractor.OrderDeactivation hauls Props.shardsRequired "
                        + "Shards: HaulAIUtility.FindFixedIngredientCount, then an InteractThing job with a "
                        + "targetQueueB, job.count and job.interactableIndex = 1. That is a different job "
                        + "shape with its own optional-item accounting, not the base route.";
                    break;
                case "CompLabyrinthDoor":
                    why = "CompLabyrinthDoor makes a plain InteractThing job, but its float menu is gated on "
                        + "Building_JammedDoor.Jammed — a widget precondition above CanInteract that this "
                        + "verb has not verified in play. Reproducing the job without the precondition would "
                        + "offer an order the player is not offered.";
                    break;
                case "CompDisableUnnaturalCorpse":
                    why = "CompDisableUnnaturalCorpse makes a plain InteractThing job, but its float menu is "
                        + "gated on UnnaturalCorpse.Tracker.CanDestroyViaResearch — a widget precondition "
                        + "above CanInteract that this verb has not verified in play.";
                    break;
                default:
                    why = cls + " overrides CompInteractable.OrderForceTarget with a route this verb does "
                        + "not reproduce. Only the base route (JobDefOf.InteractThing) and "
                        + "CompAnalyzable's (JobDefOf.AnalyzeItem) are driven; accepting an order whose "
                        + "real route opens UI or hauls an ingredient would complete a job that does "
                        + "nothing.";
                    break;
            }
            return new Refusal(GateUnsupportedRoute, why, false,
                cls + ".OrderForceTarget (vs RimWorld/CompInteractable.OrderForceTarget)");
        }

        // ==================== StartJob, REPRODUCED PER ROUTE =================
        // BASE — CompInteractable.OrderForceTarget:
        //     Job job = JobMaker.MakeJob(JobDefOf.InteractThing, parent);
        //     target.Pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        // No `count`, no `playerForced` — the two classes that set them are the
        // ones we refuse. The take is the CALLER's, through PawnActs.TakeOrder,
        // so `queue` reaches TryTakeOrderedJob's requestQueueing parameter (the
        // same reason UsableSafe.MakeJob stops short of the take).
        //
        // ANALYZE — CompAnalyzable.OrderForceTarget:
        //     JobMaker.MakeJob(JobDefOf.AnalyzeItem, parent, bench,
        //                      bench?.Position ?? IntVec3.Invalid)
        // with `bench` null exactly when Props.canStudyInPlace. TargetB is the
        // bench and TargetC the cell the item is placed in
        // (JobDriver_StudyItem: GotoThing A, StartCarryThing A, GotoThing B at
        // InteractionCell, PlaceHauledThingInCell C, then
        // JobDriver_AnalyzeItem's wait toil scaled by the pawn's ResearchSpeed).
        // Returns NULL exactly where vanilla's OrderForceTarget would have done
        // nothing at all — the bench lookup failing — so the caller refuses
        // instead of reporting an order that was never taken.
        public static Job MakeJob(CompInteractable comp, Pawn p, out Building_ResearchBench bench)
        {
            bench = null;
            var parent = comp.parent;
            if (Route(comp) != RouteAnalyze)
                return JobMaker.MakeJob(JobDefOf.InteractThing, parent);

            bool inPlace = false;
            try { inPlace = ((CompAnalyzable)comp).Props.canStudyInPlace; } catch { }
            if (!inPlace)
            {
                bool found = false;
                try { found = StudyUtility.TryFindResearchBench(p, out bench); } catch { }
                // Vanilla's own second guard: OrderForceTarget does NOTHING at
                // all when the bench lookup fails, which is a silent no-op the
                // caller must not read as an order.
                if (!found) return null;
            }
            return JobMaker.MakeJob(JobDefOf.AnalyzeItem, parent, bench,
                bench != null ? (LocalTargetInfo)bench.Position : LocalTargetInfo.Invalid);
        }

        // The un-forbid CompAnalyzable.OrderForceTarget does — AFTER the gate,
        // never before it. Returns whether the flag was actually cleared.
        public static bool ClearForbidden(CompInteractable comp)
        {
            try
            {
                var forbiddable = comp?.parent?.TryGetComp<CompForbiddable>();
                if (forbiddable == null) return false;
                if (!forbiddable.Forbidden) return false;
                forbiddable.Forbidden = false;
                return true;
            }
            catch { return false; }
        }

        // ===================== WHAT WILL STOP THE ADVANCE ====================
        // Named at order time, because the halt lands during the advance that
        // runs the job and an orchestrator who did not expect it reads it as a
        // hang. Two different halts, two different answers.
        public static string AdvanceHalt(CompInteractable comp)
        {
            if (comp is CompAnalyzable analyzable)
            {
                string label = null;
                try { label = analyzable.Props.completedLetterLabel; } catch { }
                bool destroys = false;
                try { destroys = analyzable.Props.destroyedOnAnalyzed; } catch { }
                return "THIS INTERACTION WILL STOP THE ADVANCE THAT COMPLETES IT, with a NEWS halt, not a "
                    + "dialog. CompAnalyzable.OnAnalyzed ends in Find.LetterStack.ReceiveLetter — a letter, "
                    + "so nothing is force-pausing and nothing needs dismissing, but `advance` halts on "
                    + "unread news unless you pass `through_news:\"<why>\"`. "
                    + (string.IsNullOrEmpty(label) ? "" : "Expect the letter \"" + label + "\". ")
                    + (destroys
                        ? "The item IS destroyed on completion (Props.destroyedOnAnalyzed)."
                        : "The item is NOT destroyed (Props.destroyedOnAnalyzed is false) — it stays on the "
                          + "map, and a second analysis is refused by `already-analyzed` unless the def sets "
                          + "allowRepeatAnalysis.");
            }
            string cls = comp?.GetType()?.Name;
            if (cls == "CompVoidNode" || cls == "CompCerebrexCore")
                return "THIS INTERACTION WILL HALT THE NEXT ADVANCE. " + cls
                    + " raises a Dialog_NodeTree from its interaction, which is forcePause — expect "
                    + "advance to stop with reason:\"dialog\". That one IS a Dialog_NodeTree, so "
                    + "`dialog-choose` answers it.";
            return null;
        }

        // ======================= READ-ONLY DESCRIPTION =======================
        // The per-thing facts `interact-options` publishes and `interact` echoes,
        // in one place so a refusal and an acceptance describe the target
        // identically.
        public static Dictionary<string, object> Describe(CompInteractable comp)
        {
            var d = new Dictionary<string, object>
            {
                ["comp"] = comp.GetType().Name,
                ["route"] = Route(comp),
                ["job_string"] = SafeObj(() => (object)Resolve(comp.Props.jobString)),
                ["activate_label"] = SafeObj(() => (object)Resolve(comp.Props.activateLabelString)),
                ["active"] = SafeObj(() => (object)comp.Active),
                ["on_cooldown"] = SafeObj(() => (object)comp.OnCooldown),
                ["cooldown_ticks_remaining"] = CooldownTicks(comp),
                ["cooldown_ticks_total"] = SafeInt(() => comp.Props.cooldownTicks),
                ["ticks_to_activate"] = SafeInt(() => comp.TicksToActivate),
                ["active_ticks"] = SafeInt(() => comp.Props.activeTicks),
                ["requires_power"] = SafeObj(() => (object)comp.Props.requiresPower),
                ["ignore_forbidden"] = SafeObj(() => (object)comp.Props.ignoreForbidden),
                ["activate_stat"] = SafeObj(() => (object)comp.Props.activateStat?.defName),
                ["hide_interaction"] = SafeObj(() => (object)comp.HideInteraction),
                // ITargetingSource. Both are CONSTANTS on CompInteractable
                // (`Targetable => true`, `MultiSelect => false`), and publishing
                // them without saying what the target IS would invite the wrong
                // reading — see `target_note`.
                ["targetable"] = SafeObj(() => (object)comp.Targetable),
                ["multi_select"] = SafeObj(() => (object)comp.MultiSelect),
                ["target_params"] = TargetParams(comp),
                ["target_note"] = "`targetable` is TRUE on every CompInteractable and it does NOT mean the "
                    + "interaction is aimed at something. CompInteractable.OrderForceTarget takes the "
                    + "ACTIVATING PAWN as its target (ValidateTarget refuses target.Pawn == null, and the "
                    + "cursor label is \"ChooseWhoShouldActivate\"), so `interact {pawn, thing}` already IS "
                    + "the targeted form and there is no second target to pass.",
            };
            if (comp is CompAnalyzable analyzable) d["analysis"] = Analysis(analyzable);
            string halt = AdvanceHalt(comp);
            if (halt != null) d["advance_halt"] = halt;
            return d;
        }

        // The analysis ledger, read off Find.AnalysisManager — the same numbers
        // `ResearchProjectDef.AnalyzedThingsCompleted` reads, so `research-set`'s
        // `blocked_by:"analysis"` and this block cannot disagree.
        public static Dictionary<string, object> Analysis(CompAnalyzable comp)
        {
            int id = -1;
            try { id = comp.AnalysisID; } catch { }
            AnalysisDetails details = null;
            try { Find.AnalysisManager.TryGetAnalysisProgress(id, out details); } catch { }
            var d = new Dictionary<string, object>
            {
                ["analysis_id"] = id,
                ["required"] = details != null ? (object)details.required : null,
                ["times_done"] = details != null ? (object)details.timesDone : null,
                ["satisfied"] = details != null ? (object)details.Satisfied : null,
                ["duration_hours"] = SafeObj(() => (object)comp.Props.analysisDurationHours),
                ["can_study_in_place"] = SafeObj(() => (object)comp.Props.canStudyInPlace),
                ["destroyed_on_analyzed"] = SafeObj(() => (object)comp.Props.destroyedOnAnalyzed),
                ["allow_repeat"] = SafeObj(() => (object)comp.Props.allowRepeatAnalysis),
            };
            if (details == null)
                d["detail"] = "no AnalysisDetails registered for this id. CompAnalyzable.PostSpawnSetup "
                    + "registers one on first spawn (AddAnalysisTask with Props.analysisRequiredRange), so "
                    + "a null here means the comp never spawned — and note the AlreadyAnalyzed clause is "
                    + "guarded on `details != null`, so it does not fire either.";
            if (comp is CompAnalyzableUnlockResearch unlock)
            {
                d["requires_mechanitor"] = SafeObj(() => (object)unlock.Props.requiresMechanitor);
                // ResearchUnlocked IS a lazy-init getter, and it is safe: the
                // field it fills is a plain private cache with no ExposeData
                // entry, and what it caches is a DefDatabase sweep — pure def
                // data, the same list CompAnalyzableUnlockResearch.ExtraNamedArg
                // builds for every gizmo tooltip and completion letter. Nothing
                // scribed is created or written. Named rather than left for the
                // next reviewer to re-derive, because the project's standing
                // rule is that lazy-init getters are guilty until checked.
                var unlocks = new List<object>();
                try
                {
                    var list = unlock.ResearchUnlocked;
                    if (list != null) for (int i = 0; i < list.Count; i++) unlocks.Add(list[i]?.defName);
                }
                catch { }
                d["research_unlocked"] = unlocks;
                d["research_note"] = "these are the ResearchProjectDefs whose `requiredAnalyzed` names this "
                    + "def. Until `satisfied` is true, `research-set` on any of them answers "
                    + "blocked_by:\"analysis\" — ResearchProjectDef.AnalyzedThingsCompleted reads the same "
                    + "AnalysisManager row this block does.";
            }
            return d;
        }

        private static Dictionary<string, object> TargetParams(CompInteractable comp)
        {
            TargetingParameters tp = null;
            try { tp = comp.targetParams; } catch { }
            if (tp == null) return null;
            return new Dictionary<string, object>
            {
                ["only_controlled_pawns"] = SafeObj(() => (object)tp.onlyTargetControlledPawns),
                ["only_colonists"] = SafeObj(() => (object)tp.onlyTargetColonists),
                ["can_target_mechs"] = SafeObj(() => (object)tp.canTargetMechs),
                ["can_target_animals"] = SafeObj(() => (object)tp.canTargetAnimals),
                ["note"] = "Props.targetingParameters filters the GIZMO route's cursor "
                    + "(Find.Targeter validates the click against it, and CompInteractable.OnGUI draws the "
                    + "cannot-shoot icon when it fails). The FLOAT-MENU route — the one this verb "
                    + "reproduces — calls OrderForceTarget(selPawn) directly and never consults it, so it "
                    + "is published as a FACT, not applied as a gate.",
            };
        }

        // Whether the targeter WOULD have accepted this pawn. Advisory only, for
        // the reason above; a refusal here would fabricate a restriction the
        // float menu does not have.
        public static string TargetParamsAdvisory(CompInteractable comp, Pawn p)
        {
            if (p == null) return null;
            try
            {
                var tp = comp.targetParams;
                if (tp == null) return null;
                if (tp.CanTarget(new TargetInfo(p), comp)) return null;
                return "Props.targetingParameters would NOT accept this pawn on the gizmo route "
                    + "(TargetingParameters.CanTarget is false for it), but the order was not refused: "
                    + "CompInteractable.CompFloatMenuOptions calls OrderForceTarget(selPawn) directly and "
                    + "never checks them. Reported so the difference between the game's two routes is "
                    + "visible rather than silently resolved.";
            }
            catch { return null; }
        }

        // ------------------------- reason plumbing ---------------------------

        // `Props.onCooldownString + " (" + "DurationLeft".Translate(
        //  cooldownTicks.ToStringTicksToPeriod()) + ")"`, reproduced.
        private static string CooldownReason(CompInteractable comp)
        {
            string head = null;
            try { head = comp.Props.onCooldownString; } catch { }
            if (string.IsNullOrEmpty(head)) head = "on cooldown";
            int ticks = CooldownTicks(comp);
            if (ticks < 0) return head;
            try
            {
                var left = "DurationLeft".Translate(ticks.ToStringTicksToPeriod()).Resolve();
                if (!left.NullOrEmpty() && !left.Contains("DurationLeft"))
                    return head + " (" + left + ")";
            }
            catch { }
            return head + " (" + ticks.ToStringTicksToPeriod() + " left)";
        }

        // `cooldownTicks` is `protected int` on CompInteractable, and the
        // acceptance asks for it by name. Field ref, bound once.
        private static bool cooldownRefTried;
        private static AccessTools.FieldRef<CompInteractable, int> cooldownRef;

        public static int CooldownTicks(CompInteractable comp)
        {
            if (!cooldownRefTried)
            {
                cooldownRefTried = true;
                try { cooldownRef = AccessTools.FieldRefAccess<CompInteractable, int>("cooldownTicks"); }
                catch (Exception e)
                {
                    Journal.EmitWarning("interactable: cooldownTicks field ref not bound: " + e.Message);
                }
            }
            if (comp == null || cooldownRef == null) return -1;
            try { return cooldownRef(comp); }
            catch { return -1; }
        }

        // The guarded area label. Vanilla interpolates
        // EffectiveAreaRestrictionInPawnCurrentMap.Label, which NREs on a null
        // MapHeld (PawnSafe Class D); this is the guarded sibling, and the same
        // one PawnOrderVerbs.AreaLabel and `extinguish` use.
        private static string AreaLabel(Pawn p)
        {
            try { return p?.playerSettings?.AreaRestrictionInPawnCurrentMap?.Label ?? "unrestricted"; }
            catch { return "unrestricted"; }
        }

        private static string Resolve(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try { return ((TaggedString)s).Resolve(); }
            catch { return s; }
        }

        // Same contract as PawnActs.Tr and UsableSafe.TrGame: the game's own
        // string, or a phrase of ours the caller can tell apart via
        // `reason_from`.
        private static string TrGame(string key, string fallback)
        {
            try
            {
                var s = key.Translate().ToString();
                return string.IsNullOrEmpty(s) || s.Contains(key) ? fallback : s.CapitalizeFirst();
            }
            catch { return fallback; }
        }

        private static string TrGameArg(string key, NamedArgument a, string fallback)
        {
            try
            {
                var s = key.Translate(a).ToString();
                return string.IsNullOrEmpty(s) || s.Contains(key) ? fallback : s.CapitalizeFirst();
            }
            catch { return fallback; }
        }

        private static object SafeObj(Func<object> f)
        {
            try { return f(); } catch { return null; }
        }

        private static object SafeInt(Func<int> f)
        {
            try { return f(); } catch { return null; }
        }

        // ---------------------- reflection, cached by type -------------------

        private static readonly Dictionary<Type, Type> orderRouteCache = new Dictionary<Type, Type>();
        private static readonly Dictionary<Type, bool> canInteractOverride = new Dictionary<Type, bool>();

        // The declaring type of the OrderForceTarget the game would dispatch to.
        // Walked rather than name-matched so a modded CompAnalyzable subclass
        // that does not re-override still resolves to CompAnalyzable.
        private static Type OrderForceTargetDeclaringType(Type t)
        {
            if (orderRouteCache.TryGetValue(t, out var cached)) return cached;
            Type declaring = null;
            try
            {
                var m = t.GetMethod("OrderForceTarget",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(LocalTargetInfo) }, null);
                declaring = m?.DeclaringType;
            }
            catch { }
            orderRouteCache[t] = declaring;
            return declaring;
        }

        // Does the concrete type add CanInteract clauses this file does not
        // model? CompInteractable, CompAnalyzable and CompAnalyzableUnlockResearch
        // are all reproduced above, so only a FOURTH declaring type counts.
        private static bool OverridesCanInteract(CompInteractable comp)
        {
            var t = comp.GetType();
            if (canInteractOverride.TryGetValue(t, out bool v)) return v;
            v = false;
            try
            {
                var m = t.GetMethod("CanInteract",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(Pawn), typeof(bool) }, null);
                var declaring = m?.DeclaringType;
                v = declaring != null
                    && declaring != typeof(CompInteractable)
                    && declaring != typeof(CompAnalyzable)
                    && declaring != typeof(CompAnalyzableUnlockResearch);
            }
            catch { }
            canInteractOverride[t] = v;
            return v;
        }

        // Does the concrete type READ `checkOptionalItems`? Only a type that
        // overrides CanInteract past our modelled set can, so this is the same
        // question — named separately because the CALLER's question is "is the
        // two-pass worth running", and running it on a chip would be waste.
        public static bool OptionalItemsMatter(CompInteractable comp)
            => comp != null && OverridesCanInteract(comp);
    }
}
