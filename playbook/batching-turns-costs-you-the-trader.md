---
name: batching-turns-costs-you-the-trader
trigger: driving several advances in one unattended batch; any loop that dismisses letters by class
severity: Important
confidence: observed-at-bench
source: run m1-20260901 day 14 — a bulk goods trader arrived, was dismissed as a PositiveEvent, and the run advanced 45,000 ticks past the whole visit
---

**What.** A turn driver that batches N advances must stop on **opportunities**,
not only on threats. A letter is not noise because it is filed under
`PositiveEvent`.

**What it cost.** `m1-20260901` ran a day-loop that broke its batch on
`ThreatBig`/`ThreatSmall`, casualties and red errors — and dismissed everything
else so the next advance would not wedge on a force-pausing letter. On day 14 it
printed, dismissed, and rode past:

    letters=Bulk goods trader from East Galer[PositiveEvent]

At that moment the colony had **800 silver unspent**, `resources.steel` at 0 with
every remaining stack forbidden or out of area, and `food_days` at 3.9 against a
required 6. A bulk goods trader sells exactly steel and food. By the time the
batch ended, `pawns {filter:"all"}` reported `by_class: {colonist:3, animal:1,
wildlife:47}` — the caravan had come and gone. The run then spent nine more days
short of both.

**Why the batch is the culprit rather than the dismissal.** Dismissing a letter
is correct and necessary: until a force-pausing letter is cleared, every
subsequent advance halts at 0 ticks and the run *looks* alive while wedged
(`PLAY-LOOP.md` §Halts, the dialog rule). The defect is dismissing it inside a
loop that then keeps going. A single-turn loop would have printed the same line
and handed control back to a reader who could act.

**It happened three more times before the lesson was general enough.** Two
quests expired unread (`Alert_QuestExpiresSoon` fired twice), and on day 31 the
driver dismissed **"Wanderer joins: Serenity"** — an `AcceptJoiner` letter — and
**rejected a free colonist**, on a three-person roster where labour was the
binding constraint on everything. Filing it under `trader|caravan` would not have
caught that one. The real predicate is not the subject; it is **whether the letter
carries a choice**.

`interactions` publishes it directly. Every letter has an `options` array, and an
inert letter's options are only ever closes:

    "options":[{"label":"Close","closes":true},
               {"label":"Jump to location","closes":true}]

So a **decision letter is any letter with an option whose label is not one of
`Close` / `OK` / `Dismiss` / `Jump to location`** — one jq expression, no
per-`def` allow-list to keep up to date:

    [.data.letters[]? | select([.options[]? |
        select(.label|test("^(Close|OK|Dismiss|Jump to location)$")|not)
      ]|length>0) | .label]

**How to apply.** Classify letters by **whether they carry a decision, or are
actionable and time-boxed** — never by their `def`:

- **Stop the batch** on: any `ThreatBig`/`ThreatSmall`; any casualty halt; any
  letter with a non-inert option (the jq above — this is the one that catches
  joiners, and it is the check to write first); and any letter whose label
  matches a trade or caravan (`trader|caravan|Trade`), which catches the
  time-boxed visits whose letters happen to be inert.
- **Also worth stopping on**, from the same run: a quest with an expiry
  (`Alert_QuestExpiresSoon` fired twice and both quests expired unread), and a
  trade *inspiration*, which is a multiplier on a visit that has not happened yet.
- **Print every letter before dismissing it**, with its `def`, so the transcript
  shows what was ridden past even when the batch was right to continue.

The general rule, which is the same one `read-every-return-or-lose-a-colonist`
makes about reads: **a batch is a decision to not look, and it must be bounded by
the things that need looking at.** Enumerate them on the way in, not after.

**Retire when.** The mod halts on trade opportunity the way it halts on threat —
a `--until.trader` matcher, or a `letter` halt class for time-boxed events. Then
the loop stops because the game stopped it, which is the shape every other halt
in `PLAY-LOOP.md` already has.
