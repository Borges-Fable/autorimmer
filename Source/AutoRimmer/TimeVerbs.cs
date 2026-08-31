using System;
using System.Collections.Generic;
using Verse;

namespace AutoRimmer
{
    public static class TimeVerbs
    {
        // Immediate pause; also the brake pedal — an in-flight advance is
        // interrupted (its own result says reason=interrupted).
        //
        // The pause is issued HERE as well as by the interrupted advance's
        // teardown, and that is not belt-and-braces. This verb runs inside
        // DrainCommands, which is above TimeDriver.FrameStep in the same
        // GameComponentUpdate; since 1.8 the game is genuinely running while an
        // advance is in flight, so returning `paused` read off a TickManager
        // nobody had paused yet would report false for a pause that is about to
        // succeed. Paying it here makes the answer true when it is written.
        //
        // `paused` is `CurTimeSpeed == Paused`, NOT `TickManager.Paused`:
        // `Paused` is also true from a force-pausing window alone, which would
        // claim success while the game is still set to Ultrafast and would
        // resume the moment that window closed.
        [Verb("pause")]
        public static object Pause(VerbContext ctx)
        {
            bool wasAdvancing = TimeDriver.Interrupt();
            var tm = Find.TickManager;
            var before = tm.CurTimeSpeed;
            tm.Pause();
            bool paused = tm.CurTimeSpeed == TimeSpeed.Paused;
            var data = new Dictionary<string, object>
            {
                ["was_advancing"] = wasAdvancing,
                ["paused"] = paused,
                ["speed"] = tm.CurTimeSpeed.ToString(),
                ["speed_before"] = before.ToString(),
            };
            // Pause() routes through TogglePaused -> PlayerCanControl and
            // silently no-ops during a screen fade, a gravship cutscene or a
            // landing-area confirmation (decompiled Verse/TickManager.cs). Say
            // so rather than returning paused:false with no explanation.
            if (!paused)
                data["refused"] = "the game refused Paused: PlayerCanControl is false "
                                + "(screen fade, gravship cutscene, or landing-area confirmation)";
            return data;
        }

        // Vanilla speeds, for live human watching and for anything that wants
        // the clock running outside an advance. Since 1.8 `advance` uses the
        // same ladder — see TimeDriver — so this verb and an advance's `speed:`
        // argument mean exactly the same thing.
        //
        // The CurTimeSpeed setter can silently no-op (PlayerCanControl), so the
        // result reports what actually took.
        [Verb("unpause")]
        public static object Unpause(VerbContext ctx)
        {
            string speed = ctx.Args.Str("speed", "Normal");
            // Names only, shared with advance: Enum.TryParse would accept
            // "Paused" and the bare ordinals ("3" => Superfast).
            if (!TimeDriver.TryParseSpeed(speed, out var ts))
                throw new VerbArgsException("speed must be normal|fast|superfast|ultrafast");
            var tm = Find.TickManager;
            tm.CurTimeSpeed = ts;
            bool took = tm.CurTimeSpeed == ts;
            var data = new Dictionary<string, object>
            {
                ["speed"] = tm.CurTimeSpeed.ToString(),
                ["took"] = took,
                ["paused"] = tm.Paused,
                ["nominal_tps"] = TimeDriver.NominalTps(tm.CurTimeSpeed),
            };
            if (!took)
                data["refused"] = "the CurTimeSpeed setter no-opped: PlayerCanControl is false "
                                + "(screen fade, gravship cutscene, or landing-area confirmation)";
            // `paused` above is TickManager.Paused, which is ALSO true from a
            // force-pausing window: the speed took, and the game still will not
            // tick until that window closes. Name it rather than let the pair
            // read as a contradiction.
            try
            {
                var stack = Find.WindowStack;
                if (stack != null && stack.WindowsForcePause)
                    data["force_pause_windows"] = TimeDriver.ForcePausePayload(stack);
            }
            catch { }
            return data;
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
