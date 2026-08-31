using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // `things` and `fires` (spec 2.4). Rollup-first: the player does not read a
    // list of 412 steel stacks, they read "steel 3,410 in 12 stacks", and an LLM
    // must not be handed the former. Per-thing detail is behind `detail:true`.
    //
    // Read-only throughout; WorldSafe holds the hazard catalogue and the guarded
    // routes, and nothing here calls a raw accessor it warns about.
    //
    // FOG OF WAR (DESIGN decisions log 2026-08-30): a thing standing in ground
    // the colony has never explored is not reported, and the count hidden is
    // published as `skipped.fogged` so "no medicine" and "no medicine you have
    // found" stay distinguishable — the shape 2.3's `nearest` established.
    //
    // ===================== TRUNCATION IS A CONTRACT =========================
    // Rollups are capped and ORDERED BY ATTENTION BEFORE THE CUT: a def with
    // forbidden or deteriorating stock outranks a bigger pile of steel, because
    // the cap must never hide the problem behind the inventory. `total`/`more`
    // are published, as everywhere.
    //
    // ================= ORDER IS A CONTRACT (git-bug 70ac258) ================
    // The DETAIL list (`detail:true`) and the FIRE list are ADDRESSABLE — every
    // line carries a `thingIDNumber` a caller can and will hold — so each
    // publishes TWO order facts, because they are two different questions:
    //
    //   `selected_by` — WHICH entries survive the cap. Not negotiable and not
    //                   changed here: that is 2.6's rule. A cap that cut in
    //                   list order would hide the forbidden stack behind the
    //                   steel, and the fire outside the home area behind the
    //                   one that already has an alert.
    //   `order`       — the order the survivors are EMITTED in. Caller's
    //                   choice via `order:` (`id`|`attention`), default
    //                   `id-asc`.
    //
    // The default is `id-asc` because both selection scores MOVE AT READ TIME,
    // with nothing having happened that the caller did:
    //   * `ThingAttention` = 100000*forbidden + max(0, 100-hp_pct)*100 +
    //     min(stackCount, 1000). Deterioration ticks hit points down on
    //     anything unroofed, so two stacks a percentage point apart trade
    //     places; and a hauler merging or splitting a stack moves `stackCount`
    //     with no player action at all.
    //   * a fire's rank is `fireSize + (outside home ? 10 : 0)`, and
    //     `RimWorld/Fire.cs` `DoComplexCalcs` runs
    //     `fireSize += 0.00055f * flammabilityMax * 150f * (1f - vacuum)`
    //     until it saturates at 1.75 — it moves FASTER than hit points or mood.
    // Under those orders the lists are valid rankings and worthless handles:
    // `list[0]` names a different thing on consecutive reads with nothing
    // wrong. That is what cost `pawns` six of spec 3.4's acceptance checks
    // (git-bug 1eb2262); this is the same defect in the same shape and takes
    // the same fix. `thingIDNumber` is the key because it is stable for the
    // thing's lifetime and is already the id these lines publish.
    //
    // Urgency is not lost by reordering: every line carries `attention_rank`,
    // the 0-based position it held under the SELECTION ranking, computed BEFORE
    // the cut. For fires that ranking is not attention at all, which is why
    // `selected_by` there states its real rule, `outside-home-then-size-desc`,
    // and why the `order` it publishes for the ranked view says the same words
    // — one sequence must not be given two names.
    //
    // `rollups` is deliberately NOT changed (git-bug 70ac258 ruling): it is
    // keyed by `def`, which is already a stable key, it has no `thingIDNumber`
    // to sort by, and its position IS the message.
    //
    // THE LIMIT OF THE PROMISE, stated because a half-true contract is worse
    // than none: `order:"id"` stabilises the SEQUENCE, not the MEMBERSHIP. The
    // cap still cuts by the live score — it must, that is 2.6 — so on a list
    // longer than `detail_cap` (or than the 12 fires) the surviving SET moves
    // as the score moves, and `list[0]` can still name a different thing
    // between reads. `things_more` / `more` is the flag for exactly that: when
    // it is non-zero, position is reproducible only among the survivors, and a
    // caller wanting a durable handle raises the cap past the total or holds
    // the `id` it read rather than the index it read it at. When it is zero the
    // sequence is a register.
    //
    // ========================= FIELD DOCS ==================================
    //
    // `forbidden` / `forbidden_stacks` exist because of the session-4 amendment
    // and they are not a curiosity: ScenPart_PlayerPawnsArriveMethod.DoDropPods
    // calls DropThingGroupsNear(..., forbid: true, ...), so a DROP-POD START
    // LANDS WITH ITS OWN GEAR FORBIDDEN — which is why the scripted tutorial's
    // second action is UnforbidStartingResources. Without this field "why is
    // nothing being hauled" has no diagnostic path at all. The test is
    // Thing.IsForbidden(Faction.OfPlayer), which reads ThingWithComps
    // .compForbiddable directly (RimWorld/ForbidUtility.cs) — a field read.
    //
    // `roofed` / `unroofed` / `at_risk` are the deterioration signal, same
    // amendment. There is no vanilla alert for deterioration — all 132 checked —
    // so items rotting in the rain outdoors are silent attrition. `at_risk` is
    // unroofed stacks of a def that CAN deteriorate from environmental effects
    // (Verse/ThingDef.CanEverDeteriorate && deteriorateFromEnvironmentalEffects
    // && a non-zero abstract DeteriorationRate), which is the def half of
    // SteadyEnvironmentEffects.FinalDeteriorationRate. The per-thing half of
    // that function is exactly "is this cell roofed", which is the grid read
    // here — so `at_risk` is the game's own condition, not an approximation of
    // it, minus the weather and terrain multipliers that only change the rate.
    //
    // `hp_min_pct`/`hp_max_pct` are omitted for a def with !useHitPoints: its
    // HitPoints field is the uninitialised -1 (Verse/Thing.cs) while
    // MaxHitPoints still returns a plausible stat, so the ratio would be
    // silently wrong (2.2's lesson, same words). `hp_source:"memo"` says the
    // max is memoised per (def, stuff, quality) rather than asked per thing —
    // see WorldSafe.MaxHpMemo for why.
    //
    // `by_location` is the spec's "apparel-in-stockpile vs worn is a first-class
    // view", in ONE call: the same rollup builder run over three disjoint pools
    // (stockpiled / worn / loose). Default on for `category:"apparel"`, off
    // otherwise, and available on any query.
    //
    // Single-map by design (v1).
    public static class ThingVerbs
    {
        public const int RollupCap = 20;
        public const int DetailCap = 30;
        public const int StuffCap = 4;
        // A whole-map `category:"all"` can cross six figures of things on a
        // stirred map. The walk stops there and says so rather than spending a
        // frame budget inside a verb that is documented as cheap.
        public const int ExamineCap = 40000;

        // The `order:` vocabulary, identical to PawnVerbs' by design — a caller
        // that learned it on `pawns` must not have to learn it again here. Note
        // that the ARGUMENT words (`id`, `attention`) and the PUBLISHED values
        // (`id-asc`, `attention-desc`) deliberately differ: the argument names
        // the key, the published value names the whole ordering.
        private const string OrderById = "id";
        private const string OrderByAttention = "attention";
        private const string OrderWords = "id|attention";

        // The taxonomy (open question 1). Twelve of these fourteen words are a
        // ThingRequestGroup — the game's OWN membership test, already
        // maintained as a list by ListerThings, and the same one 2.3's
        // `nearest --category` uses for the four words it shares. The two that
        // are not have a named ThingDef property instead. Nothing here is a
        // bucket we invented, which is the whole point: `category_source` is
        // published so a consumer can read the definition rather than guess it.
        private static bool Pool(Map map, string category, out List<Thing> pool, out string source)
        {
            ThingRequestGroup group;
            Func<ThingDef, bool> extra = null;
            switch (category)
            {
                case "food": group = ThingRequestGroup.FoodSourceNotPlantOrTree; source = "group:FoodSourceNotPlantOrTree"; break;
                case "meds": group = ThingRequestGroup.Medicine; source = "group:Medicine"; break;
                case "apparel": group = ThingRequestGroup.Apparel; source = "group:Apparel"; break;
                case "weapons": group = ThingRequestGroup.Weapon; source = "group:Weapon"; break;
                case "drugs": group = ThingRequestGroup.Drug; source = "group:Drug"; break;
                case "corpses": group = ThingRequestGroup.Corpse; source = "group:Corpse"; break;
                case "chunks": group = ThingRequestGroup.Chunk; source = "group:Chunk"; break;
                case "art": group = ThingRequestGroup.Art; source = "group:Art"; break;
                case "plants": group = ThingRequestGroup.Plant; source = "group:Plant"; break;
                case "beds": group = ThingRequestGroup.Bed; source = "group:Bed"; break;
                case "buildings": group = ThingRequestGroup.BuildingArtificial; source = "group:BuildingArtificial"; break;
                case "haulable": group = ThingRequestGroup.HaulableEver; source = "group:HaulableEver"; break;
                case "all": group = ThingRequestGroup.Everything; source = "group:Everything"; break;
                case "resources":
                    // No group exists; ThingDef.CountAsResource is
                    // `resourceReadoutPriority != Uncounted` — the top-left
                    // resource readout's own definition of a resource.
                    group = ThingRequestGroup.HaulableEver;
                    extra = d => d.CountAsResource;
                    source = "group:HaulableEver + ThingDef.CountAsResource";
                    break;
                default:
                    pool = null;
                    source = null;
                    return false;
            }
            var raw = map.listerThings.ThingsInGroup(group);
            // Snapshot: the lister's group lists are the REAL backing lists, and
            // the loop below reaches def properties and stat calls that mods can
            // extend. 2.1's live Collection-was-modified bug is the standing
            // reason (DigestVerb.ColonistSection).
            pool = new List<Thing>(raw);
            if (extra != null)
            {
                var kept = new List<Thing>();
                for (int i = 0; i < pool.Count; i++)
                    if (pool[i]?.def != null && extra(pool[i].def)) kept.Add(pool[i]);
                pool = kept;
            }
            return true;
        }

        public const string CategoryWords =
            "food|meds|apparel|weapons|drugs|corpses|chunks|art|plants|beds|buildings|resources|haulable|all";

        [Verb("things")]
        public static object Things(VerbContext ctx)
        {
            var map = WorldSafe.CurrentMap();
            string defName = ctx.Args.Str("def");
            string category = ctx.Args.Str("category");
            if (defName != null && category != null)
                throw new VerbArgsException("'def' and 'category' are exclusive");
            string where = ctx.Args.Str("in", "map");
            bool detail = ctx.Args.Bool("detail", false);
            int cap = ctx.Args.Int("cap", RollupCap);
            if (cap < 1 || cap > 200) throw new VerbArgsException("cap must be 1..200");
            int detailCap = ctx.Args.Int("detail_cap", DetailCap);
            if (detailCap < 1 || detailCap > 300) throw new VerbArgsException("detail_cap must be 1..300");
            // Governs the EMIT order of both addressable lists this verb
            // returns — the detail list and the always-present fire list — so
            // it is meaningful even with `detail:false`. Unknown order is a
            // bad-args, not a silent fallback: a typo that quietly returns the
            // OTHER order is the worst outcome, since the caller asked
            // precisely because it cares which one it gets (PawnVerbs, same
            // words). `rollups` is unaffected; see the class comment.
            string order = ctx.Args.Str("order", OrderById);
            if (order != OrderById && order != OrderByAttention)
                throw new VerbArgsException($"unknown order '{order}' ({OrderWords})");

            List<Thing> pool;
            string source;
            if (defName != null)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName)
                    ?? throw new VerbArgsException($"no ThingDef named '{defName}'");
                // ListerThings.ThingsOfDef Log.ErrorOnce's on MinifiedThing and
                // names the group to use instead; a red error raised by
                // agent-supplied args breaches the zero-red-errors invariant, so
                // take the route the game itself names (2.3's `nearest` does the
                // same and for the same reason).
                pool = def == ThingDefOf.MinifiedThing
                    ? new List<Thing>(map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing))
                    : new List<Thing>(map.listerThings.ThingsOfDef(def));
                source = "def:" + def.defName;
            }
            else
            {
                string cat = category ?? "haulable";
                if (!Pool(map, cat, out pool, out source))
                    throw new VerbArgsException($"unknown category '{cat}' ({CategoryWords})");
                category = cat;
            }

            var scope = Scope.Resolve(map, where, ctx.Args);
            bool byLocation = ctx.Args.Bool("by_location", category == "apparel" && where == "map");

            var data = new Dictionary<string, object>
            {
                ["query"] = new Dictionary<string, object>
                {
                    ["def"] = defName,
                    ["category"] = defName == null ? category : null,
                    ["category_source"] = source,
                    ["in"] = scope.Label,
                    ["scope"] = scope.Detail,
                },
            };

            if (byLocation)
            {
                // Three disjoint pools, one builder. `worn` is not in the thing
                // lister at all (apparel on a pawn is held, not spawned), so it
                // is gathered from the pawns.
                var stockpiled = new List<Thing>();
                var loose = new List<Thing>();
                int skippedFogged = 0, skippedScope = 0, examined = 0;
                for (int i = 0; i < pool.Count && examined < ExamineCap; i++)
                {
                    var t = pool[i];
                    examined++;
                    if (t?.def == null) continue;
                    if (WorldSafe.Hidden(t, map)) { skippedFogged++; continue; }
                    if (!scope.Contains(t)) { skippedScope++; continue; }
                    bool stored;
                    try { stored = t.Position.GetSlotGroup(map) != null; }
                    catch { stored = false; }
                    (stored ? stockpiled : loose).Add(t);
                }
                data["by_location"] = new Dictionary<string, object>
                {
                    ["stockpiled"] = Rollups(map, stockpiled, cap, detail, detailCap, order),
                    ["worn"] = Rollups(map, WornPool(map, WornMatch(defName, category)), cap, detail, detailCap, order),
                    ["loose"] = Rollups(map, loose, cap, detail, detailCap, order),
                    ["note"] = "stockpiled = in a slot group; loose = on the ground outside one; "
                        + "worn = on a colonist's back (never in the thing lister)",
                };
                data["skipped"] = new Dictionary<string, object>
                {
                    ["fogged"] = skippedFogged,
                    ["out_of_scope"] = skippedScope,
                };
            }
            else
            {
                var kept = new List<Thing>();
                int skippedFogged = 0, skippedScope = 0, examined = 0;
                bool capped = false;
                if (scope.Kind == "worn")
                {
                    kept = WornPool(map, WornMatch(defName, category));
                }
                else
                {
                    for (int i = 0; i < pool.Count; i++)
                    {
                        if (examined >= ExamineCap) { capped = true; break; }
                        var t = pool[i];
                        examined++;
                        if (t?.def == null) continue;
                        if (WorldSafe.Hidden(t, map)) { skippedFogged++; continue; }
                        if (!scope.Contains(t)) { skippedScope++; continue; }
                        kept.Add(t);
                    }
                }
                var roll = Rollups(map, kept, cap, detail, detailCap, order);
                foreach (var kv in roll) data[kv.Key] = kv.Value;
                data["skipped"] = new Dictionary<string, object>
                {
                    // removal "none", reason "unexplored": a fogged thing is not
                    // blocked, it is simply not known to the colony.
                    ["fogged"] = skippedFogged,
                    ["out_of_scope"] = skippedScope,
                };
                // The candidate set before fog and scope. For in:"worn" the
                // ground lister was never consulted, so it is the carried set.
                data["pool"] = scope.Kind == "worn" ? kept.Count : pool.Count;
                if (capped) data["examine_capped"] = ExamineCap;
            }

            // ALWAYS PRESENT and deliberately NOT filtered by `in`, `def` or
            // `category`: session-4 amendment 2. Alert_FireInHomeArea is scoped
            // to areaManager.Home, and no other vanilla alert covers fire, so a
            // fire on unclaimed ground is a total blind spot in the alert
            // readout the digest passes through verbatim. Any `things` call
            // closes it; `fires` answers it on its own for a cheap poll.
            data["fire"] = FireScan(map, order);
            return data;
        }

        // The map-level fire scan, independent of the alert readout.
        //
        // fires {order?} — the SECOND route into FireScan. The fix for the
        // addressable-list-ordered-by-a-live-score defect has to land on both
        // of them or the same list answers to two different contracts
        // depending on which verb asked (git-bug 70ac258 ruling, point 1).
        [Verb("fires")]
        public static object Fires(VerbContext ctx)
        {
            var map = WorldSafe.CurrentMap();
            string order = ctx.Args.Str("order", OrderById);
            if (order != OrderById && order != OrderByAttention)
                throw new VerbArgsException($"unknown order '{order}' ({OrderWords})");
            return FireScan(map, order);
        }

        private const int FireCap = 12;

        // The fires' selection rule, published verbatim as `selected_by` — and
        // as `order` too when the caller asks for that view, because the same
        // sequence must not be given two names. It is NOT "attention": no
        // ThingAttention is computed here, and calling it that would be a lie
        // that reads as data.
        private const string FireRank = "outside-home-then-size-desc";

        private static Dictionary<string, object> FireScan(Map map, string order)
        {
            var list = new List<object>();
            int total = 0, inHome = 0, outsideHome = 0, fogged = 0;
            float biggest = 0f;
            try
            {
                // ThingRequestGroup.Fire is `typeof(Fire).IsAssignableFrom(
                // def.thingClass)` (Verse/ThingListGroupHelper.cs), so it catches
                // a modded fire def too — Alert_FireInHomeArea's own
                // ThingsOfDef(ThingDefOf.Fire) would not.
                var fires = new List<Thing>(map.listerThings.ThingsInGroup(ThingRequestGroup.Fire));
                var home = map.areaManager?.Home;
                var scored = new List<KeyValuePair<float, Dictionary<string, object>>>();
                for (int i = 0; i < fires.Count; i++)
                {
                    var t = fires[i];
                    if (t == null || !t.Spawned) continue;
                    total++;
                    bool isFogged;
                    try { isFogged = t.Position.Fogged(map); }
                    catch { isFogged = true; }
                    // Fog: the same rule as everywhere. Vanilla's own alert
                    // excludes fogged fires too.
                    if (isFogged) { fogged++; continue; }
                    bool atHome = false;
                    try { atHome = home != null && home[t.Position]; }
                    catch { }
                    if (atHome) inHome++; else outsideHome++;
                    float size = 0f;
                    try { size = (t as Fire)?.fireSize ?? 0f; }
                    catch { }
                    if (size > biggest) biggest = size;
                    scored.Add(new KeyValuePair<float, Dictionary<string, object>>(
                        // Ordered before the cut: biggest first, and a fire
                        // OUTSIDE the home area outranks one inside it at the
                        // same size, because the inside one already has an alert.
                        size + (atHome ? 0f : 10f),
                        new Dictionary<string, object>
                        {
                            ["id"] = t.thingIDNumber,
                            ["at"] = Positions.Out(t.Position),
                            ["size"] = WorldSafe.R(size, 2),
                            ["in_home_area"] = atHome,
                            ["attached_to"] = WorldSafe.Safe(() => (t as AttachableThing)?.parent?.LabelShort),
                        }));
                }
                // SELECTION. Ranked before the cut, tie-broken on id so the
                // ranking itself is deterministic between reads that share a
                // score.
                scored.Sort((a, b) =>
                {
                    int c = b.Key.CompareTo(a.Key);
                    return c != 0 ? c : ((int)a.Value["id"]).CompareTo((int)b.Value["id"]);
                });
                var kept = new List<Dictionary<string, object>>();
                for (int i = 0; i < scored.Count && i < FireCap; i++)
                {
                    // The urgency the id order no longer encodes, carried on
                    // the line. 0 is the fire to send someone to first.
                    scored[i].Value["attention_rank"] = i;
                    kept.Add(scored[i].Value);
                }

                // PRESENTATION. Survivors only — re-sorting before the cut
                // would make the cap keep the twelve LOWEST-ID fires and drop
                // the one raging outside the home area, which is precisely the
                // failure 2.6 forbids.
                if (order == OrderById)
                    kept.Sort((a, b) => ((int)a["id"]).CompareTo((int)b["id"]));
                for (int i = 0; i < kept.Count; i++) list.Add(kept[i]);
            }
            catch (Exception e)
            {
                Journal.EmitWarning("things: fire scan threw: " + e.Message);
            }
            return new Dictionary<string, object>
            {
                ["count"] = total - fogged,
                ["in_home_area"] = inHome,
                ["outside_home_area"] = outsideHome,
                ["fogged"] = fogged,
                ["biggest_size"] = WorldSafe.R(biggest, 2),
                ["list"] = list,
                ["more"] = Math.Max(0, (total - fogged) - list.Count),
                // Two facts, not one (git-bug 70ac258). `order` is what
                // position means; `selected_by` is what the cap kept. A
                // consumer that conflates them reads a register as a ranking.
                // `more > 0` is the flag that even the emitted SET is moving:
                // above the cap, membership follows fireSize by design.
                ["order"] = order == OrderById ? "id-asc" : FireRank,
                ["selected_by"] = FireRank,
                // Say what this is NOT, because the digest's alert section is a
                // verbatim readout passthrough and looks like it covers this.
                ["note"] = "map-wide scan, independent of Alert_FireInHomeArea "
                    + "(which only covers the home area)",
            };
        }

        // What a query means for a thing a pawn is CARRYING. The thing lister
        // holds only spawned things, so worn apparel, equipped weapons and
        // inventory are invisible to a group read and have to be matched by def
        // instead. Derived from the QUERY, never from what happens to be lying
        // on the ground — an empty ground pool must not silently widen the worn
        // pool to everything.
        private static Func<ThingDef, bool> WornMatch(string defName, string category)
        {
            if (defName != null) return d => d != null && d.defName == defName;
            switch (category)
            {
                case "apparel": return d => d != null && d.IsApparel;
                case "weapons": return d => d != null && d.IsWeapon;
                case "drugs": return d => d != null && d.IsDrug;
                case "meds": return d => d != null && d.IsMedicine;
                case "food": return d => d != null && d.IsNutritionGivingIngestible;
                case "resources": return d => d != null && d.CountAsResource;
                // A category with no carried meaning (chunks, buildings, plants)
                // yields an empty worn block rather than a wrong one.
                case "haulable":
                case "all": return d => d != null;
                default: return d => false;
            }
        }

        // Everything a colonist has on them: worn apparel, equipment, inventory.
        // Not in the thing lister at all — apparel is held, not spawned.
        private static List<Thing> WornPool(Map map, Func<ThingDef, bool> match)
        {
            var result = new List<Thing>();
            // Snapshot: AllPawnsSpawned is the real list, but the loop reaches
            // apparel getters mods can extend (PawnVerbs' standing reason).
            var pawns = new List<Pawn>(map.mapPawns.AllPawnsSpawned);
            for (int i = 0; i < pawns.Count; i++)
            {
                var p = pawns[i];
                if (p == null || p.Dead) continue;
                if (PawnSafe.Hidden(p, map)) continue;
                string cls = PawnSafe.Classify(p);
                if (cls != PawnSafe.ClassColonist && cls != PawnSafe.ClassSlave) continue;
                // Snapshot WornApparel: it is wornApparel.InnerListForReading,
                // the LIVE list (2.2's PawnSerializer.Apparel, same reason).
                if (p.apparel != null)
                {
                    try
                    {
                        var worn = new List<Apparel>(p.apparel.WornApparel);
                        for (int j = 0; j < worn.Count; j++)
                            if (worn[j]?.def != null && match(worn[j].def)) result.Add(worn[j]);
                    }
                    catch { }
                }
                if (p.equipment != null)
                {
                    try
                    {
                        var eq = new List<ThingWithComps>(p.equipment.AllEquipmentListForReading);
                        for (int j = 0; j < eq.Count; j++)
                            if (eq[j]?.def != null && match(eq[j].def)) result.Add(eq[j]);
                    }
                    catch { }
                }
                if (p.inventory?.innerContainer != null)
                {
                    try
                    {
                        var inv = new List<Thing>(p.inventory.innerContainer);
                        for (int j = 0; j < inv.Count; j++)
                            if (inv[j]?.def != null && match(inv[j].def)) result.Add(inv[j]);
                    }
                    catch { }
                }
            }
            return result;
        }

        // ------------------------- the rollup builder -----------------------

        private sealed class Roll
        {
            public ThingDef Def;
            public int Count, Stacks, Forbidden, ForbiddenStacks, Roofed, Unroofed, AtRisk;
            public int HpMin = int.MaxValue, HpMax = int.MinValue;
            public bool UsesHp, Deteriorates;
            public QualityCategory QBest, QWorst;
            public bool AnyQuality;
            public IntVec3 At;
            public int BiggestStack = -1;
            public Dictionary<string, int> Stuffs;
        }

        // `order` governs the DETAIL list only — `rollups` is def-keyed and
        // deliberately untouched (class comment, git-bug 70ac258 ruling). It is
        // optional so the one caller that asks for no detail at all
        // (PlaceVerbs' room `contains`) does not have to name an order that
        // could not apply to anything it receives.
        internal static Dictionary<string, object> Rollups(
            Map map, List<Thing> things, int cap, bool detail, int detailCap,
            string order = OrderById)
        {
            var hp = new WorldSafe.MaxHpMemo();
            var det = new WorldSafe.DeteriorateMemo();
            var rolls = new Dictionary<ThingDef, Roll>();
            int totalCount = 0, totalStacks = 0, totalForbidden = 0, totalAtRisk = 0;

            for (int i = 0; i < things.Count; i++)
            {
                var t = things[i];
                if (t?.def == null) continue;
                if (!rolls.TryGetValue(t.def, out var r))
                    rolls[t.def] = r = new Roll { Def = t.def, Deteriorates = det.Of(t.def) };

                int n = Math.Max(1, t.stackCount);
                r.Count += n;
                r.Stacks++;
                totalCount += n;
                totalStacks++;

                bool forbidden = false;
                try { forbidden = t.IsForbidden(Faction.OfPlayer); }
                catch { }
                if (forbidden) { r.Forbidden += n; r.ForbiddenStacks++; totalForbidden += n; }

                bool roofed = false;
                try { roofed = t.Spawned && t.Position.Roofed(map); }
                catch { }
                if (roofed) r.Roofed++;
                else
                {
                    r.Unroofed++;
                    if (r.Deteriorates) { r.AtRisk++; totalAtRisk++; }
                }

                if (t.def.useHitPoints)
                {
                    int max = hp.Of(t);
                    if (max > 0)
                    {
                        r.UsesHp = true;
                        int pct = WorldSafe.Pct((float)t.HitPoints / max);
                        if (pct < r.HpMin) r.HpMin = pct;
                        if (pct > r.HpMax) r.HpMax = pct;
                    }
                }

                try
                {
                    if (t.TryGetQuality(out var q))
                    {
                        if (!r.AnyQuality) { r.QBest = q; r.QWorst = q; r.AnyQuality = true; }
                        else
                        {
                            if ((int)q > (int)r.QBest) r.QBest = q;
                            if ((int)q < (int)r.QWorst) r.QWorst = q;
                        }
                    }
                }
                catch { }

                if (t.Stuff != null)
                {
                    if (r.Stuffs == null) r.Stuffs = new Dictionary<string, int>();
                    string s = t.Stuff.defName;
                    r.Stuffs[s] = r.Stuffs.TryGetValue(s, out var sc) ? sc + n : n;
                }

                if (n > r.BiggestStack && t.Spawned) { r.BiggestStack = n; r.At = t.Position; }
            }

            // Attention order, and the reason is the same one 2.6 was written
            // for: a cap that cuts by count hides the forbidden pile behind the
            // steel. Forbidden first, then deteriorating, then size.
            var all = new List<Roll>(rolls.Values);
            all.Sort((a, b) =>
            {
                int c = Attention(b).CompareTo(Attention(a));
                return c != 0 ? c : string.CompareOrdinal(a.Def.defName, b.Def.defName);
            });

            var list = new List<object>();
            for (int i = 0; i < all.Count && i < cap; i++) list.Add(Line(all[i]));

            var d = new Dictionary<string, object>
            {
                ["rollups"] = list,
                ["rollups_total"] = all.Count,
                ["rollups_more"] = Math.Max(0, all.Count - list.Count),
                // Not a preference: state it, so position reads as urgency
                // rather than as inventory order.
                ["order"] = "attention-desc",
                ["totals"] = new Dictionary<string, object>
                {
                    ["count"] = totalCount,
                    ["stacks"] = totalStacks,
                    ["forbidden"] = totalForbidden,
                    ["at_risk"] = totalAtRisk,
                },
                ["hp_source"] = "memo(def,stuff,quality)",
            };

            if (detail)
            {
                // SELECTION. Scored ONCE per thing rather than inside the
                // comparator: ThingAttention reaches IsForbidden and the hp
                // memo, so recomputing it O(n log n) times is both wasteful and
                // a comparator that could disagree with itself. `pawns` scores
                // into this same shape for the same reason. Null-def entries
                // are dropped here rather than inside the cut loop, where they
                // used to consume a cap slot and emit nothing.
                var scored = new List<KeyValuePair<int, Thing>>();
                for (int i = 0; i < things.Count; i++)
                {
                    var t = things[i];
                    if (t?.def == null) continue;
                    scored.Add(new KeyValuePair<int, Thing>(ThingAttention(t, hp, det), t));
                }
                // Attention before the cut — 2.6's rule, untouched: a cap that
                // cut by id would hide the forbidden stack behind whatever
                // happens to have been spawned first. Tie-break on id so the
                // ranking is itself reproducible.
                scored.Sort((a, b) =>
                {
                    int c = b.Key.CompareTo(a.Key);
                    return c != 0 ? c : a.Value.thingIDNumber.CompareTo(b.Value.thingIDNumber);
                });

                var rank = new Dictionary<Thing, int>();
                var keptThings = new List<Thing>();
                for (int i = 0; i < scored.Count && i < detailCap; i++)
                {
                    rank[scored[i].Value] = i;
                    keptThings.Add(scored[i].Value);
                }

                // PRESENTATION. The survivors only — never the candidate set,
                // or the cap would start cutting by id and 2.6's rule dies.
                if (order == OrderById)
                    keptThings.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));

                var lines = new List<object>();
                for (int i = 0; i < keptThings.Count; i++)
                {
                    var t = keptThings[i];
                    object hpPct = null;
                    if (t.def.useHitPoints)
                    {
                        int max = hp.Of(t);
                        if (max > 0) hpPct = WorldSafe.Pct((float)t.HitPoints / max);
                    }
                    string quality = null;
                    try { if (t.TryGetQuality(out var q)) quality = q.ToString(); }
                    catch { }
                    lines.Add(new Dictionary<string, object>
                    {
                        ["id"] = t.thingIDNumber,
                        ["def"] = t.def.defName,
                        ["label"] = WorldSafe.Safe(() => t.LabelShort),
                        ["count"] = t.stackCount,
                        ["stuff"] = t.Stuff?.defName,
                        ["quality"] = quality,
                        ["hp_pct"] = hpPct,
                        ["forbidden"] = WorldSafe.SafeObj(() => (object)t.IsForbidden(Faction.OfPlayer)) ?? false,
                        ["roofed"] = WorldSafe.SafeObj(() => (object)(t.Spawned && t.Position.Roofed(map))) ?? false,
                        ["at"] = t.Spawned ? Positions.Out(t.Position) : null,
                        ["held_by"] = t.Spawned ? null : WorldSafe.Safe(() => (t.ParentHolder as Thing)?.LabelShort),
                        // The urgency the id order no longer encodes, carried
                        // per line so a caller reading a stable list still
                        // knows what to look at first. 0 is the most
                        // attention-worthy thing RETURNED.
                        ["attention_rank"] = rank[t],
                    });
                }
                d["things"] = lines;
                // Keyed on the scored candidates rather than the raw pool, so
                // total, `more` and the ranks all count the same population.
                d["things_total"] = scored.Count;
                d["things_more"] = Math.Max(0, scored.Count - lines.Count);
                // Two facts, not one (git-bug 70ac258), prefixed because the
                // bare `order` above belongs to `rollups`, which is a different
                // list with a different contract.
                d["things_order"] = order == OrderById ? "id-asc" : "attention-desc";
                d["things_selected_by"] = "attention-desc";
            }
            return d;
        }

        private static int Attention(Roll r)
        {
            int score = 0;
            if (r.Forbidden > 0) score += 100000;
            if (r.AtRisk > 0) score += 50000;
            if (r.UsesHp && r.HpMin != int.MaxValue && r.HpMin < 50) score += 20000;
            return score + Math.Min(r.Count, 10000);
        }

        private static int ThingAttention(Thing t, WorldSafe.MaxHpMemo hp, WorldSafe.DeteriorateMemo det)
        {
            if (t?.def == null) return 0;
            int score = 0;
            try { if (t.IsForbidden(Faction.OfPlayer)) score += 100000; }
            catch { }
            if (t.def.useHitPoints)
            {
                int max = hp.Of(t);
                if (max > 0) score += Math.Max(0, 100 - WorldSafe.Pct((float)t.HitPoints / max)) * 100;
            }
            return score + Math.Min(t.stackCount, 1000);
        }

        private static Dictionary<string, object> Line(Roll r)
        {
            var d = new Dictionary<string, object>
            {
                ["def"] = r.Def.defName,
                ["label"] = r.Def.label,
                ["count"] = r.Count,
                ["stacks"] = r.Stacks,
                ["forbidden"] = r.Forbidden,
                ["forbidden_stacks"] = r.ForbiddenStacks,
                ["roofed"] = r.Roofed,
                ["unroofed"] = r.Unroofed,
                ["deteriorates"] = r.Deteriorates,
                ["at_risk"] = r.AtRisk,
                ["at"] = r.BiggestStack >= 0 ? Positions.Out(r.At) : null,
            };
            if (r.UsesHp && r.HpMin != int.MaxValue)
            {
                d["hp_min_pct"] = r.HpMin;
                d["hp_max_pct"] = r.HpMax;
            }
            if (r.AnyQuality)
            {
                d["quality_best"] = r.QBest.ToString();
                d["quality_worst"] = r.QWorst.ToString();
            }
            if (r.Stuffs != null && r.Stuffs.Count > 0)
            {
                var byCount = new List<KeyValuePair<string, int>>(r.Stuffs);
                byCount.Sort((a, b) =>
                {
                    int c = b.Value.CompareTo(a.Value);
                    return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
                });
                var stuffs = new Dictionary<string, object>();
                for (int i = 0; i < byCount.Count && i < StuffCap; i++) stuffs[byCount[i].Key] = byCount[i].Value;
                if (byCount.Count > StuffCap) stuffs["+more"] = byCount.Count - StuffCap;
                d["stuffs"] = stuffs;
            }
            return d;
        }

        // ------------------------------ scope -------------------------------
        // `in` is a place, resolved once and then asked per thing. A cell set is
        // materialised for the bounded scopes so the per-thing test is a hash
        // lookup rather than a zone/room walk.
        internal sealed class Scope
        {
            public string Kind = "map";
            public string Label = "map";
            public Dictionary<string, object> Detail;
            private HashSet<IntVec3> cells;
            private bool anySlotGroup;
            private Map map;

            public bool Contains(Thing t)
            {
                if (Kind == "map") return true;
                if (t == null || !t.Spawned) return false;
                if (anySlotGroup)
                {
                    try { return t.Position.GetSlotGroup(map) != null; }
                    catch { return false; }
                }
                return cells != null && cells.Contains(t.Position);
            }

            public static Scope Resolve(Map map, string where, VerbArgs args)
            {
                var s = new Scope { map = map };
                if (string.IsNullOrEmpty(where) || where == "map")
                {
                    s.Detail = new Dictionary<string, object> { ["kind"] = "map" };
                    return s;
                }
                if (where == "worn")
                {
                    s.Kind = "worn";
                    s.Label = "worn";
                    s.Detail = new Dictionary<string, object> { ["kind"] = "worn" };
                    return s;
                }
                if (where == "stockpile" || where == "stockpiles")
                {
                    s.Kind = "stockpiles";
                    s.Label = "stockpiles";
                    s.anySlotGroup = true;
                    s.Detail = new Dictionary<string, object>
                    {
                        ["kind"] = "stockpiles",
                        // Slot groups, not stockpile ZONES: a storage building
                        // (shelf, Storefront rack) is storage the player reads
                        // the same way, and haulDestinationManager is what the
                        // game itself asks.
                        ["note"] = "every slot group on the map (stockpile zones AND storage buildings)",
                    };
                    return s;
                }
                if (where.StartsWith("stockpile:", StringComparison.Ordinal))
                {
                    int id = ParseId(where, "stockpile:");
                    var zone = FindZone(map, id) as Zone_Stockpile
                        ?? throw new VerbArgsException($"no stockpile zone with id {id}");
                    s.Kind = "stockpile";
                    s.Label = where;
                    s.cells = new HashSet<IntVec3>(WorldSafe.ZoneCells(zone));
                    s.Detail = new Dictionary<string, object>
                    {
                        ["kind"] = "stockpile",
                        ["id"] = zone.ID,
                        ["label"] = zone.label,
                        ["cells"] = zone.CellCount,
                    };
                    return s;
                }
                if (where.StartsWith("zone:", StringComparison.Ordinal))
                {
                    int id = ParseId(where, "zone:");
                    var zone = FindZone(map, id) ?? throw new VerbArgsException($"no zone with id {id}");
                    s.Kind = "zone";
                    s.Label = where;
                    s.cells = new HashSet<IntVec3>(WorldSafe.ZoneCells(zone));
                    s.Detail = new Dictionary<string, object>
                    {
                        ["kind"] = "zone",
                        ["id"] = zone.ID,
                        ["label"] = zone.label,
                        ["cells"] = zone.CellCount,
                    };
                    return s;
                }
                if (where.StartsWith("room:", StringComparison.Ordinal))
                {
                    int id = ParseId(where, "room:");
                    var room = WorldSafe.FindRoom(map, id);
                    s.Kind = "room";
                    s.Label = where;
                    s.cells = new HashSet<IntVec3>();
                    foreach (var c in room.Cells) s.cells.Add(c);
                    s.Detail = new Dictionary<string, object>
                    {
                        ["kind"] = "room",
                        ["id"] = room.ID,
                        ["cells"] = room.CellCount,
                    };
                    return s;
                }
                if (where == "rect")
                {
                    if (!(args.Raw("rect") is List<object> r) || r.Count != 4
                        || !(r[0] is double rx) || !(r[1] is double rz)
                        || !(r[2] is double rw) || !(r[3] is double rh))
                        throw new VerbArgsException("in:\"rect\" needs rect:[x,z,w,h]");
                    var rect = new CellRect((int)rx, (int)rz, Math.Max(1, (int)rw), Math.Max(1, (int)rh));
                    s.Kind = "rect";
                    s.Label = "rect";
                    s.cells = new HashSet<IntVec3>();
                    foreach (var c in rect) if (c.InBounds(map)) s.cells.Add(c);
                    if (s.cells.Count == 0)
                        throw new VerbArgsException(
                            $"rect [{rect.minX},{rect.minZ},{rect.Width},{rect.Height}] lies entirely outside the "
                            + $"{map.Size.x}x{map.Size.z} map");
                    s.Detail = new Dictionary<string, object>
                    {
                        ["kind"] = "rect",
                        ["rect"] = new List<object> { (double)rect.minX, (double)rect.minZ, (double)rect.Width, (double)rect.Height },
                        ["cells"] = s.cells.Count,
                    };
                    return s;
                }
                throw new VerbArgsException(
                    "in must be map|stockpiles|stockpile:<id>|zone:<id>|room:<id>|rect|worn");
            }

            private static int ParseId(string s, string prefix)
            {
                if (!int.TryParse(s.Substring(prefix.Length), out int id))
                    throw new VerbArgsException($"'{s}': id must be a whole number");
                return id;
            }

            private static Zone FindZone(Map map, int id)
            {
                var zones = map.zoneManager.AllZones;
                for (int i = 0; i < zones.Count; i++) if (zones[i] != null && zones[i].ID == id) return zones[i];
                return null;
            }
        }
    }
}
