#!/usr/bin/env python3
"""Acceptance runner for 2a7c064 — baseviz labels a building on its TRUE centre.

**No bench, no game, no protocol.** This is pure geometry over
`baseviz.render.occupied_rect` and the label anchor it feeds, so it runs
anywhere python does and costs nothing:

    ./accept/2a7c064-label-centre.py
    ./accept/2a7c064-label-centre.py -v      # also print the old formula's answer

WHAT THIS IS TESTING, in one sentence: `map-dump` publishes `at =
Thing.Position`, which is a CENTRE, and the renderer used to treat it as a
corner on one axis and over-correct on the other, so a label could land on the
wrong cell in either direction depending on which parity you happened to look at.

THE THREE DEFECTS, all in `render.py`'s `_label_pass`, all closed here:

  1. `cx = x + (sw*scale)//2` added HALF THE WIDTH to a position that was
     already central: one cell east for an odd width >= 3. This is what the
     issue was filed about, against a 3x1 `ElectricStove`.
  2. `cy = y + scale//2 - ((sh-1)*scale)//2` subtracted a correction that was
     not owed: one cell NORTH for an odd height >= 3, the opposite direction and
     the same magnitude. Invisible for four months because the specimen's height
     was 1.
  3. The rect itself was wrong for even sizes under rotation. The renderer
     swapped `sw`/`sh` for East/West — half of `Verse/GenAdj.AdjustForRotation`
     — and dropped the per-axis centre shift the other half applies when that
     axis's size is EVEN. No label arithmetic can correct a wrong rect.

WHY IT IS WORTH A RUNNER AT ALL. 2.5's PNG channel exists to be an INDEPENDENT
second read of the same ground (f7b6207). A reader asked "which cells is the
stove on?" grades the image against the published centre — so a labelling offset
and a real placement error are indistinguishable from the picture, which is
exactly the confusion the channel exists to remove. `baseviz/` has no other
tests; this is the first.

Exit 0 iff every check passes.
"""

import argparse
import os
import sys

# Run from anywhere: this file lives in accept/, `baseviz` is its sibling.
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from baseviz.render import occupied_rect

# A render grid big enough that nothing clamps. The label glyph is deliberately
# tiny so the box's own size never decides which cell its centre lands in.
SCALE, OX, OZ, W, H, GX0, GY0 = 20, 0, 0, 40, 40, 0, 0
TW = TH = 6
AT = (20, 20)

GREEN, RED, YELLOW, OFF = "\033[32m", "\033[31m", "\033[33m", "\033[0m"
PASS = FAIL = 0


def px(cx, cz):
    """The renderer's own cell -> top-left-pixel map. Kept in sync by eye; if
    `render.px` changes, this changes with it and the failure is loud."""
    return GX0 + (cx - OX) * SCALE, GY0 + (OZ + H - 1 - cz) * SCALE


def cell_of(x, y):
    return OX + x // SCALE, OZ + H - 1 - y // SCALE


def anchor(gxc, gzc, sw, sh, rot):
    """What `_label_pass` computes today, minus the clamp and the collide test."""
    mnx, mnz, sw, sh = occupied_rect(gxc, gzc, sw, sh, rot)
    x, y = px(mnx, mnz + sh - 1)
    return x + (sw * SCALE) // 2 - TW // 2, y + (sh * SCALE) // 2 - TH // 2


