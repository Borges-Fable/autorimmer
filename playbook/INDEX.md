# Playbook index

One line per lesson; the files carry the what/why/how and every citation.
Loaded at session start (`SESSION-START.md` is the order). Severity uses
vanilla's `OpportunityType` vocabulary; confidence is
`verified-in-source > observed-at-bench > evan-stated > proposed` — trust
order for MECHANISM claims only (for POLICY, Evan outranks source, which has
no opinion).

| lesson | severity | confidence | bites when |
|---|---|---|---|
| [[weapons-have-no-alert]] | Critical | verified-in-source | day 0, every roster change, every raid — no alert will ever fire |
| [[unforbid-before-expecting-pickup]] | Critical | observed-at-bench | after raids, drops, colony start — autonomy skips forbidden gear |
| [[who-will-actually-do-it]] | Critical | verified-in-source | after queuing any bill — skill is not assignment, the patient never counts |
| [[zzzt-letter-is-a-fire-already-burning]] | Critical | verified-in-source | a Zzzt or fire letter — act on the letter; the alert is home-scoped and late (**graduated**: `triggered.md §power-incident` + `templates/power-room`) |
| [[quicktest-and-autostart-collide]] | Critical | verified-in-source | launching `--quicktest` on a bench whose Saves still holds `autostart.rws` — map gen fails deterministically |
| [[one-doctor-is-zero-doctors]] | Critical | observed-at-bench | colony start and every roster change — the only doctor is a likely casualty |
| [[read-every-return-or-lose-a-colonist]] | Critical | observed-at-bench | any back-to-back short advances — a narrowed read is a blind loop |
| [[seek-off-is-a-decision-to-flee]] | Critical | verified-in-source | every threat while seek-at-will is off — armed colonists scatter instead of fighting |
| [[materials-are-a-standing-loop]] | Important | evan-stated | any input trending to zero — designate, don't retry the bill |
| [[benches-go-indoors]] | Important | evan-stated | siting any bench — enclosure plus the right room |
| [[growing-zone-default-is-potato]] | Important | verified-in-source | creating/reading growing zones — one raw read commits potato forever |
| [[wealth-buys-bigger-raids]] | Important | verified-in-source | every defence purchase and every post-mortem — the damping term |
| [[alert-need-defenses-self-silences]] | Important | verified-in-source | the whole early window — silence is expiry, not safety |
| [[combat-role-passion-over-skill]] | Important | evan-stated / proposed | assigning combat roles — formula awaits Evan |

Standing notes, kept visible on purpose:

- `growing-zone-default-is-potato` replaces a lesson that claimed an unset
  zone grows *nothing*. It grows potatoes. Kept as the worked example of a
  plausible lesson pointing at a symptom that never occurs — the failure mode
  `postmortem.md`'s conflict rule exists for.
- `combat-role-passion-over-skill` carries the orchestrator's proposed
  constants, not Evan's rule, and says so. Apply as tiebreak, present for
  correction, do not harden.

The rest of the learning system:

- `checklists/` — turn / triggered / daily; the shape argument is in its README
- `templates/` — layout starters with their lessons baked in as annotations
- `postmortem.md` (repo root) — the procedure that grows all of the above
- `SESSION-START.md` — what 4.2's loop loads, in order
- `PLAY-LOOP.md` — the loop itself (4.2): the turn, halts, emergency
  posture, cadence, artifacts, escalation — the file a play session runs
