# Every open issue, accounted for

The pass over all 84 issues that were open on 2026-09-03 (76 in `autorimmer`, 8 in
`seekandkill`), against the cockpit design in `COCKPIT.md`. One line per id. Nothing
skipped. Every change described here was made in `git-bug` the same day and read back;
the id's own comments carry the full text, this file carries the verdict.

Five verdicts:

| verdict | meaning |
|---|---|
| **closed into** | closed; a root spec carries its ask as an acceptance bullet and names the id |
| **merged** | closed as a duplicate; the surviving issue carries a merge note |
| **rewritten** | kept; a comment reframes it against the cockpit, sometimes re-waved |
| **noted** | kept as is; a comment adds this run's evidence or a cross-reference |
| **untouched** | kept as is, no comment; this run says nothing about it |

## The roots, filed 2026-09-03

| id | what | pri |
|---|---|---|
| `b4adee2` | The cockpit (wave 6): the agent does not choose what to look at — the root; order and measurement | p0 |
| `827c1bf` | Who did it, and what was destroyed: provenance on every journal row, and a destroy event — **first** | p0 |
| `975973e` | The screen: advance replies with what a player would be looking at | p0 |
| `ee4b4f8` | The map: a picture every turn, chosen by the mod, drawn the way the game draws it | p0 |
| `ffef0d7` | The chores: what the mod does without a turn | p0 |
| `27bf321` | Honest instruments: ok means what you asked for happened | p1 |
| `70ee75e` | The client: prints the screen, renders the picture, never loses a result | p1 |

**The three that matter:** the screen (`975973e`, with its journal half `827c1bf`),
the map (`ee4b4f8`), the chores (`ffef0d7`). Two things land now regardless:
`e440676` and seekandkill `a6b1aa0`.

**The count.** 84 open before; 13 closed, 7 filed; 78 open after (in `autorimmer`: 13 closed into a root or merged, 9 rewritten, 17 noted, 37 untouched; in `seekandkill`: 1 noted, 7 untouched). The backlog is not
much shorter, and that is the honest result: most of the 84 are specs for work never
built or findings from runs this one did not exercise, and "untouched" is a real
verdict. What changed is that there is now a root, an order, and a name for the three
that matter. All new issues carry `wave:6`; four existing issues were re-waved to it
(a call that could go the other way — the wave labels were dependency gates in the
original plan, and these four now depend on the cockpit).

## `autorimmer` — 76

### Closed into a root (12) or merged (1)

| id | was | verdict | into |
|---|---|---|---|
| `e811574` | resources.* has no map-wide twin | closed into | `975973e` — the FOOD and STOCK gauges, two numbers on every screen |
| `4c12e5d` | crafted-but-uninstalled sculptures uncounted | closed into | `975973e` — the G5 gauge; its verb half merged into `6d4ca8a` |
| `c621849` | install verb: G5 unreachable | merged | duplicate of `6d4ca8a` |
| `91bc250` | no verdict required on an alert; no age | closed into | `975973e` — lights carry an age; an old unmuted alert is a decision owed |
| `c41bdcc` | nothing counts armament | closed into | `975973e` — the armed count and the G3 gauge |
| `cd92db7` | file-mode journal never moves the watermark | closed into | `975973e` — the answer gate replaces the read gate; no watermark to move |
| `9dc0caa` | a truncated journal read reports ok:true | closed into | `975973e` — same; its ok:true half is `27bf321` rule 3 |
| `9227839` | the colony must be named and nothing says it is pending | closed into | `975973e` — a pending dialog is a decision owed |
| `f1a1700` | a building or room that is LOST (parked) | closed into | `827c1bf` for the destroyed building; `975973e` for "was N" as a level; the room-role half is a stated limit |
| `a8d8ada` | a corpse that vanishes has no reason attached | closed into | `827c1bf` — the destroyed event, corpses included, DestroyMode verbatim |
| `fee81b2` | threat-pardon reports success with refused_count 0 | closed into | `27bf321` rule 1 |
| `5fd6dde` | the mod says what is wrong but not what fixes it | closed into | `27bf321` rule 4; acts on every decision in `975973e` |
| `7f0e245` | AUDIT: where else should the mod hold the loop | closed into | `b4adee2` — its written audit is `COCKPIT.md` §The line; its measurement is the root's acceptance |

### Rewritten (9)

