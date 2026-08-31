using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // ============================================================ spec 3.1 ===
    // The pawn-side half of the dev layer. Same three rules as DevVerbs.cs
    // (journal every mutation, disclose the cheat, wrap the DebugAction) — see
    // that file's header; this one holds only what is specific to pawns.
    //
    // WHAT THIS SUPERSEDES. `pawn-fixture` (2.2) reached three of these states
    // by hand because no dev layer existed: `wound` -> dev:damage
    // {mode:"amount"}, `prisoner` -> dev:spawn-pawn + dev:guest-status,
    // `visitor` -> dev:incident {def:"VisitorGroup"}. `journal-selftest` (1.2)
    // reached two: `downed` -> dev:damage {mode:"until-downed"}, `break` ->
    // dev:mental-state. Both files STAY — their own acceptance replays run
    // through them — and neither is edited here.
    //
    // The one thing pawn-fixture does that has no verb here is `sadden`
    // (stacking ThoughtDefOf.DebugBad memories). That is thought-system
    // stimulus for a serializer test, not fixture staging: a fixture wants a
    // mood VALUE, which dev:set-need gives, and the honest caveat about that
    // value is documented on the verb rather than papered over.
    // =========================================================================
    public static class DevPawnVerbs
    {
        // --------------------------------------------------------------------
        // dev:heal {pawn, mode?}   mode = injuries (default) | full | tend
        //
        // Provenance:
        //  * injuries — Verse/HealthUtility.HealNonPermanentInjuriesAndRestoreLegs,
        //    the call behind "Heal fully" in the pawn debug menus: drops every
        //    non-permanent Hediff_Injury and restores missing MovingLimbCore
        //    parts whose parent is still attached.
        //  * tend — Verse/DebugTools_Health.TendBleedingHediffs (hediff.Tended(1,1)
        //    over everything bleeding), which stops bleed-out without erasing
        //    the wounds a test may be asserting on.
        //  * full — `injuries`, then RestorePart over every remaining missing
        //    part, then RemoveHediff over everything still flagged
        //    HediffDef.isBad. That last step is OURS, not a DebugAction: it is
        //    what clears diseases, addictions and permanent scars so a fixture
        //    starts from a known-clean pawn. It deliberately keeps hediffs with
        //    isBad:false — bionics, implants, pregnancy — because removing a
        //    prosthetic is not "healing".
        // --------------------------------------------------------------------
        [Verb("dev:heal")]
        public static object Heal(VerbContext ctx)
        {
            const string V = "dev:heal";
            Dev.Gate(V);
            var map = Dev.CurrentMap(V);
            var pawn = Dev.PawnArg(map, ctx.Args, "pawn");
            string mode = ctx.Args.Str("mode", "injuries");
            if (pawn.Dead) throw new VerbArgsException($"{PawnSafe.Name(pawn)} is dead; dev:heal cannot resurrect");
            if (pawn.health == null) throw new VerbArgsException("that pawn has no health tracker");

            int before = pawn.health.hediffSet.hediffs.Count;
            float pctBefore = pawn.health.summaryHealth.SummaryHealthPercent;
            var removed = new List<object>();
            var restored = new List<object>();

            switch (mode)
            {
                case "tend":
                {
                    var bleeding = new List<Hediff>(pawn.health.hediffSet.hediffs);
                    foreach (var h in bleeding)
                        if (h.Bleeding) { h.Tended(1f, 1f); removed.Add(Describe(h, "tended")); }
                    break;
                }
                case "injuries":
                    HealthUtility.HealNonPermanentInjuriesAndRestoreLegs(pawn);
                    break;
                case "full":
                {
                    HealthUtility.HealNonPermanentInjuriesAndRestoreLegs(pawn);
                    // Snapshot before mutating: RestorePart and RemoveHediff both
                    // rewrite hediffSet.hediffs, and iterating the live list
                    // while it is edited is the enumerator bug 2.1 shipped.
                    var snapshot = new List<Hediff>(pawn.health.hediffSet.hediffs);
                    foreach (var h in snapshot)
                    {
                        if (h is Hediff_MissingPart missing && missing.Part != null)
                        {
                            restored.Add(new Dictionary<string, object> { ["part"] = missing.Part.def?.defName });
                            pawn.health.RestorePart(missing.Part);
                        }
                    }
                    snapshot = new List<Hediff>(pawn.health.hediffSet.hediffs);
                    foreach (var h in snapshot)
                    {
                        if (h?.def == null || !h.def.isBad) continue;
                        removed.Add(Describe(h, "removed"));
                        try { pawn.health.RemoveHediff(h); } catch { }
                    }
                    break;
                }
                default:
                    throw new VerbArgsException("mode must be injuries|full|tend");
            }

            long seq = Dev.Emit(V, "heal", PawnSafe.Name(pawn) + " (" + mode + ")",
                new Dictionary<string, object>
                {
                    ["pawn"] = pawn.thingIDNumber,
                    ["mode"] = mode,
                    ["hediffs_before"] = before,
                    ["hediffs_after"] = pawn.health.hediffSet.hediffs.Count,
                });

            return new Dictionary<string, object>
            {
                ["pawn"] = Dev.Describe(pawn),
                ["mode"] = mode,
                ["hediffs_before"] = before,
                ["hediffs_after"] = pawn.health.hediffSet.hediffs.Count,
                ["health_pct_before"] = PawnSafe.Pct(pctBefore),
                ["health_pct_after"] = PawnSafe.Pct(pawn.health.summaryHealth.SummaryHealthPercent),
                ["bleed_rate"] = PawnSafe.R(pawn.health.hediffSet.BleedRateTotal, 2),
                ["downed"] = pawn.Downed,
                ["removed"] = removed,
                ["restored"] = restored,
                ["dev"] = Dev.Stamp(seq),
            };
        }

        private static Dictionary<string, object> Describe(Hediff h, string what)
            => new Dictionary<string, object>
            {
                ["def"] = h.def?.defName,
                ["part"] = h.Part?.def?.defName,
                ["severity"] = PawnSafe.R(h.Severity, 2),
                ["action"] = what,
            };

        // --------------------------------------------------------------------
        // dev:damage {pawn, mode?, amount?, def?, part?, hits?, allow_bleeding?}
        // mode = amount (default) | until-downed | until-dead | legs | manipulation
        //
        // Provenance: Verse/DebugToolsPawns.DamageUntilDown / DamageToDeath /
        // DamageLegs / DamageUntilIncapableOfManipulation, all one-liners into
        // Verse/HealthUtility; and Verse/DebugTools_Health.Options_Damage_BodyParts
        // for the targeted `amount` case (TakeDamage with an explicit
        // BodyPartRecord).
        //
        // NOT IN THE SPEC BODY — proposed and resolved on git-bug f166fb9. It
        // supersedes pawn-fixture's `wound` and journal-selftest's `downed`, and
        // the WOUNDED-AND-STANDING state (the one 2.2's acceptance needs and
        // DamageUntilDowned by definition cannot produce) has no other route:
        // `mode:"amount"` with `hits` stops the instant the pawn goes down.
        // --------------------------------------------------------------------
        [Verb("dev:damage")]
        public static object Damage(VerbContext ctx)
        {
            const string V = "dev:damage";
            Dev.Gate(V);
            var map = Dev.CurrentMap(V);
            var a = ctx.Args;
            var pawn = Dev.PawnArg(map, a, "pawn");
            if (pawn.Dead) throw new VerbArgsException($"{PawnSafe.Name(pawn)} is already dead");

            string mode = a.Str("mode", "amount");
            bool allowBleeding = a.Bool("allow_bleeding", true);
            var damageDef = a.Has("def") ? Dev.Named<DamageDef>(a.Str("def"), "def") : DamageDefOf.Cut;
            int landed = 0;

            switch (mode)
            {
                case "amount":
                {
                    float amount = (float)a.Num("amount", 6);
                    int hits = a.Int("hits", 1);
                    if (hits < 1 || hits > 20) throw new VerbArgsException("hits must be 1..20");
                    BodyPartRecord part = a.Has("part") ? Part(pawn, a.Str("part")) : null;
                    // Stops at DOWNED, not at dead: the wounded-and-standing
                    // fixture is the whole point, and a fixture that
                    // accidentally kills its subject wastes a whole run.
                    for (int i = 0; i < hits && !pawn.Downed && !pawn.Dead; i++)
                    {
                        pawn.TakeDamage(new DamageInfo(damageDef, amount, 0f, -1f, null, part));
                        landed++;
                    }
                    break;
                }
                case "until-downed":
                    HealthUtility.DamageUntilDowned(pawn, allowBleeding, damageDef);
                    break;
                case "until-dead":
                    HealthUtility.DamageUntilDead(pawn, damageDef);
                    break;
                case "legs":
                    HealthUtility.DamageLegsUntilIncapableOfMoving(pawn, allowBleeding);
                    break;
                case "manipulation":
                    HealthUtility.DamageLimbsUntilIncapableOfManipulation(pawn, allowBleeding);
                    break;
                default:
                    throw new VerbArgsException(
                        "mode must be amount|until-downed|until-dead|legs|manipulation");
            }

            var injuries = new List<object>();
            var hs = pawn.health.hediffSet.hediffs;
            for (int i = 0; i < hs.Count && injuries.Count < 24; i++)
            {
                if (!(hs[i] is Hediff_Injury)) continue;
                injuries.Add(new Dictionary<string, object>
                {
                    ["def"] = hs[i].def?.defName,
                    ["part"] = hs[i].Part?.def?.defName,
                    ["severity"] = PawnSafe.R(hs[i].Severity, 2),
                    ["bleeding"] = PawnSafe.R(hs[i].BleedRate, 3),
                });
            }

            long seq = Dev.Emit(V, "damage",
                PawnSafe.Name(pawn) + " " + mode + (landed > 0 ? " x" + landed : "")
                + (pawn.Dead ? " (DEAD)" : pawn.Downed ? " (downed)" : " (standing)"),
                new Dictionary<string, object>
                {
                    ["pawn"] = pawn.thingIDNumber,
                    ["mode"] = mode,
                    ["damage"] = damageDef.defName,
                });

            return new Dictionary<string, object>
            {
                ["pawn"] = Dev.Describe(pawn),
                ["mode"] = mode,
                ["damage_def"] = damageDef.defName,
                ["hits_landed"] = landed,
                ["downed"] = pawn.Downed,
                ["dead"] = pawn.Dead,
                ["health_pct"] = PawnSafe.Pct(pawn.health.summaryHealth.SummaryHealthPercent),
                ["bleed_rate"] = PawnSafe.R(pawn.health.hediffSet.BleedRateTotal, 2),
                ["injuries"] = injuries,
                ["dev"] = Dev.Stamp(seq),
            };
        }

        // --------------------------------------------------------------------
        // dev:set-need {pawn, need, val|level}
        // Provenance: RimWorld/Need.CurLevelPercentage (setter routes through
        // CurLevel, which clamps to [0, MaxLevel]) via
        // Pawn_NeedsTracker.TryGetNeed(NeedDef). There is no vanilla DebugAction
        // that sets an arbitrary need to an arbitrary value — the menus offer
        // "fill all needs" style helpers — so this is the model call, guarded.
        //
        // THE HONEST CAVEAT, reported per call rather than buried: for a
        // Need_Seeker (mood, beauty, comfort, room size, indoors/outdoors) the
        // value you set is TRANSIENT. Need_Seeker.NeedInterval drives CurLevel
        // back toward CurInstantLevel — the value the thought/room system
        // computes — at def.seekerRisePerHour/seekerFallPerHour per hour. Set a
        // colonist's mood to 0.1 and it climbs straight back. `volatile:true`
        // plus the instant level and the per-hour rates come back in the result
        // so the caller can see the drift rather than discover it.
        // --------------------------------------------------------------------
        [Verb("dev:set-need")]
        public static object SetNeed(VerbContext ctx)
        {
            const string V = "dev:set-need";
            Dev.Gate(V);
            var map = Dev.CurrentMap(V);
            var a = ctx.Args;
            var pawn = Dev.PawnArg(map, a, "pawn");
            if (pawn.needs == null) throw new VerbArgsException($"{PawnSafe.Name(pawn)} has no needs tracker");

            var def = Dev.Named<NeedDef>(a.StrReq("need"), "need");
            var need = pawn.needs.TryGetNeed(def);
            if (need == null)
                throw new VerbArgsException(
                    $"{PawnSafe.Name(pawn)} has no '{def.defName}' need (present: {NeedNames(pawn)})");

            float before = need.CurLevel;
            if (a.Has("level")) need.CurLevel = (float)a.NumReq("level");
            else
            {
                double val = a.NumReq("val");
                if (val < 0 || val > 1) throw new VerbArgsException("val must be 0..1 (a fraction of MaxLevel)");
                need.CurLevelPercentage = (float)val;
            }

            bool drifts = need is Need_Seeker;
            long seq = Dev.Emit(V, "set-need",
                PawnSafe.Name(pawn) + " " + def.defName + " "
                + PawnSafe.Pct(before / Math.Max(0.0001f, need.MaxLevel)) + "% -> "
                + PawnSafe.Pct(need.CurLevelPercentage) + "%",
                new Dictionary<string, object> { ["pawn"] = pawn.thingIDNumber, ["need"] = def.defName });

            var data = new Dictionary<string, object>
            {
                ["pawn"] = Dev.Describe(pawn),
                ["need"] = def.defName,
                ["max_level"] = PawnSafe.R(need.MaxLevel, 3),
                ["level_before"] = PawnSafe.R(before, 3),
                ["level_after"] = PawnSafe.R(need.CurLevel, 3),
                ["pct_after"] = PawnSafe.Pct(need.CurLevelPercentage),
                ["volatile"] = drifts,
                ["dev"] = Dev.Stamp(seq),
            };
            if (drifts)
            {
                data["instant_pct"] = PawnSafe.Pct(need.CurInstantLevelPercentage);
                data["seeker_rise_per_hour"] = PawnSafe.R(def.seekerRisePerHour, 3);
                data["seeker_fall_per_hour"] = PawnSafe.R(def.seekerFallPerHour, 3);
                data["note"] = "this is a Need_Seeker: NeedInterval walks CurLevel back toward the "
                    + "instant level (computed from thoughts/room), so the value you set decays. "
                    + "Assert on it immediately, or change what DRIVES it.";
            }
            return data;
        }

        private static string NeedNames(Pawn pawn)
        {
            var n = new List<string>();
            var all = pawn.needs.AllNeeds;
            for (int i = 0; i < all.Count; i++) if (all[i]?.def != null) n.Add(all[i].def.defName);
            return string.Join(",", n.ToArray());
        }

        // --------------------------------------------------------------------
        // dev:add-hediff {pawn, def, part?, severity?}
        // Provenance: Verse/DebugTools_Health.Options_Hediff_BodyParts —
        // `p.health.AddHediff(def, part).PostDebugAdd()`. PostDebugAdd is the
        // half a hand-rolled AddHediff forgets: it is what makes a debug-added
        // hediff behave (e.g. Hediff_Pregnant seeding, addiction setup).
        // --------------------------------------------------------------------
        [Verb("dev:add-hediff")]
        public static object AddHediff(VerbContext ctx)
        {
            const string V = "dev:add-hediff";
            Dev.Gate(V);
            var map = Dev.CurrentMap(V);
            var a = ctx.Args;
            var pawn = Dev.PawnArg(map, a, "pawn");
            var def = Dev.Named<HediffDef>(a.StrReq("def"), "def");
            BodyPartRecord part = a.Has("part") ? Part(pawn, a.Str("part")) : null;

            var hediff = pawn.health.AddHediff(def, part);
            if (hediff == null) throw new VerbArgsException($"AddHediff returned nothing for '{def.defName}'");
            try { hediff.PostDebugAdd(); } catch { }
            if (a.Has("severity")) hediff.Severity = (float)a.NumReq("severity");

            long seq = Dev.Emit(V, "add-hediff",
                PawnSafe.Name(pawn) + " +" + def.defName
                + (part != null ? " (" + part.def.defName + ")" : ""),
                new Dictionary<string, object>
                {
                    ["pawn"] = pawn.thingIDNumber,
                    ["hediff"] = def.defName,
                    ["part"] = part?.def?.defName,
                });

            return new Dictionary<string, object>
            {
                ["pawn"] = Dev.Describe(pawn),
                ["hediff"] = def.defName,
                ["label"] = hediff.LabelCap.ToString(),
                ["part"] = part?.def?.defName,
                ["severity"] = PawnSafe.R(hediff.Severity, 3),
                ["is_bad"] = def.isBad,
                ["downed"] = pawn.Downed,
                ["dead"] = pawn.Dead,
                ["hediff_count"] = pawn.health.hediffSet.hediffs.Count,
                ["dev"] = Dev.Stamp(seq),
            };
        }

        // --------------------------------------------------------------------
        // dev:remove-hediff {pawn, def, part?, all?}
        // Provenance: Verse/DebugTools_Health.Options_RemoveHediff —
        // `pawn.health.RemoveHediff(h)` over a chosen hediff.
        // --------------------------------------------------------------------
        [Verb("dev:remove-hediff")]
        public static object RemoveHediff(VerbContext ctx)
        {
            const string V = "dev:remove-hediff";
            Dev.Gate(V);
            var map = Dev.CurrentMap(V);
            var a = ctx.Args;
            var pawn = Dev.PawnArg(map, a, "pawn");
            var def = Dev.Named<HediffDef>(a.StrReq("def"), "def");
            bool all = a.Bool("all", false);
            string wantPart = a.Str("part");

            var matches = new List<Hediff>();
            foreach (var h in pawn.health.hediffSet.hediffs)
            {
                if (h?.def != def) continue;
                if (wantPart != null && (h.Part?.def?.defName != wantPart)) continue;
                matches.Add(h);
                if (!all) break;
            }
            if (matches.Count == 0)
                throw new VerbArgsException(
                    $"{PawnSafe.Name(pawn)} has no '{def.defName}'"
                    + (wantPart != null ? " on " + wantPart : "")
                    + " (present: " + HediffNames(pawn) + ")");

            var removed = new List<object>();
            foreach (var h in matches)
            {
                removed.Add(Describe(h, "removed"));
                try { pawn.health.RemoveHediff(h); } catch (Exception e) { removed.Add(new Dictionary<string, object> { ["error"] = e.Message }); }
            }

            long seq = Dev.Emit(V, "remove-hediff",
                PawnSafe.Name(pawn) + " -" + def.defName + " x" + matches.Count,
                new Dictionary<string, object> { ["pawn"] = pawn.thingIDNumber, ["hediff"] = def.defName });

            return new Dictionary<string, object>
            {
                ["pawn"] = Dev.Describe(pawn),
                ["hediff"] = def.defName,
                ["removed"] = removed,
                ["hediff_count"] = pawn.health.hediffSet.hediffs.Count,
                ["dev"] = Dev.Stamp(seq),
            };
        }

        private static string HediffNames(Pawn pawn)
        {
            var n = new List<string>();
            var hs = pawn.health.hediffSet.hediffs;
            for (int i = 0; i < hs.Count && n.Count < 20; i++) if (hs[i]?.def != null) n.Add(hs[i].def.defName);
            return n.Count == 0 ? "none" : string.Join(",", n.ToArray());
        }

        // The body part a hediff or a damage hit lands on. "random" picks from
        // the parts that are still attached; a defName matches the first
        // not-missing part of that def, which is what a caller means by "left
        // leg" without wanting to learn BodyPartRecord identity.
        private static BodyPartRecord Part(Pawn pawn, string spec)
        {
            if (string.IsNullOrEmpty(spec) || spec == "none") return null;
            var parts = new List<BodyPartRecord>(pawn.health.hediffSet.GetNotMissingParts());
            if (spec == "random")
            {
                if (parts.Count == 0) return null;
                // Deterministic: a fixture that picks a different part each run
                // is a fixture whose assertions cannot be written.
                parts.Sort((x, y) => string.CompareOrdinal(x.def.defName, y.def.defName));
                return parts[0];
            }
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].def?.defName == spec) return parts[i];
            var names = new List<string>();
            for (int i = 0; i < parts.Count && names.Count < 12; i++)
                if (parts[i].def?.defName != null && !names.Contains(parts[i].def.defName)) names.Add(parts[i].def.defName);
            throw new VerbArgsException(
                $"'{spec}' is not an attached body part on {PawnSafe.Name(pawn)} "
                + "(some of: " + string.Join(",", names.ToArray()) + ")");
        }

        // --------------------------------------------------------------------
        // dev:set-skill {pawn, skill, level, passion?}
        //            or {pawn, skills:{Name:level,…}, passions:{Name:"Major",…}}
        //
        // Provenance: Verse/DebugToolsPawns.SetSkill and SetPassion. The
        // xpSinceLastLevel line is theirs and matters: SkillRecord.Level's
        // setter does not touch XP, so a level set without it sits one XP tick
        // from either levelling or dropping. Half the bar is the DebugAction's
        // answer and it is reproduced verbatim.
        // --------------------------------------------------------------------
        [Verb("dev:set-skill")]
        public static object SetSkill(VerbContext ctx)
        {
            const string V = "dev:set-skill";
            Dev.Gate(V);
            var map = Dev.CurrentMap(V);
            var a = ctx.Args;
            var pawn = Dev.PawnArg(map, a, "pawn");
            if (pawn.skills == null) throw new VerbArgsException($"{PawnSafe.Name(pawn)} has no skills tracker");

            var applied = new List<object>();
            if (a.Has("skills") || a.Has("passions"))
            {
                var levels = MapArg(a, "skills");
                var passions = MapArg(a, "passions");
                foreach (var kv in levels)
                    applied.Add(Apply(pawn, kv.Key, kv.Value as double?, PassionOf(passions, kv.Key)));
                foreach (var kv in passions)
                    if (!levels.ContainsKey(kv.Key))
                        applied.Add(Apply(pawn, kv.Key, null, PassionOf(passions, kv.Key)));
            }
            else
            {
                string skill = a.StrReq("skill");
                double? level = a.Has("level") ? (double?)a.NumReq("level") : null;
                Passion? passion = a.Has("passion") ? ParsePassion(a.Str("passion")) : null;
                if (level == null && passion == null)
                    throw new VerbArgsException("dev:set-skill needs 'level' and/or 'passion'");
                applied.Add(Apply(pawn, skill, level, passion));
            }

            long seq = Dev.Emit(V, "set-skill", PawnSafe.Name(pawn) + " " + applied.Count + " skill(s)",
                new Dictionary<string, object> { ["pawn"] = pawn.thingIDNumber, ["applied"] = applied });

            return new Dictionary<string, object>
            {
                ["pawn"] = Dev.Describe(pawn),
                ["applied"] = applied,
                ["dev"] = Dev.Stamp(seq),
            };
        }

        private static Dictionary<string, object> MapArg(VerbArgs a, string key)
        {
            if (!a.Has(key)) return new Dictionary<string, object>();
            if (!(a.Raw(key) is Dictionary<string, object> d))
                throw new VerbArgsException($"arg '{key}' must be an object, e.g. {{\"Shooting\":8}}");
            return d;
        }

        private static Passion? PassionOf(Dictionary<string, object> passions, string skill)
        {
            if (!passions.TryGetValue(skill, out var v)) return null;
            if (v is string s) return ParsePassion(s);
            throw new VerbArgsException($"passions['{skill}'] must be None|Minor|Major");
        }

        private static Passion ParsePassion(string s)
        {
            if (Enum.TryParse(s, ignoreCase: true, out Passion p)) return p;
            throw new VerbArgsException("passion must be None|Minor|Major");
        }

        private static Dictionary<string, object> Apply(Pawn pawn, string skillName, double? level, Passion? passion)
        {
            var def = Dev.Named<SkillDef>(skillName, "skill");
            var rec = pawn.skills.GetSkill(def);
            if (rec == null) throw new VerbArgsException($"{PawnSafe.Name(pawn)} has no '{def.defName}' skill record");
            int before = rec.Level;
            var passionBefore = rec.passion;

            if (level.HasValue)
            {
                int lvl = (int)level.Value;
                if (lvl < 0 || lvl > 20) throw new VerbArgsException($"level for '{def.defName}' must be 0..20");
                rec.Level = lvl;
                // DebugToolsPawns.SetSkill's own line: park the XP bar halfway
                // so the level is stable rather than one tick from moving.
                rec.xpSinceLastLevel = rec.XpRequiredForLevelUp / 2f;
            }
            if (passion.HasValue) rec.passion = passion.Value;

            return new Dictionary<string, object>
            {
                ["skill"] = def.defName,
                ["level_before"] = before,
                ["level_after"] = rec.Level,
                ["passion_before"] = passionBefore.ToString(),
                ["passion_after"] = rec.passion.ToString(),
                // The gate a caller actually cares about: a disabled skill's
                // level is meaningless, and setting it silently changes nothing
                // the pawn can do.
                ["disabled"] = rec.TotallyDisabled,
            };
        }

        // --------------------------------------------------------------------
        // dev:mental-state {pawn, state?, stop?, force?, reason?}
        // Provenance: Verse/DebugToolsPawns.MentalState (TryStartMentalState
        // with forced:true) and StopMentalState
        // (mindState.mentalStateHandler.CurState.RecoverFromState).
        //
        // NOT IN THE SPEC BODY — proposed and resolved on git-bug f166fb9. It
        // supersedes journal-selftest's `break` step, and the journal's
        // `mental_break` event type has no other deterministic stimulus.
        // --------------------------------------------------------------------
        [Verb("dev:mental-state")]
        public static object MentalState(VerbContext ctx)
        {
            const string V = "dev:mental-state";
            Dev.Gate(V);
            var map = Dev.CurrentMap(V);
            var a = ctx.Args;
            var pawn = Dev.PawnArg(map, a, "pawn");
            var handler = pawn.mindState?.mentalStateHandler;
            if (handler == null) throw new VerbArgsException($"{PawnSafe.Name(pawn)} has no mental state handler");

            string wasIn = handler.CurStateDef?.defName;
            bool started = false, stopped = false;
            string target;

            if (a.Bool("stop", false))
            {
                if (handler.CurState != null) { handler.CurState.RecoverFromState(); stopped = true; }
                target = PawnSafe.Name(pawn) + " stop " + (wasIn ?? "(none)");
            }
            else
            {
                var def = Dev.Named<MentalStateDef>(a.StrReq("state"), "state");
                started = handler.TryStartMentalState(def, a.Str("reason", "AutoRimmer dev fixture"),
                    forced: a.Bool("force", true), forceWake: true);
                target = PawnSafe.Name(pawn) + " " + def.defName + (started ? "" : " (refused)");
            }

            long seq = Dev.Emit(V, "mental-state", target,
                new Dictionary<string, object> { ["pawn"] = pawn.thingIDNumber, ["was"] = wasIn });

            return new Dictionary<string, object>
            {
                ["pawn"] = Dev.Describe(pawn),
                ["was"] = wasIn,
                ["now"] = handler.CurStateDef?.defName,
                ["started"] = started,
                ["stopped"] = stopped,
                ["dev"] = Dev.Stamp(seq),
                ["note"] = started || stopped ? null
                    : "TryStartMentalState refused even forced — the state's worker can still veto "
                        + "(wrong species, already in a state, or a required target missing)",
            };
        }

        // --------------------------------------------------------------------
        // dev:guest-status {pawn, status}   status = prisoner|guest|slave|none
        // Provenance: Verse/DebugToolsPawns.AddGuest(GuestStatus) —
        // `pawn.guest.SetGuestStatus(Faction.OfPlayer, guestStatus)`, which is
        // the same call JobDriver_TakeToBed's capture toil makes. It rolls
        // resistance/will from the kind's ranges, re-registers with MapPawns and
        // runs AddAndRemoveDynamicComponents, so this produces a REAL prisoner
        // rather than a flag we set (pawn-fixture's `prisoner` step made the
        // same argument and this supersedes it).
        //
        // NOT IN THE SPEC BODY — proposed and resolved on git-bug f166fb9.
        // 3.4 owns the player-facing warden verbs; this is the fixture route.
        // --------------------------------------------------------------------
        [Verb("dev:guest-status")]
        public static object GuestStatus(VerbContext ctx)
        {
            const string V = "dev:guest-status";
            Dev.Gate(V);
            var map = Dev.CurrentMap(V);
            var a = ctx.Args;
            var pawn = Dev.PawnArg(map, a, "pawn");
            if (pawn.guest == null)
                throw new VerbArgsException($"{PawnSafe.Name(pawn)} has no guest tracker (not a humanlike guest-capable pawn)");

            string want = a.StrReq("status").ToLowerInvariant();
            GuestStatus status;
            switch (want)
            {
                case "prisoner": status = RimWorld.GuestStatus.Prisoner; break;
                case "guest": status = RimWorld.GuestStatus.Guest; break;
                case "slave": status = RimWorld.GuestStatus.Slave; break;
                case "none": status = RimWorld.GuestStatus.Guest; break;
                default: throw new VerbArgsException("status must be prisoner|guest|slave|none");
            }

            // THE TWO GATES SetGuestStatus'S GUEST BRANCH ANSWERS WITH Log.Error
            // (Pawn_GuestTracker.SetGuestStatus, case GuestStatus.Guest), and a
            // red error in the journal breaks the standing zero-red-errors
            // invariant. Reproduced here as clean refusals so a caller mistake
            // is a bad-args result rather than a permanent stain on the run.
            if (want == "guest")
            {
                if (pawn.Faction.HostileTo(Faction.OfPlayer))
                    throw new VerbArgsException(
                        $"{PawnSafe.Name(pawn)}'s faction ({pawn.Faction?.Name}) is hostile to the colony; "
                        + "the game refuses guest status for a hostile pawn (use prisoner, or "
                        + "dev:faction-goodwill first)");
                if (pawn.Faction == Faction.OfPlayer)
                    throw new VerbArgsException(
                        $"{PawnSafe.Name(pawn)} is already in the player faction; a pawn cannot be a guest of its own faction");
            }

            string before = pawn.guest.GuestStatus.ToString();
            if (want == "none")
            {
                // SetGuestStatus has no "clear" value: a null host with the
                // default (Guest) status is how the game represents an ordinary
                // pawn — hostFactionInt goes null and IsPrisoner goes false.
                pawn.guest.SetGuestStatus(null, status);
            }
            else
            {
                pawn.guest.SetGuestStatus(Faction.OfPlayer, status);
            }

            long seq = Dev.Emit(V, "guest-status",
                PawnSafe.Name(pawn) + " " + before + " -> " + pawn.guest.GuestStatus,
                new Dictionary<string, object> { ["pawn"] = pawn.thingIDNumber, ["status"] = want });

            return new Dictionary<string, object>
            {
                ["pawn"] = Dev.Describe(pawn),
                ["status_before"] = before,
                ["status_after"] = pawn.guest.GuestStatus.ToString(),
                ["host_faction"] = pawn.guest.HostFaction?.Name,
                ["class"] = PawnSafe.Classify(pawn),
                ["resistance"] = PawnSafe.R(pawn.guest.resistance, 2),
                ["will"] = PawnSafe.R(pawn.guest.will, 2),
                ["dev"] = Dev.Stamp(seq),
                ["note"] = "no bed is claimed — a prisoner without a prisoner bed will try to leave. "
                    + "Building the cell is 3.3's job; dev:spawn-thing can place a bed and 3.2 zones it.",
            };
        }
    }
}
