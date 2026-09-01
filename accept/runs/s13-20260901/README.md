# Session 13 — the bench evidence three open issues rest on, banked

Bench: `_RimWorld-Agent`, session `20260901T121508`, assembly `1.0.0+ad29d6d`.
Suite `accept/s13-mod-surface.py`: **169 PASS / 0 FAIL / 1 SKIP**.

## Why this directory grew a `results/` on 2026-09-01

It was banked by the round that follows session 13, not by session 13, and the
reason is a gap worth recording rather than quietly closing.

Three open issues — `8b4839f`, `c718e4a`, `3a5ff6c` — cite raw response
envelopes by name (`results/accs13-026-devspawnthing.json`,
`accs13-025-findrect`, `accs13-009-findrect`, `accs13-028/029`) and cite journal
rows by seq (24, 35, 38, 42). **None of it was in the repo.** It lived only in
the live bench's protocol root:

    _RimWorld-Agent/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/AutoRimmer/

That directory is the working root of whatever bench is running. The next launch
writes into it, and the round these three issues exist to drive is about to
launch benches repeatedly. The evidence for the round's own premise was one run
away from being overwritten.

What the repo *did* hold was `transcripts/20260901T121508/`, which carries a
`cmd.json` per call and **no responses** — so `c718e4a`'s citation of
`064-find-rect/cmd.json` was banked and every response citation was not. A
transcript proves what was asked. It does not prove what the game answered, and
the answers are the findings.

## What is here

- `results/` — the 65 `accs13-*` response envelopes, verbatim.
- `journal-20260901T121508.ndjson` — 62 rows, including seq 24 (the
  spawn-refusal row carrying `failed[]`), 35, 38 and 42.
- `live-smoke.md` — the smoke pass that preceded the suite.
- `s13-mod-surface.txt` / `s13-mod-surface-selftest.txt` — the suite run and its
  self-test.

## The headline artifact, verified on banking

`results/accs13-026-devspawnthing.json` is exactly what `8b4839f` describes:

    "reason":  "Interaction spot is blocked by granite."
    "blocker": { "def": "WoodLog", "at": [125,128], "removal": "none" }

The sentence names granite on the interaction cell one row south; the blocker
names a wood log on the target cell with `removal: "none"` — the opposite of the
truth, since the granite is `mine`. Confirmed member for member against the
issue before this directory was committed.

## Standing lesson

An issue that cites a path under the bench's protocol root cites a file with a
lifetime shorter than the issue. Bank the envelope beside the claim, or the claim
outlives its evidence.
