# The cockpit sketch — walkthrough state

Where the element-by-element review of `COCKPIT.md` stopped, what it decided, and
the question left on the table.

**Reconstructed 2026-09-08 from the session transcript**
(`295a4e3c-7f7a-4108-b4c4-a866f1f227b2`, Sep 03 22:28 → Sep 04 14:35 local), because
none of it was in the repo. `COCKPIT.md` has not been touched since `f8bf981`
(Sep 03 23:22), which is *before* the walkthrough began — so the sketch on disk is
the pre-walkthrough text and **both amendments below are still unapplied.**

## The method, as Dorian set it

> "lets go through each element one at a time, see what I like / don't and then at
> the end we'll rewrite anything that needs it"

Three things follow from that, and they govern the rest of the pass:

- **One element at a time**, answered keep / change / drop.
- **The rewrite comes at the end**, not element by element. Nothing is edited into
  the sketch until the pass finishes.
- **The altitude is deliberately surface.** "goals for each section and even those
  are up for debate. I like what I see so far I won't overthink it." Each element is
  owed its own later Fable session; this pass is not that session and must not
  become it.

## Decided

### Element 1 of 9 — the one idea: push, not fetch — **KEEP, amended**

The agent does not choose what to look at. The mod builds a screen the way the game
builds one for a player, and `advance` returns it as its reply. No separate `look`
verb: every remembered step decayed to near zero by the last quarter of the run, and
the one thing the agent reliably read was the reply to the call it had just made.

Rejected alternative: keep `advance` small and require a `look` after it, the way
`digest` works today. Cheaper per turn, but it is the fetch model, and the fetch
model is what lost the colony.

Cost: two to four kilobytes per advance instead of roughly one.

**Amendment (Dorian):** when many decisions are owed, the screen serves them **in
groups, one kind at a time**. The raid decisions come first, the agent answers those,
the next screen shows the next group, and **time does not move until the last group
is answered**. The agent never sorts the pile itself — the mod orders the groups by
urgency and hands over one at a time.

### Element 2 of 9 — the screen's six panels — **KEEP, amended**

In the order a player's eye goes:

1. **Stop line.** Why the clock stopped, and how long since the agent last looked.
2. **Map.** Home area plus margin, mod-chosen, ASCII inline and a PNG one read away.
   Turret range and wind clearance drawn. Anything gone since last time marked and named.
3. **Colonists.** Weapon, armour, health, mood, where they are, how far outside the walls.
4. **Gauges.** Guns by position and gate coverage, power nets and unreachable batteries,
   food map-wide and in stockpiles, temperature, stock, the six goals, alerts with an age.
5. **Since you last looked.** Three rows: game, human, mod.
6. **Decisions owed.** Each a sentence with its acts ready to send, in the groups
   element 1 settled.

Nothing was reported missing from the six.

**Amendment (Dorian):** each panel gets **one line saying what it is for**, and the
detail under it comes out. The detail is a later session's, one per panel, and **the
sketch should say so rather than pretend to have settled it.**

### Element 3 of 9 — when the clock stops — **KEEP, amended**

Unchanged from today: every letter, every new alert, an own-faction casualty, a
force-pausing dialog, a red error, the caller's own `until` condition, and one
in-game day of quiet.

