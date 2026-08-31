using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // ======================================================= spec 3.4 =========
    // THE HANDS. Player verbs for pawn orders, standing policies, warden ops,
    // emergency orders and research selection.
    //
    // This file is the shared substrate: the `action` journal row, the
    // per-pawn accepted/rejected result shape, argument resolution, and the
    // widget-gate helpers every verb in the 3.4 set calls. The verbs live in
    // the other parts of this partial class:
    //
    //   PawnOrderVerbs.cs      draft/undraft/move-to/attack/prioritize/equip/…
    //   PawnEmergencyVerbs.cs  extinguish/beat-fire/tend/man-turret/repair/…
    //   PawnManageVerbs.cs     work-priorities/schedule/assign
    //   PolicyVerbs.cs         the five policy databases
    //   WardenVerbs.cs         prisoner / guest / slave interaction state
    //   MedicalBillVerbs.cs    surgery bills on a pawn
    //   ResearchVerbs.cs       research project selection
    //
    // ------------------- THE THREE RULES THIS SET IS JUDGED ON ---------------
    //
    // 1. THE GATE LIVES IN THE WIDGET, NOT IN THE MODEL (DESIGN §Action model).
    //    RimWorld puts its preconditions in the UI layer and leaves the model
    //    wide open. `Pawn_DraftController.Drafted` will happily draft a downed
    //    pawn; `Pawn_WorkSettings.SetPriority` will happily be told to set a
    //    disabled work type and answers with a RED ERROR; `BillStack.AddBill`
    //    checks nothing at all. So every verb here re-implements the check the
    //    UI holds and CITES IT BY FILE + MEMBER at the call site. Verified by
    //    hand against the decompiled 1.6 tree built from THIS bench's
    //    Assembly-CSharp.dll (misc/rimworld/reference/decompiled/RimWorldBase).
    //    Citations are FILE + MEMBER, never a line offset — grep the member.
    //
    // 2. THE PLURAL FORM IS THE VERB; the singular is its degenerate case.
    //    Work priorities are a MATRIX (and "copied from another pawn" is a
    //    third form and ONE call — PawnColumnWorker_CopyPasteWorkPriorities).
    //    Policy assignment takes a pawn list. Schedule blocks are a SPAN, not a
    //    cell. Draft and undraft take a list. Every result reports per-pawn
    //    accepted + rejected-with-reasons; a verb that can only be called in a
    //    loop is the defect.
    //
    // 3. ORDERS GO THROUGH THE GAME'S OWN ORDER PATH.
    //    `Pawn_JobTracker.TryTakeOrderedJob` / `TryTakeOrderedJobPrioritizedWork`
    //    (Verse.AI/Pawn_JobTracker.cs), never `StartJob`, so a mod's Harmony
    //    patch on the order path sees identical traffic to a player's click.
    //
    // ----------------- OBSERVER BANS vs PLAYER VERBS: THE LINE ---------------
    // PawnSafe and WorldSafe ban a set of accessors whose READ mutates. Those
    // are OBSERVER bans, and the distinction matters here: a player verb is not
    // an observer, it is a reproduction of a click, so where the WIDGET ITSELF
    // calls a lazy-init getter the verb calls it too — the player's click does.
    // (FloatMenuOptionProvider_RescuePawn reads
    // `clickedPawn.ageTracker.CurLifeStage.alwaysDowned`; reproducing rescue's
    // gate means reading it.) What stays banned is anything ADDITIONAL to the
    // click:
    //
    //   * Pawn_WorkSettings.GetPriority / SetPriority on a pawn with no work
    //     settings. ConfirmInitializedDebug() Log.Errors AND initialises
    //     (PawnSafe Class B) — and reproducing the widget IS the guard, since
    //     PawnColumnWorker_WorkPriority.DoCell returns early on
    //     `!pawn.workSettings.EverWork`. Every work path here is EverWork-gated.
    //   * The policy trackers' CurrentApparelPolicy / CurrentFoodPolicy /
    //     CurrentPolicy GETTERS (PawnSafe Class A): reading one ASSIGNS a
    //     default and scribes it. Assignment writes go through the public
    //     SETTER, which is correct for a player verb; every READ in this file
    //     goes through PawnSafe.Policies or ReadingPolicyOf below.
    //   * ResearchProjectDef.IsFinished / CanStartNow / RecipeDef.AvailableNow
    //     (WorldSafe Class A): every one bottoms out in
    //     ResearchManager.GetProgress, which INSERTS into a scribed dictionary
    //     on a miss. WorldSafe.CanStart / Finished are the shipped routes and
    //     this spec reuses them rather than re-deriving them.
    //   * Pawn_PlayerSettings.EffectiveAreaRestrictionInPawnCurrentMap
    //     (PawnSafe Class D): dereferences a null MapHeld. Several vanilla
    //     reject strings interpolate it; ours use the guarded sibling.
    //   * Pawn.DevelopmentalStage (PawnSafe Class C): routes through
    //     Pawn_AgeTracker.CurLifeStageIndex, which on a stale cache can rename
    //     the pawn and add/remove components. MainTabWindow_Work/Assign filter
    //     their roster with `!pawn.DevelopmentalStage.Baby()`; that clause is
    //     DELIBERATELY NOT REPRODUCED — it is a roster cosmetic, and the
    //     substantive gates (EverWork, a non-null timetable, a non-null
    //     tracker) already exclude every pawn it would have.
    //
    // ------------- MODAL DIALOGS: NEVER, AND WHY IT IS A HARD RULE -----------
    // Spec 1.7 (shipped): a force-pausing window makes every subsequent
    // `advance` halt at 0 ticks with reason:"dialog", and NOTHING can clear it
    // until 3.5 ships dialog routing. One such call permanently wedges an
    // unattended run. `Verse/Dialog_MessageBox.cs` sets `forcePause = true`, and
    // `PlayerKnowledgeDatabase.IsComplete` is a plain knowledge-DB lookup with
    // no tutor-enabled short-circuit — so a tutorial modal fires on any save
    // where the concept is fresh, regardless of tutorial settings.
    //
    // So: a player verb reproduces the widget's GATE, then takes the job
    // ITSELF. It never invokes the FloatMenuOption's `action` delegate, because
    // several of those delegates end in a modal. The four in this spec's paths,
    // each avoided at its own call site and named there:
    //
    //   * FloatMenuOptionProvider_Arrest's action ->
    //     TutorUtility.DoModalDialogIfNotKnown(ConceptDefOf.ArrestingCreatesEnemies)
    //   * FloatMenuOptionProvider_Equip's action -> Dialog_MessageBox twice
    //     (a bladelink already bonded elsewhere; a persona-weapon confirmation)
    //   * FloatMenuOptionProvider_Wear's action ->
    //     MechanitorUtility.TryConfirmBandwidthLossFromDroppingThing, which adds
    //     a Dialog_MessageBox.CreateConfirmation
    //   * HealthCardUtility.GenerateSurgeryOption's action ->
    //     CompRoyalImplant.CheckForViolations and RecipeWorker.GetConfirmation,
    //     both Dialog_MessageBox
    //
    // AND ONE THAT IS NOT AN OPTION DELEGATE AT ALL, found while auditing this
    // rule and worth stating loudly because a grep for the tutorial helper does
    // not find it: **HealthCardUtility.CreateSurgeryBill(…, sendMessages:true)
    // calls Bill.CreateNoPawnsWithSkillDialog, which is a bare
    // `Find.WindowStack.Add(new Dialog_MessageBox(…))`** whenever no free
    // colonist meets the recipe's skill requirement. That is a MODAL raised by
    // the ordinary bill-creation path, on an input an agent will hit constantly.
    // Both call sites in this spec therefore pass `sendMessages:false` and
    // re-derive the four warnings as RESULT FIELDS — which is better
    // information anyway, since a top-of-screen message is not something the
    // agent reads. Vanilla itself already knows this path is unsafe unattended:
    // Pawn_GuestTracker.GuestTrackerTickInterval's own auto-bill call passes
    // sendMessages:false.
    //
    // ------------------------------ FOG --------------------------------------
    // DESIGN decisions log 2026-08-30: the player-facing surface hides
    // undiscovered ground, one rule rather than a per-verb judgement. Nothing
    // here is dev:*, so a TARGET in fog is refused — which is also the game's
    // own default (`FloatMenuOptionProvider.IgnoreFogged => true`). The ONE
    // exception is the goto order, because the game makes the same exception:
    // FloatMenuOptionProvider_DraftedMove overrides `IgnoreFogged => false`, so
    // a player can order a drafted colonist into unexplored ground and so can
    // this verb. Noted at that call site too.
    internal static partial class PawnActs
    {
        // ------------------------- the `action` row --------------------------
        // Player-verb mutations journal as `action`, mirroring 3.1's `dev` row
        // shape {verb, step, target, …}. Unlike `dev` an action is NOT a cheat:
        // no `cheat`, no `fog_exempt`. The journal_seq join key comes back in
        // the result the way Dev.Stamp does, and — the honest half — when the
        // journal writer is closed Journal.Emit returns 0 and the result SAYS
        // SO rather than looking like a normal success.
        //
        // Deliberately `private static` on this partial class: 3.2's worker is
        // writing the same kind of helper in its own worktree, and a shared
        // public type would collide at merge. The orchestrator owns the
        // JOURNAL.md row and any factoring at merge time.
        private static long Act(string verb, string step, string target,
            Dictionary<string, object> extra = null)
        {
            var payload = new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["step"] = step,
                ["target"] = target,
            };
            if (extra != null)
                foreach (var kv in extra)
                    if (!payload.ContainsKey(kv.Key)) payload[kv.Key] = kv.Value;
            int tick = 0;
            try { tick = Find.TickManager.TicksGame; } catch { }
            return Journal.Emit("action", payload, tick);
        }

        // The block every mutating result carries. `journal_seq` is the join
        // key back to the `action` line.
        private static Dictionary<string, object> Stamp(long seq)
        {
            var d = new Dictionary<string, object> { ["journal_seq"] = seq };
            if (seq == 0)
                d["provenance"] = "NOT WRITTEN — the journal writer is closed, so this mutation has "
                    + "no journal line. Treat anything done in this session as unprovenanced.";
            return d;
        }

        // A call that deliberately mutated nothing (a pure query, or every
        // target rejected). No journal line is owed and saying so is not the
        // same as failing to write one.
        private static Dictionary<string, object> NoStamp()
            => new Dictionary<string, object>
            {
                ["journal_seq"] = null,
                ["provenance"] = "not applicable — nothing was mutated",
            };

        // The `action` row for a verb built on Outcome, and the ONLY route
        // those verbs should use. It decides for itself whether a row is owed —
        // `Outcome.Reached`, i.e. the verb got as far as a verdict on at least
        // one target — so the decision cannot drift between call sites, which
        // is exactly how it drifted before: fourteen sites guarded `Act` with
        // `outcome.Count > 0 ? … : 0` while six single-target verbs reached
        // `Act` unconditionally because they early-returned on refusal instead.
        // A fix phrased as "change the ternary" would have missed those six.
        //
        // The verdict rides in the payload so the journal alone answers "which
        // of my orders did nothing" — see Outcome.Verdict.
        private static long ActOn(Outcome outcome, string verb, string step, string target,
            Dictionary<string, object> extra = null)
        {
            if (outcome == null || !outcome.Reached) return 0;
            var payload = new Dictionary<string, object> { ["verdict"] = outcome.Verdict() };
            if (extra != null)
                foreach (var kv in extra)
                    payload[kv.Key] = kv.Value;
            return Act(verb, step, target, payload);
        }

        // ------------------- the per-pawn result accumulator -----------------
        // DESIGN's "candidates + reasons, never bare booleans", in the plural
        // form: every verb reports what it did per pawn AND what it refused,
        // in the game's own words, with the gate that refused it named.
        private sealed class Outcome
        {
            public readonly List<object> Accepted = new List<object>();
            public readonly List<object> Rejected = new List<object>();

            public void Ok(Pawn p, Dictionary<string, object> extra = null)
            {
                var d = Line(p);
                if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
                Accepted.Add(d);
            }

            // `gate` names the widget clause that refused; `reason` is the
            // game's own string where the game has one, and only then.
            //
            // `extra` carries the same per-pawn diagnostic block an accepted
            // line gets. It was added 2026-08-31 because a refused pawn is
            // exactly the one the caller has questions about — "why is this one
            // not seeking" is not answered by the word "already" — and without
            // it every rejection costs a second round trip to re-read state the
            // verb had already computed. Optional, so 3.4's callers are
            // unaffected.
            public void No(Pawn p, string gate, string reason,
                Dictionary<string, object> extra = null)
            {
                var d = Line(p);
                if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
                d["gate"] = gate;
                d["reason"] = reason;
                Rejected.Add(d);
            }

            public void NoThing(Thing t, string gate, string reason)
            {
                Rejected.Add(new Dictionary<string, object>
                {
                    ["thing"] = t?.thingIDNumber,
                    ["def"] = t?.def?.defName,
                    ["gate"] = gate,
                    ["reason"] = reason,
                });
            }

            // The CELL-level sibling of NoThing, for a verb whose target is a
            // position rather than a thing (`extinguish {at:…}`, `move-to
            // {to:…}`). Added by 4087644's remainder: those verbs refused a
            // whole call with a bare `error` string and no Rejected row, so
            // `Outcome.Reached` was false and ActOn wrote no `action` line —
            // the wasted order was invisible to the ledger, which is the exact
            // defect comment #1 names. ONE row per wasted ORDER rather than one
            // per doer: the gate is a fact about the cell, not about any pawn,
            // and `verdict.by_gate` should count the order that did nothing.
            public void NoAt(IntVec3 cell, string gate, string reason)
            {
                Rejected.Add(new Dictionary<string, object>
                {
                    ["at"] = Positions.Out(cell),
                    ["gate"] = gate,
                    ["reason"] = reason,
                });
            }

            private static Dictionary<string, object> Line(Pawn p)
                => new Dictionary<string, object>
                {
                    ["pawn"] = p?.thingIDNumber ?? -1,
                    ["name"] = p != null ? PawnSafe.Name(p) : null,
                };

            public int Count => Accepted.Count;

            // Did the verb REACH its targets — landed or refused? This is the
            // journal's gate, and it is deliberately not `Count`.
            //
            // git-bug 4087644 comment #1: `Outcome.Result` used to stamp off
            // Accepted.Count, so a call where every pawn was refused wrote NO
            // `action` row at all — which means THE WASTED ORDERS WERE EXACTLY
            // THE ONES INVISIBLE TO THE LEDGER. `journal {types:["action"]}` is
            // the aggregate the agent learns from, so "which of my instructions
            // are redundant" was unanswerable at session end no matter how good
            // the per-call reporting got. A reached-but-changed-nothing call now
            // journals, carrying its verdict.
            //
            // Reaching none of them is still no row: a verb that threw on
            // argument resolution, or was handed an empty pawn list, mutated
            // nothing and owes the journal nothing.
            public bool Reached => Accepted.Count > 0 || Rejected.Count > 0;

            // The verdict the `action` row carries, so the journal ALONE
            // answers "which of my orders did nothing" without a join back to
            // the result envelope the agent no longer has.
            public Dictionary<string, object> Verdict()
            {
                var byGate = new Dictionary<string, object>();
                for (int i = 0; i < Rejected.Count; i++)
                {
                    var row = Rejected[i] as Dictionary<string, object>;
                    if (row == null) continue;
                    string g = row.TryGetValue("gate", out var raw) ? raw as string : null;
                    if (string.IsNullOrEmpty(g)) continue;
                    byGate[g] = (byGate.TryGetValue(g, out var n) ? (int)n : 0) + 1;
                }
                return new Dictionary<string, object>
                {
                    ["accepted"] = Accepted.Count,
                    ["rejected"] = Rejected.Count,
                    ["by_gate"] = byGate,
                };
            }

            public Dictionary<string, object> Result(string verb, long seq, Dictionary<string, object> extra = null)
            {
                var d = new Dictionary<string, object>
                {
                    ["verb"] = verb,
                    ["accepted"] = Accepted,
                    ["rejected"] = Rejected,
                    ["counts"] = new Dictionary<string, object>
                    {
                        ["accepted"] = Accepted.Count,
                        ["rejected"] = Rejected.Count,
                    },
                    // Reached, not Accepted — see Outcome.Reached. A call that
                    // refused every target still journaled, and saying
                    // "nothing was mutated" over a written row is the same
                    // false negative e8f2c32 fixed in the matrix path.
                    ["action"] = Reached ? Stamp(seq) : NoStamp(),
                };
                if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
                return d;
            }
        }

        // ========================= ARGUMENT RESOLUTION =======================

        private static Map Map() => PawnSafe.CurrentMap();

        // A pawn LIST — the plural axis of every verb here. Accepts
        // `pawns:[id|name|"pawn:id", …]`, the singular `pawn:` as its
        // degenerate case, and `pawns:"colonists"` for the whole player roster.
        //
        // Fog-filtered and spawned-filtered, unlike Dev.PawnArg: this is the
        // player-facing surface, and the error names the POLICY rather than the
        // pawn, because confirming that id N exists is itself the leak (2.2's
        // rule, kept identical).
        private static List<Pawn> PawnList(Map map, VerbArgs args, bool required = true,
            string key = "pawns", string singular = "pawn")
        {
            var result = new List<Pawn>();
            object raw = args.Raw(key);
            if (raw == null && args.Has(singular)) raw = args.Raw(singular);
            if (raw == null)
            {
                if (!required) return result;
                throw new VerbArgsException(
                    $"missing required arg '{key}' (an array of pawn ids or names; '{singular}' takes one)");
            }

            if (raw is string word && (word == "colonists" || word == "all"))
            {
                var spawned = new List<Pawn>(map.mapPawns.AllPawnsSpawned);
                for (int i = 0; i < spawned.Count; i++)
                {
                    var p = spawned[i];
                    if (p == null || p.Dead || PawnSafe.Hidden(p, map)) continue;
                    if (PawnSafe.Classify(p) != PawnSafe.ClassColonist) continue;
                    result.Add(p);
                }
                result.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
                return result;
            }

            var items = raw as List<object> ?? new List<object> { raw };
            var seen = new HashSet<int>();
            foreach (var item in items)
            {
                var p = OnePawn(map, item, key);
                if (seen.Add(p.thingIDNumber)) result.Add(p);
            }
            if (required && result.Count == 0)
                throw new VerbArgsException($"arg '{key}' resolved to no pawns");
            return result;
        }

        private static Pawn OnePawn(Map map, object item, string key)
        {
            var spawned = map.mapPawns.AllPawnsSpawned;
            if (item is double d)
            {
                int id = (int)d;
                for (int i = 0; i < spawned.Count; i++)
                    if (spawned[i] != null && spawned[i].thingIDNumber == id && !PawnSafe.Hidden(spawned[i], map))
                        return spawned[i];
                throw new VerbArgsException(NoPawn(id));
            }
            if (item is string s)
            {
                if (s.StartsWith("pawn:", StringComparison.Ordinal) && int.TryParse(s.Substring(5), out int pid))
                {
                    for (int i = 0; i < spawned.Count; i++)
                        if (spawned[i] != null && spawned[i].thingIDNumber == pid && !PawnSafe.Hidden(spawned[i], map))
                            return spawned[i];
                    throw new VerbArgsException(NoPawn(pid));
                }
                for (int i = 0; i < spawned.Count; i++)
                {
                    var p = spawned[i];
                    if (p == null || PawnSafe.Hidden(p, map)) continue;
                    if (string.Equals(PawnSafe.Name(p), s, StringComparison.OrdinalIgnoreCase)) return p;
                    if (p.Name != null && string.Equals(p.Name.ToStringFull, s, StringComparison.OrdinalIgnoreCase)) return p;
                }
                throw new VerbArgsException(
                    $"no visible pawn named '{s}' on the current map "
                    + "(pawns that are unspawned, on another map, or in unexplored ground are not reported)");
            }
            throw new VerbArgsException($"arg '{key}' entries must be a pawn id (number) or a name (string)");
        }

        private static string NoPawn(int id)
            => $"no visible pawn with id {id} on the current map "
               + "(pawns that are unspawned, on another map, or in unexplored ground are not reported)";

        // A thing by thingIDNumber, fog-filtered — the game's own default
        // (FloatMenuOptionProvider.IgnoreFogged => true). Pawns are things, so
        // this resolves them too.
        private static Thing ThingArg(Map map, VerbArgs args, string key, bool required = true)
        {
            object raw = args.Raw(key);
            if (raw == null)
            {
                if (!required) return null;
                throw new VerbArgsException($"missing required arg '{key}' (a thing id)");
            }
            int id;
            if (raw is double d) id = (int)d;
            else if (raw is string s && s.StartsWith("thing:", StringComparison.Ordinal)
                     && int.TryParse(s.Substring(6), out int tid)) id = tid;
            else if (raw is string s2 && s2.StartsWith("pawn:", StringComparison.Ordinal)
                     && int.TryParse(s2.Substring(5), out int pid2)) id = pid2;
            else throw new VerbArgsException($"arg '{key}' must be a thing id (number), \"thing:<id>\" or \"pawn:<id>\"");

            var things = map.listerThings.AllThings;
            for (int i = 0; i < things.Count; i++)
            {
                var t = things[i];
                if (t != null && t.thingIDNumber == id)
                {
                    if (WorldSafe.Hidden(t, map))
                        throw new VerbArgsException(
                            $"no visible thing with id {id} on the current map "
                            + "(things in unexplored ground are not reported)");
                    return t;
                }
            }
            throw new VerbArgsException(
                $"no visible thing with id {id} on the current map "
                + "(things in unexplored ground are not reported)");
        }

        // ============================ WIDGET GATES ===========================
        // Every helper here reproduces a named clause of a named UI member. The
        // return is (false, reason) with the GAME's own string where the game
        // has one; a phrase of ours is never dressed up as the game's.

        // RimWorld/FloatMenuOptionProvider.cs SelectedPawnValid — the four
        // flags every right-click order is gated on, plus the mutant whitelist.
        // A verb that ignores this gate silently no-ops, which is why it is
        // reproduced rather than assumed.
        private static bool ProviderGate(Pawn pawn, bool drafted, bool undrafted,
            bool mechanoidCanDo, bool requiresManipulation, out string gate, out string reason)
        {
            gate = null;
            reason = null;
            if (pawn == null) { gate = "null"; reason = "no pawn"; return false; }
            if (!drafted && pawn.Drafted)
            {
                gate = "undrafted-only";
                reason = Tr("MustBeUndrafted", "this order is only offered to an undrafted pawn");
                return false;
            }
            if (!undrafted && !pawn.Drafted)
            {
                gate = "drafted-only";
                reason = "this order is only offered to a drafted pawn "
                    + "(FloatMenuOptionProvider.Undrafted is false for it)";
                return false;
            }
            if (!mechanoidCanDo && pawn.RaceProps != null && pawn.RaceProps.IsMechanoid)
            {
                gate = "mechanoid";
                reason = "mechanoids are not offered this order (FloatMenuOptionProvider.MechanoidCanDo)";
                return false;
            }
            if (requiresManipulation)
            {
                bool capable = false;
                try { capable = pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation); }
                catch { }
                if (!capable)
                {
                    gate = "manipulation";
                    reason = Tr("Incapable", "incapable of manipulation");
                    return false;
                }
            }
            return true;
        }

        // Reachability, in the game's words. Every drafted-order provider spells
        // its no-path rejection as "NoPath".Translate().CapitalizeFirst().
        private static bool CanReachThing(Pawn pawn, Thing t, PathEndMode mode, out string reason)
        {
            reason = null;
            try
            {
                if (pawn.CanReach(t, mode, Danger.Deadly)) return true;
            }
            catch { }
            reason = Tr("NoPath", "no path");
            return false;
        }

        private static bool CanReachCell(Pawn pawn, IntVec3 c, PathEndMode mode, out string reason)
        {
            reason = null;
            try
            {
                if (pawn.CanReach(c, mode, Danger.Deadly)) return true;
            }
            catch { }
            reason = Tr("NoPath", "no path");
            return false;
        }

        // RimWorld/Pawn_WorkSettings.cs EverWork (`priorities != null`) — the
        // gate PawnColumnWorker_WorkPriority.DoCell, JobGiver_Work and
        // PawnColumnWorker_CopyPasteWorkPriorities.DoCell all use. Ungated,
        // GetPriority/SetPriority Log.Error AND give the pawn work settings it
        // never had (PawnSafe Class B).
        private static bool EverWork(Pawn pawn, out string reason)
        {
            reason = null;
            if (pawn?.workSettings != null && pawn.workSettings.EverWork) return true;
            reason = "pawn has no work settings; touching one would create them "
                + "(Pawn_WorkSettings.EverWork is false)";
            return false;
        }

        // ------------------------ reading policy, guarded --------------------
        // The fifth policy database (session-4 amendment item 8). Its tracker
        // has the SAME lazy-init trap as the other three — RimWorld/
        // Pawn_ReadingTracker.cs CurrentPolicy does
        // `if (curPolicy == null) curPolicy = …DefaultReadingPolicy();` and
        // `curPolicy` is `Scribe_References.Look(ref curPolicy, "curAssignment")`
        // — so reading it ASSIGNS one forever. PawnSafe.Policies (2.2) predates
        // the reading database and does not cover it; the guarded route lives
        // here rather than in PawnSafe so this spec touches no shipped file.
        // ORCHESTRATOR: fold this into PawnSafe.Policies at merge if you want
        // one vocabulary in one place — the field name is `reading`.
        private static bool readingRefTried;
        private static AccessTools.FieldRef<Pawn_ReadingTracker, ReadingPolicy> readingPolicyRef;

        private static ReadingPolicy ReadingPolicyOf(Pawn pawn)
        {
            if (!readingRefTried)
            {
                readingRefTried = true;
                try { readingPolicyRef = AccessTools.FieldRefAccess<Pawn_ReadingTracker, ReadingPolicy>("curPolicy"); }
                catch (Exception e) { Journal.EmitWarning("acts: reading policy field ref failed: " + e.Message); }
            }
            if (pawn?.reading == null || readingPolicyRef == null) return null;
            try { return readingPolicyRef(pawn.reading); }
            catch { return null; }
        }

        // ---------------------------- small helpers --------------------------

        // A translated key, falling back to a plain-English phrase of OURS that
        // is LABELLED as ours by being different from the key — never a made-up
        // string presented as the game's.
        private static string Tr(string key, string fallback)
        {
            try
            {
                var s = key.Translate().ToString();
                return string.IsNullOrEmpty(s) || s.Contains(key) ? fallback : s.CapitalizeFirst();
            }
            catch { return fallback; }
        }

        private static string Tr1(string key, NamedArgument a, string fallback)
        {
            try
            {
                var s = key.Translate(a).ToString();
                return string.IsNullOrEmpty(s) || s.Contains(key) ? fallback : s;
            }
            catch { return fallback; }
        }

        // A modded getter that throws must degrade one FIELD, not the verb.
        private static T Safe<T>(Func<T> f) where T : class
        {
            try { return f(); } catch { return null; }
        }

        private static object SafeObj(Func<object> f)
        {
            try { return f(); } catch { return null; }
        }

        // What the pawn is doing now — the same `job`/`job_def` vocabulary
        // PawnSerializer.State publishes, so an echo and an observation read
        // alike (one vocabulary, not two).
        //
        // `job_def` ALONE CANNOT ANSWER "DID MY ORDER CAUSE THIS" and never
        // could: it is read off pawn.CurJobDef AFTER the order, so in a
        // collision it faithfully re-observes the job we did NOT cause. That is
        // 4087644, and JobFacts below is the discriminator it was missing.
        private static Dictionary<string, object> JobLine(Pawn pawn)
        {
            string job = null;
            try { job = pawn.jobs?.curDriver?.GetReport(); } catch { }
            var d = new Dictionary<string, object>
            {
                ["job"] = Journal.Truncate(job, PawnSerializer.JobClip),
                ["job_def"] = pawn.CurJobDef?.defName,
                ["at"] = Positions.Out(pawn.Position),
            };
            Job cur = null;
            try { cur = pawn?.jobs?.curJob; } catch { }
            foreach (var kv in JobFacts(cur)) d[kv.Key] = kv.Value;
            return d;
        }

        // ------------------------- job attribution ---------------------------
        // WHO ordered this job, WHEN, and under which work giver. Five plain
        // public field reads on Verse.AI/Job.cs — loadID, playerForced,
        // jobGiver, workGiverDef, startTick — none of them a property, none
        // with a side effect. Published on every job line AND on `pawn`'s state
        // so an echo and an observation stay one vocabulary.
        //
        // `player_forced` IS NOT THE DISCRIMINATOR, and reading it as one is
        // the trap this block exists to close. RimWorld/JobGiver_Work.cs
        // TryIssueJobPackage sets `playerForced = true` AUTONOMOUSLY on its
        // emergency-prioritized branch (`emergency &&
        // pawn.mindState.priorityWork.IsPrioritized`), with no click in that
        // tick — it is sustaining an earlier order, and the resulting job's
        // jobGiver is JobGiver_Work, not ThinkNode_QueuedJob. The unambiguous
        // signature is the TRIPLE, published as `ordered`.
        //
        // TYPE check, never identity: the Humanlike think tree holds TWO
        // ThinkNode_QueuedJob nodes (data/defs/ThinkTreeDefs/Humanlike.xml, one
        // of them inBedOnly), so comparing against a single node instance
        // misses the in-bed one.
        //
        // BEST EFFORT, AND NOTHING BRANCHES ON IT. Verse.AI/Job.cs ExposeData
        // scribes no ThinkNode reference — only an int (`lastJobGiverKey`)
        // resolved against the scribed jobGiverThinkTree, and on a miss it
        // leaves jobGiver NULL with a Log.Warning. Those keys are worse than
        // order-sensitive: Verse/ThinkTreeDef.cs ResolveReferences calls
        // ThinkTreeKeyAssigner.AssignKeys BEFORE ResolveParentNodes, so at
        // assignment time every node's parent is null and the hash collapses to
        // the node's own TYPE NAME — same-type nodes are then separated only by
        // `num ^= Rand.Int` in traversal order against a process-global set. So
        // adding or removing ANY node of an already-used type ahead of them
        // shifts the key. On a 38-mod bench that is a live possibility, which is
        // why these fields are EVIDENCE for a reader to weigh and never a
        // control-flow input.
        //
        // ---------------- `ordered` IS NARROW ON PURPOSE, AND `order_kind`
        // ---------------- IS THE FIELD THAT ANSWERS "DID I CAUSE THIS".
        //
        // git-bug ac407f1: `ordered` reads FALSE after a `wear` we issued this
        // tick, which is the least useful possible answer to the question the
        // name invites. It is not a bug — the triple is doing exactly what it
        // says — but the name promises more than the value delivers, so the
        // value gets a companion rather than a redefinition. `ordered` keeps
        // its meaning (accept/4087644-order-honesty.py 2.2b/2.2c assert it and
        // DESIGN's merged prose agrees); `order_kind` splits the ordered space
        // in two:
        //
        //     order_kind == "work"    queuedNode && workGiver != null && forced
        //                             — identical to `ordered`. A prioritized
        //                             WORK order: `prioritize`, or a right-click
        //                             work option (FloatMenuOptionProvider_
        //                             WorkGivers stamps workGiverDef, and
        //                             Pawn_JobTracker.TryTakeOrderedJob-
        //                             PrioritizedWork stamps it again).
        //     order_kind == "direct"  queuedNode && workGiver == null && forced
        //                             — a direct order: wear, equip, move-to,
        //                             tend, carry… No WorkGiver exists for any
        //                             of them, which is why the triple cannot
        //                             see them.
        //     order_kind == null      no evidence of an order. Includes the
        //                             unresolvable-jobGiver case below: null is
        //                             "we cannot tell", never "the agent did
        //                             not order it".
        //
        // SOURCE CORRECTION, and it matters because ac407f1 gets it backwards.
        // The issue says the `workGiverDef` clause is what excludes
        // JobGiver_Work's autonomous `playerForced`. VERIFIED AGAINST
        // RimWorld/JobGiver_Work.cs: it is not. That branch reaches
        // GiverTryGiveJobPrioritized, which sets `job2.workGiverDef = giver.def`
        // (and `job3.workGiverDef` on the scanCells side) BEFORE
        // TryIssueJobPackage sets `job.playerForced = true` and returns
        // `new ThinkResult(job, this, tag)`. So that job carries BOTH
        // playerForced and a workGiverDef; the only clause that rejects it is
        // `jobGiver is ThinkNode_QueuedJob`, because `this` is the JobGiver_Work
        // node and Pawn_JobTracker.StartJob assigns `curJob.jobGiver = jobGiver`
        // from the ThinkResult's SourceNode.
        //
        // That is WHY splitting on workGiverDef is safe: the autonomous case is
        // already gone by the time either kind is decided, so "direct" cannot
        // capture it. It is also why `ordered` must NOT be widened by simply
        // dropping the clause — not because the clause guards anything, but
        // because `ordered`'s published meaning is prioritized-WORK and two
        // shipped acceptance checks assert it.
        //
        // The queuedNode clause is load-bearing for a second reason. Verse.AI/
        // JobQueue is enqueued by four non-player sites — JobDriver_AttackStatic
        // and JobDriver_AttackMelee (EnqueueFirst on a follow-up attack),
        // JobInBedUtility (LayDown), and Pawn_JobTracker's own
        // resumeCurJobAfterwards path — so ThinkNode_QueuedJob alone does not
        // mean "the player asked". Every one of those enqueues a job with
        // playerForced FALSE, so the PAIR (queuedNode && forced) is the real
        // discriminator and the workGiver split is a refinement on top of it.
        internal static Dictionary<string, object> JobFacts(Job job)
        {
            if (job == null)
                return new Dictionary<string, object>
                {
                    ["job_id"] = null,
                    ["player_forced"] = null,
                    ["job_giver"] = null,
                    ["work_giver"] = null,
                    ["job_start_tick"] = null,
                    ["ordered"] = null,
                    ["order_kind"] = null,
                };
            string giver = null;
            bool queuedNode = false;
            try
            {
                var g = job.jobGiver;
                giver = g?.GetType().Name;
                queuedNode = g is ThinkNode_QueuedJob;
            }
            catch { }
            string workGiver = null;
            try { workGiver = job.workGiverDef?.defName; } catch { }
            bool forced = false;
            try { forced = job.playerForced; } catch { }
            return new Dictionary<string, object>
            {
                ["job_id"] = SafeObj(() => (object)job.loadID),
                ["player_forced"] = forced,
                ["job_giver"] = giver,
                ["work_giver"] = workGiver,
                // startTick is assigned ONLY in Pawn_JobTracker.StartJob, so a
                // job still sitting in the queue carries the uninitialised -1.
                // Publish null rather than a sentinel that reads as a tick and
                // becomes a wrong number in somebody's chart later.
                ["job_start_tick"] = SafeObj(() => job.startTick >= 0 ? (object)job.startTick : null),
                // The triple. A null jobGiver (see above) makes this false, not
                // unknown — read a false as "no evidence", never as "proof the
                // agent did not order it".
                ["ordered"] = queuedNode && workGiver != null && forced,
                // The companion field. `ordered == (order_kind == "work")` by
                // construction, deliberately: one value, two names, so a reader
                // who wants "did I cause this at all" asks `order_kind != null`
                // and a reader who wants "is this prioritized work" asks
                // `ordered`. Neither has to know the triple.
                ["order_kind"] = queuedNode && forced
                    ? (workGiver != null ? "work" : "direct")
                    : null,
            };
        }

        // ------------------ the already-doing-it pre-check --------------------
        // 4087644. Verse.AI/Pawn_JobTracker.cs TryTakeOrderedJob opens:
        //
        //     job.playerForced = true;
        //     if (curJob != null && curJob.JobIsSameAs(pawn, job))
        //     {
        //         return true;
        //     }
        //
        // A pawn already running an equivalent job makes the call return TRUE
        // having done nothing. OUR Job — the one carrying playerForced — is
        // discarded and curJob keeps playerForced == false. Every verb that
        // reported that as `accepted` was telling the agent an order worked when
        // it had changed nothing, and since `job_def` is re-read afterwards it
        // corroborated the lie by naming the job we did not cause.
        //
        // UNCONDITIONAL, AND THE QUEUED CASE IS THE WORSE ONE. `requestQueueing`
        // is not read until `isDownEvent = isDownEvent || requestQueueing`,
        // eight lines past that return — so with queue:true a collision enqueues
        // NOTHING: no queue entry, no current-job change, no trace at all, and
        // the agent's model gains a queued action that does not exist.
        // Publishing the job queue does not rescue that (there is nothing in it
        // to publish); only this pre-check does. Note JobIsSameAs is compared
        // against curJob ONLY, never against the queue, so this is specifically
        // a current-job collision swallowing a queue request.
        //
        // DIRECTION IS LOAD-BEARING — see PawnSafe's Job.JobIsSameAs entry.
        // The receiver must be the RUNNING job. Verse.AI/Job.cs JobIsSameAs
        // calls GetCachedDriver(pawn), which lazily allocates a driver when
        // cachedDriver is null and Log.Errors on a pawn mismatch. curJob is
        // running, so it already holds its driver for this pawn and the call
        // costs nothing; inverted — `job.JobIsSameAs(pawn, curJob)` — it would
        // allocate a JobDriver on our fresh throwaway Job. Never write it the
        // other way round.
        //
        // THE IN-REPO PRECEDENT IS `prioritize`, which has shipped exactly this
        // test since 3.4: PrioritizeRejection in PawnOrderVerbs.cs answers
        // "already doing exactly this" from the same comparison. This helper
        // generalises what that verb already proved here.
        //
        // The candidate Job is not returned to JobMaker's pool on this path.
        // Neither does vanilla — TryTakeOrderedJob drops it on the floor in the
        // same case — and matching the game beats tidying after it.
        private static bool AlreadyDoing(Pawn pawn, Job job)
        {
            if (pawn == null || job == null) return false;
            try
            {
                var curJob = pawn.jobs?.curJob;
                return curJob != null && curJob.JobIsSameAs(pawn, job);
            }
            catch { return false; }
        }

        // ONE gate value, not two. The gate names the widget clause that
        // refused, and it is the same clause whether or not the caller asked to
        // queue; the queue distinction is a property of the CALL, so it rides on
        // the line as a field and any by-gate aggregation stays clean. Sibling
        // of move-to's shipped `already-there`.
        private const string GateAlready = "already-doing-it";

        private static string AlreadyWhy(bool queued)
            => queued
                ? "already running an equivalent job. Pawn_JobTracker.TryTakeOrderedJob would have "
                  + "returned true and enqueued NOTHING — the order was not queued and left no trace, "
                  + "because its JobIsSameAs early-out fires before requestQueueing is ever read"
                : "already running an equivalent job, so Pawn_JobTracker.TryTakeOrderedJob would have "
                  + "returned true without taking the order (Job.JobIsSameAs against curJob)";

        // The rejected line for the collision: the same diagnostic block an
        // accepted line carries, plus the queue flag the gate deliberately does
        // not encode.
        private static Dictionary<string, object> AlreadyLine(Pawn pawn, bool queued)
        {
            var d = JobLine(pawn);
            d["queue"] = queued;
            return d;
        }

        // The durable half of a prioritized order (Verse/PriorityWork.cs). It is
        // Scribe'd state with a 30000-tick timeout, NOT a one-shot job, and
        // `prioritize`'s result has to say so — see the echo-the-durable-state
        // rule at PawnActs' Prioritize.
        private static Dictionary<string, object> PriorityWorkLine(Pawn pawn)
        {
            var pw = pawn?.mindState?.priorityWork;
            if (pw == null) return null;
            bool active = false;
            try { active = pw.IsPrioritized; } catch { }
            var d = new Dictionary<string, object>
            {
                ["active"] = active,
                ["work_giver"] = pw.WorkGiver?.defName,
                ["cell"] = pw.Cell.IsValid ? Positions.Out(pw.Cell) : null,
                ["timeout_ticks"] = 30000,
                ["note"] = "mindState.priorityWork is SCRIBED state, not a job: it survives save/load "
                    + "and expires 30000 ticks after it was set (Verse/PriorityWork.cs IsPrioritized). "
                    + "Drafting the pawn clears it (Pawn_DraftController.Drafted setter).",
            };
            return d;
        }
    }
}
