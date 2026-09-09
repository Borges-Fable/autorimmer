using System;
using System.Collections.Generic;

namespace AutoRimmer
{
    // ================== "SINCE YOU LAST LOOKED", THE FIFTH PANEL ============
    // spec 975973e. COCKPIT.md §"The screen".
    //
    // The journal rows since the last screen THIS CLIENT received, read BY THE
    // MOD, in three rows — game, human, mod. The agent is not asked to fetch
    // them: the whole finding behind the screen (themes.md T0) is that a field
    // the agent has to remember to go and get is a field nobody reads, and run
    // openrun-20260902 made 27 advances and zero journal calls in its worst
    // session.
    //
    // ===================== WHY A LIVE TAP AND NOT A READ ===================
    // There is no in-mod API that hands back journal ROWS by seq range.
    // `Journal`'s in-memory ring is `(seq, type)` and nothing else — it exists
    // for `digest.changed`'s counts — and the only full-row reader in the tree
    // is `JournalVerbs.Read`, which re-parses the ndjson FILE and is declared
    // `MainThread = false` precisely because it does file I/O. A screen is
    // built on the main thread inside `advance`'s teardown, so re-reading the
    // file there is not available. `Journal.OnRow` (added by this spec) is the
    // tap, and it carries the row's own `by` and `cmd` rather than letting
    // this file re-derive them.
    //
    // ========================= THE THREE ROWS ==============================
    // `by` is 827c1bf's four-value provenance. Three of the four are printed
    // as rows because they are the three the agent did not do:
    //
    //   game   — inside a tick. A death, a downing, a letter, a building lost.
    //   human  — Dorian at the keyboard, which this run had for 1.4M ticks and
    //            which nothing in the surface ever showed.
    //   mod    — the mod's own chores and standing observers.
    //
    // `agent` is NOT a row: those are the agent's own commands, which it has
    // already seen the results of. It is COUNTED (`agent_rows`) rather than
    // dropped, because "nothing happened" and "twelve things you did happened"
    // must not read alike.
    //
    // ============== CAPPED BY IMPORTANCE, AND IT SAYS WHAT IT DROPPED ======
    // The digest's rule, applied without exception. The spec names the set
    // that is kept WHATEVER the cap: a death, a downing, a destroyed building,
    // an arrival and a letter.
    //
    // **AN ARRIVAL HAS NO EVENT TYPE OF ITS OWN, AND THIS FILE DOES NOT
    // INVENT ONE.** The journal's seventeen types (checked by grepping every
    // `Journal.Emit` call site) contain no `arrival`: a joiner, a wanderer, a
    // refugee and a drop-pod survivor all reach the journal as a `letter`, and
    // `letter` is in the always-kept set, so the arrival is kept — through the
    // row the game actually writes rather than through a hook this spec is not
    // scoped to add. Stated rather than quietly satisfied, because "arrivals
    // are always kept" and "letters are always kept" are the same sentence
    // only for as long as arrivals arrive by letter.
    internal static class ScreenLog
    {
        // One row, flattened at capture time. The payload dictionary is NOT
        // retained: it is summarised here and dropped, so a long advance holds
        // a few hundred short strings rather than a few hundred dictionaries.
        internal sealed class Row
        {
            public long Seq;
            public int Tick;
            public string By;
            public string Type;
            public string What;      // the one-line summary
            public bool Important;
        }

        // The window handed to one screen. Everything in it is already
        // decided: the caps applied at CAPTURE time and what they dropped.
        internal sealed class Window
        {
            public List<Row> Rows = new List<Row>();
            public long FromSeq;             // exclusive; 0 = "since the beginning"
            public long ToSeq;               // inclusive; 0 = nothing captured
            public long Cut;                 // the highest seq the PANEL printed
            public int Total;                // rows seen, INCLUDING the ones dropped
            public int Kept;                 // ordinary rows in Rows
            public int KeptImportant;        // important rows in Rows
            public int OrdinaryDropped;
            public int ImportantDropped;
            public Dictionary<string, int> DroppedByType = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<string, int> CountsByType = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<string, int> CountsBy = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        // How many rows one window will HOLD. Beyond these it counts rather
        // than keeps, and the counts are what `truncated` publishes.
        //
        // The important cap is generous and the ordinary cap is not, because
        // the two answer different questions: 120 deaths in one turn is a
        // colony ending and the screen should say all of it, while the 4,000th
        // `action` row of a three-day advance tells the reader nothing the
        // count does not.
        private const int ImportantCap = 120;
        private const int OrdinaryCap = 200;

