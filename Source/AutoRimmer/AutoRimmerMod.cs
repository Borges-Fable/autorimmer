using System;
using Verse;

namespace AutoRimmer
{
    // Wiring only. The whole init is wrapped so a failure disables the bridge
    // without touching the game (AnalyzerBridge/logrelay pattern).
    public class AutoRimmerMod : Mod
    {
        public AutoRimmerMod(ModContentPack content) : base(content)
        {
            try
            {
                VerbRegistry.RegisterAll();
                Poller.Init();
                Config.Load(Poller.Root);
                Journal.Init(Poller.Root);
                ColonySampler.InitLog(Poller.Root);
                TimeDriver.HookJournal();
                Log.Message("[AutoRimmer] ready — " + VerbRegistry.Count + " verbs; root=" + Poller.Root);
            }
            catch (Exception e)
            {
                Log.Warning("[AutoRimmer] init failed, bridge disabled: " + e);
                return;
            }
            // Separate so a patching failure degrades to a live bridge with dead
            // journal hooks (and says so) instead of no bridge at all.
            try
            {
                new HarmonyLib.Harmony("dorian.autorimmer").PatchAll();
            }
            catch (Exception e)
            {
                Log.Warning("[AutoRimmer] journal hooks failed to patch: " + e);
            }
        }
    }
}
