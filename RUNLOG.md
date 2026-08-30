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
