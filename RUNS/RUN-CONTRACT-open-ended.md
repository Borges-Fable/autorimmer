# Run contract — the open-ended run

No day count. The run ends when the colony ends, when Evan calls it, or when you
have held every goal below simultaneously for **ten consecutive in-game days**.

You are playing, not being tested. Nobody will step in. Every step you skip is a
step that does not happen.

---

## Day 1, before anything else — four checks that decide the whole run

1. **`work-priorities` — confirm manual priorities are ON.** With
   `useWorkPriorities` off, every enabled work type is a flat 3: there is no 4 to
   demote to and no 0 to avoid, and the standing rules below become vacuous. Turn
   it on. Say in the ledger that you did.
2. **Is there a steam geyser in reach?** `map-dump` / `landmark` / `nearest`.
   **Geothermal is map-dependent.** If there is no geyser you can defend, say so
   on day 1 and drop that goal — do not discover it on day 40.
3. **Does the outfit policy admit armour?** `policies`. Crafted armour that no
   policy allows sits in a stockpile forever. Last run touched `policy*` **four
   times in 4,599 calls** while rewriting work priorities 55 times.
4. **Name the colony when the prompt comes.** `dialog-accept` now exists and the
   game pre-fills a valid name. Do not `dialog-dismiss` it — that closes the
   window without answering and it re-raises every 1,000 ticks.

---

## Goals

Hold all of these at once for ten days.

| # | goal | how it is graded |
|---|---|---|
| G1 | **Deep mineral scanner** | researched AND built AND powered. `research` shows the project complete; `things --def DeepScanner` finds it; `power` shows it drawing |
| G2 | **Geothermal power** | `GeothermalGenerator` built on a geyser, connected, and producing. `power` net positive with it counted. Waived only if day 1 established there is no geyser |
| G3 | **A defensive militia** | every colonist capable of violence holds a ranged weapon (`pawn.equipment`), and **no armour piece is sitting unworn** anywhere on the map |
| G4 | **A base that keeps growing** | room count and enclosed floor area both higher than ten days prior. Expansion is a standing activity, not a phase |
| G5 | **Art placed** | `MinifiedThing` count is **0**, and no colonist carries `Unsightly environment` |
| G6 | **Strangers taken on** | every joiner, wanderer and refugee offer was answered on purpose — accepted or declined with a reason in the ledger. Never lapsed |

**G3 carries a known blind spot.** Armour *rating* is unreadable — `47547ca` is
open, apparel rows carry no armor value. So G3 grades "armed and wearing the
armour that exists", not "well protected". Do not pretend otherwise in the report.

---

## Standing rules

These are not suggestions. Each one is a colonist that died last run.

1. **Never set a work priority to 0. Use 4.** Zero disables the work type
   entirely and nothing will ever be done. Last run set Mining to 0 during a food
   crisis and never restored it: **128 designated steel cells, not one mined**,
   and that is the whole causal chain of the wipe. `work_coverage` **will not flag
   this** — it checks capability, not whether anyone has the job enabled.
2. **Butcher within one in-game day of any kill, or do not hunt.** There is no rot
   clock. A corpse is inedible after 2.5 days and `hp 95%` tells you nothing about
   rot — last run read "fresh" off hit points with 600 meat rotting on the ground.
   Unconditional, because the conditional version needs a number you do not have.
3. **Check every day for unworn armour.** When you find some, check the **outfit
   policy** before you check the hauling. That is the usual cause.
4. **Query `MinifiedThing` every day.** Non-zero means finished work is doing
   nothing. Five sculptures sat in a crate for 66 days last run.
5. **If `advance` refuses twice with the same code, stop and read
   `unread_after`.** Last run burned **sixty consecutive turns and zero in-game
   ticks** on `unread-journal` because a `journal {limit:2000}` read stopped nine
   rows short — and the nine rows were a colonist's death. The envelope said
   `unread_after: 9` twice a turn for sixty turns.
