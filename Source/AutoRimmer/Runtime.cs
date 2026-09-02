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
            Layouts.Clear();
            // Keyed by layout id and measured in TicksGame, so it is stale for
            // both reasons at once after a boundary. In memory by design — see
            // LayoutEnclosureWatch's header and git-bug d16a463.
            LayoutEnclosureWatch.Clear();
            // git-bug 2d9a1da, and it is here for exactly the reason the two
            // lines above it are: state indexed by a game that no longer
            // exists. A sample ring is worse than a stale placement id, though,
            // because a load can move TicksGame BACKWARD — a regression across
            // that seam would fit two timelines at once and report a slope that
            // never happened. Cleared on BOTH detectors (this method is called
            // from the GameComponent's load/new-game virtuals and from the
            // poller's heartbeat edge), touches no Verse, and writes a
            // `boundary` row to the durable sample file so the seam is visible
            // there too. See ColonySampler's SAVE / LOAD header.
            ColonySampler.Clear();
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

    // WHAT KIND OF THING AN ok:false IS — git-bug e440676.
    //
    // Measured on run m1-20260901: 691 failed steps — 325 bad-args, 200 busy,
    // 164 unread-journal, 2 rwa-game-down. 364 of them, 53%, are the protocol
    // WORKING: `busy` means "an advance is in flight, ask again", and
    // `unread-journal` is 722c951's guard refusing to let the agent advance
    // past events it has not read. Every one of them was `ok:false` with an
    // `error.code` and nothing else, identical in shape to a mod that threw. A
    // post-mortem reading "691 errors" overstates how badly that run went by
    // roughly half, and an agent reading its own transcript to decide whether
    // it is in trouble gets the same wrong answer.
    //
    // So every failure envelope carries a CLASS beside its code:
    //
    //   refused  the bench said no to what was asked, and the answer is
    //            ACTIONABLE — fix the arguments, read the journal, rescue the
    //            bleeder. The caller's next move must DIFFER from the one it
    //            just made.
    //   flow     nothing is wrong, and nothing was asked that cannot be asked
    //            again. The bench is not ready to answer this right now. The
    //            caller's next move may be THE SAME ONE, later.
    //   fault    something went wrong inside the bench. Nobody asked for this
    //            and repeating it will not help.
    //   client   the failure is on the client side of the bridge and the mod
    //            never saw the command. Emitted by `rwa`, never from here;
    //            named here so the four-way vocabulary has one home.
    //
    // The three-way refused/flow/fault split is the requirement (e440676 §1);
    // `client` exists because a consumer reading a transcript sees all four.
    public static class ErrClass
    {
        public const string Refused = "refused";
        public const string Flow = "flow";
        public const string Fault = "fault";
        public const string Client = "client";
    }

    // A protocol error code AND its class, in ONE declaration.
    //
    // THIS IS NOT A TABLE BESIDE THE CODES, deliberately, and that is the
    // whole design (e440676 §3). A `switch (code)` at the serializer — or a
    // `Dictionary<string,string>` next to `Err` — drifts the moment a verb
    // adds a code, and it drifts SILENTLY, falling through to a default that
    // reads as authoritative. This project shipped exactly that failure this
    // round (git-bug 927be4f: two age surfaces, two vocabularies, one idea).
    // Making the code a VALUE THAT CARRIES ITS CLASS means a new code cannot
    // be declared without naming its kind, and `Result.Fail` takes an ErrCode
    // with no string overload, so the compiler asks the question at every
    // site that could invent one.
    //
    // Codes stay plain strings on the wire; this type never leaves the mod.
    public readonly struct ErrCode
    {
        public readonly string Code;
        public readonly string Class;

        private ErrCode(string code, string cls) { Code = code; Class = cls; }

        public static ErrCode Refused(string code) => new ErrCode(code, ErrClass.Refused);
        public static ErrCode Flow(string code) => new ErrCode(code, ErrClass.Flow);
        public static ErrCode Fault(string code) => new ErrCode(code, ErrClass.Fault);

        public override string ToString() => Code;
    }

    // Error taxonomy per DESIGN.md §Protocol. Codes are protocol surface —
    // per-verb codes join this set in later specs, and each states its class
    // where it is declared.
    public static class Err
    {
        // A malformed envelope, an op that does not exist, an argument the
        // verb cannot use: the caller made a mistake and the detail says which.
        public static readonly ErrCode BadJson = ErrCode.Refused("bad-json");
        public static readonly ErrCode UnknownOp = ErrCode.Refused("unknown-op");
        public static readonly ErrCode BadArgs = ErrCode.Refused("bad-args");

        // `flow`, all three, because the identical call is correct later:
        // no-active-game once a save is loaded, busy once the advance lands,
        // stale-on-restart on a resend — that one is a command file predating
        // this session, answered rather than replayed (Poller.Init), and the
        // caller resends it unchanged if it still wants it.
        public static readonly ErrCode NoActiveGame = ErrCode.Flow("no-active-game");
        public static readonly ErrCode Busy = ErrCode.Flow("busy");
        public static readonly ErrCode StaleOnRestart = ErrCode.Flow("stale-on-restart");

        // The only code in this file that means the mod is broken.
        public static readonly ErrCode Exception = ErrCode.Fault("exception");
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
        public ErrCode ErrorCode;
        public string ErrorDetail;
        public object Data; // MiniJson.Write-able tree; serialized off-thread

        // git-bug 7382bdd: keys the caller supplied that the verb never read,
        // with the keys it did read and a sentence saying so. Null on the
        // overwhelmingly common clean call, so the envelope is unchanged for
        // every correct command. Set by VerbRegistry.Execute from VerbArgs'
        // read log; a top-level envelope field rather than part of `data`,
        // because it is a statement about the CALL and not about the verb's
        // answer — a failed call can carry one too.
        public object IgnoredArgs;

        public static Result Success(string id, string op, object data)
            => new Result { Id = id, Op = op, Ok = true, Data = data };

        // Takes an ErrCode and NOT a string, so a failure cannot be built
        // without saying what kind of failure it is (see ErrCode's header).
        public static Result Fail(string id, string op, ErrCode code, string detail = null)
            => new Result { Id = id, Op = op, Ok = false, ErrorCode = code, ErrorDetail = detail };
    }
}
