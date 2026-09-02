using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ============================ git-bug d9d6c12 =============================
    // "A BILL THAT HAS FAILED N CONSECUTIVE INGREDIENT SEARCHES IS REPORTED AS
    // SUCH." The game does not count them, so this does.
    //
    // WHAT THE GAME ACTUALLY STORES is one int:
    // `RimWorld/Bill.cs nextTickToSearchForIngredients`. There is exactly one
    // write (`RimWorld/WorkGiver_DoBill.cs StartOrResumeBillJob`):
    //
    //     bill.nextTickToSearchForIngredients =
    //         Find.TickManager.TicksGame + ReCheckFailedBillTicksRange.RandomInRange;
    //
    // with `ReCheckFailedBillTicksRange = new IntRange(500, 600)`, and one read,
    // in the same method's skip clause:
    //
    //     Find.TickManager.TicksGame <= bill.nextTickToSearchForIngredients
    //         && FloatMenuMakerMap.makingFor != pawn
    //
    // TWO THINGS FOLLOW, AND BOTH CORRECT A CLAIM THIS PROJECT HAD WRITTEN DOWN.
    //
    //  1. The back-off is 500–600 TICKS — ten game seconds — not days. The
    //     m1-20260901 post-mortem and d9d6c12 both describe the field as having
    //     "sat in the future for days"; measured against the source it cannot.
    //     What it does is REARM every ten seconds for as long as the search
    //     keeps failing, so a reader who samples it sees a future value
    //     essentially always while a bill is starving, and never learns from
    //     one sample whether that is one failure or ten thousand. Publishing
    //     the raw tick and leaving the caller to compare it with `now` is
    //     therefore not merely inconvenient, it is unanswerable from one read.
    //     That is what this file fixes.
    //  2. `FloatMenuMakerMap.makingFor != pawn` means a `prioritize`-driven
    //     probe does NOT set the field — so `prioritize`'s `blocked:` reason
    //     and this counter are independent readings, which is why the run's
    //     defect was cornerable at all.
    //
    // HOW THE COUNT IS DERIVED, and what it is NOT. The field is a timestamp,
    // not a counter, and a SUCCESSFUL search leaves no trace at all. So:
    //
    //   * sampled every `Cadence` ticks (250 — under the 500 minimum back-off,
    //     so a distinct failure cannot be missed between two samples);
    //   * a stamp that DIFFERS from the last one we saw is a new failed search
    //     -> `failures++`;
    //   * the streak is cleared once the stamp has been stale for `ClearAfter`
    //     ticks (2500, ~4x the maximum back-off) — i.e. nothing has failed a
    //     search on this bill for a while. It is NOT cleared merely because
    //     `now > stamp`, which is true for most of every 500-tick window and
    //     would reset the counter continuously.
    //
    // The number is therefore a FLOOR observed since `observed_since_tick`,
    // and every published block says so. A count that pretended to be the
    // colony's whole history would be the same kind of lie as `filter: null`.
    //
    // COST. One int read per bill per 250 ticks, over
    // `ListerThings.ThingsInGroup(PotentialBillGiver)` — a list the game
    // maintains — plus the pawn surgery queues are deliberately NOT walked
    // (a surgery bill has no ingredient back-off worth a per-tick sweep; the
    // block still publishes for them, just with `observed:false`).
    // Nothing here mutates: `nextTickToSearchForIngredients` is a plain public
    // field and `Bill.GetUniqueLoadID()` is string concatenation over the
    // scribed `loadID`.
    // =========================================================================
    internal static class BillWatch
    {
        public const int Cadence = 250;
        public const int ClearAfter = 2500;
        public const int BillCap = 400;

        private sealed class Row
        {
            public int stamp;          // the last nextTickToSearchForIngredients seen
            public int failures;       // distinct stamps since the streak began
            public int firstTick;      // when the current streak started
            public int lastSeen;       // last tick this bill was sampled at all
        }

        // Keyed by `Bill.GetUniqueLoadID()` ("Bill_<recipe>_<loadID>"), which
        // is the same stable, scribed handle `bill-set {uid:…}` takes —
        // `Bill.loadID` itself is private. A bill deleted and re-added gets a
        // NEW id, which is correct: re-creating a bill genuinely resets its
        // search state (it is why "delete and re-add" reads as a fix).
        private static readonly Dictionary<string, Row> rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        private static int startedTick = -1;

        // Called from AgentGameComponent.LoadedGame / StartedNewGame: the
        // static state outlives the Game object, and a count carried across a
        // reload would be attributed to bills that no longer exist.
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

            var maps = Find.Maps;
            if (maps == null) return;
            for (int m = 0; m < maps.Count; m++)
            {
                var map = maps[m];
                if (map?.listerThings == null) continue;
                var givers = map.listerThings.ThingsInGroup(ThingRequestGroup.PotentialBillGiver);
                for (int i = 0; i < givers.Count; i++)
                {
                    var stack = (givers[i] as IBillGiver)?.BillStack;
                    if (stack == null) continue;
                    for (int b = 0; b < stack.Count; b++) Sample(stack[b], now);
                }
            }
            if (rows.Count > BillCap) Prune(now);
        }

        private static void Sample(Bill bill, int now)
        {
            if (bill == null) return;
            string id;
            int stamp;
            try
            {
                id = bill.GetUniqueLoadID();
                stamp = bill.nextTickToSearchForIngredients;
            }
            catch { return; }
            if (id == null) return;

            if (!rows.TryGetValue(id, out var row))
            {
                // FIRST SIGHT COUNTS NOTHING. Adopting the current stamp
                // rather than a sentinel is the difference between "this bill
                // has failed one search" and "this observer has seen this bill
                // once" — a fresh `bill-add` has stamp 0, and a sentinel would
                // have reported every new bill as already failing.
                row = new Row { stamp = stamp, failures = 0, firstTick = -1, lastSeen = now };
                rows[id] = row;
                return;
            }
            row.lastSeen = now;

            if (stamp != row.stamp)
            {
                // A NEW back-off stamp. The only writer of this field is
                // WorkGiver_DoBill's failed-search branch, so a change is a
                // failed ingredient search and nothing else.
                if (row.failures == 0 || row.firstTick < 0) row.firstTick = now;
                row.failures++;
                row.stamp = stamp;
                return;
            }
            // Same stamp as last time. Once it has been stale for longer than
            // any back-off could last, whatever was failing has stopped.
            if (row.failures > 0 && now - stamp > ClearAfter)
            {
                row.failures = 0;
                row.firstTick = -1;
            }
        }

        private static void Prune(int now)
        {
            var dead = new List<string>();
            foreach (var kv in rows)
                if (now - kv.Value.lastSeen > ClearAfter * 4) dead.Add(kv.Key);
            for (int i = 0; i < dead.Count; i++) rows.Remove(dead[i]);
        }

        // ------------------------------------------------------------------
        // THE PUBLISHED BLOCK (d9d6c12 items 1 and 2). A named state plus the
        // tick it wakes, so a reader who does not know the convention still
        // sees the answer — and the failure count so "asleep right now" is
        // distinguishable from "asleep for the last four game hours".
        //
        // `hopeless` (item 3) is filled in by the caller, which is the only
        // place that has the ingredient scan: "asleep and will retry" and
        // "asleep and will retry against a filter that can never match" must
        // not present identically, and the difference is not in this field.
        // ------------------------------------------------------------------
        public static Dictionary<string, object> Block(Bill bill)
        {
            var d = new Dictionary<string, object>
            {
                ["state"] = "unknown",
                ["wakes_tick"] = null,
                ["wakes_in_ticks"] = null,
                ["consecutive_failed_searches"] = null,
                ["asleep_since_tick"] = null,
                ["observed"] = false,
                ["observed_since_tick"] = startedTick >= 0 ? (object)startedTick : null,
            };
            if (bill == null) return d;
            int now, stamp;
            try
            {
                now = Find.TickManager.TicksGame;
                stamp = bill.nextTickToSearchForIngredients;
            }
            catch { return d; }

            bool asleep = now <= stamp;
            d["state"] = asleep ? "asleep" : "ready";
            d["wakes_tick"] = stamp;
            d["wakes_in_ticks"] = Math.Max(0, stamp - now);

            if (rows.TryGetValue(SafeId(bill), out var row) && row.failures > 0)
            {
                d["observed"] = true;
                d["consecutive_failed_searches"] = row.failures;
                d["asleep_since_tick"] = row.firstTick >= 0 ? (object)row.firstTick : null;
                d["asleep_for_ticks"] = row.firstTick >= 0 ? (object)Math.Max(0, now - row.firstTick) : null;
            }
            else
            {
                d["consecutive_failed_searches"] = 0;
            }

            d["note"] = "RimWorld/WorkGiver_DoBill.cs skips a bill entirely while "
                + "`TicksGame <= nextTickToSearchForIngredients`, for every pawn — so an `asleep` "
                + "bill reads `active` in `state` and is worked by nobody. The back-off is "
                + "ReCheckFailedBillTicksRange = 500..600 TICKS and REARMS on each failure, so a "
                + "future tick proves a recent failed search and NOT a long one: "
                + "`consecutive_failed_searches` is AutoRimmer's own count since "
                + "`observed_since_tick`, not a game field, and is a floor.";
            return d;
        }

        private static string SafeId(Bill b)
        {
            try { return b.GetUniqueLoadID() ?? ""; }
            catch { return ""; }
        }
    }
}
