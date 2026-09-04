# The cockpit

What the agent should be looking at while it plays. This is the root for the next
round of work, written from the audit of run `openrun-20260902` (`themes.md`,
`tables.md`, `findings.md`), the mod's own source, and the game's own source. It is
a design: nothing here was built, and nothing was run.

Dorian's phrase for the goal is *put the agent in the same cockpit the player has.*
A person playing RimWorld does not decide what to look at. The map is always on the
screen. The game pauses itself when a letter arrives. Alerts sit on the right edge
until they are dealt with. Selecting a turret draws its range. Placing a wind turbine
draws the clearance it needs. None of that is fetched; it is simply there, and the
player's whole job is to decide.

The agent that lost Andbourne had over a hundred verbs and had to decide, every turn,
which of them to call. Whatever it did not think to fetch, it did not see. The audit
measured what that cost: a turret destroyed with no event to say so; a truthful field
read zero times in fifty-two chances; a rule to look at the base that went from 22
uses in the first quarter of the run to 2 in the last; twenty-two human interventions
that appear nowhere in the record.

So this design has one idea. **The agent does not choose what to look at.** The mod
builds a screen, the way the game builds one for a player, and every turn starts with
it.

One naming note before anything else. There is already a directory called `cockpit/`
in this repo. It is Dorian's read-only viewer, which watches a run from its
transcript. In this document *the cockpit* means what the agent sees. The two are
meant to become one thing: the viewer should show the same screen the agent was
given, so that when Dorian sees something the agent cannot, the fix is to put it on
the screen rather than to reach into the game by hand.

---

## The design in one paragraph

Every turn starts with a screen. The screen is the reply to `advance`, so there is
nothing to read first and nothing sitting beside the thing the agent asked for. The
clock stops for what the game stops for, plus the things this run died of. The screen
shows what changed since the agent last looked, says who did it, and keeps showing a
loss until it is repaired or written off. Chores, meaning anything whose every step can
be computed from what the mod already knows, are done by the mod without a turn and
reported on the screen. What remains for the agent is judgement, and each judgement
arrives framed, with the acts that would answer it ready to send. The clock does not
run while a judgement is owed unless the agent defers it with a reason.

---

## The screen

Below is the screen as it would print at one real moment of the run: the raid letter
at tick 10,349,000 that ended the colony. It is a reconstruction, not a record. The
guns, power, food, stock, goals, the gone autocannon and the three decisions are from
the audit; the colonist rows, the alert ages and anything marked ~ are invented to show
the shape.

