using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Verse;

namespace AutoRimmer
{
    // ======================================================== git-bug 2d9a1da
    // HALF 2: SAMPLE WHAT THE GAME DOES NOT.
    //
    // `ColonyRates.cs` reads the eleven series RimWorld already keeps. This
    // file exists because of what is NOT in that list: **there is no food or
    // nutrition recorder, and none for medicine, resources or power either.**
    // Food is what a ten-day survival run dies of and it is precisely what the
    // game does not graph.
    //
    // THE SHAPE OF THE ANSWER, in one sentence: `digest.resources.food_days` is
    // vanilla's `Alert_LowFood` division and it is a LEVEL. `Alert_LowFood`
    // fires AT the threshold, which is the moment it is too late to plant. What
    // an agent needs is the SLOPE — food_days falling 4 -> 3 -> 2 — so "when do
    // I hit zero" is answerable while there is still time to act. `61794cd`
    // shipped `ticks_until_bleedout` for one pawn this week; **this is that, for
    // the colony.**
    //
    // ===================== WHAT IS SAMPLED, AND WHY THAT ====================
    // NOT a new observation surface. The sampler calls
    // `DigestVerb.SectionFor(map, "resources")` and `(map, "power")` and lifts
    // named scalars out of the dictionaries the digest already builds. Three
    // consequences, all of them the point:
    //
    //   * Every accessor it touches has already been audited. The hazard note
    //     on this issue says a write-on-read bug in a sampler is MULTIPLIED
    //     rather than incidental because it runs on a schedule; consuming an
    //     existing vetted builder is how that risk goes to zero instead of
    //     being re-audited.
    //   * IT INHERITS CHANGES TO THE ARITHMETIC. A parallel worker on
    //     `spec/temp-control` is adding a ROT term to `food_days`. This file
    //     does not re-derive nutrition / needers; it reads whatever
    //     `ResourceSection` publishes under the key `food_days`, so the slope
    //     is the slope of the CURRENT definition on the day it is read, and
    //     this file needs no edit when that definition improves. (It also
    //     means a series spanning the change has a discontinuity in it. The
    //     durable sample file records the mod version in its header row so a
    //     post-mortem can see which definition a run was on.)
    //   * It is the cheapest possible design, and the research on this issue
    //     said so: 17-18 of the ~24 candidate scalars are already computed by
    //     `DigestVerb` through calls that have been vetted.
    //
    // NOT SAMPLED, deliberately, each with its reason:
    //
    //   * wealth, threat points, mood, colonists, prisoners, adaptation —
    //     THE GAME ALREADY RECORDS THESE. Serialize, do not reinvent; they come
    //     from `history`, and the `trends` block below reads their slopes from
    //     the game's records rather than from this ring.
    //   * hostiles, danger, alerts, downed/injured — these are EVENTS and the
    //     journal already has them with a tick and a seq. A rate over an event
    //     count is a worse version of a count over a window, which
    //     `Journal.CountsRange` already answers.
    //   * WEAPONS. The research on this issue found the sharpest single result
    //     in it — across all 133 vanilla alert classes NONE covers armament,
    //     and `ResourceCounter` cannot help because weapons are Uncounted — so
    //     a weapons count is a NEW DERIVATION (equipped, by pawn scan, plus
    //     spare, by `ThingsInGroup(Weapon)`, which are disjoint populations).
    //     Nothing in the digest publishes it, so there is nothing here to
    //     sample. **Filed as its own issue rather than smuggled in here**, with
    //     the sampler's field table as its landing site: adding it is one row
    //     in `Fields` once a digest section publishes the number.
    //
    // ========================= CADENCE: 2,500 TICKS =========================
    // One in-game hour. Four reasons, in the order they decide it:
    //
    //   1. It divides the vanilla recorders exactly. Every game series ticks at
    //      30,000, so 12 of our samples land per game sample and the two series
    //      join without interpolation.
    //   2. It is far above the hard floor. `RimWorld/ResourceCounter
    //      .ResourceCounterTick` is `if (TicksGame % 204 == 0)
    //      UpdateResourceCounts()`, and `TotalHumanEdibleNutrition` reads the
    //      dictionary that tick fills — so nothing under `resources.*` moves
    //      faster than every 204 ticks and a finer cadence buys literally
    //      nothing. (Same floor DESIGN's session-19 entry states for predicate
    //      evaluation.)
    //   3. 240 samples is a ten-day run — the exact volume figure this issue's
    //      body computed before any of this was built.
    //   4. It is slow enough that the per-sample cost is unmeasurable against
    //      `Journal.cs`'s 0.0039 ms/frame benchmark, and the sampler PUBLISHES
    //      its own measurement (`trends.cost`) rather than claiming that.
    //
    // WHERE IT TICKS: `AgentGameComponent.GameComponentTick`, i.e. inside
    // `Verse/TickManager.DoSingleTick`, NOT `GameComponentUpdate`.
    // `GameComponentUpdate` runs every FRAME including while the game is
    // paused, so sampling there would append identical rows at a wall-clock
    // rate while the agent sat thinking — flattening every slope toward zero
    // with data that contains no game time. A trend must only advance when game
    // time advances. `DoSingleTick` calls `Find.History.HistoryTick()`
    // immediately BEFORE `GameComponentUtility.GameComponentTick()`, so on a
    // tick where both fire our sample is taken against a history the game has
    // already updated.
    //
    // ===================== THE WINDOW, AND THE WARM-UP ======================
    // Default slope window: **24 samples = 60,000 ticks = one in-game day.**
    // A day is the natural window because a colony's food is periodic on
    // exactly that period — meals, sleep, hauling, a hunt returning — and a
    // six-point window reports the slope of lunch.
    //
    // Two floors, and `Rates`' header argues them: at least 3 points, and at
    // least 15,000 ticks of span. The span floor is the one that matters. Three
    // samples span 5,000 ticks, and turning two in-game hours into a per-day
    // rate is a 12x extrapolation presented as a measurement. Below the floor
    // the answer is `null` and `ready` is false and `not_ready_why` says which
    // floor was missed. Every slope publishes `span_ticks` beside it regardless,
    // so the extrapolation factor is never hidden.
    //
    // ========================== SAVE / LOAD: LOUDLY =========================
    // **THE RING IS VOLATILE AND IS CLEARED ON EVERY GAME BOUNDARY.** It is not
    // scribed into the save. Three reasons, and the first is the real one:
    //
    //   1. A LOAD CAN MOVE THE CLOCK BACKWARD. Loading an older save rewinds
    //      `TicksGame`, and a ring that carried samples across that would fit a
    //      regression over a discontinuity — points from a future that was
    //      rolled back, mixed with points from before it, at overlapping x
    //      values. The slope would be arithmetic performed on two different
    //      timelines. Clearing is the only reading that cannot lie.
    //   2. This issue's own acceptance demands the mutation-on-read proof be a
    //      SAVE-DIFF around a full sampling window (the method 2.4 used).
    //      Scribing our own rows into the save would put this mod's writes in
    //      the middle of the diff that exists to prove the mod does not write.
    //   3. The durable tier already exists and is better: every sample is also
    //      appended to `samples/<session>.ndjson` as it is taken, so a reload
    //      loses the live slope and loses NOTHING of the record. `4.1`'s
    //      post-mortem — this issue's named first consumer — reads that file.
    //
    // So the loss is bounded and visible rather than silent: after a load,
    // `trends.ready` is false, `points` is small and `since_tick` is now, and
    // the sample file carries a `boundary` row at the seam. A trend that
    // silently reset on load would be a trap; one that says "I have 3 points
    // starting at tick N" is a fact.
    //
    // ========================= THE DURABLE FILE =============================
    // `<protocol root>/samples/<session id>.ndjson`, one JSON object per line,
    // written by the POLLER thread from a queue the main thread enqueues into —
    // `Journal.cs`'s pattern, and the parts that are copied are copied for the
    // reasons its header gives: PEEK-write-DEQUEUE so a transient write failure
    // costs a retry rather than a hole, and one reopen in append mode after
    // `FlushFailuresBeforeReopen` consecutive failures before giving up for the
    // session. What is NOT copied: seq claims, the in-memory ring, the dedupe
    // tables and the `OnEvent`/`OnRedError` taps, none of which mean anything
    // for a periodic row.
    //
    // **AND IT IS NOT THE JOURNAL, WHICH WAS THE FIRST DESIGN AND IS WRONG.**
    // Reusing `Journal.Emit` would have been cheaper and it would have broken a
    // guard that shipped the same week. `722c951`'s unread-journal refusal is
    // built on "an advance that journaled NOTHING creates no obligation, so a
    // quiet colony never pays for this at all" (TimeDriver's own header). A
    // periodic row emitted from inside the tick loop means EVERY advance longer
    // than one cadence journals something, so every subsequent advance refuses
    // — converting a refusal that means "your colony has news you have not read"
    // into one that means "time passed". That is the guard's failure mode, not
    // its purpose, and it would also have been a change to another spec's
    // contract made silently from this branch. A separate file costs ~90 lines
    // and changes nothing anybody else relies on.
    public static class ColonySampler
    {
        // ---------------------------------------------------- the contract --
        // These five numbers are the schema. The acceptance suite re-derives
        // every one of them from this file by regex rather than repeating them,
        // so a change here fails the suite instead of silently drifting from it.
        public const int CadenceTicks = 2500;
        public const int RingCapacity = 240;
        public const int DefaultWindowPoints = 24;
        public const int SlopeMinPoints = 3;
        public const int SlopeMinSpanTicks = 15000;