Added: something the colony built was destroyed (`Building.Destroy` /
`Frame.Destroy`, player faction only, carrying the game's own `DestroyMode`), and
a decision owed with a deadline inside the next day.

Not added: a human at the controls (recorded and shown, no stop), and a chore
that ran (reported, not announced).

**Amendment (Dorian):** the agent can stop any announcement from pausing its
game if it chooses — **it keeps a list**. Some things really do not need to stop
the clock, and others are situational on the state of the game.

Two notes for the rewrite, neither of them a decision taken here:

- This extends a mechanism the mod already ships rather than inventing one.
  `AlertMuteVerbs.cs` has `AlertMuteComponent` with `Muted(string id)` and an
  off-thread mirror of muted ids; stop reasons want the same shape, keyed by
  reason rather than by alert.
- A mute list is the fetch model wearing a different hat, and the audit measured
  what happens to anything the agent must remember to revisit: theme T11, it goes
  to zero. So what is currently muted belongs ON the screen, and the sketch
  should say whether a mute expires, or is per-condition, or holds until lifted.

### Element 4 of 9 — what arrives vs. what is fetched — **KEEP, unamended**

Arrives unasked every turn: the stop line, the map (home area, the stop's place,
what is gone), the colonist bar, the gauges, what changed since it last looked,
and the decisions owed with their acts.

Fetched by id when a decision needs it: the raw journal rows, a render of any
other rectangle, one pawn's detail, `things`/`room`/`zones`, `inspect`, and the
dry runs that reply with the ghost the player would see before placing.

The rule: the screen names the id, the fetch takes the id, and nothing the agent
must remember to do on a schedule lives on the fetch side — theme T11 measured
scheduled fetches going to zero.

### Element 5 of 9 — chores — **KEEP the rule, but the LIST is not settled**

The rule itself stands: if every branch of the response can be computed from
state the mod already publishes, the mod does it, and the playbook keeps
judgement. Eleven such rules were kept by hand this run and their use decayed by
quarter until the quarter with the wipe; a chore has no attention budget.

**Dorian's objections, both about the categories rather than the behaviours:**

- **`roof` is a symptom dressed as a chore.** Roofing the cells where items are
  deteriorating treats the effect. The question it skips is why items are sitting
  in an unroofed storage slot at all. It belongs inside a wider "taking care of
  items" concern, not as its own trigger keyed on a deterioration reason.
- **`save` should not be a chore, it should just be automatic.** It has no
  trigger to judge and no procedure to get wrong; putting it in the same table as
  rescuing the downed is a category error.

**Standing call: this section is owed its own Fable round**, to check the list
for logical consistency and to test whether the categories themselves are sound —
not to re-decide the behaviours. Element 6 is the taxonomy those categories come
from, so that round should almost certainly take both sections together.

### Element 6 of 9 — the line: chore, gauge, stop, or decision — **KEEP, unamended**

Four categories, one per row: a chore the mod does, a gauge on every screen (a
light is a gauge with a threshold), a stop of the clock, or a decision framed and
handed over. All 27 of this run's incidents are sorted by it, and so are the run
contract's eleven standing rules — nine of which stop being rules the agent has
to remember at all.

What stays with the agent, per the 2026-08-31 ruling: anything needing a forecast
rather than an observation, anything weighing outcomes with no shared unit, the
objective, whom to recruit, and situations no procedure anticipated.

Standing against it: element 5's call that the categories themselves get a Fable
round. Keeping the line here is keeping the sort as drawn, not a finding that the
four categories are the right four.

### Element 7 of 9 — how it stays honest — **PROVISIONAL KEEP, owed an agent read**

Six defences against the audit's central finding that a truthful field the agent
did not ask for goes unread, 52 of 52: the screen is the reply rather than a
field beside it; decisions hold the clock and deferring costs a logged reason,
replacing the read gate that was defeated four ways; losses are levels rather
than events; gauges come from a full count; chores need no attention; and every
screen is a file on disk so the next audit measures compliance instead of
reconstructing it.

**Dorian's answer:** "should an agent look at this? too much to focus on right
now but seems okay." Recorded as a provisional keep — nothing here is rejected,
and the section is owed an independent read before it is treated as settled.

Why this section in particular rewards one: it is the only element that makes
falsifiable claims rather than taste calls. Each of the six is checkable against
the audit's own evidence, and two are already admitted UNKNOWN in the sketch —
whether the lower panels get read when the stop line was all the agent wanted,
and whether a pushed picture gets opened. An independent reader can test the
other four the same way rather than agreeing with them.

## Open — the question on the table

Element 8 of 9 is next; nothing is currently awaiting an answer.

## Not yet reached — elements 4 to 9

The numbered elements track `COCKPIT.md`'s own section order; elements 1, 2 and 3
matched §"The design in one paragraph", §"The screen" and §"When the clock stops"
exactly, and the remaining six sections before the trailing material are:

| # | section |
|---|---|
| 4 | What arrives, and what has to be fetched |
| 5 | Chores: what the mod does by itself |
| 6 | The line: chore, gauge, stop, or decision |
| 7 | How it stays honest when the agent stops paying attention |
| 8 | Where it lives |
| 9 | What to build first |

**That mapping is inferred from the section order, not stated in the transcript** —
the sketch's three trailing sections (§"Calls that could have gone the other way",
§"What is UNKNOWN", and Appendices A and B) are read as not part of the nine.

## What the rewrite owes when the pass finishes

1. The group-serving rule, into §The screen (panel 6) and §When the clock stops —
   including "time does not move until the last group is answered."
2. Every panel cut to one line of purpose, the detail struck, and the sketch saying
   in its own voice that each panel is owed its own session.
3. The agent-held mute list for stop reasons, its shape against the existing
   `AlertMuteComponent`, its visibility on the screen, and whether a mute expires.
4. The chore list re-examined in its own Fable round with element 6: `roof`
   folded into a wider care-of-items concern, `save` moved out of the chore
   category to plain automatic behaviour, and the categories themselves tested.
5. An independent agent read of element 7's six honesty claims, which are
   falsifiable against the audit rather than matters of taste.
6. Whatever elements 8 to 9 change.
