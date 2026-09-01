# Round: make blueprints buildable — three session briefs

Paste ONE section below into a worker session. Session 14 (the pre-flight) did
the verify-first pass; its results are in `RUNLOG.md §Session 14`. **Do not
re-derive them** — four claims in the original round plan were refuted there and
the corrections are already on the issues.

`main` is clean, pushed, no build debt. The assembly is verified (pdb names this
worktree, Release, `InformationalVersion` = `f947917`).

## Shared ground rules

- **Your own git worktree.** Disjoint files are not enough; a shared HEAD is the
  hazard (session 4).
- **You never launch anything.** Not `_RimWorld-Agent`, not anything. The
  orchestrator runs all in-game acceptance personally.
- `dotnet build -c Release`. `Build:` commits stand alone. Check the pdb path
  names YOUR worktree before committing a DLL — and note that a branch's own
  `Build:` will name a temporary worktree, which is why the rebuild happens on
  `main` at merge (session 13, `11ef46c`).
- **Resolve, do not queue.** Against the decompiled source, cited by file +
  member, never line numbers. Resolving your OWN spec's open questions on-issue
  is normal; a resolution that changes another spec's contract is reported
  BLOCKED instead.
- Acceptance sections are the contract. Assert key EXISTENCE; `eq(None)` on an
  absent key passes and hides a wrong dig path.
- **`Source/AutoRimmer/` is one DLL, so A and B are serial.** C is not and runs
  alongside A.

## Already settled — do not re-open

- Designator coverage is COMPLETE. `deconstruct`, `cancel`, `uninstall`,
  `claim`, `fill-in` and the mine/plant/terrain set are in `DesignationVerbs.cs`
  (not `DesignateEngine.cs`, which is the target resolver);
  `build-roof`/`no-roof`/`ignore-roof` are in `AreaVerbs.cs`, where the game
  puts them. `cancel` is `Designator_Cancel`, which destroys blueprints and
  frames — so `cancel-layout` is bookkeeping over a shipped verb.
- Roofing needs no spec. Auto-roof covers enclosed non-edge non-fogged player
  rooms ≤26 regions and ≤320 cells. **But `TryGenerateAreaFor` only QUEUES** —
  `TryGenerateAreaNow` runs next tick, so an acceptance reading the roof area in
  the same call as the placement sees nothing and reports a correct
  implementation as broken.
- Row 0 is NORTH, and a rotation suffix is the `Rot4` value verbatim
  (`templates/INDEX.md`, pinned session 14).
- `place-layout` / `cancel-layout` are NEXT round, not this one.
- `e6faa51` and `2a7c064` are closed with runnable suites.

---

# SESSION A — the site routine. Nothing else can start.

Branch `spec/site-routine`. Four commits, in this order.

1. **`8b4839f`** — small, lands first, and `c718e4a` consumes it.
   `DevVerbs.WhyNoSpawn` returns `{reason, cell, thing, tier}` instead of a
   string; `Blockers.At` is called with the REFUSING cell, not the target; the
   failure row gains `cell_role`. The bench proof is banked at
   `accept/runs/s13-20260901/results/accs13-026-devspawnthing.json`: the reason
   names granite on the interaction cell, the blocker names a `WoodLog` on the
   target cell with `removal: "none"` — the opposite of the truth.

2. **The `rot` argument.** There is none anywhere in the mod today;
   `dev:spawn-thing` passes `thing.Rotation`, whatever `MakeThing` left. You
   cannot place a blueprint without facing, and an interaction cell moves with
   rotation. This commit also lands the corner↔centre conversion helper, since
   the map is rotation-dependent.

3. **`c718e4a`, split into three commits** — it is three features in one issue:
   (a) the shared `SiteGate` routine (`GenConstruct.CanPlaceBlueprintAt(godMode:false)`
   plus `Designator_Build.Visible`'s clauses through `WorldSafe.Finished` —
   NEVER `IsResearchFinished`, which inserts into scribed state);
   (b) `site-survey` with its three tiers and its ASCII crop;
   (c) `find-rect {def}` with the four-rotation search.
   `BuildableDef.PlaceWorkers` is a lazy-init getter — acceptable, same ruling
   as the game's own first hover, and the code comment must say so.