        // What a sample row is. `Section`/`Key` name the digest field it is
        // lifted from, so every field is traceable to the builder that computes
        // it and nothing here re-derives a number. `Depletes` marks a STOCK
        // that running out of is a colony-ending event, which is the only kind
        // of field a "days to zero" figure means anything for — `power_gen_w`
        // is a rate and reaching zero generation is not a countdown.
        internal sealed class FieldDef
        {
            public readonly string Name, Section, Key;
            public readonly bool Depletes;

            public FieldDef(string name, string section, string key, bool depletes)
            {
                Name = name; Section = section; Key = key; Depletes = depletes;
            }
        }

        // THE FIELD SET IS FIXED AND ORDERED. This issue's body is emphatic
        // about why: "If run 3 records `food_days` and run 9 records
        // `food_nutrition` instead, those runs cannot be compared." The order
        // is the column order of the durable file and is declared in its header
        // row; adding a field appends and never renames or reorders.
        internal static readonly FieldDef[] Fields =
        {
            new FieldDef("food_days",       "resources", "food_days",       true),
            new FieldDef("food_nutrition",  "resources", "food_nutrition",  true),
            // Not a stock — it is the DIVISOR of food_days, sampled so a reader
            // can tell a larder emptying from a colony growing, and so the
            // `-1` food_days sentinel (no colonists and no prisoners) is
            // legible as the dead-colony reading it is rather than as data.
            new FieldDef("food_needers",    "resources", "food_needers",    false),
            new FieldDef("meds",            "resources", "meds",            true),
            new FieldDef("steel",           "resources", "steel",           true),
            new FieldDef("wood",            "resources", "wood",            true),
            new FieldDef("components",      "resources", "components",      true),
            new FieldDef("silver",          "resources", "silver",          true),
            new FieldDef("power_stored_wd", "power",     "stored_wd",       true),
            new FieldDef("power_gen_w",     "power",     "gen_w",           false),
            new FieldDef("power_draw_w",    "power",     "draw_w",          false),
        };

