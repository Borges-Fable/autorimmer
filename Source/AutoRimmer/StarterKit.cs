using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ============================================================ spec 3.1 ===
    // `dev:starter-kit` — the macro. One call stages a whole fixture: pawns
    // with skills and needs, supplies, finished research, cleared fog, and
    // (optionally) the SAVE that turns the result into a reusable fixture.
    //
    // WHY THE CONFIG LOOKS LIKE THIS. Spike 0.1 found the no-UI path into a
    // fixture: with devMode on, a save named `autostart.rws` in the bench Saves
    // dir is loaded automatically on entering the Play scene
    // (SaveGameFilesUtility.GetAutostartSaveFile matches on the file's stem,
    // lowercased, equalling "autostart"). That beats `-quicktest`, which
    // regenerates a world every boot. So the kit's OUTPUT has to be savable in
    // one step — `save_as:"autostart"` — and the config has to be complete
    // enough that one JSON document IS the fixture definition. That is the
    // whole shape of the format: sections that mirror the primitive verbs, a
    // named preset for the boring parts, and a save at the end.
    //
    // THE CONFIG IS THE ARGS. There is deliberately no kit file on disk. The
    // protocol root's `config.json` belongs to 1.x (alertScanFrames), and a
    // second config file would be a second contract to keep in sync with no
    // review trail. A kit is a JSON document the caller already has to write;
    // presets are code-side defaults it can start from, and `dry_run` resolves
    // one without mutating so a typo costs nothing.
    //
    // MERGE RULE, stated once: a section the caller provides REPLACES the
    // preset's section of that name. `extra_items` appends to whatever items
    // are in force. Nothing merges element-wise — element-wise merging of lists
    // is where config formats go to die.
    //
    // REUSE, NOT REIMPLEMENTATION. Every mutation here runs through the very
    // same verb handlers a caller would invoke one at a time (DevVerbs.SpawnThing,
    // DevVerbs.SpawnPawn, DevPawnVerbs.SetSkill, …), by building a VerbContext
    // and calling them. One implementation, one set of documented deviations,
    // and — because each of those writes its own `dev` journal event — the
    // provenance trail is per-item rather than per-kit. This verb adds a
    // per-SECTION line on top, carrying the seqs of the lines it caused, so a
    // post-mortem can go either way: kit -> items, or item -> kit.
    //
    // THE KIT LANDS ITS GEAR FORBIDDEN (git-bug 091e3f0, resolved by Evan
    // 2026-08-31). This is the one place the kit's fiction diverges from the
    // primitive it calls. A colony start is an ARRIVAL:
    // `RimWorld/ScenPart_PlayerPawnsArriveMethod.DoDropPods` ends in
    // `DropPodUtility.DropThingGroupsNear(..., forbid: true, ...)`, and
    // `Data/Core/Defs/Tutor/Instructions.xml` puts `UnforbidStartingResources`
    // immediately after the stockpile steps (`MakeStockpile` ->
    // `EndStockpileDesignating` -> `UnforbidStartingResources`) and will not
    // advance to `BuildRoomWalls` until the player does it. Every real player
    // un-forbids their starting pile. A kit that skips it hands the agent an
    // affordance no player has — the same argument DESIGN's decisions log makes
    // about fog — and it makes the M1 path unable to rehearse `unforbid` against
    // a real obstacle, which is exactly how a live run once left a forbidden
    // rifle, revolver, knife and flak set sitting unused while FSWA's
    // `!thing.IsForbidden(Faction.OfPlayer)` checks stepped over all four.
    //
    // `dev:spawn-thing` DELIBERATELY DOES NOT FOLLOW. Its provenance is
    // `Verse/DebugThingPlaceHelper.DebugSpawn`, which contains no forbidding at
    // all — a bare spawn models a thing APPEARING, not a thing arriving. It
    // gains an opt-in `forbid` arg (which is how this kit gets the behaviour
    // without reimplementing placement) and keeps `false` as its default. The
    // rest of staging has nothing to forbid: pawns, research and fog carry no
    // `CompForbiddable` between them, and `DoDropPods` forbidding the arriving
    // pawn along with its cargo is a silent no-op in the game too. See
    // Dev.Forbid for what the flag does and does not reach.
    // =========================================================================
    public static class StarterKit
    {
        private sealed class Item
        {
            public string Def;
            public string Stuff;
            public int Count = 1;
            public string Quality;
        }

        private sealed class Preset
        {
            public string Summary;
            public List<Item> Items = new List<Item>();
            public List<string> Research = new List<string>();
        }

        // Built-in kits. Every defName here is vanilla Core and was checked
        // against the bench's own Data/Core/Defs, not from memory. A preset
        // entry whose def is missing (a mod pruned it) is SKIPPED with a note
        // rather than failing the call — but an item the CALLER named is a hard
        // error, because that one is a typo and silence would hide it.
        private static readonly Dictionary<string, Preset> Presets =
            new Dictionary<string, Preset>(StringComparer.OrdinalIgnoreCase)
            {
                ["survival"] = new Preset
                {
                    Summary = "food, medicine and the materials to put a roof up — DESIGN's M1 survive-10-days baseline",
                    Items =
                    {
                        new Item { Def = "MealSurvivalPack", Count = 40 },
                        new Item { Def = "Pemmican", Count = 150 },
                        new Item { Def = "MedicineHerbal", Count = 30 },
                        new Item { Def = "Steel", Count = 400 },
                        new Item { Def = "WoodLog", Count = 400 },
                        new Item { Def = "ComponentIndustrial", Count = 20 },
                        new Item { Def = "Silver", Count = 800 },
                    },
                },
                ["shelter"] = new Preset
                {
                    Summary = "construction stock only: steel, wood, blocks, components",
                    Items =
                    {
                        new Item { Def = "Steel", Count = 450 },
                        new Item { Def = "WoodLog", Count = 600 },
                        new Item { Def = "BlocksGranite", Count = 300 },
                        new Item { Def = "ComponentIndustrial", Count = 20 },
                    },
                },
                ["medical"] = new Preset
                {
                    Summary = "medicine at both tiers plus two beds to put the patients in",
                    Items =
                    {
                        new Item { Def = "MedicineIndustrial", Count = 30 },
                        new Item { Def = "MedicineHerbal", Count = 30 },
                        new Item { Def = "Bed", Stuff = "WoodLog", Count = 2, Quality = "Normal" },
                    },
                },
                ["combat"] = new Preset
                {
                    Summary = "three rifles, flak and helmets, industrial medicine — enough to survive a small raid",
                    Items =
                    {
                        new Item { Def = "Gun_BoltActionRifle", Count = 3 },
                        new Item { Def = "Apparel_FlakVest", Count = 3 },
                        new Item { Def = "Apparel_SimpleHelmet", Count = 3 },
                        new Item { Def = "MedicineIndustrial", Count = 20 },
                    },
                },
                ["workshop"] = new Preset
                {
                    Summary = "materials and the early research that unlocks working them",
                    Items =
                    {
                        new Item { Def = "Steel", Count = 500 },
                        new Item { Def = "ComponentIndustrial", Count = 30 },
                        new Item { Def = "Silver", Count = 1500 },
                    },
                    Research = { "Smithing", "Stonecutting", "ComplexFurniture", "PassiveCooler" },
                },
            };

        // --------------------------------------------------------------------
        // dev:starter-kit {preset?, at?, stockpile?, items?, extra_items?,
        //                  pawns?, research?, unfog?, forbid?, buildable?,
        //                  save_as?, dry_run?}
        // --------------------------------------------------------------------
        [Verb("dev:starter-kit")]
        public static object Kit(VerbContext ctx)
        {
            const string V = "dev:starter-kit";
            Dev.Gate(V);
            var map = Dev.CurrentMap(V);
            var a = ctx.Args;

            Preset preset = null;
            string presetName = a.Str("preset");
            if (presetName != null && !Presets.TryGetValue(presetName, out preset))
                throw new VerbArgsException(
                    $"unknown preset '{presetName}' (known: {string.Join(", ", PresetNames())}) — "
                    + "or omit 'preset' and pass 'items' yourself");

            bool dryRun = a.Bool("dry_run", false);
            object at = a.Raw("at");
            object stockpile = a.Has("stockpile") ? a.Raw("stockpile") : true;
            // Defaults TRUE — the arrival fiction, see the header. `forbid:false`
            // is the escape hatch for a fixture that deliberately wants usable
            // gear (a bill or hauling test that is not about forbidding), and it
            // is named in the plan either way so a dry run says which fiction is
            // in force.
            bool forbidItems = a.Bool("forbid", true);
            // Forwarded to every BUILDING this kit stages, and to nothing else
            // (git-bug 3a5ff6c item 3). Default false, exactly as
            // `dev:spawn-thing`'s is: the kit is a god-hand and Evan's ruling of
            // 2026-09-01 keeps it one. The flag exists so a fixture that has to
            // be defensibly buildable — an M2 rehearsal, anything `site-audit`
            // will later be pointed at — can ask for the real gate in one word
            // instead of restaging item by item.
            bool buildableItems = a.Bool("buildable", false);

            // --- resolve the plan BEFORE mutating anything -------------------
            // A kit that spawns four of six items and then fails on a typo has
            // left the colony in a state nobody asked for. Every def is resolved
            // up front so a bad-args costs nothing.
            var items = new List<Item>();
            var skipped = new List<object>();
            if (a.Has("items")) items.AddRange(ParseItems(a.Raw("items"), "items"));
            else if (preset != null) items.AddRange(Filter(preset.Items, skipped));
            if (a.Has("extra_items")) items.AddRange(ParseItems(a.Raw("extra_items"), "extra_items"));

            var research = new List<string>();
            bool allResearch = false;
            if (a.Has("research"))
            {
                var raw = a.Raw("research");
                if (raw is string s && s == "all") allResearch = true;
                else research.AddRange(a.StrList("research"));
            }
            else if (preset != null) research.AddRange(preset.Research);
            foreach (var r in research) Dev.Named<ResearchProjectDef>(r, "research");

            var pawnSpecs = ParsePawns(a.Raw("pawns"));

            var plan = new Dictionary<string, object>
            {
                ["preset"] = presetName,
                ["preset_summary"] = preset?.Summary,
                ["items"] = ItemPlan(items),
                ["pawns"] = PawnPlan(pawnSpecs),
                ["research"] = allResearch ? (object)"all" : research,
                ["unfog"] = a.Has("unfog") ? a.Raw("unfog") : null,
                ["save_as"] = a.Str("save_as"),
                ["at"] = at,
                ["stockpile"] = stockpile,
                ["forbid"] = forbidItems,
                ["buildable"] = buildableItems,
                ["forbid_note"] = forbidItems
                    ? "items land FORBIDDEN, mimicking ScenPart_PlayerPawnsArriveMethod.DoDropPods' "
                        + "forbid:true — clear them with the `unforbid` verb, as a player would"
                    : "items land usable; this kit is NOT modelling an arrival",
            };
            if (skipped.Count > 0) plan["preset_entries_skipped"] = skipped;

            if (dryRun)
            {
                return new Dictionary<string, object>
                {
                    ["dry_run"] = true,
                    ["plan"] = plan,
                    ["dev"] = Dev.NoStamp(),
                    ["note"] = "nothing was mutated; every def in the plan resolved against the live "
                        + "DefDatabase. Re-send without dry_run to apply.",
                };
            }

            // --- apply, section by section -----------------------------------
            var sections = new Dictionary<string, object>();
            var seqs = new List<object>();

            // 1. FOG FIRST. Everything below stages things the player-facing
            //    serializers must then be able to SEE; clearing fog afterwards
            //    would leave a window in which the fixture reads as empty.
            if (a.Has("unfog"))
            {
                var unfogArgs = new Dictionary<string, object>();
                var raw = a.Raw("unfog");
                if (raw is bool b && b) { unfogArgs["around"] = at; unfogArgs["radius"] = 25d; }
                else if (raw is double d) { unfogArgs["around"] = at; unfogArgs["radius"] = d; }
                else if (raw is List<object> rect) unfogArgs["rect"] = rect;
                else if (raw is string s2 && s2 == "all") unfogArgs["all"] = true;
                else throw new VerbArgsException("unfog must be true, a radius (number), a [x,z,w,h] rect, or \"all\"");
                if (unfogArgs.ContainsKey("around") && at == null) unfogArgs.Remove("around");
                sections["unfog"] = Collect(DevVerbs.Unfog(Sub(ctx, unfogArgs)), seqs);
            }

            // 2. PAWNS, before items: a pawn spawned into a stocked colony is
            //    the ordering a save wants, and a pawn is what a later
            //    `pos:"pawn:<id>"` item placement can key off.
            if (pawnSpecs.Count > 0)
            {
                var made = new List<object>();
                foreach (var spec in pawnSpecs) made.Add(ApplyPawn(ctx, spec, at, seqs));
                sections["pawns"] = made;
            }

            // 3. ITEMS.
            var tally = new ForbidTally();
            if (items.Count > 0)
            {
                var placed = new List<object>();
                foreach (var item in items)
                {
                    var itemDef = DefDatabase<ThingDef>.GetNamedSilentFail(item.Def);
                    // A BUILDING TAKES THE EXACT-CELL PATH, ONE UNIT AT A TIME,
                    // and this is M1 finding A fixed at its source (git-bug
                    // 3a5ff6c item 3). The kit passed no `mode`, so a building
                    // took `dev:spawn-thing`'s default — `ThingPlaceMode.Near`,
                    // GenPlace's radial search — which gates every candidate on
                    // GenSpawn.CanSpawnAt and reported `placed: 0` for the M1
                    // research bench when the whole disc came back refused. A
                    // building has a footprint and an interaction cell;
                    // "somewhere near here" is not a placement for one.
                    //
                    // ONE CALL PER UNIT, WITH ITS OWN CELL, because `mode:direct`
                    // alone would be a REGRESSION: `count:2` runs this verb's
                    // stack loop twice against one target, and
                    // `GenSpawn.SpawningWipes(Bed, Bed)` is true for two
                    // edifices, so the second bed of the `medical` preset would
                    // VANISH the first and the envelope would still say
                    // `placed: 2`. Near happened to hide that by scattering.
                    //
                    // ITEMS ARE UNTOUCHED, deliberately: `Near` is right for a
                    // stack (it merges into storage, which is the whole reason
                    // `stockpile` exists) and every shipped suite stages items
                    // through it.
                    bool isBuilding = itemDef != null && itemDef.category == ThingCategory.Building;
                    if (isBuilding)
                    {
                        var sites = BuildingSites(map, itemDef, item, at, buildableItems);
                        for (int u = 0; u < sites.Count; u++)
                        {
                            var args = BaseItemArgs(item, forbidItems);
                            args["count"] = 1d;
                            args["pos"] = Positions.Out(sites[u]);
                            args["mode"] = "direct";
                            if (buildableItems) args["buildable"] = true;
                            var one = Collect(DevVerbs.SpawnThing(Sub(ctx, args)), seqs);
                            tally.Accrue(one);
                            placed.Add(one);
                        }
                        continue;
                    }

                    var itemArgs = BaseItemArgs(item, forbidItems);
                    itemArgs["count"] = (double)item.Count;
                    if (at != null) itemArgs["pos"] = at;
                    if (!(stockpile is bool sb && !sb)) itemArgs["stockpile"] = stockpile;
                    var res = Collect(DevVerbs.SpawnThing(Sub(ctx, itemArgs)), seqs);
                    tally.Accrue(res);
                    placed.Add(res);
                }
                sections["items"] = placed;
            }

            // 4. RESEARCH.
            if (allResearch || research.Count > 0)
            {
                var args = new Dictionary<string, object>();
                if (allResearch) args["all"] = true;
                else
                {
                    var list = new List<object>();
                    foreach (var r in research) list.Add(r);
                    args["projects"] = list;
                }
                sections["research"] = Collect(DevVerbs.FinishResearch(Sub(ctx, args)), seqs);
            }

            // 5. SAVE — last, so the file contains everything above.
            string saveAs = a.Str("save_as");
            if (saveAs != null) sections["save"] = Save(map, saveAs);

            var forbidOut = tally.Out(forbidItems);

            long seq = Dev.Emit(V, "starter-kit",
                (presetName ?? "custom") + ": " + items.Count + " item(s), "
                + pawnSpecs.Count + " pawn(s)"
                + (forbidItems ? ", " + tally.Ids.Count + " forbidden" : "")
                + (saveAs != null ? ", saved '" + saveAs + "'" : ""),
                new Dictionary<string, object>
                {
                    ["plan"] = plan,
                    // The join in the other direction: which lines this kit
                    // caused. A post-mortem reading a dev:spawn-thing line can
                    // find the kit that issued it, and vice versa.
                    ["caused_seqs"] = seqs,
                    // Counts, not the lists: the per-item dev:spawn-thing lines
                    // already carry the ids. This is here so M1's
                    // no-dev-verbs-after-staging invariant can be read off the
                    // journal ALONE — the line that staged the colony says the
                    // gear was left forbidden, without needing the result
                    // envelope anyone happened to keep.
                    ["forbid"] = forbidItems,
                    ["forbidden_stacks"] = tally.Ids.Count,
                    ["not_forbiddable"] = tally.NotForbiddable.Count,
                });

            var data = new Dictionary<string, object>
            {
                ["preset"] = presetName,
                ["plan"] = plan,
                ["applied"] = sections,
                ["caused_journal_seqs"] = seqs,
                ["dev"] = Dev.Stamp(seq),
            };
            if (forbidOut != null) data["forbid"] = forbidOut;
            return data;
        }

        // The kit's cross-cutting summary of what it left forbidden, assembled
        // from the nested dev:spawn-thing results rather than by re-reading the
        // map — no second pass over game state, and the numbers are the ones
        // those calls actually returned.
        //
        // It exists to make the ACCEPTANCE runnable in one hop: `rect` is the
        // bounding box of every stack that took the flag, which is the shape
        // `unforbid {rect:[x,z,w,h]}` wants (DesignateEngine.Resolve), and `ids`
        // is the exact form `unforbid {things:[…]}` wants when the rect would
        // sweep up things the kit did not place.
        private sealed class ForbidTally
        {
            public readonly List<object> Ids = new List<object>();
            public readonly List<object> NotForbiddable = new List<object>();
            private int minX = int.MaxValue, minZ = int.MaxValue;
            private int maxX = int.MinValue, maxZ = int.MinValue;

            public void Accrue(object spawnResult)
            {
                if (!(spawnResult is Dictionary<string, object> d)) return;
                if (d.TryGetValue("forbid", out var fv) && fv is Dictionary<string, object> f)
                {
                    if (f.TryGetValue("ids", out var iv) && iv is List<object> il)
                        foreach (var i in il) Ids.Add(i);
                    if (f.TryGetValue("not_forbiddable", out var nv) && nv is List<object> nl)
                        foreach (var n in nl) NotForbiddable.Add(n);
                }
                // Cells come from the describes that ACTUALLY carry the flag —
                // Dev.Describe publishes `forbidden` only when true — so the
                // rect never claims to cover a stack the flag missed.
                if (!(d.TryGetValue("spawned", out var sv) && sv is List<object> sl)) return;
                foreach (var s in sl)
                {
                    if (!(s is Dictionary<string, object> sd)) continue;
                    if (!(sd.TryGetValue("forbidden", out var fb) && fb is bool fbb && fbb)) continue;
                    if (!(sd.TryGetValue("at", out var av) && av is List<object> pos
                          && pos.Count == 2 && pos[0] is double px && pos[1] is double pz)) continue;
                    int x = (int)px, z = (int)pz;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (z < minZ) minZ = z;
                    if (z > maxZ) maxZ = z;
                }
            }

            private List<object> Rect()
                => minX > maxX ? null : new List<object>
                {
                    (double)minX, (double)minZ,
                    (double)(maxX - minX + 1), (double)(maxZ - minZ + 1),
                };

            // Null when the caller never asked — an absent key means the
            // question was not put, which is not the same as asking and getting
            // nothing back.
            public Dictionary<string, object> Out(bool requested)
            {
                if (!requested) return null;
                var rect = Rect();
                var d = new Dictionary<string, object>
                {
                    ["mimics"] = "RimWorld/ScenPart_PlayerPawnsArriveMethod.DoDropPods -> "
                        + "DropPodUtility.DropThingGroupsNear(forbid: true)",
                    ["gate"] = Dev.ForbidGate,
                    ["forbidden_stacks"] = Ids.Count,
                    ["ids"] = Ids,
                    ["rect"] = rect,
                    ["not_forbiddable"] = NotForbiddable,
                };
                d["remedy"] = rect != null
                    ? "unforbid {\"rect\":[" + rect[0] + "," + rect[1] + "," + rect[2] + ","
                        + rect[3] + "]} — or unforbid {\"things\":[…ids…]} to touch only these stacks"
                    : "nothing took the flag; there is nothing to unforbid";
                if (NotForbiddable.Count > 0)
                    d["not_forbiddable_note"] =
                        "these were left USABLE on purpose rather than red-erroring: a def with no "
                        + "CompForbiddable (Bed and every other plain BuildingBase descendant, and "
                        + "pawns) cannot hold the flag, and a non-Item could not be cleared again by "
                        + "the `unforbid` verb. Spawn a building with minified:true if you want it "
                        + "forbiddable — MinifiedThing is an Item and does carry the comp";
                return d;
            }
        }

        // The arguments every item shares, whichever placement path it takes.
        // `forbid` is applied by the handler that holds the Thing reference;
        // dev:* bypasses the player gate by design (DESIGN §Action model) but
        // this particular write does NOT — Dev.Forbid asks
        // Designator_Forbid.CanDesignateThing's own predicate first, so the
        // shipped `unforbid` verb can always undo it.
        private static Dictionary<string, object> BaseItemArgs(Item item, bool forbidItems)
        {
            var args = new Dictionary<string, object> { ["def"] = item.Def };
            if (item.Stuff != null) args["stuff"] = item.Stuff;
            if (item.Quality != null) args["quality"] = item.Quality;
            if (forbidItems) args["forbid"] = true;
            return args;
        }

        // ONE CELL PER BUILDING UNIT, non-overlapping, nearest first.
        //
        // A ring walk out from the anchor, taking the first cell whose FOOTPRINT
        // (GenAdj.OccupiedRect at dev:spawn-thing's own default Rot4.North —
        // Verse/DebugThingPlaceHelper.DebugSpawn's rotation, and this kit calls
        // that verb) clears the gate the caller asked for and does not touch a
        // cell an earlier unit of this same kit already claimed. `count` cells
        // for `count` units.
        //
        // THE GATE IS THE CALLER'S CHOICE and is asked HERE only to CHOOSE; the
        // spawn that follows asks it again for real, and its answer is the one in
        // the envelope. Two calls to one predicate is the cost of not
        // reimplementing placement (this file's REUSE-NOT-REIMPLEMENTATION rule)
        // — and asking `SiteGate` here rather than `CanSpawnAt` when
        // `buildable:true` matters, because otherwise the search would hand the
        // spawn a cell the blueprint gate is about to refuse and the kit would
        // report a refusal it had chosen itself.
        //
        // ALWAYS RETURNS `count` CELLS. When the walk finds nothing it yields the
        // anchor and lets `dev:spawn-thing` refuse it — with the game's own
        // sentence, the refusing cell and a journal row, which is a better record
        // than anything this method could invent, and is what M1 finding A was
        // about (`placed: 0` with no reason anywhere).
        private static List<IntVec3> BuildingSites(Map map, ThingDef def, Item item,
            object at, bool buildable)
        {
            var anchor = at != null ? Positions.Resolve(map, at) : Dev.Anchor(map);
            var rot = Rot4.North;
            ThingDef stuff = null;
            if (buildable)
            {
                // Same resolution dev:spawn-thing will do, minus its `random`
                // option: a site chosen against one stuff and spawned with
                // another is a different terrain-affordance question
                // (BuildableDef.GetTerrainAffordanceNeed reads stuff).
                try { stuff = SiteVerbs.ResolveStuff(def, item.Stuff == "random" ? null : item.Stuff); }
                catch { stuff = null; }
            }
            int want = Math.Max(1, item.Count);
            var sites = new List<IntVec3>();
            var claimed = new HashSet<IntVec3>();
            for (int ring = 0; ring < 40 && sites.Count < want; ring++)
            {
                for (int dx = -ring; dx <= ring && sites.Count < want; dx++)
                    for (int dz = -ring; dz <= ring && sites.Count < want; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                        var c = new IntVec3(anchor.x + dx, 0, anchor.z + dz);
                        if (!c.InBounds(map)) continue;
                        var rect = GenAdj.OccupiedRect(c, rot, def.Size);
                        if (!rect.InBounds(map)) continue;
                        bool clash = false;
                        foreach (var cell in rect) if (claimed.Contains(cell)) { clash = true; break; }
                        if (clash) continue;
                        bool ok;
                        try
                        {
                            ok = buildable
                                ? SiteGate.Check(map, def, c, rot, stuff).Ok
                                : GenSpawn.CanSpawnAt(def, c, map, rot, canWipeEdifices: true);
                        }
                        catch { ok = false; }
                        if (!ok) continue;
                        sites.Add(c);
                        foreach (var cell in rect) claimed.Add(cell);
                    }
            }
            while (sites.Count < want) sites.Add(anchor);
            return sites;
        }

        // A sub-context for a nested verb call. Same command id and args
        // machinery the poller would have handed the verb directly, so the
        // nested handler behaves identically whether it was called on its own
        // or from here.
        private static VerbContext Sub(VerbContext parent, Dictionary<string, object> args)
            => new VerbContext
            {
                Id = parent.Id,
                Op = parent.Op,
                Args = new VerbArgs(args),
                Command = parent.Command,
            };

        // Pull the nested call's journal seq out of its result and keep it, so
        // the kit's own line can name every line it caused.
        private static object Collect(object result, List<object> seqs)
        {
            if (result is Dictionary<string, object> d
                && d.TryGetValue("dev", out var stamp)
                && stamp is Dictionary<string, object> sd
                && sd.TryGetValue("journal_seq", out var s))
            {
                if (s is long l && l > 0) seqs.Add(l);
            }
            return result;
        }

        private sealed class PawnSpec
        {
            public string Name;
            public string Kind = "Colonist";
            public string Faction = "player";
            public object At;
            public double Age;
            public bool ViolenceCapable;
            public Dictionary<string, object> Skills;
            public Dictionary<string, object> Passions;
            public Dictionary<string, object> Needs;
            public List<string> Hediffs;
        }

        private static List<PawnSpec> ParsePawns(object raw)
        {
            var specs = new List<PawnSpec>();
            if (raw == null) return specs;
            if (!(raw is List<object> list))
                throw new VerbArgsException("'pawns' must be an array of objects");
            foreach (var o in list)
            {
                if (!(o is Dictionary<string, object> d))
                    throw new VerbArgsException("each entry of 'pawns' must be an object");
                var spec = new PawnSpec();
                if (d.TryGetValue("name", out var n)) spec.Name = n as string;
                if (d.TryGetValue("kind", out var k)) spec.Kind = (k as string) ?? spec.Kind;
                if (d.TryGetValue("faction", out var f)) spec.Faction = (f as string) ?? spec.Faction;
                if (d.TryGetValue("pos", out var p)) spec.At = p;
                if (d.TryGetValue("age", out var ag) && ag is double agd) spec.Age = agd;
                if (d.TryGetValue("violence_capable", out var vc) && vc is bool vcb) spec.ViolenceCapable = vcb;
                spec.Skills = d.TryGetValue("skills", out var sk) ? sk as Dictionary<string, object> : null;
                spec.Passions = d.TryGetValue("passions", out var pa) ? pa as Dictionary<string, object> : null;
                spec.Needs = d.TryGetValue("needs", out var nd) ? nd as Dictionary<string, object> : null;
                if (d.TryGetValue("hediffs", out var hd) && hd is List<object> hl)
                {
                    spec.Hediffs = new List<string>();
                    foreach (var h in hl) if (h is string hs) spec.Hediffs.Add(hs);
                }
                // Resolve now so a typo is a bad-args, not a half-built colony.
                Dev.Named<PawnKindDef>(spec.Kind, "pawns[].kind");
                if (spec.Skills != null) foreach (var kv in spec.Skills) Dev.Named<SkillDef>(kv.Key, "pawns[].skills");
                if (spec.Passions != null) foreach (var kv in spec.Passions) Dev.Named<SkillDef>(kv.Key, "pawns[].passions");
                if (spec.Needs != null) foreach (var kv in spec.Needs) Dev.Named<NeedDef>(kv.Key, "pawns[].needs");
                if (spec.Hediffs != null) foreach (var h in spec.Hediffs) Dev.Named<HediffDef>(h, "pawns[].hediffs");
                specs.Add(spec);
            }
            return specs;
        }

        private static object ApplyPawn(VerbContext ctx, PawnSpec spec, object at, List<object> seqs)
        {
            var spawnArgs = new Dictionary<string, object>
            {
                ["kind"] = spec.Kind,
                ["faction"] = spec.Faction,
                ["count"] = 1d,
            };
            if (spec.Name != null) spawnArgs["name"] = spec.Name;
            if (spec.At != null) spawnArgs["pos"] = spec.At;
            else if (at != null) spawnArgs["pos"] = at;
            if (spec.Age > 0) spawnArgs["age"] = spec.Age;
            if (spec.ViolenceCapable) spawnArgs["violence_capable"] = true;

            var spawned = Collect(DevVerbs.SpawnPawn(Sub(ctx, spawnArgs)), seqs);
            int id = FirstPawnId(spawned);
            var applied = new Dictionary<string, object> { ["spawn"] = spawned };

            if (spec.Skills != null || spec.Passions != null)
            {
                var skillArgs = new Dictionary<string, object> { ["pawn"] = (double)id };
                if (spec.Skills != null) skillArgs["skills"] = spec.Skills;
                if (spec.Passions != null) skillArgs["passions"] = spec.Passions;
                applied["skills"] = Collect(DevPawnVerbs.SetSkill(Sub(ctx, skillArgs)), seqs);
            }
            if (spec.Needs != null)
            {
                var needs = new List<object>();
                foreach (var kv in spec.Needs)
                {
                    if (!(kv.Value is double val))
                        throw new VerbArgsException($"pawns[].needs['{kv.Key}'] must be a number 0..1");
                    needs.Add(Collect(DevPawnVerbs.SetNeed(Sub(ctx, new Dictionary<string, object>
                    {
                        ["pawn"] = (double)id,
                        ["need"] = kv.Key,
                        ["val"] = val,
                    })), seqs));
                }
                applied["needs"] = needs;
            }
            if (spec.Hediffs != null && spec.Hediffs.Count > 0)
            {
                var added = new List<object>();
                foreach (var h in spec.Hediffs)
                    added.Add(Collect(DevPawnVerbs.AddHediff(Sub(ctx, new Dictionary<string, object>
                    {
                        ["pawn"] = (double)id,
                        ["def"] = h,
                    })), seqs));
                applied["hediffs"] = added;
            }
            return applied;
        }

        private static int FirstPawnId(object spawnResult)
        {
            if (spawnResult is Dictionary<string, object> d
                && d.TryGetValue("pawns", out var v) && v is List<object> list && list.Count > 0
                && list[0] is Dictionary<string, object> p && p.TryGetValue("id", out var id) && id is int i)
                return i;
            throw new VerbArgsException("dev:spawn-pawn returned no pawn; the kit cannot continue this entry");
        }

        // Caller-supplied items are STRICT: every def is resolved here, so a
        // typo is a bad-args before anything is mutated. (Preset items go
        // through Filter instead — see its comment for why they are soft.)
        private static List<Item> ParseItems(object raw, string key)
        {
            var items = new List<Item>();
            if (raw == null) return items;
            if (!(raw is List<object> list))
                throw new VerbArgsException($"'{key}' must be an array of objects, e.g. [{{\"def\":\"Steel\",\"count\":200}}]");
            foreach (var o in list)
            {
                if (!(o is Dictionary<string, object> d))
                    throw new VerbArgsException($"each entry of '{key}' must be an object");
                var item = new Item();
                item.Def = d.TryGetValue("def", out var def) ? def as string : null;
                if (item.Def == null) throw new VerbArgsException($"each entry of '{key}' needs a 'def'");
                item.Stuff = d.TryGetValue("stuff", out var st) ? st as string : null;
                item.Quality = d.TryGetValue("quality", out var q) ? q as string : null;
                if (d.TryGetValue("count", out var c) && c is double cd) item.Count = (int)cd;
                if (item.Count < 1) throw new VerbArgsException($"'{key}' entry '{item.Def}' has count < 1");
                Dev.Named<ThingDef>(item.Def, key + "[].def");
                items.Add(item);
            }
            return items;
        }

        // Preset entries are soft: a def a mod removed is skipped with a note,
        // because a preset is OUR default and the caller did not ask for it by
        // name. Caller-named defs stay hard errors.
        private static List<Item> Filter(List<Item> presetItems, List<object> skipped)
        {
            var kept = new List<Item>();
            foreach (var item in presetItems)
            {
                if (DefDatabase<ThingDef>.GetNamedSilentFail(item.Def) == null)
                {
                    skipped.Add(new Dictionary<string, object>
                    {
                        ["def"] = item.Def,
                        ["reason"] = "no such ThingDef on this bench (preset entry, skipped)",
                    });
                    continue;
                }
                kept.Add(item);
            }
            return kept;
        }

        private static List<object> ItemPlan(List<Item> items)
        {
            var plan = new List<object>();
            foreach (var i in items)
                plan.Add(new Dictionary<string, object>
                {
                    ["def"] = i.Def,
                    ["stuff"] = i.Stuff,
                    ["count"] = i.Count,
                    ["quality"] = i.Quality,
                });
            return plan;
        }

        private static List<object> PawnPlan(List<PawnSpec> specs)
        {
            var plan = new List<object>();
            foreach (var s in specs)
                plan.Add(new Dictionary<string, object>
                {
                    ["name"] = s.Name,
                    ["kind"] = s.Kind,
                    ["faction"] = s.Faction,
                    ["skills"] = s.Skills,
                    ["passions"] = s.Passions,
                    ["needs"] = s.Needs,
                    ["hediffs"] = s.Hediffs,
                });
            return plan;
        }

        private static List<string> PresetNames()
        {
            var n = new List<string>(Presets.Keys);
            n.Sort(StringComparer.Ordinal);
            return n;
        }

        // The fixture-making half. `save_as:"autostart"` writes autostart.rws,
        // which SaveGameFilesUtility.GetAutostartSaveFile picks up on the next
        // boot and Root_Play loads with no UI at all (spike 0.1 FINDINGS).
        //
        // GameDataSaveLoader.SaveGame SWALLOWS its exception and Log.Errors
        // instead, so a failed save looks exactly like a successful one from the
        // call site — and the journal would show it only as a red_error with no
        // link to this verb. The file is therefore checked afterwards and its
        // size reported: `saved:true` here means a file exists, not that a
        // method returned.
        private static Dictionary<string, object> Save(Map map, string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                if (name.IndexOf(c) >= 0)
                    throw new VerbArgsException($"save_as '{name}' contains a character illegal in a filename");

            string path = null;
            try { path = GenFilePaths.FilePathForSavedGame(name); } catch { }
            GameDataSaveLoader.SaveGame(name);

            bool exists = false;
            long bytes = 0;
            try
            {
                if (path != null && File.Exists(path))
                {
                    exists = true;
                    bytes = new FileInfo(path).Length;
                }
            }
            catch { }

            var d = new Dictionary<string, object>
            {
                ["name"] = name,
                ["path"] = path,
                ["saved"] = exists,
                ["bytes"] = bytes,
                ["tick"] = map != null ? Find.TickManager.TicksGame : 0,
            };
            if (string.Equals(name, "autostart", StringComparison.OrdinalIgnoreCase))
                d["autostart"] = "this file is the bench's no-UI fixture: with devMode on, the next "
                    + "launch loads it straight into Play (SaveGameFilesUtility.GetAutostartSaveFile)";
            if (!exists)
                d["warning"] = "no file at that path after SaveGame — GameDataSaveLoader.SaveGame swallows "
                    + "its exception and logs a red error instead; check the journal's red_error events";
            return d;
        }
    }
}
