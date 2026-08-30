using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace AutoRimmer
{
    // The journal: append-only events.ndjson per process session — "what the
    // player saw while time ran" (spec 1.2). Emitters build a small payload tree
    // and enqueue; the poller thread drains to disk (file I/O never on the main
    // thread). One file per session under journal/, named by the session id so
    // status.json/sid is also the journal key. Schema: JOURNAL.md.
    public static class Journal
    {
        private static string dir;
        private static string path;
        private static StreamWriter writer;
        private static long seq;
        private static readonly ConcurrentQueue<string> pending = new ConcurrentQueue<string>();

        // Warning dedupe: first occurrence per exact text per session, then
        // silent (LogRelay owns repeat counting; the journal is chronology).
        // Red errors are each significant (zero-red-errors invariant) but a
        // storm adds nothing: per-text cap, then one suppression marker.
        private const int RedErrorCapPerText = 3;
        private static readonly ConcurrentDictionary<string, int> errorCounts = new ConcurrentDictionary<string, int>();
        private static readonly ConcurrentDictionary<string, bool> warningsSeen = new ConcurrentDictionary<string, bool>();

        // Alert scan cadence in frames; configurable via config.json
        // {"alertScanFrames": N} under the protocol root (clamped 1..600).
        public static int AlertScanFrames = 30;

        public static string CurrentFile => path;

        // Main thread, from the mod ctor (after the protocol root exists).
        public static void Init(string root)
        {
            dir = Path.Combine(root, "journal");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, Runtime.SessionId + ".ndjson");
            writer = new StreamWriter(
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false)) { AutoFlush = true };

            try
            {
                var cfgPath = Path.Combine(root, "config.json");
                if (File.Exists(cfgPath))
                {
                    var cfg = MiniJson.Parse(File.ReadAllText(cfgPath));
                    if (cfg != null && cfg.TryGetValue("alertScanFrames", out var v) && v is double d)
                        AlertScanFrames = Math.Max(1, Math.Min(600, (int)d));
                }
            }
            catch { }

            string game = "unknown";
            try { game = RimWorld.VersionControl.CurrentVersionStringWithRev; } catch { }
            Emit("session", new Dictionary<string, object>
            {
                ["kind"] = "boot",
                ["mod"] = Runtime.ModVersion,
                ["game"] = game,
                ["bench"] = Environment.MachineName,
            });
        }

        // Thread-safe: seq is atomic, the queue is concurrent, serialization
        // happens on the emitting thread (events are rare; the cost is a small
        // string build). exactTick comes from main-thread hook sites; everything
        // else stamps the last published snapshot tick (±1 frame — the schema
        // documents the approximation).
        public static void Emit(string type, Dictionary<string, object> payload, int? exactTick = null)
        {
            if (writer == null) return;
            long n = Interlocked.Increment(ref seq);
            var evt = new Dictionary<string, object>
            {
                ["seq"] = n,
                ["tick"] = exactTick ?? Runtime.GameState.tick,
                ["wall"] = DateTime.UtcNow.ToString("o"),
                ["type"] = type,
                ["payload"] = payload,
            };
            var sb = new StringBuilder(256);
            MiniJson.Write(sb, evt);
            pending.Enqueue(sb.ToString());
        }

        public static void EmitError(string text, int? exactTick = null)
        {
            string key = text ?? "";
            int count = errorCounts.AddOrUpdate(key, 1, (_, c) => c + 1);
            if (count > RedErrorCapPerText + 1) return;
            if (count == RedErrorCapPerText + 1)
            {
                Emit("red_error", new Dictionary<string, object>
                {
                    ["msg"] = Truncate(text, 200),
                    ["suppressed"] = true,
                }, exactTick);
                return;
            }
            Emit("red_error", new Dictionary<string, object> { ["msg"] = Truncate(text, 2000) }, exactTick);
        }

        public static void EmitWarning(string text, int? exactTick = null)
        {
            if (!warningsSeen.TryAdd(text ?? "", true)) return;
            Emit("warning", new Dictionary<string, object> { ["msg"] = Truncate(text, 2000) }, exactTick);
        }

        // Poller thread only.
        public static void Flush()
        {
            try
            {
                while (pending.TryDequeue(out var line)) writer.WriteLine(line);
            }
            catch { }
        }

        public static string Truncate(string s, int max)
            => s == null ? null : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }
}