        // ---------------------------------------------------------- the ring --
        // Flat arrays, preallocated once. A ring of per-sample float[] would
        // allocate 240 small arrays and churn them; this allocates nothing at
        // all after class init, which matters for something that runs inside
        // the tick loop.
        // Stride is Fields.Length, read from the table rather than written as
        // a literal: a hand-kept stride is the classic way a table gains a row
        // and a ring starts silently overwriting its neighbour. `Fields` is
        // declared above this line, so C#'s textual static-initializer order
        // guarantees it is populated first.
        private static readonly int Stride = Fields.Length;
        private static readonly int[] ringTick = new int[RingCapacity];
        private static readonly double[] ringVal = new double[RingCapacity * Fields.Length];
        private static int ringCount;
        private static int ringHead;
        private static int lastSampleTick = int.MinValue;

        // One lock over the ring. Contended at most once per 2,500 ticks by the
        // writer; the readers are the digest and the `trends` verb, both on the
        // main thread, plus `Clear()`, which the POLLER thread can call from
        // `Runtime.ResetForGameBoundary`'s heartbeat-edge route. That last one
        // is the whole reason this is not just a main-thread invariant.
        private static readonly object ringLock = new object();

        // Cost accounting, published rather than promised — the same discipline
        // `StateWatch` uses for `eval_ms_per_frame`. This issue's acceptance
        // asks for overhead measured against `Journal.cs`'s 0.0039 ms/frame
        // benchmark; a comment cannot establish that and a bench run can.
        private static long samplesTaken;
        private static double msTotal, msMax;
        private static int firstSampleTick = -1;

