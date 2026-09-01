using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ======================================================= spec 3.6 =========
    // STORAGE SETTINGS — priority, filters and storage GROUPS, for stockpile
    // zones and storage buildings alike.
    //
    //   storage        {id?, cap?}                       READ  (the Storage tab, as data)
    //   storage-set    {targets, priority?, filter?, allow?, disallow?, special?,
    //                   hp_range?, quality_range?, copy_from?}
    //   storage-link   {targets}                         the Link Storage Settings gizmo
    //   storage-unlink {targets}                         the Unlink gizmo
    //
    // In this file rather than its own spec because `Zone_Stockpile` and
    // `Building_Storage` share ONE `IStoreSettingsParent` API and splitting
    // them would split one trap; on the same partial class as the bill verbs
    // because a bill's output goes somewhere and `bill-set {store_target}`
    // needs exactly the resolution this file does.
    //
    // -------------------- WHERE 3.2's BOUNDARY IS ---------------------------
    // `ZoneVerbs.cs` (spec 3.2, shipped) already owns creating and SHAPING a
    // stockpile zone, and already ships `ParsePriority` and five whole-filter
    // category presets. That line is unchanged: presets are a whole-filter
    // REPLACEMENT over a fixed five-word vocabulary, and everything finer —
    // def-level allow/disallow, hit-point and quality ranges, special filters,
    // copy between storages, storage GROUPS, and all of it for BUILDINGS as
    // well as zones — is here. `zone edit {filter:…}` refuses an object with a
    // message naming this spec; this is the verb it names.
    //
    // ================= THE GATE LIVES IN THE WIDGET ==========================
    //  * `RimWorld/ITab_Storage.cs IsVisible` — the tab is hidden outright for
    //    a Thing whose `Faction` is non-null and not the player's. Reproduced.
    //  * `ITab_Storage.IsVisible` also ends in `SelStoreSettingsParent
    //    ?.StorageTabVisible ?? false`, and two overrides matter:
    //    `RimWorld/Building_CorpseCasket.cs StorageTabVisible => !HasCorpse`,
    //    and `RimWorld/Building_Grave.cs` narrows it further to
    //    `base.StorageTabVisible ? AssignedPawn == null : false`. So an
    //    occupied OR assigned grave has no storage tab at all — and the spec
    //    lists graves. Reproduced.
    //  * `ITab_Storage.FillTab`'s priority dropdown is built with
    //    `if (value != StoragePriority.Unstored)`, so Unstored is unreachable.
    //    Same exclusion 3.2's `ParsePriority` already makes.
    //  * `ITab_Storage.IsPrioritySettingVisible` is `protected virtual` and
    //    `RimWorld/ITab_Shells.cs` and `RimWorld/ITab_BiosculpterNutritionStorage.cs`
    //    override it to FALSE. A turret's shell filter is configurable and its
    //    PRIORITY is not — the game draws no such control. Read off the def's
    //    own resolved tab, so a modded ITab_Storage subclass is honoured too.
    //  * `Verse/ThingFilterUI.cs DoThingFilterConfigWindow`'s Clear-All /
    //    Allow-All buttons are `SetDisallowAll(...)` / `SetAllowAll(parentFilter)`;
    //    `Verse/Listing_TreeThingFilter.cs` draws the per-category and per-def
    //    checkboxes, and it only ever lists defs the PARENT filter allows.
    //  * `RimWorld/StorageGroupUtility.cs StorageGroupMemberGizmos` — the Link
    //    button is disabled below two members ("LinkStorageDisabledSelectTwo")
    //    and when every selected member already shares one group
    //    ("AlreadyLinked"); the candidate set is filtered by matching
    //    `StorageGroupTag`, same Map, and `!(Building_Storage {
    //    StorageTabVisible: false })`. The Unlink button is not yielded at all
    //    for an ungrouped member. All reproduced; `Find.Selector` is replaced
    //    by the `targets` argument, because a headless player verb must not
    //    read or write the player's selection.
    //
    // ---------------------------- HAZARDS ------------------------------------
    //  * THE SHADOWED SETTINGS OBJECT, and it is the spec's own acceptance
    //    bullet. `RimWorld/Building_Storage.cs GetStoreSettings()` is
    //        if (storageGroup != null) return storageGroup.GetStoreSettings();
    //        return settings;
    //    with BOTH `settings` and `storageGroup` public fields. Writing
    //    `building.settings` while the building is GROUPED edits an object
    //    nothing reads, and `StorageGroupUtility.SetStorageGroup`'s leave path
    //    later overwrites it wholesale (`member.StoreSettings.CopyFrom(
    //    groupSettings)`). Every write here goes through `GetStoreSettings()`
    //    and every result publishes `settings:"group"|"own"` so the agent knows
    //    the write was shared. `Zone_Stockpile` is not an `IStorageGroupMember`,
    //    so the trap is building-only — verified, and the reason the zone path
    //    needs no equivalent.
    //  * `StorageSettings.Priority`'s SETTER is where haul invalidation lives —
    //    `Notify_HaulDestinationChangedPriority` plus the right
    //    `listerHaulables` recalc for a group / haul destination / haul source /
    //    slot-group parent. Never the `priorityInt` backing field. It also
    //    skips ALL of that when `Current.ProgramState != Playing`, which is not
    //    our case but is why the property is the only correct route.
    //  * `ITab_Storage.FillTab` brackets the filter widget with a before/after
    //    diff over `BillUtility.GlobalBills()` and fires
    //    `MessageBillValidationStoreZoneInsufficient` for every bill that
    //    dropped out — i.e. narrowing a storage filter silently orphans a
    //    bill's output, and the game warns about it. Reproduced as the
    //    `bills_invalidated` RESULT FIELD (DESIGN 2026-08-31: disclose the
    //    click's side effects rather than dropping them). NOT via GlobalBills,
    //    which can `Log.ErrorOnce("Found non-bill-giver tagged as
    //    PotentialBillGiver")` on a modded bench — the sweep here walks
    //    `ThingsInGroup(PotentialBillGiver)` with its own casts, as
    //    `ColonyVerbs.Bills` does.
    //  * `StorageGroup.CellsList` and `HaulSourcesList` return STATIC tmp lists
    //    cleared and refilled on every read (WorldSafe Class E). Not read here;
    //    the member walk uses `StorageGroup.members`, the real list, snapshotted.
    //  * `ThingFilter.DisplayRootCategory` is a lazy ParallelFor whose SETTER
    //    writes `allowedHitPointsConfigurable`/`allowedQualitiesConfigurable`
    //    (WorldSafe Class COST). Never read; `FilterSummary` is the route, as
    //    2.4 established, and the parent-filter gate below uses
    //    `ThingFilter.Allows`, a HashSet lookup.
    //  * `StorageSettingsClipboard.Copy`/`PasteInto` are two static calls
    //    around one `StorageSettings.CopyFrom`, each ending in a
    //    `Messages.Message` toast, and `Copy` CLOBBERS the player's clipboard.
    //    `storage-set {copy_from:…}` does the same `CopyFrom` directly: one
    //    round trip instead of two, no toast the agent will never read, and
    //    the human's clipboard left alone. Resolved on the issue.
    // =========================================================================
    internal static partial class BillActs
    {
        public const int StorageCap = 40;

        // --------------------------------------------------------------------
        // storage {id?, cap?}   READ-ONLY
        //
        // Every IStoreSettingsParent the player could click: stockpile zones
        // from the ZoneManager, and player buildings from ListerBuildings —
        // resolved the way `ITab_Storage.GetThingOrThingCompStoreSettingsParent`
        // does, so a turret's shell filter (a `CompChangeableProjectile`) and a
        // biosculpter's nutrition store are found on their comps rather than
        // missed.
        // --------------------------------------------------------------------
        [Verb("storage")]
        public static object Storage(VerbContext ctx)
        {
            var map = Map();
            var a = ctx.Args;
            int cap = a.Int("cap", StorageCap);
            if (cap < 1 || cap > 300) throw new VerbArgsException("cap must be 1..300");
            bool one = a.Has("id") || a.Has("target");

            var rows = new List<object>();
            int total = 0, hiddenTab = 0, fogged = 0;

            if (one)
            {
                object raw = a.Raw("id") ?? a.Raw("target");
                var parent = ResolveStoreParent(map, raw, out string why);
                if (parent == null) throw new VerbArgsException(why);
                total = 1;
                rows.Add(StorageRow(map, parent, true));
            }
            else
            {
                foreach (var parent in AllStoreParents(map, ref hiddenTab, ref fogged))
                {
                    total++;
                    if (rows.Count >= cap) continue;
                    rows.Add(StorageRow(map, parent, false));
                }
            }

            var groups = new List<object>();
            try
            {
                var all = map.storageGroups?.StorageGroupsForReading;
                if (all != null)
                    for (int i = 0; i < all.Count; i++)
                    {
                        var g = all[i];
                        if (g == null) continue;
                        groups.Add(new Dictionary<string, object>
                        {
                            ["id"] = g.loadID,
                            ["label"] = Safe(() => g.RenamableLabel),
                            ["members"] = g.MemberCount,
                            ["priority"] = Safe(() => g.GetStoreSettings()?.Priority.ToString()),
                        });
                    }
            }
            catch { }

            return new Dictionary<string, object>
            {
                ["verb"] = "storage",
                ["storages"] = rows,
                ["total"] = total,
                ["more"] = Math.Max(0, total - rows.Count),
                ["groups"] = groups,
                ["order"] = "zones-then-buildings, each id-asc",
                ["skipped"] = new Dictionary<string, object>
                {
                    ["tab_hidden"] = hiddenTab,
                    ["fogged"] = fogged,
                },
                ["scope"] = "map.zoneManager.AllZones (stockpiles) + map.listerBuildings.allBuildingsColonist, "
                    + "each resolved through ITab_Storage.GetThingOrThingCompStoreSettingsParent's own rule "
                    + "(the thing, else its first IStoreSettingsParent comp). A non-player-faction thing is "
                    + "not listed because ITab_Storage.IsVisible hides its tab.",
                ["note"] = "`settings:\"group\"` means GetStoreSettings() returned the STORAGE GROUP's "
                    + "object, shared with every other member — writing this storage writes all of them. "
                    + "`priority_settable:false` means the game draws no priority control for this thing "
                    + "(ITab_Storage.IsPrioritySettingVisible, false on ITab_Shells and "
                    + "ITab_BiosculpterNutritionStorage).",
            };
        }

        // --------------------------------------------------------------------
        // storage-set {targets|target, …}
        //
        // The plural form: one call re-filters N storages, because "make every
        // shelf in the freezer take meals" is one intention.
        // --------------------------------------------------------------------
        [Verb("storage-set")]
        public static object StorageSet(VerbContext ctx)
        {
            const string V = "storage-set";
            var map = Map();
            var a = ctx.Args;
            var targets = TargetList(map, a);
            // EVERY ARGUMENT, BEFORE THE FIRST WRITE. `ParseStoragePriority`,
            // `Pct01`, `ParseQualityRange` and StorageFilterOps' word checks
            // all throw, and they used to throw INSIDE the loop below — after
            // `copy_from` and `priority` had been written to that target, and
            // (with `targets` plural) after earlier targets were written in
            // full. `Act(...)` is never reached on that path, so there was NO
            // JOURNAL ROW for the writes that did land: an unprovenanced state
            // change reported to the caller as a clean `bad-args`. See
            // BillVerbs.ValidateBillArgs for the whole argument.
            ValidateStorageArgs(a);

            // The state the bill-validation diff is measured against, taken
            // BEFORE anything is written. ITab_Storage.FillTab does exactly
            // this around its filter widget.
            var before = StorableBills(map);

            var accepted = new List<object>();
            var rejected = new List<object>();
            int changedTotal = 0;

            foreach (var parent in targets)
            {
                var line = new Dictionary<string, object>
                {
                    ["target"] = TargetToken(parent),
                    ["label"] = LabelOf(parent),
                };
                string tabWhy = TabHidden(map, parent);
                if (tabWhy != null)
                {
                    line["gate"] = "tab-hidden";
                    line["reason"] = tabWhy;
                    rejected.Add(line);
                    continue;
                }

                var settings = SettingsOf(parent);
                if (settings == null)
                {
                    line["gate"] = "no-settings";
                    line["reason"] = "GetStoreSettings() returned null";
                    rejected.Add(line);
                    continue;
                }

                var changed = new List<object>();
                var refused = new List<object>();

                // --- copy_from -----------------------------------------------
                // StorageSettingsClipboard.PasteInto is `s.CopyFrom(clipboard)`
                // plus a toast; the toast is dropped and the global clipboard
                // is left alone (see the file header).
                if (a.Has("copy_from"))
                {
                    var src = ResolveStoreParent(map, a.Raw("copy_from"), out string why);
                    var srcSettings = src == null ? null : SettingsOf(src);
                    if (srcSettings == null)
                        refused.Add(Refusal("copy_from", "not-found", why ?? "that storage has no settings"));
                    else if (ReferenceEquals(srcSettings, settings))
                        refused.Add(Refusal("copy_from", "same-object",
                            "source and destination share one StorageSettings object (they are in the same "
                            + "storage group), so the copy would be a no-op"));
                    else
                    {
                        // CopyFrom goes through the Priority PROPERTY and
                        // filter.CopyAllowancesFrom, then TryNotifyChanged.
                        settings.CopyFrom(srcSettings);
                        changed.Add(new Dictionary<string, object>
                        {
                            ["field"] = "copy_from",
                            ["value"] = TargetToken(src),
                        });
                    }
                }

                // --- priority -------------------------------------------------
                if (a.Has("priority"))
                {
                    if (!PrioritySettable(parent, out string pWhy))
                        refused.Add(Refusal("priority", "no-priority-control", pWhy));
                    else
                    {
                        var pr = ParseStoragePriority(a.Str("priority"));
                        // THE PROPERTY, never priorityInt — the setter is where
                        // haul-manager invalidation lives.
                        settings.Priority = pr;
                        changed.Add(new Dictionary<string, object>
                        {
                            ["field"] = "priority",
                            ["value"] = pr.ToString(),
                        });
                    }
                }

                // --- the filter -----------------------------------------------
                if (a.Has("filter") || a.Has("allow") || a.Has("disallow") || a.Has("special"))
                {
                    var parentFilter = SafeFilter(() => parent.GetParentStoreSettings()?.filter);
                    var ops = StorageFilterOps.Apply(settings.filter, parentFilter, "storable", a,
                        out var filterRefusals, StorageFilterOps.IdeoDietFilters());
                    foreach (var r in filterRefusals) refused.Add(Refusal("filter", "outside-parent-filter", r));
                    if (ops.Count > 0)
                        changed.Add(new Dictionary<string, object> { ["field"] = "filter", ["value"] = ops });
                }

                // --- the two special-filter sliders ----------------------------
                // ThingFilterUI.DrawHitPointsFilterConfig / DrawQualityFilterConfig,
                // drawn above the tree. Both go through the ThingFilter
                // PROPERTIES, whose setters fire settingsChangedCallback.
                if (a.Has("hp_range") && settings.filter != null)
                {
                    var pair = Pct01(a, "hp_range");
                    settings.filter.AllowedHitPointsPercents = new FloatRange(pair[0], pair[1]);
                    changed.Add(new Dictionary<string, object>
                    {
                        ["field"] = "hp_range",
                        ["value"] = new List<object> { WorldSafe.Pct(pair[0]), WorldSafe.Pct(pair[1]) },
                    });
                }
                if (a.Has("quality_range") && settings.filter != null)
                {
                    var qr = ParseQualityRange(a, "quality_range");
                    settings.filter.AllowedQualityLevels = qr;
                    changed.Add(new Dictionary<string, object>
                    {
                        ["field"] = "quality_range",
                        ["value"] = qr.min + ".." + qr.max,
                    });
                }

                if (changed.Count > 0) changedTotal++;
                line["changed"] = changed;
                line["refused"] = refused;
                line["settings"] = SettingsSource(parent);
                line["after"] = StorageRow(map, parent, true);
                accepted.Add(line);
            }

            // The AFTER half of ITab_Storage.FillTab's diff. A bill whose
            // specific-stockpile output no longer accepts its product has been
            // orphaned by this call, and the game says so with a top-of-screen
            // message the agent never reads — so it is a result field.
            var invalidated = new List<object>();
            try
            {
                var after = StorableBills(map);
                foreach (var kv in before)
                    if (!after.ContainsKey(kv.Key))
                        invalidated.Add(kv.Value);
            }
            catch { }

            // REACHED, not CHANGED — git-bug 4087644: a call where every target
            // was refused is the wasted order the journal must show.
            long seq = accepted.Count + rejected.Count == 0
                ? 0
                : Act(V, "set", changedTotal + " of " + targets.Count + " storage(s)",
                    new Dictionary<string, object>
                    {
                        ["targets"] = targets.Count,
                        ["changed"] = changedTotal,
                        ["rejected"] = rejected.Count,
                        ["bills_invalidated"] = invalidated.Count,
                    });

            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["accepted"] = accepted,
                ["rejected"] = rejected,
                ["counts"] = new Dictionary<string, object>
                {
                    ["targeted"] = targets.Count,
                    ["changed"] = changedTotal,
                    ["rejected"] = rejected.Count,
                },
                ["bills_invalidated"] = invalidated,
                ["action"] = seq == 0 && changedTotal == 0 ? NoAction() : Stamp(seq),
                ["note"] = invalidated.Count > 0
                    ? "bills_invalidated lists bills whose SPECIFIC output storage no longer accepts their "
                      + "product — vanilla raises MessageBillValidationStoreZoneInsufficient for each. They "
                      + "still exist and will still be worked; the product will just have nowhere to go."
                    : "a write on a storage whose `settings` is \"group\" edits every member of that group.",
            };
        }

        // --------------------------------------------------------------------
        // storage-link {targets, label?}
        //
        // `RimWorld/StorageGroupUtility.cs StorageGroupMemberGizmos`'s Link
        // action, with `targets` in place of `Find.Selector.SelectedObjects`.
        // The gizmo is a MULTI-SELECT command — the plural form is the verb
        // here in the game's own design, not only in ours.
        // --------------------------------------------------------------------
        [Verb("storage-link")]
        public static object StorageLink(VerbContext ctx)
        {
            const string V = "storage-link";
            var map = Map();
            var a = ctx.Args;
            var targets = TargetList(map, a);

            var members = new List<IStorageGroupMember>();
            var rejected = new List<object>();
            string tag = null;
            bool tagSet = false;

            foreach (var parent in targets)
            {
                var line = new Dictionary<string, object>
                {
                    ["target"] = TargetToken(parent),
                    ["label"] = LabelOf(parent),
                };
                var member = parent as IStorageGroupMember;
                if (member == null)
                {
                    line["gate"] = "not-groupable";
                    line["reason"] = parent is Zone_Stockpile
                        ? "a stockpile ZONE is not an IStorageGroupMember — RimWorld/Zone_Stockpile.cs does "
                          + "not implement it, so the Link gizmo is never drawn for one. Storage groups are "
                          + "buildings only."
                        : "this storage is not an IStorageGroupMember, so StorageGroupUtility"
                          + ".StorageGroupMemberGizmos never yields a Link button for it";
                    rejected.Add(line); continue;
                }
                // The gizmo's own candidate filter.
                if (parent is Building_Storage bs && !bs.StorageTabVisible)
                {
                    line["gate"] = "storage-tab-hidden";
                    line["reason"] = "the gizmo skips any `Building_Storage { StorageTabVisible: false }`";
                    rejected.Add(line); continue;
                }
                string myTag = Safe(() => member.StorageGroupTag);
                if (!tagSet) { tag = myTag; tagSet = true; }
                else if (!string.Equals(tag, myTag, StringComparison.Ordinal))
                {
                    line["gate"] = "tag-mismatch";
                    line["reason"] = $"StorageGroupTag '{myTag ?? "null"}' does not match '{tag ?? "null"}' — "
                        + "the gizmo only collects members whose def.building.storageGroupTag matches the one "
                        + "it was drawn for, so a shelf and a grave never link";
                    rejected.Add(line); continue;
                }
                if (Safe(() => member.Map) != map)
                {
                    line["gate"] = "wrong-map";
                    line["reason"] = "the gizmo requires every member on the same map";
                    rejected.Add(line); continue;
                }
                members.Add(member);
            }

            if (members.Count < 2)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["gate"] = "select-two",
                    ["reason"] = Safe(() => (string)"LinkStorageDisabledSelectTwo".Translate())
                        ?? "linking needs at least two storages",
                    ["eligible"] = members.Count,
                    ["rejected"] = rejected,
                    ["action"] = NoAction(),
                };

            // The gizmo's "AlreadyLinked" disable: every candidate already in
            // ONE non-null group.
            var firstGroup = Safe(() => members[0].Group);
            bool allSame = firstGroup != null;
            for (int i = 0; allSame && i < members.Count; i++)
                if (Safe(() => members[i].Group) != firstGroup) allSame = false;
            if (allSame)
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["gate"] = "already-linked",
                    ["reason"] = Safe(() => (string)"AlreadyLinked".Translate())
                        ?? "these storages already share one group",
                    ["group"] = GroupLine(firstGroup),
                    ["rejected"] = rejected,
                    ["action"] = NoAction(),
                };

            // The action, verbatim: reuse the first member's group if it has
            // one, else a NEW group seeded from that member's settings.
            StorageGroup group;
            bool created;
            try
            {
                var existing = Safe(() => members[0].Group);
                created = existing == null;
                group = existing ?? map.storageGroups.NewGroup(a.Str("label"));
                if (created) group.InitFrom(members[0]);
                foreach (var m in members) m.SetStorageGroup(group);
            }
            catch (Exception e)
            {
                return new Dictionary<string, object>
                {
                    ["verb"] = V,
                    ["ok"] = false,
                    ["gate"] = "exception",
                    ["reason"] = e.GetType().Name + ": " + e.Message,
                    ["action"] = NoAction(),
                };
            }

            long seq = Act(V, "link", members.Count + " storages -> group " + group.loadID,
                new Dictionary<string, object>
                {
                    ["group"] = group.loadID,
                    ["members"] = members.Count,
                    ["created"] = created,
                });

            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["group"] = GroupLine(group),
                ["created"] = created,
                ["linked"] = members.Count,
                ["rejected"] = rejected,
                ["storages"] = MemberRows(map, group),
                ["action"] = Stamp(seq),
                ["note"] = "every linked storage now returns the GROUP's StorageSettings from "
                    + "GetStoreSettings(); their own `settings` objects are shadowed and no longer read. "
                    + "Vanilla shows a \"SettingsLinkedFor\" toast here, dropped as unreadable by an agent. "
                    + (created
                        ? "The new group was seeded from the first target's settings (StorageGroup.InitFrom)."
                        : "The existing group's settings win; each joining member's own settings are "
                          + "overwritten by SetStorageGroup's leave path only when it LATER unlinks."),
            };
        }

        // --------------------------------------------------------------------
        // storage-unlink {targets}
        //
        // The Unlink action. `StorageGroupUtility.SetStorageGroup(null)` is the
        // same body the gizmo runs inline — RemoveMember, Group = null, then
        // `member.StoreSettings.CopyFrom(the group's settings)` so the building
        // keeps what it had — plus the `Notify_SettingsChanged` the inline
        // version skips. The gizmo is not yielded at all for an ungrouped
        // member, which is the gate.
        // --------------------------------------------------------------------
        [Verb("storage-unlink")]
        public static object StorageUnlink(VerbContext ctx)
        {
            const string V = "storage-unlink";
            var map = Map();
            var targets = TargetList(map, ctx.Args);

            var unlinked = new List<object>();
            var rejected = new List<object>();
            var dissolved = new List<object>();
            var dissolvedIds = new HashSet<int>();

            foreach (var parent in targets)
            {
                var line = new Dictionary<string, object>
                {
                    ["target"] = TargetToken(parent),
                    ["label"] = LabelOf(parent),
                };
                var member = parent as IStorageGroupMember;
                if (member == null)
                {
                    line["gate"] = "not-groupable";
                    line["reason"] = "this storage is not an IStorageGroupMember";
                    rejected.Add(line); continue;
                }
                var group = Safe(() => member.Group);
                if (group == null)
                {
                    line["gate"] = "not-linked";
                    line["reason"] = "StorageGroupUtility.StorageGroupMemberGizmos yields no Unlink button "
                        + "for an ungrouped member (`if (member.Group == null) yield break;`)";
                    rejected.Add(line); continue;
                }
                int before = group.MemberCount;
                int id = group.loadID;
                try { member.SetStorageGroup(null); }
                catch (Exception e)
                {
                    line["gate"] = "exception";
                    line["reason"] = e.GetType().Name + ": " + e.Message;
                    rejected.Add(line); continue;
                }
                line["was_group"] = id;
                line["settings"] = SettingsSource(parent);
                unlinked.Add(line);
                // Verse/StorageGroupManager.cs Notify_MemberRemoved dissolves a
                // group the moment it is down to ONE member, unlinking that one
                // too. Reported, because "I unlinked one shelf and the other
                // one left the group as well" is otherwise a mystery.
                bool gone = false;
                try { gone = !map.storageGroups.HasStorageGroup(group); } catch { }
                if (gone && dissolvedIds.Add(id))
                    dissolved.Add(new Dictionary<string, object>
                    {
                        ["group"] = id,
                        ["had_members"] = before,
                        ["reason"] = "StorageGroupManager.Notify_MemberRemoved dissolves a group at "
                            + "MemberCount <= 1 and unlinks its last member too",
                    });
            }

            long seq = unlinked.Count == 0
                ? 0
                : Act(V, "unlink", unlinked.Count + " storages",
                    new Dictionary<string, object>
                    {
                        ["members"] = unlinked.Count,
                        ["dissolved"] = dissolved.Count,
                    });

            return new Dictionary<string, object>
            {
                ["verb"] = V,
                ["ok"] = true,
                ["unlinked"] = unlinked,
                ["rejected"] = rejected,
                ["groups_dissolved"] = dissolved,
                ["action"] = unlinked.Count == 0 ? NoAction() : Stamp(seq),
                ["note"] = "an unlinked building keeps the settings it had inside the group "
                    + "(SetStorageGroup copies them back into its own object before clearing Group).",
            };
        }

        // ===================== storage plumbing ==============================

        // ITab_Storage.GetThingOrThingCompStoreSettingsParent: the thing itself
        // if it is one, else its first IStoreSettingsParent comp. That comp
        // route is how a turret's shell filter and a biosculpter's nutrition
        // store are reachable at all.
        private static IStoreSettingsParent StoreParentOf(Thing t)
        {
            if (t == null) return null;
            if (t is IStoreSettingsParent direct) return direct;
            var withComps = t as ThingWithComps;
            if (withComps?.AllComps == null) return null;
            for (int i = 0; i < withComps.AllComps.Count; i++)
                if (withComps.AllComps[i] is IStoreSettingsParent c) return c;
            return null;
        }

        private static IEnumerable<IStoreSettingsParent> AllStoreParents(Map map, ref int hiddenTab, ref int fogged)
        {
            var found = new List<IStoreSettingsParent>();
            int hid = 0, fog = 0;
            try
            {
                var zones = new List<Zone>(map.zoneManager.AllZones);
                zones.Sort((x, y) => x.ID.CompareTo(y.ID));
                for (int i = 0; i < zones.Count; i++)
                {
                    if (!(zones[i] is Zone_Stockpile sp)) continue;
                    if (WorldSafe.ZoneHidden(sp, map, out _)) { fog++; continue; }
                    found.Add(sp);
                }
            }
            catch { }
            try
            {
                // allBuildingsColonist is the real backing list; snapshot it,
                // because StoreParentOf reaches comp code (WorldSafe Class E).
                var buildings = new List<Building>(map.listerBuildings.allBuildingsColonist);
                buildings.Sort((x, y) => x.thingIDNumber.CompareTo(y.thingIDNumber));
                for (int i = 0; i < buildings.Count; i++)
                {
                    var b = buildings[i];
                    if (b == null) continue;
                    if (WorldSafe.Hidden(b, map)) { fog++; continue; }
                    var parent = StoreParentOf(b);
                    if (parent == null) continue;
                    // ITab_Storage.IsVisible's last clause.
                    bool visible = false;
                    try { visible = parent.StorageTabVisible; } catch { }
                    if (!visible) { hid++; continue; }
                    found.Add(parent);
                }
            }
            catch { }
            hiddenTab = hid;
            fogged = fog;
            return found;
        }

        private static Dictionary<string, object> StorageRow(Map map, IStoreSettingsParent parent, bool deep)
        {
            var settings = SettingsOf(parent);
            var thing = parent as Thing ?? (parent as ThingComp)?.parent;
            var zone = parent as Zone_Stockpile;
            var d = new Dictionary<string, object>
            {
                ["target"] = TargetToken(parent),
                ["kind"] = zone != null ? "zone" : (parent is ThingComp ? "comp" : "building"),
                ["label"] = LabelOf(parent),
                ["def"] = thing?.def?.defName,
                ["at"] = thing != null && thing.Spawned ? Positions.Out(thing.Position) : null,
                ["priority"] = Safe(() => settings?.Priority.ToString()),
                ["priority_settable"] = PrioritySettable(parent, out string pWhy),
                ["settings"] = SettingsSource(parent),
                ["tab_visible"] = TabHidden(map, parent) == null,
            };
            if (pWhy != null) d["priority_gate"] = pWhy;
            var member = parent as IStorageGroupMember;
            if (member != null)
            {
                d["group"] = GroupLine(Safe(() => member.Group));
                d["group_tag"] = Safe(() => member.StorageGroupTag);
            }
            if (zone != null)
            {
                d["cells"] = zone.CellCount;
                d["space_remaining"] = SafeObj(() => (object)zone.SpaceRemaining);
            }
            if (deep)
                d["filter"] = SafeObj(() => FilterSummary.Build(settings?.filter,
                    SafeFilter(() => parent.GetParentStoreSettings()?.filter), "storable"));
            else
                d["allowed_defs"] = SafeObj(() => (object)(settings?.filter?.AllowedDefCount ?? 0));
            return d;
        }

        private static Dictionary<string, object> GroupLine(StorageGroup g)
            => g == null ? null : new Dictionary<string, object>
            {
                ["id"] = g.loadID,
                ["label"] = Safe(() => g.RenamableLabel),
                ["members"] = g.MemberCount,
            };

        private static List<object> MemberRows(Map map, StorageGroup group)
        {
            var list = new List<object>();
            if (group == null) return list;
            var members = new List<IStorageGroupMember>(group.members);
            for (int i = 0; i < members.Count; i++)
            {
                var p = members[i] as IStoreSettingsParent;
                if (p == null) continue;
                list.Add(StorageRow(map, p, false));
            }
            return list;
        }

        // GetStoreSettings(), never `Building_Storage.settings` — the shadowed
        // object trap. This one line is the spec's third acceptance bullet.
        private static StorageSettings SettingsOf(IStoreSettingsParent parent)
        {
            try { return parent?.GetStoreSettings(); } catch { return null; }
        }

        private static string SettingsSource(IStoreSettingsParent parent)
        {
            var member = parent as IStorageGroupMember;
            if (member == null) return "own";
            try { return member.Group != null ? "group" : "own"; } catch { return "own"; }
        }

        // ITab_Storage.IsVisible, reproduced: a non-player-faction Thing hides
        // the tab outright, and so does `!StorageTabVisible` — which
        // Building_CorpseCasket makes `!HasCorpse` and Building_Grave narrows
        // again to "and nobody is assigned to it".
        private static string TabHidden(Map map, IStoreSettingsParent parent)
        {
            var thing = parent as Thing ?? (parent as ThingComp)?.parent;
            if (thing != null)
            {
                try
                {
                    if (thing.Faction != null && thing.Faction != Faction.OfPlayer)
                        return "ITab_Storage.IsVisible returns false for a Thing whose Faction is non-null "
                            + "and not the player's";
                }
                catch { }
            }
            try
            {
                if (!parent.StorageTabVisible)
                {
                    if (parent is Building_Grave grave)
                        return grave.AssignedPawn != null
                            ? "Building_Grave.StorageTabVisible is false while a pawn is ASSIGNED to the grave"
                            : "Building_CorpseCasket.StorageTabVisible is `!HasCorpse` — this one is occupied";
                    if (parent is Building_CorpseCasket)
                        return "Building_CorpseCasket.StorageTabVisible is `!HasCorpse` — this one is occupied";
                    return "IStoreSettingsParent.StorageTabVisible is false, so the game draws no storage tab";
                }
            }
            catch { }
            return null;
        }

        // ITab_Storage.IsPrioritySettingVisible, read off the DEF's own
        // resolved inspector tab. ITab_Shells (turret shells) and
        // ITab_BiosculpterNutritionStorage override it to false: the filter is
        // configurable there and the priority is not, so setting one would be a
        // control the player does not have. A stockpile ZONE has no def and
        // uses the plain ITab_Storage (Zone_Stockpile.GetInspectTabs), which is
        // always true.
        private static readonly Dictionary<ThingDef, string> priorityGateMemo = new Dictionary<ThingDef, string>();

        private static bool PrioritySettable(IStoreSettingsParent parent, out string why)
        {
            why = null;
            var thing = parent as Thing ?? (parent as ThingComp)?.parent;
            var def = thing?.def;
            if (def == null) return true;
            if (priorityGateMemo.TryGetValue(def, out var memo)) { why = memo; return memo == null; }
            string reason = null;
            try
            {
                var tabs = def.inspectorTabsResolved;
                if (tabs != null)
                    for (int i = 0; i < tabs.Count; i++)
                    {
                        var tab = tabs[i] as ITab_Storage;
                        if (tab == null) continue;
                        var prop = AccessTools.DeclaredProperty(tab.GetType(), "IsPrioritySettingVisible")
                                   ?? AccessTools.Property(tab.GetType(), "IsPrioritySettingVisible");
                        if (prop?.GetGetMethod(true) == null) break;
                        object v = prop.GetGetMethod(true).Invoke(tab, null);
                        if (v is bool ok && !ok)
                            reason = tab.GetType().Name
                                + ".IsPrioritySettingVisible is false — the game draws no priority control "
                                + "on this thing's storage tab, only a filter";
                        break;
                    }
            }
            catch { reason = null; }
            priorityGateMemo[def] = reason;
            why = reason;
            return reason == null;
        }

        // ITab_Storage.FillTab's dropdown: every StoragePriority EXCEPT
        // Unstored. Identical to 3.2's ZoneVerbs.ParsePriority, deliberately —
        // the same gate on both verbs rather than one lenient and one strict.
        private static StoragePriority ParseStoragePriority(string s)
        {
            if (s == null) throw new VerbArgsException("priority must be a string");
            foreach (StoragePriority v in Enum.GetValues(typeof(StoragePriority)))
            {
                if (!string.Equals(v.ToString(), s, StringComparison.OrdinalIgnoreCase)) continue;
                if (v == StoragePriority.Unstored)
                    throw new VerbArgsException(
                        "priority 'Unstored' is not selectable (RimWorld/ITab_Storage.FillTab builds its "
                        + "dropdown with `if (value != StoragePriority.Unstored)`); "
                        + "use Low|Normal|Preferred|Important|Critical");
                return v;
            }
            throw new VerbArgsException("priority must be Low|Normal|Preferred|Important|Critical");
        }

        // ======================= target resolution ===========================

        // "zone:<id>" | "thing:<id>" | a bare number (thing id). Zone.ID and
        // Thing.thingIDNumber are different counters, so a bare number can only
        // mean one of them and it means the one every other verb means.
        private static IStoreSettingsParent ResolveStoreParent(Map map, object raw, out string why)
        {
            why = null;
            int id;
            bool isZone = false;
            if (raw is double d) id = (int)d;
            else if (raw is string s)
            {
                if (s.StartsWith("zone:", StringComparison.Ordinal)) { isZone = true; s = s.Substring(5); }
                else if (s.StartsWith("thing:", StringComparison.Ordinal)) s = s.Substring(6);
                if (!int.TryParse(s, out id))
                {
                    why = $"'{raw}' is not a storage target — pass \"zone:<id>\", \"thing:<id>\" or a thing id";
                    return null;
                }
            }
            else
            {
                why = "a storage target must be \"zone:<id>\", \"thing:<id>\" or a thing id (number)";
                return null;
            }

            if (isZone)
            {
                try
                {
                    var zones = map.zoneManager.AllZones;
                    for (int i = 0; i < zones.Count; i++)
                        if (zones[i] is Zone_Stockpile sp && sp.ID == id)
                        {
                            if (WorldSafe.ZoneHidden(sp, map, out _)) break;
                            return sp;
                        }
                }
                catch { }
                why = $"no visible stockpile zone with id {id} on the current map "
                    + "(zones in unexplored ground are not reported). `zones {kind:\"stockpile\"}` lists them.";
                return null;
            }

            try
            {
                var things = map.listerThings.AllThings;
                for (int i = 0; i < things.Count; i++)
                {
                    var t = things[i];
                    if (t == null || t.thingIDNumber != id) continue;
                    if (WorldSafe.Hidden(t, map)) break;
                    var parent = StoreParentOf(t);
                    if (parent == null)
                    {
                        why = $"thing {id} ({t.def?.defName}) is not an IStoreSettingsParent and carries no "
                            + "IStoreSettingsParent comp — it has no storage settings";
                        return null;
                    }
                    return parent;
                }
            }
            catch { }
            why = $"no visible thing with id {id} on the current map "
                + "(things in unexplored ground are not reported)";
            return null;
        }

        private static List<IStoreSettingsParent> TargetList(Map map, VerbArgs a)
        {
            var raws = new List<object>();
            object many = a.Raw("targets");
            if (many is List<object> list) raws.AddRange(list);
            else if (many != null) raws.Add(many);
            else
            {
                object one = a.Raw("target") ?? a.Raw("id");
                if (one == null)
                    throw new VerbArgsException(
                        "missing required arg 'targets' (an array of \"zone:<id>\" / \"thing:<id>\" / thing ids; "
                        + "'target' takes one)");
                raws.Add(one);
            }
            var result = new List<IStoreSettingsParent>();
            foreach (var raw in raws)
            {
                var p = ResolveStoreParent(map, raw, out string why);
                if (p == null) throw new VerbArgsException(why);
                if (!result.Contains(p)) result.Add(p);
            }
            return result;
        }

        private static string TargetToken(IStoreSettingsParent parent)
        {
            if (parent is Zone_Stockpile sp) return "zone:" + sp.ID;
            if (parent is StorageGroup g) return "group:" + g.loadID;
            var thing = parent as Thing ?? (parent as ThingComp)?.parent;
            return thing != null ? "thing:" + thing.thingIDNumber : "?";
        }

        private static string LabelOf(IStoreSettingsParent parent)
        {
            if (parent is Zone_Stockpile sp) return Safe(() => sp.label);
            if (parent is StorageGroup g) return Safe(() => g.RenamableLabel);
            var thing = parent as Thing ?? (parent as ThingComp)?.parent;
            return thing == null ? null : Safe(() => thing.LabelCap);
        }

        // The ISlotGroup a bill's SpecificStockpile store mode would target.
        // Dialog_BillConfig.FillOutputDropdownOptions offers the STORAGE GROUP
        // when a slot group has one, not the individual building.
        private static ISlotGroup SlotGroupOf(IStoreSettingsParent parent)
        {
            try
            {
                if (parent is StorageGroup g) return g;
                var member = parent as IStorageGroupMember;
                if (member?.Group != null) return member.Group;
                if (parent is ISlotGroupParent sgp) return sgp.GetSlotGroup();
            }
            catch { }
            return null;
        }

        private static ThingFilter SafeFilter(Func<ThingFilter> f)
        {
            try { return f(); } catch { return null; }
        }

        private static Dictionary<string, object> Refusal(string field, string gate, string reason)
            => new Dictionary<string, object>
            {
                ["field"] = field,
                ["gate"] = gate,
                ["reason"] = reason,
            };

        // ITab_Storage.FillTab's bill-validation predicate, map-scoped and
        // WITHOUT BillUtility.GlobalBills (which Log.ErrorOnce's on a modded
        // bench). Key is the bill; value is a reportable line.
        private static Dictionary<Bill, object> StorableBills(Map map)
        {
            var result = new Dictionary<Bill, object>();
            try
            {
                var givers = new List<Thing>(map.listerThings.ThingsInGroup(ThingRequestGroup.PotentialBillGiver));
                for (int i = 0; i < givers.Count; i++)
                {
                    var stack = (givers[i] as IBillGiver)?.BillStack;
                    if (stack == null) continue;
                    var bills = new List<Bill>(stack.Bills);
                    for (int j = 0; j < bills.Count; j++)
                    {
                        var prod = bills[j] as Bill_Production;
                        if (prod?.recipe == null) continue;
                        var group = Safe(() => prod.GetSlotGroup());
                        if (group == null) continue;
                        bool ok = false;
                        try { ok = prod.recipe.WorkerCounter.CanPossiblyStore(prod, group); } catch { }
                        if (!ok) continue;
                        result[prod] = new Dictionary<string, object>
                        {
                            ["bench"] = givers[i].thingIDNumber,
                            ["uid"] = Safe(() => prod.GetUniqueLoadID()),
                            ["recipe"] = prod.recipe.defName,
                            ["store_target"] = Safe(() => SlotGroup.GetGroupLabel(group)),
                            ["game_message"] = "MessageBillValidationStoreZoneInsufficient",
                        };
                    }
                }
            }
            catch { }
            return result;
        }

        // ===================== the shared substrate ==========================
        // Private static on this partial class on purpose: 3.5's worker is
        // writing the same kind of helper in a parallel worktree and a shared
        // public type would collide at merge. The orchestrator owns any
        // factoring at merge time — the same call ZoneVerbs and PawnActs made.

        private static Map Map() => PawnSafe.CurrentMap();

        // The `action` journal row: {verb, step, target} plus additive extras,
        // mirroring the `dev` row's shape but carrying neither `cheat` nor
        // `fog_exempt`, because a player verb is not a cheat. Journal.Emit
        // returns 0 when the writer is closed and Stamp SAYS SO rather than
        // looking like a normal success.
        private static long Act(string verb, string step, string target,
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
                    if (!payload.ContainsKey(kv.Key) && kv.Value != null) payload[kv.Key] = kv.Value;
            int tick = 0;
            try { tick = Find.TickManager.TicksGame; } catch { }
            return Journal.Emit("action", payload, tick);
        }

        private static Dictionary<string, object> Stamp(long seq)
        {
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

        // A modded getter that throws degrades one FIELD, never the verb —
        // PawnSerializer's rule, same silence for the same reason.
        private static T Safe<T>(Func<T> f) where T : class
        {
            try { return f(); } catch { return null; }
        }

        private static object SafeObj(Func<object> f)
        {
            try { return f(); } catch { return null; }
        }
    }

    // ======================================================= spec 3.6 =========
    // THE FILTER EDITOR — shared by a bill's ingredientFilter and a storage's
    // settings.filter, because `Verse/ThingFilterUI.cs
    // DoThingFilterConfigWindow` is literally the same widget in both places:
    // ITab_Storage.FillTab passes (settings.filter, parentStoreSettings.filter)
    // and Dialog_BillConfig.DoIngredientConfigPane passes
    // (bill.ingredientFilter, recipe.fixedIngredientFilter).
    //
    //   filter:"all"|"none"     the Allow-All / Clear-All buttons
    //   allow:[…] / disallow:[…] the per-category and per-def checkboxes
    //   special:{def:bool}       the `*`-prefixed SpecialThingFilterDef rows
    //
    // A REPLACEMENT then a patch, in that order, so `{filter:"none",
    // allow:["MealSimple"]}` is one call and is idempotent — which is what
    // makes a fixture reproducible (3.2's ApplyPreset makes the same promise).
    internal static class StorageFilterOps
    {
        // `hidden` is the caller's force-hidden special-filter set — the
        // `forceHiddenFilters` argument DoThingFilterConfigWindow already takes.
        // ITab_Storage passes the four Ideology diet filters; Dialog_BillConfig
        // passes those plus `recipe.forceHiddenSpecialFilters`. A row the game
        // does not draw is a control the player does not have.
        private const string FilterWordMsg =
            "filter must be \"all\" or \"none\" (the Allow-All / Clear-All buttons). "
            + "Name individual defs or categories with `allow` / `disallow`. "
            + "The five word PRESETS are spec 3.2's `zone edit {filter:…}`.";

        private const string SpecialShapeMsg =
            "special must be an object of {SpecialThingFilterDef: true|false}, "
            + "e.g. {\"AllowRotten\":false}";

        // The PURE-PARSE half of Apply, callable before the caller has written
        // anything. `filter` and `special` are the two args here that throw
        // rather than refuse, and Apply is reached from inside `storage-set`'s
        // per-target loop and from `bill-add`'s post-AddBill config — i.e. from
        // two places where a throw lands on top of a completed mutation and is
        // reported as a clean `bad-args`. Both callers now run this first. It
        // touches nothing: word comparison and type tests only. A def NAME that
        // does not resolve stays a `refused` line in Apply, because that is a
        // fact about the game's defs and not a malformed request.
        public static void Validate(VerbArgs a)
        {
            if (a.Has("filter"))
            {
                string word = a.Str("filter");
                if (!string.Equals(word, "none", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(word, "all", StringComparison.OrdinalIgnoreCase))
                    throw new VerbArgsException(FilterWordMsg);
            }
            if (a.Has("allow")) a.StrList("allow");
            if (a.Has("disallow")) a.StrList("disallow");
            if (a.Has("special") && !(a.Raw("special") is Dictionary<string, object>))
                throw new VerbArgsException(SpecialShapeMsg);
        }

        public static List<object> Apply(ThingFilter filter, ThingFilter parentFilter, string universeName,
            VerbArgs a, out List<string> refused, List<SpecialThingFilterDef> hidden = null)
        {
            var ops = new List<object>();
            refused = new List<string>();
            if (filter == null) return ops;

            // ---- the two buttons --------------------------------------------
            if (a.Has("filter"))
            {
                string word = a.Str("filter");
                if (string.Equals(word, "none", StringComparison.OrdinalIgnoreCase))
                {
                    // ThingFilterUI's Clear All. The forceHidden arguments are
                    // null here for the same reason they are null at the
                    // storage tab's own call site.
                    filter.SetDisallowAll();
                    ops.Add(new Dictionary<string, object> { ["op"] = "clear-all" });
                }
                else if (string.Equals(word, "all", StringComparison.OrdinalIgnoreCase))
                {
                    // Allow All takes the PARENT filter as its argument — the
                    // universe of what this container could ever hold. Passing
                    // null would allow every storable ThingDef in the game,
                    // which is not what the button does.
                    filter.SetAllowAll(parentFilter);
                    ops.Add(new Dictionary<string, object> { ["op"] = "allow-all", ["universe"] = universeName });
                }
                else
                    throw new VerbArgsException(FilterWordMsg);
            }

            // ---- per-def and per-category ------------------------------------
            Toggle(filter, parentFilter, a, "allow", true, ops, refused);
            Toggle(filter, parentFilter, a, "disallow", false, ops, refused);

            // ---- the special filters ------------------------------------------
            if (a.Has("special"))
            {
                if (!(a.Raw("special") is Dictionary<string, object> map))
                    throw new VerbArgsException(SpecialShapeMsg);
                foreach (var kv in map)
                {
                    var sf = DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail(kv.Key);
                    if (sf == null) { refused.Add($"no SpecialThingFilterDef named '{kv.Key}'"); continue; }
                    if (!sf.configurable)
                    {
                        refused.Add($"'{sf.defName}' has configurable:false — "
                            + "Listing_TreeThingFilter never draws a checkbox for it");
                        continue;
                    }
                    if (hidden != null && hidden.Contains(sf))
                    {
                        refused.Add($"'{sf.defName}' is force-hidden on this filter's own widget "
                            + "(DoThingFilterConfigWindow's forceHiddenFilters), so the game draws no row");
                        continue;
                    }
                    if (!(kv.Value is bool want))
                    { refused.Add($"special['{kv.Key}'] must be a bool"); continue; }
                    filter.SetAllow(sf, want);
                    ops.Add(new Dictionary<string, object>
                    {
                        ["op"] = want ? "allow-special" : "disallow-special",
                        ["def"] = sf.defName,
                    });
                }
            }
            return ops;
        }

        // A name is tried as a ThingDef, then a ThingCategoryDef — the two
        // kinds of row Listing_TreeThingFilter draws (a `*` special filter is
        // the third and has its own arg, because its value is a tri-state the
        // caller must state rather than infer).
        private static void Toggle(ThingFilter filter, ThingFilter parentFilter, VerbArgs a, string key,
            bool allow, List<object> ops, List<string> refused)
        {
            if (!a.Has(key)) return;
            foreach (var name in a.StrList(key))
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                if (def != null)
                {
                    // Listing_TreeThingFilter only ever LISTS defs under
                    // `parentFilter.DisplayRootCategory`, so a def the parent
                    // disallows has no checkbox and cannot be turned on. (The
                    // allowance would also be dead: StorageSettings
                    // .AllowedToAccept ANDs with the parent.) Disallowing one
                    // is always harmless, so only the `allow` direction is gated.
                    if (allow && parentFilter != null && !Allows(parentFilter, def))
                    {
                        refused.Add($"'{def.defName}' is outside this container's fixed filter, so "
                            + "Listing_TreeThingFilter draws no row for it and the allowance would be dead "
                            + "(StorageSettings.AllowedToAccept ANDs with the parent filter)");
                        continue;
                    }
                    filter.SetAllow(def, allow);
                    ops.Add(new Dictionary<string, object>
                    {
                        ["op"] = allow ? "allow" : "disallow",
                        ["kind"] = "def",
                        ["def"] = def.defName,
                    });
                    continue;
                }

                var cat = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(name);
                if (cat == null)
                {
                    refused.Add($"no ThingDef or ThingCategoryDef named '{name}'");
                    continue;
                }
                // The category checkbox: `filter.SetAllow(node.catDef, on,
                // forceHiddenDefs, hiddenSpecialFilters)`. It walks
                // DescendantThingDefs regardless of the parent filter, and that
                // is harmless (the allowance is dead for anything the parent
                // rejects), so this matches vanilla exactly.
                int before = filter.AllowedDefCount;
                filter.SetAllow(cat, allow);
                ops.Add(new Dictionary<string, object>
                {
                    ["op"] = allow ? "allow" : "disallow",
                    ["kind"] = "category",
                    ["def"] = cat.defName,
                    ["defs_delta"] = filter.AllowedDefCount - before,
                });
            }
        }

        private static bool Allows(ThingFilter f, ThingDef def)
        {
            try { return f.Allows(def); } catch { return true; }
        }

        // The four Ideology diet filters BOTH call sites force-hide:
        // `ITab_Storage.HiddenSpecialThingFilters()` and
        // `Dialog_BillConfig.HiddenSpecialThingFilters`, each gated on
        // ModsConfig.IdeologyActive. Cached the same way vanilla caches its own.
        private static List<SpecialThingFilterDef> ideoDietFilters;

        public static List<SpecialThingFilterDef> IdeoDietFilters()
        {
            if (ideoDietFilters != null) return ideoDietFilters;
            var list = new List<SpecialThingFilterDef>();
            try
            {
                if (ModsConfig.IdeologyActive)
                {
                    list.Add(SpecialThingFilterDefOf.AllowVegetarian);
                    list.Add(SpecialThingFilterDefOf.AllowCarnivore);
                    list.Add(SpecialThingFilterDefOf.AllowCannibal);
                    list.Add(SpecialThingFilterDefOf.AllowInsectMeat);
                }
            }
            catch { }
            ideoDietFilters = list;
            return list;
        }
    }
}
