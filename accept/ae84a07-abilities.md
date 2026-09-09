# Acceptance — `abilities` / `ability-cast` (git-bug `ae84a07`)

The worker that wrote this may never launch RimWorld, so this is the acceptance
in envelope form for the orchestrator to drive — by hand, by `rwa`, or by the
raw file protocol (`commands/<id>.json` → `results/<id>.json`).

Every envelope below is `{"op":…,"args":…}`; with python on the box each is
`rwa <op> --args-json '<json>'`.

**Every `advance` below needs the two per-call escapes** (git-bug `722c951`):

```json
"unread_ok": "accept/ae84a07-abilities.md: fixture, not a play loop",
"through_casualties": "accept/ae84a07-abilities.md: fixture, not a play loop"
```

---

## Fixture

| needs | why | stage it with |
|---|---|---|
| **Royalty active** | `Psycast.GetGizmos` yields nothing without it (`ModLister.RoyaltyInstalled`), and `Pawn_PsychicEntropyTracker` is not created | the `_RimWorld-Agent` profile already has it |
| a colonist with a **psylink** | `psylink-level` refuses every level>0 psycast at 0 | `dev:add-hediff {pawn, hediff:"PsychicAmplifier"}`, repeated for level. Phase 2 wants the caster at psylink **0** first, so do phase 2 BEFORE this |
| a colonist who **knows** ≥1 psycast | `not-known` otherwise. `dev:add-hediff` gives the psylink, not the ability | any psytrainer through `use`/`consume`, or a Royalty title reward. On the fingerkill bench, `Bloodbond` (level 5), `Fingerkill` (level 6) and `Voidpull` (level 6, cell-target) |
| a **prisoner or other pawn** to aim at | phases 5–7 | any |
| **psyfocus above 50%** for a level-5/6 cast | the `psyfocus-band` gate caps casting at level 2 below 25% and level 4 below 50%, *independently of the cost* | meditate, or accept the refusal as phase 4's evidence |
| the caster **DRAFTED** | `AbilityDef.disableGizmoWhileUndrafted` DEFAULTS TO TRUE, and a def that sets only `displayGizmoWhileUndrafted` shows the gizmo and disables it. All three fingerkill psycasts are in that state | `draft {pawns:[<id>]}` |

**Nothing here needs the fingerkill mod.** Every check is generic; the def names
in phases 5–7 are whatever psycasts the bench caster actually has, read out of
phase 1.

---

## Phase 0 — preflight and the shape contract

`eq(…, None)` passes on an *absent* key, which is how a suite reports green on a
verb that shipped nothing. These assert PRESENCE.

1. `{"op":"status"}` → `ok:true`, `data.gameLoaded:true`, **no** `data.forcePause`.
2. `{"op":"status"}` → `data.verbs` contains **both** `abilities` and
   `ability-cast`. If not, the DLL is stale — rebuild.
3. `{"op":"journal","args":{"limit":1}}` → record `data.last_seq` as **seq0**.

## Phase 1 — the read, and the psychic block nothing published before

4. `{"op":"abilities","args":{"pawn":<casterId>}}` → `ok:true`, and these keys
   are **PRESENT**: `data.casters[0].psychic.psylink_level`,
   `.psychic.max_psylink_level`, `.psychic.psyfocus`, `.psychic.psyfocus_band`,
   `.psychic.max_ability_level`, `.psychic.entropy`, `.psychic.max_entropy`,
   `.psychic.entropy_recovery_rate`, `.psychic.psychic_sensitivity`,
   `.psychic.queued.psyfocus_cost`, `.psychic.queued.entropy`,
   `.psychic.bands.psyfocus_band_floors`, `data.counts.abilities`,
   `data.counts.castable`, `data.gates.gizmo_layer`, `data.gates.target_layer`,
   `data.action.journal_seq` (which is **null** — the read mutates nothing).
5. Same call → each `data.casters[0].abilities[*]` carries `def`, `label`,
   `level`, `psyfocus_cost`, `entropy_gain`, `range`, `requires_los`,
   `cooldown_ticks_remaining`, `uses_charges`, `target_params`, `castable`,
   `gate`, `reason`, `baby_check`.
   `baby_check` must be `"cached"` or `"re-derived"`, **never** `"unavailable"`
   on a normal colonist — that is the guarded Class C route reporting itself.
6. `{"op":"journal","args":{"since":<seq0>}}` → **no new rows.** A read verb that
   journals has mutated something.
