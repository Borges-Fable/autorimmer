---
name: benches-go-indoors
trigger: siting any workbench, and any product stockpile that serves one
severity: Important
confidence: evan-stated
source: Evan, session 5 — "when you place a workbench, make sure it's indoors"
---

**What.** Benches, and the piles that feed and drain them, go indoors and in the
right room.

**Why.** Unroofed benches and their product piles deteriorate. Room context
matters beyond weather: a kitchen wants to be clean and near cold storage, and you
do not butcher where people eat. The API can tell you a cell is enclosed; it
cannot tell you the room is the RIGHT one for the job.

**How to apply.** Two of the three mechanisms exist today; the third does not.

- **`room-at`** says whether a cell is in a real room. Ships.
- **`roofed` / `unroofed`** is the exposure flag — on `things`' rollup, and as
  a `find-rect` requirement. Ships. *(This lesson previously said the
  serializers flag "roofed/**sheltered**". There is no `sheltered` field
  anywhere in `Source/AutoRimmer/`; the words are `roofed` and `unroofed`, and
  `find-rect` reports the failure as `reason:"unroofed"`.)*
- **Per-cell placement preflight** — `place-layout`'s per-cell failure report —
  **does not exist yet.** There is no `place-layout` verb and no `build` verb
  in the shipped surface; `PlaceVerbs.cs` registers `rooms`, `room`, `zones`
  and `areas` in spite of its name. Preflight arrives with **3.3**
  (`1adc737`, open).

So the check is manual today: `find-rect` with `roofed`, or `room-at` on the
target cell, before building. The RULE is playbook material: check enclosure at
siting time, then bake the requirement into the room template so it stops being
a check at all. That escalation is the point (see `postmortem.md`).

**Retire when.** The bench appears in a `templates/` room that guarantees
enclosure, at which point this drops from checklist to template comment.
