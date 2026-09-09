using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ========================= DECISIONS OWED, PANEL SIX ====================
    // spec 975973e. COCKPIT.md §"The screen".
    //
    // Each one a sentence a person could answer, with the acts that would
    // answer it ready to send. The act shape is `triage`'s, verbatim and not a
    // second vocabulary: `{op, args, why}` — `op` and `args` are the wire
    // envelope, so a caller sends them as they stand.
    //
    // ===================== WHAT THIS HALF DOES AND DOES NOT DO ==============
    // **THIS PANEL RENDERS. IT DOES NOT GATE.** The answer gate — `advance`
    // refusing with `decision-owed` while one is owed, `defer {id, reason}`,
    // and `write-off {id, reason}` — is the SECOND half of 975973e and is
    // deliberately absent here. Nothing in this file can make `advance` refuse
    // for a new reason, and the unread-journal gate of `722c951` is untouched.
    // A reader who wants to know what the gate will key on: `owed[*].id`, and
    // the ids are derived below so they are stable across screens.
    //
    // ===================== IDS ARE DERIVED, NEVER COUNTED ===================
    // `d-<kind>-<handle>` off the game's own handle — the letter's `ID`, the
    // quest's `id`, the alert's class name, the window's type. A counter would
    // renumber the same decision on the next screen, and the second half's
    // whole mechanism is that "the verb that answers a decision must cite an id
    // that appears only on that screen" (COCKPIT §How it stays honest, item 1)
    // — which needs the id to name the DECISION, not the printing of it.
    //
    // ================= ONE GROUP AT A TIME, NOT A PILE ======================
    // "Decisions arrive in groups, not as a pile. When several are owed, the
    // screen serves one kind at a time and the mod orders the groups by
    // urgency." `owed` is the most urgent KIND; `groups_pending` says how many
    // other kinds are waiting and `total_owed` how many decisions in all, so
    // nothing is hidden — it is queued, and the count of the queue is on the
    // screen.
    internal static class ScreenDecisions
    {
        // 91bc250 item 4, as 975973e restates it: "an alert live for more than
        // a day with no verdict". One in-game day.
        internal const int AlertVerdictTicks = 60000;

        // Sized against the measured saturated screen, not by feel. At 8 owed
        // with 6 acts each the decisions panel alone was 8,756 bytes of a
        // 31,299-byte saturated screen — two to three times the whole turn
        // budget — and the group discipline means the panel is serving ONE
        // KIND at a time anyway. Five of a kind, four ways to answer each,
        // plus the read act; `truncated` says what the cut cost.
        private const int OwedCap = 5;
        private const int ActCap = 4;

        private sealed class Owed
        {
            public string Id;
            public string Kind;
            public string Text;
            public int Urgency;            // higher first
            public int? DeadlineTick;
            public List<object> Acts = new List<object>();
            public Dictionary<string, object> Extra;
        }

        internal static Dictionary<string, object> Panel(Map map)
        {
            var all = new List<Owed>();
            var errors = new List<object>();

            Add(all, errors, "dialog", () => Dialogs());
            Add(all, errors, "letter", () => Letters());
            Add(all, errors, "offer", () => Offers());
            Add(all, errors, "alert", () => Alerts());
            Add(all, errors, "build", () => Builds(map));

            // Urgency orders the GROUPS, and the group is the most urgent
            // kind. Within a group, urgency again, then the id so the order is
            // deterministic run to run.
            all.Sort((a, b) =>
            {
                int c = b.Urgency.CompareTo(a.Urgency);
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            });

            string group = all.Count > 0 ? all[0].Kind : null;
            var inGroup = new List<Owed>();
            var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < all.Count; i++)
            {
                kinds[all[i].Kind] = kinds.TryGetValue(all[i].Kind, out var n) ? n + 1 : 1;
                if (all[i].Kind == group) inGroup.Add(all[i]);
            }

            var rows = new List<object>();
            for (int i = 0; i < inGroup.Count && i < OwedCap; i++) rows.Add(Row(inGroup[i]));

            var d = new Dictionary<string, object>
            {
                ["owed"] = rows,
                ["group"] = group,
                ["in_group"] = inGroup.Count,
                ["shown"] = rows.Count,
                // The digest's rule without exception: capped by importance,
                // and it says what it dropped.
                ["truncated"] = Math.Max(0, inGroup.Count - rows.Count),
                ["total_owed"] = all.Count,
                ["groups_pending"] = Math.Max(0, kinds.Count - (group == null ? 0 : 1)),
                ["by_kind"] = Counts(kinds),
                ["order"] = "urgency-desc, then id; ONE KIND at a time",
            };
            if (errors.Count > 0) d["errors"] = errors;
            // Stated on every screen, because the absence of a gate is
            // load-bearing information for the caller until the second half
            // lands: an unanswered decision costs nothing today.
            d["gate"] = "NOT ENFORCED. The answer gate, `defer` and `write-off` are 975973e's SECOND "
                + "half. Nothing here refuses an advance; 722c951's read gate is unchanged.";
            d["not_detected"] = "'a chore that needs a policy it does not have' is absent because "
                + "the chore set is not built (SweepNameDialogs is the only one). Stated, not hidden.";
            return d;
        }

        private static void Add(List<Owed> all, List<object> errors, string kind, Func<List<Owed>> f)
        {
            try
            {
                var got = f();
                if (got != null) all.AddRange(got);
            }
            catch (Exception e)
            {
                // Degrade the KIND, never the panel, and never the screen. One
                // modded window or one letter whose Choices throws must not
                // cost the agent its whole turn.
                errors.Add(new Dictionary<string, object>
                {
                    ["kind"] = kind,
                    ["error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 120),
                });
            }
        }

        private static Dictionary<string, object> Row(Owed o)
        {
            var d = new Dictionary<string, object>
            {
                ["id"] = o.Id,
                ["kind"] = o.Kind,
                ["text"] = o.Text,
            };
            if (o.DeadlineTick.HasValue)
            {
                d["deadline_tick"] = o.DeadlineTick.Value;
                int now = 0;
                try { now = Find.TickManager.TicksGame; } catch { }
                d["deadline_ticks_left"] = Math.Max(0, o.DeadlineTick.Value - now);
            }
            d["acts"] = o.Acts;
            if (o.Extra != null)
                foreach (var kv in o.Extra) if (!d.ContainsKey(kv.Key)) d[kv.Key] = kv.Value;
            return d;
        }

        private static Dictionary<string, object> Act(string op, Dictionary<string, object> args,
                                                      string why, string label = null)
        {
            var d = new Dictionary<string, object>
            {
                ["op"] = op,
                ["args"] = args ?? new Dictionary<string, object>(),
                ["why"] = why,
            };
            if (label != null) d["label"] = label;
            return d;
        }

        // ---------------------------- dialogs -------------------------------
        //
        // git-bug 9227839: a pending force-pausing dialog is a decision owed.
        // The colony naming prompt arrived at tick ~255,000 in run m1-20260901
        // as a `Dialog_NamePlayerFactionAndSettlement`, halted every advance,
        // and `dialog-dismiss` put it back 1,000 ticks later forever.
        //
        // MOST URGENT OF ALL, because the game is not merely waiting for a
        // judgement — it is stopped: `WindowStack.WindowsForcePause` is true
        // and `TickManagerUpdate` early-returns while it is.
        private static List<Owed> Dialogs()
        {
            var outp = new List<Owed>();
            var stack = Find.WindowStack;
            if (stack == null) return outp;
            var live = new List<Window>(stack.Windows);
            for (int i = 0; i < live.Count; i++)
            {
                var w = live[i];
                if (w == null || !w.forcePause) continue;
                if (w is ImmediateWindow) continue;
                string type = w.GetType().Name;
                var o = new Owed
                {
                    Id = "d-dialog-" + type,
                    Kind = "dialog",
                    Urgency = 100000,
                    Text = "A " + type + " is up and force-pausing the game. Answer it: nothing "
                         + "advances until it closes.",
                    Extra = new Dictionary<string, object>
                    {
                        ["window"] = type,
                        ["window_full"] = w.GetType().FullName,
                    },
                };
                if (w is Dialog_GiveName)
                {
                    // `dialog-dismiss` closes it without answering and
                    // Faction.FactionTick raises it again 1,000 ticks later,
                    // forever — run contract, day-1 check 4.
                    o.Acts.Add(Act("dialog-accept", new Dictionary<string, object> { ["window"] = type },
                        "accepts the name the window already holds. Do not dismiss it: it re-raises."));
                }
                else
                {
                    o.Acts.Add(Act("interactions", new Dictionary<string, object>(),
                        "the window's own option labels, without pressing anything."));
                    o.Acts.Add(Act("dialog-choose", new Dictionary<string, object>
                    {
                        ["window"] = type,
                        ["option"] = 0,
                    }, "press a button by index (`option_label` takes a substring). Read the labels first."));
                    o.Acts.Add(Act("dialog-dismiss", new Dictionary<string, object> { ["window"] = type },
                        "close without answering. A prompt the game re-raises is not answered."));
                }
                outp.Add(o);
            }
            return outp;
        }

        // ---------------------------- letters -------------------------------
        //
        // "Never advance past a letter that carries a choice" is standing rule
        // 6 of the run contract, and the run lost a trader, two quests, a free
        // colonist and a rescue inside batched turns by doing exactly that.
        private static List<Owed> Letters()
        {
            var outp = new List<Owed>();
            var stack = Find.LetterStack;
            if (stack == null) return outp;
            int now = 0;
            try { now = Find.TickManager.TicksGame; } catch { }
            var live = new List<Letter>(stack.LettersListForReading);
            for (int i = 0; i < live.Count; i++)
            {
                var l = live[i];
                var cl = l as ChoiceLetter;
                if (cl == null) continue;
                // The game's own "should this still be shown" test. A
                // ChoiceLetter_AcceptVisitors whose pawns have all left the map
                // answers false, and that is the difference between a live
                // decision and a stale one.
                try { if (!l.CanShowInLetterStack) continue; } catch { }
                // A BundleLetter is the stack's "N more…" roll-up and has no
                // Choices at all; its own OpenLetter dereferences
                // `Event.current`, which is null outside OnGUI.
                if (l is BundleLetter) continue;

                var labels = InteractionVerbs.ChoiceLabels(cl, ActCap, out int disabled, out string err);
                if (labels.Count == 0 && err == null) continue;   // informational, not a decision

                int urgency = 5000;
                int? deadline = null;
                var timed = l as LetterWithTimeout;
                try
                {
                    if (timed != null && timed.TimeoutActive)
                    {
                        deadline = timed.disappearAtTick;
                        // Sooner is more urgent, on the same curve
                        // `InteractionVerbs.LetterAttention` already uses.
                        urgency = 20000 + Math.Max(0, 60000 - Math.Max(0, timed.disappearAtTick - now)) / 100;
                    }
                }
                catch { }
                try
                {
                    if (l.def != null && (l.def == LetterDefOf.ThreatBig || l.def == LetterDefOf.ThreatSmall))
                        urgency += 8000;
                }
                catch { }

                string label = null;
                try { label = Journal.Truncate(l.Label.ToString(), 120); } catch { }
                var o = new Owed
                {
                    Id = "d-letter-" + l.ID,
                    Kind = "letter",
                    Urgency = urgency,
                    DeadlineTick = deadline,
                    Text = (label ?? l.GetType().Name) + " — a letter with a choice. "
                         + (deadline.HasValue
                            ? "It times out, and a letter that times out answers itself."
                            : "It waits, and it will keep waiting."),
                    Extra = new Dictionary<string, object>
                    {
                        ["letter"] = l.ID,
                        ["def"] = l.def?.defName,
                        ["type"] = l.GetType().Name,
                        ["options_disabled"] = disabled,
                    },
                };
                if (err != null) o.Extra["options_error"] = err;
                for (int j = 0; j < labels.Count; j++)
                    o.Acts.Add(Act("letter-choose", new Dictionary<string, object>
                    {
                        ["letter"] = l.ID,
                        ["option"] = labels[j].Key,
                    // Addressed by INDEX: `option_label` is a substring match
                    // and two buttons can share one. Disabled options are not
                    // offered at all — the widget gate would refuse them.
                    }, "presses this button.", labels[j].Value));
                o.Acts.Add(Act("letter-read", new Dictionary<string, object> { ["letter"] = l.ID },
                    "full text and every option, without answering."));
                outp.Add(o);
            }
            return outp;
        }

        // ----------------------------- offers -------------------------------
        //
        // A joiner, a wanderer or a refugee offer. `QuestState.NotYetAccepted`
        // is the game's own gate — `MainTabWindow_Quests.DoAcceptButton`
        // returns before drawing the button unless the state is exactly that —
        // and it is contract goal G6, whose whole text is "never lapsed".
        private static List<Owed> Offers()
        {
            var outp = new List<Owed>();
            var mgr = Find.QuestManager;
            if (mgr == null) return outp;
            int now = 0;
            try { now = Find.TickManager.TicksGame; } catch { }
            var all = new List<Quest>(mgr.QuestsListForReading);
            for (int i = 0; i < all.Count; i++)
            {
                var q = all[i];
                if (q == null) continue;
                string state;
                try { state = q.State.ToString(); } catch { continue; }
                if (state != "NotYetAccepted") continue;
                try { if (q.hidden) continue; } catch { }

                int urgency = 3000;
                int? deadline = null;
                try
                {
                    // Quest.acceptanceExpireTick is -1 for "never expires",
                    // straight from Quest.TicksUntilExpiry.
                    if (q.acceptanceExpireTick > 0)
                    {
                        deadline = q.acceptanceExpireTick;
                        urgency = 15000 + Math.Max(0, 60000 - Math.Max(0, q.acceptanceExpireTick - now)) / 100;
                    }
                }
                catch { }

                var o = new Owed
                {
                    Id = "d-offer-" + q.id,
                    Kind = "offer",
                    Urgency = urgency,
                    DeadlineTick = deadline,
                    Text = (q.name ?? ("quest " + q.id)) + " — an offer nobody has answered. "
                         + (deadline.HasValue ? "It lapses if you say nothing." : "It has no deadline."),
                    Extra = new Dictionary<string, object>
                    {
                        ["quest"] = q.id,
                        ["name"] = q.name,
                    },
                };
                o.Acts.Add(Act("quest", new Dictionary<string, object> { ["quest"] = q.id },
                    "read it: parts, rewards, what accepting commits."));
                o.Acts.Add(Act("quest-accept", new Dictionary<string, object> { ["quest"] = q.id },
                    "accept. The widget's own gate is state == NotYetAccepted."));
                o.Acts.Add(Act("quest-dismiss", new Dictionary<string, object>
                {
                    ["quest"] = q.id,
                    ["dismissed"] = true,
                // Dismissing is NOT declining: it sets a UI flag and the offer
                // still lapses on its own clock. G6 asks for an answer.
                }, "hide it — this is not a decline, and it still lapses."));
                outp.Add(o);
            }
            return outp;
        }

        // ----------------------------- alerts -------------------------------
        //
        // git-bug 91bc250 item 4, as 975973e restates it: an unmuted alert live
        // for more than a day with no verdict. `Alert_ChessTableNoChairs` was
        // harmless; the same silence covered every other alert the run declined
        // to answer, and the run's NeedDoctor sat live for thirteen days.
        //
        // A MUTED ALERT IS NEVER A DECISION. `alert-mute` is a recorded,
        // reasoned act and is therefore the verdict. It is the only verdict the
        // mod records today — `defer {id, reason}` is the second half of this
        // spec — and that is stated on the row rather than implied.
        private static List<Owed> Alerts()
        {
            var outp = new List<Owed>();
            var live = AlertScanner.Snapshot();
            var store = AlertMuteComponent.Current;
            int now = 0;
            try { now = Find.TickManager.TicksGame; } catch { }
            for (int i = 0; i < live.Count; i++)
            {
                string id = live[i].Id;
                if (store != null && store.Has(id)) continue;
                if (!AlertScanner.AgeOf(id, out int firstSeen, out int returns, out int lastOff)) continue;
                int age = now - firstSeen;
                if (age <= AlertVerdictTicks) continue;
                var o = new Owed
                {
                    Id = "d-alert-" + id,
                    Kind = "alert",
                    // A Critical that has been live for a week outranks a
                    // Medium that has been live for a week; the age breaks the
                    // tie inside a priority.
                    Urgency = 1000 + (int)live[i].Priority * 500 + Math.Min(400, age / 60000),
                    Text = live[i].Label + " — live " + Math.Round(age / 60000.0, 1)
                         + " days with no verdict. Fix it, or mute it with a reason.",
                    Extra = new Dictionary<string, object>
                    {
                        ["alert"] = id,
                        ["priority"] = live[i].Priority.ToString(),
                        ["age_ticks"] = age,
                        ["age_days"] = Math.Round(age / 60000.0, 2),
                        ["returned"] = returns > 0,
                    },
                };
                o.Acts.Add(Act("alert-mute", new Dictionary<string, object>
                {
                    ["ids"] = new List<object> { id },
                    ["reason"] = "",
                // The mute and its reason stay on every screen under
                // `gauges.lights.muted`, so it cannot become a decision the
                // agent forgot it made (theme T11).
                }, "records a decision NOT to wake for this. `reason` is REQUIRED and non-empty."));
                outp.Add(o);
            }
            return outp;
        }

        // ------------------------------ builds ------------------------------
        //
        // "A build blocked on a thing the map cannot make." The two clauses
        // that mean exactly that already ship on `construction`:
        // `no_builder` (nobody has Construction enabled at all) and
        // `skill_blocked` (a ceiling nobody on the map clears). Material
        // shortage is deliberately NOT one of these: a hauler will get to it,
        // and `awaiting_materials` is a level rather than a judgement.
        private static List<Owed> Builds(Map map)
        {
            var outp = new List<Owed>();
            if (map == null) return outp;
            var section = ConstructionVerbs.Section(map);
            if (section == null) return outp;
            int noBuilder = Int(section, "no_builder");
            int skillBlocked = Int(section, "skill_blocked");
            if (noBuilder > 0)
            {
                var o = new Owed
                {
                    Id = "d-build-no-builder",
                    Kind = "build",
                    Urgency = 2200,
                    Text = noBuilder + " blueprint(s) nobody can build: no colonist has Construction "
                         + "enabled. Enable it, or cancel the work.",
                    Extra = new Dictionary<string, object> { ["blueprints"] = noBuilder },
                };
                // Standing rule 1: never set a work priority to 0. Zero
                // disables the work type entirely and nothing is ever done —
                // it is the whole causal chain of the last wipe. Use 4.
                o.Acts.Add(Act("work-priorities", new Dictionary<string, object>(),
                    "read the priorities. Never set one to 0 — use 4."));
                o.Acts.Add(Act("construction", new Dictionary<string, object>(),
                    "which blueprints, where, how long stalled."));
                outp.Add(o);
            }
            if (skillBlocked > 0)
            {
                var o = new Owed
                {
                    Id = "d-build-skill",
                    Kind = "build",
                    Urgency = 2100,
                    Text = skillBlocked + " blueprint(s) above every colonist's Construction skill. "
                         + "Nobody on this map can finish them — train, recruit, or cancel.",
                    Extra = new Dictionary<string, object> { ["blueprints"] = skillBlocked },
                };
                o.Acts.Add(Act("construction", new Dictionary<string, object>(),
                    "which blueprints and what ceiling they need."));
                o.Acts.Add(Act("cancel", new Dictionary<string, object>(),
                    "remove the blueprint; same targeting as `designate`."));
                outp.Add(o);
            }
            return outp;
        }

        private static int Int(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.TryGetValue(k, out var v)) return 0;
            if (v is int i) return i;
            if (v is double x) return (int)x;
            if (v is long l) return (int)l;
            return 0;
        }

        private static Dictionary<string, object> Counts(Dictionary<string, int> src)
        {
            var d = new Dictionary<string, object>();
            var keys = new List<string>(src.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++) d[keys[i]] = src[keys[i]];
            return d;
        }
    }
}
