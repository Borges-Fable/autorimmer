---
name: combat-role-passion-over-skill
trigger: assigning combat roles — day 0, and after any roster change
severity: Important
confidence: evan-stated — CALIBRATION STILL OPEN, see below
source: Evan, session 7
---

**What.** Pick combat roles on traits first, then passion, then raw skill.

**Why — Evan's rule, in his words.**

- **Traits first.** A Brawler is melee-only: ranged weapons give them a mood
  penalty and waste the trait's melee bonus.
- **Passion beats current skill, because you are picking for the long run.** "A
  passion for melee at 0 skill trumps no passion and high skill — think long
  term." The passionate pawn climbs; the other stalls.
- **Unless the gap is absurd.** His calibration point: "unless it's literally 2 to
  16 then it's not worth it." At that distance raw skill wins, because the
  passionate pawn will not close fourteen levels soon enough to matter.

**What the mod already handles.** `FindSuitableWeaponAndAmmo` implements the
trait half by itself — it tests `HasTrait(TraitDefOf.Brawler)` and sets
`allowRanged = !brawler`. **Brawler needs nothing from us.**

**What cannot be expressed today.** FSWA's `WeaponPreference` is **mod-wide**
(`FSWAMod.Settings.weaponPreference`), not per-pawn; the Brawler trait is its only
per-pawn override. So "make THIS pawn melee-only because of passion and skill"
cannot be delegated. Either FSWA grows a per-pawn preference, or the agent stops
delegating for that pawn and equips explicitly. Both halves are available today —
`assign {auto_arm:…}` and `equip` both shipped in session 8 — so this is now a
policy choice, not a blocked dependency.

**OPEN — needs Evan, do not invent a formula.** Two data points bound the
threshold but do not fix it: passion-at-0 beats no-passion-at-high, and 2-vs-16
is not worth it. A proposed encoding to correct rather than a rule to follow:

> effective_level = skill + (passion == Major ? 6 : passion == Minor ? 3 : 0)
> pick the higher effective_level; ties go to the passionate pawn.

That makes passion-0 (6) beat no-passion-5, and lets no-passion-16 beat
passion-2 (8), which matches both of his data points — but the constants are
mine, not his, and the file says so until he corrects them.

**Retire when.** The constants are Evan-confirmed and baked into a role-assignment
routine, at which point this becomes a template rather than a judgement call.
