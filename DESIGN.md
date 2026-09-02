# AutoRimmer — Design

Claude plays a real, unattended RimWorld instance and uses the colony as a test
platform for our mods. Not a fork, not a reimplementation: the shipped game runs
normally (window parked out of sight, watchable on demand), and an in-game mod —
AutoRimmer — is the agent's eyes and hands: observe state, execute player verbs,
control time, over a file protocol. The agent drives it turn-based from outside
through the `rwa` CLI. The driving loop:

    act → advance(until …) → read journal delta + digest → think → act

That loop is also the test substrate: `rwtest` scenarios are the same loop with
assertions instead of judgment.

Status: building. The build plan is the git-bug spec issues (start at the
`type:muster` issue); this document is the shared context they all reference,
and it outranks any spec body that disagrees with it.

## Non-goals (v1)

- No game fork / headless reimplementation of the sim.
- Single-map colony play only: no world map, caravans, settlements, gravship travel.
- No Combat Extended yet (deferred tier; FindSuitableWeaponAndAmmo confirmed to
  run vanilla-only via its `ModsConfig.IsActive` guard and split `.CE` assembly).
- No multiplayer anything.
- The agent never launches the user's installs (`_RimWorld-Testing`, MP). Only
  `_RimWorld-Agent`. This is the standing carve-out to the workspace "never
  launch RimWorld" rule; the rule stays in force for every other install.
- **DLC-specific colony management is not driven in v1** (decided 2026-08-30
  after an action-surface audit found it was an unstated omission across six
  active DLCs, 62 of the 132 alerts). All DLCs stay ACTIVE on the bench — their
  presence is part of the integration test and always was — but the agent does
  not manage Royalty titles/psycasts, Ideology rituals, Biotech genes/children/
  mechs, or Anomaly entities. Observation is not restricted: serializers report
  DLC state like any other, and the agent may be interrupted by it. **One
  carve-out, because it is a live mood drain and one call to fix:** assigning a
  pawn to an empty Ideology role (`Precept_Role.Assign`), since
  `createsRoleEmptyThought` produces a standing `Thought_IdeoRoleEmpty`.
- **Animal management is deferred, not excluded.** Training, master assignment,
  auto-slaughter and pen filters are not v1 verbs. Animal state is still
  OBSERVED — seven vanilla alerts hang off it — so the agent can see a problem
  it cannot yet act on, and says so rather than silently ignoring it.
- Cosmetics (the `Designator_Paint` family, styling stations, glower colour) and
  the planning grid (`Designator_Plan_*`, redundant with the baseviz layout IR)
  are out of scope. Named here so nobody specs them later.

## Architecture

    [_RimWorld-Agent: native Linux build, Hyprland special workspace, fps-capped]
        AutoRimmer mod (C#): verb registry · serializers · tick driver · journal
            ⇅ file protocol under save-data AutoRimmer/
    [rwa CLI, python, in THIS repo: rwa/] ⇄ [Claude session | rwtest runner]

Prior art in-tree — copy, don't reinvent:
- `analyzerbridge/` — the protocol + threading pattern (its CLAUDE.md "How to
  drive it" section is the protocol's user manual; `CommandPoller.cs` is the
  file half, `BridgeGameComponent.cs` the main-thread half).
- `logrelay/` — log capture + background flusher pattern.
- `rimworld-tools/baseviz/` — layout IR (JSON grid ↔ KCSG XML), def catalog
  dumped from the live DefDatabase, ASCII canvas. (It has NO raster renderer —
  its colored grid is browser JS in `static/viewer.js`. Vendored into this
  repo as `baseviz/` by 2.5, which added its own PNG encoder; the sibling copy
  is now reference-only.)
- `_RimWorld-Test/make-profile.sh` — symlink-farm bench profile (128K install).
- `rimworld-tools/Info/decompiled/RimWorldBase/` — decompiled vanilla source.
- `_mp/DETERMINISM.md` — the lazy-getter/UI-mutation hazard classes.
- `_mp/actions.json` — MUTATES audit of our own mods' gizmo surface (the
  eventual coverage checklist for mod-under-test interactions).

## Protocol

AnalyzerBridge's, generalized:
- `commands/<id>.json` inbox; consumed (moved to `commands/done/`) BEFORE
  execution; `results/<id>.json` one per command, always written, never dropped;
  `status.json` ~1Hz heartbeat; `journal/` event stream.
- Envelope: `{"id","op","args":{…}}` → `{"id","op","ok":true,"data":{…}}` or
  `{"id","op","ok":false,"error":{"code","detail"}}`.
- Background thread touches files only, never Verse. Main thread
  (`GameComponentUpdate`, runs while paused) drains the pending queue.
- Error taxonomy: `bad-json | unknown-op | bad-args | no-active-game | busy |
  exception | stale-on-restart` + per-verb codes. Stale inbox files on relaunch
  are answered with `stale-on-restart`, never replayed.

## Time model

Paused by default. The agent acts, then explicitly advances:

    advance { ticks:N | until:{letter|threat|alert|event|condition|layout},
              max_tps:T, timeout_ticks:M }

The first four are **journal taps over discrete events** — they hook
`Journal.OnEvent` and halt on a match, so they can only fire on something that
HAPPENS. `threat` is `letter` narrowed to `ThreatBig`/`ThreatSmall`.

The last two (spec 1.6, 2026-09-01) halt on **state**, polled from
`TimeDriver.Step` on a frame cadence — the same poll site that already watches
`WindowsForcePause` and `CurTimeSpeed`. `condition` is a predicate over a digest
path (`{path:"time.hour", op:">=", value:6}` is "advance until dawn");
`layout` is "every element of this `place-layout` transaction is resolved".
They exist because nothing is emitted when a continuous value crosses a
threshold and **nothing at all is emitted when a building finishes**, so
"advance until the thing I asked for is done" — the most ordinary request an
agent makes — had no spelling until then.

Two rulings that are easy to get wrong and are recorded in the decisions log:
a `condition` requires an **edge** by default (`hour >= 6` is true all
afternoon), and the layout form is a **named family rather than a path**,
because `construction.frames == 0` is the wrong predicate and no amount of edge
detection makes it the right one.

`timeout_ticks` **defaults to 60,000 — one in-game day — on any `until`
advance**, and the result publishes `timeout_ticks` beside a `timeout_source` of
`caller | default | none` so the caller's own bound is distinguishable from
ours. An `until:{condition}` that is ALREADY TRUE when it arms, with the edge
required and no positive bound, is **refused** (`unreachable-halt`): the edge
can never come, so the halt cannot fire. Both are session 21's, and the
decisions log carries why the day is the idle unit rather than the panic
button.

**A clock predicate is the one that makes tick arithmetic unnecessary.**
`advance {ticks:N}` overshoots by up to `MaxTicksPerFrame(speed)` — 30 at
Ultrafast — and the overshoots accumulate with nothing to re-anchor them. Every
evaluation of `time.hour` re-reads the real clock, so it cannot drift.

Vanilla ceiling (decompiled `TickManager.cs`): `TickRateMultiplier * 2` ticks
per frame inside a ~45ms budget; Ultrafast = 15×, dev UltraSpeedBoost = 150×.
Implementation is a budgeted `DoSingleTick` loop that must preserve whatever
per-frame updates the sim needs (spike 0.1 enumerates them; Zetrith's
Multiplayer catch-up code is the reference). `advance` ALWAYS returns with the
game paused, reporting `{reason, tick, events}`. `max_tps` is a hard thermal
cap — this laptop has a history of thermal trips under sustained load; the cap
is enforced regardless of what the caller asks for.

## Observation model

"All the data the player sees" is mostly precomputed by the game — serialize,
don't reinvent: Alerts (132 classes = the attention model), Room roles/stats,
health summaries, needs, the Work/Schedule/Assign tabs, the resource readout.

Four layers of play, each with its own channel:
1. **Interrupts** — letters/alerts/deaths → journal + advance-until.
2. **Cadenced upkeep** — the morning checklist: food-days, per-pawn apparel HP%
   vs stockpile vs season, mood trends, meds, power margin, popper coverage.
   The checklist is versioned playbook data, not code. (Key fact: alerts fire
   LATE — tattered-apparel alert means the mood penalty already landed.)
3. **Projects** — house/farm/research → verb sequences + layouts.
4. **Emergencies** — delegated to the colony brain (SeekAndKill autocombat,
   FSWA auto-arm, medical/firefighting via work priorities). The agent
   intervenes by exception, not by micro.

Context budgets are part of every observer spec; everything outside the digest is
drill-down on demand, and jq-side filtering is expected and encouraged.

**Digest budget, measured rather than guessed (2026-08-30).** This line said
"~1–2KB" from spec stage, before anything had been built. Measured on real
colonies: **0.7–2.0KB typical, 2.6KB worst case** with every cap saturated
(12 alerts, 10 colonists, 20 of each present). The budget is the WORST case,
because that is the one that bites — call it **≤3KB, ~1KB typical**. What makes
it a budget rather than a hope is that every list-valued section is capped and
reports what it dropped; an uncapped section is the defect, not a large number.
Do not buy headroom by cutting the alert cap: alerts are the attention model,
and 2.6 exists partly because truncating them badly hid a Critical.

## Action model

Two layers:
- **`dev:*` god-hand** — spawn/heal/set-need/incident/finish-research.
  DebugAction-backed, cheap, journaled as cheats. Fixture setup and demos.
- **Player verbs** — parity with the UI at the semantic level: designations,
  build, zones, bills, priorities, schedules, policies, draft/orders, letter
  choices, trade. Modal dialogs are transacted against the model (`TradeDeal`),
  never by driving UI widgets. Zetrith's Multiplayer is the existence proof
  that every player verb can be reified as a replayable command.

Every mutation echoes evidence (before/after crop for spatial verbs, result
data otherwise) and lands in the journal.

**The plural form IS the verb; the singular is its degenerate case.** Each verb
call is one round trip through the agent's attention, so a verb that does one
thing at a time turns a 40-cell job into 40 turns of thinking. Every verb whose
job can plausibly be asked of N things takes the N: `designate --cells rect`,
`place-layout` over a whole house, work priorities as a matrix, `zone` over a
rect. A verb that can only be called in a loop is the defect. The client-side
escape hatch — a shell loop over `rwa` — exists and is fine for the ragged
tail, but it costs a 0.25–1s round trip each and hands back N envelopes to read.

**The gate lives in the widget, not in the model — so every player verb must
re-implement its precondition and cite it.** This is the standing invariant that
"transact against the model, never drive widgets" does not by itself give you.
RimWorld puts its preconditions in the UI layer and leaves the model wide open;
verified in the decompiled 1.6 source, 2026-08-30:

- `BillStack.AddBill` (`RimWorld/BillStack.cs:69`) is four lines and checks
  nothing — not `RecipeDef.AvailableNow`, not the 15-bill cap.
- `GenConstruct.CanPlaceBlueprintAt` performs no research check; the gate is
  `Designator_Build.Visible` (`RimWorld/Designator_Build.cs:125`,
  `!entDef.IsResearchFinished`).
- `ResearchManager.SetCurrentProject` (`RimWorld/ResearchManager.cs:110`) tests
  only `baseCost > 0f` — no prerequisite check at all.

A verb that calls the model directly therefore hands the agent a god-hand it was
never meant to have, silently and while looking correct: unresearched buildings
blueprinted, impossible bills queued, a research project started out of order.
`dev:*` may bypass these deliberately; a player verb may not. **Each such verb
names the widget-layer check it reproduces, in a comment, with its file:line** —
the same discipline `Blockers.cs` already follows for the removal taxonomy.

## Tile-system risk (top model-risk)

Principle: **the model does topology, the game does geometry.**
- Query-first spatial API: `find-rect` / `nearest` / `reachable?` / `room-at`,
  backed by `CellFinder`/`GenClosest`/`Reachability`/`RegionGrid`. Candidates +
  reasons, never bare booleans. The agent should rarely do coordinate math.
- Layouts, not cells: houses are baseviz IR placed by one verb (blueprint mode
  for play, instant mode for fixtures).
- Named landmark registry (`base-center`, `kitchen-door`) so plans reference
  places, not numbers.
- ASCII viewports with coordinate rulers; a PNG render channel (vendored
  `baseviz/` catalog + a deterministic stdlib PNG encoder written for 2.5 —
  baseviz itself never had one) the agent Reads as an image — an independent
  second visual check.
- Fallback: parametric room templates if freeform IR authoring underperforms.

## Journal

Append-only `events.ndjson` per session: letter, message, alert_on/alert_off,
death, downed, mental_break, red_error, warning, dev-verb provenance — each
stamped with game tick + wall time. It is simultaneously: the agent's "what
happened while time ran", the watchpoint source for advance-until, the
post-mortem input, and the primary rwtest assertion substrate. Standing
invariant everywhere: **zero red errors**.

## Bench: `_RimWorld-Agent`

Symlink-farm profile (pattern: `_RimWorld-Test/make-profile.sh` — engine+Data
from the Steam install, `Mods/` symlinked, `gen-modsconfig.py` activates
presence), `XDG_CONFIG_HOME` isolation, Steam API detached. Watchable by
design: the game window lives on a Hyprland special workspace (silent
windowrule), GPU-rendered, low fps cap when unwatched; `rwa watch` reveals it
and raises the cap. Xvfb + x11vnc is the documented fallback for fully
detached runs.

v1 mod set (ONE set — the suite coexisting is itself the integration test):
- **Infra**: Harmony (+ transitive infra only if required), LogRelay,
  AnalyzerBridge + Dubs Performance Analyzer, AutoRimmer (which carries the
  catalog dump — BaseVizCatalogDumper was folded in as source by 2.5, so it is
  no longer a separate mod on the bench).
- **Own vanilla+DLC mods**: Factions, SeekAndKill, FindSuitableWeaponAndAmmo
  (vanilla mode), RandomResearch, MechPatrol, JoyVariety, FuzzyRoomRequirements,
  RetryFailedSurgery, Church, CruelAndUnusualPunishment, Fingerkill, AutoQuest,
  CoherentBionics, Nepobaby, DisableLeaveBadWeather, NoMoreAlarms,
  RealisticGasses, RealisticHeatDeath, SuperHotFire, WirelessChargingMech, Music.
- **Visitor cluster (third-party)**: Hospitality, Gastronomy, Storefront,
  CashRegister (transitive dep of the latter two) — plus the own mods they
  exist to serve, **Guests and RegisterLanes**. On the bench from day one
  because Factions/Guests is the first analysis target. Load order is pinned
  (CashRegister → Hospitality → Gastronomy → Storefront → Guests, all ahead of
  the alphabetical middle: Factions declares `loadAfter` Guests).
- **DLCs**: all owned (Royalty, Ideology, Biotech, Anomaly, Odyssey).
- **Not needed, verified in spike 0.1**: HugsLib (nothing on the bench declares
  it) and PerspectiveShift (SeekAndKill runs standalone — `PSInterop.Init`
  returns early when PS is absent and its own gizmo path takes over).
  Transitive deps are resolved from About.xml at profile-build time, so this
  list stays honest as the bench grows.

Deferred tiers (each joins with its own mods AND unlocks its bugfix mods):
CE/DMS/PerspectiveShift cluster; singletons (NiceHealthTab → NHTInjuriesOnly,
IsekaiLeveling → AnimeRage, HyperfoldEngine → Hyprfold, Euterpe →
SocioButterflyJoyIntegration, VEF → BetterBases). A bugfix mod always rides
with its target's tier.

The bench modlist is versioned; every saved fixture is tagged with the bench
version it was built on; tier changes ⇒ regenerate fixtures.

## Learning: the playbook

In-repo, versioned: `playbook/` (one lesson per file: trigger, why, how to
apply), `checklists/`, `templates/`. Lessons are earned via journal
post-mortems and escalate in strength: prose lesson → checklist item → baked
into templates/policies (the power-room template grows a firefoam popper). Git
inheritance means every agent instance — including orchestration workers —
starts with everything ever learned, and Dorian can review lessons in diffs.

## rwtest

Scenario = fixture (save or gen-script) + acts + advance specs + property
assertions over journal + state, within tick windows. Single-player is not
load-deterministic; NEVER assert byte-exact states. `no_red_errors` is on by
default in every test. Reports are quarantined from the log-watcher triage
queue by default (an agent flailing mid-experiment must not page triage).

## Conventions & invariants

- Observers never mutate. Beware lazy-init getters (`_mp/DETERMINISM.md`).
- All Verse access on the main thread at safe points; file I/O off-thread.
- `dotnet build -c Release` always; `Build:` commits stand alone; verify the
  pdb path names this worktree and Release before committing a DLL.
- Never launch `_RimWorld-Testing` or the MP install. Only `_RimWorld-Agent`.
- Thermal cap on by default. The fan must be audible truth, not hope.

## Decisions log

- 2026-08-30 — Named `autorimmer`. One mod set, tiered growth. CE deferred
  (FSWA verified vanilla-capable). File transport over socket; `rwa` CLI over
  MCP (context pre-filtering via jq is the decisive ergonomic). Survive demo
  (M1) before player-built house (M2); first Factions/Guests suite green is
  M3. Triage quarantine by default. Watchability via Hyprland special
  workspace, Xvfb+VNC fallback. Orchestrator = Opus with mechanical acceptance
  gates + escalation rule; design-setting specs = Fable workers, patterned
  waves = Opus workers.
- 2026-08-30 — Spike 0.1 accepted (git-bug 3fa4cf5). Bench v1 modlist confirmed
  at 38 active (6 DLC + 32 mods) with Guests/RegisterLanes/CashRegister in and
  HugsLib/PerspectiveShift out. `advance` is a budgeted `DoSingleTick` loop in
  `GameComponentUpdate` yielding every frame, not vanilla speeds; `max_tps`
  default 1000 with a thermal governor. The bench engine stub must be a real
  copy (a symlink re-roots the game at the Steam install), and the parked
  window needs `render_unfocused` or its frame-bound tick loop stalls. See
  FINDINGS.md.
- 2026-08-30 — `rwa` (1.4) and `rwtest` (5.1) live in THIS repo as `rwa/` and
  `rwtest/`, not in `rimworld-tools`. Reverses the original split. The CLI is
  the client half of a protocol whose server half is here, so a verb and its
  CLI surface change in one commit, one clone is a working system, and rwtest
  asserts against `JOURNAL.md`, which is also here. `rimworld-tools` has git
  but NO REMOTE (one "Initial import" commit, eabba3eb; 467MB, 206MB of it
  decompiled RimWorld and ~60 third-party mods), so the original split had no
  review trail and no route to the second bench. It stays reference-only.
  (Session 9 finished the move: 2.5 VENDORED baseviz into this repo — the
  in-repo `baseviz/` is the pinned copy, provenance sha in its README.)
- 2026-08-30 — **Fog of war is respected by the whole player-facing surface;
  `dev:*` is exempt.** Every observer and query hides undiscovered cells —
  `map-view`, `find-rect`, `nearest`, `room-at` alike — mirroring the action
  model's player/dev split so it stays one rule rather than a per-verb
  judgement. Spec 2.3 shipped three different behaviours in one file because
  the question was never asked. The agent must not site a building in ground
  it has never explored; and an agent with information no player has weakens
  the colony as a test substrate, since exploration-triggered mod bugs would
  never be provoked.
- 2026-08-30 — **`advance until:` is journal-tap only; `condition` becomes its
  own issue rather than a line in this document.** This section advertised
  `until:{letter|alert|event-match|condition}` while `TimeDriver` shipped
  `letter|threat|alert|event` — so `condition` did not exist and `threat` was
  undocumented. Found by 1.4's worker reading the C# against this prose. The
  line now describes what runs. `condition` is NOT dropped: a state predicate is
  categorically different from an event tap (nothing is emitted when a
  continuous value crosses a threshold), and it is the direct answer to this
  document's own observation that **alerts fire late** — `Alert_LowFood` is a
  lagging indicator. (This entry originally claimed `food_days < 3` leads the
  alert; the fc287ba verification refuted it — `Alert_LowFood` trips at
  nutrition per colonist < 4, and the digest divides by colonists PLUS
  prisoners, so `< 3` is strictly LATER on a prisoner-free colony. A leading
  predicate must sit above the alert's threshold, e.g. `food_days < 6`; the
  corrected acceptance lives on fc287ba.) Filed as its own spec so it has a
  home, dependencies and an acceptance section instead of being a word in a
  list nobody implemented.
- 2026-08-30 — **The observer surface has its own gate-in-the-widget rule:
  where the game's accessor writes, the serializer re-implements the
  derivation and cites the member.** 2.4's audit found four vanilla accessors
  whose READ mutates scribed state or the RNG, none visible at the call site:
  `ResearchManager.GetProgress` inserts a zero entry into a scribed dictionary
  on a miss (and `IsFinished`/`CanStartNow`/`RecipeDef.AvailableNow` all
  bottom out there); `Bill_Production.ShouldDoNow` writes the scribed `paused`
  on three paths; `Zone_Growing.PlantDefToGrow` assigns and scribes a default
  on a never-configured zone; `Zone.Cells` Fisher-Yates-shuffles a scribed
  list over the shared `Rand` stream. `WorldSafe`/`PawnSafe` hold the guarded
  routes; a serializer never touches a raw accessor those files ban, and each
  guarded route names the member it reproduces. Every result whose data came
  through a backing-field route publishes `source:"backing-field"` so "not
  configured" and "we could not look" never read alike. (2.3's `map-view`
  tripped the growing-zone write in shipped code; fixed session 5.)
- 2026-08-30 — **Blockers report HOW they are removable, not merely that they
  block.** A bare "not buildable" is useless: some obstacles are mined, some
  deconstructed, and some must be beaten down by a drafted colonist. The game
  already reifies this and it must be serialized, not reinvented —
  `Building.DeconstructibleBy(Faction.OfPlayer)` returns an `AcceptanceReport`
  carrying the game's own reason string, and `Designator_Deconstruct` answers
  the attack case with the literal `RemoveByAttackingTooltip`. Every spatial
  result that rejects a cell or thing carries a `removal` field:
  `mine` (`def.mineable` → 3.2 `designate mine`) ·
  `deconstruct` (`DeconstructibleBy` accepted → 3.2 `designate deconstruct`) ·
  `attack` (`def.IsNonDeconstructibleAttackableBuilding` → 3.4 draft + attack
  thing) · `none` (permanent), plus the game's own reason string verbatim.
  This is the concrete form of the standing "candidates + reasons, never bare
  booleans" invariant.
- 2026-08-31 — **When two specs claim the same verb, the split follows where
  the GAME puts the control.** 3.2 and 3.4 both listed "allowed area assign".
  The generalizable rule, and the same reasoning that produced the
  gate-lives-in-the-widget invariant: a verb belongs to the spec that owns the
  widget the player uses for it. So creating, renaming and painting an
  `Area_Allowed` is 3.2's (it is `AreaManager` plus cell writes — the
  `Dialog_ManageAreas` and area-designator surface, the same drag-a-rect
  vocabulary as its designations), and assigning a pawn to one is 3.4's (it is
  `Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap`, a column of the Assign
  tab beside `medCare`, `hostilityResponse` and `selfTend`, which 3.4 already
  owns — and it carries its own widget gate, `SupportsAllowedAreas` plus the
  player-faction test in `PawnColumnWorker_AllowedArea`). Deciding it by which
  spec "feels" spatial or "feels" pawn-ish would have split the same control
  two ways, which is the 2.3 fog-of-war failure in a different costume.
- 2026-08-31 — **A new hazard class: write-on-SAVE, not just write-on-read —
  and it means "save and read the Scribe XML" is not a neutral second reader
  for bills.** `Bill.ExposeData` (`RimWorld/Bill.cs`) runs this during the
  SAVING pass, before scribing the filter:

      if (Scribe.mode == LoadSaveMode.Saving && recipe.fixedIngredientFilter != null)
          foreach (ThingDef d in DefDatabase<ThingDef>.AllDefs)
              if (!recipe.fixedIngredientFilter.Allows(d))
                  ingredientFilter.SetAllow(d, false);

  So saving a game NARROWS the live `ingredientFilter` of every bill whose
  recipe has a `fixedIngredientFilter`, in memory, as a side effect of writing
  the file. It only ever removes allowances and it converges after one save,
  but two consequences are real. First, an observer can report a bill's filter,
  an autosave can fire with no player action, and the next read differs —
  correctly, with nothing wrong. Second, and the reason this is here rather
  than only in a code comment: **sessions 2.2 and 2.4 established "save the
  game and read the save's Scribe XML" as the independent second reader that
  makes an acceptance claim credible.** For bill ingredient filters that reader
  perturbs what it measures, so it is not independent and must not be cited as
  though it were. Use a live model read plus a game-acted change (the
  arrange-act-read lesson) instead. The known write-on-read accessors are
  catalogued in `WorldSafe.cs`; this one is a different class and the file's
  header should not be read as covering it.
- 2026-08-31 — **A vanilla helper can end in a tutorial modal, and a tutorial
  modal is a force-pause — so it wedges the run.** Found by 3.2's worker in
  `FlickUtility.UpdateFlickDesignation`, which unconditionally ends with
  `TutorUtility.DoModalDialogIfNotKnown(ConceptDefOf.SwitchFlickingDesignation)`.
  That helper does `Find.WindowStack.Add(new Dialog_MessageBox(msg))` whenever
  the concept has not been demonstrated (`RimWorld/TutorUtility.cs`), and
  `Dialog_MessageBox` sets `forcePause = true` (`Verse/Dialog_MessageBox.cs`).
  `PlayerKnowledgeDatabase.IsComplete` is a bare knowledge-lookup with **no
  tutor-enabled short-circuit**, so it fires on any save where the concept is
  fresh, whatever the tutorial settings say. Per 1.7 a force-pausing window
  halts every later `advance` at 0 ticks, and nothing clears it until 3.5's
  routing ships — so **one call to an innocuous-looking utility permanently
  wedges an unattended run.** A colony's first power switch would have done it.
  There are exactly five call sites in the 1.6 tree, and three are in specs we
  are still building: `FlickUtility` (3.2), `FloatMenuOptionProvider_Arrest`
  (3.4), `TradeShip.TradeGoodsMustBeNearBeacon` (3.5); `Settlement` is
  world-map (a v1 non-goal) and `Dialog_Options` is the settings UI. **The rule:
  a player verb reproduces the helper's gate and its effect, and drops the
  tutorial line** — never calls the wrapper. This is the same shape as the
  gate-lives-in-the-widget invariant seen from the other side: there the UI held
  a check the model lacked, here a "model" helper drags UI in with it. Treat any
  vanilla utility reaching `Dialog_MessageBox` as UI code wearing a
  model-shaped name.
