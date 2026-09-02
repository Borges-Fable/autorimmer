# cockpit — watch a run, or replay one

A read-only terminal cockpit over `transcripts/`. It replays a finished run and
tails a growing one with the same code: **live mode is replay mode following a
directory that is still growing** — one parser, one renderer, one code path.

```bash
cockpit/cockpit m1-20260901              # replay a finished run
cockpit/cockpit m1-20260901 --follow     # tail one that is still growing
cockpit/cockpit --live                   # whatever the bench is writing now
cockpit/cockpit m1-20260901 --probe      # resolution and counts, no terminal
```

Requires `textual` (`pip install textual`); everything else is stdlib.

## It sends nothing

**No verb, ever, not even an observer. No socket.** This is a correctness
requirement, not a preference:

- `journal` moves `Journal.ReadWatermark` (`JournalVerbs.cs:152`), and the
  watermark is what `advance` compares against before it will run again. A
  cockpit that called it would discharge the driving agent's obligation and let
  its next advance run past unread events — git-bug `722c951`'s refusal
  defeated, and the duplicate-driver deadlock that froze run `m1-20260901`
  twice. Line 229 names the hazard: *"can see another client move it"*.
- `digest` would write `commands/<id>.json`, take a `results/<id>.json`, and
  claim a numbered step directory inside the driver's own run.

So the journal is read **as a file**, never through the verb, and every other
panel is built from `cmd.json` / `result.json` the client already wrote.

`verify-readonly.py` is the proof rather than the assertion. It snapshots the
protocol root, runs the cockpit headless in `--follow` for 60 seconds under a
`sys.addaudithook` that fails on any write, socket or subprocess, and diffs:

```bash
python cockpit/verify-readonly.py m1-20260901 --seconds 60
```

**On the watermark.** `readWatermark` is a private static in `Journal.cs` and is
never persisted, so it cannot be read off disk — and the only thing that returns
it is the `journal` verb, whose call *moves* it. The observation is destructive,
which is precisely why the cockpit does not make it. It is proved statically
instead, and the chain has no branches: `readWatermark` is moved only by
`Journal.NoteRead`; `NoteRead`'s only caller is `JournalVerbs.Read`; that runs
only when `Poller.ScanInbox` finds a file in `commands/`. **`commands/` gained
no file, therefore the watermark did not move.**

## Two pages

`tab` switches. Page one is the colony; page two is the agent's own instruments.

**Flight deck.** The map is the hero: `map-view`'s ASCII, monochrome, with fog
dimmed, pawns bright and hostiles in the warning colour — three weights of one
hue, no colour-per-def. The sidebar carries the step: op, args, `ok`/`error`,
and the envelope *folded* to one line per section, the accent marking only what
changed since the last step with this op. `enter` expands the section under the
cursor to its raw JSON. Below it, the journal delta for that step.

**Rig.** Every verb the mod exposes against the ones the run ever reached —
dim is never called, muted is called later, accent is what the agent had used by
*this* step, so scrubbing lights the surface up as the run learns its own tools.
Beside it, the shelf the driver was meant to be reading (playbook, checklists,
colony memory, the run's own documents) and `checklist.ndjson`, the one place it
wrote down a reading and a note rather than a verb call.

On `m1-20260901` the rig reads **133 verbs, 71 used, 62 never called** — the
whole `dev:` god-hand dark, which is correct, and `quest*`, `policy*`, `orders`,
`repair` and `schedule` dark, which is a finding.

## Keys

`←`/`→` or `h`/`l` step · `[`/`]` jump an in-game day · `↑`/`↓` move the section
cursor · `enter` expand · `pgup`/`pgdn` scroll the map · `tab` page · `f` follow
· `g` newest · `?` help · `q` quit.

## What it reads

- `transcripts/<run>/NNN-<op>/{cmd,result}.json` — the whole **chain**. A run
  over 999 calls rotates into `<run>-s01`, `-s02`, … (git-bug `5eba561`), and
  `m1-20260901` is five segments and 4,599 steps: reading only the head shows
  21.7% of it. The walk follows `meta.json` links *and* derives siblings by
  name, because the five segments of `m1-20260901` carry no links at all —
  `rwa` derives them on first open, which is a write the cockpit will not do.
- `journal/<sid>.ndjson`, found under `RUNS/<run>/journal/` or the protocol
  root. `--journal PATH` overrides.
- `Source/AutoRimmer/*.cs` for the `[Verb("…")]` registry (page two only). The
  source is the list; asking the bench would mean sending a command.

Two shapes it degrades honestly on, both real in `m1-20260901`:

- **A step with `cmd.json` and no `result.json`** renders as *NEVER RETURNED*
  with the command that was asked for, not as an empty envelope. `rwa` writes
  `cmd.json` before dispatch, so this is a call that was in flight when the
  client stopped. Steps 125, 324 and `-s02/574`.
- **A tick that goes backwards.** The run does it 67 times: `-s00/521-advance`
  ends at tick 2,276,023 and `522-pawn` reports 2,223,181, because a step
  directory is claimed when the command is *sent* and two clients against one
  bench interleave. The day index takes a strict maximum, the journal delta is
  keyed on the order results actually returned, and the sidebar says *returned
  BEFORE the previous step* when it happens.

## Screenshots

`screenshots/*.svg`, written by Textual's own `App.save_screenshot`:

```bash
cockpit/cockpit m1-20260901 --at 4595 --screenshot out.svg --size 140x42
```

**Open them in a browser.** Rich's SVG references Fira Code from a CDN with a
`local()` fallback; `rsvg-convert` on a machine without that font substitutes
one with different metrics and then squeezes each run to its declared
`textLength`, which eats leading spaces and makes the layout look broken. The
cell coordinates in the file are correct — it is a converter artifact, not the
app.

## Why

`RUNS/m1-20260901/next-run.md` §C opens with *"I never looked at the base after
day 1."* Sixty-six days driven off numbers, and the colony starved with a hole
in its freezer wall that one look would have shown. The map panel's caption says
how many steps back its grid came from, precisely so that stops being invisible.
