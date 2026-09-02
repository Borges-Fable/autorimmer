using System;
using System.Collections.Generic;
using System.Globalization;
using Verse;

namespace AutoRimmer
{
    // ============================================================ spec 1.6 ===
    // `advance {until:{condition|layout}}` — A HALT SOURCE THAT IS A QUESTION
    // ABOUT STATE, not a tap on an event.
    //
    // WHY IT EXISTS (git-bug fc287ba). Every shipped `until` matcher hooks
    // `Journal.OnEvent` and halts on something that HAPPENED. Nothing is
    // emitted when a continuous value crosses a threshold and nothing at all is
    // emitted when a building finishes, so the most ordinary request an agent
    // can make — "advance until the thing I asked for is done" — had no
    // spelling. Session 18 met M2 by advancing 2000 ticks, looking, and
    // advancing 6000 more. Dorian asked whether the 2000 was part of the
    // instructions. It was not; it was a guess, and there was nothing better.
    //
    // AND A PROGRESS SHELL IS NOT THE ANSWER, which is the structural half.
    // Every look is a protocol round trip against a 0.25–1 s floor, and BETWEEN
    // two looks an agent with no way to sleep must still guess a tick count —
    // so a shell hands back the same arbitrary number plus latency. The halting
    // decision belongs inside the loop that is running the ticks.
    //
    // -------------------------- WHY THIS IS SMALL ----------------------------
    // `TimeDriver.Step` ALREADY DOES per-frame state-predicate halting: it
    // polls `WindowStack.WindowsForcePause`, `CurTimeSpeed == Paused` and the
    // stall watchdog every frame and halts on them. This is an addition to an
    // existing poll site, not a new category of machinery. The driver also
    // already has a halt source that is not an `OnEvent` journal event
    // (`Journal.OnRedError`), so a non-event halt is precedented too.
    //
    // FRAMES, NOT TICKS, are the cadence unit, counted the way
    // `AlertScanner.Tick` counts them. 1.8 deleted the per-frame tick budget
    // (`Config`: "`advanceBudgetMs` … is GONE"), so there is no tick loop to
    // spend a budget in; `Step()` runs once per frame and that is the only
    // legal evaluation site — `TimeDriver.Notice` is documented "any thread,
    // called synchronously from Journal.Emit" and a predicate cannot live
    // there.
    //
    // ------------------------- THE 204-TICK FLOOR ----------------------------
    // `ResourceCounter.ResourceCounterTick` is
    // `if (Find.TickManager.TicksGame % 204 == 0) { UpdateResourceCounts(); }`,
    // and `TotalHumanEdibleNutrition` reads the dictionary that tick fills. So
    // for ANY `resources.*` path a cadence finer than 204 ticks buys nothing,
    // and "halts within one cadence window" cannot mean better than ~204 ticks
    // no matter what `every_frames` says. Stated here rather than enforced: the
    // cadence is in frames and the floor is in ticks, and the conversion moves
    // with the speed the advance is running at.
    //
    // ------------------------------- COST ------------------------------------
    // A PREDICATE IS NOT ONE PRICE. `resources.food_days` walks the counted
    // amounts calling `GetStatValueAbstract(Nutrition)` per def; `power.gain_w`
    // walks every net and every powerComp; `threats.danger` costs a
    // `CalculateDangerRating` at most once per 101 ticks; anything under
    // `colonists.list[*]` costs a `Room.Role` — a full room analysis — PER
    // COLONIST, and DigestVerb calls that the most expensive line in the file.
    // So only the ONE section a path names is built, never the whole digest,
    // and every advance publishes what its predicate actually cost in ms per
    // frame. The measurement is the control, not a promise in a comment.
    //
    // ------------------------ OBSERVERS NEVER MUTATE -------------------------
    // Evaluation is exactly as read-only as the digest it reads, and no more:
    // `threats.danger` writes `dangerRatingInt`/`lastUpdateTick`, and
    // `colonists.list[*].room` writes `role`/`stats` and clears
    // `statsAndRoleDirty`. Both are idempotent, RNG-free and unscribed, which
    // is the project's real invariant — nothing SCRIBED changes and the shared
    // `Rand` stream is untouched — and is the same ruling `CostListCalculator`'s
    // cache and `BuildableDef.PlaceWorkers` already got. "No mutation
    // observable" would either forbid two useful paths or be vacuous.
    //
    // ---------------------------- WHERE THIS GOES ----------------------------
    // The same gap exists for research (progress is published, no `until`),
    // bills (`targetCount` is settable, nothing reports done-so-far), growing
    // (`Plant.Growth` is published nowhere) and healing (hediffs, but no bleed
    // clock — git-bug 61794cd). A ten-day run wants all four constantly. They
    // are NOT closed here, and the surface is shaped so that closing them later
    // is a subclass and one parse branch rather than a second mechanism:
    // anything the digest publishes is already reachable by path, and anything
    // that is a NAMED COMMITMENT rather than a global reading is a `StateWatch`
    // beside `LayoutWatch`. `until:{research:"Electricity"}` and
    // `until:{bill:"bl-3"}` are that subclass; nothing in TimeDriver has to
    // learn what they mean.
    public abstract class StateWatch
    {
        // The advance's halt reason token. `condition` and `layout` today.
        public abstract string Reason { get; }