        // ------------------------------------------------------- the hook ----
        // Main thread, inside DoSingleTick. Returns immediately on every tick
        // that is not a sample tick, which is 2,499 out of every 2,500.
        public static void Tick()
        {
            int tick;
            try { tick = Find.TickManager.TicksGame; }
            catch { return; }
            // `lastSampleTick` is int.MinValue before the first sample and
            // after every boundary clear, so a fresh colony is sampled on its
            // first ticked frame rather than 2,500 ticks in. The subtraction is
            // guarded against a clock that went BACKWARD (an older save loaded
            // without a boundary event, which should not happen but would
            // otherwise stall sampling until the clock caught up).
            if (lastSampleTick != int.MinValue)
            {
                int since = tick - lastSampleTick;
                if (since >= 0 && since < CadenceTicks) return;
            }
            var map = Find.CurrentMap;
            if (map == null) return;

            var sw = Stopwatch.StartNew();
            try
            {
                // The digest's own builders, once each. Everything else in this
                // method is arithmetic on the dictionaries they returned.
                var sections = new Dictionary<string, Dictionary<string, object>>(2);
                for (int i = 0; i < Fields.Length; i++)
                {
                    string s = Fields[i].Section;
                    if (sections.ContainsKey(s)) continue;
                    sections[s] = DigestVerb.SectionFor(map, s);
                }
                var row = new double[Fields.Length];
                for (int i = 0; i < Fields.Length; i++)
                {
                    row[i] = double.NaN;
                    var sec = sections[Fields[i].Section];
                    if (sec == null || !sec.TryGetValue(Fields[i].Key, out var v)) continue;
                    if (Num(v, out double d)) row[i] = d;
                }
                Store(tick, row);
                SampleLog.WriteSample(tick, row);
                lastSampleTick = tick;
                if (firstSampleTick < 0) firstSampleTick = tick;
                samplesTaken++;
            }
            catch (Exception e)
            {
                // A sampler must never be able to stop the clock. The tick hook
                // is inside DoSingleTick and a throw here would surface as a
                // red error every 2,500 ticks for the rest of the run.
                lastSampleTick = tick;
                Log.Warning("[AutoRimmer] colony sample failed: " + e);
            }
            finally
            {
                sw.Stop();
                double ms = sw.Elapsed.TotalMilliseconds;
                msTotal += ms;
                if (ms > msMax) msMax = ms;
            }
        }

        private static void Store(int tick, double[] row)
        {
            lock (ringLock)
            {
                ringTick[ringHead] = tick;
                int at = ringHead * Stride;
                for (int i = 0; i < Fields.Length; i++) ringVal[at + i] = row[i];
                ringHead = (ringHead + 1) % RingCapacity;
                if (ringCount < RingCapacity) ringCount++;
            }
        }

        // Called from `Runtime.ResetForGameBoundary` — BOTH detectors, the
        // GameComponent's load/new-game virtuals and the poller's heartbeat
        // edge — beside `Placements.Clear()` and `Layouts.Clear()`, and for the
        // same reason those are there: the state is indexed by a game that no
        // longer exists. Touches no Verse, so it is safe on either thread.
        public static void Clear()
        {
            lock (ringLock)
            {
                ringCount = 0;
                ringHead = 0;
                lastSampleTick = int.MinValue;
                firstSampleTick = -1;
                samplesTaken = 0;
                msTotal = 0;
                msMax = 0;
            }
            SampleLog.WriteBoundary();
        }

        // The two lifecycle calls the bridge makes. Wrappers rather than
        // exposing SampleLog: the durable file is this sampler's business and
        // nothing outside needs a second name for it.
        public static void InitLog(string root) => SampleLog.Init(root);

        public static void FlushSamples() => SampleLog.Flush();

        private static bool Num(object v, out double d)
        {
            switch (v)
            {
                case double x: d = x; return true;
                case int x: d = x; return true;
                case long x: d = x; return true;
                case float x: d = x; return true;
                case short x: d = x; return true;
                case byte x: d = x; return true;
                default: d = 0; return false;
            }
        }

        // ------------------------------------------------------ reading it ---
        // A snapshot of the last `want` samples in chronological order. Copied
        // under the lock and returned as plain arrays so every caller does its
        // arithmetic outside it.
        private static void Window(int want, out List<int> ticks, out List<double[]> rows)
        {
            ticks = new List<int>();
            rows = new List<double[]>();
            lock (ringLock)
            {
                int take = want < ringCount ? want : ringCount;
                int start = (ringHead - take + RingCapacity * 2) % RingCapacity;
                for (int i = 0; i < take; i++)
                {
                    int idx = (start + i) % RingCapacity;
                    ticks.Add(ringTick[idx]);
                    var r = new double[Fields.Length];
                    int at = idx * Stride;
                    for (int f = 0; f < Fields.Length; f++) r[f] = ringVal[at + f];
                    rows.Add(r);
                }
            }
        }

        private static int Points { get { lock (ringLock) return ringCount; } }

        // The per-field slope over a window, with the NaN samples dropped. A
        // field the digest did not publish on some tick (a section that threw,
        // a key that moved) leaves a hole rather than a zero, and a hole must
        // not be fitted through.
        private sealed class Fit
        {
            public object Slope;
            public int Points, SpanTicks;
            public object Now, First, Min, Max;
        }

