using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ==================== git-bug 5cb1f9f / 9227839 =========================
    // THE NAMING DIALOG — the one force-pausing window that answers itself.
    //
    // `RimWorld/Faction.FactionTick` raises it, on a periodic check:
    //
    //     if (Find.TickManager.TicksGame % 1000 == 200 && IsPlayer)
    //     { ... Find.WindowStack.Add(new Dialog_NamePlayerFactionAndSettlement(...)); }
    //
    // and which of its four branches fires is decided by
    // `RimWorld/NamePlayerFactionAndSettlementUtility.CanNameFactionNow()` and
    // `CanNameSettlementNow(settlement)`, whose bodies test
    // `!Faction.OfPlayer.HasName` and `!factionBase.namedByPlayer` against 4.3
    // days elapsed (plus `CanNameAnythingNow()`: a player home map is current,
    // >= 2 spawned free colonists, no active threat, `!GameEnder.gameEnding`).
    //
    // NOTHING IN THAT PREDICATE IS ABOUT THE WINDOW. So a dialog that is CLOSED
    // rather than ANSWERED is raised again 1,000 ticks later, forever —
    // ~940 dismiss-and-halt cycles over the remainder of an M1 run, measured
    // (5cb1f9f). `dialog-dismiss` reports `removed:true` and is telling the
    // truth about the window and nothing at all about the decision.
    //
    // THE ANSWER IS ALREADY IN THE WINDOW. Every `Dialog_GiveName` subclass
    // runs its own `nameGenerator()` in its constructor —
    // `NameGenerator.GenerateName(Faction.OfPlayer.def.factionNameMaker,
    // IsValidName)` — so `curName` / `curSecondName` arrive PRE-FILLED with
    // names the game generated and its own validator already accepted.
    // Accepting them is a complete answer; nothing here has to invent a name.
    //
    // WHAT "ACCEPT" IS. `RimWorld/Dialog_GiveName.DoWindowContents`, OK branch:
    //
    //     string text2 = curName?.Trim();
    //     string text3 = curSecondName?.Trim();
    //     if (IsValidName(text2) && (!useSecondName || IsValidSecondName(text3)))
    //     {
    //         if (useSecondName) { Named(text2); NamedSecond(text3);
    //                              Messages.Message(gainedNameMessageKey…); }
    //         else               { Named(text2); Messages.Message(…); }
    //         Find.WindowStack.TryRemove(this);
    //     }
    //     else Messages.Message(invalidNameMessageKey.Translate(), …);
    //     Event.current.Use();
    //
    // REPRODUCED, not called: the method's FIRST statement is
    // `if (Event.current.type == EventType.KeyDown …)`, and `Event.current` is
    // null outside OnGUI, so invoking it from GameComponentUpdate NREs before
    // it reaches anything. Kept: both validators, in the widget's own order,
    // then `Named`/`NamedSecond`, then TryRemove. Dropped: `Messages.Message`
    // (presentation — `historical:false`, it reaches the screen and nothing
    // else) and `Event.current.Use()`. Every one of those members is
    // `protected` on `Dialog_GiveName`, so the route is reflection, bound once
    // and tolerantly, exactly as DialogVerbs binds `DiaOption.text`.
    //
    // THE WIDGET GATE IS NOT WAIVED — unlike `dialog-dismiss`, whose waiver is
    // DESIGN's waiver 2. `IsValidName` / `IsValidSecondName` ARE the widget's
    // preconditions and they are called before anything is named, so a refusal
    // here is the game's own refusal and carries the game's own
    // `invalidNameMessageKey`.
    //
    // AND IT RUNS UNPROMPTED, from the advance loop, which is the part that
    // needs arguing. The standing rule is that the agent plays and the mod does
    // not. The carve-out is narrow and it is this: a `Dialog_GiveName` is not a
    // decision the agent CAN take — every protocol route it has either leaves
    // the window up (nothing writes a text field) or removes it and gets it
    // back 1,000 ticks later. An unattended run therefore cannot pass day 4.3
    // without a human at the keyboard, which is what happened in run
    // `m1-20260901`. Answering with the game's own generated value is the
    // minimum act that clears it, and it is journaled as an `action` row like
    // any other mutation, so a run report shows exactly when and to what the
    // colony was named. `config.json`'s `autoAnswerNameDialogs:false` turns it
    // off. CHOOSING the name — the agent supplying its own text through these
    // same validators — is deliberately NOT here; it is filed separately.
    //
    // NULL IS A REAL CASE, not defensive padding: `RimWorld/Dialog_Name-
    // PlayerGravship`'s constructor assigns `curName` INSIDE
    // `if (ModLister.CheckOdyssey(...))`, so without Odyssey the window opens
    // with `curName == null` and vanilla's own OK branch would NRE on
    // `s.Length` inside `IsValidName`. Refused here as `no-current-value`
    // rather than reproduced faithfully.
    internal static partial class PawnActs
    {
        private static bool nameRefTried;
        private static FieldInfo fCurName, fCurSecondName, fUseSecondName,
            fInvalidNameKey, fInvalidSecondNameKey, fNameMessageKey;
        private static MethodInfo mIsValidName, mIsValidSecondName, mNamed, mNamedSecond;

        // Bound once, tolerantly. A binding that fails does not throw at load;
        // it makes every accept refuse with `gate:"reflection-unbound"`, which
        // is a reported refusal rather than a silent no-op.
        private static void EnsureNameRefs()
        {
            if (nameRefTried) return;
            nameRefTried = true;
            var t = typeof(Dialog_GiveName);
            try
            {
                fCurName = AccessTools.Field(t, "curName");
                fCurSecondName = AccessTools.Field(t, "curSecondName");
                fUseSecondName = AccessTools.Field(t, "useSecondName");
                fInvalidNameKey = AccessTools.Field(t, "invalidNameMessageKey");
                fInvalidSecondNameKey = AccessTools.Field(t, "invalidSecondNameMessageKey");
                fNameMessageKey = AccessTools.Field(t, "nameMessageKey");
                // Taken from the BASE declaration deliberately: all four are
                // abstract/virtual, so MethodInfo.Invoke dispatches to the
                // subclass override — one binding covers every Dialog_GiveName.
                mIsValidName = AccessTools.Method(t, "IsValidName", new[] { typeof(string) });
                mIsValidSecondName = AccessTools.Method(t, "IsValidSecondName", new[] { typeof(string) });
                mNamed = AccessTools.Method(t, "Named", new[] { typeof(string) });
                mNamedSecond = AccessTools.Method(t, "NamedSecond", new[] { typeof(string) });
            }
            catch (Exception e)
            {
                Journal.EmitWarning("dialog: Dialog_GiveName member binding failed: " + e.Message);
            }
        }

        private static bool NameRefsBound =>
            fCurName != null && fUseSecondName != null
            && mIsValidName != null && mIsValidSecondName != null
            && mNamed != null && mNamedSecond != null;

        // The read half, for `interactions` and for the force-pause payload:
        // what this window is asking and what it would be answered with.
        public static Dictionary<string, object> NameDialogState(Window w)
        {
            EnsureNameRefs();
            var d = new Dictionary<string, object>();
            if (!NameRefsBound) return d;
            try
            {
                bool useSecond = (bool)fUseSecondName.GetValue(w);
                d["current"] = fCurName.GetValue(w) as string;
                d["uses_second_name"] = useSecond;
                if (useSecond) d["second_current"] = fCurSecondName?.GetValue(w) as string;
                d["message_key"] = fNameMessageKey?.GetValue(w) as string;
            }
            catch (Exception e) { d["read_error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 120); }
            return d;
        }

        public static bool IsNameDialog(Window w) => w is Dialog_GiveName;

        // ====================================================================
        // The act. `via` is "verb" or "auto" and rides the journal row, so a
        // run report can separate a name the agent asked for from one the
        // advance loop took on its behalf.
        // ====================================================================
        private static Dictionary<string, object> AcceptNameDialog(Dialog_GiveName w, string via)
        {
            EnsureNameRefs();
            var row = new Dictionary<string, object>
            {
                ["type"] = w.GetType().Name,
                ["type_full"] = w.GetType().FullName,
                ["via"] = via,
                ["ok"] = false,
            };
            if (!NameRefsBound)
            {
                row["gate"] = "reflection-unbound";
                row["reason"] = "Dialog_GiveName's protected members did not bind; nothing was named "
                    + "and the window is still up. Answer it at the keyboard, or `dialog-dismiss` it "
                    + "and expect it back in 1,000 ticks (Faction.FactionTick).";
                return row;
            }

            bool useSecond;
            string first, second;
            try
            {
                useSecond = (bool)fUseSecondName.GetValue(w);
                first = (fCurName.GetValue(w) as string)?.Trim();
                second = (fCurSecondName?.GetValue(w) as string)?.Trim();
            }
            catch (Exception e)
            {
                row["gate"] = "read-failed";
                row["reason"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 200);
                return row;
            }
            row["name"] = first;
            row["uses_second_name"] = useSecond;
            if (useSecond) row["second_name"] = second;

            // Vanilla would NRE here; see the header on Dialog_NamePlayerGravship.
            if (first == null || (useSecond && second == null))
            {
                row["gate"] = "no-current-value";
                row["reason"] = "the window's curName" + (useSecond ? "/curSecondName" : "")
                    + " is null — its constructor never ran the name generator (Dialog_Name"
                    + "PlayerGravship does this when Odyssey is absent). There is nothing to accept; "
                    + "the game's own OK branch would throw on it too.";
                return row;
            }

            bool validFirst, validSecond;
            try
            {
                validFirst = (bool)mIsValidName.Invoke(w, new object[] { first });
                validSecond = !useSecond || (bool)mIsValidSecondName.Invoke(w, new object[] { second });
            }
            catch (Exception e)
            {
                var inner = (e as TargetInvocationException)?.InnerException ?? e;
                row["gate"] = "validator-threw";
                row["reason"] = inner.GetType().Name + ": " + Journal.Truncate(inner.Message, 200);
                return row;
            }
            if (!validFirst || !validSecond)
            {
                // The game's own refusal, in the game's own words.
                var key = (validFirst ? fInvalidSecondNameKey : fInvalidNameKey)?.GetValue(w) as string;
                row["gate"] = "invalid-name";
                row["invalid_message_key"] = key;
                row["reason"] = WorldSafe.Safe(() => key != null ? key.Translate().ToString() : null)
                    ?? "the window's own validator rejected its own generated value";
                row["rejected"] = validFirst ? "second_name" : "name";
                return row;
            }

            // Permadeath is not a hypothetical side effect: NamePlayerFaction-
            // DialogUtility.Named queues a synchronous long event that
            // autosaves under a new name and DELETES the old save file.
            try { row["permadeath"] = Find.GameInfo != null && Find.GameInfo.permadeathMode; }
            catch { }

            try
            {
                mNamed.Invoke(w, new object[] { first });
                if (useSecond) mNamedSecond.Invoke(w, new object[] { second });
            }
            catch (Exception e)
            {
                var inner = (e as TargetInvocationException)?.InnerException ?? e;
                row["gate"] = "named-threw";
                row["reason"] = inner.GetType().Name + ": " + Journal.Truncate(inner.Message, 300);
                Journal.EmitWarning("dialog: " + row["type"] + " Named() threw: " + inner.Message);
                return row;
            }

            bool removed = false;
            try { removed = Find.WindowStack.TryRemove(w, doCloseSound: false); }
            catch (Exception e) { row["close_error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 160); }
            row["removed"] = removed;
            if (!removed && !row.ContainsKey("close_error"))
                row["close_why_not"] = "WindowStack.TryRemove returned false — the naming DID take "
                    + "effect (Named ran first, as in the game's own OK branch), but the window is "
                    + "still on the stack";
            row["ok"] = true;
            row["journal_seq"] = Act("dialog-accept", "accept", w.GetType().Name,
                new Dictionary<string, object>
                {
                    ["name"] = first,
                    ["second_name"] = useSecond ? second : null,
                    ["via"] = via,
                });
            return row;
        }

        // ====================================================================
        // dialog-accept {window?, all?}
        //
        // Answers a text-entry force-pausing dialog by ACCEPTING ITS CURRENT
        // VALUE — the minimal always-safe operation, and the one `dialog-choose`
        // structurally cannot do (it binds `Dialog_NodeTree.curNode`, and a
        // Dialog_GiveName has no DiaOptions at all).
        //
        // Supplying text is NOT accepted here, and a `name` argument is REFUSED
        // rather than ignored: silently accepting the generated name while the
        // caller believes it chose one is the "truthful field answering a
        // different question" failure this verb exists to end.
        // ====================================================================
        [Verb("dialog-accept")]
        public static object DialogAccept(VerbContext ctx)
        {
            const string V = "dialog-accept";
            var stack = Find.WindowStack ?? throw new VerbArgsException("no window stack");
            ctx.Args.RefuseStray(V, new[] { "window", "all" },
                "Supplying a name is not implemented: this verb accepts the value the window "
                + "already holds. Refused rather than dropped, because a caller that thinks it "
                + "chose a name and got the generated one would never find out.");
            bool all = ctx.Args.Bool("all", false);

            var targets = new List<Dialog_GiveName>();
            if (ctx.Args.Has("window"))
            {
                if (all) throw new VerbArgsException("pass `window` or `all`, not both");
                var w = WindowArg(ctx.Args, stack);
                targets.Add(w as Dialog_GiveName
                    ?? throw new VerbArgsException(w.GetType().Name + " is not a Dialog_GiveName — it "
                        + "has no name to accept. `dialog-accept` answers text-entry naming windows "
                        + "(Dialog_NamePlayerFaction, Dialog_NamePlayerSettlement, "
                        + "Dialog_NamePlayerFactionAndSettlement, Dialog_NamePlayerGravship). Use "
                        + "`dialog-choose` for a node tree, `dialog-dismiss` otherwise."));
            }
            else
            {
                var live = new List<Window>(stack.Windows);
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    if (!(live[i] is Dialog_GiveName g)) continue;
                    targets.Add(g);
                    if (!all) break;
                }
            }

            if (targets.Count == 0)
            {
                var none = new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = true,
                    ["accepted"] = new List<object>(),
                    ["reason"] = "no naming dialog is open; nothing to accept",
                    ["action"] = NoStamp(),
                };
                AddStackState(none);
                return none;
            }

            var done = new List<object>();
            long seq = 0;
            foreach (var w in targets)
            {
                var row = AcceptNameDialog(w, "verb");
                if (row.TryGetValue("ok", out var o) && o is bool b && b
                    && seq == 0 && row.TryGetValue("journal_seq", out var js) && js is long l)
                    seq = l;
                done.Add(row);
            }
            bool any = seq != 0 || AnyOk(done);

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = any,
                ["accepted"] = done,
                ["action"] = any ? Stamp(seq) : NoStamp(),
                ["note"] = "accepting is not dismissing: Faction.FactionTick re-raises this window "
                    + "every 1,000 ticks while !Faction.OfPlayer.HasName / !settlement.namedByPlayer, "
                    + "so only a name that STUCK clears the wedge. Check `blocking` below, and that "
                    + "no Dialog_GiveName returns within 1,000 ticks. The naming itself is real "
                    + "colony state: the faction name is scribed, and under permadeath "
                    + "NamePlayerFactionDialogUtility.Named autosaves under a new filename and "
                    + "deletes the old save.",
            };
            AddStackState(d);
            return d;
        }

        // ====================================================================
        // The advance loop's sweep. Main thread, from TimeDriver.Step, and only
        // when something is already force-pausing — see this file's header for
        // why the mod answers this one window unprompted.
        //
        // The failure cap is not defensive padding: a window whose own generated
        // name fails its own validator (or a binding that never bound) would
        // otherwise be re-attempted at the head of every advance for the rest of
        // the session, and each attempt writes a journal line. Three tries, one
        // warning, then it is the agent's problem — which is the honest outcome,
        // because at that point a human really is needed.
        // ====================================================================
        private static bool AnyOk(List<object> rows)
        {
            foreach (var r in rows)
                if (r is Dictionary<string, object> d && d.TryGetValue("ok", out var o)
                    && o is bool b && b) return true;
            return false;
        }

        private const int NameSweepFailureCap = 3;
        private static int nameSweepFailures;

        public static List<object> AutoAnswerNameDialogs(WindowStack stack)
        {
            if (stack == null || nameSweepFailures >= NameSweepFailureCap) return null;
            List<object> done = null;
            var live = new List<Window>(stack.Windows);
            for (int i = live.Count - 1; i >= 0; i--)
            {
                if (!(live[i] is Dialog_GiveName g)) continue;
                var row = AcceptNameDialog(g, "auto");
                bool ok = row.TryGetValue("ok", out var o) && o is bool b && b;
                if (!ok && ++nameSweepFailures >= NameSweepFailureCap)
                {
                    row.TryGetValue("gate", out var gate);
                    Journal.EmitWarning("dialog: auto-accept of " + g.GetType().Name + " failed "
                        + NameSweepFailureCap + " times (" + (gate ?? "unknown") + "); giving up for "
                        + "this session — naming dialogs will halt the advance from here on");
                }
                (done ?? (done = new List<object>())).Add(row);
            }
            return done;
        }
    }
}
