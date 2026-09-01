# Acceptance — `assign {auto_arm}` (git-bug `1a072fa`)

The worker that wrote this may never launch RimWorld, so this is the acceptance
in envelope form for the orchestrator to drive — by hand, by `rwa`, or by the
raw file protocol (`commands/<id>.json` → `results/<id>.json`, ids kept to
`[A-Za-z0-9-]` so `Poller.Sanitize` leaves them alone).

Every envelope below is `{"op":…,"args":…}`; with python on the box each is
`rwa <op> --args-json '<json>'`.

**Every `advance` below needs the two per-call escapes** (git-bug `722c951`).
`advance` now refuses with `error.code:"unread-journal"` when the previous
advance journaled events no `journal` call has read, and halts with
`reason:"casualty"` on an own-faction downing or death. Both are right for a
play loop and wrong for a scripted fixture, which advances to move state and
never reads the journal in between — so add to every advance envelope below:

```json
"unread_ok": "<this file>: fixture, not a play loop",
"through_casualties": "<this file>: fixture, not a play loop"
```

or `--unread_ok:str '<why>' --through_casualties:str '<why>'` on the `rwa`
line. The reason is REQUIRED and non-empty, and the mod journals it as an act.
The `.py`/`.ps1` twin of this file does it in one `advance()` wrapper; by hand
it has to go on each call. Nothing else about the steps changes.

## Fixture

