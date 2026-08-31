using System;
using System.Collections.Generic;
using System.Diagnostics;
using Verse;

namespace AutoRimmer
{
    // The advance engine (spec 1.3). Shape fixed by the 0.1 spike and verified
    // against decompiled Game.UpdatePlay/TickManager: a budgeted DoSingleTick
    // loop inside GameComponentUpdate that RETURNS EVERY FRAME, with
    // CurTimeSpeed pinned to Paused the whole time. The GameComponent slot runs
    // after the frame's MapUpdates, so every once-per-frame manager has already
    // run — nothing per-frame is re-implemented (vanilla itself runs up to 300
    // ticks/frame against the same envelope; MP's TickPatch.DoUpdate is the
    // same loop). TickManagerUpdate early-returns on Paused, so all ticking is
    // ours — no double-stepping.
    //
    // Never `while(ticks--) DoSingleTick()` unbounded: the frame budget
    // (<=25ms, thermally halvable) and the per-second quota (max_tps, capped by
    // config regardless of the caller) are the whole point. tps is
    // state-dependent by 3x (raids triple per-tick cost), so max_tps is a
    // ceiling, never a promise — the result reports what actually happened.
    public static class TimeDriver
    {
        private enum Until { None, Ticks, Letter, Threat, Alert, Event }

        // Written on the main thread; read by the poller for status.json.
        public static volatile bool Active;
        public static volatile int TicksDone;
        public static volatile int Target; // -1 = until-driven
        public static string ActiveId; // reference write, atomic

        private static PendingCommand cmd;
        private static Until until;
        private static string filterA;             // letter def / alert id / event type
        private static string filterB;             // event contains
        private static bool haltOnError;
        private static int timeoutTicks;
        private static int effMaxTps;
        private static int startTick;
        private static long startSeq;
        private static DateTime startWall;

        // Halt handshake: Notice() (any thread) sets these; the loop polls.
        private static volatile bool haltFlag;
        private static string haltReason;
        private static Dictionary<string, object> haltEvent;
        private static long haltSeq;

        private static readonly Stopwatch frameClock = new Stopwatch();
        private static int quotaSecond = -1;
        private static int ticksThisSecond;
        private static int repinned; // external speed-change re-pins, diagnostics

        private static bool slowerNow;
        private static int slowerFromTick;
        private static readonly List<object> slowerSpans = new List<object>();

        public static void HookJournal()
        {
            Journal.OnEvent += Notice;
            // NOT Journal.OnEvent: that tap only ever sees EMITTED events, and
            // the journal's per-text cap stops emitting after the 4th identical
            // red error — so halt_on_error silently died from the 5th
            // occurrence onward, which is precisely the repeating error a long
            // unattended run produces (1.5 blocker 3).
            Journal.OnRedError += NoticeRedError;
        }

        // Main thread, from the advance verb handler. Returns null on success,
        // else an immediate-failure Result.
        public static Result Start(PendingCommand command, VerbArgs args)
        {
            var tm = Find.TickManager;
            int ticks = args.Int("ticks", -1);
            var untilObj = args.Has("until") ? args.Raw("until") : null;

            if (ticks < 0 && untilObj == null)
                throw new VerbArgsException("advance needs 'ticks' or 'until'");
            if (ticks >= 0 && untilObj != null)
                throw new VerbArgsException("'ticks' and 'until' are exclusive");

            until = Until.Ticks;
            filterA = null;
            filterB = null;
            if (untilObj != null)
            {
                if (!(untilObj is Dictionary<string, object> u))
                    throw new VerbArgsException("'until' must be an object");
                var ua = new VerbArgs(u);
                if (u.ContainsKey("letter"))
                {
                    until = Until.Letter;
                    if (u["letter"] is string def) filterA = def;
                    else if (!(u["letter"] is bool)) throw new VerbArgsException("until.letter must be true or a LetterDef name");
                }
                else if (u.ContainsKey("threat")) until = Until.Threat;
                else if (u.ContainsKey("alert"))
                {
                    until = Until.Alert;
                    if (u["alert"] is string id) filterA = id;
                    else if (!(u["alert"] is bool)) throw new VerbArgsException("until.alert must be true or an Alert class name");
                }
                else if (u.ContainsKey("event"))
                {
                    until = Until.Event;
                    if (!(u["event"] is Dictionary<string, object> ev))
                        throw new VerbArgsException("until.event must be {type, contains?}");
                    var eva = new VerbArgs(ev);
                    filterA = eva.StrReq("type");
                    filterB = eva.Str("contains");
                }
                else throw new VerbArgsException("until needs one of: letter, threat, alert, event");
            }

            haltOnError = args.Bool("halt_on_error", true);
            timeoutTicks = args.Int("timeout_ticks", until == Until.Ticks ? 0 : 600000);
            int askedTps = args.Int("max_tps", Config.MaxTpsCap);
            effMaxTps = Math.Min(Math.Max(30, askedTps), Config.MaxTpsCap); // hard cap, always

            // Own the pause. The CurTimeSpeed setter silently no-ops when the
            // player cannot control time (cutscenes, landing confirmations) —
            // verify it took rather than risk TickManagerUpdate double-ticking
            // beside our loop.
            tm.Pause();
            if (tm.CurTimeSpeed != TimeSpeed.Paused)
                return Result.Fail(command.Id, command.Op, "cannot-pause",
                    "the game refused Paused (cutscene or landing confirmation in progress)");

            cmd = command;
            Target = ticks;
            TicksDone = 0;
            startTick = tm.TicksGame;
            startSeq = Journal.CurrentSeq;
            startWall = DateTime.UtcNow;
            haltFlag = false;
            haltReason = null;
            haltEvent = null;
            haltSeq = 0;
            quotaSecond = -1;
            ticksThisSecond = 0;
            repinned = 0;
            slowerSpans.Clear();
            slowerNow = false;
            ActiveId = command.Id;
            Active = true;
            return null;
        }

