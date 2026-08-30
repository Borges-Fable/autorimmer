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

        private static string root, inboxDir, doneDir, resultsDir, statusPath;
        private static long lastHeartbeat = -1;
        private static DateTime lastBeatChange = DateTime.UtcNow;
        private static DateTime lastStatusWrite = DateTime.MinValue;

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

        private static void Loop()
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(PollMs);
                    ScanInbox();
                    Journal.Flush();
                    while (Runtime.Outgoing.TryDequeue(out var result)) WriteResult(result);
                    if ((DateTime.UtcNow - lastStatusWrite).TotalSeconds >= 1)
                    {
                        lastStatusWrite = DateTime.UtcNow;
                        WriteStatus();
                    }
                }
                catch { }
            }
        }

        private static bool GameLoaded()
        {
            long beat = Interlocked.Read(ref Runtime.Heartbeat);
            if (beat != lastHeartbeat)
            {
                lastHeartbeat = beat;
                lastBeatChange = DateTime.UtcNow;
            }
            return beat > 0 && (DateTime.UtcNow - lastBeatChange).TotalSeconds < NoGameAfterSeconds;
        }

        private static void ScanInbox()
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
                    WriteResult(Result.Fail(fallbackId, "?", Err.BadJson));
                    continue;
                }

                string id = MiniJson.GetString(obj, "id", fallbackId);
                string op = MiniJson.GetString(obj, "op");
                if (op == null)
                {
                    WriteResult(Result.Fail(id, "?", Err.UnknownOp, "envelope has no 'op'"));
                    continue;
                }

                Dictionary<string, object> args = VerbArgs.Empty;
                if (obj.TryGetValue("args", out var rawArgs) && rawArgs != null)
                {
                    args = rawArgs as Dictionary<string, object>;
                    if (args == null)
                    {
                        WriteResult(Result.Fail(id, op, Err.BadArgs, "'args' must be an object"));
                        continue;
                    }
                }

                var verb = VerbRegistry.Get(op);
                if (verb == null)
                {
                    WriteResult(Result.Fail(id, op, Err.UnknownOp,
                        "known ops: " + string.Join(", ", VerbRegistry.Ops)));
                    continue;
                }

                var cmd = new PendingCommand { Id = id, Op = op, Verb = verb, Args = args };
                if (!verb.MainThread)
                {
                    WriteResult(VerbRegistry.Execute(cmd)); // handler contract: no Verse access
                }
                else if (!GameLoaded())
                {
                    WriteResult(Result.Fail(id, op, Err.NoActiveGame,
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

        private static string Sanitize(string id)
        {
            if (string.IsNullOrEmpty(id)) return "unnamed";
            var sb = new StringBuilder(id.Length);
            foreach (char c in id)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        // Envelope per DESIGN.md §Protocol, plus a small state header on every
        // response (open-question resolution: yes — the agent learns when its
        // command landed without a second round trip).
        private static void WriteResult(Result r)
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
            AtomicWrite(Path.Combine(resultsDir, Sanitize(r.Id) + ".json"), sb.ToString());
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
              .Append(",\"activeOp\":").Append(MiniJson.J(snap.activeOp))
              .Append('}');
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
