using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // TRIAGE — "who is down, how long have they got, and can anybody reach them
    // in time" (git-bug 40ed42f #1, parts 2 and 3).
    //
    // ============ THE BOUNDARY BETWEEN 61794cd AND 40ed42f, SETTLED =========
    // `61794cd` asks: "Consider publishing [the bleed-out clock] alongside a
    // travel estimate for the nearest capable rescuer, since the comparison is
    // the whole decision — but that may belong with the triage procedure
    // (40ed42f) instead. Decide and say which."
    //
    // DECIDED, ONCE, HERE, and recorded on both issues and in DESIGN:
    //
    //   THE CLOCK IS A PROPERTY OF THE PATIENT -> `pawn.health` (61794cd).
    //     `HealthUtility.TicksUntilDeathDueToBloodLoss` is one division on one
    //     pawn. It is correct with no roster, no map and no pathfinder, it is
    //     the same number whoever asks, and `pawn` is read constantly.
    //
    //   THE RESCUE IS A PROPERTY OF THE ROSTER -> this verb (40ed42f).
    //     "Who could rescue" is `work_coverage`'s own question one level down,
    //     and "how long would they take" is a PATHFIND PER CANDIDATE. Putting
    //     that in `pawn` or in the digest would make the cheapest read in the
    //     surface pay for a pathfinder, at every glance, for every pawn — the
    //     exact mistake DESIGN's 2026-09-01 predicate-cost decision exists to
    //     stop.
    //
    // So neither issue grows the other's half, and the comparison — the thing
    // that is actually the decision — happens in the one place that already has
    // both numbers.
    //
    // ==================== WHAT THE M1 RUN DID INSTEAD =======================
    // At tick 231,968 Captain was bleeding out 118 cells away. The response was
    // a WORK-PRIORITY FLIP — Chili's Doctor 0 -> 3 — an adjustment to what a
    // pawn might choose to do next. She was still asleep at 233,497, 235,024 and
    // 236,549, ate a meal at 238,074, and set off ~6,100 ticks after the clock
    // was set. He died.
    //
    // `rescue` is SHIPPED, it FORCES the job through
    // `Pawn_JobTracker.TryTakeOrderedJob`, and it INTERRUPTS `LayDown`. It was
    // called ZERO times in 195 ops. So this verb does not merely report: every
    // casualty row carries `act`, the exact `rescue` call to send, with both ids
    // filled in. A procedure that names the verb removes the whole class of
    // "adjust priorities and hope".
    //
    // ========================= THE ESTIMATE, HONESTLY =======================
    // `travel_ticks` is `PawnPath.TotalCost` from
    // `map.pathFinder.FindPathNow(rescuer, patient, TraverseParms.For(rescuer))`.
    // Those units ARE ticks: `Verse.AI/Pawn_PathFollower.CostToMoveIntoCell` is
    // `TicksPerMoveCardinal|Diagonal + pathGrid.CalculatedCostAt(c) +
    // edifice.PathWalkCostFor(pawn)`, and `nextCellCostLeft` is decremented once
    // per tick — the pathfinder budgets in the same currency the walk spends.
    //
    // It is a FLOOR and is published as one. It does not include: waiting on a
    // door, colliding with another pawn, or the time to abandon the current job
    // — which is precisely what `rescue` removes by forcing it. `carry_ticks`
    // is the second leg, patient -> the bed `TakeToBedGate` actually chose, so
    // `total_ticks` is the whole journey and not just the half that is easy to
    // measure.
    internal static partial class PawnActs
    {
        // Pathfinding is the expensive thing here, so only the nearest few
        // candidates by straight-line distance are pathed, and the number is
        // published rather than assumed. A colony of twenty with five
        // casualties would otherwise cost a hundred FindPathNow calls.
        private const int PathCandidateCap = 3;
        private const int CasualtyCap = 12;

        [Verb("triage")]
        public static object Triage(VerbContext ctx)
        {
            const string V = "triage";
            var map = Map();
            int pathCap = ctx.Args.Int("path_candidates", PathCandidateCap);
            if (pathCap < 1 || pathCap > 12)
                throw new VerbArgsException("path_candidates must be 1..12 — each one is a "
                    + "FindPathNow, which is the expensive part of this verb");

            // SNAPSHOT — FreeColonistsSpawned clears and rebuilds one cached
            // list on every access (DigestVerb.ColonistSection's header).
            var colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);

            var casualties = new List<object>();
            int considered = 0;
            foreach (var patient in colonists)
            {
                if (!IsCasualty(patient, out var clock, out bool downed, out bool tend)) continue;
                considered++;
                if (casualties.Count >= CasualtyCap) continue;
                casualties.Add(CasualtyRow(map, patient, colonists, pathCap, clock, downed, tend));
            }

            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["casualties"] = casualties,
                ["total"] = considered,
                ["more"] = Math.Max(0, considered - casualties.Count),
                ["counts"] = Counts(casualties),
                ["path_candidates"] = pathCap,
                // An observer. Present and null, so a caller can prove the verb
                // mutated nothing rather than infer it.
                ["action"] = NoStamp(),
                ["note"] = "`travel_ticks` and `carry_ticks` are PawnPath.TotalCost, whose unit is "
                         + "ticks (Pawn_PathFollower.CostToMoveIntoCell spends TicksPerMove* plus "
                         + "the path grid, one per tick). They are a FLOOR: they exclude waiting "
                         + "on doors, pawn collisions, and the time to abandon the current job — "
                         + "which is exactly what `rescue` removes by forcing it. `verdict` "
                         + "compares total_ticks against health.ticks_until_bleedout; "
                         + "`no-deadline` means the patient is not bleeding, NOT that there is no "
                         + "hurry.",
            };
        }

        // A CASUALTY IS ANY OF THE THREE, deliberately. Downed is the obvious
        // one and is the one that killed this colony; but a standing pawn
        // bleeding out is the case `Alert_ColonistNeedsTend` INVERTS on (the
        // post-mortem's sharpest signal finding: its getter excludes pawns
        // needing rescue, so it goes OFF when the patient goes DOWN), and a pawn
        // who merely needs tending is the one hour-earlier version of both.
        private static bool IsCasualty(Pawn patient, out Dictionary<string, object> clock,
            out bool downed, out bool tend)
        {
            clock = null; downed = false; tend = false;
            if (patient == null || patient.Dead) return false;
            clock = BleedClock(patient);
            try { downed = patient.Downed; } catch { }
            try { tend = HealthAIUtility.ShouldBeTendedNowByPlayer(patient); } catch { }
            return downed || tend || clock != null;
        }

        // ONE ROW, ONE VERDICT, TWO CALLERS. `advance`'s `bleedout-deadline`
        // refusal (git-bug 40ed42f part 3, built on 722c951's machinery) asks
        // exactly the question this row answers, so it calls THIS — it does not
        // grow a second pathfind or a second comparison. The refusal reads
        // `verdict == "too-slow"` off a row built here and reports the same two
        // numbers this row publishes.
        private static Dictionary<string, object> CasualtyRow(Map map, Pawn patient,
            List<Pawn> colonists, int pathCap, Dictionary<string, object> clock,
            bool downed, bool tend)
        {
            var row = new Dictionary<string, object>
            {
                ["pawn"] = patient.thingIDNumber,
                ["name"] = PawnSafe.Name(patient),
                ["at"] = Positions.Out(patient.Position),
                ["downed"] = downed,
                ["needs_tend"] = tend,
                ["clock"] = clock,
            };

            // Rank by straight-line distance, then gate, then path the top
            // few. The gate runs on ALL candidates because a refusal is the
            // answer to "why is nobody coming" and costs nothing.
            var cands = new List<KeyValuePair<int, Pawn>>();
            var refused = new List<object>();
            foreach (var doer in colonists)
            {
                if (doer == null || doer == patient || doer.Dead) continue;
                if (!ProviderGate(doer, drafted: true, undrafted: true,
                        mechanoidCanDo: false, requiresManipulation: true,
                        out string g1, out string r1))
                { refused.Add(Refusal(doer, g1, r1)); continue; }
                if (!TakeToBedGate("rescue", doer, patient, out string g2, out string r2,
                        out Building_Bed _, out JobDef _))
                { refused.Add(Refusal(doer, g2, r2)); continue; }
                int d2 = (doer.Position - patient.Position).LengthHorizontalSquared;
                cands.Add(new KeyValuePair<int, Pawn>(d2, doer));
            }
            cands.Sort((a, b) =>
            {
                int c = a.Key.CompareTo(b.Key);
                return c != 0 ? c : string.CompareOrdinal(
                    PawnSafe.Name(a.Value), PawnSafe.Name(b.Value));
            });

            var rescuers = new List<object>();
            int bestTotal = int.MaxValue;
            Pawn best = null;
            for (int i = 0; i < cands.Count && i < pathCap; i++)
            {
                var doer = cands[i].Value;
                int travel = PathTicks(map, doer, doer.Position, patient.Position);
                Building_Bed bed = null;
                TakeToBedGate("rescue", doer, patient, out _, out _, out bed, out _);
                int carry = bed == null ? -1
                    : PathTicks(map, doer, patient.Position, bed.Position);
                int total = travel < 0 ? -1 : (carry < 0 ? travel : travel + carry);
                var line = new Dictionary<string, object>
                {
                    ["pawn"] = doer.thingIDNumber,
                    ["name"] = PawnSafe.Name(doer),
                    ["at"] = Positions.Out(doer.Position),
                    ["cells"] = (int)Math.Round(Math.Sqrt(cands[i].Key)),
                    ["travel_ticks"] = travel < 0 ? null : (object)travel,
                    ["carry_ticks"] = carry < 0 ? null : (object)carry,
                    ["total_ticks"] = total < 0 ? null : (object)total,
                    ["bed"] = bed?.thingIDNumber,
                    // WHAT THEY ARE DOING NOW, because the M1 failure was a
                    // rescuer who was asleep and stayed asleep. `rescue`
                    // forces the job; a priority flip does not.
                    ["doing"] = Journal.Truncate(SafeJob(doer), 48),
                    ["drafted"] = doer.Drafted,
                };
                rescuers.Add(line);
                if (total >= 0 && total < bestTotal) { bestTotal = total; best = doer; }
            }
            row["rescuers"] = rescuers;
            row["rescuers_gated_out"] = refused;
            row["candidates_total"] = cands.Count;
            row["candidates_pathed"] = rescuers.Count;
            row["candidates_not_pathed"] = Math.Max(0, cands.Count - rescuers.Count);

            object clockTicks = clock == null ? null : clock["ticks"];
            string verdict;
            object margin = null;
            if (best == null)
                verdict = cands.Count == 0 ? "no-rescuer" : "no-path";
            else if (!(clockTicks is int) && !(clockTicks is long))
                // Not bleeding: there is no deadline to beat, which is not
                // the same as "no hurry" — a downed pawn still starves and
                // still needs tending. Named rather than folded into
                // `in-time`, because the two want different follow-ups.
                verdict = "no-deadline";
            else
            {
                long t = Convert.ToInt64(clockTicks);
                margin = t - bestTotal;
                verdict = bestTotal <= t ? "in-time" : "too-slow";
            }
            row["verdict"] = verdict;
            row["margin_ticks"] = margin;
            if (best != null)
                // THE CALL, SPELLED OUT. Not advice — the exact envelope.
                row["act"] = new Dictionary<string, object>
                {
                    ["op"] = "rescue",
                    ["args"] = new Dictionary<string, object>
                    {
                        ["pawn"] = best.thingIDNumber,
                        ["target"] = patient.thingIDNumber,
                    },
                    ["why"] = "rescue FORCES the job through "
                            + "Pawn_JobTracker.TryTakeOrderedJob and interrupts LayDown. A "
                            + "work-priority change does not: it adjusts what the pawn might "
                            + "choose next, and on the M1 run the chosen rescuer stayed "
                            + "asleep for ~6,100 ticks after the flip.",
                };
            return row;
        }

        // ============================ git-bug 40ed42f #1 part 3 ==============
        // THE ARM-TIME DEADLINE CHECK `advance` CONSUMES.
        //
        // The M1 decision at tick 231,968 was arithmetic: a ~9,040-tick bleed
        // clock against a ~2,810-tick walk. Both numbers were computable and
        // neither was published, so "advance and hope" looked the same as
        // "advance and let him die". This is the comparison, made once, at the
        // moment the agent asks to burn time — and it is the SAME comparison
        // `triage` publishes, because it is literally `CasualtyRow`'s verdict.
        //
        // WHAT IT COSTS, AND WHEN IT IS SKIPPED. Session 19's predicate-cost
        // decision exists to stop a pathfinder running at every glance, so the
        // gate is deliberately steep and is checked cheapest-first:
        //
        //   1. One `FreeColonistsSpawned` snapshot (the cached list rebuilds on
        //      every access — DigestVerb.ColonistSection's header).
        //   2. Per colonist, ONE FLOAT: `hediffSet.BleedRateTotal`, which is a
        //      cached field (Verse/HediffSet.BleedRateTotal returns
        //      `cachedBleedRate` and only recomputes when the game dirtied it).
        //      Nobody bleeding — the overwhelmingly common case — costs exactly
        //      this and stops here. NO pathfind, no hediff walk, no
        //      `ShouldBeTendedNowByPlayer`.
        //   3. Only for a bleeder: the bleed clock
        //      (`HealthUtility.TicksUntilDeathDueToBloodLoss`, one division) and,
        //      only if it is FINITE, the full `CasualtyRow` — which is where the
        //      pathfinds live, capped at `path_candidates` (3) per patient.
        //
        // So the worst case is bounded at 3 `FindPathNow` calls per BLEEDING
        // patient, once per advance, and the ordinary case is one float per
        // colonist. It runs ONCE at arm, never per frame and never per tick:
        // `TimeDriver.Step` polls WindowsForcePause and the state predicate,
        // and a pathfinder has no business in either.
        //
        // ============ IT REFUSES ON `too-slow` AND ON NOTHING ELSE ===========
        // `no-rescuer`, `no-path` and `no-deadline` do NOT refuse, and that is a
        // decision rather than an oversight.
        //
        // The refusal exists to force ONE decision into the open: "this pawn
        // dies unless you act, and advancing is choosing not to". That decision
        // only exists when there is a TIMING question — a rescuer who could make
        // it and a clock that says they cannot. Where nobody can reach the
        // patient AT ALL, advancing changes nothing about the outcome and
        // refusing forever would simply wedge the run: there is no act that
        // clears the condition, so the next advance would refuse identically,
        // and a ten-day unattended run would stop dead on a state it cannot fix.
        // That is the difference between a guard and a wall.
        //
        // THIS IS NOT HYPOTHETICAL. On a bare `--quicktest` map there is no bed
        // anywhere, so `TakeToBedGate` answers `no-bed` for every colonist and
        // EVERY triage verdict is `no-rescuer` with a null margin (orchestrator
        // bench pass, 2026-09-01). A refusal keyed on "somebody is bleeding"
        // rather than on the comparison would have made `advance` unusable on
        // the standard bench.
        //
        // The casualty is not lost by staying quiet here: `advance` HALTED on
        // the downing when it happened (722c951 #1), `digest.work_coverage` and
        // `pawn.health.ticks_until_bleedout` carry it at every glance, and
        // `triage` names the gate that refused each candidate — `no-bed` says
        // build a bed, which is an act, where "refuse to advance" says nothing.
        //
        // THE OTHER BOUNDARY, STATED: `TakeToBedGate("rescue", …)` bottoms out
        // in `HealthAIUtility.CanRescueNow` -> `WantsToBeRescued`, whose first
        // two clauses are `!pawn.Downed` and `pawn.InBed()`. So a STANDING
        // bleeder and a bleeder ALREADY IN BED both yield no rescuer and no
        // refusal — correctly, because neither needs carrying. A patient in bed
        // whose DOCTOR cannot reach them in time is a different comparison (tend
        // travel, not rescue travel) and is not claimed here.
        //
        // Returns null when nothing is too slow. Non-null is the refusal payload.
        internal static Dictionary<string, object> BleedoutDeadline(Map map, int pathCap = PathCandidateCap)
        {
            if (map == null) return null;
            List<Pawn> colonists;
            try { colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned); }
            catch { return null; }

            Dictionary<string, object> worst = null;
            long worstMargin = long.MaxValue;
            foreach (var patient in colonists)
            {
                if (patient == null || patient.Dead) continue;
                // Gate 2 — the one float.
                float rate;
                try { rate = patient.health.hediffSet.BleedRateTotal; }
                catch { continue; }
                if (rate <= 0f) continue;
                // Gate 3 — the clock, then the row.
                if (!IsCasualty(patient, out var clock, out bool downed, out bool tend)) continue;
                if (clock == null || !(clock["ticks"] is int) && !(clock["ticks"] is long)) continue;
                var row = CasualtyRow(map, patient, colonists, pathCap, clock, downed, tend);
                if ((row["verdict"] as string) != "too-slow") continue;
                long margin = row["margin_ticks"] is int mi ? mi
                    : row["margin_ticks"] is long ml ? ml : 0L;
                if (margin >= worstMargin) continue;
                worstMargin = margin;
                worst = row;
            }
            return worst;
        }

        private static Dictionary<string, object> Refusal(Pawn p, string gate, string reason)
            => new Dictionary<string, object>
            {
                ["pawn"] = p.thingIDNumber,
                ["name"] = PawnSafe.Name(p),
                ["gate"] = gate,
                ["reason"] = reason,
            };

        private static Dictionary<string, object> Counts(List<object> rows)
        {
            var d = new Dictionary<string, object>();
            foreach (Dictionary<string, object> r in rows)
            {
                string v = r["verdict"] as string ?? "?";
                d[v] = d.TryGetValue(v, out var n) ? Convert.ToInt32(n) + 1 : 1;
            }
            return d;
        }

        private static string SafeJob(Pawn p)
        {
            try { return p.jobs?.curDriver?.GetReport(); } catch { return null; }
        }

        // The same block `pawn {sections:["health"]}` publishes, so the two
        // reads cannot drift: one builder, two callers.
        private static Dictionary<string, object> BleedClock(Pawn p)
        {
            try
            {
                var h = PawnSerializer.BleedoutBlock(p);
                return h != null && h["ticks"] != null ? h : null;
            }
            catch { return null; }
        }

        private static int PathTicks(Map map, Pawn pawn, IntVec3 from, IntVec3 to)
        {
            PawnPath path = null;
            try
            {
                path = map.pathFinder.FindPathNow(from, to, TraverseParms.For(pawn));
                if (path == null || !path.Found) return -1;
                return (int)Math.Round(path.TotalCost);
            }
            catch { return -1; }
            finally { path?.ReleaseToPool(); }
        }
    }
}
