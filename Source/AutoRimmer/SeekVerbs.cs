using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;

namespace AutoRimmer
{
    // ============================================== spec: seek-at-will ========
    // AUTONOMOUS COMBAT. One toggle per pawn instead of a draft-and-order cycle
    // per raid.
    //
    // Evan, 2026-08-31: "all we need for combat is to flip that thing on and to
    // configure it in a way that respects the pawns skills, or to make it not on
    // at all, they also always need to make sure flee mode is set to attack, not
    // flee or ignore."
    //
    // 3.4 already ships the micromanagement path — draft, attack, equip,
    // fire-at-will, man-turret. It works, and over a ten-day run it eats the
    // whole play loop. SeekAndKill's "Seek at Will" hands combat to a squad
    // brain: threat clustering, priority dispatch, cover-aware formations,
    // self-defense reflex. Toggling it is the entire combat interface.
    //
    // ------------------------- THIS IS A MOD-AWARE VERB ----------------------
    // The first one. Every other verb in AutoRimmer targets vanilla, and
    // SeekAndKill is NOT a dependency — it happens to be on the agent bench. So
    // this file takes NO compile-time reference and reaches everything by
    // reflection, resolved once and cached. When the mod is absent the verb says
    // so instead of throwing.
    //
    // The template is SeekAndKill's own PSInterop, which reflects into
    // Perspective Shift and — if PS is present but its internals moved — goes
    // INERT for the session rather than running two brains on one pawn. Same
    // instinct here: if a member we need is missing, report that plainly rather
    // than half-acting.
    //
    // -------------------------- THE GATE, AND A TWIST ------------------------
    // DESIGN's rule is that a player verb re-implements the widget's gate and
    // cites it. Here the widget's gate is `Patch_PawnGetGizmos.ShowsSeekGizmo`,
    // a PUBLIC STATIC method we can simply CALL — which is strictly better than
    // a copy, because a copy drifts when the mod changes and this one cannot.
    //
    // So: the mod's own method is the AUTHORITY on whether a pawn may be
    // toggled. Our clause-by-clause copy exists only to say WHICH clause
    // refused, because "refused" without a reason is the failure mode this
    // project exists to avoid. If the two ever disagree — authority says no,
    // our copy finds nothing wrong — we report gate `mod-refused` rather than
    // inventing a reason.
    //
    //   ShowsSeekGizmo, clause for clause (SeekAndKill/Patch_PawnGetGizmos.cs):
    //     pawn.Faction == Faction.OfPlayer            -> "not-player-faction"
    //     !pawn.RaceProps.Animal                      -> "animal"
    //     !pawn.Drafted                               -> "drafted"
    //     !pawn.WorkTagIsDisabled(WorkTags.Violent)   -> "violent-disabled"
    //
    // `!Drafted` is the point of the feature, not an obstacle to route around.
    // Seek at Will is what an UNDRAFTED pawn does; drafting is the thing it
    // replaces. A drafted pawn is refused, and the refusal says to undraft.
    //
    // ------------------------- TOGGLE IS A FLIP, NOT A SET -------------------
    // `SeekRegistry.Toggle` flips, and `MpCompat.SyncedToggleSeek` is the path
    // the gizmo uses — the seek set is saved state the think tree reads on every
    // client, which is why it is synced. We go through the synced path and NEVER
    // call Toggle directly.
    //
    // Because it is a flip, an absolute `on:true` has to read first and only
    // call when current != wanted. A pawn already in the wanted state is an
    // `already` rejection (3.4's convention) rather than a double-toggle that
    // would land on exactly the wrong value.
    //
    // And `SyncedToggleSeek` re-checks `ShowsSeekGizmo` itself and RETURNS
    // SILENTLY when it fails, so a caller that assumes success is wrong. Every
    // write here is read back through `IsToggled` before it is reported.
    // =========================================================================
    internal static partial class PawnActs
    {
        private const string SeekV = "seek-at-will";

        // Stance is a fraction of weapon range (SeekStanceExtensions):
        // Close 0.35, Far 0.9, Medium the mod's tuned setting. That is what
        // makes "respects the pawns skills" a concrete rule rather than a vibe.
        private const int StanceClose = 0;
        private const int StanceMedium = 1;
        private const int StanceFar = 2;

        private static string StanceName(int s)
            => s == StanceClose ? "close" : s == StanceFar ? "far" : "medium";

