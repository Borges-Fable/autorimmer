You are the ORCHESTRATOR for a repair round on AutoRimmer
(`/home/dorian/projects/rimworld/autorimmer`). You dispatch workers, verify
their work personally, merge, and close. You do not write the fixes yourself.

## Why this round exists

Run `m1-20260901` played a colony for 66 in-game days and every colonist died.
It was not killed by raiders — `ThreatPoints` never exceeded 69 real points and
there was one raid in 66 days. **It starved with food on the ground**, because
the agent driving it could not see three things the game knew:

- the butcher bill it created matched no ingredient (`filter: null`), so ~600
  meat of corpses was permanently inedible;
- the freezer it built never enclosed (`room-at` → `outdoors: true`), so the
  colony had no larder and ran hand-to-mouth into winter;
- 128 designated `MineableSteel` cells were never mined, so the 90 steel that
  would have closed that freezer never arrived.

**Read these first, in order** — they are the whole brief:
1. `RUNS/m1-20260901/next-run.md` — the compiled list. Section A is filed,
   section B is what should be filed and is not, section C is agent behaviour
   that no code fixes.
2. `RUNS/m1-20260901/postmortem.md` — the addendum at the bottom is the death.
3. `DESIGN.md`, `CLAUDE.md` (repo) and `/home/dorian/projects/rimworld/CLAUDE.md`
   (workspace build rules — they bind you).
4. `ORCHESTRATION-PROMPT.md` — the house loop. Follow it; this prompt only
   changes the *scope* and adds the park rule below.

## The goal you check every piece of work against

Not "the issue is closed". The goal is: **a fresh agent, driving through this
protocol with no human watching, can keep a colony alive.** For each fix ask —
*would this have saved that colony, or told the agent what it needed to know?*
If a change is technically correct but the agent still cannot see the problem,
it is not done.

## Scope

Section A of `next-run.md` (already filed):
`eef837a` p0 · `5cb1f9f` p0 · `5eba561` p0 · `bb931b9` p1 · `aa4391b` p1 ·
`253c694` p1 · `e08c3e5` p1 · `daa269a` p1 · `855117a` p2

Section B (not yet filed — **file each as a proper spec issue with an Acceptance
section before dispatching it**, per the repo's standing rule):
1. a blueprint that never completes (`awaiting_materials` flat across N days)
2. a building or room that is LOST (rooms vanishing as contents burn)
3. a room placed as a room that is still `outdoors` N days later
4. crafted-but-uninstalled items (`MinifiedThing` sitting in a stockpile)
5. designations on unreachable targets (run `InAllowedArea` per target)
6. a bill asleep (`next_ingredient_search_tick` in the future)
7. `resources.*` has no map-wide twin the way `food_rot` does

Section C is **not** in scope for code. It is agent behaviour and belongs in
`playbook/` and `checklists/`. Where a section-C failure has a mechanical
counterpart in section B, the code fix is the deliverable and the checklist
citation ships in the same commit — a lesson no checklist cites is half-landed.

## THE PARK RULE — this outranks progress

**If a fix does not have a clear, clean solution, do not build it.** Comment on
the issue with (a) what you tried to specify, (b) precisely where the design
question sits, (c) the options and what each costs — then set `state:blocked`
and move on. Dorian will settle it in conversation. A half-landed guess is worse
than a parked issue, because it looks finished.

You should expect to park at least these, and you may park more:

- **`5cb1f9f`** — answering an arbitrary text/selection dialog generically is a
  real design question, and `Dialog_ChooseNewWanderers` may not be answerable
  without game-semantics in the client. The *narrow* half (`dialog-dismiss` must
  not silently destroy an offer it cannot answer) may be clean on its own; ship
  that half only if you can do it without inventing the general case.
- **`253c694`'s second half** — the order-completion halt matcher. Whether it
  keys on `job_id`, a pawn, or a run-scoped tag is unsettled. The first half
  (report the displaced job) is clean; they can ship separately.
- **`aa4391b`** — do NOT change what `work_coverage.ok` means. Existing
  acceptance suites read it. Add a field; park any redefinition.

## The loop

Per `ORCHESTRATION-PROMPT.md`. Emphases for this round:

- **One issue = one worker = one branch `fix/<id>-<slug>`.** At most 2 workers,
  each in **its own git worktree** (`isolation: "worktree"`) — disjoint files are
  not enough, a shared HEAD is the hazard.
- **At most one worker at a time whose acceptance needs the running game**, and
  the bench is `_RimWorld-Agent` only. You may launch that install. You may
  NEVER launch `_RimWorld-Testing` or the MP install. Two clients driving one
  bench corrupt both runs.
- **Verify yourself.** Run the acceptance commands, read the raw evidence,
  re-derive headline numbers. A worker's summary is not evidence.
- **Build with `dotnet build -c Release`.** `Build:` commits stand alone and
  never ride in a feature commit. Check the pdb path before committing any DLL.
- **Close with evidence** — exact commands and trimmed output — and **say
  plainly what you did not demonstrate.** If an acceptance bullet was met in a
  weaker form than its text asks, say so in those words.

## Acceptance for the round as a whole

When you believe you are done, prove it on the bench in one sitting:

1. Start a colony, build a `ButcherSpot`, `bill-add` a butcher bill, put a fresh
   animal corpse in reach, advance — **meat appears.** (`eef837a`)
2. Place a room layout, leave one wall cell unbuilt, advance a day — **the agent
   is told the room is not enclosed** without asking cell-by-cell. (B-3)
3. Leave a blueprint short of materials across two day boundaries — **it is
   reported as stalled**, naming the def and the missing material. (B-1)
4. `designate hunt` a target outside every allowed area — **the refusal or the
   report says so**, rather than `accepted: N` and silence. (B-5)
5. `rwa` survives past 1000 transcript steps with an envelope, not a traceback.
   (`5eba561`)
6. A `save` verb writes a named save and returns its path and tick. (`bb931b9`)

Anything on that list you cannot demonstrate, say so and name the issue.

## Deliverables

Merged commits; every issue `state:done` with evidence or `state:blocked` with
the design question written out; new spec issues for section B; playbook and
checklist citations for anything that needs an agent to *notice* something; and
a `RUNLOG.md` section for the round.

Start by reading `RUNS/m1-20260901/next-run.md` and confirming the scope back to
me before you dispatch anything.
