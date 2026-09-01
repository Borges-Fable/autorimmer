# s21 orchestrator smoke — the session-20 surface meets a bench for the first time

Bench `_RimWorld-Agent`, session `20260901T205859`, `--quicktest` map, game
1.6.4871 rev600, assembly `1.0.0+52606d1` (the artifact `db08b3d` carries).

This is **not** an acceptance suite and does not claim to be one. It is the
orchestrator proving, before any suite was written, that `61794cd` and
`40ed42f` emit anything at all — because both shipped in session 20 without
touching a bench, and a suite built on a dig path that does not exist goes green
while asserting nothing. Raw envelopes are banked beside this file; every number
below was read back out of them, not out of a runner's summary line.

## `digest.work_coverage` — live at tick 1291 on a colony 7 days old

`ok:false`, `under:["Doctor"]`, twelve rows. **Doctor `have:1` against
`floor:2`** on a brand-new quicktest colony — the M1 death condition, visible
from birth, which is the entire argument of `40ed42f`. The row carries the full
diagnosis (`floor_on:"available"`, `floor_by:"autorimmer:one-doctor-is-zero-doctors"`,
`short_by:1`, `capable:3`, `enabled:1`, `available:1`, `available_pawns:["Ark"]`)
and two ranked `candidates` with skill and passion; the eleven FINE rows carry
three fields each, so the cap only ever drops rows that are fine.

`Diplomat` appears with `floor:1` and is not vanilla — a modded work type that
sets `requireCapableColonist` is picked up free, exactly as the decision claimed.

## `61794cd` — the headline, and it is the M1 failure reproduced and fixed

One pawn (Pattie, 362), four reads, banked as `05`/`07`/`09`/`11`.

| read | hediffs_total | rows | dropped | BloodLoss row | severity | life_threatening | clock |
|---|---|---|---|---|---|---|---|
| 05 healthy | 5 | 5 | 0 | — | 0 | — | `null` |
| 07 bleeding | 53 | 20 | 33 | **absent** | 0.007 | — | 16,317 |
| 09 + BloodLoss 0.478 | 54 | 20 | **34** | **0** | 0.562 | **false** | 7,223 |
| 11 healed | 5 | 5 | 0 | **0** | 0.623 | **true** | `null` |

**Read 09 is the whole issue.** Thirty-four hediffs dropped — a harder
truncation than Captain ever faced, whose worst read dropped 19 — and
`BloodLoss` is **row 0**. Under the sort that shipped before session 20 it was
rank 0, below `TendableNow` and below `Hediff_MissingPart`, and it was cut from
all five of his reads.

**Read 09 also refutes the Scope's own alternative empirically rather than by
argument.** `61794cd` offered "a severity/lethality ordering that puts
life-threatening hediffs first" as the generalising fix. On read 09 that row's
`life_threatening` is **false** at severity 0.562 — the `lifeThreatening` flag
sits on `BloodLoss`'s fifth stage at `minSeverity 0.60` — so the alternative
would have dropped it exactly as the old sort did. Read 11 crosses 0.60 and the
same field flips to **true**. One pawn, two reads, and the band that works is
`IsLethal`, not `lifeThreatening`.

**Read 07 is the invisibility case, and it is not a bug.** At severity 0.007
`BloodLoss` is legitimately absent — stage 0 sets `becomeVisible false` — while
the clock reads 16,317 anyway. That is the decision "the clock is independent of
the hediff list" demonstrated rather than asserted.

The clock nulls **both ways on one pawn**, which is stronger than two pawns:
`ticks_until_bleedout` is present-and-`null` when not bleeding (the key exists,
so a suite's `has_key` is meaningful) and finite when bleeding.

## Three fixture traps, each of which would have cost a suite an hour

1. **`pawn` takes `--id`, not `--pawn`.** `--pawn` is `bad-args`,
   `missing required arg 'id' (number)`. Banked as the first `05` attempt.
2. **`dev:heal` does NOT clear `BloodLoss`.** It is
   `HealthUtility.HealNonPermanentInjuriesAndRestoreLegs`, and `BloodLoss` is
   not a `Hediff_Injury`, so it survives — read 11 has it at 0.62 and *rising*
   with every injury gone. A suite that heals between phases and expects a clean
   pawn gets a pawn whose `blood_loss_severity` is higher than it was.
3. **`dev:damage {mode:"amount"}` stops at Downed**, by construction: the loop
   is `i < hits && !pawn.Downed && !pawn.Dead`. Three calls of `hits:8
   amount:2` landed 53 hediffs on a standing pawn; one call of `hits:20
   amount:6` would have downed her early and landed far fewer. Small `amount`,
   repeated calls, is the route to the Captain shape. `mode:"manipulation"` is
   the ready-made fixture for `40ed42f`'s "enabled but incapable" doctor.

## One shape question, raised rather than fixed

`bleedout.outcome` is `"death"` on a pawn with `ticks:null` — reads 05 and 11
both. It is defensible (it answers the hypothetical "what happens WHEN the clock
runs out", which is what the decision log says it computes) but it is not
obviously right for a pawn who has no clock, and any suite asserting `outcome`
must know it is populated regardless. Flagged on the issue; not changed here,
because the orchestrator does not edit a worker's spec mid-round.

## What this does NOT prove

`work-cover`, `triage`, `triage.act` executing as a real `rescue`, the
`work_coverage` predicate section, the "enabled but incapable" row, and the
in-game readout comparison. All of those are the acceptance suite's job. The
suite is the deliverable; this is the proof it has ground to stand on.

---

# Second half: `40ed42f`'s verbs, and the whole `triage` chain

Envelopes `12`–`23`, same bench session. Written up in full as comment #3 on
`40ed42f`; the short version:

- **`work-cover` repairs for real** — promoted Harrell citing
  `Pawn_WorkSettings.EnableAndInitialize`'s own order, `coverage_after.ok`
  flipped true, a **separate `digest` read agreed**, and it is journalled as an
  `action` row (seq 33) carrying the `repaired` list. That acceptance bullet is
  met on a bench.
- **One defect, filed as `58794e4`:** `work-cover {dry_run:true}` reports
  `coverage_after` as the coverage BEFORE the repair, while `repaired` in the
  same envelope names the promotion that fixes it.
- **`triage` proven through four states of one casualty** — needs-tend standing
  (`no-rescuer`), downed with no bed (**`no-bed`**), downed with a bed
  (**`in-time`**, `margin_ticks:6755`, `act` populated), and in bed
  (`no-rescuer` again). The casualty UNION is confirmed: the first row was
  `downed:false, needs_tend:true` and was reported anyway.
- **`act` carries `{"op":"rescue","args":{"pawn":359,"target":362}}`** with both
  ids and the M1 rationale.

## Two things the suite must know

**`triage`'s `act` path is unreachable on a bare `--quicktest` map.** No bed
means `TakeToBedGate` returns `no-bed` for every colonist and every verdict is
`no-rescuer`. One `dev:spawn-thing {def:"Bed", stuff:"WoodLog",
pos:"pawn:<id>", mode:"direct"}` is the whole fixture.

**`act` is a snapshot and the world moves under it.** Sent verbatim, it was
refused `cannot-rescue`. Not a gate mismatch — `triage` and `rescue` call the
same two predicates in the same order — but the patient had been carried to the
bed by the game's own AI between the read and the send, and `CanRescueNow` is
false for a pawn already in a bed. Pause before reading `triage`; send `act`
while still paused.
