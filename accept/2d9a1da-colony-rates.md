# 2d9a1da — colony rates: the game's own series, and a sampler for what it does not keep

Runner: `accept/2d9a1da-colony-rates.py`. Exit 0 pass · 1 fail · 2 fixture gap.

    ./accept/2d9a1da-colony-rates.py             # phases 0-5, needs a bench
    ./accept/2d9a1da-colony-rates.py --selftest  # phase 9, offline, no bench
    ./accept/2d9a1da-colony-rates.py --dry-run   # print the plan, send nothing
    ./accept/2d9a1da-colony-rates.py --phase 6   # save half; then load; then --phase 7

## The finding that reframed the issue

**RimWorld already keeps the graphs. The mod could not see the graph page.**

`RimWorld/HistoryAutoRecorder` has recorded eleven time series since the first
tick of every colony ever played on this bench — wealth (total / items /
buildings / creatures), free colonists, prisoners, average mood, threat points,
adaptation, pop-adaptation, pop-intent. `grep -rn "HistoryAutoRecorder"
Source/AutoRimmer/` returned **nothing** before this round.

The project had already paid for that. To answer "did wealth cause the M1
raids?", session 13 decoded `HistoryAutoRecorder` **out of `Autosave-5.rws` by
hand** — 11 samples at a 30,000-tick cadence, lifted from a save file because no
verb could ask. That decode is now `accept/fixtures/history-autoRecorderGroups-
m1-Autosave-1.xml` and phase 9 grades against it offline.

So the issue is two halves:

| half | what | where |
|---|---|---|
| 1 | read the series the game already keeps | `history` verb, `ColonyRates.cs` |
| 2 | sample what it does **not** — food above all | `trends` verb + `digest.trends`, `ColonySampler.cs` |

There is **no food or nutrition recorder in vanilla**, and food is what a
ten-day survival run dies of.

## What each field is, by provenance

The issue asks that every field be marked game-recorded, digest-already, or a
new derivation. That mark is in the data, not only here: every `trends` series
row publishes `from: "digest.<section>.<key>"`, and `history` publishes
`source: "RimWorld/History.Groups() -> …"`.

| field | provenance |
|---|---|
| `history.series[*]` (11 defs) | **game-recorded**, verbatim `records` |
| `trends.series.food_days` / `food_nutrition` / `food_needers` / `meds` / `steel` / `wood` / `components` / `silver` | **digest-already** (`resources`) |
| `trends.series.power_stored_wd` / `power_gen_w` / `power_draw_w` | **digest-already** (`power`) |
| every `slope_per_day` | **new derivation** — least squares over the window |
| every `days_to_zero` | **new derivation** — `now / -slope` |
| `digest.trends.wealth_per_day` / `threat_points_per_day` / `mood_per_day` / `colonists_per_day` | **game-recorded** series, our slope over it |

Nothing is re-implemented. The sampler calls `DigestVerb.SectionFor` and lifts
named keys; the History reader reads `records` and never calls
`Worker.PullRecord()`.

## The design choices, each with its reason

**Cadence 2,500 ticks (one in-game hour).** Divides the vanilla 30,000-tick
recorders exactly 12:1 so the two series join without interpolation; far above
the hard floor (`ResourceCounter.ResourceCounterTick` updates on `TicksGame %
204 == 0`, so nothing under `resources.*` moves faster than that); 240 samples
is a ten-day run, which is the volume figure the issue itself computed.

**Ring of 240, in memory, preallocated flat arrays.** ~21 KB, zero allocation
per sample after class init. Ten in-game days at the cadence.

**Window 24 points = one in-game day.** A colony's food is periodic on exactly
that period — meals, sleep, hauling, a hunt returning — and a six-point window
reports the slope of lunch. The verb takes `window_points` so a caller can ask
about a different stretch, which phase 3 relies on.

**Least squares, not an endpoint difference.** Stocks move in lumps (a hunt
returns 200 nutrition at once). An endpoint estimate over a window IS one lump
at either end; the regression moves by 1/n. Phase 9 demonstrates it: on a series
whose true slope is −12/day with one +6 lump at the end, the endpoint estimate
reads −5.74 and the regression −10.56.

**Two floors: ≥3 points AND ≥15,000 ticks of span.** The span floor is the one
that matters. Three samples span 5,000 ticks, and turning two in-game hours into
a per-day rate is a 12× extrapolation presented as a measurement. Below it the
answer is `null`, `ready` is false and `not_ready_why` names the floor that was
missed. The first slope of a run therefore arrives at sample 7 — 15,000 ticks,
a quarter of an in-game day.

