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

        public int Int(string key, int fallback) => (int)Num(key, fallback);

        public int IntReq(string key) => (int)NumReq(key);

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
                var ctx = new VerbContext { Id = cmd.Id, Op = cmd.Op, Args = new VerbArgs(cmd.Args) };
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
