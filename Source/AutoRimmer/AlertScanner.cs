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

        // One live alert, as the readout holds it. `Order` is the readout's own
        // discovery order (its activeAlerts index) — carried out because
        // AlertsReadout NEVER sorts that list (decompiled AlertsReadout.cs:262
        // appends; priority grouping happens only in AlertsReadoutOnGUI at draw
        // time), so a consumer that truncates must sort for itself and wants a
        // stable tie-break when it does. `Priority` is carried NUMERICALLY:
        // AlertPriority is Medium=0, High=1, Critical=2, and comparing the
        // ToString() would sort "Critical" first only by alphabetical luck.
        public struct AlertLine
        {
            public string Id;
            public string Label;
            public AlertPriority Priority;
            public int Order;
        }

        // Live verbatim read of the readout's active list (main thread only) —
        // the digest's alert section (spec 2.1: the readout IS the attention
        // model). Same read-only discipline as the scan: labels via the same
        // Label the readout itself draws, never Recalculate/GetReport.
        public static List<AlertLine> Snapshot()
        {
            var result = new List<AlertLine>();
            if (!(Find.UIRoot is UIRoot_Play play)) return result;
            var active = ActiveAlerts(play.alerts);
            for (int i = 0; i < active.Count; i++)
            {
                var alert = active[i];
                if (alert == null) continue;
                string id = alert.GetType().Name;
                // Priority is VIRTUAL and belongs inside the same guard as
                // Label: a modded alert that throws from it would otherwise
                // take the whole call down (1.5 nit).
                string label;
                AlertPriority priority;
                try { label = alert.Label; priority = alert.Priority; }
                catch { label = id; priority = AlertPriority.Medium; }
                result.Add(new AlertLine
                {
                    Id = id,
                    Label = label,
                    Priority = priority,
                    Order = i,
                });
            }
            return result;
        }

        // Fixture hook (spec 2.6 acceptance): inject/clear alert instances in
        // the readout's own activeAlerts list. Dev-gated and journaled by its
        // ONE caller, journal-selftest — see JournalVerbs.Selftest. It lives
        // here because the private-field ref does, and nowhere else may use it.
        //
        // Safe by construction: an instance we create is not in the readout's
        // AllAlerts list, so the round-robin CheckAddOrRemoveAlert never touches
        // it, and Alert_Custom/Alert_CustomCritical (and their subclasses) are
        // explicitly EXCLUDED from allAlertTypesCached (decompiled
        // AlertsReadout.cs:64), so the game never instantiates ours either.
        public static bool FixtureInject(Alert alert)
        {
            if (!(Find.UIRoot is UIRoot_Play play)) return false;
            var active = ActiveAlerts(play.alerts);
            if (!active.Contains(alert)) active.Add(alert);
            return true;
        }

        public static int FixtureClear(System.Func<Alert, bool> pred)
        {
            if (!(Find.UIRoot is UIRoot_Play play)) return 0;
            var active = ActiveAlerts(play.alerts);
            int removed = 0;
            for (int i = active.Count - 1; i >= 0; i--)
                if (pred(active[i])) { active.RemoveAt(i); removed++; }
            return removed;
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
                // alert.Priority is VIRTUAL, and it used to sit OUTSIDE this
                // try — in the Emit call below. One modded alert throwing from
                // it aborted the whole GameComponentUpdate body for that frame:
                // the command drain and the advance loop with it (1.5 nit).
                string label, priority;
                try { label = alert.Label; priority = alert.Priority.ToString(); }
                catch { label = id; priority = "unknown"; }
                known[alert] = new[] { id, label };
                Journal.Emit("alert_on", new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["label"] = label,
                    ["priority"] = priority,
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
