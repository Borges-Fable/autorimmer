# What would make the next run better

Compiled at the end of `m1-20260901` (colony wiped day 66, 6 dead, 1 kidnapped).
Split into what is **filed**, what **should be filed and is not**, and what no
code will fix because it was **my play**.

---

## A. Filed — 11 issues + 4 comments

| id | pri | what it would change |
|---|---|---|
| `eef837a` | **p0** | `bill-add` makes a butcher bill with `filter:null` that matches nothing, and `bill-set` won't persist a fix. **Killed three colonists.** Fixing it means a colony can eat its own kills |
| `5cb1f9f` | **p0** | `dialog-dismiss` can't *answer* a dialog. Wedged the run once (faction naming) and **ended** it once (`Dialog_ChooseNewWanderers` — the post-wipe recovery I couldn't take) |
| `5eba561` | **p0** | `rwa`'s 1000-step transcript cap bricks a long run with a bare traceback instead of an envelope. Split this run's transcript and broke its own audit |
| `bb931b9` | p1 | No `save` verb. Every "save before the raid" in this run is really "the nearest autosave, up to 60,000 ticks stale" |
| `aa4391b` | p1 | `work_coverage` can't see an **outranked** doctor. Cost ~8,000 ticks of untended infection |
| `253c694` | p1 | Forced orders collide silently — 4 of 5 equip/wear orders replaced each other, all `ok:true` |
| `e08c3e5` | p1 | Build preflight ignores `constructionSkillPrerequisite` and reports a skill gate as a *material* shortfall |
| `daa269a` | p1 | `owners_total` reads 0 against a populated `beds[].owners` — **T6 is graded on that field** |
| `855117a` | p2 | Mine designations can't be aimed; `map-view`'s `%` glyph collapses ore into worthless rock |
| comment on `d2e1229` | — | Colony start has an unwritten step: the colony must be **named**, and it falls due days later |
| comment on `d2e1229` | — | **Nothing requires a verdict on an active alert.** `Alert_ChessTableNoChairs` sat live 13 days |
| comments ×2 on `664e9b9` | — | The amendment, and the correction that the scenario *does* ship wood, steel and 5 finished research projects |

---

## B. Should be filed and is not — the signals that exist and land nowhere

These are all `no-signal` in `postmortem.md`'s taxonomy: the observers publish
the number, nothing turns it into a decision.

1. **A blueprint that never completes.** `construction.awaiting_materials` sat at
   **exactly 22 for fifteen straight in-game days** (day 40→55) and I read it
   every time. Nothing flags a stalled build. *Wanted: a `turn.md` trip-wire —
   `awaiting_materials` unchanged across N day boundaries is a stalled project;
   name the def and the missing material.* This is what would have surfaced the
   freezer.
2. **A building or room that is LOST.** Rooms silently vanished from
   `temperature.rooms` as their contents burned — room 52 (Laboratory) gone by
   day 40, room 54 (Kitchen) by day 50. A room losing its role, or a bench being
   destroyed, is a structural regression and nothing reports it. *Wanted: a
   triggered item on `construction` `destroyed` rows and on a room's `role`
   changing away from what it was.*
3. **A room that should be enclosed and is not.** `room-at` on the freezer reads
   `outdoors: true, cells: 60082`. A layout placed as a *room* that is still part
   of the great outdoors N days later is a defect the agent cannot see without
   asking cell by cell. *Wanted: `construction --layout_id` to report enclosure,
   or `rooms` to list intended-but-unenclosed layouts.*
4. **Crafted-but-uninstalled items.** Five sculptures sat **minified** in the
   stockpile all run giving zero beauty, while every colonist carried
   `Unsightly environment −5` and `Awful barracks −7`. *Wanted: a trip-wire on
   `MinifiedThing` count > 0.*
5. **A designation nobody can reach.** Already a playbook lesson
   ([[a-designation-outside-the-allowed-area-does-nothing]]) but it belongs in
   the mod: `designate` should run the same `InAllowedArea` test `posture`
   already uses for `area_bound` and report per-target reachability.
6. **A bill asleep.** `next_ingredient_search_tick` in the *future* means the bill
   ran a failed search and backed off — it stays dead long after its ingredients
   are freed. Re-creating the bill resets it. `production-still-runs` names the
   field; nothing flags the future value.
7. **`resources.*` has no map-wide twin.** `food_rot` publishes both a
   stockpile-scoped and a map-wide figure with a `scope` note; `steel`, `wood`,
   `meds` and `components` do not, and "0 in stockpiles" was misread as "none"
   three separate times.

---

## C. My play — what no code will fix

1. **I never looked at the base after day 1.** One render on the first morning,
   then 66 days driven entirely off numbers. A single `render` would have shown
   a freezer with a hole in it and a farm outside the wall.
2. **I never mined.** 128 `MineableSteel` cells, designated repeatedly, **not one
   ever mined.** I set Mining to 0/3/4 during each food crisis and never restored
   it. Steel ended at 33 — which is precisely why the freezer's second cooler
   (90), the Hi-Tech bench (100) and the hidden conduits (120) never happened.
   The whole causal chain of the wipe runs through this one omission.
3. **I never used Animals.** Lacey had **Animals 10 with a major passion** — the
   best skill on the starting roster — and I never tamed a single creature.
   Tamed animals are renewable meat that walks itself home, plus haulers. In a
   colony that starved beside unbutcherable corpses, this was the answer sitting
   in plain sight on the roster sheet from day 1.
4. **I let the creepjoiner timeout expire.** `Alert_CreepJoinerTimeout` ran 30,000
   ticks and lapsed at the exact tick Sean turned hostile. A fourth colonist
   became the thing that killed two.
5. **I sent Lacey at that creepjoiner at 56% health** on a def read that ignored
   her injuries. She was the second doctor; Wouter followed her in bare-handed.
6. **I never placed the art, and never replaced the conduits with hidden ones**
   despite being asked — both because I had no steel, because of (2).
7. **Priority thrash.** I rewrote work priorities ~20 times, repeatedly flipping
   the same job between 0 and 1 within minutes. A stable, written division of
   labour would have beaten every one of those edits.
8. **I stalled the clock twice** — duplicate drivers deadlocking on
   `unread-journal`, and editing a shell script while it was executing.
9. **I batched turns past decision letters four times** — a trader, two quests,
   Serenity's joiner letter, and finally *Breaking Jimmy Out*, the rescue that
   would have brought a colonist home.

---

## The one-line lesson

The colony did not die of raiders, bad luck, or a hard map. **It died because it
had no larder**, and it had no larder because I never mined the steel that was
20 cells away and designated. Everything else is downstream of not looking.