def old_anchor(gxc, gzc, sw, sh, rot):
    """The formula this issue replaced, kept so the checks have teeth: a test
    that cannot fail against the bug it was written for proves nothing."""
    if rot in ("East", "West"):
        sw, sh = sh, sw
    x, y = px(gxc, gzc)
    return (x + (sw * SCALE) // 2 - TW // 2,
            y + SCALE // 2 - ((sh - 1) * SCALE) // 2 - TH // 2)


def centre_cell(a):
    return cell_of(a[0] + TW // 2, a[1] + TH // 2)


def check(num, what, ok, detail=""):
    global PASS, FAIL
    if ok:
        PASS += 1
        print("  %s%-7s PASS%s    %s" % (GREEN, num, OFF, what))
    else:
        FAIL += 1
        print("  %s%-7s FAIL%s    %s%s" % (RED, num, OFF, what,
                                           ("\n            " + detail) if detail else ""))


def banner(t):
    print("\n%s%s%s" % (YELLOW, t, OFF))


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("-v", "--verbose", action="store_true",
                    help="also print what the pre-fix formula answered")
    args = ap.parse_args(argv)

    # ---- 1. the port itself, against the game's own worked example ---------
    # c718e4a states these three rects for a 5x2 HiTechResearchBench about one
    # centre. They are the reason `occupied_rect` exists; if the port is wrong
    # every check below is measuring the wrong thing.
    banner("PHASE 1 — occupied_rect is GenAdj.OccupiedRect (c718e4a's worked example)")
    C = (100, 100)
    for num, rot, want in (("1.1", "North", (98, 100, 5, 2)),
                           ("1.2", "South", (98, 99, 5, 2)),
                           ("1.3", "East", (100, 98, 2, 5))):
        got = occupied_rect(C[0], C[1], 5, 2, rot)
        check(num, "5x2 %-5s -> %s" % (rot, want), got == want, "got %s" % (got,))

    # AdjustForRotation returns early for 1x1, so rotation cannot move a
    # single-cell thing. Worth pinning: it is the one branch with no arithmetic.
    check("1.4", "a 1x1 thing is rotation-invariant",
          all(occupied_rect(7, 9, 1, 1, r) == (7, 9, 1, 1)
              for r in ("North", "East", "South", "West")))

    # The centre must lie inside its own rect for every size and rotation, and
    # rotation must never change the cell COUNT. Cheap, and it is the invariant
    # that would have caught defect 3 without anyone thinking about parity.
    bad = []
    for sw in range(1, 8):
        for sh in range(1, 8):
            for rot in ("North", "East", "South", "West"):
                mnx, mnz, rw, rh = occupied_rect(50, 50, sw, sh, rot)
                if rw * rh != sw * sh or not (mnx <= 50 < mnx + rw and mnz <= 50 < mnz + rh):
                    bad.append((sw, sh, rot, (mnx, mnz, rw, rh)))
    check("1.5", "centre is inside its rect and area is preserved, 7x7x4 combinations",
          not bad, "first failures: %r" % (bad[:3],))

    check("1.6", "an unknown or absent rot degrades to North rather than raising",
          occupied_rect(10, 10, 3, 1, None) == occupied_rect(10, 10, 3, 1, "North"))

    # ---- 2. odd sizes: the label lands on Thing.Position -------------------
    banner("PHASE 2 — an ODD-sized building's code is on its true centre cell")
    odd = (("2.1", 3, 1, "North", "the filed specimen: 3x1 ElectricStove"),
           ("2.2", 1, 3, "North", "odd HEIGHT — the half the issue did not name"),
           ("2.3", 3, 3, "North", "both axes odd"),
           ("2.4", 5, 1, "North", "wider: the error grew with the size"),
           ("2.5", 1, 5, "East", "odd, rotated"))
    for num, sw, sh, rot, why in odd:
        got = centre_cell(anchor(AT[0], AT[1], sw, sh, rot))
        was = centre_cell(old_anchor(AT[0], AT[1], sw, sh, rot))
        extra = ("   (pre-fix answered %s)" % (was,)) if args.verbose else ""
        check(num, "%dx%d %-5s -> %s  — %s%s" % (sw, sh, rot, AT, why, extra),
              got == AT, "got %s" % (got,))

    # ---- 3. the checks have teeth -----------------------------------------
    banner("PHASE 3 — the pre-fix formula FAILS these, so the checks mean something")
    was_wrong = [(sw, sh, rot) for _, sw, sh, rot, _ in odd
                 if centre_cell(old_anchor(AT[0], AT[1], sw, sh, rot)) != AT]
    check("3.1", "every phase-2 case was wrong before the fix (%d/%d)"
          % (len(was_wrong), len(odd)), len(was_wrong) == len(odd),
          "these already passed pre-fix, so they prove nothing: %r"
          % ([c for c in ((s, h, r) for _, s, h, r, _ in odd) if c not in was_wrong],))

    # Defect 2 specifically: the old z axis erred NORTH while the old x axis
    # erred EAST. Naming the direction is what stops a "fix" that moves both
    # the same way and calls it done.
    o13 = centre_cell(old_anchor(AT[0], AT[1], 1, 3, "North"))
    o31 = centre_cell(old_anchor(AT[0], AT[1], 3, 1, "North"))
    check("3.2", "the two axes erred in OPPOSITE directions (1x3 north, 3x1 east)",
          o13 == (AT[0], AT[1] + 1) and o31 == (AT[0] + 1, AT[1]),
          "1x3 -> %s, 3x1 -> %s" % (o13, o31))

    # ---- 4. even sizes: there is no middle cell ----------------------------
    banner("PHASE 4 — an EVEN-sized building centres on the BOUNDARY, not a cell")
    # The acceptance asks for this to be stated explicitly rather than left to
    # whatever the arithmetic happens to do: with no middle cell the centre is
    # the seam between the two middle ones, i.e. exactly rw/2 cells from the
    # rect's west edge and rh/2 from its north edge.
    for num, sw, sh, rot in (("4.1", 2, 2, "North"), ("4.2", 4, 1, "North"),
                             ("4.3", 1, 4, "North"), ("4.4", 2, 4, "East"),
                             ("4.5", 4, 2, "West")):
        mnx, mnz, rw, rh = occupied_rect(AT[0], AT[1], sw, sh, rot)
        x, y = px(mnx, mnz + rh - 1)
        a = anchor(AT[0], AT[1], sw, sh, rot)
        dx, dy = (a[0] + TW // 2) - x, (a[1] + TH // 2) - y
        check(num, "%dx%d %-5s rect %s, centre %d/%d cells from the NW corner"
              % (sw, sh, rot, (mnx, mnz, rw, rh), dx // SCALE, dy // SCALE),
              dx == rw * SCALE // 2 and dy == rh * SCALE // 2,
              "got (%d,%d)px, wanted (%d,%d)px" % (dx, dy, rw * SCALE // 2, rh * SCALE // 2))

    # ---- 5. defect 3: rotation moves an even-sized rect --------------------
    banner("PHASE 5 — the rotation shift the renderer used to drop")
    # A swap-only implementation gets the SIZE right and the ORIGIN wrong, so
    # comparing extents cannot catch it. Compare origins.
    def swap_only(cx, cz, sw, sh, rot):
        if rot in ("East", "West"):
            sw, sh = sh, sw
        return cx - (sw - 1) // 2, cz - (sh - 1) // 2, sw, sh

    moved = [(sw, sh, rot) for sw in (2, 4) for sh in (2, 4)
             for rot in ("East", "South", "West")
             if swap_only(20, 20, sw, sh, rot) != occupied_rect(20, 20, sw, sh, rot)]
    check("5.1", "a swap-only rect differs from the game's for even sizes (%d cases)"
          % len(moved), len(moved) > 0,
          "if this passes trivially the port has lost AdjustForRotation's shift")
    # ...and does NOT differ where the game applies no shift, which is the
    # other half of the claim: the port must not invent movement either.
    same = all(swap_only(20, 20, sw, sh, rot) == occupied_rect(20, 20, sw, sh, rot)
               for sw in (3, 5) for sh in (3, 5)
               for rot in ("North", "East", "South", "West"))
    check("5.2", "and agrees with it for ODD sizes, where no shift is owed", same)

    print("\n%s%d PASS%s / %s%d FAIL%s"
          % (GREEN, PASS, OFF, RED if FAIL else GREEN, FAIL, OFF))
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
