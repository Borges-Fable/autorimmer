# Spike 0.1 — Findings (git-bug 3fa4cf5)

Empirical + source-analysis results that amend the wave 1–3 specs. Bench:
`/home/dorian/projects/rimworld/_RimWorld-Agent`, RimWorld 1.6.4871 rev600,
Mesa 26.0.6, AMD Radeon RX 6700S laptop (the thermal-trip-history machine).

## 1. Profile: engine symlinks re-root the game at Steam (affects every spec that touches the bench)

A symlinked `RimWorldLinux` is fatal to profile isolation: Unity resolves the
game root via `/proc/self/exe`, which canonicalizes symlinks, so the game
rooted itself at the Steam install and loaded **Steam's `Mods/`** (a farm of
dev symlinks with no Harmony) instead of the profile's — producing ~40
TypeLoad red errors that had nothing to do with the modlist. First boot of the
spike found it; `Mono path[0]` in Player.log names the effective root and is
the one-line diagnostic.

Fix (implemented in `profile/make-profile-agent.sh`): `RimWorldLinux` is a
**real copy** (14KB stub; re-copied on every profile rebuild),
`RimWorldLinux_Data` is a **real dir of symlinked children**, and only
`UnityPlayer.so`/`Data`/`Version.txt` stay symlinks. Engine updates therefore
require re-running make-profile-agent.sh (the stub must match UnityPlayer.so).

- **`_RimWorld-Test` has the same latent bug** (its make-profile.sh symlinks
  the stub); it "works" only because Steam's Mods/ happens to contain most of
  the same mod symlinks. Flagged for Dorian; not this spike's scope to fix.
- Spec 1.1+: any future script that boots the bench can assert isolation by
  grepping Player.log line 1 for `_RimWorld-Agent/RimWorldLinux_Data/Managed`.

## 2. Modlist (v1 bench, 38 active: 6 DLC + 32 mods)

Deviations from DESIGN.md §Bench "Own vanilla+DLC mods", for Dorian to confirm
at the post-0.1 gate:

- **Guests and RegisterLanes added.** DESIGN's own-mods list omits them, but
  its visitor-cluster line says Hospitality/Gastronomy/Storefront are on the
  bench from day one *because* they are "required by Guests (and
  Music/RegisterLanes)", and Factions/Guests is the first analysis target
  (M3). A bench that carries the cluster but not the mods it exists to serve
  tests nothing. Not settled law — flagged deviation.
- **CashRegister added** — the only package the build-time transitive
  resolution pulled in (Gastronomy and Storefront both declare
  `Orion.CashRegister`). Resolution walks `modDependencies` of everything in
  `Mods/` against the `_RimWorld-Testing/Mods` + `_upstream/Mods` libraries,
  so future modlist growth resolves the same way automatically.
- **HugsLib NOT needed.** Hospitality (Continued) 1.6 declares only
  `brrainz.harmony`. Nothing on the bench declares HugsLib. It stays off.
- **PerspectiveShift NOT needed.** SeekAndKill's About declares only Harmony;
  source-verified (`PSInterop.Init` resolves PS types by name, returns early
  when absent, and `Patch_PawnGetGizmos` then provides SeekAndKill's own
  `Command_SeekToggle`; its think node is injected whenever PS is absent).
  Runtime-verified: loads clean without PS (§5). PS stays in its deferred
  tier.
- Name→repo mappings that are not obvious: FuzzyRoomRequirements=`Rooms/`,
  CruelAndUnusualPunishment=`prisonsentenceslikehemocasket/`,
  RealisticGasses=`gas/`, Nepobaby=`nepo/`, WirelessChargingMech=
  `wirelesschargingmech/` (base only; the `…upgrade/` repo is a separate mod
  and stays off).
- Load-order constraints encoded in `profile/gen-modsconfig.py` HEAD:
  CashRegister → Hospitality → Gastronomy → Storefront → Guests, all before
  the alphabetical middle (alpha order would put `adamas.storefront` before
  `orion.cashregister`, and `dorian.factions` before `dorian.guests` —
  Factions declares `loadAfter` Guests). TAIL: DubsPerformanceAnalyzer →
  AnalyzerBridge → AutoRimmer (observers last).

## 3. Prefs that gate agent operation (spec 1.1/1.3)

