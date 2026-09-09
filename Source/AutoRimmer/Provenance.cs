using System;
using HarmonyLib;
using Verse;

namespace AutoRimmer
{
    // WHO DID IT — git-bug 827c1bf, COCKPIT.md §"Where it lives".
    //
    // Every journal row carries a `by`. Four values and no others:
    //
    //   agent   a command the agent sent, executing inside the drain
    //           (VerbRegistry.Execute). The command id rides beside it as `cmd`.
    //   mod     the mod acting on its own initiative — a chore. Nobody asked
    //           for it and no envelope reports it.
    //   game    inside a game tick (Verse/TickManager.DoSingleTick), whoever
    //           set the clock running. A colonist who starves at tick 400,112
    //           was killed by the simulation, not by whoever pressed play.
    //   human   the default, and it means exactly "none of the above was
    //           running when this row was written". In practice that is a hand
    //           at the keyboard reaching a mutation through the game's own
    //           input handlers; HumanActions.cs turns the five such handlers
    //           into `action` rows of their own.
    //
    // WHY IT EXISTS. The audit of run openrun-20260902 counted at least 22
    // human interventions in a run whose journal marks none of them: a revival
    // through the debug menu, a deleted weather event, 411 meals, nine in-game
    // days played by hand (themes.md T13, T14). Three of that run's 23 `death`
    // rows are debug-menu residue and one death was reversed with no row for
    // the reversal — so no count taken from that journal is a measurement. A
    // count you can filter by provenance is.
    //
    // ---- the mechanism -------------------------------------------------
    //
    // One [ThreadStatic] string, set at three sites, read once per emit in
    // Journal.Emit. Every existing hook therefore gains the field without
    // being touched.
    //
    // [ThreadStatic] AND NOT A PLAIN STATIC, and that is the difference
    // between a provenance and a guess. The bridge is two threads: main-thread
    // verbs run in AgentGameComponent.DrainCommands, off-main verbs
    // (MainThread=false) run inline on the poller thread in Poller.ScanInbox,
    // and Journal.Emit is called from both plus every Harmony log hook, which
    // fires from whatever thread logged. A plain static set by the main thread
    // would stamp `agent` on a warning a background thread happened to log
    // during a drain, and stamp `human` on the drain's own rows the instant a
    // background emit reset it. Thread-local, each thread reports what IT is
    // doing, and a thread nobody instrumented reports the honest default.
    //
    // The scope is a STRUCT with a `using`, so it allocates nothing (C# calls
    // Dispose on a struct through a constrained call, never a box) and it
    // restores the PREVIOUS value rather than clearing to null — so a nested
    // scope, e.g. a tick reached from inside a verb, unwinds correctly.
    //
    // A scope is never entered from an observer. Nothing here reads or writes
    // game state; the tick bracket below is the only Verse-adjacent part and
    // it touches nothing but this class's own fields.
    public static class Provenance
    {
        public const string Agent = "agent";
        public const string Mod = "mod";
        public const string Game = "game";
        public const string Human = "human";

        [ThreadStatic] private static string who;
        [ThreadStatic] private static string cmd;

        // Journal.Emit reads this once per row. Null means no site claimed
        // this thread, which is the definition of `human`.
        public static string Current => who ?? Human;

        // Only meaningful under `agent`: `cmd` is not cleared when a tick
        // bracket nests inside a verb, so the read is gated on `who` rather
        // than on the field being non-null.
        public static string CommandId => who == Agent ? cmd : null;

        // THE GUARD THE HUMAN-ACTION HOOKS USE. `human` is defined as "while
        // nothing of ours is running", so a hook on an engine input handler
        // asks this before it writes a row: if the mod, the agent or a tick
        // reached that handler, the press was not a human's and the row would
        // be a false attribution. Deliberately `who == null` and not
        // `Current == Human` — they are the same today and would stop being so
        // the moment anything set `who = Human` explicitly.
        public static bool NothingOfOursIsRunning => who == null;

        public struct Scope : IDisposable
        {
            private readonly string prevWho;
            private readonly string prevCmd;
            private readonly bool armed;

            internal Scope(string enter, string id)
            {
                prevWho = who;
                prevCmd = cmd;
                who = enter;
                cmd = id;
                armed = true;
            }

            // `default(Scope)` is not armed, so a Dispose on one cannot blank
            // a scope it never entered.
            public void Dispose()
            {
                if (!armed) return;
                who = prevWho;
                cmd = prevCmd;
            }
        }