        // True once the watch is satisfied. `evidence` is what goes in
        // `halted_on`, and it is the ONLY evidence the acceptance can have:
        // `AgentGameComponent.DrainCommands` fails every non-`pause` command
        // with Err.Busy while an advance is in flight, so a predicate halt
        // cannot be cross-checked from inside the advance.
        public abstract bool Poll(int tick, out Dictionary<string, object> evidence);

        // Published on EVERY exit, not only a successful halt — a layout that
        // times out has to say which elements were still unresolved and why,
        // which is a strictly better failure report than the fixed-tick advance
        // it replaces.
        public abstract Dictionary<string, object> Report();

        // How many frames between polls. Read once when the watch is armed.
        public int EveryFrames = Config.ConditionScanFrames;

        // ------------------------------------------------------- the parse --

        // The state-matcher keys and the sibling options, so `TimeDriver` can
        // refuse a second matcher and an unknown key without knowing what any
        // of them mean — and so adding a family (research, a bill, a plant's
        // growth) touches this file and nothing else.
        public static readonly string[] MatcherKeys = { "condition", "layout" };
        public static readonly string[] OptionKeys = { "every_frames" };

        // Returns null when `u` names no state matcher — the four journal-tap
        // matchers are still TimeDriver's own parse.
        public static StateWatch Parse(Dictionary<string, object> u)
        {
            StateWatch w = null;
            if (u.ContainsKey("condition")) w = PathWatch.Parse(u["condition"]);
            else if (u.ContainsKey("layout")) w = LayoutWatch.Parse(u["layout"]);
            if (w == null) return null;

            if (u.TryGetValue("every_frames", out var ef))
            {
                if (!(ef is double d) || d < 1 || d > 600)
                    throw new VerbArgsException(
                        "until.every_frames must be a whole number of frames in 1..600 (how often "
                        + "the predicate is evaluated). Frames, not ticks: 1.8 deleted the tick "
                        + "budget and Step() runs once per frame. Note that for any `resources.*` "
                        + "path nothing finer than ~204 ticks is observable at all — "
                        + "ResourceCounter.ResourceCounterTick updates on `TicksGame % 204 == 0`.");
                w.EveryFrames = (int)d;
            }
            return w;
        }
    }

