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
