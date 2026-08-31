using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ============================================================ spec 3.2 ===
    // AREA VERBS — the areas themselves.
    //
    //   area {kind} add|remove  {rect|cells}     paint / clear an area's cells
    //   area allowed create     [name]           a new Area_Allowed
    //   area allowed rename     {id, name}
    //   area allowed delete     {id}
    //   area allowed invert     {id}
    //
    // kinds: home | allowed | build-roof | no-roof | ignore-roof | snow-clear |
    //        pollution-clear.  Reading areas is 2.4's `areas`.
    //
    // ------------------- WHAT IS DELIBERATELY NOT HERE ----------------------
    // **Assigning a PAWN to an allowed area is spec 3.4's**, settled by the
    // orchestrator mid-build. That write is
    // `Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap` — a pawn field
    // driven by `PawnColumnWorker_AllowedArea` in the Assign tab, beside
    // medCare/hostilityResponse/selfTend, with its own widget gate
    // (`SupportsAllowedAreas`, player faction) and a job-interrupt side effect
    // in the setter. This file creates and shapes areas; it never assigns one.
    //
    // ------------------- THE GATE LIVES IN THE WIDGET -----------------------
    //  * home        — `RimWorld/Designator_AreaHome.CanDesignateCell`
    //                  (`Designator_AreaHomeExpand` / `…Clear` are the two modes).
    //  * build-roof  — `RimWorld/Designator_AreaBuildRoof.CanDesignateCell`
    //  * no-roof     — `RimWorld/Designator_AreaNoRoof.CanDesignateCell`
    //                  (refuses thick roof with the game's own
    //                  "MessageNothingCanRemoveThickRoofs")
    //  * ignore-roof — `RimWorld/Designator_AreaIgnoreRoof` — this IS the game's
    //                  "remove from the roof areas" tool: its DesignateSingleCell
    //                  clears BuildRoof AND NoRoof. There is no separate clear
    //                  designator for either one, so `remove` on build-roof /
    //                  no-roof is refused and names ignore-roof instead.
    //  * snow-clear  — `RimWorld/Designator_AreaSnowClear{Expand,Clear}`
    //  * pollution-clear — `RimWorld/Designator_AreaPollutionClear{Expand,Clear}`
    //                  (Biotech; the area does not exist without it)
    //  * allowed create/rename/delete — `RimWorld/Dialog_ManageAreas`:
    //      new    -> gated on `AreaManager.CanMakeNewAllowed()` (max 10)
    //      rename -> `Verse/Dialog_RenameArea.NameIsValid` (non-empty,
    //                <= Dialog_Rename.MaxNameLength = 28, unique across AllAreas)
    //      delete -> `Area.Delete()`, and the dialog only lists `area.Mutable`
    //                rows, so Home/BuildRoof/NoRoof cannot be deleted
    //      invert -> `Area.Invert()`, the dialog's own "InvertArea" button
    //
    // ------------------- ONE GATE WE REPRODUCE RATHER THAN DRIVE ------------
    // `Designator_AreaAllowedExpand.CanDesignateCell` is
    // `c.InBounds(Map) && Designator_AreaAllowed.SelectedArea != null &&
    // !SelectedArea[c]` — it reads a PUBLIC STATIC UI field
    // (`Designator_AreaAllowed.selectedArea`) that `Dialog_ManageAreas
    // .SelectDesignator` writes when the player clicks Expand. Driving that
    // designator would mean assigning the player's currently-selected paint
    // target as a side effect of an agent call, so the three-clause gate is
    // reproduced here against the area the caller NAMED and the static is left
    // alone. Same logic, no UI mutation.
    //
    // ------------------------- HAZARDS --------------------------------------
    //  * `Designator_AreaNoRoof` keeps a STATIC `justAddedCells` list that only
    //    `FinalizeDesignationSucceeded` drains — and that method is also where
    //    `BuildRoof[c] = false` happens for the cells just added. So Finalize IS
    //    called on success (DesignateEngine.FinalizeSucceeded); skipping it would
    //    both lose the BuildRoof clear and leak our cells into the next real
    //    player drag.
    //  * `new Area_Allowed(...)` rolls `Rand.Value` twice for its colour and so
    //    advances the shared RNG (_mp/DETERMINISM.md class R). That is the
    //    game's own code on the player's own path — the "New area" button does
    //    exactly this — so it is faithful rather than avoidable, and it is named
    //    here so a determinism post-mortem does not have to find it twice.
    // =========================================================================
    public static class AreaVerbs
    {
        public const string KindWords =
            "home|allowed|build-roof|no-roof|ignore-roof|snow-clear|pollution-clear";

        [Verb("area")]
        public static object Area(VerbContext ctx)
        {
            var map = DesignateEngine.Map();
            var a = ctx.Args;
            string kind = a.Str("kind", "home");
            string op = a.Str("op", "add");

            if (kind == "allowed")
                switch (op)
                {
                    case "create": return Create(map, a);
                    case "rename": return Rename(map, a);
                    case "delete": return DeleteArea(map, a);
                    case "invert": return Invert(map, a);
                }
            switch (op)
            {
                case "add":
                case "remove":
                    return Paint(map, a, kind, op == "add");
                case "create":
                case "rename":
                case "delete":
                case "invert":
                    throw new VerbArgsException(
                        $"op '{op}' applies to kind 'allowed' only (the built-in areas are not creatable, "
                        + "renamable or deletable — Area.Mutable is false, RimWorld/Dialog_ManageAreas "
                        + "lists only mutable rows)");
                default:
                    throw new VerbArgsException("area op must be add|remove (any kind) "
                        + "or create|rename|delete|invert (kind 'allowed')");
            }
        }

        // ------------------------------------------------------------------
        // PAINT / CLEAR
        // ------------------------------------------------------------------
        private static object Paint(Map map, VerbArgs a, string kind, bool add)
        {
            bool dryRun = a.Bool("dry_run", false);
            var targets = DesignateEngine.Resolve(map, a, DesignateEngine.MaxCellsArg(a));
            if (targets.IsThings)
                throw new VerbArgsException("area takes cells: use rect:[x,z,w,h] or cells:[P,…]");

            var accepted = new List<IntVec3>();
            var rejects = new List<DesignateEngine.Reject>();
            string gate;
            Verse.Area area = null;
            int before, after;

            if (kind == "allowed")
            {
                area = FindAllowed(map, a);
                gate = add
                    ? "RimWorld/Designator_AreaAllowedExpand.CanDesignateCell (reproduced — see the class header)"
                    : "RimWorld/Designator_AreaAllowedClear.CanDesignateCell (reproduced — see the class header)";
                before = area.TrueCount;
                foreach (var c in targets.Cells)
                {
                    if (!c.InBounds(map)) { rejects.Add(new DesignateEngine.Reject { At = c, Why = "out-of-bounds" }); continue; }
                    // Fog is OUR uniform rule; the two allowed-area designators
                    // do not check it themselves (only InBounds and the current
                    // value), so this is a gate we ADD rather than reproduce,
                    // and it is added for the reason DESIGN's decisions log
                    // gives: the agent may not shape ground it has never seen.
                    if (c.Fogged(map))
                    {
                        rejects.Add(new DesignateEngine.Reject
                        { At = c, Why = DesignateEngine.WhyFogged, Reason = Blockers.FoggedReason });
                        continue;
                    }
                    if (area[c] == add)
                    {
                        rejects.Add(new DesignateEngine.Reject
                        { At = c, Why = add ? "already-in-area" : "not-in-area" });
                        continue;
                    }
                    if (!dryRun) area[c] = add;
                    accepted.Add(c);
                }
                after = area.TrueCount;
            }
            else
            {
                var des = MakeDesignator(map, kind, add, out gate, out area);
                des.isOrder = true;
                before = area?.TrueCount ?? -1;
                DesignateEngine.RunCells(map, des, targets.Cells, dryRun, accepted, rejects);
                if (!dryRun) DesignateEngine.FinalizeSucceeded(des, accepted.Count > 0);
                after = area?.TrueCount ?? -1;
            }

            var data = new Dictionary<string, object>
            {
                ["verb"] = "area",
                ["op"] = add ? "add" : "remove",
                ["kind"] = kind,
                ["gate"] = gate,
                ["area"] = area == null ? null : new Dictionary<string, object>
                {
                    ["id"] = area.ID,
                    ["label"] = WorldSafe.Safe(() => area.Label),
                    ["type"] = area.GetType().Name,
                },
                ["targeted"] = targets.Count,
                ["requested"] = targets.Requested,
                ["capped"] = targets.Capped,
                ["target_scope"] = targets.Detail,
                ["accepted"] = accepted.Count,
                // null when the kind writes more than one area (ignore-roof
                // zeroes BuildRoof and NoRoof), so a single count would be a lie.
                ["cells_before"] = before < 0 ? null : (object)before,
                ["cells_now"] = after < 0 ? null : (object)after,
                ["dry_run"] = dryRun,
            };
            if (kind == "ignore-roof")
                data["note"] = "ignore-roof is the game's clear tool for BOTH roof areas: "
                    + "Designator_AreaIgnoreRoof.DesignateSingleCell zeroes BuildRoof and NoRoof";
            data["cells"] = DesignateEngine.CellsOut(accepted, out int more);
            data["cells_more"] = more;
            DesignateEngine.PublishRejects(map, rejects, data);
            data["crop"] = DesignateEngine.Echo(map, targets.Cells);
            data["action"] = dryRun
                ? NoAction()
                : Act("area", (add ? "add:" : "remove:") + kind,
                    accepted.Count + " cell(s) of " + (WorldSafe.Safe(() => area?.Label) ?? kind),
                    new Dictionary<string, object>
                    {
                        ["counts"] = new Dictionary<string, object>
                        {
                            ["targeted"] = targets.Count,
                            ["accepted"] = accepted.Count,
                            ["rejected"] = rejects.Count,
                        },
                        ["cells"] = data["cells"],
                        ["rejected_by_reason"] = data["rejects_by_reason"],
                    });
            return data;
        }

        private static Designator MakeDesignator(Map map, string kind, bool add,
            out string gate, out Verse.Area area)
        {
            switch (kind)
            {
                case "home":
                    area = map.areaManager.Home;
                    gate = "RimWorld/Designator_AreaHome.CanDesignateCell";
                    return add ? (Designator)new Designator_AreaHomeExpand() : new Designator_AreaHomeClear();
                case "build-roof":
                    if (!add) throw NoClearTool("build-roof");
                    area = map.areaManager.BuildRoof;
                    gate = "RimWorld/Designator_AreaBuildRoof.CanDesignateCell";
                    return new Designator_AreaBuildRoof();
                case "no-roof":
                    if (!add) throw NoClearTool("no-roof");
                    area = map.areaManager.NoRoof;
                    gate = "RimWorld/Designator_AreaNoRoof.CanDesignateCell";
                    return new Designator_AreaNoRoof();
                case "ignore-roof":
                    if (!add)
                        throw new VerbArgsException(
                            "ignore-roof has no 'remove': it IS the removal (it clears BuildRoof and NoRoof). "
                            + "Use `area build-roof add` or `area no-roof add` to put cells back.");
                    area = null;   // it writes two areas, so neither is "the" area
                    gate = "RimWorld/Designator_AreaIgnoreRoof.CanDesignateCell";
                    return new Designator_AreaIgnoreRoof();
                case "snow-clear":
                    area = map.areaManager.SnowOrSandClear;
                    gate = "RimWorld/Designator_AreaSnowClear.CanDesignateCell";
                    return add
                        ? (Designator)new Designator_AreaSnowClearExpand()
                        : new Designator_AreaSnowClearClear();
                case "pollution-clear":
                    if (!ModsConfig.BiotechActive)
                        throw new VerbArgsException("pollution-clear needs Biotech (the area does not exist without it)");
                    area = map.areaManager.PollutionClear;
                    if (area == null)
                        throw new VerbArgsException("this map has no pollution-clear area");
                    gate = "RimWorld/Designator_AreaPollutionClear.CanDesignateCell";
                    return add
                        ? (Designator)new Designator_AreaPollutionClearExpand()
                        : new Designator_AreaPollutionClearClear();
                default:
                    throw new VerbArgsException($"unknown area kind '{kind}' ({KindWords})");
            }
        }

        private static VerbArgsException NoClearTool(string kind)
            => new VerbArgsException(
                $"'{kind}' has no clear designator in the game — RimWorld/Designator_AreaIgnoreRoof is the "
                + "one tool that removes cells from the roof areas, and it clears BOTH. "
                + "Use kind:\"ignore-roof\" op:\"add\".");

        // ------------------------------------------------------------------
        // allowed create / rename / delete / invert
        // ------------------------------------------------------------------
        private static object Create(Map map, VerbArgs a)
        {
            bool dryRun = a.Bool("dry_run", false);
            // Dialog_ManageAreas.DoWindowContents only draws "NewArea" when
            // CanMakeNewAllowed() — ten Area_Allowed per map (AreaManager
            // .MaxAllowedAreas).
            if (!map.areaManager.CanMakeNewAllowed())
                throw new VerbArgsException(
                    "the map already has the maximum of 10 allowed areas "
                    + "(AreaManager.CanMakeNewAllowed; the game's own MaxAreasReached)");
            string name = a.Str("name");
            if (name != null) ValidateName(map, name, null);

            Area_Allowed area = null;
            if (!dryRun)
            {
                if (!map.areaManager.TryMakeNewAllowed(out area))
                    throw new VerbArgsException("AreaManager.TryMakeNewAllowed refused");
                if (name != null) area.SetLabel(name);
            }
            var data = new Dictionary<string, object>
            {
                ["verb"] = "area",
                ["op"] = "create",
                ["kind"] = "allowed",
                ["gate"] = "RimWorld/Dialog_ManageAreas.DoWindowContents (AreaManager.CanMakeNewAllowed)",
                ["id"] = area?.ID,
                ["label"] = area == null ? name : WorldSafe.Safe(() => area.Label),
                ["allowed_areas_now"] = CountAllowed(map),
                ["dry_run"] = dryRun,
                ["note"] = "a new area is EMPTY; paint it with `area allowed add {id, rect}`. "
                    + "Assigning a pawn to it is spec 3.4's verb, not this one.",
            };
            data["action"] = dryRun
                ? NoAction()
                : Act("area", "create:allowed", WorldSafe.Safe(() => area.Label) ?? "?",
                    new Dictionary<string, object> { ["ids"] = new List<object> { area.ID } });
            return data;
        }

        private static object Rename(Map map, VerbArgs a)
        {
            var area = FindAllowed(map, a);
            string name = a.StrReq("name");
            ValidateName(map, name, area);
            bool dryRun = a.Bool("dry_run", false);
            string was = WorldSafe.Safe(() => area.Label);
            if (!dryRun) (area as Area_Allowed)?.SetLabel(name);
            var data = new Dictionary<string, object>
            {
                ["verb"] = "area",
                ["op"] = "rename",
                ["kind"] = "allowed",
                ["gate"] = "Verse/Dialog_RenameArea.NameIsValid (non-empty, <= 28, unique across AllAreas)",
                ["id"] = area.ID,
                ["was"] = was,
                ["label"] = name,
                ["dry_run"] = dryRun,
            };
            data["action"] = dryRun
                ? NoAction()
                : Act("area", "rename:allowed", was + " -> " + name,
                    new Dictionary<string, object> { ["ids"] = new List<object> { area.ID } });
            return data;
        }

        private static object DeleteArea(Map map, VerbArgs a)
        {
            var area = FindAllowed(map, a);
            bool dryRun = a.Bool("dry_run", false);
            string label = WorldSafe.Safe(() => area.Label);
            int cells = area.TrueCount;
            // AreaManager.Remove refuses a non-Mutable area with a red error, and
            // Dialog_ManageAreas never offers the button for one. FindAllowed
            // already restricted us to Area_Allowed, so this is belt and braces
            // against a mod's own mutable-but-undeletable area.
            if (!area.Mutable)
                throw new VerbArgsException($"area {area.ID} is not deletable (Area.Mutable is false)");
            if (!dryRun) area.Delete();
            var data = new Dictionary<string, object>
            {
                ["verb"] = "area",
                ["op"] = "delete",
                ["kind"] = "allowed",
                ["gate"] = "RimWorld/Dialog_ManageAreas.DoAreaRow (Area.Delete, mutable rows only)",
                ["id"] = area.ID,
                ["label"] = label,
                ["cells_freed"] = cells,
                ["allowed_areas_now"] = CountAllowed(map),
                ["dry_run"] = dryRun,
                ["note"] = "every pawn restricted to it was notified "
                    + "(AreaManager.NotifyEveryoneAreaRemoved -> Pawn_PlayerSettings.Notify_AreaRemoved)",
            };
            data["action"] = dryRun
                ? NoAction()
                : Act("area", "delete:allowed", label + " (" + cells + " cells)",
                    new Dictionary<string, object> { ["ids"] = new List<object> { area.ID } });
            return data;
        }

        private static object Invert(Map map, VerbArgs a)
        {
            var area = FindAllowed(map, a);
            bool dryRun = a.Bool("dry_run", false);
            int before = area.TrueCount;
            if (!dryRun) area.Invert();   // Dialog_ManageAreas' "InvertArea" button
            var data = new Dictionary<string, object>
            {
                ["verb"] = "area",
                ["op"] = "invert",
                ["kind"] = "allowed",
                ["gate"] = "RimWorld/Dialog_ManageAreas.DoAreaRow (Area.Invert)",
                ["id"] = area.ID,
                ["label"] = WorldSafe.Safe(() => area.Label),
                ["cells_before"] = before,
                ["cells_now"] = area.TrueCount,
                ["dry_run"] = dryRun,
                // Invert is whole-map and therefore includes ground the colony
                // has never explored. The verb is the game's own button, so it
                // is offered as-is, but the fog note is owed.
                ["note"] = "Area.Invert flips EVERY cell on the map, unexplored ground included — "
                    + "the one area write that is not fog-gated, because the game's own button is not",
            };
            data["action"] = dryRun
                ? NoAction()
                : Act("area", "invert:allowed", WorldSafe.Safe(() => area.Label) ?? "?",
                    new Dictionary<string, object> { ["ids"] = new List<object> { area.ID } });
            return data;
        }

        // ------------------------------------------------------------------
        private static void ValidateName(Map map, string name, Verse.Area self)
        {
            if (string.IsNullOrEmpty(name)) throw new VerbArgsException("area name must not be empty");
            if (name.Length > 28)
                throw new VerbArgsException("area name must be at most 28 characters "
                    + "(Verse/Dialog_Rename.MaxNameLength)");
            var all = map.areaManager.AllAreas;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i] != self && all[i].Label == name)
                    throw new VerbArgsException($"area name '{name}' is already in use "
                        + "(Verse/Dialog_RenameArea.NameIsValid)");
        }

        private static Verse.Area FindAllowed(Map map, VerbArgs a)
        {
            var all = map.areaManager.AllAreas;
            if (a.Has("id"))
            {
                int id = a.IntReq("id");
                for (int i = 0; i < all.Count; i++)
                    if (all[i] is Area_Allowed && all[i].ID == id) return all[i];
                throw new VerbArgsException($"no allowed area with id {id}");
            }
            if (a.Has("name"))
            {
                string name = a.Str("name");
                for (int i = 0; i < all.Count; i++)
                    if (all[i] is Area_Allowed && all[i].Label == name) return all[i];
                throw new VerbArgsException($"no allowed area named '{name}'");
            }
            throw new VerbArgsException("kind 'allowed' needs id (or name) to say which area");
        }

        private static int CountAllowed(Map map)
        {
            int n = 0;
            var all = map.areaManager.AllAreas;
            for (int i = 0; i < all.Count; i++) if (all[i] is Area_Allowed) n++;
            return n;
        }

        // ------------------------------------------------------------------
        // The `action` journal event — see DesignationVerbs.Act for the contract
        // and why this helper is private and duplicated rather than shared.
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