Seeded by make-profile-agent.sh (only if absent):

- **`runInBackground` defaults False and MUST be True** — `PrefsData.Apply()`
  sets `Application.runInBackground` from it a few frames in, overriding the
  `true` that `Root.Start` sets. The bench window is parked unfocused on a
  special workspace by design, so a default-prefs bench freezes the sim the
  moment focus moves. This is the single most load-bearing pref.
- `devMode=True` — required for the `dev:*` verb layer (spec 3.1), dev
  QuickTest, and the `autostart` save hook.
- `automaticPauseMode=Never`, `pauseOnLoad=False`, `pauseOnError=False` —
  AutoRimmer owns time; the game must not pause behind its back. (advance's
  own pause-on-return is spec 1.3's job, not the game's.)
- Volumes 0, `customCursorEnabled=False`, `edgeScreenScroll=False`, windowed
  1280×768.
- **Fixture hook for spec 5.1**: with devMode on, a save named `autostart.rws`
  is loaded automatically on entering the Play scene
  (`SaveGameFilesUtility.GetAutostartSaveFile`), and
  `Root_Play.Start` also honors `Find.GameInitData.gameToLoad`. Both are
  no-UI paths into a fixture.
- **Advance-vs-pause semantics note (spec 1.3)**: the harness set
  `CurTimeSpeed = Paused` and drove `DoSingleTick` directly — on plan end
  the game was left paused, matching DESIGN's "advance ALWAYS returns
  paused". Worked exactly as designed across 950K ticks.

## 4. Boot modes

- **Primary**: `run-agent.sh` on the live Hyprland session. Dynamic rule
  `hyprctl keyword windowrule "workspace special:rwagent silent, match:class ^(RimWorldLinux)$"`
  (dialect verified on Hyprland 0.55.1 — block syntax in config files, the
  inline `match:` form for `hyprctl keyword`). Verified: window lands on
  `special:rwagent`, active window (terminal) keeps focus.
- **`-quicktest`**: boots Entry scene → auto-loads Play scene
  (`QuickStarter.CheckQuickStart` in `UIRoot_Entry`) →
  `SetupForQuickTestPlay`: Crashlanded, Cassandra **Rough**, 0.3 planet
  coverage, 250×250 map, random seed. Good enough for smoke boots; too hostile
  and too slow (fresh worldgen every boot, ~2 min) for fixtures — spec 5.1
  should prefer `autostart.rws`-style fixture loads.
- **fps cap**: mangohud with `MANGOHUD_CONFIGFILE` pointing into the profile
  (`config/mangohud-agent.conf`). MangoHud watches the file — rewriting
  `fps_limit` re-caps the running game with no restart. That is the `rwa
  watch` mechanism for spec 1.4: unwatched default 30, watched raise to 60,
  no game-side code at all.
- **Xvfb fallback**: design ready + script-flagged, runtime demo pending
  install (§8).
- **batchmode**: boots and ticks headless but fails zero-red-errors (§7).
- Boot-to-playable-map time with the full modlist + `-quicktest`: 2–4 min
  (mod load ~75s; worldgen at 0.3 coverage dominates and varies by seed).
  Spec 5.1: fixtures via `autostart.rws` skip worldgen entirely.
- **Live fps re-cap demonstrated**: rewriting `fps_limit` in
  `config/mangohud-agent.conf` mid-run snapped the running game from
  free-run (~34 fps) to a locked 30.0 fps within a second or two, no
  restart, no game-side code. `rwa watch`/`unwatch` (spec 1.4) is exactly
  this file write.

## 4b. Hidden-workspace frame throttling — the platform-critical finding

RimWorld's tick loop is frame-bound (`ticksThisFrame < TickRateMultiplier*2`
per `TickManagerUpdate`), so anything that stops FRAMES stops TIME. Hyprland
does not drive frames for windows on a hidden special workspace: parked
there, the game fell to **1–2 fps ≈ 4 tps at Normal speed** (measured via
AnalyzerBridge heartbeat), and `runInBackground=True` does not help — that
governs input focus, not frame delivery. Moving the window to a visible
workspace snapped it to the mangohud cap instantly (30 fps / 60 tps);
re-hiding throttled it again. Diagnosis is unambiguous.

