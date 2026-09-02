using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ============================================ git-bug e08c3e5 =============
    // THE SKILL CEILING, WHICH THE MOD REPORTED AS A MATERIAL SHORTFALL.
    //
    // Run m1-20260901 placed a barracks. Thirty-two of its thirty-three
    // elements built. The thirty-third was a `Heater`, and `construction` said
    //
    //     {"def":"Heater","state":"awaiting-materials",
    //      "missing":[{"def":"ComponentIndustrial","count":1}]}
    //
    // with THIRTY unforbidden reachable `ComponentIndustrial` on the map — the
    // same dry-run that priced the layout had printed
    // `{"def":"ComponentIndustrial","count":1,"available":30}`. Nothing was
    // missing. `Heater.constructionSkillPrerequisite` is 5 and the best
    // Construction on the roster was 4.
    //
    // ------------------------- THE GAME'S OWN GATE ---------------------------
    // `RimWorld/GenConstruct.cs CanConstruct(Thing, Pawn, bool checkSkills, …)`,
    // read by member:
    //
    //     if (checkSkills) {
    //       if (p.skills != null) {
    //         if (p.skills.GetSkill(SkillDefOf.Construction).Level
    //               < t.def.constructionSkillPrerequisite) { … return false; }
    //         if (p.skills.GetSkill(SkillDefOf.Artistic).Level
    //               < t.def.artisticSkillPrerequisite)     { … return false; }
    //       }
    //       if (p.IsColonyMech) { … RaceProps.mechFixedSkillLevel … }
    //     }
    //
    // THREE FACTS THAT DECIDE THE IMPLEMENTATION, each verified by member:
    //
    //  1. THE FIELD IS READ OFF THE BLUEPRINT, NOT OFF THE BUILT DEF.
    //     `t.def.constructionSkillPrerequisite` where `t` is the Blueprint or
    //     Frame. `RimWorld/ThingDefGenerator_Buildings.cs` copies it onto both
    //     generated defs (`NewBlueprintDef_Thing`, `NewFrameDef_Thing`) — and
    //     onto the blueprint only `if (!isInstallBlueprint)`. So a
    //     `Blueprint_Install` genuinely has NO skill gate, and reading the built
    //     def instead of `t.def` would invent one for every reinstall. Live
    //     things are asked through `OfThing`, which reads `t.def`; a preflight
    //     has no thing yet and asks `Of(BuildableDef)`, which is what
    //     `Designator_Build` reads.
    //
    //  2. BOTH PREREQUISITES MUST BE MET BY THE **SAME** COLONIST. Two maxima do
    //     not decide it: a pawn at Construction 5 / Artistic 0 beside one at
    //     Construction 0 / Artistic 6 clears a def needing 5 and 6 in neither
    //     body. `RimWorld/Designator_Build.cs DrawPlaceMouseAttachments` loops
    //     `Find.CurrentMap.mapPawns.FreeColonists` testing both levels on one
    //     pawn before it decides whether to draw
    //     `NoColonistWithAllSkillsForConstructing` in red. This class asks the
    //     same question the same way.
    //
    //  3. THE DELIVERY WORK GIVER CHECKS SKILLS TOO — WHICH IS WHY THE HEATER
    //     REPORTED A MATERIAL SHORTFALL AND NOT `ready`.
    //     `WorkGiver_ConstructDeliverResourcesToBlueprints.JobOnThing` calls
    //     `GenConstruct.CanConstruct(blueprint, pawn, def.workType, …)`, and
    //     that overload passes `checkSkills: workType == WorkTypeDefOf
    //     .Construction`. The Core def `ConstructDeliverResourcesToBlueprints`
    //     has `<workType>Construction</workType>`, so the skill clause fires,
    //     no component is hauled, and the blueprint sits short of a material it
    //     is not short of. That is the run's symptom, mechanism named.
    //
    //     **BUT THERE IS A SECOND WORK GIVER ON THE SAME CLASS UNDER HAULING.**
    //     `Core/Defs/WorkGiverDefs/WorkGivers.xml` also declares
    //     `DeliverResourcesToBlueprints` / `DeliverResourcesToFrames` with
    //     `<workType>Hauling</workType>`, and for those `checkSkills` is FALSE.
    //     `WorkGiver_ConstructDeliverResources.IsNewValidNearbyNeeder` passes
    //     `checkSkills: false` as well. So a hauler CAN top a skill-gated
    //     blueprint up, after which it is a stocked Frame that
    //     `ConstructFinishFrames` (workType Construction, skills checked) will
    //     never finish. The gate therefore wears TWO costumes, not one:
    //     `awaiting-materials` when no hauler got to it and `ready` when one
    //     did. The README's triage table sent an agent down the wrong branch in
    //     both cases, which is why `no-builder` outranks both.
    //
    // ----------------------------- WHAT IT COSTS -----------------------------
    // One roster snapshot per envelope — a copy of `FreeColonists` plus two
    // `SkillRecord.Level` reads per colonist. `Level` fills three memos on first
    // read and is a field read after (PawnSafe class F, already sanctioned).
    // Per ELEMENT it is ONE int compare in the common case, because
    // `constructionSkillPrerequisite` and `artisticSkillPrerequisite` are both 0
    // for every buildable in the M1 corpus except `Heater`, `Cooler` (5) and
    // `WoodFiredGenerator` (4) — `Gated` short-circuits and the roster loop is
    // never entered. That is what makes this affordable on `digest`'s hot path.
    //
    // ------------------------------ NOT ASKED --------------------------------
    //  * MECHS. `CanConstruct`'s `p.IsColonyMech` branch tests
    //    `RaceProps.mechFixedSkillLevel`, and `Designator_Build
    //    .AnyMechWithSkillsRequired` asks `MechanitorUtility
    //    .AnyPlayerMechCanDoWork`. `FreeColonists` is Humanlike-only, so a
    //    mech that could build this is NOT counted and the ceiling reported here
    //    may be lower than the colony's true one. Out of scope for M1
    //    (e08c3e5's own note), said in `Basis` so it is never silent.
    //  * WORK SETTINGS. `CanConstruct(Thing, Pawn, WorkTypeDef, …)` refuses
    //    first on `!pawn.workSettings.WorkIsActive(workType)`, and
    //    `Designator_Build.AnyColonistWithSkill` takes `careIfDisabled` as a
    //    parameter precisely because the two questions differ. A colonist who
    //    HAS the skill with Construction switched off is the README's existing
    //    work-priority branch and must not be dressed up as a skill ceiling.
    //  * DOWNED / DRAFTED. Vanilla's own red-text loop does not filter them and
    //    neither does this: the ceiling is a property of the ROSTER, and a
    //    colonist in bed still knows how to build a heater.
    // =========================================================================
    public static class ConstructionSkill
    {
        // Colonists read. A colony past this is not a colony whose ceiling is
        // in doubt, and the cap is reported when it bites.
        private const int RosterCap = 40;
        // Names listed in `clears`. The agent needs somebody to prioritise, not
        // a census.
        private const int ClearsCap = 6;

        public sealed class Member
        {
            public Pawn Pawn;
            public string Name;
            public int Construction;
            public int Artistic;
        }

        // ONE snapshot per envelope. `MapPawns.FreeColonists` (through
        // `FreeHumanlikesOfFaction`) CLEARS and refills a cached List on EVERY
        // access — PawnSafe class E — so it is copied once, here, before
        // anything below touches a skill record.
        public sealed class Roster
        {
            public readonly List<Member> Members = new List<Member>();
            public int BestConstruction = -1;
            public int BestArtistic = -1;
            public string BestConstructionName;
            public string BestArtisticName;
            public int More;
            public bool Read;

            public int Count => Members.Count;
        }

        public static Roster Read(Map map)
        {
            var r = new Roster();
            if (map == null) return r;
            List<Pawn> all;
            try { all = new List<Pawn>(map.mapPawns.FreeColonists); }
            catch { return r; }
            r.Read = true;
            for (int i = 0; i < all.Count; i++)
            {
                if (r.Members.Count >= RosterCap) { r.More++; continue; }
                var p = all[i];
                if (p == null) continue;
                SkillRecord con = null, art = null;
                try
                {
                    if (p.skills == null) continue;
                    con = p.skills.GetSkill(SkillDefOf.Construction);
                    art = p.skills.GetSkill(SkillDefOf.Artistic);
                }
                catch { continue; }
                if (con == null || art == null) continue;
                int cl, al;
                try { cl = con.Level; al = art.Level; }
                catch { continue; }
                var m = new Member
                {
                    Pawn = p,
                    Name = PawnSafe.Name(p),
                    Construction = cl,
                    Artistic = al,
                };
                r.Members.Add(m);
                if (cl > r.BestConstruction) { r.BestConstruction = cl; r.BestConstructionName = m.Name; }
                if (al > r.BestArtistic) { r.BestArtistic = al; r.BestArtisticName = m.Name; }
            }
            return r;
        }

        // ------------------------------------------------------- the verdict --

        public sealed class Verdict
        {
            public int ConstructionRequired;
            public int ArtisticRequired;
            public int BestConstruction;
            public int BestArtistic;
            public List<string> Clears = new List<string>();
            public int ClearsCount;
            public int RosterCount;
            public bool RosterRead;

            // A def with a prerequisite at all. FALSE for every wall, door, bed
            // and workbench in the M1 corpus, which is why nothing below this
            // line runs on the hot path in the common case.
            public bool Gated => ConstructionRequired > 0 || ArtisticRequired > 0;
            // Gated AND nobody on the roster clears it. The fourth triage
            // branch keys on this and on nothing else.
            public bool Blocked => Gated && ClearsCount == 0;

            public Dictionary<string, object> Out()
            {
                var names = new List<object>();
                for (int i = 0; i < Clears.Count; i++) names.Add(Clears[i]);
                var d = new Dictionary<string, object>
                {
                    ["construction_required"] = ConstructionRequired,
                    ["artistic_required"] = ArtisticRequired,
                    ["best_construction"] = BestConstruction < 0 ? (object)null : BestConstruction,
                    ["best_artistic"] = BestArtistic < 0 ? (object)null : BestArtistic,
                    ["clears"] = names,
                    ["clears_count"] = ClearsCount,
                    ["blocked"] = Blocked,
                };
                if (Blocked) d["hint"] = Hint();
                return d;
            }

            // The named fix, in `Materials.Availability.Hint`'s idiom: a
            // measurement the caller can act on, never a bare boolean.
            public string Hint()
            {
                if (!RosterRead)
                    return "the colonist roster could not be read, so no skill verdict was "
                        + "reached — this is not a claim that nobody can build it";
                if (RosterCount == 0)
                    return "there are no free colonists on this map at all, so nothing here can "
                        + "be built by anyone";
                bool conShort = ConstructionRequired > 0 && BestConstruction < ConstructionRequired;
                bool artShort = ArtisticRequired > 0 && BestArtistic < ArtisticRequired;
                if (conShort && artShort)
                    return "nobody has Construction " + ConstructionRequired + " (best is "
                        + BestConstructionName() + " at " + BestConstruction + ") and nobody has "
                        + "Artistic " + ArtisticRequired + " (best is " + BestArtisticName()
                        + " at " + BestArtistic + ")";
                if (conShort)
                    return "nobody has Construction " + ConstructionRequired + "; the best is "
                        + BestConstructionName() + " at " + BestConstruction;
                if (artShort)
                    return "nobody has Artistic " + ArtisticRequired + "; the best is "
                        + BestArtisticName() + " at " + BestArtistic;
                // Both ceilings are individually met and still nobody clears —
                // fact 2 in this file's header, and the only case two maxima
                // would have got wrong.
                return "no single colonist clears BOTH Construction " + ConstructionRequired
                    + " and Artistic " + ArtisticRequired + " — the best Construction is "
                    + BestConstructionName() + " at " + BestConstruction + " and the best Artistic "
                    + "is " + BestArtisticName() + " at " + BestArtistic
                    + ". RimWorld/GenConstruct.cs CanConstruct tests both on ONE pawn.";
            }

            internal string BestConName;
            internal string BestArtName;
            private string BestConstructionName() => BestConName ?? "nobody";
            private string BestArtisticName() => BestArtName ?? "nobody";
        }

        // The preflight question: a def, before any blueprint exists. This is
        // the field `RimWorld/Designator_Build.cs DrawPlaceMouseAttachments`
        // reads to decide whether to draw its red no-colonist line.
        public static Verdict Of(BuildableDef def, Roster roster)
        {
            int con = 0, art = 0;
            if (def != null)
            {
                try { con = def.constructionSkillPrerequisite; } catch { }
                try { art = def.artisticSkillPrerequisite; } catch { }
            }
            return Judge(con, art, roster);
        }

        // The live question: a Blueprint or Frame standing on the map. Read off
        // `t.def`, which is the expression `GenConstruct.CanConstruct` itself
        // uses — and which is 0 on a `Blueprint_Install` by
        // `ThingDefGenerator_Buildings`'s own `if (!isInstallBlueprint)`.
        public static Verdict OfThing(Thing t, Roster roster)
        {
            int con = 0, art = 0;
            if (t?.def != null)
            {
                try { con = t.def.constructionSkillPrerequisite; } catch { }
                try { art = t.def.artisticSkillPrerequisite; } catch { }
            }
            return Judge(con, art, roster);
        }

        private static Verdict Judge(int con, int art, Roster roster)
        {
            var v = new Verdict
            {
                ConstructionRequired = con,
                ArtisticRequired = art,
                BestConstruction = roster?.BestConstruction ?? -1,
                BestArtistic = roster?.BestArtistic ?? -1,
                RosterCount = roster?.Count ?? 0,
                RosterRead = roster?.Read ?? false,
                BestConName = roster?.BestConstructionName,
                BestArtName = roster?.BestArtisticName,
            };
            // THE SHORT CIRCUIT THAT MAKES THIS FREE. No prerequisite, no
            // question, no roster walk — which is every element of every layout
            // in the M1 corpus but three.
            if (!v.Gated || roster == null) return v;
            for (int i = 0; i < roster.Members.Count; i++)
            {
                var m = roster.Members[i];
                if (m.Construction < con || m.Artistic < art) continue;
                v.ClearsCount++;
                if (v.Clears.Count < ClearsCap) v.Clears.Add(m.Name);
            }
            return v;
        }

        // ------------------------------------------------------- the basis --

        // Published ONCE on an envelope, the way `Materials.Basis` and
        // `cancel-layout`'s `gate`/`gate_detail` are — never per row.
        public static Dictionary<string, object> Basis(Roster roster)
            => new Dictionary<string, object>
            {
                ["gate"] = "RimWorld/GenConstruct.cs CanConstruct (the checkSkills branch)",
                ["gate_detail"] =
                    "`p.skills.GetSkill(SkillDefOf.Construction).Level < t.def"
                    + ".constructionSkillPrerequisite` and the identical Artistic clause beside "
                    + "it, both asked of ONE pawn — RimWorld/Designator_Build.cs "
                    + "DrawPlaceMouseAttachments loops FreeColonists testing both levels on the "
                    + "same colonist before it draws NoColonistWithAllSkillsForConstructing. The "
                    + "prerequisite is read off the BLUEPRINT/FRAME def, which "
                    + "ThingDefGenerator_Buildings copies from the built def — except onto an "
                    + "install blueprint, which is why a reinstall is never skill-gated.",
                ["roster"] = roster?.Count ?? 0,
                ["roster_source"] = "MapPawns.FreeColonists, snapshotted",
                ["not_asked"] =
                    "MECHS (CanConstruct's p.IsColonyMech branch reads RaceProps"
                    + ".mechFixedSkillLevel and Designator_Build.AnyMechWithSkillsRequired asks "
                    + "MechanitorUtility.AnyPlayerMechCanDoWork; FreeColonists is Humanlike-only, "
                    + "so a mech colony's real ceiling may be HIGHER than reported) and WORK "
                    + "SETTINGS (a colonist with the skill and Construction switched off is a "
                    + "work-priority problem, not a skill ceiling, and must not be reported as "
                    + "one).",
                ["work_givers"] =
                    "the skill clause fires for ConstructDeliverResourcesToBlueprints/ToFrames and "
                    + "ConstructFinishFrames (workType Construction). The Core defs "
                    + "DeliverResourcesToBlueprints/ToFrames run the SAME class under workType "
                    + "Hauling, where checkSkills is false — so a hauler may still stock a "
                    + "skill-gated blueprint, leaving a Frame nobody can finish.",
            };
    }
}
