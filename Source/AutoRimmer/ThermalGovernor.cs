using System;
using System.IO;

namespace AutoRimmer
{
    // The advance loop's thermal defense (spec 1.3, 0.1 amendment): while the
    // CPU sits at/above ThermalHalveC for ThermalSustainS, the per-frame tick
    // budget is halved; it restores below ThermalResumeC. The spike measured
    // this chassis class plateauing at 93.4-94.1C under ANY sustained full-duty
    // load with ~1C of headroom — there is no thermally free tps number, only
    // duty cycle, so the governor acts on the budget, not the tps target.
    //
    // Temperature comes from a FILE, polled off-thread by the poller at ~1Hz:
    //   1. config.json thermalPath (explicit override),
    //   2. Linux hwmon (prefer the k10temp/coretemp package sensor),
    //   3. <root>/thermal.txt — plain degrees C, for platforms with no
    //      in-process sensor (Windows: Mono has no usable WMI; a sidecar can
    //      write this file, and without one the governor is simply inert).
    // The quota cap (Config.MaxTpsCap) is the primary, machine-independent
    // control; this is defense-in-depth for the bench that has sensors.
    public static class ThermalGovernor
    {
        private static string sensorPath;
        private static bool probed;
        private static DateTime hotSince = DateTime.MinValue;

        // Read by the advance loop every frame and by status.json.
        public static volatile float Scale = 1f;
        public static double TempC = -1; // torn reads harmless: diagnostics only

        public static bool Available => sensorPath != null;

        // Poller thread, ~1Hz.
        public static void Poll(string root)
        {
            try
            {
                if (!probed) Probe(root);
                if (sensorPath == null) return;
                double c = ReadC(sensorPath);
                if (c <= 0) return;
                TempC = c;
                if (c >= Config.ThermalHalveC)
                {
                    if (hotSince == DateTime.MinValue) hotSince = DateTime.UtcNow;
                    else if ((DateTime.UtcNow - hotSince).TotalSeconds >= Config.ThermalSustainS)
                        Scale = 0.5f;
                }
                else if (c < Config.ThermalResumeC)
                {
                    hotSince = DateTime.MinValue;
                    Scale = 1f;
                }
            }
            catch { }
        }

        private static void Probe(string root)
        {
            probed = true;
            if (Config.ThermalPath != null && File.Exists(Config.ThermalPath))
            {
                sensorPath = Config.ThermalPath;
                return;
            }
            try
            {
                const string hwmon = "/sys/class/hwmon";
                if (Directory.Exists(hwmon))
                {
                    string fallback = null;
                    foreach (var dir in Directory.GetDirectories(hwmon))
                    {
                        string temp = Path.Combine(dir, "temp1_input");
                        if (!File.Exists(temp)) continue;
                        string name = "";
                        try { name = File.ReadAllText(Path.Combine(dir, "name")).Trim(); }
                        catch { }
                        if (name == "k10temp" || name == "coretemp")
                        {
                            sensorPath = temp;
                            return;
                        }
                        if (fallback == null) fallback = temp;
                    }
                    if (fallback != null)
                    {
                        sensorPath = fallback;
                        return;
                    }
                }
            }
            catch { }
            string txt = Path.Combine(root, "thermal.txt");
            if (File.Exists(txt)) sensorPath = txt;
        }

        private static double ReadC(string path)
        {
            var raw = File.ReadAllText(path).Trim();
            if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v)) return -1;
            return v > 1000 ? v / 1000.0 : v; // hwmon reports millidegrees
        }
    }
}
