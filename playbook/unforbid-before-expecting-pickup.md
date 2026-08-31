---
name: unforbid-before-expecting-pickup
trigger: after any raid, drop-pod arrival, or colony start
severity: Critical
confidence: observed-at-bench
source: Evan, session 7 — a forbidden rifle, revolver, knife and full flak set sat unused; FSWA_MapComponent.cs:80
---

**What.** Unforbid dropped gear before expecting anyone to pick it up.

**Why.** Raider drops land **forbidden**, and drop-pod starts land with their own
gear forbidden. Auto-arming skips forbidden weapons explicitly — FSWA tests
`thing.def.IsWeapon && !thing.IsForbidden(...)`, and the verification pass found
it is not one call site but THREE (candidate scan, per-weapon check, pinned-weapon
check — all in FSWA_MapComponent.cs). So the colony can be standing in a pile of
guns it will never touch. The same fact bites clothing: `Alert_NeedWarmClothes`
counts only apparel `IsInAnyStorage()`, so a parka on the ground does not exist
to it — the alert can fire with warm clothes twenty cells away. One underlying
rule, seen from two directions: **loose gear is invisible to every autonomous
system until it is unforbidden and hauled.**

**The trap that hides it:** a direct `equip` order **bypasses forbidden**. So the
manual path works, the autonomous path silently does nothing, and testing by hand
proves the opposite of what is true. Evan caught this live for exactly that
reason.

**How to apply.** `unforbid` over a rect covering the drop site or the battlefield
after the fight, before reading armament. Pair it with
[[weapons-have-no-alert]] — an armament count taken before the sweep is
measuring the wrong thing.

**Retire when.** Never. This is a standing property of how the game marks drops.
