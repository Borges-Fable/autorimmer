# Design the cockpit

A Claude agent played RimWorld for 26 hours through a bridge mod and lost the colony.
Everything it did was recorded. That record has been audited, and the audit is the input
to this job.

Your job is to answer one question:

**What should the agent be looking at while it plays?**

Not "which of these 76 issues should we fix." That question has been asked for weeks and
the backlog has grown to 76 open items, every one of them true, with no way to tell which
three matter. This is the other question — the one that, answered well, tells you what
the 76 were all circling.

---

## Read these first

Everything is in `RUNS/openrun-20260902/audit/`.

| file | what it is |
|---|---|
| **`README.md`** | How the audit was built, and where it is weakest. **Read the weaknesses before you trust a number.** |
| **`themes.md`** | 19 themes — cause, occurrences, cost, and whether an issue already covers it. **8,600 words. This is the one that matters.** |
| **`issues.md`** | All 84 open issues in `autorimmer` (76) and `seekandkill` (8), grouped by area |
| `tables.md` | The arithmetic — every command, every failure, the two journals |
| `findings.md` | All 360 raw findings, if you need to check a claim |
| `spine.ndjson` | The merged timeline: 6,674 commands and 7,039 game events, in order |

Issues live in `git-bug`, not in files: `git-bug bug --status open`, and
`git-bug bug show <id> --format json` for a full body.

---

## The idea, in Dorian's words

> "Put the agent in the same cockpit the player has."

A human playing RimWorld sees things the agent has never had. When you select a turret the
game draws its range. When you place a wind turbine it draws the clearance it needs and
the cells that block it. When a building is destroyed you watch it happen. You see the
room you are about to seal, the power line you are about to orphan, the corner your guns
do not cover.

The agent had none of that. It had 44 verbs it could call one at a time, and it had to
decide, every turn, which ones to bother with.

---

## Four things the audit found that you should not have to rediscover

**1. The tools tell the truth in fields nobody reads.** This is theme T0 and it is the
spine of the whole audit. The bridge is unusually honest: when it drops an argument it
says so, in the reply, naming the correction. **That field appeared 52 times in this run
and was read zero times.** Worse, the project already decided this was the fix — issue
`7382bdd` was closed on a ruling that traded *refusing the bad call* for *reporting it in
a field*. This run is the experiment that ruling implied, and it failed. Nine other open
issues are instances of the same shape. **If your cockpit's answer is "publish another
field," say why this time is different.**

**2. A surface the agent must choose to consult will rot.** `find-rect` is the verb whose
entire job is "look before you place." It ran 22 times in the first quarter of the run and
**2 times in the last**, while building went from 134 placements to 209. Every discipline
in the run contract decayed the same way: pictures, saves, daily checks, the written
ledger. Nothing measured whether any of them were happening. **A cockpit the agent opens
when it remembers to is the same design that already failed.**

**3. The picture channel was never built.** The agent produced **7 pictures in 176 in-game
days** against 584 buildings placed. That is not laziness — the PNG render channel is an
open spec (`f7b6207`) that was never delivered, the ASCII map collapses gravel, sand and
rock into one character, and the data dump prints upside down (`8847053`, open, p2). When
the agent did look, it looked at a rectangle it chose *after* deciding where to build —
which is how it sealed its own workshop door and put seven blueprints inside a planned
kitchen.

**4. Destruction is not an event.** The game journal has fifteen event types and none of
them is "something was destroyed." Construction rows come in exactly two kinds, `completed`
and `failed`. When a manhunter pack destroyed two turrets and an autocannon, **the journal
recorded nothing at all** — the only turret rows in that window are the agent building
replacements. It rebuilt the two it happened to count and never learned the third was
gone. That is the first link in the chain that killed the colony.

---

## Two examples worth designing against

Dorian named these off the top of his head. Neither is in the 76.

**Turret radius.** Nothing on the surface publishes weapon range, turret coverage or line
of sight. The agent made 84 calls naming a turret and could never ask what any of them
covered. Its own account of the wipe: *"every turret points outward at a gate… the lancer
spent roughly 200,000 ticks unopposed inside the base."*

**The windmill cycle.** Nothing publishes a wind turbine's clearance or its output over
time. The agent dry-ran four positions, built two, found both sitting on power nets
disconnected from the base, hand-traced conduits twice, and then gave up on them: *"I'll
stop counting the turbines as real generation."* It also discovered on roughly day 170
that the map has **four steam geysers and it was using one**, with geothermal power an
unmet goal the whole run.

---

## What to produce

**Two things, in this order. The first is what Dorian reads.**

### 1. The sketch, and a short description of it

A drawing or layout of the cockpit, and enough plain writing to explain it. This is meant
to be **the root of something he builds on, not a finished system.** Small enough to hold
in your head. If it needs a diagram, draw one.

