# s21 suite runs, and the `too-slow` fixture the suite could not build

Bench `_RimWorld-Agent`, `--quicktest`, assembly `1.0.0+3f4412f` (`Build:`
`f91eb2c`). Orchestrator-run; workers launch nothing.

## `accept/722c951-advance-halt.py` — 79 PASS / 1 FAIL / 1 SKIP, exit 2

The FAIL was mine, not the mod's: check 1.17 hard-coded halt `reason:
"condition"`, correct while that wait was dressed as a `time.tick` predicate and
wrong the moment `fba467b` made it an honest `{"ticks": N}`. Fixed in `2deb6df`.
Everything else about the check passed.

The SKIP is a real fixture gap the suite names precisely and refuses to fake:
**`triage` never reached `too-slow`, and the deadline refusal fires on that
verdict and nothing else.**

## The refusal, proven by hand — `722c951` and `40ed42f` part 3

Two things had to be true at once and a bare `--quicktest` map gives neither:

1. **A bed must exist.** With none, `TakeToBedGate` answers `no-bed` for every
   colonist and every verdict is `no-rescuer`.
2. **The casualty must be FAR from the rescuers.** On a fresh map everyone
   stands within a few cells, so the clock cannot be driven below the walk
   without killing the patient outright.

The recipe that worked, and the numbers it produced:

    dev:spawn-thing {def:"Bed", stuff:"WoodLog", pos:<colony>, mode:"direct"}
    dev:spawn-pawn  {kind:"Colonist", faction:"PlayerColony", pos:<+110,+90>}
    dev:damage      {pawn:<victim>, mode:"until-downed"}
    dev:add-hediff  {pawn:<victim>, def:"BloodLoss", severity:0.85}

    triage -> verdict "too-slow", margin_ticks -1487
      clock 3,061 ticks
      Olsen  142 cells  travel 2,264 + carry 2,284 = total 4,548
      Alex   147 cells  travel 2,688 + carry 2,601 = total 5,289
      act still published: {"op":"rescue","args":{"pawn":905,"target":44015}}

`act` is published even on `too-slow`, which is right — the agent may still
choose to try, and the verb's job is to say what it would cost, not to decide.

Then the refusal:

    advance {ticks:500}
    ok: false   code: bleedout-deadline
    Darcie bleeds out in 3061 ticks and the nearest capable rescuer needs 4548
    ticks to reach a bed with them — 1487 ticks short. bleedout_ticks=3061
    rescue_ticks=4548 margin_ticks=-1487 pawn=44015 pawn_name=Darcie
    rescuer=905 verdict=too-slow. Advancing now is a decision to let this pawn
    die, and the M1 run made it by accident: at tick 231,968 a ~9,040-tick bleed
    clock was answered with a work-priority flip whose chosen rescuer stayed
    asleep for ~6,100 ticks.

And the escape:

    advance {ticks:500, through_casualties:"<reason>"}   ->  ok: true
    escaped.bleedout_deadline: {reason, detail, ...}

**This is the M1 decision at tick 231,968, answered.** That run faced "is there
time to walk a rescuer 118 cells", had a ~9,040-tick clock against a ~2,810-tick
walk, and neither number was published. Both are now, with a verdict, a signed
margin, and a refusal that names what advancing means.

## `722c951`'s own headline, also proven by hand

    advance {ticks:2000}          -> ok, journals 6 events
    advance {ticks:2000}          -> ok:false  code:unread-journal
      "the previous advance journaled 6 event(s) that no `journal` call has read
       (seq 65..70; types: alert_on 1, DEATH 1, letter 2, message 2)"
    advance {ticks:2000, unread_ok:"<reason>"}  -> ok, escaped.unread_journal
    advance {ticks:100,  unread_ok:""}          -> bad-args
    journal ; advance {ticks:500}               -> ok  (watermark MOVED)

The delta it refused contained a **death**. That is M1's failure exactly — an
advance journaled a casualty and the next advance would have been blind to it —
and it is now refused by default.

## What phase 6 should become

The suite's SKIP text already names both halves of the gap. Converting it to a
PASS needs the four-call recipe above staged inside the phase. Handed back to
the worker that owns the suite rather than patched from the chair.
