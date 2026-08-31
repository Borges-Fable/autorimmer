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

**How to apply.** The mechanisms all exist — `room-at` says whether a cell is in a
real room, the world serializers flag roofed/sheltered, and `place-layout`'s
preflight reports per-cell failures. The RULE is playbook material: check
enclosure at siting time, then bake the requirement into the room template so it
stops being a check at all. That escalation is the point (see `postmortem.md`).

**Retire when.** The bench appears in a `templates/` room that guarantees
enclosure, at which point this drops from checklist to template comment.