**`GameComponentTick`, never `GameComponentUpdate`.** Update runs every FRAME
including while paused, so sampling there would append identical rows at a
wall-clock rate while the agent sat thinking and flatten every slope with data
containing no game time. Phase 2 check 2.3 proves it: four wall-clock seconds
paused, zero new points.

**In the digest, ~200 bytes.** The M1 post-mortem's sharpest execution finding
was 27 advances, 10 digests, ZERO journal calls. An indicator behind a verb the
agent must remember to call is an indicator nobody reads.

**A separate durable file, not the journal.** See `JOURNAL.md`'s new section:
emitting a periodic row through `Journal.Emit` would make `722c951`'s
unread-journal refusal fire on every advance.

## Save / load, stated loudly

**The live ring does NOT survive a game boundary. The durable file does.**

The ring is cleared at `Runtime.ResetForGameBoundary` — both detectors — because
a load can move `TicksGame` BACKWARD, and a regression across that seam fits two
timelines at once. Every sample is also appended to
`samples/<sid>.ndjson` as it is taken, and a `boundary` row marks the seam
there, so a post-mortem loses nothing. A running agent loses 15,000 ticks of
warm-up and can SEE that it did: `trends.ready` is false, `points` is small,
`first_tick` is now. Phases 6 and 7 measure exactly this and cannot be automated
end to end (the `b1b3060` precedent — a human saves and loads between them).

## The null trap, which is a hazard and not a feature

`*_to_zero` is null whenever the stock is not falling, and `*_per_day` is null
until the window fills. `StateWatch.One()` refuses an ordering operator against
null: at ARM time that is a clean refusal, but MID-ADVANCE `Poll` returns false
and never halts. So an advance waiting on `trends.food_days_to_zero <= 2` stops
halting the moment food stops falling — the good news — and runs to its timeout.

**Predicates want `*_per_day`.** Phase 5 checks 5.10/5.11 prove the refusal is
real, so this paragraph cannot quietly stop being true.

## Acceptance bullets, mapped

| issue bullet | where | verdict |
|---|---|---|
| a sample row on a defined cadence during `advance`, field set fixed and documented, file readable mid-run | 2.11–2.18 | covered |
| overhead measured against `Journal.cs`'s 0.0039 ms/frame and reported | 2.9, 2.10 | covered — the mod publishes `trends.cost`; the suite reports it and asserts only a < 1 ms ceiling, because a suite cannot measure in-tick cost from outside |
| every field marked game-recorded / digest-already / new derivation | the table above, plus `from` and `source` in the data | covered |
| a series from which `food_days`' SLOPE is computable, shown BEFORE the vanilla alert fires | phase 3 | covered for the slope in both directions (3.3, 3.9, 3.10, 3.11). The "before the alert" half is a NOTE (3.15), not an assertion: `Alert_LowFood.GetReport` cannot fire at all before tick 150,000, so on a young colony the comparison has no lagging side to beat, and on an old one staging a real starvation is a ten-day fixture. **Orchestrator-manual** if a live starvation run is wanted as evidence. |
| zero red errors, no accessor mutates on read, demonstrated with a save-diff around a full sampling window | phase 4 | covered (4.3, 4.4 with the clock stopped; 4.8 across a real sampling window). Zero-red-errors is the standing bench invariant and is the orchestrator's read of the journal, not this suite's. |

## Fixture recipe

- A colony, paused, `devMode = True`.
- **Phase 3 needs a stockpile zone that accepts meals.** `digest.resources.*` is
  stockpile-only, so food on unzoned ground reads as zero nutrition and a series
  staged there never moves. Phase 3 preconditions on
  `dev:spawn-thing {stockpile:true}` reporting `pos_source == "stockpile"` and
  exits 2 with this recipe if it does not.
- Phase 1's boundary check advances up to ~30,000 ticks; phase 3 advances
  ~42,000 across its 16 rounds; phase 4 advances ~15,400. Budget a few minutes
  of Ultrafast per full run.
- Phase 4 leaves `rates-diff-a/-b/-c.rws` on the bench. They are evidence until
  the run is graded; delete them after.
- `--food-def` overrides `MealSimple` if the bench's colony has something better.