```
ANDBOURNE · Spring 5, 5503 · 14h · tick 10,349,000 · paused · saved: raid-5503-spring-5
STOPPED   letter — Raid: Nyararm Mechhive. 2 scythers, 1 lancer, drop pods at (125,175),
          80 cells north of the walls. You last looked 7,700 ticks ago.

MAP       home area + 8, 60x50 cells, north up · picture: frames/10349000.png (map changed: yes)
          ┌ x74 ─────────────────────────────────────────────────── x132 ┐
          │  ...  ##########+##########  ...                             │ z120
          │  ...  #  .  .  T .  .  .  #  ...                             │
          │  ...  #  .  ·  ·  ·  .  !  #     [ascii crop, ruler on both  │
          │  ...  #  .  .  .  .  .  .  #      edges; one char per cell,  │
          │  ...  #  W::::  ~~~~~  .  #      fixed alphabet]             │
          │  ...  ##########+##########  ...                             │ z70
          └────────────────────────────────────────────────────────────┘
          # wall  + door  T turret  · its range  W wind turbine  : its clearance  ~ conduit
          @ colonist  h hostile  ! gone since you last looked  , soil  . gravel  _ sand  ^ rock  % ore
   GONE   autocannon at (98,114) — destroyed by a manhunter vulture 376,000 ticks ago. Not rebuilt.
   NEW    3 hostiles at (125,175).
   OPEN   north wall x97–99 (the planned gate).

COLONISTS 7 · armed 5 · downed 1 · inside the walls 1, outside 6 · posture: unbound, seek off
   Gauss   LMG            marine   hp 100  mood 61   (140,80)  hauling     66 cells out
   Niklas  chain shotgun  marine   hp  62  mood 40   (118,78)  downed      not bleeding
   Anon    assault rifle  flak     hp 100  mood 55   (60,100)  mining      40 cells out
   Ludo    bolt-action    flak     hp 100  mood 31   (101,95)  sleeping    inside
   John    gladius        flak     hp  95  mood 58   (130,90)  hauling     44 cells out
   Sinn    —              —        hp 100  mood 70   (150,60)  wandering   80 cells out
   Dilly   —              —        hp  40  mood 20   (116,78)  in bed, cannot walk, room at −4.6 °C

GUNS      mini-turrets 6 at (93,108) (103,108) (85,79) (88,79) (84,75) (86,75) · autocannon 1 at (85,75), was 2
          north gate covered by 2 guns, south gate by 5 · cells inside the walls covered by any gun: 0 of ~1,900
POWER     +184 W · batteries 3 at 51 % in room 73 — no door, nobody can reach them · nets 2: net B (two
          wind turbines) has no consumer
FOOD      48 days map-wide (411 survival meals, human-sent) · in stockpiles 0.4 days · rotting: nothing
TEMP      room 10 (Dilly's bed) −4.6 °C, no heater · outdoors −2 °C, cold snap
STOCK     steel 0 · components 3 · wood 210 · medicine 18 · unworn armour 0 · uninstalled art 1
GOALS     G1 scanner researched, built, unpowered · G2 geysers 4, used 1 · G3 armed 5 of 7 · G4 rooms +2
          since day 166 · G5 art uninstalled 1 at (108,105) · G6 offers unanswered 1 (below)
LIGHTS    4 alerts live, 2 muted · NeedDoctor: 13 days, no verdict · ColonistNeedsRescuing: Niklas

SINCE YOU LAST LOOKED (7,700 ticks)
   game    letter: raid · downed: Niklas, scyther · arrived: refugee Raccoon at (0,140), injured
   human   — nothing this turn. Earlier: 411 survival meals at tick 9,150,000; John revived at 4,866,218.
   mod     roofed stockpile 12 (8 cells designated) · research put back to GroundPenetratingScanner
           after the game picked Cocoa

DECISIONS OWED — the clock will not run until each is answered, or deferred with a reason
   d-311  RAID     fight from the walls, or shelter?
            act: posture {pawns:"all", area:"lockdown", seek:false, hostility:"attack"}
            act: seek-at-will {pawns:"all"}      (broken until seekandkill a6b1aa0 is fixed)
   d-309  REFUGEE  Raccoon, crash-landed, injured, at the map edge. Take in?   deadline ~2 days
            act: quest-accept {quest:"…"}   act: quest-decline {quest:"…", reason:"…"}
   d-301  ALERT    NeedDoctor has been live 13 days with no verdict. Fix, or mute with a reason?
            act: work-priorities {…}   act: alert-mute {id:"Alert_NeedDoctor", reason:"…"}
```

Six panels. Read top to bottom, the way a player's eye goes: why we stopped, the map,
the people, the numbers, what happened, what is asked of me.

**The stop line.** Why the clock stopped, and how long since the agent last looked.
This is the line the audit shows the agent actually read this run: it acted on
`halted_on` every time it carried a casualty. Everything else on the screen sits
under the line the agent came for.

**The map.** A picture of the home area, chosen by the mod, not the agent. The agent
this run looked at rectangles it chose after deciding where to build, which is how it
sealed its own workshop door. Now the rectangle is the game's own home area plus a
margin, and on a stop with a place attached (a raid, a casualty, a destroyed
building) a second crop around that place. The ASCII crop is always there; the PNG is
one `Read` away and the screen says whether the map changed since last time, so the
agent knows when the picture is worth opening. The alphabet is fixed and tells gravel
from sand from rock, because this run wrote off 131 farmable gravel cells as sand.
Turret range and wind-turbine clearance are drawn, because they are what the game
draws when a player selects those things. Anything that was there last time and is
gone now is marked, and named below the crop, with what destroyed it and when.