        private static int StanceValue(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "close": return StanceClose;
                case "medium": return StanceMedium;
                case "far": return StanceFar;
                default:
                    throw new VerbArgsException(
                        "stance must be close|medium|far|auto ('auto' picks by weapon and skill)");
            }
        }

        // ------------------------------------------------------------------
        // Reflection surface. Resolved once; `SeekMod.Present` is the single
        // question every caller asks.
        // ------------------------------------------------------------------
        private static class SeekMod
        {
            private static bool resolved;
            private static MethodInfo showsGizmo, isToggled, stanceOf, syncedToggle, syncedSetStance;
            private static MethodInfo shouldSeek;

            // A swallowed reflection failure that returns a LEGAL state value is
            // indistinguishable from a real answer, which is how a verb ends up
            // reporting "already not seeking" for a pawn that is seeking. Every
            // catch below records here and journals, so a fabricated answer is
            // at least visible. PawnSerializer.State does the same for a
            // throwing job report.
            [ThreadStatic] public static string LastError;

            private static T Guarded<T>(string what, Func<T> f, T onFail)
            {
                try { LastError = null; return f(); }
                catch (Exception e)
                {
                    LastError = what + " threw: " + e.GetType().Name + ": " + e.Message;
                    Journal.EmitWarning("seek-at-will: " + LastError);
                    return onFail;
                }
            }

            public static bool Present { get { Resolve(); return showsGizmo != null && isToggled != null
                && stanceOf != null && syncedToggle != null && syncedSetStance != null
                && shouldSeek != null; } }

            public static string Missing { get; private set; }

            private static void Resolve()
            {
                if (resolved) return;
                resolved = true;
                try
                {
                    var gizmos = TypeByName("SeekAndKill.Patch_PawnGetGizmos");
                    var registry = TypeByName("SeekAndKill.SeekRegistry");
                    var mp = TypeByName("SeekAndKill.MpCompat");
                    if (gizmos == null || registry == null || mp == null)
                    {
                        Missing = "SeekAndKill is not loaded";
                        return;
                    }
                    showsGizmo = gizmos.GetMethod("ShowsSeekGizmo", BindingFlags.Public | BindingFlags.Static);
                    isToggled = registry.GetMethod("IsToggled", BindingFlags.Public | BindingFlags.Static);
                    stanceOf = registry.GetMethod("StanceOf", BindingFlags.Public | BindingFlags.Static);
                    syncedToggle = mp.GetMethod("SyncedToggleSeek", BindingFlags.Public | BindingFlags.Static);
                    syncedSetStance = mp.GetMethod("SyncedSetStance",
                        BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(Pawn), typeof(int) }, null);
                    shouldSeek = registry.GetMethod("ShouldSeek", BindingFlags.Public | BindingFlags.Static);
                    var gone = new List<string>();
                    if (showsGizmo == null) gone.Add("Patch_PawnGetGizmos.ShowsSeekGizmo");
                    if (isToggled == null) gone.Add("SeekRegistry.IsToggled");
                    if (stanceOf == null) gone.Add("SeekRegistry.StanceOf");
                    if (syncedToggle == null) gone.Add("MpCompat.SyncedToggleSeek");
                    if (syncedSetStance == null) gone.Add("MpCompat.SyncedSetStance(Pawn,int)");
                    if (shouldSeek == null) gone.Add("SeekRegistry.ShouldSeek");
                    if (gone.Count > 0)
                        Missing = "SeekAndKill is loaded but these members are gone (mod updated?): "
                            + string.Join(", ", gone.ToArray());
                }
                catch (Exception e)
                {
                    Missing = "resolving SeekAndKill threw: " + e.GetType().Name + ": " + e.Message;
                }
            }

