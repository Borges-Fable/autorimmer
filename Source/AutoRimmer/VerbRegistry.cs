using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace AutoRimmer
{
    // The verb registry: every capability the agent has is a [Verb]-attributed
    // static method discovered by scanning this assembly at startup. Attribute
    // scan over an explicit table so later specs (1.2..3.5) can add verb files
    // without all merging through one registration site.
    //
    // A verb runs on the MAIN THREAD at the GameComponentUpdate safe point by
    // default; MainThread = false opts into execution on the poller thread, and
    // such a handler must never touch Verse — it exists so status/version stay
    // answerable from the main menu, before any game is loaded.
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class VerbAttribute : Attribute
    {
        public string Op { get; }
        public bool MainThread { get; set; } = true;

        public VerbAttribute(string op) { Op = op; }
    }

    public sealed class VerbDef
    {
        public string Op;
        public bool MainThread;
        public Func<VerbContext, object> Handler;
    }

    public sealed class VerbContext
    {
        public string Id;
        public string Op;
        public VerbArgs Args;
        public PendingCommand Command;
    }

    // Thrown by VerbArgs getters; the executor turns it into a bad-args result.
    public sealed class VerbArgsException : Exception
    {
        public VerbArgsException(string detail) : base(detail) { }
    }

    // Typed accessors over the parsed "args" object. Absent + fallback => the
    // fallback; absent + Req => bad-args; present with the wrong type => bad-args
    // even for optionals — the caller is a program, and silently coercing its
    // mistakes hides them.
    public sealed class VerbArgs
    {
        public static readonly Dictionary<string, object> Empty = new Dictionary<string, object>();

        private readonly Dictionary<string, object> raw;

        // THE READ LOG — the general half of git-bug 7382bdd. Every accessor
        // on this class funnels through Look(), so the set of keys a handler
        // actually QUERIED is observed rather than declared. See the read-log
        // block at the bottom of the class for why that is not the per-verb
        // declaration session 15 measured and rejected.
        private readonly HashSet<string> queried = new HashSet<string>(StringComparer.Ordinal);

        public VerbArgs(Dictionary<string, object> raw) { this.raw = raw ?? Empty; }

        // The one gate every accessor goes through. Marking happens on the
        // LOOKUP, not on a successful hit: a verb that asks for an optional
        // key and gets nothing has still read it, and a caller who then
        // supplies it must not be refused.
        private bool Look(string key, out object v)
        {
            if (key != null) queried.Add(key);
            return raw.TryGetValue(key, out v);
        }

        public bool Has(string key) => Look(key, out _);

        // The parsed value as-is (object/array args validated by the caller).
        public object Raw(string key) => Look(key, out var v) ? v : null;

        public string Str(string key, string fallback = null)
        {
            if (!Look(key, out var v)) return fallback;
            if (v is string s) return s;
            throw new VerbArgsException($"arg '{key}' must be a string");
        }

        public string StrReq(string key)
            => Str(key) ?? throw new VerbArgsException($"missing required arg '{key}' (string)");

        public bool Bool(string key, bool fallback)
        {
            if (!Look(key, out var v)) return fallback;
            if (v is bool b) return b;
            throw new VerbArgsException($"arg '{key}' must be a bool");
        }

        public double Num(string key, double fallback)
        {
            if (!Look(key, out var v)) return fallback;
            if (v is double d) return d;
            throw new VerbArgsException($"arg '{key}' must be a number");
        }

        public double NumReq(string key)
        {
            if (!Look(key, out var v)) throw new VerbArgsException($"missing required arg '{key}' (number)");
            if (v is double d) return d;
            throw new VerbArgsException($"arg '{key}' must be a number");
        }

        // Range-checked, not an unchecked cast. `advance {ticks:1e10}` used to
        // become a garbage int silently — int.MinValue on Mono, 0 on .NET Core,
        // both undefined behaviour for an out-of-range double-to-int
        // conversion — and the caller got a misleading error about a negative
        // tick count instead of being told its number was too big (1.5 nit).
        // Fractional values still truncate, as before.
        public int Int(string key, int fallback) => ToInt(key, Num(key, fallback));

        public int IntReq(string key) => ToInt(key, NumReq(key));

        private static int ToInt(string key, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d) || d > int.MaxValue || d < int.MinValue)
                throw new VerbArgsException(
                    $"arg '{key}' must be a whole number in [-2147483648, 2147483647] (got {Show(d)})");
            return (int)d;
        }

        private static string Show(double d)
            => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        // Journal seq is a `long` (Journal.CurrentSeq), and `since`/`since_seq`
        // are compared against it. Int() would silently wrap anything past 2^31
        // into a negative — a bug that would surface only on a long-lived
        // session, which is the one place it must not (2.6 nit). JSON numbers
        // arrive as double, so exact integers hold to 2^53; the narrowing that
        // was actually reachable is gone.
        public long Long(string key, long fallback)
        {
            double d = Num(key, fallback);
            if (double.IsNaN(d) || double.IsInfinity(d) || d > 9.2233720368547758E18 || d < -9.2233720368547758E18)
                throw new VerbArgsException($"arg '{key}' is out of range for a journal seq (got {Show(d)})");
            return (long)d;
        }

        // THE NARROW HALF OF git-bug 7382bdd. Refuse a key that is obviously a
        // misspelling of one this verb reads and that would otherwise fall
        // through to a DEFAULT — the case where being wrong looks like being
        // right. Same disposition as this class's header ("present with the
        // wrong type => bad-args even for optionals — the caller is a program,
        // and silently coercing its mistakes hides them"), applied to the KEY
        // rather than to the value.
        //
        // SCOPED, not global. A per-verb declared arg set over all 120
        // registered verbs was attempted first, as that issue's comment #1
        // pre-authorized, and rejected on measurement: 22 verbs forward
        // `ctx.Args` wholesale into a helper, ~50 more read their arguments
        // through shared helpers rather than at the handler's own call sites,
        // and five shipped suite call sites build their argument dict at
        // runtime. A declaration could not be derived from or checked against
        // the code, and its drift mode is refusing a LEGITIMATE call mid-run.
        // DESIGN's 2026-09-01 entry carries the numbers.
        //
        // Aliases are supplied by the CALL SITE and never guessed globally: a
        // list that overlapped a real argument name somewhere else would refuse
        // a correct call, which is a worse bug than the one being fixed.
        public void NearMiss(string key, params string[] aliases)
        {
            if (aliases == null || Look(key, out _)) return;
            for (int i = 0; i < aliases.Length; i++)
                if (raw.ContainsKey(aliases[i]))
                    throw new VerbArgsException(
                        $"unknown arg '{aliases[i]}' — did you mean '{key}'? This verb does "
                        + $"not read '{aliases[i]}', and '{key}' is absent, so the call would "
                        + $"have used a default for '{key}' and reported success");
        }

        public List<string> StrList(string key)
        {
            var result = new List<string>();
            if (!Look(key, out var v)) return result;
            if (!(v is List<object> list)) throw new VerbArgsException($"arg '{key}' must be an array of strings");
            foreach (var item in list)
            {
                if (item is string s) result.Add(s);
                else throw new VerbArgsException($"arg '{key}' must be an array of strings");
            }
            return result;
        }

        // ------------------------------------------------- the read log ----
        // THE GENERAL HALF OF git-bug 7382bdd, and the reason it is not the
        // per-verb declaration session 15 measured and rejected.
        //
        // That measurement stands and is not re-litigated: 120 verbs, 22 of
        // whose handlers read no argument at their own call site, 88 (verb,
        // key) pairs the suites send that are read through shared helpers, and
        // five suite call sites that build their argument dict at runtime. Its
        // conclusion — a hand-written arg set could be neither derived from the
        // code nor checked against it — is a conclusion about a SECOND SOURCE
        // OF TRUTH. None of it is an objection to observing what the verb
        // actually read.
        //
        // So: `queried` is marked by Look(), every accessor funnels through
        // Look(), and `supplied − queried` is the unknown-argument set. It is
        // derived from the code that does the reading; it covers all 120 verbs
        // at once; it needs no update when a verb gains an argument; and the
        // 22-forwarder case is not a special case at all, because the log
        // follows the VerbArgs OBJECT and `TimeDriver.Start(ctx.Command,
        // ctx.Args)` hands over that same object. A runtime-built caller dict
        // is likewise a non-issue: nothing is validated against a list.
        //
        // THE ONE THING IT CANNOT DO is fire before the verb runs, and that is
        // why what ships is a REPORT and not a refusal. Measured over the tree:
        // 729 accessor call sites, ~290 of them conditional, and 73 keys across
        // 26 verbs are read only on SOME paths while the verb still returns
        // success. Refusing on those would refuse legitimate shipped calls —
        // `zone {op:"add", plant, label, dry_run:true}` (the preflight skips
        // the block that reads `plant`), `dev:spawn-thing {stockpile, pos}`
        // (`pos` is the fallback and goes unread when storage accepts),
        // `wear {pawn, thing, queue:true}` when the gate refuses, and every
        // `bill-add` refusal that returns before ValidateBillArgs reads its
        // twenty levers. So: Execute PUBLISHES `ignored_args` and refuses
        // nothing, and RefuseStray() below is the pre-mutation refusal that the
        // three verbs whose default is dangerous adopt explicitly.
        // DESIGN's 2026-09-01 entry carries the numbers.
        public List<string> Queried()
        {
            var list = new List<string>(queried);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        // Supplied but never looked at. `alsoAccepted` is for a call site that
        // must run the check EARLY, before the keys it reads later in the
        // handler have been queried; it is a local statement about one method,
        // not a registry.
        public List<string> StrayKeys(string[] alsoAccepted = null)
        {
            List<string> stray = null;
            foreach (var kv in raw)
            {
                if (queried.Contains(kv.Key)) continue;
                if (alsoAccepted != null && Array.IndexOf(alsoAccepted, kv.Key) >= 0) continue;
                (stray ?? (stray = new List<string>())).Add(kv.Key);
            }
            if (stray == null) return EmptyKeys;
            stray.Sort(StringComparer.Ordinal);
            return stray;
        }

        private static readonly List<string> EmptyKeys = new List<string>();

        // THE PRE-MUTATION GUARD, and the ONE place an unknown argument is
        // REFUSED rather than reported. A verb whose default is dangerous
        // calls this before its first step, so a stray key is refused with
        // NOTHING MUTATED — which is the whole of what git-bug 7382bdd
        // comment #7 asked for on `journal-selftest {kind:"save"}`.
        //
        // It is safe to refuse here precisely because `accepted` is the
        // enclosing verb's FULL argument list: the conditional-read problem
        // that keeps Execute in report-only mode — `journal-selftest`'s own
        // step-gated `save_name`, `power_lamps`, `error_text` and the rest —
        // is answered by naming them, at the one site that reads them, in the
        // same file.
        //
        // `accepted` is the enclosing verb's own argument names, written beside
        // the code that reads them. It exists so the refusal can NAME what the
        // verb takes, per that comment's suggested acceptance bullet; the
        // detection does not depend on it, because a key missing from that list
        // is still caught post-dispatch by the general check. Its drift mode is
        // therefore a worse MESSAGE, never a refused legitimate call — which is
        // exactly the asymmetry that made a 120-verb registry unacceptable and
        // makes a three-site one fine.
        public void RefuseStray(string op, string[] accepted, string safety)
        {
            var stray = StrayKeys(accepted);
            if (stray.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            sb.Append(Plural(stray)).Append(Join(stray)).Append(" — ").Append(op)
              .Append(" accepts ").Append(Join(accepted)).Append('.');
            string near = Suggest(stray, accepted);
            if (near != null) sb.Append(' ').Append(near);
            if (safety != null) sb.Append(' ').Append(safety);
            throw new VerbArgsException(sb.ToString());
        }

        // The `ignored_args` report. It names the stray key, the keys this call
        // ACTUALLY read — which is the caller's fastest route to the right
        // spelling, available for every verb without a declaration — and, when
        // the verb mutated, the journal seqs it wrote, so "what did that
        // dropped argument cost me" is answerable from the envelope alone.
        // That last part is the whole complaint in git-bug 7382bdd comment #7:
        // the destructive call returned a truthful echo nobody read.
        public Dictionary<string, object> StrayReport(
            string op, List<string> stray, long seqBefore, long seqAfter)
        {
            var read = Queried();
            var sb = new System.Text.StringBuilder();
            sb.Append(Plural(stray)).Append(Join(stray)).Append(" — ").Append(op)
              .Append(read.Count == 0
                  ? " read no arguments at all on this call"
                  : " read " + Join(read) + " on this call")
              .Append('.');
            string near = Suggest(stray, read.ToArray());
            if (near != null) sb.Append(' ').Append(near);
            sb.Append(stray.Count == 1 ? " It was" : " They were")
              .Append(" DROPPED and the verb RAN ANYWAY, so this result may have come "
                    + "from a default rather than from what you asked for.");
            if (seqAfter > seqBefore)
                sb.Append($" It wrote journal seq {seqBefore + 1}..{seqAfter}; read those "
                        + "rows to see what it actually did.");
            var report = new Dictionary<string, object>
            {
                ["keys"] = new List<object>(stray.ToArray()),
                ["read"] = new List<object>(read.ToArray()),
                ["detail"] = sb.ToString(),
            };
            if (seqAfter > seqBefore)
            {
                report["journal_seq_from"] = seqBefore + 1;
                report["journal_seq_to"] = seqAfter;
            }
            return report;
        }

        private static string Plural(List<string> stray)
            => stray.Count == 1 ? "unknown arg " : "unknown args ";

        private static string Join(IList<string> keys)
        {
            if (keys == null || keys.Count == 0) return "{}";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(i == keys.Count - 1 ? " and " : ", ");
                sb.Append('\'').Append(keys[i]).Append('\'');
            }
            return sb.ToString();
        }

        // A suggestion is DERIVED, not guessed from a global alias table: the
        // candidates are the keys this very call read (or, early, the enclosing
        // verb's own list). NearMiss above still covers `at` -> `pos`, which is
        // an edit distance of 3 and which no distance rule this conservative
        // would ever find — that is why both exist.
        private static string Suggest(List<string> stray, string[] candidates)
        {
            if (candidates == null) return null;
            string bestStray = null, best = null;
            int bestD = int.MaxValue;
            foreach (var s in stray)
                foreach (var c in candidates)
                {
                    int d = Distance(s, c);
                    if (d < bestD) { bestD = d; best = c; bestStray = s; }
                }
            if (best == null) return null;
            int limit = Math.Min(bestStray.Length, best.Length) >= 5 ? 2 : 1;
            return bestD <= limit ? $"Did you mean '{best}' rather than '{bestStray}'?" : null;
        }

        // Levenshtein, two rows. Keys are short; this runs once per refusal.
        private static int Distance(string a, string b)
        {
            if (a == null || b == null) return int.MaxValue;
            int n = a.Length, m = b.Length;
            if (n == 0) return m;
            if (m == 0) return n;
            var prev = new int[m + 1];
            var cur = new int[m + 1];
            for (int j = 0; j <= m; j++) prev[j] = j;
            for (int i = 1; i <= n; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    int del = prev[j] + 1, ins = cur[j - 1] + 1, sub = prev[j - 1] + cost;
                    cur[j] = Math.Min(Math.Min(del, ins), sub);
                }
                var t = prev; prev = cur; cur = t;
            }
            return prev[m];
        }
    }

    public static class VerbRegistry
    {
        private static readonly Dictionary<string, VerbDef> verbs = new Dictionary<string, VerbDef>();

        public static int Count => verbs.Count;

        public static IEnumerable<string> Ops
        {
            get
            {
                var ops = new List<string>(verbs.Keys);
                ops.Sort(StringComparer.Ordinal);
                return ops;
            }
        }

        public static VerbDef Get(string op)
            => op != null && verbs.TryGetValue(op, out var v) ? v : null;

        // Scans this assembly for [Verb] static methods of shape
        // `static object M(VerbContext)`. A malformed or duplicate registration
        // logs and is skipped — a bad verb must not take the bridge down.
        public static void RegisterAll()
        {
            foreach (var type in typeof(VerbRegistry).Assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var attr = method.GetCustomAttribute<VerbAttribute>();
                    if (attr == null) continue;
                    var ps = method.GetParameters();
                    if (method.ReturnType != typeof(object) || ps.Length != 1 || ps[0].ParameterType != typeof(VerbContext))
                    {
                        Log.Warning($"[AutoRimmer] verb '{attr.Op}' at {type.Name}.{method.Name} skipped: handlers must be static object M(VerbContext)");
                        continue;
                    }
                    if (verbs.ContainsKey(attr.Op))
                    {
                        Log.Warning($"[AutoRimmer] duplicate verb '{attr.Op}' at {type.Name}.{method.Name} skipped");
                        continue;
                    }
                    var handler = (Func<VerbContext, object>)method.CreateDelegate(typeof(Func<VerbContext, object>));
                    verbs[attr.Op] = new VerbDef { Op = attr.Op, MainThread = attr.MainThread, Handler = handler };
                }
            }
        }

        // Runs a verb and always yields exactly one Result: handler exceptions
        // become code=exception with the stack in detail, arg failures bad-args.
        //
        // AND, since git-bug 7382bdd, the UNKNOWN-ARGUMENT REPORT. The handler
        // has just run, so VerbArgs' read log is complete: every key the verb
        // asked for is marked, and anything the caller supplied that is not
        // marked was dropped on the floor. That is the defect in this issue's
        // title — `journal-selftest {kind:"save"}` returned ok:true after
        // downing three colonists, `dev:spawn-thing {at:…}` returned
        // ok:true, placed:1 three times at the wrong cell.
        //
        // IT REPORTS AND DOES NOT REFUSE, and that is a measurement, not
        // timidity: 73 keys across 26 verbs are read only on some paths while
        // the verb still succeeds, so a blanket refusal would refuse
        // legitimate calls mid-run (see VerbArgs' read-log header for the
        // named cases). The verbs whose DEFAULT is dangerous take the
        // pre-mutation guard instead — VerbArgs.RefuseStray, adopted by
        // `journal-selftest`, `pawn-fixture` and `world-fixture` — so the
        // colony-ending case is refused before it acts.
        //
        // Two channels, deliberately. `ignored_args` rides the envelope, which
        // is what the caller reads; Log.Warning is picked up by JournalHooks'
        // patch on Log.Warning, so the same finding lands as a `warning` row
        // in the run's durable record and a ten-day run can be audited for
        // dropped arguments afterwards. The log half is main-thread only,
        // because a MainThread=false handler runs on the poller thread and its
        // contract is no Verse access at all.
        public static Result Execute(PendingCommand cmd)
        {
            var ctx = new VerbContext { Id = cmd.Id, Op = cmd.Op, Args = new VerbArgs(cmd.Args), Command = cmd };
            long seqBefore = Journal.CurrentSeq;
            try
            {
                object data = cmd.Verb.Handler(ctx);
                var stray = ctx.Args.StrayKeys();
                if (stray.Count == 0) return Result.Success(cmd.Id, cmd.Op, data);

                var report = ctx.Args.StrayReport(cmd.Op, stray, seqBefore, Journal.CurrentSeq);
                // Guarded: this is a REPORT, and a report must never be able to
                // turn a command that worked into code=exception. A throw from
                // Log (or from another mod's patch on it) would otherwise be
                // caught below and answered as a failure the verb never had.
                if (cmd.Verb.MainThread)
                    try { Log.Warning("[AutoRimmer] " + cmd.Op + ": " + report["detail"]); }
                    catch { }

                // A deferred verb's single result belongs to its own writer
                // (TimeDriver), so there is no envelope of ours to attach the
                // report to; the journal row above is the only channel it has.
                // `advance` is the one such verb.
                if (data is DeferredResult) return Result.Success(cmd.Id, cmd.Op, data);

                var ok = Result.Success(cmd.Id, cmd.Op, data);
                ok.IgnoredArgs = report;
                return ok;
            }
            catch (VerbArgsException e)
            {
                return Result.Fail(cmd.Id, cmd.Op, Err.BadArgs, e.Message, cmd.Args);
            }
            catch (Exception e)
            {
                return Result.Fail(cmd.Id, cmd.Op, Err.Exception, e.ToString(), cmd.Args);
            }
        }
    }
}