    // ======================================================= the path form ===
    //
    // `until:{condition:{path, op, value, edge?, quantify?}}` — a predicate over
    // the serializer field set, addressed by path.
    //
    // THE PATHS ARE THE DIGEST'S OWN, AND THE ISSUE'S EXAMPLE DID NOT PARSE.
    // `digest.colonists[*].mood_pct` is not a path into what the digest emits:
    // `DigestVerb.ColonistSection` returns `{list, total, more, order}` and NO
    // section is list-valued at the top level. The addressable spelling is
    // `colonists.list[*].mood_pct`, and a grammar written to the issue's
    // example would have silently matched nothing. A leading `digest.` is
    // accepted and stripped, because the issue and DESIGN both write it.
    //
    // THE CLOCK IS THE HIGHEST-VALUE PREDICATE AND THE ISSUE NEVER MENTIONED
    // IT (Evan, 2026-09-01: "you should focus on time not ticks silly … drift
    // will happen if you focus on ticks too much right?"). `digest.time`
    // already publishes `tick`, `hour`, `day_of_season`, `season` and `year`,
    // so `{path:"time.hour", op:">=", value:6}` is "advance until dawn" and
    // falls out for free. It is the case that makes tick arithmetic
    // unnecessary: `advance {ticks:N}` overshoots by up to
    // `MaxTicksPerFrame(speed)` — 30 at Ultrafast — and those overshoots
    // ACCUMULATE with nothing to re-anchor them, so an agent reasoning "20,000
    // ticks have passed so it must be morning" is wrong by an amount it never
    // sees. A clock predicate cannot drift, because every evaluation re-reads
    // the real clock.
    //
    // WHICH IS WHY `edge` DEFAULTS TRUE. `hour >= 6` is true all afternoon, and
    // "wait until dawn" must not return instantly at 14:00. The predicate has
    // to be observed FALSE once before a true reading halts. `edge:false` is
    // the "assert now" reading and is a different verb in spirit; it is
    // available and it is not the default.
    public sealed class PathWatch : StateWatch
    {
        public override string Reason => "condition";

        private string path;          // canonical, `digest.` stripped
        private string section;
        private List<string> keys;    // path after the section, `[*]` as a marker
        private string op;
        private object want;
        private bool edge;
        private string quantify;      // "any" | "all", only when the path stars

        // Measurement, published whether or not the predicate ever fires.
        private int evaluations;
        private double msTotal;
        private double msMax;
        private bool sawFalse;
        private int firstFalseTick = -1;
        private object lastValue;
        private string lastError;
        private bool armedTrue;

        private const string Star = "[*]";

        public static PathWatch Parse(object raw)
        {
            if (!(raw is Dictionary<string, object> c))
                throw new VerbArgsException(
                    "until.condition must be an object: {path, op, value, edge?, quantify?}, e.g. "
                    + "{\"path\":\"time.hour\",\"op\":\">=\",\"value\":6} — advance until dawn.");
            var a = new VerbArgs(c);
            a.NearMiss("path", "field", "key");
            a.NearMiss("value", "val", "threshold");

            var w = new PathWatch();
            string given = a.StrReq("path");
            w.path = Canonical(given);
            w.ParsePath(given);

            w.op = a.StrReq("op");
            switch (w.op)
            {
                case "<": case "<=": case ">": case ">=": case "==": case "!=": break;
                default:
                    throw new VerbArgsException(
                        $"until.condition.op '{w.op}' is not one of < <= > >= == != ");
            }
            if (!c.ContainsKey("value"))
                throw new VerbArgsException(
                    "until.condition needs 'value' — the number, string, bool or null the path is "
                    + "compared against. It is required even for `!=`, because a comparison with "
                    + "no right-hand side is not a predicate.");
            w.want = c["value"];
            if (!(w.want == null || w.want is double || w.want is string || w.want is bool))
                throw new VerbArgsException(
                    "until.condition.value must be a number, a string, a bool or null");
            w.edge = a.Bool("edge", true);

            bool starred = w.keys.Contains(Star);
            string q = a.Str("quantify");
            if (q != null)
            {
                if (q != "any" && q != "all")
                    throw new VerbArgsException("until.condition.quantify must be 'any' or 'all'");
                if (!starred)
                    throw new VerbArgsException(
                        $"until.condition.quantify was given but '{w.path}' names no list — a "
                        + "quantifier needs a `[*]` segment, e.g. colonists.list[*].mood_pct");
                w.quantify = q;
            }
            else w.quantify = starred ? "any" : null;
            return w;
        }

        private static string Canonical(string given)
        {
            string p = (given ?? "").Trim();
            if (p.StartsWith("digest.", StringComparison.Ordinal)) p = p.Substring(7);
            return p;
        }

