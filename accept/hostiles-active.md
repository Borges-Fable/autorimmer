# Acceptance — `digest.threats.hostiles_active` / `hostiles_dormant`

Implements `RUNS/openrun-20260902/audit/ROUNDS-2.md`, "Round 4 — the chores'
four loose ends" §1.

The worker that wrote this may never launch RimWorld, so this is the acceptance
in envelope form for the orchestrator to drive — by hand, by `rwa`, or by the
raw file protocol (`commands/<id>.json` → `results/<id>.json`).

Every envelope below is `{"op":…,"args":…}`; with python on the box each is
`rwa <op> --args-json '<json>'`.

**No Python harness on purpose.** The RUNLOG's own lesson from the round that
produced 8,093 lines of never-run Python: the offline half of this change is
already asserted by `accept/s13-mod-surface.py` checks **0.6f–0.6i** (added by
this commit), and everything else here needs a live dormant cluster, which is a
bench fixture and not a fixture a script can fake. A numbered list an
orchestrator reads in three minutes beats a driver nobody runs.

**Nothing in this file has met a bench.** Source-level only.

---

## What changed, in one paragraph

`digest.threats.hostiles` counts faction hostility —
`!p.Downed && !p.Dead && p.HostileTo(Faction.OfPlayer)` over
`MapPawns.AllPawnsSpawned`. `GenHostility.HostileTo(Thing, Faction)`'s only
dormancy clause is `IsActivityDormant`, which short-circuits for Anomaly
entities and deliberately keeps a `CompCanBeDormant`-asleep mech hostile, so
`hostiles == 0` is unreachable while a sleeping cluster stands on the map. In
openrun-20260902 all 37 digests from tick 8,793,512 to the wipe read
`hostiles: 5` for a dormant Padenik cluster that was fighting nobody. Two
fields now sit beside it: **`hostiles_active`**, the count over
`map.attackTargetsCache.TargetsHostileToColony` where
`GenHostility.IsActiveThreatToPlayer(t)` and the target is a pawn — the same
predicate `AutoUndrafter.AnyHostilePreventingAutoUndraft` uses for the game's
own auto-undraft — and **`hostiles_dormant`**, the count where the existing
`ThreatPardonComponent.Dormant(p)` helper returns `true`. `hostiles`,
`hostiles_pardoned` and `hostiles_unpardoned` are **untouched**.

**The three counters do not sum, and are not meant to.** `hostiles` and
`hostiles_dormant` walk `AllPawnsSpawned` minus downed and dead;
`hostiles_active` reads the attack-targets cache, which also holds downed
hostiles (excluded again by `ThreatDisabled`) and, unfiltered, turrets and
hives. `hostiles_dormant <= hostiles` always; nothing else is arithmetic.

**`hostiles_active` is nullable.** `null` is the degraded read and is never `0`,
because `0` is the value a fight-over rule acts on. On a live map it is a
number.

---

## Before you start

**devMode = True.** Phases 2–5 are dev-gated.

**It dirties the bench.** Phase 2 spawns a hostile mechanoid; phase 3 fires the
`MechCluster` incident, which drops real cluster buildings on the map; phase 4
wakes it. Phase 6 cleans up. Do this on a scratch save, or save first.

**Every `advance` below needs the two per-call escapes** (git-bug `722c951`) —
`"unread_ok"` and `"through_casualties"`, each with a non-empty reason such as
`"accept/hostiles-active.md: fixture, not a play loop"`. A woken cluster can
also down a colonist, so phase 4's advance is the one that will actually use
`through_casualties`.

---

## Phase 0 — preflight and the shape contract

| # | envelope | expected |
|---|---|---|
| 0.1 | `{"op":"status"}` | `ok:true`, `data.gameLoaded:true` |
| 0.2 | `{"op":"digest"}` | `data.threats` carries **all five** counters: `hostiles`, `hostiles_pardoned`, `hostiles_unpardoned`, `hostiles_active`, `hostiles_dormant`, plus `danger` and `kinds` |
| 0.3 | same envelope | `hostiles_active` is a **number or null**, never a string and never absent |
| 0.4 | same envelope | `hostiles_dormant <= hostiles` |
| 0.5 | same envelope | `hostiles_unpardoned == hostiles - hostiles_pardoned` — the old arithmetic still holds, i.e. this change is additive |
| 0.6 | `./accept/s13-mod-surface.py --selftest` | exit 0. Off-bench; asserts the assertions, not the mod |

Record the baseline five numbers as **B**.

---

## Phase 1 — a quiet colony reads zero active

Run on a map with no live fight. If the colony is mid-raid, advance past it
first and re-take **B**.

| # | envelope | expected |
|---|---|---|
| 1.1 | `{"op":"digest"}` | `threats.hostiles_active == 0` |
| 1.2 | same envelope | `threats.danger == "None"` — the two agree on a quiet map, which is the whole reason this file also asks phase 3 |
| 1.3 | `{"op":"digest"}` again, immediately | **every** `threats.*` field identical to 1.1. An observer that moved a number by being called twice is the bug this project's `PawnSafe`/`WorldSafe` layer exists for |
| 1.4 | `{"op":"journal","args":{"limit":50}}` | **no** warning or red-error row emitted by 1.1–1.3. `TargetsHostileToFaction(null)` would `Log.Warning`; nothing here may |

---

## Phase 2 — an AWAKE hostile moves `hostiles_active`

A freshly generated mechanoid is awake: `CompCanBeDormant.PostPostMake` calls
`WakeUp()` whenever `Props.startsDormant` is false, and the mech races
(`Data/Core/Defs/ThingDefs_Races/Races_Mechanoid.xml`) do not set it.

