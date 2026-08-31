using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ============================================================ spec 3.2 ===
    // ZONE VERBS — create and SHAPE the zones the colony works out of.
    //
    //   zone add    {kind: stockpile|dumping|growing, rect|cells, …}
    //   zone expand {id, rect|cells}
    //   zone shrink {id, rect|cells}
    //   zone edit   {id, label|priority|plant|allow_sow|allow_cut|hidden|filter}
    //   zone delete {id}
    //
    // Reading zones is 2.4's `zones`; this file only writes.
    //
    // ------------------- WHERE 3.6's BOUNDARY IS ----------------------------
    // Storage FILTER settings moved OUT of this spec to 3.6 (git-bug 48f666c)
    // because `Zone_Stockpile` and `Building_Storage` share one
    // `IStoreSettingsParent` API and splitting them would split one trap. This
    // spec still CREATES and SHAPES the zone — rect, priority, plant, allowSow/
    // allowCut, edit/delete/shrink/expand — and applies exactly ONE filter
    // primitive, the five named CATEGORY PRESETS the scope calls for (food,
    // meds, apparel, weapons, raw) plus `all`/`none`. That is the whole of
    // `ApplyPreset` below: `SetDisallowAll()` then one
    // `SetAllow(ThingCategoryDef, true)` per preset word.
    //
    // The line 3.6 inherits is therefore sharp: **presets are a whole-filter
    // REPLACEMENT keyed on a fixed five-word vocabulary; anything finer — a
    // def-level allow/disallow, hit-point or quality ranges, special filters,
    // per-node patches, copy/paste between storages, storage GROUPS — is 3.6's,
    // for stockpile zones and storage buildings alike.** `zone edit
    // {filter:…}` takes the same five words and nothing else, and refuses an
    // object outright with a message naming 3.6, so an agent that needs more
    // gets told where it lives rather than a half-answer.
    //
    // ------------------- THE GATE LIVES IN THE WIDGET -----------------------
    //  * zoneable cell — `RimWorld/Designator_ZoneAdd.IsZoneableCell` (fog,
    //    map-edge no-zone band, `def.CanOverlapZones`), reached through each
    //    concrete designator's own `CanDesignateCell`, which adds the parts
    //    that differ: `Designator_ZoneAddStockpile` refuses impassable terrain,
    //    `Designator_ZoneAdd_Growing` refuses cells below the minimum fertility
    //    (`ThingDefOf.Plant_Potato.plant.fertilityMin`, or 0.5 under Biotech).
    //  * plant to grow — `Verse/Command_SetPlantToGrow.ProcessInput` builds its
    //    float menu from `PlantUtility.ValidPlantTypesForGrowers` filtered by
    //    `Command_SetPlantToGrow.IsPlantAvailable`. Reproduced in CanGrowHere.
    //  * priority — `RimWorld/ITab_Storage.FillTab` offers every StoragePriority
    //    EXCEPT `Unstored`; so do we.
    //  * filter — `Verse/ThingFilterUI.DoThingFilterConfigWindow`'s Clear-All
    //    (`SetDisallowAll`) and `Verse/Listing_TreeThingFilter.DoCategoryChildren`'s
    //    per-category checkbox (`SetAllow(node.catDef, …)`).
    //  * delete/shrink — `RimWorld/Designator_ZoneDelete.CanDesignateCell`.
    //
    // ------------------------- HAZARDS --------------------------------------
    //  * `Zone.Cells` Fisher-Yates SHUFFLES a scribed list on read and advances
    //    the shared Rand stream (WorldSafe Class R). Every read here is
    //    `WorldSafe.ZoneCells`.
    //  * `Zone_Growing.PlantDefToGrow` / `GetPlantDefToGrow()` ASSIGN and scribe
    //    a default on a never-configured zone (Class A). Reads go through
    //    `WorldSafe.PlantToGrow`; the SETTER `SetPlantDefToGrow` is a plain
    //    field write and is what a player verb is supposed to call.
    //  * `Command_SetPlantToGrow.IsPlantAvailable` calls
    //    `ResearchProjectDef.IsFinished`, which bottoms out in
    //    `ResearchManager.GetProgress` and INSERTS a zero entry into a scribed
    //    dictionary (Class A). The gate is therefore re-implemented with
    //    `WorldSafe.Finished` in place of that one clause — same logic, no write.
    //  * We never call `Designator_ZoneAdd.DesignateMultiCell`: it drives
    //    `Find.Selector` (clearing and re-selecting the player's selection) and
    //    re-enters TutorSystem. Its cell-attachment ALGORITHM is reproduced
    //    below, line for line, with an explicit `into` zone in place of
    //    `SelectedZone`.
    //  * `Zone.Delete()` plays a sound; `Delete(playSound:false)` is used.
    // =========================================================================
    public static class ZoneVerbs
    {
        [Verb("zone")]
        public static object Zone(VerbContext ctx)
        {
            var map = DesignateEngine.Map();
            var a = ctx.Args;
            string op = a.Str("op", "add");
            switch (op)
            {
                case "add": return Add(map, a, null);
                case "expand": return Add(map, a, FindZone(map, a.IntReq("id")));
                case "shrink": return Shrink(map, a);
                case "edit": return Edit(map, a);
                case "delete": return Delete(map, a);
                default:
                    throw new VerbArgsException("zone op must be add|expand|shrink|edit|delete "
                        + "(reading zones is the `zones` verb)");
            }
        }

        // ------------------------------------------------------------------
        // zone add / zone expand
        // ------------------------------------------------------------------
        private static object Add(Map map, VerbArgs a, Verse.Zone into)
        {
            string kind = into != null ? KindOf(into) : a.Str("kind", null)
                ?? throw new VerbArgsException("zone add needs kind: stockpile|dumping|growing");
            if (kind == null)
                throw new VerbArgsException(
                    $"zone {into.label} (id {into.ID}) is a {into.GetType().Name}, which this verb "
                    + "does not shape (stockpile|dumping|growing only)");
            bool dryRun = a.Bool("dry_run", false);

            // DOCTRINE, from the session-4 curriculum audit: a growing zone with
            // no plant set grows whatever the game defaults to, which is why the
            // scripted tutorial names rice explicitly. Refusing is the honest
            // reading of "should refuse or warn without --plant" — a warning in
            // a result field is a warning nothing reads.
            if (kind == "growing" && into == null && !a.Has("plant") && !a.Bool("allow_unset_plant", false))
                throw new VerbArgsException(
                    "zone add growing needs 'plant' (a growing zone with no plant set grows the game's "
                    + "default, not what you meant — Zone_Growing.PlantDefToGrow assigns Potato or "
                    + "Toxipotato on first read). Pass allow_unset_plant:true to create one anyway.");

            var des = MakeDesignator(kind);
            des.isOrder = true;
            var targets = DesignateEngine.Resolve(map, a, DesignateEngine.MaxCellsArg(a), filterSelectsTargets: false);
            if (targets.IsThings)
                throw new VerbArgsException("zone takes cells: use rect:[x,z,w,h] or cells:[P,…]");

            var rejects = new List<DesignateEngine.Reject>();
            var free = new List<IntVec3>();
            for (int i = 0; i < targets.Cells.Count; i++)
            {
                var c = targets.Cells[i];
                if (!c.InBounds(map)) { rejects.Add(new DesignateEngine.Reject { At = c, Why = "out-of-bounds" }); continue; }
                if (c.Fogged(map))
                {
                    rejects.Add(new DesignateEngine.Reject
                    { At = c, Why = DesignateEngine.WhyFogged, Reason = Blockers.FoggedReason });
                    continue;
                }
                // Cells already in ANY zone are dropped, exactly as
                // Designator_ZoneAdd.DesignateMultiCell does
                // (`unsetCells.RemoveAll(c => ZoneAt(c) != null)`) — but NAMED,
                // because "your rect overlapped the kitchen stockpile" is the
                // single most likely reason an agent's zone came out the wrong
                // shape and a silent drop makes it invisible.
                var existing = map.zoneManager.ZoneAt(c);
                if (existing != null)
                {
                    rejects.Add(new DesignateEngine.Reject
                    {
                        At = c,
                        Why = existing == into ? "already-in-this-zone" : "already-in-zone",
                        Reason = "cell belongs to zone '" + existing.label + "' (id " + existing.ID + ")",
                    });
                    continue;
                }
                AcceptanceReport report;
                try { report = des.CanDesignateCell(c); }
                catch (Exception e)
                {
                    rejects.Add(new DesignateEngine.Reject { At = c, Why = "designator-threw", Reason = e.Message });
                    continue;
                }
                if (!report.Accepted)
                {
                    rejects.Add(new DesignateEngine.Reject
                    {
                        At = c,
                        Why = kind == "growing" ? "not-zoneable-or-infertile" : "not-zoneable",
                        Reason = DesignateEngine.ReasonOf(report),
                    });
                    continue;
                }
                free.Add(c);
            }

            // Grow CONSUMES its list (the game's loop removes each cell as it
            // attaches it), so the counts and the echo are taken first and the
            // loop is handed a copy.
            int acceptedCount = free.Count;
            var firstCell = free.Count > 0 ? free[0] : IntVec3.Invalid;
            var touched = new List<Verse.Zone>();
            if (into != null) touched.Add(into);
            if (!dryRun && acceptedCount > 0)
                Grow(map, kind, new List<IntVec3>(free), into, touched);

            // Shaping, applied to every zone this call created or expanded, so
            // one call yields one coherent result rather than a zone plus a
            // follow-up edit.
            var warnings = new List<object>();
            if (!dryRun)
                for (int i = 0; i < touched.Count; i++)
                    Shape(map, touched[i], a, warnings, created: touched[i] != into);

            var echo = new List<IntVec3>(targets.Cells);
            var data = new Dictionary<string, object>
            {
                ["verb"] = "zone",
                ["op"] = into != null ? "expand" : "add",
                ["kind"] = kind,
                ["designator"] = des.GetType().Name,
                ["gate"] = "RimWorld/" + des.GetType().Name + ".CanDesignateCell "
                    + "(-> Designator_ZoneAdd.IsZoneableCell)",
                ["targeted"] = targets.Count,
                ["requested"] = targets.Requested,
                ["capped"] = targets.Capped,
                ["target_scope"] = targets.Detail,
                ["accepted"] = acceptedCount,
                ["dry_run"] = dryRun,
                ["zones"] = ZonesOut(map, touched),
                // The game's own algorithm can split a non-contiguous rect into
                // several zones (Designator_ZoneAdd.DesignateMultiCell restarts
                // whenever no unset cell touches the current zone), so a caller
                // that assumed one zone is told otherwise.
                ["zones_touched"] = touched.Count,
            };
            if (warnings.Count > 0) data["warnings"] = warnings;
            DesignateEngine.PublishRejects(map, rejects, data);
            data["crop"] = DesignateEngine.Echo(map, echo);
            data["action"] = dryRun
                ? NoAction()
                : Act("zone", into != null ? "expand" : "add:" + kind,
                    acceptedCount + " cell(s) at "
                        + (firstCell.IsValid ? DesignateEngine.Show(firstCell) : "-"),
                    new Dictionary<string, object>
                    {
                        ["counts"] = new Dictionary<string, object>
                        {
                            ["targeted"] = targets.Count,
                            ["accepted"] = acceptedCount,
                            ["rejected"] = rejects.Count,
                        },
                        ["ids"] = IdList(touched),
                        ["rejected_by_reason"] = data["rejects_by_reason"],
                    });
            return data;
        }

        // Designator_ZoneAdd.DesignateMultiCell's attachment loop, reproduced.
        // The ONLY substitution is `into` for `Find.Selector.SelectedZone` —
        // the original both reads and WRITES the player's selection, which a
        // headless player verb must not touch.
        private static void Grow(Map map, string kind, List<IntVec3> unset, Verse.Zone into, List<Verse.Zone> touched)
        {
            var current = into;
            if (current == null)
            {
                current = NewZone(map, kind, touched);
                current.AddCell(unset[0]);
                unset.RemoveAt(0);
            }
            while (unset.Count > 0)
            {
                int count = unset.Count;
                for (int i = unset.Count - 1; i >= 0; i--)
                {
                    bool adjacent = false;
                    for (int d = 0; d < 4; d++)
                    {
                        var c = unset[i] + GenAdj.CardinalDirections[d];
                        if (c.InBounds(map) && map.zoneManager.ZoneAt(c) == current) { adjacent = true; break; }
                    }
                    if (!adjacent) continue;
                    current.AddCell(unset[i]);
                    unset.RemoveAt(i);
                }
                if (unset.Count == 0) break;
                if (unset.Count == count)
                {
                    current = NewZone(map, kind, touched);
                    current.AddCell(unset[0]);
                    unset.RemoveAt(0);
                }
            }
            for (int i = 0; i < touched.Count; i++)
            {
                touched[i].CheckContiguous();
                // Designator_ZoneAdd.DesignateMultiCell's own tail: a new
                // stockpile drops the Haul designation off anything already
                // standing in it, or colonists haul stored goods to themselves.
                if (touched[i] is Zone_Stockpile sp) sp.slotGroup?.RemoveHaulDesignationOnStoredThings();
            }
        }

        private static Verse.Zone NewZone(Map map, string kind, List<Verse.Zone> touched)
        {
            Verse.Zone z;
            switch (kind)
            {
                case "growing": z = new Zone_Growing(map.zoneManager); break;
                case "dumping": z = new Zone_Stockpile(StorageSettingsPreset.DumpingStockpile, map.zoneManager); break;
                default: z = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager); break;
            }
            map.zoneManager.RegisterZone(z);
            touched.Add(z);
            return z;
        }

        // ------------------------------------------------------------------
        // zone shrink
        // ------------------------------------------------------------------
        // Gate: RimWorld/Designator_ZoneDelete.CanDesignateCell (in bounds, not
        // fogged, a zone is there) plus "and it is THIS zone" — the game's
        // shrink tool is scoped by the drag, ours by an explicit id, and
        // shrinking the neighbour by accident is the failure that would follow
        // from dropping the id check.
        private static object Shrink(Map map, VerbArgs a)
        {
            var zone = FindZoneAny(map, a.IntReq("id"));
            bool dryRun = a.Bool("dry_run", false);
            var des = new Designator_ZoneDelete_Shrink();
            des.isOrder = true;
            var targets = DesignateEngine.Resolve(map, a, DesignateEngine.MaxCellsArg(a), filterSelectsTargets: false);
            if (targets.IsThings)
                throw new VerbArgsException("zone shrink takes cells: use rect:[x,z,w,h] or cells:[P,…]");

            var rejects = new List<DesignateEngine.Reject>();
            var removed = new List<IntVec3>();
            int before = zone.CellCount;
            foreach (var c in targets.Cells)
            {
                if (!c.InBounds(map)) { rejects.Add(new DesignateEngine.Reject { At = c, Why = "out-of-bounds" }); continue; }
                if (c.Fogged(map))
                {
                    rejects.Add(new DesignateEngine.Reject
                    { At = c, Why = DesignateEngine.WhyFogged, Reason = Blockers.FoggedReason });
                    continue;
                }
                var at = map.zoneManager.ZoneAt(c);
                if (at == null) { rejects.Add(new DesignateEngine.Reject { At = c, Why = "no-zone-here" }); continue; }
                if (at != zone)
                {
                    rejects.Add(new DesignateEngine.Reject
                    {
                        At = c,
                        Why = "other-zone",
                        Reason = "cell belongs to zone '" + at.label + "' (id " + at.ID + ")",
                    });
                    continue;
                }
                if (!des.CanDesignateCell(c).Accepted)
                { rejects.Add(new DesignateEngine.Reject { At = c, Why = "not-removable" }); continue; }
                if (!dryRun) zone.RemoveCell(c);
                removed.Add(c);
            }
            // Designator_ZoneDelete.FinalizeDesignationSucceeded's own tail —
            // a shrink can strand an island, and CheckContiguous is what drops
            // it. RemoveCell already deregistered the zone if it hit zero.
            bool gone = zone.CellCount == 0;
            if (!dryRun && !gone) zone.CheckContiguous();

            var data = new Dictionary<string, object>
            {
                ["verb"] = "zone",
                ["op"] = "shrink",
                ["id"] = zone.ID,
                ["gate"] = "RimWorld/Designator_ZoneDelete.CanDesignateCell",
                ["targeted"] = targets.Count,
                ["accepted"] = removed.Count,
                ["cells_before"] = before,
                ["cells_now"] = zone.CellCount,
                ["deleted"] = gone,
                ["dry_run"] = dryRun,
                ["note"] = gone
                    ? "the zone lost its last cell and was deregistered (Zone.RemoveCell)"
                    : "CheckContiguous ran: cells stranded by the shrink were dropped too",
            };
            data["cells"] = DesignateEngine.CellsOut(removed, out int more);
            data["cells_more"] = more;
            DesignateEngine.PublishRejects(map, rejects, data);
            data["crop"] = DesignateEngine.Echo(map, targets.Cells);
            data["action"] = dryRun
                ? NoAction()
                : Act("zone", "shrink", "zone " + zone.ID + " -" + removed.Count + " cell(s)",
                    new Dictionary<string, object>
                    {
                        ["counts"] = new Dictionary<string, object>
                        {
                            ["removed"] = removed.Count,
                            ["rejected"] = rejects.Count,
                        },
                        ["ids"] = new List<object> { zone.ID },
                    });
            return data;
        }

        // ------------------------------------------------------------------
        // zone edit
        // ------------------------------------------------------------------
        private static object Edit(Map map, VerbArgs a)
        {
            var zone = FindZone(map, a.IntReq("id"));
            bool dryRun = a.Bool("dry_run", false);
            var warnings = new List<object>();
            var changed = new List<object>();
            Shape(map, zone, a, warnings, created: false, changed: changed, dryRun: dryRun);

            var data = new Dictionary<string, object>
            {
                ["verb"] = "zone",
                ["op"] = "edit",
                ["id"] = zone.ID,
                ["kind"] = KindOf(zone),
                ["fields"] = "label|priority|filter (stockpile) · label|plant|allow_sow|allow_cut (growing)",
                ["changed"] = changed,
                ["dry_run"] = dryRun,
                ["zones"] = ZonesOut(map, new List<Verse.Zone> { zone }),
            };
            if (warnings.Count > 0) data["warnings"] = warnings;
            data["crop"] = DesignateEngine.Echo(map, WorldSafe.ZoneCells(zone));
            data["action"] = dryRun || changed.Count == 0
                ? NoAction()
                : Act("zone", "edit", "zone " + zone.ID + " '" + zone.label + "'",
                    new Dictionary<string, object>
                    {
                        ["ids"] = new List<object> { zone.ID },
                        ["fields"] = changed,
                    });
            return data;
        }

        // ------------------------------------------------------------------
        // zone delete
        // ------------------------------------------------------------------
        private static object Delete(Map map, VerbArgs a)
        {
            var zone = FindZoneAny(map, a.IntReq("id"));
            bool dryRun = a.Bool("dry_run", false);
            int cells = zone.CellCount;
            string label = zone.label;
            var echo = new List<IntVec3>(WorldSafe.ZoneCells(zone));
            // Zone.Delete() with playSound:true fires Designate_ZoneDelete on
            // the camera; the overload is the same code path without it. It also
            // calls Find.Selector.Deselect(this), which is CORRECT and kept —
            // leaving a deleted zone selected is a dangling reference in the UI.
            if (!dryRun) zone.Delete(playSound: false);

            var data = new Dictionary<string, object>
            {
                ["verb"] = "zone",
                ["op"] = "delete",
                ["id"] = zone.ID,
                ["label"] = label,
                ["cells_freed"] = cells,
                ["dry_run"] = dryRun,
                ["gate"] = "Verse/Zone.Delete (the zone gizmo's CommandDeleteZoneLabel action)",
            };
            data["crop"] = DesignateEngine.Echo(map, echo);
            data["action"] = dryRun
                ? NoAction()
                : Act("zone", "delete", "zone " + zone.ID + " '" + label + "' (" + cells + " cells)",
                    new Dictionary<string, object> { ["ids"] = new List<object> { zone.ID } });
            return data;
        }

        // ------------------------------------------------------------------
        // SHAPING — the settings a zone carries
        // ------------------------------------------------------------------
        private static void Shape(Map map, Verse.Zone zone, VerbArgs a, List<object> warnings,
            bool created, List<object> changed = null, bool dryRun = false)
        {
            void Note(string field, object value)
            {
                changed?.Add(new Dictionary<string, object> { ["field"] = field, ["value"] = value });
            }

            // --- label ---------------------------------------------------
            // Zone implements IRenameable; Dialog_RenameZone writes
            // RenamableLabel, which is `label`. A zone created by this call
            // takes the label as its name; an edit renames it.
            if (a.Has("label"))
            {
                string label = a.Str("label");
                if (string.IsNullOrEmpty(label) || label.Length > 60)
                    throw new VerbArgsException("zone label must be 1..60 characters");
                if (!dryRun) zone.label = label;
                Note("label", label);
            }

            // NO `hidden`, deliberately. The game has the toggle
            // (Command_Hide_ZoneStockpile / Command_Hide_ZoneGrow ->
            // `zone.Hidden = !zone.Hidden`), but the SETTER does
            // `foreach (IntVec3 cell in Cells)` — Zone.Cells, the Fisher-Yates
            // getter that permanently reorders a SCRIBED list and advances the
            // shared Rand stream (WorldSafe Class R). It is purely a rendering
            // toggle that no work giver reads, so the agent gains nothing and
            // the save pays for it.

            if (zone is Zone_Stockpile sp)
            {
                // --- priority --------------------------------------------
                if (a.Has("priority"))
                {
                    var pr = ParsePriority(a.Str("priority"));
                    if (!dryRun) sp.settings.Priority = pr;
                    Note("priority", pr.ToString());
                }
                // --- filter preset ---------------------------------------
                if (a.Has("filter"))
                {
                    object raw = a.Raw("filter");
                    if (!(raw is string) && !(raw is List<object>))
                        throw new VerbArgsException(
                            "filter takes a preset word or an array of them (" + PresetWords + "). "
                            + "Def-level allow/disallow, hit-point and quality ranges, special filters and "
                            + "storage groups are spec 3.6's (`IStoreSettingsParent`), not this verb's.");
                    var presets = new List<string>();
                    if (raw is string one) presets.Add(one);
                    else foreach (var p in a.StrList("filter")) presets.Add(p);
                    if (!dryRun) ApplyPreset(sp.settings, presets);
                    else foreach (var p in presets) PresetCategory(p);   // validate
                    Note("filter", presets.ToArray());
                }
            }
            else if (zone is Zone_Growing zg)
            {
                // --- plant -----------------------------------------------
                if (a.Has("plant"))
                {
                    var plant = Dev.Named<ThingDef>(a.Str("plant"), "plant");
                    string why = CanGrowHere(map, zg, plant);
                    if (why != null)
                        throw new VerbArgsException(
                            $"'{plant.defName}' cannot be sown in zone {zone.ID}: {why} "
                            + "(Command_SetPlantToGrow.ProcessInput's own list gate)");
                    // The SETTER is a plain field write; the GETTER is the one
                    // that scribes a default (WorldSafe Class A).
                    if (!dryRun) zg.SetPlantDefToGrow(plant);
                    Note("plant", plant.defName);
                    // Command_SetPlantToGrow's own post-set warnings, reported as
                    // data rather than posted as Messages. The sowMinSkill one is
                    // deliberately NOT reproduced by opening its Dialog_MessageBox:
                    // that window force-pauses and would halt every later advance
                    // (JOURNAL.md, spec 1.7).
                    if (plant.plant != null && plant.plant.interferesWithRoof && AnyRoofed(map, zg))
                        warnings.Add("plant interferes with roof and part of the zone is roofed "
                            + "(MessagePlantIncompatibleWithRoof)");
                    if (plant.plant != null && plant.plant.sowMinSkill > 0 && !AnyGrower(map, plant.plant.sowMinSkill))
                        warnings.Add("no available grower has Plants " + plant.plant.sowMinSkill
                            + " (NoGrowerCanPlant); nothing will be sown");
                }
                else if (created)
                {
                    // Only reachable via allow_unset_plant:true — say so in the
                    // result, since `zones` will report plant_configured:false.
                    warnings.Add("no plant set: this zone grows the game's default "
                        + "(Zone_Growing.PlantDefToGrow assigns Potato or Toxipotato on first read)");
                }
                if (a.Has("allow_sow"))
                {
                    bool v = a.Bool("allow_sow", true);
                    if (!dryRun) zg.allowSow = v;   // CommandAllowSow toggle
                    Note("allow_sow", v);
                }
                if (a.Has("allow_cut"))
                {
                    bool v = a.Bool("allow_cut", true);
                    if (!dryRun) zg.allowCut = v;   // CommandAllowCut toggle
                    Note("allow_cut", v);
                }
                if (a.Has("priority") || a.Has("filter"))
                    throw new VerbArgsException("priority and filter apply to stockpile zones, not growing zones");
            }
        }

        // ------------------------------------------------------------------
        // THE FIVE PRESETS — this spec's whole share of the storage filter
        // ------------------------------------------------------------------
        public const string PresetWords = "food|meds|apparel|weapons|raw|all|none";

        private static ThingCategoryDef PresetCategory(string preset)
        {
            switch (preset)
            {
                case "food": return ThingCategoryDefOf.Foods;
                case "meds": case "medicine": return ThingCategoryDefOf.Medicine;
                case "apparel": return ThingCategoryDefOf.Apparel;
                case "weapons": return ThingCategoryDefOf.Weapons;
                case "raw": case "resources": return ThingCategoryDefOf.ResourcesRaw;
                case "all": case "none": return null;
                default:
                    throw new VerbArgsException($"unknown filter preset '{preset}' ({PresetWords})");
            }
        }

        // SetDisallowAll then one SetAllow per preset — the Clear-All button
        // (Verse/ThingFilterUI.DoThingFilterConfigWindow) followed by the
        // category checkboxes (Verse/Listing_TreeThingFilter.DoCategoryChildren).
        // A REPLACEMENT, never a patch: two calls with the same presets leave
        // the same filter, which is what makes a fixture reproducible.
        private static void ApplyPreset(StorageSettings settings, List<string> presets)
        {
            if (settings?.filter == null) return;
            var cats = new List<ThingCategoryDef>();
            bool allowAll = false;
            foreach (var p in presets)
            {
                var cat = PresetCategory(p);
                if (p == "all") allowAll = true;
                else if (cat != null) cats.Add(cat);
            }
            settings.filter.SetDisallowAll();
            if (allowAll)
            {
                // The Allow-All button's own argument: the parent filter, i.e.
                // exactly what this storage could ever hold.
                settings.filter.SetAllowAll(settings.owner?.GetParentStoreSettings()?.filter);
                return;
            }
            for (int i = 0; i < cats.Count; i++) settings.filter.SetAllow(cats[i], allow: true);
        }

        private static StoragePriority ParsePriority(string s)
        {
            if (s == null) throw new VerbArgsException("priority must be a string");
            foreach (StoragePriority v in Enum.GetValues(typeof(StoragePriority)))
            {
                if (!string.Equals(v.ToString(), s, StringComparison.OrdinalIgnoreCase)) continue;
                // ITab_Storage.FillTab's dropdown skips Unstored, so a player
                // cannot choose it and neither can the agent: a stockpile at
                // Unstored is not a stockpile, it is a hole in the haul graph.
                if (v == StoragePriority.Unstored)
                    throw new VerbArgsException(
                        "priority 'Unstored' is not selectable (RimWorld/ITab_Storage.FillTab skips it); "
                        + "use Low|Normal|Preferred|Important|Critical");
                return v;
            }
            throw new VerbArgsException("priority must be Low|Normal|Preferred|Important|Critical");
        }

        // Command_SetPlantToGrow.ProcessInput's list gate, per def:
        //   PlantUtility.ValidPlantTypesForGrowers  -> category == Plant
        //                                             && CanSowOnGrower(def, zone)
        //   Command_SetPlantToGrow.IsPlantAvailable -> sow research prerequisites,
        //                                             permanent darkness, must-be-wild
        // The ONE deviation: IsPlantAvailable tests `ResearchProjectDef.IsFinished`,
        // which inserts a zero entry into a scribed dictionary on a miss
        // (WorldSafe Class A); `WorldSafe.Finished` is the same arithmetic
        // without the write. Returns null when the plant is choosable, else why not.
        private static string CanGrowHere(Map map, Zone_Growing zone, ThingDef plant)
        {
            if (plant.category != ThingCategory.Plant || plant.plant == null)
                return "not a plant";
            try
            {
                if (!PlantUtility.CanSowOnGrower(plant, zone))
                    return plant.plant.sowTags != null && !plant.plant.sowTags.Contains("Ground")
                        ? "cannot be sown on the ground (sowTags: "
                            + string.Join(",", plant.plant.sowTags.ToArray()) + ")"
                        : "cannot be sown in this grower (pollution, or the wrong sow tag)";
            }
            catch (Exception e) { return "sow check threw: " + e.Message; }

            var prereqs = plant.plant.sowResearchPrerequisites;
            if (prereqs != null)
                for (int i = 0; i < prereqs.Count; i++)
                    if (!WorldSafe.Finished(prereqs[i]))
                        return "research '" + prereqs[i].defName + "' is not finished";
            try
            {
                if (plant.plant.mustBePermanentDarknessToSow && !map.gameConditionManager.IsAlwaysDarkOutside)
                    return "must be sown in permanent darkness";
                if (plant.plant.mustBeWildToSow && !map.wildPlantSpawner.AllWildPlants.Contains(plant))
                    return "does not grow wild in this biome";
            }
            catch (Exception e) { return "availability check threw: " + e.Message; }
            return null;
        }

        private static bool AnyRoofed(Map map, Verse.Zone zone)
        {
            var cells = WorldSafe.ZoneCells(zone);   // never Zone.Cells — it shuffles
            for (int i = 0; i < cells.Count; i++)
                if (cells[i].InBounds(map) && cells[i].Roofed(map)) return true;
            return false;
        }

        private static bool AnyGrower(Map map, int minSkill)
        {
            try
            {
                foreach (var p in map.mapPawns.FreeColonistsSpawned)
                {
                    if (p == null || p.Downed || p.skills == null || p.workSettings == null) continue;
                    if (p.skills.GetSkill(SkillDefOf.Plants).Level >= minSkill
                        && p.workSettings.WorkIsActive(WorkTypeDefOf.Growing)) return true;
                }
            }
            catch { return true; }   // unknown, so do not cry wolf
            return false;
        }

        // ------------------------------------------------------------------
        // LOOKUP + OUTPUT
        // ------------------------------------------------------------------
        private static string KindOf(Verse.Zone z)
        {
            if (z is Zone_Growing) return "growing";
            if (z is Zone_Stockpile) return "stockpile";
            return null;
        }

        private static Designator MakeDesignator(string kind)
        {
            switch (kind)
            {
                case "stockpile": return new Designator_ZoneAddStockpile_Resources();
                case "dumping": return new Designator_ZoneAddStockpile_Dumping();
                case "growing": return new Designator_ZoneAdd_Growing();
                default:
                    throw new VerbArgsException("zone kind must be stockpile|dumping|growing "
                        + "(fishing zones are Odyssey-only and research-gated; see git-bug 57ab92a)");
            }
        }

        // Fog: a zone every cell of which is unexplored is not addressable, the
        // same rule 2.4's `zones` reports it under.
        private static Verse.Zone FindZoneAny(Map map, int id)
        {
            var all = map.zoneManager.AllZones;   // the real backing list
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] == null || all[i].ID != id) continue;
                if (WorldSafe.ZoneHidden(all[i], map, out _))
                    throw new VerbArgsException(
                        $"zone {id} lies entirely in unexplored ground and is not addressable");
                return all[i];
            }
            throw new VerbArgsException($"no zone with id {id} on the current map");
        }

        private static Verse.Zone FindZone(Map map, int id)
        {
            var z = FindZoneAny(map, id);
            if (KindOf(z) == null)
                throw new VerbArgsException(
                    $"zone {id} is a {z.GetType().Name}, which this verb does not shape "
                    + "(stockpile|dumping|growing only)");
            return z;
        }

        private static List<object> IdList(List<Verse.Zone> zones)
        {
            var list = new List<object>();
            for (int i = 0; i < zones.Count; i++) list.Add(zones[i].ID);
            return list;
        }

        // A compact echo of each zone this call touched. Reading a zone in full
        // is 2.4's `zones`; this is the join key plus the fields the caller just
        // set, so a scripted step can assert without a second round trip.
        private static List<object> ZonesOut(Map map, List<Verse.Zone> zones)
        {
            var list = new List<object>();
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                var cells = WorldSafe.ZoneCells(z);
                var d = new Dictionary<string, object>
                {
                    ["id"] = z.ID,
                    ["kind"] = KindOf(z),
                    ["label"] = z.label,
                    ["cells"] = z.CellCount,
                    ["at"] = cells.Count > 0 ? Positions.Out(cells[0]) : null,
                };
                if (z is Zone_Stockpile sp)
                {
                    d["priority"] = WorldSafe.Safe(() => sp.settings?.Priority.ToString());
                    d["space_remaining"] = WorldSafe.SafeObj(() => (object)sp.SpaceRemaining);
                    try
                    {
                        d["filter"] = FilterSummary.Build(sp.settings?.filter,
                            sp.GetParentStoreSettings()?.filter, "storable");
                    }
                    catch { }
                }
                else if (z is Zone_Growing zg)
                {
                    var plant = WorldSafe.PlantToGrow(zg);   // never the scribing getter
                    d["plant"] = plant?.defName;
                    d["plant_configured"] = plant != null;
                    d["plant_source"] = WorldSafe.PlantRefOk ? "backing-field" : "unavailable";
                    d["allow_sow"] = zg.allowSow;
                    d["allow_cut"] = zg.allowCut;
                }
                list.Add(d);
            }
            return list;
        }

        // ------------------------------------------------------------------
        // The `action` journal event. Private static, in this file, on purpose —
        // 3.4's worker is writing the same helper in parallel and a shared
        // public class would collide at merge; the orchestrator factors it then.
        // Mirrors the `dev` row ({verb, step, target} + additive extras) but is
        // NOT a cheat, so it carries neither `cheat` nor `fog_exempt`. Journal
        // .Emit returns 0 when the writer is closed, and that is reported rather
        // than hidden — Dev.Stamp's discipline, same words.
        // ------------------------------------------------------------------
        private static Dictionary<string, object> Act(string verb, string step, string target,
            Dictionary<string, object> extra)
        {
            var payload = new Dictionary<string, object>
            {
                ["verb"] = verb,
                ["step"] = step,
                ["target"] = target,
            };
            if (extra != null)
                foreach (var kv in extra)
                    if (!payload.ContainsKey(kv.Key) && kv.Value != null) payload[kv.Key] = kv.Value;
            int tick = 0;
            try { tick = Find.TickManager.TicksGame; } catch { }
            long seq = Journal.Emit("action", payload, tick);
            var d = new Dictionary<string, object> { ["journal_seq"] = seq };
            if (seq == 0)
                d["provenance"] = "NOT WRITTEN — the journal writer is closed, so this mutation has no "
                    + "journal line. Treat any state changed in this session as unprovenanced.";
            return d;
        }

        private static Dictionary<string, object> NoAction()
            => new Dictionary<string, object>
            {
                ["journal_seq"] = null,
                ["provenance"] = "not applicable — nothing was mutated",
            };
    }
}