7. `{"op":"abilities","args":{"pawn":<casterId>,"cap":1}}` →
   `data.casters[0].abilities` has length 1 and `data.casters[0].more` > 0 while
   `data.casters[0].total` is unchanged from step 4.

## Phase 2 — no psylink refuses with a reason, not an NRE

Run this BEFORE granting the psylink, or on a second colonist who has none.

8. `{"op":"abilities","args":{"pawn":<noPsylinkId>}}` → for every row with
   `level > 0`: `castable:false` and `gate:"psylink-level"`, with a `reason`
   naming the required level. **Not** an exception, not `gate:null`.
9. `{"op":"ability-cast","args":{"pawn":<noPsylinkId>,"ability":"<any>","target":"pawn:<victimId>"}}`
   → `ok:true`, `data.counts.accepted:0`, `data.rejected[0].gate` is
   `"psylink-level"` or `"not-known"`. Never `code:"exception"`.

## Phase 3 — the undrafted gate, which every bench psycast hits

10. With the caster **undrafted**:
    `{"op":"abilities","args":{"pawn":<casterId>}}` → the psycast rows are
    `castable:false`, `gate:"undrafted"`, and each row shows
    `display_gizmo_while_undrafted:true` beside
    `disable_gizmo_while_undrafted:true`. That pairing IS the fingerkill defect
    the scoping pass filed; the gate reporting it is the acceptance.
11. `{"op":"draft","args":{"pawns":[<casterId>]}}` then repeat step 10 → the
    `undrafted` gate is gone.

## Phase 4 — the banded refusal, distinct from the cost refusal

12. With psyfocus **below 25%** and a level-5+ psycast known:
    `{"op":"abilities","args":{"pawn":<casterId>}}` → that row is
    `gate:"psyfocus-band"` (not `"psyfocus"`), and its `reason` contains the
    word `BANDED` and names the psyfocus floor to reach.
13. Meditate up past 50% and repeat → the row's gate is either absent
    (`castable:true`) or `"psyfocus"` if the cost genuinely does not fit.
    **The two gates must never both be reachable for the same call** — they are
    different clauses of `Psycast.GizmoDisabled` and this is the check that they
    were not collapsed.

## Phase 5 — the target layer, and the cell/pawn distinction

Use a **pawn-target** psycast (`canTargetPawns:true, canTargetLocations:false`)
for 14–15 and a **cell-target** one (the reverse) for 16.

14. `{"op":"abilities","args":{"pawn":<casterId>,"target":"pawn:<victimId>"}}`
    → each row gains `target_castable`, `target_gate`, `target_reason`, and
    `data.target.kind:"thing"` with `data.target.id`, `.label`, `.at`,
    `.psychic_sensitivity`, `.boss`.
15. Same verb, **cell** target on a pawn-only psycast:
    `{"op":"abilities","args":{"pawn":<casterId>,"target":"10,10"}}` → that row is
    `target_gate:"target-params"` and the reason names
    `canTargetLocations is False`.
    Then `{"op":"ability-cast","args":{"pawn":<casterId>,"ability":"<pawnOnly>","target":"10,10"}}`
    → `accepted:0`, `rejected[0].gate:"target-params"`. **This is the acceptance
    bullet "refuses a cell for an ability whose canTargetLocations is false".**
16. The cell-target psycast with a **pawn** target → `target-params` again, from
    the other side (`canTargetPawns:false`).
17. A target far outside range on a short-range psycast →
    `target_gate:"out-of-range"`, and the reason quotes the **effective** range.
    Confirm `data.casters[0].abilities[*].range` (EffectiveRange) and
    `.nominal_range` (`verbProps.range`) are published separately — in fog or
    under a weather range cap they differ, and only the first is real.

## Phase 6 — the cast actually happens

18. `{"op":"ability-cast","args":{"pawn":<casterId>,"ability":"<pawnTargetPsycast>","target":"pawn:<victimId>"}}`
    → `ok:true`, `data.counts.accepted:1`, and on the accepted line:
    `ability_job_running:true`, `order_effect:"started"`, `job_def` naming the
    ability's job (`CastAbilityOnThing` unless the def overrides `jobDef`),
    `warmup_ticks` present, and `data.action.journal_seq` **not null**.
19. `{"op":"journal","args":{"since":<seq0>,"types":["action"]}}` → one `action`
    row, `verb:"ability-cast"`, `step:"cast"`, `target:"<defName>"`, carrying
    `verdict.accepted:1`.
