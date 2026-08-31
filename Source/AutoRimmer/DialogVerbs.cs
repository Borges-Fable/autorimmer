using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ======================================================= spec 3.5 =========
    // LETTERS AND DIALOGS — answering what is awaiting input, headless.
    //
    // 2.4's `interactions` is the READ: every open letter with its exact option
    // labels, and every window on the stack. This file is the HANDS for the
    // same surface, and its first duty is not convenience — it is UN-WEDGING.
    // Spec 1.7 shipped the halt: an `advance` under a force-pausing modal
    // returns `reason:"dialog"` at 0 ticks instead of ticking underneath it
    // (which silently killed every timing-out letter for the rest of the
    // session, because `Verse/LetterStack.OpenAutomaticLetters` opens with
    // `if (Find.WindowStack.WindowsForcePause) { return; }`). 1.7 stops and
    // reports; NOTHING clears the stack, so after a dialog halt every
    // subsequent advance halts at 0 ticks until a human intervenes. These verbs
    // are what clears it.
    //
    // ================== FIVE TRAPS, ALL VERIFIED BY MEMBER ===================
    //
    // 1. `Verse/DiaOption.Activate()` DOES NOT CHECK `disabled`. The gate is
    //    one line up, in `DiaOption.OptOnGUI`:
    //        else if (Widgets.ButtonText(rect, text, drawBackground: false,
    //                                    !disabled, textColor, active && !disabled))
    //        { Activate(); }
    //    `disabled` is an argument to the Widgets call — TWICE. So a verb that
    //    replays Activate faithfully presses greyed-out buttons no player can
    //    press. Checked here, and refused with the game's own `disabledReason`.
    //
    // 2. `Activate()` BEGINS `if (resolveTree) { OwningDialog.Close(); }`, and
    //    `protected Dialog_NodeTree OwningDialog => (Dialog_NodeTree)dialog;`
    //    where the public `dialog` field is assigned in exactly ONE place —
    //    `Dialog_NodeTree.GotoNode`. `Verse/ChoiceLetter.Choices` is an ITERATOR
    //    that constructs a `new DiaOption(...)` on every enumeration, and EVERY
    //    stock ChoiceLetter option sets `resolveTree = true` (Option_Close,
    //    Option_Reject, Option_JumpToLocation, Option_JumpToLocationAndPostpone,
    //    Option_Postpone, Option_ViewInQuestsTab). So replaying a letter's own
    //    Choices would hit `null.Close()` on the FIRST branch, for essentially
    //    every vanilla letter. Not an edge case — the default. `Replay` below
    //    takes the owner explicitly and never dereferences `dialog`.
    //
    // 3. AND THE SAME FACT MEANS ANSWERING THE LETTER DOES NOT CLOSE THE
    //    WINDOW. Because Choices mints fresh objects, the options inside an
    //    open `Dialog_NodeTreeWithFactionInfo` are DIFFERENT objects from the
    //    ones a letter-side verb enumerates: replaying the action removes the
    //    LETTER while the WINDOW stays up, still forcePause, and the run stays
    //    halted at 0 ticks. So `letter-choose` FIRST looks for the window that
    //    letter opened and routes through ITS option objects (`via:
    //    "open-window"`), which closes the window and removes the letter in one
    //    act. The letter-side path is the fallback for a letter that is not
    //    open, and it reports the window state either way.
    //
    // 4. TWO STOCK OPTIONS DRIVE THE UI INSIDE THEIR OWN ACTION, which collides
    //    head-on with this spec's "zero UI-widget driving" invariant:
    //      * `ChoiceLetter.Option_ViewInQuestsTab` — `Find.MainTabsRoot
    //        .SetCurrentTab(MainButtonDefOf.Quests)` then `.Select(quest)` then
    //        `RemoveLetter(this)`; SetCurrentTab -> ToggleTab -> `Find
    //        .WindowStack.Add(newTab.TabWindow)`, i.e. it puts a window up.
    //      * `ChoiceLetter.Option_JumpToLocation` (and `DeathLetter
    //        .Option_ReadMore`) — `CameraJumper.TryJumpAndSelect(target)` then
    //        `RemoveLetter(this)`.
    //    The letter removal and any quest/faction mutation live in the SAME
    //    closure as the presentation, so they cannot be split apart from
    //    outside. The project's standing rule for a vanilla helper that drags
    //    UI in (3.2's flick tutorial modal, 3.4's `sendMessages:false`) is
    //    "reproduce the model effect, drop the presentation". Reproduced here
    //    as a BEFORE/AFTER WINDOW DIFF rather than by guessing at labels: the
    //    action runs, and any `MainTabWindow` that appeared because of it is
    //    closed again (`EscapeCurrentTab(playSound:false)`) and reported as
    //    `presentation_reverted`. Anything ELSE that appeared — a modded
    //    confirmation, a nested node tree — is REPORTED, never silently closed,
    //    because that is a real decision the agent now owes.
    //
    // 5. `Verse/WindowStack.TryRemove` DOES NOT COPY. Its body is
    //    membership-scan, `OnCloseRequest()`, sound, `PreClose()`,
    //    `windows.Remove(window)`, `PostClose()` — a direct mutation of the live
    //    list. (`WindowStack.WindowStackOnGUI` is the member that copies, into
    //    `windowStackOnGUITmpList`, which is what makes removal safe from inside
    //    OnGUI; `WindowsUpdate` iterates `windows` directly and does not.)
    //    AutoRimmer's verbs run from `GameComponentUpdate`, which is neither
    //    loop, so TryRemove is safe HERE — but for a different reason than
    //    "it copies", and a future caller inside an iteration must not believe
    //    otherwise. Three consequences, all handled below:
    //      * TryRemove returns FALSE when the window is not on the stack OR
    //        when `Window.OnCloseRequest()` refuses. The bool is read and
    //        reported, never discarded.
    //      * TryRemove runs `PreClose()` and `PostClose()`. For a node tree
    //        that is `curNode.PreClose()` and then `closeAction()` — and
    //        `Dialog_NodeTree.closeAction` is ARBITRARY GAME CODE set by
    //        whoever built the dialog. Dismissal is not inert, and the result
    //        says so.
    //      * `doCloseSound:false` suppresses ONLY the sound. Nothing else is
    //        skipped.
    //    Also: `Dialog_NodeTree`'s constructor sets `closeOnCancel = false`, so
    //    "escape" is not what clears one. Never `Notify_PressedCancel` /
    //    `OnCancelKeyPressed` from tick context; TryRemove is the route.
    //
    // ---------------- THE THREE LETTERS THAT BREAK THE CONTRACT --------------
    // The spec's contract is "options by label or index, exactly as 2.4 reports
    // them". A grep for `override void OpenLetter` returns exactly five classes;
    // `Verse/DeathLetter` keeps the contract and three do not:
    //   * `Verse/NewQuestLetter` — OpenLetter switches to the Quests tab,
    //     selects the quest and removes the letter. Its `Choices` are only
    //     ViewInQuestsTab / JumpToLocation / Close. THERE IS NO ACCEPT OPTION,
    //     which is why accept is its own verb in QuestVerbs.cs.
    //   * `RimWorld/ChoiceLetter_GrowthMoment` — extends `LetterWithTimeout`,
    //     not `ChoiceLetter`, so it has no `Choices` MEMBER AT ALL. Reported
    //     `kind:"growth-moment"` and NOT opened: `OpenLetter` calls
    //     `TrySetChoices`, which writes the scribed `passionChoices` /
    //     `traitChoices` through `PassionOptions(...).InRandomOrder()`,
    //     `Rand.Value < 0.35f` and `PawnGenerator.GenerateTraitsFor` — RNG into
    //     scribed fields, unavoidable once opened.
    //   * `Verse/BundleLetter` — `OpenLetter` is
    //     `if (Event.current.button == 0) Find.WindowStack.Add(new FloatMenu(...))`.
    //     `Event.current` is NULL outside OnGUI, so calling it from a verb NREs
    //     on `.button` before it ever reaches the FloatMenu. Refused by name.
    //     Its sibling trap — `LetterStack.BundleLetter`, the GETTER, which
    //     lazily runs `LetterMaker.MakeLetter` -> `GetNextLetterID()` and
    //     permanently advances the `Scribe_Values`-scribed `nextLetterID` — is
    //     never touched here: letters come from `LettersListForReading` only.
    internal static partial class PawnActs
    {
        public const int DiaOptionCap = 24;
        public const int LetterTextClip = 4000;
        public const int DiaLabelClip = 300;

        private static bool diaRefTried;
        private static AccessTools.FieldRef<DiaOption, string> diaTextRef;
        private static FieldInfo diaCurNode;

        // Bound once, tolerantly, exactly as InteractionVerbs and PawnSafe do.
        // `DiaOption.text` is `protected string` with a public `SetText` and no
        // getter; `Dialog_NodeTree.curNode` is `protected DiaNode`. There is no
        // public accessor for either, and both are needed to name a button.
        private static void EnsureDiaRefs()
        {
            if (diaRefTried) return;
            diaRefTried = true;
            try { diaTextRef = AccessTools.FieldRefAccess<DiaOption, string>("text"); }
            catch (Exception e) { Journal.EmitWarning("dialog: DiaOption text field ref failed: " + e.Message); }
            try { diaCurNode = AccessTools.Field(typeof(Dialog_NodeTree), "curNode"); }
            catch (Exception e) { Journal.EmitWarning("dialog: Dialog_NodeTree curNode field failed: " + e.Message); }
        }

        public static string OptText(DiaOption o)
        {
            try { return diaTextRef != null ? diaTextRef(o) : null; }
            catch { return null; }
        }

        public static DiaNode CurNode(Dialog_NodeTree tree)
        {
            try { return diaCurNode?.GetValue(tree) as DiaNode; }
            catch { return null; }
        }

        // ====================================================================
        // letter-read {letter|index}
        //
        // The drill-down `interactions` deliberately clips. Read-only: nothing
        // here calls OpenLetter, which is the one thing on a letter that is
        // never safe from a verb (three of the five overrides misbehave — see
        // the header — and the fourth stacks a forcePause window that halts the
        // next advance).
        // ====================================================================
        [Verb("letter-read")]
        public static object LetterRead(VerbContext ctx)
        {
            EnsureDiaRefs();
            var let = LetterArg(ctx.Args);
            var d = LetterBlock(let, true);
            d["verb"] = "letter-read";
            d["action"] = NoStamp();
            return d;
        }

        // ====================================================================
        // letter-choose {letter|index, option|option_label}
        //
        // Answer a letter. Options are addressed BY INDEX OR LABEL, exactly as
        // 2.4 reports them.
        // ====================================================================
        [Verb("letter-choose")]
        public static object LetterChoose(VerbContext ctx)
        {
            const string V = "letter-choose";
            EnsureDiaRefs();
            var let = LetterArg(ctx.Args);

            if (let is BundleLetter)
                return LetterRefuse(V, let, "bundle-letter",
                    "Verse/BundleLetter.OpenLetter is `if (Event.current.button == 0) "
                    + "Find.WindowStack.Add(new FloatMenu(floatMenuOptions))`",
                    "a BundleLetter has no Choices — it is the stack's \"N more...\" roll-up, and its "
                    + "OpenLetter dereferences Event.current, which is null outside OnGUI. Answer the "
                    + "bundled letters individually; they are all in `interactions`.");

            var choice = let as ChoiceLetter;
            if (choice == null)
                return LetterRefuse(V, let, "not-a-choice-letter",
                    "Verse/ChoiceLetter is the only Letter subclass with a `Choices` member",
                    let is ChoiceLetter_GrowthMoment
                        ? "ChoiceLetter_GrowthMoment extends LetterWithTimeout, not ChoiceLetter, and has "
                          + "no Choices at all — its decision is a custom passion/trait grid "
                          + "(Dialog_GrowthMomentChoices) and is not driven in v1. `letter-dismiss` is "
                          + "also refused for it (CanDismissWithRightClick is false); it clears itself "
                          + "when its timeout passes."
                        : "this letter carries no options — it is informational. Use `letter-dismiss`.");

            // ---- enumerate the letter's own options (the addressing space) --
            var opts = new List<DiaOption>();
            string enumError = null;
            try
            {
                foreach (var o in choice.Choices) { if (o != null) opts.Add(o); if (opts.Count >= DiaOptionCap) break; }
            }
            catch (Exception e)
            {
                // ChoiceLetter.Choices CONSTRUCTS DiaOptions, and
                // Option_ViewInQuestsTab dereferences `quest.name` BEFORE its
                // own null check (WorldSafe Class D). Degrade the letter, not
                // the verb — 2.4 makes the same call.
                enumError = e.GetType().Name + ": " + Journal.Truncate(e.Message, 160);
            }
            if (enumError != null && opts.Count == 0)
                return LetterRefuse(V, let, "choices-threw",
                    "Verse/ChoiceLetter.Choices is an iterator that CONSTRUCTS DiaOptions",
                    "enumerating this letter's options threw: " + enumError);
            if (opts.Count == 0)
                return LetterRefuse(V, let, "no-options",
                    "Verse/ChoiceLetter.Choices yielded nothing",
                    "this letter has no options to choose. Use `letter-dismiss`"
                    + (WorldSafe.SafeObj(() => (object)let.CanDismissWithRightClick) as bool? == false
                        ? " — though " + let.GetType().Name + " refuses that too "
                          + "(CanDismissWithRightClick is false)." : "."));

            int idx = ResolveOptionIndex(ctx.Args, opts.Count, i => OptText(opts[i]));
            var picked = opts[idx];
            string label = OptText(picked);

            // ---- the widget gate (trap 1) ----------------------------------
            if (picked.disabled)
                return LetterRefuse(V, let, "option-disabled",
                    "Verse/DiaOption.OptOnGUI gates the button with "
                    + "`Widgets.ButtonText(rect, text, drawBackground: false, !disabled, textColor, "
                    + "active && !disabled)` and only then calls Activate(); Activate() itself does "
                    + "NOT check `disabled`",
                    "option " + idx + " (\"" + label + "\") is disabled"
                        + (string.IsNullOrEmpty(picked.disabledReason) ? " (the game gives no reason)"
                                                                      : ": " + picked.disabledReason),
                    new Dictionary<string, object>
                    {
                        ["option"] = idx,
                        ["option_label"] = label,
                        ["disabled_reason"] = picked.disabledReason,
                        ["options"] = OptionLines(opts),
                    });

            // ---- trap 3: route through the OPEN window if there is one -----
            var owner = OwningWindowFor(choice);
            DiaOption live = picked;
            string via = "letter-choices";
            if (owner != null)
            {
                var node = CurNode(owner);
                var match = MatchOption(node, label, idx);
                if (match != null)
                {
                    live = match;
                    via = "open-window";
                    if (live.disabled)
                        return LetterRefuse(V, let, "option-disabled",
                            "Verse/DiaOption.OptOnGUI (the live option on the open window is disabled)",
                            "option " + idx + " (\"" + label + "\") is disabled on the open dialog"
                                + (string.IsNullOrEmpty(live.disabledReason) ? "" : ": " + live.disabledReason));
                }
            }

            var outcome = Replay(live, via == "open-window" ? owner : null, "letter option");

            bool stillListed = false;
            try { stillListed = Find.LetterStack.LettersListForReading.Contains(let); } catch { }

            long seq = Act(V, "choose", "Letter_" + let.ID, new Dictionary<string, object>
            {
                ["letter"] = let.ID,
                ["letter_def"] = let.def?.defName,
                ["option"] = idx,
                ["option_label"] = label,
                ["via"] = via,
                ["letter_removed"] = !stillListed,
            });

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["letter"] = let.ID,
                ["label"] = WorldSafe.Safe(() => Journal.Truncate(let.Label.ToString(), DiaLabelClip)),
                ["option"] = idx,
                ["option_label"] = label,
                // "open-window" means the window's OWN option object was
                // pressed, so the dialog closed and the letter went with it.
                // "letter-choices" means the letter was answered while closed,
                // and no window was ever involved.
                ["via"] = via,
                ["letter_removed"] = !stillListed,
                ["options"] = OptionLines(opts),
                ["action"] = Stamp(seq),
            };
            if (enumError != null) d["options_error"] = enumError;
            foreach (var kv in outcome) d[kv.Key] = kv.Value;
            AddStackState(d);
            // The acceptance line, made explicit rather than left for the
            // caller to derive from `force_pause.count`: an answered letter
            // that leaves a window up leaves the run halted at 0 ticks, and
            // that is a different outcome from a clean answer.
            if (d.TryGetValue("blocking", out var b) && b is bool blocking && blocking)
                d["still_blocked"] = via == "open-window"
                    ? "the letter's own window was closed, but ANOTHER force-pausing window is still up "
                      + "— see force_pause.windows. `advance` will halt at 0 ticks until it is cleared "
                      + "(`dialog-dismiss`, or `dialog-choose` if it is a node tree)."
                    : "this letter was answered from the letter side and a force-pausing window is "
                      + "still up. If it is this letter's dialog, the label match against its DiaNode "
                      + "text did not hold (a modded letter that builds its node from something other "
                      + "than ChoiceLetter.Text). Clear it with `dialog-dismiss`.";
            return d;
        }

        // ====================================================================
        // letter-dismiss {letter|index}
        //
        // THE GATE LIVES IN THE WIDGET: `Verse/Letter.DrawButtonAt` is
        //     if (CanDismissWithRightClick && Event.current.type ==
        //         EventType.MouseDown && Event.current.button == 1 &&
        //         Mouse.IsOver(rect))
        //     { SoundDefOf.Click.PlayOneShotOnCamera();
        //       Find.LetterStack.RemoveLetter(this); Event.current.Use(); }
        // — so `CanDismissWithRightClick` is the whole precondition, and the
        // model effect is exactly `RemoveLetter`. The sound is presentation and
        // is dropped. `BundleLetter` and `ChoiceLetter_GrowthMoment` both
        // override it to false and are refused with that as the reason.
        // ====================================================================
        [Verb("letter-dismiss")]
        public static object LetterDismiss(VerbContext ctx)
        {
            const string V = "letter-dismiss";
            EnsureDiaRefs();
            var let = LetterArg(ctx.Args);

            bool can = true;
            try { can = let.CanDismissWithRightClick; } catch { }
            if (!can)
                return LetterRefuse(V, let, "cannot-dismiss",
                    "Verse/Letter.DrawButtonAt dismisses only `if (CanDismissWithRightClick && "
                    + "Event.current.type == EventType.MouseDown && Event.current.button == 1 && "
                    + "Mouse.IsOver(rect))`",
                    let.GetType().Name + " overrides CanDismissWithRightClick to false, so no "
                    + "right-click dismissal is available to a player either"
                    + (let is ChoiceLetter_GrowthMoment
                        ? " — a growth moment is answered or left to time out"
                        : let is BundleLetter
                          ? " — dismiss the bundled letters individually"
                          : ""));

            Find.LetterStack.RemoveLetter(let);

            bool gone = true;
            try { gone = !Find.LetterStack.LettersListForReading.Contains(let); } catch { }

            long seq = Act(V, "dismiss", "Letter_" + let.ID, new Dictionary<string, object>
            {
                ["letter"] = let.ID,
                ["letter_def"] = let.def?.defName,
                ["label"] = WorldSafe.Safe(() => let.Label.ToString()),
            });

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                // Read the write back: RemoveLetter is void and also fires
                // `let.Removed()`, which a subclass may override.
                ["ok"] = gone,
                ["letter"] = let.ID,
                ["label"] = WorldSafe.Safe(() => Journal.Truncate(let.Label.ToString(), DiaLabelClip)),
                ["removed"] = gone,
                ["action"] = Stamp(seq),
                ["note"] = "dismissing the letter does NOT close a window it already opened — see "
                    + "`force_pause` below and use `dialog-dismiss` if it is non-zero.",
            };
            AddStackState(d);
            return d;
        }

        // ====================================================================
        // dialog-choose {option|option_label, window?}
        //
        // Press an option on an OPEN node-tree window. This is the un-wedger:
        // it is the only path that presses the option objects whose `dialog`
        // field is set, so `resolveTree` genuinely closes the window and the
        // option's own action (which is where a letter's removal lives) runs.
        //
        // A SECOND, PRIVATE WIDGET GATE IS DELIBERATELY WAIVED, and the reason
        // is recorded rather than left implicit. `Dialog_NodeTree.DrawNode`
        // calls `curNode.options[i].OptOnGUI(rect3, InteractiveNow)`, and
        // `private bool InteractiveNow => Time.realtimeSinceStartup >=
        // makeInteractiveAtTime;` with `makeInteractiveAtTime =
        // RealTime.LastRealTime + 1f` when the dialog was built with
        // `delayInteractivity: true`. That `active` argument is ANDed into the
        // same `Widgets.ButtonText` call as `!disabled`, so it IS a real gate on
        // pressing the button. It is waived because it is an anti-misclick
        // delay rather than a game rule, it is WALL-CLOCK rather than tick
        // based, and reproducing it would put real-time dependence into an
        // otherwise deterministic verb. Note that `ChoiceLetter.OpenLetter` and
        // `DeathLetter.OpenLetter` both pass `delayInteractivity: false`, so
        // letters are unaffected either way; the gate matters only for callers
        // that pass true.
        // ====================================================================
        [Verb("dialog-choose")]
        public static object DialogChoose(VerbContext ctx)
        {
            const string V = "dialog-choose";
            EnsureDiaRefs();
            var stack = Find.WindowStack ?? throw new VerbArgsException("no window stack");

            var tree = NodeTreeArg(ctx.Args, stack);
            var node = CurNode(tree);
            if (node == null)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["window"] = tree.GetType().Name,
                    ["gate"] = "no-readable-node",
                    ["reason"] = "Dialog_NodeTree.curNode is protected and the field ref did not bind, or "
                        + "the window has no current node. Use `dialog-dismiss` to clear it.",
                    ["source"] = diaCurNode != null ? "backing-field" : "unavailable",
                    ["action"] = NoStamp(),
                };

            var opts = new List<DiaOption>();
            foreach (var o in node.options) { if (o != null) opts.Add(o); if (opts.Count >= DiaOptionCap) break; }
            if (opts.Count == 0)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["window"] = tree.GetType().Name,
                    ["gate"] = "no-options",
                    ["reason"] = "the current node has no options; use `dialog-dismiss`",
                    ["action"] = NoStamp(),
                };

            int idx = ResolveOptionIndex(ctx.Args, opts.Count, i => OptText(opts[i]));
            var picked = opts[idx];
            string label = OptText(picked);

            if (picked.disabled)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["window"] = tree.GetType().Name,
                    ["option"] = idx,
                    ["option_label"] = label,
                    ["gate"] = "option-disabled",
                    ["gate_cite"] = "Verse/DiaOption.OptOnGUI gates the button with "
                        + "`Widgets.ButtonText(..., !disabled, textColor, active && !disabled)` and only "
                        + "then calls Activate(); Activate() itself does NOT check `disabled`",
                    ["reason"] = "option " + idx + " (\"" + label + "\") is disabled"
                        + (string.IsNullOrEmpty(picked.disabledReason) ? " (the game gives no reason)"
                                                                      : ": " + picked.disabledReason),
                    ["disabled_reason"] = picked.disabledReason,
                    ["options"] = OptionLines(opts),
                    ["action"] = NoStamp(),
                };

            string windowType = tree.GetType().Name;
            var outcome = Replay(picked, tree, "dialog option");

            long seq = Act(V, "choose", windowType, new Dictionary<string, object>
            {
                ["window"] = windowType,
                ["window_full"] = tree.GetType().FullName,
                ["option"] = idx,
                ["option_label"] = label,
            });

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["window"] = windowType,
                ["window_full"] = tree.GetType().FullName,
                ["option"] = idx,
                ["option_label"] = label,
                ["source"] = diaTextRef != null ? "backing-field" : "unavailable",
                ["action"] = Stamp(seq),
            };
            foreach (var kv in outcome) d[kv.Key] = kv.Value;
            // The node the dialog moved to, when it did not close.
            var after = CurNode(tree);
            if (after != null && after != node && stack.IsOpen(tree))
                d["now_showing"] = new Dictionary<string, object>
                {
                    ["text"] = WorldSafe.Safe(() => Journal.Truncate(after.text.ToString(), LetterTextClip)),
                    ["options"] = OptionLines(after.options),
                };
            AddStackState(d);
            return d;
        }

        // ====================================================================
        // dialog-dismiss {window?, all?}
        //
        // The esc-equivalent that always works — the spec's "`dialog dismiss`
        // must always work" for an unknown modded dialog that `interactions`
        // could only report as opaque. Defaults to the TOP force-pausing
        // window, which is the one wedging the run; `all:true` clears the whole
        // force-pause stack in one call.
        //
        // NOT INERT, and the result says so: TryRemove runs `PreClose()` and
        // `PostClose()`, and `Dialog_NodeTree.PostClose` calls `closeAction()`,
        // which is arbitrary game code set by whoever built the dialog.
        // ====================================================================
        [Verb("dialog-dismiss")]
        public static object DialogDismiss(VerbContext ctx)
        {
            const string V = "dialog-dismiss";
            EnsureDiaRefs();
            var stack = Find.WindowStack ?? throw new VerbArgsException("no window stack");
            bool all = ctx.Args.Bool("all", false);

            var targets = new List<Window>();
            if (ctx.Args.Has("window"))
            {
                targets.Add(WindowArg(ctx.Args, stack));
                if (all) throw new VerbArgsException("pass `window` or `all`, not both");
            }
            else
            {
                // Top-down over a SNAPSHOT: TryRemove mutates the live list.
                var live = new List<Window>(stack.Windows);
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    var w = live[i];
                    if (w == null || w is ImmediateWindow || !w.forcePause) continue;
                    targets.Add(w);
                    if (!all) break;
                }
            }

            if (targets.Count == 0)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = true,
                    ["dismissed"] = new List<object>(),
                    ["reason"] = "no force-pausing window is up; nothing to dismiss",
                    ["action"] = NoStamp(),
                    ["force_pause"] = WorldSafe.SafeObj(() => TimeDriver.ForcePausePayload(stack)),
                };

            var done = new List<object>();
            bool anyRemoved = false;
            foreach (var w in targets)
            {
                var row = new Dictionary<string, object>
                {
                    ["type"] = w.GetType().Name,
                    ["type_full"] = w.GetType().FullName,
                    ["force_pause"] = w.forcePause,
                };
                bool removed = false;
                try { removed = stack.TryRemove(w, doCloseSound: false); }
                catch (Exception e) { row["error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 160); }
                row["removed"] = removed;
                if (!removed && !row.ContainsKey("error"))
                    // The bool is READ, not discarded: a "dismissed" that
                    // silently did nothing is the exact failure mode the order-
                    // honesty issue (4087644) exists about.
                    row["why_not"] = "WindowStack.TryRemove returned false — the window was not on the "
                        + "stack, or its Window.OnCloseRequest() refused (the default returns true; only "
                        + "RimWorld/Page_ModsConfig overrides it)";
                anyRemoved |= removed;
                done.Add(row);
            }

            long seq = anyRemoved
                ? Act(V, "dismiss", targets[0].GetType().Name,
                    new Dictionary<string, object> { ["count"] = done.Count })
                : 0;

            var d = new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = anyRemoved,
                ["dismissed"] = done,
                ["action"] = anyRemoved ? Stamp(seq) : NoStamp(),
                ["note"] = "dismissal is NOT inert: WindowStack.TryRemove runs PreClose() and "
                    + "PostClose(), and Dialog_NodeTree.PostClose calls `closeAction()`, which is "
                    + "arbitrary game code set by whoever built the dialog. Only the SOUND is "
                    + "suppressed by doCloseSound:false. Note also that Dialog_NodeTree sets "
                    + "closeOnCancel = false, so pressing escape would not have cleared it.",
            };
            AddStackState(d);
            return d;
        }

        // ======================== the option walker =========================
        //
        // `Verse/DiaOption.Activate()`, reproduced rather than called (it is
        // `protected`, and everything it does is public):
        //
        //     protected void Activate()
        //     {
        //         if (clickSound != null && !resolveTree) clickSound.PlayOneShotOnCamera();
        //         if (resolveTree) OwningDialog.Close();
        //         if (action != null) action();
        //         if (linkLateBind != null) OwningDialog.GotoNode(linkLateBind());
        //         else if (link != null) OwningDialog.GotoNode(link);
        //     }
        //
        // Differences, each deliberate:
        //   * the click SOUND is dropped (presentation);
        //   * `OwningDialog` is never dereferenced — the owner is passed in,
        //     and a null owner means "this option was minted by an iterator and
        //     its `dialog` field was never set by GotoNode", which is the
        //     default case for a letter's Choices (trap 2);
        //   * `Window.Close()` is `Find.WindowStack.TryRemove(this, doCloseSound)`,
        //     so closing goes through TryRemove and its bool is READ;
        //   * a window diff around `action()` reverts the presentation half of
        //     the two UI-driving stock options and reports everything else that
        //     appeared (trap 4).
        //
        // `onNode` / `onResolve` are how a HEADLESS tree navigates: CommsVerbs
        // walks a `FactionDialogMaker.FactionDialogFor` node graph with NO
        // window on the stack at all, so there is nothing to `GotoNode` and
        // nothing to `Close`. One walker, three owners (window, headless
        // session, letter-with-no-window), so the disabled check and the
        // presentation revert cannot drift between them.
        private static Dictionary<string, object> Replay(DiaOption opt, Dialog_NodeTree owner, string what,
            Action<DiaNode> onNode = null, Action onResolve = null)
        {
            var d = new Dictionary<string, object>
            {
                ["closes_dialog"] = opt.resolveTree,
                ["opens_node"] = opt.link != null || opt.linkLateBind != null,
                ["has_action"] = opt.action != null,
            };
            var stack = Find.WindowStack;

            // 1. resolveTree -> close, FIRST, exactly as Activate orders it.
            if (opt.resolveTree)
            {
                if (owner != null)
                {
                    bool closed = false;
                    try { closed = stack.TryRemove(owner, doCloseSound: false); }
                    catch (Exception e) { d["close_error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 160); }
                    d["dialog_closed"] = closed;
                    if (!closed && !d.ContainsKey("close_error"))
                        d["close_why_not"] = "WindowStack.TryRemove returned false (not on the stack, or "
                            + "Window.OnCloseRequest() refused)";
                }
                else if (onResolve != null)
                {
                    onResolve();
                    d["dialog_closed"] = true;
                    d["session_ended"] = true;
                }
                else
                {
                    d["dialog_closed"] = false;
                    d["close_skipped"] = "this option's `dialog` field was never set — Dialog_NodeTree"
                        + ".GotoNode is the only place that assigns it, and ChoiceLetter.Choices mints "
                        + "fresh DiaOptions. Replaying Activate() here would have NRE'd on "
                        + "OwningDialog.Close().";
                }
            }

            // 2. the action — the model effect, and the presentation with it.
            if (opt.action != null)
            {
                var before = Snapshot(stack);
                try { opt.action(); }
                catch (Exception e)
                {
                    d["action_error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 300);
                    Journal.EmitWarning("dialog: " + what + " action threw: " + e.Message);
                }
                var opened = Diff(stack, before);

                // The presentation half of Option_ViewInQuestsTab: SetCurrentTab
                // -> ToggleTab -> Find.WindowStack.Add(newTab.TabWindow). Close
                // it again; a main tab left open is a window the agent did not
                // ask for and would have to reason about in `interactions`.
                var reverted = new List<object>();
                for (int i = opened.Count - 1; i >= 0; i--)
                {
                    var w = opened[i];
                    if (!(w is MainTabWindow)) continue;
                    try { Find.MainTabsRoot.EscapeCurrentTab(playSound: false); } catch { }
                    reverted.Add(w.GetType().Name);
                    opened.RemoveAt(i);
                }
                if (reverted.Count > 0)
                {
                    d["presentation_reverted"] = reverted;
                    d["presentation_note"] = "the option's action opened a main tab "
                        + "(ChoiceLetter.Option_ViewInQuestsTab does Find.MainTabsRoot.SetCurrentTab -> "
                        + "WindowStack.Add(tab.TabWindow)); the model half of the action ran and the tab "
                        + "was closed again. Camera/selection moves (CameraJumper.TryJumpAndSelect in "
                        + "Option_JumpToLocation) are presentation too and are not undone — they touch "
                        + "no game state.";
                }
                if (opened.Count > 0)
                {
                    // NOT auto-closed: a window the action raised is a real
                    // decision the agent now owes, and silently removing it
                    // would answer it for them.
                    var list = new List<object>();
                    foreach (var w in opened)
                        list.Add(new Dictionary<string, object>
                        {
                            ["type"] = w.GetType().Name,
                            ["type_full"] = w.GetType().FullName,
                            ["force_pause"] = w.forcePause,
                        });
                    d["opened_windows"] = list;
                    d["opened_windows_note"] = "the option's action put these on the stack and they were "
                        + "left there deliberately — each is a decision. Answer with `dialog-choose` or "
                        + "clear with `dialog-dismiss`.";
                }
            }

            // 3. follow the link. Note the ORDER: Activate closes the dialog
            //    before running the action, so an option that is BOTH
            //    resolveTree and linked would navigate a closed window —
            //    vanilla's own behaviour, reproduced, and reported.
            DiaNode next = null;
            if (opt.linkLateBind != null)
            {
                try { next = opt.linkLateBind(); }
                catch (Exception e) { d["link_error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 200); }
            }
            else if (opt.link != null) next = opt.link;

            if (next != null)
            {
                if (owner != null)
                {
                    try { owner.GotoNode(next); d["went_to_node"] = true; }
                    catch (Exception e) { d["link_error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 200); }
                }
                else if (onNode != null)
                {
                    onNode(next);
                    d["went_to_node"] = true;
                }
                else
                {
                    d["went_to_node"] = false;
                    d["link_skipped"] = "this option links to a follow-up node but has no owning dialog "
                        + "to navigate (see close_skipped). The follow-up is unreachable from the letter "
                        + "side; open-window routing is the path that walks it.";
                    d["link_text"] = WorldSafe.Safe(() => Journal.Truncate(next.text.ToString(), DiaLabelClip));
                }
            }
            return d;
        }

        private static List<Window> Snapshot(WindowStack stack)
        {
            var l = new List<Window>();
            try { if (stack != null) l.AddRange(stack.Windows); } catch { }
            return l;
        }

        private static List<Window> Diff(WindowStack stack, List<Window> before)
        {
            var added = new List<Window>();
            try
            {
                foreach (var w in stack.Windows)
                    if (w != null && !(w is ImmediateWindow) && !before.Contains(w)) added.Add(w);
            }
            catch { }
            return added;
        }

        // 1.7's payload verbatim under `force_pause` — ONE window vocabulary,
        // never a second — so "did that clear the wedge?" is answerable from
        // the same shape `advance`, `status` and `interactions` all publish.
        private static void AddStackState(Dictionary<string, object> d)
        {
            try
            {
                var stack = Find.WindowStack;
                d["force_pause"] = TimeDriver.ForcePausePayload(stack);
                d["blocking"] = stack != null && stack.WindowsForcePause;
            }
            catch { }
        }

        // ============================ addressing ============================

        public static Letter LetterArg(VerbArgs args)
        {
            var stack = Find.LetterStack ?? throw new VerbArgsException("no letter stack (no game loaded?)");
            // LettersListForReading is `=> letters`, the live list. Snapshot:
            // an index is only meaningful against a fixed list.
            var live = new List<Letter>(stack.LettersListForReading);

            if (args.Has("index"))
            {
                int i = args.IntReq("index");
                if (i < 0 || i >= live.Count)
                    throw new VerbArgsException("index must be 0.." + (live.Count - 1)
                        + " (" + live.Count + " letters on the stack; see `interactions`)");
                return live[i] ?? throw new VerbArgsException("letter at index " + i + " is null");
            }
            if (!args.Has("letter"))
                throw new VerbArgsException("missing required arg 'letter' (the letter ID from "
                    + "`interactions`), or 'index' (its position on the stack)");
            int id = args.IntReq("letter");
            foreach (var l in live) if (l != null && l.ID == id) return l;
            throw new VerbArgsException("no letter with ID " + id + " on the stack ("
                + live.Count + " letters; see `interactions`). A letter that timed out or was already "
                + "answered is gone from the stack — LetterStack.LetterStackUpdate reaps anything whose "
                + "CanShowInLetterStack has gone false.");
        }

        // Index OR label, exactly as 2.4 reports them. A label match is
        // case-insensitive and must be unambiguous; ambiguity is an error
        // rather than a coin flip.
        private static int ResolveOptionIndex(VerbArgs args, int count, Func<int, string> labelAt)
        {
            if (args.Has("option"))
            {
                int i = args.IntReq("option");
                if (i < 0 || i >= count)
                    throw new VerbArgsException("option must be 0.." + (count - 1)
                        + " (" + count + " options; see `interactions` or `letter-read`)");
                return i;
            }
            if (args.Has("option_label"))
            {
                string want = args.StrReq("option_label");
                int hit = -1, matches = 0;
                var labels = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    string l = labelAt(i);
                    labels.Add(l);
                    if (l != null && l.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                    { hit = i; matches++; }
                }
                if (matches == 1) return hit;
                if (matches > 1)
                    throw new VerbArgsException("option_label '" + want + "' matches " + matches
                        + " options — use `option` (index). Labels: " + string.Join(" | ", labels.ToArray()));
                throw new VerbArgsException("no option matching '" + want + "'. Labels: "
                    + string.Join(" | ", labels.ToArray()));
            }
            throw new VerbArgsException("missing 'option' (index) or 'option_label' (substring)");
        }

        // The window a letter is currently showing in, or null.
        //
        // There is no back-reference from a Letter to its Window, so this
        // matches on the one thing that is deterministic: `ChoiceLetter
        // .OpenLetter` builds `new DiaNode(text)` from the letter's own `Text`
        // and stacks a `Dialog_NodeTreeWithFactionInfo` around it, so the open
        // window's curNode text IS the letter's text. `DeathLetter.OpenLetter`
        // appends a battle-log tail to the same text, hence StartsWith rather
        // than equality. A modded letter that builds its node from something
        // else simply does not match, and the verb falls back to the letter
        // side and reports `force_pause` so the caller can still clear it.
        private static Dialog_NodeTree OwningWindowFor(ChoiceLetter let)
        {
            try
            {
                string want = let.Text.ToString();
                if (string.IsNullOrEmpty(want)) return null;
                var stack = Find.WindowStack;
                if (stack == null) return null;
                var live = new List<Window>(stack.Windows);
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    if (!(live[i] is Dialog_NodeTree tree)) continue;
                    var node = CurNode(tree);
                    if (node == null) continue;
                    string have = node.text.ToString();
                    if (have != null && have.StartsWith(want, StringComparison.Ordinal)) return tree;
                }
            }
            catch { }
            return null;
        }

        // The live option on the open window that corresponds to the letter's
        // freshly-minted one. Label first (the words on the button are what the
        // agent addressed), index as the fallback.
        private static DiaOption MatchOption(DiaNode node, string label, int idx)
        {
            if (node?.options == null) return null;
            if (!string.IsNullOrEmpty(label))
                foreach (var o in node.options)
                    if (o != null && string.Equals(OptText(o), label, StringComparison.Ordinal)) return o;
            if (idx >= 0 && idx < node.options.Count) return node.options[idx];
            return null;
        }

        private static Dialog_NodeTree NodeTreeArg(VerbArgs args, WindowStack stack)
        {
            var w = args.Has("window") ? WindowArg(args, stack) : null;
            if (w != null)
                return w as Dialog_NodeTree
                    ?? throw new VerbArgsException(w.GetType().Name + " is not a Dialog_NodeTree — it has "
                        + "no options to press. Use `dialog-dismiss`, or read `interactions` for its "
                        + "`kind` (message-box windows and opaque modded windows are dismiss-only in v1).");

            var live = new List<Window>(stack.Windows);
            for (int i = live.Count - 1; i >= 0; i--)
                if (live[i] is Dialog_NodeTree t && t.forcePause) return t;
            for (int i = live.Count - 1; i >= 0; i--)
                if (live[i] is Dialog_NodeTree t2) return t2;
            throw new VerbArgsException("no Dialog_NodeTree is open (see `interactions`)");
        }

        // `window` is the SHORT class name, the assertable identifier
        // `interactions` and 1.7's force-pause payload both publish as `type`.
        private static Window WindowArg(VerbArgs args, WindowStack stack)
        {
            string want = args.StrReq("window");
            var live = new List<Window>(stack.Windows);
            var names = new List<string>();
            Window hit = null;
            int matches = 0;
            for (int i = live.Count - 1; i >= 0; i--)
            {
                var w = live[i];
                if (w == null || w is ImmediateWindow) continue;
                var t = w.GetType();
                names.Add(t.Name);
                if (string.Equals(t.Name, want, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.FullName, want, StringComparison.OrdinalIgnoreCase))
                { hit = w; matches++; }
            }
            if (matches == 1) return hit;
            if (matches > 1)
                throw new VerbArgsException(matches + " windows of type '" + want + "' are open; "
                    + "use the full type name or `dialog-dismiss {all:true}`");
            throw new VerbArgsException("no window of type '" + want + "' is open. Open: "
                + (names.Count == 0 ? "(none)" : string.Join(", ", names.ToArray())));
        }

        // ========================== serialization ===========================

        private static List<object> OptionLines(List<DiaOption> opts)
        {
            var l = new List<object>();
            for (int i = 0; i < opts.Count && i < DiaOptionCap; i++)
                if (opts[i] != null) l.Add(OptionLine(opts[i], i));
            return l;
        }

        private static Dictionary<string, object> OptionLine(DiaOption o, int i)
            => new Dictionary<string, object>
            {
                ["index"] = i,
                // The literal words on the button. Null means the field ref did
                // not bind (see `source`), not that the button is unlabelled.
                ["label"] = Journal.Truncate(OptText(o), DiaLabelClip),
                ["disabled"] = o.disabled,
                ["disabled_reason"] = o.disabledReason,
                ["closes"] = o.resolveTree,
                ["opens_node"] = o.link != null || o.linkLateBind != null,
                ["has_action"] = o.action != null,
            };

        private static Dictionary<string, object> LetterBlock(Letter let, bool full)
        {
            var d = new Dictionary<string, object>
            {
                ["letter"] = let.ID,
                ["def"] = let.def?.defName,
                ["label"] = WorldSafe.Safe(() => Journal.Truncate(let.Label.ToString(), DiaLabelClip)),
                ["type"] = let.GetType().Name,
                ["type_full"] = let.GetType().FullName,
                ["arrival_tick"] = let.arrivalTick,
                ["faction"] = let.relatedFaction?.Name,
                ["can_show"] = WorldSafe.SafeObj(() => (object)let.CanShowInLetterStack),
                ["dismissable"] = WorldSafe.SafeObj(() => (object)let.CanDismissWithRightClick),
            };
            var timed = let as LetterWithTimeout;
            if (timed != null && WorldSafe.SafeObj(() => (object)timed.TimeoutActive) as bool? == true)
            {
                int now = 0;
                try { now = Find.TickManager.TicksGame; } catch { }
                d["timeout"] = new Dictionary<string, object>
                {
                    ["disappear_at_tick"] = timed.disappearAtTick,
                    ["ticks_left"] = timed.disappearAtTick - now,
                    // ShouldAutomaticallyOpenLetter IS this flag: on its last
                    // tick the letter opens ITSELF from LetterStackTick, which
                    // is 1.7's whole mechanism and halts an advance.
                    ["opens_itself_next_tick"] = WorldSafe.SafeObj(() => (object)timed.LastTickBeforeTimeout) ?? false,
                    ["passed"] = WorldSafe.SafeObj(() => (object)timed.TimeoutPassed) ?? false,
                };
            }

            if (let is ChoiceLetter_GrowthMoment growth)
            {
                d["kind"] = "growth-moment";
                d["pawn"] = WorldSafe.Safe(() => growth.pawn?.LabelShortCap.ToString());
                d["choice_made"] = growth.choiceMade;
                d["text"] = WorldSafe.Safe(() => Journal.Truncate(growth.text.ToString(), LetterTextClip));
                d["opaque"] = true;
                d["note"] = "ChoiceLetter_GrowthMoment extends LetterWithTimeout, NOT ChoiceLetter — it "
                    + "has no Choices member and no letter option to press. Its decision is a custom "
                    + "passion/trait grid (Dialog_GrowthMomentChoices) and is NOT driven in v1: opening "
                    + "it runs TrySetChoices, which rolls the shared Rand into the scribed "
                    + "passionChoices/traitChoices fields, and CanDismissWithRightClick is false so it "
                    + "cannot be dismissed either. It clears itself when its timeout passes.";
                return d;
            }
            if (let is BundleLetter)
            {
                d["kind"] = "bundle";
                d["opaque"] = true;
                d["note"] = "BundleLetter is the stack's \"N more...\" roll-up. Its OpenLetter reads "
                    + "Event.current.button, which NREs outside OnGUI, and it has no Choices. The "
                    + "bundled letters are each listed individually in `interactions`; answer those. "
                    + "(Note also that LetterStack.BundleLetter — the GETTER — burns a scribed letter "
                    + "ID via LetterMaker.MakeLetter -> GetNextLetterID; AutoRimmer never calls it.)";
                return d;
            }

            var choice = let as ChoiceLetter;
            if (choice == null)
            {
                d["kind"] = "plain";
                return d;
            }
            d["kind"] = "choice";
            d["title"] = choice.title;
            d["radio_mode"] = choice.radioMode;
            d["quest"] = choice.quest == null ? null : (object)choice.quest.id;
            d["quest_name"] = choice.quest?.name;
            d["text"] = WorldSafe.Safe(() => Journal.Truncate(choice.Text.ToString(),
                full ? LetterTextClip : 400));

            var opts = new List<DiaOption>();
            try
            {
                foreach (var o in choice.Choices) { if (o != null) opts.Add(o); if (opts.Count >= DiaOptionCap) break; }
                d["options"] = OptionLines(opts);
            }
            catch (Exception e)
            {
                d["options"] = OptionLines(opts);
                d["options_error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 160);
            }
            d["source"] = diaTextRef != null ? "backing-field" : "unavailable";

            if (let is NewQuestLetter)
                d["note"] = "NewQuestLetter has NO accept option — its Choices are only "
                    + "ViewInQuestsTab / JumpToLocation / Close, and its OpenLetter switches to the "
                    + "Quests tab and removes the letter. Accept the quest with `quest-accept` "
                    + (choice.quest != null ? "{quest:" + choice.quest.id + "}" : "") + ", then "
                    + "`letter-dismiss` this letter (they are independent).";

            var owner = OwningWindowFor(choice);
            if (owner != null)
                d["open_in"] = new Dictionary<string, object>
                {
                    ["type"] = owner.GetType().Name,
                    ["type_full"] = owner.GetType().FullName,
                    ["force_pause"] = owner.forcePause,
                    ["note"] = "this letter is CURRENTLY OPEN in that window. `letter-choose` will route "
                        + "through the window's own option objects, so the window closes with the "
                        + "letter; answering it any other way would leave the window up and the run "
                        + "halted (LetterStack.OpenAutomaticLetters early-returns while "
                        + "WindowStack.WindowsForcePause).",
                };
            return d;
        }

        private static Dictionary<string, object> LetterRefuse(string verb, Letter let, string gate,
            string cite, string reason, Dictionary<string, object> extra = null)
        {
            var d = new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["ok"] = false,
                ["letter"] = let.ID,
                ["label"] = WorldSafe.Safe(() => Journal.Truncate(let.Label.ToString(), DiaLabelClip)),
                ["type"] = let.GetType().Name,
                ["gate"] = gate,
                ["gate_cite"] = cite,
                ["reason"] = reason,
                ["action"] = NoStamp(),
            };
            if (extra != null) foreach (var kv in extra) d[kv.Key] = kv.Value;
            AddStackState(d);
            return d;
        }
    }
}
