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

        // A game boundary — return to main menu, load, new game — invalidates
        // every command waiting on the main thread and any advance in flight:
        // the tick stream they were queued against no longer exists. Answer
        // them all with no-active-game, exactly once, right here (1.5 blockers
        // 1 and 2). Without this an in-flight advance never gets a result file
        // and RESUMES against the next colony, and everything queued in the
        // heartbeat window is consumed into commands/done/ with no result at
        // all.
        //
        // Callable from EITHER thread and touches no Verse, deliberately: when
        // the game is gone there is no main thread running to notice, so the
        // poller's heartbeat edge is the only detector available; the
        // GameComponent's lifecycle virtuals cover the load edge. Both routes
        // can fire for the same boundary, so the claim inside
        // TimeDriver.Abandon is interlocked and the queue is concurrent —
        // one command, one result, whoever gets there first.
        public static int ResetForGameBoundary(string detail)
        {
            int answered = 0;
            // A placement id names a cell on a map. After a boundary the map is
            // gone, so every id in the table would resolve against whatever
            // loads next — which is the shape of bug 1.5 blocker 2 one layer up
            // (an advance resuming against the next colony). Touches no Verse,
            // so it is safe on either thread. See Placements' header.
            Placements.Clear();
            if (TimeDriver.Abandon(Err.NoActiveGame, detail)) answered++;
            while (Pending.TryDequeue(out var cmd))
            {
                Outgoing.Enqueue(Result.Fail(cmd.Id, cmd.Op, Err.NoActiveGame, detail));
                answered++;
            }
            return answered;
        }

        // The one detail string both detectors use, so the caller sees the same
        // sentence whichever noticed first.
        public const string BoundaryDetail =
            "the game was unloaded while this command was in flight";
    }

    public sealed class GameSnapshot
    {
        public bool gameLoaded;
        public bool paused;
        public string speed = "";
        public int tick;
        public double fps;
        public string activeOp;

        // Spec 1.7: a force-pausing modal stops `advance` dead, and nothing
        // in the protocol could see one. Non-null ONLY while the stack is
        // non-empty, so the per-frame snapshot allocates nothing in the
        // overwhelmingly common case.
        public Dictionary<string, object> forcePause;
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

    // Sentinel a long-running verb returns from its handler: the executor's
    // auto-result is suppressed and the verb's owner (e.g. TimeDriver) writes
    // the single real result later. The one-result-per-command invariant moves
    // to that owner.
    public sealed class DeferredResult
    {
        public static readonly DeferredResult Instance = new DeferredResult();

        private DeferredResult() { }
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
