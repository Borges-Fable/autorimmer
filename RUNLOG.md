# AutoRimmer — orchestration run log

Append-only. One section per orchestrator session.

## Session 1 — 2026-08-30

Opus orchestrator. Wave 0 only; stopped at the post-0.1 hard gate.

### Issues

| issue | model | wall time | outcome |
|---|---|---|---|
| 0.1 Spike: profile, headless boot, throughput (3fa4cf5) | fable | 52 min worker + ~25 min verify | merged; **not closed** — one acceptance line blocked |

**0.1 — what landed.** `profile/make-profile-agent.sh`, `profile/run-agent.sh`,
`profile/gen-modsconfig.py`, `FINDINGS.md` (439 lines). Bench
`_RimWorld-Agent` built and booting: 38 active mods (6 DLC + 32), 0 red errors,
9 known-benign warnings. Merged to main **fast-forward** (26b638c..57263e7); no
assemblies in the range, so no `Build:` commit owed.

**Verification.** Ran the profile build, booted the bench myself, and re-derived
every headline number from the raw artifacts rather than trusting the worker's
table. Boot: `Mono path[0]` proves isolation; `gameLoaded:true` at 60.8 tps
sustained; LogRelay 0 errors / 9 warnings, matching FINDINGS section 5 exactly,
with the four first-run tutor-key warnings correctly absent on a second boot.
tps: 1026 samples recomputed across 8 phases, every row within a few tps of the
published table. Thermals: 239 samples, Tctl median 93.4 / max 94.1, **zero
samples at or above 95C**. Per-frame update set checked line by line against
decompiled `Game.UpdatePlay` and `Map.MapUpdate` — correct, in order, including
the draw-half gate.

**One commit added by the orchestrator: 57263e7.** The script linked
`Mods/AutoRimmer` at `autorimmer/Mod`; spec 1.1 owns that decision and states
the layout root-relative, matching every sibling mod repo. Repointed at the repo
root so 1.1's worker inherits a bench that expects the layout its own spec
describes.

**Oddities.**
- The worker's Player.log was gone from disk by the time I verified, so its
  zero-errors evidence was uncheckable. My own boot settled it — same result.
  Worth remembering that the bench's Player.log does not survive a later
  `--batchmode` run.
- Two small overstatements in FINDINGS, neither load-bearing: batchmode frame
  rate quoted as "~2860 fps" against a measured median ~2100 (max 3162), and
  thermal minimum quoted 82.5 against a measured 80.8.
- The spike surfaced two platform bugs nobody had specced for: a symlinked
  engine stub re-roots the game at the Steam install via `/proc/self/exe`
  (silently loading Steam's Mods/, ~40 TypeLoad errors), and Hyprland delivers
  no frames to a hidden special workspace, which stops RimWorld's frame-bound
  tick loop dead (1-2 fps ~= 4 tps) while the process looks healthy. Both fixed
  in the profile scripts; both amended onto the specs that need to know.

### Hard gate: discharged

FINDINGS conclusions posted as amendments on **8 of 14** wave 1-3 specs — 1.1,
1.2, 1.3, 1.4, 2.1, 2.3, 2.4, 3.1. The remaining six (2.2, 2.5, 3.2, 3.3, 3.4,
3.5) have nothing in FINDINGS bearing on them; a decision, not an omission.
Amendments only — no spec body was rewritten.

### Needs Dorian

1. **Install `xorg-server-xvfb x11vnc`** (`sudo pacman -S --needed
   xorg-server-xvfb x11vnc`), or waive the Xvfb acceptance line. It is the only
   thing between 0.1 and `state:done`; verification afterwards is one 3-minute
   run of `./run-agent.sh --xvfb --vnc --quicktest`.
2. **Confirm the modlist deviation.** Guests + RegisterLanes were added to the
   v1 bench (DESIGN omits both from its own-mods list, but justifies the visitor
   cluster as required *by Guests*, and Factions/Guests is the M3 target).
   CashRegister added as a transitive dep. HugsLib and PerspectiveShift dropped
   as unnecessary, both verified. FINDINGS section 2.
3. **FYI, untouched:** `_RimWorld-Test/make-profile.sh` has the same latent
   engine-symlink re-rooting bug. It works today only because Steam's Mods/
   happens to carry most of the same symlinks. Not this run's scope.

### Gate lifted, same session

Dorian cleared both items: installed the packages ("do that sudo") and confirmed
the modlist ("I'm fine with whatever modlist"). Recorded on the issue and the
muster, and written into DESIGN.md (4cf191a) — its own-mods list had omitted
Guests and RegisterLanes while the visitor-cluster line justified the cluster as
existing to serve them, so a worker reading only the list would have built the
wrong bench.

**0.1 closed.** With `xorg-server-xvfb`/`x11vnc` present I ran the last
acceptance line myself: booted to a playable ticking map under Xvfb in 73s,
**0 errors / 9 warnings** (identical set to the live-session boot), 0 exceptions
in Player.log, `127.0.0.1:5900` listening loopback-only, **zero windows on the
live Hyprland session**. Both display modes now demonstrated; all three
acceptance criteria met; issue `state:done` and closed.

**Running it found a bug the spike could not have found**, because the packages
did not exist while it ran: `--vnc` produced an Xvfb with nothing listening.
x11vnc 0.9.17 tests for `WAYLAND_DISPLAY`'s PRESENCE, not its value — from a
Wayland session it prints "Wayland display server detected ... Exiting." and
dies before binding, and `-quiet` swallows the message, so the symptom is a
silent absence. `WAYLAND_DISPLAY=` clears the value but leaves the variable
defined. Fixed in a66d78b (`env -u` for x11vnc, subshell `unset` for the game).
The game had been surviving the same trap only because SDL falls back to X11
when the Wayland connect fails.

Second fix, 292de94: follow-on from my own 57263e7. Repointing
`Mods/AutoRimmer` at the repo root meant the link always resolved, dropping an
About-less folder into the bench. Now guarded on `About/About.xml`, printing
"skipped ... spec 1.1 creates it" until that file exists.

FINDINGS section 8 rewritten from PENDING to verified (cc420dc), including what
the fallback costs: software rendering holds the frame rate below the mangohud
cap, so the sim runs **50.9 tps under Xvfb where the live session gives 60**,
and boot takes 73s against 39s. Section 6's tps table is a GPU-backed-session
table and spec 1.3 should read it that way.

### 1.1 dispatched

**1.1 (097f33a)**, agent:fable, `state:doing`. Nothing runs alongside it: every
other open spec lists 1.1 among its dependencies, so wave 1 is single-file until
the skeleton lands. It is also the first spec in this repo to ship an assembly,
so it sets the `Build:`-commit pattern.

### Handover to another machine

Session budget ran out, so 1.1's worker was stopped deliberately before it
committed anything. It left nothing behind — no branch, no commits, no untracked
files, tree clean — and the issue went back to `state:next` with a note saying
so. Nothing about 1.1 is blocked; it simply has not started.

Repo published: **https://github.com/Borges-Fable/autorimmer** (public). `main`
and `spec/0.1-profile-spike` pushed, plus **21 `refs/bugs/*` and 1
`refs/identities/*`** — the build plan lives in those refs and neither `git
clone` nor `git push` carries them by default, so publishing without them would
have handed the next machine the code and none of the plan.

Two things fail on a fresh clone and both are now in the README, found by
running the sequence into a throwaway clone rather than assuming:

- `git-bug pull origin` dies with "No identity is set" before merging anything.
  A local identity must be created first (`git-bug user new --non-interactive`).
- `git-bug push origin` cannot authenticate against an HTTPS remote through
  gh's credential helper — it does its own transport. Use
  `git push origin 'refs/bugs/*:refs/bugs/*' 'refs/identities/*:refs/identities/*'`
  after any issue change, or the next machine reads stale labels.

Verified recipe end to end: clone -> identity -> pull gives 21 issues, all 8
comments on the closed 0.1, and the wave 1-3 amendments intact.

Also made the profile scripts machine-portable (e1a144f): `RIMWORLD_STEAM` and
`RIMWORLD_TOOLS` now join the existing `RIMWORLD_VAULT` override, with this
box's values as defaults. Re-verified it still builds an identical 32-mod,
0-missing profile here. The bench still needs the sibling mod repos and a
RimWorld install to be useful; spec work that does not touch the game does not.

### State at session end

- **Done:** 0.1 (3fa4cf5) — closed, merged, acceptance fully met.
- **In flight:** none — 1.1's worker was stopped for the handover.
- **Blocked:** none.
- **Next pick:** 1.1 (097f33a, agent:fable), `state:next`, not started. After it,
  1.2 (journal) and 2.3 (spatial) both list 1.1 as their only dependency, so
  they are the first pair that can run in parallel — different files, and only
  one of them needs the running game.

### Needs Dorian (carried forward, not blocking)

`_RimWorld-Test/make-profile.sh` has the same latent engine-symlink re-rooting
bug that cost the spike its first boots. It works today only because Steam's
`Mods/` happens to carry most of the same symlinks. Untouched — not this
project's repo.

## Session 2 — 2026-08-30 (BORGES)

Fable orchestrator on Evan's Windows laptop; fable specs implemented
in-session per the machine model rule, opus specs dispatched as workers.

### Issues

| issue | model | wall time | outcome |
|---|---|---|---|
| 1.1 Skeleton: protocol, verb registry, main-thread loop (097f33a) | fable (in-session) | ~35 min incl. bench build | merged ff 5c751e7..4f61933; closed, all acceptance verified |
| 1.2 Journal: events.ndjson (45d01a3) | fable (in-session) | ~30 min | a1a6b76 + Build da35c3c; closed, all acceptance verified |

**1.2 — what landed.** Journal core (per-session NDJSON keyed by sid, atomic
seq, poller-thread flusher), 7 decompile-verified Harmony postfixes + 2
GameComponent virtuals, cadenced alert diff (no notify path exists), `journal`
+ `journal-selftest` verbs, JOURNAL.md schema contract. Verified live: the
scripted raid/letter/error/downed/break sequence in causal order (the raid's
own letter lands before its dev-provenance event, alerts trail 11 ticks —
exactly the documented semantics); save/load round-trip with zero duplication
and no seq gaps; overhead 0.0039 ms/frame measured through AnalyzerBridge
driving DPA. First boot exposed mapgen corpse-setup flooding death/downed at
tick 0 — gated on ProgramState.Playing; and SteamAPI.Init predates any mod
ctor, so the journal's log capture starts at its boot marker (LogRelay's
backfill owns the pre-ctor window; JOURNAL.md documents both boundaries).

**Process slip, owned:** 1.2's commits landed directly on main instead of a
spec branch — the `spec/1.2-journal` pointer was created after the fact at the
Build commit. Tree identical to a branch + ff-merge; nothing lost but the
ordering discipline. Branch-first restored from 1.3 on.

| 1.3 Time control: advance-until, tps throttle (c9b5769) | fable (in-session) | ~45 min | merged ff ee2acbf..2dd7655; closed, all acceptance verified |

