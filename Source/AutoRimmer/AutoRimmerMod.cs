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
                Log.Message("[AutoRimmer] ready — " + VerbRegistry.Count + " verbs; root=" + Poller.Root);
            }
            catch (Exception e)
            {
                Log.Warning("[AutoRimmer] init failed, bridge disabled: " + e);
            }
        }
    }
}
