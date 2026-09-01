# 1113019 — the runaway advance, as the issue recorded it

**Nothing in this directory was captured by a runner.** `00-runaway-advance.json`
is a WRAPPER, not an envelope: its `envelope` object holds the fields git-bug
1113019 quotes verbatim — the body (#0) for the result line and comment #2 for
the `until` sub-object — and nothing else. The run itself was never banked, so
every other key an advance publishes is simply missing from the file rather than
absent from the bench.

It is kept because it is the only artifact of the defect, and because it is an
honest negative fixture for exactly one thing: **it cannot carry
`data.timeout_ticks` or `data.timeout_source`, because the code that publishes
them did not exist when it was produced.** `accept/1113019-until-bound.py
--selftest` uses it for that and for the absent-vs-null trap, and asserts nothing
about any key the issue did not quote.

What it shows, in one line: `ok: true`, `reason: "casualty"`,
`ticks_elapsed: 187541`, and an `until` block that says
`true_when_armed: true, saw_false: false, first_false_tick: null`. The mod
diagnosed the unreachable halt completely, published the diagnosis, and accepted
the unbounded advance anyway — which is why the fix is enforcement keyed on
`true_when_armed` and not a new observation.

The advance was stopped by `722c951`'s own-faction casualty halt, which had
shipped hours earlier the same session. Before that halt existed, this call had
no upper bound at all.