        private void ParsePath(string given)
        {
            string p = Canonical(given);
            if (p.Length == 0)
                throw new VerbArgsException("until.condition.path is empty");
            var parts = p.Split('.');
            // The SECTION segment can carry a bracket too, and it must not end
            // up inside the section NAME: the issue's own example is
            // `colonists[*].mood_pct`, and taking `colonists[*]` as a section
            // name would refuse it with "not a digest section" — technically a
            // refusal, but naming the wrong problem. Split the head the same
            // way every other segment is split, then let `[*]` fail against the
            // section dict with the message that is actually true: no digest
            // section is list-valued at the top level, so the addressable
            // spelling is `colonists.list[*].mood_pct`.
            int headBracket = parts[0].IndexOf('[');
            section = headBracket < 0 ? parts[0] : parts[0].Substring(0, headBracket);
            if (!DigestVerb.IsPredicateSection(section))
                throw new VerbArgsException(
                    $"'{section}' is not a digest section a predicate can address. The sections "
                    + "are: " + string.Join(", ", DigestVerb.PredicateSections)
                    + ". (`changed` is deliberately absent: it is a journal delta since a seq the "
                    + "caller passed, not a reading of colony state, so a predicate over it would "
                    + "be asking a question about the past.) A path may be written with or "
                    + "without a leading `digest.`.");
            keys = new List<string>();
            // The head contributes only its bracket: its NAME is the section
            // and is already consumed, so `colonists[*]` leaves a bare `[*]`
            // that Walk applies to the section dictionary and refuses with the
            // sentence that is actually true — "colonists is not a list".
            if (headBracket >= 0) AddIndex(parts[0].Substring(headBracket), p);
            for (int i = 1; i < parts.Length; i++)
            {
                string seg = parts[i];
                if (seg.Length == 0)
                    throw new VerbArgsException($"until.condition.path '{p}' has an empty segment");
                // `list[*]` and `list[3]` split into the key and the index.
                int br = seg.IndexOf('[');
                if (br < 0) { keys.Add(seg); continue; }
                if (br == 0)
                    throw new VerbArgsException(
                        $"until.condition.path segment '{seg}' is malformed — write `list[*]` for "
                        + "every element or `list[0]` for one");
                keys.Add(seg.Substring(0, br));
                AddIndex(seg.Substring(br), p);
            }
        }

        // `[*]` or `[N]`, verbatim including the brackets.
        private void AddIndex(string bracketed, string whole)
        {
            if (!bracketed.EndsWith("]", StringComparison.Ordinal))
                throw new VerbArgsException(
                    $"until.condition.path '{whole}' has an unclosed `[` — write `list[*]` for "
                    + "every element or `list[0]` for one");
            string inside = bracketed.Substring(1, bracketed.Length - 2);
            if (inside == "*") { keys.Add(Star); return; }
            if (int.TryParse(inside, NumberStyles.Integer, CultureInfo.InvariantCulture,
                             out int idx) && idx >= 0)
            {
                keys.Add("#" + idx);
                return;
            }
            throw new VerbArgsException(
                $"until.condition.path index '[{inside}]' must be `*` or a non-negative whole "
                + "number");
        }

        // Called once at arm time. Refuses a path that does not resolve, which
        // is the whole point: a predicate that silently matches nothing is
        // 7382bdd's class with a tick budget attached, and it would present as
        // an advance that ran to its timeout for no stated reason.
        public string Arm(Map map, int tick)
        {
            var values = Resolve(map, out string refusal);
            if (refusal != null) return refusal;
            bool ok = Compare(values, out lastError);
            if (lastError != null) return lastError;
            evaluations = 0;   // the arm read is validation, not a measurement
            msTotal = 0;
            armedTrue = ok;
            if (!ok) { sawFalse = true; firstFalseTick = tick; }
            return null;
        }

        // ==================================================== git-bug 1113019 ==
        // THE ENFORCEMENT AND THE PUBLISHED FIELD READ ONE BACKING FIELD EACH.
        //
        // `TimeDriver.UnreachableHalt` refuses an advance whose halt cannot
        // happen — already true at arm time, `edge` required, no positive bound
        // — and Evan's ruling is that it must be DERIVED from `true_when_armed`
        // rather than from a second computation of the same thing, "so the field
        // and the enforcement cannot disagree". These two properties are that
        // derivation, and `Report()` below publishes THEM rather than the fields
        // directly, so there is exactly one route to each answer.
        public bool TrueWhenArmed => armedTrue;
        public bool EdgeRequired => edge;