| needs | why | stage it with |
|---|---|---|
| **FindSuitableWeaponAndAmmo active** (`dorian.findsuitableweaponandammo`) | the whole lever | already in `profile/make-profile-agent.sh` line 97 and the `.ps1` modlist |
| ≥ 1 visible colonist **capable of Violence** | the checkbox is not drawn for a pawn who is not (`PawnColumnWorker_AutoArm.HasCheckbox`) | any colony; this pawn is **A** |
| ≥ 1 visible colonist **INCAPABLE of Violence** | phase 3's gate check. **Skip phase 3 with a NOTE if the colony has none** — it is not stageable with `dev:*` (no trait/backstory verb exists) and its absence is not a spec failure | a colony that happens to have one; check `pawn {sections:["work"]}` `disabled` |
| A holds a weapon | phase 4 drops it to make the pawn unarmed | any armed colonist, or `dev:spawn-thing` + `equip` |
| **A is UNDRAFTED, not downed, in no mental state, spawned on the current map, and capable of Manipulation** | phase 4 only. Every one of these is a clause of FSWA's own `FSWA_MapComponent.CollectEligiblePawns`, and each one silently drops A from the arming pass — the opt-in still reads `true` and nothing ever happens | undraft before 4.6; check `pawn {sections:["state","health"]}` |
| **`allowSwapWhileDrafted` is OFF** (FSWA's default) | the same clause. If the orchestrator has ever turned it on, a drafted A would still arm and phase 4 proves less than it looks | FSWA mod settings; leave at default |

**Session-8 lesson, applied.** The rows above are the ones `accept/3.4-pawn-orders.md`
was missing by omission and each cost a full bench cycle. They are FSWA's pass
gate, not the checkbox gate — the checkbox is drawn (and `assign` accepts the
lever) for a drafted, downed or manipulation-incapable colonist, exactly as it
is in the game. Phase 4 is the only phase they touch; phases 1–3 do not care.

Nothing here needs a fixture verb that does not already ship.

---

## Phase 0 — preflight

| # | envelope | expected |
|---|---|---|
| 0.1 | `{"op":"status"}` | `ok:true`, `data.gameLoaded:true`, **no** `data.forcePause` |
| 0.2 | `{"op":"pawns","args":{"filter":"colonist"}}` | ≥ 1 entry. Pick **A** = a colonist with Violence enabled. **Do not key on roster index** — `pawns` order is not stable (see `accept/3.4-pawn-orders.md`); pick by name and hold the id |
| 0.3 | `{"op":"journal","args":{"limit":1}}` | `data.last_seq` becomes `seq0` |

---

## Phase 1 — **read auto-arm without setting it**

This is the acceptance bullet "`pawn`'s state or assign's `after` publishes
current auto-arm state so it can be checked without setting it". Both routes
ship, and both must answer.

| # | envelope | expected |
|---|---|---|
| 1.1 | `{"op":"pawn","args":{"id":A,"sections":["area"]}}` | `area.auto_arm` is a **bool** (`false` on a fresh colony), **not null**, and there is **no** `area.auto_arm_unknown` key. The observer route |
| 1.2 | `{"op":"assign","args":{"pawns":[A],"med_care":"Best"}}` | `data.levers:["med_care"]` — `auto_arm` is **not** in `levers`; `accepted[0].applied:["med_care"]`; and **`accepted[0].before.auto_arm` and `.after.auto_arm` are both present and equal** to 1.1's value. Nothing was set. There is **no** `data.auto_arm_mod` block, because nothing about the value is ambiguous |

> A `null` in 1.1 with `auto_arm_unknown` present means the mod is not loaded or
> its API drifted — read the reason, it names which. That is phase 5, not a
> failure of phase 1, but it invalidates phases 1–4.

---

## Phase 2 — **the lever, both ways, read back from the tracker**

| # | envelope | expected |
|---|---|---|
| 2.1 | `{"op":"assign","args":{"pawns":[A],"auto_arm":true}}` | `counts.accepted:1`, `counts.rejected:0`; `accepted[0].applied` contains `"auto_arm"`; `accepted[0].refused:[]`; `before.auto_arm:false`; **`after.auto_arm:true`**; `data.levers:["auto_arm"]`; `data.auto_arm_mod.loaded:true`, `.tracker:true`, `.mech_route:true`; `action.journal_seq ≥ 1` |
| 2.2 | `{"op":"pawn","args":{"id":A,"sections":["area"]}}` | `area.auto_arm:true` — the actor and the observer agree, one vocabulary |
| 2.3 | `{"op":"assign","args":{"pawns":[A],"auto_arm":true}}` | applied again, `before.auto_arm:true`, `after.auto_arm:true`. Setting a value it already holds is a no-op in FSWA (`HashSet.Add` answers false) and is reported as applied, exactly like re-assigning the current med care |
| 2.4 | `{"op":"assign","args":{"pawns":[A],"auto_arm":false}}` | `applied` contains `"auto_arm"`; `before.auto_arm:true`; **`after.auto_arm:false`**. Round-trips |
| 2.5 | `{"op":"journal","args":{"since_seq":seq0,"types":["action"],"limit":200}}` | ≥ 3 rows with `payload.verb:"assign"`, each `payload.levers` containing `"auto_arm"` |
| 2.6 | `{"op":"journal","args":{"since_seq":seq0,"types":["red_error"]}}` | `data.count:0` — the standing invariant |
| 2.7 | `{"op":"journal","args":{"since_seq":seq0,"types":["warning"]}}` | **no** warning whose text starts `[AutoRimmer] FSWA`. Those are emitted only when a reflection call throws or the API drifted; one here means the bridge is fabricating and every value above is suspect |

**The read-back is the point of 2.1 and 2.4.** `AutoArmTracker.SetAutoArm` is
`void` and returns silently on a null tracker, so the verb sets, then calls
`IsAutoArm`, then compares. If they disagree the pawn lands in `rejected` with
`refused:[{lever:"auto_arm", reason:"the write did not take — …"}]` and a
diagnosis, never in `accepted`.

---

## Phase 3 — **composition, and a refusal that does not take the call down**

| # | envelope | expected |
|---|---|---|
| 3.1 | `{"op":"assign","args":{"pawns":[A],"auto_arm":true,"med_care":"Best","hostility":"Attack"}}` | `data.levers:["med_care","hostility","auto_arm"]` (that order — it is the order the code applies them in); `accepted[0].applied` contains all three; `after.auto_arm:true`, `after.med_care:"Best"`, `after.hostility_response:"Attack"`. **One call, three columns, one journal row** |
| 3.2 | with **V** = a colonist incapable of Violence: `{"op":"assign","args":{"pawns":[V],"auto_arm":true,"med_care":"Best"}}` | **V is in `accepted`, not `rejected`** — `applied:["med_care"]` and `refused:[{"lever":"auto_arm","reason":"this pawn is incapable of Violence: the Assign-tab checkbox is not drawn (PawnColumnWorker_AutoArm.HasCheckbox) and the gizmo is disabled with FSWA_CannotViolent"}]`; `after.med_care:"Best"`; `after.auto_arm:false`. The refused lever did not cost the applied one. **Skip with a NOTE if the colony has no such pawn** |
| 3.3 | `{"op":"assign","args":{"pawns":[A],"auto_arm":"yes"}}` | `ok:false`, bad-args, `arg 'auto_arm' must be a bool`. `VerbArgs` refuses a coerced string on purpose |
| 3.4 | `{"op":"assign","args":{"pawns":[A]}}` | `ok:false`, bad-args, and the message lists `auto_arm` among the levers |
| 3.5 | red-error sweep | `data.count:0` |

---

## Phase 4 — **the demonstration: an unarmed colonist arms itself, with no `equip` order**

The issue's own bullet. **This tests FSWA, not AutoRimmer** — all this verb owes
is the opt-in — but it is the only check that proves the opt-in reached the mod
rather than a field this repo owns.

| # | envelope | expected |
|---|---|---|
| 4.1 | `{"op":"assign","args":{"pawns":[A],"auto_arm":false}}` | reset to a known state; `after.auto_arm:false` |
| 4.2 | `{"op":"pawn","args":{"id":A,"sections":["equipment"]}}` | records the BEFORE weapon; A is armed |
| 4.3 | `{"op":"drop","args":{"pawns":[A]}}` then `advance {ticks:200,max_tps:600}` | the primary is on the ground. `pawn {sections:["equipment"]}` shows **no primary** |
| 4.4 | `{"op":"things","args":{"category":"weapons","detail":true}}` | the dropped weapon is listed; its id becomes **W**. It is **FORBIDDEN** — see below |
| 4.5 | `{"op":"unforbid","args":{"things":[W]}}` | **REQUIRED, not belt and braces.** Expect `ok:true`. Skipping this makes phase 4 fail and look like the lever is broken |
| 4.6 | `{"op":"assign","args":{"pawns":[A],"auto_arm":true}}` | `after.auto_arm:true` |
| 4.7 | `{"op":"advance","args":{"ticks":5000,"max_tps":600}}` | `data.reason:"ticks"` |
| 4.8 | `{"op":"pawn","args":{"id":A,"sections":["equipment"]}}` | **A holds a weapon again.** If not, advance a documented further 10000 and say which window it took — FSWA re-arms on its map component's own scan cadence, not on a tick we control |
| 4.9 | `{"op":"journal","args":{"since_seq":seq0,"types":["action"],"limit":300}}` | **no row with `payload.verb:"equip"`.** The pawn armed itself. This is the whole bullet |
| 4.10 | red-error sweep | `data.count:0` |

**4.5 IS LOAD-BEARING, and the first draft of this note had the reason exactly
backwards.** It claimed a colonist's own drop lands unforbidden. It does not.
`Verse/Pawn_EquipmentTracker.cs TryDropEquipment(eq, out resultingEq, pos,
bool forbid = true)` calls `resultingEq.SetForbidden(forbid, warnOnFail: false)`,
and `RimWorld/JobDriver_DropEquipment.cs` — the driver behind the `drop` verb's
`JobDefOf.DropEquipment` — passes only three arguments, so **`forbid` takes its
default of `true`.** An ordered drop lands FORBIDDEN. FSWA then skips it:
`FSWA_MapComponent.CollectCandidates` excludes any weapon where
`ForbidUtility.IsForbidden(thing, Faction.OfPlayer) || FireUtility.IsBurning(thing)`.
So without 4.5 there are zero candidates, the pass ends at "no candidate
weapons on the map", A stays unarmed, and phase 4 reads as a broken lever.

The one vanilla path that does un-forbid is `Pawn_EquipmentTracker.MakeRoomFor`,
which explicitly calls `dropped.SetForbidden(value: false)` on the weapon it
displaces — so staging phase 4 with an `equip` of a second weapon instead of a
`drop` would leave the old one usable. `drop` does not.

**The standing order that comes with this feature** (the issue says so, and 4.4
is where it bites): raider drops land FORBIDDEN too —
`Pawn_EquipmentTracker.DropAllEquipment(pos, forbid = true)` — FSWA skips
forbidden weapons, and a direct `equip` order bypasses forbidden, so the manual
path works while the autonomous one silently does nothing. The play loop (4.2)
must `unforbid` after every raid. Not this issue's code, but the reason this
issue's feature will look broken if it is skipped.

**Cadence, so 4.7's tick count is not a guess.** `FSWA_MapComponent
.MapComponentTick` runs a pass on `Gen.IsHashIntervalTick(map,
FSWAMod.Settings.evalIntervalTicks)` — default **600** — and only when something
changed; `AutoArmTracker.SetAutoArm` calls `MarkPawnDirty(pawn)` on opt-in, which
is exactly that change, so 4.6 arms the next pass. There is also a forced floor
of 7500 ticks. 5000 ticks therefore covers several passes plus the walk to the
weapon; if A is still unarmed at 4.8 the cause is an eligibility clause from the
fixture table, not the window.

---

## Phase 5 — **absent mod (OPTIONAL, and it costs a game restart)**

The bench ships FSWA active, so this cannot be checked in the same session as
phases 1–4. Run it only if the orchestrator wants the absence path exercised:
disable **Find Suitable Weapon And Ammo** in the bench's `ModsConfig.xml`,
restart, load, then:

| # | envelope | expected |
|---|---|---|
| 5.1 | `{"op":"assign","args":{"pawns":[A],"auto_arm":true,"med_care":"Best"}}` | `ok:true` — **the verb does not fail.** A is in `accepted` with `applied:["med_care"]` and `refused:[{"lever":"auto_arm","reason":"FindSuitableWeaponAndAmmo (dorian.findsuitableweaponandammo) is not loaded — no type FSWA.AutoArmTracker in any loaded assembly. …"}]` |
| 5.2 | same result | `data.auto_arm_mod.loaded:false` with `.reason` naming the package id; `accepted[0].before.auto_arm:null` and `.after.auto_arm:null` — **null, never false** |
| 5.3 | `{"op":"pawn","args":{"id":A,"sections":["area"]}}` | `area.auto_arm:null` **and** `area.auto_arm_unknown` carrying the same reason |
| 5.4 | red-error sweep | `data.count:0` — an absent optional mod is not an error |

Put `ModsConfig.xml` back afterwards.

---

## What this acceptance deliberately does NOT cover

- **A weapon-capable mechanoid.** The gate admits one (the gizmo's route,
  `MechUtility.IsWeaponUsableMech`), but it needs Fortified Framework / DMS on
  the bench and a mech that implements `Fortified.IWeaponUsable`. Unexercised;
  the failure mode if the branch is wrong is a refusal with a named reason, not
  a bad write.
- **Multiplayer.** `AutoArmTracker.SetAutoArm` is itself the registered sync
  site, so calling it directly is the correct and only route — but MP's prefix
  fires only in interface context, and AutoRimmer's verbs run from
  `GameComponentUpdate`, which is not. Under MP this write would stay local.
  Out of scope for a single-player agent bench; recorded so nobody rediscovers
  it.
- **API drift.** The bridge refuses the lever and journals a warning when
  `IsAutoArm`/`SetAutoArm` no longer match their signatures. Reachable only by
  editing FSWA, so it is verified by reading, not by running.
- **FSWA loaded with its `AutoArmTracker` GameComponent missing.** Reachable
  only if `Game.FillComponents` fails to instantiate it, which also logs a red
  error, so it cannot be staged deliberately. The bridge now answers `null` plus
  a reason for that case instead of the fabricated `false` FSWA's own
  `IsAutoArm` returns (`Get?.optedIn.Contains(pawn) ?? false`), and journals
  `[AutoRimmer] FSWA auto-arm READ is UNKNOWN, not off` — which 2.7 already
  sweeps for. Verified by reading, not by running.

## A note on the independent second reader

Sessions 2.2 and 2.4 established "save the game and read the save's Scribe XML"
as the second reader that makes a claim credible, and DESIGN's 2026-08-31 entry
records the one case where that reader perturbs what it measures. Auto-arm is
mostly safe for it: `AutoArmTracker.ExposeData` scribes `optedIn` as
`LookMode.Reference`, so an opted-in pawn appears in the save under
`<optedIn>`. But `ExposeData` runs `Purge()` during the **Saving** pass, which
drops every entry whose pawn is null, `Dead` or `Destroyed` — a write-on-SAVE of
the same class as `Bill.ExposeData`. So the XML is a valid second reader for a
LIVE pawn's opt-in and is **not** one for a dead pawn's: the save will show it
opted out because saving removed it, and the live model would have shown the
same, since `Purge` mutates the in-memory set. Do not read a discrepancy there
as a bridge fault.
