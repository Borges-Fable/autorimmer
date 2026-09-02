using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AutoRimmer
{
    // Main-thread half of the bridge. RimWorld auto-instantiates every
    // GameComponent subclass with a (Game) ctor; GameComponentUpdate runs every
    // frame while a game is loaded, INCLUDING paused — the safe point where all
    // main-thread verbs execute. Verbs run to completion inside the drain, so a
    // command never observes or mutates mid-tick state.
    public class AgentGameComponent : GameComponent
    {
        private static double fpsEma = 60;
        private static string activeOp;

        public AgentGameComponent(Game game)
        {
        }

        public override void GameComponentUpdate()
        {
            System.Threading.Interlocked.Increment(ref Runtime.Heartbeat);
            try
            {
                float dt = Time.unscaledDeltaTime;
                if (dt > 0) fpsEma = fpsEma * 0.95 + (1.0 / dt) * 0.05;
                PublishSnapshot();
                // Isolated, because it reads MODDED code (Alert.Label and
                // Alert.Priority are both virtual). A throw here used to skip
                // the rest of this body for the frame — the command drain and
                // the advance loop included — which is how a third-party alert
                // could silently stall the bridge (1.5 nit).
                try { AlertScanner.Tick(); }
                catch (Exception e) { Log.Warning("[AutoRimmer] alert scan error: " + e); }
                DrainCommands();
                TimeDriver.FrameStep();
                // After FrameStep, deliberately: the 1.5 lifecycle acceptance
                // needs the game to unload with an advance genuinely in
                // flight, so the advance must have ticked this frame first.
                try { JournalVerbs.TickMainMenuFixture(); }
                catch (Exception e) { Log.Warning("[AutoRimmer] main-menu fixture error: " + e); }
            }
            catch (Exception e)
            {
                // Per-command failures are already caught inside Execute; this
                // guards the plumbing itself.
                Log.Warning("[AutoRimmer] update error: " + e);
            }
        }

        // Runs INSIDE DoSingleTick (GameComponentUtility.GameComponentTick),
        // i.e. inside the advance loop. Only fixtures live here, and both are
        // here for the same reason: they have to fire from inside a TICK to
        // reproduce their failure honestly — 1.5 blocker 3's red error, and
        // 722c951's own-faction downing, which halts an advance and so must
        // happen while one is running.
        public override void GameComponentTick()
        {
            // git-bug 2d9a1da. HERE AND NOT IN GameComponentUpdate, and that is
            // the whole correctness argument for the sampler: this virtual runs
            // inside DoSingleTick, so it fires once per GAME TICK, whereas
            // GameComponentUpdate fires once per FRAME including while the game
            // is paused. Sampling per frame would append identical rows at a
            // wall-clock rate while the agent sat thinking and flatten every
            // slope toward zero with data containing no game time. (Verse/
            // TickManager.DoSingleTick calls Find.History.HistoryTick()
            // immediately before GameComponentUtility.GameComponentTick(), so a
            // sample taken here sees a history the game has already updated
            // this tick.) The method returns on its first line 2,499 ticks out
            // of every 2,500; it catches its own exceptions, because a throw
            // from inside the tick loop would be a red error every cadence for
            // the rest of the run.
            try { ColonySampler.Tick(); }
            catch { }
            // git-bug d9d6c12. Same argument as the sampler's, one cadence
            // faster: `Bill.nextTickToSearchForIngredients` is rearmed every
            // 500-600 ticks while a bill keeps failing its ingredient search,
            // so a per-FRAME observer would count wall-clock while the agent
            // sat thinking and a 2,500-tick one would miss whole streaks.
            // Returns on its first line 249 ticks out of every 250.
            try { BillWatch.Tick(); }
            catch { }
            // git-bug f9dadc7. The third of the same kind, one cadence slower
            // than the bill watch and for the mirror-image reason: an element's
            // stall is measured in DAYS, so a per-FRAME observer would count
            // wall-clock while the agent sat thinking, and — the case this
            // issue is about — an advance that runs twenty in-game days would
            // be sampled by nothing at all if the tracker lived on the read
            // path. Returns on its first line 2,499 ticks out of every 2,500.
            try { ConstructionWatch.Tick(); }
            catch { }
            try { JournalVerbs.TickErrorFixture(); }
            catch { }
            try { JournalVerbs.TickCasualtyFixture(); }
            catch { }
            // 280fb78's `alert-at`. Third of the same kind: the wake halt fires
            // on an `alert_on` that arrives while time runs, and the alert
            // scanner is a per-FRAME diff — so an alert injected from the
            // command drain (game paused) is journaled before the advance
            // starts and halts nothing.
            try { JournalVerbs.TickAlertFixture(); }
            catch { }
        }

        public override void StartedNewGame() => GameBoundary("newgame");

        public override void LoadedGame() => GameBoundary("loaded");

        // Both lifecycle virtuals do the same thing, because both are the same
        // event as far as the bridge is concerned: a Game object that was NOT
        // there is there now, and everything armed against the previous one is
        // void. AutoRimmer's driver state is static, so it outlives the Game —
        // without this reset an advance in flight when the colony unloaded
        // resumes here and ticks the NEW colony to finish the old command,
        // reporting ticks_elapsed and journal_seq spanning two games
        // (1.5 blocker 1).
        //
        // The poller's heartbeat edge does the same reset when NO game comes
        // back, which is the case this hook structurally cannot see.
        private static void GameBoundary(string kind)
        {
            int answered = Runtime.ResetForGameBoundary(Runtime.BoundaryDetail);
            AlertScanner.Reset();
            // A failed-search count carried across a reload would be
            // attributed to bills that no longer exist (git-bug d9d6c12).
            BillWatch.Reset();
            // An element age carried across a reload would be attributed to
            // blueprints that no longer exist (git-bug f9dadc7). The clock
            // restarting is expected; `tracked_since_tick` and the tri-state
            // `stalled` are what make it honest rather than silent.
            ConstructionWatch.Reset();
            // Fixtures armed against the colony that just went away.
            JournalVerbs.MainMenuAtTick = -1;
            JournalVerbs.ErrorAtTick = -1;
            JournalVerbs.DownAtTick = -1;
            JournalVerbs.AlertAtTick = -1;
            var payload = new System.Collections.Generic.Dictionary<string, object>
            {
                ["kind"] = kind,
            };
            if (answered > 0) payload["aborted"] = answered;
            Journal.Emit("session", payload, Find.TickManager.TicksGame);
        }

        private void PublishSnapshot()
        {
            var tm = Find.TickManager;
            // Only built when a force-pausing window is actually up — the
            // bool property allocates nothing, the payload does. This is how
            // status.json says "an advance cannot run right now, and here is
            // what is in the way" (spec 1.7).
            Dictionary<string, object> forcePause = null;
            try
            {
                var stack = Find.WindowStack;
                if (stack != null && stack.WindowsForcePause)
                    forcePause = TimeDriver.ForcePausePayload(stack);
            }
            catch { }
            Runtime.GameState = new GameSnapshot
            {
                gameLoaded = true,
                paused = tm.Paused,
                speed = tm.CurTimeSpeed.ToString(),
                tick = tm.TicksGame,
                fps = fpsEma,
                activeOp = TimeDriver.Active ? "advance:" + TimeDriver.ActiveId : activeOp,
                forcePause = forcePause,
            };
        }

        private void DrainCommands()
        {
            while (Runtime.Pending.TryDequeue(out var cmd))
            {
                // One long-running op at a time: while an advance is in flight,
                // main-thread verbs answer busy — except pause, the brake pedal.
                if (TimeDriver.Active && cmd.Op != "pause")
                {
                    Runtime.Outgoing.Enqueue(Result.Fail(cmd.Id, cmd.Op, Err.Busy,
                        $"advance '{TimeDriver.ActiveId}' in flight ({TimeDriver.TicksDone} ticks done)",
                        cmd.Args));
                    continue;
                }
                activeOp = cmd.Op;
                var result = VerbRegistry.Execute(cmd);
                if (!(result.Ok && result.Data is DeferredResult))
                    Runtime.Outgoing.Enqueue(result);
                activeOp = null;
            }
        }
    }
}