**Colonists.** The colonist bar: weapon, armour, health, mood, where they are, what they
are doing, and how far outside the walls they stand. This run's colony was scattered
40 to 70 cells out when the fatal raid landed, after a `posture` call with no pawn
filter unbound all seven. That fact was never on any screen.

**Gauges.** The numbers that are always there. Guns are counted by kind with every
position listed, never one coordinate for six things, and the gauge says what each
gate is covered by and how much of the inside is covered by anything at all. Power
says which nets have no generator or no consumer and which batteries nobody can walk
to. Food says map-wide and in-stockpile as two numbers, because the difference was
misread four times this run. The six goals of the run contract are graded on every
screen: the day-170 discovery of four steam geysers is a day-1 line here. Lights are
the game's alert readout, with an age on each, and any alert older than a day with no
verdict is a decision owed.

**Since you last looked.** What happened while time ran, in three rows: the game,
a human, the mod. This is the journal already kept, read by the mod instead of by the
agent, capped by importance, with the raw rows one `journal` call away. A death, a
downing, a destroyed building, an arrival, a letter are always shown. Dorian's
interventions appear in the human row the turn after they happen, so the agent knows
food arrived by hand and a dead colonist is alive again. What the mod did by itself is
the third row, so a chore is never invisible.

**Decisions owed.** Each one a sentence a person could answer, with the acts that
would answer it written out and ready to send. This is the shape `triage` already has,
which the run's own ledger called the best thing on the surface. A raid, a refugee, an
old alert, a build blocked on a resource the map does not have, a chore that could not
finish because a policy was never set. Each carries a deadline when the game has one.

---

## When the clock stops

The mod already stops the clock for every letter, every new alert, an own-faction
casualty, a force-pausing dialog, a red error, the caller's own `until` condition, and
one in-game day of quiet. That set was decided on 2026-09-01 and it is right. Two
things are added, and two are deliberately not.

Added:

- **Something the colony built was destroyed.** A wall, a door, a turret, a generator,
  a bench, a bed, a conduit, a blueprint's frame. The journal has fifteen event types
  and none of them is this. The hook is `Building.Destroy` (Verse/Building.cs) and
  `Frame.Destroy` (RimWorld/Frame.cs), for player-faction things only, carrying the
  game's own `DestroyMode` rather than a story about what happened. This is the first
  link in the agent's own causal chain for the wipe, and it stops the clock.
- **A decision is owed with a deadline inside the next day.** A refugee offer that
  lapses, a corpse that will rot, a downed enemy who will wake. Today only letters
  stop the clock; a deadline the mod computed does not.

Not added, on purpose:

- **A human at the controls.** Dorian's actions are recorded and shown on the next
  screen, but they do not stop the clock, because he played nine in-game days by hand
  this run and nine days of stops would have been unplayable for both of them. This is
  a call that could go the other way; see the list at the end.
- **A chore that ran.** The mod's own work is reported, not announced.

The morning checklist of the play loop becomes the morning screen: the one-day
timeout already means every day starts with a stop, and the screen is what a day
starts with.

---

## What arrives, and what has to be fetched

| arrives unasked, every turn | fetched by id, when a decision needs it |
|---|---|
| the stop line | `journal {since_seq}` for the raw rows |
| the map: home area, the stop's place, what is gone | `map-dump` / `render` of any other rectangle |
| colonists: the bar | `pawn {id, sections}` for one pawn's detail |
| gauges: guns, power, food, temperature, stock, goals, lights | `things {def}`, `room {id}`, `zones` — each saying in its first line how many of the total it shows |
| since you last looked: game, human, mod | `inspect {id}`: what the player's bottom pane says about one thing, its buttons, and what the game would draw for it |
| decisions owed, each with its acts | dry runs: `build`, `place-layout`, `zone` reply with the ghost the player sees before placing |

