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
                    // The sample log rides the same cycle (git-bug 2d9a1da). It
                    // is deliberately NOT inside the journal-consistency
                    // ordering above: samples are periodic readings rather than
                    // events, nothing joins them to a result envelope by seq,
                    // and there is no invariant of the "journal flushed before
                    // the result" kind to establish for them.
                    ColonySampler.FlushSamples();
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
                    args = Underscore(args);
                }

                var verb = VerbRegistry.Get(op);
                if (verb == null)
                {
                    sink.Add(Result.Fail(id, op, Err.UnknownOp,
                        "known ops: " + string.Join(", ", VerbRegistry.Ops), args));
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
                        "load a save first; this verb runs at the in-game safe point", args));
                }
                else
                {
                    Runtime.Pending.Enqueue(cmd);
                }
            }
        }

        // HYPHENATED ARGUMENT KEYS ARE READ AS UNDERSCORED, here and nowhere
        // else (git-bug c519477; the ruling is COCKPIT.md §"Where it lives",
        // the same paragraph that generalises 7382bdd's refusal).
        //
        // WHY IT IS SAFE: no verb in the tree reads a hyphenated key. Every
        // read goes through a `VerbArgs` accessor and the key is a literal at
        // the call site, so the claim is one grep and it holds —
        //   grep -rnE '\.(Has|Raw|Str|StrReq|Bool|Num|NumReq|Int|IntReq|Long|StrList)\("[a-z_]*-'
        // finds nothing. The only hyphen anywhere in an argument name is
        // `LayoutVerbs`' NearMiss ALIAS `stuff-map`, which exists precisely to
        // catch this typo and which this rewrite now answers before it fires.
        //
        // WHY IT REWRITES RATHER THAN REFUSES, which is the opposite of the
        // rule this same issue applies to STRAY keys: a hyphenated key is not
        // an unknown argument, it is a known argument spelled the way the CLI
        // spells it. The run's own instance is the argument for it —
        // `build {dry-run:true}` was dropped ten times and each call placed a
        // REAL blueprint on the default path (audit F-S10-30/J2150). Refusing
        // would also be safe; rewriting is safe AND does what the caller
        // plainly asked. A key that is genuinely unknown still lands on the
        // read log, and on the five guarded verbs it is still refused.
        //
        // It runs on the poller thread, touches no Verse, and copies rather
        // than mutating in place: `VerbArgs.Empty` is shared and the parsed
        // dict is handed on to `Result.Args` for RefusalStreak.
        private static Dictionary<string, object> Underscore(Dictionary<string, object> args)
        {
            bool any = false;
            foreach (var kv in args)
                if (kv.Key.IndexOf('-') >= 0) { any = true; break; }
            if (!any) return args;

            var copy = new Dictionary<string, object>(args.Count);
            foreach (var kv in args) copy[kv.Key] = kv.Value;
            foreach (var kv in args)
            {
                if (kv.Key.IndexOf('-') < 0) continue;
                string under = kv.Key.Replace('-', '_');
                // The caller sent BOTH spellings: leave the hyphenated one
                // alone so it is reported (or refused) rather than silently
                // overwriting the key the caller also spelled correctly.
                if (args.ContainsKey(under)) continue;
                copy.Remove(kv.Key);
                copy[under] = kv.Value;
            }
            return copy;
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
            // git-bug f08dfc4. FIRST, before a byte is written: this call both
            // reads and ADVANCES the streak, and the streak must move for every
            // result exactly once whatever the serializer does with the rest of
            // the envelope. A success resets; a repeat past the threshold comes
            // back as a block to publish below.
            var repeated = RefusalStreak.Note(r);
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
                // git-bug e440676. `class` is READ OFF THE CODE, which carries
                // it (Runtime.ErrCode), rather than looked up here — a lookup
                // at the serializer is the table that drifts, because a new
                // code falls through its default and the default reads as an
                // answer. Every failure has one; there is no unclassified
                // branch to hit.
                sb.Append(",\"error\":{\"code\":").Append(MiniJson.J(r.ErrorCode.Code))
                  .Append(",\"class\":").Append(MiniJson.J(r.ErrorCode.Class))
                  .Append(",\"detail\":").Append(MiniJson.J(r.ErrorDetail))
                  .Append('}');
            }
            // git-bug 7382bdd. Present only when the caller supplied a key the
            // verb never read; absent on every correct command, so no existing
            // consumer sees a new field until it makes the mistake this exists
            // to name.
            if (r.IgnoredArgs != null)
            {
                sb.Append(",\"ignored_args\":");
                MiniJson.Write(sb, r.IgnoredArgs);
            }
            // The streak, noted at the top of this method. Present only once
            // the same verb has been refused the same way with the same
            // arguments RefusalStreak.Threshold times running — absent on the
            // overwhelmingly common call, exactly like `ignored_args` above,
            // and top-level for the same reason: it is a statement about the
            // CALL, not about the verb's answer.
            if (repeated != null)
            {
                sb.Append(",\"repeated\":");
                MiniJson.Write(sb, repeated);
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
              .Append(",\"ok\":false,\"error\":{\"code\":").Append(MiniJson.J(Err.Exception.Code))
              .Append(",\"class\":").Append(MiniJson.J(Err.Exception.Class))
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

        // tmp + rename so readers polling for the file never see a partial
        // write — and, since git-bug bench-down-race, never see NO file either.
        //
        // The old body was WriteAllText(tmp) / Delete(path) / Move(tmp, path),
        // which leaves a window on EVERY ~1Hz status write in which status.json
        // does not exist. `rwa` sampled the path once per poll and read that
        // window as a dead bench: openrun-20260902 cause C, three advances
        // declared dead while they were still running (97,505 ticks, 6% of all
        // time that moved unwitnessed), each followed by a `busy` naming the
        // same command id. F-S09-2's `quest` is the same race.
        //
        // File.Replace is one rename() underneath, so the path holds the old
        // bytes until the instant it holds the new ones. VERIFIED ON THE GAME'S
        // OWN RUNTIME rather than assumed — RimWorld 1.6.4871 ships Mono
        // 6.13.0 (libmonobdwgc-2.0.so) with a CoreFX System.IO stack, and a
        // probe hosted on that runtime against the game's own mscorlib.dll
        // reported:
        //   File.Replace(s,d,b)      PRESENT, and replaces while a reader holds
        //                            the destination open
        //   File.Move(s,d,overwrite) ABSENT  (it is .NET Core 3.0+, not net48)
        //   File.Move(s,d) over an existing file -> IOException
        //   File.Replace with a MISSING destination -> FileNotFoundException
        //   a reader spinning beside 500 writes: delete+move missed the path
        //   1418 times, File.Replace 0 times.
        //
        // Hence the shape below. The first write of a session has no
        // destination, so Move still owns that one case; every later write is a
        // Replace. The delete+move path survives as a last resort because this
        // method runs on the poller thread and owes every consumed command
        // exactly one result file — degrading to the old behaviour is strictly
        // better than losing the write, and it cannot throw either way.
        private static void AtomicWrite(string path, string content)
        {
            string tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
            }
            catch
            {
                // Nothing was moved; whatever is at `path` still stands, which
                // for status.json means a heartbeat that ages rather than one
                // that vanishes.
                return;
            }

            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return;
            }
            catch { }

            // Only reachable if Replace itself failed (a destination deleted
            // between the Exists and the Replace, a filesystem that will not
            // take it). This is the one branch that can still leave the path
            // missing for an instant, and it is the branch the client's
            // three-sample rule exists to cover.
            try
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }
}
