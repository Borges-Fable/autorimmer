using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AutoRimmer
{
    // Shared state between the poller thread (file I/O only) and the
    // GameComponent (main thread, all Verse access). Commands flow in via
    // Pending, results flow out via Outgoing; GameState is a volatile reference
    // to an immutable snapshot so the poller never touches Verse off-thread.
    // Pattern copied from AnalyzerBridge's BridgeRuntime, proven on this bench
    // by the spike's throwaway mod across ~950K ticks.
    public static class Runtime
    {
        public const string ModVersion = "0.1.0"; // spec 1.1 skeleton

        public static readonly ConcurrentQueue<PendingCommand> Pending = new ConcurrentQueue<PendingCommand>();
        public static readonly ConcurrentQueue<Result> Outgoing = new ConcurrentQueue<Result>();

        // Incremented every GameComponentUpdate; a stalled value means no game loaded.
        public static long Heartbeat;

        public static volatile GameSnapshot GameState = new GameSnapshot();

        public static string SessionId;
    }

    public sealed class GameSnapshot
    {
        public bool gameLoaded;
        public bool paused;
        public string speed = "";
        public int tick;
        public double fps;
        public string activeOp;
    }

    public sealed class PendingCommand
    {
        public string Id;
        public string Op;
        public VerbDef Verb;
        public Dictionary<string, object> Args; // never null
    }

    // Error taxonomy per DESIGN.md §Protocol. Codes are protocol surface —
    // per-verb codes join this set in later specs.
    public static class Err
    {
        public const string BadJson = "bad-json";
        public const string UnknownOp = "unknown-op";
        public const string BadArgs = "bad-args";
        public const string NoActiveGame = "no-active-game";
        public const string Busy = "busy";
        public const string Exception = "exception";
        public const string StaleOnRestart = "stale-on-restart";
    }

    public sealed class Result
    {
        public string Id;
        public string Op;
        public bool Ok;
        public string ErrorCode;
        public string ErrorDetail;
        public object Data; // MiniJson.Write-able tree; serialized off-thread

        public static Result Success(string id, string op, object data)
            => new Result { Id = id, Op = op, Ok = true, Data = data };

        public static Result Fail(string id, string op, string code, string detail = null)
            => new Result { Id = id, Op = op, Ok = false, ErrorCode = code, ErrorDetail = detail };
    }
}
