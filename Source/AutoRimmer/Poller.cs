using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Verse;

namespace AutoRimmer
{
    // File half of the bridge, all on a background thread (AnalyzerBridge's
    // CommandPoller / logrelay's flusher pattern): scans the command inbox,
    // routes to the verb registry, writes results and a ~1Hz status heartbeat.
    // After Init (main thread) it never touches Verse — game state comes from
    // Runtime.GameState, and main-thread verbs are handed to the GameComponent
    // via Runtime.Pending.
    //
    // Layout under <SaveDataFolderPath>/AutoRimmer/:
    //   commands/<id>.json       inbox (one JSON envelope per file)
    //   commands/done/           consumed commands (moved BEFORE execution)
    //   results/<id>.json        exactly one result per consumed command
    //   status.json              ~1Hz heartbeat
    //   journal/                 reserved for spec 1.2 (events.ndjson)
    public static class Poller
    {
        private const int PollMs = 500;
        private const double MinFileAgeMs = 250;     // writer-finished heuristic
        private const double NoGameAfterSeconds = 5;

        // Deliberately much longer than NoGameAfterSeconds. A stalled heartbeat
        // is ambiguous: the game may have unloaded, or the main thread may just
        // be inside a long event (map generation, a big-colony autosave), and
        // falsely abandoning a HEALTHY in-flight advance is far worse than
        // answering an orphaned one late. 5s is right for refusing a NEW
        // command with no-active-game (it is retryable); 20s is the bar for
        // declaring an in-flight command dead (it is not).
        private const double AbandonAfterSeconds = 20;

        private static string root, inboxDir, doneDir, resultsDir, statusPath;
        private static long lastHeartbeat = -1;
        private static DateTime lastBeatChange = DateTime.UtcNow;
        private static DateTime lastStatusWrite = DateTime.MinValue;
        private static bool sawGame;

        public static string Root => root;

        public static void Init()
        {
            root = Path.Combine(GenFilePaths.SaveDataFolderPath, "AutoRimmer");
            inboxDir = Path.Combine(root, "commands");
            doneDir = Path.Combine(inboxDir, "done");
            resultsDir = Path.Combine(root, "results");
            statusPath = Path.Combine(root, "status.json");
            Directory.CreateDirectory(inboxDir);
            Directory.CreateDirectory(doneDir);
            Directory.CreateDirectory(resultsDir);
            Directory.CreateDirectory(Path.Combine(root, "journal"));
            Runtime.SessionId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss");

            // A command left in the inbox by a crash/kill must never replay on
            // the next launch — consume it with an explicit error instead.
            foreach (var stale in Directory.GetFiles(inboxDir, "*.json"))
            {
                string id = IdOf(stale);
                Consume(stale);
                WriteResult(Result.Fail(id, "?", Err.StaleOnRestart,
                    "command file predates this game session"));
            }

            var t = new Thread(Loop) { IsBackground = true, Name = "AutoRimmerPoller" };
            t.Start();
        }

        // The cycle order IS the "journal flushed before the result" invariant,
        // and the old order did not establish it (git-bug 4b65a28, defect 6).
        // Flush() and the Outgoing drain read two independent queues, so
        // flushing and then draining proved nothing about what the drain would
        // find; and ScanInbox ran BEFORE Flush, which executes the off-thread
        // `journal` verb against a file that is up to one cycle stale.
        //
        // Correct order, and the reason for each step:
        //   1. boundary check   — answer anything the vanished game orphaned
        //   2. Flush            — the `journal` verb about to run in step 3
        //                         reads the FILE; it must be current first
        //   3. ScanInbox        — buffers its results rather than writing them,
        //                         so no result escapes ahead of step 4
        //   4. drain Outgoing   — into the SAME buffer, so the set of results
        //                         this cycle will write is now fixed
        //   5. Flush            — everything journaled before any of those
        //                         results is now on disk
        //   6. write the buffer — every result file is therefore
        //                         journal-consistent by construction
        private static readonly List<Result> batch = new List<Result>();

