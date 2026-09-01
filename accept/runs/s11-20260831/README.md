# Session 11 — the first live acceptance run in this project's history

Bench: `_RimWorld-Agent`, session `20260901T003735`, launched fresh at 20:36 EDT
on 2026-08-31 against `autostart.rws` (tick ~90.4k, 4 colonists).

**Why "fresh" is written down.** A bench was already running when this session
opened — session `20260831T232158`, launched 19:21:58. The assembly carrying
3.5's dialog verbs and 3.6's bills verbs was committed at **20:19:20**, 57
minutes later. RimWorld loads the assembly at STARTUP, so that bench was running
IL that predates both surfaces, and any number measured against it would have
been a phantom in exactly the way session 10's `4087644` "three real failures"
were. It was killed and relaunched before a single check ran.

IL confirmed on the fresh bench by a live `status` read: **118 verbs**, including
`dialog-choose`, `dialog-dismiss`, `letter-read`, `letter-choose`,
`letter-dismiss` (3.5) and `bill-add`, `bill-options`, `bill-remove`,
`bill-reorder`, `bill-set`, `bills`, `storage`, `storage-link`, `storage-set`,
`storage-unlink` (3.6).

## Results

| suite | checks | result |
|---|---|---|
| suite | first run | after repair |
|---|---|---|
| `3.4-pawn-orders` | 147/150 | **159/159** ✅ (`3.4-run2.log`) |
| `8b0b88f-already-designated` | 121/123 | **123/123** ✅ (`8b0b88f-run2.log`) |
| `4087644-order-honesty` | 92/97 | **100/100** ✅ (`4087644-run2.log`) |
| `70ac258-things-stable-order` | **99/99** ✅ | — |
| `3.6-bills-storage` | **116/116** ✅ | — |
| `3.5-dialog-verbs` | 48, exit 2 | 104 pass / 0 fail (`3.5-run2.log`); trade phase 79/86 (`3.5-p3.log`) |

**Eleven of the twelve failures across every suite were DRIVER defects needing no
mod change.** The two real mod defects both sit on the trade surface and are filed
as `be75bc4` and `7e8c969`.

**Zero red errors in every suite that checked for them** — 3.4's `5.18a`,
4087644's `6.16`, 3.6's `6.5`, each asserting `data.count` of red errors is 0
across its whole run.

## The phase-0 guard was itself exercised

The standing worry was that the shape contracts added in session 10 were
unproven — `eq(..., None)` passes on an ABSENT key exactly as happily as a null
one, so a wrong dig path goes green while asserting nothing. Phase 0 ran and
passed in all five drivers: 48 shape checks in `70ac258`, 58 in `3.6`. The dig
paths those suites depend on are real, read back off a live envelope.

## What the failures were

- **3.4 (3.2, 3.6b, 3.6c)** — one cause, and it is the DRIVER's. The bench
  colonist already wears exactly `Apparel_Parka` + `Apparel_Tuque`, which is
  exactly what phase 3's `cold` policy allows, so the policy asks for a wardrobe
  the pawn is already in. Worth more than the three reds: check `3.6a` ("the pawn
  is WEARING the parka") **PASSED while asserting nothing** — it was already true
  before the policy was assigned. A hollow green hiding behind three honest reds.
- **3.5 (exit 2)** — `dev:incident` cannot fire world-targeted incidents:
  `IncidentDef 'GiveQuest_Random' does not allow a Map target (targetTags: World)`.
  It always passes a Map. That blocked ~123 of 171 checks — the largest block of
  unproven acceptance in the project — and it is a real gap, correctly routed to
  exit 2 (fixture) rather than reported as a spec failure.
- **4087644 (6.5, 6.9a/b, 6.15a/b)** — triaged separately; see the session 11
  ledger.