The rule for the right-hand column: the screen names the id, the fetch takes the id.
Nothing the agent must remember to do on a schedule lives on the right. The audit's
theme T11 measured what happens to scheduled fetches: they go to zero.

---

## Chores: what the mod does by itself

The rule was decided twice already, on 2026-08-31 and 2026-09-01, and it stands:
*if every branch of the response can be computed from state the mod already
publishes, the mod does it; the playbook carries judgement.* This run kept eleven
such rules in a contract and executed them by hand, and their use decayed by quarter
until the quarter with the wipe. A chore has no attention budget.

Each chore has a trigger, a fixed procedure, one knob where a policy is involved, and
a line on the screen every time it runs. Each is a spec issue with an Acceptance
section; this table is the list, not the specs.

| chore | trigger | what the mod does | the knob |
|---|---|---|---|
| after a fight | standing hostiles reach 0 | rescue downed colonists, nearest capable pawn first; tend each until stable; finish off or capture downed enemies; unforbid what the fight dropped; undraft everyone | finish off, on only while there is no prison (`cc8988c`) |
| butcher | a fresh corpse of a colony kill or hunt, and a butcher spot or table | make sure one butcher bill exists and can run; if there is no spot, a decision owed with the rot deadline | which animals; default all but pets |
| roof | any stored item whose deterioration reasons include being unroofed (`Thing.GetInspectStringLowPriority`, `SteadyEnvironmentEffects.FinalDeteriorationRate`) | designate a roof over those cells, as a player would with the roof area tool | on by default |
| research queue | a project finishes and the game auto-picks | put the next project from the agent's queue back; if the queue is empty, a decision owed | the queue is the agent's |
| a joiner | a pawn joins | essentials to priority 1 (firefighting, patient, basic work, doctor if capable); nothing at 0 unless incapable; the colony's food and outfit policy; the colony's posture | the essentials list |
| a doctor gone | the only doctor is downed or dead | promote the next best medic (`40ed42f`, already specified) | — |
| tend until stable | a colonist needs tending and no doctor job starts within a short window | force the nearest capable pawn, awake or not, and repeat until tending is no longer needed | — |
| save | every stop | `auto-<why>-<date>`; the agent's own `save` stays | how many to keep |

What is not a chore, and why, is the next section.

---

## The line: chore, gauge, stop, or decision

This is the table Dorian asked for. Every row is something that went wrong or was
decided in this run. The middle column is the call; the right column is where it goes
in the design. A *gauge* is a number on every screen. A *light* is a gauge with a
threshold. A *decision* is framed on the screen and waits for the agent.

