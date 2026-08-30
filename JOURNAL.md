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
| `session` | `kind`: `boot` (+`mod`,`game`,`bench`), `newgame`, `loaded`, `saved` (+`file`) | `boot` is always seq 1 |
| `letter` | `def`, `label`, `text?` (≤1500 chars), `target?`, `faction?` | captured at the LetterStack funnel on ARRIVAL — never from letter-open, which runs once per frame and drops bursts under fast-forward |
| `message` | `text` (≤500), `def` | top-of-screen messages; flash-dedupe rejections are not journaled |
| `alert_on` | `id` (Alert class name), `label`, `priority` | see cadence note |
| `alert_off` | `id`, `label` | label as remembered at `alert_on` |
| `death` | `pawn`, `faction?` | every pawn death during PLAY; mapgen corpse setup is excluded; filter by `faction` |
| `downed` | `pawn`, `faction?`, `damage?` | ditto |
| `mental_break` | `pawn`, `faction?`, `state`, `causedByMood`, `reason?` | successful starts only, during play |
| `red_error` | `msg` (≤2000) or `msg`+`suppressed:true` | per-text cap 3 per session, then one suppression marker |
| `warning` | `msg` (≤2000) | first occurrence per exact text per session; repeats are LogRelay's job |

Log hooks attach when AutoRimmer's ctor runs — last in the load order — so
engine-init and earlier-mod load warnings (the bench's SteamAPI.Init line,
notably) never reach the journal. That is LogRelay's beat (it backfills the
pre-ctor log); the journal starts at its `boot` marker.
| `dev` | `verb`, `step`, `target?`, … | provenance of every state-mutating dev action (3.1 owns the type; `journal-selftest` writes it today) |

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
