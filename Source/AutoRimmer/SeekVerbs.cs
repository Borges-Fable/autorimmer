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

        // ================================================ spec: posture =======
        // POSTURE IS A STANDING STATE, NOT THREE VERBS REMEMBERED IN THE RIGHT
        // ORDER (git-bug b1b3060). One call sets the three settings that must
        // agree — the allowed area, seek, and the hostility response — and
        // reports PER PAWN what it did and what it refused, because "partially
        // succeeded in silence" is the failure this verb exists to remove.
        //
        // ------------- WHAT THE INVESTIGATION CHANGED, BEFORE THE CODE -------
        // The issue (and `[[seek-off-is-a-decision-to-flee]]`) said the
        // `hostility_response` echo is truthful but MISLEADING, on the ground
        // that SeekAndKill's node sits above `ThinkNode_ConditionalColonist` and
        // so makes the vanilla flee node unreachable. Checked against both
        // sources rather than assumed, and **the premise is wrong in the way
        // that matters**:
        //
        //   * `JobGiver_ConfigurableHostilityResponse` — the ONLY consumer of
        //     `playerSettings.hostilityResponse` that can produce
        //     `JobDefOf.FleeAndCower` — is NOT in the `Humanlike` tree at all.
        //     It is in **`HumanlikeConstant`**, under
        //     `ThinkNode_ConditionalCanDoConstantThinkTreeJobNow`
        //     (Core/Defs/ThinkTreeDefs/Humanlike.xml).
        //   * `SeekAndKill/ThinkTreeInjector.Inject` skips any tree whose ROOT
        //     has none of its four anchors. `HumanlikeConstant`'s root holds
        //     exactly `ThinkNode_Subtree(Despawned)`,
        //     `ThinkNode_ConditionalCanDoConstantThinkTreeJobNow` and
        //     `ThinkNode_ConditionalCanDoLordJobNow` — no
        //     `ThinkNode_ConditionalColonist`, no `ThinkNode_QueuedJob`, no
        //     LordDuty subtree, no `ThinkNode_ConditionalRevenantState`. **Seek
        //     is never injected into the constant tree.**
        //   * `Verse.AI/Pawn_JobTracker.DetermineNextJob` runs
        //     `DetermineNextConstantThinkTreeJob()` FIRST and returns its result
        //     without ever touching `MainThinkNodeRoot`; and
        //     `JobTrackerTickInterval` re-runs the constant tree every 30 ticks
        //     (`AITuning.ConstantThinkTreeJobCheckIntervalTicks`) and starts its
        //     job with `JobCondition.InterruptForced` over whatever is running.
        //
        // So `hostility_response` does not describe an unreachable node. It
        // describes a node that runs STRICTLY ABOVE seek, in a tree the mod does
        // not touch, and that can INTERRUPT a seek job twice a second. A second
        // consumer says the same thing one level down: `JobGiver_
        // ReactToCloseMeleeThreat` sits at index 6 of the `Humanlike` root —
        // above the index-11 insertion point — and returns null unless
        // `hostilityResponse == Attack`.
        //
        // The M1 evidence the issue cites is the confirmation, not the
        // counter-example: op 109 had seek ON, hostility `Flee`, and Captain in
        // `JobDriver_FleeAndCower`. If the flee node were unreachable he could
        // not have been in that driver. **`Flee` beat seek, exactly as the
        // decision order says it must.**
        //
        // Consequences this file is built on:
        //   * `hostility:"Attack"` is not a BACKSTOP. It is the load-bearing
        //     setting, and seek is what happens after it declines.
        //   * seek ON + hostility `Flee` is the WORST combination available, and
        //     the flee branch has TWO halves that must not be conflated (this
        //     comment named the ATTACK branch's numbers until the orchestrator
        //     caught it, 2026-09-01):
        //       TRIGGER — `TryGetFleeJob` opens with
        //         `if (!SelfDefenseUtility.ShouldStartFleeing(pawn)) return null;`
        //         and THAT is where distance and sight live: `ShouldFleeFrom`
        //         with `checkDistance:true, checkLOS:false` over
        //         `ThingRequestGroup.AlwaysFlee`, and `checkDistance:true,
        //         checkLOS:true` over `ThingRequestGroup.AttackTarget` inside a
        //         `RegionTraverser.BreadthFirstTraverse` capped at 9 regions.
        //         `checkDistance:true` is `InHorDistOf(pawn.Position, 8f)`.
        //       DESTINATION — once triggered, `TryGetFleeJob` re-gathers the
        //         threat set at all THREE of its own call sites with
        //         `checkDistance:false, checkLOS:false` and hands the lot to
        //         `CellFinderLoose.GetFleeDest`. So the pawn flees from EVERY
        //         hostile the caches know about, not just the one that set it
        //         off — which is why one crow at 8 cells produced a 150-cell
        //         run. That is M1 day 1 and M1 day 4 in one state.
        //   * `on_contact` is therefore COMPUTABLE and is published. See
        //     `OnContact` below for the ordered resolution and its citations.
        //
        // ---------------------------- THE ARGUMENT SHAPE ---------------------
        //   posture {area, pawns?, seek?, hostility?, dry_run?, allow_empty_area?}
        //
        //   area       REQUIRED in write mode — an area id, an area label, or
        //              null/"none"/"unrestricted" to DECLARE unrestricted.
        //   pawns      default the whole colonist roster ("colonists").
        //   seek       true (default) | false | "auto" — "auto" is
        //              `seek-at-will`'s shipped skill rule, which is the thing
        //              that would have kept three unarmed colonists home on M1
        //              day 1.
        //   hostility  "Attack" (default) | "Flee" | "Ignore".
        //   dry_run    decide and report, write nothing.
        //
        // **No lever present at all is a pure READ** — same contract as
        // `seek-at-will`, so "what is our posture" costs nothing and changes
        // nothing. Presence of ANY lever switches to write mode, and then `area`
        // is required, because a posture with two of three settings is the bug.
        //
        // -------------- WHY AN ABSENT AREA IS REFUSED, NOT CREATED -----------
        // Three reasons, and the first is decisive:
        //   1. A fresh `Area_Allowed` is EMPTY, and
        //      `RimWorld/ForbidUtility.InAllowedArea` short-circuits on
        //      `TrueCount > 0` — so an auto-created area binds NOTHING while
        //      this verb would report every pawn bound. That is precisely the
        //      false report the issue exists to remove, manufactured by the fix.
        //   2. `new Area_Allowed(...)` rolls `Rand.Value` twice for its colour
        //      (AreaVerbs' header, determinism class R). A defaulted argument
        //      must not advance the shared RNG.
        //   3. `area allowed create` and `area allowed add {rect}` already ship.
        // The same short-circuit is why a NAMED area with zero cells is refused
        // at argument time unless `allow_empty_area:true`: binding to it is a
        // no-op that looks like a posture.
        //
        // ------------------------- GATES, ONE PER LEVER ----------------------
        //   area       RimWorld/PawnColumnWorker_AllowedArea.cs DoCell, then
        //              Verse/Area.AssignableAsAllowed — reproduced by `AreaGate`,
        //              which `assign` already owns and this verb CALLS rather
        //              than copies.
        //   hostility  RimWorld/PawnColumnWorker_HostilityResponse.cs DoCell
        //              (`pawn.RaceProps.Humanlike`), plus
        //              HostilityResponseModeUtility.DrawResponseButton_GenerateMenu,
        //              whose menu OMITS `Attack` when
        //              `WorkTagIsDisabled(WorkTags.Violent)`.
        //   seek       SeekAndKill/Patch_PawnGetGizmos.ShowsSeekGizmo, CALLED
        //              (the mod's own method is the authority — see this file's
        //              header) with `SeekGateReason` for which clause refused.
        // =====================================================================
        private const string PostureV = "posture";

        // The three verdict populations, named once so the digest and the verb
        // cannot drift. Each is a fact about the pawn, not about the call.
        private sealed class PostureFacts
        {
            public Pawn P;
            public bool Downed, Drafted, Mental, ViolenceCapable, Humanlike;
            public bool RespectsArea, SupportsAreas, ConfigurableHostility;
            public Area Area;            // EffectiveAreaRestrictionInPawnCurrentMap
            public int AreaCells;        // its TrueCount; 0 means the game ignores it
            public string Hostility;     // the raw enum name, or null
            public bool SeekToggled, WillSeek, SeekEligible;
        }

        // Read-only throughout. Every member here is a field read, a dictionary
        // lookup or a bitmask combine: `Pawn.CombinedDisabledWorkTags` and
        // `Pawn_StoryTracker.DisabledWorkTagsBackstoryTraitsAndGenes` both
        // RECOMPUTE and write no cache, unlike `GetDisabledWorkTypes` — which is
        // why the violence test here is the TAG one and not the work-type one.
        private static PostureFacts ReadPosture(Pawn p)
        {
            var f = new PostureFacts { P = p };
            try { f.Downed = p.Downed; } catch { }
            try { f.Drafted = p.Drafted; } catch { }
            try { f.Mental = p.InMentalState; } catch { }
            try { f.Humanlike = p.RaceProps != null && p.RaceProps.Humanlike; } catch { }
            try { f.ViolenceCapable = !p.WorkTagIsDisabled(WorkTags.Violent); } catch { }
            var ps = p.playerSettings;
            if (ps != null)
            {
                try { f.SupportsAreas = ps.SupportsAllowedAreas; } catch { }
                try { f.RespectsArea = ps.RespectsAllowedArea; } catch { }
                try { f.ConfigurableHostility = ps.UsesConfigurableHostilityResponse; } catch { }
                try { f.Hostility = ps.hostilityResponse.ToString(); } catch { }
                // EFFECTIVE, not the raw field: `RespectsAllowedArea` is false
                // for a pawn in a Lord or with a HostFaction, and the game's own
                // `ForbidUtility.InAllowedArea` reads the effective one.
                //
                // PawnSafe CLASS D, guarded explicitly rather than caught:
                // `EffectiveAreaRestrictionInPawnCurrentMap` does
                // `allowedAreas.TryGetValue(pawn.MapHeld, ...)` with NO null
                // check, and Dictionary.TryGetValue(null) throws
                // ArgumentNullException (its sibling
                // AreaRestrictionInPawnCurrentMap has the guard). Every caller
                // here passes spawned pawns, so MapHeld is non-null in practice
                // — but a swallowed throw would report "no area" for a pawn that
                // has one, which is a fabricated answer and not a degraded read.
                try { if (p.MapHeld != null) f.Area = ps.EffectiveAreaRestrictionInPawnCurrentMap; }
                catch { }
                try { f.AreaCells = f.Area != null ? f.Area.TrueCount : 0; } catch { }
            }
            if (SeekMod.Present)
            {
                f.SeekToggled = SeekMod.IsToggled(p);
                f.WillSeek = SeekMod.WillSeek(p);
                f.SeekEligible = SeekMod.ShowsGizmo(p);
            }
            return f;
        }

        // The game's own test for "this restriction does anything":
        // ForbidUtility.InAllowedArea ignores an area whose TrueCount is 0.
        private static bool AreaBinds(PostureFacts f) => f.Area != null && f.AreaCells > 0;

        // ------------------------------------------------------------------
        // WHAT THE PAWN WILL ACTUALLY DO ON CONTACT.
        //
        // The issue asked whether this can be published honestly. It can,
        // because the order is deterministic and every input is already
        // published. The resolution below is the decision order, first match
        // wins, with the member that decides named in `why`:
        //
        // FOUR OF THESE ALSO TURN ON `TryGiveJob`'s PRE-SWITCH BAILS, which run
        // before the hostility mode is even read: `playerSettings == null ||
        // !UsesConfigurableHostilityResponse`, `PawnUtility
        // .PlayerForcedJobNowOrSoon`, `pawn.Downed`, and (Anomaly) a
        // `LordJob_PsychicRitual` lord. A verdict that leaned only on the seek
        // side would be citing half the reason.
        //
        //   1 downed            Humanlike root index 2 is
        //                       `ThinkNode_Subtree(Downed)`, above the index-11
        //                       insertion point, and ThinkTreeInjector skips the
        //                       Downed tree outright. The constant node is inert
        //                       too: `TryGiveJob` returns null on `pawn.Downed`
        //                       before the switch.
        //   2 mental-break      `SeekRegistry.ShouldSeek` requires
        //                       `!InMentalState`; so does
        //                       `ThinkNode_ConditionalCanDoConstantThinkTreeJobNow`.
        //   3 player-controlled Both of those also require `!Drafted`, and a
        //                       drafted order is `playerForced`, which
        //                       `PlayerForcedJobNowOrSoon` reads off `CurJob`
        //                       (else the head of `jobs.jobQueue`) to bail
        //                       `TryGiveJob` before the switch.
        //   4 flee              `JobGiver_ConfigurableHostilityResponse` in the
        //                       CONSTANT tree, which runs before the main tree
        //                       and interrupts it every 30 ticks. TRIGGER is
        //                       `SelfDefenseUtility.ShouldStartFleeing` (8 cells,
        //                       LOS on the AttackTarget branch); the DESTINATION
        //                       is scored against every threat, distance and LOS
        //                       both OFF. See the header.
        //   5 attack-then-seek  hostility Attack and `ShouldSeek` passes: the
        //                       constant node engages inside its radius, seek
        //                       takes everything beyond it.
        //   6 attack-nearby     hostility Attack, seek off or absent. Radius is
        //                       8 for melee, else
        //                       `Clamp(EffectiveRange * 0.66, 2, 20)`.
        //   7 seek-only         the constant node returns null (Ignore, or
        //                       `!UsesConfigurableHostilityResponse`) and
        //                       `ShouldSeek` passes.
        //   8 ignore            nothing above fires.
        //
        // WHAT IT DOES NOT MODEL, said rather than implied: this is the STANDING
        // posture — the answer for an awake, idle pawn. A sleeping pawn is
        // handled at `ThinkNode_ConditionalLyingDown` (root index 0) and
        // `ThinkNode_ConditionalCanDoConstantThinkTreeJobNow` requires
        // `pawn.Awake()`, so contact wakes it first and the verdict then applies;
        // `PawnUtility.PlayerForcedJobNowOrSoon` also nulls the constant node
        // while a forced job runs. Neither is a standing state and neither is
        // published as one, because a field that flickers with the day/night
        // cycle is not a posture.
        // THE VOCABULARY IS CLOSED AND EVERY VERDICT IS ALWAYS PUBLISHED, ZERO
        // INCLUDED. A count that appears only when non-zero would make
        // `posture.on_contact.flee` a path that resolves today and refuses
        // tomorrow — and session 19 ruled that an unresolvable path is a REFUSAL
        // at arm time, so a predicate armed on a colony that had a fleer would
        // stop arming the moment it did not. Eight small integers is the price
        // of a predicate that keeps working.
        internal static readonly string[] ContactVerdicts =
            { "downed", "mental-break", "player-controlled", "flee",
              "attack-then-seek", "attack-nearby", "seek-only", "ignore" };

        private static string OnContact(PostureFacts f, out string why)
        {
            if (f.Downed)
            {
                why = "downed — the Downed subtree is at Humanlike root index 2, above the "
                    + "index-11 seek insertion, and SeekAndKill/ThinkTreeInjector skips that tree. "
                    + "The hostility response is inert too: JobGiver_ConfigurableHostilityResponse"
                    + ".TryGiveJob returns null on pawn.Downed BEFORE it reads the mode";
                return "downed";
            }
            if (f.Mental)
            {
                why = "in a mental state — SeekRegistry.ShouldSeek requires !InMentalState and so "
                    + "does ThinkNode_ConditionalCanDoConstantThinkTreeJobNow";
                return "mental-break";
            }
            if (f.Drafted)
            {
                why = "DRAFTED — you are driving this pawn. ShouldSeek requires !Drafted and "
                    + "ThinkNode_ConditionalCanDoConstantThinkTreeJobNow requires !Drafted; and a "
                    + "drafted order is playerForced, which PawnUtility.PlayerForcedJobNowOrSoon "
                    + "reads off CurJob (else the head of jobs.jobQueue) to bail "
                    + "JobGiver_ConfigurableHostilityResponse.TryGiveJob before it reads the mode. "
                    + "So neither seek nor the hostility response decides anything while the draft "
                    + "holds";
                return "player-controlled";
            }
            bool attack = f.ConfigurableHostility
                && string.Equals(f.Hostility, "Attack", StringComparison.OrdinalIgnoreCase);
            bool flee = f.ConfigurableHostility
                && string.Equals(f.Hostility, "Flee", StringComparison.OrdinalIgnoreCase);

            if (flee)
            {
                // THE TRIGGER AND THE DESTINATION ARE DIFFERENT CALLS with
                // different flags, and this string named the ATTACK branch's
                // numbers until 2026-09-01. TryGetFleeJob's own three
                // ShouldFleeFrom calls pass checkDistance:false, checkLOS:false;
                // the 8 cells and the sight test are one level up, in the
                // ShouldStartFleeing gate it opens with.
                why = "hostility_response is Flee, and that is decided ABOVE seek: "
                    + "JobGiver_ConfigurableHostilityResponse lives in the HumanlikeConstant tree, "
                    + "which Pawn_JobTracker.DetermineNextJob runs BEFORE the main tree and "
                    + "JobTrackerTickInterval re-runs every 30 ticks with JobCondition."
                    + "InterruptForced. TryGetFleeJob opens on "
                    + "SelfDefenseUtility.ShouldStartFleeing, which is the TRIGGER and the only "
                    + "place distance and sight are tested: ShouldFleeFrom(checkDistance:true, "
                    + "checkLOS:false) over ThingRequestGroup.AlwaysFlee, and "
                    + "(checkDistance:true, checkLOS:true) over ThingRequestGroup.AttackTarget in a "
                    + "9-region BreadthFirstTraverse, where checkDistance:true is "
                    + "InHorDistOf(pawn.Position, 8f). Once it fires, the DESTINATION is a "
                    + "different question: TryGetFleeJob re-gathers threats with "
                    + "checkDistance:false and checkLOS:false at all three of its call sites and "
                    + "passes the lot to CellFinderLoose.GetFleeDest, so the pawn runs from EVERY "
                    + "hostile the caches hold, not just the one that set it off"
                    + (f.WillSeek
                        ? ". SEEK IS ON AND LOSES. That is the M1 state that killed two "
                          + "colonists: one crow inside 8 cells triggered a flee scored against "
                          + "the whole map, and Captain ran 150 cells into unexplored ground."
                        : ".");
                return "flee";
            }
            if (attack && !f.ViolenceCapable)
            {
                // Reachable only via a mod or a hand-edited save: the dropdown
                // omits Attack for such a pawn and `assign` refuses it.
                why = "hostility_response is Attack but this pawn is incapable of Violent work, and "
                    + "JobGiver_ConfigurableHostilityResponse.TryGetAttackNearbyEnemyJob opens with "
                    + "WorkTagIsDisabled(WorkTags.Violent) and returns null — so the setting does "
                    + "nothing at all";
                return "ignore";
            }
            if (attack && f.WillSeek)
            {
                why = "hostility_response is Attack (the constant tree engages a target within 8 "
                    + "cells for melee, else Clamp(EffectiveRange * 0.66, 2, 20)) AND "
                    + "SeekRegistry.ShouldSeek passes, so the squad brain takes everything the "
                    + "close-range node declines. This is the posture the checklist asks for";
                return "attack-then-seek";
            }
            if (attack)
            {
                why = "hostility_response is Attack, seek is " + (SeekMod.Present ? "off" : "absent")
                    + " — the pawn fights what comes to it (within 8 cells for melee, else "
                    + "Clamp(EffectiveRange * 0.66, 2, 20)) and goes nowhere to find it";
                return "attack-nearby";
            }
            if (f.WillSeek)
            {
                why = (f.ConfigurableHostility
                        ? "hostility_response is Ignore, so JobGiver_ConfigurableHostilityResponse "
                          + "returns null and control reaches seek"
                        : "this pawn does not use the configurable hostility response "
                          + "(Pawn_PlayerSettings.UsesConfigurableHostilityResponse is false — a "
                          + "guest, or a pawn with a HostFaction), so seek is the only thing deciding")
                    + ". SeekRegistry.ShouldSeek passes: the squad brain drives it";
                return "seek-only";
            }
            why = "nothing will make this pawn fight: "
                + (f.ViolenceCapable ? "" : "it is incapable of Violent work; ")
                + "hostility_response is " + (f.Hostility ?? "null")
                + (SeekMod.Present
                    ? (f.SeekToggled ? " and seek is toggled but ShouldSeek does not pass" : " and seek is off")
                    : " and SeekAndKill is not loaded");
            return "ignore";
        }

        // ------------------------------------------------------------------
        // posture {area, pawns?, seek?, hostility?, dry_run?, allow_empty_area?}
        // ------------------------------------------------------------------
        [Verb("posture")]
        public static object Posture(VerbContext ctx)
        {
            var map = Map();
            var a = ctx.Args;
            bool dryRun = a.Bool("dry_run", false);

            bool wantArea = a.Has("area");
            bool wantSeek = a.Has("seek");
            bool wantHostility = a.Has("hostility");
            bool readOnly = !wantArea && !wantSeek && !wantHostility;

            // `pawns` defaults to the whole roster: the posture is a property of
            // the COLONY, and a per-pawn default would make "did I do all of
            // them" the caller's problem again.
            var pawns = a.Has("pawns") || a.Has("pawn")
                ? PawnList(map, a)
                : PawnList(map, new VerbArgs(new Dictionary<string, object> { ["pawns"] = "colonists" }));

            Area area = null;
            bool areaUnrestricted = false;
            if (!readOnly)
            {
                if (!wantArea)
                    throw new VerbArgsException(
                        "posture is THREE settings that must agree, and a posture with two of them is "
                        + "the bug this verb exists to remove — pass `area` (an area id or label), or "
                        + "`area:null` to DECLARE unrestricted deliberately. Call `posture` with no "
                        + "arguments at all for a pure read.");
                area = FindArea(map, a.Raw("area"));
                areaUnrestricted = area == null;
                if (area != null)
                {
                    int cells = 0;
                    try { cells = area.TrueCount; } catch { }
                    if (cells == 0 && !a.Bool("allow_empty_area", false))
                        throw new VerbArgsException(
                            $"area '{Safe(() => area.Label) ?? "?"}' has ZERO cells, and "
                            + "RimWorld/ForbidUtility.InAllowedArea short-circuits on `TrueCount > 0` "
                            + "— binding pawns to it restricts nothing while this verb would report "
                            + "them bound. Paint it first (`area {kind:\"allowed\", op:\"add\", id:"
                            + area.ID + ", rect:[…]}`), or pass allow_empty_area:true to bind anyway.");
                }
            }

            // seek is tri-state, read raw like `seek-at-will`'s `on`.
            bool autoSeek = false, seekValue = true;
            if (wantSeek)
            {
                object raw = a.Raw("seek");
                if (raw is bool b) seekValue = b;
                else if (raw is string s && s.Equals("auto", StringComparison.OrdinalIgnoreCase)) autoSeek = true;
                else throw new VerbArgsException("seek must be true, false, or \"auto\"");
            }
            var hostility = wantHostility ? Hostility(a.Str("hostility")) : HostilityResponseMode.Attack;

            var outcome = new Outcome();
            var ids = new List<object>();
            var incapable = new List<object>();
            var levers = new List<object>();
            if (!readOnly) { levers.Add("area"); levers.Add("seek"); levers.Add("hostility"); }

            foreach (var p in pawns)
            {
                var before = ReadPosture(p);
                var line = new Dictionary<string, object>
                {
                    ["class"] = PawnSafe.Classify(p),
                    ["violence_capable"] = before.ViolenceCapable,
                    ["before"] = PostureRow(before),
                };
                // NAMED, NOT SKIPPED. A pawn who cannot take the posture is a
                // real answer — this is b1b3060's second acceptance bullet, and
                // the name goes in the headline as well as the row so a caller
                // can branch without walking `accepted`.
                if (!before.ViolenceCapable)
                    incapable.Add(new Dictionary<string, object>
                    {
                        ["pawn"] = p.thingIDNumber,
                        ["name"] = PawnSafe.Name(p),
                        ["gate"] = "violent-disabled",
                        ["reason"] = "incapable of Violent work: SeekAndKill/Patch_PawnGetGizmos"
                            + ".ShowsSeekGizmo refuses it and HostilityResponseModeUtility's own "
                            + "dropdown omits Attack for it (WorkTagIsDisabled(WorkTags.Violent)). "
                            + "The area still binds; this pawn will never fight.",
                    });

                if (readOnly)
                {
                    string whyRead;
                    line["on_contact"] = OnContact(before, out whyRead);
                    line["on_contact_why"] = whyRead;
                    outcome.Ok(p, line);
                    continue;
                }

                var applied = new List<object>();
                var refused = new List<object>();

                // ---- area. `AreaGate` is `assign`'s, called not copied. ------
                One(p, "area", applied, refused, () =>
                {
                    string why = AreaGate(p, area);
                    if (why != null) return why;
                    if (dryRun) return null;
                    // Same setter `assign` uses, and it can END the pawn's
                    // current job when a target falls outside the new area
                    // (RimWorld/Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap).
                    p.playerSettings.AreaRestrictionInPawnCurrentMap = area;
                    return null;
                });

                // ---- hostility. The load-bearing setting, not the backstop. --
                One(p, "hostility", applied, refused, () =>
                {
                    if (p.playerSettings == null) return "this pawn has no player settings";
                    if (!before.Humanlike)
                        return "the hostility-response column is drawn for humanlikes only "
                            + "(RimWorld/PawnColumnWorker_HostilityResponse.DoCell)";
                    if (hostility == HostilityResponseMode.Attack && !before.ViolenceCapable)
                        return "the game does not offer Attack to a pawn incapable of violence "
                            + "(HostilityResponseModeUtility.DrawResponseButton_GenerateMenu omits it)";
                    if (!before.ConfigurableHostility)
                        return "this pawn does not use the configurable hostility response "
                            + "(Pawn_PlayerSettings.UsesConfigurableHostilityResponse) — the field "
                            + "would be set and JobGiver_ConfigurableHostilityResponse would ignore it";
                    if (dryRun) return null;
                    p.playerSettings.hostilityResponse = hostility;
                    return null;
                });

                // ---- seek. -----------------------------------------------
                One(p, "seek", applied, refused, () =>
                {
                    if (!SeekMod.Present)
                        return SeekMod.Missing ?? "SeekAndKill is not loaded";
                    if (!before.SeekEligible)
                    {
                        string gate = SeekGateReason(p);
                        line["seek_gate"] = gate;
                        return SeekGateText(gate);
                    }
                    bool target = seekValue;
                    if (autoSeek)
                    {
                        target = AutoOn(p, out string whyOn);
                        line["seek_auto_reason"] = whyOn;
                    }
                    if (target == before.SeekToggled)
                        return "already " + (target ? "seeking" : "not seeking");
                    if (dryRun) return null;
                    // Toggle is a FLIP through the synced path, and
                    // SyncedToggleSeek re-checks the gizmo gate and returns
                    // SILENTLY on failure — so the write is read back.
                    string err = SeekMod.Toggle(p);
                    if (err != null) return err;
                    bool now = SeekMod.IsToggled(p);
                    if (now != target)
                        return "the write did not take: asked for toggled=" + target
                            + " and SeekRegistry.IsToggled still answers " + now;
                    return null;
                });

                var after = dryRun ? before : ReadPosture(p);
                line["applied"] = applied;
                line["refused"] = refused;
                line["after"] = PostureRow(after);
                // The dry-run projection is NOT read back, so say which it is
                // rather than letting `after` read as an observation.
                if (dryRun) line["after_is"] = "the BEFORE state — dry_run wrote nothing and read nothing back";
                string whyContact;
                line["on_contact"] = OnContact(after, out whyContact);
                line["on_contact_why"] = whyContact;

                if (applied.Count > 0)
                {
                    outcome.Ok(p, line);
                    ids.Add(p.thingIDNumber);
                }
                else
                {
                    outcome.No(p, "all-refused", "no lever of the posture applied to this pawn", line);
                }
            }

            long seq = (!dryRun && !readOnly)
                ? ActOn(outcome, PostureV, "posture",
                        (areaUnrestricted ? "unrestricted" : (Safe(() => area.Label) ?? "?"))
                        + " x" + ids.Count,
                        new Dictionary<string, object>
                        {
                            ["ids"] = ids,
                            ["levers"] = levers,
                            ["hostility"] = hostility.ToString(),
                            ["seek"] = autoSeek ? (object)"auto" : seekValue,
                        })
                : 0;

            var extra = new Dictionary<string, object>
            {
                ["mode"] = readOnly ? "read" : (dryRun ? "dry-run" : "write"),
                ["levers"] = levers,
                ["area"] = readOnly ? null
                    : (areaUnrestricted ? null : (object)Safe(() => area.Label)),
                ["area_id"] = readOnly || areaUnrestricted ? null : (object)area.ID,
                ["area_cells"] = readOnly || areaUnrestricted ? null : (object)SafeObj(() => (object)area.TrueCount),
                ["hostility"] = readOnly ? null : hostility.ToString(),
                ["seek"] = readOnly ? null : (autoSeek ? (object)"auto" : seekValue),
                ["dry_run"] = dryRun,
                // The headline, so the second acceptance bullet is answerable
                // without walking rows.
                ["incapable_of_violence"] = incapable,
                ["posture"] = PostureSection(map),
                ["note"] = "hostility_response is NOT a backstop to seek — it is decided ABOVE it. "
                    + "JobGiver_ConfigurableHostilityResponse is in the HumanlikeConstant tree, which "
                    + "SeekAndKill/ThinkTreeInjector never injects into (its root has none of the four "
                    + "anchor nodes), and Pawn_JobTracker runs the constant tree BEFORE the main tree "
                    + "and re-runs it every 30 ticks with InterruptForced. `on_contact` is the "
                    + "resolution of that order per pawn. Assigning an area can END a pawn's current "
                    + "job when a target falls outside it.",
            };
            if (readOnly || dryRun) extra["action"] = NoStamp();
            return outcome.Result(PostureV, seq, extra);
        }

        private static Dictionary<string, object> PostureRow(PostureFacts f)
            => new Dictionary<string, object>
            {
                ["area"] = f.Area == null ? null : Safe(() => f.Area.Label),
                ["area_cells"] = f.Area == null ? (object)null : f.AreaCells,
                // The game's own test, not ours: an area with no cells is
                // ignored by ForbidUtility.InAllowedArea.
                ["area_binds"] = AreaBinds(f),
                ["respects_area"] = f.RespectsArea,
                ["hostility_response"] = f.Hostility,
                ["configurable_hostility"] = f.ConfigurableHostility,
                ["seek_toggled"] = SeekMod.Present ? (object)f.SeekToggled : null,
                ["will_seek"] = SeekMod.Present ? (object)f.WillSeek : null,
                ["seek_eligible"] = SeekMod.Present ? (object)f.SeekEligible : null,
            };

        // ------------------------------------------------------------------
        // THE DIGEST BLOCK (b1b3060). The standing state at every read, instead
        // of inferred from a field whose meaning the M1 run got backwards.
        //
        // CHEAP ON THE AXIS SESSION 19's PREDICATE-COST DECISION CARES ABOUT:
        // no `Room.Role`, no `GetStatValueAbstract`, no pathfind. It is one
        // snapshot of `FreeColonistsSpawned` and, per pawn, field reads, a
        // dictionary lookup (`allowedAreas`), a `GetLord()` walk, a bitmask
        // combine (`CombinedDisabledWorkTags`, which writes no cache) and — when
        // SeekAndKill is present — three cached-MethodInfo invokes whose bodies
        // are seven field reads and a HashSet lookup. So it IS registered as a
        // predicate section, and `posture.ok == false` is the halt an agent
        // wants: "stop when the colony stops holding its combat posture".
        //
        // THE DENOMINATORS ARE DIFFERENT ON PURPOSE and are published in words.
        // `area_bound` is over pawns that support area restriction at all, since
        // the area is not a combat setting; `will_seek` and `attack` are over
        // VIOLENCE-CAPABLE pawns, because the game refuses both to the others
        // and counting them would make a correct colony look under-postured
        // forever.
        //
        // n/m IS PUBLISHED AS A STRING **AND** AS INTEGERS. The issue asks for
        // "n/m", which is the glance; a predicate cannot use it, because
        // `advance {until:{condition}}` refuses `<` on a string rather than
        // coercing it (session 19). So `will_seek` is "2/3" and `will_seek_n` /
        // `will_seek_of` are the numbers.
        internal static Dictionary<string, object> PostureSection(Map map)
        {
            if (map == null) return null;
            try
            {
                // SNAPSHOT before iterating — FreeColonistsSpawned CLEARS and
                // rebuilds one cached List on every access (DigestVerb
                // .ColonistSection's header).
                var colonists = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
                int areaOf = 0, areaN = 0, violent = 0, seekN = 0, attackN = 0;
                var byContact = new Dictionary<string, int>();
                for (int v = 0; v < ContactVerdicts.Length; v++) byContact[ContactVerdicts[v]] = 0;
                var fleeRisk = new List<object>();
                var areas = new Dictionary<string, int>();
                bool seekPresent = SeekMod.Present;

                for (int i = 0; i < colonists.Count; i++)
                {
                    var p = colonists[i];
                    if (p == null) continue;
                    var f = ReadPosture(p);

                    if (f.SupportsAreas)
                    {
                        areaOf++;
                        if (AreaBinds(f))
                        {
                            areaN++;
                            string label = Safe(() => f.Area.Label) ?? "?";
                            areas[label] = areas.TryGetValue(label, out var n) ? n + 1 : 1;
                        }
                    }
                    if (f.ViolenceCapable)
                    {
                        violent++;
                        if (f.WillSeek) seekN++;
                        if (string.Equals(f.Hostility, "Attack", StringComparison.OrdinalIgnoreCase)
                            && f.ConfigurableHostility) attackN++;
                    }

                    string why;
                    string contact = OnContact(f, out why);
                    byContact[contact] = byContact.TryGetValue(contact, out var c) ? c + 1 : 1;

                    // THE M1 STATE, NAMED. A violence-capable pawn set to Flee
                    // runs whatever seek says, because the constant tree decides
                    // first — triggered by a threat inside 8 cells
                    // (SelfDefenseUtility.ShouldStartFleeing), then fleeing a
                    // destination scored against EVERY threat, distance and LOS
                    // both off (TryGetFleeJob -> CellFinderLoose.GetFleeDest).
                    if (f.ViolenceCapable && contact == "flee")
                        fleeRisk.Add(new Dictionary<string, object>
                        {
                            ["name"] = PawnSafe.Name(p),
                            ["pawn"] = p.thingIDNumber,
                            ["will_seek"] = seekPresent ? (object)f.WillSeek : null,
                        });
                }

                var areaList = new List<object>();
                foreach (var kv in areas) areaList.Add(kv.Key + " x" + kv.Value);
                areaList.Sort((x, y) => string.CompareOrdinal((string)x, (string)y));
                // In the vocabulary's own order, not the dictionary's hash
                // order — the 2.6 nit about `threats.kinds`, which took the
                // first three in enumeration order and called them top-3.
                var contactCounts = new Dictionary<string, object>();
                for (int v = 0; v < ContactVerdicts.Length; v++)
                    contactCounts[ContactVerdicts[v]] = byContact[ContactVerdicts[v]];

                bool ok = violent > 0 && seekN == violent && attackN == violent
                          && areaOf > 0 && areaN == areaOf && fleeRisk.Count == 0;

                return new Dictionary<string, object>
                {
                    // The headline a predicate branches on.
                    ["ok"] = ok,
                    ["will_seek"] = seekN + "/" + violent,
                    ["will_seek_n"] = seekN,
                    ["will_seek_of"] = violent,
                    ["area_bound"] = areaN + "/" + areaOf,
                    ["area_bound_n"] = areaN,
                    ["area_bound_of"] = areaOf,
                    // The field that actually decides, published beside the two
                    // the issue named — see this file's investigation header.
                    ["attack"] = attackN + "/" + violent,
                    ["attack_n"] = attackN,
                    ["attack_of"] = violent,
                    ["colonists"] = colonists.Count,
                    ["areas"] = areaList,
                    // WHAT THEY WILL DO, not what a field says.
                    ["on_contact"] = contactCounts,
                    ["flee_risk"] = fleeRisk,
                    ["seek_mod"] = seekPresent,
                    ["seek_mod_missing"] = seekPresent ? null : (SeekMod.Missing ?? "SeekAndKill is not loaded"),
                    ["denominators"] = "`will_seek` and `attack` are over VIOLENCE-CAPABLE free "
                        + "colonists (the game refuses both to the others); `area_bound` is over free "
                        + "colonists whose Pawn_PlayerSettings.SupportsAllowedAreas is true, and a "
                        + "pawn counts as bound only when its EFFECTIVE area has TrueCount > 0, "
                        + "because RimWorld/ForbidUtility.InAllowedArea ignores an empty one.",
                    ["note"] = "`on_contact` is the resolved decision order, not a field echo: "
                        + "JobGiver_ConfigurableHostilityResponse is in the HumanlikeConstant tree, "
                        + "which runs BEFORE the main tree and which SeekAndKill does not inject "
                        + "into, so `Flee` BEATS seek. `flee_risk` names every violence-capable pawn "
                        + "in that state. Repair with `posture {area:…}`.",
                };
            }
            catch (Exception e)
            {
                return new Dictionary<string, object>
                {
                    ["error"] = e.GetType().Name + ": " + Journal.Truncate(e.Message, 160),
                };
            }
        }
    }
}
