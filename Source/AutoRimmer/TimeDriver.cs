using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace AutoRimmer
{
    // The advance engine. The CONTRACT is spec 1.3's and unchanged — one
    // deferred result per command, the same halt reasons, the same journal tap,
    // busy-gating, always-return-paused, the 1.5 game-boundary abandon. What
    // spec 1.8 replaced is how the ticks are PRODUCED.
    //
    // 1.3 pinned `CurTimeSpeed = Paused` every frame and pumped
    // `tm.DoSingleTick()` in a wall-clock-budgeted loop. That was chosen in the
    // 0.1 spike to beat frame starvation on a parked window; `render_unfocused`
    // fixed frame starvation separately and nobody re-examined the loop. 1.8
    // does: `advance` now sets the game's own time speed, lets RimWorld tick
    // itself from `TickManagerUpdate`, and pauses when the stop condition is
    // met. Stop checks moved from per-tick to per-FRAME, which is why overshoot
    // exists and is reported (below).
    //
    // The reason this is strictly better rather than merely simpler:
    // `TickManager.Paused` is `curTimeSpeed == Paused || ForcePaused`, and
    // `ForcePaused` includes `Find.WindowStack.WindowsForcePause`
    // (decompiled Verse/TickManager.cs). So `TickManagerUpdate` early-returns
    // the instant a force-pausing modal is up, and its inner loop breaks on
    // `if (Paused || ...)` after every single tick. A force-pausing window
    // therefore really does stop the game now, and
    // `LetterStack.OpenAutomaticLetters` stops being starved BY CONSTRUCTION
    // instead of by our vigilance. 1.7's per-TICK guard existed only because
    // the old loop defeated vanilla's own force-pause; the per-FRAME check
    // below is all that is left of it, and it earns its place — see there.
    //
    // Vanilla's envelope, which we now simply inherit
    // (`Verse/TickManager.TickManagerUpdate`): it wants
    // `deltaTime / CurTimePerTick` ticks per frame, ceilinged at
    // `TickRateMultiplier * 2`, with a hard break at 45.4545ms
    // (`WorstAllowedFPS` 22). Nothing per-frame needs re-implementing —
    // FINDINGS 9 enumerates the per-frame manager set and vanilla runs up to
    // 300 ticks/frame against it unchanged.
    public static class TimeDriver
    {
        // Halt reasons the advance result can carry:
        //   ticks | timeout | interrupted        — the caller's own terms
        //   letter | threat | alert | event      — the until: matchers
        //   red_error                            — halt_on_error
        //   dialog                               — a force-pausing modal is up
        //                                          (spec 1.7); vanilla stopped
        //                                          the clock, we report it
        //   stalled                              — 1.8. The game stopped
        //                                          advancing for a reason that
        //                                          is not ours: something else
        //                                          set Paused, or `ForcePaused`
        //                                          held with no modal to name.
        //                                          Reported, never left to time
        //                                          out silently.
        //   exception                            — the driver itself threw
        //                                          (a failure result, not this)
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
        private static int startTick;
        private static long startSeq;
        private static DateTime startWall;

        // Speed resolution (1.8).
        private static TimeSpeed requestedSpeed;   // the caller's ask, pre-ceilings
        private static TimeSpeed baseSpeed;        // post-ceiling, pre-thermal
        private static TimeSpeed activeSpeed;      // what is actually set right now
        private static string speedSource;         // "speed" | "max_tps" | "default"
        private static int askedMaxTps;            // -1 when the caller gave none
        private static Dictionary<string, object> speedClamp; // null when nothing moved
        private static bool thermalStepped;
        private static readonly List<object> speedChanges = new List<object>();
        private static readonly List<object> speedRefusals = new List<object>();

        // Progress + the stall watchdog. `advance` no longer drives the clock,
        // so "the clock is not moving" is a state it can now be IN, and
        // timeout_ticks is measured in game ticks — a stalled game would never
        // reach it. The watchdog is what keeps "advance always returns" true.
        private static int lastSeenTick;
        private static int stallFrames;
        private static int maxTicksInFrame;
        private static Dictionary<string, object> stallInfo;

        // The pause debt. `Pause()`/`CurTimeSpeed` route through
        // `PlayerCanControl` and silently no-op when the player cannot control
        // time; the game-boundary abandon runs on either thread and may not
        // touch Verse at all. Both hand the pause forward to the next
        // main-thread frame rather than dropping it.
        private static volatile bool pendingPause;
        private static bool pauseRefusedAtExit;

        // Halt handshake: Notice() (any thread) sets these; FrameStep polls.
        private static volatile bool haltFlag;
        private static string haltReason;
        private static Dictionary<string, object> haltEvent;
        private static long haltSeq;

        private static bool slowerNow;
        private static int slowerFromTick;
        private static readonly List<object> slowerSpans = new List<object>();

        // ---- vanilla's speed ladder ---------------------------------------
        //
        // Read off `TickManager.TickRateMultiplier` (decompiled): Normal 1,
        // Fast 3, Superfast 6, Ultrafast 15, against the sim's 60 ticks/second.
        // These are NOMINAL and only nominal:
        //   - Superfast returns 12 (=720 tps) when `NothingHappeningInGame()`
        //     and 18 with no maps;
        //   - Ultrafast returns 150 (=9000 tps) with no maps or when the
        //     private `UltraSpeedBoost` TweakValue is set (we never set it);
        //   - `slower.ForcedNormalSpeed` clamps ANY speed to 1 (=60 tps) for
        //     240–800 ticks after a threat.
        // So measured tps is the only truth (FINDINGS 4c and 6) and the result
        // reports measured, not nominal, throughput.
        public static int NominalTps(TimeSpeed s)
        {
            switch (s)
            {
                case TimeSpeed.Normal: return 60;
                case TimeSpeed.Fast: return 180;
                case TimeSpeed.Superfast: return 360;
                case TimeSpeed.Ultrafast: return 900;
                default: return 0;
            }
        }

        // The overshoot bound. `TickManagerUpdate` runs at most
        // `TickRateMultiplier * 2` ticks per frame and our stop check is once
        // per frame, after those ticks — so an advance can run at most one
        // frame's worth of ticks past its target. Superfast is quoted at its
        // BOOSTED multiplier (12), because `NothingHappeningInGame()` can flip
        // mid-advance and the bound has to hold when it does.
        public static int MaxTicksPerFrame(TimeSpeed s)
        {
            switch (s)
            {
                case TimeSpeed.Normal: return 2;
                case TimeSpeed.Fast: return 6;
                case TimeSpeed.Superfast: return 24;
                case TimeSpeed.Ultrafast: return 30;
                default: return 0;
            }
        }

        // Names only — deliberately not Enum.TryParse, which would accept
        // "Paused" and the bare ordinals ("3" => Superfast) and hand the caller
        // a silent misfire. The caller is a program; coercing its mistakes
        // hides them.
        public static bool TryParseSpeed(string s, out TimeSpeed speed)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "normal": speed = TimeSpeed.Normal; return true;
                case "fast": speed = TimeSpeed.Fast; return true;
                case "superfast": speed = TimeSpeed.Superfast; return true;
                case "ultrafast": speed = TimeSpeed.Ultrafast; return true;
                default: speed = TimeSpeed.Paused; return false;
            }
        }

        // max_tps is a CEILING — DESIGN calls it "a hard thermal cap … enforced
        // regardless of what the caller asks for" — so it rounds DOWN the
        // ladder and never up: 800 selects Superfast (360), not Ultrafast's
        // 900. Below 60 there is nothing slower than Normal, and that is
        // REPORTED (max_tps_clamped by:"floor") rather than silently honoured.
        private static TimeSpeed SpeedForTps(int tps)
        {
            if (tps >= 900) return TimeSpeed.Ultrafast;
            if (tps >= 360) return TimeSpeed.Superfast;
            if (tps >= 180) return TimeSpeed.Fast;
            return TimeSpeed.Normal;
        }

        // TimeSpeed's declaration order IS its speed order (Paused, Normal,
        // Fast, Superfast, Ultrafast), so the slower of two is the smaller.
        private static TimeSpeed Slower(TimeSpeed a, TimeSpeed b) => a < b ? a : b;

        private static TimeSpeed OneNotchDown(TimeSpeed s)
            => s <= TimeSpeed.Normal ? TimeSpeed.Normal : (TimeSpeed)((byte)s - 1);

        // EVERY speed change in this file goes through here.
        //
        // `TickManager.CurTimeSpeed`'s setter tests `PlayerCanControl` and, when
        // the player cannot control time, shows a `Messages.Message` and LEAVES
        // THE SPEED ALONE — no exception, no return value, nothing at the call
        // site. `PlayerCanControl` is false during a gravship landing-area
        // confirmation and whenever `Game.PlayerHasControl` is false, which is
        // any `ScreenFader.IsFading()` and any gravship cutscene
        // (decompiled Verse/TickManager.cs, Verse/Game.cs). 1.3's per-frame
        // re-pinning papered over this by trying again 60 times a second; there
        // is no such cover now, so a refused set is recorded and reported and
        // never swallowed into an advance that looks healthy and never ticks.
        private static bool SetSpeed(TickManager tm, TimeSpeed want, string why)
        {
            tm.CurTimeSpeed = want;
            if (tm.CurTimeSpeed == want)
            {
                activeSpeed = want;
                return true;
            }
            speedRefusals.Add(new Dictionary<string, object>
            {
                ["wanted"] = want.ToString(),
                ["observed"] = tm.CurTimeSpeed.ToString(),
                ["why"] = why,
                ["detail"] = "the CurTimeSpeed setter no-opped: PlayerCanControl is false "
                             + "(screen fade, gravship cutscene, or landing-area confirmation)",
            });
            return false;
        }

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

            // ---- which speed to run at ---------------------------------
            //
            // `speed:` is 1.8's own knob. `max_tps:` is kept, and kept
            // MEANINGFUL, because 1.4's CLI and the committed acceptance
            // scripts pass it: it selects the fastest rung of vanilla's ladder
            // that does not exceed it. When both are given, `speed:` names the
            // ask and `max_tps:` still binds as a ceiling — otherwise `speed:`
            // would be a way around the bench's own cap.
            askedMaxTps = args.Has("max_tps") ? args.Int("max_tps", Config.MaxTpsCap) : -1;
            string speedArg = args.Str("speed");
            if (speedArg != null)
            {
                if (!TryParseSpeed(speedArg, out requestedSpeed))
                    throw new VerbArgsException("speed must be normal|fast|superfast|ultrafast");
                speedSource = "speed";
            }
            else if (askedMaxTps >= 0)
            {
                requestedSpeed = SpeedForTps(askedMaxTps);
                speedSource = "max_tps";
            }
            else
            {
                requestedSpeed = SpeedForTps(Config.MaxTpsCap);
                speedSource = "default";
            }

            baseSpeed = requestedSpeed;
            string clampBy = null;
            if (askedMaxTps >= 0)
            {
                var byAsk = SpeedForTps(askedMaxTps);
                if (byAsk < baseSpeed) { baseSpeed = byAsk; clampBy = "max_tps"; }
            }
            var byCap = SpeedForTps(Config.MaxTpsCap);
            if (byCap < baseSpeed) { baseSpeed = byCap; clampBy = "config-cap"; }

            // The thermal governor's whole remaining job: one notch down while
            // hot. It no longer scales a frame budget and must never
            // reintroduce a continuous rate — the notch is the entire control.
            var want = baseSpeed;
            thermalStepped = ThermalGovernor.StepDown;
            if (thermalStepped)
            {
                var stepped = OneNotchDown(baseSpeed);
                if (stepped != want) { want = stepped; clampBy = "thermal"; }
            }
            speedClamp = clampBy == null ? null : new Dictionary<string, object>
            {
                ["from"] = requestedSpeed.ToString(),
                ["to"] = want.ToString(),
                ["by"] = clampBy,
            };

            // Own the clock: set the speed and VERIFY it took. A refused set is
            // a failure result, not a stalled advance — see SetSpeed.
            speedRefusals.Clear();
            activeSpeed = tm.CurTimeSpeed;
            if (!SetSpeed(tm, want, "advance start"))
                return Result.Fail(command.Id, command.Op, "cannot-set-speed",
                    $"the game refused {want} and stayed at {tm.CurTimeSpeed}: PlayerCanControl is "
                    + "false (screen fade, gravship cutscene, or landing-area confirmation). "
                    + "Nothing was armed; retry when the game hands control back.");

            cmd = command;
            Target = ticks;
            TicksDone = 0;
            startTick = tm.TicksGame;
            lastSeenTick = startTick;
            startSeq = Journal.CurrentSeq;
            startWall = DateTime.UtcNow;
            haltFlag = false;
            haltReason = null;
            haltEvent = null;
            haltSeq = 0;
            stallFrames = 0;
            stallInfo = null;
            maxTicksInFrame = 0;
            pauseRefusedAtExit = false;
            speedChanges.Clear();
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
            // 1.7. Journal.Emit fires this synchronously on the emitting
            // thread, and the dialog hook emits from inside WindowStack.Add —
            // which, for a self-opening letter, is inside the very tick
            // TickManagerUpdate is running. Vanilla stops the clock on its own
            // the moment that window is up (TickManagerUpdate's loop breaks on
            // `Paused`, and `Paused` includes `ForcePaused`); this flag is what
            // turns that stop into a REPORTED halt instead of a silent stall.
            //
            // Unconditional, deliberately: there is no honest "plough on"
            // option while a force-pausing window is up, because
            // OpenAutomaticLetters is dead for as long as it is. See the
            // open-question resolutions on git-bug 8555381.
            if (type == "dialog")
            {
                Halt("dialog", payload, seq);
                return;
            }
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
        //
        // 1.8 adds the pause debt. This exit used to be safe to leave alone
        // because the advance never unpaused anything; now it does, so the
        // colony would carry on playing itself into whatever game comes next.
        // We cannot pause from here (wrong thread, possibly no Game), so we arm
        // pendingPause and the next main-thread FrameStep discharges it.
        public static bool Abandon(string code, string detail)
        {
            // Interlocked because the poller's unload edge and the main
            // thread's lifecycle virtual can both fire for one boundary, and
            // the command owes exactly one result file.
            var c = System.Threading.Interlocked.Exchange(ref cmd, null);
            if (Active) pendingPause = true;
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
            // The pause debt, before anything else and whether or not an
            // advance is running: a PlayerCanControl refusal is transient (the
            // fade ends, the cutscene ends) and an advance that handed back
            // control with the colony still running is the one failure this
            // spec must not ship.
            if (pendingPause) DischargePause();
            if (!Active) return;
            try
            {
                Step();
            }
            catch (Exception e)
            {
                // The body throwing used to cost a frame of ticking. Now it
                // would leave the game RUNNING, so it ends the advance — and
                // Teardown's restore is what actually stops the clock.
                FinishFailed("exception", e.ToString());
            }
        }

        private static void Step()
        {
            var tm = Find.TickManager;

            // Progress comes from the GAME's clock now, not from a counter we
            // increment: RimWorld runs the ticks, so TicksGame is the only
            // honest measure of how many happened.
            int now = tm.TicksGame;
            int ran = now - lastSeenTick;
            if (ran > maxTicksInFrame) maxTicksInFrame = ran;
            lastSeenTick = now;
            TicksDone = now - startTick;

            // TimeSlower spans. Under the budget loop this recorded something
            // we were overriding (the loop never consulted TickRateMultiplier);
            // the clamp genuinely applies now, so a span is a plain observed
            // fact: that stretch ran at ~60 tps because the storyteller said so.
            bool slower = tm.slower.ForcedNormalSpeed;
            if (slower != slowerNow)
            {
                if (slower) slowerFromTick = now;
                else slowerSpans.Add(new List<object> { (double)slowerFromTick, (double)now });
                slowerNow = slower;
            }

            if (haltFlag) { Finish(haltReason); return; }

            // 1.7, and the per-TICK guard is deliberately gone.
            //
            // The old comment here said it plainly: "we pin CurTimeSpeed=Paused
            // and drive DoSingleTick ourselves, so nothing pauses us" — that,
            // and only that, is why the check had to run between every tick.
            // Unpaused, `TickManagerUpdate` early-returns while
            // `WindowStack.WindowsForcePause` is true and its inner loop breaks
            // on `Paused` after each tick, so the game stops itself and
            // `LetterStack.OpenAutomaticLetters` is never starved.
            //
            // A check still earns its place here, per FRAME, for two reasons
            // that are not vanilla's problem:
            //   1. `advance` owes a RESULT. Without this, a modal would simply
            //      freeze the clock and the advance would sit there until its
            //      timeout — the "healthy-looking stall" this spec exists to
            //      remove. (The Notice() tap catches the common case one frame
            //      earlier; this is the backstop for a window whose forcePause
            //      is set after WindowStack.Add, which the tap cannot see.)
            //   2. advance must return with the game paused. Vanilla's stop
            //      leaves `curTimeSpeed` at Ultrafast — the clock resumes the
            //      moment the window closes. Only Teardown makes it Paused.
            var stack = Find.WindowStack;
            if (stack != null && stack.WindowsForcePause)
            {
                Halt("dialog", ForcePausePayload(stack), Journal.CurrentSeq);
                Finish("dialog");
                return;
            }

            if (Target >= 0 && TicksDone >= Target) { Finish("ticks"); return; }
            if (timeoutTicks > 0 && TicksDone >= timeoutTicks) { Finish("timeout"); return; }

            // Speed supervision. NOT re-pinning: we set the speed once and then
            // watch it, because re-asserting it every frame is exactly the
            // behaviour 1.8 removed.
            var cur = tm.CurTimeSpeed;
            if (cur == TimeSpeed.Paused)
            {
                // Something else stopped the clock. This is real and routine on
                // a full-DLC bench: `LetterStack.ReceiveLetter` pauses whenever
                // `Prefs.AutomaticPauseMode >= letter.def.pauseMode`,
                // `TickManager.Notify_GeneratedPotentiallyHostileMap` pauses on
                // a hostile map generation, `CompVoidNode`/`CompCerebrexCore`
                // (Anomaly) pause outright, `Log.Error` pauses under
                // `DebugSettings.pauseOnError`, and a human watching the bench
                // can press space. 1.3 was immune because it re-pinned; we are
                // not, and folding is right — the agent is turn-based and a
                // game that paused itself has said something.
                stallInfo = StallInfo(tm, "external-pause", stack);
                Finish("stalled");
                return;
            }
            if (cur != activeSpeed)
            {
                // An external speed change that is not a pause — a watching
                // human on the speed keys, or a mod. Adopt and report rather
                // than fight: fighting is the re-pinning this spec deleted.
                speedChanges.Add(new Dictionary<string, object>
                {
                    ["tick"] = (double)now,
                    ["from"] = activeSpeed.ToString(),
                    ["to"] = cur.ToString(),
                    ["by"] = "external",
                });
                activeSpeed = cur;
            }

            // Thermal governor: act on the EDGE only, so it does not fight an
            // external change every frame and does not become a rate.
            if (ThermalGovernor.StepDown != thermalStepped)
            {
                thermalStepped = ThermalGovernor.StepDown;
                var want = thermalStepped ? OneNotchDown(baseSpeed) : baseSpeed;
                if (want != activeSpeed)
                {
                    var from = activeSpeed;
                    if (SetSpeed(tm, want, thermalStepped ? "thermal step-down" : "thermal recovery"))
                        speedChanges.Add(new Dictionary<string, object>
                        {
                            ["tick"] = (double)now,
                            ["from"] = from.ToString(),
                            ["to"] = want.ToString(),
                            ["by"] = "thermal",
                        });
                }
            }

            // The stall watchdog. `timeout_ticks` is counted in GAME ticks, so
            // a game that is not ticking can never reach it — an advance would
            // hang forever and status.json would look healthy. Counted in
            // FRAMES rather than wall seconds, deliberately: `Root_Play.Update`
            // returns early while `LongEventHandler.ShouldWaitForEvent`, so an
            // autosave or a map generation simply stops calling us and costs no
            // frames at all. Only a game that is rendering and not ticking
            // accumulates here.
            if (ran > 0) stallFrames = 0;
            else if (++stallFrames >= Config.StallFrames)
            {
                stallInfo = StallInfo(tm, SafeForcePaused(tm) ? "force-paused" : "no-progress", stack);
                Finish("stalled");
            }
        }

        // What stopped the clock, in the caller's terms. `force-paused` is
        // `TickManager.ForcePaused` with no force-pausing WINDOW to name it:
        // `LongEventHandler.ForcePause`, `Find.TilePicker.Active`, a gravship
        // cutscene or landing-area confirmation, or `MapGenerator.debugMode`.
        private static Dictionary<string, object> StallInfo(TickManager tm, string cause, WindowStack stack)
        {
            var d = new Dictionary<string, object>
            {
                ["cause"] = cause,
                ["time_speed"] = tm.CurTimeSpeed.ToString(),
                ["expected_speed"] = activeSpeed.ToString(),
                ["frames_without_progress"] = (double)stallFrames,
            };
            try { d["paused"] = tm.Paused; } catch { }
            try { d["force_paused"] = SafeForcePaused(tm); } catch { }
            try { if (stack != null) d["windows_force_pause"] = stack.WindowsForcePause; } catch { }
            return d;
        }

        private static bool SafeForcePaused(TickManager tm)
        {
            try { return tm.ForcePaused; } catch { return false; }
        }

        // Every exit claims `cmd` with the same interlocked exchange, so a game
        // boundary that already answered the command (Abandon, from either
        // thread) can never produce a second result file for it.
        //
        // Teardown runs BEFORE BuildData, unlike 1.3's order: the result has to
        // report whether the pause actually took, and the open TimeSlower span
        // has to be closed before the list is copied (1.3 built the data first
        // and dropped a span that was still open at the halt).
        private static void Finish(string reason)
        {
            var c = System.Threading.Interlocked.Exchange(ref cmd, null);
            Teardown();
            if (c == null) return;
            Runtime.Outgoing.Enqueue(Result.Success(c.Id, c.Op, BuildData(reason)));
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
            // Cleared first: Journal.Emit fires Notice synchronously, and
            // RestorePause can log a warning, so nothing below may re-enter a
            // driver that is on its way out.
            Active = false;
            ActiveId = null;
            try
            {
                if (slowerNow)
                {
                    slowerSpans.Add(new List<object> { (double)slowerFromTick, (double)Find.TickManager.TicksGame });
                    slowerNow = false;
                }
            }
            catch { slowerNow = false; }
            pauseRefusedAtExit = !RestorePause();
            System.Threading.Interlocked.Exchange(ref cmd, null);
        }

        // advance ALWAYS returns paused. Under 1.3 that was nearly free — the
        // loop never unpaused — and the call was a `try { Pause(); } catch { }`
        // whose result nobody looked at. It is load-bearing now, so it is
        // set-then-VERIFY like every other speed change, and a refusal arms the
        // debt instead of being swallowed.
        private static bool RestorePause()
        {
            try
            {
                var tm = Find.TickManager;
                if (tm.CurTimeSpeed == TimeSpeed.Paused) { pendingPause = false; return true; }
                tm.Pause();
                if (tm.CurTimeSpeed == TimeSpeed.Paused) { pendingPause = false; return true; }
                pendingPause = true;
                Log.Warning("[AutoRimmer] advance could not pause the game on exit (PlayerCanControl "
                            + "refused; speed is " + tm.CurTimeSpeed + "). Retrying every frame.");
                return false;
            }
            catch
            {
                pendingPause = true;
                return false;
            }
        }

        // Main thread, from FrameStep, until it takes.
        private static void DischargePause()
        {
            try
            {
                var tm = Find.TickManager;
                if (tm == null) return;
                if (tm.CurTimeSpeed == TimeSpeed.Paused) { pendingPause = false; return; }
                tm.Pause();
                if (tm.CurTimeSpeed != TimeSpeed.Paused) return;
                pendingPause = false;
                Log.Warning("[AutoRimmer] deferred pause taken: the game is paused again.");
            }
            catch { }
        }

        private static Dictionary<string, object> BuildData(string reason)
        {
            var tm = Find.TickManager;
            double wall = (DateTime.UtcNow - startWall).TotalSeconds;
            long endSeq = Journal.CurrentSeq;
            int ticks = TicksDone;
            int effTps = NominalTps(activeSpeed);
            var data = new Dictionary<string, object>
            {
                ["reason"] = reason,
                ["tick"] = tm.TicksGame,
                ["ticks_elapsed"] = ticks,
                ["wall_seconds"] = wall,
                ["avg_tps"] = wall > 0 ? ticks / wall : 0,

                // 1.8: which of vanilla's speeds actually ran, what was asked
                // for, and where the number came from. `speed_nominal_tps` is
                // arithmetic (60 x TickRateMultiplier); `avg_tps` above is
                // measured, and the two differ legitimately — TimeSlower clamps
                // to 60 after a threat and Superfast doubles to 720 when
                // NothingHappeningInGame(). Only the measured one is truth.
                ["speed"] = activeSpeed.ToString(),
                ["speed_requested"] = requestedSpeed.ToString(),
                ["speed_source"] = speedSource,
                ["speed_nominal_tps"] = effTps,
                // Kept under its 1.3 name because 1.4's CLI and the acceptance
                // scripts read it. It now means "the nominal tps of the speed
                // that ran", which is the same thing it always claimed to be.
                ["max_tps_effective"] = effTps,

                // Overshoot is REPORTED, never promised away: the stop check is
                // per frame and vanilla runs up to TickRateMultiplier*2 ticks
                // per frame, so `advance {ticks:N}` lands at N or a little past.
                ["max_ticks_in_frame"] = maxTicksInFrame,
                ["overshoot_bound"] = MaxTicksPerFrame(activeSpeed),

                // The turn-based contract, stated every time rather than only
                // when it fails. False here means the colony is still running.
                ["paused_on_exit"] = PausedOnExit(),

                // empty list when nothing was journaled during the advance
                ["journal_seq"] = endSeq > startSeq
                    ? new List<object> { (double)(startSeq + 1), (double)endSeq }
                    : new List<object>(),
                ["slower_spans"] = new List<object>(slowerSpans),
            };
            if (Target >= 0) data["overshoot"] = Math.Max(0, ticks - Target);
            if (speedClamp != null) data["speed_clamped"] = speedClamp;
            // Present ONLY when the caller's number was moved, so silence means
            // "you got what you asked for". A floor nobody documents is a floor
            // nobody can debug (1.5 nit).
            if (askedMaxTps >= 0)
            {
                data["max_tps_asked"] = askedMaxTps;
                if (effTps != askedMaxTps)
                    data["max_tps_clamped"] = new Dictionary<string, object>
                    {
                        ["asked"] = askedMaxTps,
                        ["to"] = effTps,
                        // "floor": nothing in vanilla runs slower than Normal's
                        // 60 tps, so a smaller cap cannot be honoured at all.
                        // "cap": Config.MaxTpsCap bound it. "thermal": the
                        // governor stepped it. "speed-step": the ask fell
                        // between rungs of the ladder and rounded down.
                        ["by"] = askedMaxTps < 60 ? "floor"
                               : askedMaxTps > Config.MaxTpsCap ? "cap"
                               : thermalStepped ? "thermal" : "speed-step",
                    };
            }
            if (speedChanges.Count > 0) data["speed_changes"] = new List<object>(speedChanges);
            if (speedRefusals.Count > 0) data["speed_set_refused"] = new List<object>(speedRefusals);
            if (pauseRefusedAtExit)
                data["pause_refused"] = "the game refused Paused on exit; AutoRimmer retries every "
                                      + "frame until it takes (see the mod log)";
            if (stallInfo != null) data["stalled"] = stallInfo;
            // 1.7 standing invariant, the same shape as "advance always returns
            // paused": advance returns with an EMPTY force-pause stack, or it
            // says so here. Computed live at Finish, not copied from the halt,
            // so it is true of the moment the caller is being handed — a window
            // that went up during the final frame shows up even when the halt
            // reason is something else entirely.
            try
            {
                var stack = Find.WindowStack;
                if (stack != null && stack.WindowsForcePause)
                    data["force_pause_windows"] = ForcePausePayload(stack);
            }
            catch { }
            if (haltEvent != null)
            {
                data["halted_on"] = haltEvent;
                data["halted_seq"] = (double)haltSeq;
            }
            if (ThermalGovernor.Available)
            {
                data["thermal_c"] = ThermalGovernor.TempC;
                data["thermal_step_down"] = ThermalGovernor.StepDown;
            }
            return data;
        }

        // `CurTimeSpeed`, not `TickManager.Paused`: `Paused` is also true from a
        // force-pausing window alone, which would report success while the game
        // is still set to Ultrafast and would resume the instant that window
        // closed. The distinction is the whole point of the 1.7 path.
        private static bool PausedOnExit()
        {
            try { return Find.TickManager.CurTimeSpeed == TimeSpeed.Paused; }
            catch { return false; }
        }

        // ---- 1.7: force-pausing modals ------------------------------------
        //
        // Read-only throughout. Nothing here closes, focuses or reorders a
        // window: deciding what a dialog MEANS is 3.5's job, and this spec's
        // job is to stop, report, and not corrupt the letter queue.

        // Main thread. Names every force-pausing window currently up, so the
        // caller (and 3.5, and rwtest) can route on it. `type` is the short
        // class name — the assertable identifier — and `type_full` separates a
        // modded window from a vanilla one that happens to share it.
        public static Dictionary<string, object> ForcePausePayload(WindowStack stack)
        {
            var windows = new List<object>();
            try
            {
                if (stack != null)
                {
                    var list = stack.Windows;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var w = list[i];
                        if (w == null || !w.forcePause) continue;
                        windows.Add(DescribeWindow(w));
                    }
                }
            }
            catch { }
            var payload = new Dictionary<string, object>
            {
                ["count"] = (double)windows.Count,
                ["windows"] = windows,
            };
            // What decision is actually owed. The window type says a dialog is
            // up; the letter stack says which one, which is what 3.5 needs.
            try
            {
                var letters = Find.LetterStack?.LettersListForReading;
                if (letters != null && letters.Count > 0)
                {
                    var labels = new List<object>();
                    for (int i = 0; i < letters.Count && i < 10; i++)
                        labels.Add(letters[i]?.Label.ToString());
                    payload["letters"] = labels;
                }
            }
            catch { }
            return payload;
        }

        private static Dictionary<string, object> DescribeWindow(Window w)
        {
            var d = new Dictionary<string, object>();
            var t = w.GetType();
            d["type"] = t.Name;
            d["type_full"] = t.FullName;
            try { if (!string.IsNullOrEmpty(w.optionalTitle)) d["title"] = w.optionalTitle; }
            catch { }
            try { d["layer"] = w.layer.ToString(); }
            catch { }
            return d;
        }

        // The journal side of 1.7: a modal going up mid-advance used to leave
        // no trace at all. WindowStack.Add calls PreOpen() before this runs, so
        // a window that sets forcePause there is caught too.
        //
        // It is journaled whether or not an advance is running — a dialog is a
        // first-class event either way — and because Journal.Emit fires
        // TimeDriver.Notice synchronously, this is also the fast halt path.
        [HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add))]
        public static class Patch_WindowAdd
        {
            public static void Postfix(WindowStack __instance, Window window)
            {
                try
                {
                    if (window == null || !window.forcePause) return;
                    if (Current.ProgramState != ProgramState.Playing) return;
                    var payload = ForcePausePayload(__instance);
                    payload["opened"] = DescribeWindow(window);
                    int tick;
                    try { tick = Find.TickManager.TicksGame; }
                    catch { tick = Runtime.GameState.tick; }
                    Journal.Emit("dialog", payload, tick);
                }
                catch { }
            }
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
