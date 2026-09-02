using System.Collections.Generic;
using System.Text;

namespace AutoRimmer
{
    // A REPEATED IDENTICAL OUTCOME IS ONE EVENT — git-bug f08dfc4.
    //
    // Run m1-20260901 sent `build {def:"DeepDrill", at:"122,130",
    // stuff:"Steel"}` 238 times and was told `'DeepDrill' is not made from
    // stuff` 238 times. The refusal was correct every time; DeepDrill genuinely
    // is not made from stuff and the message says so plainly. The defect is
    // that an unattended agent looped on a deterministic refusal for 238 turns
    // and NOTHING EVER SAID SO. The repetition existed only in a transcript
    // nobody read until the run was over.
    //
    // The same run carries a second, worse one, found while measuring this and
    // not in the issue: sixty consecutive `advance {until:{letter:true},
    // timeout_ticks:60000}` calls, every one refused `unread-journal` with a
    // byte-identical detail, from `m1-20260901-s02/695-advance` to
    // `m1-20260901-s03/047-advance`. TicksGame was 3,704,384 at the first and
    // 3,704,384 at the last: five minutes of wall clock, sixty turns, ZERO
    // in-game ticks.
    //
    // The mod was right on all sixty. The loop DID call `journal` between every
    // pair — but as `journal {since_seq:0, limit:2000}`, which exhausts its
    // limit nine rows short of the tail, and a truncated read only moves the
    // watermark as far as the rows it handed over. Every reply published
    // `unread_after: 9` and the loop advanced anyway. So the envelope carried
    // the answer twice a turn and nobody read it sixty times, which is the
    // argument for this counter in one sentence: a true field the caller does
    // not notice is not a signal, and a count of consecutive IDENTICAL
    // refusals is the one thing that cannot be mistaken for noise.
    //
    // So the mod publishes the count. It does NOT refuse, does NOT rate-limit
    // and has no opinion about how long a driver may loop (f08dfc4 is
    // deliberately narrower than a rate limiter): it makes the fact visible,
    // on the envelope, in the `ignored_args` idiom — a top-level field, absent
    // on the overwhelmingly common call, that the caller cannot miss.
    //
    // ---------------------------------------------------------------------
    // THE KEY IS THE OP; the (code, args) pair lives in the entry.
    //
    // f08dfc4 asks for a count per `(op, error code, normalised args)`. That is
    // what the STREAK is. The TABLE is keyed by op alone, and the entry holds
    // the code and the argument fingerprint that the streak is running on, so
    // that a refusal which differs from the one in flight — different code,
    // different arguments — RESETS rather than opening a second row. Three
    // things fall out of that and each is one of the issue's reset rules:
    //
    //   * the call SUCCEEDS            -> the op's entry is dropped
    //   * the ARGUMENTS change         -> fingerprint mismatch, count back to 1
    //   * the answer changes           -> code mismatch, count back to 1
    //
    // It also bounds the table at the number of ops that have ever failed
    // (~120 upper bound, single digits in practice), with no eviction policy to
    // get wrong.
    //
    // The cost is that a caller ALTERNATING two doomed calls to the same verb
    // never builds a streak on either. That under-reports and never
    // over-reports, which is the right direction for a field whose whole value
    // is that it is trustworthy when it appears. Measured on the real case: the
    // DeepDrill run reaches 94 rather than 238 under this keying, because other
    // `build` calls interrupted it. 94 is not a number anybody reads as normal.
    //
    // ---------------------------------------------------------------------
    // WHAT IS NOT MODELLED, deliberately: "the game state the refusal depends
    // on changed" (f08dfc4 §3) is served by the three resets above and by
    // Clear() at a game boundary, and NOT by a per-code notion of which state
    // each code watches. Such a thing would be a table beside the codes, which
    // is exactly what e440676's sibling design refused an hour earlier; and its
    // failure mode is silencing the field on the wedge it exists to name — a
    // state term that ticks would reset the counter every call. The honest
    // signal is already in the envelope: `repeated.ticks` is how far the colony
    // clock moved since the streak began, and a large count beside a `ticks` of
    // 0 is the strongest statement this surface can make.
    //
    // THREADING: Note() is called only from Poller.BuildResultJson, which runs
    // on the poller thread (and once on the main thread from Poller.Init,
    // before that thread starts). Clear() is reachable from EITHER thread —
    // Runtime.ResetForGameBoundary has two detectors by design — so the map is
    // guarded. One uncontended lock per result file is not a cost worth
    // measuring. Touches no Verse: the tick comes from the published snapshot.
    public static class RefusalStreak
    {
        // THE THRESHOLD, in the terms a driver loops in.
        //
        // The unit is CALLS OF THE SAME VERB, and on this bench one such call
        // is one TURN, because the play loop's turn is a fixed script — read,
        // act, journal, advance — that issues each verb at most once. Measured
        // on m1-20260901: the 238 DeepDrill refusals were never adjacent in the
        // step stream, not once; every one sat inside its own turn between a
        // `things` and a `journal`. So 3 here means "three turns", and a turn
        // on that run was a mean 12,525 ticks of advance — call it five in-game
        // hours, so a streak of 3 covers roughly fifteen in-game hours during
        // which nothing about the refusal changed.
        //
        // Why not 2: a second identical call is an ordinary retry, and `busy`
        // in particular is MEANT to be retried — it is `flow`, "ask again"
        // (git-bug e440676). Charging a wedge at 2 would fire on correct
        // behaviour. Why not 10: the run's own numbers say the threshold does
        // not need to be brave. The wedges that actually happened reached 94
        // and 60; anything from 3 to 20 catches both, and the smaller number
        // catches a NEW one sooner. Three is the first count that cannot be a
        // retry-after-a-transient: asked, refused, tried once more, and asked a
        // third time with byte-identical arguments is a caller that has stopped
        // reading the answer.
        public const int Threshold = 3;