        private static Fit FitField(int f, List<int> ticks, List<double[]> rows)
        {
            // A name that is not in the table (IndexOf returned -1) is a coding
            // error, not a runtime state — but this runs inside the digest, so
            // it answers "nothing known" rather than throwing out of the glance.
            if (f < 0) return new Fit();
            var xs = new List<int>();
            var ys = new List<double>();
            for (int i = 0; i < rows.Count; i++)
            {
                double v = rows[i][f];
                if (double.IsNaN(v)) continue;
                xs.Add(ticks[i]);
                ys.Add(v);
            }
            var fit = new Fit { Points = xs.Count };
            if (xs.Count > 0)
            {
                double min = ys[0], max = ys[0];
                for (int i = 1; i < ys.Count; i++)
                {
                    if (ys[i] < min) min = ys[i];
                    if (ys[i] > max) max = ys[i];
                }
                fit.Now = Math.Round(ys[ys.Count - 1], 3);
                fit.First = Math.Round(ys[0], 3);
                fit.Min = Math.Round(min, 3);
                fit.Max = Math.Round(max, 3);
            }
            fit.Slope = Rates.SlopePerDay(xs, ys, SlopeMinPoints, SlopeMinSpanTicks,
                                          out _, out int span);
            fit.SpanTicks = span;
            return fit;
        }

        // ================================================ the digest block ===
        //
        // `digest.trends` — THE SLOPES ONLY, no arrays, ~200 bytes.
        //
        // IN THE GLANCE ON PURPOSE, and the M1 post-mortem is the argument: that
        // run made 27 advances, 10 digests and ZERO journal calls. The agent
        // reads the digest and essentially nothing else, so a leading indicator
        // that lives only behind a verb it must remember to call is a leading
        // indicator nobody reads — which is exactly DESIGN's session-13 ruling
        // that a deterministic finding goes in the MOD rather than in a note,
        // applied one layer up.
        //
        // COST, on session 19's axis (no `Room.Role`, no `GetStatValueAbstract`,
        // no pathfind): **this is the cheapest section in the digest, cheaper
        // than `time`.** It reads NO game state for its own fields — the sample
        // ring is already in memory and this is arithmetic over at most 24
        // doubles per field — plus `HistorySafe.All()`, which is a walk of 3
        // groups and 11 recorders doing plain field reads. `time` by comparison
        // makes four `GenLocalDate` calls. So it is an affordable predicate
        // section, which is the whole point of registering it as one:
        // `advance {until:{condition:{path:"trends.food_days_per_day",
        // op:"<=", value:-1.0}}}` is "stop when the colony starts losing more
        // than a food-day per day", and nothing in the harness could ask that
        // before.
        //
        // THE READING IS UP TO ONE CADENCE STALE by construction — `as_of_tick`
        // is the last sample's tick and can be 2,500 ticks behind the clock.
        // That is a floor of the same kind as the 204-tick `ResourceCounter`
        // quantum DESIGN's session-19 entry states for `resources.*`, and it is
        // published rather than implied.
        //
        // ------------------ NULL IS A REAL ANSWER, AND A TRAP ---------------
        // `*_per_day` is null until the window has 3 points over 15,000 ticks.
        // `*_to_zero` is null WHENEVER THE STOCK IS NOT FALLING, which is most
        // of the time. `StateWatch.One()` refuses an ordering operator against
        // null, so:
        //
        //   * at ARM time that is a clean refusal naming the reading, and
        //   * MID-ADVANCE `Poll` returns false and never halts.
        //
        // So an advance waiting on `trends.food_days_to_zero <= 2` stops
        // halting the moment food stops falling — the good news — and runs to
        // its timeout. **Predicates want `*_per_day`, which is always a number
        // once `ready` is true.** `*_to_zero` is for the agent to read.
        internal static Dictionary<string, object> TrendSection(Map map)
        {
            Window(DefaultWindowPoints, out var ticks, out var rows);
            var d = new Dictionary<string, object>();
            int pts = ticks.Count;
            var food = FitField(IndexOf("food_days"), ticks, rows);
            var nutrition = FitField(IndexOf("food_nutrition"), ticks, rows);
            var meds = FitField(IndexOf("meds"), ticks, rows);
            bool ready = food.Slope != null;
            d["ready"] = ready;
            d["points"] = pts;
            d["window_points"] = DefaultWindowPoints;
            d["span_ticks"] = food.SpanTicks;
            d["as_of_tick"] = pts > 0 ? (object)ticks[pts - 1] : null;
            if (!ready)
                d["not_ready_why"] = pts < SlopeMinPoints
                    ? $"only {pts} sample(s); a slope needs {SlopeMinPoints} "
                      + $"(one every {CadenceTicks} ticks, cleared at every game boundary)"
                    : $"{pts} samples spanning {food.SpanTicks} ticks; a per-day rate is not "
                      + $"published under {SlopeMinSpanTicks} ticks of span because turning "
                      + "two in-game hours into a day rate is extrapolation, not measurement";
            // OURS — the three the game does not record and a ten-day run dies
            // of. `food_days_to_zero` is the headline: the colony's
            // `ticks_until_bleedout`.
            d["food_days_per_day"] = food.Slope;
            d["food_days_to_zero"] = Rates.DaysToZero(food.Slope, AsDouble(food.Now));
            d["nutrition_per_day"] = nutrition.Slope;
            d["meds_per_day"] = meds.Slope;
            // THE GAME'S — read from its own recorders, never re-derived. This
            // is the feedback loop the project needs to see: wealth drives
            // threat points drives raids drives "build more guns" drives wealth.
            var hist = HistorySafe.All();
            d["wealth_per_day"] = HistorySafe.SlopePerDay(hist, "Wealth_Total",
                                                          HistorySafe.DefaultWindowPoints);
            // Stored as points/10 by the game's own worker — multiplied back
            // here so the digest's number is POINTS, matching what
            // `StorytellerUtility.DefaultThreatPointsNow` returns and what a
            // reader would compare against `PointsPerWealthCurve`. `history`
            // publishes the raw record and the multiplier beside it.
            d["threat_points_per_day"] = Scale(
                HistorySafe.SlopePerDay(hist, "ThreatPoints", HistorySafe.DefaultWindowPoints),
                10.0);
            d["mood_per_day"] = HistorySafe.SlopePerDay(hist, "ColonistMood",
                                                        HistorySafe.DefaultWindowPoints);
            d["colonists_per_day"] = HistorySafe.SlopePerDay(hist, "FreeColonists",
                                                             HistorySafe.DefaultWindowPoints);
            return d;
        }

