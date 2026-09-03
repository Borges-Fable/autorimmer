# Issue inventory — `autorimmer` and `seekandkill`

Read out of git-bug (`refs/bugs/<64-hex>`) on 2026-09-03. **Nothing here was filed,
edited, or closed by this audit.** Ids are 7-hex short ids; `git-bug bug show <id>`
for the full body, `--format json` for the machine form.

| repo | total | open | closed |
|---|---:|---:|---:|
| `autorimmer` | 135 | 76 | 59 |
| `seekandkill` | 46 | 8 | 38 |

Two genres are mixed in `autorimmer`, and they behave differently when you match a
theme against them:

- **`type:spec`** — the 23-issue numbered build plan (`1.1`…`5.x`, gated by `wave:0..5`).
  These describe *work not yet done*. A theme matching a spec usually means the theme
  is already in the plan but unbuilt, not that it is fixed.
- **`type:bug` / `type:enhancement`** — observations filed *from* previous runs. These
  describe *behaviour already seen*. A theme matching one of these is the COVERED /
  PARTIAL case that matters.

## Open in `autorimmer` — 76

### observation/digest (22)

| id | pri | type | state | wave | title | summary |
|---|---|---|---|---|---|---|
| `d9d6c12` | p1 | enhancement | doing | 3 | A bill asleep: next_ingredient_search_tick in the future is the only proof and nothing flags it | `Bill.next_ingredient_search_tick` in the future means the bill ran a failed ingredient search and backed off. It is a sleep state, and it outlives its cause: a bill that failed while its ingredients were forbidden stays dead long after… |
| `f9dadc7` | p1 | enhancement | doing | 3 | A blueprint that never completes is invisible: awaiting_materials sat flat at 22 for fifteen days | `ConstructionVerbs.Section(map)` publishes `awaiting_materials` as a scalar count. A scalar tells you the size of the set, never its age. Fifteen identical reads and one read are the same envelope, so "stalled" is only visible to a… |
| `2d9a1da` | p1 | spec | next | 4 | Colony sampling: the harness records no rates, only events — so every leading indicator is unreachable | 2026-08-31. Pairs with 4.1 (96d9315); see SEQUENCING below. THE GAP DESIGN already states the problem and does not solve it: "alerts fire LATE — tattered-apparel alert means the mood penalty already landed", and the 1.6 issue records… |
| `40ed42f` | p1 | spec | doing | 2 | Doctor coverage is computable, so the mod should compute it: work_coverage in the digest, and a roster-change repair | Lesson `[[one-doctor-is-zero-doctors]]` rescoped as code, per Evan's ruling of 2026-09-01 that a deterministic finding goes in the mod, not in a note (DESIGN decisions log; `postmortem.md` step 5 and the ladder's mod rung, now… |
| `61794cd` | p1 | spec | doing | 2 | The observation surface hid the cause of death: BloodLoss was cut by the hediff cap, and there is no bleed-out clock | OUT-2 of the M1 post-mortem (`RUNS/m1-20260831/postmortem.md`). Class: no-signal + mistrusted-signal, landing at the mod rung — every input is already read, so per DESIGN's 2026-09-01 ruling this cannot stop at prose. ## The measurement… |
| `e811574` | p1 | enhancement | backlog | 3 | resources.* has no map-wide twin the way food_rot does, so 0 in stockpiles reads as none | `resources.food_rot` publishes both a stockpile-scoped and a map-wide figure with an explicit `scope` note, precisely because the difference is load-bearing. `steel`, `wood`, `meds` and `components` publish one number, stockpile-scoped,… |
| `aa4391b` | p1 | enhancement | backlog | 3 | work_coverage cannot see an outranked doctor: a floor met only by outranked pawns reads ok | Wouter went down at tick 244,343 and developed a wound infection. Over the next four in-game days it climbed 0.13 → 0.93, with 1.0 the value that kills, and nobody tended him for roughly 8,000 ticks after the rescue put him in his own… |
| `d2e1229` | p2 | spec | backlog | 4 | 4.2 Play-loop skill | The play loop as a reusable skill/prompt: how an agent session actually runs a colony — cadence, checklist discipline, escalation, artifacts. |
| `f1a1700` | p2 | enhancement | blocked | 3 | A building or room that is LOST: rooms vanish from temperature.rooms as their contents burn | A building being destroyed, or a room losing the role it was built for, is a structural regression. Nothing in the observation surface reports one. 1. A room whose `role` changes away from a previously observed role is reported, naming… |
| `a8d8ada` | p2 | enhancement | backlog | 4 | A corpse that vanishes has no reason attached: journal the destroy with its DestroyMode | Observed live on the bench, 2026-09-02, while proving `eef837a`. A fresh `Corpse_Cat` was unforbidden, the butcher bill went `workable`, the run advanced ~80,000 ticks — and the corpse was gone. No `Meat_Cat`, no butcher job, bill back… |
| `4c12e5d` | p2 | enhancement | backlog | 3 | Crafted-but-uninstalled: five sculptures sat minified all run giving zero beauty and nothing counted them | The agent had commissioned the art, seen the bills complete, and had no way to learn that the output was inert. Mood is a survival term at this scale; the postmortem records that at 42 threat points beauty was free and was declined. 1.… |
| `ae78ecc` | p2 | spec | backlog | 2 | No observer reads Pawn_RecordsTracker: PlantsHarvested, MealsCooked, ThingsConstructed are the direct proof of production, and M1 grades them by inference | Found while rewriting `664e9b9` (the M1 re-run: 20 days, thrive). Its "real food cycle" bullet has to prove grow -> harvest -> cook -> eat, and today every proof is an INFERENCE: `zones` growth reaching harvest, a raw-crop def appearing… |
| `261f2e9` | p2 | enhancement | doing | — | No verb can set a temperature target, so a built freezer can only hold the vanilla 21C default | Found by the M2 rehearsal (session 18), one step past the room that met M2. ## What happened `templates/freezer-kitchen.ir.json` was placed, built by colonists, and wired to the power room's generator. `digest.power` went from `draw_w:… |
| `91bc250` | p2 | enhancement | backlog | 3 | Nothing requires a verdict on an active alert, and an active alert carries no age | The mod has the two ends of this and not the middle. `alert-mute` is a deliberate, reasoned act with a required `reason`, and `digest` is explicitly described in `AlertMuteVerbs.cs` as *"what keeps it from being forgotten"*. But an… |
| `d16a463` | p2 | bug | backlog | — | Observers flush the region updater: GetRoom can rebuild rooms and write scribed state | Found while implementing 261f2e9 (temperature), by auditing every getter the two new files reach. It is NOT a defect of that work — it is a property of the whole observation surface that predates it, and the fix belongs to all of it at… |
| `cc8988c` | p2 | spec | backlog | 4 | Post-raid procedure: an event-triggered checklist, and the downed-hostiles read it depends on | Evan, 2026-08-31. The first concrete instance of a general idea: take deterministic decisions off the agent by running a fixed procedure when a trigger fires, and be intentional about what is NOT deterministic enough to automate. THE… |
| `c41bdcc` | p2 | spec | backlog | 4 | Weapons: no vanilla alert covers armament and ResourceCounter cannot count one, so the harness still cannot say how armed the colony is | Split out of 2d9a1da by the rates worker, session 22, rather than smuggled into it. 2d9a1da shipped `history` (the game's eleven recorders) and `trends` (a 2,500-tick sampler over what the game does not record). Weapons is the one… |
| `9b179ef` | p2 | bug | doing | 2 | digest.work_coverage.order says 'under-first' and the rows are not: the only under-covered row lands at index 1 | Found by the worker writing `40ed42f`'s acceptance suite (session 21) and confirmed independently from a live envelope the orchestrator had already banked before the finding was reported: `accept/runs/s21-20260901/03-digest.json`. ##… |
| `47547ca` | p3 | spec | backlog | 4 | 'Armoured' is unreadable: apparel rows carry no armor rating, so a militia's protection cannot be graded | Found while rewriting `664e9b9` (the M1 re-run). Evan's militia is "armed, armoured, posture set, able to survive a raid". Three of the four are readable today — armed from `pawn {sections:["equipment"]}` (`primary`), posture from… |
| `d32eadd` | p3 | spec | backlog | 4 | 4.4 Checklist budget + lesson retirement | escalation step additionally wants 5.1. Filed from a design conversation with Evan, session 5 (2026-08-30): "lets file the checklist budget as a follow up issue." The playbook must not accrete. 4.1 defines the escalation ladder (prose… |
| `65e7cf9` | p3 | spec | backlog | 1 | A dead rwa client leaves the game RUNNING: the mod has no client liveness at all | Finding G (mod half), from M1 run `m1-20260831`. The client half is being fixed separately; this issue is the mod's. ## What happened An `rwa advance` whose CLIENT process died left the game advancing at `Ultrafast` with nobody… |
| `3275f0c` | — | — | — | — | No verb for ideology roles or rituals — costs ~9 mood per colonist with no lever | `rwa verbs` returns nothing matching ideo, role or ritual. The observation surface has no `pawn {sections:['ideo']}` either (git-bug for that is already noted in the run contract's known-gaps list), so precepts driving mood are… |

### spatial/map (19)

| id | pri | type | state | wave | title | summary |
|---|---|---|---|---|---|---|
| `a1644d6` | p0 | bug | doing | 3 | A layout placed as a room that never encloses is invisible: the freezer read outdoors:true all run | Run `m1-20260901`: the freezer was placed as a layout, built, and never enclosed. `room-at` on its interior returned `outdoors: true, cells: 60082` — the great outdoors — for the rest of the run. The colony therefore had no larder, ran… |
| `acee526` | p1 | spec | next | 1 | 1.9 Placement must be exact-or-refuse: things slide silently today | Filed by the orchestrator, session 6, BORGES, at Evan's call. The bug, reproduced live `dev:spawn-thing` uses `GenPlace.TryPlaceThing(..., ThingPlaceMode.Near)`. When the requested cell is occupied, Near does what it is designed to do:… |
| `1adc737` | p1 | spec | backlog | 3 | 3.3 Build verbs + place-layout (IR) | Building: single-blueprint verb + whole-layout placement from baseviz IR, in blueprint mode (pawns build it — real play) and instant mode (dev-spawn the built result — fixtures). |
| `e08c3e5` | p1 | bug | doing | 3 | Build preflight ignores constructionSkillPrerequisite, and reports the skill gate as a material shortfall | The barracks layout `ly-1` was preflighted with `place-layout --dry-run`, which reported a material shortfall of exactly 6 steel, was corrected, re-run clean (`shortfall: []`), and placed. 33 elements, zero blockers, zero refusals. 32… |
| `9717e52` | p1 | spec | backlog | — | No verb reads or presses a thing's GIZMOS — shuttle autoload, turret hold-fire, door hold-open are all unreachable | resolved it by hand; the agent had no route to it. Quest "The Solitary Knight" (accepted, Ongoing). The lodger Eurplaerepla is `faction: PlayerColony`, `class: colonist`, standing 16 cells from his own shuttle (`Shuttle` id 13252 at… |
| `4950f14` | p1 | bug | backlog | 4 | construction.gaps serializes as a .NET type name: MiniJson silently ToStrings List<Dictionary<string,object>> | A .NET type name where a list of cells should be. The "name the gap" clause of `a1644d6` is unreadable in shipped code, and it is the clause that turns "your room is open" into "your room is open HERE". `MiniJson.cs:75` matches `case… |
| `8e5db24` | p1 | spec | backlog | — | designate hunt: no verb publishes an animal's manhunter chance, and composition reports the TERRAIN under the animal instead of the animal | Found in play, run openrun-20260902 day 6 (summer 1). Dorian caught the bad call; the mod had the information and put it where the decision could not use it. ## What happened Five hunts designated in one call — 2 emu, 1 ostrich, 1… |
| `daa269a` | p1 | bug | doing | 3 | room/rooms report owners_total 0 while beds[].owners in the same envelope names all three owners | `room {id:38}` on a colonist-built barracks: Three beds, three distinct owners, one per bed — and `owners_total: 0` with `owners: []` in the same response. The per-bed `owners` arrays are right; the room-level rollup is wrong. `rooms`… |
| `039e359` | p2 | spec | backlog | 3 | 1.6 until:{condition\|layout}: four branches the acceptance suite never reaches | Filed by the session-19 round itself, unasked, because `accept/fc287ba-until-state.py` returned 140/140 on a live bench and a green number that size reads as coverage it does not have. Four branches of the shipped code were never… |
| `f7b6207` | p2 | spec | next | 2 | 2.5 PNG render channel (baseviz) | The PNG channel: render any map rect as an image the agent Reads directly — the independent second check on textual spatial reasoning, and Dorian's at-a-glance view of what the agent built. |
| `36c03c9` | p2 | spec | backlog | — | BRAINSTORM: a pickup / equip / drop engine — equipment reallocation cannot be composed, and cannot be planned while paused | four separate verbs each behave correctly on their own and the COMPOSITION of them is what a colony actually needs and cannot express. A new colonist arrived (Fitz, Shooting 8) who should hold the bolt-action rifle currently carried by… |
| `bac4eba` | p2 | spec | backlog | 3 | Base composition: a 7x7 module grid, so layouts tile instead of landing as islands | Design decision by Evan, session 11 (`65b03c2`), 2026-08-31, in his own framing: > "separate rooms would be the easiest but a base is usually connected rooms, a > grid would be the easiest, people usually make 7x7 rooms and then they… |
| `95dbdfc` | p2 | enhancement | backlog | — | IR dialect has no per-element stuff channel, so one place-layout call cannot give a layout two wall materials | Found designing RUNS/openrun-20260902/andbourne-ii.ir.json (Andbourne II). ## The gap The design wants GRANITE on the 144-cell perimeter (MaxHitPoints x1.7, where things get shot) and SANDSTONE on the 211 interior wall cells… |
| `855117a` | p2 | enhancement | doing | 3 | Mine designations cannot be aimed: map-view collapses ore into rock, and mine-vein is unreachable from the checklist | Aiming at the compacted-steel face 20 cells east of the base, the run issued: The intent was ~11,000 steel out of the 274 `MineableSteel` cells the map carries. What was actually designated is 14 cells of *whatever rock was exposed on… |
| `927be4f` | p2 | bug | backlog | 3 | Two age vocabularies shipped in one round: unenclosed_for must move to f9dadc7's tri-state | \| \| `a1644d6` `unenclosed_for` (per layout) \| `f9dadc7` (per element) \| \|---\|---\|---\| \| start \| `since_tick` \| `state_since_tick` \| \| elapsed \| `ticks` \| `state_age_ticks` \| \| age unit \| `day_boundaries` (int) \| `state_age_days` (float)… |
| `8847053` | p2 | bug | backlog | — | baseviz view prints the grid upside down while its header says north up | says "north up". Row 0 of the IR is NORTH (pinned, `bac4eba`, `templates/INDEX.md`), and the view puts row 0 at the BOTTOM. A deliberately asymmetric 3x4 IR: `Bed` on grid row 1 (second from north), `Door` on grid row 3 (south wall).… |
| `15842b9` | p2 | bug | backlog | 4 | bills and food count things nobody can reach (materials does NOT — original filing was wrong) | Evan, watching the bench on 2026-09-02 after `b7359fa` landed: *"that's deterministic, it should tell you what's on the ground and next to that if it's forbidden or in a forbidden area — should this framework be anywhere else?"* As… |
| `1de2fbe` | p2 | bug | backlog | — | rwa/selftest.sh fails 28 checks when run the way its own README says to run it | `rwa/README.md` documents the self-test as: Run that way — from `rwa/`, which is what `./` means there — the suite reports 227 passed, 28 failed. Run from the repo root it reports 255 passed, 0 failed. Same commit, same code, same… |
| `e1c072e` | p3 | spec | backlog | 5 | Prisoner capture and the prison room — its own research task, after the rest is done | Evan, 2026-08-31, filed deliberately as LATER: "capturing prisoners and having a prison room should be filed too, only after everything's done since taking care of / recruiting prisoners is its own research task." So this is scoped as a… |

### pawn/work (10)

| id | pri | type | state | wave | title | summary |
|---|---|---|---|---|---|---|
| `664e9b9` | p1 | spec | next | 4 | 4.3 M1: thrive-20-days run (rewritten from survive-10-days, 2026-09-01) | Prove the loop end-to-end: three colonists survive ten in-game days under agent control, and the platform learns something from it. |
| `29824e4` | p1 | enhancement | backlog | — | Dev suite needs organization and a runtime toggle: today it is 17 loose verbs with no arming gate | ## What `rwa verbs` publishes the god-hand as ~17 flat `dev:*` entries plus two fixtures (`pawn-fixture`, `world-fixture`) sitting in the same namespace as the ~44 player verbs, with no grouping, no listing, and no way to arm or disarm… |
| `253c694` | p1 | enhancement | backlog | 3 | Forced orders collide silently, and completion is only pollable | Five forced orders were issued to two pawns while the game was PAUSED, in five separate calls: Every one returned `ok:true` with an `accepted` row naming the job it started, `player_forced:true`, `queue:false`, `queue_depth:0`. All four… |
| `e1a9542` | p1 | bug | backlog | — | pawn-fixture with NO args silently wounds a pawn: RefuseStray guards strays, nothing guards the empty case | ## What `pawn-fixture` called with no arguments mutates game state. It wounds a colonist, stacks four `Debug bad thought` memories (-40 mood) and destroys three apparel items' durability — with no confirmation and no dry-run.… |
| `3d53df2` | p2 | enhancement | backlog | 4 | A transcript's step order is CLAIM order, not completion order, and nothing on disk says so | Found while building the cockpit (df378fa) against run `m1-20260901`. The measurement. Across the 4,599-step chain, 141 adjacent step pairs have their `result.ts` inverted — step N+1's result came back before step N's. 80 of those are… |
| `826d4bf` | p2 | spec | backlog | 3 | No verb can use a targetable item on a target — resurrector serums, shock lances, EMP are unreachable | Found by play, 2026-08-31. Evan asked for a resurrector serum to be used on a dead colonist. There is no route. The verb surface has exactly one item-use verb, `consume`, and it is INGEST (food, drugs, medicine). Everything whose use is… |
| `1d381be` | p2 | enhancement | backlog | 3 | designate's reach report is allowed-area only: a target inside the area can still be unpathable, and that is the same silence | `b7359fa` shipped `designate`'s `reach` block: per target, over the roster of colonists capable of the work, `RimWorld/ForbidUtility.InAllowedArea`. It closes the case that killed `Marco`. It does not close the neighbouring one, and the… |
| `ff1f0b9` | p2 | spec | backlog | 3 | dev:add-gene — no shipped verb can make a pawn incapable of violence, so an acceptance fixture is a dice roll | Found while building `accept/b1b3060-posture.py` phase 3, whose acceptance bullet is "a pawn incapable of violence is refused BY NAME". There is no deterministic way to produce that pawn from the shipped verb surface. ## What was… |
| `00a1be7` | p3 | spec | backlog | 4 | combat-report: read the battle log by field, never by the game's sentence builder | Evan asked for proof that something USEFUL could be concluded from RimWorld's combat logs before anything was filed. It can. Filed on that basis. WHAT IS DERIVABLE (confirmed by an investigation that built them from source) - Who shot… |
| `35a84f6` | p3 | enhancement | backlog | 3 | dialog-accept takes the game's generated name and nothing else: the agent cannot name its own colony | `dialog-accept` (shipped 2026-09-02, git-bug `5cb1f9f`) answers a `Dialog_GiveName` by accepting the value the window already holds — the name `NameGenerator.GenerateName(Faction.OfPlayer.def.factionNameMaker, IsValidName)` produced in… |

### bills/production (8)

| id | pri | type | state | wave | title | summary |
|---|---|---|---|---|---|---|
| `8fbb09a` | p1 | spec | backlog | 5 | 5.2 Factions/Guests first suites (M3) | First real mod suites: encode the Factions/Guests behaviors currently sitting in "awaiting in-game verification" as rwtest scenarios — starting with the recurring-regulars feature. |
| `1113019` | p1 | bug | next | 2 | advance {until:{condition}} with an already-true predicate and no timeout runs UNBOUNDED — 187,541 ticks, stopped only by the casualty halt | Found on a live bench in session 21, and it is the sharpest unattended-run hazard I have seen in this project. The command and its own result envelope are the whole report. ## What was sent… |
| `e440676` | p2 | enhancement | doing | 4 | A correct refusal and a real fault are both ok:false, so 53% of a run's 'errors' are the protocol working | 364 of 691 — 53% — are the protocol working correctly. `busy` means "retry in a moment". `unread-journal` is `722c951`'s guard, the one that stops an agent advancing past events it has not read; a run that trips it a lot is a run being… |
| `f794bfc` | p2 | bug | next | 2 | accept/fc287ba 0.7c cannot tell a refusal that ran the clock from a bench that arrived running — 390 ticks blamed on TimeDriver | Found on a bench on 2026-09-01 (session 21), while verifying that `1113019` had not regressed this suite. It had not. 0.7c fired anyway, and the interesting part is that it cannot say which of two things it caught. ## What happened 0.7a… |
| `33bc796` | p2 | enhancement | backlog | 3 | prioritize blocked on a DoBill work giver says WHAT is missing, never WHY | Found while closing `eef837a`. Preserved rather than fixed there because it changes `prioritize`'s contract, not `bills`'. In run `m1-20260901` the diagnostic that finally cornered the butcher defect was: That string is the game's own… |
| `fee81b2` | p2 | bug | backlog | 2 | threat-pardon reports success with refused_count 0 when every pardon lapsed on arrival | Found on the live bench, session 13 (`20260901T121508`), testing the pardon path that the smoke pass could not reach because that map had no hostiles. The mechanism is correct. The reporting is not. ## What happened Three megascarabs… |
| `58794e4` | p2 | bug | doing | 2 | work-cover {dry_run:true} reports coverage_after as the coverage BEFORE the repair, so a dry run's verdict is always 'still broken' | Found on first bench contact with `work-cover`, session 21, bench `_RimWorld-Agent` `20260901T205859`, assembly `1.0.0+52606d1`. Raw envelopes at `accept/runs/s21-20260901/12-workcover-dryrun.json` and `13-workcover-real.json`. ## What… |
| `6fc75e3` | p2 | bug | doing | 2 | work-cover's clean dry run says 'the journal writer is closed' when it is open and nothing was mutated | A clean `work-cover {dry_run:true}` writes no journal line — correctly — and then reports it in the words of a FAILURE. `data.action` is `{"journal_seq": 0, "provenance": "NOT WRITTEN — the journal writer is closed, so this mutation has… |

### time/advance (7)

| id | pri | type | state | wave | title | summary |
|---|---|---|---|---|---|---|
| `5cb1f9f` | p0 | bug | next | 3 | dialog-dismiss cannot answer a text-entry dialog, so the faction-naming prompt re-raises every 1000 ticks and wedges the run | At tick ~255,000 the game raised `RimWorld.Dialog_NamePlayerFactionAndSettlement` — the prompt to name the player faction and its settlement. It is `forcePause` and `absorbsInput`, so every `advance` halts on it. `dialog-dismiss`… |
| `7c83d6c` | p1 | spec | backlog | 5 | 5.1 rwtest runner + assertion helpers | `rwtest`: scenario scripts in, pass/fail + evidence out. The mod-testing product the whole platform exists for. |
| `cd92db7` | p1 | bug | backlog | — | rwa journal's default FILE mode never moves the read watermark, so PLAY-LOOP's step-1 read does not clear the advance gate | `PLAY-LOOP.md` §read step 1 and §advance both say the fix for an `unread-journal` refusal is: rwa journal --since <last_seq> and that 'nothing else clears it'. On this client that call does not clear it. `rwa journal` defaults to a… |
| `9dc0caa` | p2 | enhancement | backlog | 4 | A truncated journal read reports ok:true and wedged run m1-20260901 for 60 turns: the reply never says it fell short | The loop was not ignoring the guard. It called `journal` between every single pair. It called it like this, every turn: From seq 0 the `limit` is exhausted at seq 2024, nine rows short of the tail. A truncated read correctly moves the… |
| `9227839` | p2 | enhancement | next | 3 | Colony start has an unwritten step: the colony must be NAMED, and nothing says it is pending | Colony start has an unwritten step: the colony must be NAMED. It does not fall due at colony start — it falls due days later, well after the colony-start checklist section is logged complete. In run `m1-20260901` it arrived at tick… |
| `5fd6dde` | p2 | spec | next | 2 | The mod says what is wrong but not what fixes it, and the  op answers nothing: move discoverability into the mod | Session 21 moved DISCIPLINE into the mod — `advance` refuses a blind advance, halts on a casualty, halts on news. This is the successor: move DISCOVERABILITY in. Raised by Evan while reviewing the M1 launch prompt: *"is our harness that… |
| `a30c807` | p3 | defect | backlog | — | accept/fc287ba-until-state.py --dry-run crashes in phase 3: the plan for three of its five phases has never been printable | `accept/fc287ba-until-state.py --dry-run` crashes in phase 3 with a `TypeError`, so the plan for phases 3-5 has never been printable and the `--dry-run` mode cannot be used to review that suite. Found on 2026-09-01 while working… |

### build/construction (4)

| id | pri | type | state | wave | title | summary |
|---|---|---|---|---|---|---|
| `eef837a` | p0 | bug | doing | 3 | bill-add creates a butcher bill with filter:null that matches nothing, and bill-set does not persist a fix | A `ButcherSpot` was built and a bill queued the normal way: The bill then read perfectly healthy for the rest of the run and never produced a single meat: With a fresh wild boar corpse (hp 95%, unforbidden) lying on the butcher spot's… |
| `6d4ca8a` | p1 | spec | backlog | — | No verb installs a MinifiedThing, so anything the art bench makes can never be placed | Found in play, run openrun-20260902 day 5. `Make_SculptureSmall` on a `TableSculpting` produces a MinifiedThing (`wooden small sculpture (normal)`, id 12500, sitting at [108,105]). Placing it needs an install blueprint. Nothing in the… |
| `f08dfc4` | p2 | enhancement | doing | 4 | The same refusal 238 times in one run and nothing says so: a repeated identical outcome is one event | 238 of them are the same call, refused the same way, repeated. The agent passed `stuff` to a `DeepDrill`, was told no, and made the identical call 238 more times across the run. Nothing stopped it, nothing escalated, and nothing in any… |
| `c621849` | p2 | enhancement | backlog | 3 | install verb: a MinifiedThing cannot be placed, so G5 is unreachable | ## What happened Run openrun-20260902, Spring 1 of 5501. `things {def:"MinifiedThing"}` reports count 1 — a wooden small sculpture (id 12500, normal quality) sitting at (108,105) inside the base. The open-ended run contract grades G5 as… |

### trade/quests (2)

| id | pri | type | state | wave | title | summary |
|---|---|---|---|---|---|---|
| `be75bc4` | p1 | bug | next | 3 | trade-set accepts two lines addressing the SAME Tradeable and echoes a deal state that never existed | Found by the first live run of `accept/3.5-dialog-verbs.py --phase 3`, session 11 (`65b03c2`), 2026-08-31. Confirmed against the decompiled 1.6 source. `RimWorld/Tradeable.CountToTransfer` is a single signed field, and… |
| `7e8c969` | p2 | bug | next | 3 | A silently clamped trade line reports full success, and buy_value/sell_value include the silver row so their difference is always zero | Two sharp edges on the same surface, found by the first live run of `accept/3.5-dialog-verbs.py --phase 3`, session 11 (`65b03c2`), 2026-08-31. Both confirmed against the decompiled 1.6 source. Neither is the duplicate-line defect… |

### muster (1)

| id | pri | type | state | wave | title | summary |
|---|---|---|---|---|---|---|
| `01f0b85` | p1 | muster | next | — | Muster: AutoRimmer build plan | The AutoRimmer build plan. Read DESIGN.md before anything else; run the build via ORCHESTRATION-PROMPT.md (Opus orchestrator, workers per `agent:` label). Claude plays an unattended RimWorld bench (`_RimWorld-Agent`) through the… |

### storage/zones (1)

| id | pri | type | state | wave | title | summary |
|---|---|---|---|---|---|---|
| `9c68756` | p2 | enhancement | backlog | 3 | Nothing re-checks a designation once placed: an area change or a herd that walks away makes it inert in silence | `b7359fa` made `designate` answer honestly AT THE MOMENT OF THE CALL. The other half of what killed `Marco` is that the answer goes stale and nothing notices. From the playbook lesson, which is the incident itself: > The herd had… |

### protocol/rwa (1)

| id | pri | type | state | wave | title | summary |
|---|---|---|---|---|---|---|
| `5697725` | p2 | bug | backlog | — | landmark: rwa README's documented --set.<name> form is refused by the verb | `rwa/README.md` §Argument syntax lists, as a worked example of dotted keys: rwa landmark --set.kitchen 120,130 That expands to `{"set":{"kitchen":"120,130"}}`, and the mod refuses it: bad-args / refused: missing required arg 'name'… |

### other (1)

| id | pri | type | state | wave | title | summary |
|---|---|---|---|---|---|---|
| `7f0e245` | p1 | spec | backlog | — | AUDIT: where else should the mod hold the loop? every poll the agent runs is a call that decides nothing | see attached |

## Open in `seekandkill` — 8

### pawn/work (6)

| id | pri | type | state | wave | title | summary |
|---|---|---|---|---|---|---|
| `000987b` | p1 | bug | done | — | Toggle/stance/autocast pruned at PostLoadInit, before map pawns exist | PruneStaleIds() runs from SeekAndKillGameComponent.ExposeData under LoadSaveMode.PostLoadInit (SeekAndKillGameComponent.cs:52). In Game.LoadGame(), Scribe.loader.FinalizeLoading() -> DoAllPostLoadInits() runs BEFORE… |
| `7ac5f77` | p2 | enhancement | backlog | — | Bounding reposition: part of the squad keeps firing while the rest moves | When the fight shifts, a replan can put the whole squad on the move at the same moment — a few seconds where nobody is shooting and everyone is a target. Wanted: repositioning happens in ripples, fireteam-style — some pawns hold and… |
| `95bd2df` | p2 | enhancement | backlog | — | Fighting withdrawal: ranged pawns give ground against closing melee (kiting) | The squad knows advance, hold, and tighten — it has no concept of giving ground. When a melee raider sprints at a sniper, the sniper's only plan is to shoot it before it arrives. Wanted: a threatened ranged pawn backs away while… |
| `e873033` | p2 | enhancement | backlog | — | Predictive seating: pick firing spots against where enemies are heading | Pawns currently choose firing positions that are good against enemies' positions at this instant. Raiders keep walking; seconds later the chosen cover faces the wrong way and the pawn relocates mid-fight while exposed. Wanted: positions… |
| `23a453f` | p3 | enhancement | backlog | — | Unified combat utility: one judgment replaces separate positioning/targeting/charging heuristics | The combat AI is a bundle of separate instincts — where to stand, whom to shoot, when to charge — each with independently tuned numbers and hysteresis bands. Individually sensible, but two instincts can combine into nonsense (the… |
| `dca6838` | p3 | bug | backlog | — | seek: detached pawns are seated by PlanSlots but skipped by AssignTargets | Found while implementing 299ab6b. Squad.detached is inconsistently honoured across the planners, and its own doc comment overstates what it does. Squad.cs:129 documents detached as 'Members fighting an immediate local threat; their… |

### combat/threat (2)

| id | pri | type | state | wave | title | summary |
|---|---|---|---|---|---|---|
| `6545a67` | p2 | enhancement | backlog | — | Density gate: runt clusters skip squad dispatch, personal initiative handles them | From Dorian's note (rember.md): "we only need the radius thing when we have more than x enemies in x amount of space" — the squad/formation machinery should only engage above an enemy-density threshold. Scattered stragglers (drop-pod… |
| `a6b1aa0` | — | — | — | — | Dispatcher.InContact NREs on squad.members when a dormant mech cluster is on the map | Every advance halts on red_error within ~60 ticks of enabling seek-at-will while a dormant mechanoid cluster (quest OpportunitySite / Padenik's Mechs) sits on the map. Stack, verbatim from the AutoRimmer journal (seq 3008, tick… |

## Closed issues — titles only

Listed so a theme can be told apart from something already fixed. If a theme looks
like it matches one of these, **check the issue before calling it COVERED** — closed
means the work landed, and a recurrence is a regression, which is a different plan.

### `autorimmer` — 59 closed

- `53846a8` [—] **Filed mid-run (m1-20260901, tick ~47,365), Evan's call.** Evan, watching the window: *"you didn't check to see if you could build the heater or not… this is a deterministic thing, heater takes 5, pawns are at 4, the game already knows you can't build it, just have to pass that on."* He raised Lacey's Construction to 8 by hand so the run could continue — recorded as a human intervention in the run report.
- `c8c0199` [enhancement] --quicktest and autostart.rws race: restoring the save deterministically breaks the bench fixture
- `3fa4cf5` [spec] 0.1 Spike: profile, headless boot, throughput
- `097f33a` [spec] 1.1 AutoRimmer skeleton: file protocol, verb registry, main-thread loop
- `45d01a3` [spec] 1.2 Journal: events.ndjson event stream
- `c9b5769` [spec] 1.3 Time control: advance-until, tps throttle
- `e3d04c7` [spec] 1.4 rwa CLI (rimworld-tools)
- `4b65a28` [spec] 1.5 Substrate remediation: lifecycle resets, result loss, halt-on-error
- `fc287ba` [spec] 1.6 advance until:condition — state-predicate halting
- `8555381` [spec] 1.7 advance must not tick under a force-pausing modal
- `b8785e8` [spec] 1.8 advance drives the game clock: unpause to play, pause to stop
- `b2b89f3` [spec] 2.1 Colony digest + what-changed
- `69ae91f` [spec] 2.2 Pawn serializers
- `1a35e07` [spec] 2.3 Spatial: viewport, queries, landmarks
- `21856e3` [spec] 2.4 World serializers: things, rooms, zones, bills, research, interactions
- `3dce29a` [spec] 2.6 Observer remediation: find-rect selection, alert severity, digest budget
- `f166fb9` [spec] 3.1 Dev-layer verbs (dev:*)
- `57ab92a` [spec] 3.2 Designation + zone verbs
- `39c9db7` [spec] 3.4 Pawn orders + policies
- `e8f2c32` [spec] 3.4: manual work priorities cannot be turned on by any verb, so work-priorities is unreachable on a fresh colony
- `20e5cda` [spec] 3.5 Dialog + interaction verbs
- `48f666c` [spec] 3.6 Production bills + storage settings
- `96d9315` [spec] 4.1 Playbook: lessons, morning checklist, post-mortems
- `ac407f1` [bug] A gate-refused order writes no action row, so half of 4087644's journal rule is unimplemented
- `df378fa` [enhancement] A read-only cockpit: watch a run live or replay a finished one, from the transcript alone
- `7382bdd` [bug] An unknown argument name is silently ignored and falls back to a default, so a wrong arg reports success
- `b1b3060` [spec] Combat posture is a standing state, not three verbs remembered in the right order: a posture verb, and posture in the digest
- `3a5ff6c` [spec] Dev staging bypasses every PlaceWorker and vanishes walls: a dev-staged base can be a state no colonist could build — buildable:true (opt-in) + site-audit
- `4087644` [spec] Job-ordering verbs report success for orders that did nothing — a false positive on every 'did my action work?' check
- `548ef48` [spec] No observer surfaces the quest log, so the agent can only answer quests it was told about by a letter
- `bb931b9` [enhancement] No save verb: an unattended run cannot checkpoint before a raid
- `d7c8088` [spec] Nothing reads blueprints or frames: a placed build is invisible, and completion is an absence
- `280fb78` [spec] Nothing wakes a sleeping agent but a casualty: every letter and alert halt is opt-in, so a day-long advance sleeps through raids, traders and inspirations
- `ce15092` [—] Run: session 10 — audit what BORGES built, then merge it
- `fbb2c59` [—] Run: session 9 — unblock 4.1, end 2.5's machine-exclusivity
- `65b03c2` [muster] Session 11 — PROVE IT. The round that ran the suites for the first time.
- `e56447b` [muster] Session 21 — the M1 gate, all four items, fanned out across four worktrees
- `2f2796e` [task] Verify the issue backlog before building to it — filed diagnoses mix confirmed source with inference, and two were wrong
- `8799218` [spec] What else should wake the agent? A day of quiet is only safe to sleep through if the halt set is good enough
- `05dd70e` [docs] WorldSafe's zone-hazard note still says map-view trips it; Spatial.cs fixed that and the note now misdirects
- `722c951` [spec] advance must refuse while the journal has an unread delta — the discipline that failed cost a colonist
- `1a072fa` [spec] assign needs an auto_arm lever — nothing can turn FSWA auto-arming on
- `36999fd` [defect] construction cannot be asked about a layout, and silently ignored --layout_id while reporting success
- `b7359fa` [bug] designate accepts targets outside every allowed area and reports accepted:N with no way to tell
- `8b0b88f` [bug] designate verbs report already-designated as not-designatable, so a redundant order is indistinguishable from an impossible one
- `8b4839f` [bug] dev:spawn-thing's blocker describes the TARGET cell, not the cell that refused: granite on the interaction spot reported as a wood log with removal none
- `091e3f0` [enhancement] dev:starter-kit must land its gear forbidden, the way a real drop-pod arrival does
- `16b959a` [bug] git-bug pull produced FIVE unreadable merge commits; the whole store panicked until they were reset
- `2a7c064` [bug] map-view labels an odd-width building one cell off centre: _label_pass adds size/2 to a position that is already the centre
- `e6faa51` [enhancement] map-view publishes a legend but no alphabet identity, so the ASCII and PNG channels cannot be keyed against each other
- `bc2250b` [spec] move-to accepts `queue:true`, ignores it, replaces the running job, and reports success
- `32b9e01` [bug] orders claims read-only but mutates: missing FloatMenuMakerMap.makingFor causes a colony-wide bill cooldown, an RNG burn and a fidelity divergence
- `1eb2262` [spec] pawns returns colonists in an unstable order, so any acceptance keyed on roster index is flaky
- `54b0c9a` [defect] place-layout reports a shortfall that does not exist: short_by is a conclusion drawn from a stockpile-only count
- `5eba561` [bug] rwa transcript cap of 1000 steps bricks a long run with a bare traceback instead of an envelope
- `3a0e042` [spec] seek-at-will verb: autonomous combat via SeekAndKill, so a raid is not per-pawn micromanagement
- `c718e4a` [spec] site-survey: nothing surveys the ground AROUND a footprint — find-rect approves boxes the game then refuses, and hands back a corner where the verbs want a centre
- `70ac258` [bug] things emits an addressable list ordered by a live score — same bug as pawns, same fix
- `0d9cbd7` [bug] world-fixture steps do not chain: the bill step can attach to a different bench than the bench step just made

### `seekandkill` — 38 closed

- `e4f4e82` [enhancement] Ability-aware seating: fold vanilla toggled offensive abilities into psycastAwareSeating
- `fef1cd9` [enhancement] Advance to contact: seek pawns get eyes on the enemy instead of doing chores while any live threat exists
- `836fd47` [bug] Area-excluded pawns stay in squads they can never fight with
- `08d95be` [bug] CE self-defense finder ignores line of sight
- `0ac97c9` [bug] CE self-defense finder never detects turrets
- `5132e98` [bug] CE suppression ping-pong: recovered pawns sent back to the same exposed slot
- `67414ee` [bug] Confirmed cluster split gives both fragments the same id, crashing Dispatcher.Assign
- `86b13b4` [enhancement] Fire-distance stance Close/Medium/Far via seek gizmo right-click
- `78b5072` [enhancement] Formation axis and projected centroid oscillate, causing replan storms
- `5e31890` [enhancement] Formation effectiveness: range-aware slots, standoff shrink, intercept
- `a4cb516` [bug] Formation planner seats pawns behind full walls: replace band planner with per-pawn CastPositionFinder seating
- `2e70473` [enhancement] Formation spacing: seated pawns keep a gap instead of bunching on shared cover
- `2f1aa33` [c#-system] In-game verification + tuning pass
- `3e6e63b` [enhancement] MP: sync player actions in SeekAndKill (5 sites, 5 to sync)
- `b3b4c28` [enhancement] No reaction to incoming fire beyond the 15-cell self-defense radius
- `8af91c7` [bug] Personal-initiative fallback: seek pawns fall through to chores mid-battle
- `8b66b41` [enhancement] Psycast-aware seating (opt-in setting, default off)
- `0a230b2` [enhancement] Seek node starves CE proactive reload while engaged but idle
- `0665aa9` [bug] Seek pawns ignore allowed-area restriction when attacking
- `1014dd6` [bug] Seek pawns never bash doors to reach an engagement
- `c6e5772` [enhancement] Seek pawns should drop non-medical jobs to fight when combat starts
- `1b1b664` [enhancement] Self/ally buff psycast autocast on engagement
- `60e3b7b` [enhancement] Slot churn: no assignment stickiness, every replan reshuffles the whole formation
- `41d2d5d` [bug] Slotless squad members are sent to map corner (0,0,0)
- `fdd6f39` [enhancement] Squad consolidation: merge co-assigned squads, implement minSquadSize, cross-squad slot dedup
- `73e84d2` [enhancement] Squad splitting for scatter raids
- `2253a82` [bug] Squads never replan for slotless or area-barred members
- `4290416` [enhancement] Toggle-ability lifecycle: engage-on / disengage-off for stateful implant toggles (Greyscythe)
- `ac527b6` [enhancement] VPE/VEF psycast autocast in seek mode (opportunistic, interop foundation)
- `eadffd9` [enhancement] Vanilla-ability autocast: per-pawn right-click toggle + cast layer in seek mode
- `1b97366` [bug] seek: cap cluster spatial extent
- `84cc344` [enhancement] seek: debug overlay per-concern toggles, drop slot field-edges
- `2562f8b` [enhancement] seek: demand-driven force allocation
- `c77fb04` [enhancement] seek: fire-and-movement — SafeToMove signal + bounding advance
- `083c59d` [enhancement] seek: home-weighted dispatch
- `cd0f95a` [enhancement] seek: march cohesion — squad waypoint chain
- `299ab6b` [bug] seek: never idle while a reachable fight exists — cold-squad re-dispatch + march pacing
- `e134058` [bug] seek: raid reaction latency + mobilization discipline
