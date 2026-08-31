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
