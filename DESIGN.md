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

Status: spec stage. The build plan is the git-bug spec issues (start at the
`type:muster` issue); this document is the shared context they all reference.

## Non-goals (v1)

- No game fork / headless reimplementation of the sim.
- Single-map colony play only: no world map, caravans, settlements, gravship travel.
- No Combat Extended yet (deferred tier; FindSuitableWeaponAndAmmo confirmed to
  run vanilla-only via its `ModsConfig.IsActive` guard and split `.CE` assembly).
- No multiplayer anything.
- The agent never launches the user's installs (`_RimWorld-Testing`, MP). Only
  `_RimWorld-Agent`. This is the standing carve-out to the workspace "never
  launch RimWorld" rule; the rule stays in force for every other install.

## Architecture

    [_RimWorld-Agent: native Linux build, Hyprland special workspace, fps-capped]
        AutoRimmer mod (C#): verb registry · serializers · tick driver · journal
            ⇅ file protocol under save-data AutoRimmer/
    [rwa CLI, python, in rimworld-tools] ⇄ [Claude session | rwtest runner]

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

    advance { ticks:N | until:{letter|alert|event-match|condition},
              max_tps:T, timeout_ticks:M }

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

Context budgets are part of every observer spec (digest ~1–2KB; everything else
drill-down on demand). jq-side filtering is expected and encouraged.

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
