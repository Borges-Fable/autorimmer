using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // How a blocker CLEARS, not merely that it blocks (spec 2.6; DESIGN
    // decisions log 2026-08-30). A bare `buildable:false` cannot be acted on:
    // some obstacles are mined, some deconstructed, and some have to be beaten
    // down by a drafted colonist. This is the concrete form of the standing
    // "candidates + reasons, never bare booleans" invariant, and 3.2 (mine /
    // deconstruct), 3.3 (place-layout preflight) and 3.4 (drafted attack)
    // consume the field.
    //
    // The taxonomy is NOT invented here — the game already reifies it, and this
    // file only serializes it. Verified against the decompiled 1.6 tree at
    // rimworld-tools/Info/decompiled/RimWorldBase:
    //
    //   Verse/Building.cs:488   DeconstructibleBy(Faction) -> AcceptanceReport
    //                           (comps' CompForceDeconstructable, then
    //                           def.building.IsDeconstructible, godMode, own
    //                           faction, alwaysDeconstructible, ClaimableBy).
    //   Verse/ThingDef.cs:1303  IsNonDeconstructibleAttackableBuilding =
    //                           IsBuildingArtificial && !IsDeconstructible &&
    //                           destroyable && !mineable && isTargetable &&
    //                           draftAttackNonDeconstructable.
    //   RimWorld/Designator_Deconstruct.cs:94-99 answers exactly that case with
    //                           the literal "RemoveByAttackingTooltip"
    //                           ("Remove this by attacking it." — the key is in
    //                           Core/Languages/English/Keyed/Misc_Gameplay.xml).
    //   RimWorld/BuildingProperties.cs:436 IsDeconstructible is FALSE for
    //                           natural rock, which is why rock lands on `mine`
    //                           rather than on `none`.
    //
    // Order is DESIGN's order — mine, deconstruct, attack, none — not
    // Designator_Deconstruct's, because a mineable rock wall is rejected by
    // DeconstructibleBy with an EMPTY reason and the player's actual verb for
    // it is "mine".
    //
    // OBSERVER DISCIPLINE: every call here is read-only. DeconstructibleBy
    // walks AllComps (a plain list field on ThingWithComps) and def flags;
    // ClaimableBy does the same. No lazy-init getter, no cached list rebuilt on
    // read — this is the same path Designator_Deconstruct runs under the mouse
    // cursor every frame. One caveat worth knowing: DeconstructibleBy returns
    // accepted for EVERYTHING while DebugSettings.godMode is on (Building.cs:
    // 501). godMode is off on the bench and is not Prefs.DevMode; if a session
    // ever turns it on, `removal` reads optimistically.
    public static class Blockers
    {
        public const string Mine = "mine";
        public const string Deconstruct = "deconstruct";
        public const string Attack = "attack";
        public const string None = "none";

        // Fog is a cell-level rejection rather than a thing-level one: nothing
        // is in the way, the colony has simply never been there.
        public const string FoggedReason = "unexplored";

        // (removal, reason) for one thing, in the game's own words. `reason` is
        // null when the game has no string to give — never a phrase of ours
        // dressed up as the game's.
        public static void Classify(Thing t, out string removal, out string reason)
        {
            removal = None;
            reason = null;
            if (t == null) return;

            if (t.def.mineable)
            {
                removal = Mine;
                return;
            }

            var building = t.GetInnerIfMinified() as Building;
            if (building == null)
            {
                // Not a building: an item, a plant, a pawn. Nothing to clear by
                // designation; the caller's own reason string stands.
                return;
            }

            AcceptanceReport report;
            try { report = building.DeconstructibleBy(Faction.OfPlayer); }
            catch { return; }

            if (report.Accepted)
            {
                removal = Deconstruct;
                return;
            }
            if (t.def.IsNonDeconstructibleAttackableBuilding)
            {
                removal = Attack;
                reason = Translate("RemoveByAttackingTooltip");
                return;
            }
            removal = None;
            reason = string.IsNullOrEmpty(report.Reason) ? null : report.Reason;
        }

        // {def,label,at,removal,reason} for a blocking thing — the shape 3.2,
        // 3.3 and 3.4 read. Null-safe so callers can inline it.
        public static Dictionary<string, object> Describe(Thing t)
        {
            if (t == null) return null;
            Classify(t, out string removal, out string reason);
            var d = new Dictionary<string, object>
            {
                ["def"] = t.def.defName,
                ["label"] = t.def.label,
                ["at"] = Positions.Out(t.PositionHeld),
                ["removal"] = removal,
                ["reason"] = reason,
            };
            return d;
        }

        // The obstacle AT A CELL, for a caller that knows only that the cell
        // refused and not what is standing on it.
        //
        // `IntVec3.GetFirstBuilding` is NOT enough, and that was M1 finding A
        // (2026-09-01): it consults Verse/EdificeGrid via
        // Verse/GridsUtility.cs GetEdifice and so sees the edifice ONLY, so a
        // cell that refused for its terrain, for a non-building thing, or for
        // being out of bounds described its blocker as `null` — and the journal
        // row, which is the only durable record of the refusal, then carried no
        // obstacle at all.
        //
        // Order is "what a player would point at": the edifice that owns the
        // cell (wall, rock, building), else the first impassable thing, else
        // whatever is there. `reason` is why the CELL refused — the caller's
        // knowledge — and is published as `refused` so it never overwrites
        // Classify's `reason`, which is how the THING clears.
        //
        // OBSERVER DISCIPLINE: GetEdifice is a grid index and GetThingList
        // returns Map.thingGrid's stored list; neither is lazy and neither
        // rebuilds on read.
        public static Dictionary<string, object> At(Map map, IntVec3 c, string reason)
        {
            Thing t = null;
            try
            {
                if (map != null && c.InBounds(map))
                {
                    t = c.GetEdifice(map);
                    if (t == null)
                        foreach (var x in c.GetThingList(map))
                        {
                            if (t == null) t = x;
                            if (x.def != null && x.def.passability == Traversability.Impassable) { t = x; break; }
                        }
                }
            }
            catch { }
            var d = t == null ? Cell(c, reason) : Describe(t);
            d["refused"] = reason;
            return d;
        }

        // A rejection that is not a thing (fog, terrain, roof, reachability):
        // still carries the pair, so a consumer never has to special-case the
        // absence of the field.
        public static Dictionary<string, object> Cell(IntVec3 c, string reason)
            => new Dictionary<string, object>
            {
                ["at"] = Positions.Out(c),
                ["removal"] = None,
                ["reason"] = reason,
            };

        private static string Translate(string key)
        {
            try { return key.Translate().ToString(); }
            catch { return key; }
        }
    }
}