| id | was | what changed |
|---|---|---|
| `d2e1229` | 4.2 Play-loop skill | the loop is read the screen, decide, act, advance; no checklist to load; escalation is a decision of kind "ask"; acceptance now on the cockpit; re-waved to `wave:6` |
| `d32eadd` | 4.4 Checklist budget + lesson retirement | narrowed to the playbook half; the checklist half is met by construction (gauges replace lines, chores replace rules) |
| `cc8988c` | Post-raid procedure | chore 1 of `ffef0d7`, extended with tend-until-stable, unforbid and undraft; rescue under fire drawn out as judgement; re-waved to `wave:6` |
| `9717e52` | no verb reads or presses gizmos | split: the read half is `inspect` in `ee4b4f8`; this keeps the press half; `wave:6` added |
| `f7b6207` | 2.5 PNG render channel | under `ee4b4f8`; gains "rendered after every advance, unasked" via `70ee75e`; the legibility read still owed; re-waved to `wave:6` |
| `6d4ca8a` | no verb installs a MinifiedThing | absorbs `c621849` and the verb half of `4c12e5d`; the count is the screen's |
| `1adc737` | 3.3 Build verbs + place-layout | two notes: band automatically above the 600 cap (T10); the dry-run ghost is a contract on its reply |
| `664e9b9` | 4.3 M1 thrive-20-days run | the next run plays on the cockpit; the joiner rule becomes chore 5; the contract's rules are dispositioned in `COCKPIT.md`; the post-mortem reports the root's measurements |
| `01f0b85` | Muster | wave 6 added with the seven ids, the order, and the three that matter |

### Noted (17)

| id | was | the note |
|---|---|---|
| `e440676` | refusals and faults both ok:false | LAND IT — first on the audit's list; built, never benched |
| `29824e4` | dev suite needs an arming gate | LAND IT — the control Dorian named; provenance tags dev verbs |
| `e1a9542` | pawn-fixture with no args wounds a pawn | LAND IT with `29824e4`; the empty-args case of `27bf321` rule 2 |
| `65e7cf9` | a dead rwa client leaves the game running | stays the mod half; the client half is `70ee75e`; this run's orphan evidence added |
| `3d53df2` | transcript step order is claim order | stays; `70ee75e`'s wait records completion order |
| `aa4391b` | work_coverage cannot see an outranked doctor | `7f0e245` #2's "exclude the patient" lands here; three deaths this run |
| `61794cd` | BloodLoss cut by the hediff cap | the cap half generalises into `27bf321` rule 3; the bleed clock stays |
| `15842b9` | bills and food count unreachable things | the screen's FOOD and POWER gauges depend on this basis |
| `855117a` | mine designations cannot be aimed | the map's alphabet (`ee4b4f8`) is where ore-vs-rock lands; stays concrete |
| `8847053` | baseviz view prints upside down | stays concrete; `ee4b4f8` makes north-up an acceptance bullet |
| `36c03c9` | BRAINSTORM: pickup / equip / drop engine | this run's evidence added (a second wear order cancels the first; auto_arm re-picks) |
| `253c694` | forced orders collide silently | reporting half cited in `27bf321`; completion half is a condition over screen fields |
| `7e8c969` | a clamped trade line reports success | clamp half cited in `27bf321`; the silver-row arithmetic stays |
| `5697725` | landmark README form refused | stays; the hyphen/underscore class is `27bf321` rule 2 |
| `9c68756` | nothing re-checks a designation | candidate light on the screen; otherwise untouched by this run |
| `2d9a1da` | colony sampling: rates | trends is a gauge on every screen, as its own warning asked |
| `3275f0c` | no verb for ideology roles | labelled (it had none): `mod:autorimmer type:enhancement priority:p2 state:backlog`; otherwise untouched |

### Untouched (37) — this run says nothing about them

In progress on someone's branch, appears done but not closed, a spec for work never
built, or a finding from an earlier run this one did not exercise. No comment was
added; closing is the owner's call.