        // The predicate and the reading that satisfied it, for a refusal that
        // has no `data` block to put them in (Poller.BuildResultJson drops
        // `data` on a failure, so every number a refusal reports has to be in
        // `error.detail`).
        public string DescribeAtArm()
        {
            string q = quantify == null ? "" : $" quantify={quantify}";
            return $"{path} {op} {Show(want)}{q} observed={Show(lastValue)}";
        }

        public override bool Poll(int tick, out Dictionary<string, object> evidence)
        {
            evidence = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok;
            try
            {
                var values = Resolve(PawnSafe.CurrentMap(), out string refusal);
                if (refusal != null) { lastError = refusal; return false; }
                ok = Compare(values, out lastError);
            }
            finally
            {
                sw.Stop();
                evaluations++;
                double ms = sw.Elapsed.TotalMilliseconds;
                msTotal += ms;
                if (ms > msMax) msMax = ms;
            }
            if (lastError != null) return false;
            if (!ok)
            {
                if (!sawFalse) { sawFalse = true; firstFalseTick = tick; }
                return false;
            }
            // THE EDGE. A false-at-start predicate is the normal case for a
            // clock (`hour >= 6` is true all afternoon) and requiring the
            // crossing is the difference between "wait until" and "assert now".
            if (edge && !sawFalse) return false;
            evidence = new Dictionary<string, object>
            {
                ["kind"] = "condition",
                ["path"] = path,
                ["op"] = op,
                ["value"] = want,
                // The value that actually tripped it — the path, the reading and
                // the tick, the way the other matchers report a def and a seq.
                ["observed"] = lastValue,
                ["quantify"] = quantify,
                ["tick"] = (double)tick,
            };
            return true;
        }

        public override Dictionary<string, object> Report()
        {
            var d = new Dictionary<string, object>
            {
                ["kind"] = "condition",
                ["path"] = path,
                ["section"] = section,
                ["op"] = op,
                ["value"] = want,
                ["quantify"] = quantify,
                // Through the properties, not the fields — see the 1113019 note
                // beside them: the arm-time refusal is derived from the same two
                // accessors, so what the envelope says and what the mod enforced
                // are one answer and not two.
                ["edge"] = EdgeRequired,
                // True when the predicate was ALREADY satisfied at arm time. With
                // edge:true that means the advance waited for it to go false and
                // true again; with edge:false it halted on the first frame. Either
                // way the caller should know it asked a question that was already
                // answered.
                //
                // 1113019: with edge:true and no positive `timeout_ticks` that
                // wait can never end, so THAT combination is now refused at arm
                // time and this field is the refusal's own evidence. It still
                // ships true here for the bounded case, which is unchanged.
                ["true_when_armed"] = TrueWhenArmed,
                ["saw_false"] = sawFalse,
                ["first_false_tick"] = firstFalseTick >= 0 ? (object)firstFalseTick : null,
                ["observed_last"] = lastValue,
                ["every_frames"] = EveryFrames,
                // The cost, measured, because "a predicate that is expensive per
                // evaluation is a defect even if correct" and a comment cannot
                // establish that. There is no advance BUDGET to measure against
                // any more (1.8 deleted it), so the unit is ms per evaluation and
                // ms per frame.
                ["evaluations"] = evaluations,
                ["eval_ms_total"] = Math.Round(msTotal, 4),
                ["eval_ms_avg"] = evaluations > 0 ? Math.Round(msTotal / evaluations, 4) : 0d,
                ["eval_ms_max"] = Math.Round(msMax, 4),
            };
            if (lastError != null) d["last_error"] = lastError;
            if (section == "resources")
                d["floor_note"] = "ResourceCounter.ResourceCounterTick updates on "
                    + "`TicksGame % 204 == 0`, so no `resources.*` reading changes more often "
                    + "than every 204 ticks and a finer cadence buys nothing.";
            return d;
        }

        // ------------------------------------------------------- the values --

        // The ONE section the path names, never the whole digest — see the cost
        // note in this file's header.
        private List<object> Resolve(Map map, out string refusal)
        {
            refusal = null;
            if (map == null) { refusal = "no current map"; return null; }
            var root = DigestVerb.SectionFor(map, section);
            if (root == null)
            {
                refusal = $"digest section '{section}' returned nothing on this map";
                return null;
            }
            var values = new List<object>();
            if (!Walk(root, 0, values, out string why))
            {
                refusal = why;
                return null;
            }
            return values;
        }

