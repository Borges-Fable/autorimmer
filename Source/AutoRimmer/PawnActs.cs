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
            public void No(Pawn p, string gate, string reason)
            {
                var d = Line(p);
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

            private static Dictionary<string, object> Line(Pawn p)
                => new Dictionary<string, object>
                {
                    ["pawn"] = p?.thingIDNumber ?? -1,
                    ["name"] = p != null ? PawnSafe.Name(p) : null,
                };

            public int Count => Accepted.Count;

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
                    ["action"] = Accepted.Count > 0 ? Stamp(seq) : NoStamp(),
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
        private static Dictionary<string, object> JobLine(Pawn pawn)
        {
            string job = null;
            try { job = pawn.jobs?.curDriver?.GetReport(); } catch { }
            return new Dictionary<string, object>
            {
                ["job"] = Journal.Truncate(job, PawnSerializer.JobClip),
                ["job_def"] = pawn.CurJobDef?.defName,
                ["at"] = Positions.Out(pawn.Position),
            };
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
