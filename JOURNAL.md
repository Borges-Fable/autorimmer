# Journal schema (spec 1.2)

Append-only NDJSON under save-data `AutoRimmer/journal/`, one file per process
session, named `<sid>.ndjson` — the same `sid` that `status.json` and every
result envelope carry. rwtest (5.1) asserts on these fields; treat them as a
contract and grow them additively.

## Event envelope

```json
{"seq":12,"tick":48210,"wall":"2026-08-30T20:31:04.11Z","type":"letter","payload":{…}}
```

- `seq` — monotonically increasing per session file, no gaps at rest (a gap
  means lost writes; the flusher is single-threaded, so this is an invariant,
  not a hope).
- `tick` — game tick. From main-thread hook sites it is exact
  (`Find.TickManager.TicksGame`); from off-main emitters (log hooks on
  background threads, boot-time events) it is the last published snapshot tick,
  accurate to about one frame. Before any game is loaded it is 0.
- `wall` — UTC ISO-8601, always exact.
- `type`, `payload` — below. Consumers must ignore unknown payload fields and
  unknown types.

## Types

| type | payload | notes |
|---|---|---|
| `session` | `kind`: `boot` (+`mod`,`game`,`bench`), `newgame`, `loaded`, `saved` (+`file`), `unloaded` | `boot` is always seq 1. `unloaded` is the poller noticing the heartbeat stop (no game means no main thread to notice it); `aborted:N` on any of `newgame`/`loaded`/`unloaded` is how many in-flight commands were answered `no-active-game` at that boundary (1.5 blockers 1–2) |
| `letter` | `def`, `label`, `text?` (≤1500 chars), `target?`, `faction?` | captured at the LetterStack funnel on ARRIVAL — never from letter-open, which opens at most ONE letter per call and so drops bursts (see the correction below) |
| `message` | `text` (≤500), `def` | top-of-screen messages; flash-dedupe rejections are not journaled |
| `alert_on` | `id` (Alert class name), `label`, `priority` | see cadence note |
| `alert_off` | `id`, `label` | label as remembered at `alert_on` |
| `death` | `pawn`, `faction?` | every pawn death during PLAY; mapgen corpse setup is excluded; filter by `faction` |
| `downed` | `pawn`, `faction?`, `damage?` | ditto |
| `mental_break` | `pawn`, `faction?`, `state`, `causedByMood`, `reason?` | successful starts only, during play |
| `red_error` | `msg` (≤2000) or `msg`+`suppressed:true`, `overflow?` | per-text cap 3 per session, then one suppression marker. **The cap is a FILE policy only** — `advance {halt_on_error:true}` halts on every occurrence including the ones not written here (1.5 blocker 3), so a repeat count in the file is a floor, not a total |
| `warning` | `msg` (≤2000), `overflow?` | first occurrence per exact text per session; repeats are LogRelay's job |
| `dialog` | `count`, `windows`: `[{type,type_full,title?,layer}]`, `opened`: same shape, `letters?` (≤10 labels) | a **force-pausing** modal went up. See below — this is why `advance` stops |

Log hooks attach when AutoRimmer's ctor runs — last in the load order — so
engine-init and earlier-mod load warnings (the bench's SteamAPI.Init line,
notably) never reach the journal. That is LogRelay's beat (it backfills the
pre-ctor log); the journal starts at its `boot` marker.
| `dev` | `verb`, `step`, `target?`, … | provenance of every state-mutating dev action (3.1 owns the type; `journal-selftest` writes it today) |

## Letter timing — the "once per frame" claim was half wrong

The 1.2/1.3 amendments justified hooking `LetterStack.ReceiveLetter` by saying
letters open "once per FRAME". Corrected (1.5 doc correction): in 1.6
`OpenAutomaticLetters` is called from **both** `Game.UpdatePlay` (once per
frame, at the top, before `GameComponentUpdate`) **and**
`LetterStack.LetterStackTick`, which runs inside `DoSingleTick` — so also once
per TICK, and the advance loop drives those ticks itself.

