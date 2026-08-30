using System;
using System.IO;

namespace AutoRimmer
{
    // Optional config.json under the protocol root, read once at init. Every
    // knob has a shipped default; the file exists for bench tuning, not play.
    //
    //   {"alertScanFrames":30, "maxTpsCap":1000, "advanceBudgetMs":25,
    //    "thermalHalveC":93, "thermalResumeC":90, "thermalSustainS":30,
    //    "thermalPath":"/sys/class/hwmon/hwmon3/temp1_input"}
    public static class Config
    {
        public static int AlertScanFrames = 30;

        // The 0.1 spike's hard recommendation: max_tps 1000 as a <=25ms
        // per-frame DoSingleTick budget. The cap is enforced regardless of what
        // a caller asks for; the budget clamps at 25ms because beyond it the
        // chassis gains tps but only by pinning the thermal plateau harder.
        public static int MaxTpsCap = 1000;
        public static double AdvanceBudgetMs = 25;

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
                if (cfg.TryGetValue("maxTpsCap", out var m) && m is double mc)
                    MaxTpsCap = Clamp((int)mc, 30, 5000);
                if (cfg.TryGetValue("advanceBudgetMs", out var b) && b is double bm)
                    AdvanceBudgetMs = Math.Max(1, Math.Min(25, bm));
                if (cfg.TryGetValue("thermalHalveC", out var h) && h is double hc) ThermalHalveC = hc;
                if (cfg.TryGetValue("thermalResumeC", out var r) && r is double rc) ThermalResumeC = rc;
                if (cfg.TryGetValue("thermalSustainS", out var s) && s is double sc) ThermalSustainS = Math.Max(1, sc);
                ThermalPath = MiniJson.GetString(cfg, "thermalPath");
            }
            catch { }
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
