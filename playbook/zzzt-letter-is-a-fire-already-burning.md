---
name: zzzt-letter-is-a-fire-already-burning
trigger: a Zzzt / short-circuit letter, or any fire letter
severity: Critical
confidence: verified-in-source
source: postmortem.md's worked dry-run (96d9315 acceptance); Alert_FireInHomeArea is home-scoped; ShortCircuitUtility.DoShortCircuit
graduated-to: checklists/triggered.md §power-incident · templates/power-room.{ir.json,md}
---

**What.** Respond at the LETTER, not at the alert. A Zzzt letter is the
ignition event itself — already happened, already past — and the alert that
would tell you about the fire trails it by a lag OUR advance sizing creates.

**Why.** Three lags stack, and the last one is the killer:

1. `ShortCircuitUtility.DoShortCircuit` is where the letter comes from, and it
   fires AFTER the damage: the letter is the ignition event, not a warning
   about a possible one.
2. `Alert_FireInHomeArea` is **home-area-scoped**, so a fire in an outbuilding or
   an unhomed power room raises nothing at all
   ([[alert-need-defenses-self-silences]] covers the same trap-class).
3. Even in the home area the alert is a poll, not a hook — and OUR poll is much
   slower than the game's. See the lag note below.

**Read the letter body — the outcome is not always a fire.** `DoShortCircuit`
branches on stored charge:

    if (powerNet.batteryComps.Any(x => x.StoredEnergy > 20f))
        DrainBatteriesAndCauseExplosion(...);   // Flame explosion, sized by charge
    else
        flag = TryStartFireNear(culprit);       // and this CAN return false

With any battery on the culprit's net above 20 Wd you get a Flame explosion
(plus a Bomb sub-blast above radius 3.5). Otherwise the game tries to light a
cell within 3 of the culprit, in line of sight — and if `TryStartFireNear`
finds nowhere it will take, **nothing ignites at all**. The letter text is the
tell: `ShortCircuitStartedFire` when a fire actually started,
`ShortCircuit` otherwise. The `ShortCircuitWasLarge` / `WasHuge` lines are
appended above blast radius 5 and 8.

The response does not change — `fires` map-wide, then popper coverage — but
"the letter always means a fire is burning" would send the agent hunting a
fire that in the no-battery case may not exist, and treating a plain
`ShortCircuit` as a false alarm is the opposite error. Read which one arrived.

**The ~2,000-tick lag is OURS, not the game's** *(corrected — an earlier
version of this lesson attributed it to vanilla)*. `AlertsReadout
.AlertCycleLength` is 24 and `AlertsReadoutUpdate` advances one index per
FRAME, so vanilla re-checks a given alert within ~24 frames — at 1× that is
~24 ticks, nothing. The 800–2,000 TICK figure is this repo's own arithmetic:
24 readout frames + `Config.AlertScanFrames` (default 30) = 54 frames, at the
~33 ticks/frame a budgeted advance delivers (`JOURNAL.md` §Alert timing). It
is a lag we buy by running fast, and it shrinks if `alertScanFrames` is
lowered — it cannot go below the readout's own 24-frame sweep.

The blast is sized by stored charge: a 4,800 Wd bank gives
`sqrt(4800) x 0.05 = 3.5` cells of blast radius, right at the Bomb threshold. So
the conduit's position inside the room decides whether the explosion reaches the
walls.

**How to apply.** On the letter — not on the alert — read `fires` map-wide rather
than trusting the scoped alert, then confirm popper coverage is standing. The
digest counts fires map-wide precisely because the vanilla alert does not.

**This lesson ships ALREADY GRADUATED**, and that is why it has no outstanding
action. Its response is encoded twice: as the `power-incident` trigger in
`checklists/triggered.md`, and structurally in `templates/power-room`, whose IR
carries a `FirefoamPopper` in fuse range with the conduit moved out to a wall
cell. DESIGN names this exact loss as the canonical pre-learned case, and the
escalation ladder's endpoint is a template that makes the mistake unavailable.

It keeps a file and an index row anyway, deliberately: **a graduated lesson with
no index entry is indistinguishable from a lesson nobody ever learned.** The
ladder is supposed to move checks out of prose, not erase the record that the
colony ever paid for them. (The general convention — every graduated lesson keeps
an index row naming where it went — belongs to 4.4, `d32eadd`, which owns
retirement evidence.)

**Retire when.** Never as knowledge. As a CHECK it is already retired: nothing in
`turn.md` or `daily.md` polls for this, because the trigger and the template
carry it.
