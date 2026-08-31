using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoRimmer
{
    // Deliberate stimulus for spec 2.2's acceptance, in its OWN verb rather
    // than as steps on `journal-selftest` — that file belongs to another
    // worker this wave, and the acceptance needs game state no shipped verb can
    // create.
    //
    // WHY IT EXISTS. 2.2's acceptance is "one wounded, sad, badly-dressed pawn
    // … prisoner + visitor render correctly too", and on this bench:
    //   * `journal-selftest downed` produces a DOWNED pawn, not a wounded and
    //     standing one, and downed pawns hide half the surface under test.
    //   * NO capture verb exists until 3.4 and no dev-spawn until 3.1, so a
    //     prisoner cannot otherwise be produced at all.
    //   * a visitor arrives naturally (Hospitality) or from a VisitorGroup
    //     incident, which nothing exposes.
    // Superseded by 3.1's dev layer, exactly as `journal-selftest` is.
    //
    // THIS VERB MUTATES GAME STATE BY DESIGN. It is gated on Prefs.DevMode, it
    // journals a `dev` event per step (the provenance rule 3.1 inherits), and
    // it is disclosed on git-bug 69ae91f as an undeclared addition — the same
    // duty 2.1's `stockpile` step and 2.6's `alerts`/`colonists`/`power` steps
    // discharged. It is NOT an observer and nothing in it belongs on a hot path.
    public static class PawnFixtureVerbs
    {
        [Verb("pawn-fixture")]
        public static object Fixture(VerbContext ctx)
        {
            if (!Prefs.DevMode)
                throw new VerbArgsException("pawn-fixture requires devMode=True (it mutates game state)");
            var map = Find.CurrentMap ?? throw new VerbArgsException("pawn-fixture needs a current map");

            var steps = ctx.Args.StrList("steps");
            if (steps.Count == 0) steps = new List<string> { "wound", "sadden", "tatter" };

            var executed = new List<object>();
            var extras = new Dictionary<string, object>();

            foreach (var step in steps)
            {
                string target;
                switch (step)
                {
                    case "wound": target = Wound(map, ctx, extras); break;
                    case "sadden": target = Sadden(map, ctx, extras); break;
                    case "tatter": target = Tatter(map, ctx, extras); break;
                    case "prisoner": target = Prisoner(map, ctx, extras); break;
                    case "visitor": target = Visitor(map, extras); break;
                    default:
                        throw new VerbArgsException(
                            $"unknown step '{step}' (wound|sadden|tatter|prisoner|visitor)");
                }
                Journal.Emit("dev", new Dictionary<string, object>
                {
                    ["verb"] = "pawn-fixture",
                    ["step"] = step,
                    ["target"] = target,
                }, Find.TickManager.TicksGame);
                executed.Add(step);
            }

            var data = new Dictionary<string, object> { ["executed"] = executed };
            foreach (var kv in extras) data[kv.Key] = kv.Value;
            return data;
        }

        // WOUNDED AND STANDING — the state `journal-selftest downed` cannot
        // produce. Bounded TakeDamage that stops the instant the pawn goes down,
        // so the fixture is a wound rather than a casualty. The chosen pawn and
        // its resulting hediff list come back as the independent hand-check the
        // serializer's `health.hediffs` must agree with.
        private static string Wound(Map map, VerbContext ctx, Dictionary<string, object> extras)
        {
            var pawn = Pick(map, ctx);
            float amount = (float)ctx.Args.Num("wound_amount", 6);
            int hits = ctx.Args.Int("wound_hits", 3);
            if (hits < 1 || hits > 12) throw new VerbArgsException("wound_hits must be 1..12");
            int landed = 0;
            for (int i = 0; i < hits && !pawn.Downed && !pawn.Dead; i++)
            {
                pawn.TakeDamage(new DamageInfo(DamageDefOf.Cut, amount));
                landed++;
            }
            var wounds = new List<object>();
            var hs = pawn.health.hediffSet.hediffs;
            for (int i = 0; i < hs.Count; i++)
            {
                if (!(hs[i] is Hediff_Injury)) continue;
                wounds.Add(new Dictionary<string, object>
                {
                    ["def"] = hs[i].def.defName,
                    ["part"] = hs[i].Part?.def?.defName,
                    ["severity"] = Math.Round(hs[i].Severity, 2),
                    ["bleeding"] = Math.Round(hs[i].BleedRate, 3),
                });
            }
            extras["wound"] = new Dictionary<string, object>
            {
                ["id"] = pawn.thingIDNumber,
                ["name"] = PawnSafe.Name(pawn),
                ["hits"] = landed,
                ["downed"] = pawn.Downed,
                // Counted from the hediff set directly, not from the serializer:
                // two readers, one truth (the `stockpile` step's discipline).
                ["injuries"] = wounds,
                ["bleed_rate"] = Math.Round(pawn.health.hediffSet.BleedRateTotal, 2),
                ["summary_pct"] = Mathf.RoundToInt(pawn.health.summaryHealth.SummaryHealthPercent * 100f),
            };
            return PawnSafe.Name(pawn) + " x" + landed;
        }

        // SAD — real memory thoughts, so `mood.thoughts` has genuine negative
        // groups with real offsets rather than a hand-set mood float. Stacking a
        // repeatable thought is what makes the GROUP offset differ from a single
        // thought's baseMoodEffect, which is the thing the serializer must get
        // right (it publishes MoodOffsetOfGroup, not baseMoodEffect x count).
        private static string Sadden(Map map, VerbContext ctx, Dictionary<string, object> extras)
        {
            var pawn = Pick(map, ctx);
            var mood = pawn.needs?.mood;
            if (mood == null) throw new VerbArgsException("sadden needs a pawn with a mood need");
            int stacks = ctx.Args.Int("sad_stacks", 4);
            if (stacks < 1 || stacks > 20) throw new VerbArgsException("sad_stacks must be 1..20");
            int gained = 0;
            for (int i = 0; i < stacks; i++)
            {
                try { mood.thoughts.memories.TryGainMemory(ThoughtDefOf.DebugBad); gained++; }
                catch { break; }
            }
            try { mood.thoughts.memories.TryGainMemory(ThoughtDefOf.DebugGood); } catch { }

            // The group offsets, computed here from the game's own maths, as the
            // independent check for `mood.thoughts[].offset`.
            var groups = new List<Thought>();
            var lines = new List<object>();
            try
            {
                mood.thoughts.GetDistinctMoodThoughtGroups(groups);
                foreach (var g in groups)
                {
                    if (!g.VisibleInNeedsTab) continue;
                    lines.Add(new Dictionary<string, object>
                    {
                        ["def"] = g.def?.defName,
                        ["offset"] = Math.Round(mood.thoughts.MoodOffsetOfGroup(g), 1),
                    });
                }
            }
            catch { }
            extras["sadden"] = new Dictionary<string, object>
            {
                ["id"] = pawn.thingIDNumber,
                ["name"] = PawnSafe.Name(pawn),
                ["bad_memories_added"] = gained,
                ["mood_pct"] = Mathf.RoundToInt(mood.CurLevelPercentage * 100f),
                ["instant_pct"] = Mathf.RoundToInt(mood.CurInstantLevelPercentage * 100f),
                ["expect_groups"] = lines,
            };
            return PawnSafe.Name(pawn) + " +" + gained;
        }

        // BADLY DRESSED — apparel driven under the game's own tattered
        // threshold. HitPoints is set directly rather than via TakeDamage so the
        // result is exact and the apparel is never destroyed mid-fixture; the
        // field is a plain int on Thing.
        private static string Tatter(Map map, VerbContext ctx, Dictionary<string, object> extras)
        {
            var pawn = Pick(map, ctx);
            var worn = pawn.apparel?.WornApparel;
            if (worn == null || worn.Count == 0)
                throw new VerbArgsException("tatter needs a pawn wearing something");
            float frac = (float)ctx.Args.Num("tatter_frac", 0.15);
            if (frac <= 0f || frac >= 1f) throw new VerbArgsException("tatter_frac must be 0<f<1");
            var touched = new List<object>();
            for (int i = 0; i < worn.Count; i++)
            {
                var a = worn[i];
                if (a?.def == null || !a.def.useHitPoints) continue;
                int max = a.MaxHitPoints;
                if (max <= 1) continue;
                a.HitPoints = Mathf.Clamp(Mathf.RoundToInt(max * frac), 1, max);
                touched.Add(new Dictionary<string, object>
                {
                    ["id"] = a.thingIDNumber,
                    ["def"] = a.def.defName,
                    ["hp"] = a.HitPoints,
                    ["max_hp"] = max,
                    // ThoughtWorker_ApparelDamaged's own thresholds: the label
                    // the serializer must independently arrive at.
                    ["expect_wear"] = (float)a.HitPoints / max < 0.2f ? "tattered"
                        : ((float)a.HitPoints / max < 0.5f ? "frayed" : "good"),
                });
            }
            extras["tatter"] = new Dictionary<string, object>
            {
                ["id"] = pawn.thingIDNumber,
                ["name"] = PawnSafe.Name(pawn),
                ["apparel"] = touched,
            };
            return PawnSafe.Name(pawn) + " " + touched.Count + " items";
        }

        // PRISONER — the acceptance line that is otherwise not demonstrable
        // before 3.4. Generates a pawn of a non-hostile outlander faction (a
        // hostile one would be refused by SetGuestStatus's Guest branch and
        // would fight), spawns it near the colony, then hands it to the game's
        // OWN capture path: Pawn_GuestTracker.SetGuestStatus, which is what
        // JobDriver_TakeToBed's capture toil calls. It rolls resistance and
        // will from the kind's ranges, re-registers the pawn with MapPawns and
        // runs AddAndRemoveDynamicComponents, so the result is a real prisoner
        // rather than a flag we set.
        //
        // NOT MODELLED: a prisoner bed / cell. The serializer does not read
        // ownership, and building a cell is 3.3's job.
        private static string Prisoner(Map map, VerbContext ctx, Dictionary<string, object> extras)
        {
            var anchor = AnchorCell(map);
            var faction = Faction.OfPlayer;
            var host = FindNonHostileFaction();
            var kind = PawnKindDefOf.Villager;
            var pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(kind, host,
                PawnGenerationContext.NonPlayer, -1, forceGenerateNewPawn: true));
            GenSpawn.Spawn(pawn, CellFinder.RandomClosewalkCellNear(anchor, map, 10), map);
            if (pawn.guest == null)
                throw new VerbArgsException("generated pawn has no guest tracker; cannot imprison");
            pawn.guest.SetGuestStatus(faction, GuestStatus.Prisoner);

            extras["prisoner"] = new Dictionary<string, object>
            {
                ["id"] = pawn.thingIDNumber,
                ["name"] = PawnSafe.Name(pawn),
                ["kind"] = kind.defName,
                ["origin_faction"] = host?.def?.defName,
                ["at"] = Positions.Out(pawn.Position),
                // What `pawn <id>` must independently report: class "prisoner",
                // guest.status "Prisoner", host_faction PlayerColony, and
                // resistance/will as real numbers rather than the -1 sentinel.
                ["expect_class"] = PawnSafe.ClassPrisoner,
                ["expect_resistance"] = Math.Round(pawn.guest.resistance, 2),
                ["expect_will"] = Math.Round(pawn.guest.will, 2),
            };
            return PawnSafe.Name(pawn) + " (prisoner)";
        }

        // VISITOR — the vanilla incident, forced. Visitors also arrive on their
        // own via Hospitality on this bench; this exists so the acceptance does
        // not have to wait for one.
        private static string Visitor(Map map, Dictionary<string, object> extras)
        {
            // The incident's OWN category, not a guessed constant:
            // IncidentCategoryDefOf has no arrival member, and DefaultParmsNow
            // keys population/wealth scaling off the category it is handed.
            var parms = StorytellerUtility.DefaultParmsNow(IncidentDefOf.VisitorGroup.category, map);
            parms.forced = true;
            bool fired = false;
            try { fired = IncidentDefOf.VisitorGroup.Worker.TryExecute(parms); } catch { }
            extras["visitor"] = new Dictionary<string, object>
            {
                ["fired"] = fired,
                // A refusal is normal — the incident needs a non-hostile faction
                // able to reach the map. Say so rather than looking broken.
                ["note"] = fired
                    ? "VisitorGroup fired; the group walks in over the next few hundred ticks"
                    : "VisitorGroup refused (no eligible faction / no arrival spot) - advance and retry",
            };
            return fired ? "fired" : "refused";
        }

        // The pawn the wound/sadden/tatter steps act on: `pawn_id` if given,
        // else the first standing free colonist in id order — deterministic, so
        // a rerun hits the same pawn.
        private static Pawn Pick(Map map, VerbContext ctx)
        {
            // Snapshot: FreeColonistsSpawned clears and rebuilds on every access.
            var colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
            colonists.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
            if (ctx.Args.Has("pawn_id"))
            {
                int id = ctx.Args.IntReq("pawn_id");
                for (int i = 0; i < colonists.Count; i++)
                    if (colonists[i].thingIDNumber == id) return colonists[i];
                throw new VerbArgsException($"no spawned free colonist with id {id}");
            }
            for (int i = 0; i < colonists.Count; i++)
                if (!colonists[i].Downed && !colonists[i].Dead) return colonists[i];
            throw new VerbArgsException("no standing free colonist to use as a fixture subject");
        }

        private static IntVec3 AnchorCell(Map map)
        {
            var colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
            return colonists.Count > 0 ? colonists[0].Position : map.Center;
        }

        private static Faction FindNonHostileFaction()
        {
            var player = Faction.OfPlayer;
            foreach (var f in Find.FactionManager.AllFactionsListForReading)
            {
                if (f == null || f == player || f.IsPlayer || f.Hidden) continue;
                if (f.def == null || !f.def.humanlikeFaction) continue;
                if (f.HostileTo(player)) continue;
                return f;
            }
            // Better a hostile origin faction than no prisoner at all: the
            // capture path itself does not care, only the Guest branch does.
            foreach (var f in Find.FactionManager.AllFactionsListForReading)
                if (f != null && !f.IsPlayer && !f.Hidden && f.def != null && f.def.humanlikeFaction) return f;
            throw new VerbArgsException("no humanlike faction available to source a prisoner from");
        }
    }
}