**1.3 — what landed.** TimeDriver (budgeted DoSingleTick loop per the 0.1
shape), pause/unpause/advance, journal-tap halt matchers, deferred results,
Config, ThermalGovernor (file-driven; INERT on BORGES — WMI thermal probed
unsupported; hwmon serves dorian's bench), status advance/thermal blocks.
Verified: exact +60000 halt; until:letter halted the SAME tick a
deterministically-delayed letter arrived; **400K ticks at avg 994.4 tps against
the 1000 hard cap** (caller asked 99999) over 6.7 min, seven natural TimeSlower
spans reported-not-honored while Cassandra wrecked the colony (deaths, Man in
Black, mad animals — the journal chronicled all of it unattended); timeout
exact; pause-interrupt; busy-gating; 0 errors / 9 warnings throughout.
Wave 1 in-mod substrate complete.

**Bench hygiene:** the 1.2 acceptance's `autostart.rws` would have hijacked
every later boot (devMode auto-load) — renamed to `journal-accept.rws`.

| 2.1 Colony digest + what-changed (b2b89f3) | fable (in-session) | ~40 min | merged ff c2131a0..3c4c5cd; closed |
| 2.3 Spatial: viewport, queries, landmarks (1a35e07) | fable (in-session) | ~35 min | merged ff 3c4c5cd..cefa580; closed |

**2.1 — what landed.** DigestVerb (the 2.2/2.4 exemplar), AlertScanner.Snapshot,
Journal (seq,type) ring for main-thread-safe what-changed, selftest `stockpile`
step. Verified across five fixture states (731-1593 bytes); food-days at 0%
error against an independent raw-thing tally; since-bracketing exact. Stated
caveats: 5-pawn size extrapolated (~1.9KB data), power validated at zero only.
Bug found live: `FreeColonistsSpawned` clears/rebuilds its cached list on every
access — snapshot before iterating, now taught loudly in the exemplar.

**2.3 — what landed.** Positions resolver (landmarks/pawn:/thing: everywhere),
LandmarkComponent (save-persisted), CropRenderer (the single crop source),
seven query/view verbs. 5x7 corner placed sight-unseen via queries only and
validated through the independent renderer path; four edge crops clip-honest;
landmark save/kill/load exact; echo crops legible (colonists named in legend).
`catalog.py` does not exist on this machine — glyph policy derives from live
def properties, colors stay the dumped baseviz_catalog.json's; cross-check
debt below.

**Parallel workers.** 2.2 (pawn serializers) and 2.4 (world serializers)
dispatched to opus workers in isolated worktrees at ~17:20 — new files only, no
game launches (orchestrator runs all in-game acceptance), no DLL commits (the
orchestrator owns Build: commits; two workers would collide on the binary).
**Both stopped deliberately at the 95% usage limit** before either committed a
line (one mid-decompile, one setting up its branch). Nothing left behind —
worktrees auto-cleaned, empty branch pointers deleted, both issues back to
`state:next` with PARKED comments carrying fixture guidance for the next
machine.

### State at session end

- **Done this session:** 1.1, 1.2, 1.3, 2.1, 2.3 — all closed, merged
  fast-forward, acceptance personally verified. Wave 1 substrate complete; the
  bridge does act -> advance(until…) -> journal -> digest/spatial-read end to
  end on a live colony. Verbs shipped: ping/status/version, journal,
  journal-selftest (stimulus layer: raid/downed/break/letter-delay/save/
  stockpile), pause/unpause/advance, digest, map-view/find-rect/nearest/
  reachable/room-at/path-cost/landmark.
- **In flight:** none — both opus workers stopped for the handover.
- **Blocked:** none.
- **Next picks:** 2.2 + 2.4 (`state:next`, opus, deps met, PARKED notes on
  each); then 3.2/3.3 (deps 1.1+2.3, met) and 3.1 (deps 1.1+1.2, met). 1.4 and
  2.5 need `rimworld-tools`, which exists only on dorian's machine — natural
  picks THERE alongside the sibling-repo work.
- **Bench state:** `_RimWorld-Agent` (Windows flavor) built and healthy —
  0 errors / 9 warnings baseline reproduced across ~10 boots. No autostart.rws
  armed; scratch saves journal-accept.rws + Autosave-1..5 (collapse-era
  fixtures) remain in SaveData/Saves. Mod DLL current with all five specs.

### Needs Dorian (carried forward + new)

1. `_RimWorld-Test/make-profile.sh` engine-symlink re-rooting bug (from
   session 1) — untouched, not this repo.
2. **catalog.py cross-check** (new): 2.3's ASCII glyph policy derives from live
   def properties because `rimworld-tools/baseviz/catalog.py` is not on BORGES;
   when the two meet on one machine, diff the policies and reconcile BEFORE
   3.2/3.3 build UX on the viewport (2.3's closing comment has the glyph
   table).
3. **WirelessChargingMech missing on BORGES** (not in Steam Mods, not in the
   pack) — the one hole in the v1 modlist here; ship it in the next pack
   publish if bench parity matters before M3.
4. FYI: the Steam Workshop's Storefront (Aug-3 update) breaks the pack-pinned
   Guests (AmbiguousMatch on TryGiveJob) — the BORGES bench junctions the
   pack's pinned copies instead; if dorian updates Storefront upstream, Guests
   needs its patch disambiguated.

**1.1 — what landed.** `About/`, `Source/AutoRimmer/` (7 files, AnalyzerBridge's
shape generalized), `Assemblies/AutoRimmer.dll` in its own `Build:` commit, and
the Windows bench scripts `profile/{make-profile-agent,gen-modsconfig,run-agent}.ps1`
(no python on this box — the generator is a faithful PowerShell port). Registry
is attribute-scan; VerbArgs typed getters treat wrong types as bad-args even on
optionals; every result carries `state:{gameLoaded,tick,paused}`; results and
status.json written atomically. ping is main-thread on purpose (the loop's
canary); status/version answer off-thread from the menu.

**Verification.** Built the bench, booted twice, drove every acceptance case
through the real file protocol: 7-command batch (status/version/ping/echo/
bad-type/mangled-JSON/unknown-op) — 7 results, 0 inbox leftovers, structured
errors exact; kill -9 (`Stop-Process -Force`) + planted command → answered
`stale-on-restart` by the NEXT session at init, not replayed, then a fresh ping
round-tripped at tick 737. Boot health **0 errors / 9 warnings — the identical
Linux-baseline set**. `Mono path[0]` profile-rooted both boots; tick delta
301/5s ≈ 60 tps with the window unfocused.

**The bench on this machine** (muster environment note has the full story):
junctions + hardlinks + real-copy engine stub; `-savedatafolder` + `-logfile`
isolation; launcher refuses battery/second-bench and coexists with Evan's own
RimWorld (which ran undisturbed throughout). Third-party mods junction
READ-ONLY from the MP pack (`C:\RimWorldPack\mp`) after a live incident: the
Aug-3 **workshop** Storefront grew a `TryGiveJob` overload and Guests' PatchAll
died AmbiguousMatch (1 red error); the pack's pinned Jun-16 copy — what Guests
is built against — restored the exact baseline. Own mods are Steam-install
`Mods\` copies, byte-identical to the pack's. **WirelessChargingMech is missing
on this machine** (not in Steam Mods, not in the pack) — the only hole in the
v1 modlist; 38 active regardless (AutoRimmer joined).

**Oddities.**
- Game reports `1.6.4871 rev591`, Version.txt says rev590 — per-build counters
  (handoff doc lore), neither is a build identity.
- Boot-to-ticking-quicktest ≈ 40-70s on this box vs 2-4 min on dorian's — the
  12700H worldgens fast. tps floor differs too; do not port throughput numbers
  across machines.

## Session 3 — 2026-08-30 (dorian's Linux box)

Opus orchestrator. No specs implemented: this session reviewed session 2's five
closed specs, verified them on the Linux bench, and filed the remediation the
review turned up. BORGES unavailable until 20:00, so the plan is scoped to this
machine.

### Review of session 2

Three reviewers: two opus on the code (wave-1 substrate; wave-2 observers), one
sonnet on scope and process discipline.

**Process: clean.** No hidden or uncommitted scope — every file maps to a spec,
`profile/`, or a doc. The `profile/*.ps1` ports are faithful where they can be
and divergent only where Windows requires it, documented in-file. The one
undeclared addition, `journal-selftest`, was disclosed and justified on its
issue. Five `Build:` commits, each DLL-only; no commit anywhere mixes source and
binary. Nothing left behind. One real gap: branch-first discipline is
*unverifiable* for four of five specs — the history is fully linear with no
merge commits, so a real branch-then-ff and a direct-to-main commit labelled
afterwards are indistinguishable. 1.2's slip is admitted in their own RUNLOG;
it may not have been the only one.

**Code: good work, above average — and not mergeable as-is.** Hook points were
chosen from decompiled source rather than guessed, the observer discipline holds
under inspection, the tick loop matches FINDINGS section 9 exactly, and the
closing comments are unusually honest about what was and was not demonstrated.
Both code reviewers nonetheless landed on real defects, none of which the
acceptance runs could have reached.

Filed as two issues, both `state:next`, `agent:opus`, neither requiring any
revert:
- **1.5 (4b65a28)** — three blockers, all the same shape, state outliving its
  owner: `TimeDriver` state survives a game unload (an in-flight advance never
  gets a result and RESUMES against the next colony); `Runtime.Pending` is not
  drained at a game boundary (consumed commands, zero results); and the
  red-error dedupe cap silently disables `halt_on_error` after four occurrences.
  All three violate 1.1's stated absolute invariant.
- **2.6 (3dce29a)** — `find-rect` returns candidates that are not the nearest
  (it sorts by CENTER distance after terminating a ring walk over ORIGINS —
  visible in 2.3's own closing evidence); alert truncation drops by discovery
  order rather than severity, so a Critical alert can be the one cut; the
  digest's `colonists` section is the only one with no cap; `map-view
  radius:30` is documented-legal and always errors on an off-by-one.

**Sequencing consequence:** 2.6 gates 2.2 and 2.4, which are told to copy 2.1's
exemplar — and that exemplar has an uncommented per-colonist `Room.Role` (a lazy
full room analysis), the uncapped list, and a `DangerWatcher` hazard a copy of
the pattern would trip. Both moved to `state:backlog` with GATED comments. 2.6
also gates 3.2/3.3, which build UX on `find-rect`.

Both reviewers independently judged 2.1's two stated caveats to be the right
instinct aimed at the wrong thing: each framed a structural gap as merely
unmeasured. The extrapolated 2.1KB digest size is already outside the stated
1-2KB budget, and "power validated at zero only" hides that gen and draw are
not separable at all, which is invisible precisely at zero.

### Verified on the Linux bench

The Windows-built DLL loads and runs correctly here — full protocol round-trip
(`ping`, `version` reporting `bench:"dorian"`, structured `unknown-op` listing
all 16 verbs), `digest` at 1201 bytes inside budget, `advance ticks:60000`
landing exactly +60000 and returning paused, 0 red errors against the same
9-warning baseline. **No Linux/Windows behavioural divergence in the substrate.**

A 3000-tick advance reported 1123.6 tps against a 1000 cap, which looked like a
breach; the 60000-tick run came in at 1000.84. The cap holds at sustained scale
and overshoots only on bursts shorter than a few seconds.

Two genuine box-to-box differences worth carrying:
- The source builds clean on Linux (0 warnings) once `RIMWORLD_MANAGED` is set
  — the csproj's fallback default is a hardcoded BORGES Windows path, which
  fails loud rather than wrong, but is documented nowhere outside a csproj
  comment. Same source, same output size, **32,941 of 68,608 bytes differ**
  (different Roslyn; this box is on SDK 10.0.104). Binary comparison is
  therefore not a cross-box parity check — behavioural testing is the only one.
- `Journal.cs` uses `WriteLine`, so journals are LF here and CRLF on BORGES.
  Confirmed LF-only on this box. Spec 5.1 makes the journal its primary
  assertion substrate, so golden files will not port between machines.

Thermals deliberately dropped as a test target: that governor exists for this
old laptop, and BORGES handles its own.

### Needs Dorian

1. **Fog of war is a spec ambiguity that must not be a worker's call.** It is
   nowhere in spec 2.3 and got resolved three different ways in one file:
   `nearest` skips fogged things, `map-view` renders full detail under
   undiscovered fog, `find-rect` will return a "buildable" candidate in
   unexplored ground. On a fresh crashlanded map that is most of the map. Three
   defensible answers (agent sees what the player sees / agent sees everything /
   per-verb flag) and it changes 3.2/3.3's contract. Parked as a question in 2.6
   and on the muster.
2. Carried forward unchanged: `_RimWorld-Test` re-rooting bug; catalog.py
   cross-check (only doable on this box, gates 3.2/3.3 glyph UX);
   WirelessChargingMech missing on BORGES; Storefront/Guests version clash.

### State at session end

- **Done:** nothing new closed. 0.1, 1.1, 1.2, 1.3, 2.1, 2.3 remain closed.
- **In flight:** none.
- **Blocked:** none.

**Correction made within the session.** I first labelled 1.5 and 2.6 as though
they blocked things, and moved 2.2/2.4 to `backlog` on that basis. That was too
strong and I reversed it. Nothing is unmerged: all five specs are on main,
closed, and working on both benches — "would not merge as-is" was the reviewers
answering a hypothetical I posed, and I repeated it as if it described a gate.
What exists is a defect list against shipped code. None of the seven blockers
is reachable by anything we are about to build; `halt_on_error` bites at 4.3 and
`find-rect` at 3.2/3.3. The exemplar risk for 2.2/2.4 is real but is a
dispatch-note problem, not a dependency — both are back to `state:next` with a
precise do-not-copy list on the issue.

- **Next picks: 2.6 (3dce29a) + 1.4 (e3d04c7) as the pair.** Disjoint files
  (observers vs a new top-level `rwa/`), and only 2.6 needs the bench for
  acceptance until 1.4's round-trip test at the end. 1.4 is unblocked now that
  its location is settled, and it is the piece that makes the whole thing
  drivable by hand. Then 1.5 (4b65a28) + 2.2 (69ae91f), then 2.4, then 2.5.
- No open contract questions remain. 3.2/3.3 are clear to start once 2.6 lands.

### rimworld-tools now has local version control

Closed at the end of the session. `git init`, 345 files, 34MB tracked of 467MB
on disk, **no remote**. Version control is not publication — the copyright
concern only ever applied to pushing, which I had conflated with versioning.
`Info/decompiled/` (decompiled RimWorld + ~60 third-party mods) is gitignored
with the rest of the derived bulk.

Correction to my own earlier report: I said baseviz and log-watcher were both
unbacked. Only baseviz was — `_meta/` and `log-watcher/` are each already their
own local repo, so the umbrella ignores them rather than swallowing them as
gitlinks. It did swallow `_meta` on the first attempt, because a `.gitignore`
entry written `_meta/` does not match a gitlink, which is a file entry named
`_meta`. Caught and fixed in the same operation.

The point of doing it: spec 2.5 reuses baseviz's catalog and canvas, and an
unversioned dependency cannot be pinned. Vendor-vs-import is still 2.5's open
question, but it can no longer be decided on the grounds that there is no
version to pin.
- **Before 3.2/3.3:** the fog-of-war contract question needs Dorian.

### Needs Dorian — second escalation, found late in the session

**`rimworld-tools` is not a git repository, and two specs are supposed to land
there.** DESIGN.md line 34 and the README both put the `rwa` CLI (1.4) and the
PNG render channel (2.5) in `/home/dorian/projects/rimworld-tools/`; that
directory has no `.git`. Work landing there would have no branch, no commits, no
review trail and no route to the other bench, which breaks the muster's process
law and sits outside the GitHub handover.

Not the orchestrator's call, because `git init` there is not neutral: 467 MB
across 43,884 files, 206 MB of it `Info/decompiled/` — decompiled RimWorld plus
~60 decompiled third-party mods. That is a copyright question and the handover
repo is public. Three options are laid out on 1.4, 2.5 and the muster:
gitignore-the-bulk and keep it private; move `rwa/` into the autorimmer repo
(simplest, but contradicts DESIGN.md so it needs his sign-off, not a quiet
deviation); or a small separate repo for `rwa/` + `rwtest/` alone.

This was found by Dorian asking why the project seemed to span more than one
repo — a good question that the plan had not been checked against.

### Both escalations resolved by Dorian, same session

**1. `rwa/` and `rwtest/` move into this repo.** The CLI is the client half of
a protocol whose server half is here, so a verb and its CLI surface change in
one commit and one clone is a working system; `rimworld-tools` stays unversioned
and reference-only, and nothing decompiled gets published. DESIGN.md's
architecture line and decisions log updated; 1.4, 2.5, 5.1 and the muster carry
the amendment, since their bodies still say otherwise. **1.4 and 2.5 are
unblocked.**

**2. Fog of war is respected across the player-facing surface; `dev:*` is
exempt.** One rule, mirroring the action model's player/dev split, so it cannot
drift into three behaviours again. Fogged cells get their own glyph in
`map-view` rather than being blanked — the shape of the unexplored is
information a player has too.

**And a third thing Dorian added that the specs had missed entirely:** a blocker
must report HOW it clears, not merely that it blocks. Some obstacles are mined,
some deconstructed, and some have to be beaten down by a drafted colonist — a
bare `buildable: false` cannot be acted on. The game already reifies this, so it
gets serialized rather than reinvented: `Building.DeconstructibleBy(Faction)`
returns an `AcceptanceReport` with the game's own reason string, and
`Designator_Deconstruct` answers the attack case with the literal
`RemoveByAttackingTooltip`. Every rejected cell or thing now carries
`removal: mine|deconstruct|attack|none` plus that reason verbatim. Landed on
2.6 (which owns find-rect's candidate path), 2.3, 3.2, 3.3, 3.4 and the muster.
This is the concrete form of the standing candidates-and-reasons invariant, and
it came from Dorian knowing the game, not from the review.

## Session 4 — 2026-08-30 (dorian's Linux box)

Opus orchestrator. Two specs dispatched in parallel to opus workers, both
verified, merged and closed. The session's own process failure is written up
first because it shaped everything after it.

### Issues

| issue | model | wall time | outcome |
|---|---|---|---|
| 2.6 Observer remediation (3dce29a) | opus | ~37 min worker + ~35 min verify | merged ff `09d1a1b..7e83f9b`; closed, acceptance verified |
| 1.4 rwa CLI (e3d04c7) | opus | ~39 min worker + ~15 min verify | merged ff `7e83f9b..28a9fe4`; closed, acceptance verified |

### Process failure: I dispatched two workers into one checkout

The muster allows two workers at once and session 2 ran its parallel pair in
**isolated worktrees**. I did not. Both workers got the same working tree, so
they shared a HEAD: 1.4's four commits landed on top of
`spec/2.6-observer-remediation`, and `spec/1.4-rwa-cli` sat unmoved at `main`
the whole time.

Nothing was lost, for two reasons that were part design and part luck. The file
sets were genuinely disjoint (`Source/AutoRimmer/*` + `Assemblies/` vs `rwa/`
plus two root files — I verified zero overlapping paths). And the 2.6 worker,
finding another spec's commits above its own, **did nothing**: it tagged its
true tip and reported, rather than running the `reset --hard` that would have
deleted `rwa/` from disk under a still-running peer. That was the right call and
it is the only reason this is a paragraph rather than an incident. I then sent
the live 1.4 worker an advisory telling it explicitly NOT to fix the branches
itself, which it followed.

Recovery was two ref moves with the tree untouched. **1.4's history therefore
sits on top of 2.6's rather than beside it** — real and visible in the log. I
chose not to rebase onto `main`: 2.6 merged first as a fast-forward, so 1.4's
base is simply "main as it then was", and rebasing would have rewritten the
exact shas the worker's own acceptance evidence cites.

**For the next orchestrator: pass `isolation: "worktree"` to every parallel
worker, or run them one at a time. Disjoint files are not enough — a shared HEAD
is the hazard.**

### 2.6 — what landed

`Blockers.cs` (the game's own removal taxonomy, serialized not invented), a
find-rect ring walk in CENTRE space with a proven termination bound, alert sort
before truncation, a colonist cap ordered by attention, `MaxSide` 61,
gen/draw/battery_days split, a reserved pawn+fog glyph band, designations
promoted to an overlay, fog across `map-view`/`find-rect`/`nearest`/`room-at`,
and four `journal-selftest` fixture steps (declared).

**Verification highlights** (full evidence on the issue). I re-derived rather
than read: for 9 (near, w×h) combinations I recomputed `center` and `dist`
independently — zero mismatches, ordering monotonic, and `dist 0.0` reached
where the ideal centre is clear. The differential test 2.3 would have failed —
top-1 must not depend on `max` — holds across max 1/3/5/20. The Critical alert
injected LAST came back as `active[0]` with all 8 truncations Medium, and later
a storyteller-generated `Alert_ColonistNeedsRescuing/Critical` did the same
without any fixture. All four `removal` values observed live, and I checked
`'Remove this by attacking it.'` against `Misc_Gameplay.xml:284` rather than
against the code that produces it.

**One acceptance bullet met in a weaker form, stated as such on the issue.**
"Keeps the digest inside budget": the digest is now BOUNDED where it was
unbounded — the actual defect is fixed — but with both caps saturated it
measures **2625 B**, outside DESIGN's "~1–2KB". The dominant term is now
`alerts` at 1053 B, which is 2.1's cap of 12, untouched by this spec. So the
breach moved rather than closed. Filed below.

**Zero red errors under real load**: one 240,000-tick advance (4 in-game days,
20 colonists) with `halt_on_error` at its default true ran to completion without
halting — 19 deaths, 21 downed, 16 mental breaks, 0 `red_error`. LogRelay
**0 errors / 9 warnings**, the exact FINDINGS §5 set.

**First live observation of the thermal governor.** It engaged at ~93.5 °C and
held `scale 0.5`, and sustained tps settled at ~698 against the 1000 cap.
BORGES has no sensor and can never test this; session 3 had deliberately dropped
thermals as a target. The governor works.

### 1.4 — what landed

`rwa/` — `rwa` (the client), `fakebench.py` (a synthetic bench emulating
`Poller.cs`), `selftest.sh` (124 checks), `README.md` (the drive-it manual), and
the root README's location correction. Zero game semantics client-side: no verb
table, generic JSON tree renderer.

I ran everything the worker was barred from: the live round-trip (`advance
--ticks 3000` landing exactly +3000 and returning paused), the documented jq
pipelines as pasted, the game-down path (**refuses the send rather than planting
a ghost command** that would surface as `stale-on-restart`), transcripts and
replay (which reproduces a session including its failures), and `rwa watch` —
the running game re-capped **30.0 → 60.1 → 30.0 fps with no restart**, workspace
revealed and re-hidden, focus never moved, desktop restored.

**A defect I found and fixed myself (`a3a8876`).** Mid-advance, `rwa status
--sample` printed "2699 ticks … paused — tick will not move without `rwa
advance`" — denying that time was moving in the same sentence as the ticks it
had counted. It branched on `paused` and never consulted `tick_delta`, but
paused-and-ticking is this platform's NORMAL driven state, because `TimeDriver`
pins `CurTimeSpeed` to Paused for the whole of an advance. Four states now; the
frame-starved alarm unchanged; selftest still 124/0. Fixed rather than filed
because 4.2's play-loop reads that line to decide whether the colony is running.

### Decisions taken — corrected within the session

I first wrote this section as a "Needs Dorian" list with two questions on it.
Dorian's response, in full: *"there should be nothing on the muster for me. you
take care of these things through agents or a future session."*

So the escalation rule is amended (ORCHESTRATION-PROMPT `a0df478`, muster
comment): **the orchestrator resolves and records, it does not queue.** His two
hard gates stand — 4.3's demo review and 5.2's sign-off — because those are
reviews of finished work, not questions. Genuine how-should-RimWorld-be-PLAYED
questions still reach him through the playbook, which DESIGN already makes the
channel for his game knowledge. Both items are now resolved, in `02948cb`:

1. **`advance until:`** — investigated rather than coin-flipped. All four
   shipped matchers hook `Journal.OnEvent` and halt on a discrete event;
   `threat` is `letter` narrowed to `ThreatBig`/`ThreatSmall`. DESIGN's line was
   simply stale and now says what runs, plus the property that makes the set
   coherent. **`condition` was not deleted**: nothing is emitted when a
   continuous value crosses a threshold, so no existing matcher can say "stop
   when food gets low" — only "stop when the game finally complained". DESIGN
   itself records that alerts fire LATE, so a state predicate is the direct
   answer to a problem this project had already written down. Filed as **1.6
   (fc287ba)**, wave:3, deps 1.3 + 2.2 + 2.4, whose acceptance requires it to
   halt EARLIER than the equivalent alert — being a leading indicator is the
   whole justification, so it should have to prove it.
2. **The digest budget** — "~1–2KB" predated any measurement. Measured 0.7–2.0KB
   typical, 2.6KB at saturation; restated as the worst case (≤3KB, ~1KB
   typical), with an explicit note not to buy headroom by cutting the alert cap.
   Capping alerts to defend a number invented before measurement would have
   undone the attention-model fix 2.6 had just made.

The other carried-forward items were never decisions for him, just tasks with no
owner. Re-homed: the `_RimWorld-Test` re-rooting bug belongs to whoever next
touches that sibling repo and should stop being relisted here; the two BORGES
bench-parity facts (WirelessChargingMech, workshop-Storefront vs pinned Guests)
belong in that machine's session notes.

### catalog.py glyph cross-check — done, and its premise was wrong

Carried since session 2 as "diff the two glyph policies and reconcile them
before 3.2/3.3". There is nothing to reconcile. `baseviz/catalog.py:74` emits a
**2-char uppercase token keyed on the defName** (`DiningChair` → `DC`) as a label
drawn on a coloured, sized cell in a PNG; `CropRenderer` emits **1 ASCII char
keyed on def properties** for a text grid with no colour to disambiguate with.
Opposite constraints — collisions are harmless in the first and fatal in the
second, which is exactly why 2.6 had to reserve a band. Forcing them to agree
would break a fixed-width grid or throw away information the raster can afford.

Two facts also make the old framing wrong. **AutoRimmer reads no catalog data at
all** — `grep -rn 'catalog\|baseviz' Source/AutoRimmer/*.cs` gives three
comments and no code, so 2.3's "colors stay the catalog's" reads like a
dependency that does not exist. And **the dumped `baseviz_catalog.json` has no
glyph field**: 3849 defs carrying `color, designationCategory, isStuff, kind,
label, mod, rotatable, size, stuffColor, stuffable, thingCategory`, with the
glyph computed in python at render time. There was never a stored policy on the
bench to diverge from.

The real hazard is narrower and belongs to **2.5**: it renders the same map
through baseviz's canvas, so the agent gets two visual channels with two
alphabets for one cell (`#` vs a cell labelled `WA`). DESIGN wants the PNG to be
an *independent* second check, so they need not match — but the channel must
declare which alphabet it is using, or 4.2 and 5.1 will compare glyphs across
channels. Recorded on 2.5; **the gate on 3.2/3.3 is lifted** and both were told
so, along with a pointer to read the glyph policy from `Spatial.cs` rather than
from 2.3's now-stale closing table.

### State at session end

- **Done:** 0.1, 1.1, 1.2, 1.3, 1.4, 2.1, 2.3, 2.6 — 8 of 22 specs. Wave 1 is
  complete including the CLI; the wave-2 observer defects are closed before
  2.2/2.4 could copy them and before 3.2/3.3 could build on them.
- **In flight:** none.
- **Blocked:** none.
- **Bench:** `_RimWorld-Agent` healthy, stopped cleanly. Scratch quicktest maps
  only; no saves written. `Assemblies/AutoRimmer.dll` is now a LINUX build
  (pdb path `…/autorimmer/Source/AutoRimmer/obj/Release/`), where every prior
  artifact was BORGES-built — expect ~48% of bytes to differ on the next
  cross-box comparison, which is Roslyn, not a regression.
- **Next picks: 1.5 (4b65a28) + 2.2 (69ae91f).** Disjoint files (substrate
  lifecycle vs new pawn serializers) and only 1.5 needs the bench. 2.2's
  do-not-copy list is now largely discharged by 2.6 — its GATED note should be
  re-read against what actually shipped. Then 2.4, then 3.1 (which supersedes
  the `journal-selftest` fixture layer and unblocks the rest of wave 3).
- **Run them in separate worktrees.**

### Two coverage audits, at Dorian's suggestion — the highest-value hour of the session

Dorian: *"an agent looking at the decompiled code for actions we might have
missed is a good idea, or gameplay loops, a sonnet agent on the learning helper
could be useful too, that's how you're meant to learn the game, that and the
tutorial should be looked at for gaps in what we're doing."*

Two read-only agents: an opus sweep of the decompiled action surface (81
designators, 53 float-menu providers, ~60 mutating gizmos, 53 pawn columns) and
a sonnet sweep of the game's own curriculum (the ConceptDef/tutor system and the
scripted tutorial). **They converged, from completely different directions, on
the same two flagship gaps: production bills and research selection.** The
action audit found them by enumerating `BillStack` and `ResearchManager`; the
curriculum audit found them because they are steps 11 and 14 of the tutorial.
Two independent methods agreeing is the strongest signal either produced.

**The single structural finding, verified myself before acting on it:**
RimWorld puts its preconditions in the UI layer and leaves the model wide open.
`BillStack.AddBill` (`BillStack.cs:69`) is four lines and checks nothing — not
`RecipeDef.AvailableNow`, not the 15-bill cap. The research gate for building
lives in `Designator_Build.Visible:125`, not in `GenConstruct
.CanPlaceBlueprintAt`. `ResearchManager.SetCurrentProject:110` tests only
`baseCost > 0f`. So DESIGN's "transact against the model, never drive widgets"
is right about the mechanism and silently drops the gate — a player verb calling
the model directly is a god-hand that looks correct. **Now an invariant in
DESIGN §Action model (`f690cf2`), with the cite-your-file:line discipline
`Blockers.cs` already follows.**

**The most severe single defect, verified line by line before filing:** an
advance can tick underneath a force-pausing modal, and after that every
timing-out letter in the session silently expires. `TickManager.cs:463` drives
`LetterStackTick` inside `DoSingleTick` — which is what we call;
`LetterWithTimeout.ShouldAutomaticallyOpenLetter => LastTickBeforeTimeout`
(`:35`) means a letter opens ITSELF; `LetterStack.OpenAutomaticLetters`
(`:139-142`) then early-returns forever while any `forcePause` window is up. And
`grep -n 'WindowStack' Source/AutoRimmer/*.cs` returns **nothing**. Vanilla
survives because a forcePause window actually pauses; we pin `CurTimeSpeed =
Paused` and drive ticks ourselves, so we do not stop. No exception, no red
error, no halted advance — the run looks healthy and the colony stops being told
things. **Filed as 1.7 (8555381), p1.**

**Filed:** 1.6 (fc287ba, `until:condition`), 1.7 (8555381, the modal hazard),
3.6 (48f666c, production bills + storage settings).
**Amended:** 2.4, 3.2, 3.3, 3.4, 3.5, 4.1, 4.2, 4.3 — each with file:line
citations so a worker can check rather than trust.

**Scope decisions taken rather than queued** (now in DESIGN's non-goals):
DLC-specific colony management is not driven in v1 — the DLCs stay ACTIVE on the
bench because their presence is the integration test, but the agent does not
manage Royalty/Ideology/Biotech/Anomaly content. One carve-out: assigning an
empty Ideology role, because `createsRoleEmptyThought` is a live standing mood
penalty and the fix is one call. Animal management deferred but still OBSERVED
(seven alerts hang off it — the agent must see what it cannot yet act on).
Cosmetics and the planning grid excluded outright.

**A correction the curriculum audit produced, worth keeping:** unmet ritual
obligations are NOT a mood penalty. No `ThoughtWorker_*` references
`RitualObligation`; the coupling is positive-only, obligations are pruned when
stale, and they are inert below 3 believers. Rituals are a foregone buff, not an
accruing debuff — so they were correctly deprioritised, for a reason nobody had
established.

**4.3's dependency line is wrong and M1 is not reachable as written.** It omits
3.5 entirely, omits the new 1.7, and its own scope says "cook" while production
bills do not exist. Amended on the issue with the three options.

### Handover — session ends at the budget line

- **On `main` and pushed:** everything above. Working tree clean.
- **Both in-flight workers stopped at the budget line and are PARKED.** 1.5
  (4b65a28) and 2.2 (69ae91f) are back at `state:next`; neither committed
  anything; both worktrees removed and both empty branch pointers deleted; tree
  clean. The worktree isolation worked — each had its own checkout and they
  never touched each other.

  **2.2's research was salvaged and is the most valuable thing either produced.**
  It had begun a `PawnSerializer.cs` (349 lines, uncommitted, never run) — that
  file is gone deliberately, because a partly-written unreviewed serializer
  misleads more than it helps. But a sub-agent of it returned a full **pawn
  read-safety catalogue**, now preserved verbatim as a comment on 2.2. It is
  worth reading before anyone writes a serializer again:
  - **`Pawn_WorkSettings.GetPriority` MUTATES** — on a pawn with no work settings
    it logs an error and permanently rewrites that pawn's priorities. The gate
    vanilla itself uses is `workSettings != null && workSettings.EverWork`.
  - **`OpinionOf` is not a read** — it creates cache entries and thought objects
    for the (observer, other) pair and runs a full family-graph BFS per call.
  - **`SocialCardUtility` has static caches keyed on "the pawn the player has
    open"** — calling it repoints the player's Social tab.
  - `Pawn_IdeoTracker.Certainty` writes a Scribe-serialized field from its
    getter; `EffectiveAreaRestrictionInPawnCurrentMap` throws for caravan pawns;
    anything reaching `CurLifeStageIndex` on a cold pawn can rename it.

  These are leads to verify, not established fact — no code ran against them.
  But they are the same hazard shape as the two this project already found the
  hard way (`FreeColonistsSpawned`, `DangerWatcher`), and those were both real.
- **Dispatch model now in force** (ORCHESTRATION-PROMPT updated): every parallel
  worker gets `isolation: "worktree"`; workers never launch the game and never
  commit a DLL; the orchestrator runs all in-game acceptance and owns the
  `Build:` commit. This is a direct consequence of this session's failure.
- **Bench:** `_RimWorld-Agent` healthy, stopped cleanly, 0 errors / 9 warnings.
  `Assemblies/AutoRimmer.dll` is now a LINUX build — first time. BORGES will see
  ~48% of bytes differ on a same-source rebuild. That is Roslyn, not a
  regression; behavioural testing is the only parity check.
- **Next picks after 1.5/2.2:** 1.7 (p1, and it gates M1), then 2.4, then 3.1
  (which supersedes the `journal-selftest` fixture layer and unblocks wave 3).
  3.6 is filed but backlogged behind 2.4.
- **BORGES can close one thing cheaply:** 1.4's `rwa` root resolution lists the
  Windows `-savedatafolder` layout as a candidate but it was never exercised.
  One command there closes it.

## Session 5 — 2026-08-30 (BORGES)

Fable orchestrator on Evan's Windows laptop. Both next-picks dispatched to
opus workers in ISOLATED WORKTREES (the session-4 lesson, applied — zero
branch collisions), plus 1.7 taken by the 1.5 worker per that issue's parked
note. All in-game acceptance run personally on the Windows bench; three specs
closed. 11 of 22 done.

### Issues

| issue | model | wall time | outcome |
|---|---|---|---|
| 2.2 Pawn serializers (69ae91f) | opus | ~23 min worker + ~35 min verify | merged ff cf8e68f..5ea7034; closed |
| 1.5 Substrate remediation (4b65a28) | opus | ~34 min worker + ~25 min verify | merged 3-way b2f037a; closed |
| 1.7 Dialog halt (8555381) | opus (same worker as 1.5) | (in 1.5's time) | same branch, own commits; closed |

**Infrastructure first: BORGES now has a decompiled tree.** No rimworld-tools
here, but `ilspycmd` 9.1 was already installed — decompiled the bench's own
`Assembly-CSharp.dll` to `misc/rimworld/reference/decompiled/RimWorldBase/`
(one-time, ~1 min) before dispatch. Both workers verified accessors against
it; line numbers differ from the Linux tree, so citations are file+member.
Accessor verification no longer needs dorian's box.

**2.2 — what landed.** `PawnSafe.cs` (the read-safety layer: policy trackers
read via AccessTools backing-field refs, EverWork gate, the classification
ladder, fog route), `PawnSerializer.cs` (13 sections, every list capped and
ordered-before-cut), `PawnVerbs.cs` (`pawns` roster + `pawn <id>` drill-down),
`PawnFixtureVerbs.cs` (dev-gated `pawn-fixture`: wound/sadden/tatter/prisoner/
visitor — its own verb because JournalVerbs.cs belonged to the parallel
worker). The worker corrected the preserved research in two places (outfit
field name; CurLifeStageIndex can rename a pawn) — the catalogue said "verify,
don't trust" and the worker did.

**2.2 verification.** Two independent readers per number: fixture-time model
reads + the save file's Scribe XML. Mood 56/37 exact; all six expected thought
groups at identical offsets plus ApparelDamaged −5 arriving BECAUSE tatter ran
after sadden computed expectations (the wear→mood chain live); hediffs 9/9
against both readers (a 10th in my first extraction belonged to the next
animal in the file — checked, not waved through); apparel 20/130→15%
tattered; skills 12/12 vs the save, with Intellectual UI-0/raw-3/permanent
live. The prisoner fixture captured via the game's own SetGuestStatus and the
policies came back NULL via backing-field — the lazy-init-write guard proving
itself on the exact pawn class it exists for. The prisoner then escaped (no
cell): the ThreatSmall letter 3 ticks later is the escape announcement.
**Visitor: weaker form, stated on the issue** — the Factions director owns
arrivals on this bench (Player.log: "the director owns the supply") and
swallowed the forced VisitorGroup across 2+ advanced days. M3 suites must
drive arrivals through the director, not the incident. Zero red errors
throughout; drill-down measured 8.7KB full / 1.9KB for two sections (noted on
4.2: ask for sections).

**1.5 — what landed.** Game-boundary reset (both edges: lifecycle virtuals +
poller heartbeat, one interlocked result claim), `Journal.OnRedError` upstream
of the dedupe cap, throw-proof MiniJson + guarded result build with fallback
envelope, peek-before-write flush with bounded retry, one-lock atomic emit
with a re-entrancy guard (a hole the review had not named: a Verse ToString()
that Log.Errors mid-serialize would have re-entered Emit), the poller cycle
reordered so journal-flushed-before-result holds by construction, injective
result filenames (FNV-1a suffix), range-checked int args, documented+reported
max_tps floor, bounded dedupe tables, and six new dev-gated fixture steps
that make the whole acceptance drivable through the file protocol.

**1.5 verification highlights.** Unload mid-advance: exactly one
`no-active-game` result (~25s — the reviewed-and-accepted 20s abandon
window), journal `session {kind:unloaded, aborted:5}`, 8/8 pings fired across
the window answered. halt_on_error: 9 errors → exactly 4 journal lines; the
10th, fired mid-advance by `error-at`, halted the advance with
`occurrence:10, journal_suppressed:true` and the file STILL at 4 lines.
`weird-result` (null string, throwing ToString, cyclic tree, NaN): one valid
result file, next command answered. 65 journal entries, zero seq gaps.
**Weaker form, stated:** the same-process load-from-menu variant has no
programmatic path before 3.1 — the LoadedGame edge shares the demonstrated
code path but was not driven.

**1.7 verification highlights.** The letter opened ITSELF at its predicted
tick (590) and the advance halted `reason:"dialog"` that same tick, naming
`Dialog_NodeTreeWithFactionInfo` and the letter; tick frozen under the modal
across a 10s watch; after `dialogs-clear`, a second letter halted a second
advance identically — **the queue was not poisoned**, demonstrated explicitly.
Overhead: 967/988/989 tps vs 1000/1006 pre-guard same-session — shared-CPU
noise (Evan's own RimWorld ran throughout; the launcher coexisted correctly).

**Found and fixed by the orchestrator during acceptance** (own commits + Build):
status.json served the dead snapshot's `activeOp` beside `gameLoaded:false`
after an unload — nulled when the game is gone, verified with a second
boot-unload cycle. Plus JOURNAL.md's dev-row now names `pawn-fixture` as the
second writer.

**Consequence recorded on the muster: 3.5 is on M1's critical path.** A
dialog halt is visible-and-stuck; nothing clears the stack until 3.5 ships.
Amendments on 3.5, 4.3 (its dependency line adds 1.7+3.5), 3.4 (manhunters
classify as wildlife), 4.2 (sections economy; dialog as a turn input).

### State at session end

- **Done:** 0.1, 1.1, 1.2, 1.3, 1.4, 1.5, 1.7, 2.1, 2.2, 2.3, 2.6 — 11 of 22.
  Wave 1 fully closed including remediation; the pawn half of wave-2 eyes
  shipped.
- **In flight:** none. Both worktrees removed, both auto-created branch
  pointers deleted; `spec/2.2-pawn-serializers` and
  `spec/1.5-substrate-remediation` merged and kept.
- **Blocked:** none.
- **Bench:** `_RimWorld-Agent` healthy, stopped cleanly. 0 errors / known
  warnings across 4 boots this session. DLL on main is a BORGES build
  (f15f69e + the activeOp-fix rebuild). New scratch saves: pawn-accept.rws
  (the 2.2 fixture save) beside journal-accept.rws and Autosave-1..5.
- **Housekeeping:** two stale `worktree-agent-*` pointers from session 2's
  parked workers deleted (both at 3c4c5cd, no unique work).
- **The session-4 "cheap close" for 1.4's Windows rwa root:** not cheap here —
  BORGES has no python (Store stub only). Left with 1.4 closed as-is; the
  Windows layout line stays unexercised until python lands or a Linux session
  scripts it another way.
- **Next picks: 2.4 (21856e3, opus) + 3.1 (f166fb9, opus)** — deps met,
  disjoint files (new serializers vs new dev-verb layer), separate worktrees,
  only the orchestrator touches the bench. 2.4 follows 2.2's naming as its
  pattern. 3.1 supersedes the selftest fixture layer and unblocks the rest of
  wave 3. Then 3.2/3.3, and 1.6 once 2.4 lands.

## Session 5, round 2 — 2026-08-30 (BORGES, same session)

The session continued past the first handover point. Two more specs closed:
**13 of 23 done.**

### Issues

| issue | model | wall time | outcome |
|---|---|---|---|
| 3.1 Dev-layer verbs (f166fb9) | opus | ~27 min worker + ~30 min verify | merged ff (incl. one orch fix cfe8c08); closed |
| 2.4 World serializers (21856e3) | opus | ~34 min worker + ~45 min verify | merged 3-way 4a2ad9a; closed |

**3.1.** 17 dev:* verbs, per-mutation journal provenance with a result-side
seq join, deterministic-by-default deviations from the DebugActions all named,
letters suppressed by default (the 1.7 wedge). The autostart fixture path
demonstrated end to end: `dev:starter-kit {save_as:"autostart"}` → plain
relaunch → playing the fixture in ~20s, no UI. Acceptance found the session's
only red error — `SetRelationDirect` on a goodwill faction Log.Errors and
no-ops — fixed on the branch (relation now drives goodwill; permanent enemies
refused with the reason), re-verified live, clean.

**2.4.** The best accessor audit of the project: four vanilla getters whose
READ writes the save or advances the RNG, all re-verified by the orchestrator
in the decompile, all routed around in WorldSafe. Live proofs: the
double-read of an unconfigured growing zone (null both times), and the
save-diff around a full research read (252 progress entries before AND after;
naive inserts ~160). The whole acceptance fixture staged in one
world-fixture call; interactions matched a choice letter's four option labels
string for string; rooms demonstrated by having the dev layer BUILD a bedroom
(find-rect sited, 19 walls + door + bed) that the game's own analysis
classified. **Evan challenged the stockpile classification evidence
mid-verification — correctly**: reading back a self-placed item only proved
self-consistency. Re-proven with vanilla hauling as the independent actor:
colonists moved loose pemmican into the Important Foods-only zone on their
own and the observer tracked the transition mid-haul. The lesson generalizes:
a fixture that arranges what the observer then reads proves agreement, not
truth — let the game act between the arrange and the read.

**Knock-on fixes (orchestrator commits on main):** shipped 2.3 `map-view` was
writing the save via `GetPlantDefToGrow` on unconfigured growing zones —
fixed with WorldSafe.PlantToGrow; the digest's threat section now counts
visible fires map-wide (verified live against the fixture fire: digest saw
fires:1 with zero fire alerts active); DESIGN decisions-log entry for the
observer gate-in-the-widget rule; JOURNAL.md dev-row contract updated;
README git-bug traps corrected (# lines are dropped AT STORAGE — measured;
it had already mistitled issue 4.4, retitled via `bug title edit`).

**Also this round:** 4.4 (d32eadd) filed at Evan's request — checklist
budget, retirement evidence, checklist→rwtest final escalation (wave 4, deps
4.1, p3 backlog); two seed game-knowledge lessons from Evan recorded on 4.1
(wood is a standing designation loop; benches go indoors — mechanisms exist,
the rules are playbook material).

### State at session end (round 2)

- **Done:** 0.1, 1.1, 1.2, 1.3, 1.4, 1.5, 1.7, 2.1, 2.2, 2.3, 2.6, 3.1, 2.4
  — 13 of 23. Waves 0-2 complete except 2.5 (needs dorian's box); the dev
  layer and full observation surface shipped.
- **In flight / blocked:** none. Worktrees removed, both spec branches merged
  and kept, tree clean.
- **Bench:** healthy across 6 boots this round, 0 red errors in every
  session's journal, 0 exceptions. Scratch saves kit-accept.rws added;
  autostart.rws created, demonstrated, and deleted; pre24/post24 proof saves
  cleaned.
- **Next picks: 3.2 (57ab92a, opus) + 3.4 (39c9db7, opus)** — deps met,
  disjoint new files, the respond-half's core. Then 3.5 (fable, M1-critical),
  3.3 (fable, M2), 3.6, 1.6. 2.5 and 1.4's rwa Windows check remain
  machine-bound (dorian's box / python).

### Round 3 — dispatched and parked (session end)

3.2 + 3.4 were dispatched to opus workers in isolated worktrees with two
cross-spec contracts settled at dispatch: `flick` belongs to 3.2 (it is a
designation), and player-verb mutations journal as a new `action` event type
mirroring the `dev` shape (the orchestrator owns the JOURNAL.md row when the
first consumer merges). Evan called the wrap-up at the usage line before
either worker finished reading context: **both stopped clean — no branches,
no commits, no files, worktrees auto-cleaned — and both issues are PARKED at
`state:next` with their dispatch comments intact.** Round-2's leftover
worktrees and auto-branch pointers were removed at the same time.

### Session 5 final state

- **Done this session: 2.2, 1.5, 1.7, 3.1, 2.4** — five specs, plus 4.4
  filed, seed playbook lessons on 4.1, and three orchestrator fixes on main.
  **13 of 23 total.** Zero red errors in every bench session; the arrange-
  act-read verification lesson (Evan's catch) recorded above.
- **In flight:** none. **Blocked:** none. Tree clean, everything pushed.
- **Bench:** healthy, stopped cleanly. Saves: kit-accept.rws + pawn-accept.rws
  + journal-accept.rws + Autosave-1..5; no autostart armed.
- **Next session picks:** 3.2 + 3.4 (redispatch; dispatch comments current),
  then 3.5 (fable, M1-critical), 3.3 (fable, M2), 3.6, 1.6. Machine-bound:
  2.5 (dorian's box), 1.4's rwa Windows check (python).

## Session 6 — 2026-08-31 (BORGES)

Opus orchestrator on Evan's Windows laptop. **3.2 closed (14 of 23).** 3.4 built
and pushed but NOT verified; 1.8 filed, part-built and pushed; 1.9 filed. Session
ended at Evan's call at the usage line — **work continues on dorian's Linux box.**

The session's centre of gravity was not spec throughput. It was two design
challenges from Evan that both landed, and three hazard classes found in vanilla.

### Issues

| issue | model | wall time | outcome |
|---|---|---|---|
| 3.2 Designation + zone verbs (57ab92a) | opus | ~33 min worker + ~70 min verify | merged 3-way ccb8d67; **closed** |
| 3.4 Pawn orders + policies (39c9db7) | opus | ~53 min worker | branch pushed, **unverified**, state:next |
| 1.8 advance drives the game clock (b8785e8) | opus | ~20 min, stopped | 1 real commit + WIP accept, pushed, state:next |
| 3.5 / 3.6 recon | fable x2 | ~12 + ~11 min | reports written, findings on both issues |

### Evan's two challenges, both of which changed the build

**1. "You unpause the game to play — why on earth are we not doing that?"**

He was right and my first two answers were bad. I defended the budgeted
`DoSingleTick` loop with (a) TimeSlower immunity, (b) the thermal governor,
(c) exact tick counts. He dismantled all three: raids triple per-tick cost so
immunity buys nothing exactly when claimed; thermals are hardware's job and are
INERT on BORGES anyway; nobody needs exact ticks. The original justification —
frame starvation at 1–2 fps — had been fixed separately by `render_unfocused`
and nobody re-examined the loop. **Filed as 1.8** with the honest history in the
body so it is not re-litigated. The genuinely good argument turned out to be for
HIS side: today's loop DEFEATS vanilla's own force-pause (TimeDriver's comment
says so outright), which is the entire reason 1.7 needed a per-TICK modal guard.
The worker's one commit confirms it — `TickManager.Paused` includes
`WindowStack.WindowsForcePause`, so unpaused, vanilla stops itself and
`OpenAutomaticLetters` is no longer starved by construction.

**2. "Things can just slide over? That's no good."**

Asked whether a blocked placement is reported or silently built over. Checked
rather than answered: outright failure IS reported well (`failed[]` with the
blocker and its `removal`, loop breaks, `requested` vs `placed`). But
`ThingPlaceMode.Near` **slides to a neighbouring cell and returns unqualified
success** — reproduced twice live (a bed onto a bed, [112,108] -> [113,108];
steel onto an occupied cell, [100,113] -> [101,113]), both `ok:true, placed:N`,
no flag. The data is there (`data.at` vs `spawned[].at`) but nothing compares
them. **Filed as 1.9, p1, and it BLOCKS 3.3** — `place-layout` that nudges
blueprints builds a house that is not the one asked for, and the agent keeps
building against a floor plan that no longer describes the world. 3.3 raised to
p1 alongside it.

### Three vanilla hazard classes found, all recorded in DESIGN

1. **A tutorial modal is a force-pause, and it wedges the run.** 3.2's worker
   found `FlickUtility.UpdateFlickDesignation` ending in
   `TutorUtility.DoModalDialogIfNotKnown` -> `Dialog_MessageBox(forcePause)`.
   **The counterfactual is measured, not argued:** `Knowledge.xml` on this bench
   has `SwitchFlickingDesignation` at 0 and no concept above 0, so vanilla WOULD
   have fired it on the colony's first power switch. Verified live that 3.2's
   re-implementation does not.
2. **My grep for it was too narrow, and 3.4's worker corrected me.** I searched
   `DoModalDialogIfNotKnown`; the hazard is any helper reaching
   `new Dialog_MessageBox`. Sixth site:
   `HealthCardUtility.CreateSurgeryBill(sendMessages: true)` — and true is the
   DEFAULT — from the ordinary surgery-bill path. DESIGN now records the sharper
   two-shape taxonomy: behind a FloatMenuOption's action closure is safe while we
   never invoke delegates; on a utility's own execution path is dangerous.
   `ITab_Bills` reaches the same helper, so **3.6 inherits it.**
3. **Write-on-SAVE, a class this project had not seen.** `Bill.ExposeData`
   narrows the live `ingredientFilter` of every bill whose recipe has a
   `fixedIngredientFilter`, during the SAVING pass. Recorded in DESIGN rather
   than a code comment because it invalidates a METHOD: "save the game and read
   the Scribe XML" was sessions 2.2/2.4's independent second reader, and for that
   surface it perturbs what it measures.

Plus two new write-on-read hazards from 3.5's recon, both verified end to end and
neither touched by shipped code: `LetterStack.BundleLetter`'s getter burns a
scribed letter ID, and `Settlement_TraderTracker.StockListForReading` destroys
and regenerates an entire trader inventory through an RNG ThingSetMaker — the
most severe instance of the class found so far, though world-map settlements are
a v1 non-goal.

### 3.2 — what landed and how it was verified

Six verbs (designate/26 types, forbid, unforbid, flick, zone, area), 4 new files,
no existing file touched. All three acceptance bullets driven live:

- **Chop**: 25 mature trees from ONE envelope over 520 cells; after 60000 ticks,
  wood 700 -> 1077 (11 -> 21 stacks) and designations 25 -> 14. Two independent
  readings that pawns took the jobs.
- **Zones**: rice sown 25/25, `wrong_plant` 0, growth 17%; `plant_source
  "backing-field"` proving WorldSafe's guard held. The stockpile was proven with
  **vanilla hauling as the independent actor** — my first attempt sited it on the
  starting loot pile and I threw that away, because reading a zone I had
  contaminated proves nothing. Re-sited on empty ground: all 50 medicine on the
  map converged in, and not one unit of 75 steel 13 cells away came with it.
- **Rejects**: the game's own Keyed strings verbatim. Fog verified as OUR uniform
  gate (`Designator_Mine.CanDesignateCell` accepts fogged cells) — same
  `fogged / unexplored` shape from two different verbs.
- **`claim`**, the one type whose shape differs (immediate `SetFaction`, no
  designation): staged an abandoned bed via `faction:"none"` and drove it. The
  verb self-discloses the difference in a `note`, reports `designations_*` as
  null rather than a misleading 0, and claiming again is refused — the faction
  flip observed rather than assumed.
- 0 red errors; 7 `action` journal lines, one per mutating call, none carrying a
  cheat stamp; dry-runs and refusals wrote none.

**Orchestrator fix during acceptance (037c5df):** `zone add --rect … --filter
meds` — the spec's own bullet 2 — was impossible, because the shared resolver
consumed `filter` as a target selector. The committed acceptance script could not
run as written. Fixed with `filterSelectsTargets:false`, re-verified, and
regression-checked that `forbid --filter` still resolves group:Medicine.

**Process slip against myself:** I committed a DESIGN edit onto the 3.2 spec
branch instead of main. Caught immediately and moved with `reset --mixed` (not
`--hard`, which would have reverted the built DLL the bench was running). Docs
belong on main; a spec branch carries its spec.

### 3.4 — built, pushed, NOT verified

35 verbs across 8 new files; clean Release build; 78-envelope acceptance script
committed. **No part of it has run in the game.** The worker found and fixed a
real bug on self-review (`int.MaxValue * 2` wrapped negative, aborting the
work-giver scan before its first giver — `prioritize` would have failed every
call). It also flagged that **acceptance bullet 3's mechanism does not exist**:
`dev:weather` cannot drive a re-dress because `neededWarmth` comes from the
tile's SEASONAL average (`JobGiver_OptimizeApparel` -> `CalculateNeededWarmth
(pawn, tile, GenLocalDate.Twelfth)`) — verified. The clothes loop is
filter-driven and season-independent, so the bullet's intent is testable but its
stated method is stale.

**Orchestrator debt at merge:** fold the worker's `Pawn_ReadingTracker` guarded
route into `PawnSafe.Policies` (reading policies postdate 2.2, field name
`reading`), and dedupe the two private `action` emitters against 3.2's.

### State at session end

- **Done:** 0.1, 1.1–1.5, 1.7, 2.1–2.4, 2.6, 3.1, 3.2 — **14 of 25** (two new
  issues filed this session).
- **In flight:** none running. `spec/3.4-pawn-orders` (10 commits) and
  `spec/1.8-game-clock-advance` (1 commit + WIP accept) are pushed and unmerged.
  Both worktrees remain on this box; the branches are on GitHub.
- **Bench:** healthy across 3 boots, 0 red errors in every session. `autostart.rws`
  IS STILL ARMED from kit-accept.rws — delete it or the next launch loads a
  fixture. Scratch state on the bench: a claimed bed, a standing lamp, two
  stockpiles, a rice zone.
- **Machine-bound:** 2.5 (dorian's box), 1.4's rwa Windows check (no python here).

### Next picks, in order, for dorian's box

1. **3.4 verification** — the branch is done and pushed; it needs a bench. Its
   own script is 78 envelopes; bullet 3's method is stale (see above).
2. **1.9** (placement exact-or-refuse) — p1, blocks 3.3, and 3.3 is M2.
3. **1.8** — resume from `fa11564`; the core commit is sound, the acceptance
   script is unverified draft. Re-run 1.3's and 1.7's acceptance after.
4. **3.5** (M1-critical) and **3.6** — both now carry verified recon comments
   with exact signatures, gates and hazards. 3.5's load-bearing answer: a trade
   CAN be transacted with no window, but `TradeDeal.TryExecute` has an unguarded
   NRE on that exact path (`Find.WindowStack.WindowOfType<Dialog_Trade>()
   .FlashSilver()`) — make the branch unreachable, do not try/catch it.

## Session 7 — 2026-08-31 (dorian's Linux box)

Opus orchestrator, work handed over from BORGES. **1.8 closed and merged (15 of
25).** 3.4 verified with caveats and deliberately left open; one real defect and
one spec gap found in it. Both of the branches BORGES pushed have now been built
and driven against a live bench.

The session's shape was set by a fact the handover did not anticipate: **the two
boxes have disjoint toolchains.** BORGES has no python, this box has no pwsh.
3.4's acceptance shipped as a 797-line PowerShell driver, so on the box that now
owns the bench it could not run at all.

### Issues

| issue | outcome |
|---|---|
| 1.8 advance drives the game clock (b8785e8) | **GREEN, 109/0/2, merged FF at `0079e00`, CLOSED** |
| 3.4 Pawn orders + policies (39c9db7) | 130/148, 1 real defect, stays `state:next` |
| manual work priorities unreachable (e8f2c32) | **filed**, p2, backlog |

### 1.8 — Evan's challenge, now measured rather than argued

The one commit BORGES left (`fa11564`) was sound and its "unverified draft"
acceptance script needed no correction — only its exec bit changed. That is
worth recording, because the commit introducing it said to trust nothing in it.

**The regression test is the result.** A timing-out letter opens itself from
`LetterStack.LetterStackTick` and stacks a force-pausing dialog. Under 1.3's
budgeted `DoSingleTick` loop we ticked straight through it and
`OpenAutomaticLetters` was starved for the rest of the session. Measured under
1.8: the first letter halted the advance on its own open tick (41202 vs 41202),
the dialog cleared, and a **second** timing-out letter arrived and opened at
41801 vs 41801. The old loop was not merely inelegant — it broke vanilla's own
letter timing, which is exactly what 1.7 had to paper over.

The ladder is exact and replaces 1.3's tps table:

| speed | nominal | measured | overshoot bound |
|---|---|---|---|
| Normal | 60 | 59.9857 | 2 |
| Fast | 180 | 179.9873 | 6 |
| Superfast | 360 | 359.8356 | 24 |
| Ultrafast | 900 | 899.7799 | 30 |

20010 ticks in 22.24s wall at Ultrafast; `status.json` showed `paused:false`
across 109 samples mid-flight with the in-flight `advance` block carrying
`ticks_done`/`target`. Two SKIPs, both refusals to fake a result: an external
pause needs a human on the space bar, and `PlayerCanControl` is unreachable from
the protocol (the script documents the exact `fade-screen` fixture step that
would reach it, and declines to add it because that file is outside 1.8's set).

### 3.4 — verified with caveats, NOT closed

Ported the acceptance to `accept/3.4-pawn-orders.py`: same check ids, same raw
file protocol, no `rwa` dependency. **Both drivers now exist on purpose** —
each is the only one that runs where it lives. Two deviations from the
PowerShell, both where PowerShell forgives what python throws on: `@($null)` is
a one-element array there (so an absent list reads as non-empty), and an
out-of-range read returns null instead of raising.

Built and verified on **main merged into the branch**, not the branch alone:
3.4 sat 16 commits behind, so a branch-only DLL contains no 3.2 code and would
be an artifact that never ships. The merge was clean and the combined build is
0/0, which settles the flagged merge debt as a dedupe question, not a collision.

**Bullets 1 and 4 fully passed** — the draft/move/undraft round trip with the
4000-tick hold, and a surgery bill performed by a doctor under advance inside
the first 6000-tick window, with the `Anesthetic` hediff on the patient
afterwards. Bullet 2 did not run and bullet 3 half-ran, both for fixture
reasons recorded on the issue.

**One real defect:** `warden {mode:"AttemptRecruit", recruitable:true}` evaluates
the mode gate against the BEFORE state and applies `recruitable` afterwards, so
the mode silently does not take — and 3.4's own acceptance makes exactly that
call. The refusal is at least visible in `refused[]`, which is why it is a
defect and not a trap.

**One spec gap, filed as `e8f2c32`:** `work-priorities` correctly refuses
priority 1 or 2 when manual priorities are off, but manual priorities default
OFF on a new colony and nothing in the ~80-verb surface can turn them on
(`playSettings.useWorkPriorities`). 8 of 3.4's 18 failures are unreachable
state, not wrong behaviour — its acceptance cannot pass on a colony the agent
staged itself.

### The fixture lesson, which cost the first run

The first attempt loaded `Autosave-5.rws` after checking its 39 required mods
against the bench's 39 — zero missing, so compatibility was verified rather than
assumed. What was NOT checked was whether the colony was *alive*. It was in a
mood death spiral: 4 of 5 colonists downed, bleeding and untended, and the
designated surgery patient **died mid-run**, which is why `surgery-options`
returned empty. All 44 failures traced to that.

It was not wasted. Zero red errors, and every verb refused cleanly under
conditions no fixture would have produced deliberately: `undraft` on the whole
roster returned gates `["downed","downed","downed","downed","downed","already"]`,
and `assign` on the dead pawn returned a clean bad-args naming the id. **A
compatible save is not a viable save — check the pawns, not just the modIds.**

### New hazard instance for DESIGN

`Dialog_NamePlayerFactionAndSettlement` force-paused the game mid-run and
`advance` returned `reason:"dialog"`. A naming dialog on the ordinary play path
is a 1.7-class wedge not previously in the list, and is concrete evidence for
3.5 being M1-critical.

### Bench state

- **`autostart.rws` IS ARMED, and this time deliberately** — a quicktest map
  staged with `dev:starter-kit survival`, 3 healthy colonists, a research bench,
  32 startable projects, Anesthetize addable. It is a known-good fixture, not a
  stale one. **It still needs the stockpile re-sited away from where the
  colonists idle before 3.4 is re-run** — that is what broke bullet 2.
- **`Prefs.xml` was changed from 640x480 windowed to 2560x1600 fullscreen** so
  the run could be watched. Backup at `Prefs.xml.bak-640x480`. The small
  resolution was a deliberate cheap-render choice; restore it for unwatched runs.
- The launcher re-applies its `special:rwagent` windowrule every boot; `--no-rule`
  skips it.

### Next, in order

1. **3.4**: fix the `warden` lever ordering, re-site the fixture stockpile,
   re-run. Bullets 2 and 3 are the only ones outstanding.
2. **1.9** (placement exact-or-refuse) — p1, blocks 3.3.
3. **3.5** (M1-critical) and **3.6**, both pre-armed with verified recon.
4. Orchestrator debt still owed: factor the four private `Act` emitters
   (AreaVerbs, DesignationVerbs, ZoneVerbs from 3.2; PawnActs from 3.4) into one
   helper. Both workers left the same comment saying the orchestrator owns it.
   They differ in one behaviour: 3.2 drops null-valued extras, 3.4 keeps them.
5. **2.5** is unblocked here — it needed dorian's box and this is it.

### Session 7, round 2 — 3.4 closed (16 of 25)

Cleanup round at Evan's call, before designing an orchestration session. 3.4 is
CLOSED: 139 of 147, all four bullets in ONE run, zero red errors, merged
fast-forward at `7996fe6`.

**One code fix, four fixture rounds.** The score went 130 -> 133 -> 138 -> 139
and only the first step was a code change. Everything after it was the fixture.
That ratio is the finding worth carrying into how we plan sessions.

- **The defect (`0a5085d`):** `warden` ran the `mode` gate — which reads
  `pawn.guest.Recruitable` via `ModeHidden` — two levers before it applied
  `recruitable`. So `{mode:"AttemptRecruit", recruitable:true}`, the call 3.4's
  own acceptance makes, returned ok:true with the mode silently unapplied. Order
  is now clear -> recruitable -> mode -> …, which is also how the tab behaves.
- **Bullet 2** needed the stockpile moved off the colonists: phase 2 spawns the
  steel at `pawn:A`, and on top of the zone it lands already-stored, so there is
  no haul job. Now: *"hauling steel x75 to Stockpile zone 1."*
- **Bullet 3** needed a rested pawn. `JobGiver_OptimizeApparel` runs at a free
  think tick and the pawn had been going to sleep first.
- **Bullet 4** needed doctor capacity. Cutting the colony to two pawns, where
  the patient could not doctor, left one doctor at flat priority and the surgery
  never happened.
- **5.3 fired for the first time ever** — the disabled-work-type red-error
  pre-check had been a NOTE in every previous run because no actor happened to
  have a disabled work type.

**The fixture table in `accept/3.4-pawn-orders.md` is wrong by omission.** It
asks for "at least 2 visible colonists". It actually needs an actor capable of
Hauling and Doctor, enough doctor capacity, rested pawns, and a stockpile sited
away from where colonists idle. Each missing row cost a full bench cycle.

**And the actor is not deterministic.** `pawns {filter:"colonist"}` returned a
different order for the same colony across two calls in one session — spawning a
pawn reordered it. Phase 0 keys A and B on `roster[0]`/`roster[1]`, so the
acceptance rides an unstable index. Worked around by making every colonist in
the fixture suitable, which is a workaround. Either `pawns` should promise a
stable order or phase 0 should SELECT by capability. **Decide this before more
position-keyed acceptance is written — 3.6 and 4.3 will hit it.**

Remaining 8 failures are all `e8f2c32` (work priorities 1 and 2 unreachable with
manual priorities off, and no verb turns them on). Not fixable inside 3.4.

**Bench fixture, `autostart.rws` at tick 58205:** quicktest map, 4 colonists at
100% health with needs topped up and none with Hauling or Doctor disabled, a
144-cell stockpile with 115 free sited 16+ cells clear of the pawns, research
bench, 32 startable projects, Anesthetize addable. Start here.

**Process note against myself:** I left ~15 stray shells running. My waiters were
`until ! pgrep -f '<pattern>'` where the waiter's own command line contains the
pattern, so `pgrep` matched itself and the loop could never exit. I hit this
once with `1.8-game-clock`, said so, then wrote it four more times. Use the
task-completion notification instead of polling, or match on something the
waiter does not itself contain.

### Session 7, round 3 — seek-at-will, and a long play session that filed five issues

**`seek-at-will` shipped (git-bug 3a0e042), merged, closed.** The first
MOD-AWARE verb in the repo: it reflects into SeekAndKill with no compile-time
reference, calls the mod's own `ShowsSeekGizmo` as the gate authority rather
than copying it, and toggles through `MpCompat.SyncedToggleSeek`.

Evan's brief was narrow and correct: "all we need for combat is to flip that
thing on and to configure it in a way that respects the pawns skills, or to make
it not on at all, they also always need to make sure flee mode is set to attack".
Hostility stayed `assign`'s lever — one setter, one owner — and `seek-at-will`
WARNS when a seeking pawn is not set to Attack, which is how the pair stops
drifting without duplicating the setter.

**Two raids, zero combat orders.** 400 points / 6 hostiles killed to the last;
700 points / 10 hostiles fought off. Journal across the second:
`{wear:11, equip:6, unforbid:2, assign:4, seek-at-will:5, drop:1, policy-new:1}`
— no `draft`, no `attack`, no `move-to`.

**A code review of the file found nine defects before it shipped**, three of
which defeated its own purpose. Worth repeating as a process point: the file
built clean, read well, and was wrong in ways only an adversarial pass caught —
the hostility warning skipped the `already` path, so an idempotent re-issue (how
an agent CHECKS readiness) reported zero warnings; every read printed "NOT
WRITTEN — the journal writer is closed"; every rejection discarded diagnostics
it had already computed.

#### Five issues filed, all from playing rather than reading specs

| id | what |
|---|---|
| `4087644` p1 | **Job verbs report success for orders that did nothing.** `TryTakeOrderedJob` returns true when `curJob.JobIsSameAs` — a false positive on every did-my-action-work check. Seen live: `equip` returned `accepted:1` with `job_def:"Wait_MaintainPosture"`. |
| `1a072fa` p1 | No verb can turn on FSWA auto-arming, so pawns cannot arm themselves. |
| `1eb2262` p1 | `pawns` returns an unstable order; acceptance keyed on `roster[0]` is flaky. |
| `826d4bf` p2 | No verb can use a targetable item on a target — resurrector serums, shock lances, EMP all unreachable. |
| `e8f2c32` p2 | Manual work priorities unreachable (filed earlier this session). |

Plus two in the FSWA repo (`f512ec0` role-from-traits/skills/passions,
`8070f34` non-CE defaults), because that logic is deterministic and belongs in
the mod, not guessed per call by an agent.

#### Evan's framing that reshaped the attribution problem

> "We're in the business of optimizing, we can't have any features we're wasting
> tokens on, and it turns out the whole time another pawn was doing them by
> themselves. A job is done when it's done, but the reason it's done needs to
> come along with that."

The point is COST, not correctness. If the agent cannot tell which of its orders
were redundant, it keeps paying tokens to issue instructions the colony would
have executed anyway — and it can never learn which classes of instruction to
stop sending. Three failure shapes, only the first fixable by better reporting:
the order silently does nothing (confirmed bug, `4087644`); the order works but
they would have done it anyway (no bug, and the outcome is identical); the order
works and is interrupted. An investigation into the cheapest attribution signal —
including whether redundancy can be detected BEFORE spending the order — was
dispatched and is the input to the next decision.

#### Play lessons for the playbook (4.1)

- **Skill is not assignment.** A queued medical bill does nothing unless some
  OTHER pawn has that work CHECKED — a pawn cannot operate on itself, and with
  manual priorities off the Work tab is a checkbox column where 0 is unchecked.
  Evan caught this live: the only pawn with Doctor ticked was the patient.
- **Vanilla auto-forbids weapons dropped by downed or dead pawns.** 8 of 23 on
  the map were unusable until `unforbid`. FSWA skips forbidden weapons
  explicitly, so autonomous arming silently does nothing — while a direct
  `equip` order bypasses forbidden, which is what MASKS the problem.
- **But do not unforbid during a fight**: a colonist immediately went to haul an
  assault rifle to a stockpile mid-raid.
- **`equip`/`wear` are jobs, not writes.** The pawn must walk to the item and
  combat interrupts it. Our best melee fighter spent an engagement empty-handed.
- **A policy beats an order for durable state.** Nine forced `wear` orders and a
  `drop` failed to get armour onto a pawn whose apparel policy preferred a
  parka; one `policy-new` + `assign` did it for all four instantly.
- **Seek releases a pawn when danger drops**, even with hostiles still alive — a
  full-health swordsman went back to hauling wood with three raiders on the map.

#### Bench

`autostart.rws` intact at tick 89426 and NOT overwritten with the post-raid
wreck. Four colonists, marine armour, needs topped, Doctor and Hauling checked
for all — start here.

## Session 8 — BORGES, 2026-08-31. `32b9e01` acceptance: the fix is real, and so was the fixture

First orchestrator session on BORGES rather than dorian's box, and the first
shipped DLL built here. Work picked per session 7's handover: acceptance on
`orders-makingfor` before the audit, because its claim is a RUNTIME side effect
that no amount of re-reading the source can measure.

**`32b9e01` CLOSED. 18 of 25.** Merged `24e5f7a`, DLL `8d4ff00`, acceptance
driver `1bf2913`. One new issue filed: `0d9cbd7`.

### What was proved, in one line each

| phase | build | result |
|---|---|---|
| R | repro | one `orders` call moved a bill by **594** then **555** ticks on two fresh fixtures, clock stopped |
| M | main vs branch | `blocked_total` **0 -> 1**; the branch returns `DoBillsButcherFlesh` blocked, reason `"Missing 1x corpses"` |
| F | branch | the field is untouched, and stays untouched across a repeated call. 0 red errors |
| D | **both** | one `orders` call flips a bill's SCRIBED `paused` flag false -> true |

Two fresh fixtures giving two DIFFERENT deltas inside `IntRange(500,600)` is the
RNG-burn evidence — the draw is `Rand.RangeInclusive` off the shared stream, and
there is no verb that reports `Rand`'s position.

Final run against the SHIPPED binary after the merge and the `Build:` commit:
19 checks, 0 failures. The committed DLL is the one that was verified.

### The comparability problem, and the fix for it

`-quicktest` generates a NEW random map every boot, so main's baseline and the
branch's run would have been different colonies — a before/after across two
different worlds is not a before/after. Staged a clean fixture map and saved it
with `dev:starter-kit {save_as:"autostart"}` at tick 607; both builds then booted
the identical colony with no UI. **`autostart.rws` is now ARMED on BORGES**
(it was not before) and every future bench launch loads it instead of honouring
`-quicktest`. Deliberate; delete it to go back.

### Four fixture requirements the acceptance .md does not state

Every one of them produced a run that would otherwise have passed for the wrong
reason. The .md's own fixture table names four such ways; these are four more.

1. **`world-fixture {steps:["bench","bill"]}` DOES NOT CHAIN** — filed as
   `0d9cbd7`. `FindBench` with no `bench` arg returns the first `TableButcher`
   in the lister, not the one the `bench` step just spawned. Observed live:
   `bench.id 23492` / `bill.bench_id 23491`, four bills stacked on the old
   bench, none on the new one, `orders` scanning an empty bench, `total:0`, M1
   vacuously green. The verb's own error string already claims the chaining it
   does not do.
2. **F4 is a RADIUS test, not map-wide.** `TryFindBestBillIngredients` searches
   within `bill.ingredientSearchRadius` of the giver. My first guard refused
   map-wide and sent the run off to destroy a corpse 53 cells out of play.
3. **`total=0` on main is the CORRECT baseline.** `StartOrResumeBillJob`'s two
   arms are mutually exclusive: `if (makingFor != pawn) { write } else if (flag)
   { JobFailReason.Is(...) }`. main takes the write arm, sets no reason, and
   `ScanWorkGivers`' `!HaveReason` continue drops the giver. `ShouldSkip` does
   NOT gate it away — it returns false as soon as any bill giver has
   `AnyShouldDoNow`. So the giver is reached on both builds and the runs are one
   `if` apart. An earlier draft of my driver demanded `total>0` and stopped a
   correct run.
4. **Phase D needs a warm `resourceCounter`, and the right recipe.** Three
   compounding traps: `CountProducts` reads `map.resourceCounter`, which counts
   STOCKPILED things only; that counter rebuilds on a TICK, so with the game
   paused for the run it never refreshes and `current_count` stays 0 whatever is
   spawned; and `ButcherCorpseFlesh` and `Make_StoneBlocksAny` have no fixed
   product, so their counter is 0 forever regardless. A stove's
   `CookMealSimple` has one. **Order matters: warm the counter, THEN add the
   bill** — a bill that exists while the clock runs gets its flag flipped by an
   ordinary work-giver think tick, leaving `orders` nothing to prove.

### The acceptance driver found two of its own bugs, and that is the point

`accept/32b9e01-orders-makingfor.ps1` reported **M2 and F.5 as FAILURES when the
branch was right**:

- the giver defName is `DoBillsButcherFlesh`, not the `DoBillsButcherTable` I
  guessed from the building's def;
- a `/Read-only/` search is case-insensitive in PowerShell and matched the NEW
  note's own phrase "asking is **not** read-only". A negative assertion has to
  name the sentence it bans, not a word that survives into the replacement.

Both were caught by reading the raw envelopes in `.accept-evidence/` rather than
the runner's summary — which is exactly the orchestration rule "re-derive
headline numbers from raw artifacts rather than trusting a summary table",
earning its keep against my own script. The driver now copies every result
envelope per run.

### Notes for whoever is next

- **BORGES has no python**, confirmed again (Store stub only). Every acceptance
  written here speaks the raw file protocol in PowerShell. `rwa` cannot run.
- **`dev:destroy {mode:"killfinalize"}` on a pawn leaves NO corpse.** It
  destroys rather than kills. There is no verb that kills a pawn, so an
  animal-corpse fixture currently has no clean route — I worked around it by not
  needing one. Worth knowing before someone plans a butchering fixture.
- **Butcher bills exclude HUMAN corpses by default**, so a human corpse next to
  the bench does not un-starve the bill. This cost a round: I built a "positive
  control" on one and read its failure as evidence the giver never ran.
- The build is `dotnet build -c Release` **from `Source/AutoRimmer/`**, not the
  repo root — there is no solution file at the top.
- The embedded pdb path in the shipped DLL is now this box's. main's previous
  DLL carried dorian's Linux path; same in-repo relative shape, different root.

### Next, in order

1. **Audit + merge the other three branches** — `auto-arm-lever` (already merged
   with main, clean), `stable-pawn-order`, `manual-work-priorities`. Only
   `DESIGN.md` overlaps, additively, and this session's merge already resolved
   one such conflict by keeping both entries; `stable-pawn-order` and
   `manual-work-priorities` also both edit `accept/3.4-pawn-orders.{md,py,ps1}`.
   `manual-work-priorities` unblocks 8 of 3.4's checks.
2. **`2f2796e`** — verify the remaining backlog diagnoses before building to them.
3. **1.9** (placement exact-or-refuse) — p1, blocks 3.3.
4. **3.5** (M1-critical) and **3.6**.
5. Orchestrator debt still owed and NOT paid this session: factor the four
   private `Act` emitters (AreaVerbs, DesignationVerbs, ZoneVerbs from 3.2;
   PawnActs from 3.4) into one helper, and fold `Pawn_ReadingTracker` into
   `PawnSafe.Policies`.

### Bench state

`autostart.rws` at tick 607 — quicktest map, 3 healthy colonists (Yun #215,
Foxy #218, Slick #221), no corpses, no butcher tables, clean. Every phase stages
its own fixture on top and the leftovers are not saved back. Zero red errors in
every run this session. `Prefs.xml` is 3072x1875 windowed, devMode on.

## Session 8, round 2 — three branches audited in parallel, all three merged

Evan's call: one opus auditor per branch, three at once, each in its own git
worktree. **21 of 25.** `1a072fa`, `1eb2262`, `e8f2c32` all CLOSED. Merges
`5074a5b`, `fda8e74`, `99a54e3`; DLL `09a4ace`.

Workers audited only — the orchestrator ran every in-game check personally, per
the standing rule. Each committed its own fixes to its own branch: `addd6d3`,
`60a4226`, `d31ed19`.

### The headline: 3.4 now passes 82 of 82

`pwsh accept\3.4-pawn-orders.ps1 -Phase 4,5` on the merged DLL, one clean run.
The eight checks that were unreachable at 3.4's close (4.7a-e, 5.1a-c) all pass.
Zero red errors across the entire session, no modal left behind.

Phase 0 now reads: *"actor A = 218 (Foxy) - selected by predicate, 2 of 3
colonists qualified"* / *"not eligible as actor: Yun: Hauling disabled"*. Under
the old `roster[0]` rule the actor would have BEEN Yun, who fails six checks for
exactly that reason. That is `stable-pawn-order` paying for itself in another
spec's acceptance on its first run.

### What each audit actually caught

**`1a072fa` auto-arm** — the auditor decompiled the bench's own
`FindSuitableWeaponAndAmmo.dll` with `ilspycmd` rather than trusting the issue.
Two real defects: `IsAutoArm` is `Get?.optedIn.Contains(pawn) ?? false`, so a
missing tracker answers the LEGAL value `false` and **no `catch` can see it** —
the observer would publish "auto-arm is off" for a game whose tracker was gone;
and two OPTIONAL probes sat inside a `catch` that nulls the lever, so a throw in
code that only produces the EXPLANATION would have disabled the feature. Plus
two acceptance-doc errors, one of them backwards: an ordered drop lands
**FORBIDDEN** (`TryDropEquipment(…, bool forbid = true)`, and
`JobDriver_DropEquipment` passes three args), FSWA skips forbidden things, so the
`unforbid` step is load-bearing rather than belt-and-braces.

**`1eb2262` stable order** — the once-corrected diagnosis survived AND got
stronger: `PawnVerbs` at the branch point already sorted attention-desc with an
id tie-break, which makes the original "a spawn reordered the roster" story
*impossible*, not merely unproven. Confirmed live: a spawn APPENDS
(`215,218,221` -> `215,218,221,27032`). The audit's real find is a limit on the
promise — **`id-asc` stabilises the SEQUENCE, not the MEMBERSHIP**, because the
cap still cuts by attention, so above the cap the surviving set moves with mood.
True only while `more == 0`, now said in code, DESIGN and the acceptance.
Demonstrated at `cap:2`, which dropped 218 out of the set entirely.

**`e8f2c32` manual priorities** — the diagnosis confirmed at the strongest
available level: `Notify_UseWorkPrioritiesChanged` is `workGiversDirty = true`
and grepping it over the whole 1.6 tree returns exactly TWO hits, its declaration
and its one call site. So a bare field write really is inert. The defect found:
`work-priorities`' matrix path never calls `outcome.Ok`, so `Outcome.Result`
stamped every successful write `journal_seq: null, "nothing was mutated"` over a
call that had just journaled — and 3.4's `4.7e` asserts `journal_seq >= 1`. The
branch would have failed one of the eight checks it exists to deliver.

### Two pre-existing defects on main, found sideways and fixed

1. **The red-error watermark has been near-zero all along.**
   `accept/3.4-pawn-orders.{ps1,py}` took it with `journal {limit:1}`, but
   `JournalVerbs.Read` updates `last_seq` BEFORE the `since_seq` skip and breaks
   on `events.Count >= limit` BEFORE the append — so that call reports the
   **second** line's seq. 3.4's red-error check was scanning the whole journal
   and could charge a stale error to a clean run. Now
   `{since_seq: 999999999, limit: 1}`, the idiom
   `accept/1.8-game-clock-advance.sh:165` already documents. Verified live:
   `seq0 = 14` on a fresh load, not 2.
2. **3.4 check 4.5d asked whether `Dig` returned something, not whether the key
   was there** (`0a62fbf`). `SurgeryWarnings` returns an EMPTY list when nothing
   is wrong, and `Dig` cannot represent that — PowerShell unrolls an empty array
   on `return`, so the caller reads `$null` and "no warnings" is
   indistinguishable from "no warnings FIELD". It went RED against a correct mod
   on the first fixture clean enough to raise zero warnings. 3.4 closed green
   only because its old fixture happened to trip one.

**And a warning to my future self, recorded because I did it today:** the
tempting fix is `return ,$cur` in `Dig`. Do not. Every other caller relies on
`Dig` unrolling arrays and re-collecting with `@(...)`, so wrapping made a
3-colonist roster read as 1 and turned the 35-verb registry check red. I ran it,
saw both failures, and reverted. The narrow fix at the one call site is correct.

### Fixture facts learned this round

- **`autostart.rws` has NO violence-capable colonist.** All three roll
  Violence-disabled, so `auto_arm` was REFUSED on every one of them — which
  proved the gate and its citation (*"the Assign-tab checkbox is not drawn
  (PawnColumnWorker_AutoArm.HasCheckbox) and the gizmo is disabled with
  FSWA_CannotViolent"*) but blocked the accept path. Stage one with
  `dev:spawn-pawn {violence_capable:true}`. Not in any fixture table.
- **Phase 4 wears its fixture out.** Re-running it applies `dev:damage` again and
  the patient goes down, failing 4.3b. Reload `autostart.rws` between runs
  rather than re-running in place.

### Next, in order

1. **`2f2796e`** — verify the remaining backlog diagnoses. Three-for-three of the
   diagnoses audited today survived, but two of five checked in session 7 did
   not, and the remaining filed-from-play issues have not been through this.
2. **`4087644`** (p1) — job verbs report success for orders that did nothing.
   This is the one that matters for 4.2/4.3: it lands squarely on the play
   loop's act -> read -> think cadence, and a false positive there would make
   4.3's ten-day demo lie.
3. **1.9** (p1, blocks 3.3), then **3.5** (M1-critical), then **3.6**.
4. **4.1 then 4.2** are now the only things between here and the play loop —
   both prose, no C#. 4.2's other three deps (1.3, 1.4, 2.1) are closed.
5. Orchestrator debt STILL owed, untouched for a third session: factor the four
   private `Act` emitters (AreaVerbs, DesignationVerbs, ZoneVerbs, PawnActs) into
   one helper, and fold `Pawn_ReadingTracker` into `PawnSafe.Policies`.
6. Open and unaddressed from today: `0d9cbd7` (world-fixture steps do not chain)
   and `70ac258` (`things` has `pawns`' bug, and the fix now ports mechanically).

### Bench

`autostart.rws` unchanged at tick 607 — 3 colonists, none violence-capable, no
corpses, no benches. Every phase stages its own fixture on top; nothing was saved
back. Four worktrees still on disk under `.claude/worktrees/` (three from this
round's auditors, one stale from session 5) — `git worktree prune` when the
branches are no longer wanted.

## Session 9 — 2026-08-31 (dorian's Linux box AND BORGES, concurrently)

Two orchestrators ran the same calendar day without seeing each other's tree
until a merge at the very end. **No numbered spec closed — still 21 of 25.**
The session's yield is two large in-flight defect fixes (neither proven
in-game by session's end), a backlog verification pass, and three more specs
authored-but-unmerged for session 10 to audit. This section is written from
three sources that do not always agree on scope, and where they don't, that is
said rather than resolved: the run ledger `git-bug fbb2c59` (dorian's box,
comments #0–#8, last edited 19:03), `HANDOFF.md` as committed at `d153521`
(BORGES, written at a hard stop), and `git log` on `main`, which is the only
place the two threads' full combined shape is visible — the ledger's own plan
never mentions 4.1, 70ac258 or `bc2250b`, because that work happened on the
other machine.

### Thread 1 — dorian's box (ledger `fbb2c59`, agent:opus)

Three lanes: **A** = 2.5 PNG render channel (`f7b6207`, includes vendoring
baseviz), **B** = `4087644` remainder (job verbs report success for orders
that did nothing), **C** = `2f2796e` (verify the open backlog before building
to it, read-only). A and B each got an isolated worktree and a vet before any
build (`VET: CONFIRM|AMEND|BLOCK`); both vets AMENDED the plan and both were
overruled or corrected at least once by the orchestrator re-reading source
directly — recorded in full on the issues, not repeated here.

**4087644 (B) — merged `983d11a`, NOT closed.** `TryTakeOrderedJob`'s
early-out is confirmed unconditional on `queue`, and worse queued than not:
a collision with `queue:true` enqueues nothing and leaves no trace, so the
caller believes it queued an action that was never created. The "all 17 job
verbs are blind" framing in the issue body was wrong — the vet found the
blind set is **14**: `prioritize` already shipped this exact pre-check,
`move-to` already rejects via `already-there`, and `rest-until-healed` flips
a field on the *running* job, a real mutation, so converting it would have
made it less honest. The issue's own acceptance contradicted its own comment
#1 on whether a wasted order should journal (body said no, comment #1 said
yes); the orchestrator ruled comment #1 supersedes and amended the body in
place. **In-game acceptance is what session 9 owed and did not fully pay**:
a live run (`accept/4087644-order-honesty.py`, swept to 51 checks after the
first draft couldn't reach phase 1 against invented envelope shapes) went
**48 of 51** on the merged tree — three real behavioural gaps, filed as
`ac407f1` (p1): a drafted-only refusal (`tend`) writes a journal row while
most other refusal gates still don't; the `ordered` triple (`queuedNode &&
workGiver != null && forced`) is correct for work-giver jobs and wrongly
false for direct orders like `wear`, which have no `workGiver`; and a queued
order does not appear in `job_queue.total`. None of that is closed.

**2.5 (A) — merged `762e942`, NOT closed.** The spec's premise was checked
and found false: **baseviz has no PNG renderer** — grep for png/Pillow/numpy/
cairo/matplotlib is clean, `canvas.py` is ASCII-only, the colored grid is
browser JS. The raster encoder is new code (hand-rolled zlib+struct, not
Pillow, for byte-for-byte determinism across versions), and "reuse baseviz"
reduces to reusing `catalog.py` and `viewer.js`'s drawing rules. Vendored
`baseviz/` into this repo (pinned at `rimworld-tools` sha
`eabba3eb9fbc435bbdcb2a6250d1e3734170d992`, MIT header per Evan's on-issue
call, no separate LICENSE file), folded the 143-line `CatalogDump.cs` into
AutoRimmer as source rather than a second mod (Evan's "what can be in the mod
should be"), and caught a real `.gitignore` leak before it shipped —
`Source/**/bin/`/`obj/` are anchored at the repo root and do not match a
vendored subdirectory, so four intermediates would have published absolute
`/home/dorian/.steam` and `.nuget` paths into a public repo.

**Verified live, twice, with a real defect found and fixed in between.** First
render: determinism held (byte-identical PNG twice, offline replay matched),
but **a fresh reader answered 2 of 3 legibility questions wrong** — a 1px
inset gap made adjacent walls read as a dotted line with no way to tell an
opening from an unlabelled cell, and six single-cell doorway "rooms" were
counted as real rooms, quoted back as the room count. Both are fix-the-source
bugs, not renderer bugs: `map-dump` now emits `doorway`/`proper` on rooms and
`impassable`/`natural_rock` on things, walls are drawn by shape rather than by
per-cell label. Re-rendered after the fix (`aac6209`): determinism still
holds, room-count is now correctly **0** on that fixture (six door-pockets,
zero enclosed rooms) — but that means the re-render's fixture has no enclosed
room, so it **cannot answer** the room-count/stove-location questions it was
supposed to prove. **Legibility is therefore still unverified**, stated as
such on the issue rather than counted as a pass. Separately: mechanical
acceptance (`catalog-dump`, live render, offline replay) DID run and go green
at 19:04 — 3849 defs, two live renders byte-identical, offline replay
matching — with one disclosed gap: the fixture had no registered landmarks,
so `--landmarks` rendered identically with and without it and that code path
is unexercised.

**C (`2f2796e`) — done as a task, but the issue itself was not closed.** 17
open issues verified, **11 corrected**. The standouts, each independently
confirmed against decompiled source: `acee526` (1.9) — the proposed fix route
is wrong, `mode:"direct"` currently *deletes* buildings in the target cell;
`fc287ba` (1.6) — the flagship acceptance criterion fails on a *correct*
implementation; `20e5cda` (3.5) — acceptance is impossible as written, an NRE
on the default path; `96d9315` (4.1) — "an unset growing zone grows nothing"
is false, it grows potatoes.

**A workspace-CLAUDE.md correction, verified independently and reported
rather than silently fixed:** `AutoRimmer.dll` *does* embed the commit sha —
the documented `strings | grep` idiom just never presents it, because
`InformationalVersion` isn't on its own line. `grep -aoE
'[0-9]+\.[0-9]+\.[0-9]+\+[0-9a-f]{40}'` over the raw bytes finds it. This
nearly shipped wrong: a worker rewrote its branch history after a build, and
the committed artifact named a commit the rewrite had deleted — caught only
because a same-HEAD rebuild moved 148 bytes, which the workspace rule already
treats as the tell.

### Thread 2 — BORGES (Fable co-authored commits, `corey@getbi.net`)

Not described in the dorian's-box ledger at all; reconstructed from commit
messages and `HANDOFF.md`, since no muster handover comment for session 9
exists — a real silence in the record, noted rather than filled in.

**4.1 playbook landed** (`b2a6010`, `b3d79a0`, `c73e58e` — nine seed lessons,
checklists reshaped around the loop's own moments, templates, post-mortem
procedure) but `96d9315` stays `state:backlog`: it is written, **not
audited**, per Evan's standing instruction that a lot was authored very fast
and the project was "bitten twice in one day by claims that were plausible,
well-cited and wrong."

**Two more defects found and fixed on main directly:** `70ac258` — `things`
(detail rows) and `fires` have the same live-score-ordering bug `1eb2262`
fixed in `pawns` (`5178458`, merged `b1f6ea7`); and a newly-discovered
`bc2250b` — `move-to` never read `queue`, silently replacing a running job
instead of queueing behind it, and `attack` had no way to refuse `queue:true`
cleanly. Fixed (`734211f`, `9109eaf`) and folded together with 4087644's
remaining refusal-journaling gaps into one merge (`0fa98ec`, Build
`93721ac`). **`bc2250b` does not exist as a `git-bug` issue in this repo** —
every citing commit names it, but `git-bug bug show bc2250b` and a
`refs/bugs/*` search both come up empty. Most likely filed on BORGES and never
pushed; flagged here rather than guessed at.

**A new PawnSafe hazard class, Class I:** a vanilla helper that does the
whole job, returns void, and takes its most important input from *ambient*
state the bench cannot supply — `FloatMenuOptionProvider_DraftedMove
.PawnGotoAction` reads queueing from live keyboard state
(`KeyBindingDefOf.QueueOrder.IsDownEvent`) inside a helper with no queueing
parameter, so wrapping it was silently unable to ever queue. Documented in
`PawnSafe.cs` (`b59b830`).

**DESIGN.md corrected in five places** (`d180f57`) on the strength of this
session's own verifications: baseviz is ASCII-only (the PNG encoder is 2.5's
own, not reused); BaseVizCatalogDumper is no longer a separate mod; the
"food_days < 3" leading-indicator example was backwards (`fc287ba`'s
verification: `Alert_LowFood` trips at nutrition/colonist < 4); `arrest` and
`equip` are exposed and the modal-hazard entry wrongly claimed otherwise.
Plus a stale `WorldSafe` hazard note corrected once Spatial.cs had already
fixed what it warned about (`ebf41bd`, `05dd70e`).

**Wrote `spec/3.5-dialog-verbs`, `spec/3.6-bills-storage`, `spec/4.2-play-loop`
— none merged, none audited, none run.** Per `HANDOFF.md`: 3.5's verbs are
complete but its acceptance is "partial and never run"; 3.6's bills+storage
work is complete with **no acceptance script at all**, and it modifies 3.4's
already-shipped `MedicalBillVerbs.cs`; 4.2's playbook and Python auditor are
written but the auditor has never executed — no Python on BORGES. All three
branches are pushed and sit untouched, deliberately, pending the audit
Evan's instruction requires before any of them merges.

**Filed:** `8b0b88f` (p2, designate-already-designated), `e6faa51` (p3, PNG/
ASCII alphabet identity), `05dd70e` (p3, stale hazard note, closed same
session), `ac407f1` (p1, half of 4087644's journal rule unimplemented).
**Resolved on the muster:** the session-8 label conflict (4.1/4.3 stay
`agent:fable`; the "never dispatch Fable on BORGES" rule is a machine rule,
not a spec-label override — dispatch opus there and say so in the RUNLOG).

### The two threads never shared a tree until the very end

`0c151c1` ("BORGES's session-9 work joins this box's render and sweep") is a
real three-way merge of two mains that had diverged all session: dorian's box
touched only `MapDumpVerbs.cs` plus `baseviz/`, `rwa/`, `accept/`; BORGES
touched `PawnOrderVerbs.cs`, `PawnEmergencyVerbs.cs`, `PawnActs.cs`,
`ThingVerbs.cs`, `PawnSafe.cs`, `WorldSafe.cs` — source was disjoint, verified
rather than assumed. Two conflicts, both hand-resolved and recorded in the
merge commit: `accept/4087644-order-honesty.md`'s stale check-count line, and
`Assemblies/AutoRimmer.dll` itself, where **neither side was correct** —
dorian's box's build predated BORGES's verb work, BORGES's predated the
map-dump structure fields. Rebuilt at `0dd1029`; 0 errors, 0 warnings,
confirming for the first time that BORGES's verb work and this box's map-dump
fields compile together at all.

### What this session did NOT prove, stated plainly

- **4087644**: merged, 48/51 live on the merged tree, 3 real gaps open
  (`ac407f1`), issue stays open.
- **`bc2250b`/70ac258 remainder** (refusal journaling, move-to queue honesty,
  things/fires ordering): built and merged (`0fa98ec`, `93721ac`) but **never
  run on a bench** — `HANDOFF.md` lists phases 5–6 of 4087644 and the whole of
  70ac258's acceptance as outstanding acceptance debt for the next session.
- **2.5**: mechanical acceptance is green; legibility acceptance — the spec's
  actual acceptance bar — has never been demonstrated on a fixture that could
  answer its own questions.
- **3.5, 3.6, 4.2**: written, not compiled by an orchestrator, not audited,
  not merged, not run.
- **4.1**: written, not audited.
- No numbered spec closed this session. **21 of 25** stands, unchanged since
  session 8.

### Where sources were silent or disagreed

- The muster (`git-bug 01f0b85`) has **no session-9 handover comment** —
  comment #25 is the label-conflict resolution and predates most of this
  session's work; the run ledger and `HANDOFF.md` are session 9's only
  first-party record.
- The ledger `fbb2c59`'s own plan describes only dorian's-box work (A/B/C);
  it says nothing about 4.1, `bc2250b`, or 70ac258, which happened on BORGES
  and are known only from `HANDOFF.md` and `git log`.
- `bc2250b` is cited by six commits as a `git-bug` issue but does not exist in
  this repo's `refs/bugs/*` — likely filed and never pushed from BORGES.
  Treated here as real (the code and acceptance both reference it concretely)
  but its issue text could not be checked.
- The session ends without a clean "state at session end" summary from either
  orchestrator; this section's bench-state and next-picks below are assembled
  from `HANDOFF.md` (BORGES's side) since dorian's box wrote none of its own
  before the session-10 ledger (`ce15092`) opened.

### Bench state (as left by BORGES; dorian's-box bench state not recorded)

`autostart.rws` — BORGES: 3-colonist quicktest map at tick 607, no violence-
capable colonist. No `_RimWorld-Agent` state recorded for dorian's box this
session. Two machine facts worth carrying: BORGES has no Python (accept
scripts there are hand-driven PowerShell/raw-protocol); dorian's box has no
`pwsh`. `cmp -l` across machines is confirmed a trap independent of same-box
determinism — a same-commit Linux build differs from the shipped BORGES blob
by 185,249 bytes at identical size, purely from the pdb path length shifting
the tail.

### Next, per `HANDOFF.md`

1. Run `accept/4087644-order-honesty.md` phases 5–6, then
   `accept/70ac258-things-stable-order.md` in full; close `4087644`,
   `bc2250b`, `70ac258` only if green.
2. Audit `spec/3.5-dialog-verbs` and `spec/3.6-bills-storage` (code, one
   agent each) and `96d9315`/`d2e1229` (prose) before merging any of them.
3. `DESIGN.md` decisions-log entry for PawnSafe Class I (text drafted in
   `PawnSafe.cs`'s Class I block).
4. Factor the four private `Act` emitters into one helper — five sessions
   old now.
5. Prune stale worktrees under `.claude/worktrees/`.

Superseded by session 10's ledger (`git-bug ce15092`), which keeps `fbb2c59`
open only until its two acceptance debts (4087644, f7b6207) discharge.

## Session 10 — 2026-08-31 (dorian's Linux box). Audit and merge; ledger `ce15092`

Fable orchestrator, opus agents. The round's rule, Evan's: **one agent per
branch, and the audits are REPORT-ONLY.** Auditors do not fix — they report, the
orchestrator decides what gets fixed and by whom. That is a change from session
8, where auditors committed their own fixes. Fourteen agents ran; nine merges
landed; **no acceptance ran at all**, which is the whole of what is outstanding.

### Step 0 — the two mains were one tree again before anything was dispatched

Local `main` (`aac6209`) and `origin/main` (`d153521`) had diverged: two machines
did session-9 work that never shared a tree. Merged at `0c151c1`, rebuilt at
`0dd1029`. Source was disjoint — this box had touched only `MapDumpVerbs.cs`
plus `baseviz/`, `rwa/`, `accept/`; BORGES had touched the pawn, thing and safe
files. Two conflicts, both hand-resolved: the 4087644 acceptance `.md` (whose
"54 checks" header was ALREADY STALE at 51 — which is exactly why the BORGES side
had refused to record a count, so that side won), and `Assemblies/AutoRimmer.dll`,
where NEITHER side was correct. First demonstration that both machines' session-9
work compiles together.

### The audits all passed, and the code was the strong part

All four returned MERGEABLE WITH NAMED FIXES; nothing came back unmergeable.
**Both branches that had never been compiled by anyone compiled 0 errors, 0
warnings** — 3.5 with all 18 new op strings in the assembly, 3.6 with all nine.
That was the round's largest unknown and it is discharged.

**3.6's most alarming property was its safest.** It edits `MedicalBillVerbs.cs`,
3.4's shipped file, and the change is five comment fixes plus a TRUE MOVE of
`RecipeAvailableNow` into `WorldSafe` — character-identical body, one added null
guard that changes nothing, one caller retargeted, exactly one definition
repo-wide. `accept/3.4-pawn-orders.py` holds unchanged; no assertion flips.

### Three of the round's own premises were wrong

Recorded because the pattern matters more than the items: the brief was written
against a tree that had already moved.

- **The DESIGN.md conflict the round was briefed on does not exist.** `d180f57`
  is already an ancestor of `main`; 3.5 appends at the log's tail and git
  3-way-merges it. (It did conflict on merge here — but only because THIS session
  had appended entries of its own in the meantime. Resolved by concatenation,
  twice, with no duplicates either time.)
- **Five of the six owed DESIGN.md corrections were already paid**, also by
  `d180f57`. All five were re-verified at source anyway and all five were
  accurate. Only the PawnSafe Class I entry was genuinely outstanding.
- **RUNLOG sessions 7 and 8 were not unwritten** — only session 9 was. The brief
  generalised `HANDOFF.md`'s accurate singular into a wrong plural.

### `bc2250b` does not exist

Six merged commits cite it as a git-bug id — `734211f`, `b59b830`, `a91e624`,
`d02dcad`, `0fa98ec`, `93721ac`. It is in neither bug store (48 refs local, 42 on
origin, absent from both), so it was presumably filed on BORGES and never pushed.
Its work IS on `main` regardless: the refusals journal, `move-to` honouring
`queue:true`, and PawnSafe Class I. The round's instruction to "close `bc2250b`
only if green" therefore has no referent.

### What the audits actually found — all of it in the suites and the prose

This is the round's lesson landing exactly where session 9 predicted it would.

- **3.5's suite had NINE structurally broken checks.** Eight passed a computed
  value where the `$Env` parameter belongs, shifting every later argument — four
  of those asserted literally nothing while reporting PASS, four were spurious
  red. The ninth, `4.8g`, read `data.ticks`, a key `TimeDriver` never emits: the
  single most load-bearing check in the file, and it could only ever fail.
- **4.2's auditor was run here for the first time** (BORGES has no python) and
  passes its own selftest — but its `advance-invariants` check was PARTIALLY
  VACUOUS, and that was DEMONSTRATED rather than argued. It read only the
  declared `cmd.json` args, never the result's `ticks_elapsed`, while
  `TimeDriver.Start` defaults `timeout_ticks` to 600000 when `until` is set and
  the arg is omitted — 10x the stated policy cap. A synthetic 5x violation
  reported PASS.
- **A third verb trap, the same shape as the two already known.** PLAY-LOOP.md's
  act list read as though `orders` were an umbrella taking `move-to`/`attack`/
  `rescue`/`capture`/`tend` as subcommands. Each of those is its own verb, and
  `orders` is a sixth, unrelated one that is NOT read-only: it can delete an
  incompletable bill, burns a scribed `nextJobID` per candidate, and can rewrite
  the scribed `paused` flag on every bill on the map.
- **3.6 shipped a real bug: a PARTIAL MUTATION REPORTED AS `bad-args`.**
  `bill-add` ran `AddBill` and THEN `Configure`, so a bad config argument left the
  bill in the stack and a retrying agent ended up with two. `storage-set` threw
  inside its per-target loop after writing, so `Act()` never ran and the state
  change carried NO JOURNAL ROW AT ALL. Both now validate before the first write.
- **The 4.1 playbook came back NOT SAFE TO TEACH FROM.** Its worst item was
  structural, not textual — see below.

### The power-room template's batteries were on a dead net

`templates/power-room.ir.json` placed two `Battery`s two cells clear of the
generator. Batteries carry `transmitsPower true`, so they are TRANSMITTERS, and
`PowerNetMaker.ContiguousPowerBuildings` grows a net by walking
`GenAdj.CellsAdjacentCardinal` — **transmitters must physically TOUCH.** The bank
therefore formed its own net and charged from nothing, and the template's own
check could not detect it: `nets_with_generator >= 1` and `batteries: 2` both
pass on a split net. The safety story inverted too, since with no batteries on
the conduit's net `DoShortCircuit` takes the start-a-fire branch rather than the
explosion branch.

**Evan supplied the better fix and it is now the template's.** The audit proposed
moving the batteries; the bridge used instead is `HiddenConduit`, a SEPARATE
ThingDef whose parent is `PowerConduit` — so it still transmits, but
`ShortCircuitUtility.GetShortCircuitablePowerConduits` lists
`ThingDefOf.PowerConduit` BY DEF, so a hidden conduit is never a Zzzt candidate.
That fixes the net AND removes the spark point, which is what a Zzzt-safety
template should have done from the start.

The distinction that made the dead net invisible is now written down, because it
is reusable: **transmitters must TOUCH; only CONNECTORS get the six cells** of
`PowerConnectionMaker.BestTransmitterForConnector`'s `ExpandedBy(6)`. "Within 6
cells" is true for a stove and false for a battery.

### All three order-honesty defects had wrong premises

`ac407f1` was filed off three failures measured at 48/51 on this bench. Every one
of the three diagnoses was overturned at source before any code was written.

- **(a) was already fixed.** `9109eaf` and `734211f` had done it hours earlier and
  both are ancestors of `main`. **The 48/51 baseline was measured against a bench
  running an assembly built BEFORE `93721ac`** — RimWorld loads the DLL at
  startup, so a rebuild never reaches a running bench. The BASELINE was stale,
  not the code.
- **(b)'s reasoning was backwards.** `workGiverDef != null` is not what excludes
  `JobGiver_Work`'s autonomous `playerForced`: `GiverTryGiveJobPrioritized` sets
  `workGiverDef` BEFORE `TryIssueJobPackage` sets `playerForced`, so the
  autonomous job carries both. `jobGiver is ThinkNode_QueuedJob` is what rejects
  it. `ordered` keeps its meaning; `order_kind` (work|direct|null) is published
  alongside.
- **(c)'s framing was wrong.** `TryTakeOrderedJob` has THREE branches past the
  early-out, not one. `queue:true` on an IDLE pawn does not queue — it STARTS.
  Two of the three branches `ClearQueuedJobs`, destroying work already lined up,
  and the third queues an order the caller never asked to queue while the pawn
  carries on — and the verb still answered `accepted:1`. `order_effect`
  (started|queued|gone), `queue_depth` and `queue_dropped` now publish it.

The fixture lesson that came with it is the general one: the suite staged its
"running job" with `wear`, then advanced 2500 ticks — long enough to FINISH
wearing. Worn apparel is unspawned, so `ThingArg` refused it, the stage silently
did nothing, an idle pawn took branch 1, and the resulting queue reading of 0
looked exactly like a publication bug. **A fixture that fails silently is
indistinguishable from the defect it was built to detect.**

### `--landmarks` was never passing

Session 9 recorded that 2.5's renders with and without the flag produced an
identical hash. That was an EMPTY REGISTRY, not agreement — `landmark --list`
returned `{}`, so there was nothing to draw. The path is untested, not proven.
The plain `landmark {set:{name,at}}` verb is not dev-gated, so it CAN be
exercised, and the check is now inverted: two landmarks in the rect, and the
renders MUST differ.

### A dry-run must not say "passed"

Four of the five drivers in `accept/` ended a `--dry-run` with "RESULT: all N
checks passed" in green — for a run that SENDS NOTHING and evaluates nothing.
That is this round's own failure mode reproduced one level up, and it is the
first thing anyone sees when trying a suite out; two of those numbers were quoted
as evidence during this very session before it was noticed. All five now report
the count as expectations PRINTED, say plainly that nothing was sent and no dig
path was proved, and say to run it live — in yellow (`61e3fc1`).

### What landed

`0c151c1` converge · `0dd1029` build · `03398e4` RUNLOG 9 · `850c1eb` DESIGN
Class I · `25f8ec6` designate honesty + map-view alphabet · `8089ca7` 4.2 ·
`88dae16` order honesty · `10da528` DESIGN order_kind · `6775eb6` build ·
`4b60f3f` 2.5 fixture · `baaaea4` 3.5 · `51f39bc` 3.6 · `0dbea20` playbook ·
`8675cb2` 70ac258 driver · `61e3fc1` dry-run honesty · `172e168` build.

`172e168` is the **first assembly that has ever contained the dialog and bills
surfaces.** Verified rather than assumed: InformationalVersion names `61e3fc1`,
AssemblyConfiguration blob is Release, pdb path is this worktree, and the new op
strings are present.

Filed: `2a7c064` (p3) — `_label_pass` adds `size/2` to a position that is already
the centre, so an odd-width building's code draws one cell east of true centre.

### What this session did NOT prove, stated plainly

**NO ACCEPTANCE RAN. Not one suite, not one check, against no bench.** Five
drivers now exist and plan 615 expectations between them — 4087644 (96), 3.4
(137), 3.5 (171), 3.6 (119), 70ac258 (92) — and 2.5's legibility fixture is
staged on paper and unexecuted. Per the standing rule, treat every one of those
numbers as ZERO evidence. Nothing was closed, correctly: `4087644`, `70ac258`,
`f7b6207`, `20e5cda`, `48f666c`, `d2e1229`, `96d9315`, `ac407f1`, `8b0b88f`,
`e6faa51` and `05dd70e` all remain open pending that run.

`20e5cda`'s Acceptance section was AMENDED IN PLACE first, because its second
criterion could not be met by any implementation — `Verse/NewQuestLetter` has no
accept option, so no letter option exists to press.

### Process note, recorded against this orchestrator

Eight agents were dispatched against the round's defect list without first
spending five minutes checking `git log` for what had already been fixed. That
one cheap pass would have cancelled or shrunk a meaningful fraction of the work —
(a) was already done, the stale WorldSafe note was already done, five of six
DESIGN corrections were already done. **Taking a handover brief at face value is
the exact failure this round existed to catch**, and the orchestrator committed
it. A staleness sweep belongs before dispatch, not inside each agent.

### Bench state

CLOSED — never launched this session. `autostart.rws` untouched.

## Session 11 — 2026-08-31 (dorian's Linux box). PROVE IT; ledger `65b03c2`

Fable orchestrator, opus agents. The round the acceptance suites finally RAN.
Seven agents; seven merges; **502 checks on the first pass and 884 across the
session, against a live bench, where the project's lifetime total had been zero.**
**FIVE SUITES FINISHED GREEN** — 3.4 (159/159), 4087644 (100/100), 8b0b88f
(123/123), 70ac258 (99/99), 3.6 (116/116). Only 3.5's trade phase is outstanding.

**Evan locked the play run mid-session** — "the actually play part needs to be
locked, do everything but step 3, you can run the game to test or whatever, just
no step 3." So 4.3 / M1 (`664e9b9`) did not run and stays open. Everything below
is Wave 1 and Wave 2.

### Step 0 — the staleness sweep paid for itself in four minutes

`origin/main` was 50 commits behind. Pushed `d153521..3e831ee` (fast-forward)
before anything else, so BORGES was not stranded the way session 10's first hour
was spent un-stranding it.

Then the finding that shaped the night. **A bench was already running, and it was
stale.** Session `20260831T232158`, launched **19:21:58**. The assembly carrying
3.5's dialog verbs and 3.6's bills verbs — `172e168` — was committed at
**20:19:20**, fifty-seven minutes later. RimWorld loads the assembly at STARTUP.
That bench was running IL that predates both surfaces, and any number measured
against it would have been a phantom in exactly the way session 10's `4087644`
"48/51, three real failures" were: all three of those were measured against a DLL
built before the fix.

Killed and relaunched. Freshness then PROVEN rather than assumed, by reading
**118 verbs** back off a live `status` call — including all five `dialog-*` /
`letter-*` verbs and all ten `bill-*` / `storage-*` verbs, none of which exist in
the older assembly.

### The first live acceptance run in this project's history

Logs committed at `accept/runs/s11-20260831/` (`8e6cedb`).

| suite | first run | after repair |
|---|---|---|
| `70ac258-things-stable-order` | **99/99**, exit 0 | — |
| `3.6-bills-storage` | **116/116**, exit 0 | — |
| `4087644-order-honesty` | 92/97 | **100/100**, exit 0 |
| `8b0b88f-already-designated` (new) | 121/123 | **123/123**, exit 0 |
| `3.4-pawn-orders` | 147/150 | **159/159**, exit 0 |
| `3.5-dialog-verbs` | 48 pass, 0 fail, exit 2 | **89/0** through phase 2; **101/1** in trade |

**Zero red errors in every suite that asserted on them**, across every run.

**The phase-0 guard is no longer unproven.** It was session 10's answer to the
`eq(..., None)`-passes-on-an-absent-key hazard and had never executed. It ran and
passed in all five drivers — 48 shape checks in `70ac258`, 58 in `3.6`.

### THE HEADLINE: every failure so far has been the DRIVER's, not the mod's

Eight failures across three suites on the first pass, plus two in the new suite.
**Every one classified so far is a driver defect. No mod change was owed by any
of them.** That is worth stating plainly because the round was budgeted for the
opposite.

- **`4087644` 6.5** read `data.rows` / `data.entries`; `JournalVerbs.Read`
  publishes `data.events` — a key the same driver's own `0.2b` asserts. It then
  read `verb` flat where `PawnActs.Act` nests it at `payload.verb`. The rows were
  being written all along: `6.1b`–`6.4b` each returned `journal_seq >= 1`. **The
  journal rule was implemented and the instrument was pointed at the wrong key.**
  This is what had made `ac407f1` look unimplemented for two sessions.
- **`4087644` 6.9a/b** sent `work:"Warden_DeliverFood"` — the giverClass, not the
  defName `DeliverFoodToPrisoner`. `Dev.Named` throws before `Prioritize` builds
  its `Outcome`, so the reply had **no `data` block at all**. The null was an
  ABSENT key, indistinguishable to `dig` from a gate-less rejection.
- **`4087644` 6.15a/b** targeted a colonist, so `Attack`'s `CanDraftAttack` →
  `cannot-target` exit returned before the `queue-unsupported` branch was
  reachable. **That check was unpassable on ANY save** — not a fixture gap, a
  wrong target choice. My own first read called it a fixture problem and was
  wrong; the diagnosis pass refuted it with the control-flow order.
- **`8b0b88f` 1.2l/m** dug `data.action.rejected_by_reason`. The tally has two
  spellings in two PLACES: `rejects_by_reason` on the response data block
  (`DesignateEngine.PublishRejects`), `rejected_by_reason` on the JOURNAL ROW
  (`DesignationVerbs.Designate`). The response's `action` block is `{journal_seq}`
  and nothing else. **The suite written to close the absent-key trap contained
  it.** Now reads the journal at the seq the action block names, which is the
  stronger assertion anyway.

### The one that matters most: a green that could not go red

`3.4` failed `3.2`, `3.6b` and `3.6c` — one cause. The bench colonist already
wore exactly `Apparel_Parka` + `Apparel_Tuque`, which is exactly what phase 3's
`cold` policy allows, so the policy asked for a wardrobe the pawn was already in.

**The three reds were not the important part.** Check `3.6a` — "the pawn is
WEARING the parka" — **PASSED while asserting nothing**, because it was already
true before the policy was ever assigned. A check that cannot go red is this
project's standing failure mode, and here it was hiding behind three
honest-looking reds. Repairing the reds without closing the hollow green would
have been the wrong fix and would have left the suite reporting 150/150.

Phase 3 now stages its own start state and gates on a `3.0j` precondition that
the pawn wears neither garment; only `JobGiver_OptimizeApparel` can dress it from
there, because phase 3 issues no `wear` order. `3.6c` also loses its "or it
started naked" escape, which let a naked pawn pass a check about taking clothes
off.

### `dev:incident` could not fire a world-targeted incident

3.5 exited 2 on `IncidentDef 'GiveQuest_Random' does not allow a Map target
(targetTags: World)` — the verb always passed a Map. That blocked ~123 of 171
checks, the largest block of unproven acceptance in the project. The target is
now resolved from the def by the game's own chooser, the `GetDefaultTarget` local
function inside `StorytellerComp.DebugTablesIncidentChances`.
`DebugActionsIncidents.GetTarget` was deliberately NOT copied: it picks by camera
state, which is meaningless headless. 3.5 went 48 → 104 passing, still zero
failures, and then reached its trade phase.

### What did NOT come out green, stated plainly

- **3.5's trade phase went 79/86 to 101/102, and the survivor is the night's
  only unresolved MOD defect.** All seven original failures were the driver's and
  are fixed (`8904138`): it bought and sold the SAME def, which one signed
  `Tradeable.CountToTransfer` cannot represent; its silver expectation was
  `sell_value − buy_value`, structurally zero because `AddDealTotals` includes the
  currency row where `TradeDeal.UpdateCurrencyCount` skips it; the out-of-range
  gate is unreachable from a zero row; and `3.2d` asserted nothing because
  `trade-start` never publishes `force_pause`. The remaining failure: `data.after`
  reported 50 `ComponentIndustrial` after a purchase that an independent `things`
  read shows brought the colony to 60. **The trade itself was correct** — silver
  moved by exactly the promised −360, zero red errors — but the verb's own
  post-trade echo does not see the goods it just bought. Filed on `7e8c969`;
  `20e5cda` stays open on it.
- **`091e3f0` is merged but NOT closed.** The forbidding is proven live from two
  directions — the kit reports 10 forbidden stacks, and `unforbid` independently
  accepts exactly 10, then 0 on a second pass. Left forbidden and advanced 2500
  ticks, all 10 were still there. Journaled as `type: dev` seq 133 with
  `forbid: true`. But acceptance bullet 2 says "**and a pawn then picks the gear
  up** — proving the obstacle was real and the remedy works, not just that a flag
  flipped", and after 4000 further ticks **no haul job ran**. That bullet exists
  precisely to stop a flipped flag from counting as done, so it would be wrong to
  close on the flag.
- **Deferred, and said rather than skipped:** `f7b6207` (2.5's legibility read —
  it needs a fresh reader per iteration and I am disqualified the moment I have
  seen the answer key), `e6faa51` (two-channel compare), `2a7c064` (p3).

### The bug store broke, and it was not our code

Pushing the bug refs was rejected; `git-bug pull` merged the remote's work and
**every git-bug command then panicked with `DFS failed`.** Bisected from an empty
ref set: five bugs unreadable (`20e5cda`, `70ac258`, `96d9315`, `d2e1229`,
`fbb2c59`), 49 fine. The decisive measurement — **all five have two-parent merge
heads and BOTH PARENTS OF ALL FIVE ARE INDIVIDUALLY READABLE.** Only the merges
git-bug itself wrote are broken, and they are exactly the bugs where both sides
had diverged. `20e5cda`'s bad merge came from the REMOTE, so BORGES's git-bug
does it too. No data lost; each was reset to its fuller parent. Filed as
`16b959a` with the dropped parent shas.

One methodological note worth keeping: the first bisect blamed `20e5cda` alone
and then "proved" all 17 of its commits unreadable. **A confound — one bad ref in
the set poisons every later test.** Nothing can be bisected until the set is
otherwise clean.

### What landed

`9219a6d` 091e3f0 forbidden kit · `8e6cedb` first-run evidence · `cd9f390`
dev:incident world target · `2a31cb9` build · `3b3f905` 8b0b88f suite · `1fc5e5b`
3.4 staging · plus the 4087644 driver merge and the 8b0b88f 1.2l/m repair.

`2a31cb9` verified: InformationalVersion names `cd9f390`, AssemblyConfiguration
blob Release, pdb path this worktree's `obj/Release`.

**A verification trap worth recording.** The new op strings are NOT findable with
an ASCII grep — .NET puts string literals in the UTF-16 `#US` heap, so
`grep -a 'target_note'` returns 0 on an assembly that contains it. Counted as
UTF-16LE they are all there. The build rules already record this for
`InformationalVersion`; it applies to every literal.

### Bench state

RUNNING at session close — `20260901T005842`, autostart.rws, paused. It has been
mutated by the acceptance runs (policies created, kits spawned, ~12k ticks
advanced, a quest and a trade caravan staged). **`autostart.rws` on disk is
untouched**; the next session should relaunch rather than inherit it.

## Session 12 — 2026-08-31 → 09-01 (dorian's Linux box). M1: the ten-day run that stopped on day 6

Written retrospectively in session 13, from `RUNS/m1-20260831/`, the transcript
(195 ops), the journal (`20260901T022324.ndjson`, 175 events) and the ledger
(`checklist.ndjson`, 53 lines). Session 12 wrote no RUNLOG section of its own.

**Verdict: the platform passed, the colony failed.** Stopped at Evan's call on
day 6 of a planned ten, with the hard acceptance already decided against on
day 4. Crashlanded, faction *New Arrivals*, temperate forest, 250x250, Cassandra
Rough. Tick span 723 (staged) → 322,314; Spring 1 → Spring 6, year 5500.

### The acceptance, as measured

| criterion | result |
|---|---|
| ≥2 of 3 alive at day 10 | **FAILED** — 2 dead on day 4, 1 alive at stop |
| no dev verbs after staging | **PASSED** — last `dev` row tick **723**; all 50 dev rows at 723 |
| draft → fight → undraft, final digest 0 drafted | **FAILED** — the one draft ended in an involuntary undraft when Captain was downed. Final digest does read 0 drafted |
| zero unexplained red errors | **PASSED** — **0 red errors**, whole run |
| clothes check fires at least once | **PASSED** — `Alert_NeedWarmClothes` on 655, off 1,570, journalled |

Food was never under pressure (60.3 food-days at stop). The "one raid" was a
`ThreatSmall` manhunter crow; no faction raid arrived in five days.

### What killed the colony — a single crow, and four decisions

Neither colonist died of the crow. Both died of **blood loss, untended**, hours
later. The chain, in the order it had to break:

1. **Day 1, seek-at-will ON as a standing posture marched all three unarmed
   colonists 60+ cells at a fogged insect hive.** It was turned OFF — which put
   them back on the vanilla flee branch, and that is the branch they took when
   the crow arrived. → `[[seek-off-is-a-decision-to-flee]]`
2. **The only pawn with Doctor enabled was Table, and Table was the first
   casualty.** `Alert_NeedDoctor` fired *after* he was down.
   → `[[one-doctor-is-zero-doctors]]`
3. **Table went down at 214,599 inside six back-to-back 2,500-tick advances
   during which only `pawns {filter:"hostile"}` was read** — never the journal,
   never the digest. He bled for 11,335 ticks.
   → `[[read-every-return-or-lose-a-colonist]]`
4. **Captain fled 150 cells into unexplored ground**, went down there, and died
   before the last colonist could cross the distance.

The four fixes the run produced, for any re-run: an `Area_Allowed` bounding
where colonists work, `seek-at-will` ON as a standing posture,
`assign {hostility:"Attack"}`, and **two** doctors.

### Three launches to get one map

`--quicktest` and `autostart.rws` are a **deterministic** map-gen failure, root-
caused to `Root_Entry`/`Root_Play` racing on `Root.checkedAutostartSaveFile` with
a scene-targeted long event. Two failures, then a clean launch after moving the
save to `Saves/pre-m1/`. Both Player.logs were captured *before* the relaunch
that would have overwritten them — `RUNS/m1-20260831/mapgen-failure-{1,2}.Player.log`.
→ `[[quicktest-and-autostart-collide]]`, now filed as `c8c0199`.

### The finding that cost the most, and hid the longest

**`dev:spawn-thing` returned `ok:true` with `placed:0`** for the research bench
(journal seq 66). The bench never existed, `Alert_NeedResearchBench` never
cleared, and **research could not progress for the entire run**. It went unseen
for five in-game days because `research-set` kept answering `bench_ok: true` —
`ResearchVerbs.cs:151` short-circuits when `requiredResearchBuilding == null`,
which is faithful to vanilla's gate but names the field as though a bench exists.

And once staging ended, the no-dev-verbs invariant made it permanent: with no
build verb (3.3) there was no way to replace it. `Alert_ColonistsIdle` was up
for most of the run; four dev-staged buildings were the entire colony for six
days. Filed on `1adc737`.

### Time was lost, twice, and the record could not say to what

An `rwa advance` whose CLIENT dies leaves the game **running**. `pause` reported
`was_advancing: true, speed_before: Ultrafast` after a lost tool result.
`136-advance` and `187-advance` are empty transcript directories — `rwa` mkdirs
the step before sending and writes both files only after the result returns, so
an empty directory is the entire fingerprint. **~60,000 ticks — a full in-game
day — elapsed unobserved, more than once.** Session 12 first blamed a stray
keypress on the watched window and **corrected that in the ledger**; the
corrected cause is client death. Mitigation adopted mid-run: read
`status.paused` before every advance. Filed as `65e7cf9`.

### Session 12's own compliance failure, stated rather than skipped

**Day 1 missed all four daily items** — no sweep ran at all, colony-start ran
instead — and **day 4 missed three** while the two deaths were being handled.
`execution-slip` in `postmortem.md`'s taxonomy: a 4.2 compliance finding, not a
new artifact, and deliberately not papered over with an invented checklist item.

### What session 12 left owed

No RUNLOG section, no post-mortem (mandatory — two deaths), no escalation, and
**git-bug untouched**: `664e9b9` still carried the same six comments it had
before the run. Every one of those is discharged in session 13.

## Session 13 — 2026-09-01 (dorian's Linux box). Discharge M1's loose ends

Opus orchestrator, five opus workers, each in its own git worktree. The session
session 12's brief asked for: verify every claim, then do the work. Fifteen
findings verified before any of them were acted on, and **four of the brief's
own claims did not survive that pass** — which is the argument for doing it.

### What the verification changed

- **B is REFUTED, and the evidence for it never existed.** The brief said the
  envelope publishes `overshoot: 1`, so "the driver appears to know it
  overshoots". **There is no `overshoot` key on that envelope.** `TimeDriver`
  emits `overshoot` only in `ticks` mode (`if (Target >= 0)`), so an
  `until`+timeout advance never carries one. `advance` does not overshoot its
  cap: 20/20 advances came in within `overshoot_bound`, max 21 against 30. The
  FAIL was the auditor comparing `ticks_elapsed` against `timeout_ticks` alone.
- **A's `ok:true` is CORRECT**, and reframing it was the point. A refusal is
  information, not breakage (`PawnActs.cs:288`, PLAY-LOOP §act). The real defect
  was underneath: the response carries a `failed` list and **the journal row
  dropped it**, so the only surviving record of the refusal had no cause in it.
- **D's premise is refuted and the FAIL still stands.** The six insects were not
  a fog problem — seek marched all three colonists 60 cells to that hive in one
  advance, so they were reachable. Killing them with three unarmed pawns was
  simply not survivable.
- **I4 is not a defect.** Vanilla `WoodLog` ships `<tools>` and
  `<weaponClasses>Melee</weaponClasses>`; the weapons rollup was right.
- Also: "3 PASS" was 4, and "1 commit unpushed" was 2.

### Evan's ruling on D, and the verb it produced

> 0 drafted, 0 hostiles **that we haven't pardoned**

The insects "aren't hostile in the same way a normal hostile is, since they
won't attack at will", and the run **should have explicitly declared it was not
attacking them because it wasn't ready.** The decision must be a recorded ACT,
not a silent exemption in a counter — so `threat-pardon` takes a REQUIRED reason
and journals it, and `digest.threats` gained `hostiles_pardoned` /
`hostiles_unpardoned` beside an unchanged `hostiles`.

**The M1 run still fails that criterion, and that is the correct outcome.** The
auditor falls back to `threats.hostiles` when the new field is absent, precisely
so a pre-ruling run cannot be massaged green.

The lapse predicate is real rather than invented: `LordJob_StructureThreatCluster`'s
graph starts in `LordToil_Sleep` and every wake transition leaves it, so
`CurLordToil is LordToil_Sleep` is exactly "still dormant". Where no predicate
exists the pardon stays manual — no heuristic.

### The auditor: 4 PASS / 2 WARN / 5 FAIL → 8 PASS / 2 WARN / 2 FAIL

Three of the five failures were the auditor's own. **Both survivors are facts
about the run.** `daily-coverage` actually WIDENED — days 1, 2 and 4 now, not 1
and 4 — because `barracks-heat` was promoted into `daily.md` this session. A
closed run is a record; a count that moves with the item set is the diff
working. Banked at `RUNS/m1-20260831/audit-s13.txt`, because session 12 reported
`EXIT=0` for this command by piping it to `tail` and reading tail's status.

**F's day-1 half was not a slip.** The auditor keyed `daily-coverage` on the day
digest; PLAY-LOOP keyed the sweep on `day_of_season` *differing from the last
read*, which cannot fire on a session's first read. The session obeyed the
playbook and failed the audit. Resolved in the auditor's favour: a session's
first read is a day boundary. Day 4 remains a clean `execution-slip`.

### The post-mortem, run in full for the first time

Twelve root causes across six classes. Two were invisible before it ran:

- **`Alert_ColonistNeedsTend` does not merely scope down — it INVERTS.** Its
  getter excludes pawns needing rescue, so it goes OFF when the patient goes
  DOWN. Silence means tended OR collapsed. It was the only pre-casualty signal
  in the whole run, on at 205,979, and it self-silenced 8,620 ticks before the
  first death.
- **`BloodLoss` was CUT by the 20-row hediff cap in all four of Captain's health
  reads** (`hediffs_more` 7 / 16 / 19 / 19). The observation surface truncated
  away the thing that was killing him, four times, in favour of rows that did
  not matter.

The sharpest execution finding: **27 advances, ZERO `journal` calls, 10
digests.** At tick 231,968, with Captain bleeding 118 cells away, the fix
attempted was a work-priority flip — while **`rescue`, which is shipped, forces
the job and interrupts `LayDown`, was called 0 times in 195 ops.** The verb that
solves it existed and the run never reached for it. Bleed clock ~9,040 ticks
against a ~2,810-tick walk, and neither number was published.

The wealth check was done on real numbers rather than proxies —
`HistoryAutoRecorder` decoded straight out of `Autosave-5.rws`, 11 samples at a
30,000-tick cadence. Peak wealth 22,530.7 against `PointsPerWealthCurve`'s
14,000 free floor; marginal 0.00622 points per silver. **Every named prevention
costs zero wealth**, so no damping caveat applies, and the turret answer would
have cost ~1.5 points and saved neither colonist.

### The ruling that re-scoped the rest

**A deterministic finding goes in the MOD, not in a note.** `postmortem.md` step
5 said "lowest rung" and DESIGN said "deterministic goes in the mod"; Evan
resolved it for DESIGN. Notes get ignored — and the proof was already in the
repo: **all four M1 lessons were orphans**, cited by no checklist, while every
pre-M1 lesson was cited by one. Mod code has no attention budget. M1 day 4
missed three daily items while two colonists were bleeding out, which is exactly
when a checklist line is worth most.

So three of the post-mortem's outputs went to the mod rung, and 4.4 gained the
**promotion pass** it never had — it could only ever shrink the checklists.
Candidates are now COMPUTED off the ledger (ids with no `### <id>` behind them,
plus lessons no checklist cites), so finding L becomes a grep instead of a
sweep.

### What landed

`s13-rwa` `--id` reaches the verb and `cmd.json` is written before dispatch ·
`s13-auditor` C, B and D · `s13-checklists` M, L, N, E, F and the
`status.paused` invariant · `s13-mod` A, J, K, I1, D + one standalone `Build:` ·
`s13-postmortem` the procedure · plus the `--quicktest` refusal, DESIGN's two
new decisions, and this file's Session 12 section.

`11ef46c` verified: pdb path names THIS worktree — the branch's own `Build:`
named the agent's temporary worktree, which is why the rebuild happened on main.
AssemblyConfiguration Release, Debug blob absent, `InformationalVersion` naming
`ad29d6d`. IL unchanged from the branch artifact: 300 bytes across nine regions,
all metadata, the 203-byte one at 714325 being the pdb path this rebuild exists
to correct. New literals verified as UTF-16, never by ASCII grep.

### git-bug, discharged serially from one place

Escalation and the full spec-gap list on `664e9b9` · I5 on `1adc737` · N on
`d32eadd` · OUT-1 on `722c951`, OUT-3 on `40ed42f`, OUT-6 on `cc8988c`. New:
`65e7cf9` mod-side client liveness, `c8c0199` the quicktest collision
(**resolved and CLOSED** — autostart stays parked, both launchers now refuse
rather than warn), `40ed42f` doctor coverage, `722c951` advance refuses on an
unread journal, `b1b3060` the posture verb, `61794cd` the bleed-out clock and
the hediff cap that hid it. Serially, from one session, per `16b959a`.

### What is NOT done, said rather than skipped

- ~~**None of the new mod surface has been exercised on a bench.**~~
  **Superseded later the same session — the bench WAS launched and all five
  findings were proven live** (`_RimWorld-Agent`, session `20260901T121508`,
  assembly `1.0.0+ad29d6d`). Smoke pass at
  `accept/runs/s13-20260901/live-smoke.md`, then the suite at
  **169 PASS / 0 FAIL / 1 SKIP**. The sharpest single artifact is journal seq
  24 — `SimpleResearchBench x0 (WoodLog) @ 107,119 REFUSED`, carrying
  `failed[]` with the reason and a blocker naming granite and its removal.
  M1's seq 66, the row the fix exists for, had `placed:0`, `ids:[]` and nothing
  else. `bench_ok` was proven BOTH ways on one map: false with no bench, true
  after spawning one.
  Still genuinely unproven: the pardon of a **dormant** hostile, which is the
  case the verb exists for. A `--quicktest` map has no sleeping cluster and one
  could not be constructed on it.
- **Live testing produced findings no amount of source reading would have.**
  An unknown argument name is silently ignored and falls back to a default —
  passing `at:` instead of `pos:` put three spawns at the colony anchor while
  reporting success. `data.at` echoes the TARGET cell while `spawned[].at` holds
  where the thing landed, three cells away (`acee526`, now measured).
  `threat-pardon` reports `ok:true, refused_count:0` when every pardon lapsed on
  arrival. And `find-rect` approves a rect on its own cells while a workbench
  also needs its **interaction spot**, which lies outside the footprint — the
  suite's one SKIP, kept rather than papered over with a retry loop.
- **A correction worth keeping.** This section previously ended at the struck
  line above. It was true when written and false three hours later, which is
  exactly the failure mode the workspace build rules devote an essay to. Struck
  rather than deleted, because the point is that a status line goes stale on its
  own and nothing warns you.
- **Whether `664e9b9` closes is still Evan's call.** The run failed its hard
  criterion. Either it closes as evidence — the platform proved out, the colony
  did not — or M1 re-runs on a fresh seed with the four fixes the run produced.

## Session 14 — 2026-09-01 (dorian's Linux box). Pre-flight for the round that makes blueprints buildable

No worker was dispatched and no branch was cut. This session did the verify-first
pass the round's brief demanded, the pre-flight the brief did not know it needed,
and the git-bug record-keeping that came out of both. Everything below is on
`main`.

### Four of the brief's claims did not survive verification

Session 13's brief lost four claims to the same pass; this is not a new failure
mode, it is the pass working.

- **"Is there deconstruct / cancel / build-roof? I looked in `DesignateEngine.cs`
  and found storage filter categories, not designators."** The table is
  `DesignationVerbs.cs`; `DesignateEngine.cs` is the target resolver.
  `deconstruct`, `deconstruct-conduit`, `uninstall`, `cancel`, `claim`,
  `fill-in`, `strip`, `open` and the whole mine/plant/terrain set all ship, each
  citing the `Designator_*` class whose own `CanDesignate*` it runs, each gated
  additionally on `des.Visible`. `build-roof` / `no-roof` / `ignore-roof` ship in
  `AreaVerbs.cs` — correctly, because the game makes those `Designator_Area*`.
  **A build round's "needs cancel and deconstruct at minimum" was already met.**
  And `cancel` is `Designator_Cancel`, which destroys player blueprints and
  frames, so 3.3's `cancel-layout` is placement-id bookkeeping over a shipped
  verb rather than new game-facing work.
- **"`templates/power-room.ir.json` exists and the rest are .md only."** All
  three templates carry both halves — `bedroom` [5,7], `freezer-kitchen` [11,6],
  `power-room` [7,7] — in the identical nine-key dialect. The IR is a format
  with three conforming instances. What is actually open is `INDEX.md`'s own
  "Row 0 = north is PROPOSED, not established", which is a different question.
- **`e6faa51` (map-view alphabet identity) was already shipped**, in `28d52ae`,
  and merely never closed. `Spatial.cs` carries
  `AlphabetId = "map-view/ascii-1"` in a `channel` block mirroring `map-dump`'s,
  each naming the other in `distinct_from`. Closed on evidence.
- **`build` is in the brief's DONE MEANS and was assigned to no session.** A is
  the shared routine, B is "consumers", C touches no C#. The verb the round is
  named for had no owner.

### The pre-flight the brief did not anticipate

**The evidence three open issues rest on lived outside the repo, one bench launch
from being overwritten.** `8b4839f`, `c718e4a` and `3a5ff6c` cite response
envelopes by name (`results/accs13-026-devspawnthing.json` and four others) and
journal rows by seq. Those files existed only in `_RimWorld-Agent`'s protocol
root — the working directory of whatever bench runs next — and this round
launches benches repeatedly.

What the repo *did* hold was `transcripts/20260901T121508/`, which carries a
`cmd.json` per call and **no responses**. So the round's premise was banked on
the ask side and nowhere on the answer side. A transcript proves what was asked;
it does not prove what the game answered, and the answers are the findings.

Banked at `accept/runs/s13-20260901/`: 65 `accs13-*` envelopes, the 62-row
journal, and a README recording why. `accs13-026` verified against `8b4839f`'s
text before committing — `reason` names granite on the interaction cell,
`blocker` names a `WoodLog` on the target cell with `removal: "none"`, the
opposite of the truth. Standing lesson written down there: **an issue that cites
a path under the bench's protocol root cites a file with a lifetime shorter than
the issue.**

Also on `main`: session 13's two DESIGN decisions were still uncommitted, so
every worktree cut today would have started without them. Committed. Six stale
session-13 worktrees pruned (all merged, all clean; the one carrying untracked
files held a `fakebench` stub root for a different session and an older copy of
a suite `main` supersedes). Branches kept.

### Resolved by investigation, not queued

- **Roofing needs no spec.** Designation ships (`area`), automatic roofing covers
  any enclosed non-edge non-fogged player room of ≤26 regions and ≤320 cells —
  a 7x7 module is 49 — and observation ships three ways. What is owed is a test
  discipline: `TryGenerateAreaFor` only QUEUES, `TryGenerateAreaNow` runs next
  tick, so **an acceptance that reads the roof area in the same call as the
  placement sees nothing and reports a correct implementation as broken.**
- **"Does the work happen?" is not a work-priority gap.** `work-priorities`
  ships, fan-out included. The gap is observation and it is total:
  `grep Blueprint Source/AutoRimmer/` returns **zero**, and `digest` has no
  construction section. Filed as **`d7c8088`**, with the finding that makes it
  more than "list the blueprints": **completion is an absence.** A finished build
  leaves no blueprint and no frame, so `built` and `cancelled` are the same
  nothing unless the read is keyed on 3.3's placement id and the
  `Frame.CompleteConstruction` / `FailConstruction` transitions are journaled.
  Three verified hazards recorded on it, including that `CostListAdjusted`
  `Log.Error`s on null stuff for a `MadeFromStuff` def — so a naive observer
  **turns a clean run red by reading it**, and the suites count red errors.
- **`2a7c064` is half a defect as filed.** The x axis is wrong for odd widths, as
  reported. The z axis is wrong too, one cell in the opposite direction, for odd
  heights — invisible until now because the specimen is a 3x1 stove, height 1.
  A third defect in the same function: the renderer reproduces only the swap half
  of `GenAdj.AdjustForRotation` and drops the per-axis even-size centre shift, so
  an even-sized rotated building's derived rect is displaced. The exact formula
  for both axes and both parities is on the issue, with the even-width answer
  stated as the acceptance asked.
- **`7382bdd`'s whitelist-vs-narrow choice is pre-authorized** rather than left
  to a round trip on a one-DLL tree: attempt the whitelist, fall back to
  `pos_source` if a shipped suite breaks for anything but a genuine caller bug,
  record which suites forced it.
- **The round ships `build`; `place-layout` is next.** DONE MEANS names one verb
  and one read, and this project's rule is that the acceptance section is the
  contract. `place-layout --origin P` also cannot state its own convention until
  the north pin exists, and that pin is this round's output. `build` lands in
  session B beside `3a5ff6c`, because comment #7 on `1adc737` already establishes
  that instant mode IS `dev:spawn-thing {buildable:true}` — splitting them means
  two sessions writing the same `SiteGate` call serially against one DLL.
- **The north pin moves from 3.3 to `bac4eba`.** It is a decision about
  `templates/` and `baseviz/`, neither of which is C#; the mod has no opinion on
  IR orientation and would not until `place-layout` exists. `bac4eba`'s session
  runs in parallel; 3.3's is serialized behind the site routine.

### git-bug, discharged serially from one place

`e6faa51` **closed** on evidence (`state:done`) · `d7c8088` **new** (construction
observation, p1/spec/wave:3) · `1adc737` two comments (the three investigation
resolutions; the scope split) · `bac4eba` (templates correction, the north pin,
its ownership) · `2a7c064` (the z axis, the rotation shift, the formula) ·
`7382bdd` (the pre-authorization).

### Then the two findings were worked, rather than handed back

They had been written up as "worth your eye" — which was the wrong disposition,
as Dorian pointed out. Neither needed a worker, a branch or a bench.

**`2a7c064` — three defects, not one, and the two extra ones are the lesson.**
The filed defect was the x axis: `cx = x + (sw*scale)//2` adds half the width to
a position that is already central, so an odd-width building's code lands one
cell east. The z axis was wrong too — `- ((sh-1)*scale)//2` is a correction that
is not owed, putting an odd-HEIGHT label one cell NORTH — and it survived
because **the specimen the issue was filed against is a 3x1 stove, height 1**,
so the bad term was multiplied by zero. At most one of the two axes could ever
have been right. Third, the rect itself was wrong for even sizes under rotation:
the renderer reproduced the axis swap from `GenAdj.AdjustForRotation` and
dropped its per-axis even-size centre shift, and no label arithmetic corrects a
wrong rect. Fixed by porting `OccupiedRect` whole and centring in the rect it
returns, which needs no parity special-case; the even-width answer the
acceptance asked to have stated falls out of it rather than needing a rule.

**A fourth defect, found only because the third could not be fixed without it.**
`map-dump` published `labels[].rot` through `Rot4.ToStringHuman()` — which is
`"North".Translate()`, a LOCALIZED string — while `render.py` compares it
against the literals `"East"`/`"West"`. On any non-English install the swap
silently never happened. Now `ToStringWord()`. Same class as `00a1be7`; no
change in English, which is exactly why it could sit there.

`accept/2a7c064-label-centre.py`: 20 checks, 20 PASS, and **no bench, no game,
no protocol** — pure geometry, runnable anywhere python is. `baseviz/` had no
tests before it. Phase 1 pins the port against `c718e4a`'s own worked example
(one centre, three rects, for a 5x2 bench) so a drifting port fails loudly
instead of silently re-measuring; phase 3 asserts the PRE-FIX formula fails
every phase-2 case 5/5, because a check that cannot fail against its own bug
proves nothing — and this issue's specimen is the proof, having passed the z
axis by accident since it was filed.

**The north pin, and what pinning was actually worth.** `INDEX.md` had carried
"Row 0 = north is PROPOSED — 3.3 pins it" since session 10 and 3.3 never
reached it. Pinned, and moved off 3.3 on purpose: it is a `templates/` and
`baseviz/` decision, no C#, while 3.3's session is serialized behind the site
routine. North-up was already the convention in `render.py`, `CropRenderer` and
`map-dump`'s `north_up: true`; only `ir.py` was silent.

The orientation was the cheap half. **The corpus was then checked AGAINST the
pin instead of assumed to agree with it, and one template did not.**
`freezer-kitchen` carried `FueledStove_South`; `FueledStove` is
`interactionCellOffset (0,0,-1)`, so `Rot4.South` rotates the cook's cell 180°
to the NORTH of the stove — row 0, a wall. The stove was unusable as drawn, and
the answer is the same whether the IR token is read as a corner or a centre, so
it did not wait on pin 1. Now `FueledStove_North`.

The cause was the DIALECT, not the template. The suffix was documented as "the
direction the def FACES", which means opposite things for a cooler (vents
toward its rotation — `Cooler_North` was right) and a workbench (used from the
side opposite it — so a stove a cook stands south of is `Rot4.North`). One
gloss, two conventions, and the corpus contained one of each. Pin 2 is now the
`Rot4` value verbatim, as `map-dump` publishes it. The audit is complete rather
than sampled: `FueledStove` is the only interaction-cell-bearing rotated token
in the corpus.

Three findings in one day resolved to the same rule — `ToStringWord` over
`ToStringHuman`, the rotation suffix over a facing description, and `00a1be7`
before both: **read by field, never through a description.**

`2a7c064` stays OPEN on one bullet: nobody has rendered a real dump and graded
2.5's fixture question 4 against it. That is bench work, it belongs to the
orchestrator, and it is noted on `f7b6207` so it is not re-derived.

### The four leftovers, also cleared

Dorian's call: the two items I had labelled "yours" were mine too.

- **Pushed** — 9 commits and the git-bug refs, including five new bug refs.
- **`2a7c064`'s last bullet did not need a bench, and CLOSED.** "2.5's fixture
  question 4 grades correctly against the published centre" reads like in-game
  work; `transcripts/` banks real `map-dump` results, labels and all, so phase 6
  renders one OFFLINE. `20260831T230213/006-map-dump`, 51x51, 213 labels,
  recorded the day before any of this was contemplated: every code inside its
  own occupied rect, on the centre cell wherever both axes are odd, **7 of 213
  moved by the fix and none of the 201 1x1s.** The four classes that moved are
  exactly the three defects — `3x2 North` is the x-axis error, `1x2 South`,
  `2x1 South` and `1x2 West` are the dropped even-axis rotation shift. Nothing
  else moved because nothing else was broken.
- **The orphan I created, discharged.** Closing `e6faa51` I wrote that the
  missing shape check was "handed to the geometry cluster of the current
  round" — a round that had not started. A promise in a CLOSED issue with no
  owner is the exact shape session 13 wrote up when it found all four M1
  lessons cited by no checklist. `accept/e6faa51-channel-alphabet.py`, 12
  checks, banked envelopes, no bench. It came out bigger than the one line
  promised and better: the issue's third bullet — "a change to the symbol table
  changes the identity" — had been a CONVENTION in a code comment, and is now
  ENFORCED, because each envelope's identity is checked against the constant its
  own source declares and `MapDumpVerbs` must cite `Spatial.cs`'s `AlphabetId`
  verbatim. Bump one and not the other and it fails here instead of shipping
  two truths.
- **The three session briefs** are `ROUND-BUILDABLE-PROMPT.md`. Session C's is
  much smaller than the round plan assumed, and says so: three of its four items
  landed this session, leaving `bac4eba` alone.

Worth noting for its own sake: **two acceptance bullets that read as bench work
were dischargeable offline**, because the repo banks real envelopes and both
questions were about FIELDS and GEOMETRY rather than about game behaviour. The
first instinct both times was "that needs a launch". Check what is banked first.

### State at handover

`main` clean, one worktree, no build debt. The assembly was rebuilt for the
`rot` fix and verified per the workspace rules before committing: pdb path names
THIS worktree under `obj/Release/`, AssemblyConfiguration Release with the Debug
blob absent, `InformationalVersion` naming `f947917` (i.e. its own HEAD), and
231,267 differing bytes at identical file size against the previous blob — far
above the ~200-byte metadata floor, so a real IL change rather than a no-op
verification rebuild.

Nothing has been launched and nothing has been branched. The three sessions are
ready to cut.

## Session 15 — 2026-09-01 (dorian's Linux box). Session A of the buildable round

One opus worker, its own git worktree, branch `spec/site-routine`. Merged
`25b65ff`, rebuilt `43e86c7`. **Nothing has run against a game.**

### What landed

Six commits, in the order the brief set: `edfee42` (`8b4839f`) · `33e9d95` (the
`rot` argument + `Siting.cs`) · `7e23e7f` `SiteGate` · `90e578e` `site-survey` ·
`923800b` `find-rect {def}` · `280a0cb` (`7382bdd`). Verb surface 119 → 120, the
addition being `site-survey`. Six DESIGN decisions-log entries.

The worker was stopped mid-round by a session rate limit, having committed
steps 1 and 2. **That it lost nothing is the brief working:** it had been told
to commit after each numbered step because a half-finished step is fine and an
uncommitted one is gone. Resumed with an explicit "steps 1 and 2 are DONE, do
not re-read the briefs", it finished the remaining four commits.

### Verified by the orchestrator, not taken on report

- `dotnet build -c Release` at the merged tree: 0 warnings, 0 errors.
- The merged tree's `*.cs` is byte-identical to the branch tip's — checked, not
  assumed. The branch deliberately carried no assembly, so the rebuild was owed
  unconditionally rather than by the tree-comparison rule. It landed alone: pdb
  path names this worktree under `obj/Release/`, Release blob present, Debug
  blob absent, `InformationalVersion` = this HEAD, 598,402 differing bytes at
  +24K.
- `923800b` is **340 insertions and zero deletions** in `SpatialVerbs.cs`. The
  Acceptance asked that `find-rect {w,h}` be "byte-for-byte what it was"; a
  zero-deletion diff proves it more cheaply than a comparison run.
- `--selftest` 25/25, and the runner's own closing line is the caveat on the
  whole merge: *"Nothing about the mod was asserted; take this to a bench."*
- **The claim with a bad history, checked because of that history.**
  `Designator_Build.Visible` really does have TEN clauses. `1adc737` amendment
  #1 named two; its verification comment corrected that to six and titled
  itself "one clause of six". The three neither names are
  `buildingPrerequisites`, `discoveryPrerequisites` and
  `requireInspectedGravEngine`. All ten ship as branchable tokens, because a
  clause count in prose goes stale against a DLC boundary and a token does not.

### The worker found a defect in MY work, and it read like a regression

It reported `accept/e6faa51-channel-alphabet.py` as 0/2 from its worktree. That
was correct. **`transcripts/` is gitignored** (`.gitignore:25`), and BOTH suites
written in session 14 read their input from there — so both passed only on this
machine, scored 0 in any worktree or clean clone, and `2a7c064` and `e6faa51`
had already been CLOSED on their evidence.

This is `accept/runs/s13-20260901/README.md`'s own lesson committed the same day
it was written, one directory over: *"an issue that cites a path under the
bench's protocol root cites a file with a lifetime shorter than the issue."* A
gitignored path is that lesson's other half — the file survives locally and
vanishes for everyone else. Fixed in `accept/fixtures/`, kept separate from
`accept/runs/` on purpose (runs/ holds evidence, which may be pruned once its
claim is settled; fixtures/ holds input, which may not), and **verified 25/25
and 12/12 from a worktree with no `transcripts/` at all** — the case that was
actually broken, rather than the case that was convenient to test.

### Resolved by the orchestrator

**godMode is published, never honoured.** The worker built `SiteGate` that way
and flagged it rather than assuming, because it reads as contradicting
`1adc737` amendment #4's Correction 3, which calls godMode "the legitimate
bypass for instant/dev mode". Flagging was right; the choice stands. That
correction was ENUMERATING vanilla's clauses, not prescribing which our gate
honours, and honouring it would make every player verb a god-hand the moment a
dev session left the flag set — worse than no gate, because it looks like one.
The god-hand stays where `3a5ff6c` put it: the path that never calls `SiteGate`.
Session B's `build` inherits this.

### Open, deliberately

All three issues stay `state:doing`. Every bullet settleable by reading or
compiling is settled; every bullet that says "on a bench" has not been run —
the granite replay, the chair tolerated on an interaction cell, two benches
refused with `InteractionSpotOverlaps`, the rotation search in a two-deep
corridor, the even-size `pos` round-trip, and `7382bdd`'s "run the suites".
Said in those words on each issue, including that `7382bdd`'s first bullet is
now true in a WEAKER form than its text asks.