            private static Type TypeByName(string full)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = null;
                    try { t = asm.GetType(full, false); } catch { }
                    if (t != null) return t;
                }
                return null;
            }

            public static bool ShowsGizmo(Pawn p)
                => Guarded("ShowsSeekGizmo", () => (bool)showsGizmo.Invoke(null, new object[] { p }), false);

            public static bool IsToggled(Pawn p)
                => Guarded("IsToggled", () => (bool)isToggled.Invoke(null, new object[] { p }), false);

            public static int StanceOf(Pawn p)
                => Guarded("StanceOf", () => Convert.ToInt32(stanceOf.Invoke(null, new object[] { p })),
                           StanceMedium);

            // The think tree's OWN gate, which is NOT the gizmo's gate:
            // ShouldSeek additionally requires Spawned, !InMentalState and
            // !IsPsAvatar, and does NOT test WorkTags.Violent. So `toggled:true`
            // does not mean "this pawn will fight" — a colonist mid-mental-break
            // or off-map is toggled and inert. Publishing only the toggle would
            // be a bare boolean standing in for a decision.
            public static bool WillSeek(Pawn p)
                => Guarded("ShouldSeek", () => (bool)shouldSeek.Invoke(null, new object[] { p }), false);

            // These two MUTATE, so a throw must not be swallowed into a value —
            // it is returned to the caller, which records the pawn as failed and
            // keeps going rather than abandoning the batch mid-write.
            public static string Toggle(Pawn p)
            {
                try { syncedToggle.Invoke(null, new object[] { p }); return null; }
                catch (Exception e) { return "SyncedToggleSeek threw: " + e.GetType().Name + ": " + e.Message; }
            }

            public static string SetStance(Pawn p, int stance)
            {
                try { syncedSetStance.Invoke(null, new object[] { p, stance }); return null; }
                catch (Exception e) { return "SyncedSetStance threw: " + e.GetType().Name + ": " + e.Message; }
            }
        }

        // Public read for the `pawn` observer. Null when the mod is absent, so
        // the observer says "no such concept here" rather than "off".
        internal static Dictionary<string, object> SeekStateOf(Pawn p)
        {
            if (p == null || !SeekMod.Present) return null;
            return new Dictionary<string, object>
            {
                ["toggled"] = SeekMod.IsToggled(p),
                ["stance"] = StanceName(SeekMod.StanceOf(p)),
                ["eligible"] = SeekMod.ShowsGizmo(p),
                // The one that answers "is this pawn actually going to fight".
                ["will_seek"] = SeekMod.WillSeek(p),
            };
        }

        // ------------------------------------------------------------------
        // Which clause of ShowsSeekGizmo refused. Diagnosis only — the mod's
        // own method is the authority (see the header).
        // ------------------------------------------------------------------
        private static string SeekGateReason(Pawn p)
        {
            try
            {
                if (p.Faction != Faction.OfPlayer)
                    return "not-player-faction";
                if (p.RaceProps != null && p.RaceProps.Animal)
                    return "animal";
                if (p.Drafted)
                    return "drafted";
                if (p.WorkTagIsDisabled(WorkTags.Violent))
                    return "violent-disabled";
            }
            catch { }
            return "mod-refused";
        }

        private static string SeekGateText(string gate)
        {
            switch (gate)
            {
                case "not-player-faction": return "the Seek at Will gizmo is drawn only for player-faction pawns";
                case "animal": return "the gizmo is not drawn for animals";
                case "drafted":
                    return "the gizmo is hidden while a pawn is DRAFTED — Seek at Will is what an "
                        + "undrafted pawn does, and drafting is the micromanagement it replaces. "
                        + "Undraft the pawn first.";
                case "violent-disabled": return "this pawn is incapable of Violent work";
                default:
                    return "SeekAndKill's own ShowsSeekGizmo refused this pawn and none of the four "
                        + "documented clauses explains it — the mod's gate may have gained a clause.";
            }
        }

        // ------------------------------------------------------------------
        // Skill-aware choices. Both rules are ECHOED in the result so a caller
        // never has to re-derive why a pawn got what it got.
        // ------------------------------------------------------------------
        private static int SkillLevel(Pawn p, SkillDef def)
        {
            try
            {
                var rec = p.skills?.GetSkill(def);
                return rec == null || rec.TotallyDisabled ? -1 : rec.Level;
            }
            catch { return -1; }
        }

        private static ThingWithComps PrimaryWeapon(Pawn p)
        {
            try { return p.equipment?.Primary; } catch { return null; }
        }

        private static bool HasBrawler(Pawn p)
        {
            try { return p.story?.traits != null && p.story.traits.HasTrait(TraitDefOf.Brawler); }
            catch { return false; }
        }

        // Weapon range is what the stance scales, so the weapon decides first
        // and the skill only breaks the ranged tie.
        private static int AutoStance(Pawn p, out string why)
        {
            var w = PrimaryWeapon(p);
            bool ranged = false, melee = false;
            try
            {
                ranged = w != null && w.def != null && w.def.IsRangedWeapon;
                melee = w != null && w.def != null && w.def.IsMeleeWeapon;
            }
            catch { }

            if (HasBrawler(p)) { why = "Brawler — wants to be in melee"; return StanceClose; }
            if (w == null) { why = "no weapon equipped — nothing to hold range with"; return StanceClose; }
            // IsMeleeWeapon is `IsWeapon && !IsRangedWeapon`, so a NON-weapon in
            // the primary slot reads as neither. Say so rather than falling
            // through to a default whose text claims "ranged weapon".
            if (melee) { why = "melee weapon — must close to fight at all"; return StanceClose; }
            if (!ranged)
            {
                why = "primary equipment is not a weapon (" + (w.def?.defName ?? "?")
                    + ") — nothing to hold range with";
                return StanceClose;
            }

            int shooting = SkillLevel(p, SkillDefOf.Shooting);
            if (shooting < 0)
            {
                // -1 is the sentinel for "no skills tracker or totally
                // disabled" — a player-faction combat mechanoid is the live
                // case, and it is NOT a middling shot. It fights with natural
                // verbs at whatever range those have, so the mod's tuned
                // default is the honest answer and the text must not invent a
                // skill level it never read.
                why = "no Shooting skill to read (mechanoid or skill-disabled) — the mod's tuned default";
                return StanceMedium;
            }
            if (ranged && shooting >= 8)
            {
                why = "ranged weapon, Shooting " + shooting + " >= 8 — can use the reach (0.9 of range)";
                return StanceFar;
            }
            if (ranged && shooting <= 4)
            {
                // RimWorld's hit chance falls off with distance, so a poor shot
                // standing at 0.9 of range mostly misses. Closing is the better
                // of two bad options, not a preference.
                why = "ranged weapon, Shooting " + shooting + " <= 4 — hit chance rises as range "
                    + "closes, so a poor shot should close (0.35 of range)";
                return StanceClose;
            }
            why = "ranged weapon, middling Shooting " + shooting + " — the mod's tuned default";
            return StanceMedium;
        }

        // "or to make it not on at all" — the skill-aware ON decision.
        private static bool AutoOn(Pawn p, out string why)
        {
            var w = PrimaryWeapon(p);
            bool ranged = false;
            try { ranged = w != null && w.def != null && w.def.IsRangedWeapon; } catch { }
            if (ranged) { why = "has a ranged weapon"; return true; }

            int melee = SkillLevel(p, SkillDefOf.Melee);
            if (melee >= 6) { why = "Melee " + melee + " >= 6"; return true; }
            if (melee < 0)
            {
                // No skills tracker at all — a player-faction combat mechanoid.
                // It has no equipment.Primary either, because it fights with
                // natural verbs, so the unarmed-weakling branch below would turn
                // a centipede off as a liability. Seek is what it is FOR.
                why = "no skills tracker (mechanoid) — fights with natural verbs, so arm it";
                return true;
            }
            why = w == null
                ? "unarmed and Melee " + melee + " < 6 — seeking would make this pawn a casualty"
                : "melee-only weapon and Melee " + melee + " < 6 — seeking would make this pawn a casualty";
            return false;
        }

        // ------------------------------------------------------------------
        // seek-at-will {pawns, on?, stance?, dry_run?}
        //
        //   on      true | false | "auto"
        //   stance  "close" | "medium" | "far" | "auto"
        //
        // NEITHER on NOR stance => a pure READ. Report and mutate nothing, so
        // the agent can check combat readiness without changing it.
        // ------------------------------------------------------------------
        [Verb("seek-at-will")]
        public static object SeekAtWill(VerbContext ctx)
        {
            var map = Map();
            var a = ctx.Args;

            if (!SeekMod.Present)
                throw new VerbArgsException(
                    (SeekMod.Missing ?? "SeekAndKill is not loaded")
                    + ". `seek-at-will` drives that mod's autonomous squad combat and has no vanilla "
                    + "equivalent; without it, combat is 3.4's draft/attack/fire-at-will path.");

            var pawns = PawnList(map, a);
            bool dryRun = a.Bool("dry_run", false);

            // `on` is tri-state, so it is read raw rather than through Bool().
            bool wantOn = a.Has("on");
            bool autoOn = false, onValue = false;
            if (wantOn)
            {
                object raw = a.Raw("on");
                if (raw is bool b) onValue = b;
                else if (raw is string s && s.Equals("auto", StringComparison.OrdinalIgnoreCase)) autoOn = true;
                else throw new VerbArgsException("on must be true, false, or \"auto\"");
            }

            bool wantStance = a.Has("stance");
            bool autoStance = false;
            int stanceValue = StanceMedium;
            if (wantStance)
            {
                string s = a.Str("stance");
                if (string.Equals(s, "auto", StringComparison.OrdinalIgnoreCase)) autoStance = true;
                else stanceValue = StanceValue(s);
            }

            bool readOnly = !wantOn && !wantStance;
            var outcome = new Outcome();
            var ids = new List<object>();
            var warnings = new List<object>();

            foreach (var p in pawns)
            {
                var line = new Dictionary<string, object>();
                bool eligible = SeekMod.ShowsGizmo(p);

                // The before picture is worth reporting even for a refused
                // pawn: "why is this one not seeking" is the question being
                // asked, and a bare gate does not answer it.
                bool wasOn = SeekMod.IsToggled(p);
                int wasStance = SeekMod.StanceOf(p);
                string hostility = null;
                try { hostility = p.playerSettings?.hostilityResponse.ToString(); } catch { }

                line["before"] = new Dictionary<string, object>
                {
                    ["toggled"] = wasOn,
                    ["stance"] = StanceName(wasStance),
                    ["hostility_response"] = hostility,
                };
                line["shooting"] = SkillLevel(p, SkillDefOf.Shooting);
                line["melee"] = SkillLevel(p, SkillDefOf.Melee);
                line["weapon"] = SafeObj(() => (object)(PrimaryWeapon(p)?.def?.defName));
                line["brawler"] = HasBrawler(p);
                line["eligible"] = eligible;

                if (readOnly)
                {
                    // A read reports every pawn as accepted — nothing was
                    // refused, because nothing was attempted.
                    outcome.Ok(p, line);
                    WarnHostility(p, wasOn, hostility, warnings);
                    continue;
                }

                if (!eligible)
                {
                    string gate = SeekGateReason(p);
                    // The diagnostic block goes WITH the rejection: "why is this
                    // one not seeking" is the question, and a bare gate word is
                    // not the answer.
                    outcome.No(p, gate, SeekGateText(gate), line);
                    // A refused pawn can still be seeking from an earlier call —
                    // a drafted pawn keeps its toggle — so the pairing check
                    // still applies to it.
                    WarnHostility(p, wasOn, hostility, warnings);
                    continue;
                }

                bool targetOn = wasOn;
                if (wantOn)
                {
                    if (autoOn)
                    {
                        targetOn = AutoOn(p, out string whyOn);
                        line["on_auto_reason"] = whyOn;
                    }
                    else targetOn = onValue;
                }

                int targetStance = wasStance;
                if (wantStance)
                {
                    if (autoStance)
                    {
                        targetStance = AutoStance(p, out string whyStance);
                        line["stance_auto_reason"] = whyStance;
                    }
                    else targetStance = stanceValue;
                }

                bool toggleNeeded = wantOn && targetOn != wasOn;
                bool stanceNeeded = wantStance && targetStance != wasStance;

                if (!toggleNeeded && !stanceNeeded)
                {
                    outcome.No(p, "already",
                        "already " + (wasOn ? "seeking" : "not seeking")
                        + " at stance " + StanceName(wasStance) + " — nothing to change",
                        line);
                    // THIS IS THE COMMON PATH, and the warning used to be skipped
                    // on it. Re-issuing the same call is how an agent CHECKS
                    // readiness, so a colony of seeking pawns all set to Flee
                    // would report zero warnings on exactly the run that was
                    // asking. Found in review, 2026-08-31.
                    WarnHostility(p, wasOn, hostility, warnings);
                    continue;
                }

                var applied = new List<object>();
                string writeError = null;
                if (!dryRun)
                {
                    // SyncedToggleSeek is a FLIP and it re-checks the gizmo gate
                    // itself, returning silently on failure — so it is called
                    // only when the state must change, and the result is read
                    // back rather than assumed.
                    //
                    // A throw here is caught per pawn: `Act` runs after the loop,
                    // so letting it propagate would leave the pawns already
                    // toggled mutated with NO journal row at all — unprovenanced
                    // partial mutation, which is the one outcome the journal
                    // exists to prevent.
                    if (toggleNeeded)
                    {
                        writeError = SeekMod.Toggle(p);
                        if (writeError == null) applied.Add("on");
                    }
                    if (writeError == null && stanceNeeded)
                    {
                        writeError = SeekMod.SetStance(p, targetStance);
                        if (writeError == null) applied.Add("stance");
                    }
                }
                else
                {
                    if (toggleNeeded) applied.Add("on");
                    if (stanceNeeded) applied.Add("stance");
                }

                bool nowOn = dryRun ? targetOn : SeekMod.IsToggled(p);
                int nowStance = dryRun ? targetStance : SeekMod.StanceOf(p);

                line["applied"] = applied;
                line["after"] = new Dictionary<string, object>
                {
                    ["toggled"] = nowOn,
                    ["stance"] = StanceName(nowStance),
                    ["hostility_response"] = hostility,
                };

                // The write is verified, not hoped for. SyncedToggleSeek can
                // no-op, and a silent no-op reported as success is exactly the
                // class of bug this project keeps finding in vanilla.
                if (!dryRun && (writeError != null || nowOn != targetOn || nowStance != targetStance))
                {
                    line["took"] = false;
                    if (writeError != null) line["write_error"] = writeError;
                    outcome.No(p, writeError != null ? "write-threw" : "write-did-not-take",
                        writeError ?? ("asked for toggled=" + targetOn + " stance="
                            + StanceName(targetStance) + " but the mod reads back toggled=" + nowOn
                            + " stance=" + StanceName(nowStance)),
                        line);
                    WarnHostility(p, nowOn, hostility, warnings);
                    continue;
                }

                line["took"] = true;
                outcome.Ok(p, line);
                ids.Add(p.thingIDNumber);
                WarnHostility(p, nowOn, hostility, warnings);
            }

            long seq = (!dryRun && ids.Count > 0)
                ? Act(SeekV, "seek-at-will", "x" + ids.Count,
                      new Dictionary<string, object> { ["ids"] = ids })
                : 0;

            var extra = new Dictionary<string, object>
            {
                ["mode"] = readOnly ? "read" : (dryRun ? "dry-run" : "write"),
                ["counts_mean"] = readOnly
                    ? "this was a READ: `counts.accepted` is how many pawns were REPORTED, not changed"
                    : "`counts.accepted` is how many pawns were changed",
                ["dry_run"] = dryRun,
                ["warnings"] = warnings,
                ["stance_meaning"] = "fraction of weapon range the pawn engages at — "
                    + "close 0.35, far 0.9, medium the mod's tuned setting",
                ["note"] = "Seek at Will is for UNDRAFTED pawns: the gizmo is hidden while drafted, "
                    + "so `draft` and this verb are alternatives, not partners. The second half of "
                    + "combat readiness is `assign {hostility:\"Attack\"}` — this verb reports it and "
                    + "warns when a seeking pawn is not set to Attack, but does not set it, because "
                    + "the Assign tab's column owns that lever.",
            };
            // Outcome.Result stamps `action` from `Accepted.Count > 0`, which
            // assumes accepted implies mutated. This verb is the first with a
            // read mode and a dry run, so that assumption is false here and the
            // default would print "NOT WRITTEN — the journal writer is closed"
            // on every read. `extra` is applied after, so it wins.
            if (readOnly || dryRun) extra["action"] = NoStamp();
            return outcome.Result(SeekV, seq, extra);
        }

        // "they also always need to make sure flee mode is set to attack, not
        // flee or ignore" — enforced by making it impossible to miss rather
        // than by duplicating `assign`'s setter.
        private static void WarnHostility(Pawn p, bool seeking, string hostility, List<object> warnings)
        {
            if (!seeking) return;
            if (string.Equals(hostility, "Attack", StringComparison.OrdinalIgnoreCase)) return;
            warnings.Add(new Dictionary<string, object>
            {
                ["key"] = "hostility-not-attack",
                ["pawn"] = p.thingIDNumber,
                ["name"] = PawnSafe.Name(p),
                ["hostility_response"] = hostility,
                ["detail"] = "this pawn is set to seek but its hostility response is '"
                    + (hostility ?? "null") + "'. An undrafted pawn that meets a hostile will "
                    + (string.Equals(hostility, "Flee", StringComparison.OrdinalIgnoreCase)
                        ? "run instead of fighting" : "ignore it")
                    + ". Fix with `assign {pawns:[" + p.thingIDNumber + "],hostility:\"Attack\"}`.",
            });
        }
    }
}
