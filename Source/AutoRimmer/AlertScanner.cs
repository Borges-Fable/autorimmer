using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // Alerts have no notify event — the readout recomputes them round-robin
    // from UIRootUpdate — so the journal diffs the readout's own activeAlerts
    // list on a frame cadence (spec 1.2; cadence via config.json). Read-only:
    // the alerts were already computed by the game, we never call Recalculate
    // or GetReport. An alert_on's tick is "when the scan noticed", which trails
    // the causing tick by design (0.1 amendment; documented in JOURNAL.md).
    public static class AlertScanner
    {
        private static readonly AccessTools.FieldRef<AlertsReadout, List<Alert>> ActiveAlerts =
            AccessTools.FieldRefAccess<AlertsReadout, List<Alert>>("activeAlerts");

        // Alert instance -> (id, label) as remembered at alert_on, so alert_off
        // never re-reads a dead alert.
        private static readonly Dictionary<Alert, string[]> known = new Dictionary<Alert, string[]>();
        private static readonly HashSet<Alert> current = new HashSet<Alert>();
        private static readonly List<Alert> toForget = new List<Alert>();
        private static int frameCounter;

        // Fresh game = fresh readout instance; stale Alert references must not
        // produce ghost alert_offs across a load boundary.
        public static void Reset()
        {
            known.Clear();
        }

        // Main thread, every GameComponentUpdate (same thread that mutates the
        // list, so no torn reads).
        public static void Tick()
        {
            if (++frameCounter < Config.AlertScanFrames) return;
            frameCounter = 0;
            if (!(Find.UIRoot is UIRoot_Play play)) return;

            var active = ActiveAlerts(play.alerts);
            current.Clear();
            for (int i = 0; i < active.Count; i++)
            {
                var alert = active[i];
                if (alert == null) continue;
                current.Add(alert);
                if (known.ContainsKey(alert)) continue;
                string id = alert.GetType().Name;
                string label;
                try { label = alert.Label; }
                catch { label = id; }
                known[alert] = new[] { id, label };
                Journal.Emit("alert_on", new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["label"] = label,
                    ["priority"] = alert.Priority.ToString(),
                }, Find.TickManager.TicksGame);
            }

            toForget.Clear();
            foreach (var kv in known)
            {
                if (current.Contains(kv.Key)) continue;
                Journal.Emit("alert_off", new Dictionary<string, object>
                {
                    ["id"] = kv.Value[0],
                    ["label"] = kv.Value[1],
                }, Find.TickManager.TicksGame);
                toForget.Add(kv.Key);
            }
            foreach (var alert in toForget) known.Remove(alert);
        }
    }
}
