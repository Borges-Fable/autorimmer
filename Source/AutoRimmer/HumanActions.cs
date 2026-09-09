using System.Collections.Generic;
using HarmonyLib;
using LudeonTK;
using UnityEngine;
using Verse;

namespace AutoRimmer
{
    // WHEN DORIAN TOUCHES THE COLONY — git-bug 827c1bf, COCKPIT.md
    // §"Where it lives".
    //
    // The audit of run openrun-20260902 counted at least 22 human
    // interventions the journal is silent about: a revival through the debug
    // menu, a deleted weather event, 411 meals, nine in-game days played by
    // hand (themes.md T13, T14). Three of that run's 23 `death` rows are
    // debug-menu residue and one death was reversed with no row for the
    // reversal, so no count taken from it is a measurement. Provenance on
    // every row (Provenance.cs) makes the CONSEQUENCES attributable; this file
    // makes the ACTS visible, as `action` rows of their own carrying the
    // game's own label for what was pressed.
    //
    // ---- the rules every hook here follows ------------------------------
    //
    // POSTFIX ONLY, try/catch-swallowed, no `__result` write, no mutation —
    // JournalHooks.cs's rules, and these are journal hooks by another name.
    //
    // GUARDED ON `Provenance.NothingOfOursIsRunning`. `human` is DEFINED as
    // "a mutation reached from the game's own input handlers while nothing of
    // ours is running", so the guard is the definition rather than a
    // precaution. It also makes each hook idempotent against the mod's own
    // use of the same path: none of these five members is called anywhere in
    // this tree today (`PawnActs.Replay` (DialogVerbs.cs) deliberately REPRODUCES
    // `DiaOption.Activate` rather than calling it — see its header), but a
    // future verb that pressed a real gizmo would otherwise book its own act
    // as a human's, which is precisely the wrong attribution this issue
    // exists to eliminate.
    //
    // COST. Four of the five fire only when a person actually presses
    // something: a gizmo, a float-menu entry, a dialog button, a debug-menu
    // leaf. Their bodies are a bool read and, on the rare true path, a small
    // dictionary. The fifth, `DesignatorManager.ProcessInputEvents`, is on the
    // OnGUI path and is discussed at its own patch.
    //
    // EVERY MEMBER BELOW WAS VERIFIED BY NAME against
    // rimworld-tools/Info/decompiled/RimWorldBase before this file was
    // written. Line offsets differ between benches; names do not.
    public static class HumanActions
    {
        private static int Tick()
        {
            try { return Find.TickManager.TicksGame; }
            catch { return Runtime.GameState.tick; }
        }

        // One shape for all five, so the screen can fold "N actions over M
        // ticks" off a single `type == "action" && by == "human"` filter.
        // `step` names WHICH input handler it came through, which is the fact
        // a reader needs to tell a debug-menu revival from a mouse click.
        private static void Note(string step, string label, Dictionary<string, object> extra = null)
        {
            var payload = new Dictionary<string, object>
            {
                ["verb"] = "human",
                ["step"] = step,
                ["label"] = Journal.Truncate(label, 300),
            };
            if (extra != null)
                foreach (var kv in extra) payload[kv.Key] = kv.Value;
            Journal.Emit("action", payload, Tick());
        }

        // ==================================================== the gizmos ====
        // `Verse/Command.ProcessInput(Event ev)` — VERIFIED: `public override
        // void ProcessInput(Event ev)` on `public abstract class Command :
        // Gizmo`, overriding `Gizmo.ProcessInput`.
        //
        // THE BASE IS THE FUNNEL BECAUSE THE SUBCLASSES CALL IT FIRST.
        // `Command_Action.ProcessInput` is `base.ProcessInput(ev); action();`
        // and `Command_Toggle.ProcessInput` is `base.ProcessInput(ev);
        // toggleAction();` — both verified by reading the members. So this
        // postfix fires BEFORE the button's effect, and the journal reads
        // "pressed Draft" and then whatever drafting did. That order is the
        // right one for a chronology and is the reason the base is the hook
        // rather than each subclass.
        //
        // It covers every gizmo the game builds through `Command`, which is
        // the whole of the selected-thing button strip: draft, undraft,
        // forbid, hold-open, set-target-temperature, every `Designator` (which
        // is itself a `Command` — `public abstract class Designator :
        // Command`), and every modded gizmo that inherits from it.
        //
        // WHAT IT MISSES, MEASURED RATHER THAN GUESSED. 23 members in the
        // decompiled 1.6 tree override `ProcessInput`; six do not call base,
        // and five of those are Designators — `Designator_Build`,
        // `Designator_Install`, `Designator_Dropdown`, `Designator_Paint`,
        // `Designator_MechControlGroup` — plus one gizmo inside
        // `Comp_AtmosphericHeater`. So PICKING one of those tools off the
        // architect menu writes no row. It costs nothing that matters: the tool
        // does nothing until it is used, and USING it goes through
        // `DesignatorManager.ProcessInputEvents`, which is hooked at the bottom
        // of this file. The press that has an effect is recorded; the press
        // that only arms a cursor is not.
        //
        // The `ev` parameter is deliberately NOT declared: Harmony matches
        // postfix parameters by name and omits what is not asked for, and this
        // hook has nothing to say about the event.
        [HarmonyPatch(typeof(Command), nameof(Command.ProcessInput))]
        public static class Patch_CommandProcessInput
        {
            public static void Postfix(Command __instance)
            {
                try
                {
                    if (!Provenance.NothingOfOursIsRunning) return;
                    if (Current.ProgramState != ProgramState.Playing) return;
                    if (__instance == null) return;
                    // `Command.Label` is `public virtual string Label =>
                    // defaultLabel;` — a field read on the base, and whatever
                    // a subclass computes for itself. Not `LabelCap`, which
                    // calls `CapitalizeFirst()` and allocates.
                    Note("gizmo", __instance.Label, new Dictionary<string, object>
                    {
                        ["gizmo"] = __instance.GetType().Name,
                    });
                }
                catch { }
            }
        }