        private static object Scale(object v, double by)
            => v is double d ? (object)Math.Round(d * by, 4) : null;

        private static double AsDouble(object v) => v is double d ? d : double.NaN;

        internal static int IndexOf(string name)
        {
            for (int i = 0; i < Fields.Length; i++) if (Fields[i].Name == name) return i;
            return -1;
        }

        // ==================================================== the verb ========
        //
        // `trends` — the full read: every sampled field, its window, its slope,
        // its span, and (on request) the raw tail. `digest.trends` is the
        // headline; this is the drill-down, and it is where the COST
        // MEASUREMENT lives, because this issue's acceptance asks for overhead
        // measured against `Journal.cs`'s 0.0039 ms/frame rather than asserted.
        [Verb("trends")]
        public static object Trends(VerbContext ctx)
        {
            ctx.Args.NearMiss("window_points", "window");
            ctx.Args.NearMiss("points", "limit");
            int window = ctx.Args.Int("window_points", DefaultWindowPoints);
            if (window < SlopeMinPoints || window > RingCapacity)
                throw new VerbArgsException(
                    $"window_points must be {SlopeMinPoints}..{RingCapacity}");
            int tail = ctx.Args.Int("points", 0);
            if (tail < 0 || tail > RingCapacity)
                throw new VerbArgsException($"points must be 0..{RingCapacity} (0 = no raw values)");
            var wanted = ctx.Args.StrList("fields");

            Window(window, out var ticks, out var rows);
            List<int> tailTicks = null;
            List<double[]> tailRows = null;
            if (tail > 0) Window(tail, out tailTicks, out tailRows);

            var series = new Dictionary<string, object>();
            var names = new List<object>();
            for (int f = 0; f < Fields.Length; f++)
            {
                var fd = Fields[f];
                names.Add(fd.Name);
                if (wanted.Count > 0 && !wanted.Contains(fd.Name)) continue;
                var fit = FitField(f, ticks, rows);
                var row = new Dictionary<string, object>
                {
                    ["now"] = fit.Now,
                    ["first"] = fit.First,
                    ["min"] = fit.Min,
                    ["max"] = fit.Max,
                    ["slope_per_day"] = fit.Slope,
                    ["points"] = fit.Points,
                    ["span_ticks"] = fit.SpanTicks,
                    ["from"] = "digest." + fd.Section + "." + fd.Key,
                };
                // Only where zero is a cliff. See FieldDef.Depletes.
                if (fd.Depletes)
                    row["days_to_zero"] = Rates.DaysToZero(fit.Slope, AsDouble(fit.Now));
                if (tail > 0)
                {
                    var vals = new List<object>();
                    for (int i = 0; i < tailRows.Count; i++)
                    {
                        double v = tailRows[i][f];
                        vals.Add(double.IsNaN(v) ? null : (object)Math.Round(v, 3));
                    }
                    row["values"] = vals;
                }
                series[fd.Name] = row;
            }

