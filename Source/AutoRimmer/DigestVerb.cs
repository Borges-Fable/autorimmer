using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoRimmer
{
    // The glance (spec 2.1): one main-thread verb, the player's peripheral
    // vision sized for LLM context. Read-only throughout — precomputed game
    // state (the alert readout, resource counter, danger watcher, the needs
    // tab's own change arrows), never recomputation, never lazy-init getters.
    //
    // This file is the serializer EXEMPLAR: 2.2/2.4 follow its shape — one
    // section per builder method, plain dictionaries, stable snake_case field
    // names (rwtest consumes them forever), explicit truncation rules where a
    // list could grow (alerts cap +N more, job strings clipped).
    //
    // Field-doc notes for consumers: `alerts` is verbatim from AlertsReadout
    // and therefore TRAILS the causing tick (readout recomputes on UI cadence;
    // late by design per DESIGN, plus a frame under advance) — assert windows,
    // not exact ticks. `food_days` is the vanilla Alert_LowFood division
    // (human-edible nutrition / (colonists + prisoners)), not a consumption
    // simulation. Single-map by design (v1): everything reads Find.CurrentMap.
    public static class DigestVerb
    {
        private const int AlertCap = 12;
        private const int JobClip = 48;
        private const int KindCap = 3;

        [Verb("digest")]
        public static object Digest(VerbContext ctx)
        {
            var map = Find.CurrentMap;
            if (map == null)
                throw new VerbArgsException("no current map");
            long since = ctx.Args.Int("since", 0);
            return new Dictionary<string, object>
            {
                ["time"] = TimeSection(map),
                ["alerts"] = AlertSection(),
                ["colonists"] = ColonistSection(map),
                ["resources"] = ResourceSection(map),
                ["power"] = PowerSection(map),
                ["threats"] = ThreatSection(map),
                ["changed"] = ChangedSection(since),
            };
        }

        private static Dictionary<string, object> TimeSection(Map map)
        {
            var tm = Find.TickManager;
            return new Dictionary<string, object>
            {
                ["tick"] = tm.TicksGame,
                ["paused"] = tm.Paused,
                ["speed"] = tm.CurTimeSpeed.ToString(),
                ["day_of_season"] = GenLocalDate.DayOfSeason(map) + 1,
                ["season"] = GenLocalDate.Season(map).ToString(),
                ["year"] = GenLocalDate.Year(map),
                ["hour"] = GenLocalDate.HourOfDay(map),
                ["weather"] = map.weatherManager.curWeather?.label,
                ["outdoor_c"] = Mathf.Round(map.mapTemperature.OutdoorTemp),
            };
        }

        private static Dictionary<string, object> AlertSection()
        {
            var live = AlertScanner.Snapshot();
            var list = new List<object>();
            for (int i = 0; i < live.Count && i < AlertCap; i++)
            {
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = live[i][0],
                    ["label"] = live[i][1],
                    ["priority"] = live[i][2],
                });
            }
            return new Dictionary<string, object>
            {
                ["active"] = list,
                ["more"] = live.Count > AlertCap ? live.Count - AlertCap : 0,
            };
        }

        private static List<object> ColonistSection(Map map)
        {
            var list = new List<object>();
            // SNAPSHOT before iterating — 2.2/2.4, copy this. MapPawns'
            // FreeColonistsSpawned (via FreeHumanlikesSpawnedOfFaction) CLEARS
            // and rebuilds the same cached List instance on EVERY access, so
            // any getter below that re-enters it mid-loop invalidates the
            // enumerator (found live: first digest on a stirred colony threw
            // Collection-was-modified). The lazy-getter hazard class of
            // _mp/DETERMINISM.md, in read-only clothing.
            var colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
            foreach (var pawn in colonists)
            {
                var flags = new List<object>();
                var health = pawn.health;
                if (pawn.Downed) flags.Add("downed");
                if (pawn.MentalStateDef != null) flags.Add("mental:" + pawn.MentalStateDef.defName);
                if (health.hediffSet.BleedRateTotal > 0.01f) flags.Add("bleeding");
                if (HealthAIUtility.ShouldBeTendedNowByPlayer(pawn)) flags.Add("tend");
                if (health.hediffSet.AnyHediffMakesSickThought) flags.Add("sick");

                string room = null;
                var r = pawn.GetRoom();
                if (r != null) room = r.PsychologicallyOutdoors ? "outside" : r.Role?.label;

                string job = null;
                try { job = pawn.jobs?.curDriver?.GetReport(); }
                catch { }

                var mood = pawn.needs?.mood;
                var line = new Dictionary<string, object>
                {
                    ["name"] = pawn.LabelShortCap.ToString(),
                    ["job"] = Journal.Truncate(job, JobClip),
                    ["mood_pct"] = mood != null ? (object)Mathf.Round(mood.CurLevelPercentage * 100f) : null,
                    // The needs tab's own trajectory arrow: +1 rising toward the
                    // instant level, -1 falling, 0 steady. No bespoke window.
                    ["mood_arrow"] = mood != null ? (object)mood.GUIChangeArrow : null,
                    ["drafted"] = pawn.Drafted,
                    ["room"] = room,
                };
                if (flags.Count > 0) line["flags"] = flags;
                list.Add(line);
            }
            return list;
        }

        private static Dictionary<string, object> ResourceSection(Map map)
        {
            var rc = map.resourceCounter;
            float nutrition = rc.TotalHumanEdibleNutrition;
            int needers = map.mapPawns.FreeColonistsSpawnedCount + map.mapPawns.PrisonersOfColonyCount;
            return new Dictionary<string, object>
            {
                // Vanilla Alert_LowFood math, unfloored; needers published so
                // the division is checkable from the digest alone.
                ["food_days"] = needers > 0 ? System.Math.Round(nutrition / needers, 1) : -1,
                ["food_needers"] = needers,
                ["food_nutrition"] = Mathf.Round(nutrition),
                ["meds"] = rc.GetCountIn(ThingCategoryDefOf.Medicine),
                ["steel"] = rc.GetCount(ThingDefOf.Steel),
                ["wood"] = rc.GetCount(ThingDefOf.WoodLog),
                ["silver"] = rc.GetCount(ThingDefOf.Silver),
                ["components"] = rc.GetCount(ThingDefOf.ComponentIndustrial),
            };
        }

        private static Dictionary<string, object> PowerSection(Map map)
        {
            float gainWd = 0f;
            float storedWd = 0f;
            int nets = 0;
            var netList = map.powerNetManager.AllNetsListForReading;
            for (int i = 0; i < netList.Count; i++)
            {
                var net = netList[i];
                if (net.hasPowerSource) nets++;
                gainWd += net.CurrentEnergyGainRate();
                storedWd += net.CurrentStoredEnergy();
            }
            return new Dictionary<string, object>
            {
                // Net balance across all grids in watts (gain rate is Wd/tick;
                // x60000 ticks/day converts back to the W the inspect pane shows).
                ["gain_w"] = Mathf.Round(gainWd * 60000f),
                ["stored_wd"] = Mathf.Round(storedWd),
                ["nets"] = nets,
            };
        }

        private static Dictionary<string, object> ThreatSection(Map map)
        {
            int hostiles = 0;
            var kinds = new Dictionary<string, int>();
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                var p = pawns[i];
                if (p.Downed || p.Dead || !p.HostileTo(Faction.OfPlayer)) continue;
                hostiles++;
                string kind = p.kindDef?.label ?? p.def.label;
                kinds[kind] = kinds.TryGetValue(kind, out var c) ? c + 1 : 1;
            }
            var top = new List<object>();
            foreach (var kv in kinds)
            {
                if (top.Count >= KindCap) { top.Add("+" + (kinds.Count - KindCap) + " kinds"); break; }
                top.Add(kv.Key + " x" + kv.Value);
            }
            return new Dictionary<string, object>
            {
                ["danger"] = map.dangerWatcher.DangerRating.ToString(),
                ["hostiles"] = hostiles,
                ["kinds"] = top,
            };
        }

        private static Dictionary<string, object> ChangedSection(long since)
        {
            var counts = Journal.CountsSince(since, out long lastSeq, out bool truncated);
            var byType = new Dictionary<string, object>();
            foreach (var kv in counts) byType[kv.Key] = kv.Value;
            var changed = new Dictionary<string, object>
            {
                ["since"] = since,
                ["last_seq"] = lastSeq,
                ["counts"] = byType,
            };
            if (truncated) changed["truncated"] = true;
            return changed;
        }
    }
}
