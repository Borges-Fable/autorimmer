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

        // ============================ spec 975973e (absorbing 91bc250) ======
        // AN ALERT'S AGE, AND WHETHER IT HAS BEEN HERE BEFORE.
        //
        // "This has been live for 13 days" was not a fact any reader could
        // state: the scanner noticed the transition and kept nothing but the
        // id and the label. This is that memory, and it is deliberately keyed
        // on the ID rather than on the Alert INSTANCE the dictionary above
        // uses. The instance dictionary is right for its job (it must never
        // re-read a dead alert), and wrong for this one: the readout throws
        // its instances away and an alert that cleared and returned would
        // present as brand new, which is precisely the fact 91bc250 asks to
        // see.
        //
        // IN MEMORY, CLEARED AT A GAME BOUNDARY, exactly like the sampler ring
        // and the screen mark — `Reset()` below is called from
        // `AgentGameComponent.GameBoundary`. Nothing here is scribed: the
        // hazard ruling on observers owning scribed state (d16a463, and
        // f1a1700 comment #2) applies, and the honest consequence is stated in
        // the data — `age_basis` says the age is measured from the first scan
        // THIS SESSION, so an alert that was already live when the save loaded
        // reads its age from the load and not from when it really began.
        //
        // `LiveCount` and not a bool, because the fixture route
        // (`FixtureInject`) can put two instances with the same id in the
        // readout at once, and a bool would make the first `alert_off` of the
        // pair look like the alert clearing.
        private sealed class AlertHistory
        {
            public int FirstSeenTick;   // the scan that noticed the CURRENT run
            public int Returns;         // times it cleared and came back, this session
            public int LiveCount;       // instances currently in the readout
            public int LastOffTick;     // when the last run ended (-1 = never)
        }

        private static readonly Dictionary<string, AlertHistory> history =
            new Dictionary<string, AlertHistory>(System.StringComparer.Ordinal);

        // Fresh game = fresh readout instance; stale Alert references must not
        // produce ghost alert_offs across a load boundary.
        public static void Reset()
        {
            known.Clear();
            // Same boundary, same argument: an age measured in `TicksGame` on a
            // colony that no longer exists is worse than no age, because a load
            // can move `TicksGame` BACKWARD and the subtraction would publish a
            // negative or a wildly inflated one. (The sampler ring is cleared
            // here for exactly this reason — see ColonySampler.Clear.)
            history.Clear();
        }

        // The screen's LIGHTS gauge. False when this session has never seen the
        // id go active, which is a real state and not an error: the readout can
        // hold an alert that was already live when the game loaded and whose
        // first `alert_on` therefore predates our memory of it.
        //
        // `returns > 0` is 91bc250's "cleared and came back" flag.
        public static bool AgeOf(string id, out int firstSeenTick, out int returns, out int lastOffTick)
        {
            firstSeenTick = 0;
            returns = 0;
            lastOffTick = -1;
            if (id == null) return false;
            if (!history.TryGetValue(id, out var h) || h == null) return false;
            firstSeenTick = h.FirstSeenTick;
            returns = h.Returns;
            lastOffTick = h.LastOffTick;
            return true;
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
                int nowOn = Find.TickManager.TicksGame;
                // 91bc250 item 1, on the ON edge. A RETURN is `LiveCount == 0`
                // with a record already present: the id has been here before
                // and went away. The age restarts — the issue asks for exactly
                // that ("an alert that clears and returns starts a new age") —
                // and `Returns` is what keeps the return itself visible instead
                // of presenting it as a fresh alert.
                if (!history.TryGetValue(id, out var hOn) || hOn == null)
                {
                    hOn = new AlertHistory { FirstSeenTick = nowOn, LastOffTick = -1 };
                    history[id] = hOn;
                }
                else if (hOn.LiveCount == 0)
                {
                    hOn.Returns++;
                    hOn.FirstSeenTick = nowOn;
                }
                hOn.LiveCount++;
                Journal.Emit("alert_on", new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["label"] = label,
                    ["priority"] = priority,
                    // 91bc250 item 1: the return is visible on the ROW as well
                    // as on the gauge, so a post-mortem reading the journal
                    // alone can count flapping. 0 on a first appearance.
                    ["returns"] = hOn.Returns,
                }, nowOn);
            }

            toForget.Clear();
            foreach (var kv in known)
            {
                if (current.Contains(kv.Key)) continue;
                int nowOff = Find.TickManager.TicksGame;
                string offId = kv.Value[0];
                if (history.TryGetValue(offId, out var hOff) && hOff != null)
                {
                    if (hOff.LiveCount > 0) hOff.LiveCount--;
                    // Only the LAST instance of an id going away ends the run;
                    // see AlertHistory.LiveCount for why that is not the same
                    // as "an alert_off was emitted".
                    if (hOff.LiveCount == 0) hOff.LastOffTick = nowOff;
                }
                Journal.Emit("alert_off", new Dictionary<string, object>
                {
                    ["id"] = offId,
                    ["label"] = kv.Value[1],
                }, nowOff);
                toForget.Add(kv.Key);
            }
            foreach (var alert in toForget) known.Remove(alert);
        }
    }
}
