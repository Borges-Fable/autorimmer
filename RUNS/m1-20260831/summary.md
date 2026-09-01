# M1 run `m1-20260831` — summary

**Verdict: the platform passed, the colony failed.** Stopped at Evan's call on
day 6 of a planned ten, with the hard acceptance already decided against on
day 4.

| | |
|---|---|
| colony | crashlanded, faction *New Arrivals*, temperate forest, 250x250 |
| fixture | `run-agent.sh --quicktest` — Crashlanded, Cassandra, Rough, random tile |
| game session id | `20260901T022324` |
| run / transcript | `RWA_RUN=m1-20260831` -> `transcripts/m1-20260831/` (195 ops) |
| tick span | 723 (staged) -> 322,314 |
| in-game span | Spring 1 -> Spring 6, year 5500 |
| last journal seq read | **175** (next session's `since_seq`) |
| colony notes | `RUNS/newarrivals.colony.md` |

## Hard acceptance (664e9b9)

| criterion | result |
|---|---|
| >=2 of 3 colonists alive at day 10 | **FAILED** — 2 dead on day 4; 1 alive at stop |
| no dev verbs after staging | **PASSED** — last `dev` journal row is tick **723**; 50 dev rows, all at 723 |
| full draft -> fight -> undraft, final digest 0 drafted | **FAILED** — one draft (Captain, 225,935) ended in an involuntary undraft when he was downed, not a completed cycle. Final digest does read **0 drafted** |
| zero unexplained red errors | **PASSED** — **0 red errors** for the whole run |
| food handled | passed — 60.3 food-days at stop, never under pressure |
| one medical event | occurred and was **lost** — see deaths |
| one raid | a `ThreatSmall` manhunter crow; no faction raid arrived in 5 days |
| clothes check fires at least once | **PASSED** — `Alert_NeedWarmClothes` on at 655, off at 1,570, journalled |

## Evidence

- `checklist.ndjson` — 53 ledger lines, no silent skips
- `digests/day-1.json` .. `day-5.json`, `digests/final.json` (named by the
  `day_of_season` each was actually read on — an earlier off-by-one was corrected)
- `saves/Autosave-1..5.rws` — the game's own daily autosaves, archived past the
  5-file rotation; Autosave-5 is tick 300,000
- `mapgen-failure-{1,2}.Player.log` — the two failed launches, captured before
  the relaunch that would have overwritten them
- 8 journal warnings, all mod-startup (Guests, SimpleSidearms, gas tank);
  triaged **mod-under-test**, none ours, none red

## What killed the colony

A single manhunter crow. Neither colonist died of it — both died of **blood
loss, untended**, hours later.

1. Day 1, seek-at-will ON as a standing posture marched all three unarmed
   colonists 60+ cells at a fogged insect hive. I turned it OFF. That left them
   on the vanilla flee branch, which is what they took when the crow arrived.
   -> [[seek-off-is-a-decision-to-flee]]
2. The only pawn with Doctor enabled was Table, and Table was the first
   casualty. `Alert_NeedDoctor` fired *after* he was down.
   -> [[one-doctor-is-zero-doctors]]
3. Table went down at 214,599 inside six back-to-back 2,500-tick advances during
   which I read only `pawns {filter:"hostile"}` and never the journal or digest.
   He bled 11,335 ticks. -> [[read-every-return-or-lose-a-colonist]]
4. Captain fled 150 cells into unexplored ground, went down there, and died
   before the last colonist could cross the distance.

## Platform findings (filed separately)

- **`--quicktest` + `autostart.rws` is a deterministic map-gen failure**,
  root-caused to `Root_Entry`/`Root_Play` racing on `Root.checkedAutostartSaveFile`
  with a scene-targeted long event. Two failures, one clean launch after moving
  the save. -> [[quicktest-and-autostart-collide]]
- **`dev:spawn-thing` returned `ok:true` with `placed:0`** (journal seq 66) for
  the research bench. It never existed; `Alert_NeedResearchBench` never cleared
  and research could not progress all run.
- **An `rwa advance` whose client dies leaves the game running.** `pause`
  reported `was_advancing:true, speed_before:Ultrafast` after a lost tool result.
  The agent owns time only while the client lives. Mitigation adopted mid-run:
  read `status.paused` before every advance.
- **`seek-at-will` echoes `hostility_response:"Flee"` even when seek would
  pre-empt the flee node** — truthful about the field, misleading about behaviour.
- **No build verb (3.3) makes the work surface shallow.** `Alert_ColonistsIdle`
  was up for most of the run; the colony could only haul, grow, cook, chop and
  tend. Four dev-staged buildings were the entire colony for six days.
- **No biome or tile in the observation surface.** 4.3 asks for a temperate
  fixture and nothing reports biome; it had to be inferred from `map-view` terrain.
- **`rwa pawn --id N` collides with `rwa`'s own `--id`** and silently becomes the
  command id -> `bad-args: missing required arg 'id'`. `--args-json` is the way.
- **`tend` is drafted-only** (`FloatMenuOptionProvider.Undrafted` false); the
  undrafted route to a patient is `work-priorities`, not a verb.

## Ended

Normally, at Evan's instruction, bench paused and saved via the game's own
autosave (tick 300,000), then closed. No wedge, no dialog halt, no escalation
posted yet — the escalation and the auditor run are tomorrow's first items.
