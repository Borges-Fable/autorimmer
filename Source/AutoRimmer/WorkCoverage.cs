using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // WORK COVERAGE — "how many pawns could actually do this right now", per
    // essential work type, against a floor (git-bug 40ed42f).
    //
    // ================ WHY THIS IS CODE AND NOT A CHECKLIST LINE ==============
    // M1 run `m1-20260831`. The only pawn with Doctor enabled was Table, and
    // Table was the first casualty. Both colonists who died died of blood loss,
    // UNTENDED — not of the manhunter crow that downed them. The lesson
    // `one-doctor-is-zero-doctors` had been written and was cited by no
    // checklist at all. Per DESIGN's 2026-09-01 ruling, a response computable
    // end to end from published state does not get to stop at prose.
    //
    // ========================== WHERE THE FLOORS COME FROM ===================
    // Two sources, and only one of them is ours.
    //
    // 1. THE GAME'S OWN LIST. `Verse/WorkTypeDef.requireCapableColonist` is
    //    RimWorld's own "somebody must be able to do this", consumed by
    //    `Verse/StartingPawnUtility.WorkTypeRequirementsSatisfied` and
    //    `RequiredWorkTypesDisabledForEveryone`, which refuse to start a colony
    //    where a flagged work type is disabled for every starting pawn. Twelve
    //    vanilla types carry it (Firefighter, Warden, Construction, Growing,
    //    Mining, PlantCutting, Crafting, Hauling, Cleaning, plus Childcare from
    //    Biotech). Their floor here is 1, and it is a floor on CAPABILITY,
    //    because that is what the game checks. A modded work type that sets the
    //    flag is picked up for free.
    //
    //    Note what that check is NOT: it runs ONCE, at world-gen, against the
    //    STARTING pawns, and never again. A colony that loses its only miner on
    //    day 40 is never told.
    //
    // 2. DOCTOR AT 2, AND IT IS THE ONLY DEVIATION IN THIS FILE.
    //    **`Doctor.requireCapableColonist` is FALSE** — the game does not
    //    require a doctor to start a colony at all. And the floor of 2 is not a
    //    house rule either; it is the arithmetic the game's own doctor test
    //    forces. `RimWorld/Alert_NeedDoctor.Patients`:
    //
    //        (item.Spawned || item.BrieflyDespawned()) && !item.Downed
    //        && item.workSettings != null
    //        && item.workSettings.WorkIsActive(WorkTypeDefOf.Doctor)
    //
    //    `!item.Downed` is IN THE GAME'S OWN PREDICATE. One doctor's coverage
    //    is therefore zero the moment that doctor is the patient — which is
    //    exactly what happened — so a colony that intends to have a doctor
    //    available needs two. The floor is on AVAILABILITY, not capability,
    //    for the same reason.
    //
    // ============================ THE THREE COUNTS ===========================
    // `capable`   — `!pawn.WorkTypeIsDisabled(w)`, the game's own "could ever".
    // `enabled`   — `workSettings.WorkIsActive(w)`, i.e. `GetPriority(w) > 0`.
    // `available` — the `Alert_NeedDoctor` predicate above, generalised, AND
    //               not missing a capacity the work type's own work-givers
    //               require. This is the number compared against the floor.
    //
    // The gap between `enabled` and `available` is the trap one level down that
    // 40ed42f names: **every vanilla Doctor work-giver except `VisitSickPawn`
    // requires `Manipulation`** (`DoctorTendEmergency`, `DoctorTendToHumanlikes`,
    // `DoctorRescue`, `DoBillsMedicalHumanOperation`, … — eleven of twelve). A
    // doctor whose hands are gone has the work type ON, undisabled, and cannot
    // tend anybody. `RimWorld/WorkGiver.MissingRequiredCapacity` is the gate,
    // and it is reproduced here over the union of the type's work-givers.
    //
    // CAPABILITY IS READ THROUGH THE PURE FUNCTION, not through
    // `pawn.health.capacities.CapableOf`. `CapableOf` is
    // `GetLevel(c) > c.minForCapable`, and `Verse/PawnCapacitiesHandler.GetLevel`
    // LAZILY BUILDS `cachedCapacityLevels` on first read — the lazy-getter
    // hazard class `PawnSafe`'s header exists for. `PawnCapacityUtility
    // .CalculateCapacityLevel(hediffSet, c)` is what that cache stores, so the
    // number is identical and nothing is written. `PawnSerializer.Health` took
    // the same route for the same reason.
    internal static class WorkCoverage
    {
        // Ours, and the only one. See the header.
        public const int DoctorFloor = 2;
        public const string FloorByGame = "game:WorkTypeDef.requireCapableColonist";
        public const string FloorByUs = "autorimmer:one-doctor-is-zero-doctors";

        // The digest is a glance. Rows that are FINE cost three fields; a row
        // that is UNDER carries the whole diagnosis, and is NEVER truncated —
        // the cap only ever drops rows that are fine.
        private const int RowCap = 14;

        internal sealed class Row
        {
            public WorkTypeDef Def;
            public int Floor;
            public string FloorBy;
            public bool FloorIsCapability;   // the game's list floors CAPABILITY
            public readonly List<Pawn> Available = new List<Pawn>();
            public readonly List<Pawn> Capable = new List<Pawn>();
            public readonly List<Pawn> Enabled = new List<Pawn>();
            // enabled, undowned, and still cannot do the job: the trap.
            public readonly List<KeyValuePair<Pawn, string>> Impaired =
                new List<KeyValuePair<Pawn, string>>();
            public int Have => FloorIsCapability ? Capable.Count : Available.Count;
            public bool Under => Have < Floor;
            public int ShortBy => Math.Max(0, Floor - Have);
        }

        // ------------------------------------------------------------------
        // The essential set: the game's flagged types, plus Doctor.
        // Recomputed per call rather than cached — DefDatabase is fixed after
        // load, but a cache here would be one more thing to invalidate and the
        // list is twelve entries.
        internal static List<WorkTypeDef> EssentialTypes()
        {
            var outp = new List<WorkTypeDef>();
            var all = DefDatabase<WorkTypeDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                var w = all[i];
                if (w == null || !w.visible) continue;
                if (w.requireCapableColonist || w == WorkTypeDefOf.Doctor) outp.Add(w);
            }
            // The Work tab's own order, so a reader who has seen one has seen
            // both. `naturalPriority` descending, def name as the tie-break so
            // the order is stable run to run.
            outp.Sort((a, b) =>
            {
                int c = b.naturalPriority.CompareTo(a.naturalPriority);
                return c != 0 ? c : string.CompareOrdinal(a.defName, b.defName);
            });
            return outp;
        }

        internal static int FloorFor(WorkTypeDef w, out string by, out bool capabilityFloor)
        {
            if (w == WorkTypeDefOf.Doctor)
            {
                by = FloorByUs;
                capabilityFloor = false;
                return DoctorFloor;
            }
            by = FloorByGame;
            capabilityFloor = true;
            return 1;
        }

        // ------------------------------------------------------------------
        // `RimWorld/WorkGiver.MissingRequiredCapacity`, reproduced over the
        // union of the work type's work-givers. Returns the FIRST capacity the
        // pawn lacks, or null. One capacity is enough to name the problem;
        // listing all of them buys nothing an agent acts on differently.
        internal static string MissingCapacity(Pawn pawn, WorkTypeDef w)
        {
            var hs = pawn?.health?.hediffSet;
            if (hs == null || w?.workGiversByPriority == null) return null;
            for (int i = 0; i < w.workGiversByPriority.Count; i++)
            {
                var wg = w.workGiversByPriority[i];
                var caps = wg?.requiredCapacities;
                if (caps == null) continue;
                for (int j = 0; j < caps.Count; j++)
                {
                    var c = caps[j];
                    if (c == null) continue;
                    float level;
                    try { level = PawnCapacityUtility.CalculateCapacityLevel(hs, c); }
                    catch { continue; }
                    if (!(level > c.minForCapable)) return c.defName;
                }
            }
            return null;
        }

        // `RimWorld/Alert_NeedDoctor.Patients`' doctor clause, generalised.
        private static bool Responder(Pawn p)
        {
            try
            {
                if (p == null || p.Dead) return false;
                if (!(p.Spawned || p.BrieflyDespawned())) return false;
                return !p.Downed;
            }
            catch { return false; }
        }

        private static bool EverWork(Pawn p)
        {
            // PawnSafe Class B: an ungated GetPriority/WorkIsActive on a pawn
            // that never had work settings Log.Errors AND initialises them.
            try { return p?.workSettings != null && p.workSettings.EverWork; }
            catch { return false; }
        }

        // ------------------------------------------------------------------
        internal static List<Row> Compute(Map map)
        {
            var rows = new List<Row>();
            if (map == null) return rows;
            // SNAPSHOT before iterating — FreeColonistsSpawned CLEARS and
            // rebuilds one cached List instance on every access
            // (DigestVerb.ColonistSection's header). Anything below that
            // re-enters it mid-loop invalidates the enumerator.
            var colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
            var types = EssentialTypes();
            for (int t = 0; t < types.Count; t++)
            {
                var w = types[t];
                var row = new Row { Def = w };
                row.Floor = FloorFor(w, out row.FloorBy, out row.FloorIsCapability);
                for (int i = 0; i < colonists.Count; i++)
                {
                    var p = colonists[i];
                    if (p == null) continue;
                    bool disabled;
                    try { disabled = p.WorkTypeIsDisabled(w); } catch { disabled = true; }
                    if (disabled) continue;
                    row.Capable.Add(p);
                    if (!EverWork(p)) continue;
                    bool on;
                    try { on = p.workSettings.WorkIsActive(w); } catch { on = false; }
                    if (!on) continue;
                    row.Enabled.Add(p);
                    if (!Responder(p)) continue;
                    string missing = MissingCapacity(p, w);
                    if (missing != null) { row.Impaired.Add(new KeyValuePair<Pawn, string>(p, missing)); continue; }
                    row.Available.Add(p);
                }
                rows.Add(row);
            }
            return rows;
        }

        private static List<object> Names(List<Pawn> ps, int cap)
        {
            var l = new List<object>();
            for (int i = 0; i < ps.Count && i < cap; i++) l.Add(PawnSafe.Name(ps[i]));
            return l;
        }

        // ------------------------------------------------------------------
        // The digest block. Under-covered rows carry the diagnosis and are
        // never dropped; covered rows are three fields and are what the cap
        // eats.
        internal static Dictionary<string, object> Section(Map map)
        {
            List<Row> rows;
            try { rows = Compute(map); }
            catch (Exception e)
            {
                return new Dictionary<string, object>
                {
                    ["error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 160),
                };
            }

            var under = new List<object>();
            var list = new List<object>();
            int dropped = 0;
            foreach (var r in rows)
            {
                if (r.Under)
                {
                    under.Add(r.Def.defName);
                    var d = new Dictionary<string, object>
                    {
                        ["work"] = r.Def.defName,
                        ["floor"] = r.Floor,
                        ["floor_on"] = r.FloorIsCapability ? "capable" : "available",
                        ["floor_by"] = r.FloorBy,
                        ["have"] = r.Have,
                        ["short_by"] = r.ShortBy,
                        ["capable"] = r.Capable.Count,
                        ["enabled"] = r.Enabled.Count,
                        ["available"] = r.Available.Count,
                        ["available_pawns"] = Names(r.Available, 6),
                        ["candidates"] = Candidates(r, 6),
                    };
                    if (r.Impaired.Count > 0)
                    {
                        var imp = new List<object>();
                        for (int i = 0; i < r.Impaired.Count && i < 6; i++)
                            imp.Add(new Dictionary<string, object>
                            {
                                ["pawn"] = PawnSafe.Name(r.Impaired[i].Key),
                                ["missing_capacity"] = r.Impaired[i].Value,
                            });
                        // ENABLED AND STILL USELESS. Published separately from
                        // `available` because `work-priorities` does not fix it
                        // and surgery might.
                        d["enabled_but_incapable"] = imp;
                    }
                    list.Add(d);
                    continue;
                }
                if (list.Count - under.Count >= RowCap) { dropped++; continue; }
                list.Add(new Dictionary<string, object>
                {
                    ["work"] = r.Def.defName,
                    ["floor"] = r.Floor,
                    ["have"] = r.Have,
                });
            }

            return new Dictionary<string, object>
            {
                ["ok"] = under.Count == 0,
                // The headline: names only, so a caller can branch on it
                // without walking rows.
                ["under"] = under,
                ["rows"] = list,
                ["total"] = rows.Count,
                ["more"] = dropped,
                ["order"] = "under-first, then natural-priority-desc",
                ["note"] = "`have` is compared against `floor`: for the game's own "
                         + "requireCapableColonist types that is CAPABLE (its own check is "
                         + "capability, once, at world-gen), and for Doctor it is AVAILABLE "
                         + "— Alert_NeedDoctor.Patients requires `!item.Downed`, so one "
                         + "doctor is zero doctors the moment that doctor is the patient. "
                         + "`work-cover` is the repair.",
            };
        }

        // WHO TO PROMOTE, in the game's own order.
        // `RimWorld/Pawn_WorkSettings.EnableAndInitialize` orders by
        // `pawn.skills?.AverageOfRelevantSkillsFor(w) ?? 1f` DESCENDING, which
        // is the game's own answer to "who is best at this work type". Passion
        // rides along unweighted rather than folded into the score: the
        // playbook's `combat-role-passion-over-skill` is a COMBAT lesson and
        // tend quality is skill-driven, so inverting it here would be cargo.
        internal static List<Pawn> Ranked(Row r)
        {
            var cands = new List<Pawn>();
            for (int i = 0; i < r.Capable.Count; i++)
            {
                var p = r.Capable[i];
                if (r.Enabled.Contains(p)) continue;
                if (!Responder(p)) continue;
                if (!EverWork(p)) continue;
                if (MissingCapacity(p, r.Def) != null) continue;
                cands.Add(p);
            }
            cands.Sort((a, b) =>
            {
                float sa = Score(a, r.Def), sb = Score(b, r.Def);
                int c = sb.CompareTo(sa);
                return c != 0 ? c : string.CompareOrdinal(PawnSafe.Name(a), PawnSafe.Name(b));
            });
            return cands;
        }

        private static float Score(Pawn p, WorkTypeDef w)
        {
            try { return p.skills?.AverageOfRelevantSkillsFor(w) ?? 1f; }
            catch { return 1f; }
        }

        internal static List<object> Candidates(Row r, int cap)
        {
            var l = new List<object>();
            var ranked = Ranked(r);
            for (int i = 0; i < ranked.Count && i < cap; i++)
            {
                var p = ranked[i];
                var d = new Dictionary<string, object>
                {
                    ["pawn"] = PawnSafe.Name(p),
                    ["id"] = p.thingIDNumber,
                    ["skill"] = PawnSafe.R(Score(p, r.Def), 2),
                };
                try
                {
                    var rel = r.Def.relevantSkills;
                    if (rel != null && rel.Count > 0 && p.skills != null)
                    {
                        var rec = p.skills.GetSkill(rel[0]);
                        if (rec != null)
                        {
                            d["top_skill"] = rel[0].defName;
                            d["level"] = rec.Level;
                            d["passion"] = rec.passion.ToString();
                        }
                    }
                }
                catch { }
                l.Add(d);
            }
            return l;
        }
    }
}
