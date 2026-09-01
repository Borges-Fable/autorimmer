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
    // / (colonists + prisoners)), not a consumption simulation. It is also
    // STOCKPILE-ONLY and FRESH-ONLY and has NO ROT TERM — `resources.food_rot`
    // is the honest block beside it and `resources.food_days_basis` says so in
    // the data. See FoodRot.cs (git-bug 261f2e9).
    //
    // `temperature` is the only section whose `ok` concerns a CONTINUOUS value
    // rather than a count, and it is deliberately narrow: false when a room a
    // switched-on controller serves is off its target by more than
    // `tolerance_c`. It publishes no room ROLE, because Room.Role is the
    // most expensive line in this file and that section is predicate-addressable.
    //
    // RESOURCE COUNTS ARE STOCKPILE-ONLY. `steel`, `wood`, `silver`,
    // `components`, `meds` and `food_nutrition` all come from
    // map.resourceCounter, which walks SlotGroup haul destinations — so goods
    // lying in unzoned ground read as ZERO. A colony with 5000 steel scattered
    // across the crash site reports steel:0. Any wave-3 build verb that gates on
    // a resource count must say "in stockpiles" or count things itself, or it
    // will refuse a build the player could make.
    //
    // That last sentence was written as a warning and then came true: on
    // 2026-09-01 `place-layout` reported `short_by: 185` while 869 unforbidden
    // WoodLog lay ten cells from the site (git-bug 54b0c9a). `Materials.Of` is
    // the count that asks the builder's own question and is what a verdict must
    // now come from; `scope: "stockpiles-only"` below stays, because this
    // section draws no conclusion and the stockpile figure is a real fact.
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
                // WHERE the colony is (M1 finding I1). Constant for the life of
                // a map and a handful of plain field reads, so it rides in the
                // glance rather than in a verb of its own — nothing else in the
                // observation surface answered "what biome is this", and 4.3's
                // temperate fixture was being inferred from terrain glyphs.
                // WorldSafe.Site documents every member it reads and every
                // lazy-init getter it routes around.
                ["site"] = WorldSafe.Site(map),
                ["alerts"] = AlertSection(),
                // WHAT THE COLONY IS BUILDING (git-bug d7c8088). The section
                // that makes an idle-colonist run diagnosable in one call
                // instead of none: three colonists standing around with four
                // blueprints `awaiting_materials` is a different colony from
                // three colonists standing around with nothing to build, and
                // until now the glance could not tell them apart. Capped inside
                // ConstructionVerbs.Section — `Frame.WorkToBuild` is a
                // GetStatValueAbstract per frame and this verb is called
                // constantly.
                ["construction"] = ConstructionVerbs.Section(map),
                ["colonists"] = ColonistSection(map, colonistCap),
                // WHO COULD DO THE ESSENTIAL JOBS, AT EVERY READ (git-bug
                // 40ed42f). The M1 run had exactly one Doctor-enabled pawn and
                // he was the first casualty; both deaths were untended blood
                // loss. Under-coverage belongs in the glance rather than in an
                // emergency, which is the whole argument for it being here and
                // not in a verb the agent has to think to call. See
                // WorkCoverage's header for where every floor comes from.
                ["work_coverage"] = WorkCoverage.Section(map),
                // WHAT THE COLONY WILL DO WHEN SOMETHING ARRIVES (git-bug
                // b1b3060). The M1 run held its combat posture as three settings
                // remembered in the right order, and lost it silently: seek was
                // turned off to stop a march, which put every colonist back on
                // the vanilla flee branch, and nothing in the surface said so.
                // The three settings are one state, so they are published as one
                // block — `will_seek` and `area_bound` as the issue asks, plus
                // `attack` and `on_contact`, because the investigation in
                // SeekVerbs' posture header found that `hostility_response` is
                // decided ABOVE seek rather than beneath it. Cheap on session
                // 19's axis: no Room.Role, no GetStatValueAbstract, no pathfind.
                ["posture"] = PawnActs.PostureSection(map),
                ["resources"] = ResourceSection(map),
                ["power"] = PowerSection(map),
                // IS THE ROOM I TOLD TO BE COLD ACTUALLY COLD (git-bug
                // 261f2e9). Session 18 built a freezer, wired it, and read
                // 14.6 C — and the only way to find that out was to call
                // `room <id>` for a room whose id you already had to know.
                // Temperature is a continuous value that kills food silently,
                // which is exactly the class of thing the glance exists for;
                // `ok` is narrow (a switched-on controller's room off its
                // target) so it stays an alarm rather than a permanent
                // complaint. Cheap on session 19's axis — see
                // TemperatureVerbs' Section header for the per-member argument.
                ["temperature"] = TemperatureVerbs.Section(map),
                ["threats"] = ThreatSection(map),
                // WHICH WAY THE COLONY IS MOVING (git-bug 2d9a1da). Every other
                // section here is a LEVEL, and a level is what an alert already
                // is: `Alert_LowFood` fires AT the threshold, which is the
                // moment it is too late to plant. This block is the SLOPE — the
                // colony's `ticks_until_bleedout`, which `61794cd` shipped this
                // week for one pawn. Half of it is the game's own recorders
                // (wealth, threat points, mood, population, which RimWorld has
                // graphed since tick 0 and nothing here had ever read); half is
                // AutoRimmer's own 2,500-tick sampler, because the game records
                // no food series at all and food is what a ten-day run dies of.
                //
                // It is in the GLANCE for the M1 post-mortem's reason: that run
                // made 27 advances, 10 digests and zero journal calls, so an
                // indicator behind a verb the agent must remember to call is an
                // indicator nobody reads. Cheapest section in the file on
                // session 19's axis — it reads no game state for its own fields
                // (the ring is already in memory) and the game's half is plain
                // field reads over 11 recorders. See ColonySampler.TrendSection.
                ["trends"] = ColonySampler.TrendSection(map),
                ["changed"] = ChangedSection(since),
            };
        }

        // ------------------------------------------------ the predicate view --
        //
        // ONE SECTION, BY NAME, for `advance {until:{condition:{path…}}}` (spec
        // 1.6). The predicate addresses this verb's own field set, so it must
        // read this verb's own builders — but building the WHOLE digest once
        // per cadence window would pay for every section to answer a question
        // about one, and the sections are not the same price. `colonists` costs
        // a `Room.Role` per colonist (the most expensive line in this file);
        // `resources` walks the counted amounts calling GetStatValueAbstract
        // per def; `time` is nine field reads. A predicate on the clock must
        // not cost a room analysis.
        //
        // `changed` is deliberately NOT addressable: it is a journal delta
        // since a seq the caller passed, so it is a question about the past
        // rather than a reading of colony state, and there is no `since` to
        // pass from inside an advance.
        //
        // The colonist cap is deliberately the MAXIMUM the verb allows rather
        // than its context-sized default: `colonists.list[*]` under an `all`
        // quantifier is wrong — silently, and in the direction of halting early
        // — if the list it quantifies over was truncated for context budget.
        // Nothing here is being sent to a model.
        internal static readonly string[] PredicateSections =
            { "time", "site", "alerts", "construction", "colonists", "work_coverage",
              "posture", "resources", "power", "temperature", "threats", "trends" };

        internal static bool IsPredicateSection(string name)
        {
            for (int i = 0; i < PredicateSections.Length; i++)
                if (PredicateSections[i] == name) return true;
            return false;
        }

        internal static Dictionary<string, object> SectionFor(Map map, string name)
        {
            if (map == null) return null;
            switch (name)
            {
                case "time": return TimeSection(map);
                case "site": return WorldSafe.Site(map);
                case "alerts": return AlertSection();
                case "construction": return ConstructionVerbs.Section(map);
                case "colonists": return ColonistSection(map, 200);
                // Cheap on the axis that matters: no Room.Role, no
                // GetStatValueAbstract. It is a roster walk times the essential
                // work types, with a CalculateCapacityLevel only for pawns who
                // actually have the type enabled. `work_coverage.ok == false`
                // is therefore an affordable predicate, and it is the one an
                // agent wants — "stop when the colony loses its second doctor".
                case "work_coverage": return WorkCoverage.Section(map);
                // Same axis, same verdict as work_coverage: a roster walk of
                // field reads, one dictionary lookup and a GetLord() per pawn,
                // plus three cached-MethodInfo invokes into SeekAndKill whose
                // bodies are seven field reads and a HashSet lookup. No
                // Room.Role, no GetStatValueAbstract, no pathfind — so
                // `posture.ok == false` is an affordable halt, and it is the one
                // the M1 post-mortem asks for: stop when the colony stops
                // holding the posture it was set to.
                case "posture": return PawnActs.PostureSection(map);
                case "resources": return ResourceSection(map);
                case "power": return PowerSection(map);
                // Same axis, same verdict as work_coverage and posture: one
                // walk of the real `allBuildingsColonist` list with a memoised
                // per-def comp test, one walk of the stored
                // FoodSourceNotPlantOrTree list with a memoised per-def
                // Nutrition, region-grid room lookups and plain field reads. No
                // Room.Role, no Room.GetStat, no pathfind — so
                // `temperature.ok == false` is an affordable halt, and it is
                // the one a ten-day food run wants: stop when the room that is
                // supposed to be cold stops being cold.
                case "temperature": return TemperatureVerbs.Section(map);
                case "threats": return ThreatSection(map);
                // The cheapest predicate section there is: arithmetic over a
                // ring already in memory plus 11 field reads. So
                // `advance {until:{condition:{path:"trends.food_days_per_day",
                // op:"<=", value:-1.0}}}` — "stop when the colony starts losing
                // more than a food-day per day" — is affordable, and it is the
                // leading indicator this whole surface was missing.
                //
                // ONE TRAP, stated where a caller writing a predicate will read
                // it: `trends.*_to_zero` is NULL whenever the stock is not
                // falling, and StateWatch.One() refuses an ordering operator
                // against null — at arm time that is a clean refusal, but
                // mid-advance Poll returns false and never halts, so an advance
                // waiting on `food_days_to_zero <= 2` stops halting the moment
                // food stops falling. Predicates want `*_per_day`, which is
                // always a number once `trends.ready` is true.
                case "trends": return ColonySampler.TrendSection(map);
                default: return null;
            }
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
            var store = AlertMuteComponent.Current;
            var list = new List<object>();
            var droppedByPriority = new Dictionary<string, object>();
            int mutedLive = 0;
            for (int i = 0; i < live.Count; i++)
            {
                bool muted = store != null && store.Has(live[i].Id);
                if (muted) mutedLive++;
                if (i < AlertCap)
                {
                    list.Add(new Dictionary<string, object>
                    {
                        ["id"] = live[i].Id,
                        ["label"] = live[i].Label,
                        ["priority"] = live[i].Priority.ToString(),
                        // git-bug 280fb78. Beside the thing it modifies, the
                        // way `threat-pardon` puts `pardoned` on the candidate
                        // row: a reader looking at this alert is told, here,
                        // that it has been decided not to wake for it.
                        ["muted"] = muted,
                    });
                    continue;
                }
                string key = live[i].Priority.ToString();
                droppedByPriority[key] = droppedByPriority.TryGetValue(key, out var n) ? (int)n + 1 : 1;
            }
            var data = new Dictionary<string, object>
            {
                ["active"] = list,
                ["total"] = live.Count,
                ["more"] = live.Count > AlertCap ? live.Count - AlertCap : 0,
                // What the cap cost, by severity — so "more:3" is never a
                // question about whether something important was hidden.
                ["more_by_priority"] = droppedByPriority,
            };
            // ============================================== git-bug 280fb78 ==
            // THE STANDING DECISION, IN THE GLANCE. `alert_on` now halts an
            // advance unconditionally and `alert-mute` is how an agent stops a
            // chronic one waking it — which makes the mute list exactly the
            // shape of failure `b1b3060` shipped `digest.posture` to close
            // ([[seek-off-is-a-decision-to-flee]]): a standing decision the
            // agent cannot see is one it will forget it made and then be
            // baffled by.
            //
            // A SEPARATE LIST AND NOT ONLY THE PER-ROW FLAG, because the two
            // answer different questions and only one of them survives the day.
            // `active[*].muted` says "this alert is up and you have decided to
            // ignore it"; `muted` says "here is every decision of that kind you
            // are holding", INCLUDING the ones whose alert is not currently up
            // — which is the day-8 case the issue names. It is also outside the
            // `AlertCap` truncation on purpose: the cap drops LIVE rows by
            // priority, and a mute that fell off the bottom of a busy readout
            // would be a standing decision hidden by a display budget.
            //
            // Uncapped is safe on the axis that matters: this list is bounded
            // by what the AGENT muted, one act at a time with a required
            // reason, not by anything the colony can generate.
            var muteList = new List<object>();
            if (store != null)
            {
                var liveIds = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < live.Count; i++) liveIds.Add(live[i].Id);
                var ids = new List<string>(store.All.Keys);
                ids.Sort(StringComparer.Ordinal);
                foreach (var id in ids)
                    muteList.Add(new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["reason"] = store.Reason(id),
                        ["live"] = liveIds.Contains(id),
                    });
            }
            data["muted"] = muteList;
            data["muted_count"] = muteList.Count;
            data["muted_live"] = mutedLive;
            return data;
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

        // THE DIVISOR IS THE ALERT'S EXPLANATION, NOT ITS TRIGGER, and the two
        // are different members. Verified in the decompiled 1.6 tree:
        //
        //   Alert_LowFood.MapWithLowFood  -> `map.resourceCounter
        //       .TotalHumanEdibleNutrition < 4f * (float)freeColonistsSpawnedCount`
        //   Alert_LowFood.GetExplanation  -> `FreeColonistsSpawnedCount
        //       + PrisonersOfColonyCount`
        //
        // So the ALERT fires at nutrition per COLONIST < 4, while `food_days`
        // below divides by colonists PLUS PRISONERS, mirroring the sentence the
        // player reads. With no prisoners the two are identical; with prisoners
        // `food_days` is the SMALLER number.
        //
        // It matters now that this is a predicate target (spec 1.6): a caller
        // writing `resources.food_days < N` to LEAD the alert must put N above
        // 4, not below it. `food_days < 3` is strictly LATER than the alert on
        // a prisoner-free colony — it only wins when 3 x prisoners > colonists.
        // The leading-indicator argument is still right, and its real reason is
        // elsewhere: `Alert_LowFood.GetReport` opens with
        // `if ((float)Find.TickManager.TicksGame < 150000f) return false;`, so
        // for the first 2.5 in-game days the alert CANNOT fire however empty
        // the larder is, and a state predicate can. (git-bug fc287ba #1.)
        //
        // AND IT HAS NO ROT TERM AT ALL — git-bug 261f2e9, and it is the second
        // half of the same lie. `ResourceCounter.ShouldCount` opens with
        // `if (t.IsNotFresh()) return false;`, so a stack leaves this division
        // the instant it finishes rotting, with nothing said during the ramp:
        // `food_days` holds its value and then falls off a cliff. That is the
        // M1 death shape (a surface showing a number that is not the thing
        // killing you) one system over. `food_days` IS NOT REDEFINED — it is a
        // shipped predicate target with suites asserting on it, and "what the
        // vanilla alert will do" is a real question. What is added is
        // `food_days_basis`, so the disclaimer stops living only in this
        // comment, and `food_rot` beside it, which counts map-wide and carries
        // the clock. FoodRot.cs holds the argument and the citations.
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
                // THE DISCLAIMER, IN THE DATA. `Materials.cs` exists because a
                // true warning in a source comment did not stop the code three
                // lines below it from drawing a conclusion the agent could not
                // check (git-bug 54b0c9a). Same fix, applied to the field the
                // agent actually reads.
                ["food_days_basis"] = "vanilla Alert_LowFood: map.resourceCounter's "
                    + "TotalHumanEdibleNutrition / (colonists + prisoners). STOCKPILE-ONLY "
                    + "(ResourceCounter walks SlotGroup haul destinations, so food on unzoned "
                    + "ground reads as zero) and FRESH-ONLY (ShouldCount drops anything "
                    + "IsNotFresh, so rotted food leaves this number with no warning). It has NO "
                    + "rot term — read `food_rot` for the honest map-wide figure and the clock.",
                ["meds"] = rc.GetCountIn(ThingCategoryDefOf.Medicine),
                ["steel"] = rc.GetCount(ThingDefOf.Steel),
                ["wood"] = rc.GetCount(ThingDefOf.WoodLog),
                ["silver"] = rc.GetCount(ThingDefOf.Silver),
                ["components"] = rc.GetCount(ThingDefOf.ComponentIndustrial),
                // See FIELD DOCS: every count above is stockpile-only.
                ["scope"] = "stockpiles-only",
                // The one block in this section that is NOT stockpile-scoped,
                // and it says so on itself.
                ["food_rot"] = FoodRot.Block(FoodRot.Of(map), needers, nutrition),
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
            int pardoned = 0;
            var kinds = new Dictionary<string, int>();
            // Safe to iterate: AllPawnsSpawned returns the real pawnsSpawned
            // list, not a cache rebuilt on read (decompiled MapPawns.cs:327).
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                var p = pawns[i];
                if (p.Downed || p.Dead || !p.HostileTo(Faction.OfPlayer)) continue;
                hostiles++;
                // M1 finding D. Pardoned = the colony has RECORDED a decision not
                // to fight this one (`threat-pardon`), and it is still dormant.
                // Pure read: ThreatPardonComponent.Pardoned never prunes the
                // scribed set, precisely so an observer cannot write.
                if (ThreatPardonComponent.Pardoned(p)) pardoned++;
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
                // `hostiles` KEEPS its meaning — the total, everything, always.
                // A pardon is a recorded decision, not a filter, so it must never
                // be able to shrink the field a reader has always trusted. The
                // two new fields sit ALONGSIDE it (M1 finding D); their names are
                // a fixed contract with accept/4.2-play-loop.py, which keys on
                // `hostiles_unpardoned` and falls back to `hostiles` when absent.
                ["hostiles"] = hostiles,
                ["hostiles_pardoned"] = pardoned,
                ["hostiles_unpardoned"] = hostiles - pardoned,
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
