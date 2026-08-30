using System;
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
                AlertScanner.Tick();
                DrainCommands();
                TimeDriver.FrameStep();
            }
            catch (Exception e)
            {
                // Per-command failures are already caught inside Execute; this
                // guards the plumbing itself.
                Log.Warning("[AutoRimmer] update error: " + e);
            }
        }

        public override void StartedNewGame()
        {
            AlertScanner.Reset();
            Journal.Emit("session", new System.Collections.Generic.Dictionary<string, object>
            {
                ["kind"] = "newgame",
            }, Find.TickManager.TicksGame);
        }

        public override void LoadedGame()
        {
            AlertScanner.Reset();
            Journal.Emit("session", new System.Collections.Generic.Dictionary<string, object>
            {
                ["kind"] = "loaded",
            }, Find.TickManager.TicksGame);
        }

        private void PublishSnapshot()
        {
            var tm = Find.TickManager;
            Runtime.GameState = new GameSnapshot
            {
                gameLoaded = true,
                paused = tm.Paused,
                speed = tm.CurTimeSpeed.ToString(),
                tick = tm.TicksGame,
                fps = fpsEma,
                activeOp = TimeDriver.Active ? "advance:" + TimeDriver.ActiveId : activeOp,
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
                        $"advance '{TimeDriver.ActiveId}' in flight ({TimeDriver.TicksDone} ticks done)"));
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
