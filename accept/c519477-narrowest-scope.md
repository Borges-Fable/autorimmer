# c519477 — the narrowest scope, and the levers a bundle actually wrote

Run on the agent bench (`_RimWorld-Agent`). **Nothing below has been run**: the
code builds clean (`dotnet build -c Release`, 0/0) and the source-level half
passes offline, but no `error.class`, no refusal and no `levers` array from this
change has ever come back from a live poller.

Two shapes are being proved, and they are the two halves of the audit's theme
T8: **a verb that would widen refuses instead**, and **a bundle says what it
added**. `b1b3060`'s three-lever `posture` is NOT re-opened — the bundle stays.

Most of it is already automated inside the posture suite, because that is where
the fixture (an area, a roster, a known posture) already lives:

```bash
./accept/b1b3060-posture.py --phase 0 --phase 2
```

- **0.9f–0.9k** — a write-mode `posture` with no `pawns` is refused, `bad-args`
  / `error.class: refused`, naming `pawns`, offering `pawns:"colonists"`,
  citing `PawnColumnWorker_AllowedArea`, and saying nothing was written.
- **0.9l** — the same call *with* the scope declared is accepted. The
  capability was never missing; the default was.
- **0.9m–0.9o** — a MISSPELLED scope key (`pawnz`) is refused pre-mutation.
- **2.1c–2.1c4** — `levers_asked` is the request, `levers` is the fact, and on
  a call that named all three they agree with nothing unasked.
- **2.7a–2.7i** — the T8 shape itself: a ONE-lever call still succeeds, and its
  result and its journal row both carry `levers`, `levers_asked`,
  `levers_unasked` and the `also_changed` sentence.

## The four other verbs, by hand

Five verbs mutate differently when a key is DROPPED rather than passed, and all
five now refuse a stray key before mutating. `posture` is covered above; these
four are one `rwa` call each. Every one must come back `ok:false`,
`error.code: bad-args`, `error.class: refused`, and a detail that names the
stray key **and** says nothing was done.

| # | call | expect |
|---|---|---|
| 1 | `rwa build --def Wall --pos:json '[10,10]' --dryrun:bool true --json` | refused; detail names `dryrun`, suggests `dry_run`, and says *"Nothing was placed"*. **Then `rwa map-dump` / `construction`: no blueprint at 10,10.** That is the whole finding — ten of these placed real blueprints in openrun-20260902 |
| 2 | `rwa build --def Wall --pos:json '[10,10]' --dry-run:bool true --json` | **ok:true, `data.dry_run: true`, `data.placed: false`.** The HYPHENATED spelling is rewritten at the poller, not refused — and this is the call the run actually sent |
| 3 | `rwa alert-mute --ids:json '["Alert_LowFood"]' --releas:bool true --json` | refused; detail names `releas` and says a dropped `release` INVERTS the call. Then `rwa alert-mute --json` → `Alert_LowFood` is **not** muted |
| 4 | `rwa carry --pawn <id> --target <downed id> --to:json '[98,99]' --json` | refused; detail names `to` and says there is no destination argument. `rwa journal --types:json '["action"]'` shows **no** `carry` row |
| 5 | `rwa trade-start --trader <id> --negotiater <id> --json` | refused; detail names `negotiater`, suggests `negotiator`; `rwa trade-status` shows no open session |

Cross-check on 1 and 3 that the *correct* spelling still works, so the refusal
is a spelling gate and not a broken verb:
`rwa build --def Wall --pos:json '[10,10]' --dry_run:bool true` → `ok:true`,
`data.dry_run:true`, `data.placed:false`.

## The source-level half

Four invariants no bench call can show, because they are about what cannot be
written. All offline:

```bash
./accept/s13-mod-surface.py --selftest      # phase 9; no bench, no game
```

- **9.11a–e** — each of the five verbs declares every argument it reads at its
  own call site. The assertion is a SUPERSET on purpose: a key the verb reads
  but does not declare would refuse a *legitimate* call, and that is the only
  drift here that costs a run.
- **9.12a** — no verb anywhere in the tree reads a hyphenated key, which is the
  premise the poller rewrite rests on. Re-derived over every `.cs` file rather
  than asserted in a comment.
- **9.12b/c** — `ScanInbox` calls `Underscore`, and a caller who sent BOTH
  spellings is left alone so a self-contradicting call is reported rather than
  silently resolved.
- `posture`'s `levers` is no longer a constant:
  `grep -n 'levers' Source/AutoRimmer/SeekVerbs.cs` — it is built from `wrote`,
  which is unioned from the per-pawn `applied` rows, and the same three arrays
  go to the envelope and to the journal payload.

## What this does NOT prove

That the refusal fires before anything is written *in the game* — the ordering
is source-level (the guard is the first statement, the scope check is above the
pawn loop) and the bench check for it is the "no blueprint at 10,10" half of
row 1 and the "no `carry` row" half of row 4. Run those two; they are the ones
that would have caught this.

And it proves nothing about the CHORE half of `c519477`'s reasoning — that a
chore may act unasked while a verb may not. Nothing here makes a chore.
