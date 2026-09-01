using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace AutoRimmer
{
    // ===================================================== M1 finding D ======
    // HOSTILES THE COLONY HAS DELIBERATELY DECLINED TO FIGHT.
    //
    // The M1 map shipped a dormant insect cluster — 4 megascarab, a locust, a
    // spelopede — fogged from tick 0, never triggered, never approaching.
    // `threats.hostiles` counted 6 for ten in-game days while
    // `pawns {filter:"hostile"}` returned 0 (the fog filter), so 4.2's "final
    // digest reads 0 drafted, 0 hostiles" was unpassable on that map and the
    // agent could not even see what was failing it.
    //
    // Evan's ruling (2026-09-01): those insects are not hostile in the same way
    // a raider is — they will not attack at will — and the run SHOULD have
    // explicitly declared it was not attacking them because it was not ready.
    // The criterion becomes "0 drafted, 0 hostiles that we haven't pardoned".
    //
    // The whole point is that this is a RECORDED ACT and not a silent exemption
    // baked into a counter. Hence: `reason` is required, the act is journaled
    // with its ids and its reason, and the set is scribed so the decision
    // survives save/load and can be audited afterwards. `hostiles` KEEPS its
    // meaning — the total, everything, always — so a pardon can never hide a
    // threat from a reader who looks at the field that has always been there.
    //
    // ------------------------- THE GATE, HONESTLY ---------------------------
    // DESIGN §Action model says a player verb re-implements the precondition
    // its UI widget holds and cites it. THERE IS NO SUCH WIDGET HERE: RimWorld
    // has no "declare we are not fighting this" control, because RimWorld has no
    // such concept — this is AutoRimmer's own bookkeeping over AutoRimmer's own
    // counter. Citing a member would be dressing our invention as the game's,
    // which is the exact failure the rule exists to prevent. So the precondition
    // is stated as ours and made narrow instead:
    //
    //   * an id must resolve to a pawn the DIGEST IS COUNTING — spawned, not
    //     downed, not dead, HostileTo(Faction.OfPlayer), the identical predicate
    //     DigestVerb.ThreatSection uses. A pardon can therefore never be granted
    //     for something that is not a counted threat, and the two numbers cannot
    //     drift apart.
    //   * `reason` is required and non-empty on an add.
    //
    // FOG. `ThingArg` fog-filters, which is right for every verb that acts on a
    // thing IN THE WORLD and wrong here: the whole finding is that these pawns
    // are fogged, and a pardon that cannot name them is no pardon. Resolution:
    // this verb resolves ids WITHOUT the fog filter and publishes id, kind label
    // and dormancy — NEVER a position, never a def, never anything spatial. The
    // digest already publishes the count and the kind rollup ("megascarab x4"),
    // so this adds no knowledge of unexplored ground; it only lets the agent
    // name what it has already been told exists. The no-args listing carries the
    // candidates for the same reason: ids the agent cannot obtain make the verb
    // unusable, and it had no other route to them.
    public class ThreatPardonComponent : GameComponent
    {
        // thingIDNumber -> the stated reason. Scribed by value, so a pardon
        // outlives the session that granted it.
        private Dictionary<int, string> pardons = new Dictionary<int, string>();

        public ThreatPardonComponent(Game game)
        {
        }

        public static ThreatPardonComponent Current
            => Verse.Current.Game?.GetComponent<ThreatPardonComponent>();

        public Dictionary<int, string> All => pardons;

        public bool Has(int id) => pardons.ContainsKey(id);

        public string Reason(int id) => pardons.TryGetValue(id, out var r) ? r : null;

        public void Add(int id, string reason) => pardons[id] = reason;

        public bool Remove(int id) => pardons.Remove(id);

        public int Clear()
        {
            int n = pardons.Count;
            pardons.Clear();
            return n;
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref pardons, "autoRimmerThreatPardons", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && pardons == null)
                pardons = new Dictionary<int, string>();
        }

        // ---------------- IS IT STILL ASLEEP? ------------------------------
        //
        // A pardon must not outlive the thing being harmless, and the game does
        // reify that state — twice, at two levels, both read here, most specific
        // first. Neither is a heuristic; both are the cluster's own machinery.
        //
        // 1. THE LORD'S TOIL. RimWorld/RoomGenUtility.cs SpawnDormantThreatCluster
        //    (which SpawnDormantInsectCluster delegates to) puts every pawn of
        //    the cluster under a `LordJob_StructureThreatCluster`, and
        //    RimWorld/LordJob_StructureThreatCluster.cs CreateGraph makes the
        //    graph's StartingToil `GetIdleToil()` = `LordToil_Sleep`. EVERY wake
        //    path leaves it: the DormancyWakeup/Clamor transition to
        //    LordToil_DefendPoint, and the Trigger_PawnHarmed /
        //    Trigger_AcquiredTarget transition to LordToil_HuntDownColonists. So
        //    `CurLordToil is LordToil_Sleep` IS "still dormant", exactly.
        //
        // 2. CompCanBeDormant.Awake — RimWorld/CompCanBeDormant.cs Awake, the
        //    per-pawn flag the cluster's OWN LordJob_StructureThreatCluster
        //    ShouldRemovePawn keys on. Used when there is no such lord.
        //
        // Returns null when NEITHER exists: the pawn has no dormancy state to
        // read, so there is nothing to lapse from and the pardon stays purely
        // manual. That is deliberate — the ruling says do not invent a
        // heuristic, and "hostile and approaching" has no clean predicate of its
        // own. The floor the ruling asks for is met unconditionally by
        // `hostiles`, which counts everything regardless of pardon.
        //
        // OBSERVER DISCIPLINE: Pawn.GetLord() is `p.lord` (Verse.AI.Group/
        // LordUtility.cs GetLord), Lord.CurLordToil and Lord.LordJob are the
        // `curLordToil`/`curJob` fields (Verse.AI.Group/Lord.cs). All plain
        // field reads. `Awake` is virtual and so is modded code — hence the
        // catch, WorldSafe.Safe's rule.
        public static bool? Dormant(Pawn p)
        {
            if (p == null) return null;
            try
            {
                var lord = p.GetLord();
                if (lord?.LordJob is LordJob_StructureThreatCluster)
                    return lord.CurLordToil is LordToil_Sleep;
                var comp = p.TryGetComp<CompCanBeDormant>();
                if (comp != null) return !comp.Awake;
            }
            catch { }
            return null;
        }

        // Pardoned AND still dormant. A pardon LAPSES the moment a dormancy
        // predicate exists and says awake — it stops counting as pardoned, which
        // pushes the pawn straight back into `hostiles_unpardoned` and in front
        // of the agent. The entry is NOT removed here: this is called from
        // `digest`, and an observer that pruned a scribed dictionary would be
        // the exact mutation-on-read hazard WorldSafe exists to catalogue. The
        // set is pruned only by `threat-pardon`, which is an act.
        public static bool Pardoned(Pawn p)
        {
            if (p == null) return false;
            var store = Current;
            if (store == null || !store.Has(p.thingIDNumber)) return false;
            return Dormant(p) != false;
        }
    }

    internal static partial class PawnActs
    {
        // --------------------------------------------------------------------
        // threat-pardon
        //   {}                              -> list the set and the candidates
        //   {ids:[…], reason:"…"}           -> pardon (reason REQUIRED)
        //   {ids:[…], release:true}         -> release those
        //   {release_all:true}              -> release everything
        // --------------------------------------------------------------------
        [Verb("threat-pardon")]
        public static object ThreatPardon(VerbContext ctx)
        {
            const string V = "threat-pardon";
            var map = Find.CurrentMap ?? throw new VerbArgsException("no current map");
            var store = ThreatPardonComponent.Current
                ?? throw new VerbArgsException("no pardon store (no game loaded?)");
            var a = ctx.Args;

            if (a.Bool("release_all", false))
            {
                var was = new List<object>();
                foreach (var kv in store.All) was.Add(kv.Key);
                int n = store.Clear();
                // Nothing held is nothing mutated, and a journal row for a no-op
                // is a row that dilutes the audit trail this verb exists to keep.
                long s = n > 0
                    ? Act(V, "release-all", n + " pardons",
                        new Dictionary<string, object> { ["ids"] = was, ["released"] = n })
                    : 0;
                return Listing(V, map, store, n > 0 ? Stamp(s) : NoStamp(), "released " + n);
            }

            if (!a.Has("ids"))
                return Listing(V, map, store, NoStamp(), null);

            if (!(a.Raw("ids") is List<object> rawIds) || rawIds.Count == 0)
                throw new VerbArgsException("'ids' must be a non-empty list of thing ids");
            bool release = a.Bool("release", false);
            string reason = a.Str("reason");
            if (!release && string.IsNullOrEmpty(reason?.Trim()))
                throw new VerbArgsException(
                    "'reason' is required to pardon: a pardon with no stated reason is the silent "
                    + "exemption this verb exists to prevent. Say why the colony is declining to "
                    + "fight these (e.g. \"dormant hive, no ranged weapons yet\").");

            var applied = new List<object>();
            var refused = new List<object>();
            foreach (var raw in rawIds)
            {
                if (!(raw is double d)) { refused.Add(new Dictionary<string, object> { ["id"] = raw, ["reason"] = "not a thing id (number)" }); continue; }
                int id = (int)d;
                if (release)
                {
                    if (store.Remove(id)) applied.Add(id);
                    else refused.Add(new Dictionary<string, object> { ["id"] = id, ["reason"] = "was not pardoned" });
                    continue;
                }
                // THE PRECONDITION: it must be a threat the digest is counting.
                var p = CountedHostile(map, id);
                if (p == null)
                {
                    refused.Add(new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["reason"] = "not a hostile the digest is counting (spawned, not downed, not "
                            + "dead, HostileTo the player) — see `threat-pardon {}` for the candidates",
                    });
                    continue;
                }
                store.Add(id, reason.Trim());
                applied.Add(id);
            }

            long seq = applied.Count > 0
                ? Act(V, release ? "release" : "pardon", applied.Count + " hostiles",
                    new Dictionary<string, object>
                    {
                        ["ids"] = applied,
                        ["reason"] = release ? null : reason.Trim(),
                        ["refused_count"] = refused.Count,
                    })
                : 0;
            var data = Listing(V, map, store, applied.Count > 0 ? Stamp(seq) : NoStamp(),
                (release ? "released " : "pardoned ") + applied.Count);
            data["applied"] = applied;
            if (refused.Count > 0) data["refused"] = refused;
            return data;
        }

        // DigestVerb.ThreatSection's predicate, by id and WITHOUT the fog filter
        // — see this file's header on why fog is handled differently here.
        private static Pawn CountedHostile(Map map, int id)
        {
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                var p = pawns[i];
                if (p == null || p.thingIDNumber != id) continue;
                if (p.Downed || p.Dead || !p.HostileTo(Faction.OfPlayer)) return null;
                return p;
            }
            return null;
        }

        // The set, plus the candidates it could be drawn from. Id, kind label and
        // dormancy only — no position, no def, nothing spatial (header).
        private static Dictionary<string, object> Listing(string verb, Map map,
            ThreatPardonComponent store, Dictionary<string, object> action, string did)
        {
            var list = new List<object>();
            int pardoned = 0, total = 0;
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                var p = pawns[i];
                if (p == null || p.Downed || p.Dead || !p.HostileTo(Faction.OfPlayer)) continue;
                total++;
                bool? dormant = ThreatPardonComponent.Dormant(p);
                bool now = ThreatPardonComponent.Pardoned(p);
                if (now) pardoned++;
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = p.thingIDNumber,
                    ["kind"] = p.kindDef?.label ?? p.def?.label,
                    ["pardoned"] = now,
                    // null = the pawn has no dormancy state to read, so nothing
                    // will lapse its pardon automatically.
                    ["dormant"] = dormant,
                    ["reason"] = store.Reason(p.thingIDNumber),
                    // A pardon whose subject has WOKEN. The entry is still in the
                    // set (an observer may not prune it); it has simply stopped
                    // counting, and `threat-pardon {ids:[…],release:true}` is how
                    // it is cleaned up.
                    ["lapsed"] = store.Has(p.thingIDNumber) && dormant == false,
                });
            }
            var data = new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["ok"] = true,
                ["hostiles"] = total,
                ["hostiles_pardoned"] = pardoned,
                ["hostiles_unpardoned"] = total - pardoned,
                ["pardons_held"] = store.All.Count,
                ["candidates"] = list,
                ["action"] = action,
                ["note"] = "a pardon is a recorded decision not to fight, not a filter: `hostiles` "
                    + "still counts everything. A pardon lapses on its own when the subject wakes "
                    + "(dormant:false), and pardons for ids no longer on the map are held until "
                    + "released.",
            };
            if (did != null) data["did"] = did;
            return data;
        }
    }
}
