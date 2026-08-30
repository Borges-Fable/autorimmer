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
                DrainCommands();
            }
            catch (Exception e)
            {
                // Per-command failures are already caught inside Execute; this
                // guards the plumbing itself.
                Log.Warning("[AutoRimmer] update error: " + e);
            }
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
                activeOp = activeOp,
            };
        }

        private void DrainCommands()
        {
            while (Runtime.Pending.TryDequeue(out var cmd))
            {
                activeOp = cmd.Op;
                Runtime.Outgoing.Enqueue(VerbRegistry.Execute(cmd));
                activeOp = null;
            }
        }
    }
}
