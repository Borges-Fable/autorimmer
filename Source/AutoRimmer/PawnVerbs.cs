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
    public static class PawnVerbs
    {
        private const int RosterCap = 20;

        // pawns {filter?, cap?} -> the roster, capped and ordered by attention.
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

            // Ordered by attention BEFORE the cut, for the reason 2.6 exists:
            // a cap that cuts in roster order hides the downed colonist behind
            // ten healthy ones. Tie-break on id, not name — ids are unique and
            // stable, two pawns can share a short name.
            scored.Sort((a, b) =>
            {
                int c = b.Key.CompareTo(a.Key);
                return c != 0 ? c : a.Value.thingIDNumber.CompareTo(b.Value.thingIDNumber);
            });

            var list = new List<object>();
            for (int i = 0; i < scored.Count && i < cap; i++)
                list.Add(PawnSerializer.Brief(scored[i].Value, classOf[scored[i].Value]));

            return new Dictionary<string, object>
            {
                ["filter"] = filter,
                ["list"] = list,
                ["total"] = scored.Count,
                ["more"] = scored.Count > cap ? scored.Count - cap : 0,
                // Not a preference: state it, so position reads as urgency
                // rather than as roster order.
                ["order"] = "attention-desc",
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
