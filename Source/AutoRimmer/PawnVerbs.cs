using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // The pawn observers (spec 2.2): `pawns` is the roster glance, `pawn` the
    // per-pawn drill-down — everything the player sees opening a pawn's tabs.
    // Read-only throughout; PawnSafe holds the hazard catalogue and the guarded
    // routes, and nothing here calls a raw accessor PawnSafe warns about.
    //
    // FOG OF WAR (DESIGN decisions log 2026-08-30): both verbs are
    // player-facing, so neither reports a pawn the colony has not found — an
    // unspawned pawn, a pawn on another map, or a pawn standing in fog.
    // `pawns` COUNTS what it hid (`skipped`) rather than dropping it silently,
    // so "no raiders" and "no raiders you have seen" stay distinguishable;
    // `pawn <id>` declines the lookup outright, because confirming that id N
    // exists is itself the leak. `dev:*` is exempt and nothing here is dev:*.
    //
    // ONE LIST WALK. Both verbs classify from a single snapshot of
    // MapPawns.AllPawnsSpawned — the real `pawnsSpawned` list — rather than
    // from FreeColonistsSpawned / PrisonersOfColony / friends, because
    // FreeColonistsSpawned CLEARS and rebuilds a shared cached list on every
    // access and any getter that re-enters it mid-loop invalidates the
    // enumerator. That is the live Collection-was-modified bug 2.1 shipped, and
    // the digest's snapshot-before-iterate comment is the standing lesson.
    //
    // ================= ORDER IS A CONTRACT (git-bug 1eb2262) ================
    // `pawns` publishes TWO order facts because they are two different
    // questions, and 2.6 shipped only one of them:
    //
    //   `selected_by` — WHICH entries survive the cap. Always "attention-desc".
    //                   A cap that cut in list order would hide the downed
    //                   colonist behind ten healthy ones; that is 2.6's rule
    //                   and it is not negotiable.
    //   `order`       — the order the survivors are EMITTED in. Caller's
    //                   choice via `order:`, default "id-asc".
    //
    // The default is `id-asc` — ascending thingIDNumber — because attention is
    // a live score. `PawnSerializer.Attention` sums mood as
    // `100 - mood_pct`, so two colonists one mood point apart swap places on
    // any tick that moves either mood, and downed/bleeding/tend flip whole
    // 400-1000 point terms. Under `attention-desc` the roster is a valid
    // ranking and a WORTHLESS handle: `roster[0]` names a different pawn on
    // consecutive reads with nothing wrong. 3.4's acceptance rode that index
    // and six checks failed with no visible cause.
    //
    // Nothing in RimWorld could have been documented instead — this had to be
    // a sort we add. `MapPawns.AllPawnsSpawned` IS the raw `pawnsSpawned`
    // List<Pawn>: `RegisterPawn` appends, `DeRegisterPawn` removes, and
    // `UpdateRegistryForPawn` (faction change, host-faction change) does both,
    // moving a pawn to the END of the list while it stands still. Loading a
    // save re-registers in save-file order. The player-faction sub-list IS
    // maintained sorted — `RegisterPawn` InsertionSorts
    // `pawnsInFactionSpawned[Faction.OfPlayer]` by `playerSettings.joinTick`
    // — but joinTick ties on every pawn that joined the same tick (the three
    // starting colonists are all `joinTick` 0 by Pawn_PlayerSettings's own
    // `joinTick = 0` branch), and it is the wrong list anyway: we walk
    // AllPawnsSpawned, not SpawnedPawnsInFaction. The game's genuinely
    // player-facing order, `Pawn_PlayerSettings.displayOrder`, is worse: it
    // defaults to the sentinel -9999999 and is assigned LAZILY BY THE UI —
    // `ColonistBar.CheckRecacheEntries` writes the scribed field on any pawn
    // still holding the sentinel — so on a bench where the bar has not drawn
    // it is the same number for every colonist. Reading it to sort by it is
    // also exactly the write-on-read shape PawnSafe exists to refuse.
    //
    // thingIDNumber is the key because it is stable for a pawn's lifetime, is
    // already the id every other verb takes (`pawn:<id>`), and is what
    // `PawnActs.PawnList` has always sorted `pawns:"colonists"` by — the
    // action side was stable and the observer side was not, which is the
    // inconsistency this fixes.
    //
    // Urgency is not lost by reordering: every line carries `attention_rank`,
    // the 0-based position it would have held under `order:"attention"`.
    public static class PawnVerbs
    {
        private const int RosterCap = 20;

        private const string OrderById = "id";
        private const string OrderByAttention = "attention";
        private const string OrderWords = "id|attention";

        // pawns {filter?, cap?, order?} -> the roster, attention-selected and
        // id-ordered. See the ORDER contract in the class comment.
        //
        // `filter` accepts the five spec words, `all`, and every value the
        // `class` field can take — a published field value that is not a legal
        // filter is a papercut. See PawnSafe.FilterClasses.
        [Verb("pawns")]
        public static object Pawns(VerbContext ctx)
        {
            var map = PawnSafe.CurrentMap();
            string filter = ctx.Args.Str("filter", "colonist");
            var classes = PawnSafe.FilterClasses(filter);
            if (classes != null && classes.Count == 0)
                throw new VerbArgsException($"unknown filter '{filter}' ({PawnSafe.FilterWords})");
            int cap = ctx.Args.Int("cap", RosterCap);
            if (cap < 1 || cap > 200) throw new VerbArgsException("cap must be 1..200");
            // Unknown order is a bad-args, not a silent fallback: a typo that
            // quietly returns the OTHER order is the worst outcome here, since
            // the caller asked precisely because it cares which one it gets.
            string order = ctx.Args.Str("order", OrderById);
            if (order != OrderById && order != OrderByAttention)
                throw new VerbArgsException($"unknown order '{order}' ({OrderWords})");

            // Snapshot: see the class comment. AllPawnsSpawned is the real list,
            // but the loop below reaches getters (job reports, mood) that mods
            // can extend, so it is copied on principle rather than on proof.
            var spawned = new List<Pawn>(map.mapPawns.AllPawnsSpawned);
            var scored = new List<KeyValuePair<int, Pawn>>();
            var classOf = new Dictionary<Pawn, string>();
            int skippedFogged = 0, skippedDead = 0;
            var byClass = new Dictionary<string, object>();

            for (int i = 0; i < spawned.Count; i++)
            {
                var p = spawned[i];
                if (p == null) continue;
                if (p.Dead) { skippedDead++; continue; }
                // Fog: one rule across the player-facing surface.
                if (PawnSafe.Hidden(p, map)) { skippedFogged++; continue; }
                string cls = PawnSafe.Classify(p);
                byClass[cls] = byClass.TryGetValue(cls, out var n) ? (int)n + 1 : 1;
                if (classes != null && !classes.Contains(cls)) continue;
                classOf[p] = cls;
                scored.Add(new KeyValuePair<int, Pawn>(PawnSerializer.Attention(p), p));
            }

            // SELECTION. Ordered by attention BEFORE the cut, for the reason
            // 2.6 exists: a cap that cuts in roster order hides the downed
            // colonist behind ten healthy ones. Tie-break on id, not name —
            // ids are unique and stable, two pawns can share a short name.
            // This ranking is what `attention_rank` publishes, and it decides
            // membership regardless of what `order` the caller asked for.
            scored.Sort((a, b) =>
            {
                int c = b.Key.CompareTo(a.Key);
                return c != 0 ? c : a.Value.thingIDNumber.CompareTo(b.Value.thingIDNumber);
            });

            var rank = new Dictionary<Pawn, int>();
            var kept = new List<Pawn>();
            for (int i = 0; i < scored.Count && i < cap; i++)
            {
                rank[scored[i].Value] = i;
                kept.Add(scored[i].Value);
            }

            // PRESENTATION. Re-sort the survivors only — never the candidate
            // set, or the cap would start cutting by id and 2.6's rule dies.
            if (order == OrderById)
                kept.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));

            var list = new List<object>();
            for (int i = 0; i < kept.Count; i++)
            {
                var line = PawnSerializer.Brief(kept[i], classOf[kept[i]]);
                // The urgency the id order no longer encodes, carried per line
                // so a caller reading a stable roster still knows who to look
                // at first. 0 is the most attention-worthy pawn RETURNED.
                line["attention_rank"] = rank[kept[i]];
                list.Add(line);
            }

            return new Dictionary<string, object>
            {
                ["filter"] = filter,
                ["list"] = list,
                ["total"] = scored.Count,
                ["more"] = scored.Count > cap ? scored.Count - cap : 0,
                // Two facts, not one (git-bug 1eb2262). `order` is what
                // position means; `selected_by` is what the cap kept. They
                // differ by default and a consumer that conflates them will
                // read the roster as a ranking when it is a register.
                ["order"] = order == OrderById ? "id-asc" : "attention-desc",
                ["selected_by"] = "attention-desc",
                // Every class present on the map, filtered or not — one line
                // that answers "is there anything I did not ask about".
                ["by_class"] = byClass,
                ["skipped"] = new Dictionary<string, object>
                {
                    // removal "none", reason "unexplored": a fogged pawn is not
                    // blocked, it is simply not known to the colony.
                    ["fogged_or_unspawned"] = skippedFogged,
                    ["dead"] = skippedDead,
                },
            };
        }

        // pawn {id, sections?, opinions?, opinion_cap?} -> the drill-down.
        //
        // `sections` selects a subset (the full result is ~4KB; two sections are
        // ~600 bytes) and defaults to all of them. Unknown names are a bad-args
        // rather than a silent no-op, because a typo that quietly returns an
        // empty result is the worst outcome for a program caller.
        [Verb("pawn")]
        public static object PawnDetail(VerbContext ctx)
        {
            var map = PawnSafe.CurrentMap();
            int id = ctx.Args.IntReq("id");

            var want = new HashSet<string>();
            if (ctx.Args.Has("sections"))
            {
                foreach (var s in ctx.Args.StrList("sections"))
                {
                    if (Array.IndexOf(PawnSerializer.AllSections, s) < 0)
                        throw new VerbArgsException(
                            $"unknown section '{s}' ({string.Join("|", PawnSerializer.AllSections)})");
                    want.Add(s);
                }
                if (want.Count == 0) throw new VerbArgsException("sections must not be empty");
            }
            else
            {
                foreach (var s in PawnSerializer.AllSections) want.Add(s);
            }

            var pawn = Find(map, id);
            var data = PawnSerializer.Detail(pawn, map, want, ctx.Args);
            data["sections"] = new List<object>(want);
            return data;
        }

        // Visible, spawned pawns on the current map only. The error names the
        // POLICY rather than this pawn: saying "id 4211 is fogged" would confirm
        // the pawn exists, which is the leak the fog rule exists to prevent.
        private static Pawn Find(Map map, int id)
        {
            var spawned = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < spawned.Count; i++)
            {
                var p = spawned[i];
                if (p != null && p.thingIDNumber == id && !PawnSafe.Hidden(p, map)) return p;
            }
            throw new VerbArgsException(
                $"no visible pawn with id {id} on the current map "
                + "(pawns that are unspawned, on another map, or in unexplored ground are not reported)");
        }
    }
}