        // Depth-first, because `[*]` fans out. `found` is distinct from a null
        // VALUE throughout: a key that is absent is a broken path and must be
        // refused, while a key whose value is null is a legitimate reading
        // (`percent` on a blueprint) and must compare.
        private bool Walk(object node, int depth, List<object> into, out string why)
        {
            why = null;
            if (depth == keys.Count) { into.Add(node); return true; }
            string key = keys[depth];
            if (key == Star)
            {
                if (!(node is List<object> list))
                {
                    why = $"'{PathTo(depth)}' is not a list, so `[*]` cannot be applied to it";
                    return false;
                }
                for (int i = 0; i < list.Count; i++)
                    if (!Walk(list[i], depth + 1, into, out why)) return false;
                return true;
            }
            if (key.Length > 1 && key[0] == '#')
            {
                if (!(node is List<object> list))
                {
                    why = $"'{PathTo(depth)}' is not a list, so it cannot be indexed";
                    return false;
                }
                int idx = int.Parse(key.Substring(1), CultureInfo.InvariantCulture);
                if (idx >= list.Count)
                {
                    why = $"'{PathTo(depth)}' has {list.Count} element(s), so [{idx}] is past its "
                        + "end. A predicate over a list that can shrink wants `[*]` and a "
                        + "quantifier, not a fixed index.";
                    return false;
                }
                return Walk(list[idx], depth + 1, into, out why);
            }
            if (!(node is Dictionary<string, object> obj))
            {
                why = $"'{PathTo(depth)}' is not an object, so it has no key '{key}'";
                return false;
            }
            if (!obj.ContainsKey(key))
            {
                why = $"'{PathTo(depth)}' has no key '{key}'. It publishes: " + KeyList(obj)
                    + ". (A path that resolves to nothing is refused rather than treated as "
                    + "never-true: an advance that runs to its timeout because a field name was "
                    + "misspelled reports nothing a caller can act on.)";
                return false;
            }
            return Walk(obj[key], depth + 1, into, out why);
        }

        private string PathTo(int depth)
        {
            var sb = new System.Text.StringBuilder(section);
            for (int i = 0; i < depth; i++)
            {
                if (keys[i] == Star) sb.Append("[*]");
                else if (keys[i].Length > 1 && keys[i][0] == '#')
                    sb.Append("[").Append(keys[i].Substring(1)).Append("]");
                else sb.Append('.').Append(keys[i]);
            }
            return sb.ToString();
        }

        private static string KeyList(Dictionary<string, object> obj)
        {
            var names = new List<string>(obj.Keys);
            names.Sort(StringComparer.Ordinal);
            if (names.Count > 24) { names.RemoveRange(24, names.Count - 24); names.Add("…"); }
            return string.Join(", ", names.ToArray());
        }

        // ---------------------------------------------------- the compare --

        private bool Compare(List<object> values, out string error)
        {
            error = null;
            if (values == null) { error = "the path resolved to nothing"; return false; }
            if (quantify == null)
            {
                lastValue = values.Count == 1 ? values[0] : null;
                return One(values.Count == 1 ? values[0] : null, out error);
            }
            // ANY over an empty list is false; ALL over an empty list is
            // vacuously true. Both are the standard readings and both are worth
            // saying out loud, because an empty colonist list is a real state.
            bool all = quantify == "all";
            bool result = all;
            object trip = null;
            for (int i = 0; i < values.Count; i++)
            {
                bool one = One(values[i], out error);
                if (error != null) return false;
                if (all)
                {
                    if (!one) { result = false; trip = values[i]; break; }
                }
                else if (one) { result = true; trip = values[i]; break; }
            }
            lastValue = trip ?? (values.Count > 0 ? values[0] : null);
            return result;
        }

