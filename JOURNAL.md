# Journal schema (spec 1.2)

Append-only NDJSON under save-data `AutoRimmer/journal/`, one file per process
session, named `<sid>.ndjson` — the same `sid` that `status.json` and every
result envelope carry. rwtest (5.1) asserts on these fields; treat them as a
contract and grow them additively.

## Event envelope

```json
{"seq":12,"tick":48210,"wall":"2026-08-30T20:31:04.11Z","by":"game","type":"letter","payload":{…}}
{"seq":13,"tick":48210,"wall":"2026-08-30T20:31:04.19Z","by":"agent","cmd":"advance-203104-8412","type":"action","payload":{…}}
```

- `seq` — monotonically increasing per session file, no gaps at rest (a gap
  means lost writes; the flusher is single-threaded, so this is an invariant,
  not a hope).
- `tick` — game tick. From main-thread hook sites it is exact
  (`Find.TickManager.TicksGame`); from off-main emitters (log hooks on
  background threads, boot-time events) it is the last published snapshot tick,
  accurate to about one frame. Before any game is loaded it is 0.
- `wall` — UTC ISO-8601, always exact.
- `by` — **WHO DID IT** (827c1bf). On **every** row, including the fallback
  line a serializer failure produces. Exactly four values:
  `agent` (executing inside the drain — `VerbRegistry.Execute`, which covers
  both the main-thread drain and the poller-thread one) ·
  `mod` (a chore: work the mod decided to do, with no command behind it and no
  result envelope in front of it) ·
  `game` (inside `Verse/TickManager.DoSingleTick`, whoever set the clock
  running) ·
  `human` (**the default**, and it means precisely "none of the above was
  running when this row was written").

  Five sites DECLARE `mod` rather than inherit, and they are the mod's standing
  observers — code that runs every frame whatever anyone did, where the ambient
  value would be meaningless: the clock observer (`TimeDriver.ClockEmit`), the
  alert scanner (`AlertScanner.Tick`), the name-dialog sweep
  (`TimeDriver.SweepNameDialogs`), `session/boot` and `session/unloaded`.
  Everywhere else the ambient value IS the answer, which is why
  `session/saved` gets correct provenance for free — `agent` for the `save`
  verb, `game` for an autosave (it fires inside the tick), `human` for a hand
  save — and why `session/newgame`/`loaded` stay on the default: somebody
  loaded that save.
  Read once per emit from `Provenance.Current`, a `[ThreadStatic]` set at three
  sites; every existing hook gained the field without being touched. Thread-local
  and not a plain static, because the bridge emits from the main thread, the
  poller thread and whatever thread logged an error, and a shared value would
  cross-attribute all three (`Provenance.cs` carries the argument).
- `cmd` — the command id, present **only** under `by:"agent"`. The join key from
  a journal row back to the result envelope that caused it.
- `type`, `payload` — below. Consumers must ignore unknown payload fields and
  unknown types.

**Why `by` exists.** The audit of run `openrun-20260902` counted at least 22
human interventions the journal marks nowhere — a revival through the debug
menu, a deleted weather event, 411 meals, nine in-game days played by hand
(themes.md T13, T14). Three of that run's 23 `death` rows are debug-menu residue
and one death was reversed with no row for the reversal, so **no count taken
from that journal is a measurement**. A count you can filter by provenance is.

## Types

| type | payload | notes |
|---|---|---|
| `session` | `kind`: `boot` (+`mod`,`game`,`bench`), `newgame`, `loaded`, `saved` (+`file`), `unloaded` | `boot` is always seq 1. `unloaded` is the poller noticing the heartbeat stop (no game means no main thread to notice it); `aborted:N` on any of `newgame`/`loaded`/`unloaded` is how many in-flight commands were answered `no-active-game` at that boundary (1.5 blockers 1–2) |
| `letter` | `def`, `label`, `text?` (≤1500 chars), `target?`, `faction?` | captured at the LetterStack funnel on ARRIVAL — never from letter-open, which opens at most ONE letter per call and so drops bursts (see the correction below) |
| `message` | `text` (≤500), `def` | top-of-screen messages; flash-dedupe rejections are not journaled |
| `alert_on` | `id` (Alert class name), `label`, `priority` | see cadence note |
| `alert_off` | `id`, `label` | label as remembered at `alert_on` |
| `death` | `pawn`, `pawn_id?`, `faction?`, `player`, `kind` | every pawn death during PLAY; mapgen corpse setup is excluded. **`player` is `Faction.IsPlayer` resolved on the main thread** (git-bug 722c951): `advance`'s casualty halt runs on the emitting thread and may not touch Verse, so the faction test has to travel in the payload. `kind` is `colonist`\|`slave`\|`animal`\|`mech`\|`other`, published beside it so a consumer that wants the narrower reading has it without the mod deciding. `pawn_id` is the `thingIDNumber` — the join key that turns the news into a `rescue {pawn,target}` call |
| `downed` | `pawn`, `pawn_id?`, `faction?`, `player`, `kind`, `damage?` | ditto, from `Pawn_HealthTracker.MakeDowned`. Both rows are on the TRANSITION, so a pawn already down when an advance starts emits neither — which is why the casualty halt cannot re-fire for the same pawn |
| `mental_break` | `pawn`, `faction?`, `state`, `causedByMood`, `reason?` | successful starts only, during play |
| `red_error` | `msg` (≤2000) or `msg`+`suppressed:true`, `overflow?` | per-text cap 3 per session, then one suppression marker. **The cap is a FILE policy only** — `advance {halt_on_error:true}` halts on every occurrence including the ones not written here (1.5 blocker 3), so a repeat count in the file is a floor, not a total |
| `warning` | `msg` (≤2000), `overflow?` | first occurrence per exact text per session; repeats are LogRelay's job |
| `dialog` | `count`, `windows`: `[{type,type_full,title?,layer}]`, `opened`: same shape, `letters?` (≤10 labels) | a **force-pausing** modal went up. See below — this is why `advance` stops |
| `clock` | `drove`: `external` \| `mod`, `from`, `to`, `ticks`, `speed` (the FASTEST the span ran at), `speed_at_open`, `frames`, `wall_seconds`, `avg_tps`, `closed_by`: `pause` \| `advance-start` \| `game-boundary` \| `advance-failed`, `advance?` | **game time that moved with no advance in flight** (git-bug 65e7cf9). `TimeDriver.FrameStep` diffs `TicksGame` and `CurTimeSpeed` against the previous frame, before its own `!Active` early-out; the span opens on the first moved tick and closes when `CurTimeSpeed` goes `Paused` or an `advance` arms. The count is frame-exact, not sampled: `Verse/Game.UpdatePlay` runs `TickManagerUpdate()` and then `GameComponentUtility.GameComponentUpdate()` in the same method, so every tick the frame produced already exists when the diff is taken. `drove` is `mod` when an advance was in flight when the span OPENED and `external` otherwise — it is not a claim about whose finger was on the key, and an `unpause` the agent itself sent reads `external`. **It was called `by` until 827c1bf and was renamed with no change of meaning**: `by` now names the row's own provenance everywhere in this file, the two are different questions, and folding them was refused because `external` -> `human` would assert a finger on a key in the one case that provably is not one (the agent's own `unpause`). The row's `by` is `mod`, always, because the mod's clock observer writes it on its own initiative. The same rename lands on `since_last_look.outside[].drove`. **`drove:"mod"` is journaled only when the result those ticks belong to carries no data block**, which is two cases: `closed_by:"game-boundary"` (`Abandon` answers `no-active-game` with `Data = null` — the colony went away underneath) and `closed_by:"advance-failed"` (`FinishFailed`, or a `Finish` whose command was already answered). Every other `mod` span is the advance's own and is fully described by that advance's result — journaling it as well would put a row inside every advance's own `journal_seq` and destroy 722c951's "a quiet colony never pays for this". **`speed` is why the row is not just a tick count**: `TickManager.TogglePaused` restores `prePauseTimeSpeed` and the mod's exit `Pause()` IS a `TogglePaused` from Ultrafast, so the first spacebar tap after an advance runs the colony at ~900 tps (measured 858–887). An `external` row is the ONE new thing that can create a 722c951 read obligation — see `advance`'s `since_last_look` below. |

