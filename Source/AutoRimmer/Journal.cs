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

        // One lock over the whole emit critical section — seq claim, line
        // build, queue enqueue, ring write — and over the ring read, so a
        // CountsSince never straddles a half-recorded event either. Nothing
        // inside it calls out to non-journal code, which is what makes it
        // provably deadlock-free.
        private static readonly object journalLock = new object();

        public static Dictionary<string, int> CountsSince(long since, out long lastSeq, out bool truncated)
        {
            var counts = new Dictionary<string, int>();
            lock (journalLock)
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

        // exactTick comes from main-thread hook sites; everything else stamps
        // the last published snapshot tick (±1 frame — the schema documents the
        // approximation). Returns the seq it claimed, or 0 if the journal is not
        // open — callers that need to cite the line they just wrote (EmitError)
        // must not re-read CurrentSeq, which another thread may have moved on.
        //
        // Claim, serialize and enqueue happen under ONE lock. They used not to:
        // seq was an interlocked increment and the enqueue followed, so two
        // emitting threads could interleave and put seq 6 in the file ahead of
        // seq 5. Filters are order-independent and never noticed; rwtest (5.1)
        // assertions would not be, and JOURNAL.md calls monotonic seq an
        // invariant (git-bug 4b65a28). Events are rare and the body is a small
        // string build, so the contention is nil.
        //
        // OnEvent fires OUTSIDE the lock, deliberately: it runs arbitrary
        // handler code (TimeDriver's halt matchers) and holding the journal's
        // lock across that is how a deadlock gets built. Handlers may therefore
        // observe two events out of order; they are matchers, not readers, and
        // each carries its own seq.
        public static long Emit(string type, Dictionary<string, object> payload, int? exactTick = null)
        {
            if (writer == null) return 0;
            int tick = exactTick ?? Runtime.GameState.tick;
            long n;
            lock (journalLock)
            {
                n = Interlocked.Increment(ref seq);
                var evt = new Dictionary<string, object>
                {
                    ["seq"] = n,
                    ["tick"] = tick,
                    ["wall"] = DateTime.UtcNow.ToString("o"),
                    ["type"] = type,
                    ["payload"] = payload,
                };
                // The seq is claimed by this point, so SOMETHING must be
                // enqueued or the file gets the gap this lock exists to
                // prevent. MiniJson.Write is throw-proof as of 1.5; this is
                // the backstop that keeps the invariant true even if it is not.
                string line;
                try
                {
                    var sb = new StringBuilder(256);
                    MiniJson.Write(sb, evt);
                    line = sb.ToString();
                }
                catch (Exception e)
                {
                    line = "{\"seq\":" + n + ",\"tick\":" + tick
                        + ",\"wall\":" + MiniJson.J(DateTime.UtcNow.ToString("o"))
                        + ",\"type\":" + MiniJson.J(type)
                        + ",\"payload\":{\"autorimmer_serialize_error\":"
                        + MiniJson.J(Truncate(e.ToString(), 500)) + "}}";
                }
                pending.Enqueue(line);
                ring[(int)(n % RingSize)] = ValueTuple.Create(n, type);
            }
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
        //
        // PEEK, write, THEN dequeue. It used to dequeue first, inside a
        // catch-all: any writer failure discarded the line it had already
        // taken, producing exactly the seq gap JOURNAL.md calls an invariant
        // rather than a hope (git-bug 4b65a28, defect 5). The line now stays
        // in the queue until the write has actually returned, so a transient
        // failure — a locked file, a full disk that frees up — costs a retry
        // next cycle instead of a hole in the chronology.
        //
        // The bounded part matters as much as the peek: an unbounded retry
        // against a permanently dead writer would grow the queue without
        // limit. After FlushFailuresBeforeReopen consecutive failed cycles the
        // stream is reopened once in append mode; if that fails too the journal
        // is closed for the session, which makes Emit a no-op — no more seq
        // claims, so what was written stays gapless and the file simply stops.
        private const int FlushFailuresBeforeReopen = 20; // ~10s at PollMs=500
        private static int flushFailures;

        public static void Flush()
        {
            if (writer == null) return;
            try
            {
                while (pending.TryPeek(out var line))
                {
                    writer.WriteLine(line);
                    pending.TryDequeue(out _);
                }
                flushFailures = 0;
            }
            catch (Exception e)
            {
                if (++flushFailures < FlushFailuresBeforeReopen) return;
                flushFailures = 0;
                try
                {
                    writer = new StreamWriter(
                        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(false)) { AutoFlush = true };
                }
                catch
                {
                    writer = null;
                    // A sibling file, not Log.Warning: this is the poller
                    // thread, which never touches Verse, and Log.Warning is
                    // patched straight back into this class.
                    try { File.WriteAllText(path + ".error", DateTime.UtcNow.ToString("o") + "\n" + e); }
                    catch { }
                }
            }
        }

        public static string Truncate(string s, int max)
            => s == null ? null : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }
}
