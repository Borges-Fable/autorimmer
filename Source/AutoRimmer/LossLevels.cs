using System;
using System.Collections.Generic;
using Verse;

namespace AutoRimmer
{
    // ===================== LOSSES ARE LEVELS, NOT EVENTS ====================
    // spec 975973e, absorbing the levels half of f1a1700. COCKPIT.md
    // §"How it stays honest", item 3.
    //
    // A destroyed turret stays on the guns gauge as "was 2". The alternative —
    // an expiry — was rejected for the alert-mute ruling's reason inverted: a
    // loss should not quietly stop being shown. In run openrun-20260902 a
    // manhunter pack destroyed two turrets and an autocannon, the agent
    // rebuilt the two it had happened to count, and the third was never
    // mentioned again by anything. `827c1bf`'s `destroyed` row makes the
    // MOMENT visible; this file makes the LEVEL visible, which is the half
    // that survives the agent not reading that turn's journal.
    //
    // WHERE THE STATE LIVES, AND WHY IT IS NOT THE JOURNAL RING.
    // `Journal`'s in-memory ring holds `(seq, type)` and nothing else, and it
    // is 4096 entries long — so deriving the level from the journal would lose
    // a loss the moment 4096 rows went past it, which is "quietly stopping to
    // show it" wearing a different hat. This is its own store, written on the
    // destroy edge by `JournalHooks.EmitDestroyed` and never on a read.
    //
    // IN MEMORY AND CLEARED AT A GAME BOUNDARY, like the sampler ring, the
    // clock spans and the screen mark (`Runtime.ResetForGameBoundary`).
    // Nothing here is scribed: `d16a463` is an open hazard on observers owning
    // scribed state and `f1a1700` comment #2 refused to buy a durable baseline
    // with it. The consequence is stated in the data rather than hidden —
    // `basis` says the level counts losses SINCE THIS SESSION BEGAN.
    //
    // ================= WHAT "was N" MEANS, EXACTLY =========================
    // `was = count + lost`, where `count` is the full current count of that
    // def and `lost` is the number destroyed this session and not yet written
    // off. It is NOT "the count at some earlier moment", and the field docs
    // say so, because the two diverge the instant something is rebuilt:
    // rebuild one of two lost turrets and `count` rises while `lost` holds, so
    // the gauge reads `2 … was 3`. That is deliberate and it is the safe
    // direction — the loss is still on the screen, and `write-off {id, reason}`
    // is what takes it off.
    //
    // **`write-off` IS THE SECOND HALF OF 975973e AND IS NOT BUILT HERE.**
    // `Release` below is the seam it will call; nothing calls it today, so a
    // level simply persists for the life of the game. Rebuild-detection was
    // considered as a substitute and REFUSED: "a thing of this def appeared,
    // so the loss must be repaired" is a guess (a different turret, in a
    // different place, built for a different reason), and a guess that ERASES
    // a loss is the one direction this design refuses to guess in.
    public static class LossLevels
    {
        // A destroyed player building or frame, as the destroy hook saw it.
        public sealed class Loss
        {
            public string Def;      // what was LOST (a frame publishes what it was building)
            public string Label;    // the def's own label, for a line a person can read
            public string Kind;     // "building" | "frame"
            public int ThingId;     // thingIDNumber, the handle `write-off` will take
            public int X;
            public int Z;
            public int Tick;
            public string Mode;     // the game's own Verse/DestroyMode, never a story
        }

        // A cap, because the list is per-session and a colony can be levelled.
        // Ordered by importance (newest first) at read time, never at write
        // time — the write is on the destroy path and must stay O(1).
        private const int Cap = 400;

        private static readonly object gate = new object();
        private static readonly List<Loss> losses = new List<Loss>();
        // Per-def outstanding counts, kept beside the list so `WasFor` is a
        // dictionary hit rather than a walk: the guns gauge asks it once per
        // def on every screen.
        private static readonly Dictionary<string, int> byDef =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private static int dropped;

        // Called from `JournalHooks.EmitDestroyed`, on the main thread, on the
        // destroy edge. NOT from a read — that is the whole discipline this
        // file exists under.
        //
        // The DENY LIST IS THE SAME ONE THE HALT USES, and it is here rather
        // than at the call site so the two can never drift: five of
        // `Verse/DestroyMode`'s nine values are the colony's own work arriving
        // as planned, and a wall the colony deconstructed on purpose is not a
        // loss to carry on a gauge forever. Written as a DENY list so a tenth
        // mode from a DLC or a mod counts as a loss by default.
        public static bool CountsAsLoss(string mode)
        {
            switch (mode)
            {
                case "Deconstruct":
                case "WillReplace":
                case "Cancel":
                case "Refund":
                case "FailConstruction":
                    return false;
                default:
                    return true;
            }
        }

        public static void Record(string def, string label, string kind, int thingId,
                                  IntVec3 at, int tick, string mode)
        {
            if (def == null) return;
            if (!CountsAsLoss(mode)) return;
            lock (gate)
            {
                if (losses.Count >= Cap) { dropped++; }
                else
                    losses.Add(new Loss
                    {
                        Def = def,
                        Label = label,
                        Kind = kind,
                        ThingId = thingId,
                        X = at.x,
                        Z = at.z,
                        Tick = tick,
                        Mode = mode,
                    });
                // The COUNT is kept whatever the list cap did, so `was N` never
                // shrinks because the list overflowed. A capped list that
                // silently shrinks its own total is the truncation defect 2.6
                // paid for twice.
                byDef[def] = byDef.TryGetValue(def, out var c) ? c + 1 : 1;
            }
        }

        // The seam `write-off {id, reason}` (975973e, second half) will call.
        // Nothing calls it today; it exists so the second half adds a verb and
        // not a data model.
        public static bool Release(int thingId)
        {
            lock (gate)
            {
                for (int i = 0; i < losses.Count; i++)
                {
                    if (losses[i].ThingId != thingId) continue;
                    string def = losses[i].Def;
                    losses.RemoveAt(i);
                    if (byDef.TryGetValue(def, out var c))
                    {
                        if (c <= 1) byDef.Remove(def);
                        else byDef[def] = c - 1;
                    }
                    return true;
                }
                return false;
            }
        }

        // Either thread — `Runtime.ResetForGameBoundary` is reached from the
        // poller's heartbeat edge as well as the GameComponent's virtuals.
        public static void Clear()
        {
            lock (gate)
            {
                losses.Clear();
                byDef.Clear();
                dropped = 0;
            }
        }

        // Outstanding losses of one def. 0 when there are none, which is the
        // normal case and is why the gauge omits `was` rather than printing
        // `was N` equal to the count.
        public static int LostFor(string def)
        {
            if (def == null) return 0;
            lock (gate) { return byDef.TryGetValue(def, out var c) ? c : 0; }
        }

        public static int Total { get { lock (gate) { return losses.Count + dropped; } } }

        // Newest first — the importance order for a loss list, because the
        // thing that just went is the thing a screen is about. Capped by the
        // caller, which publishes what it dropped.
        public static List<Loss> Snapshot()
        {
            lock (gate)
            {
                var copy = new List<Loss>(losses);
                copy.Sort((a, b) => b.Tick.CompareTo(a.Tick));
                return copy;
            }
        }

        public static int Dropped { get { lock (gate) { return dropped; } } }
    }
}