        // Any thread, called synchronously from Journal.Emit.
        private static void Notice(string type, Dictionary<string, object> payload, int tick, long seq)
        {
            if (!Active || haltFlag) return;
            // red_error is deliberately NOT matched here — NoticeRedError below
            // owns the halt, upstream of the journal's dedupe cap. An explicit
            // until:{event:{type:"red_error"}} still works through Until.Event,
            // and it is honestly capped: it is a journal-event matcher.
            switch (until)
            {
                case Until.Letter:
                    if (type == "letter" && (filterA == null || Str(payload, "def") == filterA))
                        Halt("letter", payload, seq);
                    break;
                case Until.Threat:
                    if (type == "letter")
                    {
                        string def = Str(payload, "def");
                        if (def == "ThreatBig" || def == "ThreatSmall") Halt("threat", payload, seq);
                    }
                    break;
                case Until.Alert:
                    if (type == "alert_on" && (filterA == null || Str(payload, "id") == filterA))
                        Halt("alert", payload, seq);
                    break;
                case Until.Event:
                    if (type == filterA && (filterB == null || PayloadContains(payload, filterB)))
                        Halt("event", payload, seq);
                    break;
            }
        }

        // Any thread, from Journal.EmitError — for EVERY occurrence, whether or
        // not the journal wrote a line for it. `journal_suppressed` tells the
        // caller not to go hunting for a journal event that the cap kept out
        // of the file; `occurrence` is which repeat this was.
        private static void NoticeRedError(string text, int occurrence, long emittedSeq, int tick)
        {
            if (!Active || haltFlag || !haltOnError) return;
            var payload = new Dictionary<string, object>
            {
                ["type"] = "red_error",
                ["msg"] = Journal.Truncate(text, 2000),
                ["occurrence"] = (double)occurrence,
                ["tick"] = (double)tick,
            };
            if (emittedSeq == 0) payload["journal_suppressed"] = true;
            Halt("red_error", payload, emittedSeq);
        }

        private static void Halt(string reason, Dictionary<string, object> evt, long seq)
        {
            haltReason = reason;
            haltEvent = evt;
            haltSeq = seq;
            haltFlag = true;
        }

        // Main thread, from the pause verb.
        public static bool Interrupt()
        {
            if (!Active) return false;
            Halt("interrupted", null, 0);
            return true;
        }

        // EITHER thread, from Runtime.ResetForGameBoundary. The game went away
        // underneath the advance (main menu, load, new game), so there is
        // nothing left to tick and possibly no Verse to touch: this is the one
        // exit that does NOT call Teardown, because Teardown pauses the
        // TickManager and there may not be one (1.5 blocker 1).
        //
        // Without it the driver's STATIC state outlives the Game object: the
        // next load resumes the old advance against a new colony and reports
        // ticks_elapsed/journal_seq spanning two games, and if no game reloads
        // every main-thread verb answers busy forever.
        public static bool Abandon(string code, string detail)
        {
            // Interlocked because the poller's unload edge and the main
            // thread's lifecycle virtual can both fire for one boundary, and
            // the command owes exactly one result file.
            var c = System.Threading.Interlocked.Exchange(ref cmd, null);
            Active = false;
            ActiveId = null;
            haltFlag = false;
            haltReason = null;
            haltEvent = null;
            haltSeq = 0;
            slowerNow = false;
            if (c == null) return false;
            var r = Result.Fail(c.Id, c.Op, code, detail);
            r.Data = null;
            Runtime.Outgoing.Enqueue(r);
            return true;
        }