6. **Never advance past a letter that carries a choice.** Not "past a bad letter"
   — past a *choice*. Last run lost a trader, two quests, a free colonist, and the
   rescue that would have brought Jimmy home, all inside batched turns.
7. **Read `uses_outdoor_temp`, not just `enclosed`.** A sealed room with no roof is
   on outdoor temperature and `enclosed` reads `true`. Roof it, then set a
   temperature target — a built freezer holds 21°C by default.
8. **Batteries before geothermal.** An unbuffered grid loses the larder to one
   Zzzt, and you will not notice.
9. **Tame animals.** `designate {type:"tame"}` exists and last run never used it
   while carrying Animals 10 with a major passion. Renewable meat that walks
   itself home, plus haulers.
10. **Look at the base.** A `render` or a wide `map-dump` every few days. Last run
    took one render on the first morning and then drove 66 days off numbers, with
    a hole in the freezer wall the whole time.
11. **`find-rect` before you place.** Not after the refusal.

---

## What the mod will now tell you that it would not have last run

Read these; they exist because they killed people.

- `bills[].health` and `.remedy` — one word per bill, and the verb that fixes it.
  `NO BILL LEVER FIXES THIS` means stop sending `bill-set`.
- `construction {layout_id}.enclosure` — `enclosed` and `uses_outdoor_temp`,
  separately, plus every unroofed cell.
- `designate`'s `reach` block — per-pawn allowed-area reachability, the excluding
  area named, and the fix. A designation nobody can reach is refused, not accepted.
- `construction`'s stall clock and `no_builder` / `skill_blocked` — a blueprint's
  age in its current state, and whether the blocker is a material or a skill
  ceiling.
- `save {name}` — take one at every threat and every day boundary.
- `error.class` on failures — `refused` and `flow` are the protocol working;
  `fault` is a problem. Do not treat a `busy` as a failure.

### Added 2026-09-09 — thirteen changes, none of them yet run in a game

**Read this section twice. It is the largest change to the protocol since the run
contract was written, and none of it has been in front of RimWorld.**

- **`advance` now replies with a SCREEN, not a field set.** Six panels in order:
  the stop line, the map, the colonists, the gauges, what changed since you last
  looked, and the decisions you owe. `look` returns the same object on demand. The
  stop line is the first thing in it and says why the clock stopped.
  **Do not pipe this reply through a filter.** Last run 2,821 of 2,988 `rwa` calls
  went through `python3 -c`, `head` or `/dev/null`, and the audit's "the agent
  ignored 52 truthful fields" turned out to be "the agent's own filter deleted 49 of
  them before it saw them". The screen is the reply. Read the reply.
- **`decisions` are served one kind at a time**, with `groups_pending` and
  `total_owed` beside them. Nothing forces you to answer yet — the answer gate is
  not built — but `decisions.gate` says so on every screen, and an unanswered
  decision is still a decision you skipped.
- **Every journal row now says `by`**: `agent`, `mod`, `game` or `human`. A human
  row is Dorian doing something by hand. Last run 22 of his interventions were
  invisible and three "deaths" were debug-menu residue.
- **A `destroyed` event exists.** Player buildings, frames and corpses, carrying the
  game's own `DestroyMode`. Last run a manhunter pack destroyed two turrets and an
  autocannon and the journal recorded nothing; the agent rebuilt the two it counted
  and never learned about the third. That was the first link in the wipe.
- **Losses are levels.** A destroyed building stays on its gauge as "was N" until
  it is rebuilt. There is no `write-off` verb yet, so a level you do not rebuild
  persists — that is deliberate.
- **`threats.hostiles_active` and `.hostiles_dormant` sit beside `hostiles`.** A
  dormant mech cluster is hostile by faction but is not fighting anyone. Last run 31
  digests read `hostiles: 5` beside `danger: "None"` and every "is the fight over"
  read was wrong. Key "the fight is over" on `hostiles_active`, not `hostiles`.
