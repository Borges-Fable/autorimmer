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
        //
        // Both dedupe tables are keyed on the EXACT text, and an error whose
        // text carries a tick number or a pawn name is a fresh key every time,
        // so both are bounded: past DistinctTextCap keys the tables stop
        // growing and new texts share one overflow counter. The cap bounds
        // MEMORY only — OnRedError below still fires for every single
        // occurrence, capped or not.
        private const int RedErrorCapPerText = 3;
        private const int DistinctTextCap = 512;
        private static readonly ConcurrentDictionary<string, int> errorCounts = new ConcurrentDictionary<string, int>();
        private static readonly ConcurrentDictionary<string, bool> warningsSeen = new ConcurrentDictionary<string, bool>();
        private static int errorOverflowCount;
        private static int warningOverflowNoted;

        public static string CurrentFile => path;

        public static long CurrentSeq => Interlocked.Read(ref seq);

        // Synchronous tap on every emission — (type, payload, tick, seq) —
        // fired on the EMITTING thread. TimeDriver's halt matchers hang here;
        // handlers must be cheap and thread-safe.
        public static event Action<string, Dictionary<string, object>, int, long> OnEvent;

        // Every red error, INCLUDING the ones the per-text cap keeps out of the
        // file — (text, occurrence, seq of the emitted event or 0 if suppressed,
        // tick). OnEvent only ever sees EMITTED events, so a halt tap hung
        // there stopped halting from the 5th occurrence of an identical error
        // onward: `advance {halt_on_error:true}` returned a clean reason:"ticks"
        // while the error fired thousands of times, which is exactly the
        // zero-red-errors failure the invariant exists to catch
        // (1.5 blocker 3).
        //
        // The cap is a JOURNAL policy — an error storm must not flood the file
        // — and that goal is entirely separable from the halt policy. This
        // event is the separation: file volume stays capped, halting does not.
        public static event Action<string, int, long, int> OnRedError;

        // In-memory (seq, type) ring so what-changed queries (digest, spec 2.1)
        // never read the journal file on the main thread. 4096 events dwarfs
        // any between-glance window; a since older than the ring says so.
        private const int RingSize = 4096;
        private static readonly ValueTuple<long, string>[] ring = new ValueTuple<long, string>[RingSize];
        private static readonly object ringLock = new object();

        public static Dictionary<string, int> CountsSince(long since, out long lastSeq, out bool truncated)
        {
            var counts = new Dictionary<string, int>();
            lock (ringLock)
            {
                lastSeq = seq;
                truncated = since < lastSeq - RingSize;
                for (long s = Math.Max(since + 1, lastSeq - RingSize + 1); s <= lastSeq; s++)
                {
                    var entry = ring[(int)(s % RingSize)];
                    if (entry.Item1 != s) continue;
                    counts[entry.Item2] = counts.TryGetValue(entry.Item2, out var c) ? c + 1 : 1;
                }
            }
            return counts;
        }

        // Main thread, from the mod ctor (after the protocol root exists).
        public static void Init(string root)
        {
            dir = Path.Combine(root, "journal");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, Runtime.SessionId + ".ndjson");
            writer = new StreamWriter(
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false)) { AutoFlush = true };

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
        // Returns the seq it claimed, or 0 if the journal is not open — callers
        // that need to cite the line they just wrote (EmitError) must not
        // re-read CurrentSeq, which another thread may have moved on.
        public static long Emit(string type, Dictionary<string, object> payload, int? exactTick = null)
        {
            if (writer == null) return 0;
            long n = Interlocked.Increment(ref seq);
            int tick = exactTick ?? Runtime.GameState.tick;
            var evt = new Dictionary<string, object>
            {
                ["seq"] = n,
                ["tick"] = tick,
                ["wall"] = DateTime.UtcNow.ToString("o"),
                ["type"] = type,
                ["payload"] = payload,
            };
            var sb = new StringBuilder(256);
            MiniJson.Write(sb, evt);
            pending.Enqueue(sb.ToString());
            lock (ringLock) { ring[(int)(n % RingSize)] = ValueTuple.Create(n, type); }
            try { OnEvent?.Invoke(type, payload, tick, n); }
            catch { }
            return n;
        }

        public static void EmitError(string text, int? exactTick = null)
        {
            string key = text ?? "";
            bool overflow = errorCounts.Count >= DistinctTextCap && !errorCounts.ContainsKey(key);
            int count = overflow
                ? Interlocked.Increment(ref errorOverflowCount)
                : errorCounts.AddOrUpdate(key, 1, (_, c) => c + 1);

            // The journal half: capped, so a storm cannot flood the file.
            long emittedSeq = 0;
            if (count <= RedErrorCapPerText)
            {
                var payload = new Dictionary<string, object> { ["msg"] = Truncate(text, 2000) };
                if (overflow) payload["overflow"] = true;
                emittedSeq = Emit("red_error", payload, exactTick);
            }
            else if (count == RedErrorCapPerText + 1)
            {
                var payload = new Dictionary<string, object>
                {
                    ["msg"] = Truncate(text, 200),
                    ["suppressed"] = true,
                };
                if (overflow) payload["overflow"] = true;
                emittedSeq = Emit("red_error", payload, exactTick);
            }

            // The halt half: uncapped, always. Fired AFTER the emit so a halt
            // that lands on an emitted error can cite that event's seq;
            // seq 0 means "this occurrence is not in the file".
            try { OnRedError?.Invoke(key, count, emittedSeq, exactTick ?? Runtime.GameState.tick); }
            catch { }
        }

        public static void EmitWarning(string text, int? exactTick = null)
        {
            string key = text ?? "";
            if (warningsSeen.Count >= DistinctTextCap && !warningsSeen.ContainsKey(key))
            {
                // One marker, once, then silence: warnings carry no halt
                // semantics, so the bound costs nothing but visibility, and
                // LogRelay has the full stream regardless.
                if (Interlocked.CompareExchange(ref warningOverflowNoted, 1, 0) == 0)
                    Emit("warning", new Dictionary<string, object>
                    {
                        ["msg"] = $"[AutoRimmer] {DistinctTextCap} distinct warning texts this session; further NEW warnings are not journaled (see LogRelay)",
                        ["overflow"] = true,
                    }, exactTick);
                return;
            }
            if (!warningsSeen.TryAdd(key, true)) return;
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