| what happened this run | mechanical or judgement | where it goes |
|---|---|---|
| Vultures destroyed two turrets and an autocannon; no event said so | the loss is a fact | stop; map GONE; guns gauge shows "was 2" until rebuilt or written off |
| The agent re-counted mini-turrets and never autocannons | counting is mechanical | guns gauge, by kind, every position listed |
| Every turret pointed at a gate; the lancer walked in | coverage is computable; the layout is judgement | guns gauge: cells inside the walls covered by any gun; the fix is the agent's |
| Whether to rebuild the autocannon with steel at 0 | judgement | decision owed: north gate uncovered, autocannon needs steel, map has none |
| Meat, medicine, cloth rotted in storage all run | chore | roof |
| Corpses rotted unbutchered | chore | butcher |
| The research auto-picker stole hours 16 times | chore | research queue |
| Doctor coverage read green with one doctor, who then went down; three deaths | an instrument bug, then a chore | `aa4391b`; a doctor gone |
| The only doctor was asleep; tending had to be forced four times | chore | tend until stable |
| `posture` with no pawn filter unbound all seven | a verb whose default mutates broadly | the unscoped form is refused; `pawns` is required |
| The colony was scattered when the raid landed | the fact is a gauge; where to be is judgement | colonists: inside/outside the walls; the raid decision's act re-binds |
| Ellis shot by a colonist; the journal read was thrown away | the stop fired; the read was discarded | the screen is the reply; nothing to discard; the answer gate below |
| 56 of 76 zones kept sowing after "stop sowing" was verified on a 20-row page | a capped read verified a change | gauges come from a full count; a capped reply says "20 of 76 shown" in its first line |
| Wind turbines built on isolated nets, twice | the net is a fact | power gauge names nets with no consumer; a dry run says which net the site would join |
| Four geysers, one used, discovered on day 170 | a fact available on day 1 | goals gauge, G2 |
| A sculpture sat uninstalled all run | a count, and one missing verb | goals gauge, G5; the `install` verb (`6d4ca8a`) |
| Three batteries in a doorless room | reachability is computable | power gauge: unreachable, with the room |
| Twelve manhunter vultures invisible to the hostile filter | a bug | threats count manhunters (`T7`) |
| Rice planted on Fall 14 that winter would kill | the game knows the growing season | the zone verb's reply says it will not mature; a gate in the widget |
| Dorian removed toxic fallout, sent 411 meals, revived John, played nine days | human acts | since you last looked: human |
| Whether to ask Dorian for steel | judgement, under a standing policy of "ask" | decision owed when a build is blocked on a thing the map cannot make |
| Gauss pressed at 64 % for one more burst; a ten-year-old sent to carry a bleeding man under fire | judgement; the after-a-fight chore waits for the fight to end | decision owed during the fight: "Niklas down under fire; nearest capable: Sinn, age 10" |
| Take the Empire quest for goodwill over silver | judgement | decision owed |
| Refugee Raccoon at the map edge, never reached | judgement with a deadline | decision owed, deadline shown |
| The naming dialog wedged the run | a stop, already | unchanged |
| `advance` refused 40 times for an unread journal | the read gate | replaced by the answer gate |
| Mortar barrels not craftable and not on the map | a fact | the build's own reply already says so |

The run contract's eleven standing rules, by the same line:

| rule | becomes |
|---|---|
| 1. never set a work priority to 0 | a light: work types nobody has enabled, by name |
| 2. butcher within a day of a kill | the butcher chore |
| 3. check daily for unworn armour | a gauge, and a light when the outfit policy is what forbids it |
| 4. query `MinifiedThing` daily | the G5 gauge |
| 5. if `advance` refuses twice, read `unread_after` | gone; there is no read gate |
| 6. never advance past a letter with a choice | the answer gate |
| 7. read `uses_outdoor_temp`, not just `enclosed` | a light on the temperature gauge |
| 8. batteries before geothermal | judgement; stays in the playbook |
| 9. tame animals | judgement; stays in the playbook |
| 10. look at the base every few days | the map, every turn |
| 11. `find-rect` before you place | every placement verb replies with the ghost; there is no un-surveyed placement |

What genuinely stays with the agent, in the words of the 2026-08-31 ruling: anything
needing a forecast rather than an observation (what to research, expand or fortify),
anything weighing outcomes that do not share a unit (risk one colonist for another;
a quest whose reward is goodwill and whose cost is a hostile faction), the objective
itself, whom to recruit, and situations no procedure anticipated. The screen's job is
to hand those over framed, and to take everything else away.

---

## How it stays honest when the agent stops paying attention

The audit's central finding is that a truthful field the agent did not ask for goes
unread: fifty-two of fifty-two. Every part of this design was checked against that.

1. **The screen is the reply, not a field beside it.** What the agent read this run
   was the thing it asked for: the stop reason, a refusal, `triage`'s verdict. What it
   did not read was anything next to a success. So the screen is what `advance`
   returns, and the stop line is its first line.
2. **Decisions owed hold the clock.** An answer is a verb that names the decision.
   Deferring is `defer {id, reason}`, per call, with a required reason, journaled,
   the same three controls `unread_ok` already has. This replaces the read gate. The
   read gate was defeated four ways this run, one of them by piping the journal to
   `/dev/null`; a screen that arrives with the reply cannot be discharged by
   discarding it, because discarding it discharges nothing.