- 2026-08-31 — **Amendment to the entry above: the modal hazard is wider than
  the tutorial helper, and it has two distinct shapes.** 3.4's worker found a
  sixth site my grep missed, because I searched for
  `DoModalDialogIfNotKnown` rather than for what actually hurts:
  `new Dialog_MessageBox`. The two shapes are not equally dangerous, and
  telling them apart is what keeps this rule cheap to follow:

  * **Behind an option delegate — safe if you never invoke one.**
    `FloatMenuOptionProvider_Equip`, `Building_OutfitStand`,
    `FloatMenuOptionProvider_Arrest` and friends build the modal INSIDE the
    `FloatMenuOption`'s action closure. Reproducing a provider's GATE and then
    taking the job yourself never runs that closure. This is already our
    practice and it is how `arrest` and `equip` are exposed safely
    (PawnOrderVerbs.cs reproduces each provider's gate and never invokes the
    option's action closure).
  * **On a utility's own execution path — dangerous.** The method raises the
    window as a side effect of doing its job, with no delegate in between:
    `FlickUtility.UpdateFlickDesignation` (tutorial modal), and
    `HealthCardUtility.CreateSurgeryBill(…, sendMessages: true)` ->
    `Bill.CreateNoPawnsWithSkillDialog` -> `new Dialog_MessageBox`, which fires
    from the ORDINARY surgery-bill path whenever no colonist meets the recipe's
    skill floor. **`sendMessages` defaults to true.** The same helper is reached
    from `ITab_Bills`, so 3.6's add-bill path inherits it.

  Vanilla itself shows the escape hatch — `Pawn_GuestTracker` calls
  `CreateSurgeryBill(…, sendMessages: false)` for its non-UI path. Prefer the
  game's own suppression flag where one exists; re-implement and drop the line
  where one does not; and re-derive whatever the suppressed message would have
  said as RESULT FIELDS, so the agent still learns what a player would have
  been told. The grep to run before any new verb spec is
  `new Dialog_MessageBox`, not the tutorial helper's name.
- 2026-08-31 — **The flick trap was live on the bench, and the counterfactual is
  measured rather than argued.** `Knowledge.xml` on `_RimWorld-Agent` has
  `SwitchFlickingDesignation` at **0**, and no concept in that file is above 0 —
  nothing has ever been demonstrated on this save. `PlayerKnowledgeDatabase
  .IsComplete` is `value > 0.999f`, so vanilla's `FlickUtility
  .UpdateFlickDesignation` WOULD have stacked a force-pausing `Dialog_MessageBox`
  on the colony's first power switch, wedging every later `advance`. 3.2's
  re-implementation was verified live on that exact save: `flick` designated the
  lamp and `interactions` reported `force_pause {count:0}` afterwards. Recorded
  because "we avoided a trap" is worth much less than "the trap was armed, here
  is the reading, and it did not fire."
- 2026-08-31 — **Deterministic goes in the mod; the playbook carries judgement.
  And judgement is CHOOSING a policy, not executing one.** Evan, arguing this
  out: "what can be in the mod should be." The test for "is this deterministic"
  is whether every branch is computable from state the observers already
  publish — and if it is not, the answer is to name the missing read, not to
  reach for the playbook.

  The post-raid procedure is the worked example. It looked like it needed
  judgement and does not: "the fight is over" is `threats.hostiles == 0` (no
  STANDING hostile, since `DigestVerb.ThreatSection` skips `p.Downed`); "rescue
  with the nearest capable pawn" is distance plus capability; "rescue before
  finishing off" is because bleeding is time-critical and finishing off is not;
  "finish off or capture" is the world fact *is there a prison*; and "a raider
  woke up" needs no decision at all because seek-at-will re-engages on its own.

  **The general pattern: what feels like judgement at execution time usually
  means a policy has not been decided yet.** "Finish off downed raiders?" looked
  like a judgement call until the policy was fixed, at which point it collapsed
  to a boolean about the world. The fix is to decide the policy once — in the
  playbook, with its reasoning, reviewable in a diff — and let the mod execute
  it every time thereafter.

  What genuinely is NOT deterministic, and belongs to the agent: anything
  needing a FORECAST rather than an observation (which research next, expand or
  fortify); anything weighing INCOMMENSURABLE outcomes (risk one colonist to
  save another, accept a quest whose reward is good and whose cost is a hostile
  faction); the OBJECTIVE itself, which nothing in world state implies; whom to
  recruit, which inherits both problems; and genuinely novel situations, where a
  hardcoded procedure would confidently do the wrong thing.

  Two corrections that produced this entry, recorded because they are easy to
  repeat. **`map.dangerWatcher.DangerRating` is not `threats.hostiles`** — the
  live case where a full-health swordsman wandered off to haul wood with three
  raiders alive was `DangerRating` reading "None" while `hostiles` was still 3.
  Conflating them is what made "is the fight over" look like a judgement call.
  And **"changing it needs a rebuild" is a virtue for a load-bearing
  deterministic procedure**, not a cost: it forces the change through a diff, a
  build and acceptance, which is the standard every verb is already held to.

  On cost, since that is what drove the question: the mod route is not "zero
  tokens", it is ONE round trip per firing instead of N. For a procedure that
  fires every raid and has roughly six steps, that difference is the argument.
- 2026-08-31 — **A verb can be a faithful clause-for-clause copy of the game's
  own widget and still mutate, because the state that made vanilla safe lives
  in the widget's CALLER.** `orders`/`prioritize` reproduce
  `FloatMenuOptionProvider_WorkGivers.GetWorkGiverOption` exactly, and both
  documented themselves "read-only". Neither set
  `FloatMenuMakerMap.makingFor`, which `FloatMenuMakerMap.GetOptions` — the
  caller, not the provider — sets around the whole option-building pass. Four
  members downstream branch on that static: `WorkGiver_DoBill
  .StartOrResumeBillJob` writes `bill.nextTickToSearchForIngredients =
  TicksGame + IntRange(500,600).RandomInRange` when it is unset (a
  `Rand.RangeInclusive` burn AND a bill suppression that applies to every
  colonist, since the field is read back with no pawn qualifier);
  `DangerUtility.NormalMaxDanger` returns `Deadly` only when it is set, so
  reachability was evaluated at a different threshold than the menu we claim
  parity with; and the `JobFailReason` strings the agent needs ("missing 30
  steel", from `WorkGiver_ConstructDeliverResources.JobOnThing`) are gated on
  it, along with the loop that collects ALL the missing defs instead of
  breaking at the first. Asking what a cook could do at a stove stopped the
  colony cooking. **The rule: reproducing a widget means reading its caller
  too, and grepping every member you invoke for the ambient statics it branches
  on.** Recorded as PawnSafe Class G — the hazard is not always a getter, and a
  read-only intention is not a guarantee. git-bug 32b9e01.
- 2026-08-31 — **Where an unavoidable side effect is genuine click-parity, it is
  DISCLOSED in the verb's own result, not suppressed.** The same work-giver scan
  still runs `BillStack.RemoveIncompletableBills` — which deletes a bill whose
  body part is gone — and still consumes one `UniqueIDsManager.nextJobID` per
  candidate job, a counter that IS scribed. Neither can be removed without
  ceasing to ask the game the question, and a player's right-click does both.
  A THIRD, wider than either and not in the issue — found while writing the
  acceptance: `WorkGiver_DoBill.ShouldSkip` asks every potential bill giver on
  the map for `BillStack.AnyShouldDoNow`, which is `Bill_Production.ShouldDoNow`
  per bill, which WRITES the scribed `paused` flag. `bills` refuses to call that
  method at all (the entry above; WorldSafe Class A) and `orders` cannot refuse,
  because the call is inside vanilla's `JobOnThing` and declining it means
  declining to ask. **Both are correct, and the difference is worth naming: a
  serializer picks its own route, a widget reproduction calls the game's code.**
  So `orders` names all three in its header and in its result note, and stops
  saying "read-only". The complement of the gate-lives-in-the-widget rule: where a
  player verb cannot be made cheaper than the click, it must at least be as
  HONEST as the click, and the agent is told what asking cost. `bills` now
  publishes `next_ingredient_search_tick` for the same reason — a bill can read
  `active` and be worked by nobody, and that field is the only observable proof
  that something ran a failing ingredient search on it.
- 2026-08-31 — **A mod-aware bridge answers `null` for "we could not look", never
  the mod's own falsy value** (git-bug 1a072fa, `Source/AutoRimmer/FswaBridge.cs`).
  `IsAutoArm` answers `false` for a pawn that is merely not opted in, so
  `catch { return false; }` on a reflection throw would make a broken bridge
  indistinguishable from a working one reporting "off" — wrong in the worst way,
  because it reads as data. Three rules, and they generalise to every third-party
  bridge this repo grows: **bind by signature** (parameter-type array AND return
  type AND a typed delegate, so drift fails at load rather than at invoke);
  **journal the fabrication instead of committing it** (`Journal.EmitWarning`,
  with pawn-free text, because EmitWarning dedupes by exact string and a per-pawn
  message burns the distinct-text cap); and **read the write back**, since a
  `void` setter that bails silently makes "the invoke did not throw" no evidence
  at all. An absent optional mod is refused BY NAME on its own lever while every
  other lever in the same call still applies — absence is not an error.
  Multiplayer, checked: `MpSync` registers `SetAutoArm` ITSELF as the synced
  method, so the direct call is the only route and the correct one — but MP's
  prefix fires only in interface context and AutoRimmer's verbs run from
  `GameComponentUpdate`, so under MP the write would stay local. Out of scope
  for a single-player bench, recorded so nobody rediscovers it.
- 2026-08-31 — **A modded column belongs on the verb that owns its tab, and its
  gate is the UNION of every widget that reaches the setter** (git-bug
  1a072fa). FindSuitableWeaponAndAmmo injects an auto-arm checkbox into the
  Assign tab, so it is a lever on `assign` rather than a verb of its own — the
  column strip is the verb's whole shape and a modded column is still a column.
  Two widgets reach `AutoArmTracker.SetAutoArm`, and the spec cited only one of
  them: `PawnColumnWorker_AutoArm.HasCheckbox` (`IsColonist && !WorkTagIsDisabled
  (Violent)`) and the pawn gizmo (`IsColonistPlayerControlled ||
  MechUtility.IsWeaponUsableMech`, which SKIPS the Violent check for mechs and
  is a player mech's only route, since no column cell is drawn for one).
  Enforcing just the column would have fabricated a restriction the player does
  not have, so the verb honours both and cites both. A third gate is not a
  widget at all and is the easiest to miss: the setter itself returns SILENTLY
  unless `MpSync.Configurable(pawn)` holds, so a dead pawn would otherwise be
  reported applied against a write that never happened.
- 2026-08-31 — **An observer's list has TWO orders — which entries survive the
  cap, and which order they are emitted in — and publishing only the first one
  makes the list unholdable.** `pawns` sorted by `PawnSerializer.Attention`
  descending and said so (`order:"attention-desc"`). That is a correct
  statement about a ranking and a trap for anything that keeps a position:
  attention sums `100 - mood_pct`, so two colonists one mood point apart trade
  places on any tick that moves either mood, and downed/mental/bleeding/tend
  flip in 400–1000 point steps. `roster[0]` names a different pawn on
  consecutive reads with nothing wrong. 3.4's acceptance rode that index, drew
  an actor with Hauling disabled, and six checks failed with no direct clue.
  The observer was the half that was wrong: `PawnActs.PawnList` has sorted
  `pawns:"colonists"` by `thingIDNumber` since it shipped, so the ACTION side
  already had a stable roster while the OBSERVER side did not — an agent could
  act on a list it could not re-read.

  **RimWorld promises nothing here, so this had to be a sort we add, not an
  order we document.** `MapPawns.AllPawnsSpawned` is the raw `pawnsSpawned`
  `List<Pawn>`: `RegisterPawn` appends, `DeRegisterPawn` removes, and
  `UpdateRegistryForPawn` does both — so a faction or host-faction change moves
  a pawn to the END while it stands still, and loading re-registers in
  save-file order. `RegisterPawn` *does* keep
  `pawnsInFactionSpawned[Faction.OfPlayer]` `InsertionSort`ed by
  `playerSettings.joinTick`, the closest thing the game has to a stable roster
  order — unusable here, because joinTick ties on every pawn that joined the
  same tick (`Pawn_PlayerSettings` sets a flat `joinTick = 0` for the starting
  colonists) and it is a different list from the one our verbs walk. And
  `Pawn_PlayerSettings.displayOrder` — the order the colonist bar and the
  Assign/Work tables actually draw in, via `PlayerPawnsDisplayOrderUtility` —
  is worse than useless: it defaults to the sentinel `-9999999` and is assigned
  **lazily by the UI**, `ColonistBar.CheckRecacheEntries` writing the scribed
  field on any pawn still holding the sentinel. On a bench where the bar has
  not recached it is the same number for every colonist, and sorting by it
  would mean depending on a UI side effect on scribed state — the write-on-read
  shape `PawnSafe` exists to refuse. Worth noting for its own sake: **the
  colonist bar is a Class-A write-on-read accessor**, and it is UI, so we never
  touch it.

  **The rule, generalizable past `pawns`:** selection may key on a live score;
  presentation keys on identity. `pawns` still ranks by attention to decide who
  survives the cap (2.6's rule — a cap that cut in list order would hide the
  downed colonist behind ten healthy ones — is untouched, and the re-sort runs
  on the survivors only, never on the candidate set), emits by `thingIDNumber`
  ascending by default, publishes BOTH facts (`selected_by` always
  `"attention-desc"`, `order` either `"id-asc"` or `"attention-desc"` per a new
  `order:` arg), and carries `attention_rank` on every line so the urgency the
  id order no longer encodes travels with the data instead of being lost to it.
  `thingIDNumber` is the key because it is stable for a pawn's lifetime and is
  already the id every verb takes (`pawn:<id>`) — the same key the action side
  had all along. `digest`'s colonist section deliberately does NOT change: it
  publishes no `id` at all, so it is a glance and not an addressable roster,
  and attention-desc is the whole point of it. The corollary for acceptance
  scripts is separate and both halves are needed: a stable index is still the
  wrong way to pick an actor with a REQUIRED CAPABILITY, so 3.4's phase 0 now
  selects by predicate over `pawn {sections:["work"]}` `disabled` and only
  falls back to position. Stability makes an index reproducible; it does not
  make it meaningful.

  **And the promise has a stated limit, because a half-true contract is worse
  than none: a stable EMIT order does not make MEMBERSHIP stable.** The cap
  still cuts by attention — it has to, that is 2.6 — so above `cap` the
  surviving set moves as the live score moves and `list[0]` can still name a
  different pawn on consecutive reads. `more > 0` is precisely that flag: when
  it is non-zero, position is reproducible only among the survivors, and a
  caller wanting a durable handle raises `cap` past `total` or holds the `id`
  rather than the index. When `more == 0` the sequence is a register. This is
  the same "cap the output, count the truth" discipline the observers already
  follow, applied to the one place where truncation and ordering interact.
- 2026-08-31 — **A play setting belongs to the verb that owns ITS widget, not to
  a `play-settings` grab-bag — so manual work priorities is an argument on
  `work-priorities`.** 3.4's acceptance found that `work-priorities` correctly
  refuses priority 1, 2 or 4 while manual priorities are off, and that nothing
  in the ~90-verb surface could turn them on (git-bug `e8f2c32`).
  `PlaySettings.useWorkPriorities` scribes `defaultValue: false`
  (`RimWorld/PlaySettings.cs` ExposeData), so eight acceptance checks were
  unreachable on every colony the agent stages itself, and reachable only on a
  save where a human had already ticked the box.

  The lever is now `work-priorities {manual:true|false}`, and the reasoning
  generalizes to the rest of `PlaySettings`. **Where does the GAME put the
  control?** `RimWorld/MainTabWindow_Work.cs DoManualPrioritiesCheckbox` draws
  this checkbox at (5,5) of the *same* `MainTabWindow` whose body is the
  priority matrix `PawnColumnWorker_WorkPriority` fills in. That is the
  2026-08-31 rule above applied to a lever instead of to a verb, and it settles
  the other 23 fields on that class the same way rather than one at a time: the
  `defaultCareFor*` block belongs to whoever owns the medical-defaults dialog,
  and the twenty overlay/visibility toggles belong to `PlaySettings
  .DoMapControls`'s own row — a different window, and pure rendering with no
  headless meaning. A single `play-settings` verb would have gathered
  twenty-four fields whose only common property is the class they are declared
  on, which is the fog-of-war failure (one question answered three ways) in yet
  another costume. Two supporting reasons, both secondary to that one: it makes
  the flip and the cells ONE round trip with a fixed ordering — the flag is
  installed before the priorities in the same call are validated against it,
  which is exactly the fixture-staging sequence the bug blocked — and it keeps
  the read (`use_priorities`, already published beside every row) and the write
  on one verb. Zetrith's Multiplayer corroborates that this is colony state
  rather than a client display preference: it scribes the whole `PlaySettings`
  per faction (`Multiplayer.Client/FactionWorldData.playSettings`) and brackets
  `DoManualPrioritiesCheckbox` with a sync marker.

  **The widget's EFFECT is the load-bearing half here, not its gate.** There is
  no gate — the checkbox is unconditional inside the window, and the only
  precondition is the tab itself (`MainButtonWorker.Disabled` =>
  `Find.CurrentMap == null`, which the verb's existing `Map()` call already
  reproduces). What the widget does that a naive field write does not is fan
  `Pawn_WorkSettings.Notify_UseWorkPrioritiesChanged()` out to every
  player-faction pawn with non-null `workSettings`. That sets `workGiversDirty`,
  and `WorkGiversInOrderNormal`/`Emergency` — the lists `JobGiver_Work` actually
  walks — rebuild only when it is set. Flip the field alone and every colonist
  keeps dispatching off an order computed under the OLD reading until some
  unrelated `SetPriority` happens to dirty the cache: a silent, delayed, wrong
  answer. **So the gate-in-the-widget invariant has a mirror image worth stating
  once: a widget's non-obvious SIDE EFFECT is as much a part of the click as its
  precondition, and a verb that reproduces only the gate is half a verb.**

  **And the flip is lossless, measured against the source rather than assumed.**
  `useWorkPriorities` is a read-time mask and nothing else —
  `Pawn_WorkSettings.GetPriority` is `int num = priorities[w]; if
  (pawn.RaceProps.Humanlike && num > 0 && !Find.PlaySettings.useWorkPriorities)
  return 3; return num;`. `SetPriority` writes the raw number either way,
  `ExposeData` scribes the raw `DefMap`, and `Notify_UseWorkPrioritiesChanged`
  touches one bool. A stored 1 survives a trip to off and back. What DOES
  destroy data is a *write* while off: vanilla's checkbox column
  (`WidgetsWork.DrawWorkBoxFor`'s else-branch, and `PawnColumnWorker_WorkPriority
  .HeaderClicked`'s) can only write 0 or 3, so a click — or a `work-priorities`
  call using the two priorities it still permits — flattens a stored 1/2/4. The
  verb says so in its `manual.note` rather than leaving the agent to find out.
  Note also that the mask is gated on `RaceProps.Humanlike`: a Biotech mech's
  priorities read raw whatever the setting says.
- 2026-08-31 — **A vanilla helper can do the whole job, return `void`, and take
  its most important input from AMBIENT state rather than from its signature —
  so wrapping it is correct for a player and lossy for us, silently.** PawnSafe
  Class G is a widget whose CALLER supplied the state the provider branched on;
  this is the same widening seen from the other side, and it is why `move-to`
  accepted `queue:true`, dropped it, replaced the running job and reported
  success (git-bug bc2250b). `RimWorld/FloatMenuOptionProvider_DraftedMove.cs
  PawnGotoAction` ends in `pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)` — the
  default `requestQueueing: false`. Vanilla is not missing the capability:
  `Verse.AI/Pawn_JobTracker.cs TryTakeOrderedJob` reads
  `KeyBindingDefOf.QueueOrder.IsDownEvent` ITSELF and ORs it with that
  parameter, so a player's shift-click queues the goto. The switch is LIVE
  KEYBOARD STATE — permanently false on an unattended bench, and the helper's
  signature offers no way to override it. The verb registry rejects wrong TYPES
  but not unknown KEYS, so nothing on the way in could have caught the dropped
  argument either.

  **And the helper's return tells you nothing.** `PawnGotoAction` is `void`; its
  internal `flag` is consumed only to decide whether to draw a fleck, and it is
  TRUE on two paths that take no job at all — the pawn already standing on the
  destination, and the pawn already running a `Goto` to it. So "the call did not
  throw" was never evidence that an order happened. That is the `FswaBridge`
  read-the-write-back rule reached from the opposite direction: there a `void`
  setter that bails silently, here a `void` helper that reports nothing.

  The fix is to REPRODUCE the helper's clauses with the parameter added, citing
  it clause for clause, and to keep calling the game's own pieces INSIDE that
  reproduction (`RCellFinder.BestOrderedGotoDestNear` stays vanilla's):
  **reproduce the smallest thing that has the missing parameter, not the whole
  feature.** **The rule this leaves behind is cheap to run — before wrapping any
  vanilla helper, ask what the GAME supplies that we cannot (keyboard state,
  `Event.current`, a UI static, a selection) and whether the return
  distinguishes "did it" from "declined"; grep the helper's body for
  `KeyBindingDefOf`, `Find.Selector`, `Event.current` and `Messages.Message`.**
  Wrap-don't-reinvent is still the default; this is the exception's shape.
  Recorded as PawnSafe Class I.
- 2026-08-31 — **A derived boolean can be RIGHT and still be read as an answer
  to a question it was never computing.** `ordered` is published as the triple
  `jobGiver is ThinkNode_QueuedJob && workGiverDef != null && playerForced`, and
  a `wear` order reads `ordered:false`. Every reader took that for a bug. It is
  not: `ordered` means *prioritized-WORK order*, and a direct order has no
  WorkGiver. The field is correct and its NAME invites the wrong question.

  **The reasoning recorded for that triple was nonetheless backwards, and the
  correction matters more than the field.** The `workGiverDef != null` clause
  was believed to be what excludes `JobGiver_Work`'s autonomous `playerForced`.
  It is not. `RimWorld/JobGiver_Work.cs TryIssueJobPackage` reaches its
  emergency-prioritized branch through `GiverTryGiveJobPrioritized`, which sets
  `workGiverDef` on the job BEFORE the branch sets `playerForced = true` — so
  the autonomous job carries BOTH clauses and neither excludes it. What actually
  rejects it is `jobGiver is ThinkNode_QueuedJob`: `Verse.AI/Pawn_JobTracker.cs
  StartJob` assigns `curJob.jobGiver` from the ThinkResult's source node, and
  that node is the `JobGiver_Work` itself, never a queued node. The queued-node
  clause is load-bearing for a second reason too — four non-player sites
  (`JobDriver_AttackStatic`, `JobDriver_AttackMelee`, `JobInBedUtility`, and
  `Pawn_JobTracker`'s `resumeCurJobAfterwards`) enqueue with `playerForced`
  false, so the PAIR is the discriminator and the WorkGiver split is a
  refinement on top of it. That is why splitting on it is safe.

  **The resolution is to publish the distinction rather than to widen the
  boolean**: `order_kind` = `work` | `direct` | `null`, alongside an unchanged
  `ordered`, with `ordered == (order_kind == "work")` by construction. Widening
  `ordered` to mean "the player caused this" would have re-admitted exactly the
  autonomous `playerForced` the triple was built to exclude. **The rule: when a
  field is correct but under-read, add the field that answers the question being
  asked — do not redefine the one that is already right.**
- 2026-08-31 — **`TryTakeOrderedJob` has THREE outcomes past its early-out, and
  a verb that reports only `accepted` cannot tell you which one you got.** Past
  the `JobIsSameAs` collision return, `Verse.AI/Pawn_JobTracker.cs` branches:
  `ClearQueuedJobs` + `EnqueueFirst` + `EndJobWith(InterruptForced)`; or
  `EnqueueLast`; or `ClearQueuedJobs` + `EnqueueLast`. Three consequences that
  nothing published and every one of which an agent would misread:

  1. **`queue:true` on an IDLE pawn does not queue.** The first branch is
     reachable with `requestQueueing` set whenever `mindState.IsIdle ||
     CurJob == null || CurJob.def.isIdle`, and it STARTS the job. `job_queue.total`
     then reads 0 — not because publication is broken, but because nothing queued.
  2. **Two of the three branches CLEAR THE EXISTING QUEUE.** An order can
     silently destroy work already lined up.
  3. **The third queues an order the caller never asked to queue**, leaving the
     pawn on its current job, and still answers `accepted:1`.

  So accepted lines now carry `order_effect` (`started` | `queued` | `gone`),
  `queue_depth` and `queue_dropped`, with a note attached only when the effect
  CONTRADICTS the request. All three reads are side-effect free and by reference
  identity — never `JobIsSameAs`, whose `GetCachedDriver` lazily allocates and
  `Log.Error`s on a pawn mismatch (PawnSafe Class H).

  **The acceptance lesson travelled with it and is the more general one.** The
  suite staged its "running job" with `wear`, then advanced 2500 ticks — long
  enough to FINISH wearing. Worn apparel is unspawned, so `ThingArg` refused it
  and the stage silently did nothing, leaving an idle pawn, firing branch 1, and
  producing a queue reading of 0 that looked exactly like a publication bug.
  **A fixture that fails silently is indistinguishable from the defect it was
  built to detect** — so a staging step must assert that it staged (git-bug
  4087644, ac407f1).

- 2026-08-31 — **Modal-dialog verbs transact the model and NEVER take the job,
  because both vanilla entry points are jobs whose only payload is a window**
  (specs 3.5 `20e5cda`, quest observer `548ef48`). The session-4 amendment
  assumed `trade start` would be "order-then-advance-then-transact"; the
  decompiled read says that buys nothing. `JobDriver_TradeWithPawn`'s final
  toil is `if (Trader.CanTradeNow) Find.WindowStack.Add(new Dialog_Trade(actor,
  Trader))`, `JobDriver_UseCommsConsole`'s is `commTarget.TryOpenComms(actor)`,
  and both `TradeShip.TryOpenComms` and `Faction.TryOpenComms` are a
  `WindowStack.Add`. `Dialog_Trade`'s constructor calls `TradeSession.SetupWith`
  — the same static state the verb sets — and sets `forcePause = true`. So the
  job produces IDENTICAL model state plus a force-pausing window, and 1.7 proves
  a force-pausing window halts every subsequent `advance` at 0 ticks.
  (`Dialog_Trade.PostOpen` can stack a second modal on top of it: a
  `Dialog_MessageBox` when the negotiator's Talking or Hearing is below 0.95.)
  **The gates the walk would have had to pass are reproduced in full** —
  including `CanReach` on both routes — and every such verb publishes
  `negotiator_walked:false` rather than implying a colonist crossed the map.

- 2026-08-31 — **Three red errors are reachable from the model side of this
  surface, and each is pre-empted by reproducing the widget clause that guards
  it.** This is the gate-in-the-widget invariant paying for itself three times
  in one spec, so the instances are named:
  `QuestPart_Choice.PreQuestAccept` Log.Errors "still has a choice unresolved"
  and auto-picks the first when `choices.Count >= 2` — so `quest-accept`
  REQUIRES `choice` while one is outstanding, which is exactly what
  `MainTabWindow_Quests.DoAcceptButton` does by not drawing the plain Accept
  button at all in that case. `Transferable.AdjustTo` Log.Errors "Failed to
  adjust transferable counts" on an out-of-range count — so `trade-set` asks
  `CanAdjustTo` first and refuses with the game's own
  `UnderflowReport`/`OverflowReport`, as `TradeUI.DrawTradeableRow` does by
  pre-clamping to `GetMinimumToTransfer`/`GetMaximumToTransfer`.
  `Building_CommsConsole.GetFailureReason`'s own last clause Log.Errors "could
  not use comm console for unknown reason" — so `comms-call` reproduces the
  solar-flare and power clauses that make it unreachable. A fourth is an NRE
  rather than a red error and is the same shape: `TradeDeal.TryExecute`'s
  cannot-afford branch calls
  `Find.WindowStack.WindowOfType<Dialog_Trade>().FlashSilver()` unguarded, which
  NREs on exactly the headless path, so `trade-confirm` pre-tests the exact
  negation of that condition and returns "MessageColonyCannotAfford" itself.
  **None of the four is caught; all four are made unreachable.** A caught
  exception leaves the model half-validated and tells the caller nothing.

- 2026-08-31 — **A confirmation modal becomes an ARGUMENT, never a window.**
  Three vanilla paths in this spec end in `Dialog_MessageBox.CreateConfirmation`
  or equivalent: the trader-short-funds confirm in
  `Dialog_Trade.DoWindowContents` ("ConfirmTraderShortFunds"), the royal-favour
  accepter confirm in `MainTabWindow_Quests.AcceptQuestByInterface`
  ("QuestGivesRoyalFavor" + "WantToContinue"), and — outside this spec but the
  same family — 3.4's `Bill.CreateNoPawnsWithSkillDialog`. Each is reproduced as
  a default-false boolean arg (`allow_trader_short_funds`,
  `confirm_accepter_warnings`) whose refusal carries the findings as RESULT
  FIELDS. Default-false is the headless equivalent of the player clicking "Go
  back", and it means an unattended run cannot be surprised into a decision the
  game thought was worth asking about.

- 2026-08-31 — **`quest.dismissed` is cosmetic filtering and is reported as
  such, in the payload, every time.** `MainTabWindow_Quests.DoDismissButton`'s
  label keys are literally "DismissQuest"/"UnDismissQuest"; it does not decline,
  expire or end anything, and `Quest.Accept` sets `dismissed = false` on its way
  past. An agent that reads it as "we said no" will be wrong, so `quests`,
  `quest` and `quest-dismiss` all carry a `dismissed_means` / `note` string
  rather than trusting a comment nobody will read.
  **The tab's THIRD state is a HIDE, not a delete, and an earlier draft of this
  entry said the opposite.** `DoDismissButton`'s historical branch is
  `selected.hiddenInUI = true;` + `SoundDefOf.Tick_High` + `Select(null)` +
  `return` — `QuestManager.Remove` is never called from that window at all (its
  only vanilla callers are `Verse/DebugActionsQuests` and
  `RimWorld/QuestPart_SubquestGenerator`). ONLY THE TOOLTIP KEY SAYS DELETE:
  `string key = (selected.Historical ? "DeleteQuest" : ...)`. Reading that key
  as a destructive call is what made `quest-dismiss` refuse historical quests
  outright; it now reproduces the hide and reports `mode:"hide"`. The hide is
  ONE-WAY — nothing in the game clears `hiddenInUI` — so the verb refuses
  `dismissed:false` on a historical quest rather than inventing an un-hide no
  player has.
  Two further properties of the same button that a model-side write drops if it
  is not looking: it is a **TOGGLE** (`selected.dismissed = !selected.dismissed`),
  so `quest-dismiss` toggles when `dismissed` is omitted and only sets when it is
  given; and it **PROPAGATES ONE LEVEL** —
  `foreach (Quest subquest in selected.GetSubquests()) subquest.dismissed =
  selected.dismissed;`. `QuestUtility.GetSubquests` walks the plain
  `QuestManager.questsInDisplayOrder` list for `parent == quest` (direct children
  only, no write-on-read), and writing the parent flag alone leaves a
  parent/subquest split that no player can reach through the widget.

- 2026-08-31 — **"Cancel leaves the trade untouched" is defined against the
  world, not against the statics, and what opening a session costs is disclosed
  rather than suppressed.** `TradeSession.Close()` is `trader = null;` and
  nothing else — `deal`, `playerNegotiator` and `giftMode` stay set — so
  "untouched" means NO `Tradeable.ResolveTrade` RAN AND COLONY SILVER AND STOCK
  COUNTS ARE UNCHANGED. It cannot mean "nothing moved at all", because
  `TradeDeal.AddAllTradeables` runs `ThingMaker.MakeThing(ThingDefOf.Silver)`
  when the trader has no silver tradeable, and `Thing.PostMake` calls
  `ThingIDMaker.GiveIDTo` -> `GetNextThingID()` (a `Scribe_Values`-scribed
  counter) plus `def.startingHpRange.RandomInRange`. **Opening a trade burns a
  scribed thing ID and a Rand call.** Small, unavoidable, and exactly what the
  click does — so `trade-start` publishes it under `session_cost`, the same
  call `orders` made for the job-ID counter. `TradeSession.Close()` has ZERO
  vanilla callers (the window owned the lifecycle), so this verb set closes the
  session itself on every exit path, and reproduces `Dialog_Trade.Close()`'s one
  model effect — `TradeUtility.ReceiveQuestFromTrader` for a quest-giving trader
  pawn — while dropping its sound.

- 2026-08-31 — **TWO widget gates in this spec are deliberately WAIVED, and
  both are named here so neither omission reads as a gap.**
  **Waiver 1 — `Dialog_NodeTree.InteractiveNow`.** `Dialog_NodeTree.DrawNode`
  calls `curNode.options[i].OptOnGUI(rect3, InteractiveNow)`, and `private bool
  InteractiveNow => Time.realtimeSinceStartup >= makeInteractiveAtTime;` with
  `makeInteractiveAtTime = RealTime.LastRealTime + 1f` under
  `delayInteractivity: true`. That `active` argument is ANDed into the same
  `Widgets.ButtonText` call as `!disabled`, so it is a real gate. It is waived
  because it is an anti-misclick delay rather than a game rule, it is wall-clock
  rather than tick-based, and reproducing it would put real-time dependence into
  an otherwise deterministic verb. `ChoiceLetter.OpenLetter` and
  `DeathLetter.OpenLetter` both pass `delayInteractivity: false`, so letters are
  unaffected either way.
  **Waiver 2 — `dialog-dismiss` closes a window no player could close.**
  `Verse/Dialog_NodeTree`'s constructor sets `closeOnCancel = false`, and
  `Verse/Window.OnCancelKeyPressed` closes only `if (closeOnCancel)`. So there is
  NO PLAYER ROUTE that clears a node tree without pressing one of its options —
  escape does nothing — and `dialog-dismiss` removes it anyway. The reason it is
  waived rather than honoured: the spec's Scope requires an esc-equivalent that
  ALWAYS works against a modded dialog nobody has read, and 1.7 proves an
  unanswerable force-pausing window halts every subsequent `advance` at 0 ticks.
  A faithful verb here would leave an unattended run wedged forever, which is the
  failure this spec exists to prevent. The cost is stated in the verb's own
  `note` rather than left for the agent to discover: dismissing a
  `ChoiceLetter_*` node tree without choosing SKIPS the option's `action`, and
  that closure is where `LetterStack.RemoveLetter` lives — so the LETTER SURVIVES
  the dismissal and can re-open and re-wedge the run. `dialog-dismiss` is the
  last resort; `letter-choose` / `dialog-choose` answer the decision.
  Recorded here so the omissions are decisions, not gaps.

- 2026-08-31 — **Where a vanilla option's `action` closure mixes a model effect
  with presentation, the presentation is REVERTED by a window diff rather than
  guessed at by label.** `ChoiceLetter.Option_ViewInQuestsTab`'s action is
  `SetCurrentTab(Quests)` + `.Select(quest)` + `RemoveLetter(this)` and
  `Option_JumpToLocation`'s is `CameraJumper.TryJumpAndSelect(target)` +
  `RemoveLetter(this)` — the letter removal and any quest/faction mutation are
  in the SAME closure as the UI drive and cannot be split from outside. So the
  walker snapshots the window stack, runs the action, and closes any
  `MainTabWindow` that appeared (`EscapeCurrentTab(playSound:false)`), reporting
  it as `presentation_reverted`. Anything ELSE that appeared is REPORTED and
  left standing, because a window an action raised is a real decision the agent
  now owes and silently closing it would answer it for them. This generalises
  3.2's flick-tutorial rule from "drop the known line" to "revert the observed
  effect", which is what makes it safe against a modded letter nobody has read.

- 2026-08-31 — **A letter is answered through the WINDOW it opened, not beside
  it, and the two are different objects.** `ChoiceLetter.Choices` is an ITERATOR
  that constructs a `new DiaOption(...)` on every enumeration, and `DiaOption
  .dialog` is assigned in exactly one place — `Dialog_NodeTree.GotoNode`. So the
  options a letter-side verb enumerates are NOT the objects inside an open
  `Dialog_NodeTreeWithFactionInfo`: replaying one removes the LETTER while the
  WINDOW stays up, still `forcePause`, and `LetterStack.OpenAutomaticLetters`
  keeps early-returning — the 1.7 wedge, unanswered. `letter-choose` therefore
  looks for the window first and routes through ITS option objects (`via:
  "open-window"`), matching on the one deterministic link available: `Choice
  Letter.OpenLetter` builds `new DiaNode(text)` from the letter's own `Text`, so
  the open window's `curNode.text` starts with it (`DeathLetter.OpenLetter`
  appends a battle-log tail, hence StartsWith rather than equality). A modded
  letter that builds its node from something else simply does not match, and the
  verb says so in `still_blocked` and points at `dialog-dismiss` rather than
  reporting a clean answer over a halted run. Every result on this surface
  carries 1.7's `ForcePausePayload` verbatim under `force_pause` — one window
  vocabulary across `advance`, `status`, `interactions` and these verbs, never a
  second.
- 2026-08-31 — **Three gates nobody had written down, and they share a shape:
  the widget's most consequential behaviour is often not its precondition at
  all — it is a filter on what the dropdown OFFERS, a side effect that fires
  after the click, or a cost the read itself pays.** Spec 3.6 (git-bug
  `48f666c`) hit all three in one file, and each was invisible from the model
  side, so each is recorded here rather than only in a comment.

  **(a) `Dialog_BillConfig.FillOutputDropdownOptions` never offers an
  UNGROUPED storage building as a bill's output, and no vanilla
  `Building_Storage` can be offered.** The collection clause is
  `else if (!(slotGroup.parent is Building_Storage building_Storage) ||
  building_Storage is IRenameable)` — so a slot group whose parent is a
  `Building_Storage` is admitted *only* when that building implements
  `IRenameable`, and **no vanilla `Building_Storage` does**. The branch above it
  admits `slotGroup.StorageGroup` unconditionally. Net effect: the "store in…"
  and "Include from" dropdowns list **stockpile zones and STORAGE GROUPS, and
  nothing else** — a bare shelf or crate is never a bill's specific output, and
  linking it into a group with `storage-link` is the thing that makes it
  offerable. `Bill_Production.SetStoreMode(mode, group)` accepts the shelf's
  own `SlotGroup` perfectly happily, so a verb that resolves a target and calls
  the setter hands the agent an output the player cannot select, on a bill the
  player then cannot see the reason for. `bill-set {store_target}` and
  `bill-set {include_from}` therefore both refuse it with
  `gate:"ungrouped-storage-building"` and name `storage-link` as the route.
  **The general form is the one worth carrying: a dropdown's CANDIDATE FILTER
  is a gate, and it is the easiest kind to miss, because there is no `if` in
  front of the setter to notice — the restriction lives in the code that built
  the list.** Same class as `BillDialogUtility.GetPawnRestrictionOptionsForBill`
  drawing an unclickable null-action row for a pawn whose work type is
  disabled: the model would take that write too.

  **(b) `targetCount` and `unpauseWhenYouHave` are COUPLED by the widget, and
  the coupling is in the drawing code, not in either field's setter.**
  `Dialog_BillConfig.DoWindowContents` reads the old value, draws the
  `IntEntry`, and then unconditionally runs
  `bill.unpauseWhenYouHave = Mathf.Max(0, bill.unpauseWhenYouHave +
  (bill.targetCount - oldTargetCount));` — i.e. the unpause threshold TRACKS
  the target by delta, every frame the dialog is open. A verb that writes
  `targetCount` alone leaves a threshold the player never chose, and the bill
  then unpauses at a number nothing in the UI would ever have produced. This is
  the same shape as `work-priorities` needing
  `Notify_UseWorkPrioritiesChanged` (2026-08-31, above) and it is the second
  time the rule has paid: **a widget's non-obvious SIDE EFFECT is as much a
  part of the click as its precondition, and a verb that reproduces only the
  gate is half a verb.** `bill-set {repeat:"target", target:N}` reports BOTH
  `target` and `unpause_when_you_have` in `configured`, so the coupling is
  visible in the result rather than inferred — and 3.6's acceptance asserts
  both, because a suite checking only `target` passes against the half-verb.

  **(c) Constructing a `Bill` to ASK A QUESTION burns a scribed id.** Every
  `Bill` ctor ends in `InitializeAfterClone()`, which assigns
  `loadID = Find.UniqueIDsManager.GetNextBillID()` — a counter that
  `UniqueIDsManager.ExposeData` scribes. So `new Bill_Production(recipe)` in a
  READ path permanently advances the save's bill-id counter, once per call, per
  recipe. `bill-options` needs `RecipeWorkerCounter.CanCountProducts(bill)` for
  every recipe on a bench to say whether "do until you have X" is even
  offerable, and the obvious implementation would burn one id per recipe per
  call, forever. It passes `null` instead: all three vanilla counters ignore
  the argument (the base is
  `specialProducts == null && products != null && products.Count == 1`, and
  `RecipeWorkerCounter_ButcherAnimals` / `_MakeStoneBlocks` both `return
  true`), and a modded counter that dereferences it throws into a catch that
  falls back to the base predicate. **This is WorldSafe Class A reached from an
  unusual direction — not a lazy getter that writes, but a CONSTRUCTOR whose
  cost is the write — and the rule it leaves is: before instantiating a game
  type to interrogate it, check whether its ctor touches a `UniqueIDsManager`
  counter, and prefer passing `null` to a worker that will ignore it.** DESIGN
  already records the `nextJobID` burn `orders` pays; that one is unavoidable
  because the job must exist. This one was entirely avoidable.
- 2026-08-31 — **A fixture verb inherits the FICTION of the thing it models, and
  the fiction decides the default — so `dev:starter-kit` forbids and
  `dev:spawn-thing` does not.** (git-bug 091e3f0.) The kit models a colony
  ARRIVAL. `RimWorld/ScenPart_PlayerPawnsArriveMethod.DoDropPods` ends in
  `DropPodUtility.DropThingGroupsNear(..., forbid: true, ...)`, and
  `Data/Core/Defs/Tutor/Instructions.xml` puts `UnforbidStartingResources`
  immediately after the stockpile steps (`MakeStockpile` ->
  `EndStockpileDesignating` -> `UnforbidStartingResources`, with
  `BuildRoomWalls` waiting on its `InstructionDeactivated-` tag), and the
  tutorial does not advance until the player does it: un-forbidding the
  starting pile is a step every player takes on every colony. A kit that skipped it handed the
  agent an affordance no player has — the same argument this log already makes
  about fog — and left M1 unable to rehearse `unforbid` against a real
  obstacle, which is how a live run once found a forbidden rifle, revolver,
  knife and flak set sitting unused while `FSWA_MapComponent`'s three
  `!thing.IsForbidden(Faction.OfPlayer)` checks stepped over all four.
  `dev:spawn-thing` does NOT follow, and the reason is not taste: its
  provenance is `Verse/DebugThingPlaceHelper.DebugSpawn`, which contains no
  forbidding at all. A bare spawn models a thing APPEARING. It takes an opt-in
  `forbid` arg — which is how the kit gets the behaviour without reimplementing
  placement — and keeps `false`. The rest of staging has nothing to forbid:
  pawns, research and fog carry no `CompForbiddable` between them.

  Two findings the implementation had to absorb. **(a) The flag is not
  universal, and its absence is silent by design.**
  `RimWorld/ForbidUtility.SetForbidden` needs a `ThingWithComps` carrying a
  `CompForbiddable`; the comp is declared per def, `ResourceBase` has it (so
  resources, food, medicine, weapons, apparel and `MinifiedThing` do) and
  `BuildingBase` does not — `Bed`, which the kit's own `medical` preset spawns,
  has no comp anywhere in its BedWithQualityBase -> BedBase -> FurnitureBase ->
  BuildingBase chain, and no pawn has one either. `warnOnFail: true` makes that
  a `Log.Error`, i.e. a red_error the journal records and the bench's
  zero-red-errors rule will not have; `warnOnFail: false` makes it a no-op
  after which `IsForbidden(Thing, Faction)` answers false forever. The game
  RELIES on the silence — `DoDropPods` puts the arriving pawn in the same group
  and forbids it too. We take the game's `warnOnFail: false` and then REPORT
  every miss with its reason, because a silent no-op inside a fixture is the
  failure mode this codebase keeps paying for. **(b) The fixture and the remedy
  must be the same set.** `DropThingGroupsNear` forbids blind; `Dev.Forbid`
  asks `RimWorld/Designator_Forbid.CanDesignateThing` first (`category == Item`
  AND a `CompForbiddable`) because that same category test also gates
  `Designator_Unforbid.CanDesignateThing`, which the shipped `unforbid` verb
  drives. Forbidding a Building-with-comp — a door, a shelf — would leave the
  agent a lock with no key, an obstacle no player verb in this mod can clear.
  The ordering deviates too: the game forbids BEFORE placing, we forbid the
  placed result, because `CompForbiddable` overrides neither `AllowStackWith`
  nor `PreAbsorbStack` and a forbidden stack absorbed into an unforbidden one
  loses the flag entirely — tolerable when a pod lands on open ground, not when
  a kit aims at a stockpile.
- 2026-08-31 (session 11) — **When an acceptance suite goes red here, suspect
  the INSTRUMENT before the mod.** Measured, not asserted: the first live run of
  all five drivers produced twelve failures, and **eleven were driver defects
  requiring no mod change**. The three shapes they took are worth naming because
  each recurred: a dig at the wrong key (`data.rows` where `JournalVerbs.Read`
  publishes `data.events`); a value read flat where the payload nests it
  (`verb` vs `payload.verb`); and an argument that is a class name rather than a
  defName (`Warden_DeliverFood` vs `DeliverFoodToPrisoner`), which made
  `Dev.Named` throw before the verb built its `Outcome` so the reply had **no
  `data` block at all**. That last one matters most: an absent block and a
  gate-less rejection are indistinguishable to `dig`, so the failure said
  "the mod published no gate" when the truth was "the call never ran".
- 2026-08-31 (session 11) — **A check that cannot go red is worse than a check
  that fails, and it hides best behind neighbours that do fail.** `3.4`'s `3.6a`
  ("the pawn is WEARING the parka") PASSED while asserting nothing: the bench
  colonist already wore one before the policy was ever assigned. Three genuine
  reds sat beside it from the same cause, and repairing only those would have
  shipped a 150/150 that proved nothing about the clothes loop. The rule that
  follows: when a fixture precondition turns out to be unmet, re-examine the
  checks that PASSED under it before fixing the ones that failed. Corollary
  found the same night in `8b0b88f`, the suite written specifically to close the
  absent-key trap: its own `1.2l/m` dug `data.action.rejected_by_reason`, and
  the response's `action` block is `{journal_seq}` and nothing else. The tally
  ships under two spellings in two PLACES — `rejects_by_reason` on the data
  block (`DesignateEngine.PublishRejects`), `rejected_by_reason` on the JOURNAL
  ROW (`DesignationVerbs.Designate`). Reading the journal at the seq the action
  block names is both the correct check and the stronger one.
- 2026-08-31 (session 11) — **An ASCII grep cannot verify a string literal in a
  .NET assembly.** Literals live in the UTF-16 `#US` heap, so
  `grep -a 'target_note' AutoRimmer.dll` returns 0 on an assembly that contains
  it, while type and member names in the `#Strings` heap DO match — which makes
  the false negative look selective and therefore credible. Count as UTF-16LE
  instead. The build rules already recorded this for `InformationalVersion`;
  it generalises to every literal, and a verification pass that greps for new op
  names is measuring nothing.
