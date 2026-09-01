# Session 13 — live bench proof of the five mod findings

Bench session `20260901T121508`, `_RimWorld-Agent`, `--quicktest`, fresh
250x250 temperate forest map. Assembly `1.0.0+ad29d6d…` (the session-13 build).
Client `rwa 0.2.0`. Run by the orchestrator; no worker launched anything.

This is a SMOKE pass, not the suite. `accept/s13-mod-surface.py` is the suite.

## 0 — the launcher guard fired first

Before any verb, the `--quicktest` refusal added this session was proven on the
real install by staging an `autostart.rws` into `Saves/` and launching:

    refusing: --quicktest cannot run while Saves/autostart.rws exists.
      Map generation WILL fail (Root.checkedAutostartSaveFile race).

Unstaged afterwards. **The bench's `run-agent.sh` was two days stale and had
none of the guard** — it is a COPY, not a symlink. Recorded in PLAY-LOOP.

## A — dev:spawn-thing's journal row carries the refusal

`{"def":"SimpleResearchBench","stuff":"WoodLog","pos":[107,119],"mode":"direct"}`
into solid granite. Envelope `ok:true` — CORRECT, a refusal is information —
`placed:0`, and journal seq 24:

```json
{"seq":24,"type":"dev","payload":{
  "verb":"dev:spawn-thing",
  "target":"SimpleResearchBench x0 (WoodLog) @ 107,119 REFUSED",
  "placed":0,"ids":[],
  "failed":[{"at":[107,119],"reason":"the cell is not walkable",
             "blocker":{"def":"Granite","label":"granite","at":[107,119],
                        "removal":"mine","refused":"the cell is not walkable"}}],
  "placed_is_floor":true}}
```

Compare M1 seq 66, the row this fix exists for:
`SimpleResearchBench x0 (WoodLog) @ 123,117`, `placed:0`, `ids:[]`, and nothing
else. The reason, the blocker and the `REFUSED` suffix are all new, and the
blocker names granite and how to remove it (`mine`).

On a SUCCESSFUL spawn no `failed` and no `blocker` appear — `WhyNoSpawn`
returns null rather than fabricating a cause. Verified on seqs 15, 18, 19.

## J — research-set, both directions

Batteries (needs a bench), on a map with NO bench:

    bench_ok: False    bench_required: None

Then a bench spawned, same call:

    bench_ok: True     bench_required: None

**This is the lie that hid A for five in-game days**, and it is gone. Before the
fix `bench_ok` short-circuited to `true` whenever `requiredResearchBuilding` was
null — which is every project that demands no SPECIFIC bench, i.e. most of them.
`bench_required: null` now means "no specific building demanded"; `bench_ok`
means "an appropriate bench exists". Two questions, two fields.

## K — overshoot_bound comes from the fastest speed

`advance {ticks:300}`:

    ticks_elapsed 318 · overshoot 18 · overshoot_bound 30 · overshoot_bound_speed Ultrafast

`18 <= 30`, the invariant `accept/4.2-play-loop.py` now keys on. Note `overshoot`
IS present here because this is `ticks` mode — which confirms empirically why it
was ABSENT from M1's `until`+timeout envelopes, and why the session-12 brief's
"the envelope publishes overshoot: 1" was false for the advance it named.

## I1 — digest.site

```json
{"biome":"TemperateForest","biome_label":"temperate forest","tile":72908,
 "avg_temp_c":14,"rainfall":1072,"elevation":33,"hilliness":"Mountainous",
 "swampiness":0,"pollution":0,"map_size":[250,250],"pocket_map":false}
```

4.3 asks for a temperate fixture and M1 had to infer it from `map-view` terrain
glyphs. It is now one read. The four skipped lazy/caching getters (`Tile.Biomes`,
`Max/MinTemperature`, `HillinessLabel`, `Landmark`) are confirmed ABSENT, so the
observer-mutation hazard stays closed.

## D — threat-pardon

No-args listing:

```json
{"hostiles":0,"hostiles_pardoned":0,"hostiles_unpardoned":0,
 "pardons_held":0,"candidates":[],
 "note":"a pardon is a recorded decision not to fight, not a filter: `hostiles`
         still counts everything. A pardon lapses on its own when the subject
         wakes (dormant:false)…"}
```

An add with no reason is refused:

    bad-args: 'reason' is required to pardon: a pardon with no stated reason is
    the silent exemption this verb exists to prevent.

`digest.threats` publishes `hostiles_pardoned` and `hostiles_unpardoned` beside
an unchanged `hostiles`. The pardon-with-live-hostiles path needs a map with
hostiles on it and is left to the suite.

## Two findings this pass produced

1. **`data.at` is the TARGET cell, not where the thing landed.** `near` at
   `[108,119]` reported `at:[108,119]` while `spawned[0].at` was `[111,117]` —
   three cells away, silently. `spawned[].at` is the honest read. This is
   `acee526` (1.9) and it now has a live measurement.
2. **An unknown argument is silently ignored and falls back to a default.**
   Passing `at:` instead of `pos:` made `Dev.PosArg` return `Anchor(map)` — the
   first colonist's cell — so three spawns in a row landed at the colony centre
   while reporting success. Same class as `accept/2.5`'s trap 6: a wrong
   argument name here is silent.