Fix battery (all three applied pre-launch by `run-agent.sh`; verified: 60.1
fps while hidden on `special:rwagent`, no focus change anywhere):

1. `hyprctl keyword windowrule "render_unfocused on, match:class ^(RimWorldLinux)$"`
   — Hyprland's own mechanism for exactly this (games that must keep
   rendering); must be set BEFORE the window maps (a dynamic rule added
   after mapping did not retro-apply, and `setprop` has no such prop on
   0.55.1).
2. `hyprctl keyword misc:render_unfocused_fps <cap>` — its default 15 would
   halve the unwatched frame budget.
3. `export vblank_mode=0` — Mesa never blocks the swap on a compositor
   present; mangohud's `fps_limit` becomes the only pacer.

Consequences for later specs:
- Spec 1.1/1.3: after any bench boot, assert liveness (heartbeat tick delta
  over 5s at Normal ≈ 300), not just process-up — a frame-starved game looks
  healthy in `ps`.
- Both hyprctl keywords are session-scoped (lost on Hyprland restart);
  run-agent.sh re-applies per launch, so nothing persists in Dorian's
  config. If a permanent rule is ever wanted, the block form for
  `~/.config/hypr/custom/rules.conf` is:
  `windowrule { name = rwagent-render; render_unfocused = on; match:class = ^(RimWorldLinux)$ }`
  (unverified spelling of the block property — the inline keyword form above
  is the verified one; Dorian's call, do not add it for him).

## 4c. TimeSlower and threats during fast-forward (spec 1.3 contract input)

Empirical, from the usb phase: 5s into 150×-boosted Ultrafast (~1760 tps), a
raid fired and `TimeSlower.SignalForceNormalSpeedShort` clamped
`TickRateMultiplier` to 1 → 60 tps for ~115s of wall time while the colony
fought (re-signaled while danger persisted), then full speed resumed
automatically. The `UltraSpeedBoost` field stayed true throughout — the
clamp lives in `TickRateMultiplier`, not the field.

The budgeted `DoSingleTick` loop is **immune** to that clamp (it never
consults `TickRateMultiplier`) — measured: budget phases plowed through a
later event window at full duty while per-tick cost tripled. So spec 1.3
must make an explicit choice, and the spike's recommendation is: **do not
silently honor TimeSlower, and do not silently ignore it either — surface
it.** `advance` should watch the journal/letter conditions it was given
(`until:{letter|alert|…}`) and return on them; for plain `ticks:N` advances
it should keep full speed (the whole point of the platform is unattended
fast-forward) but stamp `{slower_active:true}` periods in the result so the
caller knows the storyteller considered that stretch dangerous. The
"NothingHappening" x2 boost (Superfast 360→720 mid-phase when colonists
slept) is the same lesson from the other side: **tps at a named speed is
state-dependent; only measured tps is truth.**

## 5. Boot health (zero-red-errors gate): PASS

Acceptance boot (pure v1 modlist, `-quicktest`, 2026-08-30 14:14): booted to
main menu (Entry scene; `-quicktest` fires from `UIRoot_Entry`), generated a
world + 250×250 map, reached a playable, ticking colony (verified via
AnalyzerBridge `status.json`: `gameLoaded:true`, ticks advancing at speed
Normal). **Red errors: 0. Exceptions in Player.log: 0.** LogRelay severity
counts: 13 warnings, 0 errors.

The measurement boot then soaked the same modlist for ~19 min of continuous
hyperspeed play — 958,671 ticks ≈ 16 in-game days, including one raid fought
autonomously (SeekAndKill), a psychic-drone-class event, colonist sleep
cycles, weather — and ended at **0 errors, 9 warnings** (same set minus
first-run entries). Zero red errors holds under sustained load, not just at
boot.

Warning triage (all benign, none gating):

| Warning | Count | Verdict |
|---|---|---|
| SteamAPI.Init() failed | 1 | By design — the bench detaches Steam (env unset, no steam_appid.txt). Permanent. |
| Hospitality "Knowledge data was missing key …" ×4 | 4 | First-run tutor-key registration; persisted into Config, absent on later boots. |
| Guests: Socio-Butterfly join pool / seat-count not found | 2 | Expected — SocioButterfly is deferred-tier; Guests degrades gracefully. Permanent until that tier joins. |
| "Type X probably needs StaticConstructorOnStartup" ×6 | 6 | Dev-mode-only hygiene notes (devMode=True enables the report): Guests ×3 (CompGuestParking, GuestsDebugComponent, Patch_TourCancelGizmo), FSWA ×2 (SidearmGizmoUtility, Command_WeaponRole), RealisticGasses ×1 (CompGasTank). Candidate one-line fixes in those repos; harmless meanwhile. |

## 6. Throughput + thermals

Method: throwaway `AutoRimmerSpike` mod (GameComponent + files-only watcher
thread — the analyzerbridge threading pattern, validated end-to-end here for
spec 1.1). Phases driven from a `plan.txt`, per-second samples to
`tps.ndjson`; `sensors` sampled every 5s alongside. Bench: full v1 modlist,
fresh `-quicktest` colony (3 colonists, 250×250), fps cap 60 (30 for the
final phase), window hidden on the special workspace throughout.

### tps table (measured, small colony aging 0→16 days across the run)

| Phase (wall time) | Target | Measured | Notes |
|---|---|---|---|
| Normal 60s | 60 | 60 flat | meanTickMs 1.22 |
| Fast 90s | 180 | 179–181, avg 180 | meanTickMs 0.73–0.79 |
| Superfast 90s | 360/720 | 360 awake, 718–739 asleep; avg 472 | `NothingHappeningInGame()` doubled the rate live mid-phase |
| Ultrafast 150s | 900 | 899–920 steady, avg 890 | full arithmetic target at 60 fps; meanTickMs ~0.58 |
| Ultrafast+USB 180s | 9000 arith. | **1720–1913** while free; avg 567 | 45.45ms/frame wall-clock break binds: ~90 ticks/frame at ~20 fps. Mid-phase a raid fired and `TimeSlower` clamped rate to 1 (60 tps) for ~115s — `usb` flag stayed true throughout (reflection write sticks; the clamp is TickRateMultiplier-level) |
| budget 25ms, fps 60 free-run (~34 fps), 180s | — | avg **1267** (med 1481, range 610–1732), duty 83% | game Paused; loop drives DoSingleTick |
| budget 40ms (~22 fps), 180s | — | avg **1467** (med 1576, range 460–2108), duty 88% | thermal-peak phase |
| budget 25ms, fps capped 30, 120s | — | avg **1392** (med 1381, range 500–2113), duty 75% | the recommended default shape |

Readings:

- **The sustained ceiling on this machine is ~1500–1750 tps** (peak 1s
  sample 2113) at 0.5ms/tick base cost, and it is **state-dependent by 3×**:
  during the raid + a heavy event window, per-tick cost hit ~1.3ms and the
  same 25ms budget delivered ~650 tps. Spec 1.3 must treat `max_tps` as a
  ceiling, never a promise, and report actual ticks advanced.
- The budgeted loop beats every vanilla speed at equal or lower fps: vanilla
  Ultrafast needs 60 fps for its 900; budget-25 at 30 fps delivers ~1400.
  The gap vanilla leaves is the frame-count bound (`mult*2` per frame);
  the budget loop's bound is pure wall-clock duty.
- `TickManager.MeanTickTime` freezes while Paused (only TickManagerUpdate
  feeds it) — a budget loop must derive cost from its own stopwatch, not
  that field (the spike's constant 0.512 column is that artifact).
- USB (150×) adds nothing over the budget loop — same 45ms-class wall, plus
  it stays subject to TimeSlower clamps. The budget loop is strictly better
  for advance. USB's one use: quick manual fast-forward without a mod.

### Thermal (16.5 min continuous, `sensors` every 5s)

Curve (per-minute medians; run phases annotated):

    min  0–2  Normal/Fast          Tctl 93.9–94.0  cpu_fan 5600–5700  gpu 81
    min  3–4  Superfast/Ultrafast  Tctl 93.9        fan 5600           gpu 80–81
    min  5–6  usb (raid-clamped)   Tctl 89.6–90.5  ← only dip: 60 tps window
    min  7–15 usb tail + budgets   Tctl 93.2–93.8  fan 5600–5700      gpu 78–79
    min 16    post-kill cooldown   Tctl 87.1 falling, fan 4100

    Tctl: min 82.5 / median 93.5 / max 94.1.  Fans: cpu max 5700, gpu max 3800.
    GPU edge 78–81C, PPT 17–23W throughout (GPU is not the constraint).

What was actually observed: this chassis **plateaus at Tctl 93.5–94.1C with
the CPU fan pinned at 5600–5700 rpm under ANY sustained full-duty load** —
Ultrafast at 900 tps pins it exactly as hard as budget-40 at 1700. No
thermal trip occurred in 16.5 min and tps never sagged in a way that
suggests clock throttling, but headroom above the plateau is ~1C against
the typical 95C Tctl limit for this silicon. The only cooling observed came
from the raid-clamp window (60 tps → ~90C). Conclusion: there is no tps
number on this laptop that is "thermally free"; there is only duty cycle.

### Recommended default `max_tps` cap (→ spec 1.3, hard-enforced)

- **`max_tps` default 1000, implemented as a per-frame `DoSingleTick`
  budget** (at most 25ms/frame, and stop early once the second's tick quota
  is met). At the 30 fps unwatched cap that is at most 75% duty — the exact
  configuration that ran sustained at ≤94.1C without incident — and 1000
  tps still fast-forwards an in-game day in a minute.
- **Add the thermal governor in the same spec, it is cheap**: read
  `/sys/class/hwmon/*/temp*_input` (k10temp Tctl, the file behind
  `sensors`) off-thread once a second; while Tctl ≥ 93C for 30s, halve the
  budget; resume at < 90C. That converts the ~1C headroom into an enforced
  floor instead of hope. The fan is audible truth: 5700 rpm is the sound of
  the plateau, per DESIGN's own invariant.
- Unwatched fps cap stays 30 (60 fps rendering alone holds the plateau
  even at Normal speed); `advance` should not raise fps even when watched —
  watching a fast-forward at 30 fps is fine.

## 7. batchmode/nographics experiment: ticks headless, fails the error gate

`run-agent.sh --batchmode --quicktest` (`-batchmode -nographics`), 4-min
timebox. Outcome — much further than expected:

- **Boots fully headless to a generated, TICKING map**: mods loaded, world
  generated, AnalyzerBridge heartbeat `gameLoaded:true`, ticks advancing at
  Normal (Factions' traffic atlas "110ms of work over 29 ticks" proves live
  sim). No display, no window, no Xvfb.
- **The frame loop free-runs at ~2860 fps** (no vsync, no render, mangohud
  is bypassed in this mode). A budget advance loop would get near-100% duty.
  Pacing would need `Application.targetFrameRate` game-side.
- **But: 23 red errors** — `Could not execute post-long-event action:
  NullReferenceException` in `GlobalTextureAtlasManager.TryInsertStatic`
  (texture-atlas building; `Texture2D` creation returns null with no
  graphics device), plus a stream of Unity-side "Shader … not supported"
  stdout noise. The sim survives, but the zero-red-errors invariant is
  violated structurally.
- **No Player.log is written** in this mode — Unity logs to stdout. Any
  log-scraping tooling (and the §1 `Mono path[0]` diagnostic) must read the
  launcher's captured stdout instead.

Verdict: **not the v1 headless path.** Xvfb remains the sanctioned fallback
(real GL device → no atlas NREs, mangohud works, Player.log exists).
batchmode is a plausible future fast-CI lane only if someone patches or
whitelists the atlas NREs and adds frame pacing — file under deferred, no
spec depends on it.

## 8. Xvfb fallback: VERIFIED (2026-08-30, orchestrator, post-install)

`xorg-server-xvfb`/`x11vnc` were absent for the whole spike (sudo needs a
password), so this line shipped as UNVERIFIED-PENDING-INSTALL. Dorian installed
both at the post-0.1 gate and the orchestrator ran it. **It works, and running
it found a bug in the launcher that no amount of reading would have found.**

    cd _RimWorld-Agent && ./run-agent.sh --xvfb --vnc --quicktest --fps 30

Result: booted to a playable ticking map in **73 s**, `gameLoaded:true`,
**0 errors / 9 warnings** — byte-for-byte the same known-benign set as the live
session boot (section 5) — 0 exceptions in Player.log, `127.0.0.1:5900`
listening (v4 + v6, loopback only), and **zero windows on the live Hyprland
session**. Genuinely detached, which is the whole point of the fallback.

### The bug: `WAYLAND_DISPLAY=` is not the same as unsetting it

`--vnc` produced an Xvfb with nothing listening. x11vnc 0.9.17 tests for
`WAYLAND_DISPLAY`'s **presence**, not its value: launched from a Wayland
session it prints

    Wayland display server detected.
    Wayland sessions are as of now only supported via -rawfb ... Exiting.

and dies before binding — and `-quiet` swallows the message, so the symptom is
a silent absence. The launcher's `WAYLAND_DISPLAY=` clears the value but leaves
the variable defined, so the check still fired. Fixed in a66d78b: `env -u` for
x11vnc, and a subshell with `unset` for the game (a shell function cannot be
launched through `env -u`). The game had been surviving the same trap only
because SDL falls back to X11 when the Wayland connect fails.

### Xvfb costs about 15% of throughput

Mesa cannot reach the GPU under Xvfb (`amdgpu_device_initialize failed`, then
software rendering), so the frame rate lands **below** the mangohud cap: 25.2
fps against a cap of 30, and because ticks are frame-bound that is **50.9 tps
where the live session gives 60**. Measured over 11 s at Normal speed
(764 -> 1324 ticks). Consequence for spec 1.3: the tps table in section 6 is a
GPU-backed-session table. Under the Xvfb fallback the budgeted loop still
governs correctly, but its ceiling is lower, and the thermal governor matters
less because the frame loop itself is the bottleneck.

Boot is also slower — 73 s here against 39 s on the live session, both
`-quicktest` with worldgen — since worldgen is CPU-bound and the software
rasteriser is competing for the same cores (853% CPU observed during worldgen).

Standing note for spec 1.4: with the section 4b render-unfocused fix proven on
the live session, Xvfb is genuinely a *fallback* (fully detached runs, e.g.
under a lock screen or a headless boot), not the daily driver. `rwa watch`
against an Xvfb run means pointing a VNC client at `localhost:5900`, not
revealing a workspace.

## 9. Per-frame update set a fast-forward loop must preserve (spec 1.3's core input)

Read from decompiled 1.6 source + Zetrith's Multiplayer (decompiled at
`rimworld-tools/Info/decompiled/Multiplayer16Full/`), verified empirically by
the budgeted-loop phases (§6).

**The design conclusion first**: vanilla itself runs up to 300 ticks per frame
(Ultrafast+UltraSpeedBoost) against per-FRAME manager updates — power nets,
region/room rebuilds, glow, lords, weather all update once per frame no matter
how many ticks passed. So a budgeted `DoSingleTick` loop that **yields to the
frame** inherits vanilla's own correctness envelope; nothing per-frame needs
to be re-implemented inside the loop. MP's catch-up (`TickPatch.DoUpdate`)
does exactly this: it runs `DoTick` in a **25ms-budget** loop, then returns so
the engine frame (and with it `Map.MapUpdate`) runs, repeating next frame.
What MP moved *into* its per-tick path (`AsyncTimeComp.UpdateManagers`:
regionGrid.UpdateClean, regionAndRoomUpdater, powerNetManager, glowGrid; plus
`pathFinder.ForceCompleteScheduledJobs`) it moved for cross-client
*determinism* — frame boundaries differ between clients — not correctness;
single-player AutoRimmer does not need that, and vanilla's own
`MapPreTick` already contains `PathFinderTick()`.

**Hard rule for spec 1.3**: the advance loop lives in
`GameComponentUpdate` (inside `Game.UpdatePlay`, after this frame's
`MapUpdate`s), runs `DoSingleTick()` under a wall-clock budget with the game
speed left at Paused (so `TickManagerUpdate` early-returns and cannot
double-tick), and RETURNS every frame. Never `while(ticks--) DoSingleTick()`
unbounded: beyond stutter, a frame that never ends starves everything below.

What runs per frame between bursts (the things a non-yielding loop would
starve — enumerated from source):

- `Root.Update`: RealTime.Update, LongEventHandler.LongEventsUpdate,
  SteamManager.Update, PortraitsCache, AttackTargetsCacheStaticUpdate,
  Pawn_MeleeVerbs static update, Storyteller.StorytellerStaticUpdate,
  CaravanInventoryUtility static update, `uiRoot.UIRootUpdate` (letters,
  windows, input), soundRoot.Update.
- `Root_Play.Update`: ShipCountdown, ArchonexusCountdown, TargetHighlighter,
  `Game.UpdatePlay`, MusicManagerPlay, PerformanceBenchmarkUtility.
- `Game.UpdatePlay`: LetterStack.OpenAutomaticLetters, **TickManagerUpdate**,
  LetterStackUpdate, `World.WorldUpdate` (WorldComponentUpdate; render bits
  gated on world camera), per-map **`Map.MapUpdate`**, GameInfoUpdate,
  **GameComponentUtility.GameComponentUpdate** ← the advance loop's slot,
  SignalManagerUpdate, GlobalTextureAtlasManagerUpdate.
- `Map.MapUpdate`, non-render half (runs even for background maps):
  SkyManagerUpdate, **powerNetManager.UpdatePowerNetsAndConnections_First**,
  regionGrid.UpdateClean, regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms,
  glowGrid.GlowGridUpdate_First, **lordManager.LordManagerUpdate**,
  postTickVisuals.ProcessPostTickVisuals, areaManager.AreaManagerUpdate,
  **weatherManager.WeatherManagerUpdate**, flecks.FleckManagerUpdate,
  MapComponentUpdate. (The draw half is gated behind
  `WorldRendererUtility.DrawingMap && Find.CurrentMap == this` and can be
  ignored for throughput.)
- Inside `DoSingleTick` already (nothing to add): MapPreTick (incl.
  PathFinderTick, temperature, wind), the three TickLists, DateNotifier,
  Scenario, WorldTick, StoryWatcher, GameEnder, Storyteller, TaleManager,
  QuestManager, WorldPostTick, MapPostTick, History, GameComponentTick,
  LetterStackTick, Autosaver, FilthMonitor, TransportShipManager.

Two per-frame consumers worth knowing about when the loop runs many ticks per
frame: `LetterStack.OpenAutomaticLetters` opens any letter marked
open-automatically once per FRAME (a burst can accumulate several before the
first opens — advance-until on letters, spec 1.3, must read the stack itself,
not rely on dialogs), and `Alert`s are recalculated by UIRootUpdate on their
own cadence, so alert-based watchpoints see alerts a frame or two "late"
relative to the tick that caused them (they are late-by-design anyway; DESIGN
§Observation already treats alerts as trailing signals).

## 10. Tick-rate machinery facts (spec 1.3 implementation notes)

From decompiled `TickManager` (verified against orchestrator's baseline):

- `TickRateMultiplier`: Paused 0, Normal 1, Fast 3, Superfast 6 (12 when
  `NothingHappeningInGame()`, 18 with no maps), Ultrafast 15 — or **150 with
  no maps OR when the private static `UltraSpeedBoost` is set**.
- `TickManagerUpdate` per frame: wants `deltaTime/CurTimePerTick` ticks
  (`CurTimePerTick = 1/(60·mult)`), ceiling `mult*2` ticks, hard break at
  45.45ms (= 1s/22, `WorstAllowedFPS`).
- **`UltraSpeedBoost` is reachable outside dev mode** and sticks:
  `[TweakValue]` fields are only written by `EditWindow_TweakValues` (a
  dev-mode window) when a human drags them; nothing re-writes the field per
  frame. A plain
  `typeof(TickManager).GetField("UltraSpeedBoost", NonPublic|Static)` write
  works from any mod code and survives across frames (verified empirically by
  the usb phase readback, §6). It is per-session state (not saved).
- `DoSingleTick()` is public; it increments `ticksGameInt` itself and is safe
  to drive externally while `CurTimeSpeed == Paused` (that is how the spike
  harness ran it; TickManagerUpdate early-returns on Paused so there is no
  double-ticking).
- `Pause()`/`CurTimeSpeed` setter goes through `PlayerCanControl`; during
  cutscenes/landing confirmations the setter silently no-ops (messages
  instead). Spec 1.3's advance must check `Find.TickManager.Paused` after
  setting, not assume.
- `slower.SignalForceNormalSpeedShort()` (TimeSlower) forces ~normal speed
  after threats; `TickRateMultiplier` honors it — an advance at Ultrafast can
  legitimately deliver 60tps for a while after a raid fires. Report actual
  tps, don't assert the multiplier.
