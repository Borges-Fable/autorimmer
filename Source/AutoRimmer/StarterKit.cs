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
        //                  pawns?, research?, unfog?, save_as?, dry_run?}
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
            if (items.Count > 0)
            {
                var placed = new List<object>();
                foreach (var item in items)
                {
                    var args = new Dictionary<string, object>
                    {
                        ["def"] = item.Def,
                        ["count"] = (double)item.Count,
                    };
                    if (item.Stuff != null) args["stuff"] = item.Stuff;
                    if (item.Quality != null) args["quality"] = item.Quality;
                    if (at != null) args["pos"] = at;
                    if (!(stockpile is bool sb && !sb)) args["stockpile"] = stockpile;
                    placed.Add(Collect(DevVerbs.SpawnThing(Sub(ctx, args)), seqs));
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

            long seq = Dev.Emit(V, "starter-kit",
                (presetName ?? "custom") + ": " + items.Count + " item(s), "
                + pawnSpecs.Count + " pawn(s)" + (saveAs != null ? ", saved '" + saveAs + "'" : ""),
                new Dictionary<string, object>
                {
                    ["plan"] = plan,
                    // The join in the other direction: which lines this kit
                    // caused. A post-mortem reading a dev:spawn-thing line can
                    // find the kit that issued it, and vice versa.
                    ["caused_seqs"] = seqs,
                });

            return new Dictionary<string, object>
            {
                ["preset"] = presetName,
                ["plan"] = plan,
                ["applied"] = sections,
                ["caused_journal_seqs"] = seqs,
                ["dev"] = Dev.Stamp(seq),
            };
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