        private static void Loop()
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(PollMs);
                    CheckGameBoundary();
                    Journal.Flush();
                    batch.Clear();
                    ScanInbox(batch);
                    while (Runtime.Outgoing.TryDequeue(out var result)) batch.Add(result);
                    Journal.Flush();
                    for (int i = 0; i < batch.Count; i++) WriteResult(batch[i]);
                    batch.Clear();
                    if ((DateTime.UtcNow - lastStatusWrite).TotalSeconds >= 1)
                    {
                        lastStatusWrite = DateTime.UtcNow;
                        ThermalGovernor.Poll(root);
                        WriteStatus();
                    }
                }
                catch { }
            }
        }

        private static bool GameLoaded() => SecondsSinceHeartbeat() < NoGameAfterSeconds;

        // Poller thread only (it advances the edge-detector's own state).
        // double.MaxValue before the very first beat, so "never started" reads
        // the same as "long gone".
        private static double SecondsSinceHeartbeat()
        {
            long beat = Interlocked.Read(ref Runtime.Heartbeat);
            if (beat != lastHeartbeat)
            {
                lastHeartbeat = beat;
                lastBeatChange = DateTime.UtcNow;
            }
            return beat > 0 ? (DateTime.UtcNow - lastBeatChange).TotalSeconds : double.MaxValue;
        }

        // The main thread cannot notice its own disappearance: once the game
        // unloads, GameComponentUpdate stops, so an advance in flight and every
        // command already queued for the safe point would wait forever — the
        // commands consumed into done/ with zero result files (1.5 blocker 2).
        // The heartbeat is the only unload signal available off-thread, so the
        // poller owns this edge.
        private static void CheckGameBoundary()
        {
            double stalled = SecondsSinceHeartbeat();
            if (stalled < NoGameAfterSeconds) { sawGame = true; return; }
            if (!sawGame || stalled < AbandonAfterSeconds) return;
            sawGame = false;
            int answered = Runtime.ResetForGameBoundary(Runtime.BoundaryDetail);
            Journal.Emit("session", new Dictionary<string, object>
            {
                ["kind"] = "unloaded",
                ["aborted"] = answered,
            });
        }

        // Results are buffered into `sink` rather than written here: every
        // result file this cycle produces must land AFTER the cycle's second
        // Journal.Flush, and an off-thread verb executed inline would otherwise
        // beat it to disk.
        private static void ScanInbox(List<Result> sink)
        {
            foreach (var file in Directory.GetFiles(inboxDir, "*.json"))
            {
                if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalMilliseconds < MinFileAgeMs)
                    continue;
                string text;
                try { text = File.ReadAllText(file); }
                catch { continue; } // mid-write or locked; retry next scan
                string fallbackId = IdOf(file);
                Consume(file);

                var obj = MiniJson.Parse(text);
                if (obj == null)
                {
                    sink.Add(Result.Fail(fallbackId, "?", Err.BadJson));
                    continue;
                }

                string id = MiniJson.GetString(obj, "id", fallbackId);
                string op = MiniJson.GetString(obj, "op");
                if (op == null)
                {
                    sink.Add(Result.Fail(id, "?", Err.UnknownOp, "envelope has no 'op'"));
                    continue;
                }

                Dictionary<string, object> args = VerbArgs.Empty;
                if (obj.TryGetValue("args", out var rawArgs) && rawArgs != null)
                {
                    args = rawArgs as Dictionary<string, object>;
                    if (args == null)
                    {
                        sink.Add(Result.Fail(id, op, Err.BadArgs, "'args' must be an object"));
                        continue;
                    }
                }

                var verb = VerbRegistry.Get(op);
                if (verb == null)
                {
                    sink.Add(Result.Fail(id, op, Err.UnknownOp,
                        "known ops: " + string.Join(", ", VerbRegistry.Ops)));
                    continue;
                }

                var cmd = new PendingCommand { Id = id, Op = op, Verb = verb, Args = args };
                if (!verb.MainThread)
                {
                    sink.Add(VerbRegistry.Execute(cmd)); // handler contract: no Verse access
                }
                else if (!GameLoaded())
                {
                    sink.Add(Result.Fail(id, op, Err.NoActiveGame,
                        "load a save first; this verb runs at the in-game safe point"));
                }
                else
                {
                    Runtime.Pending.Enqueue(cmd);
                }
            }
        }

        private static void Consume(string file)
        {
            try
            {
                string dest = Path.Combine(doneDir, Path.GetFileName(file));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(file, dest);
            }
            catch
            {
                try { File.Delete(file); } catch { }
            }
        }

        private static string IdOf(string file) => Path.GetFileNameWithoutExtension(file);

        // One result FILE per command id. Sanitizing alone collapsed distinct
        // ids onto one filename — "a/b" and "a_b" both became "a_b.json", so
        // one command silently overwrote the other's result (git-bug 4b65a28).
        // A clean id (letters, digits, - and _, which is every id rwa
        // generates) is passed through byte-for-byte, so this changes no
        // existing filename; anything the sanitizer had to touch gets the
        // original id's hash appended, which makes the mapping injective again.
        private static string ResultFileName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "unnamed.json";
            string safe = Sanitize(id);
            if (safe == id && id.Length <= 120) return safe + ".json";
            if (safe.Length > 120) safe = safe.Substring(0, 120);
            return safe + "-" + StableHash(id) + ".json";
        }

        private static string Sanitize(string id)
        {
            if (string.IsNullOrEmpty(id)) return "unnamed";
            var sb = new StringBuilder(id.Length);
            foreach (char c in id)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        // FNV-1a 32-bit. Deliberately not string.GetHashCode(): that is
        // randomized per process on some runtimes, and the same id must map to
        // the same filename across a relaunch (the stale-on-restart answer is
        // written to it).
        private static string StableHash(string s)
        {
            uint h = 2166136261;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619;
            }
            return h.ToString("x8");
        }

        // Every consumed command owes exactly one result file, so the JSON
        // BUILD has to be inside the guard too, not just the write. Before 1.5
        // only AtomicWrite was guarded, and the result was already dequeued by
        // then: a serializer throw lost that result permanently AND aborted the
        // rest of the poller cycle — the remaining results, the journal flush
        // and the status heartbeat with it (git-bug 4b65a28, defect 4).
        //
        // MiniJson.Write is now throw-proof in its own right; this is the
        // second line of defence, and it degrades to a result file that says
        // what happened rather than to silence.
        private static void WriteResult(Result r)
        {
            if (r == null) return;
            string path;
            try { path = Path.Combine(resultsDir, ResultFileName(r.Id)); }
            catch { return; } // nowhere to write it; nothing further is possible
            string content;
            try
            {
                content = BuildResultJson(r);
            }
            catch (Exception e)
            {
                try { content = FallbackResultJson(r, e); }
                catch { return; }
            }
            AtomicWrite(path, content);
        }

        // Envelope per DESIGN.md §Protocol, plus a small state header on every
        // response (open-question resolution: yes — the agent learns when its
        // command landed without a second round trip).
        private static string BuildResultJson(Result r)
        {
            var snap = Runtime.GameState;
            var sb = new StringBuilder(512);
            sb.Append("{\"id\":").Append(MiniJson.J(r.Id))
              .Append(",\"op\":").Append(MiniJson.J(r.Op))
              .Append(",\"ok\":").Append(r.Ok ? "true" : "false");
            if (r.Ok)
            {
                sb.Append(",\"data\":");
                MiniJson.Write(sb, r.Data);
            }
            else
            {
                sb.Append(",\"error\":{\"code\":").Append(MiniJson.J(r.ErrorCode))
                  .Append(",\"detail\":").Append(MiniJson.J(r.ErrorDetail))
                  .Append('}');
            }
            bool loaded = GameLoaded() && snap.gameLoaded;
            sb.Append(",\"state\":{\"gameLoaded\":").Append(loaded ? "true" : "false")
              .Append(",\"tick\":").Append(snap.tick)
              .Append(",\"paused\":").Append(snap.paused ? "true" : "false")
              .Append('}');
            sb.Append(",\"sid\":").Append(MiniJson.J(Runtime.SessionId))
              .Append(",\"ts\":").Append(MiniJson.J(DateTime.UtcNow.ToString("o")))
              .Append('}');
            return sb.ToString();
        }

        // Hand-built from nothing but MiniJson.J over known-safe strings, so it
        // cannot fail the same way the real builder did. The command still gets
        // its one result file and the caller still learns the id, the op and
        // why.
        private static string FallbackResultJson(Result r, Exception e)
        {
            var sb = new StringBuilder(384);
            sb.Append("{\"id\":").Append(MiniJson.J(r.Id))
              .Append(",\"op\":").Append(MiniJson.J(r.Op))
              .Append(",\"ok\":false,\"error\":{\"code\":").Append(MiniJson.J(Err.Exception))
              .Append(",\"detail\":").Append(MiniJson.J(
                  "result serialization failed; the result data is lost but the command is answered: "
                  + Journal.Truncate(e.ToString(), 1500)))
              .Append("},\"sid\":").Append(MiniJson.J(Runtime.SessionId))
              .Append(",\"ts\":").Append(MiniJson.J(DateTime.UtcNow.ToString("o")))
              .Append('}');
            return sb.ToString();
        }

        private static void WriteStatus()
        {
            var snap = Runtime.GameState;
            bool loaded = GameLoaded() && snap.gameLoaded;
            var sb = new StringBuilder(384);
            sb.Append("{\"ts\":").Append(MiniJson.J(DateTime.UtcNow.ToString("o")))
              .Append(",\"sid\":").Append(MiniJson.J(Runtime.SessionId))
              .Append(",\"mod\":").Append(MiniJson.J(Runtime.ModVersion))
              .Append(",\"gameLoaded\":").Append(loaded ? "true" : "false")
              .Append(",\"paused\":").Append(snap.paused ? "true" : "false")
              .Append(",\"speed\":").Append(MiniJson.J(snap.speed))
              .Append(",\"tick\":").Append(snap.tick)
              .Append(",\"fps\":").Append(MiniJson.N(snap.fps))
              // The snapshot outlives the main thread that published it: after
              // an unload, snap.activeOp still names the command the boundary
              // reset already answered. gameLoaded:false governs, but a stale
              // op name beside it reads as a contradiction — null it instead.
              .Append(",\"activeOp\":").Append(MiniJson.J(loaded ? snap.activeOp : null));
            if (TimeDriver.Active)
            {
                sb.Append(",\"advance\":{\"id\":").Append(MiniJson.J(TimeDriver.ActiveId))
                  .Append(",\"ticks_done\":").Append(TimeDriver.TicksDone)
                  .Append(",\"target\":").Append(TimeDriver.Target)
                  .Append('}');
            }
            // Present only while a force-pausing modal is up — i.e. exactly
            // when `advance` cannot run (spec 1.7). Its absence is the
            // heartbeat's way of saying the stack is clear.
            if (snap.forcePause != null)
            {
                sb.Append(",\"forcePause\":");
                MiniJson.Write(sb, snap.forcePause);
            }
            if (ThermalGovernor.Available)
            {
                sb.Append(",\"thermal\":{\"c\":").Append(MiniJson.N(ThermalGovernor.TempC))
                  .Append(",\"scale\":").Append(MiniJson.N(ThermalGovernor.Scale))
                  .Append('}');
            }
            sb.Append('}');
            AtomicWrite(statusPath, sb.ToString());
        }

        // tmp + rename so readers polling for the file never see a partial write.
        private static void AtomicWrite(string path, string content)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { }
        }
    }
}