        // Rows shown PER `by` bucket on the screen, on top of the window caps
        // above. Importance beats recency for the cut and recency orders what
        // is left; important rows are exempt, which is the spec's own rule.
        internal const int ShowPerBucket = 8;

        private static readonly object gate = new object();
        private static Window live = new Window();
        private static Window delivered;
        private static bool hooked;

        // Called once, from the mod ctor, beside TimeDriver.HookJournal.
        internal static void Hook()
        {
            if (hooked) return;
            hooked = true;
            Journal.OnRow += Capture;
        }

        // ANY THREAD — `Journal.Emit` is called from the poller as well as the
        // main thread, and `OnRow` fires on whichever emitted. Everything below
        // is under the lock and touches no Verse.
        private static void Capture(long seq, int tick, string by, string cmd,
                                    string type, Dictionary<string, object> payload)
        {
            string what;
            try { what = Summarise(type, payload); }
            catch { what = null; }
            bool important = IsImportant(type);
            lock (gate)
            {
                var w = live;
                if (w.FromSeq == 0 && w.Total == 0) w.FromSeq = seq - 1;
                w.ToSeq = seq;
                w.Total++;
                Bump(w.CountsByType, type);
                Bump(w.CountsBy, by ?? "?");
                // RUNNING COUNTERS, not a walk of the list. This method is on
                // the journal's own emit path and a three-day advance writes
                // thousands of rows; counting the kept ones per row made the
                // window quadratic in the length of the advance.
                if (important ? w.KeptImportant >= ImportantCap : w.Kept >= OrdinaryCap)
                {
                    if (important) w.ImportantDropped++;
                    else w.OrdinaryDropped++;
                    Bump(w.DroppedByType, type);
                    return;
                }
                if (important) w.KeptImportant++; else w.Kept++;
                w.Rows.Add(new Row
                {
                    Seq = seq,
                    Tick = tick,
                    By = by ?? "?",
                    Type = type,
                    What = what,
                    Important = important,
                });
            }
        }

        private static void Bump(Dictionary<string, int> d, string k)
        {
            if (k == null) return;
            d[k] = d.TryGetValue(k, out var c) ? c + 1 : 1;
        }

        // The always-kept set, from the spec: a death, a downing, a destroyed
        // building, an arrival and a letter. `red_error` and `mental_break`
        // ride with them — a red error is the invariant `advance` halts on and
        // a mental break is a colonist about to do something to the colony,
        // and neither is a thing a cap may quietly eat.
        //
        // A `destroyed` row for a CORPSE is not filtered here: the corpse hook
        // is deliberately not faction-filtered (827c1bf), a rotting body is a
        // hauling backlog the colony is measured by, and the payload's `kind`
        // is in the summary so the reader can tell one from a wall.
        internal static bool IsImportant(string type)
        {
            switch (type)
            {
                case "death":
                case "downed":
                case "destroyed":
                case "letter":
                case "red_error":
                case "mental_break":
                    return true;
                default:
                    return false;
            }
        }

        // A row in a handful of words. Deliberately short — this is the panel
        // whose whole reason for existing is that the long form went unread.
        private static string Summarise(string type, Dictionary<string, object> p)
        {
            if (p == null) return null;
            switch (type)
            {
                case "death":
                case "downed":
                    return Join(Str(p, "name"), Str(p, "kind"), At(p));
                case "destroyed":
                    return Join(Str(p, "label") ?? Str(p, "builds") ?? Str(p, "def"),
                                Str(p, "kind"), At(p), Str(p, "mode"));
                case "letter":
                    return Join(Str(p, "label"), Str(p, "def"), Str(p, "choice") != null ? "choice" : null);
                case "message":
                    return Clip(Str(p, "text"), 90);
                case "alert_on":
                case "alert_off":
                    return Str(p, "id");
                case "mental_break":
                    return Join(Str(p, "name"), Str(p, "state") ?? Str(p, "break"));
                case "action":
                    return Join(Str(p, "verb"), Str(p, "step"), Str(p, "target"));
                case "construction":
                    return Join(Str(p, "kind"), Str(p, "def") ?? Str(p, "builds"), At(p));
                case "clock":
                    return Join(Str(p, "drove"), Num(p, "ticks") + "t", Str(p, "speed"),
                                Str(p, "closed_by"));
                case "dialog":
                    return Join(Str(p, "type"), Str(p, "title"));
                case "dialog_answered":
                    return Join(Str(p, "via"), Num(p, "accepted") >= 0 ? null : null);
                case "session":
                    return Join(Str(p, "kind"), Str(p, "file"));
                case "dev":
                    return Join(Str(p, "step"), Str(p, "def"), Str(p, "target"));
                case "red_error":
                case "warning":
                    return Clip(Str(p, "text"), 120);
                default:
                    return null;
            }
        }

