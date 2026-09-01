# `7382bdd` on a bench — the colony-ender is gone

Bench `_RimWorld-Agent` session `20260901T212651`, `--quicktest`, assembly
`1.0.0+060c379` (the `spec/arg-names` merge; `Build:` commit `043fa46`).
Envelopes in this directory. Run by the orchestrator; workers launch nothing.

## The check the issue was promoted for

    rwa journal-selftest --kind save

    ok: false   code: bad-args
    detail: unknown arg 'kind' — journal-selftest accepts 'steps',
            'letter_delay_ticks', 'save_name', 'alert_count',
            'alert_critical_last', 'colonist_target', 'power_lamps',
            'power_fuel', 'power_stored', 'power_generator',
            'timeout_ticks_letter', 'letter_tag', 'drop_fixture_letters',
            'error_delay_ticks', 'error_repeats', 'error_text' and
            'main_menu_delay_ticks'. Refused BEFORE any step ran, so nothing
            was mutated.

**And the colony proves it.** Colonist roster read immediately before and
immediately after, `00-pawns-before.json` / `07-pawns-after.json`:

| | 301 Siqueira | 304 Maskinnen | 307 Simpson |
|---|---|---|---|
| before | up, no mental state | up, no mental state | up, no mental state |
| after | up, no mental state | up, no mental state | up, no mental state |

Identical. On 2026-09-01 (`20260901T195344`) that same call returned `ok:true`
and ran its default list — `letter, message, error, downed, break` — which
downed all three colonists and started a berserk. Four calls landed before
anyone noticed.

## The general form, report-only, on both kinds of verb

A pure observer:

    rwa digest --wibble 3        ->  ok: true
    ignored_args: {"keys":["wibble"], "read":["colonists_cap","since"],
      "detail":"unknown arg 'wibble' — digest read 'colonists_cap' and 'since'
       on this call. It was DROPPED and the verb RAN ANYWAY, so this result may
       have come from a default rather than from what you asked for."}

A mutator, which additionally carries the journal seqs it wrote:

    rwa dev:spawn-thing --def WoodLog --pos pawn:301 --mode direct --wibble 3
    ok: true, placed 1
    ignored_args.read: buildable, count, def, faction, forbid, force, minified,
                       mode, pos, quality, rot, stockpile, stuff

That `read` list is the caller's fastest route to the right spelling and it
exists for all 120 verbs with no declaration anywhere.

## The controls, which are what make the above mean anything

- A CLEAN `digest` publishes **no** `ignored_args` key at all.
- `journal-selftest --steps message --steps letter` still runs and returns
  `executed: ["message","letter"]` — the guard refuses a stray key, not a
  legitimate call.
- The near-miss that shipped in session 15 still fires: `dev:spawn-thing --at`
  is refused with "did you mean 'pos'?".

## Two traps I hit myself, banked so the next run does not

1. **`status.json` PERSISTS ON DISK ACROSS BENCH RESTARTS, and `health.state`
   ALONE DOES NOT SAVE YOU.** A waiter keyed on `gameLoaded: true` matched the
   *previous* session's file and returned immediately, so a whole sweep came
   back `no-active-game`. **This note first said "key the wait on
   `health.state == "ok"`, which is the verdict that consults `age_s`" — and
   that is WRONG, measured twice more after it was written.** `age_s` only goes
   stale after 10s, so for the first ten seconds after a `pkill` the dead
   session's file still reads `state: "ok"` with a high tick, and a waiter
   passes instantly. The correct wait needs the **session id to CHANGE**:
   capture `status.sid` before the restart, then poll for a DIFFERENT `sid`
   with `gameLoaded: true` and `health.state == "ok"`. Pinning the expected new
   sid explicitly is better still. Three false positives in one session came
   from getting this wrong in three different ways.
2. **`rwa` only builds a list from a REPEATED flag** (`rwa` line 265: a value
   becomes a list on the second occurrence of the same key). `--steps message`
   sends the string `"message"` and earns a clean `arg 'steps' must be an array
   of strings`. Suites that write JSON directly are unaffected; this is a CLI
   property only.
