# AutoRimmer — agent play + mod test platform

Read `DESIGN.md` first. The build plan is 23 spec issues in git-bug — start at
the muster (`git-bug bug --status open`, the `type:muster` issue), whose later
comments carry each session's handover. `RUNLOG.md`'s last section says where
the previous session stopped and why. `ORCHESTRATION-PROMPT.md` is the prompt
that runs the build.

**The mod is real and running.** `Source/AutoRimmer/` ships ~44 verbs — the
protocol, journal, time driver, the observation surface (digest, pawns, world,
spatial), and the `dev:*` god-hand — against a live bench. A spec issue adds to
a working system; read the shipped code before writing more of it.

Ground rules that outrank convenience:

- **Never launch `_RimWorld-Testing` or the MP install.** The agent bench is
  `_RimWorld-Agent` (`profile/make-profile-agent.sh`, or the `.ps1` on BORGES).
  The workspace-wide "never launch RimWorld" rule has exactly one carve-out,
  and it is that install. **Workers never launch anything** — the orchestrator
  runs all in-game acceptance personally.
- **Observers never mutate game state.** Lazy-init getters and cached lists
  rebuilt on read are the standing hazard; `PawnSafe.cs` and `WorldSafe.cs`
  hold the guarded routes and document the ones already found.
  (`_mp/DETERMINISM.md` documents the hazard class.)
- **The gate lives in the widget, not in the model.** RimWorld puts its
  preconditions in the UI layer, so every player verb re-implements its
  precondition and cites it (file + member). `dev:*` may bypass; a player verb
  may not. See DESIGN §Action model.
- All Verse access on the main thread at a safe point; the file half of the
  bridge never touches Verse (analyzerbridge is the template).
- `dotnet build -c Release`; `Build:` commits stand alone; check the pdb path
  before committing any DLL (workspace CLAUDE.md build rules apply in full).
- Every parallel worker gets its own git worktree. Disjoint files are not
  enough — a shared HEAD is the hazard (session 4).
- Specs are contracts: the Acceptance section is the definition of done.
- **Ambiguity is RESOLVED, not queued** (Dorian, session 4: "there should be
  nothing on the muster for me"). Resolve it by INVESTIGATION against the
  decompiled source, record the decision and its reasoning in DESIGN.md's
  decisions log, and comment it on the affected issues. Where the resolution
  reveals real missing work, file it as a spec issue with an Acceptance
  section. A worker resolving its OWN spec's Open-questions section on-issue is
  normal; a resolution that would change another spec's contract is reported
  BLOCKED instead. The bar is "did I check this against the game's own source",
  not "am I allowed to decide".

Decompiled 1.6 source: `rimworld-tools/Info/decompiled/RimWorldBase/` on
dorian's box; `misc/rimworld/reference/decompiled/RimWorldBase/` on BORGES.
Line offsets differ between the two — **verify by member name.**

Label map: `wave:0..5` (dependency gates), `agent:fable|opus` (worker model),
`type:spec`, `type:muster`, plus workspace-standard `priority:`/`state:`/`mod:`.