- 2026-09-01 (session 13) — **A deterministic finding goes in the MOD, not in
  a note — the mod rung outranks the lowest-rung rule.** `postmortem.md`
  step 5 said "land the outputs at the lowest rung that removes the cause"
  while this log's 2026-08-31 entry said "deterministic goes in the mod; the
  playbook carries judgement", and the two disagree whenever a computable
  response would ALSO be served by a checklist line or a prose lesson —
  which is most of them. Evan resolved it for DESIGN: notes get ignored. So
  the lowest rung is the floor for JUDGEMENT findings only; where every
  branch of the response is computable from state the observers already
  publish, the output is a mod procedure plus its spec issue, whatever
  cheaper rung was available. The reasoning is the one this repo has already
  paid for twice: a checklist line is executed only if the session reads it,
  remembers it, and is not busy — M1 day 4 missed three daily items while
  two colonists were bleeding out, which is exactly when the line was worth
  most. Mod code has no attention budget. The cost is real and accepted:
  "changing it needs a rebuild" is the rigour, not the objection.
  `postmortem.md` step 3's table now carries the override, step 5 states it,
  and the ladder's mod rung is marked MANDATORY rather than available.
- 2026-09-01 (session 13) — **`autostart.rws` stays PARKED while `--quicktest`
  is the bench fixture; `--quicktest` is not retired.** The two cannot coexist:
  `Root_Entry` and `Root_Play` race on `Root.checkedAutostartSaveFile` with a
  scene-targeted long event, the autostart load wins, the quicktest lambda then
  finds `Current.Game != null` and skips, and map generation fails. It is
  DETERMINISTIC, not flaky — it cost the M1 run two launches before anyone knew
  why (`RUNS/m1-20260831/mapgen-failure-{1,2}.Player.log`,
  `[[quicktest-and-autostart-collide]]`, git-bug `c8c0199`). The alternative was
  real and was rejected on cost, not on principle: making the save the fixture
  would be MORE reproducible — a fixed map rather than a fresh random tile, and
  4.3's temperate requirement satisfied by construction instead of by draw — but
  every suite in `accept/` is written against a fresh `--quicktest` map, so each
  would have to be re-checked against a fixed one before the switch could land.
  Nothing in the M1 run argues for paying that now. Mechanised rather than
  written down, per the entry above: `profile/run-agent.sh` and `run-agent.ps1`
  now REFUSE to launch `--quicktest` while `Saves/autostart.rws` exists, naming
  the `mv` that fixes it. Refusing rather than warning, because the launch cannot
  succeed and a warning scrolls past inside two minutes of boot log. That
  refusal is what stops a future session recreating the collision by tidying the
  save directory.