        // SITE 1 — the drain. VerbRegistry.Execute wraps the handler, which
        // covers both drains at once: main-thread verbs from
        // AgentGameComponent.DrainCommands and MainThread=false verbs run
        // inline on the poller thread by Poller.ScanInbox.
        public static Scope Command(string id) => new Scope(Agent, id);

        // SITE 2 — a chore: work the mod does because it decided to, with no
        // command behind it and no result envelope in front of it. Two exist
        // today (TimeDriver.SweepNameDialogs and TimeDriver.ClockEmit); the
        // chore set COCKPIT.md describes is not built yet and will enter here.
        public static Scope Chore() => new Scope(Mod, null);

        // SITE 3 — the tick. Entered by the bracket below rather than by hand,
        // for the reason its header gives.
        public static Scope Tick() => new Scope(Game, null);

        // THE FRAME RE-ANCHOR. AgentGameComponent.GameComponentUpdate calls
        // this first thing, before the drain and before FrameStep.
        //
        // It is a repair, not a mechanism: every scope above unwinds itself,
        // and the tick bracket unwinds through a Finalizer so it survives a
        // throw out of DoSingleTick. What this covers is the residue neither
        // can — a Harmony patch on the bracket that a third-party mod removes
        // mid-run, an unwind path nobody thought of — and it bounds the damage
        // at one frame instead of the rest of the session. Verse/Game.UpdatePlay
        // calls GameComponentUtility.GameComponentUpdate() OUTSIDE
        // TickManagerUpdate, so the main thread is provably at no site here.
        // One field write per frame.
        public static void ReanchorFrame()
        {
            who = null;
            cmd = null;
        }

        // ---- SITE 3: the tick bracket ---------------------------------------
        //
        // THE ONE HARMONY PREFIX IN THE TREE, and it is here because there is
        // no other way to be right.
        //
        // `game` has to mean "inside any tick", not "inside a tick this mod
        // drove". Since 1.8 the mod does not drive ticks at all — `advance`
        // sets a speed and the game's own `TickManagerUpdate` calls
        // `DoSingleTick` (TimeDriver's header; grep the tree for DoSingleTick
        // and there is no call site, only comments). And the whole point of
        // the `clock` row is that time also moves with no advance in flight.
        // So a bracket wrapped around the mod's own call sites would label a
        // starvation death during a human's play window `human`, which is
        // false by the definition above and is precisely the wrong attribution
        // this issue exists to eliminate.
        //
        // WHY NOT GameComponentTick, WHICH ALREADY RUNS INSIDE THE TICK. It is
        // too late: Verse/TickManager.DoSingleTick calls MapPreTick, the three
        // tick lists, WorldTick, StorytellerTick and MapPostTick — every path
        // that kills a pawn or destroys a building — and only THEN
        // GameComponentUtility.GameComponentTick(). Verified by member name
        // and by reading the body.
        //
        // WHY A PREFIX IS SAFE HERE while JournalHooks.cs bans them. That
        // file's rule guards journal EVENT hooks: no prefix, no `__result`
        // write, no Rand, no lazy-init getter, because such a hook must never
        // alter game flow. This prefix returns `void`, so Harmony cannot skip
        // the original with it (only a `bool`-returning prefix can), and its
        // body is two writes to this class's own [ThreadStatic] fields. It
        // reads no game state and cannot throw.
        //
        // WHY A FINALIZER AND NOT A POSTFIX. A postfix does not run when the
        // original throws, and `DoSingleTick` leaves `MapPreTick`,
        // `MapPostTick` and `tickListNormal/Rare/Long.Tick()` OUTSIDE its
        // try/catch blocks. One throw from a modded tick would have left the
        // main thread stamped `game` for the rest of the session. A void
        // Finalizer that does not touch `__exception` runs on both paths and
        // rethrows whatever was in flight, unchanged.
        //
        // COST. Two static writes plus Harmony's wrapper per tick. A RimWorld
        // tick on the 38-mod bench is hundreds of microseconds of work; the
        // wrapper is tens of nanoseconds, so at the bench ceiling of ~900 tps
        // this is on the order of 0.01% of one core, and it allocates nothing.
        // It is also the cheapest of the four sites by construction: it does
        // per TICK what the alternative (a hook on every mutation) would do
        // per mutation.
        [HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
        public static class Patch_DoSingleTick
        {
            public static void Prefix(out string __state)
            {
                __state = who;
                who = Game;
            }

            public static void Finalizer(string __state)
            {
                who = __state;
            }
        }
    }
}
