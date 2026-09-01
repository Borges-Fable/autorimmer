# accept/fixtures/ — banked envelopes that offline suites read as INPUT

Two suites here grade real mod output without a bench:
`accept/2a7c064-label-centre.py` (phase 6) and
`accept/e6faa51-channel-alphabet.py`. Both need a real `map-dump` /
`map-view` response envelope.

**They originally read them out of `transcripts/`, which is GITIGNORED**
(`.gitignore:25`; `git ls-files transcripts/` returns nothing). So both suites
passed on the machine that wrote them and could not pass anywhere else — not in
a clean clone, and not in a worker's git worktree, where `transcripts/` does not
exist at all. A session-A worker hit exactly that and reported
`e6faa51-channel-alphabet.py` as 0/2, correctly, and it read like a regression.

Worse than the inconvenience: **two issues were closed on evidence from those
suites** (`2a7c064`, `e6faa51`) while the evidence sat in untracked files.

This is the same failure the `accept/runs/s13-20260901/README.md` lesson names —
"an issue that cites a path under the bench's protocol root cites a file with a
lifetime shorter than the issue" — committed the same day it was written down,
one directory over. A gitignored path is that lesson's other half: the file
survives locally and vanishes for everyone else.

## Why a separate directory from `accept/runs/`

`accept/runs/` is EVIDENCE — what a run produced, banked so a claim outlives it.
These are INPUT — fixtures a suite consumes on every future run. Different
lifetime, different reason to exist, so they do not share a directory. A file in
`runs/` may be pruned once its claim is settled; a file here may not, because
deleting it breaks a check.

## Contents

| file | source | what reads it |
|---|---|---|
| `map-dump-20260831T230213-006.json` | bench `20260831T230213`, call 006 | `2a7c064-label-centre.py` phase 6 — 51x51, 213 labels, sizes 1x1/1x2/2x1/2x2/3x2, rotations East/South/West |
| `map-view-m1-20260831-124.json` | run `m1-20260831`, call 124 | `e6faa51-channel-alphabet.py` — the `channel` block |

Both are verbatim, unedited response envelopes. `map-dump-*` also supplies the
`channel` block `e6faa51`'s suite compares against `map-view`'s.

Do not hand-edit these. A suite that needs a case these do not cover wants
another banked envelope beside them, not a doctored one — the whole value is
that they were recorded by a real bench before the checks existed.
