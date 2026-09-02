# e440676 — `error.class` on every failure, by hand

Run in order on the agent bench. Every line is one `rwa` call and one thing to
read. `CLS` is the whole check: `jq -r '.error.code + " " + .error.class'`.

```bash
CLS='.error.code + " " + .error.class'
```

1. `rwa ping --json | jq .ok` → `true`. The bench answers; nothing below is
   about a dead bench.
2. `rwa nosuchverb --json | jq -r "$CLS"` → `unknown-op refused`.
3. `rwa ping --echo:num 5 --json | jq -r "$CLS"` → `bad-args refused`
   (`echo` must be a string).
4. `rwa advance --ticks 20000 --json >/tmp/adv.json & sleep 2; rwa digest --json | jq -r "$CLS"; wait`
   → `busy flow`. **The one `flow` reachable by hand**; it is the 200 of run
   m1-20260901's 691.
5. `rwa advance --ticks 2000 --json | jq .ok` → `true`, then
   `rwa advance --ticks 2000 --json | jq -r "$CLS"` → `unread-journal refused`.
   Clear it with `rwa journal --json | jq .ok`.
6. `RWA_ROOT=/nonexistent rwa ping --json | jq -r "$CLS"` → `rwa-no-root client`.
   Needs no bench; proves the client stamps its own side.
7. `rwa ping --json | jq 'has("error")'` → `false`. A success carries no
   `error` block and therefore no class.
8. The run tally, which is the point of the issue —
   `cat transcripts/<run>/*/result.json | jq -r 'select(.ok==false) | .error.class' | sort | uniq -c`
   → every line names a class; **no nulls**. On m1-20260901's shape this reads
   `refused` and `flow` where "691 errors" used to.
9. `python3 cockpit/cockpit <run>` and step to a `busy` or `unread-journal`
   step → the `result` line reads `flow · busy` / `refused · unread-journal`
   and is **not** in the warning colour; an `exception` step still is.
10. `python3 accept/4.2-play-loop.py RUNS/<run> --transcript 'transcripts/<run>*' | grep advance-discipline`
    → `INFO … unread-journal refusal(s) OBEYED`, and a FAIL only where the loop
    asked for time again with nothing read.

## The source-level half

Two invariants no bench call can show, because they are about what CANNOT be
written:

- `Result.Fail` takes an `ErrCode` and there is no `string` overload, so a code
  cannot reach an envelope without a class. `grep -n 'static Result Fail'
  Source/AutoRimmer/Runtime.cs` — one signature.
- Every code in the mod, with its class, is one grep and there is no second
  list to compare it against:
  `grep -rn 'ErrCode\.\(Refused\|Flow\|Fault\)(' Source/AutoRimmer/`
  → 11 declarations, in `Runtime.Err` and `TimeDriver`.