            // A `fields` entry that names nothing is REPORTED, never silently
            // dropped: asking for a series that does not exist and getting a
            // short answer back is 7382bdd's class, and here the caller would
            // read the absence as "that field is flat".
            var unknownFields = new List<object>();
            for (int i = 0; i < wanted.Count; i++)
                if (IndexOf(wanted[i]) < 0) unknownFields.Add(wanted[i]);

            int pts = ticks.Count;
            var data = new Dictionary<string, object>
            {
                ["cadence_ticks"] = CadenceTicks,
                ["ring_capacity"] = RingCapacity,
                ["window_points"] = window,
                ["min_points"] = SlopeMinPoints,
                ["min_span_ticks"] = SlopeMinSpanTicks,
                ["points"] = pts,
                ["ring_points"] = Points,
                ["first_tick"] = pts > 0 ? (object)ticks[0] : null,
                ["last_tick"] = pts > 0 ? (object)ticks[pts - 1] : null,
                ["span_ticks"] = pts > 1 ? ticks[pts - 1] - ticks[0] : 0,
                ["fields"] = names,
                ["series"] = series,
                ["cost"] = Cost(),
                ["durable_file"] = SampleLog.RelativePath,
                // Said in the envelope, not only in a source comment: an agent
                // that reloads and reads a flat trend must be able to see WHY
                // it is flat.
                ["volatile"] = "the in-memory ring is CLEARED at every game boundary "
                             + "(load, new game, return to menu), because a load can move "
                             + "TicksGame BACKWARD and a regression across that seam fits two "
                             + "timelines at once. Nothing is lost: every sample is also "
                             + "appended to durable_file as it is taken, and a `boundary` row "
                             + "marks the seam there.",
            };
            if (tail > 0) data["values_from_tick"] = tailTicks.Count > 0 ? (object)tailTicks[0] : null;
            if (unknownFields.Count > 0) data["unknown_fields"] = unknownFields;
            return data;
        }

        // The measurement this issue's acceptance asks for. `Journal.cs`'s
        // benchmark is 0.0039 ms PER FRAME and this runs per SAMPLE, so the
        // comparable figure is normalised per 1,000 ticks and published beside
        // the raw numbers rather than instead of them.
        private static Dictionary<string, object> Cost()
        {
            long n = samplesTaken;
            int covered = firstSampleTick >= 0 && lastSampleTick != int.MinValue
                ? lastSampleTick - firstSampleTick : 0;
            return new Dictionary<string, object>
            {
                ["samples"] = (double)n,
                ["ms_total"] = Math.Round(msTotal, 4),
                ["ms_avg"] = n > 0 ? Math.Round(msTotal / n, 5) : 0d,
                ["ms_max"] = Math.Round(msMax, 5),
                ["ticks_covered"] = covered,
                ["ms_per_1000_ticks"] = covered > 0
                    ? Math.Round(msTotal / covered * 1000.0, 5) : 0d,
                ["benchmark"] = "Journal.cs measured 0.0039 ms/frame with an off-thread flush; "
                              + "this sampler runs once per " + CadenceTicks + " ticks, so "
                              + "ms_per_1000_ticks is the comparable figure",
            };
        }
    }

    // ===================== THE DURABLE TIER, ~90 LINES ======================
    //
    // `samples/<session id>.ndjson`. Journal.cs's flush discipline, none of its
    // event machinery. See ColonySampler's header for why this is a separate
    // file and not `Journal.Emit` (short version: a periodic row inside the
    // tick loop would make `722c951`'s unread-journal refusal fire on every
    // advance, turning "your colony has news" into "time passed").
    //
    // THREE ROW KINDS, and the header is what makes the file self-describing —
    // this issue's acceptance asks that the field set be "fixed and documented"
    // and a column list written by the code that fills it cannot drift from it:
    //
    //   {"kind":"header","sid":…,"mod":…,"game":…,"cadence_ticks":2500,
    //    "fields":[…],"depletes":[…]}
    //   {"kind":"sample","tick":…,"day":…,"wall":…,"v":{…}}
    //   {"kind":"boundary","wall":…}
    //
    // Values are an OBJECT rather than a positional array on purpose. A
    // positional row is smaller and is exactly the format that breaks when
    // somebody inserts a column in the middle; a named row costs ~15 bytes a
    // field and survives the edit. At 240 rows a run that is nothing.
    internal static class SampleLog
    {
        private const int FlushFailuresBeforeReopen = 20;   // ~10s at PollMs=500

