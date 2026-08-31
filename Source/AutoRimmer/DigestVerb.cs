using System;
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
    // list could grow.
    //
    // ===================== TRUNCATION IS A CONTRACT =========================
    // Every list here is capped AND every cap orders by IMPORTANCE before it
    // cuts. That second half is the 2.6 lesson and it cost two real defects:
    // alerts were cut in the readout's discovery order, so a Critical that went
    // active last was the one dropped while twelve Mediums stayed; and
    // `colonists` had no cap at all, the only section without one. Ordering by
    // arrival and calling it truncation loses the headline silently. If you add
    // a section to this file or copy it into 2.2/2.4: cap it, sort it by what
    // the reader would miss most, and publish the count you dropped.
    //
    // ===================== MUTATION / COST HAZARDS ==========================
    // Read-only is not the same as free, and some read-only getters are worse
    // than slow — they rebuild shared state under you. Known hazards in this
    // file; check any getter you add against the decompiled source the same way
    // (rimworld-tools/Info/decompiled/RimWorldBase).
    //
    //  * MapPawns.FreeColonistsSpawned CLEARS and rebuilds the same cached List
    //    instance on EVERY access (via FreeHumanlikesSpawnedOfFaction). SNAPSHOT
    //    it before iterating — see ColonistSection. Found live: the first digest
    //    on a stirred colony threw Collection-was-modified.
    //  * DangerWatcher.DangerRating (ThreatSection) recomputes every 101 ticks
    //    and its CalculateDangerRating calls map.mapPawns.FreeColonistsSpawned
    //    INTERNALLY (decompiled RimWorld/DangerWatcher.cs). Calling it from
    //    inside a FreeColonistsSpawned foreach is therefore the same live bug as
    //    above, one indirection further away. It is called here outside every
    //    pawn loop, deliberately. AllPawnsSpawned is NOT in this class — it
    //    returns the real `pawnsSpawned` list and is safe to iterate.
    //  * Room.Role (ColonistSection, and SpatialVerbs.RoomAt) runs
    //    UpdateRoomStatsAndRole() whenever statsAndRoleDirty — a full room
    //    analysis: every RoomStatDef and RoomRoleDef worker over the room's
    //    cells and contents. statsAndRoleDirty is set from six sites in
    //    Verse/Room.cs, so ordinary hauling in and out of a room makes this
    //    recompute constantly, and this verb is documented as called constantly.
    //    Idempotent and RNG-free, so it is not a correctness defect and it stays
    //    (the room a colonist is in is worth its price at one call per colonist)
    //    — but it is the most expensive line in the file by a wide margin, and
    //    the colonist cap now bounds how many times it runs. Do not copy it into
    //    a per-tick path.
    //
    // ========================= FIELD DOCS ==================================
    // `alerts.active` is verbatim from AlertsReadout and therefore TRAILS the
    // causing tick (readout recomputes on UI cadence; late by design per DESIGN,
    // plus a frame under advance) — assert windows, not exact ticks. It is
    // sorted by priority descending here; the readout's own list is unsorted.
    //
    // `food_days` is the vanilla Alert_LowFood division (human-edible nutrition
    // / (colonists + prisoners)), not a consumption simulation.
    //
    // RESOURCE COUNTS ARE STOCKPILE-ONLY. `steel`, `wood`, `silver`,
    // `components`, `meds` and `food_nutrition` all come from
    // map.resourceCounter, which walks SlotGroup haul destinations — so goods
    // lying in unzoned ground read as ZERO. A colony with 5000 steel scattered
    // across the crash site reports steel:0. Any wave-3 build verb that gates on
    // a resource count must say "in stockpiles" or count things itself, or it
    // will refuse a build the player could make.
    //
    // Single-map by design (v1): everything reads Find.CurrentMap.
    public static class DigestVerb
    {
        private const int AlertCap = 12;
        private const int JobClip = 48;
        private const int KindCap = 3;
        // ~130 bytes a line. 10 keeps a 20-colonist colony's digest inside the
        // ~1-2KB budget DESIGN sets; `colonists_cap` overrides per call for a
        // deliberate full read.
        private const int ColonistCap = 10;

        [Verb("digest")]
        public static object Digest(VerbContext ctx)
        {
            var map = Find.CurrentMap;
            if (map == null)
                throw new VerbArgsException("no current map");
            // long, not int: journal seq is long and `since` is compared
            // against it (VerbRegistry.VerbArgs.Int would silently narrow).
            long since = ctx.Args.Long("since", 0);
            int colonistCap = ctx.Args.Int("colonists_cap", ColonistCap);
            if (colonistCap < 1 || colonistCap > 200)
                throw new VerbArgsException("colonists_cap must be 1..200");
            return new Dictionary<string, object>
            {
                ["time"] = TimeSection(map),
                ["alerts"] = AlertSection(),
                ["colonists"] = ColonistSection(map, colonistCap),
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

        // 2.6 blocker 2. AlertsReadout.activeAlerts is APPEND-ORDERED and never
        // sorted (decompiled AlertsReadout.cs:262; the priority grouping in
        // AlertsReadoutOnGUI happens at draw time and does not touch the list),
        // so 2.1's `live[0..11]` dropped by when an alert went active. A
        // Critical that fired most recently landed last and was the one cut —
        // the digest could show twelve Mediums and `more:3` while hiding
        // Alert_ColonistNeedsRescue. The attention model losing its headline is
        // the worst thing this verb can do. The 10-alert acceptance fixture in
        // 2.1 was under the cap, so it never exercised this.
        //
        // Sort: priority descending, discovery order preserved within a priority
        // (List.Sort is unstable, so the readout index is the explicit
        // tie-break) — the truncation is then deterministic run to run.
        private static Dictionary<string, object> AlertSection()
        {
            var live = AlertScanner.Snapshot();
            live.Sort((a, b) =>
            {
                int c = ((int)b.Priority).CompareTo((int)a.Priority);
                return c != 0 ? c : a.Order.CompareTo(b.Order);
            });
            var list = new List<object>();
            var droppedByPriority = new Dictionary<string, object>();
            for (int i = 0; i < live.Count; i++)
            {
                if (i < AlertCap)
                {
                    list.Add(new Dictionary<string, object>
                    {
                        ["id"] = live[i].Id,
                        ["label"] = live[i].Label,
                        ["priority"] = live[i].Priority.ToString(),
                    });
                    continue;
                }
                string key = live[i].Priority.ToString();
                droppedByPriority[key] = droppedByPriority.TryGetValue(key, out var n) ? (int)n + 1 : 1;
            }
            return new Dictionary<string, object>
            {
                ["active"] = list,
                ["total"] = live.Count,
                ["more"] = live.Count > AlertCap ? live.Count - AlertCap : 0,
                // What the cap cost, by severity — so "more:3" is never a
                // question about whether something important was hidden.
                ["more_by_priority"] = droppedByPriority,
            };
        }

        // 2.6 blocker 3: this was the only section without a cap, on a verb
        // documented as called constantly. 20 colonists is ~2.6KB of colonist
        // lines alone, which is outside the whole digest's ~1-2KB budget on its
        // own — 2.1's Caveat 1 framed that as unmeasured when its own
        // extrapolated ~2.1KB already breached it.
        //
        // Capped AND ordered by attention, for the same reason alerts are: a cap
        // that cuts in MapPawns order would hide the downed colonist behind ten
        // healthy ones. Order is deterministic (score, then name) so rwtest can
        // assert on it.
        private static Dictionary<string, object> ColonistSection(Map map, int cap)
        {
            // SNAPSHOT before iterating — 2.2/2.4, copy this. MapPawns'
            // FreeColonistsSpawned (via FreeHumanlikesSpawnedOfFaction) CLEARS
            // and rebuilds the same cached List instance on EVERY access, so
            // any getter below that re-enters it mid-loop invalidates the
            // enumerator (found live: first digest on a stirred colony threw
            // Collection-was-modified). The lazy-getter hazard class of
            // _mp/DETERMINISM.md, in read-only clothing. DangerWatcher.
            // DangerRating re-enters it INTERNALLY — see the class comment.
            var colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
            var scored = new List<KeyValuePair<int, Pawn>>();
            foreach (var pawn in colonists) scored.Add(new KeyValuePair<int, Pawn>(Attention(pawn), pawn));
            scored.Sort((a, b) =>
            {
                int c = b.Key.CompareTo(a.Key);
                return c != 0 ? c : string.CompareOrdinal(
                    a.Value.LabelShortCap.ToString(), b.Value.LabelShortCap.ToString());
            });

            var list = new List<object>();
            for (int i = 0; i < scored.Count && i < cap; i++)
            {
                var pawn = scored[i].Value;
                var flags = new List<object>();
                var health = pawn.health;
                if (pawn.Downed) flags.Add("downed");
                if (pawn.MentalStateDef != null) flags.Add("mental:" + pawn.MentalStateDef.defName);
                if (health.hediffSet.BleedRateTotal > 0.01f) flags.Add("bleeding");
                if (HealthAIUtility.ShouldBeTendedNowByPlayer(pawn)) flags.Add("tend");
                if (health.hediffSet.AnyHediffMakesSickThought) flags.Add("sick");

                string room = null;
                // LAZY + EXPENSIVE: full room analysis when dirty. Class comment.
                var r = pawn.GetRoom();
                if (r != null) room = r.PsychologicallyOutdoors ? "outside" : r.Role?.label;

                string job = null, jobError = null;
                try { job = pawn.jobs?.curDriver?.GetReport(); }
                catch (Exception e)
                {
                    // 2.6 nit: 2.1 swallowed this to null, which is
                    // indistinguishable from "no job" and left no trace at all.
                    // With 32 mods on the bench, a JobDriver whose GetReport
                    // throws is exactly the class of failure the zero-red-errors
                    // invariant exists to surface. Journal it (deduped by text,
                    // Journal.EmitWarning) and say so on the line.
                    jobError = e.GetType().Name + ": " + Journal.Truncate(e.Message, 120);
                    Journal.EmitWarning("digest: job report threw for "
                        + pawn.LabelShortCap + " (" + (pawn.CurJobDef?.defName ?? "no-jobdef") + "): " + jobError);
                }

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
                if (jobError != null) line["job_error"] = jobError;
                list.Add(line);
            }

            return new Dictionary<string, object>
            {
                ["list"] = list,
                ["total"] = scored.Count,
                ["more"] = scored.Count > cap ? scored.Count - cap : 0,
                // Not a preference: state it so a consumer knows position is
                // urgency, not roster order.
                ["order"] = "attention-desc",
            };
        }

        // What the reader would most regret not seeing. Additive so a colonist
        // who is downed AND bleeding outranks one who is only downed.
        private static int Attention(Pawn pawn)
        {
            int score = 0;
            if (pawn.Downed) score += 1000;
            if (pawn.MentalStateDef != null) score += 900;
            if (pawn.health.hediffSet.BleedRateTotal > 0.01f) score += 500;
            if (HealthAIUtility.ShouldBeTendedNowByPlayer(pawn)) score += 400;
            if (pawn.health.hediffSet.AnyHediffMakesSickThought) score += 200;
            var mood = pawn.needs?.mood;
            if (mood != null) score += Mathf.Clamp(100 - Mathf.RoundToInt(mood.CurLevelPercentage * 100f), 0, 100);
            return score;
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
                ["food_days"] = needers > 0 ? Math.Round(nutrition / needers, 1) : -1,
                ["food_needers"] = needers,
                ["food_nutrition"] = Mathf.Round(nutrition),
                ["meds"] = rc.GetCountIn(ThingCategoryDefOf.Medicine),
                ["steel"] = rc.GetCount(ThingDefOf.Steel),
                ["wood"] = rc.GetCount(ThingDefOf.WoodLog),
                ["silver"] = rc.GetCount(ThingDefOf.Silver),
                ["components"] = rc.GetCount(ThingDefOf.ComponentIndustrial),
                // See FIELD DOCS: every count above is stockpile-only.
                ["scope"] = "stockpiles-only",
            };
        }

        // 2.6 should-fix 5. 2.1 published gain_w (the NET) and stored_wd, which
        // cannot answer what its own scope asked — "generation vs draw + battery
        // days". Gen and draw were not separable and battery days needs draw.
        // Its caveat ("power validated at zero only") understates the gap: at
        // zero both figures are 0, which is precisely the state where a missing
        // split is invisible.
        //
        // The split mirrors PowerNet.CurrentEnergyGainRate exactly (decompiled
        // RimWorld/PowerNet.cs:181): only PowerOn comps count, and what is
        // summed is CompPowerTrader.PowerOutput, whose SIGN is the game's own
        // producer/consumer distinction. Batteries carry CompPowerBattery, not
        // CompPowerTrader, so they appear in neither accumulator — same as the
        // game's own display.
        private static Dictionary<string, object> PowerSection(Map map)
        {
            float genW = 0f, drawW = 0f, storedWd = 0f;
            int nets = 0, netsWithGenerator = 0, batteries = 0;
            var netList = map.powerNetManager.AllNetsListForReading;
            for (int i = 0; i < netList.Count; i++)
            {
                var net = netList[i];
                if (net.hasPowerSource) nets++;
                bool hasGenerator = false;
                var comps = net.powerComps;
                for (int j = 0; j < comps.Count; j++)
                {
                    var comp = comps[j];
                    // Props.PowerConsumption < 0 is the game's own IsPowerSource
                    // test for a trader (PowerNet.cs:102) and holds whether or
                    // not the thing is currently running or fuelled.
                    if (comp.Props != null && comp.Props.PowerConsumption < 0f) hasGenerator = true;
                    if (!comp.PowerOn) continue;
                    float w = comp.PowerOutput;
                    if (w > 0f) genW += w;
                    else drawW -= w;
                }
                if (hasGenerator) netsWithGenerator++;
                batteries += net.batteryComps.Count;
                storedWd += net.CurrentStoredEnergy();
            }
            float deficitW = drawW - genW;
            return new Dictionary<string, object>
            {
                ["gen_w"] = Mathf.Round(genW),
                ["draw_w"] = Mathf.Round(drawW),
                // The net balance 2.1 published, kept so the name stays honest
                // and so a consumer that already reads it is not broken.
                ["gain_w"] = Mathf.Round(genW - drawW),
                ["stored_wd"] = Mathf.Round(storedWd),
                // Watt-days / watts = days. null when generation covers the
                // draw: the batteries are charging and "days left" has no
                // meaning. This is the figure the morning checklist wants.
                ["battery_days"] = deficitW > 0.01f ? (object)Math.Round(storedWd / deficitW, 2) : null,
                ["batteries"] = batteries,
                // RIDER on 2.6 should-fix 5: `nets` is the game's own
                // hasPowerSource, and IsPowerSource counts a bare battery
                // (PowerNet.cs:96-107), so a battery-only grid reads as
                // powered. nets_with_generator is the strict test — gate on it.
                ["nets"] = nets,
                ["nets_with_generator"] = netsWithGenerator,
            };
        }

        private static Dictionary<string, object> ThreatSection(Map map)
        {
            int hostiles = 0;
            var kinds = new Dictionary<string, int>();
            // Safe to iterate: AllPawnsSpawned returns the real pawnsSpawned
            // list, not a cache rebuilt on read (decompiled MapPawns.cs:327).
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                var p = pawns[i];
                if (p.Downed || p.Dead || !p.HostileTo(Faction.OfPlayer)) continue;
                hostiles++;
                string kind = p.kindDef?.label ?? p.def.label;
                kinds[kind] = kinds.TryGetValue(kind, out var c) ? c + 1 : 1;
            }
            // 2.6 nit: 2.1 took the first three in Dictionary enumeration order
            // and called them "top-3". Hash order is not count order and is not
            // stable across runs, which breaks rwtest's stable-field contract.
            var byCount = new List<KeyValuePair<string, int>>(kinds);
            byCount.Sort((a, b) =>
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            var top = new List<object>();
            for (int i = 0; i < byCount.Count; i++)
            {
                if (i >= KindCap) { top.Add("+" + (byCount.Count - KindCap) + " kinds"); break; }
                top.Add(byCount[i].Key + " x" + byCount[i].Value);
            }
            var threats = new Dictionary<string, object>
            {
                // Recomputes every 101 ticks and re-enters FreeColonistsSpawned
                // internally — called here outside every pawn loop on purpose.
                ["danger"] = map.dangerWatcher.DangerRating.ToString(),
                ["hostiles"] = hostiles,
                ["kinds"] = top,
            };
            // Alert_FireInHomeArea covers only the home area and no other
            // vanilla alert covers fire at all, so the alert section — a
            // verbatim readout passthrough — cannot see a fire on unclaimed
            // ground (session-4 amendment on 2.4; ThingVerbs.FireScan is the
            // full answer). One count here so the DIGEST is not blind either;
            // present only when non-zero, like every other exception field.
            try
            {
                int fires = 0;
                var fireList = map.listerThings.ThingsInGroup(ThingRequestGroup.Fire);
                for (int i = 0; i < fireList.Count; i++)
                {
                    var f = fireList[i];
                    if (f == null || !f.Spawned) continue;
                    try { if (f.Position.Fogged(map)) continue; } catch { continue; }
                    fires++;
                }
                if (fires > 0) threats["fires"] = fires;
            }
            catch { }
            return threats;
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