- 2026-09-01 (session 13) — **Siting is a survey of 2–3x the footprint, the
  survey is structured, the ASCII crop rides with it, and the PNG stays
  CLI-side.** Live bench `20260901T121508`: `find-rect {w:3,h:2}` approved a
  box that the game then refused for granite on the interaction cell one row
  south of it — `SpatialVerbs.CheckRect` walks only the box's own cells and
  knows no def, while `GenConstruct.InteractionCellStandable` (run by both
  `GenSpawn.CanSpawnAt` and `GenConstruct.CanPlaceBlueprintAt`) refuses any
  non-Standable thing on the cell `ThingUtility.InteractionCellsWhenAt` names.
  Evan's requirement, verbatim: "the agent looks at an area 2-3x bigger than
  whatever they're building." So the read is `site-survey` (`c718e4a`), three
  tiers — footprint (the game's gate), interaction cell (standable, exclusive
  against other interaction cells per `PlaceWorker_PreventInteractionSpotOverlap`,
  tolerates a chair), margin (computed facts, not a gate, over the footprint
  expanded by `max(3, max(w,h))` per side). The picture question — structured,
  ASCII, or the 2.5 PNG — was a false choice: the structured survey is the
  CONTRACT (verbs act on it, suites prove it by shape); the ASCII crop is
  embedded in the same result because the agent reads it mid-loop with no
  round trip and in `map-view`'s alphabet; the PNG keeps 2.5's invariant ("no
  game-side image rendering; a pure function of dump + catalog") and gains a
  CLI-side overlay input that is the survey's tiers. One source of truth,
  three renderings, no image code in the mod. Two conventions pinned by the
  same finding: `find-rect`'s `at` is a rect CORNER while placement is about
  `GenAdj.OccupiedRect`'s CENTRE, and for even sizes `CellRect.CenterCell`
  (`min + size/2`) and `OccupiedRect` (`center - (size-1)/2`) disagree by one
  cell — so every siting read publishes the game's `pos`, computed the game's
  way, and a caller never derives it. And the shared gate routine is ONE
  routine: `CanPlaceBlueprintAt(godMode:false)` plus `Designator_Build.Visible`'s
  clauses through `WorldSafe.Finished`, consumed by `site-survey`,
  `find-rect {def}`, `dev:spawn-thing {buildable:true}`, `site-audit` and
  3.3's `build`/`place-layout` — the acceptance asserts the survey's verdict
  and the build verb's refusal are the same sentence.
- 2026-09-01 (session 13) — **Dev staging keeps the god-hand by default;
  `buildable:true` is opt-in, and a staged base proves itself with
  `site-audit`.** `GenSpawn.CanSpawnAt` runs no PlaceWorker — those live only
  in `CanPlaceBlueprintAt`'s closing loop — and `dev:spawn-thing` passes
  `canWipeEdifices:true` then spawns `WipeMode.VanishOrMoveAside`, whose
  `WipeExistingThings` destroys walls, rock and buildings in the footprint
  with `DestroyMode.Vanish` and no journal line. So every dev-staged building
  is an untested claim about what 3.3's blueprint path could produce, and M1's
  fixture was one. Evan chose opt-in over flipping the default (2026-09-01):
  the god-hand is what `dev:*` is FOR and the existing suites stage with it;
  what changes is that (a) `buildable:true` runs the real gate and refuses
  with the game's sentence, (b) the god-hand path reports `wiped[]` so an
  erased wall is never silent even when intended, and (c) `site-audit`
  re-runs the validator over every player building in a rect, which is how
  "instant ≡ built-blueprint" on 3.3 becomes a provable bullet instead of an
  undefined diff. `3a5ff6c`.
- 2026-09-01 (session 14) — **A build's identity is its placement id, because
  completion is an ABSENCE.** A finished build leaves no blueprint and no
  frame: `Blueprint.TryReplaceWithSolidThing` turns the blueprint into a
  `Frame` (`Blueprint_Build.MakeSolidThing`, which also calls
  `Map.enrouteManager.SendReservations`), and `Frame.CompleteConstruction`
  turns the frame into the building and destroys itself. So any read that only
  enumerates live blueprints and frames reports "finished" and "cancelled"
  identically, as nothing — and an agent that asked for a wall cannot tell
  that it got one from the fact that something deconstructed it. 3.3's "every
  placement journaled with a placement id" was written as bookkeeping; it is
  in fact the only handle the completion answer can hang on, and it is
  promoted here to an invariant: **a placement verb publishes an id, and the
  construction read answers `blueprint | frame | built | cancelled` for that
  id, by field and never by parsing a sentence.** The two transitions are
  journaled as positive events via Harmony postfixes on
  `Frame.CompleteConstruction` and `Frame.FailConstruction`, in
  `JournalHooks.cs`'s existing read-only idiom, rather than inferred from two
  absences. `d7c8088`, `1adc737`.
- 2026-09-01 (session 14) — **An issue that cites a path under the bench's
  protocol root cites a file with a lifetime shorter than the issue.** Three
  open issues (`8b4839f`, `c718e4a`, `3a5ff6c`) named response envelopes
  (`results/accs13-026-devspawnthing.json` and four more) and journal seqs as
  their evidence. None of it was in the repo; it lived only in
  `_RimWorld-Agent`'s protocol root, which is the working directory of
  whatever bench runs next — and the round those issues exist to drive
  launches benches repeatedly. What the repo did hold was
  `transcripts/<sid>/`, which carries a `cmd.json` per call and **no
  responses**, so the claims were banked on the ask side and nowhere on the
  answer side. A transcript proves what was asked; it does not prove what the
  game answered, and the answers are the findings. **Bank the envelope beside
  the claim** — `accept/runs/<session>/results/` — at the time the issue is
  filed, not at the time someone needs it.
- 2026-09-01 (session 14) — **Row 0 is NORTH, and a rotation suffix is the
  `Rot4` value verbatim, not a description of which way a thing faces.**
  `templates/INDEX.md` had carried "Row 0 = north is PROPOSED, not established
  — 3.3 pins it" since session 10, and 3.3 had not reached it. Pinned here
  instead, and moved off 3.3 deliberately: it is a decision about `templates/`
  and `baseviz/`, neither of which is C#, and the mod holds no opinion on IR
  orientation until `place-layout` exists. North-up was already the
  convention in every artifact that had one — `render.py` ("row 0 is
  z = oz + h - 1"), `CropRenderer` ("north at top"), `map-dump`'s
  `north_up: true` — and only `ir.py` was silent, saying "the top row as
  written in the XML", which is an XML-order statement that takes no compass
  position. **The pin's value was not the orientation; it was what checking
  the corpus against it turned up.** `freezer-kitchen` carried
  `FueledStove_South`, and `FueledStove` is `interactionCellOffset (0,0,-1)`,
  so `Thing.InteractionCell = Position + offset.RotatedBy(Rotation)` puts the
  cook one cell NORTH of a `Rot4.South` stove — row 0, a wall. The stove was
  unusable as drawn, and the same answer falls out whether the IR token is
  read as a corner or as a centre. The cause was the dialect, not the
  template: the suffix was documented as "the direction the def FACES", which
  means opposite things for a cooler (vents toward its rotation) and a
  workbench (used from the side OPPOSITE its rotation), so one gloss produced
  two conventions and the corpus happened to exercise both. A `Rot4` value has
  one meaning and round-trips through `map-dump`; a facing is prose and
  belongs in the template's .md. Same rule as `00a1be7` and as the
  `ToStringHuman` → `ToStringWord` fix landed the same day: read by field,
  never through a description. `FueledStove` is the only
  interaction-cell-bearing rotated token in the corpus (`Bed`, `Battery`,
  `Cooler`, `TorchLamp`, `WoodFiredGenerator`, `FirefoamPopper` all leave
  `hasInteractionCell` unset), so that audit is complete rather than a sample.
  `bac4eba`, `1adc737`.
- 2026-09-01 (session 15) — **A refusal names the cell that refused, and
  `GenSpawn.CanSpawnAt` has SEVEN branches, not six.** `DevVerbs.WhyNoSpawn`
  returned a bare sentence, and its caller then asked `Blockers.At` about the
  cell it had ASKED for — different cells whenever the refusal is off-footprint,
  which is the whole of `8b4839f`. It now returns `{tier, cell, thing, reason}`
  and every failure row carries `cell` (the refusing cell) and `cell_role` (the
  tier) beside the unchanged `at` (the caller's own argument echoed back).
  Re-walking `Verse/GenSpawn.CanSpawnAt` member by member to write the tiers
  turned up a branch the mod never reproduced:
  **`GenConstruct.NotBlockingAnyInteractionCells`** runs there as well as inside
  `CanPlaceBlueprintAt`, so a placement refused for covering a NEIGHBOUR's
  interaction cell was reported as `ThingDef.CanSpawnAt refused` — the wrong
  branch, and no cell at all. That is the same defect as `8b4839f` one level up:
  a refusal describing something other than what refused. Seven tiers rather
  than the four the issue named, because the two extra ones are different
  REMEDIES: `blocks-interaction` is cleared by moving our own building and never
  by clearing the cell it names, `def` names no cell, and `place-search` is the
  honest answer when `CanSpawnAt` accepted the target and `GenPlace` still found
  nowhere. `8b4839f`.
- 2026-09-01 (session 15) — **`rot` is a first-class argument, and the default
  is the verb's OWN model rather than one project-wide answer.**
  `dev:spawn-thing` had no `rot` at all; it passed whatever `ThingMaker.MakeThing`
  left, which is `Thing.rotationInt`'s field initialiser, i.e. always North. Its
  default stays North — the verb models
  `Verse/DebugThingPlaceHelper.DebugSpawn`, which reaches the
  `GenSpawn.Spawn(def, c, map, wipeMode)` overload that hard-codes `Rot4.North`
  — while the siting reads (`site-survey`, `find-rect {def}`) default to
  `def.defaultPlacingRot`, because they model `Designator_Build`, which starts
  there. **76 vanilla defs set `defaultPlacingRot` to something other than
  North**, so unifying the two would silently re-face every building every
  shipped suite stages, and would do it without a diff to show for it. Two
  verbs, two models, each citing its own in a comment. The vocabulary is one
  vocabulary though: `Rot4.ToStringWord`'s four words or the bare 0..3, the same
  token `map-dump` publishes and `templates/INDEX.md` pinned — and NOT
  `Verse/Rot4.FromString`, which `Log.Error`s on an unrecognised string and so
  would let an agent's typo raise a red error, the same trap
  `ListerThings.ThingsOfDef` sets for MinifiedThing. The corner-to-centre
  inverse lands with it (`Footprint.TryCentreFor`) and is CHECKED against
  `GenAdj.OccupiedRect` on every call rather than trusted, because a one-cell
  placement slide is cumulative on a module grid (`bac4eba`) and a wrong inverse
  is invisible until it is. `8b4839f`, `c718e4a`.
- 2026-09-01 (session 15) — **`Designator_Build.Visible` has TEN clauses, and
  every count on record was low.** `1adc737` amendment #1 named two (research,
  `maxTechLevelToBuild`); its own verification comment #4 corrected that to six
  and titled the correction "amendment #1's gate list is one clause of six". The
  1.6 member has ten: godMode, `minTechLevelToBuild`, `maxTechLevelToBuild`,
  `IsResearchFinished`, `minMonolithLevel` (Anomaly), `difficulty.AllowedToBuild`,
  the `PlaceWorkers` × `IsBuildDesignatorVisible` loop, **`buildingPrerequisites`
  via `ListerBuildings.ColonistsHaveBuilding`**, **`discoveryPrerequisites` via
  `HiddenItemsManager.Hidden`**, and **`requireInspectedGravEngine` (Odyssey)**.
  The last three are named nowhere in the issue record, and they are the same
  class of gap the amendment was arguing about: a verb that reproduces only the
  research clause is a god-hand for prerequisite- and DLC-restricted defs. All
  ten are now in `SiteGate.Selectable`, each as a TOKEN an agent can branch on
  (`min-tech`, `research`, `building-prerequisite`, …) with the prose in a
  separate `detail` field — read by field, never through a description.
  Two rulings that came out of writing it:
  **the godMode clause is published and NOT honoured.** Vanilla's first line is
  `if (DebugSettings.godMode) return true;`, and reproducing it would turn every
  player verb into a god-hand the moment a dev session left the flag on, with
  `ok:true` to show for it. The clauses are evaluated regardless; `god_mode_on`
  rides in the envelope as a fact about the session. A caller that wants the
  bypass asks a `dev:*` verb, which says so itself.
  And **an unreadable research route REFUSES rather than shrugging.**
  `WorldSafe.Finished` returns "unfinished" for everything if its field ref
  failed, so a shrug would silently refuse every buildable while looking like a
  research answer; the clause `research-unreadable` says which of the two
  happened. That is `PawnSafe.Policies`'s `source` discipline applied to a gate:
  "not researched" and "we could not look" must never read alike. `c718e4a`,
  `1adc737`.
- 2026-09-01 (session 15) — **`site-survey`'s four resolutions, all of them
  about not letting one field mean two things.** (1) **`pos` and `at` are
  mutually exclusive and passing both is `bad-args`.** They are different
  conventions — the game's placement centre versus the footprint's south-west
  corner — and for an even-sized def they name different cells, so accepting
  both and preferring one would reproduce exactly the bench failure the verb
  exists to prevent. `pos_source` rides in the envelope either way, which is
  `7382bdd`'s narrow fix arriving here on its own merits. (2) **The overlay is a
  SECOND grid, not extra glyphs in the crop.** Mixing tier markers into
  `map-view`'s grid would change what a char means, which by `CropRenderer`'s
  own rule obliges a bump of `map-view/ascii-1` and of `map-dump`'s
  `distinct_from` — the identity `accept/e6faa51-channel-alphabet.py` enforces.
  Two grids over one origin/w/h cost nothing and keep both alphabets true:
  `site-survey/overlay-1` says which TIER a cell is in and nothing about what is
  on the ground. (3) **The margin tier publishes rows for NOTABLE cells and
  tallies the rest.** All three tiers keep one row shape, so a consumer written
  for the footprint reads the margin — but a 3x survey of an 11x6 def is 594
  margin cells, and 594 rows of "fine" is not a read an agent can use mid-loop.
  Rows for what is fogged, unstandable, roofed or a door (capped at 40, with
  `more`); counts for everything; and the picture carries the whole ring, which
  is what the picture is for. (4) **The two gate tiers test fog and the
  identical-thing scan PER CELL where vanilla tests only the centre.** Strictly
  more informative, and it cannot contradict the verdict, because the verdict is
  `CanPlaceBlueprintAt`'s own answer published verbatim beside the rows rather
  than re-derived from them. Said out loud in the code because a reader
  comparing the two will notice the difference. `c718e4a`.
- 2026-09-01 (session 15) — **`find-rect {def}` is a second verb behind one
  name, and the size path is untouched by construction.** `c718e4a`'s acceptance
  demands `find-rect {w,h}` output be what it always was, byte for byte, and the
  cheapest proof of that is a diff with zero deletions: `def` branches at the
  top into its own routine and nothing below it moved. Six resolutions came out
  of writing it. (1) **One candidate per cell — the first rotation in
  `def.defaultPlacingRot`-first order that the gate accepts, not the cross
  product.** Four rotations of one cell are one site as far as "where can this
  go" is concerned, and the cross product spends `max` four times on the same
  ground; a caller that wants a specific facing pins `rot`, and `rot_order` is
  published so the choice is legible. (2) **`center` is DROPPED in def mode
  rather than relabelled.** `CellRect.CenterCell` is not the placement centre of
  an even-sized rect, and a field that looks like the value to pass and is off by
  one exactly where it matters is the bench failure this verb was fixed for.
  `pos` is the argument; `at`/`w`/`h` are the identity. (3) **`dist` is measured
  to `pos`, which is also the key the ring walk terminates on.** 2.6 blocker 1
  was terminating on one key and sorting by another, which selects the wrong set
  rather than merely ordering it badly; def mode keeps the two identical and says
  so where the break is. The rotation rank is the tie-break, never a sort key.
  (4) **`require` narrows the def gate and never replaces it.** `buildable` and
  `walkable` are REFUSED as subsumed — refusing beats accepting-and-ignoring,
  which is `7382bdd`'s whole failure mode — while `roofed`/`unroofed` are honest
  extra filters. And **`reachable-from:P` changes meaning, which is the fix for
  `c718e4a`'s third complaint**: in size mode it tests the rect's CENTRE cell,
  which for a workbench is a cell no pawn ever stands on; with a def in hand it
  tests the INTERACTION cells, falling back to touching the footprint for a def
  that has none. The echoed `require` block says which question was answered.
  (5) **`Designator_Build.Visible` is asked ONCE, before the walk**, because it
  is def-level; an unselectable def returns no candidates anywhere and saying so
  in a `selectable` block is a different fact from "no room". It also saves
  thousands of `CanPlaceBlueprintAt` calls, which is why the def path's examine
  cap is 2000 against the size path's 6000 and why `gate_calls` is published.
  (6) **No per-cell blocker tally in def mode.** The refusing cell of a rejected
  candidate is frequently not the cell that was walked — `8b4839f` is exactly
  that mistake once — and describing the walked cell for thousands of rejects is
  that defect at scale. `rejected` tallies the game's own sentences, `refusals`
  carries a few worked examples, and one `site-survey` on a chosen candidate
  names the cells for real. `c718e4a`, `8b4839f`.
- 2026-09-01 (session 15) — **The per-verb argument whitelist was ATTEMPTED and
  REJECTED ON MEASUREMENT; `7382bdd` ships its narrow half, and the measurement
  is the finding.** `7382bdd` comment #1 pre-authorized either shape and named
  the fallback trigger: "a verb that forwards args to a sub-verb ... is the
  whitelist being too invasive, and one such case is enough to take the fallback
  for the whole change". The audit found not one case but a structural one.
  Measured against the shipped tree and the shipped suites, statically, because
  a worker never launches a bench:
  **120 registered verbs. 22 handlers read no argument at their own call sites**
  — some take none, the rest forward `ctx.Args` wholesale into a helper
  (`advance` into `TimeDriver.Start`, `bill-set` and `bill-remove` into
  `BillVerbs`' helpers, `draft`/`undraft`/`clear-priority-work` into
  `PawnOrderVerbs`', and fifteen more). **88 (verb, key) pairs that the shipped
  suites actually send, across 53 verbs, are read through shared helpers rather
  than at the handler** — `pawns`, `pawn`, `thing`, `target`, `bench`, `ticks`,
  `max_tps`, `letter`, `quest`, `kind`, `rect`, `targets` and the rest, every one
  legitimate. **And five suite call sites build their argument dict at RUNTIME**
  (`advance`, `designate`, `quest-accept`, `surgery-add`, `things`), so a
  whitelist cannot be validated statically at all.
  So a declared arg set could be neither derived from the code nor checked
  against it — it would be a second source of truth over 120 verbs, and this
  repo has a worked example of what that costs: the `Build:` tally in the
  workspace CLAUDE.md went stale three times in one day and misled two review
  agents. Worse, its drift mode is asymmetric in the wrong direction: a missing
  declaration REFUSES a legitimate call, mid-run, on a live bench. Shipping that
  unmeasured against a ten-day run is not a trade this project makes.
  **What ships instead is the narrow fix, sharpened.** `Dev.PosArg` publishes
  `pos_source` (`arg` | `anchor-default` | `stockpile`) into both the envelope
  and the journal row for `dev:spawn-thing` and `dev:spawn-pawn` — so the
  fallback is never silent even when it was intended — and it REFUSES a near
  miss rather than defaulting past it: `at`, `cell`, `position`, `loc`,
  `location` and `where` are read by nothing that defaults a cell (audited over
  all six `PosArg` call sites and over `StarterKit`'s and `world-fixture`'s
  forwarded dicts, which pass only `pos` and `around`), so one of them arriving
  is unambiguously a caller mistake. That satisfies the issue's own worked
  example — `at` -> `pos` on `dev:spawn-thing`, which is the call that produced
  three wrong results in a row on bench 20260901T121508 — at the entry point of
  the one argument whose default is dangerous, with the check exposed as
  `VerbArgs.NearMiss(key, aliases)` so any verb with a defaulted positional
  adopts it in one line. **Aliases are supplied by the call site and never
  guessed globally**: a list that overlapped a real argument name elsewhere
  would refuse a CORRECT call, which is a worse bug than the one being fixed —
  and `site-survey` reading both `pos` and `at` legitimately is the live proof
  that such overlaps exist. `7382bdd`.
- 2026-09-01 (session 16) — **`buildable:true` is EXACT-OR-REFUSE and
  buildings-only, and the erasure it replaces is now on the record.**
  `dev:spawn-thing {buildable:true}` calls `SiteGate` and honours both halves,
  which is what the acceptance's unresearched-def bullet asks for. Two things
  fell out of wiring it. (1) **`mode:"near"` is REFUSED under
  `buildable:true`.** `GenPlace.TryPlaceThing`'s radial search gates every
  candidate on `GenSpawn.CanSpawnAt` and can land the building on a cell the
  blueprint gate never saw, so the envelope would carry a verdict about one cell
  and a building on another — `acee526`'s whole subject, reintroduced by the verb
  that exists to close it. `buildable` implies `mode:"direct"`; passing
  `mode:"near"` with it is `bad-args`. (2) **`canWipeEdifices` is not "not
  passed" on the buildable path, it does not EXIST there.** It is a parameter of
  `GenSpawn.CanSpawnAt`, and the buildable path never calls that member;
  occupancy is decided per occupant by `GenConstruct.CanPlaceBlueprintOver`,
  which is the rule a colonist's blueprint is held to. The issue's wording ("does
  not pass it") reads as a flag being withheld; there is no flag.
  And `wiped[]` is **PREDICTED, THEN CONFIRMED** (`WipeWatch`): candidates from
  `GenSpawn.SpawningWipes(newDef, oldDef)` called exactly as
  `WipeExistingThings` calls it, rows built BEFORE the spawn because
  `Blockers.Describe` reads `PositionHeld`, and a row published only for a
  candidate that is `Destroyed` afterwards. Prediction alone would be a second
  implementation of the game's rule; confirmation alone cannot name what went.
  It also makes `VanishOrMoveAside`'s first half honest — `CheckMoveItemsAside`
  RELOCATES items, so a moved item is not reported wiped and one whose
  relocation failed is. The list is on the RESULT and in the JOURNAL ROW, which
  is M1 finding A's lesson (the response is thrown away, the row is forever).
  `3a5ff6c`.
- 2026-09-01 (session 16) — **`dev:starter-kit` needed one call per building
  unit, not `mode:"direct"`, and `mode:"direct"` alone would have been a
  REGRESSION.** `3a5ff6c` item 3 asks the kit to pass `mode:"direct"` for
  buildings, to fix M1's `placed: 0` research bench (the kit passed no `mode`, so
  a building took `ThingPlaceMode.Near` and reported nothing when the radial
  search came back full). Done literally, it breaks the `medical` preset:
  `count: 2` runs `dev:spawn-thing`'s stack loop twice against ONE target cell,
  and `GenSpawn.SpawningWipes(Bed, Bed)` is true for two edifices, so the second
  bed VANISHES the first while the envelope still says `placed: 2`. Near was
  hiding that by scattering. So the kit issues one `dev:spawn-thing` per unit
  with its own `pos`, chosen by a ring walk from the anchor that skips any cell
  an earlier unit's `OccupiedRect` already claimed and asks the SAME gate the
  caller asked for (`SiteGate` when `buildable:true`, `CanSpawnAt` otherwise —
  choosing against a different predicate than the spawn will use would make the
  kit report refusals it had chosen itself). When the walk finds nothing it hands
  the anchor over anyway and lets the verb refuse it: the game's own sentence,
  the refusing cell and a journal row beat anything the kit could invent, and a
  silent `placed: 0` is precisely M1 finding A. Items are untouched — `Near` is
  right for a stack that merges into storage. `3a5ff6c`.
- 2026-09-01 (session 16) — **The two fixture spawn routes honour the GROUND half
  of the gate and REPORT the widget half; `dev:spawn-thing {buildable:true}`
  honours both.** `3a5ff6c` item 3 says `world-fixture`'s `bench` step and
  `JournalVerbs.SpawnPlayerBuilding` adopt "the same helper with `buildable:true`
  semantics". Taken as both halves it deletes a working fixture to enforce a rule
  about a menu: `journal-selftest`'s `power` step lays `PowerConduit`, which
  carries a research prerequisite of Electricity, and it runs on FRESH quicktest
  maps — that grid is what `digest.power` is tested against. So `FixtureSite`
  refuses on `GenConstruct.CanPlaceBlueprintAt`, which is about the ground and is
  the hole the issue is actually about (both routes previously had NO validator
  at all — not even the `CanSpawnAt` `dev:spawn-thing` uses, so the bench step
  could stage a butcher table whose interaction cell was in a wall), and
  publishes `Designator_Build.Visible`'s answer as `selectable` on every call
  without acting on it. The asymmetry is the same one `SiteGate`'s header argues
  for: "the ground refuses this" and "this is not on the architect menu" are
  different news, and only the first is a claim about buildability.
  `dev:spawn-thing {buildable:true}` keeps both halves because its own acceptance
  names the unresearched-def refusal and because it is a verb an agent calls
  rather than a fixture's private helper. `3a5ff6c`.
- 2026-09-01 (session 16) — **`site-audit` reports two numbers because a hit and
  an unselectable are different findings, and it carries its own disclaimer in
  the envelope.** `hits` is `CanPlaceBlueprintAt`'s refusals with the building
  itself passed as BOTH `thingToIgnore` and `thing` — the pair
  `Designator_Install.CanDesignateCell` passes for a reinstall, and without it
  every building refuses itself with `IdenticalThingExists`. `unselectable` is
  the widget half and is NOT a hit: an unresearched or now-forbidden building on
  a played map is ordinary, and folding it in would make the count useless. Fog
  is EXEMPT by default because `CanPlaceBlueprintAt`'s second clause is
  `center.Fogged(map)`, so every unexplored ancient ruin would otherwise be a
  hit; the flag and the skipped count are both published. `heuristic` rides in
  the result as a sentence — a real game reaches states the validator now refuses
  (a roof built over a solar panel later, an item hauled onto an interaction
  spot) — so a suite cannot quote `hit_count` without it. Frames are dropped by
  name: `ThingDef.IsBuildingArtificial` is `category == Building || IsFrame`, so
  `ThingRequestGroup.BuildingArtificial` includes them, and a build in progress
  belongs to `construction` rather than to an audit of what is standing.
  `3a5ff6c`.
- 2026-09-01 (session 16) — **`build` is `Designator_Build.DesignateSingleCell`
  minus four things, and the zero-work branch it KEEPS is the one that looks like
  a cheat.** Reproduced in the game's own order: destroy any Frame whose
  `replaceTags` intersect `def.blueprintDef.replaceTags` with
  `DestroyMode.Cancel`; then either the zero-work direct placement or
  `GenSpawn.WipeExistingThings(pos, rot, def.blueprintDef, map,
  DestroyMode.Deconstruct)` followed by `GenConstruct.PlaceBlueprintForBuild`;
  then `PlaceWorkers[i].PostPlace`. **Dropped on purpose:**
  `FleckMaker.ThrowMetaPuffs` (a particle effect), `TutorSystem.AllowAction` /
  `Notify_Event` (a UI session that does not exist here), and
  `PlayerKnowledgeDatabase.KnowledgeDemonstrated(BuildOrbitalTradeBeacon)` —
  which WRITES scribed knowledge state and is exactly the kind of write-on-act an
  agent's placement has no business doing. **Kept on purpose:** vanilla's
  `WorkToBuild == 0` branch, which places the finished thing rather than a
  blueprint. Its sibling disjunct `DebugSettings.godMode` is dropped (the
  session-15 ruling), and dropping the whole condition with it would have made
  `build` refuse to do what a player's click does — a blueprint needing no work
  is a blueprint no pawn is ever dispatched to. `mode` in the result is
  `blueprint` or `instant-zero-work` so the two never read alike.
  Two guards fell out of writing it. **`!def.BuildableByPlayer` is `bad-args`,
  and the first version of this entry got the reason wrong.** The guard was
  written believing `build {def:"Steel"}` would otherwise reach the zero-work
  branch and god-hand a steel stack. Measured instead of assumed: `WorkToBuild`'s
  `defaultBaseValue` is **1**, not 0 (Core `Defs/Stats/Stats_Building_Special.xml`),
  so a resource takes the BLUEPRINT branch and dies on a null `blueprintDef`.
  The guard's real value is therefore the SENTENCE, not extra coverage — and the
  predicate has a name in the game: `Verse/BuildableDef.BuildableByPlayer` is
  `designationCategory != null`, which is both the condition
  `DesignationCategoryDef.ResolveDesignators` uses to generate a
  `Designator_Build` and the condition
  `ThingDefGenerator_Buildings.ImpliedBlueprintAndFrameDefs` uses to generate a
  `blueprintDef`. So "no designator exists" and "no blueprintDef exists" are the
  same fact, and the verb says which one the caller tripped. Recorded with the
  correction visible rather than silently fixed, because an unmeasured
  defaultBaseValue is exactly the kind of claim this log has gone stale on
  before. And **`cleared` is a different field from
  `dev:spawn-thing`'s `wiped`, because the wipe MODE differs**: the blueprint
  path passes `DestroyMode.Deconstruct`, which REFUNDS, while the god-hand passes
  `Vanish`, which does not. One vocabulary for two behaviours would have made
  "the game took your wall apart and gave the steel back" indistinguishable from
  "your wall is gone". `1adc737`, `c718e4a`.
- 2026-09-01 (session 16) — **The placement registry is IN MEMORY and
  session-scoped, and that is the observer discipline rather than a shortcut.**
  `Placements` holds `{id, def, stuff, pos, rot, mapId, mode, journalSeq,
  completedTick, failures}` in a static table cleared by
  `Runtime.ResetForGameBoundary`. Nothing is scribed: a placement id is a handle
  for the agent that issued the placement, not a durable fact about the colony,
  and writing our own data into the save is the one mutation this mod's whole
  observer discipline exists to avoid. The DURABLE record is the journal — `build`
  writes an `action` row carrying the id and the two Frame transitions write
  `construction` rows carrying it — so a post-mortem reads the ndjson and a live
  agent reads the table. Clearing on a game boundary is the same bug class as 1.5
  blocker 2 one layer up: an id names a CELL ON A MAP, and after a load it would
  resolve against whatever colony loaded next. **`Answer` looks live first and at
  the recorded completion second, deliberately**, because `Frame.FailConstruction`
  puts the blueprint BACK — a placement whose frame failed is genuinely
  `blueprint` again, so failures are a COUNT beside the state and never a state.
  `cancelled` is the residual, reached only after both positive answers said no.
  `d7c8088`, `1adc737`.
- 2026-09-01 (session 16) — **`CostListAdjusted`'s `errorOnNullStuff:false` is a
  TRAP, and refusing to ask is the only safe guard.** `d7c8088` hazard 2 says
  reading material cost can emit a red error and asks the implementation to guard
  `stuffToUse == null` before asking. The obvious guard is the parameter vanilla
  already provides — `CostListCalculator.CostListAdjusted(BuildableDef, ThingDef,
  bool errorOnNullStuff = true)` — and it is worse than the error it silences.
  With `false` and a null stuff on a `MadeFromStuff` def the method falls through
  to `value.Add(new ThingDefCountClass(stuff, num))` with `stuff` NULL and then
  `cachedCosts.Add(key, value)` under the key `(entDef, null)`. Vanilla's own
  error path never inserts that key — it recurses into
  `(entDef, DefaultStuffFor(entDef))` and returns — so passing `false` converts a
  red error into a process-lifetime poisoned cache entry holding a
  `ThingDefCountClass` with a null `thingDef`, which the next vanilla call for
  that key consumes instead of taking its error branch. An observer mutating a
  shared game cache is strictly worse than an observer logging. So
  `Placements.Materials` **does not call it at all** for that pair: `materials` is
  null and `materials_note` says why. Every path in this mod resolves stuff
  before placing (`SiteVerbs.ResolveStuff` -> `GenStuff.DefaultStuffFor`), so the
  case is reachable only for a blueprint someone else made — which is precisely
  the case the acceptance bullet stages. The cache ITSELF is accepted, same
  ruling as `BuildableDef.PlaceWorkers`: def-level, keyed on `(entDef, stuff)`,
  never scribed, reset when `Find.Storyteller.difficulty` changes, and filled by
  any hover over the architect menu. `d7c8088`.
- 2026-09-01 (session 16) — **`construction`'s state precedence is
  `blocked > in-progress > awaiting-materials > ready`, and the issue's listing
  order is not it.** `d7c8088` names the four states in the order
  awaiting-materials / ready / in-progress / blocked and says the value "must be
  computed, not guessed", but a blueprint can satisfy several at once and nothing
  said which wins. Resolved: `blocked` first, because nothing can proceed while
  something is in the way and the remedy is different from every other state's;
  then `in-progress`, because a pawn already on the job outranks a materials
  shortfall (the pawn is usually fetching the materials, and reporting
  `awaiting-materials` would send an agent to solve a problem that is already
  being solved); then `awaiting-materials`; `ready` is the residual, i.e. stocked
  and nobody on it, which is the one state that means "give somebody the
  Construction work type". `d7c8088`.
- 2026-09-01 (session 16) — **A NAIVE `FirstBlockingThing` REPORTS THE COLONIST
  WHO IS BUILDING IT, and that is a fourth hazard `d7c8088` does not name.**
  `RimWorld/GenConstruct.BlocksConstruction` ends
  `if (t is Pawn pawn && !pawn.IsHiddenFromPlayer()) return true;`, and
  `FirstBlockingThing(constructible, pawnToIgnore)` is the member BOTH
  construction work givers call — passing the worker precisely so it is excluded.
  Called with null it names the builder as the blocker, so a build being worked
  on right now reads `blocked` and an agent goes to fix nothing. So the worker is
  identified FIRST (one pass over `AllPawnsSpawned` indexing both `targetA` and
  `targetB`, because `JobDriver_ConstructFinishFrame` puts the constructible in A
  while `WorkGiver_ConstructDeliverResources`' HaulToContainer puts it in B) and
  handed to the call; a Pawn that still comes back is PUBLISHED — it is a true
  fact about the cell — under `blocking_is_pawn`, and does not set the state.
  Same family as the three the issue does name: every one is a way for a READ to
  make a healthy colony look broken. `d7c8088`.
- 2026-09-01 (session 16) — **`Frame.ThingCountNeededWithEnroute` is not worth
  its exposure; `EnrouteManager.GetEnroute` is.** `d7c8088` hazard 2 leaves the
  choice open ("decide on-issue whether `WithEnroute` is worth the exposure or
  whether `enrouteManager.GetEnroute` should be read directly"). Decided: read the
  manager. The member's two `Log.Error` branches ARE the arithmetic — negative,
  and greater than could be needed — so calling it is asking the game to
  red-error on our behalf and then clamping the answer it already clamped.
  `Verse.AI/EnrouteManager.GetEnroute(IHaulEnroute, ThingDef, Pawn)` is a
  `TryGetValue` over a stored lookup with no insert, i.e. the same number that
  member subtracts, and the clamp is one `Math.Max`. Published as `enroute` and
  `still_wanted` per material so "we have no steel", "the steel is not in a
  stockpile" and "somebody is already carrying it" are three distinct answers —
  the last of which is the difference between a stalled build and a slow one.
  `missing[]` also carries `in_stockpiles` from `map.resourceCounter`, with
  DigestVerb's caveat: that counter walks SlotGroup haul destinations, so goods
  on unzoned ground read as ZERO. `d7c8088`.
- 2026-09-01 (session 16) — **The journal's `action` type shipped in spec 3.2 and
  was never documented, and five verbs had been writing it for four sessions.**
  Found while adding the `construction` row to `JOURNAL.md`'s Types table:
  `DesignationVerbs`, `AreaVerbs`, `PawnActs`, `StorageVerbs` and `ZoneVerbs` all
  call `Journal.Emit("action", …)` — the non-`dev` twin of the documented `dev`
  row, i.e. provenance for a state-mutating PLAYER action — and the table listed
  eleven types, none of them that one. A consumer written from the doc would have
  dropped every player mutation on the floor while looking complete. Both rows are
  in the table now. This is the same failure mode as the `Build:` tally in the
  workspace CLAUDE.md and as `templates/INDEX.md`'s unpinned orientation: a
  document that is authoritative by convention and updated by nobody in
  particular. `d7c8088`, `1adc737`.
- 2026-09-01 (session 16) — **`world-fixture` steps chain through a typed handle,
  and the audit is "every step in the switch" because the issue's own list was
  wrong by omission.** `0d9cbd7` named `bill` and asked for an audit of
  `stockpiles`, `growing`, `research` and `letter` — four steps that CREATE
  things and therefore have nothing to chain to — and omitted `open-letter`,
  which RESOLVES one and has all three tells of the same bug: first-match over a
  shared live list, an error string that ASSERTS the chaining ("run the `letter`
  step first"), and an id the earlier step already published and nobody
  consulted. Following the list literally would have closed the issue with the
  class still open in a second step, which is what its verification comment
  predicted. **Both resolvers are fixed; the audit is recorded in
  `FixtureChain`'s header, step by step, including why the ones that look like
  the same shape are not:** `forbid` genuinely is first-match over a live list
  (`HaulableEver` sorted by id) but no step creates a haulable, so there is
  nothing to chain to and it is the step to revisit if one is ever added; and
  `stockpiles`/`growing`/`fire` pick GROUND rather than an object, with
  `FindClearRect`/`ZoneOk` already excluding cells an earlier step used.
  Three resolutions came with it. (1) **`bench_source` / `letter_source` are
  published** — `arg` | `chained` | `first-on-map` — which is `Dev.PosArg`'s
  `pos_source` discipline (`7382bdd`) applied here, and is what would have made
  the original defect visible in the envelope rather than only in a hand
  comparison of two ids. An explicit arg still wins, so
  `accept/32b9e01-orders-makingfor.ps1`'s two-call workaround keeps working
  unchanged. (2) **`expect_bills` is KEPT and made true**, resolving the issue's
  unsatisfiable either/or: an independent hand-count is the whole point of the
  field, and deleting it would leave `bills` with nothing to be checked against.
  It is refreshed after the step loop, and so is **`expect_slots_free`, which is
  the same defect and which the acceptance does not name** — fixing only the
  named one would have left a bench holding two bills reporting fifteen free
  slots. (3) **A repeated step is legal and now says so**: `{steps:["bench",
  "bench"]}` really does spawn two tables, each block reports only the last, and
  a later step chains to the last; `repeated_steps` publishes the counts so the
  truncation is not silent. `0d9cbd7`.
- 2026-09-01 (session 16) — **The `bill` step's cap is REFUSED, not cleared, and
  its bill is built by `BillUtility.MakeNewBill`.** Two more findings from
  `0d9cbd7` comment #1, neither in the issue body. `RimWorld/BillStack.AddBill`
  is `bill.billStack = this; bills.Add(bill);` and validates nothing, while the
  game's UI stops the player at `BillStack.MaxCount` (15) — and this step adds
  TWO bills and never cleared, so eight calls pushed a stack past a cap no player
  can exceed. **Refusing beats clearing**: a fixture that clears destroys state
  the caller or an earlier step staged, and it would do it under a name ("bill")
  that says nothing about removal; `clear_bills:true` is the opt-in and the
  cleared count is published. And `new Bill_Production(recipe)` is replaced by
  `RimWorld/BillUtility.MakeNewBill`, which dispatches to
  `Bill_ProductionWithUft` (`UsesUnfinishedThing`), `Bill_ResurrectMech`
  (`mechResurrection`), `Bill_ProductionMech` (`gestationCycles > 0`) or
  `Bill_Autonomous` (`formingTicks > 0`) — so a caller-supplied `recipe` of any of
  those four was being staged as the WRONG RUNTIME TYPE, which is this issue's
  own "silently stages the wrong object" one level down. All four derive from
  `Bill_Production` (via `Bill_Mech : Bill_Autonomous` for the two mech ones), so
  the repeat-mode fields still apply; the cast is guarded anyway and
  `bill_class` is published. `0d9cbd7`.
- 2026-09-01 (session 16) — **A blueprint's stuff is NOT `Thing.Stuff`, and
  reading the wrong one made every wooden wall report no materials.** Caught in
  self-review before any bench saw it. `GenConstruct.PlaceBlueprintForBuild`
  calls `ThingMaker.MakeThing(sourceDef.blueprintDef)` with NO stuff and then
  assigns `blueprint_Build.stuffToUse`, so `Thing.Stuff` — the `stuffInt` field —
  is null on every `Blueprint_Build` while the real answer sits in a field of its
  own. A `construction` read keyed on `Thing.Stuff` therefore handed
  `Placements.Materials` a null stuff for a `MadeFromStuff` def, hit the
  refuse-to-ask guard, and published `materials: null` with a note about a hazard
  that was not the actual problem — the guard hid the bug it was protecting
  against. `IConstructible.EntityToBuildStuff()` is the accessor all three types
  implement (`stuffToUse`, `Frame`'s `base.Stuff`, the minified thing's stuff for
  an install) and, unlike its interface sibling `TotalMaterialCost()`, it calls no
  cost list and cannot log. **And that sibling turns out to be a FOURTH
  red-error route:** `Blueprint_Install.TotalMaterialCost()` opens with
  `Log.Error("Called MaterialsNeededTotal on a Blueprint_Install.")` before
  returning an empty list — so an install blueprint is the one case where reading
  the cost is both wrong (an install needs no materials) and loud. Handled by
  name. Three of the four red-error routes on this subject are now ones
  `d7c8088` did not list. `d7c8088`.
- 2026-09-01 (session 16) — **The tolerated occupant is LISTED, and `blocker` was
  never the field for it.** `accept/runs/s15-20260901/README.md` item 1: a chair
  on an interaction cell gave `{ok: true, blocker: null, standable: true}` and
  named no occupant, so "a chair is there, which is fine" and "the cell is empty"
  read identically — and that chair is exactly what the NEXT placement's
  `PlaceWorker_PreventInteractionSpotOverlap` trips on. Fixed additively:
  `occupants` on an ACCEPTED interaction row, in `Blockers.cs`'s shape plus
  `tolerated`, `category`, `passability` and `has_interaction_cell`. `blocker`
  stays null because nothing is blocking, every existing field keeps its meaning,
  and no alphabet id moves — so this is not the contract change it would have
  been to overload `blocker`. `has_interaction_cell` is the field that predicts
  the next refusal: an occupant with its own spot is what the overlap rule
  objects to and a `DiningChair` (which has none) is not. Filth, motes and
  attachments are excluded — they are not occupants in any sense a placement
  cares about. `c718e4a`.
- 2026-09-01 (session 16) — **`rwa` owns the IR dialect; `place-layout` takes a
  RESOLVED layout in one call.** The mod reads no IR — `File.ReadAllText`
  appears twice in the whole tree, for `config.json` and the journal — and it
  stays that way. `rwa place-layout <file.ir.json> --origin P` is the CLI
  surface; `baseviz/ir.py`, which already IS the dialect, expands it and sends
  ONE call carrying `{def, at, rot, stuff?}` per element. **Atomicity is why
  it is one call and not N `build` calls:** "preflight every cell, place
  nothing on any failure" cannot hold across N transactions, and a half-built
  room is exactly what the invariant exists to prevent. Same split as 2.5's PNG
  channel ("no game-side image rendering; a pure function of dump + catalog")
  and the same ground rule ("the file half of the bridge never touches Verse").
  One dialect, one parser: a C# reader beside `ir.py` is a guaranteed drift
  point, and `ir.py` already documents that XML→IR is lossy at the edges. And a
  file path the GAME must resolve is machine-dependent — on BORGES the client
  and the bench are different trees — while a resolved payload cannot mean two
  things. Plumbing exists on both ends: `rwa --args-json` carries arbitrary
  nested objects and `DesignateEngine` already accepts a `cells` list.
  `1adc737`.
- 2026-09-01 (session 17) — **`place-layout`'s `at` is the footprint's
  NORTH-WEST cell, which is deliberately NOT the corner `build --at` takes, and
  the divergence is published rather than hidden.** `build --at` and
  `find-rect`'s `at` are the SOUTH-WEST corner, because that is `[x,z,w,h]`'s own
  `x,z` and the only sane anchor for a rect. A layout element's anchor is fixed
  by the IR instead — `templates/INDEX.md` pin 1, "the token sits in the
  footprint's north-west cell; remaining cells are `.`" — and it HAS to be,
  because converting north-west to south-west needs the def's ROTATED size
  (`Footprint.RotatedSize`, i.e. the game's `AdjustForRotation` axis swap), which
  is exactly the knowledge the client half is not allowed to have. Two anchors in
  one mod is the bug class this project keeps writing essays about, so the answer
  is not to pick one but to make both unmistakable: every placement publishes
  `at` (as given), `pos` (the game's centre) and `footprint` (`[x,z,w,h]`, whose
  `x,z` is the south-west corner), and the envelope carries `anchor:
  "north-west"` plus an `anchor_note` naming all three. The conversion itself is
  one line over `Siting.cs`'s existing map and is never a second copy —
  `Footprint.TryCentreFor` verifies its own round trip against
  `GenAdj.OccupiedRect`, so a failure is a refusal rather than a slide. The CLI's
  `--origin` is the LAYOUT's south-west corner, matching `find-rect`, so
  surveying a 5x7 and placing a 5x7 name the same ground. `1adc737`.
- 2026-09-01 (session 17) — **Build order inside a layout: terrain first, the
  rest in the caller's order, and we never stage.** `1adc737`'s open question,
  answered by reading the game's gates rather than by picking a pleasing order.
  Terrain goes first because `RimWorld/GenConstruct.CanPlaceBlueprintAt`'s
  occupancy loop carries a terrain-specific clause — `entDef is TerrainDef &&
  thing3.def.category == ThingCategory.Building && thing3.def
  .terrainAffordanceNeeded != null && !terrainDef3.affordances.Contains(…)`, with
  a `FoundationAt` escape — so a floor asked for UNDER a building it cannot
  support is refused, while the same building over the same floor is governed by
  `CanPlaceBlueprintOver`'s `CoexistsWithFloors` branch and is not. That is an
  ASYMMETRIC rule and floor-first is its safe half. **Everything else keeps the
  caller's order, because everything else is symmetric.**
  `CanPlaceBlueprintOver` lets a non-edifice go under an edifice
  (`canBuildNonEdificesUnder`, default true) and an edifice go over a non-edifice
  (`IsEdificeOverNonEdifice`), so conduit-then-wall and wall-then-conduit both
  pass — which matters, because `power-room`'s deliberate conduit sits in a wall
  cell. Two edifices in one cell fail both ways. And the two interaction rules
  refuse in BOTH directions: `InteractionCellStandable` refuses a bench whose
  spot already holds an unstandable blueprint ("InteractionSpotWillBeBlocked"),
  `NotBlockingAnyInteractionCells` refuses a wall that would cover a bench's spot
  ("WouldBlockInteractionSpot"). A layout that violates them is a broken layout,
  not a mis-ordered one, so no ordering rescues it and inventing one would only
  hide the defect. **The other half — "stage or trust the work givers" — is
  trust,** because there is no dependency ordering in construction work at all:
  `WorkGiver_ConstructDeliverResources` and `WorkGiver_ConstructFinishFrame` scan
  the player's blueprints and frames with no notion of walls-before-furniture, so
  "staging" could only mean WITHHOLDING blueprints until some condition we
  invented was met. That is a scheduler the game does not have, hidden inside a
  placement verb, and it would make the colony's own work surface a function of
  our bookkeeping. `1adc737`.
- 2026-09-01 (session 17) — **A preflight cannot see a layout blocking itself,
  so the transaction has a ROLLBACK — and the intra-layout interaction check is
  REPORTED, NEVER REFUSED.** Two halves of one problem. `place-layout` asks the
  game's gate about a map that does not yet contain the layout, so an element can
  be legal at preflight and refused at placement by an element placed before it.
  Self-OVERLAP is answered exactly and without the map, by
  `GenConstruct.CanPlaceBlueprintOver` over every intersecting pair in placement
  order — a pure function of two defs and two stuffs. Interaction-cell
  interference between our own elements is NOT refused, and that is the
  gate-lives-in-the-widget rule cutting the other way: vanilla's
  `NotBlockingAnyInteractionCells` only walks `GenAdj.CellsAdjacentCardinal`, so
  a diagonal arrangement the game accepts exists, and a stricter gate than the
  widget's is as wrong as a looser one. It is published as `self_conflicts`
  instead, naming both elements by index. **The gate that decides is asked again
  immediately before each element is placed**, against the map as it actually is
  by then; a late refusal with `partial:false` rolls the WHOLE call back —
  blueprints and frames through `DestroyMode.Cancel` (which refunds, and is
  `Designator_Cancel.DesignateThing`'s own mode), instant-mode things through
  `DestroyMode.Vanish` because this is an UNDO and not a deconstruction, terrain
  restored to the def read before the write. What cannot be undone says so:
  anything instant mode wiped is gone, and `rollback.incomplete` is the field.
  Without this, "no partial placement without --partial" would be true of the
  preflight and false of the call. `1adc737`.
- 2026-09-01 (session 17) — **Three more of `1adc737`'s open questions, all
  resolved by reading rather than by choosing.** (1) **Door-in-wall adjacency
  imposes no validation constraint, because there is no such rule.** `DoorBase`
  carries exactly one PlaceWorker, `PlaceWorker_DoorLearnOpeningSpeed`, and it
  overrides `PostPlace` ONLY — no `AllowsPlacing` — so `CanPlaceBlueprintAt`'s
  PlaceWorker loop imposes nothing on a door. A door is an edifice, so the only
  rule it meets in a wall layout is edifice-over-edifice in
  `CanPlaceBlueprintOver` (a door and a wall may not share a cell); adjacency is
  unconstrained and a free-standing door is legal. The layout's own geometry is
  the only thing that puts a door in a wall. (2) **The stuff-map is `defName ->
  stuff defName` with `*` as the default**, an element's own `stuff` beating both,
  and the invariant "no silent substitutes" is honoured by PUBLISHING rather than
  by refusing: `GenStuff.DefaultStuffFor` is what `Designator_Build`'s stuff
  dropdown opens on, and refusing it would make `place-layout
  templates/bedroom.ir.json` fail out of the box for a template whose own
  INDEX.md says material is bound at placement. So each placement carries
  `stuff_source` (`element` | `stuff_map` | `stuff_map:*` | `game-default`), the
  envelope carries `stuff_defaulted`, and `strict_stuff:true` refuses for a caller
  that wants it to. (3) **The roof grid is not consumed.** A roof is a
  DESIGNATION, not a placement; `area {kind:"build-roof"}` already ships gated on
  `Designator_AreaBuildRoof.CanDesignateCell`, and an enclosed, non-map-edge,
  non-fogged player room of ≤26 regions and ≤320 cells roofs itself via
  `AutoBuildRoofAreaSetter` — 49 cells for a 7x7 module, 35 for the 5x7
  rehearsal. Folding a second designator into this verb's transaction would make
  one call mean two things, so `rwa place-layout` REPORTS the roof grid and
  `--roof` sends the `area` call explicitly, as a second call, outside the
  transaction. `1adc737`.
- 2026-09-01 (session 17) — **Instant mode uses `WipeMode.VanishOrMoveAside`,
  and that choice is what makes "instant ≡ blueprint" true rather than nearly
  true.** `Verse/GenSpawn.Spawn`'s switch runs `CheckMoveItemsAside` before
  `WipeExistingThings` for that mode and not for `Vanish` — so a wood stack in
  the footprint is MOVED, which is what a colonist hauling it would have left,
  instead of destroyed. It matters because `CanPlaceBlueprintOver` lets a
  haulable through the preflight unconditionally (`oldDef.EverHaulable → true`),
  so a plain `Vanish` would silently destroy exactly the things blueprint mode
  hands to a hauler, and the two modes would genuinely diverge on any ground with
  loose items on it. `WipeWatch` publishes whatever was destroyed anyway, so
  "nothing was wiped" is a measurement and not an assumption. The equivalence
  claim itself is settled by `site-audit` over the layout's rect rather than by
  the issue's original "things-dump diff modulo construction byproducts", which
  its own verification comment showed was not a decidable predicate: a validator
  that accepts every placed building means the state is one blueprint mode could
  have produced. `1adc737`, `3a5ff6c`.
- 2026-09-01 (session 19) — **`advance until:{condition|layout}` ships, and the
  layout form is a NAMED FAMILY rather than a path — because the obvious path is
  the wrong predicate.** The natural spelling of "wait until the build is done"
  is `construction.frames == 0`, and on the run that met M2 that predicate was
  TRUE AT THREE SEPARATE MOMENTS: before `place-layout` was ever sent (empty
  map), for the ~900 ticks between placement and the first blueprint becoming a
  frame (a blueprint awaiting materials is not a frame), and again at the end,
  which is the only one meant. Same-tick evaluation therefore halts instantly on
  a room that does not exist, and **requiring an edge does not save it** — the
  middle case is a real false→true→false→true sequence and an edge detector
  halts on the wrong crossing. What the predicate needs is a scope that is
  MONOTONE: "every placement in `ly-1` is resolved", where resolved is built or
  cancelled and never goes back (`Placements.StateOf` carries the argument;
  `Frame.FailConstruction` interchanges blueprint and frame but never un-builds).
  The design consequence is the general one: **some predicates are not
  expressible as an operator over a digest path at all**, so the surface is two
  forms and not one. `fc287ba`, `36999fd`.
- 2026-09-01 (session 19) — **A `condition` requires an EDGE by default, and the
  clock is the case that makes it obvious.** `time.hour >= 6` is true all
  afternoon, so "advance until dawn" issued at 14:00 must not return instantly;
  the predicate has to be observed FALSE once before a true reading halts.
  `edge:false` is the "assert now" reading and is available and is not the
  default. The advance reports `true_when_armed`, so a caller that asked a
  question already answered can see that it did. The clock also settles Evan's
  "focus on time not ticks" (2026-09-01): `advance {ticks:N}` overshoots by up to
  `MaxTicksPerFrame(speed)` — 30 at Ultrafast — and nothing re-anchors, so the
  overshoots accumulate and an agent reasoning "20,000 ticks have passed so it
  must be morning" is wrong by an amount it never sees. A clock predicate cannot
  drift, because every evaluation re-reads the real clock. `fc287ba` #2.
- 2026-09-01 (session 19) — **Predicate evaluation builds ONE digest section,
  and the cadence is in FRAMES.** Frames because 1.8 deleted the per-frame tick
  budget (`Config`: "`advanceBudgetMs` … is GONE") and `TimeDriver.Step` is a
  per-frame poll site that already halts on three state facts —
  `WindowsForcePause`, `CurTimeSpeed == Paused`, the stall watchdog — so this is
  an addition to an existing site rather than a new category. One section
  because **a predicate is not one price**: `time` is nine field reads,
  `resources` walks the counted amounts calling `GetStatValueAbstract` per def,
  and anything under `colonists.list[*]` costs a `Room.Role` (a full room
  analysis) per colonist. Building the whole digest to answer a question about
  the clock would pay for all of it. Two floors are stated rather than enforced:
  `ResourceCounter.ResourceCounterTick` updates on `TicksGame % 204 == 0`, so no
  `resources.*` reading moves faster than every 204 ticks whatever the cadence
  says; and the halt can be one cadence window late by construction. Every
  advance publishes `until.eval_ms_per_frame` and `until.every_frames`, so both
  are measured rather than promised. `fc287ba` #1.
- 2026-09-01 (session 19) — **A path that does not resolve is a REFUSAL at arm
  time, and `until` takes exactly one matcher with no unknown keys.** The
  `until` parse was a `ContainsKey` else-if chain: a second matcher was silently
  outranked by whichever the method checked first, and a misspelled one was
  silently ignored — `until:{conditon:{…}}` would have armed nothing and
  presented as an advance that ran to its timeout for no stated reason. That is
  `7382bdd`'s class with ten in-game days attached, and it is the same shape as
  `construction --layout_id` answering whole-map. Both are closed here: the
  predicate is evaluated ONCE when it is armed, which validates the path and
  seeds the edge, and a broken path names the keys the section actually
  publishes. `fc287ba`, `36999fd`, `7382bdd`.
- 2026-09-01 (session 19) — **`short_by` comes from the BUILDER's availability
  test, not from the stockpile counter.** On the run that met M2,
  `place-layout` reported `short_by: 185` while 869 unforbidden WoodLog lay ten
  cells from the site and no stockpile zone existed anywhere on the map; the
  room was then built out of that "missing" wood. `MaterialBill`'s own header
  already said the number "cannot tell them apart" — and the code three lines
  below it drew a conclusion from it anyway, which is this project's "candidates
  + reasons, never bare booleans" rule broken at one remove: the agent got a
  verdict manufactured from a partial count, with the disclaimer in a source
  comment it cannot read. Vanilla's own test has no stockpile clause at all —
  `WorkGiver_ConstructDeliverResources.ResourceValidator` is def match,
  `IsForbidden(pawn)` and `PawnCanAutomaticallyHaulFast`, and `SlotGroup`,
  `haulDestination` and `resourceCounter` appear nowhere in that file. So
  `Materials.Of` counts reachable, unforbidden stacks by asking the colonists
  who could take the job, `in_stockpiles` stays beside it as the separate
  measurement it is, and a short row says WHICH problem it is —
  `forbidden`/`unreachable`/genuinely absent — because `unforbid` fixes one of
  them and not the others. `CanReserve` is deliberately not applied: a stack
  somebody is already hauling is not missing. `54b0c9a`.
- 2026-09-01 (session 20) — **The hediff cap gets a band ABOVE bleeding, keyed
  on `HediffDef.lethalSeverity`, because "bleeding first" ranked a death below
  every wound that caused it.** The M1 post-mortem said `BloodLoss` was cut by
  the 20-row cap; the round brief doubted it, on the grounds that
  `PawnSerializer.Rank` already bands bleeding first and shipped in `c85189d`
  the day BEFORE the run. The premise holds and the doubt is what is wrong.
  `Verse/Hediff.BleedRate` is `public virtual float BleedRate => 0f` and
  `BloodLoss` declares no `hediffClass`, so it is not a `Hediff_Injury` and
  **`Rank` can never return 4 for it** — a wound bleeds, blood loss is what
  bleeding produces. Captain's five reads (`transcripts/m1-20260831/`
  157/161/163/165/167, `hediffs_more` 7/16/18/19/19) each carry exactly twenty
  rows and every one is a bleeding `Bite` or `Scratch`: twenty rank-4 rows
  against a cap of 20. Nor would the Scope's own alternative — "life-threatening
  first" — have helped: `Hediffs_Global_Misc.xml` puts `lifeThreatening` on
  `BloodLoss`'s FIFTH stage (`minSeverity 0.60`) and he died at ~0.478, so he
  was rank 0, below `TendableNow` and below `Hediff_MissingPart`. The band that
  generalises is `Verse/Hediff.IsLethal` (`def.lethalSeverity > 0f` and
  `canBeThreateningToPart`) — the game's own name for "kills on its own clock",
  consumed at `Hediff.CauseDeathNow` whose debug line is "CauseOfDeath: lethal
  severity exceeded". True for `BloodLoss` from severity 0.01 rather than 0.60,
  and it picks up every disease and toxin with a lethal ceiling, none of which
  bleed either. An ADDED top band, not a re-sort: a bleeding wound still
  outranks a tended flu. `61794cd`.
- 2026-09-01 (session 20) — **`ticks_until_bleedout` is the GAME's number and is
  INDEPENDENT of the hediff list, and what happens when it expires is published
  beside it because it is not always death.** `Verse/HealthUtility
  .TicksUntilDeathDueToBloodLoss` is read rather than re-derived — the M1
  post-mortem back-solved that same formula by hand and it agreed with Captain's
  actual death to four ticks, which is the argument for taking it from the game
  rather than reimplementing it. It is deliberately not computed off the
  serialized `hediffs` list: `Verse/Hediff.Visible` returns
  `CurStage.becomeVisible` when `visible` is false and `BloodLoss` stage 0 sets
  `becomeVisible false`, so below severity 0.15 the row is legitimately absent
  and the clock must still be computable — and it is, because the estimator
  reads `BleedRateTotal` and `GetFirstHediffOfDef` directly and never consults
  `Visible`. `int.MaxValue` is published as `null`, never as 2147483647, which
  reads like a real deadline. **Three outcomes, from
  `Verse/Pawn_HealthTracker.ShouldBeDead` and the branch above it:** `none` when
  `hediffSet.HasPreventsDeath` (that method opens by returning false on it);
  `coma` for the Deathless gene WITH an intact brain, via
  `ShouldBeDeathrestingOrInComa` -> `RimWorld/SanguophageUtility
  .ShouldBeDeathrestingOrInComaInsteadOfDead`, whose real condition is
  `brain != null && !PartIsMissing(brain) && GetPartHealth(brain) > 0f`; `death`
  otherwise. The three brain reads are reproduced rather than calling that
  utility, because it opens with `if (!pawn.health.ShouldBeDead()) return
  false;` — it answers "is this pawn dying NOW" and the question here is the
  hypothetical "what happens WHEN the clock runs out". Note the Deathless gene
  alone does NOT appear in `ShouldBeDead`, so a Deathless pawn with a destroyed
  brain bleeds out like anyone else — while `RimWorld/HealthCardUtility` prints
  "(Deathless)" on the bare gene check and tells the player otherwise.
  `game_shows_clock` reproduces the WIDGET (that is what "matches the game's own
  readout" means for acceptance) and `outcome` is computed from the death path,
  and on exactly that pawn they disagree. `61794cd`.
- 2026-09-01 (session 20) — **Work-coverage floors come from the GAME's own
  list, plus exactly one stated deviation.** `Verse/WorkTypeDef
  .requireCapableColonist` is RimWorld's own "somebody must be able to do this",
  consumed by `Verse/StartingPawnUtility.WorkTypeRequirementsSatisfied`, which
  refuses to start a colony where a flagged type is disabled for every starting
  pawn. NINE vanilla types carry it — Firefighter, Warden, Construction,
  Growing, Mining, PlantCutting, Crafting, Hauling, Cleaning — plus Childcare
  from Biotech, making TEN. (This said "twelve" while enumerating ten from
  2026-09-01 until session 21 counted them in
  `Data/Core/Defs/WorkTypeDefs/WorkTypes.xml`. Twelve is what a bench with
  Biotech and a modded `Diplomat` actually REPORTS, because our own Doctor
  deviation is an eleventh row and `Diplomat` a twelfth — which is exactly why
  the wrong number looked confirmed by every read.) Their floor is 1 and it is a floor on CAPABILITY, because that
  is what the game checks; a modded type that sets the flag is picked up free.
  Note what that check is not: it runs ONCE, at world-gen, against the STARTING
  pawns, and never again — a colony that loses its only miner on day 40 is never
  told. **`Doctor.requireCapableColonist` is FALSE**: the game does not require
  a doctor to start a colony at all. Doctor's floor of 2 is the one deviation
  and it is not a house rule either — `RimWorld/Alert_NeedDoctor.Patients` tests
  `(item.Spawned || item.BrieflyDespawned()) && !item.Downed && item.workSettings
  != null && item.workSettings.WorkIsActive(Doctor)`, and **`!item.Downed` is in
  the game's own predicate**, so one doctor's coverage is zero the moment that
  doctor is the patient. That is the M1 death exactly. Doctor's floor is
  therefore on AVAILABILITY, not capability. The gap between `enabled` and
  `available` is the trap one level down: every vanilla Doctor work-giver except
  `VisitSickPawn` requires `Manipulation`, so a doctor with no hands has the
  work type on, undisabled, and cannot tend — reproduced from
  `RimWorld/WorkGiver.MissingRequiredCapacity` over the union of the type's
  work-givers, and read through `PawnCapacityUtility.CalculateCapacityLevel`
  rather than `capacities.CapableOf`, because `PawnCapacitiesHandler.GetLevel`
  lazily builds `cachedCapacityLevels` and observers do not write. `40ed42f`.
- 2026-09-01 (session 20) — **The roster repair is a VERB, not a hook, and the
  clock/travel boundary is settled: the clock belongs to the patient, the rescue
  belongs to the roster.** `40ed42f` reads as though a Harmony hook should
  repair Doctor coverage on a roster change. It is `work-cover` instead, for the
  reason session 13 settled on `threat-pardon`: **the decision must be a
  recorded ACT, not a silent exemption.** A mod that quietly reassigns work
  behind the agent produces a colony no transcript explains, and the next
  post-mortem cannot tell the agent's decision from the mod's. What makes it
  unforgettable instead is that `work_coverage` is in EVERY digest and that
  `advance` halts on an own-faction downing (`722c951`) — the agent is the
  trigger, and the agent is made to look. On the boundary `61794cd` asked to be
  decided: **the clock is a property of the PATIENT** (`pawn.health
  .ticks_until_bleedout` — one division, correct with no roster, no map and no
  pathfinder, and `pawn` is read constantly), **and the rescue estimate is a
  property of the ROSTER** (`triage` — "who could rescue" is `work_coverage`'s
  question one level down and "how long would they take" is a pathfind PER
  CANDIDATE). Putting a pathfinder in `pawn` or in the digest would make the
  cheapest read in the surface pay for it at every glance, which is what the
  predicate-cost decision of session 19 exists to stop. `travel_ticks` is
  `PawnPath.TotalCost`, whose unit genuinely IS ticks —
  `Verse.AI/Pawn_PathFollower.CostToMoveIntoCell` spends `TicksPerMoveCardinal|
  Diagonal + pathGrid.CalculatedCostAt + edifice.PathWalkCostFor`, one per tick
  — and it is published as a FLOOR: it excludes door waits, pawn collisions and
  the time to abandon the current job, which is exactly what `rescue` removes by
  forcing it. `carry_ticks` is the second leg to the bed `TakeToBedGate` chose,
  so `total_ticks` is the whole journey. Every casualty row carries `act`, the
  exact `rescue` envelope with both ids filled in, because the M1 run's sharpest
  finding is that `rescue` was shipped, forces the job, interrupts `LayDown` —
  and was called zero times in 195 ops while the response tried was a
  work-priority flip. `40ed42f`, `61794cd`.
- 2026-09-01 (session 21) — **An unknown argument name is OBSERVED, not
  declared: `VerbArgs` keeps a read log, `supplied − queried` is the
  unknown-argument set, and it is REPORTED for every verb and REFUSED only
  where a default mutates.** Session 15's rejection of the per-verb whitelist
  stands and is not re-litigated — 120 verbs, 22 handlers that read nothing at
  their own call site, 88 `(verb, key)` pairs the suites send through shared
  helpers, five suite call sites that build their dict at runtime. But every
  one of those is an objection to a SECOND SOURCE OF TRUTH, and none of them is
  an objection to watching what the verb actually read. `VerbArgs` is one
  sealed class over one `Dictionary<string, object>`, and every accessor —
  `Has`, `Raw`, `Str`, `StrReq`, `Bool`, `Num`, `NumReq`, `Int`, `IntReq`,
  `Long`, `StrList`, `NearMiss` — now funnels through one private `Look(key)`
  that marks the key. **The 22-forwarder case is not a special case at all**,
  because the log follows the OBJECT: `TimeDriver.Start(ctx.Command, ctx.Args)`
  hands over the same `VerbArgs`, so its reads land on the parent's log.
  Runtime-built caller dicts are likewise a non-issue, because nothing is
  compared against a list. `trade-set` is the proof by construction: it reads
  its singular sugar as `foreach (var k in new[]{"thing","index","count","buy",
  "sell"}) if (ctx.Args.Has(k))` — a key that no static scan can see and that
  the read log gets right for free. Nothing here needs updating when a verb
  gains an argument, which is what worker B's `advance {unread_ok,
  through_casualties}` and worker C's `posture` land into this round.
  **THE FORWARDING AUDIT, because a linked child log would have been a bug.**
  Nine `new VerbArgs(...)` exist. One is the dispatch root. Seven are NESTED
  SUB-OBJECT parses over a value the parent already read through `Raw(key)` —
  `until.event` (`TimeDriver`), `until.condition` (`StateWatch.Parse`),
  `filter` (`DesignateEngine.FromFilter`), `set` (`SpatialVerbs.Landmark`),
  `drugs[i]` (`PolicyVerbs`), `set[i]` (`PawnManageVerbs`), `elements[i]`
  (`LayoutVerbs`) — and they must NOT union into the parent, or an inner key
  would be counted as a top-level read. The ninth is `StarterKit.Sub`, the only
  real forward, and it needs no link either: it hands the child a dict the
  parent CONSTRUCTED, and to construct it the parent had to read its own args
  through accessors that already marked them. **A nested object's own unknown
  keys are therefore still unchecked** — `until.condition {pth: …}` is silent
  except where `NearMiss` already covers it — and that is the acknowledged
  remaining gap.
  **WHY IT REPORTS RATHER THAN REFUSES, measured.** The read log is only
  complete after the handler returns, and 729 accessor call sites were swept:
  ~290 are conditional, and **73 keys across 26 verbs are read only on SOME
  paths while the verb still returns success.** Four named shapes make a
  blanket refusal unshippable. (1) `dry_run` skips the block that reads the
  keys: `ZoneVerbs.Add` calls `Shape()` — the only reader of `label`,
  `priority`, `filter`, `plant`, `allow_sow`, `allow_cut` — under `if
  (!dryRun)`, so `zone {op:"add", plant, label, dry_run:true}`, the exact
  preflight an agent should run, would be refused. (2) A fallback unread on the
  happy path: `dev:spawn-thing` reads `pos` only when `stockpile` is absent or
  storage refused, which is the documented "store it, else drop it here" call.
  (3) `queue` sits after the per-pawn gates in twelve verbs (`attack`, `equip`,
  `wear`, `drop`, `consume`, `extinguish`, `beat-fire`, `tend`, `repair`,
  `man-turret`, `rest-until-healed`, `TakeToBed`), so `wear {pawn, thing,
  queue:true}` refused by its gate is a success envelope with `queue` unread —
  and `accept/4087644-order-honesty.py` is a suite about exactly those
  refusals. (4) Whole-verb refusals return BEFORE the config block: `bill-add`
  answers `NotAWorkTable` before `ValidateBillArgs` reads its twenty levers, so
  refusing would replace an informative refusal with "unknown args". So
  `Result` gains `IgnoredArgs`, the poller writes it as a top-level
  **`ignored_args {keys, read, detail, journal_seq_from?, journal_seq_to?}`**
  present ONLY when a key went unread, and `Log.Warning` — captured by
  `JournalHooks`' patch on `Log.Warning` — puts the same finding in the journal
  as a durable `warning` row so a ten-day run can be audited for dropped
  arguments afterwards. The log half is main-thread only; a `MainThread=false`
  handler runs on the poller thread and may not touch Verse. `advance` is the
  one verb with no envelope of ours (it returns `DeferredResult` and
  `TimeDriver` writes its own result), so the journal row is its only channel.
  The suggestion in `detail` is Levenshtein against the keys THIS CALL READ —
  derived, so it works for arguments added after it was written — while
  `NearMiss`'s call-site alias list still covers `at` -> `pos`, an edit
  distance of 3 that no conservative distance rule would ever find.
  **WHERE IT DOES REFUSE, and why that is safe.** git-bug 7382bdd comment #7
  named the shape: "a defaulted list argument whose default is non-empty and
  whose steps mutate". The tree has exactly three — `journal-selftest` (default
  `letter, message, error, downed, break`, of which `downed` is
  `HealthUtility.DamageUntilDowned` and `break` starts a Berserk),
  `pawn-fixture` (`wound, sadden, tatter`) and `world-fixture` (`bench, bill,
  stockpiles, growing, research, letter`). All three now call
  `VerbArgs.RefuseStray(op, ownArgs, …)` BEFORE the first step, unconditionally
  rather than only when the default fires, so a typo is refused with nothing
  mutated. `ownArgs` is the verb's full argument list written beside the code
  that reads it, and it is a MESSAGE-ONLY list: the detection is the read log,
  which consults it not at all, so its drift mode is a worse sentence and never
  a refused legitimate call — which is precisely the asymmetry that made a
  120-verb registry unacceptable and makes a three-site one fine. It is also
  what makes refusing safe there despite the conditional-read finding above:
  `journal-selftest`'s step-gated `save_name`, `power_lamps` and `error_text`
  are in the list, so they are accepted whether or not their step ran.
  `accept/s13-mod-surface.py` phase 9 re-derives all three lists from the
  source and fails on drift (9.10a-c), and re-derives that every accessor
  reaches the backing dictionary only through `Look()` (9.9a-b) — because an
  accessor added later that read `raw` directly would make a legitimate key
  look unread, which is the rejected declaration's failure mode sneaking back
  in through the mechanism that replaced it.
  **BULLET 1 AS LITERALLY WRITTEN IS STILL NOT MET, and this says so in those
  words.** "A verb call carrying an unrecognised argument key is REFUSED with
  `bad-args` naming the key" is true for the three fixture verbs and for the
  `NearMiss` aliases, and false everywhere else, where the call succeeds and
  publishes `ignored_args`. What IS met for all 120 verbs is the defect in the
  issue's title — an unknown argument name is no longer SILENT — and the
  destructive instance comment #7 filed is refused before it can act.
  `7382bdd`.
- 2026-09-01 (session 21) — **`hostility_response` is decided ABOVE seek, not
  beneath it, so `posture` is one verb and `Attack` is its load-bearing
  setting.** `b1b3060` and `[[seek-off-is-a-decision-to-flee]]` both held that
  SeekAndKill's `ThinkTreeInjector` puts its node above
  `ThinkNode_ConditionalColonist` and therefore makes the vanilla flee node
  unreachable, so the echoed field "describes a node nothing consults".
  **Refuted.** `RimWorld/JobGiver_ConfigurableHostilityResponse` — the only
  producer of `JobDefOf.FleeAndCower` for a sane colonist — is not in the
  `Humanlike` tree at all; it sits in **`HumanlikeConstant`** under
  `ThinkNode_ConditionalCanDoConstantThinkTreeJobNow`. `SeekAndKill/
  ThinkTreeInjector.Inject` skips any tree whose ROOT holds none of its four
  anchors, and `HumanlikeConstant`'s root holds only
  `ThinkNode_Subtree(Despawned)`, that conditional, and
  `ThinkNode_ConditionalCanDoLordJobNow` — so seek is never injected there. And
  `Verse.AI/Pawn_JobTracker.DetermineNextJob` runs
  `DetermineNextConstantThinkTreeJob()` FIRST and returns without touching
  `MainThinkNodeRoot`, while `JobTrackerTickInterval` re-runs the constant tree
  every 30 ticks (`RimWorld/AITuning.ConstantThinkTreeJobCheckIntervalTicks`)
  and starts its job with `JobCondition.InterruptForced`. A second consumer says
  the same one level down: `JobGiver_ReactToCloseMeleeThreat` is at index 6 of
  the `Humanlike` root — above the index-11 insertion point — and returns null
  unless `hostilityResponse == Attack`. **The M1 evidence the issue cited as
  proof is the refutation**: op 109 had seek ON, hostility `Flee`, and Captain
  in `JobDriver_FleeAndCower`, which is impossible if the flee node is
  unreachable. So `Flee` BEATS seek, and seek ON with `Flee` is the worst
  combination available. **The flee branch has two halves and this entry
  conflated them with the ATTACK branch's numbers until the orchestrator caught
  it on 2026-09-01** — recorded rather than quietly corrected, because a
  citation that names the wrong method is worse than none. The TRIGGER is
  `SelfDefenseUtility.ShouldStartFleeing`, which `TryGetFleeJob` opens with, and
  it is the only place distance and sight are tested: `ShouldFleeFrom` with
  `checkDistance:true, checkLOS:false` over `ThingRequestGroup.AlwaysFlee` and
  `checkDistance:true, checkLOS:true` over `ThingRequestGroup.AttackTarget` in a
  9-region `BreadthFirstTraverse`, where `checkDistance:true` is
  `InHorDistOf(pawn.Position, 8f)`. The DESTINATION is a different question:
  `TryGetFleeJob` re-gathers threats with `checkDistance:false, checkLOS:false`
  at all three of its own call sites and passes the lot to
  `CellFinderLoose.GetFleeDest`, so the pawn runs from EVERY hostile the caches
  hold — which is how one crow inside 8 cells produced Captain's 150-cell run.
  The `maxDist = 8f` / `Clamp(EffectiveRange * 0.66, 2, 20)` pair and the
  `NeedLOSToAll` scan flag belong to `TryGetAttackNearbyEnemyJob` and are cited
  only there. `TryGiveJob` also bails to null before the switch on
  `PlayerForcedJobNowOrSoon`, `pawn.Downed` and an Anomaly
  `LordJob_PsychicRitual`, which is why the `downed` and `player-controlled`
  verdicts cite the constant tree as well as the seek side. That is M1 day 1 and
  M1 day 4 in one state. **`on_contact` is therefore
  computable and is published**, per pawn and as a rollup, as the resolved
  decision order rather than a field echo: downed / mental-break /
  player-controlled / flee / attack-then-seek / attack-nearby / seek-only /
  ignore, each carrying the member that decides it. What it deliberately does
  not model is said rather than implied — asleep (`ThinkNode_
  ConditionalLyingDown` at root index 0; the constant gate needs
  `pawn.Awake()`) and mid-forced-job (`PawnUtility.PlayerForcedJobNowOrSoon`)
  are transient, and a field that flickers with the day/night cycle is not a
  posture. `b1b3060`.
- 2026-09-01 (session 21) — **`posture` REFUSES an absent area rather than
  creating one, and refuses a zero-cell one; and no lever at all is a pure
  read.** `posture {area, pawns?, seek?, hostility?, dry_run?}` sets the three
  settings that must agree and reports per pawn what it applied and what it
  refused, with each lever's widget gate cited
  (`PawnColumnWorker_AllowedArea.DoCell` + `Area.AssignableAsAllowed` via
  `assign`'s own `AreaGate`, CALLED not copied;
  `PawnColumnWorker_HostilityResponse.DoCell` plus
  `HostilityResponseModeUtility.DrawResponseButton_GenerateMenu`, whose menu
  omits `Attack` for a pawn with `WorkTags.Violent` disabled;
  `SeekAndKill/Patch_PawnGetGizmos.ShowsSeekGizmo`, called for the same reason
  `seek-at-will` calls it). **Creating the area on demand is refused because the
  fix would manufacture the bug**: a fresh `Area_Allowed` is EMPTY and
  `RimWorld/ForbidUtility.InAllowedArea` short-circuits on `TrueCount > 0`, so
  an auto-created area binds nothing while the verb reports every pawn bound —
  the exact false report the issue exists to remove. Two lesser reasons hold the
  same way: `new Area_Allowed(...)` rolls `Rand.Value` twice (determinism class
  R), and `area allowed create` / `area allowed add` already ship. The same
  short-circuit makes a NAMED zero-cell area a bad-args refusal unless
  `allow_empty_area:true`, and makes `digest.posture.area_bound` count a pawn as
  bound only when its EFFECTIVE area has cells. Passing any one lever requires
  `area`, because a posture with two of three settings is the defect; passing
  none is a read, the same contract `seek-at-will` already has. **`n/m` ships as
  a string AND as integers** — the issue asks for `n/m`, which is the glance,
  but `advance {until:{condition}}` refuses `<` on a string rather than coercing
  it (session 19), so `will_seek` is `"2/3"` and `will_seek_n`/`will_seek_of`
  are the numbers. Denominators differ on purpose and are published in words:
  `will_seek` and `attack` over violence-capable free colonists, `area_bound`
  over those whose `SupportsAllowedAreas` is true. Registered as a predicate
  section on session 19's axis — no `Room.Role`, no `GetStatValueAbstract`, no
  pathfind; `Pawn.CombinedDisabledWorkTags` and
  `Pawn_StoryTracker.DisabledWorkTagsBackstoryTraitsAndGenes` both recompute and
  write no cache, which is why the violence test is the TAG one rather than
  `GetDisabledWorkTypes`, whose getter fills `cachedDisabledWorkTypes`.
  `b1b3060`.
- 2026-09-01 (session 21) — **All three posture settings survive a save/load
  round trip, and the digest is what proves it rather than this sentence.**
  Read rather than assumed. `RimWorld/Pawn_PlayerSettings.ExposeData` scribes
  `hostilityResponse` (`Scribe_Values`, default `Flee` — which is also the field
  initialiser, so a fresh colony is a colony of fleers) and `allowedAreas`
  (`Scribe_Collections`, `LookMode.Reference` on both key and value, with a
  save-time prune of null entries and a PostLoadInit clear for Roamers). The
  third is a third party's and was the one in doubt:
  `SeekAndKill/SeekAndKillGameComponent.ExposeData` scribes the toggle set as
  `SK_SeekPawns` — but ONLY when `PSInterop.PsToggleShared` is false, because
  with Perspective Shift present PS's `seekAtWillPawns` is the single source of
  truth and S&K writes nothing. PS is OUT of the bench modlist, so on this bench
  the set is S&K's own and it persists. `LoadedGame` then runs `PruneStaleIds`,
  which drops ids absent from `PawnsFinder.AllMapsWorldAndTemporary_Alive` and
  bails entirely when `Find.Maps.Count == 0`, so a living colonist is never
  pruned. The posture therefore survives — and `digest.posture` is built so that
  when it does NOT, the read says so on its own: `will_seek`, `area_bound` and
  `attack` are counted from live state at every glance, `seek_mod` and
  `seek_mod_missing` name a mod that stopped answering, and `flee_risk` names
  every violence-capable pawn whose response has fallen back to `Flee`.
  `b1b3060`.
- 2026-09-01 (session 21) — **`advance` refuses on an unread journal delta, and
  the alternative — attaching the delta to the advance result — was costed and
  rejected because IT IS WHAT ALREADY FAILED.** `722c951` asked for both to be
  priced and one chosen. The refusal wins on evidence, not on bytes: run
  `m1-20260831` step 148 returned `journal_seq:[125,128]`, so the advance result
  ALREADY CARRIED the news that Table was down, and the run advanced five more
  times while he bled for 11,335 ticks. Attaching the events themselves is the
  same fix, larger, and an echo a caller may ignore is what the failure was. The
  bytes are the second argument and they are not small: measured across 24
  session journals on this bench, an event averages **243 bytes** and the longest
  single line is **4,416**; the densest sustained run (`20260901T180111`) is 166
  events / 54,397 bytes over 76,635 ticks, i.e. **~1,775 bytes per 2,500 ticks
  and ~42 KB for a 60,000-tick advance**, while a raid burst put **20,928 bytes
  in 102 ticks** (`20260901T163220`). Against that, 67 real `advance` envelopes
  measure **698–1,383 bytes**: attaching would grow every advance result by
  15–60x typically, on every call whether or not anything happened, bounded in
  the tail only by the `journal` verb's own 2,000-event cap (~8.8 MB). So:
  refuse, and the delta stays in the verb built to page it. **The window that
  blocks is the PREVIOUS ADVANCE's delta, not "any unread event".** The issue's
  own question is "since the last advance", and the distinction is load-bearing:
  events emitted while TIME RAN are news nobody saw, while events emitted while
  the agent is AT THE WHEEL are its own acts, each of which already returned a
  result envelope. The play loop is read -> think -> ACT -> advance and every
  mutating verb journals an `action` row, so blocking on those would charge a
  `journal` round trip to every turn that acted — friction with no safety in it,
  and friction is what makes a run leave the escape hatch on. Consequences
  stated rather than discovered: the first advance of a session is never refused
  (nothing has run unobserved), and an advance that journaled nothing creates no
  obligation, so a quiet colony never pays. `722c951`.
- 2026-09-01 (session 21) — **A "client" is the whole bench: ONE global read
  watermark, because the bridge has no client identity to key on and inventing
  one would mean inventing the registry too.** `722c951`'s scope says "per-client
  watermark". Investigated rather than assumed: `Poller.ScanInbox` reads
  `commands/<id>.json` envelopes carrying `id`, `op` and `args` and nothing else
  — no handshake, no connect or disconnect edge, no field a client could stamp
  itself with — and `rwa`'s own `new_id(op)` is `<op>-<HHMMSS>-<pid4>`, fresh for
  every COMMAND, because each `rwa` invocation is its own process. Even the pid
  suffix names a call, not a client. A dictionary keyed on a string the bridge
  never validates also has no expiry edge, so it is a leak with no way to close
  it. **What breaks with a second client, stated:** the watermark is shared, so a
  `rwa journal` typed at a shell by a human watching the bench discharges the
  agent's obligation and its next advance proceeds having read nothing — a real
  hazard here, since the orchestrator does read the journal mid-run. The
  mitigation that ships is VISIBILITY, not prevention: `journal` publishes
  `read_watermark`/`watermark_was`/`watermark_moved` and every advance echoes
  `journal_read_watermark`, so a client tracking its own last read can see the
  number move without it. The upgrade, when a second client is real, is an
  OPTIONAL `client` field on the envelope defaulting to one name — one line in
  `Poller.ScanInbox` and a dictionary beside `Journal.readWatermark`. Not built
  on speculation. Also settled: only the `journal` verb moves the mark, and it
  moves to `last_seq` for an unfiltered untruncated read and to the highest seq
  actually RETURNED otherwise, so `journal {types:["letter"]}` does not discharge
  a `downed` it never asked for; a direct `cat` of the file moves nothing,
  because the bytes are the same and the mod cannot see a shell. `722c951`.
- 2026-09-01 (session 21) — **The escape hatch is three controls and no fourth,
  and what it costs is that the discipline becomes optional — so the controls are
  the whole design, not a footnote to it.** `advance {unread_ok:"<why>"}` and
  `advance {through_casualties:"<why>"}`, per `722c951`'s requirement that the
  bypass be deliberate and journaled (session 13's `threat-pardon` precedent: the
  decision must be a recorded ACT, not a silent exemption). The honest statement
  of the cost is that the escape IS the guard made optional: a run that passes it
  on every advance has exactly the mod it had before this issue, and no amount of
  refusal design prevents that. Three controls make it expensive instead of
  impossible. **(1) PER-CALL, never a mode** — there is no session flag, no
  config key and no environment variable, so "turn it off and forget" is not
  spellable; and `unread_ok` deliberately does NOT move the watermark, so it buys
  one call and the next advance asks again. Three in-game days of riding past a
  delta costs three separate journaled admissions. **(2) A REQUIRED, NON-EMPTY
  REASON STRING** — empty, whitespace and non-string are all `bad-args`, so the
  argument cannot degrade into a bare boolean, and the reason is what a
  post-mortem actually reads. **(3) A GREPPABLE ROW** — one `action` event per
  advance carrying `verb:"advance"`, `step:"escape"`, the reasons and a
  `bypassed` list naming what it actually overrode, plus an envelope echo
  (`unread_ok`/`through_casualties`/`escaped`) so a transcript-only audit sees it
  too. `accept/4.2-play-loop.py`'s `advance-discipline` surfaces every one as a
  WARN with its reason, and the selftest asserts THAT rather than only asserting
  FAILs — an opt-out nobody can see in the audit is the silent bypass this issue
  exists to prevent. **Are three enough? Not on their own.** What they buy is
  detectability, and the missing control is a consequence: nothing here FAILS a
  run for standing on the escape. The two additions worth making, when there is
  evidence to set them against, are a per-run BUDGET (N escaped advances, then
  the mod refuses the escape itself) and a hard `advance-discipline` FAIL above a
  fraction of escaped advances. Both need a threshold, no number is defensible
  yet, and a guessed threshold is the thing this project keeps deleting — so the
  measurement ships first and the wall waits for the M1 re-run's numbers.
  `722c951`.
- 2026-09-01 (session 21) — **The casualty halt filters on FACTION and the bleed
  refusal fires on `too-slow` and nothing else: one is about who fell, the other
  is about whether a decision exists.** For the halt, `Verse/Faction.IsPlayer`
  (`def.isPlayer`) is the game's own "mine", resolved on the MAIN thread by the
  emitting hook and carried in the `downed`/`death` payload as `player`, because
  `TimeDriver.Notice` is documented "any thread, called synchronously from
  Journal.Emit" and may not touch Verse to ask for itself. Deliberately NOT
  `Verse/Pawn.IsColonist` — `Faction.IsPlayer && RaceProps.Humanlike && (!IsSlave
  || guest.SlaveIsSecure)` — which excludes an INSECURE slave, whose downing is
  precisely a casualty somebody must look at; `kind` (colonist|slave|animal|mech)
  rides beside it so the narrower reading is the caller's to take rather than the
  mod's to impose. A hostile downing halts nothing: that is the advance working,
  and stopping on it would make every fight a wedge. Both hooks are on the
  TRANSITION (`Pawn_HealthTracker.MakeDowned`, `SetDead`), so a pawn already down
  when an advance starts emits neither and the halt cannot re-fire for the same
  pawn. For the refusal (`40ed42f` part 3), the verdict comes from `triage`'s own
  row — `CasualtyRow` is extracted so the verb and the refusal share one pathfind
  and one comparison — and **`no-rescuer`, `no-path` and `no-deadline` do NOT
  refuse.** The refusal exists to force ONE decision into the open, "this pawn
  dies unless you act", and that decision only exists when there is a TIMING
  question. Where nobody can reach the patient at all, no act clears the
  condition, the next advance would refuse identically, and a ten-day unattended
  run stops dead on a state it cannot fix — a wall, not a guard. **Not
  hypothetical:** on a bare `--quicktest` map there is no bed, so
  `TakeToBedGate` -> `HealthAIUtility.CanRescueNow` -> `WantsToBeRescued` answers
  `no-bed` for every colonist and EVERY verdict is `no-rescuer` with a null
  margin (orchestrator bench pass, `accept/runs/s21-20260901/`). The casualty is
  not lost by staying quiet: the advance HALTED when they went down, the clock is
  in every `pawn` read, and `triage` names the gate that refused each candidate —
  `no-bed` says build a bed, which is an act, where "refuse to advance" says
  nothing. `WantsToBeRescued`'s first two clauses (`!Downed`, `InBed()`) also put
  a standing bleeder and a bedded bleeder outside this refusal, correctly: a
  patient in bed whose DOCTOR cannot arrive in time is a different comparison
  (tend travel, not rescue travel) and is not claimed. `722c951`, `40ed42f`.

- 2026-09-01 (session 21) — **The casualty halt is on the TRANSITION, not on the
  STATE: an advance made while an own-faction colonist is ALREADY down runs
  normally, and that is the mod being right.** Raised as a genuine ambiguity by
  acceptance check 7.6a of `accept/61794cd-bleed-triage.py`, which staged four
  already-downed colonists and asserted that an un-escaped `advance` could not
  complete. On the s21 bench it completed — `ok:true reason:"ticks" ticks:300` —
  and the suite, not the mod, was wrong. `722c951`'s own text pulls both ways in
  one place only: its Acceptance bullet says "an advance **spanning** an
  own-faction downing stops at it", which a reader can take as a condition.
  Everywhere the issue is SPECIFIC it is a transition: the scope extension says
  "stop early when an own-faction pawn goes down or dies **during the advance**,
  returning what happened **and the tick it happened at**" — a state has no such
  tick; the sibling bullet says "an advance spanning a HOSTILE downing does NOT
  stop — prove the filter is on faction, **not on the event**" — a downing is
  named as an event; and the replay bullet says "an advance **across tick
  214,599** stops there". The implementation matches: `JournalHooks.Patch_MakeDowned`
  and `Patch_SetDead` are POSTFIXES on `Pawn_HealthTracker.MakeDowned` / `SetDead`,
  so a pawn already down emits nothing.
  **The design argument decides it independently of the wording.** A state halt
  wedges a ten-day unattended run: on a bedless map every casualty's verdict is
  `no-rescuer` (`TakeToBedGate` refuses everybody), there is no act that clears
  the condition, and every subsequent advance would halt at zero ticks on the
  same pawn forever. That is the identical reasoning that already made the
  `bleedout-deadline` refusal fire on `too-slow` and on nothing else, in the
  entry above. And the escape does not rescue a state halt: `through_casualties`
  is per-call and deliberately not a mode (`722c951` checks 2.17–2.18), so a
  state halt would force the escape onto every advance for the rest of the run
  and train the agent to pass it unread — the guard switched off by fatigue,
  which is worse than the guard absent. The casualty is not lost meanwhile: it is
  in every `digest`, in `triage`'s rows with the gate that refused each
  candidate, and in `pawn.health.ticks_until_bleedout`.
  The suite now asserts BOTH halves — 7.5a that the state does not halt (the
  anti-wedge property, previously only an accident), 7.6 that a downing armed
  INSIDE the advance does, via `722c951`'s own `journal-selftest --steps down-at`
  fixture — and 9.12a re-derives the transition claim from `JournalHooks.cs` so a
  hook moved onto `Pawn.Downed` fails offline rather than on a bench. `722c951`,
  `61794cd`.

- 2026-09-01 (session 21) — **`enabled_but_incapable` is published
  UNCONDITIONALLY on every diagnosed row — empty list, never absent.** It was
  emitted only when the impaired list was non-empty, in BOTH homes
  (`WorkCoverage.Section`'s under-row and `work-cover`'s `still_under` row), so
  an absent key meant both "nobody enabled here is missing a capacity" and "this
  build does not publish that key". That is the exact conflation `61794cd`
  already ruled against for `ticks_until_bleedout` (`null`, never omitted, never
  `int.MaxValue`) and that `PawnActs.NoStamp()` exists for, and every sibling in
  the same dictionary — `available_pawns`, `candidates` — is already an
  always-present, possibly empty list. It cost acceptance check 7.2h, which asked
  for the key on a refusal whose fixture happened to have Doctor switched off
  across the roster: nobody was enabled-but-incapable, the key was absent, and
  the check could not tell that from a wrong dig path. `work-cover`'s `note` now
  says a **non-empty** list means surgery, and `checklists/triggered.md` item 7
  says to branch on whether the list has entries rather than on whether it is
  there. **Not fixed, and deliberately:** the outer `if (r.Under)` that keeps a
  FINE row to three fields stays — that is the digest's stated byte budget,
  asserted by 3.4a, and whether a COVERED row should carry the diagnosis at all
  is a separate question filed as finding 4.7 on `40ed42f`. `40ed42f`.

- 2026-09-01 (session 21) — **`no-bed` is only reachable for a DOWNED patient;
  a standing one is refused `cannot-rescue` first, and the two are different
  answers to "why is nobody coming".** `TakeToBedGate("rescue", …)` opens on
  `HealthAIUtility.CanRescueNow` -> `WantsToBeRescued`, whose FIRST clause is
  `!pawn.Downed`, and the `RestUtility.FindBedFor` lookup is its LAST. So on a
  bedless map a downed casualty's candidates all read `no-bed` (banked at
  `accept/runs/s21-20260901/18-triage-downed.json`) while a STANDING bleeder's
  all read `cannot-rescue`, and no fixture can make the second produce the first.
  The entry two above says "`no-bed` for every colonist" on a bedless map; that
  is true only of the downed patient it was measured on, and this corrects it —
  the verdict is `no-rescuer` either way, which is what that entry's argument
  actually rests on. Acceptance checks 6.4c/6.4d were reading the gate off
  `casualties[0]`, which this suite's fixture makes the standing bleeder; they
  now select the row by `downed` and 6.4b2 asserts the standing half by name.
  `40ed42f`.
- 2026-09-01 (session 22) — **RimWorld ALREADY RECORDS ELEVEN TIME SERIES, and
  the mod had never read one; so half of the rates spec is a reader, not a
  sampler.** `RimWorld/HistoryAutoRecorder.Tick` appends `Worker.PullRecord()`
  to a public `List<float> records` every `def.recordTicksFrequency` ticks, from
  `RimWorld/History.HistoryTick`, which `Verse/TickManager.DoSingleTick` calls
  unconditionally. Eleven recorders, all Core, no DLC adds any (verified against
  `Data/Core/Defs/Misc/HistoryAutoRecording/HistoryAutoRecorders.xml`): four
  wealth, colonists, prisoners, mood at 30,000 ticks; adaptation, threat points,
  pop-adaptation, pop-intent in a `devModeOnly` group. **`devModeOnly` gates the
  UI TAB and nothing else** — `RimWorld/MainTabWindow_History` guards the group
  with `!groupLocal.def.devModeOnly || Prefs.DevMode` while `HistoryTick` loops
  every group — so threat points are recorded on a bench with dev mode off and
  always have been. `grep -rn "HistoryAutoRecorder" Source/AutoRimmer/` returned
  NOTHING before this round, and the project had already paid for that: session
  13 answered "did wealth cause the M1 raids?" by decoding `HistoryAutoRecorder`
  **out of `Autosave-5.rws` by hand**. That decode is now
  `accept/fixtures/history-autoRecorderGroups-m1-Autosave-1.xml` and the
  acceptance suite grades against it offline. The loop this exposes is the one
  the project most needs to see: `Wealth_Total` and `ThreatPoints` are both
  recorded and raid points scale with wealth
  (`StorytellerUtility.DefaultThreatPointsNow` over `PointsPerWealthCurve`), so
  "we died to raiders, build more guns" raises the threat it is answering, and
  the game has been graphing both sides all along. `2d9a1da`.
- 2026-09-01 (session 22) — **INDEX IS TICK, and the map is the GAME's, not
  ours; `aligned` says when it does not hold.** `HistoryAutoRecorderGroup
  .DrawGraph` plots sample j at day `(float)j * (float)recordTicksFrequency /
  60000f`, so index j is tick `j*freq`, and `history` publishes
  `last_point_tick = (count-1)*freq` beside the live clock. The map holds for a
  recorder that existed at tick 0, which is all eleven; it does NOT hold for one
  a mod adds to a live save, because `AddOrRemoveHistoryRecorders` creates it
  empty at PostLoadInit and `Tick`'s `|| !records.Any()` clause appends
  immediately at whatever tick that was. Rather than assume the common case, the
  verb publishes `aligned` — the clock against where the index says the last
  sample should be — so a series whose index cannot be read as a tick says so
  instead of being discovered. **And the stored number is not always the value:**
  `HistoryAutoRecorderWorker_ThreatPoints` stores `DefaultThreatPointsNow / 10f`
  and `_PopIntent` stores `PopulationIntent * 10f`, warned about only in a
  human-readable def LABEL ("fun points /10"). `stored_scale` publishes the
  multiplier and NAMES the member it recovers, keyed on defName for exactly
  those two so a modded recorder gets nothing rather than a guess. `2d9a1da`.
- 2026-09-01 (session 22) — **Four History members audited, three routed around,
  and the one that matters is the one that is not a write.** `Find.History
  .Groups()`, `HistoryAutoRecorderGroup.recorders`, `HistoryAutoRecorder.records`
  and the def's `recordTicksFrequency`/`label`/`valueFormat` are plain fields and
  safe. Routed around: `HistoryAutoRecorderDef.Worker`, a lazy-init
  `Activator.CreateInstance` into `workerInt`; `Verse/Def.LabelCap`, which caches
  into `cachedLabelCap` on a getter that reads like a plain accessor (`label` is
  published instead); `HistoryAutoRecorderGroup.DrawGraph`, which rebuilds
  `curves` and stamps `cachedGraphTickCount`. **`Worker` is refused for a second
  reason that outranks the write:** calling `PullRecord()` would RE-DERIVE a
  number the game has already stored, and for ThreatPoints it would run
  `DefaultThreatPointsNow` on the spot. Serialize, do not reinvent — and the
  acceptance suite greps the SOURCE WITH COMMENTS AND STRING LITERALS STRIPPED to
  assert it, because the file argues about these members at length and the naive
  grep failed the check for doing the documenting the check exists to reward (it
  hit the verb's own `source` provenance string, measured on the suite's first
  run). `2d9a1da`.
- 2026-09-01 (session 22) — **The sampler CONSUMES `DigestVerb.SectionFor` and
  re-derives nothing, which is what makes it safe, cheap and forward-compatible
  at once.** This issue's hazard note says a write-on-read bug in a sampler is
  MULTIPLIED rather than incidental because it runs on a schedule; consuming a
  builder whose every accessor is already audited takes that risk to zero
  instead of re-auditing it. It is also the cheapest design available — the
  research on this issue found 17-18 of the ~24 candidate scalars already
  computed by `DigestVerb` — and it means the sampler INHERITS changes to the
  arithmetic rather than forking them: `spec/temp-control` is adding a rot term
  to `food_days` in parallel, and nothing in `ColonySampler.cs` needs to know.
  What is deliberately NOT sampled, each for its own reason: wealth / threat /
  mood / population, because THE GAME ALREADY RECORDS THEM; hostiles, danger and
  alerts, because those are EVENTS and `Journal.CountsRange` already answers a
  count over a window; and **weapons, which is the sharpest gap the research
  found — across all 133 vanilla alert classes NONE covers armament and
  `ResourceCounter` cannot help because weapons are Uncounted — but which is a
  NEW DERIVATION (equipped by pawn scan plus spare by `ThingsInGroup(Weapon)`,
  disjoint populations) that no digest section publishes yet.** It is filed as
  its own issue rather than smuggled in here; the field table is its landing
  site, one row once a section publishes the number. `2d9a1da`.
- 2026-09-01 (session 22) — **A SLOPE IS A STATEMENT ABOUT A WINDOW, and the
  window is published beside every number; the span floor is the guard, not the
  point count.** Least squares rather than an endpoint difference, because
  colony stocks move in LUMPS — a hunt returns 200 nutrition at once — and an
  endpoint estimate over a window IS one lump at either end while the regression
  moves by 1/n (measured in the suite: a series whose true slope is −12/day with
  one +6 lump at the end reads −5.74 endpoint against −10.56 regression). Two
  floors: at least 3 points, and at least 15,000 ticks of SPAN. The second is
  the one that matters. At the 2,500-tick cadence three samples span 5,000 ticks
  — two in-game hours — and reporting a per-day rate off that is a 12x
  extrapolation presented as a measurement. Below the floor the answer is
  `null`, `ready` is false, and `not_ready_why` names the floor that was missed;
  so the first slope of a run arrives at sample 7, a quarter of an in-game day
  in. `span_ticks` rides every slope regardless, so the extrapolation factor is
  never hidden. The default window is 24 points = one in-game day, because a
  colony's food is periodic on exactly that period and a six-point window
  reports the slope of lunch. `2d9a1da`.
- 2026-09-01 (session 22) — **`days_to_zero` is the colony's
  `ticks_until_bleedout`, it is NULL when the stock is not falling, and that null
  makes it a BAD PREDICATE TARGET — said in the digest's own header because a
  hazard documented nowhere a caller looks is not documented.** `61794cd` ruled
  that `int.MaxValue` is published as null rather than as 2147483647 because a
  sentinel reads like a real deadline; the same argument gives null for "this is
  not falling, so there is no honest countdown". But `StateWatch.One()` refuses
  an ordering operator against null, and the two failure modes are not
  symmetric: at ARM time it is a clean refusal naming the reading, while
  MID-ADVANCE `Poll` returns false and never halts — so an advance waiting on
  `trends.food_days_to_zero <= 2` stops halting the moment food stops falling,
  which is exactly when the good news arrived, and runs to its timeout. **So
  predicates want `*_per_day`, which is always a number once `ready` is true, and
  `*_to_zero` is for the agent to read.** The acceptance suite arms the null case
  and asserts the refusal, so the sentence cannot quietly stop being true. It is
  also the number that corrects the game's own 1.6x overstatement: `food_days` is
  nutrition per head, the UI calls that "days worth", and a colonist eats ~1.6
  nutrition/day — a MEASURED slope needs no such assumption. `2d9a1da`.
- 2026-09-01 (session 22) — **The sampler ticks on `GameComponentTick`, its ring
  is VOLATILE and cleared at every game boundary, and the durable tier is a
  SEPARATE FILE rather than the journal.** Three decisions, one argument each.
  (1) `GameComponentUpdate` runs every FRAME including while paused, so sampling
  there would append identical rows at a wall-clock rate while the agent sat
  thinking and flatten every slope with data containing no game time;
  `GameComponentTick` runs inside `DoSingleTick`, which also calls
  `Find.History.HistoryTick()` immediately before it, so a sample sees a history
  the game has already updated. (2) The ring is cleared at
  `Runtime.ResetForGameBoundary` — both detectors, beside `Placements.Clear()` —
  because **a load can move `TicksGame` BACKWARD** and a regression across that
  seam fits two timelines at once; scribing it into the save would also put this
  mod's own writes inside the save-diff that exists to prove the mod does not
  write. The loss is bounded and VISIBLE: `ready` false, `points` small,
  `first_tick` now. (3) The durable file is
  `samples/<sid>.ndjson`, copying `Journal.Flush`'s peek-write-dequeue and
  bounded-reopen discipline and none of its seq/ring/dedupe machinery —
  **because `Journal.Emit` would have broken `722c951`.** That refusal rests on
  "an advance that journaled NOTHING creates no obligation, so a quiet colony
  never pays for this at all", and a periodic row from inside the tick loop
  makes every advance longer than one cadence journal something, so every
  subsequent advance refuses: "your colony has news" becomes "time passed". A
  separate file costs ~90 lines and changes nothing anybody else relies on.
  `2d9a1da`, `722c951`.
- 2026-09-01 (session 22) — **The temperature gate is the game's own
  -273.15..1000 clamp and NOTHING ELSE; the def's advertised range is dead code
  and is published as an advisory rather than enforced as a refusal.** 261f2e9's
  acceptance asks that "setting a target below the def's `minTemperature` is a
  REFUSAL naming the clamp and the value", on the stated premise that
  "`TemperatureControlProps` carry `minTemperature` / `maxTemperature`, which is
  the range the UI slider is clamped to". Checked rather than assumed, three
  things in that sentence are false. **There is no slider** —
  `RimWorld/CompTempControl.CompGetGizmosExtra` yields five `Command_Action`
  buttons (-10, -1, "reset to 21", +1, +10). **The fields are named
  differently** — `CompProperties_TempControl.minTargetTemperature` (-50) and
  `.maxTargetTemperature` (50). And **nothing in the 1.6 tree reads either
  one**: grepped unpiped over the whole decompiled source, the only two hits are
  the declarations. The only clamp that exists is
  `InterfaceChangeTargetTemperature`'s `Mathf.Clamp(TargetTemperature,
  -273.15f, 1000f)`, so a player walks a cooler to -273 with the -10 button and
  the game does not stop them. Implementing the issue's refusal would make a
  player verb REFUSE SOMETHING A PLAYER CAN DO, which is DESIGN's Action model
  broken in the other direction and the same class of error as bypassing a gate
  — the gate lives in the widget, and it is the widget's gate that must be
  reproduced, not a gate the def merely advertises. What ships: `def_min_c` /
  `def_max_c` on every row with `def_clamp_enforced: false`,
  `outside_def_range: true` plus a named `advisory` when a target lies outside,
  and a `bad-args` refusal citing the member for the clamp that is real. The
  same reasoning settles the issue's OTHER refusal ask — an unpowered cooler
  REPORTS `powered:false` and carries an advisory citing
  `Building_Cooler.TickRare`'s `if (!compPowerTrader.PowerOn) return;`, because
  `CompGetGizmosExtra` has no power clause either. `261f2e9`.
- 2026-09-01 (session 22) — **A `Vent` is a `flick` case, and the answer comes
  from the def rather than from judgement.** 261f2e9 flags "whether the vent
  belongs in the same verb or is a `flick` case" as worth checking. Core's
  `Defs/ThingDefs_Buildings/Buildings_Temperature.xml` gives `Vent` exactly one
  comp, `CompProperties_Flickable`, and NO `CompProperties_TempControl` — so
  `Building_Vent.compTempControl` is null even though `Building_Vent` derives
  from `Building_TempControl`, the temperature gizmos never exist for it, and
  `Building_Vent.TickRare` reads no target at all
  (`GenTemperature.EqualizeTemperaturesThroughBuilding(this, 14f,
  twoWay: true)`). A vent has no temperature to set. `temp-set` refuses it by
  name with that reason and points at `flick`, which is the verb that opens and
  closes it. Worth recording because the vent is in the Temperature build
  category, is called a temperature building, and derives from the temperature
  base class — three signals that all point the wrong way. `261f2e9`.
- 2026-09-01 (session 22) — **The room a cooler SERVES is the game's own
  south-rotated cell, not the cell the cooler stands in, and both of its sides
  must be passable or it does nothing.** `RimWorld/Building_Cooler.TickRare`
  computes `intVec = Position + IntVec3.South.RotatedBy(Rotation)` and
  `intVec2 = Position + IntVec3.North.RotatedBy(Rotation)`, pushes the
  temperature change into `intVec.GetRoom(Map)` and the waste heat into
  `intVec2` — and wraps the whole block in `if (!intVec2.Impassable(Map) &&
  !intVec.Impassable(Map))`. A cooler sits IN a wall, so its own cell's room is
  neither side, and a cooler walled in on its exhaust side draws idle power
  forever while moving no heat. `temp-control` therefore publishes `serves`
  (south), `exhaust` (north), `cold_side_blocked` and `hot_side_blocked` with a
  `serves_basis` naming the member each came from, and `Building_Heater.TickRare`
  gets its own citation because it uses its OWN room. Three more silent no-op
  states are published the same way — `GenTemperature.
  ControlTemperatureTempChange` returns 0f for a room that is null or
  `UsesOutdoorTemperature` (so a controller in an unroofed or map-edge room
  stores its target and does nothing), `IsBrokenDown()`, and a flick switch that
  is off. Every one of them is a state in which the target is accepted and never
  honoured, which is exactly the shape of the session-18 finding, so each gets a
  `candidates + reasons` advisory rather than a bare boolean. `261f2e9`.
- 2026-09-01 (session 22) — **`digest.temperature` is a registered predicate
  section and its `ok` is deliberately NARROW; the food alarm lives in
  `resources.food_rot` instead.** 261f2e9 reads as an actuator gap; the
  investigation found the observation gap underneath it, and the blind spot is
  the dangerous half — session 18 read 14.6 C only by calling `room <id>` for a
  room whose id it already had to know, and nothing in the glance said the
  freezer was at room temperature. A room is WATCHED when a controller serves it
  or when it holds human-edible food. `ok` is false only when a room a
  **switched-on** controller serves is more than `tolerance_c` (2 C, OURS,
  published on every read) on the wrong side of that controller's target.
  Switched-off controllers are excluded because `CompFlickable.SwitchIsOn` is the
  player's own recorded intent — a heater off in summer is not a fault —
  while UNPOWERED ones are NOT excluded, because a freezer whose net died is the
  emergency the section exists for. Food sitting warm in an uncontrolled room is
  counted (`food_rooms_uncontrolled`, `food_rooms_unfrozen`) and NOT alarmed
  here: on a colony with no freezer that alarm would be permanently on, and an
  alarm that is always on is not an alarm. Cheap on session 19's axis and
  therefore registered: one walk of the real `allBuildingsColonist` list with a
  per-def memoised `HasComp`, one walk of the stored
  `FoodSourceNotPlantOrTree` list with a per-def memoised `Nutrition`,
  region-grid room lookups and plain field reads. **No `Room.Role` and no
  `Room.GetStat`** — both run `UpdateRoomStatsAndRole()`, the most expensive
  line in `DigestVerb`, so a room row here is identified by id/at/cells and a
  reader who wants the role calls `room <id>` and pays for it there. `261f2e9`.
- 2026-09-01 (session 22) — **`food_days` is NOT redefined; the rot term ships
  beside it, and the disclaimer moves out of the source comment and into the
  data.** `resources.food_days` had no rot term at all (`grep -rn CompRottable
  Source/AutoRimmer/` returned nothing), and it is worse than incomplete: it is
  `map.resourceCounter.TotalHumanEdibleNutrition`, whose
  `UpdateResourceCounts` walks SlotGroup haul destinations (food on unzoned
  ground reads as ZERO — the DEFAULT state of a quicktest map) and whose
  `ShouldCount` opens `if (t.IsNotFresh()) return false;`, so a stack leaves the
  division the instant it finishes rotting with nothing said during the ramp.
  The number holds its value and then falls off a cliff. **That is the M1 death
  shape one system over** — M1 died because `BloodLoss` was truncated out of the
  health read, i.e. a surface showing a number that is not the thing killing
  you. It is still not redefined: it is a shipped predicate target with suites
  asserting on it, and "what the vanilla alert will do" is a real question.
  What ships is `food_days_basis`, a sentence IN THE DATA saying what it does
  not count — `Materials.cs` exists because a true warning in a source comment
  did not stop the code three lines below it drawing a conclusion the agent
  could not check (`54b0c9a`), and this is the same fix applied to the field the
  agent actually reads — plus `resources.food_rot`, which is map-wide and
  carries the clock. **`Materials.Of` is deliberately NOT used**, and its own
  header says why in its last line: "no predicate and no digest section reaches
  this file". It runs `Pawn.CanReach` per stack per builder — a pathfind, the
  disqualifier session 19 names explicitly — and it is per-def where food is
  dozens of defs. The cheap half of the same correction ships instead: a
  `listerThings` walk gives map-wide nutrition with no reachability, and the
  honest consequence is PUBLISHED rather than hidden — `nutrition` is an UPPER
  bound, `nutrition_in_stockpiles` is the LOWER bound, and
  `nutrition_forbidden` narrows the gap for free because
  `Thing.IsForbidden(Faction)` short-circuits on `compForbiddable` and never
  walks a lord. The bands and the clock are the game's own
  (`GenTemperature.RotRateAtTemperature`, and
  `CompRottable.CompInspectStringExtra`'s own 0.001/0.999 cutpoints), so the
  digest says what the player's inspect pane says. **The membership test
  reproduces all THREE of `ResourceCounter`'s clauses, and the third is the one
  that is easy to miss:** `CountAsResource` is what keeps CORPSES out, since
  `ThingDefGenerator_Corpses` sets `ingestible.foodType = FoodTypeFlags.Corpse`
  and `HumanEdible` is `(OmnivoreHuman & foodType) != 0` with `OmnivoreHuman` =
  0x1F3F, which HAS the 0x8 Corpse bit — without it a battlefield lands in the
  larder figure and pins `spoiled_stacks` non-zero forever. They are counted and
  published as `corpse_stacks_excluded` rather than silently dropped. `261f2e9`.
- 2026-09-01 (session 21) — **A quiet in-game DAY is the play loop's normal idle
  unit, so an `until` advance that names no bound gets 60,000 ticks — and an
  already-true predicate with the edge required and no bound is REFUSED, because
  its halt cannot fire.** Evan's ruling, and the reframing is the load-bearing
  half rather than the number: *"a full day without doing anything while you're
  fully set is pretty typical. Lots of things the colony does itself day to day
  and ideally if something bad happens, you'll be woken up, you won't have to
  check."* So the bound is **not a safety net bolted onto an error path** — it is
  the natural idle period of the loop, an advance that runs a day and returns
  `reason:"timeout"` is the system working, and the number's job is only
  "eventually hand control back when nothing interesting happened". The
  consequence is that **the HALTS are the wake-up mechanism and this raises their
  status**: `722c951`'s own-faction casualty halt is the primary interrupt, not a
  guard rail, and the question this bound puts weight on — what ELSE should wake
  the agent (a raid letter, a breakdown, a mood collapse, a food cliff) — is its
  own issue, because a day of quiet is only safe to sleep through if the halt set
  is good enough. **What it replaces was never a decision:** 1.3 shipped 600000
  (ten in-game days) as "big enough not to get in the way", which for anything a
  human would notice is no bound at all. **The refusal, and why the mod already
  knew.** Session 19's edge rule is right (`hour >= 6` is true all afternoon) and
  is not reopened; but `time.tick >= N` is MONOTONE, so once true there is no
  crossing left and the halt is unreachable by construction. Session 19 also
  anticipated this and instrumented it — `true_when_armed`, `saw_false`,
  `first_false_tick` ship in every `until` envelope — so **this was an
  ENFORCEMENT gap, not an observation one**: on a bench on 2026-09-01 the mod
  published `true_when_armed:true, saw_false:false, first_false_tick:null` and
  accepted the unbounded advance anyway, which then ran **187,541 ticks** and was
  stopped only by the casualty halt that had shipped hours earlier, with
  `ok:true`. The refusal is therefore DERIVED from `PathWatch.TrueWhenArmed` and
  `EdgeRequired`, the same two accessors `Report()` publishes, so the envelope
  and the enforcement cannot disagree; and it NAMES `edge:false`, because a
  caller who wrote `time.tick >= now + N` almost always meant "stop as soon as
  this holds" and handing back the right call beats rejecting the wrong one.
  **Both halves are needed, and the reason is a RACE.** Reading the clock and
  arming are two protocol round trips at a 0.25–1 s floor each (`rwa/README.md`:
  500 ms poller, inbox files younger than 250 ms ignored), so at ~30 tps the
  clock moves 60–120 ticks in between and a short `now + N` lead is false when
  computed and true when armed. Refusal alone would make the same call fail or
  not depending on latency — a flaky refusal, not a fix; the default bound is
  what makes a lost race benign everywhere the refusal does not fire. The three
  outcomes are now total: no bound + already true + edge is refused with the fix
  named; a caller's own bound + already true + edge is session 19 unchanged
  (waits for a re-crossing, stops at that bound); everything else gets the
  60,000 default. One extension beyond the literal ruling, recorded because it is
  a widening: a SUPPLIED `timeout_ticks:0` is refused too — it is the same
  unreachable halt with the default explicitly switched off, nothing in the repo
  spells it, and a caller who wants a very long wait can still pass a large
  finite number. `1113019`, `fc287ba`, `722c951`.

- 2026-09-01 (session 21) — **A field that states a false reason is the same
  defect as a read that hides the truth, and three of them shipped in one
  envelope.** `work_coverage.order` said `"under-first, then
  natural-priority-desc"` while `WorkCoverage.Section` made ONE pass and
  appended under-rows inline (`9b179ef`); a dry run's `coverage_after` reported
  the coverage BEFORE the repair, under a name that promises the coverage after
  it, while `repaired` in the same envelope named the promotion that fixes it
  (`58794e4`); and a clean dry run's `Stamp(0)` said "the journal writer is
  closed … treat anything done in this session as unprovenanced" about an open
  writer and a call that mutated nothing (`6fc75e3`). **That is the class this
  whole session is about, one size down.** `61794cd` exists because a health
  read hid `BloodLoss` behind a truncation policy; `7382bdd` because a dropped
  argument reported success; `40ed42f` because a colony's only doctor was its
  own first patient and nothing said so. A field that describes itself falsely
  is worse than an absent field, because prose shipped INSIDE the envelope reads
  as authoritative and invites exactly the shortcut it breaks.
  **The fixes, and each is the data moving rather than the words:**
  `Section` now emits under-covered rows in a first pass and covered rows in a
  second, so the string it always published is true — sorted rather than
  reworded, because under-first is the more useful order for this block's reader
  and the cap already promises never to drop an under-covered row, so a caller
  walking `rows` gets the right answer without consulting `under`. `work-cover`'s
  dry-run arm calls a new `Section(map, projected)` that applies the planned
  promotions to a fresh snapshot, so `coverage_after` answers "would this fix
  it" — the question a dry run is asked — instead of "is it fixed", which in a
  dry run is always no by construction; `still_under`'s `enabled` and `available`
  move to post-call to stop contradicting the `have` beside them, which was
  already `r.Have + did`. And `action` is chosen by the emit GUARD rather than
  by the returned seq: **0 was a sentinel doing double duty** as "nothing was
  owed" and "something was owed and the write failed", which is the
  absent-vs-null trap of the `enabled_but_incapable` entry above wearing
  different clothes. `Journal.Emit` really can return 0, so that case keeps
  `Stamp(0)`'s warning and the no-line case gets `NoStamp()`.
  **Two-under-types could not be staged and is not a fixture oversight:** every
  floor but Doctor's is on CAPABILITY (`!WorkTypeIsDisabled`), which is backstory
  and trait driven and no shipped verb sets it. The suite gets the same
  discrimination from two reads of ONE type (5.6a–5.6c: Doctor sits at its
  natural index when covered and at index 0 when under, so it was moved past a
  covered row of higher natural priority) and proves the multi-row case offline
  against its own comparator (9.3d–9.3h). The suite asserts the AGREEMENT
  between `order` and `rows` rather than either ordering, so the check survives
  the reword the issue offered as the alternative fix, and 9.6f/9.6v/9.6w keep
  the pre-fix banked envelopes as the filed evidence rather than rewriting them.
  `9b179ef`, `58794e4`, `6fc75e3`, `40ed42f`.
- 2026-09-01 (session 21) — **Every letter and every `alert_on` wakes a sleeping
  advance, and it is NOT a severity filter.** Four halts fired unconditionally
  (`casualty`, `dialog`, `red_error`, and `NoticeRedError`'s own path upstream of
  the journal's dedupe cap — the issue's own table wrongly listed a fourth called
  `thermal`; there is no such halt, the governor only steps the SPEED down).
  Everything else sat inside `switch (until)`, so `advance {ticks:60000}` slept
  through a raid landing, a trader arriving and leaving, a quest expiring, an
  inspiration expiring, `Alert_LowFood`, a fire and a prisoner escaping, unless
  the agent had GUESSED IN ADVANCE that today was the day. **And it was worse
  than opt-in: it was MUTUALLY EXCLUSIVE.** `CheckUntilKeys` refuses a second
  matcher, so an agent already waiting on `until:{condition:{…}}` could not also
  ask to be woken by a raid — there was no workaround available even to an agent
  that knew the hazard. I proposed a severity cut (wake on
  `ThreatBig`/`ThreatSmall`/`Death`/`NegativeEvent` and Critical/High alerts, on
  the grounds of noise) and **Evan rejected the framing**: "anything neutral or
  positive should wake you, maybe you want to act on an inspiration, things like
  that. that's how you get propelled into actually playing the game and having
  fun." The rule is **"is there something I might act on", not "is this bad"** —
  an inspiration expires, a trader leaves, a wanderer at the door is a roster
  decision, and a run that only ever wakes for disasters survives ten days
  without ever playing. That collapses the letter half to nothing: halt on EVERY
  letter, no allow-list, because `Verse/LetterStack.ReceiveLetter` is the game's
  own "the player should look at this" (it is where vanilla decides whether to
  pause at all, `Prefs.AutomaticPauseMode >= let.def.pauseMode`) and re-filtering
  it second-guesses the one system that is good at it — while shipping a second
  source of truth, which this project has been burned by twice (`7382bdd`'s
  rejected arg whitelist; the `Build:` tally essay in the workspace CLAUDE.md).
  Noise is smaller than it sounds: on a bench being actively wrecked by an
  acceptance suite, 13,667 ticks produced 53 journal events, of which 3 letters
  and 6 `alert_on`. **THE ASKED-FOR HALT WINS THE NAMING, and the matchers are
  therefore evaluated FIRST.** Both halts fire on the same journal row; what
  differs is the token the caller gets back, and an advance armed `until:{threat}`
  that stopped on a `ThreatBig` letter must report `reason:"threat"`. Running the
  wake first would have renamed every explicit wait to `"letter"` and broken a
  matcher shipped since 1.3 **invisibly**, since the advance still stops at the
  same tick on the same event. `halted_on.armed_by` is `"until"` or `"default"`
  and is present on both, so neither is inferred from an absence — that is the
  field a suite asserts. `until:{event}` is deliberately NOT stamped with
  `kind`/`armed_by`: it matches an arbitrary journal type and `downed`/`death`
  payloads own `kind` themselves (colonist|slave|animal|mech), so stamping would
  rewrite the caller's data; `letter` and `alert_on` have fixed documented key
  sets and can be stamped safely. `280fb78`, `722c951`, `1113019`.
- 2026-09-01 (session 21) — **`alert-mute` is a recorded ACT, permanent until
  released, and visible in the digest; there is no game-side gate and no lapse
  rule, and both were checked rather than assumed.** A letter happens ONCE; an
  alert is a STANDING CONDITION. `alert_on` is already a transition so a chronic
  alert wakes you once per on-cycle rather than continuously, but a condition the
  colony has deliberately decided not to fix still flickers off and on and each
  flicker is a wake for a decision already made. Evan: "the agent should have the
  ability to blacklist what alerts wake them up, while they're playing" —
  RUNTIME, mid-run, not static config. This is session 13's `threat-pardon`
  ruling applied a second time, so the disposition is copied argument for
  argument: required non-empty `reason`, journalled as an `action` row with its
  ids, scribed so it survives save/load, `{}` lists the set and the live
  candidates, `release`/`release_all` are acts too, and a no-op writes no row.
  **THE GATE, honestly:** DESIGN §Action model says a player verb re-implements
  its widget's precondition and cites it, and there is no such widget —
  `RimWorld/AlertsReadout.cs` has no mute, no dismiss and no hide (its only
  per-alert interaction is `Alert.OnClick`'s jump-to-target) and `Verse/Prefs.cs`
  carries no alert preference at all. RimWorld has no concept of an alert you
  have stopped caring about, because a human looks past one. Citing a member
  would dress our invention as the game's, so the precondition is stated as ours
  and made narrow: an id must name a real `Alert` subclass in the loaded
  assemblies (`typeof(Alert).AllSubclassesNonAbstract()`, which covers modded
  alerts free and — unlike `AlertsReadout.allAlertTypesCached` — does not exclude
  the `Alert_Custom` subclasses our own fixtures are). A typo is refused with
  near-misses rather than stored, because a mute that silently matches nothing is
  worse than no mute: the agent believes it is covered. Liveness is deliberately
  NOT required — pre-muting before a long build is the normal case. **NOTHING
  LAPSES IT, and the obvious rule was killed on evidence.** `threat-pardon`
  lapses because the game reifies "still asleep" twice (`LordToil_Sleep`,
  `CompCanBeDormant.Awake`); the analogue here would be "un-mute when it comes
  back at a higher priority", and `Alert.Priority` is
  `public virtual AlertPriority Priority => defaultPriority` with **exactly one
  declaration in the whole decompiled 1.6 tree** — no vanilla alert overrides it,
  so priority is a per-class constant and that rule would never fire once. A
  TIMED expiry was rejected for the stronger reason: an expiry the agent did not
  choose is a wake it did not ask for, at a tick it cannot predict, for a
  decision it already made. **`digest.alerts` publishes `muted` (id, reason,
  live), `muted_count`, `muted_live` and a `muted` flag on each active row.** The
  standalone list is OUTSIDE the `AlertCap` truncation on purpose: the cap drops
  live rows by priority, and a standing decision hidden by a display budget is
  the `[[seek-off-is-a-decision-to-flee]]` failure that `b1b3060` shipped
  `digest.posture` to close — an agent that muted `Alert_LowFood` on day 2 must
  see it on day 8. Uncapped is safe because the list is bounded by what the AGENT
  muted, one act at a time, not by anything the colony can generate. A mute is
  consulted only on the WAKE path: `until:{alert:"X"}` halts on X even when X is
  muted, because "wait FOR this" is a different question from "wake me for this"
  and the one asked this call outranks a standing decision from an earlier day.
  Read from `TimeDriver.Notice` through a static volatile mirror rather than
  `Current.Game.GetComponent`, because that tap is documented "any thread" and
  may not touch Verse. `280fb78`, `b1b3060`.
- 2026-09-01 (session 21) — **`through_news` is a THIRD escape, not an extension
  of `through_casualties`; and the day-long default bound and the wake halts are
  two halves of one decision.** On the escape: they are different decisions and
  one reason string cannot honestly cover both — `through_casualties` says "my
  colonists may fall while this runs and I accept that", `through_news` says "do
  not wake me for things I might act on" — so a post-mortem grepping for who
  accepted casualties must not turn up every run that only wanted to sleep
  through a trade caravan. They are also asymmetric in MECHANISM: one bypasses an
  ARM-TIME refusal and appears in `escaped`/`bypassed`, the other suppresses a
  DURING-ADVANCE halt whose cost is only knowable at the end, so it is reported
  in `news_rode_past` (count kept whole, first 20 events shown) instead. What the
  MUTE swallowed is reported separately as `muted_alerts`, and unconditionally —
  regardless of `through_news` — because that is a standing decision doing its
  work and an agent should be able to watch it happen rather than infer it from
  an absence. `through_news` does NOT defeat an explicit `until:{letter}`, since
  the matchers run first. On the dependency: **`1113019` and `280fb78` were ruled
  together in one conversation on 2026-09-01 and each is the other's
  precondition.** `1113019` makes an unbounded `until` advance default to 60,000
  ticks — one in-game day — on Evan's framing that "a full day without doing
  anything while you're fully set is pretty typical … ideally if something bad
  happens, you'll be woken up, you won't have to check". That default is only
  safe BECAUSE the halts wake you, and the halts are only affordable BECAUSE a
  bound stops a quiet day running forever. `1113019`'s own comment in
  `TimeDriver.Start` named the open question — "what ELSE should wake the agent"
  — and now names `280fb78` as its answer. **Neither should be reverted without
  the other.** `280fb78`, `1113019`.
- 2026-09-01 (M1 re-plan, planner session for `664e9b9`) — **The M1 gate is
  ZERO colonists lost, and a loss does not stop the run.** The old floor
  (">=2 of 3 alive at day 10") tolerated a death; the rewrite does not, for
  three reasons. Every cause of the two M1 deaths is now a mod feature rather
  than a habit — the casualty halt and unread refusal (`722c951`), the bleed
  clock and `triage` (`61794cd`, `40ed42f`), the posture verb (`b1b3060`), the
  wake halts (`280fb78`) — so a death is no longer bad luck the platform could
  not have shown; "thrive" has no meaning with a corpse in it; and a death is
  by this repo's own rule a post-mortem event. The read is two-sided on
  purpose: a journal `death` row with `player:true` catches the kill, and the
  game's `FreeColonists` recorder never dipping below its post-staging value
  catches the losses that write no death row — a colonist who gave up and
  walked off the map, or was kidnapped by raiders. The run PLAYS ON after a
  loss to day 20 and is graded FAILED at the end, because M1 stopped on day 6
  and the four days of evidence it did not collect are what the rest of the
  contract is for.
- 2026-09-01 (M1 re-plan) — **Three verdicts — FAILED, SURVIVED, THRIVED —
  because Evan's "see what you can come up with" is the right instruction to
  the agent and a wrong acceptance bullet.** One pass/fail line would have to
  either make every thrive row a hard floor, which turns "come up with
  something" into a checklist grind, or make them decoration nobody grades. So
  the gate rows are the floor, the thrive rows are each graded, and the summary
  says which of the three the run earned. A post-mortem that cannot say whether
  the colony grew is a failure of the contract, not of the run; each thrive row
  therefore names a read and a direction, never prose.
- 2026-09-01 (M1 re-plan) — **"Thrive" is graded off the game's own recorders
  and the mod's sampler over the WHOLE window, read at day 20 from `history`
  and the durable `samples/` file — never the live ring.** `2d9a1da` shipped
  eleven `HistoryAutoRecorder` series; the M1 post-mortem had to hex-dump them
  out of an autosave. Wealth: `Wealth_Total` `slope_per_day > 0` and
  `Wealth_Buildings` last > first is "the colony built and grew", the two
  series the storyteller itself prices raids from. Mood: the mean of the last
  four `ColonistMood` samples (two in-game days at the 30,000-tick cadence, the
  same window the sampler defaults to) at or above 50 — the series stores
  `CurLevel * 100`, and 50 sits fifteen points above the minor-break line,
  `MentalBreakThreshold`'s `defaultBaseValue 0.35`
  (`Data/Core/Defs/Stats/Stats_Pawns_General.xml`; major is x0.5714 and extreme
  x1/7 of it, `Verse/MentalBreaker.BreakThresholdMajor`/`Extreme`) — plus no
  player-faction `mental_break` in the final five days, because a break is the
  game's own verdict on mood and a mean can hide one pawn at 20%. The ring is
  refused as a source because a load moves `TicksGame` backward and clears it
  (`2d9a1da`); a 20-day run crosses relaunches, so the file is the record.
- 2026-09-01 (M1 re-plan) — **A "real food cycle" is grow -> harvest -> cook
  -> eat inside the window, and the window was checked against the growing
  period rather than assumed.** `Root_Play.SetupForQuickTestPlay` never sets
  `GameInitData.startingSeason`, so `GenTicks.ConfiguredTicksAbsAtGameStart`
  takes `TwelfthUtility.FindStartingWarmTwelfth` — the first twelfth whose
  average is at least 12 C — which on a temperate tile is Spring day 1 (M1
  read "Spring 1", year 5500). Twenty days end on Summer day 5
  (`GenDate.DaysPerQuadrum 15`), so `PlantUtility.GrowthSeasonNow` (cell
  temperature above `minGrowthTemperature`, default 0) holds throughout.
  Growth accrues only outside `Plant.Resting` — `DayPercent` 0.25 to 0.8,
  55% of the day — and at reduced rate below `minOptimalGrowthTemperature`
  (`Plant.DefaultMinOptimalGrowthTemperature` 6), so a def's `growDays`
  understates the sow-to-harvest time by 1.8x or more: rice 3 -> about 5.5
  days, potato 5.8 -> about 10.5, corn 11.3 -> about 20.5 and does not fit
  (`Plants_Cultivated_Farm.xml`). Two rice cycles fit; one potato cycle fits.
  **So staging stages NO food beyond the scenario's own 44 survival meals** —
  about eight colonist-days at the ~1.6 nutrition a colonist eats per day —
  which makes production load-bearing by day 8 instead of optional until
  day 21, which is the only way the row can fail. The proof is three
  inferential reads (zone growth, the raw crop in `things` with no trade row,
  a meal def rising beside a live bill) because no observer reads
  `Pawn_RecordsTracker`; `ae78ecc` is filed for the direct one.
- 2026-09-01 (M1 re-plan) — **Rooms are graded by `room.role` as the game
  scores it, and the scoring constants are what make "design a rec room" a
  test rather than a claim.** `RoomRoleWorker_RecRoom` scores 7 per building
  named by any `JoyGiverDef` (all count by default — `JoyGiverDef
  .countsForRecRoom = true` — so HorseshoesPin, HoopstoneRing, BilliardsTable,
  GameOfUrBoard, ChessTable, PokerTable, the three televisions, Telescope),
  while `RoomRoleWorker_DiningRoom` scores 12 per eat surface: a table in the
  rec room needs two joy buildings beside it or the room is a dining room.
  `RoomRoleWorker_Workshop` scores 27 per bench whose def carries
  `workTableRoomRole Workshop` (the `BenchBase` default in
  `Buildings_Production.xml`: tailoring, smithy, stonecutter, machining,
  sculpting, butcher and more), while `RoomRoleWorker_Laboratory` scores 60
  per research bench and `RoomRoleWorker_Kitchen` 28 per bench with a
  human-edible product — a research bench or a lone butcher table in the
  workshop flips its role. `RoomRoleWorker_Barracks` is 100100 per humanlike
  bed once `RoomRoleWorker_Bedroom.IsBedroom` is false, which it is from two
  owned beds by non-partners up — three colonists' beds in one room read
  `Barracks`, and `owners_total == roster` is "for all of their pawns".
  "Colonist-built" is placement ids in `action` rows plus `construction`
  `completed` rows after the staging watermark, and because a room id dies at
  the next region rebuild (`d16a463`) the graded rooms are identified by an
  interior cell, not an id carried across days.
- 2026-09-01 (M1 re-plan) — **Militia: armed and postured are gated, armoured
  is narrative, and the raid comes from the storyteller's own schedule rather
  than a `dev:incident`.** Armed is `pawn {sections:["equipment"]}` `primary`
  per violence-capable colonist (the denominators `digest.posture` already
  publishes); postured is `digest.posture.ok` at every daily snapshot.
  Armoured has no read — `PawnSerializer.Apparel` publishes no armor rating —
  so it is reported by def name and `47547ca` is filed; a def-name list is not
  written into the contract because it would call a devilstrand duster
  unarmoured. The raid: `StorytellerComp_ClassicIntro` fires `RaidEnemy` at
  `IntervalsPassed == 324` — tick 324,000, day 6 — at 40 points with
  `raidForceOneDowned`, whenever `Difficulty.allowIntroThreats` holds (default
  true; only `Peaceful` sets it false), and Cassandra's ThreatBig
  `StorytellerCompProperties_OnOffCycle` opens at `minDaysPassed 11` with
  `onDays 4.6`, `numIncidentsRange 1~2` and `forceRaidEnemyBeforeDaysPassed
  20`. Two raids are therefore expected in twenty days — M1 stopped at tick
  322,314, 1,686 ticks short of the scripted one, which is why it "never saw a
  raid". If the schedule somehow fails, the row reads "not exercised"; a
  `dev:incident` after staging would break G2 to rescue G5, and M1's original
  "schedule it during staging" is not spellable — `dev:incident` has no delay
  argument.
- 2026-09-01 (M1 re-plan) — **Research, weapons crafted and armour are
  REPORTED, not graded, and "prison" is out of scope by measurement.** No
  floor for research or crafting survives the skill roll of three random
  crash-landers, and a floor would push the agent to grind research instead of
  Evan's "whatever they can". Prison is excluded because `grep -rn ForPrisoners
  Source/AutoRimmer/` is three reads and no write (`e1c072e` comment #2), so
  the capture chain breaks in the middle and recruitment is unreachable;
  caravans, DLC management and animal training are DESIGN non-goals or
  deferred; burial has no verb and is reported, never failed.
- 2026-09-01 (M1 re-plan) — **Escapes and mutes are counted with their
  reasons and NOT capped.** `722c951`'s own ruling stands: a per-run budget
  needs a threshold, no number is defensible before a real run supplies one,
  and a guessed threshold is what this project keeps deleting. This run is the
  one that supplies the numbers; the contract requires every `unread_ok`,
  `through_casualties`, `through_news` and `alert-mute` to appear in the
  summary with its reason, which is the measurement the wall waits for.

- 2026-09-02 (repair round) — **`Room.Owners` yields NOTHING for a barracks
  with more than one owned bed, by vanilla's own design, so `daa269a`'s
  `owners_total: 0` is the game's semantics and not a swallowed exception.**
  `Verse/Room.cs` `Owners`, read by member name:

  ```csharp
  if (TouchesMapEdge || IsHuge || (Role != Bedroom && Role != PrisonCell
      && Role != Barracks && Role != PrisonBarracks)) yield break;
  var beds = ContainedBeds.Where(x => x.def.building.bed_humanlike);
  if (beds.Count() > 1 && (Role == Barracks || Role == PrisonBarracks)
      && beds.Where(b => b.OwnersForReading.Any()).Count() > 1) yield break;
  ```

  Room 38 of run `m1-20260901` is a Barracks with three humanlike beds, three
  of them owned, so it takes the second `yield break` exactly. The issue
  offered two candidates — "Owners may deliberately yield nothing for a
  Barracks" and "the enumeration is throwing into the bare `catch {}`". The
  first is right in substance and wrong in detail, and the detail decides the
  fix: the gate is not the Barracks role, it is **more than one owned bed**.
  A barracks with exactly ONE owned bed yields that owner normally, which is
  why this never showed up before a three-colonist room.

  Consequences, all of which bind the fix:

  1. The room-level rollup must be derived from `ContainedBeds` /
     `OwnersForReading` — the same route the `beds[]` block at
     `PlaceVerbs.cs:173-192` already uses and gets right. `room.Owners` is the
     wrong source for the question "who lives here" and cannot be repaired by
     catching harder.
  2. `TouchesMapEdge` and `IsHuge` are two further silent-empty conditions
     nobody had accounted for. A map-edge-touching barracks reports no owners
     for a third distinct reason.
  3. The bare `catch {}` around the enumeration is a real latent hazard and is
     NOT the cause here. It stays worth replacing with one that records that it
     fired, per the issue's acceptance item 2, but fixing it alone would have
     changed nothing and would have looked like a fix.
  4. Vanilla's `Owners` is still the right source for the single-owner
     *bedroom* question. It is not wrong; it answers "whose room is this",
     while the mod was asking "who sleeps in here". Two different questions
     that agree on every room with fewer than two owned beds.

- 2026-09-02 (repair round) — **Layout enclosure is fully decidable today, and
  there are TWO ways the freezer failed, not one.** `a1644d6` (B-3) asks for
  enclosure reporting on a placed layout. Resolved by investigation before
  dispatch, because the issue does not say what "enclosed" means and a worker
  would have had to choose.

  **The enclosure test is the game's own `ProperRoom`**, `Verse/Room.cs`:

  ```csharp
  public bool ProperRoom {
    get {
      if (TouchesMapEdge) return false;
      for (int i = 0; i < districts.Count; i++)
        if (districts[i].RegionType == RegionType.Normal) return true;
      return false;
    }
  }
  ```

  A layout that never closed leaks into the map-wide outdoor room, which
  touches the map edge, so `ProperRoom` is false. This is exactly what run
  `m1-20260901` saw: `room-at` on the freezer returned `outdoors: true` with
  `cells: 60082` — the whole outdoors, not a 60,000-cell freezer. Do not invent
  a flood fill; call the game's member and cite it, per the standing
  "the gate lives in the widget" rule.

  **The second failure mode, which the issue does not name.** A room can be
  properly enclosed and still be thermally outdoors:

  ```csharp
  public bool UsesOutdoorTemperature =>
      TouchesMapEdge || OpenRoofCount >= Mathf.CeilToInt(CellCount * 0.25f);
  ```

  A quarter of the roof missing puts the room on outdoor temperature with
  `ProperRoom` still true. For a FREEZER — the whole point of B-3 — that is the
  same colony outcome by a different mechanism, and a check that only reports
  `ProperRoom` would pass a freezer that cannot hold cold. Both must be
  reported, distinctly. `PsychologicallyOutdoors` is a third, mood-facing
  reading with different thresholds and is not the enclosure question.

  **The layout to cells link already exists and needs no new IR field.**
  `LayoutVerbs.Layouts.Open(...)` records a `CellRect` per placed layout and it
  survives across days — run `m1-20260901` called
  `construction {layout_id:"ly-1"}` on day 40+ successfully. Intended interior
  cells are the record's rect minus the cells occupied by declared `Wall` and
  `Door` elements; intended roofed cells are the IR's own `roof` mask, which
  every shipped template already carries.

  **Naming the gap is therefore a comparison, not a search.** A declared `Wall`
  or `Door` cell with no edifice standing (or with an unbuilt blueprint or
  frame) is the hole, and a declared roofed cell that is unroofed is the
  thermal hole. Both are answerable from data the mod already holds, so
  acceptance item 2's "name the gap" does not require pathfinding.

  Note a door does NOT break enclosure — an unbuilt door does. The gap check is
  about what stands at the cell, not about the def's type.
- 2026-09-02 (session 22) — **The transcript cap is the WIDTH OF THE STEP
  COUNTER, so the fix for a full run is to rotate rather than to raise it.**
  `rwa`'s `Transcript.step` refused a 1,001st step with a bare `RuntimeError`,
  and run `m1-20260901` lost every call from in-game day 31 onward to a Python
  traceback rather than an envelope (`5eba561`). Two questions came with it —
  how big should the cap be, and what should the client do at it — and the
  first one answers itself once you look at who reads the directory. Every
  consumer orders steps by SORTING the directory names (`rwa replay`,
  `accept/4.2-play-loop.py`, and any `ls`), the names are zero-padded to three
  digits, and `1000-ping` sorts BEFORE `999-ping`. Widening the field would
  silently reorder every transcript on disk; a cap at the end of the field
  costs nothing once the client keeps going past it. So the cap stays at
  **999** — one fewer than the old message claimed, because the counter starts
  at 001 — and rotation makes it a non-event: `<run>` is segment 0, and the
  client continues in `<run>-s01`, `-s02`, … with `prev`/`next` in each
  segment's `meta.json` and an `rwa:rotate` line in both logs. **The suffix
  scheme is the shell workaround's, deliberately**: `m1-20260901` and
  `-s00`..`-s03` are already on disk and a consumer has to walk what exists,
  so a prettier `-002` would have orphaned the only run this has ever
  happened to. The general rule the bug is an instance of: **client policy in
  a caller's shell script is a defect in the client** (spec 1.4), and the
  workaround that unblocked the run — pick the highest `-sNN` under 940 steps
  — was policy, in bash, in the run's own wrapper. **`--no-rotate` is the
  opt-out and is now the only way to reach the cap**; it answers
  `rwa-transcript-full` in the client's own envelope shape with `sent:false`,
  because the step directory is claimed BEFORE the inbox write and "nothing
  was dispatched" is the most useful thing the refusal can say. Exit 2, with
  the usage errors, not 1: a full directory is a fact about the invocation and
  must not be confused with `ok:false` from the colony. The consumer side
  moved with it — `--transcript` takes a glob or a chain — because auditing
  the head of a chain is the dangerous shape rather than the incomplete one:
  on `m1-20260901` it reports `113 advances within policy` where the whole run
  FAILs the wedge rule. `5eba561`.
- 2026-09-02 (git-bug eef837a) — **A DEF-LEVEL FILTER SUMMARY IS NOT AN ANSWER
  ABOUT A THING, and the gap between the two killed a colony.** Run
  m1-20260901 lost three colonists to a `ButcherCorpseFlesh` bill that reported
  `suspended:false`, an unlimited search radius, and an ingredient filter whose
  `allowed_defs` contained `Corpse_WildBoar` — with a wild boar corpse standing
  on the butcher spot's own cell. Every one of those readings was true. What
  rejected the corpse was `ButcherCorpseFlesh.fixedIngredientFilter`'s
  `<specialFiltersToDisallow><li>AllowRotten</li>`, evaluated PER THING by
  `Verse/ThingFilter.cs Allows(Thing)`'s last clause
  (`disallowedSpecialFilters[i].Worker.Matches(t)`, i.e.
  `CompRottable.Stage != Fresh`), and consulted by `RimWorld/Bill.cs
  IsFixedOrAllowedIngredient` **before** the bill's own filter. The save proves
  it: `day-62.rws` has `Corpse_WildBoar` at `(114, 0, 138)` — the spot's own
  cell — unforbidden, `rotProg 183767`, past `CompRottable`'s 150,000-tick rot
  start. The agent read "hp 95%" and called it fresh, because hit points are
  not rot.

  **The general rule, which is bigger than bills.** Wherever this mod
  summarises a `ThingFilter`, the summary answers a question about DEFS and the
  game answers a question about THINGS, and the two differ by exactly the
  clauses a def cannot carry: hit points, quality, and the special filters.
  Publishing the def summary and calling it "the filter" was the defect. So
  every filter summary now carries both special-filter lists — its own and its
  universe's — and a bill additionally carries `ingredient_match`, which runs
  `WorkGiver_DoBill`'s own predicate over the things actually on the map and
  NAMES the clause that rejected each one. `health` folds that into one word
  and `remedy` names the verb that fixes it, including the case where the
  honest answer is that no verb does: no `bill-set` lever can widen past
  `recipe.fixedIngredientFilter`, and an agent that does not know that will
  retry it forever, which is what happened.

  **Two of the issue's own premises are false, and the artifacts say so.**
  (a) `bill-add` was never broken: `BillUtility.MakeNewBill` ends in the
  `Bill(RecipeDef, Precept)` ctor, whose body is
  `ingredientFilter.CopyAllowancesFrom(recipe.defaultIngredientFilter)`, and
  `day-46.rws` — the save from the day the bill was created — has it at 115
  allowed defs, every one an animal corpse, no `Corpse_Human`, no mech corpse:
  `defaultIngredientFilter: CorpsesAnimal` exactly. (b) `bill-set` did persist:
  `day-66.rws` holds 127. What was WRONG was the number the verb reported — see
  the next entry. Both premises came from reading `filter: null`, which is not
  a shape `bills` has ever emitted; the key it emits is `ingredient_filter`,
  and it was set inside a bare `try/catch` as the last statement of `BillLine`,
  so a throw left it ABSENT. Absent and null are the same thing to every
  consumer, and the run could not tell "this bill has no filter" from "the
  summary failed" from "the filter is empty". All three are now different
  words in `filter_state`, and the key is unconditional.
- 2026-09-02 (git-bug eef837a) — **A widget gate that is only half enforced is
  worse than none, because the half that is missing is the half that reports a
  number.** `StorageFilterOps.Toggle`'s per-DEF path refused a def the parent
  filter disallows, citing `Listing_TreeThingFilter.Visible` — the widget draws
  no row, so the player has no such checkbox. Its per-CATEGORY path did not, on
  the recorded argument that "the allowance is dead for anything the parent
  rejects, so this matches vanilla exactly". For a storage filter that is true.
  For a BILL it is false, because `Bill.ExposeData` DELETES those allowances
  during the saving pass. Measured on the run's own saves: `bill-set
  {allow:["Corpses"]}` reported `defs_delta: 39` over a base of 115 (and
  printed `allowed_defs: 154`, internally consistent); the next save left 127.
  **Twenty-seven of the thirty-nine evaporated** — every mechanoid and drone
  corpse def, which is exactly what `fixedIngredientFilter.disallowedCategories`
  names. A verb that reports a delta which is not true for longer than one
  autosave is lying, and DESIGN's 2026-08-31 write-on-save entry had already
  identified the mechanism without following it to this consequence.

  The fix uses the game's own parameter rather than inventing one:
  `ThingFilter.SetAllow(ThingCategoryDef, bool, exceptedDefs, exceptedFilters)`
  takes the excluded set as its third argument, and `Listing_TreeThingFilter`
  passes `forceHiddenDefs` there for the same reason. `clampCategories` is ON
  for bills and OFF for storage, and the withheld defs become `refused` lines
  naming the mechanism. `defs_delta` is now the delta that survives a save;
  `will_not_persist` re-runs ExposeData's predicate as a QUESTION after every
  write, so the claim is asserted per call rather than trusted.
- 2026-09-02 (git-bug d9d6c12) — **A timestamp is not a state, and this one
  could not be read as one even by a caller who knew the convention.**
  `Bill.nextTickToSearchForIngredients` has exactly one writer
  (`WorkGiver_DoBill.StartOrResumeBillJob`'s failed-search branch,
  `TicksGame + ReCheckFailedBillTicksRange.RandomInRange`) and
  `ReCheckFailedBillTicksRange` is `new IntRange(500, 600)`. **Ten game
  seconds, rearmed on every failure.** So the field CANNOT "sit in the future
  for days", as both the m1-20260901 post-mortem and the issue say — what it
  does is sit in the future essentially always while a bill is starving, which
  is worse, because one sample cannot distinguish one failed search from ten
  thousand and the raw number invites the reader to think it can. The
  post-mortem's `3606060` was about 1,000 ticks ahead of `now`, not days.

  The consequence for observers generally: **a published field that requires
  the caller to hold a second field to interpret is not published.** `bills`
  now emits `ingredient_search` — a named state, the wake tick, the wait, and a
  consecutive-failure count derived by AutoRimmer's own 250-tick sampler
  (`BillWatch`), sampling faster than the minimum back-off so no distinct
  failure can slip between two samples. The count says in the same block that
  it is a floor observed since the watch armed, because a number that pretended
  to be the colony's whole history would be the same class of lie. The raw tick
  is still published beside it: `32b9e01` uses it as proof and that use is
  unaffected.

  **And the two sleep states are different words.** `asleep-will-retry` (backed
  off, and there IS a usable ingredient) versus
  `asleep-no-matching-ingredient` (backed off, and nothing on the map can ever
  satisfy it). Only the second needs the agent, and presenting them identically
  is what let twenty days pass.

- 2026-09-02 (repair round, worker) — **The enclosure check keys on
  `GetExpectedRegionType`, not on `GetEdifice`, because A WALL FRAME IS AN
  EDIFICE.** Implementing `a1644d6` against the resolution above turned up one
  fact that decides the whole gap check.
  `RimWorld/ThingDefGenerator_Buildings.NewFrameDef_Thing` copies
  `building.isEdifice` from the finished def:

  ```csharp
  thingDef.passability = def.passability;
  if ((int)thingDef.passability > 1) thingDef.passability = Traversability.PassThroughOnly;
  thingDef.fillPercent = 0.2f;
  thingDef.building.isEdifice = def.building.isEdifice;
  ```

  So a **wall frame registers in `Verse/EdificeGrid`** and `c.GetEdifice(map)`
  is non-null at it — while the same generator clamps `Impassable` down to
  `PassThroughOnly` and sets `fillPercent = 0.2f`, so the cell is walkable and
  the room leaks straight through it. A gap check written as "is an edifice
  standing here" therefore reports a half-built wall as SEALED, which is the
  original defect with extra steps. The check calls the region builder's own
  predicate instead, `Verse/RegionTypeUtility.GetExpectedRegionType`, and a
  declared shell cell closes the room exactly when that answers `None` (a
  `Fillage == Full` wall) or `Portal` (a door). Three nuances then fall out
  rather than being special-cased: a built door does not break enclosure and an
  unbuilt one does; a wall frame reads open; and a sandbag in a wall slot reads
  `ImpassableFreeAirExchange`, which is open, correctly.

- 2026-09-02 (repair round, worker) — **"Intended roofed" is the layout's
  DECLARED INTERIOR, not the IR's roof mask, because `place-layout` does not
  receive the mask.** `8c8680a` says the intended roof is "the IR's own `roof`
  mask, which every shipped template already carries", and the templates do
  carry one — but the mod never sees it: `place-layout` takes a RESOLVED
  element list and `LayoutVerbs`' header records the roof as a deliberate
  non-consumption ("a roof is a DESIGNATION, not a placement"). Adding a `roof`
  argument would change that verb's contract to serve a report.

  Taken as the cells the declared shell encloses instead. For a room layout
  that is the same set the mask would give; it needs no new IR field; and it is
  the set `UsesOutdoorTemperature` is actually counting against. Where the
  built room is LARGER than the declared interior — two layouts sharing a wall,
  a room extended by hand — the room's own `OpenRoofCount` can exceed the named
  holes, so both numbers are published and `unroofed_note` says which is the
  floor.

- 2026-09-02 (repair round, worker) — **A layout "intends a room" is decided
  from the DECLARATION ALONE, by a flood over the layout's own rect, and that
  is the cry-wolf guard.** `a1644d6`'s roll-ups have to answer "which placed
  layouts should be rooms and are not", and the tempting test — "it has walls
  and its interior is not a proper room" — reports a defensive wall, a conduit
  spine and a row of solar panels as failed rooms forever. A report that fires
  on things that are not regressions is worse than the silence it replaces,
  because the agent learns to ignore it (the rule `f1a1700` is parked under).

  So: flood the layout's rect 4-connected, blocked by the declared `Wall`/`Door`
  cells; a component that never reaches the rect perimeter is a declared
  interior. `intends_room` is "at least one such component exists". It contains
  **no game state at all** — same answer on the day of placement and on day 40,
  independent of what has been built — and it is bounded by the rect
  (`LayoutEnclosure.CellCap`, one budget for the whole roll-up because `digest`
  rides on it). A straight wall's flood escapes on both sides, so it is never
  listed. This is the file's ONLY search; the enclosure question itself is
  still `ProperRoom`, per the gate-lives-in-the-widget rule.

  Verified offline against all three shipped templates
  (`accept/a1644d6-enclosure.py --selftest`): `freezer-kitchen` declares 2
  rooms / 32 interior cells, `bedroom` 1 / 15, `power-room` 1 / 25, and a
  5-cell wall run declares 0. The freezer template is only checkable because
  `Cooler` is `fillPercent 1.0` / `Impassable` in Core's
  `Buildings_Temperature.xml` and so seals a wall slot exactly as `Wall` does —
  had it not been, the flood would escape through the north wall and the one
  template that shipped this failure would have been silently exempt.

- 2026-09-02 (repair round, worker) — **`rooms` cannot report a room that does
  not exist, which is why `daa269a`'s roll-up and `a1644d6`'s roll-up are on
  different keys.** `rooms` lists `map.regionGrid.AllRooms` and skips
  `PsychologicallyOutdoors`, so an unclosed freezer is not a row in it — its
  cells belong to the map-wide outdoor blob that the verb filters out. The
  failing LAYOUTS therefore ride alongside the room list on
  `layouts_unenclosed`, with one gap cell named per row, and the same rows
  appear on `digest.construction.layouts_unenclosed` under the presence-is-the-
  signal rule. The digest is the read the play loop makes unconditionally, and
  B-3's bar is that the agent is told *without asking cell by cell*; a key only
  `construction {layout_id}` carried would have needed the agent to already
  suspect the layout, which is exactly what run `m1-20260901` never did.

- 2026-09-02 (repair round) — **CORRECTION to `f1a1700`'s filed premise, which
  the orchestrator wrote and which was wrong.** The issue said `Room.ID` "is
  assigned on rebuild, and `Map.MapUpdate` calls
  `TryRebuildDirtyRegionsAndRooms` every frame, so 'room 52' is not the same
  handle across days." The call happens every frame; the rebuild does not.
  `Verse/RegionAndRoomUpdater.cs`:

  ```csharp
  public void TryRebuildDirtyRegionsAndRooms() {
    if (working || !Enabled) return;
    working = true;
    if (!initialized) RebuildAllRegionsAndRooms();
    if (!map.regionDirtyer.AnyDirty) { working = false; return; }
    ...
  }
  ```

  `RegionDirtyer.AnyDirty` is `dirtyCells.Count > 0`, so an undisturbed map
  early-returns, and where cells ARE dirty `CreateOrAttachToExistingRooms`
  reuses the existing `Room` via `FindCurrentRoomNeighborWithMostRegions`.
  **`Room.ID` is therefore stable across ordinary play.** What destroys it is a
  LOAD — `!initialized` takes `RebuildAllRegionsAndRooms()`, every room is
  re-made and `nextRoomID` is not scribed. Run `m1-20260901` spanned bench
  relaunches, which is why its room ids moved.

  The conclusion (park `f1a1700`) survives, but for a different and narrower
  reason, and one branch of it is now cheap:

  1. A **durable handle already ships**: `Spatial.cs`'s `LandmarkComponent` is a
     `GameComponent` WITH `ExposeData`, scribing named cells, and
     `templates/freezer-kitchen.md` already instructs the agent to register one.
     The handle was never the missing piece.
  2. The genuinely open question is the **BASELINE** — "this room used to be a
     Barracks" is new scribed state, which `d16a463` and this round's `f9dadc7`
     ruling both rule out without Evan.
  3. `f1a1700` item 2 (a destroyed building of consequence is reported) needs
     **no room identity at all** — a Harmony destruction postfix of the same
     shape as the five already in `JournalHooks.cs` closes it with zero
     persistence. That half is separable and cheap.

  Recorded because the wrong version was filed on an issue and would have sent
  the next reader hunting for a stable handle that already exists.
- 2026-09-02 (git-bug e08c3e5) — **THE SKILL CEILING IS A FOURTH TRIAGE
  BRANCH, not a variant of the first, and the state token is `no-builder`.**
  `construction`'s precedence becomes **blocked > in-progress > no-builder >
  awaiting-materials > ready**, and every consumer (`ConstructionVerbs.Item`,
  `digest.construction`, `advance {until:{layout}}`'s `unresolved_items`,
  `ConstructionWatch`) reaches it through the one `State(...)` call, as before.

  **Where it sits, and why, is the game's own order.**
  `RimWorld/GenConstruct.cs CanConstruct` tests `FirstBlockingThing` first and
  the `checkSkills` clause several clauses later, so `blocked` stays above it.
  It sits below `in-progress` because a pawn with a job on the thing is a true
  statement about this tick — but the `skill` block is published on the row in
  every state, so the fact never disappears while that is true, and
  `digest.construction.skill_blocked` counts the verdict independently of the
  state precedence for exactly that reason.

  **The prerequisite is read off `t.def`, the BLUEPRINT's def, not the built
  def.** `CanConstruct` reads `t.def.constructionSkillPrerequisite`;
  `RimWorld/ThingDefGenerator_Buildings.cs` copies the field onto the generated
  blueprint def `if (!isInstallBlueprint)` and unconditionally onto the frame
  def. So a `Blueprint_Install` genuinely has NO skill gate, and reading the
  built def would have invented one for every reinstall. A preflight (`build`,
  `place-layout`) has no thing yet and asks the `BuildableDef`, which is what
  `Designator_Build` reads.

  **BOTH prerequisites must be met by the SAME colonist**, so two maxima do not
  decide it — `Designator_Build.DrawPlaceMouseAttachments` loops
  `FreeColonists` testing both levels on one pawn before drawing
  `NoColonistWithAllSkillsForConstructing`. The implementation loops the roster
  and short-circuits on a def with no prerequisite at all, which is every
  buildable in the M1 corpus but `Heater` (5), `Cooler` (5) and
  `WoodFiredGenerator` (4).

  **THE ISSUE'S STATED MECHANISM IS INCOMPLETE, and the correction changes the
  design.** e08c3e5 says "no pawn can take the job, so
  `WorkGiver_ConstructDeliverResources` never hauls the component". That is true
  of the CONSTRUCTION-workType giver (`ConstructDeliverResourcesToBlueprints`,
  whose `JobOnThing` passes `def.workType` into the overload that sets
  `checkSkills: workType == WorkTypeDefOf.Construction`). But
  `Core/Defs/WorkGiverDefs/WorkGivers.xml` declares the SAME class a second time
  as `DeliverResourcesToBlueprints` with `<workType>Hauling</workType>`, where
  `checkSkills` is FALSE — and
  `WorkGiver_ConstructDeliverResources.IsNewValidNearbyNeeder` passes
  `checkSkills: false` too. So a hauler can stock a skill-gated blueprint,
  leaving a Frame that `ConstructFinishFrames` will never finish. **The gate
  therefore wears TWO wrong costumes**, `awaiting-materials` and `ready`, and
  the README's triage table sent an agent down a different wrong branch for
  each. That is the whole argument for `no-builder` outranking both.

  **NEITHER `build` NOR `place-layout` REFUSES on a skill shortfall.** The issue
  argues "the material shortfall precedent argues for refusing without
  `--partial`"; read `LayoutVerbs.PlaceLayout` and the precedent says the
  opposite. `failed` — the only quantity the place/refuse invariant keys on —
  counts parse errors, `SiteGate` verdicts and self-overlaps; `MaterialBill`
  runs after it and only fills `data`. Run m1-20260901's barracks was priced,
  found 6 steel short, corrected and placed; a shortfall has never refused
  anything. A skill ceiling is even less permanent than a shortage — it is
  cleared by a colonist levelling up, which happens by building the other 32
  elements — so refusing would break "place the room, build what you can" for a
  condition that resolves itself. `place-layout` publishes `skill_shortfall[]`
  (always present, shaped like `shortfall[]`, so `[]` means checked-and-clean)
  and `build` publishes `skill` when the def is gated.

  **Mechs are NOT considered**, said in `skill_basis.not_asked` rather than
  silently: `CanConstruct`'s `p.IsColonyMech` branch reads
  `RaceProps.mechFixedSkillLevel` and
  `Designator_Build.AnyMechWithSkillsRequired` asks
  `MechanitorUtility.AnyPlayerMechCanDoWork`, while `MapPawns.FreeColonists` is
  Humanlike-only. A mech colony's real ceiling can be HIGHER than reported.
  Neither are WORK SETTINGS: a colonist with the skill and Construction switched
  off is the README's existing work-priority branch and must not be dressed up
  as a skill ceiling.

  **Verified against the run's artifacts, and the artifacts say MORE than the
  issue.** `RUNS/m1-20260901/journal/20260902T002505.ndjson` seq 42 places
  `ly-1`'s 33 elements at tick 1023 with `pl-23` = `Heater` at `[111,137]`; 32
  completed by tick 12687 and `pl-23` completed at **tick 60755, worker Lacey**
  — after `summary.md`'s recorded human intervention raising Lacey's
  Construction 4 -> 8. `Buildings_Temperature.xml` has
  `Heater.constructionSkillPrerequisite 5` and a cost list of `Steel 50 +
  ComponentIndustrial 1`, matching `missing` exactly; `Cooler` is 5 as well. The
  colony's scenario grants exactly 30 `ComponentIndustrial`, and no save carries
  a `<forbidden>` element on any component stack. The quoted `construction`
  envelope itself is NOT in the artifacts (no save exists inside the Heater's
  window), so the literal "30 unforbidden reachable" figure is corroborated
  rather than read back — but completion-by-Lacey-after-the-raise is stronger
  evidence than the envelope would have been.

- 2026-09-02 (git-bug f9dadc7) — **A SCALAR TELLS YOU THE SIZE OF A SET, NEVER
  ITS AGE — and the fix publishes what it does NOT know.**
  `digest.construction.awaiting_materials` sat flat while the agent read it
  every turn, and fifteen identical reads and one read were the same envelope.
  **Measured rather than assumed: it was 22 for TWENTY consecutive in-game days,
  days 38 to 57 of run m1-20260901, not the fifteen the issue claims** — and
  that window carries no `more`/`cap`/`cap_note`, with 22-25 elements against a
  60-item scan cap, so every one of those twenty readings was a true census and
  not a floor. The same directory shows a flat 60 across days 13-18 and a flat
  24 across 21-24 and 31-36. This is the shape of the whole run.
  (`digests/day-1.json` is a mislabelled day-62-era snapshot and days 2-9, 28,
  32 and 35 are absent, so 12 of 66 day-boundary digests are effectively
  missing — worth knowing before quoting that directory.)

  **The state lives in memory and NOWHERE ELSE.** `AgentGameComponent` has no
  `ExposeData`, so nothing it holds survives a save/load, and the obvious repair
  — scribing a tracking dictionary — is refused here: writing scribed state from
  the observation surface is a live hazard class (git-bug d16a463,
  `_mp/DETERMINISM.md`). `ConstructionWatch` is therefore `BillWatch`'s shape one
  surface over: a static dictionary keyed by `thingIDNumber`, sampled from
  `GameComponentTick` every 2,500 ticks, cleared by `GameBoundary`.

  **THE THIRD STATE IS THE WHOLE POINT.** `stalled` is a TRI-STATE — `true` (in
  this state for at least two in-game days) · `false` (observed entering it more
  recently) · **`null` (tracking cannot answer yet)** — and `null` is never
  "clean". `age_basis` says which kind of age it is: `observed-transition` is
  exact, `since-first-seen` is a FLOOR because the element was already in that
  state when tracking began, `not-tracked` is no measurement at all. FIRST SIGHT
  COUNTS NOTHING, exactly as BillWatch's rule reads, and for the same reason. A
  floor OVER the threshold is still proof; a floor under it proves nothing and
  must answer `null`. Per [[acceptance-suites-must-prove-shapes]], an absent or
  zero age reading as healthy is the original defect repeated one layer in.

  **The threshold is elapsed time, not a calendar count.** The issue asks for
  ">= 2 day boundaries". A half-open window of exactly `2 * GenDate.TicksPerDay`
  contains at least two multiples of `TicksPerDay`, so `age >= 120000` implies
  two boundaries crossed and never the reverse — the conservative direction, and
  it costs no `WorldGrid.LongLatOf` on a hot path. `GenLocalDate.DayOfSeason`
  (what `digest.time` publishes) is offset from `TicksGame % 60000` by the start
  hour and the tile's longitude, so counting its increments would have needed a
  per-element longitude lookup to gain nothing.

  **The key is the live thing's `thingIDNumber`**, so a blueprint that becomes a
  Frame — or the blueprint `Frame.FailConstruction` puts back — starts a new
  clock. Both are real state changes (the materials arrived; the work was lost),
  so losing the age there is correct rather than a limitation. The case the
  issue exists for, a blueprint nobody ever touches, keeps one id for its whole
  life.

  **`stalled[]` covers four states, two more than the issue names.**
  `awaiting-materials` and `blocked` are the contract; `no-builder` is added
  because it is EXACTLY the run's own Heater and excluding it would drop the
  headline example out of the headline report, and `ready` is added because
  materials-present-and-nobody-on-it for two days is the README's third triage
  branch and a genuine stall. `in-progress` is excluded: the clock restarting
  when a pawn picks the thing up is the correct behaviour.

  **The cost, and why it is where it is.** The sampler probes through
  `ConstructionVerbs.Probe`, which makes NO `GetStatValueAbstract` call — the
  state token does not depend on `WorkToBuild`, which is the whole reason it can
  run inside the tick loop. Display facts (def, cell, layout id, `why`) are
  cached on the row at first sight, so the digest's roll-up is a dictionary walk
  with zero Verse access — and it therefore covers the sampler's 300-item window
  rather than the digest's own 60-item one. `Placements.For` and
  `Layouts.Owning` are both linear scans and are walked ONCE per element, at
  first sight, never per sample.

  **One vocabulary for two issues, deliberately.** e08c3e5 adds the skill branch
  and this issue adds the time dimension to the same triage, so both land as
  `state` + a one-sentence `why` (`ConstructionVerbs.Why`) carried identically
  by `construction`'s items, `digest.construction.stalled[]` and `advance
  {until:{layout}}`'s `unresolved_items`. Split across two workers they would
  have been two answers to "why is there no worker".

- 2026-09-02 (repair round) — **`f9dadc7`'s age vocabulary is CANONICAL; the
  enclosure roll-up's `unenclosed_for` is the one that moves.** Both landed in
  the same round, on different objects (per-element vs per-layout), and they
  disagree on every field name and on the shape of the verdict. Ruled by the
  orchestrator rather than left to whoever edits next.

  | | `a1644d6` `unenclosed_for` | `f9dadc7` (canonical) |
  |---|---|---|
  | start | `since_tick` | `state_since_tick` |
  | elapsed | `ticks` | `state_age_ticks` |
  | age unit | `day_boundaries` (int) | `state_age_days` (float) |
  | tracking start | `tracked_since` | `tracked_since_tick` |
  | verdict | `stale` — **bool** | `stalled` — **tri-state true/false/null** |
  | epistemics | `floor_note` (string, conditional) | `age_basis` (enum) |
  | sampling | on READ | on TICK (2500 cadence) |

  **Why the tri-state wins.** The worker reported the enclosure side as
  emitting `stale: false` on an untracked layout. Checked: it does not.
  `LayoutEnclosureWatch.Age` returns `null` when the layout is not in
  `firstFail`, and both callers set the key only when `age != null`. So an
  untracked layout produces **no `unenclosed_for` key at all**.

  That is the same defect wearing the other costume, and it is worse for a
  reader: an ABSENT key reads as clean exactly the way `eef837a`'s absent
  `ingredient_filter` read as `null` and cost that run twenty days.
  `eq(dig(env,"unenclosed_for.stale"), None)` passes on it. A tri-state
  `stalled: null` plus `age_basis: not-tracked` cannot be misread the same way,
  and it is what this round's own B-1 resolution specified: *stalled*, *not
  stalled*, and *not known yet* must never collapse into each other.

  **What moves and what does not.** The field NAMES and the tri-state verdict
  move to `f9dadc7`'s form. The **thresholds do not have to match** — an
  unenclosed room is alarming at one day boundary and a stalled blueprint at
  two, and that difference is a judgement about the game, not an inconsistency.
  Nesting need not match either; they hang off different objects.

  **Shipped incoherent, deliberately.** Both are on `main` as of `5170677` and
  the reconciliation is filed rather than rushed, because the round's remaining
  budget belongs to bench acceptance. An agent reading both surfaces today
  learns two vocabularies for one idea — which is precisely what batching the
  two issues into one worker was supposed to prevent, and did not, because they
  went to different workers on my dispatch.

- 2026-09-02 (repair round, worker; git-bug b7359fa) — **The allowed-area test
  is PER PAWN, so `designate` cannot state "outside the allowed area" as one
  fact, and the number that decides a refusal is quantified over the CAPABLE
  ROSTER.** `RimWorld/ForbidUtility.InAllowedArea(IntVec3, Pawn)` reads
  `forPawn.playerSettings.EffectiveAreaRestrictionInPawnCurrentMap` — there is
  no colony-wide area to compare a designation against. A check written against
  a single global or Home area would have reported clean on the exact colony it
  was written for, which is the failure that cost `Marco`.

  Three shapes follow, and each was a decision:

  1. **`accepted` is split rather than replaced.** `accepted_actionable` and
     `accepted_unreachable` sit beside it, and the `reach` block carries the
     roster, the areas (id, label, cells, which pawns, how many targets each
     shuts out) and a capped list of the unreachable targets. `accepted` keeps
     its old meaning — the gate took N — because a consumer that reads it today
     is not reading it wrong, only incompletely.
  2. **The refusal is narrow and has a door.** A batch where NOT ONE target is
     workable by ANY capable colonist is refused before anything is written,
     from a DRY PREFLIGHT (a throwaway designator run with `dryRun:true`), so
     the refusal is a refusal and not an apology for a mutation already made. A
     mixed batch reports and proceeds. `allow_unreachable:true` overrides:
     painting ahead of an area expansion is a thing players do, and a wall with
     no door is worse than a loud report.
  3. **The franchise is CAPABILITY, not assignment.** `!WorkTypeIsDisabled(w)`
     decides who counts, because a capable colonist with the work switched off
     is one `work-priorities` call away, while an incapable one never will be.
     `enabled` (`WorkIsActive`) is published beside it and raises a `warning`
     when it is zero — that is the OTHER half of what run m1-20260901 got wrong
     (128 designated `MineableSteel` cells, never mined) and there is nowhere
     else the agent sees it at the moment it designates.

  **The designation → work type link is data, not a table of ours.** The
  designator table names the `WorkGiver_*` CLASS that consumes each designation
  (verified in the decompile by member name); `DesignateReach.WorkTypeFor` looks
  that class up in `DefDatabase<WorkGiverDef>` by `giverClass` and reads the
  def's own `workType`. A mod that re-homes `WorkGiver_Miner` is honoured for
  free, and a class no def claims yields `applies:false` with a reason rather
  than a guess. `Mine` and `MineVein` share `WorkGiver_Miner` —
  `MineAIUtility.PotentialMineables` unions both designations — so the two verbs
  answer with one roster.

  **What it deliberately does NOT test, said in the envelope's own `test`
  field: pathing.** A cell inside a pawn's area can still be unroutable, and
  `Pawn.CanReach` is O(cells × pawns) region traversal against a 20,000-cell
  ceiling. Naming the field `reach` and quietly meaning "area" would have been
  the same class of defect the issue is about, so the envelope says which
  question it answered. Filed separately.

- 2026-09-02 (repair round, worker; git-bug b7359fa/855117a) — **`accepted` is
  not the count of designations created, and exactly one designator makes that
  true.** `Designator_MineVein.DesignateSingleCell` calls
  `FloodFillDesignations`, which paints `MineVein` over every contiguous
  non-fogged cell whose edifice def matches the clicked one — so one accepted
  cell can create forty designations, and every later cell in the same drag then
  comes back already-designated and is REJECTED. Any rollup keyed on `accepted`
  — the reach report, a per-def composition — would therefore be measuring the
  wrong set, and would say "1" about a call that did forty cells of work.

  `DesignateEngine.Landed` is the fix and it is a set, not a count:
  `CellSnapshot` before and after, delta for a Cell-targeted def, the accepted
  things for a Thing-targeted one, and `designated_from` names which reading the
  caller got. `designations_before`/`designations_now` already reported the same
  truth as a pair of counts; this is that pair with the cells in hand.

- 2026-09-02 (repair round, worker; git-bug 855117a) — **`855117a`'s headline
  count is exact and its headline DIAGNOSIS is not supported by the run's own
  artifacts, and the fix is the same either way.** The issue says
  `designate {type:"mine", rect:[131,116,6,10]}` accepted "14 cells of whatever
  rock was exposed on that rect, which on this face is mostly sandstone and
  marble". Checked before building, per the standing rule that an issue's stated
  cause is checked against the artifacts:

  * **"14 of 60" is exact.** `RUNS/m1-20260901/journal/…ndjson` seq 45:
    `{"verb":"designate","step":"mine","counts":{"targeted":60,"accepted":14,
    "rejected":46},"rejected_by_reason":{"not-designatable":22,"fogged":24}}`.
  * **"mostly sandstone" is contradicted.** The save's
    `<compressedThingMapDeflate>` is a ushort-per-cell grid of `def.shortHash`
    (`MapFileCompressor.HashValueForSquare` /
    `DataSerializeUtility.SerializeUshort`, deflate + base64). Decoded from
    `day17-tick1020680-autosave.rws`, the rect's still-unmined cells are **13
    `MineableSteel`, 8 `Marble`, and one `ChunkMarble` lying on the ground —
    no sandstone rock anywhere in it.** All four cells `nearest
    {def:"MineableSteel"}` had named are among the fourteen accepted.
  * **What the artifacts DO prove is sharper.** Thirteen `MineableSteel` cells
    inside that same rect were never designated and were still standing twenty
    in-game days later. The rect caught the exposed face; the ore body ran past
    it. So the aiming failure is real and its shape is "the rect UNDER-covers
    the ore", not "the rect designated worthless rock".

  Neither reading changes the fix, and that is the argument for it: **nobody
  could tell which it had been from `accepted: 14`.** So `designate` publishes
  `composition` — a per-def rollup of what actually landed, `by` naming whether
  the subject was the cell's mineable, its edifice or its terrain, and for an
  ore def `mineable_thing`, `mineable_yield` and `EffectiveMineableYield` (both,
  because the raw number is the def's and the effective one is this game's
  difficulty). It is scored on `DesignateEngine.Landed`, never on `accepted`.

  Two further facts about the mine pair, confirmed in the decompile, both now
  reported rather than silent:

  1. `Designator_MineVein` **flood-fills at designate time** —
     `DesignateSingleCell` calls `FloodFillDesignations`, which paints every
     contiguous non-fogged cell whose edifice def matches and calls
     `TryRemoveDesignation(c, DesignationDefOf.Mine)` on each. So `mine-vein`
     REPLACES a Mine designation silently: the MineVein count rises, the Mine
     count falls, and nothing said the second half. `replaced` now does.
     `Designator_Mine.DesignateSingleCell` does the same to `SmoothWall`.
  2. The reverse is a REFUSAL, and it was the residual `8b0b88f` recorded and
     left: `Designator_Mine.CanDesignateThing`'s third clause rejects on
     `DesignationAt(t.Position, DesignationDefOf.MineVein)`, a def that is not
     the entry's, so `designate mine` over vein-marked ground read
     `not-designatable` — the same envelope as "this rock is not mineable",
     which is the opposite correction. `already-designated-other` is its own
     key now, with `designation_present` naming the def. This is not a blind
     re-implementation of the widget: the gate has already spoken and the probe
     only re-keys a rejection that was going to be emitted anyway, which is
     `WhyAlready`'s own rule.

  `mine-vein` was registered and had never been exercised — the issue said to
  verify that first. It is exercised in `accept/855117a-mine-vein.py`, and one
  hazard turned up in the reading that the fog gate already covers:
  `Designator_MineVein.CanDesignateCell` returns **true** for a fogged cell,
  while `DesignateSingleCell` then does `loc.GetEdifice(base.Map).def` with no
  null check. `DesignateEngine`'s own uniform fog gate rejects the cell before
  the designator ever sees it, so the NRE is unreachable through this verb —
  but it is unreachable *because of that gate*, not because the game is safe.

- 2026-09-02 (repair round, worker; git-bug 855117a) — **A collapsed-alphabet
  channel is for TOPOLOGY; identity needs a def-keyed query.** `map-view` is one
  ASCII char per cell, so `"%": "sandstone | marble | compacted steel"` is the
  channel being honest about a collision it documents. The general form, one
  level down from `e6faa51`'s independence argument (never compare a glyph from
  one channel against a glyph from another): **never compare a glyph against a
  def either.** A `|` in a legend entry is the channel saying it cannot answer
  the question about to be asked of it. Carried as the playbook lesson
  `a-glyph-is-topology-not-identity` AND as a `use_for` line in the `channel`
  block `Spatial.Render` publishes — because the channel block is what the agent
  is actually holding at the moment it chooses a rect, and a lesson it did not
  reload is not a control.
