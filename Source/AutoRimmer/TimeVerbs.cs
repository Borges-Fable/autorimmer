using System;
using System.Collections.Generic;
using Verse;

namespace AutoRimmer
{
    public static class TimeVerbs
    {
        // Immediate pause; also the brake pedal — an in-flight advance is
        // interrupted (its own result says reason=interrupted).
        [Verb("pause")]
        public static object Pause(VerbContext ctx)
        {
            bool wasAdvancing = TimeDriver.Interrupt();
            if (!wasAdvancing) Find.TickManager.Pause();
            return new Dictionary<string, object>
            {
                ["was_advancing"] = wasAdvancing,
                ["paused"] = Find.TickManager.Paused,
            };
        }

        // Vanilla speeds, for live human watching only — advance never uses
        // them (the budget loop outruns Ultrafast at a fraction of the frame
        // rate). The CurTimeSpeed setter can silently no-op (PlayerCanControl),
        // so the result reports what actually took.
        [Verb("unpause")]
        public static object Unpause(VerbContext ctx)
        {
            string speed = ctx.Args.Str("speed", "Normal");
            if (!Enum.TryParse<TimeSpeed>(speed, ignoreCase: true, out var ts) || ts == TimeSpeed.Paused)
                throw new VerbArgsException("speed must be Normal|Fast|Superfast|Ultrafast");
            var tm = Find.TickManager;
            tm.CurTimeSpeed = ts;
            return new Dictionary<string, object>
            {
                ["speed"] = tm.CurTimeSpeed.ToString(),
                ["took"] = tm.CurTimeSpeed == ts,
                ["paused"] = tm.Paused,
            };
        }

        // Long-running: the handler only arms the driver; the result is written
        // when the advance halts. See TimeDriver for the loop's contract.
        [Verb("advance")]
        public static object Advance(VerbContext ctx)
        {
            var fail = TimeDriver.Start(ctx.Command, ctx.Args);
            if (fail != null)
            {
                Runtime.Outgoing.Enqueue(fail);
                return DeferredResult.Instance;
            }
            return DeferredResult.Instance;
        }
    }
}
