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
  dumped from the live DefDatabase, colored-grid renderer.
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
- ASCII viewports with coordinate rulers; a PNG render channel (baseviz canvas)
  the agent Reads as an image — an independent second visual check.
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
  AnalyzerBridge + Dubs Performance Analyzer, BaseVizCatalogDumper, AutoRimmer.
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
  asserts against `JOURNAL.md`, which is also here. `rimworld-tools` has no git
  at all (467MB, 206MB of it decompiled RimWorld and ~60 third-party mods), so
  the original split also had no review trail and no route to the second bench.
  It stays unversioned and reference-only. 2.5 still REUSES `baseviz/`'s
  catalog and canvas — that reuse is a code dependency, not a location.
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
  lagging indicator, `food_days < 3` is a leading one. Filed as its own spec so
  it has a home, dependencies and an acceptance section instead of being a word
  in a list nobody implemented.
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
