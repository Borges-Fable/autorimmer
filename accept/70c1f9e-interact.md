# Acceptance — `interact` / `interact-options` (git-bug 70c1f9e)

The worker that wrote this may never launch RimWorld, so the acceptance is in
envelope form for the orchestrator to drive. Every envelope is
`{"op":…,"args":…}`; with `rwa` on the box each is
`rwa <op> --args-json '<json>'`.

**Every `advance` below needs the escapes** (git-bug `722c951`), and this
surface needs a fourth one the `use` acceptance did not:

```json
"unread_ok": "accept/70c1f9e-interact.md: fixture, not a play loop",
"through_casualties": "accept/70c1f9e-interact.md: fixture, not a play loop",
"through_news": "accept/70c1f9e-interact.md: the analysis letter is expected"
```

`through_news` is the important one. `CompAnalyzable.OnAnalyzed` ends in
`Find.LetterStack.ReceiveLetter` — a letter, not a modal — so nothing needs
dismissing, but `advance` halts on unread news and the halt lands **during** the
advance that finishes the analysis. `interact`'s own reply says so in
`data.advance_halt` before you ever advance.

---

## Fixture

| needs | why | stage it with |
|---|---|---|
| a **mechanitor** colonist, awake, undrafted, capable of manipulation and Intellectual | clause 17 (`requiresMechanitor`) and clause 14 (`ResearchSpeed` disabled) | the live run already has one |
| a **research bench**, powered, reachable, unreserved | clause 15 — the chips do **not** set `canStudyInPlace`, so the item is HAULED to a bench | any colony that built one |
| a `SignalChip` on the ground, **not forbidden** | the whole point; clause 8 refuses a forbidden one for an undrafted pawn | the live run has one bought from Dad's catalog. `unforbid {thing:<id>}` clears it, or draft the pawn |
| `BasicMechtech` finished | `StandardMechtech`'s prerequisite; otherwise `research-set` blocks on `prereq`, not `analysis` | the live run already has it |
| a **non-mechanitor** colonist | phase 2's `mechanitor` refusal | any colony |
| a `Turret_FoamTurret` (optional) | the only **cooldown** case reachable on the base route — `cooldownTicks: 3600`. The chips ship `cooldownTicks: 0`, so `on-cooldown` **cannot** fire on one | `dev:spawn-thing {def:"Turret_FoamTurret"}` |

Thing ids come from `things` / `nearest`. Nothing here needs a mod.

---

## Phase 0 — preflight and the shape contract

`eq(…, None)` passes on an *absent* key, so assert PRESENCE, not truthiness.
Every `interact-options` row carries `can_interact`, `gate` and `reason` even
with no pawn (they are seeded null on purpose) — assert the keys exist.

1. `{"op":"status"}` → `ok:true`, `data.gameLoaded:true`, **no** `data.forcePause`.
2. `{"op":"status"}` → `data.verbs` contains **both** `interact` and
   `interact-options`. If not, the DLL is stale — rebuild.
3. `{"op":"journal","args":{"limit":1}}` → record `data.last_seq` as **seq0**.
4. `{"op":"interact-options","args":{"cap":5}}` → `ok:true` and these keys
   PRESENT: `data.options`, `data.total`, `data.more`, `data.interactable`
   (null with no pawn), `data.note`. Each row has `thing`, `def`, `label`,
   `pos`, `comp`, `route`, `job_string`, `activate_label`, `active`,
   `on_cooldown`, `cooldown_ticks_remaining`, `ticks_to_activate`,
   `requires_power`, `activate_stat`, `hide_interaction`, `targetable`,
   `multi_select`, `target_params`, `target_note`, `can_interact`, `gate`,
   `reason`.
5. `{"op":"interact-options","args":{"pawn":<MECHANITOR>,"thing":<CHIP>}}` →
   the row additionally has `refusals` and `optional_items`, an `analysis`
   block, and `advance_halt`. Each refusal row has `gate`, `reason`,
   **`reason_from`** and **`cite`**.

---

## Phase 1 — the argument surface, and the issue's own wrong assumption

6. `{"op":"interact","args":{"pawn":<P>,"thing":<CHIP>,"target":<P>}}` →
   `ok:false`, `error` names `target` and says the target IS the activating
   pawn. **Nothing was ordered** — `journal {limit:5}` shows no new `action`
   row. This is the pre-mutation guard, and it is here because the issue that
   asked for this verb specified a `target` argument that cannot exist.
