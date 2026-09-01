using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRimmer
{
    // ============================================================ spec 3.3 ===
    // `construction {rect? | around?+radius? | placement_id? | id?}` — A READ.
    //
    // WHY IT EXISTS (git-bug d7c8088). Before this file `grep Blueprint
    // Source/AutoRimmer/` returned ZERO hits: nothing in ~44 verbs and 34k lines
    // read a `Blueprint`, a `Frame`, `workDone`, `resourceContainer` or
    // `TotalMaterialCost`, and `digest` had no construction section. So the
    // moment `build` places a blueprint the agent is blind to it and cannot
    // distinguish waiting for a hauler to bring steel, fully stocked and waiting
    // for a builder, actively being worked, finished, or cancelled. That is M1's
    // invisible research bench one level up: the symptom was
    // `Alert_ColonistsIdle`, and the cause the agent could not see was the state
    // of the thing it had asked for.
    //
    // COMPLETION IS AN ABSENCE, which is why this verb has a `placement_id`
    // mode. `Frame.CompleteConstruction` destroys the frame and spawns the
    // building; `Designator_Cancel` destroys the frame and spawns nothing. Both
    // leave the cell with no blueprint and no frame, so enumeration alone reports
    // "finished" and "cancelled" identically. `Placements` (BuildVerbs.cs) holds
    // the id, the two Harmony postfixes in `JournalHooks.cs` record the positive
    // transition, and `Placements.Answer` is the by-field answer.
    //
    // ------------------------- OBSERVER DISCIPLINE ---------------------------
    // Three verified hazards, and every one of them is a way for a READ to break
    // the run it is reading.
    //
    // 1. READING MATERIAL COST CAN EMIT A RED ERROR, and every acceptance suite
    //    watermarks red errors, so a naive observer turns a clean run RED by
    //    reading it. `Frame.TotalMaterialCost()` and
    //    `Blueprint_Build.TotalMaterialCost()` both bottom out in
    //    `CostListCalculator.CostListAdjusted`, which `Log.Error`s on a null
    //    stuff for a `MadeFromStuff` def. Neither member is called here:
    //    `Placements.Materials` asks the def directly and REFUSES to ask at all
    //    for that pair — see its header for why `errorOnNullStuff:false` is a
    //    worse fix than the error it silences.
    //
    // 2. `Frame.ThingCountNeededWithEnroute` `Log.Error`s TWICE MORE, on a
    //    negative amount and on an over-large one. The information it carries is
    //    worth having — "somebody is already bringing the steel" is exactly what
    //    separates a stalled build from a slow one — so the enroute count is read
    //    from `Map.enrouteManager.GetEnroute(thing, def, null)` instead, which is
    //    the same number that member subtracts and is a plain `TryGetValue` over
    //    a stored lookup (Verse.AI/EnrouteManager). The clamping vanilla does
    //    around it is ours to do, and doing it silently is the point: an agent
    //    cannot act on a red error.
    //
    // 3. `Frame.WorkToBuild` is a `GetStatValueAbstract` call PER FRAME PER READ.
    //    Fine for a handful and unbounded in principle, so the scan is capped
    //    (`scan_cap`) separately from the row list (`cap`) and both report what
    //    they hid. The rollup is honest about which: `scanned` is what was
    //    measured and `scan_more` is what was not.
    //
    // A fourth, found while writing it and not on the issue:
    // 4. `GenConstruct.FirstBlockingThing(t, pawnToIgnore)` REPORTS THE
    //    COLONIST WHO IS BUILDING IT as the blocker when `pawnToIgnore` is null
    //    — `GenConstruct.BlocksConstruction` ends `if (t is Pawn pawn &&
    //    !pawn.IsHiddenFromPlayer()) return true;`, and the game's own work
    //    givers pass the worker precisely to exclude it. So the worker is
    //    identified FIRST and handed to the call, and a Pawn that still comes
    //    back is published (it is a real fact about the cell) but does not make
    //    the state `blocked`. A build reported blocked by the person building it
    //    is the kind of answer that sends an agent to fix nothing.
    public static class ConstructionVerbs
    {
        // Rows. ~200 bytes a row, so 24 keeps a busy site inside the digest-era
        // budget while `cap` overrides for a deliberate full read.
        private const int RowCap = 24;
        // Items MEASURED. Every frame in this window costs one
        // GetStatValueAbstract and one CostListAdjusted (cached), so it is a cost
        // ceiling and not only a context one — DigestVerb's own argument for its
        // colonist cap.
        private const int ScanCap = 300;
        // The digest's window is tighter still: that verb is documented as
        // called constantly.
        private const int DigestScanCap = 60;

        // States, as tokens. The precedence between them is a RESOLUTION and is
        // stated in DESIGN: blocked > in-progress > awaiting-materials > ready.
        public const string StateAwaitingMaterials = "awaiting-materials";
        public const string StateReady = "ready";
        public const string StateInProgress = "in-progress";
        public const string StateBlocked = "blocked";

        [Verb("construction")]
        public static object Construction(VerbContext ctx)
        {
            var a = ctx.Args;

            // ---------------------------------------- one placement id ------
            // The completion answer, and the only mode that can distinguish
            // `built` from `cancelled` — because both are an empty cell.
            if (a.Has("placement_id"))
            {
                string id = a.StrReq("placement_id");
                var p = Placements.Get(id)
                    ?? throw new VerbArgsException(
                        $"no placement '{id}' in this session. Ids are session-scoped and are "
                        + "cleared at a game boundary (a load, a new game, a return to the main "
                        + "menu); the journal's `action` and `construction` rows are the durable "
                        + "record.");
                var answer = Placements.Answer(p);
                var pmap = Placements.MapOf(p);
                // The live item too, when there is one, so a caller does not
                // have to make a second call to learn WHY it is still a
                // blueprint.
                answer["item"] = pmap == null ? null : ItemAt(pmap, p);
                return answer;
            }

            var map = Find.CurrentMap ?? throw new VerbArgsException("no current map");

            int cap = a.Int("cap", RowCap);
            if (cap < 1 || cap > 500) throw new VerbArgsException("cap must be 1..500");
            int scanCap = a.Int("scan_cap", ScanCap);
            if (scanCap < 1 || scanCap > 5000) throw new VerbArgsException("scan_cap must be 1..5000");

            // ---------------------------------------------- one thing id ------
            if (a.Has("id"))
            {
                int wanted = a.IntReq("id");
                foreach (var t in Constructibles(map))
                    if (t.thingIDNumber == wanted)
                    {
                        var workers = WorkerIndex(map);
                        return new Dictionary<string, object>
                        {
                            ["item"] = Item(map, t, workers),
                        };
                    }
                throw new VerbArgsException(
                    $"no blueprint or frame with id {wanted} on this map. It may have completed or "
                    + "been cancelled — ask by placement_id, which answers for a build that is no "
                    + "longer there.");
            }

            // ------------------------------------------------- the window ----
            CellRect rect;
            string rectSource;
            if (a.Has("rect"))
            {
                if (!(a.Raw("rect") is List<object> r) || r.Count != 4
                    || !(r[0] is double rx) || !(r[1] is double rz)
                    || !(r[2] is double rw) || !(r[3] is double rh))
                    throw new VerbArgsException("rect must be [x,z,w,h]");
                rect = new CellRect((int)rx, (int)rz, Math.Max(1, (int)rw), Math.Max(1, (int)rh));
                rectSource = "rect";
            }
            else if (a.Has("around"))
            {
                var around = Positions.Resolve(map, a.Raw("around"));
                int radius = a.Int("radius", 12);
                if (radius < 1 || radius > 200) throw new VerbArgsException("radius must be 1..200");
                rect = CellRect.CenteredOn(around, radius);
                rectSource = "around";
            }
            else
            {
                rect = CellRect.WholeMap(map);
                rectSource = "whole-map";
            }
            rect = rect.ClipInsideMap(map);

            var workerIndex = WorkerIndex(map);
            var rows = new List<object>();
            var byState = new Dictionary<string, int>();
            var missingTotal = new Dictionary<ThingDef, int>();
            int blueprints = 0, frames = 0, scanned = 0, scanMore = 0, outside = 0;
            float workLeftTotal = 0f;

            foreach (var t in Constructibles(map))
            {
                if (!rect.Contains(t.Position)) { outside++; continue; }
                if (scanned >= scanCap) { scanMore++; continue; }
                scanned++;
                var item = Item(map, t, workerIndex);
                if (t is Frame) frames++; else blueprints++;
                string state = item["state"] as string ?? StateReady;
                byState[state] = byState.TryGetValue(state, out var n) ? n + 1 : 1;
                if (item.TryGetValue("work_left", out var wl) && wl is double d)
                    workLeftTotal += (float)d;
                if (item["missing"] is List<object> miss)
                    foreach (var m in miss)
                        if (m is Dictionary<string, object> md
                            && md["def"] is string defName
                            && md["count"] is int c)
                        {
                            var td = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                            if (td == null) continue;
                            missingTotal[td] = missingTotal.TryGetValue(td, out var have) ? have + c : c;
                        }
                if (rows.Count < cap) rows.Add(item);
            }

            var missingOut = new List<object>();
            foreach (var kv in missingTotal)
                missingOut.Add(new Dictionary<string, object>
                {
                    ["def"] = kv.Key.defName,
                    ["count"] = kv.Value,
                    // The number the agent actually needs to act on: what is in
                    // STOCKPILES. map.resourceCounter walks SlotGroup haul
                    // destinations, so goods lying on unzoned ground read as
                    // ZERO — DigestVerb's header states the same caveat and it
                    // matters more here, because "we have no steel" and "the
                    // steel is not in a stockpile" are different problems.
                    ["in_stockpiles"] = Stored(map, kv.Key),
                });

            var byStateOut = new Dictionary<string, object>();
            foreach (var kv in byState) byStateOut[kv.Key] = kv.Value;

            return new Dictionary<string, object>
            {
                ["rect"] = Footprint.Out(rect),
                ["rect_source"] = rectSource,
                ["blueprints"] = blueprints,
                ["frames"] = frames,
                ["by_state"] = byStateOut,
                ["work_left_total"] = Math.Round(workLeftTotal, 1),
                ["missing"] = missingOut,
                ["items"] = rows,
                ["listed"] = rows.Count,
                ["cap"] = cap,
                ["more"] = Math.Max(0, scanned - rows.Count),
                ["scanned"] = scanned,
                ["scan_cap"] = scanCap,
                ["scan_more"] = scanMore,
                ["outside_rect"] = outside,
            };
        }

        // ------------------------------------------------------- the digest --

        // `digest.construction` — small, capped, in the digest's idiom. This is
        // what makes an idle-colonist run diagnosable in ONE call instead of
        // none: three colonists standing around with four blueprints
        // `awaiting-materials` is a different colony from three colonists
        // standing around with nothing to build.
        internal static Dictionary<string, object> Section(Map map)
        {
            int blueprints = 0, frames = 0, awaiting = 0, blocked = 0, inProgress = 0, ready = 0;
            int scanned = 0, more = 0;
            float workLeft = 0f;
            var workerIndex = WorkerIndex(map);
            foreach (var t in Constructibles(map))
            {
                if (scanned >= DigestScanCap) { more++; continue; }
                scanned++;
                var frame = t as Frame;
                if (frame != null) frames++; else blueprints++;
                MaterialRows(map, t, out int missingKinds, out _);
                var worker = Worker(workerIndex, t);
                var blocker = Blocking(t, worker);
                string state = State(blocker, worker, missingKinds);
                switch (state)
                {
                    case StateBlocked: blocked++; break;
                    case StateInProgress: inProgress++; break;
                    case StateAwaitingMaterials: awaiting++; break;
                    default: ready++; break;
                }
                if (frame != null) workLeft += WorkLeft(frame);
            }
            var d = new Dictionary<string, object>
            {
                ["blueprints"] = blueprints,
                ["frames"] = frames,
                ["awaiting_materials"] = awaiting,
                ["ready"] = ready,
                ["in_progress"] = inProgress,
                ["blocked"] = blocked,
                ["work_left"] = Math.Round(workLeft, 1),
            };
            // Presence is the signal, the same rule Dev.NoteFog follows: the cap
            // only appears when it actually bit, so a reader never compares a
            // zero against a missing key.
            if (more > 0)
            {
                d["more"] = more;
                d["cap"] = DigestScanCap;
                d["cap_note"] = "counts are a floor: Frame.WorkToBuild is a GetStatValueAbstract "
                    + "per frame and the digest is called constantly, so the glance stops at the "
                    + "cap. `construction` reads the rest.";
            }
            return d;
        }

        // ------------------------------------------------------------ items --

        // Blueprints and frames, in one enumeration, from the map's own request
        // groups — `Verse/ThingRequestGroup` has both `Blueprint` and
        // `BuildingFrame`, so nothing is scanned cell by cell. SNAPSHOTTED,
        // because `ListerThings.ThingsInGroup` hands back the real stored list
        // and the loop below reaches stat workers and (through
        // `FirstBlockingThing`) arbitrary def flags.
        private static List<Thing> Constructibles(Map map)
        {
            var list = new List<Thing>();
            try { list.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)); }
            catch { }
            try { list.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)); }
            catch { }
            list.Sort((x, y) => x.thingIDNumber.CompareTo(y.thingIDNumber));
            return list;
        }

        // The live blueprint or frame belonging to one placement, or null.
        private static Dictionary<string, object> ItemAt(Map map, Placement p)
        {
            var list = map.thingGrid.ThingsListAtFast(p.Pos);
            var workers = WorkerIndex(map);
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (t?.def == null || t.def.entityDefToBuild != p.Def) continue;
                if (t is Blueprint || t is Frame) return Item(map, t, workers);
            }
            return null;
        }

        private static Dictionary<string, object> Item(Map map, Thing t,
            Dictionary<int, Pawn> workerIndex)
        {
            var frame = t as Frame;
            var built = t.def.entityDefToBuild;
            var stuff = t.Stuff;
            var worker = Worker(workerIndex, t);
            var blocker = Blocking(t, worker);
            var mats = MaterialRows(map, t, out int missingKinds, out List<object> missing);
            var placement = Placements.For(t);

            var d = new Dictionary<string, object>
            {
                ["id"] = t.thingIDNumber,
                ["kind"] = frame != null ? Placements.StateFrame : Placements.StateBlueprint,
                ["def"] = built?.defName,
                ["def_thing"] = t.def.defName,
                ["stuff"] = stuff?.defName,
                ["at"] = Positions.Out(t.Position),
                ["rot"] = t.Rotation.ToStringWord(),
                ["footprint"] = Footprint.Out(t.OccupiedRect()),
                // Non-null only for a placement THIS session issued. A blueprint
                // the player drew, or one from a save, has no id and says so by
                // absence rather than by a fabricated one.
                ["placement_id"] = placement?.Id,
                ["state"] = State(blocker, worker, missingKinds),
                ["materials"] = mats,
                ["missing"] = missing,
                ["blocking"] = blocker == null ? null : Blockers.Describe(blocker),
                // A Pawn standing on a constructible is `BlocksConstruction`'s
                // last clause and is NOT a stalled build — see this class's
                // hazard 4. Published because it is true; flagged because acting
                // on it would be acting on nothing.
                ["blocking_is_pawn"] = blocker is Pawn,
                ["worker"] = worker == null ? null : new Dictionary<string, object>
                {
                    ["id"] = worker.thingIDNumber,
                    ["name"] = PawnSafe.Name(worker),
                    ["job"] = SafeJobDef(worker),
                },
            };
            // THE GAME'S OWN ANSWER TO "WHY HAS NOBODY STARTED THIS", and it is
            // a public method on Blueprint only, so it is asked of blueprints and
            // reported absent for frames rather than re-derived for them.
            if (t is Blueprint bp)
            {
                Thing haulable = null;
                try { haulable = bp.BlockingHaulableOnTop(); }
                catch { }
                d["blocking_haulable"] = haulable == null ? null : Blockers.Describe(haulable);
            }

            // WORK FIELDS ARE ABSENT ON A BLUEPRINT, not zero. A blueprint has no
            // `workDone` at all, and publishing 0 would make "nobody has started"
            // read exactly like "started and got nowhere" (git-bug d7c8088).
            // `work_total` IS published for both, because the agent wants to know
            // what it is in for — computed with the same expression
            // `Blueprint_Build.WorkTotal` and `Frame.WorkToBuild` use, since both
            // of those members are protected or instance-bound.
            float total = WorkTotal(built, stuff);
            d["work_total"] = Math.Round(total, 1);
            if (frame != null)
            {
                d["work_done"] = Math.Round(frame.workDone, 1);
                d["work_left"] = (double)Math.Round(Math.Max(0f, total - frame.workDone), 1);
                d["percent"] = total > 0f
                    ? Math.Round(Math.Min(1f, frame.workDone / total), 4)
                    : (object)null;
            }
            return d;
        }

        // blocked > in-progress > awaiting-materials > ready. See DESIGN for why
        // this order and not the issue's listing order.
        private static string State(Thing blocker, Pawn worker, int missingKinds)
        {
            if (blocker != null && !(blocker is Pawn)) return StateBlocked;
            if (worker != null) return StateInProgress;
            if (missingKinds > 0) return StateAwaitingMaterials;
            return StateReady;
        }

        // Per-material `{def, needed, present, enroute}` plus the `missing` list.
        //
        // `needed` is the cost list. `present` is what the frame's
        // `resourceContainer` already holds — zero for a blueprint, which holds
        // nothing (`Blueprint.ThingCountNeeded` returns the full count and
        // `Blueprint.IsCompleted()` is `return false;`). `enroute` is read from
        // the enroute manager rather than through
        // `Frame.ThingCountNeededWithEnroute`, which Log.Errors twice — hazard 2.
        private static List<object> MaterialRows(Map map, Thing t, out int missingKinds,
            out List<object> missing)
        {
            missingKinds = 0;
            missing = new List<object>();
            var built = t.def.entityDefToBuild;
            var costs = Placements.Materials(built, t.Stuff, out string note);
            var rows = new List<object>();
            if (costs == null)
            {
                if (note != null)
                    rows.Add(new Dictionary<string, object> { ["unread"] = note });
                return rows;
            }
            var frame = t as Frame;
            var enrouteHost = t as IHaulEnroute;
            for (int i = 0; i < costs.Count; i++)
            {
                if (!(costs[i] is Dictionary<string, object> c)) continue;
                var td = DefDatabase<ThingDef>.GetNamedSilentFail(c["def"] as string);
                if (td == null) continue;
                int need = c["count"] is int n ? n : 0;
                int present = 0;
                if (frame != null)
                    try { present = frame.resourceContainer.TotalStackCountOfDef(td); }
                    catch { }
                int enroute = 0;
                if (enrouteHost != null)
                    try { enroute = map.enrouteManager.GetEnroute(enrouteHost, td); }
                    catch { }
                int shortfall = Math.Max(0, need - present);
                rows.Add(new Dictionary<string, object>
                {
                    ["def"] = td.defName,
                    ["needed"] = need,
                    ["present"] = present,
                    ["enroute"] = enroute,
                    // Clamped HERE rather than by asking
                    // Frame.ThingCountNeededWithEnroute, whose two Log.Error
                    // branches are exactly this arithmetic going out of range.
                    ["still_wanted"] = Math.Max(0, shortfall - enroute),
                });
                if (shortfall > 0)
                {
                    missingKinds++;
                    missing.Add(new Dictionary<string, object>
                    {
                        ["def"] = td.defName,
                        ["count"] = shortfall,
                    });
                }
            }
            return rows;
        }

        // thingIDNumber -> the pawn whose CURRENT job names it.
        //
        // ONE PASS over the pawn list rather than one per item, because the item
        // loop is capped and the pawn list is not. `AllPawnsSpawned` returns the
        // real `pawnsSpawned` list and is safe to iterate (DigestVerb's hazard
        // note: it is `FreeColonistsSpawned` that rebuilds a cached list on every
        // access).
        //
        // BOTH targets are indexed, deliberately. `JobDriver_ConstructFinishFrame`
        // puts the frame in TargetA; `WorkGiver_ConstructDeliverResources` builds
        // a HaulToContainer whose TargetA is the RESOURCE and whose TargetB is the
        // constructible. Indexing only A would report a fully-staffed delivery as
        // nobody working, which is the "fully stocked and waiting" vs "somebody is
        // bringing it" distinction this verb exists for. `job` is published so the
        // agent can tell which of the two it is looking at.
        private static Dictionary<int, Pawn> WorkerIndex(Map map)
        {
            var index = new Dictionary<int, Pawn>();
            try
            {
                var pawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    var p = pawns[i];
                    Job job = null;
                    try { job = p?.CurJob; }
                    catch { }
                    if (job == null) continue;
                    Note(index, job.targetA, p);
                    Note(index, job.targetB, p);
                }
            }
            catch { }
            return index;
        }

        private static void Note(Dictionary<int, Pawn> index, LocalTargetInfo target, Pawn p)
        {
            Thing t = null;
            try { t = target.Thing; }
            catch { }
            if (t == null || !(t is Blueprint || t is Frame)) return;
            if (!index.ContainsKey(t.thingIDNumber)) index[t.thingIDNumber] = p;
        }

        private static Pawn Worker(Dictionary<int, Pawn> index, Thing t)
            => index.TryGetValue(t.thingIDNumber, out var p) ? p : null;

        // `GenConstruct.FirstBlockingThing`, the member both construction work
        // givers call, WITH the worker excluded — see hazard 4. Read-only:
        // OccupiedRect, GetThingList and def flags.
        private static Thing Blocking(Thing t, Pawn worker)
        {
            try { return GenConstruct.FirstBlockingThing(t, worker); }
            catch { return null; }
        }

        private static float WorkTotal(BuildableDef built, ThingDef stuff)
        {
            if (built == null) return 0f;
            try { return built.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff); }
            catch { return 0f; }
        }

        private static float WorkLeft(Frame f)
        {
            try { return Math.Max(0f, WorkTotal(f.def.entityDefToBuild, f.Stuff) - f.workDone); }
            catch { return 0f; }
        }

        private static int Stored(Map map, ThingDef def)
        {
            try { return map.resourceCounter.GetCount(def); }
            catch { return 0; }
        }

        private static string SafeJobDef(Pawn p)
        {
            try { return p.CurJob?.def?.defName; }
            catch { return null; }
        }
    }
}