        private static string path;
        private static string relative;
        private static StreamWriter writer;
        private static int flushFailures;
        private static readonly ConcurrentQueue<string> pending = new ConcurrentQueue<string>();

        internal static string RelativePath => relative;

        // Main thread, from the mod ctor, after the protocol root exists.
        internal static void Init(string root)
        {
            var dir = Path.Combine(root, "samples");
            Directory.CreateDirectory(dir);
            relative = "samples/" + Runtime.SessionId + ".ndjson";
            path = Path.Combine(dir, Runtime.SessionId + ".ndjson");
            writer = new StreamWriter(
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false)) { AutoFlush = true };

            string game = "unknown";
            try { game = RimWorld.VersionControl.CurrentVersionStringWithRev; } catch { }
            var fields = new List<object>();
            var depletes = new List<object>();
            for (int i = 0; i < ColonySampler.Fields.Length; i++)
            {
                fields.Add(ColonySampler.Fields[i].Name);
                if (ColonySampler.Fields[i].Depletes) depletes.Add(ColonySampler.Fields[i].Name);
            }
            Enqueue(new Dictionary<string, object>
            {
                ["kind"] = "header",
                ["sid"] = Runtime.SessionId,
                ["mod"] = Runtime.ModVersion,
                ["game"] = game,
                ["wall"] = DateTime.UtcNow.ToString("o"),
                ["cadence_ticks"] = ColonySampler.CadenceTicks,
                ["fields"] = fields,
                ["depletes"] = depletes,
                ["note"] = "one row per " + ColonySampler.CadenceTicks + " game ticks while the "
                         + "clock runs; values are lifted from the digest sections named in "
                         + "ColonySampler.Fields and are never re-derived here",
            });
        }

        internal static void WriteSample(int tick, double[] row)
        {
            if (writer == null) return;
            var v = new Dictionary<string, object>();
            for (int i = 0; i < ColonySampler.Fields.Length; i++)
                v[ColonySampler.Fields[i].Name] =
                    double.IsNaN(row[i]) ? null : (object)Math.Round(row[i], 3);
            Enqueue(new Dictionary<string, object>
            {
                ["kind"] = "sample",
                ["tick"] = tick,
                ["day"] = Math.Round(tick / Rates.TicksPerDay, 4),
                ["wall"] = DateTime.UtcNow.ToString("o"),
                ["v"] = v,
            });
        }

        // The seam. Written whenever the ring is cleared, so a reader of this
        // file can see where one game ended and another began — and, crucially,
        // where TicksGame may have gone backward.
        internal static void WriteBoundary()
        {
            if (writer == null) return;
            Enqueue(new Dictionary<string, object>
            {
                ["kind"] = "boundary",
                ["wall"] = DateTime.UtcNow.ToString("o"),
                ["note"] = "game boundary: the live ring was cleared. Ticks after this row may "
                         + "be LOWER than ticks before it.",
            });
        }

        // Any thread. Serialising here rather than in Flush keeps the poller's
        // cycle free of allocation and means a serialisation failure costs one
        // row instead of the file.
        private static void Enqueue(Dictionary<string, object> row)
        {
            try
            {
                var sb = new StringBuilder(256);
                MiniJson.Write(sb, row);
                pending.Enqueue(sb.ToString());
            }
            catch { }
        }

        // Poller thread only. PEEK, write, THEN dequeue — Journal.Flush's
        // discipline and its reasoning: a line taken from the queue before the
        // write returned is a line lost to a transient failure. Bounded the
        // same way, one reopen in append mode and then closed for the session.
        internal static void Flush()
        {
            if (writer == null) return;
            try
            {
                while (pending.TryPeek(out var line))
                {
                    writer.WriteLine(line);
                    pending.TryDequeue(out _);
                }
                flushFailures = 0;
            }
            catch (Exception e)
            {
                if (++flushFailures < FlushFailuresBeforeReopen) return;
                flushFailures = 0;
                try
                {
                    writer = new StreamWriter(
                        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(false)) { AutoFlush = true };
                }
                catch
                {
                    writer = null;
                    // A sibling file, never Log.Warning: this is the poller
                    // thread, which never touches Verse, and Log.Warning is
                    // patched straight into Journal.
                    try { File.WriteAllText(path + ".error", DateTime.UtcNow.ToString("o") + "\n" + e); }
                    catch { }
                }
            }
        }
    }
}
