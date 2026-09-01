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
        //   letter | alert                       — 280fb78. THE WAKE, and it is
        //                                          NOT opt-in: every letter and
        //                                          every `alert_on` stops an
        //                                          advance whether or not the
        //                                          caller asked. `until:{letter}`
        //                                          and `until:{alert}` still work
        //                                          as explicit WAITS and win the
        //                                          naming when both would fire —
        //                                          see Notice(), and
        //                                          `halted_on.armed_by` is which
        //                                          one it was.
        //   threat | event                       — the remaining until: matchers,
        //                                          still opt-in: both are
        //                                          NARROWINGS of something the
        //                                          wake already covers (a threat
        //                                          letter is a letter) or of the
        //                                          journal at large.
        //   condition | layout                   — 1.6. A halt on STATE rather
        //                                          than on an event: a
        //                                          predicate over the digest's
        //                                          own field set, or "every
        //                                          element of this layout is
        //                                          resolved". See StateWatch.cs
        //                                          for why the second is a
        //                                          named family and not a path.
        //   casualty                             — 722c951. An OWN-FACTION pawn
        //                                          went down or died while time
        //                                          ran. Not a matcher and not
        //                                          opt-in: default-on, like
        //                                          `dialog`, because "the pawn
        //                                          you were told about died
        //                                          during the next advance" is
        //                                          the M1 failure verbatim.
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
        // `State` covers both 1.6 matchers; which one it is, and everything
        // about how it is evaluated, belongs to the StateWatch — so a third
        // family (research, a bill, a plant's growth) is a subclass and one
        // parse branch, and nothing here has to learn what it means.
        private enum Until { None, Ticks, Letter, Threat, Alert, Event, State }

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
        // "caller" | "default" | "none" — see the ruling at the read site.
        private static string timeoutSource;

        // git-bug 1113019. ONE IN-GAME DAY, applied to any `until` advance that
        // supplies no `timeout_ticks` of its own.
        //
        // NOT a `Config` knob, deliberately. Everything in `Config` is a
        // MACHINE-shaped tunable — scan cadences, the tps ceiling, the stall
        // watchdog's frame count, the thermal sensor — and its header says the
        // file "exists for bench tuning, not play". This number is a statement
        // about the PLAY LOOP: how long the agent may sleep when nothing is
        // asking for it. A bench that could set it to 0 would spell the exact
        // unbounded advance this constant exists to remove.
        public const int DefaultUntilTimeoutTicks = 60000;

        // ==================================================== git-bug 722c951 ==
        // THE READ OBLIGATION, and it is created BY AN ADVANCE.
        //
        // `Journal.CurrentSeq` at the moment the LAST advance handed back
        // control — i.e. exactly the top of the `journal_seq:[a,b]` that advance
        // published. The next `advance` refuses while
        // `Journal.ReadWatermark < lastAdvanceEndSeq`.
        //
        // WHY THE WINDOW IS THE PREVIOUS ADVANCE'S DELTA and not "any unread
        // event at all". The issue's own question is "has this client read the
        // journal SINCE THE LAST ADVANCE", and the two readings differ sharply
        // in practice:
        //
        //   * Events emitted while TIME RAN are news the caller did not see.
        //     Nobody was watching; that is what an advance IS. m1-20260831 step
        //     148 produced seqs 125..128 announcing Table's downing, the run
        //     read none of them, and advanced five more times.
        //   * Events emitted while the agent is AT THE WHEEL — the game is
        //     paused, the loop is reading and acting — are things it did or
        //     asked for, and it got a result envelope for each. The play loop is
        //     read -> think -> ACT -> advance, and every mutating verb journals
        //     an `action` row, so blocking on those would charge a `journal`
        //     round trip to every turn that acted. That is friction with no
        //     safety in it, and friction is what drives a run to leave the
        //     escape hatch switched on — the one outcome this issue must not
        //     produce.
        //
        // The narrower window is also strictly enough for the failure being
        // fixed: every event in the M1 sequence was produced inside an advance.
        // And it is still bookkeeping with no judgement in it — two longs, no
        // allow-list of "interesting" event types, which is the shape that would
        // have had to GUESS that `downed` mattered.
        //
        // Consequences worth stating rather than discovering:
        //   * The FIRST advance of a session is never refused. Nothing has run
        //     unobserved yet, so the session `boot` event is not an obligation.
        //   * An advance that journaled NOTHING creates no obligation, so a
        //     quiet colony never pays for this at all.
        //   * `unread_ok` DOES NOT DISCHARGE THE OBLIGATION. It bypasses the
        //     refusal for ONE call and leaves the watermark exactly where it
        //     was, so the next advance refuses again unless it reads or escapes
        //     again — and every escape writes its own journal row. Riding past a
        //     delta for three in-game days therefore costs three journaled
        //     admissions rather than one flag, which is the whole difference
        //     between a per-call escape and a mode. Only `journal` clears it.
        private static long lastAdvanceEndSeq;

        // The THREE per-call escapes, each a REQUIRED non-empty reason string.
        // Null when not passed. Session 13's `threat-pardon` precedent: the
        // decision must be a recorded ACT, not a silent exemption — so these are
        // journaled at arm time and echoed in the result envelope, and a
        // post-mortem can grep either.
        private static string unreadOk;
        private static string throughCasualties;
        private static bool haltOnCasualty;
        // 280fb78's escape. A THIRD argument and deliberately not an extension
        // of `through_casualties`, because they are two different decisions and
        // one reason string cannot honestly cover both: `through_casualties`
        // says "my colonists may fall while this runs and I accept that",
        // `through_news` says "do not wake me for things I might act on". A
        // post-mortem grepping for who accepted casualties must not turn up
        // every run that only wanted to sleep through a trade caravan. They are
        // also asymmetric in shape — one bypasses an ARM-TIME refusal, the
        // other suppresses a DURING-ADVANCE halt — so folding them would fold
        // two mechanisms as well as two meanings.
        private static string throughNews;
        private static bool haltOnNews;
        // What the wake WOULD have stopped for. An escape that hides the count
        // it bypassed is a silent bypass with a reason string stapled on — the
        // same argument the `bypassed` block above is built on — and a muted
        // alert that fires during an advance is a standing decision doing its
        // work, which the agent should be able to see it doing. Capped, with
        // the count kept whole, because a three-day advance through a siege
        // must not return a thousand rows.
        private const int NewsLogCap = 20;
        private static readonly object newsLock = new object();
        private static readonly List<object> rodePast = new List<object>();
        private static readonly List<object> mutedSeen = new List<object>();
        private static int rodePastCount;
        private static int mutedSeenCount;
        // What the refusal WOULD have said, kept for the result envelope when an
        // escape overrode it. An escape that hides the number it bypassed is a
        // silent bypass with a reason string stapled on.
        private static Dictionary<string, object> bypassed;
        // 1.6. Non-null only for until:{condition|layout}; polled from Step on
        // its own frame cadence, never from Notice (wrong thread).
        private static StateWatch watch;
        private static int watchFrames;
        private static int framesSeen;
        private static int startTick;
        private static long startSeq;
        private static DateTime startWall;

        // Speed resolution (1.8).
        private static TimeSpeed requestedSpeed;   // the caller's ask, pre-ceilings
        private static TimeSpeed baseSpeed;        // post-ceiling, pre-thermal
        private static TimeSpeed activeSpeed;      // what is actually set right now
        // The FASTEST speed this advance ever ran at. M1 finding K
        // (2026-09-01): `overshoot_bound` was computed from `activeSpeed`, the
        // speed the advance happened to EXIT at, so a 192-tick advance that ran
        // at Ultrafast and exited at Superfast (thermal step-down, TimeSlower, or
        // a human on the speed keys) published a bound of 24 next to a measured
        // overshoot of 30. The bound is a property of the whole advance, not of
        // its last frame — the overrun happened while the game was fast, and the
        // reader gets that number afterwards. The CAP is not affected and is not
        // touched: 20/20 advances came in under the real bound (max 29 vs 30).
        private static TimeSpeed fastestSpeed;
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
        //
        // For the same reason the ARGUMENT must be the fastest speed the advance
        // ran at, never the speed it exited at: the bound has to hold over the
        // whole advance, and a step-down after the overrun does not retroactively
        // shrink it. Callers pass `fastestSpeed` (M1 finding K).
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

        // EVERY write to activeSpeed goes through here, so the high-water mark
        // cannot be missed by a later edit that adds a fourth assignment. Same
        // enum-order fact as Slower: the faster of two is the larger.
        private static void NoteActive(TimeSpeed s)
        {
            activeSpeed = s;
            if (s > fastestSpeed) fastestSpeed = s;
        }

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
                NoteActive(want);
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
            watch = null;
            if (untilObj != null)
            {
                if (!(untilObj is Dictionary<string, object> u))
                    throw new VerbArgsException("'until' must be an object");
                // ONE MATCHER, AND NO UNKNOWN KEYS. The parse was a ContainsKey
                // else-if chain, so a second matcher was silently outranked by
                // whichever came first in this method and a misspelled one was
                // silently ignored — `until:{conditon:{…}}` would have armed
                // nothing and run to its timeout. That is the same class as
                // `construction --layout_id` answering whole-map (git-bug
                // 36999fd, 7382bdd), and 1.6 is the round that stops paying it.
                CheckUntilKeys(u);
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
                else
                {
                    watch = StateWatch.Parse(u);
                    if (watch == null)
                        throw new VerbArgsException(
                            "until needs one of: letter, threat, alert, event (journal taps), "
                            + "condition (a predicate over a digest path), layout (every element "
                            + "of a place-layout transaction resolved)");
                    until = Until.State;
                }
            }

            haltOnError = args.Bool("halt_on_error", true);

            // ==================================================== git-bug 1113019 ==
            // THE DEFAULT BOUND, AND WHAT THE NUMBER MEANS.
            //
            // An `until` advance that supplies no `timeout_ticks` gets
            // `DefaultUntilTimeoutTicks` — ONE IN-GAME DAY. This replaces a
            // 600000 (ten in-game days) that had been here since 1.3 and was
            // never a decision: it was "big enough not to get in the way",
            // which is the same as no bound at all for anything a human would
            // notice.
            //
            // Evan's ruling, 2026-09-01, and it is a reframing rather than a
            // number: "a full day without doing anything while you're fully set
            // is pretty typical. Lots of things the colony does itself day to
            // day and ideally if something bad happens, you'll be woken up, you
            // won't have to check."
            //
            // So this is NOT a safety net bolted onto an error path. A quiet day
            // is the NORMAL idle unit of the play loop — a set-up colony runs
            // itself and the agent is supposed to be able to say "advance" and
            // go away — and an advance that runs a full day and returns is the
            // system working, not a wedge being cut short. The consequence is
            // that the HALTS are the wake-up mechanism and this number is
            // merely "eventually return control even when nothing interesting
            // happened". 722c951's own-faction casualty halt is therefore the
            // primary interrupt, not a guard rail; and the open question this
            // bound puts weight on — what ELSE should wake the agent (a raid
            // letter, a breakdown, a mood collapse, a food cliff) — is its own
            // issue, because a day of quiet is only safe to sleep through if
            // the halt set is good enough.
            //
            // THAT ISSUE IS `280fb78` AND IT IS ANSWERED IN THIS FILE (see
            // Notice()'s wake block): every letter and every `alert_on` now
            // halts unconditionally. The two were ruled together in one
            // conversation and each is the other's precondition — a day-long
            // default is only safe because the halts wake you, and the halts
            // are only affordable because a bound stops a quiet day running
            // forever. Neither should be reverted without the other.
            //
            // `timeoutSource` distinguishes the caller's own bound from ours.
            // It is ONE field with three values rather than a bound plus a
            // boolean, for the same reason 1113019 requires the refusal below to
            // be derived from `true_when_armed`: two fields carrying one fact
            // can disagree, and one cannot.
            bool timeoutGiven = args.Has("timeout_ticks");
            timeoutTicks = args.Int("timeout_ticks",
                                    until == Until.Ticks ? 0 : DefaultUntilTimeoutTicks);
            timeoutSource = timeoutGiven ? "caller"
                          : until == Until.Ticks ? "none"
                          : "default";

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

            // ---- 722c951 / 40ed42f#3: the two refusals ---------------------
            //
            // HERE, and the position is load-bearing in BOTH directions.
            //
            // AFTER every other argument has been READ, because `VerbArgs` now
            // keeps a read log and reports `supplied - queried` as
            // `ignored_args` (git-bug 7382bdd). Returning from above the speed
            // block would leave `speed`/`max_tps` unread on a refused advance
            // and make the caller's own arguments look ignored — a refusal that
            // manufactures a second, false complaint.
            //
            // BEFORE `SetSpeed`, because a refusal must leave the clock exactly
            // where it found it: no ticks ran, nothing was armed, and `cmd` is
            // not claimed yet, so there is nothing to unwind and no risk of a
            // second result for one command.
            //
            // The escapes are parsed here too, and REFUSED rather than coerced:
            // a present-but-empty reason is the shape of a bypass somebody added
            // to get a suite green, and it must not be spellable. `VerbArgs.Str`
            // already throws for a non-string. They need no registration
            // anywhere — reading them through this same `VerbArgs` instance is
            // what marks them read, which is the whole design of 7382bdd.
            unreadOk = args.Has("unread_ok") ? Reason(args, "unread_ok") : null;
            throughCasualties = args.Has("through_casualties") ? Reason(args, "through_casualties") : null;
            haltOnCasualty = throughCasualties == null;
            // 280fb78. Read HERE with the other two so the read log sees it on
            // a refused advance too (the block header above), even though what
            // it suppresses happens later, inside Notice().
            throughNews = args.Has("through_news") ? Reason(args, "through_news") : null;
            haltOnNews = throughNews == null;
            bypassed = null;

            // REFUSAL 1 — the unread journal delta. Cheapest check first,
            // deliberately: it is two longs plus (only when it fires) one ring
            // walk, whereas refusal 2 can cost a pathfind. Doing the cheap one
            // first means a blind advance never pays for the deadline check —
            // and the unread refusal's own type breakdown names the `downed`
            // that refusal 2 is about, so the caller is pointed at the casualty
            // either way.
            long watermark = Journal.ReadWatermark;
            if (watermark < lastAdvanceEndSeq)
            {
                long n = lastAdvanceEndSeq - watermark;
                long total = Math.Max(0L, Journal.CurrentSeq - watermark);
                var counts = Journal.CountsRange(watermark, lastAdvanceEndSeq, out _, out bool ringTrunc);
                string breakdown = Breakdown(counts);
                var block = new Dictionary<string, object>
                {
                    ["unread"] = (double)n,
                    ["unread_total"] = (double)total,
                    ["read_watermark"] = (double)watermark,
                    ["advance_end_seq"] = (double)lastAdvanceEndSeq,
                    ["seq_from"] = (double)(watermark + 1),
                    ["seq_to"] = (double)lastAdvanceEndSeq,
                    ["types"] = breakdown,
                };
                if (ringTrunc) block["ring_truncated"] = true;
                string detail =
                    $"the previous advance journaled {n} event(s) that no `journal` call has read "
                    + $"(seq {watermark + 1}..{lastAdvanceEndSeq}; types: {breakdown}). "
                    + $"unread={n} unread_total={total} read_watermark={watermark} "
                    + $"advance_end_seq={lastAdvanceEndSeq}. "
                    + "Advancing again now is advancing BLIND: run m1-20260831 lost a colonist to "
                    + "exactly this, when step 148's own result carried journal_seq:[125,128] "
                    + "announcing Table was down and the run advanced five more times while he "
                    + "bled for 11,335 ticks. "
                    + $"Fix: `journal {{since_seq:{watermark}}}`, then advance. "
                    + "Or pass `advance {unread_ok:\"<why>\"}` to proceed anyway — the reason is "
                    + "journaled as an act. It bypasses ONE call and does not move the watermark, "
                    + "so the next advance asks again; only `journal` clears this.";
                if (unreadOk == null)
                    return Result.Fail(command.Id, command.Op, ErrUnreadJournal, detail);
                bypassed = bypassed ?? new Dictionary<string, object>();
                block["reason"] = unreadOk;
                bypassed["unread_journal"] = block;
            }

            // REFUSAL 2 — the bleed clock against the rescue. `triage`'s own
            // row, `triage`'s own verdict: TriageVerbs.BleedoutDeadline is the
            // one path, and its header carries what this costs and when it is
            // skipped (one cached float per colonist when nobody is bleeding;
            // pathfinds only for a bleeder on a finite clock, capped at 3).
            var deadline = PawnActs.BleedoutDeadline(Find.CurrentMap);
            if (deadline != null)
            {
                string detail = DeadlineDetail(deadline);
                if (throughCasualties == null)
                    return Result.Fail(command.Id, command.Op, ErrBleedoutDeadline, detail);
                bypassed = bypassed ?? new Dictionary<string, object>();
                var block = new Dictionary<string, object>
                {
                    ["reason"] = throughCasualties,
                    ["detail"] = detail,
                    ["casualty"] = deadline,
                };
                bypassed["bleedout_deadline"] = block;
            }

            // Own the clock: set the speed and VERIFY it took. A refused set is
            // a failure result, not a stalled advance — see SetSpeed.
            speedRefusals.Clear();
            // fastestSpeed is a per-advance high-water mark, so it is cleared
            // HERE — before the SetSpeed below takes the first reading — and not
            // with the rest of the per-advance state further down, which runs
            // after the speed is set.
            //
            // The PRE-ARM speed is deliberately NOT fed into it: it is a
            // snapshot of what the game was doing before this advance existed
            // (normally Paused, since the agent is turn-based), and counting it
            // would inflate the bound of a slow advance that happened to start
            // while the clock was still running.
            fastestSpeed = TimeSpeed.Paused;
            activeSpeed = tm.CurTimeSpeed;
            if (!SetSpeed(tm, want, "advance start"))
                return Result.Fail(command.Id, command.Op, "cannot-set-speed",
                    $"the game refused {want} and stayed at {tm.CurTimeSpeed}: PlayerCanControl is "
                    + "false (screen fade, gravship cutscene, or landing-area confirmation). "
                    + "Nothing was armed; retry when the game hands control back.");

            // THE ESCAPE IS AN ACT, so it is journaled the moment it is used —
            // present-but-unnecessary included, because a post-mortem asking
            // "was this run driving with the guard rails off" wants every
            // occurrence, not only the ones that changed an outcome. ONE event
            // per advance however many escapes were passed, so
            // `accept/4.2-play-loop.py`'s per-verb journal-vs-transcript tally
            // stays <= 1 line per `advance` op.
            //
            // AFTER the speed took, deliberately: an advance the game itself
            // refused (`cannot-set-speed`) never existed, and a journal row
            // declaring an escape for it would be a confession to a decision
            // nobody got to make.
            if (unreadOk != null || throughCasualties != null || throughNews != null)
            {
                var payload = new Dictionary<string, object>
                {
                    ["verb"] = "advance",
                    ["step"] = "escape",
                    ["id"] = command.Id,
                };
                if (unreadOk != null) payload["unread_ok"] = unreadOk;
                if (throughCasualties != null) payload["through_casualties"] = throughCasualties;
                // NOT in `bypassed` below, and the asymmetry is real rather
                // than an oversight: `bypassed` names ARM-TIME refusals this
                // call overrode, and there is no arm-time refusal for news.
                // What `through_news` actually cost is only knowable at the
                // END, so it is reported there — `news_rode_past` in the
                // result envelope — while this row records that the guard was
                // switched off at all, which is what a grep of `action` rows
                // is asking.
                if (throughNews != null) payload["through_news"] = throughNews;
                var applied = new List<object>();
                if (bypassed != null) foreach (var kv in bypassed) applied.Add(kv.Key);
                payload["bypassed"] = applied;
                if (bypassed != null) payload["detail"] = bypassed;
                Journal.Emit("action", payload, tm.TicksGame);
            }

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
            watchFrames = 0;
            framesSeen = 0;
            lock (newsLock)
            {
                rodePast.Clear();
                mutedSeen.Clear();
                rodePastCount = 0;
                mutedSeenCount = 0;
            }

            // ARM THE PREDICATE LAST, AND EVALUATE IT ONCE.
            //
            // Once, because a path that does not resolve must be a REFUSAL and
            // not an advance that runs to its timeout: "until.condition.path
            // 'resources.food_dayz' — resources has no key 'food_dayz'" is
            // worth more than ten in-game days of silence. And because the
            // edge needs seeding: a predicate already true when it is armed has
            // to be observed FALSE before it may halt, or "advance until dawn"
            // returns instantly at 14:00.
            //
            // A refusal here returns a Result rather than throwing, the way
            // `cannot-set-speed` does — but the clock has already been set by
            // then, so it is put back first. Nothing is armed and nothing is
            // left running.
            if (watch != null)
            {
                var pw = watch as PathWatch;
                string refusal = pw != null ? pw.Arm(Find.CurrentMap, startTick)
                    : watch is LayoutWatch lw ? lw.Arm(startTick)
                    : null;
                if (refusal != null)
                    return UnarmAndFail(command, Err.BadArgs, refusal);

                // ---- 1113019: REFUSAL 3, the halt that cannot happen -------
                //
                // Runs HERE and nowhere else, because it is the first moment
                // the answer exists: `true_when_armed` is what `Arm` just
                // computed, and the whole point of this refusal is that it is
                // DERIVED from the field the envelope already publishes rather
                // than computed a second time. See UnreachableHalt.
                if (pw != null)
                {
                    string unreachable = UnreachableHalt(pw, timeoutGiven);
                    if (unreachable != null)
                        return UnarmAndFail(command, ErrUnreachableHalt, unreachable);
                }
            }

            ActiveId = command.Id;
            Active = true;
            return null;
        }

        // ---- 722c951 / 40ed42f#3: refusal codes, and why `ok:false` ---------
        //
        // `ok:false` IS RIGHT HERE AND `ok:true` IS RIGHT FOR A REFUSED
        // `dev:spawn-thing`, and the difference is not a style preference.
        // Session 13's ruling (RUNLOG, PLAY-LOOP §act): a refusal from a player
        // verb is a WIDGET-GATE ANSWER — "the game will not let this pawn be
        // drafted", "there is nowhere to put that" — an answer ABOUT THE WORLD,
        // which is information, so the envelope succeeded even though the act
        // did not. These two are not answers about the world. They are the mod
        // REFUSING TO ACT: no ticks ran, the clock was never touched, nothing
        // was armed, and the caller's next move must be different from the one
        // it just made. That is a failed command, and a caller that branches on
        // `ok` alone must land in its error path, not read a `data` block and
        // conclude time passed.
        //
        // Both codes are protocol surface (DESIGN §Protocol) and both carry
        // their numbers in `error.detail`, because the envelope carries no
        // `data` on a failure (Poller.BuildResultJson). The detail is written so
        // it can be parsed as well as read: `key=value` tokens for every number,
        // prose for the human.
        public const string ErrUnreadJournal = "unread-journal";
        public const string ErrBleedoutDeadline = "bleedout-deadline";

        // git-bug 1113019. The third refusal, and it is the same class as the
        // two above rather than `bad-args`: every argument is individually
        // well-formed, and THE IDENTICAL CALL IS VALID ON A DIFFERENT WORLD
        // STATE. That is what makes it an answer about the world — the same
        // reason `unread-journal` and `bleedout-deadline` have their own codes
        // — and it is not academic, because the trigger is a RACE: `time.tick
        // >= now + 60` is false when the caller computes it off a `digest` and
        // true by the time the `advance` arms, two protocol round trips later
        // at a 0.25-1 s floor each (rwa/README.md). A caller branching on
        // `bad-args` would conclude its call was malformed, which is the one
        // thing it is not.
        public const string ErrUnreachableHalt = "unreachable-halt";

        // A per-call escape's reason. Required, non-empty, a string — a blank
        // one would let "unread_ok" become a bare boolean by another name.
        private static string Reason(VerbArgs args, string key)
        {
            string s = args.Str(key); // throws bad-args for a non-string
            if (string.IsNullOrWhiteSpace(s))
                throw new VerbArgsException(
                    $"'{key}' must be a non-empty reason string saying WHY this advance may skip "
                    + "the guard. It is journaled as an act (session 13's threat-pardon "
                    + "precedent: the decision is a recorded ACT, not a silent exemption), so a "
                    + "blank one would be a silent bypass with a quote mark on it. "
                    + $"Example: {key}:\"burning 3 days unattended to reach the caravan, "
                    + "casualties accepted\".");
            return s;
        }

        // Every arm-time refusal unwinds the same way, and it is easy to get
        // wrong in a way nothing catches until an advance answers twice.
        //
        // `cmd` was claimed above; release it before returning the failure, or a
        // later exit would enqueue a SECOND result for a command that has
        // already been answered. The clock was set above too, so it is put back:
        // a refusal must leave the colony exactly as it found it, which is the
        // invariant `accept/fc287ba-until-state.py` 0.7a-c assert across every
        // refusal in one go. (The `cannot-set-speed` refusal does not route
        // through here only because it returns before the claim.)
        private static Result UnarmAndFail(PendingCommand command, string code, string detail)
        {
            System.Threading.Interlocked.Exchange(ref cmd, null);
            watch = null;
            RestorePause();
            return Result.Fail(command.Id, command.Op, code, detail);
        }

        // ==================================================== git-bug 1113019 ==
        // "YOU HAVE ASKED FOR A HALT THAT CANNOT HAPPEN." Returns null when it
        // can.
        //
        // WHAT WENT WRONG, from the bench on 2026-09-01. A caller did exactly
        // what this project's rules tell it to — no guessed tick counts, read
        // the clock off the game — and sent
        // `advance {until:{condition:{path:"time.tick", op:">=", value:949}}}`
        // with the map at tick 949. Session 19's rule is that a `condition`
        // requires a false->true EDGE (`time.hour >= 6` is true all afternoon,
        // so "advance until dawn" at 14:00 must not return instantly), and
        // `time.tick >= N` is MONOTONICALLY true once true — there is no second
        // edge, ever. With no `timeout_ticks` the advance had no other exit
        // either. It ran 187,541 ticks, three in-game days, and was stopped only
        // by 722c951's own-faction casualty halt, which had shipped hours
        // earlier. `ok: true`.
        //
        // WHY IT IS DERIVED FROM `TrueWhenArmed` AND `EdgeRequired` AND NOT
        // RECOMPUTED. The mod was never blind to this: session 19 put
        // `true_when_armed`, `saw_false` and `first_false_tick` in the envelope
        // for exactly this case, and that envelope said
        // `true_when_armed: true, saw_false: false, first_false_tick: null`
        // while the advance burned three days. So this is an ENFORCEMENT gap,
        // not an observation one, and a second computation of "is it already
        // true" would be a second thing to keep in sync. Both properties read
        // the same backing fields `PathWatch.Report()` publishes, so the
        // refusal and the envelope cannot disagree (Evan's requirement).
        //
        // AND WHY THE DEFAULT BOUND IS NOT ENOUGH ON ITS OWN. It bounds the
        // damage; it does not answer the caller. A `timeout_ticks` default turns
        // "runs until something else stops it" into "sits for an in-game day and
        // returns having halted on nothing" — better, and still not what was
        // asked. The refusal is what hands back the call the caller meant.
        //
        // AND WHY THE REFUSAL IS NOT ENOUGH ON ITS OWN — the race, again. Refuse
        // with no default and the SAME call fails or succeeds depending on
        // protocol latency, which is a flaky refusal rather than a fix. The two
        // together are what make the outcome well-defined in every case:
        //   - no bound + already true + edge      -> refused, with the fix named
        //   - a bound  + already true + edge      -> session 19, unchanged: it
        //                                            waits for a re-crossing and
        //                                            stops at the caller's bound
        //   - no bound + false at arm (or no edge)-> the 60,000 default applies
        private static string UnreachableHalt(PathWatch pw, bool timeoutGiven)
        {
            if (!pw.TrueWhenArmed || !pw.EdgeRequired) return null;
            // The literal ruling is "no `timeout_ticks` was supplied". A
            // supplied NON-POSITIVE one is folded in because it is the same
            // shape and strictly worse: an unreachable halt with the default
            // bound explicitly switched off. Nothing in the repo spells it; a
            // caller that means "wait indefinitely for a re-crossing" can still
            // pass a large finite number and gets session 19's behaviour.
            if (timeoutGiven && timeoutTicks > 0) return null;
            string bound = timeoutGiven ? timeoutTicks.ToString() : "absent";
            return "the predicate was ALREADY TRUE when this advance armed, `edge` is true (the "
                 + "default), and no positive `timeout_ticks` was given — so this advance has no "
                 + "reachable halt and would run until something else stopped it. "
                 + $"true_when_armed=true edge=true timeout_ticks={bound} "
                 + $"predicate={pw.DescribeAtArm()}. "
                 + "WHY: `until.condition` requires a false->true EDGE by default (DESIGN, "
                 + "session 19: `time.hour >= 6` is true all afternoon, so \"advance until dawn\" "
                 + "issued at 14:00 must not return instantly), and a predicate that is already "
                 + "true has no crossing left to wait for — a monotonic one like `time.tick >= N` "
                 + "never will. On 2026-09-01 this exact call ran 187,541 ticks, three in-game "
                 + "days, and was stopped only by the own-faction casualty halt (git-bug "
                 + "1113019). "
                 + "FIX, and it is almost certainly what you meant: add `edge:false` to the "
                 + "condition — the \"assert now\" reading, \"stop as soon as this holds\" — which "
                 + "halts on the first evaluation. "
                 + "Or keep the edge and bound the wait with `timeout_ticks:N` (an `until` "
                 + $"advance that omits it gets {DefaultUntilTimeoutTicks}, one in-game day). "
                 + "If the value came from a clock read, note that reading the tick and arming "
                 + "the advance are TWO protocol round trips at a 0.25-1 s floor each "
                 + "(rwa/README.md), so at ~30 tps the clock moves 60-120 ticks in between and a "
                 + "short `time.tick >= now + N` lead loses that race.";
        }

        private static string Breakdown(Dictionary<string, int> counts)
        {
            if (counts == null || counts.Count == 0) return "none in the ring";
            var keys = new List<string>(counts.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(keys[i]).Append(' ').Append(counts[keys[i]]);
            }
            return sb.ToString();
        }

        // The refusal sentence for a `too-slow` triage row. Every number comes
        // off the row `triage` itself publishes — nothing is recomputed here,
        // which is what "consume it, do not grow a second path" means in code.
        private static string DeadlineDetail(Dictionary<string, object> row)
        {
            string name = row.TryGetValue("name", out var n) ? n as string : "?";
            object id = row.TryGetValue("pawn", out var i) ? i : null;
            long bleed = 0, margin = 0;
            try
            {
                if (row["clock"] is Dictionary<string, object> clock)
                    bleed = Convert.ToInt64(clock["ticks"]);
                margin = Convert.ToInt64(row["margin_ticks"]);
            }
            catch { }
            long rescue = bleed - margin;   // margin is clock - best total, by construction
            object rescuer = null;
            try
            {
                if (row["act"] is Dictionary<string, object> act
                    && act["args"] is Dictionary<string, object> aa)
                    rescuer = aa["pawn"];
            }
            catch { }
            return $"{name} bleeds out in {bleed} ticks and the nearest capable rescuer needs "
                + $"{rescue} ticks to reach a bed with them — {-margin} ticks short. "
                + $"bleedout_ticks={bleed} rescue_ticks={rescue} margin_ticks={margin} "
                + $"pawn={id} pawn_name={name} rescuer={rescuer} verdict=too-slow. "
                + "Advancing now is a decision to let this pawn die, and the M1 run made it by "
                + "accident: at tick 231,968 a ~9,040-tick bleed clock was answered with a "
                + "work-priority flip whose chosen rescuer stayed asleep for ~6,100 ticks. "
                + "Send the call `triage` already spells out (`rescue {pawn, target}` FORCES the "
                + "job through Pawn_JobTracker.TryTakeOrderedJob and interrupts LayDown), read "
                + "`triage` for the whole row and the other candidates, or pass "
                + "`advance {through_casualties:\"<why>\"}` to make the decision explicitly. "
                + "The travel estimate is a FLOOR (PawnPath.TotalCost excludes door waits, pawn "
                + "collisions and the time to abandon the current job), so the real margin is "
                + "worse than this, not better.";
        }

        // The `until` object's whole vocabulary, in one place. Exactly one
        // matcher; other keys must be known. The journal taps are this file's;
        // the state families and their options come from StateWatch, so adding
        // one does not need an edit here.
        private static readonly string[] JournalMatchers = { "letter", "threat", "alert", "event" };

        private static bool In(string[] set, string key)
        {
            for (int i = 0; i < set.Length; i++) if (set[i] == key) return true;
            return false;
        }

        private static void CheckUntilKeys(Dictionary<string, object> u)
        {
            var found = new List<string>();
            foreach (var kv in u)
            {
                if (In(JournalMatchers, kv.Key) || In(StateWatch.MatcherKeys, kv.Key))
                {
                    found.Add(kv.Key);
                    continue;
                }
                if (In(StateWatch.OptionKeys, kv.Key)) continue;
                throw new VerbArgsException(
                    $"unknown key 'until.{kv.Key}'. Matchers: "
                    + string.Join(", ", JournalMatchers) + ", "
                    + string.Join(", ", StateWatch.MatcherKeys) + ". Other keys: "
                    + string.Join(", ", StateWatch.OptionKeys) + ". Refused rather than ignored: "
                    + "an ignored matcher arms nothing and presents as an advance that ran to its "
                    + "timeout for no stated reason.");
            }
            if (found.Count > 1)
                throw new VerbArgsException(
                    "until takes ONE matcher and was given " + found.Count + " ("
                    + string.Join(", ", found.ToArray()) + "). Two halt conditions in one advance "
                    + "would need a precedence rule, and picking one silently is the bug this "
                    + "check exists to stop. Run two advances, or pick the one that matters.");
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
            // ---- 722c951 #1: the casualty halt --------------------------------
            //
            // DEFAULT-ON AND NOT A MATCHER, exactly like `dialog` above and for
            // the same kind of reason. `until:{event:{type:"downed"}}` has always
            // existed and the M1 run did not use it — the failure was never that
            // the spelling was missing, it was that the DEFAULT let a long
            // advance run straight through a downing and a death without
            // returning control. Post-mortem numbers: 27 advances, zero `journal`
            // calls; Table went down at 214,599 inside a 2,500-tick advance and
            // Captain at 229,014 the same way. Making this opt-in would ship the
            // fix switched off.
            //
            // THE FILTER IS ON FACTION, NOT ON THE EVENT. A hostile going down is
            // the advance working — that is what "advance until the raid is over"
            // looks like from the inside — and halting on it would make every
            // fight a wedge. `payload["player"]` is `Faction.IsPlayer` resolved on
            // the MAIN thread by the hook that emitted it (JournalHooks.StampPawn,
            // and see its header for why `IsColonist` is the wrong test): this tap
            // is documented "any thread" and may not touch Verse to ask for
            // itself.
            //
            // Both transitions, because both are the loss: `downed` is
            // Pawn_HealthTracker.MakeDowned and `death` is SetDead, and a pawn
            // already down when the advance started emits neither — the hooks are
            // on the transition, so this cannot re-fire for the same pawn and
            // cannot wedge.
            if (haltOnCasualty && (type == "downed" || type == "death")
                && payload != null && payload.TryGetValue("player", out var mine)
                && mine is bool isMine && isMine)
            {
                var evt = new Dictionary<string, object>
                {
                    ["kind"] = "casualty",
                    ["event"] = type,
                    ["pawn"] = Str(payload, "pawn"),
                    ["faction"] = Str(payload, "faction"),
                    ["player_faction"] = true,
                    ["tick"] = (double)tick,
                };
                if (payload.TryGetValue("pawn_id", out var pid)) evt["pawn_id"] = pid;
                if (payload.TryGetValue("kind", out var pk)) evt["pawn_kind"] = pk;
                if (payload.TryGetValue("damage", out var dmg)) evt["damage"] = dmg;
                evt["detail"] = Str(payload, "pawn") + " ("
                    + (Str(payload, "faction") ?? "your faction") + ") "
                    + (type == "death" ? "DIED" : "went DOWN") + " at tick " + tick
                    + " — the advance stopped here rather than running on. `triage` has the "
                    + "bleed clock, the rescuers and the exact `rescue` call; "
                    + "`advance {through_casualties:\"<why>\"}` rides past it deliberately.";
                Halt("casualty", evt, seq);
                return;
            }

            // red_error is deliberately NOT matched here — NoticeRedError below
            // owns the halt, upstream of the journal's dedupe cap. An explicit
            // until:{event:{type:"red_error"}} still works through Until.Event,
            // and it is honestly capped: it is a journal-event matcher.
            //
            // ---- THE ASKED-FOR HALT RUNS FIRST (git-bug 280fb78) ------------
            //
            // The matchers are evaluated BEFORE the unconditional wake below,
            // and the order is the whole answer to "`until:{letter}` and
            // `until:{threat}` must keep working and the two must not collide".
            //
            // Both halts fire on the same journal row. What differs is the NAME
            // the caller gets back, and the caller's name has to win: an
            // advance armed `until:{threat}` that stopped on a `ThreatBig`
            // letter must report `reason:"threat"`, because that is the
            // question it asked and the token its caller is branching on.
            // Running the wake first would have renamed every explicit wait to
            // `"letter"` and quietly broken a matcher that has shipped since
            // 1.3 — and it would have done it INVISIBLY, since the advance
            // still stops at the same tick on the same event.
            //
            // Each arm therefore returns rather than breaking, so the wake
            // below sees only rows the caller did not ask about.
            switch (until)
            {
                case Until.Letter:
                    if (type == "letter" && (filterA == null || Str(payload, "def") == filterA))
                    {
                        Halt("letter", WakeEvent("letter", payload, tick, "until"), seq);
                        return;
                    }
                    break;
                case Until.Threat:
                    if (type == "letter")
                    {
                        string def = Str(payload, "def");
                        if (def == "ThreatBig" || def == "ThreatSmall")
                        {
                            Halt("threat", WakeEvent("threat", payload, tick, "until"), seq);
                            return;
                        }
                    }
                    break;
                case Until.Alert:
                    if (type == "alert_on" && (filterA == null || Str(payload, "id") == filterA))
                    {
                        // A MUTE DOES NOT APPLY HERE, deliberately. "Wake me if
                        // this happens" and "wait FOR this to happen" are
                        // different questions, and a caller that names an alert
                        // in `until` has asked the second one this call — which
                        // outranks a standing decision it made on some earlier
                        // day. The mute is consulted only on the wake path.
                        Halt("alert", WakeEvent("alert", payload, tick, "until"), seq);
                        return;
                    }
                    break;
                case Until.Event:
                    if (type == filterA && (filterB == null || PayloadContains(payload, filterB)))
                    {
                        // NOT wrapped in WakeEvent, and this is a real
                        // constraint rather than an omission: `until:{event}`
                        // matches an ARBITRARY journal type, and several
                        // payloads already own the key `kind` — `downed` and
                        // `death` carry `kind` = colonist|slave|animal|mech.
                        // Stamping our own `kind` over it would silently
                        // rewrite the caller's data. The three families above
                        // have fixed, documented payload key sets (JOURNAL.md:
                        // letter {def,label,text,target,faction}, alert_on
                        // {id,label,priority}) and can be stamped safely.
                        Halt("event", payload, seq);
                        return;
                    }
                    break;
            }

            // ================================================ git-bug 280fb78 ==
            // THE WAKE. EVERY LETTER AND EVERY `alert_on`, WHETHER OR NOT THE
            // CALLER ASKED — the same disposition as `casualty` and `dialog`
            // above, and for a sharper version of the same reason.
            //
            // Before this, four halts were unconditional (`casualty`, `dialog`,
            // `red_error`, and `NoticeRedError`'s own path) and EVERYTHING else
            // sat inside the switch above. So `advance {ticks:60000}` — one
            // in-game day — slept through a raid landing, a trade caravan
            // arriving and leaving, a quest expiring, an inspiration expiring,
            // `Alert_LowFood`, a fire, and a prisoner escaping, unless the
            // agent had GUESSED IN ADVANCE that today was the day. A raid at
            // hour 2 was discovered at hour 24, after the colony had fought it
            // alone. That was coherent while advances were short and
            // hand-driven; `1113019` makes an unbounded `until` default to a
            // full in-game day, and the two were ruled together — the day-long
            // default is only safe BECAUSE these halts exist.
            //
            // ---- IT IS NOT A SEVERITY FILTER, AND THAT WAS THE RULING -------
            //
            // The obvious design is a severity cut: wake on ThreatBig /
            // ThreatSmall / Death / NegativeEvent letters and Critical/High
            // alerts, on the grounds of noise. Evan rejected the framing
            // outright (2026-09-01): "anything neutral or positive should wake
            // you, maybe you want to act on an inspiration, things like that.
            // that's how you get propelled into actually playing the game and
            // having fun."
            //
            // The rule is "IS THERE SOMETHING I MIGHT ACT ON", not "is this
            // bad". An inspiration expires if you sleep through it. A trader
            // leaves. A wanderer at the door is a roster decision. A run that
            // only ever wakes for disasters is one that survives ten days
            // without ever playing.
            //
            // Which collapses the letter half to nothing: HALT ON EVERY LETTER,
            // NO FILTER. `Verse/LetterStack.ReceiveLetter` is the game's own
            // "the player should look at this" — it is where vanilla decides
            // whether to pause the game at all
            // (`Prefs.AutomaticPauseMode >= let.def.pauseMode`) — so the
            // filtering has already been done by the one system that is good at
            // it, and re-filtering it here would second-guess it with a list.
            // It also avoids shipping an allow-list, which is a second source
            // of truth this project has been burned by twice (`7382bdd`'s
            // rejected arg whitelist; the `Build:` tally essay in the workspace
            // CLAUDE.md).
            //
            // Noise is not the problem it sounds like. Measured on a bench
            // being actively wrecked by an acceptance suite, 13,667 ticks —
            // about half an in-game day — produced 53 journal events in total,
            // of which 3 were letters and 6 were `alert_on`.
            //
            // ---- ALERTS DIFFER IN ONE WAY, AND THE MUTE IS THE ANSWER -------
            //
            // A letter HAPPENS ONCE. An alert is a STANDING CONDITION, and
            // `alert_on` is already a transition so a chronic one wakes you
            // once per on-cycle rather than continuously. But a condition the
            // colony has deliberately decided not to fix still flickers off and
            // on, and each flicker is a wake for a decision already made. Hence
            // `alert-mute` — runtime, mid-run, a required reason, journaled as
            // an act, and published in `digest.alerts.muted` so it cannot be
            // forgotten. See AlertMuteVerbs.cs for the whole argument.
            if (type == "letter")
            {
                if (!haltOnNews)
                {
                    NoteRodePast("letter", Str(payload, "def"), Str(payload, "label"), tick);
                    return;
                }
                Halt("letter", WakeEvent("letter", payload, tick, "default"), seq);
                return;
            }
            if (type == "alert_on")
            {
                string id = Str(payload, "id");
                // The mute is checked BEFORE the escape so the two are
                // distinguishable in the result: an alert that did not wake
                // this run because of a standing decision is reported as such,
                // rather than being folded into "you rode past 7 things".
                if (AlertMuteComponent.Muted(id))
                {
                    NoteMuted(id, Str(payload, "label"), Str(payload, "priority"), tick);
                    return;
                }
                if (!haltOnNews)
                {
                    NoteRodePast("alert", id, Str(payload, "label"), tick);
                    return;
                }
                Halt("alert", WakeEvent("alert", payload, tick, "default"), seq);
            }
        }

        // The halt event for a journal-tap halt: the journal payload verbatim,
        // plus the three things a caller needs that the payload does not carry.
        //
        //   `kind`      — which halt this was, matching `casualty`/`condition`/
        //                 `layout`, so `halted_on.kind` is answerable for every
        //                 halt that publishes an event at all.
        //   `armed_by`  — "until" when the CALLER asked for this halt,
        //                 "default" when the 280fb78 wake produced it. Present
        //                 on BOTH, never inferred from an absence: this is the
        //                 field that makes "`until:{letter}` still works as an
        //                 explicit wait" a thing a suite can assert rather than
        //                 a thing a reader has to take on trust.
        //   `tick`      — the journal envelope carries it; `halted_on` did not.
        //
        // Any thread — a plain dictionary copy, no Verse. The copy matters:
        // `payload` is the same dictionary the journal writer holds, and
        // stamping keys into it in place would edit a row that has already been
        // emitted.
        private static Dictionary<string, object> WakeEvent(string kind,
            Dictionary<string, object> payload, int tick, string armedBy)
        {
            var evt = new Dictionary<string, object>();
            if (payload != null)
                foreach (var kv in payload) evt[kv.Key] = kv.Value;
            evt["kind"] = kind;
            evt["armed_by"] = armedBy;
            evt["tick"] = (double)tick;
            evt["detail"] = Detail(kind, evt, tick, armedBy);
            return evt;
        }

        private static string Detail(string kind, Dictionary<string, object> evt, int tick,
            string armedBy)
        {
            bool asked = armedBy == "until";
            if (kind == "alert")
                return (Str(evt, "label") ?? Str(evt, "id") ?? "an alert")
                    + " (" + (Str(evt, "id") ?? "?") + ", priority "
                    + (Str(evt, "priority") ?? "?") + ") went ON at tick " + tick
                    + (asked
                        ? " — the alert this advance was waiting for."
                        : " — a standing condition the colony did not have a moment ago, so the "
                          + "advance stopped rather than running on. If this one should stop "
                          + "waking the run, `alert-mute {ids:[\"" + (Str(evt, "id") ?? "?")
                          + "\"], reason:\"<why>\"}` records that decision and `digest.alerts."
                          + "muted` keeps it visible; `advance {through_news:\"<why>\"}` rides "
                          + "past every wake for ONE call.");
            string what = (Str(evt, "label") ?? "a letter")
                + " (" + (Str(evt, "def") ?? "?") + ")";
            if (kind == "threat")
                return what + " arrived at tick " + tick
                    + " — the threat letter this advance was waiting for.";
            return what + " arrived at tick " + tick
                + (asked
                    ? " — the letter this advance was waiting for."
                    : " — the advance stopped here because RimWorld only sends a letter when it "
                      + "thinks the player should look, and that includes the good ones: an "
                      + "inspiration expires, a trader leaves, a wanderer at the door is a "
                      + "roster decision. `journal {since_seq:<n>}` has the full text; "
                      + "`advance {through_news:\"<why>\"}` rides past letters and alerts for "
                      + "ONE call and is journaled as an act.");
        }

        // Any thread. Both logs are bounded lists behind one lock, and the
        // COUNT is kept whole even when the list stops growing — "27 letters,
        // here are the first 20" is honest; a silently truncated list is not.
        private static void NoteRodePast(string kind, string id, string label, int tick)
        {
            lock (newsLock)
            {
                rodePastCount++;
                if (rodePast.Count >= NewsLogCap) return;
                rodePast.Add(new Dictionary<string, object>
                {
                    ["kind"] = kind,
                    ["id"] = id,
                    ["label"] = label,
                    ["tick"] = (double)tick,
                });
            }
        }

        private static void NoteMuted(string id, string label, string priority, int tick)
        {
            lock (newsLock)
            {
                mutedSeenCount++;
                if (mutedSeen.Count >= NewsLogCap) return;
                mutedSeen.Add(new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["label"] = label,
                    ["priority"] = priority,
                    ["tick"] = (double)tick,
                });
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
            // 722c951: the read obligation dies with the game that created it.
            // The command was answered `no-active-game` with NO data, so the
            // caller was never handed a `journal_seq` range and never learned a
            // delta existed; blocking the NEXT colony's first advance on events
            // from a colony that no longer exists is noise, not discipline. The
            // events are still in the file for a post-mortem.
            lastAdvanceEndSeq = 0;
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

            // ---- 1.6: the state predicate --------------------------------
            //
            // HERE, and not in Notice(): that tap is documented "any thread,
            // called synchronously from Journal.Emit", so it may not touch
            // Verse. Step() is the only legal evaluation site, and it is
            // already a state-predicate poll site — the two lines below it
            // poll WindowsForcePause and CurTimeSpeed exactly this way.
            //
            // BEFORE the timeout check, deliberately: on the frame where both
            // are true the caller asked about the predicate, and "your room is
            // finished" beats "your advance ran out of time".
            framesSeen++;
            if (watch != null && ++watchFrames >= watch.EveryFrames)
            {
                watchFrames = 0;
                if (watch.Poll(now, out var evidence))
                {
                    Halt(watch.Reason, evidence, 0);
                    Finish(watch.Reason);
                    return;
                }
            }

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
                NoteActive(cur);
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
            // 722c951: the read obligation this advance just created. HERE and
            // not in Finish, because `FinishFailed` runs ticks too — an advance
            // that died on an exception still let time pass unobserved, and the
            // caller got no `journal_seq` at all for it. Read before
            // `RestorePause` below, whose Log.Warning would itself journal.
            //
            // ONLY IF THIS ADVANCE ACTUALLY PRODUCED EVENTS. `endSeq > startSeq`
            // is the same test `BuildData` uses to decide whether to publish a
            // `journal_seq` range at all, so the obligation and the published
            // delta are the same fact. A silent advance leaves any EARLIER
            // obligation standing — it does not discharge one — and it does not
            // invent one out of events that predate it.
            long endedAt = Journal.CurrentSeq;
            if (endedAt > startSeq) lastAdvanceEndSeq = endedAt;
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

                // 1113019: the bound that was in force, and WHOSE it was.
                // Always present, including on `advance {ticks:N}` — a key that
                // appears only sometimes cannot be asserted, and `eq(…, None)`
                // passes on an absent key.
                //   caller  — the caller passed `timeout_ticks` and got it
                //   default — an `until` advance passed none, so
                //             DefaultUntilTimeoutTicks (one in-game day) was
                //             APPLIED. This is the value the reader needs in
                //             order to tell our bound from its own.
                //   none    — `advance {ticks:N}` with no `timeout_ticks`; the
                //             tick target is the bound and this is 0.
                ["timeout_ticks"] = timeoutTicks,
                ["timeout_source"] = timeoutSource,

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
                // FROM THE FASTEST SPEED THIS ADVANCE RAN AT, not the one it
                // exited at (M1 finding K — see `fastestSpeed`). An advance that
                // ran Ultrafast and was stepped down to Superfast before it
                // stopped can still have overshot by Ultrafast's frame, and it
                // did: 192 ticks, published bound 24, measured overshoot 30.
                ["overshoot_bound"] = MaxTicksPerFrame(fastestSpeed),
                // Named so the bound can be checked rather than trusted, and so
                // a bound that differs from `speed`'s is self-explaining.
                ["overshoot_bound_speed"] = fastestSpeed.ToString(),

                // The turn-based contract, stated every time rather than only
                // when it fails. False here means the colony is still running.
                ["paused_on_exit"] = PausedOnExit(),

                // empty list when nothing was journaled during the advance
                ["journal_seq"] = endSeq > startSeq
                    ? new List<object> { (double)(startSeq + 1), (double)endSeq }
                    : new List<object>(),
                ["slower_spans"] = new List<object>(slowerSpans),

                // 722c951. THIS ECHO IS NOT A READ AND DOES NOT DISCHARGE
                // ANYTHING — see JournalVerbs.Read's header for why, and the M1
                // step 148 that proves it. It is published so the caller knows
                // what its next `journal {since_seq}` should be and can see the
                // obligation it is now carrying.
                //
                // `journal_unread` is what the NEXT advance will refuse on:
                // nonzero here means the next one is blocked until a `journal`
                // call moves the watermark past it.
                // Teardown has already run by the time BuildData is called, so
                // `lastAdvanceEndSeq` is THIS advance's obligation and this
                // subtraction is literally the comparison Start will make next.
                ["journal_read_watermark"] = (double)Journal.ReadWatermark,
                ["journal_unread"] = (double)Math.Max(0L, lastAdvanceEndSeq - Journal.ReadWatermark),
            };
            // The escapes, echoed on the envelope as well as journaled, so a
            // post-mortem reading transcripts alone still sees them and does not
            // have to join to the journal to find out the guard rails were off.
            // Present ONLY when one was passed, so silence means "the defaults
            // ran".
            if (unreadOk != null) data["unread_ok"] = unreadOk;
            if (throughCasualties != null) data["through_casualties"] = throughCasualties;
            if (throughNews != null) data["through_news"] = throughNews;
            if (bypassed != null) data["escaped"] = bypassed;
            // 280fb78. WHAT THE ESCAPE AND THE MUTE LIST ACTUALLY COST, on the
            // envelope, so a transcript-only audit sees it without joining to
            // the journal. Present ONLY when non-empty, so silence means "the
            // wake had nothing to suppress" and never "nobody counted".
            //
            // `muted_alerts` is published REGARDLESS of `through_news`: it is
            // the standing decision doing its work, and an agent that muted
            // `Alert_LowFood` on day 2 should be able to watch it swallow a
            // wake on day 8 rather than infer it from an absence.
            lock (newsLock)
            {
                if (rodePastCount > 0)
                    data["news_rode_past"] = new Dictionary<string, object>
                    {
                        ["count"] = (double)rodePastCount,
                        ["shown"] = (double)rodePast.Count,
                        ["events"] = new List<object>(rodePast),
                    };
                if (mutedSeenCount > 0)
                    data["muted_alerts"] = new Dictionary<string, object>
                    {
                        ["count"] = (double)mutedSeenCount,
                        ["shown"] = (double)mutedSeen.Count,
                        ["events"] = new List<object>(mutedSeen),
                        ["detail"] = "these `alert_on` transitions did NOT stop the advance "
                            + "because `alert-mute` holds a recorded decision for them. "
                            + "`digest.alerts.muted` carries the list and the reasons.",
                    };
            }
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
                // ONLY WHEN IT NAMES A JOURNAL LINE. `halted_seq` is a journal
                // sequence, and a state halt has none — the predicate did not
                // fire because anything was emitted. Publishing 0 would send a
                // caller to `journal --since 0`, so the key is absent instead
                // and `halted_on.kind` says what kind of halt it was.
                if (haltSeq > 0) data["halted_seq"] = (double)haltSeq;
            }
            // 1.6. Published on EVERY exit — a timeout, an interrupt, a dialog
            // — not only on a successful halt. A layout that could not finish
            // has to hand back which elements were outstanding and why, which
            // is the whole reason this beats a fixed-tick advance; and a
            // predicate's measured cost is only interesting when it is
            // reported unconditionally.
            if (watch != null)
            {
                var report = watch.Report();
                report["frames"] = framesSeen;
                report["eval_ms_per_frame"] = framesSeen > 0
                    ? Math.Round((double)(report.TryGetValue("eval_ms_total", out var t)
                        && t is double tv ? tv : 0d) / framesSeen, 5)
                    : 0d;
                data["until"] = report;
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