| id | was | why untouched |
|---|---|---|
| `d9d6c12` | a bill asleep | in progress (`state:doing`); its output is a light on the screen |
| `f9dadc7` | a blueprint that never completes | in progress; the stall clock shipped (`5170677`); feeds the construction gauge |
| `40ed42f` | doctor coverage computed in the mod | in progress; it is chore 6 of `ffef0d7` as written |
| `ae78ecc` | no observer reads Pawn_RecordsTracker | a spec for M1 grading; a candidate gauge source, not exercised |
| `261f2e9` | no verb sets a temperature target | in progress; `temp-set` exists in the verb list |
| `d16a463` | observers flush the region updater | a hazard-class bug; adjacent to T7, not exercised |
| `9b179ef` | work_coverage row order | in progress |
| `47547ca` | armour rating unreadable | the contract already names it as a known blind spot; nothing new |
| `a1644d6` | a layout that never encloses | in progress; its enclosure roll-up is what T7's "rooms omit unenclosed" wants |
| `acee526` | 1.9 exact-or-refuse placement | ongoing spec; the dry-run ghost in `ee4b4f8` assumes it |
| `e08c3e5` | preflight ignores the skill gate | in progress; shipped (`5170677`) |
| `4950f14` | construction.gaps serializes a type name | a real serializer bug; the contract lists it as known-blind |
| `8e5db24` | manhunter chance unpublished | filed from this run by the agent; nothing to add |
| `daa269a` | owners_total 0 | in progress |
| `039e359` | 1.6 four unreached branches | test coverage |
| `bac4eba` | 7x7 module grid | design spec, not exercised |
| `95dbdfc` | IR per-element stuff channel | filed from this run's design work; nothing further |
| `927be4f` | two age vocabularies | appears done (`3b5c431`) |
| `1de2fbe` | selftest fails from `rwa/` | client hygiene |
| `e1c072e` | prisoner capture — LATER | deliberately later; the finish-off knob in chore 1 reads "is there a prison" |
| `826d4bf` | no verb uses a targetable item | not exercised |
| `1d381be` | designate reach: unpathable inside the area | not exercised (Dilly's case was `b7359fa`'s) |
| `ff1f0b9` | dev:add-gene | fixture tooling |
| `00a1be7` | combat-report | not exercised; could feed the screen's game row for fights |
| `35a84f6` | dialog-accept cannot name the colony | not exercised |
| `8fbb09a` | 5.2 Factions/Guests suites | far spec |
| `1113019` | until-condition unbounded | appears done (60,000 default and the refusal shipped, session 21) |
| `f794bfc` | accept suite 0.7c | test tooling |
| `33bc796` | prioritize says what, never why | not exercised |
| `58794e4` | work-cover dry run coverage_after | in progress |
| `6fc75e3` | work-cover dry run says journal closed | in progress |
| `5cb1f9f` | naming dialog wedges the run | appears done (`a458c5f`, built `2917156`) |
| `7c83d6c` | 5.1 rwtest runner | far spec |
| `a30c807` | accept dry-run crashes | test tooling |
| `eef837a` | butcher bill filter null | in progress; chore 2 depends on it |
| `f08dfc4` | repeated refusal | appears done (`8426ba1`, built `be8db77`) |
| `be75bc4` | trade-set two lines, one Tradeable | not exercised |

## `seekandkill` — 8

| id | was | verdict |
|---|---|---|
| `a6b1aa0` | Dispatcher.InContact NRE on a dormant cluster | **noted**: LAND IT NOW; labelled `p0 next type:bug` (it had none) |
| `000987b` | toggle/stance/autocast pruned at PostLoadInit | untouched — labelled `state:done`, still open; closing is the owner's call |
| `7ac5f77` | bounding reposition | untouched |
| `95bd2df` | fighting withdrawal | untouched |
| `e873033` | predictive seating | untouched |
| `23a453f` | unified combat utility | untouched |
| `dca6838` | detached pawns skipped by AssignTargets | untouched |
| `6545a67` | density gate | untouched |

## Themes to roots, for cross-checking

| audit theme | lands in |
|---|---|
| T0 truthful field nobody reads | `975973e` (the screen is the reply), `27bf321` rule 2 |
| T1 ok:true over an inner false | `27bf321` rule 1 |
| T2 argument names guessed | `27bf321` rules 2 and 4 |
| T3 caps that do not look like caps; the rollup `at` | `27bf321` rule 3; `975973e` gauges from a full count |
| T4 the journal gate defeated | `975973e` the answer gate |
| T5 no queue, no in-flight query | `70ee75e` |
| T6 orphans; status untraced | `70ee75e` |
| T7 instruments measuring the adjacent thing | `aa4391b`, `15842b9`, `40ed42f` as they are; hostile filter and reachability into `975973e` gauges |
| T8 verbs that write more levers than passed | `27bf321` rule 2 (`posture` requires pawns) |
| T9 no plural form | `27bf321` rule 5 |
| T10 the 600 cap | `1adc737` note |
| T11 disciplines decay | `ffef0d7`, `975973e` gauges; measured by `b4adee2` |
| T12 goals graded against absent verbs | `975973e` GOALS gauge |
| T13 the god-hand has no provenance | `827c1bf`; `29824e4` and `e1a9542` as they are |
| T14 the co-op with one player in the record | `827c1bf` |
| T15 handover discards tool knowledge | `27bf321` rule 4; `d2e1229` rewrite |
| T16 `RWA_RUN` fallback | `70ee75e` |
| T17 refusals and faults alike | `e440676`, land it |
| T18 nothing filed | out of scope for the cockpit; the audit README's git-bug hazards stand |
| T19 the seekandkill NRE | `a6b1aa0`, land it |
| Dorian's turret range and windmill, in no issue | `ee4b4f8` |
