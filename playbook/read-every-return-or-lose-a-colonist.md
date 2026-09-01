# Read every return, or lose a colonist inside your own polling loop

- **severity**: Critical
- **confidence**: observed-at-bench (M1 `m1-20260831`, one death directly
  attributable)
- **bites when**: short advances issued back to back while waiting for something
  specific to appear

## The failure

`PLAY-LOOP.md` §read is unconditional: **every** return from `advance` reads the
journal delta, then one digest. It is written as a discipline rather than a
suggestion, and the reason is this.

M1 day 4. A `ThreatSmall` "Mad crow" letter arrived and the crow was not visible
(`threats.hostiles` counts fogged hostiles; `pawns {filter:"hostile"}` does not
report them). I wrote a loop: advance 2500 ticks, read `pawns {filter:"hostile"}`,
repeat, stop when a hostile appears. Six iterations, 15,000 ticks.

That read answers exactly one question — *is an enemy visible* — and I asked
only it. In iteration three, at tick 214,599, the crow reached Table and downed
him. `Alert_ColonistNeedsRescuing` went Critical at 214,659. `Alert_ColonistNeedsTend`
had already been up since 205,979. A "Critical alert: Medical emergency" message
landed at 219,897. **All of it was in the journal delta and the digest I was not
reading.** Table bled for 11,335 ticks and died at 225,934.

I found out because the *next* advance halted on a letter, and the letter was
his funeral.

## Why the shape is seductive

A tight poll loop feels like attentive play — six advances in a minute, each one
checked. It is the opposite: the check was narrowed to the thing I was waiting
for, so the loop was blind to everything I was not waiting for, at exactly the
cadence that generates the most events per unit of attention. A single one-day
advance with a proper read would have caught it; six short ones with a narrow
read did not.

## What to do

- **Never substitute a targeted query for the read.** A drill-down is what you
  do *after* `journal --since` and `digest`, never instead of them. If a loop is
  worth writing, put the digest inside it.
- **Prefer a guard to a poll.** The thing I wanted was a halt condition:
  `--until.event.type downed` would have stopped the advance at 214,599 and
  handed me the event. I added that guard *after* the death, at 221,529. Guards
  are cheap and they are the reason `until:` exists — polling is what you do
  when no guard fits, and then you still read.
- **Waiting for an invisible enemy is not a reason to advance short.** The crow
  never became visible to `pawns {filter:"hostile"}` at any point, including
  while it was killing two colonists. Fog does not lift because you asked
  repeatedly.

## The helper that encodes it

Written after the fact, kept because the discipline needs to be mechanical:
one turn = guard time control, advance, journal delta, digest — with the read
unconditional and not parameterised. See the run's `summary.md`.

Related: [[one-doctor-is-zero-doctors]], [[seek-off-is-a-decision-to-flee]].