Log hooks attach when AutoRimmer's ctor runs — last in the load order — so
engine-init and earlier-mod load warnings (the bench's SteamAPI.Init line,
notably) never reach the journal. That is LogRelay's beat (it backfills the
pre-ctor log); the journal starts at its `boot` marker.
| `dev` | `verb`, `step`, `target?`, … (`args?`, `ids?`, `placed?`, `caused_seqs?`, `forbid?`/`forbidden_stacks?`/`not_forbiddable?` — additive; ignore unknown fields) | provenance of every state-mutating dev action. 3.1 owns the type and its `dev:*` verbs are the primary writers; `journal-selftest`, `pawn-fixture` and `world-fixture` write it too (superseded but retained for acceptance replay). A dev verb's RESULT carries `dev.journal_seq` — the join key back to this line; `dev:starter-kit`'s line carries `caused_seqs` for the reverse join, and — since git-bug 091e3f0 — `forbid`/`forbidden_stacks`/`not_forbiddable`, so "the kit left its gear forbidden" is readable from the journal alone |
| `action` | `verb`, `step`, `target?`, … (additive; ignore unknown fields) | provenance of every state-mutating PLAYER action, the non-`dev` twin of the row above. **Since 827c1bf a HUMAN press is one of these too**, with `by:"human"`, `verb:"human"` and a `step` naming which of the game's own input handlers it came through: `gizmo` (`Verse/Command.ProcessInput` — the selected-thing button strip, and every `Designator`, which is itself a `Command`), `float-menu` (`Verse/FloatMenuOption.Chosen`), `dialog-button` (`Verse/DiaOption.Activate`, the `Dialog_NodeTree` funnel), `debug-menu` (`LudeonTK/DebugActionNode.Enter`, leaf nodes only — a node with children is navigation, not an act) and `designator` (`Verse/DesignatorManager.ProcessInputEvents`, keyed on `Event.current.type == EventType.Used`, which is what each of that method's three branches leaves behind). `label` is the game's own label for what was pressed. Every one of the five is guarded on `Provenance.NothingOfOursIsRunning`, which is the definition of `human` rather than a precaution. **Three gaps, measured and not approximated**: (1) `Verse/Dialog_MessageBox`, whose three buttons are inline `Widgets.ButtonText` calls in `DoWindowContents` with no funnel to hook — the alternatives are `DoWindowContents` (every OnGUI frame, and a postfix cannot tell which button ran) or `Widgets.ButtonText` (every button in the whole UI, every frame); (2) the `DebugActionType.ToolMap`/`ToolWorld`/`ToolMapForPawns` second step, where `Enter` only arms `DebugTools.curTool` and the effect lands on the next map click through a delegate with no member of its own — the arming still produces a row carrying `action_type`, and the effect still reads `by:"human"`; (3) six of the 23 vanilla `ProcessInput` overrides do not call base — `Designator_Build`, `Designator_Install`, `Designator_Dropdown`, `Designator_Paint`, `Designator_MechControlGroup` and one gizmo inside `Comp_AtmosphericHeater` — so PICKING one of those tools writes no `gizmo` row. USING it does, through the `designator` hook. Written by `designate`/`forbid`/`flick` (DesignationVerbs), the area brushes (AreaVerbs), pawn orders (PawnActs), storage edits (StorageVerbs), zone edits (ZoneVerbs), `threat-pardon` and — since git-bug 280fb78 — `alert-mute`, whose row carries `step` (`mute`\|`unmute`\|`unmute-all`), the `ids` and the required `reason`; `advance` writes one too, `step:"escape"`, naming `unread_ok`\|`through_casualties`\|`through_news`. And — since session 16 — `build`, whose row carries `placement_id`, `def`, `at`, `rot`, `footprint`, `gate` and `thing_id`. `place-layout` and `cancel-layout` write it too, and `place-layout` writes **ONE row for the whole transaction** rather than one per element: it carries `layout_id`, `mode`, `origin`, `rect`, `requested`/`placed`/`skipped`, `rolled_back` and a `placements` array holding every placement id in the layout. One row and not N because a 66-element layout would bury the journal, while the durability those ids need is satisfied by one row that names them all — and a rolled-back call still writes its row, with `rolled_back: true`, because "we placed nothing and here is why" is provenance too. **This type shipped in spec 3.2 and was never listed here**; the omission was found in session 16 while adding the `construction` row, and five verbs had been writing an undocumented type for four sessions. `temp-set` (TemperatureVerbs, git-bug 261f2e9) writes one row per CALL rather than per building, carrying `target_c` and a `targets` array of `{id, def, before_c, after_c}` — because "what did this colony tell its coolers to hold, and when" is one decision even when it touches four buildings, and the before/after pair is what 261f2e9's last acceptance bullet asks the journal for. Its `step` is the target in INVARIANT culture with a `C` suffix (`-10C`), never the ambient locale's decimal comma. The verb's RESULT carries `journal_seq`, the join key back to the line. `nepo-order` (NepoVerbs, the Nepo soft dependency) writes one row per CALL — one order is one decision even when it names four goods — carrying `cost`, `charged`, `balance_before`, `balance_after`, `unlimited_money`, `instant_delivery`, `arrival_tick`, `delay_ticks`, a `lines` array of `{kind, def, count, unit_price, line_total}` and the `negotiator`. **`charged` is MEASURED, not computed**: it is the movement of the balance, or under `unlimitedMoney` the movement of `spentWhileUnlimited`, because Nepo's own `PlaceOrder` returns `void` and can silently drop a manifest line. A dry run and every refusal write NO row — nothing was spent — and the two read verbs (`nepo-catalog`, `nepo-inbound`) never write one at all. |
| `construction` | `kind`: `completed` \| `failed`, plus `def`, `at`, `stuff?`, `rot?`, `worker`, `thing_id?`, `placement_id?` | the two Frame transitions, as POSITIVE events, from Harmony postfixes on `Frame.CompleteConstruction` and `Frame.FailConstruction`. They exist because **completion is an absence**: a finished build leaves no blueprint and no frame, and neither does a cancelled one, so without these rows the two are the same nothing (git-bug d7c8088). `failed` is NOT a cancellation — `FailConstruction` respawns the blueprint and a pawn tries again. `placement_id` is present only for a build THIS session placed through `build`; its absence means the blueprint was drawn by the player or came out of a save, which is different from a null id. `thing_id` is null for a TerrainDef, which sets the grid and produces no Thing. |
| `destroyed` | `kind`: `building` \| `frame` \| `corpse`, plus `def`, `thing_id`, `at`, `mode` (a `Verse/DestroyMode` name), `player`, `faction?`, `label?`, `builds?` | **something the colony had is gone** (827c1bf, absorbing a8d8ada and the building half of f1a1700). Harmony postfixes on `Verse/Building.Destroy`, `RimWorld/Frame.Destroy` and `Verse/Corpse.Destroy`. **`Thing.Destroy` is NOT patched** — it is on every stack merge, every bullet and every hauled item, and a8d8ada's caution stands. `Frame : Building` and `Frame.Destroy` calls `base.Destroy(mode)`, so the building hook hands frames to the frame hook or every frame would produce two rows. Buildings and frames are **player faction only** (`Faction.IsPlayer`, resolved on the main thread and carried in `player` because the halt tap may not touch Verse) — which is how mining a mountain stays off this row; corpses are journaled whatever their faction. `mode` is the game's own word and nothing is inferred from it. `builds` is a frame's `entityDefToBuild` — what was actually lost, since a frame's own def is `Wall_Frame`. No map id: `Destroy` sets `mapIndexOrState = -2` so `Thing.Map` is null in a postfix, while `Position` (`positionInt`) and `Faction` (`factionInt`) are plain fields and survive. **It stops a running advance** the way a casualty does — see `advance`'s `reason:"loss"` and its `through_losses` escape — except for a corpse, which never halts, and except for the five `DestroyMode` values that are the colony's own decision arriving as planned (`Deconstruct`, `WillReplace`, `Cancel`, `Refund`, `FailConstruction`). The row is written in all of those cases regardless; only the halt is filtered. **The fact behind it**: a manhunter pack destroyed two turrets and an autocannon in run `openrun-20260902`, the journal recorded nothing, the agent rebuilt the two it had counted and never learned the third was gone — its own account names that as the first link in the chain that ended the colony (themes.md T3, F-S12-13/14) |

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