        private bool One(object got, out string error)
        {
            error = null;
            if (want == null || got == null)
            {
                if (op != "==" && op != "!=")
                {
                    error = $"'{path}' read {Show(got)} and the value is {Show(want)}; `{op}` needs "
                        + "two numbers. Only == and != accept a null on either side.";
                    return false;
                }
                bool same = want == null && got == null;
                return op == "==" ? same : !same;
            }
            if (want is bool wb)
            {
                if (!(got is bool gb))
                {
                    error = $"'{path}' read {Show(got)}, which is not a bool, and the value is "
                        + $"{Show(want)}";
                    return false;
                }
                if (op != "==" && op != "!=")
                {
                    error = $"`{op}` is not an ordering on bools; use == or !=";
                    return false;
                }
                return op == "==" ? gb == wb : gb != wb;
            }
            if (want is string ws)
            {
                if (!(got is string gs))
                {
                    error = $"'{path}' read {Show(got)}, which is not a string, and the value is "
                        + $"{Show(want)}";
                    return false;
                }
                if (op != "==" && op != "!=")
                {
                    error = $"`{op}` is not an ordering on strings; use == or !=. (Season and "
                        + "weather are strings; `time.hour` and `time.day_of_season` are numbers "
                        + "and order fine.)";
                    return false;
                }
                return op == "==" ? string.Equals(gs, ws, StringComparison.Ordinal)
                                  : !string.Equals(gs, ws, StringComparison.Ordinal);
            }
            double w = (double)want;
            if (!Numeric(got, out double g))
            {
                error = $"'{path}' read {Show(got)}, which is not a number, and the value is "
                    + $"{Show(want)}. A number was expected because the value is one.";
                return false;
            }
            switch (op)
            {
                case "<": return g < w;
                case "<=": return g <= w;
                case ">": return g > w;
                case ">=": return g >= w;
                case "==": return g == w;
                default: return g != w;
            }
        }

        // The serializers emit int, long, double and float side by side —
        // `time.tick` is an int, `resources.food_days` a double — and a
        // predicate must not care which.
        private static bool Numeric(object v, out double d)
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

        private static string Show(object v)
        {
            if (v == null) return "null";
            if (v is string s) return "\"" + s + "\"";
            if (v is bool b) return b ? "true" : "false";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }

    // ===================================================== the named form ====
    //
    // `until:{layout:"ly-N"}` — halt when every element of that transaction is
    // resolved.
    //
    // WHY A NAMED FAMILY AND NOT A PATH. The natural spelling of "wait until
    // the build is done" is `construction.frames == 0`, and on the run that met
    // M2 that predicate was TRUE AT THREE SEPARATE MOMENTS: before
    // `place-layout` was ever sent (empty map), for the ~900 ticks between
    // placement and the first blueprint becoming a frame (blueprints awaiting
    // materials are not frames), and again at the end — which is the only one
    // meant. So same-tick evaluation halts instantly on a room that does not
    // exist, and REQUIRING AN EDGE IS NOT ENOUGH EITHER: the middle case is a
    // real false-to-true-to-false-to-true sequence and an edge detector halts
    // on the wrong crossing.
    //
    // What the predicate actually needs is a scope that is MONOTONE — "every
    // placement in ly-1 is resolved", where a placement is resolved when it is
    // built or cancelled and never goes back (`Placements.StateOf` carries the
    // argument). That is not expressible as `path op value` over the digest at
    // all, which is the design consequence: if the surface were only the path
    // form, the most-wanted condition in the mod could not be written.
    //
    // MONOTONE IS A CLAIM ABOUT WHEN WE LOOK, TOO. `Frame.FailConstruction`
    // destroys the frame and spawns the blueprint again, so a cell IS empty for
    // an instant — but both halves run inside one tick, and this polls once per
    // frame from `GameComponentUpdate`, never between two ticks. There is no
    // interleaving to catch.
    public sealed class LayoutWatch : StateWatch
    {
        public override string Reason => "layout";

        private string layoutId;
        private LayoutRecord record;
        private int evaluations;
        private double msTotal;
        private double msMax;
        private LayoutProgress last;

        public static LayoutWatch Parse(object raw)
        {
            if (!(raw is string id) || id.Length == 0)
                throw new VerbArgsException(
                    "until.layout must be a layout id, e.g. {\"layout\":\"ly-1\"} — the id "
                    + "`place-layout` returns and `cancel-layout` takes.");
            return new LayoutWatch { layoutId = id };
        }

        public string Arm(int tick)
        {
            record = Layouts.Get(layoutId);
            if (record == null) return Unknown(layoutId);
            last = Layouts.Progress(record);
            return null;
        }

