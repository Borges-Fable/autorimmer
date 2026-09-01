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

        // ------------------------------------------------------- forbidding --
        // THE ARRIVAL FLAG. A real colony start does not hand the player usable
        // gear: `RimWorld/ScenPart_PlayerPawnsArriveMethod.DoDropPods` ends in
        // `DropPodUtility.DropThingGroupsNear(..., forbid: true, ...)`, whose
        // forbid branch is exactly
        //
        //     thingsGroup[num].SetForbidden(value: true, warnOnFail: false);
        //
        // and Core's own tutorial puts `UnforbidStartingResources` immediately
        // after the stockpile steps (`MakeStockpile` -> `EndStockpileDesignating`
        // -> `UnforbidStartingResources`, which `BuildRoomWalls` then waits on).
        // So `unforbid` is a step every player takes
        // and a fixture that skips it hands the agent an affordance no player
        // has (DESIGN decisions log, 2026-08-30, the fog entry's argument).
        //
        // NOT EVERY THING CAN HOLD THE FLAG, AND THE GAME LEANS ON THAT.
        // `RimWorld/ForbidUtility.SetForbidden` needs a `ThingWithComps` whose
        // comps include `CompForbiddable`, and the comp is declared per def:
        // `ResourceBase` has it (so resources, food, medicine, weapons, apparel
        // and `MinifiedThing` do) while `BuildingBase` does NOT — `Bed`, which
        // the `medical` preset spawns, carries no `CompForbiddable` anywhere in
        // its BedWithQualityBase -> BedBase -> FurnitureBase -> BuildingBase
        // chain, and neither does any pawn. On such a thing the call is
        // `Log.Error("Tried to SetForbidden on non-Forbiddable Thing ...")` when
        // `warnOnFail` is true — a red_error the journal records and the bench's
        // zero-red-errors rule will not have — and a SILENT NO-OP when it is
        // false, after which `ForbidUtility.IsForbidden(Thing, Faction)` (which
        // reads `ThingWithComps.compForbiddable` and returns false for a null
        // comp) answers "not forbidden" forever. `DoDropPods` puts the arriving
        // PAWN in the same group and forbids it too; `warnOnFail: false` is what
        // makes that harmless.
        //
        // NAMED DEVIATION FROM `DropThingGroupsNear`: it forbids BLIND, we ask
        // first. The predicate is the game's own —
        // `RimWorld/Designator_Forbid.CanDesignateThing`: `def.category ==
        // ThingCategory.Item` AND a `CompForbiddable` — because the SAME
        // category test gates the REMEDY, `Designator_Unforbid
        // .CanDesignateThing`, which the shipped `unforbid` verb drives
        // (DesignationVerbs.ForbidCore). Forbidding a Building-with-comp (a
        // door, a shelf) would leave the agent an obstacle no player verb in
        // this mod can clear — a lock with no key. The fixture and the remedy
        // have to be the same set.
        //
        // ORDERING, also a deviation: `DropThingGroupsNear` forbids BEFORE it
        // places, we forbid the placed result. `CompForbiddable` overrides
        // neither `AllowStackWith` nor `PreAbsorbStack`, so a forbidden stack
        // absorbed into an unforbidden one loses the flag entirely
        // (`ThingWithComps.TryAbsorbStack` keeps the ABSORBER's comps). The game
        // can ignore that because a pod lands on open ground; a starter kit
        // aims at a stockpile, where merging is the normal case.
        public const string ForbidGate =
            "RimWorld/Designator_Forbid.CanDesignateThing (category==Item and a CompForbiddable) — "
            + "the same predicate the shipped `unforbid` verb's Designator_Unforbid twin uses, so "
            + "everything forbidden here is clearable by `unforbid`";

        // Returns null when the flag took, else the reason it could not — never
        // throws, never Log.Errors. The caller REPORTS the reason; a silent
        // no-op is the failure mode this whole comment exists to prevent.
        public static string Forbid(Thing t)
        {
            if (t == null) return "gone before it could be forbidden";
            if (t.def == null) return "no def";
            if (t.def.category != ThingCategory.Item)
                return "category is " + t.def.category + ", not Item — Designator_Forbid refuses it "
                    + "and Designator_Unforbid could not clear it again";
            if (t.TryGetComp<CompForbiddable>() == null)
                return "'" + t.def.defName + "' has no CompForbiddable; SetForbidden would be a "
                    + "silent no-op (with warnOnFail:true it would be a red error)";
            try { t.SetForbidden(value: true, warnOnFail: false); }
            catch (Exception e) { return "SetForbidden threw: " + e.Message; }
            // Read back rather than trust the write. This is the same field read
            // every observer uses for `forbidden` (ThingVerbs), so the answer
            // here is the answer a `things` read will give.
            bool now;
            try { now = t.IsForbidden(Faction.OfPlayer); }
            catch (Exception e) { return "set, but IsForbidden threw on read-back: " + e.Message; }
            return now ? null : "SetForbidden did not take";
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
            // Present only when true, the same "presence is the signal"
            // convention NoteFog uses — a reader never compares an empty value
            // against a missing key. Pure field read (ForbidUtility.IsForbidden
            // reads ThingWithComps.compForbiddable), so no observer rule is bent.
            try { if (t.IsForbidden(Faction.OfPlayer)) d["forbidden"] = true; } catch { }
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
        //                  faction?, mode?, minified?, force?, forbid?}
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
        //
        // `forbid` DEFAULTS TO FALSE, AND THAT IS A DECISION, NOT AN OVERSIGHT
        // (git-bug 091e3f0). `dev:starter-kit` forbids by default because it
        // models an ARRIVAL and the game's arrival path
        // (`ScenPart_PlayerPawnsArriveMethod.DoDropPods`) passes `forbid: true`.
        // This verb models the DEBUG SPAWNER, and the debug spawner it is
        // written against — `Verse/DebugThingPlaceHelper.DebugSpawn` — contains
        // no forbidding at all: a thing appears, usable, exactly as clicking
        // "Spawn thing" in the dev palette produces it. Two fictions, two
        // defaults. The arg exists so a caller staging an arrival one item at a
        // time gets the kit's behaviour without the kit, and so the kit reuses
        // this handler rather than reimplementing placement (StarterKit's
        // REUSE-NOT-REIMPLEMENTATION rule). See Dev.Forbid for what the flag
        // costs on a def with no CompForbiddable.
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
            bool forbid = a.Bool("forbid", false);

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
            var forbiddenIds = new List<object>();
            var notForbiddable = new List<object>();
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
                        var no = WhyNoSpawn(def, target, map, thing.Rotation);
                        string why = no?.Reason
                            ?? "GenSpawn.CanSpawnAt refused this cell for " + def.defName;
                        failures.Add(FailureRow(map, target, why, no));
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
                    {
                        // Near searches GenRadial's pattern out to
                        // PlaceNearMaxRadialCells and keeps the best
                        // PlaceSpotQuality (Verse/GenPlace.cs
                        // TryFindPlaceSpotNear / PlaceSpotQualityAt), and EVERY
                        // candidate is gated by the same GenSpawn.CanSpawnAt. So
                        // the target cell's own refusal is the representative
                        // reason when there is one, and its absence is itself
                        // the finding: the anchor would have taken it and the
                        // whole disc was still full.
                        var branch = WhyNoSpawn(def, target, map, thing.Rotation);
                        string why = "GenPlace.TryPlaceThing found no spot for " + def.defName
                            + " in mode " + mode
                            + (branch != null
                                ? "; at the target cell: " + branch.Reason
                                : "; the target cell itself would take it, so every cell in the "
                                  + "radial search was refused (stack limits, a storage that "
                                  + "declines it, or an unreachable room)");
                        // The row's `reason` is the Near sentence; the BLOCKER's
                        // `refused` stays the branch's own clause, which is what
                        // 1.6i in accept/s13-mod-surface.py keys on.
                        var row = FailureRow(map, target, branch?.Reason ?? why, branch);
                        row["reason"] = why;
                        failures.Add(row);
                    }
                }

                if (!ok) break;
                try { result?.Notify_DebugSpawned(); } catch { }
                placed += thisStack;
                if (result != null)
                {
                    // Forbid BEFORE Describe, so the echoed `forbidden` is the
                    // state the caller will read back, not the state before the
                    // flag. `result` is the PLACED thing — GenPlace may have
                    // merged our stack into an existing one and handed that back
                    // — which is the whole reason we forbid here rather than
                    // pre-placement the way DropThingGroupsNear does.
                    if (forbid)
                    {
                        string why = Dev.Forbid(result);
                        if (why == null) forbiddenIds.Add(result.thingIDNumber);
                        else notForbiddable.Add(new Dictionary<string, object>
                        {
                            ["id"] = result.thingIDNumber,
                            ["def"] = result.def?.defName,
                            ["reason"] = why,
                        });
                    }
                    spawned.Add(Dev.Describe(result));
                    if (result.Spawned) cells.Add(result.Position);
                }
            }

            string label = def.defName + " x" + placed
                + (stuff != null ? " (" + stuff.defName + ")" : "")
                + " @ " + target.x + "," + target.z
                + (forbid ? " [forbidden x" + forbiddenIds.Count + "]" : "")
                // M1 finding A: `SimpleResearchBench x0 (WoodLog) @ 123,117` is
                // indistinguishable from a success at a glance, and the label is
                // what a human scanning JOURNAL.md reads. Say it refused.
                + (failures.Count > 0 ? " REFUSED" : "");
            var extra = new Dictionary<string, object>
            {
                ["args"] = new Dictionary<string, object>
                {
                    ["def"] = def.defName,
                    ["stuff"] = stuff?.defName,
                    ["count"] = count,
                    ["at"] = Positions.Out(target),
                    ["forbid"] = forbid,
                },
                ["placed"] = placed,
                ["ids"] = IdsOf(spawned),
            };
            // Only when asked — an absent key means the caller never asked for
            // the flag, which is not the same as asking and getting nothing.
            if (forbid)
            {
                extra["forbidden"] = forbiddenIds.Count;
                if (notForbiddable.Count > 0) extra["not_forbiddable"] = notForbiddable.Count;
            }
            // M1 finding A, the defect proper: the RESPONSE carried `failed` and
            // the JOURNAL ROW did not, so seq 66's `placed: 0, ids: []` was the
            // only surviving record of the refusal and it had no reason in it.
            // The row is the durable record; it carries the reasons.
            if (failures.Count > 0)
            {
                extra["failed"] = failures;
                extra["placed_is_floor"] = true;
            }
            long seq = Dev.Emit(V, "spawn-thing", label, extra);

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
            if (forbid)
            {
                data["forbid"] = new Dictionary<string, object>
                {
                    ["requested"] = true,
                    ["gate"] = Dev.ForbidGate,
                    ["forbidden_stacks"] = forbiddenIds.Count,
                    ["ids"] = forbiddenIds,
                    ["not_forbiddable"] = notForbiddable,
                    ["remedy"] = forbiddenIds.Count > 0
                        ? "unforbid {\"things\":[…the ids above…]} — or a rect over them"
                        : null,
                };
            }
            return data;
        }

        // WHICH TIER of the placement gate refused. Published on every failure
        // row as `cell_role`, so a reader knows the blocker is off-footprint
        // without doing the arithmetic (git-bug 8b4839f).
        //
        // 8b4839f named four — footprint, interaction, terrain, bounds. Walking
        // Verse/GenSpawn.cs CanSpawnAt against the 1.6 source turned up two more
        // branches it did not know about, and they are kept apart rather than
        // folded in, because each is a different remedy for the agent:
        // `blocks-interaction` is cleared by moving OUR building, never by
        // clearing the cell it names, and `def` names no cell at all.
        internal static class SpawnTier
        {
            // A cell of the occupied rect is off the map.
            public const string Bounds = "bounds";
            // A cell the thing itself would occupy: not walkable, or held by an
            // occupant that SpawningWipes cannot destroy.
            public const string Footprint = "footprint";
            // The footprint's terrain cannot carry the def.
            public const string Terrain = "terrain";
            // OUR interaction cell is blocked (GenConstruct.InteractionCellStandable).
            public const string Interaction = "interaction";
            // Our footprint would cover a NEIGHBOUR's interaction cell
            // (GenConstruct.NotBlockingAnyInteractionCells). The cell named is
            // inside our own rect; the thing named is the neighbour.
            public const string BlocksInteraction = "blocks-interaction";
            // ThingDef.CanSpawnAt's own override said no. There is no cell.
            public const string Def = "def";
            // GenSpawn.CanSpawnAt accepted the target cell and GenPlace still
            // found nowhere to put the stack: no single cell refused, so there
            // is no tier to name. Covers both the Near radial search and a
            // Direct placement the cell's item limit turned away.
            public const string PlaceSearch = "place-search";
        }

        // What WhyNoSpawn now answers with. `Cell` is the cell that REFUSED,
        // which is the target only by coincidence; `Thing` is what the game's
        // own predicate found there, when it found anything.
        internal sealed class NoSpawn
        {
            public readonly string Tier;
            public readonly IntVec3 Cell;
            public readonly Thing Thing;
            public readonly string Reason;

            public NoSpawn(string tier, IntVec3 cell, Thing thing, string reason)
            {
                Tier = tier;
                Cell = cell;
                Thing = thing;
                Reason = reason;
            }
        }

        // The failure row both spawn branches publish. `at` stays the cell the
        // CALLER asked for — an echo of its own argument, and unchanged. `cell`
        // and `cell_role` are new: the cell that actually refused and which tier
        // of the gate it belongs to. `blocker` is now asked about THAT cell, and
        // is handed the thing the re-walk already identified.
        private static Dictionary<string, object> FailureRow(Map map, IntVec3 target, string why, NoSpawn no)
        {
            var cell = no != null ? no.Cell : target;
            return new Dictionary<string, object>
            {
                ["at"] = Positions.Out(target),
                ["reason"] = why,
                ["cell"] = Positions.Out(cell),
                ["cell_role"] = no != null ? no.Tier : SpawnTier.PlaceSearch,
                // Was Blockers.Describe(target.GetFirstBuilding(map)), which is
                // null for every refusal that is not an edifice — i.e. for most
                // of them (M1 finding A) — and then Blockers.At(map, TARGET),
                // which described the wrong cell (8b4839f).
                ["blocker"] = Blockers.At(map, cell, why, no?.Thing),
            };
        }

        // WHICH branch of GenSpawn.CanSpawnAt refused, WHICH CELL refused, and
        // WHAT is standing on it.
        //
        // Verse/GenSpawn.cs CanSpawnAt is one boolean over seven distinct
        // conditions — ThingDef.CanSpawnAt, IntVec3.InBounds, IntVec3.Walkable,
        // an occupant that SpawningWipes would have to destroy and cannot,
        // GenConstruct.CanBuildOnTerrain (buildings only),
        // GenConstruct.InteractionCellStandable and
        // GenConstruct.NotBlockingAnyInteractionCells — and a caller that only
        // knows "false" cannot say anything an agent can act on. Re-walked here
        // in the same order so the refusal names its own cause (M1 finding A).
        // (An eighth, `!canWipeEdifices && map.edificeGrid[item] != null`, is
        // unreachable from here: dev:spawn-thing passes canWipeEdifices:true.)
        //
        // THE STRUCT, NOT A STRING (git-bug 8b4839f). This used to return the
        // sentence alone, and the caller then asked Blockers.At about the cell
        // it had ASKED for. Those are different cells whenever the refusal is
        // off-footprint, and the bench proved it: bench 20260901T121508 refused
        // a HiTechResearchBench with "Interaction spot is blocked by granite."
        // while the blocker named a WoodLog on the target cell with
        // removal:"none" — the exact opposite of the truth, since the granite
        // one cell south clears by MINING. Every branch below therefore carries
        // the cell it objected to, the thing it found there when it found one,
        // and its tier.
        //
        // The ORDER here is vanilla's with one deliberate change: vanilla tests
        // the opaque `ThingDef.CanSpawnAt` first, and this walks the concrete,
        // actionable branches first and names that one last as the residual. A
        // refusal is a refusal either way; the difference is only which sentence
        // the caller gets, and "the cell is not walkable" beats "the def said no".
        //
        // Returns null when CanSpawnAt is in fact satisfied, so a Near-mode
        // failure never gets handed a fabricated reason.
        //
        // Two of vanilla's checks are keyed on the CENTRE cell inside a loop over
        // the occupied rect (`!c.Walkable(map)` and the `c.GetThingList(map)`
        // wipe scan, Verse/GenSpawn.cs CanSpawnAt) — a quirk, and reproduced
        // rather than corrected, because the job is to name the branch the game
        // took. `InBounds` really is per-cell and is walked as such; so is the
        // terrain check, whose own loop lives inside CanBuildOnTerrain and is
        // re-walked here to name the offending cell rather than the centre.
        //
        // Read-only: grid lookups, def flags and terrain reads. The one
        // non-obvious call is GenConstruct.CanBuildOnTerrain, which is what
        // Designator_Build runs under the cursor every frame.
        private static NoSpawn WhyNoSpawn(ThingDef def, IntVec3 c, Map map, Rot4 rot)
        {
            try
            {
                var rect = GenAdj.OccupiedRect(c, rot, def.Size);
                foreach (var item in rect)
                    if (!item.InBounds(map))
                        return new NoSpawn(SpawnTier.Bounds, item, null,
                            def.defName + " at " + c.x + "," + c.z
                            + " would extend past the map edge (" + item.x + "," + item.z + ")");
                if (!c.Walkable(map))
                    return new NoSpawn(SpawnTier.Footprint, c, c.GetEdifice(map),
                        "the cell is not walkable");
                foreach (var t in c.GetThingList(map))
                    if (t?.def != null
                        && GenSpawn.SpawningWipes(def, t.def, ignoreDestroyable: true)
                        && !t.def.destroyable)
                        return new NoSpawn(SpawnTier.Footprint, c, t,
                            "'" + t.def.defName + "' holds the cell and cannot be destroyed to make room");
                if (def.category == ThingCategory.Building
                    && !GenConstruct.CanBuildOnTerrain(def, c, map, rot))
                {
                    var bad = FirstBadTerrainCell(def, c, map, rot);
                    return new NoSpawn(SpawnTier.Terrain, bad, null,
                        "terrain '" + map.terrainGrid.TerrainAt(bad)?.defName + "' cannot carry " + def.defName);
                }
                if (def.HasSingleOrMultipleInteractionCells)
                {
                    // Returns an AcceptanceReport, so the game's own sentence is
                    // available and is preferred over one of ours — the same
                    // rule Blockers.Classify follows. It names the thing's LABEL
                    // and nothing else, so the cell and the thing come from
                    // repeating the walk it did.
                    var report = GenConstruct.InteractionCellStandable(def, c, rot, map);
                    if (!report.Accepted)
                    {
                        FirstBlockedInteractionCell(def, c, map, rot, out var cell, out var culprit);
                        return new NoSpawn(SpawnTier.Interaction, cell, culprit,
                            string.IsNullOrEmpty(report.Reason)
                                ? def.defName + "'s interaction cell is not standable"
                                : report.Reason);
                    }
                }
                // THE BRANCH THAT WAS MISSING, and it is not hypothetical: this
                // is the second half of the interaction-spot rule — not "is our
                // own spot free" but "would our footprint cover a NEIGHBOUR's".
                // Without it a WouldBlockInteractionSpot refusal fell through to
                // the residual below and was reported as "ThingDef.CanSpawnAt
                // refused", naming the wrong branch and no cell at all.
                var neighbours = GenConstruct.NotBlockingAnyInteractionCells(def, c, rot, map);
                if (!neighbours.Accepted)
                {
                    FirstCoveredInteractionCell(def, c, map, rot, rect, out var cell, out var neighbour);
                    return new NoSpawn(SpawnTier.BlocksInteraction, cell, neighbour,
                        string.IsNullOrEmpty(neighbours.Reason)
                            ? def.defName + " would cover a neighbour's interaction cell"
                            : neighbours.Reason);
                }
                return GenSpawn.CanSpawnAt(def, c, map, rot)
                    ? null
                    // ThingDef.CanSpawnAt is `return true;` on the base class, so
                    // this is always a def's own override talking and there is no
                    // cell to point at — the target is published as the locus,
                    // and `def` is the tier that says so.
                    : new NoSpawn(SpawnTier.Def, c, null,
                        "ThingDef.CanSpawnAt refused " + def.defName + " here at rotation " + rot.ToStringWord());
            }
            catch (Exception e)
            {
                return new NoSpawn(SpawnTier.Def, c, null,
                    "GenSpawn.CanSpawnAt refused this cell (" + e.GetType().Name
                    + " while naming which branch)");
            }
        }

        // The first cell of the occupied rect whose terrain cannot carry the
        // def, walked the way RimWorld/GenConstruct.CanBuildOnTerrain walks it:
        // the rect clipped inside the map, the affordance from
        // ThingUtility.GetTerrainAffordanceNeed, and IntVec3.GetAffordances
        // (which prefers TerrainGrid.FoundationAt over TerrainAt). stuffDef is
        // null here because GenSpawn.CanSpawnAt itself passes no stuff — a
        // vanilla quirk that matters for useStuffTerrainAffordance defs, and
        // reproduced rather than corrected, since the job is to name the branch
        // the game took.
        //
        // Falls back to the centre when nothing fails, which happens when
        // CanBuildOnTerrain refused on one of its two other clauses (a crater
        // def over preventCraters terrain, or a TerrainDef blueprint already on
        // the cell whose affordances do not cover the need).
        private static IntVec3 FirstBadTerrainCell(ThingDef def, IntVec3 c, Map map, Rot4 rot)
        {
            var need = def.GetTerrainAffordanceNeed();
            if (need == null) return c;
            var rect = GenAdj.OccupiedRect(c, rot, def.Size).ClipInsideMap(map);
            foreach (var item in rect)
                if (!item.GetAffordances(map).Contains(need)) return item;
            return c;
        }

        // GenConstruct.InteractionCellStandable's own walk, repeated for the
        // cell and the thing its AcceptanceReport does not carry: out of bounds
        // first, then the two predicates in the game's order — an occupant that
        // is not Standable or is this very def, then the same test against an
        // occupant's entityDefToBuild (an unstandable blueprint standing where
        // the pawn would).
        //
        // ThingUtility.InteractionCellsWhenAt returns the SHARED static
        // tmpInteractionCells list, cleared and refilled on every call, so it is
        // copied before anything else can call it. Read-only otherwise:
        // ThingsListAtFast is Map.thingGrid's stored list.
        private static void FirstBlockedInteractionCell(ThingDef def, IntVec3 c, Map map, Rot4 rot,
            out IntVec3 cell, out Thing culprit)
        {
            cell = c;
            culprit = null;
            var cells = new List<IntVec3>(ThingUtility.InteractionCellsWhenAt(def, c, rot, map));
            for (int i = 0; i < cells.Count; i++)
            {
                var ic = cells[i];
                if (!ic.InBounds(map)) { cell = ic; return; }
                var list = map.thingGrid.ThingsListAtFast(ic);
                for (int j = 0; j < list.Count; j++)
                {
                    var t = list[j];
                    if (t?.def == null) continue;
                    if (t.def.passability != Traversability.Standable || t.def == def)
                    {
                        cell = ic; culprit = t; return;
                    }
                    var built = t.def.entityDefToBuild;
                    if (built != null && (built.passability != Traversability.Standable || built == def))
                    {
                        cell = ic; culprit = t; return;
                    }
                }
            }
            // Nothing matched: the cells list itself is the answer, so point at
            // the first one rather than at the centre.
            if (cells.Count > 0) cell = cells[0];
        }

        // GenConstruct.NotBlockingAnyInteractionCells' own walk, repeated for
        // the cell and the thing. The refusing CELL is the neighbour's
        // interaction cell that our footprint would sit on — inside our rect,
        // which is why `cell_role` is what tells a reader this is a neighbour's
        // complaint and not our own. The THING is the neighbour.
        //
        // Vanilla resolves a Blueprint's or a Frame's entityDefToBuild before
        // asking about interaction cells, so a planned bench blocks a placement
        // exactly as a built one does; reproduced here.
        private static void FirstCoveredInteractionCell(ThingDef def, IntVec3 c, Map map, Rot4 rot,
            CellRect rect, out IntVec3 cell, out Thing neighbour)
        {
            cell = c;
            neighbour = null;
            foreach (var item in GenAdj.CellsAdjacentCardinal(c, rot, def.Size))
            {
                if (!item.InBounds(map)) continue;
                var list = item.GetThingList(map);
                for (int i = 0; i < list.Count; i++)
                {
                    var t = list[i];
                    if (t?.def == null) continue;
                    ThingDef other;
                    if (t is Blueprint || t is Frame) other = t.def.entityDefToBuild as ThingDef;
                    else other = t.def;
                    if (other == null) continue;
                    if (!other.HasSingleOrMultipleInteractionCells
                        || (def.passability == Traversability.Standable && def != other)) continue;
                    var cells = new List<IntVec3>(
                        ThingUtility.InteractionCellsWhenAt(other, t.Position, t.Rotation, map));
                    for (int j = 0; j < cells.Count; j++)
                        if (rect.Contains(cells[j])) { cell = cells[j]; neighbour = t; return; }
                }
            }
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
        // dev:incident {def, target?, points?, faction?, strategy?, arrival?,
        //               at?, drop_radius?, trader_kind?, count?, letter?,
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
        // colony you just built. A WORLD target recounts every map, not just the
        // current one: RimWorld.Planet/World.PlayerWealthForStoryteller SUMS
        // every map's and every caravan's, so one map's stale watcher makes the
        // world's points stale too.
        //
        // WORLD-TARGETED INCIDENTS, AND WHY THE DEF PICKS THE TARGET. Measured
        // on the bench 2026-08-31: `dev:incident {def:"GiveQuest_Random"}` was
        // refused — the verb always passed a Map, and GiveQuest_Random's
        // targetTags are World-only, so ~123 of spec 3.5's checks could not be
        // staged. RimWorld/IncidentParms.target is an IIncidentTarget, not a
        // Map, and Verse/Map and RimWorld.Planet/World both implement it with
        // DISJOINT tag sets — Map yields the Map_* tags, World yields exactly
        // IncidentTargetTagDefOf.World — so RimWorld/IncidentDef.TargetAllowed
        // (targetTags.Intersect(target.IncidentTargetTags()).Any()) already
        // answers "which of the two does this def want".
        //
        // NOT the palette's chooser, and the game supplies the one we DO want.
        // Verse/DebugActionsIncidents.GetTarget picks by CAMERA: world view
        // selected -> the selected world object or Find.World, otherwise
        // Find.CurrentMap. That is the right rule for a human holding a mouse
        // and a useless one for a headless caller, who would have to know to
        // flip the view before asking for a quest. The def-driven chooser is
        // the game's own, in RimWorld/StorytellerComp — the local function
        // GetDefaultTarget inside DebugTablesIncidentChances, which is verbatim
        // "map if TargetAllowed(Find.CurrentMap), else world if
        // TargetAllowed(Find.World), else null". ResolveIncidentTarget below is
        // that function plus a refusal that says why. `target:"map"|"world"`
        // overrides it when a caller needs to.
        //
        // Everything downstream follows the choice — StorytellerUtility
        // .DefaultParmsNow(category, target) and StorytellerComp.GenerateParms
        // both take the IIncidentTarget, which is the palette's own route
        // (GetIncidentDebugAction passes incidentParms.target, not the map).
        //
        // THE 1.7 WEDGE. A letter-bearing incident can open a force-pausing
        // modal, and from then on `advance` halts `reason:"dialog"` and
        // OpenAutomaticLetters is dead for the rest of the session (JOURNAL.md).
        // Three things here address it: `letter:false` sets parms.sendLetter and
        // suppresses the letter entirely; `letter_bearing` reports the cheap
        // static prediction (does def.letterDef's letterClass derive from
        // ChoiceLetter/LetterWithTimeout); and `force_pause_after` reports what
        // ACTUALLY went up, measured, in TimeDriver.ForcePausePayload's shape.
        //
        // `letter:false` IS NOT UNIVERSAL, and the world-targeted quest
        // incidents are the exception that matters. parms.sendLetter is honoured
        // only by workers that read it; RimWorld/IncidentWorker_GiveQuest.
        // GiveQuest never looks at it — it calls QuestUtility
        // .SendLetterQuestAvailable(quest) whenever the quest is not hidden and
        // questDef.sendAvailableLetter, full stop. So GiveQuest_Random posts its
        // letter even with letter:false. `force_pause_after` is still the
        // measurement, and 3.5 owns dismissal.
        // --------------------------------------------------------------------
        [Verb("dev:incident")]
        public static object Incident(VerbContext ctx)
        {
            const string V = "dev:incident";
            Dev.Gate(V);
            var a = ctx.Args;

            var def = Dev.Named<IncidentDef>(a.StrReq("def"), "def");

            // THE TARGET IS RESOLVED FIRST, because everything below is a
            // function of it: the wealth recount, the parms the storyteller
            // generates, and whether `at` means anything at all. Note that the
            // "needs a current map" refusal is no longer unconditional — a
            // World-targeted incident does not need one, and Dev.CurrentMap's
            // message would have been a lie for it.
            var map = Find.CurrentMap;
            IIncidentTarget world = Find.World;
            string targetNote;
            bool onMap;
            IIncidentTarget target = ResolveIncidentTarget(
                def, a.Str("target", "auto"), map, world, out onMap, out targetNote);

            int iterations = a.Int("count", 1);
            if (iterations < 1 || iterations > 10) throw new VerbArgsException("count must be 1..10");
            bool recount = a.Bool("recount_wealth", true);
            int recounted = recount ? RecountWealth(onMap ? map : null) : 0;

            bool hasPoints = a.Has("points");
            IncidentParms parms;
            if (def.pointsScaleable && !hasPoints)
            {
                // The DebugAction's exact fallback: regenerate through the
                // storyteller's own OnOffCycle/RandomMain comp so the parms
                // carry a storyteller-shaped point budget rather than
                // DefaultParmsNow's flat one.
                var comp = MainStorytellerComp();
                parms = comp != null ? comp.GenerateParms(def.category, target)
                                     : StorytellerUtility.DefaultParmsNow(def.category, target);
            }
            else
            {
                parms = StorytellerUtility.DefaultParmsNow(def.category, target);
            }
            parms.target = target;
            parms.forced = true;
            if (hasPoints) parms.points = (float)a.NumReq("points");
            if (a.Has("faction")) parms.faction = Dev.FactionArg(a.Str("faction"));
            if (a.Has("strategy")) parms.raidStrategy = Dev.Named<RaidStrategyDef>(a.Str("strategy"), "strategy");
            if (a.Has("arrival")) parms.raidArrivalMode = Dev.Named<PawnsArrivalModeDef>(a.Str("arrival"), "arrival");
            if (a.Has("trader_kind")) parms.traderKind = Dev.Named<TraderKindDef>(a.Str("trader_kind"), "trader_kind");
            if (a.Has("at"))
            {
                // A cell is a MAP coordinate. Refuse rather than silently drop
                // it: IncidentParms.spawnCenter on a world-targeted incident is
                // read by nobody, and Positions.Resolve has no map to resolve
                // against anyway.
                if (!onMap)
                    throw new VerbArgsException(
                        $"arg 'at' names a cell on a map, but IncidentDef '{def.defName}' fires at "
                        + "the World (targetTags: " + TagNames(def) + ")");
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
                ["target"] = onMap ? "map" : "world",
                ["target_tags"] = TagNames(def),
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
            if (recount) data["wealth_recount_maps"] = recounted;
            if (targetNote != null) data["target_note"] = targetNote;

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
                + (parms.faction != null ? " (" + parms.faction.Name + ")" : "")
                + " -> " + (onMap ? "map" : "world"),
                new Dictionary<string, object>
                {
                    ["args"] = new Dictionary<string, object>
                    {
                        ["def"] = def.defName,
                        ["target"] = onMap ? "map" : "world",
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

        // The def picks the target. RimWorld/StorytellerComp's GetDefaultTarget
        // (local to DebugTablesIncidentChances) verbatim — map first, world
        // second, null third — with the null arm turned into a refusal that says
        // which candidate failed and why, and with `want` able to override.
        //
        // MAP-FIRST IS THE GAME'S OWN PREFERENCE, not a tiebreak we invented, and
        // in vanilla the tie cannot arise: RimWorld/IncidentDef.ConfigErrors
        // reports "allows world target type along with other targets. World
        // targeting incidents should only target the world." for any def that
        // tags both. A modded def that does it anyway gets the map and is TOLD
        // so (target_note), because silently preferring one of two legal targets
        // is the kind of thing that costs an afternoon.
        //
        // `onMap` rather than a type test at the call sites: Verse/Map is not
        // the only Map-tagged IIncidentTarget in principle, and the two things
        // downstream that need a Map (the wealth recount, `at`) need the
        // variable, not the interface.
        private static IIncidentTarget ResolveIncidentTarget(
            IncidentDef def, string want, Map map, IIncidentTarget world,
            out bool onMap, out string note)
        {
            onMap = false;
            note = null;

            // Verse/Map.IncidentTargetTags delegates to info.parent (the
            // MapParent), so a parentless map yields NO tags and TargetAllowed
            // is false for every def — hence the null-guard AND the allowed
            // check, not one standing in for the other.
            bool mapOk = map != null && def.TargetAllowed(map);
            bool worldOk = world != null && def.TargetAllowed(world);

            switch (want)
            {
                case "map":
                    if (map == null)
                        throw new VerbArgsException("target:\"map\" was asked for but there is no current map");
                    if (!mapOk)
                        throw new VerbArgsException(
                            $"IncidentDef '{def.defName}' does not allow a Map target "
                            + "(targetTags: " + TagNames(def) + ")");
                    onMap = true;
                    return map;

                case "world":
                    if (world == null)
                        throw new VerbArgsException("target:\"world\" was asked for but there is no world");
                    if (!worldOk)
                        throw new VerbArgsException(
                            $"IncidentDef '{def.defName}' does not allow a World target "
                            + "(targetTags: " + TagNames(def) + ")");
                    return world;

                case "auto":
                    break;

                default:
                    throw new VerbArgsException(
                        $"arg 'target' must be \"auto\", \"map\" or \"world\" (got '{want}'); "
                        + "\"auto\" reads the def's own targetTags and is the default");
            }

            if (mapOk)
            {
                onMap = true;
                if (worldOk)
                    note = "this def's targetTags allow BOTH a Map and the World, which "
                         + "IncidentDef.ConfigErrors calls malformed (\"World targeting incidents "
                         + "should only target the world\"). Picked the map, as the game's own "
                         + "StorytellerComp.GetDefaultTarget does. Pass target:\"world\" to override.";
                return map;
            }
            if (worldOk) return world;

            // Neither. Name targetTags — that is the diagnostic that told us
            // GiveQuest_Random was World-only in the first place — and say what
            // each candidate did, because "no current map" and "the def wants a
            // tag neither candidate has" are different operator problems.
            string mapWhy = map == null
                ? "there is no current map"
                : "the current map offers " + MapTagNames(map);
            string worldWhy = world == null
                ? "there is no world"
                : "the world offers only " + IncidentTargetTagDefOf.World.defName;
            throw new VerbArgsException(
                $"IncidentDef '{def.defName}' allows neither a Map nor a World target "
                + "(targetTags: " + TagNames(def) + ") — " + mapWhy + "; " + worldWhy);
        }

        private static string MapTagNames(Map map)
        {
            try
            {
                var n = new List<string>();
                foreach (var t in map.IncidentTargetTags()) if (t != null) n.Add(t.defName);
                return n.Count == 0 ? "no target tags at all (no MapParent)" : string.Join(",", n.ToArray());
            }
            catch { return "unreadable target tags"; }
        }

        // A Map target reads its own wealthWatcher; the World SUMS every map's
        // (RimWorld.Planet/World.PlayerWealthForStoryteller), so `null` here
        // means "the target is the world, recount them all". Returns how many
        // watchers actually recounted, so a caller can see the world path did
        // more than one. Verse/DebugActionsIncidents.RecalculateThreatPoints is
        // the single-map original.
        private static int RecountWealth(Map only)
        {
            int n = 0;
            if (only != null)
            {
                try { only.wealthWatcher.ForceRecount(); n++; } catch { }
                return n;
            }
            var maps = Find.Maps;
            if (maps == null) return 0;
            for (int i = 0; i < maps.Count; i++)
            {
                if (maps[i] == null) continue;
                try { maps[i].wealthWatcher.ForceRecount(); n++; } catch { }
            }
            return n;
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

            // A permanent enemy's relation cannot move by ANY route —
            // TryAffectGoodwillWith refuses (CanChangeGoodwillFor) and
            // SetRelationDirect would be overridden — so refuse up front with
            // the reason instead of returning an unchanged after-value the
            // caller has to diff to notice.
            if (other.def != null && other.def.permanentEnemy)
                throw new VerbArgsException(
                    $"'{other.Name}' ({other.def.defName}) is a permanent enemy; "
                    + "its relation to the colony cannot be changed by any means");

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
                if (other.HasGoodwill && player.HasGoodwill)
                {
                    // SetRelationDirect Log.Errors AND no-ops for goodwill-using
                    // factions (Faction.SetRelationDirect's first branch) — a red
                    // error from a fixture verb breaches the standing invariant,
                    // and this was found live in session 5's acceptance. For
                    // these factions the relation IS the goodwill band, so drive
                    // the goodwill to the band's far value instead.
                    int target = kind == FactionRelationKind.Hostile ? -100
                        : kind == FactionRelationKind.Ally ? 100 : 0;
                    other.TryAffectGoodwillWith(player, target - before,
                        canSendMessage: false, canSendHostilityLetter: false);
                    how = "relation=" + kind + " (via goodwill " + before + " -> " + target + ")";
                }
                else
                {
                    other.SetRelationDirect(player, kind, canSendHostilityLetter: false, reason: "AutoRimmer dev fixture");
                    how = "relation=" + kind;
                }
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