7. `{"op":"interact","args":{"thing":<CHIP>}}` → `ok:false`, missing `pawn`.
8. `{"op":"interact","args":{"pawn":<P>,"thing":<a rock>}}` → `rejected[0].gate`
   is `not-interactable`.
9. `{"op":"interact","args":{"pawn":<P>,"thing":<a Neurotrainer>}}` →
   `not-interactable`, and the reason **names `use`** (the onward pointer).
   Symmetrically, `{"op":"use","args":{"pawn":<P>,"thing":<CHIP>}}` →
   `not-usable`, and the reason **names `interact`**.

---

## Phase 2 — the gates, each with its own name

Run these as `interact-options {pawn, thing:<CHIP>}` first (it lists EVERY
clause that refuses, not just the first) and confirm the same gate comes back
from `interact` (which stops where the game stops).

10. **`mechanitor`** — `{"op":"interact","args":{"pawn":<NON-MECHANITOR>,
    "thing":<CHIP>}}` → `rejected[0].gate:"mechanitor"`,
    `reason_from:"game"`, `cite` ends `CompAnalyzableUnlockResearch.CanInteract`.
11. **`forbidden`** — `forbid {thing:<CHIP>}`, then `interact` with an
    **undrafted** mechanitor → `gate:"forbidden"`, and the refusal row carries
    `drafted:false`, `ignore_forbidden:false` and an `escape` string.
12. Draft the same pawn and repeat → the forbidden clause is **gone** (the
    escape is real). Undraft and `unforbid {thing:<CHIP>}` before continuing.
13. **`downed`** — a downed colonist as `pawn` → `gate:"downed"`, not
    `manipulation` and not `no-path`. (Clause 9 fires before 11 and 12.)
14. **`no-research-bench`** — cut the research bench's power (`flick`) or
    reserve it, then `interact-options {pawn, thing:<CHIP>}` →
    `gate:"no-research-bench"`, and the refusal's `detail` says the item is
    HAULED to the bench. Restore power.
15. **`already-analyzed`** — after phase 4 succeeds, re-run `interact` on the
    same chip → `gate:"already-analyzed"` with `times_done` and `required` on
    the row. (`destroyedOnAnalyzed` is false on the chips, so the chip is still
    on the map — this is reachable.)
16. **`on-cooldown`** (optional, needs the foam turret) — `interact` it once,
    advance ~60 ticks, `interact-options {pawn, thing:<turret>}` →
    `gate:"on-cooldown"`, `cooldown_ticks_remaining` between 1 and 3600, and the
    reason carries the game's own duration suffix. **This cannot be tested on a
    chip**: `CompProperties_CompAnalyzableUnlockResearch` leaves `cooldownTicks`
    at 0, so `StartCooldown` sets 0 and `OnCooldown` is never true.
17. **`no-power`** — no vanilla `CompProperties_Interactable` sets
    `requiresPower: true` (`GravshipShieldGenerator` sets it explicitly
    **false**), so this gate is **unreachable on a vanilla bench**. Assert the
    key instead: every `interact-options` row carries `requires_power`, and it
    is `false` on the chips. Flagged rather than faked.
18. **`unsupported-route`** — needs Anomaly content on the map (a golden cube,
    an obelisk, a nociosphere) or Odyssey's `CerebrexCore`. If any is present:
    `interact-options {thing:<it>}` → `can_interact:false`,
    `gate:"unsupported-route"`, and the reason NAMES the class and why (a
    `Dialog_MessageBox`, a `Find.Targeter` session, or a Shard haul). If none is
    present, skip — and note that `interact` on the void obelisk would answer
    `ambiguous-interactable` first, because it carries two comps.

---

## Phase 3 — `checkOptionalItems` is its own concept

19. `{"op":"interact-options","args":{"pawn":<P>,"thing":<CHIP>}}` →
    `optional_items.applies:false`, `optional_items.checked_with:true`, and
    `optional_items.note` explains that `CompInteractable.Interact` re-asks with
    `false` in the job's LAST toil, after
    `pawn.carryTracker.DestroyCarriedThing()`. `detail` says this comp does not
    read the parameter at all.
20. With a golden cube or a deactivatable obelisk on the map (Anomaly only):
    `optional_items.applies:true`, and BOTH
    `refusals_with_optional_items` and `refusals_without_optional_items` are
    present, with `differs` saying whether the two passes disagree. If no
    Anomaly content is on this bench, record 20 as **not reachable here** —
    the `applies:false` half of 19 is the assertion that ships.

