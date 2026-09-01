#!/usr/bin/env python3
"""Acceptance runner for e6faa51 — the two visual channels declare their
alphabets, and a stale consumer can tell.

No bench: `transcripts/` banks real `map-view` and `map-dump` envelopes, and
this is a check about a FIELD, so banked envelopes are the right input.

    ./accept/e6faa51-channel-alphabet.py

Why it exists at all. The issue closed on evidence — the fix shipped in
`28d52ae` and was never closed — and two of its three bullets were satisfied by
shipped code. The third was not, and the close said so: "a change to the symbol
table changes the identity" was documented as a CONVENTION in a code comment
with nothing enforcing it, and no suite asserted `map-view` published the field
at all. So a regression that dropped it would have gone unnoticed, which is the
trap `accept/`'s own rule exists for ("assert the key exists, never eq(None)").

This is that enforcement. It also closes the loop the close comment opened by
handing the check to a round that had not started yet — a promise in a closed
issue with no owner is the orphan shape session 13 wrote up.
"""

import glob
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GREEN, RED, YELLOW, OFF = "\033[32m", "\033[31m", "\033[33m", "\033[0m"
PASS = FAIL = 0


def check(num, what, ok, detail=""):
    global PASS, FAIL
    if ok:
        PASS += 1
        print("  %s%-6s PASS%s   %s" % (GREEN, num, OFF, what))
    else:
        FAIL += 1
        print("  %s%-6s FAIL%s   %s%s" % (RED, num, OFF, what,
                                          ("\n           " + detail) if detail else ""))


# The envelopes live in `accept/fixtures/` and are TRACKED. The first version of
# this runner globbed `transcripts/`, which is GITIGNORED — so it passed only on
# the machine that wrote it, scored 0/2 in a worker's git worktree (correctly
# reported, and it read like a regression), and would score 0 in a clean clone,
# while `e6faa51` had already been closed on its evidence. A check whose input
# is untracked is not a check. See `accept/fixtures/README.md`.
FIXTURES = {
    "map-view": "map-view-m1-20260831-124.json",
    "map-dump": "map-dump-20260831T230213-006.json",
}


def banked(verb):
    """The tracked envelope for a verb, or a transcripts/ fallback if present."""
    f = os.path.join(ROOT, "accept", "fixtures", FIXTURES[verb])
    if os.path.exists(f):
        d = json.load(open(f))
        if d.get("ok") and isinstance(d.get("data"), dict):
            return f, d["data"]
    # Fallback only, and never the primary route: a live transcript is handy
    # locally and must not be what the check depends on.
    for g in reversed(sorted(glob.glob(os.path.join(
            ROOT, "transcripts", "*", "*-%s" % verb, "result.json")))):
        try:
            d = json.load(open(g))
        except ValueError:
            continue
        if d.get("ok") and isinstance(d.get("data"), dict):
            return g, d["data"]
    return None, None


def main():
    print("%sChannel identity — banked envelopes, no bench%s" % (YELLOW, OFF))
    fv, view = banked("map-view")
    fd, dump = banked("map-dump")
    check("0.1", "a TRACKED map-view envelope exists", view is not None, "none found")
    check("0.2", "a TRACKED map-dump envelope exists", dump is not None, "none found")
    if view is None or dump is None:
        print("\n%s%d PASS%s / %s%d FAIL%s" % (GREEN, PASS, OFF, RED, FAIL, OFF))
        return 1
    print("       map-view: %s\n       map-dump: %s"
          % (os.path.relpath(fv, ROOT), os.path.relpath(fd, ROOT)))

    # Bullet 1: same field, same format. KEY EXISTENCE, not value equality —
    # an absent key must FAIL, which is the whole point of the convention.
    for num, name, d in (("1.1", "map-view", view), ("1.2", "map-dump", dump)):
        ch = d.get("channel")
        check(num, "%s publishes a `channel` block with `alphabet` and `distinct_from`"
              % name,
              isinstance(ch, dict) and "alphabet" in ch and "distinct_from" in ch
              and isinstance(ch.get("alphabet"), str) and ch["alphabet"] != "",
              "channel = %r" % (ch,))

    vc, dc = view.get("channel") or {}, dump.get("channel") or {}

    # Bullet 2: comparable by that field ALONE, with no out-of-band knowledge.
    # The two do NOT share an alphabet and must not — one is a single ASCII char
    # per cell, the other 2-char catalog tokens per layer. What has to hold is
    # that each names the other, so a consumer comparing them cell-for-cell can
    # detect the mistake by field instead of by mysterious disagreement.
    check("2.1", "the two alphabets are DIFFERENT, as they must be",
          vc.get("alphabet") != dc.get("alphabet"),
          "both report %r — the channels have been conflated" % vc.get("alphabet"))
    check("2.2", "map-view's `distinct_from` names map-dump's alphabet",
          vc.get("distinct_from") == dc.get("alphabet"),
          "%r vs %r" % (vc.get("distinct_from"), dc.get("alphabet")))
    check("2.3", "map-dump's `distinct_from` names map-view's alphabet",
          dc.get("distinct_from") == vc.get("alphabet"),
          "%r vs %r" % (dc.get("distinct_from"), vc.get("alphabet")))

    # Bullet 3: "a change to the symbol table changes the identity." Nothing can
    # prove intent, but the identity CAN be tied to the source that declares it,
    # so an id edited in one place and not the other fails here rather than
    # silently shipping two truths.
    spatial = open(os.path.join(ROOT, "Source/AutoRimmer/Spatial.cs")).read()
    mapdump = open(os.path.join(ROOT, "Source/AutoRimmer/MapDumpVerbs.cs")).read()
    m = re.search(r'AlphabetId\s*=\s*"([^"]+)"', spatial)
    check("3.1", "Spatial.cs declares an AlphabetId constant", m is not None)
    if m:
        check("3.2", "and it is the string the banked map-view envelope carries (%r)"
              % m.group(1), m.group(1) == vc.get("alphabet"),
              "source says %r, envelope says %r" % (m.group(1), vc.get("alphabet")))
        check("3.3", "MapDumpVerbs.cs's `distinct_from` cites that same constant "
              "verbatim, so bumping one and not the other fails here",
              ('"%s"' % m.group(1)) in mapdump,
              "MapDumpVerbs.cs does not contain %r" % m.group(1))
    md = re.search(r'\["alphabet"\]\s*=\s*"([^"]+)"', mapdump)
    check("3.4", "MapDumpVerbs.cs's own alphabet literal matches its envelope",
          md is not None and md.group(1) == dc.get("alphabet"),
          "source says %r, envelope says %r"
          % (md.group(1) if md else None, dc.get("alphabet")))

    # The identity is versioned so a stale consumer can detect a table change.
    # An unversioned name gives it nothing to compare.
    check("3.5", "both identities carry a trailing version",
          bool(re.search(r"[-/]\d+$", vc.get("alphabet") or ""))
          and bool(re.search(r"[-/]\d+$", dc.get("alphabet") or "")),
          "%r / %r" % (vc.get("alphabet"), dc.get("alphabet")))

    print("\n%s%d PASS%s / %s%d FAIL%s"
          % (GREEN, PASS, OFF, RED if FAIL else GREEN, FAIL, OFF))
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