4. **`7382bdd`** last, and **the choice is pre-authorized — do not come back for
   it.** Attempt the per-verb arg whitelist in `VerbArgs`; if it breaks a
   shipped suite for anything but a genuine caller bug, fall back to the narrow
   `pos_source` fix and record which suites forced it. 119 verbs is the burden;
   `StarterKit` and `world-fixture` forward constructed arg dicts to other
   handlers, so the check goes at the ENTRY point.

**Done:** `site-survey`'s verdict and `find-rect {def}`'s gate are the same
routine and produce the same sentence for the same arguments. Every candidate
publishes `{at, w, h, rot, pos}` and its `pos` round-trips: spawning there with
that rot yields an `OccupiedRect()` equal to the candidate's rect, for all four
rotations of an even-sized def.

---

# SESSION B — the gate's consumers, and `build`. After A merges.

Branch `spec/build-verb`. This is where the round's headline verb lands.

- **`3a5ff6c`** — `dev:spawn-thing {buildable:true}` (opt-in; the god-hand stays
  default), `wiped[]` on the default path, `site-audit`, and `dev:starter-kit`
  passing `mode:"direct"` for buildings. That omission is M1's `placed:0` root
  cause: `StarterKit` builds `{def, count, stuff, quality, pos, stockpile,
  forbid}` with no `mode`, so a building goes through `ThingPlaceMode.Near`.

- **`build {def, pos, rot, stuff}`** — consumes A's `SiteGate`; does not
  re-implement it. It lands here, not in its own session, because `1adc737` #7
  establishes that instant mode IS `dev:spawn-thing {buildable:true}` — the same
  call, and splitting them means writing it twice against one DLL.

- **`d7c8088`** — the construction read, which `build` is unusable without.
  Three things to carry:
  - **Completion is an ABSENCE.** A finished build leaves no blueprint and no
    frame, so `built` and `cancelled` are the same nothing unless the read is
    keyed on the placement id and the `Frame.CompleteConstruction` /
    `FailConstruction` transitions are journaled (Harmony postfixes, in
    `JournalHooks.cs`'s existing read-only idiom).
  - **Reading material cost can EMIT A RED ERROR**, and every suite counts them.
    `CostListCalculator.CostListAdjusted` `Log.Error`s on null stuff for a
    `MadeFromStuff` def; `Frame.ThingCountNeededWithEnroute` twice more. A naive
    observer turns a clean run red by reading it. The acceptance has a bullet
    that proves the guard exists — it is not optional.
  - `CostListAdjusted` is a memoizing static cache. Acceptable, same ruling as
    `PlaceWorkers`, and the comment must say why.

- **`0d9cbd7`** — world-fixture chaining. Its verification comment is load-
  bearing: the audit list in the body is WRONG BY OMISSION, and `open-letter`
  has the identical defect. Read comment #1 before the body.

**Done (the round's DONE MEANS):** `build {def, pos, rot, stuff}` places a
blueprint the game would accept, refuses with the game's own sentence when it
would not, and the agent can read back whether it stalled, progressed, or
finished.

---

# SESSION C — CUT FROM THIS ROUND

`bac4eba` (the 7x7 module grid) was in the original round plan as a parallel
session. It is **held until after M2** — Evan, 2026-09-01, and it is the first
thing after.

The parallel slot was a RESOURCE argument (it touches no C#, so it does not
contend for the one DLL) and that is not a sequencing argument. `bac4eba`
comment #1 is Evan's own ruling from session 11 and already said so: *"3.3 goes
first, as already specced; this issue is the follow-on. Prove `place-layout`
can put ONE room down correctly at all; then make rooms tile."*

Its acceptance settles it independently — three of its five bullets are
unsatisfiable before `place-layout` exists: the off-phase origin refusal, two
adjacent modules sharing a wall built once and billed once, and the 2x2
rehearsal reading as a connected base. Only "record the module in DESIGN" and
"re-cut the templates" are reachable now, and re-cutting the corpus to a module
nobody has placed is the thing worth not doing.

The other three items the round plan had put in this session are already done
(session 14): the north pin, `2a7c064` and `e6faa51`.

**So this round is A then B. Two sessions.**
