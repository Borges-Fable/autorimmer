using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ======================================================= git-bug b7359fa ==
    // WHO CAN ACTUALLY DO THIS WORK, AND WHOSE AREA LETS THEM.
    //
    //     designate {type:"hunt", things:[…]}  ->  {ok:true, accepted:6}
    //
    // Six designations were genuinely created and no pawn ever acted on one:
    // every deer stood outside the hunters' allowed area, and `Marco` starved
    // to death at tick 3,072,772 with five designated deer standing on the map
    // (run m1-20260901; playbook lesson
    // `a-designation-outside-the-allowed-area-does-nothing`, severity
    // Critical). The envelope was TRUTHFUL AND USELESS — `accepted: 6` was the
    // right answer to a question nobody had asked.
    //
    // -------------------------- THE GAME'S OWN TEST --------------------------
    // `RimWorld/ForbidUtility.InAllowedArea(IntVec3, Pawn)`, whole body:
    //
    //     if (forPawn.playerSettings != null) {
    //       Area a = forPawn.playerSettings.EffectiveAreaRestrictionInPawnCurrentMap;
    //       if (a != null && a.TrueCount > 0 && !a[c]) return false;
    //     }
    //     return true;
    //
    // CALLED, never re-derived — the standing "the gate lives in the widget"
    // rule, and `PawnEmergencyVerbs.CanExtinguish` already calls it in exactly
    // this per-pawn form. Three consequences decide the shape of everything
    // below, and the first is why the obvious fix would have been WRONG IN THE
    // DIRECTION THAT REPORTS FALSE CLEAN:
    //
    //  1. THE AREA IS PER PAWN. There is no colony-wide allowed area to test a
    //     designation against. "Outside every allowed area" means: for every
    //     colonist who could do that work, the cell is outside THAT pawn's
    //     effective area. A check written against a single global or Home area
    //     reports clean on the exact colony that killed Marco.
    //  2. It reads `EffectiveAreaRestrictionInPawnCurrentMap`, NOT the plain
    //     `AreaRestrictionInPawnCurrentMap`. PawnSafe CLASS D: the effective
    //     getter does `allowedAreas.TryGetValue(pawn.MapHeld, …)` with no null
    //     check and throws ArgumentNullException for a pawn with no map, while
    //     its sibling carries the guard. `SeekVerbs.ReadPosture` already holds
    //     the guarded route (`if (p.MapHeld != null)`) and this file takes the
    //     same one rather than opening a second.
    //  3. AN AREA WITH `TrueCount == 0` IS IGNORED ENTIRELY — the clause
    //     short-circuits and the cell counts as allowed. So "this pawn has an
    //     area assigned" is not "this pawn is restricted", and a report that
    //     conflates them flags a colony that has no restriction at all.
    //     `SeekVerbs.AreaBinds` is the same predicate; it is private to that
    //     verb's fact struct, so the one line is restated here rather than
    //     making this file depend on it.
    //
    // ------------------------ WHAT IS AND IS NOT TESTED ----------------------
    // THIS IS THE ALLOWED-AREA TEST AND NOTHING ELSE, and the envelope says so
    // in its own `test` field rather than letting the word "reach" be read as
    // more than it is. It is NOT a pathing test: a cell inside a pawn's area
    // can still be unreachable — no route, a locked door, a different region —
    // and `Pawn.CanReach` / the `reachable` verb are what answer that. Pathing
    // is deliberately not run here: it is O(cells x pawns) region traversal
    // against a 20,000-cell ceiling, and the defect this file closes is
    // specifically the area one. The pathing half is filed as its own issue.
    //
    // --------------------- WHICH PAWNS COUNT, AND WHY --------------------
    // The roster is `map.mapPawns.FreeColonistsSpawned`, SNAPSHOTTED — that
    // getter CLEARS and refills one cached List on every access (WorldSafe
    // Class E), so it is read once and never re-entered mid-loop. Same source
    // `WorkCoverage.Compute` uses, and the same three words for the counts, so
    // one reader has seen both:
    //
    //   capable  — `!pawn.WorkTypeIsDisabled(w)`, the game's own "could ever".
    //   enabled  — `workSettings.WorkIsActive(w)`, i.e. `GetPriority(w) > 0`.
    //   allowed  — this file's addition: `cell.InAllowedArea(pawn)`.
    //
    // `GetPriority`/`WorkIsActive` are EverWork-gated (PawnSafe Class B: an
    // ungated call `Log.Error`s AND initialises work settings on a pawn that
    // never had them, which is a mutation by an observer).
    //
    // THE FRANCHISE IS `capable`, NOT `enabled`. A capable colonist with the
    // work type switched off is one `work-priorities` call from doing the job,
    // so refusing the designation over it would be refusing the wrong thing;
    // a colonist who CANNOT do the work never will. `enabled` is published
    // beside it and raises a `warning` when it is zero, because that is the
    // other half of what run m1-20260901 got wrong (128 designated
    // `MineableSteel` cells, never mined) and the agent cannot see it anywhere
    // else at the moment it designates.
    //
    // ------------------- THE DESIGNATION -> WORK TYPE LINK ------------------
    // Resolved through the game's OWN data, not a table of ours:
    // `DesignationVerbs`' table names the `WorkGiver_*` CLASS that consumes
    // each designation (verified against the decompiled 1.6 source by member
    // name), and `WorkTypeFor` looks that class up in
    // `DefDatabase<WorkGiverDef>` by `giverClass` and reads `workType`. A mod
    // that re-homes `WorkGiver_Miner` from Mining to something else is
    // therefore honoured for free, and a class that no WorkGiverDef claims
    // yields `applies:false` with a reason instead of a guess.
    // =========================================================================
    internal static class DesignateReach
    {
        // The unreachable-target list is capped like every other list in this
        // spec; the COUNTS are complete. DesignateEngine.RejectCap, restated
        // rather than referenced so the two can diverge if one ever should.
        public const int TargetCap = 24;
        public const int PawnCap = 32;

        // -----------------------------------------------------------------
        // ONE CAPABLE COLONIST
        // -----------------------------------------------------------------
        public sealed class Hand
        {
            public Pawn P;
            public Area Area;          // EffectiveAreaRestrictionInPawnCurrentMap
            public int AreaCells;      // its TrueCount; 0 means the game ignores it
            public bool Enabled;       // WorkIsActive
            public int Priority;
            public bool HasPriority;
            public bool Downed;
            public int Allowed;        // targets this pawn's area lets it work

            // ForbidUtility.InAllowedArea's own condition for the restriction
            // doing anything at all.
            public bool Binds => Area != null && AreaCells > 0;
        }

        // -----------------------------------------------------------------
        // THE VERDICT
        // -----------------------------------------------------------------
        public sealed class Verdict
        {
            public bool Applies;
            public string Why;             // when !Applies
            public WorkTypeDef Work;
            public string WorkSource;
            public readonly List<Hand> Hands = new List<Hand>();
            public readonly List<Dictionary<string, object>> Unreadable =
                new List<Dictionary<string, object>>();
            public int Considered;
            public int Actionable;
            public int Unreachable;
            public readonly List<IntVec3> UnreachableCells = new List<IntVec3>();
            public readonly List<Thing> UnreachableThings = new List<Thing>();
            public bool Scored;

            public int Capable => Hands.Count;

            public int EnabledCount
            {
                get
                {
                    int n = 0;
                    for (int i = 0; i < Hands.Count; i++) if (Hands[i].Enabled) n++;
                    return n;
                }
            }

            public bool AnyUnrestricted
            {
                get
                {
                    for (int i = 0; i < Hands.Count; i++) if (!Hands[i].Binds) return true;
                    return false;
                }
            }

            // THE REFUSAL CONDITION, and it is deliberately narrow: a batch
            // where NOT ONE target can be worked by ANY capable colonist. A
            // mixed batch reports and proceeds — the material-shortfall
            // precedent in `place-layout`, which the issue names.
            public bool NothingActionable
                => Applies && Scored && Considered > 0 && Actionable == 0;

            public string RefusalCode
                => Capable == 0 ? "no-capable-pawn" : "outside-every-allowed-area";
        }

        // -----------------------------------------------------------------
        // designation -> work type, through the game's own WorkGiverDefs
        // -----------------------------------------------------------------
        // DefDatabase is fixed after load, so the answer is memoised per class.
        // A null answer is memoised too: "no WorkGiverDef claims this class" is
        // an answer and must not be re-scanned on every call.
        private static readonly Dictionary<Type, KeyValuePair<WorkTypeDef, string>> workCache =
            new Dictionary<Type, KeyValuePair<WorkTypeDef, string>>();

        public static WorkTypeDef WorkTypeFor(Type giverClass, out string source)
        {
            source = null;
            if (giverClass == null) return null;
            if (workCache.TryGetValue(giverClass, out var hit))
            {
                source = hit.Value;
                return hit.Key;
            }
            WorkTypeDef work = null;
            string src = null;
            try
            {
                var all = DefDatabase<WorkGiverDef>.AllDefsListForReading;
                for (int i = 0; i < all.Count; i++)
                {
                    var d = all[i];
                    if (d == null || d.giverClass != giverClass || d.workType == null) continue;
                    work = d.workType;
                    src = "WorkGiverDef '" + d.defName + "' (giverClass " + giverClass.FullName
                        + ") -> workType " + d.workType.defName;
                    break;
                }
            }
            catch (Exception e)
            {
                Journal.EmitWarning("b7359fa: WorkGiverDef scan for " + giverClass.Name
                    + " threw: " + e.Message);
            }
            workCache[giverClass] = new KeyValuePair<WorkTypeDef, string>(work, src);
            source = src;
            return work;
        }

        // -----------------------------------------------------------------
        // THE ROSTER
        // -----------------------------------------------------------------
        public static Verdict Roster(Map map, Type giverClass)
        {
            var v = new Verdict();
            if (giverClass == null)
            {
                v.Why = "this designator produces no pawn work, so there is no allowed-area "
                    + "question to ask (it takes effect immediately, or it removes rather "
                    + "than orders)";
                return v;
            }
            v.Work = WorkTypeFor(giverClass, out v.WorkSource);
            if (v.Work == null)
            {
                v.Why = "no WorkGiverDef in this game declares giverClass " + giverClass.FullName
                    + ", so the work type that consumes this designation is unknown and the "
                    + "capable roster cannot be named honestly";
                return v;
            }
            v.Applies = true;

            // SNAPSHOT. FreeColonistsSpawned clears and refills one cached List
            // on every access (WorldSafe Class E) — anything that re-enters it
            // mid-loop invalidates the enumerator.
            List<Pawn> colonists;
            try { colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned); }
            catch (Exception e)
            {
                v.Applies = false;
                v.Why = "the colonist roster could not be read: " + e.GetType().Name + ": " + e.Message;
                return v;
            }

            for (int i = 0; i < colonists.Count; i++)
            {
                var p = colonists[i];
                if (p == null) continue;
                bool disabled;
                try { disabled = p.WorkTypeIsDisabled(v.Work); }
                catch { disabled = true; }
                if (disabled) continue;

                var h = new Hand { P = p };
                try { h.Downed = p.Downed; } catch { }

                // PawnSafe Class D, guarded EXPLICITLY rather than caught: a
                // swallowed throw here would report "no area" for a pawn that
                // has one, i.e. would report the cell reachable when it is not
                // — the false-clean direction that cost the colonist. A pawn
                // whose area cannot be read is dropped from the roster and
                // NAMED, so it neither silently allows nor silently blocks.
                var ps = p.playerSettings;
                if (ps != null)
                {
                    try
                    {
                        if (p.MapHeld != null) h.Area = ps.EffectiveAreaRestrictionInPawnCurrentMap;
                    }
                    catch (Exception e)
                    {
                        v.Unreadable.Add(new Dictionary<string, object>
                        {
                            ["pawn"] = p.thingIDNumber,
                            ["name"] = PawnSafe.Name(p),
                            ["why"] = "area-unreadable",
                            ["reason"] = e.GetType().Name + ": " + e.Message,
                        });
                        continue;
                    }
                    try { h.AreaCells = h.Area != null ? h.Area.TrueCount : 0; } catch { }
                }

                // PawnSafe Class B: EverWork-gated, because an ungated
                // GetPriority/WorkIsActive Log.Errors AND initialises the
                // tracker on a pawn that never had one.
                try
                {
                    if (p.workSettings != null && p.workSettings.EverWork)
                    {
                        h.Priority = p.workSettings.GetPriority(v.Work);
                        h.HasPriority = true;
                        h.Enabled = p.workSettings.WorkIsActive(v.Work);
                    }
                }
                catch { }

                v.Hands.Add(h);
            }
            return v;
        }

        // -----------------------------------------------------------------
        // THE SCORE
        // -----------------------------------------------------------------
        // `cell.InAllowedArea(pawn)` per (target, capable pawn). The extension
        // method is the game's, called not copied; the cached `Area` on each
        // Hand is used only for LABELS and the per-area rollup, never for the
        // decision.
        //
        // Cost is one bool-array index per pair — `Area.this[IntVec3]` is
        // `innerGrid[map.cellIndices.CellToIndex(c)]` — plus one dictionary
        // lookup inside the effective-area getter. At the 20,000-cell ceiling
        // and a ten-colonist roster that is 200,000 array reads, which is
        // cheaper than the designator pass that produced the targets.
        public static void Score(Verdict v, Map map, List<IntVec3> cells, List<Thing> things)
        {
            if (!v.Applies) return;
            v.Scored = true;
            int n = things != null ? things.Count : (cells != null ? cells.Count : 0);
            v.Considered = n;
            if (n == 0) return;

            // No capable colonist at all: every target is unworkable, and that
            // is a DIFFERENT fact from "outside the area" with a different
            // correction. Recorded without running the per-pawn loop, which
            // would be empty.
            if (v.Hands.Count == 0)
            {
                v.Unreachable = n;
                for (int i = 0; i < n && v.UnreachableCells.Count < TargetCap; i++)
                {
                    if (things != null)
                    {
                        var t = things[i];
                        if (t == null) continue;
                        v.UnreachableThings.Add(t);
                        v.UnreachableCells.Add(t.PositionHeld);
                    }
                    else v.UnreachableCells.Add(cells[i]);
                }
                return;
            }

            for (int i = 0; i < n; i++)
            {
                IntVec3 at;
                Thing thing = null;
                if (things != null)
                {
                    thing = things[i];
                    if (thing == null) continue;
                    at = thing.PositionHeld;
                }
                else at = cells[i];

                bool any = false;
                for (int j = 0; j < v.Hands.Count; j++)
                {
                    var h = v.Hands[j];
                    bool ok;
                    // The game's own member. A throw here counts as NOT
                    // allowed for this pawn: the roster already dropped every
                    // pawn whose area read threw, so reaching this catch means
                    // something changed underneath us, and the loud direction
                    // is the safe one.
                    try { ok = at.InAllowedArea(h.P); }
                    catch { ok = false; }
                    if (!ok) continue;
                    h.Allowed++;
                    any = true;
                }
                if (any) { v.Actionable++; continue; }
                v.Unreachable++;
                if (v.UnreachableCells.Count < TargetCap)
                {
                    v.UnreachableCells.Add(at);
                    if (thing != null) v.UnreachableThings.Add(thing);
                }
            }
        }

        // -----------------------------------------------------------------
        // THE ENVELOPE
        // -----------------------------------------------------------------
        public const string Gate =
            "RimWorld/ForbidUtility.InAllowedArea(IntVec3, Pawn) — per pawn: "
            + "playerSettings.EffectiveAreaRestrictionInPawnCurrentMap, ignored when TrueCount == 0";

        public const string TestNote =
            "ALLOWED AREA ONLY. This is not a pathing test: a target inside a pawn's area may "
            + "still be unreachable (no route, a closed region, a locked door). Use `reachable "
            + "{from, to, pawn}` for that question.";

        public static Dictionary<string, object> Out(Verdict v, Map map)
        {
            var d = new Dictionary<string, object>
            {
                ["applies"] = v.Applies,
                ["gate"] = Gate,
                ["test"] = TestNote,
                ["roster"] = "map.mapPawns.FreeColonistsSpawned",
            };
            if (!v.Applies)
            {
                d["why"] = v.Why;
                return d;
            }
            d["work_type"] = v.Work?.defName;
            d["work_source"] = v.WorkSource;
            d["capable"] = v.Capable;
            d["enabled"] = v.EnabledCount;
            d["unrestricted"] = Unrestricted(v);
            d["considered"] = v.Considered;
            d["scored"] = v.Scored;
            d["actionable"] = v.Actionable;
            d["unreachable"] = v.Unreachable;

            var pawns = new List<object>();
            for (int i = 0; i < v.Hands.Count && i < PawnCap; i++)
            {
                var h = v.Hands[i];
                pawns.Add(new Dictionary<string, object>
                {
                    ["pawn"] = h.P.thingIDNumber,
                    ["name"] = PawnSafe.Name(h.P),
                    ["enabled"] = h.Enabled,
                    // null, not 0, when work settings were never initialised —
                    // "no such priority" and "priority zero" must not read
                    // alike (PawnSafe Class B).
                    ["priority"] = h.HasPriority ? (object)h.Priority : null,
                    ["downed"] = h.Downed,
                    ["area"] = h.Area == null ? null : WorldSafe.Safe(() => h.Area.Label),
                    ["area_id"] = h.Area == null ? (object)null : h.Area.ID,
                    ["area_cells"] = h.AreaCells,
                    // The area EXISTS but binds nothing: TrueCount == 0 short-
                    // circuits InAllowedArea, so this pawn is unrestricted.
                    ["restricted"] = h.Binds,
                    ["can_work"] = v.Scored ? (object)h.Allowed : null,
                });
            }
            d["pawns"] = pawns;
            d["pawns_more"] = Math.Max(0, v.Hands.Count - pawns.Count);
            if (v.Unreadable.Count > 0)
            {
                // Named, never silently folded either way — see the roster.
                d["unreadable_pawns"] = new List<object>(v.Unreadable.ToArray());
            }

            d["areas"] = Areas(v);

            var targets = new List<object>();
            for (int i = 0; i < v.UnreachableCells.Count; i++)
            {
                var row = new Dictionary<string, object> { ["at"] = Positions.Out(v.UnreachableCells[i]) };
                if (i < v.UnreachableThings.Count)
                {
                    var t = v.UnreachableThings[i];
                    row["id"] = t.thingIDNumber;
                    row["def"] = t.def?.defName;
                    row["label"] = WorldSafe.Safe(() => t.LabelShort);
                }
                targets.Add(row);
            }
            d["unreachable_targets"] = targets;
            d["unreachable_more"] = Math.Max(0, v.Unreachable - targets.Count);

            string warn = Warning(v);
            if (warn != null) d["warning"] = warn;
            return d;
        }

        private static int Unrestricted(Verdict v)
        {
            int n = 0;
            for (int i = 0; i < v.Hands.Count; i++) if (!v.Hands[i].Binds) n++;
            return n;
        }

        // WHICH AREA EXCLUDES THE TARGETS — the issue's acceptance item 2. One
        // row per DISTINCT binding area on the capable roster, with how many of
        // the considered targets that area shuts out. Keyed on the Area object,
        // because two Area_Allowed can carry the same user-typed label and a
        // label-keyed rollup would silently merge them.
        private static List<object> Areas(Verdict v)
        {
            var order = new List<Area>();
            var excludes = new Dictionary<Area, int>();
            var members = new Dictionary<Area, List<object>>();
            for (int i = 0; i < v.Hands.Count; i++)
            {
                var h = v.Hands[i];
                if (!h.Binds) continue;
                if (!excludes.ContainsKey(h.Area))
                {
                    order.Add(h.Area);
                    excludes[h.Area] = v.Scored ? Math.Max(0, v.Considered - h.Allowed) : 0;
                    members[h.Area] = new List<object>();
                }
                members[h.Area].Add(PawnSafe.Name(h.P));
            }
            var outp = new List<object>();
            for (int i = 0; i < order.Count; i++)
            {
                var a = order[i];
                outp.Add(new Dictionary<string, object>
                {
                    ["id"] = a.ID,
                    ["label"] = WorldSafe.Safe(() => a.Label),
                    ["cells"] = SafeCount(a),
                    ["pawns"] = members[a],
                    ["excludes"] = v.Scored ? (object)excludes[a] : null,
                });
            }
            return outp;
        }

        private static int SafeCount(Area a)
        {
            try { return a.TrueCount; } catch { return -1; }
        }

        // The one sentence the agent gets for free on the way past. Ordered by
        // which correction is owed first.
        private static string Warning(Verdict v)
        {
            if (!v.Applies) return null;
            if (v.Capable == 0)
                return "NO colonist on this map can do " + Label(v.Work)
                    + " at all — every designation this call makes is inert until one can. "
                    + "Check `work-cover`.";
            if (v.Scored && v.Unreachable > 0)
                return v.Unreachable + " of " + v.Considered + " target(s) lie outside EVERY "
                    + "capable colonist's allowed area. A designation there is valid, permanent "
                    + "and inert: no pawn will step outside its area to reach it. Fix the AREA "
                    + "(`area {kind:\"allowed\", op:\"add\", id:<id>, rect:[…]}` or `posture "
                    + "{pawns:[…], area:null}`), not the designation — re-designating succeeds "
                    + "again and still does nothing.";
            if (v.EnabledCount == 0)
                return "every colonist capable of " + Label(v.Work) + " has it switched OFF "
                    + "(priority 0), so nothing will be worked until `work-priorities` turns it "
                    + "on. The allowed area is clean; this is the other half.";
            return null;
        }

        public static string Label(WorkTypeDef w)
        {
            if (w == null) return "this work";
            try { return string.IsNullOrEmpty(w.gerundLabel) ? w.defName : w.gerundLabel; }
            catch { return w.defName; }
        }

        // -----------------------------------------------------------------
        // THE REFUSAL
        // -----------------------------------------------------------------
        // Built from the PREFLIGHT verdict — scored on the set the game's gate
        // would accept, BEFORE anything is designated, so the refusal is a
        // refusal and not an apology for a mutation already made.
        public static Dictionary<string, object> Refusal(Verdict v)
        {
            var d = new Dictionary<string, object>
            {
                ["code"] = v.RefusalCode,
                ["reason"] = v.Capable == 0
                    ? "no colonist on this map can do " + Label(v.Work)
                        + ", so not one of the " + v.Considered
                        + " target(s) the gate accepted could ever be worked"
                    : "not one of the " + v.Considered + " target(s) the gate accepted lies "
                        + "inside ANY capable colonist's allowed area, so every designation "
                        + "this call would create is inert",
                ["hint"] = v.Capable == 0
                    ? "give a colonist the work type (`work-priorities`), or pass "
                        + "allow_unreachable:true to designate ahead of that"
                    : "extend the area (`area {kind:\"allowed\", op:\"add\", id:<id>, "
                        + "rect:[…]}`), clear the restriction (`posture {pawns:[…], "
                        + "area:null}`), or pass allow_unreachable:true to designate anyway",
                ["allow_unreachable"] = false,
            };
            return d;
        }
    }
}