        private sealed class Entry
        {
            public string Code;
            public string Fingerprint;
            public int Count;
            public int FirstTick;
        }

        private static readonly object gate = new object();
        private static readonly Dictionary<string, Entry> streaks =
            new Dictionary<string, Entry>();

        // A boundary invalidates every streak at once: the colony the refusal
        // was about no longer exists, and `FirstTick` was measured on a clock
        // that can now run BACKWARD (a load moves TicksGame). Same reason
        // Placements, Layouts and ColonySampler clear here — see
        // Runtime.ResetForGameBoundary.
        public static void Clear()
        {
            lock (gate) streaks.Clear();
        }

        // Called for EVERY result, success or failure, exactly once, as the
        // envelope is built. Returns the `repeated` block when the streak has
        // reached the threshold, and null — the overwhelmingly common case —
        // otherwise.
        public static Dictionary<string, object> Note(Result r)
        {
            if (r == null || string.IsNullOrEmpty(r.Op)) return null;
            int tick = Runtime.GameState.tick;

            lock (gate)
            {
                if (r.Ok)
                {
                    streaks.Remove(r.Op);
                    return null;
                }

                string code = r.ErrorCode.Code ?? "?";
                string fp = Fingerprint(r.Args);
                if (!streaks.TryGetValue(r.Op, out var e)
                    || e.Code != code || e.Fingerprint != fp)
                {
                    streaks[r.Op] = new Entry
                    {
                        Code = code,
                        Fingerprint = fp,
                        Count = 1,
                        FirstTick = tick,
                    };
                    return null;
                }

                e.Count++;
                if (e.Count < Threshold) return null;
                return Report(r.Op, e, tick);
            }
        }

        // The sentence is written for the agent that is currently making the
        // mistake, so it says what happened, how long it has been happening in
        // both units, and what the two ways out are. `ignored_args`' report is
        // the model: a machine-readable body and one paragraph a reader can act
        // on without opening anything else.
        private static Dictionary<string, object> Report(string op, Entry e, int tick)
        {
            int ticks = tick - e.FirstTick;
            var sb = new StringBuilder(320);
            sb.Append("this is time ").Append(e.Count).Append(" IN A ROW that `").Append(op)
              .Append("` has been refused `").Append(e.Code)
              .Append("` with byte-identical arguments");
            if (ticks > 0)
                sb.Append(", over ").Append(ticks).Append(" ticks");
            else
                sb.Append(", and THE COLONY CLOCK HAS NOT MOVED since the first one");
            sb.Append(". Nothing about the refusal has changed and repeating it will not "
                    + "change it: the answer is deterministic in the arguments you are "
                    + "sending. Change the arguments, or treat this as a wedge and "
                    + "escalate — see checklists/turn.md `repeated-refusal`. This counter "
                    + "resets on a success of this verb, on any change to the arguments, "
                    + "and on a different error code.");
            return new Dictionary<string, object>
            {
                ["count"] = (double)e.Count,
                ["op"] = op,
                ["code"] = e.Code,
                ["since_tick"] = (double)e.FirstTick,
                ["ticks"] = (double)ticks,
                ["threshold"] = (double)Threshold,
                ["detail"] = sb.ToString(),
            };
        }

        // "Normalised args" (f08dfc4 §1): a canonical rendering with object
        // keys in ordinal order, so `{a:1,b:2}` and `{b:2,a:1}` are one call.
        // Deliberately NOT MiniJson.Write — that preserves insertion order,
        // which is the order the caller's JSON happened to arrive in.
        //
        // The DETAIL string is deliberately not part of the key. `busy`'s
        // detail carries the in-flight advance's id and its ticks-done, so it
        // differs on every single call; keying on it would mean the counter
        // could never fire on `busy`, which is one of the two codes most likely
        // to be looped on (200 of run m1-20260901's 691 failures).
        private static string Fingerprint(Dictionary<string, object> args)
        {
            if (args == null || args.Count == 0) return "{}";
            var sb = new StringBuilder(128);
            WriteCanonical(sb, args, 0);
            return sb.ToString();
        }

        private const int MaxDepth = 16;

        private static void WriteCanonical(StringBuilder sb, object v, int depth)
        {
            // A fingerprint may never throw and may never recurse forever: it
            // runs inside the one path that owes every command a result file
            // (Poller.WriteResult's guard is the second line of defence, not
            // the first — MiniJson learned this in git-bug 4b65a28).
            if (depth > MaxDepth) { sb.Append("<deep>"); return; }
            switch (v)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: sb.Append('"').Append(s.Replace("\"", "\\\"")).Append('"'); break;
                case double d: sb.Append(MiniJson.N(d)); break;
                case Dictionary<string, object> obj:
                    var keys = new List<string>(obj.Keys);
                    keys.Sort(System.StringComparer.Ordinal);
                    sb.Append('{');
                    for (int i = 0; i < keys.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(keys[i]).Append("\":");
                        WriteCanonical(sb, obj[keys[i]], depth + 1);
                    }
                    sb.Append('}');
                    break;
                case List<object> list:
                    sb.Append('[');
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteCanonical(sb, list[i], depth + 1);
                    }
                    sb.Append(']');
                    break;
                default:
                    // Nothing else can come out of MiniJson.Parse, but a
                    // ToString() fallback keeps this total rather than
                    // throwing inside the result writer.
                    try { sb.Append('"').Append(v.ToString()).Append('"'); }
                    catch { sb.Append("\"<?>\""); }
                    break;
            }
        }
    }
}
