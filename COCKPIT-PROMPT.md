# Design the cockpit

## What this is for

A Claude agent played RimWorld for 26 hours through the AutoRimmer bridge and lost the
colony. Everything it did was recorded, and the record has been audited: 360 findings,
19 themes, in `RUNS/openrun-20260902/audit/`.

Dorian owns this project. He wants to know **what the agent should be looking at while it
plays** — and he wants that as a design he can build the next round of work from, not as
a longer backlog. The backlog is already 84 open issues, every one of them true, and
nobody can tell which three matter. That is why the question is put this way round: the
right answer to "what does the agent see" is what those 84 were circling.

His phrase for it: *"Put the agent in the same cockpit the player has."* A person playing
RimWorld selects a turret and the game draws its range. Places a wind turbine and sees
the clearance it needs. Watches a wall come down. The agent had 44 verbs it could call one
at a time and had to decide, every turn, which of them to bother with.

He is also part of the record. The run was not unattended: Dorian intervened in the game
at least 22 times — reviving a colonist, deleting a weather event, sending food, playing
nine in-game days by hand — and none of it is marked as his anywhere. Two colonists joined
and died entirely outside the agent's view. Each intervention is a place where a human
could see something the agent could not, so a real part of what the cockpit is for is
letting him stop doing that.

## The material

Everything is in `RUNS/openrun-20260902/audit/`. `README.md` says how the audit was built
and where it is weakest — read the weaknesses before trusting a number. `themes.md` is the
file to reason over. `issues.md` lists all 84 open issues (76 in `autorimmer`, 8 in
`seekandkill`) grouped by area. `tables.md`, `findings.md` and `spine.ndjson` are the
arithmetic, the raw findings and the merged timeline, for checking claims.

Issues live in `git-bug`, not files: `git-bug bug --status open`, `git-bug bug show <id>`
(`--format json` for the full body). `seekandkill` is a separate repo at
`/home/dorian/projects/rimworld/seekandkill` with its own store.

The mod is real and running; `DESIGN.md` is its architecture and `Source/AutoRimmer/` its
code. The cockpit has to fit the system that exists — a file bridge polled about once a
second, observers that never mutate game state, a journal, a client called `rwa`, a run
contract the agent plays under. It may live at any of those layers or across them; that
choice is part of the design. The decompiled game source is at
`rimworld-tools/Info/decompiled/RimWorldBase/` and is the authority on what the game
itself knows and shows.

## Three facts you would otherwise have to dig for

These are evidence, not conclusions. What they mean for the design is yours to decide.

**The project has already tried "report it in a truthful field," and this run measured
the result.** Theme T0. The bridge is unusually honest: when it drops an argument it says
so in the reply, naming the correction. That field appeared in 52 result envelopes and the
agent read it zero times. Issue `7382bdd` was closed on 2026-09-01 on a ruling that traded
*refusing the bad call* for *reporting it in a field*; this run is the experiment that
ruling implied. Nine other open issues have the same shape and the same standing remedy.
Whatever the cockpit does with information the agent did not ask for, it has to account
for that result.

**The picture channel was never built.** The PNG render (`f7b6207`) is an open spec. The
run directory holds seven pictures for 176 in-game days, the last of them from day 9,
against 584 buildings placed. What the agent had instead was an ASCII map that draws
gravel, sand and rock as the same character, and a grid dump that prints upside down
(`8847053`). When it did look, it looked at a rectangle it chose after deciding where to
build — which is how it sealed its own workshop door and put seven blueprints inside a
planned kitchen. The audit does not have a theme for this because pictures were too rare
to leave a pattern.

**Destruction is not an event.** The journal has fifteen event types (`tables.md` §7) and
none of them is "something was destroyed"; construction rows come in exactly two kinds,
`completed` and `failed`. When a manhunter pack destroyed two turrets and an autocannon,
the journal recorded nothing. The only turret rows in that window are the agent building
replacements for the two it happened to count. It never learned the third was gone, and
its own account names that as the first link in the chain that ended the colony.