- **Time that moves with no advance in flight is now journaled** as `clock` rows,
  and every advance carries `since_last_look` — including spans where a human drove.
  Last run 15.3% of all game time was invisible.
- **`posture` with no `pawns` is REFUSED.** It used to widen to every colonist: one
  such call unbound all seven and they were 40–70 cells outside the walls when the
  fatal raid landed. Pass `pawns` explicitly, or `pawns:"colonists"` to say
  colony-wide **on purpose**.
- **A stray or missing key is refused on any verb whose default mutates** —
  `posture`, `alert-mute`, `carry`, `trade-start`, `build`. A dropped `dry_run` used
  to place a real blueprint; it did that eleven times last run.
- **Hyphenated argument keys now work.** `dry-run` is read as `dry_run`.
- **You can order from Dad's catalog**: `nepo-catalog`, `nepo-order`,
  `nepo-inbound`. Items, mechs, animals and slaves. With unlimited money and instant
  delivery set, **this colony cannot starve** — if food is short, order food. Do not
  let a colonist die of hunger and then report it as a finding.

### Your squads behave differently now (seekandkill)

- **An engagement leash bounds every fight the mod picks**, default 60 cells from
  home. A squad will not march across the map any more. Last run a pawn walked 130
  cells to melee shamblers and the downing that followed started the infection that
  killed him.
- **Runt clusters skip squad dispatch.** One or two stragglers are handled by
  individual initiative rather than by marching a formation at them.
- Both refusals are visible in the log under `[SK/Leash]` and `[SK/Density]`.

## Known-blind, work around it

- Armour rating (G3 above).
- ~~`construction.gaps` returns a .NET type name~~ — **FIXED 2026-09-09** (`4950f14`).
  `gaps` is now a JSON array of cells, the same shape as `first_gap`. You no longer
  need the `room-at` workaround.
- `resources.*` is stockpile-scoped with no map-wide twin except `food_rot`. "0 in
  stockpiles" is not "none on the map" — this was misread three times last run.

---

## Bench setup — do this before you start playing, and say in the ledger that you did

The run is watched. Two surfaces, two workspaces:

**Workspace 2 — the cockpit.** Read-only; it sends nothing and cannot disturb
your run.

```
./cockpit/launch-cockpit.sh <your-run-name> --follow
```

Launch it into workspace 2:

```
hyprctl dispatch exec "[workspace 2] kitty --title autorimmer-cockpit --hold \
  /home/dorian/projects/rimworld/autorimmer/cockpit/launch-cockpit.sh <run> --follow"
```

Use `--follow` so it tracks the tail of the run as you play. It reads the
transcript and the journal **as files** — it never issues a verb, so it cannot
move the journal watermark or claim a step in your run.

**Workspace 3 — the game.** `run-agent.sh` parks the window on the hidden
special workspace, which is right for unattended runs and wrong here.

```
./profile/show-bench.sh --wait 90       # moves it and follows
hyprctl dispatch movetoworkspacesilent "3,address:<the RimWorldLinux window>"
```

**For a watched run, fix the resolution first.** The bench ships
`<screenWidth>640</screenWidth>`, `<screenHeight>480</screenHeight>`,
`<fullscreen>False</fullscreen>`, which Unity draws as a small viewport
letterboxed inside a large window — it looks broken and is not. Set the two
dimensions to the monitor size with `<fullscreen>True</fullscreen>` in
`_RimWorld-Agent/.../Config/Prefs.xml` **before launching** (RimWorld reads
these only at startup). A backup of the small values is at
`Prefs.xml.bak-640x480`; **restore it when the run ends**, since the large
surface costs throughput on a bench rendering to nobody.

`rwa watch on` reveals the window and unthrottles it from 30 to 60 fps if you
need that later.

---

## The ledger

One line per decision, with the reason. Every letter answered or declined on
purpose. Every rule above that you broke, and why. A `save` at every threat.

The run's value is the record, not the score.
