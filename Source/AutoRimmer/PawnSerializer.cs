using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoRimmer
{
    // Pawn serializers (spec 2.2). One section per builder method, plain
    // dictionaries, stable snake_case field names — the shape 2.1's DigestVerb
    // established and 2.4 will follow. Every read routes through PawnSafe where
    // the game's own accessor is unsafe; READ PawnSafe's header before adding
    // a field here, because the traps are not visible at the call site.
    //
    // ===================== TRUNCATION IS A CONTRACT =========================
    // Same rule as the digest: every list-valued section caps, orders by what
    // the reader would most regret losing BEFORE it cuts, and publishes what it
    // dropped. `PawnSafe.Capped` is the shape.
    //
    // ========================= FIELD DOCS ==================================
    //
    // `id` is thingIDNumber. Stable across saves and the id every other verb
    // takes (`pawn:<id>` in Positions.Resolve). NOT stable across colonies.
    //
    // `needs[].arrow` and `mood.arrow` are the needs tab's OWN trajectory
    // arrow (Need.GUIChangeArrow): +1 rising, 0 steady, -1 falling. This is the
    // "recent trend" the spec asks for, and it is deliberately not a sampled
    // window — see the resolution comment on git-bug 69ae91f. `instant_pct` is
    // the instant marker the tab draws beside the bar (CurInstantLevelPercentage),
    // null where the need has no instant level (Need.CurInstantLevel is -1 by
    // default and only the seeker needs override it).
    //
    // `mood.thoughts` is the game's own grouping — ThoughtHandler
    // .GetDistinctMoodThoughtGroups filtered by VisibleInNeedsTab, offsets from
    // MoodOffsetOfGroup — i.e. exactly what PawnNeedsUIUtility
    // .GetThoughtGroupsInDisplayOrder builds, with two deliberate deviations:
    // we do NOT write Thought.cachedMoodOffsetOfGroup (that is the game's sort
    // scratch field; observers do not write), and the tie-break is the def name
    // rather than GetHashCode(), which is not stable run to run and would break
    // rwtest's stable-field contract.
    //
    // `skills[].level` is GetLevelForUI() — the number printed on the Character
    // tab, which zeroes only for a PERMANENTLY disabled skill. `level_raw` is
    // the stored levelInt, before aptitudes and before either disable rule; the
    // two differ on a pawn with skill-affecting genes or a temporary disable.
    //
    // `apparel.worn[].wear` uses the GAME's thresholds, not ours:
    // ThoughtWorker_ApparelDamaged is `frayed` below 0.5 and `tattered` below
    // 0.2 of MaxHitPoints, counting only apparel with def.useHitPoints, not
    // locked, and careIfDamaged. `hp`/`hp_pct` are OMITTED (null) for
    // !useHitPoints things: their HitPoints field is the uninitialised -1
    // (Verse/Thing.cs) while MaxHitPoints still returns a plausible stat, so
    // dividing them yields a silently wrong ratio.
    //
    // `health.capacities[].pct` is PawnCapacityUtility.CalculateCapacityLevel —
    // the number the health tab PRINTS. HealthCardUtility.GetEfficiencyLabel
    // uses the cached capacities.GetLevel only to pick the label COLOUR
    // (RimWorld/HealthCardUtility.cs), so matching the tab means calling the
    // pure function, not the cache.
    //
    // `work.row` is an ORDERED LIST, not an object, because the Work tab's
    // column order is generated rather than declared (PawnColumnDefGenerator
    // over WorkTypeDefsUtility.WorkTypeDefsInPriorityOrder, filtered to
    // def.visible; net left-to-right is naturalPriority DESCENDING) and a JSON
    // object's key order is not a contract. Read `work.use_priorities` with it:
    // when the player has priorities switched off, GetPriority returns a hard 3
    // for every active work type and the number here is not the stored number.
    //
    // `relations` is DIRECT relations only by default — the DirectRelations
    // backing list, zero writes, zero graph walks. Opinions are opt-in and
    // bounded; see PawnSafe Class C.
    //
    // Single-map by design (v1): callers pass Find.CurrentMap.
    public static class PawnSerializer
    {
        // Caps. Every one of these bounds a list that could grow with mods.
        public const int NeedCap = 12;
        public const int ThoughtSideCap = 6;   // top N positive AND top N negative
        public const int HediffCap = 20;
        public const int CapacityCap = 16;
        public const int SkillCap = 24;
        public const int ApparelCap = 16;
        public const int EquipmentCap = 6;
        public const int InventoryCap = 12;
        public const int WorkCap = 40;
        public const int RelationCap = 16;
        public const int OpinionCap = 12;
        public const int JobClip = 64;

        // ------------------------------------------------------------------
        // The brief line: `pawns`. HOT PATH — no stat calls, no room analysis,
        // no thought recompute, no relations. Roughly 120 bytes a line.
        // ------------------------------------------------------------------
        public static Dictionary<string, object> Brief(Pawn pawn, string cls)
        {
            var flags = new List<object>();
            var health = pawn.health;
            if (pawn.Downed) flags.Add("downed");
            if (pawn.MentalStateDef != null) flags.Add("mental:" + pawn.MentalStateDef.defName);
            if (health != null && health.hediffSet != null)
            {
                if (health.hediffSet.BleedRateTotal > 0.01f) flags.Add("bleeding");
                if (health.hediffSet.AnyHediffMakesSickThought) flags.Add("sick");
            }
            if (SafeShouldTend(pawn)) flags.Add("tend");
            if (pawn.Drafted) flags.Add("drafted");
            if (pawn.InContainerEnclosed) flags.Add("contained");

            string job = null, jobError = null;
            try { job = pawn.jobs?.curDriver?.GetReport(); }
            catch (Exception e)
            {
                // 2.1's lesson, kept: a JobDriver whose GetReport throws is
                // exactly the failure the zero-red-errors invariant exists to
                // surface, and swallowing it to null is indistinguishable from
                // "no job". Journal it (deduped by text) and say so on the line.
                jobError = e.GetType().Name + ": " + Journal.Truncate(e.Message, 120);
                Journal.EmitWarning("pawns: job report threw for " + PawnSafe.Name(pawn)
                    + " (" + (pawn.CurJobDef?.defName ?? "no-jobdef") + "): " + jobError);
            }

            var mood = pawn.needs?.mood;
            var line = new Dictionary<string, object>
            {
                ["id"] = pawn.thingIDNumber,
                ["name"] = PawnSafe.Name(pawn),
                ["class"] = cls,
                ["kind"] = pawn.kindDef?.defName,
                ["faction"] = pawn.Faction?.def?.defName,
                ["at"] = Positions.Out(pawn.Position),
                ["mood_pct"] = mood != null ? (object)PawnSafe.Pct(mood.CurLevelPercentage) : null,
                ["health_pct"] = SafeSummaryHealthPct(pawn),
                ["job"] = Journal.Truncate(job, JobClip),
            };
            if (flags.Count > 0) line["flags"] = flags;
            if (jobError != null) line["job_error"] = jobError;
            return line;
        }

        // What the reader would most regret not seeing, in the digest's additive
        // shape so a downed AND bleeding pawn outranks one that is only downed.
        //
        // A SELECTION key, not an identity key (git-bug 1eb2262). The mood term
        // is `100 - mood_pct`, so this score moves on any tick that moves a
        // mood, and the 400-1000 point flags flip on injury and recovery — two
        // reads seconds apart legitimately rank the same colony differently.
        // The caller decides the tie-break (`pawns` uses thingIDNumber; the
        // digest keeps its own private copy of this method and ties on name),
        // and `pawns` no longer EMITS in this order by default because a
        // consumer cannot hold an index into a list ordered by a live value.
        public static int Attention(Pawn pawn)
        {
            int score = 0;
            try
            {
                if (pawn.Downed) score += 1000;
                if (pawn.MentalStateDef != null) score += 900;
                var hs = pawn.health?.hediffSet;
                if (hs != null && hs.BleedRateTotal > 0.01f) score += 500;
                if (SafeShouldTend(pawn)) score += 400;
                if (hs != null && hs.AnyHediffMakesSickThought) score += 200;
                var mood = pawn.needs?.mood;
                if (mood != null) score += Mathf.Clamp(100 - PawnSafe.Pct(mood.CurLevelPercentage), 0, 100);
            }
            catch { }
            return score;
        }

        // ------------------------------------------------------------------
        // The drill-down: `pawn <id>`. Sections are individually selectable so
        // a caller paying for context can ask for two of them.
        // ------------------------------------------------------------------
        public static readonly string[] AllSections =
        {
            "identity", "state", "needs", "mood", "health", "skills", "apparel",
            "equipment", "inventory", "schedule", "work", "area", "relations",
        };

        public static Dictionary<string, object> Detail(Pawn pawn, Map map, HashSet<string> want, VerbArgs args)
        {
            string cls = PawnSafe.Classify(pawn);
            var d = new Dictionary<string, object>
            {
                ["id"] = pawn.thingIDNumber,
                ["name"] = PawnSafe.Name(pawn),
                ["class"] = cls,
            };
            if (want.Contains("identity")) d["identity"] = Identity(pawn, cls);
            if (want.Contains("state")) d["state"] = State(pawn);
            if (want.Contains("needs")) d["needs"] = Needs(pawn);
            if (want.Contains("mood")) d["mood"] = Mood(pawn);
            if (want.Contains("health")) d["health"] = Health(pawn);
            if (want.Contains("skills")) d["skills"] = Skills(pawn);
            if (want.Contains("apparel")) d["apparel"] = Apparel(pawn);
            if (want.Contains("equipment")) d["equipment"] = Equipment(pawn);
            if (want.Contains("inventory")) d["inventory"] = Inventory(pawn);
            if (want.Contains("schedule")) d["schedule"] = Schedule(pawn);
            if (want.Contains("work")) d["work"] = WorkRow(pawn);
            if (want.Contains("area")) d["area"] = Area(pawn);
            if (want.Contains("relations"))
                d["relations"] = Relations(pawn, map,
                    args.Bool("opinions", false),
                    Math.Min(OpinionCap, Math.Max(0, args.Int("opinion_cap", OpinionCap))));
            return d;
        }

        // ---------------------------- identity -----------------------------
        private static Dictionary<string, object> Identity(Pawn pawn, string cls)
        {
            var story = pawn.story;
            var traits = new List<object>();
            if (story?.traits?.allTraits != null)
            {
                // allTraits is the real backing list; TraitsSorted clears and
                // refills a shared tmp list on every access (PawnSafe Class E).
                var all = story.traits.allTraits;
                for (int i = 0; i < all.Count; i++)
                {
                    var t = all[i];
                    if (t?.def == null) continue;
                    traits.Add(new Dictionary<string, object>
                    {
                        ["def"] = t.def.defName,
                        ["label"] = Safe(() => t.LabelCap),
                        ["degree"] = t.Degree,
                        ["suppressed"] = Safe(() => (object)t.Suppressed) ?? false,
                    });
                }
            }

            Dictionary<string, object> ideo = null;
            // Ideology guard FIRST, then the tracker: pawn.ideo exists for every
            // humanlike regardless of DLC, but ideo.Ideo is null without it.
            // Certainty is NOT read — its getter writes a scribed field on a
            // baby and routes through the life-stage recalc (PawnSafe Class A/C).
            if (ModsConfig.IdeologyActive && pawn.Ideo != null)
            {
                string role = null;
                try { role = pawn.Ideo.GetRole(pawn)?.LabelCap; } catch { }
                ideo = new Dictionary<string, object>
                {
                    ["name"] = pawn.Ideo.name,
                    ["role"] = role,
                };
            }

            return new Dictionary<string, object>
            {
                ["class"] = cls,
                ["kind"] = pawn.kindDef?.defName,
                ["species"] = pawn.def?.defName,
                ["faction"] = pawn.Faction?.def?.defName,
                ["faction_name"] = pawn.Faction?.Name,
                ["gender"] = pawn.gender.ToString(),
                // Pure ticks/3600000 division, both of them (Verse/
                // Pawn_AgeTracker.cs). CurLifeStage is NOT read: it recalculates
                // the life-stage index, which can rename the pawn.
                ["age_bio"] = pawn.ageTracker?.AgeBiologicalYears ?? -1,
                ["age_chrono"] = pawn.ageTracker?.AgeChronologicalYears ?? -1,
                ["childhood"] = story?.Childhood?.title,
                ["adulthood"] = story?.Adulthood?.title,
                ["traits"] = traits,
                ["ideo"] = ideo,
                ["guest"] = Guest(pawn),
                ["policies"] = PawnSafe.Policies(pawn),
            };
        }

        // Every field here is a plain field on Pawn_GuestTracker. resistance and
        // will are sentinel -1f until initialized — that means "not yet a
        // prisoner", not "zero resistance", so -1 is published as null.
        private static Dictionary<string, object> Guest(Pawn pawn)
        {
            var g = pawn.guest;
            if (g == null) return null;
            var status = Safe(() => pawn.GuestStatus?.ToString());
            return new Dictionary<string, object>
            {
                ["status"] = status,
                ["host_faction"] = g.HostFaction?.def?.defName,
                ["is_prisoner"] = g.IsPrisoner,
                ["is_slave"] = g.IsSlave,
                ["resistance"] = g.resistance >= 0f ? (object)PawnSafe.R(g.resistance, 2) : null,
                ["will"] = g.will >= 0f ? (object)PawnSafe.R(g.will, 2) : null,
                ["interaction"] = Safe(() => g.ExclusiveInteractionMode?.defName),
                ["released"] = Safe(() => (object)g.Released) ?? false,
            };
        }

        // ------------------------------ state ------------------------------
        private static Dictionary<string, object> State(Pawn pawn)
        {
            string job = null, jobError = null;
            try { job = pawn.jobs?.curDriver?.GetReport(); }
            catch (Exception e)
            {
                jobError = e.GetType().Name + ": " + Journal.Truncate(e.Message, 120);
                Journal.EmitWarning("pawn: job report threw for " + PawnSafe.Name(pawn)
                    + " (" + (pawn.CurJobDef?.defName ?? "no-jobdef") + "): " + jobError);
            }
            var d = new Dictionary<string, object>
            {
                ["at"] = Positions.Out(pawn.Position),
                ["spawned"] = pawn.Spawned,
                ["drafted"] = pawn.Drafted,
                // Null when SeekAndKill is not loaded — "no such concept on
                // this bench" is a different answer from "off", and an agent
                // deciding whether combat is automated needs to tell them apart.
                //
                // READ SAFETY, audited 2026-08-31 and CONDITIONAL — do not
                // shorten this to "read-only". SeekStateOf calls
                // SeekRegistry.IsToggled/StanceOf/ShouldSeek and
                // Patch_PawnGetGizmos.ShowsSeekGizmo. Those reach
                // SeekRegistry.ActiveSet, which has TWO branches:
                //   * SeekAndKillGameComponent.OwnSeekSet / StanceByPawn are
                //     `??=` lazy inits over SCRIBED fields, but both carry field
                //     initialisers and are repaired in ExposeData's PostLoadInit
                //     arm, so the only null window is INSIDE a scribe pass and
                //     our verbs run at GameComponentUpdate. Unreachable.
                //   * PSInterop.PsSeekSet, taken only when Perspective Shift is
                //     loaded, does a reflective SetValue that INSTALLS a fresh
                //     HashSet into PS's scribed seekAtWillPawns field when it
                //     reads null (PSInterop.cs, PsSeekSet). That is a genuine
                //     write-on-read, and this observer would trigger it.
                // PS is absent from the agent bench by construction
                // (profile/make-profile-agent.sh: "no PerspectiveShift"), so the
                // second branch is dead HERE. If PS is ever added, re-audit
                // before trusting this line.
                ["seek"] = PawnActs.SeekStateOf(pawn),
                ["downed"] = pawn.Downed,
                ["dead"] = pawn.Dead,
                ["awake"] = Safe(() => (object)pawn.Awake()) ?? true,
                ["in_bed"] = Safe(() => (object)pawn.InBed()) ?? false,
                ["mental"] = pawn.MentalStateDef?.defName,
                ["job"] = Journal.Truncate(job, 160),
                ["job_def"] = pawn.CurJobDef?.defName,
            };
            if (jobError != null) d["job_error"] = jobError;
            return d;
        }

        // ------------------------------ needs ------------------------------
        // AllNeeds is the real backing list (RimWorld/Pawn_NeedsTracker.cs).
        // CurLevel is a plain field read; the cost is in GUIChangeArrow and
        // CurInstantLevel, which for the seeker needs recompute the instant
        // level (memoized per tick for mood) and for Need_Food reach
        // GetStatValue. All read-only.
        private static Dictionary<string, object> Needs(Pawn pawn)
        {
            var needs = pawn.needs?.AllNeeds;
            if (needs == null) return null;
            var rows = new List<object>();
            for (int i = 0; i < needs.Count; i++)
            {
                var n = needs[i];
                if (n?.def == null) continue;
                object instant = null;
                try { if (n.CurInstantLevel >= 0f) instant = PawnSafe.Pct(n.CurInstantLevelPercentage); }
                catch { }
                rows.Add(new Dictionary<string, object>
                {
                    ["def"] = n.def.defName,
                    ["label"] = Safe(() => n.LabelCap.ToString()),
                    ["pct"] = PawnSafe.Pct(n.CurLevelPercentage),
                    ["arrow"] = Safe(() => (object)n.GUIChangeArrow) ?? 0,
                    ["instant_pct"] = instant,
                });
            }
            // Lowest need first: the one about to cause a problem is the one
            // worth keeping when the cap bites.
            rows.Sort((a, b) => Cmp((Dictionary<string, object>)a, (Dictionary<string, object>)b, "pct", "def"));
            return PawnSafe.Capped(rows, NeedCap, "pct-asc");
        }

        // ------------------------------ mood -------------------------------
        private static Dictionary<string, object> Mood(Pawn pawn)
        {
            var mood = pawn.needs?.mood;
            if (mood == null) return null;

            var groups = new List<Thought>();
            var scratch = new List<Thought>();
            var scored = new List<KeyValuePair<float, Thought>>();
            try
            {
                // The game's own grouping. Our OWN list, never the handler's
                // static scratch: MoodOffsetOfGroup uses a private static
                // tmpThoughts internally and would clobber a shared one.
                mood.thoughts.GetDistinctMoodThoughtGroups(groups);
                for (int i = groups.Count - 1; i >= 0; i--)
                {
                    // VisibleInNeedsTab dereferences CurStage, which a modded
                    // thought can leave null. Anything but a definite `true`
                    // drops the group rather than the whole section.
                    var g = groups[i];
                    if ((Safe(() => (object)g.VisibleInNeedsTab) as bool?) != true) groups.RemoveAt(i);
                }
                for (int i = 0; i < groups.Count; i++)
                {
                    float off = 0f;
                    try { off = mood.thoughts.MoodOffsetOfGroup(groups[i]); } catch { }
                    scored.Add(new KeyValuePair<float, Thought>(off, groups[i]));
                }
            }
            catch (Exception e)
            {
                Journal.EmitWarning("pawn: mood thoughts threw for " + PawnSafe.Name(pawn) + ": " + e.Message);
            }

            // Offset descending, tie-broken on the group's DEF NAME. Vanilla
            // ties on GetHashCode(), which is not stable run to run — fine for
            // a UI list, fatal for an rwtest assertion.
            scored.Sort((a, b) =>
            {
                int c = b.Key.CompareTo(a.Key);
                return c != 0 ? c : string.CompareOrdinal(a.Value?.def?.defName ?? "", b.Value?.def?.defName ?? "");
            });

            // "top +/- thoughts": the head and the tail of that order. The
            // middle is what gets dropped, and the middle is the near-zero
            // stuff — which is the right thing to lose.
            var emit = new List<object>();
            int headCount = 0;
            int headMax = Math.Min(ThoughtSideCap, scored.Count);
            for (int i = 0; i < headMax; i++)
            {
                if (scored[i].Key <= 0f) break;
                emit.Add(ThoughtLine(mood, scored[i], scratch));
                headCount++;
            }
            // Walk back from the most negative. The floor is headCount — the
            // indices the head already took — NOT a running total, which would
            // tighten as the tail grows and leave room unused (a -2 dropped
            // while -8 was emitted and four slots were free).
            var tail = new List<object>();
            for (int i = scored.Count - 1; i >= headCount && tail.Count < ThoughtSideCap; i--)
            {
                if (scored[i].Key >= 0f) break;
                tail.Add(ThoughtLine(mood, scored[i], scratch));
            }
            // Reversed so `thoughts` is a strict monotone-descending
            // subsequence of the sort, which is what `order` claims and what
            // rwtest can assert. Collected worst-first only because the walk
            // runs backwards.
            tail.Reverse();
            emit.AddRange(tail);

            return new Dictionary<string, object>
            {
                ["pct"] = PawnSafe.Pct(mood.CurLevelPercentage),
                ["arrow"] = Safe(() => (object)mood.GUIChangeArrow) ?? 0,
                ["instant_pct"] = Safe(() => (object)PawnSafe.Pct(mood.CurInstantLevelPercentage)),
                ["state"] = Safe(() => mood.MoodString),
                ["mental"] = pawn.MentalStateDef?.defName,
                // The three break thresholds the mood bar draws, as percentages
                // of the bar. All three are one GetStatValue on
                // MentalBreakThreshold (Verse.AI/MentalBreaker.cs).
                ["break_minor_pct"] = Safe(() => (object)PawnSafe.Pct(pawn.mindState.mentalBreaker.BreakThresholdMinor)),
                ["break_major_pct"] = Safe(() => (object)PawnSafe.Pct(pawn.mindState.mentalBreaker.BreakThresholdMajor)),
                ["break_extreme_pct"] = Safe(() => (object)PawnSafe.Pct(pawn.mindState.mentalBreaker.BreakThresholdExtreme)),
                ["thoughts"] = emit,
                ["thoughts_total"] = scored.Count,
                ["thoughts_more"] = Math.Max(0, scored.Count - emit.Count),
                ["order"] = "offset-desc-head-and-tail",
            };
        }

        private static Dictionary<string, object> ThoughtLine(
            Need_Mood mood, KeyValuePair<float, Thought> g, List<Thought> scratch)
        {
            int count = 1;
            try { mood.thoughts.GetMoodThoughts(g.Value, scratch); count = scratch.Count; }
            catch { }
            return new Dictionary<string, object>
            {
                ["def"] = g.Value?.def?.defName,
                ["label"] = Safe(() => g.Value.LabelCap),
                // The GROUP's offset, from the game's own stacking maths — not
                // a single thought's baseMoodEffect times a count.
                ["offset"] = PawnSafe.R(g.Key, 1),
                ["count"] = count,
            };
        }

        // ----------------------------- health ------------------------------
        private static Dictionary<string, object> Health(Pawn pawn)
        {
            var h = pawn.health;
            if (h?.hediffSet == null) return null;

            var hediffs = new List<object>();
            // Snapshot the real list, then precompute the sort keys BEFORE
            // sorting. Rank() and Severity both go through virtual getters that
            // a modded hediff can make throw; a comparator that swallows an
            // intermittent throw is an INCONSISTENT comparator, and List.Sort
            // answers that with "IComparer.Compare() method returns inconsistent
            // results" — turning a cosmetic mod bug into a failed verb.
            var raw = new List<Hediff>(h.hediffSet.hediffs);
            var keyed = new List<KeyValuePair<long, Hediff>>();
            for (int i = 0; i < raw.Count; i++)
            {
                var hd0 = raw[i];
                if (hd0 == null) continue;
                // rank in the high bits, severity (x1000, clamped) in the low —
                // one long key, so the comparison itself cannot run game code.
                long sev = (long)Mathf.Clamp(SevOf(hd0) * 1000f, 0f, 999999f);
                keyed.Add(new KeyValuePair<long, Hediff>(Rank(hd0) * 1000000L + sev, hd0));
            }
            keyed.Sort((a, b) =>
            {
                int c = b.Key.CompareTo(a.Key);
                return c != 0 ? c : string.CompareOrdinal(
                    a.Value?.def?.defName ?? "", b.Value?.def?.defName ?? "");
            });
            for (int i = 0; i < keyed.Count; i++)
            {
                var hd = keyed[i].Value;
                if (hd == null) continue;
                // Visible is the player's own filter: an invisible hediff is
                // not on the health tab and must not be in the serializer.
                if (!(Safe(() => (object)hd.Visible) as bool? ?? true)) continue;

                bool tended = false;
                object tendQuality = null;
                var comp = (hd as HediffWithComps)?.TryGetComp<HediffComp_TendDuration>();
                if (comp != null)
                {
                    tended = Safe(() => (object)comp.IsTended) as bool? ?? false;
                    tendQuality = PawnSafe.R(comp.tendQuality, 2);
                }
                float bleed = Safe(() => (object)hd.BleedRate) as float? ?? 0f;
                var line = new Dictionary<string, object>
                {
                    ["def"] = hd.def?.defName,
                    ["label"] = Safe(() => hd.LabelCap),
                    // null part = whole body, which is how the health tab reads it.
                    ["part"] = Safe(() => hd.Part?.LabelCap),
                    ["severity"] = PawnSafe.R(SevOf(hd), 2),
                    ["bleeding"] = bleed > 0.0001f ? (object)PawnSafe.R(bleed, 2) : null,
                    ["tended"] = tended,
                    ["tend_quality"] = tendQuality,
                    ["permanent"] = Safe(() => (object)hd.IsPermanent()) as bool? ?? false,
                    ["life_threatening"] = Safe(() => (object)(hd.CurStage?.lifeThreatening ?? false)) as bool? ?? false,
                };
                var immunity = Safe(() => (object)pawn.health.immunity.GetImmunity(hd.def)) as float?;
                if (immunity.HasValue && immunity.Value > 0f) line["immunity_pct"] = PawnSafe.Pct(immunity.Value);
                hediffs.Add(line);
            }

            var caps = new List<object>();
            var capDefs = DefDatabase<PawnCapacityDef>.AllDefsListForReading;
            var capRows = new List<KeyValuePair<int, Dictionary<string, object>>>();
            for (int i = 0; i < capDefs.Count; i++)
            {
                var cd = capDefs[i];
                if (cd == null || !cd.CanShowOnPawn(pawn)) continue;
                float level;
                // The PURE function the health tab prints, not the cached level
                // it only uses to pick a colour.
                try { level = PawnCapacityUtility.CalculateCapacityLevel(h.hediffSet, cd); }
                catch { continue; }
                capRows.Add(new KeyValuePair<int, Dictionary<string, object>>(cd.listOrder,
                    new Dictionary<string, object>
                    {
                        ["def"] = cd.defName,
                        ["label"] = Safe(() => cd.GetLabelFor(pawn)),
                        ["pct"] = PawnSafe.Pct(level),
                    }));
            }
            capRows.Sort((a, b) =>
            {
                int c = a.Key.CompareTo(b.Key);
                return c != 0 ? c : string.CompareOrdinal(
                    (string)a.Value["def"] ?? "", (string)b.Value["def"] ?? "");
            });
            for (int i = 0; i < capRows.Count && i < CapacityCap; i++) caps.Add(capRows[i].Value);

            var d = new Dictionary<string, object>
            {
                ["summary_pct"] = SafeSummaryHealthPct(pawn),
                ["pain_pct"] = Safe(() => (object)PawnSafe.Pct(h.hediffSet.PainTotal)),
                ["bleed_rate"] = PawnSafe.R(h.hediffSet.BleedRateTotal, 2),
                ["needs_tend"] = SafeShouldTend(pawn),
                ["med_care"] = pawn.playerSettings?.medCare.ToString(),
                ["self_tend"] = pawn.playerSettings?.selfTend,
                ["capacities"] = caps,
                ["capacities_more"] = Math.Max(0, capRows.Count - caps.Count),
            };
            var capped = PawnSafe.Capped(hediffs, HediffCap, "urgency-desc");
            d["hediffs"] = capped["list"];
            d["hediffs_total"] = capped["total"];
            d["hediffs_more"] = capped["more"];
            return d;
        }

        private static float SevOf(Hediff hd)
        {
            try { return hd?.Severity ?? 0f; } catch { return 0f; }
        }

        // Urgency band for the hediff cap: bleeding first, then life-threatening,
        // then tendable, then everything else. The cap must never hide the wound.
        private static int Rank(Hediff hd)
        {
            if (hd == null) return -1;
            try
            {
                if (hd.BleedRate > 0.0001f) return 4;
                if (hd.CurStage != null && hd.CurStage.lifeThreatening) return 3;
                if (hd.TendableNow()) return 2;
                if (hd is Hediff_MissingPart) return 1;
            }
            catch { }
            return 0;
        }

        // ----------------------------- skills ------------------------------
        private static Dictionary<string, object> Skills(Pawn pawn)
        {
            var st = pawn.skills;
            if (st?.skills == null) return null;
            var rows = new List<object>();
            var all = st.skills;
            for (int i = 0; i < all.Count && i < SkillCap; i++)
            {
                var s = all[i];
                if (s?.def == null) continue;
                string disabled = null;
                bool perm = Safe(() => (object)s.PermanentlyDisabled) as bool? ?? false;
                if (perm) disabled = "permanent";
                else if (Safe(() => (object)s.TotallyDisabled) as bool? ?? false) disabled = "total";
                rows.Add(new Dictionary<string, object>
                {
                    ["def"] = s.def.defName,
                    ["level"] = Safe(() => (object)s.GetLevelForUI()) ?? s.levelInt,
                    ["level_raw"] = s.levelInt,
                    ["passion"] = s.passion.ToString(),
                    // xpSinceMidnight is a plain field: "xp today" for free.
                    ["xp_today"] = (int)Math.Round(s.xpSinceMidnight),
                    ["xp_pct"] = Safe(() => (object)PawnSafe.Pct(s.XpProgressPercent)),
                    ["saturated_today"] = Safe(() => (object)s.LearningSaturatedToday) ?? false,
                    ["disabled"] = disabled,
                });
            }
            return new Dictionary<string, object>
            {
                ["list"] = rows,
                ["total"] = all.Count,
                ["more"] = Math.Max(0, all.Count - rows.Count),
                ["order"] = "defdatabase",
            };
        }

        // ----------------------------- apparel -----------------------------
        // The clothes checklist feeds on this. Stat calls live here and nowhere
        // in the hot path — see PawnSafe Class F for why MaxHitPoints cannot
        // avoid writing a memo.
        private static Dictionary<string, object> Apparel(Pawn pawn)
        {
            var tracker = pawn.apparel;
            if (tracker == null) return null;
            // Snapshot: WornApparel is wornApparel.InnerListForReading, the LIVE
            // list. Nothing below removes apparel today, but a modded stat part
            // reached through GetStatValue is arbitrary code running mid-loop —
            // the same shape as the digest's live Collection-was-modified bug.
            var worn = new List<Apparel>(tracker.WornApparel);
            var rows = new List<object>();
            float insCold = 0f, insHeat = 0f;
            float worstRatio = 999f;
            for (int i = 0; i < worn.Count && i < ApparelCap; i++)
            {
                var a = worn[i];
                if (a?.def == null) continue;
                object hp = null, maxHp = null, hpPct = null;
                string wear = null;
                bool useHp = a.def.useHitPoints;
                bool locked = Safe(() => (object)tracker.IsLocked(a)) as bool? ?? false;
                bool cares = a.def.apparel?.careIfDamaged ?? true;
                if (useHp)
                {
                    int max = Safe(() => (object)a.MaxHitPoints) as int? ?? 0;
                    if (max > 0)
                    {
                        hp = a.HitPoints;
                        maxHp = max;
                        float ratio = (float)a.HitPoints / max;
                        hpPct = PawnSafe.Pct(ratio);
                        // The game's own thresholds, from
                        // RimWorld/ThoughtWorker_ApparelDamaged.cs, applied to
                        // the game's own subset (useHitPoints && !locked &&
                        // careIfDamaged) so `worst_wear` matches the mood
                        // penalty the pawn actually gets.
                        wear = ratio < 0.2f ? "tattered" : (ratio < 0.5f ? "frayed" : "good");
                        if (!locked && cares && ratio < worstRatio) worstRatio = ratio;
                    }
                }
                float ic = Safe(() => (object)a.GetStatValue(StatDefOf.Insulation_Cold)) as float? ?? 0f;
                float ih = Safe(() => (object)a.GetStatValue(StatDefOf.Insulation_Heat)) as float? ?? 0f;
                insCold += ic;
                insHeat += ih;
                string quality = null;
                if (a.TryGetQuality(out var qc)) quality = qc.ToString();
                var layers = new List<object>();
                if (a.def.apparel?.layers != null)
                    foreach (var l in a.def.apparel.layers) layers.Add(l.defName);
                rows.Add(new Dictionary<string, object>
                {
                    ["id"] = a.thingIDNumber,
                    ["def"] = a.def.defName,
                    ["label"] = Safe(() => a.LabelShort),
                    ["quality"] = quality,
                    // null, not a bogus ratio, when the def does not use hit
                    // points: HitPoints is the uninitialised -1 there.
                    ["hp"] = hp,
                    ["max_hp"] = maxHp,
                    ["hp_pct"] = hpPct,
                    ["wear"] = wear,
                    ["insulation_cold"] = PawnSafe.R(ic, 1),
                    ["insulation_heat"] = PawnSafe.R(ih, 1),
                    ["layers"] = layers,
                    ["locked"] = locked,
                    ["cares_if_damaged"] = cares,
                });
            }
            return new Dictionary<string, object>
            {
                ["worn"] = rows,
                ["total"] = worn.Count,
                ["more"] = Math.Max(0, worn.Count - rows.Count),
                ["insulation_cold_total"] = PawnSafe.R(insCold, 1),
                ["insulation_heat_total"] = PawnSafe.R(insHeat, 1),
                // The pawn's real comfortable band, which folds in apparel,
                // hediffs, genes and traits — not the naive sum above. Both are
                // published because they answer different questions: the totals
                // say "which coat", the band says "will they freeze".
                ["comfort_min_c"] = Safe(() => (object)PawnSafe.R(pawn.GetStatValue(StatDefOf.ComfyTemperatureMin), 1)),
                ["comfort_max_c"] = Safe(() => (object)PawnSafe.R(pawn.GetStatValue(StatDefOf.ComfyTemperatureMax), 1)),
                ["worst_hp_pct"] = worstRatio < 999f ? (object)PawnSafe.Pct(worstRatio) : null,
                ["worst_wear"] = worstRatio < 999f
                    ? (worstRatio < 0.2f ? "tattered" : (worstRatio < 0.5f ? "frayed" : "good"))
                    : null,
            };
        }

        private static Dictionary<string, object> Equipment(Pawn pawn)
        {
            var eq = pawn.equipment;
            if (eq == null) return null;
            // Snapshot, same reason as WornApparel above.
            var list = new List<ThingWithComps>(eq.AllEquipmentListForReading);
            var rows = new List<object>();
            for (int i = 0; i < list.Count && i < EquipmentCap; i++)
            {
                var t = list[i];
                if (t?.def == null) continue;
                string quality = null;
                if (t.TryGetQuality(out var qc)) quality = qc.ToString();
                object hpPct = null;
                if (t.def.useHitPoints)
                {
                    int max = Safe(() => (object)t.MaxHitPoints) as int? ?? 0;
                    if (max > 0) hpPct = PawnSafe.Pct((float)t.HitPoints / max);
                }
                rows.Add(new Dictionary<string, object>
                {
                    ["id"] = t.thingIDNumber,
                    ["def"] = t.def.defName,
                    ["label"] = Safe(() => t.LabelShort),
                    ["quality"] = quality,
                    ["hp_pct"] = hpPct,
                    ["primary"] = t == eq.Primary,
                });
            }
            return new Dictionary<string, object>
            {
                ["list"] = rows,
                ["total"] = list.Count,
                ["more"] = Math.Max(0, list.Count - rows.Count),
            };
        }

        private static Dictionary<string, object> Inventory(Pawn pawn)
        {
            var inv = pawn.inventory;
            if (inv?.innerContainer == null) return null;
            var rows = new List<object>();
            int total = inv.innerContainer.Count;
            int i = 0;
            foreach (var t in inv.innerContainer)
            {
                if (i++ >= InventoryCap) break;
                if (t?.def == null) continue;
                rows.Add(new Dictionary<string, object>
                {
                    ["id"] = t.thingIDNumber,
                    ["def"] = t.def.defName,
                    ["label"] = Safe(() => t.LabelShort),
                    ["count"] = t.stackCount,
                });
            }
            return new Dictionary<string, object>
            {
                ["list"] = rows,
                ["total"] = total,
                ["more"] = Math.Max(0, total - rows.Count),
            };
        }

        // ---------------------------- schedule -----------------------------
        // `times` is the LIVE 24-entry list (RimWorld/Pawn_TimetableTracker.cs)
        // and GetAssignment has an unguarded index, so the hour bound is checked
        // here rather than trusted. The tracker is null for anything not
        // player-faction humanlike.
        //
        // `row` is a 24-character string with a legend rather than 24 defNames:
        // ~24 bytes instead of ~350, and the legend is a FIXED table so a
        // character means the same thing on every pawn (a legend derived from
        // the defs a given pawn happens to use would not be a contract).
        // TimeAssignmentDefOf.Meditate does not exist without Royalty; the
        // tracker scrubs it to Anything on load, so it simply never appears.
        private static readonly Dictionary<string, char> ScheduleChars = new Dictionary<string, char>
        {
            ["Anything"] = 'A',
            ["Work"] = 'W',
            ["Joy"] = 'J',
            ["Sleep"] = 'S',
            ["Meditate"] = 'M',
        };

        private static Dictionary<string, object> Schedule(Pawn pawn)
        {
            var tt = pawn.timetable;
            if (tt?.times == null || tt.times.Count < 24) return null;
            var chars = new char[24];
            var legend = new Dictionary<string, object>();
            var unmapped = new List<object>();
            for (int h = 0; h < 24; h++)
            {
                var def = tt.times[h];
                string name = def?.defName ?? "Anything";
                char c;
                if (!ScheduleChars.TryGetValue(name, out c))
                {
                    c = '?';
                    if (!unmapped.Contains(name)) unmapped.Add(name);
                }
                chars[h] = c;
                legend[c.ToString()] = c == '?' ? "unmapped" : (object)name;
            }
            int hour = 0;
            try { hour = GenLocalDate.HourOfDay(pawn); } catch { }
            if (hour < 0 || hour > 23) hour = 0;
            var d = new Dictionary<string, object>
            {
                ["row"] = new string(chars),
                ["legend"] = legend,
                ["hour"] = hour,
                ["now"] = tt.times[hour]?.defName,
            };
            if (unmapped.Count > 0) d["unmapped"] = unmapped;
            return d;
        }

        // ------------------------------ work -------------------------------
        // THE guarded section. See PawnSafe Class B: an ungated GetPriority on a
        // pawn that never had work settings logs a red error AND writes the
        // pawn's priorities. EverWork is `priorities != null`, which is exactly
        // the gate PawnColumnWorker_WorkPriority and JobGiver_Work use.
        private static Dictionary<string, object> WorkRow(Pawn pawn)
        {
            var ws = pawn.workSettings;
            if (ws == null || !ws.EverWork)
                return new Dictionary<string, object>
                {
                    ["initialized"] = false,
                    // Say why nothing was read, so "no work row" is never
                    // mistaken for "all priorities zero".
                    ["note"] = "pawn has no work settings; reading one would create them",
                };

            bool usePriorities = true;
            try { usePriorities = Find.PlaySettings.useWorkPriorities; } catch { }

            var row = new List<object>();
            var disabled = new List<object>();
            int considered = 0;
            // The Work tab's own column set and order: naturalPriority
            // descending, filtered on the plain `def.visible` field.
            // VisibleCurrently writes a frame cache and walks PawnsFinder.
            foreach (var def in WorkTypeDefsUtility.WorkTypeDefsInPriorityOrder)
            {
                if (def == null || !def.visible) continue;
                considered++;
                if (row.Count >= WorkCap) continue;
                bool off;
                try { off = pawn.WorkTypeIsDisabled(def); } catch { off = true; }
                if (off) { disabled.Add(def.defName); continue; }
                int prio;
                try { prio = ws.GetPriority(def); } catch { continue; }
                row.Add(new Dictionary<string, object>
                {
                    ["work"] = def.defName,
                    ["priority"] = prio,
                });
            }
            return new Dictionary<string, object>
            {
                ["initialized"] = true,
                // Read this WITH the row: with priorities off, GetPriority
                // returns a flat 3 for every active work type and the numbers
                // below are not the stored numbers.
                ["use_priorities"] = usePriorities,
                ["row"] = row,
                ["disabled"] = disabled,
                ["total"] = considered,
                ["more"] = Math.Max(0, considered - row.Count - disabled.Count),
                ["order"] = "natural-priority-desc",
            };
        }

        // ------------------------------ area -------------------------------
        private static Dictionary<string, object> Area(Pawn pawn)
        {
            var ps = pawn.playerSettings;
            if (ps == null) return null;
            return new Dictionary<string, object>
            {
                // The Restrict tab's own words, via the NULL-GUARDED area getter
                // (Verse/AreaUtility.cs -> AreaRestrictionInPawnCurrentMap). The
                // Effective variant throws for a pawn with no MapHeld.
                ["label"] = Safe(() => AreaUtility.AreaAllowedLabel(pawn)),
                ["area"] = Safe(() => ps.AreaRestrictionInPawnCurrentMap?.Label),
                ["respects"] = Safe(() => (object)ps.RespectsAllowedArea) ?? false,
                ["hostility_response"] = ps.hostilityResponse.ToString(),
                ["follow_drafted"] = ps.followDrafted,
                ["follow_fieldwork"] = ps.followFieldwork,
                ["animals_released"] = ps.animalsReleased,
                ["join_tick"] = ps.joinTick,
                ["master"] = Safe(() => ps.Master != null ? (object)ps.Master.thingIDNumber : null),
            };
        }

        // --------------------------- relations -----------------------------
        // Default: DirectRelations only — a plain backing list, no graph walk,
        // no cache entry created. That covers spouse/lover/parent/child/sibling
        // and animal bonds, which is what "colony-relevant" means in practice.
        //
        // Opinions are OPT-IN because OpinionOf is not a read: it allocates
        // situational social-thought cache entries per pair and runs a family
        // BFS per call (PawnSafe Class C). When asked for, the number of pairs
        // perturbed is bounded and PUBLISHED, so the disclosure travels with
        // the data.
        private static Dictionary<string, object> Relations(Pawn pawn, Map map, bool opinions, int cap)
        {
            var rt = pawn.relations;
            if (rt == null) return null;

            var direct = new List<object>();
            int directTotal = 0;
            var src = rt.DirectRelations;
            if (src != null)
            {
                // Snapshot before iterating: nothing below re-enters it today,
                // but this is the discipline the digest's own live bug bought.
                var snapshot = new List<DirectPawnRelation>(src);
                directTotal = snapshot.Count;
                var scored = new List<KeyValuePair<float, Dictionary<string, object>>>();
                for (int i = 0; i < snapshot.Count; i++)
                {
                    var r = snapshot[i];
                    var other = r?.otherPawn;
                    if (other == null || r.def == null) continue;
                    bool onMap = other.Spawned && other.Map == map && !PawnSafe.Hidden(other, map);
                    scored.Add(new KeyValuePair<float, Dictionary<string, object>>(
                        r.def.importance + (onMap ? 1000f : 0f),
                        new Dictionary<string, object>
                        {
                            ["id"] = other.thingIDNumber,
                            ["name"] = PawnSafe.Name(other),
                            ["relation"] = r.def.defName,
                            ["label"] = Safe(() => r.def.GetGenderSpecificLabelCap(other)),
                            ["class"] = PawnSafe.Classify(other),
                            ["on_map"] = onMap,
                            ["dead"] = other.Dead,
                        }));
                }
                // On-map and important first: a spouse standing in the room
                // outranks a cousin in another world.
                scored.Sort((a, b) =>
                {
                    int c = b.Key.CompareTo(a.Key);
                    return c != 0 ? c : ((int)a.Value["id"]).CompareTo((int)b.Value["id"]);
                });
                for (int i = 0; i < scored.Count && i < RelationCap; i++) direct.Add(scored[i].Value);
            }

            var d = new Dictionary<string, object>
            {
                ["scope"] = opinions ? "direct+opinions" : "direct-only",
                ["direct"] = direct,
                ["direct_total"] = directTotal,
                ["direct_more"] = Math.Max(0, directTotal - direct.Count),
            };

            var op = new Dictionary<string, object>
            {
                ["enabled"] = opinions,
                ["cap"] = cap,
                ["queried"] = 0,
                // The disclosure travels with the data, not only in a comment.
                ["note"] = "OpinionOf allocates a situational social-thought cache entry per pair "
                    + "and runs a family-graph walk per call; off by default",
            };
            d["opinions"] = op;

            if (opinions && cap > 0 && pawn.RaceProps != null && pawn.RaceProps.Humanlike)
            {
                // Deterministic query set: spawned, visible, humanlike colony
                // members other than self, ordered by thingIDNumber so the SAME
                // pairs are perturbed on every call rather than a fresh set each
                // time. That bounds the total damage across a session, which a
                // per-call cap alone does not.
                var pool = new List<Pawn>();
                var all = new List<Pawn>(map.mapPawns.AllPawnsSpawned);
                for (int i = 0; i < all.Count; i++)
                {
                    var o = all[i];
                    if (o == null || o == pawn || o.Dead) continue;
                    if (o.RaceProps == null || !o.RaceProps.Humanlike) continue;
                    if (PawnSafe.Hidden(o, map)) continue;
                    string c = PawnSafe.Classify(o);
                    if (c != PawnSafe.ClassColonist && c != PawnSafe.ClassPrisoner
                        && c != PawnSafe.ClassSlave && c != PawnSafe.ClassGuest) continue;
                    pool.Add(o);
                }
                pool.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));

                var rows = new List<object>();
                Dictionary<string, object> best = null, worst = null;
                int bestV = int.MinValue, worstV = int.MaxValue;
                int queried = 0;
                for (int i = 0; i < pool.Count && queried < cap; i++)
                {
                    int val;
                    try { val = rt.OpinionOf(pool[i]); }
                    catch { continue; }
                    queried++;
                    var line = new Dictionary<string, object>
                    {
                        ["id"] = pool[i].thingIDNumber,
                        ["name"] = PawnSafe.Name(pool[i]),
                        ["opinion"] = val,
                    };
                    rows.Add(line);
                    if (val > bestV) { bestV = val; best = line; }
                    if (val < worstV) { worstV = val; worst = line; }
                }
                op["queried"] = queried;
                op["pool"] = pool.Count;
                op["skipped"] = Math.Max(0, pool.Count - queried);
                d["social"] = rows;
                // The "bonds and rivals" the spec asks for, without making the
                // caller scan: the extremes of what was actually queried.
                d["best"] = best;
                d["worst"] = worst;
            }
            return d;
        }

        // ---------------------------- helpers ------------------------------

        private static int Cmp(Dictionary<string, object> a, Dictionary<string, object> b, string num, string tie)
        {
            int av = a.TryGetValue(num, out var x) && x is int xi ? xi : 0;
            int bv = b.TryGetValue(num, out var y) && y is int yi ? yi : 0;
            int c = av.CompareTo(bv);
            if (c != 0) return c;
            return string.CompareOrdinal(a.TryGetValue(tie, out var p) ? p as string ?? "" : "",
                                         b.TryGetValue(tie, out var q) ? q as string ?? "" : "");
        }

        // A modded getter that throws must degrade one FIELD to null, not fail
        // the whole verb. Deliberately silent: with 32 mods on the bench a
        // per-field journal line would be a storm, and the null is visible in
        // the output. The two failures worth naming (job report, mood thoughts)
        // journal explicitly at their call sites.
        private static T Safe<T>(Func<T> f) where T : class
        {
            try { return f(); } catch { return null; }
        }

        private static object Safe(Func<object> f)
        {
            try { return f(); } catch { return null; }
        }

        private static object SafeSummaryHealthPct(Pawn pawn)
        {
            try { return PawnSafe.Pct(pawn.health.summaryHealth.SummaryHealthPercent); }
            catch { return null; }
        }

        private static bool SafeShouldTend(Pawn pawn)
        {
            try { return HealthAIUtility.ShouldBeTendedNowByPlayer(pawn); }
            catch { return false; }
        }
    }
}