        // ================================================ the float menu ====
        // `Verse/FloatMenuOption.Chosen(bool colonistOrdering, FloatMenu
        // floatMenu)` — VERIFIED: `public void Chosen(bool colonistOrdering,
        // FloatMenu floatMenu)`.
        //
        // This is the right-click order menu — "Rescue Table", "Prioritize
        // hauling", "Arrest". A postfix fires AFTER `action()`, so the row
        // lands after the order's own effects rather than before them; the
        // asymmetry with the gizmo hook above is the game's, not a choice made
        // here, and every row carries its own seq so the chronology is exact
        // either way.
        //
        // `Disabled` options run no action (the body plays a reject sound
        // instead) and are skipped, so a misclick on a greyed-out entry does
        // not read as an act.
        [HarmonyPatch(typeof(FloatMenuOption), nameof(FloatMenuOption.Chosen))]
        public static class Patch_FloatMenuOptionChosen
        {
            public static void Postfix(FloatMenuOption __instance, bool colonistOrdering)
            {
                try
                {
                    if (!Provenance.NothingOfOursIsRunning) return;
                    if (Current.ProgramState != ProgramState.Playing) return;
                    if (__instance == null || __instance.Disabled) return;
                    // `public string Label { get { return labelInt; } … }` — a
                    // field read.
                    Note("float-menu", __instance.Label, new Dictionary<string, object>
                    {
                        ["colonist_ordering"] = colonistOrdering,
                    });
                }
                catch { }
            }
        }

        // ============================================== the debug menu ======
        // `LudeonTK/DebugActionNode.Enter(Dialog_Debug dialog)` — VERIFIED:
        // `public void Enter(Dialog_Debug dialog)`.
        //
        // THE ROW THAT WOULD HAVE MARKED THE REVIVAL. Three of run
        // openrun-20260902's 23 death rows are debug-menu residue and one death
        // was reversed through this menu with nothing in the record to say so.
        //
        // LEAVES ONLY. `Enter` does two different jobs depending on the node:
        // with children it calls `dialog.SetCurrentNode(this)` and RETURNS —
        // that is navigating a menu, not doing anything — and only a childless
        // node reaches the `switch (actionType)` that runs `action()` or arms
        // a `DebugTool`. `children` is `public List<DebugActionNode>` and
        // `TrySetupChildren()` has already run by the time a postfix sees it,
        // so `children.Count == 0` is the leaf test and it is exact.
        //
        // WHAT THIS DOES NOT CATCH, stated rather than discovered: for
        // `DebugActionType.ToolMap`, `ToolWorld` and `ToolMapForPawns`, `Enter`
        // only ARMS `DebugTools.curTool`; the effect happens on the next map
        // click, through the tool's own delegate, which has no member of its
        // own to hook. So a resurrect-by-tool produces the row "chose
        // Resurrect" at the moment of choosing and the resurrection itself
        // lands a few frames later — with `by:"human"` on whatever it emits,
        // since nothing of ours is running then either. `action_type` is
        // published so a reader can tell which kind it was rather than having
        // to assume.
        [HarmonyPatch(typeof(DebugActionNode), nameof(DebugActionNode.Enter))]
        public static class Patch_DebugActionNodeEnter
        {
            public static void Postfix(DebugActionNode __instance)
            {
                try
                {
                    if (!Provenance.NothingOfOursIsRunning) return;
                    if (__instance == null) return;
                    if (__instance.children != null && __instance.children.Count > 0) return;
                    Note("debug-menu", __instance.LabelNow, new Dictionary<string, object>
                    {
                        ["action_type"] = __instance.actionType.ToString(),
                        // Deliberately NOT gated on ProgramState.Playing: half
                        // the debug menu is reachable from the main menu and
                        // from world generation, and "what did somebody do to
                        // this bench before the colony loaded" is exactly the
                        // question T13 could not answer.
                        ["program_state"] = Current.ProgramState.ToString(),
                    });
                }
                catch { }
            }
        }

