using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ======================================================= git-bug bb931b9 ==
    // save {name} — the agent may WRITE a save; only the LAUNCHER may load one.
    //
    // THAT ASYMMETRY IS DELIBERATE and it is the first thing to say, because it
    // reads like an omission and is not. `PLAY-LOOP.md` position 6: "there is
    // no load verb — loading is the LAUNCHER's job (`run-agent.sh` / the
    // autostart pattern), deliberately outside the protocol." A protocol that
    // could load would let a run rewind itself out of a mistake, and the whole
    // value of an unattended run is that its record is what happened. Writing a
    // checkpoint costs nothing and buys a post-mortem an exact snapshot; being
    // able to go back to it inside the same session buys save-scumming.
    //
    // ------------------------------- WHY AT ALL ------------------------------
    // Before this verb the only save artifact on the bench was the game's own
    // rotating autosave, five slots cycling on `autosaveIntervalDays` (1 day on
    // this bench), and the overnight driver copied the newest one out of
    // `Saves/` before it rotated away. That is *the nearest autosave PRECEDING
    // the event*, up to 60,000 ticks stale — so "the base at its peak,
    // immediately before the fight" is exactly what it could not give, and run
    // m1-20260901's saves are labelled `nearest autosave; no save verb exists`
    // for that reason. `advance` already halts on `threat` and on `casualty`;
    // with this the loop can do halt -> save -> read -> act.
    //
    // ---------------------------- THE WIDGET GATES ---------------------------
    // The player's route to a manual save is the ESC menu's "Save" option, and
    // `RimWorld/MainMenuDrawer.MainMenuOnGUI` only lists it when:
    //
    //     Current.ProgramState == ProgramState.Playing
    //     && !GameDataSaveLoader.SavingIsTemporarilyDisabled
    //     && !Current.Game.Info.permadeathMode
    //
    // All three are reproduced below and cited, per the standing "the gate
    // lives in the widget" rule — `GameDataSaveLoader.SaveGame` itself checks
    // NOTHING, which is the usual shape. The permadeath clause is a real game
    // rule and gets no bypass here: in permadeath the player cannot take a
    // manual save either, and `dev:*` is the layer that may cheat, not this
    // one. `SavingIsTemporarilyDisabled` is
    // `Find.TilePicker.Active || Find.WindowStack.WindowsPreventSave ||
    // WorldComponent_GravshipController.CutsceneInProgress`.
    //
    // Two more, from the save DIALOG's own type-in field
    // (`RimWorld/Dialog_FileList.DoTypeInField`): the name must be non-empty
    // (`typingName.NullOrEmpty()` -> "NeedAName") and must satisfy
    // `Verse/GenText.IsValidFilename` — at most 40 characters, and none of
    // `Path.GetInvalidFileNameChars()` plus `/\{}<>:*|!@#$%^&*?`. We REFUSE an
    // invalid name rather than sanitising it. Vanilla's
    // `Dialog_SaveFileList_Save.DoFileInteraction` calls
    // `GenFile.SanitizedFileName` first, which silently strips characters — for
    // a human typing at a keyboard that is a kindness, but the caller here is a
    // program that will look for the path it asked for, so exact-or-refuse
    // (git-bug acee526's rule) is the right shape. Note the illegal set
    // contains `/` and `\`, which is also what makes `../escape` impossible.
    //
    // ---------------------- TWO RULES THAT ARE OURS, NOT THE GAME'S ---------
    //  1. **A name the game itself would classify as an autosave is refused.**
    //     `Verse/SaveGameFilesUtility.IsAutoSave` is a prefix test on the first
    //     eight characters, and `RimWorld/Autosaver.NewAutosaveFileName` picks
    //     the oldest file literally named `Autosave-<1..Prefs.AutosavesCount>`.
    //     So writing `Autosave-3.rws` ourselves would hand a rotating slot a
    //     file the rotation will later overwrite — the issue's "must not
    //     consume a rotating slot", enforced by refusing the only names that
    //     could. `GameDataSaveLoader.SaveGame` has no autosave awareness at
    //     all; the rotation lives entirely in `Autosaver` and is name-based.
    //  2. **An existing name is refused unless `overwrite:true`.** Vanilla
    //     overwrites silently (the dialog's file list is the confirmation).
    //     A program has no such list in front of it, so the default is refuse.
    //     `SaveGameFilesUtility.SavedGameNamedExists` is the test, which is the
    //     game's own and matches on the name without the extension.
    //
    // -------------------------- THE FAILURE HAZARD --------------------------
    // `GameDataSaveLoader.SaveGame` RETURNS VOID and swallows its own
    // exception into a `Log.Error`. Worse, `Verse/SafeSaver.Save` pops
    // `GenUI.ErrorDialog(...)` before rethrowing — a `Dialog_MessageBox`, which
    // sets `forcePause = true`, and per JOURNAL.md (spec 1.7) a force-pausing
    // window halts EVERY subsequent `advance` with reason `"dialog"`, is not
    // suppressible, and cannot be closed from here. Same class as
    // `FlickUtility.UpdateFlickDesignation`'s tutorial modal, and the reason
    // that one is re-implemented line for line in DesignationVerbs.
    //
    // We cannot re-implement `SaveGame`, so instead the verb VERIFIES: it
    // stats the file afterwards and publishes `written`, `bytes` and
    // `force_pause` (via `TimeDriver.ForcePausePayload`, the same payload
    // `status.json` carries). A save that did not land says so in the same
    // envelope that says the window is up, which is the only honest answer
    // available for a void method that eats its own errors.
    //
    // ------------------------ WHY IT IS NOT A LONG EVENT --------------------
    // Vanilla wraps the call in `LongEventHandler.QueueLongEvent(...,
    // doAsynchronously: false, ...)` purely for the "Saving" progress screen.
    // Queuing it here would return BEFORE the write, so the verb could not
    // report the path, the byte count or the tick the snapshot captured — and
    // the tick is the whole point of a checkpoint. The call runs synchronously
    // on the main thread at the `GameComponentUpdate` safe point, which is
    // where `Autosaver.DoAutosave` ends up too.
    //
    // -------------------------- BUSY, AND WHY IT IS FREE --------------------
    // "Callable while paused and while no advance is in flight; a `busy`
    // refusal is fine." That needs no code: `AgentGameComponent.DrainCommands`
    // already answers `Err.Busy` to every main-thread verb except `pause` while
    // `TimeDriver.Active`. This verb is main-thread and not `pause`, so it
    // inherits the refusal.
    //
    // ------------------------------ SIDE EFFECT -----------------------------
    // "Observers never mutate" is the standing invariant; THIS IS NOT AN
    // OBSERVER. A save is a real side effect on disk, and it journals as an
    // `action` like every other mutating verb so the transcript shows when the
    // run took one.
    // =========================================================================
    public static class SaveVerbs
    {
        // GenText.IsValidFilename's own ceiling, restated so the refusal can
        // name it. Verse/GenText.cs.
        public const int MaxNameLength = 40;

        // Verse/SaveGameFilesUtility.IsAutoSave — a prefix test on the first
        // eight characters, which is also the prefix
        // Verse/GameDataSaveLoader.AutosavePrefix declares.
        public const string AutosavePrefix = "Autosave";

        [Verb("save")]
        public static object Save(VerbContext ctx)
        {
            var a = ctx.Args;
            string name = a.StrReq("name");
            bool overwrite = a.Bool("overwrite", false);

            // ---- the name, exact-or-refuse -----------------------------
            string trimmed = name.Trim();
            if (trimmed.Length == 0)
                throw new VerbArgsException(
                    "arg 'name' is empty — RimWorld/Dialog_FileList.DoTypeInField refuses a "
                    + "blank name (\"NeedAName\") and there is no default here");
            if (!GenText.IsValidFilename(trimmed))
                throw new VerbArgsException(
                    $"'{trimmed}' is not a valid save name: Verse/GenText.IsValidFilename caps a "
                    + $"name at {MaxNameLength} characters and rejects any of "
                    + "Path.GetInvalidFileNameChars() plus /\\{}<>:*|!@#$%^&*?. The name is "
                    + "REFUSED rather than sanitised, because the caller is a program that will "
                    + "look for the path it asked for (vanilla's Dialog_SaveFileList_Save calls "
                    + "GenFile.SanitizedFileName and silently writes a different file)");
            if (SaveGameFilesUtility.IsAutoSave(trimmed))
                throw new VerbArgsException(
                    $"'{trimmed}' would be classified as an AUTOSAVE by the game itself "
                    + "(Verse/SaveGameFilesUtility.IsAutoSave is a prefix test on the first 8 "
                    + $"characters, \"{AutosavePrefix}\"), and RimWorld/Autosaver"
                    + ".NewAutosaveFileName rotates over exactly those names — so this save "
                    + "would later be overwritten by the rotation, or would consume a slot. "
                    + "Pick a name that does not start with that prefix");

            // ---- the widget gates, all three, cited --------------------
            // RimWorld/MainMenuDrawer.MainMenuOnGUI's Save option:
            //   ProgramState.Playing && !SavingIsTemporarilyDisabled && !permadeathMode
            if (Current.ProgramState != ProgramState.Playing)
                throw new VerbArgsException(
                    "no game is being played (Current.ProgramState is "
                    + Current.ProgramState + "); RimWorld/MainMenuDrawer.MainMenuOnGUI offers "
                    + "Save only while ProgramState.Playing");
            var game = Current.Game
                ?? throw new VerbArgsException("no active game to save");

            bool permadeath = false;
            try { permadeath = game.Info != null && game.Info.permadeathMode; } catch { }
            if (permadeath)
                throw new VerbArgsException(
                    "this colony is in PERMADEATH mode, and the game does not offer a manual "
                    + "save in permadeath — RimWorld/MainMenuDrawer.MainMenuOnGUI gates its "
                    + "Save option on `!Current.Game.Info.permadeathMode`. The gate lives in "
                    + "the widget and a player verb may not bypass it; the permadeath save is "
                    + "the autosaver's, under Current.Game.Info.permadeathModeUniqueName");

            bool disabled;
            try { disabled = GameDataSaveLoader.SavingIsTemporarilyDisabled; }
            catch { disabled = false; }
            if (disabled)
                throw new VerbArgsException(
                    "saving is temporarily disabled — Verse/GameDataSaveLoader"
                    + ".SavingIsTemporarilyDisabled is true (Find.TilePicker.Active, a window "
                    + "with preventSave on the stack, or a gravship cutscene). The autosaver "
                    + "skips its own save on this same test");

            // ---- the overwrite gate, ours ------------------------------
            bool exists;
            try { exists = SaveGameFilesUtility.SavedGameNamedExists(trimmed); }
            catch { exists = false; }
            if (exists && !overwrite)
                throw new VerbArgsException(
                    $"a save named '{trimmed}' already exists. Vanilla's save dialog overwrites "
                    + "silently — the file list in front of the player IS the confirmation — "
                    + "and a program has no such list, so this refuses instead. Pass "
                    + "overwrite:true to replace it, or pick another name");

            string path;
            try { path = GenFilePaths.FilePathForSavedGame(trimmed); }
            catch (Exception e)
            {
                throw new VerbArgsException("could not resolve the save path: "
                    + e.GetType().Name + ": " + e.Message);
            }

            int tick = 0;
            try { tick = Find.TickManager.TicksGame; } catch { }
            long before = FileSize(path);

            // ---- the write ---------------------------------------------
            // One line, and it is the vanilla call. Synchronously, not through
            // LongEventHandler — see the header. It returns void and swallows
            // its own exception, so everything after this is verification.
            string threw = null;
            try { GameDataSaveLoader.SaveGame(trimmed); }
            catch (Exception e) { threw = e.GetType().Name + ": " + e.Message; }

            long bytes = FileSize(path);
            // "There is a file with content at that path now." On a FRESH name
            // that is proof the write landed. On an OVERWRITE it is not — the
            // old file would satisfy it too — so `overwrote` and `bytes_before`
            // are published beside it and a caller comparing them has exactly
            // the evidence we do. Claiming more than that would be inventing a
            // success signal a void method never gave us.
            bool written = bytes > 0;
            var data = new Dictionary<string, object>
            {
                ["verb"] = "save",
                ["name"] = trimmed,
                ["path"] = path,
                ["tick"] = tick,
                ["sid"] = Runtime.SessionId,
                ["written"] = written,
                ["bytes"] = bytes,
                ["overwrote"] = exists,
                ["bytes_before"] = exists ? (object)before : null,
                ["gate"] = "RimWorld/MainMenuDrawer.MainMenuOnGUI (ProgramState.Playing && "
                    + "!GameDataSaveLoader.SavingIsTemporarilyDisabled && !Info.permadeathMode)"
                    + " + RimWorld/Dialog_FileList.DoTypeInField (GenText.IsValidFilename)",
                ["call"] = "Verse/GameDataSaveLoader.SaveGame",
                // The asymmetry, in the envelope and not only in a comment —
                // an agent reading this back is the audience for it.
                ["note"] = "the agent may WRITE a save; only the LAUNCHER may load one "
                    + "(PLAY-LOOP.md position 6). There is deliberately no load verb: a "
                    + "protocol that could rewind is a protocol whose record is not what "
                    + "happened.",
                ["autosave_slots"] = AutosaveSlots(),
            };
            if (threw != null) data["threw"] = threw;

            // A save that FAILED pops GenUI.ErrorDialog, which is a
            // Dialog_MessageBox, which sets forcePause — and per spec 1.7 that
            // halts every subsequent `advance` with reason "dialog". Published
            // whenever it is up, so "the save did not land" and "the run is now
            // wedged" arrive together instead of the second being discovered
            // three advances later.
            try
            {
                var stack = Find.WindowStack;
                if (stack != null && stack.WindowsForcePause)
                    data["force_pause"] = TimeDriver.ForcePausePayload(stack);
            }
            catch { }

            if (!written)
                data["failed"] = "no file at " + path + " after Verse/GameDataSaveLoader"
                    + ".SaveGame returned. That method is `void` and catches its own exception "
                    + "into a Log.Error, so the failure is invisible to the caller — but "
                    + "Verse/SafeSaver.Save pops GenUI.ErrorDialog first, which is a "
                    + "Dialog_MessageBox with forcePause set, so `advance` will now halt on "
                    + "reason \"dialog\" until that window is dealt with. Check `force_pause` "
                    + "and the journal's red errors.";

            data["action"] = Act("save", overwrite && exists ? "overwrite" : "write",
                trimmed, new Dictionary<string, object>
                {
                    ["path"] = path,
                    ["tick"] = tick,
                    ["bytes"] = bytes,
                    ["written"] = written,
                    ["sid"] = Runtime.SessionId,
                });
            return data;
        }

        // -1 when the file is not there or cannot be stat'd, so "absent" and
        // "empty" do not read alike.
        private static long FileSize(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? fi.Length : -1;
            }
            catch { return -1; }
        }

        // The rotation, as it stands right now: the names
        // RimWorld/Autosaver.AutoSaveNames yields (`Autosave-1` ..
        // `Autosave-<Prefs.AutosavesCount>`) and whether each exists. Published
        // so the acceptance can assert THE SLOTS WERE UNTOUCHED from the
        // envelope itself rather than by shelling out to `ls` — the issue's
        // acceptance bullet, made checkable.
        private static object AutosaveSlots()
        {
            var list = new List<object>();
            int count;
            try { count = Prefs.AutosavesCount; } catch { count = 0; }
            for (int i = 1; i <= count; i++)
            {
                string n = "Autosave-" + i;
                long size;
                try { size = FileSize(GenFilePaths.FilePathForSavedGame(n)); }
                catch { size = -1; }
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = n,
                    ["exists"] = size >= 0,
                    ["bytes"] = size,
                });
            }
            return new Dictionary<string, object>
            {
                ["count"] = count,
                ["source"] = "RimWorld/Autosaver.AutoSaveNames (1..Prefs.AutosavesCount)",
                ["slots"] = list,
                ["note"] = "this verb never writes one of these: a name the game's own "
                    + "SaveGameFilesUtility.IsAutoSave would classify as an autosave is "
                    + "refused at argument time",
            };
        }

        // ------------------------------------------------------------------
        // The `action` journal row: {verb, step, target} plus additive extras,
        // mirroring the `dev` row's shape but carrying neither `cheat` nor
        // `fog_exempt`, because a player verb is not a cheat. Journal.Emit
        // returns 0 when the writer is closed and this SAYS SO rather than
        // looking like a normal success.
        //
        // Private static in this file on purpose: DesignationVerbs, ZoneVerbs,
        // StorageVerbs, AreaVerbs and TemperatureVerbs each carry their own
        // copy for the same reason — a shared public type collides at merge
        // between parallel worktrees, and the orchestrator owns any factoring.
        private static Dictionary<string, object> Act(string verb, string step, string target,
            Dictionary<string, object> extra)
        {
            var payload = new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["step"] = step,
                ["target"] = target,
            };
            if (extra != null)
                foreach (var kv in extra)
                    if (!payload.ContainsKey(kv.Key) && kv.Value != null) payload[kv.Key] = kv.Value;
            int tick = 0;
            try { tick = Find.TickManager.TicksGame; } catch { }
            long seq = Journal.Emit("action", payload, tick);
            var d = new Dictionary<string, object> { ["journal_seq"] = seq };
            if (seq == 0)
                d["provenance"] = "NOT WRITTEN — the journal writer is closed, so this save has "
                    + "no journal line. The file on disk is real; its provenance is not.";
            return d;
        }
    }
}
