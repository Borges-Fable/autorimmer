# `b1b3060` on a bench — one call, three settings, and an honest partial

Bench `_RimWorld-Agent` session `20260901T213125`, `--quicktest`, assembly
`1.0.0+cb5de43` (`Build:` commit `db6656b`). Envelopes in this directory.
Orchestrator-run; workers launch nothing.

## The read, before anything was set

`digest.posture`:

    ok: false   will_seek 0/1   area_bound 0/3   attack 0/1
    on_contact: {flee: 3, everything else 0}
    flee_risk: [{name: "Teresa", pawn: 317, will_seek: false}]

**`flee: 3`.** All three colonists would run on contact, visible in one field at
every read. That is the M1 state the issue exists to make sayable, and the
surface says it without being asked.

## "A pawn incapable of violence is refused BY NAME" — met, and better than refused

The quicktest map rolled **two violence-incapable colonists out of three**, so
the fixture worker C reported it could not force (`ff1f0b9`) simply OCCURRED.
`posture` puts them in a dedicated `incapable_of_violence` list with the game's
own reason:

    incapable of Violent work: SeekAndKill/Patch_PawnGetGizmos.ShowsSeekGizmo
    refuses it and HostilityResponseModeUtility's own dropdown omits Attack for
    it (WorkTagIsDisabled(WorkTags.Violent)). The area still binds; this pawn
    will never fight.

That last clause is why this is better than a flat refusal: **two of the three
settings still apply to them**, and a verb that just said "refused" would have
hidden that.

## The set — one call, `levers: ["area","seek","hostility"]`

    posture {area: 6, seek: true, hostility: "Attack"}   ->  accepted 3, rejected 0

| pawn | violence | area | hostility | seek | `on_contact` |
|---|---|---|---|---|---|
| 317 Teresa | capable | Area 2, 196 cells | **Attack** | **on** | **attack-then-seek** |
| 310 Danielle | incapable | Area 2, 196 cells | Flee | off | flee |
| 314 Greve | incapable | Area 2, 196 cells | Flee | off | flee |

`digest.posture` after:

    ok: true    will_seek 1/1   area_bound 3/3   attack 1/1
    on_contact: {flee: 2, attack-then-seek: 1}
    flee_risk: []

**`ok` is true while two pawns still flee, and that is correct.** The
denominators are stated in the block itself: `will_seek` and `attack` are over
violence-capable free colonists, because the game refuses both to the others;
`area_bound` is over colonists whose `Pawn_PlayerSettings.SupportsAllowedAreas`
is true. So `1/1` means "every pawn who can, does" and the two remaining `flee`
rows are a fact about the roster, not a failure of the verb.

## The refusal that makes the verb one verb

    posture {seek: true}        # no area

    ok: false   code: bad-args
    detail: posture is THREE settings that must agree, and a posture with two of
            them is the bug this verb exists to remove — pass `area` (an area id
            or label), or `area:null` to DECLARE unrestricted deliberately. Call
            `posture` with no arguments at all for a pure read.

You cannot set two of three by accident, and `area:null` makes "unrestricted" a
declaration rather than an omission.

## An unplanned catch, and the best evidence `7382bdd` could have asked for

Setting this fixture up I passed `area --op create --label s21-posture`. The
verb reads `name`, not `label`. It created "Area 2" and returned `ok:true` — and
told me so:

    ignored_args: {"keys":["label"],
      "read":["dry_run","kind","name","op"],
      "detail":"unknown arg 'label' — area read 'dry_run', 'kind', 'name' and
       'op' on this call. It was DROPPED and the verb RAN ANYWAY … It wrote
       journal seq 35..35; read those rows to see what it actually did.",
      "journal_seq_from":35, "journal_seq_to":35}

**Nobody wrote a test for this.** It was a real mistake by the orchestrator,
minutes after the mechanism merged, and the envelope named the key, named the
four keys the verb actually read, and handed over the journal seq to inspect.
Before `060c379` this was a silent "Area 2" and a reported success — the defect
in `7382bdd`'s title, caught in the wild rather than in a fixture.

## Two CLI properties, not mod defects

- `rwa` builds a list only from a **repeated** flag, so `--rect 118,118,14,14`
  sends a string and earns a clean `rect must be [x,z,w,h]`. `--rect 118 --rect
  118 --rect 14 --rect 14` is the working form. Python suites write JSON
  directly and are unaffected.
- `area allowed create` takes `name`, not `label`.

## Still owed on this issue

The save/load round trip, which is the one acceptance bullet a suite cannot
assert without a restart.
