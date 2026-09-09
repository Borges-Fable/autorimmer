# Acceptance — `use` / `use-options` (git-bug d318d4a)

The worker that wrote this may never launch RimWorld, so the acceptance is in
envelope form for the orchestrator to drive. Every envelope is
`{"op":…,"args":…}`; with python on the box each is
`rwa <op> --args-json '<json>'`.

**Every `advance` below needs the two per-call escapes** (git-bug `722c951`):

```json
"unread_ok": "accept/d318d4a-use.md: fixture, not a play loop",
"through_casualties": "accept/d318d4a-use.md: fixture, not a play loop"
```

---

## Fixture

| needs | why | stage it with |
|---|---|---|
| one free, awake, undrafted colonist who can reach a stockpile cell | every phase orders it | any colony |
| a `Neurotrainer_Shooting` on the ground | phase 2 (the plain case: no dialog, item destroyed) | `dev:spawn-thing {def:"Neurotrainer_Shooting"}` |
| a `TechprofSubpersonaCore` on the ground | phase 3, **the halting case** | `dev:spawn-thing {def:"TechprofSubpersonaCore"}` |
| a **current research project** | `CompUseEffect_FinishRandomResearchProject.CanBeUsedBy` refuses when `Find.ResearchManager.GetProject()` is null | `research-set {project:…}` |
| a `MechSerumResurrector` on the ground | phase 5 (the `needs-target` refusal — 826d4bf's half) | `dev:spawn-thing {def:"MechSerumResurrector"}` |
| a `CommsConsole` on the map | phase 6 (a **building** is a use target too) | any colony that built one |

Thing ids come from `things` / `nearest`. Nothing here needs a mod.

---

## Phase 0 — preflight and the shape contract

`eq(…, None)` passes on an *absent* key, so assert PRESENCE, not truthiness.

1. `{"op":"status"}` → `ok:true`, `data.gameLoaded:true`, **no** `data.forcePause`.
2. `{"op":"status"}` → `data.verbs` contains **both** `use` and `use-options`.
   If not, the DLL is stale — rebuild.
3. `{"op":"journal","args":{"limit":1}}` → record `data.last_seq` as **seq0**.
4. `{"op":"use-options","args":{"cap":5}}` → `ok:true` and these keys PRESENT:
   `data.options`, `data.total`, `data.more`, `data.usable` (null with no pawn),
   `data.note`. Each row has `thing`, `def`, `label`, `pos`, `comp`, `use_job`,
   `use_duration`, `destroys_self`, `effects`, `show_use_gizmo`, `stack`.
5. `{"op":"use-options","args":{"pawn":<P>,"thing":<neurotrainer>}}` → the row
   additionally has `usable`, `refusals`, `confirmations`, and each refusal row
   has `gate`, `reason`, **`reason_from`**.

---

## Phase 1 — the refusals name a gate, never an exception

6. `use` on a thing with no `CompUsable` (a steel stack) →
   `rejected[0].gate:"not-usable"`, `counts.accepted:0`, and `action.journal_seq`
   is **non-null** (a refused order journals — git-bug 4087644).
7. `use` on any *ingestible* thing (a meal) → the same `not-usable` gate, and
   the reason names **`consume`**.
8. `consume` on a `VoidsightSerum` (Anomaly only; skip without it) →
   `gate:"not-ingestible"` and the reason now names **`use`** (the onward
   pointer this issue added; those four serums set `showIngestFloatOption=false`
   and land here correctly).
9. `use {pawn, thing, target:…}` → **refused as bad args**, naming `target` as
   git-bug 826d4bf. Nothing mutated.
10. `use` on the neurotrainer with a **mechanoid** as the pawn →
    `gate:"not-flesh"`, `reason_from:"autorimmer"` (clause 1 returns a bare
    `false`; the game has no words for it).

---

## Phase 2 — the plain case: a neurotrainer, no dialog

11. `{"op":"use","args":{"pawn":<P>,"thing":<neurotrainer>}}` →
    `ok:true`, `counts.accepted:1`, `accepted[0].order_effect:"started"`,
    `data.use_job:"UseNeurotrainer"`, `data.destroys_self:true`,
    `data.confirmations:[]` (empty — no `ConfirmMessage` on this comp),
    `data.advance_halt` **absent**.
12. Record the pawn's Shooting XP from `pawns {ids:[<P>]}` first.
13. `advance` ~600 ticks. The run must **not** halt with `reason:"dialog"`.
14. `pawns {ids:[<P>]}` → Shooting XP is **+50,000** (`Props.xpGainAmount`
    defaults to 50000f; whether a *level* boundary is crossed depends on where
    the pawn started, so assert the XP, not "gained levels"), and
    `things`/`nearest` no longer finds the neurotrainer.
15. `journal {types:["action"],from:seq0}` → one `use` row with
    `verdict.accepted:1`, `use_job:"UseNeurotrainer"`.

---

## Phase 3 — the techprof core: the run HALTS, and that is correct

16. `research` → record `current`.
17. `use-options {pawn:<P>, thing:<core>}` → `usable:true` and
    **`advance_halt` PRESENT**, naming `Dialog_NodeTree` and `dialog-choose`.
18. `use {pawn:<P>, thing:<core>}` → `ok:true`, `data.advance_halt` present.
19. `advance` → **halts at ~0 ticks with `reason:"dialog"`**. This is the
    expected result, not a regression: `FinishProject(proj,
    doCompletionDialog:true)` adds a `Dialog_NodeTree` at **DoEffect** time,
    i.e. during this advance.
20. `interactions` → the window is a `Dialog_NodeTree`.
21. `dialog-choose` with the OK option → `ok:true`. (This one IS a
    `Dialog_NodeTree`, so 3.5 covers it — unlike the `Dialog_MessageBox` on the
    order path, which is why `use` never calls `TryStartUseJob`.)
22. `advance` again → ticks normally.
23. `research` → the recorded project is finished and `current` is **null**.
    Note `FinishProject` recursively finishes unfinished **prerequisites** and
    calls `AddTechprints`, so more than one project may have completed.
24. The core is gone (it carries `CompProperties_UseEffectDestroySelf`).

---

## Phase 4 — the refusal the core has when nothing is being researched

25. `research-stop` (or use a save with no current project).
26. `use-options {pawn:<P>, thing:<another core>}` → `usable:false`, and
    `refusals` contains `gate:"finish-random-research-project"` with
    `reason_from:"game"` and the game's own `NoActiveResearchProjectToFinish`
    text.
27. `use {pawn, thing}` → the same gate, `counts.accepted:0`, no exception.

---

## Phase 5 — `needs-target`: the silent no-op that is refused instead

28. `use {pawn:<P>, thing:<MechSerumResurrector>}` →
    `rejected[0].gate:"needs-target"`, `use_effect:"CompTargetable_SingleCorpse"`,
    and the reason names git-bug **826d4bf**.
    This is the point of the gate: without it the job would be taken, the pawn
    would walk over, the job would COMPLETE and nobody would be resurrected,
    because `CompTargetable.DoEffect` returns early while its private
    `selectedTarget` is null.

---

## Phase 6 — a BUILDING is a use target

29. `use-options {pawn:<P>}` (no `thing`) → the option list contains the
    `CommsConsole` with `comp:"CompUsable"`, `use_job:"TriggerObject"`,
    `destroys_self:false`.
30. Cut the console's power, then `use-options {pawn, thing:<console>}` →
    `refusals` contains `gate:"no-power"` with `reason_from:"game"`.
31. Restore power → `usable:true`.

---

## Phase 7 — every refusal at once, and `queue`

32. Pick a pawn that is both **unable to reach** the thing and **already
    running an equivalent job is not required** — any pawn behind a closed door
    will do. `use-options {pawn, thing}` → `refusals` has **more than one** row
    (the read runs the whole chain; `use` stops at the first, which is what the
    game does).
33. `use {pawn, thing, queue:true}` on a busy, interruptible pawn →
    `accepted[0].queue:true` and `order_effect:"queued"`, with `queue_depth`
    incremented. On an idle pawn expect `order_effect:"started"` plus
    `order_effect_note` — vanilla shift-click behaviour, not a dropped flag.
34. Repeat the identical `use` immediately → `gate:"already-doing-it"`.
35. `use {pawns:[<A>,<B>], thing}` → `A` accepted, `B` rejected with
    `gate:"one-user"`.

---

## Phase 8 — the implants, where the modal would have wedged the run

Needs Biotech and a `Mechlink` (`dev:spawn-thing {def:"Mechlink"}`).

36. `use-options {pawn:<P>, thing:<Mechlink>}` → `comp:"CompUsableImplant"`,
    `use_job:"InstallMechlink"`.
37. `use {pawn:<P>, thing:<Mechlink>}` → `ok:true` and
    **`data.dialogs_skipped` present**. If the colonist's Smithing work type or
    Intellectual work tag is disabled, `data.confirmations` carries the
    `CompUseEffect_InstallImplantMechlink.ConfirmMessage` text; if a royal title
    would be violated it carries the `CompRoyalImplant.CheckForViolations` row
    with its `consequence` line.
38. `advance` ~600 ticks → **no `reason:"dialog"` halt**. This is the whole
    point of the issue: routing through `TryStartUseJob` here would have raised
    a `Dialog_MessageBox` no verb can press Accept on, wedging every later
    advance permanently.
39. `pawns {ids:[<P>]}` → the `MechlinkImplant` hediff is present.
40. `use` the Mechlink again on the same pawn (spawn a second) →
    `gate:"install-implant"` with the game's `InstallImplantAlreadyInstalled`
    text and `reason_from:"game"`.

---

## What this acceptance deliberately does NOT cover

* **The `showUseGizmo` route.** `CompUsable.CompGetGizmosExtra` arms
  `Find.Targeter` and consumes the next click, which never comes headless. The
  float-menu route is the one reproduced; the gizmo is named in `use-options` as
  `show_use_gizmo` and not driven.
* **`use {target}`.** git-bug 826d4bf, a strict dependent of this route.
* **`bossgroup-call`.** git-bug 88880d7 keeps only the discovery half; the act
  is `use` on a `MechbandDish` / `CommsConsole` / `BurnoutMechlinkBooster`, and
  `CompUseEffect_CallBossgroup.ConfirmMessage` is ALWAYS non-empty, so that verb
  will land in `confirmations` on every call by construction.
