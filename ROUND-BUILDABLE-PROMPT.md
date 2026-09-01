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

# SESSION B — the gate's consumers, and `build`

Branch `spec/build-verb`. Session A is MERGED (`25b65ff`, rebuilt `43e86c7`) and
had a watched bench pass (`accept/runs/s15-20260901/`). Read that README before
you start — it lists what is proven and, more usefully, four things that are not.

## What A left you, so you do not rewrite it

`Source/AutoRimmer/Siting.cs` and `SiteVerbs.cs` ship the shared routine.
`site-survey {def, pos|at, rot?, stuff?, margin?}` returns:

- `verdict` — `{ok, source: "GenConstruct.CanPlaceBlueprintAt(godMode:false)", reason}`,
  the game's own sentence verbatim.
- `selectable` — `{ok, source: "Designator_Build.Visible", clause, detail}`, a
  SEPARATE field so "would be refused" and "cannot even be selected" never read
  alike. All TEN of `Visible`'s clauses ship as branchable tokens.
- `gate: "site-gate/1"`, `pos`, `rot`, `pos_source`, `rot_source`, `footprint`,
  `interaction_cells`, `tiers.{footprint,interaction,margin,margin_facts}`, `view`.

**`build` and `dev:spawn-thing {buildable:true}` CONSUME this. Do not
re-implement any of it.**

## Three inherited decisions — do not relitigate

1. **godMode is published, never honoured.** `Designator_Build.Visible`'s first
   clause is `if (DebugSettings.godMode) return true;`. `SiteGate` reports the
   flag and ignores it, because honouring it would make every player verb a
   god-hand the moment a dev session left it set. Resolved by the orchestrator
   on `c718e4a` and `1adc737`. **`build` gets no godMode bypass, ever.**
2. **`rot` defaults diverge per verb, deliberately.** `dev:spawn-thing` stays
   `Rot4.North`; the siting reads use `def.defaultPlacingRot`. 76 vanilla defs
   set it non-North, so unifying would silently re-face every building the
   shipped suites stage.
3. **Row 0 is north; a rotation suffix is the `Rot4` value verbatim**
   (`templates/INDEX.md`). Only matters to you if you touch templates.

## Your work

- **`3a5ff6c`** — `buildable:true` (opt-in; god-hand stays default), `wiped[]`,
  `site-audit`, and `dev:starter-kit` passing `mode:"direct"` for buildings.
  **Live evidence you are closing a real hole:** on the bench, `site-survey`
  refused a bench for `InteractionSpotOverlaps` and `dev:spawn-thing --mode
  direct` **placed it anyway** — two benches on one standing square, nothing in
  the envelope or journal remarking on it. `GenSpawn.CanSpawnAt` runs no
  PlaceWorker. See `accept/runs/s15-20260901/evidence/`.
  **And `wiped[]` is completely unexercised** — every spawn in the pass reported
  `wiped: null` because nothing placed onto an occupied cell. Stage a wall and
  place on top of it, or that bullet ships unproven.

- **`build {def, pos, rot, stuff}`** — the round's headline verb. It lands here
  because `1adc737` #7 establishes instant mode IS `dev:spawn-thing
  {buildable:true}`: same call, and splitting them means writing it twice
  against one DLL.
  **You owe the half A could not test:** `c718e4a`'s acceptance asks that
  `site-survey`'s verdict and the build verb's refusal be the SAME SENTENCE for
  the same arguments. A proved survey ≡ `dev:spawn-thing`'s current refusal;
  assert the `build` half rather than assuming it.

- **`d7c8088`** — the construction read, which `build` is unusable without.
  - **Completion is an ABSENCE.** A finished build leaves no blueprint and no
    frame, so `built` and `cancelled` are the same nothing unless the read keys
    on the placement id and `Frame.CompleteConstruction` / `FailConstruction`
    are journaled (Harmony postfixes, `JournalHooks.cs`'s read-only idiom).
  - **Reading material cost can EMIT A RED ERROR** and every suite counts them.
    `CostListCalculator.CostListAdjusted` `Log.Error`s on null stuff for a
    `MadeFromStuff` def; `Frame.ThingCountNeededWithEnroute` twice more. The
    acceptance has a bullet that proves the guard exists — not optional.
  - `CostListAdjusted` is a memoizing static cache. Acceptable, same ruling as
    `PlaceWorkers`; the comment must say why.

- **`0d9cbd7`** — world-fixture chaining. **Read comment #1 before the body:**
  the body's audit list is wrong by omission and `open-letter` has the identical
  defect.

## Two small things from the bench pass, if they are cheap while you are there

- **The tolerated chair is not listed.** With a chair on the interaction cell
  the tier reports `{ok: true, blocker: null, standable: true}` and names no
  occupant, so an agent cannot tell "a chair is there, which is fine" from "the
  cell is empty" — and that chair is what the next bench's overlap check trips
  on. `blocker` is the wrong field (nothing is blocking); the cell wants an
  `occupants`/`tolerated` list. It is `c718e4a`'s open bullet, but you are in
  the same tiers. Fix it if trivial, report BLOCKED if it would change the
  contract.
- **The rotation search never had to rotate** — open desert took
  `defaultPlacingRot` everywhere. If you stage a two-deep corridor for anything
  else, note whether an East candidate appears.

## Done

`build {def, pos, rot, stuff}` places a blueprint the game would accept, refuses
with the game's own sentence when it would not, and the agent can read back
whether it stalled, progressed, or finished.

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
