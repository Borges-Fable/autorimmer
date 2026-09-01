using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ============================================================ spec 3.3 ===
    // `place-layout` / `cancel-layout` — A ROOM IN ONE TRANSACTION.
    //
    // `build` places one thing. This places a whole layout, and the difference
    // is not "a loop over build": it is the ATOMICITY INVARIANT. git-bug
    // 1adc737's Scope — "preflight EVERY cell first; on any failure: place
    // NOTHING" — cannot hold across N separate calls, because N calls are N
    // transactions and a half-built room is reachable between any two of them.
    // One call keeps the whole layout in front of the gate at once. That is the
    // entire reason this verb exists rather than a client-side loop, and it is
    // worth saying because the loop is the tempting shortcut.
    //
    // ------------------------ WHAT THIS VERB IS NOT --------------------------
    // IT DOES NOT READ THE IR. `rwa` owns the baseviz/KCSG dialect and hands
    // this verb a RESOLVED layout — a list of `{def, at, rot?, stuff?}` — in one
    // call (git-bug 1adc737 #13, DESIGN decisions log 2026-09-01). The mod reads
    // no layout file, knows no token grid, and has no opinion on rows or
    // columns. `File.ReadAllText` appears twice in this whole tree, for
    // `config.json` and the journal, and it stays that way: `baseviz/ir.py`
    // already IS the dialect, a second reader in C# is a guaranteed drift point,
    // and a file path the GAME must resolve is machine-dependent (on BORGES the
    // client and the bench are different trees) while a resolved payload cannot
    // mean two things.
    //
    // IT IS NOT A SECOND PLACEMENT GATE. `SiteGate.Check` is the one routine —
    // `GenConstruct.CanPlaceBlueprintAt(godMode:false)` plus
    // `Designator_Build.Visible`'s ten clauses — and `site-survey`,
    // `find-rect {def}`, `dev:spawn-thing {buildable:true}`, `site-audit`,
    // `build` and this verb all ASK it. And there is no godMode bypass here
    // either, on any setting, ever (git-bug 1adc737 #12).
    //
    // ------------------------- `at` IS A CORNER ------------------------------
    // Each element's `at` is the footprint's NORTH-WEST cell —
    // `templates/INDEX.md` pin 1, "the token sits in the footprint's north-west
    // cell; remaining cells are `.`" — and it is published back as
    // `anchor: "north-west"` on every call so a consumer cannot misread it.
    //
    // THIS IS DELIBERATELY NOT THE SAME CORNER `build --at` TAKES, and the
    // divergence is named rather than hidden. `build --at` and `find-rect`'s
    // `at` are the SOUTH-WEST corner, which is `[x,z,w,h]`'s own `x,z` and the
    // only sane anchor for a rect. A layout element's anchor is fixed by the IR
    // instead, and it has to be, because converting north-west to south-west
    // needs the def's ROTATED size — `Footprint.RotatedSize`, i.e. the game's
    // `AdjustForRotation` axis swap — which is precisely the knowledge the
    // client half is not allowed to have. So the conversion happens HERE, in one
    // place, using `Siting.cs`'s own map:
    //
    //     rotated = Footprint.RotatedSize(def.Size, rot)
    //     south-west corner = (at.x, at.z - (rotated.z - 1))
    //     centre = Footprint.TryCentreFor(def.Size, corner, rot, …)
    //
    // and every placement publishes `at` (as given), `pos` (the game's centre)
    // and `footprint` ([x,z,w,h], whose x,z is the south-west corner), so the
    // three are never confused. `TryCentreFor` verifies its own round trip
    // against `GenAdj.OccupiedRect` and a failure is a refusal, not a slide.
    //
    // ------------------------- BUILD ORDER, RESOLVED -------------------------
    // 1adc737's open question. Answered by reading the game's own gates rather
    // than by picking a pleasing order:
    //
    //   * TERRAIN GOES FIRST, and it is the only ordering rule.
    //     `RimWorld/GenConstruct.CanPlaceBlueprintAt`'s occupancy loop has a
    //     terrain-specific clause — `entDef is TerrainDef && thing3.def.category
    //     == ThingCategory.Building && thing3.def.terrainAffordanceNeeded !=
    //     null && !terrainDef3.affordances.Contains(…)` with a `FoundationAt`
    //     escape — so a floor asked for UNDER a building the floor cannot
    //     support is refused, while the same building over the same floor is
    //     governed by `CanPlaceBlueprintOver`'s `CoexistsWithFloors` branch and
    //     is not. Floor first, therefore, is strictly the safer half of an
    //     asymmetric rule.
    //
    //   * EVERYTHING ELSE KEEPS THE CALLER'S ORDER, because for everything else
    //     the rules are SYMMETRIC and no order rescues a bad layout.
    //     `CanPlaceBlueprintOver` lets a non-edifice go under an edifice
    //     (`canBuildNonEdificesUnder`, default true) AND an edifice go over a
    //     non-edifice (`IsEdificeOverNonEdifice`), so conduit-then-wall and
    //     wall-then-conduit both pass. Two edifices in one cell fail both ways.
    //     And the two interaction rules refuse in BOTH directions:
    //     `InteractionCellStandable` refuses a bench whose spot already holds an
    //     unstandable blueprint ("InteractionSpotWillBeBlocked"), while
    //     `NotBlockingAnyInteractionCells` refuses a wall that would cover a
    //     bench's spot ("WouldBlockInteractionSpot"). A layout that violates
    //     them is a broken layout, not a mis-ordered one.
    //
    //   * WE DO NOT STAGE, WE TRUST THE WORK GIVERS. The other half of the same
    //     open question — "whether to stage or trust work givers". There is no
    //     dependency ordering in construction work at all:
    //     `WorkGiver_ConstructDeliverResources` and
    //     `WorkGiver_ConstructFinishFrame` scan the player's blueprints and
    //     frames with no notion of walls-before-furniture, so "staging" could
    //     only mean WITHHOLDING blueprints from the colony until some condition
    //     we invented was met. That is a scheduler the game does not have,
    //     hidden inside a placement verb, and it would make the colony's own
    //     work surface a function of our bookkeeping. All blueprints go down at
    //     once; the pawns choose.
    //
    // ------------------ WHAT THE PREFLIGHT CAN AND CANNOT SEE ----------------
    // The preflight asks the game's gate about a map that does not yet contain
    // the layout. Two consequences, both handled explicitly:
    //
    //   1. SELF-OVERLAP is answered without the map, by the game's own
    //      `GenConstruct.CanPlaceBlueprintOver` over every intersecting pair in
    //      placement order. That is a def-level predicate, so it is exact.
    //
    //   2. INTERACTION-CELL INTERFERENCE BETWEEN OUR OWN ELEMENTS is REPORTED,
    //      NOT REFUSED (`self_conflicts`). Refusing would be a STRICTER gate
    //      than the widget's, which the gate-lives-in-the-widget rule forbids
    //      just as firmly as a looser one: vanilla's `NotBlockingAnyInteraction
    //      Cells` only walks `GenAdj.CellsAdjacentCardinal`, so a diagonal
    //      arrangement the game accepts exists, and we do not get to invent a
    //      refusal for it. The gate that DOES decide is asked again immediately
    //      before each element is placed, against the map as it actually is by
    //      then — see `late_refusals` — and a late refusal with `partial:false`
    //      ROLLS THE WHOLE CALL BACK, which is how the atomicity invariant
    //      survives contact with an order-dependent rule.
    //
    // ------------------------------ THE MODES --------------------------------
    // `blueprint` reproduces `RimWorld/Designator_Build.DesignateSingleCell`,
    // exactly as `build` does and through the same helpers.
    //
    // `instant` IS `dev:spawn-thing {buildable:true}` (git-bug 1adc737 #7): the
    // SAME `SiteGate` gate, then `GenSpawn.Spawn(…, WipeMode.VanishOrMoveAside)`
    // under a `WipeWatch`. It is NOT the default god-hand path — `GenSpawn
    // .CanSpawnAt` runs no PlaceWorker and, with `canWipeEdifices:true`, would
    // erase walls in the footprint with nothing in the journal to show for it.
    // `VanishOrMoveAside` is chosen over `Vanish` on the same reasoning that
    // makes instant ≡ blueprint checkable: `Verse/GenSpawn.Spawn`'s switch runs
    // `CheckMoveItemsAside` first for that mode, so the wood stack a colonist
    // would have HAULED out of the footprint is moved rather than destroyed —
    // and `CanPlaceBlueprintOver` lets that stack through the preflight
    // (`oldDef.EverHaulable → true`), so without the move the two modes would
    // genuinely diverge. `WipeWatch` publishes anything that was destroyed
    // anyway, so "nothing was wiped" is a measurement and not an assumption.
    //
    // "Instant ≡ what blueprint mode would have produced" is PROVABLE rather
    // than asserted, and the instrument is `site-audit` (git-bug 3a5ff6c): it
    // re-runs the validator over every player building in a rect, so an
    // instant-mode layout that audits clean over its own `rect` is, by
    // construction, a state blueprint mode could have reached. That replaces
    // the issue's original "things-dump diff … modulo construction byproducts",
    // which its own verification comment showed was not a decidable predicate.
    //
    // ------------------------------ THE ROOF ---------------------------------
    // NOT CONSUMED, and that is a resolution rather than an omission. Roofing is
    // already served three ways (git-bug 1adc737 #8): `area {kind:"build-roof"}`
    // is shipped and gated on `Designator_AreaBuildRoof.CanDesignateCell`;
    // `Verse/AutoBuildRoofAreaSetter.TryGenerateAreaNow` roofs any enclosed,
    // non-map-edge, non-fogged player room of ≤26 regions and ≤320 cells by
    // itself (a 7x7 module is 49 cells, the 5x7 rehearsal 35 — both qualify);
    // and `map-dump`/`map-view`/`find-rect` all read roofs. A roof is a
    // DESIGNATION, not a placement, and folding a second designator into this
    // verb's transaction would make one call mean two things. `rwa place-layout`
    // reports the IR's roof grid rather than dropping it silently, and `--roof`
    // sends the `area` call itself — as a SECOND call, outside this
    // transaction, which is what it is.
    //
    // TEST DISCIPLINE THAT COSTS A DAY IF MISSED: `TryGenerateAreaFor` only
    // QUEUES (`queuedGenerateRooms.Add(room)`); `TryGenerateAreaNow` runs from
    // `AutoBuildRoofAreaSetterTick_First`, i.e. NEXT TICK. An acceptance that
    // reads the roof area in the same call as the placement sees nothing and
    // reports a correct implementation as broken. Advance one tick first.
    // =========================================================================
    public static class LayoutVerbs
    {
        // A 7x7 module is 49 elements and `freezer-kitchen` is 66 across two
        // layers, so this is roomy; it is a budget rather than a wall, and it
        // refuses rather than truncating because a TRUNCATED layout is a
        // half-built room by another name.
        public const int MaxElements = 600;

        // The crop echo's margin around the layout's own rect.
        private const int EchoMargin = 2;

        // Rows are capped in the ENVELOPE and complete in the tally — the
        // truncation contract every other verb here follows.
        private const int RowCap = 200;

        public const string ModeBlueprint = "blueprint";
        public const string ModeInstant = "instant";

        // What `at` means. Published on every result: the alternative is a
        // convention that lives only in a comment, which is how `find-rect`'s
        // corner and `dev:spawn-thing`'s centre spent a session disagreeing.
        public const string Anchor = "north-west";

        // ------------------------------------------------------------------
        // place-layout
        // ------------------------------------------------------------------
        [Verb("place-layout")]
        public static object PlaceLayout(VerbContext ctx)
        {
            var map = Find.CurrentMap ?? throw new VerbArgsException("no current map");
            var a = ctx.Args;

            a.NearMiss("elements", "layout", "placements", "items", "tokens", "grid");
            a.NearMiss("stuff_map", "stuffmap", "stuff-map", "materials");

            string mode = a.Str("mode", ModeBlueprint);
            if (mode != ModeBlueprint && mode != ModeInstant)
                throw new VerbArgsException(
                    $"mode must be '{ModeBlueprint}' or '{ModeInstant}' (got '{mode}')");
            bool partial = a.Bool("partial", false);
            bool dryRun = a.Bool("dry_run", false);
            bool strictStuff = a.Bool("strict_stuff", false);
            string name = a.Str("name");

            var stuffMap = ReadStuffMap(a.Raw("stuff_map"));

            IntVec3 origin = IntVec3.Invalid;
            bool hasOrigin = a.Has("origin");
            if (hasOrigin) origin = Positions.Resolve(map, a.Raw("origin"));

            if (!(a.Raw("elements") is List<object> rawElements))
                throw new VerbArgsException(
                    "elements must be an array of {def, at, rot?, stuff?} — the RESOLVED layout. "
                    + "This verb does not read a layout file or a token grid; `rwa place-layout "
                    + "<file.ir.json> --origin P` expands the IR with baseviz/ir.py and sends one "
                    + "call (git-bug 1adc737 #13).");
            if (rawElements.Count == 0)
                throw new VerbArgsException("elements is empty — there is nothing to place");
            if (rawElements.Count > MaxElements)
                throw new VerbArgsException(
                    $"{rawElements.Count} elements exceeds the {MaxElements} cap. The cap refuses "
                    + "rather than truncating: a truncated layout is a half-built room, which is "
                    + "the state this verb's whole preflight exists to prevent.");

            // ---------------------------------------------------- parse ----
            // EVERY element is parsed before any is judged, and parse errors
            // are collected rather than thrown, because the plural form IS the
            // verb: an agent fixing a layout wants all its bad defs in one
            // round trip, not one per call.
            var elements = new List<Element>();
            var failures = new List<object>();
            int failed = 0;
            for (int i = 0; i < rawElements.Count; i++)
            {
                var e = Parse(map, rawElements[i], i, stuffMap, strictStuff, out var parseFail);
                if (parseFail != null) { failures.Add(parseFail); failed++; continue; }
                elements.Add(e);
            }

            // -------------------------------------------------- ordering ----
            // Terrain first, input order preserved within each rank. See the
            // class header: this is the ONE ordering rule the game's own gates
            // justify.
            var ordered = new List<Element>(elements);
            ordered.Sort((x, y) =>
            {
                int r = x.Rank.CompareTo(y.Rank);
                return r != 0 ? r : x.Index.CompareTo(y.Index);
            });

            // ------------------------------------------------- preflight ----
            // 1. The game's gate, per element, against the map as it is now.
            for (int i = 0; i < ordered.Count; i++)
            {
                var e = ordered[i];
                e.Verdict = SiteGate.Check(map, e.Def, e.Pos, e.Rot, e.Stuff);
                if (e.Verdict.Ok) continue;
                failed++;
                failures.Add(RefusalRow(map, e, e.Verdict, "preflight"));
            }

            // 2. Self-overlap, answered by the game's own def-level predicate.
            var selfOverlaps = SelfOverlaps(ordered, mode);
            failed += selfOverlaps.Count;
            failures.AddRange(selfOverlaps);

            // 3. Interaction interference between our own elements — REPORTED,
            //    never refused. See the class header.
            var selfConflicts = SelfInteractionConflicts(map, ordered);

            var rect = BoundingRect(ordered, origin, hasOrigin, a.Raw("size"));

            var data = new Dictionary<string, object>
            {
                ["gate"] = SiteGate.GateId,
                ["mode"] = mode,
                ["anchor"] = Anchor,
                ["anchor_note"] =
                    "each element's `at` is its footprint's NORTH-WEST cell (templates/INDEX.md "
                    + "pin 1). `pos` is the game's placement centre and `footprint` is [x,z,w,h] "
                    + "whose x,z is the SOUTH-WEST corner — which is what `build --at` and "
                    + "`find-rect` take. Three values, three names, never interchangeable.",
                ["origin"] = hasOrigin ? Positions.Out(origin) : null,
                ["rect"] = Footprint.Out(rect),
                ["requested"] = rawElements.Count,
                ["partial"] = partial,
                ["dry_run"] = dryRun,
            };

            var preflight = new Dictionary<string, object>
            {
                ["ok"] = failed == 0,
                ["checked"] = rawElements.Count,
                ["failed"] = failed,
                ["failures"] = Cap(failures, out int failuresMore),
                ["source"] = "SiteGate.Check (GenConstruct.CanPlaceBlueprintAt(godMode:false) + "
                    + "Designator_Build.Visible) per element, then "
                    + "GenConstruct.CanPlaceBlueprintOver between every intersecting pair",
            };
            if (failuresMore > 0) preflight["failures_more"] = failuresMore;
            if (selfConflicts.Count > 0)
            {
                preflight["self_conflicts"] = Cap(selfConflicts, out int scMore);
                if (scMore > 0) preflight["self_conflicts_more"] = scMore;
                preflight["self_conflicts_note"] =
                    "REPORTED, NOT REFUSED. These are interaction-cell collisions between two "
                    + "elements of this layout. Vanilla's own NotBlockingAnyInteractionCells only "
                    + "walks GenAdj.CellsAdjacentCardinal, so a diagonal arrangement the game "
                    + "accepts exists and refusing here would be a stricter gate than the widget's. "
                    + "The gate that decides is re-asked immediately before each placement; see "
                    + "`late_refusals`.";
            }
            data["preflight"] = preflight;

            data["materials"] = MaterialBill(map, ordered, out var shortfall, out int unpriced);
            data["shortfall"] = shortfall;
            if (unpriced > 0) data["materials_unpriced"] = unpriced;
            data["stuff_defaulted"] = CountDefaulted(ordered);

            // THE INVARIANT. Any preflight failure and nothing is placed —
            // unless the caller opted out with `partial`, which is the only
            // door out of it and is echoed above so a reader of the envelope
            // knows which contract was in force.
            if (dryRun || (failed > 0 && !partial))
            {
                data["ok"] = failed == 0;
                data["layout_id"] = null;
                data["placed"] = new List<object>();
                data["placed_count"] = 0;
                // Everything was skipped, because nothing was attempted — not
                // `failed`, which counts preflight FAILURES and can exceed the
                // element count (a self-overlap is one row per offending pair).
                data["skipped"] = dryRun ? 0 : rawElements.Count;
                data["detail"] = dryRun
                    ? "dry_run: preflight only, nothing was placed"
                    : (failed + " preflight failure(s) across " + rawElements.Count
                       + " elements, so NOTHING was placed. Clear the blockers (see each "
                       + "failure's `blocker.removal`) or site the layout elsewhere, then retry; "
                       + "`partial:true` places the rest.");
                data["view"] = Echo(map, rect, out string viewNote);
                if (viewNote != null) data["view_note"] = viewNote;
                return data;
            }

            // ----------------------------------------------------- place ----
            var record = Layouts.Open(map, mode, origin, hasOrigin, rect, name);
            var placed = new List<object>();
            var undo = new List<Undo>();
            var lateRefusals = new List<object>();
            var skipped = new List<object>();
            var cleared = new List<object>();
            var wiped = new List<object>();
            int clearedMore = 0, wipedMore = 0;
            bool rolledBack = false;

            for (int i = 0; i < ordered.Count; i++)
            {
                var e = ordered[i];
                if (e.Verdict != null && !e.Verdict.Ok)
                {
                    // Only reachable under `partial`.
                    skipped.Add(RefusalRow(map, e, e.Verdict, "preflight"));
                    continue;
                }

                // THE GATE, ASKED AGAIN, against the map this call has been
                // changing. Cheap (one CanPlaceBlueprintAt plus the clause
                // walk) and it is the only thing that can see an element of
                // this layout blocking another one.
                var now = SiteGate.Check(map, e.Def, e.Pos, e.Rot, e.Stuff);
                if (!now.Ok)
                {
                    var row = RefusalRow(map, e, now, "late");
                    row["note"] = "the site was clear at preflight and is not now — something "
                        + "placed earlier in THIS call is in the way, or the map changed under us";
                    lateRefusals.Add(row);
                    if (partial) { skipped.Add(row); continue; }
                    rolledBack = true;
                    break;
                }

                Place(map, e, mode, undo, cleared, wiped, ref clearedMore, ref wipedMore);
                // `e.PlaceMode` is what actually ran — `instant`, `blueprint`
                // or vanilla's `instant-zero-work` branch — never the mode
                // argument, because a zero-work def inside a blueprint-mode
                // layout is already built and `construction` must not call it a
                // blueprint.
                var p = Placements.Record(map, e.Def, e.Stuff, e.Pos, e.Rot,
                    e.PlaceMode, e.Produced);
                e.PlacementId = p.Id;
                record.PlacementIds.Add(p.Id);
                placed.Add(PlacedRow(e, p));
            }

            var rollback = rolledBack ? Rollback(map, undo) : null;
            if (rolledBack)
            {
                Layouts.Abandon(record);
                placed.Clear();
            }

            // --------------------------------------------------- publish ----
            // ONE journal row for the whole layout, carrying EVERY placement
            // id. Not N rows: a 66-element layout would bury the journal, and
            // the durability the ids need is satisfied by one row that names
            // them all. Each element is still in `Placements`, so
            // `construction {placement_id}` answers for it individually and the
            // Frame hooks keep writing their own `construction` rows.
            int tick = 0;
            try { tick = Find.TickManager.TicksGame; } catch { }
            var payload = new Dictionary<string, object>
            {
                ["verb"] = "place-layout",
                ["step"] = mode,
                ["target"] = (name ?? "layout") + " " + rect.Width + "x" + rect.Height
                    + " @ " + rect.minX + "," + rect.minZ + " [" + mode + "]"
                    + (rolledBack ? " ROLLED BACK" : ""),
                ["layout_id"] = record.Id,
                ["mode"] = mode,
                ["origin"] = hasOrigin ? Positions.Out(origin) : null,
                ["rect"] = Footprint.Out(rect),
                ["requested"] = rawElements.Count,
                ["placed"] = placed.Count,
                ["skipped"] = skipped.Count,
                ["rolled_back"] = rolledBack,
                ["gate"] = SiteGate.GateId,
                ["placements"] = JournalPlacements(ordered),
            };
            if (cleared.Count > 0) { payload["cleared"] = cleared; payload["cleared_mode"] = "Deconstruct"; }
            if (wiped.Count > 0) { payload["wiped"] = wiped; payload["wiped_mode"] = "VanishOrMoveAside"; }
            long seq = Journal.Emit("action", payload, tick);
            record.JournalSeq = seq;

            data["ok"] = !rolledBack && failed == 0 && lateRefusals.Count == 0;
            data["layout_id"] = rolledBack ? null : record.Id;
            data["placed"] = Cap(placed, out int placedMore);
            if (placedMore > 0) data["placed_more"] = placedMore;
            data["placed_count"] = placed.Count;
            data["skipped"] = skipped.Count;
            if (skipped.Count > 0) data["skipped_rows"] = Cap(skipped, out int _);
            if (lateRefusals.Count > 0) data["late_refusals"] = Cap(lateRefusals, out int _);
            data["rolled_back"] = rolledBack;
            if (rollback != null) data["rollback"] = rollback;
            if (cleared.Count > 0)
            {
                data["cleared"] = Cap(cleared, out int _);
                data["cleared_mode"] =
                    "Deconstruct — the blueprint's own wipe mode (Designator_Build passes it), so "
                    + "what it removed was REFUNDED.";
                if (clearedMore > 0) data["cleared_more"] = clearedMore;
            }
            if (wiped.Count > 0)
            {
                data["wiped"] = Cap(wiped, out int _);
                data["wiped_mode"] =
                    "VanishOrMoveAside — GenSpawn.Spawn runs CheckMoveItemsAside first, so items a "
                    + "colonist would have hauled out are MOVED; anything listed here was destroyed.";
                if (wipedMore > 0) data["wiped_more"] = wipedMore;
            }
            data["journal_seq"] = seq;
            if (seq == 0)
                data["provenance"] = "NOT WRITTEN — the journal writer is closed, so this layout "
                    + "has no journal line. `construction` and `cancel-layout` can still answer for "
                    + "the ids in this session; nothing outside it can.";
            if (mode == ModeInstant)
                data["audit_hint"] =
                    "instant ≡ what blueprint mode would have produced is PROVABLE, not asserted: "
                    + "`site-audit {rect: " + Show(rect) + "}` re-runs the validator over every "
                    + "player building in this rect, and 0 hits means this state is one blueprint "
                    + "mode could have reached.";
            data["view"] = Echo(map, rect, out string note2);
            if (note2 != null) data["view_note"] = note2;
            return data;
        }

        // ------------------------------------------------------------------
        // cancel-layout
        // ------------------------------------------------------------------
        // THE UNDO, AND IT IS BOOKKEEPING OVER A SHIPPED DESIGNATOR.
        //
        // `RimWorld/Designator_Cancel.DesignateThing` is
        // `if (t is Frame || t is Blueprint) { t.Destroy(DestroyMode.Cancel); }`
        // and its `CanDesignateThing` ends `return t.Faction == Faction.OfPlayer
        // && (t is Frame || t is Blueprint);`. That is the whole game-facing
        // half, it already ships as `designate {type:"cancel"}`, and this verb
        // adds exactly one thing the designator cannot do: knowing WHICH
        // blueprints a placement id owns (git-bug 1adc737 #8).
        //
        // IT POINTS AT THINGS, NOT AT CELLS, and that is deliberate.
        // `Designator_Cancel.DesignateSingleCell` also removes every cancelable
        // DESIGNATION on the cell — a mine order, a chop order, a smoothing
        // order the agent put there on purpose. Cancelling a layout must not
        // quietly undo a designation nobody asked about, so the targets are the
        // layout's own blueprints and frames, resolved by id, and run through
        // `DesignateEngine.RunThings` — the same driver `designate` uses, with
        // the same gate, the same rejection shape and the same `Finalize`.
        [Verb("cancel-layout")]
        public static object CancelLayout(VerbContext ctx)
        {
            var map = Find.CurrentMap ?? throw new VerbArgsException("no current map");
            var a = ctx.Args;
            a.NearMiss("layout_id", "layoutid", "layout", "id");

            bool dryRun = a.Bool("dry_run", false);
            var targets = new List<Placement>();
            LayoutRecord record = null;
            string scope;

            if (a.Has("layout_id"))
            {
                string id = a.StrReq("layout_id");
                record = Layouts.Get(id) ?? throw new VerbArgsException(
                    $"no layout '{id}' in this session. Layout and placement ids are "
                    + "session-scoped and are cleared at a game boundary (a load, a new game, a "
                    + "return to the main menu); the journal's `action` row is the durable record.");
                for (int i = 0; i < record.PlacementIds.Count; i++)
                {
                    var p = Placements.Get(record.PlacementIds[i]);
                    if (p != null) targets.Add(p);
                }
                scope = "layout";
            }
            else if (a.Has("placement_id"))
            {
                string id = a.StrReq("placement_id");
                var p = Placements.Get(id) ?? throw new VerbArgsException(
                    $"no placement '{id}' in this session (see `construction` for the same rule)");
                targets.Add(p);
                record = Layouts.Owning(id);
                scope = "placement";
            }
            else
            {
                throw new VerbArgsException(
                    "cancel-layout needs 'layout_id' (every outstanding blueprint or frame of one "
                    + "place-layout call) or 'placement_id' (one of them). `rwa place-layout` "
                    + "returns both.");
            }

            // Resolve each placement to the live Blueprint or Frame it owns.
            // `Placements.Answer` is the same state machine `construction`
            // publishes, so "already built" and "already cancelled" are told
            // apart here exactly as they are there — which matters, because a
            // finished build and a cancelled one are the same absence.
            var things = new List<Thing>();
            var rows = new List<object>();
            var byState = new Dictionary<string, int>();
            for (int i = 0; i < targets.Count; i++)
            {
                var p = targets[i];
                var answer = Placements.Answer(p);
                string state = answer["state"] as string;
                byState[state] = byState.TryGetValue(state, out var n) ? n + 1 : 1;
                var row = new Dictionary<string, object>
                {
                    ["placement_id"] = p.Id,
                    ["def"] = p.DefName,
                    ["at"] = Positions.Out(p.Pos),
                    ["state"] = state,
                };
                var live = LiveConstructible(map, p);
                if (live != null)
                {
                    things.Add(live);
                    row["thing_id"] = live.thingIDNumber;
                    row["outcome"] = dryRun ? "would-cancel" : "cancelling";
                }
                else
                {
                    row["thing_id"] = null;
                    row["outcome"] = state == Placements.StateBuilt ? "already-built" : "not-present";
                    row["detail"] = state == Placements.StateBuilt
                        ? "there is no blueprint or frame left to cancel — this build finished (or "
                          + "was placed in instant mode). Removing a standing building is "
                          + "`designate {type:\"deconstruct\"}`, which is a different decision."
                        : "no blueprint and no frame at this cell; it was already cancelled, "
                          + "destroyed, or its map is gone";
                }
                rows.Add(row);
            }

            var des = new Designator_Cancel();
            des.isOrder = true;
            bool visible;
            try { visible = des.Visible; } catch { visible = true; }
            if (!visible)
                throw new VerbArgsException(
                    "Designator_Cancel.Visible is false in this game — the cancel designator is "
                    + "hidden from the architect menu, so the agent may not use it either");

            var accepted = new List<Thing>();
            var rejects = new List<DesignateEngine.Reject>();
            DesignateEngine.RunThings(map, des, things, dryRun, accepted, rejects);
            if (!dryRun) DesignateEngine.FinalizeSucceeded(des, accepted.Count > 0);

            var echoCells = new List<IntVec3>();
            for (int i = 0; i < targets.Count; i++) echoCells.Add(targets[i].Pos);

            var byStateOut = new Dictionary<string, object>();
            foreach (var kv in byState) byStateOut[kv.Key] = kv.Value;

            var data = new Dictionary<string, object>
            {
                ["ok"] = rejects.Count == 0,
                ["scope"] = scope,
                ["layout_id"] = record?.Id,
                ["mode"] = record?.Mode,
                ["dry_run"] = dryRun,
                ["gate"] = "RimWorld/Designator_Cancel.CanDesignateThing",
                ["gate_detail"] =
                    "Designator_Cancel.DesignateThing destroys a player Blueprint or Frame with "
                    + "DestroyMode.Cancel, which REFUNDS a frame's contents. Aimed at this "
                    + "layout's own things by id, never at their cells: the cell form would also "
                    + "remove every cancelable designation there, which nobody asked for.",
                ["targets"] = targets.Count,
                ["cancelled"] = accepted.Count,
                ["by_state"] = byStateOut,
                ["placements"] = Cap(rows, out int rowsMore),
            };
            if (rowsMore > 0) data["placements_more"] = rowsMore;
            DesignateEngine.PublishRejects(map, rejects, data);

            if (!dryRun && accepted.Count > 0)
            {
                int tick = 0;
                try { tick = Find.TickManager.TicksGame; } catch { }
                var ids = new List<object>();
                for (int i = 0; i < targets.Count; i++) ids.Add(targets[i].Id);
                long seq = Journal.Emit("action", new Dictionary<string, object>
                {
                    ["verb"] = "cancel-layout",
                    ["step"] = scope,
                    ["target"] = (record?.Id ?? targets[0].Id) + " — " + accepted.Count
                        + " of " + targets.Count + " cancelled",
                    ["layout_id"] = record?.Id,
                    ["placements"] = ids,
                    ["cancelled"] = accepted.Count,
                    ["gate"] = "RimWorld/Designator_Cancel.CanDesignateThing",
                }, tick);
                data["journal_seq"] = seq;
                if (record != null) record.CancelledSeq = seq;
            }

            data["view"] = DesignateEngine.Echo(map, echoCells);
            return data;
        }

        // ==================================================== the elements ==

        private sealed class Element
        {
            public int Index;                 // the caller's own index, for its error messages
            public string Label;
            public BuildableDef Def;
            public ThingDef Stuff;
            public string StuffSource;
            public Rot4 Rot;
            public bool RotGiven;
            public IntVec3 At;                // the NORTH-WEST cell, as given
            public IntVec3 Pos;               // the game's placement centre
            public CellRect Rect;
            public int Rank;                  // 0 terrain, 1 everything else
            public SiteVerdict Verdict;
            public Thing Produced;
            public string PlaceMode;
            public string PlacementId;

            public ThingDef AsThing => Def as ThingDef;
        }

        // One element, parsed and sited. Returns null and fills `fail` rather
        // than throwing: see the call site.
        private static Element Parse(Map map, object raw, int index,
            Dictionary<string, string> stuffMap, bool strictStuff, out Dictionary<string, object> fail)
        {
            fail = null;
            if (!(raw is Dictionary<string, object> obj))
            {
                fail = ParseFail(index, null, null, "element must be an object {def, at, rot?, stuff?}");
                return null;
            }
            var ea = new VerbArgs(obj);
            string defName = null;
            try
            {
                defName = ea.StrReq("def");
                var e = new Element { Index = index, Label = ea.Str("label") };
                e.Def = SiteGate.Named(defName);
                if (!e.Def.BuildableByPlayer)
                    throw new VerbArgsException(
                        $"'{e.Def.defName}' is not BuildableByPlayer (Verse/BuildableDef: its "
                        + "designationCategory is null, so no Designator_Build exists for it and "
                        + "it has no blueprintDef either)");

                // STUFF, EXPLICITLY, WITH ITS SOURCE PUBLISHED. 1adc737's
                // invariant is "stuff resolution explicit (no silent
                // substitutes)", and the substitute that would otherwise be
                // silent is `GenStuff.DefaultStuffFor`. It is not forbidden —
                // it is what `Designator_Build`'s stuff dropdown opens on, and
                // refusing it outright would make `place-layout
                // templates/bedroom.ir.json` fail out of the box for a template
                // whose own INDEX.md says material is bound at placement. So it
                // is ALLOWED, NAMED (`stuff_source`), COUNTED
                // (`stuff_defaulted`) and refusable (`strict_stuff:true`).
                string stuffArg = ea.Str("stuff");
                e.StuffSource = "element";
                if (stuffArg == null && e.Def.MadeFromStuff && stuffMap != null)
                {
                    if (stuffMap.TryGetValue(e.Def.defName, out var byDef))
                    { stuffArg = byDef; e.StuffSource = "stuff_map"; }
                    else if (stuffMap.TryGetValue("*", out var any))
                    { stuffArg = any; e.StuffSource = "stuff_map:*"; }
                }
                if (stuffArg == null)
                    e.StuffSource = e.Def.MadeFromStuff ? "game-default" : "not-stuffable";
                if (stuffArg == null && e.Def.MadeFromStuff && strictStuff)
                    throw new VerbArgsException(
                        $"'{e.Def.defName}' is MadeFromStuff and strict_stuff is set, but neither "
                        + "the element nor stuff_map names a material (add a `stuff`, a "
                        + $"stuff_map entry for '{e.Def.defName}', or a stuff_map '*' default)");
                e.Stuff = SiteVerbs.ResolveStuff(e.Def, stuffArg);

                e.Rot = Rotations.Arg(ea, "rot", e.Def.defaultPlacingRot);
                e.RotGiven = ea.Has("rot");

                if (!ea.Has("at"))
                    throw new VerbArgsException("element needs 'at' — the footprint's north-west cell");
                e.At = Positions.Resolve(map, ea.Raw("at"));

                // NORTH-WEST -> SOUTH-WEST -> CENTRE. The first step is the
                // IR's convention meeting the mod's; the second is Siting.cs's
                // and is never re-implemented here.
                var rotated = Footprint.RotatedSize(e.Def.Size, e.Rot);
                var corner = new IntVec3(e.At.x, 0, e.At.z - (rotated.z - 1));
                if (!Footprint.TryCentreFor(e.Def.Size, corner, e.Rot, out var pos))
                    throw new VerbArgsException(
                        $"could not invert GenAdj.OccupiedRect for {e.Def.defName} at "
                        + $"{e.At.x},{e.At.z} rot {e.Rot.ToStringWord()} — the round trip did not "
                        + "close, so no centre reproduces that footprint");
                e.Pos = pos;
                e.Rect = GenAdj.OccupiedRect(pos, e.Rot, e.Def.Size);
                e.Rank = e.Def is TerrainDef ? 0 : 1;
                return e;
            }
            catch (VerbArgsException ex)
            {
                fail = ParseFail(index, defName, ea.Raw("at"), ex.Message);
                return null;
            }
        }

        private static Dictionary<string, object> ParseFail(int index, string defName, object at, string why)
            => new Dictionary<string, object>
            {
                ["index"] = index,
                ["def"] = defName,
                ["at"] = at,
                ["role"] = "element",
                ["ok"] = false,
                ["stage"] = "parse",
                ["reason"] = why,
                ["blocker"] = null,
            };

        // The refusal row, in the shape every other refusal in this mod uses —
        // `{at, role, ok, reason, blocker}` plus the half that refused — so a
        // caller reads a refused layout with the code that reads a refused
        // survey (git-bug 1adc737 #7 point 3).
        private static Dictionary<string, object> RefusalRow(Map map, Element e, SiteVerdict v, string stage)
        {
            var cell = v.PlaceOk ? null : SiteVerbs.FirstRefusingRow(map, e.Def, e.Stuff, e.Pos, e.Rot);
            var row = new Dictionary<string, object>
            {
                ["index"] = e.Index,
                ["def"] = e.Def?.defName,
                ["stuff"] = e.Stuff?.defName,
                ["at"] = Positions.Out(e.At),
                ["pos"] = Positions.Out(e.Pos),
                ["rot"] = e.Rot.ToStringWord(),
                ["footprint"] = Footprint.Out(e.Rect),
                ["ok"] = false,
                ["stage"] = stage,
                // WHICH HALF refused: "the ground refuses this" and "this is not
                // on the architect menu at all" are different news and are
                // actionable in different ways.
                ["half"] = v.PlaceOk ? "selectable" : "verdict",
                ["reason"] = v.PlaceOk ? v.SelectableDetail : v.PlaceReason,
                ["clause"] = v.PlaceOk ? v.SelectableClause : null,
                ["role"] = cell != null ? cell["role"] : (v.PlaceOk ? "selectable" : "footprint"),
                ["cell"] = cell != null ? cell["at"] : Positions.Out(e.Pos),
                ["blocker"] = cell != null ? cell["blocker"] : null,
            };
            return row;
        }

        // ===================================================== self-checks ==

        // Every intersecting pair, in PLACEMENT order, asked of the game's own
        // `GenConstruct.CanPlaceBlueprintOver`. This is the half of the
        // preflight the map cannot answer, because the map does not yet contain
        // the layout — and it is exact rather than a heuristic, because that
        // member is a pure function of two defs and two stuffs.
        //
        // The `oldDef` handed to it is what the earlier element will BE by the
        // time the later one is placed: its `blueprintDef` in blueprint mode
        // (a `Blueprint_*` ThingDef whose `entityDefToBuild` is the real def),
        // the def itself in instant mode. Same member, different argument, and
        // getting that wrong would answer a question about the wrong object.
        private static List<object> SelfOverlaps(List<Element> ordered, string mode)
        {
            var found = new List<object>();
            for (int j = 0; j < ordered.Count; j++)
            {
                var later = ordered[j];
                for (int i = 0; i < j; i++)
                {
                    var earlier = ordered[i];
                    if (!later.Rect.Overlaps(earlier.Rect)) continue;

                    // Two TerrainDefs on one cell is one floor over another,
                    // which is legal on the map (the second replaces the first)
                    // and is always a layout mistake: nothing in the IR can mean
                    // "put two floors here", so it is a duplicate token.
                    if (later.Def is TerrainDef && earlier.Def is TerrainDef)
                    {
                        found.Add(ConflictRow(later, earlier, "self-overlap",
                            "two terrain defs occupy the same cell (" + earlier.Def.defName
                            + " then " + later.Def.defName + ") — one of them is a duplicate token"));
                        continue;
                    }
                    // Terrain and a thing coexist by construction: terrain lives
                    // in the terrain grid, not the thing grid. The affordance
                    // question between them is the game's and is asked by the
                    // per-element gate.
                    if (later.Def is TerrainDef || earlier.Def is TerrainDef) continue;

                    if (later.Def == earlier.Def)
                    {
                        found.Add(ConflictRow(later, earlier, "self-overlap",
                            "IdenticalThingExists — two " + later.Def.defName
                            + " elements occupy the same cell"));
                        continue;
                    }
                    var oldDef = mode == ModeInstant
                        ? earlier.AsThing
                        : (earlier.Def.blueprintDef ?? earlier.AsThing);
                    if (oldDef == null) continue;
                    bool over;
                    try { over = GenConstruct.CanPlaceBlueprintOver(later.Def, oldDef, later.Stuff, earlier.Stuff); }
                    catch { over = true; }
                    if (!over)
                        found.Add(ConflictRow(later, earlier, "self-overlap",
                            "SpaceAlreadyOccupied — GenConstruct.CanPlaceBlueprintOver refuses "
                            + later.Def.defName + " over " + earlier.Def.defName
                            + " and both are in this layout"));
                }
            }
            return found;
        }

        // The two interaction rules, applied between our OWN elements. Reported
        // and never refused — see the class header for why a stricter gate is
        // as wrong as a looser one. The test is symmetric on purpose: whichever
        // of the pair is placed second, one of vanilla's two rules refuses it,
        // so naming the pair is more useful than naming an order.
        private static List<object> SelfInteractionConflicts(Map map, List<Element> ordered)
        {
            var found = new List<object>();
            for (int i = 0; i < ordered.Count; i++)
            {
                var owner = ordered[i];
                var thingDef = owner.AsThing;
                if (thingDef == null || !thingDef.HasSingleOrMultipleInteractionCells) continue;
                List<IntVec3> spots;
                try { spots = SiteVerbs.InteractionCells(owner.Def, owner.Pos, owner.Rot, map); }
                catch { continue; }
                for (int s = 0; s < spots.Count; s++)
                {
                    for (int j = 0; j < ordered.Count; j++)
                    {
                        if (j == i) continue;
                        var other = ordered[j];
                        if (!other.Rect.Contains(spots[s])) continue;
                        var otherThing = other.AsThing;
                        // Vanilla's own two tests, verbatim in substance:
                        // InteractionCellStandable refuses an occupant whose
                        // passability is not Standable or whose def is the same
                        // def; NotBlockingAnyInteractionCells refuses a
                        // non-Standable entDef covering somebody's spot.
                        bool blocks = otherThing != null
                            && (otherThing.passability != Traversability.Standable
                                || otherThing == thingDef);
                        if (!blocks) continue;
                        found.Add(ConflictRow(other, owner, "interaction-spot",
                            other.Def.defName + " covers " + owner.Def.defName
                            + "'s interaction cell at " + Show(spots[s])
                            + " — GenConstruct.InteractionCellStandable "
                            + "(\"InteractionSpotWillBeBlocked\") or NotBlockingAnyInteractionCells "
                            + "(\"WouldBlockInteractionSpot\") will refuse whichever of the two is "
                            + "placed second"));
                    }
                }
            }
            return found;
        }

        private static Dictionary<string, object> ConflictRow(Element later, Element earlier,
            string kind, string why)
            => new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["index"] = later.Index,
                ["def"] = later.Def?.defName,
                ["at"] = Positions.Out(later.At),
                ["footprint"] = Footprint.Out(later.Rect),
                ["ok"] = false,
                ["stage"] = "self-check",
                ["role"] = "layout",
                ["with_index"] = earlier.Index,
                ["with_def"] = earlier.Def?.defName,
                ["with_at"] = Positions.Out(earlier.At),
                ["reason"] = why,
                // No `blocker`: nothing is on the map yet. The pair IS the
                // blocker, and it is named by index so the caller can edit the
                // layout it sent rather than the ground.
                ["blocker"] = null,
            };

        // ====================================================== the placing ==

        private sealed class Undo
        {
            public Thing Thing;
            public IntVec3 Pos;
            public TerrainDef PrevTerrain;
            public bool Foundation;
            public bool Instant;
        }

        // `Designator_Build.DesignateSingleCell`'s own order, reached through
        // `build`'s helpers rather than a second copy of them.
        private static void Place(Map map, Element e, string mode, List<Undo> undo,
            List<object> cleared, List<object> wiped, ref int clearedMore, ref int wipedMore)
        {
            if (mode == ModeInstant)
            {
                var prev = e.Def is TerrainDef ? map.terrainGrid.TerrainAt(e.Pos) : null;
                var watch = WipeWatch.Before(map, e.Def, e.Pos, e.Rot);
                if (e.Def is TerrainDef)
                {
                    e.Produced = BuildVerbs.PlaceZeroWork(map, e.Def, e.Stuff, e.Pos, e.Rot);
                }
                else
                {
                    var thing = ThingMaker.MakeThing(e.AsThing, e.Stuff);
                    thing.SetFactionDirect(Faction.OfPlayer);
                    e.Produced = GenSpawn.Spawn(thing, e.Pos, map, e.Rot, WipeMode.VanishOrMoveAside);
                }
                var destroyed = watch.Destroyed();
                for (int i = 0; i < destroyed.Count; i++)
                    if (wiped.Count < RowCap) wiped.Add(destroyed[i]); else wipedMore++;
                wipedMore += watch.Skipped;
                e.PlaceMode = ModeInstant;
                undo.Add(new Undo
                {
                    Thing = e.Produced,
                    Pos = e.Pos,
                    PrevTerrain = prev,
                    Foundation = (e.Def as TerrainDef)?.isFoundation ?? false,
                    Instant = true,
                });
                PostPlace(map, e);
                return;
            }

            // 1. A Frame this def is allowed to REPLACE, destroyed with
            //    DestroyMode.Cancel — what Designator_Build passes, and what
            //    refunds the frame's contents instead of vanishing them.
            foreach (var frame in BuildVerbs.ReplaceableFrames(map, e.Def, e.Pos))
            {
                if (cleared.Count < RowCap) cleared.Add(Blockers.Describe(frame)); else clearedMore++;
                try { frame.Destroy(DestroyMode.Cancel); } catch { }
            }

            float work = 0f;
            try { work = e.Def.GetStatValueAbstract(StatDefOf.WorkToBuild, e.Stuff); } catch { }
            if (work == 0f)
            {
                // Vanilla's own branch, not a cheat: a blueprint that needs no
                // work is a blueprint no pawn will ever be dispatched to. `mode`
                // per placement says which branch ran.
                var prev = e.Def is TerrainDef ? map.terrainGrid.TerrainAt(e.Pos) : null;
                e.PlaceMode = "instant-zero-work";
                e.Produced = BuildVerbs.PlaceZeroWork(map, e.Def, e.Stuff, e.Pos, e.Rot);
                undo.Add(new Undo
                {
                    Thing = e.Produced,
                    Pos = e.Pos,
                    PrevTerrain = prev,
                    Foundation = (e.Def as TerrainDef)?.isFoundation ?? false,
                    Instant = true,
                });
                PostPlace(map, e);
                return;
            }

            var bpDef = e.Def.blueprintDef;
            if (bpDef == null)
            {
                // Unreachable through BuildableByPlayer (the same condition
                // generates the blueprintDef), guarded because a modded def can
                // set designationCategory without the generator running.
                e.PlaceMode = "no-blueprint";
                return;
            }
            var watch2 = WipeWatch.Before(map, bpDef, e.Pos, e.Rot);
            try { GenSpawn.WipeExistingThings(e.Pos, e.Rot, bpDef, map, DestroyMode.Deconstruct); }
            catch { }
            var gone = watch2.Destroyed();
            for (int i = 0; i < gone.Count; i++)
                if (cleared.Count < RowCap) cleared.Add(gone[i]); else clearedMore++;
            clearedMore += watch2.Skipped;
            e.PlaceMode = ModeBlueprint;
            e.Produced = GenConstruct.PlaceBlueprintForBuild(e.Def, e.Pos, map, e.Rot,
                Faction.OfPlayer, e.Stuff);
            undo.Add(new Undo { Thing = e.Produced, Pos = e.Pos, Instant = false });
            PostPlace(map, e);
        }

        // Wrapped: a PlaceWorker is third-party code for a modded def, and
        // `PlaceWorker_SunLamp`-style hooks are how the game attaches a matching
        // growing zone to a placement. `PlaceWorker_DoorLearnOpeningSpeed` is
        // the one every door in the corpus carries and it overrides PostPlace
        // ONLY — it has no AllowsPlacing, which is the whole answer to
        // 1adc737's "how does door-in-wall adjacency validate": it does not,
        // because there is no such rule.
        private static void PostPlace(Map map, Element e)
        {
            try
            {
                var workers = e.Def.PlaceWorkers;
                if (workers != null)
                    for (int i = 0; i < workers.Count; i++)
                        try { workers[i].PostPlace(map, e.Def, e.Pos, e.Rot); }
                        catch { }
            }
            catch { }
        }

        // ONE CALL, ONE TRANSACTION — the half of the invariant a preflight
        // cannot deliver on its own, because an element of this layout can
        // refuse another one and only the map knows.
        //
        // Blueprints and frames go back through `DestroyMode.Cancel`, which is
        // `Designator_Cancel.DesignateThing`'s own mode and refunds. An
        // instant-mode thing goes through `DestroyMode.Vanish`: this is an UNDO,
        // not a deconstruction, and leaving a pile of stone chunks where a
        // rolled-back wall briefly stood would be a mutation the caller never
        // asked for. Terrain is restored to the def read before the write.
        //
        // WHAT CANNOT BE UNDONE IS SAID SO. Instant mode may have wiped
        // something; `WipeWatch` recorded it and this publishes
        // `incomplete: true` with the reason, because a rollback that quietly
        // fell short is worse than one that failed loudly.
        private static Dictionary<string, object> Rollback(Map map, List<Undo> undo)
        {
            int destroyed = 0, terrain = 0, failed = 0;
            bool incomplete = false;
            var notes = new List<object>();
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                var u = undo[i];
                try
                {
                    if (u.Thing != null && !u.Thing.Destroyed)
                    {
                        u.Thing.Destroy(u.Instant ? DestroyMode.Vanish : DestroyMode.Cancel);
                        destroyed++;
                    }
                    else if (u.PrevTerrain != null)
                    {
                        map.terrainGrid.SetTerrain(u.Pos, u.PrevTerrain);
                        terrain++;
                        if (u.Foundation)
                        {
                            incomplete = true;
                            notes.Add("a foundation was set at " + Show(u.Pos)
                                + "; SetTerrain restores the top layer but the foundation stays");
                        }
                    }
                }
                catch (Exception ex) { failed++; notes.Add(ex.GetType().Name + " at " + Show(u.Pos)); }
            }
            var d = new Dictionary<string, object>
            {
                ["reason"] = "an element was refused by the game AFTER the preflight passed, and "
                    + "`partial` is false — so this call placed nothing, which is the invariant "
                    + "(git-bug 1adc737: no partial placement without --partial)",
                ["destroyed"] = destroyed,
                ["terrain_restored"] = terrain,
                ["failed"] = failed,
                ["incomplete"] = incomplete || failed > 0,
            };
            if (notes.Count > 0) d["notes"] = notes;
            if (incomplete || failed > 0)
                d["detail"] = "the rollback did not fully restore the map — see `notes`. Anything "
                    + "instant mode WIPED is gone regardless; `wiped` on the placing call is the "
                    + "record of it.";
            return d;
        }

        // ======================================================= the extras ==

        private static Dictionary<string, string> ReadStuffMap(object raw)
        {
            if (raw == null) return null;
            if (!(raw is Dictionary<string, object> obj))
                throw new VerbArgsException(
                    "stuff_map must be an object of defName -> stuff defName, e.g. "
                    + "{\"Wall\":\"WoodLog\", \"*\":\"WoodLog\"}. `*` is the default for every "
                    + "MadeFromStuff def the map does not name; an element's own `stuff` beats "
                    + "both.");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in obj)
            {
                if (!(kv.Value is string s))
                    throw new VerbArgsException($"stuff_map['{kv.Key}'] must be a stuff defName");
                map[kv.Key] = s;
            }
            return map;
        }

        private static int CountDefaulted(List<Element> ordered)
        {
            int n = 0;
            for (int i = 0; i < ordered.Count; i++)
                if (ordered[i].StuffSource == "game-default") n++;
            return n;
        }

        // THE BILL, AND WHAT THE COLONY ACTUALLY HAS. 1adc737's Scope asks
        // blueprint mode to "report material bill vs current stockpiles
        // (shortfall list)". The cost comes through `Placements.Materials`,
        // never `TotalMaterialCost()` — see that method for the poisoned-cache
        // trap in `CostListAdjusted(errorOnNullStuff:false)`.
        //
        // `in_stockpiles` is `map.resourceCounter`, which walks SlotGroup haul
        // destinations, so goods lying on unzoned ground read as ZERO. Said
        // here as `ConstructionVerbs` and `DigestVerb` both say it: "we have no
        // steel" and "the steel is not in a stockpile" are different problems
        // and this number cannot tell them apart.
        private static List<object> MaterialBill(Map map, List<Element> ordered,
            out List<object> shortfall, out int unpriced)
        {
            unpriced = 0;
            var total = new Dictionary<ThingDef, int>();
            for (int i = 0; i < ordered.Count; i++)
            {
                var e = ordered[i];
                var costs = Placements.Materials(e.Def, e.Stuff, out string note);
                if (costs == null) { unpriced++; continue; }
                for (int j = 0; j < costs.Count; j++)
                {
                    if (!(costs[j] is Dictionary<string, object> row)) continue;
                    var td = DefDatabase<ThingDef>.GetNamedSilentFail(row["def"] as string);
                    if (td == null) continue;
                    int c = row["count"] is int n ? n : 0;
                    total[td] = total.TryGetValue(td, out var have) ? have + c : c;
                }
            }
            var bill = new List<object>();
            shortfall = new List<object>();
            foreach (var kv in total)
            {
                int stored = ConstructionVerbs.Stored(map, kv.Key);
                bill.Add(new Dictionary<string, object>
                {
                    ["def"] = kv.Key.defName,
                    ["count"] = kv.Value,
                    ["in_stockpiles"] = stored,
                });
                if (stored < kv.Value)
                    shortfall.Add(new Dictionary<string, object>
                    {
                        ["def"] = kv.Key.defName,
                        ["needed"] = kv.Value,
                        ["in_stockpiles"] = stored,
                        ["short_by"] = kv.Value - stored,
                    });
            }
            return bill;
        }

        private static Dictionary<string, object> PlacedRow(Element e, Placement p)
            => new Dictionary<string, object>
            {
                ["index"] = e.Index,
                ["placement_id"] = p.Id,
                ["def"] = e.Def.defName,
                ["stuff"] = e.Stuff?.defName,
                ["stuff_source"] = e.StuffSource,
                ["at"] = Positions.Out(e.At),
                ["pos"] = Positions.Out(e.Pos),
                ["rot"] = e.Rot.ToStringWord(),
                ["rot_source"] = e.RotGiven ? "arg" : "def.defaultPlacingRot",
                ["footprint"] = Footprint.Out(e.Rect),
                ["mode"] = e.PlaceMode,
                ["thing_id"] = e.Produced?.thingIDNumber,
                ["kind"] = Placements.KindOf(e.Produced),
                ["label"] = e.Label,
            };

        private static List<object> JournalPlacements(List<Element> ordered)
        {
            var list = new List<object>();
            for (int i = 0; i < ordered.Count; i++)
            {
                var e = ordered[i];
                if (e.PlacementId == null) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["placement_id"] = e.PlacementId,
                    ["def"] = e.Def.defName,
                    ["stuff"] = e.Stuff?.defName,
                    ["at"] = Positions.Out(e.At),
                    ["pos"] = Positions.Out(e.Pos),
                    ["rot"] = e.Rot.ToStringWord(),
                });
            }
            return list;
        }

        // The live Blueprint or Frame a placement still owns. Matched the way
        // `Placements.Answer` matches — cell, map and the def being built — so
        // the two can never disagree about what a placement id points at.
        private static Thing LiveConstructible(Map map, Placement p)
        {
            var pmap = Placements.MapOf(p);
            if (pmap == null || pmap != map) return null;
            var list = pmap.thingGrid.ThingsListAtFast(p.Pos);
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (t?.def == null) continue;
                if (t.def.entityDefToBuild != p.Def) continue;
                if (t is Blueprint || t is Frame) return t;
            }
            return null;
        }

        private static CellRect BoundingRect(List<Element> ordered, IntVec3 origin, bool hasOrigin,
            object sizeArg)
        {
            // A declared origin + size wins, because it is what the CALLER
            // reasoned about; the bounding box of the elements is the fallback
            // and can be smaller (an IR grid's outer rows are often empty).
            if (hasOrigin && sizeArg is List<object> s && s.Count == 2
                && s[0] is double w && s[1] is double h && w >= 1 && h >= 1)
                return new CellRect(origin.x, origin.z, (int)w, (int)h);
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            for (int i = 0; i < ordered.Count; i++)
            {
                var r = ordered[i].Rect;
                if (r.minX < minX) minX = r.minX;
                if (r.minZ < minZ) minZ = r.minZ;
                if (r.maxX > maxX) maxX = r.maxX;
                if (r.maxZ > maxZ) maxZ = r.maxZ;
            }
            if (minX > maxX)
                return hasOrigin ? new CellRect(origin.x, origin.z, 1, 1) : new CellRect(0, 0, 1, 1);
            return new CellRect(minX, minZ, maxX - minX + 1, maxZ - minZ + 1);
        }

        // The picture, in `map-view`'s alphabet and nothing else. A layout can
        // be wider than `CropRenderer`'s cap, and a verb that THREW on its own
        // echo would refuse a placement it had already made — so the echo is
        // dropped with a reason instead.
        private static Dictionary<string, object> Echo(Map map, CellRect rect, out string note)
        {
            note = null;
            var view = rect.ExpandedBy(EchoMargin);
            int margin = EchoMargin;
            while ((view.Width > CropRenderer.MaxSide || view.Height > CropRenderer.MaxSide)
                   && margin > 0)
                view = rect.ExpandedBy(--margin);
            if (view.Width > CropRenderer.MaxSide || view.Height > CropRenderer.MaxSide)
            {
                note = "no echo: the layout is " + rect.Width + "x" + rect.Height + ", past "
                    + "CropRenderer's " + CropRenderer.MaxSide + "-cell cap. `map-view` over "
                    + Show(rect) + " in two crops shows it.";
                return null;
            }
            try
            {
                return CropRenderer.Render(map, view.ClipInsideMap(map),
                    new List<string>(CropRenderer.DefaultLayers));
            }
            catch (Exception e) { note = "no echo: " + e.GetType().Name; return null; }
        }

        private static List<object> Cap(List<object> rows, out int more)
        {
            more = 0;
            if (rows.Count <= RowCap) return rows;
            var list = new List<object>();
            for (int i = 0; i < RowCap; i++) list.Add(rows[i]);
            more = rows.Count - RowCap;
            return list;
        }

        private static string Show(IntVec3 c) => "[" + c.x + "," + c.z + "]";

        private static string Show(CellRect r)
            => "[" + r.minX + "," + r.minZ + "," + r.Width + "," + r.Height + "]";
    }

    // ============================================================= Layouts ===
    // The layout-id registry, and it is `Placements` one level up: the same
    // reasoning, the same lifetime, the same reason for existing.
    //
    // A layout id names a SET of placement ids. `cancel-layout` needs it because
    // "the blueprints this one call placed" is not a question the map can
    // answer — the map has blueprints, not transactions — and after the layout
    // is built there is nothing on the map that says those forty-nine walls
    // arrived together.
    //
    // IN MEMORY AND SESSION-SCOPED, exactly as `Placements` is and for the same
    // reason: an id names cells on a map, so after a load it would resolve
    // against whatever colony loaded next. `Runtime.ResetForGameBoundary`
    // clears both. The durable record is the journal's `action` row, which
    // carries the layout id AND every placement id in it.
    public static class Layouts
    {
        private const int Cap = 400;

        private static readonly object gate = new object();
        private static readonly List<LayoutRecord> order = new List<LayoutRecord>();
        private static readonly Dictionary<string, LayoutRecord> byId =
            new Dictionary<string, LayoutRecord>(StringComparer.Ordinal);
        private static int counter;

        public static LayoutRecord Open(Map map, string mode, IntVec3 origin, bool hasOrigin,
            CellRect rect, string name)
        {
            lock (gate)
            {
                counter++;
                var r = new LayoutRecord
                {
                    Id = "ly-" + counter,
                    Name = name,
                    Mode = mode,
                    Origin = origin,
                    HasOrigin = hasOrigin,
                    Rect = rect,
                    MapId = map?.uniqueID ?? -1,
                };
                try { r.Tick = Find.TickManager.TicksGame; } catch { }
                order.Add(r);
                byId[r.Id] = r;
                while (order.Count > Cap)
                {
                    byId.Remove(order[0].Id);
                    order.RemoveAt(0);
                }
                return r;
            }
        }

        // A rolled-back call placed nothing, so its id must not survive: a
        // `cancel-layout` against it would answer for a transaction that never
        // happened, which is the id equivalent of reporting a build that is not
        // there.
        public static void Abandon(LayoutRecord r)
        {
            if (r == null) return;
            lock (gate)
            {
                byId.Remove(r.Id);
                order.Remove(r);
            }
        }

        public static LayoutRecord Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (gate) return byId.TryGetValue(id, out var r) ? r : null;
        }

        // The layout a placement id belongs to, for `cancel-layout
        // {placement_id}` — so cancelling one element still reports which
        // transaction it came from. Null for a `build` placement, which belongs
        // to no layout, and that is a fact rather than a failure.
        public static LayoutRecord Owning(string placementId)
        {
            if (string.IsNullOrEmpty(placementId)) return null;
            lock (gate)
                for (int i = order.Count - 1; i >= 0; i--)
                    if (order[i].PlacementIds.Contains(placementId)) return order[i];
            return null;
        }

        public static List<LayoutRecord> All()
        {
            lock (gate) return new List<LayoutRecord>(order);
        }

        public static void Clear()
        {
            lock (gate)
            {
                order.Clear();
                byId.Clear();
            }
        }
    }

    public sealed class LayoutRecord
    {
        public string Id;
        public string Name;
        public string Mode;
        public IntVec3 Origin;
        public bool HasOrigin;
        public CellRect Rect;
        public int MapId;
        public int Tick;
        public long JournalSeq;
        public long CancelledSeq;
        public readonly List<string> PlacementIds = new List<string>();
    }
}
