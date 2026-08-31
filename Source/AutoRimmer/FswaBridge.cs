using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AutoRimmer
{
    // ================================================= git-bug 1a072fa ========
    // MOD-AWARE BRIDGE — FindSuitableWeaponAndAmmo (dorian.findsuitableweaponandammo).
    //
    // FSWA is per-pawn opt-in auto-arming: an opted-in pawn equips the best
    // available weapon and re-arms when disarmed. It is NOT a dependency of
    // this mod and must never become one — AutoRimmer loads against benches
    // that do not have it — so nothing here takes a compile-time reference.
    // Every member is resolved by name and SIGNATURE at first use and latched.
    //
    // -------------------- THE FOUR RULES THIS FILE IS JUDGED ON --------------
    //
    // 1. BIND BY SIGNATURE, NOT BY NAME. `AccessTools.Method(t, "SetAutoArm")`
    //    with no parameter list happily binds a method whose arguments have
    //    changed and then throws at INVOKE time — i.e. in the middle of a
    //    player verb, on a bench, hours after the drift landed. Every lookup
    //    below passes the parameter-type array AND checks the return type, and
    //    the resolved MethodInfo is then turned into a typed delegate, which
    //    makes the CLR re-verify the whole signature a third time.
    //
    // 2. A REFLECTION THROW IS NEVER A LEGAL STATE VALUE. `IsAutoArm` answers
    //    `false` for a pawn that is simply not opted in, so `catch { return
    //    false; }` on the read would make a BROKEN bridge indistinguishable
    //    from a working one reporting "auto-arm is off" — the worst kind of
    //    wrong, because it reads as data. The read returns `bool?`: null means
    //    WE COULD NOT LOOK, it is journaled as a warning, and the reason
    //    travels with it to the caller.
    //
    //    And the trap has a SECOND MOUTH that no `catch` can cover: FSWA's own
    //    `IsAutoArm` is `Get?.optedIn.Contains(pawn) ?? false`, so a missing
    //    `AutoArmTracker` GameComponent answers `false` WITHOUT throwing. A
    //    bare `false` is therefore probed against `AutoArmTracker.Get` before
    //    it is published; a definite absence becomes null, and only a definite
    //    one, because turning every honest "not opted in" into UNKNOWN would be
    //    the same lie facing the other way. See `AutoArmOf`.
    //
    // 3. READ THE WRITE BACK. `SetAutoArm` returns `void` and bails SILENTLY on
    //    a null tracker and on a non-Configurable pawn (dead/destroyed), so "the
    //    invoke did not throw" is no evidence at all that anything changed.
    //    Callers set, then read, then compare, and a disagreement is a
    //    REJECTION with a diagnosis — never a success.
    //
    // 4. ABSENCE IS NOT AN ERROR. A bench without FSWA answers "the lever is
    //    unavailable, here is why", by name, and the rest of the call proceeds.
    //
    // --------------------------- MULTIPLAYER ---------------------------------
    // Checked, as the spec asked. `MpSync.cs` does NOT wrap `SetAutoArm` in a
    // separate synced entry point that we could call instead: `MpSync.RegisterAll`
    // registers `AutoArmTracker.SetAutoArm(Pawn, bool)` ITSELF with
    // `MP.RegisterSyncMethod`, deliberately, because all of FSWA's own callers
    // (the gizmo's toggleAction and the Assign-tab column's SetValue) funnel
    // through that one static setter. So the setter IS the sync site and calling
    // it directly is both the only route and the correct one.
    //
    // The honest caveat, which is FSWA's design rather than a defect of this
    // bridge: Multiplayer's prefix only fires in INTERFACE context
    // (`Multiplayer.ShouldSync` requires `InInterface` — see FSWA/MpBridge.cs's
    // "What registration buys"). AutoRimmer's verbs run from
    // `AgentGameComponent.GameComponentUpdate`, which is not interface context,
    // so under Multiplayer this write would execute locally and NOT be shipped
    // to the other clients. AutoRimmer is a single-player agent bench and MP is
    // out of its scope; recorded here so nobody has to rediscover it. FSWA's
    // own `MpSync` header already says none of its sync has been confirmed in a
    // running two-client session either.
    //
    // Main thread only. Like every other Verse-touching file here, this is
    // called from the command drain at a safe point; the file half of the
    // bridge never reaches it, so the latch needs no lock.
    internal static class Fswa
    {
        public const string ModName = "FindSuitableWeaponAndAmmo";
        public const string PackageId = "dorian.findsuitableweaponandammo";

        private const string TrackerType = "FSWA.AutoArmTracker";
        private const string MechType = "FSWA.MechUtility";

        private static bool resolved;
        private static Func<Pawn, bool> readAutoArm;
        private static Action<Pawn, bool> writeAutoArm;
        private static MethodInfo trackerGetter;      // AutoArmTracker.Get — diagnosis only
        private static Func<Pawn, bool> weaponUsableMech;
        private static string autoArmWhyNot;          // null once the two core members bind
        private static string mechWhyNot;

        // ------------------------------------------------------------------ probe

        /// True only when BOTH the read and the write bound. A half-bound bridge
        /// is unavailable: a lever that can set but not verify is exactly the
        /// thing rule 3 exists to prevent.
        public static bool AutoArmAvailable
        {
            get { Resolve(); return readAutoArm != null && writeAutoArm != null; }
        }

        /// Why the lever is unavailable, in words that name the mod. Null when
        /// it IS available.
        public static string AutoArmUnavailable
        {
            get { Resolve(); return AutoArmAvailable ? null : autoArmWhyNot; }
        }

        private static void Resolve()
        {
            if (resolved) return;
            // Latched BEFORE the work: a probe that throws must not re-run on
            // every frame of every subsequent call.
            resolved = true;
            try
            {
                Type tracker = AccessTools.TypeByName(TrackerType);
                if (tracker == null)
                {
                    // The ordinary case on most benches, and not a fault: no
                    // warning, no log, just a reason a caller can print.
                    autoArmWhyNot = ModName + " (" + PackageId + ") is not loaded — no type "
                        + TrackerType + " in any loaded assembly. Auto-arm is that mod's feature; "
                        + "with it absent there is nothing to toggle.";
                    mechWhyNot = autoArmWhyNot;
                    return;
                }

                var drift = new List<string>();
                MethodInfo read = Bind(tracker, "IsAutoArm", typeof(bool), new[] { typeof(Pawn) }, drift);
                MethodInfo write = Bind(tracker, "SetAutoArm", typeof(void), new[] { typeof(Pawn), typeof(bool) }, drift);
                if (read != null) readAutoArm = (Func<Pawn, bool>)Delegate.CreateDelegate(typeof(Func<Pawn, bool>), read);
                if (write != null) writeAutoArm = (Action<Pawn, bool>)Delegate.CreateDelegate(typeof(Action<Pawn, bool>), write);

                if (readAutoArm == null || writeAutoArm == null)
                {
                    // Loaded but changed shape. That IS a fault and it is loud:
                    // silence here is how a bridge rots for a release without
                    // anyone noticing.
                    autoArmWhyNot = ModName + " is loaded but its auto-arm API has drifted, so this lever "
                        + "is disabled rather than guessed at: " + string.Join("; ", drift.ToArray());
                    Log.Warning("[AutoRimmer] " + autoArmWhyNot);
                    Journal.EmitWarning("[AutoRimmer] " + autoArmWhyNot);
                    return;
                }

                // THE OPTIONAL PROBES, IN THEIR OWN GUARD. Both comments below
                // promise that failing to bind these costs the EXPLANATION and
                // not the lever — but the outer `catch` nulls `readAutoArm` and
                // `writeAutoArm`, so without this inner guard a throw down here
                // would break exactly the promise they make, disabling a lever
                // whose two core members had already bound cleanly.
                try
                {
                    // Diagnosis only, and OPTIONAL: `AutoArmTracker.Get` is what
                    // SetAutoArm's first clause tests, so a null tracker is the
                    // single most likely explanation for a write that returns
                    // normally and does nothing. Failing to bind it costs us the
                    // explanation, not the lever.
                    trackerGetter = AccessTools.PropertyGetter(tracker, "Get");

                    // The gizmo's second route (see the gate in PawnManageVerbs).
                    // Also optional: without it a MECHANOID is refused with a named
                    // reason, which is not the same as being told auto-arm is off.
                    Type mech = AccessTools.TypeByName(MechType);
                    if (mech == null)
                    {
                        mechWhyNot = "no type " + MechType + " in the loaded " + ModName + " assembly";
                    }
                    else
                    {
                        var mechDrift = new List<string>();
                        MethodInfo usable = Bind(mech, "IsWeaponUsableMech", typeof(bool), new[] { typeof(Pawn) }, mechDrift);
                        if (usable != null)
                            weaponUsableMech = (Func<Pawn, bool>)Delegate.CreateDelegate(typeof(Func<Pawn, bool>), usable);
                        else
                            mechWhyNot = string.Join("; ", mechDrift.ToArray());
                    }
                }
                catch (Exception probe)
                {
                    trackerGetter = null;
                    weaponUsableMech = null;
                    mechWhyNot = "probing " + ModName + "'s optional members threw: "
                        + probe.GetType().Name + ": " + Journal.Truncate(probe.Message, 160);
                    Journal.EmitWarning("[AutoRimmer] FSWA optional probes failed; the auto-arm lever "
                        + "still works but a mechanoid refusal and the write diagnosis lose their "
                        + "detail. " + mechWhyNot);
                }
            }
            catch (Exception e)
            {
                readAutoArm = null;
                writeAutoArm = null;
                autoArmWhyNot = "probing " + ModName + " threw, so the auto-arm lever is disabled: "
                    + e.GetType().Name + ": " + Journal.Truncate(e.Message, 200);
                Log.Warning("[AutoRimmer] " + autoArmWhyNot);
                Journal.EmitWarning("[AutoRimmer] " + autoArmWhyNot);
            }
        }

        /// Name AND parameter types AND return type. `AccessTools.Method` with a
        /// type array is `GetMethod(name, types)` underneath, which will not
        /// match a changed parameter list — the point of passing it. The return
        /// check is the half `GetMethod` cannot do: an `IsAutoArm` that started
        /// answering an enum would bind fine here and blow up at the cast.
        private static MethodInfo Bind(Type owner, string name, Type ret, Type[] args, List<string> drift)
        {
            MethodInfo m = AccessTools.Method(owner, name, args);
            if (m == null)
            {
                drift.Add("no " + owner.FullName + "." + name + "(" + Params(args) + ")");
                return null;
            }
            if (!m.IsStatic)
            {
                drift.Add(owner.FullName + "." + name + " is no longer static");
                return null;
            }
            if (m.ReturnType != ret)
            {
                drift.Add(owner.FullName + "." + name + " now returns " + m.ReturnType.Name
                    + ", expected " + ret.Name);
                return null;
            }
            return m;
        }

        private static string Params(Type[] args)
        {
            var names = new string[args.Length];
            for (int i = 0; i < args.Length; i++) names[i] = args[i].Name;
            return string.Join(", ", names);
        }

        // ------------------------------------------------------------------ read

        /// Auto-arm for one pawn. `null` means WE COULD NOT LOOK — never "off"
        /// (rule 2) — and `error` says why.
        public static bool? AutoArmOf(Pawn pawn, out string error)
        {
            error = null;
            Resolve();
            if (readAutoArm == null) { error = autoArmWhyNot; return null; }
            if (pawn == null) { error = "no pawn"; return null; }
            try
            {
                // FSWA/AutoArmTracker.cs IsAutoArm: a HashSet lookup on a
                // GameComponent, reached through `Get` => `Current.Game
                // .GetComponent<AutoArmTracker>()`, which is a plain scan over
                // `Game.components` (Verse/Game.cs GetComponent<T>) and creates
                // nothing. No lazy init, no write to scribed state, nothing
                // rebuilt-on-read — which is why the OBSERVER surface is allowed
                // to call it too (DESIGN §Observation model).
                bool v = readAutoArm(pawn);
                if (v) return true;

                // RULE 2, THE HALF A `catch` CANNOT SEE. IsAutoArm's body is
                // `Get?.optedIn.Contains(pawn) ?? false`, so a MISSING
                // AutoArmTracker answers the legal value `false` and never
                // throws — the fabricated-data failure this file exists to
                // prevent, arriving by the one route the try/catch above does
                // not cover. The WRITE path already catches it (the read-back
                // disagrees and WriteDiagnosis names the null tracker); the
                // OBSERVER path did not, and an observer publishing "auto-arm is
                // off" for a game whose tracker is gone is precisely the lie.
                //
                // Only a DEFINITE absence downgrades the answer. A probe we
                // could not run leaves the `false` standing, because turning
                // every ordinary "not opted in" into UNKNOWN would be a worse
                // lie in the other direction. Paid for only on a `false`, which
                // is also the only value that is ambiguous.
                bool? tracker = TrackerPresent(out _);
                if (tracker == false)
                {
                    error = ModName + " is loaded but its AutoArmTracker GameComponent is not on the "
                        + "current Game, and " + TrackerType + ".IsAutoArm answers a plain `false` in "
                        + "that case — so this is NOT evidence that auto-arm is off.";
                    Journal.EmitWarning("[AutoRimmer] FSWA auto-arm READ is UNKNOWN, not off. " + error);
                    return null;
                }
                return false;
            }
            catch (Exception e)
            {
                error = TrackerType + ".IsAutoArm threw: " + e.GetType().Name + ": "
                    + Journal.Truncate(e.Message, 160);
                // Deliberately pawn-free text: Journal.EmitWarning dedupes by
                // exact string, so naming the pawn would burn the distinct-text
                // cap on one bad loop and then go silent for everything else.
                Journal.EmitWarning("[AutoRimmer] FSWA auto-arm READ failed — the state is UNKNOWN, "
                    + "not off. " + error);
                return null;
            }
        }

        /// Sets `into[key]` to true/false/null and, when null, `into[key +
        /// "_unknown"]` to the reason. One helper so the observer and the verb
        /// publish the same shape and a null can never read as a false.
        ///
        /// `inlineReason:false` is for callers that publish `Describe()` at the
        /// result level: the mod-is-absent reason is the SAME long sentence for
        /// every pawn, and repeating it in a before AND an after block for each
        /// row is pure context tax. A PER-PAWN failure — the mod is there and
        /// the read threw — is always inlined regardless, because that one
        /// genuinely differs from pawn to pawn and has nowhere else to go.
        public static void PublishAutoArm(Pawn pawn, Dictionary<string, object> into,
            string key = "auto_arm", bool inlineReason = true)
        {
            bool? v = AutoArmOf(pawn, out string error);
            into[key] = v;
            if (v != null || error == null) return;
            if (inlineReason || AutoArmAvailable) into[key + "_unknown"] = error;
        }

        // ----------------------------------------------------------------- write

        /// Calls FSWA's setter. The bool is only "the invoke completed" — it is
        /// NOT a claim that anything changed. Callers must read back (rule 3).
        public static bool TrySetAutoArm(Pawn pawn, bool value, out string error)
        {
            error = null;
            Resolve();
            if (writeAutoArm == null) { error = autoArmWhyNot; return false; }
            if (pawn == null) { error = "no pawn"; return false; }
            try
            {
                writeAutoArm(pawn, value);
                return true;
            }
            catch (Exception e)
            {
                error = TrackerType + ".SetAutoArm threw: " + e.GetType().Name + ": "
                    + Journal.Truncate(e.Message, 160);
                Journal.EmitWarning("[AutoRimmer] FSWA auto-arm WRITE failed. " + error);
                return false;
            }
        }

        /// Why a write can return normally and change nothing. Called only when
        /// the read-back has already disagreed with what was asked, so it is
        /// paying for itself.
        public static string WriteDiagnosis()
        {
            bool? tracker = TrackerPresent(out string error);
            if (tracker == false)
                return "FSWA's AutoArmTracker GameComponent is not on the current Game "
                    + "(Current.Game.GetComponent<AutoArmTracker>() answered null), and SetAutoArm's "
                    + "first clause returns on exactly that.";
            if (tracker == null)
                return "the tracker could not be probed (" + error + "), so the cause is unknown.";
            return "the tracker IS present, so the cause is inside FSWA's own setter — under Multiplayer "
                + "SetAutoArm is a synced command and this verb does not run in interface context, which "
                + "is the one case this bridge knows of.";
        }

        /// Is FSWA's GameComponent on the current Game? `null` = could not look.
        public static bool? TrackerPresent(out string error)
        {
            error = null;
            Resolve();
            if (trackerGetter == null)
            {
                error = TrackerType + ".Get did not bind";
                return null;
            }
            try { return trackerGetter.Invoke(null, null) != null; }
            catch (Exception e)
            {
                error = TrackerType + ".Get threw: " + e.GetType().Name + ": "
                    + Journal.Truncate(e.Message, 120);
                return null;
            }
        }

        /// FSWA/MechUtility.cs IsWeaponUsableMech. `null` = could not look, which
        /// a caller must report as such rather than treating as "not a mech".
        public static bool? IsWeaponUsableMech(Pawn pawn, out string error)
        {
            error = null;
            Resolve();
            if (weaponUsableMech == null)
            {
                error = mechWhyNot ?? (MechType + ".IsWeaponUsableMech did not bind");
                return null;
            }
            if (pawn == null) { error = "no pawn"; return null; }
            try { return weaponUsableMech(pawn); }
            catch (Exception e)
            {
                error = MechType + ".IsWeaponUsableMech threw: " + e.GetType().Name + ": "
                    + Journal.Truncate(e.Message, 160);
                Journal.EmitWarning("[AutoRimmer] FSWA mech-route probe failed. " + error);
                return null;
            }
        }

        // ---------------------------------------------------------- provenance

        /// What the caller needs to tell "the mod said no" from "we never
        /// reached the mod". Published beside any result that carries an
        /// auto-arm value.
        public static Dictionary<string, object> Describe()
        {
            Resolve();
            var d = new Dictionary<string, object>
            {
                ["mod"] = ModName,
                ["package_id"] = PackageId,
                ["loaded"] = AutoArmAvailable,
            };
            if (!AutoArmAvailable)
            {
                d["reason"] = autoArmWhyNot;
                return d;
            }
            bool? tracker = TrackerPresent(out string trackerError);
            d["tracker"] = tracker;
            if (trackerError != null) d["tracker_probe"] = trackerError;
            d["mech_route"] = weaponUsableMech != null;
            if (weaponUsableMech == null && mechWhyNot != null) d["mech_route_reason"] = mechWhyNot;
            return d;
        }
    }
}
