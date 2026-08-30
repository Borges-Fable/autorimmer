# AutoRimmer

> Agent play platform: Claude plays an unattended RimWorld instance — structured
> observation, full player verb set, turn-based time control — through a
> file-based command bridge, and uses the colony as a regression-test platform
> for our mods. The C# bridge mod, the bench profile, the `rwa` CLI and (later)
> the `rwtest` runner all live in this repo — the client and server halves of
> one protocol, in one clone.

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

## Driving the bench

`rwa/rwa` is the CLI: write a command, wait for the result, print it. Start with
`rwa/rwa status` — it grades the bench into five states, and only one of them
means "the game is not running". `rwa/README.md` is the manual (protocol root
resolution, argument syntax, jq recipes, transcripts, `rwa watch`), and
`rwa/selftest.sh` exercises the whole client against a synthetic protocol root
with no game involved.

## Issues

This repo uses [git-bug](https://github.com/git-bug/git-bug) for issue tracking.
Issues live in `refs/bugs/*` inside this repo's `.git/` — **they are not files
in the worktree**, so a plain `git clone` does not bring them and a plain
`git push` does not send them.

```bash
git-bug bug --status open   # list open issues
git-bug bug show <id>       # view an issue
git-bug bug new -t "..." -m "..."
git-bug bug comment new <id> -F body.md   # prefer -F; -m mangles apostrophes
```

Cross-repo dashboard: see `../_meta/dashboard/`.

## Working from a fresh clone

Verified end to end on 2026-08-30 — a plain clone gives you the code and none of
the plan, and `git-bug pull` on a fresh clone fails with "No identity is set"
until you create one. The working sequence is:

```bash
git clone https://github.com/Borges-Fable/autorimmer.git
cd autorimmer
git-bug user new --name "<you>" --email "<you@example.com>" --non-interactive
git-bug pull origin
git-bug bug --status open        # 23 issues: 17 open, 6 closed
```

Then read `DESIGN.md`, then the muster (`git-bug bug show 01f0b85`), then
`RUNLOG.md` for where the last session left off, then the amendments on the
wave 1-3 specs. `ORCHESTRATION-PROMPT.md` is the exact prompt body that runs the
build.

**Pushing issue changes back.** `git-bug push origin` fails against an
HTTPS remote authenticated through the `gh` credential helper
(`authentication required: No anonymous write access` — git-bug does its own
transport and does not consult the helper). Push the refs with git instead:

```bash
git push origin 'refs/bugs/*:refs/bugs/*' 'refs/identities/*:refs/identities/*'
```

Do that whenever you change issue state, or the next machine sees stale labels.

The bench profile scripts in `profile/` are machine-portable through three env
overrides, all defaulting to this workspace's layout:

| var | what | default |
|---|---|---|
| `RIMWORLD_VAULT` | the mod-repo workspace | `/home/dorian/projects/rimworld` |
| `RIMWORLD_STEAM` | the Steam RimWorld install | `$HOME/.steam/steam/steamapps/common/RimWorld` |
| `RIMWORLD_TOOLS` | the `rimworld-tools` repo | `<vault>/../rimworld-tools` |

`make-profile-agent.sh` reports anything it cannot find as `MISSING SRC` and
counts it; it never guesses. Note that the bench needs the sibling mod repos and
a RimWorld install to be useful — on a machine without them, the spec work that
does not touch the game still proceeds.

## License

MIT — see `LICENSE`.
