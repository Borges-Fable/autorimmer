using System;
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
        public static System.Collections.Generic.List<object> Out(CellRect rect)
            => new System.Collections.Generic.List<object>
            {
                (double)rect.minX, (double)rect.minZ, (double)rect.Width, (double)rect.Height,
            };
    }
}
