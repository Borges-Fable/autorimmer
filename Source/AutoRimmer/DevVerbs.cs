using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace AutoRimmer
{
    // ============================================================ spec 3.1 ===
    // THE DEV LAYER — the god-hand. Fixture setup and demo staging.
    //
    // DESIGN §Action model splits the verb surface in two. Player verbs must
    // re-implement the precondition the UI widget holds and cite it, because
    // RimWorld leaves its model wide open. `dev:*` is the OTHER half: it
    // bypasses those gates deliberately, and its whole contract is that it says
    // so. Three standing rules, all of them enforced in this file rather than
    // left to each verb:
    //
    //  1. EVERY dev verb writes a `dev` journal event before it returns, and the
    //     result carries that event's seq in `dev.journal_seq`. The bench runs
    //     `pauseOnError=False` / `automaticPauseMode=Never` (spike 0.1), so a
    //     dev:incident raid runs straight into whatever `advance` is doing —
    //     the journal line is the ONLY provenance there will be, and the seq in
    //     the result is what lets a test tie a mutation to its own line.
    //
    //  2. FOG IS EXEMPT (DESIGN decisions log 2026-08-30). The player-facing
    //     surface — map-view, find-rect, nearest, room-at, pawns, pawn — hides
    //     undiscovered cells. These verbs do not. Every result therefore carries
    //     `dev.fog_exempt:true`, and any verb that actually acted on fogged
    //     ground says which cells in `dev.fogged`. That is the difference
    //     between "documented god-hand" and "silently different answer".
    //
    //  3. WRAP, DON'T REINVENT. Where a DebugAction's body is a one-liner into
    //     game API, this file calls the game API and cites the DebugAction by
    //     member name. Where the DebugAction is deliberately RANDOM (stuff
    //     choice, quality roll) a fixture verb cannot be — a fixture that is not
    //     reproducible is not a fixture — so the default is deterministic and
    //     the random behaviour is opt-in. Each such deviation is named at its
    //     call site.
    //
    // VERB NAMES CARRY A COLON. `dev:spawn-thing`, not `dev-spawn-thing`.
    // Verified end to end before committing to it: VerbAttribute takes an
    // arbitrary string and VerbRegistry keys a Dictionary on it (VerbRegistry.cs
    // RegisterAll); the op travels in JSON only — Poller.ScanInbox reads it with
    // MiniJson.GetString and never touches the filesystem with it; the result
    // FILE is named from the command **id**, not the op (Poller.ResultFileName),
    // and Poller.Sanitize is applied to that id alone. On the client side
    // `rwa`'s new_id() strips non-alphanumerics out of the op before using it as
    // an id prefix, so `dev:spawn-thing` yields the id `devspawnthin-…`, which
    // passes rwa's own ID_RE. Nothing in the chain sees the colon but the
    // registry dictionary and the JSON envelope.
    //
    // SUPERSESSION. `journal-selftest` (1.2) and `pawn-fixture` (2.2) were
    // written as stimulus because no dev layer existed. They stay — their
    // acceptance replays depend on them — but every state mutation they make now
    // has a first-class verb here: selftest `downed`/`break`/`raid` -> dev:damage
    // / dev:mental-state / dev:incident; pawn-fixture `wound`/`sadden`/`tatter`/
    // `prisoner`/`visitor` -> dev:damage / dev:add-hediff (thought stimulus stays
    // theirs) / dev:destroy+spawn / dev:guest-status / dev:incident.
    // =========================================================================
    public static class Dev
    {
        // Gate. Same shape as pawn-fixture's, and for the same reason: these
        // verbs mutate, devMode is the bench's own switch, and the profile
        // seeds it True (spike 0.1 FINDINGS). A bench without it is a bench
        // where a fixture would half-apply.
        public static void Gate(string verb)
        {
            if (!Prefs.DevMode)
                throw new VerbArgsException(
                    verb + " requires devMode=True (the dev layer mutates game state by design)");
        }

        public static Map CurrentMap(string verb)
            => Find.CurrentMap ?? throw new VerbArgsException(verb + " needs a current map");

        // Def lookup that TELLS YOU WHAT IT KNOWS when it misses. A dev verb
        // whose every argument is a defName, against a 38-mod bench, will be
        // mistyped constantly; `no ThingDef named 'steel'` with no follow-up
        // costs a round trip per typo. This is the same courtesy Poller already
        // extends for `unknown-op` ("known ops: …") — one rule, not two.
        public static T Named<T>(string defName, string arg) where T : Def, new()
        {
            if (string.IsNullOrEmpty(defName))
                throw new VerbArgsException($"arg '{arg}' must be a {typeof(T).Name} defName");
            var d = DefDatabase<T>.GetNamedSilentFail(defName);
            if (d != null) return d;
            throw new VerbArgsException(
                $"no {typeof(T).Name} named '{defName}' (arg '{arg}'){NearMisses<T>(defName)}");
        }

        private const int SuggestCap = 8;

        private static string NearMisses<T>(string defName) where T : Def, new()
        {
            var hits = new List<string>();
            try
            {
                string needle = defName.ToLowerInvariant();
                foreach (var d in DefDatabase<T>.AllDefsListForReading)
                {
                    if (d?.defName == null) continue;
                    string hay = d.defName.ToLowerInvariant();
                    if (hay.Contains(needle) || needle.Contains(hay)
                        || (d.label != null && d.label.ToLowerInvariant().Contains(needle)))
                    {
                        hits.Add(d.defName);
                        if (hits.Count >= SuggestCap) break;
                    }
                }
            }
            catch { }
            if (hits.Count == 0) return "";
            return " — did you mean: " + string.Join(", ", hits.ToArray());
        }

        // Pawn handle. 2.2's thingIDNumber is the canonical id; a NAME is also
        // accepted because dev:spawn-pawn can set one and a scripted fixture
        // reads far better as "Ada" than as 4211. NOT fog-filtered and NOT
        // spawned-filtered: this is the dev layer (rule 2), and a fixture
        // routinely touches a pawn that is inside a drop pod or standing in
        // ground the colony has not walked.
        public static Pawn PawnArg(Map map, VerbArgs args, string key, bool required = true)
        {
            object raw = args.Raw(key);
            if (raw == null)
            {
                if (!required) return null;
                throw new VerbArgsException($"missing required arg '{key}' (pawn id or name)");
            }
            var all = new List<Pawn>(map.mapPawns.AllPawns);
            if (raw is double d)
            {
                int id = (int)d;
                for (int i = 0; i < all.Count; i++)
                    if (all[i] != null && all[i].thingIDNumber == id) return all[i];
                throw new VerbArgsException($"no pawn with id {id} on the current map");
            }
            if (raw is string s)
            {
                if (s.StartsWith("pawn:", StringComparison.Ordinal)
                    && int.TryParse(s.Substring(5), out int pid))
                {
                    for (int i = 0; i < all.Count; i++)
                        if (all[i] != null && all[i].thingIDNumber == pid) return all[i];
                    throw new VerbArgsException($"no pawn with id {pid} on the current map");
                }
                for (int i = 0; i < all.Count; i++)
                {
                    var p = all[i];
                    if (p == null) continue;
                    if (string.Equals(PawnSafe.Name(p), s, StringComparison.OrdinalIgnoreCase)) return p;
                    if (p.Name != null
                        && string.Equals(p.Name.ToStringFull, s, StringComparison.OrdinalIgnoreCase)) return p;
                }
                throw new VerbArgsException($"no pawn named '{s}' on the current map");
            }
            throw new VerbArgsException($"arg '{key}' must be a pawn id (number) or a name (string)");
        }

        // Thing handle by thingIDNumber — the same id every 2.x serializer
        // publishes, so a spawned thing round-trips: spawn, read the id out of
        // the result, hand it back here.
        public static Thing ThingById(Map map, int id)
        {
            var things = map.listerThings.AllThings;
            for (int i = 0; i < things.Count; i++)
                if (things[i] != null && things[i].thingIDNumber == id) return things[i];
            throw new VerbArgsException($"no spawned thing with id {id} on the current map");
        }

        // Faction handle: "player" | "none" | a FactionDef defName | a faction's
        // in-game Name | "hostile"/"ally"/"neutral" (first match, humanlike).
        public static Faction FactionArg(string s)
        {
            if (s == null) return null;
            switch (s.ToLowerInvariant())
            {
                case "player": case "colony": return Faction.OfPlayer;
                case "none": case "null": case "wild": return null;
                case "insect": return Faction.OfInsects;
                case "mechanoid": case "mech": return Faction.OfMechanoids;
            }
            var fd = DefDatabase<FactionDef>.GetNamedSilentFail(s);
            if (fd != null)
            {
                var byDef = Find.FactionManager.FirstFactionOfDef(fd);
                if (byDef != null) return byDef;
                throw new VerbArgsException(
                    $"FactionDef '{s}' exists but no faction of it is present in this world");
            }
            var all = Find.FactionManager.AllFactionsListForReading;
            for (int i = 0; i < all.Count; i++)
                if (all[i]?.Name != null && string.Equals(all[i].Name, s, StringComparison.OrdinalIgnoreCase))
                    return all[i];
            string want = s.ToLowerInvariant();
            if (want == "hostile" || want == "ally" || want == "neutral")
            {
                var player = Faction.OfPlayer;
                for (int i = 0; i < all.Count; i++)
                {
                    var f = all[i];
                    if (f == null || f.IsPlayer || f.Hidden || f.def == null || !f.def.humanlikeFaction) continue;
                    var kind = f.RelationKindWith(player);
                    if (want == "hostile" && kind == FactionRelationKind.Hostile) return f;
                    if (want == "ally" && kind == FactionRelationKind.Ally) return f;
                    if (want == "neutral" && kind == FactionRelationKind.Neutral) return f;
                }
                throw new VerbArgsException($"no visible humanlike faction is currently '{want}'");
            }
            throw new VerbArgsException(
                $"'{s}' is not a faction: use player|none|insect|mechanoid|hostile|ally|neutral, "
                + "a FactionDef defName, or a faction's name");
        }

        // Position with a default. dev verbs default to the colony anchor
        // rather than refusing, because a fixture that has to compute its own
        // coordinates is a fixture nobody writes.
        public static IntVec3 PosArg(Map map, VerbArgs args, string key)
        {
            object raw = args.Raw(key);
            return raw == null ? Anchor(map) : Positions.Resolve(map, raw);
        }

        // First free colonist, else map centre — pawn-fixture's AnchorCell, kept
        // identical on purpose so both stimulus layers stage in the same place.
        public static IntVec3 Anchor(Map map)
        {
            var colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
            return colonists.Count > 0 ? colonists[0].Position : map.Center;
        }

        // ------------------------------------------------------- provenance --
        // Rule 1. Returns the journal seq so the caller can stamp it into the
        // result: `dev.journal_seq` is the join key between a mutation and its
        // own provenance line, and it is what makes "journal carries complete
        // dev provenance" a checkable claim rather than an eyeball.
        //
        // Payload shape is the one journal-selftest and pawn-fixture already
        // write — {verb, step, target} — plus optional extras. JOURNAL.md's row
        // is `verb, step, target?, …` and consumers must ignore unknown fields,
        // so growing it here is additive by contract. 3.1 owns the type.
        public static long Emit(string verb, string step, string target,
            Dictionary<string, object> extra = null)
        {
            var payload = new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["step"] = step,
                ["target"] = target,
            };
            if (extra != null)
                foreach (var kv in extra)
                    if (!payload.ContainsKey(kv.Key)) payload[kv.Key] = kv.Value;
            int tick = 0;
            try { tick = Find.TickManager.TicksGame; } catch { }
            return AutoRimmer.Journal.Emit("dev", payload, tick);
        }

        // The block every dev result carries. `cheat` and `fog_exempt` are the
        // disclosure DESIGN asks for; `journal_seq` is the provenance join.
        //
        // Journal.Emit returns 0 when the writer is closed (no session file, or
        // Flush has closed it at shutdown). That is the one way the "every dev
        // verb is journalled" invariant can silently fail, so it is REPORTED
        // rather than left to look like a normal result — a fixture whose
        // provenance was never written is a fixture that cannot be trusted.
        public static Dictionary<string, object> Stamp(long seq)
        {
            var d = new Dictionary<string, object>
            {
                ["cheat"] = true,
                ["fog_exempt"] = true,
                ["journal_seq"] = seq,
            };
            if (seq == 0)
                d["provenance"] = "NOT WRITTEN — the journal writer is closed, so this mutation has "
                    + "no journal line. Treat any fixture built in this session as unprovenanced.";
            return d;
        }

        // The stamp for a call that deliberately mutated NOTHING (dry_run,
        // check_only). No journal line is owed, and saying so is not the same
        // as failing to write one.
        public static Dictionary<string, object> NoStamp()
            => new Dictionary<string, object>
            {
                ["cheat"] = true,
                ["fog_exempt"] = true,
                ["journal_seq"] = null,
                ["provenance"] = "not applicable — nothing was mutated",
            };

        // Adds the cells that were actually fogged when we touched them. Absent
        // when nothing was — so its PRESENCE is the signal, and a reader never
        // has to compare an empty list against a missing key.
        public static void NoteFog(Dictionary<string, object> stamp, Map map, IEnumerable<IntVec3> cells)
        {
            var fogged = new List<object>();
            foreach (var c in cells)
            {
                if (!c.IsValid || !c.InBounds(map)) continue;
                if (c.Fogged(map)) fogged.Add(Positions.Out(c));
                if (fogged.Count >= 16) break;
            }
            if (fogged.Count > 0)
            {
                stamp["fogged"] = fogged;
                stamp["fogged_note"] =
                    "acted on ground the colony has not explored; the player-facing verbs cannot see it "
                    + "(dev:unfog makes it observable)";
            }
        }

        public static Dictionary<string, object> Describe(Thing t)
        {
            if (t == null) return null;
            var d = new Dictionary<string, object>
            {
                ["id"] = t.thingIDNumber,
                ["def"] = t.def?.defName,
                ["label"] = t.LabelCap.ToString(),
                ["count"] = t.stackCount,
            };
            if (t.Stuff != null) d["stuff"] = t.Stuff.defName;
            if (t.Spawned) d["at"] = Positions.Out(t.Position);
            var q = t.TryGetComp<CompQuality>();
            if (q != null) d["quality"] = q.Quality.ToString();
            return d;
        }

        public static Dictionary<string, object> Describe(Pawn p)
        {
            if (p == null) return null;
            var d = new Dictionary<string, object>
            {
                ["id"] = p.thingIDNumber,
                ["name"] = PawnSafe.Name(p),
                ["kind"] = p.kindDef?.defName,
                ["faction"] = p.Faction?.Name,
                ["faction_def"] = p.Faction?.def?.defName,
                ["class"] = PawnSafe.Classify(p),
            };
            if (p.Spawned) d["at"] = Positions.Out(p.Position);
            return d;
        }
    }

    // =========================================================================
    // World-side dev verbs: spawning, destruction, fog, weather, incidents,
    // research, faction relations. The pawn-side half is DevPawnVerbs.cs and the
    // macro is StarterKit.cs.
    // =========================================================================
    public static class DevVerbs
    {
        // --------------------------------------------------------------------
        // dev:spawn-thing {def, stuff?, count?, pos?|stockpile?, quality?,
        //                  faction?, mode?, minified?, force?}
        //
        // Provenance: Verse/DebugThingPlaceHelper.DebugSpawn (stack default,
        // stuff, quality, faction, GenPlace.TryPlaceThing, Notify_DebugSpawned)
        // and Verse/DebugToolsSpawning.TryPlaceNearThing, which is the menu
        // entry that calls it.
        //
        // THREE DELIBERATE DEVIATIONS, each because a fixture must reproduce:
        //  * stuff defaults to GenStuff.DefaultStuffFor, not RandomStuffFor.
        //  * quality defaults to Normal, not GenerateQualityRandomEqualChance.
        //  * minification is OFF by default. DebugSpawn minifies anything
        //    Minifiable, so "spawn a bed" hands you a *minified* bed the
        //    serializers report as an item. `minified:true` restores it.
        // `stuff:"random"` / `quality:"random"` opt back into the game's roll.
        // --------------------------------------------------------------------
        [Verb("dev:spawn-thing")]
        public static object SpawnThing(VerbContext ctx)
        {
            const string V = "dev:spawn-thing";
            Dev.Gate(V);
            var map = Dev.CurrentMap(V);
            var a = ctx.Args;

            var def = Dev.Named<ThingDef>(a.StrReq("def"), "def");
            int count = a.Int("count", 1);
            if (count < 1 || count > 5000) throw new VerbArgsException("count must be 1..5000");
            bool force = a.Bool("force", false);

            // The game's own spawnability guard. allowPlayerBuildable:true is
            // the "Spawn thing with wipe mode" menu's setting, i.e. the most
            // permissive vanilla offers; anything it still rejects (corpses,
            // blueprints, frames, MinifiedThing, UnfinishedThing, SignalAction,
            // destroyOnDrop) spawns into a broken state, so it is refused with
            // the reason rather than silently producing garbage.
            if (!force && !DebugThingPlaceHelper.IsDebugSpawnable(def, allowPlayerBuildable: true))
                throw new VerbArgsException(
                    $"'{def.defName}' is not debug-spawnable (DebugThingPlaceHelper.IsDebugSpawnable "
                    + "rejects corpses, blueprints, frames, minified/unfinished things and "
                    + "destroy-on-drop things) — pass force:true to try anyway");

            ThingDef stuff = ResolveStuff(def, a.Str("stuff"));
            var quality = ResolveQuality(a.Str("quality"));
            bool minified = a.Bool("minified", false);
            string mode = a.Str("mode", "near");
            if (mode != "near" && mode != "direct")
                throw new VerbArgsException("mode must be 'near' or 'direct'");

            Faction faction = a.Has("faction") ? Dev.FactionArg(a.Str("faction")) : null;
            bool factionGiven = a.Has("faction");

            // Destination: an explicit stockpile beats an explicit position,
            // because "put the food where the colony stores food" is the thing
            // a starter kit actually wants and coordinates are the fallback.
            string stockpileNote = null;
            IntVec3 target;
            if (a.Has("stockpile") && !(a.Raw("stockpile") is bool bb && !bb))
            {
                object raw = a.Raw("stockpile");
                string wanted = raw as string;
                target = FindStoreCell(map, def, stuff, wanted, out stockpileNote);
                if (!target.IsValid)
                {
                    target = Dev.PosArg(map, a, "pos");
                    stockpileNote = (stockpileNote ?? "no storage accepted it")
                        + "; fell back to " + (a.Has("pos") ? "pos" : "the colony anchor");
                }
            }
            else
            {
                target = Dev.PosArg(map, a, "pos");
            }

            var spawned = new List<object>();
            var cells = new List<IntVec3>();
            int placed = 0, remaining = count;
            int stackLimit = Math.Max(1, def.stackLimit);
            var failures = new List<object>();

            while (remaining > 0)
            {
                int thisStack = Math.Min(remaining, stackLimit);
                remaining -= thisStack;

                Thing thing = ThingMaker.MakeThing(def, stuff);
                if (quality.HasValue) thing.TryGetComp<CompQuality>()?.SetQuality(quality.Value, ArtGenerationContext.Colony);
                if (minified && thing.def.Minifiable) thing = thing.MakeMinified();
                if (thing.def.CanHaveFaction)
                {
                    // DebugSpawn's rule, with an explicit override: insect
                    // cocoons go to OfInsects, everything else to the player
                    // (silent-fail so a def that cannot hold the player faction
                    // is not an error).
                    if (factionGiven) thing.SetFaction(faction);
                    else if (thing.def.building != null && thing.def.building.isInsectCocoon) thing.SetFaction(Faction.OfInsects);
                    else thing.SetFaction(Faction.OfPlayerSilentFail);
                }
                thing.stackCount = thisStack;

                Thing result = null;
                bool ok;
                if (mode == "direct" && def.category == ThingCategory.Building)
                {
                    // A building placed "near" fails against its own footprint;
                    // GenSpawn with a wipe mode is the path the game's own
                    // "Spawn thing with wipe mode" action uses. CanSpawnAt is
                    // checked first so a refusal is an error result rather than
                    // a vanilla Log.Error (which the journal would record as a
                    // red_error, breaking the standing zero-red-errors rule).
                    if (!GenSpawn.CanSpawnAt(def, target, map, thing.Rotation, canWipeEdifices: true))
                    {
                        ok = false;
                        failures.Add(new Dictionary<string, object>
                        {
                            ["at"] = Positions.Out(target),
                            ["reason"] = "GenSpawn.CanSpawnAt refused this cell for " + def.defName,
                            ["blocker"] = Blockers.Describe(target.GetFirstBuilding(map)),
                        });
                    }
                    else
                    {
                        result = GenSpawn.Spawn(thing, target, map, thing.Rotation, WipeMode.VanishOrMoveAside);
                        ok = result != null;
                    }
                }
                else
                {
                    ok = GenPlace.TryPlaceThing(thing, target, map,
                        mode == "direct" ? ThingPlaceMode.Direct : ThingPlaceMode.Near, out result);
                    if (!ok)
                        failures.Add(new Dictionary<string, object>
                        {
                            ["at"] = Positions.Out(target),
                            ["reason"] = "GenPlace.TryPlaceThing found no spot for " + def.defName
                                + " in mode " + mode,
                        });
                }

                if (!ok) break;
                try { result?.Notify_DebugSpawned(); } catch { }
                placed += thisStack;
                if (result != null)
                {
                    spawned.Add(Dev.Describe(result));
                    if (result.Spawned) cells.Add(result.Position);
                }
            }

            string label = def.defName + " x" + placed
                + (stuff != null ? " (" + stuff.defName + ")" : "")
                + " @ " + target.x + "," + target.z;
            long seq = Dev.Emit(V, "spawn-thing", label, new Dictionary<string, object>
            {
                ["args"] = new Dictionary<string, object>
                {
                    ["def"] = def.defName,
                    ["stuff"] = stuff?.defName,
                    ["count"] = count,
                    ["at"] = Positions.Out(target),
                },
                ["placed"] = placed,
                ["ids"] = IdsOf(spawned),
            });

            var stamp = Dev.Stamp(seq);
            Dev.NoteFog(stamp, map, cells);
            var data = new Dictionary<string, object>
            {
                ["def"] = def.defName,
                ["stuff"] = stuff?.defName,
                ["quality"] = quality?.ToString(),
                ["requested"] = count,
                ["placed"] = placed,
                ["at"] = Positions.Out(target),
                ["mode"] = mode,
                ["spawned"] = spawned,
                ["dev"] = stamp,
            };
            if (failures.Count > 0)
            {
                data["failed"] = failures;
                // GenPlace.TryPlaceThing's Near mode can place PART of a stack
                // and still report false, so `placed` is a floor once anything
                // has failed. Say so rather than letting a count be read as
                // exact.
                data["placed_is_floor"] = true;
            }
            if (stockpileNote != null) data["stockpile"] = stockpileNote;
            return data;
        }

        private static List<object> IdsOf(List<object> described)
        {
            var ids = new List<object>();
            foreach (var o in described)
                if (o is Dictionary<string, object> d && d.TryGetValue("id", out var v)) ids.Add(v);
            return ids;
        }

        private static ThingDef ResolveStuff(ThingDef def, string arg)
        {
            if (!def.MadeFromStuff)
            {
                if (arg != null && arg != "random")
                    throw new VerbArgsException($"'{def.defName}' is not made from stuff");
                return null;
            }
            if (arg == "random") return GenStuff.RandomStuffFor(def);
            if (arg == null) return GenStuff.DefaultStuffFor(def);
            var stuff = Dev.Named<ThingDef>(arg, "stuff");
            if (stuff.stuffProps == null || !stuff.stuffProps.CanMake(def))
                throw new VerbArgsException(
                    $"'{stuff.defName}' cannot make '{def.defName}' (stuffProps.CanMake said no)");
            return stuff;
        }

        private static QualityCategory? ResolveQuality(string arg)
        {
            if (arg == null) return QualityCategory.Normal;
            if (arg == "random") return QualityUtility.GenerateQualityRandomEqualChance();
            if (Enum.TryParse(arg, ignoreCase: true, out QualityCategory q)) return q;
            throw new VerbArgsException(
                "quality must be one of Awful|Poor|Normal|Good|Excellent|Masterwork|Legendary, or \"random\"");
        }

        // A storage cell that will actually ACCEPT this thing. Deliberately not
        // StoreUtility.TryFindBestBetterStoreCellFor: with carrier == null that
        // helper dereferences `carrier.PositionHeld` for any thing that is not
        // yet spawned (StoreUtility.TryFindBestBetterStoreCellForWorker), and a
        // thing we are about to spawn never is. The two tests it ends in —
        // StorageSettings.AllowedToAccept and StoreUtility.IsGoodStoreCell —
        // both handle carrier == null and an unspawned thing correctly
        // (IsGoodStoreCell guards every carrier use; NoStorageBlockersIn only
        // asks the probe CanStackWith), so the walk is done here over the same
        // AllGroupsListInPriorityOrder that helper uses.
        //
        // The probe is ABANDONED rather than Destroy()ed on the way out.
        // Thing.Destroy Log.Errors for a def with destroyable:false, and a red
        // error raised by a storage LOOKUP would be an own goal against the
        // standing zero-red-errors invariant. An unspawned, unheld Thing is
        // garbage; it costs one thingIDNumber.
        private static IntVec3 FindStoreCell(Map map, ThingDef def, ThingDef stuff, string wanted, out string note)
        {
            note = null;
            Thing probe;
            try { probe = ThingMaker.MakeThing(def, stuff); }
            catch { note = "could not build a probe thing to test storage against"; return IntVec3.Invalid; }

            var groups = map.haulDestinationManager.AllGroupsListInPriorityOrder;
            var names = new List<string>();
            for (int g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                if (group?.Settings == null || group.parent == null) continue;
                string label = group.parent.SlotYielderLabel();
                if (label != null) names.Add(label);
                if (wanted != null && !string.Equals(label, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                if (!group.Settings.AllowedToAccept(probe)) continue;
                var cellsList = group.CellsList;
                for (int i = 0; i < cellsList.Count; i++)
                {
                    if (StoreUtility.IsGoodStoreCell(cellsList[i], map, probe, null, Faction.OfPlayer))
                    {
                        note = "placed into storage '" + label + "'";
                        return cellsList[i];
                    }
                }
            }
            note = wanted != null
                ? $"no storage named '{wanted}' accepted {def.defName}"
                    + (names.Count > 0 ? " (storage present: " + string.Join(", ", names.ToArray()) + ")" : " (no storage on this map)")
                : (names.Count > 0
                    ? "no storage accepted " + def.defName
                    : "no storage on this map (3.2 owns zone creation; scatter is the fallback)");
            return IntVec3.Invalid;
        }

        // --------------------------------------------------------------------
        // dev:spawn-pawn {kind?, faction?, pos?, count?, name?, age?,
        //                 violence_capable?, downed?}
        //
        // Provenance: Verse/DebugToolsSpawning.SpawnPawn (GeneratePawn ->
        // GenSpawn.Spawn -> PostPawnSpawn) and its PostPawnSpawn helper, whose
        // lord-joining is reproduced below — without it a spawned raider stands
        // still forever, which makes every combat fixture a lie.
        //
        // DEVIATION: forceGenerateNewPawn:true. The DebugAction lets the
        // generator redress an existing world pawn, which makes a fixture's
        // roster depend on world history. A fixture wants a fresh pawn.
        // --------------------------------------------------------------------
        [Verb("dev:spawn-pawn")]
        public static object SpawnPawn(VerbContext ctx)
        {
            const string V = "dev:spawn-pawn";
            Dev.Gate(V);
            var map = Dev.CurrentMap(V);
            var a = ctx.Args;

            var kind = Dev.Named<PawnKindDef>(a.Str("kind", "Colonist"), "kind");
            Faction faction = a.Has("faction")
                ? Dev.FactionArg(a.Str("faction"))
                : FactionUtility.DefaultFactionFrom(kind.defaultFactionDef);
            var at = Dev.PosArg(map, a, "pos");
            int count = a.Int("count", 1);
            if (count < 1 || count > 20) throw new VerbArgsException("count must be 1..20");
            bool violence = a.Bool("violence_capable", false);
            bool downed = a.Bool("downed", false);
            double age = a.Num("age", 0);
            // DETERMINISTIC BY DEFAULT. The DebugAction spawns at the mouse
            // cell; `spread` is our substitute for a mouse, and 0 means "exactly
            // at pos" so a fixture puts its pawns in the same cells every run.
            // Raise it for a group that should not be stacked on one tile.
            // Pawns may legally share a cell, so 0 is not a conflict.
            int spread = a.Int("spread", 0);
            if (spread < 0 || spread > 30) throw new VerbArgsException("spread must be 0..30");
            var names = a.Has("name") ? NameList(a) : null;
            if (names != null && names.Count > 0 && names.Count != count)
                throw new VerbArgsException($"'name' has {names.Count} entries but count is {count}");

            var made = new List<object>();
            var cells = new List<IntVec3>();
            for (int i = 0; i < count; i++)
            {
                var req = new PawnGenerationRequest(kind, faction,
                    PawnGenerationContext.NonPlayer, map.Tile,
                    forceGenerateNewPawn: true,
                    mustBeCapableOfViolence: violence);
                var pawn = PawnGenerator.GeneratePawn(req);
                if (age > 0)
                    pawn.ageTracker.AgeBiologicalTicks = (long)(age * 3600000L);
                if (names != null && names.Count > i) Rename(pawn, names[i]);

                var cell = spread > 0 ? CellFinder.RandomClosewalkCellNear(at, map, spread) : at;
                // A pawn spawned inside a wall is a fixture that reads as
                // working and behaves as broken; fall back to the nearest
                // walkable cell rather than trusting the argument.
                if (!cell.Standable(map)) cell = CellFinder.RandomClosewalkCellNear(at, map, Math.Max(3, spread));
                GenSpawn.Spawn(pawn, cell, map);
                PostPawnSpawn(pawn, map);
                if (downed && !pawn.Downed)
                    HealthUtility.DamageUntilDowned(pawn, allowBleedingWounds: false);

                made.Add(Dev.Describe(pawn));
                cells.Add(pawn.Position);
            }

            long seq = Dev.Emit(V, "spawn-pawn",
                kind.defName + " x" + made.Count + " (" + (faction?.Name ?? "no faction") + ")",
                new Dictionary<string, object>
                {
                    ["args"] = new Dictionary<string, object>
                    {
                        ["kind"] = kind.defName,
                        ["faction"] = faction?.def?.defName,
                        ["at"] = Positions.Out(at),
                        ["count"] = count,
                    },
                    ["ids"] = IdsOf(made),
                });

            var stamp = Dev.Stamp(seq);
            Dev.NoteFog(stamp, map, cells);
            return new Dictionary<string, object>
            {
                ["kind"] = kind.defName,
                ["faction"] = faction?.Name,
                ["faction_def"] = faction?.def?.defName,
                ["at"] = Positions.Out(at),
                ["spread"] = spread,
                ["pawns"] = made,
                ["dev"] = stamp,
            };
        }

        private static List<string> NameList(VerbArgs a)
        {
            var raw = a.Raw("name");
            if (raw is string s) return new List<string> { s };
            return a.StrList("name");
        }

        // "First" | "First Last" | "First 'Nick' Last". Deterministic names are
        // what make a scripted fixture assertable — `dev:set-skill {pawn:"Ada"}`
        // beats threading an id through every step.
        private static void Rename(Pawn pawn, string spec)
        {
            if (string.IsNullOrEmpty(spec) || pawn.Name == null) return;
            string first = spec, nick = null, last = "";
            int q1 = spec.IndexOf('\'');
            int q2 = q1 >= 0 ? spec.IndexOf('\'', q1 + 1) : -1;
            if (q1 > 0 && q2 > q1)
            {
                nick = spec.Substring(q1 + 1, q2 - q1 - 1);
                first = spec.Substring(0, q1).Trim();
                last = spec.Substring(q2 + 1).Trim();
            }
            else
            {
                int sp = spec.IndexOf(' ');
                if (sp > 0) { first = spec.Substring(0, sp).Trim(); last = spec.Substring(sp + 1).Trim(); }
            }
            pawn.Name = new NameTriple(first, nick ?? first, last);
        }

        // Verse/DebugToolsSpawning.PostPawnSpawn, reproduced: join the nearest
        // same-faction lord or make a LordJob_DefendPoint, then face south. A
        // pawn with no lord has no group AI at all.
        private static void PostPawnSpawn(Pawn pawn, Map map)
        {
            try
            {
                if (pawn.Spawned && pawn.Faction != null && pawn.Faction != Faction.OfPlayer)
                {
                    Lord lord = null;
                    var same = map.mapPawns.SpawnedPawnsInFaction(pawn.Faction);
                    for (int i = 0; i < same.Count; i++)
                    {
                        if (same[i] == pawn) continue;
                        var l = same[i].GetLord();
                        if (l != null) { lord = l; break; }
                    }
                    if (lord == null || !lord.CanAddPawn(pawn))
                        lord = LordMaker.MakeNewLord(pawn.Faction, new LordJob_DefendPoint(pawn.Position), map);
                    if (lord != null && lord.LordJob.CanAutoAddPawns) lord.AddPawn(pawn);
                }
                pawn.Rotation = Rot4.South;
            }
            catch (Exception e)
            {
                // A lord failure must not orphan a spawned pawn mid-fixture.
                Log.Warning("[AutoRimmer] dev:spawn-pawn PostPawnSpawn: " + e.Message);
            }
        }

        // --------------------------------------------------------------------
        // dev:destroy {thing|things|def+near+radius, mode?}
        // Provenance: Verse/Thing.Destroy(DestroyMode) — the same call
        // DebugActionsMisc.DestroyAllThings/DestroyClutter make in a loop.
        // Default mode Vanish: no leavings, no death letter, no corpse.
        // --------------------------------------------------------------------
        [Verb("dev:destroy")]
        public static object Destroy(VerbContext ctx)
        {
            const string V = "dev:destroy";
            Dev.Gate(V);
            var map = Dev.CurrentMap(V);
            var a = ctx.Args;

            string modeArg = a.Str("mode", "vanish");
            if (!Enum.TryParse(modeArg, ignoreCase: true, out DestroyMode mode))
                throw new VerbArgsException(
                    "mode must be one of vanish|killfinalize|deconstruct|refund|cancel (DestroyMode)");

            var targets = new List<Thing>();
            if (a.Has("thing")) targets.Add(Dev.ThingById(map, a.IntReq("thing")));
            if (a.Has("things"))
            {
                if (!(a.Raw("things") is List<object> list))
                    throw new VerbArgsException("'things' must be an array of thingIDNumbers");
                foreach (var o in list)
                {
                    if (!(o is double d)) throw new VerbArgsException("'things' must be an array of numbers");
                    targets.Add(Dev.ThingById(map, (int)d));
                }
            }
            if (a.Has("def"))
            {
                var def = Dev.Named<ThingDef>(a.Str("def"), "def");
                var near = Dev.PosArg(map, a, "near");
                int radius = a.Int("radius", 12);
                if (radius < 0 || radius > 200) throw new VerbArgsException("radius must be 0..200");
                int cap = a.Int("cap", 200);
                var of = map.listerThings.ThingsOfDef(def);
                for (int i = 0; i < of.Count && targets.Count < cap; i++)
                {
                    var t = of[i];
                    if (t == null || !t.Spawned) continue;
                    if (radius > 0 && (t.Position - near).LengthHorizontalSquared > radius * radius) continue;
                    targets.Add(t);
                }
            }
            if (targets.Count == 0)
                throw new VerbArgsException("dev:destroy needs 'thing', 'things' or 'def' (+ optional near/radius)");

            var destroyed = new List<object>();
            var cells = new List<IntVec3>();
            foreach (var t in targets)
            {
                if (t == null || t.Destroyed) continue;
                var described = Dev.Describe(t);
                if (t.Spawned) cells.Add(t.Position);
                try { t.Destroy(mode); destroyed.Add(described); }
                catch (Exception e)
                {
                    described["error"] = e.Message;
                    destroyed.Add(described);
                }
            }

            long seq = Dev.Emit(V, "destroy", destroyed.Count + " thing(s), mode " + mode,
                new Dictionary<string, object> { ["ids"] = IdsOf(destroyed), ["mode"] = mode.ToString() });
            var stamp = Dev.Stamp(seq);
            Dev.NoteFog(stamp, map, cells);
            return new Dictionary<string, object>
            {
                ["mode"] = mode.ToString(),
                ["destroyed"] = destroyed,
                ["count"] = destroyed.Count,
                ["dev"] = stamp,
            };
        }

        // --------------------------------------------------------------------
        // dev:unfog {rect?|around?+radius?|all?}
        // Provenance: Verse/DebugToolsGeneral.UnfogRect (fogGrid.Unfog per cell)
        // and Verse/FogGrid.ClearAllFog.
        //
        // NOT IN THE SPEC BODY — proposed and resolved on git-bug f166fb9. The
        // argument: fog is respected by the ENTIRE player-facing surface, so a
        // fixture staged in fog is invisible to every 2.x serializer and
        // unusable by every player verb. Without this, "spawned things visible
        // to 2.x serializers" is not reliably achievable for anything staged
        // away from the colony, and the failure is silent.
        // --------------------------------------------------------------------
        [Verb("dev:unfog")]
        public static object Unfog(VerbContext ctx)
        {
            const string V = "dev:unfog";
            Dev.Gate(V);
            var map = Dev.CurrentMap(V);
            var a = ctx.Args;

            int before = CountFogged(map);
            string scope;
            if (a.Bool("all", false))
            {
                map.fogGrid.ClearAllFog();
                scope = "all";
            }
            else
            {
                CellRect rect;
                if (a.Has("rect"))
                {
                    if (!(a.Raw("rect") is List<object> r) || r.Count != 4
                        || !(r[0] is double rx) || !(r[1] is double rz)
                        || !(r[2] is double rw) || !(r[3] is double rh))
                        throw new VerbArgsException("rect must be [x,z,w,h]");
                    rect = new CellRect((int)rx, (int)rz, Math.Max(1, (int)rw), Math.Max(1, (int)rh));
                }
                else
                {
                    var around = Dev.PosArg(map, a, "around");
                    int radius = a.Int("radius", 20);
                    if (radius < 1 || radius > 200) throw new VerbArgsException("radius must be 1..200");
                    rect = CellRect.CenteredOn(around, radius);
                }
                rect = rect.ClipInsideMap(map);
                foreach (var c in rect) map.fogGrid.Unfog(c);
                scope = $"[{rect.minX},{rect.minZ},{rect.Width},{rect.Height}]";
            }
            int after = CountFogged(map);

            long seq = Dev.Emit(V, "unfog", scope,
                new Dictionary<string, object> { ["cleared"] = before - after });
            return new Dictionary<string, object>
            {
                ["scope"] = scope,
                ["cells_cleared"] = before - after,
                ["fogged_before"] = before,
                ["fogged_after"] = after,
                ["dev"] = Dev.Stamp(seq),
                ["note"] = "the player-facing verbs can now see this ground; "
                    + "unfogging is a CHEAT and changes what the colony is deemed to have explored",
            };
        }

        private static int CountFogged(Map map)
        {
            int n = 0;
            var size = map.Size;
            for (int x = 0; x < size.x; x++)
                for (int z = 0; z < size.z; z++)
                    if (map.fogGrid.IsFogged(new IntVec3(x, 0, z))) n++;
            return n;
        }

        // --------------------------------------------------------------------
        // dev:weather {def}
        // Provenance: Verse/DebugActionsMisc.ChangeWeather —
        // `Find.CurrentMap.weatherManager.TransitionTo(localWeather)`, verbatim.
        // --------------------------------------------------------------------
        [Verb("dev:weather")]
        public static object Weather(VerbContext ctx)
        {
            const string V = "dev:weather";
            Dev.Gate(V);
            var map = Dev.CurrentMap(V);
            var def = Dev.Named<WeatherDef>(ctx.Args.StrReq("def"), "def");
            var from = map.weatherManager.curWeather;
            map.weatherManager.TransitionTo(def);

            long seq = Dev.Emit(V, "weather", (from?.defName ?? "?") + " -> " + def.defName);
            return new Dictionary<string, object>
            {
                ["from"] = from?.defName,
                ["to"] = def.defName,
                ["label"] = def.LabelCap.ToString(),
                ["dev"] = Dev.Stamp(seq),
                // WeatherManager.TransitionTo sets lastWeather/curWeather and
                // zeroes curWeatherAge; the visual lerp runs over
                // WeatherManager.TransitionTicks (4000) but the mechanical
                // effects (rainRate, windSpeedFactor, accuracyMultiplier) are
                // lerped too — so the FULL effect only lands ~4000 ticks in.
                ["note"] = "transition is lerped over ~4000 ticks (WeatherManager.TransitionTicks); "
                    + "advance before asserting on rain/wind-derived state",
            };
        }

        // --------------------------------------------------------------------
        // dev:finish-research {project|all}
        // Provenance: RimWorld/ResearchManager.FinishProject (which recurses
        // into prerequisites and grants techprints itself) and
        // Verse/DebugActionsMisc.FinishAllResearch (DebugSetAllProjectsFinished
        // + EntityCodex.debug_UnhideAllResearch).
        //
        // DEVIATION: doCompletionLetter:false. The default is TRUE, and a
        // completion letter mid-fixture is exactly the 1.7 wedge — a letter that
        // can stack a force-pausing window and halt every later advance. A
        // fixture does not want to be congratulated.
        // --------------------------------------------------------------------
        [Verb("dev:finish-research")]
        public static object FinishResearch(VerbContext ctx)
        {
            const string V = "dev:finish-research";
            Dev.Gate(V);
            Dev.CurrentMap(V);
            var a = ctx.Args;
            var mgr = Find.ResearchManager;
            var finished = new List<object>();
            string target;

            if (a.Bool("all", false) || a.Str("project") == "all")
            {
                int before = CountFinished();
                mgr.DebugSetAllProjectsFinished();
                try { if (Find.EntityCodex != null) Find.EntityCodex.debug_UnhideAllResearch = true; }
                catch { }
                int after = CountFinished();
                target = "all (" + (after - before) + " newly finished)";
                finished.Add(new Dictionary<string, object> { ["all"] = true, ["newly_finished"] = after - before });
            }
            else
            {
                var projects = new List<ResearchProjectDef>();
                if (a.Has("projects"))
                    foreach (var s in a.StrList("projects"))
                        projects.Add(Dev.Named<ResearchProjectDef>(s, "projects"));
                else
                    projects.Add(Dev.Named<ResearchProjectDef>(a.StrReq("project"), "project"));

                foreach (var proj in projects)
                {
                    bool was = proj.IsFinished;
                    if (!was) mgr.FinishProject(proj, doCompletionDialog: false, researcher: null, doCompletionLetter: false);
                    finished.Add(new Dictionary<string, object>
                    {
                        ["project"] = proj.defName,
                        ["label"] = proj.LabelCap.ToString(),
                        ["was_finished"] = was,
                        ["is_finished"] = proj.IsFinished,
                        // FinishProject recurses into prerequisites, so one call
                        // can finish several projects. Report the chain length
                        // rather than implying one.
                        ["prerequisites"] = PrereqNames(proj),
                    });
                }
                target = string.Join(",", ProjectNames(projects).ToArray());
            }

            long seq = Dev.Emit(V, "finish-research", target,
                new Dictionary<string, object> { ["finished"] = finished });
            return new Dictionary<string, object>
            {
                ["finished"] = finished,
                ["total_finished"] = CountFinished(),
                ["dev"] = Dev.Stamp(seq),
                ["note"] = "completion letter suppressed (doCompletionLetter:false) so a fixture "
                    + "cannot stack a force-pausing window mid-run (spec 1.7)",
            };
        }

        private static List<string> ProjectNames(List<ResearchProjectDef> ps)
        {
            var n = new List<string>();
            foreach (var p in ps) n.Add(p.defName);
            return n;
        }

        private static List<object> PrereqNames(ResearchProjectDef proj)
        {
            var n = new List<object>();
            if (proj.prerequisites != null)
                foreach (var p in proj.prerequisites) n.Add(p.defName);
            return n;
        }

        private static int CountFinished()
        {
            int n = 0;
            foreach (var p in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
                if (p.IsFinished) n++;
            return n;
        }

        // --------------------------------------------------------------------
        // dev:incident {def, points?, faction?, strategy?, arrival?, at?,
        //               drop_radius?, trader_kind?, count?, letter?,
        //               recount_wealth?, check_only?}
        //
        // Provenance: Verse/DebugActionsIncidents — GetIncidentDebugAction (the
        // DefaultParmsNow + forced + pointsScaleable-regenerate chain),
        // ExecuteRaidWithPoints, ExecuteRaidWithSpecifics (faction/points/
        // strategy/arrival), ExecuteDropPodRaidAtLocation (SpecificDropDebug +
        // dropInRadius + spawnCenter), DoTradeCaravanSpecific (traderKind), and
        // RecalculateThreatPoints (wealthWatcher.ForceRecount).
        //
        // WHY THE WEALTH RECOUNT IS ON BY DEFAULT. A points-scaleable incident's
        // default points come off the storyteller, which reads
        // map.wealthWatcher — and the wealth watcher recounts on a slow tick.
        // A fixture that spawns a starter kit and immediately fires a scaled
        // raid gets points computed against the colony's wealth BEFORE the kit,
        // silently. ForceRecount is one call and makes the raid match the
        // colony you just built.
        //
        // THE 1.7 WEDGE. A letter-bearing incident can open a force-pausing
        // modal, and from then on `advance` halts `reason:"dialog"` and
        // OpenAutomaticLetters is dead for the rest of the session (JOURNAL.md).
        // Three things here address it: `letter:false` sets parms.sendLetter and
        // suppresses the letter entirely; `letter_bearing` reports the cheap
        // static prediction (does def.letterDef's letterClass derive from
        // ChoiceLetter/LetterWithTimeout); and `force_pause_after` reports what
        // ACTUALLY went up, measured, in TimeDriver.ForcePausePayload's shape.
        // --------------------------------------------------------------------
        [Verb("dev:incident")]
        public static object Incident(VerbContext ctx)
        {
            const string V = "dev:incident";
            Dev.Gate(V);
            var map = Dev.CurrentMap(V);
            var a = ctx.Args;

            var def = Dev.Named<IncidentDef>(a.StrReq("def"), "def");
            if (!def.TargetAllowed(map))
                throw new VerbArgsException(
                    $"IncidentDef '{def.defName}' does not allow a Map target "
                    + "(targetTags: " + TagNames(def) + ")");

            int iterations = a.Int("count", 1);
            if (iterations < 1 || iterations > 10) throw new VerbArgsException("count must be 1..10");
            bool recount = a.Bool("recount_wealth", true);
            if (recount) { try { map.wealthWatcher.ForceRecount(); } catch { } }

            bool hasPoints = a.Has("points");
            IncidentParms parms;
            if (def.pointsScaleable && !hasPoints)
            {
                // The DebugAction's exact fallback: regenerate through the
                // storyteller's own OnOffCycle/RandomMain comp so the parms
                // carry a storyteller-shaped point budget rather than
                // DefaultParmsNow's flat one.
                var comp = MainStorytellerComp();
                parms = comp != null ? comp.GenerateParms(def.category, map)
                                     : StorytellerUtility.DefaultParmsNow(def.category, map);
            }
            else
            {
                parms = StorytellerUtility.DefaultParmsNow(def.category, map);
            }
            parms.target = map;
            parms.forced = true;
            if (hasPoints) parms.points = (float)a.NumReq("points");
            if (a.Has("faction")) parms.faction = Dev.FactionArg(a.Str("faction"));
            if (a.Has("strategy")) parms.raidStrategy = Dev.Named<RaidStrategyDef>(a.Str("strategy"), "strategy");
            if (a.Has("arrival")) parms.raidArrivalMode = Dev.Named<PawnsArrivalModeDef>(a.Str("arrival"), "arrival");
            if (a.Has("trader_kind")) parms.traderKind = Dev.Named<TraderKindDef>(a.Str("trader_kind"), "trader_kind");
            if (a.Has("at"))
            {
                // The drop-pod-at-location shape: an explicit spawnCenter is
                // only honoured by an arrival mode that reads it, so default to
                // the debug one the game uses for exactly this.
                parms.spawnCenter = Positions.Resolve(map, a.Raw("at"));
                parms.dropInRadius = a.Int("drop_radius", 4);
                if (parms.raidArrivalMode == null) parms.raidArrivalMode = PawnsArrivalModeDefOf.SpecificDropDebug;
            }
            bool sendLetter = a.Bool("letter", true);
            parms.sendLetter = sendLetter;

            bool canFire;
            try { canFire = def.Worker.CanFireNow(parms); } catch { canFire = false; }

            var data = new Dictionary<string, object>
            {
                ["def"] = def.defName,
                ["category"] = def.category?.defName,
                ["points"] = Math.Round(parms.points, 1),
                ["faction"] = parms.faction?.Name,
                ["faction_def"] = parms.faction?.def?.defName,
                ["strategy"] = parms.raidStrategy?.defName,
                ["arrival"] = parms.raidArrivalMode?.defName,
                ["points_scaleable"] = def.pointsScaleable,
                ["can_fire_now"] = canFire,
                ["wealth_recount"] = recount,
                ["send_letter"] = sendLetter,
                ["letter_bearing"] = LetterBearing(def),
            };

            if (a.Bool("check_only", false))
            {
                // No mutation, so no journal line: the provenance rule covers
                // state-mutating actions, and journalling a dry run would put
                // noise in the one file a post-mortem has to trust.
                data["fired"] = false;
                data["check_only"] = true;
                data["dev"] = Dev.NoStamp();
                return data;
            }

            int lettersBefore = LetterCount();
            int fired = 0;
            var errors = new List<object>();
            for (int i = 0; i < iterations; i++)
            {
                try { if (def.Worker.TryExecute(parms)) fired++; }
                catch (Exception e) { errors.Add(e.Message); break; }
            }

            var stack = Find.WindowStack;
            var newLetters = NewLetters(lettersBefore);

            long seq = Dev.Emit(V, "incident",
                def.defName + " x" + fired + "/" + iterations
                + " @" + Math.Round(parms.points, 0) + "pts"
                + (parms.faction != null ? " (" + parms.faction.Name + ")" : ""),
                new Dictionary<string, object>
                {
                    ["args"] = new Dictionary<string, object>
                    {
                        ["def"] = def.defName,
                        ["points"] = Math.Round(parms.points, 1),
                        ["faction"] = parms.faction?.def?.defName,
                        ["strategy"] = parms.raidStrategy?.defName,
                        ["arrival"] = parms.raidArrivalMode?.defName,
                    },
                    ["fired"] = fired,
                });

            data["fired"] = fired > 0;
            data["fired_count"] = fired;
            data["requested"] = iterations;
            data["letters_opened"] = newLetters;
            if (errors.Count > 0) data["errors"] = errors;
            if (stack != null && stack.WindowsForcePause)
            {
                data["force_pause_after"] = TimeDriver.ForcePausePayload(stack);
                data["wedge_warning"] =
                    "a force-pausing modal is UP: every later advance halts reason:\"dialog\" and "
                    + "LetterStack.OpenAutomaticLetters is dead until it is dismissed (spec 1.7; "
                    + "3.5 owns dismissal). Re-run with letter:false to fire this incident silently.";
            }
            if (fired == 0)
                data["note"] = canFire
                    ? "the worker refused despite CanFireNow (no arrival spot / no eligible group) — advance and retry"
                    : "CanFireNow said no before the attempt; check faction, points and season";
            data["dev"] = Dev.Stamp(seq);
            return data;
        }

        private static string TagNames(IncidentDef def)
        {
            if (def.targetTags == null) return "none";
            var n = new List<string>();
            foreach (var t in def.targetTags) n.Add(t.defName);
            return string.Join(",", n.ToArray());
        }

        private static StorytellerComp MainStorytellerComp()
        {
            var comps = Find.Storyteller?.storytellerComps;
            if (comps == null) return null;
            for (int i = 0; i < comps.Count; i++)
                if (comps[i] is StorytellerComp_OnOffCycle || comps[i] is StorytellerComp_RandomMain)
                    return comps[i];
            return null;
        }

        // Static prediction, cheap: does this incident's own letter def open a
        // ChoiceLetter (which is a LetterWithTimeout, which is what stacks a
        // force-pausing window when it opens)? A worker can still raise a letter
        // the def does not declare — quest offers do — so this is a HINT, and
        // `force_pause_after` is the measurement.
        private static object LetterBearing(IncidentDef def)
        {
            try
            {
                var ld = def.letterDef;
                if (ld?.letterClass == null) return false;
                if (typeof(ChoiceLetter).IsAssignableFrom(ld.letterClass)) return "choice";
                if (typeof(LetterWithTimeout).IsAssignableFrom(ld.letterClass)) return "timeout";
                return "standard";
            }
            catch { return false; }
        }

        private static int LetterCount()
        {
            try { return Find.LetterStack?.LettersListForReading?.Count ?? 0; }
            catch { return 0; }
        }

        private static List<object> NewLetters(int before)
        {
            var added = new List<object>();
            try
            {
                var letters = Find.LetterStack?.LettersListForReading;
                if (letters == null) return added;
                for (int i = before; i < letters.Count && added.Count < 10; i++)
                {
                    var l = letters[i];
                    if (l == null) continue;
                    added.Add(new Dictionary<string, object>
                    {
                        ["label"] = l.Label.ToString(),
                        ["def"] = l.def?.defName,
                        ["type"] = l.GetType().Name,
                        ["choice"] = l is ChoiceLetter,
                    });
                }
            }
            catch { }
            return added;
        }

        // --------------------------------------------------------------------
        // dev:faction-goodwill {faction, goodwill?|delta?|relation?}
        // Provenance: RimWorld/Faction.TryAffectGoodwillWith and
        // Faction.SetRelationDirect; menu equivalent is
        // Verse/DebugActionsMisc.SetFactionRelations.
        //
        // NOT IN THE SPEC BODY — proposed and resolved on git-bug f166fb9. The
        // argument: dev:incident's most useful argument is `faction`, and a raid
        // from a faction that is not hostile is a different incident (RaidEnemy
        // vs RaidFriendly, per DebugActionsIncidents.DoRaid). Staging a hostile
        // or an ally is a prerequisite of the raid fixture, and Factions is the
        // first mod under test.
        //
        // DEVIATION: canSendMessage/canSendHostilityLetter false. Both default
        // TRUE, and a hostility letter is a 1.7 wedge candidate.
        // --------------------------------------------------------------------
        [Verb("dev:faction-goodwill")]
        public static object FactionGoodwill(VerbContext ctx)
        {
            const string V = "dev:faction-goodwill";
            Dev.Gate(V);
            Dev.CurrentMap(V);
            var a = ctx.Args;

            var other = Dev.FactionArg(a.StrReq("faction"))
                ?? throw new VerbArgsException("faction must name a real faction, not 'none'");
            if (other.IsPlayer) throw new VerbArgsException("cannot set the player faction's relation to itself");
            var player = Faction.OfPlayer;

            int before = other.GoodwillWith(player);
            var kindBefore = other.RelationKindWith(player);
            string how;

            if (a.Has("relation"))
            {
                string want = a.Str("relation").ToLowerInvariant();
                FactionRelationKind kind;
                switch (want)
                {
                    case "hostile": kind = FactionRelationKind.Hostile; break;
                    case "neutral": kind = FactionRelationKind.Neutral; break;
                    case "ally": kind = FactionRelationKind.Ally; break;
                    default: throw new VerbArgsException("relation must be hostile|neutral|ally");
                }
                other.SetRelationDirect(player, kind, canSendHostilityLetter: false, reason: "AutoRimmer dev fixture");
                how = "relation=" + kind;
            }
            else if (a.Has("goodwill"))
            {
                int want = a.IntReq("goodwill");
                if (want < -100 || want > 100) throw new VerbArgsException("goodwill must be -100..100");
                other.TryAffectGoodwillWith(player, want - before, canSendMessage: false, canSendHostilityLetter: false);
                how = "goodwill=" + want;
            }
            else if (a.Has("delta"))
            {
                int delta = a.IntReq("delta");
                other.TryAffectGoodwillWith(player, delta, canSendMessage: false, canSendHostilityLetter: false);
                how = "delta=" + delta;
            }
            else
            {
                throw new VerbArgsException("dev:faction-goodwill needs 'relation', 'goodwill' or 'delta'");
            }

            int after = other.GoodwillWith(player);
            long seq = Dev.Emit(V, "faction-goodwill",
                other.Name + " " + kindBefore + "(" + before + ") -> " + other.RelationKindWith(player) + "(" + after + ")",
                new Dictionary<string, object> { ["faction"] = other.def?.defName, ["how"] = how });

            return new Dictionary<string, object>
            {
                ["faction"] = other.Name,
                ["faction_def"] = other.def?.defName,
                ["goodwill_before"] = before,
                ["goodwill_after"] = after,
                ["relation_before"] = kindBefore.ToString(),
                ["relation_after"] = other.RelationKindWith(player).ToString(),
                ["hostile"] = other.HostileTo(player),
                ["dev"] = Dev.Stamp(seq),
                ["note"] = "hostility letter and message suppressed; goodwill can be clamped by the "
                    + "faction def's own permanent-enemy / natural-goodwill rules, so read the after values",
            };
        }
    }
}