| # | envelope | expected |
|---|---|---|
| 2.1 | `{"op":"dev:spawn-pawn","args":{"kind":"Mech_Scyther","faction":"mechanoid","count":1,"pos":"<x,z — a visible, unfogged cell>"}}` | `ok:true`. Hold the spawned pawn's thingIDNumber as **M** |
| 2.2 | `{"op":"digest"}` | `hostiles == B.hostiles + 1` |
| 2.3 | same envelope | `hostiles_active == B.hostiles_active + 1` — **the point of phase 2** |
| 2.4 | same envelope | `hostiles_dormant == B.hostiles_dormant` (unchanged) |
| 2.5 | same envelope | `danger` is no longer `"None"` |
| 2.6 | `{"op":"dev:destroy","args":{"thing":<M>}}` then `{"op":"digest"}` | all five back to **B** |

**If 2.1 lands the mech in fogged ground, 2.3 fails legitimately** —
`IsActiveThreatTo` returns false on `Thing.Fogged()` under the default
`canBeFogged: false`. That is correct behaviour, not a defect. Re-spawn in
sight, or `dev:unfog` first.

---

## Phase 3 — a DORMANT cluster is the run's own case

This is the measurement the change exists for: `hostiles` rises, the fight-over
predicate does not.

| # | envelope | expected |
|---|---|---|
| 3.1 | `{"op":"dev:incident","args":{"def":"MechCluster","letter":false}}` | `ok:true`. If a modal went up anyway, dismiss it (`dialog` verbs, spec 3.5) before continuing |
| 3.2 | `{"op":"advance","args":{"ticks":600,"unread_ok":"…","through_casualties":"…"}}` | lets the cluster finish landing |
| 3.3 | `{"op":"digest"}` | `hostiles > B.hostiles` — the sleeping mechs count as hostile, exactly as they always did |
| 3.4 | same envelope | **`hostiles_active == 0`** — the assertion this whole change is for |
| 3.5 | same envelope | `hostiles_dormant == hostiles - B.hostiles` (every new one is dormant), or at minimum `> 0` |
| 3.6 | same envelope | `danger == "None"` — reproduces the run's 31 envelopes reading `danger:"None"` beside a non-zero `hostiles` |
| 3.7 | `{"op":"digest"}` again | every `threats.*` field identical to 3.3–3.6 |

Record these as **C**.

---

## Phase 4 — waking the cluster moves `hostiles_active` off zero

`CompProperties_WakeUpDormant` on the mech races has `wakeUpOnDamage: true`
with a 30-cell radius, so damaging one mech wakes the cluster.

| # | envelope | expected |
|---|---|---|
| 4.1 | `{"op":"pawns","args":{"filter":"hostile"}}` | pick one cluster mech; hold its id as **W** |
| 4.2 | `{"op":"dev:damage","args":{"pawn":"<W>","mode":"amount","amount":5}}` | `ok:true`. `amount` mode stops at downed, so it wakes rather than kills |
| 4.3 | `{"op":"advance","args":{"ticks":600,"unread_ok":"…","through_casualties":"…"}}` | past the wake-up delay (`wakeUpDelayRange` 60–300 ticks) |
| 4.4 | `{"op":"digest"}` | `hostiles_active > 0` |
| 4.5 | same envelope | `hostiles_dormant < C.hostiles_dormant` |
| 4.6 | same envelope | `hostiles == C.hostiles` (nothing died yet), i.e. **the old field did not move while the new pair did** — the single most important row in this file |

---

## Phase 5 — the trigger the round asked for is spellable with no new machinery

`until:{condition:{…}}` addresses any digest field by path, and `edge` defaults
**true**, so this is the `hostiles_active > 0 → 0` edge without a new matcher.

| # | envelope | expected |
|---|---|---|
| 5.1 | `{"op":"advance","args":{"ticks":20000,"until":{"condition":{"path":"threats.hostiles_active","op":"==","value":0}},"unread_ok":"…","through_casualties":"…"}}` | arms without refusal — proves the path is a real predicate address |
| 5.2 | let it run with the cluster awake and the colony defending | halts `reason:"condition"` when the last active threat drops, with `halted_on` naming the path, and `edge:true` in the report |
| 5.3 | the same envelope against the DORMANT cluster (phase 3 state, before 4.2) | with `edge:true` it does **not** halt immediately — the predicate is already true at arm time and the edge has to be seen false first. That is the documented `edge` behaviour (`StateWatch.cs`), not a bug. Use `edge:false` to halt on the first frame |

---

## Phase 6 — cleanup

| # | envelope | expected |
|---|---|---|
| 6.1 | `{"op":"dev:destroy","args":{"things":[<the remaining cluster thingIDNumbers>]}}`, or let the colony finish them | — |
| 6.2 | `{"op":"digest"}` | `hostiles_active == 0`, `hostiles_dormant == 0` |

---

## What this file does NOT prove

- **Anything about an Anomaly activity entity.** `IsActivityDormant`'s second
  branch (`pawn.activity?.IsDormant`) is the one clause `hostiles` already
  handles, and no phase here stages one.
- **The turret case.** `hostiles_active` is filtered to pawns for parity with
  `hostiles`, so a cluster of hostile turrets with no mechs reads
  `hostiles_active: 0`. That is deliberate and it is the one place `danger`
  is not a substitute either (`AffectsStoryDanger` gives a non-mortar turret
  zero combat power). If a fight-over rule ever needs turrets, it needs a
  third field, not a change to this one.
- **Any claim about performance.** The read is a `HashSet` walk plus one
  `IsActiveThreatToPlayer` per hostile pawn, with no pathfind on this call path
  (see `WorldSafe.ActiveHostilePawns` for why the `CanReachUnfogged` clause
  short-circuits) — but nothing here measured it. `advance`'s own per-frame
  predicate cost is published; read it there.