        public override bool Poll(int tick, out Dictionary<string, object> evidence)
        {
            evidence = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { last = Layouts.Progress(record); }
            finally
            {
                sw.Stop();
                evaluations++;
                double ms = sw.Elapsed.TotalMilliseconds;
                msTotal += ms;
                if (ms > msMax) msMax = ms;
            }
            if (!last.Done) return false;
            evidence = last.Out();
            evidence["kind"] = "layout";
            evidence["tick"] = (double)tick;
            return true;
        }

        public override Dictionary<string, object> Report()
        {
            var d = last != null ? last.Out() : new Dictionary<string, object>();
            d["kind"] = "layout";
            d["name"] = record?.Name;
            d["mode"] = record?.Mode;
            d["every_frames"] = EveryFrames;
            d["evaluations"] = evaluations;
            d["eval_ms_total"] = Math.Round(msTotal, 4);
            d["eval_ms_avg"] = evaluations > 0 ? Math.Round(msTotal / evaluations, 4) : 0d;
            d["eval_ms_max"] = Math.Round(msMax, 4);
            // THE DIAGNOSIS, and it is the reason a layout that can never
            // finish is worth more than a fixed-tick advance: a timeout here
            // hands back which elements are still outstanding and WHY —
            // `awaiting-materials` on all of them is an unreachable-material
            // colony, `blocked` is something standing in the way, `ready` with
            // nobody working is a work-priority problem.
            if (last != null && last.Live > 0)
                d["unresolved_items"] = Outstanding(last);
            return d;
        }

        private const int OutstandingCap = 24;

        private static List<object> Outstanding(LayoutProgress p)
        {
            var rows = new List<object>();
            Map map = null;
            try { map = Find.CurrentMap; } catch { }
            var index = map == null ? null : ConstructionVerbs.WorkerIndexFor(map);
            var roster = map == null ? null : ConstructionSkill.Read(map);
            int now = 0;
            try { now = Find.TickManager.TicksGame; } catch { }
            for (int i = 0; i < p.LivePlacements.Count && rows.Count < OutstandingCap; i++)
            {
                var pl = p.LivePlacements[i];
                string state = Placements.StateOf(pl, out var bp, out var fr, out _);
                var live = bp ?? fr;
                var row = new Dictionary<string, object>
                {
                    ["placement_id"] = pl.Id,
                    ["def"] = pl.DefName,
                    ["stuff"] = pl.Stuff?.defName,
                    ["at"] = Positions.Out(pl.Pos),
                    ["kind"] = state,
                };
                // The FINER state, computed once per exit rather than per poll —
                // `Frame.WorkToBuild` is a GetStatValueAbstract and the cost
                // belongs to the report, not to the cadence.
                if (live != null && index != null && Placements.MapOf(pl) == map)
                {
                    row["state"] = ConstructionVerbs.Probe(map, live, index, roster,
                        out string why, out var skill);
                    // ONE VOCABULARY (git-bug e08c3e5 + f9dadc7). The timeout
                    // report, `construction`'s items and the digest's stalled
                    // roll-up all carry `state` + `why`, and the skill block
                    // where the def is gated, so an agent triaging a layout that
                    // never finished never has to learn a second set of words.
                    row["why"] = why;
                    if (skill != null && skill.Gated) row["skill"] = skill.Out();
                    // …and how long it has been that way, which is what turns
                    // "still outstanding" into "outstanding since tick N".
                    ConstructionWatch.Look(live, now).Fill(row);
                }
                rows.Add(row);
            }
            return rows;
        }

        internal static string Unknown(string wanted)
        {
            var known = Layouts.All();
            var names = new List<string>();
            for (int i = known.Count - 1; i >= 0 && names.Count < 20; i--) names.Add(known[i].Id);
            names.Reverse();
            return $"no layout '{wanted}' in this session, so there is nothing to advance until. "
                + "Layout ids are session-scoped and are cleared at a game boundary (a load, a new "
                + "game, a return to the main menu). "
                + (names.Count == 0
                    ? "This session has placed no layouts."
                    : "This session knows: " + string.Join(", ", names.ToArray())
                      + (known.Count > names.Count
                         ? " (and " + (known.Count - names.Count) + " older)" : ""));
        }
    }
}
