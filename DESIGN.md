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

    advance { ticks:N | until:{letter|threat|alert|event},
              max_tps:T, timeout_ticks:M }

Every `until` matcher is a **journal tap over discrete events** — it hooks
`Journal.OnEvent` and halts on a match, so it can only fire on something that
HAPPENS. `threat` is `letter` narrowed to `ThreatBig`/`ThreatSmall`. There is
deliberately no state-predicate matcher here; see the 2026-08-30 decisions-log
entry, which records why the original `condition` was moved out of this line and
into its own issue rather than dropped.

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
