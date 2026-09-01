using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ======================================================== git-bug 2d9a1da
    // HALF 1 OF THE RATES SPEC: READ THE SERIES THE GAME ALREADY KEEPS.
    //
    // The issue says "nothing in the harness samples anything" and that is true
    // of the HARNESS. It is not true of the GAME. RimWorld has recorded eleven
    // time series since the first tick of every colony ever played on this
    // bench, and `grep -rn HistoryAutoRecorder Source/AutoRimmer/` returned
    // nothing before this file: the agent had never read one.
    //
    // THE PROOF OF THE COST OF NOT HAVING THIS. Session 13's post-mortem had to
    // answer "did wealth cause the M1 raids?", and the only route was decoding
    // `HistoryAutoRecorder` OUT OF `Autosave-5.rws` BY HAND — 11 samples at a
    // 30,000-tick cadence, lifted from a save file because no verb could ask.
    // The numbers were right (peak wealth 22,530.7 against `PointsPerWealthCurve`'s
    // 14,000 free floor) and the method was a hex dump. This verb is that
    // question, asked of the running game, in one call.
    //
    // AND IT IS THE LOOP THAT MATTERS MOST. `Wealth_Total` and `ThreatPoints`
    // are both recorded, and raid points scale with wealth
    // (`RimWorld/StorytellerUtility.DefaultThreatPointsNow` evaluates
    // `PointsPerWealthCurve` against `PlayerWealthForStoryteller`). So "we died
    // to raiders, build more guns" raises the threat it is answering, and the
    // game has been graphing BOTH SIDES of that feedback loop the whole time.
    //
    // ------------------------- WHAT IS ACTUALLY THERE ------------------------
    // Eleven recorders, all Core, no DLC adds any. Verified against
    // `Data/Core/Defs/Misc/HistoryAutoRecording/HistoryAutoRecorders.xml` and
    // `HistoryAutoRecorderGroups.xml` on the bench install:
    //
    //   Wealth      (30,000 ticks): Wealth_Total, Wealth_Items,
    //                               Wealth_Buildings, Wealth_Pawns
    //   Population  (30,000 ticks): FreeColonists, Prisoners
    //   ColonistMood(30,000 ticks): ColonistMood
    //   Debug       (30,000/60,000): ThreatPoints, Adaptation, PopAdaptation,
    //                               PopIntent
    //
    // **The Debug group's `devModeOnly` gates the UI TAB AND NOTHING ELSE.**
    // `RimWorld/MainTabWindow_History.DoWindowContents` guards the group with
    // `if (!groupLocal.def.devModeOnly || Prefs.DevMode)`, while
    // `RimWorld/History.HistoryTick` loops EVERY group unconditionally. So
    // ThreatPoints is recorded on a bench with dev mode off and has been all
    // along; this verb publishes it either way and says which group it came
    // from.
    //
    // ------------------- INDEX IS TICK, AND IT IS THE GAME'S MAP -------------
    // `RimWorld/HistoryAutoRecorder.Tick` appends when
    // `TicksGame % def.recordTicksFrequency == 0`, and
    // `RimWorld/HistoryAutoRecorderGroup.DrawGraph` maps sample j to day
    // `(float)j * (float)recordTicksFrequency / 60000f`. So index j IS tick
    // j*freq, and that is the game's own arithmetic rather than ours.
    //
    // The mapping holds for a recorder that has existed since tick 0, which is
    // all eleven. It does NOT hold for a recorder a mod adds to an existing
    // save: `HistoryAutoRecorderGroup.AddOrRemoveHistoryRecorders` creates it
    // with an empty `records` at PostLoadInit and `Tick`'s `|| !records.Any()`
    // clause appends immediately, so its index 0 is whatever tick that was.
    // Rather than assume, every series publishes `last_point_tick`
    // ((count-1)*freq) and `aligned` — the game clock against where the index
    // says the last sample should be. A false `aligned` is a series whose
    // index cannot be read as a tick, said out loud instead of discovered.
    //
    // ------------------- THE STORED NUMBER IS NOT ALWAYS THE VALUE -----------
    // Two workers scale before storing, and the game says so in the def LABEL
    // rather than anywhere a program would look:
    //
    //   ThreatPoints  label "fun points /10" — the worker stores
    //                 `DefaultThreatPointsNow(Find.AnyPlayerHomeMap) / 10f`
    //   PopIntent     label "pop intent x10" — the worker stores
    //                 `StorytellerUtilityPopulation.PopulationIntent * 10f`
    //
    // An agent comparing wealth against raid points off the raw record is
    // wrong by 10x in one direction and 10x in the other, and the only warning
    // is a human-readable label. `stored_scale` publishes the multiplier and
    // NAMES THE MEMBER it recovers, for exactly those two defs and no others —
    // keyed on defName, so a modded recorder simply has no entry rather than
    // getting a guess. (`ColonistMood` stores `CurLevel * 100`, which is the
    // percent its `valueFormat` "{0}%" already advertises, so it is not a
    // hidden scale and gets no entry.)
    //
    // ============================ OBSERVERS NEVER MUTATE ====================
    // Four members audited, three of them routed around. Same discipline as
    // `WorldSafe`/`PawnSafe`, checked against the decompiled 1.6 tree:
    //
    //  * `Find.History.Groups()` returns the `autoRecorderGroups` FIELD.
    //    `AddOrRemoveHistoryRecorderGroups` is called only from the ctor and
    //    from `ExposeData`'s PostLoadInit branch, never from the getter. SAFE.
    //  * `HistoryAutoRecorderGroup.recorders`, `HistoryAutoRecorder.records`,
    //    `HistoryAutoRecorderDef.recordTicksFrequency` / `label` /
    //    `valueFormat` / `defName` are plain fields. SAFE.
    //  * `HistoryAutoRecorderDef.Worker` is a LAZY-INIT GETTER —
    //    `workerInt = (HistoryAutoRecorderWorker)Activator.CreateInstance(workerClass)`
    //    on first read. NEVER TOUCHED HERE, and not merely because of the
    //    write: calling `PullRecord()` would RE-DERIVE a number the game has
    //    already stored (and `HistoryAutoRecorderWorker_ThreatPoints` would run
    //    `DefaultThreatPointsNow` on the spot). Serialize, do not reinvent.
    //  * `Verse/Def.LabelCap` caches into `cachedLabelCap` on first read — a
    //    write on a getter that looks like a plain accessor. `def.label` is
    //    published instead.
    //  * `HistoryAutoRecorderGroup.DrawGraph` rebuilds `curves` and stamps
    //    `cachedGraphTickCount`. NEVER CALLED. `GetMaxDay()` is a pure read but
    //    is not called either: it is `(count-1)*freq/60000` and computing it
    //    here keeps the file honest about where every number comes from.
    //
    // Nothing in this file writes anything scribed, so a save-diff around any
    // number of `history` calls is byte-identical in the `autoRecorderGroups`
    // block. That is what acceptance phase 4 measures.
    internal static class HistorySafe
    {
        // Cap on the SERIES list. Vanilla has 11; a mod could add more. Capped
        // and ordered deterministically (group order, then the group's own
        // recorder order) rather than by hash, per DigestVerb's truncation
        // contract.
        internal const int SeriesCap = 40;

        // Default tail length. 32 points at the 30,000-tick vanilla cadence is
        // 16 in-game days — longer than the ten-day run this project targets,
        // so the default answer is the whole run.
        internal const int DefaultPoints = 32;

        // Default slope window, in POINTS. 4 points at 30,000 ticks is 1.5
        // days. Shorter than that and a single wealth spike (a caravan
        // arriving, a raid's corpses) is the whole slope.
        internal const int DefaultWindowPoints = 4;

        // The slope floors for a GAME recorder, and they are not the sampler's.
        // Every vanilla recorder ticks at 30,000 or 60,000 ticks, so three
        // points is already a full in-game day of span and the extrapolation
        // this project is afraid of — a per-day rate read off two in-game hours
        // — cannot happen here. Three points rather than two because a line
        // through two points is interpolation, not a fit.
        internal const int SlopeMinPoints = 3;
        internal const int SlopeMinSpanTicks = 30000;

        internal sealed class Row
        {
            public string Def, Group, Label, ValueFormat;
            public bool DevGroup;
            public int Freq;
            public List<float> Records;
        }

        // Every recorder on the game, in group order. Pure field reads.
        // `records` is read by index against a Count captured once; the list is
        // mutated only by `HistoryAutoRecorder.Tick`, which runs on the main
        // thread inside `DoSingleTick` — and this runs on the main thread at
        // the GameComponentUpdate safe point, so there is no concurrent writer.
        internal static List<Row> All()
        {
            var rows = new List<Row>();
            // `Find.History` is `Current.Game.history`, so it throws rather
            // than returning null when there is no Game. This runs from the
            // DIGEST, which is the most-called verb in the surface, and a
            // trends block that could take the whole glance down would be a
            // worse defect than a missing trends block.
            History hist;
            try { hist = Find.History; }
            catch { return rows; }
            if (hist == null) return rows;
            var groups = hist.Groups();
            if (groups == null) return rows;
            for (int g = 0; g < groups.Count; g++)
            {
                var grp = groups[g];
                if (grp == null || grp.def == null || grp.recorders == null) continue;
                for (int i = 0; i < grp.recorders.Count; i++)
                {
                    var rec = grp.recorders[i];
                    if (rec == null || rec.def == null || rec.records == null) continue;
                    rows.Add(new Row
                    {
                        Def = rec.def.defName,
                        Group = grp.def.defName,
                        // `label`, never `LabelCap` — see the audit above.
                        Label = rec.def.label,
                        ValueFormat = rec.def.valueFormat,
                        DevGroup = grp.def.devModeOnly,
                        Freq = rec.def.recordTicksFrequency,
                        Records = rec.records,
                    });
                }
            }
            return rows;
        }

        // The two defs whose stored number is not the quantity their label
        // names. Keyed on defName so an unknown (modded) recorder gets nothing
        // rather than a guess.
        internal static Dictionary<string, object> StoredScale(string defName)
        {
            switch (defName)
            {
                case "ThreatPoints":
                    return new Dictionary<string, object>
                    {
                        ["multiply_by"] = 10.0,
                        ["to_get"] = "RimWorld/StorytellerUtility.DefaultThreatPointsNow",
                        ["why"] = "HistoryAutoRecorderWorker_ThreatPoints.PullRecord stores "
                                + "DefaultThreatPointsNow(Find.AnyPlayerHomeMap) / 10f",
                    };
                case "PopIntent":
                    return new Dictionary<string, object>
                    {
                        ["multiply_by"] = 0.1,
                        ["to_get"] = "RimWorld/StorytellerUtilityPopulation.PopulationIntent",
                        ["why"] = "HistoryAutoRecorderWorker_PopIntent.PullRecord stores "
                                + "PopulationIntent * 10f",
                    };
                default:
                    return null;
            }
        }

        // The one series lookup the digest's `trends` block needs. Returns the
        // slope of a named recorder over its last `windowPoints` samples, in
        // units per DAY, or null when the recorder does not exist or has too
        // few points. Two field reads and a walk of at most `windowPoints`
        // floats — cheaper than `time`, which is nine field reads plus four
        // GenLocalDate calls.
        internal static object SlopePerDay(List<Row> rows, string defName, int windowPoints)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Def != defName) continue;
                var r = rows[i];
                return Rates.SlopePerDay(r.Records, r.Freq, windowPoints,
                    SlopeMinPoints, SlopeMinSpanTicks, out _, out _);
            }
            return null;
        }

        internal static object Latest(List<Row> rows, string defName)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Def != defName) continue;
                var rec = rows[i].Records;
                if (rec.Count == 0) return null;
                return Math.Round((double)rec[rec.Count - 1], 3);
            }
            return null;
        }
    }

    // ============================== THE SLOPE ===============================
    //
    // ORDINARY LEAST SQUARES, NOT AN ENDPOINT DIFFERENCE, and the reason is the
    // shape of the data rather than a preference for the fancier estimator. A
    // colony's stocks move in LUMPS: a hunt returns 200 nutrition at once, a
    // caravan drops 500 silver, a meal is eaten. An endpoint difference over a
    // window is the difference between two single samples, so one lump at
    // either end IS the answer; the regression weights every sample in the
    // window and a lump moves it by 1/n.
    //
    // A SLOPE NEEDS A WINDOW AND THE WINDOW IS PUBLISHED, always, beside the
    // number. Two guards, both stated rather than implied:
    //
    //   * MIN POINTS. Two samples define a line through anything. Three is the
    //     floor at which the regression is doing arithmetic rather than
    //     interpolation.
    //   * MIN SPAN. This is the one that matters, and it is the trap the issue
    //     is about. At the sampler's 2,500-tick cadence three points span 5,000
    //     ticks — two in-game hours — and reporting a PER-DAY rate off that is
    //     a 12x extrapolation presented as a measurement. The caller sees
    //     `span_ticks` on every slope so the extrapolation factor is always
    //     visible, and below the floor the answer is null rather than a number
    //     with a caveat nobody reads.
    //
    // Null, never a sentinel: `61794cd` established that `int.MaxValue` is
    // published as null because 2147483647 reads like a real deadline, and the
    // same argument applies to a slope of 0 that means "not yet known".
    internal static class Rates
    {
        // Ticks in an in-game day. `Verse/GenDate.TicksPerDay`, reproduced as a
        // constant rather than referenced so this class has no Verse dependency
        // and can be reasoned about (and re-derived by the acceptance suite)
        // without the game.
        internal const double TicksPerDay = 60000.0;

        // Least-squares slope of (x, y) in units per DAY, where x is a TICK.
        // `points` is the number of trailing samples to use.
        //
        // Returns null when there are fewer than `minPoints`, when the span is
        // shorter than `minSpanTicks`, or when every x is identical (a vertical
        // fit has no slope). `usedPoints` and `spanTicks` are set either way so
        // a caller can say WHY it got null.
        // NO DEFAULT OVERLOAD, deliberately. The floors are different for the
        // two callers — a game recorder samples every 30,000 ticks and the
        // sampler every 2,500 — and a shared default would have been right for
        // one of them and silently wrong for the other. Both pass their own.

        internal static object SlopePerDay(IList<float> values, int tickStep,
            int windowPoints, int minPoints, int minSpanTicks,
            out int usedPoints, out int spanTicks)
        {
            usedPoints = 0;
            spanTicks = 0;
            if (values == null || tickStep <= 0) return null;
            int n = values.Count;
            if (n < minPoints) { usedPoints = n; return null; }
            int take = windowPoints < n ? windowPoints : n;
            if (take < minPoints) return null;
            int from = n - take;
            usedPoints = take;
            spanTicks = (take - 1) * tickStep;
            if (spanTicks < minSpanTicks) return null;
            // x in ticks RELATIVE to the window's first sample: absolute ticks
            // reach 10^7 on a long colony and squaring them inside a float sum
            // is how a regression quietly loses its low bits.
            double sx = 0, sy = 0;
            for (int i = 0; i < take; i++)
            {
                sx += (double)i * tickStep;
                sy += values[from + i];
            }
            double mx = sx / take, my = sy / take;
            double num = 0, den = 0;
            for (int i = 0; i < take; i++)
            {
                double dx = (double)i * tickStep - mx;
                num += dx * (values[from + i] - my);
                den += dx * dx;
            }
            if (den <= 0) return null;
            return Math.Round(num / den * TicksPerDay, 4);
        }

        // Same regression over an explicit (tick, value) pair list — the ring
        // buffer's shape, where the sample ticks are real readings rather than
        // an arithmetic series. A sample can be LATE (the cadence check runs
        // inside `GameComponentTick`, and a frame at Ultrafast covers up to 30
        // ticks) so the x values are not exactly evenly spaced and must not be
        // assumed to be.
        internal static object SlopePerDay(IList<int> ticks, IList<double> values,
            int minPoints, int minSpanTicks, out int usedPoints, out int spanTicks)
        {
            usedPoints = ticks == null ? 0 : ticks.Count;
            spanTicks = 0;
            if (ticks == null || values == null || ticks.Count != values.Count) return null;
            int take = ticks.Count;
            if (take < minPoints) return null;
            spanTicks = ticks[take - 1] - ticks[0];
            if (spanTicks < minSpanTicks) return null;
            double x0 = ticks[0];
            double sx = 0, sy = 0;
            for (int i = 0; i < take; i++) { sx += ticks[i] - x0; sy += values[i]; }
            double mx = sx / take, my = sy / take;
            double num = 0, den = 0;
            for (int i = 0; i < take; i++)
            {
                double dx = (ticks[i] - x0) - mx;
                num += dx * (values[i] - my);
                den += dx * dx;
            }
            if (den <= 0) return null;
            return Math.Round(num / den * TicksPerDay, 4);
        }

        // "When does this hit zero, at this rate" — the colony analogue of
        // `61794cd`'s `ticks_until_bleedout`, which is the same idea for one
        // pawn. NULL WHEN IT IS NOT FALLING, and that is load-bearing:
        //
        //   * There is no honest finite answer for a stock that is flat or
        //     growing, and a large sentinel reads like a real deadline
        //     (61794cd's ruling).
        //   * BUT a null makes this a bad PREDICATE target. `StateWatch`'s
        //     `One()` refuses an ordering operator against null — at arm time
        //     that is a clean refusal, but MID-ADVANCE it makes `Poll` return
        //     false forever, so an advance waiting on `*_to_zero <= N` would
        //     run to its timeout the moment the stock stopped falling, which is
        //     exactly when the good news arrived. **Predicates want the
        //     `*_per_day` slope, which is always a number once the window is
        //     full.** Said here, in the digest section's own header, and in
        //     accept/2d9a1da-colony-rates.md.
        internal static object DaysToZero(object slopePerDay, double now)
        {
            if (!(slopePerDay is double s)) return null;
            if (s >= 0) return null;
            // NaN is "this field was not published on the last sample", and a
            // negative reading is the digest's own no-needers sentinel
            // (`food_days: -1` when the colony has neither colonists nor
            // prisoners). Neither is a stock level, so neither gets a countdown.
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0) return null;
            if (now == 0) return 0.0;
            return Math.Round(now / -s, 2);
        }
    }

    public static class HistoryVerb
    {
        // ============================== `history` ============================
        //
        // The game's own eleven series, read rather than re-derived. See
        // HistorySafe's header for the full audit and for why `Worker` and
        // `LabelCap` are not touched.
        //
        // TRUNCATION IS A CONTRACT (DigestVerb's rule, applied here): the
        // `values` array is capped at `points` and the cap takes the TAIL,
        // because for a time series the thing a reader would most regret losing
        // is the recent end. `values_from_index` and `dropped` are published so
        // the index-to-tick map survives the cut, and `order` says which end
        // was kept rather than leaving it to be inferred.
        [Verb("history")]
        public static object History(VerbContext ctx)
        {
            ctx.Args.NearMiss("points", "limit", "count");
            ctx.Args.NearMiss("window_points", "window");
            var wanted = ctx.Args.StrList("series");
            int points = ctx.Args.Int("points", HistorySafe.DefaultPoints);
            if (points < 0 || points > 2000)
                throw new VerbArgsException("points must be 0..2000 (0 = metadata only)");
            int window = ctx.Args.Int("window_points", HistorySafe.DefaultWindowPoints);
            if (window < 2 || window > 2000)
                throw new VerbArgsException("window_points must be 2..2000");
            bool withValues = ctx.Args.Bool("values", true) && points > 0;

            History hist;
            try { hist = Find.History; }
            catch { hist = null; }
            if (hist == null)
                throw new VerbArgsException("no game loaded, so there is no History to read");
            int tick = Find.TickManager.TicksGame;

            var rows = HistorySafe.All();
            var list = new List<object>();
            int emitted = 0, skipped = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (wanted.Count > 0 && !wanted.Contains(r.Def)) continue;
                if (emitted >= HistorySafe.SeriesCap) { skipped++; continue; }
                emitted++;
                list.Add(SeriesRow(r, tick, points, window, withValues));
            }

            var unknown = new List<object>();
            for (int i = 0; i < wanted.Count; i++)
            {
                bool found = false;
                for (int j = 0; j < rows.Count && !found; j++) found = rows[j].Def == wanted[i];
                if (!found) unknown.Add(wanted[i]);
            }

            var data = new Dictionary<string, object>
            {
                ["game_tick"] = tick,
                ["day"] = Math.Round(tick / Rates.TicksPerDay, 3),
                ["recorders"] = rows.Count,
                ["returned"] = emitted,
                ["series"] = list,
                ["window_points"] = window,
                ["order"] = "tail-most-recent",
                // Provenance, so a reader never has to ask whether this is the
                // game's number or ours. It is the game's, in full.
                ["source"] = "RimWorld/History.Groups() -> HistoryAutoRecorderGroup.recorders "
                           + "-> HistoryAutoRecorder.records (read-only; Worker.PullRecord is "
                           + "never called and LabelCap is never read)",
            };
            if (skipped > 0) data["more"] = skipped;
            if (unknown.Count > 0)
            {
                // Named, not silently empty: asking for a series that does not
                // exist and getting an empty list back is 7382bdd's class.
                var names = new List<object>();
                for (int i = 0; i < rows.Count && i < 40; i++) names.Add(rows[i].Def);
                data["unknown_series"] = unknown;
                data["known_series"] = names;
            }
            return data;
        }

        private static Dictionary<string, object> SeriesRow(HistorySafe.Row r, int tick,
            int points, int window, bool withValues)
        {
            var rec = r.Records;
            int count = rec.Count;
            // The game's own index->day map, from
            // HistoryAutoRecorderGroup.DrawGraph: day = j * freq / 60000.
            int lastPointTick = count > 0 ? (count - 1) * r.Freq : -1;
            var d = new Dictionary<string, object>
            {
                ["def"] = r.Def,
                ["group"] = r.Group,
                ["label"] = r.Label,
                ["value_format"] = r.ValueFormat,
                // The Debug group is dev-flagged for its UI TAB only; the
                // recorder ticks regardless. Published so a reader knows why a
                // series they cannot see in game is answering here.
                ["dev_group"] = r.DevGroup,
                ["record_ticks"] = r.Freq,
                ["days_per_point"] = Math.Round(r.Freq / Rates.TicksPerDay, 4),
                ["count"] = count,
                ["last_point_tick"] = lastPointTick,
                // The index-is-tick claim, checked rather than asserted. False
                // means this recorder did not exist at tick 0 (a mod added it
                // to a live save) and its index cannot be read as a tick.
                ["aligned"] = count > 0 && Math.Abs(tick - lastPointTick) <= r.Freq,
            };
            if (count > 0)
            {
                double min = rec[0], max = rec[0];
                for (int i = 1; i < count; i++)
                {
                    if (rec[i] < min) min = rec[i];
                    if (rec[i] > max) max = rec[i];
                }
                d["first"] = Math.Round((double)rec[0], 3);
                d["last"] = Math.Round((double)rec[count - 1], 3);
                d["min"] = Math.Round(min, 3);
                d["max"] = Math.Round(max, 3);
            }
            var slope = Rates.SlopePerDay(rec, r.Freq, window,
                HistorySafe.SlopeMinPoints, HistorySafe.SlopeMinSpanTicks,
                out int used, out int span);
            d["slope_per_day"] = slope;
            d["slope_points"] = used;
            d["slope_span_days"] = Math.Round(span / Rates.TicksPerDay, 3);
            var scale = HistorySafe.StoredScale(r.Def);
            if (scale != null) d["stored_scale"] = scale;
            if (withValues)
            {
                int take = points < count ? points : count;
                int from = count - take;
                var vals = new List<object>(take);
                for (int i = from; i < count; i++) vals.Add(Math.Round((double)rec[i], 3));
                d["values"] = vals;
                d["values_from_index"] = from;
                d["dropped"] = from;
            }
            return d;
        }
    }
}