3. **Losses are levels, not events.** A destroyed turret stays on the guns gauge as
   "was 2" until it is rebuilt or written off with `write-off {id, reason}`. An
   alert carries its age. A decision carries its deadline. Nothing important is
   shown once and then gone.
4. **Gauges come from a full count.** Nothing on the screen is fed by a capped read,
   and nothing on the screen gives one coordinate for many things. The reads that are
   capped (`things`, `zones`, a pawn's conditions) say "20 of 76 shown" in their
   first line, and the design says in words that a capped read cannot verify a change
   made across the whole set.
5. **Chores do not need attention.** If the agent skims the screen, the corpse is
   still butchered and the stockpile is still roofed. The cost of a skimmed screen is
   confined to judgement.
6. **It is measured.** Every screen is a file on disk (`frames/<tick>.json` and
   `.png`). The harness log shows whether the agent quoted it. The journal counts
   deferrals and write-offs. The next audit reads those instead of reconstructing
   compliance from command counts.

Two of these need an honest caveat. Whether the lower panels of a screen get read
when the stop line was what the agent wanted is UNKNOWN; the evidence only says that
the reply gets read better than a sibling field. And whether a pushed picture gets
opened is UNKNOWN; seven fetched pictures in twenty-six hours says nothing about a
picture that arrives every turn. Both are measurable from the first run on the
cockpit, and item 6 is how.

---

## Where it lives

The cockpit fits the system that exists: a file bridge polled about once a second,
observers that never mutate, a journal, the `rwa` client, a run contract. It lives
across those layers, and the split follows one rule: image-shaped work and printing
stay in the client; facts and hands stay in the mod.

**In the mod.**
- The screen builder: a `look` verb that returns the screen, and `advance` returning
  the same thing. The digest's sections are its gauges; the new panels are the diff
  against the last screen this client received, the three-row "since you last
  looked", and decisions owed. The last-screen mark lives in memory and is cleared at
  a game boundary like the sampler ring; the first screen after a load says "no
  earlier screen".
- The destroy hook, and who-did-it on every journal row: `agent` (inside the
  poller's drain, with the verb's id), `mod` (inside a chore), `game` (inside the
  tick), `human` (anything reaching a mutation from the game's own input handlers:
  `DesignatorManager.ProcessInputEvents`, `Command.ProcessInput`,
  `FloatMenuOption.Chosen`, `DebugActionNode.Enter`, and the dialog buttons). Each is
  a short postfix; verify every member name against the decompiled source before
  building.
- The chores, and the answer gate.
- Overlays as data in `map-dump`: turret range and line of sight per cell, wind
  cells, power-net membership, roof, home area, gone-since-last. These are the
  game's own `DrawExtraSelectionOverlays` and `PlaceWorker.DrawGhost` made into
  fields.
- `inspect {id}`: the bottom pane (`GetInspectString`), the buttons (`GetGizmos`,
  the read half of `9717e52`), and the overlay for one thing.
- A full count for every gauge.

**In `rwa`.**
- Prints the screen in the shape above; writes `frames/<tick>.json` and renders
  `frames/<tick>.png` after every `advance`. The render already exists (`rwa render`,
  spec `f7b6207`); what was never built is calling it without being asked.
- Names the transcript run in every envelope and warns when `RWA_RUN` falls back;
  waits behind an in-flight advance instead of losing the call; recovers a result
  the client gave up on, since the mod always writes it.

**In the contract and the playbook.** Rules 2, 4, 5, 6, 10 and 11 leave the
contract because the mod now holds them. Rules 1, 3 and 7 become lights. Rules 8 and
9 stay, as judgement. The playbook is for what to research, when to expand, whom to
take in, and what to do in a situation nobody wrote a procedure for.

**In Dorian's viewer.** It reads `frames/`. His screen and the agent's are the same
file.

Two earlier rulings are changed by this, and the change is deliberate:

- The 2026-09-01 ruling on `722c951` rejected attaching the journal delta to the
  advance result, on two grounds: the run that failed had already carried a pointer
  to the delta and ignored it, and the raw delta is up to 42 KB per day-long advance.
  This design attaches neither the pointer nor the raw rows. It attaches the mod's
  reading of them, bounded to a screen, and it moves the gate from "you have read"
  to "you have answered". The first ground is the audit's theme T0 and is answered
  by making the screen the reply; the second is answered by the bound.
- The 2026-09-01 ruling on `7382bdd` reports an unknown argument rather than refusing
  it, except on three fixture verbs whose default mutates. This run found five more
  verbs whose default mutates when a key is dropped: `posture` (no pawns means all),
  `alert-mute` (no op means mute), `carry` (no destination means a default one),
  `trade-start` (no trader means a default one), and `build` (`dry-run` dropped
  means a real blueprint). The rule generalises: a stray key is refused on any verb
  that mutates differently when that key is missing. Hyphenated keys are read as
  underscored at the poller, since no verb reads a hyphenated key.

---

## What to build first

Three roots, in this order. Each is a spec issue in `git-bug`, filed 2026-09-03 under
the round's root `b4adee2`; the pass over every open issue against them is
`ISSUES-DISPOSITION.md` beside this file.

1. **The screen** (`975973e`, with its journal half `827c1bf`, which goes first
   because the screen depends on it). `advance` replies with it. Since-you-last-looked with game, human
   and mod rows; the destroy hook; guns, power and goals gauges; the answer gate. This
   is first because the other two report into it, and because on its own it closes
   the four largest findings of the audit: destruction with no event, the unread
   field, the defeated read gate, and the invisible human.
2. **The map** (`ee4b4f8`). A picture every turn, mod-chosen, with turret range and coverage,
   wind-turbine clearance, power nets, and what is gone; a fixed alphabet that tells
   gravel from sand; the ghost in every placement dry run; `inspect`. Second because
   the two things Dorian named that no issue holds both live here, and because every
   spatial mistake this run made was made without a picture.
3. **The chores** (`ffef0d7`). After a fight, butcher, roof, the research queue, a joiner, a doctor
   gone, tend until stable. Third because each is small once there is a screen to
   report into, and because together they remove most of the contract.

The honesty rules (`27bf321`) and the client work (`70ee75e`) are filed beside these
and are taken as their items come up. Two things land now regardless of the order
above, because they are built and cost nothing: `e440676` (error classes, on a branch, never benched) and seekandkill
`a6b1aa0` (a null guard at a named line, which took autonomous combat out of the
run's final battle).

---

## Calls that could have gone the other way

- **The screen is the reply to `advance`, not a separate `look` after it.** A
  required fetch decays (T11) and the read gate was defeated four ways (T4). The cost
  is two to four kilobytes per advance; the run's agent called `digest` 424 times
  anyway.
- **The clock holds on an unanswered decision.** The alternative is a warning. Rule 6
  was already the contract and nothing enforced it; the escape is per call, with a
  reason, journaled, which is the pattern that already shipped for `unread_ok`.
- **A human at the controls is recorded, not a stop.** The alternative fires once per
  action across nine hand-played days.
- **An ASCII map on every screen, plus the PNG one `Read` away.** The alternative is
  PNG only. The ASCII was consulted ninety times this run and the PNG seven, so the
  text crop is the one that gets looked at; its alphabet is fixed so it stops lying.
- **Chores default on.** The contract's rules were already unconditional.
- **A loss needs a write-off to leave the screen.** The alternative is expiry. The
  alert-mute ruling of 2026-09-01 rejected timed expiry because an expiry the agent
  did not choose is a wake it did not ask for; the same argument, inverted, says a
  loss should not quietly stop being shown.
- **Seven root issues rather than one.** One would be a muster, not a spec; seven is
  one per buildable part, each with its own Acceptance.

---

## What is UNKNOWN

- Whether the lower panels of a screen are read when the stop line is what the agent
  came for. Measurable: quoted lines in the harness log.
- Whether a pushed picture is opened. Measurable: `Read` calls on `frames/*.png`.
- The token cost of a screen in practice. Estimated at two to four thousand per turn;
  the ASCII crop is most of it.
- Whether a postfix on `Building.Destroy` is cheap enough on a 38-mod bench.
  `a8d8ada` warns that `Thing.Destroy` is hot; buildings only, and measure.
- Whether the gun-coverage count is affordable at every stop. About 1,900 cells by
  seven guns is thirteen thousand line-of-sight checks, once per stop, never per
  tick. Probably fine; measure.
- How nine hand-played days should appear. Proposed: one journal row per human
  mutation, folded on the screen to "human: N actions over M ticks" with the rows
  behind a fetch.
- Whether the final save of this run loads. Eight engine warnings promised load
  errors and nobody tested it. Not a cockpit question, but the next run starts from
  a save.

---

## Appendix A: the player's screen, member by member

What a player sees, what the agent's screen shows for it, and where in the game it
comes from. This is the cockpit in one table.

| the player sees | the agent's screen | the game's member |
|---|---|---|
| the map, always | the map panel | `map-dump` planes, plus overlays from `Thing.DrawExtraSelectionOverlays` and `PlaceWorker.DrawGhost` |
| a turret's range ring when selected | ring on the map; coverage on the guns gauge | `Building_TurretGun.DrawExtraSelectionOverlays`; `AttackVerb.verbProps.range`; `GenSight.LineOfSight` |
| a wind turbine's clearance when placing or selected | clearance on the map; blocked-by on `inspect` | `PlaceWorker_WindTurbine.DrawGhost`; `WindTurbineUtility.CalculateWindCells`; `CompPowerPlantWind.CompInspectStringExtra` |
| the game pausing on a letter | a stop | `LetterStack.ReceiveLetter` (already hooked) |
| a building blowing up on screen | a stop; GONE on the map; "was N" on the gauge | `Building.Destroy`, `Frame.Destroy` (new) |
| the alert readout on the right edge | lights, with an age | `AlertsReadout` (already); age is new |
| the colonist bar | colonists | `ColonistBar`'s inputs: mood, health, drafted, position |
| the resource readout, top left | stock and food, map-wide and in stockpiles | `ResourceCounter`, plus a full count |
| the date, weather and temperature, top right | the stop line | `GenLocalDate`, `WeatherManager`, `MapTemperature` |
| the bottom-left inspect pane | `inspect {id}` | `Thing.GetInspectString`, `GetInspectStringLowPriority` (deterioration and why) |
| the buttons on a selected thing | `inspect {id}`, and the write half of `9717e52` | `Thing.GetGizmos`, `Command_Toggle` |
| the history graphs | trends (already) | `HistoryAutoRecorder` |
| the letter stack, bottom right, with its choices | decisions owed, with acts | `LetterStack`, `ChoiceLetter.Choices` |
| the mouse, and the debug menu | since you last looked: human | `DesignatorManager.ProcessInputEvents`, `Command.ProcessInput`, `FloatMenuOption.Chosen`, `DebugActionNode.Enter` |
| the player's own habits: butcher, roof, undraft, tend | chores | the mod |

## Appendix B: the screen as a file, for the builder

One JSON object per screen at `RUNS/<run>/frames/<tick>.json`, beside its PNG. Field
names follow the digest's conventions; the digest's sections are reused as they are.

```
{ time, stop: {kind, why, target, since_last_look_ticks, saved},
  map: {rect, alphabet, ascii, picture, changed, gone: [...], new: [...], open: [...]},
  colonists: [...],
  guns: {by_def: [{def, count, at: [...]}], gates: [...], inside_covered_cells, inside_cells, was: {...}},
  power, resources, temperature, stock, goals: {G1..G6}, alerts: {active: [{id, age_ticks, verdict}], muted},
  since: {game: [...], human: [...], mod: [...], truncated},
  decisions: [{id, kind, text, deadline_tick, acts: [{verb, args}]}] }
```

Every list is capped by importance and says what it dropped; that rule is the
digest's and it applies here without exception.