Answer these inside it, but answer them by designing, not by listing:

- What does the agent see, and when does it see it?
- What is pushed at it whether it asks or not, and what does it have to go and fetch?
- **What is mechanical and what requires a decision?** Some of what went wrong here should
  never have reached the agent at all — butcher the corpse, re-count the turrets after a
  raid, roof the stockpile. Other things are genuinely judgment: accept this refugee, press
  this attack or pull back. Draw that line. It is the most useful thing you can do.
- How does it stay honest when the agent stops paying attention?

**Write it in clear English.** No jargon, no invented vocabulary, nothing that needs a
glossary. If a sentence would not survive being read aloud to someone who has not read the
audit, rewrite it.

### 2. Every open issue accounted for

All 84 — 76 in `autorimmer`, 8 in `seekandkill`. Rewrite them, merge them, or leave them
alone, whatever each one needs once the root is decided. Nothing may be silently skipped.

**One warning, so you do not waste the run.** The audit names 30 of the 84. **It has
nothing new to say about the other 54.** Those are specs for work never built, or findings
from earlier runs this one did not exercise. Rewriting them *from this audit* means
inventing justification. Mark them honestly as untouched instead — that is a real answer,
and it is better than 54 rewritten documents nobody can trust.

Expect real consolidation. `6d4ca8a`, `c621849` and `4c12e5d` are three separate issues for
one gap: the verb that installs a sculpture, the goal it blocks, and the run where five
sculptures sat in a crate. Nine issues are instances of theme T0 alone. **Closing issues
into the root is a legitimate outcome and probably the right one for a good number of
them.**

---

## How you work is up to you

Use subagents or don't. Fan the issue pass out or write it yourself. Dorian's only
constraint is on the result: the sketch should be simple enough to build on, and the issue
pass should leave nothing hidden.

Two suggestions from the audit, take or leave:

- The sketch needs one mind. It is the one part that must be coherent, and splitting it
  will produce a committee design.
- The issue pass is derivative once the root is decided, so it splits cleanly.

---

## Nothing here is waiting on an answer

**This brief has no unresolved questions in it.** You can start immediately. The design
questions it poses are the job, not gaps someone forgot to close.

**Ask Dorian whatever you like** — he is happy to be asked and he is watching. Two things
only:

- **Don't stall on it.** Decide provisionally, mark the assumption where he will see it,
  and keep working. If he answers and you were wrong, change it.
- **Don't hand him a menu instead of a design.** A sketch that picks one option and is
  wrong is worth more than one that lists three and picks none — he can argue with a
  decision and he cannot build on a list. This is the standing rule here (`CLAUDE.md`:
  *"Ambiguity is RESOLVED, not queued"*), and it exists because he said there should be
  nothing on the muster waiting for him.

Where you made a call that could reasonably have gone the other way, spend a line saying
so and a line saying why. Three or four of those across the whole sketch, not thirty.
Where the evidence genuinely does not exist, write UNKNOWN — that is a finding, not a hole
in your work.

---

## Practicalities

- **Work on the branch `audit/openrun-20260902`**, where the audit lives. Branch off it if
  you prefer.
- **Write the sketch to `RUNS/openrun-20260902/audit/COCKPIT.md`** — beside the evidence it
  comes from. If it needs images, put them next to it.
- **`seekandkill` is a separate repo** at `/home/dorian/projects/rimworld/seekandkill`,
  with its own `git-bug` store. 8 of the 84 open issues are there.
- **Commit your work.** The sketch and the issue pass are separate commits.

---

## Rules

- **Change no code and launch nothing.** Read the game's decompiled source freely
  (`rimworld-tools/Info/decompiled/RimWorldBase/`), read the mod's source freely
  (`Source/AutoRimmer/`), but this is a design job.
- **You may edit issues.** That is the second deliverable. Read each one back with
  `git-bug bug show <id>` after you write it.
- **`git-bug` silently deletes any line starting with `#` from a body passed with `-F`.**
  It does this at commit time and tells you nothing. Your issue bodies will be full of
  markdown headings. Use `-m` with a single-quoted heredoc, and check what actually landed.
- **Two more `git-bug` traps this run hit:** shell backtick substitution ate a line out of
  an issue body, and `git-bug bug | head -1` grabbed the wrong issue and retitled a closed
  one. `git-bug bug new` prints the id — capture it.
- **Say UNKNOWN rather than guessing.** The audit does this throughout and it is why it is
  usable.

---

## One thing to keep in view

The run was **not unattended**. Dorian intervened in the game at least 22 times — reviving
a dead colonist, deleting a weather event, sending food, playing nine in-game days by hand
— and none of it is marked as his anywhere in the record. Two colonists joined and died
entirely outside the agent's view.

So a real part of what the cockpit is for is **letting him stop doing that**. Every one of
those interventions is a place where a human could see something the agent could not.
