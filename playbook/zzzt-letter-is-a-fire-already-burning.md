---
name: zzzt-letter-is-a-fire-already-burning
trigger: a Zzzt / short-circuit letter, or any fire letter
severity: Critical
confidence: verified-in-source
source: postmortem.md's worked dry-run (96d9315 acceptance); Alert_FireInHomeArea is home-scoped
graduated-to: checklists/triggered.md §power-incident · templates/power-room.{ir.json,md}
---

**What.** Respond at the LETTER, not at the alert. A Zzzt letter means a fire is
already burning; the alert trails it by up to ~2,000 ticks of spread.

**Why.** Three lags stack, and the last one is the killer:

1. `DoShortCircuit` drains the battery bank and detonates — the letter is the
   ignition event itself, not a warning about a possible one.
2. `Alert_FireInHomeArea` is **home-area-scoped**, so a fire in an outbuilding or
   an unhomed power room raises nothing at all
   ([[alert-need-defenses-self-silences]] covers the same trap-class).
3. Even in the home area the alert is a poll, not a hook. By the time it reads
   true the fire has spread — and if the room is wooden, spread is the whole
   loss.

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