Hooking the arrival funnel was right either way, and for a reason the old
wording obscured: `OpenAutomaticLetters` opens **at most one** letter per call
and `break`s, so a burst still cannot be reconstructed from letter-opens no
matter how often it runs. The per-tick call is also what makes spec 1.7 real —
a letter can open a force-pausing dialog from inside our own tick loop.

## `dialog`, and why `advance` halts on it (spec 1.7)

`LetterStack.OpenAutomaticLetters` — the only thing that opens a timing-out
letter — early-returns for as long as `Find.WindowStack.WindowsForcePause` is
true. Vanilla is fine with that because a `forcePause` window really does pause
the game. AutoRimmer's `advance` pins `CurTimeSpeed = Paused` and calls
`DoSingleTick` itself, so nothing pauses it: a modal stacked mid-advance means
every subsequent trade offer, quest offer and timed threat expires **without
ever being shown**, for the rest of the session, with no exception and no red
error. The run looks healthy and the colony stops being told things.

So:

- `advance` halts with **`reason:"dialog"`** the moment a force-pausing window
  is up — checked per TICK, because a `LetterWithTimeout` opens itself from
  `LetterStackTick`, inside `DoSingleTick`. Reason set is now
  `ticks | timeout | interrupted | letter | threat | alert | event |
  red_error | dialog`.
- **Standing invariant: `advance` returns with an empty force-pause stack, or
  it says so.** When it does not, the result carries `force_pause_windows` in
  the same shape as this event's payload — computed live at halt, so a window
  that went up during the final frame shows up even when the halt reason is
  something else.
- The halt is **not suppressible and does not close anything.** There is no
  honest "plough on" while `OpenAutomaticLetters` is dead, and deciding what a
  dialog means is spec 3.5's job. Journaling it, halting, and leaving the queue
  intact is the whole of 1.7.
- Journaled whether or not an advance is running: a modal going up is a
  first-class event.

## Alert timing — read before asserting on ticks

Alerts have no notify event; the game recomputes them round-robin (1/24th of
all alerts per frame, quest/precept/scenario sweeps every 20 frames) from
`UIRootUpdate`, and the journal diffs the readout's active list every
`alertScanFrames` frames (default 30; `config.json` under the protocol root:
`{"alertScanFrames": N}`, clamped 1–600). So an `alert_on` tick is **when the
scan noticed**, trailing the causing state change by up to a readout cycle plus
a scan cadence — on top of DESIGN's standing point that alerts fire late *by
design* (tattered-apparel means the mood penalty already landed). Assert
windows, not exact ticks. Alerts also start only after game tick 600
(the readout's own warm-up delay).

**Quantified, because "a frame or two" is badly wrong during an advance**
(1.5 doc correction). Both cadences are per FRAME, and an advance runs many
ticks per frame — so the latency in TICKS scales with how fast you are going.
Worst case is 24 frames for the readout's round-robin to reach the alert plus
30 frames for the next scan = 54 frames; at the ~33 ticks/frame a budgeted
advance delivers, that is **up to roughly 800–2000 ticks late**, not a frame
or two. `advance {until:{alert:…}}` therefore halts LATE by that much and
reports the tick it actually halted at. If you need a tight window, lower
`alertScanFrames` (it costs one list diff per frame) — it cannot go below the
readout's own 24-frame sweep.

## Reading it

- `journal` verb: `{"op":"journal","args":{"since_seq":N,"since_tick":T,"types":["letter"],"limit":500}}`
  → `{file,count,truncated,last_seq,events:[…]}` (current session only;
  `limit` caps at 2000, `truncated:true` says there is more).
- Or tail the file directly; it is plain NDJSON with `FileShare.Read`.

## Cost

Hooks are read-only postfixes on rare paths (letters, messages, log calls,
deaths, mental breaks, saves). The only recurring work is the alert diff at the
scan cadence and one queue drain per poller cycle; file I/O happens on the
poller thread, never the main thread.
