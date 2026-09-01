# `rwa` — driving the bench from a shell

The client half of the AutoRimmer file protocol (spec 1.4). The server half is
`../Source/AutoRimmer/`; where this document and that code disagree, **the code
is right** — it is what runs on the bench.

```bash
rwa/rwa status              # is the bench alive, and how alive
rwa/rwa ping
rwa/rwa digest --json | jq .data.alerts
rwa/rwa advance --until.letter true --timeout_ticks 60000
rwa/rwa journal --since 42
rwa/rwa watch on            # reveal the window, raise the fps cap
```

Put it on `PATH` if you like — `ln -s "$PWD/rwa/rwa" ~/.local/bin/rwa`. The
script resolves its own repo through the symlink, so transcripts still land in
`<repo>/transcripts/`.

Stdlib python3 only, no dependencies. Everything below was exercised against a
synthetic protocol root by `./selftest.sh`; see the bottom of this file for
what that does and does not prove.

## How to drive it (the part you actually need)

1. `rwa status`. Five verdicts, and only one of them means "start the game" —
   see [Liveness](#liveness-is-not-process-up) below.
2. `rwa <op> [--arg value …]`. Any op the mod registers is a valid word here;
   there is no verb table in the client. `rwa verbs` asks the mod what it knows,
   and an `unknown-op` error lists them too.
3. Read the result. `--json` gives you the mod's envelope byte for byte,
   `--pretty` renders it as an indented tree. On a tty you get pretty, in a pipe
   you get JSON, and `--json`/`--pretty`/`RWA_OUTPUT` override that.
4. Exit code: `0` ok · `1` the mod said `ok:false` · `2` bad usage · `3` no live
   bench · `4` timed out waiting for a result.

Under the covers each command is one file written to `commands/<id>.json` and
one file read back from `results/<id>.json`. The mod's poller runs on a 500 ms
cycle and ignores inbox files younger than 250 ms, so **the floor on a round
trip is roughly 0.25–1 s** even for a trivial verb. That is the protocol, not
the client being slow.

## Where the protocol root is

Derived, in this order, and the first one holding a `status.json` wins:

| candidate | when |
|---|---|
| `$RIMWORLD_VAULT/_RimWorld-Agent/config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/AutoRimmer` | this box — the bench isolates `XDG_CONFIG_HOME` |
| `$RIMWORLD_VAULT/_RimWorld-Agent/SaveData/AutoRimmer` | a `-savedatafolder` launch (the rwpack pattern) |
| `~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/AutoRimmer` | an un-isolated Linux install |
| `~/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/AutoRimmer` (and `$USERPROFILE/…`) | the Windows bench |

`RIMWORLD_VAULT` defaults to `/home/dorian/projects/rimworld` and is the same
variable `profile/make-profile-agent.sh` honours — one convention, not two.
`RWA_ROOT` (or `--root`) overrides the whole search; that is the escape hatch
for a bench somewhere else, and for the synthetic root the self-test uses.

`rwa root` prints what was resolved; `rwa root --json` prints every candidate it
tried and whether it exists. Use it first when something looks wrong.

## Liveness is not "process up"

RimWorld's tick loop is frame-bound, and Hyprland delivers no frames to a window
on a hidden special workspace. A bench parked there without the launcher's
`render_unfocused` fix runs at 1–2 fps ≈ 4 tps — and looks perfectly healthy in
`ps` (FINDINGS.md §4b). So `rwa status` grades five states rather than two:

| state | what it means | exit | what it tells you |
|---|---|---|---|
| `down` | no `status.json`, or its `ts` is older than `--stale-secs` (10 s) | 3 | how to start the bench. **The only state that does.** |
| `menu` | heartbeat fresh, no game loaded this session | 0 | load a save; `status`/`version`/`journal` still answer |
| `stalled` | heartbeat fresh, but `gameLoaded:false` over a real snapshot — `GameComponentUpdate` has not run for >5 s | 3 | loading screen, or total frame stop; check the windowrules |
| `starved` | game loaded, frames arriving below `--fps-floor` (5) | 0 | the §4b throttle; relaunch through `run-agent.sh`, or `rwa watch on` |
| `ok` | live | 0 | — |

The heartbeat is written by the mod's **poller thread**, not by frames, so a
stale `status.json` means the process is gone; frame starvation shows up as a
low `fps` with a fresh timestamp. Paused is normal — the agent owns time.

`rwa status --sample 2` reads the heartbeat twice two seconds apart and reports
the tick delta, which is FINDINGS §4b's standing advice ("assert liveness, not
process-up") in one command.

Every command that sends checks liveness **before** writing to the inbox. That
is not just politeness: a command dropped into a dead bench's inbox is answered
`stale-on-restart` at the next launch, so failing fast is the difference between
"nothing happened" and "a ghost surfaces tomorrow". Only `down` blocks, though —
a `stalled` or `starved` bench still has a live poller thread and answers
promptly (off-thread verbs succeed, main-thread verbs come back
`no-active-game`), so refusing there would hide the mod's own better answer
behind a guess.

## Argument syntax

```
--key value        scalar; type guessed: true/false/null, a number, else a string
--key              a bare flag is the boolean true
--key=value        same as --key value
--key:str value    force the type — also :num, :bool, :json
--a.b.c value      dotted keys nest:  --until.event.type death
--key x --key y    repeating a key builds an array
--args-json JSON   the whole args object; '-' reads stdin, '@path' reads a file
```

The mod is strict about types (`VerbArgs` rejects a string where it wanted a
number rather than coercing it), so a wrong guess is a loud `bad-args`, never a
silent misread. Two things worth knowing:

- A **position** is `"x,z"` — a string, not an array — so `--near 120,130` is
  already right. `[x,z]`, a landmark name, `pawn:<id>` and `thing:<id>` all work
  too (`Positions.Resolve`).
- Values are never split on commas. `--types letter --types death` is how you
  build `["letter","death"]`; `--rect:json '[10,20,5,5]'` is how you build a
  number array. (The one exception is `rwa journal --type`/`rwa tail --type`,
  which are this script's own flags rather than op arguments and do accept
  `--type letter,death` as a convenience — journal type names never contain a
  comma.)

Worked examples, all of them real ops:

```bash
rwa advance --ticks 2500
rwa advance --until.letter true --timeout_ticks 60000 --max_tps 600
rwa advance --until.event.type death --until.event.contains Xitral
rwa advance --until.threat --halt_on_error:bool false
rwa find-rect --w 7 --h 5 --near 120,130 --require buildable --require noRoof
rwa map-view --rect:json '[100,100,24,24]' --layers terrain --layers things
rwa landmark --set.kitchen 120,130
rwa journal-selftest --steps letter --steps message --letter_delay_ticks 400
rwa pawn --id 3
rwa zone --op delete --id 12
rwa send status                       # explicit form, needed only for name clashes
rwa advance --args-json '{"until":{"alert":"Alert_LowFood"},"timeout_ticks":120000}'
```

`--args-json` is the escape hatch and the machine-friendly path: it takes the
exact object the protocol will carry, and explicit flags override it.

### Reserved names (`--id` is NOT one of them)

`rwa`'s own flags are recognised anywhere on the line, so any op argument
sharing a name with one would be eaten before the mod ever saw it. The reserved
set is deliberately kept out of the verbs' namespace:

```
--root --timeout --cmd-id --run --transcripts --stale-secs --fps-floor
--poll-ms --json --pretty --no-transcript --quiet --version --help -h
```

`id` is the only argument name in the whole verb registry that ever collided
with that list, and **`--id` no longer does**: since client 0.2.0 rwa's own
command id is spelled `--cmd-id`, and a bare `--id` is an ordinary op argument
that reaches the verb. Before that, `rwa pawn --id 3` silently became the
command id and the mod answered `bad-args: missing required arg 'id'` — a full
M1 run worked around it with `--args-json`. If you scripted `--id` meaning the
*command* id, that is the one thing to re-spell.

Nothing else on that list is a verb argument today. If a future verb wants one
of those names, `--args-json '{"run":…}'` passes it through untouched — the
reserved table applies to flags, never to the JSON object.

## Output, and what pretty mode is allowed to do

Machine output is always valid JSON: one envelope per invocation, in the mod's
own shape — `{id, op, ok, data|error, state, sid, ts}` — passed through
untouched. A client-side failure gets the same shape with an `rwa-…` error code
and an `rwa` block; nothing else in the envelope moves, so a jq pipeline never
has to branch on who failed.

```
rwa-game-down    no live bench (before or during the wait)
rwa-timeout      command accepted, no result inside --timeout
rwa-bad-result   the result file was not a parseable envelope
rwa-no-root      no protocol root could be resolved
rwa-usage        bad invocation (e.g. an id the mod would rewrite)
rwa-io           a filesystem failure on our side
```

Pretty mode is a **generic** JSON tree renderer. It has no verb-specific
formatting, on purpose: a per-verb formatter would be game semantics on the
client, which this spec forbids, and it would silently hide any field a later
serializer adds. If a verb ever wants a prose rendering, that belongs in the
verb's own data (a digest string from spec 2.1), not here. Pretty mode never
drops, renames or reorders a field. The single place it elides anything is the
one-line-per-event journal view, where a long payload is clipped with `…` and
`--full` turns clipping off.

## jq recipes

These are copy-pasteable and are exercised by `selftest.sh` §10.

```bash
# the alert readout, one line each, highest-priority information first
rwa digest --json | jq -r '.data.alerts.active[] | "\(.priority)\t\(.label)"'

# who is unhappy
rwa digest --json | jq -r '.data.colonists[] | select(.mood_pct < 60) | .name'

# advance, then read exactly what happened while time ran
seq=$(rwa journal --json --limit 1 --since 99999 | jq .data.last_seq)
rwa advance --ticks 2500 --json | jq -c '.data | {reason, ticks_elapsed, avg_tps}'
rwa journal --json --since "$seq" | jq -r '.data.events[] | "\(.tick)\t\(.type)"'

# the halt event of an until-advance, in full
rwa advance --until.threat --json | jq '.data.halted_on'

# one shape whoever failed: mod error codes and client error codes read alike
rwa nosuchverb --json | jq -r 'if .ok then "ok" else .error.code end'

# every red error this session — the standing zero-red-errors invariant
rwa journal --json --type red_error | jq -r '.data.events[].payload.msg'

# what the mod actually registers, as a shell array
verbs=($(rwa verbs --json | jq -r '.data.verbs[]'))
```

`rwa tail --json` is the streaming exception: NDJSON, one journal event per
line, which is what jq consumes natively.

```bash
rwa tail --json --type letter --type death | jq -r '"\(.tick)\t\(.payload.label // .payload.pawn)"'
```

## Journal

`rwa journal` reads `journal/<sid>.ndjson` **directly**. That file is the same
one the `journal` verb reads (`JournalVerbs` runs off the main thread), so a
direct read costs the game nothing, needs no live bench, and is the only path
that works for a post-mortem after the game has exited. `--verb` takes the same
read through the protocol instead; the `data` shape is identical either way, and
`.rwa.source` says which path produced it.

```bash
rwa journal --since 42                 # everything after seq 42
rwa journal --type letter --type death # filter (repeat the flag, or --type letter,death)
rwa journal --since-tick 60000
rwa journal --limit 0                  # no cap (the verb's own max is 2000)
rwa journal --list                     # every session file under journal/
rwa journal --sid 20260830T214611      # an earlier session
rwa tail                               # follow, including across a game restart
rwa tail -n 50 --once                  # last 50 and exit
```

Schema: `../JOURNAL.md`. Read its alert-timing section before asserting on
ticks — alerts trail the state change that caused them, by design.

## Transcripts

Every command and result is mirrored into a run directory:

```
transcripts/<sid>/
  meta.json          run id, client version, start time
  log.ndjson         one line per command: op, id, ok, error, elapsed_s, source
  001-ping/cmd.json     the exact bytes written to commands/<id>.json
  001-ping/result.json  the exact bytes read back from results/<id>.json
  002-advance/…
```

`cmd.json` is written **before** the command is dispatched; `result.json` is
written when the result comes back. So a step directory holding a `cmd.json`
and no `result.json` is a command that was in flight when the client stopped —
killed, disconnected, or still running — and it names exactly what was asked
for. (It used to be the other way round: both files were written after the
result returned, so a dead client left an empty numbered directory and no
record at all. `136-advance` and `187-advance` in run `m1-20260831` are that
hole — about 60,000 ticks, a full in-game day, with nothing on disk saying
what had been sent.) Consumers already tolerate the half-written step:
`accept/4.2-play-loop.py` warns past it and carries on.

The run directory is named for the **game session id** by default, because
`sid` is already the join key for `journal/<sid>.ndjson` and for every result
envelope — one game session, one transcript, one journal, no correlation table.
`--run NAME` / `RWA_RUN` overrides it (use it for a scripted scenario), and
`--no-transcript` / `RWA_NO_TRANSCRIPT` switches recording off. `RWA_TRANSCRIPTS`
moves the root; it defaults to `<repo>/transcripts/`, which is gitignored.

```bash
export RWA_RUN=food-crisis
rwa journal-selftest --steps stockpile
rwa advance --until.alert Alert_LowFood --timeout_ticks 120000
rwa digest
rwa replay food-crisis            # re-send the whole thing, in order
rwa replay food-crisis --dry-run  # …or just print what it would send
```

`replay` regenerates command ids by default (`--same-ids` keeps the originals),
so a replay never overwrites the evidence of the run it came from.

## `rwa watch`

Two independent mechanisms, deliberately not merged, because they fail
separately:

- **The fps cap** is a line in `<bench>/config/mangohud-agent.conf`. The
  launcher points `MANGOHUD_CONFIGFILE` at that file and MangoHud re-reads it
  while the game runs, so rewriting `fps_limit` re-caps a **running** game
  within a second or two — no restart, no game-side code. Unwatched 30, watched
  60. `advance` does not want more: at a 30 fps cap the budgeted tick loop
  measured ~1392 tps (FINDINGS §6), so raising it for a fast-forward buys
  nothing and costs thermals.
- **The window** is parked on Hyprland's `special:rwagent` by a `silent`
  windowrule applied before launch. `rwa watch on` reveals that workspace,
  `off` hides it. The reveal is idempotent — it reads `hyprctl -j monitors`
  first and only toggles if the state is wrong.

```bash
rwa watch          # report: cap, workspace, whether the window is there
rwa watch on       # reveal + fps_limit=60 + misc:render_unfocused_fps 60
rwa watch off      # hide + fps_limit=30
rwa watch on --fps 144
```

If there is no `RimWorldLinux` window on this Hyprland session, `rwa watch` says
so and points at the Xvfb fallback: a bench started with `run-agent.sh --xvfb
--vnc` is watched with a VNC client on `localhost:5900`, not by revealing a
workspace (FINDINGS §8). Off Hyprland entirely — the Windows bench, a TTY — the
fps half still works and the command says the reveal was skipped.

## `rwa render` — the PNG channel

Spec 2.5. `map-dump` publishes per-cell planes; `render` draws them. The mod
does no image work at all, so a render is a pure function of (dump, catalog,
options) and **the same dump always produces the same bytes** — which is the
acceptance, and is checked in `selftest.sh` §12 rather than asserted here.

```bash
rwa catalog-dump                                       # once per bench session
rwa render --rect 100,110,40,30 --out base.png --scale 16
rwa render --around base-center --radius 25 --out base.png --landmarks
rwa render --whole-map --out map.png --scale 3
rwa render --dump saved.json --out base.png            # offline, no bench at all
```

Two things worth knowing before you use it:

- **The catalog is what makes it mod-aware.** Colours, footprints and the 2-char
  glyph token all resolve out of `<root>/catalog.json`, written by the
  `catalog-dump` verb. Without it the render still answers every geometry
  question — rooms, doors, walls, where the stove is — but in fallback greys,
  and both the legend and the command's own output say so. It is not silent
  about it, because a grey base that looks authoritative is worse than a warning.
- **The offline path is not a convenience.** `--dump FILE` renders a saved dump
  with no bench running and no protocol root at all; `--save-dump FILE` on a
  live render writes the file to replay. That is what makes the determinism
  claim checkable, and what lets a post-mortem redraw its evidence months later.

**This is a different alphabet from `map-view`, deliberately.** `map-view` says
`#` for a wall in a fixed-width ASCII grid where glyph collisions are fatal;
the PNG says a coloured block labelled `WA`, where colour disambiguates and two
characters are affordable. Both channels report the identity of the symbol
system they used (`channel.alphabet`), and **a glyph from one must never be
compared against a glyph from the other**. Independence is the point: 2.5 exists
to be a second opinion on 2.3's ASCII, and a second opinion computed from the
same collapsed cell would not be one.

Fog is respected, exactly as it is on every other player-facing verb: an
unexplored cell is empty in every plane and hatched in the image. The agent does
not get to see the shape of ground the colony has never visited just because it
asked for a picture instead of text.

`--layers a,b,c` restricts what is dumped and drawn (`terrain`, `things`,
`zones`, `rooms`, `roof`, `pawns`). `--scale` is pixels per cell — 12 is the
default and roughly the floor for glyph labels; below that the labels are
dropped rather than drawn illegibly. `--max-side` guards the output dimensions
at 8000px, and it is worth respecting: a very large PNG is also the wrong thing
to hand an agent, because it gets downsampled and the glyphs stop being readable
before the guard ever trips.

## `rwa place-layout` — the IR half of spec 3.3

Spec 3.3. **The mod never sees a layout file, a path or a token grid.** This
command reads the IR with `baseviz/ir.py` — the module that already IS the
dialect — and sends ONE `place-layout` call carrying a resolved list of
`{def, at, rot?}`.

```bash
rwa place-layout templates/bedroom.ir.json --origin 120,130 --stuff '*=WoodLog'
rwa place-layout templates/power-room.ir.json --origin 200,200 --dry-run
rwa place-layout templates/bedroom.ir.json --origin 120,130 --mode instant
rwa place-layout templates/bedroom.ir.json --origin 1,1 --print-payload   # offline
```

**One call, not N `build` calls, and that is the whole design.** The invariant
is "preflight every cell first; on any failure place NOTHING", and it cannot
hold across N transactions — a half-built room is reachable between any two of
them. So the whole layout goes in front of the gate at once, and without
`--partial` a single refusal places nothing at all. The per-cell failures come
back in `Blockers`' `{removal, reason}` shape, so a refused layout reads like a
refused `site-survey` and the agent can choose between "clear this and retry"
and "site it elsewhere".

Three coordinate facts, and getting any of them wrong puts a room somewhere you
did not mean:

- **`--origin` is the layout's SOUTH-WEST corner**, deliberately the same corner
  `find-rect` returns as `at` and `[x,z,w,h]` carries as `x,z`. So
  `rwa find-rect --w 5 --h 7` and `rwa place-layout … --origin <its at>` name
  the same ground.
- **Row 0 of the grid is NORTH** (`baseviz/ir.py`'s pinned docstring), so a cell
  at grid `(r,c)` is `(ox + c, oz + h - 1 - r)`.
- **Each element's `at` is its footprint's north-west cell**
  (`templates/INDEX.md` pin 1). The mod converts north-west → south-west → the
  game's placement CENTRE, because that needs the def's ROTATED size, and the
  def database is not on this side of the bridge. Every placement comes back
  with all three — `at`, `pos` and `footprint` — so they can never be confused.

**A rotation suffix is split; a material suffix is not.** Only the four `Rot4`
words (`_North/_East/_South/_West`) come off a token, because that is what the
suffix MEANS (`INDEX.md` pin 2: the `Rot4` value verbatim, not which way the
thing faces). A KCSG-style `Wall_WoodLog` goes over as a def name and the mod
refuses it by name — telling a material from a def needs the def database, and a
silent split would invent a def. Bind material with `--stuff DEF=STUFF` (repeat
it; `*` is the default for every stuffable def not named) or `--stuff-map
JSON|@file`; `--strict-stuff` refuses to fall back to the game's default at all.

**The roof grid is reported, not sent.** A roof is a DESIGNATION, not a
placement, and an enclosed room under 320 cells roofs itself next tick anyway
(`AutoBuildRoofAreaSetter`). `--roof` sends `area {kind:"build-roof"}` as an
explicit SECOND call, outside the transaction.

`--print-payload` resolves a layout with no bench at all and prints the JSON —
the same argument `render --dump` makes, and the way to review what a template
expands to before placing it. `--save-payload FILE` banks it for
`rwa send place-layout --args-json @FILE`. `rwa send` also reaches the raw verb
directly, which is what an acceptance suite building its own payload wants.

Undo is `rwa cancel-layout --layout_id ly-N` (or `--placement_id pl-N` for one
element), which points `Designator_Cancel` at that transaction's own blueprints
and frames — never at their cells, which would also remove every cancelable
designation there.

Progress is `rwa construction --layout_id ly-N`, which answers for THAT
transaction's placements and no others:

```
rwa construction --layout_id ly-1 --json | jq '{done, built, cancelled, unresolved, by_state}'
```

`rect_source` reads `layout`, so an envelope says which question it answered.
`done`/`built`/`cancelled`/`unresolved` are uncapped (only the per-element
detail is capped, as everywhere in this verb), which is why
`advance --until.layout ly-1` can halt on them. A `--layout_id` this session
does not know is a refusal naming the ids it does; `--layout_id` together with
`--rect`, `--around`, `--id` or `--placement_id` is a refusal too, because two
scopes are two questions.

## Environment

| var | what | default |
|---|---|---|
| `RIMWORLD_VAULT` | the mod-repo workspace | `/home/dorian/projects/rimworld` |
| `RWA_ROOT` | protocol root, overriding the search | derived |
| `RWA_TRANSCRIPTS` | transcript root | `<repo>/transcripts` |
| `RWA_RUN` | transcript run-dir name | the game session id |
| `RWA_NO_TRANSCRIPT` | disable recording | unset |
| `RWA_OUTPUT` | `json` or `pretty` | tty-dependent |

## Self-test

```bash
./selftest.sh          # ~60s, no game involved
KEEP=1 ./selftest.sh   # leave the synthetic root behind to poke at
```

`fakebench.py` emulates `Poller.cs` — the same 500 ms scan, the same 250 ms
minimum file age, the same consume-before-execute, the same envelope, the same
id sanitisation — so the client cannot tell it apart, and every failure mode is
a flag instead of a race: a stale heartbeat, a live heartbeat over a starved
frame loop, a timeout, a mangled result, each error code in the taxonomy.

§13 covers `place-layout`'s IR expansion the same way, and is scoped just as
narrowly on purpose: `--print-payload` resolves a layout with no bench in the
loop, so the coordinate map, the token split, the stuff-map merge and the
argument refusals are settled offline — and nothing else about `place-layout`
is. Whether the game ACCEPTS any of it is `accept/1adc737-place-layout.py`, on
a bench.

**What the self-test does not prove**: the live round-trip against a running
game, and `rwa watch` actually moving a window and re-capping a live process.
The Hyprland calls are stubbed out there on purpose (toggling a special
workspace is not a side effect a test should have). Those two are demonstrated
on the bench, by hand.
