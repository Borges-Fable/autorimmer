using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ======================================================= spec 3.4 =========
    // MANAGEMENT — the standing levers: the Work tab's matrix, the Schedule
    // tab's spans, and the Assign/Restrict tab's column strip.
    //
    // THE PLURAL FORM IS THE VERB (DESIGN §Action model). This is the file where
    // that rule does the most work:
    //
    //   * `work-priorities` takes a MATRIX. Its `set` argument is a list of
    //     blocks, each a cross product of {pawns} x {works} at one priority, so
    //     "everyone stops hauling" and "these three become doctors" are one call
    //     each. And per the session-4 amendment's item 7 there is a THIRD form
    //     that is one call in the game too — copy a whole row from another pawn
    //     (RimWorld/PawnColumnWorker_CopyPasteWorkPriorities.cs CopyFrom/PasteTo)
    //     — so `copy_from` is here rather than being 20 SetPriority calls.
    //   * `schedule` takes a SPAN of hours, not a cell. "0-5" and [22,23] are
    //     both spans; a single hour is the degenerate case. `copy_from` copies
    //     the whole 24-hour row (RimWorld/PawnColumnWorker_CopyPasteTimetable.cs).
    //   * `assign` takes a pawn list and sets ANY SUBSET of the Assign/Restrict
    //     column strip in one call: the four (five, with Reading) policies, the
    //     allowed area, medical care, self-tend and hostility response. That is
    //     the tab's own shape — one row per pawn, one column per lever.
    //
    // Every write is EverWork- or tracker-gated per the widget, and every
    // rejection carries the game's own reason. See PawnActs.cs's header for why
    // that is not optional.
    internal static partial class PawnActs
    {
        // --------------------------------------------------------------------
        // work-priorities {set:[{pawns|pawn, works|work, priority}], …}
        //                 {copy_from:<id>, to:[…]}
        //                 {manual:true|false}          — the Work tab's checkbox
        //
        // WIDGET GATE — RimWorld/PawnColumnWorker_WorkPriority.cs DoCell:
        //   `pawn.Dead || pawn.workSettings == null || !pawn.workSettings.EverWork`
        //   returns early — no cell, no click. HeaderClicked (the shift-click
        //   mass edit, the closest vanilla analogue to this verb) adds
        //   `pawn.WorkTypeIsDisabled(def.workType)` and skips.
        //
        // WHY THE DISABLED CHECK IS NOT OPTIONAL: RimWorld/Pawn_WorkSettings.cs
        // SetPriority does
        //     if (priority != 0 && pawn.WorkTypeIsDisabled(w)) { Log.Error(…); return; }
        // — a RED ERROR, which breaches this project's standing zero-red-errors
        // invariant, for an input a program will produce constantly (a burning
        // passion for a work type the pawn's backstory forbids). And
        // ConfirmInitializedDebug() at the top of SetPriority/GetPriority both
        // Log.Errors AND initialises a pawn that never had work settings
        // (PawnSafe Class B). Both are pre-checked here.
        //
        // `use_priorities` is published beside every row for the reason
        // PawnSerializer.WorkRow already documents: with the manual-priorities
        // play setting OFF, GetPriority returns a flat 3 for every active work
        // type, so the numbers read back are not the numbers stored — and a
        // `copy_from` taken in that state copies threes.
        //
        // ---------------- `manual`: THE CHECKBOX ABOVE THE MATRIX ------------
        // git-bug e8f2c32. The refusal below ("priority 2 is meaningless with
        // manual priorities off") is correct and stays, but it named a lever no
        // verb owned: `useWorkPriorities` defaults FALSE on a new colony
        // (RimWorld/PlaySettings.cs ExposeData — `defaultValue: false`), so a
        // colony the agent staged itself could never reach priorities 1, 2 or 4
        // at all.
        //
        // The lever is an ARGUMENT ON THIS VERB rather than a verb of its own
        // because the game puts the control in this window: RimWorld/
        // MainTabWindow_Work.cs DoManualPrioritiesCheckbox draws it at (5,5) of
        // the SAME MainTabWindow whose body is the priority matrix. DESIGN's
        // 2026-08-31 decision — "when two specs claim the same verb, the split
        // follows where the GAME puts the control" — answers this the same way,
        // and it keeps the flip and the cells one round trip and one ordering.
        // See DESIGN's decisions log for the full reasoning and for why
        // PlaySettings is NOT one verb's territory.
        //
        // WIDGET GATE — there is none, and saying so IS the citation. The
        // checkbox is unconditional inside the window; the only precondition is
        // the tab itself, RimWorld/MainButtonWorker.cs Disabled =>
        // `Find.CurrentMap == null && !def.validWithoutMap`, which the Map()
        // call at the top of this verb already reproduces. (Tutorial mode can
        // deny OPENING a tab — MainButtonWorker.InterfaceTryActivate's
        // TutorSystem.AllowAction — but that gates a UI mode we never enter,
        // not the setting.)
        //
        // WIDGET EFFECT — and this half is NOT optional. The checkbox does two
        // things, and reproducing only the first leaves the flip inert:
        //
        //     Widgets.CheckboxLabeled(rect, "ManualPriorities".Translate(),
        //         ref Current.Game.playSettings.useWorkPriorities);
        //     if (changed)
        //         foreach (Pawn p in PawnsFinder.AllMapsWorldAndTemporary_Alive)
        //             if (p.Faction == Faction.OfPlayer && p.workSettings != null)
        //                 p.workSettings.Notify_UseWorkPrioritiesChanged();
        //
        // RimWorld/Pawn_WorkSettings.cs Notify_UseWorkPrioritiesChanged sets
        // `workGiversDirty = true`, and WorkGiversInOrderNormal/Emergency —
        // what JobGiver_Work actually walks — are rebuilt only when that flag
        // is set. Flip the field alone and every colonist keeps dispatching off
        // an order computed under the OLD reading until some unrelated
        // SetPriority happens to dirty it. That is a silent, delayed, wrong
        // answer, which is the exact failure mode the gate-in-the-widget rule
        // exists to prevent, seen from the effect side rather than the gate side.
        //
        // NOTHING IS DESTROYED BY THE FLIP, and the verb says so rather than
        // leaving the agent to guess. `useWorkPriorities` is a READ-TIME MASK
        // and nothing else: Pawn_WorkSettings.GetPriority is
        //     int num = priorities[w];
        //     if (pawn.RaceProps.Humanlike && num > 0 && !Find.PlaySettings.useWorkPriorities)
        //         return 3;
        //     return num;
        // — SetPriority writes the raw number either way, ExposeData scribes the
        // raw DefMap, and Notify_UseWorkPrioritiesChanged touches one bool. So a
        // stored 1 survives a trip to off and back, and the flip is lossless in
        // both directions. (Two real consequences that are NOT the flip: a WRITE
        // while off can only be 0 or 3 — vanilla's own checkbox column,
        // WidgetsWork.DrawWorkBoxFor's else-branch, writes exactly those — so
        // writing while off flattens a stored 1/2/4; and `copy_from` while off
        // copies the masked threes, which this verb already warns about. Note
        // also that the mask is gated on `RaceProps.Humanlike`: a Biotech mech's
        // priorities read raw regardless of the setting.)
        // --------------------------------------------------------------------
        [Verb("work-priorities")]
        public static object WorkPriorities(VerbContext ctx)
        {
            const string V = "work-priorities";
            var map = Map();
            var a = ctx.Args;
            var outcome = new Outcome();
            var changes = new List<object>();
            bool usePriorities = true;
            try { usePriorities = Find.PlaySettings.useWorkPriorities; } catch { }

            // The checkbox runs FIRST, so `work-priorities {manual:true, set:[…
            // priority 1 …]}` is one call: the refusal below is judged against
            // the value this call just installed, not the one it replaced.
            //
            // THE COST OF THAT ORDER, stated rather than discovered: everything
            // after this point can still throw bad-args (an unknown pawn id, a
            // misspelled work type, a priority out of range), and the flip has
            // ALREADY landed and been journaled by then. So an `ok:false` from
            // this verb does NOT imply the colony is unchanged — check
            // `use_priorities`, or the `manual-priorities` journal step, before
            // assuming a rejected call was inert. Removing that would mean
            // resolving every `set` block before the flip and re-checking the
            // refusal after it; it is not free and it is not this branch's.
            Dictionary<string, object> manual = null;
            if (a.Has("manual"))
            {
                manual = SetManualPriorities(a.Bool("manual", usePriorities));
                usePriorities = (bool)manual["after"];
            }

            // `manual` alone is a whole call — turning the Work tab from a
            // checkbox column into a priority column is the operation, and the
            // matrix may well be somebody else's next call.
            if (manual != null && !a.Has("set") && !a.Has("copy_from"))
            {
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["mode"] = "manual",
                    ["manual"] = manual,
                    ["use_priorities"] = usePriorities,
                    ["action"] = ManualFallbackStamp(manual),
                    ["note"] = (string)manual["note"],
                };
            }

            if (a.Has("copy_from"))
            {
                var src = OnePawn(map, a.Raw("copy_from"), "copy_from");
                var targets = PawnList(map, a, true, "to", "to");
                if (!EverWork(src, out string srcWhy))
                    throw new VerbArgsException("copy_from: " + srcWhy);

                // RimWorld/PawnColumnWorker_CopyPasteWorkPriorities.cs CopyFrom:
                // the WHOLE DefDatabase list, not the visible subset, and a
                // disabled work type copies as 0.
                var clipboard = new Dictionary<WorkTypeDef, int>();
                foreach (var w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                {
                    if (w == null) continue;
                    int v = 0;
                    try { v = src.WorkTypeIsDisabled(w) ? 0 : src.workSettings.GetPriority(w); } catch { }
                    clipboard[w] = v;
                }

                foreach (var p in targets)
                {
                    if (p == src) { outcome.No(p, "self", "cannot copy a pawn's work row onto itself"); continue; }
                    if (p.Dead) { outcome.No(p, "dead", "dead pawns have no work row"); continue; }
                    if (!EverWork(p, out string why)) { outcome.No(p, "no-work-settings", why); continue; }
                    int set = 0, skipped = 0;
                    foreach (var kv in clipboard)
                    {
                        // PasteTo's own guard, and the red-error precheck.
                        if (p.WorkTypeIsDisabled(kv.Key)) { skipped++; continue; }
                        try { p.workSettings.SetPriority(kv.Key, kv.Value); set++; }
                        catch { skipped++; }
                    }
                    outcome.Ok(p, new Dictionary<string, object>
                    {
                        ["copied_from"] = src.thingIDNumber,
                        ["set"] = set,
                        ["skipped_disabled"] = skipped,
                    });
                }

                long copySeq = ActOn(outcome, V, "copy-row",
                    PawnSafe.Name(src) + " -> " + outcome.Count + " pawn(s)",
                    new Dictionary<string, object> { ["from"] = src.thingIDNumber });
                var copyResult = outcome.Result(V, copySeq, new Dictionary<string, object>
                {
                    ["mode"] = "copy",
                    ["from"] = src.thingIDNumber,
                    ["use_priorities"] = usePriorities,
                    ["note"] = usePriorities
                        ? "the whole row was copied in one call (PawnColumnWorker_CopyPasteWorkPriorities)"
                        : "MANUAL PRIORITIES ARE OFF: GetPriority returns a flat 3 for every active work "
                          + "type, so this copy wrote threes, not the source pawn's stored numbers. "
                          + "Pass manual:true in the same call to copy the stored numbers instead",
                });
                if (manual != null) copyResult["manual"] = manual;
                // A copy that accepted nobody still mutated if the `manual`
                // flip in the same call took. See ManualFallbackStamp.
                // Reached, not Count (4087644 comment #1): a copy that refused
                // every pawn now journals its verdict, so the manual-flip
                // fallback must only fire when the copy half wrote NO row at
                // all. Keyed on Count it would have overwritten a real
                // journal_seq with "nothing was mutated" — the same false
                // negative e8f2c32 fixed one branch below.
                if (!outcome.Reached) copyResult["action"] = ManualFallbackStamp(manual);
                return copyResult;
            }

            if (!a.Has("set"))
                throw new VerbArgsException(
                    "pass 'set' (an array of {pawns|pawn, works|work, priority} blocks — the matrix form) "
                    + "or 'copy_from' with 'to' (the copy-a-whole-row form) "
                    + "or 'manual' (a bool — the Work tab's manual-priorities checkbox, which must be "
                    + "on before priorities 1, 2 and 4 mean anything)");
            if (!(a.Raw("set") is List<object> blocks))
                throw new VerbArgsException("'set' must be an array of {pawns|pawn, works|work, priority} objects");

            foreach (var raw in blocks)
            {
                if (!(raw is Dictionary<string, object> block))
                    throw new VerbArgsException("each 'set' entry must be an object");
                var bargs = new VerbArgs(block);
                int priority = bargs.IntReq("priority");
                // Pawn_WorkSettings.SetPriority Log.Message's out-of-range
                // rather than refusing; refuse instead of writing garbage.
                if (priority < 0 || priority > 4)
                    throw new VerbArgsException("priority must be 0..4 (0 = off, 1 = highest, 4 = lowest)");
                if (!usePriorities && priority != 0 && priority != 3)
                    throw new VerbArgsException(
                        $"priority {priority} is meaningless with manual priorities off: the Work tab is a "
                        + "checkbox then and GetPriority returns a flat 3. Use 0 or 3, or turn manual "
                        + "priorities on in the play settings — this verb owns that checkbox, so "
                        + "`work-priorities {manual:true}`, or add manual:true to THIS call and the "
                        + "priorities below are judged against the new value.");

                var pawns = PawnList(map, bargs);
                var works = WorkTypeList(bargs);
                foreach (var p in pawns)
                {
                    if (p.Dead) { outcome.No(p, "dead", "dead pawns have no work row"); continue; }
                    if (!EverWork(p, out string why)) { outcome.No(p, "no-work-settings", why); continue; }
                    foreach (var w in works)
                    {
                        if (p.WorkTypeIsDisabled(w))
                        {
                            // Pre-checked precisely because SetPriority answers
                            // this with Log.Error (zero-red-errors invariant).
                            outcome.No(p, "work-disabled",
                                "work type disabled for this pawn: " + (w.gerundLabel ?? w.defName));
                            continue;
                        }
                        int before;
                        try { before = p.workSettings.GetPriority(w); } catch { before = -1; }
                        try { p.workSettings.SetPriority(w, priority); }
                        catch (Exception e) { outcome.No(p, "exception", e.GetType().Name + ": " + e.Message); continue; }
                        changes.Add(new Dictionary<string, object>
                        {
                            ["pawn"] = p.thingIDNumber,
                            ["name"] = PawnSafe.Name(p),
                            ["work"] = w.defName,
                            ["before"] = before,
                            ["after"] = priority,
                        });
                    }
                }
            }

            // The plural echo: one accepted line per pawn touched, with the
            // per-cell changes beside it.
            var byPawn = new Dictionary<int, int>();
            foreach (Dictionary<string, object> c in changes)
            {
                int id = (int)c["pawn"];
                byPawn[id] = byPawn.TryGetValue(id, out var n) ? n + 1 : 1;
            }
            // Deliberately NOT ActOn. e8f2c32's rule — the provenance stamp has
            // to count the same unit `counts` does — applies to the journal row's
            // verdict too: this path's unit is matrix CELLS, it never calls
            // Outcome.Ok, so ActOn's verdict would report accepted:0 over a call
            // that just wrote `changes.Count` cells. Hand-built instead, with the
            // unit named on the row.
            bool owed = changes.Count > 0 || outcome.Rejected.Count > 0;
            long seq = owed
                ? Act(V, "set", changes.Count + " cell(s) across " + byPawn.Count + " pawn(s)",
                      new Dictionary<string, object>
                      {
                          ["cells"] = changes.Count,
                          ["verdict"] = new Dictionary<string, object>
                          {
                              ["accepted"] = changes.Count,
                              ["rejected"] = outcome.Rejected.Count,
                              ["by_gate"] = outcome.Verdict()["by_gate"],
                              ["unit"] = "matrix cells",
                          },
                      })
                : 0;

            var result = outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["mode"] = "matrix",
                ["changes"] = changes,
                ["cells"] = changes.Count,
                ["pawns_touched"] = byPawn.Count,
                ["use_priorities"] = usePriorities,
                ["note"] = "priority 1 is HIGHEST and 4 is lowest; 0 is off. "
                    + (usePriorities ? "" : "Manual priorities are OFF, so only 0 and 3 mean anything."),
            });
            // `accepted` is per-cell here rather than per-pawn, so state the
            // shape rather than let the counts read as pawns.
            result["counts"] = new Dictionary<string, object>
            {
                ["accepted"] = changes.Count,
                ["rejected"] = outcome.Rejected.Count,
                ["unit"] = "matrix cells",
            };
            result["accepted"] = changes;
            if (manual != null) result["manual"] = manual;
            // THE PROVENANCE STAMP HAS TO COUNT THE SAME UNIT `counts` DOES.
            // Outcome.Result stamps `action` off Accepted.Count, and this path
            // never calls Outcome.Ok — it fills `changes` instead, because its
            // unit is matrix CELLS, not pawns. So Accepted.Count was always 0
            // and every successful matrix write returned
            // `action:{journal_seq:null, provenance:"…nothing was mutated"}`
            // over a call that had just written journal line `seq`. That is a
            // false negative on the one field the journal join key lives in,
            // and 3.4's own acceptance asserts against it (4.7e,
            // `action.journal_seq >= 1`) — a check that never ran until this
            // branch's step 0.5 made priority 1 reachable at all.
            // `owed`, not changes.Count: a matrix call that refused every cell
            // now writes a row carrying that verdict, and Stamp(seq) is what
            // reports it — including seq == 0, which honestly says the journal
            // writer was closed rather than pretending nothing was attempted.
            result["action"] = owed ? Stamp(seq) : ManualFallbackStamp(manual);
            return result;
        }

        // `action` for a call whose matrix/copy half wrote nothing but whose
        // `manual` flip DID land: the flip is a mutation and it has a journal
        // line, so "not applicable — nothing was mutated" would be untrue.
        // Falls through to NoStamp() when there was no flip, or none that
        // changed anything, which is what Outcome.Result would have said.
        private static Dictionary<string, object> ManualFallbackStamp(Dictionary<string, object> manual)
        {
            if (manual != null && manual["changed"] is bool changed && changed)
                return Stamp(manual["journal_seq"] is long s ? s : 0L);
            return NoStamp();
        }

        // ------------------- the Work tab's manual-priorities checkbox -------
        // RimWorld/MainTabWindow_Work.cs DoManualPrioritiesCheckbox, both
        // halves. See this verb's header for why the second half is mandatory
        // and why the flip destroys nothing.
        //
        // The `after` value is RE-READ from the field rather than echoed from
        // the request: durable state is reported from a read, so a write that
        // silently did not take reads back as `changed:false` instead of as a
        // success.
        private static Dictionary<string, object> SetManualPriorities(bool want)
        {
            PlaySettings ps;
            try { ps = Find.PlaySettings; } catch { ps = null; }
            if (ps == null)
                throw new VerbArgsException(
                    "no PlaySettings — Current.Game is not loaded, so there is no Work tab to check");

            bool before = ps.useWorkPriorities;
            ps.useWorkPriorities = want;
            bool after = ps.useWorkPriorities;      // read back, never assumed
            bool changed = after != before;

            int notified = changed ? NotifyUseWorkPrioritiesChanged() : 0;
            long seq = changed
                ? Act("work-priorities", "manual-priorities",
                      (before ? "on" : "off") + " -> " + (after ? "on" : "off"),
                      new Dictionary<string, object>
                      {
                          ["before"] = before,
                          ["after"] = after,
                          ["pawns_notified"] = notified,
                      })
                : 0;

            return new Dictionary<string, object>
            {
                ["requested"] = want,
                ["before"] = before,
                ["after"] = after,
                ["changed"] = changed,
                // How many work rows had their cached WorkGiver order rebuilt.
                // 0 with changed:true would mean the flip is inert this tick.
                ["pawns_notified"] = notified,
                ["journal_seq"] = seq,
                ["note"] = changed
                    ? (after
                        ? "manual priorities ON: the Work tab is a 1..4 priority column, GetPriority "
                          + "returns the stored number, and priorities 1, 2 and 4 are now writable. "
                          + "Stored priorities were NOT altered — the setting is a read-time mask in "
                          + "Pawn_WorkSettings.GetPriority, so any 1/2/4 saved before it was last "
                          + "turned off has just reappeared"
                        : "manual priorities OFF: the Work tab is a checkbox column, GetPriority "
                          + "returns a flat 3 for every active humanlike work type, and this verb will "
                          + "refuse priorities 1, 2 and 4 until it is turned back on. Stored numbers "
                          + "are NOT erased and will reappear — but a write made while off can only be "
                          + "0 or 3, and such a write DOES overwrite a stored 1/2/4")
                    : "already " + (after ? "on" : "off") + "; nothing was written and no pawn was notified",
            };
        }

        // The checkbox's own fan-out, verbatim in shape:
        //   foreach (Pawn p in PawnsFinder.AllMapsWorldAndTemporary_Alive)
        //       if (p.Faction == Faction.OfPlayer && p.workSettings != null)
        //           p.workSettings.Notify_UseWorkPrioritiesChanged();
        //
        // Two departures from the literal source, both deliberate:
        //  * SNAPSHOT FIRST. Every PawnsFinder property is
        //    `<static field>.Clear(); …AddRange(…); return <static field>`
        //    (RimWorld/PawnsFinder.cs), so the returned list is a shared buffer
        //    the next PawnsFinder read clears underneath you. The widget gets
        //    away with iterating it live because nothing in its loop body reads
        //    PawnsFinder; copying costs one allocation and removes the trap.
        //  * per-pawn try/catch, because a modded workSettings override must not
        //    take the whole flip down halfway through the roster.
        //
        // Notify_UseWorkPrioritiesChanged (RimWorld/Pawn_WorkSettings.cs) is
        // `workGiversDirty = true` and nothing else — it does NOT route through
        // ConfirmInitializedDebug, so unlike GetPriority/SetPriority it is safe
        // on a pawn that never had work settings, and vanilla accordingly gates
        // on `workSettings != null` rather than on EverWork. Reproduced as-is.
        private static int NotifyUseWorkPrioritiesChanged()
        {
            Faction player;
            try { player = Faction.OfPlayer; } catch { player = null; }
            if (player == null) return 0;

            List<Pawn> all;
            try { all = new List<Pawn>(PawnsFinder.AllMapsWorldAndTemporary_Alive); }
            catch { return 0; }

            int notified = 0;
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (p == null || p.Faction != player || p.workSettings == null) continue;
                try { p.workSettings.Notify_UseWorkPrioritiesChanged(); notified++; } catch { }
            }
            return notified;
        }

        // `works` / `work`, plus "all" for every visible work type in the Work
        // tab's own order (WorkTypeDefsUtility.WorkTypeDefsInPriorityOrder
        // filtered on the plain `def.visible` field — VisibleCurrently writes a
        // frame cache and walks PawnsFinder, PawnSafe's note).
        private static List<WorkTypeDef> WorkTypeList(VerbArgs args)
        {
            object raw = args.Raw("works") ?? args.Raw("work");
            if (raw == null) throw new VerbArgsException("each 'set' block needs 'work' or 'works'");
            var result = new List<WorkTypeDef>();
            if (raw is string all && all == "all")
            {
                foreach (var w in WorkTypeDefsUtility.WorkTypeDefsInPriorityOrder)
                    if (w != null && w.visible) result.Add(w);
                return result;
            }
            var items = raw as List<object> ?? new List<object> { raw };
            foreach (var item in items)
            {
                if (!(item is string s))
                    throw new VerbArgsException("'work'/'works' entries must be WorkTypeDef defNames");
                result.Add(Dev.Named<WorkTypeDef>(s, "work"));
            }
            return result;
        }

        // --------------------------------------------------------------------
        // schedule {pawns:[…], hours:"0-5"|[0,1,22]|"all", assignment:"Sleep"}
        //          {pawns:[…], copy_from:<id>}
        //
        // A SPAN, not a cell. The Schedule tab is a 24-wide drag strip
        // (RimWorld/PawnColumnWorker_Timetable.cs DoCell draws 24 cells and the
        // player paints across them), so a verb that set one hour at a time
        // would be the defect the plural-form rule names.
        //
        // WIDGET GATE — same DoCell: `pawn.timetable != null && !pawn.IsSubhuman`.
        // RimWorld/Pawn_TimetableTracker.cs SetAssignment indexes `times`
        // UNGUARDED, so the 0..23 bound is checked here rather than trusted.
        //
        // The legend is PawnSerializer.Schedule's — A/W/J/S/M — so a written row
        // and a read row are the same vocabulary. TimeAssignmentDefOf.Meditate
        // does not exist without Royalty and the tracker scrubs it to Anything
        // on load, so it is offered only when the def resolves.
        // --------------------------------------------------------------------
        [Verb("schedule")]
        public static object Schedule(VerbContext ctx)
        {
            const string V = "schedule";
            var map = Map();
            var a = ctx.Args;
            var pawns = PawnList(map, a);
            var outcome = new Outcome();

            if (a.Has("copy_from"))
            {
                var src = OnePawn(map, a.Raw("copy_from"), "copy_from");
                if (src.timetable?.times == null || src.timetable.times.Count < 24)
                    throw new VerbArgsException("copy_from: that pawn has no 24-hour timetable");
                // RimWorld/PawnColumnWorker_CopyPasteTimetable.cs CopyFrom/PasteTo.
                var clipboard = new List<TimeAssignmentDef>(src.timetable.times);
                foreach (var p in pawns)
                {
                    if (p == src) { outcome.No(p, "self", "cannot copy a pawn's timetable onto itself"); continue; }
                    if (!TimetableGate(p, out string gate, out string reason)) { outcome.No(p, gate, reason); continue; }
                    string before = RowOf(p);
                    for (int h = 0; h < 24; h++) p.timetable.times[h] = clipboard[h];
                    outcome.Ok(p, new Dictionary<string, object>
                    {
                        ["copied_from"] = src.thingIDNumber,
                        ["before"] = before,
                        ["row"] = RowOf(p),
                    });
                }
                long cseq = ActOn(outcome, V, "copy-row",
                    PawnSafe.Name(src) + " -> " + outcome.Count + " pawn(s)",
                    new Dictionary<string, object> { ["from"] = src.thingIDNumber });
                return outcome.Result(V, cseq, new Dictionary<string, object>
                {
                    ["mode"] = "copy",
                    ["from"] = src.thingIDNumber,
                    ["source_row"] = RowOf(src),
                    ["legend"] = ScheduleLegend(),
                });
            }

            var hours = HourSpan(a);
            var assignment = TimeAssignment(a.StrReq("assignment"));
            foreach (var p in pawns)
            {
                if (!TimetableGate(p, out string gate, out string reason)) { outcome.No(p, gate, reason); continue; }
                string before = RowOf(p);
                foreach (int h in hours) p.timetable.SetAssignment(h, assignment);
                outcome.Ok(p, new Dictionary<string, object>
                {
                    ["before"] = before,
                    ["row"] = RowOf(p),
                    ["hours"] = hours.Count,
                });
            }

            long seq = ActOn(outcome, V, "set-span", assignment.defName + " x" + hours.Count + "h",
                      new Dictionary<string, object>
                      {
                          ["assignment"] = assignment.defName,
                          ["hours"] = new List<object>(hours.ConvertAll(h => (object)h)),
                      });
            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["mode"] = "span",
                ["assignment"] = assignment.defName,
                ["hours"] = new List<object>(hours.ConvertAll(h => (object)h)),
                ["legend"] = ScheduleLegend(),
            });
        }

        // RimWorld/PawnColumnWorker_Timetable.cs DoCell.
        private static bool TimetableGate(Pawn p, out string gate, out string reason)
        {
            gate = null; reason = null;
            if (p.timetable?.times == null || p.timetable.times.Count < 24)
            { gate = "no-timetable"; reason = "this pawn has no timetable (the Schedule tab shows no row for it)"; return false; }
            bool subhuman = false;
            try { subhuman = p.IsSubhuman; } catch { }
            if (subhuman) { gate = "subhuman"; reason = "the Schedule tab offers no row for a subhuman"; return false; }
            return true;
        }

        // "0-5" (inclusive), [0,1,22], a bare number, or "all".
        private static List<int> HourSpan(VerbArgs args)
        {
            object raw = args.Raw("hours") ?? args.Raw("hour");
            if (raw == null)
                throw new VerbArgsException("missing 'hours' — a span like \"0-5\", a list like [22,23], "
                    + "one hour, or \"all\"");
            var result = new List<int>();
            if (raw is string s)
            {
                if (s == "all") { for (int h = 0; h < 24; h++) result.Add(h); return result; }
                int dash = s.IndexOf('-');
                if (dash > 0
                    && int.TryParse(s.Substring(0, dash).Trim(), out int from)
                    && int.TryParse(s.Substring(dash + 1).Trim(), out int to))
                {
                    if (from < 0 || from > 23 || to < 0 || to > 23)
                        throw new VerbArgsException("hours must be 0..23");
                    // A span may WRAP — "22-3" is the night watch, and refusing
                    // it would make the caller issue two calls for one block.
                    int h = from;
                    while (true)
                    {
                        result.Add(h);
                        if (h == to) break;
                        h = (h + 1) % 24;
                        if (result.Count > 24) break;
                    }
                    return result;
                }
                if (int.TryParse(s.Trim(), out int one)) { result.Add(Hour(one)); return result; }
                throw new VerbArgsException($"'{s}' is not an hour span (\"0-5\"), an hour, or \"all\"");
            }
            if (raw is double d) { result.Add(Hour((int)d)); return result; }
            if (raw is List<object> list)
            {
                foreach (var item in list)
                {
                    if (!(item is double hd)) throw new VerbArgsException("'hours' entries must be numbers 0..23");
                    result.Add(Hour((int)hd));
                }
                return result;
            }
            throw new VerbArgsException("'hours' must be a span string, an hour, an array of hours, or \"all\"");
        }

        private static int Hour(int h)
        {
            if (h < 0 || h > 23) throw new VerbArgsException("hours must be 0..23");
            return h;
        }

        private static TimeAssignmentDef TimeAssignment(string name)
        {
            var def = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(name);
            if (def != null) return def;
            var known = new List<string>();
            foreach (var d in DefDatabase<TimeAssignmentDef>.AllDefsListForReading) if (d != null) known.Add(d.defName);
            throw new VerbArgsException($"no TimeAssignmentDef named '{name}' — known: {string.Join("|", known.ToArray())}");
        }

        // PawnSerializer.Schedule's legend, verbatim: one vocabulary.
        private static string RowOf(Pawn p)
        {
            var chars = new char[24];
            for (int h = 0; h < 24; h++)
            {
                string n = p.timetable.times[h]?.defName ?? "Anything";
                switch (n)
                {
                    case "Anything": chars[h] = 'A'; break;
                    case "Work": chars[h] = 'W'; break;
                    case "Joy": chars[h] = 'J'; break;
                    case "Sleep": chars[h] = 'S'; break;
                    case "Meditate": chars[h] = 'M'; break;
                    default: chars[h] = '?'; break;
                }
            }
            return new string(chars);
        }

        private static Dictionary<string, object> ScheduleLegend()
            => new Dictionary<string, object>
            {
                ["A"] = "Anything",
                ["W"] = "Work",
                ["J"] = "Joy",
                ["S"] = "Sleep",
                ["M"] = "Meditate",
                ["?"] = "unmapped (a modded TimeAssignmentDef)",
            };

        // --------------------------------------------------------------------
        // assign {pawns:[…], apparel_policy?, food_policy?, drug_policy?,
        //         reading_policy?, area?, med_care?, self_tend?, hostility?,
        //         auto_arm?}
        //
        // The Assign/Restrict tab's whole column strip, plural, in one call.
        // Any subset of the levers may be present; absent means "leave alone",
        // and `area:null` means "unrestricted", which is a real setting rather
        // than an omission (AreaAllowedGUI.DoAllowedAreaSelectors draws a null
        // selector first, labelled "Unrestricted").
        //
        // `auto_arm` is the ONE MODDED column here (git-bug 1a072fa):
        // FindSuitableWeaponAndAmmo injects a checkbox into this same tab, so
        // its natural home is this verb rather than a verb of its own. It is
        // reached by reflection through `Fswa` — no compile-time reference —
        // and on a bench without the mod it is REFUSED BY NAME while every
        // other lever in the same call still applies. Its before/after value is
        // `true`/`false`/`null`, where null is "we could not look" and never
        // "off"; `auto_arm` is echoed in before/after on EVERY assign call, so
        // the state can be read without setting it.
        //
        // WIDGET GATES, one per lever, each from its own column worker:
        //   apparel  RimWorld/PawnColumnWorker_Outfit.cs DoCell: `pawn.outfits != null`
        //   food     RimWorld/PawnColumnWorker_FoodRestriction.cs DoCell: `pawn.foodRestriction != null`
        //   drug     RimWorld/PawnColumnWorker_DrugPolicy.cs DoCell: `pawn.drugs != null`
        //   reading  RimWorld/PawnColumnWorker_Reading.cs DoCell: `pawn.reading != null`
        //   area     RimWorld/PawnColumnWorker_AllowedArea.cs DoCell:
        //              `pawn.Faction == Faction.OfPlayer
        //               && (!pawn.IsMutant || pawn.mutant.Def.respectsAllowedArea)
        //               && (!pawn.RaceProps.IsMechanoid || pawn.GetOverseer() != null)`
        //              then `pawn.playerSettings.SupportsAllowedAreas`; and the
        //              AREA itself must answer `AssignableAsAllowed()`
        //              (Verse/Area.cs — false by default; Area_Allowed and
        //              Area_Home override it to true, BuildRoof/NoRoof/SnowClear
        //              do not).
        //   med care RimWorld/PawnColumnWorker_MedicalCare.cs DoCell → MedicalCareUtility
        //   selfTend RimWorld/HealthCardUtility.cs DrawOverviewTab: the checkbox
        //              exists for a live colonist with playerSettings, and
        //              turning it ON is REVERTED by the widget itself when
        //              `pawn.WorkTypeIsDisabled(Doctor)` ("MessageCannotSelfTendEver")
        //   hostility RimWorld/PawnColumnWorker_HostilityResponse.cs DoCell:
        //              `pawn.RaceProps.Humanlike`; and
        //              HostilityResponseModeUtility's own menu omits `Attack`
        //              when `WorkTagIsDisabled(WorkTags.Violent)`
        //   auto_arm FSWA/PawnColumnWorker_AutoArm.cs HasCheckbox, plus the
        //              gizmo's mech route — see AutoArmGate below, which cites
        //              both and is where the reasoning lives
        //
        // POLICY READS STAY GUARDED, POLICY WRITES USE THE PUBLIC SETTER. The
        // getters (Pawn_OutfitTracker.CurrentApparelPolicy and friends) ASSIGN a
        // default to any pawn that has none and SCRIBE it — PawnSafe Class A —
        // so the before/after echo reads through PawnSafe.Policies and
        // ReadingPolicyOf, while the assignment goes through the setter, which
        // is what the dropdown does. The apparel setter additionally fires
        // `mindState.Notify_OutfitChanged()`, which sets `nextApparelOptimizeTick
        // = now` (Verse.AI/Pawn_MindState.cs) — that is what makes the pawn
        // re-dress promptly instead of at the next 6000–9000 tick check.
        // --------------------------------------------------------------------
        [Verb("assign")]
        public static object Assign(VerbContext ctx)
        {
            const string V = "assign";
            var map = Map();
            var a = ctx.Args;
            var pawns = PawnList(map, a);
            var outcome = new Outcome();
            var touched = new List<string>();

            ApparelPolicy apparel = a.Has("apparel_policy") ? FindApparelPolicy(a.Raw("apparel_policy")) : null;
            FoodPolicy food = a.Has("food_policy") ? FindFoodPolicy(a.Raw("food_policy")) : null;
            DrugPolicy drug = a.Has("drug_policy") ? FindDrugPolicy(a.Raw("drug_policy")) : null;
            ReadingPolicy reading = a.Has("reading_policy") ? FindReadingPolicy(a.Raw("reading_policy")) : null;
            bool wantArea = a.Has("area");
            Area area = wantArea ? FindArea(map, a.Raw("area")) : null;
            bool wantMed = a.Has("med_care");
            MedicalCareCategory medCare = wantMed ? MedCare(a.Str("med_care")) : MedicalCareCategory.NoCare;
            bool wantSelfTend = a.Has("self_tend");
            bool selfTend = wantSelfTend && a.Bool("self_tend", false);
            bool wantHostility = a.Has("hostility");
            HostilityResponseMode hostility = wantHostility ? Hostility(a.Str("hostility")) : HostilityResponseMode.Flee;
            bool wantAutoArm = a.Has("auto_arm");
            bool autoArm = wantAutoArm && a.Bool("auto_arm", false);

            if (apparel != null) touched.Add("apparel_policy");
            if (food != null) touched.Add("food_policy");
            if (drug != null) touched.Add("drug_policy");
            if (reading != null) touched.Add("reading_policy");
            if (wantArea) touched.Add("area");
            if (wantMed) touched.Add("med_care");
            if (wantSelfTend) touched.Add("self_tend");
            if (wantHostility) touched.Add("hostility");
            if (wantAutoArm) touched.Add("auto_arm");
            if (touched.Count == 0)
                throw new VerbArgsException(
                    "nothing to assign — pass at least one of apparel_policy, food_policy, drug_policy, "
                    + "reading_policy, area, med_care, self_tend, hostility, auto_arm");

            var ids = new List<object>();
            foreach (var p in pawns)
            {
                // Guarded READ of the before state (PawnSafe Class A).
                var before = PawnSafe.Policies(p);
                before["reading"] = ReadingPolicyOf(p)?.label;
                before["area"] = Safe(() => p.playerSettings?.AreaRestrictionInPawnCurrentMap?.Label);
                before["med_care"] = p.playerSettings?.medCare.ToString();
                before["self_tend"] = p.playerSettings?.selfTend;
                before["hostility_response"] = p.playerSettings?.hostilityResponse.ToString();
                // Echoed whether or not the lever was passed: this is how
                // auto-arm is READ without being SET (1a072fa's acceptance).
                // The reason for a null is left to the result-level
                // `auto_arm_mod` block rather than repeated on every row.
                Fswa.PublishAutoArm(p, before, inlineReason: false);

                var applied = new List<object>();
                var refused = new List<object>();

                if (apparel != null) One(p, "apparel_policy", applied, refused, () =>
                {
                    if (p.outfits == null) return "this pawn has no apparel tracker (no Outfit column)";
                    if (MutantBlocksApparel(p)) return "this pawn's mutant type disables apparel or policies";
                    p.outfits.CurrentApparelPolicy = apparel;
                    return null;
                });
                if (food != null) One(p, "food_policy", applied, refused, () =>
                {
                    if (p.foodRestriction == null) return "this pawn has no food tracker (no Food column)";
                    p.foodRestriction.CurrentFoodPolicy = food;
                    return null;
                });
                if (drug != null) One(p, "drug_policy", applied, refused, () =>
                {
                    if (p.drugs == null) return "this pawn has no drug tracker (no Drugs column)";
                    p.drugs.CurrentPolicy = drug;
                    return null;
                });
                if (reading != null) One(p, "reading_policy", applied, refused, () =>
                {
                    if (p.reading == null) return "this pawn has no reading tracker (no Reading column)";
                    p.reading.CurrentPolicy = reading;
                    return null;
                });
                if (wantArea) One(p, "area", applied, refused, () =>
                {
                    string why = AreaGate(p, area);
                    if (why != null) return why;
                    // The SETTER has a side effect worth knowing about
                    // (RimWorld/Pawn_PlayerSettings.cs
                    // AreaRestrictionInPawnCurrentMap): after storing the area
                    // it re-checks the pawn's CURRENT JOB against it and calls
                    // EndCurrentJob(InterruptForced) when any job target now
                    // lies outside — so assigning an area can interrupt work in
                    // flight. Echoed in `note`.
                    p.playerSettings.AreaRestrictionInPawnCurrentMap = area;
                    return null;
                });
                if (wantMed) One(p, "med_care", applied, refused, () =>
                {
                    if (p.playerSettings == null) return "this pawn has no player settings";
                    p.playerSettings.medCare = medCare;
                    return null;
                });
                if (wantSelfTend) One(p, "self_tend", applied, refused, () =>
                {
                    if (p.playerSettings == null) return "this pawn has no player settings";
                    if (p.Dead) return "dead";
                    if (!p.IsColonist) return "the self-tend checkbox is offered to colonists only";
                    // The widget itself reverts an ON with this exact test and
                    // messages "MessageCannotSelfTendEver"; refusing up front is
                    // the same behaviour without the lie.
                    if (selfTend && p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor))
                        return "this pawn can never self-tend: the Doctor work type is disabled for it";
                    p.playerSettings.selfTend = selfTend;
                    return null;
                });
                if (wantHostility) One(p, "hostility", applied, refused, () =>
                {
                    if (p.playerSettings == null) return "this pawn has no player settings";
                    if (p.RaceProps == null || !p.RaceProps.Humanlike)
                        return "the hostility-response column is drawn for humanlikes only";
                    if (hostility == HostilityResponseMode.Attack && p.WorkTagIsDisabled(WorkTags.Violent))
                        return "the game does not offer Attack to a pawn incapable of violence";
                    p.playerSettings.hostilityResponse = hostility;
                    return null;
                });
                if (wantAutoArm) One(p, "auto_arm", applied, refused, () =>
                {
                    string why = AutoArmGate(p);
                    if (why != null) return why;
                    // READ THE WRITE BACK. FSWA's setter is `void` and returns
                    // silently on a null tracker (FSWA/AutoArmTracker.cs
                    // SetAutoArm's first clause), so "the invoke did not throw"
                    // is not evidence that anything moved. The tracker is the
                    // only authority on what it now holds, so ask it.
                    if (!Fswa.TrySetAutoArm(p, autoArm, out string setError)) return setError;
                    bool? now = Fswa.AutoArmOf(p, out string readError);
                    if (now == null)
                        return "SetAutoArm(" + (autoArm ? "true" : "false") + ") was called and did not "
                            + "throw, but the result could not be read back, so this pawn's auto-arm state "
                            + "is now UNKNOWN rather than unchanged: " + readError;
                    if (now.Value != autoArm)
                        return "the write did not take — SetAutoArm(" + (autoArm ? "true" : "false")
                            + ") returned normally and IsAutoArm still answers "
                            + (now.Value ? "true" : "false") + ". " + Fswa.WriteDiagnosis();
                    return null;
                });

                var after = PawnSafe.Policies(p);
                after["reading"] = ReadingPolicyOf(p)?.label;
                after["area"] = Safe(() => p.playerSettings?.AreaRestrictionInPawnCurrentMap?.Label);
                after["med_care"] = p.playerSettings?.medCare.ToString();
                after["self_tend"] = p.playerSettings?.selfTend;
                after["hostility_response"] = p.playerSettings?.hostilityResponse.ToString();
                Fswa.PublishAutoArm(p, after, inlineReason: false);

                var line = new Dictionary<string, object>
                {
                    ["class"] = PawnSafe.Classify(p),
                    ["applied"] = applied,
                    ["refused"] = refused,
                    ["before"] = before,
                    ["after"] = after,
                    // What actually decides whether the area setting DOES
                    // anything, published rather than assumed: a pawn under a
                    // lord, or a guest, ignores its allowed area entirely.
                    ["respects_area"] = SafeObj(() => (object)(p.playerSettings?.RespectsAllowedArea ?? false)),
                    ["configurable_hostility"] = SafeObj(() => (object)(p.playerSettings?.UsesConfigurableHostilityResponse ?? false)),
                };
                if (applied.Count > 0) { outcome.Accepted.Add(WithPawn(p, line)); ids.Add(p.thingIDNumber); }
                else { line["gate"] = "all-refused"; line["reason"] = "no lever applied to this pawn"; outcome.Rejected.Add(WithPawn(p, line)); }
            }

            long seq = ActOn(outcome, V, "assign", string.Join(",", touched.ToArray()) + " x" + ids.Count,
                new Dictionary<string, object> { ["ids"] = ids, ["levers"] = touched.ConvertAll(t => (object)t) });

            var meta = new Dictionary<string, object>
            {
                ["levers"] = touched.ConvertAll(t => (object)t),
                ["note"] = "policy READS in before/after go through PawnSafe's guarded backing-field routes "
                    + "(the public getters ASSIGN a default and scribe it); the WRITES go through the public "
                    + "setters, which is what the dropdown does. Assigning an apparel policy fires "
                    + "mindState.Notify_OutfitChanged, which sets nextApparelOptimizeTick to now — the pawn "
                    + "re-dresses at its next free moment rather than in 6000-9000 ticks. Assigning an area "
                    + "can END the pawn's current job when a target falls outside it. `auto_arm` is a MODDED "
                    + "column (FindSuitableWeaponAndAmmo), reached by reflection and read back from the "
                    + "tracker after every write; null there means the state could not be read, never off.",
            };
            // The provenance block earns its bytes exactly when a caller could
            // otherwise misread a null: it asked for the lever, or the lever is
            // not there to ask for.
            if (wantAutoArm || !Fswa.AutoArmAvailable) meta["auto_arm_mod"] = Fswa.Describe();
            return outcome.Result(V, seq, meta);
        }

        private static Dictionary<string, object> WithPawn(Pawn p, Dictionary<string, object> line)
        {
            var d = new Dictionary<string, object>
            {
                ["pawn"] = p.thingIDNumber,
                ["name"] = PawnSafe.Name(p),
            };
            foreach (var kv in line) d[kv.Key] = kv.Value;
            return d;
        }

        // One lever: run it, record applied or refused-with-reason. Never lets
        // one lever's exception take the other seven down.
        private static void One(Pawn p, string lever, List<object> applied, List<object> refused, Func<string> act)
        {
            string why;
            try { why = act(); }
            catch (Exception e) { why = e.GetType().Name + ": " + e.Message; }
            if (why == null) applied.Add(lever);
            else refused.Add(new Dictionary<string, object> { ["lever"] = lever, ["reason"] = why });
        }

        // RimWorld/PawnColumnWorker_AllowedArea.cs DoCell + Verse/Area.cs
        // AssignableAsAllowed.
        private static string AreaGate(Pawn p, Area area)
        {
            if (p.playerSettings == null) return "this pawn has no player settings";
            if (p.Faction != Faction.OfPlayer) return "the allowed-area column is drawn for player-faction pawns only";
            try
            {
                if (p.IsMutant && p.mutant?.Def != null && !p.mutant.Def.respectsAllowedArea)
                    return "this pawn's mutant type does not respect allowed areas";
                if (p.RaceProps != null && p.RaceProps.IsMechanoid && p.GetOverseer() == null)
                    return "an unoverseen mechanoid has no allowed-area control";
                if (!p.playerSettings.SupportsAllowedAreas)
                    return "this pawn does not support area restriction "
                        + "(Pawn_PlayerSettings.SupportsAllowedAreas: a roamer, or disableAreaControl)";
                if (area != null && !area.AssignableAsAllowed())
                    return $"area '{area.Label}' is not assignable as an allowed area "
                        + "(Verse/Area.AssignableAsAllowed — BuildRoof, NoRoof and SnowClear are not)";
            }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
            return null;
        }

        // FSWA's auto-arm checkbox, gated the way the widgets that draw it are.
        //
        // WHY THIS COPIES A GATE WHERE `seek-at-will` CALLS ONE. That verb's
        // rule is the better one and it is followed wherever it can be: the
        // mod's own predicate is the authority, because a copy drifts and a call
        // cannot. It does not apply here, and the reason is worth writing down
        // so nobody "fixes" this into a Class A hazard:
        //   * The column's predicate is `protected override bool HasCheckbox
        //     (Pawn)` on an INSTANCE. The only instance the game configures is
        //     `PawnColumnDef.Worker`, and that getter is a lazy-init that
        //     CONSTRUCTS the worker and writes `workerInt`/`workerInt.def` on
        //     first read (RimWorld/PawnColumnDef.cs Worker) — PawnSafe Class A,
        //     reached from an observer path on a bench where the Assign tab may
        //     never have been opened. Constructing our own instead gives an
        //     object with a null `def` that is not the game's.
        //   * The gizmo's predicate is not a member at all: it is an inline
        //     clause in a Harmony postfix ITERATOR (FSWA/Patch_PawnGizmos.cs),
        //     so there is nothing to call.
        // So: copy, cite both widgets by member, and let the read-back in the
        // lever catch the case where a drifted copy lets a bad write through —
        // which it does, because `SetAutoArm` re-checks `MpSync.Configurable`
        // itself and the verb compares what the tracker says afterwards.
        //
        // TWO widgets reach AutoArmTracker.SetAutoArm and this verb honours
        // BOTH, because refusing a route the player actually has would be a
        // fabricated restriction:
        //
        //   column  FSWA/PawnColumnWorker_AutoArm.cs HasCheckbox —
        //             `pawn.IsColonist && !pawn.WorkTagIsDisabled(WorkTags.Violent)`
        //           This is the Assign tab's own cell, i.e. precisely the strip
        //           `assign` reproduces, so it is the primary gate.
        //   gizmo   FSWA/HarmonyPatches.cs Patch_PawnGizmos.Postfix — drawn when
        //             `pawn.IsColonistPlayerControlled
        //              || MechUtility.IsWeaponUsableMech(pawn)`,
        //           and then `Disable`d (drawn but unclickable) when
        //             `!pawn.RaceProps.IsMechanoid
        //              && pawn.WorkTagIsDisabled(WorkTags.Violent)`.
        //           The mech half is the ONLY route a player mech has — no
        //           column cell is drawn for one — and it deliberately skips
        //           the Violent check, so mechs take their own branch below.
        //
        // AND ONE GATE THAT IS NOT A WIDGET AT ALL, which is why it is easy to
        // miss: the setter itself refuses and returns SILENTLY unless
        // `MpSync.Configurable(pawn)` holds (FSWA/MpSync.cs Configurable —
        // `pawn != null && !pawn.Dead && !pawn.Destroyed`). Reproduced here so a
        // dead pawn is refused with a reason rather than reported applied
        // against a write that never happened. Deliberately NOT a Spawned check:
        // MpSync's own comment says a caravan pawn is unspawned and still
        // configurable from the Assign tab.
        private static string AutoArmGate(Pawn p)
        {
            if (!Fswa.AutoArmAvailable) return Fswa.AutoArmUnavailable;
            if (p.Dead || p.Destroyed)
                return "FSWA's setter refuses a dead or destroyed pawn and returns silently "
                    + "(FSWA/MpSync.cs Configurable), so the write would not have taken";
            try
            {
                if (p.RaceProps != null && p.RaceProps.IsMechanoid)
                {
                    bool? usable = Fswa.IsWeaponUsableMech(p, out string mechError);
                    // A probe we could not run is reported as such. Answering
                    // "not a weapon-capable mech" here would be the same class
                    // of lie as reporting auto-arm off because the read threw.
                    if (usable == null)
                        return "this pawn is a mechanoid and FSWA's mech route could not be checked, so "
                            + "the gate is unknown rather than closed: " + mechError;
                    if (!usable.Value)
                        return "FSWA offers auto-arm to a mechanoid only when it can hold a weapon "
                            + "(FSWA/MechUtility.cs IsWeaponUsableMech: player faction, an equipment "
                            + "tracker, and a pawn class implementing Fortified.IWeaponUsable)";
                    return null;
                }
                if (!p.IsColonist)
                    return "the auto-arm checkbox is drawn for colonists only "
                        + "(FSWA/PawnColumnWorker_AutoArm.cs HasCheckbox); the gizmo's only other route "
                        + "is a weapon-capable player mechanoid";
                if (p.WorkTagIsDisabled(WorkTags.Violent))
                    return "this pawn is incapable of Violence: the Assign-tab checkbox is not drawn "
                        + "(PawnColumnWorker_AutoArm.HasCheckbox) and the gizmo is disabled with "
                        + "FSWA_CannotViolent";
            }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
            return null;
        }

        private static bool MutantBlocksApparel(Pawn p)
        {
            try { return p.IsMutant && p.mutant?.Def != null && (p.mutant.Def.disableApparel || p.mutant.Def.disablePolicies); }
            catch { return false; }
        }

        private static MedicalCareCategory MedCare(string s)
        {
            foreach (MedicalCareCategory c in Enum.GetValues(typeof(MedicalCareCategory)))
                if (string.Equals(c.ToString(), s, StringComparison.OrdinalIgnoreCase)) return c;
            throw new VerbArgsException(
                "med_care must be NoCare|NoMeds|HerbalOrWorse|NormalOrWorse|Best");
        }

        private static HostilityResponseMode Hostility(string s)
        {
            foreach (HostilityResponseMode m in Enum.GetValues(typeof(HostilityResponseMode)))
                if (string.Equals(m.ToString(), s, StringComparison.OrdinalIgnoreCase)) return m;
            throw new VerbArgsException("hostility must be Ignore|Attack|Flee");
        }

        // An area by id, label, or null for "unrestricted" (a real setting).
        // AreaManager.AllAreas is the real backing list (WorldSafe Class E note).
        private static Area FindArea(Map map, object raw)
        {
            if (raw == null) return null;
            var all = map.areaManager.AllAreas;
            if (raw is double d)
            {
                int id = (int)d;
                for (int i = 0; i < all.Count; i++) if (all[i] != null && all[i].ID == id) return all[i];
                throw new VerbArgsException($"no area with id {id} on the current map (see the `areas` verb)");
            }
            if (raw is string s)
            {
                if (s == "null" || s == "none" || s == "unrestricted") return null;
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i] == null) continue;
                    string label = Safe(() => all[i].Label);
                    if (string.Equals(label, s, StringComparison.OrdinalIgnoreCase)) return all[i];
                }
                var known = new List<string>();
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i] == null) continue;
                    if (all[i].AssignableAsAllowed()) known.Add(Safe(() => all[i].Label) ?? "?");
                }
                throw new VerbArgsException(
                    $"no area named '{s}' on the current map — assignable areas: "
                    + (known.Count > 0 ? string.Join(", ", known.ToArray()) : "(none)")
                    + ". Creating and painting areas is spec 3.2's `area` verbs, not this one.");
            }
            throw new VerbArgsException("'area' must be an area id, an area label, or null for unrestricted");
        }

        // --------------------------------------------------------------------
        // work-cover {work?} {priority?} {dry_run?}
        //
        // THE ROSTER-CHANGE REPAIR (git-bug 40ed42f). `digest.work_coverage`
        // says a floor is unmet; this puts it right, by enabling the work type
        // on the best remaining CAPABLE and AVAILABLE pawn. `WorkCoverage`'s
        // header carries where the floors come from and why Doctor's is 2.
        //
        // ---------------- WHY THIS IS A VERB AND NOT A HOOK -----------------
        // The issue's words are "on a roster change (death, downing,
        // recruitment, a colonist leaving), enable Doctor on the best remaining
        // capable pawn", and the obvious reading is a Harmony hook that repairs
        // coverage by itself. It is deliberately NOT that, for the reason
        // session 13 settled on `threat-pardon`: **the decision must be a
        // recorded ACT, not a silent exemption.** A mod that quietly reassigns
        // work behind the agent produces a colony whose state no transcript
        // explains, and the next post-mortem cannot tell an agent's decision
        // from the mod's.
        //
        // What makes it unforgettable instead is two other things this round
        // ships: `work_coverage` is in EVERY digest, so under-coverage is
        // visible at every glance rather than discovered in an emergency; and
        // `advance` halts on an own-faction downing (722c951), so the roster
        // change cannot pass unseen. The trigger is the agent, and the agent is
        // made to look.
        //
        // ------------------------------- GATES ------------------------------
        // Same three as `work-priorities`, and for the same reasons — see its
        // header. `PawnColumnWorker_WorkPriority.DoCell`'s early-out
        // (`Dead || workSettings == null || !EverWork`), `WorkTypeIsDisabled`
        // (because `Pawn_WorkSettings.SetPriority` answers that with a RED
        // ERROR), and the manual-priorities check on any priority but 0 and 3.
        [Verb("work-cover")]
        public static object WorkCover(VerbContext ctx)
        {
            const string V = "work-cover";
            var map = DesignateEngine.Map();
            var a = ctx.Args;
            bool dryRun = a.Bool("dry_run", false);

            bool usePriorities = true;
            try { usePriorities = Find.PlaySettings.useWorkPriorities; } catch { }
            int priority = a.Int("priority", 3);
            if (priority < 1 || priority > 4)
                throw new VerbArgsException(
                    "priority must be 1..4 — this verb ENABLES a work type, so 0 is not a "
                    + "value it can take. Use `work-priorities` to turn one off.");
            if (!usePriorities && priority != 3)
                throw new VerbArgsException(
                    $"priority {priority} is meaningless with manual priorities off: the Work tab "
                    + "is a checkbox then and GetPriority returns a flat 3. Use 3, or turn manual "
                    + "priorities on with `work-priorities {manual:true}`.");

            var rows = WorkCoverage.Compute(map);
            // A named work type is answered even when it is NOT under its
            // floor: "Doctor is already covered" is the answer to the question
            // asked, and returning an empty repair list instead would read as a
            // failure. An unknown name is a refusal listing the set, never a
            // silent no-op (git-bug 7382bdd's class).
            string want = a.Str("work");
            if (want != null)
            {
                WorkCoverage.Row picked = null;
                foreach (var r in rows)
                    if (string.Equals(r.Def.defName, want, StringComparison.OrdinalIgnoreCase)) { picked = r; break; }
                if (picked == null)
                {
                    var known = new List<string>();
                    foreach (var r in rows) known.Add(r.Def.defName);
                    throw new VerbArgsException(
                        $"'{want}' is not an essential work type. This verb covers the game's own "
                        + "`requireCapableColonist` set plus Doctor: "
                        + string.Join(", ", known.ToArray())
                        + ". Any other work type is `work-priorities`' job.");
                }
                rows = new List<WorkCoverage.Row> { picked };
            }

            var repaired = new List<object>();
            var stillUnder = new List<object>();
            var alreadyOk = new List<object>();
            // git-bug 58794e4 — the promotions, as objects rather than as the
            // report of them, so `coverage_after` can be PROJECTED for a dry run
            // (WorkCoverage.Project). In a real run they have already been
            // written and the map is re-read instead.
            var planned = new List<KeyValuePair<WorkTypeDef, Pawn>>();

            foreach (var r in rows)
            {
                if (!r.Under)
                {
                    alreadyOk.Add(new Dictionary<string, object>
                    {
                        ["work"] = r.Def.defName,
                        ["floor"] = r.Floor,
                        ["have"] = r.Have,
                    });
                    continue;
                }
                var ranked = WorkCoverage.Ranked(r);
                int need = r.ShortBy;
                int did = 0;
                for (int i = 0; i < ranked.Count && did < need; i++)
                {
                    var pawn = ranked[i];
                    // Ranked() already excludes disabled, non-EverWork, downed
                    // and capacity-blocked pawns. Re-check the two the game
                    // punishes with a red error anyway: this is the shape
                    // `work-priorities` uses, and a modded workSettings can
                    // disagree with a predicate evaluated a few lines earlier.
                    if (pawn.WorkTypeIsDisabled(r.Def)) continue;
                    int before;
                    try { before = pawn.workSettings.GetPriority(r.Def); } catch { continue; }
                    if (!dryRun)
                    {
                        try { pawn.workSettings.SetPriority(r.Def, priority); }
                        catch (Exception e)
                        {
                            stillUnder.Add(new Dictionary<string, object>
                            {
                                ["work"] = r.Def.defName,
                                ["gate"] = "exception",
                                ["reason"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 160),
                            });
                            continue;
                        }
                    }
                    planned.Add(new KeyValuePair<WorkTypeDef, Pawn>(r.Def, pawn));
                    repaired.Add(new Dictionary<string, object>
                    {
                        ["work"] = r.Def.defName,
                        ["pawn"] = pawn.thingIDNumber,
                        ["name"] = PawnSafe.Name(pawn),
                        ["before"] = before,
                        ["after"] = priority,
                        ["skill"] = PawnSafe.R(Skill(pawn, r.Def), 2),
                        ["chosen_by"] = "RimWorld/Pawn_WorkSettings.EnableAndInitialize's own order "
                                      + "— AverageOfRelevantSkillsFor(work) descending",
                    });
                    did++;
                }
                if (did < need)
                {
                    // THE REFUSAL IS THE POINT. "No capable pawn remains" is a
                    // real answer about the colony and must never present as a
                    // quiet success with nobody assigned. Every count that
                    // explains it rides along, because the follow-up is
                    // different for each: `enabled` short means promote,
                    // `capable` short means recruit, and an impaired list means
                    // the fix is surgery.
                    var why = new Dictionary<string, object>
                    {
                        ["work"] = r.Def.defName,
                        ["floor"] = r.Floor,
                        ["floor_on"] = r.FloorIsCapability ? "capable" : "available",
                        ["have"] = r.Have + did,
                        ["short_by"] = need - did,
                        ["enabled_this_call"] = did,
                        ["candidates_offered"] = ranked.Count,
                        // POST-CALL, LIKE `have` BESIDE THEM. `have` has always
                        // been `r.Have + did` — the projection — while these two
                        // were the pre-call snapshot, so on any call that
                        // promoted somebody the same row said `have:2` and
                        // `available:1` about a floor measured on availability.
                        // `capable` is genuinely unchanged by a promotion.
                        // The pre-call values are still recoverable: subtract
                        // `enabled_this_call` (git-bug 58794e4's second half).
                        ["capable"] = r.Capable.Count,
                        ["enabled"] = r.Enabled.Count + did,
                        ["available"] = r.Available.Count + did,
                        ["gate"] = ranked.Count == 0 ? "no-candidate" : "too-few-candidates",
                        ["reason"] = ranked.Count == 0
                            ? "no colonist on this map is capable of " + r.Def.defName
                              + ", available (spawned, not downed) and has the capacities its "
                              + "work-givers require. This is a fact about the roster, not a "
                              + "refused write."
                            : "only " + ranked.Count + " candidate(s) could be promoted and the "
                              + "floor needs " + need + " more.",
                    };
                    // UNCONDITIONAL — see WorkCoverage.Section's copy of this
                    // block for the argument. A refusal is READ FOR ITS COUNTS,
                    // and this is one of the three the note tells the caller to
                    // branch on ("`enabled` short means promote, `capable`
                    // short means recruit, an `enabled_but_incapable` list
                    // means the fix is surgery"). An absent key made "no
                    // impaired pawn" indistinguishable from "wrong dig path",
                    // which is how acceptance 7.2h went red on a correct
                    // colony. Empty list, always present.
                    var imp = new List<object>();
                    foreach (var kv in r.Impaired)
                        imp.Add(new Dictionary<string, object>
                        {
                            ["pawn"] = PawnSafe.Name(kv.Key),
                            ["missing_capacity"] = kv.Value,
                        });
                    why["enabled_but_incapable"] = imp;
                    stillUnder.Add(why);
                }
            }

            // Re-read rather than assume. The whole M1 lesson is that a verb
            // reporting what it INTENDED is not the same as the state
            // afterwards, and this one is cheap enough to just look again.
            //
            // …EXCEPT IN A DRY RUN, WHERE THERE IS NOTHING TO RE-READ. Nothing
            // was written, so a re-read reports the coverage BEFORE the repair
            // under a field named `coverage_after` — the defect git-bug 58794e4
            // is about, and the two arms of this ternary used to be identical.
            // The dry-run arm now PROJECTS the promotions it just planned onto a
            // fresh snapshot, so the field answers the question a dry run asks.
            var after = dryRun ? WorkCoverage.Section(map, planned)
                               : WorkCoverage.Section(map);
            bool covered = stillUnder.Count == 0;

            // THE EMIT GUARD IS ITS OWN ANSWER, not the seq it produced
            // (git-bug 6fc75e3). A dry run that plans a CLEAN repair owes no
            // journal line — `repaired > 0 && !dryRun` is false and there is
            // nothing still under — so it emits nothing and `seq` stays 0. But
            // `Stamp(0)` means "the emit FAILED" and says so in words: "the
            // journal writer is closed … treat anything done in this session as
            // unprovenanced." Neither clause was true, and 0 was doing double
            // duty as both "nothing was owed" and "something was owed and it did
            // not get written" — the same sentinel-conflation as
            // `enabled_but_incapable`'s absent key, one level down. Keyed on the
            // GUARD, `NoStamp()` says the honest thing and `Stamp(0)` keeps its
            // warning for the case where `Journal.Emit` really did fail.
            bool owedJournalLine = (repaired.Count > 0 && !dryRun) || stillUnder.Count > 0;
            long seq = owedJournalLine
                ? Act(V, dryRun ? "plan" : "cover",
                      repaired.Count + " promotion(s), " + stillUnder.Count + " still under",
                      new Dictionary<string, object>
                      {
                          ["repaired"] = repaired,
                          ["still_under"] = stillUnder,
                          ["dry_run"] = dryRun,
                      })
                : 0;

            return new Dictionary<string, object>
            {
                ["verb"] = V,
                // The verb's own verdict, NOT the envelope's: the call was
                // processed either way, and "the colony has no second doctor"
                // is information rather than breakage (PawnActs.cs, PLAY-LOOP
                // §act).
                ["ok"] = covered,
                ["dry_run"] = dryRun,
                ["repaired"] = repaired,
                ["still_under"] = stillUnder,
                ["already_covered"] = alreadyOk,
                ["use_priorities"] = usePriorities,
                ["priority_set"] = priority,
                ["coverage_after"] = after,
                ["action"] = Stamp(seq),
                ["note"] = "`ok` is whether every targeted floor is now met. A `still_under` row "
                         + "is a fact about the roster — read its `gate`: `no-candidate` means "
                         + "recruit or heal, `too-few-candidates` means the colony is simply too "
                         + "small, and a NON-EMPTY `enabled_but_incapable` list means the fix "
                         + "is surgery rather than a work priority. That list is always "
                         + "present; empty means nobody enabled is missing a capacity.",
            };
        }

        private static float Skill(Pawn p, WorkTypeDef w)
        {
            try { return p.skills?.AverageOfRelevantSkillsFor(w) ?? 1f; }
            catch { return 1f; }
        }
    }
}
