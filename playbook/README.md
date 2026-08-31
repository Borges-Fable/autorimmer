# playbook/ — what the colony knows

The learning system's memory, versioned in-repo so every session — and every
orchestration worker — starts with everything ever learned, and Dorian can
review learning in diffs (DESIGN §Learning). Four parts, one job each:

- **lessons** (this directory) — facts worth keeping: one per file, cited.
- **`checklists/`** — watches: thresholds and responses, keyed to the moments
  the loop actually has (its README carries the shape argument).
- **`templates/`** — structure: layouts whose lessons are baked in, so a
  class of checks stops being checks at all.
- **`postmortem.md`** (repo root) — the pump: journal history in, artifacts
  out, each at the lowest rung of the escalation ladder that removes the
  cause.

`INDEX.md` is the per-session recall surface; `SESSION-START.md` is the load
order 4.2 consumes.

## Lesson format

One lesson per file — the auto-memory pattern (one fact per file + index),
because it is proven and because merges of single-fact files never conflict
by accident. Frontmatter:

```
name:         <kebab slug, = filename>
trigger:      <when to recall this — a moment, not a topic>
applies-when: <optional applicability predicate (biome:desert, mod:X active);
               absent = always. Drives the ledger's n/a verdict, so 4.4's
               retirement pass can tell "idle" from "inapplicable">
severity:     GoodToKnow | Important | Critical   (vanilla's OpportunityType —
               reused so concept defs mine directly; severity is per call
               site when mining, one concept can be taught at two tiers)
confidence:   verified-in-source | observed-at-bench | evan-stated | proposed
source:       <run / session / member — the citation. Non-negotiable.>
```

Body sections, fixed: **What** (the rule, one or two sentences), **Why** (the
mechanism or the incident, with the citation inline), **How to apply** (verbs
and reads, by name), **Retire when** (the evidence that ends or escalates the
lesson — 4.4 makes this contract). Link related lessons with `[[name]]`.

`confidence` ranks trust for MECHANISM claims only: source beats bench beats
recollection. For POLICY (what to want, what to trade), Evan outranks source,
which has no opinion. `proposed` marks the author's own invention awaiting
correction — constants, formulas, thresholds — and must be visible at the
claim, not only in the frontmatter.

## Standing history, kept on purpose

- **The potato correction.** `growing-zone-default-is-potato.md` replaces an
  earlier lesson claiming an unset zone grows *nothing* — false, and worse
  than wrong: it aimed the agent at a symptom that never occurs. The
  correction stays visible in the file as the standing example of how a
  plausible lesson fails, and it is why `postmortem.md`'s conflict rule is
  verify-then-replace, never newest-wins.
- **The formula that awaits Evan.** `combat-role-passion-over-skill.md`
  proposes constants that fit both of his data points and are still invented.
  The file says so; keep saying so until he corrects them.
- **Provenance.** The first nine lessons were seeded from the session-9
  design conversation (git-bug 96d9315: the curriculum audit, Evan's
  game-knowledge comments, and the verification pass that corrected two of
  them). Everything since arrives through `postmortem.md` or from Evan
  directly.
