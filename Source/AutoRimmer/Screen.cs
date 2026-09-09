using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ============================== THE SCREEN ==============================
    // spec 975973e (first half). COCKPIT.md §"The screen", §"When the clock
    // stops", §"How it stays honest".
    //
    // What a player would be looking at, in six panels, in this order:
    //
    //   1. stop       — why the clock stopped, and how long since you looked
    //   2. map        — where things are          (ee4b4f8; the site line for now)
    //   3. colonists  — who is where, in what state
    //   4. gauges     — the numbers, each from a full count
    //   5. since      — what happened, in three rows: game, human, mod
    //   6. decisions  — each a sentence a person could answer, with its acts
    //
    // THE SCREEN IS THE REPLY. `look` returns it and `advance` returns the
    // same object as `data.screen`, so there is nothing to read first. The
    // reason is the audit's theme T0: the bridge put a truthful `ignored_args`
    // block into 52 result envelopes and the agent read it zero times, while
    // it read the thing it asked for — `halted_on`, a refusal, `triage`'s
    // verdict — every time. The stop line therefore leads, because it is the
    // one line the audit proves the agent read.
    //
    // The blind honesty round sharpened that and this file states the
    // sharpened version rather than the flattering one: making the screen the
    // reply removes the thing the agent has to remember to fetch, and it does
    // NOT on its own make the screen read. 2,546 of 2,988 `rwa` invocations
    // went through a `python3 -c` filter. What makes not-reading FAIL is the
    // citation join in the decisions panel — the verb that answers a decision
    // must cite an id that appears only on that screen — and that gate is the
    // SECOND half of this spec.
    //
    // =================== WHAT THIS HALF DELIBERATELY DOES NOT DO ============
    //  * No answer gate. `advance` refuses for exactly the reasons it refused
    //    before this change, and 722c951's unread-journal gate is untouched.
    //  * No `defer`, no `write-off`. A loss level simply persists, which is
    //    the safe direction.
    //  * No map. `ee4b4f8` is its own spec; the map panel carries the digest's
    //    `site` line and names the seam.
    //
    // ============================ THE BUDGET ================================
    // COCKPIT.md estimates two to four kilobytes and the round's budget is two
    // to four thousand tokens a turn. The digest's own sections carry long
    // prose disclaimers — `food_days_basis` alone is ~460 bytes — which earn
    // their place in a verb a caller asked for and do not earn it on a screen
    // that arrives every turn. `Lean` below drops those keys and NAMES them,
    // so the rule that a cut says what it dropped holds for prose as well as
    // for lists.
    public static class Screen
    {
        // The digest's own default. A colony bigger than this prints `more`,
        // which is the digest's shape and not a new one.
        private const int ColonistCap = 10;

        // ---------------------------------------------------------------- //
        //                              look                                 //
        // ---------------------------------------------------------------- //
        //
        // Main thread (the default), because every panel reads Verse. While an
        // advance is in flight `DrainCommands` answers this `busy` like every
        // other main-thread verb but `pause` — which is right: a screen is a
        // snapshot of a stopped colony, and the advance is about to hand back
        // one of its own.
        [Verb("look")]
        public static object Look(VerbContext ctx)
        {
            var map = Find.CurrentMap;
            if (map == null) throw new VerbArgsException("no current map");
            int cap = ctx.Args.Int("colonists_cap", ColonistCap);
            if (cap < 1 || cap > 200) throw new VerbArgsException("colonists_cap must be 1..200");

            // PEEK, BUILD, THEN COMMIT — in that order, so a screen that
            // throws while it is being built consumes nothing. `Teardown` gets
            // this for free because it consumes only when a data block is
            // actually going out; a verb has to spell it.
            var window = ScreenLog.Peek();
            var screen = Build(StopForLook(), window, null, cap);
            // The clock half of the window, and the mark. AFTER the build.
            var since = TimeDriver.DeliverScreenForLook();
            ScreenLog.Commit(window);
            if (screen.TryGetValue("since", out var sinceP) && sinceP is Dictionary<string, object> sp)
                sp["clock"] = since;
            // The stop line's own "you last looked N ticks ago", which is only
            // knowable once the clock window has been taken. A `look` moves no
            // time, so this is entirely what the clock did outside an advance.
            if (screen.TryGetValue("stop", out var stopP) && stopP is Dictionary<string, object> st
                && since.TryGetValue("ticks", out var t))
                st["since_last_look_ticks"] = t;
            return screen;
        }

        // ---------------------------------------------------------------- //
        //                          the assembly                             //
        // ---------------------------------------------------------------- //
        internal static Dictionary<string, object> Build(Dictionary<string, object> stop,
                                                         ScreenLog.Window window,
                                                         Dictionary<string, object> sinceClock)
            => Build(stop, window, sinceClock, ColonistCap);

        internal static Dictionary<string, object> Build(Dictionary<string, object> stop,
                                                         ScreenLog.Window window,
                                                         Dictionary<string, object> sinceClock,
                                                         int colonistCap)
        {
            var map = Find.CurrentMap;
            var d = new Dictionary<string, object>
            {
                ["screen"] = 1,
                ["spec"] = "975973e",
            };
            if (map == null)
            {
                d["error"] = "no current map";
                d["panels"] = Panels();
                return d;
            }

            var time = Safe("time", () => DigestVerb.SectionFor(map, "time"));
            var resources = Safe("resources", () => DigestVerb.SectionFor(map, "resources"));

            // -------- panel 1: the stop line ---------------------------------
            var stopPanel = stop ?? new Dictionary<string, object>();
            stopPanel["time"] = time;
            int lookTicks = -1;
            if (sinceClock != null && sinceClock.TryGetValue("ticks", out var lt) && lt is int lti)
                lookTicks = lti;
            stopPanel["since_last_look_ticks"] = lookTicks >= 0 ? (object)lookTicks : null;
            stopPanel["saved"] = JournalHooks.LastSaveName;
            stopPanel["saved_tick"] = JournalHooks.LastSaveTick >= 0
                ? (object)JournalHooks.LastSaveTick : null;
            try
            {
                var stack = Find.WindowStack;
                if (stack != null && stack.WindowsForcePause)
                    stopPanel["force_paused"] = true;
            }
            catch { }
            d["stop"] = stopPanel;

            // -------- panel 2: the map ---------------------------------------
            //
            // THE SEAM, LEFT CLEAN. `ee4b4f8` owns the crop, the alphabet, the
            // gone marks, the open gates and the sight coverage; until it
            // lands the screen carries the digest's `site` line where the map
            // will go, which is what this spec's own body says to do. The
            // losses are NOT smuggled in here: they are levels and they live
            // on `gauges.losses`, so the map spec adds a picture rather than
            // taking a field over.
            d["map"] = new Dictionary<string, object>
            {
                ["site"] = Safe("site", () => DigestVerb.SectionFor(map, "site")),
                ["pending"] = "ee4b4f8",
                ["detail"] = "the map is its own spec and has not landed: the ASCII crop, what is "
                    + "gone, which openings are covered. Until then the site line here, "
                    + "`gauges.losses` for what is gone, `map-dump {rect, planes}` for a rectangle.",
            };

            // -------- panel 3: the colonists ---------------------------------
            var armament = Safe("armament", () => ScreenGauges.Armament(map));
            var colonists = Safe("colonists", () => DigestVerb.ColonistsFor(map, colonistCap))
                            ?? new Dictionary<string, object>();
            colonists["armed"] = armament.TryGetValue("armed", out var ar) ? ar : null;
            colonists["armed_of"] = armament.TryGetValue("armed_of", out var ao) ? ao : null;
            colonists["unarmed"] = armament.TryGetValue("unarmed", out var ua) ? ua : null;
            colonists["posture"] = Safe("posture", () => Lean(DigestVerb.SectionFor(map, "posture"),
                                        "note", "denominators", "colonists", "areas"));
            d["colonists"] = colonists;

            // -------- panel 4: the gauges ------------------------------------
            var stock = Safe("stock", () => ScreenGauges.Stock(map, resources));
            int offersOpen = 0;
            try { offersOpen = OpenOffers(); } catch { }
            d["gauges"] = new Dictionary<string, object>
            {
                ["guns"] = Safe("guns", () => ScreenGauges.Guns(map)),
                ["power"] = Safe("power",
                    () => ScreenGauges.Power(map, DigestVerb.SectionFor(map, "power"))),
                ["food"] = Safe("food", () => ScreenGauges.Food(resources)),
                ["temperature"] = Safe("temperature",
                    () => Lean(DigestVerb.SectionFor(map, "temperature"), "note", "rooms")),
                ["stock"] = stock,
                ["goals"] = Safe("goals", () => ScreenGauges.Goals(map, armament, stock, offersOpen)),
                // The LIGHTS gauge IS the digest's alert section, with the age
                // and the return flag 91bc250 asked for added to its own rows
                // — the same object `digest.alerts` returns, not a second one.
                ["lights"] = Safe("lights", () => DigestVerb.SectionFor(map, "alerts")),
                ["losses"] = Safe("losses", () => Losses()),
                ["threats"] = Safe("threats", () => Lean(DigestVerb.SectionFor(map, "threats"))),
                ["armament"] = armament,
            };

            // -------- panel 5: since you last looked --------------------------
            //
            // NOT wrapped in `Safe`: it is the only panel whose window is
            // CONSUMED by being printed, so swallowing a throw here would eat
            // rows and report an empty panel. A throw out of this one takes
            // the screen, and `Look` and `BuildData` both leave the window
            // uncommitted when that happens.
            d["since"] = ScreenLog.Panel(window, sinceClock, TimeDriver.EverDeliveredScreen);

            // -------- panel 6: decisions owed ---------------------------------
            d["decisions"] = Safe("decisions", () => ScreenDecisions.Panel(map));

            d["panels"] = Panels();
            return d;
        }

        private static List<object> Panels()
            => new List<object> { "stop", "map", "colonists", "gauges", "since", "decisions" };

        // DEGRADE THE SECTION, NEVER THE SCREEN. One modded alert whose
        // virtual `Label` throws, one letter whose `Choices` iterator
        // dereferences a null quest, one power comp from a mod — any of these
        // would otherwise cost the agent its whole turn, and a turn is the
        // unit this design is built on. The same discipline `interactions`,
        // `temperature` and `work_coverage` already apply to themselves; this
        // is it applied at the panel seam so it holds for sections that do not.
        //
        // The failure is REPORTED in the section's own place, not swallowed:
        // an `{error}` where a gauge should be is visible, and a missing key
        // is not.
        private static Dictionary<string, object> Safe(string what,
                                                       Func<Dictionary<string, object>> f)
        {
            try { return f(); }
            catch (Exception e)
            {
                Journal.EmitWarning("screen: " + what + " threw: " + e.Message);
                return new Dictionary<string, object>
                {
                    ["error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 160),
                    ["section"] = what,
                };
            }
        }

        // ---------------------------------------------------------------- //
        //                          the stop line                            //
        // ---------------------------------------------------------------- //
        internal static Dictionary<string, object> StopForAdvance(string reason, object haltEvent,
                                                                  long haltSeq)
        {
            var d = new Dictionary<string, object>
            {
                ["kind"] = reason,
                ["why"] = Why(reason),
            };
            if (haltEvent != null)
            {
                d["halted_on"] = haltEvent;
                // ONLY WHEN IT NAMES A JOURNAL LINE, the same rule `BuildData`
                // applies: a state halt has no seq, and publishing 0 would
                // send a caller to `journal --since 0`.
                if (haltSeq > 0) d["halted_seq"] = (double)haltSeq;
            }
            return d;
        }

        internal static Dictionary<string, object> StopForLook()
            => new Dictionary<string, object>
            {
                ["kind"] = "look",
                ["why"] = "you asked to look. No time passed and nothing was decided; this is the "
                    + "same object `advance` returns as its result.",
            };

        // The halt reasons `TimeDriver.Finish` can pass, in a sentence. Anything
        // it does not know is echoed rather than guessed at, because a reason
        // this file has not heard of is a reason a future halt added.
        private static string Why(string reason)
        {
            switch (reason)
            {
                case "ticks": return "the tick target was reached. Nothing interrupted.";
                case "letter": return "a letter arrived. Read `stop.halted_on`; a letter that carries "
                    + "a choice is in `decisions`.";
                case "alert": return "an alert went active. `gauges.lights` carries its age.";
                case "threat": return "a hostile the colony has not decided about is on the map.";
                case "event": return "the `until` condition you passed matched a journal event.";
                case "state": return "the `until` state predicate you passed became true.";
                case "casualty": return "somebody of ours went down or died.";
                case "destroyed": return "something the colony built was destroyed. It stays on its "
                    + "gauge as `was N` until it is written off.";
                case "dialog": return "a force-pausing window went up. The game is stopped until it "
                    + "is answered — see `decisions`.";
                case "error": return "a red error was logged.";
                case "timeout": return "the timeout bound was reached before anything else stopped it.";
                case "interrupt": return "the advance was interrupted.";
                default: return reason == null ? null : "halted: " + reason;
            }
        }

        // ---------------------------------------------------------------- //
        //                        losses are levels                          //
        // ---------------------------------------------------------------- //
        //
        // Everything the colony built and lost this session, newest first,
        // capped and saying what it dropped. The per-def `was N` lives on the
        // gauge the thing belongs to (`gauges.guns` today); this is the list,
        // and it is the seam `ee4b4f8` marks on the map and `write-off` clears.
        private const int LossRowCap = 10;

        private static Dictionary<string, object> Losses()
        {
            var all = LossLevels.Snapshot();
            var rows = new List<object>();
            int now = 0;
            try { now = Find.TickManager.TicksGame; } catch { }
            for (int i = 0; i < all.Count && i < LossRowCap; i++)
            {
                var l = all[i];
                rows.Add(new Dictionary<string, object>
                {
                    ["thing"] = l.ThingId,
                    ["def"] = l.Def,
                    ["label"] = l.Label,
                    ["kind"] = l.Kind,
                    ["at"] = new List<object> { (double)l.X, (double)l.Z },
                    ["tick"] = l.Tick,
                    ["ticks_ago"] = Math.Max(0, now - l.Tick),
                    // The game's own word, never a story about what happened.
                    ["mode"] = l.Mode,
                });
            }
            return new Dictionary<string, object>
            {
                ["rows"] = rows,
                ["total"] = LossLevels.Total,
                ["more"] = Math.Max(0, LossLevels.Total - rows.Count),
                ["dropped_at_source"] = LossLevels.Dropped,
                ["order"] = "newest first",
                ["basis"] = "player-faction buildings and frames destroyed SINCE THIS SESSION BEGAN, "
                    + "minus the five DestroyMode values that are the colony's own work (Deconstruct, "
                    + "WillReplace, Cancel, Refund, FailConstruction). A LEVEL, not an event.",
                ["clears_with"] = "write-off {id, reason} — 975973e's SECOND half, NOT BUILT. Nothing "
                    + "clears a level today, rebuilding included: `was` is count + lost.",
            };
        }

        // Quests in `QuestState.NotYetAccepted` — the G6 count and the
        // decisions panel's `offer` kind, read once and shared.
        private static int OpenOffers()
        {
            var mgr = Find.QuestManager;
            if (mgr == null) return 0;
            int n = 0;
            var all = new List<Quest>(mgr.QuestsListForReading);
            for (int i = 0; i < all.Count; i++)
            {
                var q = all[i];
                if (q == null) continue;
                try { if (q.State != QuestState.NotYetAccepted) continue; } catch { continue; }
                try { if (q.hidden) continue; } catch { }
                n++;
            }
            return n;
        }

        // ---------------------------------------------------------------- //
        //                        the prose budget                           //
        // ---------------------------------------------------------------- //
        //
        // A shallow copy of a digest section with the long prose keys removed
        // and NAMED. The digest's own disclaimers exist because a warning in a
        // source comment did not stop the code three lines below it from
        // drawing a conclusion the agent could not check (`54b0c9a`), and they
        // stay on `digest`, which a caller asks for. On a screen that arrives
        // every turn they are a kilobyte of the same sentence, and this half's
        // budget is two to four thousand tokens.
        //
        // The cut says what it dropped, like every other cut on this screen —
        // it does not silently thin the object.
        private static Dictionary<string, object> Lean(Dictionary<string, object> src,
                                                       params string[] drop)
        {
            if (src == null) return null;
            var d = new Dictionary<string, object>();
            List<object> omitted = null;
            foreach (var kv in src)
            {
                bool skip = false;
                for (int i = 0; i < drop.Length; i++)
                    if (drop[i] == kv.Key) { skip = true; break; }
                if (!skip && !IsProse(kv.Key)) { d[kv.Key] = kv.Value; continue; }
                if (omitted == null) omitted = new List<object>();
                omitted.Add(kv.Key);
            }
            if (omitted != null)
            {
                d["omitted"] = omitted;
                d["omitted_why"] = "dropped for the turn budget; `digest` carries them in full.";
            }
            return d;
        }

        // The digest's prose conventions, by suffix rather than by an
        // enumeration that would go stale the next time a section gains a
        // disclaimer.
        private static bool IsProse(string key)
            => key == "note" || key == "detail" || key == "cite"
               || key.EndsWith("_basis", StringComparison.Ordinal)
               || key.EndsWith("_note", StringComparison.Ordinal);
    }
}
