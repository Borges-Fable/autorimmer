using System;
using System.Collections.Generic;
using Verse;

namespace AutoRimmer
{
    public static class CoreVerbs
    {
        // Main-thread deliberately: ping is the canary for the full loop every
        // later verb rides — inbox -> queue -> GameComponentUpdate safe point ->
        // outgoing -> result file. At the main menu it answers no-active-game,
        // which is itself the correct signal; menu-time liveness is status's job.
        [Verb("ping")]
        public static object Ping(VerbContext ctx)
        {
            var data = new Dictionary<string, object> { ["pong"] = true };
            if (ctx.Args.Has("echo")) data["echo"] = ctx.Args.Str("echo");
            return data;
        }

        // Off-thread so the bench is observable before any game is loaded; reads
        // only the published snapshot, never Verse.
        [Verb("status", MainThread = false)]
        public static object Status(VerbContext ctx)
        {
            var snap = Runtime.GameState;
            var verbs = new List<object>();
            foreach (var op in VerbRegistry.Ops) verbs.Add(op);
            return new Dictionary<string, object>
            {
                ["gameLoaded"] = snap.gameLoaded,
                ["paused"] = snap.paused,
                ["speed"] = snap.speed,
                ["tick"] = snap.tick,
                ["fps"] = snap.fps,
                ["activeOp"] = snap.activeOp,
                ["verbs"] = verbs,
                ["root"] = Poller.Root,
            };
        }

        [Verb("version", MainThread = false)]
        public static object Version(VerbContext ctx)
        {
            string game = "unknown";
            try { game = RimWorld.VersionControl.CurrentVersionStringWithRev; } catch { }
            return new Dictionary<string, object>
            {
                ["game"] = game,
                ["mod"] = Runtime.ModVersion,
                ["bench"] = Environment.MachineName,
                ["sid"] = Runtime.SessionId,
            };
        }
    }
}
