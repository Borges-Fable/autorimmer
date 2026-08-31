using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ======================================================= spec 3.4 =========
    // WARDEN OPS — prisoner, guest and slave interaction state.
    //
    // The spec body said "minimal prisoner ops (recruit toggle, release) IF
    // CHEAP; else defer". The session-4 amendment's item 1 overrules that and it
    // is right on both counts: every one of these is a field write or a two-line
    // method, and M3 IS Factions/Guests with the Hospitality cluster, which is
    // precisely the suite that overloads guest status and prisoner interaction
    // mode. Deferring this defers M3.
    //
    // WIDGET GATE — RimWorld/ITab_Pawn_Visitor.cs, which is the whole tab
    // (ITab_Pawn_Prisoner and ITab_Pawn_Guest are 12- and 22-line subclasses of
    // it). FillTab splits on `SelPawn.IsPrisonerOfColony` / `IsSlaveOfColony`,
    // and DoPrisonerTab filters the mode list through its own local
    // CanUsePrisonerInteractionMode — recruitability, wild-man-ness,
    // bloodfeeder presence, hemogenic genes, classic ideo mode and two Anomaly
    // clauses. All reproduced below; a mode the tab would not draw is refused
    // here with the clause that hid it.
    //
    // TWO RED-ERROR TRAPS, both pre-checked (zero-red-errors is a standing
    // invariant, and both are trivially reachable from a program):
    //   * RimWorld/Pawn_GuestTracker.cs SetExclusiveInteraction Log.ErrorOnce's
    //     when handed a def with `isNonExclusiveInteraction` — and Bloodfeed,
    //     HemogenFarm and Study ARE non-exclusive.
    //   * ToggleNonExclusiveInteraction Log.ErrorOnce's the other way round.
    // So `mode` and `enable`/`disable` are DIFFERENT arguments here, exactly as
    // the tab draws two different controls (a radio group and a checkbox list),
    // and each rejects a def belonging to the other.
    //
    // RELEASE IS A MODE, NOT A FLAG. `Pawn_GuestTracker.Released` is set by the
    // release JOB, not by the player; the player's control is the exclusive
    // interaction mode `Release` (PrisonerInteractionModeDefOf.Release), after
    // which a warden walks the prisoner out. Setting `Released` directly would
    // be a god-hand that skips the job, so this verb does not offer it.
    internal static partial class PawnActs
    {
        // --------------------------------------------------------------------
        // warden {pawns:[…], mode?, enable?:[…], disable?:[…], slave_mode?,
        //         recruitable?, convert_to?}
        //
        // Plural over prisoners: "set every prisoner to reduce resistance" is
        // one call, which is what the Prisoner tab's own workflow wants when a
        // raid leaves five of them.
        // --------------------------------------------------------------------
        [Verb("warden")]
        public static object Warden(VerbContext ctx)
        {
            const string V = "warden";
            var map = Map();
            var a = ctx.Args;
            var pawns = PawnList(map, a);
            var outcome = new Outcome();

            var mode = a.Has("mode") ? PrisonerMode(a.Str("mode")) : null;
            var enable = ModeList(a, "enable");
            var disable = ModeList(a, "disable");
            var slaveMode = a.Has("slave_mode") ? SlaveMode(a.Str("slave_mode")) : null;
            bool wantRecruitable = a.Has("recruitable");
            bool recruitable = wantRecruitable && a.Bool("recruitable", true);
            string convertTo = a.Str("convert_to");
            bool clear = a.Bool("clear", false);

            var levers = new List<string>();
            if (mode != null) levers.Add("mode");
            if (enable.Count > 0) levers.Add("enable");
            if (disable.Count > 0) levers.Add("disable");
            if (slaveMode != null) levers.Add("slave_mode");
            if (wantRecruitable) levers.Add("recruitable");
            if (convertTo != null) levers.Add("convert_to");
            if (clear) levers.Add("clear");
            if (levers.Count == 0)
                throw new VerbArgsException(
                    "nothing to set — pass at least one of mode, enable, disable, slave_mode, "
                    + "recruitable, convert_to, clear");

            // SetExclusiveInteraction Log.ErrorOnce's on a non-exclusive def and
            // vice versa; refuse up front rather than stain the journal.
            if (mode != null && mode.isNonExclusiveInteraction)
                throw new VerbArgsException(
                    $"'{mode.defName}' is a NON-exclusive interaction (a checkbox in the tab, not a radio "
                    + "button) — pass it in `enable`/`disable`, not `mode`. "
                    + "Pawn_GuestTracker.SetExclusiveInteraction answers this with a red error.");
            foreach (var m in enable) RequireNonExclusive(m);
            foreach (var m in disable) RequireNonExclusive(m);

            var ids = new List<object>();
            foreach (var p in pawns)
            {
                var g = p.guest;
                if (g == null) { outcome.No(p, "no-guest-tracker", "this pawn has no guest tracker"); continue; }

                var before = GuestLine(p);
                var applied = new List<object>();
                var refused = new List<object>();
                bool isPrisoner = false, isSlave = false, wildMan = false;
                try { isPrisoner = p.IsPrisonerOfColony; isSlave = p.IsSlaveOfColony; wildMan = p.IsWildMan(); }
                catch { }

                if (clear) One(p, "clear", applied, refused, () =>
                {
                    if (!isPrisoner) return "the prisoner interaction controls are drawn only for a prisoner of the colony";
                    g.SetNoInteraction();   // = SetExclusiveInteraction(MaintainOnly) + clear the checkbox set
                    return null;
                });
                if (mode != null) One(p, "mode", applied, refused, () =>
                {
                    if (!isPrisoner) return "the prisoner interaction controls are drawn only for a prisoner of the colony";
                    string hidden = ModeHidden(p, mode, wildMan);
                    if (hidden != null) return hidden;
                    g.SetExclusiveInteraction(mode);
                    // ITab_Pawn_Visitor.InteractionModeChanged: picking Convert
                    // with no target ideo defaults it to the player's primary.
                    if (mode == PrisonerInteractionModeDefOf.Convert && g.ideoForConversion == null
                        && ModsConfig.IdeologyActive)
                    {
                        try { g.ideoForConversion = Faction.OfPlayer.ideos.PrimaryIdeo; } catch { }
                    }
                    return null;
                });
                foreach (var m in enable)
                {
                    var mm = m;
                    One(p, "enable:" + mm.defName, applied, refused, () =>
                    {
                        if (!isPrisoner) return "the prisoner interaction controls are drawn only for a prisoner of the colony";
                        string hidden = ModeHidden(p, mm, wildMan);
                        if (hidden != null) return hidden;
                        g.ToggleNonExclusiveInteraction(mm, enabled: true);
                        HemogenSideEffect(p, mm, true);
                        return null;
                    });
                }
                foreach (var m in disable)
                {
                    var mm = m;
                    One(p, "disable:" + mm.defName, applied, refused, () =>
                    {
                        if (!isPrisoner) return "the prisoner interaction controls are drawn only for a prisoner of the colony";
                        g.ToggleNonExclusiveInteraction(mm, enabled: false);
                        HemogenSideEffect(p, mm, false);
                        return null;
                    });
                }
                if (slaveMode != null) One(p, "slave_mode", applied, refused, () =>
                {
                    if (!isSlave) return "the slave interaction controls are drawn only for a slave of the colony";
                    // ITab_Pawn_Visitor.DoSlaveTab: choosing Imprison with no
                    // prisoner bed is REFUSED by the tab with this message
                    // rather than applied.
                    if (slaveMode == SlaveInteractionModeDefOf.Imprison
                        && RestUtility.FindBedFor(p, p, checkSocialProperness: false,
                               ignoreOtherReservations: false, GuestStatus.Prisoner) == null)
                        return Tr("NoPrisonerBed", "no prisoner bed");
                    g.slaveInteractionMode = slaveMode;
                    return null;
                });
                if (wantRecruitable) One(p, "recruitable", applied, refused, () =>
                {
                    // Pawn_GuestTracker.Recruitable's GETTER short-circuits true
                    // for several cases (ever been a colonist, a wild man, a
                    // trade chattel, or unwavering-prisoners off), so the stored
                    // field and the effective answer can differ. Both echoed.
                    g.Recruitable = recruitable;
                    return null;
                });
                if (convertTo != null) One(p, "convert_to", applied, refused, () =>
                {
                    if (!ModsConfig.IdeologyActive) return "Ideology is not active";
                    if (!isPrisoner) return "the conversion target is drawn only for a prisoner of the colony";
                    Ideo target = null;
                    try
                    {
                        foreach (var ideo in Faction.OfPlayer.ideos.AllIdeos)
                            if (string.Equals(ideo.name, convertTo, StringComparison.OrdinalIgnoreCase)) target = ideo;
                    }
                    catch { }
                    if (target == null) return $"no player ideo named '{convertTo}'";
                    g.ideoForConversion = target;
                    return null;
                });

                var line = new Dictionary<string, object>
                {
                    ["class"] = PawnSafe.Classify(p),
                    ["applied"] = applied,
                    ["refused"] = refused,
                    ["before"] = before,
                    ["after"] = GuestLine(p),
                };
                if (applied.Count > 0) { outcome.Accepted.Add(WithPawn(p, line)); ids.Add(p.thingIDNumber); }
                else { line["gate"] = "all-refused"; line["reason"] = "no lever applied to this pawn"; outcome.Rejected.Add(WithPawn(p, line)); }
            }

            long seq = ids.Count > 0
                ? Act(V, "warden", string.Join(",", levers.ToArray()) + " x" + ids.Count,
                      new Dictionary<string, object> { ["ids"] = ids, ["levers"] = levers.ConvertAll(l => (object)l) })
                : 0;

            return outcome.Result(V, seq, new Dictionary<string, object>
            {
                ["levers"] = levers.ConvertAll(l => (object)l),
                ["modes_available"] = ModeCatalog(),
                ["note"] = "RELEASE IS A MODE: set mode:\"Release\" and a warden walks the prisoner out; "
                    + "Pawn_GuestTracker.Released is written by that job, not by the player, so this verb "
                    + "does not offer it. `mode` is the radio group (exclusive), `enable`/`disable` the "
                    + "checkbox list (non-exclusive) — mixing them is a red error in the game and a "
                    + "bad-args here.",
            });
        }

        private static void RequireNonExclusive(PrisonerInteractionModeDef m)
        {
            if (!m.isNonExclusiveInteraction)
                throw new VerbArgsException(
                    $"'{m.defName}' is an EXCLUSIVE interaction (a radio button in the tab) — pass it as "
                    + "`mode`, not in enable/disable. Pawn_GuestTracker.ToggleNonExclusiveInteraction "
                    + "answers this with a red error.");
        }

        // ITab_Pawn_Visitor.DoPrisonerTab's local CanUsePrisonerInteractionMode,
        // clause for clause. Returns null when the tab WOULD draw the mode.
        private static string ModeHidden(Pawn pawn, PrisonerInteractionModeDef mode, bool wildMan)
        {
            try
            {
                if (!pawn.guest.Recruitable && mode.hideIfNotRecruitable)
                    return "the tab hides this mode for an unrecruitable prisoner (hideIfNotRecruitable)";
                if (wildMan && !mode.allowOnWildMan)
                    return "the tab hides this mode for a wild man (allowOnWildMan is false)";
                if (mode.hideIfNoBloodfeeders && pawn.MapHeld != null && !AnyBloodfeeder(pawn.MapHeld))
                    return "the tab hides this mode when the colony has no bloodfeeder";
                if (mode.hideOnHemogenicPawns && ModsConfig.BiotechActive && pawn.genes != null
                    && pawn.genes.HasActiveGene(GeneDefOf.Hemogenic))
                    return "the tab hides this mode on a hemogenic pawn";
                if (!mode.allowInClassicIdeoMode && Find.IdeoManager.classicMode)
                    return "the tab hides this mode in classic ideology mode";
                if (ModsConfig.AnomalyActive)
                {
                    if (mode.hideIfNotStudiableAsPrisoner && !StudiableAsPrisoner(pawn))
                        return "the tab hides this mode for a prisoner that is not studiable";
                    if (mode.hideIfGrayFleshNotAppeared && !Find.Anomaly.hasSeenGrayFlesh)
                        return "the tab hides this mode until gray flesh has appeared";
                }
            }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
            return null;
        }

        // ITab_Pawn_Visitor.IsStudiable.
        private static bool StudiableAsPrisoner(Pawn pawn)
        {
            try
            {
                if (!ModsConfig.AnomalyActive) return false;
                if (!pawn.TryGetComp<CompStudiable>(out var comp) || !comp.EverStudiable()) return false;
                return pawn.kindDef.studiableAsPrisoner && !pawn.everLostEgo;
            }
            catch { return false; }
        }

        // ITab_Pawn_Visitor.ColonyHasAnyBloodfeeder. FreeColonistsAndPrisonersSpawned
        // is a cached list rebuilt on read (PawnSafe Class E), so it is walked
        // once and never held.
        private static bool AnyBloodfeeder(Map map)
        {
            try
            {
                if (!ModsConfig.BiotechActive) return false;
                foreach (var p in new List<Pawn>(map.mapPawns.FreeColonistsAndPrisonersSpawned))
                    if (p != null && p.IsBloodfeeder()) return true;
            }
            catch { }
            return false;
        }

        // ITab_Pawn_Visitor.NonExclusiveInteractionToggled: enabling HemogenFarm
        // CREATES an ExtractHemogenPack surgery bill, and disabling it removes
        // that bill. That is a real state mutation carried by the checkbox, so
        // it rides with the toggle here too rather than being silently dropped.
        private static void HemogenSideEffect(Pawn pawn, PrisonerInteractionModeDef mode, bool enabled)
        {
            try
            {
                if (!ModsConfig.BiotechActive || mode != PrisonerInteractionModeDefOf.HemogenFarm) return;
                Bill existing = null;
                var stack = pawn.BillStack?.Bills;
                if (stack != null)
                    foreach (var b in new List<Bill>(stack))
                        if (b?.recipe == RecipeDefOf.ExtractHemogenPack) { existing = b; break; }
                if (enabled)
                {
                    if (existing == null && SanguophageUtility.CanSafelyBeQueuedForHemogenExtraction(pawn))
                        HealthCardUtility.CreateSurgeryBill(pawn, RecipeDefOf.ExtractHemogenPack, null);
                }
                else if (existing != null)
                {
                    pawn.BillStack.Bills.Remove(existing);
                }
            }
            catch { }
        }

        // The guest block in 2.2's PawnSerializer vocabulary (`status`,
        // `host_faction`, `is_prisoner`, `is_slave`, `resistance`, `will`,
        // `interaction`, `released`), extended with the fields this verb writes.
        // Every one is a plain field read on Pawn_GuestTracker.
        private static Dictionary<string, object> GuestLine(Pawn pawn)
        {
            var g = pawn.guest;
            if (g == null) return null;
            var nonExclusive = new List<object>();
            try
            {
                foreach (var m in DefDatabase<PrisonerInteractionModeDef>.AllDefsListForReading)
                    if (m != null && m.isNonExclusiveInteraction && g.IsInteractionEnabled(m))
                        nonExclusive.Add(m.defName);
            }
            catch { }
            return new Dictionary<string, object>
            {
                ["status"] = SafeObj(() => pawn.GuestStatus?.ToString()),
                ["host_faction"] = g.HostFaction?.def?.defName,
                ["is_prisoner"] = g.IsPrisoner,
                ["is_slave"] = g.IsSlave,
                ["resistance"] = g.resistance >= 0f ? (object)PawnSafe.R(g.resistance, 2) : null,
                ["will"] = g.will >= 0f ? (object)PawnSafe.R(g.will, 2) : null,
                ["interaction"] = SafeObj(() => g.ExclusiveInteractionMode?.defName),
                ["interactions_enabled"] = nonExclusive,
                ["slave_interaction"] = g.slaveInteractionMode?.defName,
                // The STORED flag and the EFFECTIVE answer are different things
                // (the getter short-circuits true for ex-colonists, wild men,
                // trade chattel and non-unwavering difficulties), so both.
                ["recruitable"] = SafeObj(() => (object)g.Recruitable),
                ["ever_enslaved"] = SafeObj(() => (object)g.EverEnslaved),
                ["convert_to"] = g.ideoForConversion?.name,
                ["released"] = SafeObj(() => (object)g.Released),
            };
        }

        // What the tab could draw at all, split the way the tab splits it. Not
        // per-pawn — the per-pawn filter is ModeHidden — so a caller can see the
        // whole vocabulary from one call.
        private static Dictionary<string, object> ModeCatalog()
        {
            var exclusive = new List<object>();
            var nonExclusive = new List<object>();
            try
            {
                var all = new List<PrisonerInteractionModeDef>(
                    DefDatabase<PrisonerInteractionModeDef>.AllDefsListForReading);
                all.Sort((a, b) => a.listOrder.CompareTo(b.listOrder));
                foreach (var m in all)
                {
                    if (m == null) continue;
                    (m.isNonExclusiveInteraction ? nonExclusive : exclusive).Add(m.defName);
                }
            }
            catch { }
            var slave = new List<object>();
            try
            {
                foreach (var m in DefDatabase<SlaveInteractionModeDef>.AllDefsListForReading)
                    if (m != null) slave.Add(m.defName);
            }
            catch { }
            return new Dictionary<string, object>
            {
                ["mode"] = exclusive,
                ["enable_disable"] = nonExclusive,
                ["slave_mode"] = slave,
            };
        }

        private static PrisonerInteractionModeDef PrisonerMode(string name)
        {
            var d = DefDatabase<PrisonerInteractionModeDef>.GetNamedSilentFail(name);
            if (d != null) return d;
            var known = new List<string>();
            foreach (var m in DefDatabase<PrisonerInteractionModeDef>.AllDefsListForReading) if (m != null) known.Add(m.defName);
            throw new VerbArgsException(
                $"no PrisonerInteractionModeDef named '{name}' — known: {string.Join("|", known.ToArray())}");
        }

        private static SlaveInteractionModeDef SlaveMode(string name)
        {
            var d = DefDatabase<SlaveInteractionModeDef>.GetNamedSilentFail(name);
            if (d != null) return d;
            var known = new List<string>();
            foreach (var m in DefDatabase<SlaveInteractionModeDef>.AllDefsListForReading) if (m != null) known.Add(m.defName);
            throw new VerbArgsException(
                $"no SlaveInteractionModeDef named '{name}' — known: "
                + (known.Count > 0 ? string.Join("|", known.ToArray()) : "(none — Ideology is not active)"));
        }

        private static List<PrisonerInteractionModeDef> ModeList(VerbArgs args, string key)
        {
            var result = new List<PrisonerInteractionModeDef>();
            if (!args.Has(key)) return result;
            object raw = args.Raw(key);
            var items = raw as List<object> ?? new List<object> { raw };
            foreach (var item in items)
            {
                if (!(item is string s))
                    throw new VerbArgsException($"'{key}' entries must be PrisonerInteractionModeDef defNames");
                result.Add(PrisonerMode(s));
            }
            return result;
        }
    }
}