        private static string Str(Dictionary<string, object> p, string k)
            => p != null && p.TryGetValue(k, out var v) && v is string s && s.Length > 0 ? s : null;

        private static double Num(Dictionary<string, object> p, string k)
        {
            if (p == null || !p.TryGetValue(k, out var v)) return -1;
            if (v is double d) return d;
            if (v is int i) return i;
            if (v is long l) return l;
            return -1;
        }

        // The `at` field is the two-element list `Positions.Out` writes.
        private static string At(Dictionary<string, object> p)
        {
            if (p == null || !p.TryGetValue("at", out var v)) return null;
            if (!(v is List<object> l) || l.Count != 2) return null;
            try { return "(" + (int)Convert.ToDouble(l[0]) + "," + (int)Convert.ToDouble(l[1]) + ")"; }
            catch { return null; }
        }

        private static string Clip(string s, int max)
            => s == null ? null : (s.Length <= max ? s : s.Substring(0, max) + "…");

        private static string Join(params string[] parts)
        {
            string outp = null;
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                outp = outp == null ? parts[i] : outp + " " + parts[i];
            }
            return outp;
        }

        // ------------------------- delivery ---------------------------------
        //
        // THE SAME TWO-STEP THE CLOCK WINDOW USES. `Peek` hands back the live
        // window without consuming it, so a screen that throws while it is
        // being built loses nothing; `Commit` swaps in a fresh window and is
        // called only once the screen is real. `Deliver` is the advance's
        // route: `TimeDriver.Teardown` consumes the clock window before
        // `BuildData` runs, so this one is consumed at the same moment for the
        // same reason — the two halves of one screen must not be cut at two
        // different seqs.
        internal static Window Peek()
        {
            lock (gate) { return live; }
        }

        internal static void Commit(Window shown)
        {
            lock (gate)
            {
                if (shown == null || !ReferenceEquals(shown, live)) return;
                var next = new Window();
                // ROWS ABOVE THE CUT SURVIVE. `Panel` stamps `Cut` with the
                // highest seq it actually printed, and the build between
                // `Peek` and here CAN journal — a degraded section emits a
                // warning, and every one of those is a row. Dropping them
                // would lose journal rows that no screen ever showed, which is
                // the one failure this panel exists to prevent.
                for (int i = 0; i < shown.Rows.Count; i++)
                {
                    var r = shown.Rows[i];
                    if (r.Seq <= shown.Cut) continue;
                    if (next.FromSeq == 0 && next.Total == 0) next.FromSeq = r.Seq - 1;
                    next.ToSeq = r.Seq;
                    next.Total++;
                    Bump(next.CountsByType, r.Type);
                    Bump(next.CountsBy, r.By);
                    if (r.Important) next.KeptImportant++; else next.Kept++;
                    next.Rows.Add(r);
                }
                live = next;
            }
        }

        internal static void Deliver()
        {
            lock (gate)
            {
                delivered = live;
                live = new Window();
            }
        }

        internal static Window Delivered
        {
            get { lock (gate) { return delivered; } }
        }

        // Either thread, from `Runtime.ResetForGameBoundary`. Everything here
        // is indexed by a journal whose colony is gone, and a `tick` from it
        // can be ahead of the new game's clock.
        internal static void Clear()
        {
            lock (gate)
            {
                live = new Window();
                delivered = null;
            }
        }

