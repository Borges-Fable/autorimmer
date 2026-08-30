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
    // side effects: OpenAutomaticLetters runs once per FRAME, so under
    // fast-forward several letters arrive before the first opens (0.1 finding).
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