## `clock`, and `advance`'s `since_last_look` (git-bug 65e7cf9)

The audit of run `openrun-20260902` recomputed the ticks that moved outside any
returned advance as an interval union of `[tick − ticks_elapsed, tick]` rather
than as a sum of `state.tick` deltas: **1,613,739 ticks, 15.3% of the run**, of
which **89.6% was Dorian playing the colony by hand for nine in-game days**.
659 of 659 returned advances ended paused and none refused a pause, so the mod
never left the clock running — somebody else did, legitimately, and nothing in
the record said so. `clock` rows are the record saying so. **There is no
auto-pause anywhere in this**; the mod observes the clock and never sets it
outside an advance.

`advance`'s result gained a sibling to `journal_seq`:

    since_last_look: {
      ticks,             # every game tick since the last screen this mod delivered
      in_this_advance,   # == ticks_elapsed
      outside: [{from, to, ticks, drove, speed}],   # capped at 20 (`drove` was `by` before 827c1bf)
      outside_ticks, outside_spans,              # whole, uncapped
      journal_seq: [lastScreenSeq+1, endSeq]     # [] when nothing was journaled
    }

`outside` holds a span only when its ticks reach no result envelope of their
own, which is arithmetic rather than taste: `ticks` is
`outside_ticks + in_this_advance`, so a `mod` span whose advance DID report its
ticks would be counted twice. One such closes mid-advance whenever the clock is
paused with an advance still armed — a human on the spacebar, a `pause` verb, a
discharged pause debt — and every tick of it is already `in_this_advance`. The
`mod` spans that DO appear here are the ones whose own result carried no data
block (`closed_by` `game-boundary` or `advance-failed`); the window is also not
consumed by such a result, so the next one that CAN carry a `since_last_look`
reports it.

