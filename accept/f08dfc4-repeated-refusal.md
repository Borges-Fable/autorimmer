# f08dfc4 — `repeated` on an envelope, by hand

The mod does not refuse and does not rate-limit; it publishes a count from the
third consecutive identical refusal onward. Run in order on the agent bench.

```bash
REP='.repeated | if . == null then "absent" else "\(.count)x \(.code) ticks=\(.ticks)" end'
```

1. `rwa build --def DeepDrill --at 100,100 --stuff Steel --json | jq -r "$REP"`
   → `absent`. First refusal; `error.detail` reads `'DeepDrill' is not made
   from stuff`.
2. Same call again → `absent`.
3. Same call a **third** time → `3x bad-args ticks=0`. The field appears; the
   clock has not moved, so `ticks` is 0 and the detail says so in words.
4. Same call a **fourth** time → `4x bad-args ticks=0`.
5. `rwa build --def DeepDrill --at 101,101 --stuff Steel --json | jq -r "$REP"`
   → `absent`. **One changed argument resets it.**
6. Repeat step 1 three times, then `rwa build --def Wall --at <buildable> --stuff WoodLog --json | jq .ok`
   → `true`, then repeat step 1 once → `absent`. **A success of the same verb
   resets it.**
7. `rwa advance --ticks 2000 --json | jq .ok` → `true`; then send
   `rwa advance --ticks 2000` three times without a `journal` in between and
   read the third: `3x unread-journal ticks=0` — and confirm the tick in
   `.state.tick` is the same on all three. That is the 60-advance wedge of run
   m1-20260901 caught on its third turn instead of its sixtieth.
8. `rwa journal --json | jq .ok` → `true`, then `rwa advance --ticks 2000 --json | jq -r "$REP"`
   → `absent` and `.ok` is `true`.
9. `rwa ping --json | jq 'has("repeated")'` → `false`. A healthy call is
   unchanged; no consumer sees a new field until the wedge exists.
10. `python3 cockpit/cockpit <run>` on a step from 4 → a `repeated` section at
    the TOP of the envelope fold, in the warning colour, above `error.code`.

## Offline, because it needs no bench

`RefusalStreak.cs` is pure logic over a dictionary. 18 checks — threshold,
reset on success / changed args / changed code, interleaved turns, key-order
normalisation, nested args, `ticks` from the first of the streak, null args,
`Clear()` — were run against a **verbatim copy** of the shipped file with stubs
for the three symbols it touches (`Result`, `Runtime.GameState`, `MiniJson.N`),
the method commit `4e44116` used for `VerbArgs`. 18/18. Not a substitute for
the bench list above: nothing has been dispatched through a live poller.