Theme T11 measures how every discipline in the run contract decayed over the four
quarters of the run — looking before placing went from 22 calls to 2 while building went
from 134 to 209. It bears directly on what is pushed at the agent versus what it fetches;
draw your own line from it.

## Two things Dorian named that are in no issue

**Turret range.** Nothing publishes weapon range, turret coverage or line of sight. The
agent made 84 calls naming a turret and could never ask what any of them covered. Its own
account of the wipe: *"every turret points outward at a gate… the lancer spent roughly
200,000 ticks unopposed inside the base."*

**The windmill.** Nothing publishes a wind turbine's clearance or its output over time. The
agent dry-ran four positions, built two, found both on power nets disconnected from the
base, hand-traced conduits twice, and gave up on them. Around day 170 it discovered the
map has four steam geysers and it was using one, with geothermal power an unmet goal the
whole run.

Neither is in the 84. The backlog is not over-complete; it is under-complete in the
direction he is pointing.

## What to produce

**1. The sketch.** A drawing or layout of the cockpit and enough plain writing to explain
it, at `RUNS/openrun-20260902/audit/COCKPIT.md`, images beside it if it needs them. It is
the root of something he builds on, not a finished system: small enough to hold in your
head, and it picks one design rather than offering three.

It should make clear what the agent sees and when; what arrives whether it asked or not
and what it has to go and fetch; how the cockpit stays honest when the agent stops paying
attention; and — the thing Dorian will use most — where the line falls between what is
mechanical and what needs a decision. Some of what went wrong should never have reached
the agent: butcher the corpse, re-count the turrets after a raid, roof the stockpile.
Other things are judgment: take this refugee, press this attack or pull back. Draw that
line.

Write it in clear English. No jargon, no invented vocabulary, nothing that needs a
glossary; if a sentence would not survive being read aloud to someone who has not read
the audit, rewrite it.

**2. Every open issue accounted for.** All 84. Rewrite, merge, close into the root, or
leave alone — whatever each needs once the root is decided — and leave a single place
where a reader can find what happened to every id. Nothing silently skipped.

The audit names roughly 30 of the 84. For the rest it is not evidence: they are specs for
work never built, or findings from earlier runs this one did not exercise. "Untouched;
this run says nothing about it" is a real disposition and better than a rewrite with
invented justification.

Expect consolidation. `6d4ca8a`, `c621849` and `4c12e5d` are one gap filed three times
(the verb that installs a sculpture, the goal it blocks, the run where five sculptures sat
in a crate). Nine issues are instances of T0 alone. Closing issues into the root is
probably the right outcome for a good number of them.

## How to work

The sketch wants one mind — it is the part that has to be coherent. The issue pass is
derivative once the root is decided and splits cleanly across subagents if you want it
to.

Dorian is watching and happy to be asked. Don't stall on him: decide provisionally, mark
the assumption where he will see it, keep working, and change it if he answers
differently. Where you made a call that could have gone the other way, say so in a line.
Where the evidence does not exist, write UNKNOWN — the audit does this throughout and it
is why the audit is usable.

Work on the branch `audit/openrun-20260902`, or branch off it. Commit the sketch and the
issue pass separately.

## Rules

- **Change no code and launch nothing.** Read the mod's source and the game's source
  freely; this is a design job.
- **`git-bug` silently deletes any line starting with `#` from a body passed with `-F`.**
  At commit time, with no message. Your bodies will be full of markdown headings. Use `-m`
  with a single-quoted heredoc (double quotes let backticks eat lines — that happened this
  run), and read each issue back with `git-bug bug show <id>` before moving on.
- `git-bug bug new` prints the new id; capture it. `git-bug bug | head -1` grabbed the
  wrong issue this run and retitled a closed spec.