`journal_seq` (the older field) starts at the advance's own ARM point.
`since_last_look.journal_seq` starts at the last SCREEN — the last result this
mod handed back, or the highest seq a `journal` call has served, whichever is
later. The difference between the two marks is the window in which nobody was
at the wheel, and rows journaled in it — `(lastAdvanceEndSeq, startSeq]` — were
claimed by **no** advance's `journal_seq`. Measured on that run's own spine
(`RUNS/openrun-20260902/audit/spine.ndjson`): **404 gaps between consecutively
published advance ranges, holding 2,973 journal rows** — 93 of them `death`,
`downed` or `letter`. Tony's downing and death (seq 1201, 1267), Tanya's (1779,
1789) and two `ThreatBig` letters (`Shamblers approach` 1803, `Raid: Nyararm
Mechhive` 3103) are all in that list.

Those rows were also **gated by nothing**: `TimeDriver.Start` refuses on
`ReadWatermark < lastAdvanceEndSeq`, and `lastAdvanceEndSeq` only moves when an
advance journals something of its own — so a silent advance after a human play
window left the window unread and unrefused forever. It now also moves to the
seq of the `clock` row that closed the human window, which is at or above
everything that window produced. **Only that** — an advance does not create an
obligation out of the agent's own `action` rows, which are emitted while it is
at the wheel with the game paused and which it got a result envelope for.