        // ============================================= the dialog buttons ===
        // `Verse/DiaOption.Activate()` — VERIFIED: `protected void Activate()`,
        // no parameters. Patched by string name because it is not public.
        //
        // This is the button funnel for `Dialog_NodeTree` and everything built
        // on it, which is how a letter gets answered, how a trade dialog is
        // confirmed and how a quest is accepted or declined —
        // `DiaOption.OptOnGUI` is the only caller and it calls `Activate()`
        // exactly when the widget's button returns true. The mod's own
        // `dialog-choose` does NOT come through here: `PawnActs.Replay` (DialogVerbs.cs)
        // reproduces `Activate()`'s body rather than calling it, for the
        // reasons in that file's header, so this hook sees human presses only
        // even before the provenance guard.
        //
        // WHAT IS NOT COVERED, and it is the one gap in the issue's phrase
        // "the dialog button paths": `Verse/Dialog_MessageBox` has NO funnel.
        // Its three buttons are inline `Widgets.ButtonText` calls in
        // `DoWindowContents` that invoke `buttonAAction`/`buttonBAction`/
        // `buttonCAction` directly — verified by reading the member — so the
        // only hooks available are `DoWindowContents` itself, which runs every
        // OnGUI frame and cannot tell a postfix which button ran, or
        // `Widgets.ButtonText`, which is every button in the entire UI on
        // every frame. Neither is acceptable at the cost, and neither would be
        // correct, so `Dialog_MessageBox` is reported as an uncovered path
        // rather than approximated.
        [HarmonyPatch(typeof(DiaOption), "Activate")]
        public static class Patch_DiaOptionActivate
        {
            public static void Postfix(DiaOption __instance)
            {
                try
                {
                    if (!Provenance.NothingOfOursIsRunning) return;
                    if (__instance == null) return;
                    // `DiaOption.text` is `protected string`; PawnActs
                    // (DialogVerbs.cs) already holds the guarded field ref and
                    // binds it lazily, so there is one reflection site for this
                    // field in the tree and not two.
                    Note("dialog-button", PawnActs.OptText(__instance),
                        new Dictionary<string, object>
                        {
                            ["dialog"] = __instance.dialog?.GetType().Name,
                            ["resolves"] = __instance.resolveTree,
                        });
                }
                catch { }
            }
        }

        // ============================================== the designators =====
        // `Verse/DesignatorManager.ProcessInputEvents()` — VERIFIED: `public
        // void ProcessInputEvents()`, no parameters.
        //
        // THE ONLY ONE OF THE FIVE ON A PER-FRAME PATH, so it is worth being
        // precise about what it costs and what it can honestly say.
        //
        // COST. It is called from the map's OnGUI, so a postfix runs a few
        // times per frame whatever the player is doing. The body's FIRST
        // statement is a [ThreadStatic] read that returns immediately whenever
        // the mod, the agent or a tick is running, and its second is a null
        // check on `Event.current`; the dictionary is built only on the frames
        // where a click was actually consumed. The method it postfixes
        // ITSELF early-returns on `!CheckSelectedDesignatorValid()`, which is
        // true whenever no designator is held — i.e. almost always — but a
        // Harmony postfix runs regardless, which is why the guard has to be
        // the cheapest thing available and why nothing here allocates on the
        // common path.
        //
        // WHAT IT KEYS ON. The method leaves no return value and no state a
        // postfix can diff, but each of its three branches ends in
        // `Event.current.Use()`, and `Use()` sets `Event.current.type` to
        // `EventType.Used`. So `Used` is the game's own "this input was
        // consumed here" and it is exact for this method. The right-click and
        // Cancel-key branch also consumes, and it DESELECTS — so
        // `SelectedDesignator` is null afterwards — which is how a cancel is
        // told from a designation without guessing: no designator, no row.
        //
        // WHAT IT DELIBERATELY DOES NOT CLAIM. The MouseDown branch calls
        // `Use()` whether `CanDesignateCell` accepted or refused, so this row
        // says a designator was USED at a cell, not that a designation now
        // exists there. `step` is `designator` and the label is the game's
        // own; the effect, if there was one, is elsewhere in the journal with
        // `by:"human"` on it.
        [HarmonyPatch(typeof(DesignatorManager), nameof(DesignatorManager.ProcessInputEvents))]
        public static class Patch_DesignatorInput
        {
            public static void Postfix(DesignatorManager __instance)
            {
                try
                {
                    if (!Provenance.NothingOfOursIsRunning) return;
                    var e = Event.current;
                    if (e == null || e.type != EventType.Used) return;
                    if (Current.ProgramState != ProgramState.Playing) return;
                    var des = __instance?.SelectedDesignator;
                    if (des == null) return;  // the deselect branch consumed it
                    Note("designator", des.Label, new Dictionary<string, object>
                    {
                        ["designator"] = des.GetType().Name,
                        ["at"] = Positions.Out(UI.MouseCell()),
                    });
                }
                catch { }
            }
        }
    }
}