20. `{"op":"advance","args":{"ticks":<warmup_seconds*60 + 120>, …escapes}}` then
    re-read the victim: `{"op":"pawn","args":{"id":<victimId>,"sections":["health","state"]}}`
    → the ability's effect is on the target.
    On the fingerkill bench with **Bloodbond**: the bond hediffs on the victim,
    and **murderous rage — or `Tantrum` if the victim is incapable of violence**
    (a pacifist prisoner takes the second, and the issue's original acceptance
    bullet is wrong about this).
    With **Fingerkill**: the CASTER loses a body part FIRST (bail if the
    sacrifice ledger is exhausted), and the victim's heart is destroyed **LAST**
    — the issue body has that order backwards.
21. Immediately re-read `{"op":"abilities","args":{"pawn":<casterId>}}` →
    `psychic.psyfocus` and `psychic.entropy` have MOVED (read back, never
    computed: `Psycast.Activate` subtracts psyfocus unconditionally and clamps
    at 0), and the cast ability's `cooldown_ticks_remaining` > 0 with
    `gate:"cooldown"`.

## Phase 7 — the confirmation that must never open a window

Needs a psycast with `confirmationDialogText` (Voidpull on the fingerkill
bench).

22. `{"op":"abilities","args":{"pawn":<casterId>}}` → that row carries
    `confirmation.required:true` and `confirmation.text` with the def's text.
23. `{"op":"ability-cast","args":{"pawn":<casterId>,"ability":"<confirmed>","target":"<cell>"}}`
    → `ok:true`, and `data.confirmation.text` is present on the envelope.
24. **`{"op":"status"}` → `data.forcePause` is ABSENT.** This is the check that
    matters: a shown `Dialog_MessageBox` sets `forcePause = true` and every
    subsequent `advance` halts at 0 ticks with `reason:"dialog"` forever.
25. `{"op":"advance","args":{"ticks":600, …escapes}}` → it advances 600 ticks,
    not 0.

## Phase 8 — the queue, and the collision

26. `{"op":"ability-cast","args":{"pawn":<casterId>,"ability":"<x>","target":"pawn:<v>","queue":true}}`
    on a BUSY caster → accepted line has `order_effect:"queued"` and
    `queue_depth` ≥ 1.
    On an IDLE caster → `order_effect:"started"` with the
    `order_effect_note` explaining that `TryTakeOrderedJob`'s first branch fires
    for an idle pawn. Both are correct; the point is that the line reports what
    HAPPENED, not the flag that was asked for.
27. Immediately repeat the identical call → `accepted:0`,
    `rejected[0].gate:"already-doing-it"`. `TryTakeOrderedJob` would have
    returned true having enqueued nothing.
28. With a cast queued, `{"op":"abilities","args":{"pawn":<casterId>}}` →
    `psychic.queued.psyfocus_cost` and `.queued.entropy` are non-zero, and any
    ability whose cost no longer fits shows `gate:"psyfocus"` or `"entropy"`.
    **This is the check that the queue terms were not dropped** — the naive
    implementation publishes "psyfocus 0.6, cost 0.3, castable" with two casts
    already committed.

## Phase 9 — the plural, and a modded refusal

29. `{"op":"ability-cast","args":{"pawns":[<a>,<b>],"ability":"<x>","target":"pawn:<v>"}}`
    → `data.accepted` and `data.rejected` between them cover both pawns, each
    with its own gate.
30. Any ability whose comp refuses (an exhausted resource, an already-bonded
    victim, a wrong target type the def's flags do not describe) → the rejection
    is `gate:"comp-gizmo"`, `"comp-valid"` or `"cannot-apply"` with the **comp's
    class name** in the reason. Nothing is special-cased by mod, def or comp
    name anywhere in the implementation; if a modded refusal comes back as a
    bare `refused`, that is the finding.

---

## What a failure here means

* `gate:"gizmo-disabled"` on a refusal — the reproduction missed a clause and
  the authority (`Ability.GizmoDisabled`) caught it. The reason string says
  which subclass. That is the design working, but it is worth a follow-up issue.
* `baby_check:"unavailable"` — the `PawnSafe.Baby` field refs did not bind on
  this bench; clause B12 was not evaluated by the read. Check the journal for
  the `warning` row.
* `source:"unavailable"` on a caster — `AllAbilitiesForReading` threw. The list
  is empty because we could not ask, not because the pawn has none.
