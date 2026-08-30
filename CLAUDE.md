# AutoRimmer — agent play + mod test platform

Spec stage: no code yet. Read `DESIGN.md` first; the build plan is 20 spec
issues in git-bug — start at the muster (`git-bug bug --status open`, the
`type:muster` issue). `ORCHESTRATION-PROMPT.md` is the prompt that runs the
build with an Opus orchestrator dispatching fable/opus workers per the
`agent:` label on each issue.

Ground rules that outrank convenience:

- **Never launch `_RimWorld-Testing` or the MP install.** The agent bench is
  `_RimWorld-Agent`, created by `profile/make-profile-agent.sh` (spec 0.1).
  The workspace-wide "never launch RimWorld" rule has exactly one carve-out,
  and it is that install.
- **Observers never mutate game state.** Watch for lazy-init getters
  (`_mp/DETERMINISM.md` documents the hazard class).
- All Verse access on the main thread at a safe point; the file half of the
  bridge never touches Verse (analyzerbridge is the template).
- `dotnet build -c Release`; `Build:` commits stand alone; check the pdb path
  before committing any DLL (workspace CLAUDE.md build rules apply in full).
- Specs are contracts: the Acceptance section is the definition of done.
  Spec ambiguities go back to the muster as comments — never silently
  resolved, by worker or orchestrator.

Label map: `wave:0..5` (dependency gates), `agent:fable|opus` (worker model),
`type:spec`, `type:muster`, plus workspace-standard `priority:`/`state:`/`mod:`.