        // Main thread, every GameComponentUpdate.
        public static void FrameStep()
        {
            if (!Active) return;
            var tm = Find.TickManager;

            // Something external (a mod, a debug key) changed the speed: re-pin.
            // Unpaused speed beside this loop would double-tick via
            // TickManagerUpdate.
            if (tm.CurTimeSpeed != TimeSpeed.Paused)
            {
                tm.CurTimeSpeed = TimeSpeed.Paused;
                repinned++;
            }

            // TimeSlower span bookkeeping (finding: the budget loop is immune to
            // the clamp — we neither honor nor ignore it silently, we report it).
            bool slower = tm.slower.ForcedNormalSpeed;
            if (slower != slowerNow)
            {
                if (slower) slowerFromTick = tm.TicksGame;
                else slowerSpans.Add(new List<object> { (double)slowerFromTick, (double)tm.TicksGame });
                slowerNow = slower;
            }

            double budgetMs = Config.AdvanceBudgetMs * ThermalGovernor.Scale;
            int second = Environment.TickCount / 1000;
            if (second != quotaSecond)
            {
                quotaSecond = second;
                ticksThisSecond = 0;
            }

            frameClock.Restart();
            while (true)
            {
                if (haltFlag) { Finish(haltReason); return; }
                if (Target >= 0 && TicksDone >= Target) { Finish("ticks"); return; }
                if (timeoutTicks > 0 && TicksDone >= timeoutTicks) { Finish("timeout"); return; }
                if (ticksThisSecond >= effMaxTps) return;                    // quota met; yield
                if (frameClock.Elapsed.TotalMilliseconds >= budgetMs) return; // budget spent; yield
                try
                {
                    tm.DoSingleTick();
                }
                catch (Exception e)
                {
                    FinishFailed("exception", e.ToString());
                    return;
                }
                TicksDone++;
                ticksThisSecond++;
            }
        }

        // Every exit claims `cmd` with the same interlocked exchange, so a game
        // boundary that already answered the command (Abandon, from either
        // thread) can never produce a second result file for it.
        private static void Finish(string reason)
        {
            var data = BuildData(reason);
            var c = System.Threading.Interlocked.Exchange(ref cmd, null);
            Teardown();
            if (c == null) return;
            Runtime.Outgoing.Enqueue(Result.Success(c.Id, c.Op, data));
        }

        private static void FinishFailed(string code, string detail)
        {
            var c = System.Threading.Interlocked.Exchange(ref cmd, null);
            Teardown();
            if (c == null) return;
            var r = Result.Fail(c.Id, c.Op, code, detail);
            r.Data = null;
            Runtime.Outgoing.Enqueue(r);
        }

        private static void Teardown()
        {
            // advance ALWAYS returns paused — the loop never unpaused, but be
            // explicit on the way out, whatever happened.
            try { Find.TickManager.Pause(); } catch { }
            if (slowerNow)
            {
                slowerSpans.Add(new List<object> { (double)slowerFromTick, (double)Find.TickManager.TicksGame });
                slowerNow = false;
            }
            Active = false;
            ActiveId = null;
            System.Threading.Interlocked.Exchange(ref cmd, null);
        }

        private static Dictionary<string, object> BuildData(string reason)
        {
            var tm = Find.TickManager;
            double wall = (DateTime.UtcNow - startWall).TotalSeconds;
            long endSeq = Journal.CurrentSeq;
            var data = new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["tick"] = tm.TicksGame,
                ["ticks_elapsed"] = TicksDone,
                ["wall_seconds"] = wall,
                ["avg_tps"] = wall > 0 ? TicksDone / wall : 0,
                ["max_tps_effective"] = effMaxTps,
                // empty list when nothing was journaled during the advance
                ["journal_seq"] = endSeq > startSeq
                    ? new List<object> { (double)(startSeq + 1), (double)endSeq }
                    : new List<object>(),
                ["slower_spans"] = new List<object>(slowerSpans),
            };
            if (haltEvent != null)
            {
                data["halted_on"] = haltEvent;
                data["halted_seq"] = (double)haltSeq;
            }
            if (repinned > 0) data["repinned_speed_changes"] = repinned;
            if (ThermalGovernor.Available)
            {
                data["thermal_c"] = ThermalGovernor.TempC;
                data["thermal_scale"] = ThermalGovernor.Scale;
            }
            return data;
        }

        private static string Str(Dictionary<string, object> d, string key)
            => d != null && d.TryGetValue(key, out var v) && v is string s ? s : null;

        private static bool PayloadContains(Dictionary<string, object> d, string needle)
        {
            if (d == null) return false;
            foreach (var kv in d)
                if (kv.Value is string s && s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }
}
