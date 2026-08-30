# AutoRimmer

> Agent play platform: Claude plays an unattended RimWorld instance — structured
> observation, full player verb set, turn-based time control — through a
> file-based command bridge, and uses the colony as a regression-test platform
> for our mods. C# bridge mod + bench profile in this repo; the `rwa` CLI and
> `rwtest` runner live in `rimworld-tools`.

**Status:** spec
**Type:** c#-system + tooling
**Target:** the whole mod suite (first analysis target: Factions/Guests)

## About

Not a fork and not a reimplementation: the shipped game runs normally on its own
bench install (`_RimWorld-Agent`, window parked on a Hyprland special workspace so
Dorian can watch at any time), and the AutoRimmer mod is the agent's eyes and
hands. The agent drives it turn-based from outside: act → advance-until → read
journal + digest → think.

Read `DESIGN.md` first. The build plan is a set of spec issues in git-bug —
start at the muster (`type:muster`). `ORCHESTRATION-PROMPT.md` is the prompt
that runs the build.

## Issues

This repo uses [git-bug](https://github.com/git-bug/git-bug) for local issue tracking.
Issues live in `refs/bugs/*` inside this repo's `.git/`.

```bash
git bug ls           # list open issues
git bug show <id>    # view an issue
git bug add          # create a new issue
```

Cross-repo dashboard: see `../_meta/dashboard/`.

## License

MIT — see `LICENSE`.
