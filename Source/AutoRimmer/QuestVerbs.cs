using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ================================================ specs 548ef48 + 20e5cda ==
    // THE QUEST LOG — the read (548ef48) and the two acts that need it (3.5).
    //
    // WHY THESE SHIP TOGETHER. 3.5's recon settled that a quest cannot be
    // accepted from its letter: `Verse/NewQuestLetter.OpenLetter` switches to
    // the Quests tab, selects the quest and REMOVES the letter, and its
    // `Choices` are only View / Jump / Close. Accept is therefore its own verb,
    // `RimWorld/QuestPart_Choice.Choose` then `RimWorld/Quest.Accept`. And
    // `QuestPart_Choice.PreQuestAccept` is verbatim:
    //
    //     if (choices.Count >= 2)
    //     {
    //         Log.Error("Tried to accept a quest but " + GetType().Name
    //             + " still has a choice unresolved. Auto-choosing the first option.");
    //         Choose(choices[0]);
    //     }
    //
    // A RED ERROR, which breaches this project's standing zero-red-errors
    // invariant. So accept MUST choose first — and it cannot choose what it
    // cannot read. `interactions` (2.4) reports letters and windows awaiting
    // input, which is not the quest log: a quest whose letter was never read
    // while open is invisible afterwards. Hence the read.
    //
    // ------------------------ OBSERVER DISCIPLINE ----------------------------
    // Every accessor below was checked for the write-on-read shape WorldSafe
    // exists for. What was found, and what it cost:
    //
    //  * `Quest.State` / `TicksUntilExpiry` / `TicksSinceAccepted` / `Historical`
    //    / `RequiresAccepter` / `EverAccepted` — all pure reads over scribed
    //    ints and a `parts` walk. Safe. (RimWorld/Quest.cs, each member read.)
    //  * `QuestManager.QuestsListForReading` is `=> allQuests`, a plain field
    //    return. Safe, but SNAPSHOTTED anyway before describing, because
    //    describing runs third-party `QuestPart` code.
    //  * `PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoSuspended`
    //    RETURNS A SHARED STATIC BUFFER that it `Clear()`s and refills on every
    //    read (RimWorld/PawnsFinder.cs, the member's own body). Holding it
    //    across a second PawnsFinder read silently rewrites it under you. Every
    //    use here copies it immediately.
    //  * `Reward` subclasses: `StackElements` is a UI drawer enumerable and is
    //    NEVER touched; `GetDescription` needs a `RewardsGeneratorParams` we do
    //    not have. Rewards are described from public FIELDS plus
    //    `TotalMarketValue`, guarded — the same fields-not-properties rule
    //    `interactions` applies to unknown modded windows, and for the same
    //    reason: `QuestPart` and `Reward` are third-party-extensible, so a
    //    modded getter is arbitrary code.
    //  * `LetterStack.BundleLetter` (the getter) is NOT touched anywhere in this
    //    file or in DialogVerbs.cs: it lazily does
    //    `LetterMaker.MakeLetter(LetterDefOf.BundleLetter)`, `MakeLetter`
    //    assigns `obj.ID = Find.UniqueIDsManager.GetNextLetterID()`, and
    //    `nextLetterID` is `Scribe_Values.Look`-scribed
    //    (RimWorld/UniqueIDsManager.ExposeData). Merely ASKING the stack for its
    //    bundle letter permanently advances a scribed counter.
    //  * `Settlement_TraderTracker.StockListForReading` regenerates an entire
    //    trader inventory from a getter. Not touched, here or in TradeVerbs.cs:
    //    world settlements are a DESIGN v1 non-goal.
    //
    // --------------------- `dismissed` IS NOT A DECLINE ----------------------
    // `Quest.dismissed` is cosmetic filtering in the Quests tab
    // (`MainTabWindow_Quests.DoDismissButton`, whose label key is literally
    // "DismissQuest"/"UnDismissQuest"). It does not decline, expire or end
    // anything — `Quest.Accept` even sets `dismissed = false` on its way past.
    // An agent that reads `dismissed` as "we said no" will be wrong, so the
    // result says what it is, every time, in the payload.
    internal static partial class PawnActs
    {
        public const int QuestCap = 40;
        public const int RewardCap = 12;
        public const int PartCap = 24;
        public const int QuestTextClip = 1200;

        // ====================================================================
        // quests {state?, include_dismissed?, include_hidden?, cap?}
        //
        // The list. State comes from `Quest.State` (RimWorld/QuestState.cs)
        // verbatim, plus the three orthogonal flags the tab draws separately:
        // `dismissed`, `hidden`, `hidden_in_ui`.
        // ====================================================================
        [Verb("quests")]
        public static object Quests(VerbContext ctx)
        {
            const string V = "quests";
            var mgr = Find.QuestManager ?? throw new VerbArgsException("no quest manager (no game loaded?)");

            string state = ctx.Args.Str("state", "all");
            if (Array.IndexOf(StateFilters, state) < 0)
                throw new VerbArgsException("state must be one of " + string.Join("|", StateFilters));
            bool includeDismissed = ctx.Args.Bool("include_dismissed", true);
            bool includeHidden = ctx.Args.Bool("include_hidden", false);
            int cap = ctx.Args.Int("cap", QuestCap);
            if (cap < 1 || cap > 500) throw new VerbArgsException("cap must be 1..500");

            // QuestsListForReading is the live list; snapshot before describing,
            // because describing runs QuestPart and Reward code that mods write.
            var all = new List<Quest>(mgr.QuestsListForReading);

            var rows = new List<object>();
            int total = 0, available = 0, ongoing = 0, ended = 0, dismissed = 0, hidden = 0, withChoice = 0;
            foreach (var q in all)
            {
                if (q == null) continue;
                var st = QState(q);
                if (q.hidden) { hidden++; if (!includeHidden) continue; }
                if (q.dismissed) dismissed++;
                if (q.dismissed && !includeDismissed) continue;
                if (state != "all" && !MatchesFilter(state, st)) continue;

                total++;
                if (st == "NotYetAccepted") available++;
                else if (st == "Ongoing") ongoing++;
                else ended++;
                if (OutstandingChoice(q) != null) withChoice++;

                if (rows.Count < cap) rows.Add(Line(q, st, false));
            }

            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["quests"] = rows,
                ["total"] = total,
                ["more"] = Math.Max(0, total - rows.Count),
                ["counts"] = new Dictionary<string, object>
                {
                    ["available"] = available,
                    ["ongoing"] = ongoing,
                    ["ended"] = ended,
                    ["dismissed"] = dismissed,
                    ["hidden"] = hidden,
                    ["with_outstanding_choice"] = withChoice,
                },
                ["filter"] = new Dictionary<string, object>
                {
                    ["state"] = state,
                    ["include_dismissed"] = includeDismissed,
                    ["include_hidden"] = includeHidden,
                },
                // Stated in the result, not only in a comment: an agent reading
                // `dismissed:true` as "we declined" would be wrong, and this is
                // the only place it will look.
                ["dismissed_means"] = "cosmetic filtering in the Quests tab (MainTabWindow_Quests"
                    + ".DoDismissButton) — NOT a decline, NOT an expiry. A dismissed quest that is "
                    + "still NotYetAccepted can still be accepted; Quest.Accept clears the flag.",
                ["action"] = NoStamp(),
            };
        }

        private static readonly string[] StateFilters =
            { "all", "available", "ongoing", "ended" };

        private static bool MatchesFilter(string filter, string st)
        {
            switch (filter)
            {
                case "available": return st == "NotYetAccepted";
                case "ongoing": return st == "Ongoing";
                case "ended": return st != "NotYetAccepted" && st != "Ongoing";
                default: return true;
            }
        }

        // ====================================================================
        // quest {quest} -> the drill-down, in the sections shape 2.2/2.4 use.
        // ====================================================================
        public static readonly string[] QuestSections =
            { "head", "choice", "rewards", "requirements", "parts", "targets", "description" };

        [Verb("quest")]
        public static object QuestDetail(VerbContext ctx)
        {
            var q = QuestArg(ctx.Args, "quest");

            var want = new HashSet<string>();
            if (ctx.Args.Has("sections"))
            {
                foreach (var s in ctx.Args.StrList("sections"))
                {
                    if (Array.IndexOf(QuestSections, s) < 0)
                        throw new VerbArgsException(
                            $"unknown section '{s}' ({string.Join("|", QuestSections)})");
                    want.Add(s);
                }
                if (want.Count == 0) throw new VerbArgsException("sections must not be empty");
            }
            else foreach (var s in QuestSections) want.Add(s);

            var d = Line(q, QState(q), true, want);
            d["verb"] = "quest";
            d["sections"] = new List<object>(want);
            d["action"] = NoStamp();
            return d;
        }

        // ====================================================================
        // quest-accept {quest, choice?, pawn?}
        //
        // THE GATE LIVES IN THE WIDGET, and for this verb it lives in three
        // separate places in `RimWorld/MainTabWindow_Quests.cs`:
        //
        //  1. `DoAcceptButton` DOES NOT DRAW the plain Accept button at all
        //     when the quest has a `QuestPart_Choice` (`if (questPart_Choice
        //     != null && !Prefs.DevMode) return;` and the button is inside
        //     `if (questPart_Choice == null)`). That is the widget-level form
        //     of "choose first", and it is why `choice` is REQUIRED here
        //     whenever a choice is outstanding. Refusing is what keeps
        //     `QuestPart_Choice.PreQuestAccept`'s Log.Error unreachable.
        //  2. `DoAcceptButton` also returns early unless
        //     `selected.State == QuestState.NotYetAccepted`.
        //  3. `AcceptQuestByInterface` refuses with "MessageCannotAcceptQuest"
        //     when `QuestUtility.CanAcceptQuest(quest)` is not accepted, and
        //     with "MessageNoColonistCanAcceptQuest" when `requiresAccepter`
        //     and no colonist passes `QuestUtility.CanPawnAcceptQuest`.
        //
        // `requiresAccepter` IS NOT `Quest.RequiresAccepter` on the choice
        // path. The reward-choice button (`MainTabWindow_Quests`, the
        // "AcceptQuestFor" button) computes it over the parts that will REMAIN
        // after `Choose` removes the other choices' parts, which is a strictly
        // smaller set. Reproduced exactly below, because using the whole-quest
        // property would demand an accepter for a choice we are about to throw
        // away.
        //
        // TWO THINGS THE WIDGET DOES THAT THIS VERB DELIBERATELY DOES NOT:
        //  * `SoundDefOf.Quest_Accepted.PlayOneShotOnCamera()` — presentation.
        //  * The royal-favour confirmation. When the chosen accepter would take
        //    a `QuestPart_GiveRoyalFavor` with `giveToAccepter` and has a
        //    conceited trait / disabled Social / a psylink-hostile trait,
        //    `AcceptQuestByInterface` raises a `Dialog_MessageBox`
        //    CONFIRMATION. A modal is exactly what spec 1.7 proves wedges an
        //    unattended run, so the three findings are re-derived and returned
        //    as `accepter_warnings` instead — the same call 3.4 made for
        //    `Bill.CreateNoPawnsWithSkillDialog` (`sendMessages:false` plus the
        //    warnings as result fields). Set `confirm_accepter_warnings:true`
        //    to proceed when any fire; the default refuses, which is the
        //    headless equivalent of the player clicking "Go back".
        //
        // `Quest.Accept(Pawn by)` is a SILENT NO-OP outside NotYetAccepted (it
        // is a bare `if (State == QuestState.NotYetAccepted)` with no return
        // value), so the state is READ BACK after the call — the FSWA bridge's
        // rule for void setters.
        // ====================================================================
        [Verb("quest-accept")]
        public static object QuestAccept(VerbContext ctx)
        {
            const string V = "quest-accept";
            var q = QuestArg(ctx.Args, "quest");
            bool confirmWarnings = ctx.Args.Bool("confirm_accepter_warnings", false);

            string st = QState(q);
            if (st != "NotYetAccepted")
                return Refuse(V, q, "not-yet-accepted", "MainTabWindow_Quests.DoAcceptButton returns "
                    + "before drawing the button unless State == QuestState.NotYetAccepted",
                    "quest state is " + st + "; only a NotYetAccepted quest can be accepted");

            var choicePart = FirstChoicePart(q);
            QuestPart_Choice.Choice picked = null;

            if (choicePart != null && choicePart.choices != null && choicePart.choices.Count >= 2)
            {
                if (!ctx.Args.Has("choice"))
                    return Refuse(V, q, "choice-outstanding",
                        "MainTabWindow_Quests.DoAcceptButton draws the plain Accept button only when "
                        + "questPart_Choice == null; the reward-choice button is the only accept path "
                        + "for a quest with an outstanding QuestPart_Choice",
                        "this quest has " + choicePart.choices.Count + " outstanding reward choices and "
                        + "`choice` was not given. Accepting without choosing would run "
                        + "QuestPart_Choice.PreQuestAccept, which Log.Errors \"still has a choice "
                        + "unresolved\" and auto-picks the first — a red error. Read `quest "
                        + "{quest:" + q.id + "}` and pass `choice`.",
                        new Dictionary<string, object> { ["choice"] = ChoiceBlock(choicePart) });

                int idx = ctx.Args.IntReq("choice");
                if (idx < 0 || idx >= choicePart.choices.Count)
                    throw new VerbArgsException("choice must be 0.." + (choicePart.choices.Count - 1)
                        + " (this quest has " + choicePart.choices.Count + " outstanding reward choices)");
                picked = choicePart.choices[idx];
            }
            else if (choicePart != null && choicePart.choices != null && choicePart.choices.Count == 1)
            {
                // Already resolved to one: PreQuestAccept's guard is
                // `choices.Count >= 2`, so this accepts cleanly either way. A
                // `choice:0` here is accepted rather than ignored, so a caller
                // that read the quest a moment earlier is not surprised.
                if (ctx.Args.Has("choice"))
                {
                    int only = ctx.Args.IntReq("choice");
                    if (only != 0)
                        throw new VerbArgsException("choice must be 0 (only one reward choice remains "
                            + "on this quest — it is already resolved)");
                    picked = choicePart.choices[0];
                }
            }
            else if (ctx.Args.Has("choice"))
            {
                throw new VerbArgsException("this quest has no outstanding QuestPart_Choice; "
                    + "remove the `choice` arg");
            }

            // Gate 3a — QuestUtility.CanAcceptQuest, the AcceptanceReport the
            // tab shows as a grey tooltip and AcceptQuestByInterface refuses on.
            AcceptanceReport report;
            try { report = QuestUtility.CanAcceptQuest(q); }
            catch (Exception e) { throw new VerbArgsException("QuestUtility.CanAcceptQuest threw: " + e.Message); }
            if (!report.Accepted)
                return Refuse(V, q, "cannot-accept",
                    "MainTabWindow_Quests.AcceptQuestByInterface -> "
                    + "Messages.Message(\"MessageCannotAcceptQuest\") when "
                    + "!QuestUtility.CanAcceptQuest(quest)",
                    string.IsNullOrEmpty(report.Reason)
                        ? "the game refuses this quest (no player home map, or the player does not have "
                          + "control, or a QuestPart_RequirementsToAccept is unmet)"
                        : report.Reason);

            // requiresAccepter, over the parts that SURVIVE the choice.
            bool requiresAccepter = RequiresAccepterAfter(q, choicePart, picked);

            Pawn accepter = null;
            var candidates = new List<Pawn>();
            if (requiresAccepter)
            {
                // Copied immediately: PawnsFinder hands back a shared buffer.
                var pool = new List<Pawn>(
                    PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoSuspended);
                foreach (var p in pool)
                {
                    if (p == null) continue;
                    bool canAccept = false;
                    try { canAccept = QuestUtility.CanPawnAcceptQuest(p, q); } catch { }
                    if (canAccept) candidates.Add(p);
                }
                if (candidates.Count == 0)
                    return Refuse(V, q, "no-accepter",
                        "MainTabWindow_Quests.AcceptQuestByInterface -> "
                        + "Messages.Message(\"MessageNoColonistCanAcceptQuest\") when the float menu "
                        + "over PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_"
                        + "FreeColonists_NoSuspended filtered by QuestUtility.CanPawnAcceptQuest is empty",
                        "this quest requires an accepter and no free, alive, unsuspended, "
                        + "non-downed, non-lodger colonist passes CanPawnAcceptQuest");

                if (ctx.Args.Has("pawn"))
                {
                    int id = ctx.Args.IntReq("pawn");
                    foreach (var p in candidates) if (p.thingIDNumber == id) { accepter = p; break; }
                    if (accepter == null)
                        throw new VerbArgsException("pawn " + id + " cannot accept this quest; "
                            + "eligible: " + Ids(candidates));
                }
                else
                {
                    // The float menu is a CHOICE the player makes. With none
                    // given, take the first eligible in PawnsFinder order —
                    // deterministic, unlike the widget's RandomElement dev path
                    // — and say which, so the caller can pin it next time.
                    accepter = candidates[0];
                }

                var warnings = AccepterWarnings(q, accepter);
                if (warnings.Count > 0 && !confirmWarnings)
                    return Refuse(V, q, "accepter-warnings",
                        "MainTabWindow_Quests.AcceptQuestByInterface raises a Dialog_MessageBox "
                        + "confirmation (\"QuestGivesRoyalFavor\" + \"WantToContinue\") before "
                        + "accepting with this pawn",
                        "the chosen accepter would take a royal favour they are a poor fit for; "
                        + "the game asks the player to confirm. Re-send with "
                        + "confirm_accepter_warnings:true to proceed, or pass a different `pawn`.",
                        new Dictionary<string, object>
                        {
                            ["accepter"] = PawnRef(accepter),
                            ["accepter_warnings"] = warnings,
                            ["eligible"] = Ids(candidates),
                        });
            }
            else if (ctx.Args.Has("pawn"))
            {
                throw new VerbArgsException("this quest does not require an accepter "
                    + "(no surviving QuestPart.RequiresAccepter); remove the `pawn` arg");
            }

            // ---------------------------- the act ---------------------------
            // Order is load-bearing: Choose BEFORE Accept, always. The widget
            // does the same thing by passing Choose as `preAcceptAction`, which
            // runs immediately before `selected.Accept(...)`.
            int choicesBefore = choicePart?.choices?.Count ?? 0;
            if (picked != null) choicePart.Choose(picked);
            int choicesAfter = choicePart?.choices?.Count ?? 0;

            q.Accept(accepter);

            // Read the write back: Accept is `void` and silently does nothing
            // outside NotYetAccepted.
            string after = QState(q);
            bool ok = after != "NotYetAccepted" && q.acceptanceTick >= 0;

            long seq = Act(V, "accept", "Quest_" + q.id, new Dictionary<string, object>
            {
                ["quest"] = q.id,
                ["name"] = q.name,
                ["choice"] = picked != null ? (object)ctx.Args.Int("choice", -1) : null,
                ["accepter"] = accepter?.LabelShortCap.ToString(),
                ["state_after"] = after,
            });

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = ok,
                ["quest"] = q.id,
                ["name"] = q.name,
                ["state"] = after,
                ["acceptance_tick"] = q.acceptanceTick,
                ["ticks_since_accepted"] = WorldSafe.SafeObj(() => (object)q.TicksSinceAccepted),
                ["dismissed"] = q.dismissed,
                ["accepter"] = accepter == null ? null : PawnRef(accepter),
                ["required_accepter"] = requiresAccepter,
                ["choice_taken"] = picked == null ? null : (object)ctx.Args.Int("choice", -1),
                ["choices_before"] = choicesBefore,
                ["choices_after"] = choicesAfter,
                ["action"] = Stamp(seq),
            };
            if (requiresAccepter) d["eligible_accepters"] = Ids(candidates);
            if (!ok)
                d["note"] = "Quest.Accept is a bare `if (State == QuestState.NotYetAccepted)` with no "
                    + "return value — it did not take. State was read back rather than assumed.";
            else if (choicesBefore >= 2)
                d["note"] = "the reward choice was made BEFORE Accept ran, so "
                    + "QuestPart_Choice.PreQuestAccept's `choices.Count >= 2` Log.Error branch was "
                    + "never reached. Expect no red error from this call.";
            return d;
        }

        // ====================================================================
        // quest-dismiss {quest, dismissed?}
        //
        // `MainTabWindow_Quests.DoDismissButton`. Cosmetic filtering only, and
        // the result says so every time. The widget's own third state — DELETE,
        // for a Historical quest — is NOT reproduced: it calls
        // `QuestManager.Remove`, which is destructive and outside this spec.
        // ====================================================================
        [Verb("quest-dismiss")]
        public static object QuestDismiss(VerbContext ctx)
        {
            const string V = "quest-dismiss";
            var q = QuestArg(ctx.Args, "quest");
            bool want = ctx.Args.Bool("dismissed", true);

            // The widget's own branch: for a Historical quest the button is
            // DELETE, not dismiss ("DeleteQuest"), so dismissing one is not a
            // click any player can make.
            bool historical = false;
            try { historical = q.Historical; } catch { }
            if (historical && want)
                return Refuse(V, q, "historical",
                    "MainTabWindow_Quests.DoDismissButton draws DeleteQuest (not DismissQuest) "
                    + "for a Historical quest",
                    "this quest is historical (" + QState(q) + "); the tab offers deletion rather than "
                    + "dismissal for it, and deletion is out of scope for this verb");

            bool before = q.dismissed;
            q.dismissed = want;

            long seq = before == want ? 0 : Act(V, want ? "dismiss" : "undismiss", "Quest_" + q.id,
                new Dictionary<string, object> { ["quest"] = q.id, ["name"] = q.name });

            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = q.dismissed == want,
                ["quest"] = q.id,
                ["name"] = q.name,
                ["dismissed"] = q.dismissed,
                ["was"] = before,
                ["state"] = QState(q),
                ["action"] = before == want ? NoStamp() : Stamp(seq),
                ["note"] = "cosmetic filtering only — this does NOT decline the quest, end it or stop "
                    + "its clock. A dismissed NotYetAccepted quest can still be accepted, and "
                    + "Quest.Accept clears the flag itself.",
            };
        }

        // ======================= shared quest plumbing ======================

        public static Quest QuestArg(VerbArgs args, string key)
        {
            var mgr = Find.QuestManager ?? throw new VerbArgsException("no quest manager (no game loaded?)");
            object raw = args.Raw(key);
            if (raw == null) throw new VerbArgsException($"missing required arg '{key}' (quest id or name)");
            var all = new List<Quest>(mgr.QuestsListForReading);
            if (raw is double d)
            {
                int id = (int)d;
                foreach (var q in all) if (q != null && q.id == id) return q;
                throw new VerbArgsException("no quest with id " + id + " (see `quests`)");
            }
            if (raw is string s)
            {
                Quest hit = null;
                int matches = 0;
                foreach (var q in all)
                {
                    if (q?.name == null) continue;
                    if (string.Equals(q.name, s, StringComparison.OrdinalIgnoreCase)) { hit = q; matches++; }
                }
                if (matches == 1) return hit;
                if (matches > 1)
                    throw new VerbArgsException("quest name '" + s + "' is ambiguous (" + matches
                        + " quests share it) — use the numeric id from `quests`");
                throw new VerbArgsException("no quest named '" + s + "' (see `quests`)");
            }
            throw new VerbArgsException($"arg '{key}' must be a quest id (number) or name (string)");
        }

        private static string QState(Quest q)
        {
            try { return q.State.ToString(); } catch { return "unknown"; }
        }

        public static QuestPart_Choice FirstChoicePart(Quest q)
        {
            try
            {
                // The widget's own scan: MainTabWindow_Quests.DoAcceptButton
                // walks PartsListForReading and breaks on the FIRST
                // QuestPart_Choice, so a quest with two of them is answered by
                // its first — reproduced rather than improved on.
                var parts = q.PartsListForReading;
                for (int i = 0; i < parts.Count; i++)
                    if (parts[i] is QuestPart_Choice c) return c;
            }
            catch { }
            return null;
        }

        // Outstanding == the game's own definition, `QuestPart_Choice
        // .PreventsAutoAccept => choices.Count >= 2`. One remaining choice is
        // already resolved and does not gate acceptance.
        private static QuestPart_Choice OutstandingChoice(Quest q)
        {
            var c = FirstChoicePart(q);
            if (c?.choices != null && c.choices.Count >= 2) return c;
            return null;
        }

        // MainTabWindow_Quests' "AcceptQuestFor" button, exactly: all parts,
        // minus every part unique to a choice that is NOT the one being taken,
        // then ask which of the survivors RequiresAccepter.
        private static bool RequiresAccepterAfter(Quest q, QuestPart_Choice part, QuestPart_Choice.Choice taking)
        {
            try
            {
                if (part == null || taking == null)
                {
                    try { return q.RequiresAccepter; } catch { return false; }
                }
                var remaining = new List<QuestPart>(q.PartsListForReading);
                for (int i = 0; i < part.choices.Count; i++)
                {
                    var other = part.choices[i];
                    if (other == taking) continue;
                    for (int j = 0; j < other.questParts.Count; j++)
                    {
                        var item = other.questParts[j];
                        if (!taking.questParts.Contains(item)) remaining.Remove(item);
                    }
                }
                for (int i = 0; i < remaining.Count; i++)
                    if (remaining[i] != null && remaining[i].RequiresAccepter) return true;
            }
            catch { }
            return false;
        }

        // The three conditions AcceptQuestByInterface confirms with a modal,
        // re-derived as data. Same shape as 3.4's surgery warnings.
        private static List<object> AccepterWarnings(Quest q, Pawn p)
        {
            var list = new List<object>();
            try
            {
                QuestPart_GiveRoyalFavor favor = null;
                var parts = q.PartsListForReading;
                for (int i = 0; i < parts.Count; i++)
                    if (parts[i] is QuestPart_GiveRoyalFavor f) { favor = f; break; }
                if (favor == null || !favor.giveToAccepter) return list;

                bool socialDisabled = false;
                try { socialDisabled = p.skills.GetSkill(SkillDefOf.Social).TotallyDisabled; } catch { }
                if (socialDisabled)
                    list.Add(new Dictionary<string, object>
                    {
                        ["clause"] = "social-disabled",
                        ["text"] = WorldSafe.Safe(() => "RoyalIncapableOfSocial".Translate(
                            p.Named("PAWN"), favor.faction.Named("FACTION")).Resolve()),
                    });

                var conceited = new List<object>();
                try
                {
                    foreach (var t in RoyalTitleUtility.GetConceitedTraits(p))
                        if (t != null) conceited.Add(t.Label);
                }
                catch { }
                if (conceited.Count > 0)
                    list.Add(new Dictionary<string, object>
                    {
                        ["clause"] = "conceited-trait",
                        ["traits"] = conceited,
                    });

                bool hasPsylink = false;
                try { hasPsylink = p.HasPsylink; } catch { }
                var psyBad = new List<object>();
                try
                {
                    if (!hasPsylink)
                        foreach (var t in RoyalTitleUtility.GetTraitsAffectingPsylinkNegatively(p))
                            if (t != null) psyBad.Add(t.Label);
                }
                catch { }
                if (psyBad.Count > 0)
                    list.Add(new Dictionary<string, object>
                    {
                        ["clause"] = "psylink-hostile-trait",
                        ["traits"] = psyBad,
                    });
            }
            catch { }
            return list;
        }

        private static Dictionary<string, object> Refuse(string verb, Quest q, string gate,
            string cite, string reason, Dictionary<string, object> extra = null)
        {
            var d = new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["ok"] = false,
                ["quest"] = q.id,
                ["name"] = q.name,
                ["state"] = QState(q),
                ["gate"] = gate,
                ["gate_cite"] = cite,
                ["reason"] = reason,
                ["action"] = NoStamp(),
            };
            if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
            return d;
        }

        private static List<object> Ids(List<Pawn> pawns)
        {
            var l = new List<object>();
            foreach (var p in pawns) if (p != null) l.Add(PawnRef(p));
            return l;
        }

        private static Dictionary<string, object> PawnRef(Pawn p)
            => new Dictionary<string, object>
            {
                ["id"] = p.thingIDNumber,
                ["name"] = WorldSafe.Safe(() => p.LabelShortCap.ToString()),
            };

        // -------------------------- serialization ---------------------------

        private static Dictionary<string, object> Line(Quest q, string st, bool full,
            HashSet<string> sections = null)
        {
            bool Want(string s) => !full || sections == null || sections.Contains(s);

            var d = new Dictionary<string, object>();
            if (Want("head"))
            {
                d["id"] = q.id;
                d["name"] = q.name;
                d["state"] = st;
                d["root"] = q.root?.defName;
                d["hidden"] = q.hidden;
                d["hidden_in_ui"] = q.hiddenInUI;
                // Reported distinctly from state, deliberately: dismissed is a
                // flag, declined/expired are states, and conflating them is the
                // failure 548ef48 names.
                d["dismissed"] = q.dismissed;
                d["ever_accepted"] = WorldSafe.SafeObj(() => (object)q.EverAccepted);
                d["initially_accepted"] = q.initiallyAccepted;
                d["appearance_tick"] = q.appearanceTick;
                d["acceptance_tick"] = q.acceptanceTick;
                d["acceptance_expire_tick"] = q.acceptanceExpireTick;
                // -1 means "never expires", straight from Quest.TicksUntilExpiry.
                d["ticks_until_expiry"] = WorldSafe.SafeObj(() => (object)q.TicksUntilExpiry);
                d["ticks_since_appeared"] = WorldSafe.SafeObj(() => (object)q.TicksSinceAppeared);
                d["ticks_since_accepted"] = WorldSafe.SafeObj(() => (object)q.TicksSinceAccepted);
                d["challenge_rating"] = q.challengeRating;
                d["points"] = WorldSafe.R(q.points, 1);
                d["charity"] = q.charity;
                d["requires_accepter"] = WorldSafe.SafeObj(() => (object)q.RequiresAccepter);
                d["accepter"] = WorldSafe.Safe(() => q.AccepterPawnLabelCap);
                d["increases_population"] = WorldSafe.SafeObj(() => (object)q.IncreasesPopulation);
                d["historical"] = WorldSafe.SafeObj(() => (object)q.Historical);
                var tags = new List<object>();
                try { if (q.tags != null) foreach (var t in q.tags) tags.Add(t); } catch { }
                d["tags"] = tags;
                var factions = new List<object>();
                try { foreach (var f in q.InvolvedFactions) if (f != null) factions.Add(f.Name); }
                catch { }
                d["factions"] = factions;

                // Can it be accepted right now, and if not, in the game's words.
                if (st == "NotYetAccepted")
                {
                    try
                    {
                        var rep = QuestUtility.CanAcceptQuest(q);
                        d["can_accept"] = new Dictionary<string, object>
                        {
                            ["ok"] = rep.Accepted,
                            ["reason"] = rep.Accepted ? null : (object)rep.Reason,
                        };
                    }
                    catch (Exception e)
                    {
                        d["can_accept"] = new Dictionary<string, object>
                        {
                            ["ok"] = null,
                            ["error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 120),
                        };
                    }
                }
            }

            if (Want("choice"))
            {
                var part = FirstChoicePart(q);
                d["choice"] = part == null ? null : (object)ChoiceBlock(part);
            }

            if (Want("rewards"))
            {
                // The non-choice rewards: everything a QuestPart_Choice does not
                // own. Cheap and bounded.
                var rewards = new List<object>();
                try
                {
                    var part = FirstChoicePart(q);
                    if (part?.choices != null && part.choices.Count == 1)
                        foreach (var r in part.choices[0].rewards)
                        {
                            if (rewards.Count >= RewardCap) break;
                            rewards.Add(RewardLine(r));
                        }
                }
                catch { }
                d["rewards"] = rewards;
            }

            if (Want("requirements")) d["requirements"] = Requirements(q);
            if (Want("parts")) d["parts"] = Parts(q, full);
            if (Want("targets") && full) d["targets"] = Targets(q);
            if (Want("description") && full)
                d["description"] = WorldSafe.Safe(
                    () => Journal.Truncate(q.description.ToString(), QuestTextClip));

            return d;
        }

        // The read 3.5's accept verb needs in order to choose before accepting.
        private static Dictionary<string, object> ChoiceBlock(QuestPart_Choice part)
        {
            var options = new List<object>();
            int count = 0;
            try
            {
                count = part.choices?.Count ?? 0;
                for (int i = 0; i < count && i < RewardCap; i++)
                {
                    var c = part.choices[i];
                    var rewards = new List<object>();
                    double value = 0;
                    try
                    {
                        for (int j = 0; j < c.rewards.Count; j++)
                        {
                            if (rewards.Count < RewardCap) rewards.Add(RewardLine(c.rewards[j]));
                            try { value += c.rewards[j].TotalMarketValue; } catch { }
                        }
                    }
                    catch { }
                    options.Add(new Dictionary<string, object>
                    {
                        // THE INDEX `quest-accept {choice:N}` TAKES.
                        ["index"] = i,
                        ["rewards"] = rewards,
                        ["total_market_value"] = Math.Round(value, 1),
                        ["quest_parts"] = c.questParts?.Count ?? 0,
                    });
                }
            }
            catch { }
            return new Dictionary<string, object>
            {
                ["part"] = part.GetType().Name,
                ["count"] = count,
                // The game's own test, QuestPart_Choice.PreventsAutoAccept.
                ["outstanding"] = count >= 2,
                ["choice_used"] = part.choiceUsed,
                ["options"] = options,
                ["note"] = count >= 2
                    ? "OUTSTANDING — `quest-accept` requires `choice` while this is true. Accepting "
                      + "without it runs QuestPart_Choice.PreQuestAccept, which logs a red error and "
                      + "auto-picks option 0."
                    : "resolved (fewer than 2 choices remain); PreQuestAccept's Log.Error branch is "
                      + "already unreachable",
            };
        }

        // FIELDS and TotalMarketValue only — never StackElements (UI drawers)
        // and never GetDescription (needs generator params we do not have).
        // Reward subclasses are third-party-extensible, so an unknown one
        // degrades to its type name rather than taking the verb down with it.
        private static Dictionary<string, object> RewardLine(Reward r)
        {
            var d = new Dictionary<string, object>
            {
                ["type"] = r.GetType().Name,
                ["type_full"] = r.GetType().FullName,
                ["market_value"] = WorldSafe.SafeObj(() => (object)Math.Round((double)r.TotalMarketValue, 1)),
            };
            try
            {
                if (r is Reward_Items items)
                {
                    var things = new List<object>();
                    var src = items.items;
                    for (int i = 0; i < src.Count && things.Count < RewardCap; i++)
                    {
                        var t = src[i];
                        if (t == null) continue;
                        things.Add(new Dictionary<string, object>
                        {
                            ["def"] = t.def?.defName,
                            ["label"] = WorldSafe.Safe(() => t.LabelNoCount),
                            ["count"] = t.stackCount,
                        });
                    }
                    d["items"] = things;
                    d["items_total"] = src.Count;
                }
                else if (r is Reward_Goodwill gw)
                {
                    d["amount"] = gw.amount;
                    d["faction"] = gw.faction?.Name;
                }
                else if (r is Reward_RoyalFavor rf)
                {
                    d["amount"] = rf.amount;
                    d["faction"] = rf.faction?.Name;
                    // Reward_RoyalFavor.MakesUseOfChosenPawnSignal is true, i.e.
                    // this is the reward that drives quest-accept's accepter
                    // confirmation.
                    d["goes_to_accepter"] = true;
                }
                else if (r is Reward_Pawn rp)
                {
                    d["pawn"] = rp.detailsHidden ? null : WorldSafe.Safe(() => rp.pawn?.LabelShortCap.ToString());
                    d["details_hidden"] = rp.detailsHidden;
                    d["arrival_mode"] = rp.arrivalMode.ToString();
                }
                else
                {
                    // Unknown (modded, or a vanilla one with no public shape):
                    // ToString is overridden on most Reward subclasses and is a
                    // plain string build. Guarded, clipped, and clearly flagged.
                    d["summary"] = WorldSafe.Safe(() => Journal.Truncate(r.ToString(), LabelClipQ));
                    d["opaque"] = true;
                }
            }
            catch (Exception e)
            {
                d["error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 120);
                d["opaque"] = true;
            }
            return d;
        }

        private const int LabelClipQ = 200;

        // What the quest DEMANDS, in the game's own words: the requirement
        // boxes MainTabWindow_Quests draws from
        // QuestPart_RequirementsToAccept.CanAccept().
        private static List<object> Requirements(Quest q)
        {
            var list = new List<object>();
            try
            {
                var parts = q.PartsListForReading;
                for (int i = 0; i < parts.Count && list.Count < PartCap; i++)
                {
                    if (!(parts[i] is QuestPart_RequirementsToAccept req)) continue;
                    var d = new Dictionary<string, object> { ["part"] = req.GetType().Name };
                    try
                    {
                        var rep = req.CanAccept();
                        d["met"] = rep.Accepted;
                        d["reason"] = rep.Accepted ? null : (object)rep.Reason;
                    }
                    catch (Exception e)
                    {
                        d["met"] = null;
                        d["error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 120);
                    }
                    try { d["show_in_requirement_box"] = req.ShowInRequirementBox; } catch { }
                    list.Add(d);
                }
            }
            catch { }
            return list;
        }

        // The parts, by type + state, plus the expiry clock the tab shows.
        // A QuestPart subclass is arbitrary third-party code, so nothing here
        // calls a virtual beyond `State` and the two delay members, each guarded.
        private static List<object> Parts(Quest q, bool full)
        {
            var list = new List<object>();
            try
            {
                var parts = q.PartsListForReading;
                for (int i = 0; i < parts.Count && list.Count < PartCap; i++)
                {
                    var p = parts[i];
                    if (p == null) continue;
                    var d = new Dictionary<string, object> { ["type"] = p.GetType().Name };
                    if (full) d["type_full"] = p.GetType().FullName;
                    if (p is QuestPartActivable act)
                        d["state"] = WorldSafe.Safe(() => act.State.ToString());
                    if (p is QuestPart_Delay delay)
                    {
                        d["delay_ticks"] = delay.delayTicks;
                        d["ticks_left"] = WorldSafe.SafeObj(() => (object)delay.TicksLeft);
                        d["is_bad"] = delay.isBad;
                        d["expiry_info"] = delay.expiryInfoPart;
                    }
                    if (p is QuestPart_Choice ch) d["choices"] = ch.choices?.Count ?? 0;
                    list.Add(d);
                }
                if (parts.Count > list.Count)
                    list.Add(new Dictionary<string, object>
                    {
                        ["more"] = parts.Count - list.Count,
                    });
            }
            catch { }
            return list;
        }

        private static List<object> Targets(Quest q)
        {
            var list = new List<object>();
            try
            {
                foreach (var t in q.QuestLookTargets)
                {
                    if (list.Count >= PartCap) break;
                    if (!t.IsValid) continue;
                    var d = new Dictionary<string, object>();
                    if (t.HasThing && t.Thing != null)
                    {
                        d["thing"] = WorldSafe.Safe(() => t.Thing.LabelShort);
                        d["id"] = t.Thing.thingIDNumber;
                    }
                    if (t.Cell.IsValid && t.Map != null) d["at"] = Positions.Out(t.Cell);
                    if (d.Count > 0) list.Add(d);
                }
            }
            catch { }
            return list;
        }
    }
}