        // ------------------------- the panel --------------------------------
        //
        // `ticksBlock` is TimeDriver's own `since_last_look` — the tick side of
        // the same question, already built and already named. It rides inside
        // this panel rather than beside it because "what happened" and "how
        // long it took" are one answer.
        internal static Dictionary<string, object> Panel(Window w, Dictionary<string, object> ticksBlock,
                                                         bool everDelivered)
        {
            var d = new Dictionary<string, object>();
            // The spec's own words for the first screen after a load. The mark
            // is cleared at a game boundary with the sampler ring, so this is
            // "no earlier screen for THIS game", which is the honest reading.
            if (!everDelivered)
                d["no_earlier_screen"] = "no earlier screen — first screen since the game loaded, so "
                    + "the rows below are everything recorded so far, not a delta.";
            if (w == null) w = new Window();
            // ONE LOCK OVER THE WHOLE READ, and it is a correctness
            // requirement rather than tidiness: `Capture` runs on whichever
            // thread emitted the row — the poller writes `session` and `clock`
            // rows — so a bucket walking `w.Rows` unlocked would throw
            // "collection was modified" and take the screen with it, and the
            // three counts dictionaries have the same exposure. Cheap: nothing
            // inside blocks, and the only contender is one dictionary bump per
            // journal row.
            lock (gate)
            {
                // The high-water mark this panel is about to print. `Commit`
                // reads it so a row journaled BETWEEN this call and the commit
                // — a degraded section's warning — is carried into the next
                // window rather than dropped.
                w.Cut = w.ToSeq;
                d["from_seq"] = (double)w.FromSeq;
                d["to_seq"] = (double)w.ToSeq;
                d["rows_total"] = (double)w.Total;
                if (ticksBlock != null) d["clock"] = ticksBlock;

                d["game"] = Bucket(w, "game");
                d["human"] = Bucket(w, "human");
                d["mod"] = Bucket(w, "mod");
                // Counted, never shown: see the header. The agent has already
                // been handed the result of each of these.
                d["agent_rows"] = (double)(w.CountsBy.TryGetValue("agent", out var a) ? a : 0);
                d["counts"] = Counts(w.CountsByType);

                d["truncated"] = new Dictionary<string, object>
                {
                    ["dropped"] = (double)(w.OrdinaryDropped + w.ImportantDropped),
                    ["ordinary_dropped"] = (double)w.OrdinaryDropped,
                    // Non-zero here is the honest admission that even the
                    // always-keep set overflowed its window. It takes 120
                    // deaths, downings, losses and letters in one turn.
                    ["important_dropped"] = (double)w.ImportantDropped,
                    ["by_type"] = Counts(w.DroppedByType),
                    ["read_the_rest"] = "journal {since_seq: " + w.FromSeq + "}",
                };
            }
            d["always_kept"] = new List<object> { "death", "downed", "destroyed", "letter",
                                                  "red_error", "mental_break" };
            d["note"] = "rows since the last screen you were handed, read by the mod. `by`: game = "
                + "inside a tick, human = at the keyboard, mod = the mod's own chores. `agent` rows "
                + "are counted, not listed. An arrival has no event type and arrives as a `letter`.";
            return d;
        }

        private static Dictionary<string, object> Counts(Dictionary<string, int> src)
        {
            var d = new Dictionary<string, object>();
            if (src == null) return d;
            var keys = new List<string>(src.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++) d[keys[i]] = (double)src[keys[i]];
            return d;
        }

        // Importance decides the CUT, sequence decides the ORDER of what is
        // left. Cutting in arrival order is the 2.6 defect this whole file's
        // caps were written against; presenting out of order is just hard to
        // read, and a chronology that is not in order is not a chronology.
        private static Dictionary<string, object> Bucket(Window w, string by)
        {
            var mine = new List<Row>();
            for (int i = 0; i < w.Rows.Count; i++)
                if (w.Rows[i].By == by) mine.Add(w.Rows[i]);

            var keep = new List<Row>();
            int ordinaryRoom = ShowPerBucket;
            // Walk newest-first so the ordinary rows that survive the cut are
            // the recent ones.
            for (int i = mine.Count - 1; i >= 0; i--)
            {
                var r = mine[i];
                if (r.Important) { keep.Add(r); continue; }
                if (ordinaryRoom <= 0) continue;
                ordinaryRoom--;
                keep.Add(r);
            }
            keep.Sort((x, y) => x.Seq.CompareTo(y.Seq));

            var rows = new List<object>();
            for (int i = 0; i < keep.Count; i++)
            {
                var r = keep[i];
                var line = new Dictionary<string, object>
                {
                    ["seq"] = (double)r.Seq,
                    ["tick"] = r.Tick,
                    ["type"] = r.Type,
                };
                if (r.What != null) line["what"] = r.What;
                rows.Add(line);
            }
            return new Dictionary<string, object>
            {
                ["rows"] = rows,
                ["total"] = (double)mine.Count,
                ["more"] = (double)Math.Max(0, mine.Count - keep.Count),
                ["order"] = "important always kept; newest-first cut; printed in seq order",
            };
        }
    }
}
