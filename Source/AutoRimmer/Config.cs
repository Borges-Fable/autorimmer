using System;
using System.IO;

namespace AutoRimmer
{
    // Optional config.json under the protocol root, read once at init. Every
    // knob has a shipped default; the file exists for bench tuning, not play.
    //
    //   {"alertScanFrames":30, "conditionScanFrames":30, "maxTpsCap":1000,
    //    "stallFrames":300, "autoAnswerNameDialogs":true,
    //    "thermalHalveC":93, "thermalResumeC":90, "thermalSustainS":30,
    //    "thermalPath":"/sys/class/hwmon/hwmon3/temp1_input"}
    //
    // `advanceBudgetMs` was 1.3's per-frame DoSingleTick budget and is GONE:
    // 1.8 sets the game's own time speed and lets RimWorld tick, so there is no
    // budget to spend. Unknown keys are ignored, so an old config.json still
    // loads — it just no longer says anything.
    public static class Config
    {
        public static int AlertScanFrames = 30;

        // How often `advance {until:{condition|layout}}` evaluates its
        // predicate (spec 1.6). FRAMES, counted the way AlertScanner counts
        // them, because 1.8 deleted the tick budget and TimeDriver.Step is a
        // per-frame poll site.
        //
        // 15 frames is ~0.5s at the bench's unwatched 30 fps cap. The number is
        // a HALT-PRECISION choice, not a cost one: the window is how late a
        // halt can be, and at Ultrafast a frame is up to 30 ticks, so 15 frames
        // bounds the overshoot at ~450 ticks — the same order as the 204-tick
        // quantum `ResourceCounter.ResourceCounterTick` already imposes on
        // every `resources.*` reading, and far under the 3,180 ticks the M2
        // bedroom took to build. Halving it halves the lateness and doubles the
        // evaluations; the advance result publishes `until.eval_ms_per_frame`
        // so the trade is measured rather than argued. The one predicate worth
        // RAISING it for is `colonists.list[*]`, which costs a `Room.Role` — a
        // full room analysis — per colonist per evaluation.
        //
        // Per-call override: `until.every_frames`.
        public static int ConditionScanFrames = 15;

        // The bench's own throughput ceiling, enforced regardless of what a
        // caller asks for (DESIGN: "max_tps is a hard thermal cap"). Since 1.8
        // it selects a vanilla SPEED rather than sizing a tick budget: the
        // fastest rung of the ladder whose nominal tps does not exceed this.
        // At 1000 that is Ultrafast (900 tps nominal), the fastest speed the
        // game offers without the UltraSpeedBoost TweakValue — which AutoRimmer
        // deliberately does not set.
        //
        // The ladder is coarse (60 / 180 / 360 / 900), so a cap between rungs
        // rounds DOWN and the advance result says so in `max_tps_clamped`. A
        // cap below 60 cannot be honoured at all — nothing in vanilla runs
        // slower than Normal — and that is reported too, as by:"floor", rather
        // than being silently rounded up.
        public static int MaxTpsCap = 1000;

        // The stall watchdog (1.8). An advance no longer drives the clock, so
        // "the clock stopped" is a state it can be in, and `timeout_ticks` is
        // counted in GAME ticks — a stalled game never reaches it. After this
        // many consecutive frames with zero tick progress the advance halts
        // with reason "stalled" and reports what it observed.
        //
        // Counted in FRAMES, not seconds, deliberately: `Root_Play.Update`
        // returns early while `LongEventHandler.ShouldWaitForEvent`, so a long
        // event (autosave, map generation) stops calling GameComponentUpdate
        // altogether and costs no frames. Only a game that is rendering and not
        // ticking accumulates here. 300 frames is ~10s at the bench's unwatched
        // 30 fps cap, ~5s watched.
        public static int StallFrames = 300;

        // git-bug 5cb1f9f. The advance loop answers a `Dialog_GiveName` with
        // the window's own generated name rather than halting on it, because
        // the protocol has no other route: nothing writes a text field, and
        // dismissing gets the window back 1,000 ticks later from
        // Faction.FactionTick. Off, and every naming dialog halts the advance
        // and waits for a human — which is what run m1-20260901 did.
        public static bool AutoAnswerNameDialogs = true;

        public static double ThermalHalveC = 93;
        public static double ThermalResumeC = 90;
        public static double ThermalSustainS = 30;
        public static string ThermalPath; // explicit sensor file override

        public static void Load(string root)
        {
            try
            {
                var path = Path.Combine(root, "config.json");
                if (!File.Exists(path)) return;
                var cfg = MiniJson.Parse(File.ReadAllText(path));
                if (cfg == null) return;
                if (cfg.TryGetValue("alertScanFrames", out var a) && a is double af)
                    AlertScanFrames = Clamp((int)af, 1, 600);
                if (cfg.TryGetValue("conditionScanFrames", out var cs) && cs is double cf)
                    ConditionScanFrames = Clamp((int)cf, 1, 600);
                if (cfg.TryGetValue("maxTpsCap", out var m) && m is double mc)
                    MaxTpsCap = Clamp((int)mc, 1, 10000);
                if (cfg.TryGetValue("stallFrames", out var s2) && s2 is double sf)
                    StallFrames = Clamp((int)sf, 30, 100000);
                if (cfg.TryGetValue("thermalHalveC", out var h) && h is double hc) ThermalHalveC = hc;
                if (cfg.TryGetValue("thermalResumeC", out var r) && r is double rc) ThermalResumeC = rc;
                if (cfg.TryGetValue("thermalSustainS", out var s) && s is double sc) ThermalSustainS = Math.Max(1, sc);
                if (cfg.TryGetValue("autoAnswerNameDialogs", out var an) && an is bool ab)
                    AutoAnswerNameDialogs = ab;
                ThermalPath = MiniJson.GetString(cfg, "thermalPath");
            }
            catch { }
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
