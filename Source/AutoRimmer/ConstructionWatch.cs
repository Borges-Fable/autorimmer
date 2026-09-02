using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ============================================ git-bug f9dadc7 =============
    // A SCALAR TELLS YOU THE SIZE OF A SET, NEVER ITS AGE.
    //
    // `digest.construction.awaiting_materials` sat at **22 for TWENTY
    // consecutive in-game days** on run m1-20260901 — days 38 through 57, every
    // digest read, and the agent read it every turn. (The issue says fifteen.
    // Measured across `RUNS/m1-20260901/digests/day-*.json`: 22 on days 38-57
    // inclusive, with no gap. The same directory shows a flat 60 for days 13-18
    // and a flat 24 for 21-24 and again for 31-36, so this is the shape of the
    // whole run and not one incident.) Fifteen identical reads and one read are
    // the same envelope. Nothing in the surface could say "this has not moved".
    //
    // The days 38-57 window is NOT capped — no `more`, no `cap`, no `cap_note`
    // on any of those twenty days, and blueprints+frames was 22-25 against a
    // 60-item scan cap. So 22 was a true census every time. The number was
    // right; the number was the problem.
    //
    // ---------------------- WHERE THE STATE LIVES ----------------------------
    // `AgentGameComponent` has NO `ExposeData`. Nothing it holds survives a
    // save/load, and run m1-20260901 spanned sessions and bench relaunches. The
    // obvious repair — scribing a tracking dictionary — is the WRONG one here:
    // writing scribed state from the observation surface is a live hazard class
    // in this project (git-bug d16a463, `_mp/DETERMINISM.md`). Resolved by the
    // orchestrator before dispatch: **track in memory, and publish what you do
    // not know.**
    //
    // So this class is BillWatch's shape one surface over: a static dictionary,
    // sampled from `GameComponentTick`, cleared at every game boundary, with a
    // `tracked_since` tick beside every answer. What that buys is the third
    // state, and the third state is the whole point:
    //
    //     stalled: true   — observed in this state for >= StallTicks
    //     stalled: false  — observed ENTERING this state more recently than that
    //     stalled: null   — tracking has not run long enough to say
    //
    // `null` is never "clean". Per [[acceptance-suites-must-prove-shapes]] an
    // absent or zero age must not read as healthy, because a flat number
    // reading as a healthy one is the original defect. A reload resets the
    // clock; a reload must not silently report "not stalled".
    //
    // -------------------------- HOW THE AGE IS DERIVED -----------------------
    // FIRST SIGHT COUNTS NOTHING, exactly as BillWatch's does. An element seen
    // for the first time gets `sinceTick = now` and `observed = false`: the age
    // that follows is a FLOOR, because the element may have been sitting in
    // that state since long before this process started. Only once the state
    // CHANGES under observation does `observed` become true and the age become
    // an exact measurement. Both are published, and `age_basis` says which.
    //
    // A floor over the threshold is still proof: `age >= StallTicks` with
    // `observed:false` means "at least two days", which is what the contract
    // asks. A floor under the threshold proves nothing, and that is the case
    // that must answer `null`.
    //
    // THE THRESHOLD IS ELAPSED TIME, NOT A CALENDAR COUNT. f9dadc7 asks for
    // ">= 2 day boundaries". A half-open window of exactly 2 x
    // `GenDate.TicksPerDay` contains at least two multiples of TicksPerDay, so
    // `age >= 120000` implies two boundaries crossed and never the reverse.
    // That is the conservative direction — it can only under-report — and it
    // costs no longitude lookup on a hot path. `GenLocalDate.DayOfSeason` is
    // the agent-facing calendar and is deliberately not consulted: the local
    // day boundary is offset from `TicksGame % 60000` by the game's start hour
    // and by the tile's longitude, so counting calendar increments would need a
    // `WorldGrid.LongLatOf` per element per read.
    //
    // ------------------------------ THE KEY ----------------------------------
    // `Thing.thingIDNumber` of the live Blueprint or Frame. A blueprint that
    // becomes a Frame is a NEW thing with a new id, and so is the blueprint
    // `Frame.FailConstruction` puts back. Both are real state changes — one
    // means the materials arrived, the other means work was lost — so losing
    // the age at those two transitions is correct rather than a limitation. The
    // case this issue exists for, a blueprint nobody ever touches, keeps one id
    // for its whole life.
    //
    // ------------------------------ WHAT IT COSTS ----------------------------
    // Once per `Cadence` (2500) ticks — ColonySampler's rate, i.e. the method
    // returns on its first line 2499 ticks out of 2500 — up to `SampleCap`
    // constructibles are probed through `ConstructionVerbs.Probe`: one cached
    // cost list, one `GenConstruct.FirstBlockingThing`, one dictionary lookup
    // and (only for a def with a prerequisite) an int compare per colonist.
    // Deliberately NO `GetStatValueAbstract`: the state token does not depend
    // on `WorkToBuild`, which is the whole reason this can run inside the tick
    // loop at all.
    //
    // The DISPLAY facts — def, cell, layout id, the `why` sentence — are cached
    // on the row at sample time, so the digest's roll-up is a dictionary walk
    // with ZERO Verse access. That matters twice: the digest is documented as
    // called constantly, and the roll-up then covers the sampler's whole
    // 300-item window rather than the digest's own 60-item one.
    // =========================================================================
    internal static class ConstructionWatch
    {
        // Ticks between samples. ColonySampler's cadence, and 24 samples an
        // in-game day is ample resolution for a two-day threshold.
        public const int Cadence = 2500;
        // Elements probed per sample, matching `construction`'s own ScanCap.
        public const int SampleCap = 300;
        // Rows retained. A row is ~60 bytes; this is a memory ceiling, not a
        // correctness one, and it is pruned by last-seen.
        public const int RowCap = 1200;
        // Two in-game days. See the header: this implies >= 2 day boundaries
        // crossed and is never satisfied by fewer.
        public const int StallTicks = 2 * 60000;

        private sealed class Row
        {
            public string State;
            public int SinceTick;
            public int LastSeen;
            public bool Observed;    // did we watch it ENTER this state
            // Cached at first sight so the roll-up touches no Verse.
            public string Def;
            public int X, Z;
            public string PlacementId;
            public string LayoutId;
            public string Why;
            public int MapId;
        }

        private static readonly Dictionary<int, Row> rows = new Dictionary<int, Row>();
        private static int startedTick = -1;

        // When this process started watching. Null before the first sample, and
        // it is published beside every age so a reader can see how much of the
        // colony's history the answer covers.
        public static object StartedTick => startedTick >= 0 ? (object)startedTick : null;

        public static bool TrackingOlderThanStall(int now)
            => startedTick >= 0 && now - startedTick >= StallTicks;

        // Called from AgentGameComponent's two lifecycle virtuals, for
        // BillWatch's reason: the static state outlives the Game object, and an
        // age carried across a reload would be attributed to blueprints that no
        // longer exist. The clock restarting is expected and is exactly what
        // `age_basis` and `tracked_since_tick` exist to say.
        public static void Reset()
        {
            rows.Clear();
            startedTick = -1;
        }

        public static void Tick()
        {
            int now;
            try { now = Find.TickManager.TicksGame; }
            catch { return; }
            if (startedTick < 0) startedTick = now;
            if (now % Cadence != 0) return;

            List<Map> maps;
            try { maps = Find.Maps; }
            catch { return; }
            if (maps == null) return;

            int budget = SampleCap;
            for (int m = 0; m < maps.Count && budget > 0; m++)
            {
                var map = maps[m];
                if (map?.listerThings == null) continue;
                var roster = ConstructionSkill.Read(map);
                Dictionary<int, Pawn> index;
                try { index = ConstructionVerbs.WorkerIndexFor(map); }
                catch { continue; }
                var live = ConstructionVerbs.ConstructiblesFor(map);
                for (int i = 0; i < live.Count && budget > 0; i++)
                {
                    budget--;
                    Sample(map, live[i], index, roster, now);
                }
            }
            if (rows.Count > RowCap) Prune(now);
        }

        private static void Sample(Map map, Thing t, Dictionary<int, Pawn> index,
            ConstructionSkill.Roster roster, int now)
        {
            if (t == null || t.Destroyed) return;
            string state, why;
            try { state = ConstructionVerbs.Probe(map, t, index, roster, out why, out _); }
            catch { return; }

            int id = t.thingIDNumber;
            if (!rows.TryGetValue(id, out var row))
            {
                // FIRST SIGHT COUNTS NOTHING — BillWatch's rule, and the same
                // argument: adopting `now` rather than a sentinel is the
                // difference between "this has been stuck since tick N" and
                // "this observer has seen it once". `Observed` false marks the
                // age that follows as a floor.
                row = new Row
                {
                    State = state,
                    SinceTick = now,
                    Observed = false,
                    MapId = SafeMapId(map),
                };
                Describe(row, t, why);
                rows[id] = row;
                row.LastSeen = now;
                return;
            }
            row.LastSeen = now;
            row.Why = why;
            if (row.State != state)
            {
                row.State = state;
                row.SinceTick = now;
                // The transition was WATCHED, so from here the age is exact.
                row.Observed = true;
            }
        }

        private static void Describe(Row row, Thing t, string why)
        {
            row.Why = why;
            try { row.Def = t.def?.entityDefToBuild?.defName ?? t.def?.defName; } catch { }
            try { row.X = t.Position.x; row.Z = t.Position.z; } catch { }
            // Walked ONCE, at first sight — `Placements.For` is a linear scan of
            // a table capped at 2000 and `Layouts.Owning` another, so paying it
            // per sample would be the one real cost in this file.
            try
            {
                var p = Placements.For(t);
                if (p != null)
                {
                    row.PlacementId = p.Id;
                    row.LayoutId = Layouts.Owning(p.Id)?.Id;
                }
            }
            catch { }
        }

        private static int SafeMapId(Map map)
        {
            try { return map?.uniqueID ?? -1; }
            catch { return -1; }
        }

        private static void Prune(int now)
        {
            var dead = new List<int>();
            foreach (var kv in rows)
                if (now - kv.Value.LastSeen > Cadence * 4) dead.Add(kv.Key);
            for (int i = 0; i < dead.Count; i++) rows.Remove(dead[i]);
        }

        // ------------------------------------------------------- the answer --

        // The per-element age, for `construction`'s rows and for `advance`'s
        // `unresolved_items`. Never throws and never mutates.
        public sealed class Age
        {
            public bool Tracked;
            public bool Observed;
            public int SinceTick;
            public int Ticks;
            public object Stalled;      // true | false | null — see the header

            public void Fill(Dictionary<string, object> row)
            {
                row["state_since_tick"] = Tracked ? (object)SinceTick : null;
                row["state_age_ticks"] = Tracked ? (object)Ticks : null;
                row["state_age_days"] = Tracked
                    ? (object)Math.Round(Ticks / 60000.0, 2) : null;
                row["age_basis"] = !Tracked ? "not-tracked"
                    : Observed ? "observed-transition" : "since-first-seen";
                // THE TRI-STATE. `null` means "tracking cannot answer yet" and
                // MUST NOT be read as false. It is the reason this whole file
                // exists rather than a scalar age.
                row["stalled"] = Stalled;
                row["tracked_since_tick"] = StartedTick;
            }
        }

        private static readonly Age Untracked = new Age { Tracked = false, Stalled = null };

        public static Age Look(Thing t, int now)
        {
            if (t == null) return Untracked;
            if (!rows.TryGetValue(t.thingIDNumber, out var row)) return Untracked;
            int age = Math.Max(0, now - row.SinceTick);
            return new Age
            {
                Tracked = true,
                Observed = row.Observed,
                SinceTick = row.SinceTick,
                Ticks = age,
                Stalled = Verdict(age, row.Observed),
            };
        }

        // A floor over the threshold is proof; a floor under it is not.
        private static object Verdict(int age, bool observed)
        {
            if (age >= StallTicks) return true;
            return observed ? (object)false : null;
        }

        // ------------------------------------------------------- the rollup --

        // `digest.construction.stalled[]` (f9dadc7 item 2). A dictionary walk,
        // no Verse access, so it is affordable on the section documented as
        // called constantly — and it covers the SAMPLER's window (300) rather
        // than the digest's own scan cap (60), which is how an element the
        // glance never reaches still gets reported.
        //
        // "A count alone repeats this defect", so every row names the def, the
        // cell, the layout, the age and — in the one vocabulary shared with
        // e08c3e5's skill branch — WHY.
        public static List<object> Stalled(Map map, int now, int cap, out int total)
        {
            var hits = new List<KeyValuePair<int, Row>>();
            int mapId = SafeMapId(map);
            foreach (var kv in rows)
            {
                var row = kv.Value;
                if (row.MapId != mapId) continue;
                // A row nobody has sampled recently is a build that finished or
                // was cancelled; it is not stalled, it is gone.
                if (now - row.LastSeen > Cadence * 2) continue;
                if (!IsStallable(row.State)) continue;
                int age = Math.Max(0, now - row.SinceTick);
                if (age < StallTicks) continue;
                hits.Add(new KeyValuePair<int, Row>(age, row));
            }
            total = hits.Count;
            // SORT BEFORE THE CAP, never after. An agent reading a capped list
            // wants the element that has been stuck longest; capping first and
            // sorting the survivors would hand it an arbitrary subset in a
            // convincing order. `thingIDNumber` breaks ties so the list is
            // deterministic read to read.
            hits.Sort((a, b) =>
            {
                int c = b.Key.CompareTo(a.Key);
                return c != 0 ? c : a.Value.SinceTick.CompareTo(b.Value.SinceTick);
            });
            var list = new List<object>();
            for (int i = 0; i < hits.Count && list.Count < cap; i++)
            {
                var row = hits[i].Value;
                list.Add(new Dictionary<string, object>
                {
                    ["def"] = row.Def,
                    ["at"] = new List<object> { row.X, row.Z },
                    ["state"] = row.State,
                    ["why"] = row.Why,
                    ["layout_id"] = row.LayoutId,
                    ["placement_id"] = row.PlacementId,
                    ["state_since_tick"] = row.SinceTick,
                    ["state_age_ticks"] = hits[i].Key,
                    ["state_age_days"] = Math.Round(hits[i].Key / 60000.0, 2),
                    ["age_basis"] = row.Observed ? "observed-transition" : "since-first-seen",
                });
            }
            return list;
        }

        // WHICH STATES CAN STALL. f9dadc7 item 2 names `awaiting-materials` and
        // `blocked`. Two more belong here and are added deliberately:
        //
        //  * `no-builder` — e08c3e5's fourth branch, and it is EXACTLY the run's
        //    own case. The Heater sat unbuildable for the whole window this
        //    issue was filed about; excluding it would drop the headline example
        //    out of the headline report.
        //  * `ready` — materials present, nobody on it, for two days. That is
        //    the README's third triage branch and it is a genuine stall; a
        //    `ready` element is only healthy while somebody is about to take it.
        //
        // `in-progress` is excluded: a pawn has a job on it and the clock
        // restarting when one picks it up is the correct behaviour. `unscanned`
        // is excluded because it is not a measurement.
        private static bool IsStallable(string state)
            => state == ConstructionVerbs.StateAwaitingMaterials
            || state == ConstructionVerbs.StateBlocked
            || state == ConstructionVerbs.StateNoBuilder
            || state == ConstructionVerbs.StateReady;
    }
}
