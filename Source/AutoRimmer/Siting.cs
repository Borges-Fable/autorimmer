using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ============================================================ siting ====
    // The geometry every placement verb shares: which way a thing faces, and
    // the map between the rect an agent reasons about and the cell the game
    // wants to be handed.
    //
    // Both halves exist because of one bench failure (20260901T121508, git-bug
    // c718e4a). `find-rect {w:3,h:2}` approved a box; the spawn that followed
    // landed one cell west of it, because `find-rect`'s `at` is a rect CORNER
    // and every placement verb takes a CENTRE — and for an even-sized rect the
    // game has TWO centres that disagree by one cell. Nothing the caller could
    // pass reproduced the approved rect.

    // Rot4 from an agent argument.
    //
    // NOT `Verse/Rot4.FromString`: it `Log.Error`s on anything it does not
    // recognise, and a red error raised by agent-supplied arguments breaks the
    // standing zero-red-errors invariant — the same trap
    // `ListerThings.ThingsOfDef` sets for MinifiedThing (see
    // SpatialVerbs.Nearest). It is also case-sensitive, and lower case is the
    // obvious thing for a program to send.
    //
    // The vocabulary is the one `map-dump` already publishes: `Rot4`'s four
    // words as `ToStringWord` writes them, plus the bare 0..3 the same value
    // serializes as. A rotation token therefore round-trips between a dump, a
    // template and a verb argument without translation — templates/INDEX.md's
    // session-14 pin, "a rotation suffix is the Rot4 value verbatim, not a
    // description of which way a thing faces".
    public static class Rotations
    {
        public static Rot4 Arg(VerbArgs args, string key, Rot4 fallback)
        {
            object raw = args?.Raw(key);
            if (raw == null) return fallback;
            if (raw is double d)
            {
                if (d != Math.Floor(d) || d < 0 || d > 3) throw Bad(key);
                return new Rot4((int)d);
            }
            if (raw is string s)
            {
                switch (s.Trim().ToLowerInvariant())
                {
                    case "north": case "0": return Rot4.North;
                    case "east": case "1": return Rot4.East;
                    case "south": case "2": return Rot4.South;
                    case "west": case "3": return Rot4.West;
                }
            }
            throw Bad(key);
        }

        private static VerbArgsException Bad(string key)
            => new VerbArgsException(
                $"arg '{key}' must be North|East|South|West (any case) or 0..3 — the Rot4 "
                + "value map-dump publishes, not a description of which way the thing faces");

        // The four, in the game's own order, for a caller that searches them
        // all. `def.defaultPlacingRot` first is the caller's business.
        public static readonly Rot4[] All = { Rot4.North, Rot4.East, Rot4.South, Rot4.West };
    }

    // Corner <-> centre, and the rect a def occupies.
    //
    // FORWARD is the game's: `GenAdj.OccupiedRect(centre, rot, size)`, called
    // directly wherever a rect is wanted. Nothing here re-implements it.
    //
    // INVERSE is ours, because the game does not provide one — and it is
    // ROTATION-DEPENDENT, which is why a rect corner is the only stable
    // identity a candidate site can have. `Verse/GenAdj.AdjustForRotation`
    // swaps the def's two axes when `rot.IsHorizontal`, then shifts the centre
    // by a per-rotation offset **applied to each axis only when that axis's
    // (post-swap) size is even**. So one centre yields three different rects
    // for a 5x2 def: North `[C.x-2, C.z, 5, 2]`, South `[C.x-2, C.z-1, 5, 2]`,
    // East `[C.x, C.z-2, 2, 5]`.
    //
    // And `CellRect.CenterCell` (`minX + Width/2`) is NOT that centre: it is
    // `minX + w/2` where `OccupiedRect` uses `centre - (w-1)/2`, so the two
    // disagree by exactly one cell on every even axis. `CenterCell` keeps its
    // one legitimate role — ranking candidates by distance, where a constant
    // offset cannot change the order — and is never the value a caller passes
    // as `pos`.
    public static class Footprint
    {
        // The def's size with the axis swap `AdjustForRotation` performs, and
        // nothing else. This is the w/h of the occupied rect.
        public static IntVec2 RotatedSize(IntVec2 size, Rot4 rot)
            => rot.IsHorizontal ? new IntVec2(size.z, size.x) : size;

        // `AdjustForRotation`'s offset table for reference `Rot4.North`, with
        // its per-axis even-size gate already applied. `rotated` is the size
        // AFTER the swap, which is the order vanilla tests it in.
        //
        // Vanilla returns early for a 1x1 def; no special case is needed here,
        // because 1 is odd on both axes and the gate drops both shifts anyway.
        private static IntVec3 Shift(Rot4 rot, IntVec2 rotated)
        {
            int dx = 0, dz = 0;
            switch (rot.AsInt)
            {
                case 1: dz = -1; break;             // East
                case 2: dx = -1; dz = -1; break;    // South
                case 3: dx = -1; break;             // West
                default: break;                     // North — no shift
            }
            return new IntVec3(rotated.x % 2 == 0 ? dx : 0, 0, rotated.z % 2 == 0 ? dz : 0);
        }

        // The centre to hand a placement verb so that the def lands with its
        // rect's south-west corner exactly on `corner`.
        //
        // VERIFIED ON EVERY CALL against the game's own forward map rather than
        // trusted: `false` means the round trip did not close, which can only
        // happen if `AdjustForRotation`'s table moves under us in a future
        // version. A refusal is the right answer there — a silently displaced
        // building is what this whole file exists to prevent, and on a module
        // grid a one-cell slide is cumulative (git-bug bac4eba).
        public static bool TryCentreFor(IntVec2 size, IntVec3 corner, Rot4 rot, out IntVec3 centre)
        {
            var r = RotatedSize(size, rot);
            var s = Shift(rot, r);
            centre = new IntVec3(corner.x + (r.x - 1) / 2 - s.x, 0, corner.z + (r.z - 1) / 2 - s.z);
            var back = GenAdj.OccupiedRect(centre, rot, size);
            return back.minX == corner.x && back.minZ == corner.z
                && back.Width == r.x && back.Height == r.z;
        }

        // `[x, z, w, h]` — the shape every siting read publishes a footprint
        // in, and the one `find-rect` already uses for `at` plus `w`/`h`.
        public static List<object> Out(CellRect rect)
            => new List<object>
            {
                (double)rect.minX, (double)rect.minZ, (double)rect.Width, (double)rect.Height,
            };

        // `{at, w, h}` — the same rect as a named block, for a result that also
        // carries a `pos` and must not invite the two to be confused.
        public static Dictionary<string, object> Block(CellRect rect)
            => new Dictionary<string, object>
            {
                ["at"] = Positions.Out(new IntVec3(rect.minX, 0, rect.minZ)),
                ["w"] = rect.Width,
                ["h"] = rect.Height,
            };
    }

    // ============================================================ SiteGate ==
    // THE ONE PLACEMENT GATE. `site-survey`, `find-rect {def}`,
    // `dev:spawn-thing {buildable:true}`, `site-audit` and 3.3's `build` /
    // `place-layout` preflight all ask THIS, and none of them re-implements it
    // — the acceptance on git-bug c718e4a is that a survey's verdict and a
    // build verb's refusal for the same arguments are the same sentence, and
    // one routine is the only way that is true rather than nearly true.
    //
    // It is TWO gates, deliberately kept apart in the answer, because they are
    // two different pieces of news for an agent (git-bug 1adc737 #3, DESIGN
    // §Action model):
    //
    //   `verdict`    — RimWorld/GenConstruct.CanPlaceBlueprintAt(godMode:false).
    //                  "The ground refuses this." Actionable: mine the rock,
    //                  haul the log, turn the building, site it elsewhere.
    //
    //   `selectable` — RimWorld/Designator_Build.Visible. "This is not on the
    //                  architect menu at all." Not actionable by moving: the
    //                  research is not done, the difficulty forbids it, a
    //                  prerequisite building does not exist.
    //
    // Collapsing them into one boolean is what makes a god-hand look correct:
    // `CanPlaceBlueprintAt` performs NO research check — an unpiped grep for
    // Research|TechLevel|techLevel over the whole of Verse/GenConstruct.cs
    // returns nothing — so a verb that consults only the placement gate will
    // cheerfully blueprint an unresearched building and report success.
    public static class SiteGate
    {
        // Bump on any change to what the two gates test or to the clause
        // vocabulary below, the same discipline CropRenderer.AlphabetId
        // documents: a consumer holding a stale idea of the gate must be able
        // to DETECT that rather than silently misread a verdict.
        public const string GateId = "site-gate/1";

        // Designator_Build.Visible's clauses, as tokens. A token because an
        // agent branches on it and a sentence is not a branch; the human half
        // rides alongside in `detail`. Read by field, never through a
        // description (DESIGN, 2026-09-01).
        public const string ClauseMinTech = "min-tech";
        public const string ClauseMaxTech = "max-tech";
        public const string ClauseResearch = "research";
        public const string ClauseResearchUnreadable = "research-unreadable";
        public const string ClauseMonolith = "monolith";
        public const string ClauseDifficulty = "difficulty";
        public const string ClausePlaceWorker = "place-worker";
        public const string ClauseBuildingPrereq = "building-prerequisite";
        public const string ClauseDiscoveryPrereq = "discovery-prerequisite";
        public const string ClauseGravEngine = "grav-engine";

        // Both gates for one (def, pos, rot, stuff), and the footprint they
        // were asked about. Never throws: a third-party PlaceWorker is
        // arbitrary code and a refusal that says "a PlaceWorker threw" is worth
        // more than an exception envelope with no verdict in it.
        public static SiteVerdict Check(Map map, BuildableDef def, IntVec3 pos, Rot4 rot, ThingDef stuff)
        {
            var v = new SiteVerdict
            {
                Def = def,
                Stuff = stuff,
                Pos = pos,
                Rot = rot,
                Rect = GenAdj.OccupiedRect(pos, rot, def.Size),
            };
            try
            {
                // godMode:false is the whole point — it is what turns off
                // CanPlaceBlueprintAt's own edge-area and occupancy bypasses.
                // stuffDef is passed because GetTerrainAffordanceNeed reads it
                // for a useStuffTerrainAffordance def: a stone wall needs a
                // different affordance from a wooden one, and GenSpawn's own
                // CanSpawnAt does NOT pass it (a vanilla quirk noted in
                // DevVerbs.WhyNoSpawn — this gate is the blueprint path and
                // does pass it).
                var report = GenConstruct.CanPlaceBlueprintAt(def, pos, rot, map,
                    godMode: false, stuffDef: stuff);
                v.PlaceOk = report.Accepted;
                v.PlaceReason = report.Accepted ? null
                    : (string.IsNullOrEmpty(report.Reason)
                        ? def.defName + " cannot be placed here (the game gave no reason)"
                        : report.Reason);
            }
            catch (Exception e)
            {
                v.PlaceOk = false;
                v.PlaceReason = "GenConstruct.CanPlaceBlueprintAt threw " + e.GetType().Name
                    + " — most likely a mod PlaceWorker; treat the site as refused";
            }
            v.Selectable = Selectable(map, def, out string clause, out string detail);
            v.SelectableClause = clause;
            v.SelectableDetail = detail;
            // Published, NOT honoured. Designator_Build.Visible's first clause
            // is `if (DebugSettings.godMode) return true;`, and reproducing that
            // would turn every player verb into a god-hand the moment a dev
            // session left the flag on — silently, and with `ok:true` to show
            // for it. The clauses are evaluated regardless and `ok` is their
            // answer; a caller that wants the bypass asks a `dev:*` verb, which
            // says so in its own envelope.
            try { v.GodMode = DebugSettings.godMode; } catch { }
            return v;
        }

        // Designator_Build.Visible, clause for clause, in the game's order.
        //
        // AMENDMENT #3 ON 1adc737 NAMED TWO CLAUSES AND ITS OWN VERIFICATION
        // COMMENT NAMED SIX. The 1.6 member has TEN, and the four nobody has
        // written down are `buildingPrerequisites` (via
        // ListerBuildings.ColonistsHaveBuilding), `discoveryPrerequisites` (via
        // HiddenItemsManager.Hidden), `requireInspectedGravEngine` (Odyssey) and
        // the godMode bypass. A verb reproducing only the research clause is
        // still a god-hand for scenario-, difficulty- and prerequisite-
        // restricted defs, which is the argument the amendment was making.
        //
        // OBSERVER DISCIPLINE, member by member: Difficulty.AllowedToBuild reads
        // three bools and def.building flags; ListerBuildings.ColonistsHaveBuilding
        // walks a stored list; HiddenItemsManager.Hidden is a TryGetValue;
        // GameComponent_Anomaly.HighestLevelReached is a field and
        // GenerateMonolith reads the playstyle def; ResearchManager
        // .gravEngineInspected is a bool field. None writes.
        //
        // THE ONE THAT DOES WRITE is the research clause, and it is the reason
        // this routine exists rather than a call to the vanilla property.
        // Verse/BuildableDef.IsResearchFinished loops
        // researchPrerequisites[i].IsFinished; ResearchProjectDef.IsFinished is
        // ProgressReal >= Cost; ProgressReal is
        // Find.ResearchManager.GetProgress(this); and GetProgress inserts a zero
        // entry for a project it has never seen into `progress`, which is
        // Scribe_Collections-scribed. So READING the vanilla gate edits the
        // save. WorldSafe.Finished is that property without the insert
        // (WorldSafe.cs documents it at length); IsResearchFinished must never
        // be called from anywhere in this mod.
        public static bool Selectable(Map map, BuildableDef def, out string clause, out string detail)
        {
            clause = null;
            detail = null;
            if (def == null) { clause = ClauseDifficulty; detail = "no def"; return false; }

            try
            {
                var playerTech = Faction.OfPlayer.def.techLevel;
                if (def.minTechLevelToBuild != TechLevel.Undefined
                    && (int)playerTech < (int)def.minTechLevelToBuild)
                {
                    clause = ClauseMinTech;
                    detail = def.defName + " needs tech level " + def.minTechLevelToBuild
                        + "; the player faction is " + playerTech;
                    return false;
                }
                if (def.maxTechLevelToBuild != TechLevel.Undefined
                    && (int)playerTech > (int)def.maxTechLevelToBuild)
                {
                    clause = ClauseMaxTech;
                    detail = def.defName + " is only buildable up to tech level "
                        + def.maxTechLevelToBuild + "; the player faction is " + playerTech;
                    return false;
                }
            }
            catch { }

            if (def.researchPrerequisites != null && def.researchPrerequisites.Count > 0)
            {
                // A refusal rather than a shrug when the guarded route is not
                // available: WorldSafe.Finished would report every project
                // unfinished if the field ref failed, so "researched" and
                // "we could not look" must not read alike (PawnSafe.Policies's
                // `source` discipline). It has never failed on a bench; if it
                // does, this is loud instead of wrong.
                if (!WorldSafe.ResearchRefsOk)
                {
                    clause = ClauseResearchUnreadable;
                    detail = "ResearchManager.progress could not be read through the guarded "
                        + "route, and the vanilla accessor inserts into scribed state, so "
                        + "whether " + def.defName + " is researched cannot be answered";
                    return false;
                }
                for (int i = 0; i < def.researchPrerequisites.Count; i++)
                {
                    var proj = def.researchPrerequisites[i];
                    if (proj != null && !WorldSafe.Finished(proj))
                    {
                        clause = ClauseResearch;
                        detail = def.defName + " needs research '" + proj.defName + "'";
                        return false;
                    }
                }
            }

            try
            {
                if (ModsConfig.AnomalyActive && def.minMonolithLevel > 0
                    && Find.Anomaly != null
                    && def.minMonolithLevel > Find.Anomaly.HighestLevelReached
                    && Find.Anomaly.GenerateMonolith)
                {
                    clause = ClauseMonolith;
                    detail = def.defName + " needs monolith level " + def.minMonolithLevel;
                    return false;
                }
            }
            catch { }

            try
            {
                // Difficulty.AllowedToBuild dereferences thingDef.building
                // without a null check, so a buildable ThingDef with no
                // building block would throw inside the game's own member —
                // hence the try, not a defensive rewrite of its logic.
                if (!Find.Storyteller.difficulty.AllowedToBuild(def))
                {
                    clause = ClauseDifficulty;
                    detail = "the storyteller difficulty forbids building " + def.defName
                        + " (traps, turrets or mortars are switched off)";
                    return false;
                }
            }
            catch { }

            // PlaceWorkers is a LAZY-INIT GETTER: Verse/BuildableDef.PlaceWorkers
            // instantiates the def's PlaceWorker list on first read and caches it
            // in placeWorkersInstantiatedInt. Accepted, and the same ruling as
            // the game's own first hover over the architect menu — it is
            // def-level, never scribed, and the instances are stateless
            // strategy objects. What it is NOT is free, and it is not idempotent
            // in TIME: whoever reads it first pays for the allocation.
            //
            // AllowsPlacing and IsBuildDesignatorVisible are third-party code
            // for a modded def, so each call is wrapped the way
            // DevVerbs.WhyNoSpawn wraps its own — a mod PlaceWorker throwing
            // must not take the verb down.
            try
            {
                var workers = def.PlaceWorkers;
                if (workers != null)
                    for (int i = 0; i < workers.Count; i++)
                    {
                        bool visible = true;
                        try { visible = workers[i].IsBuildDesignatorVisible(def); }
                        catch { }
                        if (!visible)
                        {
                            clause = ClausePlaceWorker;
                            detail = workers[i].GetType().Name + ".IsBuildDesignatorVisible hides "
                                + def.defName;
                            return false;
                        }
                    }
            }
            catch { }

            try
            {
                if (def.buildingPrerequisites != null && map != null)
                    for (int i = 0; i < def.buildingPrerequisites.Count; i++)
                        if (!map.listerBuildings.ColonistsHaveBuilding(def.buildingPrerequisites[i]))
                        {
                            clause = ClauseBuildingPrereq;
                            detail = def.defName + " needs a built "
                                + def.buildingPrerequisites[i].defName + " on this map";
                            return false;
                        }
                if (def.discoveryPrerequisites != null && Find.HiddenItemsManager != null)
                    for (int i = 0; i < def.discoveryPrerequisites.Count; i++)
                        if (Find.HiddenItemsManager.Hidden(def.discoveryPrerequisites[i]))
                        {
                            clause = ClauseDiscoveryPrereq;
                            detail = def.defName + " is hidden until "
                                + def.discoveryPrerequisites[i].defName + " has been discovered";
                            return false;
                        }
                if (ModsConfig.OdysseyActive && def.requireInspectedGravEngine
                    && Find.ResearchManager != null && !Find.ResearchManager.gravEngineInspected)
                {
                    clause = ClauseGravEngine;
                    detail = def.defName + " needs an inspected gravship engine";
                    return false;
                }
            }
            catch { }

            return true;
        }

        // A BuildableDef by name: a ThingDef first, then a TerrainDef, because
        // "Wall" and "Flagstone_Granite" are both things an agent asks to
        // build and CanPlaceBlueprintAt takes either.
        public static BuildableDef Named(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new VerbArgsException("missing required arg 'def'");
            BuildableDef d = DefDatabase<ThingDef>.GetNamedSilentFail(name);
            if (d == null) d = DefDatabase<TerrainDef>.GetNamedSilentFail(name);
            if (d == null)
                throw new VerbArgsException($"no ThingDef or TerrainDef named '{name}'");
            return d;
        }
    }

    // What SiteGate answers with, and the shape every consumer publishes.
    public sealed class SiteVerdict
    {
        public BuildableDef Def;
        public ThingDef Stuff;
        public IntVec3 Pos;
        public Rot4 Rot;
        public CellRect Rect;

        public bool PlaceOk;
        public string PlaceReason;

        public bool Selectable;
        public string SelectableClause;
        public string SelectableDetail;

        public bool GodMode;

        // BOTH gates, because a site is usable only when the ground accepts it
        // AND the def is on the menu.
        public bool Ok => PlaceOk && Selectable;

        // `verdict` and `selectable` are separate blocks on purpose: "would be
        // refused" and "cannot even be selected" must never read alike.
        public Dictionary<string, object> Out()
        {
            var d = new Dictionary<string, object>
            {
                ["ok"] = Ok,
                ["gate"] = SiteGate.GateId,
                ["def"] = Def?.defName,
                ["stuff"] = Stuff?.defName,
                ["pos"] = Positions.Out(Pos),
                ["rot"] = Rot.ToStringWord(),
                ["footprint"] = Footprint.Block(Rect),
                ["verdict"] = new Dictionary<string, object>
                {
                    ["ok"] = PlaceOk,
                    ["source"] = "GenConstruct.CanPlaceBlueprintAt(godMode:false)",
                    ["reason"] = PlaceReason,
                },
                ["selectable"] = new Dictionary<string, object>
                {
                    ["ok"] = Selectable,
                    ["source"] = "Designator_Build.Visible",
                    ["clause"] = SelectableClause,
                    ["detail"] = SelectableDetail,
                },
            };
            // Presence is the signal: the flag appears only when it is on, and
            // it is never acted on — see SiteGate.Check.
            if (GodMode) d["god_mode_on"] = true;
            return d;
        }
    }
}
