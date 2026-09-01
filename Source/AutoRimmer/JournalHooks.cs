using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // Read-only Harmony postfixes on the game's notify paths (spec 1.2). Every
    // body is try/catch-swallowed: a journal hook must never alter game flow.
    // No prefixes, no __result writes, no Rand, no lazy-init getters.
    //
    // Letters are captured from the LetterStack funnel, NOT from letter-opened
    // side effects. The 0.1 finding said this was because OpenAutomaticLetters
    // "runs once per FRAME"; that is half wrong and corrected here (1.5 doc
    // correction). It runs once per frame from Game.UpdatePlay AND once per
    // TICK from LetterStack.LetterStackTick, which is inside DoSingleTick —
    // i.e. inside our own advance loop. The hook was right anyway, for the
    // stronger reason: OpenAutomaticLetters opens at most ONE letter per call
    // and breaks, so a burst can never be reconstructed from letter-opens
    // however often it runs.
    public static class JournalHooks
    {
        private static readonly AccessTools.FieldRef<Pawn_HealthTracker, Pawn> HealthPawn =
            AccessTools.FieldRefAccess<Pawn_HealthTracker, Pawn>("pawn");
        private static readonly AccessTools.FieldRef<MentalStateHandler, Pawn> MentalPawn =
            AccessTools.FieldRefAccess<MentalStateHandler, Pawn>("pawn");

        private static int Tick()
        {
            try { return Find.TickManager.TicksGame; }
            catch { return Runtime.GameState.tick; }
        }

        private static string PawnName(Pawn p) => p?.LabelShortCap.ToString() ?? "?";

        private static string PawnFaction(Pawn p) => p?.Faction?.Name;

        // All overloads funnel here. delayTicks > 0 only queues the letter; the
        // eventual arrival re-enters with delayTicks 0, so skipping the queue
        // call is what prevents double capture. Membership in the stack is the
        // arrival test (a letter that CanShowInLetterStack rejected was dropped).
        [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter),
            typeof(Letter), typeof(string), typeof(int), typeof(bool))]
        public static class Patch_ReceiveLetter
        {
            public static void Postfix(Letter let, int delayTicks)
            {
                try
                {
                    if (delayTicks > 0 || let == null) return;
                    if (!Find.LetterStack.LettersListForReading.Contains(let)) return;
                    var payload = new Dictionary<string, object>
                    {
                        ["def"] = let.def?.defName,
                        ["label"] = let.Label.ToString(),
                    };
                    try
                    {
                        if (let is ChoiceLetter cl)
                            payload["text"] = Journal.Truncate(cl.Text.ToString(), 1500);
                    }
                    catch { }
                    try
                    {
                        var targets = let.lookTargets?.targets;
                        if (targets != null && targets.Count > 0)
                            payload["target"] = targets[0].ToString();
                    }
                    catch { }
                    if (let.relatedFaction != null) payload["faction"] = let.relatedFaction.Name;
                    Journal.Emit("letter", payload, Tick());
                }
                catch { }
            }
        }

        // The Messages funnel. AcceptsMessage dedupes flashes inside; IsLive is
        // the "it actually landed" test for the new message object.
        [HarmonyPatch(typeof(Messages), nameof(Messages.Message), typeof(Message), typeof(bool))]
        public static class Patch_Message
        {
            public static void Postfix(Message msg)
            {
                try
                {
                    if (msg == null || !Messages.IsLive(msg)) return;
                    Journal.Emit("message", new Dictionary<string, object>
                    {
                        ["text"] = Journal.Truncate(msg.text, 500),
                        ["def"] = msg.def?.defName,
                    }, Tick());
                }
                catch { }
            }
        }

        // Log hooks can fire from any thread; Journal.Emit is thread-safe and
        // the tick falls back to the published snapshot off-main.
        [HarmonyPatch(typeof(Log), nameof(Log.Error), typeof(string))]
        public static class Patch_LogError
        {
            public static void Postfix(string text)
            {
                try { Journal.EmitError(text); }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Log), nameof(Log.Warning), typeof(string))]
        public static class Patch_LogWarning
        {
            public static void Postfix(string text)
            {
                try { Journal.EmitWarning(text); }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.SetDead))]
        public static class Patch_SetDead
        {
            public static void Postfix(Pawn_HealthTracker __instance)
            {
                try
                {
                    // Playing only: mapgen kills pawns to furnish ruins with
                    // corpses, and that setup noise is not play chronology.
                    if (Current.ProgramState != ProgramState.Playing) return;
                    var pawn = HealthPawn(__instance);
                    Journal.Emit("death", new Dictionary<string, object>
                    {
                        ["pawn"] = PawnName(pawn),
                        ["faction"] = PawnFaction(pawn),
                    }, Tick());
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeDowned")]
        public static class Patch_MakeDowned
        {
            public static void Postfix(Pawn_HealthTracker __instance, DamageInfo? dinfo)
            {
                try
                {
                    if (Current.ProgramState != ProgramState.Playing) return;
                    var pawn = HealthPawn(__instance);
                    var payload = new Dictionary<string, object>
                    {
                        ["pawn"] = PawnName(pawn),
                        ["faction"] = PawnFaction(pawn),
                    };
                    if (dinfo.HasValue && dinfo.Value.Def != null) payload["damage"] = dinfo.Value.Def.defName;
                    Journal.Emit("downed", payload, Tick());
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
        public static class Patch_MentalState
        {
            public static void Postfix(bool __result, MentalStateHandler __instance,
                MentalStateDef stateDef, string reason, bool causedByMood)
            {
                try
                {
                    if (!__result || Current.ProgramState != ProgramState.Playing) return;
                    var pawn = MentalPawn(__instance);
                    var payload = new Dictionary<string, object>
                    {
                        ["pawn"] = PawnName(pawn),
                        ["faction"] = PawnFaction(pawn),
                        ["state"] = stateDef?.defName,
                        ["causedByMood"] = causedByMood,
                    };
                    if (!reason.NullOrEmpty()) payload["reason"] = reason;
                    Journal.Emit("mental_break", payload, Tick());
                }
                catch { }
            }
        }

        // ==================================================== construction ==
        // THE TWO TRANSITIONS, AS POSITIVE EVENTS. Without these, "did my build
        // finish or did something cancel it" is an inference from two absences:
        // `Frame.CompleteConstruction` destroys the frame and spawns the
        // building, `Designator_Cancel` destroys the frame and spawns nothing,
        // and both leave a cell with no blueprint and no frame on it (DESIGN
        // decisions log, 2026-09-01; git-bug d7c8088).
        //
        // POSTFIX, AND THE FRAME IS ALREADY GONE BY THE TIME WE RUN.
        // `CompleteConstruction` calls `Destroy()` on itself near the top, so
        // `__instance.Map` is NULL in the postfix while `__instance.Position`
        // and `__instance.def` are still readable. The map therefore comes from
        // the WORKER, which is an argument and is still spawned — the reason a
        // prefix is not needed and is not used (this file's rule: no prefixes,
        // no `__result` writes).
        [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
        public static class Patch_CompleteConstruction
        {
            public static void Postfix(Frame __instance, Pawn worker)
            {
                try
                {
                    if (Current.ProgramState != ProgramState.Playing) return;
                    var built = __instance?.def?.entityDefToBuild;
                    if (built == null) return;
                    var map = worker?.Map;
                    var pos = __instance.Position;
                    // The thing that resulted, found at the frame's own cell.
                    // Null for a TerrainDef, which sets the grid and produces no
                    // Thing at all — a real case, not a failure to find one.
                    Thing result = null;
                    if (map != null)
                    {
                        var list = map.thingGrid.ThingsListAtFast(pos);
                        for (int i = 0; i < list.Count; i++)
                            if (list[i]?.def == built) { result = list[i]; break; }
                    }
                    int tick = Tick();
                    var placement = Placements.NoteCompleted(__instance, result, map, tick);
                    var payload = new Dictionary<string, object>
                    {
                        ["kind"] = "completed",
                        ["def"] = built.defName,
                        ["at"] = Positions.Out(pos),
                        ["rot"] = __instance.Rotation.ToStringWord(),
                        ["stuff"] = __instance.Stuff?.defName,
                        ["worker"] = PawnName(worker),
                        ["thing_id"] = result?.thingIDNumber,
                    };
                    // The join key, when this session placed it. Absent for a
                    // blueprint the player drew or one that came out of a save —
                    // which is a different fact from a null id.
                    if (placement != null) payload["placement_id"] = placement.Id;
                    Journal.Emit("construction", payload, tick);
                }
                catch { }
            }
        }

        // The OTHER half, and it is not a cancellation: `FailConstruction`
        // destroys the frame and SPAWNS THE BLUEPRINT AGAIN
        // (`GenSpawn.Spawn(blueprint_Build, …, WipeMode.FullRefund)`), so the
        // placement goes back to `blueprint` and a pawn will try once more. The
        // count of failures is the fact worth having — a build that fails
        // repeatedly is a construction-skill problem an agent can act on — and
        // `Placements.Answer` publishes it beside the state rather than as one.
        [HarmonyPatch(typeof(Frame), nameof(Frame.FailConstruction))]
        public static class Patch_FailConstruction
        {
            public static void Postfix(Frame __instance, Pawn worker)
            {
                try
                {
                    if (Current.ProgramState != ProgramState.Playing) return;
                    var built = __instance?.def?.entityDefToBuild;
                    if (built == null) return;
                    int tick = Tick();
                    var placement = Placements.NoteFailed(__instance, worker?.Map);
                    var payload = new Dictionary<string, object>
                    {
                        ["kind"] = "failed",
                        ["def"] = built.defName,
                        ["at"] = Positions.Out(__instance.Position),
                        ["stuff"] = __instance.Stuff?.defName,
                        ["worker"] = PawnName(worker),
                        ["detail"] = "the frame was destroyed and the blueprint respawned; the "
                            + "build is not cancelled and a pawn will try again",
                    };
                    if (placement != null) payload["placement_id"] = placement.Id;
                    Journal.Emit("construction", payload, tick);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame), typeof(string))]
        public static class Patch_SaveGame
        {
            public static void Postfix(string fileName)
            {
                try
                {
                    Journal.Emit("session", new Dictionary<string, object>
                    {
                        ["kind"] = "saved",
                        ["file"] = fileName,
                    }, Tick());
                }
                catch { }
            }
        }
    }
}