---

## Phase 4 — THE CHAIN THAT MATTERS

This is the acceptance. Everything above is the safety rail around it.

21. `{"op":"research-set","args":{"project":"StandardMechtech"}}` →
    `data.ok:false`, `blocked_by:"analysis"`,
    `reason:"requires more analysed items"`. **Record this**; it is the
    before-state the whole issue was filed on.
22. Buy a `SignalChip` if the run does not already have one on the map
    (`nepo-order` / the trade verbs). `things {def:"SignalChip"}` → its id.
    Confirm it is not forbidden — `unforbid {thing:<CHIP>}` if it is.
23. `{"op":"interact-options","args":{"pawn":<MECHANITOR>,"thing":<CHIP>}}` →
    `can_interact:true`, `refusals:[]`, `route:"analyze-item"`,
    `analysis.satisfied:false`, `analysis.required:1`, `analysis.times_done:0`,
    `analysis.requires_mechanitor:true`, and
    `analysis.research_unlocked` contains `"StandardMechtech"`.
24. `{"op":"interact","args":{"pawn":<MECHANITOR>,"thing":<CHIP>}}` →
    `accepted` has one row, `counts.accepted:1`, `data.job:"AnalyzeItem"`,
    `data.research_bench` names the bench it will be hauled to,
    `data.route:"analyze-item"`, `data.dialogs_skipped` says **none**, and
    `data.advance_halt` warns about the letter. `action.journal_seq` > seq0.
25. `{"op":"journal","args":{"types":["action"],"limit":3}}` → the newest row is
    `verb:"interact"` with `route:"analyze-item"`, `job:"AnalyzeItem"`,
    `comp:"CompAnalyzableUnlockResearch"` and a `verdict` of
    `accepted:1, rejected:0`.
26. `{"op":"advance","args":{"ticks":3000,"through_news":"…","unread_ok":"…",
    "through_casualties":"…"}}` — repeat until the pawn has walked to the chip,
    carried it to the bench, and run the wait toil. The analysis is
    `ceil(0.5 * 2500) = 1250` ticks scaled by the pawn's `ResearchSpeed`, plus
    the two walks. Watch `pawn {id:<P>}` → `job_def` goes `AnalyzeItem`.
27. The advance that COMPLETES it halts on news (or delivers the letter if
    `through_news` swallowed it): the letter is
    **"Signal chip studied: standard mechtech unlocked"**. `letter-read` to see
    it, `letter-dismiss` to clear it.
28. `{"op":"interact-options","args":{"pawn":<P>,"thing":<CHIP>}}` →
    `analysis.satisfied:true`, `analysis.times_done:1`, and
    `can_interact:false` with `gate:"already-analyzed"`.
29. **`{"op":"research-set","args":{"project":"StandardMechtech"}}` →
    `data.ok:true`.** No `blocked_by`. That is the issue closed.

---

## Phase 5 — the other two rungs (the run's real goal)

30. Repeat 22–29 with `PowerfocusChip` → `research-set {project:"HighMechtech"}`.
    Note `HighMechtech` ALSO needs `MultiAnalyzer` researched and built and a
    `HiTechResearchBench`, so `blocked_by` may move from `"analysis"` to
    something else rather than to `ok:true` — that is a correct pass for this
    verb, and the assertion is that `blocked_by` is **no longer `"analysis"`**.
31. Repeat with `NanostructuringChip` → `UltraMechtech`, same caveat.

---

## What this verb deliberately does not do

- **No `target` argument, ever.** `CompInteractable`'s target is the activating
  pawn. See DESIGN's decisions log for the source citations.
- **No route that opens UI.** Six vanilla classes override `OrderForceTarget`
  into a confirmation modal, a targeting cursor or an ingredient haul; all six
  are refused by name with the reason quoted from their own source.
- **No un-forbid before the gate.** Unlike `use`, whose chain has no forbidden
  clause at all. A forbidden chip and an undrafted pawn is a refusal a player
  would also get; `unforbid` or draft.
- **No `dev:` escape for analysis.** `CompAnalyzable.CompGetGizmosExtra` has a
  "DEV: Finish analysis" gizmo and `AnalysisManager.ForceCompleteAnalysisProgress`
  is public, so one is cheap — but it is not this issue and a player verb may
  not bypass a gate. Filed separately if the run wants it.
