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

        public VerbArgs(Dictionary<string, object> raw) { this.raw = raw ?? Empty; }

        public bool Has(string key) => raw.ContainsKey(key);

        // The parsed value as-is (object/array args validated by the caller).
        public object Raw(string key) => raw.TryGetValue(key, out var v) ? v : null;

        public string Str(string key, string fallback = null)
        {
            if (!raw.TryGetValue(key, out var v)) return fallback;
            if (v is string s) return s;
            throw new VerbArgsException($"arg '{key}' must be a string");
        }

        public string StrReq(string key)
            => Str(key) ?? throw new VerbArgsException($"missing required arg '{key}' (string)");

        public bool Bool(string key, bool fallback)
        {
            if (!raw.TryGetValue(key, out var v)) return fallback;
            if (v is bool b) return b;
            throw new VerbArgsException($"arg '{key}' must be a bool");
        }

        public double Num(string key, double fallback)
        {
            if (!raw.TryGetValue(key, out var v)) return fallback;
            if (v is double d) return d;
            throw new VerbArgsException($"arg '{key}' must be a number");
        }

        public double NumReq(string key)
        {
            if (!raw.TryGetValue(key, out var v)) throw new VerbArgsException($"missing required arg '{key}' (number)");
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
            if (aliases == null || raw.ContainsKey(key)) return;
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
            if (!raw.TryGetValue(key, out var v)) return result;
            if (!(v is List<object> list)) throw new VerbArgsException($"arg '{key}' must be an array of strings");
            foreach (var item in list)
            {
                if (item is string s) result.Add(s);
                else throw new VerbArgsException($"arg '{key}' must be an array of strings");
            }
            return result;
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
        public static Result Execute(PendingCommand cmd)
        {
            try
            {
                var ctx = new VerbContext { Id = cmd.Id, Op = cmd.Op, Args = new VerbArgs(cmd.Args), Command = cmd };
                return Result.Success(cmd.Id, cmd.Op, cmd.Verb.Handler(ctx));
            }
            catch (VerbArgsException e)
            {
                return Result.Fail(cmd.Id, cmd.Op, Err.BadArgs, e.Message);
            }
            catch (Exception e)
            {
                return Result.Fail(cmd.Id, cmd.Op, Err.Exception, e.ToString());
            }
        }
    }
}