A human play window therefore costs **at most one** `unread-journal` refusal,
whose type breakdown names `clock` beside the deaths — and it lands one turn
late by construction: the `clock` row is written when the NEXT advance arms, so
that advance cannot have read it and is not refused on it. The advance after it
is. The obligation exists from the first teardown that follows, so the lag is
one turn and never more.

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
  → `{file,count,truncated,last_seq,read_watermark,watermark_was,watermark_moved,unread_after,filtered,events:[…]}`
  (current session only; `limit` caps at 2000, `truncated:true` says there is
  more).
- Or tail the file directly; it is plain NDJSON with `FileShare.Read`.

**THE VERB IS ALSO A COMMITMENT** (git-bug 722c951). Calling it moves a
per-bench READ WATERMARK, and `advance` refuses (`ok:false`,
`error.code:"unread-journal"`) while the previous advance's delta sits below it.
Nothing else moves the watermark — not `digest.changed`, which is a count per
type and cannot name the pawn, and not the advance's own `journal_seq` echo,
which is precisely what run `m1-20260831` was handed and ignored. Tailing the
file directly does NOT move it either: the file is the same bytes, but the mod
cannot see a `cat`.

How far it moves: to `last_seq` for an unfiltered, untruncated read; otherwise
to the highest seq actually RETURNED. So `journal {types:["letter"]}` does not
discharge a `downed` it never asked for, and `unread_after` says what is left.
The watermark is one global value, not per client — the command envelope carries
no client identity to key on (`Poller.ScanInbox` reads `id`/`op`/`args`, and
`rwa`'s ids are per-CALL), so a second client reading the journal clears the
first client's obligation. `read_watermark` is published on every read so that
is visible rather than silent; `Journal.cs`'s header carries the upgrade path.

## Cost

Hooks are read-only postfixes on rare paths (letters, messages, log calls,
deaths, mental breaks, saves, and — since 827c1bf — building/frame/corpse
destruction and the five human input handlers). The only recurring work is the
alert diff at the scan cadence and one queue drain per poller cycle; file I/O
happens on the poller thread, never the main thread.

Two of 827c1bf's hooks are not on rare paths and are called out rather than
buried:

- **`Verse/TickManager.DoSingleTick`** carries the tree's only Harmony PREFIX
  (plus a Finalizer, so a throw out of the tick cannot leave the provenance
  stuck). Both bodies are one write to a `[ThreadStatic]`; the cost is Harmony's
  wrapper, tens of nanoseconds against a tick that is hundreds of microseconds
  of work on the 38-mod bench — order 0.01% of one core at the ~900 tps ceiling,
  and no allocation. It is there because since 1.8 the mod does not call
  `DoSingleTick` at all (the game's own `TickManagerUpdate` does), so a bracket
  around the mod's call sites would label a starvation death during a human's
  play window `human`, which is false.
- **`Verse/DesignatorManager.ProcessInputEvents`** is on the OnGUI path, so its
  postfix runs a few times per frame whatever the player is doing. Its first
  statement is the `[ThreadStatic]` read and its second a null check; nothing
  allocates unless a click was actually consumed.

## What is NOT here: periodic samples (git-bug 2d9a1da)

The journal is **events-only, by design** — discrete things that happened, each
with a tick and a seq. It can say "LowFood fired". It cannot say "food has
fallen 1.5/day for six days". So the colony sampler writes somewhere else:

    <save data>/AutoRimmer/samples/<sid>.ndjson

Same `sid`, same shape of file, a `header` row declaring the column set and then
one `sample` row per 2,500 game ticks plus a `boundary` row at every game
boundary. `ColonySampler.cs`'s header is the schema; the `trends` verb publishes
the file's path as `durable_file`.

**It is a separate file and that is not tidiness.** Emitting a periodic row
through `Journal.Emit` would have been fewer lines and would have broken
`722c951`: the unread-journal refusal rests on "an advance that journaled
NOTHING creates no obligation, so a quiet colony never pays for this at all"
(`TimeDriver`'s own header). A row emitted from inside the tick loop means every
advance longer than one cadence journals something, so every subsequent advance
refuses — which turns "your colony has news you have not read" into "time
passed", and that is the guard's failure mode rather than its purpose.
